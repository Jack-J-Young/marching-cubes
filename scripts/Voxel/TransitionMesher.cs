using System;
using System.Collections.Generic;
using Godot;
using static Voxel.VoxelConst;
using static Voxel.TransvoxelTables;

namespace Voxel;

/// <summary>
/// Transvoxel transition cells (Lengyel 2010, chapter 4). For a chunk at LOD L &gt; 0, all six faces get a strip of
/// transition cells whose outer side is sampled at the finer LOD L-1 resolution, so it matches a finer neighbor.
/// Every face is built up front. The shader shows a face's strip (and shrinks the regular boundary cells)
/// only when that face's bit is set in the chunk's lod_mask, so neighbor LOD changes never need a remesh.
/// </summary>
public static class TransitionMesher
{
  private const int G = 2 * Cells + 1; // face samples per axis at half-cell spacing

  // Per-face frame in cell coordinates: P(a, b) = Origin + a * U + b * V, with U x V pointing into the chunk.
  // Face f: axis = f >> 1, side = f & 1 (0 = low/negative, 1 = high/positive). Matches lod_mask bit f.
  private static readonly Vector3I[] FaceOrigin =
  [
    new(0, 0, 0), new(Cells, 0, 0),
    new(0, 0, 0), new(0, Cells, 0),
    new(0, 0, 0), new(0, 0, Cells),
  ];
  private static readonly Vector3I[] FaceU = [Vector3I.Up, Vector3I.Back, Vector3I.Back, Vector3I.Right, Vector3I.Right, Vector3I.Up];
  private static readonly Vector3I[] FaceV = [Vector3I.Back, Vector3I.Up, Vector3I.Right, Vector3I.Back, Vector3I.Up, Vector3I.Right];

  public static readonly bool FlipWinding = ComputeFlip();

  public sealed class Scratch
  {
    public readonly float[] Density = new float[G * G];
    public readonly float[] Raw = new float[G * G];
    public readonly uint[] Colors = new uint[G * G];
    public readonly Vector3[] Gradients = new Vector3[G * G];
    public readonly bool[] HasGradient = new bool[G * G];
    public readonly Dictionary<long, int> Verts = [];
    public readonly List<SphereEdit> FaceEdits = [];
  }

  public static void Mesh(VoxelVolume vol, TerrainGenerator gen, List<SphereEdit> edits, MeshBuffers buf, Scratch s)
  {
    float step = vol.Key.Step;
    var origin = (Vector3)vol.Key.Origin;
    for (int f = 0; f < 6; f++)
    {
      // Only edits near this face can change what's sampled for it (on it, and a cell either side for gradients):
      // a chunk with lots of digs inside it would otherwise apply all of them to every seam sample.
      var p0 = origin + (Vector3)FaceOrigin[f] * step;
      var p1 = p0 + (Vector3)(FaceU[f] + FaceV[f]) * (Cells * step);
      var box = new Aabb(p0, Vector3.Zero).Expand(p1).Grow(step * 1.5f);
      s.FaceEdits.Clear();
      foreach (var e in edits)
        if (VoxelMath.SphereIntersectsAabb(e.Center, e.Radius, box)) s.FaceEdits.Add(e);
      MeshFace(f, vol, gen, s.FaceEdits, buf, s);
    }
  }

