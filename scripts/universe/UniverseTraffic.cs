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
        public bool PlayerOwned => FactionId == FactionRelations.Player;
        public Vector2? CommandDestination;
        public float Radius;
        public readonly ShipFlightController Pilot = new();
        public ShipFlightController.FlightProfile Profile;
        public readonly Dictionary<int, float> Activity = new();
        public int Waypoint;
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
        var core = ship.AddBlock("core", Vector2.Zero, 0);
        // Radial branches use the same edge-port connection model as the editor.
        foreach (var p in new[] { 0, 2, 4, 5 })
        {
            var block = Attach(ship, core, p, "hull", 2);
            for (var i = 0; i < 3; i++) block = Attach(ship, block, 0, i == 2 ? "reactor" : "hull", 2);
        }
        return ship;
    }
    private static ModularShip.Block Attach(ModularShip ship, ModularShip.Block host, int port, string shape, int self)
    {
        var snap = ModularShip.SnapBlock(ShipShapeLibrary.Get(shape).Ports[self], host.Spec.Ports[port], host.Position, host.Angle);
        return ship.AddBlock(shape, snap.Position, snap.Angle);
    }
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
            StationActors.Add(station.Id, new CombatVessel { Name = station.Name, Ship = ship, FactionId = FactionRelations.League,
                Position = station.Position, PreviousPosition = station.Position });
            AddVessel("联航巡逻舰", station.Position + Vector2.Up * 700, station.Position, false);
            AddVessel("联航运输舰", station.Position + Vector2.Left * 900, station.Position, false);
        }
        AddVessel("联航勘测舰", player + new Vector2(-480, -280), player, false);
        AddVessel("联航巡逻舰", player + new Vector2(-680, 280), player, false);
        AddVessel("赤色行商", player + new Vector2(1600, -600), player + new Vector2(1600, -600), false, FactionRelations.Red);
        AddVessel("晶体勘探舰", player + new Vector2(-1700, 900), player + new Vector2(-1700, 900), false, FactionRelations.Crystal);
        _visitedVessels[cell] = new List<Vessel>(Vessels);
        _visitedStations[cell] = new Dictionary<string, CombatVessel>(StationActors);
        BringFleet(player);
    }
    private void AddVessel(string name, Vector2 position, Vector2 anchor, bool escort, int faction = FactionRelations.League)
    {
        var ship = new ModularShip();
        ship.SeedReferenceShip();
        if (name == "联航运输舰")
        {
            var cannon = ship.Blocks.First(b => b.ShapeId == "cannon");
            var bow = ship.GetConnections().First(pair => pair.A == cannon || pair.B == cannon);
            var host = bow.A == cannon ? bow.B : bow.A;
            ship.RemoveBlock(cannon);
            Attach(ship, host, 0, "cargo", 4);
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
            vessel.CommandDestination = null; vessel.CombatTarget = null; vessel.Escort = true;
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
            var target = v.PlayerOwned && v.CommandDestination.HasValue ? v.CommandDestination.Value
                : v.Escort && v.PlayerOwned ? player + new Vector2(-240, i % 2 == 0 ? -190 : 190).Rotated(playerHeading)
                : v.Anchor + Vector2.Right.Rotated(v.Waypoint * Mathf.Tau / 8) * 900;
            if (v.CombatTarget is { Alive: true } enemy)
            {
                var away = (v.Position - enemy.Position).Normalized();
                target = enemy.Position + away * 500;
            }
            var offset = target - v.Position;
            if (!v.Escort && offset.Length() < 180) v.Waypoint = (v.Waypoint + 1) % 8;
            var error = Mathf.Wrap(offset.Angle() - v.Heading, -Mathf.Pi, Mathf.Pi);
            // Slow down before each waypoint instead of snapping position or heading.
            var thrust = offset.Length() > 100 && v.Velocity.Length() < Mathf.Min(150, offset.Length() * 0.4f)
                && Mathf.Abs(error) < 0.5f ? Vector2.Right : Vector2.Zero;
            var result = v.Pilot.Evaluate(v.Profile, new ShipFlightController.Command(thrust, error, 0),
                v.Velocity.Rotated(-v.Heading), v.AngularVelocity, v.Activity);
            var acceleration = result.LocalForce.Rotated(v.Heading) / v.Profile.TotalMass;
            ShipFlightController.Integrate(ref v.Position, ref v.Velocity, ref v.Heading, ref v.AngularVelocity,
                acceleration, result.Torque / v.Profile.MomentOfInertia, dt);
            // IReadOnlyList enumeration boxes List<T>.Enumerator; avoid six allocations per tick.
            for (var b = 0; b < bodies.Count; b++)
                UniverseNavigation.ResolveSurface(bodies[b], v.Radius, ref v.Position, ref v.Velocity);
        }
    }
}
