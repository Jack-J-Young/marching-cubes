using Godot;
using Godot.Collections;
using System;

[Tool]
public partial class ChunkDebug : Node3D
{
  [Export]
  public int Size = 32;

  [Export]
  public FastNoiseLite Noise { get; set; } = new FastNoiseLite();

  private Chunk Chunk { get
    {
      return GetNode<Chunk>("./Chunk");
    }
  }

  [ExportToolButton("Generate")]
  public Callable GenerateButton => Callable.From(Generate);
  public void Generate()
  {
    Array<Image> data = [];

    for (int z = 0; z < Size; z++)
    {
      byte[] bytes = new byte[Size * Size];
      for (int x = 0; x < Size; x++)
      {
        for (int y = 0; y < Size; y++)
        {
          var i = x + y * Size;
          float fVal = Noise.GetNoise3D(x, y, z);
          fVal = (fVal + 1) * 0.5f;
          fVal *= 255f;
          byte bVal = (byte)fVal;

          bytes[i] = bVal;
        }
      }
      data.Add(Image.CreateFromData(Size, Size, false, Image.Format.R8, bytes));
    }

    var texture = Chunk.Data.DensityTexture;

    texture.Create(Image.Format.R8, Size, Size, Size, false, data);

    Chunk.DefferedMeshUpdate();
  }
}
