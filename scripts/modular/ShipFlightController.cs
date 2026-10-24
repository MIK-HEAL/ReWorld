using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// 原版三种控制坐标系与刚体驾驶。证据：Reassembly/analysis/flight_evidence.json。
/// 模式、键位、阻尼和速度目标取自原版；执行器分配适配 ReWorld 的质量和单位。
/// </summary>
public sealed class ShipFlightController
{
    public enum ControlScheme { Cursor = 1, Keyboard = 2, Cardinal = 3 }
    public readonly record struct Controls(float Forward, float Horizontal, float Strafe);
    public readonly record struct Command(Vector2 LocalTranslation, float HeadingError, float ManualTurn);
    public readonly record struct FlightProfile(IReadOnlyList<ModularShip.ThrusterMount> Mounts,
        float TorquerTorque, float MomentOfInertia, float TotalMass, bool HasLiveCore, float BoundingRadius,
        ulong Revision);

    // CVar 默认值，分别见原版 0x14000be80、0x140010fb0、0x140002660/2710。
    public const float VelocityRetention = 0.8f;
    public const float MaxSpeed = 5000;
    public const float RotationKp = 1.2f;
    public const float RotationKd = 0.3f;
    public ControlScheme Scheme { get; private set; } = ControlScheme.Cursor;
    private float _targetHeading;
    private bool _headingInitialized;
    private readonly ShipPropulsion.Workspace _allocation = new();

    public string SchemeLabel => Scheme switch
    {
        ControlScheme.Cursor => "光标转向 · 相对光标移动",
        ControlScheme.Keyboard => "键盘转向 · 相对舰体移动",
        _ => "光标转向 · 地图坐标移动",
    };

    public void CycleScheme(float heading)
    {
        // 原版 Player 0x1401d2e31–3f：(old + 1) % 3 + 1。
        Scheme = (ControlScheme)(((int)Scheme + 1) % 3 + 1);
        _targetHeading = heading;
        _headingInitialized = true;
    }

    public static Controls ReadControls(Func<Key, bool>? pressed = null)
    {
        pressed ??= Input.IsKeyPressed;
        bool Down(Key primary, Key alternate) => pressed(primary) || pressed(alternate);
        return new Controls(
            (Down(Key.W, Key.Up) ? 1 : 0) - (Down(Key.S, Key.Down) ? 1 : 0),
            (Down(Key.D, Key.Right) ? 1 : 0) - (Down(Key.A, Key.Left) ? 1 : 0),
            (pressed(Key.E) ? 1 : 0) - (pressed(Key.Q) ? 1 : 0));
    }

    public Command Resolve(Controls controls, float heading, Vector2? aimDirection)
    {
        if (!_headingInitialized)
        {
            _targetHeading = heading;
            _headingInitialized = true;
        }
        if (Scheme != ControlScheme.Keyboard && aimDirection is { } aim && aim.LengthSquared() > 0.0001f)
            _targetHeading = aim.Angle();

        Vector2 translation;
        if (Scheme == ControlScheme.Keyboard)
        {
            translation = new Vector2(controls.Forward, controls.Strafe).LimitLength();
            _targetHeading = heading;
        }
        else if (Scheme == ControlScheme.Cardinal)
        {
            // W/S 对应地图上下，A/D（或 Q/E）对应地图左右。
            translation = new Vector2(Mathf.Clamp(controls.Horizontal + controls.Strafe, -1, 1),
                -controls.Forward).LimitLength().Rotated(-heading);
        }
        else
        {
            translation = new Vector2(controls.Forward,
                Mathf.Clamp(controls.Horizontal + controls.Strafe, -1, 1)).LimitLength()
                .Rotated(_targetHeading - heading);
        }
        var error = Mathf.Wrap(_targetHeading - heading, -Mathf.Pi, Mathf.Pi);
        if (Mathf.Abs(error) < 0.00001f) error = 0;
        return new Command(translation, error,
            Scheme == ControlScheme.Keyboard ? controls.Horizontal : 0);
    }

