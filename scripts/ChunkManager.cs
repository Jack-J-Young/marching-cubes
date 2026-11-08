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
  [Export]
  public FastNoiseLite ColorNoise { get; set; }

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
    List<Image> colorData = [];

    for (int iz = 0; iz < 33; iz++)
    {
      byte[] bytes = new byte[33 * 33];
      byte[] colBytes = new byte[33 * 33 * 3];
      for (int ix = 0; ix < 33; ix++)
      {
        for (int iy = 0; iy < 33; iy++)
        {
          var i = ix + iy * 33;
          var pos = new Vector3(ix, iy, iz) + localPos;
          float fVal = Noise.GetNoise3Dv(pos);
          // fVal = (fVal + 1) * 0.5f;
          fVal = (fVal + 1) * 2;
          fVal = Math.Clamp(fVal, 0, 1);

          float hue = ColorNoise.GetNoise3Dv(pos);
          hue = (hue + 1) * 1f;
          // hue = (hue + 1) * 0.5f;
          Color c = Color.FromHsv(hue, Math.Clamp((fVal - 0.5f)*8, 0, 1), Math.Clamp((fVal - hue * 0.25f), 0, 1));


          fVal *= 255f;
          byte bVal = (byte)fVal;

          bytes[i] = bVal;


          byte r = (byte)(c.R * 255f);
          byte g = (byte)(c.G * 255f);
          byte b = (byte)(c.B * 255f);

          colBytes[i * 3] = r;
          colBytes[i * 3 + 1] = g;
          colBytes[i * 3 + 2] = b;
        }
      }
      data.Add(Image.CreateFromData(33, 33, false, Image.Format.R8, bytes));
      colorData.Add(Image.CreateFromData(33, 33, false, Image.Format.Rgb8, colBytes));
    }
    // var colorTexture = Chunk.Data.ColorTexture;

    // colorTexture.Create(Image.Format.Rgb8, Size, Size, Size, false, colorData);



    var chunkScene = GD.Load<PackedScene>("res://scenes/chunk.tscn");
    var chunk = chunkScene.Instantiate<Chunk>();
    chunk.Data = new ChunkData();
    var gdDataArr = new Godot.Collections.Array<Image>(data.ToArray());
    chunk.Data.DensityTexture.Create(Image.Format.R8, 33, 33, 33, false, gdDataArr);
    var gdColorDataArr = new Godot.Collections.Array<Image>(colorData.ToArray());
    chunk.Data.ColorTexture.Create(Image.Format.Rgb8, 33, 33, 33, false, gdColorDataArr);
    chunk.Position = localPos;
    AddChild(chunk);
    chunk.DefferedMeshUpdate();
    // chunk.Scale = Vector3.One * 0.99f;

    Chunks.Add(chunkPos, chunk);
    
  }
}
