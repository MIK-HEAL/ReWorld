using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// 无限宇宙探索视图：玩家在无边的 2D 太空中自由飞行。
/// 星球/空间站/小行星带按扇区程序化生成（靠近才计算），
/// 相机跟随 + 缩放，右下角小地图显示周边天体。
/// 星球采用航行尺度；预留轨道接近范围，地表场景另行接入。
/// </summary>
public partial class UniverseView : Node2D
{
    private const float ViewWidth = 1280.0f;
    private const float ViewHeight = 720.0f;

    private Vector2 _playerPosition = Vector2.Zero;
    private Vector2 _playerVelocity = Vector2.Zero;
    private float _playerHeading;
    private float _playerAngularVelocity;
    private Vector2 _previousPlayerPosition;
    private float _previousPlayerHeading;
    private Vector2 _visualPlayerPosition = Vector2.Zero;
    private float _visualPlayerHeading;

    private Vector2 _cameraPosition = Vector2.Zero;
    private float _cameraZoom = 0.65f;
    private const float ZoomMin = 0.008f;
    private const float ZoomMax = 2.5f;

    private readonly List<UniverseGenerator.CelestialBody> _visibleBodies = new();
    private sealed class BodyVisualCache
    {
        public Vector2[][] PlanetPatches = Array.Empty<Vector2[]>();
        public Vector2[] PlanetShadow = Array.Empty<Vector2>();
        public Vector2[] StationHex = Array.Empty<Vector2>();
        public Vector2[] StationOutline = Array.Empty<Vector2>();
        public Vector2[][] AsteroidRocks = Array.Empty<Vector2[]>();
    }
    private readonly Dictionary<string, BodyVisualCache> _bodyVisualCache = new();
    private UniverseGenerator.CelestialBody? _nearestBody;
    private UniverseGenerator.CelestialBody? _targetBody;
    private Vector2I _loadedSector = new(int.MaxValue, int.MaxValue);
    private int _loadedRadius = -1;
    private string _hoverBodyName = "";
    private float _currentGravity;   // 当前所受合引力加速度（用于 HUD 显示）。

    private Font _font = null!;
    private int _bodyRegenCounter;
    private readonly record struct BackgroundStar(Vector2 Position, float Size, Color Tint);
    private readonly List<BackgroundStar> _backgroundStars = new(450);
    private Vector2I _starSector = new(int.MaxValue, int.MaxValue);

    /// <summary>玩家舰体（来自模块化造舰编辑器）。未设置时只显示占位图形。</summary>
    private ModularShip? _ship;
    private readonly Dictionary<int, float> _thrusterActivity = new();
    private readonly ShipFlightController _pilot = new();
    private Vector2? _pointerScreen;
    private ShipFlightController.FlightProfile _flightProfile;

    /// <summary>设置玩家舰体（由 Main 从造舰编辑器传入）。</summary>
    public void SetShip(ModularShip ship)
    {
        _ship = ship;
        RefreshFlightProfile();
    }

    private void RefreshFlightProfile() => _flightProfile = _ship is null
        ? default : ShipFlightController.Capture(_ship);

    public void ResumeFlight()
    {
        _pointerScreen = GetViewport().GetMousePosition();
        _previousPlayerPosition = _visualPlayerPosition = _playerPosition;
        _previousPlayerHeading = _visualPlayerHeading = _playerHeading;
        if (_ship is not null && _flightProfile.Revision != _ship.Revision) RefreshFlightProfile();
    }
    public Vector2 PlayerPosition => _playerPosition;
    public Vector2 PlayerVelocity => _playerVelocity;
    public float PlayerHeading => _playerHeading;
    public float PlayerAngularVelocity => _playerAngularVelocity;
    public Vector2 RenderPosition => _visualPlayerPosition;
    public float RenderHeading => _visualPlayerHeading;
    public ShipFlightController.ControlScheme ControlScheme => _pilot.Scheme;
    public UniverseGenerator.CelestialBody? TargetBody => _targetBody;

    public override void _Ready()
    {
        _font = CreateUiFont();
        // 开局就在母星外侧，能看见巨大的星球弧面，也可沿切向绕行。
        var homeSystem = UniverseGenerator.GenerateSystem(Vector2I.Zero);
        var homeBodies = homeSystem.Bodies;
        _targetBody = homeBodies.First(body => body.Kind == UniverseGenerator.CelestialKind.Planet);
        _playerPosition = UniverseNavigation.SpawnNear(_targetBody, homeBodies, 60);
        _previousPlayerPosition = _playerPosition;
        _previousPlayerHeading = _playerHeading;
        _visualPlayerPosition = _playerPosition;
        _visualPlayerHeading = _playerHeading;
        _cameraPosition = _playerPosition;
        RenderingServer.SetDefaultClearColor(new Color("#04070d"));
        InitializeWorldServices();
        RefreshVisibleBodies();
        ResumeFlight();
    }

