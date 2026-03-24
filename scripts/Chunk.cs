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
      // mesherTask.Di
    }
  }

  public void UpdateChunkFromTexture(Array<Image> data)
  {
    var width = 33;
    var height = 33;
    var depth = 33;

    // var data = texture.GetData();

    Data.DensityTexture.Create(data[0].GetFormat(), width, height, depth, false, data);

    DefferedMeshUpdate();
    // Data.DensityTexture.Update();
  }
  
  private class DigOptions
  {
    public Vector3I Offset;
    public Texture3D DigTexture;

    public DigOptions(Vector3I Offset, Texture3D DigTexture)
    {
      this.Offset = Offset;
      this.DigTexture = DigTexture;
    }
  }

  private Queue<DigOptions> defferedDigs = [];

  public void Dig(Vector3 globalPos, Texture3D digTexture, ChunkComputeManager ccm)
  {
    // if (mesherTask != null || digging)
    // {
    //   return;
    // }

    // digging = true;

    var pos = globalPos - Position;

    Vector3 offset = pos.Floor();

    Vector3 subGridOffset = pos - offset;
    // TODO use sub grid for dig texture;
    // GenerateDigTexture();

    Vector3 textureCenter = Vector3.One * digTexture.GetWidth() * 0.5f;
    offset -= textureCenter;

    Vector3I iOffset = new Vector3I((int)offset.X, (int)offset.Y, (int)offset.Z);

    defferedDigs.Enqueue(new DigOptions(iOffset, digTexture));

    // ccm.Dig(iOffset, Data, digTexture, () =>
    // {
    //   // digging = false;
    //   // DefferedMeshUpdate();
    // });
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

  public ChunkComputeManager? ccm;

  public void DigProcess()
  {
    if (ccm == null)
    {
      return;
    }

    if (mesherTask != null || defferedDigs.Count == 0 || digging)
    {
      return;
    }
    digging = true;

    DigOptions nextDig = defferedDigs.Dequeue();


    ccm.Dig(nextDig.Offset, Data, nextDig.DigTexture, () =>
    {
      DefferedMeshUpdate();
      digging = false;
    });
  }

  public override void _Process(double delta)
  {
    base._Process(delta);

    MesherProcess();
    DigProcess();
  }

}
