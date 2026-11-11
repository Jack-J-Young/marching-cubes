using System;
using System.Collections.Generic;
using Godot;
using Ships;

/// <summary>
/// Free-flying first-person controller (no gravity), fully relative to where you look: the mouse turns you about your
/// own axes (no pitch limit), Q / E roll, W/S fly along your view, A/D strafe along your right, Space/Ctrl along
/// your up. Three tools, shown in the hotbar (Hud.Hotbar) with the selected one held in view:
///   [1] Terrain: hold left to dig, right to build; scroll resizes the brush.
///   [2] Ship builder: right click adds a block (on the face you aim at, or a new free-floating ship), left click
///       breaks the block you aim at; scroll picks the block type.
///   [3] Prefab: left click on a ship saves it (you name it; user://ships, see ShipLibrary); scroll picks a saved
///       ship, R toggles whether it spawns flown by the follower AI, right click spawns it where the ghost shows.
/// Keys 1 / 2 / 3 or Ctrl+scroll switch tools; plain scroll is the current tool's own setting.
/// F on a ship climbs in and flies it (see ShipPilot); F again climbs out.
/// </summary>
public partial class Player : CharacterBody3D
{
  public enum Tool { Terrain, Ship, Prefab }

  [Export] public float Speed = 6f;
  [Export] public float JumpVelocity = 4.5f;
  [Export] public float MouseSensitivity = 0.002f;
  /// <summary>Roll speed while Q / E is held (radians per second).</summary>
  [Export] public float RollSpeed = 1.5f;
  /// <summary>How hard flying into a ship shoves it: share of the closing speed handed over per contact.</summary>
  [Export(PropertyHint.Range, "0,2,0.05")] public float PushStrength = 0.5f;

  public Tool CurrentTool { get; private set; } = Tool.Terrain;
  /// <summary>Block the ship tool places (scroll to change).</summary>
  public BlockType SelectedBlock { get; set; } = BlockType.Hull;
  public ShipPilot Pilot { get; private set; }
  /// <summary>The map (M); while it's open it gets the mouse and clicks do nothing here.</summary>
  public Map.TerrainMap? Map { get; private set; }
  private bool MapOpen => Map is { IsOpen: true };
  public bool Piloting => Pilot.Piloting;
  private CollisionShape3D _bodyShape;
  private Camera3D _camera;
  /// <summary>Physics frames until our collision comes back after climbing out (see ExitShip).</summary>
  private int _collisionBackIn;

  private Node3D cameraPivot;

  private RayCast3D rayCast;
  private ChunkManager Cm;
  private ShipBuilder _ships;
  private Hud.Hotbar _hotbar;
  public Hud.Hotbar Hotbar => _hotbar;

  /// <summary>Saved ships for the prefab tool (user://ships; tests point it elsewhere and call RefreshPrefabs).</summary>
  public ShipLibrary Library { get; set; }
  /// <summary>Selected library entry and whether it spawns AI-flown (R).</summary>
  public int PrefabIndex { get; private set; }
  public AIJob? PrefabJob { get; set; }
  public bool PrefabAI => PrefabJob != null;
  public string PrefabName => _prefabEntry?.Name ?? "";
  /// <summary>Where the selected prefab would spawn right now (updated each frame with the prefab tool out).</summary>
  public ShipBuilder.PrefabSpot PrefabSpot { get; private set; }
  /// <summary>The "save ship as" box is open: it has the keyboard, and the player doesn't move.</summary>
  public bool SavePopupOpen => _savePopup is { Visible: true };
  private ShipLibrary.Entry _prefabEntry;
  private List<(Vector3I Cell, Block Block)> _prefabBlocks;
  private readonly ShipGrid _prefabGrid = new();
  private Mesh _prefabMesh;
  private Vector3 _prefabCentre;
  private float _prefabRadius;
  private int _prefabVersion;
  private Ship _saveTarget;
  private PanelContainer _savePopup;
  private LineEdit _saveName;
  private Label _saveNote;
  private string _status = "";
  private double _statusAge = double.MaxValue;

