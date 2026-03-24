using System;
using System.Linq;
using Godot;
using Godot.Collections;

public class ChunkComputeManager : IDisposable
{
  private RenderingDevice RD { get { return RenderingServer.GetRenderingDevice(); } }

  private Rid DigShader;
  private Rid DigPipeline;
  private Rid ChunkRdTexture;
  private Rid DigBuffer;
  private Rid DigUniformSet;
  public void EnsureDigPipeline(int chunkSize)
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
    DigBuffer = RD.StorageBufferCreate(16);

    var digUniform = new RDUniform
    {
      UniformType = RenderingDevice.UniformType.StorageBuffer,
      Binding = 1
    };

    digUniform.AddId(DigBuffer);

    // uniform set
    DigUniformSet = RD.UniformSetCreate(
      [texture_uniform, digUniform],
      DigShader, // RDShader
      0       // set index
    );

    // var computeList = RD.ComputeListBegin();

    // RD.ComputeListBindComputePipeline(computeList, pipeline);
    // RD.ComputeListBindUniformSet(computeList, DigUniformSet, 0);
    // RD.ComputeListDispatch(computeList, 11, 11, 11);
    // RD.ComputeListEnd();
  }

  public void Dig(Vector3 pos, float radius, ChunkData chunk, Action callback)
  {
    EnsureDigPipeline(chunk.DensityTexture.GetWidth());    

    Array<byte> bytes = [];
    var images = chunk.DensityTexture.GetData();
    foreach (var image in images)
    {
      bytes.AddRange(image.GetData());
    }
    RD.TextureUpdate(ChunkRdTexture, 0, bytes.ToArray());

    byte[] digBytes = new byte[16];
    BitConverter.GetBytes(pos.X).CopyTo(digBytes, 0);
    BitConverter.GetBytes(pos.Y).CopyTo(digBytes, 4);
    BitConverter.GetBytes(pos.Z).CopyTo(digBytes, 8);

    BitConverter.GetBytes(radius).CopyTo(digBytes, 12);

    RD.BufferUpdate(DigBuffer, 0, 16, digBytes);

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

      chunk.DensityTexture = imgTexture;
      // chunk.DensityTexture.Update(images);

      callback.Invoke();
    }));
  }

  public void Dispose()
  {
    // GD.Print($"uniformSet: {uniformSet.Id}");
    RD.FreeRid(DigUniformSet);

    // GD.Print($"texture: {texture.Id}");
    RD.FreeRid(ChunkRdTexture);
    // GD.Print($"offsetBuffer: {offsetBuffer.Id}");
    RD.FreeRid(DigBuffer);


    // GD.Print($"pipeline: {pipeline.Id}");
    RD.FreeRid(DigPipeline);
    // GD.Print($"shader: {shader.Id}");
    RD.FreeRid(DigShader);

    RD.Dispose();
  }
}