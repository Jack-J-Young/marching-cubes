#nullable enable
using System.Collections.Generic;
using Godot;

namespace Ships;

/// <summary>
/// One free-floating ship: a zero-gravity rigid body made of 1-unit blocks. Each block is a box collision shape;
/// the render mesh shows only the outer faces. Mass and centre of mass follow the blocks.
///
/// Terrain impacts (see ImpactModel): when the ship hits terrain, the part of its velocity straight into the surface
/// (measured just before the hit) digs a crater sized by the energy, and the ship keeps the speed that matches the
/// energy left over. The collision itself stops it (no bounce), so that leftover speed is held back and given back
/// once the terrain has given way (the crater's collision is in) or after CarryDelay; if it is still blocked, that
/// is a new, weaker impact. So a fast ship ploughs a tunnel that shrinks until its energy runs out.
/// </summary>
public partial class Ship : RigidBody3D
{
  private static readonly BoxShape3D BlockShape = new() { Size = Vector3.One };

  public readonly ShipGrid Grid = new();
  private readonly Dictionary<Vector3I, CollisionShape3D> _shapes = [];
  private MeshInstance3D _mesh = null!;
  /// <summary>The ship's current render mesh (shared, e.g. by the map's marker).</summary>
  public Mesh? RenderMesh => _mesh.Mesh;
  private float _blockMass = 1f;

  /// <summary>Owner (impact settings); set by ShipBuilder.</summary>
  public ShipBuilder? Builder;
  /// <summary>Craters to dig (global point, dig radius), collected during physics and dug by ShipBuilder.</summary>
  public readonly List<(Vector3 Point, float Radius)> PendingCraters = [];

  private Vector3 _lastLinear, _lastAngular;
  private Vector3 _carry;
  private double _carryAt, _lastImpactAt = double.NegativeInfinity;
  private Vector3 _impactNormal;
  private float _impactSpeed;

  public int BlockCount => Grid.Count;

  /// <summary>Hull block colour (null = the usual grey); applied on the next Rebuild.</summary>
  public Color? HullColor;

  /// <summary>The AI flying this ship when nobody pilots it (null for player ships).</summary>
  public ShipAI? AI { get; set; }

  /// <summary>
  /// Frozen with its velocity saved: either unloaded (see Unloaded) or held in place, still visible, for a moment
  /// while the terrain collision around it is built.
  /// </summary>
  public bool Paused { get; private set; }

  /// <summary>
  /// Out in space no loaded chunk covers: paused, hidden and out of the physics world (costs nothing, can't be hit
  /// or targeted) until terrain is loaded around it again. Never true while piloted.
  /// </summary>
  public bool Unloaded => Paused && _unloadWanted && !_piloted;

  /// <summary>Being flown: the pilot's ship never unloads (ShipBuilder keeps it simulating).</summary>
  public bool Piloted
  {
    get => _piloted;
    set
    {
      _piloted = value;
      ApplyLook();
    }
  }
  private bool _piloted, _unloadWanted;
  private Vector3 _savedLinear, _savedAngular;

  /// <summary>The ship's velocity, whether it is moving or paused (paused ships keep theirs for later).</summary>
  public Vector3 EffectiveLinearVelocity => Paused ? _savedLinear : LinearVelocity;
  public Vector3 EffectiveAngularVelocity => Paused ? _savedAngular : AngularVelocity;

  /// <summary>
  /// Pauses (remembers the velocity and freezes; with unload, also hides and leaves the physics world) or resumes
  /// (back in the world, visible, with exactly that velocity).
  /// </summary>
  public void SetSimulating(bool run, bool unload = true)
  {
    _unloadWanted = !run && unload;
    if (run == Paused)
    {
      if (!run)
      {
        _savedLinear = LinearVelocity;
        _savedAngular = AngularVelocity;
        Freeze = true;
        Paused = true;
      }
      else
      {
        Freeze = false;
        Paused = false;
        LinearVelocity = _savedLinear;
        AngularVelocity = _savedAngular;
      }
    }
    ApplyLook();
  }

