#nullable enable
using System.Collections.Generic;
using Godot;

namespace Ships;

public enum BlockType { Hull, Thruster, Laser }

/// <summary>
/// One block: its type and, for thrusters and lasers, the ship-space axis it points along (the thruster's nozzle
/// faces that way and pushes the ship the opposite way; the laser fires that way). Hull blocks ignore Facing.
/// </summary>
public readonly record struct Block(BlockType Type, Vector3I Facing)
{
  public static readonly Block Hull = new(BlockType.Hull, Vector3I.Zero);
}

/// <summary>
/// The blocks of one ship: occupied cells in ship space, block c filling [c, c + 1) on each axis.
/// Pure data (no nodes) so it can be tested on its own.
/// </summary>
public sealed class ShipGrid
{
  public static readonly Vector3I[] Neighbors =
  [
    Vector3I.Right, Vector3I.Left, Vector3I.Up, Vector3I.Down, Vector3I.Back, Vector3I.Forward,
  ];

  private readonly Dictionary<Vector3I, Block> _blocks = [];

  public int Count => _blocks.Count;
  public IEnumerable<Vector3I> Cells => _blocks.Keys;
  public IEnumerable<KeyValuePair<Vector3I, Block>> Blocks => _blocks;

  public bool Contains(Vector3I c) => _blocks.ContainsKey(c);
  public bool Add(Vector3I c) => Add(c, Block.Hull);
  public bool Add(Vector3I c, Block block) => _blocks.TryAdd(c, block);
  public bool Remove(Vector3I c) => _blocks.Remove(c);
  public bool TryGet(Vector3I c, out Block block) => _blocks.TryGetValue(c, out block);

  /// <summary>Mean of the block centres (the centre of mass when every block weighs the same).</summary>
  public Vector3 Centroid()
  {
    if (_blocks.Count == 0) return Vector3.Zero;
    var sum = Vector3.Zero;
    foreach (var c in _blocks.Keys) sum += (Vector3)c;
    return sum / _blocks.Count + Vector3.One * 0.5f;
  }

  /// <summary>Groups of blocks connected through shared faces, largest first.</summary>
  public List<List<Vector3I>> Components()
  {
    var result = new List<List<Vector3I>>();
    var seen = new HashSet<Vector3I>();
    var stack = new Stack<Vector3I>();
    foreach (var start in _blocks.Keys)
    {
      if (!seen.Add(start)) continue;
      var group = new List<Vector3I>();
      stack.Push(start);
      while (stack.Count > 0)
      {
        var c = stack.Pop();
        group.Add(c);
        foreach (var d in Neighbors)
        {
          var n = c + d;
          if (_blocks.ContainsKey(n) && seen.Add(n)) stack.Push(n);
        }
      }
      result.Add(group);
    }
    result.Sort((a, b) => b.Count.CompareTo(a.Count));
    return result;
  }
}
