using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Node based shell for the builder screen. Drawing remains limited to the grid and
/// module artwork; ordinary UI layout and interaction are handled by Godot Controls.
/// </summary>
public partial class BuilderUi : Control
{
    private static readonly string[] FactoryModuleOrder = { "solar", "miner", "refinery", "battery", "cargo", "reactor" };
    private static readonly string[] ShipModuleOrder = { "hull", "thruster", "radiator", "cargo", "laser", "cannon", "shield", "reactor" };

    private Main _main = null!;
    private Panel _leftPanel = null!;
    private Panel _rightPanel = null!;
    private Label _contextSubtitle = null!;
    private Label _categoryLabel = null!;
    private Label _targetLabel = null!;
    private Label _metricsLabel = null!;
    private Label _resourceLabel = null!;
    private Label _guildLabel = null!;
    private Label _eventLabel = null!;
    private Label _detailLabel = null!;
    private Label _footerLabel = null!;
    private Label _statusBadge = null!;
    private Label _runtimeLabel = null!;
    private Button _factoryButton = null!;
    private Button _shipButton = null!;
    private Button _shipAction = null!;
    private Button _factoryAction = null!;
    private Button _launchAction = null!;
    private Button _helpAction = null!;
    private Button _robAction = null!;
    private Button _tradeAction = null!;
    private readonly Dictionary<string, Button> _moduleButtons = new(StringComparer.Ordinal);
    private readonly List<ProgressBar> _statusBars = new();
    private bool _built;

    public void Setup(Main main)
    {
        _main = main;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 20;
        BuildTree();
        Refresh();
    }

