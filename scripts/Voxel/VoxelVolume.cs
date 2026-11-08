#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Godot;
using static Voxel.VoxelConst;

namespace Voxel;

/// <summary>
/// Samples for one chunk in plain managed arrays: samples -1 .. Cells+1 on each axis, spaced key.Step apart.
/// Raw = noise only, Density = Raw with edits applied, Colors = lazily computed packed RGBA (0 = not yet computed).
/// </summary>
public sealed class VoxelVolume
{
  public readonly float[] Raw = new float[VolumeSize];
  public readonly float[] Density = new float[VolumeSize];
  public readonly uint[] Colors = new uint[VolumeSize];

  public ChunkKey Key;

  /// <summary>Newest edit Seq already applied to Density.</summary>
  public int AppliedEditSeq = -1;

  public void Generate(TerrainGenerator gen, ChunkKey key)
  {
    Key = key;
    AppliedEditSeq = -1;
    Array.Clear(Colors);

    var origin = key.Origin;
    int step = key.Step;
    int i = 0;
    for (int z = -1; z <= Cells + 1; z++)
    {
      float wz = origin.Z + z * step;
      for (int y = -1; y <= Cells + 1; y++)
      {
        float wy = origin.Y + y * step;
        for (int x = -1; x <= Cells + 1; x++, i++)
        {
          float d = gen.RawDensity(origin.X + x * step, wy, wz);
          Raw[i] = d;
          Density[i] = d;
        }
      }
    }
  }

  /// <summary>Applies edits (sorted by Seq, all newer than AppliedEditSeq) to Density.</summary>
  public void ApplyEdits(List<SphereEdit> edits, int newestSeq)
  {
    var origin = (Vector3)Key.Origin;
    float step = Key.Step;
    foreach (var e in edits)
    {
      // Sample-index range covered by the sphere, clamped to the stored range.
      var lo = ((e.Center - Vector3.One * e.Radius - origin) / step).Floor();
      var hi = ((e.Center + Vector3.One * e.Radius - origin) / step).Ceil();
      int x0 = Math.Max(-1, (int)lo.X), y0 = Math.Max(-1, (int)lo.Y), z0 = Math.Max(-1, (int)lo.Z);
      int x1 = Math.Min(Cells + 1, (int)hi.X), y1 = Math.Min(Cells + 1, (int)hi.Y), z1 = Math.Min(Cells + 1, (int)hi.Z);
      for (int z = z0; z <= z1; z++)
        for (int y = y0; y <= y1; y++)
        {
          int idx = Index(x0, y, z);
          for (int x = x0; x <= x1; x++, idx++)
          {
            var p = origin + new Vector3(x, y, z) * step;
            Density[idx] = e.Apply(p, Density[idx]);
          }
        }
    }
    AppliedEditSeq = Math.Max(AppliedEditSeq, newestSeq);
  }

  /// <summary>True if the meshed range (samples 0 .. Cells) contains both solid and air.</summary>
  public bool HasSurface()
  {
    bool solid = false, air = false;
    for (int z = 0; z <= Cells; z++)
      for (int y = 0; y <= Cells; y++)
      {
        int idx = Index(0, y, z);
        for (int x = 0; x <= Cells; x++, idx++)
        {
          if (Density[idx] >= Iso) solid = true; else air = true;
          if (solid && air) return true;
        }
      }
    return false;
  }

  public Vector3 Gradient(int idx)
  {
    var d = Density;
    return new Vector3(d[idx + 1] - d[idx - 1], d[idx + Dim] - d[idx - Dim], d[idx + DimSq] - d[idx - DimSq]);
  }

  public Color ColorAt(TerrainGenerator gen, int idx, int x, int y, int z)
  {
    uint c = Colors[idx];
    if (c == 0)
    {
      var p = (Vector3)Key.Origin + new Vector3(x, y, z) * Key.Step;
      c = TerrainGenerator.Pack(gen.ColorAt(p, Raw[idx]));
      Colors[idx] = c;
    }
    return TerrainGenerator.Unpack(c);
  }
}

/// <summary>
/// Pool of volumes plus an LRU cache of recent ones (used for LOD0 chunks near the player, so digs skip regeneration).
/// Volumes are checked out exclusively: Take removes from the cache, Return puts back.
/// </summary>
public sealed class VolumeCache
{
  private readonly object _lock = new();
  private readonly Dictionary<ChunkKey, LinkedListNode<VoxelVolume>> _map = [];
  private readonly LinkedList<VoxelVolume> _lru = new();
  private readonly ConcurrentBag<VoxelVolume> _free = [];
  public int Capacity;

  public VolumeCache(int capacity) => Capacity = capacity;

  public int Count
  {
    get { lock (_lock) return _map.Count; }
  }

  public VoxelVolume? Take(ChunkKey key)
  {
    lock (_lock)
    {
      if (!_map.Remove(key, out var node))
      {
        return null;
      }
      _lru.Remove(node);
      return node.Value;
    }
  }

  public VoxelVolume Rent() => _free.TryTake(out var v) ? v : new VoxelVolume();

  public void Release(VoxelVolume v) => _free.Add(v);

  public void Return(VoxelVolume v, bool keep)
  {
    if (!keep || Capacity <= 0)
    {
      Release(v);
      return;
    }
    lock (_lock)
    {
      if (_map.Remove(v.Key, out var old))
      {
        _lru.Remove(old);
        if (old.Value != v) _free.Add(old.Value);
      }
      _map[v.Key] = _lru.AddFirst(v);
      while (_map.Count > Capacity)
      {
        var last = _lru.Last!;
        _lru.RemoveLast();
        _map.Remove(last.Value.Key);
        _free.Add(last.Value);
      }
    }
  }

  public void Drop(ChunkKey key)
  {
    var v = Take(key);
    if (v != null) Release(v);
  }
}
