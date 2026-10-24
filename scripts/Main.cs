using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class Main : Node2D
{
    private enum BuildContext
    {
        Factory,
        Ship,
        Route,
    }

    private static readonly Rect2 GridRect = new(0.0f, 128.0f, 1014.0f, 368.0f);
    private Vector2 GridOrigin => new Vector2(520, 340) + _cameraPan;
    private float CellSize => 52.0f * _cameraZoom;
    private Vector2 _cameraPan;
    private float _cameraZoom = 1.0f;
    private bool _cameraDragging;
    private static readonly string[] FactoryModuleOrder = { "solar", "miner", "refinery", "battery", "radiator", "cargo", "reactor" };
    private static readonly string[] ShipModuleOrder = { "hull", "armor", "thruster", "gyro", "radiator", "cargo", "tractor", "laser", "cannon", "beam", "railgun", "missile", "shield", "point_defense", "reactor", "generator", "capacitor", "sensor" };

    private WorldState _world = null!;
    private SpaceTest _spaceTest = null!;
    private ShipBuilder _shipBuilder = null!;
    private UniverseView _universe = null!;
    private string _mode = "universe";
    // The game opens in orbit. Factory construction is a landed-planet activity,
    // so it is entered explicitly from the space route instead of being the shell
    // around the whole game.
    private BuildContext _buildContext = BuildContext.Route;
    private string _selectedKind = "hull";
    private int _buildRotation;
    private Vector2I? _selectedGrid;
    private Vector2I? _draggingOrigin;
    private Vector2I _draggingGrabOffset;
    private Vector2I _hoverGrid;
    private bool _hoverValid;
    private Font _font = null!;
    private BuilderUi _builderUi = null!;
    private SpaceHud _spaceHud = null!;

    private ModuleGraph ActiveGraph => _buildContext == BuildContext.Ship ? _world.ShipGraph : _world.FactoryGraph;
    private IReadOnlyList<string> ActiveModuleOrder => _buildContext == BuildContext.Ship ? ShipModuleOrder : FactoryModuleOrder;

    // Read-only screen data exposed to BuilderUi. The simulation remains the single
    // source of truth; the Control tree only presents it and emits user intent.
    public WorldState World => _world;
    public ModuleGraph ActiveGraphForUi => ActiveGraph;
    public IReadOnlyList<ModuleLink> ActiveLinks => ActiveGraph.Links;
    public ModuleMetrics ActiveMetrics => ActiveGraph.GetMetrics();
    public bool IsShipContext => _buildContext == BuildContext.Ship;
    public bool IsRouteContext => _buildContext == BuildContext.Route;
    public string SelectedKind => _selectedKind;
    public int BuildRotation => _buildRotation;
    public ModuleInstance? InspectedModule => _selectedGrid.HasValue ? ActiveGraph.GetModuleAt(_selectedGrid.Value) : null;

    public override void _Ready()
    {
        _font = CreateUiFont();
        _world = new WorldState();
        _world.Changed += OnWorldChanged;
        FrameBlueprint();

        _spaceTest = new SpaceTest();
        AddChild(_spaceTest);
        _spaceTest.Setup(_world);

        _builderUi = new BuilderUi();
        AddChild(_builderUi);
        _builderUi.Setup(this);
        _spaceHud = new SpaceHud();
        AddChild(_spaceHud);
        _spaceHud.Setup(_spaceTest);

        // 模块化造舰页（Reassembly 式任意角度多边形 + 端口吸附）。默认隐藏。
        _shipBuilder = new ShipBuilder();
        AddChild(_shipBuilder);
        _shipBuilder.Visible = false;

        // 无限宇宙是默认入口，占据整屏。造舰/工厂/航线通过按键进入。
        _universe = new UniverseView();
        AddChild(_universe);
        _universe.Visible = true;

        // 把模块化造舰的舰体共享给宇宙探索（运行时以模块多边形显示）。
        _universe.SetShip(_shipBuilder.Ship);

        // 启动进入宇宙：隐藏原有建造 UI。
        _builderUi.Visible = false;
        _spaceHud.Visible = false;

        SetProcess(true);
        QueueRedraw();
    }

    private void OnWorldChanged()
    {
        QueueRedraw();
        _builderUi?.Refresh();
    }

    public override void _Process(double delta)
    {
        // 宇宙探索：UniverseView 自己处理飞行/引力/渲染，这里不推进工厂时钟。
        if (_mode == "universe")
            return;

        // 模块化造舰页：不推进工厂经济时钟，只让造舰编辑器运行。
        if (_mode == "modular")
        {
            QueueRedraw();
            return;
        }

        // The world clock continues while a real expedition is active. Pause and
        // time scale therefore affect production, heat and combat consistently.
        var scaledDelta = Math.Min(delta, _mode == "builder" ? 0.25 : 0.05) * _world.TimeScale;
        _world.Tick(scaledDelta);
        if (_mode == "space")
            _spaceTest.Tick(scaledDelta);

        if (_mode == "space" && _spaceTest.ConsumeExitRequest())
        {
            ExitSpaceTest();
            return;
        }

        QueueRedraw();
        if (_mode == "builder")
        {
            var pointer = GetGlobalMousePosition();
            _hoverValid = !IsRouteContext && !_cameraDragging && GridRect.HasPoint(pointer) && !_builderUi.IsPointerOverOverlay(pointer);
            if (!Input.IsMouseButtonPressed(MouseButton.Middle)) _cameraDragging = false;
            if (_hoverValid)
                _hoverGrid = ScreenToGrid(pointer);
            _builderUi?.Refresh();
        }
        else
            _spaceHud?.Refresh();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // 宇宙探索：M 进模块化造舰，Tab/B 进原有航线/造舰。
        if (_mode == "universe")
        {
            if (@event is InputEventKey ukey && ukey.Pressed && !ukey.Echo)
            {
                if (ukey.Keycode == Key.M)
                    EnterShipBuilder();
                else if (ukey.Keycode == Key.Tab || ukey.Keycode == Key.B)
                    EnterBuilderFromUniverse();
            }
            return;
        }

        // 模块化造舰页：Esc 返回普通建造，其余输入交给 ShipBuilder。
        if (_mode == "modular")
        {
            if (@event is InputEventKey modularKey && modularKey.Pressed && !modularKey.Echo &&
                (modularKey.Keycode == Key.Escape || modularKey.Keycode == Key.M))
            {
                ExitShipBuilder();
            }
            return;
        }

        if (_mode == "space")
        {
            if (_spaceTest.HandleInput(@event))
                return;

            if (_spaceTest.ConsumeExitRequest())
            {
                ExitSpaceTest();
                return;
            }

            if (@event is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo &&
                (keyEvent.Keycode == Key.Escape || keyEvent.Keycode == Key.B))
                ExitSpaceTest();

            return;
        }

        if (_buildContext == BuildContext.Route)
        {
            if (@event is InputEventMouseButton routeMouse && routeMouse.Pressed && routeMouse.ButtonIndex == MouseButton.Left)
            {
                var routeStage = GetRouteStageAt(routeMouse.Position);
                if (routeStage > 0)
                    SelectExpeditionStage(routeStage);
                else if (routeMouse.Position.DistanceTo(new Vector2(650.0f, 560.0f)) <= 54.0f)
                    SelectBuildContext(false);
            }
            if (@event is InputEventKey routeKey && routeKey.Pressed && !routeKey.Echo)
            {
                if (routeKey.Keycode == Key.Tab)
                    SelectBuildContext(true);
                else if (routeKey.Keycode == Key.Space)
                    RequestLaunch();
                else if (routeKey.Keycode == Key.P)
                    _world.TogglePause();
                else if (routeKey.Keycode == Key.Equal || routeKey.Keycode == Key.Plus)
                    _world.AdjustTimeScale(0.25);
                else if (routeKey.Keycode == Key.Minus)
                    _world.AdjustTimeScale(-0.25);
                else if (routeKey.Keycode >= Key.Key1 && routeKey.Keycode <= Key.Key3)
                    SelectExpeditionStage((int)routeKey.Keycode - (int)Key.Key1 + 1);
            }
            return;
        }

        if (@event is InputEventMouseMotion motion)
        {
            if (_cameraDragging)
            {
                _cameraPan += motion.Relative;
                QueueRedraw();
                return;
            }
            _hoverValid = GridRect.HasPoint(motion.Position);
            if (_hoverValid)
                _hoverGrid = ScreenToGrid(motion.Position);
            QueueRedraw();
            return;
        }

        if (@event is InputEventMouseButton mouseButton)
        {
            if (mouseButton.ButtonIndex == MouseButton.Middle)
            {
                _cameraDragging = mouseButton.Pressed && GridRect.HasPoint(mouseButton.Position);
                return;
            }
            if (mouseButton.Pressed && GridRect.HasPoint(mouseButton.Position) && mouseButton.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                var cellAtPointer = (mouseButton.Position - GridOrigin) / CellSize;
                _cameraZoom = Math.Clamp(_cameraZoom * (mouseButton.ButtonIndex == MouseButton.WheelUp ? 1.12f : 1 / 1.12f), 0.55f, 1.8f);
                _cameraPan += mouseButton.Position - (GridOrigin + cellAtPointer * CellSize);
                return;
            }
            if (!mouseButton.Pressed)
            {
                if (mouseButton.ButtonIndex == MouseButton.Left && _draggingOrigin.HasValue)
                {
                    var origin = _draggingOrigin.Value;
                    _draggingOrigin = null;
                    if (GridRect.HasPoint(mouseButton.Position))
                    {
                        var target = ScreenToGrid(mouseButton.Position) - _draggingGrabOffset;
                        if (target != origin && MoveActiveModule(origin, target))
                            _selectedGrid = target;
                    }
                    _draggingGrabOffset = Vector2I.Zero;
                    QueueRedraw();
                }
                return;
            }

            if (mouseButton.ButtonIndex == MouseButton.Left)
            {
                if (GridRect.HasPoint(mouseButton.Position))
                {
                    var gridPosition = ScreenToGrid(mouseButton.Position);
                    var existing = ActiveGraph.GetModuleAt(gridPosition);
                    if (existing is not null)
                    {
                        _selectedGrid = gridPosition;
                        if (existing.Kind != "core")
                        {
                            _draggingOrigin = existing.GridPosition;
                            _draggingGrabOffset = gridPosition - existing.GridPosition;
                        }
                        QueueRedraw();
                    }
                    else
                    {
                        if (BuildActiveModule(gridPosition))
                            _selectedGrid = gridPosition;
                    }
                }
            }
            else if (mouseButton.ButtonIndex == MouseButton.Right && GridRect.HasPoint(mouseButton.Position))
            {
                var gridPosition = ScreenToGrid(mouseButton.Position);
                RemoveActiveModule(gridPosition);
                if (_selectedGrid == gridPosition)
                    _selectedGrid = null;
            }

            QueueRedraw();
            return;
        }

        if (@event is InputEventKey key && key.Pressed && !key.Echo)
        {
            if (key.Keycode == Key.Tab)
            {
                SwitchBuildContext(_buildContext == BuildContext.Factory ? BuildContext.Route : _buildContext == BuildContext.Route ? BuildContext.Ship : BuildContext.Route);
            }
            else if (key.Keycode == Key.F)
            {
                FrameBlueprint();
            }
            else if (key.CtrlPressed && key.Keycode == Key.S)
            {
                if (_buildContext == BuildContext.Ship)
                    _world.SaveBlueprint();
                else
                    _world.Log("蓝图保存属于舰船设计模式，请先按 Tab 切换。");
            }
            else if (key.CtrlPressed && key.Keycode == Key.L)
            {
                if (_buildContext == BuildContext.Ship)
                    _world.LoadBlueprint();
                else
                    _world.Log("蓝图载入属于舰船设计模式，请先按 Tab 切换。");
                _selectedGrid = null;
            }
            else if (key.CtrlPressed && key.Keycode == Key.Z)
            {
                _world.Undo();
                _selectedGrid = null;
            }
            else if (key.CtrlPressed && key.Keycode == Key.Y)
            {
                _world.Redo();
                _selectedGrid = null;
            }
            else if (key.Keycode == Key.Q || key.Keycode == Key.E)
            {
                RotateBuilding(key.Keycode == Key.E ? 1 : -1);
            }
            else if (key.Keycode == Key.Delete || key.Keycode == Key.Backspace)
            {
                var target = _selectedGrid ?? (_hoverValid ? _hoverGrid : (Vector2I?)null);
                if (target.HasValue)
                {
                    RemoveActiveModule(target.Value);
                    _selectedGrid = null;
                }
            }
            else if (key.Keycode == Key.Escape)
            {
                // Esc 返回无限宇宙（宇宙是默认入口）。
                EnterUniverse();
            }
            else if (key.Keycode == Key.M)
            {
                EnterShipBuilder();
            }
            else if (TryGetModuleShortcut(key.Keycode, out var moduleIndex) && moduleIndex < ActiveModuleOrder.Count)
            {
                _selectedKind = ActiveModuleOrder[moduleIndex];
                _selectedGrid = null;
                QueueRedraw();
            }
            else
            {
                switch (key.Keycode)
                {
                    case Key.H:
                        _world.ChooseContract("help");
                        break;
                    case Key.R:
                        _world.ChooseContract("rob");
                        break;
                    case Key.T:
                        _world.ChooseContract("trade");
                        break;
                    case Key.Space:
                        if (_buildContext == BuildContext.Ship)
                            EnterSpaceTest();
                        else
                            _world.Log("试航使用舰船蓝图，请先按 Tab 切换到舰船设计。");
                        break;
                    case Key.P:
                        _world.TogglePause();
                        break;
                    case Key.Equal:
                    case Key.Plus:
                        _world.AdjustTimeScale(0.25);
                        break;
                    case Key.Minus:
                        _world.AdjustTimeScale(-0.25);
                        break;
                }
            }
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (_mode != "builder" || @event is not InputEventMouseButton mouse || mouse.Pressed) return;
        if (mouse.ButtonIndex == MouseButton.Middle) _cameraDragging = false;
        if (mouse.ButtonIndex == MouseButton.Left && (!GridRect.HasPoint(mouse.Position) || _builderUi.IsPointerOverOverlay(mouse.Position)))
        {
            _draggingOrigin = null;
            _draggingGrabOffset = Vector2I.Zero;
        }
    }

    public override void _Draw()
    {
        if (_mode == "space" || _mode == "modular" || _mode == "universe")
            return;

        DrawRect(new Rect2(Vector2.Zero, new Vector2(1280.0f, 720.0f)), UiTheme.BackgroundDeep);
        if (IsRouteContext)
            DrawRoutePanel();
        else
            DrawFactoryPanel();
        // Header, palette, status cards, actions and footer are ordinary Godot
        // Controls owned by BuilderUi. Only the simulation canvas is drawn here.
    }



    private void DrawFactoryPanel()
    {
        // A full-screen engineering projection, with tools floating above it.
        if (!IsShipContext)
        {
            for (var i = 0; i < 18; i++)
            {
                var center = new Vector2((i * 197 + 33) % 1280, (i * 131 + 97) % 624);
                var points = Enumerable.Range(0, 6).Select(j => center + Vector2.FromAngle(j * Mathf.Tau / 6) * (80 + i % 4 * 18)).ToArray();
                DrawColoredPolygon(points, new Color("#163144", 0.38f));
            }
        }
        DrawGrid();
    }

    private void DrawRoutePanel()
    {
        var mapCenter = new Vector2(510, 382);
        for (var radius = 150; radius <= 620; radius += 118)
            DrawArc(mapCenter, radius, 0, Mathf.Tau, 96, new Color("#97aab6", 0.38f), 1, true);
        for (var i = 0; i < 80; i++)
            DrawCircle(new Vector2(24 + i * 97 % 980, 154 + i * 53 % 444), 1, new Color(UiTheme.CanvasInk, 0.3f));

        var nodes = new[]
        {
            new Vector2(230.0f, 430.0f),
            new Vector2(470.0f, 236.0f),
            new Vector2(760.0f, 404.0f),
        };
        DrawLine(nodes[0], nodes[1], new Color("#657e90"), 2.0f);
        DrawLine(nodes[1], nodes[2], new Color("#657e90"), 2.0f);
        var supply = new Vector2(150.0f, 220.0f);
        var relay = new Vector2(520.0f, 540.0f);
        var archive = new Vector2(900.0f, 255.0f);
        DrawLine(nodes[0], supply, new Color("#dfe3e9"), 2.0f);
        DrawLine(nodes[0], relay, new Color("#dfe3e9"), 2.0f);
        DrawLine(relay, nodes[2], new Color("#dfe3e9"), 2.0f);
        DrawLine(relay, new Vector2(650.0f, 560.0f), new Color("#dfe3e9"), 2.0f);
        DrawLine(nodes[2], archive, new Color("#dfe3e9"), 2.0f);
        DrawRouteSignalNode(supply, "补给航标", new Color("#6e94ac"));
        DrawRouteSignalNode(relay, "漂流信标", new Color("#8e7ca6"));
        DrawRouteSignalNode(archive, "未知信号", new Color("#a18d61"));

        DrawRouteNode(nodes[0], 1, "残骸区", "失控无人机 · 低风险");
        DrawRouteNode(nodes[1], 2, "风暴闸门", "风暴哨卫 · 高压");
        DrawRouteNode(nodes[2], 3, "档案环", "监管核心 · 极限");
        var selected = _world.SelectedExpeditionStage;
        DrawCircle(nodes[Math.Clamp(selected - 1, 0, 2)], 42.0f, new Color(UiTheme.Accent, 0.12f), false, 2.0f);
        DrawCircle(nodes[Math.Clamp(selected - 1, 0, 2)], 48.0f, new Color(UiTheme.Accent, 0.16f), false, 1.0f);
        var planet = new Vector2(650.0f, 560.0f);
        DrawCircle(planet, 37.0f, new Color("#d68b59", 0.88f));
        DrawArc(planet, 49.0f, -0.35f, Mathf.Pi + 0.35f, 36, new Color("#7a9cb0", 0.7f), 2.0f);
        DrawCircle(planet, 56.0f, new Color("#d68b59", 0.35f), false, 1.0f);
        DrawRouteNode(planet, 0, "赤道据点", "登录行星 / 工厂升级");
    }

    private void DrawRouteSignalNode(Vector2 center, string label, Color color)
    {
        DrawCircle(center, 8.0f, new Color(color, 0.72f));
        DrawCircle(center, 18.0f, new Color(color, 0.28f), false, 1.0f);
        LabelText(label, center + new Vector2(-30.0f, 31.0f), 11, new Color("#8b9aa4"));
    }

    private void DrawRouteNode(Vector2 center, int stage, string title, string subtitle)
    {
        var unlocked = stage == 0 || _world.IsExpeditionStageUnlocked(stage);
        var selected = stage > 0 && _world.SelectedExpeditionStage == stage;
        var color = stage == 0 ? UiTheme.Info : unlocked ? UiTheme.AccentDeep : new Color("#7f96a4");
        DrawCircle(center, selected ? 26.0f : 20.0f, new Color(color, unlocked ? 0.95f : 0.35f));
        DrawCircle(center, selected ? 38.0f : 31.0f, new Color(color, unlocked ? 0.55f : 0.18f), false, 2.0f);
        LabelText(stage == 0 ? "补给" : stage.ToString(), center + new Vector2(-8.0f, 5.0f), 13, UiTheme.BackgroundDeep);
        LabelText(title, center + new Vector2(-42.0f, 56.0f), 17, UiTheme.CanvasInk);
        LabelText(unlocked ? subtitle : "未解锁", center + new Vector2(-42.0f, 79.0f), 13, UiTheme.CanvasInk);
    }

    private static int GetRouteStageAt(Vector2 position)
    {
        var nodes = new[]
        {
            new Vector2(230.0f, 430.0f),
            new Vector2(470.0f, 236.0f),
            new Vector2(760.0f, 404.0f),
        };
        for (var index = 0; index < nodes.Length; index++)
            if (position.DistanceTo(nodes[index]) <= 44.0f)
                return index + 1;
        return 0;
    }

    private void DrawStars()
    {
        // Faint paper-like speckle behind the grid. The builder canvas stays
        // light and uncluttered, like Reassembly's editor rather than a console.
        for (var i = 0; i < 90; i++)
        {
            var x = (i * 97 + 41) % 1280;
            var y = (i * 53 + 71) % 720;
            var size = 1.0f + i % 2;
            DrawCircle(new Vector2(x, y), size,
                i % 5 == 0 ? new Color("#d3d7df") : i % 3 == 0 ? new Color("#dfe3e9") : new Color("#e9ecf1"));
        }
    }

    private void DrawGrid()
    {
        var firstX = GridOrigin.X - CellSize * 0.5f + Mathf.Ceil((GridRect.Position.X - GridOrigin.X + CellSize * 0.5f) / CellSize) * CellSize;
        var firstY = GridOrigin.Y - CellSize * 0.5f + Mathf.Ceil((GridRect.Position.Y - GridOrigin.Y + CellSize * 0.5f) / CellSize) * CellSize;
        for (var x = firstX; x < GridRect.End.X; x += CellSize)
        {
                DrawLine(new Vector2(x, GridRect.Position.Y), new Vector2(x, GridRect.End.Y), new Color("#1c3949"), 1.0f);
        }

        for (var y = firstY; y < GridRect.End.Y; y += CellSize)
        {
            DrawLine(new Vector2(GridRect.Position.X, y), new Vector2(GridRect.End.X, y), new Color("#1c3949"), 1.0f);
        }

        LabelText($"构造器视图  {_cameraZoom:P0}  /  F 居中", new Vector2(26, 510), 12, UiTheme.TextMuted);

        foreach (var link in ActiveGraph.Links)
        {
            var first = ActiveGraph.Modules.FirstOrDefault(module => module.Id == link.FirstId);
            var second = ActiveGraph.Modules.FirstOrDefault(module => module.Id == link.SecondId);
            if (first is null || second is null)
                continue;
            DrawLine(ModuleCenter(first), ModuleCenter(second), new Color("#36586a"), 5.0f);
            DrawLine(ModuleCenter(first), ModuleCenter(second), new Color("#86a9b5"), 2.0f);
        }

        var operationalIds = ActiveGraph.GetOperationalModuleIds();
        foreach (var module in ActiveGraph.Modules)
        {
            var definition = ModuleGraph.Definitions[module.Kind];
            var center = ModuleCenter(module);
            var rect = ModuleRect(module);
            DrawModuleVisual(module, definition, rect, center);
            if (!operationalIds.Contains(module.Id))
            {
                DrawRect(rect, new Color(UiTheme.Danger, 0.08f));
                DrawRect(rect, new Color(UiTheme.Danger, 0.8f), false, 2.0f);
                DrawLine(rect.Position + new Vector2(6.0f, 6.0f), rect.End - new Vector2(6.0f, 6.0f), new Color(UiTheme.Danger, 0.65f), 1.5f);
                DrawLine(new Vector2(rect.End.X - 6.0f, rect.Position.Y + 6.0f), new Vector2(rect.Position.X + 6.0f, rect.End.Y - 6.0f), new Color(UiTheme.Danger, 0.65f), 1.5f);
            }
            if (_selectedGrid.HasValue && ActiveGraph.GetModuleAt(_selectedGrid.Value)?.Id == module.Id)
            {
                DrawRect(new Rect2(rect.Position - new Vector2(3.0f, 3.0f), rect.Size + new Vector2(6.0f, 6.0f)), UiTheme.Accent, false, 2.0f);
                LabelText(definition.Label, new Vector2(rect.Position.X, rect.End.Y + 20.0f), 14, UiTheme.CanvasInk);
            }
        }

        if (_hoverValid && (_draggingOrigin.HasValue || ActiveGraph.GetModuleAt(_hoverGrid) is null))
        {
            var draggedModule = _draggingOrigin.HasValue ? ActiveGraph.GetModuleAt(_draggingOrigin.Value) : null;
            var previewKind = draggedModule?.Kind ?? _selectedKind;
            var previewRotation = draggedModule?.Rotation ?? _buildRotation;
            var previewAnchor = _draggingOrigin.HasValue ? _hoverGrid - _draggingGrabOffset : _hoverGrid;
            var previewFootprint = ActiveGraph.GetFootprint(previewKind, previewRotation);
            var previewCenter = GridToScreen(previewAnchor) + new Vector2((previewFootprint.X - 1) * CellSize * 0.5f, (previewFootprint.Y - 1) * CellSize * 0.5f);
            var hoverRect = new Rect2(previewCenter - new Vector2(previewFootprint.X * CellSize, previewFootprint.Y * CellSize) * 0.5f + new Vector2(4.0f, 4.0f), new Vector2(previewFootprint.X * CellSize - 8.0f, previewFootprint.Y * CellSize - 8.0f));
            var canPlace = ActiveGraph.CanOccupy(previewKind, previewAnchor, previewRotation, draggedModule)
                && ActiveGraph.HasAdjacentModule(previewKind, previewAnchor, previewRotation, draggedModule);
            DrawRect(hoverRect, canPlace ? new Color(UiTheme.Accent, 0.20f) : new Color(UiTheme.Danger, 0.18f), false, 2.0f);
            if (ActiveGraph.GetModuleAt(_hoverGrid) is null || _draggingOrigin.HasValue)
                LabelText(_draggingOrigin.HasValue ? (canPlace ? "可移动" : "已占用") : (canPlace ? "可部署" : "需连接 / 已占用"), hoverRect.Position + new Vector2(-8.0f, -8.0f), 14, canPlace ? UiTheme.CanvasInk : UiTheme.Danger);
            if (canPlace)
            {
                var previewDefinition = ModuleGraph.Definitions[previewKind];
                DrawRect(hoverRect, new Color(previewDefinition.ColorHex, 0.20f));
                DrawRotationIndicator(previewCenter, previewRotation, UiTheme.Accent);
            }
        }
    }











    private void SwitchBuildContext(BuildContext context)
    {
        if (_buildContext == context)
            return;
        _buildContext = context;
        FrameBlueprint();
        var moduleOrder = ActiveModuleOrder;
        if (!moduleOrder.Contains(_selectedKind))
            _selectedKind = moduleOrder[0];
        _selectedGrid = null;
        _draggingOrigin = null;
        _draggingGrabOffset = Vector2I.Zero;
        _world.Log(context switch
        {
            BuildContext.Factory => "已登录大型行星「赤道据点」：这里可以升级工厂、采矿和精炼设施。",
            BuildContext.Ship => "返回轨道舰船设计：调整模块、射界和推进布局。",
            _ => "返回深空：选择远征航段、查看信号与回收情报。",
        });
        QueueRedraw();
    }

    public void SelectBuildContext(bool ship) => SwitchBuildContext(ship ? BuildContext.Ship : BuildContext.Factory);

    private void FrameBlueprint()
    {
        if (ActiveGraph.Modules.Count == 0) return;
        var minX = ActiveGraph.Modules.Min(module => module.GridPosition.X);
        var minY = ActiveGraph.Modules.Min(module => module.GridPosition.Y);
        var maxX = ActiveGraph.Modules.Max(module => module.GridPosition.X + ActiveGraph.GetFootprint(module.Kind, module.Rotation).X);
        var maxY = ActiveGraph.Modules.Max(module => module.GridPosition.Y + ActiveGraph.GetFootprint(module.Kind, module.Rotation).Y);
        _cameraZoom = Math.Clamp(Math.Min(540f / ((maxX - minX) * 52), 390f / ((maxY - minY) * 52)), 0.55f, 1.35f);
        var center = new Vector2((minX + maxX - 1) * 0.5f, (minY + maxY - 1) * 0.5f);
        _cameraPan = new Vector2(620, 366) - center * CellSize - new Vector2(520, 340);
    }

    public void SelectRoute() => SwitchBuildContext(BuildContext.Route);

    public void SelectExpeditionStage(int stage)
    {
        _world.SelectExpeditionStage(stage);
        QueueRedraw();
        _builderUi?.Refresh();
    }

    public void SelectModule(string kind)
    {
        if (!ActiveModuleOrder.Contains(kind))
            return;
        _selectedKind = kind;
        _selectedGrid = null;
        QueueRedraw();
        _builderUi?.Refresh();
    }

    public void ChooseContract(string choice) => _world.ChooseContract(choice);

    public void RequestLaunch()
    {
        if (IsShipContext && !_world.ShipReady)
        {
            if (!_world.ShipAssemblyQueued)
                _world.AssembleShip();
            else
                _world.Log($"装配坞仍在工作：{_world.ShipAssemblyProgress:P0}。先等待装配完成。");
            return;
        }

        if (IsRouteContext && !_world.ShipReady)
        {
            _world.Log("这艘舰船尚未装配完成，返回设计台等待装配坞完成后再出发。");
            SelectBuildContext(true);
            return;
        }

        if (IsRouteContext || IsShipContext)
            EnterSpaceTest();
        else
            _world.Log("试航使用舰船蓝图，请先切换到舰船设计。");
    }

    private bool BuildActiveModule(Vector2I gridPosition) => _buildContext == BuildContext.Factory
        ? _world.BuildModule(_selectedKind, gridPosition, _buildRotation)
        : _world.BuildShipModule(_selectedKind, gridPosition, _buildRotation);

    private bool RemoveActiveModule(Vector2I gridPosition) => _buildContext == BuildContext.Factory
        ? _world.RemoveModule(gridPosition)
        : _world.RemoveShipModule(gridPosition);

    private bool MoveActiveModule(Vector2I from, Vector2I to) => _buildContext == BuildContext.Factory
        ? _world.MoveModule(from, to)
        : _world.MoveShipModule(from, to);


    /// <summary>进入模块化造舰页（Reassembly 式任意角度多边形舰体）。</summary>
    private void EnterShipBuilder()
    {
        _mode = "modular";
        if (_builderUi is not null)
            _builderUi.Visible = false;
        if (_spaceHud is not null)
            _spaceHud.Visible = false;
        if (_shipBuilder is not null)
            _shipBuilder.Visible = true;
        _world.Log("进入模块化造舰：用任意角度多边形方块 + 端口吸附，拼出 Reassembly 式舰体。按 Esc 或 M 返回。");
        QueueRedraw();
    }

    private void ExitShipBuilder()
    {
        EnterUniverse();
    }

    /// <summary>从无限宇宙进入原有航线/造舰界面。</summary>
    private void EnterBuilderFromUniverse()
    {
        _mode = "builder";
        _buildContext = BuildContext.Route;
        if (_universe is not null)
            _universe.Visible = false;
        if (_shipBuilder is not null)
            _shipBuilder.Visible = false;
        if (_builderUi is not null)
            _builderUi.Visible = true;
        if (_spaceHud is not null)
            _spaceHud.Visible = false;
        QueueRedraw();
        _builderUi?.Refresh();
    }

    /// <summary>返回无限宇宙（默认入口）。</summary>
    private void EnterUniverse()
    {
        _mode = "universe";
        if (_shipBuilder is not null)
            _shipBuilder.Visible = false;
        if (_builderUi is not null)
            _builderUi.Visible = false;
        if (_spaceHud is not null)
            _spaceHud.Visible = false;
        if (_universe is not null)
            _universe.Visible = true;
        QueueRedraw();
    }

    private void EnterSpaceTest()
    {
        _mode = "space";
        if (_builderUi is not null)
            _builderUi.Visible = false;
        if (_spaceHud is not null)
            _spaceHud.Visible = true;
        _spaceTest.Enter();
        _spaceHud?.Refresh();
        QueueRedraw();
    }

    private void ExitSpaceTest()
    {
        _mode = "builder";
        _spaceTest.Exit();
        _buildContext = BuildContext.Route;
        _selectedGrid = null;
        _draggingOrigin = null;
        if (_builderUi is not null)
            _builderUi.Visible = true;
        if (_spaceHud is not null)
            _spaceHud.Visible = false;
        QueueRedraw();
        _builderUi?.Refresh();
    }

    private Vector2 GridToScreen(Vector2I gridPosition) => GridOrigin + new Vector2(gridPosition.X, gridPosition.Y) * CellSize;

    private Vector2 ModuleCenter(ModuleInstance module)
    {
        var footprint = ActiveGraph.GetFootprint(module.Kind, module.Rotation);
        return GridToScreen(module.GridPosition) + new Vector2((footprint.X - 1) * CellSize * 0.5f, (footprint.Y - 1) * CellSize * 0.5f);
    }

    private Rect2 ModuleRect(ModuleInstance module)
    {
        var footprint = ActiveGraph.GetFootprint(module.Kind, module.Rotation);
        var size = new Vector2(footprint.X * CellSize - 8.0f, footprint.Y * CellSize - 8.0f);
        return new Rect2(ModuleCenter(module) - size * 0.5f, size);
    }

    private Vector2I ScreenToGrid(Vector2 screenPosition) => new(
        Mathf.RoundToInt((screenPosition.X - GridOrigin.X) / CellSize),
        Mathf.RoundToInt((screenPosition.Y - GridOrigin.Y) / CellSize));

    private static bool TryGetModuleShortcut(Key key, out int index)
    {
        if (key >= Key.Key1 && key <= Key.Key9)
        {
            index = (int)key - (int)Key.Key1;
            return true;
        }
        if (key == Key.Key0)
        {
            index = 9;
            return true;
        }
        if (key == Key.A)
        {
            index = 10;
            return true;
        }
        if (key == Key.D)
        {
            index = 11;
            return true;
        }
        index = -1;
        return false;
    }

    private static string GetModuleShortcutLabel(int index) => index switch
    {
        < 9 => (index + 1).ToString(),
        9 => "0",
        10 => "A",
        11 => "D",
        _ => "?",
    };

    private Font CreateUiFont()
    {
        var systemFont = new SystemFont
        {
            FontNames = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Noto Sans CJK SC" },
            AllowSystemFallback = true,
        };
        return systemFont;
    }

    private void RotateBuilding(int quarterTurns)
    {
        if (_selectedGrid.HasValue && ActiveGraph.GetModuleAt(_selectedGrid.Value) is not null)
        {
            if (_buildContext == BuildContext.Factory)
                _world.RotateModule(_selectedGrid.Value, quarterTurns);
            else
                _world.RotateShipModule(_selectedGrid.Value, quarterTurns);
            return;
        }

        _buildRotation = ((_buildRotation + quarterTurns) % 4 + 4) % 4;
        QueueRedraw();
    }

    private void DrawRotationIndicator(Vector2 center, int rotation, Color color)
    {
        var angle = -Mathf.Pi * 0.5f + rotation * Mathf.Pi * 0.5f;
        var direction = Vector2.Right.Rotated(angle);
        DrawLine(center, center + direction * 15.0f, color, 2.0f);
        var tip = center + direction * 15.0f;
        var side = direction.Rotated(Mathf.Pi * 0.75f) * 5.0f;
        DrawLine(tip, tip - side, color, 2.0f);
        DrawLine(tip, tip - side.Rotated(Mathf.Pi * 0.5f), color, 2.0f);
    }

    private void DrawModuleVisual(ModuleInstance module, ModuleDefinition definition, Rect2 rect, Vector2 center)
    {
        var color = new Color(definition.ColorHex).Lerp(new Color("#b5c3cc"), 0.4f);
        if (module.Kind == "shield")
        {
            var radius = Math.Min(rect.Size.X, rect.Size.Y) * 0.42f;
            DrawCircle(center, radius, color);
            DrawCircle(center, radius, new Color("#2f3540"), false, 1.0f);
            return;
        }

        if (module.Kind == "thruster")
        {
            var direction = Vector2.Right.Rotated(-Mathf.Pi * 0.5f + module.Rotation * Mathf.Pi * 0.5f);
            var nose = center + direction * Math.Min(rect.Size.X, rect.Size.Y) * 0.42f;
            var tail = center - direction * Math.Min(rect.Size.X, rect.Size.Y) * 0.42f;
            var side = direction.Rotated(Mathf.Pi * 0.5f) * Math.Min(rect.Size.X, rect.Size.Y) * 0.34f;
            DrawColoredPolygon(new[] { nose, tail + side, tail - side }, color);
            DrawPolyline(new[] { nose, tail + side, tail - side, nose }, new Color("#2f3540"), 1.0f);
            return;
        }

        if (module.Kind == "hull")
        {
            var chamfer = Math.Min(rect.Size.X, rect.Size.Y) * 0.18f;
            var polygon = new[]
            {
                rect.Position + new Vector2(chamfer, 0.0f),
                rect.Position + new Vector2(rect.Size.X - chamfer, 0.0f),
                rect.Position + new Vector2(rect.Size.X, chamfer),
                rect.Position + new Vector2(rect.Size.X, rect.Size.Y - chamfer),
                rect.Position + new Vector2(rect.Size.X - chamfer, rect.Size.Y),
                rect.Position + new Vector2(chamfer, rect.Size.Y),
                rect.Position + new Vector2(0.0f, rect.Size.Y - chamfer),
                rect.Position + new Vector2(0.0f, chamfer),
            };
            DrawColoredPolygon(polygon, color);
            DrawPolyline(polygon.Append(polygon[0]).ToArray(), new Color("#2f3540"), 1.0f);
            DrawLine(rect.Position + new Vector2(rect.Size.X * 0.25f, rect.Size.Y * 0.5f), rect.Position + new Vector2(rect.Size.X * 0.75f, rect.Size.Y * 0.5f), new Color("#2f3540", 0.5f), 1.0f);
            return;
        }

        DrawStyleBox(UiTheme.Box(new Color("#71838f", 0.35f), Colors.Transparent, 0, 0), new Rect2(rect.Position + new Vector2(3, 4), rect.Size));
        DrawRect(rect, color);
        DrawRect(rect, new Color("#435c6b"), false, 1.0f);
        DrawLine(rect.Position + new Vector2(2, 2), new Vector2(rect.End.X - 2, rect.Position.Y + 2), new Color("#e0e7e8"), 1);
        DrawRect(new Rect2(rect.Position + new Vector2(4, 4), rect.Size - new Vector2(8, 8)), new Color("#607785", 0.35f), false, 1);
        if (module.Kind == "core")
        {
            var radius = Math.Min(rect.Size.X, rect.Size.Y) * 0.28f;
            var diamond = new[] { center + Vector2.Up * radius, center + Vector2.Right * radius, center + Vector2.Down * radius, center + Vector2.Left * radius };
            DrawColoredPolygon(diamond, new Color("#456677"));
            DrawCircle(center, radius * 0.32f, new Color("#c8ece7"));
        }
        if (module.Kind == "solar")
        {
            for (var index = 1; index < 4; index++)
            {
                var x = rect.Position.X + rect.Size.X * index / 4.0f;
                DrawLine(new Vector2(x, rect.Position.Y + 4.0f), new Vector2(x, rect.End.Y - 4.0f), new Color("#8a6d2f", 0.75f), 1.0f);
            }
            DrawLine(new Vector2(rect.Position.X + 4.0f, center.Y), new Vector2(rect.End.X - 4.0f, center.Y), new Color("#8a6d2f", 0.75f), 1.0f);
        }
        else if (module.Kind == "miner")
        {
            var direction = Vector2.Right.Rotated(-Mathf.Pi * 0.5f + module.Rotation * Mathf.Pi * 0.5f);
            DrawLine(center - direction * Math.Min(rect.Size.X, rect.Size.Y) * 0.12f, center + direction * Math.Min(rect.Size.X, rect.Size.Y) * 0.42f, new Color("#2f3540"), 2.0f);
            DrawCircle(center + direction * Math.Min(rect.Size.X, rect.Size.Y) * 0.42f, 4.0f, new Color("#2f3540"));
        }
        else if (module.Kind == "refinery")
        {
            var radius = Math.Min(rect.Size.X, rect.Size.Y) * 0.24f;
            DrawCircle(center, radius, new Color("#8a6d2f", 0.35f));
            DrawCircle(center, radius, new Color("#8a6d2f"), false, 1.5f);
            DrawLine(center + Vector2.Left * radius, center + Vector2.Right * radius, new Color("#8a6d2f", 0.8f), 1.0f);
        }
        else if (module.Kind == "battery")
        {
            var horizontal = rect.Size.X >= rect.Size.Y;
            var start = horizontal ? new Vector2(rect.Position.X + 8.0f, center.Y) : new Vector2(center.X, rect.Position.Y + 8.0f);
            var end = horizontal ? new Vector2(rect.End.X - 8.0f, center.Y) : new Vector2(center.X, rect.End.Y - 8.0f);
            DrawLine(start, end, new Color("#2f3540"), 3.0f);
            DrawLine(start + (horizontal ? Vector2.Up * 5.0f : Vector2.Left * 5.0f), end + (horizontal ? Vector2.Up * 5.0f : Vector2.Left * 5.0f), new Color("#2f3540", 0.45f), 1.0f);
        }
        else if (module.Kind == "cargo")
        {
            var inner = new Rect2(rect.Position + new Vector2(8.0f, 8.0f), rect.Size - new Vector2(16.0f, 16.0f));
            DrawRect(inner, new Color("#8a6d2f", 0.14f));
            DrawRect(inner, new Color("#8a6d2f", 0.7f), false, 1.0f);
            DrawLine(inner.Position, inner.End, new Color("#8a6d2f", 0.6f), 1.0f);
            DrawLine(new Vector2(inner.End.X, inner.Position.Y), new Vector2(inner.Position.X, inner.End.Y), new Color("#8a6d2f", 0.6f), 1.0f);
        }
        else if (module.Kind == "radiator")
        {
            var horizontal = rect.Size.X >= rect.Size.Y;
            for (var index = 1; index < 4; index++)
            {
                if (horizontal)
                {
                    var x = rect.Position.X + rect.Size.X * index / 4.0f;
                    DrawLine(new Vector2(x, rect.Position.Y + 7.0f), new Vector2(x, rect.End.Y - 7.0f), new Color("#2f3540", 0.7f), 1.0f);
                }
                else
                {
                    var y = rect.Position.Y + rect.Size.Y * index / 4.0f;
                    DrawLine(new Vector2(rect.Position.X + 7.0f, y), new Vector2(rect.End.X - 7.0f, y), new Color("#2f3540", 0.7f), 1.0f);
                }
            }
        }
        if (module.Kind == "reactor")
        {
            var diamond = new[]
            {
                center + Vector2.Up * Math.Min(rect.Size.X, rect.Size.Y) * 0.28f,
                center + Vector2.Right * Math.Min(rect.Size.X, rect.Size.Y) * 0.28f,
                center + Vector2.Down * Math.Min(rect.Size.X, rect.Size.Y) * 0.28f,
                center + Vector2.Left * Math.Min(rect.Size.X, rect.Size.Y) * 0.28f,
            };
            DrawPolyline(new[] { diamond[0], diamond[1], diamond[2], diamond[3], diamond[0] }, new Color("#8a6d2f"), 1.5f);
        }
        if (module.Kind is "laser" or "cannon")
        {
            var direction = Vector2.Right.Rotated(-Mathf.Pi * 0.5f + module.Rotation * Mathf.Pi * 0.5f);
            DrawLine(center, center + direction * Math.Min(rect.Size.X, rect.Size.Y) * 0.46f, new Color("#8a6d2f"), 2.0f);
        }
    }

    private void DrawPanel(Rect2 rect, Color color)
    {
        DrawStyleBox(UiTheme.Box(color, UiTheme.Border, 1, 0), rect);
    }


    private void LabelText(string text, Vector2 position, int size, Color color)
    {
        DrawString(_font, position, text, HorizontalAlignment.Left, -1.0f, size, color);
    }
}
