using Godot;
using Godot.Collections;

[GlobalClass, Tool]
public partial class ChunkData : Resource
{
  [Export]
  public ImageTexture3D DensityTexture;

  // public ArrayMesh Mesh = new ArrayMesh();
  public Array arr;
}