  /// <summary>
  /// An unloaded ship is hidden and out of the physics world (disabled nodes leave the space with DisableMode =
  /// Remove: no collisions, no ray hits); running and held ships are shown and in the world.
  /// </summary>
  private void ApplyLook()
  {
    bool unloaded = Unloaded;
    if (Visible == unloaded) Visible = !unloaded;
    var mode = unloaded ? ProcessModeEnum.Disabled : ProcessModeEnum.Inherit;
    if (ProcessMode != mode) ProcessMode = mode;
  }

  /// <summary>Sets the velocity a paused ship will resume with (or the live one if it isn't paused).</summary>
  public void SetEffectiveVelocity(Vector3 linear, Vector3 angular)
  {
    if (Paused)
    {
      _savedLinear = linear;
      _savedAngular = angular;
    }
    else
    {
      LinearVelocity = linear;
      AngularVelocity = angular;
    }
  }

  /// <summary>Global axis-aligned box around all blocks.</summary>
  public Aabb WorldBounds()
  {
    bool first = true;
    var box = new Aabb();
    foreach (var c in Grid.Cells)
    {
      var cell = new Aabb((Vector3)c, Vector3.One);
      box = first ? cell : box.Merge(cell);
      first = false;
    }
    return GlobalTransform * box;
  }

  public static Ship Create(Material? material, float blockMass)
  {
    var ship = new Ship
    {
      Name = "Ship",
      GravityScale = 0f,
      LinearDampMode = DampMode.Replace,
      LinearDamp = 0f,
      AngularDampMode = DampMode.Replace,
      AngularDamp = 0f,
      ContinuousCd = true,
      ContactMonitor = true,
      MaxContactsReported = 8,
      DisableMode = DisableModeEnum.Remove,
      CenterOfMassMode = CenterOfMassModeEnum.Custom,
      _blockMass = blockMass,
    };
    ship._mesh = new MeshInstance3D { Name = "Mesh", MaterialOverride = material };
    ship.AddChild(ship._mesh);
    return ship;
  }

  /// <summary>Global transform of a block's centre.</summary>
  public Transform3D CellTransform(Vector3I c) => GlobalTransform * new Transform3D(Basis.Identity, (Vector3)c + Vector3.One * 0.5f);

  /// <summary>Thruster and laser blocks (cell, facing), refreshed by Rebuild.</summary>
  public readonly List<(Vector3I Cell, Vector3I Facing)> Thrusters = [];
  public readonly List<(Vector3I Cell, Vector3I Facing)> Lasers = [];

  public bool AddBlock(Vector3I c, bool rebuild = true) => AddBlock(c, Block.Hull, rebuild);

  public bool AddBlock(Vector3I c, Block block, bool rebuild = true)
  {
    if (!Grid.Add(c, block)) return false;
    var shape = new CollisionShape3D { Shape = BlockShape, Position = (Vector3)c + Vector3.One * 0.5f };
    AddChild(shape);
    _shapes[c] = shape;
    if (rebuild) Rebuild();
    return true;
  }

  public bool RemoveBlock(Vector3I c, bool rebuild = true)
  {
    if (!Grid.Remove(c)) return false;
    if (_shapes.Remove(c, out var shape))
    {
      RemoveChild(shape); // out of the physics body now, not at the end of the frame
      shape.QueueFree();
    }
    if (rebuild) Rebuild();
    return true;
  }

  /// <summary>Refreshes the mesh, mass and centre of mass after the blocks changed.</summary>
  public void Rebuild()
  {
    _mesh.Mesh = ShipMesh.Build(Grid, HullColor);
    Mass = Mathf.Max(1, Grid.Count) * _blockMass;
    CenterOfMass = Grid.Centroid();
    Thrusters.Clear();
    Lasers.Clear();
    foreach (var (cell, block) in Grid.Blocks)
    {
      if (block.Type == BlockType.Thruster) Thrusters.Add((cell, block.Facing));
      else if (block.Type == BlockType.Laser) Lasers.Add((cell, block.Facing));
    }
  }

