using System;
using Godot;
using Godot.Collections;

public class ChunkComputeManager
{
  private readonly RenderingDevice RD = RenderingServer.GetRenderingDevice();

  public void GetDigTextureAsync(int size, Action<Array<Image>> callback)
  {
    var shaderFile = ResourceLoader.Load<RDShaderFile>("res://dig_compute.glsl");

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

    var bytes = RD.TextureGetDataAsync(texture, 0, Callable.From<byte[]>((bytes) =>
    {
      Array<Image> images = [];
      int layerSize = size * size;
      for (int i = 0; i < bytes.Length; i += layerSize)
      {
        images.Add(Image.CreateFromData(size, size, false, Image.Format.R8, bytes[i..(i + layerSize)]));
      }

      callback.Invoke(images);
    }));
  }
}