using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>Friendly modular clusters: fixed stations, local patrols and two escorts. No hostile factions.</summary>
public sealed class UniverseTraffic
{
    public sealed class Vessel
    {
        public required string Name;
        public required ModularShip Ship;
        public required Vector2 Anchor;
        public Vector2 Position, PreviousPosition, Velocity;
        public float Heading, PreviousHeading, AngularVelocity;
        public bool Escort;
        public float Radius;
        public readonly ShipFlightController Pilot = new();
        public ShipFlightController.FlightProfile Profile;
        public readonly Dictionary<int, float> Activity = new();
        public int Waypoint;
    }
    public readonly List<Vessel> Vessels = new(6);
    public readonly Dictionary<string, ModularShip> Stations = new();
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
        var system = UniverseGenerator.GenerateSystem(cell);
        foreach (var station in system.Bodies.Where(b => b.Kind == UniverseGenerator.CelestialKind.Station))
        {
            Stations.Add(station.Id, BuildStation());
            AddVessel("联航巡逻舰", station.Position + Vector2.Up * 700, station.Position, false);
            AddVessel("联航运输舰", station.Position + Vector2.Left * 900, station.Position, false);
        }
        AddVessel("友方护航 · 一", player + new Vector2(-180, -180), player, true);
        AddVessel("友方护航 · 二", player + new Vector2(-220, 180), player, true);
    }
    private void AddVessel(string name, Vector2 position, Vector2 anchor, bool escort)
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
            PreviousPosition = position, Escort = escort, Radius = ship.BoundingRadius() };
        vessel.Profile = ShipFlightController.Capture(ship);
        Vessels.Add(vessel);
    }
    public void Step(float dt, Vector2 player, float playerHeading, IReadOnlyList<UniverseGenerator.CelestialBody> bodies)
    {
        for (var i = 0; i < Vessels.Count; i++)
        {
            var v = Vessels[i];
            v.PreviousPosition = v.Position;
            v.PreviousHeading = v.Heading;
            var target = v.Escort ? player + new Vector2(-240, i % 2 == 0 ? -190 : 190).Rotated(playerHeading)
                : v.Anchor + Vector2.Right.Rotated(v.Waypoint * Mathf.Tau / 8) * 900;
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
