using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// =============================================================================
// 照搬 Reassembly 逆向数据结构（见 Reassembly/scripts/Block.cs）
//   - PortSpec：Offset + Direction + Type（端口在边上 + 法线 + 类型）
//   - BlockFeatures：位标志（COMMAND/GENERATOR/THRUSTER/WEAPON/TURRET/...）
//   - ShapeSpec：Vertices + Ports + Radius + HasCircleShape + Scale
// 本文件把逆向的纯数据结构落地为可运行代码，供建造编辑器与运行时渲染共用。
// =============================================================================

/// <summary>ReWorld 内部能力标志；并非原版的数值 ABI。</summary>
[Flags]
public enum BlockFeatures
{
    None = 0,
    Command = 1 << 0,        // COMMAND（指挥/核心）
    Generator = 1 << 1,      // GENERATOR（发电机）
    Environmental = 1 << 2,  // ENVIRONMENTAL（环境/装饰）
    Thruster = 1 << 3,       // 推进器
    Torquer = 1 << 13,        // TORQUER：纯力矩转向器
    Weapon = 1 << 4,         // 武器
    Turret = 1 << 5,         // 炮塔
    Shield = 1 << 6,         // 护盾
    Launcher = 1 << 7,       // 发射器（导弹/无人机）
    Factory = 1 << 8,        // 工厂
    Tractor = 1 << 9,        // 牵引器
    Healer = 1 << 10,        // 修理
    Vegetable = 1 << 11,     // 植物
    Transient = 1 << 12,     // 临时
    Hyperdrive = 1 << 14,    // ReWorld 跨星系迁跃能力（项目自定义）
}

public static class ShipShapeLibrary
{
    /// <summary>原版 EPort_ 注册值，0x140235c7b–0x140235ceb；只列当前使用的类型。</summary>
    public enum PortType
    {
        Normal = 1,
        ThrusterOut = 8,
        ThrusterIn = 16,
    }

    /// <summary>
    /// 端口规格（照搬逆向 PortSpec：Offset + Direction + Type）。
    /// 端口在多边形边的中点，Direction 是边外法线（径向单位向量）。
    /// 两方块对接 = 两个 PortSpec 的世界 Offset 重合且 Direction 反向。
    /// </summary>
    public readonly record struct Port(Vector2 Offset, Vector2 Direction, PortType Type = PortType.Normal, bool CanAttach = true)
    {
        /// <summary>端口在形状本地坐标（未旋转）下的位置（边中点）。兼容旧 API。</summary>
        public Vector2 LocalPosition => Offset;
        /// <summary>端口朝向（单位外法线）。兼容旧 API。</summary>
        public Vector2 Normal => Direction;

        /// <summary>把端口变换到方块旋转+平移后的世界位置。</summary>
        public Vector2 WorldPosition(Vector2 blockPos, float blockAngle) =>
            blockPos + Offset.Rotated(blockAngle);
        /// <summary>把端口法线变换到方块旋转后的世界法线。</summary>
        public Vector2 WorldNormal(float blockAngle) =>
            Direction.Rotated(blockAngle);

        /// <summary>
        /// 原版 0x1401e3c10：按两个类型的 OR 查询兼容表，并非类型相等。
        /// NORMAL+THRUSTER_IN 可安装，THRUSTER_OUT+THRUSTER_IN 可串联。
        /// </summary>
        public bool CanConnect(Port other) => CanAttach && other.CanAttach &&
            (int)(Type | other.Type) is 1 or 6 or 17 or 24 or 33 or 129 or 160;
    }

