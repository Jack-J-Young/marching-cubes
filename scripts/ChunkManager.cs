#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Godot;
using Voxel;

/// <summary>
/// Streams an octree of marching-cubes chunks around the camera.
///
/// Main thread only does bookkeeping. Octree rebuilds, noise, edits and meshing all run on worker threads, and
/// finished meshes are applied within a per-frame time budget. Old chunks stay visible until their replacements
/// are ready, so LOD changes never leave holes.
/// </summary>
public partial class ChunkManager : Node3D
{
  [Export] public FastNoiseLite Noise { get; set; } = new();
  [Export] public FastNoiseLite ColorNoise { get; set; } = new();
  /// <summary>
  /// Style template for the terrain (shader parameters such as the hatch textures and shade bands). Chunk materials
  /// copy its parameters; empty = res://materials/terrain_toon.tres.
  /// </summary>
  [Export] public ShaderMaterial? TerrainMaterial { get; set; }

  /// <summary>
  /// Empty space hollowed out around the player's starting position (radius in units, 0 = none), so there is room to
  /// spawn and fly. Its wall blends into the rock over SpawnClearEdge units.
  /// </summary>
  [ExportGroup("Spawn")]
  [Export(PropertyHint.Range, "0,1024,1")] public float SpawnClearRadius = 256f;
  [Export(PropertyHint.Range, "0.5,64,0.5")] public float SpawnClearEdge = 8f;
  /// <summary>Centre of the clearing when there's no Player sibling to take it from (ChunkManager-local).</summary>
  [Export] public Vector3 SpawnClearCenter;

  /// <summary>Coarsest LOD. Root nodes are 32 &lt;&lt; MaxLod units wide.</summary>
  [ExportGroup("LOD")]
  [Export(PropertyHint.Range, "0,8")] public int MaxLod = 3;
  /// <summary>A node splits while the camera is closer than SplitFactor * its size. Higher = more detail, more chunks.</summary>
  [Export(PropertyHint.Range, "0.5,4,0.05")] public float LodSplitFactor = 1.0f;
  /// <summary>Root nodes loaded in each direction around the camera's root node.</summary>
  [Export(PropertyHint.Range, "0,4")] public int RootRadius = 1;

  /// <summary>
  /// LOD0 chunks within this many chunks (Chebyshev distance) of the camera get collision. Each collision shape
  /// costs a few ms of main thread (Jolt builds it when the body enters the tree), so keep this small; digging
  /// doesn't need collision (see RaycastTerrain).
  /// </summary>
  [ExportGroup("Performance")]
  [Export(PropertyHint.Range, "0,8")] public int CollisionRadius = 1;
  /// <summary>
  /// Chunks whose collision was requested (RequestCollision, e.g. around a ship) keep it this long after the last
  /// request, so something hovering on a chunk border doesn't make it rebuild back and forth.
  /// </summary>
  [Export(PropertyHint.Range, "0,30,0.5")] public float CollisionKeepSeconds = 2f;
  /// <summary>Main-thread time per frame spent turning finished jobs into meshes and shapes.</summary>
  [Export(PropertyHint.Range, "0.5,16,0.5")] public double ApplyBudgetMs = 3.0;
  /// <summary>
  /// Collision shapes are the costliest thing the main thread builds. A chunk away from the player that already has
  /// one gets it rebuilt at most this often while edits keep coming (mining drones): ships meet the dug-out rock up
  /// to this late. The player's own collision area is always rebuilt straight away.
  /// </summary>
  [Export(PropertyHint.Range, "0,5,0.05")] public float CollisionRefreshSeconds = 0.75f;
  /// <summary>0 = automatic (processor count - 2, at most 8), leaving cores for the main and render threads.</summary>
  [Export(PropertyHint.Range, "0,64")] public int WorkerThreads = 0;
  /// <summary>
  /// Volumes kept in memory (about 0.5 MB each, 0.7 MB above LOD0) so rebuilds skip noise and only apply new digs:
  /// LOD0 chunks near the camera, plus chunks of any LOD dug in the last EditCacheSeconds. Least recently used go first.
  /// </summary>
  [Export(PropertyHint.Range, "0,2048")] public int VolumeCacheSize = 256;
  /// <summary>How long a dug chunk keeps its volume cached (holding a big brush rebuilds the same chunks over and over).</summary>
  [Export(PropertyHint.Range, "0,600")] public float EditCacheSeconds = 60f;

  /// <summary>
  /// Chunks touched by digs are rebuilt against one edit snapshot and shown in the same frame, so seams never
  /// open between a chunk that has a dig and its neighbour that doesn't yet. Off = each chunk shows as soon as it's built.
  /// </summary>
  [ExportGroup("Editing")]
  [Export] public bool BatchEdits = true;
  /// <summary>A batch waiting longer than this shows what it has and lets the stragglers apply on their own.</summary>
  [Export(PropertyHint.Range, "50,5000,10")] public double MaxBatchWaitMs = 500;
  /// <summary>
  /// How long an edited mesh takes to slide from its previous surface to the new one, for a lone edit. 0 = no animation.
  /// While digging is held, each animation instead lasts a little less than the recent gap between commits (up to
  /// MaxMorphSeconds): long enough that the surface doesn't stop and wait for the next commit (big brushes touch more
  /// chunks, so their commits are further apart), short enough that the next commit rarely cuts it off mid-way.
  /// </summary>
  [Export(PropertyHint.Range, "0,1,0.01")] public float MorphSeconds = 0.1f;
  /// <summary>
  /// When digs arrive faster than chunks can be rebuilt, a dig that no build has picked up yet absorbs the next one
  /// (same radius, centre within this fraction of the radius) instead of adding another edit. Keeps the edit list and
  /// per-rebuild work proportional to the rebuild rate, not the brush tick rate. 0 = never merge.
  /// </summary>
  [Export(PropertyHint.Range, "0,1,0.05")] public float MergeEditShift = 0.25f;
  [Export(PropertyHint.Range, "0,2,0.01")] public float MaxMorphSeconds = 0.5f;

  private readonly EditStore _edits = new();
  private TerrainGenerator? _gen;
  /// <summary>The terrain's density / colour / edits (thread-safe), for anything that builds its own copy (the map).</summary>
  internal TerrainGenerator? Generator => _gen;
  /// <summary>Chunks known to be all air or all rock (builds skip them; the ship pathfinder flies through the air ones).</summary>
  public UniformChunks? Uniform { get; private set; }
  private VolumeCache? _cache;
  private MeshJobScheduler? _scheduler;
  private Shader? _shader;
  private Shader? _morphShader;
  private readonly ShaderMaterial?[] _materials = new ShaderMaterial?[64];

  /// <summary>Chunks rebuilt together for one edit snapshot. Results are held until every member has one.</summary>
  private sealed class EditBatch
  {
    public int Target;
    public long StartTicks;
    public readonly HashSet<Chunk> Members = [];
    public readonly Dictionary<Chunk, (MeshResult Result, ArrayMesh? Mesh)> Held = [];
    public double CompletedAt;
    public double BuildMs;
    public double MorphDuration;
  }

  /// <summary>Finished batches waiting their turn to be shown, oldest first (one animation after another).</summary>
  private readonly Queue<EditBatch> _readyBatches = new();

  private EditBatch? _batch;
  private readonly HashSet<Chunk> _nextBatch = [];
  /// <summary>Newest edit Seq every shown chunk is guaranteed to include or be queued for.</summary>
  private int _committedSeq = -1;
  /// <summary>Newest edit Seq any build has been given. Newer edits can still be merged in place.</summary>
  private int _handedOutSeq = -1;
  private readonly List<SphereEdit> _probeEdits = [];

  private readonly Queue<Chunk> _collisionQueue = new();
  /// <summary>Collision rebuilds held back by CollisionRefreshSeconds (still flagged CollisionQueued).</summary>
  private readonly List<Chunk> _collisionLater = [];

  private readonly List<MorphGroup> _morphGroups = [];
  private readonly Stack<ShaderMaterial> _morphPool = new();
  private MorphGroup? _frameMorph;
  private int _morphingChunks;
  private double _lastCompleteTime = double.NegativeInfinity;
  /// <summary>When the latest batch commit's animation ends.</summary>
  private double _morphEndTime = double.NegativeInfinity;
  private double _gapEma = -1;
  private double _morphJumpSum;
  private int _morphStarts;
  /// <summary>Duration for morphs started this frame (paced to the commit rate while digging).</summary>
  private double _morphDuration;

