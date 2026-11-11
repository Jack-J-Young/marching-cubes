#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Godot;
using Voxel;

namespace Ships;

/// <summary>
/// Finds a way for a ship through the asteroid field, on a background task, in two levels (ChunkManager-local space):
///   - Coarse: A* over 32-unit cells (one per detail chunk). Cells the terrain already knows are empty space
///     (UniformChunks: a chunk built with no surface, all air, at any LOD) cost 1; others cost more the more rock
///     a few samples find in them. So the route goes through open space wherever it can, at any distance.
///   - Fine: A* over 4-unit cells from the ship to a point up to FineReach along that route (or to the goal when
///     it's that close). A cell is solid if any of 7 samples in it is near rock; free if no solid cell is within the
///     ship's clearance radius. Inside chunks known to be empty (LOD 0-1, fine enough to trust) nothing is sampled at
///     all. Around the start the clearance is waived (the ship may be nosing near rock and has to get out).
///   - The fine path is pulled tight (each waypoint the furthest later one in clear line). Beyond is how much of the
///     coarse route is left after it, so the ship keeps its speed and replans as it goes.
/// The fine search stops at the first free cell within its arrive radius; if it can't get there within its limits it
/// leads to the explored cell nearest the goal (Partial).
/// </summary>
public sealed class ShipPathfinder
{
  public const float Cell = 4f;
  public const float Coarse = 32f;
  /// <summary>Density counted as rock for planning (under the 0.5 surface: a margin).</summary>
  public const float SolidLevel = 0.4f;
  public const int MaxExpansions = 30000;
  public const int MaxCoarseExpansions = 20000;
  /// <summary>Fine search box: start and goal grown by this, at most MaxBox per side.</summary>
  public const float BoxMargin = 96f, MaxBox = 512f;
  /// <summary>Goals further than this go through the coarse route first.</summary>
  public const float FineReach = 160f;
  /// <summary>Coarse search box: at most this many cells per side.</summary>
  public const int MaxCoarseCells = 64;

  public sealed record Request(Vector3 Start, Vector3 Goal, float Clearance, float ArriveRadius, int EditStamp);

  /// <summary>
  /// Points: the fine path (Points[0] = start). Found: it reaches its goal (the sub-goal on a long trip). Beyond: coarse
  /// route length left after the fine path's goal (0 when it is the real goal). CoarseCells: coarse route length in
  /// cells (0 if none was needed); KnownEmptyCells: how many of them were known empty.
  /// </summary>
  public sealed record Result(Request Request, List<Vector3> Points, bool Found, bool Partial, int Expanded, double Ms,
    float Beyond, int CoarseCells, int KnownEmptyCells);

  private Task<Result>? _task;

  public bool Building => _task != null && !_task.IsCompleted;

  /// <summary>Starts planning in the background (ignored while one is running).</summary>
  public void Start(TerrainGenerator gen, Request req, UniformChunks? uniform = null)
  {
    if (Building) return;
    _task = Task.Run(() => Plan(gen, req, uniform));
  }

  /// <summary>The finished plan, once (null while planning or if nothing new is ready).</summary>
  public Result? TakeResult()
  {
    if (_task == null || !_task.IsCompleted) return null;
    var task = _task;
    _task = null;
    if (task.IsFaulted)
    {
      GD.PushError($"Ship path planning failed: {task.Exception}");
      return null;
    }
    return task.Result;
  }

  /// <summary>Plans synchronously (thread-safe: reads only the generator, the edit store and the uniform chunks).</summary>
  public static Result Plan(TerrainGenerator gen, Request req, UniformChunks? uniform = null)
  {
    var sw = Stopwatch.StartNew();
    var field = new Field(gen, uniform);
    var fineGoal = req.Goal;
    float arrive = req.ArriveRadius;
    float beyond = 0;
    int coarseCells = 0, knownEmpty = 0;
    if (req.Start.DistanceTo(req.Goal) > FineReach)
    {
      var route = CoarseRoute(field, req.Start, req.Goal, out knownEmpty);
      coarseCells = route.Count;
      // The furthest route point still within reach is the fine search's goal.
      int sub = Math.Min(1, route.Count - 1);
      for (int i = 1; i < route.Count; i++)
      {
        if (route[i].DistanceTo(req.Start) > FineReach) break;
        sub = i;
      }
      if (route.Count > 1 && sub < route.Count - 1)
      {
        fineGoal = route[sub];
        arrive = Math.Max(Coarse * 0.75f, Cell * 2f);
        for (int i = sub; i < route.Count - 1; i++) beyond += route[i].DistanceTo(route[i + 1]);
        beyond += Math.Max(0f, route[^1].DistanceTo(req.Goal) - req.ArriveRadius);
      }
    }

    var fineReq = req with { Goal = fineGoal, ArriveRadius = arrive };
    var (points, found, expanded) = FinePlan(field, fineReq);
    return new Result(req, points, found, !found && points.Count > 1, expanded, sw.Elapsed.TotalMilliseconds, beyond, coarseCells, knownEmpty);
  }