  public override void _IntegrateForces(PhysicsDirectBodyState3D state)
  {
    var b = Builder;
    if (b != null && b.ImpactsDeform) HandleImpacts(state, b);
    _lastLinear = state.LinearVelocity;
    _lastAngular = state.AngularVelocity;
  }

  private void HandleImpacts(PhysicsDirectBodyState3D state, ShipBuilder b)
  {
    double now = Time.GetTicksMsec() / 1000.0;
    var com = state.Transform.Origin + state.CenterOfMass;
    var settings = b.ImpactSettings;
    bool touching = false;
    ImpactModel.Result best = default;
    Vector3 bestPoint = default, bestNormal = default;
    for (int i = 0; i < state.GetContactCount(); i++)
    {
      if (state.GetContactColliderObject(i) is not Node node || !node.IsInGroup(ShipBuilder.TerrainGroup)) continue;
      touching = true;
      var p = state.GetContactLocalPosition(i);
      var n = state.GetContactLocalNormal(i);
      if (n.Dot(com - p) < 0) n = -n; // out of the terrain, toward the ship
      // Velocity just before this step: the solver has already stopped the ship against the surface.
      var v = _lastLinear + _lastAngular.Cross(p - com);
      if (ImpactModel.Evaluate(Mass, v, n, settings, out var r) && r.NormalSpeed > best.NormalSpeed)
      {
        best = r;
        bestPoint = p;
        bestNormal = n;
      }
    }
    if (best.NormalSpeed > 0 && now - _lastImpactAt >= b.ImpactCooldown)
    {
      _lastImpactAt = now;
      _impactNormal = bestNormal;
      _impactSpeed = best.NormalSpeed;
      PendingCraters.Add((bestPoint, best.CraterRadius));
      // Keep going into the crater with what's left once the terrain gives way, but only if the ship fits through
      // it (solid rock is carved out to about half the dig radius). Otherwise the rest of the energy is lost too:
      // the ship stops there, embedded, rather than being driven into rock and pushed back out.
      _carry = best.CraterRadius * 0.5f >= CrossSectionHalfWidth(bestNormal)
        ? -bestNormal * best.RemainingNormalSpeed
        : Vector3.Zero;
      _carryAt = now + b.CarryDelay;
    }
    // Bounciness, enforced: for a moment after an impact the ship may leave that surface no faster than
    // ShipBounce x the impact speed. (The physics engine pushing it out of terrain whose new collision hasn't arrived
    // yet would otherwise read as a bounce.)
    if (now - _lastImpactAt < b.ReboundGuard)
    {
      float away = state.LinearVelocity.Dot(_impactNormal);
      float allowed = b.ShipBounce * _impactSpeed;
      if (away > allowed) state.LinearVelocity -= _impactNormal * (away - allowed);
    }
    if (_carry != Vector3.Zero && (!touching || now >= _carryAt))
    {
      float speed = _carry.Length();
      var dir = _carry / speed;
      float already = state.LinearVelocity.Dot(dir);
      if (already < speed) state.LinearVelocity += dir * (speed - already);
      _carry = Vector3.Zero;
    }
  }

  /// <summary>Half the ship's width across a direction (roughly: from its box, ignoring that direction's extent).</summary>
  private float CrossSectionHalfWidth(Vector3 n)
  {
    var size = WorldBounds().Size;
    float sum = size.X * size.X * (1f - n.X * n.X) + size.Y * size.Y * (1f - n.Y * n.Y) + size.Z * size.Z * (1f - n.Z * n.Z);
    return 0.5f * Mathf.Sqrt(sum * 0.5f);
  }

  /// <summary>Velocity of the body at a global point (linear plus spin).</summary>
  public Vector3 VelocityAt(Vector3 globalPoint) =>
    EffectiveLinearVelocity + EffectiveAngularVelocity.Cross(globalPoint - GlobalTransform * CenterOfMass);
}