    public void Refresh()
    {
        if (!_built || _main.World is null)
            return;

        var world = _main.World;
        var metrics = _main.ActiveMetrics;
        var ship = _main.IsShipContext;
        _factoryButton.ButtonPressed = !ship;
        _shipButton.ButtonPressed = ship;
        _contextSubtitle.Text = ship ? "舰体模块 / 武器系统 · 独立蓝图" : "资源采集 / 加工设施 · 消耗库存";
        _categoryLabel.Text = ship ? "骨架     机动     武装" : "采集     加工     能源";
        _statusBadge.Text = world.Paused ? "模拟暂停" : $"自动化 {world.TimeScale:0.0}x";
        _runtimeLabel.Text = $"运行时间  {world.Elapsed:00000.0}s";
        _targetLabel.Text = "当前目标  ·  " + (ship
            ? $"{metrics.Counts.Values.Sum()} 个模块 · {(_main.ActiveLinks.Count)} 条结构连接"
            : world.StormGateUnlocked
                ? "风暴闸门已解锁 → 改装舰船，准备下一次远征"
                : metrics.Counts.GetValueOrDefault("miner") > 0 && metrics.Counts.GetValueOrDefault("refinery") > 0
                    ? "生产链在线 → 进入舰船设计，开始试航"
                    : "连接采矿臂 → 精炼炉，完成第一条生产链");
        _metricsLabel.Text = ship
            ? $"功率 {metrics.Generation:0.0}/{metrics.Consumption:0.0}\n热量 {metrics.Heat:0.0} / {Math.Max(12.0, metrics.Cooling * 3.0):0.0}\n结构 {metrics.Integrity:0.0}/100\n质量 {metrics.Mass:0.0} · 装甲 {metrics.Hull:0.0} · 护盾 {metrics.Shield:0.0}\n推力 {metrics.Thrust:0.0} · 火力 {metrics.Damage:0.0} · 信号 {metrics.Signal:0.0}"
            : $"功率 {metrics.Generation:0.0}/{metrics.Consumption:0.0}\n热量 {world.CurrentHeat:0.0}/{world.HeatCapacity:0.0}\n结构 {metrics.Integrity:0.0}/100\n仓储 {world.StorageUsed:0}/{world.StorageCapacity:0} · 带宽 {metrics.Control:0}\n信号 {metrics.Signal:0}";
        _resourceLabel.Text = $"铁质  {world.Resources["iron"]:0.0}     合金  {world.Resources["alloy"]:0.0}\n挥发  {world.Resources["volatile"]:0.0}     晶体  {world.Resources["crystal"]:0.0}";
        var guild = world.Factions["guild"];
        _guildLabel.Text = $"航路公社\n信任 {guild.Trust:+0;-0;0}    恐惧 {guild.Fear:+0;-0;0}    债务 {guild.Debt:0}";
        _eventLabel.Text = world.EventLog.FirstOrDefault() ?? "暂无事件";
        _footerLabel.Text = ship
            ? "左键放置/选中/拖动  ·  右键或 Delete 拆除  ·  Q/E 旋转  ·  Ctrl+S/L 蓝图  ·  SPACE 试航"
            : "左键放置/选中/拖动  ·  右键或 Delete 拆除  ·  Q/E 旋转  ·  Ctrl+Z/Y 撤销  ·  Tab 舰船设计  ·  P 暂停";

        var order = ship ? ShipModuleOrder : FactoryModuleOrder;
        foreach (var pair in _moduleButtons)
            pair.Value.ButtonPressed = pair.Key == _main.SelectedKind;
        for (var index = 0; index < order.Length; index++)
        {
            var kind = order[index];
            if (!_moduleButtons.TryGetValue(kind, out var button))
                continue;
            button.Visible = true;
            var definition = ModuleGraph.Definitions[kind];
            button.Text = $"{GetShortcut(index)}  {definition.Label}\n{(ship ? $"占格 {definition.Size.X}×{definition.Size.Y}" : $"铁 {definition.Cost["iron"]:0} · 合金 {definition.Cost["alloy"]:0}")}";
        }
        foreach (var pair in _moduleButtons)
            if (!order.Contains(pair.Key))
                pair.Value.Visible = false;

        var selected = ModuleGraph.Definitions[_main.SelectedKind];
        var footprint = _main.ActiveGraphForUi.GetFootprint(_main.SelectedKind, _main.BuildRotation);
        _detailLabel.Text = $"当前选择  ·  {selected.Label}\n{selected.Description}\n占格 {footprint.X}×{footprint.Y}   质量 {selected.Mass:0.0}   热量 +{selected.Heat:0.0}\n铁 {selected.Cost["iron"]:0}   合金 {selected.Cost["alloy"]:0}   类别 {selected.Category}\n冷却 {selected.Cooling:0.0}   仓储 +{selected.Storage:0}\nQ/E 旋转 · 快捷键选择";

        _shipAction.Visible = !ship;
        _launchAction.Visible = ship;
        _factoryAction.Visible = ship;
        _helpAction.Visible = !ship;
        _robAction.Visible = !ship;
        _tradeAction.Visible = !ship;
        _statusBars[0].Value = metrics.Consumption <= 0.0 ? 1.0 : Math.Clamp(metrics.Generation / metrics.Consumption, 0.0, 1.0) * 100.0;
        _statusBars[1].Value = ship
            ? Math.Clamp(metrics.Heat / Math.Max(12.0, metrics.Cooling * 3.0), 0.0, 1.0) * 100.0
            : Math.Clamp(world.CurrentHeat / world.HeatCapacity, 0.0, 1.0) * 100.0;
        _statusBars[2].Value = Math.Clamp(metrics.Integrity / 100.0, 0.0, 1.0) * 100.0;
    }

