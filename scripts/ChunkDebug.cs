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
  [Export]
  public FastNoiseLite ColorNoise { get; set; } = new FastNoiseLite();


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
    Array<Image> colorData = [];

    for (int z = 0; z < Size; z++)
    {
      byte[] bytes = new byte[Size * Size];
      byte[] colBytes = new byte[Size * Size * 3];
      for (int x = 0; x < Size; x++)
      {
        for (int y = 0; y < Size; y++)
        {
          var i = x + y * Size;

          float fDensVal = Noise.GetNoise3D(x, y, z);
          fDensVal = (fDensVal + 1) * 0.5f;


          float hue = ColorNoise.GetNoise3D(x, y, z);
          hue = (hue + 1) * 0.5f;
          Color c = Color.FromHsv(hue, 1.0f, fDensVal);


          fDensVal *= 255f;
          byte bDensVal = (byte)fDensVal;

          bytes[i] = bDensVal;


          byte r = (byte)(c.R * 255f);
          byte g = (byte)(c.G * 255f);
          byte b = (byte)(c.B * 255f);

          colBytes[i * 3] = r;
          colBytes[i * 3 + 1] = g;
          colBytes[i * 3 + 2] = b;
        }
      }
      data.Add(Image.CreateFromData(Size, Size, false, Image.Format.R8, bytes));
      colorData.Add(Image.CreateFromData(Size, Size, false, Image.Format.Rgb8, colBytes));
    }

    Chunk.Data.DensityTexture.Create(Image.Format.R8, Size, Size, Size, false, data);

    Chunk.Data.ColorTexture.Create(Image.Format.Rgb8, Size, Size, Size, false, colorData);

    Chunk.DefferedMeshUpdate();
  }
}
