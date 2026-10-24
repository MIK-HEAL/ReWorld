using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Reassembly 式模块化舰体 —— 数据模型。
///
/// 对齐逆向的 Block / BlockCluster：
///   - 一个方块 = 形状 + 连续浮点位置 + 任意弧度朝向（非网格、非 90° 步进）。
///   - 连接关系由「端口位置重合」自动推导，无需显式边表。
///   - 断裂 = 连通分量切分（对应 killRecursive / detachFromParent）。
///
/// 本类是纯数据（无场景节点），供建造编辑器与运行时渲染共用。
/// </summary>
public sealed class ModularShip
{
    /// <summary>一个已放置的方块。</summary>
    public sealed class Block
    {
        public int Id { get; init; }
        public required string ShapeId { get; init; }
        /// <summary>世界位置（连续浮点）。</summary>
        public Vector2 Position { get; set; }
        /// <summary>朝向（弧度）。</summary>
        public float Angle { get; set; }
        public bool Enabled { get; set; } = true;

        public ShipShapeLibrary.ShapeSpec Spec => ShipShapeLibrary.Get(ShapeId);
    }

    private readonly List<Block> _blocks = new();
    private int _nextId = 1;

    public IReadOnlyList<Block> Blocks => _blocks;
    public event Action? Changed;

    public Block? Core => _blocks.FirstOrDefault(block => block.ShapeId == "core" && block.Enabled);

    /// <summary>添加方块（不自动连接，由编辑器决定位置/朝向）。</summary>
    public Block AddBlock(string shapeId, Vector2 position, float angle)
    {
        if (!ShipShapeLibrary.Shapes.ContainsKey(shapeId))
            return null!;
        var block = new Block { Id = _nextId++, ShapeId = shapeId, Position = position, Angle = angle };
        _blocks.Add(block);
        Changed?.Invoke();
        return block;
    }

