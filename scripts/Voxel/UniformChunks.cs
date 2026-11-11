#nullable enable
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Godot;

namespace Voxel;

/// <summary>
/// Chunks known to be all air (empty space) or all rock, so they never have a surface. Remembered when a build finds
/// no surface, together with the newest edit it included, so the next time that chunk is needed (streamed out and
/// back in, LOD changes) the build is skipped without generating any noise.
///
/// Edits made since are checked when a record is used, so it can't go stale: digs can't put rock into empty space, so
/// an all-air chunk stays known-empty until a build edit touches it; an all-rock chunk is dropped by any edit.
/// The pathfinder also asks it which space is known to be empty (KnownAir). Thread-safe.
/// </summary>
public sealed class UniformChunks
{
  public readonly record struct Record(bool Air, int Seq);

  private readonly ConcurrentDictionary<ChunkKey, Record> _records = new();
  private readonly EditStore _edits;
  private long _hits, _stale;
  /// <summary>Records beyond this are dropped (all of them; they're cheap to learn again).</summary>
  public int MaxRecords = 400_000;

  public UniformChunks(EditStore edits) => _edits = edits;

  public int Count => _records.Count;
  /// <summary>Builds skipped because the chunk was known uniform, and records dropped because edits touched them.</summary>
  public long Hits => Interlocked.Read(ref _hits);
  public long Stale => Interlocked.Read(ref _stale);

  /// <summary>Remembers that this chunk had no surface with edits up to seq applied (air: empty space, else rock).</summary>
  public void Remember(ChunkKey key, bool air, int seq)
  {
    if (_records.Count >= MaxRecords) _records.Clear();
    _records[key] = new Record(air, seq);
  }

  public void Forget(ChunkKey key) => _records.TryRemove(key, out _);

  /// <summary>
  /// True if the chunk is still known uniform with edits up to upToSeq (scratch is a per-thread list). A record that
  /// edits have since made unsure is dropped.
  /// </summary>
  public bool StillUniform(ChunkKey key, int upToSeq, List<SphereEdit> scratch, out bool air)
  {
    air = false;
    if (!_records.TryGetValue(key, out var r)) return false;
    if (upToSeq < r.Seq) return false; // an older snapshot than the record: can't say
    _edits.Query(key.PaddedBounds, r.Seq, scratch, upToSeq);
    foreach (var e in scratch)
      if (!r.Air || e.Build)
      {
        _records.TryRemove(key, out _);
        Interlocked.Increment(ref _stale);
        return false;
      }
    air = r.Air;
    return true;
  }

  public void CountHit() => Interlocked.Increment(ref _hits);

  /// <summary>
  /// True if this box (ChunkManager-local) lies inside a chunk of LOD at most maxLod that is known to be all air with
  /// every edit so far. Coarser chunks sample more sparsely, so they can miss rock smaller than their step.
  /// </summary>
  public bool KnownAir(Aabb box, int maxLod, List<SphereEdit> scratch)
  {
    var centre = box.GetCenter();
    for (int lod = 0; lod <= maxLod; lod++)
    {
      var key = ChunkKey.Containing(centre, lod);
      if (!key.Bounds.Encloses(box)) continue;
      if (StillUniform(key, int.MaxValue, scratch, out bool air) && air) return true;
    }
    return false;
  }
}
