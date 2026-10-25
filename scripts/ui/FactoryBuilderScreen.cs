using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// 地表工厂的模块化建造界面（接入 FactoryModuleGraph）。
/// 登陆星球后经地表作业面板「建造工厂」打开：为空槽规划新蓝图，或修改已建工厂的模块布局。
/// 蓝图决定工厂产出——矿机采出矿石，精炼炉提升 25% 效率，产出按群系倍率折算成 R
/// 自动结算入账，可用于造舰、科技解密与外交援助。
/// 交互：[左键] 放置 / 选中    [Shift+左键] 移动选中模块    [R] 旋转    [Delete] 拆除    [ESC/G] 关闭
/// </summary>
public partial class FactoryBuilderScreen : Node2D
{
    private const float ViewWidth = 1280;
    private const float ViewHeight = 720;
    private const int GridCols = 14;
    private const int GridRows = 10;
    private const float Cell = 44f;
    private const float SlotWidth = 150;
    private const float SlotHeight = 44;
    private static readonly Vector2 GridOrigin = new(60, 150);

    private Font _font = null!;
    private string _planetId = "";
    private string _planetName = "";
    private string _biome = "iron";
    /// <summary>打开时该星球已建工厂数（新建槽位在它之后）。</summary>
    private int _existingCount;
    private readonly List<(bool Built, bool Modular, float OreRate, float AlloyRate)> _slots = new();
    private int _slot = -1;
    private FactoryModuleGraph _graph = new();

    private string _selectedKind = "core";
    private int _previewRotation;
    private ModuleInstance? _selection;
    private Vector2 _pointer;
    private string _notice = "先放置种子核心，再围绕它布置矿机与能源网络";

    /// <summary>关闭时带给 UniverseView 的结果提示；取消为 null。</summary>
    public string? ResultMessage { get; private set; }
    public PlayerProgress Progress { get; set; } = new();
    public event Action? CloseRequested;

    public override void _Ready()
    {
        _font = new SystemFont
        {
            FontNames = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Noto Sans CJK SC" },
            AllowSystemFallback = true,
        };
    }

    public override void _Process(double delta) => QueueRedraw();

    /// <summary>登陆星球后打开界面：planetId/biome 决定存档位置与产出倍率。</summary>
    public void Open(string planetName, string planetId, string biome)
    {
        _planetName = planetName;
        _planetId = planetId;
        _biome = biome;
        _existingCount = Progress.FactoryCountOf(planetId);
        ResultMessage = null;
        _slots.Clear();
        for (var i = 0; i < PlayerProgress.MaxFactoriesPerPlanet; i++)
        {
            if (i < _existingCount)
            {
                var blueprint = Progress.FactorySlotBlueprint(planetId, i);
                var (ore, alloy) = FactorySlotProduction(planetId, i);
                _slots.Add((true, blueprint is not null, ore, alloy));
            }
            else _slots.Add((false, false, 0, 0));
        }
        SelectSlot(Math.Min(_existingCount, PlayerProgress.MaxFactoriesPerPlanet - 1));
        _notice = "矿机采碎料 · 精炼炉产合金 · 产出入库，到空间站出售换 R（行情波动）";
    }

    private (float Ore, float Alloy) FactorySlotProduction(string planetId, int slot)
    {
        var entry = Progress.Factories.FirstOrDefault(f => f.PlanetId == planetId);
        if (entry is null || slot >= entry.Slots.Count) return (0, 0);
        return (entry.Slots[slot].OreRate, entry.Slots[slot].AlloyRate);
    }

    private void SelectSlot(int slot)
    {
        _slot = slot;
        _graph = new FactoryModuleGraph();
        if (slot < _existingCount && Progress.FactorySlotBlueprint(_planetId, slot) is { } blueprint)
            foreach (var module in blueprint)
                _graph.AddModule(module.Kind, new Vector2I(module.X, module.Y), module.Rotation);
        if (_graph.Modules.Count == 0)
            _graph.AddModule("core", new Vector2I(GridCols / 2, GridRows / 2));
        _selection = null;
        _previewRotation = 0;
        _selectedKind = "core";
    }

