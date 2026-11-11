#nullable enable
using System.Collections.Generic;
using Godot;

namespace Ships;

/// <summary>
/// Owns every ship (they are its children) and does the ship-tool edits:
///   Place: aimed at a ship within Reach, add a block on the face you're looking at; otherwise start a new
///          one-block ship PlaceDistance ahead (closer if terrain is in the way).
///   Break: remove the block you're looking at. A ship left empty is deleted; one cut into pieces splits so every
///          connected group of blocks is its own ship, each keeping the velocity it had at that spot.
/// Targeting uses a physics ray, so terrain in front of a ship blocks it.
/// </summary>
public partial class ShipBuilder : Node3D
{
  [Export] public float Reach = 12f;
  [Export] public float PlaceDistance = 4f;
  [Export] public float BlockMass = 1f;
  [ExportGroup("Simulation")]
  /// <summary>Terrain collision is built this far around a ship...</summary>
  [Export] public float CollisionMargin = 2f;
  /// <summary>...plus how far it travels in this many seconds, so collision is ready before it gets there.</summary>
  [Export] public float CollisionLookahead = 0.5f;
  /// <summary>Cap on the lookahead distance, so a fast ship's box stays inside the detailed terrain.</summary>
  [Export] public float MaxLookahead = 12f;
  [ExportGroup("Impacts")]
  /// <summary>Ships hitting terrain dig craters with part of their momentum (see ImpactModel).</summary>
  [Export] public bool ImpactsDeform = true;
  /// <summary>Slower than this straight into the surface: no crater, so resting / sliding ships don't dig.</summary>
  [Export] public float ImpactMinSpeed = 2f;
  /// <summary>Share of the impact energy that breaks rock (the rest is lost); the ship keeps sqrt(1 - share) of its speed into the surface.</summary>
  [Export(PropertyHint.Range, "0,1,0.05")] public float DeformShare = 0.5f;
  /// <summary>Energy per unit volume of rock dug out (asteroid-like: hard).</summary>
  [Export] public float TerrainHardness = 60f;
  [Export] public float MaxCraterRadius = 12f;
  /// <summary>At most one crater per ship this often (several contact points in one hit are one impact).</summary>
  [Export] public float ImpactCooldown = 0.08f;
  /// <summary>How long a ship waits for its crater's collision before carrying on with its leftover speed anyway.</summary>
  [Export] public float CarryDelay = 0.3f;
  /// <summary>How long after an impact a ship's speed away from that surface is held to ShipBounce x the impact speed.</summary>
  [Export] public float ReboundGuard = 0.5f;
  /// <summary>Ships' bounciness off terrain and each other (0 = they don't bounce; later a block material property).</summary>
  [Export(PropertyHint.Range, "0,1,0.05")] public float ShipBounce = 0f;
  [Export(PropertyHint.Range, "0,2,0.05")] public float ShipFriction = 0.6f;
  [ExportGroup("Spawn")]
  /// <summary>Start with a ready-made ship (ShipPrefabs.Miner) parked in front of the player.</summary>
  [Export] public bool SpawnPrefab = true;
  /// <summary>How far in front of the player the prefab's centre goes.</summary>
  [Export] public float PrefabDistance = 14f;
  /// <summary>Also start with an AI-flown copy of the prefab that follows the player (ShipAI).</summary>
  [Export] public bool SpawnFollower = true;
  /// <summary>Where the follower starts, in the player's view space (x right, -z ahead).</summary>
  [Export] public Vector3 FollowerOffset = new(-45f, 0f, 10f);
  [ExportGroup("")]
  /// <summary>Ship look (toon shading); empty = res://materials/ship_toon.tres.</summary>
  [Export] public Material? ShipMaterial { get; set; }

  /// <summary>What the ship tool is pointing at.</summary>
  public struct Target
  {
    /// <summary>Ship under the crosshair, or null.</summary>
    public Ship? Ship;
    /// <summary>The block hit (valid when Ship != null).</summary>
    public Vector3I Block;
    /// <summary>The empty cell on the face that was hit (valid when Ship != null).</summary>
    public Vector3I PlaceCell;
    /// <summary>
    /// Ship-space axis a thruster / laser placed here would point along: out of the face that was hit, or (for a
    /// new ship) along the view, snapped to the nearest axis.
    /// </summary>
    public Vector3I Facing;
    /// <summary>Global centre where a block would go (on the ship face, or a new ship).</summary>
    public Transform3D PlaceTransform;
    public bool CanPlace;
  }

