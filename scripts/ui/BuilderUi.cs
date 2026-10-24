using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>Edge-mounted tools and contextual readouts over the game world.</summary>
public partial class BuilderUi : Control
{
    private static readonly string[] FactoryOrder = { "solar", "miner", "refinery", "battery", "radiator", "cargo", "reactor" };
    private static readonly string[] ShipOrder = { "hull", "armor", "thruster", "gyro", "radiator", "cargo", "tractor", "laser", "cannon", "beam", "railgun", "missile", "shield", "point_defense", "reactor", "generator", "capacitor", "sensor" };
    private Main _main = null!;
    private Panel _library = null!, _routes = null!, _inspection = null!, _comms = null!;
    private ScrollContainer _palette = null!;
    private Control _paletteContent = null!;
    private Label _title = null!, _subtitle = null!, _resources = null!, _clock = null!, _stats = null!, _hint = null!, _event = null!;
    private Label _detailTitle = null!, _detail = null!, _detailStats = null!, _inspectTitle = null!, _inspectBody = null!, _commsBody = null!, _routeBrief = null!;
    private Button _factory = null!, _ship = null!, _route = null!, _primary = null!, _secondary = null!, _pause = null!, _commsButton = null!;
    private readonly Dictionary<string, Button> _cards = new();
    private readonly Dictionary<string, Label> _keys = new();
    private readonly List<Button> _stageButtons = new();
    private readonly List<Label> _stageLabels = new();
    private string _lastEvent = "";
    private ulong _eventAt;
    private bool _built;

