using Godot;
using System.Text.Json;

public sealed class FactionState
{
    public required string Name { get; init; }
    public double Trust { get; set; }
    public double Fear { get; set; }
    public double Debt { get; set; }
    public double Intel { get; set; }
}

public sealed class WorldState
{
    private const string BlueprintPath = "user://reworld_blueprint.json";

    public sealed class BlueprintDto
    {
        public List<BlueprintModuleDto> Modules { get; set; } = new();
    }

    public sealed class BlueprintModuleDto
    {
        public string Kind { get; set; } = string.Empty;
        public int X { get; set; }
        public int Y { get; set; }
        public int Rotation { get; set; }
        public bool Enabled { get; set; } = true;
    }

    private sealed class WorldSnapshot
    {
        public required IReadOnlyList<ModuleSnapshot> FactoryModules { get; init; }
        public required IReadOnlyList<ModuleSnapshot> ShipModules { get; init; }
        public required Dictionary<string, double> Resources { get; init; }
        public double Heat { get; init; }
    }

    public ModuleGraph FactoryGraph { get; } = new();
    public ModuleGraph ShipGraph { get; } = new();
    public ModuleGraph Graph => FactoryGraph;
    public Dictionary<string, double> Resources { get; } = new() { ["iron"] = 42, ["alloy"] = 8, ["volatile"] = 18, ["crystal"] = 0 };
    public Dictionary<string, FactionState> Factions { get; } = new()
    {
        ["guild"] = new() { Name = "航路公社" },
        ["forge"] = new() { Name = "熔核铸团" },
        ["bloom"] = new() { Name = "绽变群落" },
    };
    public List<string> EventLog { get; } = new();
    public double Elapsed { get; private set; }
    public double CurrentHeat { get; private set; }
    public double HeatCapacity { get; private set; } = 18.0;
    public double PowerRatio { get; private set; } = 1.0;
    public double StorageCapacity { get; private set; } = 80.0;
    public double StorageUsed => Resources.Values.Sum();
    public bool Paused { get; private set; }
    public double TimeScale { get; private set; } = 1.0;
    public int CombatVictories { get; private set; }
    public int ArchiveFragments { get; private set; }
    public bool StormGateUnlocked { get; private set; }
    public event Action? Changed;
    private double _statusTimer;
    private readonly Stack<WorldSnapshot> _undo = new();
    private readonly Stack<WorldSnapshot> _redo = new();
    private bool _restoring;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public WorldState()
    {
        FactoryGraph.Changed += () => Changed?.Invoke();
        ShipGraph.Changed += () => Changed?.Invoke();
        SeedDemoLayout();
        Log("演示布局已上线：工厂正在采矿与精炼，舰船蓝图可直接试航。");
    }

    private void SeedDemoLayout()
    {
        // Open with a readable vertical slice instead of an empty canvas. These modules are
        // seeded as the starting scenario, so the player can immediately inspect production,
        // switch to the ship tab, and enter the test range without a setup tax.
        FactoryGraph.AddModule("core", Vector2I.Zero);
        FactoryGraph.AddModule("solar", new Vector2I(1, 0));
        FactoryGraph.AddModule("battery", new Vector2I(0, 1));
        FactoryGraph.AddModule("miner", new Vector2I(1, 1));
        FactoryGraph.AddModule("refinery", new Vector2I(3, 1));
        FactoryGraph.AddModule("cargo", new Vector2I(3, 2));

        ShipGraph.AddModule("core", Vector2I.Zero);
        ShipGraph.AddModule("hull", new Vector2I(-1, -1), 1);
        ShipGraph.AddModule("hull", new Vector2I(1, 0));
        // Two rear engines sit above and below the center line. They provide forward
        // thrust together, while differential firing gives the test ship real yaw control.
        ShipGraph.AddModule("thruster", new Vector2I(-2, -1), 3);
        ShipGraph.AddModule("thruster", new Vector2I(-2, 1), 3);
        ShipGraph.AddModule("radiator", new Vector2I(1, 2));
        ShipGraph.AddModule("laser", new Vector2I(3, 0));
        ShipGraph.AddModule("shield", new Vector2I(3, 1));
        ShipGraph.AddModule("cargo", new Vector2I(3, 2));
        ShipGraph.AddModule("reactor", new Vector2I(5, 0));
    }