  /// <summary>Group of the terrain's collision bodies (ChunkManager adds them).</summary>
  public const string TerrainGroup = "terrain";

  private ChunkManager? _cm;
  private PhysicsMaterial? _material;

  public ImpactModel.Settings ImpactSettings => new()
  {
    MinSpeed = ImpactMinSpeed,
    DeformShare = DeformShare,
    Hardness = TerrainHardness,
    MaxCraterRadius = MaxCraterRadius,
  };

  /// <summary>Craters dug by impacts so far, and the size of the last one (debugging and tests).</summary>
  public int CratersDug { get; private set; }
  public float LastCraterRadius { get; private set; }

  public int SimulatingCount { get; private set; }
  public int PausedCount { get; private set; }
  /// <summary>Ships no loaded chunk covers: hidden and out of the physics world (part of PausedCount).</summary>
  public int UnloadedCount { get; private set; }
  private readonly BoxShape3D _probe = new() { Size = Vector3.One * 0.95f };
  private MeshInstance3D _ghostPlace = null!, _ghostBreak = null!, _ghostFacing = null!, _ghostPrefab = null!;

  public IEnumerable<Ship> Ships
  {
    get
    {
      foreach (var n in GetChildren())
        if (n is Ship s && !s.IsQueuedForDeletion()) yield return s;
    }
  }

  public int ShipCount
  {
    get
    {
      int n = 0;
      foreach (var _ in Ships) n++;
      return n;
    }
  }

  public override void _Ready()
  {
    _cm = GetNodeOrNull<ChunkManager>("../ChunkManager");
    if (ShipMaterial == null && ResourceLoader.Exists("res://materials/ship_toon.tres"))
      ShipMaterial = GD.Load<Material>("res://materials/ship_toon.tres");
    _ghostPlace = MakeGhost("GhostPlace", new Color(0.4f, 1f, 0.6f, 0.35f));
    _ghostBreak = MakeGhost("GhostBreak", new Color(1f, 0.3f, 0.25f, 0.35f));
    _ghostFacing = MakeGhost("GhostFacing", new Color(1f, 1f, 1f, 0.8f));
    _ghostFacing.Mesh = new BoxMesh { Size = new Vector3(0.6f, 0.6f, 0.08f) };
    _ghostPrefab = MakeGhost("GhostPrefab", new Color(0.4f, 0.9f, 1f, 0.3f));
    if (SpawnPrefab) CallDeferred(MethodName.SpawnStartShip);
  }

  /// <summary>The prefab ship, PrefabDistance ahead of the player and pointing where they look.</summary>
  private void SpawnStartShip()
  {
    var player = GetNodeOrNull<Node3D>("../Player");
    if (player == null) return;
    var view = (player.GetNodeOrNull<Node3D>("CameraPivot") ?? player).GlobalBasis.Orthonormalized();
    var centre = player.GlobalPosition - view.Z * PrefabDistance;
    SpawnShip(ShipPrefabs.Miner(), new Transform3D(view, centre));
    if (SpawnFollower) SpawnAIShip(ShipPrefabs.Miner(), new Transform3D(view, player.GlobalPosition + view * FollowerOffset));
  }

  /// <summary>Hull colour of AI-flown ships.</summary>
  public static readonly Color AIHullColor = new(0.85f, 0.42f, 0.2f);

  /// <summary>Hull colour of AI ships that dig.</summary>
  public static readonly Color DiggerHullColor = new(0.85f, 0.7f, 0.2f);

  /// <summary>A ship like SpawnShip, flown by a ShipAI doing this job, with an orange (follow) or yellow (dig) hull.</summary>
  public Ship SpawnAIShip(IEnumerable<(Vector3I Cell, Block Block)> blocks, Transform3D where, AIJob job = AIJob.Follow)
  {
    var ship = SpawnShip(blocks, where);
    ship.HullColor = job == AIJob.Dig ? DiggerHullColor : AIHullColor;
    ship.Rebuild();
    var ai = new ShipAI { Name = "AI", Job = job };
    ship.AI = ai;
    ship.AddChild(ai);
    return ship;
  }

  /// <summary>A new ship made of these blocks, with its centre of mass at where.Origin and its axes along where.Basis.</summary>
  public Ship SpawnShip(IEnumerable<(Vector3I Cell, Block Block)> blocks, Transform3D where)
  {
    var ship = NewShip();
    AddChild(ship);
    foreach (var (cell, block) in blocks) ship.AddBlock(cell, block, rebuild: false);
    ship.Rebuild();
    var basis = where.Basis.Orthonormalized();
    ship.GlobalTransform = new Transform3D(basis, where.Origin - basis * ship.CenterOfMass);
    return ship;
  }