  public override void _Ready()
  {
    cameraPivot = GetNode<Node3D>("CameraPivot");
    rayCast = GetNode<RayCast3D>("CameraPivot/RayCast3D");
    Cm = GetNode<ChunkManager>("../ChunkManager");
    _ships = GetNodeOrNull<ShipBuilder>("../Ships");
    Map = GetNodeOrNull<Map.TerrainMap>("../Map");
    rayCast.Enabled = false; // used only for its transform and target; see AimTerrain

    // Flying: no floor / wall logic, and the view turns the whole body (the pivot stays level with it).
    MotionMode = MotionModeEnum.Floating;
    cameraPivot.Rotation = Vector3.Zero;
    _bodyShape = GetNode<CollisionShape3D>("CollisionShape3D");
    _camera = GetNode<Camera3D>("CameraPivot/Camera3D");
    Pilot = new ShipPilot { Name = "ShipPilot", MouseSensitivity = MouseSensitivity, Terrain = Cm };
    AddChild(Pilot);
    CreatePreview();
    Library = new ShipLibrary();
    CreateHud();
    RefreshPrefabs();
    Input.MouseMode = Input.MouseModeEnum.Captured;
  }

  public override void _Notification(int what)
  {
    if (what == NotificationWMWindowFocusOut)
    {
      // User alt-tabbed or window lost focus
      Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    if (what == NotificationWMWindowFocusIn)
    {
      // Window regained focus (optional)
      Input.MouseMode = Input.MouseModeEnum.Captured;
    }
  }

  public override void _Input(InputEvent e)
  {
    if (SavePopupOpen)
    {
      // The name box has the keyboard (Enter saves via its signal); Esc cancels.
      if (e.IsActionPressed("ui_cancel"))
      {
        CancelSave();
        GetViewport().SetInputAsHandled();
      }
      return;
    }
    if (MapOpen)
    {
      // The map has the mouse (orbit / zoom); keys still fly, F still climbs out of a ship.
      Map!.HandleInput(e);
      if (Piloting && e.IsActionPressed("interact")) ExitShip();
      if (Input.IsActionJustPressed("ui_cancel")) Input.MouseMode = Input.MouseModeEnum.Visible;
      if (e is InputEventMouseButton { Pressed: true }) Input.MouseMode = Input.MouseModeEnum.Captured;
      return;
    }
    if (Piloting)
    {
      PilotInput(e);
      return;
    }
    if (e is InputEventMouseMotion motion)
    {
      HandleMouseMotion(motion);
      if (Input.MouseMode == Input.MouseModeEnum.Captured) _hotbar.AddMouse(motion.Relative);
    }
    if (e.IsActionPressed("interact") && _ships != null)
    {
      AimRay(out var from, out var aim);
      var t = _ships.Aim(from, aim, GetRid());
      if (t.Ship != null)
      {
        EnterShip(t.Ship);
        return;
      }
    }

    if (Input.IsActionJustPressed("ui_cancel"))
    {
      Input.MouseMode = Input.MouseModeEnum.Visible;
    }
    if (Input.IsMouseButtonPressed(MouseButton.Left) || Input.IsMouseButtonPressed(MouseButton.Right))
    {
      Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    if (e.IsActionPressed("tool_terrain")) SelectTool(Tool.Terrain);
    if (e.IsActionPressed("tool_ship")) SelectTool(Tool.Ship);
    if (e.IsActionPressed("tool_prefab")) SelectTool(Tool.Prefab);
    if (CurrentTool == Tool.Prefab && e.IsActionPressed("prefab_ai") && !e.IsEcho())
      PrefabJob = PrefabJob switch { null => AIJob.Follow, AIJob.Follow => AIJob.Dig, _ => null }; // R: off, follow, dig

    if (e is InputEventMouseButton { Pressed: true } wheel &&
        (wheel.ButtonIndex == MouseButton.WheelUp || wheel.ButtonIndex == MouseButton.WheelDown))
    {
      // Touchpads report fractional notches in Factor; mouse wheels report 1 (or 0 on some platforms).
      float notches = wheel.Factor > 0 ? wheel.Factor : 1f;
      float dir = wheel.ButtonIndex == MouseButton.WheelUp ? 1f : -1f;
      if (wheel.CtrlPressed)
      {
        // Ctrl+scroll cycles tools.
        int count = Enum.GetValues<Tool>().Length;
        SelectTool((Tool)(((int)CurrentTool + (dir > 0 ? count - 1 : 1)) % count));
      }
      else if (CurrentTool == Tool.Terrain)
      {
        DigRadius = Mathf.Clamp(DigRadius * Mathf.Pow(ScrollFactor, dir * notches), MinDigRadius, MaxDigRadius);
        _previewAge = 0;
      }
      else if (CurrentTool == Tool.Ship)
      {
        // Ship tool: scroll picks the block to place.
        int count = Enum.GetValues<BlockType>().Length;
        SelectedBlock = (BlockType)(((int)SelectedBlock + (dir > 0 ? count - 1 : 1)) % count);
      }
      else
      {
        // Prefab tool: scroll picks the saved ship.
        SelectPrefab(PrefabIndex + (dir > 0 ? -1 : 1));
      }
    }

    if (CurrentTool == Tool.Prefab && _ships != null && Input.MouseMode == Input.MouseModeEnum.Captured)
    {
      if (e.IsActionPressed("build")) PlacePrefab();
      if (e.IsActionPressed("fire"))
      {
        AimRay(out var from, out var aim);
        var t = _ships.Aim(from, aim, GetRid());
        if (t.Ship != null) SaveShipPrompt(t.Ship);
        else ShowStatus("Aim at a ship to save it");
      }
    }

    if (CurrentTool == Tool.Ship && _ships != null && Input.MouseMode == Input.MouseModeEnum.Captured)
    {
      AimRay(out var from, out var aim);
      if (e.IsActionPressed("build")) _ships.Place(from, aim, GetRid(), SelectedBlock);
      if (e.IsActionPressed("fire")) _ships.Break(from, aim, GetRid());
    }
  }

  private void PilotInput(InputEvent e)
  {
    if (e is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured) Pilot.AddMouse(motion.Relative);
    if (Input.IsActionJustPressed("ui_cancel")) Input.MouseMode = Input.MouseModeEnum.Visible;
    if (e is InputEventMouseButton { Pressed: true }) Input.MouseMode = Input.MouseModeEnum.Captured;
    if (e.IsActionPressed("interact")) ExitShip();
  }

  /// <summary>Climbs into a ship: chase camera on, our own body out of the way (we ride at its centre).</summary>
  public void EnterShip(Ship ship)
  {
    _preview.Visible = _previewLabel.Visible = false;
    _ships?.UpdateGhost(false, default, default, GetRid());
    _ships?.UpdatePrefabGhost(false, default, null, null);
    // Off right away (not deferred): we're about to ride inside the ship's blocks.
    _bodyShape.Disabled = true;
    _collisionBackIn = 0;
    Velocity = Vector3.Zero;
    Pilot.Enter(ship, _camera);
  }

  /// <summary>Climbs out next to the ship (somewhere free), facing the way the chase camera looked.</summary>
  public void ExitShip()
  {
    if (!Piloting)
    {
      Pilot.Exit();
      return;
    }
    var ship = Pilot.Ship!;
    var cam = Pilot.Camera.GlobalTransform;
    var bounds = ship.WorldBounds();
    var centre = bounds.GetCenter();
    float reach = bounds.Size.Length() * 0.5f + 1.5f;
    var basis = ship.GlobalBasis.Orthonormalized();
    var candidates = new[]
    {
      cam.Origin, centre + basis.Y * reach, centre + basis.Z * reach, centre - basis.Y * reach,
      centre + basis.X * reach, centre - basis.X * reach, centre - basis.Z * reach,
    };
    var spot = cam.Origin;
    foreach (var c in candidates)
    {
      if (SpotIsFree(c))
      {
        spot = c;
        break;
      }
    }
    Pilot.Exit();
    GlobalPosition = spot;
    // Face exactly the way the chase camera was looking, roll included.
    GlobalBasis = cam.Basis.Orthonormalized();
    cameraPivot.Rotation = Vector3.Zero;
    // Collision comes back a couple of physics frames later, once we're at rest out here: enabled in the same step
    // as the teleport, the physics engine treats the jump out of the ship as a very fast move through it and flings
    // the ship.
    _collisionBackIn = 2;
    _camera.MakeCurrent();
  }

  private bool SpotIsFree(Vector3 at)
  {
    var q = new PhysicsShapeQueryParameters3D { Shape = _bodyShape.Shape, Transform = new Transform3D(Basis.Identity, at) };
    q.Exclude = [GetRid()];
    if (GetWorld3D().DirectSpaceState.IntersectShape(q, 1).Count > 0) return false;
    return Cm.DensityAt(at) < Voxel.VoxelConst.Iso - 0.1f;
  }

  public void SelectTool(Tool tool)
  {
    CurrentTool = tool;
    _digAccum = 1.0 / DigTickRate;
  }

  /// <summary>Re-reads the ship library and keeps (or selects) an entry by name.</summary>
  public void RefreshPrefabs(string select = null)
  {
    select ??= _prefabEntry?.Name;
    Library.Refresh();
    int index = 0;
    for (int i = 0; i < Library.Entries.Count; i++)
      if (Library.Entries[i].Name == select) index = i;
    SelectPrefab(index);
  }

  /// <summary>Selects a library entry (wrapping round) and loads its blocks, mesh and size.</summary>
  public void SelectPrefab(int index)
  {
    int n = Library.Entries.Count;
    PrefabIndex = ((index % n) + n) % n;
    _prefabEntry = Library.Entries[PrefabIndex];
    _prefabBlocks = Library.Load(_prefabEntry);
    foreach (var c in new List<Vector3I>(_prefabGrid.Cells)) _prefabGrid.Remove(c);
    if (_prefabBlocks != null)
      foreach (var (c, b) in _prefabBlocks) _prefabGrid.Add(c, b);
    _prefabMesh = ShipMesh.Build(_prefabGrid);
    _prefabCentre = _prefabGrid.Count > 0 ? _prefabGrid.Centroid() : Vector3.Zero;
    _prefabRadius = 0.5f;
    foreach (var c in _prefabGrid.Cells)
      _prefabRadius = Mathf.Max(_prefabRadius, ((Vector3)c + Vector3.One * 0.5f).DistanceTo(_prefabCentre) + 0.87f);
    _prefabVersion++;
  }

  /// <summary>Opens the name box for saving this ship.</summary>
  public void SaveShipPrompt(Ship ship)
  {
    _saveTarget = ship;
    _saveName.Text = Library.NextFreeName();
    UpdateSaveNote();
    _savePopup.Visible = true;
    Input.MouseMode = Input.MouseModeEnum.Visible;
    _saveName.GrabFocus();
    _saveName.SelectAll();
  }

  /// <summary>Saves the ship the box was opened for under this name and selects it. False if it couldn't.</summary>
  public bool ConfirmSave(string name)
  {
    var ship = _saveTarget;
    CloseSavePopup();
    if (ship == null || !IsInstanceValid(ship) || ship.BlockCount == 0)
    {
      ShowStatus("That ship is gone");
      return false;
    }
    var entry = Library.Save(name, ship.Grid);
    if (entry == null)
    {
      ShowStatus($"Can't save as \"{name}\" (that name is the built-in ship)");
      return false;
    }
    RefreshPrefabs(entry.Name);
    ShowStatus($"Saved \"{entry.Name}\" ({ship.BlockCount} blocks) to {entry.Path}");
    return true;
  }

  public void CancelSave() => CloseSavePopup();

  private void CloseSavePopup()
  {
    _savePopup.Visible = false;
    _saveName.ReleaseFocus();
    _saveTarget = null;
    Input.MouseMode = Input.MouseModeEnum.Captured;
  }

  private void UpdateSaveNote()
  {
    var name = ShipLibrary.CleanName(_saveName.Text);
    _saveNote.Text = name.Equals(ShipLibrary.BuiltInName, StringComparison.OrdinalIgnoreCase) ? "That's the built-in ship's name"
      : Library.Exists(name) ? $"Overwrites the saved \"{name}\"" : $"Saves to {Library.PathFor(name)}";
  }

  /// <summary>Spawns the selected prefab where the ghost is (AI-flown if PrefabAI). Null if there's no room.</summary>
  public Ship PlacePrefab()
  {
    if (_ships == null || _prefabBlocks == null) return null;
    UpdatePrefabSpot();
    if (!PrefabSpot.Free)
    {
      ShowStatus("No room there: something is in the way");
      return null;
    }
    var ship = _ships.SpawnFromPrefab(_prefabBlocks, PrefabSpot, PrefabJob);
    ShowStatus($"Spawned {PrefabName}" + PrefabJob switch
    {
      AIJob.Follow => " (AI: follows you)",
      AIJob.Dig => " (AI: digs the nearest rock)",
      _ => "",
    });
    return ship;
  }

  private void UpdatePrefabSpot()
  {
    if (_ships == null || _prefabGrid.Count == 0) return;
    AimRay(out var from, out var aim);
    PrefabSpot = _ships.PrefabPlacement(from, aim, cameraPivot.GlobalBasis, _prefabGrid, GetRid());
  }

  private void ShowStatus(string text)
  {
    _status = text;
    _statusAge = 0;
  }

  /// <summary>Camera ray: origin and unit direction.</summary>
  private void AimRay(out Vector3 from, out Vector3 dir)
  {
    from = rayCast.GlobalPosition;
    dir = (rayCast.ToGlobal(rayCast.TargetPosition) - from).Normalized();
  }

  [ExportGroup("Digging")]
  [Export] public float DigRadius = 20f;
  [Export] public float MinDigRadius = 2f;
  [Export] public float MaxDigRadius = 60f;
  /// <summary>Radius multiplier per scroll-wheel notch.</summary>
  [Export] public float ScrollFactor = 1.12f;
  /// <summary>
  /// How fast holding fire erodes terrain, as a rate: the centre of the brush loses 1 - e^(-DigSpeed * t) of its
  /// density after t seconds. 3 carves the centre in about a quarter of a second and the full sphere in about one.
  /// </summary>
  [Export(PropertyHint.Range, "0.1,20,0.1")] public float DigSpeed = 3f;
  /// <summary>
  /// Brush applications per second while fire is held. Each one is a small, partial edit; ticks that arrive faster
  /// than chunks rebuild are merged into one edit by ChunkManager, so a high rate costs little.
  /// </summary>
  [Export(PropertyHint.Range, "1,120,1")] public float DigTickRate = 30f;
  /// <summary>
  /// Aim sweeps a sphere of DigRadius * AimRadiusScale - AimRadiusInset along the view ray and targets the first
  /// terrain it touches, so bits poking into the tube around the crosshair get dug. 0 = plain ray (exactly what's
  /// under the crosshair). Keep it below 1: the brush's strength fades to nothing at its rim, so terrain the sweep
  /// catches near the full radius is barely eroded and would hold the brush in place. At 0.6, anything the sweep
  /// stops on is dug at 40% or more of the centre's rate.
  /// </summary>
  [Export(PropertyHint.Range, "0,1,0.05")] public float AimRadiusScale = 0.6f;
  /// <summary>Fixed amount (world units) taken off the sweep radius after AimRadiusScale.</summary>
  [Export(PropertyHint.Range, "0,20,0.5")] public float AimRadiusInset = 0f;
  /// <summary>
  /// The aim tube starts as a cone: it widens from nothing at the camera to its full radius this many radii away, so
  /// terrain right beside you (the walls of a tunnel you're digging) doesn't pull the aim off what you're looking at.
  /// 0 = full-width tube from the camera.
  /// </summary>
  [Export(PropertyHint.Range, "0,8,0.25")] public float AimRampRadii = 3f;
  /// <summary>
  /// Dig on the line of sight: the sweep's contact point is projected onto the aim ray, so held digging cuts a clean
  /// cylinder. Off = dig centred on the contact point itself (follows the terrain, leaves a rougher, drilled wall).
  /// </summary>
  [Export] public bool DigOnAimRay = true;
  /// <summary>
  /// Building never closes terrain in within this distance of the player: each fill is weakened (or skipped) so the
  /// player always keeps a clear bubble of at least this radius.
  /// </summary>
  [Export(PropertyHint.Range, "0,20,0.5")] public float BuildClearance = 3f;
  /// <summary>How long the radius preview sphere stays visible after the last scroll.</summary>
  [Export] public float PreviewFadeSeconds = 1f;

  private double _digAccum;
  private MeshInstance3D _preview;
  private ShaderMaterial _previewMaterial;
  private Label3D _previewLabel;
  private double _previewAge = double.MaxValue;

  // One terrain raycast per frame at most, shared by the dig brush and the preview.
  private ulong _aimFrame = ulong.MaxValue;
  private bool _aimHit;
  private Vector3 _aimPoint;

  public override void _Process(double delta)
  {
    base._Process(delta);
    Pilot.InputBlocked = MapOpen;
    _statusAge += delta;
    if (Piloting)
    {
      UpdateHud(delta);
      return;
    }
    if (CurrentTool == Tool.Terrain)
    {
      UpdateDig(delta);
    }
    else
    {
      _previewAge = double.MaxValue;
    }
    UpdatePreview(delta);

    if (_ships != null)
    {
      AimRay(out var from, out var aim);
      _ships.UpdateGhost(CurrentTool == Tool.Ship && !SavePopupOpen, from, aim, GetRid(), SelectedBlock);
      bool prefab = CurrentTool == Tool.Prefab && !SavePopupOpen;
      if (prefab) UpdatePrefabSpot();
      _ships.UpdatePrefabGhost(prefab, PrefabSpot, _prefabMesh, PrefabJob);
    }
    UpdateHud(delta);
  }

  /// <summary>
  /// The RayCast3D node only defines the ray; the hit test uses the terrain's density field and shown meshes
  /// directly, so it works at any distance (collision shapes only exist right around the player).
  /// </summary>
  private bool AimTerrain(out Vector3 hit)
  {
    ulong frame = Engine.GetProcessFrames();
    if (frame != _aimFrame)
    {
      _aimFrame = frame;
      var from = rayCast.GlobalPosition;
      var to = rayCast.ToGlobal(rayCast.TargetPosition);
      float r = Mathf.Max(0f, DigRadius * AimRadiusScale - AimRadiusInset);
      _aimHit = Cm.SphereCastTerrain(from, to, r, out _aimPoint, r * AimRampRadii);
      if (_aimHit && DigOnAimRay)
      {
        // Keep how far along the ray the sweep hit, but centre the dig on the ray itself: every dig then lies on
        // the line of sight, so holding fire bores a straight, clean cylinder instead of chasing whatever terrain
        // the sphere grazed off to the side.
        var dir = (to - from).Normalized();
        _aimPoint = from + dir * Mathf.Max(0f, (_aimPoint - from).Dot(dir));
      }
    }
    hit = _aimPoint;
    return _aimHit;
  }

  /// <summary>
  /// Holding fire (left mouse) applies a weak dig DigTickRate times a second at the aim point, so terrain erodes
  /// gradually; holding build (right mouse) does the same but adds material, growing out from the aim point to the
  /// same radius.
  /// </summary>
  private void UpdateDig(double delta)
  {
    bool dig = !MapOpen && !SavePopupOpen && Input.IsActionPressed("fire");
    bool build = !dig && !MapOpen && !SavePopupOpen && Input.IsActionPressed("build");
    if (!dig && !build)
    {
      _digAccum = 1.0 / DigTickRate; // first tick lands immediately on the next press
      return;
    }

    double interval = 1.0 / DigTickRate;
    _digAccum += delta;
    int ticks = 0;
    while (_digAccum >= interval && ticks < 3)
    {
      _digAccum -= interval;
      ticks++;
    }
    _digAccum = Math.Min(_digAccum, interval);
    if (ticks == 0 || !AimTerrain(out var pos)) return;

    // Strength per tick from the continuous rate, so speed doesn't depend on the tick rate; merged into one edit.
    float strength = 1f - Mathf.Exp(-DigSpeed * ticks / DigTickRate);
    if (build) Cm.FillKeepingClear(pos, DigRadius, strength, GlobalPosition, BuildClearance);
    else Cm.Dig(pos, DigRadius, strength);
  }

  private void CreatePreview()
  {
    _previewMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/dig_preview.gdshader") };
    _preview = new MeshInstance3D
    {
      Name = "DigPreview",
      Mesh = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 48, Rings = 24 },
      MaterialOverride = _previewMaterial,
      CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
      TopLevel = true,
      Visible = false,
    };
    AddChild(_preview);

    _previewLabel = new Label3D
    {
      Name = "DigPreviewLabel",
      Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
      NoDepthTest = true,
      FixedSize = true,
      PixelSize = 0.0015f,
      FontSize = 32,
      OutlineSize = 10,
      TopLevel = true,
      Visible = false,
    };
    AddChild(_previewLabel);
  }

  /// <summary>After a scroll, shows the brush sphere at the aim point and fades it out over PreviewFadeSeconds.</summary>
  private void UpdatePreview(double delta)
  {
    if (_previewAge >= PreviewFadeSeconds)
    {
      _preview.Visible = false;
      _previewLabel.Visible = false;
      return;
    }
    _previewAge += delta;
    float t = (float)Math.Clamp(_previewAge / PreviewFadeSeconds, 0, 1);
    float fade = 1f - t * t; // stays bright briefly, then drops off

    Vector3 center;
    if (!AimTerrain(out center))
    {
      // Nothing under the crosshair: show the size floating in front of the camera.
      var cam = GetViewport().GetCamera3D();
      center = cam.GlobalPosition - cam.GlobalBasis.Z * Math.Max(DigRadius * 2.5f, 10f);
    }

    _preview.Visible = true;
    _preview.GlobalTransform = new Transform3D(Basis.Identity.Scaled(Vector3.One * DigRadius), center);
    _previewMaterial.SetShaderParameter("fade", fade);

    _previewLabel.Visible = true;
    _previewLabel.GlobalPosition = center;
    _previewLabel.Text = $"r {DigRadius:0.#}";
    _previewLabel.Modulate = new Color(1f, 0.9f, 0.6f, fade);
    _previewLabel.OutlineModulate = new Color(0f, 0f, 0f, fade);
  }

  private void CreateHud()
  {
    var layer = new CanvasLayer { Name = "Hud", Layer = 50 };
    _hotbar = new Hud.Hotbar { Name = "Hotbar" };
    layer.AddChild(_hotbar);

    // "Save ship as" box, centred.
    _savePopup = new PanelContainer { Name = "SaveShip", Visible = false };
    _savePopup.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center, Control.LayoutPresetMode.Minsize);
    _savePopup.GrowHorizontal = Control.GrowDirection.Both;
    _savePopup.GrowVertical = Control.GrowDirection.Both;
    var style = new StyleBoxFlat { BgColor = new Color(0.06f, 0.08f, 0.11f, 0.92f), BorderColor = new Color(1f, 0.85f, 0.35f) };
    style.SetBorderWidthAll(2);
    style.SetCornerRadiusAll(8);
    style.SetContentMarginAll(16);
    _savePopup.AddThemeStyleboxOverride("panel", style);
    var box = new VBoxContainer();
    box.AddThemeConstantOverride("separation", 8);
    _savePopup.AddChild(box);
    var title = new Label { Text = "Save ship as:" };
    title.AddThemeFontSizeOverride("font_size", 20);
    box.AddChild(title);
    _saveName = new LineEdit { CustomMinimumSize = new Vector2(340, 0), MaxLength = 64 };
    _saveName.TextSubmitted += text => ConfirmSave(text);
    _saveName.TextChanged += _ => UpdateSaveNote();
    box.AddChild(_saveName);
    _saveNote = new Label { Modulate = new Color(1f, 1f, 1f, 0.6f) };
    _saveNote.AddThemeFontSizeOverride("font_size", 13);
    box.AddChild(_saveNote);
    var help = new Label { Text = "Enter: save    Esc: cancel" };
    help.AddThemeFontSizeOverride("font_size", 14);
    box.AddChild(help);
    layer.AddChild(_savePopup);
    AddChild(layer);
  }

