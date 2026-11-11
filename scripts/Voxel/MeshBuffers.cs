using System;
using Godot;

namespace Voxel;

/// <summary>Growable, reusable mesh output buffers. One per worker thread.</summary>
public sealed class MeshBuffers
{
  public Vector3[] Positions = new Vector3[8192];
  public Vector3[] Normals = new Vector3[8192];
  public Color[] Colors = new Color[8192];
  /// <summary>CUSTOM0 per vertex: xyz = Transvoxel secondary offset, w = flag bits (see terrain.gdshader).</summary>
  public float[] Custom = new float[8192 * 4];
  /// <summary>CUSTOM1 per vertex (only when HasMorph): offset from the new position back to the old surface.</summary>
  public float[] Morph = new float[8192 * 3];
  public bool HasMorph;
  public int[] Indices = new int[16384];
  public int VertexCount;
  public int IndexCount;

  /// <summary>Index count of the regular (non-transition) part; collision only uses this part.</summary>
  public int RegularIndexCount;

  public void Clear()
  {
    VertexCount = 0;
    IndexCount = 0;
    RegularIndexCount = 0;
    HasMorph = false;
  }

  public int AddVertex(Vector3 p, Vector3 n, Color c, Vector3 offset, int flags)
  {
    if (VertexCount == Positions.Length)
    {
      int cap = Positions.Length * 2;
      Array.Resize(ref Positions, cap);
      Array.Resize(ref Normals, cap);
      Array.Resize(ref Colors, cap);
      Array.Resize(ref Custom, cap * 4);
      Array.Resize(ref Morph, cap * 3);
    }
    int i = VertexCount++;
    Positions[i] = p;
    Normals[i] = n;
    Colors[i] = c;
    int ci = i * 4;
    Custom[ci] = offset.X;
    Custom[ci + 1] = offset.Y;
    Custom[ci + 2] = offset.Z;
    Custom[ci + 3] = flags;
    return i;
  }

  public void AddTriangle(int a, int b, int c)
  {
    if (IndexCount + 3 > Indices.Length)
    {
      Array.Resize(ref Indices, Indices.Length * 2);
    }
    Indices[IndexCount++] = a;
    Indices[IndexCount++] = b;
    Indices[IndexCount++] = c;
  }

  /// <summary>AddSurfaceFromArrays format flags matching ToSurfaceArrays.</summary>
  public static Mesh.ArrayFormat SurfaceFormat(bool morph)
  {
    long f = (long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift;
    if (morph) f |= (long)Mesh.ArrayCustomFormat.RgbFloat << (int)Mesh.ArrayFormat.FormatCustom1Shift;
    return (Mesh.ArrayFormat)f;
  }

  private Vector3[] _faces = new Vector3[16384];

  /// <summary>
  /// Builds the Godot surface arrays. Spans are copied straight into native packed arrays, so no managed
  /// garbage is created and the buffers can be reused immediately.
  /// </summary>
  public Godot.Collections.Array ToSurfaceArrays()
  {
    var arrays = new Godot.Collections.Array();
    arrays.Resize((int)Mesh.ArrayType.Max);
    arrays[(int)Mesh.ArrayType.Vertex] = Variant.CreateFrom(Positions.AsSpan(0, VertexCount));
    arrays[(int)Mesh.ArrayType.Normal] = Variant.CreateFrom(Normals.AsSpan(0, VertexCount));
    arrays[(int)Mesh.ArrayType.Color] = Variant.CreateFrom(Colors.AsSpan(0, VertexCount));
    arrays[(int)Mesh.ArrayType.Custom0] = Variant.CreateFrom(Custom.AsSpan(0, VertexCount * 4));
    if (HasMorph) arrays[(int)Mesh.ArrayType.Custom1] = Variant.CreateFrom(Morph.AsSpan(0, VertexCount * 3));
    arrays[(int)Mesh.ArrayType.Index] = Variant.CreateFrom(Indices.AsSpan(0, IndexCount));
    return arrays;
  }

  /// <summary>De-indexed triangle soup of the regular part as a PackedVector3Array, for ConcavePolygonShape3D.Data.</summary>
  public Variant ToCollisionFaces()
  {
    if (_faces.Length < RegularIndexCount) Array.Resize(ref _faces, RegularIndexCount * 2);
    for (int i = 0; i < RegularIndexCount; i++)
    {
      _faces[i] = Positions[Indices[i]];
    }
    return Variant.CreateFrom(_faces.AsSpan(0, RegularIndexCount));
  }
}
