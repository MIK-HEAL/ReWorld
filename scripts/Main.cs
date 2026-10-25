using Godot;

/// <summary>开始页面与宇宙探索 / M 模块化造舰的页面入口。</summary>
public partial class Main : Node2D
{
    private StartScreen? _start;
    private ShipBuilder? _shipBuilder;
    private UniverseView? _universe;
    private UpgradeScreen? _upgrades;
    private StationScreen? _station;
    private FactoryBuilderScreen? _factory;
    private bool _gameReady;
    private bool _building;
    private bool _upgradesOpen;
    private bool _stationOpen;
    private bool _factoryOpen;
    private bool _returnToBuilder;
    private Window.ModeEnum _windowedMode = Window.ModeEnum.Maximized;

    public override void _Ready()
    {
        // 游戏页面在选择战役后构建；开始页面负责「新的战役 / 继续战役」。
        _start = new StartScreen { Name = "StartScreen" };
        _start.NewGameRequested += () => StartGame(fresh: true);
        _start.ContinueRequested += () => StartGame(fresh: false);
        _start.QuitRequested += () => GetTree().Quit();
        AddChild(_start);
    }

    /// <summary>按开始页选择构建战役：fresh 为真时丢弃存档进度重新开始。</summary>
    private void StartGame(bool fresh)
    {
        if (_gameReady) return;
        var progress = fresh ? new PlayerProgress() : PlayerProgress.Load();
        progress.Autosave = true;

        _shipBuilder = new ShipBuilder { Name = "ShipBuilder", Progress = progress };
        _shipBuilder.Ship.SeedReferenceShip();
        AddChild(_shipBuilder);

        _universe = new UniverseView { Name = "Universe", Progress = progress };
        _universe.SetShip(_shipBuilder.Ship);
        _universe.FactoryBuildRequested += OpenFactoryBuilder;
        AddChild(_universe);
        _upgrades = new UpgradeScreen { Name = "Upgrades", Progress = progress };
        _upgrades.CloseRequested += CloseUpgrades;
        AddChild(_upgrades);
        _station = new StationScreen { Name = "Station", Progress = progress, View = _universe };
        _station.CloseRequested += CloseStation;
        AddChild(_station);
        _factory = new FactoryBuilderScreen { Name = "FactoryBuilder", Progress = progress };
        _factory.CloseRequested += CloseFactoryBuilder;
        AddChild(_factory);
        // AI 桥：游戏开始后即可经 127.0.0.1 TCP 暴露统一命令层（GameCommandApi）。
        AddChild(new AiBridge(_universe));
        SetPage(_shipBuilder, false);
        SetPage(_universe, true);
        SetPage(_upgrades, false);
        SetPage(_station, false);
        SetPage(_factory, false);

        if (_start is not null)
        {
            RemoveChild(_start);
            _start.QueueFree();
            _start = null;
        }
        _gameReady = true;
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
            GetViewport().SetInputAsHandled();
            return;
        }
        if (!_gameReady || _shipBuilder is null || _universe is null ||
            _upgrades is null || _station is null || _factory is null) return;
        if (_factoryOpen) { GetViewport().SetInputAsHandled(); return; }
        if (key.Keycode == Key.U)
        {
            if (_upgradesOpen) CloseUpgrades();
            else if (!_stationOpen) OpenUpgrades();
            GetViewport().SetInputAsHandled();
        }
        else if (key.Keycode == Key.T)
        {
            if (_stationOpen) CloseStation();
            else if (!_building && !_upgradesOpen && _universe.IsStationDocked) OpenStation();
            GetViewport().SetInputAsHandled();
        }
        else if (_stationOpen && key.Keycode == Key.Escape)
        {
            CloseStation();
            GetViewport().SetInputAsHandled();
        }
        else if (_upgradesOpen && key.Keycode == Key.Escape)
        {
            CloseUpgrades();
            GetViewport().SetInputAsHandled();
        }
        else if (key.Keycode == Key.M || (_building && key.Keycode == Key.Escape))
        {
            if (_upgradesOpen || _stationOpen) { GetViewport().SetInputAsHandled(); return; }
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
        _universe!.CloseNavigationOverlays();
        _shipBuilder!.FocusShip();
        SetPage(_universe, false);
        SetPage(_shipBuilder, false);
        SetPage(_upgrades!, true);
    }

    private void CloseUpgrades()
    {
        if (!_upgradesOpen) return;
        _upgradesOpen = false;
        SetPage(_upgrades!, false);
        SetPage(_shipBuilder!, _returnToBuilder);
        SetPage(_universe!, !_returnToBuilder);
        if (!_returnToBuilder) _universe!.ResumeFlight();
    }

    private void OpenStation()
    {
        _returnToBuilder = _building;
        _stationOpen = true;
        _universe!.CloseNavigationOverlays();
        SetPage(_universe, false);
        SetPage(_shipBuilder!, false);
        SetPage(_station!, true);
    }

    /// <summary>登陆星球后经地表作业面板进入模块化工厂建造界面：蓝图决定产出，产出折 R 可用于造舰等消费。</summary>
    private void OpenFactoryBuilder()
    {
        if (_factory is null || _universe is not { LandedBody: { } planet }) return;
        _factoryOpen = true;
        _universe.CloseNavigationOverlays();
        _factory.Open(planet.Name, planet.Id, planet.Biome);
        SetPage(_universe, false);
        SetPage(_shipBuilder!, false);
        SetPage(_factory, true);
    }

    private void CloseFactoryBuilder()
    {
        if (!_factoryOpen) return;
        _factoryOpen = false;
        var message = _factory?.ResultMessage;
        SetPage(_factory!, false);
        SetPage(_universe!, true);
        _universe!.ResumeFlight();
        if (message is not null) _universe.SurfaceNotify(message);
    }

    private void CloseStation()
    {
        if (!_stationOpen) return;
        _stationOpen = false;
        SetPage(_station!, false);
        SetPage(_shipBuilder!, _returnToBuilder);
        SetPage(_universe!, !_returnToBuilder);
        if (!_returnToBuilder) _universe!.ResumeFlight();
    }

    private static void SetPage(CanvasItem page, bool active)
    {
        page.Visible = active;
        page.ProcessMode = active ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
    }
}
