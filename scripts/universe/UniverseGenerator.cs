using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// 无限宇宙的程序化生成器（仿 Reassembly 的 SectorStreamer 范式）。
/// 世界坐标按「扇区（Sector）」划分，每个扇区用其坐标哈希做确定性随机种子，
/// 纯函数式地生成该扇区内的天体（星球/空间站/小行星带）。
/// 因此无需存储无限数据：靠近哪个扇区，就即时算出该扇区的内容，且结果稳定可复现。
/// </summary>
public static class UniverseGenerator
{
    /// <summary>扇区边长（世界单位，像素）。</summary>
    public const float SectorSize = 4000.0f;

    /// <summary>世界坐标 → 扇区坐标（向下取整）。</summary>
    public static Vector2I WorldToSector(Vector2 world)
    {
        return new Vector2I(
            Mathf.FloorToInt(world.X / SectorSize),
            Mathf.FloorToInt(world.Y / SectorSize));
    }

    /// <summary>扇区坐标 → 扇区中心世界坐标。</summary>
    public static Vector2 SectorToWorldCenter(Vector2I sector)
    {
        return new Vector2(
            (sector.X + 0.5f) * SectorSize,
            (sector.Y + 0.5f) * SectorSize);
    }

    /// <summary>扇区坐标 → 确定性随机种子（可复现）。</summary>
    private static ulong SectorSeed(Vector2I sector)
    {
        // 用两个 32 位整数混合成 64 位种子，避免相邻扇区种子相关。
        var x = (ulong)(uint)sector.X;
        var y = (ulong)(uint)sector.Y;
        ulong hash = 0xcbf29ce484222325UL;
        hash = (hash ^ (x + 0x9e3779b9UL)) * 0x100000001b3UL;
        hash = (hash ^ (y + 0x9e3779b9UL)) * 0x100000001b3UL;
        return hash;
    }

    /// <summary>一个天体（星球 / 空间站 / 小行星带）。</summary>
    public sealed class CelestialBody
    {
        public required Vector2 Position { get; init; }
        public required CelestialKind Kind { get; init; }
        public required string Name { get; init; }
        public required float Radius { get; init; }
        public required string ColorHex { get; init; }
        /// <summary>星球可降落的资源/环境特征（决定工厂能采什么）。</summary>
        public string Biome { get; init; } = "iron";

        /// <summary>
        /// 引力井半径（超出此范围不再产生引力）。仿 Reassembly 的 Gravity Well。
        /// </summary>
        public float GravityRadius { get; init; }

        /// <summary>
        /// 引力强度 = 引力常数 × 质量。质量越大、半径越大，引力越强。
        /// </summary>
        public float GravityStrength { get; init; }
    }

    public enum CelestialKind
    {
        Planet,
        Station,
        AsteroidField,
        Star,
    }

