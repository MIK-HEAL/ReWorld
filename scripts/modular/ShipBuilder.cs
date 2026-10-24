using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Reassembly 式飞船建造页面。
///
/// 对齐逆向的 ConstructEditor 行为：
///   - 从形状调色板选择方块，鼠标移动时实时预览。
///   - 靠近已有方块的端口时，自动吸附（端口位置重合 + 法线对齐），预览变绿。
///   - 可任意角度旋转（R 逆时针 / 方向键微调），不是 90° 步进。
///   - 缩放视角（滚轮），平移（中键拖动）。
///   - 左键放置/拖动、右键旋转、Delete 删除、滚轮镜头缩放。
///
/// 这是一个独立的整屏 Node2D，挂在 Main 下作为「模块化造舰」模式。
/// 它操作的是宇宙航行共享的 ModularShip（任意角度多边形舰体）。
/// </summary>
public partial class ShipBuilder : Node2D
{
    private const float ViewWidth = 1280.0f;
    private const float ViewHeight = 720.0f;

    // Reassembly-inspired visual language: cold steel, restrained cyan telemetry,
    // and amber only for selection / warnings. The playfield stays nearly black so
    // the ship silhouette owns the screen.
    private static readonly Color SpaceBlack = new("#121c26");
    private static readonly Color SpaceBlue = new("#182736");
    private static readonly Color GridMinor = new("#253a4d", 0.6f);
    private static readonly Color GridMajor = new("#3d5e78", 0.75f);
    private static readonly Color PanelFill = new("#1a2c3cd9");
    private static readonly Color PanelEdge = new("#3d627a");
    private static readonly Color Telemetry = new("#48d6c8");
    private static readonly Color MutedText = new("#829cad");
    private static readonly Color SteelText = new("#c2d5e0");
    private static readonly Color Amber = new("#e1a71c");

    private readonly ModularShip _ship = new();
    private string _selectedShapeId = "hull";
    private float _previewAngle;
    private Vector2 _cameraPosition = Vector2.Zero;
    private float _cameraZoom = 1.0f;
    private bool _cameraDragging;
    private Vector2 _mouseWorld;
    private Vector2 _pointerScreen;
    private Vector2? _palettePress;

    private ModularShip.Block? _draggingBlock;
    private Vector2 _dragGrabOffset;
    private ModularShip? _dragBefore;
    private ModularShip.Block? _selection;
    private readonly Stack<ModularShip> _undo = new();
    private readonly Stack<ModularShip> _redo = new();

    /// <summary>对称建造模式（Reassembly 标志性功能，按 X 切换）：沿过核心的纵轴镜像放置/删除。默认关闭，按需开启。</summary>
    private bool _mirrorMode = false;

    private (Vector2 Position, float Angle, bool Valid)? _snapPreview;
    /// <summary>当前吸附选定的目标端口世界位置（预览高亮用，无吸附时为 null）。</summary>
    private Vector2? _snapTargetPortWorld;
    private ModularShip.Block? _snapTargetBlock;
    private Font _font = null!;

    /// <summary>调色板按钮布局缓存（_Input 点击检测用）。每项 = (矩形, shapeId)。</summary>
    private readonly List<(Rect2 Rect, string ShapeId)> _paletteButtons = new();
    private const float PaletteX = ViewWidth - PaletteWidth - 8.0f;
    private const float PaletteWidth = 300.0f;
    private const float PaletteItemWidth = 132.0f;
    private const float PaletteItemHeight = 32.0f;

    public ModularShip Ship => _ship;
    public string SelectedShapeId => _selectedShapeId;

    public void SelectShape(string shapeId)
    {
        if (!ShipShapeLibrary.Shapes.ContainsKey(shapeId)) return;
        _selectedShapeId = shapeId;
        _selection = null;
    }

    /// <summary>把镜头居中到舰体（进入建造页时调用）。</summary>
    public void FocusShip()
    {
        if (_dragBefore is not null) _ship.Restore(_dragBefore);
        _draggingBlock = null;
        _dragBefore = null;
        _selection = null;
        _dragGrabOffset = Vector2.Zero;
        _palettePress = null;
        _cameraDragging = false;
        _snapPreview = null;
        _snapTargetPortWorld = null;
        _snapTargetBlock = null;
        if (_ship.Blocks.Count > 0)
            _cameraPosition = _ship.CenterOfMass();
        QueueRedraw();
    }

    public override void _Ready()
    {
        _font = CreateUiFont();
        // 独立打开编辑器时补上参考舰；主场景已提前初始化共享舰体。
        if (_ship.Blocks.Count == 0)
            _ship.SeedReferenceShip();
        _cameraPosition = _ship.CenterOfMass();
    }

    public override void _Process(double delta)
    {
        UpdateHover();
        QueueRedraw();
    }

    private void UpdateHover()
    {
        _mouseWorld = ScreenToWorld(_pointerScreen);
        _snapTargetPortWorld = null;
        _snapTargetBlock = null;
        if (_draggingBlock is not null)
        {
            _snapPreview = ComputeSnap(_draggingBlock.ShapeId, _draggingBlock.Angle,
                _mouseWorld + _dragGrabOffset, _draggingBlock);
            if (_snapPreview is { Valid: true } snap)
            {
                _draggingBlock.Position = snap.Position;
                _draggingBlock.Angle = snap.Angle;
            }
            else _draggingBlock.Position = _mouseWorld + _dragGrabOffset;
        }
        else if (_pointerScreen.X < PaletteX)
            _snapPreview = ComputeSnap(_selectedShapeId, _previewAngle, _mouseWorld);
        else
            _snapPreview = null;
    }