  // ---------------------------------------------------------------- coarse

  private static Vector3I CoarseOf(Vector3 p) => (Vector3I)(p / Coarse).Floor();
  private static Vector3 CoarseCentre(Vector3I c) => ((Vector3)c + Vector3.One * 0.5f) * Coarse;

  /// <summary>Coarse cell centres from the start's cell to the goal's (start and goal themselves at the ends).</summary>
  private static List<Vector3> CoarseRoute(Field field, Vector3 start, Vector3 goal, out int knownEmpty)
  {
    var s = CoarseOf(start);
    var g = CoarseOf(goal);
    var lo = new Vector3I(Math.Min(s.X, g.X), Math.Min(s.Y, g.Y), Math.Min(s.Z, g.Z)) - Vector3I.One * 3;
    var hi = new Vector3I(Math.Max(s.X, g.X), Math.Max(s.Y, g.Y), Math.Max(s.Z, g.Z)) + Vector3I.One * 3;
    // Keep the box at most MaxCoarseCells per side, around the middle (start and goal always inside).
    for (int a = 0; a < 3; a++)
    {
      int over = hi[a] - lo[a] + 1 - MaxCoarseCells;
      if (over <= 0) continue;
      int sMin = Math.Min(s[a], g[a]), sMax = Math.Max(s[a], g[a]);
      lo[a] = Math.Min(sMin, lo[a] + over / 2);
      hi[a] = Math.Max(sMax, hi[a] - (over - over / 2));
    }

    float H(Vector3I c) => ((Vector3)(c - g)).Length();
    var open = new PriorityQueue<Vector3I, float>();
    var cost = new Dictionary<Vector3I, float> { [s] = 0f };
    var from = new Dictionary<Vector3I, Vector3I>();
    var closed = new HashSet<Vector3I>();
    open.Enqueue(s, H(s));
    var best = s;
    float bestH = H(s);
    int expanded = 0;
    while (open.Count > 0 && expanded < MaxCoarseExpansions)
    {
      var c = open.Dequeue();
      if (!closed.Add(c)) continue;
      expanded++;
      float h = H(c);
      if (h < bestH)
      {
        bestH = h;
        best = c;
      }
      if (c == g) break;
      float g0 = cost[c];
      float here = field.CoarseCost(c);
      foreach (var (o, step) in Neighbours)
      {
        var n = c + o;
        if (closed.Contains(n) || n.X < lo.X || n.Y < lo.Y || n.Z < lo.Z || n.X > hi.X || n.Y > hi.Y || n.Z > hi.Z) continue;
        float ng = g0 + step * 0.5f * (here + field.CoarseCost(n));
        if (cost.TryGetValue(n, out var old) && old <= ng) continue;
        cost[n] = ng;
        from[n] = c;
        open.Enqueue(n, ng + 1.2f * H(n));
      }
    }
    var end = closed.Contains(g) ? g : best;
    var cells = new List<Vector3I>();
    for (var c = end; ; c = from[c])
    {
      cells.Add(c);
      if (c == s || !from.ContainsKey(c)) break;
    }
    cells.Reverse();
    knownEmpty = 0;
    var route = new List<Vector3> { start };
    for (int i = 1; i < cells.Count; i++)
    {
      if (field.CoarseKnownAir(cells[i])) knownEmpty++;
      route.Add(i == cells.Count - 1 && cells[i] == g ? goal : CoarseCentre(cells[i]));
    }
    return route;
  }

  // ---------------------------------------------------------------- fine