  private static void MeshFace(int f, VoxelVolume vol, TerrainGenerator gen, List<SphereEdit> edits, MeshBuffers buf, Scratch s)
  {
    var key = vol.Key;
    float step = key.Step;
    var chunkOrigin = (Vector3)key.Origin;
    var fo = FaceOrigin[f];
    var fu = FaceU[f];
    var fv = FaceV[f];

    // Sample the face at half-cell spacing. Even grid points are the volume's own samples.
    bool solid = false, air = false;
    for (int b = 0; b < G; b++)
      for (int a = 0; a < G; a++)
      {
        int gi = a + b * G;
        float dv, rv;
        if (((a | b) & 1) == 0)
        {
          var c = fo + fu * (a >> 1) + fv * (b >> 1);
          int vi = Index(c.X, c.Y, c.Z);
          dv = vol.Density[vi];
          rv = vol.Raw[vi];
        }
        else if (vol.HasFaces)
        {
          // Cached with the volume (noise once, edits applied incrementally).
          int fi = (f * G + b) * G + a;
          dv = vol.FaceDensity![fi];
          rv = vol.FaceRaw![fi];
        }
        else
        {
          var p = chunkOrigin + CellPos(f, a, b) * step;
          rv = gen.RawDensity(p.X, p.Y, p.Z);
          dv = rv;
          foreach (var e in edits) dv = e.Apply(p, dv);
        }
        s.Density[gi] = dv;
        s.Raw[gi] = rv;
        s.Colors[gi] = 0;
        s.HasGradient[gi] = false;
        if (dv >= Iso) solid = true; else air = true;
      }
    if (!(solid && air))
    {
      return;
    }

    s.Verts.Clear();
    int flags = (f + 1) << 6;
    Span<int> grid = stackalloc int[9];
    Span<int> cellVerts = stackalloc int[12];

    for (int cb = 0; cb < Cells; cb++)
      for (int ca = 0; ca < Cells; ca++)
      {
        for (int k = 0; k < 9; k++)
        {
          grid[k] = (2 * ca + k % 3) + (2 * cb + k / 3) * G;
        }

        int caseCode =
            (Bit(s, grid[0]) << 0) | (Bit(s, grid[1]) << 1) | (Bit(s, grid[2]) << 2)
          | (Bit(s, grid[5]) << 3) | (Bit(s, grid[8]) << 4) | (Bit(s, grid[7]) << 5)
          | (Bit(s, grid[6]) << 6) | (Bit(s, grid[3]) << 7) | (Bit(s, grid[4]) << 8);
        if (caseCode == 0 || caseCode == 511)
        {
          continue;
        }

        int classByte = TransitionCellClass[caseCode];
        int cls = classByte & 0x7F;
        bool flip = ((classByte & 0x80) != 0) ^ FlipWinding;
        int geom = TransitionCellGeometry[cls];
        int vertexCount = geom >> 4;
        int triCount = geom & 0x0F;

        for (int i = 0; i < vertexCount; i++)
        {
          int code = TransitionVertexData[caseCode * 12 + i];
          int e0 = (code >> 4) & 0x0F;
          int e1 = code & 0x0F;

          int vi;
          if (e0 >= 9 && e1 >= 9)
          {
            // Low-res (back) face: a coarse volume edge, emitted exactly like the regular mesher's vertex.
            int g0 = grid[LowResToFull(e0)];
            int g1 = grid[LowResToFull(e1)];
            var c0 = fo + fu * ((g0 % G) >> 1) + fv * ((g0 / G) >> 1);
            var c1 = fo + fu * ((g1 % G) >> 1) + fv * ((g1 / G) >> 1);
            var low = new Vector3I(Math.Min(c0.X, c1.X), Math.Min(c0.Y, c1.Y), Math.Min(c0.Z, c1.Z));
            var diff = (c1 - c0).Abs();
            int axis = diff.X != 0 ? 0 : diff.Y != 0 ? 1 : 2;
            long vkey = -1L - (Index(low.X, low.Y, low.Z) * 3L + axis);
            if (!s.Verts.TryGetValue(vkey, out vi))
            {
              vi = MarchingCubesMesher.EmitVolumeEdgeVertex(vol, gen, buf, low, axis, flags);
              s.Verts[vkey] = vi;
            }
          }
          else
          {
            // Full-res face: edge between two half-step samples on the face plane.
            int g0 = grid[e0 >= 9 ? LowResToFull(e0) : e0];
            int g1 = grid[e1 >= 9 ? LowResToFull(e1) : e1];
            if (g0 > g1) (g0, g1) = (g1, g0);
            long vkey = (long)g0 * (G * G) + g1;
            if (!s.Verts.TryGetValue(vkey, out vi))
            {
              vi = EmitFaceVertex(f, g0, g1, vol, gen, edits, buf, s, flags);
              s.Verts[vkey] = vi;
            }
          }
          cellVerts[i] = vi;
        }

        int ti = cls * 36;
        for (int t = 0; t < triCount; t++, ti += 3)
        {
          int a = cellVerts[TransitionCellIndices[ti]];
          int b = cellVerts[TransitionCellIndices[ti + 1]];
          int c = cellVerts[TransitionCellIndices[ti + 2]];
          if (flip) buf.AddTriangle(a, c, b); else buf.AddTriangle(a, b, c);
        }
      }
  }

