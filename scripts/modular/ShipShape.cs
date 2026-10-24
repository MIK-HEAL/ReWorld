using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Reassembly 式模块化舰体系统 —— 形状库。
///
/// 严格对齐逆向结论（见 docs/design/REASSEMBLY_MODULAR_SYSTEM.md）：
///   - Reassembly 的方块形状由 TheShapes 表定义，核心是「正多边形 + 端口（Port）」。
///   - 每个方块有多个端口，端口位于形状边缘，携带局部坐标 + 朝向法线。
///   - 两个方块通过「端口位置重合」自动连接（一个端口只能连一个方块）。
///   - 朝向是任意弧度，位置是连续浮点坐标 —— 因此舰体是任意角度多边形拼接，而非网格。
///
/// 本文件用纯数据（无场景节点）描述形状，供建造编辑器与运行时渲染共用。
/// </summary>
public static class ShipShapeLibrary
{
    /// <summary>
    /// 一个端口：位于形状边缘的连接点（对齐逆向 PortSpec：Offset + Direction）。
    /// 对正多边形，端口放在边的中点，法线即边的外法线（径向）。
    /// </summary>
    public readonly record struct Port(float AngleDeg, float Distance)
    {
        /// <summary>端口在形状局部坐标（未旋转）下的位置（边中点）。</summary>
        public Vector2 LocalPosition => new(
            Mathf.Cos(Mathf.DegToRad(AngleDeg)) * Distance,
            Mathf.Sin(Mathf.DegToRad(AngleDeg)) * Distance);

        /// <summary>端口朝向（单位外法线，正多边形边中点处等于径向）。</summary>
        public Vector2 Normal => LocalPosition.Normalized();
    }

    /// <summary>一个方块形状规格：正多边形顶点 + 端口 + 功能。</summary>
    public sealed class ShapeSpec
    {
        public required string Id { get; init; }
        public required string Label { get; init; }
        public required string Category { get; init; }
        public required string ColorHex { get; init; }
        /// <summary>正多边形边数（3=三角，4=方，6=六边…）。</summary>
        public int Sides { get; init; } = 4;
        /// <summary>外接圆半径（世界单位）。</summary>
        public float Radius { get; init; } = 22.0f;
        /// <summary>多边形起始角度（弧度），控制朝向。</summary>
        public float StartAngle { get; init; } = -Mathf.Pi * 0.5f;
        /// <summary>该形状上的端口列表。</summary>
        public List<Port> Ports { get; init; } = new();
        /// <summary>是否为舰船核心（不可拆、被击毁整舰瘫痪）。</summary>
        public bool IsCore { get; init; }
        /// <summary>质量（影响引力与惯性）。</summary>
        public float Mass { get; init; } = 5.0f;
        /// <summary>耐久。</summary>
        public float Hull { get; init; } = 20.0f;
        /// <summary>推力（推进器）。</summary>
        public float Thrust { get; init; }
        /// <summary>武器伤害。</summary>
        public float Damage { get; init; }
        /// <summary>是否为推进器（产生推力）。</summary>
        public bool IsThruster => Thrust > 0.0f;
        /// <summary>是否为武器。</summary>
        public bool IsWeapon => Damage > 0.0f;

        /// <summary>
        /// 推力方向（本地坐标，未旋转）。指向推力施加的相反方向（即喷口指向，
        /// 力沿 -ThrustDirection 施加）。对齐 Reassembly「推进器在挂点施力」。
        /// 默认 +X（朝右）。
        /// </summary>
        public Vector2 ThrustDirection { get; init; } = Vector2.Right;

        /// <summary>
        /// 炮口方向（本地坐标，未旋转）。武器沿此方向发射。默认 +X。
        /// </summary>
        public Vector2 WeaponDirection { get; init; } = Vector2.Right;

        /// <summary>该形状的顶点（局部坐标，未旋转）。</summary>
        public Vector2[] Vertices()
        {
            var verts = new Vector2[Sides];
            for (var i = 0; i < Sides; i++)
            {
                var angle = StartAngle + i * Mathf.Tau / Sides;
                verts[i] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * Radius;
            }
            return verts;
        }
    }

