#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Godot;
using static Voxel.VoxelConst;

namespace Voxel;

/// <summary>
/// Samples for one chunk in plain managed arrays: samples -1 .. Cells+1 on each axis, spaced key.Step apart.
/// Raw = noise only, Density = Raw with edits applied, Colors = lazily computed packed RGBA (0 = not yet computed).
/// </summary>
public sealed class VoxelVolume
{
  public readonly float[] Raw = new float[VolumeSize];
  public readonly float[] Density = new float[VolumeSize];
  public readonly uint[] Colors = new uint[VolumeSize];

  public ChunkKey Key;

  /// <summary>Newest edit Seq already applied to Density.</summary>
  public int AppliedEditSeq = -1;

  /// <summary>Transition face grid size: samples per axis at half-cell spacing.</summary>
  public const int FaceG = 2 * Cells + 1;

  /// <summary>
  /// LOD &gt; 0 only: the six faces sampled at half-cell spacing (the finer neighbour's resolution) for the
  /// Transvoxel strips. Only odd grid points are stored here (even ones are volume samples). Kept with the volume
  /// and edited incrementally like Density, so a cached chunk's strips cost no noise and no edit replay.
  /// </summary>
  public float[]? FaceRaw;
  public float[]? FaceDensity;
  public bool HasFaces;
  /// <summary>
  /// Raw noise half a cell either side of each face point (index * 2 + 0 = outside, + 1 = inside), for the strips'
  /// normals. Filled on first use (NaN = not yet), since only points near the surface need it.
  /// </summary>
  public float[]? FaceDepthRaw;
  /// <summary>Packed colours of the odd face points, computed on first use (0 = not yet), like Colors.</summary>
  public uint[]? FaceColors;

  public void Generate(TerrainGenerator gen, ChunkKey key)
  {
    Key = key;
    AppliedEditSeq = -1;
    Array.Clear(Colors);

    var origin = key.Origin;
    int step = key.Step;
    int i = 0;
    for (int z = -1; z <= Cells + 1; z++)
    {
      float wz = origin.Z + z * step;
      for (int y = -1; y <= Cells + 1; y++)
      {
        float wy = origin.Y + y * step;
        for (int x = -1; x <= Cells + 1; x++, i++)
        {
          float d = gen.RawDensity(origin.X + x * step, wy, wz);
          Raw[i] = d;
          Density[i] = d;
        }
      }
    }

    HasFaces = key.Lod > 0;
    if (HasFaces)
    {
      FaceRaw ??= new float[6 * FaceG * FaceG];
      FaceDensity ??= new float[6 * FaceG * FaceG];
      FaceDepthRaw ??= new float[6 * FaceG * FaceG * 2];
      FaceColors ??= new uint[6 * FaceG * FaceG];
      Array.Clear(FaceColors);
      Array.Fill(FaceDepthRaw, float.NaN);
      for (int f = 0; f < 6; f++)
        for (int b = 0; b < FaceG; b++)
          for (int a = (b & 1) == 0 ? 1 : 0; a < FaceG; a += (b & 1) == 0 ? 2 : 1)
          {
            var p = FacePoint(f, a, b);
            float d = gen.RawDensity(p.X, p.Y, p.Z);
            int fi = (f * FaceG + b) * FaceG + a;
            FaceRaw![fi] = d;
            FaceDensity![fi] = d;
          }
    }
  }

  /// <summary>World-space (chunk manager local) position of transition face point (a, b) in half cells.</summary>
  public Vector3 FacePoint(int f, int a, int b) => (Vector3)Key.Origin + TransitionMesher.CellPos(f, a, b) * Key.Step;

  /// <summary>Applies edits (sorted by Seq, all newer than AppliedEditSeq) to Density.</summary>
  public void ApplyEdits(List<SphereEdit> edits, int newestSeq)
  {
    foreach (var e in edits)
    {
      ApplyEdit(e);
      if (HasFaces) ApplyEditToFaces(e);
    }
    AppliedEditSeq = Math.Max(AppliedEditSeq, newestSeq);
  }

