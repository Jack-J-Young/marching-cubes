using System;
using Godot;
using static Voxel.VoxelConst;
using static Voxel.TransvoxelTables;

namespace Voxel;

/// <summary>
/// Regular-cell marching cubes over a VoxelVolume, using the Transvoxel "modified marching cubes" tables
/// so the triangulation is consistent with the transition cells in TransitionMesher.
/// Pure managed code; one call per chunk on a worker thread.
///
/// Vertices on a grid edge are shared through an edge cache (indexed mesh), normals come from the
/// density gradient (smooth shading), and each vertex carries a Transvoxel secondary offset in CUSTOM0
/// so the shader can shrink boundary cells when a neighbor is finer.
/// </summary>
public static class MarchingCubesMesher
{
  /// <summary>Sample-index offset of each cell corner (corner n = x | y&lt;&lt;1 | z&lt;&lt;2).</summary>
  private static readonly int[] CornerOffset = BuildCornerOffsets();

  /// <summary>True if the table winding has to be reversed to get Godot front faces facing air.</summary>
  public static readonly bool FlipWinding = ComputeFlip();

  public static int EdgeCacheSize => VolumeSize * 3;

  public static void Mesh(VoxelVolume vol, TerrainGenerator gen, MeshBuffers buf, int[] edgeCache)
  {
    Array.Fill(edgeCache, -1);
    var dens = vol.Density;
    Span<float> d = stackalloc float[8];
    Span<int> cellVerts = stackalloc int[12];

    for (int z = 0; z < Cells; z++)
      for (int y = 0; y < Cells; y++)
        for (int x = 0; x < Cells; x++)
        {
          int baseIdx = Index(x, y, z);
          int caseCode = 0;
          for (int n = 0; n < 8; n++)
          {
            float v = dens[baseIdx + CornerOffset[n]];
            d[n] = v;
            if (CaseBit(v)) caseCode |= 1 << n;
          }
          if (caseCode == 0 || caseCode == 255)
          {
            continue;
          }

          int cls = RegularCellClass[caseCode];
          int geom = RegularCellGeometry[cls];
          int vertexCount = geom >> 4;
          int triCount = geom & 0x0F;

          for (int i = 0; i < vertexCount; i++)
          {
            int code = RegularVertexData[caseCode * 12 + i];
            int c0 = (code >> 4) & 0x0F;
            int c1 = code & 0x0F;
            int lowCorner = c0 & c1;
            int axisBit = c0 ^ c1;
            int axis = axisBit == 1 ? 0 : axisBit == 2 ? 1 : 2;
            int lowIdx = baseIdx + CornerOffset[lowCorner];
            int edgeId = lowIdx * 3 + axis;

            int vi = edgeCache[edgeId];
            if (vi < 0)
            {
              var low = new Vector3I(x + (lowCorner & 1), y + ((lowCorner >> 1) & 1), z + ((lowCorner >> 2) & 1));
              vi = EmitVolumeEdgeVertex(vol, gen, buf, low, axis, 0);
              edgeCache[edgeId] = vi;
            }
            cellVerts[i] = vi;
          }

          int ti = cls * 15;
          for (int t = 0; t < triCount; t++, ti += 3)
          {
            int a = cellVerts[RegularCellIndices[ti]];
            int b = cellVerts[RegularCellIndices[ti + 1]];
            int c = cellVerts[RegularCellIndices[ti + 2]];
            if (FlipWinding) buf.AddTriangle(a, c, b); else buf.AddTriangle(a, b, c);
          }
        }

    buf.RegularIndexCount = buf.IndexCount;
  }

  private static readonly int[] AxisStride = [1, Dim, DimSq];
  private static readonly Vector3I[] AxisUnit = [Vector3I.Right, Vector3I.Up, Vector3I.Back];

