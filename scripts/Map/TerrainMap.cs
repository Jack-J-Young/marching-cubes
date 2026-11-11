#nullable enable
using System.Collections.Generic;
using Godot;
using Ships;

namespace Map;

/// <summary>
/// The map (M): a full-screen 3D hologram of the terrain within Radius of the player, as low-poly green grid
/// (map_hologram.gdshader), with markers for the player and every ship.
///   Mouse  orbit around the player (or the ship being flown)      Scroll  zoom      M  close
/// The game keeps running underneath; while the map is open the Player sends it the mouse instead of looking /
/// steering, and clicks don't dig, build or fire.
///
/// The map has its own 3D world in a SubViewport (World, so a mini map can render the same world later). Its
/// terrain is MapTerrain's coarse copy, rebuilt in the background when it's stale: the player has moved more than
/// RebuildDistance from where it was built, or the terrain has been edited. It is built once shortly after start
/// (so the first M is instant), on opening if stale, and every StaleCheckSeconds while open.
/// </summary>
public partial class TerrainMap : Node
{
  [Export] public float Radius = 512f;
  [Export] public float RebuildDistance = 64f;
  [Export] public float StaleCheckSeconds = 1f;
  [Export] public float MouseSensitivity = 0.004f;
  [Export] public float MinDistance = 40f;
  [Export] public float MaxDistance = 1100f;
  [Export] public float StartDistance = 450f;
  /// <summary>
  /// Terrain between the camera and the focus is cut away, except within this distance of the focus (plus 15% of
  /// the zoom), so rock in front of the camera never hides where you are.
  /// </summary>
  [Export] public float CutawayKeep = 60f;
  /// <summary>Seconds after start before the first (background) build, so it doesn't compete with the initial load.</summary>
  [Export] public float FirstBuildDelay = 1.5f;

  public bool IsOpen { get; private set; }
  /// <summary>Increments every time a rebuilt map is swapped in.</summary>
  public int Version { get; private set; }
  public int ChunkMeshes => _chunks.Count;
  public bool Building => _terrain.Building;
  public double LastBuildMs { get; private set; }
  public float Yaw { get; private set; }
  public float Pitch { get; private set; } = Mathf.DegToRad(-35f);
  public float Distance { get; private set; }
  public Camera3D Camera => _camera;
  public SubViewport Viewport => _viewport;
  /// <summary>The map's own 3D world (for a mini map to render too).</summary>
  public World3D World => _viewport.World3D;

  private ChunkManager? _cm;
  private Player? _player;
  private ShipBuilder? _ships;
  private readonly MapTerrain _terrain = new();
  private CanvasLayer _layer = null!;
  private SubViewport _viewport = null!;
  private Camera3D _camera = null!;
  private Node3D _terrainRoot = null!;
  private ShaderMaterial _terrainMaterial = null!;
  private MeshInstance3D _playerMarker = null!;
  private readonly List<MeshInstance3D> _chunks = [];
  private readonly Dictionary<Ship, (MeshInstance3D Body, MeshInstance3D Beacon)> _shipMarkers = [];
  private readonly List<Ship> _gone = [];
  private StandardMaterial3D _shipMaterial = null!, _pilotedMaterial = null!, _unloadedMaterial = null!, _aiMaterial = null!;
  private SphereMesh _beaconMesh = null!;
  private Vector3 _builtCentre;
  private int _builtStamp = -1;
  private bool _everBuilt;
  private double _age, _staleTimer;

