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
    }

    private static readonly Rect2 GridRect = new(250.0f, 126.0f, 690.0f, 514.0f);
    private static readonly Vector2 GridOrigin = new(595.0f, 382.0f);
    private const float CellSize = 52.0f;
    private static readonly string[] FactoryModuleOrder = { "solar", "miner", "refinery", "battery", "cargo", "reactor" };
    private static readonly string[] ShipModuleOrder = { "hull", "thruster", "radiator", "cargo", "laser", "cannon", "shield", "reactor" };

    private WorldState _world = null!;
    private SpaceTest _spaceTest = null!;
    private string _mode = "builder";
    private BuildContext _buildContext = BuildContext.Factory;
    private string _selectedKind = "miner";
    private int _buildRotation;
    private Vector2I? _selectedGrid;
    private Vector2I? _draggingOrigin;
    private Vector2I _draggingGrabOffset;
    private Vector2I _hoverGrid;
    private bool _hoverValid;
    private Font _font = null!;
    private BuilderUi _builderUi = null!;
    private SpaceHud _spaceHud = null!;

    private ModuleGraph ActiveGraph => _buildContext == BuildContext.Factory ? _world.FactoryGraph : _world.ShipGraph;
    private IReadOnlyList<string> ActiveModuleOrder => _buildContext == BuildContext.Factory ? FactoryModuleOrder : ShipModuleOrder;

    // Read-only screen data exposed to BuilderUi. The simulation remains the single
    // source of truth; the Control tree only presents it and emits user intent.
    public WorldState World => _world;
    public ModuleGraph ActiveGraphForUi => ActiveGraph;
    public IReadOnlyList<ModuleLink> ActiveLinks => ActiveGraph.Links;
    public ModuleMetrics ActiveMetrics => ActiveGraph.GetMetrics();
    public bool IsShipContext => _buildContext == BuildContext.Ship;
    public string SelectedKind => _selectedKind;
    public int BuildRotation => _buildRotation;

    public override void _Ready()
    {
        _font = CreateUiFont();
        _world = new WorldState();
        _world.Changed += OnWorldChanged;

        _spaceTest = new SpaceTest();
        AddChild(_spaceTest);
        _spaceTest.Setup(_world);

        _builderUi = new BuilderUi();
        AddChild(_builderUi);
        _builderUi.Setup(this);
        _spaceHud = new SpaceHud();
        AddChild(_spaceHud);
        _spaceHud.Setup(_spaceTest);

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
        if (_mode == "builder")
            _world.Tick(Math.Min(delta, 0.25) * _world.TimeScale);
        else
            _spaceTest.Tick(Math.Min(delta, 0.05) * _world.TimeScale);

        QueueRedraw();
        if (_mode == "builder")
            _builderUi?.Refresh();
        else
            _spaceHud?.Refresh();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
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

        if (@event is InputEventMouseMotion motion)
        {
            _hoverValid = GridRect.HasPoint(motion.Position);
            if (_hoverValid)
                _hoverGrid = ScreenToGrid(motion.Position);
            QueueRedraw();
            return;
        }

        if (@event is InputEventMouseButton mouseButton)
        {
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
                SwitchBuildContext(_buildContext == BuildContext.Factory ? BuildContext.Ship : BuildContext.Factory);
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
                _selectedGrid = null;
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

    public override void _Draw()
    {
        if (_mode == "space")
            return;

        DrawRect(new Rect2(Vector2.Zero, new Vector2(1280.0f, 720.0f)), UiTheme.BackgroundDeep);
        DrawStars();
        DrawFactoryPanel();
        // Header, palette, status cards, actions and footer are ordinary Godot
        // Controls owned by BuilderUi. Only the simulation canvas is drawn here.
    }



    private void DrawFactoryPanel()
    {
        DrawPanel(new Rect2(250.0f, 74.0f, 690.0f, 38.0f), new Color("#111922"));
        LabelText(_buildContext == BuildContext.Factory ? "行星工厂  /  赤道节点-01" : "舰船设计台  /  种子蓝图-01", new Vector2(266.0f, 99.0f), 13, new Color("#d9e2ec"));
        LabelText("结构网格  13 × 10", new Vector2(760.0f, 99.0f), 10, new Color("#718095"));
        DrawPanel(GridRect, new Color("#0d141c"));
        DrawGrid();
    }

    private void DrawStars()
    {
        for (var i = 0; i < 42; i++)
        {
            var x = (i * 97 + 41) % 1280;
            var y = (i * 53 + 71) % 720;
            var size = 1.0f + i % 2;
            DrawCircle(new Vector2(x, y), size, i % 3 == 0 ? new Color("#24384a") : new Color("#182836"));
        }
        DrawCircle(new Vector2(610.0f, 370.0f), 240.0f, new Color(0.05f, 0.14f, 0.20f, 0.08f));
    }

    private void DrawGrid()
    {
        for (var x = 0; x <= 13; x++)
        {
            var from = new Vector2(GridRect.Position.X + x * CellSize, GridRect.Position.Y);
            DrawLine(from, new Vector2(from.X, GridRect.End.Y), new Color("#172634"), 1.0f);
        }

        for (var y = 0; y <= 10; y++)
        {
            var from = new Vector2(GridRect.Position.X, GridRect.Position.Y + y * CellSize);
            DrawLine(from, new Vector2(GridRect.End.X, from.Y), new Color("#172634"), 1.0f);
        }

        LabelText("北", new Vector2(260.0f, 145.0f), 9, new Color("#526579"));
        LabelText(_buildContext == BuildContext.Factory ? "资源生产层 · 点击空格部署设施" : "舰体结构层 · 点击空格部署模块", new Vector2(650.0f, 145.0f), 9, new Color("#526579"));

        foreach (var link in ActiveGraph.Links)
        {
            var first = ActiveGraph.Modules.FirstOrDefault(module => module.Id == link.FirstId);
            var second = ActiveGraph.Modules.FirstOrDefault(module => module.Id == link.SecondId);
            if (first is null || second is null)
                continue;
            DrawLine(ModuleCenter(first), ModuleCenter(second), new Color("#5d7890"), 5.0f);
            DrawLine(ModuleCenter(first), ModuleCenter(second), new Color("#1b2d3d"), 2.0f);
        }

        foreach (var module in ActiveGraph.Modules)
        {
            var definition = ModuleGraph.Definitions[module.Kind];
            var center = ModuleCenter(module);
            var rect = ModuleRect(module);
            DrawModuleVisual(module, definition, rect, center);
            DrawCircle(center, 5.0f, new Color("#0d141c", 0.60f));
            DrawRotationIndicator(center, module.Rotation, new Color("#f4f1de"));
            if (_selectedGrid.HasValue && ActiveGraph.GetModuleAt(_selectedGrid.Value)?.Id == module.Id)
                DrawRect(new Rect2(rect.Position - new Vector2(3.0f, 3.0f), rect.Size + new Vector2(6.0f, 6.0f)), new Color("#8de1c0"), false, 2.0f);
            LabelText(definition.Label, new Vector2(rect.Position.X, rect.End.Y + 12.0f), 8, new Color("#aebdca"));
        }

        if (_hoverValid)
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
            DrawRect(hoverRect, canPlace ? new Color(new Color("#8de1c0"), 0.20f) : new Color(new Color("#f07c8e"), 0.18f), false, 2.0f);
            LabelText(_draggingOrigin.HasValue ? (canPlace ? "可移动" : "已占用") : (canPlace ? "可部署" : "需连接 / 已占用"), hoverRect.Position + new Vector2(-8.0f, -8.0f), 9, canPlace ? new Color("#8de1c0") : new Color("#f07c8e"));
            if (canPlace)
            {
                var previewDefinition = ModuleGraph.Definitions[previewKind];
                DrawRect(hoverRect, new Color(previewDefinition.ColorHex, 0.20f));
                DrawRotationIndicator(previewCenter, previewRotation, new Color("#8de1c0"));
            }
        }
    }











    private void SwitchBuildContext(BuildContext context)
    {
        if (_buildContext == context)
            return;
        _buildContext = context;
        var moduleOrder = ActiveModuleOrder;
        if (!moduleOrder.Contains(_selectedKind))
            _selectedKind = moduleOrder[0];
        _selectedGrid = null;
        _draggingOrigin = null;
        _draggingGrabOffset = Vector2I.Zero;
        _world.Log(context == BuildContext.Factory ? "切换到行星工厂：建造会消耗资源并推进生产。" : "切换到舰船设计：编辑蓝图不消耗工厂库存。");
        QueueRedraw();
    }

    public void SelectBuildContext(bool ship) => SwitchBuildContext(ship ? BuildContext.Ship : BuildContext.Factory);

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
        if (IsShipContext)
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
        if (_builderUi is not null)
            _builderUi.Visible = true;
        if (_spaceHud is not null)
            _spaceHud.Visible = false;
        QueueRedraw();
        _builderUi?.Refresh();
    }

    private static Vector2 GridToScreen(Vector2I gridPosition) => GridOrigin + new Vector2(gridPosition.X, gridPosition.Y) * CellSize;

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

    private static Vector2I ScreenToGrid(Vector2 screenPosition) => new(
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
        var color = new Color(definition.ColorHex);
        if (module.Kind == "shield")
        {
            var radius = Math.Min(rect.Size.X, rect.Size.Y) * 0.42f;
            DrawCircle(center, radius, color);
            DrawCircle(center, radius, new Color("#dce7ef"), false, 1.0f);
            return;
        }

        if (module.Kind == "thruster")
        {
            var direction = Vector2.Right.Rotated(-Mathf.Pi * 0.5f + module.Rotation * Mathf.Pi * 0.5f);
            var nose = center + direction * Math.Min(rect.Size.X, rect.Size.Y) * 0.42f;
            var tail = center - direction * Math.Min(rect.Size.X, rect.Size.Y) * 0.42f;
            var side = direction.Rotated(Mathf.Pi * 0.5f) * Math.Min(rect.Size.X, rect.Size.Y) * 0.34f;
            DrawColoredPolygon(new[] { nose, tail + side, tail - side }, color);
            DrawPolyline(new[] { nose, tail + side, tail - side, nose }, new Color("#dce7ef"), 1.0f);
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
            DrawPolyline(polygon.Append(polygon[0]).ToArray(), new Color("#dce7ef"), 1.0f);
            DrawLine(rect.Position + new Vector2(rect.Size.X * 0.25f, rect.Size.Y * 0.5f), rect.Position + new Vector2(rect.Size.X * 0.75f, rect.Size.Y * 0.5f), new Color("#dce7ef", 0.55f), 1.0f);
            return;
        }

        DrawRect(rect, color);
        DrawRect(rect, new Color("#dce7ef"), false, 1.0f);
        if (module.Kind == "solar")
        {
            for (var index = 1; index < 4; index++)
            {
                var x = rect.Position.X + rect.Size.X * index / 4.0f;
                DrawLine(new Vector2(x, rect.Position.Y + 4.0f), new Vector2(x, rect.End.Y - 4.0f), new Color("#fff1b5", 0.75f), 1.0f);
            }
            DrawLine(new Vector2(rect.Position.X + 4.0f, center.Y), new Vector2(rect.End.X - 4.0f, center.Y), new Color("#fff1b5", 0.75f), 1.0f);
        }
        else if (module.Kind == "miner")
        {
            var direction = Vector2.Right.Rotated(-Mathf.Pi * 0.5f + module.Rotation * Mathf.Pi * 0.5f);
            DrawLine(center - direction * Math.Min(rect.Size.X, rect.Size.Y) * 0.12f, center + direction * Math.Min(rect.Size.X, rect.Size.Y) * 0.42f, new Color("#dce7ef"), 2.0f);
            DrawCircle(center + direction * Math.Min(rect.Size.X, rect.Size.Y) * 0.42f, 4.0f, new Color("#dce7ef"));
        }
        else if (module.Kind == "refinery")
        {
            var radius = Math.Min(rect.Size.X, rect.Size.Y) * 0.24f;
            DrawCircle(center, radius, new Color("#fff1b5", 0.35f));
            DrawCircle(center, radius, new Color("#fff1b5"), false, 1.5f);
            DrawLine(center + Vector2.Left * radius, center + Vector2.Right * radius, new Color("#fff1b5", 0.8f), 1.0f);
        }
        else if (module.Kind == "battery")
        {
            var horizontal = rect.Size.X >= rect.Size.Y;
            var start = horizontal ? new Vector2(rect.Position.X + 8.0f, center.Y) : new Vector2(center.X, rect.Position.Y + 8.0f);
            var end = horizontal ? new Vector2(rect.End.X - 8.0f, center.Y) : new Vector2(center.X, rect.End.Y - 8.0f);
            DrawLine(start, end, new Color("#dce7ef"), 3.0f);
            DrawLine(start + (horizontal ? Vector2.Up * 5.0f : Vector2.Left * 5.0f), end + (horizontal ? Vector2.Up * 5.0f : Vector2.Left * 5.0f), new Color("#dce7ef", 0.45f), 1.0f);
        }
        else if (module.Kind == "cargo")
        {
            var inner = new Rect2(rect.Position + new Vector2(8.0f, 8.0f), rect.Size - new Vector2(16.0f, 16.0f));
            DrawRect(inner, new Color("#fff1b5", 0.16f));
            DrawRect(inner, new Color("#fff1b5", 0.75f), false, 1.0f);
            DrawLine(inner.Position, inner.End, new Color("#fff1b5", 0.65f), 1.0f);
            DrawLine(new Vector2(inner.End.X, inner.Position.Y), new Vector2(inner.Position.X, inner.End.Y), new Color("#fff1b5", 0.65f), 1.0f);
        }
        else if (module.Kind == "radiator")
        {
            var horizontal = rect.Size.X >= rect.Size.Y;
            for (var index = 1; index < 4; index++)
            {
                if (horizontal)
                {
                    var x = rect.Position.X + rect.Size.X * index / 4.0f;
                    DrawLine(new Vector2(x, rect.Position.Y + 7.0f), new Vector2(x, rect.End.Y - 7.0f), new Color("#dce7ef", 0.78f), 1.0f);
                }
                else
                {
                    var y = rect.Position.Y + rect.Size.Y * index / 4.0f;
                    DrawLine(new Vector2(rect.Position.X + 7.0f, y), new Vector2(rect.End.X - 7.0f, y), new Color("#dce7ef", 0.78f), 1.0f);
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
            DrawPolyline(new[] { diamond[0], diamond[1], diamond[2], diamond[3], diamond[0] }, new Color("#fff1b5"), 1.5f);
        }
        if (module.Kind is "laser" or "cannon")
        {
            var direction = Vector2.Right.Rotated(-Mathf.Pi * 0.5f + module.Rotation * Mathf.Pi * 0.5f);
            DrawLine(center, center + direction * Math.Min(rect.Size.X, rect.Size.Y) * 0.46f, new Color("#fff1b5"), 2.0f);
        }
    }

    private void DrawPanel(Rect2 rect, Color color)
    {
        DrawRect(rect, color);
        DrawRect(rect, new Color("#253746"), false, 1.0f);
    }


    private void LabelText(string text, Vector2 position, int size, Color color)
    {
        DrawString(_font, position, text, HorizontalAlignment.Left, -1.0f, size, color);
    }
}
