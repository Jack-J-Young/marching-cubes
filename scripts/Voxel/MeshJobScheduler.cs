#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Godot;

namespace Voxel;

/// <summary>Snapshot of what a worker should build, taken under the scheduler lock when the job starts.</summary>
/// <summary>
/// TargetSeq = build with edits up to this Seq. FromSeq = edit snapshot currently shown (-2 = nothing); when edits in
/// (FromSeq, TargetSeq] touch the chunk, vertices get morph offsets back to the shown surface.
/// </summary>
public readonly record struct MeshJob(ChunkKey Key, int Version, bool Collision, bool Cache, int TargetSeq, int FromSeq);

public sealed class MeshResult
{
  public required Chunk Chunk;
  public required MeshJob Job;
  /// <summary>Null when the chunk has no surface.</summary>
  public Godot.Collections.Array? Arrays;
  /// <summary>PackedVector3Array of collision faces, or null.</summary>
  public Variant? CollisionFaces;
  public int VertexCount;
  /// <summary>
  /// One regular mesh vertex per cell that has surface, packed into 4 bytes (see SurfacePoint): a compact copy of
  /// where the shown surface is, which the main thread sphere-casts against without touching noise or the GPU mesh.
  /// </summary>
  public uint[]? SurfacePoints;
  /// <summary>Arrays carry CUSTOM1 morph offsets (use the morph material).</summary>
  public bool HasMorph;
  public double GenerateMs;
  public double MeshMs;
}

/// <summary>Per-worker scratch memory, reused for every job on that thread.</summary>
public sealed class WorkerContext
{
  public readonly int[] EdgeCache = new int[MarchingCubesMesher.EdgeCacheSize];
  public readonly MeshBuffers Buffers = new();
  public readonly TransitionMesher.Scratch Transition = new();
  public readonly List<SphereEdit> NewEdits = [];
  public readonly List<SphereEdit> AllEdits = [];
  public readonly List<SphereEdit> MorphEdits = [];
  public readonly ulong[] CellSeen = new ulong[ChunkBuilder.CellWords];
  public readonly List<uint> Points = [];
}

/// <summary>
/// Fixed pool of worker threads pulling chunk builds from a priority queue (lower = sooner).
/// The chunk flags Queued / InFlight / Rerun are only touched under this scheduler's lock.
/// </summary>
public sealed class MeshJobScheduler : IDisposable
{
  private readonly object _lock = new();
  private readonly PriorityQueue<Chunk, float> _queue = new();
  private readonly Thread[] _threads;
  private readonly Func<MeshJob, Chunk, WorkerContext, MeshResult> _build;
  private bool _stopping;
  private int _busy;

  public readonly ConcurrentQueue<MeshResult> Results = new();

  public int QueueLength
  {
    get { lock (_lock) return _queue.Count; }
  }

  public int Busy => Volatile.Read(ref _busy);

  public int WorkerCount => _threads.Length;

  public MeshJobScheduler(int workers, Func<MeshJob, Chunk, WorkerContext, MeshResult> build)
  {
    _build = build;
    _threads = new Thread[Math.Max(1, workers)];
    for (int i = 0; i < _threads.Length; i++)
    {
      // Below normal so the OS favors the main and render threads when every core is busy.
      _threads[i] = new Thread(WorkerLoop) { IsBackground = true, Name = $"VoxelMesher{i}", Priority = ThreadPriority.BelowNormal };
      _threads[i].Start();
    }
  }

  /// <summary>Queues a (re)build. If the chunk is already being built, it is rebuilt again when that finishes.</summary>
  public void Request(Chunk c, float priority)
  {
    lock (_lock)
    {
      c.Priority = Math.Min(c.Priority, priority);
      if (c.InFlight)
      {
        c.Rerun = true;
        return;
      }
      if (c.Queued)
      {
        return;
      }
      c.Queued = true;
      _queue.Enqueue(c, priority);
      Monitor.Pulse(_lock);
    }
  }

  /// <summary>Called on the main thread once a chunk's result has been consumed (applied or discarded).</summary>
  public void Complete(Chunk c, bool stale)
  {
    lock (_lock)
    {
      c.InFlight = false;
      if ((c.Rerun || stale) && !c.Removed)
      {
        c.Rerun = false;
        c.Queued = true;
        if (c.Priority == float.MaxValue) c.Priority = 0;
        _queue.Enqueue(c, c.Priority);
        Monitor.Pulse(_lock);
      }
    }
  }

