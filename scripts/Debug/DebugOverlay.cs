#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using Voxel;

/// <summary>
/// Minecraft-style debug screen.
///   F3       : text info (fps, frame timings, chunks, workers, render stats)
///   Ctrl+F3  : profiler pie charts
///              - frame: where the main thread's frame time goes (process / terrain / physics / render / idle)
///              - terrain workers: where the background mesher threads spend their time
///   1-9      : (profiler open) drill into that section of the frame pie, 0 goes back up
///   P        : (profiler open) log a snapshot to the console and user://profiler/
/// </summary>
public partial class DebugOverlay : CanvasLayer
{
  [Export] public ChunkManager? Terrain;
  /// <summary>Time constant for the frame pie's moving average, in seconds.</summary>
  [Export] public float SmoothingSeconds = 0.5f;
  /// <summary>Worker work is bursty, so its breakdown is averaged over longer.</summary>
  [Export] public float WorkerSmoothingSeconds = 1.5f;

  private const int FontSize = 14;
  private const int LineHeight = 17;
  private const float PanelWidth = 400;
  private const float PieRadius = 110;
  private const float PieSquash = 0.5f;
  private const float PieThickness = 10;

  private sealed class Section(string name, double ms)
  {
    public readonly string Name = name;
    public double Ms = ms;
    public readonly List<Section> Children = [];

    public Section Add(string name, double ms)
    {
      var s = new Section(name, Math.Max(0, ms));
      Children.Add(s);
      return s;
    }
  }

  private Control _canvas = null!;
  private bool _showInfo;
  private bool _showProfiler;
  private readonly List<string> _path = [];

  private ulong _lastTicks;
  private readonly Dictionary<string, double> _smooth = [];
  private Section? _frame;
  private double _gpuMs;
  private readonly Queue<(ulong at, double ms)> _recentFrames = new();
  private double _worstRecentMs;

  private readonly int _sections = (int)VoxelProfiler.Section.Count;
  private long[] _workerPrev = new long[(int)VoxelProfiler.Section.Count];
  private long[] _workerNow = new long[(int)VoxelProfiler.Section.Count];
  private readonly double[] _workerMsPerSec = new double[(int)VoxelProfiler.Section.Count];
  private long _chunksPrev;
  private double _chunksPerSec;
  private readonly int[] _lodCounts = new int[9];

  public override void _Ready()
  {
    Layer = 100;
    _canvas = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
    _canvas.SetAnchorsPreset(Control.LayoutPreset.FullRect);
    AddChild(_canvas);
    _canvas.Draw += OnDraw;

    Terrain ??= GetNodeOrNull<ChunkManager>("../ChunkManager");
    RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);

