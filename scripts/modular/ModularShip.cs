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
        /// <summary>运行时耐久（默认等于形状 Hull，被击中后下降）。</summary>
        public float Health { get; set; }

        public ShipShapeLibrary.ShapeSpec Spec => ShipShapeLibrary.Get(ShapeId);

        public Block Clone() => new()
        {
            Id = Id,
            ShapeId = ShapeId,
            Position = Position,
            Angle = Angle,
            Enabled = Enabled,
            Health = Health,
        };
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

    /// <summary>深拷贝一份舰体（含方块与运行时耐久）。</summary>
    public ModularShip Clone()
    {
        var copy = new ModularShip();
        copy._nextId = _nextId;
        foreach (var block in _blocks)
            copy._blocks.Add(block.Clone());
        return copy;
    }

    /// <summary>重置所有方块耐久到形状满值（进入试航前调用）。</summary>
    public void ResetHealth()
    {
        foreach (var block in _blocks)
        {
            block.Health = block.Spec.Hull;
            block.Enabled = true;
        }
    }

    /// <summary>一个推进器挂点（用于运行时推力/转向计算）。</summary>
    public readonly record struct ThrusterMount(int BlockId, Vector2 Offset, Vector2 Force, float Power);

    /// <summary>一个武器挂点（用于运行时开火）。</summary>
    public readonly record struct WeaponMount(Vector2 Offset, Vector2 Direction, float Damage);

    /// <summary>
    /// 计算所有可用的推进器挂点（对齐 Reassembly「推进器在挂点施力」）。
    /// 挂点偏移以质心为原点，力方向 = 形状 ThrustDirection 旋转方块朝向，
    /// 推力 = 形状 Thrust。只统计 Enabled 且连通到主体的推进器。
    /// </summary>
    public List<ThrusterMount> GetThrusterMounts(float tolerance = 1.5f)
    {
        var com = CenterOfMass();
        var mainComponent = GetMainComponent(tolerance);
        var mounts = new List<ThrusterMount>();
        foreach (var block in _blocks)
        {
            if (!block.Enabled || !block.Spec.IsThruster)
                continue;
            if (!mainComponent.Contains(block))
                continue;
            var force = block.Spec.ThrustDirection.Rotated(block.Angle);
            mounts.Add(new ThrusterMount(block.Id, block.Position - com, force, block.Spec.Thrust));
        }
        return mounts;
    }

    /// <summary>计算所有可用的武器挂点（对齐 Reassembly「武器沿朝向发射」）。</summary>
    public List<WeaponMount> GetWeaponMounts(float tolerance = 1.5f)
    {
        var com = CenterOfMass();
        var mainComponent = GetMainComponent(tolerance);
        var mounts = new List<WeaponMount>();
        foreach (var block in _blocks)
        {
            if (!block.Enabled || !block.Spec.IsWeapon)
                continue;
            if (!mainComponent.Contains(block))
                continue;
            var dir = block.Spec.WeaponDirection.Rotated(block.Angle);
            mounts.Add(new WeaponMount(block.Position - com, dir, block.Spec.Damage));
        }
        return mounts;
    }

    /// <summary>是否含核心且核心存活。</summary>
    public bool HasLiveCore => Core is not null;

    /// <summary>总火力（存活武器伤害之和）。</summary>
    public float TotalDamage => GetWeaponMounts().Sum(m => m.Damage);

    /// <summary>总推力（存活推进器推力之和）。</summary>
    public float TotalThrust => GetThrusterMounts().Sum(m => m.Power);

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

    /// <summary>含核心的连通分量（舰船主体）。无核心时返回第一个分量或空。</summary>
    public List<Block> GetMainComponent(float tolerance = 1.5f)
    {
        var components = GetConnectedComponents(tolerance);
        return components.FirstOrDefault(component => component.Any(block => block.ShapeId == "core"))
            ?? components.FirstOrDefault()
            ?? new List<Block>();
    }

    /// <summary>点-多边形命中测试：返回世界坐标命中的存活方块（优先上层，即后放置的）。</summary>
    public Block? GetBlockAt(Vector2 worldPos)
    {
        for (var i = _blocks.Count - 1; i >= 0; i--)
        {
            var block = _blocks[i];
            if (!block.Enabled)
                continue;
            if (PointInPolygon(worldPos, block))
                return block;
        }
        return null;
    }

    /// <summary>
    /// 世界坐标命中测试（舰体整体有位移 + 旋转）。
    /// shipWorldPos 是舰体质心世界位置，heading 是舰体朝向弧度。
    /// </summary>
    public Block? GetBlockAtWorld(Vector2 worldPos, Vector2 shipWorldPos, float heading)
    {
        var local = CenterOfMass() + (worldPos - shipWorldPos).Rotated(-heading);
        return GetBlockAt(local);
    }

    /// <summary>判断世界坐标是否落在某方块的（旋转后）多边形内。</summary>
    private static bool PointInPolygon(Vector2 point, Block block)
    {
        var spec = block.Spec;
        var vertices = spec.Vertices();
        // 把点变换到方块本地空间（未旋转），再做多边形测试。
        var local = (point - block.Position).Rotated(-block.Angle);
        var inside = false;
        for (int i = 0, j = vertices.Length - 1; i < vertices.Length; j = i++)
        {
            var vi = vertices[i];
            var vj = vertices[j];
            if ((vi.Y > local.Y) != (vj.Y > local.Y) &&
                local.X < (vj.X - vi.X) * (local.Y - vi.Y) / (vj.Y - vi.Y) + vi.X)
                inside = !inside;
        }
        return inside;
    }

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

    /// <summary>重置为默认参考舰：用端口吸附链生成，保证所有连接端口严格对齐。</summary>
    public void SeedReferenceShip()
    {
        _blocks.Clear();
        _nextId = 1;
        var core = AddBlock("core", Vector2.Zero, 0.0f);
        // 主体：核心四个方向接装甲骨架。
        var hullUp = AttachTo(core, 4, "hull", 1);     // 核心上端口(270°) ↔ hull 左下端口(135°)
        var hullDown = AttachTo(core, 1, "hull", 1);   // 核心下端口(90°)
        var hullRight = AttachTo(core, 5, "hull", 1);  // 核心右上端口(330°)
        // 推进器：核心左下/左上端口接推进器左下端口。
        AttachTo(core, 2, "thruster", 1);
        AttachTo(core, 3, "thruster", 1);
        // 武器与能源：接在右侧装甲骨架的远端端口上。
        AttachTo(hullRight, 0, "laser", 2);
        AttachTo(hullDown, 0, "reactor", 2);
        Changed?.Invoke();
    }

    /// <summary>
    /// 把一个新方块吸附到 host 方块的指定端口上：
    /// 选中方块用 selfPortIndex 端口，对接 host 的 hostPortIndex 端口，
    /// 用 SnapBlock 计算位置与朝向（两端口重合 + 法线反向 = 边贴合）。
    /// </summary>
    private Block AttachTo(Block host, int hostPortIndex, string shapeId, int selfPortIndex)
    {
        var selfSpec = ShipShapeLibrary.Get(shapeId);
        var hostPort = host.Spec.Ports[hostPortIndex % host.Spec.Ports.Count];
        var selfPort = selfSpec.Ports[selfPortIndex % selfSpec.Ports.Count];
        var (pos, ang) = SnapBlock(selfPort, hostPort, host.Position, host.Angle);
        return AddBlock(shapeId, pos, ang);
    }
}