    public void Tick(double delta)
    {
        if (Paused)
            return;

        Elapsed += delta;
        _statusTimer += delta;
        var metrics = FactoryGraph.GetMetrics();
        StorageCapacity = 80.0 + metrics.Storage;
        HeatCapacity = 18.0 + metrics.Hull * 0.35 + metrics.Cooling * 1.5;
        CurrentHeat = Math.Clamp(CurrentHeat + metrics.Heat * delta - metrics.Cooling * delta, 0.0, HeatCapacity * 1.8);
        var basePowerRatio = metrics.Consumption > 0
            ? Math.Clamp(metrics.Generation / metrics.Consumption, 0.0, 1.0)
            : 1.0;
        var thermalRatio = HeatCapacity > 0.0 ? CurrentHeat / HeatCapacity : 0.0;
        var thermalPenalty = thermalRatio <= 1.0 ? 1.0 : Math.Clamp(1.0 - (thermalRatio - 1.0) * 0.8, 0.15, 1.0);
        PowerRatio = basePowerRatio * thermalPenalty;
        var powerRatio = PowerRatio;
        var minerCount = metrics.Counts.GetValueOrDefault("miner");
        var refineryCount = metrics.Counts.GetValueOrDefault("refinery");
        var freeStorage = Math.Max(0.0, StorageCapacity - StorageUsed);
        var mined = Math.Min(minerCount * 0.8 * powerRatio * delta, freeStorage);
        Resources["iron"] += mined;
        var refined = Math.Min(Resources["iron"], refineryCount * 0.45 * powerRatio * delta);
        Resources["iron"] -= refined;
        Resources["alloy"] += refined;

        if (_statusTimer >= 5)
        {
            _statusTimer = 0;
            if (powerRatio < 0.99 && metrics.Consumption > 0) Log("功率不足：增加太阳能板或储能单元。");
            else if (minerCount > 0 && refineryCount == 0) Log("采矿网络在线：建造精炼炉，把铁质碎料转成合金。");
            else if (refineryCount > 0 && refined > 0) Log("工厂完成一轮生产：合金可以用于舰体模块。");
        }
        Changed?.Invoke();
    }

    public bool BuildModule(string kind, Vector2I gridPosition, int rotation = 0)
    {
        if (!ModuleGraph.Definitions.TryGetValue(kind, out var definition)) return false;
        if (!FactoryGraph.CanOccupy(kind, gridPosition, rotation)) { Log("工厂建造失败：模块占格与现有结构重叠。"); return false; }
        if (!FactoryGraph.HasAdjacentModule(kind, gridPosition, rotation)) { Log("工厂建造失败：新模块必须连接到现有结构。"); return false; }
        if (definition.Cost.Any(pair => Resources.GetValueOrDefault(pair.Key) < pair.Value))
        {
            var missing = definition.Cost.First(pair => Resources.GetValueOrDefault(pair.Key) < pair.Value).Key;
            Log($"建造失败：{ResourceLabel(missing)}不足。");
            return false;
        }

        SaveUndoPoint();
        if (FactoryGraph.AddModule(kind, gridPosition, rotation) is null)
        {
            Log("工厂建造失败：模块无法放入这个占格。");
            return false;
        }
        foreach (var pair in definition.Cost) Resources[pair.Key] -= pair.Value;
        Log($"已部署「{definition.Label}」至网格 [{gridPosition.X}, {gridPosition.Y}]。");
        Changed?.Invoke();
        return true;
    }

    public bool RemoveModule(Vector2I gridPosition)
    {
        if (FactoryGraph.GetModuleAt(gridPosition) is null)
        {
            Log("拆除失败：这个网格没有模块。");
            return false;
        }

        var target = FactoryGraph.GetModuleAt(gridPosition)!;
        if (target.Kind == "core") { Log("拆除失败：种子核心不能被移除。"); return false; }
        SaveUndoPoint();
        var removed = FactoryGraph.RemoveModuleAt(gridPosition)!;
        Log($"已拆除「{ModuleGraph.Definitions[removed.Kind].Label}」。原型阶段不返还材料。");
        Changed?.Invoke();
        return true;
    }