    /// <summary>
    /// 方块形状规格（照搬逆向 ShapeSpec：Vertices + Ports + Radius + Scale）。
    /// NumId 是方块类型 ID，几何形状与方块类型分别定义。
    /// </summary>
    public sealed class ShapeSpec
    {
        public required string Id { get; init; }              // 内部字符串 ID（兼容代码可读性）
        public required int NumId { get; init; }             // Reassembly BlockType ID（如 200）
        public required string Label { get; init; }
        public required string Category { get; init; }
        public required uint ColorUint { get; init; }        // 照搬逆向 uint 颜色（0xRRGGBB）
        public Color Color => UintToColor(ColorUint);
        /// <summary>多边形顶点（本地坐标，照搬逆向 Vertices）。非空时优先使用。</summary>
        public Vector2[] VerticesField { get; init; } = Array.Empty<Vector2>();
        /// <summary>该形状上的端口列表（照搬逆向 Ports）。</summary>
        public List<Port> Ports { get; init; } = new();
        /// <summary>形状半径（照搬逆向 Radius，用于碰撞/渲染 bbox）。</summary>
        public float Radius { get; init; } = 22.0f;
        /// <summary>形状缩放（照搬逆向 SerialShapeScale）。</summary>
        public float Scale { get; init; } = 1.0f;
        /// <summary>是否圆形形状（照搬逆向 hasCircleShape）。</summary>
        public bool HasCircleShape { get; init; }
        /// <summary>特征标志（照搬逆向 features 位标志）。</summary>
        public BlockFeatures Features { get; init; } = BlockFeatures.None;

        // —— 兼容旧 API ——
        public string ColorHex => $"#{ColorUint & 0xFFFFFF:X6}";
        public bool IsCore => (Features & BlockFeatures.Command) != 0;
        public bool IsThruster => (Features & BlockFeatures.Thruster) != 0;
        public bool IsWeapon => (Features & BlockFeatures.Weapon) != 0;

        /// <summary>质量（影响引力与惯性）。</summary>
        public float Mass { get; init; } = 5.0f;
        /// <summary>耐久。</summary>
        public float Hull { get; init; } = 20.0f;
        /// <summary>推力（推进器）。</summary>
        public float Thrust { get; init; }
        /// <summary>Reassembly torquerTorque：只施加角力矩，不产生线性推力。</summary>
        public float TorquerTorque { get; init; }
        public Vector2 ExhaustPosition { get; init; }
        public float ExhaustHalfWidth { get; init; }
        /// <summary>武器伤害。</summary>
        public float Damage { get; init; }

        /// <summary>施力方向（本地 +X）；排气方向相反。</summary>
        public Vector2 ThrustDirection { get; init; } = Vector2.Right;
        /// <summary>炮口方向（本地，未旋转）。</summary>
        public Vector2 WeaponDirection { get; init; } = Vector2.Right;

        /// <summary>顶点（兼容旧 API：若无 VerticesField 则按 Sides 生成正多边形）。</summary>
        public Vector2[] Vertices()
        {
            if (VerticesField.Length > 0) return VerticesField;
            return _regularPolyVertices(Sides, Radius, StartAngle);
        }

        // —— 兼容旧字段（仅在 VerticesField 为空时用于自动生成正多边形）——
        public int Sides { get; init; } = 4;
        public float StartAngle { get; init; } = -Mathf.Pi * 0.5f;

        private static Vector2[] _regularPolyVertices(int sides, float radius, float startAngle)
        {
            var verts = new Vector2[sides];
            for (var i = 0; i < sides; i++)
            {
                var angle = startAngle + i * Mathf.Tau / sides;
                verts[i] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
            }
            return verts;
        }
    }

    /// <summary>uint 颜色 → Godot Color（照搬逆向 Faction.UintToColor）。</summary>
    public static Color UintToColor(uint c) => new(
        ((c >> 16) & 0xFF) / 255.0f,
        ((c >> 8) & 0xFF) / 255.0f,
        (c & 0xFF) / 255.0f);