  /// <summary>Recomputes every queued chunk's priority (for example after the player moves). Removed chunks are dropped.</summary>
  public void Reprioritize(Func<Chunk, float> priority)
  {
    lock (_lock)
    {
      var items = new List<Chunk>(_queue.Count);
      while (_queue.TryDequeue(out var c, out _)) items.Add(c);
      foreach (var c in items)
      {
        if (c.Removed)
        {
          c.Queued = false;
          continue;
        }
        c.Priority = priority(c);
        _queue.Enqueue(c, c.Priority);
      }
    }
  }

  private void WorkerLoop()
  {
    var ctx = new WorkerContext();
    while (true)
    {
      Chunk c;
      MeshJob job;
      lock (_lock)
      {
        while (true)
        {
          if (_stopping) return;
          if (_queue.TryDequeue(out c!, out _))
          {
            c.Queued = false;
            if (c.Removed) continue;
            c.InFlight = true;
            c.Rerun = false;
            c.Priority = float.MaxValue;
            job = new MeshJob(c.Key, c.Version, c.WantCollision, c.WantCache, c.TargetSeq, c.BaseSeq);
            break;
          }
          Monitor.Wait(_lock);
        }
      }

      Interlocked.Increment(ref _busy);
      try
      {
        Results.Enqueue(_build(job, c, ctx));
      }
      catch (Exception e)
      {
        GD.PushError($"Voxel build {job.Key} failed: {e}");
        Results.Enqueue(new MeshResult { Chunk = c, Job = job });
      }
      finally
      {
        Interlocked.Decrement(ref _busy);
      }
    }
  }

  public void Dispose()
  {
    lock (_lock)
    {
      _stopping = true;
      Monitor.PulseAll(_lock);
    }
    foreach (var t in _threads) t.Join();
  }
}

/// <summary>The work a job does: get or generate the volume, apply edits, mesh, build Godot arrays.</summary>
public sealed class ChunkBuilder
{
  private readonly TerrainGenerator _gen;
  private readonly VolumeCache _cache;
  private readonly UniformChunks? _uniform;

  public ChunkBuilder(TerrainGenerator gen, VolumeCache cache, UniformChunks? uniform = null)
  {
    _gen = gen;
    _cache = cache;
    _uniform = uniform;
  }

  public MeshResult Build(MeshJob job, Chunk chunk, WorkerContext ctx)
  {
    long start = Stopwatch.GetTimestamp();
    long t = start;
    var key = job.Key;
    var box = key.PaddedBounds;

    // Known all air / all rock (and no edit since that could change that): nothing to generate or mesh.
    if (_uniform != null && _uniform.StillUniform(key, job.TargetSeq, ctx.NewEdits, out _))
    {
      _uniform.CountHit();
      VoxelProfiler.CountChunk(true);
      return new MeshResult { Chunk = chunk, Job = job };
    }

    var vol = _cache.Take(key);
    t = VoxelProfiler.Lap(VoxelProfiler.Section.Cache, t);
    if (vol != null && vol.AppliedEditSeq > job.TargetSeq)
    {
      // Cached state is ahead of this job's edit snapshot; edits can't be undone, so start over.
      _cache.Release(vol);
      vol = null;
    }
    bool generated = vol == null;
    if (vol == null)
    {
      vol = _cache.Rent();
      vol.Generate(_gen, key);
      t = VoxelProfiler.Lap(VoxelProfiler.Section.Noise, t);
    }
    int newest = _gen.Edits.Query(box, vol.AppliedEditSeq, ctx.NewEdits, job.TargetSeq);
    VoxelProfiler.CountVolume(!generated, ctx.NewEdits.Count);
    vol.ApplyEdits(ctx.NewEdits, newest);
    t = VoxelProfiler.Lap(VoxelProfiler.Section.Edits, t);
    double genMs = VoxelProfiler.TicksToMs(t - start);

    var result = new MeshResult { Chunk = chunk, Job = job, GenerateMs = genMs };
    try
    {
      bool surface = vol.HasSurface();
      t = VoxelProfiler.Lap(VoxelProfiler.Section.SurfaceCheck, t);
      if (!surface)
      {
        _uniform?.Remember(key, vol.IsAirAtOrigin, newest);
        return result;
      }

      var buf = ctx.Buffers;
      buf.Clear();
      MarchingCubesMesher.Mesh(vol, _gen, buf, ctx.EdgeCache);
      if (buf.VertexCount > 0) result.SurfacePoints = PackSurfacePoints(buf, buf.VertexCount, key.Step, ctx.CellSeen, ctx.Points);
      t = VoxelProfiler.Lap(VoxelProfiler.Section.MarchingCubes, t);
      if (key.Lod > 0)
      {
        _gen.Edits.Query(box, -1, ctx.AllEdits, job.TargetSeq);
        TransitionMesher.Mesh(vol, _gen, ctx.AllEdits, buf, ctx.Transition);
        t = VoxelProfiler.Lap(VoxelProfiler.Section.Transvoxel, t);
      }

      if (buf.IndexCount > 0 && job.FromSeq >= -1 && job.FromSeq < job.TargetSeq)
      {
        _gen.Edits.Query(box, job.FromSeq, ctx.MorphEdits, job.TargetSeq);
        ctx.MorphEdits.RemoveAll(static e => e.Instant); // those just appear
        if (ctx.MorphEdits.Count > 0)
        {
          MorphOffsets(vol, buf, ctx.MorphEdits);
          result.HasMorph = buf.HasMorph;
        }
        t = VoxelProfiler.Lap(VoxelProfiler.Section.Morph, t);
      }

      if (buf.IndexCount > 0)
      {
        result.Arrays = buf.ToSurfaceArrays();
        result.VertexCount = buf.VertexCount;
        t = VoxelProfiler.Lap(VoxelProfiler.Section.GodotArrays, t);
        if (job.Collision && buf.RegularIndexCount > 0)
        {
          result.CollisionFaces = buf.ToCollisionFaces();
          t = VoxelProfiler.Lap(VoxelProfiler.Section.CollisionFaces, t);
        }
      }
      return result;
    }
    finally
    {
      _cache.Return(vol, job.Cache);
      t = VoxelProfiler.Lap(VoxelProfiler.Section.Cache, t);
      result.MeshMs = VoxelProfiler.TicksToMs(t - start) - genMs;
      VoxelProfiler.CountChunk(result.Arrays == null);
    }
  }

