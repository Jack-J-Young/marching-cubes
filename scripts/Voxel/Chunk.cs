#nullable enable
using System.Collections.Generic;
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
  /// <summary>When the chunk was last dug (seconds); recently dug chunks keep their volume cached.</summary>
  public double EditedAt = double.NegativeInfinity;
  public volatile bool Removed;

  /// <summary>Build with edits up to this Seq (edit snapshot), so chunks committed together agree on their seams.</summary>
  public volatile int TargetSeq = -1;
  /// <summary>Edit snapshot of the mesh currently applied, -2 = none yet.</summary>
  public volatile int ShownSeq = -2;
  /// <summary>
  /// Edit snapshot that will be on screen just before the next build lands: ShownSeq, or the snapshot of a finished
  /// batch still queued to be shown. Workers morph from this state.
  /// </summary>
  public volatile int BaseSeq = -2;

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
  /// <summary>When its collision shape was last (re)built (seconds).</summary>
  public double CollisionBuiltAt = double.NegativeInfinity;
  /// <summary>Last build was made for collision but has no faces to collide with (only seam triangles).</summary>
  public bool NoCollisionFaces;
  public int LodMask = -1;

  public MeshInstance3D? MeshInstance;
  public StaticBody3D? Body;
  public CollisionShape3D? CollisionShape;

  /// <summary>Collision faces waiting in ChunkManager's budgeted collision queue (latest wins).</summary>
  public Variant? PendingCollision;
  public bool CollisionQueued;
  /// <summary>A rebuild was requested just to get this chunk's collision shape (cleared when it arrives).</summary>
  public bool CollisionRequested;
  /// <summary>Packed points on the shown mesh (see MeshResult.SurfacePoints), for sphere-cast aiming.</summary>
  public uint[]? SurfacePoints;
  /// <summary>
  /// While retiring: the active chunks covering this one's region (its replacements), worked out when the octree
  /// changes. Ready only ever goes false -&gt; true, so ReplacementsReadyCount (how many of the list, in order, are
  /// known ready) only moves forward and each frame's check is amortised O(1).
  /// </summary>
  public List<Chunk>? Replacements;
  public int ReplacementsReadyCount;
  /// <summary>Set while the chunk's mesh is animating from its previous surface.</summary>
  public MorphGroup? Morph;

  public Chunk(ChunkKey key) => Key = key;
}

/// <summary>Chunks committed in the same frame animate together through one shared material per seam mask.</summary>
public sealed class MorphGroup
{
  public double Start;
  public double Duration;
  public readonly System.Collections.Generic.Dictionary<int, ShaderMaterial> Materials = [];
  public readonly System.Collections.Generic.List<Chunk> Chunks = [];
}
