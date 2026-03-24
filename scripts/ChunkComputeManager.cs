using System;
using System.Linq;
using Godot;
using Godot.Collections;

public class ChunkComputeManager : IDisposable
{
  private RenderingDevice RD { get { return RenderingServer.GetRenderingDevice(); } }


  private Rid DigTextureShader;
  public void EnsureTexturePipeline()
  {
    // var shaderFile = ResourceLoader.Load<RDShaderFile>("res://shaders/dig_compute.glsl");

    // DigTextureShader = RD.ShaderCreateFromSpirV(shaderFile.GetSpirV());

    // var pipeline = RD.ComputePipelineCreate(DigTextureShader);

    // var textureView = new RDTextureView
    // {
    //   FormatOverride = RenderingDevice.DataFormat.R8Unorm,
    // };

    // var textureFormat = new RDTextureFormat
    // {
    //   TextureType = RenderingDevice.TextureType.Type3D,
    //   Width = (uint),
    //   Height = (uint)size,
    //   Depth = (uint)size,
    //   Format = RenderingDevice.DataFormat.R8Unorm,
    //   UsageBits = RenderingDevice.TextureUsageBits.StorageBit |
    //               RenderingDevice.TextureUsageBits.CanCopyToBit |
    //               RenderingDevice.TextureUsageBits.CanUpdateBit |
    //               RenderingDevice.TextureUsageBits.CanCopyFromBit |
    //               RenderingDevice.TextureUsageBits.SamplingBit
    // };

    // var texture = RD.TextureCreate(textureFormat, textureView);

    // var texture_uniform = new RDUniform
    // {
    //   UniformType = RenderingDevice.UniformType.Image,
    //   Binding = 0,
    // };

    // texture_uniform.AddId(texture);

    // // uniform set
    // var uniformSet = RD.UniformSetCreate(
    //   [texture_uniform],
    //   shader, // RDShader
    //   0       // set index
    // );

    // var computeList = RD.ComputeListBegin();

    // RD.ComputeListBindComputePipeline(computeList, pipeline);
    // RD.ComputeListBindUniformSet(computeList, uniformSet, 0);
    // RD.ComputeListDispatch(computeList, (uint)size, (uint)size, (uint)size);
    // RD.ComputeListEnd();
  }

  public void GetDigTextureAsync(int size, Action<Array<Image>> callback)
  {
    var shaderFile = ResourceLoader.Load<RDShaderFile>("res://shaders/dig_compute.glsl");

    var shader = RD.ShaderCreateFromSpirV(shaderFile.GetSpirV());

    var pipeline = RD.ComputePipelineCreate(shader);

    var textureView = new RDTextureView
    {
      FormatOverride = RenderingDevice.DataFormat.R8Unorm,
    };

    var textureFormat = new RDTextureFormat
    {
      TextureType = RenderingDevice.TextureType.Type3D,
      Width = (uint)size,
      Height = (uint)size,
      Depth = (uint)size,
      Format = RenderingDevice.DataFormat.R8Unorm,
      UsageBits = RenderingDevice.TextureUsageBits.StorageBit |
                  RenderingDevice.TextureUsageBits.CanCopyToBit |
                  RenderingDevice.TextureUsageBits.CanUpdateBit |
                  RenderingDevice.TextureUsageBits.CanCopyFromBit |
                  RenderingDevice.TextureUsageBits.SamplingBit
    };

    var texture = RD.TextureCreate(textureFormat, textureView);

    var texture_uniform = new RDUniform
    {
      UniformType = RenderingDevice.UniformType.Image,
      Binding = 0,
    };

    texture_uniform.AddId(texture);

    // uniform set
    var uniformSet = RD.UniformSetCreate(
      [texture_uniform],
      shader, // RDShader
      0       // set index
    );

    var computeList = RD.ComputeListBegin();

    RD.ComputeListBindComputePipeline(computeList, pipeline);
    RD.ComputeListBindUniformSet(computeList, uniformSet, 0);
    RD.ComputeListDispatch(computeList, (uint)size, (uint)size, (uint)size);
    RD.ComputeListEnd();

    RD.TextureGetDataAsync(texture, 0, Callable.From<byte[]>((bytes) =>
    {
      Array<Image> images = [];
      int layerSize = size * size;
      for (int i = 0; i < bytes.Length; i += layerSize)
      {
        images.Add(Image.CreateFromData(size, size, false, Image.Format.R8, bytes[i..(i + layerSize)]));
      }

      RD.FreeRid(uniformSet);
      RD.FreeRid(texture);
      RD.FreeRid(pipeline);
      RD.FreeRid(shader);

      callback.Invoke(images);
    }));
  }