  public const int CellWords = VoxelConst.Cells * VoxelConst.Cells * VoxelConst.Cells / 64;

  /// <summary>
  /// Keeps the first vertex in each cell, packed as cell index (15 bits) + position inside the cell in 1/32 steps
  /// (3 x 5 bits). About 4 bytes per surface cell, a few KB per chunk.
  /// </summary>
  public static uint[] PackSurfacePoints(MeshBuffers buf, int vertexCount, int step, ulong[] seen, List<uint> points)
  {
    Array.Clear(seen);
    points.Clear();
    float inv = 1f / step;
    const int max = VoxelConst.Cells - 1;
    for (int i = 0; i < vertexCount; i++)
    {
      var g = buf.Positions[i] * inv;
      int x = Math.Clamp((int)g.X, 0, max), y = Math.Clamp((int)g.Y, 0, max), z = Math.Clamp((int)g.Z, 0, max);
      int idx = x + (y + z * VoxelConst.Cells) * VoxelConst.Cells;
      ulong bit = 1UL << (idx & 63);
      if ((seen[idx >> 6] & bit) != 0) continue;
      seen[idx >> 6] |= bit;
      uint fx = (uint)Math.Clamp((int)((g.X - x) * 32f), 0, 31);
      uint fy = (uint)Math.Clamp((int)((g.Y - y) * 32f), 0, 31);
      uint fz = (uint)Math.Clamp((int)((g.Z - z) * 32f), 0, 31);
      points.Add((uint)idx | fx << 15 | fy << 20 | fz << 25);
    }
    return points.ToArray();
  }

  /// <summary>Chunk-local position of a packed surface point.</summary>
  public static Vector3 SurfacePoint(uint packed, int step)
  {
    int idx = (int)(packed & 0x7FFF);
    int x = idx & 31, y = (idx >> 5) & 31, z = idx >> 10;
    float fx = ((packed >> 15) & 31) + 0.5f, fy = ((packed >> 20) & 31) + 0.5f, fz = ((packed >> 25) & 31) + 0.5f;
    return new Vector3(x + fx / 32f, y + fy / 32f, z + fz / 32f) * step;
  }