    private List<FactoryModuleData> CaptureGraph() => _graph.Modules
        .Select(m => new FactoryModuleData { Kind = m.Kind, X = m.GridPosition.X, Y = m.GridPosition.Y, Rotation = m.Rotation })
        .ToList();

    private bool HasCore => _graph.Modules.Any(m => m.Kind == "core");

    /// <summary>放置规则：首块必须是种子核心；其余模块须与现有网络相邻且格位不冲突。</summary>
    private bool CanPlaceAt(string kind, Vector2I cell, int rotation)
    {
        if (!FactoryModuleGraph.Definitions.ContainsKey(kind)) return false;
        if (kind == "core") return !HasCore && _graph.CanOccupy(kind, cell, rotation);
        if (!HasCore) return false;
        return _graph.HasAdjacentModule(kind, cell, rotation) && _graph.CanOccupy(kind, cell, rotation);
    }

    private void TryPlaceAt(Vector2I cell)
    {
        if (!CanPlaceAt(_selectedKind, cell, _previewRotation))
        {
            _notice = !HasCore && _selectedKind != "core"
                ? "第一块模块必须是种子核心"
                : HasCore && _selectedKind == "core" ? "种子核心只能有一座"
                : _graph.HasAdjacentModule(_selectedKind, cell, _previewRotation) ? "位置与现有模块冲突" : "模块必须与现有网络相邻";
            return;
        }
        if (_graph.AddModule(_selectedKind, cell, _previewRotation) is { } placed)
        {
            _selection = placed;
            _notice = $"{FactoryModuleGraph.Definitions[_selectedKind].Label} 已就位";
        }
    }

    private void TryConfirm()
    {
        if (!HasCore) { _notice = "蓝图需要一颗种子核心"; return; }
        var modules = CaptureGraph();
        var newCost = PlayerProgress.BlueprintCostR(modules);
        var isNew = _slot >= _existingCount;
        int total;
        if (isNew) total = PlayerProgress.FactoryCost(_existingCount) + newCost;
        else
        {
            var old = Progress.FactorySlotBlueprint(_planetId, _slot);
            total = Math.Max(0, newCost - (old is null ? 0 : PlayerProgress.BlueprintCostR(old)));
        }
        if (!Progress.Spend(total)) { _notice = $"资源不足 · 还差 {total - Progress.Resources:N0} R"; return; }

        (float Ore, float Alloy) production;
        if (isNew)
        {
            production = Progress.TryAddFactoryWithBlueprint(_planetId, _biome, modules);
            _existingCount++;
        }
        else production = Progress.TryUpdateFactorySlot(_planetId, _slot, modules);
        _slots[_slot] = (true, true, production.Ore, production.Alloy);
        ResultMessage = production.Ore + production.Alloy > 0
            ? (isNew
                ? $"工厂建成 · 耗 {total:N0} R · 产碎料 {production.Ore * 60f:0.#}/分 · 产合金 {production.Alloy * 60f:0.#}/分 · 空间站出售换 R"
                : $"工厂改造完成 · 耗 {total:N0} R · 新产出 碎料 {production.Ore * 60f:0.#}/分 · 合金 {production.Alloy * 60f:0.#}/分")
            : isNew
                ? $"工厂建成 · 耗 {total:N0} R · 未放置矿机/精炼炉 · 暂无产出"
                : $"工厂改造完成 · 耗 {total:N0} R · 未放置矿机/精炼炉 · 暂无产出";
        CloseRequested?.Invoke();
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            if (key.Keycode is Key.Escape or Key.G) { ResultMessage = null; CloseRequested?.Invoke(); }
            else if (key.Keycode is Key.R or Key.E)
            {
                if (_selection is { } module)
                {
                    if (!_graph.RotateModuleAt(module.GridPosition, 1)) _notice = "该朝向放不下";
                }
                else _previewRotation = (_previewRotation + 1) % 4;
            }
            else if (key.Keycode is Key.Delete or Key.Backspace)
            {
                if (_selection is { } module)
                {
                    if (_graph.RemoveModuleAt(module.GridPosition) is null) _notice = "种子核心不可拆除";
                    else _notice = "模块已拆除";
                    _selection = null;
                }
                else _notice = "先点击选中要拆除的模块";
            }
            GetViewport().SetInputAsHandled();
            return;
        }
        if (@event is InputEventMouseMotion motion) { _pointer = motion.Position; return; }
        if (@event is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click) return;