  /// <summary>Where a prefab would spawn: its centre-of-mass transform, the ship's own transform, and whether it fits.</summary>
  public struct PrefabSpot
  {
    /// <summary>Centre of mass and orientation (what SpawnShip takes).</summary>
    public Transform3D Where;
    /// <summary>The ship node's transform there (ship-space meshes go here).</summary>
    public Transform3D ShipTransform;
    public bool Free;
  }

  /// <summary>
  /// A prefab placed ahead along the view (far enough that it clears the player, by its size), turned to the view.
  /// Free if its bounding box overlaps no body or terrain collision and no block centre is inside rock.
  /// </summary>
  public PrefabSpot PrefabPlacement(Vector3 from, Vector3 dir, Basis view, ShipGrid grid, Rid exclude)
  {
    var spot = new PrefabSpot();
    if (grid.Count == 0) return spot;
    var com = grid.Centroid();
    float radius = 0;
    bool first = true;
    var local = new Aabb();
    foreach (var c in grid.Cells)
    {
      radius = Mathf.Max(radius, ((Vector3)c + Vector3.One * 0.5f).DistanceTo(com) + 0.87f);
      var cell = new Aabb((Vector3)c, Vector3.One);
      local = first ? cell : local.Merge(cell);
      first = false;
    }
    var basis = view.Orthonormalized();
    var centre = from + dir.Normalized() * Mathf.Max(8f, radius * 2f + 3f);
    spot.Where = new Transform3D(basis, centre);
    spot.ShipTransform = new Transform3D(basis, centre - basis * com);

    var box = new BoxShape3D { Size = (local.Size - Vector3.One * 0.05f).Max(Vector3.One * 0.1f) };
    var q = new PhysicsShapeQueryParameters3D { Shape = box, Transform = new Transform3D(basis, spot.ShipTransform * local.GetCenter()) };
    if (exclude.IsValid) q.Exclude = [exclude];
    spot.Free = GetWorld3D().DirectSpaceState.IntersectShape(q, 1).Count == 0;
    if (spot.Free && _cm?.PrepareField(spot.ShipTransform * local) is { } field)
      foreach (var c in grid.Cells)
        if (field.Density(spot.ShipTransform * ((Vector3)c + Vector3.One * 0.5f)) >= Voxel.VoxelConst.Iso)
        {
          spot.Free = false;
          break;
        }
    return spot;
  }

  /// <summary>Translucent preview of a prefab at a placement (blue-green if it fits, red if not, orange / yellow with AI).</summary>
  public void UpdatePrefabGhost(bool active, PrefabSpot spot, Mesh? mesh, AIJob? job)
  {
    _ghostPrefab.Visible = active && mesh != null;
    if (!_ghostPrefab.Visible) return;
    _ghostPrefab.Mesh = mesh;
    _ghostPrefab.GlobalTransform = spot.ShipTransform;
    var color = !spot.Free ? new Color(1f, 0.25f, 0.2f, 0.3f)
      : job == AIJob.Follow ? new Color(1f, 0.55f, 0.2f, 0.32f)
      : job == AIJob.Dig ? new Color(1f, 0.85f, 0.2f, 0.32f)
      : new Color(0.4f, 0.9f, 1f, 0.3f);
    ((StandardMaterial3D)_ghostPrefab.MaterialOverride).AlbedoColor = color;
  }

  /// <summary>Spawns a prefab at a placement, flown by an AI doing this job (null: nobody).</summary>
  public Ship SpawnFromPrefab(IEnumerable<(Vector3I Cell, Block Block)> blocks, PrefabSpot spot, AIJob? job) =>
    job is AIJob j ? SpawnAIShip(blocks, spot.Where, j) : SpawnShip(blocks, spot.Where);

  private MeshInstance3D MakeGhost(string name, Color color)
  {
    var g = new MeshInstance3D
    {
      Name = name,
      Mesh = new BoxMesh { Size = Vector3.One * 1.02f },
      MaterialOverride = new StandardMaterial3D
      {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        AlbedoColor = color,
        NoDepthTest = false,
      },
      CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
      TopLevel = true,
      Visible = false,
    };
    AddChild(g);
    return g;
  }

