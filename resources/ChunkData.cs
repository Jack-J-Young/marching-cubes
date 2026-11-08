using Godot;
using Godot.Collections;

[GlobalClass, Tool]
public partial class ChunkData : Resource
{
  [Export]
  public ImageTexture3D DensityTexture = new ImageTexture3D();
  [Export]
  public ImageTexture3D ColorTexture = new ImageTexture3D();

  // public ArrayMesh Mesh = new ArrayMesh();
  public Array arr;
}


