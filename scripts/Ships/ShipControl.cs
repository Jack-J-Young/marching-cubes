#nullable enable
using System;
using System.Collections.Generic;
using Godot;

namespace Ships;

/// <summary>
/// Flying a ship by its own hardware, shared by the player's pilot and the AI: thruster throttles (each thruster
/// pushes away from its nozzle with a fixed force, at its own position) and the gyroscope (torque up to a cap per
/// block). Directions passed in ship space are in the ship's local axes (-Z forward, +Y up).
/// </summary>
public static class ShipControl
{
  /// <summary>Applies these throttles (same order as ship.Thrusters) as forces at the thrusters.</summary>
  public static void ApplyThrottles(Ship ship, float[] throttles, float force)
  {
    var basis = ship.GlobalBasis.Orthonormalized();
    var thrusters = ship.Thrusters;
    for (int i = 0; i < thrusters.Count && i < throttles.Length; i++)
    {
      if (throttles[i] <= 0) continue;
      var (cell, facing) = thrusters[i];
      var push = basis * (-(Vector3)facing) * (force * throttles[i]);
      var at = ship.CellTransform(cell).Origin;
      ship.ApplyForce(push, at - ship.GlobalPosition);
    }
  }

  /// <summary>Throttles for a wanted push direction (|wish| up to 1 = full), applied to the ship.</summary>
  public static void Thrust(Ship ship, Vector3 wishLocal, float force, float[] throttles)
  {
    ThrustAllocator.Throttles(ship.Thrusters, wishLocal, throttles);
    ApplyThrottles(ship, throttles, force);
  }

  /// <summary>Most push the thrusters can give along a ship-space direction (at the throttles the allocator picks).</summary>
  public static float Capacity(Ship ship, Vector3 dirLocal, float force)
  {
    var thrusters = ship.Thrusters;
    if (thrusters.Count == 0 || dirLocal.LengthSquared() < 1e-12f) return 0f;
    var dir = dirLocal.Normalized();
    var tmp = new float[thrusters.Count];
    ThrustAllocator.Throttles(thrusters, dir, tmp);
    return ThrustAllocator.PushAlong(thrusters, tmp, dir, force);
  }

  /// <summary>
  /// Throttles that give (about) this acceleration (ship space): the thrusters lined up with it, scaled so their push
  /// along it is accel x mass, but never past full throttle. Returns the acceleration actually reached along it.
  /// </summary>
  public static float ThrottlesForAccel(Ship ship, Vector3 accelLocal, float force, float[] throttles)
  {
    var thrusters = ship.Thrusters;
    float want = accelLocal.Length();
    if (want < 1e-5f || thrusters.Count == 0)
    {
      Array.Clear(throttles);
      return 0f;
    }
    var dir = accelLocal / want;
    ThrustAllocator.Throttles(thrusters, dir, throttles);
    float capacity = ThrustAllocator.PushAlong(thrusters, throttles, dir, force);
    if (capacity <= 1e-6f)
    {
      Array.Clear(throttles);
      return 0f;
    }
    float scale = Math.Min(1f, want * ship.Mass / capacity);
    for (int i = 0; i < throttles.Length; i++) throttles[i] *= scale;
    return capacity * scale / ship.Mass;
  }

  /// <summary>
  /// Gyroscope: the torque that would reach this angular velocity (global) in one step, capped at torquePerBlock x
  /// blocks.
  /// </summary>
  public static void Gyro(Ship ship, Vector3 wantAngular, float torquePerBlock, float dt)
  {
    var state = PhysicsServer3D.BodyGetDirectState(ship.GetRid());
    if (state == null || dt <= 0) return;
    var inertia = state.InverseInertiaTensor.Inverse();
    var torque = inertia * ((wantAngular - ship.AngularVelocity) / dt);
    float max = torquePerBlock * ship.BlockCount;
    if (torque.Length() > max) torque = torque.Normalized() * max;
    ship.ApplyTorque(torque);
  }

  /// <summary>A rotation taking +Y to dir.</summary>
  public static Basis AlongY(Vector3 dir)
  {
    if (dir.Dot(Vector3.Up) < -0.9999f) return new Basis(Vector3.Right, Mathf.Pi);
    if (dir.Dot(Vector3.Up) > 0.9999f) return Basis.Identity;
    return new Basis(new Quaternion(Vector3.Up, dir.Normalized()));
  }

  /// <summary>
  /// A basis that turns a unit-height Y-axis cylinder into one of this length along dir. The stretch has to be along
  /// the mesh's own Y (applied before the rotation): Basis.Scaled scales world axes, which skews a tilted cylinder off
  /// its line.
  /// </summary>
  public static Basis Stretched(Vector3 dir, float length) => AlongY(dir) * Basis.FromScale(new Vector3(1, length, 1));
}

