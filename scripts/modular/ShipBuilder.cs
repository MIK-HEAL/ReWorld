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
        DrawRect(new Rect2(Vector2.Zero, new Vector2(ViewWidth, ViewHeight)), new Color("#04070d"));

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
            DrawLine(new Vector2(x, topLeft.Y), new Vector2(x, bottomRight.Y), new Color("#152434", 0.5f), 1.0f / _cameraZoom);
        for (var y = startY; y <= bottomRight.Y; y += step)
            DrawLine(new Vector2(topLeft.X, y), new Vector2(bottomRight.X, y), new Color("#152434", 0.5f), 1.0f / _cameraZoom);
    }

    private void DrawShip()
    {
        // 连接线（端口重合的方块之间）。
        foreach (var (a, b) in _ship.GetConnections())
            DrawLine(a.Position, b.Position, new Color("#36586a"), 4.0f);

        var components = _ship.GetConnectedComponents();
        var mainComponent = components.FirstOrDefault(component => component.Any(block => block.ShapeId == "core")) ?? new List<ModularShip.Block>();

        foreach (var block in _ship.Blocks)
        {
            var spec = block.Spec;
            var vertices = spec.Vertices()
                .Select(v => block.Position + v.Rotated(block.Angle))
                .ToArray();

            var color = spec.Color.Lerp(new Color("#f0f4f0"), 0.15f);
            if (!block.Enabled || !mainComponent.Contains(block))
                color = color.Darkened(0.55f);

            DrawColoredPolygon(vertices, color);
            DrawPolyline(vertices.Append(vertices[0]).ToArray(), new Color("#f0f4f0", 0.75f), 1.2f);

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
                DrawPolyline(diamond.Append(diamond[0]).ToArray(), new Color("#fff0b2"), 1.5f);
            }
            else if (spec.IsThruster)
            {
                ModularShipVisual.DrawEngine(this, spec, block.Position, block.Angle);
            }
            else if (spec.IsWeapon)
            {
                // 武器：画炮口方向。
                var dir = Vector2.Right.Rotated(block.Angle);
                DrawLine(block.Position, block.Position + dir * spec.Radius, new Color("#ffe9a6", 0.7f), 2.0f);
            }

            // 未连接到主体的方块标记。
            if (!mainComponent.Contains(block))
            {
                var bbox = block.Position;
                DrawLine(bbox + new Vector2(-8, -8), bbox + new Vector2(8, 8), new Color("#f07c8e", 0.8f), 2.0f);
                DrawLine(bbox + new Vector2(8, -8), bbox + new Vector2(-8, 8), new Color("#f07c8e", 0.8f), 2.0f);
            }
            if (block == _selection)
                DrawPolyline(vertices.Append(vertices[0]).ToArray(), new Color("#f2cc60"), 2.0f);
        }

        // 端口标记（悬停吸附时高亮显示目标端口，或恒定显示自由端口）。
        DrawPortMarkers();
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

        // 高亮吸附目标端口（对齐 Reassembly：端口吸附时端口发光）。
        if (_snapTargetPortWorld.HasValue)
        {
            DrawCircle(_snapTargetPortWorld.Value, 6.0f, new Color("#65c18c", 0.5f));
            DrawCircle(_snapTargetPortWorld.Value, 10.0f, new Color("#65c18c", 0.25f), false, 1.5f);
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
        DrawRect(new Rect2(new Vector2(PaletteX, 8), new Vector2(PaletteWidth, ViewHeight - 16)), new Color("#0b151f", 0.92f));
        DrawRect(new Rect2(new Vector2(PaletteX, 8), new Vector2(PaletteWidth, ViewHeight - 16)), new Color("#2d5263"), false, 1.0f);
        DrawString(_font, new Vector2(PaletteX + 12, 32), "方块库（点击选择）", HorizontalAlignment.Left, -1, 16, new Color("#f4f1de"));

        // 按类别分组（照搬 Reassembly 的分类方块面板）。
        _paletteButtons.Clear();
        var y = 46.0f;
        foreach (var group in ShipShapeLibrary.BuildOrder.GroupBy(id => ShipShapeLibrary.Get(id).Category))
        {
            DrawString(_font, new Vector2(PaletteX + 12, y + 13), group.Key, HorizontalAlignment.Left, -1, 12, new Color("#56707f"));
            y += 17.0f;

            var col = 0;
            foreach (var shapeId in group)
            {
                var spec = ShipShapeLibrary.Get(shapeId);
                var x = PaletteX + 12 + col * 140;
                var selected = shapeId == _selectedShapeId;
                var rect = new Rect2(new Vector2(x, y), new Vector2(PaletteItemWidth, PaletteItemHeight));
                _paletteButtons.Add((rect, shapeId));

                var bg = selected ? new Color("#1d3a52") : new Color("#122334");
                DrawRect(rect, bg);
                DrawRect(rect, selected ? new Color("#65c18c") : new Color("#2d5263"), false, 1.0f);

                // 小形状预览（照搬 Reassembly 调色板图标）。
                var previewCenter = new Vector2(x + 16, y + 16);
                var preview = spec.Vertices().Select(v => previewCenter + v * (12.0f / spec.Radius)).ToArray();
                DrawColoredPolygon(preview, spec.Color);
                DrawPolyline(preview.Append(preview[0]).ToArray(), new Color("#f0f4f0", 0.6f), 1.0f);

                DrawString(_font, new Vector2(x + 32, y + 20), spec.Label, HorizontalAlignment.Left, -1, 11,
                    selected ? new Color("#f4f1de") : new Color("#9fb3c2"));

                col++;
                if (col == 2)
                {
                    col = 0;
                    y += 32.0f;
                }
            }
            if (col != 0)
                y += 32.0f;
        }

        // 舰体统计 + 当前方块属性（面板底部）。
        var statsY = ViewHeight - 76.0f;
        DrawLine(new Vector2(PaletteX + 8, statsY - 8), new Vector2(PaletteX + PaletteWidth - 8, statsY - 8), new Color("#2d5263"), 1.0f);
        DrawString(_font, new Vector2(PaletteX + 12, statsY + 8),
            $"方块 {_ship.Blocks.Count}  质量 {_ship.TotalMass:0}  推力 {_ship.TotalThrust:0}  火力 {_ship.TotalDamage:0}",
            HorizontalAlignment.Left, -1, 12, new Color("#7f93a8"));
        var sel = ShipShapeLibrary.Get(_selectedShapeId);
        DrawString(_font, new Vector2(PaletteX + 12, statsY + 28),
            $"{sel.Label}：质量 {sel.Mass:0} 耐久 {sel.Hull:0}" +
            (sel.IsThruster ? $" 推力 {sel.Thrust:0}" : "") +
            (sel.IsWeapon ? $" 伤害 {sel.Damage:0}" : ""),
            HorizontalAlignment.Left, -1, 11, new Color("#56707f"));
        DrawString(_font, new Vector2(PaletteX + 12, statsY + 48), "Esc/M 返回宇宙 · 飞行键 W A S D", HorizontalAlignment.Left, -1, 11, new Color("#3f5566"));
    }

    private void DrawHud()
    {
        DrawString(_font, new Vector2(18, 20), $"当前：{ShipShapeLibrary.Get(_selectedShapeId).Label}  朝向 {Mathf.RadToDeg(_previewAngle):0}°  对称 {(_mirrorMode ? "开" : "关")}",
            HorizontalAlignment.Left, -1, 14, new Color("#b7c7d4"));
        DrawString(_font, new Vector2(18, 44),
            "左键放置/拖动 · 右键/Q/E 旋转 · Delete 删除 · 滚轮缩放 · Alt+滚轮 引擎尺寸",
            HorizontalAlignment.Left, -1, 13, new Color("#6e8d9c"));
        DrawString(_font, new Vector2(18, 66), "右侧选方块 · Alt+拖动复制 · Ctrl+Z/Y 撤销/重做 · X 对称 · 中键平移 · F 居中",
            HorizontalAlignment.Left, -1, 12, new Color("#56707f"));
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
