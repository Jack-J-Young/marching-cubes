using System;
using Godot;
using Godot.Collections;

[Tool]
public partial class ChunkManager : Node3D
{
  [Export]
  public int Range;

  [Export]
  public FastNoiseLite Noise { get; set; }

  private Dictionary<Vector3I, Chunk> Chunks = new Dictionary<Vector3I, Chunk>();

  [ExportToolButton("Test!")]
  public Callable testButton => Callable.From(Test);

  [ExportToolButton("Test2!")]
  public Callable test2Button => Callable.From(Test2);

  public void Test()
  {
    // Noise.Offset = new Vector3(32, 32, 32);
    // GenerateChunk(1,0);
    for (int x = -3; x < 3; x++)
    {
      for (int y = -3; y < 3; y++)
      {
        for (int z = -3; z < 3; z++)
        {
          GenerateChunk(x, y, z);
        }
      }
    }
  }

  public void Test2()
  {
    Chunks.Clear();
    foreach (var node in GetChildren())
    {
      RemoveChild(node);
    }
  }

  public override void _Ready()
  {
    base._Ready();
    Test2();
    Test();
  }

  public void Dig(Vector3 pos, int radius)
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
      Width = (uint)radius*2,
      Height = (uint)radius*2,
      Depth = (uint)radius*2,
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
    rd.ComputeListDispatch(computeList, (uint)radius*2, (uint)radius *2, (uint)radius *2);
    rd.ComputeListEnd();

    var bytes = rd.TextureGetData(texture, 0);

    Array<Image> images = [];
    int layerSize = radius * radius * 4;
    for (int i = 0; i < bytes.Length; i += layerSize)
    {
      images.Add(Image.CreateFromData(radius*2, radius*2, false, Image.Format.R8, bytes[i..(i + layerSize)]));
    }
    var imgTexture = new ImageTexture3D();

    imgTexture.Create(Image.Format.R8, radius * 2, radius * 2, radius * 2, false, images);

    // digTexture = imgTexture;

    var chunkPos = pos / 32;
    var chunkMin = (chunkPos - Vector3.One * radius / 32f).Floor();
    var chunkMax = (chunkPos + Vector3.One * radius / 32f).Ceil();

    for (int x = (int)chunkMin.X; x <= (int)chunkMax.X; x++)
    {
      for (int y = (int)chunkMin.Y; y <= (int)chunkMax.Y; y++)
      {
        for (int z = (int)chunkMin.Z; z <= (int)chunkMax.Z; z++)
        {
          var key = new Vector3I(x, y, z);
          if (Chunks.ContainsKey(key))
          {
            Chunks[key].Dig(pos, imgTexture);
          }
        }
      }
    }
  }

  public void GenerateChunk(int x, int y, int z)
  {
    // var chunkPos = new Vector3(x, y, z)*32f;

    var chunkPos = new Vector3I(x, y, z);
    var localPos = chunkPos*32;

    // Noise.Offset = localPos;
    // Noise.
    // var data = Noise.GetImage3D(33, 33, 33, normalize: false);
    Array<Image> data = [];

    for (int iz = 0; iz < 33; iz++)
    {
      byte[] bytes = new byte[33 * 33];
      for (int ix = 0; ix < 33; ix++)
      {
        for (int iy = 0; iy < 33; iy++)
        {
          var i = ix + iy * 33;
          float fVal = Noise.GetNoise3D(ix + localPos.X, iy + localPos.Y, iz + localPos.Z);
          fVal = (fVal + 1) * 0.5f;
          fVal *= 255f;
          byte bVal = (byte)fVal;

          bytes[i] = bVal;
        }
      }
      data.Add(Image.CreateFromData(33, 33, false, Image.Format.R8, bytes));
    }

    
    var chunkScene = GD.Load<PackedScene>("res://chunk.tscn");
    var chunk = chunkScene.Instantiate<Chunk>();
    chunk.Data = new ChunkData();
    chunk.Data.DensityTexture.Create(Image.Format.R8, 33, 33, 33, false, data);
    


    // Array<Image> data = [];

    // for (int _z = 0; _z < 32; _z++)
    // {
    //   var layerData = new byte[32 * 32];
    //   for (int _x = 0; _x < 32; _x++)
    //   {
    //     for (int _y = 0; _y < 32; _y++)
    //     {
    //       var localPos = new Vector3(_x, _y, _z);
    //       var noiseF = Noise.GetNoise3Dv(localPos + chunkPos + new Vector3(100, 100, 100));
    //       noiseF = (noiseF + 1)/2;
    //       var noiseB = (byte)(noiseF * 255f);
    //       layerData[_x + _y * 32] = noiseB;
    //     }
    //   }
    //   data.Add(Image.CreateFromData(32, 32, false, Image.Format.R8, layerData));
    // }

    chunk.Position = localPos;
    // chunk.Scale = Vector3.One * 0.999f;
    // var chunk = new Chunk();
    AddChild(chunk);
    // chunk.UpdateChunkFromTexture(data);
    chunk.DefferedMeshUpdate();

    Chunks.Add(chunkPos, chunk);
    
  }
}
