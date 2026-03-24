using System;
using System.Collections;
using System.Collections.Generic;
using Godot;
// using Godot.Collections;

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

  private ChunkComputeManager ccm = new ChunkComputeManager();

  private class DigData
  {
    public Vector3 LocalPos;
    public float Radius;
    public Chunk Chunk;
  }

  private Queue<DigData> DefferedDigs = [];

  private bool digging = false;

  public override void _Process(double delta)
  {
    base._Process(delta);

    if (digging || DefferedDigs.Count == 0)
    {
      return;
    }

    var nextDig = DefferedDigs.Dequeue();

    ccm.Dig(nextDig.LocalPos, nextDig.Radius, nextDig.Chunk.Data, () =>
    {
      nextDig.Chunk.DefferedMeshUpdate();
      digging = false;
    });
  }


  public void Dig(Vector3 pos, float radius)
  {
    

    var chunkPos = pos / 32;

    var chunkMin = (chunkPos - Vector3.One * radius / 32f).Floor();
    var chunkMax = (chunkPos + Vector3.One * radius / 32f).Ceil();

    for (int x = (int)chunkMin.X; x <= (int)chunkMax.X; x++)
    {
      for (int y = (int)chunkMin.Y; y <= (int)chunkMax.Y; y++)
      {
        for (int z = (int)chunkMin.Z; z <= (int)chunkMax.Z; z++)
        {
          var key = new Vector3I(x, y, z);
          if (Chunks.ContainsKey(key))
          {
            var chunk = Chunks[key];
              var localPos = pos - chunk.Position;

              var digData = new DigData
              {
                LocalPos = localPos,
                Radius = radius,
                Chunk = chunk
              };

              DefferedDigs.Enqueue(digData);
          }
        }
      }
    }
  }

  public void GenerateChunk(int x, int y, int z)
  {
    var chunkPos = new Vector3I(x, y, z);
    var localPos = chunkPos*32;

    List<Image> data = [];

    for (int iz = 0; iz < 33; iz++)
    {
      byte[] bytes = new byte[33 * 33];
      for (int ix = 0; ix < 33; ix++)
      {
        for (int iy = 0; iy < 33; iy++)
        {
          var i = ix + iy * 33;
          float fVal = Noise.GetNoise3D(ix + localPos.X, iy + localPos.Y, iz + localPos.Z);
          fVal = (fVal + 1) * 0.5f;
          fVal *= 255f;
          byte bVal = (byte)fVal;

          bytes[i] = bVal;
        }
      }
      data.Add(Image.CreateFromData(33, 33, false, Image.Format.R8, bytes));
    }

    
    var chunkScene = GD.Load<PackedScene>("res://scenes/chunk.tscn");
    var chunk = chunkScene.Instantiate<Chunk>();
    chunk.Data = new ChunkData();
    var gdDataArr = new Godot.Collections.Array<Image>(data.ToArray());
    chunk.Data.DensityTexture.Create(Image.Format.R8, 33, 33, 33, false, gdDataArr);
    chunk.Position = localPos;
    AddChild(chunk);
    chunk.DefferedMeshUpdate();

    Chunks.Add(chunkPos, chunk);
    
  }
}
