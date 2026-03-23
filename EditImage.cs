using System;
using Godot;
using Godot.Collections;

[Tool]
public partial class EditImage : Node3D
{
  [Export]
  public ImageTexture3D Texture { get; set; } = new ImageTexture3D();

  [Export]
  public float Radius = 25f;

  [ExportToolButton("Test!")]
  public Callable testButton => Callable.From(Test);

  public void Test()
  {
    var section = 0.5;
    int size = (int)Math.Ceiling(Radius * 2);
    Vector3 center = Vector3.One * (size/2f);
    Array<Image> data = [];
    for (int z = 0; z < size; z++)
    {
      byte[] imageData = new byte[size * size * 3];

      for (int x = 0; x < size; x++)
      {
        for (int y = 0; y < size; y++)
        {
          var dist = new Vector3(x, y, z).DistanceTo(center);
          var scaledDist = dist / Radius;

          var infl = 0d;
          if (scaledDist > section)
          {
            infl = (scaledDist - section) / (1 - section);
            infl *= infl;
            infl = Math.Clamp(infl, 0, 1);
          }

          // var influence = Math.Max(Math.Min((dist - radius) / 0.5f, 1), 0);
          // dat[z].

          var b = (byte)(infl * 255f);

          imageData[3 * (x + y * size)] = b;
          imageData[3 * (x + y * size) + 1] = b;
          imageData[3 * (x + y * size) + 2] = b;
        }
      }

      data.Add(Image.CreateFromData(size, size, false, Image.Format.Rgb8, imageData));
    }

    Texture.Create(Image.Format.Rgb8, size, size, size, false, data);
  }
}