  /// <summary>Copies the template's style parameters (everything but the per-chunk ones) onto a chunk material.</summary>
  private void CopyStyle(ShaderMaterial m)
  {
    if (TerrainMaterial?.Shader == null) return;
    foreach (var u in TerrainMaterial.Shader.GetShaderUniformList())
    {
      var name = u.AsGodotDictionary()["name"].AsStringName();
      if (name == "lod_mask" || name == "morph_t") continue;
      m.SetShaderParameter(name, TerrainMaterial.GetShaderParameter(name));
    }
  }

  /// <summary>One shared material per seam mask, so no per-instance shader state is needed.</summary>
  private ShaderMaterial MaterialFor(int mask)
  {
    mask = Math.Clamp(mask, 0, 63);
    var m = _materials[mask];
    if (m == null)
    {
      m = new ShaderMaterial { Shader = _shader };
      CopyStyle(m);
      m.SetShaderParameter("lod_mask", mask);
      _materials[mask] = m;
    }
    return m;
  }

  private readonly Dictionary<ChunkKey, Chunk> _active = [];
  private readonly Dictionary<ChunkKey, Chunk> _retiring = [];
  private readonly HashSet<Chunk> _pendingShow = [];
  private readonly List<Chunk> _scratch = [];

  private Task<OctreeResult>? _rebuild;
  private Vector3I _viewerCell = new(int.MinValue, 0, 0);
  private Vector3 _viewer;
  private bool _rebuildRequested = true;

  // Stats for the debugger Monitors tab.
  private double _avgGenMs, _avgMeshMs, _applyMs, _octreeMs, _diffMs, _processMs, _swapMs, _maxItemMs;
  private long _vertexCount;
  private int _visibleCount;

  // Per-frame main-thread breakdown for the debug overlay.
  private double _frameMeshMs, _frameCollisionMs, _frameDiffMs, _digMsAccum;

  /// <summary>Main-thread terrain time spent in the last frame, in milliseconds.</summary>
  public sealed class FrameTimes
  {
    public double ApplyMeshMs;
    public double CollisionMs;
    public double SwapMs;
    public double OctreeDiffMs;
    /// <summary>Dig() and RaycastTerrain() calls (made from Player, earlier in the same frame).</summary>
    public double DigMs;
    /// <summary>Everything above plus bookkeeping.</summary>
    public double TotalMs;
  }

  public FrameTimes LastFrame { get; } = new();

  public int ActiveChunks => _active.Count;
  public int VisibleChunks => _visibleCount;
  public int RetiringChunks => _retiring.Count;
  public int PendingChunks => (_scheduler?.Results.Count ?? 0) + _pendingShow.Count + BatchInFlight + _nextBatch.Count +
                              _collisionQueue.Count;
  public int BatchInFlight => (_batch?.Members.Count ?? 0) + ReadyBatchChunks;
  private int ReadyBatchChunks
  {
    get
    {
      int n = 0;
      foreach (var b in _readyBatches) n += b.Members.Count;
      return n;
    }
  }
  public int BatchQueued => _nextBatch.Count;
  public int LastBatchSize { get; private set; }
  /// <summary>Members of the last committed batch per LOD.</summary>
  public readonly int[] LastBatchByLod = new int[8];
  public double LastBatchMs { get; private set; }
  public int BatchesCommitted { get; private set; }
  /// <summary>Digs folded into a pending edit instead of adding one (see MergeEditShift).</summary>
  public int EditsMerged { get; private set; }
  /// <summary>Main-thread time spent in digs and terrain rays since start (ms).</summary>
  public double DigMsTotal { get; private set; }
  public int MorphingChunks => _morphingChunks;
  /// <summary>Duration of the most recent morph animation, in seconds.</summary>
  public double LastMorphSeconds { get; private set; }

  /// <summary>
  /// Average share of the previous animation still left when a chunk's next one started (that part is skipped, a
  /// visible jump). 0 = every animation finished first.
  /// </summary>
  public double MorphJumpAverage => _morphStarts == 0 ? 0 : _morphJumpSum / _morphStarts;

  public void ResetMorphStats()
  {
    _morphJumpSum = 0;
    _morphStarts = 0;
  }
  public int CollisionQueueLength => _collisionQueue.Count;

  /// <summary>The active chunk for key, if any (for tests and debugging).</summary>
  /// <summary>Every edit so far (for tests).</summary>
  public List<SphereEdit> EditsSnapshot()
  {
    var list = new List<SphereEdit>();
    _edits.CopyAll(list);
    return list;
  }

  /// <summary>Called whenever a result's mesh is put on screen (for tests).</summary>
  public Action<Chunk, MeshResult>? MeshApplied;

  public bool TryGetChunk(ChunkKey key, out Chunk chunk) => _active.TryGetValue(key, out chunk!);

  /// <summary>Active chunks a dig at globalPos with this radius would touch (for tests and debugging).</summary>
  public void ChunksTouching(Vector3 globalPos, float radius, List<Chunk> output)
  {
    output.Clear();
    var p = ToLocal(globalPos);
    foreach (var c in _active.Values)
    {
      if (VoxelMath.SphereIntersectsAabb(p, radius, c.Key.PaddedBounds)) output.Add(c);
    }
  }
  public int QueueLength => _scheduler?.QueueLength ?? 0;
  public int BusyWorkers => _scheduler?.Busy ?? 0;
  public int WorkerCount => _scheduler?.WorkerCount ?? 0;
  public int CachedVolumes => _cache?.Count ?? 0;
  public int EditCount => _edits.Count;
  public long VertexCount => _vertexCount;
  public double AvgGenerateMs => _avgGenMs;
  public double AvgMeshMs => _avgMeshMs;
  public double OctreeBuildMs => _octreeMs;
  public int CollisionChunks { get; private set; }
  /// <summary>Chunks (any LOD) currently wanted for collision by RequestCollision (not counting the player's own area).</summary>
  public int RequestedCollisionChunks { get; private set; }

  private readonly List<Aabb> _collisionRequests = [];
  /// <summary>Requested regions as LOD0 cell ranges (inclusive), with when they were last asked for.</summary>
  private readonly Dictionary<(Vector3I Lo, Vector3I Hi), double> _collisionWanted = [];
  private readonly List<(Vector3I Lo, Vector3I Hi)> _expiredBoxes = [];

  /// <summary>
  /// Asks for terrain collision in the chunks (of whatever LOD is loaded there) overlapping a global box. Call every
  /// physics frame for as long as it's needed, e.g. around each ship. The player's own area always has collision.
  /// </summary>
  public void RequestCollision(Aabb globalBox) => _collisionRequests.Add(GlobalTransform.AffineInverse() * globalBox);

  /// <summary>
  /// True when terrain is loaded at this global point: some chunk of the octree (any LOD) covers it. Outside the
  /// loaded area there is no terrain at all, so nothing there can be simulated.
  /// </summary>
  public bool Loaded(Vector3 globalPoint) => CoveringChunk(GlobalTransform.AffineInverse() * globalPoint, out _);

  /// <summary>The active chunk (any LOD) whose region contains this local point.</summary>
  private bool CoveringChunk(Vector3 local, out Chunk chunk)
  {
    for (var key = ChunkKey.Containing(local, 0); key.Lod <= MaxLod; key = key.Parent)
      if (_active.TryGetValue(key, out chunk!)) return true;
    chunk = null!;
    return false;
  }

  /// <summary>Has its collision shape, or has none to have (no surface, or a surface with no regular cells).</summary>
  private static bool CollisionSettled(Chunk c) => c.Ready && (c.MeshInstance == null || c.HasCollision || c.NoCollisionFaces);

  /// <summary>
  /// True when the loaded terrain overlapping the global box has its collision: every chunk covering it (any LOD)
  /// is built and has its shape. While a chunk is being replaced (LOD change) the outgoing one, still on screen,
  /// counts if it has its shape. Parts of the box where no terrain is loaded at all have nothing to wait for.
  /// </summary>
  public bool CollisionReadyFor(Aabb globalBox)
  {
    var box = GlobalTransform.AffineInverse() * globalBox;
    var lo = ChunkKey.Containing(box.Position, 0).Coord;
    var hi = ChunkKey.Containing(box.End, 0).Coord;
    for (int z = lo.Z; z <= hi.Z; z++)
      for (int y = lo.Y; y <= hi.Y; y++)
        for (int x = lo.X; x <= hi.X; x++)
        {
          var cell = new ChunkKey(0, new Vector3I(x, y, z));
          bool covered = false, ready = false;
          for (var key = cell; key.Lod <= MaxLod; key = key.Parent)
          {
            if (_active.TryGetValue(key, out var c))
            {
              covered = true;
              ready |= CollisionSettled(c);
            }
            if (_retiring.TryGetValue(key, out var r) && CollisionSettled(r)) ready = true;
          }
          if (covered && !ready) return false;
        }
    return true;
  }

