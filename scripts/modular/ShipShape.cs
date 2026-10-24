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

/// <summary>方块特征标志（照搬逆向 features 位标志）。</summary>
[Flags]
public enum BlockFeatures
{
    None = 0,
    Command = 1 << 0,        // COMMAND（指挥/核心）
    Generator = 1 << 1,      // GENERATOR（发电机）
    Environmental = 1 << 2,  // ENVIRONMENTAL（环境/装饰）
    Thruster = 1 << 3,       // 推进器
    Weapon = 1 << 4,         // 武器
    Turret = 1 << 5,         // 炮塔
    Shield = 1 << 6,         // 护盾
    Launcher = 1 << 7,       // 发射器（导弹/无人机）
    Factory = 1 << 8,        // 工厂
    Tractor = 1 << 9,        // 牵引器
    Healer = 1 << 10,        // 修理
    Vegetable = 1 << 11,     // 植物
    Transient = 1 << 12,     // 临时
}

public static class ShipShapeLibrary
{
    /// <summary>
    /// 端口规格（照搬逆向 PortSpec：Offset + Direction + Type）。
    /// 端口在多边形边的中点，Direction 是边外法线（径向单位向量）。
    /// 两方块对接 = 两个 PortSpec 的世界 Offset 重合且 Direction 反向。
    /// </summary>
    public readonly record struct Port(Vector2 Offset, Vector2 Direction, int Type = 0)
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
    }

    /// <summary>
    /// 方块形状规格（照搬逆向 ShapeSpec：Vertices + Ports + Radius + Scale）。
    /// 每种形状对应 TheShapes 表里的一个数字 ID（NumId），如 200=Command Module。
    /// </summary>
    public sealed class ShapeSpec
    {
        public required string Id { get; init; }              // 内部字符串 ID（兼容代码可读性）
        public required int NumId { get; init; }             // Reassembly TheShapes 数字 ID（如 200）
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
        public bool IsCore => Features.HasFlag(BlockFeatures.Command);
        public bool IsThruster => Features.HasFlag(BlockFeatures.Thruster);
        public bool IsWeapon => Features.HasFlag(BlockFeatures.Weapon);

        /// <summary>质量（影响引力与惯性）。</summary>
        public float Mass { get; init; } = 5.0f;
        /// <summary>耐久。</summary>
        public float Hull { get; init; } = 20.0f;
        /// <summary>推力（推进器）。</summary>
        public float Thrust { get; init; }
        /// <summary>武器伤害。</summary>
        public float Damage { get; init; }

        /// <summary>推力方向（本地，未旋转；喷口指向，力沿 -ThrustDirection 施加）。</summary>
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
    /// 方块形状表（照搬逆向 TheShapes）。用正多边形 + 端口描述每种方块。
    /// 端口均匀分布在多边形边的中点，法线即边外法线（径向），
    /// 因此方块能沿任意边连接，组合出任意角度的多边形舰体。
    /// 数字 ID 取自逆向 blocks_table.md：200=Command, 202=Thruster, 219=Hull, 205=Generator...
    /// </summary>
    public static readonly IReadOnlyDictionary<string, ShapeSpec> Shapes =
        new Dictionary<string, ShapeSpec>(StringComparer.Ordinal)
        {
            // 200 Command Module：核心，六边形，各边都有端口（照搬逆向 features=0xAEF300 含 COMMAND）。
            ["core"] = MakeRegular("core", 200, "舰船核心", "核心", 0xF4F1DE,
                BlockFeatures.Command, 6, 22.0f, mass: 12.0f, hull: 60.0f),
            // 219 Hull：装甲骨架，方形（照搬逆向 features=0xAEF300）。
            ["hull"] = MakeRegular("hull", 219, "装甲骨架", "骨架", 0xA7B5C4,
                BlockFeatures.None, 4, 18.0f, mass: 7.0f, hull: 40.0f),
            // 复合装甲：方形，更厚。
            ["armor"] = MakeRegular("armor", 248, "复合装甲", "骨架", 0x8998A8,
                BlockFeatures.None, 4, 20.0f, mass: 10.0f, hull: 70.0f),
            // 202 Thruster：推进器，方形，推力沿 +X（照搬逆向 features=0x70A000 含 THRUSTER）。
            ["thruster"] = MakeRegular("thruster", 202, "推进器", "机动", 0xCF7BFF,
                BlockFeatures.Thruster, 4, 18.0f, mass: 5.0f, hull: 25.0f, thrust: 40.0f,
                thrustDir: Vector2.Left),
            // 姿态陀螺：小六边形。
            ["gyro"] = MakeRegular("gyro", 217, "姿态陀螺", "机动", 0xB58BE3,
                BlockFeatures.None, 6, 14.0f, mass: 4.0f, hull: 25.0f),
            // 脉冲激光：五边形（炮口指向 +X）。
            ["laser"] = MakeRegular("laser", 251, "脉冲激光", "武器", 0xF07C8E,
                BlockFeatures.Weapon, 5, 18.0f, mass: 5.0f, hull: 22.0f, damage: 6.0f,
                weaponDir: Vector2.Right),
            // 动能炮：方形。
            ["cannon"] = MakeRegular("cannon", 218, "动能炮", "武器", 0xD97676,
                BlockFeatures.Weapon, 4, 17.0f, mass: 6.0f, hull: 30.0f, damage: 4.0f,
                weaponDir: Vector2.Right),
            // 连续光束：五边形。
            ["beam"] = MakeRegular("beam", 252, "连续光束", "武器", 0xFF7BB0,
                BlockFeatures.Weapon, 5, 18.0f, mass: 6.0f, hull: 20.0f, damage: 8.0f,
                weaponDir: Vector2.Right),
            // 磁轨炮：六边形（长条形武器）。
            ["railgun"] = MakeRegular("railgun", 228, "磁轨炮", "武器", 0xE9A56A,
                BlockFeatures.Weapon, 6, 20.0f, mass: 8.0f, hull: 28.0f, damage: 10.0f,
                weaponDir: Vector2.Right),
            // 导弹舱：方形。
            ["missile"] = MakeRegular("missile", 249, "导弹舱", "武器", 0xD8D27A,
                BlockFeatures.Weapon | BlockFeatures.Launcher, 4, 18.0f, mass: 7.0f, hull: 24.0f, damage: 14.0f,
                weaponDir: Vector2.Right),
            // 偏转护盾：八边形近似圆形。
            ["shield"] = MakeRegular("shield", 247, "偏转护盾", "防御", 0x7BB7E8,
                BlockFeatures.Shield, 8, 16.0f, mass: 6.0f, hull: 30.0f),
            // 点防御炮：小六边形。
            ["point_defense"] = MakeRegular("point_defense", 263, "点防御炮", "防御", 0xE28B8B,
                BlockFeatures.Weapon, 6, 12.0f, mass: 4.0f, hull: 18.0f, damage: 3.0f,
                weaponDir: Vector2.Right),
            // 205 Generator：裂变炉，大六边形（照搬逆向 features=0xAEF300 含 GENERATOR）。
            ["reactor"] = MakeRegular("reactor", 205, "裂变炉", "能源", 0xE0B45D,
                BlockFeatures.Generator, 6, 23.0f, mass: 10.0f, hull: 45.0f),
            // 聚变发电机：五边形。
            ["generator"] = MakeRegular("generator", 207, "聚变发电机", "能源", 0xF0B85E,
                BlockFeatures.Generator, 5, 17.0f, mass: 7.0f, hull: 35.0f),
            // 武器电容：小方形。
            ["capacitor"] = MakeRegular("capacitor", 261, "武器电容", "能源", 0x6FA7D8,
                BlockFeatures.None, 4, 12.0f, mass: 4.0f, hull: 18.0f),
            // 深空传感器：六边形。
            ["sensor"] = MakeRegular("sensor", 262, "深空传感器", "系统", 0x72C7C2,
                BlockFeatures.None, 6, 13.0f, mass: 3.0f, hull: 18.0f),
            // 散热翼：六边形。
            ["radiator"] = MakeRegular("radiator", 416, "散热翼", "散热", 0x6EC6C7,
                BlockFeatures.None, 6, 18.0f, mass: 3.0f, hull: 18.0f),
            // 货运舱：大方形。
            ["cargo"] = MakeRegular("cargo", 201, "货运舱", "物流", 0xD0A46A,
                BlockFeatures.None, 4, 24.0f, mass: 8.0f, hull: 50.0f),
            // 牵引光束：五边形（照搬逆向 features 含 TRACTOR）。
            ["tractor"] = MakeRegular("tractor", 275, "牵引光束", "物流", 0xC58CDA,
                BlockFeatures.Tractor, 5, 15.0f, mass: 5.0f, hull: 22.0f),
        };

    /// <summary>
    /// 为 n 边正多边形生成端口（照搬逆向 insertRegularPoly + PortPos）：
    /// 端口放在边的中点，Direction 是边外法线（径向单位向量）。
    /// 端口距离 = circumradius * cos(π/n)（边中点到中心的距离），保证端口正好落在边上。
    /// </summary>
    private static List<Port> PortsForSides(int sides, float radius)
    {
        var ports = new List<Port>();
        var halfStepRad = Mathf.Pi / sides; // 边中点相对顶点偏移半个步距。
        var distance = radius * Mathf.Cos(halfStepRad); // 边中点到中心的距离。
        for (var i = 0; i < sides; i++)
        {
            var angle = halfStepRad + i * Mathf.Tau / sides; // 边中点方向。
            var offset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * distance;
            var direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)).Normalized(); // 边外法线（径向）。
            ports.Add(new Port(offset, direction));
        }
        return ports;
    }

    /// <summary>构造一个正多边形形状规格（便捷工厂，对齐逆向 insertRegularPoly）。</summary>
    private static ShapeSpec MakeRegular(
        string id, int numId, string label, string category, uint colorUint,
        BlockFeatures features, int sides, float apothem,
        float mass = 5.0f, float hull = 20.0f,
        float thrust = 0.0f, float damage = 0.0f,
        Vector2? thrustDir = null, Vector2? weaponDir = null)
    {
        // 逆向的 regular polygon 使用 apothem 作为结构尺寸：所有边的
        // 连接节点落在同一套“边距”坐标上，避免三角形、方形、六边形
        // 仅因边数不同就出现不可控的连接间距。
        var circumradius = apothem / Mathf.Cos(Mathf.Pi / sides);
        var startAngle = -Mathf.Pi * 0.5f;
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
            Radius = circumradius,
            VerticesField = vertices,
            Ports = PortsForSides(sides, circumradius),
            Mass = mass,
            Hull = hull,
            Thrust = thrust,
            Damage = damage,
            ThrustDirection = thrustDir ?? Vector2.Right,
            WeaponDirection = weaponDir ?? Vector2.Right,
        };
    }

    /// <summary>获取形状，未知形状回退到装甲骨架。</summary>
    public static ShapeSpec Get(string id) => Shapes.TryGetValue(id, out var spec) ? spec : Shapes["hull"];

    /// <summary>建造面板的方块顺序（调色板按此顺序展示，鼠标点击选择）。</summary>
    public static readonly string[] BuildOrder =
    {
        "core", "hull", "armor", "thruster", "gyro",
        "laser", "cannon", "beam", "railgun", "missile",
        "shield", "point_defense", "reactor", "generator", "capacitor",
        "sensor", "radiator", "cargo", "tractor",
    };
}
