using Godot;

public sealed class ModuleDefinition
{
    public required string Kind { get; init; }
    public required string Category { get; init; }
    public Vector2I Size { get; init; } = Vector2I.One;
    public required string Label { get; init; }
    public required string Description { get; init; }
    public required string ColorHex { get; init; }
    public double Mass { get; init; }
    public double Generation { get; init; }
    public double Consumption { get; init; }
    public double Heat { get; init; }
    public double OreRate { get; init; }
    public double RefineRate { get; init; }
    public double Cooling { get; init; }
    public double Storage { get; init; }
    public double EnergyCapacity { get; init; }
    public double Hull { get; init; }
    public Dictionary<string, double> Cost { get; init; } = new();
}

public sealed class ModuleInstance
{
    public int Id { get; init; }
    public required string Kind { get; init; }
    public Vector2I GridPosition { get; set; }
    /// <summary>Quarter turns clockwise. Kept in simulation so the same blueprint can render in any context.</summary>
    public int Rotation { get; set; }
    public bool Enabled { get; set; } = true;
}

public readonly record struct ModuleSnapshot(string Kind, Vector2I GridPosition, int Rotation, bool Enabled);

public readonly record struct ModuleLink(int FirstId, int SecondId);

public sealed class ModuleMetrics
{
    public double Mass { get; init; }
    public double Generation { get; init; }
    public double Consumption { get; init; }
    public double NetPower => Generation - Consumption;
    public double Heat { get; init; }
    public double Cooling { get; init; }
    public double Storage { get; init; }
    public double EnergyCapacity { get; init; }
    public double Hull { get; init; }
    public double Integrity { get; init; }
    public Dictionary<string, int> Counts { get; init; } = new();
}

/// <summary>预留给行星地表的工厂模块数据与摆放、连接逻辑；当前没有 UI 入口。</summary>
public sealed class FactoryModuleGraph
{
    public static readonly IReadOnlyDictionary<string, ModuleDefinition> Definitions =
        new Dictionary<string, ModuleDefinition>(StringComparer.Ordinal)
        {
            ["core"] = new() { Kind = "core", Category = "核心", Size = new(1, 1), Label = "种子核心", Description = "自动化据点的控制中枢", ColorHex = "#f4f1de", Mass = 12, Generation = 1, Hull = 40, Storage = 20, Cost = new() { ["iron"] = 0, ["alloy"] = 0 } },
            ["solar"] = new() { Kind = "solar", Category = "能源", Size = new(2, 1), Label = "太阳能板", Description = "稳定产出功率，几乎不产生热量", ColorHex = "#f2cc60", Mass = 4, Generation = 5, Consumption = 0.2, Heat = 0.3, Cost = new() { ["iron"] = 8, ["alloy"] = 0 } },
            ["miner"] = new() { Kind = "miner", Category = "生产", Size = new(2, 1), Label = "采矿臂", Description = "从星球矿脉持续采出铁质碎料入库", ColorHex = "#65c18c", Mass = 6, Consumption = 1.5, Heat = 1, OreRate = 0.8, Cost = new() { ["iron"] = 12, ["alloy"] = 2 } },
            ["refinery"] = new() { Kind = "refinery", Category = "生产", Size = new(2, 1), Label = "精炼炉", Description = "就地精炼矿石持续产出合金，价值更高", ColorHex = "#e58c55", Mass = 8, Consumption = 2.5, Heat = 2, RefineRate = 0.45, Cost = new() { ["iron"] = 18, ["alloy"] = 4 } },
            ["battery"] = new() { Kind = "battery", Category = "能源", Size = new(1, 2), Label = "储能单元", Description = "储存电能，短时补足生产网络的功率缺口", ColorHex = "#78a6d8", Mass = 5, Consumption = 0.1, Heat = 0.1, EnergyCapacity = 24, Cost = new() { ["iron"] = 10, ["alloy"] = 2 } },
            ["radiator"] = new() { Kind = "radiator", Category = "散热", Size = new(2, 1), Label = "散热翼", Description = "主动排出热量，让高功率设计持续工作", ColorHex = "#6ec6c7", Mass = 3, Consumption = 0.2, Heat = 0.1, Cooling = 4, Cost = new() { ["iron"] = 8, ["alloy"] = 1 } },
            ["cargo"] = new() { Kind = "cargo", Category = "物流", Size = new(2, 2), Label = "货运舱", Description = "扩大仓储，适合运输与打捞任务", ColorHex = "#d0a46a", Mass = 7, Storage = 24, Cost = new() { ["iron"] = 12, ["alloy"] = 2 } },
            ["reactor"] = new() { Kind = "reactor", Category = "能源", Size = new(2, 2), Label = "裂变炉", Description = "高功率能源核心，会提高热量和信号暴露", ColorHex = "#e0b45d", Mass = 10, Generation = 11, Consumption = 1, Heat = 4, Cost = new() { ["iron"] = 24, ["alloy"] = 8 } },
        };

