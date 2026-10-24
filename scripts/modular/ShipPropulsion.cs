using Godot;
using System.Collections.Generic;

/// <summary>编辑器、宇宙和试航共用的挂点推进。数值单位换算在这里集中设置。</summary>
public static class ShipPropulsion
{
    // 原版 thrusterForce 原值保留；换算成当前世界的质量/距离单位。
    public const float ForceScale = 0.6f;

    public readonly record struct Result(Vector2 LocalForce, float Torque, float TorqueCapacity,
        int ActiveCount, float Forward, float Reverse, float Left, float Right);

    public static Result Evaluate(ModularShip ship, Vector2 translation, float yaw,
        IDictionary<int, float> activity)
    {
        activity.Clear();
        var force = Vector2.Zero;
        float torque = 0, capacity = 0, forward = 0, reverse = 0, left = 0, right = 0;
        var count = 0;
        foreach (var mount in ship.GetThrusterMounts())
        {
            // 两者都在舰体坐标内。整个舰体旋转不会改变本地 r×F。
            var arm = mount.Offset.Cross(mount.Force);
            capacity += Mathf.Abs(arm) * mount.Power * ForceScale;
            var linear = Mathf.Clamp(mount.Force.Dot(translation), 0, 1);
            var turn = arm * yaw > 0 ? Mathf.Clamp(Mathf.Abs(yaw), 0, 1) : 0;
            var activation = Mathf.Max(linear, turn);
            // 前进同时转向时，降低产生反向力矩的引擎，形成真实差动推力。
            if (arm * yaw < 0) activation *= 1 - Mathf.Clamp(Mathf.Abs(yaw), 0, 1);
            activity[mount.BlockId] = activation;
            if (activation <= 0) continue;
            count++;
            var applied = mount.Power * activation;
            force += mount.Force * applied * ForceScale;
            // 前进时也必须产生真实偏置力矩；同一引擎每帧至多点火一次。
            torque += arm * applied * ForceScale;
            forward += Mathf.Max(0, mount.Force.X) * applied;
            reverse += Mathf.Max(0, -mount.Force.X) * applied;
            if (arm < 0) left += applied;
            if (arm > 0) right += applied;
        }
        var torquer = ship.TotalTorquerTorque * ForceScale;
        torque += torquer * Mathf.Clamp(yaw, -1, 1);
        capacity += torquer;
        return new Result(force, torque, capacity, count, forward, reverse, left, right);
    }

    public static Vector2 ReadTranslation()
    {
        var x = (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up) ? 1 : 0)
              - (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down) ? 1 : 0);
        var y = (Input.IsKeyPressed(Key.E) ? 1 : 0) - (Input.IsKeyPressed(Key.Q) ? 1 : 0);
        return new Vector2(x, y).LimitLength();
    }

    public static float ReadYaw() =>
        (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right) ? 1 : 0)
        - (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left) ? 1 : 0);
}