    /// <summary>
    /// 计算方块在指定位置/角度的吸附结果。对齐逆向 connectPorts 与 Reassembly 编辑器手感：
    ///   - 阶段一：按光标距离选定**目标端口**（吸附点稳定，不因旋转跳到别的端口）。
    ///   - 阶段二：在选定的目标端口上，按「snapAngle 与玩家旋转 _previewAngle 角度差最小」选自身端口对接
    ///     —— 旋转键 R/Q/滚轮 在该吸附点切换对接朝向，旋转才真正有效。
    ///   - 端口位置重合即连接（一个端口只能连一个方块，IsPortOccupied 已过滤）。
    /// </summary>
    private (Vector2 Position, float Angle, bool Valid)? ComputeSnap(string shapeId, float angle, Vector2 worldPos,
        ModularShip.Block? ignore = null)
    {
        if (!ShipShapeLibrary.Shapes.TryGetValue(shapeId, out var spec))
            return null;

        // 无核心时，核心方块可直接放在光标处。
        if (shapeId == "core" && _ship.Core is null)
            return (worldPos, angle, true);
        if (shapeId == "core" && ignore is null) return null;

        var snapThreshold = 28.0f / _cameraZoom; // 保持屏幕像素内吸附距离稳定。

        // 阶段一：找离光标最近的目标端口（用每个自身端口候选中最小的光标距离代表该目标端口）。
        ModularShip.Block? bestTarget = null;
        ShipShapeLibrary.Port bestTargetPort = default;
        var bestTargetDist = snapThreshold;

        foreach (var target in _ship.Blocks.Where(block => block.Enabled && block != ignore))
        {
            for (var tpi = 0; tpi < target.Spec.Ports.Count; tpi++)
            {
                var targetPort = target.Spec.Ports[tpi];
                if (IsPortOccupied(target, tpi))
                    continue;
                if (!spec.Ports.Any(port => port.CanConnect(targetPort)))
                    continue;

                // 该目标端口上，取所有自身端口候选里离光标最近的距离作为该目标端口的代表距离。
                var portMinDist = float.MaxValue;
                foreach (var selfPort in spec.Ports)
                {
                    if (!selfPort.CanConnect(targetPort)) continue;
                    var (snapPos, snapAngle) = ModularShip.SnapBlock(
                        selfPort, targetPort, target.Position, target.Angle);
                    if (!_ship.CanPlace(shapeId, snapPos, snapAngle, ignore)) continue;
                    if (ignore is not null && Mathf.Abs(Mathf.Wrap(snapAngle - angle, -Mathf.Pi, Mathf.Pi)) > 0.05f) continue;
                    var d = snapPos.DistanceTo(worldPos);
                    if (d < portMinDist)
                        portMinDist = d;
                }

                if (portMinDist < bestTargetDist)
                {
                    bestTargetDist = portMinDist;
                    bestTarget = target;
                    bestTargetPort = targetPort;
                }
            }
        }

        if (bestTarget is null)
            return null;

        // 阶段二：在选定目标端口上，按角度差选自身端口（旋转切换对接朝向）。
        var bestAng = 0.0f;
        var bestPos = Vector2.Zero;
        var bestAngleDiff = float.MaxValue;
        foreach (var selfPort in spec.Ports)
        {
            if (!selfPort.CanConnect(bestTargetPort)) continue;
            var (snapPos, snapAngle) = ModularShip.SnapBlock(
                selfPort, bestTargetPort, bestTarget.Position, bestTarget.Angle);
            if (!_ship.CanPlace(shapeId, snapPos, snapAngle, ignore)) continue;
            var angleDiff = Mathf.Abs(Mathf.Wrap(snapAngle - angle, -Mathf.Pi, Mathf.Pi));
            if (ignore is not null && angleDiff > 0.05f) continue;
            if (angleDiff < bestAngleDiff)
            {
                bestAngleDiff = angleDiff;
                bestPos = snapPos;
                bestAng = snapAngle;
            }
        }

        // 记录吸附目标端口供预览高亮。
        _snapTargetBlock = bestTarget;
        _snapTargetPortWorld = bestTarget.Position + bestTargetPort.LocalPosition.Rotated(bestTarget.Angle);
        return bestAngleDiff < float.MaxValue ? (bestPos, bestAng, true) : null;
    }

    /// <summary>位置关于过 axisPoint 的纵轴（竖直直线）的镜像。</summary>
    private static Vector2 MirrorAcrossAxis(Vector2 pos, Vector2 axisPoint) => new(2.0f * axisPoint.X - pos.X, pos.Y);

    /// <summary>角度关于纵轴的镜像（向量角 θ → π − θ）。</summary>
    private static float MirrorAngleAcrossAxis(float angle) => Mathf.Pi - angle;