/// <summary>Flame cones on a ship's firing thrusters, sized by throttle (a pool of meshes under some node).</summary>
public sealed class ThrusterFlames
{
  private static Mesh? _mesh;
  private static StandardMaterial3D? _material;
  private readonly Node _parent;
  private readonly List<MeshInstance3D> _pool = [];

  public ThrusterFlames(Node parent) => _parent = parent;

  public int Visible { get; private set; }

  public void Update(Ship ship, float[] throttles)
  {
    _mesh ??= new CylinderMesh { TopRadius = 0f, BottomRadius = 0.32f, Height = 1f, RadialSegments = 10, Rings = 1 };
    _material ??= new StandardMaterial3D
    {
      ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
      AlbedoColor = new Color(1f, 0.6f, 0.15f),
    };
    var basis = ship.GlobalBasis.Orthonormalized();
    int used = 0;
    for (int i = 0; i < ship.Thrusters.Count && i < throttles.Length; i++)
    {
      if (throttles[i] <= 0.02f) continue;
      var (cell, facing) = ship.Thrusters[i];
      var dir = basis * (Vector3)facing;
      float len = 0.4f + 1.4f * throttles[i];
      var nozzle = ship.CellTransform(cell).Origin + dir * 0.5f;
      var flame = Pooled(used++);
      flame.GlobalTransform = new Transform3D(ShipControl.Stretched(dir, len), nozzle + dir * len * 0.5f);
    }
    Hide(used);
  }

  public void Hide(int keep = 0)
  {
    for (int i = keep; i < _pool.Count; i++) _pool[i].Visible = false;
    Visible = keep;
  }

  private MeshInstance3D Pooled(int index)
  {
    while (_pool.Count <= index)
    {
      var m = new MeshInstance3D
      {
        Mesh = _mesh,
        MaterialOverride = _material,
        TopLevel = true,
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
      };
      _parent.AddChild(m);
      _pool.Add(m);
    }
    _pool[index].Visible = true;
    return _pool[index];
  }
}

/// <summary>
/// A ship's mining lasers: while firing, each laser block casts a beam along its facing (a density raymarch, so it
/// hits rock at any distance) and digs where it hits, LaserTickRate times a second; the beams are drawn as red rods
/// (a pool of meshes under some node). Shared by the player's pilot and the AI.
/// </summary>
public sealed class LaserBank
{
  public float Range = 80f;
  public float DigRadius = 2.5f;
  /// <summary>How fast a beam erodes rock: the centre loses 1 - e^(-speed * t) of its density after t seconds.</summary>
  public float DigSpeed = 4f;
  /// <summary>Dig edits per second per beam (merged by ChunkManager when they come faster than it rebuilds).</summary>
  public float TickRate = 20f;

  /// <summary>
  /// Batched digging (AI ships): each beam adds up its hits on one spot and digs them as one edit once they amount to
  /// BatchStrength, after BatchSeconds, when the beam moves off the spot or stops. Far fewer edits for the terrain to
  /// store, rebuild and ray-march through (about 2 a second per beam instead of TickRate). Instant: those edits
  /// show up without the dig animation.
  /// </summary>
  public bool Batch;
  public bool Instant;
  public float BatchStrength = 0.95f;
  public float BatchSeconds = 1f;

  /// <summary>Beams of the last step: global start and end, and whether each hit terrain.</summary>
  public readonly List<(Vector3 From, Vector3 To, bool Hit)> Beams = [];
  public bool Firing { get; private set; }
  /// <summary>Beam hits that dug (one per beam per tick).</summary>
  public int Digs { get; private set; }
  /// <summary>Terrain edits made (fewer than Digs when batching).</summary>
  public int Edits { get; private set; }

  private struct Pending
  {
    public bool Active;
    public Vector3 Sum;
    public float Weight, Strength;
    public double Age;
    public Vector3 Centre => Sum / Weight;
  }
  private Pending[] _pending = [];
  private ChunkManager? _lastTerrain;

  private static Mesh? _mesh;
  private static StandardMaterial3D? _material;
  private readonly Node _parent;
  private readonly List<MeshInstance3D> _pool = [];
  private double _accum;

  public LaserBank(Node parent)
  {
    _parent = parent;
    Reset();
  }

  /// <summary>The next shot fires straight away.</summary>
  public void Reset() => _accum = 1.0 / TickRate;

