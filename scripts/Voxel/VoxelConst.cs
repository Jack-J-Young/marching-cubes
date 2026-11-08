using Godot;

namespace Voxel;

public static class VoxelConst
{
  /// <summary>Cells per chunk edge (at every LOD).</summary>
  public const int Cells = 32;

  /// <summary>Samples stored per axis: -1 .. Cells+1 (one-sample apron on each side for gradients).</summary>
  public const int Dim = Cells + 3;
  public const int DimSq = Dim * Dim;
  public const int VolumeSize = Dim * Dim * Dim;

  /// <summary>Density at or above this is solid; below is air.</summary>
  public const float Iso = 0.5f;

  /// <summary>
  /// Case-code convention for the Transvoxel tables: a corner's bit is set when it is solid ("inside").
  /// Both meshers use this, so regular and transition cells always agree. (tests/VoxelSelfTest checks seams.)
  /// </summary>
  public static bool CaseBit(float density) => density >= Iso;

  /// <summary>Transvoxel transition-cell width as a fraction of a cell.</summary>
  public const float TransitionWidth = 0.5f;

  /// <summary>Array index of sample (x, y, z), where each coordinate is in -1 .. Cells+1.</summary>
  public static int Index(int x, int y, int z) => (x + 1) + (y + 1) * Dim + (z + 1) * DimSq;
}

/// <summary>Identifies an octree node / chunk: LOD level plus integer coordinate in units of that LOD's chunk size.</summary>
public readonly record struct ChunkKey(int Lod, Vector3I Coord)
{
  public int Step => 1 << Lod;
  public int Size => VoxelConst.Cells << Lod;
  public Vector3I Origin => Coord * Size;
  public Aabb Bounds => new(Origin, Vector3.One * Size);

  /// <summary>Bounds including the one-sample apron the volume reads.</summary>
  public Aabb PaddedBounds => new(Origin - Vector3.One * Step, Vector3.One * (Size + 2 * Step));

  public ChunkKey Parent => new(Lod + 1, new Vector3I(Coord.X >> 1, Coord.Y >> 1, Coord.Z >> 1));

  public ChunkKey Child(int i) =>
    new(Lod - 1, Coord * 2 + new Vector3I(i & 1, (i >> 1) & 1, (i >> 2) & 1));

  public static ChunkKey Containing(Vector3 p, int lod)
  {
    float size = VoxelConst.Cells << lod;
    return new ChunkKey(lod, (Vector3I)(p / size).Floor());
  }

  public override string ToString() => $"L{Lod}{Coord}";
}

public static class VoxelMath
{
  public static float DistanceToAabb(Vector3 p, Aabb box)
  {
    var min = box.Position;
    var max = box.End;
    float dx = Mathf.Max(Mathf.Max(min.X - p.X, 0), p.X - max.X);
    float dy = Mathf.Max(Mathf.Max(min.Y - p.Y, 0), p.Y - max.Y);
    float dz = Mathf.Max(Mathf.Max(min.Z - p.Z, 0), p.Z - max.Z);
    return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
  }

  public static bool SphereIntersectsAabb(Vector3 c, float r, Aabb box) => DistanceToAabb(c, box) <= r;
}
