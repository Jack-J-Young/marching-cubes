#nullable enable
using System;
using System.Collections.Generic;
using Godot;

namespace Ships;

/// <summary>
/// Which thrusters to fire for a wanted direction. A thruster pushes the ship along -Facing (away from its nozzle);
/// its throttle is how well that lines up with the wanted direction (0 if it points the other way), times how much
/// is wanted (|wish| up to 1). Everything is in ship space.
/// </summary>
public static class ThrustAllocator
{
  public static void Throttles(IReadOnlyList<(Vector3I Cell, Vector3I Facing)> thrusters, Vector3 wish, float[] throttles)
  {
    float amount = Math.Min(wish.Length(), 1f);
    var dir = amount > 1e-6f ? wish.Normalized() : Vector3.Zero;
    for (int i = 0; i < thrusters.Count; i++)
    {
      var push = -(Vector3)thrusters[i].Facing;
      throttles[i] = Math.Max(0f, push.Dot(dir)) * amount;
    }
  }

  /// <summary>Total push along dir from these throttles (force per thruster = force).</summary>
  public static float PushAlong(IReadOnlyList<(Vector3I Cell, Vector3I Facing)> thrusters, float[] throttles, Vector3 dir, float force)
  {
    float sum = 0;
    for (int i = 0; i < thrusters.Count; i++) sum += throttles[i] * force * (-(Vector3)thrusters[i].Facing).Dot(dir);
    return sum;
  }
}
