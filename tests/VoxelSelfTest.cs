#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using Voxel;

/// <summary>
/// Headless checks for the voxel pipeline. Run with:
///   Godot_v4.7.1-stable_mono_win64_console.exe --headless --path . res://tests/voxel_self_test.tscn
/// Exits with code 0 when every check passes, 1 otherwise.
/// </summary>
public partial class VoxelSelfTest : Node
{
  private TerrainGenerator _gen = null!;
  private int _failures;
  private readonly List<SphereEdit> _noEdits = [];

  public override async void _Ready()
  {
    var scene = GD.Load<PackedScene>("res://scenes/node_3d.tscn").Instantiate();
    var cm = scene.GetNode<ChunkManager>("ChunkManager");

    // Rendering check: `-- --screenshot=<dir>` (needs a window, not --headless).
    var shotArg = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--screenshot="));
    if (shotArg != null)
    {
      await Screenshots(scene, cm, shotArg["--screenshot=".Length..]);
      GetTree().Quit();
      return;
    }
    _gen = new TerrainGenerator(cm.Noise, cm.ColorNoise, new EditStore());

    GD.Print($"winding flips: regular={MarchingCubesMesher.FlipWinding} transition={TransitionMesher.FlipWinding}");
    Timing();
    RegularWatertight();
    Orientation();
    SeamsMatchFinerNeighbors();
    ShrunkBoundaryClosed();
    NoStretchedTriangles();
    OctreeBalanced();

    await CollisionCost();

    // Live run of the real scene: initial load, digs, streaming.
    AddChild(scene);
    await LiveRun(scene, cm);

    GD.Print(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
    GetTree().Quit(_failures == 0 ? 0 : 1);
  }

  private void Check(bool ok, string what)
  {
    GD.Print($"[{(ok ? "PASS" : "FAIL")}] {what}");
    if (!ok) _failures++;
  }

  private (VoxelVolume vol, MeshBuffers buf) Build(ChunkKey key)
  {
    var vol = new VoxelVolume();
    vol.Generate(_gen, key);
    var buf = new MeshBuffers();
    if (vol.HasSurface())
    {
      MarchingCubesMesher.Mesh(vol, _gen, buf, new int[MarchingCubesMesher.EdgeCacheSize]);
      if (key.Lod > 0) TransitionMesher.Mesh(vol, _gen, _noEdits, buf, new TransitionMesher.Scratch());
    }
    return (vol, buf);
  }

  private IEnumerable<ChunkKey> SurfaceChunks(int lod, int count)
  {
    int found = 0;
    for (int r = 0; r < 8 && found < count; r++)
      for (int z = -r; z <= r && found < count; z++)
        for (int y = -r; y <= r && found < count; y++)
          for (int x = -r; x <= r && found < count; x++)
          {
            if (Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z))) != r) continue;
            var key = new ChunkKey(lod, new Vector3I(x, y, z));
            var vol = new VoxelVolume();
            vol.Generate(_gen, key);
            if (!vol.HasSurface()) continue;
            found++;
            yield return key;
          }
  }

  // ------------------------------------------------------------------ timing

  private void Timing()
  {
    var ctx = new WorkerContext();
    var cache = new VolumeCache(0);
    var builder = new ChunkBuilder(_gen, cache);
    double gen = 0, mesh = 0;
    long verts = 0;
    int n = 0, empty = 0;
    for (int z = -3; z < 3; z++)
      for (int y = -3; y < 3; y++)
        for (int x = -3; x < 3; x++)
        {
          var key = new ChunkKey(0, new Vector3I(x, y, z));
          var r = builder.Build(new MeshJob(key, 0, true, false), new Chunk(key), ctx);
          gen += r.GenerateMs;
          mesh += r.MeshMs;
          verts += r.VertexCount;
          n++;
          if (r.Arrays == null) empty++;
        }
    GD.Print($"timing (single thread, the old 6x6x6 grid): {n} chunks, {empty} empty, " +
             $"generate {gen / n:F2} ms/chunk, mesh+arrays+collision {mesh / n:F2} ms/chunk, " +
             $"total {(gen + mesh):F0} ms, {verts / Math.Max(1, n - empty)} vertices/meshed chunk");
  }

  // ------------------------------------------------------------------ regular mesh

  private static long Q(float v) => (long)Math.Round(v * 1000.0);
  private static (long, long, long) Q(Vector3 p) => (Q(p.X), Q(p.Y), Q(p.Z));

  private static bool Degenerate(Vector3 a, Vector3 b, Vector3 c) =>
    Q(a) == Q(b) || Q(b) == Q(c) || Q(a) == Q(c) || (b - a).Cross(c - a).LengthSquared() < 1e-10f;

  private static bool OnAnyBoundaryPlane(Vector3 a, Vector3 b, float size)
  {
    for (int ax = 0; ax < 3; ax++)
    {
      if (Math.Abs(a[ax]) < 1e-4f && Math.Abs(b[ax]) < 1e-4f) return true;
      if (Math.Abs(a[ax] - size) < 1e-4f && Math.Abs(b[ax] - size) < 1e-4f) return true;
    }
    return false;
  }

  /// <summary>
  /// Counts edges not shared by exactly two triangles. Vertices are identified by id(i) (exact, no tolerance);
  /// edges whose endpoints both lie on one of the chunk's boundary planes in raw(i) are the chunk's open border.
  /// </summary>
  private static int OpenEdges(List<(int a, int b, int c)> tris, Func<int, (int, int, int)> id, Func<int, Vector3> raw, float size)
  {
    var count = new Dictionary<((int, int, int), (int, int, int)), int>();
    var rawOf = new Dictionary<(int, int, int), Vector3>();
    foreach (var (a, b, c) in tris)
    {
      var ia = id(a); var ib = id(b); var ic = id(c);
      if (ia == ib || ib == ic || ia == ic) continue; // collapsed triangle
      foreach (var (p, q, kp, kq) in new[] { (a, b, ia, ib), (b, c, ib, ic), (c, a, ic, ia) })
      {
        rawOf[kp] = raw(p);
        rawOf[kq] = raw(q);
        var e = kp.CompareTo(kq) < 0 ? (kp, kq) : (kq, kp);
        count[e] = count.GetValueOrDefault(e) + 1;
      }
    }
    int open = 0;
    foreach (var (e, n) in count)
    {
      if (n == 2) continue;
      if (OnAnyBoundaryPlane(rawOf[e.Item1], rawOf[e.Item2], size)) continue;
      open++;
    }
    return open;
  }

  private static (int, int, int) Bits(Vector3 v) =>
    (BitConverter.SingleToInt32Bits(v.X), BitConverter.SingleToInt32Bits(v.Y), BitConverter.SingleToInt32Bits(v.Z));

  private static List<(int, int, int)> Triangles(MeshBuffers buf, int from, int to)
  {
    var tris = new List<(int, int, int)>();
    for (int i = from; i < to; i += 3) tris.Add((buf.Indices[i], buf.Indices[i + 1], buf.Indices[i + 2]));
    return tris;
  }

  private void RegularWatertight()
  {
    int open = 0, chunks = 0;
    foreach (var key in SurfaceChunks(0, 12))
    {
      var (_, buf) = Build(key);
      open += OpenEdges(Triangles(buf, 0, buf.RegularIndexCount), i => (i, 0, 0), i => buf.Positions[i], key.Size);
      chunks++;
    }
    Check(chunks > 0 && open == 0, $"regular LOD0 meshes are closed inside the chunk ({chunks} chunks, {open} open edges)");
  }

  private void Orientation()
  {
    int good = 0, bad = 0, tGood = 0, tBad = 0;
    foreach (var key in SurfaceChunks(1, 6))
    {
      var (_, buf) = Build(key);
      for (int i = 0; i < buf.IndexCount; i += 3)
      {
        int a = buf.Indices[i], b = buf.Indices[i + 1], c = buf.Indices[i + 2];
        // Transition strips are flat until the shader displaces them, so measure them displaced.
        int face = FaceOf((int)buf.Custom[a * 4 + 3]);
        int mask = face >= 0 ? 1 << face : 0;
        var pa = ShaderVertex(buf, a, mask);
        var cross = (ShaderVertex(buf, b, mask) - pa).Cross(ShaderVertex(buf, c, mask) - pa);
        if (cross.LengthSquared() < 1e-10f) continue;
        var n = buf.Normals[a] + buf.Normals[b] + buf.Normals[c];
        // Godot front faces are clockwise: cross points into the solid, normals point to the air.
        bool ok = cross.Dot(n) < 0;
        if (i < buf.RegularIndexCount) { if (ok) good++; else bad++; }
        else { if (ok) tGood++; else tBad++; }
      }
    }
    Check(good > 0 && bad < good / 100, $"regular triangles face the air ({good} ok, {bad} reversed)");
    Check(tGood > 0 && tBad < tGood / 50, $"transition triangles face the air ({tGood} ok, {tBad} reversed)");
  }

  // ------------------------------------------------------------------ seams

  private static int FaceOf(int flags) => ((flags >> 6) & 7) - 1;

  /// <summary>Every LOD1 transition face matches the four LOD0 chunks across it exactly on the shared plane.</summary>
  private void SeamsMatchFinerNeighbors()
  {
    int faces = 0, mismatched = 0, compared = 0;
    foreach (var key in SurfaceChunks(1, 6))
    {
      var (_, coarse) = Build(key);
      var origin = (Vector3)key.Origin;
      for (int f = 0; f < 6; f++)
      {
        int axis = f >> 1;
        float plane = (f & 1) == 0 ? origin[axis] : origin[axis] + key.Size;

        // Transition edges on the full-res side, in world space.
        int axisBits = 3 << (2 * axis);
        var coarseEdges = PlaneEdges(coarse, coarse.RegularIndexCount, coarse.IndexCount, origin, axis, plane,
          i => FaceOf((int)coarse.Custom[i * 4 + 3]) == f && ((int)coarse.Custom[i * 4 + 3] & axisBits) == 0);

        // The finer neighbors' edges on the same plane.
        var fineEdges = new HashSet<((long, long, long), (long, long, long))>();
        var across = key.Coord * 2;
        across[axis] += (f & 1) == 0 ? -1 : 2;
        for (int j = 0; j < 2; j++)
          for (int i = 0; i < 2; i++)
          {
            var c = across;
            c[(axis + 1) % 3] += i;
            c[(axis + 2) % 3] += j;
            var fk = new ChunkKey(0, c);
            var (_, fine) = Build(fk);
            var fo = (Vector3)fk.Origin;
            fineEdges.UnionWith(PlaneEdges(fine, 0, fine.RegularIndexCount, fo, axis, plane, _ => true));
          }

        if (coarseEdges.Count == 0 && fineEdges.Count == 0) continue;
        faces++;
        compared += fineEdges.Count;
        if (!SameWithinTolerance(coarseEdges, fineEdges))
        {
          mismatched++;
          if (mismatched == 1)
          {
            static string E(((long, long, long) a, (long, long, long) b) e) => $"[{e.a} - {e.b}]";
            GD.Print("    only transition: " + string.Join(" ", coarseEdges.Except(fineEdges).Take(4).Select(E)));
            GD.Print("    only finer:      " + string.Join(" ", fineEdges.Except(coarseEdges).Take(4).Select(E)));
          }
          GD.Print($"  seam mismatch {key} face {f}: transition {coarseEdges.Count} edges, finer {fineEdges.Count}, " +
                   $"common {coarseEdges.Intersect(fineEdges).Count()}");
        }
      }
    }
    Check(faces > 0 && mismatched == 0, $"transition faces match finer neighbors on the shared plane ({faces} faces, {compared} edges, {mismatched} mismatched)");
  }

  private static bool Near((long, long, long) a, (long, long, long) b) =>
    Math.Abs(a.Item1 - b.Item1) <= 2 && Math.Abs(a.Item2 - b.Item2) <= 2 && Math.Abs(a.Item3 - b.Item3) <= 2;

  /// <summary>Slivers under 0.01 units (a vertex sitting almost exactly on a sample) are below visibility.</summary>
  private static bool Tiny(((long, long, long) a, (long, long, long) b) e) =>
    Math.Abs(e.a.Item1 - e.b.Item1) <= 10 && Math.Abs(e.a.Item2 - e.b.Item2) <= 10 && Math.Abs(e.a.Item3 - e.b.Item3) <= 10;

  /// <summary>
  /// Edge sets equal up to 0.002 units per coordinate (both meshes compute the same vertex in different local
  /// frames, so a few land on opposite sides of a 0.001 rounding boundary). Sub-tolerance edges are ignored.
  /// </summary>
  private static bool SameWithinTolerance(HashSet<((long, long, long), (long, long, long))> a,
    HashSet<((long, long, long), (long, long, long))> b)
  {
    var onlyA = a.Except(b).Where(e => !Tiny(e)).ToList();
    var onlyB = b.Except(a).Where(e => !Tiny(e)).ToList();
    foreach (var ea in onlyA)
    {
      int match = onlyB.FindIndex(eb => (Near(ea.Item1, eb.Item1) && Near(ea.Item2, eb.Item2)) ||
                                        (Near(ea.Item1, eb.Item2) && Near(ea.Item2, eb.Item1)));
      if (match < 0) return false;
      onlyB.RemoveAt(match);
    }
    return onlyB.Count == 0;
  }

  /// <summary>
  /// The mesh's crossing line on the plane: edges lying on the plane that are used by exactly one triangle.
  /// (Transition cells can put flat caps in the plane; their inner edges are used twice and aren't part of the seam.)
  /// </summary>
  private static HashSet<((long, long, long), (long, long, long))> PlaneEdges(MeshBuffers buf, int from, int to, Vector3 origin,
    int axis, float plane, Func<int, bool> include)
  {
    var uses = new Dictionary<((long, long, long), (long, long, long)), int>();
    for (int i = from; i < to; i += 3)
    {
      int a = buf.Indices[i], b = buf.Indices[i + 1], c = buf.Indices[i + 2];
      if (a == b || b == c || a == c) continue;
      foreach (var (ia, ib) in new[] { (a, b), (b, c), (c, a) })
      {
        if (!include(ia) || !include(ib)) continue;
        var p = buf.Positions[ia] + origin;
        var q = buf.Positions[ib] + origin;
        if (Math.Abs(p[axis] - plane) > 1e-3f || Math.Abs(q[axis] - plane) > 1e-3f) continue;
        var kp = Q(p);
        var kq = Q(q);
        if (kp == kq) continue;
        var e = kp.CompareTo(kq) < 0 ? (kp, kq) : (kq, kp);
        uses[e] = uses.GetValueOrDefault(e) + 1;
      }
    }
    var set = new HashSet<((long, long, long), (long, long, long))>();
    foreach (var (e, n) in uses) if (n % 2 == 1) set.Add(e);
    return set;
  }

  /// <summary>
  /// With one face active, run the shader's vertex logic on the CPU; the shrunk regular mesh plus the transition
  /// strip must have no holes inside the chunk.
  /// </summary>
  private void ShrunkBoundaryClosed()
  {
    int open = 0, cases = 0;
    foreach (var key in SurfaceChunks(1, 6))
    {
      var (_, buf) = Build(key);
      for (int f = 0; f < 6; f++)
      {
        int mask = 1 << f;
        var tris = new List<(int, int, int)>();
        for (int i = 0; i < buf.IndexCount; i += 3)
        {
          int a = buf.Indices[i];
          int face = FaceOf((int)buf.Custom[a * 4 + 3]);
          if (face >= 0 && face != f) continue; // collapsed by the shader
          tris.Add((a, buf.Indices[i + 1], buf.Indices[i + 2]));
        }
        open += OpenEdges(tris, i => Bits(ShaderVertex(buf, i, mask)), i => buf.Positions[i], key.Size);
        cases++;
      }
    }
    Check(cases > 0 && open == 0, $"shrunk boundary + transition strip is closed ({cases} chunk faces, {open} open edges)");
  }

  /// <summary>CPU copy of terrain.gdshader's vertex().</summary>
  private static Vector3 ShaderVertex(MeshBuffers buf, int i, int mask)
  {
    int flags = (int)(buf.Custom[i * 4 + 3] + 0.5f);
    int active = flags & mask & 63;
    var v = buf.Positions[i];
    if (active == 0) return v;
    var d = new Vector3(
      (active & 3) != 0 ? buf.Custom[i * 4] : 0,
      (active & 12) != 0 ? buf.Custom[i * 4 + 1] : 0,
      (active & 48) != 0 ? buf.Custom[i * 4 + 2] : 0);
    var n = buf.Normals[i];
    return v + d - n * n.Dot(d);
  }

  /// <summary>No visible triangle may be stretched across the chunk (would show as long slivers / shadow lines).</summary>
  private void NoStretchedTriangles()
  {
    double worst = 0;
    string where = "";
    foreach (var lod in new[] { 0, 1, 2 })
      foreach (var key in SurfaceChunks(lod, 4))
      {
        var (_, buf) = Build(key);
        for (int mask = 0; mask < 64; mask++)
        {
          if (lod == 0 && mask != 0) break;
          for (int i = 0; i < buf.IndexCount; i += 3)
          {
            int a = buf.Indices[i], b = buf.Indices[i + 1], c = buf.Indices[i + 2];
            int face = FaceOf((int)buf.Custom[a * 4 + 3]);
            if (face >= 0 && (mask & (1 << face)) == 0) continue; // collapsed
            var pa = ShaderVertex(buf, a, mask);
            var pb = ShaderVertex(buf, b, mask);
            var pc = ShaderVertex(buf, c, mask);
            double longest = Math.Max(pa.DistanceTo(pb), Math.Max(pb.DistanceTo(pc), pc.DistanceTo(pa))) / key.Step;
            if (longest > worst)
            {
              worst = longest;
              where = $"{key} mask {mask} tri {i / 3} {(face >= 0 ? $"transition face {face}" : "regular")}";
            }
          }
        }
      }
    // A marching-cubes triangle spans at most a cell diagonal (sqrt 3 ~ 1.73 cells); allow a little for the shrink.
    Check(worst < 2.0, $"no stretched triangles under any lod_mask (longest edge {worst:F2} cells at {where})");
  }

  // ------------------------------------------------------------------ octree

  private void OctreeBalanced()
  {
    var viewer = new Vector3(13, -7, 40);
    var r = ChunkOctree.Build(viewer, 4, 1.0f, 1);
    int bad = 0;
    foreach (var leaf in r.Leaves)
    {
      for (int f = 0; f < 6; f++)
      {
        int axis = f >> 1;
        var p = leaf.Bounds.GetCenter();
        p[axis] += ((f & 1) == 0 ? -1 : 1) * (leaf.Size / 2f + 0.5f);
        for (int l = 0; l <= 4; l++)
        {
          if (r.Leaves.Contains(ChunkKey.Containing(p, l)) && Math.Abs(l - leaf.Lod) > 1) bad++;
        }
      }
    }
    var byLod = r.Leaves.GroupBy(k => k.Lod).OrderBy(g => g.Key).Select(g => $"L{g.Key}:{g.Count()}");
    Check(bad == 0, $"octree is 2:1 balanced ({r.Leaves.Count} leaves [{string.Join(" ", byLod)}], built in {r.Milliseconds:F1} ms, {bad} violations)");
  }

  // ------------------------------------------------------------------ screenshots

  private async System.Threading.Tasks.Task Screenshots(Node scene, ChunkManager cm, string dir)
  {
    AddChild(scene);
    Input.MouseMode = Input.MouseModeEnum.Visible;
    var player = scene.GetNode<Node3D>("Player");
    var pivot = player.GetNode<Node3D>("CameraPivot");

    async System.Threading.Tasks.Task Shot(string name, Vector3 pos, float yaw, float pitch)
    {
      player.GlobalPosition = pos;
      player.Rotation = new Vector3(0, yaw, 0);
      pivot.Rotation = new Vector3(pitch, 0, 0);
      var sw = Stopwatch.StartNew();
      int idle = 0;
      while (sw.Elapsed.TotalSeconds < 60 && idle < 30)
      {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        bool busy = (int)Performance.GetCustomMonitor("voxel/queue") + (int)Performance.GetCustomMonitor("voxel/busy_workers")
                  + (int)Performance.GetCustomMonitor("voxel/pending") + (int)Performance.GetCustomMonitor("voxel/retiring_chunks") > 0;
        idle = busy ? 0 : idle + 1;
      }
      var img = GetViewport().GetTexture().GetImage();
      var path = $"{dir}/{name}.png";
      img.SavePng(path);
      GD.Print($"saved {path} after {sw.Elapsed.TotalSeconds:F1} s, fps {Engine.GetFramesPerSecond()}");
    }

    await Shot("ground", player.GlobalPosition, player.Rotation.Y, 0);

    // Debug overlay: F3 info + Ctrl+F3 profiler, captured while digging and flying.
    void Key(Godot.Key key, bool ctrl = false)
    {
      Input.ParseInputEvent(new InputEventKey { Keycode = key, Pressed = true, CtrlPressed = ctrl });
      Input.ParseInputEvent(new InputEventKey { Keycode = key, Pressed = false, CtrlPressed = ctrl });
    }
    Key(Godot.Key.F3);
    Key(Godot.Key.F3, ctrl: true);
    var cam = GetViewport().GetCamera3D();
    var startPos = player.GlobalPosition;
    for (int i = 0; i < 400; i++)
    {
      if (i % 40 == 0 && cm.RaycastTerrain(cam.GlobalPosition, cam.GlobalPosition - cam.GlobalBasis.Z * 1000, out var hit)) cm.Dig(hit, 20);
      player.GlobalPosition = startPos + new Vector3(i * 0.3f, 0, 0);
      await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
    GetViewport().GetTexture().GetImage().SavePng($"{dir}/overlay.png");
    GD.Print($"saved {dir}/overlay.png");
    Key(Godot.Key.P);
    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    Key(Godot.Key.F3);
    Key(Godot.Key.F3, ctrl: true);
    player.GlobalPosition = startPos;
    // High above, looking down: shows the LOD rings and their seams.
    await Shot("overview", new Vector3(0, 220, 0), 0, -1.5f);
  }

  // ------------------------------------------------------------------ collision cost

  private async System.Threading.Tasks.Task CollisionCost()
  {
    var key = SurfaceChunks(0, 1).First();
    var (_, buf) = Build(key);
    var faces = buf.ToCollisionFaces();
    GD.Print($"collision cost for {key} ({buf.RegularIndexCount / 3} triangles), engine {ProjectSettings.GetSetting("physics/3d/physics_engine")}:");
    for (int run = 0; run < 3; run++)
    {
      var sw = Stopwatch.StartNew();
      var shape = new ConcavePolygonShape3D();
      shape.Set(ConcavePolygonShape3D.PropertyName.Data, faces);
      double tData = sw.Elapsed.TotalMilliseconds;
      var body = new StaticBody3D();
      var cs = new CollisionShape3D { Shape = shape };
      body.AddChild(cs);
      double tShape = sw.Elapsed.TotalMilliseconds;
      AddChild(body);
      double tTree = sw.Elapsed.TotalMilliseconds;
      await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
      var sw2 = Stopwatch.StartNew();
      await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
      GD.Print($"  run {run}: set data {tData:F1} ms, assign {tShape - tData:F1} ms, add to tree {tTree - tShape:F1} ms, next physics frame {sw2.Elapsed.TotalMilliseconds:F1} ms");
      body.QueueFree();
    }
  }

  // ------------------------------------------------------------------ live

  private async System.Threading.Tasks.Task LiveRun(Node scene, ChunkManager cm)
  {
    var player = scene.GetNode<Node3D>("Player");
    long worstFrameUs = 0;
    double worstProcessMs = 0, worstItemMs = 0, worstSwapMs = 0;
    var frame = Stopwatch.StartNew();
    var pause0 = GC.GetTotalPauseDuration();

    async System.Threading.Tasks.Task<double> WaitIdle(string label, double timeoutS)
    {
      var sw = Stopwatch.StartNew();
      int idleFrames = 0;
      while (sw.Elapsed.TotalSeconds < timeoutS)
      {
        frame.Restart();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        worstFrameUs = Math.Max(worstFrameUs, frame.ElapsedTicks * 1_000_000 / Stopwatch.Frequency);
        int queue = (int)Performance.GetCustomMonitor("voxel/queue");
        int busy = (int)Performance.GetCustomMonitor("voxel/busy_workers");
        int retiring = (int)Performance.GetCustomMonitor("voxel/retiring_chunks");
        int pending = (int)Performance.GetCustomMonitor("voxel/pending");
        worstProcessMs = Math.Max(worstProcessMs, (double)Performance.GetCustomMonitor("voxel/process_ms"));
        worstItemMs = Math.Max(worstItemMs, (double)Performance.GetCustomMonitor("voxel/max_item_ms"));
        worstSwapMs = Math.Max(worstSwapMs, (double)Performance.GetCustomMonitor("voxel/swap_ms"));
        idleFrames = queue == 0 && busy == 0 && retiring == 0 && pending == 0 ? idleFrames + 1 : 0;
        if (idleFrames > 10) break;
      }
      GD.Print($"{label}: {sw.Elapsed.TotalSeconds:F2} s, active {Performance.GetCustomMonitor("voxel/active_chunks")}, " +
               $"visible {Performance.GetCustomMonitor("voxel/visible_chunks")}, vertices {Performance.GetCustomMonitor("voxel/vertices")}, " +
               $"avg generate {(double)Performance.GetCustomMonitor("voxel/avg_generate_ms"):F1} ms, avg mesh {(double)Performance.GetCustomMonitor("voxel/avg_mesh_ms"):F1} ms, " +
               $"octree {(double)Performance.GetCustomMonitor("voxel/octree_ms"):F1} ms");
      return sw.Elapsed.TotalSeconds;
    }

    double load = await WaitIdle("initial load", 120);
    Check(load < 120, "initial load finishes");

    worstFrameUs = 0;
    worstProcessMs = 0;
    var cam = GetViewport().GetCamera3D();
    var origin = cam.GlobalPosition;
    var dirs = new[] { new Vector3(0, -1, -1), new Vector3(1, -1, 0), new Vector3(-1, -0.3f, 0.5f), new Vector3(0.2f, -1, 0.1f), new Vector3(1, 0.2f, 1) };
    var before = new float[dirs.Length];
    int hits = 0;
    var raySw = Stopwatch.StartNew();
    for (int i = 0; i < dirs.Length; i++)
    {
      before[i] = float.PositiveInfinity;
      if (cm.RaycastTerrain(origin, origin + dirs[i].Normalized() * 1000, out var hit))
      {
        hits++;
        before[i] = origin.DistanceTo(hit);
        cm.Dig(hit, 20);
      }
    }
    GD.Print($"  {hits}/{dirs.Length} terrain raycasts hit, {raySw.Elapsed.TotalMilliseconds / dirs.Length:F2} ms per ray");
    Check(hits > 0, "terrain raycast hits terrain");
    double dig = await WaitIdle($"{hits} digs", 30);
    Check(dig < 30, "digs finish");

    int deeper = 0;
    for (int i = 0; i < dirs.Length; i++)
    {
      if (float.IsInfinity(before[i])) continue;
      bool again = cm.RaycastTerrain(origin, origin + dirs[i].Normalized() * 1000, out var hit2);
      if (!again || origin.DistanceTo(hit2) > before[i] + 1) deeper++;
    }
    Check(deeper == hits, $"digging pushes the terrain back along each ray ({deeper}/{hits})");
    GD.Print($"  worst frame while digging: {worstFrameUs / 1000.0:F1} ms, worst ChunkManager._Process {worstProcessMs:F1} ms (headless)");

    worstFrameUs = 0;
    worstProcessMs = 0;
    var start = player.GlobalPosition;
    for (int i = 1; i <= 60; i++)
    {
      player.GlobalPosition = start + new Vector3(i * 8, 0, i * 3);
      frame.Restart();
      await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
      worstFrameUs = Math.Max(worstFrameUs, frame.ElapsedTicks * 1_000_000 / Stopwatch.Frequency);
      worstProcessMs = Math.Max(worstProcessMs, (double)Performance.GetCustomMonitor("voxel/process_ms"));
      worstItemMs = Math.Max(worstItemMs, (double)Performance.GetCustomMonitor("voxel/max_item_ms"));
      worstSwapMs = Math.Max(worstSwapMs, (double)Performance.GetCustomMonitor("voxel/swap_ms"));
    }
    double stream = await WaitIdle("streaming after flying ~510 units", 120);
    Check(stream < 120, "streaming settles");
    GD.Print($"  worst frame while streaming: {worstFrameUs / 1000.0:F1} ms, worst ChunkManager._Process {worstProcessMs:F1} ms, " +
             $"last octree diff {(double)Performance.GetCustomMonitor("voxel/diff_ms"):F1} ms, worst single result {worstItemMs:F1} ms, worst swap {worstSwapMs:F1} ms (headless)");
    GD.Print($"  total GC pause during live run: {(GC.GetTotalPauseDuration() - pause0).TotalMilliseconds:F0} ms, gen2 collections {GC.CollectionCount(2)}");
  }
}