    private void BuildTree()
    {
        if (_built)
            return;
        _built = true;
        var background = new Panel { Position = new Vector2(18, 18), Size = new Vector2(1244, 42) };
        UiTheme.Panel(background, UiTheme.Surface);
        AddChild(background);
        AddLabel(background, "重组：新曙光", new Vector2(16, 6), 19, UiTheme.TextPrimary);
        AddLabel(background, "种子核心控制台  /  赤道节点-01", new Vector2(172, 11), 12, UiTheme.TextSecondary);

        _factoryButton = AddButton(background, "工厂", new Vector2(602, 8), new Vector2(98, 26), UiTheme.Accent, () => _main.SelectBuildContext(false));
        _shipButton = AddButton(background, "舰船设计", new Vector2(706, 8), new Vector2(98, 26), new Color("#b48ce0"), () => _main.SelectBuildContext(true));
        _factoryButton.ToggleMode = true;
        _shipButton.ToggleMode = true;
        AddLabel(background, "可试玩演示", new Vector2(820, 12), 10, UiTheme.Warning);
        _statusBadge = AddLabel(background, "", new Vector2(934, 12), 10, UiTheme.Info);
        _runtimeLabel = AddLabel(background, "", new Vector2(1050, 12), 10, UiTheme.TextMuted);

        _leftPanel = CreatePanel(new Vector2(18, 74), new Vector2(220, 572));
        AddLabel(_leftPanel, "模块库", new Vector2(16, 16), 18, UiTheme.TextPrimary);
        _contextSubtitle = AddLabel(_leftPanel, "", new Vector2(16, 42), 10, UiTheme.TextMuted);
        _categoryLabel = AddLabel(_leftPanel, "", new Vector2(16, 65), 10, UiTheme.Accent);
        var moduleGrid = new GridContainer { Position = new Vector2(14, 86), Size = new Vector2(192, 264), Columns = 2 };
        moduleGrid.AddThemeConstantOverride("h_separation", 4);
        moduleGrid.AddThemeConstantOverride("v_separation", 5);
        _leftPanel.AddChild(moduleGrid);
        foreach (var kind in FactoryModuleOrder.Concat(ShipModuleOrder).Distinct())
        {
            var definition = ModuleGraph.Definitions[kind];
            var button = new Button { CustomMinimumSize = new Vector2(94, 39), Text = definition.Label, ToggleMode = true };
            UiTheme.Button(button, new Color(definition.ColorHex));
            button.Pressed += () => _main.SelectModule(kind);
            moduleGrid.AddChild(button);
            _moduleButtons[kind] = button;
        }
        var detailPanel = new Panel { Position = new Vector2(14, 370), Size = new Vector2(192, 188) };
        UiTheme.Panel(detailPanel, UiTheme.BackgroundDeep);
        _leftPanel.AddChild(detailPanel);
        _detailLabel = AddLabel(detailPanel, "", new Vector2(12, 10), 10, UiTheme.TextSecondary);
        _detailLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _detailLabel.Size = new Vector2(168, 165);

        _rightPanel = CreatePanel(new Vector2(958, 74), new Vector2(304, 572));
        AddLabel(_rightPanel, "状态与任务", new Vector2(16, 16), 18, UiTheme.TextPrimary);
        _targetLabel = AddLabel(_rightPanel, "", new Vector2(16, 50), 10, UiTheme.TextSecondary);
        _targetLabel.Size = new Vector2(272, 42);
        _targetLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        AddLabel(_rightPanel, "系统指标", new Vector2(16, 94), 12, UiTheme.TextSecondary);
        _metricsLabel = AddLabel(_rightPanel, "", new Vector2(16, 114), 10, UiTheme.TextSecondary);
        _metricsLabel.Size = new Vector2(272, 86);
        _statusBars.Add(AddProgress(_rightPanel, new Vector2(16, 205)));
        _statusBars.Add(AddProgress(_rightPanel, new Vector2(16, 222)));
        _statusBars.Add(AddProgress(_rightPanel, new Vector2(16, 239)));
        AddLabel(_rightPanel, "资源仓", new Vector2(16, 264), 13, UiTheme.TextSecondary);
        _resourceLabel = AddLabel(_rightPanel, "", new Vector2(16, 288), 11, UiTheme.TextPrimary);
        _resourceLabel.Size = new Vector2(272, 38);
        AddLabel(_rightPanel, "派系信号", new Vector2(16, 330), 12, UiTheme.TextSecondary);
        _guildLabel = AddLabel(_rightPanel, "", new Vector2(16, 348), 11, UiTheme.Warning);
        _guildLabel.Size = new Vector2(272, 40);
        _helpAction = AddButton(_rightPanel, "援助 [H]", new Vector2(16, 388), new Vector2(84, 30), UiTheme.Accent, () => _main.ChooseContract("help"));
        _robAction = AddButton(_rightPanel, "劫掠 [R]", new Vector2(108, 388), new Vector2(84, 30), UiTheme.Danger, () => _main.ChooseContract("rob"));
        _tradeAction = AddButton(_rightPanel, "交易 [T]", new Vector2(200, 388), new Vector2(84, 30), UiTheme.Info, () => _main.ChooseContract("trade"));
        _shipAction = AddButton(_rightPanel, "进入舰船设计 [TAB]", new Vector2(16, 430), new Vector2(272, 32), new Color("#b48ce0"), () => _main.SelectBuildContext(true));
        _launchAction = AddButton(_rightPanel, "进入试航区 [SPACE]", new Vector2(16, 430), new Vector2(272, 32), UiTheme.Accent, () => _main.RequestLaunch());
        _factoryAction = AddButton(_rightPanel, "返回行星工厂 [TAB]", new Vector2(16, 468), new Vector2(272, 32), UiTheme.Info, () => _main.SelectBuildContext(false));
        AddLabel(_rightPanel, "最近事件", new Vector2(16, 506), 12, UiTheme.TextSecondary);
        _eventLabel = AddLabel(_rightPanel, "", new Vector2(16, 524), 10, UiTheme.TextSecondary);
        _eventLabel.Size = new Vector2(272, 42);
        _eventLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;

        var footer = new Panel { Position = new Vector2(18, 660), Size = new Vector2(1244, 34) };
        UiTheme.Panel(footer, UiTheme.BackgroundDeep);
        _footerLabel = AddLabel(footer, "", new Vector2(16, 9), 10, UiTheme.TextMuted);
        _footerLabel.Size = new Vector2(1210, 20);
    }

