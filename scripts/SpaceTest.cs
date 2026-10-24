using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class SpaceTest : Node2D
{
    private const float WorldWidth = 2800.0f;
    private const float WorldHeight = 1900.0f;
    private const float ViewWidth = 1280.0f;
    private const float ViewHeight = 720.0f;

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
        public double Damage;
        public bool FromPlayer;
    }

    private sealed class Target
    {
        public Vector2 Position;
        public Vector2 Velocity;
        public float Heading;
        public required string Name;
        public double FireTimer;
        /// <summary>敌方舰体（模块化多边形，与玩家舰体同一套规则）。</summary>
        public ModularShip Ship = new();

        public double Health => Ship.Blocks.Where(b => b.Enabled).Sum(b => b.Health);
        public double MaxHealth => Ship.Blocks.Sum(b => b.Spec.Hull);
    }

    private sealed class ThrusterVector
    {
        public int BlockId;
        public Vector2 LocalOffset;
        public Vector2 LocalForce;
        public float Power;
        public float Torque;
        public float Activation;
    }

    private sealed class WeaponMount
    {
        public Vector2 LocalOffset;
        public Vector2 LocalDirection;
        public float Damage;
    }

    /// <summary>玩家舰体在战斗中的统一指标（模块化舰体或旧网格蓝图派生）。</summary>
    private readonly record struct PlayerMetrics(
        double Shield, double Hull, double Heat, double Cooling,
        double Mass, double Thrust, double Damage);

    private WorldState _world = null!;
    private Vector2 _playerPosition = new(1360.0f, 960.0f);
    private Vector2 _cameraPosition = new(1360.0f, 960.0f);
    private float _cameraZoom = 0.94f;
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
    private float _turnCapacity;
    private int _activeThrusters;
    private List<ThrusterVector> _thrusterProfile = new();
    private List<WeaponMount> _weaponProfile = new();
    private readonly HashSet<int> _disabledShipModules = new();
    private bool _requestedExit;
    private int _expeditionStage = 1;

    /// <summary>玩家模块化舰体（来自造舰编辑器，替代旧网格 ShipGraph 作为运行时数据源）。</summary>
    private ModularShip? _playerShip;
    private readonly Dictionary<int, float> _playerThrusterActivity = new();

    /// <summary>挂点偏移缩放：模块化舰体已是世界单位（1.0），旧网格蓝图需放大到世界（18.0）。</summary>
    private float MountScale => _playerShip is not null ? 1.0f : 18.0f;

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
    public float Heading => _playerHeading;
    public float AngularVelocity => _playerAngularVelocity;
    public float TurnCapacity => _turnCapacity;
    public float LeftTurnThrust => _leftTurnThrust;
    public float RightTurnThrust => _rightTurnThrust;
    public int ActiveThrusters => _activeThrusters;
    public float Speed => _playerVelocity.Length();
    public Vector2 WorldPosition => _playerPosition;
    public float CameraZoom => _cameraZoom;

    public void RequestNextWave()
    {
        if (_phase == CombatPhase.Victory)
            StartNextWave();
    }

    public void RequestRestart()
    {
        if (_phase != CombatPhase.Active)
            RestartCurrentWave();
    }

    public void RequestExit()
    {
        if (_phase != CombatPhase.Active)
            _requestedExit = true;
    }

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

    /// <summary>设置玩家模块化舰体（由 Main 从造舰编辑器传入）。</summary>
    public void SetShip(ModularShip ship)
    {
        _playerShip = ship;
    }

    public void Enter()
    {
        _expeditionStage = _world.SelectedExpeditionStage;
        ConfigureTargetWave(_expeditionStage);
        ResetCombatState();
    }

    private void ResetCombatState()
    {
        _active = true;
        Visible = true;
        _playerPosition = new Vector2(1360.0f, 960.0f);
        _cameraPosition = _playerPosition;
        _cameraZoom = 0.94f;
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
        _turnCapacity = 0.0f;
        _activeThrusters = 0;
        _thrusterProfile.Clear();
        _disabledShipModules.Clear();
        _playerShip?.ResetHealth();
        _weaponProfile = BuildWeaponProfile();
        var metrics = GetPlayerMetrics();
        _playerMaxShield = metrics.Shield;
        _playerShield = _playerMaxShield;
        _playerMaxHull = Math.Max(20.0, metrics.Hull);
        _playerHull = _playerMaxHull;
        _battleHeatCapacity = 18.0 + metrics.Hull * 0.35 + metrics.Cooling * 1.5;
        _battleHeat = metrics.Heat;
        _weaponCooldown = 0.0;
        foreach (var target in _targets)
        {
            target.Ship.ResetHealth();
            target.Velocity = Vector2.Zero;
            target.Heading = Mathf.Pi;
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
                CreateTarget(new Vector2(1960.0f, 480.0f), "失控无人机 A", 0),
                CreateTarget(new Vector2(2250.0f, 1390.0f), "失控无人机 B", 0),
                CreateTarget(new Vector2(930.0f, 1470.0f), "失控无人机 C", 0),
            });
            return;
        }

        _targets.AddRange(new[]
        {
            CreateTarget(new Vector2(2040.0f, 390.0f), "风暴哨卫 A", 1),
            CreateTarget(new Vector2(2440.0f, 760.0f), "风暴哨卫 B", 1),
            CreateTarget(new Vector2(2180.0f, 1460.0f), "风暴哨卫 C", 1),
            CreateTarget(new Vector2(610.0f, 1370.0f), "风暴哨卫 D", 1),
        });
        if (stage == 2)
            return;

        _targets.Clear();
        _targets.AddRange(new[]
        {
            CreateTarget(new Vector2(2230.0f, 260.0f), "监管核心 A", 2),
            CreateTarget(new Vector2(2540.0f, 720.0f), "档案护卫 B", 2),
            CreateTarget(new Vector2(2300.0f, 1550.0f), "档案护卫 C", 2),
            CreateTarget(new Vector2(600.0f, 1540.0f), "监管核心 D", 2),
            CreateTarget(new Vector2(470.0f, 570.0f), "拦截节点 E", 1),
        });
    }

    private Target CreateTarget(Vector2 position, string name, int tier)
    {
        var target = new Target
        {
            Position = position,
            Heading = Mathf.Pi,
            Name = name,
            FireTimer = 0.8,
        };
        BuildEnemyShip(target.Ship, tier);
        return target;
    }

    /// <summary>按 tier 用形状库拼装敌方模块化舰体（与玩家同一套形状/端口规则）。</summary>
    private static void BuildEnemyShip(ModularShip ship, int tier)
    {
        ship.AddBlock("core", Vector2.Zero, 0.0f);
        switch (tier)
        {
            case 0:
                ship.AddBlock("thruster", new Vector2(-46.0f, 0.0f), Mathf.Pi);
                ship.AddBlock("laser", new Vector2(44.0f, 0.0f), Mathf.Pi * 0.5f);
                break;
            case 1:
                ship.AddBlock("armor", new Vector2(-42.0f, 0.0f), 0.0f);
                ship.AddBlock("thruster", new Vector2(-72.0f, 0.0f), Mathf.Pi);
                ship.AddBlock("laser", new Vector2(42.0f, -26.0f), Mathf.Pi * 0.5f);
                ship.AddBlock("cannon", new Vector2(42.0f, 26.0f), Mathf.Pi * 0.5f);
                break;
            default:
                ship.AddBlock("armor", new Vector2(-46.0f, 0.0f), 0.0f);
                ship.AddBlock("armor", new Vector2(0.0f, -42.0f), 0.0f);
                ship.AddBlock("armor", new Vector2(0.0f, 42.0f), 0.0f);
                ship.AddBlock("thruster", new Vector2(-80.0f, 0.0f), Mathf.Pi);
                ship.AddBlock("laser", new Vector2(48.0f, -30.0f), Mathf.Pi * 0.5f);
                ship.AddBlock("missile", new Vector2(48.0f, 30.0f), Mathf.Pi * 0.5f);
                break;
        }
        ship.ResetHealth();
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

        var metrics = GetPlayerMetrics();
        _battleHeat = Math.Max(0.0, _battleHeat - metrics.Cooling * delta);
        var thermalRatio = _battleHeatCapacity > 0.0 ? _battleHeat / _battleHeatCapacity : 0.0;
        var thermalPenalty = thermalRatio <= 1.0 ? 1.0 : Math.Clamp(1.0 - (thermalRatio - 1.0) * 0.8, 0.15, 1.0);
        var powerScale = Mathf.Clamp((float)(_world.GetShipPowerRatio() * thermalPenalty), 0.0f, 1.0f);
        _weaponCooldown = Math.Max(0.0, _weaponCooldown - delta);
        ApplyShipMotion(metrics, powerScale, (float)delta);
        _weaponProfile = BuildWeaponProfile();
        _battleHeat = Math.Min(_battleHeatCapacity * 1.8, _battleHeat + metrics.Heat * _appliedForceRatio * delta * 0.45);
        MoveTargets((float)delta);
        UpdateCamera((float)delta);

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
                var hitBlock = FindTargetBlockAt(target, projectile.Position);
                if (hitBlock is null)
                    continue;

                var shotDamage = projectile.Damage > 0.0
                    ? projectile.Damage
                    : metrics.Damage * (0.55 + 0.45 * powerScale);
                hitBlock.Health = Mathf.Max(0.0f, hitBlock.Health - (float)shotDamage);
                hitBlock.Enabled = hitBlock.Health > 0.0f;
                if (hitBlock.ShapeId == "core" && hitBlock.Health <= 0.0f)
                {
                    foreach (var module in target.Ship.Blocks)
                    {
                        module.Health = 0.0f;
                        module.Enabled = false;
                    }
                }
                removeProjectile = true;
                if (hitBlock.Health <= 0.0f)
                {
                    _world.Announce($"试航区：{target.Name} 的{hitBlock.Spec.Label}被击毁。" + (hitBlock.ShapeId == "core" ? "敌舰瘫痪。" : ""));
                }
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
            var weapons = target.Ship.GetWeaponMounts();
            if (weapons.Count == 0)
            {
                target.FireTimer = 0.75;
                continue;
            }
            target.FireTimer = 1.35 + target.Position.X / 1800.0;
            foreach (var weapon in weapons)
            {
                var mount = target.Position + weapon.Offset.Rotated(target.Heading);
                var direction = (_playerPosition - mount).Normalized();
                _projectiles.Add(new Projectile
                {
                    Position = mount + direction * 14.0f,
                    Velocity = direction * 190.0f,
                    Life = 4.0,
                    Damage = 3.5,
                    FromPlayer = false,
                });
            }
        }

        for (var index = _projectiles.Count - 1; index >= 0; index--)
        {
            var projectile = _projectiles[index];
            var hitRadius = _playerShip is not null ? _playerShip.BoundingRadius() : 24.0f;
            if (projectile.FromPlayer || projectile.Position.DistanceTo(_playerPosition) >= hitRadius)
                continue;
            var incomingDamage = projectile.Damage > 0.0 ? projectile.Damage : 3.5;
            var shieldDamage = Math.Min(_playerShield, incomingDamage);
            _playerShield -= shieldDamage;
            _playerHull = Math.Max(0.0, _playerHull - (incomingDamage - shieldDamage));
            if (_playerShip is not null)
            {
                // 模块化舰体：按点-多边形命中，扣方块耐久，归零则离线并触发断裂。
                var hitBlock = _playerShip.GetBlockAtWorld(projectile.Position, _playerPosition, _playerHeading);
                if (hitBlock is not null && hitBlock.ShapeId != "core" && hitBlock.Enabled)
                {
                    hitBlock.Health = Mathf.Max(0.0f, hitBlock.Health - (float)incomingDamage);
                    if (hitBlock.Health <= 0.0f)
                    {
                        hitBlock.Enabled = false;
                        _world.Announce($"敌方火力击毁我方{hitBlock.Spec.Label}，该部件已离线。");
                        _weaponProfile = BuildWeaponProfile();
                        _thrusterProfile = BuildThrusterProfile();
                    }
                }
            }
            else
            {
                var damagedModule = FindPlayerModuleAt(projectile.Position);
                if (damagedModule is not null && damagedModule.Kind != "core" && damagedModule.Enabled)
                {
                    _disabledShipModules.Add(damagedModule.Id);
                    _world.Announce($"敌方火力击毁我方{ModuleGraph.Definitions[damagedModule.Kind].Label}，该部件已离线。");
                    _weaponProfile = BuildWeaponProfile();
                }
            }
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
        _world.AwardSalvage(_expeditionStage);
        _world.Announce($"{_world.GetExpeditionName(_expeditionStage)}清场完成：残骸已经标记。返回航线图选择下一段，或按 R 重新挑战。");
    }

    private void StartNextWave()
    {
        if (_expeditionStage >= 3)
        {
            _world.Announce("档案环暂时没有更深层信号。返回航线图查看已完成的远征记录。");
            return;
        }

        _expeditionStage++;
        _world.SelectExpeditionStage(_expeditionStage);
        ConfigureTargetWave(_expeditionStage);
        ResetCombatState();
        _world.Announce($"你进入了「{_world.GetExpeditionName(_expeditionStage)}」：{_world.GetExpeditionBrief(_expeditionStage)}。");
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

    private void MoveTargets(float delta)
    {
        foreach (var target in _targets)
        {
            if (target.Health <= 0.0)
                continue;

            var toPlayer = _playerPosition - target.Position;
            var distance = toPlayer.Length();
            if (distance <= 0.01f)
                continue;

            var desiredHeading = toPlayer.Angle();
            var turn = Mathf.Wrap(desiredHeading - target.Heading, -Mathf.Pi, Mathf.Pi);
            target.Heading += Mathf.Clamp(turn, -1.8f * delta, 1.8f * delta);
            var thrusterCount = target.Ship.GetThrusterMounts().Count;
            var speed = thrusterCount == 0 ? 0.0f : Mathf.Clamp(26.0f + thrusterCount * 18.0f, 26.0f, 70.0f);
            var desiredDistance = 360.0f + (target.Name.GetHashCode() & 95);
            var approach = Mathf.Clamp((distance - desiredDistance) / 180.0f, -0.35f, 1.0f);
            var thrust = new Vector2(Mathf.Cos(target.Heading), Mathf.Sin(target.Heading)) * speed * approach;
            target.Velocity = target.Velocity.MoveToward(thrust, 55.0f * delta);
            target.Position += target.Velocity * delta;
            target.Position.X = Mathf.Clamp(target.Position.X, 160.0f, WorldWidth - 160.0f);
            target.Position.Y = Mathf.Clamp(target.Position.Y, 140.0f, WorldHeight - 140.0f);
        }
    }

    private void UpdateCamera(float delta)
    {
        var halfView = new Vector2(ViewWidth, ViewHeight) * 0.5f / _cameraZoom;
        var min = halfView + new Vector2(24.0f, 24.0f);
        var max = new Vector2(WorldWidth, WorldHeight) - halfView - new Vector2(24.0f, 24.0f);
        var desired = _playerPosition;
        desired.X = Mathf.Clamp(desired.X, min.X, Math.Max(min.X, max.X));
        desired.Y = Mathf.Clamp(desired.Y, min.Y, Math.Max(min.Y, max.Y));
        _cameraPosition = _cameraPosition.Lerp(desired, Mathf.Clamp(delta * 5.0f, 0.0f, 1.0f));
        ClampCameraPosition();
    }

    private void ClampCameraPosition()
    {
        var halfView = new Vector2(ViewWidth, ViewHeight) * 0.5f / _cameraZoom;
        var min = halfView + new Vector2(24.0f, 24.0f);
        var max = new Vector2(WorldWidth, WorldHeight) - halfView - new Vector2(24.0f, 24.0f);
        _cameraPosition.X = Mathf.Clamp(_cameraPosition.X, min.X, Math.Max(min.X, max.X));
        _cameraPosition.Y = Mathf.Clamp(_cameraPosition.Y, min.Y, Math.Max(min.Y, max.Y));
    }

    private Vector2 ScreenToWorld(Vector2 screenPosition)
    {
        return _cameraPosition + (screenPosition - new Vector2(ViewWidth, ViewHeight) * 0.5f) / _cameraZoom;
    }

    private Vector2 WorldToScreen(Vector2 worldPosition)
    {
        return (worldPosition - _cameraPosition) * _cameraZoom + new Vector2(ViewWidth, ViewHeight) * 0.5f;
    }

    /// <summary>敌方弹丸命中：用舰体整体的位移 + 旋转做点-多边形测试。</summary>
    private ModularShip.Block? FindTargetBlockAt(Target target, Vector2 worldPosition) =>
        target.Ship.GetBlockAtWorld(worldPosition, target.Position, target.Heading);

    private ModuleInstance? FindPlayerModuleAt(Vector2 worldPosition)
    {
        var core = _world.ShipGraph.Modules.FirstOrDefault(module => module.Kind == "core");
        if (core is null)
            return null;
        var local = (worldPosition - _playerPosition).Rotated(-_playerHeading);
        var coreCenter = GridCenter(core);
        ModuleInstance? best = null;
        var bestDistance = float.MaxValue;
        foreach (var module in _world.ShipGraph.Modules)
        {
            if (_disabledShipModules.Contains(module.Id))
                continue;
            var footprint = _world.ShipGraph.GetFootprint(module.Kind, module.Rotation);
            var half = new Vector2(footprint.X * 9.0f, footprint.Y * 9.0f);
            var center = (GridCenter(module) - coreCenter) * 18.0f;
            var delta = local - center;
            if (Mathf.Abs(delta.X) > half.X || Mathf.Abs(delta.Y) > half.Y)
                continue;
            var distance = delta.LengthSquared();
            if (distance < bestDistance)
            {
                best = module;
                bestDistance = distance;
            }
        }

        return best;
    }

    private void ApplyShipMotion(PlayerMetrics metrics, float powerScale, float delta)
    {
        var throttleInput = 0.0f;
        if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up))
            throttleInput += 1.0f;
        if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down))
            throttleInput -= 1.0f;

        var yawInput = 0.0f;
        if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left) || Input.IsKeyPressed(Key.Q))
            yawInput -= 1.0f;
        if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right) || Input.IsKeyPressed(Key.E))
            yawInput += 1.0f;

        var bodyThrusters = BuildThrusterProfile();
        _thrusterProfile = bodyThrusters;
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
                    thruster.Activation = Mathf.Max(thruster.Activation, activation);
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
                    thruster.Activation = Mathf.Max(thruster.Activation, activation);
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
                thruster.Activation = Mathf.Max(thruster.Activation, activation * 0.65f);
                active.Add(thruster);
                if (yawInput < 0.0f)
                    _leftTurnThrust += thruster.Power * activation;
                else
                    _rightTurnThrust += thruster.Power * activation;
            }
        }

        _activeThrusters = active.Count;
        _turnCapacity = torqueCapacity;

        // 同步推进器激活度到渲染映射（按方块 Id，供 ModularShipVisual 绘制火焰）。
        _playerThrusterActivity.Clear();
        foreach (var thruster in bodyThrusters)
            if (thruster.BlockId > 0)
                _playerThrusterActivity[thruster.BlockId] = thruster.Activation;
        if (forceSum.LengthSquared() > 0.0001f && installedThrust > 0.0f)
        {
            _appliedForceDirection = forceSum.Normalized();
            _appliedForceRatio = Mathf.Clamp(forceSum.Length() / installedThrust, 0.0f, 1.25f);
            var massResponse = Mathf.Clamp(34.0f / Mathf.Max(1.0f, (float)metrics.Mass), 0.35f, 1.35f);
            var acceleration = (30.0f + (float)metrics.Thrust * 17.0f) * massResponse * powerScale * _appliedForceRatio;
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
        // Keep angular momentum for a few frames. The old damping value was larger
        // than the per-frame engine torque, so A/D appeared to do nothing at 60 FPS.
        var massResponseForTurn = Mathf.Clamp(34.0f / Mathf.Max(1.0f, (float)metrics.Mass), 0.35f, 1.35f);
        var torqueResponse = (8.0f + (float)metrics.Thrust * 0.5f) * massResponseForTurn;
        _playerAngularVelocity += torqueRatio * torqueResponse * powerScale * delta;
        _playerAngularVelocity = Mathf.Clamp(_playerAngularVelocity, -4.2f, 4.2f);
        var angularDamping = yawInput == 0.0f ? 0.65f : 0.16f;
        _playerAngularVelocity = Mathf.MoveToward(_playerAngularVelocity, 0.0f, angularDamping * delta);
        _playerHeading += _playerAngularVelocity * delta;
        if (_playerHeading > Mathf.Pi)
            _playerHeading -= Mathf.Tau;
        else if (_playerHeading < -Mathf.Pi)
            _playerHeading += Mathf.Tau;

        _playerPosition += _playerVelocity * delta;
        var previousPosition = _playerPosition;
        _playerPosition.X = Mathf.Clamp(_playerPosition.X, 150.0f, WorldWidth - 150.0f);
        _playerPosition.Y = Mathf.Clamp(_playerPosition.Y, 140.0f, WorldHeight - 140.0f);
        if (!Mathf.IsEqualApprox(previousPosition.X, _playerPosition.X))
            _playerVelocity.X *= -0.25f;
        if (!Mathf.IsEqualApprox(previousPosition.Y, _playerPosition.Y))
            _playerVelocity.Y *= -0.25f;
    }

    private List<ThrusterVector> BuildThrusterProfile()
    {
        var profile = new List<ThrusterVector>();

        // 优先使用模块化舰体（Reassembly 式），回退到旧网格蓝图。
        if (_playerShip is not null)
        {
            var com = _playerShip.CenterOfMass();
            foreach (var mount in _playerShip.GetThrusterMounts())
            {
                var localOffset = mount.Offset;
                var localForce = mount.Force;
                var torque = localOffset.X * localForce.Y - localOffset.Y * localForce.X;
                profile.Add(new ThrusterVector
                {
                    BlockId = mount.BlockId,
                    LocalOffset = localOffset,
                    LocalForce = localForce,
                    Power = mount.Power,
                    Torque = torque,
                });
            }
            return profile;
        }

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

        var centerOfMass = GetCenterOfMass();
        var operationalIds = _world.ShipGraph.GetOperationalModuleIds();
        foreach (var module in _world.ShipGraph.Modules)
        {
            if (module.Kind != "thruster" || !operationalIds.Contains(module.Id) || _disabledShipModules.Contains(module.Id))
                continue;

            var exhaustDirection = module.Rotation switch
            {
                0 => Vector2.Up,
                1 => Vector2.Right,
                2 => Vector2.Down,
                _ => Vector2.Left,
            };
            var localForce = -exhaustDirection;
            var localOffset = GridCenter(module) - centerOfMass;
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

    private List<WeaponMount> BuildWeaponProfile()
    {
        var profile = new List<WeaponMount>();

        // 优先使用模块化舰体（Reassembly 式）。
        if (_playerShip is not null)
        {
            var com = _playerShip.CenterOfMass();
            foreach (var mount in _playerShip.GetWeaponMounts())
            {
                profile.Add(new WeaponMount
                {
                    LocalOffset = mount.Offset,
                    LocalDirection = mount.Direction,
                    Damage = mount.Damage,
                });
            }
            return profile;
        }

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

        var centerOfMass = GetCenterOfMass();
        var operationalIds = _world.ShipGraph.GetOperationalModuleIds();
        foreach (var module in _world.ShipGraph.Modules)
        {
            if (!IsWeaponKind(module.Kind) || !operationalIds.Contains(module.Id) || _disabledShipModules.Contains(module.Id))
                continue;

            var facing = module.Rotation switch
            {
                0 => Vector2.Up,
                1 => Vector2.Right,
                2 => Vector2.Down,
                _ => Vector2.Left,
            };
            profile.Add(new WeaponMount
            {
                LocalOffset = GridCenter(module) - centerOfMass,
                LocalDirection = facing,
                Damage = (float)ModuleGraph.Definitions[module.Kind].Damage,
            });
        }

        return profile;
    }

    private Vector2 ResolveWeaponDirection(WeaponMount weapon, Vector2 mount, Vector2 target)
    {
        var hardpointDirection = weapon.LocalDirection.Rotated(_playerHeading).Normalized();
        var desiredDirection = target - mount;
        if (desiredDirection.LengthSquared() <= 0.001f)
            return hardpointDirection;

        desiredDirection = desiredDirection.Normalized();
        // Weapons can track a little, but their module orientation still matters:
        // a gun rotated to the stern cannot instantly fire through the bow.
        var trackingAngle = hardpointDirection.AngleTo(desiredDirection);
        return hardpointDirection.Rotated(Mathf.Clamp(trackingAngle, -1.30f, 1.30f)).Normalized();
    }

    private static bool IsWeaponKind(string kind) => kind is "laser" or "cannon" or "beam" or "railgun" or "missile" or "point_defense";

    /// <summary>玩家舰体统一战斗指标：模块化舰体优先，回退旧网格蓝图。</summary>
    private PlayerMetrics GetPlayerMetrics()
    {
        if (_playerShip is not null)
        {
            var blocks = _playerShip.Blocks;
            double hull = 0, heat = 0, cooling = 0, shield = 0, damage = 0, thrust = 0;
            foreach (var block in blocks)
            {
                if (!block.Enabled)
                    continue;
                var spec = block.Spec;
                hull += spec.Hull;
                shield += spec.Id == "shield" ? spec.Hull * 0.5f : 0.0;
                damage += spec.Damage;
                thrust += spec.Thrust;
                if (spec.Id == "radiator") cooling += spec.Hull * 0.2f;
            }
            var mass = _playerShip.TotalMass;
            return new PlayerMetrics(shield, hull, heat, cooling, mass, thrust, damage);
        }

        var metrics = _world.GetMetrics();
        return new PlayerMetrics(metrics.Shield, metrics.Hull, metrics.Heat, metrics.Cooling,
            metrics.Mass, metrics.Thrust, metrics.Damage);
    }

    private Vector2 GetCenterOfMass()
    {
        var operationalIds = _world.ShipGraph.GetOperationalModuleIds();
        var weightedPosition = Vector2.Zero;
        var totalMass = 0.0f;
        foreach (var module in _world.ShipGraph.Modules)
        {
            if (!operationalIds.Contains(module.Id))
                continue;

            var mass = (float)ModuleGraph.Definitions[module.Kind].Mass;
            weightedPosition += GridCenter(module) * mass;
            totalMass += mass;
        }

        return totalMass > 0.0f ? weightedPosition / totalMass : Vector2.Zero;
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

        if (@event is InputEventMouseButton mouseButton && mouseButton.Pressed &&
            (mouseButton.ButtonIndex == MouseButton.WheelUp || mouseButton.ButtonIndex == MouseButton.WheelDown))
        {
            var cursorWorld = ScreenToWorld(mouseButton.Position);
            _cameraZoom = Mathf.Clamp(_cameraZoom * (mouseButton.ButtonIndex == MouseButton.WheelUp ? 1.12f : 1.0f / 1.12f), 0.58f, 1.18f);
            _cameraPosition = cursorWorld - (mouseButton.Position - new Vector2(ViewWidth, ViewHeight) * 0.5f) / _cameraZoom;
            ClampCameraPosition();
            QueueRedraw();
            return true;
        }

        if (@event is InputEventMouseButton fireButton && fireButton.Pressed && fireButton.ButtonIndex == MouseButton.Left)
        {
            var metrics = GetPlayerMetrics();
            if (_phase != CombatPhase.Active || metrics.Damage <= 0.0 || _weaponCooldown > 0.0 || _playerHull <= 0.0)
            {
                if (_phase == CombatPhase.Active)
                    _world.Announce(metrics.Damage <= 0.0 ? "武器离线：先在模块实验室部署脉冲激光或动能炮。" : "武器正在冷却，或舰体已经失去响应。");
                return true;
            }

            var weaponProfile = BuildWeaponProfile();
            if (weaponProfile.Count == 0)
            {
                _world.Announce("武器已与核心断开：重新连接激光或动能炮后才能开火。");
                return true;
            }

            _weaponCooldown = Math.Max(0.08, 0.35 - metrics.Damage * 0.01);
            _battleHeat = Math.Min(_battleHeatCapacity * 1.8, _battleHeat + metrics.Heat * 0.08);
            foreach (var weapon in weaponProfile)
            {
                var mount = _playerPosition + (weapon.LocalOffset * MountScale).Rotated(_playerHeading);
                var direction = ResolveWeaponDirection(weapon, mount, ScreenToWorld(fireButton.Position));
                _projectiles.Add(new Projectile
                {
                    Position = mount + direction * 18.0f,
                    Velocity = direction * 440.0f,
                    Life = 2.0,
                    Damage = weapon.Damage * (0.55 + 0.45 * _world.GetShipPowerRatio()),
                    FromPlayer = true,
                });
            }
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

        DrawSetTransform(Vector2.Zero, 0.0f, Vector2.One);
        DrawRect(new Rect2(Vector2.Zero, new Vector2(ViewWidth, ViewHeight)), new Color("#050811"));
        DrawRect(new Rect2(Vector2.Zero, new Vector2(ViewWidth, 8.0f)), new Color("#192a3b", 0.75f));
        DrawRect(new Rect2(new Vector2(0.0f, ViewHeight - 8.0f), new Vector2(ViewWidth, 8.0f)), new Color("#192a3b", 0.75f));
        DrawSetTransform(new Vector2(ViewWidth, ViewHeight) * 0.5f - _cameraPosition * _cameraZoom, 0.0f, new Vector2(_cameraZoom, _cameraZoom));
        DrawSectorBackdrop();
        DrawStars();
        DrawMapNodes();
        DrawTargets();
        DrawProjectiles();
        DrawPlayer();
        DrawSetTransform(Vector2.Zero, 0.0f, Vector2.One);
    }

    private void DrawStars()
    {
        for (var i = 0; i < 180; i++)
        {
            var x = (i * 137 + 79) % (int)WorldWidth;
            var y = (i * 89 + 53) % (int)WorldHeight;
            var size = i % 9 == 0 ? 3.0f : i % 3 == 0 ? 2.0f : 1.0f;
            var color = i % 7 == 0 ? new Color("#9cb2ce", 0.8f) : new Color("#34475c", 0.75f);
            DrawCircle(new Vector2(x, y), size, color);
        }
    }

    private void DrawSectorBackdrop()
    {
        for (var x = 0.0f; x <= WorldWidth; x += 160.0f)
            DrawLine(new Vector2(x, 0.0f), new Vector2(x, WorldHeight), new Color("#152434", 0.24f), 1.0f);
        for (var y = 0.0f; y <= WorldHeight; y += 160.0f)
            DrawLine(new Vector2(0.0f, y), new Vector2(WorldWidth, y), new Color("#152434", 0.24f), 1.0f);

        DrawCircle(new Vector2(460.0f, 420.0f), 150.0f, new Color("#18334a", 0.32f));
        DrawCircle(new Vector2(460.0f, 420.0f), 150.0f, new Color("#4d88a3", 0.35f), false, 2.0f);
        DrawCircle(new Vector2(2420.0f, 1450.0f), 220.0f, new Color("#392946", 0.26f));
        DrawCircle(new Vector2(2420.0f, 1450.0f), 220.0f, new Color("#9a6bb0", 0.32f), false, 2.0f);

        for (var i = 0; i < 26; i++)
        {
            var center = new Vector2((i * 173 + 310) % 2250 + 190, (i * 97 + 260) % 1350 + 180);
            var radius = 9.0f + (i % 5) * 4.0f;
            var points = new Vector2[7];
            for (var point = 0; point < points.Length; point++)
            {
                var angle = point * Mathf.Tau / points.Length;
                var wobble = 0.75f + ((i + point * 3) % 5) * 0.08f;
                points[point] = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius * wobble;
            }
            DrawColoredPolygon(points, new Color("#263847", 0.82f));
            DrawPolyline(points.Append(points[0]).ToArray(), new Color("#5a7181", 0.55f), 1.2f);
        }
    }

    private void DrawMapNodes()
    {
        var route = new[]
        {
            new Vector2(230.0f, 310.0f),
            new Vector2(820.0f, 690.0f),
            new Vector2(1450.0f, 420.0f),
            new Vector2(2140.0f, 820.0f),
            new Vector2(2550.0f, 1450.0f),
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
            DrawCircle(route[index], index == 0 ? 24.0f : 16.0f, nodeColor);
            DrawCircle(route[index], index == 0 ? 34.0f : 26.0f, new Color("#25384b"), false, 2.0f);
        }

        LabelText("起始坐标", route[0] + new Vector2(-40.0f, 52.0f), 13, new Color("#b7c7d4"));
        LabelText("种子前哨", route[1] + new Vector2(-48.0f, 42.0f), 13, new Color("#a8b3c2"));
        LabelText(_world.StormGateUnlocked ? "风暴闸门 · 已解锁" : "风暴闸门 · 未知", route[2] + new Vector2(-72.0f, 42.0f), 13, _world.StormGateUnlocked ? new Color("#f2cc60") : new Color("#a8b3c2"));
        LabelText("档案环", route[4] + new Vector2(-42.0f, 46.0f), 13, new Color("#a8b3c2"));
    }

    private void DrawTargets()
    {
        foreach (var target in _targets)
        {
            var alive = target.Health > 0.0;
            var targetColor = alive ? new Color("#f07c8e") : new Color("#344454");

            // 敌方舰体用与玩家一致的模块化多边形渲染。
            ModularShipVisual.Draw(this, target.Ship, target.Position, target.Heading);

            DrawCircle(target.Position, 42.0f, new Color(targetColor, alive ? 0.25f : 0.1f), false, 1.5f);
            if (alive)
            {
                DrawRect(new Rect2(target.Position + new Vector2(-30.0f, -54.0f), new Vector2(60.0f, 5.0f)), new Color("#1d2834"));
                DrawRect(new Rect2(target.Position + new Vector2(-30.0f, -54.0f), new Vector2(60.0f * (float)(target.Health / target.MaxHealth), 5.0f)), new Color("#f07c8e"));
            }
            LabelText(target.Name, target.Position + new Vector2(-42.0f, 56.0f), 9, new Color("#9eabbc"));
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

        // 模块化舰体：直接用可复用渲染器绘制多边形模块（含推进器火焰/武器炮口）。
        if (_playerShip is not null)
        {
            ModularShipVisual.Draw(this, _playerShip, _playerPosition, angle, drawPorts: false, thrusterActivity: _playerThrusterActivity);
        }
        else
        {
            DrawShipBody(angle);

            // Render the actual engine mounts on the hull (grid blueprint fallback).
            foreach (var thruster in _thrusterProfile)
            {
                var mount = _playerPosition + (thruster.LocalOffset * MountScale).Rotated(angle);
                var forceDirection = thruster.LocalForce.Rotated(angle);
                var nozzle = mount + forceDirection * 12.0f;
                var activity = Mathf.Clamp(thruster.Activation, 0.0f, 1.0f);
                DrawCircle(mount, 4.0f + activity * 2.0f, new Color(0.81f, 0.48f, 1.0f, 0.45f + activity * 0.45f));
                DrawLine(mount, nozzle, new Color("#cf7bff", 0.35f + activity * 0.65f), 2.0f);
                if (activity > 0.01f)
                {
                    var exhaust = -forceDirection;
                    var flameLength = 12.0f + activity * 18.0f;
                    DrawLine(mount - forceDirection * 4.0f, mount + exhaust * flameLength, new Color("#f2cc60", 0.45f + activity * 0.5f), 3.0f);
                }
            }
        }

        // A short bow marker makes the heading readable even when the ship is
        // travelling sideways after a differential-thrust turn.
        DrawLine(_playerPosition, _playerPosition + new Vector2(42.0f, 0.0f).Rotated(angle), new Color("#f4f1de", 0.55f), 1.5f);

        var (shield, damage) = GetPlayerShieldDamage();
        if (shield > 0.0 && _playerShield > 0.0)
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
        if (damage > 0.0)
        {
            var mousePosition = ScreenToWorld(GetViewport().GetMousePosition());
            foreach (var weapon in _weaponProfile)
            {
                var mount = _playerPosition + (weapon.LocalOffset * MountScale).Rotated(angle);
                var hardpointDirection = weapon.LocalDirection.Rotated(angle);
                var cannon = mount + hardpointDirection * 12.0f;
                var aimDirection = ResolveWeaponDirection(weapon, cannon, mousePosition);
                DrawCircle(cannon, 4.0f, new Color("#f07c8e"));
                DrawLine(cannon, cannon + aimDirection * 12.0f, new Color("#f07c8e", 0.55f), 2.0f);
            }
        }
    }

    /// <summary>玩家舰体指标：模块化舰体直接派生，旧网格从 WorldState 取。</summary>
    private (double Shield, double Damage) GetPlayerShieldDamage()
    {
        if (_playerShip is not null)
        {
            // 模块化舰体：护盾/伤害从存活模块派生。
            var shield = _playerShip.Blocks.Where(b => b.Enabled && b.Spec.Id == "shield").Sum(b => b.Spec.Hull * 0.5f);
            var damage = _playerShip.TotalDamage;
            return (shield, damage);
        }
        var metrics = _world.GetMetrics();
        return (metrics.Shield, metrics.Damage);
    }

    private void DrawShipBody(float angle)
    {
        var core = _world.ShipGraph.Modules.FirstOrDefault(module => module.Kind == "core");
        if (core is null)
            return;

        var coreCenter = GridCenter(core);
        var operationalIds = _world.ShipGraph.GetOperationalModuleIds();
        foreach (var module in _world.ShipGraph.Modules)
        {
            var definition = ModuleGraph.Definitions[module.Kind];
            var footprint = _world.ShipGraph.GetFootprint(module.Kind, module.Rotation);
            var localCenter = (GridCenter(module) - coreCenter) * 18.0f;
            var size = new Vector2(Math.Max(10.0f, footprint.X * 18.0f - 3.0f), Math.Max(10.0f, footprint.Y * 18.0f - 3.0f));
            var center = _playerPosition + localCenter.Rotated(angle);
            var color = new Color(definition.ColorHex).Lerp(new Color("#f0f4f0"), 0.18f);
            if (!operationalIds.Contains(module.Id) || _disabledShipModules.Contains(module.Id))
                color = color.Darkened(0.55f);

            if (module.Kind == "shield")
            {
                DrawCircle(center, Math.Min(size.X, size.Y) * 0.48f, new Color(color, 0.48f));
                DrawArc(center, Math.Min(size.X, size.Y) * 0.48f, 0.0f, Mathf.Tau, 24, color, 1.5f, true);
                continue;
            }

            var corners = new[]
            {
                new Vector2(-size.X, -size.Y) * 0.5f,
                new Vector2(size.X, -size.Y) * 0.5f,
                new Vector2(size.X, size.Y) * 0.5f,
                new Vector2(-size.X, size.Y) * 0.5f,
            };
            for (var index = 0; index < corners.Length; index++)
                corners[index] = center + corners[index].Rotated(angle);
            DrawColoredPolygon(corners, color);
            DrawPolyline(new[] { corners[0], corners[1], corners[2], corners[3], corners[0] }, new Color("#f0f4f0", 0.78f), 1.2f);
            if (_disabledShipModules.Contains(module.Id))
            {
                DrawLine(corners[0], corners[2], new Color("#f07c8e", 0.9f), 2.0f);
                DrawLine(corners[1], corners[3], new Color("#f07c8e", 0.9f), 2.0f);
            }

            if (module.Kind is "reactor" or "core")
            {
                var diamond = new[]
                {
                    center + new Vector2(0, -5).Rotated(angle),
                    center + new Vector2(5, 0).Rotated(angle),
                    center + new Vector2(0, 5).Rotated(angle),
                    center + new Vector2(-5, 0).Rotated(angle),
                };
                DrawPolyline(new[] { diamond[0], diamond[1], diamond[2], diamond[3], diamond[0] }, new Color("#fff0b2"), 1.5f);
            }
            else if (module.Kind is "cargo" or "hull")
            {
                DrawLine(corners[0], corners[2], new Color("#f0f4f0", 0.28f), 1.0f);
                DrawLine(corners[1], corners[3], new Color("#f0f4f0", 0.28f), 1.0f);
            }
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
