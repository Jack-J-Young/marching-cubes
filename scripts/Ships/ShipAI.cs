#nullable enable
using System;
using System.Collections.Generic;
using Godot;

namespace Ships;

/// <summary>What an AI-flown ship does.</summary>
public enum AIJob
{
  /// <summary>Follow the player (or the ship they fly) and hold station FollowDistance away.</summary>
  Follow,
  /// <summary>Find the nearest rock, park DigStandOff from it facing it, and mine it away with the lasers.</summary>
  Dig,
}

/// <summary>
/// Flies its parent ship by itself, using only the ship's own thrusters, gyro and lasers, like a pilot would.
///   Follow: heads for the target, as fast as it can still stop in (planning with BrakeSafety of its braking thrust),
///     stops FollowDistance from it (from the ship's centre of mass) and holds station (matching the target's
///     velocity) until the target is more than FollowDistance + ResumeSlack away.
///   Dig: finds the nearest rock (a fan of density raymarches), flies to DigStandOff from it, turns its lasers onto
///     it and fires whenever the rock is in laser range in front of them. As the rock recedes it follows the surface
///     along the beam; when nothing is in range ahead it looks for the nearest rock again. A ship without lasers
///     follows instead.
/// Getting there: straight if the line is clear (a sweep of the ship's width), otherwise along a ShipPathfinder path
/// (coarse through known-empty space, fine near rock), replanned as it goes. Whisker rays along its motion make it
/// brake for rock whatever the plan says. While someone pilots the ship the AI does nothing (Yielded).
/// </summary>
public partial class ShipAI : Node3D
{
  public enum Mode { Following, Holding, Blocked, Yielded, Paused, Searching, Digging }

  [Export] public AIJob Job = AIJob.Follow;
  [Export] public float FollowDistance = 20f;
  [Export] public float ResumeSlack = 6f;
  [Export] public float MaxSpeed = 30f;
  /// <summary>Share of its braking thrust it plans to stop with (the rest is margin).</summary>
  [Export(PropertyHint.Range, "0.1,1,0.05")] public float BrakeSafety = 0.6f;
  /// <summary>How hard it corrects velocity errors (1/s): wanted acceleration = error x gain.</summary>
  [Export] public float VelocityGain = 2f;
  [Export] public float TurnGain = 3f;
  /// <summary>Fastest it turns (radians per second).</summary>
  [Export] public float MaxTurnRate = 1.5f;
  /// <summary>Replan at least this often while following a path (seconds).</summary>
  [Export] public float ReplanInterval = 2f;
  /// <summary>Line-of-sight and whisker checks per second.</summary>
  [Export] public float LineCheckRate = 5f;
  /// <summary>Speed it may still have when it reaches a path corner.</summary>
  [Export] public float CornerSpeed = 4f;
  /// <summary>Same as ShipPilot: push per thruster and gyro torque per block.</summary>
  [Export] public float ThrusterForce = 25f;
  [Export] public float GyroTorquePerBlock = 15f;
  [ExportGroup("Dig")]
  /// <summary>How far it looks for rock to dig.</summary>
  [Export] public float DigSearchRange = 250f;
  /// <summary>How far from the rock surface its centre parks while digging.</summary>
  [Export] public float DigStandOff = 22f;
  /// <summary>Lasers fire when they point within this angle (degrees) of the rock.</summary>
  [Export] public float DigAimTolerance = 8f;
  /// <summary>Seconds between searches for rock while it has none.</summary>
  [Export] public float SearchInterval = 1.5f;

  /// <summary>What it follows (default: the Player node next to the ShipBuilder).</summary>
  public Node3D? Target;
  public ChunkManager? Terrain;

