using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;

public partial class UniverseView
{
    private readonly UniverseTraffic _traffic = new();
    private readonly WarpDrive _warp = new();
    private Node2D _bodyLayer = null!;
    private readonly Dictionary<string, RetainedBodyVisual> _retainedBodies = new();
    private UniverseGenerator.CelestialBody? _dockedStation;
    private bool _chartOpen, _showPerformance;
    private Vector2I _chartOrigin, _warpSelection;
    private string _notice = "H 标记最近空间站 · J 打开星系迁跃图";
    private float _noticeTime = 9;
    private readonly double[] _frameTimes = new double[360];
    private readonly double[] _sortedTimes = new double[360];
    private int _frameIndex, _frameCount;
    private double _statsTimer, _p95, _median;
    private long _frameTimestamp;
    private bool _warpCameraActive;
    private float _warpCameraZoom, _warpZoomBlend;
    public WarpDrive Warp => _warp;
    public UniverseTraffic Traffic => _traffic;
    public bool IsDocked => _dockedStation is not null;
    public bool ChartOpen => _chartOpen;
    public bool TravelLocksBuilder => _warp.LocksFlight || _ship is { HasLiveCore: false } ||
        _traffic.Vessels.Any(v => v.Alive && Progress.Diplomacy.Hostile(1, v.FactionId) && v.Position.DistanceTo(_playerPosition) < 1500);
    public void CloseNavigationOverlays() { _chartOpen = false; CloseCampaignOverlays(); }
    public double FrameMedianMs => _median;
    public double FrameP95Ms => _p95;
    public int RetainedDrawCount => _retainedBodies.Values.Sum(v => v.DrawCount);

    private void InitializeWorldServices()
    {
        _bodyLayer = new Node2D { Name = "RetainedCelestials", ShowBehindParent = true };
        AddChild(_bodyLayer);
        _traffic.Load(UniverseGenerator.WorldToSystemCell(_playerPosition), _playerPosition);
        InitializeCampaign();
    }

    private void SyncRetainedBodies()
    {
        if (_bodyLayer is null) return;
        var ids = new HashSet<string>(_visibleBodies.Select(b => b.Id));
        foreach (var id in _retainedBodies.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            _retainedBodies[id].QueueFree();
            _retainedBodies.Remove(id);
        }
        foreach (var body in _visibleBodies)
        {
            if (_retainedBodies.ContainsKey(body.Id)) continue;
            var visual = new RetainedBodyVisual { Name = "Celestial", Position = body.Position, Paint = canvas => DrawCelestialBody(canvas, body) };
            _retainedBodies.Add(body.Id, visual);
            _bodyLayer.AddChild(visual);
        }
    }

    private void UpdateWorldPresentation(float dt)
    {
        if (_warpCameraActive)
        {
            var amount = _warp.IsMoving ? _warp.Speed / WarpDrive.CruiseSpeed : 0;
            _warpZoomBlend = Mathf.Lerp(_warpZoomBlend, amount, 1-Mathf.Exp(-3*dt));
            _cameraZoom = Mathf.Clamp(_warpCameraZoom / (1 + _warpZoomBlend * 42), ZoomMin, ZoomMax);
            if (!_warp.LocksFlight && _warpZoomBlend < 0.001f)
            { _cameraZoom = _warpCameraZoom; _warpCameraActive = false; }
        }
        _noticeTime = Math.Max(0, _noticeTime - dt);
        _bodyLayer.Position = new Vector2(ViewWidth, ViewHeight) * 0.5f - _cameraPosition * _cameraZoom;
        _bodyLayer.Scale = Vector2.One * _cameraZoom;
        var half = new Vector2(ViewWidth, ViewHeight) * 0.5f / _cameraZoom;
        var rect = new Rect2(_cameraPosition - half, half * 2);
        foreach (var body in _visibleBodies)
            if (_retainedBodies.TryGetValue(body.Id, out var visual))
                visual.Visible = rect.Grow(body.Radius * 1.65f).HasPoint(body.Position);
        var timestamp = Stopwatch.GetTimestamp();
        var frameMs = _frameTimestamp == 0 ? GetProcessDeltaTime() * 1000
            : (timestamp - _frameTimestamp) * 1000.0 / Stopwatch.Frequency;
        _frameTimestamp = timestamp;
        _frameTimes[_frameIndex++ % _frameTimes.Length] = frameMs;
        _frameCount = Math.Min(_frameCount + 1, _frameTimes.Length);
        _statsTimer += dt;
        if (_statsTimer >= 0.5 && _frameCount > 0)
        {
            Array.Copy(_frameTimes, _sortedTimes, _frameCount);
            Array.Sort(_sortedTimes, 0, _frameCount);
            _median = _sortedTimes[_frameCount / 2];
            _p95 = _sortedTimes[Math.Min(_frameCount - 1, (int)(_frameCount * 0.95))];
            _statsTimer = 0;
        }
    }

