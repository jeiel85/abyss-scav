using Godot;

namespace AbyssScav.Gameplay;

/// <summary>
/// 6DOF rigid-body submarine. Custom thruster forces with velocity damping and
/// gentle auto-level stabilization. The collision hull (capsule) is independent
/// of the visual mesh and the chase camera rig.
/// Controls: W/S surge, A/D sway, Space/Ctrl heave, arrows yaw/pitch,
/// Shift boost, Z quiet toggle handled by RunController, pad sticks mirrored.
/// </summary>
public partial class SubmarineController : RigidBody3D
{
    public const float SurgeForce = 26000f;
    public const float SwayForce = 16000f;
    public const float HeaveForce = 17000f;
    public const float YawTorque = 14000f;
    public const float PitchTorque = 12000f;

    /// <summary>Normalized throttle 0..1 reported to the domain sim each frame.</summary>
    public float Throttle01 { get; private set; }
    public bool BoostHeld { get; private set; }
    public Vector3 LastForceLocal { get; private set; }
    /// <summary>Measured central force applied last integration step (N, world).</summary>
    public Vector3 LastAppliedForce { get; private set; }

    /// <summary>
    /// Quiet running: caps physical thrust at 25% and disables boost, matching
    /// the domain throttle clamp. Set by RunController from the Z toggle.
    /// </summary>
    public bool QuietMode { get; set; }

    /// <summary>
    /// Loadout thrust multiplier (1.0 stock; 0.85 with the quiet prop). Applied
    /// to physical thruster forces below the quiet-running cap, which still
    /// applies on top. Set by RunController from the domain loadout each run.
    /// </summary>
    public float ThrustMultiplier { get; set; } = 1f;

    /// <summary>
    /// Test-only autopilot: desired world-space velocity. Null in production;
    /// set only by the debug playback test scene. Thrust caps (incl. quiet)
    /// still apply, so this cannot exceed player-physical performance.
    /// </summary>
    public Vector3? TestDriveWorld { get; set; }

    /// <summary>Test-only boost injection (headless input cannot press keys).</summary>
    public bool TestBoost { get; set; }

    private Camera3D? _camera;
    private Node3D? _visual;
    private SpotLight3D? _headlight;

    public override void _Ready()
    {
        AbyssInput.EnsureRegistered();
        Mass = 900f;
        GravityScale = 0f;
        LinearDamp = 1.4f;
        LinearDampMode = DampMode.Combine;
        AngularDamp = 2.8f;
        AngularDampMode = DampMode.Combine;
        CanSleep = false;
        ContinuousCd = true;
        ContactMonitor = true;
        MaxContactsReported = 4;
        BuildBody();
    }

    private void BuildBody()
    {
        var hull = new CollisionShape3D { Name = "Hull" };
        var capsule = new CapsuleShape3D { Radius = 3.0f, Height = 11.0f };
        hull.Shape = capsule;
        hull.RotationDegrees = new Vector3(90f, 0f, 0f);
        AddChild(hull);

        _visual = new Node3D { Name = "Visual" };
        AddChild(_visual);

        var bodyMat = new StandardMaterial3D
        {
            AlbedoColor = new Color("#29414b"),
            Metallic = 0.55f,
            Roughness = 0.5f,
        };
        var accentMat = new StandardMaterial3D
        {
            AlbedoColor = new Color("#71d9d1"),
            EmissionEnabled = true,
            Emission = new Color("#71d9d1"),
            EmissionEnergyMultiplier = 0.6f,
        };
        var hullMesh = new MeshInstance3D
        {
            Name = "HullVisual",
            Mesh = new CapsuleMesh { Radius = 2.55f, Height = 9.4f },
            MaterialOverride = bodyMat,
        };
        hullMesh.RotationDegrees = new Vector3(90f, 0f, 0f);
        _visual.AddChild(hullMesh);

        var tower = new MeshInstance3D
        {
            Name = "Tower",
            Mesh = new BoxMesh { Size = new Vector3(2.2f, 1.6f, 3.4f) },
            MaterialOverride = bodyMat,
            Position = new Vector3(0f, 2.6f, 0.6f),
        };
        _visual.AddChild(tower);

        var stripe = new MeshInstance3D
        {
            Name = "Stripe",
            Mesh = new BoxMesh { Size = new Vector3(5.3f, 0.22f, 0.22f) },
            MaterialOverride = accentMat,
            Position = new Vector3(0f, 0.4f, -4.2f),
        };
        _visual.AddChild(stripe);

        var lampL = MakeLamp(new Vector3(-1.4f, 0.4f, -4.6f), accentMat);
        var lampR = MakeLamp(new Vector3(1.4f, 0.4f, -4.6f), accentMat);
        _visual.AddChild(lampL);
        _visual.AddChild(lampR);

        _headlight = new SpotLight3D
        {
            Name = "Headlight",
            LightColor = new Color(0.75f, 0.95f, 0.92f),
            LightEnergy = 6.0f,
            SpotRange = 120f,
            SpotAngle = 28f,
            ShadowEnabled = false,
            Position = new Vector3(0f, 0.6f, -4.4f),
            Rotation = Vector3.Zero, // SpotLight3D shines along -Z by default: bow-forward.
        };
        AddChild(_headlight);

        var rig = new Node3D { Name = "CameraRig", Position = new Vector3(0f, 4.2f, 10.5f) };
        AddChild(rig);
        _camera = new Camera3D
        {
            Name = "ChaseCamera",
            Fov = 68f,
            Near = 0.2f,
            Far = 260f,
        };
        rig.AddChild(_camera);
        rig.LookAt(GlobalPosition + -GlobalTransform.Basis.Z * 10f, Vector3.Up);
    }

