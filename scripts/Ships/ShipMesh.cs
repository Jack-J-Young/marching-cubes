#nullable enable
using System.Collections.Generic;
using Godot;

namespace Ships;

/// <summary>
/// Builds a ship's render mesh: one quad per block face that isn't covered by a neighbouring block, coloured by
/// block type. A thruster's nozzle face and a laser's emitter face (their Facing side) are always drawn, in an
/// accent colour, so you can see which way parts point even when something is built against them.
/// </summary>
public static class ShipMesh
{
  public static readonly Color BlockColor = new(0.72f, 0.75f, 0.8f);
  public static readonly Color ThrusterColor = new(0.42f, 0.43f, 0.46f);
  public static readonly Color NozzleColor = new(1f, 0.55f, 0.15f);
  public static readonly Color LaserColor = new(0.3f, 0.3f, 0.34f);
  public static readonly Color EmitterColor = new(0.95f, 0.15f, 0.15f);

  public static Color BodyColor(BlockType t) => t switch
  {
    BlockType.Thruster => ThrusterColor,
    BlockType.Laser => LaserColor,
    _ => BlockColor,
  };

  // For each face direction: two tangent axes u, v with u x v = the face normal.
  private static readonly (Vector3I n, Vector3 u, Vector3 v)[] Faces =
  [
    (Vector3I.Right, Vector3.Up, Vector3.Back),
    (Vector3I.Left, Vector3.Back, Vector3.Up),
    (Vector3I.Up, Vector3.Back, Vector3.Right),
    (Vector3I.Down, Vector3.Right, Vector3.Back),
    (Vector3I.Back, Vector3.Right, Vector3.Up),
    (Vector3I.Forward, Vector3.Up, Vector3.Right),
  ];

  /// <summary>Number of exposed faces (for tests).</summary>
  public static int ExposedFaces(ShipGrid grid)
  {
    int n = 0;
    foreach (var c in grid.Cells)
      foreach (var f in Faces)
        if (!grid.Contains(c + f.n)) n++;
    return n;
  }

  /// <summary>Null when the grid is empty. hull replaces the hull blocks' colour (e.g. to tell AI ships apart).</summary>
  public static ArrayMesh? Build(ShipGrid grid, Color? hull = null)
  {
    var pos = new List<Vector3>();
    var nrm = new List<Vector3>();
    var col = new List<Color>();
    var idx = new List<int>();
    foreach (var (c, block) in grid.Blocks)
    {
      var cell = (Vector3)c;
      foreach (var (n, u, v) in Faces)
      {
        bool marked = block.Type != BlockType.Hull && n == block.Facing;
        if (grid.Contains(c + n) && !marked) continue;
        var color = marked ? (block.Type == BlockType.Thruster ? NozzleColor : EmitterColor)
          : block.Type == BlockType.Hull && hull is Color h ? h : BodyColor(block.Type);
        var normal = (Vector3)n;
        // The face lies on the cell's side facing n: its corner is the cell corner plus n where n is positive.
        var origin = cell + new Vector3(Mathf.Max(normal.X, 0), Mathf.Max(normal.Y, 0), Mathf.Max(normal.Z, 0));
        int b = pos.Count;
        pos.Add(origin);
        pos.Add(origin + u);
        pos.Add(origin + u + v);
        pos.Add(origin + v);
        for (int k = 0; k < 4; k++)
        {
          nrm.Add(normal);
          col.Add(color);
        }
        // Godot's front faces wind clockwise seen from outside; (u, v) is counter-clockwise seen along -n.
        idx.Add(b);
        idx.Add(b + 2);
        idx.Add(b + 1);
        idx.Add(b);
        idx.Add(b + 3);
        idx.Add(b + 2);
      }
    }
    if (pos.Count == 0) return null;

    var arrays = new Godot.Collections.Array();
    arrays.Resize((int)Mesh.ArrayType.Max);
    arrays[(int)Mesh.ArrayType.Vertex] = pos.ToArray();
    arrays[(int)Mesh.ArrayType.Normal] = nrm.ToArray();
    arrays[(int)Mesh.ArrayType.Color] = col.ToArray();
    arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
    var mesh = new ArrayMesh();
    mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
    return mesh;
  }
}