    private Panel CreatePanel(Vector2 position, Vector2 size, Color? fill = null)
    {
        var panel = new Panel { Position = position, Size = size };
        UiTheme.Panel(panel, fill ?? UiTheme.Surface);
        AddChild(panel);
        return panel;
    }

    private static Label AddLabel(Control parent, string text, Vector2 position, int size, Color color)
    {
        var label = new Label { Text = text, Position = position, Size = new Vector2(300, 24) };
        UiTheme.Label(label, size, color);
        parent.AddChild(label);
        return label;
    }

    private static Button AddButton(Control parent, string text, Vector2 position, Vector2 size, Color accent, Action action)
    {
        var button = new Button { Text = text, Position = position, Size = size, ToggleMode = false };
        UiTheme.Button(button, accent);
        button.Pressed += action;
        parent.AddChild(button);
        return button;
    }

    private static ProgressBar AddProgress(Control parent, Vector2 position)
    {
        var bar = new ProgressBar { Position = position, Size = new Vector2(272, 6), MinValue = 0, MaxValue = 100, ShowPercentage = false };
        bar.AddThemeStyleboxOverride("background", UiTheme.Box(UiTheme.BackgroundDeep, UiTheme.Border));
        bar.AddThemeStyleboxOverride("fill", UiTheme.Box(UiTheme.Accent, UiTheme.Accent, 0));
        parent.AddChild(bar);
        return bar;
    }

    private static string GetShortcut(int index) => index switch
    {
        < 9 => (index + 1).ToString(),
        9 => "0",
        10 => "A",
        11 => "D",
        _ => "?",
    };
}
