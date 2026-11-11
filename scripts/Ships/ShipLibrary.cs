#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace Ships;

/// <summary>
/// Saved ship designs ("prefabs"): one JSON file per ship in the user's data folder (user://ships, which is
/// ~/.local/share/godot/app_userdata/&lt;project&gt;/ships on Linux and %APPDATA%\Godot\app_userdata\&lt;project&gt;\ships
/// on Windows). Blocks are stored in ship space exactly as the ship's grid holds them. The built-in starter miner is
/// always the first entry and can't be overwritten.
/// </summary>
public sealed class ShipLibrary
{
  public const string BuiltInName = "Starter miner";
  public const int FormatVersion = 1;

  public sealed record Entry(string Name, string? Path)
  {
    public bool BuiltIn => Path == null;
  }

  private sealed class FileData
  {
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public int Version { get; set; } = FormatVersion;
    [JsonPropertyName("blocks")] public List<BlockData> Blocks { get; set; } = [];
  }

  private sealed class BlockData
  {
    [JsonPropertyName("cell")] public int[] Cell { get; set; } = [0, 0, 0];
    [JsonPropertyName("type")] public string Type { get; set; } = nameof(BlockType.Hull);
    [JsonPropertyName("facing")] public int[] Facing { get; set; } = [0, 0, 0];
  }

  private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

  /// <summary>The folder (absolute path) the ship files live in.</summary>
  public string Directory { get; }
  public IReadOnlyList<Entry> Entries => _entries;
  private readonly List<Entry> _entries = [];

  public ShipLibrary(string dir = "user://ships")
  {
    Directory = dir.StartsWith("user://") || dir.StartsWith("res://") ? ProjectSettings.GlobalizePath(dir) : dir;
    Refresh();
  }

  /// <summary>Re-reads the folder: the built-in ship first, then saved ships by name. Unreadable files are skipped.</summary>
  public void Refresh()
  {
    _entries.Clear();
    _entries.Add(new Entry(BuiltInName, null));
    if (!System.IO.Directory.Exists(Directory)) return;
    var found = new List<Entry>();
    foreach (var path in System.IO.Directory.GetFiles(Directory, "*.json"))
    {
      try
      {
        var data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(path), Json);
        if (data == null || data.Blocks.Count == 0) throw new InvalidDataException("no blocks");
        found.Add(new Entry(string.IsNullOrWhiteSpace(data.Name) ? System.IO.Path.GetFileNameWithoutExtension(path) : data.Name, path));
      }
      catch (Exception e)
      {
        GD.PushWarning($"Skipping ship file {path}: {e.Message}");
      }
    }
    _entries.AddRange(found.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase));
  }

  /// <summary>File-name-safe version of a ship name (invalid characters become _, empty becomes "Ship").</summary>
  public static string CleanName(string name)
  {
    var invalid = System.IO.Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
    var chars = name.Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
    var clean = new string(chars).Trim('.', ' ');
    return clean.Length == 0 ? "Ship" : clean.Length > 64 ? clean[..64] : clean;
  }

  public string PathFor(string name) => System.IO.Path.Combine(Directory, CleanName(name) + ".json");

  /// <summary>A saved ship with this name exists (the built-in name counts as taken).</summary>
  public bool Exists(string name) =>
    CleanName(name).Equals(BuiltInName, StringComparison.OrdinalIgnoreCase) || File.Exists(PathFor(name));

  /// <summary>First "Ship N" not taken yet.</summary>
  public string NextFreeName()
  {
    for (int i = 1; ; i++)
      if (!Exists($"Ship {i}")) return $"Ship {i}";
  }

  /// <summary>Saves (or overwrites) a ship; returns its entry. Refuses the built-in name.</summary>
  public Entry? Save(string name, ShipGrid grid)
  {
    var clean = CleanName(name);
    if (clean.Equals(BuiltInName, StringComparison.OrdinalIgnoreCase) || grid.Count == 0) return null;
    var data = new FileData { Name = clean };
    foreach (var (c, b) in grid.Blocks.OrderBy(kv => kv.Key.Z).ThenBy(kv => kv.Key.Y).ThenBy(kv => kv.Key.X))
      data.Blocks.Add(new BlockData { Cell = [c.X, c.Y, c.Z], Type = b.Type.ToString(), Facing = [b.Facing.X, b.Facing.Y, b.Facing.Z] });
    System.IO.Directory.CreateDirectory(Directory);
    var path = PathFor(clean);
    File.WriteAllText(path, JsonSerializer.Serialize(data, Json));
    Refresh();
    return _entries.FirstOrDefault(e => e.Path != null && System.IO.Path.GetFullPath(e.Path) == System.IO.Path.GetFullPath(path));
  }

  /// <summary>The blocks of an entry (null if its file can't be read any more).</summary>
  public List<(Vector3I Cell, Block Block)>? Load(Entry entry)
  {
    if (entry.BuiltIn) return ShipPrefabs.Miner();
    try
    {
      var data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(entry.Path!), Json);
      if (data == null) return null;
      var blocks = new List<(Vector3I, Block)>();
      foreach (var b in data.Blocks)
      {
        if (b.Cell.Length != 3 || b.Facing.Length != 3 || !Enum.TryParse<BlockType>(b.Type, out var type)) continue;
        var facing = new Vector3I(b.Facing[0], b.Facing[1], b.Facing[2]);
        blocks.Add((new Vector3I(b.Cell[0], b.Cell[1], b.Cell[2]), type == BlockType.Hull ? Block.Hull : new Block(type, facing)));
      }
      return blocks.Count > 0 ? blocks : null;
    }
    catch (Exception e)
    {
      GD.PushWarning($"Can't load ship {entry.Path}: {e.Message}");
      return null;
    }
  }
}