  private void UpdateHud(double delta)
  {
    string hint;
    string title = Hud.Hotbar.ToolNames[(int)CurrentTool];
    if (Piloting)
    {
      var s = Pilot.Ship!;
      string parts = s.Thrusters.Count == 0 ? "NO THRUSTERS (rotate only)" : $"{s.Thrusters.Count} thrusters";
      if (s.Lasers.Count > 0) parts += $", {s.Lasers.Count} lasers (left click)";
      hint = $"Piloting ({s.BlockCount} blocks, {parts}, {s.EffectiveLinearVelocity.Length():0.0} u/s)   " +
             "W/S thrust  A/D strafe  Space/Shift up/down  Q/E roll  mouse steer  Ctrl brake  F exit";
    }
    else
    {
      hint = CurrentTool switch
      {
        Tool.Terrain => "Left: dig    Right: build    Scroll: brush size",
        Tool.Ship => "Right: add block    Left: break block    Scroll: block type",
        _ => $"Right: spawn{(PrefabSpot.Free ? "" : " (blocked)")}    R: AI {PrefabJob switch { AIJob.Follow => "follow", AIJob.Dig => "dig", _ => "off" }}    Scroll: choose    Left on a ship: save it",
      };
      if (_ships != null)
      {
        AimRay(out var from, out var aim);
        if (_ships.Aim(from, aim, GetRid()).Ship != null) hint += "    F: fly this ship";
      }
      if (_statusAge < 5) hint = _status;
    }
    int room = PrefabAI ? 7 : 11;
    string prefabCaption = PrefabName.Length > room ? PrefabName[..(room - 1)] + "…" : PrefabName;
    if (PrefabJob != null) prefabCaption += PrefabJob == AIJob.Dig ? " +dig" : " +AI";
    var captions = new[] { $"r {DigRadius:0.#}", SelectedBlock.ToString(), prefabCaption };
    var block = SelectedBlock;
    var models = new (string, Func<Node3D>)[]
    {
      ("drill", Hud.ToolModels.Drill),
      ($"block:{block}", () => Hud.ToolModels.Block(block)),
      ($"prefab:{_prefabVersion}:{PrefabJob}", () => Hud.ToolModels.Tablet(_prefabMesh, _prefabCentre, _prefabRadius,
        PrefabJob switch { AIJob.Follow => ShipBuilder.AIHullColor, AIJob.Dig => ShipBuilder.DiggerHullColor, _ => null })),
    };
    _hotbar.SetState((int)CurrentTool, title, hint, captions, Piloting, models);
    bool usingTool = !MapOpen && !SavePopupOpen && (Input.IsActionPressed("fire") || Input.IsActionPressed("build"));
    _hotbar.Animate(delta, Velocity.Length(), usingTool && CurrentTool == Tool.Terrain);
  }

