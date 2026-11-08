using System.Diagnostics;
using System.Threading;

namespace Voxel;

/// <summary>
/// Lock-free counters for where terrain worker threads spend their time. Workers add elapsed Stopwatch ticks per
/// section; the debug overlay samples the totals and diffs them over time to get a breakdown per second.
/// </summary>
public static class VoxelProfiler
{
  public enum Section
  {
    Noise,
    Edits,
    SurfaceCheck,
    MarchingCubes,
    Transvoxel,
    GodotArrays,
    CollisionFaces,
    Cache,
    Count,
  }

  public static readonly string[] Names =
  [
    "noise (density)",
    "dig edits",
    "surface check",
    "marching cubes + colors",
    "transvoxel seams",
    "godot mesh arrays",
    "collision faces",
    "volume cache",
  ];

  private static readonly long[] _ticks = new long[(int)Section.Count];
  private static long _chunksBuilt;
  private static long _chunksEmpty;

  public static long ChunksBuilt => Interlocked.Read(ref _chunksBuilt);
  public static long ChunksEmpty => Interlocked.Read(ref _chunksEmpty);

  /// <summary>Adds the time since `start` to a section and returns the current timestamp (for chaining).</summary>
  public static long Lap(Section s, long start)
  {
    long now = Stopwatch.GetTimestamp();
    Interlocked.Add(ref _ticks[(int)s], now - start);
    return now;
  }

  public static void CountChunk(bool empty)
  {
    Interlocked.Increment(ref _chunksBuilt);
    if (empty) Interlocked.Increment(ref _chunksEmpty);
  }

  /// <summary>Copies the running totals (in Stopwatch ticks) into dest.</summary>
  public static void Snapshot(long[] dest)
  {
    for (int i = 0; i < (int)Section.Count; i++) dest[i] = Interlocked.Read(ref _ticks[i]);
  }

  public static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
