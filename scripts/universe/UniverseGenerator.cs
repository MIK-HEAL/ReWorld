using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// 先按确定性种子生成完整恒星系，再由扇区裁剪加载。
/// 行星、空间站、小行星带围绕同一恒星分布；星系之间保留长距离空域。
/// </summary>
public static class UniverseGenerator
{
    /// <summary>扇区边长（世界单位，像素）。</summary>
    public const float SectorSize = 9000.0f;
    public const float PlanetRadiusMin = 640.0f;
    public const float PlanetRadiusMax = 1100.0f;
    public const float StarRadiusMax = 1900.0f;
    public const float PlanetGravityRadiusMultiplier = 10.0f;
    public const float StarGravityRadiusMultiplier = 8.0f;
    public const float MaxGravityRadius = StarRadiusMax * StarGravityRadiusMultiplier;
    public const float SystemCellSize = 144000.0f;
    public const float SystemCenterJitter = 12000.0f;
    public const float SystemRadius = 34000.0f;
    public const float MinimumInterstellarGap = SystemCellSize - 2 * SystemCenterJitter - 2 * SystemRadius;
    private static readonly Vector2 HomeSystemCenter = new(-7500, 4500);
    /// <summary>Enemy faction traffic participates in normal system generation.</summary>
    public static bool GenerateEnemyFactions { get; } = true;

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
        public string SystemId { get; init; } = "";
        public string SystemName { get; init; } = "";
        /// <summary>稳定的天体标识；后续轨道交互与地表存档共用。</summary>
        public required string Id { get; init; }
        public required Vector2 Position { get; init; }
        public required CelestialKind Kind { get; init; }
        public required string Name { get; init; }
        public required float Radius { get; init; }
        public required string ColorHex { get; init; }
        /// <summary>星球可降落的资源/环境特征（决定工厂能采什么）。</summary>
        public string Biome { get; init; } = "iron";
        public bool HasSurface => Kind is CelestialKind.Planet or CelestialKind.Star;
        /// <summary>只有恒星表面会造成碰撞伤害；行星只反弹不伤舰体。</summary>
        public bool CausesSurfaceDamage => Kind == CelestialKind.Star;
        public float ApproachRadius => Radius + Mathf.Max(240, Radius * 0.3f);
        public float SurfaceDistance(Vector2 position) => Mathf.Max(0, Position.DistanceTo(position) - Radius);

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

    public sealed record StarSystem(string Id, string Name, Vector2 Center, IReadOnlyList<CelestialBody> Bodies);

    public static Vector2I WorldToSystemCell(Vector2 position) => new(
        Mathf.FloorToInt((position.X - HomeSystemCenter.X + SystemCellSize * 0.5f) / SystemCellSize),
        Mathf.FloorToInt((position.Y - HomeSystemCenter.Y + SystemCellSize * 0.5f) / SystemCellSize));

    public static Vector2 SystemCenter(Vector2I cell)
    {
        if (cell == Vector2I.Zero) return HomeSystemCenter;
        var seed = SectorSeed(cell) ^ 0xa0761d6478bd642fUL;
        seed = unchecked((seed ^ (seed >> 30)) * 0xbf58476d1ce4e5b9UL);
        seed = unchecked((seed ^ (seed >> 27)) * 0x94d049bb133111ebUL);
        seed ^= seed >> 31;
        var jitter = new Vector2((seed & 0xffff) / 65535.0f * 2 - 1, ((seed >> 32) & 0xffff) / 65535.0f * 2 - 1);
        return HomeSystemCenter + new Vector2(cell.X, cell.Y) * SystemCellSize + jitter * SystemCenterJitter;
    }

    public static string SystemName(Vector2I cell) => cell == Vector2I.Zero ? "启明星系" : $"星系 {cell.X},{cell.Y}";

