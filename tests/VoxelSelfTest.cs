#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using Map;
using Ships;
using Voxel;

/// <summary>
/// Headless checks for the voxel pipeline. Run with:
///   Godot_v4.7.1-stable_mono_win64_console.exe --headless --path . res://tests/voxel_self_test.tscn
/// Exits with code 0 when every check passes, 1 otherwise.
/// </summary>
public partial class VoxelSelfTest : Node
{
  private TerrainGenerator _gen = null!;
  private ChunkManager _cm = null!;
  private int _failures;
  private readonly List<SphereEdit> _noEdits = [];

  public override async void _Ready()
  {
    var scene = GD.Load<PackedScene>("res://scenes/node_3d.tscn").Instantiate();
    var cm = scene.GetNode<ChunkManager>("ChunkManager");

    // The game as it starts: the cleared spawn bubble and the prefab ship (`-- --spawn-shot=<dir>`, needs a window).
    var spawnArg = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--spawn-shot="));
    if (spawnArg != null)
    {
      await SpawnShots(scene, cm, spawnArg["--spawn-shot=".Length..]);
      GD.Print(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
      GetTree().Quit(_failures == 0 ? 0 : 1);
      return;
    }
    // The AI follower in the real startup (clearing, prefab and follower): `-- --ai-follow` (headless is fine).
    // Several AI diggers at once, for performance: `-- --dig-stress=<count>` (headless is fine).
    var stressArg = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--dig-stress"));
    if (stressArg != null)
    {
      await DigStress(scene, cm, stressArg.Contains('=') ? int.Parse(stressArg[(stressArg.IndexOf('=') + 1)..]) : 4);
      GD.Print(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
      GetTree().Quit(_failures == 0 ? 0 : 1);
      return;
    }
    if (OS.GetCmdlineUserArgs().Contains("--ai-follow"))
    {
      await FollowRun(scene, cm);
      GD.Print(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
      GetTree().Quit(_failures == 0 ? 0 : 1);
      return;
    }
    // Everything else tests terrain right around the player: no spawn clearing, no prefab ship, no follower.
    cm.SpawnClearRadius = 0;
    scene.GetNode<ShipBuilder>("Ships").SpawnPrefab = false;
    scene.GetNode<ShipBuilder>("Ships").SpawnFollower = false;

    // Rendering check: `-- --screenshot=<dir>` (needs a window, not --headless).
    var shotArg = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--screenshot="));
    if (shotArg != null)
    {
      await Screenshots(scene, cm, shotArg["--screenshot=".Length..]);
      GetTree().Quit();
      return;
    }
    var benchArg = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--bench-dig"));
    if (benchArg != null)
    {
      await BenchDig(scene, cm, benchArg.Contains('=') ? float.Parse(benchArg[(benchArg.IndexOf('=') + 1)..], System.Globalization.CultureInfo.InvariantCulture) : 60f);
      GetTree().Quit();
      return;
    }
    _gen = new TerrainGenerator(cm.Noise, cm.ColorNoise, new EditStore());
    _cm = cm;

    GD.Print($"winding flips: regular={MarchingCubesMesher.FlipWinding} transition={TransitionMesher.FlipWinding}");
    Timing();
    RegularWatertight();
    Orientation();
    SeamsMatchFinerNeighbors();
    ShrunkBoundaryClosed();
    NoStretchedTriangles();
    OctreeBalanced();
    EditStrength();
    MorphOffsets();
    EditMerge();
    ShipGridAndMesh();
    ImpactMath();
    ShipParts();
    SpawnClearing();
    PrefabShip();
    MapChunks();
    ControlAccel();
    Pathfinder();
    ShipLibraryRoundTrip();
    UniformChunkRecords();
    LongRoute();
    MorphBigSteps();

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
          var r = builder.Build(new MeshJob(key, 0, true, false, int.MaxValue, -2), new Chunk(key), ctx);
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

  // ------------------------------------------------------------------ edits

  private void EditStrength()
  {
    var c = Vector3.Zero;
    var full = new SphereEdit(c, 10, 1f, 0);
    var weak = new SphereEdit(c, 10, 0.25f, 0);
    var p = new Vector3(4, 0, 0); // 40% of the radius out
    bool ok = Mathf.IsEqualApprox(full.Apply(p, 1f), 0.4f)     // old carve: density *= dist / radius
           && Mathf.IsEqualApprox(weak.Apply(p, 1f), 0.85f)    // 1 - 0.25 * (1 - 0.4)
           && Mathf.IsEqualApprox(weak.Apply(new Vector3(11, 0, 0), 1f), 1f)
           && Mathf.IsEqualApprox(weak.Apply(c, 1f), 0.75f);
    Check(ok, "dig edit strength: 1 = old full carve, partial strengths erode proportionally");

    // Build is the mirror image: it pulls density toward solid with the same falloff.
    var build = new SphereEdit(c, 10, 0.25f, 0, true);
    bool mirror = Mathf.IsEqualApprox(build.Apply(p, 0f), 1f - weak.Apply(p, 1f))   // 0.15 at 40% out
               && Mathf.IsEqualApprox(build.Apply(c, 0.2f), 1f - 0.8f * 0.75f)
               && Mathf.IsEqualApprox(build.Apply(new Vector3(11, 0, 0), 0.3f), 0.3f);
    var store = new EditStore();
    store.Add(c, 10, 0.25f);
    bool kinds = store.TryMergeLast(c, 10, 0.25f, -1, 5, out _, build: true) == -1; // never merge a build into a dig
    Check(mirror && kinds, "build edits mirror digs (fill toward solid, same falloff) and never merge with digs");
  }

  /// <summary>Merging folds a pending dig into the newest edit, but never one a build has already been given.</summary>
  private void EditMerge()
  {
    var store = new EditStore();
    var c = new Vector3(100, 5, -40);
    store.Add(c, 20, 0.2f);
    int seq = store.TryMergeLast(c + new Vector3(2, 0, 0), 20, 0.5f, -1, 5, out var m);
    var found = new List<SphereEdit>();
    store.Query(new Aabb(m.Center - Vector3.One, Vector3.One * 2), -1, found);
    bool merged = seq == 0 && store.Count == 1 && Mathf.IsEqualApprox(m.Strength, 1f - 0.8f * 0.5f)
               && m.Center.DistanceTo(c + new Vector3(2f * 0.5f / 0.7f, 0, 0)) < 1e-4f && found.Count == 1 && found[0] == m;
    // Exact at the centre: two digs there multiply density by (1-a)(1-b), the merged one by 1 - S.
    float twoDigs = new SphereEdit(c, 20, 0.5f, 1).Apply(c, new SphereEdit(c, 20, 0.2f, 0).Apply(c, 1f));
    float oneDig = new SphereEdit(c, 20, 1f - 0.8f * 0.5f, 0).Apply(c, 1f);
    bool refused = store.TryMergeLast(c, 20, 0.5f, 0, 5, out _) == -1      // already handed to a build
                && store.TryMergeLast(c, 25, 0.5f, -1, 5, out _) == -1     // different radius
                && store.TryMergeLast(c + Vector3.One * 10, 20, 0.5f, -1, 5, out _) == -1; // too far
    Check(merged && refused && Mathf.IsEqualApprox(twoDigs, oneDig),
      "pending digs merge into one edit (exact at the centre, re-hashed), never once a build has them");
  }

  /// <summary>
  /// How far each animation's start lands from the surface that was on screen, for a big brush and several brush
  /// ticks per commit (slow commits fold more ticks into one step). Measured in world units along the gradient.
  /// </summary>
  private void MorphBigSteps()
  {
    var a = SurfaceChunks(0, 1).First();
    var (_, baseBuf) = Build(a);
    var center = (Vector3)a.Origin + baseBuf.Positions[baseBuf.VertexCount / 2];
    float strength = 1f - Mathf.Exp(-1.5f / 15f);
    var worst = 0f;
    foreach (var (ticks, build) in new[] { (1, false), (3, false), (6, false), (3, true) })
    {
      var store = new EditStore();
      var gen = new TerrainGenerator(_cm.Noise, _cm.ColorNoise, store);
      for (int i = 0; i < ticks; i++) store.Add(center, 60f, strength, build);
      var r = new ChunkBuilder(gen, new VolumeCache(0)).Build(new MeshJob(a, 0, false, false, ticks - 1, -1), new Chunk(a), new WorkerContext());
      if (r.Arrays == null || !r.HasMorph) continue;
      var pos = r.Arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
      var morph = r.Arrays[(int)Mesh.ArrayType.Custom1].AsFloat32Array();
      var errs = new List<float>();
      float moved = 0;
      for (int i = 0; i < pos.Length; i++)
      {
        var x = new Vector3(morph[i * 3], morph[i * 3 + 1], morph[i * 3 + 2]);
        if (x.LengthSquared() < 1e-8f) continue;
        var q = (Vector3)a.Origin + pos[i] + x;
        float d = gen.Density(q, _noEdits);
        const float h = 0.25f;
        var grad = new Vector3(gen.Density(q + Vector3.Right * h, _noEdits) - gen.Density(q - Vector3.Right * h, _noEdits),
                               gen.Density(q + Vector3.Up * h, _noEdits) - gen.Density(q - Vector3.Up * h, _noEdits),
                               gen.Density(q + Vector3.Back * h, _noEdits) - gen.Density(q - Vector3.Back * h, _noEdits)) / (2 * h);
        if (grad.Length() < 1e-4f) continue;
        errs.Add(Math.Abs(d - VoxelConst.Iso) / grad.Length());
        moved += x.Length();
      }
      if (errs.Count == 0) continue;
      errs.Sort();
      float mean = errs.Average(), p95 = errs[(int)(errs.Count * 0.95)];
      GD.Print($"morph start error, r=60, {ticks} {(build ? "build" : "dig")} tick(s) per commit: {errs.Count} moved vertices, mean move {moved / errs.Count:F2}, " +
               $"start off the shown surface by mean {mean:F3} / p95 {p95:F3} units");
      worst = Math.Max(worst, p95 / Math.Max(1e-3f, moved / errs.Count));
    }
    Check(worst < 0.25f, $"big-brush morphs start on the shown surface (worst p95 error {worst * 100:F0}% of the mean move)");
  }

  /// <summary>Morph offsets from (FromSeq, TargetSeq] edits land new vertices back on the old surface, identically on both sides of a chunk face.</summary>
  private void MorphOffsets()
  {
    foreach (var a in SurfaceChunks(0, 16))
    {
      var b = new ChunkKey(0, a.Coord + Vector3I.Right);
      var (_, baseBuf) = Build(a);
      int pick = -1;
      for (int i = 0; i < baseBuf.VertexCount && pick < 0; i++)
      {
        if (Math.Abs(baseBuf.Positions[i].X - VoxelConst.Cells) < 1e-4f) pick = i;
      }
      if (pick < 0) continue;

      var store = new EditStore();
      var gen = new TerrainGenerator(_cm.Noise, _cm.ColorNoise, store);
      var center = (Vector3)a.Origin + baseBuf.Positions[pick];
      float strength = 1f - Mathf.Exp(-1.5f / 15f); // one Player brush tick
      store.Add(center, 12f, strength);
      var builder = new ChunkBuilder(gen, new VolumeCache(0));
      var ctx = new WorkerContext();

      var face = new Dictionary<(long, long, long), Vector3>();
      var errMorph = new List<float>();
      var errNone = new List<float>();
      int faceShared = 0, faceMismatch = 0;
      foreach (var key in new[] { a, b })
      {
        var r = builder.Build(new MeshJob(key, 0, false, false, 0, -1), new Chunk(key), ctx);
        if (r.Arrays == null || !r.HasMorph) continue;
        var pos = r.Arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var morph = r.Arrays[(int)Mesh.ArrayType.Custom1].AsFloat32Array();
        var origin = (Vector3)key.Origin;
        for (int i = 0; i < pos.Length; i++)
        {
          var x = new Vector3(morph[i * 3], morph[i * 3 + 1], morph[i * 3 + 2]);
          var world = origin + pos[i];
          if (x.LengthSquared() > 1e-8f)
          {
            errNone.Add(Math.Abs(gen.Density(world, _noEdits) - VoxelConst.Iso));
            errMorph.Add(Math.Abs(gen.Density(world + x, _noEdits) - VoxelConst.Iso));
          }
          float plane = key == a ? VoxelConst.Cells : 0;
          if (Math.Abs(pos[i].X - plane) > 1e-4f) continue;
          var id = Q(world);
          if (key == a) face[id] = x;
          else if (face.TryGetValue(id, out var xa))
          {
            faceShared++;
            if ((xa - x).Length() > 1e-5f) faceMismatch++;
          }
        }
      }
      if (errMorph.Count < 20) continue;

      errMorph.Sort();
      errNone.Sort();
      float meanM = errMorph.Average(), meanN = errNone.Average();
      float p95M = errMorph[(int)(errMorph.Count * 0.95)], p95N = errNone[(int)(errNone.Count * 0.95)];
      GD.Print($"morph offsets ({a} + {b}, {errMorph.Count} moved vertices): old-surface density error " +
               $"mean {meanN:F4} -> {meanM:F4}, p95 {p95N:F4} -> {p95M:F4}; {faceShared} shared face vertices");
      Check(meanM < 0.25f * meanN && p95M < 0.35f * p95N, "morph offsets put new vertices back on the previously shown surface");
      Check(faceShared > 0 && faceMismatch == 0, $"morph offsets agree across a chunk face ({faceMismatch}/{faceShared} differ)");
      return;
    }
    Check(false, "morph offsets: found a chunk pair to test");
  }

  // ------------------------------------------------------------------ ships

  /// <summary>Ship blocks: connected groups (what splits a ship) and the faces the mesh shows.</summary>
  private void ShipGridAndMesh()
  {
    ShipGrid Grid(params Vector3I[] cells)
    {
      var g = new ShipGrid();
      foreach (var c in cells) g.Add(c);
      return g;
    }

    var line = Grid(new(0, 0, 0), new(1, 0, 0), new(2, 0, 0));
    line.Remove(new Vector3I(1, 0, 0));
    var ell = Grid(new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(1, 2, 0));
    var square = Grid(new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0));
    square.Remove(new Vector3I(1, 1, 0));
    bool groups = line.Components().Count == 2 && ell.Components().Count == 1 && square.Components().Count == 1;
    Check(groups, "ship blocks split into connected groups (line cut in the middle = 2, L shape = 1, square minus a corner = 1)");

    var cube = new ShipGrid();
    for (int z = 0; z < 2; z++)
      for (int y = 0; y < 2; y++)
        for (int x = 0; x < 2; x++)
          cube.Add(new Vector3I(x, y, z));
    int one = ShipMesh.ExposedFaces(Grid(Vector3I.Zero));
    int two = ShipMesh.ExposedFaces(Grid(Vector3I.Zero, Vector3I.Right));
    int eight = ShipMesh.ExposedFaces(cube);
    var mesh = ShipMesh.Build(cube);
    bool faces = one == 6 && two == 10 && eight == 24 && mesh != null && mesh.SurfaceGetArrayLen(0) == 24 * 4;
    Check(faces, $"ship mesh shows only uncovered faces (1 block {one}, 2 blocks {two}, 2x2x2 cube {eight})");
  }

  /// <summary>Map terrain: which coarse chunks it covers, and that one builds to a mesh inside its own bounds.</summary>
  private void MapChunks()
  {
    var centre = new Vector3(13, -40, 77);
    const float r = 512f;
    var keys = MapTerrain.Keys(centre, r);
    bool inRange = keys.All(k => k.Lod == MapTerrain.Lod && VoxelMath.DistanceToAabb(centre, k.Bounds) <= r);
    var set = keys.ToHashSet();
    var rng = new RandomNumberGenerator { Seed = 11 };
    bool covered = true;
    for (int i = 0; i < 2000; i++)
    {
      var p = centre + new Vector3(rng.RandfRange(-1, 1), rng.RandfRange(-1, 1), rng.RandfRange(-1, 1)).Normalized() * rng.RandfRange(0, r);
      covered &= set.Contains(ChunkKey.Containing(p, MapTerrain.Lod));
    }
    Check(inRange && covered, $"the map covers its radius with {keys.Count} LOD{MapTerrain.Lod} chunks and nothing beyond");

    var ctx = new MapTerrain.Context();
    int built = 0;
    bool inside = true;
    var sw = Stopwatch.StartNew();
    foreach (var key in keys)
    {
      var arrays = MapTerrain.BuildChunk(_gen, key, ctx);
      if (arrays == null) continue;
      built++;
      foreach (var v in arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array())
        inside &= v.X >= -key.Step && v.Y >= -key.Step && v.Z >= -key.Step
               && v.X <= key.Size + key.Step && v.Y <= key.Size + key.Step && v.Z <= key.Size + key.Step;
      if (built >= 6) break;
    }
    GD.Print($"  map chunk builds: {built} meshes in {sw.Elapsed.TotalMilliseconds:F0} ms (single thread)");
    Check(built > 0 && inside, "map chunks build into low-poly meshes inside their own bounds");
  }

  /// <summary>Generator with a spawn clearing: empty inside the radius, untouched rock well outside it.</summary>
  private void SpawnClearing()
  {
    var centre = new Vector3(10, -5, 3);
    var cleared = new TerrainGenerator(_cm.Noise, _cm.ColorNoise, new EditStore(), centre, 50f, 8f);
    var rng = new RandomNumberGenerator { Seed = 7 };
    bool insideEmpty = true, outsideSame = true, wallIso = true;
    for (int i = 0; i < 400; i++)
    {
      var dir = new Vector3(rng.RandfRange(-1, 1), rng.RandfRange(-1, 1), rng.RandfRange(-1, 1)).Normalized();
      var pIn = centre + dir * rng.RandfRange(0, 45);
      var pOut = centre + dir * rng.RandfRange(55, 200);
      insideEmpty &= cleared.RawDensity(pIn.X, pIn.Y, pIn.Z) < VoxelConst.Iso;
      outsideSame &= cleared.RawDensity(pOut.X, pOut.Y, pOut.Z) == _gen.RawDensity(pOut.X, pOut.Y, pOut.Z);
      var pWall = centre + dir * 50f;
      wallIso &= cleared.RawDensity(pWall.X, pWall.Y, pWall.Z) <= VoxelConst.Iso + 1e-4f;
    }
    Check(insideEmpty && outsideSame && wallIso, "spawn clearing: empty inside the radius, rock unchanged beyond its edge");
  }

  /// <summary>The prefab ship: one connected piece, many thrusters (every direction covered) and lasers, balanced.</summary>
  private void PrefabShip()
  {
    var grid = new ShipGrid();
    foreach (var (c, b) in ShipPrefabs.Miner()) grid.Add(c, b);
    var thrusters = new List<(Vector3I Cell, Vector3I Facing)>();
    int lasers = 0;
    foreach (var (c, b) in grid.Blocks)
    {
      if (b.Type == BlockType.Thruster) thrusters.Add((c, b.Facing));
      if (b.Type == BlockType.Laser) lasers++;
    }
    var com = grid.Centroid();
    var t = new float[thrusters.Count];
    bool allDirs = true;
    float worstTilt = 0;
    var dirs = new[] { Vector3.Forward, Vector3.Back, Vector3.Left, Vector3.Right, Vector3.Up, Vector3.Down };
    var lines = new List<string>();
    foreach (var d in dirs)
    {
      ThrustAllocator.Throttles(thrusters, d, t);
      float push = ThrustAllocator.PushAlong(thrusters, t, d, 1f);
      var torque = Vector3.Zero;
      for (int i = 0; i < thrusters.Count; i++)
        torque += ((Vector3)thrusters[i].Cell + Vector3.One * 0.5f - com).Cross(-(Vector3)thrusters[i].Facing * t[i]);
      allDirs &= push >= 2f;
      // Torque per unit of push: the lever arm the net force acts through (0 = through the centre of mass).
      if (push > 0) worstTilt = Mathf.Max(worstTilt, torque.Length() / push);
      lines.Add($"{d}: {push:F0}");
    }
    GD.Print($"  prefab: {grid.Count} blocks, {thrusters.Count} thrusters, {lasers} lasers, push per direction {string.Join(", ", lines)}, worst lever arm {worstTilt:F2}");
    Check(grid.Components().Count == 1, "the prefab ship is one connected piece");
    Check(thrusters.Count >= 20 && lasers >= 4 && allDirs, "the prefab ship has many thrusters (at least 2 pushing each way) and several lasers");
    Check(worstTilt < 0.6f, "the prefab's thrust in every direction passes close to its centre of mass");

    // Beam / flame cylinders follow their line even when tilted (stretch along the mesh's own axis).
    var dir = new Vector3(1, 2, -3).Normalized();
    var basis = ShipPilot.Stretched(dir, 10f);
    bool along = (basis * Vector3.Up).IsEqualApprox(dir * 10f);
    bool round = Mathf.IsEqualApprox((basis * Vector3.Right).Length(), 1f) && Mathf.IsEqualApprox((basis * Vector3.Back).Length(), 1f)
              && Mathf.Abs((basis * Vector3.Right).Dot(dir)) < 1e-4f && Mathf.Abs((basis * Vector3.Back).Dot(dir)) < 1e-4f;
    Check(along && round, "a tilted laser beam is drawn along the ray it digs with (not skewed)");
  }

  /// <summary>
  /// Known all-air / all-rock chunks: digs keep empty space empty, builds don't; any edit unsettles rock. The
  /// pathfinder trusts known-empty chunks without sampling (shown by marking a rocky chunk as empty).
  /// </summary>
  private void UniformChunkRecords()
  {
    var edits = new EditStore();
    var u = new UniformChunks(edits);
    var scratch = new List<SphereEdit>();
    var air = new ChunkKey(0, new Vector3I(0, 0, 0));
    var rock = new ChunkKey(0, new Vector3I(5, 0, 0));
    u.Remember(air, true, -1);
    u.Remember(rock, false, -1);
    bool both = u.StillUniform(air, int.MaxValue, scratch, out bool a1) && a1 && u.StillUniform(rock, int.MaxValue, scratch, out bool a2) && !a2;
    edits.Add(new Vector3(16, 16, 16), 4f);                 // dig in the empty chunk
    edits.Add(new Vector3(5 * 32 + 16, 16, 16), 4f);        // dig in the rock chunk
    bool digKeepsAir = u.StillUniform(air, int.MaxValue, scratch, out _);
    bool digDropsRock = !u.StillUniform(rock, int.MaxValue, scratch, out _);
    bool knownAir = u.KnownAir(new Aabb(new Vector3(4, 4, 4), Vector3.One * 8), 0, scratch);
    edits.Add(new Vector3(16, 16, 16), 4f, 1f, build: true); // build in the empty chunk
    bool buildDropsAir = !u.StillUniform(air, int.MaxValue, scratch, out _) && !u.KnownAir(air.Bounds, 0, scratch);
    Check(both && digKeepsAir && digDropsRock && knownAir && buildDropsAir,
          "known-empty chunks stay known through digs but not builds; known-rock chunks are dropped by any edit");

    // The fine planner flies straight through a rocky chunk once it's (wrongly) marked empty: it doesn't sample it.
    Vector3? rockyCentre = null;
    for (int x = -20; x < 20 && rockyCentre == null; x++)
      for (int z = -20; z < 20 && rockyCentre == null; z++)
      {
        var c = new Vector3(x * 32 + 16, 16, z * 32 + 16);
        int solid = 0;
        for (int i = -1; i <= 1; i++) if (_gen.RawDensity(c.X + i * 10, c.Y, c.Z) >= VoxelConst.Iso) solid++;
        if (solid == 3) rockyCentre = c;
      }
    if (rockyCentre is Vector3 rc)
    {
      var key = ChunkKey.Containing(rc, 0);
      var start = rc - new Vector3(14, 0, 0);
      var goal = rc + new Vector3(14, 0, 0);
      var req = new ShipPathfinder.Request(start, goal, 1.5f, 3f, 0);
      var normal = ShipPathfinder.Plan(_gen, req);
      var marked = new UniformChunks(new EditStore());
      for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
          for (int dz = -1; dz <= 1; dz++)
            marked.Remember(new ChunkKey(0, key.Coord + new Vector3I(dx, dy, dz)), true, -1);
      var trusted = ShipPathfinder.Plan(_gen, req, marked);
      GD.Print($"  rocky chunk {key}: plan without records found {normal.Found} ({normal.Expanded} cells), marked empty found {trusted.Found} ({trusted.Expanded} cells, {trusted.Points.Count} points)");
      Check(trusted.Found && trusted.Points.Count == 2 && (!normal.Found || normal.Points.Count > 2),
            "the pathfinder flies straight through chunks known to be empty without sampling them");
    }
    else
    {
      Check(false, "found a rocky chunk for the known-empty planner test");
    }
  }

  /// <summary>A long trip: the coarse route takes it most of the way, the fine path the first stretch.</summary>
  private void LongRoute()
  {
    const float clearance = 8.3f;
    bool Open(Vector3 p)
    {
      for (int z = -1; z <= 1; z++)
        for (int y = -1; y <= 1; y++)
          for (int x = -1; x <= 1; x++)
          {
            var q = p + new Vector3(x, y, z) * clearance;
            if (_gen.RawDensity(q.X, q.Y, q.Z) >= 0.4f) return false;
          }
      return true;
    }
    var rng = new RandomNumberGenerator { Seed = 33 };
    Vector3 start = default, goal = default;
    bool picked = false;
    for (int i = 0; i < 20000 && !picked; i++)
    {
      var a = new Vector3(rng.RandfRange(-800, 800), rng.RandfRange(-800, 800), rng.RandfRange(-800, 800));
      var dir = new Vector3(rng.RandfRange(-1, 1), rng.RandfRange(-1, 1), rng.RandfRange(-1, 1)).Normalized();
      var b = a + dir * 700f;
      if (!Open(a) || !Open(b)) continue;
      start = a;
      goal = b;
      picked = true;
    }
    if (!picked)
    {
      Check(false, "long route: found two open spots 700 apart");
      return;
    }
    var r = ShipPathfinder.Plan(_gen, new ShipPathfinder.Request(start, goal, clearance, 20f, 0));
    float fine = 0;
    bool clear = true;
    for (int i = 0; i + 1 < r.Points.Count; i++)
    {
      var a = r.Points[i];
      var b = r.Points[i + 1];
      fine += a.DistanceTo(b);
      int n = Math.Max(1, (int)(a.DistanceTo(b) / 0.5f));
      for (int s = 0; s <= n; s++)
      {
        var q = a.Lerp(b, s / (float)n);
        clear &= _gen.RawDensity(q.X, q.Y, q.Z) < VoxelConst.Iso;
      }
    }
    var end = r.Points[^1];
    GD.Print($"  long route: {start.DistanceTo(goal):F0} apart, coarse route {r.CoarseCells} cells, fine path {r.Points.Count} points {fine:F0} long " +
             $"ending {end.DistanceTo(start):F0} out, {r.Beyond:F0} left after it, {r.Expanded} fine cells, {r.Ms:F0} ms");
    Check(r.CoarseCells > 0 && r.Found && r.Beyond > 0 && end.DistanceTo(start) <= ShipPathfinder.FineReach + 40f,
          "a long trip plans a coarse route and a fine path along its first stretch");
    Check(clear && fine + r.Beyond >= start.DistanceTo(goal) * 0.85f, "the fine stretch is clear of rock and the whole route adds up to the trip");
  }

  private static void DeleteUserDir(string userPath)
  {
    var abs = ProjectSettings.GlobalizePath(userPath);
    if (System.IO.Directory.Exists(abs)) System.IO.Directory.Delete(abs, true);
  }

  /// <summary>Saved ships: written as JSON, listed after the built-in one, read back block for block.</summary>
  private void ShipLibraryRoundTrip()
  {
    const string dir = "user://test_ships_unit";
    DeleteUserDir(dir);
    var lib = new ShipLibrary(dir);
    bool builtInFirst = lib.Entries.Count == 1 && lib.Entries[0].BuiltIn && lib.Load(lib.Entries[0])?.Count == ShipPrefabs.Miner().Count;
    var grid = new ShipGrid();
    foreach (var (c, b) in ShipPrefabs.Miner()) grid.Add(c, b);
    var entry = lib.Save("Test / miner?", grid);
    bool listed = entry != null && entry.Name == "Test _ miner_" && lib.Entries.Count == 2 && lib.Entries[1] == entry
               && System.IO.File.Exists(entry.Path);
    var loaded = entry != null ? lib.Load(entry) : null;
    bool same = loaded != null && loaded.Count == grid.Count && loaded.All(x => grid.TryGet(x.Cell, out var g) && g == x.Block);
    lib.Save("Test / miner?", grid);
    bool overwrote = lib.Entries.Count == 2;
    System.IO.File.WriteAllText(System.IO.Path.Combine(lib.Directory, "broken.json"), "{ not json");
    lib.Refresh();
    bool skipped = lib.Entries.Count == 2;
    bool builtInKept = lib.Save(ShipLibrary.BuiltInName, grid) == null && lib.NextFreeName() == "Ship 1";
    GD.Print($"  ship library: {lib.Directory}, entries {string.Join(", ", lib.Entries.Select(e => e.Name))}");
    Check(builtInFirst && listed, "a saved ship (name made file-safe) is listed after the built-in starter miner");
    Check(same, "a saved ship loads back with the same blocks, types and facings");
    Check(overwrote && skipped && builtInKept, "saving the same name overwrites, broken files are skipped, the built-in can't be replaced");
    DeleteUserDir(dir);
  }

  /// <summary>Thrusters asked for an acceleration give exactly that (along it), or full throttle if it's more than they can.</summary>
  private void ControlAccel()
  {
    var ship = Ship.Create(null, 1f);
    foreach (var (c, b) in ShipPrefabs.Miner()) ship.AddBlock(c, b, rebuild: false);
    ship.Rebuild();
    var t = new float[ship.Thrusters.Count];
    bool exact = true, capped = true;
    var lines = new List<string>();
    foreach (var d in new[] { Vector3.Forward, Vector3.Back, Vector3.Left, Vector3.Right, Vector3.Up, Vector3.Down })
    {
      float reached = ShipControl.ThrottlesForAccel(ship, d * 0.3f, 25f, t);
      float got = ThrustAllocator.PushAlong(ship.Thrusters, t, d, 25f) / ship.Mass;
      exact &= Mathf.Abs(got - 0.3f) < 1e-3f && Mathf.Abs(reached - 0.3f) < 1e-3f && t.Max() <= 1f;
      float max = ShipControl.Capacity(ship, d, 25f) / ship.Mass;
      reached = ShipControl.ThrottlesForAccel(ship, d * 1000f, 25f, t);
      capped &= Mathf.IsEqualApprox(t.Max(), 1f) && Mathf.Abs(reached - max) < 1e-3f;
      lines.Add($"{d}: {max:F2}");
    }
    GD.Print($"  miner acceleration capacity per direction (u/s^2): {string.Join(", ", lines)}");
    Check(exact, "the thrusters give the asked-for acceleration along each axis");
    Check(capped, "asking for more acceleration than the thrusters have gives full throttle");
    ship.Free();
  }

  /// <summary>A* for ships: a path between two open spots with rock between them, that keeps clear of the rock.</summary>
  private void Pathfinder()
  {
    const float clearance = 8.3f, arrive = 20f;
    bool Open(Vector3 p, float r)
    {
      if (_gen.RawDensity(p.X, p.Y, p.Z) >= 0.4f) return false;
      for (int z = -1; z <= 1; z++)
        for (int y = -1; y <= 1; y++)
          for (int x = -1; x <= 1; x++)
          {
            var d = new Vector3(x, y, z);
            if (d == Vector3.Zero) continue;
            foreach (float k in new[] { 0.5f, 1f })
            {
              var q = p + d.Normalized() * r * k;
              if (_gen.RawDensity(q.X, q.Y, q.Z) >= 0.4f) return false;
            }
          }
      return true;
    }
    bool Blocked(Vector3 a, Vector3 b)
    {
      int n = (int)(a.DistanceTo(b) / 0.5f);
      for (int i = 0; i <= n; i++)
      {
        var q = a.Lerp(b, i / (float)n);
        if (_gen.RawDensity(q.X, q.Y, q.Z) >= VoxelConst.Iso) return true;
      }
      return false;
    }
    var rng = new RandomNumberGenerator { Seed = 21 };
    Vector3 start = default, goal = default;
    bool picked = false;
    for (int i = 0; i < 20000 && !picked; i++)
    {
      var a = new Vector3(rng.RandfRange(-600, 600), rng.RandfRange(-600, 600), rng.RandfRange(-600, 600));
      if (!Open(a, clearance)) continue;
      var dir = new Vector3(rng.RandfRange(-1, 1), rng.RandfRange(-1, 1), rng.RandfRange(-1, 1)).Normalized();
      var b = a + dir * 150f;
      if (!Open(b, clearance) || !Blocked(a, b)) continue;
      start = a;
      goal = b;
      picked = true;
    }
    if (!picked)
    {
      Check(false, "pathfinder: found two open spots with rock between them");
      return;
    }
    var result = ShipPathfinder.Plan(_gen, new ShipPathfinder.Request(start, goal, clearance, arrive, 0));
    float length = 0;
    bool clear = true;
    float worstMargin = float.PositiveInfinity;
    for (int i = 0; i + 1 < result.Points.Count; i++)
    {
      var a = result.Points[i];
      var b = result.Points[i + 1];
      length += a.DistanceTo(b);
      int n = Math.Max(1, (int)(a.DistanceTo(b) / 0.5f));
      for (int s = 0; s <= n; s++)
      {
        var q = a.Lerp(b, s / (float)n);
        foreach (var o in new[] { Vector3.Zero, Vector3.Right, Vector3.Left, Vector3.Up, Vector3.Down, Vector3.Forward, Vector3.Back })
        {
          var p = q + o * clearance * 0.5f;
          float d = _gen.RawDensity(p.X, p.Y, p.Z);
          worstMargin = Mathf.Min(worstMargin, VoxelConst.Iso - d);
          clear &= d < VoxelConst.Iso;
        }
      }
    }
    var end = result.Points[^1];
    GD.Print($"  pathfinder: {start.DistanceTo(goal):F0} apart, path {result.Points.Count} points, {length:F0} long, " +
             $"ends {end.DistanceTo(goal):F1} from the goal, {result.Expanded} cells expanded in {result.Ms:F0} ms, worst density margin {worstMargin:F2}");
    Check(result.Found && end.DistanceTo(goal) <= arrive, "the pathfinder finds a way round rock to within arrive range of the goal");
    Check(clear, "the path keeps the ship's body (half its clearance each way) out of rock");
  }

  /// <summary>Thruster allocation and block data: which thrusters fire for a direction; parts keep type and facing.</summary>
  private void ShipParts()
  {
    // Nozzles: back (+Z, pushes forward), front (-Z, pushes back), right (+X, pushes left), left (-X, pushes right).
    var thrusters = new List<(Vector3I, Vector3I)>
    {
      (Vector3I.Zero, Vector3I.Back), (Vector3I.Zero, Vector3I.Forward), (Vector3I.Zero, Vector3I.Right), (Vector3I.Zero, Vector3I.Left),
    };
    var t = new float[4];
    ThrustAllocator.Throttles(thrusters, Vector3.Forward, t);
    bool forward = t[0] == 1f && t[1] == 0f && t[2] == 0f && t[3] == 0f;
    ThrustAllocator.Throttles(thrusters, Vector3.Back, t);
    bool back = t[0] == 0f && t[1] == 1f;
    ThrustAllocator.Throttles(thrusters, Vector3.Right, t);
    bool strafe = t[0] == 0f && t[1] == 0f && t[2] == 0f && t[3] == 1f;
    ThrustAllocator.Throttles(thrusters, (Vector3.Forward + Vector3.Right) * 0.5f, t);
    bool diagonal = t[0] > 0.4f && t[3] > 0.4f && t[1] == 0f && t[2] == 0f;
    Check(forward && back && strafe && diagonal,
      "thrusters fire for the directions they push (rear nozzle = forward, side nozzles = strafe, none backwards)");

    var grid = new ShipGrid();
    grid.Add(Vector3I.Zero);
    grid.Add(Vector3I.Right, new Block(BlockType.Thruster, Vector3I.Back));
    grid.Add(Vector3I.Left, new Block(BlockType.Laser, Vector3I.Forward));
    bool kept = grid.TryGet(Vector3I.Right, out var th) && th == new Block(BlockType.Thruster, Vector3I.Back)
             && grid.TryGet(Vector3I.Left, out var la) && la.Type == BlockType.Laser && la.Facing == Vector3I.Forward
             && grid.TryGet(Vector3I.Zero, out var hu) && hu.Type == BlockType.Hull;
    var mesh = ShipMesh.Build(grid)!;
    var colors = mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Color].AsColorArray();
    // Vertex colours are stored at 8 bits per channel: compare approximately.
    bool Has(Color want) => colors.Any(c => Mathf.Abs(c.R - want.R) + Mathf.Abs(c.G - want.G) + Mathf.Abs(c.B - want.B) < 0.03f);
    bool coloured = Has(ShipMesh.NozzleColor) && Has(ShipMesh.EmitterColor);
    Check(kept && coloured, "ship blocks keep their type and facing, and the mesh marks nozzles and emitters");
  }

  /// <summary>Impact model: only the velocity straight into the surface digs; energy decides crater and leftover speed.</summary>
  private void ImpactMath()
  {
    var settings = new ImpactModel.Settings { MinSpeed = 2f, DeformShare = 0.5f, Hardness = 60f, MaxCraterRadius = 50f };
    var up = Vector3.Up;
    bool headOn = ImpactModel.Evaluate(10f, new Vector3(0, -20, 0), up, settings, out var h);
    // Same speed, 80 degrees off the normal: a skid.
    var skidVel = new Vector3(MathF.Sin(Mathf.DegToRad(80)), -MathF.Cos(Mathf.DegToRad(80)), 0) * 20f;
    bool skid = ImpactModel.Evaluate(10f, skidVel, up, settings, out var g);
    bool gentle = ImpactModel.Evaluate(10f, new Vector3(5, -1, 0), up, settings, out _);
    float expectedEnergy = 0.5f * 10f * 400f * 0.5f;
    GD.Print($"impact model: head-on 20 u/s -> crater {h.CraterRadius:F2}, keeps {h.RemainingNormalSpeed:F2} u/s; " +
             $"80-degree skid -> crater {g.CraterRadius:F2}; 1 u/s touch digs {gentle}");
    Check(headOn && Mathf.IsEqualApprox(h.Energy, expectedEnergy) && Mathf.IsEqualApprox(h.RemainingNormalSpeed, 20f * MathF.Sqrt(0.5f)),
      "a head-on impact puts half its energy into the crater and keeps the speed for the other half");
    Check(skid && g.CraterRadius < h.CraterRadius * 0.4f && !gentle,
      "a skid barely digs (only the part straight into the surface counts) and a gentle touch doesn't dig");
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

  // ------------------------------------------------------------------ big-brush benchmark

  /// <summary>
  /// `-- --bench-dig[=radius]`: holds a big brush (Player-style: 15 ticks/s, re-aimed each tick) on terrain a few
  /// chunks ahead for 6 s and logs every commit and, each second, where the worker time went.
  /// </summary>
  private async System.Threading.Tasks.Task BenchDig(Node scene, ChunkManager cm, float radius)
  {
    AddChild(scene);
    var player = scene.GetNode<Player>("Player");
    player.SetPhysicsProcess(false); // the harness moves the player around itself
    var pivot = player.GetNode<Node3D>("CameraPivot");
    pivot.Rotation = new Vector3(-0.45f, 0, 0);
    async System.Threading.Tasks.Task Idle()
    {
      int idle = 0;
      var sw0 = Stopwatch.StartNew();
      while (idle < 30 && sw0.Elapsed.TotalSeconds < 60)
      {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        bool busy = cm.QueueLength + cm.BusyWorkers + cm.PendingChunks + cm.RetiringChunks > 0;
        idle = busy ? 0 : idle + 1;
      }
    }
    await Idle();
    var cam = GetViewport().GetCamera3D();
    // Clear a pocket around the camera so the aim reaches terrain ahead.
    cm.Dig(cam.GlobalPosition, 12);
    await Idle();

    var names = VoxelProfiler.Names;
    var prev = new long[(int)VoxelProfiler.Section.Count];
    var now = new long[prev.Length];
    VoxelProfiler.Snapshot(prev);
    long hit0 = VoxelProfiler.CacheHits, miss0 = VoxelProfiler.CacheMisses, ed0 = VoxelProfiler.EditsApplied;
    cm.ResetMorphStats();
    int commits0 = cm.BatchesCommitted, lastCommits = commits0;
    var sw = Stopwatch.StartNew();
    double nextTick = 0, lastCommitAt = 0, nextReport = 1, worstFrame = 0, lastFrame = 0;
    var gaps = new List<double>();
    float speed = (float)player.Get("DigSpeed"), rate = (float)player.Get("DigTickRate");
    var rateArg = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--rate="));
    if (rateArg != null) rate = float.Parse(rateArg["--rate=".Length..], System.Globalization.CultureInfo.InvariantCulture);
    float strength = 1f - Mathf.Exp(-speed / rate);
    int edits0 = cm.EditCount, merged0 = cm.EditsMerged, ticks = 0;
    GD.Print($"bench: radius {radius}, dig speed {speed}, {rate} ticks/s, {cm.WorkerCount} workers, holding for 6 s");
    while (sw.Elapsed.TotalSeconds < 6)
    {
      double t = sw.Elapsed.TotalSeconds;
      if (t >= nextTick)
      {
        var from = cam.GlobalPosition;
        if (cm.SphereCastTerrain(from, from - cam.GlobalBasis.Z * 1000, radius, out var aim, radius * 3)) cm.Dig(aim, radius, strength);
        ticks++;
        nextTick += 1.0 / rate;
      }
      await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
      double t2 = sw.Elapsed.TotalSeconds;
      worstFrame = Math.Max(worstFrame, (t2 - lastFrame) * 1000);
      lastFrame = t2;
      if (cm.BatchesCommitted != lastCommits)
      {
        lastCommits = cm.BatchesCommitted;
        double gap = (t2 - lastCommitAt) * 1000;
        if (lastCommitAt > 0) gaps.Add(gap);
        lastCommitAt = t2;
        var lods = string.Join(" ", cm.LastBatchByLod.Select((n, l) => (n, l)).Where(x => x.n > 0).Select(x => $"L{x.l}:{x.n}"));
        GD.Print($"  {t2:F2}s commit: gap {gap:F0} ms, batch {cm.LastBatchSize} [{lods}] built in {cm.LastBatchMs:F0} ms, morph {cm.LastMorphSeconds * 1000:F0} ms, edits {cm.EditCount}");
      }
      if (t2 >= nextReport)
      {
        nextReport += 1;
        VoxelProfiler.Snapshot(now);
        var parts = Enumerable.Range(0, now.Length).Select(i => (names[i], ms: VoxelProfiler.TicksToMs(now[i] - prev[i]))).Where(x => x.ms > 1).OrderByDescending(x => x.ms)
          .Select(x => $"{x.Item1} {x.ms:F0}");
        GD.Print($"  -- second {nextReport - 1:F0}: worker ms: {string.Join(", ", parts)}; volumes cached {VoxelProfiler.CacheHits - hit0} / generated {VoxelProfiler.CacheMisses - miss0}, " +
                 $"edits applied {VoxelProfiler.EditsApplied - ed0}, worst frame {worstFrame:F1} ms");
        (prev, now) = (now, prev);
        hit0 = VoxelProfiler.CacheHits; miss0 = VoxelProfiler.CacheMisses; ed0 = VoxelProfiler.EditsApplied;
        worstFrame = 0;
      }
    }
    gaps.Sort();
    GD.Print($"bench summary: {cm.BatchesCommitted - commits0} commits, gap median {(gaps.Count > 0 ? gaps[gaps.Count / 2] : 0):F0} ms, " +
             $"max {(gaps.Count > 0 ? gaps[^1] : 0):F0} ms, average morph jump {cm.MorphJumpAverage * 100:F0}% of a step; " +
             $"{ticks} brush ticks became {cm.EditCount - edits0} edits ({cm.EditsMerged - merged0} merged)");
  }

  // ------------------------------------------------------------------ screenshots

  /// <summary>
  /// The start of the game: waits for the load, checks the bubble around the player is empty and the prefab ship is
  /// there and simulating, then flies it forward firing its lasers (spawn.png, prefab_pilot.png, prefab_beams.png).
  /// </summary>
  private async System.Threading.Tasks.Task SpawnShots(Node scene, ChunkManager cm, string dir)
  {
    AddChild(scene);
    Input.MouseMode = Input.MouseModeEnum.Visible;
    var player = scene.GetNode<Player>("Player");
    var builder = scene.GetNode<ShipBuilder>("Ships");
    var sw = Stopwatch.StartNew();
    int idle = 0;
    while (sw.Elapsed.TotalSeconds < 90 && idle < 30)
    {
      await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
      bool busy = (int)Performance.GetCustomMonitor("voxel/queue") + (int)Performance.GetCustomMonitor("voxel/busy_workers")
                + (int)Performance.GetCustomMonitor("voxel/pending") > 0;
      idle = busy ? 0 : idle + 1;
    }
    var ship = builder.Ships.FirstOrDefault(s => s.AI == null);
    for (int i = 0; i < 120 && ship != null && ship.Paused; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    GD.Print($"loaded in {sw.Elapsed.TotalSeconds:F1} s; ships {builder.ShipCount}, prefab {ship?.BlockCount} blocks, "
           + $"{ship?.Thrusters.Count} thrusters, {ship?.Lasers.Count} lasers, simulating {ship is { Paused: false }}");
    Check(ship != null && ship.Thrusters.Count >= 20 && ship.Lasers.Count >= 4, "the prefab ship spawns in front of the player");
    Check(ship is { Paused: false }, "the prefab ship is simulating at spawn");

    // Nothing within the clearing: rays from the player in every direction travel at least radius - edge.
    float nearest = float.PositiveInfinity;
    var rng = new RandomNumberGenerator { Seed = 3 };
    for (int i = 0; i < 64; i++)
    {
      var d = new Vector3(rng.RandfRange(-1, 1), rng.RandfRange(-1, 1), rng.RandfRange(-1, 1)).Normalized();
      if (cm.RaycastTerrain(player.GlobalPosition, player.GlobalPosition + d * 1000, out var hit))
        nearest = Mathf.Min(nearest, player.GlobalPosition.DistanceTo(hit));
    }
    GD.Print($"  nearest terrain from spawn: {nearest:F0} (clear radius {cm.SpawnClearRadius})");
    Check(nearest > cm.SpawnClearRadius - cm.SpawnClearEdge, "the spawn bubble is empty");

    await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
    GetViewport().GetTexture().GetImage().SavePng($"{dir}/spawn.png");
    GD.Print($"saved {dir}/spawn.png");
    await HatchFollowsShip(dir);
    if (ship == null) return;

    // The hotbar with each tool held (the prefab one facing back into the open, with AI on).
    var viewBasis = player.GlobalBasis;
    foreach (var (tool, name) in new[] { (Player.Tool.Terrain, "terrain"), (Player.Tool.Ship, "ship"), (Player.Tool.Prefab, "prefab") })
    {
      player.SelectTool(tool);
      if (tool == Player.Tool.Ship) player.SelectedBlock = BlockType.Thruster;
      if (tool == Player.Tool.Prefab)
      {
        player.PrefabJob = AIJob.Follow;
        player.GlobalBasis = viewBasis.Rotated(viewBasis.Y, Mathf.Pi);
      }
      await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout);
      GetViewport().GetTexture().GetImage().SavePng($"{dir}/hotbar_{name}.png");
      GD.Print($"saved {dir}/hotbar_{name}.png (slot {player.Hotbar.SelectedSlot}: {player.Hotbar.Title}; {player.Hotbar.Hint})");
    }
    player.PrefabJob = null;
    player.GlobalBasis = viewBasis;
    player.SelectTool(Player.Tool.Terrain);

    // The AI follower once it has flown in and stopped, seen from the player.
    var ai = builder.Ships.Select(s => s.AI).FirstOrDefault(a => a != null);
    if (ai != null)
    {
      var fsw = Stopwatch.StartNew();
      while (fsw.Elapsed.TotalSeconds < 60 && !(ai.State == ShipAI.Mode.Holding && ai.GetParent<Ship>().LinearVelocity.Length() < 1f))
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
      var aiShip = ai.GetParent<Ship>();
      var look = new Camera3D { Fov = 70, Far = 4000 };
      scene.AddChild(look);
      var eye = player.GlobalPosition;
      var at = aiShip.GlobalTransform * aiShip.CenterOfMass;
      look.GlobalTransform = Transform3D.Identity.Translated(eye).LookingAt(at, Vector3.Up);
      var prev = GetViewport().GetCamera3D();
      look.MakeCurrent();
      await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
      GetViewport().GetTexture().GetImage().SavePng($"{dir}/follower.png");
      GD.Print($"saved {dir}/follower.png ({ai.State} {ai.DistanceToTarget:F1} from the player after {fsw.Elapsed.TotalSeconds:F1} s)");
      prev?.MakeCurrent();
      look.QueueFree();
    }

    player.EnterShip(ship);
    Input.ActionPress("move_forward");
    Input.ActionPress("fire");
    await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);
    GetViewport().GetTexture().GetImage().SavePng($"{dir}/prefab_pilot.png");
    GD.Print($"saved {dir}/prefab_pilot.png ({ship.LinearVelocity.Length():F1} u/s after 1 s)");
    Input.ActionRelease("move_forward");
    // Tilt down a little with the mouse and keep firing, so the beams are seen at an angle.
    for (int i = 0; i < 20; i++)
    {
      player.Pilot.AddMouse(new Vector2(0, 6));
      await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }
    await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout);
    GetViewport().GetTexture().GetImage().SavePng($"{dir}/prefab_beams.png");
    int beamHits = player.Pilot.Beams.Count(b => b.Hit);
    GD.Print($"saved {dir}/prefab_beams.png ({player.Pilot.Beams.Count} beams, {beamHits} hitting)");
    Input.ActionRelease("fire");

    // The map, centred on the ship being flown.
    var map = scene.GetNode<TerrainMap>("Map");
    Input.ParseInputEvent(new InputEventAction { Action = "map", Pressed = true });
    Input.ParseInputEvent(new InputEventAction { Action = "map", Pressed = false });
    var msw = Stopwatch.StartNew();
    while (msw.Elapsed.TotalSeconds < 20 && (map.Version == 0 || map.Building)) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
    GetViewport().GetTexture().GetImage().SavePng($"{dir}/map.png");
    GD.Print($"saved {dir}/map.png ({map.ChunkMeshes} chunks, build {map.LastBuildMs:F0} ms)");
    // Zoomed in and orbited round a bit.
    for (int i = 0; i < 8; i++)
      map.HandleInput(new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true, Factor = 1 });
    map.HandleInput(new InputEventMouseMotion { Relative = new Vector2(200, -60) });
    await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
    GetViewport().GetTexture().GetImage().SavePng($"{dir}/map_close.png");
    GD.Print($"saved {dir}/map_close.png");
  }

