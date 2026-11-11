#nullable enable
using System.Collections.Generic;
using Godot;

namespace Ships;

/// <summary>Ready-made ship layouts (ship space: -Z is forward, +Y is up).</summary>
public static class ShipPrefabs
{
  /// <summary>
  /// A mining ship about 9 wide, 5 tall and 9 long: a 3x3 core with swept wings and a cockpit hump.
  ///   - 10 rear thrusters (8 round the tail, 1 per wing) push it forward.
  ///   - 6 retro thrusters (4 round the nose, 1 per wing) push it back and brake.
  ///   - 4 strafe thrusters per side (2 on the wing tip, 2 by the wing root), and 4 on top and 4 underneath.
  ///   - 6 mining lasers firing forward (4 at the nose corners, 1 on each wing's leading edge).
  /// Mirrored left to right and roughly balanced top to bottom, so straight thrust barely turns it.
  /// </summary>
  public static List<(Vector3I Cell, Block Block)> Miner()
  {
    var blocks = new Dictionary<Vector3I, Block>();
    void Hull(int x, int y, int z) => blocks[new Vector3I(x, y, z)] = Block.Hull;
    void Part(int x, int y, int z, BlockType type, Vector3I facing) => blocks[new Vector3I(x, y, z)] = new Block(type, facing);

    // Core: 3 x 3 x 7.
    for (int z = -3; z <= 3; z++)
      for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
          Hull(x, y, z);

    // Nose (z = -4): hull centre, lasers on the corners, retro thrusters on the edges.
    Hull(0, 0, -4);
    foreach (int x in new[] { -1, 1 })
      foreach (int y in new[] { -1, 1 })
        Part(x, y, -4, BlockType.Laser, Vector3I.Forward);
    Part(-1, 0, -4, BlockType.Thruster, Vector3I.Forward);
    Part(1, 0, -4, BlockType.Thruster, Vector3I.Forward);
    Part(0, -1, -4, BlockType.Thruster, Vector3I.Forward);
    Part(0, 1, -4, BlockType.Thruster, Vector3I.Forward);

    // Tail (z = 4): a ring of 8 main thrusters round a hull centre.
    Hull(0, 0, 4);
    for (int y = -1; y <= 1; y++)
      for (int x = -1; x <= 1; x++)
        if (x != 0 || y != 0)
          Part(x, y, 4, BlockType.Thruster, Vector3I.Back);

    foreach (int side in new[] { -1, 1 })
    {
      var outward = side < 0 ? Vector3I.Left : Vector3I.Right;
      // Wings: two blocks out, four long.
      for (int z = -1; z <= 2; z++)
      {
        Hull(2 * side, 0, z);
        Hull(3 * side, 0, z);
      }
      Part(3 * side, 0, 3, BlockType.Thruster, Vector3I.Back);     // wing main thruster
      Part(3 * side, 0, -2, BlockType.Thruster, Vector3I.Forward); // wing retro
      Part(2 * side, 0, -2, BlockType.Laser, Vector3I.Forward);    // wing laser
      // Strafe thrusters: two on the wing tip, one each in front of and behind the wing root.
      Part(4 * side, 0, 0, BlockType.Thruster, outward);
      Part(4 * side, 0, 1, BlockType.Thruster, outward);
      Part(2 * side, 0, -3, BlockType.Thruster, outward);
      Part(2 * side, 0, 3, BlockType.Thruster, outward);
      // Up / down thrusters, front and back.
      foreach (int z in new[] { -2, 2 })
      {
        Part(side, 2, z, BlockType.Thruster, Vector3I.Up);
        Part(side, -2, z, BlockType.Thruster, Vector3I.Down);
      }
    }

    // Cockpit hump on top and a keel underneath.
    for (int z = -2; z <= 1; z++) Hull(0, 2, z);
    for (int z = -1; z <= 1; z++) Hull(0, -2, z);

    var list = new List<(Vector3I, Block)>(blocks.Count);
    foreach (var (c, b) in blocks) list.Add((c, b));
    return list;
  }
}