    public static StarSystem GenerateSystem(Vector2I cell)
    {
        var seed = SectorSeed(cell);
        var rng = new Random((int)(seed & 0x7fffffff) ^ (int)(seed >> 32));
        var center = SystemCenter(cell);
        var id = $"system:{cell.X}:{cell.Y}";
        var name = SystemName(cell);
        var bodies = new List<CelestialBody>(12);
        var planets = 4 + rng.Next(3);
        var phase = cell == Vector2I.Zero ? 0 : (float)rng.NextDouble() * Mathf.Tau;
        // 内到外的轨道带有足够的径向间距；角度由星系种子决定。
        for (var i = 0; i < planets; i++)
        {
            var angle = phase + i * 2.3999632f;
            var orbit = 12000 + i * 4000;
            var position = center + Vector2.Right.Rotated(angle) * orbit;
            var biome = Biome(rng);
            var radius = PlanetRadiusMin + (float)rng.NextDouble() * (PlanetRadiusMax - PlanetRadiusMin);
            bodies.Add(new CelestialBody
            {
                Id = $"{id}:planet:{i}", SystemId = id, SystemName = name,
                Position = position, Kind = CelestialKind.Planet,
                Name = cell == Vector2I.Zero && i == 0 ? "启明星 · 母星" : PlanetName(rng, cell, i),
                Radius = radius, ColorHex = BiomeColor(biome), Biome = biome,
                GravityRadius = radius * PlanetGravityRadiusMultiplier,
                GravityStrength = radius * radius * 100,
            });
        }
        var starRadius = 1400 + (float)rng.NextDouble() * (StarRadiusMax - 1400);
        bodies.Add(new CelestialBody
        {
            Id = $"{id}:star", SystemId = id, SystemName = name,
            Position = center, Kind = CelestialKind.Star, Name = $"{name} · 恒星",
            Radius = starRadius, ColorHex = StarColor(rng),
            GravityRadius = starRadius * StarGravityRadiusMultiplier,
            GravityStrength = starRadius * starRadius * 120,
        });
        for (var i = 0; i < 1 + planets / 4; i++)
        {
            var planet = bodies[i * 2];
            var position = planet.Position + Vector2.Down.Rotated(phase + i) * (planet.Radius + 1400);
            bodies.Add(new CelestialBody
            {
                Id = $"{id}:station:{i}", SystemId = id, SystemName = name,
                Position = position, Kind = CelestialKind.Station, Name = StationName(rng),
                Radius = 160, ColorHex = "#7bb7e8",
                GravityRadius = 0, GravityStrength = 0,
            });
        }
        for (var i = 0; i < 2; i++)
        {
            var position = center + Vector2.Right.Rotated(phase + 1.2f + i * 2.4f) * (15500 + i * 9000);
            var radius = 90 + (float)rng.NextDouble() * 160;
            if (bodies.Exists(body => body.Position.DistanceTo(position) < body.Radius + radius + 1200)) continue;
            bodies.Add(new CelestialBody
            {
                Id = $"{id}:asteroid:{i}", SystemId = id, SystemName = name,
                Position = position, Kind = CelestialKind.AsteroidField, Name = $"{name} · 小行星带 {i + 1}",
                Radius = radius, ColorHex = "#8c9aaa",
            });
        }
        return new StarSystem(id, name, center, bodies);
    }

    /// <summary>
    /// 生成指定扇区内的所有天体。纯函数：相同扇区坐标始终返回相同结果。
    /// </summary>
    public static List<CelestialBody> GenerateSector(Vector2I sector)
    {
        return BodiesInSectors(sector, sector);
    }

    /// <summary>获取玩家附近所有扇区的天体（用于相机可视范围与小地图）。</summary>
    public static List<CelestialBody> GetNearbyBodies(Vector2 worldPosition, int sectorRadius = 2)
    {
        var centerSector = WorldToSector(worldPosition);
        sectorRadius = Math.Max(0, sectorRadius);
        return BodiesInSectors(centerSector - Vector2I.One * sectorRadius, centerSector + Vector2I.One * sectorRadius);
    }

    private static List<CelestialBody> BodiesInSectors(Vector2I first, Vector2I last)
    {
        var minWorld = new Vector2(first.X, first.Y) * SectorSize;
        var maxWorld = new Vector2(last.X + 1, last.Y + 1) * SectorSize;
        var minCell = WorldToSystemCell(minWorld);
        var maxCell = WorldToSystemCell(maxWorld);
        var bodies = new List<CelestialBody>();
        // 批量加载只生成每个星系一次，不为每个空扇区重复生成整套天体。
        for (var x = minCell.X; x <= maxCell.X; x++)
        for (var y = minCell.Y; y <= maxCell.Y; y++)
        {
            var cell = new Vector2I(x, y);
            var center = SystemCenter(cell);
            if (center.X + SystemRadius < minWorld.X || center.Y + SystemRadius < minWorld.Y ||
                center.X - SystemRadius >= maxWorld.X || center.Y - SystemRadius >= maxWorld.Y) continue;
            foreach (var body in GenerateSystem(cell).Bodies)
            {
                var sector = WorldToSector(body.Position);
                if (sector.X >= first.X && sector.X <= last.X && sector.Y >= first.Y && sector.Y <= last.Y)
                    bodies.Add(body);
            }
        }
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