  /// <summary>Whether a chunk overlaps one of the requested collision regions.</summary>
  private bool Requested(ChunkKey key)
  {
    if (_collisionWanted.Count == 0) return false;
    var lo = new Vector3I(key.Coord.X << key.Lod, key.Coord.Y << key.Lod, key.Coord.Z << key.Lod);
    var hi = lo + Vector3I.One * ((1 << key.Lod) - 1);
    foreach (var (b, _) in _collisionWanted)
    {
      if (b.Hi.X < lo.X || b.Lo.X > hi.X || b.Hi.Y < lo.Y || b.Lo.Y > hi.Y || b.Hi.Z < lo.Z || b.Lo.Z > hi.Z) continue;
      return true;
    }
    return false;
  }

  private bool WantsCollision(ChunkKey key) =>
    PlayerCollisionArea(key) || Requested(key);

  /// <summary>The detail chunks around the player that always have collision.</summary>
  private bool PlayerCollisionArea(ChunkKey key) => key.Lod == 0 && Chebyshev(key.Coord, _viewerCell) <= CollisionRadius;

  /// <summary>
  /// Which chunks should have collision: LOD0 chunks in the player's area (CollisionRadius), plus chunks of any LOD
  /// overlapping a region requested in the last CollisionKeepSeconds (a ship far out among coarse chunks collides
  /// with their coarser surface). Starts collision builds for newly wanted chunks and drops it from the rest.
  /// </summary>
  private void UpdateCollisionWants()
  {
    double now = Time.GetTicksMsec() / 1000.0;
    foreach (var box in _collisionRequests)
      _collisionWanted[(ChunkKey.Containing(box.Position, 0).Coord, ChunkKey.Containing(box.End, 0).Coord)] = now;
    _collisionRequests.Clear();
    if (_collisionWanted.Count > 0)
    {
      _expiredBoxes.Clear();
      foreach (var (b, t) in _collisionWanted)
        if (now - t > CollisionKeepSeconds) _expiredBoxes.Add(b);
      foreach (var b in _expiredBoxes) _collisionWanted.Remove(b);
    }

    int requested = 0;
    foreach (var (key, c) in _active)
    {
      bool byRequest = Requested(key);
      if (byRequest) requested++;
      bool want = byRequest || (key.Lod == 0 && Chebyshev(key.Coord, _viewerCell) <= CollisionRadius);
      if (want != c.WantCollision)
      {
        c.WantCollision = want;
        if (!want)
        {
          c.PendingCollision = null;
          c.CollisionRequested = false;
          if (c.HasCollision) RemoveCollision(c);
        }
      }
      // Built without a shape (it wasn't wanted then): rebuild once, with collision.
      if (want && c.Ready && !c.HasCollision && !c.NoCollisionFaces && c.MeshInstance != null && !c.CollisionRequested
          && c.PendingCollision == null)
      {
        c.CollisionRequested = true;
        _scheduler!.Request(c, PriorityOf(c) - 1000f);
      }
    }
    RequestedCollisionChunks = requested;
  }

  /// <summary>Counts active chunks per LOD into counts (index = LOD).</summary>
  public void CountByLod(int[] counts)
  {
    System.Array.Clear(counts);
    foreach (var key in _active.Keys)
    {
      if (key.Lod < counts.Length) counts[key.Lod]++;
    }
  }

  private static readonly string[] MonitorNames =
  [
    "voxel/queue", "voxel/busy_workers", "voxel/active_chunks", "voxel/visible_chunks", "voxel/retiring_chunks",
    "voxel/vertices", "voxel/avg_generate_ms", "voxel/avg_mesh_ms", "voxel/apply_ms", "voxel/octree_ms",
    "voxel/cached_volumes", "voxel/edits", "voxel/pending", "voxel/process_ms", "voxel/diff_ms",
    "voxel/swap_ms", "voxel/max_item_ms",
  ];

  public override void _Ready()
  {
    if (Engine.IsEditorHint()) return;

    var player = GetNodeOrNull<Node3D>("../Player");
    var clearCenter = player != null ? ToLocal(player.GlobalPosition) : SpawnClearCenter;
    _gen = new TerrainGenerator(Noise, ColorNoise, _edits, clearCenter, SpawnClearRadius, SpawnClearEdge);
    _cache = new VolumeCache(VolumeCacheSize);
    Uniform = new UniformChunks(_edits);
    var builder = new ChunkBuilder(_gen, _cache, Uniform);
    int workers = WorkerThreads > 0 ? WorkerThreads : Math.Clamp(System.Environment.ProcessorCount - 2, 1, 8);
    _scheduler = new MeshJobScheduler(workers, builder.Build);
    _shader = GD.Load<Shader>("res://shaders/terrain.gdshader");
    _morphShader = GD.Load<Shader>("res://shaders/terrain_morph.gdshader");
    TerrainMaterial ??= ResourceLoader.Exists("res://materials/terrain_toon.tres")
      ? GD.Load<ShaderMaterial>("res://materials/terrain_toon.tres")
      : null;

    RegisterMonitors();
  }

  public override void _ExitTree()
  {
    _scheduler?.Dispose();
    _scheduler = null;
    foreach (var name in MonitorNames)
    {
      if (Performance.HasCustomMonitor(name)) Performance.RemoveCustomMonitor(name);
    }
  }

  public override void _Process(double delta)
  {
    if (_scheduler == null) return;
    var frameSw = Stopwatch.StartNew();
    _frameMeshMs = _frameCollisionMs = _frameDiffMs = 0;

    var cam = GetViewport().GetCamera3D();
    _viewer = cam != null ? ToLocal(cam.GlobalPosition) : Vector3.Zero;

    var cell = ChunkKey.Containing(_viewer, 0).Coord;
    if (cell != _viewerCell)
    {
      _viewerCell = cell;
      _rebuildRequested = true;
    }

    if (_rebuild != null && _rebuild.IsCompleted)
    {
      var result = _rebuild.Result;
      _rebuild = null;
      var diffSw = Stopwatch.StartNew();
      ApplyOctree(result);
      _diffMs = diffSw.Elapsed.TotalMilliseconds;
      _frameDiffMs = _diffMs;
    }
    if (_rebuildRequested && _rebuild == null)
    {
      _rebuildRequested = false;
      var viewer = _viewer;
      int maxLod = MaxLod, radius = RootRadius;
      float split = LodSplitFactor;
      _rebuild = Task.Run(() => ChunkOctree.Build(viewer, maxLod, split, radius));
    }

    _frameMorph = null;
    _morphDuration = MorphSeconds;
    if (_batch == null && _nextBatch.Count == 0 && _readyBatches.Count == 0) _committedSeq = _edits.NewestSeq;
    LaunchBatch();
    UpdateCollisionWants();
    ApplyResults();
    UpdateMorphs();
    var swapSw = Stopwatch.StartNew();
    UpdateSwaps();
    _swapMs = swapSw.Elapsed.TotalMilliseconds;
    _processMs = frameSw.Elapsed.TotalMilliseconds;

    var f = LastFrame;
    f.ApplyMeshMs = _frameMeshMs;
    f.CollisionMs = _frameCollisionMs;
    f.SwapMs = _swapMs;
    f.OctreeDiffMs = _frameDiffMs;
    f.DigMs = _digMsAccum;
    f.TotalMs = _processMs + _digMsAccum;
    DigMsTotal += _digMsAccum;
    _digMsAccum = 0;
  }

  /// <summary>
  /// Digs a sphere at a global position. strength (0-1) is how much of a full carve this one edit does; call it
  /// repeatedly with a small strength for gradual digging. Every loaded chunk it touches is rebuilt at top priority
  /// (rebuild requests made while a chunk is already building coalesce into one).
  /// </summary>
  public void Dig(Vector3 globalPos, float radius, float strength = 1f) => Edit(globalPos, radius, strength, false);

