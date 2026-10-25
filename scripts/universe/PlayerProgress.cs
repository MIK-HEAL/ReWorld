using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FileAccess = Godot.FileAccess;

/// <summary>Permanent technology purchases are outside the editor's reversible geometry history.</summary>
/// <summary>Accepted station contract state; the catalog entry itself is rebuilt deterministically from the id.</summary>
public sealed class MissionState
{
    public string Id { get; set; } = "";
    public int Progress { get; set; }
    public bool Accepted { get; set; }
    public bool Claimed { get; set; }
}

/// <summary>玩家在星球领地内建成的前哨/太空站；重启后按坐标重建。</summary>
public sealed class ColonyData
{
    public float X { get; set; }
    public float Y { get; set; }
    /// <summary>关联星球天体 Id；空串表示自由选址的前哨。</summary>
    public string PlanetId { get; set; } = "";
}

/// <summary>玩家在某颗星球地表建成的工厂群；产出速率由星球生物群系在建造时锁定。</summary>
public sealed class FactoryData
{
    public string PlanetId { get; set; } = "";
    public string Biome { get; set; } = "iron";
    public int Count { get; set; }
}

public sealed class PlayerProgress
{
    // 测试版本：开局大量资源，方便解锁全部科技和随便造舰。
    public int Resources { get; set; } = 1_000_000;
    public HashSet<int> Unlocked { get; set; } = new() { 47, 48, 49, 54, 56, 58, 60, 30001 };
    public FactionRelations Diplomacy { get; set; } = new();
    public bool Established { get; set; }
    public float BaseX { get; set; }
    public float BaseY { get; set; }
    /// <summary>玩家建成的全部前哨/太空站（含首座基地）。</summary>
    public List<ColonyData> Colonies { get; set; } = new();
    /// <summary>已认领的星球天体 Id：无主星球领地内建站后纳入玩家自治领地。</summary>
    public HashSet<string> ClaimedPlanets { get; set; } = new();
    /// <summary>各星球地表工厂群：登陆后建造，按生物群系持续产出资源。</summary>
    public List<FactoryData> Factories { get; set; } = new();
    public HashSet<string> MinedDeposits { get; set; } = new();
    public List<MissionState> Missions { get; set; } = new();
    /// <summary>按派系累计的击杀台账（含中立目标），用于外交面板威胁评估。</summary>
    public Dictionary<int, int> Kills { get; set; } = new();
    public List<string> FleetDesigns { get; set; } = new();
    public event Action? Changed;
    public bool Autosave { get; set; }
    private const string SavePath = "user://reworld_campaign.json";
    public bool CanBuild(string id)
    {
        var canonical = ShipShapeLibrary.CanonicalTechnologyId(id);
        return ShipShapeLibrary.BuildOrder.Contains(canonical) &&
            ShipShapeLibrary.Shapes.TryGetValue(canonical, out var spec) && Unlocked.Contains(spec.NumId);
    }
    public static int UnlockCost(ShipShapeLibrary.ShapeSpec spec)
        => Math.Max(40, (int)(spec.Mass * 8 + (spec.IsWeapon ? spec.Damage * 5 : 0)
            + ((spec.Features & (BlockFeatures.Shield | BlockFeatures.Factory)) != 0 ? 200 : 0)));
    public bool TryUnlock(string id)
    {
        var canonical = ShipShapeLibrary.CanonicalTechnologyId(id);
        if (!ShipShapeLibrary.BuildOrder.Contains(canonical) || !ShipShapeLibrary.Shapes.TryGetValue(canonical, out var spec)) return false;
        if (Unlocked.Contains(spec.NumId)) return true;
        var cost = UnlockCost(spec);
        if (Resources < cost) return false;
        Resources -= cost;
        Unlocked.Add(spec.NumId);
        Commit();
        return true;
    }
    public bool Spend(int amount)
    {
        if (amount < 0 || Resources < amount) return false;
        Resources -= amount;
        Commit();
        return true;
    }