    private bool UpdateWorldPhysics(float delta)
    {
        var cell = UniverseGenerator.WorldToSystemCell(_playerPosition);
        if (cell != _traffic.Cell && _playerPosition.DistanceTo(UniverseGenerator.SystemCenter(cell)) < UniverseGenerator.SystemRadius)
        {
            _traffic.Load(cell, _playerPosition);
            foreach (var visual in _retainedBodies.Values) visual.QueueRedraw();
        }
        _traffic.Step(Math.Min(delta, 0.1f), _playerPosition, _playerHeading, _visibleBodies);
        var wasMoving = _warp.IsMoving;
        var wasCharging = _warp.State == WarpDrive.Phase.Charging;
        var arrived = _warp.Advance(delta);
        if (wasCharging && _warp.IsMoving) Notify("高速航行中 · 沿航线持续飞行 · Esc 减速退出");
        if (wasMoving || _warp.IsMoving || arrived)
        {
            // Hyperdrive owns the same world-space position as normal flight: the
            // camera follows it, so planets/stars visibly sweep past the ship.
            _previousPlayerPosition = _playerPosition;
            _previousPlayerHeading = _playerHeading;
            _playerPosition = _warp.Position;
            _playerVelocity = _warp.Velocity;
            _playerHeading = _warp.Direction.Angle();
            _playerAngularVelocity = 0;
            _thrusterActivity.Clear();
            _currentGravity = 0;
            if (arrived) ArriveFromWarp();
            else if (wasMoving && !_warp.IsMoving) Notify("已减速退出迁跃 · 停留在当前空域");
            return true;
        }
        var locked = _warp.LocksFlight;
        if (_dockedStation is { } station)
        {
            _playerPosition = DockingPosition(station, _flightProfile.BoundingRadius);
            _playerVelocity = Vector2.Zero;
            _playerAngularVelocity = 0;
            _playerHeading = 0;
            ResetFlightPresentation();
            _thrusterActivity.Clear();
            return true;
        }
        if (locked)
        {
            _previousPlayerPosition = _playerPosition;
            _previousPlayerHeading = _playerHeading;
            _playerHeading = Mathf.LerpAngle(_playerHeading, _warp.Direction.Angle(), 1-Mathf.Exp(-6*delta));
            _playerVelocity = Vector2.Zero;
            _playerAngularVelocity = 0;
            _thrusterActivity.Clear();
            return true;
        }
        return false;
    }

    public static Vector2 DockingPosition(UniverseGenerator.CelestialBody station, float shipRadius)
        => station.Position + Vector2.Right * (station.Radius + Math.Max(80, shipRadius + 40));

    private void ResetFlightPresentation()
    {
        _previousPlayerPosition = _visualPlayerPosition = _playerPosition;
        _previousPlayerHeading = _visualPlayerHeading = _playerHeading;
        _cameraPosition = _playerPosition;
    }
    private void Notify(string text) { _notice = text; _noticeTime = 6; }