    public bool RotateModule(Vector2I gridPosition, int quarterTurns)
    {
        var module = FactoryGraph.GetModuleAt(gridPosition);
        if (module is null)
        {
            Log("旋转失败：这个网格没有模块。");
            return false;
        }
        if (module.Kind == "core")
        {
            Log("种子核心的方向固定。");
            return false;
        }

        if (!FactoryGraph.CanOccupy(module.Kind, module.GridPosition, module.Rotation + quarterTurns, module))
        {
            Log("旋转失败：旋转后的占格与现有结构重叠。");
            return false;
        }

        SaveUndoPoint();
        if (!FactoryGraph.RotateModuleAt(gridPosition, quarterTurns))
            return false;
        var direction = module.Rotation switch { 0 => "北", 1 => "东", 2 => "南", _ => "西" };
        Log($"已旋转「{ModuleGraph.Definitions[module.Kind].Label}」：接口朝向{direction}。");
        Changed?.Invoke();
        return true;
    }

    public bool MoveModule(Vector2I from, Vector2I to)
    {
        var module = FactoryGraph.GetModuleAt(from);
        if (module is null)
        {
            Log("移动失败：起点没有模块。");
            return false;
        }
        if (module.Kind == "core")
        {
            Log("种子核心不能移动。");
            return false;
        }
        if (!FactoryGraph.CanOccupy(module.Kind, to, module.Rotation, module))
        {
            Log("移动失败：目标占格与现有结构重叠。");
            return false;
        }
        if (!FactoryGraph.HasAdjacentModule(module.Kind, to, module.Rotation, module))
        {
            Log("移动失败：模块必须继续连接到结构。");
            return false;
        }

        SaveUndoPoint();
        if (!FactoryGraph.MoveModuleAt(from, to))
            return false;
        Log($"已移动「{ModuleGraph.Definitions[module.Kind].Label}」至网格 [{to.X}, {to.Y}]。");
        Changed?.Invoke();
        return true;
    }

    public bool BuildShipModule(string kind, Vector2I gridPosition, int rotation = 0)
    {
        if (!ModuleGraph.Definitions.ContainsKey(kind))
            return false;
        if (!ShipGraph.CanOccupy(kind, gridPosition, rotation))
        {
            Log("舰船设计失败：模块占格与现有舰体重叠。");
            return false;
        }
        if (!ShipGraph.HasAdjacentModule(kind, gridPosition, rotation))
        {
            Log("舰船设计失败：模块必须连接到舰体结构。");
            return false;
        }

        SaveUndoPoint();
        if (ShipGraph.AddModule(kind, gridPosition, rotation) is null)
            return false;
        Log($"舰船蓝图已安装「{ModuleGraph.Definitions[kind].Label}」。设计模式不消耗工厂库存。");
        Changed?.Invoke();
        return true;
    }

    public bool RemoveShipModule(Vector2I gridPosition)
    {
        var module = ShipGraph.GetModuleAt(gridPosition);
        if (module is null)
        {
            Log("舰船拆除失败：这个网格没有模块。");
            return false;
        }
        if (module.Kind == "core")
        {
            Log("舰船核心不能拆除。");
            return false;
        }

        SaveUndoPoint();
        ShipGraph.RemoveModuleAt(gridPosition);
        Log($"舰船蓝图已拆除「{ModuleGraph.Definitions[module.Kind].Label}」。");
        Changed?.Invoke();
        return true;
    }

    public bool RotateShipModule(Vector2I gridPosition, int quarterTurns)
    {
        var module = ShipGraph.GetModuleAt(gridPosition);
        if (module is null)
        {
            Log("舰船旋转失败：这个网格没有模块。");
            return false;
        }
        if (module.Kind == "core")
        {
            Log("舰船核心方向固定。");
            return false;
        }

        if (!ShipGraph.CanOccupy(module.Kind, module.GridPosition, module.Rotation + quarterTurns, module))
        {
            Log("舰船旋转失败：旋转后的占格与现有舰体重叠。");
            return false;
        }

        SaveUndoPoint();
        if (!ShipGraph.RotateModuleAt(gridPosition, quarterTurns))
            return false;
        Log($"舰船模块「{ModuleGraph.Definitions[module.Kind].Label}」已旋转。");
        Changed?.Invoke();
        return true;
    }

