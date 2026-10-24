using Godot;
using System.Collections.Generic;

/// <summary>模块化舰体的挂点推进。数值单位换算在这里集中设置。</summary>
public static class ShipPropulsion
{
    /// <summary>每位驾驶员持有求解缓冲区，物理子步不反复创建数组。</summary>
    public sealed class Workspace
    {
        internal Vector3[] Columns = System.Array.Empty<Vector3>();
        internal float[] Levels = System.Array.Empty<float>();
        internal void Prepare(int count)
        {
            if (Columns.Length != count)
            {
                Columns = new Vector3[count];
                Levels = new float[count];
            }
            else System.Array.Clear(Levels);
        }
    }
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

    /// <summary>按期望合力和力矩分配喷口功率；只使用舰体实际存在的执行器。</summary>
    public static Result Allocate(ModularShip ship, Vector2 requestedForce, float requestedTorque,
        IDictionary<int, float> activity)
    {
        return Allocate(ship.GetThrusterMounts(), ship.TotalTorquerTorque, requestedForce, requestedTorque, activity);
    }

    public static Result Allocate(IReadOnlyList<ModularShip.ThrusterMount> mounts, float torquerTorque,
        Vector2 requestedForce, float requestedTorque, IDictionary<int, float> activity, Workspace? workspace = null)
    {
        var torquer = torquerTorque * ForceScale;
        float forceScale = 1, torqueCapacity = torquer;
        for (var i = 0; i < mounts.Count; i++)
        {
            var mount = mounts[i];
            forceScale += mount.Power * ForceScale;
            torqueCapacity += Mathf.Abs(mount.Offset.Cross(mount.Force)) * mount.Power * ForceScale;
        }
        var torqueScale = Mathf.Max(1, torqueCapacity);

        // 归一化后共同求解 Fx / Fy / r×F，避免平移和转向各自重复点火。
        // 这是适配本项目的有界分配器，并非原版 sNav 求解器的逐指令翻译。
        const float angularWeight = 2;
        var target = new Vector3(requestedForce.X / forceScale, requestedForce.Y / forceScale,
            requestedTorque / torqueScale * angularWeight);
        workspace ??= new Workspace();
        workspace.Prepare(mounts.Count + 1);
        var columns = workspace.Columns;
        var levels = workspace.Levels;
        for (var i = 0; i < mounts.Count; i++)
        {
            var force = mounts[i].Force * mounts[i].Power * ForceScale;
            columns[i] = new Vector3(force.X / forceScale, force.Y / forceScale,
                mounts[i].Offset.Cross(force) / torqueScale * angularWeight);
        }
        columns[^1] = new Vector3(0, 0, torquer / torqueScale * angularWeight);
        var residual = target;
        for (var pass = 0; pass < 32; pass++)
        for (var i = 0; i < columns.Length; i++)
        {
            var column = columns[i];
            if (column.LengthSquared() < 1e-12f) continue;
            var next = Mathf.Clamp(levels[i] + column.Dot(residual) / column.LengthSquared(),
                i == mounts.Count ? -1 : 0, 1);
            residual -= column * (next - levels[i]);
            levels[i] = next;
        }

        activity.Clear();
        var totalForce = Vector2.Zero;
        float torque = torquer * levels[^1], forward = 0, reverse = 0, left = 0, right = 0;
        var active = 0;
        for (var i = 0; i < mounts.Count; i++)
        {
            var mount = mounts[i];
            var level = levels[i] < 0.0001f ? 0 : levels[i];
            activity[mount.BlockId] = level;
            if (level == 0) continue;
            active++;
            var power = mount.Power * level;
            var force = mount.Force * power * ForceScale;
            totalForce += force;
            torque += mount.Offset.Cross(force);
            forward += Mathf.Max(0, mount.Force.X) * power;
            reverse += Mathf.Max(0, -mount.Force.X) * power;
            if (mount.Offset.Cross(mount.Force) < 0) left += power;
            else right += power;
        }
        return new Result(totalForce, torque, torqueCapacity, active, forward, reverse, left, right);
    }
}
