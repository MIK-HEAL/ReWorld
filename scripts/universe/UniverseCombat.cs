using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class UniverseView
{
    public const int RespawnCost = 100;
    public PlayerProgress Progress { get; set; } = new();
    public CombatWorld Combat { get; } = new();
    public CombatVessel? PlayerCombat { get; private set; }
    public bool TableOpen => _tableOpen;
    public bool DiplomacyOpen => _diplomacyOpen;
    public Vector2I CurrentCell => _traffic.Cell;
    public float ShipMass => _ship?.TotalMass ?? 0;
    public string DockedStationName => _dockedStation?.Name ?? "空间站";
    /// <summary>当前停靠站归属派系（0 = 未停靠）。</summary>
    public int DockedStationFaction => _dockedStation?.FactionId ?? 0;
    private bool _tableOpen, _diplomacyOpen, _deathAnnounced;
    private float _repairDebt;
    private float _beforeTableZoom;
    private readonly List<CombatVessel> _combatActors = new();
    private readonly Dictionary<Vector2I, List<CombatVessel>> _mineralFields = new();
    /// <summary>指挥面板当前选择的自有舰队（多选）。</summary>
    private readonly List<UniverseTraffic.Vessel> _selectedFleet = new();
    /// <summary>指挥面板是否选中旗舰（玩家本舰）。</summary>
    private bool _playerSelected;
    /// <summary>旗舰自动航行目标点（指挥面板右键下达）。</summary>
    private Vector2? _playerCommand;
    /// <summary>旗舰驻守原地（制动停船）。</summary>
    private bool _playerHold;
    /// <summary>数字编组：Ctrl+1..3 记录选择，1..3 召回；Flagship 标记编组是否含旗舰。</summary>
    private readonly Dictionary<int, (List<UniverseTraffic.Vessel> Ships, bool Flagship)> _commandGroups = new();
    /// <summary>拖框选择起点（屏幕坐标），null 表示未在拖框。</summary>
    private Vector2? _dragStartScreen;
    /// <summary>玩家建成的全部前哨/太空站（含首座基地），按存档领地列表重建。</summary>
    private readonly List<CombatVessel> _bases = new();
    private float _colonyIncomeDebt;
    private float _factoryIncomeDebt;
    private float _oreDebt;
    private float _alloyDebt;
    private Vector2I _mineralCell = new(int.MaxValue, int.MaxValue);
    private readonly List<(Rect2 Rect, int Faction, string Action)> _diplomaticButtons = new();
    private HashSet<CombatVessel> _killPrevious = new();
    private HashSet<CombatVessel> _killCurrent = new();
    private Vector2I _killCell = new(int.MaxValue, int.MaxValue);

    private void BindCombatShip()
    {
        if (_ship is null) return;
        PlayerCombat = new CombatVessel { Name = "独立舰长", Ship = _ship, FactionId = FactionRelations.Player,
            Position = _playerPosition, PreviousPosition = _previousPlayerPosition, Velocity = _playerVelocity,
            Heading = _playerHeading };
    }
    private void InitializeCampaign()
    {
        BindCombatShip();
        Combat.DepositCollected += id => { if (id.Length > 0) Progress.MinedDeposits.Add(id); };
        foreach (var colony in Progress.Colonies)
        {
            var station = new CombatVessel { Name = colony.PlanetId.Length > 0 ? "殖民太空站" : "自治前哨",
                Ship = UniverseTraffic.BuildStation(), FactionId = FactionRelations.Player,
                Position = new(colony.X, colony.Y), PreviousPosition = new(colony.X, colony.Y), CollisionStatic = true };
            _bases.Add(station);
        }
        if (_bases.Count > 0)
        {
            foreach (var lua in Progress.FleetDesigns)
            {
                var ship = ShipDesign.ParseLua(lua).ToShip();
                if (ship.HasLiveCore) _traffic.AddOwned(ship, _bases[0].Position + Vector2.Up * (200 + _traffic.Vessels.Count * 90), "自治舰队");
            }
        }
    }
    /// <summary>星球实际归属：玩家认领过的星球覆盖生成期派系（0 = 无主）。</summary>
    private int EffectivePlanetFaction(UniverseGenerator.CelestialBody body) =>
        body.Kind == UniverseGenerator.CelestialKind.Planet && Progress.ClaimedPlanets.Contains(body.Id)
            ? FactionRelations.Player : body.FactionId;
    private void SyncCombatActors(bool includePlayer)
    {
        _combatActors.Clear();
        if (includePlayer && PlayerCombat is not null) _combatActors.Add(PlayerCombat);
        for (var i = 0; i < _traffic.Vessels.Count; i++) _combatActors.Add(_traffic.Vessels[i]);
        _combatActors.AddRange(_traffic.StationActors.Values);
        for (var i = 0; i < _bases.Count; i++)
            if (_bases[i].Position.DistanceTo(_playerPosition) < UniverseGenerator.SystemRadius * 2)
                _combatActors.Add(_bases[i]);
        var cell = _traffic.Cell;
        if (!_mineralFields.TryGetValue(cell, out var field))
        {
            field = new();
            _mineralFields[cell] = field;
            for (var i = 0; i < 18; i++)
            {
                var id = $"{cell.X},{cell.Y}/ore/{i}";
                if (Progress.MinedDeposits.Contains(id)) continue;
                var ship = new ModularShip(); ship.AddBlock("ra_219", Vector2.Zero, i * 0.7f);
                var pos = _playerPosition + new Vector2(550 + i % 6 * 110, (i / 6 - 1) * 140);
                if (_visibleBodies.Any(b => b.HasSurface && b.SurfaceDistance(pos) < 90)) continue;
                field.Add(new CombatVessel { Name = "矿质残骸 · 90 R", Ship = ship, Mineral = true, DepositId = id,
                    FactionId = 100, Position = pos, PreviousPosition = pos });
            }
        }
        for (var i = 0; i < field.Count; i++) if (field[i].Alive) _combatActors.Add(field[i]);
        _mineralCell = cell;
    }
    private void TickCombat(float dt)
    {
        if (_ship is null || PlayerCombat is null) return;
        PlayerCombat.Position = _playerPosition; PlayerCombat.PreviousPosition = _previousPlayerPosition;
        PlayerCombat.Heading = _playerHeading; PlayerCombat.Velocity = _playerVelocity;
        var active = !_warp.LocksFlight && !IsDocked;
        SyncCombatActors(active);
        for (var i = 0; i < _combatActors.Count; i++)
        {
            var actor = _combatActors[i];
            if (actor.SurfaceImpact > 0)
            {
                Combat.SurfaceImpact(actor, actor.SurfaceImpact, actor.SurfaceNormal);
                actor.SurfaceImpact = 0; actor.SurfaceNormal = Vector2.Zero;
            }
        }
        var manual = active && !_tableOpen && !_diplomacyOpen && !_chartOpen &&
            (AiFiring && AiAimPoint is not null ||
             _pointerScreen is { } pointer && !MiniMapRect.HasPoint(pointer) && Input.IsMouseButtonPressed(MouseButton.Left));
        Combat.Step(dt, _combatActors, Progress.Diplomacy, PlayerCombat, manual,
            AiAimPoint ?? (_pointerScreen is { } aim ? ScreenToWorld(aim) : _playerPosition + Vector2.Right.Rotated(_playerHeading) * 1000));
        TrackMissionKills();
        if (active)
        {
            _playerPosition = PlayerCombat.Position;
            _playerVelocity = PlayerCombat.Velocity;
            var collected = Combat.Collect(_playerPosition, PlayerCombat.HasTractor ? 450 : 100);
            if (collected > 0) { Progress.Collect(collected); Progress.RecordMined(collected); Notify($"回收 {collected} R · M 造舰 / U 查看升级"); }
        }
        // Live ships slowly self-repair and regrow destroyed blocks when resources allow.
        if (_ship is not null && _ship.HasLiveCore && active)
        {
            var repairCost = _ship.TickRepair(dt, Progress.Resources);
            if (repairCost > 0f)
            {
                _repairDebt += repairCost;
                if (_repairDebt >= 1f) { var r = (int)_repairDebt; Progress.Spend(r); _repairDebt -= r; }
            }
        }
        // 领地运营收益：每座前哨/殖民星持续产出资源，基地越多复利越快。
        if (Progress.Colonies.Count > 0)
        {
            _colonyIncomeDebt += dt * 0.2f * Progress.Colonies.Count;
            if (_colonyIncomeDebt >= 1f)
            {
                var amount = (int)_colonyIncomeDebt;
                Progress.Collect(amount);
                _colonyIncomeDebt -= amount;
            }
        }
        // 地表工厂收益：老式（无蓝图）工厂按群系直接产 R；模块化工厂产出铁质碎料/合金
        // 入库存，到空间站交易页出售换 R——行情周期波动，抛售会压价。
        var factoryRate = Progress.FactoryIncomeRate();
        if (factoryRate > 0)
        {
            _factoryIncomeDebt += dt * factoryRate;
            if (_factoryIncomeDebt >= 1f)
            {
                var produced = (int)_factoryIncomeDebt;
                Progress.Collect(produced);
                _factoryIncomeDebt -= produced;
            }
        }
        var (oreRate, alloyRate) = Progress.FactoryResourceRates();
        if (oreRate > 0)
        {
            _oreDebt += dt * oreRate;
            if (_oreDebt >= 1f)
            {
                var mined = (int)_oreDebt;
                Progress.AddResource("iron", mined);
                _oreDebt -= mined;
            }
        }
        if (alloyRate > 0)
        {
            _alloyDebt += dt * alloyRate;
            if (_alloyDebt >= 1f)
            {
                var refined = (int)_alloyDebt;
                Progress.AddResource("alloy", refined);
                _alloyDebt -= refined;
            }
        }
        Progress.TickMarket(dt);
        if (_ship is not null && !_ship.HasLiveCore && !_deathAnnounced)
        { _deathAnnounced = true; Notify("指挥核心已毁 · Enter 返回空间站并启用应急舰"); }
    }

    // Station contracts: hostile vessels that drop out of the alive set count as player kills.
    // The snapshot resets on system change so despawned traffic is never mistaken for a kill.
    private void TrackMissionKills()
    {
        var cell = _traffic.Cell;
        if (cell != _killCell) { _killPrevious.Clear(); _killCell = cell; }
        _killCurrent.Clear();
        for (var i = 0; i < _combatActors.Count; i++)
            if (_combatActors[i].Alive) _killCurrent.Add(_combatActors[i]);
        foreach (var vessel in _killPrevious)
            if (!_killCurrent.Contains(vessel) && !vessel.Mineral && vessel.FactionId != FactionRelations.Player
                && !vessel.KilledByCollision)
            {
                var hostile = Progress.Diplomacy.Hostile(FactionRelations.Player, vessel.FactionId);
                if (Progress.RecordKill(vessel.FactionId, hostile))
                    Notify($"袭击{Progress.Diplomacy.Name(vessel.FactionId)}舰船 · 你已被该派系通缉 · V 停战赎罪");
            }
        (_killPrevious, _killCurrent) = (_killCurrent, _killPrevious);
    }

    public bool ManufactureDocked()
    {
        if (_dockedStation is not { } station || _ship is null || !_ship.HasLiveCore ||
            _traffic.Vessels.Count(v => v.PlayerOwned && v.Alive) >= 8 ||
            _ship.Blocks.Any(b => !Progress.CanBuild(b.ShapeId))) return false;
        var price = Math.Max(150, (int)(_ship.TotalMass * 2));
        if (!Progress.Spend(price)) return false;
        var clone = _ship.Clone();
        var spawn = station.Position + Vector2.Down * (station.Radius + 260);
        _traffic.AddOwned(clone, spawn, $"自治舰 {Progress.FleetDesigns.Count + 1}");
        Progress.FleetDesigns.Add(ShipDesign.FromShip(clone).ToLua());
        Progress.Commit();
        Notify($"船坞制造完成 · 消耗 {price} R · Tab 选择并下达指令");
        return true;
    }

    public void ChangeRelation(int faction, DiplomaticState state)
    {
        Progress.Diplomacy.Set(FactionRelations.Player, faction, state);
        foreach (var vessel in _traffic.Vessels) vessel.CombatTarget = null;
        Progress.Commit();
        Notify($"与{Progress.Diplomacy.Name(faction)}的关系：{FactionRelations.Label(state)}");
    }
    public bool AidFaction(int faction)
    {
        if (Progress.Diplomacy.Hostile(FactionRelations.Player, faction) || !Progress.Spend(50)) return false;
        ChangeRelation(faction, DiplomaticState.Friendly);
        return true;
    }
    /// <summary>
    /// 建立前哨/太空站（1000 R）：
    /// · 首座基地可自由选址；
    /// · 之后必须建在无主星球领地内（同时认领该星球）或自有星球领地内（扩建太空站）。
    /// </summary>
    public bool EstablishBase()
    {
        if (_ship is null || !_ship.HasLiveCore || _warp.LocksFlight || IsDocked ||
            _visibleBodies.Any(b => b.HasSurface && b.SurfaceDistance(_playerPosition) < 500) ||
            _traffic.Vessels.Any(v => v.Alive && Progress.Diplomacy.Hostile(v.FactionId, FactionRelations.Player) && v.Position.DistanceTo(_playerPosition) < 1500)) return false;
        var planet = _visibleBodies.Where(b => b.Kind == UniverseGenerator.CelestialKind.Planet &&
                _playerPosition.DistanceTo(b.Position) <= b.TerritoryRadius)
            .MinBy(b => _playerPosition.DistanceSquaredTo(b.Position));
        if (Progress.Established && planet is null)
        { Notify("扩建太空站需进入无主或自有星球的势力范围"); return false; }
        if (planet is { } claim && EffectivePlanetFaction(claim) is var owner && owner != 0 && owner != FactionRelations.Player)
        { Notify($"{Progress.Diplomacy.Name(owner)}控制着 {claim.Name} 的势力范围，无法在此建站"); return false; }
        if (!Progress.Spend(1000)) { Notify("建立前哨需要 1000 R"); return false; }
        var position = _playerPosition;
        var claiming = planet is not null && EffectivePlanetFaction(planet) == 0;
        var station = new CombatVessel
        {
            Name = claiming ? "殖民前哨" : planet is not null ? "殖民太空站" : "自治前哨",
            Ship = UniverseTraffic.BuildStation(), FactionId = FactionRelations.Player,
            Position = position, PreviousPosition = position, CollisionStatic = true
        };
        _bases.Add(station);
        // The outpost replaces the captain's current anchorage, so no random offset can intersect a planet.
        _playerPosition += Vector2.Right * 260;
        Progress.Colonies.Add(new ColonyData { X = position.X, Y = position.Y, PlanetId = planet?.Id ?? "" });
        Progress.BaseX = position.X; Progress.BaseY = position.Y;
        if (claiming)
        {
            Progress.ClaimedPlanets.Add(planet!.Id);
            Notify($"已认领 {planet!.Name} · 星球纳入自治领地 · 殖民前哨开始产出资源");
        }
        else if (!Progress.Established) Notify("自治前哨已建立 · Tab 指挥 · N 在前哨附近制造当前蓝图舰");
        else Notify("殖民太空站建成 · 领地收益提升 · 复活点已更新");
        Progress.Established = true;
        Progress.Commit();
        return true;
    }
    public bool ManufactureFleetShip()
    {
        var shipyard = NearestBase(1200);
        if (shipyard is null || _ship is null || !_ship.HasLiveCore ||
            _traffic.Vessels.Count(v => v.PlayerOwned && v.Alive) >= 8 ||
            _ship.Blocks.Any(b => !Progress.CanBuild(b.ShapeId))) return false;
        var price = Math.Max(150, (int)(_ship.TotalMass * 2));
        if (!Progress.Spend(price)) return false;
        var clone = _ship.Clone();
        _traffic.AddOwned(clone, shipyard.Position + new Vector2(250, 200 + Progress.FleetDesigns.Count * 90), $"自治舰 {Progress.FleetDesigns.Count + 1}");
        Progress.FleetDesigns.Add(ShipDesign.FromShip(clone).ToLua());
        Progress.Commit(); Notify($"舰船制造完成 · 消耗 {price} R · Tab 选择并下达指令");
        return true;
    }
    private CombatVessel? NearestBase(float maxDistance) => _bases
        .Where(b => b.Alive && b.Position.DistanceTo(_playerPosition) <= maxDistance)
        .MinBy(b => b.Position.DistanceSquaredTo(_playerPosition));

    /// <summary>
    /// 认领当前降落的星球（400 R）：仅无主星球可认领，纳入自治领地并解锁工厂建造。
    /// </summary>
    public bool ClaimPlanet()
    {
        if (_landedBody is not { } planet) { Notify("需要先降落到星球地表"); return false; }
        if (EffectivePlanetFaction(planet) != 0)
        { Notify($"{planet.Name} 已由 {Progress.Diplomacy.Name(EffectivePlanetFaction(planet))} 控制"); return false; }
        if (!Progress.Spend(400)) { Notify("认领星球需要 400 R"); return false; }
        Progress.ClaimedPlanets.Add(planet.Id);
        Progress.Commit();
        Notify($"已在 {planet.Name} 升起自治旗 · 星球纳入领地 · 可建造地表工厂");
        return true;
    }

    /// <summary>
    /// 在当前降落的星球上建造一座地表工厂：须为玩家已认领的星球，
    /// 造价随工厂数递增（400 + 250×已有数），产出由生物群系决定。
    /// </summary>
    public bool BuildFactory()
    {
        if (_landedBody is not { } planet) { Notify("需要先降落到星球地表"); return false; }
        if (EffectivePlanetFaction(planet) != FactionRelations.Player)
        { Notify(EffectivePlanetFaction(planet) == 0 ? "先认领星球（地表作业面板）才能建造工厂" : "只能在自己的领地星球上建造工厂"); return false; }
        var existing = Progress.FactoryCountOf(planet.Id);
        if (existing >= PlayerProgress.MaxFactoriesPerPlanet) { Notify($"每颗星球最多 {PlayerProgress.MaxFactoriesPerPlanet} 座工厂"); return false; }
        var cost = PlayerProgress.FactoryCost(existing);
        if (!Progress.Spend(cost)) { Notify($"建造工厂需要 {cost} R"); return false; }
        Progress.TryAddFactory(planet.Id, planet.Biome);
        var rate = PlayerProgress.FactoryRateOf(planet.Biome) * 60;
        Notify($"工厂建成 · 消耗 {cost} R · 该星球产出 +{rate:0.#} R/分");
        return true;
    }

    /// <summary>地表作业面板按钮动作分发。</summary>
    private void HandleSurfaceAction(string action)
    {
        switch (action)
        {
            case "claim": ClaimPlanet(); break;
            // 模块化建造：主场景订阅时打开工厂蓝图界面，蓝图决定产出（矿机产出资源折 R 入账）。
            case "build":
                if (FactoryBuildRequested is not null) FactoryBuildRequested();
                else BuildFactory();
                break;
            case "takeoff": _surfacePanelOpen = false; TakeOff(); break;
        }
    }

    public bool TryRespawn()
    {
        if (_ship is null || _ship.HasLiveCore) return false;
        if (!TryGetRespawnLocation(out var position, out var siteRadius, out var stationName))
        {
            Notify("没有可用的友好空间站或自有前哨");
            return false;
        }
        if (!Progress.Spend(RespawnCost))
        {
            Notify($"复活需要 {RespawnCost} R");
            return false;
        }
        _ship.SeedReferenceShip();
        var radius = _ship.BoundingRadius();
        _playerPosition = position + Vector2.Right * (siteRadius + radius + 40);
        _playerVelocity = Vector2.Zero; _playerAngularVelocity = 0;
        BindCombatShip(); RefreshFlightProfile(); ResetFlightPresentation(); _deathAnnounced = false;
        Notify($"已在{stationName}复活 · 消耗 {RespawnCost} R · 科技进度保留");
        return true;
    }

    private bool TryGetRespawnLocation(out Vector2 position, out float siteRadius, out string name)
    {
        position = default;
        siteRadius = 0;
        name = "";
        var candidates = new List<(float Distance, Vector2 Position, float Radius, string Name)>();
        foreach (var baseStation in _bases.Where(b => b.Alive))
            candidates.Add((_playerPosition.DistanceSquaredTo(baseStation.Position), baseStation.Position,
                baseStation.Ship.BoundingRadius(), baseStation.Name));
        var system = UniverseGenerator.GenerateSystem(UniverseGenerator.WorldToSystemCell(_playerPosition));
        foreach (var station in system.Bodies.Where(body => body.Kind == UniverseGenerator.CelestialKind.Station))
        {
            if (!_traffic.StationActors.TryGetValue(station.Id, out var actor) || !actor.Alive ||
                Progress.Diplomacy.Hostile(FactionRelations.Player, actor.FactionId)) continue;
            candidates.Add((_playerPosition.DistanceSquaredTo(actor.Position), actor.Position,
                actor.Ship.BoundingRadius(), station.Name));
        }
        var selected = candidates.OrderBy(candidate => candidate.Distance).FirstOrDefault();
        if (selected.Name is null) return false;
        position = selected.Position;
        siteRadius = selected.Radius;
        name = selected.Name;
        return true;
    }

    private bool HandleCampaignInput(InputEvent input)
    {
        if (input is InputEventKey { Pressed: true, Echo: false } key)
        {
            if (key.Keycode == Key.Tab && !_warp.LocksFlight && !_chartOpen)
            {
                _tableOpen = !_tableOpen; _diplomacyOpen = false;
                if (_tableOpen) { _beforeTableZoom = _cameraZoom; _cameraZoom = 0.22f; }
                else _cameraZoom = _beforeTableZoom;
                return true;
            }
            if (key.Keycode == Key.V && !_warp.LocksFlight && !_chartOpen)
            { CloseTable(); _diplomacyOpen = !_diplomacyOpen; return true; }
            if (key.Keycode == Key.Escape && (_tableOpen || _diplomacyOpen)) { CloseCampaignOverlays(); return true; }
            if (key.Keycode == Key.Enter && _deathAnnounced) { TryRespawn(); return true; }
            if (_tableOpen && key.Keycode == Key.B) { if (!EstablishBase()) Notify("建前哨需 1000 R · 首座可自由选址，之后需在无主/自有星球势力范围内"); return true; }
            if (_tableOpen && key.Keycode == Key.N) { if (!ManufactureFleetShip()) Notify("制造需自己的前哨、足够资源和已解锁蓝图，舰队上限 8 艘"); return true; }
            if (_tableOpen && key.Keycode == Key.G)
            {
                foreach (var v in _traffic.Vessels.Where(v => v.PlayerOwned))
                { v.CommandDestination = null; v.CombatTarget = null; v.Hold = false; v.Escort = true; }
                _playerCommand = null; _playerHold = false;
                Notify("全军集结 · 护航跟随旗舰");
                return true;
            }
            if (_tableOpen && key.Keycode == Key.A)
            {
                _selectedFleet.Clear();
                foreach (var v in _traffic.Vessels) if (v.PlayerOwned && v.Alive) _selectedFleet.Add(v);
                _playerSelected = _ship is { HasLiveCore: true };
                var total = _selectedFleet.Count + (_playerSelected ? 1 : 0);
                Notify($"全选 {total} 艘自有舰（含旗舰）");
                return true;
            }
            if (_tableOpen && key.Keycode == Key.S && (_selectedFleet.Count > 0 || _playerSelected))
            {
                var held = 0;
                foreach (var v in _selectedFleet)
                    if (v.Alive) { v.Hold = true; v.Escort = false; v.CommandDestination = null; v.CombatTarget = null; held++; }
                if (_playerSelected && _ship is { HasLiveCore: true })
                { _playerHold = true; _playerCommand = null; held++; }
                Notify($"{held} 艘舰驻守原地");
                return true;
            }
            if (_tableOpen && key.Keycode is Key.Key1 or Key.Key2 or Key.Key3)
            {
                var group = (int)(key.Keycode - Key.Key1) + 1;
                if (key.CtrlPressed)
                {
                    _commandGroups[group] = (_selectedFleet.Where(v => v.Alive).ToList(), _playerSelected && _ship is { HasLiveCore: true });
                    var count = _commandGroups[group].Ships.Count + (_commandGroups[group].Flagship ? 1 : 0);
                    Notify($"编队 {group} 已记录 {count} 艘舰");
                }
                else if (_commandGroups.TryGetValue(group, out var members))
                {
                    _selectedFleet.Clear();
                    foreach (var v in members.Ships) if (v.Alive) _selectedFleet.Add(v);
                    _playerSelected = members.Flagship && _ship is { HasLiveCore: true };
                    var count = _selectedFleet.Count + (_playerSelected ? 1 : 0);
                    Notify($"选择编队 {group} · {count} 艘舰");
                }
                else Notify($"编队 {group} 未设置 · Ctrl+{group} 记录当前选择");
                return true;
            }
        }
        if (_diplomacyOpen)
        {
            if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click)
                foreach (var (rect, faction, action) in _diplomaticButtons)
                    if (rect.HasPoint(click.Position))
                    {
                        if (action == "aid") { if (!AidFaction(faction)) Notify("援助需 50 R，且双方不能处于交战状态"); }
                        else ChangeRelation(faction, action switch
                        {
                            "war" => DiplomaticState.War,
                            "wanted" => DiplomaticState.Wanted,
                            _ => DiplomaticState.Neutral
                        });
                        break;
                    }
            return input is not InputEventKey { Keycode: Key.M or Key.F11 };
        }
        if (_tableOpen && input is InputEventMouseButton { ButtonIndex: MouseButton.Left } left)
        {
            if (left.Pressed)
            {
                _dragStartScreen = left.Position;
                return true;
            }
            // 松开左键：位移小于阈值视为点选单舰，否则按屏幕矩形框选。旗舰一并参与判定。
            if (_dragStartScreen is { } start)
            {
                _dragStartScreen = null;
                var owned = new List<UniverseTraffic.Vessel>();
                foreach (var v in _traffic.Vessels) if (v.PlayerOwned && v.Alive) owned.Add(v);
                var flagshipAlive = _ship is { HasLiveCore: true };
                var flagshipRadius = _ship?.BoundingRadius() ?? 24;
                _selectedFleet.Clear();
                _playerSelected = false;
                if ((left.Position - start).Length() < 14)
                {
                    var at = ScreenToWorld(left.Position);
                    var single = owned.Count > 0 ? owned.MinBy(v => v.Position.DistanceSquaredTo(at)) : null;
                    var fleetHit = single is not null && single.Position.DistanceTo(at) < Math.Max(120, single.Radius);
                    var flagshipHit = flagshipAlive && _playerPosition.DistanceTo(at) < Math.Max(120, flagshipRadius);
                    // 点选同时命中时取更近者；仅命中旗舰也能单独选中指挥。
                    if (fleetHit && (!flagshipHit || single!.Position.DistanceSquaredTo(at) <= _playerPosition.DistanceSquaredTo(at)))
                        _selectedFleet.Add(single!);
                    else if (flagshipHit) _playerSelected = true;
                }
                else
                {
                    var rect = new Rect2(new Vector2(Mathf.Min(start.X, left.Position.X), Mathf.Min(start.Y, left.Position.Y)),
                        (start - left.Position).Abs());
                    foreach (var v in owned)
                        if (rect.Grow(10).HasPoint(WorldToScreen(v.Position))) _selectedFleet.Add(v);
                    if (flagshipAlive && rect.Grow(10).HasPoint(WorldToScreen(_playerPosition))) _playerSelected = true;
                }
                var picked = _selectedFleet.Count + (_playerSelected ? 1 : 0);
                if (picked > 0) Notify($"已选择 {picked} 艘舰（{_selectedFleet.Count} 自治舰 + {(_playerSelected ? 1 : 0)} 旗舰）· 右键下达指令");
            }
            return true;
        }
        if (_tableOpen && input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } right &&
            (_selectedFleet.Count > 0 || _playerSelected))
        {
            var at = ScreenToWorld(right.Position);
            var squad = new List<UniverseTraffic.Vessel>();
            foreach (var v in _selectedFleet) if (v.Alive) squad.Add(v);
            // 优先判定集火：点击点附近有交战敌舰则全队攻击，否则按编队队形移动。
            var enemy = _traffic.Vessels
                .Where(v => v.Alive && !v.PlayerOwned && v.FactionId != 100 &&
                    Progress.Diplomacy.Hostile(FactionRelations.Player, v.FactionId) &&
                    v.Position.DistanceTo(at) < Math.Max(140, v.Radius))
                .MinBy(v => v.Position.DistanceSquaredTo(at));
            if (enemy is not null)
            {
                foreach (var v in squad)
                { v.CombatTarget = enemy; v.Escort = false; v.Hold = false; v.CommandDestination = null; }
                // 旗舰无需专门锁定目标：CombatWorld 每步为所有舰自动选择射程内最近敌舰开火。
                Notify($"{squad.Count + (_playerSelected ? 1 : 0)} 艘舰集火 {enemy.Name}");
            }
            else
            {
                if (_playerSelected)
                { _playerCommand = at; _playerHold = false; }
                for (var i = 0; i < squad.Count; i++)
                {
                    var v = squad[i];
                    // 旗舰参与编队时占 0 号位，自治舰从 1 号位开始落位。
                    v.CommandDestination = at + FormationOffset(i + (_playerSelected ? 1 : 0));
                    v.CombatTarget = null; v.Escort = false; v.Hold = false;
                }
                Notify($"{squad.Count + (_playerSelected ? 1 : 0)} 艘舰前往指定位置");
            }
            return true;
        }
        return false;
    }
    /// <summary>编队移动落位：目标点右侧展开纵列队形。</summary>
    private static Vector2 FormationOffset(int index) => index switch
    {
        0 => Vector2.Zero,
        1 => new Vector2(-140, -120),
        2 => new Vector2(-140, 120),
        3 => new Vector2(-280, -220),
        4 => new Vector2(-280, 0),
        5 => new Vector2(-280, 220),
        _ => new Vector2(-420, (index - 6) * 160f - 320f),
    };
    private void CloseTable()
    {
        if (_tableOpen) _cameraZoom = _beforeTableZoom;
        _tableOpen = false; _dragStartScreen = null;
        // 回到驾驶位即收回旗舰指挥权，自动航行/驻守指令作废。
        _playerCommand = null; _playerHold = false;
    }

    /// <summary>
    /// 指挥模式下的旗舰自动航行：直接构造飞行 Command（与玩家驾驶模式解耦），
    /// 移动指令朝目标旋转并按距离加减速，到达后转驻守；驻守用反向速度目标主动制动。
    /// 零分配（纯栈上结构）。返回 null 表示无指令，恢复默认静止。
    /// </summary>
    private ShipFlightController.Command? AutopilotCommand()
    {
        var localVelocity = _playerVelocity.Rotated(-_playerHeading);
        if (_playerCommand is { } target)
        {
            var offset = target - _playerPosition;
            var distance = offset.Length();
            if (distance < 80)
            {
                // 到达段：先主动制动，停稳后才转驻守。
                if (localVelocity.Length() < 15) { _playerCommand = null; _playerHold = true; }
                else return new ShipFlightController.Command(-localVelocity / ShipFlightController.MaxSpeed, 0, 0);
            }
            else
            {
                // 高速巡航 + 制动距离限速（√(2·a·剩余距离)），远途全速、到点前仍能刹停。
                var speed = Mathf.Min(1500, Mathf.Sqrt((distance - 80) * 400));
                var desired = offset / distance * speed;
                var error = Mathf.Wrap(offset.Angle() - _playerHeading, -Mathf.Pi, Mathf.Pi);
                // LocalTranslation 是期望速度/MaxSpeed：Evaluate 用其减当前速度得到速度误差，
                // 因此接近目标自动减速、超速自动反推，而非只会全速前进。
                return new ShipFlightController.Command(desired.Rotated(-_playerHeading) / ShipFlightController.MaxSpeed, error, 0);
            }
        }
        if (_playerHold)
        {
            // 非零的反向速度目标触发 Evaluate 的反向推力分配，实现主动制动。
            var translation = localVelocity.LengthSquared() > 4 ? -localVelocity / ShipFlightController.MaxSpeed : Vector2.Zero;
            return new ShipFlightController.Command(translation, 0, 0);
        }
        return null;
    }

    /// <summary>指令状态色：红=集火攻击 青=移动 黄=驻守 绿=护航。</summary>
    private Color CommandColor(UniverseTraffic.Vessel v) => v.CombatTarget is { Alive: true } ? new Color("#f46a6a")
        : v.Hold ? new Color("#d6bd7d")
        : v.CommandDestination.HasValue ? new Color("#6acbe0")
        : v.Escort ? new Color("#66d9b0") : new Color("#8b9aa8");
    private void CloseCampaignOverlays() { CloseTable(); _diplomacyOpen = false; }

    private Color FactionColor(int faction) => faction == FactionRelations.Player ? new Color("#66d9b0")
        : Progress.Diplomacy.Get(FactionRelations.Player, faction) switch
        { DiplomaticState.War or DiplomaticState.Wanted => new Color("#f46a6a"), DiplomaticState.Friendly => new Color("#6acbe0"), _ => new Color("#d6bd7d") };

    private void DrawCombatWorld(Rect2 view)
    {
        if (_mineralFields.TryGetValue(_traffic.Cell, out var minerals))
            foreach (var rock in minerals.Where(m => m.Alive && view.Grow(50).HasPoint(m.Position)))
            {
                ModularShipVisual.Draw(this, rock.Ship, rock.Position, rock.Heading);
                DrawCircle(rock.Position, 25, new Color("#b6a36c"), false, 1 / _cameraZoom);
            }
        if (_bases.Count > 0)
            foreach (var station in _bases)
                if (station.Alive && view.Grow(200).HasPoint(station.Position))
                { ModularShipVisual.Draw(this, station.Ship, station.Position, 0); DrawCircle(station.Position, 120, FactionColor(1), false, 1 / _cameraZoom); }
        foreach (var debris in Combat.Fragments)
            if (view.Grow(100).HasPoint(debris.Position)) ModularShipVisual.Draw(this, debris.Ship, debris.Position, debris.Heading);
        foreach (var resource in Combat.Resources)
            if (view.HasPoint(resource.Position)) DrawCircle(resource.Position, Math.Max(4, 3 / _cameraZoom), new Color("#a8e678"));
        foreach (var projectile in Combat.Projectiles)
            if (view.Grow(30).HasPoint(projectile.Position))
                DrawLine(projectile.Previous - projectile.Velocity.Normalized() * 12, projectile.Position,
                    projectile.Kind is WeaponKind.Missile or WeaponKind.Drone ? new Color("#ffc579") : new Color("#f5e4bb"), Math.Max(2, 1 / _cameraZoom), true);
        foreach (var beam in Combat.Beams)
            DrawLine(beam.Start, beam.End, beam.Healing ? new Color("#8de889") : new Color("#ff83a8"), Math.Max(2, 1.5f / _cameraZoom), true);
        foreach (var actor in _combatActors)
            if (actor.Alive && actor.Shield > 0 && view.Grow(actor.ShieldRadius).HasPoint(actor.Position))
                DrawCircle(actor.Position, actor.ShieldRadius, new Color(0.35f, 0.7f, 1, 0.2f + 0.4f * actor.Shield / actor.ShieldCapacity), false, 1 / _cameraZoom);
        if (_tableOpen)
        {
            // 旗舰的指令状态环（青=移动 黄=驻守 绿=待命）。
            if (_ship is { HasLiveCore: true })
            {
                var flagshipTint = _playerCommand.HasValue ? new Color("#6acbe0")
                    : _playerHold ? new Color("#d6bd7d") : new Color("#66d9b0");
                if (_playerSelected)
                {
                    DrawCircle(_playerPosition, (_ship.BoundingRadius() + 25), flagshipTint, false, 1 / _cameraZoom);
                    if (_playerCommand is { } flagshipTarget)
                        DrawLine(_playerPosition, flagshipTarget, new Color("#6acbe0", 0.6f), 1 / _cameraZoom);
                }
                else DrawCircle(_playerPosition, _ship.BoundingRadius() + 16, new Color(flagshipTint.R, flagshipTint.G, flagshipTint.B, 0.55f), false, 1 / _cameraZoom);
            }
            // 指挥面板开启时为全部自有舰画指令状态环（未选中的弱化显示）。
            foreach (var v in _traffic.Vessels)
            {
                if (!v.PlayerOwned || !v.Alive || _selectedFleet.Contains(v)) continue;
                var tint = CommandColor(v);
                DrawCircle(v.Position, v.Radius + 16, new Color(tint.R, tint.G, tint.B, 0.55f), false, 1 / _cameraZoom);
            }
            foreach (var selected in _selectedFleet)
            {
                if (!selected.Alive) continue;
                DrawCircle(selected.Position, selected.Radius + 25, CommandColor(selected), false, 1 / _cameraZoom);
                if (selected.CombatTarget is { Alive: true } enemy)
                    DrawLine(selected.Position, enemy.Position, new Color("#f46a6a", 0.75f), 1 / _cameraZoom);
                else if (selected.CommandDestination is { } target)
                    DrawLine(selected.Position, target, new Color("#6acbe0", 0.6f), 1 / _cameraZoom);
            }
        }
    }
    /// <summary>飞行 HUD 的进行中合约摘要：前两项进度 + 总数，供 DrawCampaignOverlays 每帧绘制。</summary>
    private string ActiveMissionSummary()
    {
        string? first = null, second = null;
        var total = 0;
        foreach (var mission in Progress.Missions)
        {
            if (!mission.Accepted || mission.Claimed) continue;
            var def = MissionCatalog.Find(mission.Id);
            if (def is null) continue;
            total++;
            var item = $"{def.Title} {mission.Progress}/{def.Goal}";
            if (first is null) first = item;
            else if (second is null) second = item;
        }
        if (total == 0) return "";
        var text = second is null ? first! : $"{first} · {second}";
        if (total > 2) text += $" 等 {total} 项";
        return $"合约进行中：{text}";
    }

    private void DrawCampaignOverlays()
    {
        DrawString(_font, new Vector2(24, 218), $"R {Progress.Resources}  ·  {Progress.Diplomacy.Name(1, Progress.Established)}  ·  自有舰船 {(_ship?.HasLiveCore == true ? 1 : 0) + _traffic.Vessels.Count(v => v.PlayerOwned && v.Alive)}", fontSize: 13, modulate: ReassemblyUi.CyanHighlight);
        if (PlayerCombat is { } player)
            DrawString(_font, new Vector2(24, 238), $"能量 {player.Energy:0}/{player.EnergyCapacity:0}  护盾 {player.Shield:0}/{player.ShieldCapacity:0}", fontSize: 12, modulate: ReassemblyUi.TextSteel);
        DrawString(_font, new Vector2(24, 258), "左键射击 / 采矿 · 靠近回收 R", fontSize: 12, modulate: ReassemblyUi.TextMuted);
        if (Progress.Colonies.Count > 0 || Progress.Factories.Count > 0)
        {
            var income = Progress.Colonies.Count * 12 + Progress.FactoryIncomeRate() * 60;
            DrawString(_font, new Vector2(24, 298), $"领地 {Progress.Colonies.Count} 座前哨 · 认领 {Progress.ClaimedPlanets.Count} 星 · 工厂 {Progress.Factories.Sum(f => f.Count)} 座 · 收益 {income:0.#} R/分", fontSize: 12, modulate: ReassemblyUi.PhosphorGreen);
        }
        if (!_tableOpen && !_diplomacyOpen)
        {
            var summary = ActiveMissionSummary();
            if (summary.Length > 0)
                DrawString(_font, new Vector2(24, 278), summary, fontSize: 12, modulate: ReassemblyUi.TextMuted);
        }
        if (_tableOpen)
        {
            // 拖框选择矩形（屏幕空间）。
            if (_dragStartScreen is { } start && _pointerScreen is { } pointer)
            {
                var dragRect = new Rect2(new Vector2(Mathf.Min(start.X, pointer.X), Mathf.Min(start.Y, pointer.Y)),
                    (start - pointer).Abs());
                DrawRect(dragRect, new Color("#6acbe0", 0.10f));
                DrawRect(dragRect, new Color("#6acbe0", 0.55f), false, 1);
            }
            var rect = new Rect2(20, 285, 360, 130);
            ReassemblyUi.DrawChamferFrame(this, rect, ReassemblyUi.VoidDark, ReassemblyUi.CyanMuted, 6, true);
            var ownedAlive = 1 + _traffic.Vessels.Count(v => v.PlayerOwned && v.Alive);
            var selectedAlive = _selectedFleet.Count(v => v.Alive) + (_playerSelected && _ship is { HasLiveCore: true } ? 1 : 0);
            DrawString(_font, new Vector2(36, 310), $"舰队指挥 / TAB · 已选 {selectedAlive} / 自有 {ownedAlive} 艘（含旗舰）", fontSize: 16, modulate: ReassemblyUi.CyanHighlight);
            DrawString(_font, new Vector2(36, 335), "左键选择 / 拖框多选 · 右键移动 · 右键敌舰集火", fontSize: 13, modulate: ReassemblyUi.TextSteel);
            DrawString(_font, new Vector2(36, 359), "A 全选 · S 驻守 · G 集结 · Ctrl+1..3 编队 · 1..3 选组", fontSize: 13, modulate: ReassemblyUi.TextSteel);
            DrawString(_font, new Vector2(36, 384), "环色 红=攻击 青=移动 黄=驻守 绿=护航 · B 建站 · N 造舰", fontSize: 13, modulate: ReassemblyUi.TextSteel);
        }
        if (!_diplomacyOpen) return;
        var panel = new Rect2(270, 170, 750, 345);
        ReassemblyUi.DrawChamferFrame(this, panel, ReassemblyUi.VoidDark, ReassemblyUi.CyanHighlight, 8, true);
        DrawString(_font, new Vector2(300, 208), "派系交互 · 只有交战派系才会互相攻击", fontSize: 20, modulate: ReassemblyUi.CyanHighlight);
        DrawString(_font, new Vector2(300, 238), "援助可建立友好关系；停战立即停止开火。无端袭击中立派系会被通缉。", fontSize: 13, modulate: ReassemblyUi.TextSteel);
        _diplomaticButtons.Clear();
        var index = 0;
        foreach (var faction in new[] { FactionRelations.League, FactionRelations.Red, FactionRelations.Crystal })
        {
            var y = 270 + index++ * 68;
            var state = Progress.Diplomacy.Get(1, faction);
            DrawString(_font, new Vector2(300, y + 24), $"{Progress.Diplomacy.Name(faction)} · {FactionRelations.Label(state)}", fontSize: 15, modulate: FactionColor(faction));
            var action = state is DiplomaticState.War or DiplomaticState.Wanted ? "peace" : "war";
            foreach (var item in new[]
            {
                (X: 650f, Action: action, Text: action == "war" ? "宣战" : "停战"),
                (X: 775f, Action: "wanted", Text: "通缉"),
                (X: 900f, Action: "aid", Text: "援助 50 R")
            })
            {
                var rect = new Rect2(item.X, y, 115, 34);
                _diplomaticButtons.Add((rect, faction, item.Action));
                ReassemblyUi.DrawChamferFrame(this, rect, ReassemblyUi.VoidDark, item.Action == "war" ? ReassemblyUi.TacticalAmber : ReassemblyUi.CyanMuted, 4, false);
                DrawString(_font, rect.Position + new Vector2(12, 23), item.Text, fontSize: 14, modulate: ReassemblyUi.TextWhite);
            }
        }
        DrawString(_font, new Vector2(300, 493), "[V / ESC] 关闭    资源与科技解锁会自动保存", fontSize: 13, modulate: ReassemblyUi.TextMuted);
    }
}
