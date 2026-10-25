using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>Independent faction traffic. Player ownership is explicit and never inferred from friendliness.</summary>
public sealed class UniverseTraffic
{
    public sealed class Vessel : CombatVessel
    {
        public required Vector2 Anchor;
        public bool Escort;
        /// <summary>驻守指令：忽略锚点巡逻，锚定在当前位置。</summary>
        public bool Hold;
        public bool PlayerOwned => FactionId == FactionRelations.Player;
        public Vector2? CommandDestination;
        public float Radius;
        public readonly ShipFlightController Pilot = new();
        public ShipFlightController.FlightProfile Profile;
        public readonly Dictionary<int, float> Activity = new();
        public int Waypoint;
        /// <summary>当前指令状态描述（指挥面板与状态环共用）。</summary>
        public string CommandLabel => CombatTarget is { Alive: true } ? "攻击"
            : Hold ? "驻守" : CommandDestination.HasValue ? "移动" : Escort ? "护航" : "待命";
    }
    public readonly List<Vessel> Vessels = new(6);
    public readonly Dictionary<string, ModularShip> Stations = new();
    public readonly Dictionary<string, CombatVessel> StationActors = new();
    private readonly Dictionary<Vector2I, List<Vessel>> _visitedVessels = new();
    private readonly Dictionary<Vector2I, Dictionary<string, CombatVessel>> _visitedStations = new();
    private readonly List<Vessel> _owned = new();
    public Vector2I Cell { get; private set; } = new(int.MaxValue, int.MaxValue);
    public static ModularShip BuildStation()
    {
        var ship = new ModularShip();
        var core = ship.AddBlock("ra_200", Vector2.Zero, 0);
        var weaponHosts = new List<ModularShip.Block>();
        // Radial branches use the same edge-port connection model as the editor.
        foreach (var p in new[] { 0, 2, 4, 5 })
        {
            var block = Attach(ship, core, p, "ra_219", 2);
            for (var i = 0; i < 3; i++) block = Attach(ship, block, 0, i == 2 ? "ra_205" : "ra_219", 2);
            weaponHosts.Add(block);
        }
        foreach (var host in weaponHosts) Attach(ship, host, 1, "ra_56", 2);
        return ship;
    }
    private static ModularShip.Block Attach(ModularShip ship, ModularShip.Block host, int port, string shape, int self)
    {
        var selfPorts = ShipShapeLibrary.Get(shape).Ports;
        var hostPorts = host.Spec.Ports;
        if (selfPorts.Count == 0 || hostPorts.Count == 0)
            throw new InvalidOperationException($"Cannot attach {shape}: missing port geometry");
        var snap = ModularShip.SnapBlock(selfPorts[Mathf.PosMod(self, selfPorts.Count)],
            hostPorts[Mathf.PosMod(port, hostPorts.Count)], host.Position, host.Angle);
        return ship.AddBlock(shape, snap.Position, snap.Angle);
    }
    /// <summary>站点守卫与巡逻舰按归属派系命名前缀。</summary>
    public static string FactionPrefix(int faction) => faction switch
    {
        FactionRelations.Red => "赤色",
        FactionRelations.Crystal => "晶体",
        FactionRelations.Pirates => "掠夺者",
        4 => "廷克",
        6 => "博格",
        _ => "联航",
    };