    /// <summary>
    /// 生成指定扇区内的所有天体。纯函数：相同扇区坐标始终返回相同结果。
    /// </summary>
    public static List<CelestialBody> GenerateSector(Vector2I sector)
    {
        var seed = SectorSeed(sector);
        var rng = new Random((int)(seed & 0x7fffffff) ^ (int)(seed >> 32));
        var bodies = new List<CelestialBody>();
        var center = SectorToWorldCenter(sector);
        var half = SectorSize * 0.5f;

        // 扇区内天体数量：1~4 个，按种子变化，避免空旷或过密。
        var count = 1 + rng.Next(3);
        for (var i = 0; i < count; i++)
        {
            var offset = new Vector2(
                (float)(rng.NextDouble() * 2.0 - 1.0) * half * 0.85f,
                (float)(rng.NextDouble() * 2.0 - 1.0) * half * 0.85f);
            var position = center + offset;

            // 按种子决定天体类型。
            var roll = rng.NextDouble();
            if (roll < 0.06)
            {
                bodies.Add(new CelestialBody
                {
                    Position = position,
                    Kind = CelestialKind.Star,
                    Name = $"恒星 {sector.X},{sector.Y}-{i}",
                    Radius = 160.0f + (float)rng.NextDouble() * 120.0f,
                    ColorHex = StarColor(rng),
                    GravityRadius = 2600.0f,
                    GravityStrength = 30000000.0f,  // G·M：距表面500时 a≈120（推力23%，明显拉扯但可控）
                });
            }
            else if (roll < 0.55)
            {
                var biome = Biome(rng);
                bodies.Add(new CelestialBody
                {
                    Position = position,
                    Kind = CelestialKind.Planet,
                    Name = PlanetName(rng, sector, i),
                    Radius = 46.0f + (float)rng.NextDouble() * 70.0f,
                    ColorHex = BiomeColor(biome),
                    Biome = biome,
                    GravityRadius = 1500.0f,
                    GravityStrength = 8000000.0f,  // G·M：距表面300时 a≈89（推力17%），拉扯但可控
                });
            }
            else if (roll < 0.80)
            {
                bodies.Add(new CelestialBody
                {
                    Position = position,
                    Kind = CelestialKind.Station,
                    Name = StationName(rng),
                    Radius = 30.0f + (float)rng.NextDouble() * 22.0f,
                    ColorHex = "#7bb7e8",
                    GravityRadius = 550.0f,
                    GravityStrength = 600000.0f,  // G·M：距表面200时 a≈15（推力3%，轻微引导）
                });
            }
            else
            {
                bodies.Add(new CelestialBody
                {
                    Position = position,
                    Kind = CelestialKind.AsteroidField,
                    Name = $"小行星带 {sector.X},{sector.Y}-{i}",
                    Radius = 90.0f + (float)rng.NextDouble() * 160.0f,
                    ColorHex = "#8c9aaa",
                });
            }
        }

        return bodies;
    }

    /// <summary>获取玩家附近所有扇区的天体（用于相机可视范围与小地图）。</summary>
    public static List<CelestialBody> GetNearbyBodies(Vector2 worldPosition, int sectorRadius = 2)
    {
        var centerSector = WorldToSector(worldPosition);
        var bodies = new List<CelestialBody>();
        for (var dx = -sectorRadius; dx <= sectorRadius; dx++)
            for (var dy = -sectorRadius; dy <= sectorRadius; dy++)
                bodies.AddRange(GenerateSector(centerSector + new Vector2I(dx, dy)));
        return bodies;
    }

    private static string Biome(Random rng)
    {
        var roll = rng.NextDouble();
        return roll switch
        {
            < 0.30 => "iron",      // 铁矿
            < 0.50 => "volatile",  // 挥发物冰层
            < 0.68 => "crystal",   // 晶体
            < 0.84 => "alloy",     // 富合金
            _ => "iron",
        };
    }

    private static string BiomeColor(string biome) => biome switch
    {
        "iron" => "#d68b59",
        "volatile" => "#7a9cb0",
        "crystal" => "#8e7ca6",
        "alloy" => "#a18d61",
        _ => "#d68b59",
    };

    private static string StarColor(Random rng) => rng.Next(3) switch
    {
        0 => "#f2cc60",
        1 => "#f0b85e",
        _ => "#ffd27a",
    };

    private static readonly string[] PlanetPrefixes = { "赤道", "冰封", "锈蚀", "翠绿", "灰烬", "潮汐", "熔核", "孤星", "静海", "风蚀" };
    private static readonly string[] PlanetSuffixes = { "据点", "前哨", "殖民地", "荒原", "环礁", "高台", "深渊", "边陲" };

    private static string PlanetName(Random rng, Vector2I sector, int index)
    {
        var prefix = PlanetPrefixes[rng.Next(PlanetPrefixes.Length)];
        var suffix = PlanetSuffixes[rng.Next(PlanetSuffixes.Length)];
        return $"{prefix}{suffix} {sector.X},{sector.Y}-{index}";
    }

    private static readonly string[] StationPrefixes = { "补给站", "中继站", "观测站", "船坞", "贸易港", "哨戒站" };
    private static readonly string[] StationCodes = { "阿尔法", "贝塔", "伽马", "德尔塔", "厄普西隆", "泽塔" };

    private static string StationName(Random rng)
    {
        return $"{StationPrefixes[rng.Next(StationPrefixes.Length)]} {StationCodes[rng.Next(StationCodes.Length)]}";
    }
}