    /// <summary>
    /// 方块目录。原版已验证形状与 ReWorld 功能调参共用的运行时规格。
    /// 端口均匀分布在多边形边的中点，法线即边外法线（径向），
    /// 因此方块能沿任意边连接，组合出任意角度的多边形舰体。
    /// 正多边形共用边长；引擎梯形与 COMMAND 核心使用已提取的专用顶点。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, ShapeSpec> Shapes =
        new Dictionary<string, ShapeSpec>(StringComparer.Ordinal)
        {
            // shape 8：前部削角的 COMMAND 核心（insertLegacy），并非正六边形。
            ["core"] = MakeCommand(),
            // 219 Hull：装甲骨架，方形（BlockType 219）。
            ["hull"] = MakeRegular("hull", 219, "装甲骨架", "骨架", 0xA7B5C4,
                BlockFeatures.None, 4, mass: 7.0f, hull: 40.0f),
            // 复合装甲：保留 ReWorld 耐久调参。
            ["armor"] = MakeRegular("armor", 248, "复合装甲", "骨架", 0x8998A8,
                BlockFeatures.None, 4, mass: 10.0f, hull: 70.0f),
            // 源数据见 Reassembly/analysis/engine_geometry.json：梯形，不是矩形近似。
            ["thruster"] = MakeThruster("thruster", 202, "小型引擎", 2, 4.5f, 4.194999695f, 130, 20_000, 5, 25),
            ["thruster_medium"] = MakeThruster("thruster_medium", 203, "中型引擎", 3, 7.5f, 10, 150, 40_000, 8, 38),
            ["thruster_large"] = MakeThruster("thruster_large", 204, "大型引擎", 4, 10, 15, 150, 80_000, 12, 56),
            // 姿态陀螺：小六边形。
            ["gyro"] = MakeRegular("gyro", 217, "姿态陀螺", "机动", 0xB58BE3,
                BlockFeatures.Torquer, 6, mass: 4.0f, hull: 25.0f, torquerTorque: 120_000.0f),
            // 脉冲激光：方形（炮口指向 +X）。
            ["laser"] = MakeRegular("laser", 251, "脉冲激光", "武器", 0xF07C8E,
                BlockFeatures.Weapon, 4, mass: 5.0f, hull: 22.0f, damage: 6.0f,
                weaponDir: Vector2.Right),
            // 动能炮：方形。
            ["cannon"] = MakeRegular("cannon", 218, "动能炮", "武器", 0xD97676,
                BlockFeatures.Weapon, 4, mass: 6.0f, hull: 30.0f, damage: 4.0f,
                weaponDir: Vector2.Right),
            // 连续光束：方形。
            ["beam"] = MakeRegular("beam", 252, "连续光束", "武器", 0xFF7BB0,
                BlockFeatures.Weapon, 4, mass: 6.0f, hull: 20.0f, damage: 8.0f,
                weaponDir: Vector2.Right),
            // 磁轨炮：六边形（长条形武器）。
            ["railgun"] = MakeRegular("railgun", 228, "磁轨炮", "武器", 0xE9A56A,
                BlockFeatures.Weapon, 6, mass: 8.0f, hull: 28.0f, damage: 10.0f,
                weaponDir: Vector2.Right),
            // 导弹舱：方形。
            ["missile"] = MakeRegular("missile", 249, "导弹舱", "武器", 0xD8D27A,
                BlockFeatures.Weapon | BlockFeatures.Launcher, 4, mass: 7.0f, hull: 24.0f, damage: 14.0f,
                weaponDir: Vector2.Right),
            // 偏转护盾：八边形近似圆形。
            ["shield"] = MakeRegular("shield", 247, "偏转护盾", "防御", 0x7BB7E8,
                BlockFeatures.Shield, 8, mass: 6.0f, hull: 30.0f),
            // 点防御炮：小六边形。
            ["point_defense"] = MakeRegular("point_defense", 263, "点防御炮", "防御", 0xE28B8B,
                BlockFeatures.Weapon, 6, mass: 4.0f, hull: 18.0f, damage: 3.0f,
                weaponDir: Vector2.Right),
            // 205 Generator：裂变炉，方形（BlockType 205）。
            ["reactor"] = MakeRegular("reactor", 205, "裂变炉", "能源", 0xE0B45D,
                BlockFeatures.Generator, 4, mass: 10.0f, hull: 45.0f),
            // 聚变发电机：三级方形。
            ["generator"] = MakeRegular("generator", 207, "聚变发电机", "能源", 0xF0B85E,
                BlockFeatures.Generator, 4, mass: 7.0f, hull: 35.0f, nativeScale: 3),
            // 武器电容：八边形。
            ["capacitor"] = MakeRegular("capacitor", 261, "武器电容", "能源", 0x6FA7D8,
                BlockFeatures.None, 8, mass: 4.0f, hull: 18.0f),
            // 深空传感器：六边形。
            ["sensor"] = MakeRegular("sensor", 262, "深空传感器", "系统", 0x72C7C2,
                BlockFeatures.None, 6, mass: 3.0f, hull: 18.0f),
            // Project-specific travel equipment; this is not a decoded Reassembly block.
            ["hyperdrive"] = MakeRegular("hyperdrive", 30001, "迁跃引擎", "系统", 0x77D6DC,
                BlockFeatures.Hyperdrive, 4, mass: 8.0f, hull: 35.0f),
            // 散热翼：六边形。
            ["radiator"] = MakeRegular("radiator", 416, "散热翼", "散热", 0x6EC6C7,
                BlockFeatures.None, 6, mass: 3.0f, hull: 18.0f),
            // 货运舱：八边形。
            ["cargo"] = MakeRegular("cargo", 201, "货运舱", "物流", 0xD0A46A,
                BlockFeatures.None, 8, mass: 8.0f, hull: 50.0f),
            // 牵引光束：二级八边形。
            ["tractor"] = MakeRegular("tractor", 275, "牵引光束", "物流", 0xC58CDA,
                BlockFeatures.Tractor, 8, mass: 5.0f, hull: 22.0f, nativeScale: 2),
        };