    /// <summary>
    /// 方块形状表（对齐逆向的 TheShapes）。用正多边形 + 端口描述每种方块。
    /// 端口均匀分布在多边形的边上（每个顶点方向一个），这样方块能沿任意边连接，
    /// 组合出任意角度的多边形舰体。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, ShapeSpec> Shapes =
        new Dictionary<string, ShapeSpec>(StringComparer.Ordinal)
        {
            // 核心：六边形，各边都有端口，作为舰船中枢。
            ["core"] = new()
            {
                Id = "core", Label = "舰船核心", Category = "核心", ColorHex = "#f4f1de",
                Sides = 6, Radius = 26.0f, IsCore = true, Mass = 12.0f, Hull = 60.0f,
                Ports = PortsForSides(6, 26.0f),
            },
            // 装甲骨架：方形，四边端口。
            ["hull"] = new()
            {
                Id = "hull", Label = "装甲骨架", Category = "骨架", ColorHex = "#a7b5c4",
                Sides = 4, Radius = 22.0f, Mass = 7.0f, Hull = 40.0f,
                Ports = PortsForSides(4, 22.0f),
            },
            // 复合装甲：方形，更厚。
            ["armor"] = new()
            {
                Id = "armor", Label = "复合装甲", Category = "骨架", ColorHex = "#8998a8",
                Sides = 4, Radius = 24.0f, Mass = 10.0f, Hull = 70.0f,
                Ports = PortsForSides(4, 24.0f),
            },
            // 推进器：四边形（对齐 Reassembly 常见外观，不使用三角以避免吸附与朝向歧义）。
            // 推力沿 +X（本地），喷口朝 -X；端口在四条边中点，可任意边吸附。
            ["thruster"] = new()
            {
                Id = "thruster", Label = "推进器", Category = "机动", ColorHex = "#cf7bff",
                Sides = 4, Radius = 22.0f, Mass = 5.0f, Hull = 25.0f, Thrust = 40.0f,
                Ports = PortsForSides(4, 22.0f),
                ThrustDirection = Vector2.Left,
            },
            // 姿态陀螺：小六边形。
            ["gyro"] = new()
            {
                Id = "gyro", Label = "姿态陀螺", Category = "机动", ColorHex = "#b58be3",
                Sides = 6, Radius = 16.0f, Mass = 4.0f, Hull = 25.0f,
                Ports = PortsForSides(6, 16.0f),
            },
            // 脉冲激光：五边形（尖端指向炮口）。
            ["laser"] = new()
            {
                Id = "laser", Label = "脉冲激光", Category = "武器", ColorHex = "#f07c8e",
                Sides = 5, Radius = 22.0f, Mass = 5.0f, Hull = 22.0f, Damage = 6.0f,
                Ports = PortsForSides(5, 22.0f),
                WeaponDirection = Vector2.Right,
            },
            // 动能炮：方形。
            ["cannon"] = new()
            {
                Id = "cannon", Label = "动能炮", Category = "武器", ColorHex = "#d97676",
                Sides = 4, Radius = 20.0f, Mass = 6.0f, Hull = 30.0f, Damage = 4.0f,
                Ports = PortsForSides(4, 20.0f),
                WeaponDirection = Vector2.Right,
            },
            // 连续光束：五边形。
            ["beam"] = new()
            {
                Id = "beam", Label = "连续光束", Category = "武器", ColorHex = "#ff7bb0",
                Sides = 5, Radius = 21.0f, Mass = 6.0f, Hull = 20.0f, Damage = 8.0f,
                Ports = PortsForSides(5, 21.0f),
                WeaponDirection = Vector2.Right,
            },
            // 磁轨炮：六边形（长条形武器）。
            ["railgun"] = new()
            {
                Id = "railgun", Label = "磁轨炮", Category = "武器", ColorHex = "#e9a56a",
                Sides = 6, Radius = 22.0f, Mass = 8.0f, Hull = 28.0f, Damage = 10.0f,
                Ports = PortsForSides(6, 22.0f),
                WeaponDirection = Vector2.Right,
            },
            // 导弹舱：方形。
            ["missile"] = new()
            {
                Id = "missile", Label = "导弹舱", Category = "武器", ColorHex = "#d8d27a",
                Sides = 4, Radius = 21.0f, Mass = 7.0f, Hull = 24.0f, Damage = 14.0f,
                Ports = PortsForSides(4, 21.0f),
                WeaponDirection = Vector2.Right,
            },
            // 偏转护盾：圆形视觉，用八边形近似。
            ["shield"] = new()
            {
                Id = "shield", Label = "偏转护盾", Category = "防御", ColorHex = "#7bb7e8",
                Sides = 8, Radius = 18.0f, Mass = 6.0f, Hull = 30.0f,
                Ports = PortsForSides(8, 18.0f),
            },
            // 点防御炮：小六边形。
            ["point_defense"] = new()
            {
                Id = "point_defense", Label = "点防御炮", Category = "防御", ColorHex = "#e28b8b",
                Sides = 6, Radius = 14.0f, Mass = 4.0f, Hull = 18.0f, Damage = 3.0f,
                Ports = PortsForSides(6, 14.0f),
                WeaponDirection = Vector2.Right,
            },
            // 裂变炉：大六边形。
            ["reactor"] = new()
            {
                Id = "reactor", Label = "裂变炉", Category = "能源", ColorHex = "#e0b45d",
                Sides = 6, Radius = 28.0f, Mass = 10.0f, Hull = 45.0f,
                Ports = PortsForSides(6, 28.0f),
            },
            // 聚变发电机：五边形。
            ["generator"] = new()
            {
                Id = "generator", Label = "聚变发电机", Category = "能源", ColorHex = "#f0b85e",
                Sides = 5, Radius = 20.0f, Mass = 7.0f, Hull = 35.0f,
                Ports = PortsForSides(5, 20.0f),
            },
            // 武器电容：小方形。
            ["capacitor"] = new()
            {
                Id = "capacitor", Label = "武器电容", Category = "能源", ColorHex = "#6fa7d8",
                Sides = 4, Radius = 14.0f, Mass = 4.0f, Hull = 18.0f,
                Ports = PortsForSides(4, 14.0f),
            },
            // 深空传感器：六边形。
            ["sensor"] = new()
            {
                Id = "sensor", Label = "深空传感器", Category = "系统", ColorHex = "#72c7c2",
                Sides = 6, Radius = 15.0f, Mass = 3.0f, Hull = 18.0f,
                Ports = PortsForSides(6, 15.0f),
            },
            // 散热翼：六边形（薄片结构，对齐 Reassembly 散热片外观）。
            ["radiator"] = new()
            {
                Id = "radiator", Label = "散热翼", Category = "散热", ColorHex = "#6ec6c7",
                Sides = 6, Radius = 22.0f, Mass = 3.0f, Hull = 18.0f,
                Ports = PortsForSides(6, 22.0f),
            },
            // 货运舱：大方形。
            ["cargo"] = new()
            {
                Id = "cargo", Label = "货运舱", Category = "物流", ColorHex = "#d0a46a",
                Sides = 4, Radius = 28.0f, Mass = 8.0f, Hull = 50.0f,
                Ports = PortsForSides(4, 28.0f),
            },
            // 牵引光束：五边形。
            ["tractor"] = new()
            {
                Id = "tractor", Label = "牵引光束", Category = "物流", ColorHex = "#c58cda",
                Sides = 5, Radius = 18.0f, Mass = 5.0f, Hull = 22.0f,
                Ports = PortsForSides(5, 18.0f),
            },
        };

