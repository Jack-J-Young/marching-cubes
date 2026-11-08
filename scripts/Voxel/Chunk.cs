#nullable enable
using Godot;

namespace Voxel;

/// <summary>
/// One octree leaf. A plain C# object (no per-chunk script or _Process): ChunkManager owns the nodes and
/// drives everything. Scheduler flags (Queued / InFlight / Rerun / Priority) are only touched under the scheduler lock.
/// </summary>
public sealed class Chunk
{
  public readonly ChunkKey Key;

  /// <summary>Bumped whenever the chunk's content changes (dig). Results built from an older version are discarded.</summary>
  public volatile int Version;

  public volatile bool WantCollision;
  public volatile bool WantCache;
  public volatile bool Removed;

  // Scheduler state.
  public bool Queued;
  public bool InFlight;
  public bool Rerun;
  public float Priority = float.MaxValue;

  /// <summary>A result has been applied (possibly empty). Needed before it can replace older chunks.</summary>
  public bool Ready;
  /// <summary>Shown in the scene (meshed chunks only; empty chunks are Ready but have no node).</summary>
  public bool Visible;
  public bool HasCollision;
  public int LodMask = -1;

  public MeshInstance3D? MeshInstance;
  public StaticBody3D? Body;
  public CollisionShape3D? CollisionShape;

  public Chunk(ChunkKey key) => Key = key;
}
