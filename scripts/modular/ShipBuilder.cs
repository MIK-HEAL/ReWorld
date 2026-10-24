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
///   - 左键放置、右键删除、拖拽移动。
///
/// 这是一个独立的整屏 Node2D，挂在 Main 下作为「模块化造舰」模式。
/// 它操作的是 ModularShip（任意角度多边形舰体），与原有网格 ModuleGraph 并存。
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

    private ModularShip.Block? _draggingBlock;
    private Vector2 _dragGrabOffset;

    /// <summary>对称建造模式（Reassembly 标志性功能，按 X 切换）：沿过核心的纵轴镜像放置/删除。默认关闭，按需开启。</summary>
    private bool _mirrorMode = false;

    private (Vector2 Position, float Angle, bool Valid)? _snapPreview;
    private Font _font = null!;

    public ModularShip Ship => _ship;
    public string SelectedShapeId => _selectedShapeId;

    /// <summary>把镜头居中到舰体（进入建造页时调用）。</summary>
    public void FocusShip()
    {
        if (_ship.Blocks.Count > 0)
            _cameraPosition = _ship.CenterOfMass();
        QueueRedraw();
    }

    public override void _Ready()
    {
        _font = CreateUiFont();
        // 若尚未 seed（例如 Main 已提前 seed 供宇宙/试航共享），则补上参考舰。
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
        _mouseWorld = ScreenToWorld(GetViewport().GetMousePosition());
        _snapPreview = ComputeSnap(_selectedShapeId, _previewAngle, _mouseWorld);
    }

    /// <summary>计算方块在指定位置的吸附结果（最近的未占用端口）。</summary>
    private (Vector2 Position, float Angle, bool Valid)? ComputeSnap(string shapeId, float angle, Vector2 worldPos)
    {
        if (!ShipShapeLibrary.Shapes.TryGetValue(shapeId, out var spec))
            return null;

        // 无核心时，核心方块可直接放在光标处。
        if (shapeId == "core" && _ship.Core is null)
            return (worldPos, angle, true);

        // 找最近的、且未被占用的目标端口，距离在吸附阈值内才吸附。
        // 对齐逆向 connectPorts：尝试「选中方块的每个端口」对「目标方块的每个空闲端口」所有组合，
        // 取使光标距离最近的一组（玩家不必关心用哪个端口接，算法自动选最佳对接）。
        (Vector2 Position, float Angle, bool Valid)? best = null;
        var bestDist = 80.0f; // 吸附阈值（世界单位）。

        foreach (var target in _ship.Blocks.Where(block => block.Enabled))
        {
            var targetSpec = target.Spec;
            foreach (var targetPort in targetSpec.Ports)
            {
                // 该端口是否已被占用（已有一个方块连在此端口）。
                if (IsPortOccupied(target, targetPort))
                    continue;

                foreach (var selfPort in spec.Ports)
                {
                    var (snapPos, snapAngle) = ModularShip.SnapBlock(
                        selfPort, targetPort, target.Position, target.Angle);

                    // 计算吸附后方块中心的距离（越近越好）。
                    var centerDist = snapPos.DistanceTo(worldPos);
                    if (centerDist < bestDist)
                    {
                        bestDist = centerDist;
                        best = (snapPos, snapAngle, true);
                    }
                }
            }
        }

        return best;
    }

    /// <summary>
    /// 拖拽松手时，把方块平移吸附到最近的空闲端口（保持方块自身角度不变）。
    /// 在所有「本方块端口 ↔ 目标空闲端口」组合里找距离最近且在阈值内的一对。
    /// </summary>
    private void SnapDraggedBlock(ModularShip.Block block)
    {
        const float snapThreshold = 48.0f;
        Vector2? bestPos = null;
        var bestDist = snapThreshold;

        foreach (var target in _ship.Blocks.Where(other => other.Id != block.Id && other.Enabled))
        {
            foreach (var targetPort in target.Spec.Ports)
            {
                if (IsPortOccupied(target, targetPort))
                    continue;
                var targetPortWorld = target.Position + targetPort.LocalPosition.Rotated(target.Angle);
                foreach (var ownPort in block.Spec.Ports)
                {
                    // 平移方块，使 ownPort 与目标端口重合（角度保持拖拽时的朝向）。
                    var candidate = targetPortWorld - ownPort.LocalPosition.Rotated(block.Angle);
                    var dist = candidate.DistanceTo(block.Position);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestPos = candidate;
                    }
                }
            }
        }

        if (bestPos.HasValue)
            block.Position = bestPos.Value;
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

    /// <summary>目标方块的目标端口是否已被其他方块占用。</summary>
    private bool IsPortOccupied(ModularShip.Block target, ShipShapeLibrary.Port targetPort)
    {
        var targetPortWorld = target.Position + targetPort.LocalPosition.Rotated(target.Angle);
        foreach (var other in _ship.Blocks)
        {
            if (other.Id == target.Id || !other.Enabled)
                continue;
            foreach (var otherPort in other.Spec.Ports)
            {
                var otherPortWorld = other.Position + otherPort.LocalPosition.Rotated(other.Angle);
                if (otherPortWorld.DistanceTo(targetPortWorld) <= 1.5f)
                    return true;
            }
        }
        return false;
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouse)
        {
            if (mouse.ButtonIndex == MouseButton.Middle)
            {
                _cameraDragging = mouse.Pressed;
                return;
            }

            if (mouse.Pressed && mouse.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                var worldBefore = ScreenToWorld(mouse.Position);
                _cameraZoom = Mathf.Clamp(_cameraZoom * (mouse.ButtonIndex == MouseButton.WheelUp ? 1.12f : 1.0f / 1.12f), 0.3f, 3.0f);
                _cameraPosition = worldBefore - (mouse.Position - new Vector2(ViewWidth, ViewHeight) * 0.5f) / _cameraZoom;
                return;
            }

            if (!mouse.Pressed)
            {
                // 松开左键：结束拖拽，并把方块吸附到最近的空闲端口（对齐 Reassembly 的拖拽吸附）。
                if (mouse.ButtonIndex == MouseButton.Left && _draggingBlock is not null)
                {
                    SnapDraggedBlock(_draggingBlock);
                    _draggingBlock = null;
                    _dragGrabOffset = Vector2.Zero;
                }
                return;
            }

            if (mouse.ButtonIndex == MouseButton.Left)
            {
                // 点击已有方块 → 选中/拖拽。
                var clicked = BlockAt(ScreenToWorld(mouse.Position));
                if (clicked is not null && clicked.ShapeId != "core")
                {
                    _draggingBlock = clicked;
                    _dragGrabOffset = clicked.Position - ScreenToWorld(mouse.Position);
                    return;
                }

                // 放置新方块（对称模式下同时镜像放置）。
                if (_snapPreview is { Valid: true } snap)
                {
                    _ship.AddBlock(_selectedShapeId, snap.Position, snap.Angle);
                    if (_mirrorMode && _ship.Core is { } core)
                    {
                        var mirrorPos = MirrorAcrossAxis(snap.Position, core.Position);
                        var mirrorAngle = MirrorAngleAcrossAxis(snap.Angle);
                        // 镜像点重新计算吸附，保证端口对齐。
                        var mirrorSnap = ComputeSnap(_selectedShapeId, mirrorAngle, mirrorPos);
                        if (mirrorSnap is { Valid: true } ms && ms.Position.DistanceTo(snap.Position) > 1.0f)
                            _ship.AddBlock(_selectedShapeId, ms.Position, ms.Angle);
                    }
                }
                return;
            }

            if (mouse.ButtonIndex == MouseButton.Right)
            {
                var clicked = BlockAt(ScreenToWorld(mouse.Position));
                if (clicked is not null)
                {
                    _ship.RemoveBlock(clicked);
                    // 对称模式：同时删除镜像位置的同形方块。
                    if (_mirrorMode && _ship.Core is { } core && clicked.ShapeId != "core")
                    {
                        var mirrorPos = MirrorAcrossAxis(clicked.Position, core.Position);
                        var mirrored = _ship.Blocks
                            .Where(b => b.Enabled && b.ShapeId == clicked.ShapeId && b.Id != clicked.Id)
                            .OrderBy(b => b.Position.DistanceTo(mirrorPos))
                            .FirstOrDefault();
                        if (mirrored is not null && mirrored.Position.DistanceTo(mirrorPos) <= mirrored.Spec.Radius)
                            _ship.RemoveBlock(mirrored);
                    }
                }
                return;
            }
        }

        if (@event is InputEventMouseMotion motion)
        {
            if (_cameraDragging)
            {
                _cameraPosition -= motion.Relative / _cameraZoom;
                return;
            }

            if (_draggingBlock is not null)
            {
                var world = ScreenToWorld(motion.Position);
                _draggingBlock.Position = world + _dragGrabOffset;
                return;
            }
        }

        if (@event is InputEventKey key && key.Pressed && !key.Echo)
        {
            if (key.Keycode == Key.R)
                _previewAngle += Mathf.Pi * 0.25f;
            else if (key.Keycode == Key.Q)
                _previewAngle -= Mathf.Pi * 0.25f;
            else if (key.Keycode == Key.Left)
                _previewAngle -= Mathf.Pi * 0.05f;
            else if (key.Keycode == Key.Right)
                _previewAngle += Mathf.Pi * 0.05f;
            else if (key.Keycode == Key.F)
            {
                _cameraPosition = _ship.CenterOfMass();
                _cameraZoom = 1.0f;
            }
            else if (key.Keycode == Key.X)
            {
                _mirrorMode = !_mirrorMode;
            }
            else if (TryGetShapeShortcut(key.Keycode, out var index) && index < ShipShapeLibrary.BuildOrder.Length)
            {
                _selectedShapeId = ShipShapeLibrary.BuildOrder[index];
            }
        }
    }

    private ModularShip.Block? BlockAt(Vector2 worldPos)
    {
        // 从后往前（后放置的在上层）。
        for (var i = _ship.Blocks.Count - 1; i >= 0; i--)
        {
            var block = _ship.Blocks[i];
            if (!block.Enabled)
                continue;
            if (block.Position.DistanceTo(worldPos) <= block.Spec.Radius)
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

    private static bool TryGetShapeShortcut(Key key, out int index)
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

            var color = new Color(spec.ColorHex).Lerp(new Color("#f0f4f0"), 0.15f);
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
                // 推进器：画排气方向。
                var dir = Vector2.Right.Rotated(block.Angle);
                DrawLine(block.Position, block.Position - dir * spec.Radius * 0.8f, new Color("#f2cc60", 0.6f), 2.0f);
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
        }

        // 端口标记（悬停吸附时高亮显示目标端口，或恒定显示自由端口）。
        DrawPortMarkers();
    }

    private void DrawPortMarkers()
    {
        foreach (var block in _ship.Blocks)
        {
            if (!block.Enabled)
                continue;
            foreach (var port in block.Spec.Ports)
            {
                var world = block.Position + port.LocalPosition.Rotated(block.Angle);
                var occupied = IsPortOccupied(block, port);
                DrawCircle(world, 3.0f, occupied ? new Color("#f07c8e", 0.5f) : new Color("#7bb7e8", 0.6f));
            }
        }
    }

    private void DrawSnapPreview()
    {
        if (_draggingBlock is not null)
            return;

        if (_snapPreview is not { } snap || !ShipShapeLibrary.Shapes.TryGetValue(_selectedShapeId, out var spec))
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

        // 朝向指示。
        var dir = Vector2.Right.Rotated(angle);
        DrawLine(position, position + dir * spec.Radius, line, 2.0f);
    }

    private void DrawPalette()
    {
        DrawRect(new Rect2(new Vector2(8, 8), new Vector2(300, ViewHeight - 16)), new Color("#0b151f", 0.92f));
        DrawRect(new Rect2(new Vector2(8, 8), new Vector2(300, ViewHeight - 16)), new Color("#2d5263"), false, 1.0f);
        DrawString(_font, new Vector2(20, 32), "方块库", HorizontalAlignment.Left, -1, 16, new Color("#f4f1de"));

        // 按类别分组（对齐 Reassembly 的分类方块面板）。
        var y = 46.0f;
        var index = 0;
        foreach (var group in ShipShapeLibrary.BuildOrder.GroupBy(id => ShipShapeLibrary.Get(id).Category))
        {
            DrawString(_font, new Vector2(20, y + 13), group.Key, HorizontalAlignment.Left, -1, 12, new Color("#56707f"));
            y += 20.0f;

            var col = 0;
            foreach (var shapeId in group)
            {
                var spec = ShipShapeLibrary.Get(shapeId);
                var x = 20 + col * 140;
                var selected = shapeId == _selectedShapeId;
                var bg = selected ? new Color("#1d3a52") : new Color("#122334");
                DrawRect(new Rect2(new Vector2(x, y), new Vector2(132, 32)), bg);
                DrawRect(new Rect2(new Vector2(x, y), new Vector2(132, 32)), selected ? new Color("#65c18c") : new Color("#2d5263"), false, 1.0f);

                // 小形状预览。
                var preview = spec.Vertices().Select(v => new Vector2(x + 16, y + 16) + v * 0.4f).ToArray();
                DrawColoredPolygon(preview, new Color(spec.ColorHex));
                DrawPolyline(preview.Append(preview[0]).ToArray(), new Color("#f0f4f0", 0.6f), 1.0f);

                var shortcut = index < 12 ? GetShortcutLabel(index) + " " : "";
                DrawString(_font, new Vector2(x + 32, y + 20), $"{shortcut}{spec.Label}", HorizontalAlignment.Left, -1, 11,
                    selected ? new Color("#f4f1de") : new Color("#9fb3c2"));

                col++;
                index++;
                if (col == 2)
                {
                    col = 0;
                    y += 36.0f;
                }
            }
            if (col != 0)
                y += 36.0f;
        }

        // 舰体统计 + 当前方块属性（面板底部）。
        var statsY = ViewHeight - 76.0f;
        DrawLine(new Vector2(16, statsY - 8), new Vector2(300, statsY - 8), new Color("#2d5263"), 1.0f);
        DrawString(_font, new Vector2(20, statsY + 8),
            $"方块 {_ship.Blocks.Count}  质量 {_ship.TotalMass:0}  推力 {_ship.TotalThrust:0}  火力 {_ship.TotalDamage:0}",
            HorizontalAlignment.Left, -1, 12, new Color("#7f93a8"));
        var sel = ShipShapeLibrary.Get(_selectedShapeId);
        DrawString(_font, new Vector2(20, statsY + 28),
            $"{sel.Label}：质量 {sel.Mass:0} 耐久 {sel.Hull:0}" +
            (sel.IsThruster ? $" 推力 {sel.Thrust:0}" : "") +
            (sel.IsWeapon ? $" 伤害 {sel.Damage:0}" : ""),
            HorizontalAlignment.Left, -1, 11, new Color("#56707f"));
        DrawString(_font, new Vector2(20, statsY + 48), "Esc/M 返回宇宙 · 飞行键 W A S D", HorizontalAlignment.Left, -1, 11, new Color("#3f5566"));
    }

    private void DrawHud()
    {
        DrawString(_font, new Vector2(320, 20), $"当前：{ShipShapeLibrary.Get(_selectedShapeId).Label}  朝向 {Mathf.RadToDeg(_previewAngle):0}°  对称 {(_mirrorMode ? "开" : "关")}",
            HorizontalAlignment.Left, -1, 14, new Color("#b7c7d4"));
        DrawString(_font, new Vector2(320, 44),
            "左键放置 · 右键删除 · 拖动移动 · R/Q 旋转(45°) · ←/→ 微调 · X 对称 · 滚轮缩放 · 中键平移 · F 居中",
            HorizontalAlignment.Left, -1, 13, new Color("#6e8d9c"));
        DrawString(_font, new Vector2(320, 66), "端口自动吸附：靠近任意空闲端口，方块即对齐法线连接（任意角度多边形舰体）。",
            HorizontalAlignment.Left, -1, 12, new Color("#56707f"));
    }

    private static string GetShortcutLabel(int index) => index switch
    {
        < 9 => (index + 1).ToString(),
        9 => "0",
        10 => "A",
        11 => "D",
        _ => "?",
    };

    private static Font CreateUiFont()
    {
        return new SystemFont
        {
            FontNames = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Noto Sans CJK SC" },
            AllowSystemFallback = true,
        };
    }
}
