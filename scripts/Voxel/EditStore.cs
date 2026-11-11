using System;
using System.Collections.Generic;
using Godot;

namespace Voxel;

/// <summary>
/// A spherical edit. Seq is its position in the global edit order (edits are order-dependent).
/// A dig (Build = false) pulls density toward air, a build pulls it toward solid, with the same falloff.
/// Strength 1 is a full carve / fill at the centre; smaller strengths act partially, so repeated weak edits work
/// gradually. Instant edits show up without the dig animation (AI mining: lots of them, nobody watching closely).
/// </summary>
public readonly record struct SphereEdit(Vector3 Center, float Radius, float Strength, int Seq, bool Build = false, bool Instant = false)
{
  /// <summary>
  /// density *= 1 - Strength * (1 - clamp(dist / radius, 0, 1)). With Strength 1 this is the old compute
  /// shader's falloff (density *= dist / radius): strongest at the centre, no effect at the rim.
  /// </summary>
  public float Apply(Vector3 p, float density)
  {
    float dx = p.X - Center.X, dy = p.Y - Center.Y, dz = p.Z - Center.Z;
    return Apply(dx * dx + dy * dy + dz * dz, density);
  }

  /// <summary>
  /// Apply from the squared distance. Every path (volume rows, transition face points, analytic queries) goes
  /// through this with d2 summed as (dx^2 + dy^2) + dz^2, so samples shared by two chunks get bit-identical values.
  /// </summary>
  [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
  public float Apply(float d2, float density)
  {
    if (d2 >= Radius * Radius) return density;
    float d = MathF.Sqrt(d2) / Radius;
    if (d >= 1f) return density;
    float keep = 1f - Strength * (1f - d);
    // Dig: density *= keep. Build: the mirror image, (1 - density) *= keep, filling toward solid.
    return Build ? 1f - (1f - density) * keep : density * keep;
  }
}

/// <summary>
/// Append-only list of edits, spatially hashed so a chunk can fetch only the edits touching it.
/// Because edits are analytic they can be replayed onto any chunk at any LOD, and survive chunk unloading.
/// Thread-safe: written on the main thread, read by mesh workers.
/// </summary>
public sealed class EditStore
{
  private const float CellSize = 64f;

  private readonly object _lock = new();
  private readonly List<SphereEdit> _edits = [];
  private readonly Dictionary<Vector3I, List<int>> _grid = [];

  public int Count
  {
    get { lock (_lock) return _edits.Count; }
  }

  /// <summary>Seq of the newest edit, -1 if none.</summary>
  public int NewestSeq
  {
    get { lock (_lock) return _edits.Count - 1; }
  }

  public int Add(Vector3 center, float radius, float strength = 1f, bool build = false, bool instant = false)
  {
    lock (_lock)
    {
      int seq = _edits.Count;
      var e = new SphereEdit(center, radius, Math.Clamp(strength, 0f, 1f), seq, build, instant);
      _edits.Add(e);
      AddToGrid(e);
      return seq;
    }
  }

  /// <summary>
  /// Collects edits with afterSeq &lt; Seq &lt;= upToSeq that intersect box, sorted by Seq.
  /// Returns the newest Seq covered (min of upToSeq and the newest edit; pass it back as afterSeq next time).
  /// </summary>
  public int Query(Aabb box, int afterSeq, List<SphereEdit> output, int upToSeq = int.MaxValue)
  {
    output.Clear();
    lock (_lock)
    {
      int newest = Math.Min(_edits.Count - 1, upToSeq);
      if (newest <= afterSeq)
      {
        return newest;
      }

      var min = CellOf(box.Position);
      var max = CellOf(box.End);
      for (int z = min.Z; z <= max.Z; z++)
        for (int y = min.Y; y <= max.Y; y++)
          for (int x = min.X; x <= max.X; x++)
          {
            if (!_grid.TryGetValue(new Vector3I(x, y, z), out var list))
            {
              continue;
            }
            foreach (int seq in list)
            {
              if (seq <= afterSeq || seq > newest)
              {
                continue;
              }
              var e = _edits[seq];
              if (VoxelMath.SphereIntersectsAabb(e.Center, e.Radius, box))
              {
                output.Add(e);
              }
            }
          }
      if (output.Count > 1)
      {
        output.Sort(static (a, b) => a.Seq.CompareTo(b.Seq));
        // An edit spanning several hash cells shows up once per cell.
        int w = 1;
        for (int r = 1; r < output.Count; r++)
        {
          if (output[r].Seq != output[w - 1].Seq)
          {
            output[w++] = output[r];
          }
        }
        output.RemoveRange(w, output.Count - w);
      }
      return newest;
    }
  }

  /// <summary>
  /// Folds a new dig into the newest edit instead of appending one, if that edit is newer than afterSeq (no build has
  /// been given it yet, so nothing on screen or in flight depends on its old value), has the same radius and is
  /// within maxShift of center. Strengths combine like two digs at one spot (1 - (1-a)(1-b), exact at the centre)
  /// and the centre moves to the strength-weighted average. Returns the merged edit's Seq, or -1 if it can't merge.
  /// </summary>
  public int TryMergeLast(Vector3 center, float radius, float strength, int afterSeq, float maxShift, out SphereEdit merged,
    bool build = false, bool instant = false)
  {
    merged = default;
    lock (_lock)
    {
      if (_edits.Count == 0) return -1;
      var last = _edits[^1];
      if (last.Seq <= afterSeq || last.Build != build || last.Instant != instant || Math.Abs(last.Radius - radius) > 1e-4f
          || last.Center.DistanceTo(center) > maxShift)
      {
        return -1;
      }
      strength = Math.Clamp(strength, 0f, 1f);
      float total = last.Strength + strength;
      var c = total > 0 ? (last.Center * last.Strength + center * strength) / total : center;
      merged = new SphereEdit(c, radius, 1f - (1f - last.Strength) * (1f - strength), last.Seq, build, instant);

      // Re-hash if the centre moved into other cells. This edit is the newest, so it is the last entry of each list.
      RemoveFromGrid(last);
      _edits[^1] = merged;
      AddToGrid(merged);
      return merged.Seq;
    }
  }

  private void AddToGrid(SphereEdit e)
  {
    var min = CellOf(e.Center - Vector3.One * e.Radius);
    var max = CellOf(e.Center + Vector3.One * e.Radius);
    for (int z = min.Z; z <= max.Z; z++)
      for (int y = min.Y; y <= max.Y; y++)
        for (int x = min.X; x <= max.X; x++)
        {
          var cell = new Vector3I(x, y, z);
          if (!_grid.TryGetValue(cell, out var list))
          {
            list = [];
            _grid[cell] = list;
          }
          list.Add(e.Seq);
        }
  }

  private void RemoveFromGrid(SphereEdit e)
  {
    var min = CellOf(e.Center - Vector3.One * e.Radius);
    var max = CellOf(e.Center + Vector3.One * e.Radius);
    for (int z = min.Z; z <= max.Z; z++)
      for (int y = min.Y; y <= max.Y; y++)
        for (int x = min.X; x <= max.X; x++)
        {
          if (_grid.TryGetValue(new Vector3I(x, y, z), out var list) && list.Count > 0 && list[^1] == e.Seq)
          {
            list.RemoveAt(list.Count - 1);
          }
        }
  }

  /// <summary>Copies every edit, in Seq order.</summary>
  public void CopyAll(List<SphereEdit> output)
  {
    output.Clear();
    lock (_lock) output.AddRange(_edits);
  }

  private static Vector3I CellOf(Vector3 p) => (Vector3I)(p / CellSize).Floor();
}