  /// <summary>
  /// For each new vertex, the offset back to the surface that is currently shown. The old density is the new one
  /// (trilinear from the volume) with the new edits undone analytically (see UndoEdits). Below the surface the density saturates (no slope to follow), so instead of Newton steps this
  /// searches: from the vertex along its normal (toward air for a dig) until the old density crosses Iso, then
  /// bisects. Vertices on a chunk face search within that face plane (the old mesh's face vertices were on it too),
  /// reading only face samples both neighbours share, so their offsets are identical and seams hold mid-animation.
  /// </summary>
  public static void MorphOffsets(VoxelVolume vol, MeshBuffers buf, List<SphereEdit> edits)
  {
    var origin = (Vector3)vol.Key.Origin;
    float step = vol.Key.Step;
    float maxRadius = 0;
    foreach (var e in edits) maxRadius = Math.Max(maxRadius, e.Radius);
    float hi = VoxelConst.Cells * step;
    // One-cell steps then bisection; a commit rarely moves the surface more than a few cells, so the search stops
    // at 16 (beyond that the vertex just pops).
    float march = step;
    float maxSearch = Math.Min(maxRadius, 16f * step);

    float OldDensity(Vector3 q, out bool ok)
    {
      float d = vol.SampleDensity(q);
      ok = UndoEdits(origin + q, edits, ref d);
      return d;
    }

    var morph = buf.Morph;
    bool any = false;
    for (int i = 0; i < buf.VertexCount; i++)
    {
      var local = buf.Positions[i];
      var x = Vector3.Zero;
      if (Touched(origin + local, edits))
      {
        bool fixX = local.X == 0 || local.X == hi, fixY = local.Y == 0 || local.Y == hi, fixZ = local.Z == 0 || local.Z == hi;
        float d0 = OldDensity(local, out bool ok0);
        var u = vol.SampleGradient(local); // toward solid in the new field
        if (fixX) u.X = 0;
        if (fixY) u.Y = 0;
        if (fixZ) u.Z = 0;
        float ul = u.Length();
        if (ok0 && ul > 1e-9f && Math.Abs(d0 - VoxelConst.Iso) > 1e-6f)
        {
          // Old surface lies toward air if the old density here is above Iso (a dig), toward solid if below (a build).
          u *= (d0 > VoxelConst.Iso ? -1f : 1f) / ul;
          bool above = d0 > VoxelConst.Iso;
          float sPrev = 0, dPrev = d0;
          for (float s = march; s <= maxSearch; s += march)
          {
            var q = local + u * s;
            if (q.X < 0 || q.Y < 0 || q.Z < 0 || q.X > hi || q.Y > hi || q.Z > hi) break; // no samples out there
            float d = OldDensity(q, out bool ok);
            if (!ok) break;
            if (d > VoxelConst.Iso == above)
            {
              sPrev = s;
              dPrev = d;
              continue;
            }
            // Bracketed within one cell, where the field is close to linear: two false-position steps.
            float lo = sPrev, dLo = dPrev, hiS = s, dHi = d;
            float sx = lo + (dLo - VoxelConst.Iso) / (dLo - dHi) * (hiS - lo);
            for (int k = 0; k < 2; k++)
            {
              float dm = OldDensity(local + u * sx, out _);
              if (dm > VoxelConst.Iso == above) (lo, dLo) = (sx, dm); else (hiS, dHi) = (sx, dm);
              if (Math.Abs(dLo - dHi) < 1e-7f) break;
              sx = lo + (dLo - VoxelConst.Iso) / (dLo - dHi) * (hiS - lo);
            }
            x = u * sx;
            break;
          }
        }
        any |= x.LengthSquared() > 1e-10f;
      }
      int mi = i * 3;
      morph[mi] = x.X;
      morph[mi + 1] = x.Y;
      morph[mi + 2] = x.Z;
    }
    buf.HasMorph = any;
  }

  /// <summary>
  /// Turns a density after the edits back into the density before them, undoing each edit newest first: a dig
  /// divides by its factor, a build divides (1 - density) by it. False if an edit wiped the value out (factor ~0).
  /// </summary>
  private static bool UndoEdits(Vector3 p, List<SphereEdit> edits, ref float d)
  {
    for (int i = edits.Count - 1; i >= 0; i--)
    {
      var e = edits[i];
      float d2 = (p - e.Center).LengthSquared();
      if (d2 >= e.Radius * e.Radius) continue;
      float t = MathF.Sqrt(d2) / e.Radius;
      if (t >= 1f) continue;
      float keep = 1f - e.Strength * (1f - t);
      if (keep <= 1e-6f) return false;
      d = e.Build ? 1f - (1f - d) / keep : d / keep;
    }
    return true;
  }

  /// <summary>True if any of the edits changes density at p.</summary>
  private static bool Touched(Vector3 p, List<SphereEdit> edits)
  {
    foreach (var e in edits)
    {
      if (e.Strength > 0 && (p - e.Center).LengthSquared() < e.Radius * e.Radius) return true;
    }
    return false;
  }
}
