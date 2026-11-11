#nullable enable
using System;
using System.Collections.Generic;
using Godot;

namespace Ships;

/// <summary>
/// Flies a ship from the inside with a third-person chase camera.
///   W / S       thrust forward / back      A / D      strafe left / right
///   Space/Shift thrust up / down           Q / E      roll left / right
///   Mouse       steer (gyroscope: the mouse sets the turn rate you want, the gyro applies the torque to get it,
///               up to GyroTorquePerBlock x blocks, and holds the ship steady when the mouse stops)
///   Ctrl        brake: the thrusters that can push against the ship's velocity do, until it stops
///   Left click  fire the mining lasers (continuous beams that dig terrain where they hit)
/// Thrust comes only from thruster blocks: each pushes the ship away from its nozzle with ThrusterForce, at its own
/// position, throttled by how well it lines up with the wanted direction (ThrustAllocator). Off-centre thrusters
/// also turn the ship; the gyro counters that up to its limit. A ship without thrusters can only rotate.
/// </summary>
public partial class ShipPilot : Node
{
  /// <summary>Push of one thruster block (force units: a 1-block ship weighs 1).</summary>
  [Export] public float ThrusterForce = 25f;
  /// <summary>
  /// Most torque the gyroscope can apply, per block of the ship (every block carries a bit of gyro), so a big ship
  /// turns about as readily as a small one.
  /// </summary>
  [Export] public float GyroTorquePerBlock = 15f;
  /// <summary>Turn per pixel of mouse movement (radians), like mouse look.</summary>
  [Export] public float MouseSensitivity = 0.002f;
  /// <summary>Roll rate while Q / E is held (radians per second).</summary>
  [Export] public float RollRate = 1.5f;
  [Export] public float CameraDistance = 6f;
  [Export] public float CameraHeight = 2f;
  /// <summary>How quickly the camera catches up with the ship (higher = stiffer).</summary>
  [Export] public float CameraFollow = 12f;

  [ExportGroup("Mining laser")]
  [Export] public float LaserRange = 80f;
  [Export] public float LaserDigRadius = 2.5f;
  /// <summary>How fast a beam erodes rock: the centre loses 1 - e^(-speed * t) of its density after t seconds.</summary>
  [Export] public float LaserDigSpeed = 4f;
  /// <summary>Dig edits per second per beam (merged by ChunkManager when they come faster than it rebuilds).</summary>
  [Export] public float LaserTickRate = 20f;

  public Ship? Ship { get; private set; }
  public bool Piloting => Ship != null && IsInstanceValid(Ship);
  public Camera3D Camera { get; private set; } = null!;
  /// <summary>Terrain the lasers dig (set by the Player).</summary>
  public ChunkManager? Terrain;

  /// <summary>Last physics step's thruster throttles (same order as Ship.Thrusters) and whether lasers fired.</summary>
  public float[] Throttles { get; private set; } = [];
  public bool LasersFiring => _lasers.Firing;
  /// <summary>Set while something else has the mouse (the map): the lasers don't fire.</summary>
  public bool InputBlocked;
  /// <summary>Laser beams of the last physics step: global start and end, and whether each hit terrain.</summary>
  public List<(Vector3 From, Vector3 To, bool Hit)> Beams => _lasers.Beams;

  private Vector2 _mouse;
  private ThrusterFlames _flames = null!;
  private LaserBank _lasers = null!;

  public override void _Ready()
  {
    Camera = new Camera3D { Name = "ShipCamera", TopLevel = true, Far = 4000f };
    AddChild(Camera);
    _flames = new ThrusterFlames(this);
    // Light batching: a few digs a second per beam instead of 20, still animated, so it feels the same.
    _lasers = new LaserBank(this) { Batch = true, BatchStrength = 0.5f, BatchSeconds = 0.15f };
  }

  public void Enter(Ship ship, Camera3D from)
  {
    Ship = ship;
    ship.CanSleep = false;
    ship.Sleeping = false;
    ship.Piloted = true;
    _mouse = Vector2.Zero;
    _lasers.Reset();
    Camera.Fov = from.Fov;
    Camera.Far = from.Far;
    Camera.GlobalTransform = TargetCameraTransform();
    Camera.MakeCurrent();
  }