  private static (List<Vector3> Points, bool Found, int Expanded) FinePlan(Field field, Request req)
  {
    var grid = new Grid(field, req);
    var start = Grid.CellOf(req.Start);
    var points = new List<Vector3> { req.Start };

    var open = new PriorityQueue<Vector3I, float>();
    var cost = new Dictionary<Vector3I, float> { [start] = 0f };
    var from = new Dictionary<Vector3I, Vector3I>();
    var closed = new HashSet<Vector3I>();
    open.Enqueue(start, H(start));
    var best = start;
    float bestH = H(start);
    bool found = false;
    int expanded = 0;
    Vector3I end = start;
    while (open.Count > 0 && expanded < MaxExpansions)
    {
      var c = open.Dequeue();
      if (!closed.Add(c)) continue;
      expanded++;
      float h = H(c);
      if (h < bestH)
      {
        bestH = h;
        best = c;
      }
      if (Grid.Centre(c).DistanceTo(req.Goal) <= req.ArriveRadius && (c == start || grid.Free(c)))
      {
        found = true;
        end = c;
        break;
      }
      float g = cost[c];
      foreach (var (o, step) in Neighbours)
      {
        var n = c + o;
        if (closed.Contains(n) || !grid.InBox(n) || !grid.Passable(n)) continue;
        float ng = g + step;
        if (cost.TryGetValue(n, out var old) && old <= ng) continue;
        cost[n] = ng;
        from[n] = c;
        open.Enqueue(n, ng + 1.5f * H(n));
      }
    }
    if (!found) end = best;

    var chain = new List<Vector3I>();
    for (var c = end; ; c = from[c])
    {
      chain.Add(c);
      if (c == start || !from.ContainsKey(c)) break;
    }
    chain.Reverse();
    var raw = new List<Vector3> { req.Start };
    for (int i = 1; i < chain.Count; i++) raw.Add(Grid.Centre(chain[i]));

    // Pull tight: from each kept point, jump to the furthest one in clear line.
    int at = 0;
    while (at < raw.Count - 1)
    {
      int next = at + 1;
      for (int j = raw.Count - 1; j > at + 1; j--)
        if (grid.ClearLine(raw[at], raw[j]))
        {
          next = j;
          break;
        }
      points.Add(raw[next]);
      at = next;
    }
    return (points, found, expanded);

    float H(Vector3I c) => Grid.Centre(c).DistanceTo(req.Goal) / Cell;
  }

  private static readonly List<(Vector3I Offset, float Step)> Neighbours = BuildNeighbours();

  private static List<(Vector3I, float)> BuildNeighbours()
  {
    var list = new List<(Vector3I, float)>();
    for (int z = -1; z <= 1; z++)
      for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
          if (x != 0 || y != 0 || z != 0)
            list.Add((new Vector3I(x, y, z), MathF.Sqrt(x * x + y * y + z * z)));
    return list;
  }

  /// <summary>
  /// The terrain as the planner sees it: density (noise plus edits, the edits bucketed so a sample only applies the
  /// ones near it) and which coarse cells are known to be empty. Caches per coarse cell.
  /// </summary>
  private sealed class Field
  {
    private readonly TerrainGenerator _gen;
    private readonly UniformChunks? _uniform;
    private readonly List<SphereEdit> _scratch = [];
    private readonly Dictionary<Vector3I, List<SphereEdit>> _edits = [];
    private readonly HashSet<Vector3I> _editBucketsLoaded = [];
    private readonly Dictionary<Vector3I, bool> _airFine = [], _airCoarse = [];
    private readonly Dictionary<Vector3I, float> _coarseCost = [];
    private const float Bucket = 32f;

    public Field(TerrainGenerator gen, UniformChunks? uniform)
    {
      _gen = gen;
      _uniform = uniform;
    }

    private static Vector3I BucketOf(Vector3 p) => (Vector3I)(p / Bucket).Floor();

    /// <summary>Edits touching this 32-unit bucket (fetched from the store the first time it's needed).</summary>
    private List<SphereEdit>? EditsIn(Vector3I bucket)
    {
      if (_editBucketsLoaded.Add(bucket))
      {
        var box = new Aabb((Vector3)bucket * Bucket, Vector3.One * Bucket);
        _gen.Edits.Query(box, -1, _scratch);
        if (_scratch.Count > 0) _edits[bucket] = [.. _scratch];
      }
      return _edits.TryGetValue(bucket, out var l) ? l : null;
    }

    public float Density(Vector3 p)
    {
      float d = _gen.RawDensity(p.X, p.Y, p.Z);
      if (EditsIn(BucketOf(p)) is { } list)
        foreach (var e in list)
          d = e.Apply(p, d);
      return d;
    }

    /// <summary>The coarse cell lies in a chunk of LOD 0-1 known to be all air: trust it, sample nothing.</summary>
    public bool FineKnownAir(Vector3I coarse)
    {
      if (_uniform == null) return false;
      if (!_airFine.TryGetValue(coarse, out var air))
        _airFine[coarse] = air = _uniform.KnownAir(new Aabb((Vector3)coarse * Coarse, Vector3.One * Coarse), 1, _scratch);
      return air;
    }

