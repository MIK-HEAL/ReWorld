using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// 无限宇宙探索视图：玩家在无边的 2D 太空中自由飞行。
/// 星球/空间站/小行星带按扇区程序化生成（靠近才计算），
/// 相机跟随 + 缩放，右下角小地图显示周边天体。
/// 纯宇宙探索，不含降落/工厂（本轮交付范围）。
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
    private float _cameraZoom = 1.0f;
    private const float ZoomMin = 0.25f;
    private const float ZoomMax = 2.5f;

    private readonly List<UniverseGenerator.CelestialBody> _visibleBodies = new();
    private UniverseGenerator.CelestialBody? _nearestBody;
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

    public override void _Ready()
    {
        _font = CreateUiFont();
        // 玩家初始在原点附近。
        _cameraPosition = _playerPosition;
        RefreshVisibleBodies();
    }

    public override void _Process(double delta)
    {
        var dt = (float)delta;
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
        if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) throttle -= 0.5f;

        float yaw = 0.0f;
        if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) yaw -= 1.0f;
        if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) yaw += 1.0f;

        // 朝向（俯视 2D）：heading 是弧度。
        var forward = new Vector2(Mathf.Cos(_playerHeading), Mathf.Sin(_playerHeading));

        // 推力：按模块推进器挂点施力（对齐 Reassembly「推进器在挂点施力」）。
        // 有真实舰体时用模块推进器 + 差动转向；否则回退到占位三角的固定推力。
        if (_ship is not null && ShipTotalThrust > 0.0f)
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
    }

    /// <summary>按模块推进器施力：推力沿各挂点方向，偏轴推进器产生转向力矩。</summary>
    private void ApplyModuleThrust(float throttle, float yaw, float dt)
    {
        var mounts = ThrusterMounts;
        var forceSum = Vector2.Zero;
        var torqueSum = 0.0f;

        foreach (var mount in mounts)
        {
            var worldForce = mount.Force.Rotated(_playerHeading);
            var activation = 0.0f;

            // 前进：点火力方向与船头（+X 世界朝向）同向的推进器。
            if (throttle > 0.0f)
            {
                var forwardAlign = Mathf.Max(0.0f, worldForce.Dot(Vector2.Right.Rotated(_playerHeading)));
                activation = Mathf.Max(activation, forwardAlign * throttle);
            }
            else if (throttle < 0.0f)
            {
                var reverseAlign = Mathf.Max(0.0f, worldForce.Dot(-Vector2.Right.Rotated(_playerHeading)));
                activation = Mathf.Max(activation, reverseAlign * -throttle);
            }

            // 转向：偏轴推进器差动点火，产生力矩。
            if (yaw != 0.0f)
            {
                var steerTorque = mount.Offset.X * worldForce.Y - mount.Offset.Y * worldForce.X;
                if (steerTorque * yaw > 0.0f)
                    activation = Mathf.Max(activation, Mathf.Min(1.0f, Mathf.Abs(steerTorque) * 0.05f) * Mathf.Abs(yaw));
            }

            if (activation <= 0.0f)
            {
                _thrusterActivity[KeyFor(mount)] = 0.0f;
                continue;
            }

            forceSum += worldForce * (mount.Power * activation);
            var torque = mount.Offset.X * worldForce.Y - mount.Offset.Y * worldForce.X;
            torqueSum += torque * mount.Power * activation;
            _thrusterActivity[KeyFor(mount)] = activation;
        }

        var massResponse = Mathf.Clamp(34.0f / Mathf.Max(1.0f, ShipTotalMass), 0.35f, 1.35f);
        if (forceSum.LengthSquared() > 0.0001f)
        {
            var acceleration = forceSum.Normalized() * (forceSum.Length() / Mathf.Max(1.0f, ShipTotalMass)) * massResponse * 22.0f;
            _playerVelocity += acceleration * dt;
        }

        if (Mathf.Abs(torqueSum) > 0.0001f)
        {
            var angularAccel = Mathf.Clamp(torqueSum / Mathf.Max(1.0f, ShipTotalMass), -6.0f, 6.0f) * massResponse * 0.35f;
            _playerAngularVelocity += angularAccel * dt;
        }
        else
        {
            _playerAngularVelocity = Mathf.Lerp(_playerAngularVelocity, yaw * 2.6f, 0.18f);
        }
    }

    /// <summary>推进器挂点作为活动映射的键（用其偏移量哈希，简化处理）。</summary>
    private static int KeyFor(ModularShip.ThrusterMount mount) =>
        (mount.Offset.X * 1000).GetHashCode() ^ (mount.Offset.Y * 1000).GetHashCode();

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
        _visibleBodies.Clear();
        // 根据当前缩放决定可见扇区半径：缩得越远，加载越远的天体。
        var sectorRadius = (int)Mathf.Clamp(Mathf.Ceil(3.0f / _cameraZoom), 1, 6);
        _visibleBodies.AddRange(UniverseGenerator.GetNearbyBodies(_playerPosition, sectorRadius));

        // 找最近的天体。
        _nearestBody = _visibleBodies
            .Where(body => body.Kind is UniverseGenerator.CelestialKind.Planet or UniverseGenerator.CelestialKind.Station)
            .OrderBy(body => body.Position.DistanceTo(_playerPosition))
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
        foreach (var body in _visibleBodies)
            DrawCelestialBody(body);
        DrawPlayer();
        DrawSetTransform(Vector2.Zero, 0.0f, Vector2.One);

        DrawHud();
        DrawMiniMap();
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

        // 引力井：多层同心圆，越靠近中心越亮，直观展示引力范围与强度。
        if (body.GravityRadius > 0.0f)
        {
            var rings = 5;
            for (var i = rings; i >= 1; i--)
            {
                var r = body.GravityRadius * i / rings;
                var alpha = 0.10f * (1.0f - (float)(i - 1) / rings);
                DrawCircle(position, r, new Color("#7bb7e8", alpha));
            }
            DrawCircle(position, body.GravityRadius, new Color("#7bb7e8", 0.35f), false, 1.0f / _cameraZoom);
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
                // 星球：球体 + 环/大气层。
                DrawCircle(position, body.Radius, color);
                DrawCircle(position, body.Radius, new Color(0, 0, 0, 0.35f), false, 1.5f);
                DrawArc(position, body.Radius * 1.25f, -0.4f, Mathf.Pi + 0.4f, 40, new Color("#7a9cb0", 0.5f), 1.5f);
                // 高光。
                DrawCircle(position + new Vector2(-body.Radius * 0.3f, -body.Radius * 0.3f), body.Radius * 0.25f, new Color(1, 1, 1, 0.25f));
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

        // 名称标签（缩放足够近时）。
        if (_cameraZoom > 0.55f || body.Kind == UniverseGenerator.CelestialKind.Planet)
        {
            DrawString(_font, position + new Vector2(-body.Radius, body.Radius + 16.0f / _cameraZoom),
                body.Name, HorizontalAlignment.Left, -1, (int)Mathf.Clamp(12.0f / _cameraZoom, 9, 22),
                new Color("#b7c7d4", 0.9f));
        }
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
        DrawRect(new Rect2(new Vector2(0, 0), new Vector2(ViewWidth, 40.0f)), new Color("#04070d", 0.85f));
        var sector = UniverseGenerator.WorldToSector(_playerPosition);
        DrawString(_font, new Vector2(16, 12), $"扇区 [{sector.X}, {sector.Y}]   坐标 ({_playerPosition.X:0}, {_playerPosition.Y:0})   速度 {_playerVelocity.Length():0}   缩放 {_cameraZoom:P0}",
            HorizontalAlignment.Left, -1, 14, new Color("#b7c7d4"));

        // 最近天体提示。
        if (_nearestBody is not null)
        {
            var distance = _nearestBody.Position.DistanceTo(_playerPosition);
            var kindLabel = _nearestBody.Kind switch
            {
                UniverseGenerator.CelestialKind.Planet => "星球",
                UniverseGenerator.CelestialKind.Station => "空间站",
                _ => "天体",
            };
            DrawString(_font, new Vector2(16, 34), $"最近{kindLabel}：{_nearestBody.Name} · {distance:0} 单位",
                HorizontalAlignment.Left, -1, 12, new Color("#7f93a8"));
        }

        // 引力状态指示。
        if (_currentGravity > 5.0f)
        {
            var gravityLabel = _currentGravity > 800.0f ? "⚠ 强引力" : (_currentGravity > 150.0f ? "引力作用中" : "弱引力");
            var gravityColor = _currentGravity > 800.0f ? new Color("#e8a07b") : new Color("#7bb7e8");
            DrawString(_font, new Vector2(16, 54), $"引力 {_currentGravity:0} m/s² · {gravityLabel}",
                HorizontalAlignment.Left, -1, 12, gravityColor);
        }

        // 悬停天体名。
        if (_hoverBodyName.Length > 0)
        {
            DrawString(_font, new Vector2(ViewWidth - 220, ViewHeight - 60), _hoverBodyName,
                HorizontalAlignment.Left, -1, 14, new Color("#f4f1de"));
        }

        // 操作提示。
        DrawString(_font, new Vector2(16, ViewHeight - 28), "W/S 推进 · A/D 转向 · 滚轮缩放 · 星球/恒星有引力井（靠近会被吸引）",
            HorizontalAlignment.Left, -1, 13, new Color("#6e8d9c"));
    }

    private void DrawMiniMap()
    {
        var mapSize = 200.0f;
        var margin = 16.0f;
        var mapPos = new Vector2(ViewWidth - mapSize - margin, ViewHeight - mapSize - margin);
        var mapRect = new Rect2(mapPos, new Vector2(mapSize, mapSize));
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
            var dotRadius = body.Kind == UniverseGenerator.CelestialKind.Star ? 3.0f : 2.0f;
            DrawCircle(mapPoint, dotRadius, dotColor);
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
