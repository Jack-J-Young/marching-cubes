#nullable enable
using System;
using System.Collections.Generic;
using Godot;
using Ships;

namespace Hud;

/// <summary>
/// The tool hotbar and the tool held in view:
///   - Bottom centre: one slot per tool (number key, icon, caption), the selected one raised and outlined, with the
///     tool's name and its controls in a hint line above.
///   - Lower right of the view: a placeholder model of the selected tool, drawn in its own little 3D world over the
///     game (so it never clips into terrain and the outline post-process leaves it alone). It bobs while you move,
///     sways with the mouse, jiggles while you use it, and drops away / rises up when you switch tools.
///   - Each slot's icon is a small render of the same model (ToolModels), redrawn when it changes.
/// While piloting the bar and the held tool hide and the hint line shows the flight controls.
/// </summary>
public partial class Hotbar : Control
{
  public static readonly string[] ToolNames = ["Terrain tool", "Ship builder", "Prefab"];
  private const int Slots = 3;
  private static readonly Vector3 HeldAt = new(0.5f, -0.36f, -1.15f);
  private const float HeldScale = 0.62f;

  /// <summary>Selected slot (0-based); for tests and the HUD.</summary>
  public int SelectedSlot { get; private set; }
  public string Hint => _hint.Text;
  public string Title => _title.Text;
  public string Caption(int slot) => _captions[slot].Text;

  private readonly PanelContainer[] _panels = new PanelContainer[Slots];
  private readonly Label[] _captions = new Label[Slots];
  private readonly SubViewport[] _iconViewports = new SubViewport[Slots];
  private readonly Node3D[] _iconRoots = new Node3D[Slots];
  private readonly string[] _iconKeys = new string[Slots];
  private StyleBoxFlat _slotStyle = null!, _selectedStyle = null!;
  private Label _title = null!, _hint = null!;
  private Control _bar = null!;
  private SubViewportContainer _heldContainer = null!;
  private Node3D _heldPivot = null!;
  private Node3D? _held;
  private string _heldKey = "";
  private int _heldSlot = -1;

  private double _time, _swap = 1;
  private Vector2 _sway, _mouse;
  private float _bob, _use;

