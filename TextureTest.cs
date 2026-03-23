using System;
using System.Linq;
using Godot;
using Godot.Collections;

[Tool]
public partial class TextureTest : Node3D
{
  [Export]
  public uint Size = 5;

  [ExportToolButton("Generate!")]
  public Callable GenerateButton => Callable.From(GenerateDigTexture);

  [Export]
  public Texture3D DigTexture {get;set;}

  [Export]
  public Texture3D OutTexture {get;set;}

  public Chunk Chunk { get {
      return GetNode<Chunk>("Chunk");
    }
  }

  [ExportToolButton("Dig!")]
  public Callable DigButton => Callable.From(Dig);

  [Export]
  public Vector3 DigPosition = Vector3.Zero;
  [ExportToolButton("Dig2!")]
  public Callable Dig2Button => Callable.From(Dig2);

  public void GenerateDigTexture()
  {
    var rd = RenderingServer.GetRenderingDevice();

    var shaderFile = ResourceLoader.Load<RDShaderFile>("res://dig_compute.glsl");

    var shader = rd.ShaderCreateFromSpirV(shaderFile.GetSpirV());

    var pipeline = rd.ComputePipelineCreate(shader);

    var textureView = new RDTextureView
    {
      FormatOverride = RenderingDevice.DataFormat.R8Unorm,
    };

    var textureFormat = new RDTextureFormat
    {
      TextureType = RenderingDevice.TextureType.Type3D,
      Width = Size,
      Height = Size,
      Depth = Size,
      Format = RenderingDevice.DataFormat.R8Unorm,
      UsageBits = RenderingDevice.TextureUsageBits.StorageBit |
                  RenderingDevice.TextureUsageBits.CanCopyToBit |
                  RenderingDevice.TextureUsageBits.CanUpdateBit |
                  RenderingDevice.TextureUsageBits.CanCopyFromBit |
                  RenderingDevice.TextureUsageBits.SamplingBit
    };

    var texture = rd.TextureCreate(textureFormat, textureView);

    var texture_uniform = new RDUniform
    {
      UniformType = RenderingDevice.UniformType.Image,
      Binding = 0,
    };

    texture_uniform.AddId(texture);

    // uniform set
    var uniformSet = rd.UniformSetCreate(
      [texture_uniform],
      shader, // RDShader
      0       // set index
    );

    var computeList = rd.ComputeListBegin();

    rd.ComputeListBindComputePipeline(computeList, pipeline);
    rd.ComputeListBindUniformSet(computeList, uniformSet, 0);
    rd.ComputeListDispatch(computeList, Size, Size, Size);
    rd.ComputeListEnd();

    var bytes = rd.TextureGetData(texture, 0);

    Array<Image> images = [];
    int layerSize = (int) (Size * Size);
    for (int i = 0; i < bytes.Length; i+=layerSize)
    {
      images.Add(Image.CreateFromData((int)Size, (int)Size, false, Image.Format.R8, bytes[i..(i+layerSize)]));
    }
    var imgTexture = new ImageTexture3D();

    imgTexture.Create(Image.Format.R8, (int)Size, (int)Size, (int)Size, false, images);

    DigTexture = imgTexture;
  }

  public void Dig()
  {
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

    var inputTexture = Chunk.Data.DensityTexture;

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

    var digInputTexture = DigTexture;

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

    var digTexture = rd.TextureCreate(digTextureFormat, digTextureView, [digBytes]);

    var dig_uniform = new RDUniform
    {
      UniformType = RenderingDevice.UniformType.Image,
      Binding = 1,
    };

    dig_uniform.AddId(digTexture);

    // uniform set
    var uniformSet = rd.UniformSetCreate(
      [texture_uniform, dig_uniform],
      shader, // RDShader
      0       // set index
    );

    var computeList = rd.ComputeListBegin();

    rd.ComputeListBindComputePipeline(computeList, pipeline);
    rd.ComputeListBindUniformSet(computeList, uniformSet, 0);
    rd.ComputeListDispatch(computeList, 8, 8, 8);
    rd.ComputeListEnd();

    // var whatever = rd.TextureGetData(texture, layer);

    OutTexture = new Texture3Drd()
    {
      TextureRdRid = texture
    };

    var outBytes = rd.TextureGetData(texture, 0);

    var width = Chunk.Data.DensityTexture.GetWidth();
    var height = Chunk.Data.DensityTexture.GetHeight();
    var depth = Chunk.Data.DensityTexture.GetDepth();

    Array<Image> images = [];
    int layerSize = width * height;
    for (int i = 0; i < outBytes.Length; i += layerSize)
    {
      images.Add(Image.CreateFromData(width, height, false, Image.Format.R8, outBytes[i..(i + layerSize)]));
    }
    // var imgTexture = new ImageTexture3D();

    // imgTexture.Create(Image.Format.R8, width, height, depth, false, images);
    // imgTexture.Update()

    Chunk.Data.DensityTexture.Update(images);

    Chunk.DefferedMeshUpdate();

    // Chunk.Data.DensityTexture = imgTexture;
    // rd.compute

    // rd.Free();
  }

  public void Dig2()
  {

    
    Vector3 offset = DigPosition.Floor();

    Vector3 subGridOffset = DigPosition - offset;
    // TODO use sub grid for dig texture;
    GenerateDigTexture();

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

    var inputTexture = Chunk.Data.DensityTexture;

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

    var digInputTexture = DigTexture;

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

    var digTexture = rd.TextureCreate(digTextureFormat, digTextureView, [digBytes]);

    var dig_uniform = new RDUniform
    {
      UniformType = RenderingDevice.UniformType.Image,
      Binding = 1,
    };

    dig_uniform.AddId(digTexture);
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

    OutTexture = new Texture3Drd()
    {
      TextureRdRid = texture
    };

    var outBytes = rd.TextureGetData(texture, 0);

    var width = Chunk.Data.DensityTexture.GetWidth();
    var height = Chunk.Data.DensityTexture.GetHeight();
    var depth = Chunk.Data.DensityTexture.GetDepth();

    Array<Image> images = [];
    int layerSize = width * height;
    for (int i = 0; i < outBytes.Length; i += layerSize)
    {
      images.Add(Image.CreateFromData(width, height, false, Image.Format.R8, outBytes[i..(i + layerSize)]));
    }
    // var imgTexture = new ImageTexture3D();

    // imgTexture.Create(Image.Format.R8, width, height, depth, false, images);
    // imgTexture.Update()

    Chunk.Data.DensityTexture.Update(images);

    Chunk.DefferedMeshUpdate();
  }
}