    public bool RemoveBlock(Block block)
    {
        if (block.ShapeId == "core")
            return false;
        if (!_blocks.Remove(block))
            return false;
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// 计算方块 a 的端口 p 与方块 b 的端口 q 连接后，a 应放置的世界位置与朝向。
    /// 返回 a 的世界坐标与角度，使 a 的端口 p 与 b 的端口 q 重合且法线相反。
    /// </summary>
    public static (Vector2 Position, float Angle) SnapBlock(
        ShipShapeLibrary.Port selfPort, ShipShapeLibrary.Port targetPort,
        Vector2 targetWorldPos, float targetAngle)
    {
        // 目标端口的世界位置与法线。
        var targetPortWorld = targetWorldPos + targetPort.LocalPosition.Rotated(targetAngle);
        var targetNormalWorld = targetPort.Normal.Rotated(targetAngle);

        // 自端口应指向目标端口的反方向（两个端口对接）。
        // self 端口的世界法线应等于 -targetNormalWorld。
        // self 端口本地法线旋转 selfAngle 后 = -targetNormalWorld
        // => selfAngle = (-targetNormalWorld).Angle() - selfPort.Normal.Angle()
        var selfAngle = (-targetNormalWorld).Angle() - selfPort.Normal.Angle();

        // self 位置：使 self 端口的世界位置 = targetPortWorld。
        var selfPos = targetPortWorld - selfPort.LocalPosition.Rotated(selfAngle);

        return (selfPos, selfAngle);
    }

    /// <summary>获取所有已连接的端口对（用于渲染连接线和校验）。</summary>
    public List<(Block A, Block B)> GetConnections(float snapTolerance = 1.5f)
    {
        var connections = new List<(Block, Block)>();
        var enabled = _blocks.Where(block => block.Enabled).ToList();
        for (var i = 0; i < enabled.Count; i++)
        {
            for (var j = i + 1; j < enabled.Count; j++)
            {
                if (AreConnected(enabled[i], enabled[j], snapTolerance))
                    connections.Add((enabled[i], enabled[j]));
            }
        }
        return connections;
    }

    /// <summary>两个方块是否通过端口连接（任一端口对重合）。</summary>
    public static bool AreConnected(Block a, Block b, float tolerance = 1.5f)
    {
        var specA = a.Spec;
        var specB = b.Spec;
        foreach (var portA in specA.Ports)
        {
            var posA = a.Position + portA.LocalPosition.Rotated(a.Angle);
            foreach (var portB in specB.Ports)
            {
                var posB = b.Position + portB.LocalPosition.Rotated(b.Angle);
                if (posA.DistanceTo(posB) <= tolerance)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 计算连通分量（对应 BlockCluster 子树切分）。
    /// 返回按「是否含核心」排序的分量列表（含核心的在前）。
    /// </summary>
    public List<List<Block>> GetConnectedComponents(float tolerance = 1.5f)
    {
        var enabled = _blocks.Where(block => block.Enabled).ToList();
        var adjacency = new Dictionary<int, List<int>>();
        foreach (var block in enabled)
            adjacency[block.Id] = new List<int>();

        for (var i = 0; i < enabled.Count; i++)
        {
            for (var j = i + 1; j < enabled.Count; j++)
            {
                if (AreConnected(enabled[i], enabled[j], tolerance))
                {
                    adjacency[enabled[i].Id].Add(enabled[j].Id);
                    adjacency[enabled[j].Id].Add(enabled[i].Id);
                }
            }
        }

        var byId = enabled.ToDictionary(block => block.Id);
        var visited = new HashSet<int>();
        var components = new List<List<Block>>();
        foreach (var block in enabled)
        {
            if (visited.Contains(block.Id))
                continue;
            var component = new List<Block>();
            var queue = new Queue<int>();
            queue.Enqueue(block.Id);
            visited.Add(block.Id);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                component.Add(byId[current]);
                foreach (var neighbor in adjacency[current])
                {
                    if (visited.Contains(neighbor))
                        continue;
                    visited.Add(neighbor);
                    queue.Enqueue(neighbor);
                }
            }
            components.Add(component);
        }

        return components
            .OrderByDescending(component => component.Any(block => block.ShapeId == "core"))
            .ToList();
    }

    /// <summary>舰体总质量。</summary>
    public float TotalMass => _blocks.Where(block => block.Enabled).Sum(block => block.Spec.Mass);

    /// <summary>舰体包围盒半径（用于相机/碰撞粗略估算）。</summary>
    public float BoundingRadius()
    {
        if (_blocks.Count == 0)
            return 0.0f;
        var center = CenterOfMass();
        return _blocks.Where(block => block.Enabled)
            .Max(block => block.Position.DistanceTo(center) + block.Spec.Radius);
    }

    /// <summary>质心（按质量加权）。</summary>
    public Vector2 CenterOfMass()
    {
        var enabled = _blocks.Where(block => block.Enabled).ToList();
        if (enabled.Count == 0)
            return Vector2.Zero;
        var weighted = Vector2.Zero;
        var totalMass = 0.0f;
        foreach (var block in enabled)
        {
            weighted += block.Position * block.Spec.Mass;
            totalMass += block.Spec.Mass;
        }
        return totalMass > 0.0f ? weighted / totalMass : Vector2.Zero;
    }

    /// <summary>重置为默认参考舰（一个核心 + 若干模块，与现有 SeedDemoLayout 一致的简化版）。</summary>
    public void SeedReferenceShip()
    {
        _blocks.Clear();
        _nextId = 1;
        var core = AddBlock("core", Vector2.Zero, 0.0f);
        AddBlock("hull", new Vector2(0.0f, -46.0f), 0.0f);
        AddBlock("hull", new Vector2(0.0f, 46.0f), 0.0f);
        AddBlock("hull", new Vector2(46.0f, 0.0f), 0.0f);
        AddBlock("thruster", new Vector2(-66.0f, -40.0f), Mathf.Pi);
        AddBlock("thruster", new Vector2(-66.0f, 40.0f), Mathf.Pi);
        AddBlock("laser", new Vector2(88.0f, 0.0f), Mathf.Pi * 0.5f);
        AddBlock("reactor", new Vector2(88.0f, 40.0f), 0.0f);
        Changed?.Invoke();
    }
}
