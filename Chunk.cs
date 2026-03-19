using System;
using Godot;
using Godot.Collections;

[Tool]
public partial class Chunk : Node3D
{
  [Export]
  public ChunkData Data { get; set; }

  private MeshInstance3D meshInstance;
  private CollisionShape3D collisionShape;

  [ExportToolButton("Click me2")]
  public Callable ClickMe2Button => Callable.From(UpdateMeshInEditor);

  public override void _EnterTree()
  {
    UpdateMeshInEditor();
  }

  public void Dig(Vector3 pos, float radius)
  {

    int minX = (int) Math.Floor(pos.X - radius);
    minX = Math.Max(minX, 0);
    int maxX = (int) Math.Ceiling(pos.X + radius);
    maxX = Math.Min(maxX, Data.Size - 1);

    int minY = (int) Math.Floor(pos.Y - radius);
    minY = Math.Max(minY, 0);
    int maxY = (int) Math.Ceiling(pos.Y + radius);
    maxY = Math.Min(maxY, Data.Size - 1);

    int minZ = (int) Math.Floor(pos.Z - radius);
    minZ = Math.Max(minZ, 0);
    int maxZ = (int) Math.Ceiling(pos.Z + radius);
    maxZ = Math.Min(maxZ, Data.Size - 1);

    for (int x = minX; x < maxX; x++)
    {
      for (int y = minY; y < maxY; y++)
      {
        for (int z = minZ; z < maxZ; z++)
        {
          var dist = new Vector3(x, y, z).DistanceTo(pos);
          
          var influence = Math.Max(Math.Min((dist - radius) / 0.5f, 1), 0);
          Data.Points[x][y][z] = influence * Data.Points[x][y][z];
        }
      }
    }
    UpdateMeshInEditor();
  }
 
  public void UpdateMeshInEditor()
  {
    // Suppose the MeshInstance3D child node is named "MyMesh"
    meshInstance = GetNode<MeshInstance3D>("./MeshInstance3D");
    collisionShape = GetNode<CollisionShape3D>("./CollisionShape3D");
    if (meshInstance != null)
    {
      ArrayMesh mesh = new ArrayMesh();

      // Vector3[] vertices = [
      //   new Vector3(0,0,0),
      //       new Vector3(1,0,0),
      //       new Vector3(0,1,0)
      // ];
      // int[] indices = [0, 1, 2];

      Godot.Collections.Array channels = Data.GenerateMeshChannels();

      // Array channels = [];
      // channels.Resize((int)ArrayMesh.ArrayType.Max);
      // channels[(int)ArrayMesh.ArrayType.Vertex] = channels[(int)ArrayMesh.ArrayType.Vertex];
      // channels[(int)ArrayMesh.ArrayType.Index] = channels[(int)ArrayMesh.ArrayType.Index];

      mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, channels);

      mesh.BlendShapeMode = Mesh.BlendShapeMode.Normalized;

      meshInstance.Mesh = mesh;

      collisionShape.Shape = mesh.CreateTrimeshShape();
    }
    meshInstance.NotifyPropertyListChanged();
  }
}
