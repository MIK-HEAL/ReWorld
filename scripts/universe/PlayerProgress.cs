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
    /// <summary>各座工厂的模块化蓝图（按建造顺序对齐槽位 0..Count-1）；
    /// 旧存档/旧式工厂没有对应条目，产出回落到 Count × 群系基准。</summary>
    public List<FactorySlotData> Slots { get; set; } = new();
}

/// <summary>模块化工厂的单个模块存档记录（JSON 安全的网格坐标 + 朝向）。</summary>
public sealed class FactoryModuleData
{
    public string Kind { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Rotation { get; set; }
}

/// <summary>单座模块化工厂的蓝图快照与建造时锁定的资源产出速率。</summary>
public sealed class FactorySlotData
{
    public List<FactoryModuleData> Modules { get; set; } = new();
    /// <summary>铁质碎料产出（单位/s），由蓝图矿机数与群系倍率决定。</summary>
    public float OreRate { get; set; }
    /// <summary>合金产出（单位/s），由蓝图精炼炉数与群系倍率决定。</summary>
    public float AlloyRate { get; set; }
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
    /// <summary>玩家库存的工厂产资源（iron 铁质碎料 / alloy 合金），到空间站交易页出售换 R。</summary>
    public Dictionary<string, int> ResourceStock { get; set; } = new();
    /// <summary>资源市场时钟（秒）：驱动行情的周期性波动。</summary>
    public float MarketTime { get; set; }
    /// <summary>碎料市场供给压力：抛售抬升、压低成交价，随时间回稳。</summary>
    public float IronPressure { get; set; }
    /// <summary>合金市场供给压力。</summary>
    public float AlloyPressure { get; set; }
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

    /// <summary>老式（无蓝图）工厂的合计 R 产出（R/s）——旧档与程序化直建走此通道；
    /// 模块化槽不产 R，改为产出铁质碎料/合金入库存。热路径只读字段，零分配。</summary>
    public float FactoryIncomeRate()
    {
        var rate = 0f;
        foreach (var factory in Factories)
        {
            for (var i = 0; i < factory.Count; i++)
            {
                if (i < factory.Slots.Count && factory.Slots[i].Modules.Count > 0) continue;
                rate += FactoryRateOf(factory.Biome);
            }
        }
        return rate;
    }

    /// <summary>模块化工厂的合计资源产出（单位/s）：矿机产碎料、精炼炉产合金。热路径零分配。</summary>
    public (float Ore, float Alloy) FactoryResourceRates()
    {
        float ore = 0, alloy = 0;
        foreach (var factory in Factories)
        {
            for (var i = 0; i < factory.Count && i < factory.Slots.Count; i++)
            {
                if (factory.Slots[i].Modules.Count == 0) continue;
                ore += factory.Slots[i].OreRate;
                alloy += factory.Slots[i].AlloyRate;
            }
        }
        return (ore, alloy);
    }

    /// <summary>群系倍率（与 FactoryRateOf 的基准比例一致：铁 1.0 / 晶体 1.6 / 合金 1.2 / 挥发物 0.7）。</summary>
    public static float BiomeMultiplier(string biome) => biome switch
    {
        "crystal" => 1.6f,
        "alloy" => 1.2f,
        "volatile" => 0.7f,
        _ => 1.0f,
    };

    /// <summary>工厂模块折价（R）：铁质碎料 10 R/单位，合金 25 R/单位。</summary>
    public static int ModuleCostR(ModuleDefinition definition) =>
        (int)definition.Cost.GetValueOrDefault("iron") * 10
        + (int)definition.Cost.GetValueOrDefault("alloy") * 25;

    /// <summary>整份蓝图（模块集合）的物料成本合计。</summary>
    public static int BlueprintCostR(List<FactoryModuleData> modules)
    {
        var cost = 0;
        foreach (var module in modules)
            if (FactoryModuleGraph.Definitions.TryGetValue(module.Kind, out var definition))
                cost += ModuleCostR(definition);
        return cost;
    }

    /// <summary>
    /// 计算模块化工厂蓝图的资源产出（单位/s）：只有连通核心的矿机产铁质碎料、
    /// 精炼炉产合金；群系倍率（BiomeMultiplier）决定品位高低。产出入玩家库存，
    /// 到空间站交易页出售换 R（行情波动，抛售压价）。
    /// </summary>
    public static (float Ore, float Alloy) BlueprintProduction(string biome, List<FactoryModuleData> modules)
    {
        if (modules.Count == 0) return (0, 0);
        var graph = new FactoryModuleGraph();
        foreach (var module in modules)
            graph.AddModule(module.Kind, new Vector2I(module.X, module.Y), module.Rotation);
        var operational = graph.GetOperationalModuleIds();
        double ore = 0, alloy = 0;
        foreach (var module in graph.Modules)
        {
            if (!operational.Contains(module.Id)) continue;
            var definition = FactoryModuleGraph.Definitions[module.Kind];
            ore += definition.OreRate;
            alloy += definition.RefineRate;
        }
        var multiplier = BiomeMultiplier(biome);
        return ((float)(ore * multiplier), (float)(alloy * multiplier));
    }