    /// <summary>
    /// 为 n 边正多边形生成端口（照搬逆向 insertRegularPoly + PortPos）：
    /// 端口放在边的中点，Direction 是边外法线（径向单位向量）。
    /// 端口距离 = circumradius * cos(π/n)（边中点到中心的距离），保证端口正好落在边上。
    /// </summary>
    private static List<Port> PortsForSides(int sides, float radius, float startAngle = -Mathf.Pi * 0.5f)
    {
        var ports = new List<Port>();
        var halfStepRad = Mathf.Pi / sides; // 边中点相对顶点偏移半个步距。
        var distance = radius * Mathf.Cos(halfStepRad); // 边中点到中心的距离。
        for (var i = 0; i < sides; i++)
        {
            // 顶点从 startAngle 开始，所以第一条边的中点也必须以
            // startAngle + π/n 开始。此前遗漏 startAngle，端口整体偏转 90°，
            // 造成“节点看似重合但不在边上”。
            var angle = startAngle + halfStepRad + i * Mathf.Tau / sides;
            var offset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * distance;
            var direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)).Normalized(); // 边外法线（径向）。
            ports.Add(new Port(offset, direction));
        }
        return ports;
    }

    /// <summary>构造一个正多边形形状规格（便捷工厂，对齐逆向 insertRegularPoly）。</summary>
    private static ShapeSpec MakeRegular(
        string id, int numId, string label, string category, uint colorUint,
        BlockFeatures features, int sides,
        float mass = 5.0f, float hull = 20.0f,
        float thrust = 0.0f, float damage = 0.0f,
        Vector2? thrustDir = null, Vector2? weaponDir = null, float torquerTorque = 0.0f, int nativeScale = 1)
    {
        // 共用 10 个原版单位的边长（显示比例为 2），不能为每个功能随意指定半径。
        // 正方形、三角形、六边形因此具有相同接缝长度。
        var sideLength = 10.0f * DisplayScale * nativeScale;
        var circumradius = sideLength / (2.0f * Mathf.Sin(Mathf.Pi / sides));
        var startAngle = sides % 2 == 0 ? -Mathf.Pi / sides : 0.0f;
        var vertices = new Vector2[sides];
        for (var i = 0; i < sides; i++)
        {
            var angle = startAngle + i * Mathf.Tau / sides;
            vertices[i] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * circumradius;
        }
        return new ShapeSpec
        {
            Id = id,
            NumId = numId,
            Label = label,
            Category = category,
            ColorUint = colorUint,
            Features = features,
            Sides = sides,
            Scale = nativeScale,
            Radius = circumradius,
            VerticesField = vertices,
            Ports = PortsForSides(sides, circumradius, startAngle),
            Mass = mass,
            Hull = hull,
            Thrust = thrust,
            Damage = damage,
            TorquerTorque = torquerTorque,
            ThrustDirection = thrustDir ?? Vector2.Right,
            WeaponDirection = weaponDir ?? Vector2.Right,
        };
    }

    public const float DisplayScale = 2.0f;

    /// <summary>0x14023a390 的梯形公式，0x140238a89 起的三档常量。均减去顶点均值。</summary>
    private static ShapeSpec MakeThruster(string id, int numId, string label, int scale,
        float baseLength, float legLength, float degrees, float thrust, float mass, float hull)
    {
        var angle = Mathf.DegToRad(degrees);
        var x = -Mathf.Sin(angle) * legLength;
        var y = Mathf.Cos(angle) * legLength;
        var center = new Vector2(x * 0.5f, baseLength * 0.5f);
        var vertices = new[]
        {
            (Vector2.Zero - center) * DisplayScale,
            (new Vector2(x, y) - center) * DisplayScale,
            (new Vector2(x, baseLength - y) - center) * DisplayScale,
            (new Vector2(0, baseLength) - center) * DisplayScale,
        };
        // shape 18 的跳转表选择 IN=16 / OUT=8（不是 WEAPON_OUT=2）。
        // 尾口可接下一台引擎的前口；普通装甲不能堵住尾口。
        var ports = new List<Port>
        {
            new((vertices[3] + vertices[0]) * 0.5f, Vector2.Right, PortType.ThrusterIn),
            new((vertices[1] + vertices[2]) * 0.5f, Vector2.Left, PortType.ThrusterOut),
        };
        return new ShapeSpec
        {
            Id = id, NumId = numId, Label = label, Category = "机动", ColorUint = 0x0C4E61,
            Features = BlockFeatures.Thruster, VerticesField = vertices, Ports = ports,
            Radius = vertices.Max(v => v.Length()), Scale = scale, Sides = 4,
            ExhaustPosition = (vertices[1] + vertices[2]) * 0.5f,
            ExhaustHalfWidth = vertices[1].DistanceTo(vertices[2]) * 0.5f,
            Mass = mass, Hull = hull, Thrust = thrust, ThrustDirection = Vector2.Right,
        };
    }

    private static ShapeSpec MakeCommand()
    {
        // shape 8, scale 1: h=5, bevel=10*(sqrt(2)-1)/2.
        var h = 5.0f * DisplayScale;
        var bevel = (Mathf.Sqrt(2) - 1) * h;
        var raw = new[]
        {
            new Vector2(-h, h), new Vector2(bevel, h), new Vector2(h, bevel),
            new Vector2(h, -bevel), new Vector2(bevel, -h), new Vector2(-h, -h),
        };
        var mean = raw.Aggregate(Vector2.Zero, (sum, v) => sum + v) / raw.Length;
        var vertices = raw.Select(v => v - mean).ToArray();
        var ports = new List<Port>();
        for (var i = 0; i < vertices.Length; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % vertices.Length];
            var edge = b - a;
            ports.Add(new Port((a + b) * 0.5f, new Vector2(-edge.Y, edge.X).Normalized()));
        }
        return new ShapeSpec
        {
            Id = "core", NumId = 200, Label = "舰船核心", Category = "核心", ColorUint = 0x069A95,
            Features = BlockFeatures.Command, VerticesField = vertices, Ports = ports,
            Radius = vertices.Max(v => v.Length()), Sides = vertices.Length, Mass = 12, Hull = 60,
        };
    }

    /// <summary>获取形状，未知形状回退到装甲骨架。</summary>
    public static ShapeSpec Get(string id) => Shapes.TryGetValue(id, out var spec) ? spec : Shapes["hull"];

    /// <summary>建造面板的方块顺序（调色板按此顺序展示，鼠标点击选择）。</summary>
    public static readonly string[] BuildOrder =
    {
        "core", "hull", "armor", "thruster", "thruster_medium", "thruster_large", "gyro",
        "laser", "cannon", "beam", "railgun", "missile",
        "shield", "point_defense", "reactor", "generator", "capacitor",
        "sensor", "hyperdrive", "radiator", "cargo", "tractor",
    };
}