  /// <summary>
  /// Each physics step, per ship:
  ///   - Its centre isn't in any loaded chunk (of any LOD): unloaded. Paused with its velocity saved, hidden and out
  ///     of the physics world; it carries on once terrain is loaded around it again. The loaded chunks follow the
  ///     player, so they are the load / unload boundary.
  ///   - Otherwise it asks for terrain collision around itself (plus a bit ahead along its motion) from whatever
  ///     chunks are loaded there, coarse ones included, and simulates. If the collision right around it isn't built
  ///     yet it's held still (still visible) until it is.
  ///   - The ship being flown is never paused or unloaded.
  /// </summary>
  public override void _PhysicsProcess(double delta)
  {
    if (_cm == null) return;
    int sim = 0, paused = 0, unloaded = 0;
    foreach (var ship in Ships)
    {
      foreach (var (point, radius) in ship.PendingCraters)
      {
        _cm.Dig(point, radius, 1f);
        CratersDug++;
        LastCraterRadius = radius;
      }
      ship.PendingCraters.Clear();
      if (ship.BlockCount == 0) continue;
      if (!ship.Piloted && !_cm.Loaded(ship.GlobalTransform * ship.CenterOfMass))
      {
        ship.SetSimulating(false, unload: true);
        paused++;
        unloaded++;
        continue;
      }
      var near = ship.WorldBounds().Grow(CollisionMargin);
      float ahead = Mathf.Min(ship.EffectiveLinearVelocity.Length() * CollisionLookahead, MaxLookahead);
      _cm.RequestCollision(near.Grow(ahead));
      bool run = ship.Piloted || _cm.CollisionReadyFor(near);
      ship.SetSimulating(run, unload: false);
      if (run) sim++;
      else paused++;
    }
    SimulatingCount = sim;
    PausedCount = paused;
    UnloadedCount = unloaded;
  }

  /// <summary>Works out what a click would do from this camera ray (dir normalised); exclude = the player's body.</summary>
  public Target Aim(Vector3 from, Vector3 dir, Rid exclude)
  {
    var t = new Target();
    var space = GetWorld3D().DirectSpaceState;
    var query = PhysicsRayQueryParameters3D.Create(from, from + dir * Reach);
    query.Exclude = [exclude];
    var hit = space.IntersectRay(query);
    float placeDist = PlaceDistance;
    if (hit.Count > 0)
    {
      var point = hit["position"].AsVector3();
      var normal = hit["normal"].AsVector3();
      if (hit["collider"].AsGodotObject() is Ship ship)
      {
        var local = ship.ToLocal(point);
        var n = ship.GlobalBasis.Inverse() * normal;
        var block = (Vector3I)(local - n * 0.5f).Floor();
        if (ship.Grid.Contains(block))
        {
          t.Ship = ship;
          t.Block = block;
          t.Facing = DominantAxis(n);
          t.PlaceCell = block + t.Facing;
          t.PlaceTransform = ship.CellTransform(t.PlaceCell);
          t.CanPlace = Free(t.PlaceTransform, exclude: default);
          return t;
        }
      }
      // Something else (terrain) is in the way: a new ship goes just in front of it.
      placeDist = Mathf.Min(placeDist, from.DistanceTo(point) - 0.75f);
    }
    t.Facing = DominantAxis(dir);
    t.PlaceTransform = new Transform3D(Basis.Identity, from + dir * placeDist);
    t.CanPlace = placeDist >= 1f && Free(t.PlaceTransform, exclude: default);
    return t;
  }

  /// <summary>
  /// Adds a block of this type where the crosshair points. Thrusters and lasers point out of the face they go on
  /// (see Target.Facing). False if there is no room.
  /// </summary>
  public bool Place(Vector3 from, Vector3 dir, Rid exclude, BlockType type = BlockType.Hull)
  {
    var t = Aim(from, dir, exclude);
    if (!t.CanPlace) return false;
    var block = type == BlockType.Hull ? Block.Hull : new Block(type, t.Facing);
    if (t.Ship != null) return t.Ship.AddBlock(t.PlaceCell, block);

    var ship = NewShip();
    AddChild(ship);
    // The single block (cell 0) is centred on the target point; new ships line up with the world axes.
    ship.GlobalTransform = new Transform3D(Basis.Identity, t.PlaceTransform.Origin - Vector3.One * 0.5f);
    ship.AddBlock(Vector3I.Zero, block);
    return true;
  }

  /// <summary>A ship with this builder's look, impact settings and physics material.</summary>
  public Ship NewShip()
  {
    _material ??= new PhysicsMaterial();
    _material.Bounce = ShipBounce;
    _material.Friction = ShipFriction;
    var ship = Ship.Create(ShipMaterial, BlockMass);
    ship.Builder = this;
    ship.PhysicsMaterialOverride = _material;
    return ship;
  }