  public override void _PhysicsProcess(double delta)
  {
    if (Pilot.Ship != null)
    {
      if (!Piloting)
      {
        ExitShip(); // the ship was deleted under us
        return;
      }
      Pilot.PhysicsStep((float)delta);
      // Ride along at the ship's centre (our collision is off while piloting).
      GlobalPosition = Pilot.Ship!.GlobalTransform * Pilot.Ship.CenterOfMass;
      Velocity = Vector3.Zero;
      return;
    }
    if (_collisionBackIn > 0 && --_collisionBackIn == 0)
    {
      _bodyShape.SetDeferred(CollisionShape3D.PropertyName.Disabled, false);
    }
    if (SavePopupOpen)
    {
      // Typing a name: keys don't fly us around.
      Velocity = Vector3.Zero;
      return;
    }
    float dt = (float)delta;

    // Roll about the view axis (Q left, E right).
    float roll = Input.GetAxis("roll_right", "roll_left");
    if (roll != 0) RotateObjectLocal(Vector3.Back, roll * RollSpeed * dt);
    Orthonormalize();

    // Move relative to the view: W/S along it, A/D along its right, Space/Ctrl along its up.
    var wish = new Vector3(
      Input.GetAxis("move_left", "move_right"),
      Input.GetAxis("crouch", "jump"),
      Input.GetAxis("move_forward", "move_back"));
    if (wish.LengthSquared() > 1f) wish = wish.Normalized();
    var velocity = cameraPivot.GlobalBasis.Orthonormalized() * wish * Speed;

    Velocity = velocity;
    MoveAndSlide();
    PushShips(velocity);
  }

  /// <summary>Flying into a ship shoves it along (a character body otherwise treats it as a wall).</summary>
  private void PushShips(Vector3 velocity)
  {
    if (PushStrength <= 0) return;
    for (int i = 0; i < GetSlideCollisionCount(); i++)
    {
      var col = GetSlideCollision(i);
      if (col.GetCollider() is not Ship ship) continue;
      var n = col.GetNormal(); // points from the ship toward us
      float closing = -velocity.Dot(n) - (-ship.LinearVelocity.Dot(n));
      if (closing <= 0) continue;
      ship.ApplyImpulse(-n * closing * ship.Mass * PushStrength, col.GetPosition() - ship.GlobalPosition);
    }
  }

  private void HandleMouseMotion(InputEventMouseMotion motion)
  {
    if (Input.MouseMode != Input.MouseModeEnum.Captured)
    {
      return;
    }
    // Turn about our own axes (like a ship): yaw about our up, pitch about our right. No pitch limit in space.
    RotateObjectLocal(Vector3.Up, -motion.Relative.X * MouseSensitivity);
    RotateObjectLocal(Vector3.Right, -motion.Relative.Y * MouseSensitivity);
    Orthonormalize();
  }
}