  /// <summary>A dig that shows up without the dig animation (for AI mining: frequent, nobody watching closely).</summary>
  public void DigInstant(Vector3 globalPos, float radius, float strength = 1f) => Edit(globalPos, radius, strength, false, true);

  /// <summary>
  /// Fills a sphere at a global position: the mirror of Dig, adding material with the same falloff, so holding it
  /// grows terrain out to the full radius. Goes through the same batching, merging and animation as digging.
  /// </summary>
  public void Fill(Vector3 globalPos, float radius, float strength = 1f) => Edit(globalPos, radius, strength, true);

  /// <summary>
  /// Fill, but never so much that terrain would close in on a keep-clear sphere (the player): the strength is cut
  /// to the most that leaves every sampled point of that sphere below solid. Returns the strength applied (0 = the
  /// fill was skipped because it would bury the sphere).
  /// </summary>
  public float FillKeepingClear(Vector3 globalPos, float radius, float strength, Vector3 globalKeepClear, float clearance)
  {
    float s = SafeFillStrength(ToLocal(globalPos), radius, strength, ToLocal(globalKeepClear), clearance);
    if (s > 1e-4f) Edit(globalPos, radius, s, true);
    return s;
  }

  /// <summary>
  /// The terrain's density field (noise plus edits) for queries outside the chunk meshes (e.g. ship placement).
  /// Positions are global. Prepare it for a region once per step so the edits list is fetched only once.
  /// </summary>
  public sealed class TerrainField
  {
    private readonly TerrainGenerator _gen;
    internal readonly List<SphereEdit> Edits = [];
    internal Transform3D ToLocal = Transform3D.Identity;

    internal TerrainField(TerrainGenerator gen) => _gen = gen;

    public float Density(Vector3 globalPos) => _gen.Density(ToLocal * globalPos, Edits);
  }

  private TerrainField? _field;

  /// <summary>Field covering globalBox (edits outside it are ignored), or null before the terrain is set up.</summary>
  public TerrainField? PrepareField(Aabb globalBox)
  {
    if (_gen == null) return null;
    _field ??= new TerrainField(_gen);
    _field.ToLocal = GlobalTransform.AffineInverse();
    _edits.Query((_field.ToLocal * globalBox).Grow(1f), -1, _field.Edits);
    return _field;
  }

  /// <summary>Terrain density at a global position (>= Iso is solid).</summary>
  public float DensityAt(Vector3 globalPos)
  {
    var f = PrepareField(new Aabb(globalPos, Vector3.Zero));
    return f?.Density(globalPos) ?? 0f;
  }

  private readonly List<SphereEdit> _clearEdits = [];
  private Vector3[]? _clearDirs;

  /// <summary>
  /// Largest fill strength (up to wanted) that keeps the density below solid (with a small margin) at the keep-clear
  /// centre, 26 points around its surface and the surface point facing the fill. A build makes
  /// d' = 1 - (1 - d)(1 - s(1 - t)), so per point the limit is s &lt;= (1 - (1 - L) / (1 - d)) / (1 - t).
  /// Density is the live field (noise plus every edit, including ones not on screen yet).
  /// </summary>
  private float SafeFillStrength(Vector3 center, float radius, float wanted, Vector3 keep, float clearance)
  {
    if (_gen == null) return 0f;
    const float limit = VoxelConst.Iso - 0.05f;
    if (_clearDirs == null)
    {
      var dirs = new List<Vector3> { Vector3.Zero };
      for (int z = -1; z <= 1; z++)
        for (int y = -1; y <= 1; y++)
          for (int x = -1; x <= 1; x++)
          {
            if (x != 0 || y != 0 || z != 0) dirs.Add(new Vector3(x, y, z).Normalized());
          }
      _clearDirs = dirs.ToArray();
    }
    _edits.Query(new Aabb(keep - Vector3.One * clearance, Vector3.One * (2 * clearance)).Grow(1f), -1, _clearEdits);

    float s = wanted;
    bool Limit(Vector3 q)
    {
      float t = q.DistanceTo(center) / radius;
      if (t >= 1f) return true; // outside the fill
      float d = _gen.Density(q, _clearEdits);
      if (d >= limit)
      {
        s = 0f; // already solid there: don't add to it
        return false;
      }
      float allowed = (1f - (1f - limit) / (1f - d)) / Math.Max(1f - t, 1e-6f);
      s = Math.Min(s, Math.Max(0f, allowed));
      return true;
    }

    foreach (var dir in _clearDirs)
    {
      if (!Limit(keep + dir * clearance)) return 0f;
    }
    var toward = center - keep;
    if (toward.LengthSquared() > 1e-8f && !Limit(keep + toward.Normalized() * clearance)) return 0f;
    return Math.Clamp(s, 0f, wanted);
  }

  private void Edit(Vector3 globalPos, float radius, float strength, bool build, bool instant = false)
  {
    if (_scheduler == null) return;
    long digStart = Stopwatch.GetTimestamp();
    var p = ToLocal(globalPos);
    SphereEdit merged = default;
    int seq = BatchEdits && MergeEditShift > 0
      ? _edits.TryMergeLast(p, radius, strength, _handedOutSeq, MergeEditShift * radius, out merged, build, instant)
      : -1;
    if (seq >= 0)
    {
      // The merged sphere sits between the old centre (its chunks are already queued) and p.
      EditsMerged++;
      MarkTouched(merged.Center, radius, seq);
    }
    else
    {
      seq = _edits.Add(p, radius, strength, build, instant);
    }
    MarkTouched(p, radius, seq);
    _digMsAccum += Stopwatch.GetElapsedTime(digStart).TotalMilliseconds;
  }

  private void MarkTouched(Vector3 p, float radius, int seq)
  {
    for (int lod = 0; lod <= MaxLod; lod++)
    {
      float margin = radius + (1 << lod);
      var lo = ChunkKey.Containing(p - Vector3.One * margin, lod).Coord;
      var hi = ChunkKey.Containing(p + Vector3.One * margin, lod).Coord;
      for (int z = lo.Z; z <= hi.Z; z++)
        for (int y = lo.Y; y <= hi.Y; y++)
          for (int x = lo.X; x <= hi.X; x++)
          {
            var key = new ChunkKey(lod, new Vector3I(x, y, z));
            if (!VoxelMath.SphereIntersectsAabb(p, radius, key.PaddedBounds)) continue;
            // Retiring chunks are still on screen and may be revived, so they take the edit too.
            if (_active.TryGetValue(key, out var c)) MarkEdited(c, seq);
            if (_retiring.TryGetValue(key, out var r)) MarkEdited(r, seq);
          }
    }
  }

  private void MarkEdited(Chunk c, int seq)
  {
    c.EditedAt = Time.GetTicksMsec() / 1000.0;
    c.WantCache = true;
    if (BatchEdits)
    {
      _nextBatch.Add(c);
      return;
    }
    c.TargetSeq = seq;
    _handedOutSeq = Math.Max(_handedOutSeq, seq);
    c.Version++;
    _scheduler!.Request(c, -1_000_000f + PriorityOf(c));
  }

  // ---------------------------------------------------------------- edit batches

  /// <summary>Starts building the queued edited chunks against the newest edit snapshot (one batch at a time).</summary>
  private void LaunchBatch()
  {
    if (_batch != null || _nextBatch.Count == 0) return;
    var b = new EditBatch { Target = _edits.NewestSeq, StartTicks = Stopwatch.GetTimestamp() };
    _handedOutSeq = Math.Max(_handedOutSeq, b.Target);
    foreach (var c in _nextBatch)
    {
      if (c.Removed) continue;
      // TargetSeq before Version: a worker that sees the new Version must also see the new snapshot.
      c.TargetSeq = Math.Max(c.TargetSeq, b.Target);
      c.Version++;
      b.Members.Add(c);
      _scheduler!.Request(c, -1_000_000f + PriorityOf(c));
    }
    _nextBatch.Clear();
    if (b.Members.Count > 0) _batch = b;
  }

