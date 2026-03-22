using System;
using Godot;
using Godot.Collections;

[Tool]
public partial class ChunkManager : Node3D
{
  [Export]
  public int Range;

  [Export]
  public FastNoiseLite Noise { get; set; }

  private Dictionary<Vector3I, Chunk> Chunks = new Dictionary<Vector3I, Chunk>();

  [ExportToolButton("Test!")]
  public Callable testButton => Callable.From(Test);

  [ExportToolButton("Test2!")]
  public Callable test2Button => Callable.From(Test2);

  public void Test()
  {
    // Noise.Offset = new Vector3(32, 32, 32);
    // GenerateChunk(1,0);
    for (int x = -3; x < 3; x++)
    {
      for (int y = -3; y < 3; y++)
      {
        for (int z = -3; z < 3; z++)
        {
          GenerateChunk(x, y, z);
        }
      }
    }
  }

  public void Test2()
  {
    Chunks.Clear();
    foreach (var node in GetChildren())
    {
      RemoveChild(node);
    }
  }

  public override void _Ready()
  {
    base._Ready();
    Test2();
    Test();
  }

  public void Dig(Vector3 localPos, float localRadius)
  {
    var pos  = localPos/32f;
    var radius = localRadius/32f;

    var minX = (int)Math.Floor(pos.X - radius);
    var maxX = (int)Math.Ceiling(pos.X + radius);
    var minY = (int)Math.Floor(pos.Y - radius);
    var maxY = (int)Math.Ceiling(pos.Y + radius);
    var minZ = (int)Math.Floor(pos.Z - radius);
    var maxZ = (int)Math.Ceiling(pos.Z + radius);

    for (int x = minX; x <= maxX; x++)
    {
      for (int y = minY; y <= maxY; y++)
      {
        for (int z = minZ; z <= maxZ; z++)
        {
          var chunkPos = new Vector3I(x, y, z);
          if (Chunks.ContainsKey(chunkPos))
          {
            Chunks[chunkPos].Dig(localPos, localRadius);
          }
        }
      }
    }
  }

  public void GenerateChunk(int x, int y, int z)
  {
    // var chunkPos = new Vector3(x, y, z)*32f;

    var chunkPos = new Vector3I(x, y, z);
    var localPos = chunkPos*32;

    Noise.Offset = localPos;
    // Noise.
    var data = Noise.GetImage3D(33, 33, 33, normalize: false);
    var chunkScene = GD.Load<PackedScene>("res://chunk.tscn");
    var chunk = chunkScene.Instantiate<Chunk>();
    chunk.Data = new ChunkData();

    // Array<Image> data = [];

    // for (int _z = 0; _z < 32; _z++)
    // {
    //   var layerData = new byte[32 * 32];
    //   for (int _x = 0; _x < 32; _x++)
    //   {
    //     for (int _y = 0; _y < 32; _y++)
    //     {
    //       var localPos = new Vector3(_x, _y, _z);
    //       var noiseF = Noise.GetNoise3Dv(localPos + chunkPos + new Vector3(100, 100, 100));
    //       noiseF = (noiseF + 1)/2;
    //       var noiseB = (byte)(noiseF * 255f);
    //       layerData[_x + _y * 32] = noiseB;
    //     }
    //   }
    //   data.Add(Image.CreateFromData(32, 32, false, Image.Format.R8, layerData));
    // }

    chunk.Position = localPos;
    // chunk.Scale = Vector3.One * 0.999f;
    // var chunk = new Chunk();
    AddChild(chunk);
    chunk.UpdateChunkFromTexture(data);
    // chunk.DefferedMeshUpdate();

    Chunks.Add(chunkPos, chunk);
    
  }
}
