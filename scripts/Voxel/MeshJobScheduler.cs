#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Godot;

namespace Voxel;

/// <summary>Snapshot of what a worker should build, taken under the scheduler lock when the job starts.</summary>
public readonly record struct MeshJob(ChunkKey Key, int Version, bool Collision, bool Cache);

public sealed class MeshResult
{
  public required Chunk Chunk;
  public required MeshJob Job;
  /// <summary>Null when the chunk has no surface.</summary>
  public Godot.Collections.Array? Arrays;
  /// <summary>PackedVector3Array of collision faces, or null.</summary>
  public Variant? CollisionFaces;
  public int VertexCount;
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
            job = new MeshJob(c.Key, c.Version, c.WantCollision, c.WantCache);
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

  public ChunkBuilder(TerrainGenerator gen, VolumeCache cache)
  {
    _gen = gen;
    _cache = cache;
  }

  public MeshResult Build(MeshJob job, Chunk chunk, WorkerContext ctx)
  {
    long start = Stopwatch.GetTimestamp();
    long t = start;
    var key = job.Key;
    var box = key.PaddedBounds;

    var vol = _cache.Take(key);
    t = VoxelProfiler.Lap(VoxelProfiler.Section.Cache, t);
    if (vol == null)
    {
      vol = _cache.Rent();
      vol.Generate(_gen, key);
      t = VoxelProfiler.Lap(VoxelProfiler.Section.Noise, t);
    }
    int newest = _gen.Edits.Query(box, vol.AppliedEditSeq, ctx.NewEdits);
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
        return result;
      }

      var buf = ctx.Buffers;
      buf.Clear();
      MarchingCubesMesher.Mesh(vol, _gen, buf, ctx.EdgeCache);
      t = VoxelProfiler.Lap(VoxelProfiler.Section.MarchingCubes, t);
      if (key.Lod > 0)
      {
        _gen.Edits.Query(box, -1, ctx.AllEdits);
        TransitionMesher.Mesh(vol, _gen, ctx.AllEdits, buf, ctx.Transition);
        t = VoxelProfiler.Lap(VoxelProfiler.Section.Transvoxel, t);
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
}
