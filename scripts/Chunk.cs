using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Godot.Collections;

#nullable enable
[Tool]
public partial class Chunk : Node3D
{
  [Export]
  public int Size = 32;

  [Export]
  public ChunkData Data { get; set; } = new ChunkData();

  private MeshInstance3D? meshInstance;
  private CollisionShape3D? collisionShape;

  private Task? mesherTask;
  private bool digging;

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
    if (EnsureChildren() && mesherTask == null)
    {
      mesherTask = ChunkMeshManager.RequestChunk(Data);
    }
  }

  private void MesherProcess()
  {
    if (mesherTask == null)
    {
      return;
    }

    if (mesherTask.IsCompleted)
    {
      var mesh = new ArrayMesh();
      mesh.ClearSurfaces();

      if (Data.arr[(int)Mesh.ArrayType.Vertex].AsByteArray().Length == 0)
      {
      }
      else
      {
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, Data.arr);
      }
      // mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, Data.arr);

      meshInstance!.Mesh = mesh;
      collisionShape!.Shape = mesh.CreateTrimeshShape();

      // meshInstance!.Mesh = Data.Mesh;
      // collisionShape!.Shape = Data.Mesh.CreateTrimeshShape();
      meshInstance.NotifyPropertyListChanged();
      mesherTask.Dispose();
      mesherTask = null;
    }
  }

  public override void _Process(double delta)
  {
    base._Process(delta);

    MesherProcess();
  }

}
