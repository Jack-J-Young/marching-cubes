# marching-cubes: project context

A Godot 4.7.1 **C#** voxel tech demo: marching-cubes asteroid terrain (octree LOD with Transvoxel seams) that you
dig and build. You can also build block ships (hull, thruster, mining laser), fly them, and let AI ships follow
you or mine. Jolt physics. It has to work on both **Forward+** and **Compatibility** renderers.

## Working with the user
- **Commit only when asked.** When asked, list the files and suggest a message for them to approve or replace.
  Work is on branch `perf-overhaul`; `main` is the base.
- For bigger features they like plan mode first, with a few clear-cut questions, then implementation.
- Back performance claims with measurements (there are stress and bench modes for that, see below).
- Verify visual changes with screenshots on **both** renderers before calling them done.

## Build and test
```bash
dotnet build
"/c/Program Files/Godot/Godot_v4.7.1-stable_mono_win64_console.exe" --headless --path . res://tests/voxel_self_test.tscn
```
The self-test (`tests/VoxelSelfTest.cs`) prints `[PASS]`/`[FAIL]` lines and ends with `ALL CHECKS PASSED`. The full
run takes several minutes. Modes are passed after `--`:

| Mode | What it does | Window? |
|---|---|---|
| (none) | Unit tests plus a live run of the scene. No spawn clearing, prefab or follower; the terrain is a dense field. | headless OK |
| `--ai-follow` | The real startup (clearing, prefab, follower): follow, obstacle routing, digger, empty-chunk memo | headless OK |
| `--dig-stress=<n>` | n AI diggers for 40 s; reports fps, edits per second and a terrain cost breakdown | headless OK |
| `--spawn-shot=<dir>` | Screenshots of the real startup: spawn, prefab, beams, map, hotbar, follower, plus the ship-hatching check | needs a window |
| `--screenshot=<dir>`, `--bench-dig[=r]` | Older terrain screenshots and the dig benchmark | |

Add `--rendering-method gl_compatibility` before `--path` to test Compatibility. Window runs can pick up stray
mouse or keyboard input if the window gets focus, so a one-off odd screenshot may be that, not a bug.

## Layout
- **Terrain:** `scripts/ChunkManager.cs` plus `scripts/Voxel/`:
  - **Chunks:** 32 cells; LOD L has step `2^L`, `MaxLod` 3. Meshing and edit replay happen on worker threads
    (`MeshJobScheduler`, `ChunkBuilder`).
  - **Edits:** analytic spheres in an append-only `EditStore`. They are never compacted, so per-area cost grows with
    edit count; baking old edits into stored voxel data is the known next step.
  - **Edit batching:** edits are batched and the surface change animates (morph). `SphereEdit.Instant` edits skip
    the animation.
  - **`UniformChunks`:** remembers all-air / all-rock chunks so they skip rebuilding. Digs keep air chunks valid;
    builds drop them.
  - **Collision:** per chunk, built on the main thread (the costliest part). Away from the player it's rebuilt at
    most every `CollisionRefreshSeconds`.
  - **Rays:** `RaycastTerrain` ray-marches the density field directly, so it works at any distance or LOD.
  - **Spawn clearing:** a 256-radius empty bubble, made in `TerrainGenerator.RawDensity`.
- **Ships:** `scripts/Ships/`.
  - **Core:**
    - `Ship`: a rigid body of blocks, with impacts that dig craters.
    - `ShipBuilder`: placing and breaking blocks, spawning ships, and pausing or unloading them. A ship unloads
      only when its centre is in no loaded chunk; a piloted ship never pauses.
  - **Flying:**
    - `ShipControl`: shared thrust and gyro code. `ThrusterFlames` and `LaserBank` live in it too; `LaserBank`
      batches digs, lightly for the player and heavily plus instant for AI.
    - `ShipPilot`: the player flying a ship.
  - **AI:**
    - `ShipAI`: the Follow and Dig jobs.
    - `ShipPathfinder`: A* in two levels, a 32-unit coarse route through known-empty chunks, then a fine 4-unit
      grid. It runs on a background task.
  - **Designs:**
    - `ShipPrefabs.Miner()`: the starter ship.
    - `ShipLibrary`: saved designs as JSON in `user://ships`
      (`%APPDATA%\Godot\app_userdata\marching-cubes\ships`).
- **Map:** `scripts/Map/`. **M** opens a green-grid hologram of LOD3 terrain within 512, in its own `SubViewport`
  world. `TerrainMap.World` is exposed for a future mini map.
- **HUD:** `scripts/Hud/Hotbar.cs`. Three tool slots (Terrain, Ship builder, Prefab) with icons rendered from
  placeholder 3D models, and the held tool drawn in an overlay viewport. `Player.cs` owns the tools and input, and
  the prefab tool's save popup.
- **Shaders:** `toon.gdshaderinc` does pencil hatching from `v_hatch_pos` / `v_hatch_normal`. Terrain passes world
  space; `ship.gdshader` passes object space, so the strokes move with the ship.

## Gotchas found the hard way
- **Patch scripts must write UTF-8** (`open(..., encoding='utf-8')`). Python's Windows default (cp1252) silently
  broke `Player.cs` and Godot refused to load it. Keep each file's existing CRLF/LF line endings.
- `Basis.Scaled` scales world axes. To stretch a tilted cylinder along its own axis, use
  `ShipControl.Stretched(dir, len)`.
- **Control layout created in code:** use `SetAnchorsAndOffsetsPreset(...)`. `SetAnchorsPreset` alone left the
  hotbar at size 0. The enum value is `LayoutPresetMode.Minsize`.
- **Input in tests:** events from `Input.ParseInputEvent` reach nodes a frame or two later, and `process_frame`
  fires before nodes' `_Process`. Wait 2–3 frames before checking the effect.
- **Name clashes:** `ShipBuilder.SpawnPrefab` is a bool export, which is why the spawn method is `SpawnFromPrefab`.
  The MSAA enum is `Godot.Viewport.Msaa.Msaa4X`.
- **Empty chunks:** the dense test field has almost no all-air chunks (all 447 loaded chunks had surface). Empty
  space mostly exists in the spawn bubble and in dug-out areas.
- **Laser edits:** don't let lasers or AI write one edit per tick. Unbatched, one miner made 120 edits a second and
  everything that scales with edits fell over.

## Open ideas and follow-ups
- **Terrain:** bake or compact old edits (the real long-term fix for edit growth).
- **AI ships:**
  - keep diggers within a range of the player;
  - avoid other ships (AI only avoids terrain today).
- **Prefabs:** delete or rename saved prefabs in game.
- **Map:**
  - add the mini map;
  - rebuild only the chunks a dig touched (the full rebuild takes about 1.6 s);
  - space the grid lines out when zoomed out.