  /// <summary>
  /// A batch whose members are all built is finished: it joins the ready queue and the next batch starts building
  /// right away. Ready batches are shown in order, each once the previous one's animation has played out, so the
  /// surface moves continuously without skipping. Animation length follows how often batches finish, which doesn't
  /// depend on the showing, so there is no feedback loop.
  /// </summary>
  private void UpdateBatches()
  {
    var b = _batch;
    if (b != null)
    {
      bool complete = true;
      foreach (var c in b.Members)
      {
        if (!c.Removed && !b.Held.ContainsKey(c))
        {
          complete = false;
          break;
        }
      }
      double ageMs = Stopwatch.GetElapsedTime(b.StartTicks).TotalMilliseconds;
      if (complete || ageMs >= MaxBatchWaitMs)
      {
        double now = Time.GetTicksUsec() / 1e6;
        double gap = now - _lastCompleteTime;
        _lastCompleteTime = now;
        if (gap > 2 * Math.Max(MaxMorphSeconds, MorphSeconds))
        {
          _gapEma = -1; // a fresh edit after a pause
          b.MorphDuration = MorphSeconds;
        }
        else
        {
          _gapEma = _gapEma < 0 ? gap : _gapEma * 0.5 + gap * 0.5;
          b.MorphDuration = MaxMorphSeconds > 0 ? Math.Clamp(_gapEma * 0.95, 0.01, MaxMorphSeconds) : MorphSeconds;
        }
        b.CompletedAt = now;
        b.BuildMs = ageMs;
        // Builds from here on morph from this snapshot, which will be on screen before they are.
        foreach (var (c, held) in b.Held) c.BaseSeq = Math.Max(c.BaseSeq, held.Result.Job.TargetSeq);
        _readyBatches.Enqueue(b);
        _batch = null;
        LaunchBatch();
      }
    }

    while (_readyBatches.Count > 0)
    {
      double now = Time.GetTicksUsec() / 1e6;
      var r = _readyBatches.Peek();
      // Fall behind by more than a couple of batches (or wait too long) and show it anyway.
      bool behind = _readyBatches.Count > 3 || (now - r.CompletedAt) * 1000 > MaxBatchWaitMs;
      if (now < _morphEndTime && !behind) break;
      _readyBatches.Dequeue();
      CommitBatch(r);
    }
  }

  /// <summary>Puts a finished batch on screen in one frame (meshes were built as results arrived: pointer swaps).</summary>
  private void CommitBatch(EditBatch b)
  {
    var sw = Stopwatch.StartNew();
    _frameMorph = null;
    _morphDuration = b.MorphDuration;
    foreach (var (c, held) in b.Held)
    {
      if (c.Removed) continue;
      ApplyMesh(c, held.Result, held.Mesh);
      QueueCollision(c, held.Result);
      c.Ready = true;
      c.ShownSeq = held.Result.Job.TargetSeq;
    }
    if (_frameMorph != null) _morphEndTime = _frameMorph.Start + _frameMorph.Duration;
    _frameMorph = null;
    _morphDuration = MorphSeconds;
    _frameMeshMs += sw.Elapsed.TotalMilliseconds;
    _committedSeq = Math.Max(_committedSeq, b.Target);
    LastBatchSize = b.Members.Count;
    System.Array.Clear(LastBatchByLod);
    foreach (var m in b.Members) LastBatchByLod[Math.Min(m.Key.Lod, LastBatchByLod.Length - 1)]++;
    LastBatchMs = b.BuildMs;
    BatchesCommitted++;
  }

  /// <summary>If c has a result waiting in a ready batch, replace it (keeps that batch's commit atomic).</summary>
  private bool ReplaceReadyResult(Chunk c, MeshResult r, ArrayMesh? mesh)
  {
    foreach (var b in _readyBatches)
    {
      if (b.Held.ContainsKey(c) && b.Held[c].Result.Job.Version == r.Job.Version)
      {
        b.Held[c] = (r, mesh);
        return true;
      }
    }
    return false;
  }

  // ---------------------------------------------------------------- morphing

  private MorphGroup FrameMorph()
  {
    if (_frameMorph == null)
    {
      _frameMorph = new MorphGroup { Start = Time.GetTicksUsec() / 1e6, Duration = _morphDuration };
      LastMorphSeconds = _morphDuration;
      _morphGroups.Add(_frameMorph);
    }
    return _frameMorph;
  }

  private ShaderMaterial MorphMaterial(MorphGroup g, int mask)
  {
    mask = Math.Clamp(mask, 0, 63);
    if (!g.Materials.TryGetValue(mask, out var m))
    {
      if (_morphPool.Count > 0)
      {
        m = _morphPool.Pop();
      }
      else
      {
        m = new ShaderMaterial { Shader = _morphShader };
        CopyStyle(m);
      }
      m.SetShaderParameter("lod_mask", mask);
      m.SetShaderParameter("morph_t", 0f);
      g.Materials[mask] = m;
    }
    return m;
  }

  private void StartMorph(Chunk c)
  {
    var g = FrameMorph();
    _morphStarts++;
    if (c.Morph != null && c.Morph != g && c.Morph.Duration > 0)
    {
      double t = (Time.GetTicksUsec() / 1e6 - c.Morph.Start) / c.Morph.Duration;
      _morphJumpSum += Math.Clamp(1 - t, 0, 1);
    }
    if (c.Morph == null) _morphingChunks++;
    c.Morph = g;
    g.Chunks.Add(c);
    c.MeshInstance!.MaterialOverride = MorphMaterial(g, c.LodMask);
  }

  private void EndMorph(Chunk c)
  {
    if (c.Morph == null) return;
    c.Morph = null;
    _morphingChunks--;
    if (c.MeshInstance != null) c.MeshInstance.MaterialOverride = MaterialFor(c.LodMask);
  }

  private void UpdateMorphs()
  {
    if (_morphGroups.Count == 0) return;
    double now = Time.GetTicksUsec() / 1e6;
    for (int i = _morphGroups.Count - 1; i >= 0; i--)
    {
      var g = _morphGroups[i];
      float t = MorphSeconds <= 0 || g.Duration <= 0 ? 1f : (float)Math.Clamp((now - g.Start) / g.Duration, 0, 1);
      if (t < 1f)
      {
        foreach (var m in g.Materials.Values) m.SetShaderParameter("morph_t", t);
        continue;
      }
      foreach (var c in g.Chunks)
      {
        if (c.Morph == g) EndMorph(c);
      }
      foreach (var m in g.Materials.Values) _morphPool.Push(m);
      _morphGroups.RemoveAt(i);
    }
  }

  private readonly List<SphereEdit> _rayEdits = [];