  public override void _Ready()
  {
    SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    MouseFilter = MouseFilterEnum.Ignore;

    // Held tool: its own world, transparent, full screen, under the bar.
    _heldContainer = new SubViewportContainer { Stretch = true, MouseFilter = MouseFilterEnum.Ignore };
    _heldContainer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    AddChild(_heldContainer);
    var vp = new SubViewport { OwnWorld3D = true, TransparentBg = true, Msaa3D = Viewport.Msaa.Msaa4X };
    _heldContainer.AddChild(vp);
    SetupScene(vp, 50f, Vector3.Zero, out _heldPivot);

    _slotStyle = new StyleBoxFlat
    {
      BgColor = new Color(0.05f, 0.06f, 0.08f, 0.62f),
      BorderColor = new Color(1f, 1f, 1f, 0.25f),
      CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
    };
    _slotStyle.SetBorderWidthAll(2);
    _selectedStyle = (StyleBoxFlat)_slotStyle.Duplicate();
    _selectedStyle.BgColor = new Color(0.12f, 0.14f, 0.18f, 0.8f);
    _selectedStyle.BorderColor = new Color(1f, 0.85f, 0.35f);
    _selectedStyle.SetBorderWidthAll(3);

    var column = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.End };
    column.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterBottom, LayoutPresetMode.Minsize);
    column.GrowHorizontal = GrowDirection.Both;
    column.GrowVertical = GrowDirection.Begin;
    column.OffsetBottom = -12;
    column.AddThemeConstantOverride("separation", 4);
    AddChild(column);
    _title = MakeLabel(22, HorizontalAlignment.Center);
    _hint = MakeLabel(16, HorizontalAlignment.Center);
    _hint.AutowrapMode = TextServer.AutowrapMode.Off;
    column.AddChild(_title);
    column.AddChild(_hint);

    var bar = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center };
    bar.AddThemeConstantOverride("separation", 10);
    column.AddChild(bar);
    _bar = bar;
    for (int i = 0; i < Slots; i++)
    {
      var slot = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(84, 0) };
      slot.AddThemeConstantOverride("separation", 2);
      var panel = new PanelContainer { CustomMinimumSize = new Vector2(76, 76), MouseFilter = MouseFilterEnum.Ignore };
      panel.AddThemeStyleboxOverride("panel", _slotStyle);
      var icon = new TextureRect
      {
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        CustomMinimumSize = new Vector2(68, 68),
        MouseFilter = MouseFilterEnum.Ignore,
      };
      panel.AddChild(icon);
      var badge = MakeLabel(15, HorizontalAlignment.Left);
      badge.Text = $"{i + 1}";
      badge.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
      badge.SizeFlagsVertical = SizeFlags.ShrinkBegin;
      panel.AddChild(badge);
      var caption = MakeLabel(13, HorizontalAlignment.Center);
      caption.ClipText = true;
      caption.CustomMinimumSize = new Vector2(84, 0);
      slot.AddChild(panel);
      slot.AddChild(caption);
      bar.AddChild(slot);
      _panels[i] = panel;
      _captions[i] = caption;

      var ivp = new SubViewport
      {
        Size = new Vector2I(128, 128),
        OwnWorld3D = true,
        TransparentBg = true,
        RenderTargetUpdateMode = SubViewport.UpdateMode.Once,
        Msaa3D = Viewport.Msaa.Msaa4X,
      };
      AddChild(ivp);
      SetupScene(ivp, 30f, new Vector3(0, 0, 0), out _iconRoots[i]);
      _iconRoots[i].Position = new Vector3(0, 0, -1.1f);
      _iconViewports[i] = ivp;
      icon.Texture = ivp.GetTexture();
      _iconKeys[i] = "";
    }
  }

  private static Label MakeLabel(int size, HorizontalAlignment align)
  {
    var l = new Label { HorizontalAlignment = align, MouseFilter = MouseFilterEnum.Ignore };
    l.AddThemeFontSizeOverride("font_size", size);
    l.AddThemeColorOverride("font_outline_color", Colors.Black);
    l.AddThemeConstantOverride("outline_size", 5);
    return l;
  }

  /// <summary>A camera at the origin looking down -Z, a key light and a fill light, and a root for the model.</summary>
  private static void SetupScene(SubViewport vp, float fov, Vector3 rootAt, out Node3D root)
  {
    var env = new Godot.Environment
    {
      BackgroundMode = Godot.Environment.BGMode.ClearColor,
      AmbientLightSource = Godot.Environment.AmbientSource.Color,
      AmbientLightColor = new Color(0.55f, 0.58f, 0.65f),
      AmbientLightEnergy = 0.6f,
    };
    vp.AddChild(new WorldEnvironment { Environment = env });
    vp.AddChild(new Camera3D { Fov = fov, Near = 0.02f, Far = 20f, Current = true });
    vp.AddChild(new DirectionalLight3D { Transform = Transform3D.Identity.LookingAt(new Vector3(-0.4f, -0.8f, -0.5f), Vector3.Up), LightEnergy = 1.1f });
    root = new Node3D { Name = "Root", Position = rootAt };
    vp.AddChild(root);
  }

  /// <summary>Mouse movement (pixels) for the held tool's sway.</summary>
  public void AddMouse(Vector2 relative) => _mouse += relative;

  /// <summary>
  /// Sets what the bar shows. models: per slot, a key that changes whenever the slot's model should change, and how
  /// to build it.
  /// </summary>
  public void SetState(int slot, string title, string hint, string[] captions, bool piloting,
    (string Key, Func<Node3D> Build)[] models)
  {
    _bar.Visible = !piloting;
    _title.Visible = !piloting;
    _heldContainer.Visible = !piloting;
    _hint.Text = hint;
    _title.Text = title;
    if (slot != SelectedSlot)
    {
      SelectedSlot = slot;
      _swap = 0; // drop the old tool, raise the new one
    }
    for (int i = 0; i < Slots; i++)
    {
      _captions[i].Text = captions[i];
      _panels[i].AddThemeStyleboxOverride("panel", i == slot ? _selectedStyle : _slotStyle);
      _panels[i].Modulate = i == slot ? Colors.White : new Color(1f, 1f, 1f, 0.8f);
      if (_iconKeys[i] != models[i].Key)
      {
        _iconKeys[i] = models[i].Key;
        foreach (var c in _iconRoots[i].GetChildren()) c.QueueFree();
        var model = models[i].Build();
        _iconRoots[i].AddChild(model);
        // Three-quarter view.
        _iconRoots[i].Rotation = new Vector3(0.45f, -0.7f, 0f);
        _iconViewports[i].RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
      }
    }
    // The held model switches halfway through the swap (while it's out of view), or right away if its look changed.
    bool switchNow = _swap >= 0.5 && (_heldSlot != slot || _heldKey != models[slot].Key);
    if (switchNow || _held == null)
    {
      _held?.QueueFree();
      _held = models[slot].Build();
      _heldPivot.AddChild(_held);
      _heldSlot = slot;
      _heldKey = models[slot].Key;
    }
  }

  /// <summary>Held-tool motion: speed = how fast the player moves, using = fire / build held.</summary>
  public void Animate(double delta, float speed, bool usingTool)
  {
    float dt = (float)delta;
    _time += delta;
    _swap = Math.Min(1.0, _swap + delta / 0.3);
    _bob = Mathf.Lerp(_bob, Mathf.Clamp(speed / 6f, 0f, 1.5f), 1f - Mathf.Exp(-6f * dt));
    _use = Mathf.Lerp(_use, usingTool ? 1f : 0f, 1f - Mathf.Exp(-12f * dt));
    _sway = _sway.Lerp(-_mouse * 0.0015f, 1f - Mathf.Exp(-10f * dt)).Clamp(new Vector2(-0.12f, -0.12f), new Vector2(0.12f, 0.12f));
    _mouse = Vector2.Zero;

    float t = (float)_time;
    // Down and back up: 0 -> 0.5 lowered fully, 0.5 -> 1 raised.
    float drop = Mathf.Sin((float)_swap * Mathf.Pi) * 0.45f;
    var bob = new Vector3(Mathf.Sin(t * 7f) * 0.012f, Mathf.Abs(Mathf.Cos(t * 7f)) * 0.016f, 0f) * _bob;
    var jiggle = new Vector3(Mathf.Sin(t * 53f), Mathf.Cos(t * 47f), 0f) * 0.006f * _use + new Vector3(0, 0, 0.04f * _use);
    _heldPivot.Position = HeldAt + bob + jiggle + new Vector3(_sway.X, -_sway.Y - drop, 0f);
    _heldPivot.Rotation = new Vector3(0.12f + _sway.Y * 0.8f, -0.5f + _sway.X * 0.8f, 0.05f);
    _heldPivot.Scale = Vector3.One * HeldScale;

    // Spinning bits (the prefab tablet's mini ship), in the held tool and the icons.
    if (_held?.GetNodeOrNull<Node3D>("Spin") is { } spin) spin.RotateY(dt * 0.8f);
  }
}