  private Rid DigShader;
  private Rid DigPipeline;
  private Rid ChunkRdTexture;
  private Rid DigRdTexture;
  private Rid OffsetBuffer;
  private Rid DigUniformSet;
  public void EnsureDigPipeline(int chunkSize, Texture3D digTexture)
  {
    if (DigShader.IsValid)
    {
      return;
    }

    var shaderFile = ResourceLoader.Load<RDShaderFile>("res://shaders/compute_shader.glsl");

    DigShader = RD.ShaderCreateFromSpirV(shaderFile.GetSpirV());

    DigPipeline = RD.ComputePipelineCreate(DigShader);

    // get 3d texture size
    // get max possible vertices

    var textureView = new RDTextureView
    {
      FormatOverride = RenderingDevice.DataFormat.R8Unorm,
    };

    var textureFormat = new RDTextureFormat
    {
      TextureType = RenderingDevice.TextureType.Type3D,
      Width = (uint)chunkSize,
      Height = (uint)chunkSize,
      Depth = (uint)chunkSize,
      Format = RenderingDevice.DataFormat.R8Unorm,
      UsageBits = RenderingDevice.TextureUsageBits.SamplingBit |
                  RenderingDevice.TextureUsageBits.CanCopyFromBit |
                  RenderingDevice.TextureUsageBits.InputAttachmentBit |
                  RenderingDevice.TextureUsageBits.StorageBit |
                  RenderingDevice.TextureUsageBits.CanCopyToBit |
                  RenderingDevice.TextureUsageBits.CanUpdateBit
    };

    ChunkRdTexture = RD.TextureCreate(textureFormat, textureView);

    var texture_uniform = new RDUniform
    {
      UniformType = RenderingDevice.UniformType.Image,
      Binding = 0,
    };

    texture_uniform.AddId(ChunkRdTexture);
    //
    var digTextureView = new RDTextureView
    {
      FormatOverride = RenderingDevice.DataFormat.R8Unorm,
    };

    var digTextureFormat = new RDTextureFormat
    {
      TextureType = RenderingDevice.TextureType.Type3D,
      Width = (uint)digTexture.GetWidth(),
      Height = (uint)digTexture.GetHeight(),
      Depth = (uint)digTexture.GetDepth(),
      Format = RenderingDevice.DataFormat.R8Unorm,
      UsageBits = RenderingDevice.TextureUsageBits.SamplingBit |
                  RenderingDevice.TextureUsageBits.CanCopyFromBit |
                  RenderingDevice.TextureUsageBits.InputAttachmentBit |
                  RenderingDevice.TextureUsageBits.StorageBit
    };
    var digByteData = digTexture.GetData().ToArray().Select(img => img.GetData());
    var digBytes = digByteData.Aggregate((a, b) => [.. a, .. b]);

    DigRdTexture = RD.TextureCreate(digTextureFormat, digTextureView, [digBytes]);

    var dig_uniform = new RDUniform
    {
      UniformType = RenderingDevice.UniformType.Image,
      Binding = 1,
    };

    dig_uniform.AddId(DigRdTexture);
    //
    OffsetBuffer = RD.StorageBufferCreate(12);

    var offsetUniform = new RDUniform
    {
      UniformType = RenderingDevice.UniformType.StorageBuffer,
      Binding = 2
    };

    offsetUniform.AddId(OffsetBuffer);

    // uniform set
    DigUniformSet = RD.UniformSetCreate(
      [texture_uniform, dig_uniform, offsetUniform],
      DigShader, // RDShader
      0       // set index
    );

    // var computeList = RD.ComputeListBegin();

    // RD.ComputeListBindComputePipeline(computeList, pipeline);
    // RD.ComputeListBindUniformSet(computeList, DigUniformSet, 0);
    // RD.ComputeListDispatch(computeList, 11, 11, 11);
    // RD.ComputeListEnd();
  }

  public void Dig(Vector3I offset, ChunkData chunk, Texture3D digTexture, Action callback)
  {
    EnsureDigPipeline(chunk.DensityTexture.GetWidth(), digTexture);

    byte[] offsetBytes = new byte[12];
    BitConverter.GetBytes(offset.X).CopyTo(offsetBytes, 0);
    BitConverter.GetBytes(offset.Y).CopyTo(offsetBytes, 4);
    BitConverter.GetBytes(offset.Z).CopyTo(offsetBytes, 8);

    

    Array<byte> bytes = [];
    var images = chunk.DensityTexture.GetData();
    foreach (var image in images)
    {
      bytes.AddRange(image.GetData());
    }
    RD.TextureUpdate(ChunkRdTexture, 0, bytes.ToArray());

    // var offsetBuffer = RD.StorageBufferCreate(12, offsetBytes);
    RD.BufferUpdate(OffsetBuffer, 0, 12, offsetBytes);

    // uniform set
    var computeList = RD.ComputeListBegin();

    RD.ComputeListBindComputePipeline(computeList, DigPipeline);
    RD.ComputeListBindUniformSet(computeList, DigUniformSet, 0);
    RD.ComputeListDispatch(computeList, 33, 33, 33);
    RD.ComputeListEnd();

    RD.TextureGetDataAsync(ChunkRdTexture, 0, Callable.From<byte[]>((bytes) =>
    {
      var width = chunk.DensityTexture.GetWidth();
      var height = chunk.DensityTexture.GetHeight();
      var depth = chunk.DensityTexture.GetDepth();

      Array<Image> images = [];
      int layerSize = width * height;
      for (int i = 0; i < depth; i++)
      {
        var sliceI1 = i * layerSize;
        var sliceI2 = sliceI1 + layerSize;
        images.Add(Image.CreateFromData(width, height, false, Image.Format.R8, bytes[sliceI1..sliceI2]));
      }
      var imgTexture = new ImageTexture3D();

      imgTexture.Create(Image.Format.R8, width, height, depth, false, images);
      // imgTexture.Update()

      // chunk.DensityTexture.Update(images);
      chunk.DensityTexture = imgTexture;

      // // GD.Print($"uniformSet: {uniformSet.Id}");
      // RD.FreeRid(uniformSet);

      // // GD.Print($"texture: {texture.Id}");
      // RD.FreeRid(texture);
      // // GD.Print($"digeTexture: {digeTexture.Id}");
      // RD.FreeRid(digeTexture);
      // // GD.Print($"offsetBuffer: {offsetBuffer.Id}");
      // RD.FreeRid(offsetBuffer);
      

      // // GD.Print($"pipeline: {pipeline.Id}");
      // RD.FreeRid(pipeline);
      // // GD.Print($"shader: {shader.Id}");
      // RD.FreeRid(shader);


      // DefferedMeshUpdate();

      callback.Invoke();
    }));
  }

  public void Dispose()
  {
    throw new NotImplementedException();
  }
}