    public ShipPropulsion.Result Evaluate(ModularShip ship, Command command, Vector2 localVelocity,
        float angularVelocity, IDictionary<int, float> activity)
        => Evaluate(Capture(ship), command, localVelocity, angularVelocity, activity);

    public static FlightProfile Capture(ModularShip ship)
        => new(ship.GetThrusterMounts(), ship.TotalTorquerTorque, ship.MomentOfInertia(), ship.TotalMass,
            ship.HasLiveCore, ship.BoundingRadius(), ship.Revision);

    public ShipPropulsion.Result Evaluate(FlightProfile profile, Command command, Vector2 localVelocity,
        float angularVelocity, IDictionary<int, float> activity)
    {
        if (!profile.HasLiveCore)
        {
            activity.Clear();
            return default;
        }
        var mounts = profile.Mounts;
        float positive = profile.TorquerTorque * ShipPropulsion.ForceScale, negative = positive;
        for (var i = 0; i < mounts.Count; i++)
        {
            var mount = mounts[i];
            var torque = mount.Offset.Cross(mount.Force) * mount.Power * ShipPropulsion.ForceScale;
            positive += Mathf.Max(0, torque);
            negative += Mathf.Max(0, -torque);
        }
        var inertia = profile.MomentOfInertia;
        var capacity = Mathf.Max(positive, negative);
        var angularAcceleration = capacity / inertia;
        float wantedTorque = 0;
        if (angularAcceleration > 0.0001f)
        {
            // 有限推力下的角度反馈与反向制动；不直接改写朝向或角速度。
            var yaw = RotationKp * command.HeadingError
                - 2 * RotationKd / Mathf.Sqrt(angularAcceleration) * angularVelocity;
            if (Scheme == ControlScheme.Keyboard && command.ManualTurn != 0)
                yaw = command.ManualTurn;
            wantedTorque = Mathf.Clamp(yaw, -1, 1) * (yaw >= 0 ? positive : negative);
        }

        var wantedForce = Vector2.Zero;
        if (!command.LocalTranslation.IsZeroApprox())
        {
            // 输入给出速度目标，推力由实际引擎能力限制；反向输入使用反向喷口。
            var error = command.LocalTranslation * MaxSpeed - localVelocity;
            var direction = error.Normalized();
            var available = 0.0f;
            for (var i = 0; i < mounts.Count; i++)
            {
                var mount = mounts[i];
                available += Mathf.Max(0, mount.Force.Dot(direction)) * mount.Power * ShipPropulsion.ForceScale;
            }
            wantedForce = direction * Mathf.Min(error.Length() * profile.TotalMass, available);
        }
        return ShipPropulsion.Allocate(mounts, profile.TorquerTorque, wantedForce, wantedTorque, activity, _allocation);
    }

    /// <summary>v'=a+ln(damping)*v 的解析步进；滑行每秒保留原版 80% 速度。</summary>
    public static void Integrate(ref Vector2 position, ref Vector2 velocity, ref float heading,
        ref float angularVelocity, Vector2 acceleration, float angularAcceleration, float dt)
    {
        if (dt <= 0) return;
        var decayRate = -Math.Log(VelocityRetention);
        var decayValue = Math.Exp(-decayRate * dt);
        var velocityIntegral = (float)((1 - decayValue) / decayRate);
        var forceIntegral = (float)((dt - velocityIntegral) / decayRate);
        var decay = (float)decayValue;
        position += velocity * velocityIntegral + acceleration * forceIntegral;
        heading = Mathf.Wrap(heading + angularVelocity * velocityIntegral + angularAcceleration * forceIntegral,
            -Mathf.Pi, Mathf.Pi);
        velocity = velocity * decay + acceleration * velocityIntegral;
        angularVelocity = angularVelocity * decay + angularAcceleration * velocityIntegral;
    }
}