  /// <summary>
  /// Raymarches the density field itself (noise + edits), so it hits terrain at any distance and LOD without
  /// needing collision shapes there. Returns the first solid point along the ray in global space.
  /// </summary>
  public bool RaycastTerrain(Vector3 globalFrom, Vector3 globalTo, out Vector3 globalHit)
  {
    long start = Stopwatch.GetTimestamp();
    try
    {
      return RaycastTerrainCore(globalFrom, globalTo, out globalHit);
    }
    finally
    {
      _digMsAccum += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
  }

  private bool RaycastTerrainCore(Vector3 globalFrom, Vector3 globalTo, out Vector3 globalHit)
  {
    globalHit = default;
    if (!MarchSolid(ToLocal(globalFrom), ToLocal(globalTo), out var hit, out _)) return false;
    globalHit = ToGlobal(hit);
    return true;
  }

  /// <summary>First solid point of the density field (noise + edits) along a local-space segment.</summary>
  private bool MarchSolid(Vector3 from, Vector3 to, out Vector3 hit, out float hitT)
  {
    hit = default;
    hitT = 0;
    if (_gen == null) return false;
    float length = from.DistanceTo(to);
    if (length < 1e-4f) return false;
    var dir = (to - from) / length;

    const float step = 0.5f;
    const float segment = 16f;
    float prevT = 0;
    for (float segStart = 0; segStart < length; segStart += segment)
    {
      float segEnd = Math.Min(length, segStart + segment);
      var a = from + dir * segStart;
      var b = from + dir * segEnd;
      var box = new Aabb(a, Vector3.Zero).Expand(b).Grow(1f);
      _edits.Query(box, -1, _rayEdits);
      // Keep only edits that reach the segment itself (the box query also returns ones off to the side); with
      // nothing but digs, samples that are air in the raw noise stay air, so they skip the edits altogether.
      bool builds = false;
      int w = 0;
      for (int i = 0; i < _rayEdits.Count; i++)
      {
        var e = _rayEdits[i];
        if (VoxelMath.DistanceToSegment(e.Center, a, b) > e.Radius + step) continue;
        builds |= e.Build;
        _rayEdits[w++] = e;
      }
      _rayEdits.RemoveRange(w, _rayEdits.Count - w);

      for (float t = segStart; t <= segEnd; t += step)
      {
        var q = from + dir * t;
        float raw = _gen.RawDensity(q.X, q.Y, q.Z);
        bool air = raw < VoxelConst.Iso && !builds;
        if (!air)
        {
          float d = raw;
          foreach (var e in _rayEdits) d = e.Apply(q, d);
          air = d < VoxelConst.Iso;
        }
        if (air)
        {
          prevT = t;
          continue;
        }
        if (t == 0)
        {
          hit = from; // starting inside terrain
          return true;
        }
        // Refine between the last air sample and this solid one.
        float lo = prevT, hi = t;
        for (int i = 0; i < 8; i++)
        {
          float mid = (lo + hi) * 0.5f;
          if (_gen.Density(from + dir * mid, _rayEdits) < VoxelConst.Iso) lo = mid; else hi = mid;
        }
        hit = from + dir * hi;
        hitT = hi;
        return true;
      }
    }
    return false;
  }

  /// <summary>
  /// Sweeps a sphere of this radius along the ray and returns the first terrain it touches (the terrain point,
  /// not the sphere centre), so anything poking into the tube around the line of sight gets targeted, not only
  /// what is exactly under the crosshair. Terrain already inside the sphere at the start of the sweep is ignored,
  /// so the ground you're standing on doesn't capture it. The exact ray hit is always a candidate; other contacts
  /// come from points on the shown meshes. Radius 0 = RaycastTerrain.
  /// rampDistance &gt; 0 narrows the start into a cone: the swept radius grows from 0 at the ray origin to `radius` at
  /// that distance, so walls right beside the camera (say, inside a tunnel you dug) don't capture the aim.
  /// </summary>
  public bool SphereCastTerrain(Vector3 globalFrom, Vector3 globalTo, float radius, out Vector3 globalHit, float rampDistance = 0f)
  {
    long start = Stopwatch.GetTimestamp();
    try
    {
      return SphereCastCore(globalFrom, globalTo, radius, rampDistance, out globalHit);
    }
    finally
    {
      _digMsAccum += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
  }

  private readonly List<(float Enter, Chunk Chunk)> _castChunks = [];
  public long CastChunksScanned, CastPointsScanned;

  /// <summary>Swept radius at distance `along` down the ray.</summary>
  private static float CastRadius(float radius, float ramp, float along) =>
    ramp > 0 ? radius * Math.Clamp(along / ramp, 0f, 1f) : radius;

  private bool SphereCastCore(Vector3 globalFrom, Vector3 globalTo, float radius, float ramp, out Vector3 globalHit)
  {
    globalHit = default;
    var from = ToLocal(globalFrom);
    var to = ToLocal(globalTo);
    float length = from.DistanceTo(to);
    if (length < 1e-4f) return false;
    var dir = (to - from) / length;
    if (radius <= 0f)
    {
      bool hit = MarchSolid(from, to, out var p, out _);
      if (hit) globalHit = ToGlobal(p);
      return hit;
    }

    // 1. Points on the shown meshes, nearest chunks first, stopping once no chunk can beat the best contact.
    float bestT = float.PositiveInfinity;
    var bestPoint = Vector3.Zero;
    _castChunks.Clear();
    void Collect(Chunk c)
    {
      if (c.SurfacePoints == null || c.MeshInstance == null || !c.MeshInstance.Visible) return;
      // A contact inside this chunk needs the ray within `radius` of it, i.e. inside the grown box; the sweep
      // reaches it no earlier than (entry into that box) - radius.
      if (!RayBox(from, dir, c.Key.Bounds.Grow(radius), out float enter, out float exit)) return;
      if (exit < 0 || enter > length) return;
      _castChunks.Add((enter, c));
    }
    foreach (var c in _active.Values) Collect(c);
    foreach (var c in _retiring.Values) Collect(c);
    _castChunks.Sort(static (a, b) => a.Enter.CompareTo(b.Enter));

    foreach (var (enter, c) in _castChunks)
    {
      if (enter - radius >= bestT) break;
      CastChunksScanned++;
      CastPointsScanned += c.SurfacePoints!.Length;
      ScanPoints(c.SurfacePoints!, (Vector3)c.Key.Origin, c.Key.Step, from, dir, length, radius, ramp, ref bestT, ref bestPoint);
    }

    // 2. The exact ray hit (density field, includes edits not shown yet) wins if the sphere reaches it first; it
    // only matters up to bestT + radius, so the march stops there.
    float marchTo = Math.Min(length, bestT + radius);
    if (MarchSolid(from, from + dir * marchTo, out var rayHit, out float rayT) && rayT - CastRadius(radius, ramp, rayT) < bestT)
    {
      bestT = rayT - CastRadius(radius, ramp, rayT);
      bestPoint = rayHit;
    }
    if (float.IsPositiveInfinity(bestT)) return false;
    globalHit = ToGlobal(bestPoint);
    return true;
  }

  /// <summary>Earliest sweep contact among one chunk's packed surface points (hot loop: plain floats, no closures).</summary>
  [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
  private static void ScanPoints(uint[] points, Vector3 origin, int step, Vector3 from, Vector3 dir, float length, float radius,
                                 float ramp, ref float bestT, ref Vector3 bestPoint)
  {
    // Work relative to the ray origin in units of 1/32 cell: point = base + (cell * 32 + frac + 0.5) * scale.
    float scale = step / 32f;
    float bx = origin.X - from.X + 0.5f * scale, by = origin.Y - from.Y + 0.5f * scale, bz = origin.Z - from.Z + 0.5f * scale;
    float dx = dir.X, dy = dir.Y, dz = dir.Z;
    float best = bestT;
    int bestIdx = -1;
    float maxR2 = radius * radius;
    float rampScale = ramp > 0 ? radius / ramp : 0;
    for (int i = 0; i < points.Length; i++)
    {
      uint p = points[i];
      int idx = (int)(p & 0x7FFF);
      float vx = bx + ((idx & 31) * 32 + (int)((p >> 15) & 31)) * scale;
      float vy = by + (((idx >> 5) & 31) * 32 + (int)((p >> 20) & 31)) * scale;
      float vz = bz + ((idx >> 10) * 32 + (int)((p >> 25) & 31)) * scale;
      float proj = vx * dx + vy * dy + vz * dz;
      if (proj < 0 || proj > length) continue;
      float perp2 = vx * vx + vy * vy + vz * vz - proj * proj;
      if (perp2 > maxR2) continue;
      float r2 = maxR2;
      if (ramp > 0 && proj < ramp)
      {
        float r = proj * rampScale;
        r2 = r * r;
        if (perp2 > r2) continue;
      }
      float t = proj - MathF.Sqrt(r2 - perp2);
      if (t < 0 || t >= best) continue;
      best = t;
      bestIdx = i;
    }
    if (bestIdx >= 0)
    {
      bestT = best;
      bestPoint = origin + ChunkBuilder.SurfacePoint(points[bestIdx], step);
    }
  }

  private static bool RayBox(Vector3 o, Vector3 d, Aabb box, out float enter, out float exit)
  {
    enter = float.NegativeInfinity;
    exit = float.PositiveInfinity;
    var min = box.Position;
    var max = box.End;
    for (int a = 0; a < 3; a++)
    {
      if (MathF.Abs(d[a]) < 1e-9f)
      {
        if (o[a] < min[a] || o[a] > max[a]) return false;
        continue;
      }
      float inv = 1f / d[a];
      float t0 = (min[a] - o[a]) * inv, t1 = (max[a] - o[a]) * inv;
      if (t0 > t1) (t0, t1) = (t1, t0);
      enter = Math.Max(enter, t0);
      exit = Math.Min(exit, t1);
      if (enter > exit) return false;
    }
    return true;
  }

  // ---------------------------------------------------------------- octree diff

  private void ApplyOctree(OctreeResult r)
  {
    _octreeMs = r.Milliseconds;

    // Keep, revive or create every wanted leaf.
    foreach (var key in r.Leaves)
    {
      if (_active.ContainsKey(key)) continue;
      if (_retiring.Remove(key, out var revived))
      {
        _active[key] = revived;
        continue;
      }
      var c = new Chunk(key) { TargetSeq = _committedSeq };
      _active[key] = c;
      _pendingShow.Add(c);
      // Edits newer than the committed snapshot that touch this chunk go out with the next batch.
      _edits.Query(key.PaddedBounds, _committedSeq, _probeEdits);
      if (_probeEdits.Count > 0) _nextBatch.Add(c);
    }

    // Chunks no longer wanted: visible ones wait for their replacements, the rest go now.
    _scratch.Clear();
    foreach (var (key, c) in _active)
    {
      if (!r.Leaves.Contains(key)) _scratch.Add(c);
    }
    foreach (var c in _scratch)
    {
      _active.Remove(c.Key);
      _pendingShow.Remove(c);
      if (c.Visible) _retiring[c.Key] = c;
      else Destroy(c);
    }

    // Seam masks, collision, cache flags and (re)queueing.
    foreach (var (key, c) in _active)
    {
      int mask = key.Lod > 0 && r.Masks.TryGetValue(key, out var m) ? m : 0;
      if (mask != c.LodMask)
      {
        c.LodMask = mask;
        if (c.MeshInstance != null) c.MeshInstance.MaterialOverride = c.Morph != null ? MorphMaterial(c.Morph, mask) : MaterialFor(mask);
      }

      bool cacheIt = (key.Lod == 0 && Chebyshev(key.Coord, _viewerCell) <= CollisionRadius + 1)
                     || Time.GetTicksMsec() / 1000.0 - c.EditedAt < EditCacheSeconds;
      if (c.WantCache && !cacheIt) _cache!.Drop(key);
      c.WantCache = cacheIt;
      // Collision wants (player area + requested boxes) are set by UpdateCollisionWants, every frame.
      c.WantCollision = WantsCollision(key);

      if (!c.Ready)
      {
        _scheduler!.Request(c, PriorityOf(c));
      }
    }

    // The active set changed, so every retiring chunk's replacements may have too.
    foreach (var c in _retiring.Values) CollectReplacements(c);
    _retiringCoverDirty = true;

    _scheduler!.Reprioritize(PriorityOf);
  }

  private float PriorityOf(Chunk c) => VoxelMath.DistanceToAabb(_viewer, c.Key.Bounds);

  private static int Chebyshev(Vector3I a, Vector3I b)
  {
    var d = (a - b).Abs();
    return Math.Max(d.X, Math.Max(d.Y, d.Z));
  }

  // ---------------------------------------------------------------- results

  private void ApplyResults()
  {
    var sw = Stopwatch.StartNew();
    _maxItemMs = 0;
    double itemStart = 0;
    while (sw.Elapsed.TotalMilliseconds < ApplyBudgetMs && _scheduler!.Results.TryDequeue(out var r))
    {
      _maxItemMs = Math.Max(_maxItemMs, sw.Elapsed.TotalMilliseconds - itemStart);
      itemStart = sw.Elapsed.TotalMilliseconds;
      var c = r.Chunk;
      _avgGenMs = _avgGenMs * 0.95 + r.GenerateMs * 0.05;
      _avgMeshMs = _avgMeshMs * 0.95 + r.MeshMs * 0.05;

      if (c.Removed)
      {
        _scheduler.Complete(c, false);
        continue;
      }
      if (r.Job.Version != c.Version)
      {
        _scheduler.Complete(c, true);
        continue;
      }

      bool needCollision = c.WantCollision && r.Arrays != null && r.CollisionFaces == null;
      double t0 = sw.Elapsed.TotalMilliseconds;
      var mesh = BuildMesh(r);
      if (_batch != null && _batch.Members.Contains(c) && r.Job.TargetSeq == _batch.Target)
      {
        // Part of an edit batch: keep it until the whole batch is built.
        _batch.Held[c] = (r, mesh);
      }
      else if (!ReplaceReadyResult(c, r, mesh))
      {
        ApplyMesh(c, r, mesh);
        QueueCollision(c, r);
        c.Ready = true;
        c.ShownSeq = r.Job.TargetSeq;
        c.BaseSeq = Math.Max(c.BaseSeq, r.Job.TargetSeq);
      }
      _frameMeshMs += sw.Elapsed.TotalMilliseconds - t0;
      _scheduler.Complete(c, needCollision);
    }
    _maxItemMs = Math.Max(_maxItemMs, sw.Elapsed.TotalMilliseconds - itemStart);

    UpdateBatches();

    // Collision shapes are the expensive part (Jolt builds them on the main thread), so they trail the visible
    // mesh within what's left of the budget, at least one per frame.
    double c0 = sw.Elapsed.TotalMilliseconds;
    double now = Time.GetTicksMsec() / 1000.0;
    for (int i = _collisionLater.Count - 1; i >= 0; i--)
    {
      var c = _collisionLater[i];
      if (!c.Removed && now - c.CollisionBuiltAt < CollisionRefreshSeconds) continue;
      _collisionLater.RemoveAt(i);
      _collisionQueue.Enqueue(c);
    }
    bool first = true;
    while (_collisionQueue.Count > 0 && (first || sw.Elapsed.TotalMilliseconds < ApplyBudgetMs))
    {
      var c = _collisionQueue.Dequeue();
      c.CollisionQueued = false;
      if (c.Removed || !c.WantCollision || c.PendingCollision is not Variant faces) continue;
      if (c.HasCollision && now - c.CollisionBuiltAt < CollisionRefreshSeconds && !PlayerCollisionArea(c.Key))
      {
        // Rebuilt recently and away from the player: the latest faces wait (newer results replace them).
        c.CollisionQueued = true;
        _collisionLater.Add(c);
        continue;
      }
      c.PendingCollision = null;
      SetCollision(c, faces);
      c.CollisionBuiltAt = now;
      first = false;
    }
    _frameCollisionMs += sw.Elapsed.TotalMilliseconds - c0;
    _applyMs = sw.Elapsed.TotalMilliseconds;
  }

  private static ArrayMesh? BuildMesh(MeshResult r)
  {
    if (r.Arrays == null) return null;
    var mesh = new ArrayMesh();
    mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, r.Arrays, null, null, MeshBuffers.SurfaceFormat(r.HasMorph));
    return mesh;
  }

  private void QueueCollision(Chunk c, MeshResult r)
  {
    // Built for collision but nothing to collide with (only seam triangles): settled without a shape.
    c.NoCollisionFaces = r.Job.Collision && r.Arrays != null && r.CollisionFaces == null;
    if (c.NoCollisionFaces)
    {
      c.CollisionRequested = false;
      c.PendingCollision = null;
      RemoveCollision(c);
    }
    else if (r.CollisionFaces is Variant faces && c.WantCollision)
    {
      c.PendingCollision = faces;
      if (!c.CollisionQueued)
      {
        c.CollisionQueued = true;
        _collisionQueue.Enqueue(c);
      }
    }
    else if (r.Arrays == null || !r.Job.Collision)
    {
      c.PendingCollision = null;
      RemoveCollision(c);
    }
  }

  private void ApplyMesh(Chunk c, MeshResult r, ArrayMesh? mesh)
  {
    if (c.MeshInstance != null) _vertexCount -= c.MeshInstance.Mesh is ArrayMesh old ? old.SurfaceGetArrayLen(0) : 0;

    if (mesh == null)
    {
      c.SurfacePoints = null;
      if (c.MeshInstance != null)
      {
        EndMorph(c);
        if (c.MeshInstance.Visible) _visibleCount--;
        c.MeshInstance.QueueFree();
        c.MeshInstance = null;
      }
      return;
    }

    _vertexCount += r.VertexCount;
    bool hadMesh = c.MeshInstance != null;
    if (c.MeshInstance == null)
    {
      c.MeshInstance = new MeshInstance3D
      {
        Mesh = mesh,
        MaterialOverride = MaterialFor(c.LodMask),
        Position = c.Key.Origin,
        Visible = c.Visible,
      };
      AddChild(c.MeshInstance);
      if (c.Visible) _visibleCount++;
    }
    else
    {
      c.MeshInstance.Mesh = mesh;
    }

    c.SurfacePoints = r.SurfacePoints;
    MeshApplied?.Invoke(c, r);
    // Morph meshes need the morph shader (it reads CUSTOM1); static meshes must not use it.
    if (r.HasMorph && hadMesh && MorphSeconds > 0) StartMorph(c);
    else EndMorph(c);
  }

  private void SetCollision(Chunk c, Variant faces)
  {
    if (c.Body == null)
    {
      c.Body = new StaticBody3D { Position = c.Key.Origin };
      c.Body.AddToGroup(Ships.ShipBuilder.TerrainGroup);
      c.CollisionShape = new CollisionShape3D();
      c.Body.AddChild(c.CollisionShape);
      AddChild(c.Body);
    }
    // Backface collision: collide from either side regardless of triangle winding (bodies standing on the terrain
    // must never drop through it).
    var shape = new ConcavePolygonShape3D { BackfaceCollision = true };
    shape.Set(ConcavePolygonShape3D.PropertyName.Data, faces);
    c.CollisionShape!.Shape = shape;
    if (!c.HasCollision) CollisionChunks++;
    c.HasCollision = true;
    c.CollisionRequested = false;
  }

  private void RemoveCollision(Chunk c)
  {
    if (c.HasCollision) CollisionChunks--;
    c.Body?.QueueFree();
    c.Body = null;
    c.CollisionShape = null;
    c.HasCollision = false;
  }

  // ---------------------------------------------------------------- hole-free swaps

  /// <summary>
  /// Main-thread time of the last frame's swap bookkeeping (deciding what to retire and show), excluding the engine
  /// calls that free and show nodes.
  /// </summary>
  public double LastSwapCheckMs { get; private set; }

  private void UpdateSwaps()
  {
    long checkStart = Stopwatch.GetTimestamp();
    double checkMs = 0;
    if (_retiring.Count > 0)
    {
      _scratch.Clear();
      foreach (var c in _retiring.Values)
      {
        var list = c.Replacements;
        if (list == null)
        {
          CollectReplacements(c);
          list = c.Replacements!;
        }
        while (c.ReplacementsReadyCount < list.Count && list[c.ReplacementsReadyCount].Ready) c.ReplacementsReadyCount++;
        if (c.ReplacementsReadyCount == list.Count) _scratch.Add(c);
      }
      checkMs += Stopwatch.GetElapsedTime(checkStart).TotalMilliseconds;
      foreach (var c in _scratch)
      {
        _retiring.Remove(c.Key);
        Destroy(c);
        _retiringCoverDirty = true;
      }
    }

    if (_pendingShow.Count > 0)
    {
      checkStart = Stopwatch.GetTimestamp();
      // Every retiring key plus all its ancestors, so each overlap test is a few hash lookups instead of a scan of
      // every retiring chunk (hundreds x hundreds during fast flight). Rebuilt only when the retiring set changes.
      if (_retiringCoverDirty)
      {
        _retiringCoverDirty = false;
        _retiringCover.Clear();
        foreach (var r in _retiring.Keys)
        {
          for (var k = r; k.Lod <= MaxLod && _retiringCover.Add(k); k = k.Parent) { }
        }
      }
      _scratch.Clear();
      foreach (var c in _pendingShow)
      {
        if (c.Ready && !OverlapsRetiring(c.Key)) _scratch.Add(c);
      }
      checkMs += Stopwatch.GetElapsedTime(checkStart).TotalMilliseconds;
      foreach (var c in _scratch)
      {
        _pendingShow.Remove(c);
        c.Visible = true;
        if (c.MeshInstance != null)
        {
          c.MeshInstance.Visible = true;
          _visibleCount++;
        }
      }
    }
    LastSwapCheckMs = checkMs;
  }

  /// <summary>
  /// Records the active chunks overlapping c's region: the active ancestor if there is one, otherwise every active
  /// descendant. Regions no active chunk covers need nothing. Not-yet-ready chunks go first, so the per-frame check
  /// usually stops at the first entry until the swap is close.
  /// </summary>
  private void CollectReplacements(Chunk c)
  {
    var list = c.Replacements ?? [];
    list.Clear();
    c.ReplacementsReadyCount = 0;
    c.Replacements = list;
    for (var k = c.Key; k.Lod <= MaxLod; k = k.Parent)
    {
      if (_active.TryGetValue(k, out var a))
      {
        list.Add(a);
        return;
      }
    }
    CollectDescendants(c.Key, list);
    // Stable partition: not-ready first.
    int w = 0;
    for (int i = 0; i < list.Count; i++)
    {
      if (!list[i].Ready) (list[w], list[i]) = (list[i], list[w++]);
    }
  }

  private void CollectDescendants(ChunkKey key, List<Chunk> list)
  {
    if (key.Lod == 0) return;
    for (int i = 0; i < 8; i++)
    {
      var ck = key.Child(i);
      if (_active.TryGetValue(ck, out var a)) list.Add(a);
      else CollectDescendants(ck, list);
    }
  }

  /// <summary>The original recursive test: true when every active chunk overlapping key's region has its mesh.</summary>
  private bool ReplacementsReady(ChunkKey key)
  {
    for (var k = key; k.Lod <= MaxLod; k = k.Parent)
    {
      if (_active.TryGetValue(k, out var a)) return a.Ready;
    }
    return DescendantsReady(key);
  }

  private bool DescendantsReady(ChunkKey key)
  {
    if (key.Lod == 0) return true;
    for (int i = 0; i < 8; i++)
    {
      var ck = key.Child(i);
      if (_active.TryGetValue(ck, out var a))
      {
        if (!a.Ready) return false;
      }
      else if (!DescendantsReady(ck))
      {
        return false;
      }
    }
    return true;
  }

  /// <summary>
  /// Test hook: checks the incremental swap bookkeeping against the recursive definition. Returns how many retiring
  /// chunks disagree (a chunk whose replacements are all ready may legitimately be found one frame late, so only
  /// "incremental says ready, recursive says not" counts: that would open a hole).
  /// </summary>
  public int SwapBookkeepingErrors()
  {
    int bad = 0;
    foreach (var c in _retiring.Values)
    {
      if (c.Replacements == null) continue;
      bool incremental = c.Replacements.TrueForAll(r => r.Ready);
      if (incremental && !ReplacementsReady(c.Key)) bad++;
    }
    return bad;
  }

  private readonly HashSet<ChunkKey> _retiringCover = [];
  private bool _retiringCoverDirty = true;

  /// <summary>True if key contains, or is contained by, a retiring chunk (uses _retiringCover from UpdateSwaps).</summary>
  private bool OverlapsRetiring(ChunkKey key)
  {
    if (_retiring.Count == 0) return false;
    if (_retiringCover.Contains(key)) return true; // key is a retiring chunk or one of their ancestors
    for (var k = key.Parent; k.Lod <= MaxLod; k = k.Parent)
    {
      if (_retiring.ContainsKey(k)) return true; // a retiring chunk contains key
    }
    return false;
  }

  private void Destroy(Chunk c)
  {
    c.Removed = true;
    c.PendingCollision = null;
    if (c.MeshInstance != null)
    {
      EndMorph(c);
      if (c.MeshInstance.Visible) _visibleCount--;
      if (c.MeshInstance.Mesh is ArrayMesh m) _vertexCount -= m.SurfaceGetArrayLen(0);
      c.MeshInstance.QueueFree();
      c.MeshInstance = null;
    }
    RemoveCollision(c);
    if (c.WantCache) _cache!.Drop(c.Key);
  }

  // ---------------------------------------------------------------- monitors

  private void RegisterMonitors()
  {
    void Add(string name, Func<Variant> f)
    {
      if (Performance.HasCustomMonitor(name)) Performance.RemoveCustomMonitor(name);
      Performance.AddCustomMonitor(name, Callable.From(f));
    }
    Add(MonitorNames[0], () => _scheduler?.QueueLength ?? 0);
    Add(MonitorNames[1], () => _scheduler?.Busy ?? 0);
    Add(MonitorNames[2], () => _active.Count);
    Add(MonitorNames[3], () => _visibleCount);
    Add(MonitorNames[4], () => _retiring.Count);
    Add(MonitorNames[5], () => _vertexCount);
    Add(MonitorNames[6], () => _avgGenMs);
    Add(MonitorNames[7], () => _avgMeshMs);
    Add(MonitorNames[8], () => _applyMs);
    Add(MonitorNames[9], () => _octreeMs);
    Add(MonitorNames[10], () => _cache?.Count ?? 0);
    Add(MonitorNames[11], () => _edits.Count);
    Add(MonitorNames[12], () => PendingChunks);
    Add(MonitorNames[13], () => _processMs);
    Add(MonitorNames[14], () => _diffMs);
    Add(MonitorNames[15], () => _swapMs);
    Add(MonitorNames[16], () => _maxItemMs);
  }
}
