using Godot;

/// <summary>宇宙探索与 M 模块化造舰的页面入口。</summary>
public partial class Main : Node2D
{
    private ShipBuilder _shipBuilder = null!;
    private UniverseView _universe = null!;
    private UpgradeScreen _upgrades = null!;
    private bool _building;
    private bool _upgradesOpen;
    private bool _returnToBuilder;
    private Window.ModeEnum _windowedMode = Window.ModeEnum.Maximized;

    public override void _Ready()
    {
        var progress = PlayerProgress.Load();
        progress.Autosave = true;
        _shipBuilder = new ShipBuilder { Name = "ShipBuilder", Progress = progress };
        _shipBuilder.Ship.SeedReferenceShip();
        AddChild(_shipBuilder);

        _universe = new UniverseView { Name = "Universe", Progress = progress };
        _universe.SetShip(_shipBuilder.Ship);
        AddChild(_universe);
        _upgrades = new UpgradeScreen { Name = "Upgrades", Progress = progress };
        _upgrades.CloseRequested += CloseUpgrades;
        AddChild(_upgrades);
        SetPage(_shipBuilder, false);
        SetPage(_universe, true);
        SetPage(_upgrades, false);
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (key.Keycode == Key.F11 || (key.AltPressed && key.Keycode == Key.Enter))
        {
            var window = GetWindow();
            if (window.Mode is Window.ModeEnum.Fullscreen or Window.ModeEnum.ExclusiveFullscreen)
                window.Mode = _windowedMode;
            else
            {
                _windowedMode = window.Mode;
                window.Mode = Window.ModeEnum.Fullscreen;
            }
        }
        else if (key.Keycode == Key.U)
        {
            if (_upgradesOpen) CloseUpgrades();
            else OpenUpgrades();
            GetViewport().SetInputAsHandled();
        }
        else if (_upgradesOpen && key.Keycode == Key.Escape)
        {
            CloseUpgrades();
            GetViewport().SetInputAsHandled();
        }
        else if (key.Keycode == Key.M || (_building && key.Keycode == Key.Escape))
        {
            if (_upgradesOpen) { GetViewport().SetInputAsHandled(); return; }
            if (!_building && _universe.TravelLocksBuilder) { GetViewport().SetInputAsHandled(); return; }
            _universe.CloseNavigationOverlays();
            // 丢弃未完成的拖动；已完成的编辑保留在同一个舰体中。
            _shipBuilder.FocusShip();
            _building = !_building;
            SetPage(_universe, !_building);
            SetPage(_shipBuilder, _building);
            if (!_building) _universe.ResumeFlight();
        }
        else return;
        GetViewport().SetInputAsHandled();
    }

    private void OpenUpgrades()
    {
        _returnToBuilder = _building;
        _upgradesOpen = true;
        _universe.CloseNavigationOverlays();
        _shipBuilder.FocusShip();
        SetPage(_universe, false);
        SetPage(_shipBuilder, false);
        SetPage(_upgrades, true);
    }

    private void CloseUpgrades()
    {
        if (!_upgradesOpen) return;
        _upgradesOpen = false;
        SetPage(_upgrades, false);
        SetPage(_shipBuilder, _returnToBuilder);
        SetPage(_universe, !_returnToBuilder);
        if (!_returnToBuilder) _universe.ResumeFlight();
    }

    private static void SetPage(CanvasItem page, bool active)
    {
        page.Visible = active;
        page.ProcessMode = active ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
    }
}
