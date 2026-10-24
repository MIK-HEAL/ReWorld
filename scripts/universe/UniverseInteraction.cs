using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

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
    public WarpDrive Warp => _warp;
    public UniverseTraffic Traffic => _traffic;
    public bool IsDocked => _dockedStation is not null;
    public bool ChartOpen => _chartOpen;
    public bool TravelLocksBuilder => _warp.LocksFlight;
    public void CloseNavigationOverlays() => _chartOpen = false;
    public double FrameMedianMs => _median;
    public double FrameP95Ms => _p95;
    public int RetainedDrawCount => _retainedBodies.Values.Sum(v => v.DrawCount);

    private void InitializeWorldServices()
    {
        _bodyLayer = new Node2D { Name = "RetainedCelestials", ShowBehindParent = true };
        AddChild(_bodyLayer);
        _traffic.Load(UniverseGenerator.WorldToSystemCell(_playerPosition), _playerPosition);
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
        _noticeTime = Math.Max(0, _noticeTime - dt);
        _bodyLayer.Position = new Vector2(ViewWidth, ViewHeight) * 0.5f - _cameraPosition * _cameraZoom;
        _bodyLayer.Scale = Vector2.One * _cameraZoom;
        var half = new Vector2(ViewWidth, ViewHeight) * 0.5f / _cameraZoom;
        var rect = new Rect2(_cameraPosition - half, half * 2);
        foreach (var body in _visibleBodies)
            if (_retainedBodies.TryGetValue(body.Id, out var visual))
                visual.Visible = rect.Grow(body.Radius * 1.65f).HasPoint(body.Position);
        _frameTimes[_frameIndex++ % _frameTimes.Length] = GetProcessDeltaTime() * 1000;
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
        if (cell != _traffic.Cell)
        {
            _traffic.Load(cell, _playerPosition);
            foreach (var visual in _retainedBodies.Values) visual.QueueRedraw();
        }
        _traffic.Step(Math.Min(delta, 0.1f), _playerPosition, _playerHeading, _visibleBodies);
        var locked = _warp.LocksFlight;
        if (_warp.Advance(delta)) ArriveFromWarp();
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
            _playerVelocity = Vector2.Zero;
            _playerAngularVelocity = 0;
            _thrusterActivity.Clear();
            ResetFlightPresentation();
            return true;
        }
        // Solid station envelope; friendly vessels remain non-blocking to avoid traffic jams.
        foreach (var body in _visibleBodies)
        {
            if (body.Kind != UniverseGenerator.CelestialKind.Station) continue;
            var offset = _playerPosition - body.Position;
            var clearance = body.Radius + _flightProfile.BoundingRadius + 12;
            if (offset.LengthSquared() >= clearance * clearance) continue;
            var normal = offset.IsZeroApprox() ? Vector2.Right : offset.Normalized();
            _playerPosition = body.Position + normal * clearance;
            if (_playerVelocity.Dot(normal) < 0) _playerVelocity -= normal * _playerVelocity.Dot(normal);
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
        var clear = !_visibleBodies.Any(b => b.HasSurface && b.SurfaceDistance(_playerPosition) < _flightProfile.BoundingRadius + 180);
        if (!_warp.Engage(UniverseGenerator.WorldToSystemCell(_playerPosition), destination,
            _ship?.HasLiveCore == true, IsDocked, clear))
        { Notify("迁跃不可用：需有效核心、离站、离开天体表面并选择相邻星系，等待冷却结束"); return false; }
        _chartOpen = false;
        _playerVelocity = Vector2.Zero;
        _playerAngularVelocity = 0;
        ResetFlightPresentation();
        Notify("迁跃引擎充能 · Esc 取消");
        return true;
    }
    private void ArriveFromWarp()
    {
        var system = UniverseGenerator.GenerateSystem(_warp.Destination);
        var station = system.Bodies.First(b => b.Kind == UniverseGenerator.CelestialKind.Station);
        _targetBody = station;
        _playerPosition = DockingPosition(station, _flightProfile.BoundingRadius) + Vector2.Right * 500;
        _playerVelocity = Vector2.Zero;
        _playerHeading = Mathf.Pi;
        _playerAngularVelocity = 0;
        _traffic.Load(_warp.Destination, _playerPosition);
        _loadedSector = new Vector2I(int.MaxValue, int.MaxValue);
        RefreshVisibleBodies();
        ResetFlightPresentation();
        Notify($"已抵达 {system.Name} · 友方空间站已标记 · F 停靠");
    }

    private bool HandleWorldInput(InputEvent input)
    {
        if (input is InputEventKey { Pressed: true, Echo: false } key)
        {
            if (key.Keycode == Key.F3) { _showPerformance = !_showPerformance; return true; }
            if (key.Keycode == Key.Escape && (_chartOpen || _warp.State == WarpDrive.Phase.Charging))
            { _chartOpen = false; _warp.Cancel(); Notify("迁跃已取消"); return true; }
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
        if (_chartOpen && input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mouse)
        {
            foreach (var offset in ChartOffsets)
                if (ChartPoint(offset).DistanceTo(mouse.Position) < 40) _warpSelection = _chartOrigin + offset;
            if (new Rect2(820, 535, 240, 50).HasPoint(mouse.Position)) BeginWarp(_warpSelection);
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
            DrawCircle(position, 65, new Color("#66d9b0", 0.4f), false, 1 / _cameraZoom);
        }
    }
    private void DrawWorldOverlays()
    {
        foreach (var v in _traffic.Vessels)
        {
            var point = WorldToScreen(v.PreviousPosition.Lerp(v.Position, (float)Engine.GetPhysicsInterpolationFraction()));
            if (new Rect2(320, 90, 680, 470).HasPoint(point) && _cameraZoom > 0.18f)
                DrawString(_font, point + new Vector2(-60, -42), v.Name, HorizontalAlignment.Center, 120, 11, new Color("#66d9b0"));
            var map = MiniMapPoint(v.Position);
            if (MiniMapRect.Grow(-4).HasPoint(map)) DrawCircle(map, 2, new Color("#66d9b0"));
        }
        if (_noticeTime > 0 && !_chartOpen)
            DrawString(_font, new Vector2(330, 94), _notice, HorizontalAlignment.Center, 650, 14, new Color("#9ce6d5"));
        if (IsDocked)
        {
            DrawRect(new Rect2(350, 480, 580, 90), new Color("#0b2029", 0.95f));
            DrawString(_font, new Vector2(375, 514), "联航空间站 // 已停靠 · 维修完成", fontSize: 20, modulate: new Color("#9ce6d5"));
            DrawString(_font, new Vector2(375, 547), "M 改装飞船    F 离站", fontSize: 15, modulate: new Color("#b4c8d5"));
        }
        if (_showPerformance)
            DrawString(_font, new Vector2(340, 120), $"F3 帧时间 P50 {_median:0.0}ms / P95 {_p95:0.0}ms · {Engine.GetFramesPerSecond()} FPS · 静态绘制 {RetainedDrawCount}", fontSize: 13, modulate: Colors.LightGreen);
        if (_warp.State != WarpDrive.Phase.Ready) DrawWarpEffect();
        if (_chartOpen) DrawWarpChart();
    }
    private void DrawWarpChart()
    {
        DrawRect(new Rect2(0, 0, ViewWidth, ViewHeight), new Color("#020711", 0.94f));
        DrawRect(new Rect2(190, 120, 900, 500), new Color("#0a1525"));
        DrawString(_font, new Vector2(230, 164), "星系航图 / HYPERDRIVE", fontSize: 24, modulate: new Color("#9ce6d5"));
        DrawString(_font, new Vector2(230, 196), "选择相邻星系 · 方向键 / 点击选择 · Enter 充能 · Esc 返回", fontSize: 14, modulate: new Color("#89a4be"));
        foreach (var offset in ChartOffsets)
        {
            var point = ChartPoint(offset);
            var cell = _chartOrigin + offset;
            if (offset != Vector2I.Zero) DrawLine(ChartPoint(Vector2I.Zero), point, new Color("#254357"), 1);
            var selected = cell == _warpSelection;
            DrawCircle(point, selected ? 11 : 7, new Color(offset == Vector2I.Zero ? "#9ce6d5" : "#efc785"));
            if (selected) DrawCircle(point, 21, new Color("#9ce6d5"), false, 2, true);
            DrawString(_font, point + new Vector2(-65, 38), UniverseGenerator.SystemName(cell), HorizontalAlignment.Center, 130, 13, new Color("#b4c8d5"));
        }
        var destination = UniverseGenerator.GenerateSystem(_warpSelection);
        DrawString(_font, new Vector2(780, 290), destination.Name, fontSize: 22, modulate: new Color("#e3edf4"));
        DrawString(_font, new Vector2(780, 334), _warpSelection == _chartOrigin ? "当前位置" : "相邻星系 · 航程可达", fontSize: 15, modulate: new Color("#9ce6d5"));
        DrawString(_font, new Vector2(780, 370), $"{destination.Bodies.Count(b => b.Kind == UniverseGenerator.CelestialKind.Planet)} 颗行星 / 2 座友方站", fontSize: 15, modulate: new Color("#89a4be"));
        DrawString(_font, new Vector2(780, 408), "抵达点：空间站外安全空域", fontSize: 15, modulate: new Color("#89a4be"));
        DrawRect(new Rect2(820, 535, 240, 50), new Color("#1b5c64"));
        DrawString(_font, new Vector2(845, 568), "ENTER  启动迁跃", fontSize: 18, modulate: new Color("#d5faf0"));
        if (_noticeTime > 0) DrawString(_font, new Vector2(230, 600), _notice, fontSize: 12, modulate: new Color("#efc785"));
    }
    private void DrawWarpEffect()
    {
        var transit = _warp.State == WarpDrive.Phase.Transit;
        if (transit)
        {
            DrawRect(new Rect2(0, 0, ViewWidth, ViewHeight), new Color("#08172d", 0.96f));
            var center = new Vector2(ViewWidth, ViewHeight) * 0.5f;
            for (var i = 0; i < 90; i++)
            {
                var depth = Mathf.PosMod(i * 0.618034f + _warp.Elapsed * 0.7f, 1);
                var direction = Vector2.Right.Rotated(i * 2.3999632f);
                var start = center + direction * (25 + depth * depth * 850);
                DrawLine(start, start + direction * (10 + depth * depth * 140), new Color("#8edbf5", depth * 0.8f), 1 + depth * 2, true);
            }
        }
        var label = _warp.State == WarpDrive.Phase.Charging ? "迁跃引擎充能 · Esc 取消" : transit ? "迁跃航道" : "抵达 · 引擎冷却";
        DrawRect(new Rect2(380, 570, 520, 58), new Color("#071522", 0.95f));
        DrawString(_font, new Vector2(400, 595), $"{label} // {UniverseGenerator.SystemName(_warp.Destination)}", fontSize: 16, modulate: new Color("#9ce6d5"));
        DrawRect(new Rect2(400, 610, 480, 4), new Color("#234353"));
        DrawRect(new Rect2(400, 610, 480 * _warp.Progress, 4), new Color("#9ce6d5"));
    }
}