    public List<ModuleInstance> Modules { get; } = new();
    public List<ModuleLink> Links { get; } = new();
    public event Action? Changed;
    private int _nextId = 1;

    public ModuleInstance? AddModule(string kind, Vector2I gridPosition, int rotation = 0)
    {
        if (!Definitions.ContainsKey(kind) || !CanOccupy(kind, gridPosition, rotation))
            return null;

        var module = new ModuleInstance { Id = _nextId++, Kind = kind, GridPosition = gridPosition, Rotation = NormalizeRotation(rotation) };
        Modules.Add(module);
        AutoLink(module);
        Changed?.Invoke();
        return module;
    }

    public bool RotateModuleAt(Vector2I gridPosition, int quarterTurns)
    {
        var module = GetModuleAt(gridPosition);
        if (module is null || module.Kind == "core")
            return false;

        var nextRotation = NormalizeRotation(module.Rotation + quarterTurns);
        if (!CanOccupy(module.Kind, module.GridPosition, nextRotation, module))
            return false;

        module.Rotation = nextRotation;
        RebuildLinks();
        Changed?.Invoke();
        return true;
    }

    public bool MoveModuleAt(Vector2I from, Vector2I to)
    {
        var module = GetModuleAt(from);
        if (module is null || module.Kind == "core" || !CanOccupy(module.Kind, to, module.Rotation, module))
            return false;

        module.GridPosition = to;
        RebuildLinks();
        Changed?.Invoke();
        return true;
    }

    public IReadOnlyList<ModuleSnapshot> CaptureSnapshot() =>
        Modules.Select(module => new ModuleSnapshot(module.Kind, module.GridPosition, module.Rotation, module.Enabled)).ToArray();

    public void RestoreSnapshot(IEnumerable<ModuleSnapshot> snapshot)
    {
        Modules.Clear();
        Links.Clear();
        _nextId = 1;
        foreach (var saved in snapshot)
        {
            var module = new ModuleInstance
            {
                Id = _nextId++,
                Kind = saved.Kind,
                GridPosition = saved.GridPosition,
                Rotation = NormalizeRotation(saved.Rotation),
                Enabled = saved.Enabled,
            };
            Modules.Add(module);
        }

        foreach (var module in Modules)
            AutoLink(module);
        Changed?.Invoke();
    }

    private void RebuildLinks()
    {
        Links.Clear();
        foreach (var module in Modules)
            AutoLink(module);
    }

    public ModuleInstance? RemoveModuleAt(Vector2I gridPosition)
    {
        var module = GetModuleAt(gridPosition);
        if (module is null || module.Kind == "core")
            return null;

        Modules.Remove(module);
        Links.RemoveAll(link => link.FirstId == module.Id || link.SecondId == module.Id);
        RebuildLinks();
        Changed?.Invoke();
        return module;
    }

    public ModuleInstance? GetModuleAt(Vector2I gridPosition) => Modules.FirstOrDefault(module => OccupiedCells(module).Contains(gridPosition));