    public bool MoveShipModule(Vector2I from, Vector2I to)
    {
        var module = ShipGraph.GetModuleAt(from);
        if (module is null)
        {
            Log("舰船移动失败：起点没有模块。");
            return false;
        }
        if (module.Kind == "core")
        {
            Log("舰船核心不能移动。");
            return false;
        }
        if (!ShipGraph.CanOccupy(module.Kind, to, module.Rotation, module))
        {
            Log("舰船移动失败：目标占格与现有舰体重叠。");
            return false;
        }
        if (!ShipGraph.HasAdjacentModule(module.Kind, to, module.Rotation, module))
        {
            Log("舰船移动失败：模块必须继续连接到舰体结构。");
            return false;
        }

        SaveUndoPoint();
        if (!ShipGraph.MoveModuleAt(from, to))
            return false;
        Log($"舰船模块「{ModuleGraph.Definitions[module.Kind].Label}」已移动。");
        Changed?.Invoke();
        return true;
    }

    public bool SaveBlueprint()
    {
        var blueprint = new BlueprintDto
        {
            Modules = ShipGraph.Modules.Select(module => new BlueprintModuleDto
            {
                Kind = module.Kind,
                X = module.GridPosition.X,
                Y = module.GridPosition.Y,
                Rotation = module.Rotation,
                Enabled = module.Enabled,
            }).ToList(),
        };

        var file = Godot.FileAccess.Open(BlueprintPath, Godot.FileAccess.ModeFlags.Write);
        if (file is null)
        {
            Log("蓝图保存失败：无法打开本地存档。");
            return false;
        }
        file.StoreString(JsonSerializer.Serialize(blueprint));
        file.Close();
        Log($"蓝图已保存：{blueprint.Modules.Count} 个模块。");
        return true;
    }

    public bool LoadBlueprint()
    {
        if (!Godot.FileAccess.FileExists(BlueprintPath))
        {
            Log("还没有保存的蓝图。");
            return false;
        }

        var file = Godot.FileAccess.Open(BlueprintPath, Godot.FileAccess.ModeFlags.Read);
        if (file is null)
        {
            Log("蓝图载入失败：无法读取本地存档。");
            return false;
        }
        var json = file.GetAsText();
        file.Close();

        BlueprintDto? blueprint;
        try
        {
            blueprint = JsonSerializer.Deserialize<BlueprintDto>(json);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            Log("蓝图载入失败：文件格式损坏。");
            return false;
        }

        if (blueprint is null || blueprint.Modules.Count == 0 || blueprint.Modules.Count(module => module.Kind == "core") != 1 ||
            blueprint.Modules.Any(module => !ModuleGraph.Definitions.ContainsKey(module.Kind)))
        {
            Log("蓝图载入失败：缺少唯一核心或包含未知模块。");
            return false;
        }

        var snapshots = blueprint.Modules
            .Select(module => new ModuleSnapshot(module.Kind, new Vector2I(module.X, module.Y), module.Rotation, module.Enabled))
            .ToList();

        var occupiedCells = new HashSet<Vector2I>();
        foreach (var module in snapshots)
        {
            foreach (var cell in ShipGraph.GetOccupiedCells(module.Kind, module.GridPosition, module.Rotation))
            {
                if (occupiedCells.Add(cell))
                    continue;

                Log("蓝图载入失败：矩形模块展开后存在重叠占格。");
                return false;
            }
        }

        if (snapshots.GroupBy(module => module.GridPosition).Any(group => group.Count() > 1))
        {
            Log("蓝图载入失败：存在重叠模块。");
            return false;
        }

        SaveUndoPoint();
        ShipGraph.RestoreSnapshot(snapshots);
        Log($"蓝图已载入：{snapshots.Count} 个模块。资源库存保持不变。");
        Changed?.Invoke();
        return true;
    }

    public bool Undo()
    {
        if (!CanUndo)
        {
            Log("没有可撤销的建造操作。");
            return false;
        }

        var current = CaptureSnapshot();
        _redo.Push(current);
        var previous = _undo.Pop();
        Restore(previous);
        Log("已撤销上一项建造操作。");
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo)
        {
            Log("没有可重做的建造操作。");
            return false;
        }

