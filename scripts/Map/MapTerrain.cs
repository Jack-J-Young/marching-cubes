#nullable enable
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Voxel;

namespace Map;

/// <summary>
/// Builds the map's own low-poly copy of the terrain: one coarse (LOD) chunk mesh per chunk within a radius,
/// generated with the same pipeline as the game's chunks (noise, spawn clearing and every dig / build edit), on a
/// couple of background threads. All chunks share one LOD, so their seams match without transition cells.
/// </summary>
public sealed class MapTerrain
{
  /// <summary>Chunk LOD of the map: 8-unit cells, 256-unit chunks.</summary>
  public const int Lod = 3;
  /// <summary>Threads a rebuild may use (the terrain streamer keeps the rest).</summary>
  public const int Threads = 2;

  public sealed record Result(Vector3 Centre, float Radius, int EditStamp, List<(ChunkKey Key, Godot.Collections.Array Arrays)> Chunks, double Ms);

  /// <summary>Per-thread scratch, reused across chunks.</summary>
  public sealed class Context
  {
    public readonly VoxelVolume Volume = new();
    public readonly MeshBuffers Buffers = new();
    public readonly int[] EdgeCache = new int[MarchingCubesMesher.EdgeCacheSize];
    public readonly List<SphereEdit> Edits = [];
  }

  private Task<Result>? _task;

  public bool Building => _task != null && !_task.IsCompleted;

  /// <summary>Map chunks: every LOD chunk whose bounds come within radius of centre (ChunkManager-local space).</summary>
  public static List<ChunkKey> Keys(Vector3 centre, float radius)
  {
    var keys = new List<ChunkKey>();
    var lo = ChunkKey.Containing(centre - Vector3.One * radius, Lod).Coord;
    var hi = ChunkKey.Containing(centre + Vector3.One * radius, Lod).Coord;
    for (int z = lo.Z; z <= hi.Z; z++)
      for (int y = lo.Y; y <= hi.Y; y++)
        for (int x = lo.X; x <= hi.X; x++)
        {
          var key = new ChunkKey(Lod, new Vector3I(x, y, z));
          if (VoxelMath.DistanceToAabb(centre, key.Bounds) <= radius) keys.Add(key);
        }
    return keys;
  }

  /// <summary>Surface arrays for one map chunk (null if it has no surface). Thread-safe with its own context.</summary>
  public static Godot.Collections.Array? BuildChunk(TerrainGenerator gen, ChunkKey key, Context ctx)
  {
    var vol = ctx.Volume;
    vol.Generate(gen, key);
    int newest = gen.Edits.Query(key.PaddedBounds, -1, ctx.Edits);
    vol.ApplyEdits(ctx.Edits, newest);
    if (!vol.HasSurface()) return null;
    ctx.Buffers.Clear();
    MarchingCubesMesher.Mesh(vol, gen, ctx.Buffers, ctx.EdgeCache);
    return ctx.Buffers.IndexCount > 0 ? ctx.Buffers.ToSurfaceArrays() : null;
  }

  /// <summary>Starts a background rebuild around centre. Ignored while one is running.</summary>
  public void Start(TerrainGenerator gen, Vector3 centre, float radius, int editStamp)
  {
    if (Building) return;
    var keys = Keys(centre, radius);
    _task = Task.Run(() =>
    {
      var sw = System.Diagnostics.Stopwatch.StartNew();
      var chunks = new ConcurrentBag<(ChunkKey, Godot.Collections.Array)>();
      Parallel.ForEach(keys, new ParallelOptions { MaxDegreeOfParallelism = Threads }, () => new Context(),
        (key, _, ctx) =>
        {
          var arrays = BuildChunk(gen, key, ctx);
          if (arrays != null) chunks.Add((key, arrays));
          return ctx;
        },
        _ => { });
      return new Result(centre, radius, editStamp, [.. chunks], sw.Elapsed.TotalMilliseconds);
    });
  }

  /// <summary>The finished rebuild, once (null while building or if nothing new is ready).</summary>
  public Result? TakeResult()
  {
    if (_task == null || !_task.IsCompleted) return null;
    var task = _task;
    _task = null;
    if (task.IsFaulted)
    {
      GD.PushError($"Map build failed: {task.Exception}");
      return null;
    }
    return task.Result;
  }
}
