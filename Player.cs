using Godot;
using System;

public partial class Player : CharacterBody3D
{
  [Export] public float Speed = 6f;
  [Export] public float JumpVelocity = 4.5f;
  [Export] public float MouseSensitivity = 0.002f;

  private Node3D cameraPivot;

  private RayCast3D rayCast;
  private Chunk eChunk;
  private float pitch = 0f;

  public override void _Ready()
  {
    cameraPivot = GetNode<Node3D>("CameraPivot");
    rayCast = GetNode<RayCast3D>("CameraPivot/RayCast3D");
    eChunk = GetNode<Chunk>("../Chunk");

    Input.MouseMode = Input.MouseModeEnum.Captured;
  }

  public override void _Input(InputEvent e)
  {
    if (e is InputEventMouseMotion motion)
    {
      // Left / Right rotation
      RotateY(-motion.Relative.X * MouseSensitivity);

      // Up / Down rotation
      pitch -= motion.Relative.Y * MouseSensitivity;
      pitch = Mathf.Clamp(pitch, -1.5f, 1.5f);

      cameraPivot.Rotation = new Vector3(pitch, 0, 0);
    }
    
    if (Input.IsActionJustPressed("ui_cancel"))
    {
      Input.MouseMode = Input.MouseModeEnum.Visible;
    }
    if (Input.IsMouseButtonPressed(MouseButton.Left))
    {
      Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    if (Input.IsActionJustPressed("fire"))
    {
      var radius = 5f;

      var pos = rayCast.GetCollisionPoint();
      eChunk.Dig(pos, radius);
    }
  }

  public override void _PhysicsProcess(double delta)
  {
    Vector3 velocity = Velocity;

    // // Gravity
    // if (!IsOnFloor())
    //   velocity.Y -= 9.8f * (float)delta;

    // Jump
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
}
