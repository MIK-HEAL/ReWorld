using Godot;
using System;

/// <summary>Control based HUD for the test range. SpaceTest only owns combat state and world drawing.</summary>
public partial class SpaceHud : Control
{
    private SpaceTest _space = null!;
    private Label _stage = null!;
    private Label _controls = null!;
    private Label _thrust = null!;
    private Label _damage = null!;
    private Label _ship = null!;
    private Label _power = null!;
    private Label _resultTitle = null!;
    private Label _resultBody = null!;
    private Label _resultHint = null!;
    private HudMeter _shieldBar = null!;
    private HudMeter _hullBar = null!;
    private HudMeter _heatBar = null!;
    private Label _shieldBarLabel = null!;
    private Label _hullBarLabel = null!;
    private Label _heatBarLabel = null!;
    private Panel _resultPanel = null!;
    private Button _nextWaveAction = null!;
    private Button _returnAction = null!;
    private Button _restartAction = null!;
    private ColorRect _resultBackdrop = null!;
    private string _lastPhase = "";
    private bool _built;

    public void Setup(SpaceTest space)
    {
        _space = space;
        Theme = new Theme { DefaultFont = new SystemFont { FontNames = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Noto Sans CJK SC" } } };
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 30;
        BuildTree();
        Visible = false;
    }

    public void Refresh()
    {
        if (!_built || _space is null || !_space.IsActive)
            return;

        var metrics = _space.World.GetMetrics();
        _stage.Text = $"{_space.World.GetExpeditionName(_space.ExpeditionStage)} · 已瘫痪 {_space.Score} / {_space.TargetCount}";
        _controls.Text = "W 前进 · S 反推 · A/D 或 Q/E 差动转向 · 鼠标左键开火 · 滚轮缩放视野 · 先拆武器/引擎，最后击毁核心";
        var headingDegrees = Mathf.RadToDeg(_space.Heading);
        _thrust.Text = $"引擎 {_space.ActiveThrusters}/{metrics.Counts.GetValueOrDefault("thruster")} · 前/反推力 {_space.ForwardThrust:0.#}/{_space.ReverseThrust:0.#}";
        _damage.Text = $"火力 {metrics.Damage:0.0} · 档案 {_space.World.ArchiveFragments}";
        _ship.Text = $"航向 {headingDegrees:0}° · 速度 {_space.Speed:0.0} · 扇区 {_space.WorldPosition.X:0},{_space.WorldPosition.Y:0}";
        _power.Text = $"转向 {_space.TurnCapacity:0.#}     功率 {_space.World.GetShipPowerRatio():P0}";
        _shieldBar.Value = _space.PlayerMaxShield <= 0 ? 0 : _space.PlayerShield / _space.PlayerMaxShield * 100.0;
        _hullBar.Value = _space.PlayerMaxHull <= 0 ? 0 : _space.PlayerHull / _space.PlayerMaxHull * 100.0;
        _heatBar.Value = _space.BattleHeatCapacity <= 0 ? 0 : Mathf.Clamp((float)(_space.BattleHeat / _space.BattleHeatCapacity), 0, 1) * 100.0f;
        _shieldBarLabel.Text = $"护盾 {_space.PlayerShield:0}/{_space.PlayerMaxShield:0}";
        _hullBarLabel.Text = $"舰体 {_space.PlayerHull:0}/{_space.PlayerMaxHull:0}";
        _heatBarLabel.Text = $"热量 {_space.BattleHeat:0.0}/{_space.BattleHeatCapacity:0.0}";

        var victory = _space.Phase == "Victory";
        _resultPanel.Visible = !string.Equals(_space.Phase, "Active");
        _resultBackdrop.Visible = _resultPanel.Visible;
        _resultTitle.AddThemeColorOverride("font_color", victory ? UiTheme.Success : UiTheme.Danger);
        if (_lastPhase != _space.Phase && _resultPanel.Visible)
        {
            _resultPanel.Modulate = new Color(1, 1, 1, 0);
            CreateTween().TweenProperty(_resultPanel, "modulate:a", 1.0f, 0.2);
        }
        _lastPhase = _space.Phase;
        _resultTitle.Text = victory ? $"{_space.World.GetExpeditionName(_space.ExpeditionStage)} / 残骸已标记" : "试航失败 / 舰体失去响应";
        _resultBody.Text = victory
            ? $"{_space.World.GetExpeditionBrief(_space.ExpeditionStage)} 已完成，回收物资已经送入工厂仓库。\n航线状态：{(_space.ExpeditionStage < 3 ? "下一航段已解锁" : "当前航线已完成")} · 档案碎片 +1"
            : "蓝图和已解锁航线保留。调整引擎布局、装甲或散热后再试。";
        _resultHint.Text = victory && _space.ExpeditionStage < 3
            ? "N 进入下一航段       Enter / Space 返回       R 重开"
            : "Enter / Space 返回航线图       R 重开当前航段";
        _nextWaveAction.Visible = victory && _space.ExpeditionStage < 3;
        _returnAction.Position = new Vector2(_nextWaveAction.Visible ? 240 : 44, 184);
        _restartAction.Position = new Vector2(_nextWaveAction.Visible ? 420 : 240, 184);
        _returnAction.Visible = true;
        _restartAction.Visible = true;
    }

    private void BuildTree()
    {
        if (_built)
            return;
        _built = true;
        AddLabel(this, "试航训练", new Vector2(24, 20), 26, UiTheme.OnDark);
        _stage = AddLabel(this, "", new Vector2(26, 66), 17, UiTheme.Accent);
        AddLabel(this, "ESC / B 返回    P 暂停", new Vector2(1030, 24), 13, UiTheme.TextSecondary).Size = new Vector2(230, 28);
        _controls = AddLabel(this, "", new Vector2(320, 699), 12, UiTheme.TextMuted);
        _controls.Size = new Vector2(880, 20);
        var survival = CreatePanel(new Vector2(24, 592), new Vector2(264, 100), UiTheme.Surface);
        UiTheme.AccentBar(survival, Vector2.Zero, new Vector2(32, 2), UiTheme.Accent);
        AddLabel(survival, "种子舰", new Vector2(14, 10), 17, UiTheme.TextPrimary);
        _shieldBarLabel = AddLabel(survival, "", new Vector2(14, 41), 13, UiTheme.TextSecondary);
        _hullBarLabel = AddLabel(survival, "", new Vector2(14, 69), 13, UiTheme.TextSecondary);
        _shieldBarLabel.Size = _hullBarLabel.Size = new Vector2(126, 24);
        _shieldBar = UiTheme.Progress(survival, new Vector2(146, 49), UiTheme.Info, 102, 5);
        _hullBar = UiTheme.Progress(survival, new Vector2(146, 77), UiTheme.Success, 102, 5);
        var heatPanel = CreatePanel(new Vector2(460, 636), new Vector2(360, 56), UiTheme.Surface);
        _heatBarLabel = AddLabel(heatPanel, "", new Vector2(14, 8), 14, UiTheme.TextSecondary);
        _heatBarLabel.Size = new Vector2(330, 24);
        _heatBar = UiTheme.Progress(heatPanel, new Vector2(14, 40), UiTheme.Warning, 330, 5);
        _ship = AddLabel(this, "", new Vector2(970, 586), 16, UiTheme.TextPrimary);
        _thrust = AddLabel(this, "", new Vector2(970, 621), 13, UiTheme.TextSecondary);
        _power = AddLabel(this, "", new Vector2(970, 653), 13, UiTheme.TextSecondary);
        _damage = AddLabel(this, "", new Vector2(26, 102), 13, UiTheme.TextMuted);
        foreach (var label in new[] { _ship, _thrust, _power, _damage })
            label.Size = new Vector2(290, 28);

        _resultBackdrop = new ColorRect { Color = new Color(0.06f, 0.1f, 0.09f, 0.65f), Position = Vector2.Zero, Size = new Vector2(1280, 720), MouseFilter = MouseFilterEnum.Stop, Visible = false };
        AddChild(_resultBackdrop);

        _resultPanel = CreatePanel(new Vector2(300, 188), new Vector2(680, 250), UiTheme.SurfaceRaised);
        _resultPanel.MouseFilter = MouseFilterEnum.Stop;
        UiTheme.AccentBar(_resultPanel, new Vector2(44, 36), new Vector2(36, 4), UiTheme.Accent, 2);
        _resultTitle = AddLabel(_resultPanel, "", new Vector2(44, 50), 25, UiTheme.Accent);
        _resultBody = AddLabel(_resultPanel, "", new Vector2(44, 92), 15, UiTheme.TextPrimary);
        _resultBody.Size = new Vector2(590, 72);
        _resultBody.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _nextWaveAction = AddButton(_resultPanel, "进入下一航段 [N]", new Vector2(44, 184), new Vector2(186, 30), UiTheme.Warning, _space.RequestNextWave);
        UiTheme.PrimaryButton(_nextWaveAction);
        _returnAction = AddButton(_resultPanel, "返回航线图", new Vector2(240, 184), new Vector2(170, 30), UiTheme.Info, _space.RequestExit);
        _restartAction = AddButton(_resultPanel, "重开当前航段 [R]", new Vector2(420, 184), new Vector2(190, 30), UiTheme.Accent, _space.RequestRestart);
        _resultHint = AddLabel(_resultPanel, "", new Vector2(44, 222), 12, UiTheme.TextSecondary);
    }

    private Panel CreatePanel(Vector2 position, Vector2 size, Color fill)
    {
        var panel = new Panel { Position = position, Size = size };
        UiTheme.Panel(panel, fill);
        AddChild(panel);
        return panel;
    }

    private static Label AddLabel(Control parent, string text, Vector2 position, int size, Color color)
    {
        var label = new Label { Text = text, Position = position, Size = new Vector2(620, 24), ClipText = true };
        UiTheme.Label(label, size, color);
        parent.AddChild(label);
        return label;
    }

    private static Button AddButton(Control parent, string text, Vector2 position, Vector2 size, Color accent, Action action)
    {
        var button = new Button { Text = text, Position = position, Size = size };
        UiTheme.Button(button, accent);
        button.Pressed += action;
        parent.AddChild(button);
        return button;
    }

}
