using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

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
            var emptySectors = 0;
            var sample = new List<UniverseGenerator.CelestialBody>();
            const int sampleRadius = 24;
            const int sectorCount = (sampleRadius * 2 + 1) * (sampleRadius * 2 + 1);
            for (var x = -sampleRadius; x <= sampleRadius; x++)
            for (var y = -sampleRadius; y <= sampleRadius; y++)
            {
                var sector = new Vector2I(x, y);
                var bodies = UniverseGenerator.GenerateSector(sector);
                var repeat = UniverseGenerator.GenerateSector(sector);
                if (bodies.Count == 0) emptySectors++;
                sample.AddRange(bodies);
                Check(bodies.Select(b => (b.Id, b.SystemId, b.Kind, b.Position, b.Radius, b.Biome)).SequenceEqual(
                    repeat.Select(b => (b.Id, b.SystemId, b.Kind, b.Position, b.Radius, b.Biome))), "Regeneration retains body identity, system, surface and position");
                foreach (var body in bodies)
                {
                    Check(UniverseGenerator.WorldToSector(body.Position) == sector,
                        "Every body is owned by exactly its containing sector, including negative coordinates");
                    Check(body.GravityRadius <= UniverseGenerator.MaxGravityRadius, "Streaming bound covers every gravity well");
                    if (body.Kind == UniverseGenerator.CelestialKind.Planet)
                    {
                        planets++;
                        Check(body.Radius >= 640 && body.Radius <= 1100, "Planet uses enlarged navigation radius");
                        Check(body.ApproachRadius > body.Radius + 200 && body.GravityRadius > body.ApproachRadius,
                            "Approach band and gravity field lie outside solid surface");
                        Check(body.GravityRadius >= body.Radius * 10 && body.GravityStrength / Mathf.Pow(body.Radius * 8, 2) > 1,
                            "Planet gravity extends far into deep space with measurable acceleration");
                        var spawn = UniverseNavigation.SpawnNear(body, bodies, 65);
                        Check(bodies.Where(b => b.HasSurface).All(b => b.SurfaceDistance(spawn) >= 97), "Spawn clears every solid body");
                        var velocity = new Vector2(-100, 35);
                        var position = body.Position + Vector2.Right * (body.Radius + 20);
                        Check(UniverseNavigation.ResolveSurface(body, 65, ref position, ref velocity) > 0, "Ship contact uses actual planet radius");
                        Check(Mathf.Abs(body.SurfaceDistance(position) - 81) < 0.02f && Mathf.Abs(velocity.X) < 0.01f && velocity.Y == 35,
                            "Surface contact removes inward velocity and preserves tangential flight");
                        velocity = Vector2.Right * 100;
                        position = body.Position;
                        UniverseNavigation.ResolveSurface(body, 65, ref position, ref velocity);
                        Check(float.IsFinite(position.X) && float.IsFinite(position.Y) && body.SurfaceDistance(position) > 80,
                            "Even an inside-body position resolves without a zero-normal singularity");
                        Check(!body.CausesSurfaceDamage, "Planets bounce ships without dealing surface damage");
                    }
                    if (body.Kind == UniverseGenerator.CelestialKind.Star)
                    {
                        stars++;
                        Check(body.Radius > UniverseGenerator.PlanetRadiusMax, "Stars remain larger than planets");
                        Check(body.CausesSurfaceDamage, "Stars deal surface contact damage");
                    }
                }
            }
            Check(emptySectors > sectorCount * 0.9f, "System clusters leave over ninety percent of sampled sectors empty");
            Check(sample.Select(b => b.Id).Distinct().Count() == sample.Count, "Streaming sectors never duplicate a system body");
            // 旧分布每 6000² 单位约 1.5 个天体；同等面积的新密度应低于其四分之一。
            var densityRatio = sample.Count / ((float)sectorCount * UniverseGenerator.SectorSize * UniverseGenerator.SectorSize) / (1.5f / (6000 * 6000));
            Check(densityRatio < 0.25f, "World-space celestial density is substantially lower");
            for (var i = 0; i < sample.Count; i++)
            for (var j = i + 1; j < sample.Count; j++)
                Check(sample[i].Position.DistanceTo(sample[j].Position) >= sample[i].Radius + sample[j].Radius + 1199,
                    "Planetary systems avoid overlapping bodies even across sector boundaries");
            Check(planets > 30 && stars >= 9, "Sample covers many complete planetary systems");
            Systems();
            Topology();
            Missions();
            PirateDiplomacy();
            Landing();
            Check(UniverseGenerator.GenerateEnemyFactions, "Hostile faction generation is enabled");
            var view = new UniverseView();
            AddChild(view);
            view.SetProcess(false);
            Check(view.TargetBody is { Kind: UniverseGenerator.CelestialKind.Planet }, "Game starts with a real home planet target");
            Check(view.TargetBody!.SurfaceDistance(view.PlayerPosition) >= 380 - 0.01f, "Opening camera starts outside the enlarged home planet");
            for (var i = 0; i < 40; i++)
                view._Input(new InputEventMouseButton { ButtonIndex = MouseButton.WheelDown, Pressed = true, Position = new Vector2(640, 360) });
            Check(view.TargetBody.Id == UniverseGenerator.GenerateSector(Vector2I.Zero)[0].Id,
                "Zooming to navigation scale preserves selected planet identity");
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            var home = view.TargetBody;
            var distantPosition = home.Position + Vector2.Right * home.Radius * 8;
            typeof(UniverseView).GetField("_playerPosition", hidden)!.SetValue(view, distantPosition);
            Vector2? previousGravity = null;
            foreach (var zoom in new[] { 2.5f, 0.06f })
            {
                typeof(UniverseView).GetField("_cameraZoom", hidden)!.SetValue(view, zoom);
                typeof(UniverseView).GetMethod("RefreshVisibleBodies", hidden)!.Invoke(view, null);
                var loaded = (List<UniverseGenerator.CelestialBody>)typeof(UniverseView).GetField("_visibleBodies", hidden)!.GetValue(view)!;
                Check(loaded.Any(b => b.Id == home.Id), "Distant home gravity source stays loaded at every zoom");
                var expectedGravity = Vector2.Zero;
                foreach (var body in UniverseGenerator.GetNearbyBodies(distantPosition, 6))
                {
                    var delta = body.Position - distantPosition;
                    var distance = delta.Length();
                    if (distance <= 0.01f || distance > body.GravityRadius) continue;
                    expectedGravity += delta.Normalized() * body.GravityStrength / Mathf.Pow(Mathf.Max(distance, body.Radius), 2);
                }
                var actualGravity = (Vector2)typeof(UniverseView).GetMethod("GravityAcceleration", hidden)!.Invoke(view, null)!;
                Check(actualGravity.DistanceTo(expectedGravity) < 0.001f, "Offscreen wells contribute their full gravity");
                if (previousGravity is { } previous)
                    Check(actualGravity.DistanceTo(previous) < 0.001f, "Zooming does not change gravity");
                previousGravity = actualGravity;
            }
            view.QueueFree();
            GD.Print($"UNIVERSE_DENSITY: {densityRatio:P1} of previous density; {emptySectors}/{sectorCount} empty sectors");
            GD.Print($"UNIVERSE_SCALE_CHECKS_PASS: {_checks} assertions");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
    }

    private void Systems()
    {
        for (var x = -2; x <= 2; x++)
        for (var y = -2; y <= 2; y++)
        {
            var cell = new Vector2I(x, y);
            var system = UniverseGenerator.GenerateSystem(cell);
            Check(system.Bodies.Count(b => b.Kind == UniverseGenerator.CelestialKind.Star) == 1,
                "Each system contains exactly one central star");
            var star = system.Bodies.Single(b => b.Kind == UniverseGenerator.CelestialKind.Star);
            Check(star.Position == system.Center, "Star lies at its system center");
            var planets = system.Bodies.Where(b => b.Kind == UniverseGenerator.CelestialKind.Planet).ToArray();
            Check(planets.Length is >= 3 and <= 6, "Systems vary from three to six planets by seed");
            foreach (var planet in planets)
            {
                Check(planet.TerritoryRadius > planet.Radius && planet.TerritoryRadius < planet.Radius * 1.6f,
                    "Planet territory ring sits just outside the surface");
                if (cell == Vector2I.Zero)
                    Check(planet.FactionId == FactionRelations.League, "Home system planets belong to the League");
                else
                    Check(planet.FactionId == 0 || UniverseGenerator.MajorStationFactions.Contains(planet.FactionId),
                        "Frontier planets are either unclaimed or owned by a major faction");
                Check(UniverseGenerator.PlanetFactionOf(cell, Array.IndexOf(planets, planet)) == planet.FactionId,
                    "Planet faction assignment is deterministic per system and index");
            }
            var stations = system.Bodies.Count(b => b.Kind == UniverseGenerator.CelestialKind.Station);
            Check(stations is >= 1 and <= 3, "Systems operate one to three faction stations");
            var belts = system.Bodies.Count(b => b.Kind == UniverseGenerator.CelestialKind.AsteroidField);
            Check(belts >= 7, "Each system carries a peripheral debris ring in addition to inner asteroid fields");
            var zones = system.Bodies.Where(b => b.Kind == UniverseGenerator.CelestialKind.PirateZone).ToArray();
            Check(zones.Length <= 1, "Systems contain at most one pirate zone");
            if (cell == Vector2I.Zero)
                Check(zones.Length == 0, "The home system stays free of pirate zones");
            foreach (var zone in zones)
            {
                Check(zone.FactionId == FactionRelations.Pirates, "Pirate zones belong to the pirate faction");
                Check(zone.GravityRadius == 0 && !zone.HasSurface, "Pirate zones are markers without gravity or surface");
                var repeat = UniverseGenerator.GenerateSystem(cell);
                Check(repeat.Bodies.Any(b => b.Id == zone.Id && b.Position == zone.Position),
                    "Pirate zone placement is deterministic per system");
            }
            foreach (var body in system.Bodies)
            {
                Check(body.SystemId == system.Id && body.SystemName == system.Name,
                    "Every planet, station and asteroid field belongs to its star system");
                Check(body.Position.DistanceTo(system.Center) + body.Radius <= UniverseGenerator.SystemRadius,
                    "System radius encloses all body surfaces");
                var sector = UniverseGenerator.WorldToSector(body.Position);
                Check(UniverseGenerator.GenerateSector(sector).Count(b => b.Id == body.Id && b.Position == body.Position) == 1,
                    "Sector lookup retrieves exactly one matching system body");
            }
            for (var i = 1; i < planets.Length; i++)
                Check(planets[i].Position.DistanceTo(star.Position) - planets[i - 1].Position.DistanceTo(star.Position) > 3999,
                    "Planet spacing follows separated orbital bands around the star");
            var loaded = UniverseGenerator.GetNearbyBodies(system.Center, 5);
            var sectors = new List<UniverseGenerator.CelestialBody>();
            var centerSector = UniverseGenerator.WorldToSector(system.Center);
            for (var dx = -5; dx <= 5; dx++)
            for (var dy = -5; dy <= 5; dy++)
                sectors.AddRange(UniverseGenerator.GenerateSector(centerSector + new Vector2I(dx, dy)));
            Check(loaded.Select(b => b.Id).Order().SequenceEqual(sectors.Select(b => b.Id).Order()),
                "Batch streaming matches individual sectors without missing or duplicating boundary bodies");
            Check(system.Bodies.All(body => loaded.Any(b => b.Id == body.Id)), "Local system streaming loads the complete system");
            foreach (var direction in new[] { Vector2I.Right, Vector2I.Down })
            {
                var next = UniverseGenerator.SystemCenter(cell + direction);
                Check(next.DistanceTo(system.Center) - 2 * UniverseGenerator.SystemRadius >= UniverseGenerator.MinimumInterstellarGap - 1,
                    "Neighbor systems have long stretches of empty interstellar space");
                // 在两星系之间的中心连线上，距当前星系外围一万单位的空域不能撒出天体。
                var empty = system.Center + (next - system.Center).Normalized() * (UniverseGenerator.SystemRadius + 10000);
                Check(UniverseGenerator.GetNearbyBodies(empty, 0).Count == 0, "Interstellar sectors do not generate independent planets or stations");
            }
        }
    }

    private void Topology()
    {
        var linkedEdges = 0;
        for (var x = -3; x <= 3; x++)
        for (var y = -3; y <= 3; y++)
        {
            var cell = new Vector2I(x, y);
            var parent = UniverseGenerator.SpanningParent(cell);
            if (cell != Vector2I.Zero)
            {
                Check(UniverseGenerator.SystemsLinked(cell, parent), "Every cell keeps its spanning-trunk lane toward the home system");
                Check(Math.Abs(parent.X) + Math.Abs(parent.Y) < Math.Abs(x) + Math.Abs(y),
                    "Spanning parents strictly approach the origin, so trunk lanes never cycle");
            }
            var linkedNeighbors = 0;
            foreach (var direction in new[] { Vector2I.Right, Vector2I.Up, Vector2I.One, new Vector2I(1, -1) })
            {
                var neighbor = cell + direction;
                var linked = UniverseGenerator.SystemsLinked(cell, neighbor);
                Check(linked == UniverseGenerator.SystemsLinked(neighbor, cell), "Stellar lanes are symmetric");
                Check(!linked || Math.Abs(direction.X) <= 1 && Math.Abs(direction.Y) <= 1, "Lanes never skip over a system");
                if (linked) { linkedEdges++; linkedNeighbors++; }
            }
            foreach (var direction in new[] { new Vector2I(2, 0), new Vector2I(0, 3), new Vector2I(2, 1) })
                Check(!UniverseGenerator.SystemsLinked(cell, cell + direction), "Distant systems are never directly linked");
            Check(linkedNeighbors >= 1, "Every system keeps at least one outbound lane");
        }
        // 49 格区域的主干航道恰好 48 条（原点无父节点）；支线应显著超出主干数量。
        Check(linkedEdges > 60, "Random branch lanes meaningfully enrich the pure spanning trunk");
    }

    private void Missions()
    {
        for (var x = -2; x <= 2; x++)
        for (var y = -2; y <= 2; y++)
        {
            var cell = new Vector2I(x, y);
            var missions = MissionCatalog.Generate(cell);
            Check(missions.Count == 4, "Every system offers hunt, bounty, mining and anti-pirate contracts");
            var pirate = missions.Single(m => m.Kind == MissionKind.Pirate);
            Check(pirate.Faction == FactionRelations.Pirates && pirate.Goal is >= 2 and <= 3,
                "Anti-pirate contracts target the pirate faction with a small kill goal");
            Check(MissionCatalog.Find(pirate.Id) == pirate, "Anti-pirate contract resolves from its id");
            var repeat = MissionCatalog.Generate(cell);
            Check(repeat.Count == missions.Count && repeat[3].Title == missions[3].Title && repeat[3].Reward == missions[3].Reward,
                "Contract catalogs stay deterministic across regeneration");
        }
    }

    private void PirateDiplomacy()
    {
        foreach (var faction in new[] { FactionRelations.Player, FactionRelations.League, FactionRelations.Red, FactionRelations.Crystal })
            Check(FactionRelations.Pirates != faction &&
                new FactionRelations().Hostile(FactionRelations.Pirates, faction),
                "Pirates are permanently at war with every established faction");
        Check(new FactionRelations().Get(FactionRelations.Pirates, FactionRelations.Pirates) == DiplomaticState.Friendly,
            "Pirates do not fire on their own kind");
        Check(!new FactionRelations().Hostile(FactionRelations.Pirates, 100),
            "Pirate hostility ignores environmental actors");
    }

    private void Landing()
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        // 数据层：工厂报价、容量与产出聚合。
        var progress = new PlayerProgress();
        Check(PlayerProgress.FactoryCost(0) == 400 && PlayerProgress.FactoryCost(2) == 900,
            "Factory pricing scales with the local build count");
        Check(Mathf.Abs(PlayerProgress.FactoryRateOf("crystal") - 0.16f) < 1e-4f,
            "Crystal worlds run the highest-yield factories");
        for (var i = 0; i < PlayerProgress.MaxFactoriesPerPlanet; i++)
            Check(progress.TryAddFactory("planet:a", "iron"), "Factory buildout fills the planet slot by slot");
        Check(!progress.TryAddFactory("planet:a", "iron"), "Factory capacity is capped per planet");
        Check(progress.FactoryCountOf("planet:a") == PlayerProgress.MaxFactoriesPerPlanet,
            "Factory counts resolve per planet");
        progress.Factories.Add(new FactoryData { PlanetId = "planet:b", Biome = "crystal", Count = 1 });
        Check(Mathf.Abs(progress.FactoryIncomeRate() - (PlayerProgress.MaxFactoriesPerPlanet * 0.10f + 0.16f)) < 1e-3f,
            "Income rate aggregates every factory across planets and biomes");

        // 玩法层：降落 → 锁定 → 认领 → 建厂 → 起飞。
        var view = new UniverseView();
        var ship = new ModularShip();
        ship.SeedReferenceShip();
        view.SetShip(ship);
        AddChild(view);
        view.SetProcess(false);
        var system = UniverseGenerator.GenerateSystem(new Vector2I(2, -1));
        var planet = system.Bodies.First(b => b.Kind == UniverseGenerator.CelestialKind.Planet);
        var positionField = typeof(UniverseView).GetField("_playerPosition", hidden)!;
        var velocityField = typeof(UniverseView).GetField("_playerVelocity", hidden)!;
        positionField.SetValue(view, planet.Position + Vector2.Right * (planet.Radius + 120));
        velocityField.SetValue(view, Vector2.Right * 300);
        Check(!view.TryLand(planet), "High-speed approaches are rejected for landing");
        velocityField.SetValue(view, Vector2.Zero);
        Check(view.TryLand(planet), "Ships land on planet surfaces");
        Check(view.IsDocked && view.LandedBody == planet, "Landing locks flight controls like docking");
        typeof(UniverseView).GetMethod("UpdateWorldPhysics", hidden)!.Invoke(view, new object[] { 1f / 60f });
        var landedPos = (Vector2)positionField.GetValue(view)!;
        Check(Mathf.Abs(planet.SurfaceDistance(landedPos) - (ship.BoundingRadius() * 0.6f + 8)) < 0.5f,
            "Landed ships rest exactly on the recorded surface point");

        var free = default(UniverseGenerator.CelestialBody);
        foreach (var cell in new[] { new Vector2I(2, -1), new Vector2I(-3, 2), new Vector2I(1, 3), new Vector2I(-2, -2) })
        {
            free = UniverseGenerator.GenerateSystem(cell).Bodies
                .FirstOrDefault(b => b.Kind == UniverseGenerator.CelestialKind.Planet && b.FactionId == 0);
            if (free is not null) break;
        }
        Check(free is not null, "Frontier systems expose unowned planets to claim");
        Check(view.TakeOff(), "Ships take off from the surface");
        Check(!view.IsDocked, "Takeoff restores flight controls");

        positionField.SetValue(view, free!.Position + Vector2.Right * (free.Radius + 120));
        view.Progress.Resources = 5000;
        Check(view.TryLand(free), "Captains land on unowned planets");
        Check(view.ClaimPlanet(), "Captains claim unowned planets from the surface");
        Check(view.Progress.ClaimedPlanets.Contains(free.Id), "Claimed planets are recorded in campaign progress");
        Check(!view.ClaimPlanet(), "Claiming an already-owned planet fails");
        Check(view.BuildFactory(), "Factories build on claimed planets");
        Check(view.Progress.FactoryCountOf(free.Id) == 1, "Factory counts persist per planet");
        Check(view.TakeOff(), "Ships take off after surface operations");
        view.QueueFree();
    }
}