    /// <summary>对称轴渲染：过核心的竖直虚线（对称模式开启时）。</summary>
    private void DrawMirrorAxis()
    {
        if (!_mirrorMode || _ship.Core is not { } core)
            return;
        var halfView = new Vector2(ViewWidth, ViewHeight) * 0.5f / _cameraZoom;
        var top = _cameraPosition.Y - halfView.Y;
        var bottom = _cameraPosition.Y + halfView.Y;
        var color = new Color("#7bb7e8", 0.35f);
        var dash = 10.0f / _cameraZoom;
        for (var y = top; y < bottom; y += dash * 2.0f)
            DrawLine(new Vector2(core.Position.X, y), new Vector2(core.Position.X, Mathf.Min(y + dash, bottom)), color, 1.2f / _cameraZoom);
    }

    /// <summary>目标方块的指定端口是否已被连接（对齐逆向 Port.Connected 显式状态，不再距离推断）。</summary>
    private bool IsPortOccupied(ModularShip.Block target, int portIndex)
    {
        var port = target.Spec.Ports[portIndex];
        if (!port.CanAttach) return true;
        var world = port.WorldPosition(target.Position, target.Angle);
        return _ship.Blocks.Any(other => other != target && other != _draggingBlock && other.Enabled &&
            other.Spec.Ports.Any(own => own.CanConnect(port) &&
                own.WorldPosition(other.Position, other.Angle).DistanceTo(world) < 0.1f &&
                own.WorldNormal(other.Angle).Dot(port.WorldNormal(target.Angle)) < -0.999f));
    }

    private void SaveUndo()
    {
        _undo.Push(_ship.Clone());
        _redo.Clear();
    }

    private void RotateSelection(float angle)
    {
        _previewAngle = Mathf.Wrap(_previewAngle + angle, -Mathf.Pi, Mathf.Pi);
        if (_draggingBlock is not null)
        {
            _draggingBlock.Angle = Mathf.Wrap(_draggingBlock.Angle + angle, -Mathf.Pi, Mathf.Pi);
            UpdateHover();
        }
    }