    private static MeshInstance3D MakeLamp(Vector3 pos, Material mat)
    {
        return new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.28f, Height = 0.56f },
            MaterialOverride = mat,
            Position = pos,
        };
    }

    /// <summary>Reads planar input; RunController owns discrete verbs.</summary>
    public void PollContinuous()
    {
        BoostHeld = !QuietMode && (Input.IsActionPressed("abyss_boost") || TestBoost || AbyssInput.LeftStick().Length() > 0.92f);
    }

    public override void _IntegrateForces(PhysicsDirectBodyState3D state)
    {
        if (TestDriveWorld.HasValue)
        {
            IntegrateTestDrive(state, TestDriveWorld.Value);
            return;
        }
        var fwd = Input.GetActionStrength("abyss_fwd") - Input.GetActionStrength("abyss_back");
        var sway = Input.GetActionStrength("abyss_right") - Input.GetActionStrength("abyss_left");
        var heave = Input.GetActionStrength("abyss_up") - Input.GetActionStrength("abyss_down");
        var yaw = Input.GetActionStrength("abyss_yaw_left") - Input.GetActionStrength("abyss_yaw_right");
        var pitch = Input.GetActionStrength("abyss_pitch_up") - Input.GetActionStrength("abyss_pitch_down");
        var stick = AbyssInput.LeftStick();
        var rstick = AbyssInput.RightStick();
        fwd += -stick.Y;
        sway += stick.X;
        heave += AbyssInput.PadHeave();
        yaw += -rstick.X;
        pitch += rstick.Y;
        fwd = Math.Clamp(fwd, -1f, 1f);
        sway = Math.Clamp(sway, -1f, 1f);
        heave = Math.Clamp(heave, -1f, 1f);
        yaw = Math.Clamp(yaw, -1f, 1f);
        pitch = Math.Clamp(pitch, -1f, 1f);
        var quietScale = QuietMode ? 0.25f : 1.0f;
        var thrustMult = Math.Clamp(ThrustMultiplier, 0.1f, 1f);
        BoostHeld = !QuietMode && (Input.IsActionPressed("abyss_boost") || TestBoost);
        var boostMul = BoostHeld ? 1.6f : 1.0f;

        Throttle01 = Math.Min(
            Math.Clamp(Math.Abs(fwd) * 0.7f + Math.Abs(sway) * 0.4f + Math.Abs(heave) * 0.5f, 0f, 1f),
            QuietMode ? 0.25f : 1.0f);

        var basis = state.Transform.Basis;
        var force = (basis.Z * -fwd * SurgeForce + basis.X * sway * SwayForce + basis.Y * heave * HeaveForce) * boostMul * quietScale * thrustMult;
        LastForceLocal = new Vector3(sway, heave, fwd);
        LastAppliedForce = force;
        state.ApplyCentralForce(force);

        var torque = basis.Y * yaw * YawTorque + basis.X * pitch * PitchTorque;
        // Gentle auto-level: counter roll and residual pitch when idle.
        var up = basis.Y;
        var rollError = up.Cross(Vector3.Up);
        if (Math.Abs(yaw) < 0.05f && Math.Abs(pitch) < 0.05f)
        {
            torque += rollError * 9000f - state.AngularVelocity * 2600f;
        }
        else
        {
            var flat = new Vector3(up.X, 0f, up.Z);
            torque += -flat * 3000f;
        }
        // Roll trim from sway so banking feels physical but self-corrects.
        torque += basis.Z * -sway * 2600f;
        state.ApplyTorque(torque);
    }

    /// <summary>
    /// Debug-test autopilot branch. Drives toward a desired world velocity with
    /// the same thrust caps as player input (quiet scale + boost rule apply),
    /// so measured performance never exceeds the playable ship.
    /// </summary>
    private void IntegrateTestDrive(PhysicsDirectBodyState3D state, Vector3 desired)
    {
        BoostHeld = !QuietMode && TestBoost;
        var quietScale = QuietMode ? 0.25f : 1.0f;
        var thrustMult = Math.Clamp(ThrustMultiplier, 0.1f, 1f);
        var cap = SurgeForce * 1.2f * quietScale * thrustMult * (BoostHeld ? 1.6f : 1.0f);
        var dv = desired - state.LinearVelocity;
        var force = dv * Mass * 1.5f;
        if (force.Length() > cap)
        {
            force = force.Normalized() * cap;
        }
        Throttle01 = Math.Clamp(force.Length() / Math.Max(cap, 1f), 0f, 1f);
        LastForceLocal = state.Transform.Basis.Inverse() * force / Math.Max(cap, 1f);
        LastAppliedForce = force;
        state.ApplyCentralForce(force);

        // Face travel direction (-Z toward desired), damped.
        if (desired.Length() > 1f)
        {
            var fwd = -state.Transform.Basis.Z;
            var axis = fwd.Cross(desired.Normalized());
            var torque = axis * 9000f - state.AngularVelocity * 2600f;
            if (torque.Length() > 20000f)
            {
                torque = torque.Normalized() * 20000f;
            }
            state.ApplyTorque(torque);
        }
        else
        {
            state.ApplyTorque(-state.AngularVelocity * 2600f);
        }
    }
}
