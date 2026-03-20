using System;
using System.Collections.Generic;
using System.Linq;
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
 
  public Godot.Collections.Array GenerateMeshChannels()
  {
    Godot.Collections.Array channels = [];
    channels.Resize((int)ArrayMesh.ArrayType.Max);

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
                  RenderingDevice.TextureUsageBits.StorageBit
    };
    var byteData = inputTexture.GetData().Select(img => img.GetData());
    var bytes = byteData.Aggregate((a, b) => [.. a, .. b]);

    var texture = rd.TextureCreate(textureFormat, textureView, [bytes]);

    var texture_uniform = new RDUniform
    {
      UniformType = RenderingDevice.UniformType.Image,
      Binding = 0,
    };

    texture_uniform.AddId(texture);

    // outputs
    var size = (inputTexture.GetWidth()-1) * (inputTexture.GetHeight()-1) * (inputTexture.GetDepth()-1);

    var usedBuffer = rd.StorageBufferCreate(4 * (uint)size);

    var usedUniform = new RDUniform
    {
      UniformType = RenderingDevice.UniformType.StorageBuffer,
      Binding = 1,
    };

    usedUniform.AddId(usedBuffer);

    //
    var vec3Size = 4*4;
    var vertexBufferSize = size * vec3Size;

    var vertexBuffer = rd.StorageBufferCreate((uint)vertexBufferSize);

    var vertexUniform = new RDUniform
    {
      UniformType = RenderingDevice.UniformType.StorageBuffer,
      Binding = 2,
    };

    vertexUniform.AddId(vertexBuffer);

    // uniform set
    var uniformSet = rd.UniformSetCreate(
      [texture_uniform, usedUniform, vertexUniform],
      shader, // RDShader
      0       // set index
    );

    var computeList = rd.ComputeListBegin();

    rd.ComputeListBindComputePipeline(computeList, pipeline);
    rd.ComputeListBindUniformSet(computeList, uniformSet, 0);
    rd.ComputeListDispatch(computeList, 1, 1, 1);
    rd.ComputeListEnd();

    var rawUsedBufferData = rd.BufferGetData(usedBuffer);
    var usedBufferData = new int[rawUsedBufferData.Length / 4];
    for (int i = 0; i < rawUsedBufferData.Length / 4; i++)
    {
      var offset = i * 4;
      int num = rawUsedBufferData[offset];
      num += rawUsedBufferData[offset + 1] << 8;
      num += rawUsedBufferData[offset + 2] << 16;
      num += rawUsedBufferData[offset + 3] << 24;
      usedBufferData[i] = num;
    }

    // GD.Print(usedBufferData.Join(", "));

    var rawVertexBufferData = rd.BufferGetData(vertexBuffer);
    var vertexBufferData = new float[rawVertexBufferData.Length / 4];
    for (int i = 0; i < vertexBufferData.Length; i++)
    {
      vertexBufferData[i] = BitConverter.ToSingle(rawVertexBufferData, i * 4);
    }

    var convertedVects = new Vector3[vertexBufferData.Length/4];

    for (int i = 0; i < convertedVects.Length; i++)
    {
      convertedVects[i] = new Vector3(
        vertexBufferData[i * 4],
        vertexBufferData[i * 4 + 1],
        vertexBufferData[i * 4 + 2]
      );
    }

    var outputVertices = new List<Vector3>();
    var indices = new List<int>();

    for (int i = 0; i < usedBufferData.Length; i++)
    {
      var offset = i * 12;
      if (offset + 11 >= convertedVects.Length)
      {
        break;
      }

      Vector3[] vertices = [
        convertedVects[offset],
        convertedVects[offset + 1],
        convertedVects[offset + 2],
        convertedVects[offset + 3],
        convertedVects[offset + 4],
        convertedVects[offset + 5],
        convertedVects[offset + 6],
        convertedVects[offset + 7],
        convertedVects[offset + 8],
        convertedVects[offset + 9],
        convertedVects[offset + 10],
        convertedVects[offset + 11],
      ];

      for (int j = 0; j < usedBufferData[i]; j++)
      {
        // GD.Print(j);
        if (j >= 12)
        {
          continue;
        }
        
        indices.Add(outputVertices.Count);
        outputVertices.Add(vertices[j]);

      }
    }

    channels[(int)ArrayMesh.ArrayType.Vertex] = outputVertices.ToArray();
    channels[(int)ArrayMesh.ArrayType.Index] = indices.ToArray();

    // GD.Print(vertexBufferData.Join(", "));

    return channels;
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

      Godot.Collections.Array channels = GenerateMeshChannels();

      // Array channels = [];
      // channels.Resize((int)ArrayMesh.ArrayType.Max);
      // channels[(int)ArrayMesh.ArrayType.Vertex] = channels[(int)ArrayMesh.ArrayType.Vertex];
      // channels[(int)ArrayMesh.ArrayType.Index] = channels[(int)ArrayMesh.ArrayType.Index];

      mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, channels);
      // mesh.SurfaceRemove(mesh.GetSurfaceCount());
      // mesh.clear

      // mesh.BlendShapeMode = Mesh.BlendShapeMode.Normalized;

      meshInstance.Mesh = mesh;

      collisionShape.Shape = mesh.CreateTrimeshShape();
    }
    meshInstance.NotifyPropertyListChanged();
  }
}
