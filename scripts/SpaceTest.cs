using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class SpaceTest : Node2D
{
    private enum CombatPhase
    {
        Active,
        Victory,
        Defeat,
    }

    private sealed class Projectile
    {
        public Vector2 Position;
        public Vector2 Velocity;
        public double Life;
        public bool FromPlayer;
    }

    private sealed class Target
    {
        public Vector2 Position;
        public double Health;
        public double MaxHealth;
        public required string Name;
        public double FireTimer;
    }

    private sealed class ThrusterVector
    {
        public Vector2 LocalOffset;
        public Vector2 LocalForce;
        public float Power;
        public float Torque;
    }

    private WorldState _world = null!;
    private Vector2 _playerPosition = new(560.0f, 370.0f);
    private Vector2 _playerVelocity = Vector2.Zero;
    private readonly List<Projectile> _projectiles = new();
    private readonly List<Target> _targets = new();
    private int _score;
    private bool _salvageAwarded;
    private double _playerHull;
    private double _playerMaxHull;
    private double _playerShield;
    private double _playerMaxShield;
    private double _battleHeat;
    private double _battleHeatCapacity;
    private double _weaponCooldown;
    private Font _font = null!;
    private bool _active;
    private CombatPhase _phase = CombatPhase.Active;
    private double _phaseTimer;
    private float _playerHeading;
    private float _playerAngularVelocity;
    private Vector2 _appliedForceDirection = Vector2.Zero;
    private float _appliedForceRatio;
    private float _forwardThrust;
    private float _reverseThrust;
    private float _leftTurnThrust;
    private float _rightTurnThrust;
    private int _activeThrusters;
    private bool _requestedExit;
    private int _expeditionStage = 1;

    public bool IsActive => _active;
    public string Phase => _phase.ToString();
    public int ExpeditionStage => _expeditionStage;
    public int Score => _score;
    public int TargetCount => _targets.Count;
    public WorldState World => _world;
    public double PlayerShield => _playerShield;
    public double PlayerMaxShield => _playerMaxShield;
    public double PlayerHull => _playerHull;
    public double PlayerMaxHull => _playerMaxHull;
    public double BattleHeat => _battleHeat;
    public double BattleHeatCapacity => _battleHeatCapacity;
    public float ForwardThrust => _forwardThrust;
    public float ReverseThrust => _reverseThrust;

    public bool ConsumeExitRequest()
    {
        var requested = _requestedExit;
        _requestedExit = false;
        return requested;
    }

    public override void _Ready()
    {
        _font = CreateUiFont();
        ConfigureTargetWave(1);
        Visible = false;
    }

    public void Setup(WorldState newWorld) => _world = newWorld;

    public void Enter()
    {
        _expeditionStage = 1;
        ConfigureTargetWave(_expeditionStage);
        ResetCombatState();
    }

    private void ResetCombatState()
    {
        _active = true;
        Visible = true;
        _playerPosition = new Vector2(560.0f, 370.0f);
        _playerVelocity = Vector2.Zero;
        _playerHeading = 0.0f;
        _playerAngularVelocity = 0.0f;
        _projectiles.Clear();
        _score = 0;
        _salvageAwarded = false;
        _phase = CombatPhase.Active;
        _phaseTimer = 0.0;
        _requestedExit = false;
        _appliedForceDirection = Vector2.Zero;
        _appliedForceRatio = 0.0f;
        _forwardThrust = 0.0f;
        _reverseThrust = 0.0f;
        _leftTurnThrust = 0.0f;
        _rightTurnThrust = 0.0f;
        _activeThrusters = 0;
        var metrics = _world.GetMetrics();
        _playerMaxShield = metrics.Shield;
        _playerShield = _playerMaxShield;
        _playerMaxHull = Math.Max(20.0, metrics.Hull);
        _playerHull = _playerMaxHull;
        _battleHeatCapacity = 18.0 + metrics.Hull * 0.35 + metrics.Cooling * 1.5;
        _battleHeat = metrics.Heat;
        _weaponCooldown = 0.0;
        foreach (var target in _targets)
        {
            target.Health = target.MaxHealth;
            target.FireTimer = 0.8;
        }
        QueueRedraw();
    }

    private void ConfigureTargetWave(int stage)
    {
        _targets.Clear();
        if (stage <= 1)
        {
            _targets.AddRange(new[]
            {
                new Target { Position = new Vector2(900.0f, 220.0f), Health = 20.0, MaxHealth = 20.0, Name = "失控无人机 A" },
                new Target { Position = new Vector2(1000.0f, 500.0f), Health = 20.0, MaxHealth = 20.0, Name = "失控无人机 B" },
                new Target { Position = new Vector2(780.0f, 520.0f), Health = 20.0, MaxHealth = 20.0, Name = "失控无人机 C" },
            });
            return;
        }

        _targets.AddRange(new[]
        {
            new Target { Position = new Vector2(850.0f, 180.0f), Health = 28.0, MaxHealth = 28.0, Name = "风暴哨卫 A" },
            new Target { Position = new Vector2(1040.0f, 250.0f), Health = 28.0, MaxHealth = 28.0, Name = "风暴哨卫 B" },
            new Target { Position = new Vector2(1080.0f, 500.0f), Health = 32.0, MaxHealth = 32.0, Name = "风暴哨卫 C" },
            new Target { Position = new Vector2(760.0f, 520.0f), Health = 26.0, MaxHealth = 26.0, Name = "风暴哨卫 D" },
        });
    }

    public void Exit()
    {
        _active = false;
        _requestedExit = false;
        Visible = false;
        QueueRedraw();
    }

    public void Tick(double delta)
    {
        if (!_active || _world is null || _world.Paused)
            return;

        if (_phase != CombatPhase.Active)
        {
            _phaseTimer += delta;
            QueueRedraw();
            return;
        }

        var metrics = _world.GetMetrics();
        _battleHeat = Math.Max(0.0, _battleHeat - metrics.Cooling * delta);
        var thermalRatio = _battleHeatCapacity > 0.0 ? _battleHeat / _battleHeatCapacity : 0.0;
        var thermalPenalty = thermalRatio <= 1.0 ? 1.0 : Math.Clamp(1.0 - (thermalRatio - 1.0) * 0.8, 0.15, 1.0);
        var powerScale = Mathf.Clamp((float)(_world.GetShipPowerRatio() * thermalPenalty), 0.0f, 1.0f);
        _weaponCooldown = Math.Max(0.0, _weaponCooldown - delta);
        ApplyShipMotion(metrics, powerScale, (float)delta);
        _battleHeat = Math.Min(_battleHeatCapacity * 1.8, _battleHeat + metrics.Heat * _appliedForceRatio * delta * 0.45);

        for (var index = _projectiles.Count - 1; index >= 0; index--)
        {
            var projectile = _projectiles[index];
            projectile.Position += projectile.Velocity * (float)delta;
            projectile.Life -= delta;
            var removeProjectile = projectile.Life <= 0.0;

            foreach (var target in _targets)
            {
                if (!projectile.FromPlayer)
                    break;
                if (target.Health <= 0.0)
                    continue;
                if (projectile.Position.DistanceTo(target.Position) >= 24.0f)
                    continue;

                var shotDamage = metrics.Damage * (0.55 + 0.45 * powerScale);
                target.Health = Math.Max(0.0, target.Health - shotDamage);
                removeProjectile = true;
                if (target.Health <= 0.0)
                {
                    _score++;
                    _world.Announce($"试航区：{target.Name} 已瘫痪。剩余目标 {_targets.Count(targetItem => targetItem.Health > 0.0)}。 ");
                    if (_score == _targets.Count && !_salvageAwarded)
                    {
                        _salvageAwarded = true;
                        CompleteVictory();
                    }
                }
                break;
            }

            if (removeProjectile && index < _projectiles.Count)
                _projectiles.RemoveAt(index);

            if (_phase != CombatPhase.Active)
                break;
        }

        if (_phase != CombatPhase.Active)
        {
            QueueRedraw();
            return;
        }

        foreach (var target in _targets)
        {
            if (target.Health <= 0.0)
                continue;
            target.FireTimer -= delta;
            if (target.FireTimer > 0.0)
                continue;
            target.FireTimer = 1.6 + target.Position.X / 1500.0;
            var direction = (_playerPosition - target.Position).Normalized();
            _projectiles.Add(new Projectile
            {
                Position = target.Position + direction * 22.0f,
                Velocity = direction * 185.0f,
                Life = 4.0,
                FromPlayer = false,
            });
        }

        for (var index = _projectiles.Count - 1; index >= 0; index--)
        {
            var projectile = _projectiles[index];
            if (projectile.FromPlayer || projectile.Position.DistanceTo(_playerPosition) >= 24.0f)
                continue;
            var incomingDamage = 3.5;
            var shieldDamage = Math.Min(_playerShield, incomingDamage);
            _playerShield -= shieldDamage;
            _playerHull = Math.Max(0.0, _playerHull - (incomingDamage - shieldDamage));
            _projectiles.RemoveAt(index);
            if (_playerHull <= 0.0)
                EnterDefeat();
            if (_phase != CombatPhase.Active)
                break;
        }

        QueueRedraw();
    }

    private void CompleteVictory()
    {
        _phase = CombatPhase.Victory;
        _phaseTimer = 0.0;
        _projectiles.Clear();
        _playerVelocity *= 0.25f;
        _world.AwardSalvage();
        _world.Announce("试航区清场完成：残骸已经标记。按 Enter 返回模块实验室，或按 R 重新挑战。");
    }

    private void StartNextWave()
    {
        if (_expeditionStage >= 2)
        {
            _world.Announce("风暴闸门暂时没有更深层信号。返回模块实验室继续改装，下一次远征会读取新的蓝图。");
            return;
        }

        _expeditionStage++;
        ConfigureTargetWave(_expeditionStage);
        ResetCombatState();
        _world.Announce("你进入了风暴闸门：哨卫编队已锁定，当前航段奖励会在清场后再次结算。");
    }

    private void RestartCurrentWave()
    {
        ConfigureTargetWave(_expeditionStage);
        ResetCombatState();
        _world.Announce($"航段 {_expeditionStage} 已重置，重新校验引擎布局。");
    }

    private void EnterDefeat()
    {
        if (_phase != CombatPhase.Active)
            return;

        _phase = CombatPhase.Defeat;
        _phaseTimer = 0.0;
        _projectiles.Clear();
        _playerVelocity = Vector2.Zero;
        _world.RecordCombatLoss();
    }

    private void ApplyShipMotion(ModuleMetrics metrics, float powerScale, float delta)
    {
        var throttleInput = 0.0f;
        if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up))
            throttleInput += 1.0f;
        if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down))
            throttleInput -= 1.0f;

        var yawInput = 0.0f;
        if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left))
            yawInput -= 1.0f;
        if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right))
            yawInput += 1.0f;

        var bodyThrusters = BuildThrusterProfile();
        var forceSum = Vector2.Zero;
        var installedThrust = 0.0f;
        var torqueCapacity = 0.0f;
        var torqueSum = 0.0f;
        var active = new HashSet<ThrusterVector>();
        _forwardThrust = 0.0f;
        _reverseThrust = 0.0f;
        _leftTurnThrust = 0.0f;
        _rightTurnThrust = 0.0f;

        foreach (var thruster in bodyThrusters)
        {
            installedThrust += thruster.Power;
            torqueCapacity += Mathf.Abs(thruster.Torque) * thruster.Power;
            var worldForce = thruster.LocalForce.Rotated(_playerHeading);

            // W only fires engines whose force points along the bow. S only fires a
            // genuine reverse-facing engine. A rear engine cannot magically reverse.
            if (throttleInput > 0.0f)
            {
                var activation = Mathf.Max(0.0f, thruster.LocalForce.X) * throttleInput;
                if (activation > 0.0f)
                {
                    forceSum += worldForce * (thruster.Power * activation);
                    _forwardThrust += thruster.Power * activation;
                    active.Add(thruster);
                }
            }
            else if (throttleInput < 0.0f)
            {
                var activation = Mathf.Max(0.0f, -thruster.LocalForce.X) * -throttleInput;
                if (activation > 0.0f)
                {
                    forceSum += worldForce * (thruster.Power * activation);
                    _reverseThrust += thruster.Power * activation;
                    active.Add(thruster);
                }
            }

            // Turning is differential thrust. It only works when the blueprint has
            // an engine offset from the center of mass, so engine placement matters.
            if (yawInput != 0.0f && thruster.Torque * yawInput > 0.0f)
            {
                var activation = Mathf.Min(1.0f, Mathf.Abs(thruster.Torque)) * Mathf.Abs(yawInput);
                forceSum += worldForce * (thruster.Power * activation * 0.65f);
                torqueSum += thruster.Torque * thruster.Power * activation;
                active.Add(thruster);
                if (yawInput < 0.0f)
                    _leftTurnThrust += thruster.Power * activation;
                else
                    _rightTurnThrust += thruster.Power * activation;
            }
        }

        _activeThrusters = active.Count;
        if (forceSum.LengthSquared() > 0.0001f && installedThrust > 0.0f)
        {
            _appliedForceDirection = forceSum.Normalized();
            _appliedForceRatio = Mathf.Clamp(forceSum.Length() / installedThrust, 0.0f, 1.25f);
            var acceleration = (30.0f + (float)metrics.Thrust * 17.0f) * powerScale * _appliedForceRatio;
            _playerVelocity += _appliedForceDirection * acceleration * delta;
        }
        else
        {
            _appliedForceDirection = Vector2.Zero;
            _appliedForceRatio = 0.0f;
        }

        var hasAppliedForce = forceSum.LengthSquared() > 0.0001f;
        var drag = hasAppliedForce ? 1.3f : (throttleInput != 0.0f ? 6.0f : 7.0f);
        _playerVelocity = _playerVelocity.MoveToward(Vector2.Zero, drag * delta);
        var maxSpeed = 145.0f + (float)metrics.Thrust * 24.0f;
        if (_playerVelocity.Length() > maxSpeed)
            _playerVelocity = _playerVelocity.Normalized() * maxSpeed;

        var torqueRatio = torqueCapacity > 0.0f ? Mathf.Clamp(torqueSum / torqueCapacity, -1.0f, 1.0f) : 0.0f;
        _playerAngularVelocity += torqueRatio * (1.1f + (float)metrics.Thrust * 0.12f) * powerScale * delta;
        _playerAngularVelocity = Mathf.Clamp(_playerAngularVelocity, -2.8f, 2.8f);
        _playerAngularVelocity = Mathf.MoveToward(_playerAngularVelocity, 0.0f, 1.8f * delta);
        _playerHeading += _playerAngularVelocity * delta;
        if (_playerHeading > Mathf.Pi)
            _playerHeading -= Mathf.Tau;
        else if (_playerHeading < -Mathf.Pi)
            _playerHeading += Mathf.Tau;

        _playerPosition += _playerVelocity * delta;
        var previousPosition = _playerPosition;
        _playerPosition.X = Mathf.Clamp(_playerPosition.X, 300.0f, 1170.0f);
        _playerPosition.Y = Mathf.Clamp(_playerPosition.Y, 130.0f, 610.0f);
        if (!Mathf.IsEqualApprox(previousPosition.X, _playerPosition.X))
            _playerVelocity.X *= -0.25f;
        if (!Mathf.IsEqualApprox(previousPosition.Y, _playerPosition.Y))
            _playerVelocity.Y *= -0.25f;
    }

    private List<ThrusterVector> BuildThrusterProfile()
    {
        var profile = new List<ThrusterVector>();
        ModuleInstance? core = null;
        foreach (var module in _world.ShipGraph.Modules)
        {
            if (module.Kind == "core")
            {
                core = module;
                break;
            }
        }

        if (core is null)
            return profile;

        var coreCenter = GridCenter(core);
        foreach (var module in _world.ShipGraph.Modules)
        {
            if (module.Kind != "thruster")
                continue;

            var exhaustDirection = module.Rotation switch
            {
                0 => Vector2.Up,
                1 => Vector2.Right,
                2 => Vector2.Down,
                _ => Vector2.Left,
            };
            var localForce = -exhaustDirection;
            var localOffset = GridCenter(module) - coreCenter;
            var torque = localOffset.X * localForce.Y - localOffset.Y * localForce.X;
            profile.Add(new ThrusterVector
            {
                LocalOffset = localOffset,
                LocalForce = localForce,
                Power = (float)ModuleGraph.Definitions[module.Kind].Thrust,
                Torque = torque,
            });
        }

        return profile;
    }

    private Vector2 GridCenter(ModuleInstance module)
    {
        var footprint = _world.ShipGraph.GetFootprint(module.Kind, module.Rotation);
        return new Vector2(
            module.GridPosition.X + (footprint.X - 1) * 0.5f,
            module.GridPosition.Y + (footprint.Y - 1) * 0.5f);
    }

    public bool HandleInput(InputEvent @event)
    {
        if (!_active)
            return false;

        if (@event is InputEventKey phaseKey && phaseKey.Pressed && !phaseKey.Echo)
        {
            if (_phase != CombatPhase.Active && phaseKey.Keycode == Key.R)
            {
                RestartCurrentWave();
                return true;
            }

            if (_phase == CombatPhase.Victory && phaseKey.Keycode == Key.N)
            {
                StartNextWave();
                return true;
            }

            if (_phase != CombatPhase.Active && (phaseKey.Keycode == Key.Enter || phaseKey.Keycode == Key.Space))
            {
                _requestedExit = true;
                return false;
            }
        }

        if (@event is InputEventMouseButton mouseButton && mouseButton.Pressed && mouseButton.ButtonIndex == MouseButton.Left)
        {
            var metrics = _world.GetMetrics();
            if (_phase != CombatPhase.Active || metrics.Damage <= 0.0 || _weaponCooldown > 0.0 || _playerHull <= 0.0)
            {
                if (_phase == CombatPhase.Active)
                    _world.Announce(metrics.Damage <= 0.0 ? "武器离线：先在模块实验室部署脉冲激光或动能炮。" : "武器正在冷却，或舰体已经失去响应。");
                return true;
            }

            var direction = (mouseButton.Position - _playerPosition).Normalized();
            _weaponCooldown = Math.Max(0.08, 0.35 - metrics.Damage * 0.01);
            _battleHeat = Math.Min(_battleHeatCapacity * 1.8, _battleHeat + metrics.Heat * 0.08);
            _projectiles.Add(new Projectile
            {
                Position = _playerPosition + direction * 24.0f,
                Velocity = direction * 440.0f,
                Life = 2.0,
                FromPlayer = true,
            });
            return true;
        }

        if (@event is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo &&
            (keyEvent.Keycode == Key.Escape || keyEvent.Keycode == Key.B))
            return false;

        if (@event is InputEventKey simulationKey && simulationKey.Pressed && !simulationKey.Echo)
        {
            if (simulationKey.Keycode == Key.P)
            {
                _world.TogglePause();
                return true;
            }
            if (simulationKey.Keycode == Key.Equal || simulationKey.Keycode == Key.Plus)
            {
                _world.AdjustTimeScale(0.25);
                return true;
            }
            if (simulationKey.Keycode == Key.Minus)
            {
                _world.AdjustTimeScale(-0.25);
                return true;
            }
        }

        return false;
    }

    public override void _Draw()
    {
        if (!_active)
            return;

        DrawRect(new Rect2(Vector2.Zero, new Vector2(1280.0f, 720.0f)), new Color("#070b12"));
        DrawStars();
        DrawMapNodes();
        DrawTargets();
        DrawProjectiles();
        DrawPlayer();
    }

    private void DrawStars()
    {
        for (var i = 0; i < 55; i++)
        {
            var x = (i * 83 + 29) % 1280;
            var y = (i * 47 + 43) % 720;
            DrawCircle(new Vector2(x, y), 1.0f + i % 2, new Color("#1b2b3d"));
        }
    }

    private void DrawMapNodes()
    {
        var route = new[]
        {
            new Vector2(190.0f, 170.0f),
            new Vector2(480.0f, 350.0f),
            new Vector2(840.0f, 260.0f),
            new Vector2(1110.0f, 430.0f),
        };

        for (var index = 0; index < route.Length - 1; index++)
            DrawLine(route[index], route[index + 1], new Color("#26394d"), 2.0f);
        for (var index = 0; index < route.Length; index++)
        {
            var nodeColor = index == 1
                ? new Color("#65c18c")
                : index == 2 && _world.StormGateUnlocked
                    ? new Color("#f2cc60")
                    : new Color("#57718d");
            DrawCircle(route[index], 12.0f, nodeColor);
            DrawCircle(route[index], 18.0f, new Color("#25384b"), false, 1.0f);
        }

        LabelText("种子前哨", route[1] + new Vector2(-42.0f, 36.0f), 11, new Color("#a8b3c2"));
        LabelText(_world.StormGateUnlocked ? "风暴闸门 · 已解锁" : "风暴闸门 · 未知", route[2] + new Vector2(-52.0f, 34.0f), 11, _world.StormGateUnlocked ? new Color("#f2cc60") : new Color("#a8b3c2"));
    }

    private void DrawTargets()
    {
        foreach (var target in _targets)
        {
            var alive = target.Health > 0.0;
            var targetColor = alive ? new Color("#f07c8e") : new Color("#344454");
            DrawCircle(target.Position, 22.0f, targetColor);
            DrawCircle(target.Position, 29.0f, new Color(targetColor, 0.35f), false, 2.0f);
            if (alive)
            {
                DrawRect(new Rect2(target.Position + new Vector2(-24.0f, -40.0f), new Vector2(48.0f, 4.0f)), new Color("#1d2834"));
                DrawRect(new Rect2(target.Position + new Vector2(-24.0f, -40.0f), new Vector2(48.0f * (float)(target.Health / target.MaxHealth), 4.0f)), new Color("#f07c8e"));
            }
            LabelText(target.Name, target.Position + new Vector2(-26.0f, 42.0f), 9, new Color("#9eabbc"));
        }
    }

    private void DrawProjectiles()
    {
        foreach (var projectile in _projectiles)
        {
            var direction = projectile.Velocity.Normalized();
            var color = projectile.FromPlayer ? new Color("#f2cc60") : new Color("#f07c8e");
            DrawLine(projectile.Position - direction * 10.0f, projectile.Position + direction * 10.0f, color, 3.0f);
        }
    }

    private void DrawPlayer()
    {
        var angle = _playerHeading;
        var nose = _playerPosition + new Vector2(28.0f, 0.0f).Rotated(angle);
        var left = _playerPosition + new Vector2(-18.0f, -16.0f).Rotated(angle);
        var right = _playerPosition + new Vector2(-18.0f, 16.0f).Rotated(angle);
        var polygon = new[] { nose, left, right };
        DrawColoredPolygon(polygon, new Color("#f4f1de"));
        var outline = new[] { nose, left, right, nose };
        DrawPolyline(outline, new Color("#65c18c"), 2.0f);

        // Render the actual engine mounts on the hull. The player can see which
        // engines are behind the center line, which ones are offset for yaw, and
        // why a blueprint with no forward-facing engine cannot reverse.
        foreach (var thruster in BuildThrusterProfile())
        {
            var mount = _playerPosition + (thruster.LocalOffset * 18.0f).Rotated(angle);
            var nozzle = mount + thruster.LocalForce.Rotated(angle) * 12.0f;
            DrawCircle(mount, 4.0f, new Color("#cf7bff", 0.8f));
            DrawLine(mount, nozzle, new Color("#cf7bff", 0.6f), 2.0f);
        }

        var metrics = _world.GetMetrics();
        if (metrics.Shield > 0.0 && _playerShield > 0.0)
            DrawCircle(_playerPosition, 38.0f, new Color("#7bb7e8", 0.45f), false, 2.0f);
        if (_activeThrusters > 0 && _appliedForceDirection.LengthSquared() > 0.0f)
        {
            var exhaustDirection = -_appliedForceDirection;
            var flame = _playerPosition - _appliedForceDirection * 25.0f;
            DrawLine(flame, flame + exhaustDirection * (18.0f + 14.0f * _appliedForceRatio), new Color("#cf7bff"), 4.0f);
            if (_leftTurnThrust > 0.0f || _rightTurnThrust > 0.0f)
            {
                var turnColor = _leftTurnThrust > _rightTurnThrust ? new Color("#78a6d8") : new Color("#e58c55");
                DrawArc(_playerPosition, 45.0f, angle - 0.8f, angle + 0.8f, 14, turnColor, 2.0f);
            }
        }
        if (metrics.Damage > 0.0)
        {
            var aimAngle = (GetViewport().GetMousePosition() - _playerPosition).Angle();
            var cannon = _playerPosition + new Vector2(12.0f, 0.0f).Rotated(aimAngle);
            DrawCircle(cannon, 4.0f, new Color("#f07c8e"));
            DrawLine(cannon, cannon + new Vector2(12.0f, 0.0f).Rotated(aimAngle), new Color("#f07c8e", 0.55f), 2.0f);
        }
    }


    private void LabelText(string text, Vector2 position, int size, Color color)
    {
        DrawString(_font, position, text, HorizontalAlignment.Left, -1.0f, size, color);
    }

    private Font CreateUiFont()
    {
        var systemFont = new SystemFont
        {
            FontNames = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Noto Sans CJK SC" },
            AllowSystemFallback = true,
        };
        return systemFont;
    }
}
