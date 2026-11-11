#nullable enable
using System;
using System.Collections.Generic;
using Godot;
using FNL = Voxel.Noise.FastNoiseLite;

namespace Voxel;

/// <summary>
/// Pure managed copy of a Godot FastNoiseLite resource. Godot's resource wraps the same C++ library,
/// so copying the settings reproduces the same noise without an interop call per sample.
/// Read-only after construction, so it is safe to sample from many threads.
/// </summary>
public sealed class NoiseSampler
{
  private readonly FNL _noise = new();
  private readonly FNL? _warp;
  private readonly Vector3 _offset;

  /// <summary>Must be constructed on the main thread (reads Godot properties).</summary>
  public NoiseSampler(Godot.FastNoiseLite src)
  {
    _offset = src.Offset;

    _noise.SetSeed(src.Seed);
    _noise.SetFrequency(src.Frequency);
    _noise.SetNoiseType((FNL.NoiseType)(int)src.NoiseType); // same order in both enums
    _noise.SetFractalType((FNL.FractalType)(int)src.FractalType); // None, FBm, Ridged, PingPong
    _noise.SetFractalOctaves(src.FractalOctaves);
    _noise.SetFractalLacunarity(src.FractalLacunarity);
    _noise.SetFractalGain(src.FractalGain);
    _noise.SetFractalWeightedStrength(src.FractalWeightedStrength);
    _noise.SetFractalPingPongStrength(src.FractalPingPongStrength);
    _noise.SetCellularDistanceFunction((FNL.CellularDistanceFunction)(int)src.CellularDistanceFunction);
    _noise.SetCellularReturnType((FNL.CellularReturnType)(int)src.CellularReturnType);
    _noise.SetCellularJitter(src.CellularJitter);

    if (src.DomainWarpEnabled)
    {
      _warp = new FNL();
      _warp.SetSeed(src.Seed);
      _warp.SetDomainWarpType((FNL.DomainWarpType)(int)src.DomainWarpType);
      _warp.SetDomainWarpAmp(src.DomainWarpAmplitude);
      _warp.SetFrequency(src.DomainWarpFrequency);
      _warp.SetFractalType((int)src.DomainWarpFractalType switch
      {
        1 => FNL.FractalType.DomainWarpProgressive,
        2 => FNL.FractalType.DomainWarpIndependent,
        _ => FNL.FractalType.None,
      });
      _warp.SetFractalOctaves(src.DomainWarpFractalOctaves);
      _warp.SetFractalLacunarity(src.DomainWarpFractalLacunarity);
      _warp.SetFractalGain(src.DomainWarpFractalGain);
    }
  }

  /// <summary>Equivalent to Godot's FastNoiseLite.GetNoise3D.</summary>
  public float Get(float x, float y, float z)
  {
    _warp?.DomainWarp(ref x, ref y, ref z);
    return _noise.GetNoise(x + _offset.X, y + _offset.Y, z + _offset.Z);
  }
}

/// <summary>
/// Terrain density and color as functions of world position (in ChunkManager-local space).
/// Thread-safe.
/// </summary>
public sealed class TerrainGenerator
{
  private readonly NoiseSampler _density;
  private readonly NoiseSampler _hue;
  private readonly Vector3 _clearCenter;
  private readonly float _clearRadius, _clearEdge;
  public EditStore Edits { get; }

  /// <summary>
  /// clearRadius &gt; 0 hollows out a sphere of empty space around clearCenter (the spawn), its wall blending into
  /// the rock over clearEdge units.
  /// </summary>
  public TerrainGenerator(Godot.FastNoiseLite density, Godot.FastNoiseLite hue, EditStore edits,
    Vector3 clearCenter = default, float clearRadius = 0f, float clearEdge = 8f)
  {
    _density = new NoiseSampler(density);
    _hue = new NoiseSampler(hue);
    Edits = edits;
    _clearCenter = clearCenter;
    _clearRadius = clearRadius;
    _clearEdge = Math.Max(clearEdge, 0.5f);
  }

  /// <summary>Unedited density in [0, 1]: the noise, minus the spawn clearing.</summary>
  public float RawDensity(float x, float y, float z)
  {
    float wall = 1f;
    if (_clearRadius > 0f)
    {
      float dx = x - _clearCenter.X, dy = y - _clearCenter.Y, dz = z - _clearCenter.Z;
      float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
      // 0 inside, the iso level (0.5) at the radius, solid-allowed past radius + edge / 2.
      wall = Math.Clamp(0.5f + (dist - _clearRadius) / _clearEdge, 0f, 1f);
      if (wall <= 0f) return 0f; // deep inside: no noise needed
    }
    float n = _density.Get(x, y, z);
    return Math.Min(Math.Clamp((n + 1f) * 2f, 0f, 1f), wall);
  }

  /// <summary>Density with the given edits (sorted by Seq) applied.</summary>
  public float Density(Vector3 p, List<SphereEdit> edits)
  {
    float d = RawDensity(p.X, p.Y, p.Z);
    foreach (var e in edits)
    {
      d = e.Apply(p, d);
    }
    return d;
  }

  /// <summary>Color at a sample, from its unedited density. Same formula as the old GenerateChunk.</summary>
  public Color ColorAt(Vector3 p, float raw)
  {
    float hue = _hue.Get(p.X, p.Y, p.Z) + 1f;
    return Color.FromHsv(hue, Math.Clamp((raw - 0.5f) * 8f, 0f, 1f), Math.Clamp(raw - hue * 0.25f, 0f, 1f));
  }

  public static uint Pack(Color c) => (uint)c.ToRgba32() | 0xFFu; // alpha forced to 255 so 0 means "not computed"

  public static Color Unpack(uint rgba) => new Color(rgba);
}