/// <summary>Placeholder models for the tools, shared by the held view and the hotbar icons.</summary>
public static class ToolModels
{
  private static StandardMaterial3D Mat(Color c, bool vertexColor = false) => new()
  {
    AlbedoColor = c,
    DiffuseMode = BaseMaterial3D.DiffuseModeEnum.Toon,
    SpecularMode = BaseMaterial3D.SpecularModeEnum.Toon,
    Roughness = 0.6f,
    VertexColorUseAsAlbedo = vertexColor,
  };

  private static MeshInstance3D Part(Mesh mesh, Color c, Vector3 at, Vector3 rot = default)
    => new() { Mesh = mesh, MaterialOverride = Mat(c), Position = at, Rotation = rot };

  /// <summary>Terrain tool: a hand drill pointing forward.</summary>
  public static Node3D Drill()
  {
    var root = new Node3D { Name = "Drill" };
    var forward = new Vector3(-Mathf.Pi / 2f, 0f, 0f); // cylinder Y axis -> -Z
    root.AddChild(Part(new CylinderMesh { TopRadius = 0.075f, BottomRadius = 0.085f, Height = 0.32f, RadialSegments = 16 },
      new Color(0.55f, 0.58f, 0.62f), new Vector3(0, 0, 0.02f), forward));
    root.AddChild(Part(new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.05f, Height = 0.05f, RadialSegments = 16 },
      new Color(0.25f, 0.27f, 0.3f), new Vector3(0, 0, -0.16f), forward));
    root.AddChild(Part(new CylinderMesh { TopRadius = 0f, BottomRadius = 0.045f, Height = 0.16f, RadialSegments = 12 },
      new Color(0.85f, 0.85f, 0.9f), new Vector3(0, 0, -0.26f), forward));
    root.AddChild(Part(new BoxMesh { Size = new Vector3(0.07f, 0.2f, 0.09f) },
      new Color(1f, 0.55f, 0.15f), new Vector3(0, -0.13f, 0.08f), new Vector3(0.25f, 0, 0)));
    root.AddChild(Part(new BoxMesh { Size = new Vector3(0.1f, 0.05f, 0.14f) },
      new Color(1f, 0.55f, 0.15f), new Vector3(0, 0.09f, 0.04f)));
    return root;
  }

  /// <summary>Ship builder: the selected block itself (its marked face on top so it shows).</summary>
  public static Node3D Block(BlockType type)
  {
    var root = new Node3D { Name = "Block" };
    var grid = new ShipGrid();
    grid.Add(Vector3I.Zero, type == BlockType.Hull ? Ships.Block.Hull : new Block(type, Vector3I.Up));
    var mesh = ShipMesh.Build(grid);
    const float size = 0.26f;
    root.AddChild(new MeshInstance3D
    {
      Mesh = mesh,
      MaterialOverride = Mat(Colors.White, vertexColor: true),
      Scale = Vector3.One * size,
      Position = -Vector3.One * (size * 0.5f),
    });
    return root;
  }

  /// <summary>Prefab tool: a blueprint tablet with the selected ship hovering above it (tinted when AI-flown).</summary>
  public static Node3D Tablet(Mesh? ship, Vector3 centre, float radius, Color? aiTint)
  {
    var root = new Node3D { Name = "Tablet" };
    var tilt = new Vector3(0.9f, 0f, 0f); // face up toward the viewer
    root.AddChild(Part(new BoxMesh { Size = new Vector3(0.38f, 0.02f, 0.28f) }, new Color(0.8f, 0.85f, 0.9f), new Vector3(0, -0.08f, 0), tilt));
    root.AddChild(Part(new BoxMesh { Size = new Vector3(0.34f, 0.022f, 0.24f) }, new Color(0.12f, 0.32f, 0.7f), new Vector3(0, -0.075f, 0), tilt));
    if (ship != null)
    {
      var spin = new Node3D { Name = "Spin", Position = new Vector3(0, 0.07f, -0.05f) };
      float scale = 0.13f / Mathf.Max(radius, 0.5f);
      var mi = new MeshInstance3D
      {
        Mesh = ship,
        MaterialOverride = aiTint is Color tint ? Mat(tint) : Mat(Colors.White, vertexColor: true),
        Scale = Vector3.One * scale,
        Position = -centre * scale,
      };
      spin.AddChild(mi);
      spin.Rotation = new Vector3(0.35f, 0.6f, 0f);
      root.AddChild(spin);
    }
    return root;
  }
}
