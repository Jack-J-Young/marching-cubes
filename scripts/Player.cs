using Godot;

public partial class Player : CharacterBody3D
{
  [Export] public float Speed = 6f;
  [Export] public float JumpVelocity = 4.5f;
  [Export] public float MouseSensitivity = 0.002f;

  private Node3D cameraPivot;

  private RayCast3D rayCast;
  // private Chunk eChunk;
  private ChunkManager Cm;
  private float pitch = 0f;

  public override void _Ready()
  {
    cameraPivot = GetNode<Node3D>("CameraPivot");
    rayCast = GetNode<RayCast3D>("CameraPivot/RayCast3D");
    Cm = GetNode<ChunkManager>("../ChunkManager");

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
    if (e is InputEventMouseMotion motion)
    {
      HandleMouseMotion(motion);
    }
    
    if (Input.IsActionJustPressed("ui_cancel"))
    {
      Input.MouseMode = Input.MouseModeEnum.Visible;
    }
    if (Input.IsMouseButtonPressed(MouseButton.Left))
    {
      Input.MouseMode = Input.MouseModeEnum.Captured;
    }
  }

  [Export]
  public double DigRate = 3;
  private double DigCooldown;

  public override void _Process(double delta)
  {
    base._Process(delta);

    DigCooldown -= delta;

    if (DigCooldown > 0)
    {
      return;
    }

    if (Input.IsActionPressed("fire"))
    {
      var radius = 20;

      var pos = rayCast.GetCollisionPoint();
      Cm.Dig(pos, radius);

      DigCooldown = 1/DigRate;
    }
  }

  public override void _PhysicsProcess(double delta)
  {

    Vector3 velocity = Velocity;

    // // Gravity
    // if (!IsOnFloor())
    //   velocity.Y -= 9.8f * (float)delta;

    // // Jump
    // if (Input.IsActionJustPressed("jump") && IsOnFloor())
    //   velocity.Y = JumpVelocity;

    // WASD movement
    Vector2 inputDir = Input.GetVector("move_left", "move_right", "move_forward", "move_back");

    Vector3 direction = (Transform.Basis * new Vector3(inputDir.X, 0, inputDir.Y)).Normalized();

    velocity.Y = Input.GetAxis("crouch", "jump") * Speed;

    if (direction != Vector3.Zero)
    {
      velocity.X = direction.X * Speed;
      velocity.Z = direction.Z * Speed;
    }
    else
    {
      velocity.X = Mathf.MoveToward(velocity.X, 0, Speed);
      velocity.Z = Mathf.MoveToward(velocity.Z, 0, Speed);
    }

    Velocity = velocity;
    MoveAndSlide();
  }

  private void HandleMouseMotion(InputEventMouseMotion motion)
  {
    if (Input.MouseMode != Input.MouseModeEnum.Captured)
    {
      return;
    }
    // Left / Right rotation
    RotateY(-motion.Relative.X * MouseSensitivity);

    // Up / Down rotation
    pitch -= motion.Relative.Y * MouseSensitivity;
    pitch = Mathf.Clamp(pitch, -1.5f, 1.5f);

    cameraPivot.Rotation = new Vector3(pitch, 0, 0);
  }
}