        if (HitSlot(click.Position, out var slot))
        {
            if (slot <= _existingCount) { SelectSlot(slot); _notice = slot < _existingCount ? "载入工厂蓝图 · 可修改布局" : "空槽规划新工厂"; }
            else _notice = "先按顺序建满前置槽位";
            GetViewport().SetInputAsHandled();
            return;
        }
        if (HitPalette(click.Position, out var kind))
        {
            _selectedKind = kind;
            _selection = null;
            GetViewport().SetInputAsHandled();
            return;
        }
        if (ConfirmButton.HasPoint(click.Position)) { TryConfirm(); GetViewport().SetInputAsHandled(); return; }
        if (CancelButton.HasPoint(click.Position)) { ResultMessage = null; CloseRequested?.Invoke(); GetViewport().SetInputAsHandled(); return; }

        var cell = CellAt(click.Position);
        if (cell is null) return;
        if (_graph.GetModuleAt(cell.Value) is { } hit)
        {
            _selection = hit;
            _selectedKind = hit.Kind;
            _notice = $"已选中 {FactoryModuleGraph.Definitions[hit.Kind].Label}";
        }
        else if (click.ShiftPressed && _selection is { } moving)
        {
            if (_graph.MoveModuleAt(moving.GridPosition, cell.Value))
            {
                _selection = _graph.GetModuleAt(cell.Value);
                _notice = "模块已迁移 · 断开网络的模块不产出";
            }
            else _notice = "目标格位放不下";
        }
        else TryPlaceAt(cell.Value);
        GetViewport().SetInputAsHandled();
    }

    private Vector2I? CellAt(Vector2 screen)
    {
        var local = (screen - GridOrigin) / Cell;
        var x = (int)local.X;
        var y = (int)local.Y;
        if (x < 0 || y < 0 || x >= GridCols || y >= GridRows) return null;
        return new Vector2I(x, y);
    }

    private bool HitSlot(Vector2 point, out int slot)
    {
        slot = -1;
        for (var i = 0; i < PlayerProgress.MaxFactoriesPerPlanet; i++)
        {
            var rect = new Rect2(SlotRectOrigin(i), new Vector2(SlotWidth, SlotHeight));
            if (rect.HasPoint(point)) { slot = i; return true; }
        }
        return false;
    }

    private static Vector2 SlotRectOrigin(int index) => new(60 + index * (SlotWidth + 8), 84);

    private readonly List<(Rect2 Rect, string Kind)> _paletteButtons = new();
    private bool HitPalette(Vector2 point, out string kind)
    {
        kind = "";
        foreach (var (rect, candidate) in _paletteButtons)
            if (rect.HasPoint(point)) { kind = candidate; return true; }
        return false;
    }

    private Rect2 ConfirmButton => new(712, 640, 276, 48);
    private Rect2 CancelButton => new(1004, 640, 264, 48);

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, new Vector2(ViewWidth, ViewHeight)), ReassemblyUi.VoidBlack);
        DrawBackdrop();
        DrawTopBar();
        DrawSlotStrip();
        DrawGrid();
        DrawPalette();
        DrawMetrics();
        DrawActions();
        DrawNotice();
    }

    private void DrawBackdrop()
    {
        var gridColor = new Color(ReassemblyUi.CyanGrid, 0.28f);
        for (var x = 0f; x < ViewWidth; x += 96f) DrawLine(new Vector2(x, 0), new Vector2(x, ViewHeight), gridColor, 1f);
        for (var y = 0f; y < ViewHeight; y += 96f) DrawLine(new Vector2(0, y), new Vector2(ViewWidth, y), gridColor, 1f);
        DrawRect(new Rect2(0, 0, ViewWidth, 72), new Color("#162330", 0.94f));
        DrawRect(new Rect2(0, ViewHeight - 118, ViewWidth, 118), new Color("#162330", 0.94f));
        DrawLine(new Vector2(0, 72), new Vector2(ViewWidth, 72), ReassemblyUi.PanelBorder, 1.2f);
        DrawLine(new Vector2(0, ViewHeight - 118), new Vector2(ViewWidth, ViewHeight - 118), ReassemblyUi.PanelBorder, 1.2f);
        DrawLine(new Vector2(692, 72), new Vector2(692, ViewHeight - 118), ReassemblyUi.PanelBorder, 1f);
    }

    private void DrawTopBar()
    {
        DrawString(_font, new Vector2(24, 32), $"地表工厂 // 模块化建造 · {_planetName}", fontSize: 19,
            modulate: ReassemblyUi.TextWhite);
        var biomeLabel = PlayerProgress.BiomeLabel(_biome);
        DrawString(_font, new Vector2(24, 56),
            $"{biomeLabel}矿床 · 群系产出 ×{PlayerProgress.BiomeMultiplier(_biome):0.#} · 已建 {_existingCount}/{PlayerProgress.MaxFactoriesPerPlanet} 座",
            fontSize: 12, modulate: ReassemblyUi.TextSteel);
        DrawString(_font, new Vector2(ViewWidth - 260, 34), $"R  {Progress.Resources:N0}", fontSize: 17,
            modulate: ReassemblyUi.TacticalAmber);
        DrawString(_font, new Vector2(ViewWidth - 430, 56), "[左键] 放置 / 选中   [Shift+左键] 移动   [R] 旋转   [Del] 拆除", fontSize: 11,
            modulate: ReassemblyUi.TextMuted);
    }

    private void DrawSlotStrip()
    {
        for (var i = 0; i < PlayerProgress.MaxFactoriesPerPlanet; i++)
        {
            var rect = new Rect2(SlotRectOrigin(i), new Vector2(SlotWidth, SlotHeight));
            var (built, modular, ore, alloy) = _slots[i];
            var selected = i == _slot;
            var active = i <= _existingCount;
            var border = selected ? ReassemblyUi.CyanHighlight : active ? ReassemblyUi.CyanMuted : ReassemblyUi.PanelBorder;
            ReassemblyUi.DrawChamferFrame(this, rect, selected ? new Color("#12303a") : new Color("#0a141c"),
                border, 4, selected, selected ? 1.4f : 0.9f);
            if (!built)
            {
                DrawString(_font, rect.Position + new Vector2(0, 28), i == _existingCount ? "＋ 新建工厂" : "未解锁槽位",
                    HorizontalAlignment.Center, SlotWidth, 12, active ? ReassemblyUi.TextWhite : ReassemblyUi.TextMuted);
            }
            else
            {
                DrawString(_font, rect.Position + new Vector2(0, 19),
                    ReassemblyUi.FitText(_font, $"工厂 {i + 1}{(modular ? "" : " · 旧式")}", 11, SlotWidth),
                    HorizontalAlignment.Center, SlotWidth, 11, modular ? ReassemblyUi.TextWhite : ReassemblyUi.TextSteel);
                DrawString(_font, rect.Position + new Vector2(0, 37),
                    modular
                        ? (ore + alloy > 0
                            ? ReassemblyUi.FitText(_font, $"碎料 {ore * 60f:0.#} · 合金 {alloy * 60f:0.#}/分", 10, SlotWidth)
                            : "无产出模块")
                        : "可升级改造",
                    HorizontalAlignment.Center, SlotWidth, 10,
                    modular ? (ore + alloy > 0 ? ReassemblyUi.PhosphorGreen : ReassemblyUi.TextMuted) : ReassemblyUi.TextMuted);
            }
        }
    }

    private void DrawGrid()
    {
        var board = new Rect2(GridOrigin, new Vector2(GridCols * Cell, GridRows * Cell));
        ReassemblyUi.DrawChamferFrame(this, board.Grow(8), new Color("#0a141cf2"), ReassemblyUi.CyanMuted, 8, true, 1.1f);
        DrawRect(board, new Color("#050b12", 0.9f));
        for (var x = 0; x <= GridCols; x++)
            DrawLine(GridOrigin + new Vector2(x * Cell, 0), GridOrigin + new Vector2(x * Cell, GridRows * Cell),
                ReassemblyUi.CyanGrid, 1f);
        for (var y = 0; y <= GridRows; y++)
            DrawLine(GridOrigin + new Vector2(0, y * Cell), GridOrigin + new Vector2(GridCols * Cell, y * Cell),
                ReassemblyUi.CyanGrid, 1f);

        // 模块间物理连接：断开核心网络的分支变暗。
        var mainComponent = _graph.GetConnectedComponents()
            .FirstOrDefault(component => component.Any(module => module.Kind == "core")) ?? new List<ModuleInstance>();
        var byId = _graph.Modules.ToDictionary(module => module.Id);
        foreach (var link in _graph.Links)
        {
            if (!byId.TryGetValue(link.FirstId, out var first) || !byId.TryGetValue(link.SecondId, out var second)) continue;
            DrawLine(CenterOf(first), CenterOf(second), new Color(ReassemblyUi.PointCyan, 0.35f), 1.4f);
        }

        foreach (var module in _graph.Modules)
        {
            var definition = FactoryModuleGraph.Definitions[module.Kind];
            var footprint = _graph.GetFootprint(module.Kind, module.Rotation);
            var rect = new Rect2(GridOrigin + new Vector2(module.GridPosition.X * Cell, module.GridPosition.Y * Cell),
                new Vector2(footprint.X * Cell, footprint.Y * Cell));
            var fill = new Color(definition.ColorHex);
            if (!mainComponent.Contains(module)) fill = fill.Darkened(0.55f);
            DrawRect(rect.Grow(-2), fill);
            DrawRect(rect.Grow(-2), new Color(0, 0, 0, 0.65f), false, 1.2f);
            if (module.Kind == "core")
                DrawRect(rect.Grow(-4), ReassemblyUi.TextWhite, false, 2f);
            DrawString(_font, rect.Position + new Vector2(4, 14),
                ReassemblyUi.FitText(_font, definition.Label, 10, rect.Size.X - 8),
                HorizontalAlignment.Left, -1, 10, new Color("#0a141c", 0.95f));
            if (module == _selection)
                DrawRect(rect, ReassemblyUi.TacticalAmber, false, 2.2f);
        }

        // 悬停预览：选中调色板模块时显示落点合法性。
        if (_paletteButtons.Count > 0 && CellAt(_pointer) is { } hover)
        {
            var footprint = _graph.GetFootprint(_selectedKind, _previewRotation);
            var rect = new Rect2(GridOrigin + new Vector2(hover.X * Cell, hover.Y * Cell),
                new Vector2(footprint.X * Cell, footprint.Y * Cell));
            var valid = CanPlaceAt(_selectedKind, hover, _previewRotation);
            DrawRect(rect, valid ? new Color(ReassemblyUi.PhosphorGreen, 0.22f) : new Color(ReassemblyUi.DangerRed, 0.18f));
            DrawRect(rect, valid ? ReassemblyUi.PhosphorGreen : ReassemblyUi.DangerRed, false, 1.6f);
        }
    }

    private Vector2 CenterOf(ModuleInstance module)
    {
        var footprint = _graph.GetFootprint(module.Kind, module.Rotation);
        return GridOrigin + (new Vector2(module.GridPosition.X, module.GridPosition.Y)
            + new Vector2(footprint.X, footprint.Y) * 0.5f) * Cell;
    }

    private void DrawPalette()
    {
        DrawString(_font, new Vector2(712, 172), "模块目录 // 矿机产出 · 能源供网 · 散热保运转", fontSize: 13,
            modulate: ReassemblyUi.CyanHighlight);
        _paletteButtons.Clear();
        var kinds = FactoryModuleGraph.Definitions.Keys.ToArray();
        for (var i = 0; i < kinds.Length; i++)
        {
            var kind = kinds[i];
            var definition = FactoryModuleGraph.Definitions[kind];
            var rect = new Rect2(712 + i % 2 * 284, 186 + i / 2 * 96, 276, 88);
            _paletteButtons.Add((rect, kind));
            var selected = kind == _selectedKind;
            ReassemblyUi.DrawChamferFrame(this, rect, selected ? new Color("#0e2532") : new Color("#09141d"),
                selected ? ReassemblyUi.CyanHighlight : ReassemblyUi.PanelBorder, 5, false, selected ? 1.5f : 0.9f);
            // 模块色块预览（按实际占格）。
            var preview = new Vector2(rect.Position.X + 14, rect.GetCenter().Y);
            for (var cy = 0; cy < definition.Size.Y; cy++)
                for (var cx = 0; cx < definition.Size.X; cx++)
                    DrawRect(new Rect2(preview + new Vector2(cx * 12, (cy - definition.Size.Y * 0.5f) * 12),
                        new Vector2(11, 11)), new Color(definition.ColorHex));
            DrawString(_font, rect.Position + new Vector2(46, 24),
                ReassemblyUi.FitText(_font, definition.Label, 13, 130), HorizontalAlignment.Left, 130, 13,
                selected ? ReassemblyUi.TextWhite : ReassemblyUi.TextSteel);
            DrawString(_font, rect.Position + new Vector2(46, 44),
                ReassemblyUi.FitText(_font, definition.Category, 10, 130), HorizontalAlignment.Left, 130, 10,
                ReassemblyUi.TextMuted);
            var cost = PlayerProgress.ModuleCostR(definition);
            DrawString(_font, rect.Position + new Vector2(46, 66),
                cost > 0 ? $"{cost:N0} R" : "免费", HorizontalAlignment.Left, 130, 11,
                cost > 0 ? ReassemblyUi.TacticalAmber : ReassemblyUi.TextMuted);
            DrawString(_font, rect.Position + new Vector2(184, 24),
                ReassemblyUi.FitText(_font, ModuleStatBrief(definition), 10, 84), HorizontalAlignment.Left, 84, 10,
                ReassemblyUi.CyanMuted);
            DrawString(_font, rect.Position + new Vector2(184, 44),
                ReassemblyUi.FitText(_font, definition.Description, 9, 84), HorizontalAlignment.Left, 84, 9,
                ReassemblyUi.TextMuted);
        }
    }

    private static string ModuleStatBrief(ModuleDefinition definition)
    {
        if (definition.Generation > 0 && definition.Kind != "core") return $"发电 +{definition.Generation:0.#}";
        if (definition.OreRate > 0) return $"采矿 {definition.OreRate:0.#}/s";
        if (definition.RefineRate > 0) return $"合金 {definition.RefineRate:0.#}/s";
        if (definition.Cooling > 0) return $"散热 {definition.Cooling:0.#}";
        if (definition.EnergyCapacity > 0) return $"储能 {definition.EnergyCapacity:0.#}";
        if (definition.Storage > 0) return $"仓储 {definition.Storage:0.#}";
        return definition.Kind == "core" ? "控制中枢" : $"功耗 {definition.Consumption:0.#}";
    }

    private void DrawMetrics()
    {
        var metrics = _graph.GetMetrics();
        var modules = CaptureGraph();
        var (ore, alloy) = PlayerProgress.BlueprintProduction(_biome, modules);
        var newCost = PlayerProgress.BlueprintCostR(modules);
        var old = _slot < _existingCount ? Progress.FactorySlotBlueprint(_planetId, _slot) : null;
        var total = _slot >= _existingCount
            ? PlayerProgress.FactoryCost(_existingCount) + newCost
            : Math.Max(0, newCost - (old is null ? 0 : PlayerProgress.BlueprintCostR(old)));

        DrawString(_font, new Vector2(24, ViewHeight - 96), "蓝图指标 //", fontSize: 12, modulate: ReassemblyUi.CyanHighlight);
        DrawString(_font, new Vector2(110, ViewHeight - 96),
            $"模块 {metrics.Counts.Values.Sum():0}   净功率 {metrics.NetPower:0.#}   热量 {metrics.Heat:0.#}/{metrics.Cooling:0.#}   仓储 {metrics.Storage:0}   完整度 {metrics.Integrity:0}%",
            fontSize: 12, modulate: ReassemblyUi.TextSteel);
        DrawString(_font, new Vector2(24, ViewHeight - 68),
            $"预估产出 碎料 {ore * 60f:0.#}/分 · 合金 {alloy * 60f:0.#}/分（入库存 · 空间站出售换 R · 行情波动）", fontSize: 13,
            modulate: ore + alloy > 0 ? ReassemblyUi.PhosphorGreen : ReassemblyUi.TextMuted);
        DrawString(_font, new Vector2(24, ViewHeight - 42),
            $"当前库存 碎料 {Progress.ResourceStock.GetValueOrDefault("iron"):N0} · 合金 {Progress.ResourceStock.GetValueOrDefault("alloy"):N0}" +
            $"{(_slot >= _existingCount ? $"   建厂 {PlayerProgress.FactoryCost(_existingCount):N0} R + 模块 {newCost:N0} R" : $"   改造差价 {total:N0} R")}   余额 {Progress.Resources:N0} R",
            fontSize: 12, modulate: ReassemblyUi.TacticalAmber);

        if (metrics.NetPower < 0)
            DrawString(_font, new Vector2(480, ViewHeight - 68), "⚠ 电网超载：非核心模块不产出", fontSize: 11,
                modulate: ReassemblyUi.TacticalAmber);
        else if (metrics.Heat > metrics.Cooling + 1e-9)
            DrawString(_font, new Vector2(480, ViewHeight - 68), "⚠ 散热不足：建议增配散热翼", fontSize: 11,
                modulate: ReassemblyUi.TacticalAmber);
        else if (ore + alloy <= 0 && HasCore)
            DrawString(_font, new Vector2(480, ViewHeight - 68), "提示：放置矿机/精炼炉后工厂才有产出", fontSize: 11,
                modulate: ReassemblyUi.TextMuted);
    }

    private void DrawActions()
    {
        var isNew = _slot >= _existingCount;
        var modules = CaptureGraph();
        var newCost = PlayerProgress.BlueprintCostR(modules);
        var old = !isNew ? Progress.FactorySlotBlueprint(_planetId, _slot) : null;
        var total = isNew
            ? PlayerProgress.FactoryCost(_existingCount) + newCost
            : Math.Max(0, newCost - (old is null ? 0 : PlayerProgress.BlueprintCostR(old)));

        var confirm = ConfirmButton;
        var affordable = Progress.Resources >= total;
        ReassemblyUi.DrawChamferFrame(this, confirm, affordable ? new Color("#10232c") : new Color("#0a141c"),
            affordable ? ReassemblyUi.CyanHighlight : ReassemblyUi.PanelBorder, 6, false, 1.2f);
        DrawString(_font, confirm.Position + new Vector2(0, 30),
            isNew ? $"确认建造 · {total:N0} R" : $"保存改造 · {total:N0} R",
            HorizontalAlignment.Center, (float)confirm.Size.X, 14, affordable ? ReassemblyUi.TextWhite : ReassemblyUi.TextMuted);

        var cancel = CancelButton;
        ReassemblyUi.DrawChamferFrame(this, cancel, new Color("#0a141c"), ReassemblyUi.CyanMuted, 6, false, 1f);
        DrawString(_font, cancel.Position + new Vector2(0, 30), "取消返回",
            HorizontalAlignment.Center, (float)cancel.Size.X, 13, ReassemblyUi.TextSteel);
        DrawString(_font, new Vector2(712, ViewHeight - 96 + 44),
            ReassemblyUi.FitText(_font, "改动即时生效于蓝图 · 确认才写入存档与结算", 10, 540),
            fontSize: 10, modulate: ReassemblyUi.TextMuted);
    }

    private void DrawNotice()
    {
        if (_notice.Length == 0) return;
        DrawString(_font, new Vector2(712, 626),
            ReassemblyUi.FitText(_font, _notice, 11, 540),
            HorizontalAlignment.Left, 540, 11,
            _notice.Contains("不足", StringComparison.Ordinal) || _notice.Contains("必须", StringComparison.Ordinal) ||
            _notice.Contains("不可", StringComparison.Ordinal) || _notice.Contains("只能", StringComparison.Ordinal)
                ? ReassemblyUi.TacticalAmber : ReassemblyUi.TextMuted);
    }
}