  public Mode State { get; private set; } = Mode.Following;
  /// <summary>Current path (global), empty while flying straight.</summary>
  public IReadOnlyList<Vector3> Path => _path;
  /// <summary>Distance from the ship's centre to where it's going (the target, or the rock it digs).</summary>
  public float DistanceToTarget { get; private set; }
  public int PlanCount { get; private set; }
  public double LastPlanMs { get; private set; }
  /// <summary>Coarse-route length left after the current path (long trips).</summary>
  public float PathBeyond { get; private set; }
  /// <summary>Last physics step's throttles (same order as the ship's thrusters).</summary>
  public float[] Throttles => _throttles;
  /// <summary>Whisker rays have seen rock in the way (in the last check).</summary>
  public bool WhiskerBlocked => _whiskerHit != null;
  public bool LineClear => _lineClear;
  /// <summary>Rock it's digging (global surface point), if any; lasers and how much they've dug.</summary>
  public Vector3? DigPoint => _digPoint;
  public LaserBank Lasers => _lasers;

  private Ship _ship = null!;
  private readonly ShipPathfinder _planner = new();
  private readonly List<Vector3> _path = [];
  private int _wp;
  private ShipPathfinder.Request? _pathReq;
  private double _planAt = double.NegativeInfinity;
  private bool _lineClear, _replan = true;
  private double _checkAccum = double.MaxValue;
  private Vector3? _whiskerHit;
  private float[] _throttles = [];
  private ThrusterFlames _flames = null!;
  private LaserBank _lasers = null!;
  private Vector3 _lastTargetPos, _targetVel;
  private bool _hasLast;
  private int _clearanceBlocks = -1;
  private float _clearance;
  private Vector3? _digPoint;
  private double _nextSearch;

  public override void _Ready()
  {
    _ship = GetParent<Ship>();
    _flames = new ThrusterFlames(this);
    // Mining digs are batched and appear without the dig animation: far less work for the terrain per drone.
    _lasers = new LaserBank(this) { Batch = true, Instant = true };
    var root = _ship.Builder?.GetParent();
    Target ??= root?.GetNodeOrNull<Node3D>("Player");
    Terrain ??= root?.GetNodeOrNull<ChunkManager>("ChunkManager");
  }

  /// <summary>Radius of the ship around its centre of mass, plus a margin: the sphere the planner keeps clear.</summary>
  public float Clearance
  {
    get
    {
      if (_clearanceBlocks != _ship.BlockCount)
      {
        _clearanceBlocks = _ship.BlockCount;
        float r = 0;
        foreach (var c in _ship.Grid.Cells)
          r = Math.Max(r, ((Vector3)c + Vector3.One * 0.5f).DistanceTo(_ship.CenterOfMass));
        _clearance = r + 0.87f + 1.5f;
      }
      return _clearance;
    }
  }

  private float BrakeAccel => BrakeSafety * ShipControl.Capacity(_ship, Vector3.Back, ThrusterForce) / Math.Max(_ship.Mass, 1e-3f);

  /// <summary>Ship-space direction the lasers fire (their average facing), or forward without lasers.</summary>
  private Vector3 LaserAxis
  {
    get
    {
      var sum = Vector3.Zero;
      foreach (var (_, f) in _ship.Lasers) sum += (Vector3)f;
      return sum.LengthSquared() > 1e-6f ? sum.Normalized() : Vector3.Forward;
    }
  }

  private static double Now => Time.GetTicksMsec() / 1000.0;

