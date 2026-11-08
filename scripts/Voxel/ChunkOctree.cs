using System.Collections.Generic;
using System.Diagnostics;
using Godot;

namespace Voxel;

public sealed class OctreeResult
{
  public required HashSet<ChunkKey> Leaves;
  /// <summary>Per leaf with LOD &gt; 0: bit f set if the neighbor across face f is one LOD finer.</summary>
  public required Dictionary<ChunkKey, int> Masks;
  public required Vector3 Viewer;
  public double Milliseconds;
}

/// <summary>
/// Chunk-level octree: a cube of root nodes (LOD = MaxLod) around the viewer, each split while the viewer is closer
/// than SplitFactor * nodeSize. Leaves are the chunks to show, and every leaf costs the same to build whatever its LOD.
/// The result is 2:1 balanced (face neighbors differ by at most one LOD), which Transvoxel needs.
/// Pure function of its inputs, so it runs off the main thread.
/// </summary>
public static class ChunkOctree
{
  public static OctreeResult Build(Vector3 viewer, int maxLod, float splitFactor, int rootRadius)
  {
    var sw = Stopwatch.StartNew();
    var leaves = new HashSet<ChunkKey>();
    var center = ChunkKey.Containing(viewer, maxLod).Coord;
    for (int z = -rootRadius; z <= rootRadius; z++)
      for (int y = -rootRadius; y <= rootRadius; y++)
        for (int x = -rootRadius; x <= rootRadius; x++)
        {
          Split(new ChunkKey(maxLod, center + new Vector3I(x, y, z)), viewer, splitFactor, leaves);
        }

    float rootSize = VoxelConst.Cells << maxLod;
    var regionMin = (Vector3)(center - Vector3I.One * rootRadius) * rootSize;
    var region = new Aabb(regionMin, Vector3.One * rootSize * (2 * rootRadius + 1));

    Balance(leaves, maxLod, region);

    var masks = new Dictionary<ChunkKey, int>();
    foreach (var leaf in leaves)
    {
      if (leaf.Lod > 0) masks[leaf] = NeighborMask(leaves, leaf, maxLod, region);
    }

    return new OctreeResult { Leaves = leaves, Masks = masks, Viewer = viewer, Milliseconds = sw.Elapsed.TotalMilliseconds };
  }

  private static void Split(ChunkKey key, Vector3 viewer, float splitFactor, HashSet<ChunkKey> leaves)
  {
    if (key.Lod > 0 && VoxelMath.DistanceToAabb(viewer, key.Bounds) < splitFactor * key.Size)
    {
      for (int i = 0; i < 8; i++) Split(key.Child(i), viewer, splitFactor, leaves);
    }
    else
    {
      leaves.Add(key);
    }
  }

  /// <summary>Splits leaves until no leaf has a face neighbor two or more LODs finer.</summary>
  private static void Balance(HashSet<ChunkKey> leaves, int maxLod, Aabb region)
  {
    var work = new List<ChunkKey>();
    bool changed = true;
    while (changed)
    {
      changed = false;
      work.Clear();
      foreach (var leaf in leaves)
      {
        if (leaf.Lod >= 2) work.Add(leaf);
      }
      foreach (var leaf in work)
      {
        if (!HasMuchFinerNeighbor(leaves, leaf, maxLod, region)) continue;
        leaves.Remove(leaf);
        for (int i = 0; i < 8; i++) leaves.Add(leaf.Child(i));
        changed = true;
      }
    }
  }

  private static bool HasMuchFinerNeighbor(HashSet<ChunkKey> leaves, ChunkKey leaf, int maxLod, Aabb region)
  {
    // Probe a 4x4 grid just across each face: anything two LODs finer covers at least one probe.
    float size = leaf.Size;
    for (int f = 0; f < 6; f++)
      for (int j = 0; j < 4; j++)
        for (int i = 0; i < 4; i++)
        {
          var p = AcrossFace(leaf, f, (i + 0.5f) / 4f, (j + 0.5f) / 4f, size / 8f);
          if (!region.HasPoint(p)) continue;
          if (!CoveredAtOrAbove(leaves, p, leaf.Lod - 1, maxLod)) return true;
        }
    return false;
  }

  private static int NeighborMask(HashSet<ChunkKey> leaves, ChunkKey leaf, int maxLod, Aabb region)
  {
    int mask = 0;
    for (int f = 0; f < 6; f++)
    {
      // After balancing, a face's neighbors are all the same LOD, so one probe is enough.
      var p = AcrossFace(leaf, f, 0.25f, 0.25f, leaf.Size / 4f);
      if (!region.HasPoint(p)) continue;
      if (leaves.Contains(ChunkKey.Containing(p, leaf.Lod - 1))) mask |= 1 << f;
    }
    return mask;
  }

  /// <summary>True if a leaf at LOD &gt;= minLod contains p.</summary>
  private static bool CoveredAtOrAbove(HashSet<ChunkKey> leaves, Vector3 p, int minLod, int maxLod)
  {
    for (int l = minLod; l <= maxLod; l++)
    {
      if (leaves.Contains(ChunkKey.Containing(p, l))) return true;
    }
    return false;
  }

  /// <summary>Point at in-face coordinates (s, t) in [0, 1], `depth` outside face f of the leaf.</summary>
  private static Vector3 AcrossFace(ChunkKey leaf, int f, float s, float t, float depth)
  {
    var b = leaf.Bounds;
    int axis = f >> 1;
    int ua = (axis + 1) % 3;
    int va = (axis + 2) % 3;
    var p = Vector3.Zero;
    p[axis] = (f & 1) == 0 ? b.Position[axis] - depth : b.End[axis] + depth;
    p[ua] = b.Position[ua] + s * b.Size[ua];
    p[va] = b.Position[va] + t * b.Size[va];
    return p;
  }
}