    public bool ToggleDock()
    {
        if (_warp.LocksFlight) return false;
        if (_dockedStation is not null)
        {
            _dockedStation = null;
            Notify("已离站 · 航行控制已恢复");
            return true;
        }
        var station = NearestStation();
        if (station is not null && (Progress.Diplomacy.Hostile(1, FactionRelations.League) ||
            _traffic.StationActors.TryGetValue(station.Id, out var actor) && !actor.Alive))
        { Notify("交战或已摧毁的空间站无法停靠"); return false; }
        if (station is null || station.SurfaceDistance(_playerPosition) > 700)
        { Notify("靠近空间站至 700 距离内再按 F · H 标记空间站"); return false; }
        if (_playerVelocity.Length() > 180) { Notify("停靠需减速至 180 以下"); return false; }
        _dockedStation = _targetBody = station;
        if (_ship is not null)
            foreach (var block in _ship.Blocks) block.Health = block.Spec.Hull;
        Notify("停靠完成 · 舰体已修复 · F 离站 · M 造舰");
        return true;
    }
    private UniverseGenerator.CelestialBody? NearestStation()
        => UniverseGenerator.GenerateSystem(UniverseGenerator.WorldToSystemCell(_playerPosition)).Bodies
            .Where(b => b.Kind == UniverseGenerator.CelestialKind.Station)
            .MinBy(b => b.Position.DistanceSquaredTo(_playerPosition));

    public bool BeginWarp(Vector2I destination)
    {
        if (_ship is not null && _flightProfile.Revision != _ship.Revision) RefreshFlightProfile();
        var poweredDrive = _ship?.Core is { Health: > 0 } && _ship.GetMainComponent().Any(b =>
            b.Enabled && b.Health > 0 && (b.Features & BlockFeatures.Hyperdrive) != 0);
        var clear = !_visibleBodies.Any(b => b.HasSurface && b.SurfaceDistance(_playerPosition) < _flightProfile.BoundingRadius + 180);
        if (!poweredDrive || IsDocked || !clear || _warp.State != WarpDrive.Phase.Ready ||
            !WarpDrive.InRange(UniverseGenerator.WorldToSystemCell(_playerPosition), destination))
        { Notify("迁跃不可用：需接通迁跃引擎、离站、远离地表、选择相邻星系并完成冷却"); return false; }
        var destinationSystem = UniverseGenerator.GenerateSystem(destination);
        var destinationStation = destinationSystem.Bodies.First(b => b.Kind == UniverseGenerator.CelestialKind.Station);
        var finish = UniverseView.DockingPosition(destinationStation, _flightProfile.BoundingRadius) + Vector2.Right * 500;
        var route = WarpRoutePlanner.Plan(_playerPosition, finish, _flightProfile.BoundingRadius);
        if (route is null) { Notify("迁跃航线被天体阻挡，请换一个相邻星系"); return false; }
        if (!_warp.Engage(UniverseGenerator.WorldToSystemCell(_playerPosition), destination,
            poweredDrive, IsDocked, clear, route))
        { Notify("迁跃不可用：需接通迁跃引擎、离站、远离地表、选择相邻星系并完成冷却"); return false; }
        _chartOpen = false;
        _targetBody = destinationStation;
        _warpCameraActive = true;
        _warpCameraZoom = _cameraZoom;
        _warpZoomBlend = 0;
        _playerVelocity = Vector2.Zero;
        _playerAngularVelocity = 0;
        ResetFlightPresentation();
        Notify($"迁跃引擎充能 · 航程 {_warp.TotalDistance:0} · Esc 减速退出");
        return true;
    }
    private void ArriveFromWarp()
    {
        var system = UniverseGenerator.GenerateSystem(_warp.Destination);
        var station = system.Bodies.First(b => b.Kind == UniverseGenerator.CelestialKind.Station);
        _targetBody = station;
        _playerVelocity = Vector2.Zero;
        _playerAngularVelocity = 0;
        _traffic.Load(_warp.Destination, _playerPosition);
        _loadedSector = new Vector2I(int.MaxValue, int.MaxValue);
        RefreshVisibleBodies();
        Notify($"已抵达 {system.Name} · 航程 {_warp.TotalDistance:0} · 友方空间站已标记 · F 停靠");
    }