    public MissionState MissionStateFor(string id)
    {
        foreach (var mission in Missions) if (mission.Id == id) return mission;
        var state = new MissionState { Id = id };
        Missions.Add(state);
        return state;
    }
    public bool AcceptMission(string id)
    {
        var def = MissionCatalog.Find(id);
        var state = MissionStateFor(id);
        if (def is null || state.Accepted) return false;
        state.Accepted = true;
        Commit();
        return true;
    }
    public bool MissionReadyToClaim(string id)
    {
        var def = MissionCatalog.Find(id);
        var state = MissionStateFor(id);
        return def is not null && state.Accepted && !state.Claimed && state.Progress >= def.Goal;
    }
    public bool ClaimMission(string id)
    {
        var def = MissionCatalog.Find(id);
        var state = MissionStateFor(id);
        if (def is null || !state.Accepted || state.Claimed || state.Progress < def.Goal) return false;
        state.Claimed = true;
        Resources = (int)Math.Min(int.MaxValue, (long)Resources + def.Reward);
        Commit();
        return true;
    }
    /// <summary>
    /// 记录一次击杀：累计台账；悬赏/清剿合约推进（清剿仅计交战击杀）。
    /// 无端袭击中立或友好派系会把该派系推向通缉，返回 true 供调用方提示玩家。
    /// </summary>
    public bool RecordKill(int faction, bool hostile = true)
    {
        Kills[faction] = Kills.GetValueOrDefault(faction) + 1;
        var wanted = false;
        if (!hostile && faction != FactionRelations.Player && !Diplomacy.Hostile(FactionRelations.Player, faction))
        {
            Diplomacy.Set(FactionRelations.Player, faction, DiplomaticState.Wanted);
            wanted = true;
        }
        var changed = wanted;
        foreach (var state in Missions)
        {
            if (!state.Accepted || state.Claimed) continue;
            var def = MissionCatalog.Find(state.Id);
            if (def is null || def.Kind == MissionKind.Bounty && def.Faction != faction) continue;
            if (def.Kind == MissionKind.Pirate && faction != FactionRelations.Pirates) continue;
            if (def.Kind == MissionKind.Hunt && !hostile) continue;
            if (def.Kind == MissionKind.Mine || state.Progress >= def.Goal) continue;
            state.Progress = Math.Min(def.Goal, state.Progress + 1);
            changed = true;
        }
        if (changed) Commit();
        return wanted;
    }
    public void RecordMined(int amount)
    {
        if (amount <= 0) return;
        var changed = false;
        foreach (var state in Missions)
        {
            if (!state.Accepted || state.Claimed) continue;
            var def = MissionCatalog.Find(state.Id);
            if (def is null || def.Kind != MissionKind.Mine || state.Progress >= def.Goal) continue;
            state.Progress = Math.Min(def.Goal, state.Progress + amount);
            changed = true;
        }
        if (changed) Commit();
    }

    /// <summary>
    /// 空间站解密终端报价：与站点派系的关系决定折扣——友好 85 折、中立 95 折。
    /// 交战/通缉派系本就无法停靠。discountPercent 为 0 时按中立 95 折计。
    /// </summary>
    public static int StationUnlockCost(ShipShapeLibrary.ShapeSpec spec, int discountPercent = 95)
    {
        var cost = UnlockCost(spec);
        var pct = discountPercent is >= 1 and <= 100 ? discountPercent : 95;
        return cost * pct / 100;
    }

    public bool TryStationUnlock(string id, int discountPercent = 95)
    {
        var canonical = ShipShapeLibrary.CanonicalTechnologyId(id);
        if (!ShipShapeLibrary.BuildOrder.Contains(canonical) || !ShipShapeLibrary.Shapes.TryGetValue(canonical, out var spec)) return false;
        if (Unlocked.Contains(spec.NumId)) return true;
        var cost = StationUnlockCost(spec, discountPercent);
        if (Resources < cost) return false;
        Resources -= cost;
        Unlocked.Add(spec.NumId);
        Commit();
        return true;
    }

    public void Collect(int amount)
    {
        if (amount <= 0) return;
        Resources = (int)Math.Min(int.MaxValue, (long)Resources + amount);
        Commit();
    }

