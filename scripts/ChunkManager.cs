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
  /// <summary>Main-thread time per frame spent turning finished jobs into meshes and shapes.</summary>
  [Export(PropertyHint.Range, "0.5,16,0.5")] public double ApplyBudgetMs = 3.0;
  /// <summary>0 = automatic (processor count - 2, at most 8), leaving cores for the main and render threads.</summary>
  [Export(PropertyHint.Range, "0,64")] public int WorkerThreads = 0;
  /// <summary>LOD0 volumes near the camera kept in memory (about 0.5 MB each) so digs skip regeneration.</summary>
  [Export(PropertyHint.Range, "0,1024")] public int VolumeCacheSize = 160;

  private readonly EditStore _edits = new();
  private TerrainGenerator? _gen;
  private VolumeCache? _cache;
  private MeshJobScheduler? _scheduler;
  private ShaderMaterial? _material;

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
  public int PendingChunks => (_scheduler?.Results.Count ?? 0) + _pendingShow.Count;
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

    _gen = new TerrainGenerator(Noise, ColorNoise, _edits);
    _cache = new VolumeCache(VolumeCacheSize);
    var builder = new ChunkBuilder(_gen, _cache);
    int workers = WorkerThreads > 0 ? WorkerThreads : Math.Clamp(System.Environment.ProcessorCount - 2, 1, 8);
    _scheduler = new MeshJobScheduler(workers, builder.Build);
    _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/terrain.gdshader") };

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

    ApplyResults();
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
    _digMsAccum = 0;
  }

  /// <summary>Digs a sphere at a global position. Every loaded chunk it touches is rebuilt at top priority.</summary>
  public void Dig(Vector3 globalPos, float radius)
  {
    if (_scheduler == null) return;
    long digStart = Stopwatch.GetTimestamp();
    var p = ToLocal(globalPos);
    _edits.Add(p, radius);

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
            if (!_active.TryGetValue(key, out var c)) continue;
            if (!VoxelMath.SphereIntersectsAabb(p, radius, key.PaddedBounds)) continue;
            c.Version++;
            _scheduler.Request(c, -1_000_000f + VoxelMath.DistanceToAabb(p, key.Bounds));
          }
    }
    _digMsAccum += Stopwatch.GetElapsedTime(digStart).TotalMilliseconds;
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
    if (_gen == null) return false;
    var from = ToLocal(globalFrom);
    var to = ToLocal(globalTo);
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

      for (float t = segStart; t <= segEnd; t += step)
      {
        if (_gen.Density(from + dir * t, _rayEdits) < VoxelConst.Iso)
        {
          prevT = t;
          continue;
        }
        if (t == 0)
        {
          globalHit = globalFrom; // starting inside terrain
          return true;
        }
        // Refine between the last air sample and this solid one.
        float lo = prevT, hi = t;
        for (int i = 0; i < 8; i++)
        {
          float mid = (lo + hi) * 0.5f;
          if (_gen.Density(from + dir * mid, _rayEdits) < VoxelConst.Iso) lo = mid; else hi = mid;
        }
        globalHit = ToGlobal(from + dir * hi);
        return true;
      }
    }
    return false;
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
      var c = new Chunk(key);
      _active[key] = c;
      _pendingShow.Add(c);
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
        c.MeshInstance?.SetInstanceShaderParameter("lod_mask", mask);
      }

      bool near = key.Lod == 0 && Chebyshev(key.Coord, _viewerCell) <= CollisionRadius;
      bool cacheIt = key.Lod == 0 && Chebyshev(key.Coord, _viewerCell) <= CollisionRadius + 1;
      if (c.WantCache && !cacheIt) _cache!.Drop(key);
      c.WantCache = cacheIt;
      c.WantCollision = near;
      if (!near && c.HasCollision) RemoveCollision(c);

      if (!c.Ready || (near && !c.HasCollision && c.MeshInstance != null))
      {
        _scheduler!.Request(c, PriorityOf(c));
      }
    }

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

      double t0 = sw.Elapsed.TotalMilliseconds;
      ApplyMesh(c, r);
      double t1 = sw.Elapsed.TotalMilliseconds;

      bool needCollision = c.WantCollision && r.Arrays != null && r.CollisionFaces == null;
      if (r.CollisionFaces is Variant faces && c.WantCollision) SetCollision(c, faces);
      else if (r.Arrays == null || !r.Job.Collision) RemoveCollision(c);
      _frameMeshMs += t1 - t0;
      _frameCollisionMs += sw.Elapsed.TotalMilliseconds - t1;

      c.Ready = true;
      _scheduler.Complete(c, needCollision);
    }
    _maxItemMs = Math.Max(_maxItemMs, sw.Elapsed.TotalMilliseconds - itemStart);
    _applyMs = sw.Elapsed.TotalMilliseconds;
  }

  private void ApplyMesh(Chunk c, MeshResult r)
  {
    if (c.MeshInstance != null) _vertexCount -= c.MeshInstance.Mesh is ArrayMesh old ? old.SurfaceGetArrayLen(0) : 0;

    if (r.Arrays == null)
    {
      if (c.MeshInstance != null)
      {
        if (c.MeshInstance.Visible) _visibleCount--;
        c.MeshInstance.QueueFree();
        c.MeshInstance = null;
      }
      return;
    }

    var mesh = new ArrayMesh();
    var format = (Mesh.ArrayFormat)((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift);
    mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, r.Arrays, null, null, format);
    _vertexCount += r.VertexCount;

    if (c.MeshInstance == null)
    {
      c.MeshInstance = new MeshInstance3D
      {
        Mesh = mesh,
        MaterialOverride = _material,
        Position = c.Key.Origin,
        Visible = c.Visible,
      };
      AddChild(c.MeshInstance);
      c.MeshInstance.SetInstanceShaderParameter("lod_mask", Math.Max(c.LodMask, 0));
      if (c.Visible) _visibleCount++;
    }
    else
    {
      c.MeshInstance.Mesh = mesh;
    }
  }

  private void SetCollision(Chunk c, Variant faces)
  {
    if (c.Body == null)
    {
      c.Body = new StaticBody3D { Position = c.Key.Origin };
      c.CollisionShape = new CollisionShape3D();
      c.Body.AddChild(c.CollisionShape);
      AddChild(c.Body);
    }
    var shape = new ConcavePolygonShape3D();
    shape.Set(ConcavePolygonShape3D.PropertyName.Data, faces);
    c.CollisionShape!.Shape = shape;
    if (!c.HasCollision) CollisionChunks++;
    c.HasCollision = true;
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

  private void UpdateSwaps()
  {
    if (_retiring.Count > 0)
    {
      _scratch.Clear();
      foreach (var c in _retiring.Values)
      {
        if (ReplacementsReady(c.Key)) _scratch.Add(c);
      }
      foreach (var c in _scratch)
      {
        _retiring.Remove(c.Key);
        Destroy(c);
      }
    }

    if (_pendingShow.Count > 0)
    {
      _scratch.Clear();
      foreach (var c in _pendingShow)
      {
        if (c.Ready && !OverlapsRetiring(c.Key)) _scratch.Add(c);
      }
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
  }

  /// <summary>True when every active chunk overlapping key's region has its mesh.</summary>
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

  private bool OverlapsRetiring(ChunkKey key)
  {
    if (_retiring.Count == 0) return false;
    foreach (var r in _retiring.Keys)
    {
      if (Contains(key, r) || Contains(r, key)) return true;
    }
    return false;
  }

  private static bool Contains(ChunkKey outer, ChunkKey inner)
  {
    if (outer.Lod < inner.Lod) return false;
    int s = outer.Lod - inner.Lod;
    return new Vector3I(inner.Coord.X >> s, inner.Coord.Y >> s, inner.Coord.Z >> s) == outer.Coord;
  }

  private void Destroy(Chunk c)
  {
    c.Removed = true;
    if (c.MeshInstance != null)
    {
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
    Add(MonitorNames[12], () => (_scheduler?.Results.Count ?? 0) + _pendingShow.Count);
    Add(MonitorNames[13], () => _processMs);
    Add(MonitorNames[14], () => _diffMs);
    Add(MonitorNames[15], () => _swapMs);
    Add(MonitorNames[16], () => _maxItemMs);
  }
}
