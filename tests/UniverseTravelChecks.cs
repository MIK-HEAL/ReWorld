using Godot;
using System;
using System.Linq;
using System.Reflection;

public partial class UniverseTravelChecks : Node
{
    private int _checks;
    private void Check(bool condition, string message)
    { _checks++; if (!condition) throw new InvalidOperationException(message); }
    private static void Set(UniverseView view, string field, object value)
        => typeof(UniverseView).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, value);
    public override async void _Ready()
    {
        try
        {
            var drive = new WarpDrive();
            Check(!drive.Engage(Vector2I.Zero, Vector2I.Zero, true, false, true), "Cannot warp to current system");
            Check(!drive.Engage(Vector2I.Zero, new Vector2I(2, 0), true, false, true), "Range enforced");
            Check(!drive.Engage(Vector2I.Zero, Vector2I.Right, false, false, true), "Core required");
            Check(!drive.Engage(Vector2I.Zero, Vector2I.Right, true, true, true), "Undock required");
            Check(!drive.Engage(Vector2I.Zero, Vector2I.Right, true, false, false), "Surface clearance required");
            Check(drive.Engage(Vector2I.Zero, Vector2I.Right, true, false, true), "Charging starts");
            Check(drive.Cancel() && !drive.LocksFlight, "Charging can cancel without teleporting");
            drive.Engage(Vector2I.Zero, Vector2I.Right, true, false, true);
            Check(!drive.Advance(3.5f) && drive.State == WarpDrive.Phase.Transit, "Charging crosses into tunnel without arrival");
            Check(!drive.Cancel(), "Cannot cancel mid tunnel");
            Check(drive.Advance(3.5f) && drive.State == WarpDrive.Phase.Cooldown, "Exactly one arrival at tunnel exit");
            Check(!drive.Engage(Vector2I.Right, Vector2I.Zero, true, false, true), "Cooldown blocks repeated warp");
            Check(!drive.Advance(20) && drive.State == WarpDrive.Phase.Ready, "No repeat arrival during cooldown");
            drive.Engage(Vector2I.Zero, Vector2I.One, true, false, true);
            Check(drive.Advance(12) && drive.State == WarpDrive.Phase.Ready, "Large time step preserves arrival once");

            var ship = new ModularShip(); ship.SeedReferenceShip();
            var view = new UniverseView(); view.SetShip(ship); AddChild(view);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(view.Traffic.Stations.Count == 2 && view.Traffic.Vessels.Count == 6, "Two stations, four local ships, two escorts");
            foreach (var station in view.Traffic.Stations.Values)
                Check(station.GetConnectedComponents().Count == 1 && station.Core is not null, "Station is one actual modular cluster");
            Check(!UniverseGenerator.GenerateEnemyFactions, "Enemies remain disabled");
            for (var i = 0; i < 120; i++) view.Traffic.Step(1f / 60, view.PlayerPosition, 0,
                UniverseGenerator.GenerateSystem(Vector2I.Zero).Bodies);
            Check(view.Traffic.Vessels.All(v => v.Position.IsFinite() && float.IsFinite(v.Heading)), "Friendly AI stays finite");
            Check(view.Traffic.Vessels.Any(v => v.Position.DistanceTo(v.Anchor) > 10), "Friendly traffic moves");

            var home = UniverseGenerator.GenerateSystem(Vector2I.Zero);
            var stationBody = home.Bodies.First(b => b.Kind == UniverseGenerator.CelestialKind.Station);
            Set(view, "_playerPosition", UniverseView.DockingPosition(stationBody, ship.BoundingRadius()) + Vector2.Right * 100);
            Set(view, "_playerVelocity", Vector2.Right * 300);
            Check(!view.ToggleDock(), "Docking rejects excessive speed");
            Set(view, "_playerVelocity", Vector2.Zero);
            ship.Blocks[0].Health = 1;
            Check(view.ToggleDock() && view.IsDocked, "Nearby station accepts low speed ship");
            Check(ship.Blocks.All(b => b.Health == b.Spec.Hull), "Station repairs actual blocks");
            Check(!view.BeginWarp(Vector2I.Right), "Docked ship cannot jump");
            Check(view.ToggleDock() && !view.IsDocked, "Undock returns control");
            Check(view.BeginWarp(Vector2I.Right), "Undocked ship engages warp");
            view.SetPhysicsProcess(false);
            view._PhysicsProcess(3); view._PhysicsProcess(4);
            var destination = UniverseGenerator.GenerateSystem(Vector2I.Right);
            Check(UniverseGenerator.WorldToSystemCell(view.PlayerPosition) == Vector2I.Right, "Arrives in selected system");
            Check(view.PlayerVelocity.IsZeroApprox() && view.PlayerAngularVelocity == 0, "Arrival clears momentum");
            Check(view.RenderPosition == view.PlayerPosition, "Arrival resets interpolation history");
            Check(destination.Bodies.All(b => !b.HasSurface || b.SurfaceDistance(view.PlayerPosition) > ship.BoundingRadius() + 180), "Arrival clears every solid body");
            Check(view.Traffic.Cell == Vector2I.Right && view.Traffic.Vessels.Count == 6, "Traffic reloads without accumulating fleets");
            var arrival = view.PlayerPosition;
            view._PhysicsProcess(2);
            Check(view.Warp.State == WarpDrive.Phase.Ready, "Cooldown ends in flight");
            Check(view.PlayerPosition.DistanceTo(arrival) < 20, "No second teleport");
            for (var i = 0; i < 8; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var draws = view.RetainedDrawCount;
            for (var i = 0; i < 8; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(draws > 0 && view.RetainedDrawCount == draws, "Static celestial commands remain cached over moving render frames");
            GD.Print($"UNIVERSE_TRAVEL_CHECKS_PASS: {_checks} assertions");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
}