    public ModuleInstance? GetModuleCovering(Vector2I gridPosition) => GetModuleAt(gridPosition);

    public bool HasAdjacentModule(Vector2I gridPosition)
    {
        return GetModuleAt(gridPosition + Vector2I.Right) is not null
            || GetModuleAt(gridPosition + Vector2I.Left) is not null
            || GetModuleAt(gridPosition + Vector2I.Up) is not null
            || GetModuleAt(gridPosition + Vector2I.Down) is not null;
    }

    public bool HasAdjacentModule(string kind, Vector2I anchor, int rotation, ModuleInstance? ignored = null)
    {
        if (!Definitions.ContainsKey(kind))
            return false;

        var occupied = OccupiedCells(kind, anchor, rotation).ToHashSet();
        foreach (var cell in occupied)
        {
            foreach (var direction in new[] { Vector2I.Right, Vector2I.Left, Vector2I.Up, Vector2I.Down })
            {
                var neighbor = cell + direction;
                if (occupied.Contains(neighbor))
                    continue;

                var module = GetModuleAt(neighbor);
                if (module is not null && module != ignored)
                    return true;
            }
        }

        return false;
    }

    public bool CanOccupy(string kind, Vector2I anchor, int rotation, ModuleInstance? ignored = null)
    {
        if (!Definitions.TryGetValue(kind, out _))
            return false;
        var occupied = OccupiedCells(kind, anchor, rotation).ToHashSet();
        return !Modules.Any(module => module != ignored && OccupiedCells(module).Any(occupied.Contains));
    }

    public IReadOnlyList<Vector2I> GetOccupiedCells(ModuleInstance module) => OccupiedCells(module).ToArray();

    public IReadOnlyList<Vector2I> GetOccupiedCells(string kind, Vector2I anchor, int rotation) =>
        OccupiedCells(kind, anchor, rotation).ToArray();

    public Vector2I GetFootprint(string kind, int rotation)
    {
        var size = Definitions[kind].Size;
        return NormalizeRotation(rotation) % 2 == 0 ? size : new Vector2I(size.Y, size.X);
    }

    private IEnumerable<Vector2I> OccupiedCells(ModuleInstance module) => OccupiedCells(module.Kind, module.GridPosition, module.Rotation);

    private IEnumerable<Vector2I> OccupiedCells(string kind, Vector2I anchor, int rotation)
    {
        var footprint = GetFootprint(kind, NormalizeRotation(rotation));
        for (var y = 0; y < footprint.Y; y++)
            for (var x = 0; x < footprint.X; x++)
                yield return anchor + new Vector2I(x, y);
    }

    public int CountKind(string kind) => Modules.Count(module => module.Kind == kind);