  public override void _PhysicsProcess(double delta)
  {
    float dt = (float)delta;
    if (dt <= 0 || _ship.BlockCount == 0) return;
    if (Target != null && IsInstanceValid(Target)) TrackTarget(dt);
    if (_throttles.Length != _ship.Thrusters.Count) _throttles = new float[_ship.Thrusters.Count];
    Array.Clear(_throttles);
    if (_ship.Piloted)
    {
      State = Mode.Yielded;
      DropPath();
      _lasers.Step(_ship, null, false, dt);
      return;
    }
    if (_ship.Paused)
    {
      State = Mode.Paused;
      _lasers.Step(_ship, null, false, dt);
      return;
    }

    var basis = _ship.GlobalBasis.Orthonormalized();
    var com = _ship.GlobalTransform * _ship.CenterOfMass;
    var v = _ship.LinearVelocity;
    bool digging = Job == AIJob.Dig && _ship.Lasers.Count > 0;
    var aimAxis = digging ? LaserAxis : Vector3.Forward;

    // Where to go, how close to get, what to do there.
    Vector3 goal, holdVel;
    float stop;
    if (digging)
    {
      if (_digPoint == null && Now >= _nextSearch)
      {
        _digPoint = NearestRock(com);
        _nextSearch = Now + SearchInterval;
        if (_digPoint != null) DropPath();
      }
      if (_digPoint is not Vector3 rock)
      {
        // Nothing in reach: wait, still.
        State = Mode.Searching;
        DistanceToTarget = 0;
        _lasers.Step(_ship, Terrain, false, dt);
        Drive(basis, Vector3.Zero, v, aimAxis, basis * aimAxis, dt);
        return;
      }
      goal = rock;
      stop = DigStandOff;
      holdVel = Vector3.Zero;
    }
    else
    {
      if (Target == null || !IsInstanceValid(Target)) return;
      goal = Target.GlobalPosition;
      stop = FollowDistance;
      holdVel = _targetVel;
    }

    var toGoal = goal - com;
    float dist = toGoal.Length();
    DistanceToTarget = dist;

    if (dist <= stop) State = digging ? Mode.Digging : Mode.Holding;
    else if (State is not (Mode.Holding or Mode.Digging) || dist > stop + ResumeSlack)
      State = Mode.Following;

    _checkAccum += dt;
    if (_checkAccum >= 1.0 / LineCheckRate)
    {
      _checkAccum = 0;
      Checks(com, goal, stop, v, basis, aimAxis, digging);
      if (digging && _digPoint == null)
      {
        // The rock ahead is gone: look for the next on the next step.
        _lasers.Step(_ship, Terrain, false, dt);
        return;
      }
      if (digging) goal = _digPoint!.Value;
    }

    Vector3 wantVel;
    Vector3 face = toGoal;
    if (State is Mode.Holding or Mode.Digging)
    {
      DropPath();
      wantVel = holdVel;
      float tooClose = stop * 0.5f - dist;
      if (tooClose > 0 && dist > 1e-3f) wantVel -= toGoal / dist * Math.Min(tooClose * 0.5f, 3f);
    }
    else
    {
      float aBrake = Math.Max(BrakeAccel, 0.05f);
      Vector3? steer = null;
      float remaining = 0, toWaypoint = 0;
      bool corner = false;
      if (_lineClear)
      {
        DropPath();
        steer = goal;
        remaining = dist - stop;
        toWaypoint = dist;
      }
      else
      {
        UpdatePath(com, goal, stop);
        if (_path.Count > 0)
        {
          while (_wp < _path.Count - 1 && com.DistanceTo(_path[_wp]) < 6f) _wp++;
          steer = _path[_wp];
          toWaypoint = com.DistanceTo(_path[_wp]);
          remaining = toWaypoint + PathBeyond;
          for (int i = _wp; i < _path.Count - 1; i++) remaining += _path[i].DistanceTo(_path[i + 1]);
          corner = _wp < _path.Count - 1 || PathBeyond > 0;
        }
      }
      if (steer is Vector3 s && (s - com).LengthSquared() > 1e-4f)
      {
        var dir = (s - com).Normalized();
        float speed = Math.Min(MaxSpeed, MathF.Sqrt(2f * aBrake * Math.Max(0f, remaining)));
        if (corner) speed = Math.Min(speed, MathF.Sqrt(2f * aBrake * toWaypoint) + CornerSpeed);
        wantVel = dir * speed + (_lineClear ? holdVel : Vector3.Zero);
        // Digging: keep the lasers on the rock while closing in; following: point where it's going.
        if (!digging) face = dir;
      }
      else
      {
        State = Mode.Blocked;
        wantVel = Vector3.Zero;
      }
    }

    // Whiskers: never keep closing on rock seen ahead.
    if (_whiskerHit is Vector3 hit)
    {
      var n = (hit - com).Normalized();
      float closing = wantVel.Dot(n);
      if (closing > 0) wantVel -= n * closing;
    }

    // Lasers: whenever the rock is in range and they point at it.
    bool fire = false;
    if (digging && dist <= _lasers.Range)
    {
      var axis = (basis * aimAxis).Normalized();
      fire = axis.AngleTo(toGoal) <= Mathf.DegToRad(DigAimTolerance);
    }
    _lasers.Step(_ship, Terrain, fire, dt);
    Drive(basis, wantVel, v, aimAxis, face, dt);
  }

