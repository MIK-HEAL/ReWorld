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

    /// <summary>可拥有星系空间站的主要 NPC 势力池（确定性派系分配与合约签发共用）。</summary>
    public static readonly int[] MajorStationFactions =
        { FactionRelations.League, FactionRelations.Red, FactionRelations.Crystal, 4, 6 };

    /// <summary>
    /// 星系空间站归属派系：启明星系固定联航共同体（玩家母港），其余星系按独立种子确定性分配。
    /// 与 GenerateSystem 的天体随机流互不干扰，保证天体布局不因派系化而漂移。
    /// </summary>
    public static int StationFactionOf(Vector2I cell)
    {
        if (cell == Vector2I.Zero) return FactionRelations.League;
        // 与 MissionCatalog 同源的稳定哈希；System.HashCode 按进程随机化，禁止用于可存档内容。
        var seed = unchecked(cell.X * 73856093 ^ cell.Y * 19349663);
        var rand = new Random(seed);
        return MajorStationFactions[rand.Next(MajorStationFactions.Length)];
    }

    /// <summary>
    /// 星球归属：约 60% 归入本星系主站势力，其余无主（0）可供玩家认领。
    /// 启明星系全部归联航共同体。与天体随机流独立，保证星球布局不因派系化而漂移。
    /// </summary>
    public static int PlanetFactionOf(Vector2I cell, int planetIndex)
    {
        if (cell == Vector2I.Zero) return FactionRelations.League;
        var seed = unchecked(cell.X * 374761393 + cell.Y * 668265263 ^ planetIndex * 265443576);
        seed = unchecked((seed ^ (seed >> 13)) * 1274126177);
        seed ^= seed >> 16;
        return (uint)seed % 100 < 60 ? StationFactionOf(cell) : 0;
    }

    /// <summary>
    /// 星系航道拓扑（确定性随机）：每格恒有一条指向母港方向的主干航道（逐步靠近原点，
    /// 保证全域连通、无环）；其余相邻星系按无序对稳定哈希约 35% 概率生成支线航道。
    /// 相邻但未连航道的星系不可直接迁跃，需沿已连通航道中转。
    /// </summary>
    public static bool SystemsLinked(Vector2I a, Vector2I b)
    {
        if (a == b) return false;
        var delta = b - a;
        if (Math.Abs(delta.X) > 1 || Math.Abs(delta.Y) > 1) return false;
        if (SpanningParent(a) == b || SpanningParent(b) == a) return true;
        var lo = a; var hi = b;
        if (lo.X > hi.X || (lo.X == hi.X && lo.Y > hi.Y)) (lo, hi) = (hi, lo);
        unchecked
        {
            long hash = lo.X * 73856093L ^ hi.X * 19349663L ^ lo.Y * 83492791L ^ hi.Y * 2971215073L;
            hash = (hash ^ (hash >> 27)) * 2654435761L;
            hash ^= hash >> 31;
            return (uint)hash % 100 < 35;
        }
    }

    /// <summary>主干航道父节点：每步严格向原点靠近一格（曼哈顿距离递减，故无环且必达）。</summary>
    public static Vector2I SpanningParent(Vector2I cell)
    {
        if (cell == Vector2I.Zero) return cell;
        if (Math.Abs(cell.X) >= Math.Abs(cell.Y)) return new(cell.X - Math.Sign(cell.X), cell.Y);
        return new(cell.X, cell.Y - Math.Sign(cell.Y));
    }

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
        /// <summary>空间站归属派系；启明星系固定为联航共同体，其余星系确定性分配主要 NPC 势力。</summary>
        public int FactionId { get; init; } = FactionRelations.League;
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

        /// <summary>星球势力范围半径：在此圈内建站即可认领/运营该星球领地。</summary>
        public float TerritoryRadius => Radius * 1.55f;
    }

    public enum CelestialKind
    {
        Planet,
        Station,
        AsteroidField,
        Star,
        /// <summary>海盗空域：无实体表面，只作区域标识与导航显示。</summary>
        PirateZone,
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
        // 星系原型多样化：行星数、站点数、碎石带密度都由星系种子决定，宇宙不再是流水线布局。
        var planets = 3 + rng.Next(4);
        var stationCount = 1 + rng.Next(3);
        var innerFields = 1 + rng.Next(3);
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
                FactionId = PlanetFactionOf(cell, i),
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
        for (var i = 0; i < stationCount; i++)
        {
            var planet = bodies[i % planets];
            var position = planet.Position + Vector2.Down.Rotated(phase + i) * (planet.Radius + 1400);
            // 站点仍须避开任何已有天体（行星轨道可能因黄金角分布而靠近）。
            if (bodies.Exists(body => body.Position.DistanceTo(position) < body.Radius + 160 + 1200)) continue;
            bodies.Add(new CelestialBody
            {
                Id = $"{id}:station:{i}", SystemId = id, SystemName = name,
                Position = position, Kind = CelestialKind.Station, Name = StationName(rng),
                Radius = 160, ColorHex = "#7bb7e8",
                GravityRadius = 0, GravityStrength = 0,
                FactionId = StationFactionOf(cell),
            });
        }
        for (var i = 0; i < innerFields; i++)
        {
            var position = center + Vector2.Right.Rotated(phase + 1.2f + i * 2.4f) * (14800 + i * 6200);
            var radius = 90 + (float)rng.NextDouble() * 160;
            if (bodies.Exists(body => body.Position.DistanceTo(position) < body.Radius + radius + 1200)) continue;
            bodies.Add(new CelestialBody
            {
                Id = $"{id}:asteroid:{i}", SystemId = id, SystemName = name,
                Position = position, Kind = CelestialKind.AsteroidField, Name = $"{name} · 小行星带 {i + 1}",
                Radius = radius, ColorHex = "#8c9aaa",
            });
        }
        // 星系外围碎石环带：沿系统边缘一圈稀疏的陨石簇，像 Reassembly 的边境碎屑带。
        var ringCount = 6 + rng.Next(9);
        var ringBase = 29600 + rng.NextDouble() * 1600;
        for (var i = 0; i < ringCount; i++)
        {
            var angle = phase + i * Mathf.Tau / ringCount + (float)(rng.NextDouble() - 0.5) * 0.22f;
            var orbit = (float)(ringBase + (rng.NextDouble() - 0.5) * 2400);
            var position = center + Vector2.Right.Rotated(angle) * orbit;
            var radius = 110 + (float)rng.NextDouble() * 150;
            if (position.DistanceTo(center) + radius > SystemRadius) continue;
            if (bodies.Exists(body => body.Position.DistanceTo(position) < body.Radius + radius + 1200)) continue;
            bodies.Add(new CelestialBody
            {
                Id = $"{id}:outerbelt:{i}", SystemId = id, SystemName = name,
                Position = position, Kind = CelestialKind.AsteroidField, Name = $"{name} · 外环碎石 {i + 1}",
                Radius = radius, ColorHex = "#75828f",
            });
        }
        // 海盗空域：约一半星系在外围刷新一片掠夺者盘踞区（剿匪合约的目标地）。
        if (PirateZoneSeed(cell) is { } zoneSeed)
        {
            var zoneRand = new Random(zoneSeed);
            var zoneRadius = 2100f;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var angle = (float)zoneRand.NextDouble() * Mathf.Tau;
                var orbit = 26500 + (float)zoneRand.NextDouble() * 4800;
                var position = center + Vector2.Right.Rotated(angle) * orbit;
                if (position.DistanceTo(center) + zoneRadius > SystemRadius) continue;
                if (bodies.Exists(body => body.Position.DistanceTo(position) < body.Radius + zoneRadius + 1200)) continue;
                bodies.Add(new CelestialBody
                {
                    Id = $"{id}:pirates", SystemId = id, SystemName = name,
                    Position = position, Kind = CelestialKind.PirateZone, Name = $"{name} · 海盗空域",
                    Radius = zoneRadius, ColorHex = "#c25656", FactionId = FactionRelations.Pirates,
                });
                break;
            }
        }
        return new StarSystem(id, name, center, bodies);
    }

    /// <summary>约 45% 的星系存在海盗空域；返回其确定性子种子，否则 null。</summary>
    private static int? PirateZoneSeed(Vector2I cell)
    {
        if (cell == Vector2I.Zero) return null; // 母港星系保持安全。
        var seed = unchecked((int)(cell.X * 2654435761L ^ cell.Y * 40503L));
        seed = unchecked((int)((seed ^ (seed >> 15)) * 2246822519L));
        seed ^= seed >> 13;
        return (uint)seed % 100 < 45 ? seed : null;
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