        var current = CaptureSnapshot();
        _undo.Push(current);
        var next = _redo.Pop();
        Restore(next);
        Log("已重做建造操作。");
        return true;
    }

    public void ChooseContract(string choice)
    {
        var guild = Factions["guild"];
        switch (choice)
        {
            case "help":
                if (Resources["volatile"] < 5) { Log("援助失败：运输队需要 5 单位挥发物。"); return; }
                Resources["volatile"] -= 5; Resources["alloy"] += 8; guild.Trust += 15; guild.Debt = Math.Max(0, guild.Debt - 1);
                Log("你援助了航路公社：获得低损耗管线蓝图，公社信任上升。"); break;
            case "rob":
                Resources["volatile"] += 18; guild.Trust -= 20; guild.Fear += 22; guild.Intel += 5;
                Log("你劫掠了航路公社：获得燃料，但巡逻警戒度上升。"); break;
            case "trade":
                if (Resources["alloy"] < 5) { Log("交易失败：需要 5 单位合金购买运输清单。"); return; }
                Resources["alloy"] -= 5; Resources["volatile"] += 10; guild.Trust += 4; guild.Debt += 1;
                Log("你与航路公社完成交易：获得运输清单，但欠下一份人情。"); break;
        }
        Changed?.Invoke();
    }

    public void Announce(string message) => Log(message);

    public void TogglePause()
    {
        Paused = !Paused;
        Log(Paused ? "模拟已暂停。" : $"模拟继续运行，时间倍率 {TimeScale:0.0}x。");
    }

    public void AdjustTimeScale(double delta)
    {
        TimeScale = Math.Clamp(TimeScale + delta, 0.25, 4.0);
        Log($"时间倍率调整为 {TimeScale:0.0}x。");
    }

    public void AddCombatHeat(double amount)
    {
        CurrentHeat = Math.Clamp(CurrentHeat + Math.Max(0.0, amount), 0.0, HeatCapacity * 1.8);
    }

    public void AwardSalvage()
    {
        Resources["alloy"] += 6;
        Resources["crystal"] += 2;
        Resources["volatile"] += 4;
        CombatVictories++;
        ArchiveFragments++;
        StormGateUnlocked = true;
        Factions["guild"].Trust += 5;
        Log($"残骸回收完成：获得 6 合金、2 晶体、4 挥发物和 1 档案碎片。风暴闸门航线已解锁（远征 {CombatVictories}）。");
        Changed?.Invoke();
    }

    public void RecordCombatLoss()
    {
        Log("试航记录已保存：舰体失去响应。返回模块实验室改装后可以重新挑战，已发现的航线不会丢失。");
    }

    public ModuleMetrics GetMetrics() => ShipGraph.GetMetrics();

    public ModuleMetrics GetFactoryMetrics() => FactoryGraph.GetMetrics();

    public double GetShipPowerRatio()
    {
        var metrics = ShipGraph.GetMetrics();
        return metrics.Consumption > 0.0
            ? Math.Clamp(metrics.Generation / metrics.Consumption, 0.0, 1.0)
            : 1.0;
    }

    private static string ResourceLabel(string resource) => resource switch
    {
        "iron" => "铁质碎料",
        "alloy" => "合金",
        "volatile" => "挥发物",
        "crystal" => "晶体",
        _ => resource,
    };

    public void Log(string message)
    {
        EventLog.Insert(0, message);
        if (EventLog.Count > 6) EventLog.RemoveAt(EventLog.Count - 1);
        Changed?.Invoke();
    }

    private void SaveUndoPoint()
    {
        if (_restoring)
            return;

        _undo.Push(CaptureSnapshot());
        _redo.Clear();
    }

    private WorldSnapshot CaptureSnapshot() => new()
    {
        FactoryModules = FactoryGraph.CaptureSnapshot(),
        ShipModules = ShipGraph.CaptureSnapshot(),
        Resources = new Dictionary<string, double>(Resources, StringComparer.Ordinal),
        Heat = CurrentHeat,
    };

    private void Restore(WorldSnapshot snapshot)
    {
        _restoring = true;
        try
        {
            FactoryGraph.RestoreSnapshot(snapshot.FactoryModules);
            ShipGraph.RestoreSnapshot(snapshot.ShipModules);
            Resources.Clear();
            foreach (var pair in snapshot.Resources)
                Resources[pair.Key] = pair.Value;
            CurrentHeat = snapshot.Heat;
        }
        finally
        {
            _restoring = false;
        }
        Changed?.Invoke();
    }
}