  /// <summary>Thrust toward a velocity and turn the given ship-space axis toward a direction.</summary>
  private void Drive(Basis basis, Vector3 wantVel, Vector3 v, Vector3 axisLocal, Vector3 face, float dt)
  {
    var accel = (wantVel - v) * VelocityGain;
    ShipControl.ThrottlesForAccel(_ship, basis.Inverse() * accel, ThrusterForce, _throttles);
    ShipControl.ApplyThrottles(_ship, _throttles, ThrusterForce);
    Turn(basis, axisLocal, face, dt);
  }

  public override void _Process(double delta)
  {
    if (_ship == null || _ship.Paused || _ship.Piloted)
    {
      _flames.Hide();
      _lasers.Hide();
      return;
    }
    _flames.Update(_ship, _throttles);
    _lasers.Draw(_ship);
  }

  /// <summary>Target velocity from its motion (a jump of more than 10 units in a step counts as a teleport).</summary>
  private void TrackTarget(float dt)
  {
    var p = Target!.GlobalPosition;
    if (_hasLast)
    {
      var step = p - _lastTargetPos;
      if (step.Length() > 10f) _targetVel = Vector3.Zero;
      else _targetVel = _targetVel.Lerp(step / dt, 1f - MathF.Exp(-8f * dt));
    }
    _lastTargetPos = p;
    _hasLast = true;
  }

  /// <summary>Gyro: turn a ship-space axis toward dir, without rolling about it.</summary>
  private void Turn(Basis basis, Vector3 axisLocal, Vector3 dir, float dt)
  {
    var want = Vector3.Zero;
    if (dir.LengthSquared() > 1e-6f)
    {
      var fwd = (basis * axisLocal).Normalized();
      var to = dir.Normalized();
      var axis = fwd.Cross(to);
      float angle = MathF.Atan2(axis.Length(), fwd.Dot(to));
      if (axis.LengthSquared() < 1e-8f) axis = angle > 1f ? fwd.Cross(basis.Y.Cross(fwd).LengthSquared() > 1e-4f ? basis.Y : basis.X) : Vector3.Zero;
      if (axis != Vector3.Zero) want = axis.Normalized() * Math.Min(angle * TurnGain, MaxTurnRate);
    }
    ShipControl.Gyro(_ship, want, GyroTorquePerBlock, dt);
  }

  /// <summary>
  /// Line of sight to the goal (up to where it would stop), whisker rays ahead, and while digging the rock along the
  /// lasers (density raymarches, so any distance works).
  /// </summary>
  private void Checks(Vector3 com, Vector3 goal, float stop, Vector3 v, Basis basis, Vector3 aimAxis, bool digging)
  {
    if (Terrain == null)
    {
      _lineClear = true;
      _whiskerHit = null;
      return;
    }
    float r = Clearance;
    var toGoal = goal - com;
    float reach = toGoal.Length() - stop * 0.5f;
    _lineClear = reach <= 0 || !Sweep(com, com + toGoal.Normalized() * reach, r, out _);

    _whiskerHit = null;
    float speed = v.Length();
    if (speed > 0.5f)
    {
      float stopDist = speed * speed / (2f * Math.Max(BrakeAccel, 0.05f));
      var dir = v / speed;
      if (Sweep(com, com + dir * (stopDist + r + 4f), r * 0.8f, out var hit))
      {
        _whiskerHit = hit;
        _replan = true;
      }
    }

    if (digging && State == Mode.Digging)
    {
      // Follow the rock surface as it's dug back, along the beam; nothing there any more: find the next rock.
      var axis = (basis * aimAxis).Normalized();
      if (axis.AngleTo(toGoal) <= Mathf.DegToRad(DigAimTolerance * 2f))
      {
        if (Terrain.RaycastTerrain(com, com + axis * (_lasers.Range + r), out var rock)) _digPoint = rock;
        else
        {
          _digPoint = null;
          _nextSearch = 0;
        }
      }
    }
  }

