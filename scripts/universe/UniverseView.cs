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

    private Vector2 _cameraPosition = Vector2.Zero;
    private float _cameraZoom = 0.65f;
    private const float ZoomMin = 0.06f;
    private const float ZoomMax = 2.5f;

    private readonly List<UniverseGenerator.CelestialBody> _visibleBodies = new();
    private UniverseGenerator.CelestialBody? _nearestBody;
    private UniverseGenerator.CelestialBody? _targetBody;
    private Vector2I _loadedSector = new(int.MaxValue, int.MaxValue);
    private int _loadedRadius = -1;
    private string _hoverBodyName = "";
    private float _currentGravity;   // 当前所受合引力加速度（用于 HUD 显示）。

    private Font _font = null!;
    private ulong _starSeed = 12345;
    private int _bodyRegenCounter;

    /// <summary>玩家舰体（来自模块化造舰编辑器）。未设置时回退到占位三角形。</summary>
    private ModularShip? _ship;
    private readonly Dictionary<int, float> _thrusterActivity = new();

    /// <summary>设置玩家舰体（由 Main 从造舰编辑器传入）。</summary>
    public void SetShip(ModularShip ship)
    {
        _ship = ship;
    }

    /// <summary>当前舰体的推进器挂点（基于模块位置 + 朝向）。</summary>
    private List<ModularShip.ThrusterMount> ThrusterMounts =>
        _ship?.GetThrusterMounts() ?? new List<ModularShip.ThrusterMount>();

    private float ShipTotalThrust => _ship?.TotalThrust ?? 0.0f;
    private float ShipTotalMass => _ship?.TotalMass ?? 1.0f;
    public Vector2 PlayerPosition => _playerPosition;
    public UniverseGenerator.CelestialBody? TargetBody => _targetBody;

    public override void _Ready()
    {
        _font = CreateUiFont();
        // 开局就在母星外侧，能看见巨大的星球弧面，也可沿切向绕行。
        var homeBodies = UniverseGenerator.GenerateSector(Vector2I.Zero);
        _targetBody = homeBodies[0];
        _playerPosition = UniverseNavigation.SpawnNear(_targetBody, homeBodies, 60);
        _cameraPosition = _playerPosition;
        RefreshVisibleBodies();
    }

    public override void _Process(double delta)
    {
        var dt = (float)Math.Min(delta, 0.05);
        HandleFlightInput(dt);
        UpdateCamera(dt);

        // 天体生成有节流：每 8 帧刷新一次可见扇区，避免每帧全量重算。
        _bodyRegenCounter++;
        if (_bodyRegenCounter >= 8)
        {
            _bodyRegenCounter = 0;
            RefreshVisibleBodies();
        }

        QueueRedraw();
    }

    private void HandleFlightInput(float dt)
    {
        float throttle = 0.0f;
        if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) throttle += 1.0f;
        if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) throttle -= 1.0f;

        float yaw = 0.0f;
        if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) yaw -= 1.0f;
        if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) yaw += 1.0f;

        // 朝向（俯视 2D）：heading 是弧度。
        var forward = new Vector2(Mathf.Cos(_playerHeading), Mathf.Sin(_playerHeading));

        // 推力：按模块推进器挂点施力（对齐 Reassembly「推进器在挂点施力」）。
        // 有真实舰体时用模块推进器 + 差动转向；否则回退到占位三角的固定推力。
        if (_ship is not null)
        {
            ApplyModuleThrust(throttle, yaw, dt);
        }
        else
        {
            var thrust = forward * throttle * 520.0f;
            _playerVelocity += thrust * dt;
            _playerAngularVelocity = Mathf.Lerp(_playerAngularVelocity, yaw * 2.6f, 0.18f);
        }

        // 引力：仿 Reassembly 的 kGravityConstant 牛顿引力。
        ApplyGravity(dt);

        _playerHeading += _playerAngularVelocity * dt;

        // 阻尼 + 最大速度。
        _playerVelocity = _playerVelocity.MoveToward(Vector2.Zero, 60.0f * dt);
        var maxSpeed = 900.0f;
        if (_playerVelocity.Length() > maxSpeed)
            _playerVelocity = _playerVelocity.Normalized() * maxSpeed;

        _playerPosition += _playerVelocity * dt;
        var shipRadius = _ship?.BoundingRadius() ?? 24;
        foreach (var body in _visibleBodies)
            UniverseNavigation.ResolveSurface(body, shipRadius, ref _playerPosition, ref _playerVelocity);
    }

    /// <summary>按模块推进器施力：推力沿各挂点方向，偏轴推进器产生转向力矩。</summary>
    private void ApplyModuleThrust(float throttle, float yaw, float dt)
    {
        if (_ship is null) return;
        var result = ShipPropulsion.Evaluate(_ship, ShipPropulsion.ReadTranslation(), yaw, _thrusterActivity);
        _playerVelocity += result.LocalForce.Rotated(_playerHeading) / Mathf.Max(1, ShipTotalMass) * dt;
        _playerAngularVelocity += result.Torque / _ship.MomentOfInertia() * dt;
        _playerAngularVelocity *= Mathf.Exp(-2.5f * dt);
        _playerAngularVelocity = Mathf.Clamp(_playerAngularVelocity, -4.2f, 4.2f);
    }

    /// <summary>推进器挂点作为活动映射的键（用方块 Id）。</summary>
    private static int KeyFor(ModularShip.ThrusterMount mount) => mount.BlockId;

    /// <summary>
    /// 牛顿引力（对齐 Reassembly 的 kGravityConstant 模型）：
    /// 加速度 a = G·M / r²，方向指向天体质心。多个引力井线性叠加。
    /// 用软核（softening）参数避免 r→0 时的奇点。
    /// </summary>
    private void ApplyGravity(float dt)
    {
        _currentGravity = 0.0f;
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
            _playerVelocity += delta.Normalized() * acceleration * dt;
            _currentGravity += acceleration;
        }
    }

    /// <summary>计算某天体的圆轨道速度（Reassembly 的 circularOrbitVel）。</summary>
    private float CircularOrbitVel(UniverseGenerator.CelestialBody body, float distance)
    {
        var r = Mathf.Max(distance, body.Radius);
        return Mathf.Sqrt(body.GravityStrength / r);
    }

    private void UpdateCamera(float dt)
    {
        _cameraPosition = _cameraPosition.Lerp(_playerPosition, Mathf.Clamp(dt * 6.0f, 0.0f, 1.0f));
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouse && mouse.Pressed)
        {
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
        var reach = new Vector2(ViewWidth, ViewHeight).Length() * 0.5f / _cameraZoom + UniverseGenerator.StarRadiusMax * 2.6f;
        var sectorRadius = Math.Clamp(Mathf.CeilToInt(reach / UniverseGenerator.SectorSize), 1, 6);
        if (sector != _loadedSector || sectorRadius != _loadedRadius)
        {
            _visibleBodies.Clear();
            _visibleBodies.AddRange(UniverseGenerator.GetNearbyBodies(_playerPosition, sectorRadius));
            _loadedSector = sector;
            _loadedRadius = sectorRadius;
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
        DrawRect(new Rect2(Vector2.Zero, new Vector2(ViewWidth, ViewHeight)), new Color("#04070d"));

        // 世界空间绘制（星空 + 天体）。
        DrawSetTransform(new Vector2(ViewWidth, ViewHeight) * 0.5f - _cameraPosition * _cameraZoom, 0.0f, new Vector2(_cameraZoom, _cameraZoom));
        DrawStars();
        DrawSectorGrid();
        var halfView = new Vector2(ViewWidth, ViewHeight) * 0.5f / _cameraZoom;
        var worldView = new Rect2(_cameraPosition - halfView, halfView * 2);
        foreach (var body in _visibleBodies)
            if (worldView.Grow(body.Radius * 1.65f).HasPoint(body.Position)) DrawCelestialBody(body);
        DrawPlayer();
        DrawSetTransform(Vector2.Zero, 0.0f, Vector2.One);

        DrawBodyLabels();
        if (_cameraZoom < 0.3f)
        {
            var player = WorldToScreen(_playerPosition);
            var forward = Vector2.Right.Rotated(_playerHeading);
            var side = forward.Orthogonal();
            DrawPolyline(new[] { player - forward * 5 + side * 4, player + forward * 7,
                player - forward * 5 - side * 4 }, new Color("#e1eceb"), 1.5f, true);
        }
        DrawHud();
        DrawMiniMap();
        DrawTargetIndicator();
    }

    private void DrawStars()
    {
        // 用扇区种子生成稳定的星空，让星空随移动平滑流动。
        var sector = UniverseGenerator.WorldToSector(_cameraPosition);
        for (var dx = -2; dx <= 2; dx++)
        {
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
                    DrawCircle(world, size, new Color("#9cb2ce", brightness));
                }
            }
        }
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

    private void DrawCelestialBody(UniverseGenerator.CelestialBody body)
    {
        var position = body.Position;
        var color = new Color(body.ColorHex);

        // 引力边界轻描，不再用大片蓝色同心圆盖住星球表面。
        if (body.GravityRadius > 0.0f && body.Id == _targetBody?.Id)
        {
            DrawCircle(position, body.GravityRadius, new Color("#7bb7e8", 0.10f), false, 1.0f / _cameraZoom);
        }

        switch (body.Kind)
        {
            case UniverseGenerator.CelestialKind.Star:
                // 恒星：发光光晕 + 核心。
                DrawCircle(position, body.Radius * 1.6f, new Color(color, 0.18f));
                DrawCircle(position, body.Radius, new Color(color, 0.85f));
                DrawCircle(position, body.Radius * 0.55f, new Color("#fff4d6"));
                break;

            case UniverseGenerator.CelestialKind.Planet:
                DrawPlanet(body);
                break;

            case UniverseGenerator.CelestialKind.Station:
                // 空间站：六边形 + 核心。
                var hex = new Vector2[6];
                for (var i = 0; i < 6; i++)
                {
                    var angle = -Mathf.Pi * 0.5f + i * Mathf.Tau / 6;
                    hex[i] = position + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * body.Radius;
                }
                DrawColoredPolygon(hex, color);
                DrawPolyline(hex.Append(hex[0]).ToArray(), new Color("#d8e0e7", 0.7f), 1.2f);
                DrawCircle(position, body.Radius * 0.35f, new Color("#f4f1de"));
                break;

            case UniverseGenerator.CelestialKind.AsteroidField:
                // 小行星带：散布小多边形。
                var rng = new Random((int)(body.Position.X * 0.001f) ^ (int)(body.Position.Y * 0.001f));
                for (var i = 0; i < 14; i++)
                {
                    var angle = (float)(rng.NextDouble() * Mathf.Tau);
                    var dist = (float)(rng.NextDouble() * body.Radius);
                    var rockPos = position + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * dist;
                    var rockRadius = 4.0f + (float)(rng.NextDouble() * 9.0f);
                    var sides = rng.Next(5, 8);
                    var rock = new Vector2[sides];
                    for (var s = 0; s < sides; s++)
                    {
                        var a = (float)(s * Mathf.Tau / sides);
                        rock[s] = rockPos + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rockRadius;
                    }
                    DrawColoredPolygon(rock, new Color("#5a6878", 0.85f));
                }
                break;
        }

        if (body.Kind == UniverseGenerator.CelestialKind.Planet && body.Id == _targetBody?.Id)
        {
            var orbitColor = new Color("#83c7c0", 0.35f);
            for (var i = 0; i < 48; i++)
                DrawArc(position, body.ApproachRadius, i * Mathf.Tau / 48, (i + 0.52f) * Mathf.Tau / 48, 4, orbitColor, 1 / _cameraZoom);
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

    private void DrawPlanet(UniverseGenerator.CelestialBody body)
    {
        var position = body.Position;
        var radius = body.Radius;
        var color = new Color(body.ColorHex);
        DrawCircle(position, radius * 1.035f, new Color("#7199b0", 0.07f));
        DrawCircle(position, radius * 1.012f, new Color("#9cc4d2", 0.15f));
        DrawCircle(position, radius, color.Darkened(0.12f));

        // 稳定的表面斑块；全部在球面圆盘内，不随扇区刷新闪动。
        var seed = 17;
        foreach (var c in body.Id) seed = unchecked(seed * 31 + c);
        var rng = new Random(seed);
        for (var i = 0; i < 18; i++)
        {
            var angle = (float)rng.NextDouble() * Mathf.Tau;
            var distance = (float)rng.NextDouble() * radius * 0.68f;
            var patchRadius = radius * (0.06f + (float)rng.NextDouble() * 0.18f);
            var center = position + Vector2.Right.Rotated(angle) * distance;
            var points = new Vector2[12];
            for (var j = 0; j < points.Length; j++)
                points[j] = center + Vector2.Right.Rotated(j * Mathf.Tau / points.Length) *
                    patchRadius * (0.6f + (float)rng.NextDouble() * 0.4f);
            var patchColor = i % 3 == 0 ? color.Lightened(0.18f) : color.Darkened(0.24f);
            DrawColoredPolygon(points, patchColor);
        }

        // 圆盘边缘到晨昏线之间的月牙阴影，球体轮廓保持圆形。
        var shadow = new List<Vector2>();
        for (var i = 0; i <= 64; i++)
        {
            var a = -Mathf.Pi * 0.5f + i * Mathf.Pi / 64;
            shadow.Add(position + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius);
        }
        for (var i = 63; i > 0; i--)
        {
            var a = -Mathf.Pi * 0.5f + i * Mathf.Pi / 64;
            shadow.Add(position + new Vector2(Mathf.Cos(a) * 0.18f, Mathf.Sin(a)) * radius);
        }
        DrawColoredPolygon(shadow.ToArray(), new Color("#020712", 0.65f));
        DrawArc(position, radius * 1.004f, Mathf.Pi * 0.78f, Mathf.Pi * 1.52f, 96,
            new Color("#bbd9e1", 0.6f), Mathf.Max(1 / _cameraZoom, radius * 0.0025f), true);
    }

    private void DrawPlayer()
    {
        var position = _playerPosition;
        if (_ship is not null && _ship.Blocks.Count > 0)
        {
            // 真实模块化舰体：按模块多边形渲染（与编辑器一致）。
            ModularShipVisual.Draw(this, _ship, position, _playerHeading, drawPorts: false, thrusterActivity: _thrusterActivity);
            return;
        }

        // 占位：未设置舰体时的小三角舰。
        var forward = new Vector2(Mathf.Cos(_playerHeading), Mathf.Sin(_playerHeading));
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
        DrawRect(new Rect2(new Vector2(0, 0), new Vector2(ViewWidth, 62.0f)), new Color("#04070d", 0.85f));
        var sector = UniverseGenerator.WorldToSector(_playerPosition);
        DrawString(_font, new Vector2(16, 24), $"扇区 [{sector.X}, {sector.Y}]   坐标 ({_playerPosition.X:0}, {_playerPosition.Y:0})   速度 {_playerVelocity.Length():0}   缩放 {_cameraZoom:P0}",
            HorizontalAlignment.Left, -1, 14, new Color("#b7c7d4"));

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
            DrawString(_font, new Vector2(16, 46), $"最近{kindLabel}：{_nearestBody.Name} · 距表面 {distance:0}",
                HorizontalAlignment.Left, -1, 12, new Color("#7f93a8"));
        }

        // 引力状态指示（数值对齐实际量级：推力≈520）。
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
        DrawString(_font, new Vector2(16, ViewHeight - 46), "W/S 推进 · A/D 转向 · Q/E 平移 · M 造舰",
            HorizontalAlignment.Left, -1, 13, new Color("#6e8d9c"));
        DrawString(_font, new Vector2(16, ViewHeight - 24), "滚轮：近景 / 航行视野 · 左键选择天体 · F11 / Alt+Enter 全屏",
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
    private const float MiniMapRange = UniverseGenerator.SectorSize * 2;
    private Rect2 MiniMapRect => new(ViewWidth - MiniMapSize - 16, ViewHeight - MiniMapSize - 16, MiniMapSize, MiniMapSize);
    private Vector2 MiniMapPoint(Vector2 position) => MiniMapRect.GetCenter() + (position - _playerPosition) / MiniMapRange * MiniMapSize * 0.5f;

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

        // 小地图范围 = 玩家周围 2 个扇区。
        var mapRange = UniverseGenerator.SectorSize * 2.0f;
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

        DrawString(_font, mapPos + new Vector2(4, -16), "星图", HorizontalAlignment.Left, -1, 12, new Color("#7f93a8"));
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
