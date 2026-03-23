using System;
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

  public void Dig(Vector3 globalPos, Texture3D digTexture)
  {
    if (mesherTask != null || digging)
    {
      return;
    }

    // digging = true;

    var pos = globalPos - Position;

    Vector3 offset = pos.Floor();

    Vector3 subGridOffset = pos - offset;
    // TODO use sub grid for dig texture;
    // GenerateDigTexture();

    Vector3 textureCenter = Vector3.One * Size * 0.5f;
    offset -= textureCenter;

    Vector3I iOffset = new Vector3I((int)offset.X, (int)offset.Y, (int)offset.Z);

    byte[] offsetBytes = new byte[12];
    BitConverter.GetBytes(iOffset.X).CopyTo(offsetBytes, 0);
    BitConverter.GetBytes(iOffset.Y).CopyTo(offsetBytes, 4);
    BitConverter.GetBytes(iOffset.Z).CopyTo(offsetBytes, 8);

    var rd = RenderingServer.GetRenderingDevice();

    var shaderFile = ResourceLoader.Load<RDShaderFile>("res://compute_shader.glsl");

    var shader = rd.ShaderCreateFromSpirV(shaderFile.GetSpirV());

    var pipeline = rd.ComputePipelineCreate(shader);

    // get 3d texture size
    // get max possible vertices

    var textureView = new RDTextureView
    {
      FormatOverride = RenderingDevice.DataFormat.R8Unorm,
    };

    var inputTexture = Data.DensityTexture;

    var textureFormat = new RDTextureFormat
    {
      TextureType = RenderingDevice.TextureType.Type3D,
      Width = (uint)inputTexture.GetWidth(),
      Height = (uint)inputTexture.GetHeight(),
      Depth = (uint)inputTexture.GetDepth(),
      Format = RenderingDevice.DataFormat.R8Unorm,
      UsageBits = RenderingDevice.TextureUsageBits.SamplingBit |
                  RenderingDevice.TextureUsageBits.CanCopyFromBit |
                  RenderingDevice.TextureUsageBits.InputAttachmentBit |
                  RenderingDevice.TextureUsageBits.StorageBit |
                  RenderingDevice.TextureUsageBits.CanCopyToBit |
                  RenderingDevice.TextureUsageBits.CanUpdateBit
    };

    var byteData = inputTexture.GetData().ToArray().Select(img => img.GetData());
    var bytes = byteData.Aggregate((a, b) => [.. a, .. b]);

    var texture = rd.TextureCreate(textureFormat, textureView, [bytes]);

    var texture_uniform = new RDUniform
    {
      UniformType = RenderingDevice.UniformType.Image,
      Binding = 0,
    };

    texture_uniform.AddId(texture);
    //
    var digTextureView = new RDTextureView
    {
      FormatOverride = RenderingDevice.DataFormat.R8Unorm,
    };

    var digInputTexture = digTexture;

    var digTextureFormat = new RDTextureFormat
    {
      TextureType = RenderingDevice.TextureType.Type3D,
      Width = (uint)digInputTexture.GetWidth(),
      Height = (uint)digInputTexture.GetHeight(),
      Depth = (uint)digInputTexture.GetDepth(),
      Format = RenderingDevice.DataFormat.R8Unorm,
      UsageBits = RenderingDevice.TextureUsageBits.SamplingBit |
                  RenderingDevice.TextureUsageBits.CanCopyFromBit |
                  RenderingDevice.TextureUsageBits.InputAttachmentBit |
                  RenderingDevice.TextureUsageBits.StorageBit
    };
    var digByteData = digInputTexture.GetData().ToArray().Select(img => img.GetData());
    var digBytes = digByteData.Aggregate((a, b) => [.. a, .. b]);

    var digeTexture = rd.TextureCreate(digTextureFormat, digTextureView, [digBytes]);

    var dig_uniform = new RDUniform
    {
      UniformType = RenderingDevice.UniformType.Image,
      Binding = 1,
    };

    dig_uniform.AddId(digeTexture);
    //
    var offsetBuffer = rd.StorageBufferCreate(12, offsetBytes);

    var offsetUniform = new RDUniform
    {
      UniformType = RenderingDevice.UniformType.StorageBuffer,
      Binding = 2
    };

    offsetUniform.AddId(offsetBuffer);

    // uniform set
    var uniformSet = rd.UniformSetCreate(
      [texture_uniform, dig_uniform, offsetUniform],
      shader, // RDShader
      0       // set index
    );

    var computeList = rd.ComputeListBegin();

    rd.ComputeListBindComputePipeline(computeList, pipeline);
    rd.ComputeListBindUniformSet(computeList, uniformSet, 0);
    rd.ComputeListDispatch(computeList, 8, 8, 8);
    rd.ComputeListEnd();

    // var whatever = rd.TextureGetData(texture, layer);

    var outBytes = rd.TextureGetData(texture, 0);

    rd.TextureGetDataAsync(texture, 0, new Callable(this, MethodName.UpdateBytes));

    // var width = Data.DensityTexture.GetWidth();
    // var height = Data.DensityTexture.GetHeight();
    // var depth = Data.DensityTexture.GetDepth();

    // Array<Image> images = [];
    // int layerSize = width * height;
    // for (int i = 0; i < outBytes.Length; i += layerSize)
    // {
    //   images.Add(Image.CreateFromData(width, height, false, Image.Format.R8, outBytes[i..(i + layerSize)]));
    // }
    // // var imgTexture = new ImageTexture3D();

    // // imgTexture.Create(Image.Format.R8, width, height, depth, false, images);
    // // imgTexture.Update()

    // Data.DensityTexture.Update(images);

    // DefferedMeshUpdate();
  }

  public void UpdateBytes(byte[] bytes)
  {
    digging = false;
    var width = Data.DensityTexture.GetWidth();
    var height = Data.DensityTexture.GetHeight();
    // var depth = Data.DensityTexture.GetDepth();

    Array<Image> images = [];
    int layerSize = width * height;
    for (int i = 0; i < bytes.Length; i += layerSize)
    {
      images.Add(Image.CreateFromData(width, height, false, Image.Format.R8, bytes[i..(i + layerSize)]));
    }
    // var imgTexture = new ImageTexture3D();

    // imgTexture.Create(Image.Format.R8, width, height, depth, false, images);
    // imgTexture.Update()

    Data.DensityTexture.Update(images);

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

}