    private void ScaleEngine(int direction)
    {
        var sizes = new[] { "thruster", "thruster_medium", "thruster_large" };
        var index = Array.IndexOf(sizes, _selectedShapeId);
        if (index >= 0) _selectedShapeId = sizes[Math.Clamp(index + direction, 0, sizes.Length - 1)];
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouse)
        {
            _pointerScreen = mouse.Position;
            if (mouse.ButtonIndex == MouseButton.Middle)
            {
                _cameraDragging = mouse.Pressed;
                return;
            }
            if (mouse.Pressed && mouse.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                var sign = mouse.ButtonIndex == MouseButton.WheelUp ? 1 : -1;
                if (mouse.AltPressed) ScaleEngine(sign);
                else
                {
                    var anchor = ScreenToWorld(mouse.Position);
                    _cameraZoom = Mathf.Clamp(_cameraZoom * Mathf.Pow(1.12f, sign), 0.3f, 4.0f);
                    _cameraPosition = anchor - (mouse.Position - new Vector2(ViewWidth, ViewHeight) * 0.5f) / _cameraZoom;
                }
                UpdateHover();
                return;
            }
            if (!mouse.Pressed)
            {
                if (mouse.ButtonIndex == MouseButton.Left && _draggingBlock is not null)
                {
                    UpdateHover();
                    if (_snapPreview is not { Valid: true } && _dragBefore is not null)
                    {
                        _ship.Restore(_dragBefore);
                        _selection = null;
                    }
                    else if (_dragBefore is not null)
                    {
                        _undo.Push(_dragBefore);
                        _redo.Clear();
                    }
                    _draggingBlock = null;
                    _dragBefore = null;
                    _dragGrabOffset = Vector2.Zero;
                    _ship.RefreshConnections();
                }
                else if (mouse.ButtonIndex == MouseButton.Left && _palettePress is { } origin)
                {
                    _palettePress = null;
                    if (mouse.Position.X < PaletteX && mouse.Position.DistanceTo(origin) > 4)
                    {
                        UpdateHover();
                        PlacePreview();
                    }
                }
                return;
            }
            if (mouse.ButtonIndex == MouseButton.Right)
            {
                RotateSelection(Mathf.Pi * 0.5f);
                return;
            }
            if (mouse.ButtonIndex != MouseButton.Left) return;
            if (TrySelectShapeFromPalette(mouse.Position))
            {
                _selection = null;
                _palettePress = mouse.Position;
                return;
            }
            if (mouse.Position.X >= PaletteX) return;
            UpdateHover();
            var clicked = BlockAt(ScreenToWorld(mouse.Position));
            if (clicked is not null)
            {
                _selection = clicked;
                if (clicked.Spec.IsCore) return;
                _dragBefore = _ship.Clone();
                if (mouse.AltPressed)
                    clicked = _ship.AddBlock(clicked.ShapeId, clicked.Position, clicked.Angle);
                _selection = _draggingBlock = clicked;
                _dragGrabOffset = clicked.Position - ScreenToWorld(mouse.Position);
                _previewAngle = clicked.Angle;
                return;
            }
            PlacePreview();
            return;
        }
        if (@event is InputEventMouseMotion motion)
        {
            _pointerScreen = motion.Position;
            if (_cameraDragging) _cameraPosition -= motion.Relative / _cameraZoom;
            if (_draggingBlock is not null) UpdateHover();
            return;
        }
        if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;
        if (key.CtrlPressed && (key.Keycode == Key.Z || key.Keycode == Key.Y))
        {
            if (_draggingBlock is not null) return;
            var undo = key.Keycode == Key.Z && !key.ShiftPressed;
            var source = undo ? _undo : _redo;
            var destination = undo ? _redo : _undo;
            if (source.Count > 0)
            {
                destination.Push(_ship.Clone());
                _ship.Restore(source.Pop());
                _selection = null;
            }
        }
        else if (key.Keycode is Key.Delete or Key.Backspace)
        {
            if (_draggingBlock is not null) return;
            var victim = _selection ?? BlockAt(_mouseWorld);
            if (victim is not null && !victim.Spec.IsCore)
            {
                SaveUndo();
                _ship.RemoveBlock(victim);
                _selection = null;
            }
        }
        else if (key.Keycode is Key.E or Key.R) RotateSelection(Mathf.Pi * 0.5f);
        else if (key.Keycode == Key.Q) RotateSelection(-Mathf.Pi * 0.5f);
        else if (key.Keycode == Key.Left) RotateSelection(-Mathf.DegToRad(15));
        else if (key.Keycode == Key.Right) RotateSelection(Mathf.DegToRad(15));
        else if (key.Keycode is Key.Equal or Key.Plus) _cameraZoom = Mathf.Clamp(_cameraZoom * 1.15f, 0.3f, 4);
        else if (key.Keycode == Key.Minus) _cameraZoom = Mathf.Clamp(_cameraZoom / 1.15f, 0.3f, 4);
        else if (key.Keycode == Key.F)
        {
            _cameraPosition = _ship.CenterOfMass();
            _cameraZoom = 1;
        }
        else if (key.Keycode == Key.X) _mirrorMode = !_mirrorMode;
        UpdateHover();
    }

    private void PlacePreview()
    {
        if (_snapPreview is not { Valid: true } snap) return;
        SaveUndo();
        _selection = _ship.AddBlock(_selectedShapeId, snap.Position, snap.Angle);
        if (_mirrorMode && _ship.Core is { } core)
        {
            var mirrorPos = MirrorAcrossAxis(snap.Position, core.Position);
            var mirrorSnap = ComputeSnap(_selectedShapeId, MirrorAngleAcrossAxis(snap.Angle), mirrorPos);
            if (mirrorSnap is { Valid: true } ms && ms.Position.DistanceTo(snap.Position) > 1)
                _ship.AddBlock(_selectedShapeId, ms.Position, ms.Angle);
        }
    }

    /// <summary>检测鼠标点击是否落在调色板按钮上，若是则选中该形状。</summary>
    private bool TrySelectShapeFromPalette(Vector2 screenPos)
    {
        if (screenPos.X < PaletteX || screenPos.X > PaletteX + PaletteWidth)
            return false;
        foreach (var (rect, shapeId) in _paletteButtons)
        {
            if (rect.HasPoint(screenPos))
            {
                SelectShape(shapeId);
                return true;
            }
        }
        return false;
    }

    private ModularShip.Block? BlockAt(Vector2 worldPos)
    {
        // 从后往前（后放置的在上层）。
        for (var i = _ship.Blocks.Count - 1; i >= 0; i--)
        {
            var block = _ship.Blocks[i];
            if (!block.Enabled)
                continue;
            var local = (worldPos - block.Position).Rotated(-block.Angle);
            var vertices = block.Spec.Vertices();
            var inside = false;
            for (var edgeIndex = 0; edgeIndex < vertices.Length; edgeIndex++)
            {
                var j = (edgeIndex + vertices.Length - 1) % vertices.Length;
                var a = vertices[edgeIndex];
                var b = vertices[j];
                if ((a.Y > local.Y) != (b.Y > local.Y) &&
                    local.X < (b.X - a.X) * (local.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            if (inside)
                return block;
        }
        return null;
    }

    private Vector2 ScreenToWorld(Vector2 screen)
    {
        return _cameraPosition + (screen - new Vector2(ViewWidth, ViewHeight) * 0.5f) / _cameraZoom;
    }

    private Vector2 WorldToScreen(Vector2 world)
    {
        return (world - _cameraPosition) * _cameraZoom + new Vector2(ViewWidth, ViewHeight) * 0.5f;
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, new Vector2(ViewWidth, ViewHeight)), SpaceBlack);
        DrawAmbientSpace();

        DrawSetTransform(new Vector2(ViewWidth, ViewHeight) * 0.5f - _cameraPosition * _cameraZoom, 0.0f, new Vector2(_cameraZoom, _cameraZoom));
        DrawGridBackdrop();
        DrawMirrorAxis();
        DrawShip();
        DrawSnapPreview();
        DrawSetTransform(Vector2.Zero, 0.0f, Vector2.One);

        DrawPalette();
        DrawHud();
    }

    private void DrawGridBackdrop()
    {
        var halfView = new Vector2(ViewWidth, ViewHeight) * 0.5f / _cameraZoom;
        var topLeft = _cameraPosition - halfView;
        var bottomRight = _cameraPosition + halfView;
        var step = 80.0f;
        var startX = Mathf.Floor(topLeft.X / step) * step;
        var startY = Mathf.Floor(topLeft.Y / step) * step;
        for (var x = startX; x <= bottomRight.X; x += step)
            DrawLine(new Vector2(x, topLeft.Y), new Vector2(x, bottomRight.Y), Mathf.Abs(x % (step * 5)) < 0.1f ? GridMajor : GridMinor, 1.0f / _cameraZoom);
        for (var y = startY; y <= bottomRight.Y; y += step)
            DrawLine(new Vector2(topLeft.X, y), new Vector2(bottomRight.X, y), Mathf.Abs(y % (step * 5)) < 0.1f ? GridMajor : GridMinor, 1.0f / _cameraZoom);
    }

    private void DrawAmbientSpace()
    {
        // Quiet vignette bands and sparse navigation ticks give the editor a hangar
        // instrument feel without covering the construction area with decoration.
        DrawRect(new Rect2(0, 0, ViewWidth, 88), new Color("#162330", 0.88f));
        DrawRect(new Rect2(0, ViewHeight - 46, ViewWidth, 46), new Color("#162330", 0.92f));
        for (var i = 0; i < 18; i++)
        {
            var x = 24 + i * 71;
            DrawLine(new Vector2(x, 78), new Vector2(x + 18, 78), new Color("#3c6077", 0.65f), 1);
            DrawLine(new Vector2(x, ViewHeight - 46), new Vector2(x + 10, ViewHeight - 46), new Color("#3c6077", 0.55f), 1);
        }
    }

    private void DrawShip()
    {
        // Connections sit behind the hull as thin energy rails.
        foreach (var (a, b) in _ship.GetConnections())
        {
            DrawLine(a.Position, b.Position, new Color("#122c38", 0.9f), 8.0f / _cameraZoom);
            DrawLine(a.Position, b.Position, new Color("#3c8c99", 0.55f), 1.5f / _cameraZoom);
        }

        var components = _ship.GetConnectedComponents();
        var mainComponent = components.FirstOrDefault(component => component.Any(block => block.ShapeId == "core")) ?? new List<ModularShip.Block>();

        foreach (var block in _ship.Blocks)
        {
            var spec = block.Spec;
            var vertices = spec.Vertices()
                .Select(v => block.Position + v.Rotated(block.Angle))
                .ToArray();

            var color = spec.Color.Lerp(new Color("#172431"), 0.32f);
            if (!block.Enabled || !mainComponent.Contains(block))
                color = color.Darkened(0.55f);

            // A dark offset silhouette and an inset face turn the raw polygon into
            // a manufactured module instead of a flat coloured tile.
            var shadow = vertices.Select(v => v + new Vector2(3, 4) / _cameraZoom).ToArray();
            DrawColoredPolygon(shadow, new Color("#010306", 0.78f));
            DrawColoredPolygon(vertices, color);
            var inset = vertices.Select(v => block.Position + (v - block.Position) * 0.78f).ToArray();
            DrawColoredPolygon(inset, color.Lightened(0.12f));
            DrawPolyline(vertices.Append(vertices[0]).ToArray(), new Color("#9bbac1", 0.68f), 1.2f / _cameraZoom);
            DrawPolyline(inset.Append(inset[0]).ToArray(), new Color("#0b1218", 0.92f), 1.0f / _cameraZoom);

            DrawModuleDetail(block, inset);

            // 核心：画个高亮菱形。
            if (spec.IsCore)
            {
                var radius = spec.Radius * 0.4f;
                var diamond = new[]
                {
                    block.Position + Vector2.Up * radius,
                    block.Position + Vector2.Right * radius,
                    block.Position + Vector2.Down * radius,
                    block.Position + Vector2.Left * radius,
                };
                DrawPolyline(diamond.Append(diamond[0]).ToArray(), Amber, 1.8f / _cameraZoom);
                DrawCircle(block.Position, radius * 0.38f, new Color("#8cf5e5", 0.14f));
            }
            else if (spec.IsThruster)
            {
                ModularShipVisual.DrawEngine(this, spec, block.Position, block.Angle);
            }
            else if (spec.IsWeapon)
            {
                // 武器：画炮口方向。
                var dir = Vector2.Right.Rotated(block.Angle);
                DrawLine(block.Position, block.Position + dir * spec.Radius, Amber, 2.0f / _cameraZoom);
            }

            // 未连接到主体的方块标记。
            if (!mainComponent.Contains(block))
            {
                var bbox = block.Position;
                DrawLine(bbox + new Vector2(-8, -8), bbox + new Vector2(8, 8), new Color("#f07c8e", 0.8f), 2.0f);
                DrawLine(bbox + new Vector2(8, -8), bbox + new Vector2(-8, 8), new Color("#f07c8e", 0.8f), 2.0f);
            }
            if (block == _selection)
            {
                DrawPolyline(vertices.Append(vertices[0]).ToArray(), Amber, 2.4f / _cameraZoom);
                DrawCircle(block.Position, spec.Radius * 0.16f, new Color("#f4c878", 0.2f), false, 1.5f / _cameraZoom);
            }
        }

        // 端口标记（悬停吸附时高亮显示目标端口，或恒定显示自由端口）。
        DrawPortMarkers();
    }

    private void DrawModuleDetail(ModularShip.Block block, Vector2[] inset)
    {
        var center = block.Position;
        var spec = block.Spec;
        var axis = Vector2.Right.Rotated(block.Angle);
        var cross = axis.Rotated(Mathf.Pi * 0.5f);
        if (spec.IsCore)
        {
            DrawCircle(center, spec.Radius * 0.47f, new Color("#8ce8df", 0.12f), false, 2.0f / _cameraZoom);
            DrawLine(center - axis * spec.Radius * 0.33f, center + axis * spec.Radius * 0.33f, Telemetry, 1.2f / _cameraZoom);
            DrawLine(center - cross * spec.Radius * 0.33f, center + cross * spec.Radius * 0.33f, Telemetry, 1.2f / _cameraZoom);
        }
        else if (spec.IsThruster)
        {
            var exhaust = center + spec.ExhaustPosition.Rotated(block.Angle);
            DrawLine(exhaust - cross * spec.ExhaustHalfWidth, exhaust + cross * spec.ExhaustHalfWidth, new Color("#a5eff2", 0.8f), 1.5f / _cameraZoom);
            DrawLine(center - axis * spec.Radius * 0.28f, center + axis * spec.Radius * 0.28f, new Color("#8cc5cc", 0.7f), 1.0f / _cameraZoom);
        }
        else if (spec.IsWeapon)
        {
            var muzzle = center + spec.WeaponDirection.Rotated(block.Angle) * spec.Radius * 0.45f;
            DrawCircle(muzzle, Math.Max(2.0f, spec.Radius * 0.1f), Amber, false, 1.4f / _cameraZoom);
            DrawLine(center, muzzle, new Color("#f4d48a", 0.55f), 1.0f / _cameraZoom);
        }
        else if ((spec.Features & BlockFeatures.Generator) != 0)
        {
            DrawCircle(center, spec.Radius * 0.28f, new Color("#ffd279", 0.18f));
            DrawArc(center, spec.Radius * 0.4f, -Mathf.Pi * 0.7f, Mathf.Pi * 0.55f, 12, Amber, 1.6f / _cameraZoom);
        }
        else if (spec.HasCircleShape || (spec.Features & BlockFeatures.Shield) != 0)
        {
            DrawArc(center, spec.Radius * 0.38f, 0.2f, Mathf.Pi * 1.8f, 18, Telemetry, 1.3f / _cameraZoom);
        }
        else
        {
            // Small hard-surface seams make repeated hull pieces read as a system.
            DrawLine(center - axis * spec.Radius * 0.24f, center + axis * spec.Radius * 0.24f, new Color("#b0cbd0", 0.35f), 1.0f / _cameraZoom);
            DrawCircle(center + cross * spec.Radius * 0.22f, 1.6f / _cameraZoom, new Color("#b0cbd0", 0.7f));
        }
    }

    private void DrawPortMarkers()
    {
        var selectedSpec = _draggingBlock?.Spec ?? ShipShapeLibrary.Get(_selectedShapeId);
        foreach (var block in _ship.Blocks)
        {
            if (!block.Enabled)
                continue;
            for (var pi = 0; pi < block.Spec.Ports.Count; pi++)
            {
                var port = block.Spec.Ports[pi];
                if (!selectedSpec.Ports.Any(own => own.CanConnect(port))) continue;
                var world = block.Position + port.LocalPosition.Rotated(block.Angle);
                var occupied = IsPortOccupied(block, pi);
                DrawCircle(world, 3.0f, occupied ? new Color("#f07c8e", 0.5f) : new Color("#7bb7e8", 0.6f));
            }
        }
    }

    private void DrawSnapPreview()
    {
        if (_draggingBlock is not null)
            return;

        if (_snapTargetPortWorld.HasValue)
        {
            var p = _snapTargetPortWorld.Value;
            DrawCircle(p, 4.0f, ReassemblyUi.PhosphorGreen);
            DrawArc(p, 8.0f, 0, Mathf.Tau, 24, ReassemblyUi.PhosphorGreen * 0.85f, 1.5f);
            DrawArc(p, 14.0f, 0, Mathf.Tau, 32, ReassemblyUi.PhosphorGreen * 0.4f, 1.0f);
        }

        if (_draggingBlock is not null || _snapPreview is not { } snap || !ShipShapeLibrary.Shapes.TryGetValue(_selectedShapeId, out var spec))
        {
            // 无吸附时，显示自由预览（跟随光标）。
            DrawPreviewAt(_mouseWorld, _previewAngle, false);
            return;
        }

        DrawPreviewAt(snap.Position, snap.Angle, snap.Valid);

        // 对称模式：画镜像位置的幻影预览（对齐 Reassembly 的对称建造反馈）。
        if (_mirrorMode && snap.Valid && _ship.Core is { } core)
        {
            var mirrorPos = MirrorAcrossAxis(snap.Position, core.Position);
            if (mirrorPos.DistanceTo(snap.Position) > 1.0f)
            {
                var mirrorSnap = ComputeSnap(_selectedShapeId, MirrorAngleAcrossAxis(snap.Angle), mirrorPos);
                if (mirrorSnap is { Valid: true } ms)
                    DrawGhostPreviewAt(ms.Position, ms.Angle);
            }
        }
    }

    private void DrawGhostPreviewAt(Vector2 position, float angle)
    {
        if (!ShipShapeLibrary.Shapes.TryGetValue(_selectedShapeId, out var spec))
            return;
        var vertices = spec.Vertices()
            .Select(v => position + v.Rotated(angle))
            .ToArray();
        DrawPolyline(vertices.Append(vertices[0]).ToArray(), new Color("#7bb7e8", 0.55f), 1.2f);
    }

    private void DrawPreviewAt(Vector2 position, float angle, bool valid)
    {
        if (!ShipShapeLibrary.Shapes.TryGetValue(_selectedShapeId, out var spec))
            return;
        var vertices = spec.Vertices()
            .Select(v => position + v.Rotated(angle))
            .ToArray();
        var fill = valid ? new Color("#65c18c", 0.28f) : new Color("#f07c8e", 0.22f);
        var line = valid ? new Color("#65c18c") : new Color("#f07c8e");
        DrawColoredPolygon(vertices, fill);
        DrawPolyline(vertices.Append(vertices[0]).ToArray(), line, 1.5f);
        if (spec.IsThruster) ModularShipVisual.DrawEngine(this, spec, position, angle);

        // 朝向指示。
        var dir = Vector2.Right.Rotated(angle);
        DrawLine(position, position + dir * spec.Radius, line, 2.0f);
    }

    private void DrawPalette()
    {
        var panelRect = new Rect2(PaletteX, 92, PaletteWidth, ViewHeight - 108);
        ReassemblyUi.DrawChamferFrame(this, panelRect, ReassemblyUi.VoidDark, ReassemblyUi.CyanMuted * 0.7f, 10.0f, true, 1.2f);
        DrawString(_font, new Vector2(PaletteX + 16, 120), "COMPONENTS", HorizontalAlignment.Left, -1, 15, ReassemblyUi.TextWhite);
        DrawString(_font, new Vector2(PaletteX + PaletteWidth - 96, 120), "REASSEMBLY // 03", HorizontalAlignment.Left, -1, 10, ReassemblyUi.CyanHighlight);
        DrawLine(new Vector2(PaletteX + 14, 130), new Vector2(PaletteX + PaletteWidth - 14, 130), ReassemblyUi.CyanGrid, 1.0f);
        DrawString(_font, new Vector2(PaletteX + 16, 146), "SELECT BLOCK // CONNECT VIA PORTS", HorizontalAlignment.Left, -1, 10, ReassemblyUi.TextMuted);

        _paletteButtons.Clear();
        var y = 158.0f;
        foreach (var group in ShipShapeLibrary.BuildOrder.GroupBy(id => ShipShapeLibrary.Get(id).Category))
        {
            DrawString(_font, new Vector2(PaletteX + 16, y + 12), group.Key.ToUpperInvariant(), HorizontalAlignment.Left, -1, 10, ReassemblyUi.CyanHighlight * 0.85f);
            y += 14.0f;

            var col = 0;
            foreach (var shapeId in group)
            {
                var spec = ShipShapeLibrary.Get(shapeId);
                var x = PaletteX + 12 + col * 140;
                var selected = shapeId == _selectedShapeId;
                var rect = new Rect2(new Vector2(x, y), new Vector2(PaletteItemWidth, 24));
                _paletteButtons.Add((rect, shapeId));

                var bg = selected ? new Color("#0e2532") : new Color("#07121b");
                var border = selected ? ReassemblyUi.CyanHighlight : new Color("#16303d");
                ReassemblyUi.DrawChamferFrame(this, rect, bg, border, 4.0f, false, selected ? 1.5f : 1.0f);

                var previewCenter = new Vector2(x + 14, y + 12.0f);
                var preview = spec.Vertices().Select(v => previewCenter + v * (8.0f / spec.Radius)).ToArray();
                DrawColoredPolygon(preview, spec.Color.Lerp(new Color("#0a141d"), 0.25f));
                DrawPolyline(preview.Append(preview[0]).ToArray(), selected ? ReassemblyUi.CyanHighlight : new Color("#7ca0b0", 0.6f), 1.0f);

                DrawString(_font, new Vector2(x + 28, y + 16), spec.Label, HorizontalAlignment.Left, -1, 10,
                    selected ? ReassemblyUi.TextWhite : ReassemblyUi.TextSteel);

                col++;
                if (col == 2)
                {
                    col = 0;
                    y += 27.0f;
                }
            }
            if (col != 0)
                y += 27.0f;
        }

        var statsY = ViewHeight - 64.0f;
        DrawLine(new Vector2(PaletteX + 12, statsY - 8), new Vector2(PaletteX + PaletteWidth - 12, statsY - 8), ReassemblyUi.CyanGrid, 1.0f);
        DrawString(_font, new Vector2(PaletteX + 14, statsY + 6),
            $"BLOCKS {_ship.Blocks.Count}  MASS {_ship.TotalMass:0}  THRUST {_ship.TotalThrust:0}  DPS {_ship.TotalDamage:0}",
            HorizontalAlignment.Left, -1, 10, ReassemblyUi.TextMuted);

        var sel = ShipShapeLibrary.Get(_selectedShapeId);
        DrawString(_font, new Vector2(PaletteX + 14, statsY + 26),
            $"{sel.Label.ToUpperInvariant()} // MASS {sel.Mass:0}  HULL {sel.Hull:0}" +
            (sel.IsThruster ? $"  THRUST {sel.Thrust:0}" : "") +
            (sel.IsWeapon ? $"  DPS {sel.Damage:0}" : ""),
            HorizontalAlignment.Left, -1, 10, ReassemblyUi.CyanHighlight);
        DrawString(_font, new Vector2(PaletteX + 14, statsY + 46), "[M / ESC] EXIT // [F] FOCUS SHIP", HorizontalAlignment.Left, -1, 10, ReassemblyUi.TextMuted);
    }

    private void DrawHud()
    {
        var sel = ShipShapeLibrary.Get(_selectedShapeId);
        var hudRect = new Rect2(18, 96, 360, 116);
        ReassemblyUi.DrawChamferFrame(this, hudRect, ReassemblyUi.VoidDark, ReassemblyUi.CyanMuted * 0.7f, 8.0f, true, 1.2f);

        DrawString(_font, new Vector2(34, 122), "SHIP CONSTRUCTION // REASSEMBLY DOCK", HorizontalAlignment.Left, -1, 10, ReassemblyUi.CyanHighlight);
        DrawString(_font, new Vector2(34, 148), sel.Label, HorizontalAlignment.Left, -1, 18, ReassemblyUi.TextWhite);
        DrawString(_font, new Vector2(34, 169), ModuleBrief(sel), HorizontalAlignment.Left, 320, 11, ReassemblyUi.TextSteel);

        var twrRatio = Mathf.Clamp(_ship.TotalThrust / Mathf.Max(1.0f, _ship.TotalMass * 2.5f), 0.0f, 1.0f);
        ReassemblyUi.DrawSegmentedMeter(this, new Vector2(34, 178), new Vector2(160, 6), twrRatio, ReassemblyUi.PhosphorGreen, new Color("#10222b"), 12, 2.0f);
        DrawString(_font, new Vector2(204, 184), $"TWR {(twrRatio * 3.0f):0.1}", HorizontalAlignment.Left, -1, 9, ReassemblyUi.TextMuted);

        DrawString(_font, new Vector2(34, 202), $"ANGLE {Mathf.RadToDeg(_previewAngle):0}°   MIRROR {(_mirrorMode ? "[ACTIVE]" : "[OFF]")}", HorizontalAlignment.Left, -1, 10, _mirrorMode ? ReassemblyUi.TacticalAmber : ReassemblyUi.TextMuted);

        DrawString(_font, new Vector2(18, ViewHeight - 22), "[LMB] ATTACH / DRAG    [RMB / Q / E] ROTATE    [X] MIRROR    [F] RE-CENTER    [SCROLL] ZOOM", HorizontalAlignment.Left, -1, 11, ReassemblyUi.TextSteel);
        DrawString(_font, new Vector2(ViewWidth - 192, ViewHeight - 22), "REWORLD // SHIPYARD", HorizontalAlignment.Left, -1, 10, ReassemblyUi.CyanHighlight);
    }

    private static string ModuleBrief(ShipShapeLibrary.ShapeSpec spec) => spec.Id switch
    {
        "core" => "舰船指挥核心 · 提供连接基准与生命支持",
        "hull" => "标准装甲骨架 · 轻量化结构连接件",
        "armor" => "复合防护层 · 提升耐久，适合外圈",
        "thruster" or "thruster_medium" or "thruster_large" => "矢量推进单元 · 沿端口方向输出推力",
        "gyro" => "姿态控制陀螺 · 维持转向与稳定",
        "reactor" or "generator" or "capacitor" => "能源网络节点 · 支撑高功率系统",
        "shield" => "偏转护盾 · 覆盖周边的能量防区",
        "sensor" => "深空传感器 · 扩大航行态势感知",
        "hyperdrive" => "迁跃引擎 · 接入核心后按 J 选择相邻星系",
        "radiator" => "散热翼 · 将热量导出舰体",
        "cargo" or "tractor" => "物流模块 · 运输与回收作业",
        _ when spec.IsWeapon => "武器挂载点 · 沿朝向投射火力",
        _ => "通用模块 · 接入舰体网络后生效",
    };

    private void DrawTechPanel(Rect2 rect, bool chamfer)
    {
        var points = chamfer
            ? new[] { new Vector2(rect.Position.X + 10, rect.Position.Y), new Vector2(rect.End.X, rect.Position.Y), new Vector2(rect.End.X, rect.End.Y - 10), new Vector2(rect.End.X - 10, rect.End.Y), new Vector2(rect.Position.X, rect.End.Y), new Vector2(rect.Position.X, rect.Position.Y + 10) }
            : new[] { rect.Position, new Vector2(rect.End.X, rect.Position.Y), rect.End, new Vector2(rect.Position.X, rect.End.Y) };
        DrawColoredPolygon(points, PanelFill);
        DrawPolyline(points.Append(points[0]).ToArray(), PanelEdge, 1.0f);
        DrawLine(new Vector2(rect.Position.X, rect.Position.Y + 10), new Vector2(rect.Position.X, rect.Position.Y + 26), Telemetry, 2.0f);
    }

    private static Font CreateUiFont()
    {
        return new SystemFont
        {
            FontNames = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Noto Sans CJK SC" },
            AllowSystemFallback = true,
        };
    }
}