  /// <summary>
  /// The pencil strokes on a ship are painted on it: render a lone ship, move it and the camera together (same
  /// lighting), render again, and the two images match. World-anchored strokes would slide and differ.
  /// </summary>
  private async System.Threading.Tasks.Task HatchFollowsShip(string dir)
  {
    var vp = new SubViewport { Size = new Vector2I(256, 256), OwnWorld3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
    AddChild(vp);
    var env = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = Colors.Black };
    vp.AddChild(new WorldEnvironment { Environment = env });
    // Lit from behind the ship (toward the camera): the faces in view are in shade, so they show strokes.
    vp.AddChild(new DirectionalLight3D { Transform = Transform3D.Identity.LookingAt(new Vector3(0.5f, -0.2f, 1f), Vector3.Up) });
    var grid = new ShipGrid();
    foreach (var (c, b) in ShipPrefabs.Miner()) grid.Add(c, b);
    var mesh = new MeshInstance3D { Mesh = ShipMesh.Build(grid), MaterialOverride = GD.Load<Material>("res://materials/ship_toon.tres") };
    vp.AddChild(mesh);
    var cam = new Camera3D { Fov = 50 };
    vp.AddChild(cam);
    var shipAt = new Transform3D(new Basis(new Vector3(0.3f, 1f, 0.1f).Normalized(), 0.7f), Vector3.Zero);
    var camAt = Transform3D.Identity.Translated(new Vector3(9, 6, 14)).LookingAt(Vector3.Zero, Vector3.Up);
    async System.Threading.Tasks.Task<Image> Render(Vector3 offset)
    {
      mesh.GlobalTransform = shipAt.Translated(offset);
      cam.GlobalTransform = camAt.Translated(offset);
      for (int i = 0; i < 4; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
      return vp.GetTexture().GetImage();
    }
    var a = await Render(Vector3.Zero);
    var b2 = await Render(new Vector3(13.37f, 5.21f, -7.9f));
    a.SavePng($"{dir}/ship_hatch_a.png");
    b2.SavePng($"{dir}/ship_hatch_b.png");
    double diff = 0;
    for (int y = 0; y < a.GetHeight(); y++)
      for (int x = 0; x < a.GetWidth(); x++)
      {
        Color p = a.GetPixel(x, y), q = b2.GetPixel(x, y);
        diff += (Mathf.Abs(p.R - q.R) + Mathf.Abs(p.G - q.G) + Mathf.Abs(p.B - q.B)) / 3.0;
      }
    diff /= a.GetWidth() * a.GetHeight();
    GD.Print($"  ship hatching: mean pixel difference after moving ship and camera together {diff * 100:F2}%");
    Check(diff < 0.005, "the pencil strokes on a ship move with it (moving ship and camera together changes nothing)");
    vp.QueueFree();
  }

  /// <summary>
  /// Several AI diggers mining at once near the spawn bubble's wall: reports frame times, how fast edits pile up and
  /// whether the terrain keeps up (queue, pending chunks, how far behind the shown terrain is).
  /// </summary>
  private async System.Threading.Tasks.Task DigStress(Node scene, ChunkManager cm, int count)
  {
    AddChild(scene);
    var player = scene.GetNode<Player>("Player");
    player.SetPhysicsProcess(false);
    var builder = scene.GetNode<ShipBuilder>("Ships");
    var centre = player.GlobalPosition;
    var sw = Stopwatch.StartNew();
    int idle = 0;
    while (sw.Elapsed.TotalSeconds < 120 && idle < 30)
    {
      await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
      bool busy = (int)Performance.GetCustomMonitor("voxel/queue") + (int)Performance.GetCustomMonitor("voxel/busy_workers")
                + (int)Performance.GetCustomMonitor("voxel/pending") > 0;
      idle = busy ? 0 : idle + 1;
    }
    var diggers = new List<Ship>();
    for (int i = 0; i < count; i++)
    {
      // Spread round the bubble, about 70 in from its wall.
      float a = Mathf.Tau * i / count;
      var dir = new Vector3(Mathf.Cos(a), 0.3f * Mathf.Sin(a * 2f), Mathf.Sin(a)).Normalized();
      diggers.Add(builder.SpawnAIShip(ShipPrefabs.Miner(), new Transform3D(Basis.Identity, centre + dir * (cm.SpawnClearRadius - 70f)), AIJob.Dig));
    }
    GD.Print($"dig stress: {count} diggers, loaded in {sw.Elapsed.TotalSeconds:F1} s");
    var prof0 = new long[(int)VoxelProfiler.Section.Count];
    VoxelProfiler.Snapshot(prof0);
    long built0 = VoxelProfiler.ChunksBuilt;
    double rayMs0 = cm.DigMsTotal;
    const double seconds = 40;
    sw.Restart();
    var frame = Stopwatch.StartNew();
    double worst = 0, sum = 0;
    int frames = 0, slow = 0;
    int edits0 = cm.EditCount;
    double nextReport = 5;
    double worstWindow = 0;
    int framesWindow = 0;
    double sumWindow = 0;
    double sumMesh = 0, sumCollision = 0, sumSwap = 0, sumDiff = 0, sumTerrain = 0;
    while (sw.Elapsed.TotalSeconds < seconds)
    {
      frame.Restart();
      await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
      double ms = frame.Elapsed.TotalMilliseconds;
      worst = Math.Max(worst, ms);
      worstWindow = Math.Max(worstWindow, ms);
      sum += ms;
      sumWindow += ms;
      frames++;
      framesWindow++;
      if (ms > 33) slow++;
      var lf = cm.LastFrame;
      sumMesh += lf.ApplyMeshMs;
      sumCollision += lf.CollisionMs;
      sumSwap += lf.SwapMs;
      sumDiff += lf.OctreeDiffMs;
      sumTerrain += lf.TotalMs;
      if (sw.Elapsed.TotalSeconds >= nextReport)
      {
        int digs = diggers.Sum(d => d.AI!.Lasers.Digs);
        int digging = diggers.Count(d => d.AI!.State == ShipAI.Mode.Digging);
        GD.Print($"  {sw.Elapsed.TotalSeconds:F0} s: {digging}/{count} digging, {digs} laser hits, {cm.EditCount - edits0} edits stored " +
                 $"({cm.EditCount} total), frames avg {sumWindow / framesWindow:F1} ms worst {worstWindow:F0} ms, " +
                 $"queue {Performance.GetCustomMonitor("voxel/queue")}, pending {Performance.GetCustomMonitor("voxel/pending")}, " +
                 $"busy {Performance.GetCustomMonitor("voxel/busy_workers")}, terrain main thread {(double)Performance.GetCustomMonitor("voxel/process_ms"):F1} ms, " +
                 $"last batch {cm.LastBatchSize} chunks {cm.LastBatchMs:F0} ms, cache {Performance.GetCustomMonitor("voxel/cached_volumes")}");
        GD.Print($"      terrain main thread per frame: mesh apply {sumMesh / framesWindow:F2} ms, collision {sumCollision / framesWindow:F2}, " +
                 $"swaps {sumSwap / framesWindow:F2}, octree {sumDiff / framesWindow:F2}, all incl. rays {sumTerrain / framesWindow:F2}");
        sumMesh = sumCollision = sumSwap = sumDiff = sumTerrain = 0;
        nextReport += 5;
        worstWindow = 0;
        sumWindow = 0;
        framesWindow = 0;
      }
    }
    int totalDigs = diggers.Sum(d => d.AI!.Lasers.Digs);
    int stored = cm.EditCount - edits0;
    var prof1 = new long[(int)VoxelProfiler.Section.Count];
    VoxelProfiler.Snapshot(prof1);
    long built = VoxelProfiler.ChunksBuilt - built0;
    var parts = new List<string>();
    for (int i = 0; i < prof1.Length; i++)
      parts.Add($"{VoxelProfiler.Names[i]} {VoxelProfiler.TicksToMs(prof1[i] - prof0[i]) / Math.Max(1, built):F1}");
    GD.Print($"  worker time per chunk build ({built} builds, ms): {string.Join(", ", parts)}");
    GD.Print($"  main-thread terrain rays / digs: {cm.DigMsTotal - rayMs0:F0} ms total ({(cm.DigMsTotal - rayMs0) / seconds:F1} ms per second)");
    GD.Print($"dig stress result: {frames / seconds:F0} fps avg, frame avg {sum / frames:F1} ms, worst {worst:F0} ms, {slow} frames over 33 ms; " +
             $"{totalDigs} laser hits -> {stored} stored edits ({stored / seconds:F0}/s)");
    // Afterwards: does the terrain catch up?
    foreach (var d in diggers) d.AI!.Job = AIJob.Follow;
    sw.Restart();
    idle = 0;
    while (sw.Elapsed.TotalSeconds < 60 && idle < 30)
    {
      await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
      bool busy = (int)Performance.GetCustomMonitor("voxel/queue") + (int)Performance.GetCustomMonitor("voxel/busy_workers")
                + (int)Performance.GetCustomMonitor("voxel/pending") > 0;
      idle = busy ? 0 : idle + 1;
    }
    GD.Print($"  terrain settled {sw.Elapsed.TotalSeconds:F1} s after the digging stopped");
    Check(sum / frames < 33 && totalDigs > 0, $"{count} diggers mining at once keep the game above 30 fps on average");
  }

  /// <summary>
  /// The AI follower in the real startup: it flies in and holds station, follows when the player moves, routes round
  /// rock without hitting it, and gives way to a pilot.
  /// </summary>
  private async System.Threading.Tasks.Task FollowRun(Node scene, ChunkManager cm)
  {
    AddChild(scene);
    var player = scene.GetNode<Player>("Player");
    player.SetPhysicsProcess(false); // the harness moves the player
    var spawnCentre = player.GlobalPosition; // the middle of the cleared bubble
    var builder = scene.GetNode<ShipBuilder>("Ships");
    var sw = Stopwatch.StartNew();
    int idle = 0;
    while (sw.Elapsed.TotalSeconds < 120 && idle < 30)
    {
      await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
      bool busy = (int)Performance.GetCustomMonitor("voxel/queue") + (int)Performance.GetCustomMonitor("voxel/busy_workers")
                + (int)Performance.GetCustomMonitor("voxel/pending") > 0;
      idle = busy ? 0 : idle + 1;
    }
    var ai = builder.Ships.Select(s => s.AI).FirstOrDefault(a => a != null);
    Check(ai != null, "an AI-flown copy of the starter ship spawns at the start");
    if (ai == null) return;
    var ship = ai.GetParent<Ship>();
    GD.Print($"  loaded in {sw.Elapsed.TotalSeconds:F1} s; follower {ship.BlockCount} blocks, {ai.DistanceToTarget:F0} from the player, {ai.State}");
    bool touched = false;

    async System.Threading.Tasks.Task<bool> Settle(string label, double timeout)
    {
      var s = Stopwatch.StartNew();
      int good = 0;
      float closest = float.PositiveInfinity;
      while (s.Elapsed.TotalSeconds < timeout && good < 60)
      {
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        foreach (var b in ship.GetCollidingBodies())
          touched |= b is Node n && n.IsInGroup(ShipBuilder.TerrainGroup);
        closest = Mathf.Min(closest, ai.DistanceToTarget);
        bool ok = ai.State == ShipAI.Mode.Holding && ai.DistanceToTarget <= ai.FollowDistance + 2f && ship.LinearVelocity.Length() < 1.5f;
        good = ok ? good + 1 : 0;
      }
      GD.Print($"  {label}: {s.Elapsed.TotalSeconds:F1} s, {ai.State}, {ai.DistanceToTarget:F1} from the player (closest {closest:F1}), " +
               $"speed {ship.LinearVelocity.Length():F2}, {ai.PlanCount} plans (last {ai.LastPlanMs:F1} ms), touched terrain {touched}");
      return good >= 60 && closest > ai.FollowDistance * 0.4f;
    }

    // 1. It flies in from its spawn and stops in range.
    Check(await Settle("fly in", 60), "the follower flies to the player and holds station within the follow distance");

    // 2. The player moves off: it follows again and stops again.
    player.GlobalPosition += new Vector3(0.6f, 0.2f, -0.8f).Normalized() * 70f;
    bool resumed = false;
    for (int i = 0; i < 60 && !resumed; i++)
    {
      await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
      resumed = ai.State == ShipAI.Mode.Following;
    }
    Check(resumed && await Settle("after the player moved", 60), "when the player moves away it follows and holds station again");

    // 3. Rock in the way: the player goes to an open spot out in the asteroid field that the follower can't see
    //    in a straight line, but that has a way round.
    var gen = cm.Generator!;
    var com = ship.GlobalTransform * ship.CenterOfMass;
    var centre = player.GlobalPosition;
    var rng = new RandomNumberGenerator { Seed = 5 };
    Vector3? spot = null;
    ShipPathfinder.Result? plan = null;
    int tries = 0;
    for (int i = 0; i < 3000 && spot == null && tries < 25; i++)
    {
      var d = new Vector3(rng.RandfRange(-1, 1), rng.RandfRange(-1, 1), rng.RandfRange(-1, 1)).Normalized();
      var p = centre + d * rng.RandfRange(cm.SpawnClearRadius + 30f, cm.SpawnClearRadius + 160f);
      if (cm.DensityAt(p) > 0.05f || !cm.RaycastTerrain(com, p, out _)) continue;
      bool open = true;
      foreach (var o in new[] { Vector3.Right, Vector3.Left, Vector3.Up, Vector3.Down, Vector3.Forward, Vector3.Back })
        open &= !cm.RaycastTerrain(p, p + o * (ai.FollowDistance + ai.Clearance), out _);
      if (!open) continue;
      tries++;
      var r = ShipPathfinder.Plan(gen, new ShipPathfinder.Request(cm.ToLocal(com), cm.ToLocal(p), ai.Clearance, ai.FollowDistance, 0));
      if (!r.Found) continue;
      spot = p;
      plan = r;
    }
    if (spot is Vector3 dest && plan != null)
    {
      int craters = builder.CratersDug;
      int plans = ai.PlanCount;
      touched = false;
      GD.Print($"  obstacle: player sent {com.DistanceTo(dest):F0} away behind rock, test plan {plan.Points.Count} points in {plan.Ms:F0} ms ({tries} candidates)");
      player.GlobalPosition = dest;
      bool there = await Settle("round the rock", 150);
      Check(there && ai.PlanCount > plans, "with rock in the way it plans a path and gets to the player");
      Check(builder.CratersDug == craters && !touched, "it gets there without hitting the rock");
    }
    else
    {
      Check(false, "obstacle test: found an open spot behind rock with a way round");
    }

    // Empty space: the bubble's chunks are known empty; after going away and coming back they aren't rebuilt.
    {
      player.GlobalPosition = spawnCentre;
      var ws = Stopwatch.StartNew();
      int quiet = 0;
      while (ws.Elapsed.TotalSeconds < 60 && quiet < 30)
      {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        quiet = (int)Performance.GetCustomMonitor("voxel/queue") + (int)Performance.GetCustomMonitor("voxel/busy_workers")
              + (int)Performance.GetCustomMonitor("voxel/pending") > 0 ? 0 : quiet + 1;
      }
      var uni = cm.Uniform!;
      GD.Print($"  uniform chunks: {uni.Count} known, {uni.Hits} builds skipped, {uni.Stale} dropped by edits");
      Check(uni.Count > 0 && uni.Hits > 0, "empty chunks are remembered and skipped when they stream back in");

      // Building into a known-empty chunk next to the player: the record goes and the new rock gets a mesh.
      var scratch = new List<SphereEdit>();
      var k = ChunkKey.Containing(cm.ToLocal(spawnCentre) + new Vector3(0, 40, 0), 0);
      bool knownBefore = uni.KnownAir(k.Bounds, 0, scratch);
      var buildAt = cm.ToGlobal(k.Bounds.GetCenter());
      cm.Fill(buildAt, 5f, 1f);
      bool meshed = false;
      ws.Restart();
      while (ws.Elapsed.TotalSeconds < 20 && !meshed)
      {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        for (var c = k; c.Lod <= 3 && !meshed; c = c.Parent)
          meshed = cm.TryGetChunk(c, out var chunk) && chunk.MeshInstance != null && chunk.MeshInstance.Visible;
      }
      Check(knownBefore && !uni.KnownAir(k.Bounds, 0, scratch) && meshed, "building into a known-empty chunk shows the new rock");
      cm.Dig(buildAt, 7f, 1f);
      for (int i = 0; i < 30; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    // 4. A digger: an AI miner spawned near the clearing wall finds the rock and mines it without ramming it.
    {
      var dir = new Vector3(0.3f, -0.4f, 0.85f).Normalized();
      var at = spawnCentre + dir * (cm.SpawnClearRadius - 70f);
      var digger = builder.SpawnAIShip(ShipPrefabs.Miner(), new Transform3D(Basis.Identity, at), AIJob.Dig);
      var dai = digger.AI!;
      int craters = builder.CratersDug;
      int edits0 = cm.EditCount + cm.EditsMerged;
      bool touchedRock = false, reachedDigging = false;
      var ds = Stopwatch.StartNew();
      while (ds.Elapsed.TotalSeconds < 60 && !(reachedDigging && dai.Lasers.Digs >= 200))
      {
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        reachedDigging |= dai.State == ShipAI.Mode.Digging;
        foreach (var b in digger.GetCollidingBodies())
          touchedRock |= b is Node n && n.IsInGroup(ShipBuilder.TerrainGroup);
      }
      int edits = cm.EditCount + cm.EditsMerged - edits0;
      GD.Print($"  digger: {ds.Elapsed.TotalSeconds:F1} s, {dai.State}, {dai.Lasers.Digs} laser digs ({edits} edits), " +
               $"rock {dai.DistanceToTarget:F0} away, touched rock {touchedRock}, craters {builder.CratersDug - craters}");
      Check(reachedDigging && dai.Lasers.Digs >= 200 && edits > 0, "a dig-job AI ship finds the nearest rock and mines it with its lasers");
      Check(!touchedRock && builder.CratersDug == craters, "the digger mines from a stand-off without ramming the rock");
      builder.RemoveChild(digger);
      digger.QueueFree();
    }

    // 5. Climbing in: the AI lets go.
    player.EnterShip(ship);
    for (int i = 0; i < 5; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    Check(ai.State == ShipAI.Mode.Yielded && ai.Throttles.All(t => t == 0f), "while the player flies the follower, the AI does nothing");
    player.ExitShip();
  }

  private async System.Threading.Tasks.Task Screenshots(Node scene, ChunkManager cm, string dir)
  {
    AddChild(scene);
    Input.MouseMode = Input.MouseModeEnum.Visible;
    var player = scene.GetNode<Player>("Player");
    player.SetPhysicsProcess(false); // the harness moves the player around itself
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

    // Dig radius preview: three scroll-down notches, captured 0.3 s into the 1 s fade.
    pivot.Rotation = new Vector3(-0.35f, 0, 0);
    for (int i = 0; i < 3; i++)
    {
      Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.WheelDown, Pressed = true, Factor = 1 });
      Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.WheelDown, Pressed = false, Factor = 1 });
    }
    var fadeSw = Stopwatch.StartNew();
    while (fadeSw.Elapsed.TotalSeconds < 0.3) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    GetViewport().GetTexture().GetImage().SavePng($"{dir}/dig_preview.png");
    GD.Print($"saved {dir}/dig_preview.png (radius {player.Get("DigRadius")})");
    // Game time, not wall time: the PNG save above stalls a frame for a while.
    await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);
    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    bool hidden = !player.GetNode<Node3D>("DigPreview").Visible;
    GD.Print($"preview hidden after fade: {hidden}");
    // A full-strength dig, slowed to a 1 s morph and captured about halfway.
    pivot.Rotation = new Vector3(-0.5f, 0, 0);
    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    if (cm.RaycastTerrain(cam.GlobalPosition, cam.GlobalPosition - cam.GlobalBasis.Z * 1000, out var mhit))
    {
      float oldMorph = cm.MorphSeconds;
      cm.MorphSeconds = 1f;
      GetViewport().GetTexture().GetImage().SavePng($"{dir}/morph_before.png");
      cm.Dig(mhit, 14);
      var msw = Stopwatch.StartNew();
      while (cm.MorphingChunks == 0 && msw.Elapsed.TotalSeconds < 10) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
      await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
      GetViewport().GetTexture().GetImage().SavePng($"{dir}/morph_mid.png");
      await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);
      GetViewport().GetTexture().GetImage().SavePng($"{dir}/morph_after.png");
      GD.Print($"saved {dir}/morph_before/mid/after.png (morphing chunks now {cm.MorphingChunks})");
      cm.MorphSeconds = oldMorph;
    }
    // Building: grow a mound out of the terrain in front of the camera (should be coloured like ground, not black).
    pivot.Rotation = new Vector3(-0.45f, 0, 0);
    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    for (int i = 0; i < 12; i++)
    {
      if (cm.RaycastTerrain(cam.GlobalPosition, cam.GlobalPosition - cam.GlobalBasis.Z * 1000, out var bhit)) cm.Fill(bhit, 10, 0.35f);
      await ToSignal(GetTree().CreateTimer(0.07), SceneTreeTimer.SignalName.Timeout);
    }
    await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);
    GetViewport().GetTexture().GetImage().SavePng($"{dir}/build.png");
    GD.Print($"saved {dir}/build.png");

    // Ship: start one in front of the camera with the ship tool, then add a few blocks and give it a slow spin.
    {
      var builder = scene.GetNode<ShipBuilder>("Ships");
      pivot.Rotation = new Vector3(0.1f, 0, 0);
      await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
      await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
      builder.PlaceDistance = 7f;
      if (builder.Place(cam.GlobalPosition, -cam.GlobalBasis.Z, player.GetRid()))
      {
        Ship? ship = null;
        foreach (var sh in builder.Ships) ship = sh;
        int[,] hull = { { -2, 0 }, { -1, 0 }, { 1, 0 }, { 2, 0 }, { 0, 1 }, { -1, 1 }, { 1, 1 }, { 0, 2 } };
        for (int i = 0; i < hull.GetLength(0); i++) ship!.AddBlock(new Vector3I(hull[i, 0], hull[i, 1], 0));
        ship!.AddBlock(new Vector3I(0, 0, -1));
        ship.AddBlock(new Vector3I(0, 0, 1));
        // Two thrusters at the back (nozzles +Z) and a mining laser at the front (firing -Z).
        ship.AddBlock(new Vector3I(-1, 0, 1), new Block(BlockType.Thruster, Vector3I.Back));
        ship.AddBlock(new Vector3I(1, 0, 1), new Block(BlockType.Thruster, Vector3I.Back));
        ship.AddBlock(new Vector3I(0, 0, -2), new Block(BlockType.Laser, Vector3I.Forward));
        ship.AngularVelocity = new Vector3(0.1f, 0.35f, 0f);
        await ToSignal(GetTree().CreateTimer(1.2), SceneTreeTimer.SignalName.Timeout);
        GetViewport().GetTexture().GetImage().SavePng($"{dir}/ship.png");
        GD.Print($"saved {dir}/ship.png ({ship.BlockCount} blocks)");
        // Climb in: the chase camera view with the piloting HUD.
        ship.AngularVelocity = Vector3.Zero;
        player.EnterShip(ship);
        player.SetPhysicsProcess(true);
        Input.ActionPress("move_forward");
        Input.ActionPress("fire");
        await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout);
        GetViewport().GetTexture().GetImage().SavePng($"{dir}/pilot.png");
        Input.ActionRelease("move_forward");
        Input.ActionRelease("fire");
        player.SetPhysicsProcess(false);
        GD.Print($"saved {dir}/pilot.png");
        player.ExitShip();
      }
      else
      {
        GD.Print("ship screenshot: no room to place a block in front of the camera");
      }
    }
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

  /// <summary>Compares the vertices (and morph offsets) on every shared face of adjacent LOD0 chunks.</summary>
  private static int SeamPairs(List<Chunk> chunks, Dictionary<Chunk, MeshResult> shown, out int bad)
  {
    bad = 0;
    int pairs = 0;
    var byKey = chunks.ToDictionary(c => c.Key);
    foreach (var a in chunks)
    {
      if (a.Key.Lod != 0 || !shown.TryGetValue(a, out var ra) || ra.Arrays == null) continue;
      for (int ax = 0; ax < 3; ax++)
      {
        var off = Vector3I.Zero;
        off[ax] = 1;
        if (!byKey.TryGetValue(new ChunkKey(0, a.Key.Coord + off), out var b)) continue;
        if (!shown.TryGetValue(b, out var rb) || rb.Arrays == null) continue;
        pairs++;
        var fa = FaceVertices(ra, a.Key, ax, VoxelConst.Cells);
        var fb = FaceVertices(rb, b.Key, ax, 0);
        if (!fa.SetEquals(fb)) bad++;
      }
    }
    return pairs;
  }

  private static HashSet<((long, long, long), (long, long, long))> FaceVertices(MeshResult r, ChunkKey key, int axis, float plane)
  {
    var set = new HashSet<((long, long, long), (long, long, long))>();
    var pos = r.Arrays![(int)Mesh.ArrayType.Vertex].AsVector3Array();
    float[]? morph = r.HasMorph ? r.Arrays[(int)Mesh.ArrayType.Custom1].AsFloat32Array() : null;
    var origin = (Vector3)key.Origin;
    for (int i = 0; i < pos.Length; i++)
    {
      if (pos[i][axis] != plane) continue; // shared face vertices sit exactly on the plane
      var x = morph != null ? new Vector3(morph[i * 3], morph[i * 3 + 1], morph[i * 3 + 2]) : Vector3.Zero;
      set.Add((Q(origin + pos[i]), Q(x)));
    }
    return set;
  }

  // ------------------------------------------------------------------ live

  private async System.Threading.Tasks.Task LiveRun(Node scene, ChunkManager cm)
  {
    var player = scene.GetNode<Player>("Player");
    player.SetPhysicsProcess(false); // the harness moves the player around itself
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

    // Sphere-cast aim: the swept sphere targets the first terrain within `radius` of the line of sight. Over a fan
    // of rays: the hit lies inside the tube and on the surface, it is never reached later than the exact ray hit,
    // and some rays catch terrain the bare ray misses or reaches later.
    void SphereCast(float ramp)
    {
      const float radius = 20f;
      float Rad(float along) => ramp > 0 ? radius * Math.Clamp(along / ramp, 0f, 1f) : radius;
      int rays = 0, hitsS = 0, outside = 0, later = 0, offSurface = 0, catches = 0;
      double ms = 0, plainMs = 0;
      // Warm up (tiered JIT), then time the same fan.
      var warm = new Random(7);
      for (int i = 0; i < 40; i++)
      {
        var d = new Vector3((float)warm.NextDouble() * 2 - 1, (float)warm.NextDouble() * 1.6f - 1.1f, (float)warm.NextDouble() * 2 - 1).Normalized();
        cm.SphereCastTerrain(origin, origin + d * 1000, radius, out _, ramp);
      }
      cm.CastChunksScanned = cm.CastPointsScanned = 0;
      var rng = new Random(7);
      for (int i = 0; i < 40; i++)
      {
        var d = new Vector3((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 1.6f - 1.1f, (float)rng.NextDouble() * 2 - 1).Normalized();
        var to = origin + d * 1000;
        rays++;
        var psw = Stopwatch.StartNew();
        bool plain = cm.RaycastTerrain(origin, to, out var ph);
        plainMs += psw.Elapsed.TotalMilliseconds;
        var sw = Stopwatch.StartNew();
        bool sphere = cm.SphereCastTerrain(origin, to, radius, out var sh, ramp);
        ms += sw.Elapsed.TotalMilliseconds;
        if (!sphere)
        {
          if (plain) later++; // the exact hit is always a candidate
          continue;
        }
        hitsS++;
        var v = sh - origin;
        float proj = v.Dot(d);
        float perp = MathF.Sqrt(Math.Max(0, v.LengthSquared() - proj * proj));
        if (perp > Rad(proj) + 2f || proj < -1f) outside++;
        float contact = proj - MathF.Sqrt(Math.Max(0, Rad(proj) * Rad(proj) - perp * perp));
        float pd = plain ? origin.DistanceTo(ph) : 0;
        float plainContact = plain ? pd - Rad(pd) : float.PositiveInfinity;
        if (contact > plainContact + 1.5f) later++;
        if (!plain || contact < plainContact - 1f) catches++;
        float dens = _gen.Density(cm.ToLocal(sh), cm.EditsSnapshot());
        if (Math.Abs(dens - VoxelConst.Iso) > 0.2f)
        {
          offSurface++;
          GD.Print($"    off surface: density {dens:F2} at distance {origin.DistanceTo(sh):F0}, plain hit {(plain ? origin.DistanceTo(ph) : -1):F0}");
        }
      }
      GD.Print($"  sphere cast r={radius} ramp={ramp}: {hitsS}/{rays} hit, {catches} caught terrain beside the ray, {ms / rays:F2} ms per cast (bare ray {plainMs / rays:F2} ms), {cm.CastChunksScanned / rays} chunks / {cm.CastPointsScanned / rays} points scanned per cast");
      Check(hitsS > 0 && outside == 0 && later == 0, $"sphere-cast aim hits inside the tube, never later than the bare ray ({outside} outside, {later} later)");
      Check(offSurface == 0, $"sphere-cast aim lands on the terrain surface ({offSurface} off)");
      Check(catches > 0, "sphere-cast aim catches terrain beside the crosshair");
      Check(ms / rays < 1.5, "sphere cast is cheap enough to run every frame");
    }
    double dig = await WaitIdle($"{hits} digs", 30);
    Check(dig < 30, "digs finish");
    SphereCast(0f); // after the digs have reached the screen (the camera starts inside terrain until they clear it)
    SphereCast(60f); // the Player's default: cone up to 3 radii

    int deeper = 0;
    for (int i = 0; i < dirs.Length; i++)
    {
      if (float.IsInfinity(before[i])) continue;
      bool again = cm.RaycastTerrain(origin, origin + dirs[i].Normalized() * 1000, out var hit2);
      if (!again || origin.DistanceTo(hit2) > before[i] + 1) deeper++;
    }
    Check(deeper == hits, $"digging pushes the terrain back along each ray ({deeper}/{hits})");

    await ShipBuilding();
    await ShipSimulation();
    await FlyRelative();
    await ShipPiloting();
    await ShipImpact();
    await ShipLaser();
    await MapLive();
    await PrefabTool();

    // Building with a keep-clear sphere (what the Player does) grows terrain toward the camera but stops short.
    {
      const float clearance = 3f;
      cm.Dig(origin, 10); // start from open space around the camera
      await WaitIdle("clearing around the camera", 30);
      var sdir = dirs[1].Normalized();
      if (cm.RaycastTerrain(origin, origin + sdir * 1000, out var s0))
      {
        float startDist = origin.DistanceTo(s0);
        float total = 0;
        for (int i = 0; i < 40; i++)
        {
          cm.RaycastTerrain(origin, origin + sdir * 1000, out var aim);
          total += cm.FillKeepingClear(aim, 8, 0.6f, origin, clearance);
          await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        await WaitIdle("building next to the camera", 30);
        var edits = cm.EditsSnapshot();
        float worst = 0;
        foreach (var off in new[] { Vector3.Zero, Vector3.Right, Vector3.Left, Vector3.Up, Vector3.Down, Vector3.Forward, Vector3.Back, sdir })
        {
          worst = Math.Max(worst, _gen.Density(cm.ToLocal(origin + off * clearance * 0.95f), edits));
        }
        bool hitS = cm.RaycastTerrain(origin, origin + sdir * 1000, out var s1);
        float endDist = hitS ? origin.DistanceTo(s1) : float.PositiveInfinity;
        GD.Print($"  safe building: surface {startDist:F2} -> {endDist:F2} (clearance {clearance}), total strength {total:F2}, " +
                 $"densest point inside the clearance {worst:F2}");
        Check(endDist < startDist - 1f, "safe building still builds toward the camera");
        Check(worst < VoxelConst.Iso && endDist > clearance * 0.9f, "building never closes terrain in on the player's clearance");
      }
    }

    // Building at the hit point grows terrain back toward the camera.
    {
      var bdir = dirs[0].Normalized();
      if (cm.RaycastTerrain(origin, origin + bdir * 1000, out var b0))
      {
        float before0 = origin.DistanceTo(b0);
        for (int i = 0; i < 10; i++)
        {
          cm.RaycastTerrain(origin, origin + bdir * 1000, out var aim);
          cm.Fill(aim, 8, 0.5f);
          await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        await WaitIdle("building", 30);
        bool hitB = cm.RaycastTerrain(origin, origin + bdir * 1000, out var b1);
        float after0 = hitB ? origin.DistanceTo(b1) : float.PositiveInfinity;
        GD.Print($"  building along a ray: surface at {before0:F2}, after 10 fills {after0:F2}");
        Check(after0 < before0 - 2f, "building grows terrain toward the camera");
      }
    }

    // Gradual digging: one brush tick (the Player's per-tick strength) barely moves the surface; holding for
    // ~1.5 s of ticks digs well in.
    var gdir = new Vector3(-1, -1, 0.3f).Normalized();
    if (cm.RaycastTerrain(origin, origin + gdir * 1000, out var g0))
    {
      float strength = 1f - Mathf.Exp(-1.5f / 15f);
      float d0 = origin.DistanceTo(g0);
      cm.Dig(g0, 20, strength);
      await WaitIdle("1 brush tick", 30);
      cm.RaycastTerrain(origin, origin + gdir * 1000, out var g1);
      float d1 = origin.DistanceTo(g1);
      for (int i = 0; i < 22; i++)
      {
        cm.RaycastTerrain(origin, origin + gdir * 1000, out var aim);
        cm.Dig(aim, 20, strength);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
      }
      await WaitIdle("22 more brush ticks", 30);
      bool hit2 = cm.RaycastTerrain(origin, origin + gdir * 1000, out var g2);
      float d2 = hit2 ? origin.DistanceTo(g2) : float.PositiveInfinity;
      GD.Print($"  gradual dig along ray: start {d0:F2}, after 1 tick {d1:F2}, after 23 ticks {d2:F2}");
      Check(d1 - d0 < 2f && d2 - d0 > 4f, "brush digging is gradual (small step per tick, steady progress when held)");
    }
    await BatchedDigging();
    await PacedMorph();
    GD.Print($"  worst frame while digging: {worstFrameUs / 1000.0:F1} ms, worst ChunkManager._Process {worstProcessMs:F1} ms (headless)");

    // Held brush on a chunk corner: every touched chunk must switch snapshot in the same frame, and the shown
    // meshes (including their morph offsets) must agree on every shared LOD0 face after each commit.
    // A big brush touches many chunks, so commits come further apart than a fixed-length animation: the surface
    // would move, stop, then jump (pulse). Paced morphs last until about the next commit, so held digging should
    // keep something animating nearly all the time.
    async System.Threading.Tasks.Task PacedMorph()
    {
      var corner = (origin / VoxelConst.Cells).Round() * VoxelConst.Cells + new Vector3(VoxelConst.Cells, 0, 0);
      var top = new Vector3(corner.X, origin.Y, corner.Z);
      if (!cm.RaycastTerrain(top, top + Vector3.Down * 400, out var at)) return;
      float maxMorph = cm.MaxMorphSeconds;
      foreach (bool paced in new[] { false, true })
      {
        cm.MaxMorphSeconds = paced ? maxMorph : 0;
        await WaitIdle("before paced morph run", 30);
        int framesAfterFirst = 0, still = 0, commits0 = cm.BatchesCommitted;
        cm.ResetMorphStats();
        var sw = Stopwatch.StartNew();
        double nextTick = 0, lastDig = 1.5;
        float strength = 1f - Mathf.Exp(-1.5f / 15f);
        while (sw.Elapsed.TotalSeconds < lastDig)
        {
          if (sw.Elapsed.TotalSeconds >= nextTick)
          {
            cm.Dig(at, 45, strength);
            nextTick += 1.0 / 15;
          }
          await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
          if (cm.BatchesCommitted == commits0) continue;
          framesAfterFirst++;
          if (cm.MorphingChunks == 0) still++;
        }
        int batches = cm.BatchesCommitted - commits0;
        double stillPct = 100.0 * still / Math.Max(1, framesAfterFirst);
        GD.Print($"  r=45 held dig, {(paced ? "paced" : "fixed")} morph: {batches} commits in 1.5 s (~{1500.0 / Math.Max(1, batches):F0} ms apart), " +
                 $"last batch {cm.LastBatchSize} chunks, morph {cm.LastMorphSeconds * 1000:F0} ms, surface still in {stillPct:F0}% of frames, " +
                 $"average jump {cm.MorphJumpAverage * 100:F0}% of a step");
        if (paced) Check(stillPct < 15 && cm.MorphJumpAverage < 0.15,
          $"big-brush digging animates continuously instead of pulsing (still {stillPct:F0}% of frames, jumps {cm.MorphJumpAverage * 100:F0}% of a step)");
      }
      cm.MaxMorphSeconds = maxMorph;
      await WaitIdle("paced morph digging", 30);
    }

    // Ship tool in the real scene: start a ship in open space, extend it face by face, break blocks, split one by
    // cutting it in the middle, and break everything until no ships are left. A block can't go where the player is.
    // Ship simulation radius: terrain collision is built around requested boxes (what ships ask for), and a ship
    // pauses with its velocity saved when the player goes far away, then carries on with it when they come back.
    // Piloting: climb into a small ship, thrust, brake, steer with the mouse, climb out.
    // A ship fired head-on at the terrain digs a crater, slows down, doesn't bounce and ploughs in past where the
    // surface was.
    // A ship with a mining laser pointed at terrain: holding left click while piloting digs where the beam hits.
    async System.Threading.Tasks.Task ShipLaser()
    {
      var builder = scene.GetNode<ShipBuilder>("Ships");
      var rid = player.GetRid();
      Vector3 dir = Vector3.Zero;
      float before = 0;
      foreach (var d in new[] { Vector3.Down, Vector3.Forward, Vector3.Back, Vector3.Left, Vector3.Right, Vector3.Up })
      {
        if (!cm.RaycastTerrain(origin, origin + d * 60f, out var hit) || origin.DistanceTo(hit) < 7f) continue;
        var t = builder.Aim(origin, d, rid);
        if (t.Ship == null && t.CanPlace)
        {
          dir = d;
          before = origin.DistanceTo(hit);
          break;
        }
      }
      if (dir == Vector3.Zero)
      {
        GD.Print("  ship laser: no terrain along an axis with room for a ship, skipped");
        return;
      }
      // A new free-floating laser block points along the view: straight at the terrain.
      builder.Place(origin, dir, rid, BlockType.Laser);
      var ship = builder.Ships.Last();
      bool facing = ship.Lasers.Count == 1 && ship.Lasers[0].Facing == (Vector3I)dir.Round();
      for (int i = 0; i < 60 && ship.Paused; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
      int edits0 = cm.EditCount + cm.EditsMerged;
      player.SetPhysicsProcess(true);
      player.EnterShip(ship);
      await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
      Input.ActionPress("fire");
      bool beamHit = false;
      for (int i = 0; i < 60; i++)
      {
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        beamHit |= player.Pilot.Beams.Count == 1 && player.Pilot.Beams[0].Hit;
      }
      Input.ActionRelease("fire");
      player.ExitShip();
      for (int i = 0; i < 4; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
      player.SetPhysicsProcess(false);
      await WaitIdle("laser digging", 30);
      int digs = cm.EditCount + cm.EditsMerged - edits0;
      cm.RaycastTerrain(origin, origin + dir * 80f, out var after);
      float deeper = origin.DistanceTo(after) - before;
      GD.Print($"  ship laser: facing {facing}, beam hit {beamHit}, {digs} digs in 1 s, terrain along the beam {deeper:F2} further away");
      Check(facing, "a laser placed as a new block points along the view");
      Check(beamHit && digs > 5 && deeper > 0.5f, "holding left click while piloting fires the laser and it digs where it hits");
      ship.GetParent()?.RemoveChild(ship);
      ship.QueueFree();
    }

    // The prefab tool: save a ship under a name, spawn it back with and without the follower AI.
    async System.Threading.Tasks.Task PrefabTool()
    {
      const string dir = "user://test_ships_live";
      DeleteUserDir(dir);
      var builder = scene.GetNode<ShipBuilder>("Ships");
      var map = scene.GetNode<TerrainMap>("Map");
      var oldLibrary = player.Library;
      var oldBasis = player.GlobalBasis;
      player.Library = new ShipLibrary(dir);
      player.RefreshPrefabs();
      Input.ParseInputEvent(new InputEventAction { Action = "tool_prefab", Pressed = true });
      Input.ParseInputEvent(new InputEventAction { Action = "tool_prefab", Pressed = false });
      for (int i = 0; i < 3; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
      Check(player.CurrentTool == Player.Tool.Prefab && player.Hotbar.SelectedSlot == 2 && player.Hotbar.Title == "Prefab"
            && player.PrefabName == ShipLibrary.BuiltInName,
            "key 3 selects the prefab tool (third hotbar slot, starter miner selected)");

      var small = new List<(Vector3I, Block)>
      {
        (Vector3I.Zero, Block.Hull), (new Vector3I(0, 0, 1), new Block(BlockType.Thruster, Vector3I.Back)),
        (new Vector3I(0, 0, -1), new Block(BlockType.Laser, Vector3I.Forward)), (new Vector3I(1, 0, 0), Block.Hull),
      };
      var src = builder.SpawnShip(small, new Transform3D(Basis.Identity, origin + new Vector3(0, 30, 0)));
      player.SaveShipPrompt(src);
      await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
      bool open = player.SavePopupOpen;
      // While typing: movement keys don't move the player, M doesn't open the map.
      player.SetPhysicsProcess(true);
      var before = player.GlobalPosition;
      Input.ActionPress("move_forward");
      for (int i = 0; i < 10; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
      Input.ActionRelease("move_forward");
      Input.ParseInputEvent(new InputEventAction { Action = "map", Pressed = true });
      Input.ParseInputEvent(new InputEventAction { Action = "map", Pressed = false });
      // Parsed events reach the nodes a frame or two later.
      for (int i = 0; i < 3; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
      bool still = player.GlobalPosition.IsEqualApprox(before) && !map.IsOpen && player.SavePopupOpen;
      player.SetPhysicsProcess(false);
      Check(open && still, "left click on a ship opens the name box, and typing doesn't move the player or open the map");

      bool saved = player.ConfirmSave("live test");
      bool file = System.IO.File.Exists(player.Library.PathFor("live test"));
      // process_frame fires before the nodes' _Process, so the HUD has caught up after two.
      for (int i = 0; i < 2; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
      Check(saved && file && player.PrefabName == "live test" && !player.SavePopupOpen && player.Hotbar.Caption(2) == "live test",
            "Enter saves the ship to the user folder and selects it");

      // R cycles the AI job: off, follow, dig, off.
      var jobs = new List<AIJob?>();
      for (int i = 0; i < 4; i++)
      {
        Input.ParseInputEvent(new InputEventAction { Action = "prefab_ai", Pressed = true });
        Input.ParseInputEvent(new InputEventAction { Action = "prefab_ai", Pressed = false });
        for (int f = 0; f < 3; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        jobs.Add(player.PrefabJob);
      }
      GD.Print($"  R presses: {string.Join(", ", jobs.Select(j => j?.ToString() ?? "off"))} (tool {player.CurrentTool})");
      Check(jobs[0] == AIJob.Follow && jobs[1] == AIJob.Dig && jobs[2] == null && jobs[3] == AIJob.Follow,
            "R cycles the spawned ship's AI: follow, dig, off");
      player.PrefabJob = null;

      // Spawn it: face a few directions until the ghost fits.
      var dirs = new[] { Vector3.Up, Vector3.Forward, Vector3.Back, Vector3.Left, Vector3.Right, Vector3.Down };
      int next = 0;
      async System.Threading.Tasks.Task<Ship?> TryPlace()
      {
        for (int k = 0; k < dirs.Length; k++)
        {
          var d = dirs[(next + k) % dirs.Length];
          player.GlobalBasis = Basis.LookingAt(d, Mathf.Abs(d.Y) > 0.9f ? Vector3.Back : Vector3.Up);
          await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
          var s = player.PlacePrefab();
          if (s != null)
          {
            next += k + 1;
            return s;
          }
        }
        return null;
      }
      player.PrefabJob = null;
      var plain = await TryPlace();
      player.PrefabJob = AIJob.Follow;
      for (int i = 0; i < 2; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
      bool aiCaption = player.Hotbar.Caption(2).EndsWith("+AI");
      var flown = await TryPlace();
      GD.Print($"  prefab tool: plain {plain?.BlockCount} blocks (AI {plain?.AI != null}), AI-flown {flown?.BlockCount} blocks (AI {flown?.AI != null})");
      Check(plain != null && plain.BlockCount == small.Count && plain.AI == null, "right click spawns the saved ship where the ghost was, without AI");
      Check(flown != null && flown.BlockCount == small.Count && flown.AI != null && aiCaption, "with R (AI on) it spawns flown by the follower AI");

      foreach (var s in new[] { src, plain, flown })
        if (s != null && IsInstanceValid(s))
        {
          builder.RemoveChild(s);
          s.QueueFree();
        }
      player.GlobalBasis = oldBasis;
      player.PrefabJob = null;
      player.Library = oldLibrary;
      player.RefreshPrefabs();
      player.SelectTool(Player.Tool.Terrain);
      DeleteUserDir(dir);
    }

    // The map: M opens it, the mouse orbits it instead of turning the player, clicks don't dig, digs show up, M closes.
    async System.Threading.Tasks.Task MapLive()
    {
      var map = scene.GetNode<TerrainMap>("Map");
      void Toggle()
      {
        Input.ParseInputEvent(new InputEventAction { Action = "map", Pressed = true });
        Input.ParseInputEvent(new InputEventAction { Action = "map", Pressed = false });
      }
      var sw = Stopwatch.StartNew();
      Toggle();
      await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
      bool opened = map.IsOpen && map.Viewport.RenderTargetUpdateMode == SubViewport.UpdateMode.Always;
      while (sw.Elapsed.TotalSeconds < 20 && (map.Version == 0 || map.Building)) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
      GD.Print($"  map: open {opened}, version {map.Version}, {map.ChunkMeshes} chunk meshes, last build {map.LastBuildMs:F0} ms");
      Check(opened && map.Version > 0 && map.ChunkMeshes > 0, "M opens the map and it shows low-poly terrain chunks");

      // Mouse: orbits the map, the player doesn't turn.
      var pivotNode = player.GetNode<Node3D>("CameraPivot");
      var basisBefore = pivotNode.GlobalBasis;
      float yawBefore = map.Yaw;
      Input.MouseMode = Input.MouseModeEnum.Captured;
      for (int i = 0; i < 5; i++)
      {
        Input.ParseInputEvent(new InputEventMouseMotion { Relative = new Vector2(40, 10) });
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
      }
      bool orbited = Mathf.Abs(map.Yaw - yawBefore) > 0.1f;
      bool playerStill = pivotNode.GlobalBasis.IsEqualApprox(basisBefore);
      Check(orbited && playerStill, "with the map open the mouse orbits the map, not the player's view");

      // Clicks don't dig.
      player.SelectTool(Player.Tool.Terrain);
      int edits = cm.EditCount + cm.EditsMerged;
      Input.ActionPress("fire");
      for (int i = 0; i < 20; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
      Input.ActionRelease("fire");
      Check(cm.EditCount + cm.EditsMerged == edits, "holding left click with the map open doesn't dig");

      // A dig near the player shows up: the open map rebuilds.
      int version = map.Version;
      bool dug = false;
      foreach (var d in new[] { Vector3.Down, Vector3.Forward, Vector3.Left, Vector3.Back, Vector3.Right, Vector3.Up })
        if (cm.RaycastTerrain(origin, origin + d * 200f, out var hit))
        {
          cm.Dig(hit, 12f);
          dug = true;
          break;
        }
      sw.Restart();
      while (sw.Elapsed.TotalSeconds < 15 && map.Version == version) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
      GD.Print($"  map: rebuilt after a dig in {sw.Elapsed.TotalSeconds:F2} s ({map.LastBuildMs:F0} ms build)");
      Check(dug && map.Version > version, "the open map rebuilds to show a dig");

      Toggle();
      await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
      Check(!map.IsOpen && map.Viewport.RenderTargetUpdateMode == SubViewport.UpdateMode.Disabled, "M again closes the map and it stops rendering");
      await WaitIdle("map dig", 30);
    }

    async System.Threading.Tasks.Task ShipImpact()
    {
      var builder = scene.GetNode<ShipBuilder>("Ships");
      var rid = player.GetRid();
      // Terrain 7..40 units away in some direction, with open space in front of it for the ship.
      Vector3 dir = Vector3.Zero, surface = Vector3.Zero;
      for (int i = 0; i < 26 && dir == Vector3.Zero; i++)
      {
        var d = new Vector3((i % 3) - 1, (i / 3 % 3) - 1, (i / 9 % 3) - 1);
        if (d == Vector3.Zero) continue;
        d = d.Normalized();
        if (!cm.RaycastTerrain(origin, origin + d * 40f, out var hit) || origin.DistanceTo(hit) < 7f) continue;
        var t = builder.Aim(origin, d, rid);
        if (t.Ship == null && t.CanPlace)
        {
          dir = d;
          surface = hit;
        }
      }
      if (dir == Vector3.Zero)
      {
        GD.Print("  ship impact: no terrain at the right distance with room in front, skipped");
        return;
      }
      builder.Place(origin, dir, rid);
      var ship = builder.Ships.Last();
      for (int i = 0; i < 60 && ship.Paused; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
      int craters0 = builder.CratersDug;
      int edits0 = cm.EditCount;
      const float launch = 20f;
      ship.LinearVelocity = dir * launch;
      float surfaceDist = origin.DistanceTo(surface);
      float worstBack = 0, deepest = 0;
      for (int i = 0; i < 240; i++)
      {
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        worstBack = Math.Min(worstBack, ship.EffectiveLinearVelocity.Dot(dir));
        deepest = Math.Max(deepest, (ship.GlobalTransform * ship.CenterOfMass - origin).Dot(dir));
      }
      float speedAfter = ship.EffectiveLinearVelocity.Length();
      int craters = builder.CratersDug - craters0;
      GD.Print($"  ship impact: hit terrain {surfaceDist:F1} away at {launch} u/s -> {craters} crater(s), first/last radius " +
               $"{builder.LastCraterRadius:F2}, {cm.EditCount - edits0} edits, ploughed to {deepest:F1} along the path, " +
               $"speed now {speedAfter:F2}, worst velocity back out {worstBack:F2}");
      Check(craters > 0 && cm.EditCount > edits0, "a ship hitting terrain digs a crater");
      Check(speedAfter < launch * 0.8f, "the crater's energy comes out of the ship (it slows down)");
      Check(worstBack > -1f, "the ship doesn't bounce off the terrain");
      Check(deepest > surfaceDist - 0.5f, "the ship ploughs into the terrain past where its surface was");
      ship.GetParent()?.RemoveChild(ship);
      ship.QueueFree();
    }

    // Flying on foot is relative to the view: pointed straight up, W flies up; D strafes along our right; Q rolls
    // about the view axis.
    async System.Threading.Tasks.Task FlyRelative()
    {
      var pivotNode = player.GetNode<Node3D>("CameraPivot");
      var oldPivot = pivotNode.Rotation;
      var oldBasis = player.GlobalBasis;
      float oldSpeed = player.Speed;
      pivotNode.Rotation = Vector3.Zero;
      player.Speed = 2f;
      player.GlobalBasis = new Basis(Vector3.Right, Mathf.Pi / 2f); // looking straight up
      player.SetPhysicsProcess(true);
      async System.Threading.Tasks.Task<Vector3> Hold(string action, int frames)
      {
        Input.ActionPress(action);
        for (int i = 0; i < frames; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        var v = player.Velocity;
        Input.ActionRelease(action);
        return v;
      }
      var forward = await Hold("move_forward", 5);
      var lookUp = -player.GlobalBasis.Z;
      var strafe = await Hold("move_right", 5);
      var right = player.GlobalBasis.X;
      var fwd0 = -player.GlobalBasis.Z;
      var up0 = player.GlobalBasis.Y;
      await Hold("roll_left", 30);
      var fwd1 = -player.GlobalBasis.Z;
      float rolled = up0.AngleTo(player.GlobalBasis.Y);
      player.SetPhysicsProcess(false);
      player.GlobalBasis = oldBasis;
      pivotNode.Rotation = oldPivot;
      player.Speed = oldSpeed;
      GD.Print($"  fly relative: looking up, W velocity {forward} (view {lookUp}); D velocity {strafe} (right {right}); " +
               $"Q held 0.5 s rolled {Mathf.RadToDeg(rolled):F1} deg, view moved {Mathf.RadToDeg(fwd0.AngleTo(fwd1)):F2} deg");
      Check(forward.Length() > 1.9f && forward.Normalized().Dot(lookUp) > 0.99f && lookUp.Y > 0.99f,
        "W flies along the view, even straight up");
      Check(strafe.Length() > 1.9f && strafe.Normalized().Dot(right) > 0.99f, "A/D strafe along the view's right");
      Check(rolled > 0.6f && rolled < 0.9f && fwd0.AngleTo(fwd1) < 0.01f, "Q / E roll about the view axis");
    }

    async System.Threading.Tasks.Task ShipPiloting()
    {
      var builder = scene.GetNode<ShipBuilder>("Ships");
      var rid = player.GetRid();
      Vector3 aim = Vector3.Zero;
      foreach (var d in new[] { Vector3.Up, Vector3.Forward, Vector3.Back, Vector3.Left, Vector3.Right, Vector3.Down })
      {
        var t = builder.Aim(origin, d, rid);
        if (t.Ship == null && t.CanPlace && t.PlaceTransform.Origin.DistanceTo(origin) > builder.PlaceDistance - 0.01f)
        {
          aim = d;
          break;
        }
      }
      if (aim == Vector3.Zero || !builder.Place(origin, aim, rid))
      {
        Check(false, "piloting: room for a ship next to the camera");
        return;
      }
      var ship = builder.Ships.Last();
      // A 2x2 slab across the aim direction (4 blocks, mass 4).
      var side = Mathf.Abs(aim.Y) > 0.5f ? Vector3I.Right : Vector3I.Up;
      var side2 = (Vector3I)((Vector3)side).Cross(aim).Round();
      ship.AddBlock(side);
      ship.AddBlock(side2);
      ship.AddBlock(side + side2);
      for (int i = 0; i < 60 && ship.Paused; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

      async System.Threading.Tasks.Task Physics(int frames)
      {
        for (int i = 0; i < frames; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
      }

      player.SetPhysicsProcess(true);
      player.EnterShip(ship);
      await Physics(2);
      bool entered = player.Piloting && GetViewport().GetCamera3D() == player.Pilot.Camera
                     && player.GetNode<CollisionShape3D>("CollisionShape3D").Disabled;

      // No thrusters: the ship can't translate.
      string key = aim == Vector3.Forward ? "move_forward" : aim == Vector3.Back ? "move_back" : aim == Vector3.Left ? "move_left"
                 : aim == Vector3.Right ? "move_right" : aim == Vector3.Up ? "jump" : "pilot_down";
      Input.ActionPress(key);
      await Physics(12);
      Input.ActionRelease(key);
      float drift = ship.LinearVelocity.Length();
      Check(drift < 0.01f, "a ship without thrusters can't translate");

      // Add a thruster on the side facing the camera (nozzle back toward it, so it pushes along aim) and one facing
      // along aim (pushes back, for braking).
      var aimI = (Vector3I)aim.Round();
      ship.AddBlock(-aimI, new Block(BlockType.Thruster, -aimI));
      ship.AddBlock(side2 * 2, new Block(BlockType.Thruster, aimI));
      await Physics(2);

      // Thrust toward the open space for 0.3 s: one thruster, a = F / m.
      float expected = player.Pilot.ThrusterForce / ship.Mass * 0.3f;
      Input.ActionPress(key);
      await Physics(18);
      Input.ActionRelease(key);
      float speed = ship.LinearVelocity.Dot(aim);

      // Brake until stopped.
      Input.ActionPress("crouch");
      for (int i = 0; i < 180 && ship.LinearVelocity.Length() > 0.02f; i++) await Physics(1);
      Input.ActionRelease("crouch");
      float braked = ship.LinearVelocity.Length();

      // Steer: mouse to the right yaws the ship; once the mouse stops, the gyro holds it steady.
      var fwd0 = -ship.GlobalBasis.Z;
      for (int i = 0; i < 20; i++)
      {
        player.Pilot.AddMouse(new Vector2(8, 0));
        await Physics(1);
      }
      float turned = fwd0.AngleTo(-ship.GlobalBasis.Z);
      await Physics(45);
      float spinLeft = ship.AngularVelocity.Length();

      var shipBefore = ship.GlobalPosition;
      player.ExitShip();
      await Physics(6);
      float shoved = ship.GlobalPosition.DistanceTo(shipBefore);
      bool exited = !player.Piloting && GetViewport().GetCamera3D() == player.GetNode<Camera3D>("CameraPivot/Camera3D")
                    && !player.GetNode<CollisionShape3D>("CollisionShape3D").Disabled
                    && !ship.WorldBounds().Grow(0.4f).HasPoint(player.GlobalPosition) && shoved < 0.05f;
      player.SetPhysicsProcess(false);
      GD.Print($"  piloting: entered {entered}; thrust 0.3 s -> {speed:F2} u/s (expected {expected:F2}); braked to {braked:F3} u/s; " +
               $"mouse yaw turned {Mathf.RadToDeg(turned):F1} deg, spin left after letting go {spinLeft:F3} rad/s; exited {exited} (ship moved {shoved:F3} on exit)");
      Check(entered, "E on a ship climbs in: chase camera on, the player's own collision off");
      Check(speed > expected * 0.7f && speed < expected * 1.2f, "a thruster block pushes the ship at force / mass");
      Check(braked < 0.05f, "Ctrl brakes the ship to a stop with the thrusters that can push against its velocity");
      Check(turned > 0.1f && spinLeft < 0.05f, "the mouse steers the ship and the gyro holds it steady afterwards");
      Check(exited, "E again climbs out next to the ship with the normal camera and collision back, without shoving the ship");
      ship.GetParent()?.RemoveChild(ship);
      ship.QueueFree();
    }

    async System.Threading.Tasks.Task ShipSimulation()
    {
      var builder = scene.GetNode<ShipBuilder>("Ships");
      var rid = player.GetRid();

      // 1. Collision follows requests. Shrink the player's own collision area to its chunk, then request a box on
      //    terrain in another LOD0 chunk.
      int oldRadius = cm.CollisionRadius;
      float oldKeep = cm.CollisionKeepSeconds;
      cm.CollisionRadius = 0;
      cm.CollisionKeepSeconds = 0.5f;
      var playerKey = ChunkKey.Containing(cm.ToLocal(origin), 0);
      Aabb? target = null;
      Chunk? targetChunk = null;
      for (int i = 0; i < 26 && target == null; i++)
      {
        var d = new Vector3((i % 3) - 1, (i / 3 % 3) - 1, (i / 9 % 3) - 1);
        if (d == Vector3.Zero) continue;
        if (!cm.RaycastTerrain(origin, origin + d.Normalized() * 60f, out var hit)) continue;
        var key = ChunkKey.Containing(cm.ToLocal(hit), 0);
        if (key == playerKey || !cm.TryGetChunk(key, out var c) || c.MeshInstance == null) continue;
        target = new Aabb(hit - Vector3.One, Vector3.One * 2f);
        targetChunk = c;
      }
      if (target is Aabb box && targetChunk != null)
      {
        // With the player's area shrunk, that chunk's existing shape goes first.
        for (int i = 0; i < 120 && targetChunk.HasCollision; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        bool before = targetChunk.HasCollision;
        var sw = Stopwatch.StartNew();
        bool ready = false;
        while (sw.Elapsed.TotalSeconds < 10 && !ready)
        {
          cm.RequestCollision(box);
          await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
          ready = cm.CollisionReadyFor(box) && targetChunk.HasCollision;
        }
        double readyAfter = sw.Elapsed.TotalSeconds;
        // Stop asking: the shape goes once the keep time is over.
        sw.Restart();
        while (sw.Elapsed.TotalSeconds < 3 && targetChunk.HasCollision) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        bool dropped = !targetChunk.HasCollision;
        GD.Print($"  ship collision requests: chunk {targetChunk.Key} had collision {before}, ready after {readyAfter:F2} s, dropped after the keep time {dropped}");
        Check(!before && ready, "terrain collision is built around a requested box away from the player");
        Check(dropped, "requested terrain collision is dropped once nothing asks for it");
      }
      else
      {
        Check(false, "ship collision requests: found terrain in another chunk near the camera");
      }
      cm.CollisionRadius = oldRadius;
      cm.CollisionKeepSeconds = oldKeep;

      // 2. Pause and resume. A ship drifting slowly through open space next to the camera...
      Vector3 aim = Vector3.Zero;
      foreach (var d in new[] { Vector3.Up, Vector3.Forward, Vector3.Back, Vector3.Left, Vector3.Right, Vector3.Down })
      {
        var t = builder.Aim(origin, d, rid);
        if (t.Ship == null && t.CanPlace && t.PlaceTransform.Origin.DistanceTo(origin) > builder.PlaceDistance - 0.01f)
        {
          aim = d;
          break;
        }
      }
      if (aim == Vector3.Zero || !builder.Place(origin, aim, rid))
      {
        Check(false, "ship pause/resume: room for a ship next to the camera");
        return;
      }
      Ship ship = builder.Ships.Last();
      var drift = aim * 0.3f;
      for (int i = 0; i < 30 && ship.Paused; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
      ship.LinearVelocity = drift;
      var p0 = ship.GlobalPosition;
      for (int i = 0; i < 30; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
      bool movedNear = !ship.Paused && ship.GlobalPosition.DistanceTo(p0) > 0.05f;

      // ...the player moves off a way, so only coarse chunks cover the ship: still loaded, so it keeps simulating
      // (colliding with the coarse surface) and is never hidden...
      var home = player.GlobalPosition;
      player.GlobalPosition = home + new Vector3(0, 0, 150);
      bool everUnloaded = false;
      int coarseLod = 0;
      for (int i = 0; i < 120; i++)
      {
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        everUnloaded |= ship.Unloaded || !ship.Visible;
      }
      for (int lod = 0; lod <= cm.MaxLod; lod++)
        if (cm.TryGetChunk(ChunkKey.Containing(cm.ToLocal(ship.GlobalPosition), lod), out _)) coarseLod = lod;
      var pMid = ship.GlobalPosition;
      for (int i = 0; i < 30; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
      bool movingMid = !ship.Paused && ship.GlobalPosition.DistanceTo(pMid) > 0.05f;
      GD.Print($"  ship among coarse chunks: covered by LOD {coarseLod}, ever unloaded {everUnloaded}, still moving {movingMid}");
      Check(coarseLod > 0 && !everUnloaded && movingMid, "a ship in a loaded coarse chunk keeps simulating and stays visible");

      // ...the player leaves the area entirely (no chunk covers the ship): it unloads in place and keeps its velocity...
      player.GlobalPosition = home + new Vector3(0, 0, 2000);
      for (int i = 0; i < 300 && !ship.Unloaded; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
      bool paused = ship.Paused;
      bool hidden = !ship.Visible && ship.ProcessMode == Node.ProcessModeEnum.Disabled;
      var held = ship.GlobalPosition;
      var saved = ship.EffectiveLinearVelocity;
      for (int i = 0; i < 60; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
      bool stayed = ship.GlobalPosition.DistanceTo(held) < 1e-4f;

      // ...and the player comes back: once the terrain around it has collision, it carries on at the same speed.
      player.GlobalPosition = home;
      var sw2 = Stopwatch.StartNew();
      while (sw2.Elapsed.TotalSeconds < 15 && ship.Paused) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
      bool resumed = !ship.Paused;
      bool shown = ship.Visible && ship.ProcessMode == Node.ProcessModeEnum.Inherit;
      var resumedVelocity = ship.LinearVelocity;
      var p1 = ship.GlobalPosition;
      for (int i = 0; i < 30; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
      bool movingAgain = ship.GlobalPosition.DistanceTo(p1) > 0.05f;
      GD.Print($"  ship pause/resume: drifting near {movedNear}; far away paused {paused}, stayed put {stayed}, saved velocity {saved}; " +
               $"back: resumed {resumed} after {sw2.Elapsed.TotalSeconds:F2} s with {resumedVelocity}, moving again {movingAgain}");
      Check(movedNear, "a ship near the player simulates");
      Check(paused && stayed && saved.DistanceTo(drift) < 0.02f, "a ship outside the loaded chunks pauses in place and keeps its velocity");
      Check(hidden, "a paused ship is hidden and out of the physics world");
      Check(resumed && shown && resumedVelocity.DistanceTo(saved) < 0.02f && movingAgain,
        "a paused ship reappears and resumes with the same velocity when the player returns");

      // The ship being flown never pauses, even teleported far outside the loaded chunks (into open space).
      var far = Vector3.Zero;
      for (int i = 1; i < 40 && far == Vector3.Zero; i++)
      {
        var p = home + new Vector3(1500, 37 * i, -700);
        bool open = true;
        for (int k = 0; k < 27 && open; k++)
        {
          var q = cm.ToLocal(p + new Vector3((k % 3) - 1, (k / 3 % 3) - 1, (k / 9) - 1) * 6f);
          open = _gen.RawDensity(q.X, q.Y, q.Z) < VoxelConst.Iso;
        }
        if (open) far = p;
      }
      if (far != Vector3.Zero)
      {
        player.SetPhysicsProcess(true);
        player.EnterShip(ship);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        ship.GlobalPosition = far;
        ship.LinearVelocity = Vector3.Zero;
        bool everPaused = false;
        for (int i = 0; i < 60; i++)
        {
          await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
          everPaused |= ship.Paused || !ship.Visible;
        }
        bool loadedNow = cm.Loaded(ship.GlobalPosition);
        player.ExitShip();
        player.GlobalPosition = home;
        for (int i = 0; i < 4; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        player.SetPhysicsProcess(false);
        GD.Print($"  piloted ship teleported out of the loaded area: ever paused {everPaused}, terrain loaded around it after 1 s {loadedNow}");
        Check(!everPaused, "the ship being flown never pauses or unloads");
      }
      ship.GetParent()?.RemoveChild(ship);
      ship.QueueFree();
      await WaitIdle("back home", 60);
    }

    async System.Threading.Tasks.Task ShipBuilding()
    {
      var builder = scene.GetNode<ShipBuilder>("Ships");
      var rid = player.GetRid();
      Ship? Only()
      {
        Ship? only = null;
        foreach (var sh in builder.Ships) only = sh;
        return only;
      }
      async System.Threading.Tasks.Task Tick()
      {
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
      }

      // A direction with open space in front of the camera.
      Vector3 aim = Vector3.Zero;
      foreach (var d in new[] { Vector3.Up, Vector3.Forward, Vector3.Back, Vector3.Left, Vector3.Right, Vector3.Down })
      {
        var t = builder.Aim(origin, d, rid);
        if (t.Ship == null && t.CanPlace && t.PlaceTransform.Origin.DistanceTo(origin) > builder.PlaceDistance - 0.01f)
        {
          aim = d;
          break;
        }
      }
      if (aim == Vector3.Zero)
      {
        Check(false, "ship building: found open space next to the camera");
        return;
      }

      bool placedNew = builder.Place(origin, aim, rid);
      await Tick();
      var ship = Only();
      int newShips = builder.ShipCount;
      // Aim at the ship's nearest block each time: the new block goes on the face toward the camera.
      Vector3 Nearest(Ship sh)
      {
        Vector3I best = default;
        float bestD = float.MaxValue;
        foreach (var c in sh.Grid.Cells)
        {
          float dd = sh.CellTransform(c).Origin.DistanceTo(origin);
          if (dd < bestD)
          {
            bestD = dd;
            best = c;
          }
        }
        return sh.CellTransform(best).Origin;
      }
      bool grew = false;
      if (ship != null)
      {
        builder.Place(origin, (Nearest(ship) - origin).Normalized(), rid);
        await Tick();
        builder.Place(origin, (Nearest(ship) - origin).Normalized(), rid);
        await Tick();
        grew = builder.ShipCount == 1 && ship.BlockCount == 3;
      }
      GD.Print($"  ships: new ship {placedNew} ({newShips} ship), after two face placements {builder.ShipCount} ship(s), {ship?.BlockCount ?? 0} blocks");
      Check(placedNew && newShips == 1, "ship tool starts a new ship in open space");
      Check(grew, "placing on a ship's face extends that ship (still one ship)");
      if (ship == null || !grew) return;

      // Break the nearest block, then put it back.
      bool broke = builder.Break(origin, (Nearest(ship) - origin).Normalized(), rid);
      await Tick();
      bool brokeOk = broke && builder.ShipCount == 1 && ship.BlockCount == 2;
      builder.Place(origin, (Nearest(ship) - origin).Normalized(), rid);
      await Tick();

      // Cut the line in the middle: it must split into two one-block ships.
      var cells = new List<Vector3I>(ship.Grid.Cells);
      var middle = cells.OrderBy(c => c.X + c.Y + c.Z).ElementAt(1);
      ship.RemoveBlock(middle);
      builder.SplitDisconnected(ship);
      await Tick();
      var sizes = builder.Ships.Select(sh => sh.BlockCount).OrderBy(n => n).ToList();
      bool split = sizes.SequenceEqual(new[] { 1, 1 });

      // Break everything that's left (nearest first): no ships remain and their nodes go away.
      var shipsBefore = builder.Ships.ToList();
      for (int i = 0; i < 4 && builder.ShipCount > 0; i++)
      {
        var target = builder.Ships.OrderBy(sh => Nearest(sh).DistanceTo(origin)).First();
        builder.Break(origin, (Nearest(target) - origin).Normalized(), rid);
        await Tick();
      }
      await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
      bool gone = builder.ShipCount == 0 && shipsBefore.All(sh => !IsInstanceValid(sh));
      GD.Print($"  ships: break -> {ship.BlockCount} blocks, split sizes [{string.Join(", ", sizes)}], left after breaking all {builder.ShipCount}");
      Check(brokeOk, "breaking a block removes it from its ship");
      Check(split, "cutting a ship in two makes two ships");
      Check(gone, "breaking a ship's last block deletes the ship");

      // No placing a block on top of the player.
      var spot = builder.Aim(origin, aim, rid).PlaceTransform.Origin;
      var home = player.GlobalPosition;
      player.GlobalPosition = spot;
      await Tick();
      bool refused = !builder.Place(origin, aim, rid);
      player.GlobalPosition = home;
      await Tick();
      Check(refused && builder.ShipCount == 0, "a block can't be placed where the player is");
    }

    async System.Threading.Tasks.Task BatchedDigging()
    {
      // The chunk corner nearest the camera, so the touched chunks are LOD0.
      var corner = (origin / VoxelConst.Cells).Round() * VoxelConst.Cells;
      var top = new Vector3(corner.X, origin.Y, corner.Z);
      if (!cm.RaycastTerrain(top, top + Vector3.Down * 400, out var at))
      {
        Check(false, "batched digging: found terrain at a chunk corner");
        return;
      }
      var shown = new Dictionary<Chunk, MeshResult>();
      cm.MeshApplied = (c, r) => shown[c] = r;
      var touched = new List<Chunk>();
      cm.ChunksTouching(at, 20, touched);
      var last = touched.ToDictionary(c => c, c => c.ShownSeq);
      int frames = 0, commits = 0, partial = 0, mixed = 0, seamBad = 0, seamPairs = 0, maxMorphing = 0, commitsBefore = cm.BatchesCommitted;
      float strength = 1f - Mathf.Exp(-1.5f / 15f);
      for (int i = 0; i < 45; i++)
      {
        if (i % 2 == 0 && i < 30) cm.Dig(at, 20, strength);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        frames++;
        maxMorphing = Math.Max(maxMorphing, cm.MorphingChunks);
        var live = touched.Where(c => !c.Removed && c.Ready).ToList();
        int changed = live.Count(c => c.ShownSeq != last[c]);
        if (changed > 0) commits++;
        if (changed > 0 && changed < live.Count) partial++;
        // Before the first commit they still show different snapshots from earlier, unrelated digs.
        if (commits > 0 && live.Select(c => c.ShownSeq).Distinct().Count() > 1) mixed++;
        foreach (var c in live) last[c] = c.ShownSeq;
        if (changed > 0) (seamPairs, seamBad) = (seamPairs + SeamPairs(live, shown, out int bad), seamBad + bad);
      }
      cm.MeshApplied = null;
      GD.Print($"  batched dig on corner {corner}: {touched.Count} chunks, {commits} commit frames over {frames} frames " +
               $"({cm.BatchesCommitted - commitsBefore} batches, last {cm.LastBatchSize} chunks in {cm.LastBatchMs:F0} ms), " +
               $"up to {maxMorphing} chunks morphing, {seamPairs} face pairs checked");
      Check(commits > 0 && partial == 0 && mixed == 0, $"edited chunks show each dig together ({partial} partial frames, {mixed} with mixed snapshots)");
      Check(seamPairs > 0 && seamBad == 0, $"shown LOD0 faces match during held digging, morph offsets included ({seamBad}/{seamPairs} differ)");
      Check(maxMorphing > 0, "edited chunks animate from their previous surface");
      await WaitIdle("batched corner digging", 30);
    }

    worstFrameUs = 0;
    worstProcessMs = 0;
    worstSwapMs = 0;
    int swapErrors = 0;
    double worstSwapCheckMs = 0;
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
      swapErrors += cm.SwapBookkeepingErrors();
      worstSwapCheckMs = Math.Max(worstSwapCheckMs, cm.LastSwapCheckMs);
    }
    double stream = await WaitIdle("streaming after flying ~510 units", 120);
    Check(swapErrors == 0, $"LOD swap bookkeeping never retires a chunk before its replacements are ready ({swapErrors} errors)");
    // Bookkeeping only: headless, Godot occasionally stalls ~10 ms the first time an instance is made visible (not
    // seen with a real renderer), which would make the whole-swap time flaky.
    Check(worstSwapCheckMs < 2, $"LOD swap bookkeeping stays cheap while flying fast (worst {worstSwapCheckMs:F2} ms, whole swap step {worstSwapMs:F1} ms)");
    Check(stream < 120, "streaming settles");
    GD.Print($"  worst frame while streaming: {worstFrameUs / 1000.0:F1} ms, worst ChunkManager._Process {worstProcessMs:F1} ms, " +
             $"last octree diff {(double)Performance.GetCustomMonitor("voxel/diff_ms"):F1} ms, worst single result {worstItemMs:F1} ms, worst swap {worstSwapMs:F1} ms (headless)");
    GD.Print($"  total GC pause during live run: {(GC.GetTotalPauseDuration() - pause0).TotalMilliseconds:F0} ms, gen2 collections {GC.CollectionCount(2)}");

  }
}