    /// <summary>资源显示名。</summary>
    public static string ResourceLabel(string kind) => kind switch
    {
        "alloy" => "合金",
        "iron" => "铁质碎料",
        _ => kind,
    };
    /// <summary>资源市场基价（R/单位），出售价在此基础上波动、随抛售压价。</summary>
    public static float ResourceBasePrice(string kind) => kind == "alloy" ? 2f : 1f;

    /// <summary>当前行情价（R/单位）：基价 × 周期波动（±18%）× 供需系数（抛售最多压 45%）。
    /// marketTime 为市场时钟（秒）；两种资源错相位波动，形成跨品种套利空间。</summary>
    public static float ResourcePrice(string kind, float pressure, float marketTime)
    {
        var phase = kind == "alloy" ? 2.399f : 0f;
        var wave = 1f + 0.18f * Mathf.Sin(marketTime * 0.07f + phase);
        var demand = 1f - Mathf.Clamp(pressure, 0f, 0.45f);
        return ResourceBasePrice(kind) * wave * demand;
    }

    /// <summary>行情趋势：true = 当前处于上行段（可显示 ↑）。</summary>
    public static bool ResourcePriceRising(string kind, float marketTime) =>
        Mathf.Cos(marketTime * 0.07f + (kind == "alloy" ? 2.399f : 0f)) > 0f;

    /// <summary>把工厂采出的资源计入库存（tick 结算整数单位）。</summary>
    public void AddResource(string kind, int amount)
    {
        if (amount <= 0) return;
        ResourceStock[kind] = (int)Math.Min(int.MaxValue - 1L, ResourceStock.GetValueOrDefault(kind) + amount);
    }

    /// <summary>市场时钟与供需压力：Time 持续推进，抛售抬升 Pressure，市场缓慢回稳。</summary>
    public void TickMarket(float dt)
    {
        MarketTime += dt;
        if (IronPressure > 0) IronPressure = Math.Max(0, IronPressure - dt * 0.02f);
        if (AlloyPressure > 0) AlloyPressure = Math.Max(0, AlloyPressure - dt * 0.02f);
    }

    public float MarketPressureOf(string kind) => kind == "alloy" ? AlloyPressure : IronPressure;

    /// <summary>抛售抬升供给压力（单位量 × 系数），立即压低后续成交价。</summary>
    public void AddMarketPressure(string kind, float amount)
    {
        if (kind == "alloy") AlloyPressure = Math.Min(0.45f, AlloyPressure + amount);
        else IronPressure = Math.Min(0.45f, IronPressure + amount);
    }

    /// <summary>读取某星球第 slot 座工厂的蓝图（未模块化/越界返回 null）。</summary>
    public List<FactoryModuleData>? FactorySlotBlueprint(string planetId, int slot)
    {
        var entry = Factories.FirstOrDefault(f => f.PlanetId == planetId);
        if (entry is null || slot < 0 || slot >= entry.Slots.Count) return null;
        var modules = entry.Slots[slot].Modules;
        return modules.Count > 0 ? modules : null;
    }

    /// <summary>
    /// 用模块化蓝图新建一座工厂（费用由调用方扣除）；容量语义与 TryAddFactory 一致。
    /// 返回新工厂的资源产出（碎料/s, 合金/s）。
    /// </summary>
    public (float Ore, float Alloy) TryAddFactoryWithBlueprint(string planetId, string biome, List<FactoryModuleData> modules)
    {
        var entry = Factories.FirstOrDefault(f => f.PlanetId == planetId);
        if (entry is null) Factories.Add(entry = new FactoryData { PlanetId = planetId, Biome = biome });
        if (entry.Count >= MaxFactoriesPerPlanet) return (0, 0);
        entry.Count++;
        var production = BlueprintProduction(biome, modules);
        entry.Slots.Add(new FactorySlotData { Modules = modules, OreRate = production.Ore, AlloyRate = production.Alloy });
        Commit();
        return production;
    }

    /// <summary>
    /// 修改某星球第 slot 座工厂的模块布局（费用/差价由调用方扣除）。
    /// 旧式工厂补空占位后升级为模块化；产出按新蓝图重新锁定。
    /// </summary>
    public (float Ore, float Alloy) TryUpdateFactorySlot(string planetId, int slot, List<FactoryModuleData> modules)
    {
        var entry = Factories.FirstOrDefault(f => f.PlanetId == planetId);
        if (entry is null || slot < 0 || slot >= entry.Count || slot >= MaxFactoriesPerPlanet) return (0, 0);
        while (entry.Slots.Count <= slot) entry.Slots.Add(new FactorySlotData());
        var production = BlueprintProduction(entry.Biome, modules);
        entry.Slots[slot] = new FactorySlotData { Modules = modules, OreRate = production.Ore, AlloyRate = production.Alloy };
        Commit();
        return production;
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
            progress.Factories ??= new(); progress.ResourceStock ??= new();
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