  private static int EmitFaceVertex(int f, int g0, int g1, VoxelVolume vol, TerrainGenerator gen, List<SphereEdit> edits,
    MeshBuffers buf, Scratch s, int flags)
  {
    var key = vol.Key;
    float step = key.Step;
    float t = MarchingCubesMesher.Interp(s.Density[g0], s.Density[g1]);
    var p0 = CellPos(f, g0 % G, g0 / G);
    var p1 = CellPos(f, g1 % G, g1 / G);
    var cellPos = p0 + (p1 - p0) * t;

    var n0 = FaceGradient(f, g0, vol, gen, edits, s);
    var n1 = FaceGradient(f, g1, vol, gen, edits, s);
    var n = MarchingCubesMesher.SafeNormal(-(n0 + (n1 - n0) * t));

    var col = FaceColor(f, g0, vol, gen, s).Lerp(FaceColor(f, g1, vol, gen, s), t);

    // On the full-res face the vertex stays on the face plane; only in-plane offsets apply (at chunk edges).
    var offset = MarchingCubesMesher.SecondaryOffset(cellPos, step, f >> 1, out int bits);
    return buf.AddVertex(cellPos * step, n, col, offset, bits | flags);
  }

  /// <summary>
  /// Central-difference gradient at the finer LOD's spacing (half a cell), the same value the finer
  /// neighbor computes from its own samples, so shading matches across the seam.
  /// </summary>
  private static Vector3 FaceGradient(int f, int g, VoxelVolume vol, TerrainGenerator gen, List<SphereEdit> edits, Scratch s)
  {
    if (s.HasGradient[g]) return s.Gradients[g];
    float step = vol.Key.Step;
    float h = step * 0.5f;
    int a = g % G, b = g / G;
    var p = (Vector3)vol.Key.Origin + CellPos(f, a, b) * step;
    var u = (Vector3)FaceU[f];
    var v = (Vector3)FaceV[f];
    var n = (Vector3)AxisUnit(f >> 1);

    // In-plane differences come straight from the face grid (it is sampled at exactly +-h); only the
    // depth axis, and the grid border, need fresh samples.
    float du = a > 0 && a < G - 1
      ? s.Density[g + 1] - s.Density[g - 1]
      : gen.Density(p + u * h, edits) - gen.Density(p - u * h, edits);
    float dv = b > 0 && b < G - 1
      ? s.Density[g + G] - s.Density[g - G]
      : gen.Density(p + v * h, edits) - gen.Density(p - v * h, edits);
    float dn = vol.HasFaces
      ? DepthDensity(vol, gen, edits, f, g, p + n * h, 1) - DepthDensity(vol, gen, edits, f, g, p - n * h, 0)
      : gen.Density(p + n * h, edits) - gen.Density(p - n * h, edits);
    var grad = u * du + v * dv + n * dn;
    s.Gradients[g] = grad;
    s.HasGradient[g] = true;
    return grad;
  }

  /// <summary>Density half a cell off the face (side 1 = inward): cached noise, edits applied on the spot.</summary>
  private static float DepthDensity(VoxelVolume vol, TerrainGenerator gen, List<SphereEdit> edits, int f, int g, Vector3 p, int side)
  {
    int i = (f * G * G + g) * 2 + side;
    float d = vol.FaceDepthRaw![i];
    if (float.IsNaN(d))
    {
      d = gen.RawDensity(p.X, p.Y, p.Z);
      vol.FaceDepthRaw[i] = d;
    }
    foreach (var e in edits) d = e.Apply(p, d);
    return d;
  }

