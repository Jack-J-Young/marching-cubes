using System;
using System.Threading.Tasks;
using Godot;

#nullable enable
[Tool]
public partial class Chunk : Node3D
{
  [Export]
  public ChunkData Data { get; set; } = new ChunkData();

  private MeshInstance3D? meshInstance;
  private CollisionShape3D? collisionShape;

  private Task? mesherTask;

  [ExportToolButton("Update mesh!")]
  public Callable updateMeshButton => Callable.From(DefferedMeshUpdate);

  private bool EnsureChildren()
  {
    if (meshInstance == null)
    {
      meshInstance = GetNode<MeshInstance3D>("./MeshInstance3D");
    }
    if (collisionShape == null)
    {
      collisionShape = GetNode<CollisionShape3D>("./CollisionShape3D");
    }
    return meshInstance != null && collisionShape != null;
  }

  public override void _EnterTree()
  {
    base._EnterTree();

    EnsureChildren();
  }

  public void DefferedMeshUpdate()
  {
    if (EnsureChildren())
    {
      mesherTask = ChunkMeshManager.RequestChunk(Data);
    }
  }

  public void Dig(Vector3 pos, float radius)
  {

    int minX = (int)Math.Floor(pos.X - radius);
    minX = Math.Max(minX, 0);
    int maxX = (int)Math.Ceiling(pos.X + radius);
    maxX = Math.Min(maxX, Data.DensityTexture.GetWidth());

    int minY = (int)Math.Floor(pos.Y - radius);
    minY = Math.Max(minY, 0);
    int maxY = (int)Math.Ceiling(pos.Y + radius);
    maxY = Math.Min(maxY, Data.DensityTexture.GetWidth());

    int minZ = (int)Math.Floor(pos.Z - radius);
    minZ = Math.Max(minZ, 0);
    int maxZ = (int)Math.Ceiling(pos.Z + radius);
    maxZ = Math.Min(maxZ, Data.DensityTexture.GetWidth());

    var dat = Data.DensityTexture.GetData();

    for (int x = minX; x < maxX; x++)
    {
      for (int y = minY; y < maxY; y++)
      {
        for (int z = minZ; z < maxZ; z++)
        {
          dat[z].SetPixel(x, y, new Color(0));
          // var dist = new Vector3(x, y, z).DistanceTo(pos);

          // var influence = Math.Max(Math.Min((dist - radius) / 0.5f, 1), 0);
          // Data.Points[x][y][z] = influence * Data.Points[x][y][z];
        }
      }
    }
    Data.DensityTexture.Update(dat);

    DefferedMeshUpdate();
  }

  public override void _Process(double delta)
  {
    base._Process(delta);

    // if (!loaded)
    // {
    //   loaded = true;
    //   // DefferedMeshUpdate();
    //   return;
    // }

    if (mesherTask == null)
    {
      return;
    }

    if (mesherTask.IsCompleted)
    {
      var mesh = new ArrayMesh();
      mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, Data.arr);

      meshInstance!.Mesh = mesh;
      collisionShape!.Shape = mesh.CreateTrimeshShape();

      // meshInstance!.Mesh = Data.Mesh;
      // collisionShape!.Shape = Data.Mesh.CreateTrimeshShape();
      meshInstance.NotifyPropertyListChanged();
      mesherTask = null;
    }
  }

}
