using System;
using System.Collections.Generic;
using Godot;

namespace Voxel;

/// <summary>A spherical dig. Seq is its position in the global edit order (edits are order-dependent).</summary>
public readonly record struct SphereEdit(Vector3 Center, float Radius, int Seq)
{
  /// <summary>Same falloff as the old compute shader: density *= clamp(dist / radius, 0, 1).</summary>
  public float Apply(Vector3 p, float density)
  {
    float d = p.DistanceTo(Center) / Radius;
    return d < 1f ? density * d : density;
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

  public int Add(Vector3 center, float radius)
  {
    lock (_lock)
    {
      int seq = _edits.Count;
      _edits.Add(new SphereEdit(center, radius, seq));
      var min = CellOf(center - Vector3.One * radius);
      var max = CellOf(center + Vector3.One * radius);
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
            list.Add(seq);
          }
      return seq;
    }
  }

  /// <summary>
  /// Collects edits with Seq &gt; afterSeq that intersect box, sorted by Seq.
  /// Returns the newest Seq that existed at query time (pass it back as afterSeq next time).
  /// </summary>
  public int Query(Aabb box, int afterSeq, List<SphereEdit> output)
  {
    output.Clear();
    lock (_lock)
    {
      int newest = _edits.Count - 1;
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
              if (seq <= afterSeq)
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

  private static Vector3I CellOf(Vector3 p) => (Vector3I)(p / CellSize).Floor();
}