  private static Color FaceColor(int f, int g, VoxelVolume vol, TerrainGenerator gen, Scratch s)
  {
    if (vol.HasFaces)
    {
      // Cached with the volume: even points are volume samples, odd points live in FaceColors.
      int a = g % G, b = g / G;
      if (((a | b) & 1) == 0)
      {
        var cp = FaceOrigin[f] + FaceU[f] * (a >> 1) + FaceV[f] * (b >> 1);
        return vol.ColorAt(gen, Index(cp.X, cp.Y, cp.Z), cp.X, cp.Y, cp.Z);
      }
      int fi = f * G * G + g;
      uint fc = vol.FaceColors![fi];
      if (fc == 0)
      {
        var p = vol.FacePoint(f, a, b);
        fc = TerrainGenerator.Pack(gen.ColorAt(p, Math.Max(vol.FaceRaw![fi], vol.FaceDensity![fi])));
        vol.FaceColors[fi] = fc;
      }
      return TerrainGenerator.Unpack(fc);
    }
    uint c = s.Colors[g];
    if (c == 0)
    {
      var p = (Vector3)vol.Key.Origin + CellPos(f, g % G, g / G) * vol.Key.Step;
      c = TerrainGenerator.Pack(gen.ColorAt(p, Math.Max(s.Raw[g], s.Density[g])));
      s.Colors[g] = c;
    }
    return TerrainGenerator.Unpack(c);
  }

  /// <summary>Position (in cells) of face grid point (a, b), where a and b count half cells.</summary>
  public static Vector3 CellPos(int f, int a, int b) =>
    (Vector3)FaceOrigin[f] + (Vector3)FaceU[f] * (a * 0.5f) + (Vector3)FaceV[f] * (b * 0.5f);

  private static Vector3I AxisUnit(int axis) => axis == 0 ? Vector3I.Right : axis == 1 ? Vector3I.Up : Vector3I.Back;

  private static int Bit(Scratch s, int gi) => CaseBit(s.Density[gi]) ? 1 : 0;

  /// <summary>Low-res samples 9, A, B, C have the same values and positions as full-res samples 0, 2, 6, 8.</summary>
  private static int LowResToFull(int e) => e switch { 9 => 0, 10 => 2, 11 => 6, _ => 8 };

  /// <summary>
  /// Works out the table's winding with real transition-cell geometry: full-res samples on the face plane,
  /// low-res samples pushed TransitionWidth inward, rows 0-1 solid and row 2 air. The solid side is -V, and
  /// cross(b - a, c - a) must point toward it (Godot front faces are clockwise).
  /// </summary>
  private static bool ComputeFlip()
  {
    // Frame of face 0: x = depth (inward), y = U, z = V.
    var pos = new Vector3[13];
    var dens = new float[13];
    for (int k = 0; k < 9; k++)
    {
      pos[k] = new Vector3(0, (k % 3) * 0.5f, (k / 3) * 0.5f);
      dens[k] = k < 6 ? 1f : 0f;
    }
    int[] lowMap = [0, 2, 6, 8];
    for (int k = 0; k < 4; k++)
    {
      pos[9 + k] = pos[lowMap[k]] + new Vector3(TransitionWidth, 0, 0);
      dens[9 + k] = dens[lowMap[k]];
    }

    int B(int k) => CaseBit(dens[k]) ? 1 : 0;
    int caseCode = B(0) | (B(1) << 1) | (B(2) << 2) | (B(5) << 3) | (B(8) << 4) | (B(7) << 5) | (B(6) << 6) | (B(3) << 7) | (B(4) << 8);
    int classByte = TransitionCellClass[caseCode];
    int cls = classByte & 0x7F;
    bool inv = (classByte & 0x80) != 0;
    int geom = TransitionCellGeometry[cls];

    var verts = new Vector3[12];
    for (int i = 0; i < (geom >> 4); i++)
    {
      int code = TransitionVertexData[caseCode * 12 + i];
      int e0 = (code >> 4) & 0x0F, e1 = code & 0x0F;
      float t = MarchingCubesMesher.Interp(dens[e0], dens[e1]);
      verts[i] = pos[e0] + (pos[e1] - pos[e0]) * t;
    }

    float sum = 0;
    var towardSolid = new Vector3(0, 0, -1);
    for (int tIdx = 0; tIdx < (geom & 0x0F); tIdx++)
    {
      var a = verts[TransitionCellIndices[cls * 36 + tIdx * 3]];
      var b = verts[TransitionCellIndices[cls * 36 + tIdx * 3 + 1]];
      var c = verts[TransitionCellIndices[cls * 36 + tIdx * 3 + 2]];
      if (inv) (b, c) = (c, b);
      sum += (b - a).Cross(c - a).Dot(towardSolid);
    }
    return sum < 0;
  }
}