    /// <summary>
    /// Returns modules that can receive power and control from the core through
    /// physical links. A detached branch remains in the blueprint, but it cannot
    /// contribute to factory production until it is connected again.
    /// </summary>
    public HashSet<int> GetOperationalModuleIds()
    {
        var core = Modules.FirstOrDefault(module => module.Kind == "core" && module.Enabled);
        if (core is null)
            return new HashSet<int>();

        var connected = new HashSet<int> { core.Id };
        var queue = new Queue<int>();
        queue.Enqueue(core.Id);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var link in Links)
            {
                var neighborId = link.FirstId == current ? link.SecondId : link.SecondId == current ? link.FirstId : -1;
                if (neighborId < 0 || connected.Contains(neighborId))
                    continue;

                var neighbor = Modules.FirstOrDefault(module => module.Id == neighborId);
                if (neighbor is null || !neighbor.Enabled)
                    continue;

                connected.Add(neighborId);
                queue.Enqueue(neighborId);
            }
        }

        return connected;
    }

    public bool IsOperational(ModuleInstance module) => GetOperationalModuleIds().Contains(module.Id);

    /// <summary>
    /// 计算工厂模块网络的所有连通分量。
    /// 只考虑 Enabled 的模块；通过 Links 边做 BFS。返回每个分量的模块列表，
    /// 分量按是否包含 core 排序（含 core 的在前，作为"主体"）。
    /// </summary>
    public List<List<ModuleInstance>> GetConnectedComponents()
    {
        var enabled = Modules.Where(module => module.Enabled).ToList();
        var adjacency = new Dictionary<int, List<int>>();
        foreach (var module in enabled)
            adjacency[module.Id] = new List<int>();
        foreach (var link in Links)
        {
            if (adjacency.ContainsKey(link.FirstId) && adjacency.ContainsKey(link.SecondId))
            {
                adjacency[link.FirstId].Add(link.SecondId);
                adjacency[link.SecondId].Add(link.FirstId);
            }
        }

        var byId = enabled.ToDictionary(module => module.Id);
        var visited = new HashSet<int>();
        var components = new List<List<ModuleInstance>>();
        foreach (var module in enabled)
        {
            if (visited.Contains(module.Id))
                continue;
            var component = new List<ModuleInstance>();
            var queue = new Queue<int>();
            queue.Enqueue(module.Id);
            visited.Add(module.Id);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                component.Add(byId[current]);
                foreach (var neighbor in adjacency[current])
                {
                    if (visited.Contains(neighbor))
                        continue;
                    visited.Add(neighbor);
                    queue.Enqueue(neighbor);
                }
            }
            components.Add(component);
        }

        // 核心所在的工厂网络排在离线分支之前。
        return components
            .OrderByDescending(component => component.Any(module => module.Kind == "core"))
            .ToList();
    }

    public ModuleMetrics GetMetrics()
    {
        double mass = 0, generation = 0, consumption = 0, heat = 0, cooling = 0, storage = 0, energyCapacity = 0, hull = 0;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var operationalIds = GetOperationalModuleIds();
        foreach (var module in Modules)
        {
            if (!module.Enabled)
                continue;

            var definition = Definitions[module.Kind];
            // Physical mass remains present even when a branch loses power or control.
            // Only the functional contributions below require a live connection to core.
            mass += definition.Mass;
            if (!operationalIds.Contains(module.Id))
                continue;

            generation += definition.Generation;
            consumption += definition.Consumption;
            heat += definition.Heat;
            cooling += definition.Cooling;
            storage += definition.Storage;
            energyCapacity += definition.EnergyCapacity;
            hull += definition.Hull;
            counts[module.Kind] = counts.GetValueOrDefault(module.Kind) + 1;
        }

        return new ModuleMetrics
        {
            Mass = mass,
            Generation = generation,
            Consumption = consumption,
            Heat = heat,
            Cooling = cooling,
            Storage = storage,
            EnergyCapacity = energyCapacity,
            Hull = hull,
            Integrity = GetIntegrity(),
            Counts = counts,
        };
    }

    private void AutoLink(ModuleInstance added)
    {
        foreach (var other in Modules)
        {
            if (other.Id == added.Id || !AreAdjacent(added, other))
                continue;
            if (!Links.Any(link => (link.FirstId == added.Id && link.SecondId == other.Id) || (link.FirstId == other.Id && link.SecondId == added.Id)))
                Links.Add(new ModuleLink(added.Id, other.Id));
        }
    }

    private bool AreAdjacent(ModuleInstance first, ModuleInstance second)
    {
        foreach (var firstCell in OccupiedCells(first))
        foreach (var secondCell in OccupiedCells(second))
            if (Mathf.Abs(firstCell.X - secondCell.X) + Mathf.Abs(firstCell.Y - secondCell.Y) == 1)
                return true;
        return false;
    }

    private static int NormalizeRotation(int rotation) => ((rotation % 4) + 4) % 4;

    private double GetIntegrity()
    {
        if (Modules.Count == 0)
            return 0;

        var connected = GetOperationalModuleIds();
        return connected.Count * 100.0 / Modules.Count;
    }
}
