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
public sealed partial class ModularShip
{
    /// <summary>一个已放置的方块（照搬逆向 Block：Id + Position + Angle + Shape + Features + Ports + Health）。</summary>
    public sealed class Block
    {
        private Vector2 _position;
        private float _angle;
        private bool _enabled = true;
        internal Action? GeometryChanged;
        public int Id { get; init; }
        public required string ShapeId { get; init; }
        /// <summary>Reassembly 数字形状 ID（照搬逆向 Block.Id，引用 TheShapes 表）。</summary>
        public int NumId => Spec.NumId;
        /// <summary>世界位置（连续浮点，照搬逆向 Position）。</summary>
        public Vector2 Position
        {
            get => _position;
            set { if (_position == value) return; _position = value; GeometryChanged?.Invoke(); }
        }
        /// <summary>朝向（任意弧度，照搬逆向 Angle）。</summary>
        public float Angle
        {
            get => _angle;
            set { if (_angle == value) return; _angle = value; GeometryChanged?.Invoke(); }
        }
        /// <summary>是否启用（照搬逆向 Enabled）。</summary>
        public bool Enabled
        {
            get => _enabled;
            set { if (_enabled == value) return; _enabled = value; GeometryChanged?.Invoke(); }
        }
        /// <summary>当前生命值（照搬逆向 Health/MaxHealth，被击中后下降）。</summary>
        public float Health { get; set; }

        public ShipShapeLibrary.ShapeSpec Spec => ShipShapeLibrary.Get(ShapeId);

        /// <summary>特征标志（照搬逆向 features 位标志）。</summary>
        public BlockFeatures Features => Spec.Features;

        /// <summary>
        /// 端口连接状态（照搬逆向 Port.Connected）：每个端口是否已与另一方块的端口对接。
        /// 一个端口只能连一个方块（PortPair.CanConnect 一对一）。由 ModularShip.RefreshConnections 维护。
        /// </summary>
        internal bool[]? PortConnected;

        /// <summary>端口是否已连接（照搬逆向 Port.IsFree = !Connected）。</summary>
        public bool IsPortConnected(int portIndex)
            => PortConnected is not null && portIndex >= 0 && portIndex < PortConnected.Length && PortConnected[portIndex];

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
    /// <summary>几何与可用执行器发生变化时递增；飞行/绘制缓存共用。</summary>
    public ulong Revision { get; private set; }
    private void InvalidateGeometry() => Revision++;

    public Block? Core
    {
        get
        {
            for (var i = 0; i < _blocks.Count; i++)
            {
                var block = _blocks[i];
                if (block.Spec.IsCore && block.Enabled && block.Health > 0) return block;
            }
            return null;
        }
    }

    /// <summary>添加方块并刷新端口连接状态（放置后自动与重合的空闲端口对接）。</summary>
    public Block AddBlock(string shapeId, Vector2 position, float angle)
    {
        if (!ShipShapeLibrary.Shapes.ContainsKey(shapeId))
            return null!;
        var block = new Block { Id = _nextId++, ShapeId = shapeId, Position = position, Angle = angle,
            Health = ShipShapeLibrary.Get(shapeId).Hull };
        _blocks.Add(block);
        block.GeometryChanged = InvalidateGeometry;
        InvalidateGeometry();
        RefreshConnections();
        Changed?.Invoke();
        return block;
    }