    public void Load(Vector2I cell, Vector2 player)
    {
        if (Cell == cell) return;
        Cell = cell;
        Vessels.Clear();
        Stations.Clear();
        StationActors.Clear();
        if (_visitedVessels.TryGetValue(cell, out var existing))
        {
            Vessels.AddRange(existing);
            foreach (var (id, actor) in _visitedStations[cell]) { Stations[id] = actor.Ship; StationActors[id] = actor; }
            BringFleet(player);
            return;
        }
        var system = UniverseGenerator.GenerateSystem(cell);
        foreach (var station in system.Bodies.Where(b => b.Kind == UniverseGenerator.CelestialKind.Station))
        {
            var ship = BuildStation();
            Stations.Add(station.Id, ship);
            StationActors.Add(station.Id, new CombatVessel { Name = station.Name, Ship = ship, FactionId = station.FactionId,
                Position = station.Position, PreviousPosition = station.Position, CollisionStatic = true });
            AddVessel($"{FactionPrefix(station.FactionId)}巡逻舰", station.Position + Vector2.Up * 700, station.Position, false, station.FactionId);
            AddVessel($"{FactionPrefix(station.FactionId)}运输舰", station.Position + Vector2.Left * 900, station.Position, false, station.FactionId);
        }
        AddVessel("联航勘测舰", player + new Vector2(-480, -280), player, false);
        AddVessel("联航巡逻舰", player + new Vector2(-680, 280), player, false);
        if (UniverseGenerator.GenerateEnemyFactions)
        {
            AddVessel("赤色行商", player + new Vector2(1600, -600), player + new Vector2(1600, -600), false, FactionRelations.Red);
            AddVessel("晶体勘探舰", player + new Vector2(-1700, 900), player + new Vector2(-1700, 900), false, FactionRelations.Crystal);
        }
        // 海盗空域驻军：掠夺者与所有势力永久交战，靠近即主动开火。
        if (system.Bodies.FirstOrDefault(b => b.Kind == UniverseGenerator.CelestialKind.PirateZone) is { } zone)
            for (var i = 0; i < 4; i++)
                AddVessel(i switch
                {
                    0 => "掠夺者拦截舰",
                    1 => "掠夺者炮舰",
                    2 => "掠夺者突袭舰",
                    _ => "掠夺者哨戒舰",
                }, zone.Position + new Vector2(-640 + (i % 2) * 1280, (i / 2 - 0.5f) * 760),
                    zone.Position, false, FactionRelations.Pirates);
        _visitedVessels[cell] = new List<Vessel>(Vessels);
        _visitedStations[cell] = new Dictionary<string, CombatVessel>(StationActors);
        BringFleet(player);
    }
    private void AddVessel(string name, Vector2 position, Vector2 anchor, bool escort, int faction = FactionRelations.League)
    {
        var ship = new ModularShip();
        ship.SeedReferenceShip();
        if (name.EndsWith("运输舰", StringComparison.Ordinal))
        {
            var cannon = ship.Blocks.First(b => b.Spec.NumId == 56);
            var bow = ship.GetConnections().First(pair => pair.A == cannon || pair.B == cannon);
            var host = bow.A == cannon ? bow.B : bow.A;
            ship.RemoveBlock(cannon);
            Attach(ship, host, 0, "ra_201", 4);
        }
        var vessel = new Vessel { Name = name, Ship = ship, Anchor = anchor, Position = position,
            PreviousPosition = position, Escort = escort, FactionId = faction, Radius = ship.BoundingRadius() };
        vessel.Profile = ShipFlightController.Capture(ship);
        Vessels.Add(vessel);
    }
    public Vessel AddOwned(ModularShip ship, Vector2 position, string name)
    {
        var vessel = new Vessel { Name = name, Ship = ship, Anchor = position, Position = position, PreviousPosition = position,
            FactionId = FactionRelations.Player, Escort = true, Radius = ship.BoundingRadius() };
        vessel.Profile = ShipFlightController.Capture(ship);
        Vessels.Add(vessel);
        _owned.Add(vessel);
        return vessel;
    }
    private void BringFleet(Vector2 player)
    {
        var index = 0;
        foreach (var vessel in _owned.Where(v => v.Alive))
        {
            vessel.Position = vessel.PreviousPosition = player + new Vector2(-260, 170 * (++index));
            vessel.Velocity = Vector2.Zero; vessel.AngularVelocity = 0;
            vessel.CommandDestination = null; vessel.CombatTarget = null; vessel.Escort = true; vessel.Hold = false;
            Vessels.Add(vessel);
        }
    }
    public void Step(float dt, Vector2 player, float playerHeading, IReadOnlyList<UniverseGenerator.CelestialBody> bodies)
    {
        for (var i = 0; i < Vessels.Count; i++)
        {
            var v = Vessels[i];
            if (!v.Alive) { v.Activity.Clear(); continue; }
            if (v.Profile.Revision != v.Ship.Revision)
            { v.Profile = ShipFlightController.Capture(v.Ship); v.Radius = v.Ship.BoundingRadius(); }
            v.PreviousPosition = v.Position;
            v.PreviousHeading = v.Heading;
            // 指令优先级：集火攻击 > 驻守 > 移动指令 > 护航跟随 > 锚点巡逻。
            Vector2 target;
            if (v.CombatTarget is { Alive: true } enemy)
            {
                var away = (v.Position - enemy.Position).Normalized();
                target = enemy.Position + away * 500;
            }
            else if (v.PlayerOwned && v.Hold) target = v.Position;
            else if (v.PlayerOwned && v.CommandDestination.HasValue) target = v.CommandDestination.Value;
            else if (v.Escort && v.PlayerOwned)
                target = player + new Vector2(-240, i % 2 == 0 ? -190 : 190).Rotated(playerHeading);
            else target = v.Anchor + Vector2.Right.Rotated(v.Waypoint * Mathf.Tau / 8) * 900;
            var offset = target - v.Position;
            var distance = offset.Length();
            if (!v.Escort && distance < 180) v.Waypoint = (v.Waypoint + 1) % 8;
            var desired = Vector2.Zero;
            if (!(v.PlayerOwned && v.Hold) && distance > 40)
            {
                float speed;
                if (v.PlayerOwned && v.CommandDestination.HasValue)
                    // 高速巡航 + 制动距离限速（√(2·a·剩余距离)）：远途全速，到点前仍能刹停。
                    speed = Mathf.Min(1500, Mathf.Sqrt(Mathf.Max(distance - 100, 0) * 400));
                else if (v.CombatTarget is { Alive: true }) speed = Mathf.Min(700, distance * 1.5f);
                else if (v.Escort && v.PlayerOwned) speed = Mathf.Min(1500, distance * 1.5f);
                else speed = Mathf.Min(150, distance * 1.5f);
                desired = offset / Mathf.Max(distance, 0.001f) * speed;
            }
            // 移动指令到点：先主动制动，停稳后才转驻守，不再全速绕圈。
            if (v.PlayerOwned && v.CommandDestination.HasValue && distance < 100)
            {
                desired = Vector2.Zero;
                if (v.Velocity.Length() < 12) { v.CommandDestination = null; v.Hold = true; }
            }
            var error = distance > 1 ? Mathf.Wrap(offset.Angle() - v.Heading, -Mathf.Pi, Mathf.Pi) : 0;
            // 期望速度 → 速度误差指令：Evaluate 按 translation*MaxSpeed - localVelocity 分配推力，
            // 超过期望速度自动反向制动；驻守/到达时给反向速度目标主动刹停，不再只会前进绕圈。
            var localVelocity = v.Velocity.Rotated(-v.Heading);
            var translation = desired.Rotated(-v.Heading) / ShipFlightController.MaxSpeed;
            if (translation.IsZeroApprox() && localVelocity.LengthSquared() > 4)
                translation = -localVelocity / ShipFlightController.MaxSpeed;
            var result = v.Pilot.Evaluate(v.Profile, new ShipFlightController.Command(translation, error, 0),
                localVelocity, v.AngularVelocity, v.Activity);
            var acceleration = result.LocalForce.Rotated(v.Heading) / v.Profile.TotalMass;
            ShipFlightController.Integrate(ref v.Position, ref v.Velocity, ref v.Heading, ref v.AngularVelocity,
                acceleration, result.Torque / v.Profile.MomentOfInertia, dt);
            // IReadOnlyList enumeration boxes List<T>.Enumerator; avoid six allocations per tick.
            for (var b = 0; b < bodies.Count; b++)
            {
                var impact = UniverseNavigation.ResolveSurface(bodies[b], v.Radius, ref v.Position, ref v.Velocity);
                if (impact > 0 && bodies[b].CausesSurfaceDamage)
                {
                    if (impact > v.SurfaceImpact)
                    { v.SurfaceImpact = impact; v.SurfaceNormal = (v.Position - bodies[b].Position).Normalized(); }
                }
            }
        }
    }
}
