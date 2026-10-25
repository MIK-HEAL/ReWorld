using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public enum MissionKind { Hunt, Bounty, Mine, Pirate }

/// <summary>Deterministic per-system contract offered at stations. Rebuilt from the id, never serialized.</summary>
public sealed class MissionDef
{
    public string Id = "";
    public MissionKind Kind;
    /// <summary>签发该合约的站点派系（与星系主站派系一致，确定性派生）。</summary>
    public int Issuer;
    /// <summary>悬赏目标派系（仅 Bounty 合约有意义）。</summary>
    public int Faction;
    public int Goal;
    public int Reward;
    public string Title = "";
    public string Detail = "";
}

public static class MissionCatalog
{
    // Stable hash: System.HashCode is randomized per process and must not seed saved content.
    private static int Seed(Vector2I cell) => unchecked(cell.X * 73856093 ^ cell.Y * 19349663);

    /// <summary>悬赏目标从主要派系池中排除签发方，避免"自己悬赏自己"。</summary>
    private static int BountyTarget(Random rand, int issuer)
    {
        var pool = UniverseGenerator.MajorStationFactions.Where(f => f != issuer).ToArray();
        return pool[rand.Next(pool.Length)];
    }

    /// <summary>
    /// 星系合约是纯函数产物：按星系缓存共享只读列表，避免击杀/领奖路径反复重建。
    /// MissionDef 视为不可变数据——调用方不得修改返回列表中的定义。
    /// </summary>
    private static readonly Dictionary<Vector2I, List<MissionDef>> _cache = new();

    public static List<MissionDef> Generate(Vector2I cell)
    {
        if (_cache.TryGetValue(cell, out var cached)) return cached;
        var list = Build(cell);
        _cache[cell] = list;
        return list;
    }

    private static List<MissionDef> Build(Vector2I cell)
    {
        var rand = new Random(Seed(cell));
        var issuer = UniverseGenerator.StationFactionOf(cell);
        var issuerName = FactionRelations.Names[issuer];
        var prefix = $"{cell.X},{cell.Y}";
        var list = new List<MissionDef>();
        var huntGoal = 2 + rand.Next(3);
        list.Add(new MissionDef
        {
            Id = $"{prefix}/0", Kind = MissionKind.Hunt, Issuer = issuer, Goal = huntGoal, Reward = 60 * huntGoal + 80,
            Title = "清剿空域", Detail = $"{issuerName}委托：摧毁 {huntGoal} 艘与你交战的舰船"
        });
        var target = BountyTarget(rand, issuer);
        var bountyGoal = 1 + rand.Next(3);
        list.Add(new MissionDef
        {
            Id = $"{prefix}/1", Kind = MissionKind.Bounty, Issuer = issuer, Faction = target, Goal = bountyGoal,
            Reward = 90 * bountyGoal + 120, Title = $"悬赏：{FactionRelations.Names[target]}",
            Detail = $"{issuerName}悬赏：摧毁 {bountyGoal} 艘{FactionRelations.Names[target]}舰船（对目标开火即被视为宣战）"
        });
        var mineGoal = 150 + rand.Next(4) * 50;
        list.Add(new MissionDef
        {
            Id = $"{prefix}/2", Kind = MissionKind.Mine, Issuer = issuer, Goal = mineGoal, Reward = mineGoal / 2 + 60,
            Title = "资源订单", Detail = $"{issuerName}采购：回收 {mineGoal} R 的矿质资源"
        });
        // 剿匪令：目标固定为星际掠夺者，活动区域为本星系外围海盗空域（该星系可能不存在空域）。
        var purgeGoal = 2 + rand.Next(2);
        list.Add(new MissionDef
        {
            Id = $"{prefix}/3", Kind = MissionKind.Pirate, Issuer = issuer, Faction = FactionRelations.Pirates,
            Goal = purgeGoal, Reward = 110 * purgeGoal + 150,
            Title = "剿匪令：清剿掠夺者",
            Detail = $"{issuerName}剿匪令：在星系外围海盗空域摧毁 {purgeGoal} 艘掠夺者舰船"
        });
        return list;
    }

    public static MissionDef? Find(string id)
    {
        var slash = id.LastIndexOf('/');
        if (slash < 0) return null;
        var comma = id.IndexOf(',');
        if (comma < 0 || comma > slash) return null;
        if (!int.TryParse(id.AsSpan(0, comma), out var x) ||
            !int.TryParse(id.AsSpan(comma + 1, slash - comma - 1), out var y) ||
            !int.TryParse(id.AsSpan(slash + 1), out var index)) return null;
        var list = Generate(new Vector2I(x, y));
        return index >= 0 && index < list.Count ? list[index] : null;
    }
}