    _lastTicks = Time.GetTicksUsec();
    VoxelProfiler.Snapshot(_workerPrev);
    _chunksPrev = VoxelProfiler.ChunksBuilt;
  }

  public override void _Input(InputEvent e)
  {
    if (e is not InputEventKey { Pressed: true, Echo: false } k) return;

    if (k.Keycode == Key.F3)
    {
      if (k.CtrlPressed) _showProfiler = !_showProfiler;
      else _showInfo = !_showInfo;
      if (_showProfiler) _smooth.Clear();
      _canvas.QueueRedraw();
      GetViewport().SetInputAsHandled();
      return;
    }
    if (!_showProfiler || _frame == null) return;

    if (k.Keycode >= Key.Key1 && k.Keycode <= Key.Key9)
    {
      var shown = Sorted(CurrentSection()?.Children ?? []);
      int i = (int)(k.Keycode - Key.Key1);
      if (i < shown.Count && shown[i].Children.Count > 0) _path.Add(shown[i].Name);
      GetViewport().SetInputAsHandled();
    }
    else if (k.Keycode == Key.Key0)
    {
      if (_path.Count > 0) _path.RemoveAt(_path.Count - 1);
      GetViewport().SetInputAsHandled();
    }
    else if (k.Keycode == Key.P)
    {
      LogSnapshot();
      GetViewport().SetInputAsHandled();
    }
  }

  public override void _Process(double delta)
  {
    ulong now = Time.GetTicksUsec();
    double frameMs = (now - _lastTicks) / 1000.0;
    _lastTicks = now;

    SampleWorkers(frameMs);

    // Worst frame over the last 2 s, so hitches stay visible even though the averages below damp them.
    _recentFrames.Enqueue((now, frameMs));
    while (_recentFrames.Count > 0 && now - _recentFrames.Peek().at > 2_000_000) _recentFrames.Dequeue();
    if (!_showInfo && !_showProfiler) return;
    _worstRecentMs = _recentFrames.Max(f => f.ms);

    // A single long hitch gets the weight of at most a 50 ms frame, so it can't swamp the average for seconds.
    double alpha = 1 - Math.Exp(-Math.Min(frameMs, 50.0) / 1000.0 / SmoothingSeconds);
    _frame = SmoothTree(BuildFrame(frameMs), "", alpha);
    _gpuMs = Smooth("gpu", RenderingServer.ViewportGetMeasuredRenderTimeGpu(GetViewport().GetViewportRid()), alpha);
    _canvas.QueueRedraw();
  }

  // ------------------------------------------------------------------ data

  /// <summary>One frame of main-thread time, split into nested sections (raw, unsmoothed).</summary>
  private Section BuildFrame(double frameMs)
  {
    var rid = GetViewport().GetViewportRid();
    double processMs = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0;
    double physicsMs = Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0;
    double renderCpuMs = RenderingServer.ViewportGetMeasuredRenderTimeCpu(rid) + RenderingServer.GetFrameSetupTimeCpu();

    var root = new Section("frame", frameMs);

    var t = Terrain?.LastFrame;
    double terrainMs = t?.TotalMs ?? 0;
    var process = root.Add("process (scripts)", Math.Max(processMs, terrainMs));
    var terrain = process.Add("terrain (main thread)", terrainMs);
    if (t != null)
    {
      terrain.Add("apply meshes", t.ApplyMeshMs);
      terrain.Add("collision shapes", t.CollisionMs);
      terrain.Add("lod swaps", t.SwapMs);
      terrain.Add("octree diff", t.OctreeDiffMs);
      terrain.Add("dig + raycast", t.DigMs);
      terrain.Add("bookkeeping", t.TotalMs - t.ApplyMeshMs - t.CollisionMs - t.SwapMs - t.OctreeDiffMs - t.DigMs);
    }
    process.Add("other scripts + engine", process.Ms - terrainMs);

    root.Add("physics", physicsMs);
    root.Add("render (cpu)", renderCpuMs);
    root.Add("idle / vsync / gpu wait", frameMs - process.Ms - physicsMs - renderCpuMs);
    root.Ms = Math.Max(frameMs, root.Children.Sum(c => c.Ms));
    return root;
  }

  private Section SmoothTree(Section raw, string parentPath, double alpha)
  {
    string path = parentPath + "/" + raw.Name;
    var s = new Section(raw.Name, Smooth(path, raw.Ms, alpha));
    foreach (var c in raw.Children) s.Children.Add(SmoothTree(c, path, alpha));
    return s;
  }

  private double Smooth(string key, double value, double alpha)
  {
    double v = _smooth.TryGetValue(key, out var old) ? old + (value - old) * alpha : value;
    _smooth[key] = v;
    return v;
  }

  private void SampleWorkers(double frameMs)
  {
    if (frameMs <= 0) return;
    VoxelProfiler.Snapshot(_workerNow);
    double alpha = 1 - Math.Exp(-frameMs / 1000.0 / WorkerSmoothingSeconds);
    for (int i = 0; i < _sections; i++)
    {
      double msPerSec = VoxelProfiler.TicksToMs(_workerNow[i] - _workerPrev[i]) * 1000.0 / frameMs;
      _workerMsPerSec[i] += (msPerSec - _workerMsPerSec[i]) * alpha;
    }
    (_workerPrev, _workerNow) = (_workerNow, _workerPrev);

    long chunks = VoxelProfiler.ChunksBuilt;
    _chunksPerSec += ((chunks - _chunksPrev) * 1000.0 / frameMs - _chunksPerSec) * alpha;
    _chunksPrev = chunks;
  }

  private Section? CurrentSection()
  {
    var s = _frame;
    foreach (var name in _path)
    {
      s = s?.Children.FirstOrDefault(c => c.Name == name);
    }
    if (s == null && _frame != null)
    {
      _path.Clear();
      s = _frame;
    }
    return s;
  }

  private static List<Section> Sorted(List<Section> sections) => sections.OrderByDescending(c => c.Ms).ToList();

  // ------------------------------------------------------------------ drawing

  private void OnDraw()
  {
    var font = ThemeDB.FallbackFont;
    var size = _canvas.Size;

    if (_showInfo)
    {
      DrawLines(font, new Vector2(8, 8), InfoLines());
    }

    if (_showProfiler && _frame != null)
    {
      var current = CurrentSection() ?? _frame;
      var frameSlices = Sorted(current.Children)
        .Select(c => new Slice(c.Name, c.Ms, $"{Pct(c.Ms, Math.Max(current.Children.Sum(x => x.Ms), 1e-9))}",
          $"{Pct(c.Ms, _frame.Ms)}", c.Children.Count > 0))
        .ToList();
      string crumbs = "[0] " + string.Join(" > ", new[] { "frame" }.Concat(_path));
      double fps = Engine.GetFramesPerSecond();
      DrawPiePanel(font, new Vector2(size.X - 8, size.Y - 8),
        $"{crumbs}",
        $"{current.Ms:F2} ms of {_frame.Ms:F2} ms avg frame ({fps:F0} fps), worst {_worstRecentMs:F1} ms, gpu {_gpuMs:F2} ms",
        "% here", "% frame", frameSlices, numbered: true);

      int workers = Terrain?.WorkerCount ?? 0;
      double capacity = Math.Max(1, workers) * 1000.0;
      double busy = _workerMsPerSec.Sum();
      var workerSlices = new List<Slice>();
      for (int i = 0; i < _sections; i++)
      {
        workerSlices.Add(new Slice(VoxelProfiler.Names[i], _workerMsPerSec[i], Pct(_workerMsPerSec[i], capacity),
          _chunksPerSec > 0.5 ? $"{_workerMsPerSec[i] / _chunksPerSec:F2} ms" : "-", false));
      }
      workerSlices = workerSlices.OrderByDescending(s => s.Value).ToList();
      workerSlices.Add(new Slice("idle", Math.Max(0, capacity - busy), Pct(Math.Max(0, capacity - busy), capacity), "", false));
      DrawPiePanel(font, new Vector2(size.X - PanelWidth - 16, size.Y - 8),
        $"terrain workers ({workers} threads)",
        $"{_chunksPerSec:F0} chunks/s, {Pct(busy, capacity)} busy, queue {Terrain?.QueueLength ?? 0}",
        "% capacity", "per chunk", workerSlices, numbered: false);
    }
  }

  private readonly record struct Slice(string Name, double Value, string Col1, string Col2, bool Drillable);

  private static string Pct(double part, double whole) => whole <= 0 ? "-" : $"{100.0 * part / whole:F1}%";

  /// <summary>Draws a pie panel whose bottom-right corner is at `bottomRight`.</summary>
  private void DrawPiePanel(Font font, Vector2 bottomRight, string title, string subtitle, string col1Header, string col2Header,
    List<Slice> slices, bool numbered)
  {
    float pieHeight = PieRadius * 2 * PieSquash + PieThickness;
    float height = 2 * LineHeight + 14 + pieHeight + 14 + (slices.Count + 1) * LineHeight + 8;
    var pos = bottomRight - new Vector2(PanelWidth, height);
    float pieTop = pos.Y + 2 * LineHeight + 14;
    float legendTop = pieTop + pieHeight + 14;
    _canvas.DrawRect(new Rect2(pos, new Vector2(PanelWidth, height)), new Color(0, 0, 0, 0.6f));

    var white = Colors.White;
    var gray = new Color(0.75f, 0.75f, 0.75f);
    _canvas.DrawString(font, pos + new Vector2(8, LineHeight), title, HorizontalAlignment.Left, PanelWidth - 16, FontSize, white);
    _canvas.DrawString(font, pos + new Vector2(8, 2 * LineHeight + 2), subtitle, HorizontalAlignment.Left, PanelWidth - 16, FontSize - 1, gray);

    double total = slices.Sum(s => s.Value);
    var center = new Vector2(pos.X + PanelWidth / 2, pieTop + PieRadius * PieSquash);
    if (total > 0) DrawPie(center, slices.Select(s => (SliceColor(s.Name), s.Value / total)).ToList());

    float y = legendTop + LineHeight;
    float c1 = pos.X + PanelWidth - 90;
    float c2 = pos.X + PanelWidth - 8;
    _canvas.DrawString(font, new Vector2(c1 - 80, y), col1Header, HorizontalAlignment.Right, 80, FontSize - 1, gray);
    _canvas.DrawString(font, new Vector2(c2 - 80, y), col2Header, HorizontalAlignment.Right, 80, FontSize - 1, gray);
    for (int i = 0; i < slices.Count; i++)
    {
      y += LineHeight;
      var s = slices[i];
      var color = SliceColor(s.Name);
      _canvas.DrawRect(new Rect2(pos.X + 8, y - 11, 10, 10), color);
      string label = numbered ? $"[{i + 1}] {s.Name}{(s.Drillable ? " +" : "")}" : s.Name;
      _canvas.DrawString(font, new Vector2(pos.X + 24, y), label, HorizontalAlignment.Left, c1 - 80 - pos.X - 24, FontSize, color.Lightened(0.3f));
      _canvas.DrawString(font, new Vector2(c1 - 80, y), s.Col1, HorizontalAlignment.Right, 80, FontSize, white);
      _canvas.DrawString(font, new Vector2(c2 - 80, y), s.Col2, HorizontalAlignment.Right, 80, FontSize, white);
    }
  }

  /// <summary>Tilted 3D pie: darker copies stacked downward form the side, then the top.</summary>
  private void DrawPie(Vector2 center, List<(Color color, double fraction)> slices)
  {
    for (int d = (int)PieThickness; d >= 1; d--)
    {
      DrawPieLayer(center + new Vector2(0, d), slices, 0.55f);
    }
    DrawPieLayer(center, slices, 1f);
  }

  private void DrawPieLayer(Vector2 center, List<(Color color, double fraction)> slices, float brightness)
  {
    double angle = -Math.PI / 2;
    foreach (var (color, fraction) in slices)
    {
      double sweep = fraction * Math.Tau;
      if (sweep < 0.002) continue;
      var c = new Color(color.R * brightness, color.G * brightness, color.B * brightness);
      // Split into convex pieces of at most 90 degrees.
      int pieces = (int)Math.Ceiling(sweep / (Math.PI / 2));
      for (int p = 0; p < pieces; p++)
      {
        double a0 = angle + sweep * p / pieces;
        double a1 = angle + sweep * (p + 1) / pieces;
        int segs = Math.Max(2, (int)((a1 - a0) / 0.05));
        var pts = new Vector2[segs + 2];
        pts[0] = center;
        for (int i = 0; i <= segs; i++)
        {
          double a = a0 + (a1 - a0) * i / segs;
          pts[i + 1] = center + new Vector2((float)Math.Cos(a) * PieRadius, (float)Math.Sin(a) * PieRadius * PieSquash);
        }
        _canvas.DrawColoredPolygon(pts, c);
      }
      angle += sweep;
    }
  }

  private void DrawLines(Font font, Vector2 pos, List<string> lines)
  {
    float y = pos.Y;
    foreach (var line in lines)
    {
      var w = font.GetStringSize(line, HorizontalAlignment.Left, -1, FontSize).X;
      _canvas.DrawRect(new Rect2(pos.X - 2, y, w + 6, LineHeight), new Color(0, 0, 0, 0.55f));
      _canvas.DrawString(font, new Vector2(pos.X + 1, y + LineHeight - 4), line, HorizontalAlignment.Left, -1, FontSize, Colors.White);
      y += LineHeight;
    }
  }

  private static readonly Dictionary<string, Color> Palette = new()
  {
    ["process (scripts)"] = new Color("4e79a7"),
    ["physics"] = new Color("f28e2b"),
    ["render (cpu)"] = new Color("e15759"),
    ["terrain (main thread)"] = new Color("59a14f"),
    ["other scripts + engine"] = new Color("76b7b2"),
    ["apply meshes"] = new Color("edc948"),
    ["collision shapes"] = new Color("b07aa1"),
    ["lod swaps"] = new Color("ff9da7"),
    ["octree diff"] = new Color("9c755f"),
    ["dig + raycast"] = new Color("f28e2b"),
    ["bookkeeping"] = new Color("bab0ac"),
    ["noise (density)"] = new Color("4e79a7"),
    ["dig edits"] = new Color("f28e2b"),
    ["surface check"] = new Color("bab0ac"),
    ["marching cubes + colors"] = new Color("59a14f"),
    ["transvoxel seams"] = new Color("e15759"),
    ["godot mesh arrays"] = new Color("edc948"),
    ["collision faces"] = new Color("b07aa1"),
    ["volume cache"] = new Color("9c755f"),
    ["dig morph offsets"] = new Color("76b7b2"),
  };

  /// <summary>Fixed colors for known sections; anything else gets a stable hash-based hue.</summary>
  private static Color SliceColor(string name)
  {
    if (name == "idle" || name.StartsWith("idle ")) return new Color(0.42f, 0.42f, 0.42f);
    if (Palette.TryGetValue(name, out var known)) return known;
    uint h = 2166136261;
    foreach (char ch in name) h = (h ^ ch) * 16777619;
    return Color.FromHsv((h % 360) / 360f, 0.6f, 0.95f);
  }

  // ------------------------------------------------------------------ text

  private List<string> InfoLines()
  {
    var lines = new List<string>();
    var f = _frame;
    double fps = Engine.GetFramesPerSecond();
    double frameMs = f?.Ms ?? 0;
    double Child(string name) => f?.Children.FirstOrDefault(c => c.Name == name)?.Ms ?? 0;
    lines.Add($"marching-cubes  {fps:F0} fps  frame {frameMs:F2} ms avg, {_worstRecentMs:F1} ms worst (2 s)  (process {Child("process (scripts)"):F2}, physics {Child("physics"):F2}, " +
              $"render cpu {Child("render (cpu)"):F2}, gpu {_gpuMs:F2})");

    var cam = GetViewport().GetCamera3D();
    if (cam != null)
    {
      var p = cam.GlobalPosition;
      var key = ChunkKey.Containing(Terrain != null ? Terrain.ToLocal(p) : p, 0);
      lines.Add($"XYZ {p.X:F1} / {p.Y:F1} / {p.Z:F1}   chunk {key.Coord}");
    }

    if (Terrain != null)
    {
      var t = Terrain;
      t.CountByLod(_lodCounts);
      var lods = string.Join(" ", _lodCounts.Select((n, l) => (n, l)).Where(x => x.n > 0).Select(x => $"L{x.l}:{x.n}"));
      lines.Add($"Chunks: {t.ActiveChunks} active [{lods}], {t.VisibleChunks} meshed+visible, {t.RetiringChunks} retiring, " +
                $"{t.PendingChunks} pending, {t.CollisionChunks} with collision");
      lines.Add($"Workers: {t.BusyWorkers}/{t.WorkerCount} busy, queue {t.QueueLength}, {_chunksPerSec:F0} chunks/s, " +
                $"per chunk: generate {t.AvgGenerateMs:F1} ms + mesh {t.AvgMeshMs:F1} ms, octree build {t.OctreeBuildMs:F1} ms");
      lines.Add($"Terrain main thread: {t.LastFrame.TotalMs:F2} ms this frame   meshes {t.VertexCount / 1e6:F2}M vertices, " +
                $"{t.CachedVolumes} cached volumes, {t.EditCount} digs, {VoxelProfiler.ChunksBuilt} chunks built " +
                $"({VoxelProfiler.ChunksEmpty} empty)" +
                (t.Uniform is { } u ? $", {u.Count} chunks known all air / rock ({u.Hits} builds skipped)" : ""));
      var ships = GetNodeOrNull<Ships.ShipBuilder>("../Ships");
      if (ships != null)
      {
        lines.Add($"Ships: {ships.SimulatingCount} simulating, {ships.PausedCount - ships.UnloadedCount} waiting for collision, " +
                  $"{ships.UnloadedCount} unloaded (outside the loaded chunks), " +
                  $"{t.RequestedCollisionChunks} terrain chunks with collision for ships");
        foreach (var s in ships.Ships)
          if (s.AI is { } ai)
            lines.Add($"AI ship ({ai.Job}): {ai.State}, {ai.DistanceToTarget:0} from {(ai.Job == Ships.AIJob.Dig ? "rock" : "target")}, " +
                      (ai.LineClear ? "clear line" : $"path {ai.Path.Count} waypoints") +
                      $", {ai.PlanCount} plans (last {ai.LastPlanMs:0.0} ms){(ai.WhiskerBlocked ? ", rock ahead" : "")}");
      }
      lines.Add($"Edit batches: {t.BatchInFlight} building, {t.BatchQueued} queued, last {t.LastBatchSize} chunks in " +
                $"{t.LastBatchMs:F0} ms ({t.BatchesCommitted} committed, {t.EditsMerged} digs merged), {t.MorphingChunks} morphing ({t.LastMorphSeconds * 1000:F0} ms), " +
                $"collision queue {t.CollisionQueueLength}");
    }

    lines.Add($"Render: {Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame):F0} draw calls, " +
              $"{Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame):F0} objects, " +
              $"{Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame) / 1e6:F2}M primitives, " +
              $"VRAM {Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576:F0} MB, " +
              $"static mem {Performance.GetMonitor(Performance.Monitor.MemoryStatic) / 1048576:F0} MB, " +
              $"managed {GC.GetTotalMemory(false) / 1048576} MB");
    lines.Add("F3 info   Ctrl+F3 profiler (1-9 drill in, 0 back, P log snapshot)");
    return lines;
  }

  // ------------------------------------------------------------------ logging

  private void LogSnapshot()
  {
    var sb = new StringBuilder();
    sb.AppendLine($"=== profiler snapshot {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
    foreach (var line in InfoLines().SkipLast(1)) sb.AppendLine(line);
    sb.AppendLine();
    sb.AppendLine("main thread (smoothed ms, % of parent, % of frame):");
    if (_frame != null) AppendTree(sb, _frame, _frame.Ms, _frame.Ms, 0);
    sb.AppendLine();
    int workers = Terrain?.WorkerCount ?? 0;
    double capacity = Math.Max(1, workers) * 1000.0;
    sb.AppendLine($"terrain workers ({workers} threads, {_chunksPerSec:F1} chunks/s; thread-ms per second, % of capacity, ms per chunk):");
    for (int i = 0; i < _sections; i++)
    {
      double v = _workerMsPerSec[i];
      sb.AppendLine($"  {VoxelProfiler.Names[i],-26} {v,8:F1}  {Pct(v, capacity),7}  {(_chunksPerSec > 0.5 ? v / _chunksPerSec : 0),7:F2}");
    }
    double idle = Math.Max(0, capacity - _workerMsPerSec.Sum());
    sb.AppendLine($"  {"idle",-26} {idle,8:F1}  {Pct(idle, capacity),7}");

    string text = sb.ToString();
    GD.Print(text);
    DirAccess.MakeDirRecursiveAbsolute("user://profiler");
    string path = $"user://profiler/profile_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
    using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
    file?.StoreString(text);
    GD.Print($"profiler snapshot saved to {ProjectSettings.GlobalizePath(path)}");
  }

  private static void AppendTree(StringBuilder sb, Section s, double parentMs, double frameMs, int depth)
  {
    sb.AppendLine($"{new string(' ', 2 + depth * 2)}{s.Name,-30} {s.Ms,8:F3} ms  {Pct(s.Ms, parentMs),7}  {Pct(s.Ms, frameMs),7}");
    foreach (var c in Sorted(s.Children)) AppendTree(sb, c, s.Ms, frameMs, depth + 1);
  }
}