    /// <summary>The coarse cell lies in a chunk of any LOD known to be all air (good enough for route costs).</summary>
    public bool CoarseKnownAir(Vector3I coarse)
    {
      if (_uniform == null) return false;
      if (!_airCoarse.TryGetValue(coarse, out var air))
        _airCoarse[coarse] = air = _uniform.KnownAir(new Aabb((Vector3)coarse * Coarse, Vector3.One * Coarse), 6, _scratch);
      return air;
    }

    /// <summary>Cost per unit length through a coarse cell: 1 if known empty, up to 11 the more rock 4x4x4 samples find.</summary>
    public float CoarseCost(Vector3I c)
    {
      if (_coarseCost.TryGetValue(c, out var cost)) return cost;
      if (CoarseKnownAir(c))
      {
        cost = 1f;
      }
      else
      {
        var o = (Vector3)c * Coarse;
        int solid = 0;
        for (int z = 0; z < 4; z++)
          for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
              if (Density(o + new Vector3(x * 8 + 4, y * 8 + 4, z * 8 + 4)) >= SolidLevel) solid++;
        cost = 1.3f + 10f * solid / 64f; // unknown space costs a little more than known-empty space
      }
      _coarseCost[c] = cost;
      return cost;
    }
  }

  /// <summary>The fine planning grid: lazily evaluated, cached solid / free cells inside the search box.</summary>
  private sealed class Grid
  {
    private readonly Field _field;
    private readonly Request _req;
    private readonly Vector3I _lo, _hi;
    private readonly Dictionary<Vector3I, bool> _solid = [];
    private readonly Dictionary<Vector3I, bool> _free = [];
    private readonly List<Vector3I> _ball = [];
    private readonly float _escape;

    public Grid(Field field, Request req)
    {
      _field = field;
      _req = req;
      var box = new Aabb(req.Start, Vector3.Zero).Expand(req.Goal).Grow(BoxMargin);
      var size = box.Size.Min(Vector3.One * MaxBox);
      var centre = box.GetCenter();
      box = new Aabb(centre - size * 0.5f, size).Expand(req.Start);
      _lo = CellOf(box.Position);
      _hi = CellOf(box.End);
      int k = (int)MathF.Ceiling(req.Clearance / Cell);
      for (int z = -k; z <= k; z++)
        for (int y = -k; y <= k; y++)
          for (int x = -k; x <= k; x++)
            if (MathF.Sqrt(x * x + y * y + z * z) * Cell <= req.Clearance)
              _ball.Add(new Vector3I(x, y, z));
      _escape = req.Clearance + Cell * 1.5f;
    }

    public static Vector3I CellOf(Vector3 p) => (Vector3I)(p / Cell).Floor();
    public static Vector3 Centre(Vector3I c) => ((Vector3)c + Vector3.One * 0.5f) * Cell;

    public bool InBox(Vector3I c) =>
      c.X >= _lo.X && c.Y >= _lo.Y && c.Z >= _lo.Z && c.X <= _hi.X && c.Y <= _hi.Y && c.Z <= _hi.Z;

    private static readonly Vector3[] Samples =
    [
      Vector3.Zero,
      new(2, 0, 0), new(-2, 0, 0), new(0, 2, 0), new(0, -2, 0), new(0, 0, 2), new(0, 0, -2),
    ];

    public bool Solid(Vector3I c)
    {
      if (_solid.TryGetValue(c, out var s)) return s;
      var centre = Centre(c);
      // Fine cells nest in coarse ones (32 is a multiple of 4), so the whole sampled cell is in that chunk.
      s = false;
      if (!_field.FineKnownAir(CoarseOf(centre)))
        foreach (var o in Samples)
          if (_field.Density(centre + o) >= SolidLevel)
          {
            s = true;
            break;
          }
      _solid[c] = s;
      return s;
    }

    /// <summary>No rock within the clearance radius.</summary>
    public bool Free(Vector3I c)
    {
      if (_free.TryGetValue(c, out var f)) return f;
      f = true;
      foreach (var o in _ball)
        if (Solid(c + o))
        {
          f = false;
          break;
        }
      _free[c] = f;
      return f;
    }

    /// <summary>Free, or (near the start, to get away from rock) at least not rock itself.</summary>
    public bool Passable(Vector3I c)
    {
      if (Solid(c)) return false;
      return Free(c) || Centre(c).DistanceTo(_req.Start) <= _escape;
    }

    /// <summary>Every 2 units along a to b is in a passable cell.</summary>
    public bool ClearLine(Vector3 a, Vector3 b)
    {
      float len = a.DistanceTo(b);
      int n = Math.Max(1, (int)MathF.Ceiling(len / 2f));
      for (int i = 0; i <= n; i++)
        if (!Passable(CellOf(a.Lerp(b, i / (float)n)))) return false;
      return true;
    }
  }
}