    /// <summary>每颗星球的地表工厂数上限。</summary>
    public const int MaxFactoriesPerPlanet = 5;
    /// <summary>第 n 座工厂的造价（existingCount 为该星球现有工厂数）。</summary>
    public static int FactoryCost(int existingCount) => 400 + existingCount * 250;
    /// <summary>每座工厂每秒产出（R/s），由星球生物群系决定。</summary>
    public static float FactoryRateOf(string biome) => biome switch
    {
        "crystal" => 0.16f,
        "alloy" => 0.12f,
        "volatile" => 0.07f,
        _ => 0.10f,
    };
    public static string BiomeLabel(string biome) => biome switch
    {
        "iron" => "铁矿",
        "volatile" => "挥发物",
        "crystal" => "晶体",
        "alloy" => "富合金",
        _ => "铁矿",
    };

    public int FactoryCountOf(string planetId)
        => Factories.FirstOrDefault(f => f.PlanetId == planetId)?.Count ?? 0;

    /// <summary>在星球上新增一座工厂；容量满返回 false。</summary>
    public bool TryAddFactory(string planetId, string biome)
    {
        var entry = Factories.FirstOrDefault(f => f.PlanetId == planetId);
        if (entry is null) Factories.Add(entry = new FactoryData { PlanetId = planetId, Biome = biome });
        if (entry.Count >= MaxFactoriesPerPlanet) return false;
        entry.Count++;
        Commit();
        return true;
    }

    /// <summary>全部地表工厂的合计产出速率（R/s）。</summary>
    public float FactoryIncomeRate()
    {
        var rate = 0f;
        foreach (var factory in Factories) rate += factory.Count * FactoryRateOf(factory.Biome);
        return rate;
    }

    public void Commit()
    {
        Changed?.Invoke();
        if (Autosave) Save();
    }
    public void Save(string path = SavePath)
    {
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        file?.StoreString(JsonSerializer.Serialize(this));
    }
    /// <summary>开始页面用于判断是否提供「继续战役」入口。</summary>
    public static bool SaveExists(string path = SavePath) => FileAccess.FileExists(path);

    public static PlayerProgress Load(string path = SavePath)
    {
        if (!FileAccess.FileExists(path)) return new();
        try
        {
            var progress = JsonSerializer.Deserialize<PlayerProgress>(FileAccess.GetFileAsString(path)) ?? new();
            progress.Resources = Math.Max(0, progress.Resources);
            progress.Unlocked ??= new(); progress.Diplomacy ??= new(); progress.Diplomacy.States ??= new();
            progress.MinedDeposits ??= new(); progress.FleetDesigns ??= new(); progress.Missions ??= new();
            progress.Kills ??= new(); progress.Colonies ??= new(); progress.ClaimedPlanets ??= new();
            progress.Factories ??= new();
            // 旧存档只有首座基地坐标：迁移为领地列表的第一条记录。
            if (progress.Established && progress.Colonies.Count == 0)
                progress.Colonies.Add(new ColonyData { X = progress.BaseX, Y = progress.BaseY });
            // Wanted is a temporary bounty status, not a formal declaration of war. A new session (time served)
            // clears it so the player is not permanently locked out of stations for an accidental ram.
            var wantedKeys = new List<string>();
            foreach (var (pair, state) in progress.Diplomacy.States)
                if (state == DiplomaticState.Wanted) wantedKeys.Add(pair);
            foreach (var pair in wantedKeys) progress.Diplomacy.States[pair] = DiplomaticState.Neutral;
            if (progress.Unlocked.Contains(200)) progress.Unlocked.Add(47);
            if (progress.Unlocked.Contains(219)) progress.Unlocked.Add(48);
            if (progress.Unlocked.Contains(202)) progress.Unlocked.Add(54);
            if (progress.Unlocked.Contains(218)) progress.Unlocked.Add(56);
            if (progress.Unlocked.Contains(205)) progress.Unlocked.Add(49);
            return progress;
        }
        catch (JsonException) { GD.PushWarning("进度存档无效，已使用初始进度"); return new(); }
    }
}