  /// <summary>Nearest rock within DigSearchRange: a fan of 64 rays from the ship's centre.</summary>
  private Vector3? NearestRock(Vector3 com)
  {
    if (Terrain == null) return null;
    Vector3? best = null;
    float bestD = float.MaxValue;
    const int n = 64;
    float golden = Mathf.Pi * (3f - MathF.Sqrt(5f));
    for (int i = 0; i < n; i++)
    {
      float y = 1f - 2f * (i + 0.5f) / n;
      float rad = MathF.Sqrt(1f - y * y);
      var d = new Vector3(MathF.Cos(golden * i) * rad, y, MathF.Sin(golden * i) * rad);
      if (Terrain.RaycastTerrain(com, com + d * Math.Min(DigSearchRange, bestD), out var hit))
      {
        float dist = com.DistanceTo(hit);
        if (dist < bestD)
        {
          bestD = dist;
          best = hit;
        }
      }
    }
    return best;
  }

  /// <summary>Rays from the centre and four points around it (radius r, across the line): first rock hit.</summary>
  private bool Sweep(Vector3 from, Vector3 to, float r, out Vector3 hit)
  {
    hit = default;
    var d = to - from;
    if (d.LengthSquared() < 1e-4f) return false;
    var dir = d.Normalized();
    var side = Math.Abs(dir.Dot(Vector3.Up)) > 0.9f ? Vector3.Right : Vector3.Up;
    var a = dir.Cross(side).Normalized();
    var b = dir.Cross(a);
    float best = float.MaxValue;
    bool any = false;
    foreach (var o in new[] { Vector3.Zero, a * r, -a * r, b * r, -b * r })
      if (Terrain!.RaycastTerrain(from + o, to + o, out var h))
      {
        float t = (h - from - o).Length();
        if (t < best)
        {
          best = t;
          hit = h;
          any = true;
        }
      }
    return any;
  }

  /// <summary>Takes finished plans and asks for a new one when the current one is missing or stale.</summary>
  private void UpdatePath(Vector3 com, Vector3 goal, float arrive)
  {
    var gen = Terrain?.Generator;
    if (gen == null) return;
    double now = Now;
    int stamp = Terrain!.EditCount + Terrain.EditsMerged;
    if (_planner.TakeResult() is { } result)
    {
      PlanCount++;
      LastPlanMs = result.Ms;
      _path.Clear();
      _wp = 0;
      _pathReq = result.Request;
      PathBeyond = result.Beyond;
      if (result.Points.Count > 1)
        for (int i = 1; i < result.Points.Count; i++) _path.Add(Terrain.ToGlobal(result.Points[i]));
    }
    var goalLocal = Terrain.ToLocal(goal);
    bool nearEnd = PathBeyond > 0 && _path.Count > 0 && com.DistanceTo(_path[^1]) < 40f;
    bool stale = _pathReq == null || _replan || nearEnd
      || _pathReq.Goal.DistanceTo(goalLocal) > 16f
      || _pathReq.EditStamp != stamp && Job == AIJob.Follow // a digging ship edits all the time
      || now - _planAt > ReplanInterval;
    if (stale && !_planner.Building && now - _planAt > 0.25)
    {
      _planAt = now;
      _replan = false;
      _planner.Start(gen, new ShipPathfinder.Request(Terrain.ToLocal(com), goalLocal, Clearance, arrive, stamp), Terrain.Uniform);
    }
  }

  private void DropPath()
  {
    _path.Clear();
    _wp = 0;
    _pathReq = null;
    PathBeyond = 0;
  }
}
