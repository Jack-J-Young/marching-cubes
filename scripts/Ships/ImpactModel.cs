#nullable enable
using System;
using Godot;

namespace Ships;

/// <summary>
/// How hard a ship hits the terrain and what that does. Only the velocity straight into the surface counts
/// (head-on transfers everything, a skid at a shallow angle almost nothing). The kinetic energy of that part,
/// 0.5 * mass * vn^2, is split: DeformShare of it excavates rock at TerrainHardness energy per unit volume (the
/// crater), the rest is lost. The ship keeps the speed that matches what's left, so instead of bouncing it carries
/// on into the crater it just made.
/// </summary>
public static class ImpactModel
{
  public struct Settings
  {
    /// <summary>Slower than this straight into the surface: no crater (resting or sliding ships don't dig).</summary>
    public float MinSpeed;
    /// <summary>Share of the impact energy that goes into breaking rock (0..1); the rest is lost.</summary>
    public float DeformShare;
    /// <summary>Energy needed to excavate one unit of volume (asteroid rock: high).</summary>
    public float Hardness;
    /// <summary>Largest crater (dig radius).</summary>
    public float MaxCraterRadius;
  }

  public struct Result
  {
    /// <summary>Speed straight into the surface before the hit.</summary>
    public float NormalSpeed;
    /// <summary>Dig radius for the crater (the brush's falloff carves solid rock out to about half of it).</summary>
    public float CraterRadius;
    /// <summary>Speed straight into the surface the ship keeps.</summary>
    public float RemainingNormalSpeed;
    /// <summary>Energy that went into the crater.</summary>
    public float Energy;
  }

  /// <summary>
  /// velocity: of the ship at the contact point; normal: surface normal pointing out of the terrain (toward the
  /// ship). False if the hit is too gentle to dig.
  /// </summary>
  public static bool Evaluate(float mass, Vector3 velocity, Vector3 normal, in Settings s, out Result r)
  {
    r = default;
    float vn = -velocity.Dot(normal);
    r.NormalSpeed = vn;
    if (vn < s.MinSpeed || mass <= 0 || s.Hardness <= 0) return false;
    float energy = 0.5f * mass * vn * vn * Math.Clamp(s.DeformShare, 0f, 1f);
    float volume = energy / s.Hardness;
    // Solid rock is carved out to about half the dig radius (the brush fades to nothing at its rim).
    float carved = MathF.Cbrt(3f * volume / (4f * MathF.PI));
    r.CraterRadius = Math.Min(2f * carved, s.MaxCraterRadius);
    r.Energy = energy;
    r.RemainingNormalSpeed = vn * MathF.Sqrt(1f - Math.Clamp(s.DeformShare, 0f, 1f));
    return true;
  }
}