    private bool HandleWorldInput(InputEvent input)
    {
        if (HandleCampaignInput(input)) return true;
        if (input is InputEventKey { Pressed: true, Echo: false } key)
        {
            // Main owns page/fullscreen shortcuts, including while this modal is open.
            if (key.Keycode is Key.M or Key.F11 || key.Keycode == Key.Enter && key.AltPressed) return false;
            if (key.Keycode == Key.F3) { _showPerformance = !_showPerformance; return true; }
            if (key.Keycode == Key.Escape && (_chartOpen || _warp.LocksFlight))
            { _chartOpen = false; _warp.Cancel(); Notify(_warp.State == WarpDrive.Phase.Braking ? "迁跃退出中 · 正在减速" : "迁跃已取消"); return true; }
            if (_warp.LocksFlight) return true;
            if (key.Keycode == Key.J)
            {
                _chartOpen = !_chartOpen;
                _chartOrigin = UniverseGenerator.WorldToSystemCell(_playerPosition);
                _warpSelection = _chartOrigin + Vector2I.Right;
                return true;
            }
            if (_chartOpen)
            {
                var move = key.Keycode switch { Key.Left => Vector2I.Left, Key.Right => Vector2I.Right,
                    Key.Up => Vector2I.Up, Key.Down => Vector2I.Down, _ => Vector2I.Zero };
                _warpSelection = (_warpSelection + move).Clamp(_chartOrigin - Vector2I.One, _chartOrigin + Vector2I.One);
                if (key.Keycode == Key.Enter) BeginWarp(_warpSelection);
                return true;
            }
            if (key.Keycode == Key.F) { ToggleDock(); return true; }
            if (key.Keycode == Key.H) { _targetBody = NearestStation(); Notify("已标记最近的友方空间站"); return true; }
        }
        if (!_warp.LocksFlight && input is InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown }) _warpCameraActive = false;
        if (_chartOpen && input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mouse)
        {
            foreach (var offset in ChartOffsets)
                if (ChartPoint(offset).DistanceTo(mouse.Position) < 40) _warpSelection = _chartOrigin + offset;
            if (new Rect2(780, 480, 270, 44).HasPoint(mouse.Position)) BeginWarp(_warpSelection);
            return true;
        }
        return _chartOpen || _warp.LocksFlight;
    }
    private static readonly Vector2I[] ChartOffsets = { new(-1,-1), new(0,-1), new(1,-1), new(-1,0),
        new(0,0), new(1,0), new(-1,1), new(0,1), new(1,1) };
    private static Vector2 ChartPoint(Vector2I offset) => new(460 + offset.X * 145, 370 + offset.Y * 130);

    private void DrawTraffic(Rect2 view)
    {
        var fraction = (float)Engine.GetPhysicsInterpolationFraction();
        foreach (var v in _traffic.Vessels)
        {
            var position = v.PreviousPosition.Lerp(v.Position, fraction);
            if (!view.Grow(100).HasPoint(position)) continue;
            if (_cameraZoom > 0.12f)
            {
                DrawSetTransform(WorldToScreen(position), 0, Vector2.One * _cameraZoom);
                ModularShipVisual.Draw(this, v.Ship, Vector2.Zero, Mathf.LerpAngle(v.PreviousHeading, v.Heading, fraction), thrusterActivity: v.Activity);
            }
            DrawSetTransform(new Vector2(ViewWidth, ViewHeight) * 0.5f - _cameraPosition * _cameraZoom, 0, Vector2.One * _cameraZoom);
            if (v.Alive) DrawCircle(position, v.Radius + 15, FactionColor(v.FactionId), false, 1 / _cameraZoom);
        }
    }
    private void DrawWorldOverlays()
    {
        // 舰船名牌防重叠：记录已占用的标签矩形，发生碰撞时向上错开
        var occupiedLabelRects = new List<Rect2>();
        foreach (var v in _traffic.Vessels)
        {
            if (!v.Alive) continue;
            var point = WorldToScreen(v.PreviousPosition.Lerp(v.Position, (float)Engine.GetPhysicsInterpolationFraction()));
            if (new Rect2(320, 90, 680, 470).HasPoint(point) && _cameraZoom > 0.18f)
            {
                var labelPos = point + new Vector2(-80, -42);
                var labelRect = new Rect2(labelPos + new Vector2(0, -12), new Vector2(160, 15));
                var attempts = 0;
                while (attempts < 6 && occupiedLabelRects.Any(r => r.Intersects(labelRect)))
                {
                    labelPos.Y -= 17;
                    labelRect = new Rect2(labelPos + new Vector2(0, -12), new Vector2(160, 15));
                    attempts++;
                }
                occupiedLabelRects.Add(labelRect);
                DrawString(_font, labelPos, $"{v.Name} · {FactionRelations.Label(Progress.Diplomacy.Get(1, v.FactionId))}", HorizontalAlignment.Center, 160, 11, FactionColor(v.FactionId));
            }
            var map = MiniMapPoint(v.Position);
            if (MiniMapRect.Grow(-4).HasPoint(map)) DrawCircle(map, 2, FactionColor(v.FactionId));
        }
        if (_noticeTime > 0 && !_chartOpen)
            DrawString(_font, new Vector2(330, 94), _notice, HorizontalAlignment.Center, 650, 14, ReassemblyUi.CyanHighlight);
        if (IsDocked)
        {
            var dockRect = new Rect2(340, 470, 600, 94);
            ReassemblyUi.DrawChamferFrame(this, dockRect, ReassemblyUi.VoidDark, ReassemblyUi.CyanHighlight, 8.0f, true, 1.5f);
            DrawString(_font, new Vector2(366, 504), "空间站服务 // 已停靠 · 维修完成", fontSize: 18, modulate: ReassemblyUi.CyanHighlight);
            DrawString(_font, new Vector2(366, 536), "[M] 进入造舰厂    [F] 离港", fontSize: 13, modulate: ReassemblyUi.TextSteel);
        }
        if (_showPerformance)
            DrawString(_font, new Vector2(340, 120), $"F3 帧时间 P50 {_median:0.0}ms / P95 {_p95:0.0}ms · {Engine.GetFramesPerSecond()} FPS · 静态绘制 {RetainedDrawCount}", fontSize: 13, modulate: Colors.LightGreen);
        if (_warp.State != WarpDrive.Phase.Ready) DrawWarpEffect();
        if (_chartOpen) DrawWarpChart();
        DrawCampaignOverlays();
    }
    private void DrawWarpChart()
    {
        DrawRect(new Rect2(0, 0, ViewWidth, ViewHeight), new Color("#101b24f2"));
        var chartRect = new Rect2(180, 110, 920, 510);
        ReassemblyUi.DrawChamferFrame(this, chartRect, ReassemblyUi.VoidDark, ReassemblyUi.PanelBorder, 12.0f, true, 1.2f);

        DrawString(_font, new Vector2(220, 150), "迁跃引擎星区拓扑 // 星系航图", fontSize: 20, modulate: ReassemblyUi.CyanHighlight);
        DrawString(_font, new Vector2(220, 178), "选择相邻节点 · 方向键/点击选择 · [回车] 充能启程 · [ESC] 退出", fontSize: 12, modulate: ReassemblyUi.TextMuted);

        foreach (var offset in ChartOffsets)
        {
            var point = ChartPoint(offset);
            var cell = _chartOrigin + offset;
            if (offset != Vector2I.Zero) DrawLine(ChartPoint(Vector2I.Zero), point, ReassemblyUi.CyanGrid * 1.5f, 1.2f);
            var selected = cell == _warpSelection;
            var isOrigin = offset == Vector2I.Zero;

            var nodeColor = isOrigin ? ReassemblyUi.CyanHighlight : ReassemblyUi.TacticalAmber;
            DrawCircle(point, selected ? 10 : 6, nodeColor);
            if (selected)
            {
                DrawArc(point, 18, 0, Mathf.Tau, 24, ReassemblyUi.CyanHighlight, 1.5f);
                DrawArc(point, 24, 0, Mathf.Tau, 32, ReassemblyUi.CyanHighlight * 0.4f, 1.0f);
            }
            DrawString(_font, point + new Vector2(-65, 34), UniverseGenerator.SystemName(cell), HorizontalAlignment.Center, 130, 11, selected ? ReassemblyUi.TextWhite : ReassemblyUi.TextSteel);
        }

        var destination = UniverseGenerator.GenerateSystem(_warpSelection);
        var detailRect = new Rect2(760, 240, 310, 320);
        ReassemblyUi.DrawChamferFrame(this, detailRect, ReassemblyUi.VoidDark, ReassemblyUi.PanelBorder, 8.0f, false, 1.0f);

        DrawString(_font, new Vector2(780, 280), destination.Name, fontSize: 20, modulate: ReassemblyUi.TextWhite);
        DrawString(_font, new Vector2(780, 316), _warpSelection == _chartOrigin ? "当前位置 // 现驻节点" : "目标星区 // 航程可达", fontSize: 13, modulate: ReassemblyUi.CyanHighlight);
        DrawString(_font, new Vector2(780, 350), $"{destination.Bodies.Count(b => b.Kind == UniverseGenerator.CelestialKind.Planet)} 颗行星 / 2 座空间站", fontSize: 12, modulate: ReassemblyUi.TextSteel);
        DrawString(_font, new Vector2(780, 384), "进轨点：高轨道安全区", fontSize: 11, modulate: ReassemblyUi.TextMuted);

        var btnRect = new Rect2(780, 480, 270, 44);
        ReassemblyUi.DrawChamferFrame(this, btnRect, new Color("#1c4254"), ReassemblyUi.CyanHighlight, 6.0f, true, 1.2f);
        DrawString(_font, new Vector2(810, 508), "[回车]  启动迁跃引擎", fontSize: 14, modulate: ReassemblyUi.TextWhite);

        if (_noticeTime > 0) DrawString(_font, new Vector2(220, 595), _notice, fontSize: 12, modulate: ReassemblyUi.TacticalAmber);
    }
    private void DrawWarpEffect()
    {
        var moving = _warp.IsMoving;
        if (moving)
        {
            // World-position-driven parallax. No opaque overlay: real celestial
            // bodies, the ship and sector streaming stay visible throughout.
            var strength = _warp.Speed / WarpDrive.CruiseSpeed;
            var direction = _warp.Direction;
            var center = new Vector2(ViewWidth, ViewHeight)*0.5f;
            for (var i = 0; i < 100; i++)
            {
                var seed = unchecked((uint)(i+1)*2654435761u);
                seed = unchecked((seed ^ (seed>>16))*2246822519u);
                var parallax = 0.012f + (seed & 255)/255f*0.018f;
                var point = new Vector2(
                    Mathf.PosMod((seed & 65535)/65535f*ViewWidth - _visualPlayerPosition.X*parallax, ViewWidth),
                    Mathf.PosMod((seed>>16)/65535f*ViewHeight - _visualPlayerPosition.Y*parallax, ViewHeight));
                if (point.Y < 90 || point.Y > ViewHeight-150 || point.DistanceTo(center) < 55 || MiniMapRect.HasPoint(point)) continue;
                DrawLine(point-direction*(6+strength*75), point,
                    new Color(0.48f,0.84f,0.96f, strength*0.5f), 1, true);
            }
        }
        var label = _warp.State == WarpDrive.Phase.Charging ? "引擎充能 / 航向对准"
            : _warp.State == WarpDrive.Phase.Braking ? "正在减速退出"
            : moving ? (_warp.Speed > WarpDrive.CruiseSpeed*0.99f ? "高速巡航" : _warp.TravelledDistance < _warp.TotalDistance*0.5f ? "持续加速" : "接近目标 / 减速")
            : _warp.Aborted ? "已退出迁跃 / 引擎冷却" : "已抵达 / 引擎冷却";
        var barRect = new Rect2(340, 550, 600, 80);
        ReassemblyUi.DrawChamferFrame(this, barRect, ReassemblyUi.VoidDark, ReassemblyUi.CyanHighlight, 6, true, 1.2f);
        var status = _warp.LocksFlight ? $"{label} · {UniverseGenerator.SystemName(_warp.Destination)} · ESC 减速退出" : label;
        var telemetry = _warp.LocksFlight ? $"速度 {_warp.Speed:0} / s    剩余 {_warp.RemainingDistance:0}    预计 {_warp.TimeRemaining:0.0}s"
            : $"航行控制已恢复    冷却剩余 {Math.Max(0, WarpDrive.CooldownSeconds-_warp.Elapsed):0.0}s";
        DrawString(_font, new Vector2(360,574), status, fontSize: 13, modulate: ReassemblyUi.CyanHighlight);
        DrawString(_font, new Vector2(360,597), telemetry, fontSize: 13, modulate: ReassemblyUi.TextSteel);
        ReassemblyUi.DrawSegmentedMeter(this, new Vector2(360,610), new Vector2(560,8), _warp.Progress, ReassemblyUi.CyanHighlight, new Color("#163242"), 28, 2);
    }
}
