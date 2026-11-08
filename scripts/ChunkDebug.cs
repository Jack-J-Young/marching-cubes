#nullable enable
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using Voxel;

/// <summary>
/// Editor tool: builds one chunk synchronously with the same pipeline as the game, for inspecting the mesher.
/// Set Lod &gt; 0 and LodMask (bit f = face f has a finer neighbor) to preview Transvoxel seams.
/// </summary>
[Tool]
public partial class ChunkDebug : Node3D
{
  [Export] public FastNoiseLite Noise { get; set; } = new();
  [Export] public FastNoiseLite ColorNoise { get; set; } = new();
  [Export(PropertyHint.Range, "0,6")] public int Lod = 0;
  [Export] public Vector3I ChunkCoord = Vector3I.Zero;
  [Export(PropertyHint.Flags, "-X,+X,-Y,+Y,-Z,+Z")] public int LodMask = 0;

  [ExportToolButton("Generate")]
  public Callable GenerateButton => Callable.From(Generate);

  private MeshInstance3D? _preview;

  public override void _Ready()
  {
    if (!Engine.IsEditorHint()) Generate();
  }

  public void Generate()
  {
    var sw = Stopwatch.StartNew();
    var edits = new EditStore();
    var gen = new TerrainGenerator(Noise, ColorNoise, edits);
    var key = new ChunkKey(Lod, ChunkCoord);

    var vol = new VoxelVolume();
    vol.Generate(gen, key);
    double genMs = sw.Elapsed.TotalMilliseconds;

    var buf = new MeshBuffers();
    if (vol.HasSurface())
    {
      MarchingCubesMesher.Mesh(vol, gen, buf, new int[MarchingCubesMesher.EdgeCacheSize]);
      if (key.Lod > 0)
      {
        TransitionMesher.Mesh(vol, gen, new List<SphereEdit>(), buf, new TransitionMesher.Scratch());
      }
    }
    double meshMs = sw.Elapsed.TotalMilliseconds - genMs;

    if (_preview == null || !IsInstanceValid(_preview))
    {
      _preview = new MeshInstance3D { Name = "Preview" };
      AddChild(_preview);
    }
    _preview.MaterialOverride = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/terrain.gdshader") };
    _preview.Position = key.Origin;

    if (buf.IndexCount == 0)
    {
      _preview.Mesh = null;
    }
    else
    {
      var mesh = new ArrayMesh();
      var format = (Mesh.ArrayFormat)((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift);
      mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, buf.ToSurfaceArrays(), null, null, format);
      _preview.Mesh = mesh;
    }
    _preview.SetInstanceShaderParameter("lod_mask", LodMask);

    GD.Print($"ChunkDebug {key}: generate {genMs:F1} ms, mesh {meshMs:F1} ms, " +
             $"{buf.VertexCount} vertices, {buf.IndexCount / 3} triangles " +
             $"(regular {buf.RegularIndexCount / 3})");
  }
}
