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
    private bool _tableOpen, _diplomacyOpen, _deathAnnounced;
    private float _repairDebt;
    private float _beforeTableZoom;
    private readonly List<CombatVessel> _combatActors = new();
    private readonly Dictionary<Vector2I, List<CombatVessel>> _mineralFields = new();
    private UniverseTraffic.Vessel? _commandSelection;
    private CombatVessel? _playerBase;
    private Vector2I _mineralCell = new(int.MaxValue, int.MaxValue);
    private readonly List<(Rect2 Rect, int Faction, string Action)> _diplomaticButtons = new();

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
        if (Progress.Established)
        {
            _playerBase = new CombatVessel { Name = "自治前哨", Ship = UniverseTraffic.BuildStation(),
                FactionId = FactionRelations.Player, Position = new(Progress.BaseX, Progress.BaseY), PreviousPosition = new(Progress.BaseX, Progress.BaseY), CollisionStatic = true };
            foreach (var lua in Progress.FleetDesigns)
            {
                var ship = ShipDesign.ParseLua(lua).ToShip();
                if (ship.HasLiveCore) _traffic.AddOwned(ship, _playerPosition + Vector2.Up * (200 + _traffic.Vessels.Count * 90), "自治舰队");
            }
        }
    }
    private void SyncCombatActors(bool includePlayer)
    {
        _combatActors.Clear();
        if (includePlayer && PlayerCombat is not null) _combatActors.Add(PlayerCombat);
        for (var i = 0; i < _traffic.Vessels.Count; i++) _combatActors.Add(_traffic.Vessels[i]);
        _combatActors.AddRange(_traffic.StationActors.Values);
        if (_playerBase is not null && _playerBase.Position.DistanceTo(_playerPosition) < UniverseGenerator.SystemRadius * 2)
            _combatActors.Add(_playerBase);
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
            _pointerScreen is { } pointer && !MiniMapRect.HasPoint(pointer) && Input.IsMouseButtonPressed(MouseButton.Left);
        Combat.Step(dt, _combatActors, Progress.Diplomacy, PlayerCombat, manual,
            _pointerScreen is { } aim ? ScreenToWorld(aim) : _playerPosition + Vector2.Right.Rotated(_playerHeading) * 1000);
        if (active)
        {
            _playerPosition = PlayerCombat.Position;
            _playerVelocity = PlayerCombat.Velocity;
            var collected = Combat.Collect(_playerPosition, PlayerCombat.HasTractor ? 450 : 100);
            if (collected > 0) { Progress.Collect(collected); Notify($"回收 {collected} R · M 造舰 / U 查看升级"); }
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
        if (!_ship.HasLiveCore && !_deathAnnounced)
        { _deathAnnounced = true; Notify("指挥核心已毁 · Enter 返回空间站并启用应急舰"); }
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
    public bool EstablishBase()
    {
        if (Progress.Established || _ship is null || !_ship.HasLiveCore || _warp.LocksFlight || IsDocked ||
            _visibleBodies.Any(b => b.HasSurface && b.SurfaceDistance(_playerPosition) < 500) ||
            _traffic.Vessels.Any(v => v.Alive && Progress.Diplomacy.Hostile(v.FactionId, FactionRelations.Player) && v.Position.DistanceTo(_playerPosition) < 1500)) return false;
        if (!Progress.Spend(1000)) return false;
        var position = _playerPosition;
        _playerBase = new CombatVessel { Name = "自治前哨", Ship = UniverseTraffic.BuildStation(),
            FactionId = FactionRelations.Player, Position = position, PreviousPosition = position, CollisionStatic = true };
        // The outpost replaces the captain's current anchorage, so no random offset can intersect a planet.
        _playerPosition += Vector2.Right * 260;
        Progress.Established = true; Progress.BaseX = position.X; Progress.BaseY = position.Y;
        Progress.Commit();
        Notify("自治前哨已建立 · Tab 指挥 · N 在前哨附近制造当前蓝图舰");
        return true;
    }
    public bool ManufactureFleetShip()
    {
        if (!Progress.Established || _playerBase is not { Alive: true } || _ship is null || !_ship.HasLiveCore ||
            _playerPosition.DistanceTo(_playerBase.Position) > 1200 || _traffic.Vessels.Count(v => v.PlayerOwned && v.Alive) >= 8 ||
            _ship.Blocks.Any(b => !Progress.CanBuild(b.ShapeId))) return false;
        var price = Math.Max(150, (int)(_ship.TotalMass * 2));
        if (!Progress.Spend(price)) return false;
        var clone = _ship.Clone();
        _traffic.AddOwned(clone, _playerBase.Position + new Vector2(250, 200 + Progress.FleetDesigns.Count * 90), $"自治舰 {Progress.FleetDesigns.Count + 1}");
        Progress.FleetDesigns.Add(ShipDesign.FromShip(clone).ToLua());
        Progress.Commit(); Notify($"舰船制造完成 · 消耗 {price} R · Tab 选择并下达指令");
        return true;
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
        if (_playerBase is { Alive: true } baseStation)
            candidates.Add((_playerPosition.DistanceSquaredTo(baseStation.Position), baseStation.Position,
                baseStation.Ship.BoundingRadius(), "自治前哨"));
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
            if (_tableOpen && key.Keycode == Key.B) { if (!EstablishBase()) Notify("建前哨需 1000 R，离开地表与交战区域"); return true; }
            if (_tableOpen && key.Keycode == Key.N) { if (!ManufactureFleetShip()) Notify("制造需自己的前哨、足够资源和已解锁蓝图，舰队上限 8 艘"); return true; }
            if (_tableOpen && key.Keycode == Key.G)
            { foreach (var v in _traffic.Vessels.Where(v => v.PlayerOwned)) { v.CommandDestination = null; v.Escort = true; } return true; }
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
        if (_tableOpen && input is InputEventMouseButton { Pressed: true } mouse)
        {
            var at = ScreenToWorld(mouse.Position);
            if (mouse.ButtonIndex == MouseButton.Left)
                _commandSelection = _traffic.Vessels.Where(v => v.PlayerOwned && v.Alive && v.Position.DistanceTo(at) < Math.Max(100, v.Radius))
                    .MinBy(v => v.Position.DistanceSquaredTo(at));
            if (mouse.ButtonIndex == MouseButton.Right && _commandSelection is { Alive: true, PlayerOwned: true } selected)
            { selected.CommandDestination = at; selected.Escort = false; Notify($"{selected.Name}：前往指定位置"); }
            return true;
        }
        return false;
    }
    private void CloseTable() { if (_tableOpen) _cameraZoom = _beforeTableZoom; _tableOpen = false; }
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
        if (_playerBase is { Alive: true } station && view.Grow(200).HasPoint(station.Position))
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
        if (_commandSelection is { Alive: true } selected && _tableOpen)
        {
            DrawCircle(selected.Position, selected.Radius + 25, Colors.White, false, 1 / _cameraZoom);
            if (selected.CommandDestination is { } target) DrawLine(selected.Position, target, Colors.LightGreen, 1 / _cameraZoom);
        }
    }
    private void DrawCampaignOverlays()
    {
        DrawString(_font, new Vector2(24, 218), $"R {Progress.Resources}  ·  {Progress.Diplomacy.Name(1, Progress.Established)}  ·  自有舰船 {(_ship?.HasLiveCore == true ? 1 : 0) + _traffic.Vessels.Count(v => v.PlayerOwned && v.Alive)}", fontSize: 13, modulate: ReassemblyUi.CyanHighlight);
        if (PlayerCombat is { } player)
            DrawString(_font, new Vector2(24, 238), $"能量 {player.Energy:0}/{player.EnergyCapacity:0}  护盾 {player.Shield:0}/{player.ShieldCapacity:0}", fontSize: 12, modulate: ReassemblyUi.TextSteel);
        DrawString(_font, new Vector2(24, 258), "左键射击 / 采矿 · 靠近回收 R", fontSize: 12, modulate: ReassemblyUi.TextMuted);
        if (_tableOpen)
        {
            var rect = new Rect2(20, 285, 360, 130);
            ReassemblyUi.DrawChamferFrame(this, rect, ReassemblyUi.VoidDark, ReassemblyUi.CyanMuted, 6, true);
            DrawString(_font, new Vector2(36, 310), "舰队指挥 / TAB", fontSize: 16, modulate: ReassemblyUi.CyanHighlight);
            DrawString(_font, new Vector2(36, 335), "左键选择自有舰 · 右键下达移动指令", fontSize: 13, modulate: ReassemblyUi.TextSteel);
            DrawString(_font, new Vector2(36, 359), "G 集结跟随 · B 建立前哨（1000 R）", fontSize: 13, modulate: ReassemblyUi.TextSteel);
            DrawString(_font, new Vector2(36, 384), "N 前哨造舰 · V 派系外交 · Tab 返回驾驶", fontSize: 13, modulate: ReassemblyUi.TextSteel);
        }
        if (!_diplomacyOpen) return;
        var panel = new Rect2(270, 170, 750, 345);
        ReassemblyUi.DrawChamferFrame(this, panel, ReassemblyUi.VoidDark, ReassemblyUi.CyanHighlight, 8, true);
        DrawString(_font, new Vector2(300, 208), "派系交互 · 只有交战派系才会互相攻击", fontSize: 20, modulate: ReassemblyUi.CyanHighlight);
        DrawString(_font, new Vector2(300, 238), "援助可建立友好关系；停战立即停止双方主动开火。友好舰不归玩家指挥。", fontSize: 13, modulate: ReassemblyUi.TextSteel);
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