  public void Exit()
  {
    if (Piloting)
    {
      Ship!.CanSleep = true;
      Ship.Piloted = false;
    }
    Ship = null;
    Throttles = [];
    _lasers.Step(null!, null, false, 0f);
    HideEffects();
  }

  /// <summary>Mouse movement since the last physics step (pixels).</summary>
  public void AddMouse(Vector2 relative) => _mouse += relative;

  /// <summary>Applies thrust, brake, gyro torque and the lasers for this physics step.</summary>
  public void PhysicsStep(float dt)
  {
    if (!Piloting || dt <= 0) return;
    var ship = Ship!;
    var mouse = _mouse;
    _mouse = Vector2.Zero;
    if (ship.Paused) return;
    var basis = ship.GlobalBasis.Orthonormalized();

    ApplyThrust(ship, basis, dt);

    // Gyroscope: turn rate wanted, in ship space (pitch about +X, yaw about +Y, roll about +Z).
    var wantLocal = new Vector3(
      -mouse.Y * MouseSensitivity / dt,
      -mouse.X * MouseSensitivity / dt,
      Input.GetAxis("roll_right", "roll_left") * RollRate);
    var want = basis * wantLocal;
    ShipControl.Gyro(ship, want, GyroTorquePerBlock, dt);

    _lasers.Range = LaserRange;
    _lasers.DigRadius = LaserDigRadius;
    _lasers.DigSpeed = LaserDigSpeed;
    _lasers.TickRate = LaserTickRate;
    _lasers.Step(ship, Terrain, Input.IsActionPressed("fire") && !InputBlocked, dt);
  }

  private void ApplyThrust(Ship ship, Basis basis, float dt)
  {
    var thrusters = ship.Thrusters;
    if (Throttles.Length != thrusters.Count) Throttles = new float[thrusters.Count];
    if (thrusters.Count == 0) return;

    if (Input.IsActionPressed("crouch"))
    {
      // Brake: fire whatever can push against the velocity, scaled down near standstill so it settles.
      var vLocal = basis.Inverse() * ship.LinearVelocity;
      float speed = vLocal.Length();
      if (speed < 1e-3f)
      {
        Array.Clear(Throttles);
        return;
      }
      // Enough to stop within this step, as far as the thrusters allow.
      ShipControl.ThrottlesForAccel(ship, -vLocal / dt, ThrusterForce, Throttles);
    }
    else
    {
      // Forward is -Z.
      var wish = new Vector3(
        Input.GetAxis("move_left", "move_right"),
        Input.GetAxis("pilot_down", "jump"),
        Input.GetAxis("move_forward", "move_back"));
      ThrustAllocator.Throttles(thrusters, wish, Throttles);
    }
    ShipControl.ApplyThrottles(ship, Throttles, ThrusterForce);
  }

  public override void _Process(double delta)
  {
    if (!Piloting)
    {
      HideEffects();
      return;
    }
    float k = 1f - Mathf.Exp(-CameraFollow * (float)delta);
    var target = TargetCameraTransform();
    var t = Camera.GlobalTransform;
    Camera.GlobalTransform = new Transform3D(
      t.Basis.Orthonormalized().Slerp(target.Basis.Orthonormalized(), k),
      t.Origin.Lerp(target.Origin, k));
    UpdateEffects();
  }

  /// <summary>Flames on firing thrusters (cones sized by throttle) and laser beams.</summary>
  private void UpdateEffects()
  {
    var ship = Ship!;
    _flames.Update(ship, Throttles);
    _lasers.Draw(ship);
  }

  private void HideEffects()
  {
    _flames.Hide();
    _lasers.Hide();
  }

  /// <summary>See ShipControl.Stretched.</summary>
  public static Basis Stretched(Vector3 dir, float length) => ShipControl.Stretched(dir, length);

  /// <summary>Behind and above the ship (scaled by its size), looking at its centre, rolling with it.</summary>
  private Transform3D TargetCameraTransform()
  {
    var ship = Ship!;
    var basis = ship.GlobalBasis.Orthonormalized();
    var centre = ship.GlobalTransform * ship.CenterOfMass;
    float size = ship.WorldBounds().Size.Length();
    var eye = centre + basis * new Vector3(0, CameraHeight + size * 0.25f, CameraDistance + size * 0.75f);
    var look = centre + basis.Y * (CameraHeight * 0.3f);
    return Transform3D.Identity.Translated(eye).LookingAt(look, basis.Y);
  }
}