  /// <summary>Fires (or not) for this physics step.</summary>
  public void Step(Ship ship, ChunkManager? terrain, bool fire, float dt)
  {
    Firing = fire && ship.Lasers.Count > 0 && terrain != null;
    if (terrain != null) _lastTerrain = terrain;
    if (!Firing)
    {
      Beams.Clear();
      FlushAll();
      Reset();
      return;
    }
    if (_pending.Length != ship.Lasers.Count)
    {
      FlushAll();
      _pending = new Pending[ship.Lasers.Count];
    }
    double interval = 1.0 / TickRate;
    _accum += dt;
    int ticks = 0;
    while (_accum >= interval && ticks < 3)
    {
      _accum -= interval;
      ticks++;
    }
    if (ticks == 0 && Beams.Count == ship.Lasers.Count)
    {
      // Between dig ticks the beams keep their last length (they're drawn from where the lasers are now); rays
      // are only cast when they dig, TickRate times a second, not every physics step.
      for (int i = 0; i < _pending.Length; i++)
      {
        ref var p = ref _pending[i];
        if (!p.Active) continue;
        p.Age += dt;
        if (p.Age >= BatchSeconds) Flush(ref p);
      }
      return;
    }
    Beams.Clear();
    float strength = 1f - Mathf.Exp(-DigSpeed * ticks / TickRate);
    var basis = ship.GlobalBasis.Orthonormalized();
    for (int i = 0; i < ship.Lasers.Count; i++)
    {
      var (cell, facing) = ship.Lasers[i];
      var dir = basis * (Vector3)facing;
      var from = ship.CellTransform(cell).Origin + dir * 0.55f;
      bool hit = terrain!.RaycastTerrain(from, from + dir * Range, out var point);
      var to = hit ? point : from + dir * Range;
      Beams.Add((from, to, hit));
      if (Batch)
      {
        ref var p = ref _pending[i];
        if (p.Active) p.Age += dt;
        if (p.Active && (!hit || p.Centre.DistanceTo(point) > DigRadius * 0.5f)) Flush(ref p);
        if (hit && ticks > 0)
        {
          if (!p.Active) p = new Pending { Active = true };
          p.Sum += point * strength;
          p.Weight += strength;
          p.Strength = 1f - (1f - p.Strength) * (1f - strength);
          Digs++;
        }
        if (p.Active && (p.Strength >= BatchStrength || p.Age >= BatchSeconds)) Flush(ref p);
      }
      else if (hit && ticks > 0)
      {
        DigNow(point, strength);
        Digs++;
      }
    }
  }

  private void DigNow(Vector3 point, float strength)
  {
    if (_lastTerrain == null) return;
    if (Instant) _lastTerrain.DigInstant(point, DigRadius, strength);
    else _lastTerrain.Dig(point, DigRadius, strength);
    Edits++;
  }

  private void Flush(ref Pending p)
  {
    if (p.Active && p.Weight > 0) DigNow(p.Centre, p.Strength);
    p = default;
  }

  private void FlushAll()
  {
    for (int i = 0; i < _pending.Length; i++) Flush(ref _pending[i]);
  }

  /// <summary>Draws the beams from each laser's current position along its current direction, as long as its ray.</summary>
  public void Draw(Ship ship)
  {
    _mesh ??= new CylinderMesh { TopRadius = 0.07f, BottomRadius = 0.07f, Height = 1f, RadialSegments = 8, Rings = 1 };
    _material ??= new StandardMaterial3D
    {
      ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
      AlbedoColor = new Color(1f, 0.15f, 0.1f),
    };
    var basis = ship.GlobalBasis.Orthonormalized();
    int used = 0;
    for (int i = 0; i < Beams.Count && i < ship.Lasers.Count; i++)
    {
      var (from0, to, _) = Beams[i];
      float len = from0.DistanceTo(to);
      if (len < 1e-3f) continue;
      var (cell, facing) = ship.Lasers[i];
      var dir = basis * (Vector3)facing;
      var from = ship.CellTransform(cell).Origin + dir * 0.55f;
      var beam = Pooled(used++);
      beam.GlobalTransform = new Transform3D(ShipControl.Stretched(dir, len), from + dir * (len * 0.5f));
    }
    Hide(used);
  }

  public void Hide(int keep = 0)
  {
    for (int i = keep; i < _pool.Count; i++) _pool[i].Visible = false;
  }

  private MeshInstance3D Pooled(int index)
  {
    while (_pool.Count <= index)
    {
      var m = new MeshInstance3D
      {
        Mesh = _mesh,
        MaterialOverride = _material,
        TopLevel = true,
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
      };
      _parent.AddChild(m);
      _pool.Add(m);
    }
    _pool[index].Visible = true;
    return _pool[index];
  }
}