  public override void _Ready()
  {
    _cm = GetNodeOrNull<ChunkManager>("../ChunkManager");
    _player = GetNodeOrNull<Player>("../Player");
    _ships = GetNodeOrNull<ShipBuilder>("../Ships");
    Distance = StartDistance;

    _layer = new CanvasLayer { Name = "MapLayer", Layer = 60, Visible = false };
    AddChild(_layer);
    var backdrop = new ColorRect { Color = new Color(0.01f, 0.03f, 0.02f, 0.82f), MouseFilter = Control.MouseFilterEnum.Ignore };
    backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
    _layer.AddChild(backdrop);
    var container = new SubViewportContainer { Stretch = true, MouseFilter = Control.MouseFilterEnum.Ignore };
    container.SetAnchorsPreset(Control.LayoutPreset.FullRect);
    _layer.AddChild(container);
    _viewport = new SubViewport
    {
      OwnWorld3D = true,
      TransparentBg = true,
      Msaa3D = Godot.Viewport.Msaa.Msaa4X,
      RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
      HandleInputLocally = false,
    };
    container.AddChild(_viewport);
    var hint = new Label
    {
      Text = "MAP   ·   mouse: orbit   ·   scroll: zoom   ·   M: close",
      HorizontalAlignment = HorizontalAlignment.Center,
      MouseFilter = Control.MouseFilterEnum.Ignore,
    };
    hint.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
    hint.OffsetTop = -44;
    hint.OffsetBottom = -16;
    hint.AddThemeFontSizeOverride("font_size", 20);
    hint.AddThemeColorOverride("font_color", new Color(0.45f, 1f, 0.55f));
    _layer.AddChild(hint);

    var world = new Node3D { Name = "MapWorld" };
    _viewport.AddChild(world);
    world.AddChild(new WorldEnvironment
    {
      Environment = new Godot.Environment
      {
        BackgroundMode = Godot.Environment.BGMode.ClearColor,
        AmbientLightSource = Godot.Environment.AmbientSource.Disabled,
        TonemapMode = Godot.Environment.ToneMapper.Linear,
      },
    });
    _camera = new Camera3D { Name = "MapCamera", Far = 3000f, Near = 0.5f, Fov = 60f };
    world.AddChild(_camera);
    _terrainRoot = new Node3D { Name = "Terrain" };
    world.AddChild(_terrainRoot);

    _terrainMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/map_hologram.gdshader") };
    _terrainMaterial.SetShaderParameter("radius", Radius);
    _playerMarker = new MeshInstance3D
    {
      Name = "PlayerMarker",
      Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.9f, Height = 2.6f, RadialSegments = 12, Rings = 1 },
      MaterialOverride = Unshaded(new Color(1f, 0.85f, 0.2f), noDepthTest: true),
      CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
    };
    world.AddChild(_playerMarker);
    _shipMaterial = Unshaded(new Color(0.3f, 0.85f, 1f));
    _pilotedMaterial = Unshaded(new Color(0.75f, 0.97f, 1f));
    _unloadedMaterial = Unshaded(new Color(0.2f, 0.4f, 0.5f));
    _aiMaterial = Unshaded(new Color(1f, 0.5f, 0.2f));
    _beaconMesh = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 12, Rings = 6 };
  }

  private static StandardMaterial3D Unshaded(Color c, bool noDepthTest = false) => new()
  {
    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
    AlbedoColor = c,
    NoDepthTest = noDepthTest,
    RenderPriority = noDepthTest ? 10 : 0,
  };

  public override void _Input(InputEvent e)
  {
    if (_player is { SavePopupOpen: true }) return; // typing a ship name
    if (e.IsActionPressed("map") && !e.IsEcho()) SetOpen(!IsOpen);
  }

  /// <summary>Mouse motion orbits, the wheel zooms (sent here by the Player while the map is open).</summary>
  public void HandleInput(InputEvent e)
  {
    if (e is InputEventMouseMotion motion)
    {
      Yaw -= motion.Relative.X * MouseSensitivity;
      Pitch = Mathf.Clamp(Pitch - motion.Relative.Y * MouseSensitivity, Mathf.DegToRad(-85f), Mathf.DegToRad(85f));
    }
    else if (e is InputEventMouseButton { Pressed: true } wheel
             && (wheel.ButtonIndex == MouseButton.WheelUp || wheel.ButtonIndex == MouseButton.WheelDown))
    {
      float notches = wheel.Factor > 0 ? wheel.Factor : 1f;
      float dir = wheel.ButtonIndex == MouseButton.WheelUp ? -1f : 1f;
      Distance = Mathf.Clamp(Distance * Mathf.Pow(1.15f, dir * notches), MinDistance, MaxDistance);
    }
  }

  public void SetOpen(bool open)
  {
    if (open == IsOpen) return;
    IsOpen = open;
    _layer.Visible = open;
    _viewport.RenderTargetUpdateMode = open ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
    if (!open) return;
    // Open looking the way the player looks, from behind and above.
    var forward = -ViewBasis().Z;
    if (new Vector2(forward.X, forward.Z).LengthSquared() > 1e-4f) Yaw = Mathf.Atan2(-forward.X, -forward.Z);
    Pitch = Mathf.DegToRad(-35f);
    _staleTimer = 0;
    StartIfStale();
    UpdateView();
  }

  /// <summary>Where the map centres (ChunkManager-local): the ship being flown, else the player.</summary>
  private Vector3 Focus()
  {
    if (_cm == null || _player == null) return Vector3.Zero;
    var pilot = _player.Pilot;
    var global = pilot != null && pilot.Piloting ? pilot.Ship!.GlobalTransform * pilot.Ship.CenterOfMass : _player.GlobalPosition;
    return _cm.ToLocal(global);
  }

  private Basis ViewBasis()
  {
    if (_player == null) return Basis.Identity;
    var pilot = _player.Pilot;
    if (pilot != null && pilot.Piloting) return pilot.Ship!.GlobalBasis.Orthonormalized();
    return GetViewport().GetCamera3D()?.GlobalBasis.Orthonormalized() ?? _player.GlobalBasis;
  }

  private int EditStamp() => _cm == null ? 0 : _cm.EditCount + _cm.EditsMerged;

  private bool Stale() => !_everBuilt || EditStamp() != _builtStamp || Focus().DistanceTo(_builtCentre) > RebuildDistance;

  private void StartIfStale()
  {
    if (_cm?.Generator == null || _terrain.Building || !Stale()) return;
    _terrain.Start(_cm.Generator, Focus(), Radius, EditStamp());
  }

  public override void _Process(double delta)
  {
    _age += delta;
    var result = _terrain.TakeResult();
    if (result != null) Swap(result);

    if (!_everBuilt && !_terrain.Building && _age > FirstBuildDelay) StartIfStale();
    if (!IsOpen) return;
    _staleTimer += delta;
    if (_staleTimer >= StaleCheckSeconds)
    {
      _staleTimer = 0;
      StartIfStale();
    }
    UpdateView();
  }

  private void Swap(MapTerrain.Result r)
  {
    foreach (var m in _chunks) m.QueueFree();
    _chunks.Clear();
    var format = Voxel.MeshBuffers.SurfaceFormat(false);
    foreach (var (key, arrays) in r.Chunks)
    {
      var mesh = new ArrayMesh();
      mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, format);
      var mi = new MeshInstance3D
      {
        Mesh = mesh,
        MaterialOverride = _terrainMaterial,
        Position = key.Origin,
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
      };
      _terrainRoot.AddChild(mi);
      _chunks.Add(mi);
    }
    _terrainMaterial.SetShaderParameter("map_center", r.Centre);
    _terrainMaterial.SetShaderParameter("radius", r.Radius);
    _builtCentre = r.Centre;
    _builtStamp = r.EditStamp;
    _everBuilt = true;
    LastBuildMs = r.Ms;
    Version++;
  }

  private void UpdateView()
  {
    var focus = Focus();
    var offset = Basis.FromEuler(new Vector3(Pitch, Yaw, 0)) * new Vector3(0, 0, Distance);
    _camera.Transform = new Transform3D(Basis.Identity, focus + offset).LookingAt(focus, Vector3.Up);
    // Cutaway: everything nearer the camera than this dissolves, keeping CutawayKeep (+ a share of the zoom)
    // around the focus visible.
    _terrainMaterial.SetShaderParameter("cut_distance", Mathf.Max(0f, Distance - (CutawayKeep + Distance * 0.15f)));

    // Player arrow along the view, a constant size on screen.
    float markerScale = Distance * 0.012f;
    var view = ViewBasis();
    var pilot = _player?.Pilot;
    bool flying = pilot != null && pilot.Piloting;
    _playerMarker.Visible = !flying;
    if (!flying) _playerMarker.Transform = new Transform3D(ShipPilot.Stretched(-view.Z, 1f).Scaled(Vector3.One * markerScale), focus);

    UpdateShips(markerScale);
  }

  private void UpdateShips(float markerScale)
  {
    if (_ships == null || _cm == null) return;
    _gone.Clear();
    foreach (var ship in _shipMarkers.Keys)
      if (!IsInstanceValid(ship) || ship.GetParent() != _ships) _gone.Add(ship);
    foreach (var ship in _gone)
    {
      var (body, beacon) = _shipMarkers[ship];
      body.QueueFree();
      beacon.QueueFree();
      _shipMarkers.Remove(ship);
    }
    var root = _terrainRoot.GetParent();
    foreach (var ship in _ships.Ships)
    {
      if (!_shipMarkers.TryGetValue(ship, out var m))
      {
        m = (new MeshInstance3D(), new MeshInstance3D { Mesh = _beaconMesh });
        root.AddChild(m.Body);
        root.AddChild(m.Beacon);
        _shipMarkers[ship] = m;
      }
      var material = ship.Piloted ? _pilotedMaterial : ship.Unloaded ? _unloadedMaterial : ship.AI != null ? _aiMaterial : _shipMaterial;
      var xf = _cm.GlobalTransform.AffineInverse() * ship.GlobalTransform;
      m.Body.Mesh = ship.RenderMesh;
      m.Body.MaterialOverride = material;
      m.Body.Transform = xf;
      // A beacon at the centre so even a one-block ship is visible from far away.
      m.Beacon.MaterialOverride = material;
      m.Beacon.Transform = new Transform3D(Basis.FromScale(Vector3.One * markerScale * 0.45f), xf * ship.CenterOfMass);
    }
  }
}