  /// <summary>Removes the block under the crosshair. False if not aiming at a ship.</summary>
  public bool Break(Vector3 from, Vector3 dir, Rid exclude)
  {
    var t = Aim(from, dir, exclude);
    if (t.Ship == null) return false;
    var ship = t.Ship;
    ship.RemoveBlock(t.Block);
    if (ship.BlockCount == 0)
    {
      RemoveChild(ship);
      ship.QueueFree();
      return true;
    }
    SplitDisconnected(ship);
    return true;
  }

  /// <summary>Keeps the largest connected group in the ship; every other group becomes its own ship.</summary>
  public void SplitDisconnected(Ship ship)
  {
    var groups = ship.Grid.Components();
    if (groups.Count <= 1) return;
    for (int g = 1; g < groups.Count; g++)
    {
      var piece = NewShip();
      AddChild(piece);
      piece.GlobalTransform = ship.GlobalTransform;
      foreach (var c in groups[g])
      {
        ship.Grid.TryGet(c, out var block);
        ship.RemoveBlock(c, rebuild: false);
        piece.AddBlock(c, block, rebuild: false);
      }
      piece.Rebuild();
      // Each piece carries on with the velocity the ship had where the piece is.
      var com = piece.GlobalTransform * piece.CenterOfMass;
      if (ship.Paused) piece.SetSimulating(false, unload: ship.Unloaded);
      piece.SetEffectiveVelocity(ship.VelocityAt(com), ship.EffectiveAngularVelocity);
    }
    ship.Rebuild();
  }

  /// <summary>
  /// Shows where a right click would put a block (tinted by type, with a plate on the side a thruster / laser would
  /// point) and which block a left click would break.
  /// </summary>
  public void UpdateGhost(bool active, Vector3 from, Vector3 dir, Rid exclude, BlockType type = BlockType.Hull)
  {
    if (!active)
    {
      _ghostPlace.Visible = _ghostBreak.Visible = _ghostFacing.Visible = false;
      return;
    }
    var t = Aim(from, dir, exclude);
    _ghostPlace.Visible = t.CanPlace;
    if (t.CanPlace)
    {
      _ghostPlace.GlobalTransform = t.PlaceTransform;
      var tint = ShipMesh.BodyColor(type);
      ((StandardMaterial3D)_ghostPlace.MaterialOverride).AlbedoColor = type == BlockType.Hull
        ? new Color(0.4f, 1f, 0.6f, 0.35f)
        : new Color(tint.R, tint.G, tint.B, 0.5f);
    }
    _ghostFacing.Visible = t.CanPlace && type != BlockType.Hull;
    if (_ghostFacing.Visible)
    {
      var basis = t.PlaceTransform.Basis.Orthonormalized();
      var facing = basis * (Vector3)t.Facing;
      var up = Mathf.Abs(facing.Dot(Vector3.Up)) > 0.9f ? Vector3.Right : Vector3.Up;
      var accent = type == BlockType.Thruster ? ShipMesh.NozzleColor : ShipMesh.EmitterColor;
      ((StandardMaterial3D)_ghostFacing.MaterialOverride).AlbedoColor = new Color(accent.R, accent.G, accent.B, 0.85f);
      _ghostFacing.GlobalTransform = new Transform3D(Basis.LookingAt(facing, up), t.PlaceTransform.Origin + facing * 0.52f);
    }
    _ghostBreak.Visible = t.Ship != null;
    if (t.Ship != null) _ghostBreak.GlobalTransform = t.Ship.CellTransform(t.Block);
  }

  /// <summary>True if a block at this transform would overlap nothing (bodies, terrain shapes, solid terrain).</summary>
  private bool Free(Transform3D at, Rid exclude)
  {
    var q = new PhysicsShapeQueryParameters3D { Shape = _probe, Transform = at };
    if (exclude.IsValid) q.Exclude = [exclude];
    if (GetWorld3D().DirectSpaceState.IntersectShape(q, 1).Count > 0) return false;
    return _cm == null || _cm.DensityAt(at.Origin) < Voxel.VoxelConst.Iso;
  }

  private static Vector3I DominantAxis(Vector3 n)
  {
    var a = n.Abs();
    if (a.X >= a.Y && a.X >= a.Z) return new Vector3I(n.X > 0 ? 1 : -1, 0, 0);
    if (a.Y >= a.Z) return new Vector3I(0, n.Y > 0 ? 1 : -1, 0);
    return new Vector3I(0, 0, n.Z > 0 ? 1 : -1);
  }
}