    /// <summary>
    /// 为 n 边正多边形生成端口：每个端口放在边的中点，法线为边外法线（径向）。
    /// 对齐逆向 insertRegularPoly + PortPos：端口在边上，不在顶点上。
    /// 端口距离 = radius * cos(π/n)（边中点到中心的距离），保证端口正好落在边上。
    /// </summary>
    private static List<Port> PortsForSides(int sides, float radius)
    {
        var ports = new List<Port>();
        var halfStep = 180.0f / sides; // 边中点相对顶点偏移半个步距。
        var distance = radius * Mathf.Cos(Mathf.DegToRad(halfStep));
        for (var i = 0; i < sides; i++)
        {
            var angleDeg = i * 360.0f / sides + halfStep; // 边中点方向。
            ports.Add(new Port(angleDeg, distance));
        }
        return ports;
    }

    /// <summary>获取形状，未知形状回退到装甲骨架。</summary>
    public static ShapeSpec Get(string id) => Shapes.TryGetValue(id, out var spec) ? spec : Shapes["hull"];

    /// <summary>建造面板的方块顺序（按键 1-9,0,A,D 选择）。</summary>
    public static readonly string[] BuildOrder =
    {
        "core", "hull", "armor", "thruster", "gyro",
        "laser", "cannon", "beam", "railgun", "missile",
        "shield", "point_defense", "reactor", "generator", "capacitor",
        "sensor", "radiator", "cargo", "tractor",
    };
}
