using Godot;

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
    private ProgressBar _shieldBar = null!;
    private ProgressBar _hullBar = null!;
    private ProgressBar _heatBar = null!;
    private Panel _resultPanel = null!;
    private bool _built;

    public void Setup(SpaceTest space)
    {
        _space = space;
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
        _stage.Text = $"航段 {_space.ExpeditionStage} · 已瘫痪 {_space.Score} / {_space.TargetCount}";
        _controls.Text = "W/S 前进与倒车 · A/D 差动转向 · 鼠标左键开火";
        _thrust.Text = $"引擎 {metrics.Counts.GetValueOrDefault("thruster")} · 安装推力 {metrics.Thrust:0.0} · 前向 {_space.ForwardThrust:0.0} · 反向 {_space.ReverseThrust:0.0}";
        _damage.Text = $"火力 {metrics.Damage:0.0}";
        _ship.Text = $"护盾 {_space.PlayerShield:0}/{_space.PlayerMaxShield:0}   舰体 {_space.PlayerHull:0}/{_space.PlayerMaxHull:0}";
        _power.Text = $"功率 {_space.World.GetShipPowerRatio():P0} · 热量 {_space.BattleHeat:0.0}/{_space.BattleHeatCapacity:0.0} · 档案 {_space.World.ArchiveFragments}";
        _shieldBar.Value = _space.PlayerMaxShield <= 0 ? 0 : _space.PlayerShield / _space.PlayerMaxShield * 100.0;
        _hullBar.Value = _space.PlayerMaxHull <= 0 ? 0 : _space.PlayerHull / _space.PlayerMaxHull * 100.0;
        _heatBar.Value = _space.BattleHeatCapacity <= 0 ? 0 : Mathf.Clamp((float)(_space.BattleHeat / _space.BattleHeatCapacity), 0, 1) * 100.0f;

        var victory = _space.Phase == "Victory";
        _resultPanel.Visible = !string.Equals(_space.Phase, "Active");
        _resultTitle.Text = victory ? "试航胜利 / 残骸已标记" : "试航失败 / 舰体失去响应";
        _resultBody.Text = victory
            ? "风暴闸门航线已解锁，回收物资已经送入工厂仓库。\n合金 +6   晶体 +2   挥发物 +4   档案碎片 +1"
            : "蓝图和已解锁航线保留。调整引擎布局、装甲或散热后再试。";
        _resultHint.Text = victory && _space.ExpeditionStage == 1
            ? "N 进入风暴闸门       Enter / Space 返回       R 重开"
            : "Enter / Space 返回模块实验室       R 重开当前航段";
    }

    private void BuildTree()
    {
        if (_built)
            return;
        _built = true;
        var header = CreatePanel(new Vector2(18, 18), new Vector2(1244, 46), UiTheme.Surface);
        AddLabel(header, "重组：新曙光  //  试航区", new Vector2(16, 8), 19, UiTheme.TextPrimary);
        _controls = AddLabel(header, "", new Vector2(374, 13), 11, UiTheme.TextSecondary);
        _stage = AddLabel(header, "", new Vector2(1010, 13), 13, UiTheme.Warning);

        var footer = CreatePanel(new Vector2(18, 650), new Vector2(1244, 46), UiTheme.Surface);
        _thrust = AddLabel(footer, "", new Vector2(16, 7), 11, new Color("#b48ce0"));
        _damage = AddLabel(footer, "", new Vector2(16, 27), 12, UiTheme.Danger);
        _ship = AddLabel(footer, "", new Vector2(290, 7), 11, UiTheme.TextPrimary);
        _power = AddLabel(footer, "", new Vector2(290, 27), 10, UiTheme.TextSecondary);
        _shieldBar = AddProgress(footer, new Vector2(900, 9), UiTheme.Info);
        _hullBar = AddProgress(footer, new Vector2(900, 20), UiTheme.Accent);
        _heatBar = AddProgress(footer, new Vector2(900, 31), UiTheme.Warning);

        _resultPanel = CreatePanel(new Vector2(300, 188), new Vector2(680, 250), UiTheme.BackgroundDeep);
        _resultTitle = AddLabel(_resultPanel, "", new Vector2(44, 42), 25, UiTheme.Accent);
        _resultBody = AddLabel(_resultPanel, "", new Vector2(44, 84), 13, UiTheme.TextPrimary);
        _resultBody.Size = new Vector2(590, 72);
        _resultBody.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _resultHint = AddLabel(_resultPanel, "", new Vector2(44, 188), 13, UiTheme.TextSecondary);
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
        var label = new Label { Text = text, Position = position, Size = new Vector2(620, 24) };
        UiTheme.Label(label, size, color);
        parent.AddChild(label);
        return label;
    }

    private static ProgressBar AddProgress(Control parent, Vector2 position, Color fill)
    {
        var bar = new ProgressBar { Position = position, Size = new Vector2(328, 7), MinValue = 0, MaxValue = 100, ShowPercentage = false };
        bar.AddThemeStyleboxOverride("background", UiTheme.Box(UiTheme.BackgroundDeep, UiTheme.Border));
        bar.AddThemeStyleboxOverride("fill", UiTheme.Box(fill, fill, 0));
        parent.AddChild(bar);
        return bar;
    }
}
