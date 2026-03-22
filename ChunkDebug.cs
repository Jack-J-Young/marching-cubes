using Godot;
using System;

[Tool]
public partial class ChunkDebug : Node3D
{
  [Export]
  public Texture3D Reference { get; set; }

  private Chunk Chunk { get
    {
      return GetNode<Chunk>("./Chunk");
    }
  }

  [ExportToolButton("Transfer")]
  public Callable TransferButton => Callable.From(Transfer);
  public void Transfer()
  {
    var texture = Chunk.Data.DensityTexture;

    var width = Reference.GetWidth();
    var height = Reference.GetHeight();
    var depth = Reference.GetDepth();

    var data = Reference.GetData();

    texture.Create(data[0].GetFormat(), width, height, depth, false, data);
  }
}