    public void Setup(Main main)
    {
        _main = main;
        Theme = new Theme { DefaultFont = new SystemFont { FontNames = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Noto Sans CJK SC" } } };
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 20;
        BuildTree();
        Refresh();
    }

    public bool IsPointerOverOverlay(Vector2 point) =>
        new Control[] { _library, _routes, _inspection, _comms }.Any(control => control.Visible && control.GetGlobalRect().HasPoint(point))
        || new Rect2(814, 103, 187, 30).HasPoint(point);

    public void Refresh()
    {
        if (!_built) return;
        var world = _main.World;
        var ship = _main.IsShipContext;
        var route = _main.IsRouteContext;
        var metrics = route ? world.ShipGraph.GetMetrics() : _main.ActiveMetrics;
        _factory.SetPressedNoSignal(!ship && !route);
        _ship.SetPressedNoSignal(ship);
        _route.SetPressedNoSignal(route);
        _title.Text = route ? "深空航图" : ship ? "舰船设计" : "行星工厂";
        _subtitle.Text = route ? "轨道层 / 扇区 01" : ship ? "轨道船坞 / 种子蓝图 01" : "已登录大型行星 / 赤道据点";
        _resources.Text = ship
            ? $"舰船蓝图  {world.ShipGraph.Modules.Count} 模块    舰体 {metrics.Hull:0}    连接 {world.ShipGraph.GetOperationalModuleIds().Count}/{world.ShipGraph.Modules.Count}"
            : $"铁质 {world.Resources["iron"]:0}    合金 {world.Resources["alloy"]:0}    挥发物 {world.Resources["volatile"]:0}    晶体 {world.Resources["crystal"]:0}";
        _clock.Text = $"{(world.Paused ? "已暂停" : "运行")}  {world.TimeScale:0.00}×  /  {TimeSpan.FromSeconds(world.Elapsed):mm\\:ss}";
        _pause.Text = world.Paused ? "继续 [P]" : "暂停 [P]";
        _library.Visible = !route;
        _routes.Visible = route;
        _commsButton.Visible = !ship && !route;
        if (ship || route) _comms.Visible = false;
        var order = ship ? ShipOrder : FactoryOrder;
        _library.Position = ship ? new Vector2(24, 488) : new Vector2(1026, 96);
        _library.Size = ship ? new Vector2(982, 128) : new Vector2(230, 518);
        _inspection.Position = ship ? new Vector2(1026, 96) : new Vector2(24, 430);
        _inspection.Size = ship ? new Vector2(230, 400) : new Vector2(282, 160);
        _event.Position = ship ? new Vector2(334, 456) : new Vector2(334, 586);
        _palette.Position = ship ? new Vector2(10, 52) : new Vector2(10, 68);
        _palette.Size = ship ? new Vector2(962, 70) : new Vector2(210, 248);
        _palette.HorizontalScrollMode = ship ? ScrollContainer.ScrollMode.Auto : ScrollContainer.ScrollMode.Disabled;
        _palette.VerticalScrollMode = ship ? ScrollContainer.ScrollMode.Disabled : ScrollContainer.ScrollMode.Auto;
        _paletteContent.CustomMinimumSize = ship
            ? new Vector2(Math.Max(962, order.Length * 70 + 12), 70)
            : new Vector2(202, 600);
        _detailTitle.Visible = !ship;
        _detail.Visible = !ship;
        _detailStats.Visible = !ship;
        foreach (var (kind, button) in _cards)
        {
            var i = Array.IndexOf(order, kind);
            button.Visible = i >= 0;
            if (i < 0) continue;
            button.Size = new Vector2(66, 66);
            button.Position = ship
                ? new Vector2(12 + i * 70, 0)
                : new Vector2(12 + i % 3 * 70, i / 3 * 72);
            button.SetPressedNoSignal(kind == _main.SelectedKind);
            _keys[kind].Text = (i + 1).ToString();
        }
        var instance = _main.InspectedModule;
        var definition = ModuleGraph.Definitions[instance?.Kind ?? _main.SelectedKind];
        var footprint = _main.ActiveGraphForUi.GetFootprint(definition.Kind, instance?.Rotation ?? _main.BuildRotation);
        _detailTitle.Text = definition.Label;
        _detail.Text = definition.Description;
        _detailStats.Text = $"{footprint.X} × {footprint.Y}  /  质量 {definition.Mass:0.#}\n发电 {definition.Generation:0.#}   用电 {definition.Consumption:0.#}\n热量 +{definition.Heat:0.#}   散热 {definition.Cooling:0.#}";
        if (!ship) _detailStats.Text += $"\n成本  铁 {definition.Cost["iron"]:0} / 合金 {definition.Cost["alloy"]:0}";
        var heat = ship || route ? metrics.Heat : world.CurrentHeat;
        var heatCapacity = ship || route ? Math.Max(12, metrics.Cooling * 3) : world.HeatCapacity;
        _stats.Text = ship || route
            ? $"质量 {metrics.Mass:0}    推力 {metrics.Thrust:0}    火力 {metrics.Damage:0}    护盾 {metrics.Shield:0}    控制 {metrics.Control:0}    散热 {metrics.Cooling:0}" + (world.ShipReady ? "    舰船已装配" : world.ShipAssemblyQueued ? $"    装配 {world.ShipAssemblyProgress:P0}" : "    蓝图待装配")
            : $"供电 {metrics.Generation:0.#}/{metrics.Consumption:0.#}    热量 {heat:0.#}/{heatCapacity:0.#}    仓储 {world.StorageUsed:0}/{world.StorageCapacity:0}";
        var disconnected = _main.ActiveGraphForUi.Modules.Count - _main.ActiveGraphForUi.GetOperationalModuleIds().Count;
        var warning = disconnected > 0 || heat > heatCapacity || metrics.Generation < metrics.Consumption;
        var assemblyNotice = ship && !world.ShipReady;
        _inspection.Visible = !route && (instance is not null || warning || assemblyNotice) && !_comms.Visible;
        _inspectTitle.Text = ship ? "舰船数据" : assemblyNotice ? "装配坞" : instance is null ? "系统警报" : definition.Label;
        var reason = disconnected > 0 ? $"{disconnected} 个模块断开 · 重新连接核心"
            : heat > heatCapacity ? "热量过高 · 增加散热设备"
            : metrics.Generation < metrics.Consumption ? "供电不足 · 检查发电与储能"
            : "连接正常 · 模块已接入";
        _inspectBody.Text = ship
            ? $"核心连接 {world.ShipGraph.GetOperationalModuleIds().Count}/{world.ShipGraph.Modules.Count}\n质量 {metrics.Mass:0}\n舰体 {metrics.Hull:0} · 护盾 {metrics.Shield:0}\n推力 {metrics.Thrust:0} · 转向 {metrics.Control:0}\n火力 {metrics.Damage:0} · 散热 {metrics.Cooling:0}\n\n{(assemblyNotice ? (world.ShipAssemblyQueued ? $"装配中 {world.ShipAssemblyProgress:P0}" : "蓝图已修改，等待装配") : "舰船已装配，可直接试航")}"
            : assemblyNotice
            ? world.ShipAssemblyQueued
                ? $"装配进度 {world.ShipAssemblyProgress:P0}\n已投入铁质 {world.ShipAssemblyIronCost:0}、合金 {world.ShipAssemblyAlloyCost:0}\n世界时间推进时继续装配，暂停会冻结。"
                : $"蓝图已修改，等待装配。\n预计投入铁质 {world.ShipAssemblyIronCost:0}、合金 {world.ShipAssemblyAlloyCost:0}\n点击底部“装配舰船”开始。"
            : $"{reason}\n系统供电 {metrics.Generation:0.#} / {metrics.Consumption:0.#}\n系统热量 {heat:0.#} / {heatCapacity:0.#}";
        var guild = world.Factions["guild"];
        _commsBody.Text = $"航路公社\n信任 {guild.Trust:+0;-0;0}   恐惧 {guild.Fear:+0;-0;0}\n债务 {guild.Debt:0} · 数值交互原型";
        _primary.Text = route
            ? world.ShipReady ? "出发 [SPACE]" : "返回装配"
            : ship
                ? world.ShipReady ? "试航 [SPACE]" : world.ShipAssemblyQueued ? $"装配中 {world.ShipAssemblyProgress:P0}" : "装配舰船"
                : "舰船设计 [TAB]";
        _primary.Disabled = ship && world.ShipAssemblyQueued;
        _secondary.Text = ship ? "保存蓝图" : route ? "舰船设计" : "返回太空";
        _hint.Text = route ? "1 / 2 / 3 选择航段 · SPACE 出发 · 登录行星建设工厂"
            : ship && !world.ShipReady ? "修改蓝图后需要装配；装配坞随世界时间推进，暂停会冻结进度"
            : "左键建造 · 右键拆除 · Q/E 旋转 · 滚轮缩放 · 中键平移";
        var entry = world.EventLog.FirstOrDefault() ?? "";
        if (entry != _lastEvent) { _lastEvent = entry; _eventAt = Time.GetTicksMsec(); }
        _event.Text = entry;
        _event.Visible = Time.GetTicksMsec() - _eventAt < 6500;
        _routeBrief.Text = $"风险：{world.GetExpeditionRisk(world.SelectedExpeditionStage)}\n\n{world.GetExpeditionBrief(world.SelectedExpeditionStage)}\n\n回收物资\n{world.GetExpeditionReward(world.SelectedExpeditionStage)}";
        for (var i = 0; i < 3; i++)
        {
            _stageButtons[i].Disabled = !world.IsExpeditionStageUnlocked(i + 1);
            _stageButtons[i].SetPressedNoSignal(world.SelectedExpeditionStage == i + 1);
            _stageLabels[i].Text = world.IsExpeditionStageUnlocked(i + 1) ? "已解锁" : "完成上一航段后解锁";
        }
    }

    private void BuildTree()
    {
        _built = true;
        _title = Text(this, "", new(24, 18), new(330, 42), 28, UiTheme.CanvasInk);
        _subtitle = Text(this, "", new(26, 63), new(480, 24), 14, UiTheme.CanvasInk);
        var supply = Panel(this, new(714, 18), new(542, 38));
        _resources = Text(supply, "", new(12, 6), new(518, 26), 15);
        _clock = Text(this, "", new(754, 62), new(500, 25), 13, UiTheme.CanvasInk);
        _clock.HorizontalAlignment = HorizontalAlignment.Right;

        _library = Panel(this, new(1026, 96), new(230, 458));
        _library.ClipContents = true;
        Text(_library, "零件", new(14, 13), new(150, 28), 19);
        Text(_library, "结构 / 设备 / 系统", new(14, 44), new(204, 24), 12, UiTheme.TextSecondary);
        _palette = new ScrollContainer
        {
            Position = new Vector2(10, 68),
            Size = new Vector2(210, 248),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
        };
        _paletteContent = new Control { CustomMinimumSize = new Vector2(202, 600) };
        _palette.AddChild(_paletteContent);
        _library.AddChild(_palette);
        foreach (var kind in FactoryOrder.Concat(ShipOrder).Distinct())
        {
            var definition = ModuleGraph.Definitions[kind];
            var button = Button(_paletteContent, "", Vector2.Zero, new(66, 66), () => _main.SelectModule(kind));
            button.ToggleMode = true;
            button.AddThemeStyleboxOverride("normal", UiTheme.Box(UiTheme.Surface, UiTheme.Surface, 0, 0));
            button.TooltipText = $"{definition.Label}：{definition.Description}\n铁 {definition.Cost["iron"]} / 合金 {definition.Cost["alloy"]}";
            button.AddChild(new ModuleGlyph { Kind = kind, Position = new(9, 6), Size = new(44, 32), MouseFilter = MouseFilterEnum.Ignore });
            Text(button, definition.Label, new(2, 42), new(62, 21), 12);
            _keys[kind] = Text(button, "", new(49, 0), new(16, 19), 12, UiTheme.TextMuted);
            _cards[kind] = button;
        }
        UiTheme.AccentBar(_library, new(14, 321), new(202, 1), UiTheme.Border);
        _detailTitle = Text(_library, "", new(14, 331), new(202, 28), 17, UiTheme.Accent);
        _detail = Text(_library, "", new(14, 363), new(202, 43), 14, UiTheme.TextSecondary, true);
        _detailStats = Text(_library, "", new(14, 413), new(202, 86), 13);
        // Leave room for four detail rows instead of squeezing auxiliary text.
        _library.Size = new Vector2(230, 518);

        _routes = Panel(this, new(1026, 96), new(230, 518));
        Text(_routes, "目的地", new(14, 13), new(200, 28), 19);
        for (var i = 0; i < 3; i++)
        {
            var stage = i + 1;
            var button = Button(_routes, "", new(12, 57 + i * 78), new(206, 68), () => _main.SelectExpeditionStage(stage));
            button.ToggleMode = true;
            Text(button, $"0{stage}  {_main.World.GetExpeditionName(stage)}", new(10, 9), new(186, 26), 16);
            _stageLabels.Add(Text(button, "", new(10, 39), new(186, 22), 12, UiTheme.TextSecondary));
            _stageButtons.Add(button);
        }
        _routeBrief = Text(_routes, "", new(14, 309), new(202, 190), 14, UiTheme.TextSecondary, true);
        _inspection = Panel(this, new(24, 430), new(282, 160));
        _inspectTitle = Text(_inspection, "", new(16, 12), new(250, 30), 18);
        _inspectBody = Text(_inspection, "", new(16, 52), new(250, 100), 14, UiTheme.TextSecondary, true);
        _comms = Panel(this, new(24, 382), new(298, 208));
        Text(_comms, "通讯", new(16, 10), new(140, 28), 18);
        Button(_comms, "关闭", new(220, 10), new(62, 28), () => _comms.Visible = false);
        _commsBody = Text(_comms, "", new(16, 53), new(266, 94), 14, UiTheme.TextSecondary);
        Button(_comms, "援助 H", new(16, 158), new(82, 32), () => _main.ChooseContract("help"));
        Button(_comms, "劫掠 R", new(108, 158), new(82, 32), () => _main.ChooseContract("rob"));
        Button(_comms, "交易 T", new(200, 158), new(82, 32), () => _main.ChooseContract("trade"));
        _comms.Visible = false;
        _commsButton = Button(this, "通讯", new(24, 106), new(90, 32), () => _comms.Visible = !_comms.Visible);
        _event = Text(this, "", new(334, 586), new(666, 30), 13, UiTheme.CanvasInk);
        _event.ClipText = true;

        var footer = Panel(this, new(0, 624), new(1280, 96));
        _factory = Button(footer, "登录行星", new(18, 20), new(96, 38), () => _main.SelectBuildContext(false));
        _ship = Button(footer, "舰船设计", new(122, 20), new(96, 38), () => _main.SelectBuildContext(true));
        _route = Button(footer, "深空航图", new(226, 20), new(96, 38), _main.SelectRoute);
        foreach (var tab in new[] { _factory, _ship, _route }) tab.ToggleMode = true;
        Text(footer, "REWORLD / 星构边疆", new(24, 66), new(250, 24), 12, UiTheme.TextMuted);
        _stats = Text(footer, "", new(300, 16), new(582, 30), 16);
        _hint = Text(footer, "", new(300, 59), new(690, 26), 12, UiTheme.TextSecondary);
        _secondary = Button(footer, "", new(910, 20), new(140, 40), () => { if (_main.IsShipContext) _main.World.SaveBlueprint(); else if (_main.IsRouteContext) _main.SelectBuildContext(true); else _main.SelectRoute(); });
        _primary = Button(footer, "", new(1064, 20), new(192, 40), () => { if (_main.IsShipContext || _main.IsRouteContext) _main.RequestLaunch(); else _main.SelectBuildContext(true); });
        UiTheme.PrimaryButton(_primary);
        _pause = Button(this, "", new(814, 103), new(102, 30), () => _main.World.TogglePause());
        Button(this, "−", new(923, 103), new(36, 30), () => _main.World.AdjustTimeScale(-0.25));
        Button(this, "+", new(965, 103), new(36, 30), () => _main.World.AdjustTimeScale(0.25));
    }

    private static Panel Panel(Control parent, Vector2 position, Vector2 size)
    {
        var panel = new Panel { Position = position, Size = size };
        UiTheme.Panel(panel);
        panel.MouseFilter = MouseFilterEnum.Stop;
        parent.AddChild(panel);
        UiTheme.AccentBar(panel, Vector2.Zero, new(32, 2), UiTheme.Accent);
        return panel;
    }
    private static Label Text(Control parent, string text, Vector2 position, Vector2 size, int fontSize, Color? color = null, bool wrap = false)
    {
        var label = new Label { Text = text, Position = position, Size = size, ClipText = true };
        UiTheme.Label(label, fontSize, color);
        if (wrap) label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        parent.AddChild(label);
        return label;
    }
    private static Button Button(Control parent, string text, Vector2 position, Vector2 size, Action action)
    {
        var button = new Button { Text = text, Position = position, Size = size };
        UiTheme.Button(button);
        button.Size = size;
        button.Pressed += action;
        parent.AddChild(button);
        return button;
    }
}