    public bool RemoveBlock(Block block)
    {
        if (block.Spec.IsCore)
            return false;
        if (!_blocks.Remove(block))
            return false;
        block.GeometryChanged = null;
        InvalidateGeometry();
        RefreshConnections();
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// 重算所有方块的端口连接状态（对齐逆向 connectPorts + PortPair.CanConnect 一对一语义）。
    /// 规则：两方块端口世界位置重合（距离 ≤ 容差）即连接；一个端口只能连一个方块（先匹配先占用）。
    /// 放置/删除/拖拽后调用，使 IsPortConnected 反映真实连接，而非每次距离推断。
    /// </summary>
    public void RefreshConnections(float tolerance = 1.5f)
    {
        foreach (var block in _blocks)
        {
            var ports = block.Spec.Ports;
            block.PortConnected = new bool[ports.Count];
        }

        var enabled = _blocks.Where(b => b.Enabled).ToList();
        for (var i = 0; i < enabled.Count; i++)
        {
            var a = enabled[i];
            var sa = a.Spec;
            for (var j = i + 1; j < enabled.Count; j++)
            {
                var b = enabled[j];
                var sb = b.Spec;
                for (var pa = 0; pa < sa.Ports.Count; pa++)
                {
                    if (a.IsPortConnected(pa))
                        continue;
                    var wa = a.Position + sa.Ports[pa].LocalPosition.Rotated(a.Angle);
                    for (var pb = 0; pb < sb.Ports.Count; pb++)
                    {
                        if (b.IsPortConnected(pb))
                            continue;
                        var wb = b.Position + sb.Ports[pb].LocalPosition.Rotated(b.Angle);
                        var normalA = sa.Ports[pa].WorldNormal(a.Angle);
                        var normalB = sb.Ports[pb].WorldNormal(b.Angle);
                        // Reassembly 端口是有方向的：位置重合还不够，两个节点必须
                        // 面向彼此，避免背面或交叉端口被误判为结构连接。
                        if (wa.DistanceSquaredTo(wb) <= tolerance * tolerance &&
                            sa.Ports[pa].CanConnect(sb.Ports[pb]) &&
                            normalA.Dot(normalB) <= -0.999f)
                        {
                            a.PortConnected![pa] = true;
                            b.PortConnected![pb] = true;
                            break;
                        }
                    }
                }
            }
        }
    }

    /// <summary>深拷贝一份舰体（含方块与运行时耐久）。</summary>
    public ModularShip Clone()
    {
        var copy = new ModularShip();
        copy._nextId = _nextId;
        foreach (var block in _blocks)
        {
            var cloned = block.Clone();
            cloned.GeometryChanged = copy.InvalidateGeometry;
            copy._blocks.Add(cloned);
        }
        copy.RefreshConnections();
        return copy;
    }

    /// <summary>重置所有方块耐久到形状满值。</summary>
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

    public float TotalTorquerTorque => GetMainComponent().Where(b => b.Enabled).Sum(b => b.Spec.TorquerTorque);

    /// <summary>各多边形自身惯量加平行轴项；单位与挂点的 r×F 一致。</summary>
    public float MomentOfInertia()
    {
        var com = CenterOfMass();
        var moment = 0.0f;
        foreach (var block in _blocks.Where(b => b.Enabled))
        {
            var vertices = block.Spec.Vertices();
            var area2 = 0.0f;
            var integral = 0.0f;
            for (var i = 0; i < vertices.Length; i++)
            {
                var a = vertices[i];
                var b = vertices[(i + 1) % vertices.Length];
                var cross = a.Cross(b);
                area2 += cross;
                integral += cross * (a.Dot(a) + a.Dot(b) + b.Dot(b));
            }
            var own = Mathf.Abs(area2) > 0.0001f ? integral / (6.0f * area2) : 0.0f;
            moment += block.Spec.Mass * (own + block.Position.DistanceSquaredTo(com));
        }
        return Mathf.Max(1.0f, moment);
    }

    /// <summary>凸多边形 SAT：共边允许接触，实际重叠则拒绝。渲染、命中、吸附共用顶点。</summary>
    public bool CanPlace(string shapeId, Vector2 position, float angle, Block? ignore = null)
    {
        var candidate = ShipShapeLibrary.Get(shapeId).Vertices().Select(v => position + v.Rotated(angle)).ToArray();
        foreach (var other in _blocks.Where(b => b.Enabled && b != ignore))
        {
            var polygon = other.Spec.Vertices().Select(v => other.Position + v.Rotated(other.Angle)).ToArray();
            if (Overlaps(candidate, polygon)) return false;
        }
        return true;
    }

    private static bool Overlaps(Vector2[] a, Vector2[] b)
    {
        foreach (var polygon in new[] { a, b })
        {
            for (var i = 0; i < polygon.Length; i++)
            {
                var edge = polygon[(i + 1) % polygon.Length] - polygon[i];
                var axis = new Vector2(-edge.Y, edge.X).Normalized();
                var aMin = a.Min(p => p.Dot(axis));
                var aMax = a.Max(p => p.Dot(axis));
                var bMin = b.Min(p => p.Dot(axis));
                var bMax = b.Max(p => p.Dot(axis));
                if (Mathf.Min(aMax, bMax) - Mathf.Max(aMin, bMin) <= 0.01f) return false;
            }
        }
        return true;
    }

    public void Restore(ModularShip source)
    {
        var copies = source._blocks.Select(b => b.Clone()).ToArray();
        foreach (var block in _blocks) block.GeometryChanged = null;
        _blocks.Clear();
        _blocks.AddRange(copies);
        foreach (var block in _blocks) block.GeometryChanged = InvalidateGeometry;
        InvalidateGeometry();
        _nextId = source._nextId;
        RefreshConnections();
        Changed?.Invoke();
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
                var normalA = portA.WorldNormal(a.Angle);
                var normalB = portB.WorldNormal(b.Angle);
                if (posA.DistanceTo(posB) <= tolerance &&
                    portA.CanConnect(portB) && normalA.Dot(normalB) <= -0.999f)
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
            .OrderByDescending(component => component.Any(block => block.Spec.IsCore))
            .ToList();
    }

    /// <summary>舰体总质量。</summary>
    public float TotalMass => _blocks.Where(block => block.Enabled).Sum(block => block.Spec.Mass);

    /// <summary>含核心的连通分量（舰船主体）。无核心时返回第一个分量或空。</summary>
    public List<Block> GetMainComponent(float tolerance = 1.5f)
    {
        var components = GetConnectedComponents(tolerance);
        return components.FirstOrDefault(component => component.Any(block => block.Spec.IsCore))
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
        var center = CenterOfMass();
        var radius = 0.0f;
        foreach (var block in _blocks)
            if (block.Enabled)
                radius = Mathf.Max(radius, block.Position.DistanceTo(center) + block.Spec.Radius);
        return radius;
    }

    /// <summary>质心（按质量加权）。</summary>
    public Vector2 CenterOfMass()
    {
        var weighted = Vector2.Zero;
        var totalMass = 0.0f;
        foreach (var block in _blocks)
        {
            if (!block.Enabled) continue;
            weighted += block.Position * block.Spec.Mass;
            totalMass += block.Spec.Mass;
        }
        return totalMass > 0.0f ? weighted / totalMass : Vector2.Zero;
    }

    /// <summary>重置为默认参考舰：用端口吸附链生成，保证所有连接端口严格对齐。</summary>
    public void SeedReferenceShip()
    {
        foreach (var block in _blocks) block.GeometryChanged = null;
        _blocks.Clear();
        InvalidateGeometry();
        _nextId = 1;
        var core = AddBlock("core", Vector2.Zero, 0.0f);
        var stern = AttachTo(core, 5, "hull", 0);
        var upper = AttachTo(stern, 3, "hull", 1);
        var lower = AttachTo(stern, 1, "hull", 3);
        var bow = AttachTo(core, 2, "hull", 2);
        // 引擎仅用安装面 (+X) 连接；尾部两台同向，差动产生相反的转向力矩。
        AttachTo(upper, 2, "thruster", 0);
        AttachTo(lower, 2, "thruster", 0);
        AttachTo(bow, 0, "cannon", 2);
        AttachTo(bow, 3, "gyro", 1);
        AttachTo(bow, 1, "hyperdrive", 3);
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
