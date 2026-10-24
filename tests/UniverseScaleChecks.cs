using Godot;
using System;
using System.Linq;

public partial class UniverseScaleChecks : Node
{
    private int _checks;
    private void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    public override void _Ready()
    {
        try
        {
            var planets = 0;
            var stars = 0;
            for (var x = -8; x <= 8; x++)
            for (var y = -8; y <= 8; y++)
            {
                var sector = new Vector2I(x, y);
                var bodies = UniverseGenerator.GenerateSector(sector);
                var repeat = UniverseGenerator.GenerateSector(sector);
                Check(bodies.Count is >= 1 and <= 2, "Large celestial bodies remain sparse");
                Check(bodies.Select(b => (b.Id, b.Kind, b.Position, b.Radius, b.Biome)).SequenceEqual(
                    repeat.Select(b => (b.Id, b.Kind, b.Position, b.Radius, b.Biome))), "Regeneration retains body identity, surface and position");
                foreach (var body in bodies)
                {
                    var offset = (body.Position - UniverseGenerator.SectorToWorldCenter(sector)).Abs();
                    Check(offset.X + body.Radius <= UniverseGenerator.SectorSize / 2 - 239 &&
                        offset.Y + body.Radius <= UniverseGenerator.SectorSize / 2 - 239,
                        "Body leaves clearance at sector edges, including negative sectors");
                    if (body.Kind == UniverseGenerator.CelestialKind.Planet)
                    {
                        planets++;
                        Check(body.Radius >= 640 && body.Radius <= 1100, "Planet uses enlarged navigation radius");
                        Check(body.ApproachRadius > body.Radius + 200 && body.GravityRadius > body.ApproachRadius,
                            "Approach band and gravity field lie outside solid surface");
                        var spawn = UniverseNavigation.SpawnNear(body, bodies, 65);
                        Check(bodies.Where(b => b.HasSurface).All(b => b.SurfaceDistance(spawn) >= 97), "Spawn clears every solid body");
                        var velocity = new Vector2(-100, 35);
                        var position = body.Position + Vector2.Right * (body.Radius + 20);
                        Check(UniverseNavigation.ResolveSurface(body, 65, ref position, ref velocity), "Ship contact uses actual planet radius");
                        Check(Mathf.Abs(body.SurfaceDistance(position) - 81) < 0.02f && Mathf.Abs(velocity.X) < 0.01f && velocity.Y == 35,
                            "Surface contact removes inward velocity and preserves tangential flight");
                        velocity = Vector2.Right * 100;
                        position = body.Position;
                        UniverseNavigation.ResolveSurface(body, 65, ref position, ref velocity);
                        Check(float.IsFinite(position.X) && float.IsFinite(position.Y) && body.SurfaceDistance(position) > 80,
                            "Even an inside-body position resolves without a zero-normal singularity");
                    }
                    if (body.Kind == UniverseGenerator.CelestialKind.Star)
                    {
                        stars++;
                        Check(body.Radius > UniverseGenerator.PlanetRadiusMax, "Stars remain larger than planets");
                    }
                }
                for (var i = 0; i < bodies.Count; i++)
                for (var j = i + 1; j < bodies.Count; j++)
                    Check(bodies[i].Position.DistanceTo(bodies[j].Position) >= bodies[i].Radius + bodies[j].Radius + 359,
                        "Generated objects never overlap within a sector");
            }
            Check(planets > 100 && stars > 10, "Sample covers many planets and stars");
            Check(!UniverseGenerator.GenerateEnemyFactions, "No hostile faction generation");
            var view = new UniverseView();
            AddChild(view);
            view.SetProcess(false);
            Check(view.TargetBody is { Kind: UniverseGenerator.CelestialKind.Planet }, "Game starts with a real home planet target");
            Check(view.TargetBody!.SurfaceDistance(view.PlayerPosition) >= 380 - 0.01f, "Opening camera starts outside the enlarged home planet");
            for (var i = 0; i < 40; i++)
                view._Input(new InputEventMouseButton { ButtonIndex = MouseButton.WheelDown, Pressed = true, Position = new Vector2(640, 360) });
            Check(view.TargetBody.Id == UniverseGenerator.GenerateSector(Vector2I.Zero)[0].Id,
                "Zooming to navigation scale preserves selected planet identity");
            view.QueueFree();
            GD.Print($"UNIVERSE_SCALE_CHECKS_PASS: {_checks} assertions");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
    }
}