  /// <summary>
  /// One edit over the samples inside its sphere: rows outside are skipped, and each row only spans the sphere's
  /// chord. Same arithmetic as SphereEdit.Apply (positions are exact integers, d2 summed in the same order).
  /// </summary>
  [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
  private void ApplyEdit(SphereEdit e)
  {
    var origin = (Vector3)Key.Origin;
    float step = Key.Step;
    float r = e.Radius, r2 = r * r;
    float cx = e.Center.X, cy = e.Center.Y, cz = e.Center.Z;
    int x0 = Math.Max(-1, (int)MathF.Floor((cx - r - origin.X) / step)), x1 = Math.Min(Cells + 1, (int)MathF.Ceiling((cx + r - origin.X) / step));
    int y0 = Math.Max(-1, (int)MathF.Floor((cy - r - origin.Y) / step)), y1 = Math.Min(Cells + 1, (int)MathF.Ceiling((cy + r - origin.Y) / step));
    int z0 = Math.Max(-1, (int)MathF.Floor((cz - r - origin.Z) / step)), z1 = Math.Min(Cells + 1, (int)MathF.Ceiling((cz + r - origin.Z) / step));
    if (x0 > x1 || y0 > y1 || z0 > z1) return;
    var density = Density;
    var raw = Raw;
    var colors = Colors;
    for (int z = z0; z <= z1; z++)
    {
      float dz = origin.Z + z * step - cz;
      float dz2 = dz * dz;
      if (dz2 >= r2) continue;
      for (int y = y0; y <= y1; y++)
      {
        float dy = origin.Y + y * step - cy;
        float dy2 = dy * dy;
        float rem = r2 - dy2 - dz2;
        if (rem <= 0) continue;
        float w = MathF.Sqrt(rem);
        int xa = Math.Max(x0, (int)MathF.Floor((cx - w - origin.X) / step) - 1);
        int xb = Math.Min(x1, (int)MathF.Ceiling((cx + w - origin.X) / step) + 1);
        int idx = Index(xa, y, z);
        for (int x = xa; x <= xb; x++, idx++)
        {
          float dx = origin.X + x * step - cx;
          float before = density[idx];
          float after = e.Apply(dx * dx + dy2 + dz2, before);
          density[idx] = after;
          // Colour follows max(raw, density) (see ColorAt): recompute where a build (or a dig of built material)
          // changed it.
          if (after != before && (e.Build || before > raw[idx])) colors[idx] = 0;
        }
      }
    }
  }

  private static readonly int[] FaceAxisU = [1, 2, 2, 0, 0, 1];
  private static readonly int[] FaceAxisV = [2, 1, 0, 2, 1, 0];

  /// <summary>One edit over the odd transition face points inside its sphere.</summary>
  private void ApplyEditToFaces(SphereEdit e)
  {
    var origin = (Vector3)Key.Origin;
    float step = Key.Step, half = step * 0.5f;
    float r = e.Radius;
    var faceDensity = FaceDensity!;
    for (int f = 0; f < 6; f++)
    {
      int axis = f >> 1;
      float plane = origin[axis] + ((f & 1) == 0 ? 0 : Cells * step);
      if (MathF.Abs(e.Center[axis] - plane) >= r) continue;
      int ua = FaceAxisU[f], va = FaceAxisV[f];
      int a0 = Math.Max(0, (int)MathF.Floor((e.Center[ua] - r - origin[ua]) / half)), a1 = Math.Min(FaceG - 1, (int)MathF.Ceiling((e.Center[ua] + r - origin[ua]) / half));
      int b0 = Math.Max(0, (int)MathF.Floor((e.Center[va] - r - origin[va]) / half)), b1 = Math.Min(FaceG - 1, (int)MathF.Ceiling((e.Center[va] + r - origin[va]) / half));
      for (int b = b0; b <= b1; b++)
        for (int a = a0; a <= a1; a++)
        {
          if (((a | b) & 1) == 0) continue;
          int fi = (f * FaceG + b) * FaceG + a;
          float before = faceDensity[fi];
          float after = e.Apply(FacePoint(f, a, b), before);
          faceDensity[fi] = after;
          if (after != before && (e.Build || before > FaceRaw![fi])) FaceColors![fi] = 0;
        }
    }
  }

  /// <summary>Whether the first meshed sample is air (for a volume without surface: all of it is air).</summary>
  public bool IsAirAtOrigin => Density[Index(0, 0, 0)] < Iso;

  /// <summary>True if the meshed range (samples 0 .. Cells) contains both solid and air.</summary>
  public bool HasSurface()
  {
    bool solid = false, air = false;
    for (int z = 0; z <= Cells; z++)
      for (int y = 0; y <= Cells; y++)
      {
        int idx = Index(0, y, z);
        for (int x = 0; x <= Cells; x++, idx++)
        {
          if (Density[idx] >= Iso) solid = true; else air = true;
          if (solid && air) return true;
        }
      }
    return false;
  }

  public Vector3 Gradient(int idx)
  {
    var d = Density;
    return new Vector3(d[idx + 1] - d[idx - 1], d[idx + Dim] - d[idx - Dim], d[idx + DimSq] - d[idx - DimSq]);
  }

  /// <summary>
  /// Density gradient per world unit at a chunk-local position (trilinear over the per-sample central
  /// differences). Only uses samples of the cell containing p and their direct neighbours, so two chunks agree
  /// exactly at their shared face.
  /// </summary>
  public Vector3 SampleGradient(Vector3 local)
  {
    int i = Cell(local, out float fx, out float fy, out float fz);
    var c00 = L(Gradient(i), Gradient(i + 1), fx);
    var c10 = L(Gradient(i + Dim), Gradient(i + Dim + 1), fx);
    var c01 = L(Gradient(i + DimSq), Gradient(i + DimSq + 1), fx);
    var c11 = L(Gradient(i + Dim + DimSq), Gradient(i + Dim + DimSq + 1), fx);
    var v = L(L(c00, c10, fy), L(c01, c11, fy), fz);
    return v / (2f * Key.Step);
  }

  /// <summary>Density at a chunk-local position (trilinear over the samples, clamped to the meshed range).</summary>
  public float SampleDensity(Vector3 local)
  {
    int i = Cell(local, out float fx, out float fy, out float fz);
    var d = Density;
    float c00 = L(d[i], d[i + 1], fx), c10 = L(d[i + Dim], d[i + Dim + 1], fx);
    float c01 = L(d[i + DimSq], d[i + DimSq + 1], fx), c11 = L(d[i + Dim + DimSq], d[i + Dim + DimSq + 1], fx);
    return L(L(c00, c10, fy), L(c01, c11, fy), fz);
  }

  /// <summary>Base sample index and fractions of the cell holding local (in-range, so neighbours exist).</summary>
  private int Cell(Vector3 local, out float fx, out float fy, out float fz)
  {
    var g = local / Key.Step;
    int x0 = Math.Clamp((int)MathF.Floor(g.X), 0, Cells - 1);
    int y0 = Math.Clamp((int)MathF.Floor(g.Y), 0, Cells - 1);
    int z0 = Math.Clamp((int)MathF.Floor(g.Z), 0, Cells - 1);
    fx = Math.Clamp(g.X - x0, 0f, 1f);
    fy = Math.Clamp(g.Y - y0, 0f, 1f);
    fz = Math.Clamp(g.Z - z0, 0f, 1f);
    return Index(x0, y0, z0);
  }

  // Lerps that return an endpoint exactly at t = 0 or 1, so a point on a chunk face reads exactly the face samples
  // (shared with the neighbour) from either side.
  private static float L(float a, float b, float t) => t == 1f ? b : a + (b - a) * t;
  private static Vector3 L(Vector3 a, Vector3 b, float t) => t == 1f ? b : a + (b - a) * t;

  public Color ColorAt(TerrainGenerator gen, int idx, int x, int y, int z)
  {
    uint c = Colors[idx];
    if (c == 0)
    {
      var p = (Vector3)Key.Origin + new Vector3(x, y, z) * Key.Step;
      // Built material has no natural density there, so colour by whichever is more solid: plain and dug terrain
      // keep their natural colour, built terrain gets the colour of solid ground.
      c = TerrainGenerator.Pack(gen.ColorAt(p, Math.Max(Raw[idx], Density[idx])));
      Colors[idx] = c;
    }
    return TerrainGenerator.Unpack(c);
  }
}

/// <summary>
/// Pool of volumes plus an LRU cache of recent ones (used for LOD0 chunks near the player, so digs skip regeneration).
/// Volumes are checked out exclusively: Take removes from the cache, Return puts back.
/// </summary>
public sealed class VolumeCache
{
  private readonly object _lock = new();
  private readonly Dictionary<ChunkKey, LinkedListNode<VoxelVolume>> _map = [];
  private readonly LinkedList<VoxelVolume> _lru = new();
  private readonly ConcurrentBag<VoxelVolume> _free = [];
  public int Capacity;

  public VolumeCache(int capacity) => Capacity = capacity;

  public int Count
  {
    get { lock (_lock) return _map.Count; }
  }

  public VoxelVolume? Take(ChunkKey key)
  {
    lock (_lock)
    {
      if (!_map.Remove(key, out var node))
      {
        return null;
      }
      _lru.Remove(node);
      return node.Value;
    }
  }

  public VoxelVolume Rent() => _free.TryTake(out var v) ? v : new VoxelVolume();

  public void Release(VoxelVolume v) => _free.Add(v);

  public void Return(VoxelVolume v, bool keep)
  {
    if (!keep || Capacity <= 0)
    {
      Release(v);
      return;
    }
    lock (_lock)
    {
      if (_map.Remove(v.Key, out var old))
      {
        _lru.Remove(old);
        if (old.Value != v) _free.Add(old.Value);
      }
      _map[v.Key] = _lru.AddFirst(v);
      while (_map.Count > Capacity)
      {
        var last = _lru.Last!;
        _lru.RemoveLast();
        _map.Remove(last.Value.Key);
        _free.Add(last.Value);
      }
    }
  }

  public void Drop(ChunkKey key)
  {
    var v = Take(key);
    if (v != null) Release(v);
  }
}