  /// <summary>
  /// Emits the vertex on the volume edge from sample `low` to `low + axis`. Shared by the regular mesher and the
  /// low-res face of transition cells, so both produce bit-identical positions, normals and offsets.
  /// </summary>
  public static int EmitVolumeEdgeVertex(VoxelVolume vol, TerrainGenerator gen, MeshBuffers buf, Vector3I low, int axis, int extraFlags)
  {
    int lowIdx = Index(low.X, low.Y, low.Z);
    int highIdx = lowIdx + AxisStride[axis];
    var high = low + AxisUnit[axis];
    float d0 = vol.Density[lowIdx];
    float d1 = vol.Density[highIdx];
    float t = Interp(d0, d1);

    var cellPos = (Vector3)low + (Vector3)AxisUnit[axis] * t;
    float step = vol.Key.Step;

    var g0 = vol.Gradient(lowIdx);
    var g1 = vol.Gradient(highIdx);
    var n = SafeNormal(-(g0 + (g1 - g0) * t));

    var col0 = vol.ColorAt(gen, lowIdx, low.X, low.Y, low.Z);
    var col1 = vol.ColorAt(gen, highIdx, high.X, high.Y, high.Z);
    var col = col0.Lerp(col1, t);

    var offset = SecondaryOffset(cellPos, step, -1, out int bits);
    return buf.AddVertex(cellPos * step, n, col, offset, bits | extraFlags);
  }

  public static float Interp(float d0, float d1)
  {
    float denom = d1 - d0;
    if (Math.Abs(denom) < 1e-12f) return 0.5f;
    return Math.Clamp((Iso - d0) / denom, 0f, 1f);
  }

  public static Vector3 SafeNormal(Vector3 v)
  {
    float len = v.Length();
    return len > 1e-12f ? v / len : Vector3.Up;
  }

  /// <summary>
  /// Transvoxel secondary position offset (Lengyel, section 4.4): vertices within one cell of a chunk face are
  /// pulled inward so a transition strip fits when that face borders a finer chunk. The shader applies only the
  /// components whose face is active and projects the offset onto the tangent plane.
  /// bits: 1 &lt;&lt; (2*axis) for the low face, 1 &lt;&lt; (2*axis + 1) for the high face. skipAxis zeroes one axis.
  /// </summary>
  public static Vector3 SecondaryOffset(Vector3 cellPos, float step, int skipAxis, out int bits)
  {
    bits = 0;
    var off = Vector3.Zero;
    for (int a = 0; a < 3; a++)
    {
      if (a == skipAxis) continue;
      float u = cellPos[a];
      if (u < 1f)
      {
        off[a] = (1f - u) * TransitionWidth * step;
        bits |= 1 << (2 * a);
      }
      else if (u > Cells - 1)
      {
        off[a] = (Cells - 1 - u) * TransitionWidth * step;
        bits |= 1 << (2 * a + 1);
      }
    }
    return off;
  }

  private static int[] BuildCornerOffsets()
  {
    var o = new int[8];
    for (int n = 0; n < 8; n++)
    {
      o[n] = (n & 1) + ((n >> 1) & 1) * Dim + ((n >> 2) & 1) * DimSq;
    }
    return o;
  }

  /// <summary>
  /// Godot treats clockwise triangles as front faces, so cross(b - a, c - a) must point into the solid.
  /// Checks the table's convention on the single-solid-corner case.
  /// </summary>
  private static bool ComputeFlip()
  {
    // Only corner 0 solid.
    int caseCode = 0;
    for (int n = 0; n < 8; n++)
    {
      if (CaseBit(n == 0 ? 1f : 0f)) caseCode |= 1 << n;
    }
    int cls = RegularCellClass[caseCode];
    var verts = new Vector3[12];
    int vc = RegularCellGeometry[cls] >> 4;
    for (int i = 0; i < vc; i++)
    {
      int code = RegularVertexData[caseCode * 12 + i];
      verts[i] = (Corner((code >> 4) & 0x0F) + Corner(code & 0x0F)) * 0.5f;
    }
    var a = verts[RegularCellIndices[cls * 15]];
    var b = verts[RegularCellIndices[cls * 15 + 1]];
    var c = verts[RegularCellIndices[cls * 15 + 2]];
    var cross = (b - a).Cross(c - a);
    var towardSolid = Corner(0) - (a + b + c) / 3f;
    return cross.Dot(towardSolid) < 0;
  }

  private static Vector3 Corner(int n) => new(n & 1, (n >> 1) & 1, (n >> 2) & 1);
}