    public override void _Process(double delta)
    {
        var dt = (float)Math.Min(delta, 0.05);
        UpdateRenderTransform((float)Engine.GetPhysicsInterpolationFraction());
        UpdateCamera(dt);
        UpdateWorldPresentation(dt);

        // 天体生成有节流：每 8 帧刷新一次可见扇区，避免每帧全量重算。
        _bodyRegenCounter++;
        if (_bodyRegenCounter >= 8)
        {
            _bodyRegenCounter = 0;
            RefreshVisibleBodies();
        }

        QueueRedraw();
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_ship is not null && _flightProfile.Revision != _ship.Revision) RefreshFlightProfile();
        if (UpdateWorldPhysics((float)delta)) return;
        _previousPlayerPosition = _playerPosition;
        _previousPlayerHeading = _playerHeading;
        // 固定物理步进；高推力、小惯量舰体在低帧率下也能稳定反推制动。
        var elapsed = (float)Math.Min(delta, 0.1);
        var steps = Math.Max(1, Mathf.CeilToInt(elapsed * 120));
        var dt = elapsed / steps;
        var controls = _chartOpen ? default : ShipFlightController.ReadControls();
        for (var i = 0; i < steps; i++)
        {
            Vector2? aim = null;
            if (!_chartOpen && _pointerScreen is { } pointer && !MiniMapRect.HasPoint(pointer))
            {
                var fromShip = pointer - WorldToScreen(_visualPlayerPosition);
                if (fromShip.LengthSquared() > 16 * 16) aim = fromShip;
            }
            var command = _pilot.Resolve(controls, _playerHeading, aim);
            var acceleration = GravityAcceleration();
            var angularAcceleration = 0.0f;
            if (_ship is not null)
            {
                var result = _pilot.Evaluate(_flightProfile, command, _playerVelocity.Rotated(-_playerHeading),
                    _playerAngularVelocity, _thrusterActivity);
                acceleration += result.LocalForce.Rotated(_playerHeading) / Mathf.Max(1, _flightProfile.TotalMass);
                angularAcceleration = result.Torque / _flightProfile.MomentOfInertia;
            }
            else _thrusterActivity.Clear();
            ShipFlightController.Integrate(ref _playerPosition, ref _playerVelocity, ref _playerHeading,
                ref _playerAngularVelocity, acceleration, angularAcceleration, dt);
            var shipRadius = _ship is null ? 24 : _flightProfile.BoundingRadius;
            foreach (var body in _visibleBodies)
                UniverseNavigation.ResolveSurface(body, shipRadius, ref _playerPosition, ref _playerVelocity);
        }
    }

    internal void UpdateRenderTransform(float fraction)
    {
        var alpha = Mathf.Clamp(fraction, 0, 1);
        _visualPlayerPosition = _previousPlayerPosition.Lerp(_playerPosition, alpha);
        _visualPlayerHeading = Mathf.LerpAngle(_previousPlayerHeading, _playerHeading, alpha);
    }

    /// <summary>
    /// 牛顿引力（对齐 Reassembly 的 kGravityConstant 模型）：
    /// 加速度 a = G·M / r²，方向指向天体质心。多个引力井线性叠加。
    /// 用软核（softening）参数避免 r→0 时的奇点。
    /// </summary>
    private Vector2 GravityAcceleration()
    {
        _currentGravity = 0.0f;
        var total = Vector2.Zero;
        foreach (var body in _visibleBodies)
        {
            if (body.GravityRadius <= 0.0f || body.GravityStrength <= 0.0f)
                continue;

            var delta = body.Position - _playerPosition;
            var distance = delta.Length();
            if (distance <= 0.01f || distance > body.GravityRadius)
                continue;

            // 牛顿平方反比：a = G·M / r²。
            // 软化：r_soft = max(r, body.Radius)，避免贴脸时的数值爆炸。
            var softDistance = Mathf.Max(distance, body.Radius);
            var acceleration = body.GravityStrength / (softDistance * softDistance);
            total += delta.Normalized() * acceleration;
            _currentGravity += acceleration;
        }
        return total;
    }

    /// <summary>计算某天体的圆轨道速度（Reassembly 的 circularOrbitVel）。</summary>
    private float CircularOrbitVel(UniverseGenerator.CelestialBody body, float distance)
    {
        var r = Mathf.Max(distance, body.Radius);
        return Mathf.Sqrt(body.GravityStrength / r);
    }

    private void UpdateCamera(float dt)
    {
        // Ship-locked camera: the same interpolated pose drives ship and world. No second lag filter.
        _cameraPosition = _visualPlayerPosition;
    }

    public override void _Input(InputEvent @event)
    {
        if (HandleWorldInput(@event)) { GetViewport().SetInputAsHandled(); return; }
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.R } key &&
            !key.CtrlPressed && !key.AltPressed && !key.MetaPressed)
        {
            _pilot.CycleScheme(_playerHeading);
            GetViewport().SetInputAsHandled();
        }
        else if (@event is InputEventMouseButton mouse && mouse.Pressed)
        {
            _pointerScreen = mouse.Position;
            if (mouse.ButtonIndex == MouseButton.WheelUp)
            {
                _cameraZoom = Mathf.Clamp(_cameraZoom * 1.12f, ZoomMin, ZoomMax);
            }
            else if (mouse.ButtonIndex == MouseButton.WheelDown)
            {
                _cameraZoom = Mathf.Clamp(_cameraZoom / 1.12f, ZoomMin, ZoomMax);
            }
            else if (mouse.ButtonIndex == MouseButton.Left)
            {
                var world = ScreenToWorld(mouse.Position);
                var overMap = MiniMapRect.HasPoint(mouse.Position);
                _targetBody = _visibleBodies
                    .Where(b => overMap ? MiniMapPoint(b.Position).DistanceTo(mouse.Position) < 12 :
                        b.Position.DistanceTo(world) <= b.Radius + 12 / _cameraZoom)
                    .OrderBy(b => overMap ? MiniMapPoint(b.Position).DistanceTo(mouse.Position) : b.Position.DistanceTo(world))
                    .FirstOrDefault() ?? _targetBody;
            }
            RefreshVisibleBodies();
        }
        else if (@event is InputEventMouseMotion motion)
        {
            _pointerScreen = motion.Position;
            // 检测悬停的天体。
            _hoverBodyName = "";
            var world = ScreenToWorld(motion.Position);
            foreach (var body in _visibleBodies)
            {
                if (world.DistanceTo(body.Position) <= body.Radius)
                {
                    _hoverBodyName = body.Name;
                    break;
                }
            }
        }
    }

    private void RefreshVisibleBodies()
    {
        // 视野外也保留完整引力井；只在扇区或缩放范围改变时重新生成。
        var sector = UniverseGenerator.WorldToSector(_playerPosition);
        var reach = Mathf.Max(MiniMapRange, new Vector2(ViewWidth, ViewHeight).Length() * 0.5f / _cameraZoom)
            + UniverseGenerator.MaxGravityRadius;
        var sectorRadius = Math.Clamp(Mathf.CeilToInt(reach / UniverseGenerator.SectorSize), 1, 16);
        if (sector != _loadedSector || sectorRadius != _loadedRadius)
        {
            _visibleBodies.Clear();
            _visibleBodies.AddRange(UniverseGenerator.GetNearbyBodies(_playerPosition, sectorRadius));
            var ids = new HashSet<string>(_visibleBodies.Select(body => body.Id));
            foreach (var id in _bodyVisualCache.Keys.Where(id => !ids.Contains(id)).ToArray())
                _bodyVisualCache.Remove(id);
            _loadedSector = sector;
            _loadedRadius = sectorRadius;
            SyncRetainedBodies();
        }

        // 找最近的天体。
        _nearestBody = _visibleBodies
            .Where(body => body.Kind is UniverseGenerator.CelestialKind.Planet or UniverseGenerator.CelestialKind.Station)
            .OrderBy(body => body.SurfaceDistance(_playerPosition))
            .FirstOrDefault();
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
        // 深空背景。
        // Clear color is owned by the viewport; retained body children draw behind this overlay.

        // 世界空间绘制（星空 + 天体）。
        DrawSetTransform(new Vector2(ViewWidth, ViewHeight) * 0.5f - _cameraPosition * _cameraZoom, 0.0f, new Vector2(_cameraZoom, _cameraZoom));
        var halfView = new Vector2(ViewWidth, ViewHeight) * 0.5f / _cameraZoom;
        var worldView = new Rect2(_cameraPosition - halfView, halfView * 2);
        DrawStars(worldView);
        DrawSectorGrid();
        DrawGravityRanges(worldView);
        DrawTraffic(worldView);
        DrawPlayer();
        DrawSetTransform(Vector2.Zero, 0.0f, Vector2.One);

        DrawBodyLabels();
        if (_cameraZoom < 0.3f)
        {
            var player = WorldToScreen(_visualPlayerPosition);
            var forward = Vector2.Right.Rotated(_visualPlayerHeading);
            var side = forward.Orthogonal();
            DrawPolyline(new[] { player - forward * 5 + side * 4, player + forward * 7,
                player - forward * 5 - side * 4 }, new Color("#e1eceb"), 1.5f, true);
        }
        DrawHud();
        DrawMiniMap();
        DrawTargetIndicator();
        DrawWorldOverlays();
    }

    private void DrawStars(Rect2 worldView)
    {
        // 用扇区种子生成稳定的星空，让星空随移动平滑流动。
        var sector = UniverseGenerator.WorldToSector(_cameraPosition);
        if (sector != _starSector)
        {
            _backgroundStars.Clear();
            for (var dx = -2; dx <= 2; dx++)
            for (var dy = -2; dy <= 2; dy++)
            {
                var s = sector + new Vector2I(dx, dy);
                var seed = (ulong)((uint)s.X * 73856093UL ^ (uint)s.Y * 19349663UL);
                var rng = new Random((int)(seed & 0x7fffffff));
                var count = 18;
                for (var i = 0; i < count; i++)
                {
                    var local = new Vector2(
                        (float)(rng.NextDouble() - 0.5) * UniverseGenerator.SectorSize,
                        (float)(rng.NextDouble() - 0.5) * UniverseGenerator.SectorSize);
                    var world = UniverseGenerator.SectorToWorldCenter(s) + local;
                    var size = rng.Next(3) == 0 ? 2.0f : 1.0f;
                    var brightness = (float)(0.4 + rng.NextDouble() * 0.6);
                    _backgroundStars.Add(new BackgroundStar(world, size, new Color("#9cb2ce", brightness)));
                }
            }
            _starSector = sector;
        }
        var bounds = worldView.Grow(3);
        foreach (var star in _backgroundStars)
            if (bounds.HasPoint(star.Position)) DrawCircle(star.Position, star.Size, star.Tint);
    }

    private void DrawSectorGrid()
    {
        // 扇区边界淡线，帮助感知移动（可选，缩放较小时才显示）。
        if (_cameraZoom < 0.7f)
            return;
        var halfView = new Vector2(ViewWidth, ViewHeight) * 0.5f / _cameraZoom;
        var topLeft = _cameraPosition - halfView;
        var bottomRight = _cameraPosition + halfView;
        var startX = Mathf.Floor(topLeft.X / UniverseGenerator.SectorSize) * UniverseGenerator.SectorSize;
        var startY = Mathf.Floor(topLeft.Y / UniverseGenerator.SectorSize) * UniverseGenerator.SectorSize;
        var color = new Color("#152434", 0.5f);
        for (var x = startX; x <= bottomRight.X; x += UniverseGenerator.SectorSize)
            DrawLine(new Vector2(x, topLeft.Y), new Vector2(x, bottomRight.Y), color, 1.0f / _cameraZoom);
        for (var y = startY; y <= bottomRight.Y; y += UniverseGenerator.SectorSize)
            DrawLine(new Vector2(topLeft.X, y), new Vector2(bottomRight.X, y), color, 1.0f / _cameraZoom);
    }

    private void DrawCelestialBody(Node2D canvas, UniverseGenerator.CelestialBody body)
    {
        if (!_bodyVisualCache.TryGetValue(body.Id, out var cache))
        {
            cache = BuildBodyVisualCache(body);
            _bodyVisualCache.Add(body.Id, cache);
        }
        var position = Vector2.Zero;
        var color = new Color(body.ColorHex);

        switch (body.Kind)
        {
            case UniverseGenerator.CelestialKind.Star:
                // 恒星：发光光晕 + 核心。
                canvas.DrawCircle(position, body.Radius * 1.6f, new Color(color, 0.18f));
                canvas.DrawCircle(position, body.Radius, new Color(color, 0.85f));
                canvas.DrawCircle(position, body.Radius * 0.55f, new Color("#fff4d6"));
                break;

            case UniverseGenerator.CelestialKind.Planet:
                DrawPlanet(canvas, body);
                break;

            case UniverseGenerator.CelestialKind.Station:
                if (_traffic.Stations.TryGetValue(body.Id, out var station))
                    ModularShipVisual.Draw(canvas, station, position, 0);
                canvas.DrawArc(position, body.Radius + 70, 0.3f, Mathf.Tau - 0.3f, 64, new Color("#68d9b0", 0.5f), 2);
                canvas.DrawLine(position + Vector2.Right * body.Radius, position + Vector2.Right * (body.Radius + 120), new Color("#68d9b0"), 3);
                break;

            case UniverseGenerator.CelestialKind.AsteroidField:
                // 小行星带：散布小多边形。
                foreach (var rock in cache.AsteroidRocks)
                    canvas.DrawColoredPolygon(rock, new Color("#5a6878", 0.85f));
                break;
        }

    }

    private void DrawBodyLabels()
    {
        // 标签在屏幕空间绘制，航行视野拉远时仍可读。
        foreach (var body in _visibleBodies)
        {
            if (body.Kind == UniverseGenerator.CelestialKind.AsteroidField) continue;
            var point = WorldToScreen(body.Position) + Vector2.Down * (body.Radius * _cameraZoom + 20);
            if (point.X < 96 || point.X > ViewWidth - 96 || point.Y < 80 || point.Y > ViewHeight - 80) continue;
            if (point.X < 310 && point.Y < 230 || MiniMapRect.Grow(20).HasPoint(point)) continue;
            DrawString(_font, point - new Vector2(95, 0), body.Name, HorizontalAlignment.Center, 190, 12,
                new Color("#b7c7d4", 0.8f));
        }
    }
    private void DrawPlanet(Node2D canvas, UniverseGenerator.CelestialBody body)
    {
        var position = Vector2.Zero;
        var radius = body.Radius;
        var color = new Color(body.ColorHex);
        canvas.DrawCircle(position, radius * 1.035f, new Color("#7199b0", 0.07f));
        canvas.DrawCircle(position, radius * 1.012f, new Color("#9cc4d2", 0.15f));
        canvas.DrawCircle(position, radius, color.Darkened(0.12f));

        // 稳定的表面斑块；全部在球面圆盘内，不随扇区刷新闪动。
        if (!_bodyVisualCache.TryGetValue(body.Id, out var cache)) return;
        for (var i = 0; i < cache.PlanetPatches.Length; i++)
        {
            var points = cache.PlanetPatches[i];
            var patchColor = i % 3 == 0 ? color.Lightened(0.18f) : color.Darkened(0.24f);
            canvas.DrawColoredPolygon(points, patchColor);
        }

        // 圆盘边缘到晨昏线之间的月牙阴影，球体轮廓保持圆形。
        canvas.DrawColoredPolygon(cache.PlanetShadow, new Color("#020712", 0.65f));
        canvas.DrawArc(position, radius * 1.004f, Mathf.Pi * 0.78f, Mathf.Pi * 1.52f, 96,
            new Color("#bbd9e1", 0.6f), Mathf.Max(1 / _cameraZoom, radius * 0.0025f), true);
    }

    private static BodyVisualCache BuildBodyVisualCache(UniverseGenerator.CelestialBody body)
    {
        var cache = new BodyVisualCache();
        if (body.Kind == UniverseGenerator.CelestialKind.Planet)
        {
            var seed = 17;
            foreach (var c in body.Id) seed = unchecked(seed * 31 + c);
            var rng = new Random(seed);
            cache.PlanetPatches = new Vector2[18][];
            for (var i = 0; i < cache.PlanetPatches.Length; i++)
            {
                var angle = (float)rng.NextDouble() * Mathf.Tau;
                var distance = (float)rng.NextDouble() * body.Radius * 0.68f;
                var patchRadius = body.Radius * (0.06f + (float)rng.NextDouble() * 0.18f);
                var center = Vector2.Right.Rotated(angle) * distance;
                var points = cache.PlanetPatches[i] = new Vector2[12];
                for (var j = 0; j < points.Length; j++)
                    points[j] = center + Vector2.Right.Rotated(j * Mathf.Tau / points.Length) *
                        patchRadius * (0.6f + (float)rng.NextDouble() * 0.4f);
            }
            cache.PlanetShadow = new Vector2[128];
            for (var i = 0; i <= 64; i++)
            {
                var a = -Mathf.Pi * 0.5f + i * Mathf.Pi / 64;
                cache.PlanetShadow[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * body.Radius;
            }
            for (var i = 63; i > 0; i--)
            {
                var a = -Mathf.Pi * 0.5f + i * Mathf.Pi / 64;
                cache.PlanetShadow[128 - i] = new Vector2(Mathf.Cos(a) * 0.18f, Mathf.Sin(a)) * body.Radius;
            }
        }
        else if (body.Kind == UniverseGenerator.CelestialKind.Station)
        {
            cache.StationHex = new Vector2[6];
            cache.StationOutline = new Vector2[7];
            for (var i = 0; i < 6; i++)
            {
                var angle = -Mathf.Pi * 0.5f + i * Mathf.Tau / 6;
                cache.StationOutline[i] = cache.StationHex[i] = body.Position + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * body.Radius;
            }
            cache.StationOutline[6] = cache.StationOutline[0];
        }
        else if (body.Kind == UniverseGenerator.CelestialKind.AsteroidField)
        {
            var rng = new Random((int)(body.Position.X * 0.001f) ^ (int)(body.Position.Y * 0.001f));
            cache.AsteroidRocks = new Vector2[14][];
            for (var i = 0; i < cache.AsteroidRocks.Length; i++)
            {
                var angle = (float)(rng.NextDouble() * Mathf.Tau);
                var dist = (float)(rng.NextDouble() * body.Radius);
                var rockPos = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * dist;
                var rockRadius = 4.0f + (float)(rng.NextDouble() * 9.0f);
                var sides = rng.Next(5, 8);
                var rock = cache.AsteroidRocks[i] = new Vector2[sides];
                for (var s = 0; s < sides; s++)
                {
                    var a = (float)(s * Mathf.Tau / sides);
                    rock[s] = rockPos + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rockRadius;
                }
            }
        }
        return cache;
    }

    private void DrawPlayer()
    {
        var position = _visualPlayerPosition;
        if (_ship is not null && _ship.Blocks.Count > 0)
        {
            // 真实模块化舰体：按模块多边形渲染（与编辑器一致）。
            DrawSetTransform(WorldToScreen(position), 0, Vector2.One * _cameraZoom);
            ModularShipVisual.Draw(this, _ship, Vector2.Zero, _visualPlayerHeading, drawPorts: false, thrusterActivity: _thrusterActivity);
            return;
        }

        // 占位：未设置舰体时的小三角舰。
        var forward = new Vector2(Mathf.Cos(_visualPlayerHeading), Mathf.Sin(_visualPlayerHeading));
        var nose = position + forward * 18.0f;
        var back = position - forward * 14.0f;
        var side = forward.Rotated(Mathf.Pi * 0.5f) * 10.0f;
        DrawColoredPolygon(new[] { nose, back + side, back - side }, new Color("#7bb7e8"));
        DrawPolyline(new[] { nose, back + side, back - side, nose }, new Color("#f4f1de", 0.8f), 1.5f);

        // 速度向量。
        if (_playerVelocity.Length() > 10.0f)
        {
            var vel = _playerVelocity.Normalized() * Mathf.Clamp(_playerVelocity.Length() * 0.06f, 12.0f, 60.0f);
            DrawLine(position, position + vel, new Color("#78a6d8", 0.6f), 2.0f);
        }
    }

    private void DrawHud()
    {
        // 顶部坐标与速度信息。
        DrawRect(new Rect2(new Vector2(0, 0), new Vector2(ViewWidth, 70.0f)), new Color("#050c13", 0.94f));
        DrawLine(new Vector2(0, 69), new Vector2(ViewWidth, 69), new Color("#315260"), 1.0f);
        DrawString(_font, new Vector2(16, 17), "REWORLD // STARFORGE FRONTIER", HorizontalAlignment.Left, -1, 10, new Color("#77d6dc"));
        var sector = UniverseGenerator.WorldToSector(_playerPosition);
        var systemCell = UniverseGenerator.WorldToSystemCell(_playerPosition);
        var systemCenter = UniverseGenerator.SystemCenter(systemCell);
        var location = _playerPosition.DistanceTo(systemCenter) <= UniverseGenerator.SystemRadius
            ? UniverseGenerator.SystemName(systemCell) : "星际空域";
        DrawString(_font, new Vector2(16, 43), $"{location}   扇区 [{sector.X}, {sector.Y}]   坐标 ({_playerPosition.X:0}, {_playerPosition.Y:0})   速度 {_playerVelocity.Length():0}   缩放 {_cameraZoom:P0}",
            HorizontalAlignment.Left, -1, 13, new Color("#c7d8dc"));

        // 最近天体提示。
        if (_nearestBody is not null)
        {
            var distance = _nearestBody.SurfaceDistance(_playerPosition);
            var kindLabel = _nearestBody.Kind switch
            {
                UniverseGenerator.CelestialKind.Planet => "星球",
                UniverseGenerator.CelestialKind.Station => "空间站",
                _ => "天体",
            };
            DrawString(_font, new Vector2(16, 62), $"最近{kindLabel}：{_nearestBody.Name} · 距表面 {distance:0}",
                HorizontalAlignment.Left, -1, 11, new Color("#7893a1"));
        }

        // 引力状态指示。
        if (_currentGravity > 1.0f)
        {
            var gravityLabel = _currentGravity > 400.0f ? "⚠ 强引力（难以逃逸）" : (_currentGravity > 60.0f ? "引力作用中" : "弱引力");
            var gravityColor = _currentGravity > 400.0f ? new Color("#e8a07b") : new Color("#7bb7e8");
            DrawString(_font, new Vector2(16, 218), $"引力 {_currentGravity:0} · {gravityLabel}",
                HorizontalAlignment.Left, -1, 12, gravityColor);
        }

        // 悬停天体名。
        if (_hoverBodyName.Length > 0)
        {
            DrawString(_font, new Vector2(ViewWidth - 230, ViewHeight - 236), _hoverBodyName,
                HorizontalAlignment.Left, -1, 14, new Color("#f4f1de"));
        }

        // 操作提示。
        var driveHint = _pilot.Scheme == ShipFlightController.ControlScheme.Keyboard
            ? "W/S 推进 · A/D 转向 · Q/E 横移"
            : "WASD 移动 · 光标转向 · Q/E 横移";
        DrawString(_font, new Vector2(16, ViewHeight - 68), $"驾驶：{_pilot.SchemeLabel} · R 切换模式",
            HorizontalAlignment.Left, -1, 13, new Color("#83c7c0"));
        DrawString(_font, new Vector2(16, ViewHeight - 46), $"{driveHint} · M 造舰 · H 空间站 · F 停靠",
            HorizontalAlignment.Left, -1, 13, new Color("#6e8d9c"));
        DrawString(_font, new Vector2(16, ViewHeight - 24), "J 星系图 / 迁跃 · 滚轮缩放 · 左键选择 · F11 全屏",
            HorizontalAlignment.Left, -1, 12, new Color("#6e8d9c"));

        if (_targetBody is { } target)
        {
            DrawRect(new Rect2(16, 76, 290, 124), new Color("#0b151f", 0.88f));
            DrawRect(new Rect2(16, 76, 3, 124), new Color("#83c7c0"));
            DrawString(_font, new Vector2(30, 100), target.Name, HorizontalAlignment.Left, 262, 16, new Color("#e1eceb"));
            var kind = target.Kind == UniverseGenerator.CelestialKind.Planet ? "行星" : target.Kind == UniverseGenerator.CelestialKind.Station ? "空间站" : "天体";
            DrawString(_font, new Vector2(30, 124), $"{kind} · 直径 {target.Radius * 2:0} · 距表面 {target.SurfaceDistance(_playerPosition):0}",
                HorizontalAlignment.Left, 262, 12, new Color("#9bb2c3"));
            var inApproach = target.Position.DistanceTo(_playerPosition) <= target.ApproachRadius;
            DrawString(_font, new Vector2(30, 150), inApproach ? "已进入轨道接近区" : "深空航行 · 接近目标", HorizontalAlignment.Left, 262, 13, new Color("#83c7c0"));
            DrawString(_font, new Vector2(30, 180), target.Kind == UniverseGenerator.CelestialKind.Planet ? "虚线：轨道接近范围 · 沿切向绕行" : "滚轮拉远可查看周边天体",
                HorizontalAlignment.Left, 262, 12, new Color("#6e8d9c"));
        }
    }

    private const float MiniMapSize = 200;
    private float MiniMapRange => Mathf.Max(UniverseGenerator.SectorSize * 2, ViewWidth * 0.6f / _cameraZoom);
    private static readonly Vector2[] GravityRingDirections = CreateGravityRing();
    private Rect2 MiniMapRect => new(ViewWidth - MiniMapSize - 16, ViewHeight - MiniMapSize - 16, MiniMapSize, MiniMapSize);
    private Vector2 MiniMapPoint(Vector2 position) => MiniMapRect.GetCenter() + (position - _playerPosition) / MiniMapRange * MiniMapSize * 0.5f;

    private static Vector2[] CreateGravityRing()
    {
        var points = new Vector2[97];
        for (var i = 0; i < points.Length; i++) points[i] = Vector2.Right.Rotated(i * Mathf.Tau / 96);
        return points;
    }

    private Color GravityRangeColor(UniverseGenerator.CelestialBody body)
    {
        var selected = body.Id == _targetBody?.Id;
        var inside = body.Position.DistanceSquaredTo(_playerPosition) <= body.GravityRadius * body.GravityRadius;
        var color = body.Kind == UniverseGenerator.CelestialKind.Star ? new Color("#e8bd7b") : new Color("#7bb7e8");
        color.A = selected ? 0.55f : inside ? 0.32f : 0.20f;
        return color;
    }

    private void DrawGravityRanges(Rect2 worldView)
    {
        // 独立于天体本体裁剪：星球在屏幕外时，进入视野的引力边界也要显示。
        foreach (var body in _visibleBodies)
        {
            var radius = body.GravityRadius;
            if (radius <= 0 || body.GravityStrength <= 0) continue;
            var radiusSquared = radius * radius;
            var nearest = body.Position.Clamp(worldView.Position, worldView.End);
            if (nearest.DistanceSquaredTo(body.Position) > radiusSquared) continue;
            var farthest = (body.Position - worldView.GetCenter()).Abs() + worldView.Size * 0.5f;
            if (farthest.LengthSquared() < radiusSquared) continue;
            DrawCircle(body.Position, radius, GravityRangeColor(body), false,
                (body.Id == _targetBody?.Id ? 1.5f : 1.0f) / _cameraZoom, true);
        }
    }

    private void DrawMiniMapGravityRanges(Rect2 mapRect)
    {
        foreach (var body in _visibleBodies)
        {
            if (body.GravityRadius <= 0 || body.GravityStrength <= 0) continue;
            var center = MiniMapPoint(body.Position);
            var radius = body.GravityRadius / MiniMapRange * MiniMapSize * 0.5f;
            var nearest = center.Clamp(mapRect.Position, mapRect.End);
            if (nearest.DistanceSquaredTo(center) > radius * radius) continue;
            var color = GravityRangeColor(body);
            for (var i = 0; i < GravityRingDirections.Length - 1; i++)
            {
                var from = center + GravityRingDirections[i] * radius;
                var to = center + GravityRingDirections[i + 1] * radius;
                if (ClipMapLine(mapRect, ref from, ref to)) DrawLine(from, to, color, 1, true);
            }
        }
    }

    private static bool ClipMapLine(Rect2 rect, ref Vector2 from, ref Vector2 to)
    {
        var start = from;
        var delta = to - start;
        float enter = 0, exit = 1;
        bool Clip(float direction, float distance)
        {
            if (Mathf.Abs(direction) < 0.00001f) return distance >= 0;
            var fraction = distance / direction;
            if (direction < 0) enter = Mathf.Max(enter, fraction);
            else exit = Mathf.Min(exit, fraction);
            return enter <= exit;
        }
        if (!Clip(-delta.X, start.X - rect.Position.X) || !Clip(delta.X, rect.End.X - start.X) ||
            !Clip(-delta.Y, start.Y - rect.Position.Y) || !Clip(delta.Y, rect.End.Y - start.Y)) return false;
        from = start + delta * enter;
        to = start + delta * exit;
        return true;
    }

    private void DrawTargetIndicator()
    {
        if (_targetBody is not { } target) return;
        var point = WorldToScreen(target.Position);
        var clamped = point.Clamp(new Vector2(336, 92), new Vector2(ViewWidth - 238, ViewHeight - 100));
        if (clamped.DistanceTo(point) < 1)
            DrawCircle(point, 14, new Color("#83c7c0", 0.8f), false, 1);
        else
        {
            var direction = (point - clamped).Normalized();
            var side = direction.Orthogonal() * 5;
            DrawPolyline(new[] { clamped - direction * 10 + side, clamped, clamped - direction * 10 - side }, new Color("#83c7c0"), 2, true);
        }
        var labelLeft = clamped.X > ViewWidth * 0.5f ? clamped.X - 210 : clamped.X + 16;
        DrawString(_font, new Vector2(labelLeft, clamped.Y - 14), $"{target.Name} · {target.SurfaceDistance(_playerPosition):0}",
            HorizontalAlignment.Left, 210, 12, new Color("#83c7c0"));
    }

    private void DrawMiniMap()
    {
        var mapSize = MiniMapSize;
        var mapRect = MiniMapRect;
        var mapPos = mapRect.Position;
        DrawRect(mapRect, new Color("#0b151f", 0.92f));
        DrawRect(mapRect, new Color("#2d5263", 0.8f), false, 1.0f);
        DrawMiniMapGravityRanges(mapRect.Grow(-1));

        // 近景显示局部，拉远时一起扩展到完整星系及周边空域。
        var mapRange = MiniMapRange;
        var mapCenter = mapPos + new Vector2(mapSize, mapSize) * 0.5f;

        foreach (var body in _visibleBodies)
        {
            var delta = body.Position - _playerPosition;
            var norm = delta / mapRange;
            var mapPoint = mapCenter + norm * (mapSize * 0.5f);
            if (mapPoint.X < mapPos.X || mapPoint.X > mapPos.X + mapSize ||
                mapPoint.Y < mapPos.Y || mapPoint.Y > mapPos.Y + mapSize)
                continue;
            var dotColor = body.Kind switch
            {
                UniverseGenerator.CelestialKind.Planet => new Color("#d68b59"),
                UniverseGenerator.CelestialKind.Station => new Color("#7bb7e8"),
                UniverseGenerator.CelestialKind.Star => new Color("#f2cc60"),
                _ => new Color("#5a6878"),
            };
            var dotRadius = Mathf.Clamp(body.Radius / mapRange * mapSize * 0.5f, 2, 16);
            DrawCircle(mapPoint, dotRadius, dotColor);
            if (body.Id == _targetBody?.Id)
                DrawCircle(mapPoint, dotRadius + 3, new Color("#83c7c0"), false, 1);
        }

        // 玩家位置（小地图中心）。
        DrawCircle(mapCenter, 3.0f, new Color("#f4f1de"));

        DrawString(_font, mapPos + new Vector2(4, -16), "星图 · 圆环为引力范围", HorizontalAlignment.Left, -1, 12, new Color("#7f93a8"));
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
