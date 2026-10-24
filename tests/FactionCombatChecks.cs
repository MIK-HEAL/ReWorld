using Godot;
using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Reflection;
using FileAccess = Godot.FileAccess;

public partial class FactionCombatChecks : Node
{
    private int _checks;
    private void Check(bool condition, string message)
    { _checks++; if (!condition) throw new InvalidOperationException(message); }
    private static CombatVessel Vessel(int faction, Vector2 position)
    {
        var ship = new ModularShip(); ship.SeedReferenceShip();
        return new CombatVessel { Name = "Test", Ship = ship, FactionId = faction, Position = position, PreviousPosition = position };
    }
    private static ModularShip.Block Attach(ModularShip ship, ModularShip.Block host, int port, string id, int self = 2)
    {
        var pose = ModularShip.SnapBlock(ShipShapeLibrary.Get(id).Ports[self], host.Spec.Ports[port], host.Position, host.Angle);
        return ship.AddBlock(id, pose.Position, pose.Angle);
    }
    public override void _Ready()
    {
        try
        {
            Catalog(); GroundTruthCatalog(); Technology(); DiplomacyAndWeapons(); DamageAndResources(); Campaign(); DeathRespawnPhysics(); RepairAndRegrow();
            GD.Print($"FACTION_COMBAT_CHECKS_PASS: {_checks} assertions; catalog={ShipShapeLibrary.Shapes.Count}, registrations={ShipShapeLibrary.RegisteredCount}");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
    private void Catalog()
    {
        Check(ShipShapeLibrary.ReferencedCount >= 483, "Every source ID is represented in the catalog");
        Check(ShipShapeLibrary.BuildOrder.Distinct().Count() == ShipShapeLibrary.BuildOrder.Length
            && ShipShapeLibrary.BuildOrder.All(id => ShipShapeLibrary.Get(id).IsRegistered),
            "The build catalog contains only registered modules with complete source records");
        Check(ShipShapeLibrary.BuildOrder.Select(id => ShipShapeLibrary.Get(id).NumId).Distinct().Count() == ShipShapeLibrary.BuildOrder.Length,
            "Registered source modules appear only once in the build catalog");
        var known = ShipShapeLibrary.Shapes.Values.Select(s => s.NumId).ToHashSet();
        foreach (var folder in new[] { "ships", "extra_ships", "blockfonts", "backup_agents" })
        foreach (var path in System.IO.Directory.EnumerateFiles(ProjectSettings.GlobalizePath($"res://Reassembly/reference/{folder}"), "*.lua", System.IO.SearchOption.AllDirectories))
        {
            var text = System.IO.File.ReadAllText(path);
            var ids = Regex.Matches(text, @"\{\s*(\d+)\s*,\s*\{").Select(m => int.Parse(m.Groups[1].Value)).ToArray();
            Check(ids.All(known.Contains), $"No catalog holes in {path}");
            var design = ShipDesign.ParseLua(text);
            Check(design.Blocks.Count == ids.Length, $"Lua parser preserves omitted rotations in {path}");
        }
        var omitted = ShipDesign.ParseLua("{blocks={{800,{1,2}},{859,{3,4},1.57}}}");
        Check(omitted.Blocks.Count == 2 && omitted.Blocks[0].Angle == 0, "Missing angle defaults to zero");
        Check(omitted.ToShip().Blocks.Count == 2, "Imported blocks are retained in runtime ships");
        Check(ShipShapeLibrary.Shapes.Values.All(s => s.Vertices().Length >= 3 && s.Ports.Count > 0 && s.Hull > 0 && s.Mass > 0), "Catalog geometry is usable for editing and damage");
    }

    private void GroundTruthCatalog()
    {
        var exact = ShipShapeLibrary.Shapes.Values.Where(s => s.NumId is >= 47 and <= 69).ToArray();
        Check(exact.Length == 23, "The exported player-faction block set is fully migrated");
        Check(ShipShapeLibrary.BuildOrder.Count(id => ShipShapeLibrary.Get(id).HasGroundTruth) == 23 &&
            ShipShapeLibrary.BuildOrder.All(id => ShipShapeLibrary.Get(id).HasGroundTruth || id == "hyperdrive"),
            "Only exported native modules and the project-specific travel drive are offered to players");
        var boost = ShipShapeLibrary.Get("ra_50");
        Check(boost.IsRegistered, "Exported player modules are eligible for the original upgrade catalog");
        Check(boost.NativeShapeName == "THRUSTER_NOSTACK" && boost.Scale == 2 && boost.Thrust == 500000,
            "Booster keeps native shape, scale and force");
        Check(Mathf.Abs(boost.ThrusterColor.A - 0x90 / 255.0f) < 0.001f && boost.FillColor1Uint == 0x3D4F80,
            "Booster keeps alpha-bearing exhaust and iridescent fill colors");
        var shield = ShipShapeLibrary.Get("ra_51");
        Check(shield.Shield is not null && shield.Shield.Strength == 1000 && shield.Shield.Regen == 200,
            "Shield substructure is available to combat");
        var missile = ShipShapeLibrary.Get("ra_59");
        Check(missile.NativeShapeName == "RECT_LAUNCHER" && missile.ReplicateBlock is not null &&
            missile.ReplicateBlock.ShapeName == "MISSILE" && missile.ReplicateBlock.ExplodeDamage == 30,
            "Launcher keeps its own native shape separately from its nested missile payload");
        var antimatter = ShipShapeLibrary.Get("ra_66");
        Check(antimatter.Cannon is not null && antimatter.Cannon.Damage == 500 && antimatter.Shrouds.Count == 2,
            "Cannon stats and barrel shrouds are available to rendering");
        Check(antimatter.Shrouds[1].Offset.X == 6 && antimatter.Shrouds[0].Size == new Vector2(10, 5) &&
            ShipShapeLibrary.Get("ra_68").BarrelOffset == new Vector2(-1, 0),
            "Weapon accessories retain positions, dimensions and barrel offsets from the mod export");
        Check(!ShipShapeLibrary.BuildOrder.Contains("ra_228") && !progressCanBuildLegacy(),
            "Undecoded catalog approximations cannot leak back into the player upgrade catalog");
        var triangle = ShipShapeLibrary.Get("ra_58");
        var centroid = triangle.Vertices().Aggregate(Vector2.Zero, (sum, vertex) => sum + vertex) / triangle.Vertices().Length;
        Check(centroid.Length() < 0.01f && triangle.NativeShapeName == "RIGHT_TRI",
            "Legacy polygon geometry is centered on its native rotation origin");

        static bool progressCanBuildLegacy() => new PlayerProgress().CanBuild("ra_228");
    }
    private void Technology()
    {
        var progress = new PlayerProgress { Resources = 0 };
        Check(!progress.CanBuild("ra_51") && !progress.TryUnlock("ra_51") && progress.Resources == 0, "Insufficient resources cannot unlock or debit");
        var cost = PlayerProgress.UnlockCost(ShipShapeLibrary.Get("ra_51"));
        progress.Collect(cost);
        Check(progress.TryUnlock("ra_51") && progress.CanBuild("ra_51") && progress.Resources == 0, "Unlock spends exact resources");
        Check(progress.TryUnlock("ra_51") && progress.Resources == 0, "Repeated unlock is free and idempotent");
        var upgrades = new UpgradeScreen { Progress = progress }; AddChild(upgrades);
        Check(!upgrades.TryPurchase("ra_57"), "Upgrade screen cannot decrypt without sufficient resources");
        progress.Collect(PlayerProgress.UnlockCost(ShipShapeLibrary.Get("ra_57")));
        Check(upgrades.TryPurchase("ra_57"), "Upgrade screen permanently decrypts purchased modules");
        var builder = new ShipBuilder { Progress = progress }; AddChild(builder);
        builder.SelectShape("ra_57");
        builder._Input(new InputEventKey { Keycode = Key.Z, CtrlPressed = true, Pressed = true });
        Check(progress.CanBuild("ra_57") && progress.Resources == 0, "Blueprint undo cannot refund permanent technology");
        var path = "res://.godot/faction-progress-test.json";
        progress.Diplomacy.Set(1, 8, DiplomaticState.War); progress.MinedDeposits.Add("test-ore"); progress.Save(path);
        var loaded = PlayerProgress.Load(path);
        Check(loaded.CanBuild("ra_57") && loaded.Resources == 0 && loaded.Diplomacy.AtWar(8, 1) && loaded.MinedDeposits.Contains("test-ore"), "Currency, technology, diplomacy and mining survive save reload");
        builder.QueueFree(); upgrades.QueueFree();
    }
    private void DiplomacyAndWeapons()
    {
        var relations = new FactionRelations();
        var a = Vessel(1, Vector2.Zero); var b = Vessel(8, new Vector2(400, 0));
        var actors = new[] { a, b }; var combat = new CombatWorld();
        for (var i = 0; i < 120; i++) combat.Step(1f / 60, actors, relations);
        Check(combat.Projectiles.Count == 0 && a.CombatTarget is null && b.CombatTarget is null, "Different factions stay peaceful without war");
        Check(!relations.Hostile(1, 8) && !relations.Hostile(1, 3), "All factions begin neutral to the independent player");
        relations.Set(1, 8, DiplomaticState.Friendly);
        Check(combat.NearestHostile(a, actors, relations) is null, "Friendly identity does not become an attack target");
        relations.Set(1, 8, DiplomaticState.War);
        Check(relations.AtWar(8, 1) && !relations.AtWar(1, 1), "War is symmetric and own faction is protected");
        combat.Step(1f / 60, actors, relations);
        Check(a.CombatTarget == b && b.CombatTarget == a && combat.Projectiles.Count > 0, "Both parties acquire hostiles and fire only after war");
        var before = b.Ship.Blocks.Sum(x => x.Health);
        for (var i = 0; i < 45; i++) combat.Step(1f / 60, actors, relations);
        Check(b.Ship.Blocks.Sum(x => x.Health) < before, "Swept projectiles damage actual target modules");
        relations.Set(1, 8, DiplomaticState.Neutral);
        var saved = b.Ship.Blocks.Sum(x => x.Health);
        var stopped = a.Energy;
        for (var i = 0; i < 120; i++) combat.Step(1f / 60, actors, relations);
        Check(b.Ship.Blocks.Sum(x => x.Health) == saved && a.CombatTarget is null && combat.Projectiles.Count == 0, "Ceasefire stops targeting and prevents in-flight friendly damage");
        var neutralHit = CombatWorld.FindHit(new Vector2(-1000, 0), new Vector2(1000, 0), a, actors, relations);
        Check(neutralHit.Actor is null, "Manual shots cannot harm a non-warring faction");
        var c = Vessel(3, new Vector2(300, 0));
        relations.Set(8, 3, DiplomaticState.War);
        Check(combat.NearestHostile(b, new[] { a, b, c }, relations) == c, "NPC factions use their own pairwise relations");
        relations.Set(1, 8, DiplomaticState.Wanted);
        Check(relations.Hostile(1, 8) && combat.NearestHostile(a, actors, relations) == b,
            "Wanted status enables hostile targeting");
        relations.Set(1, 8, DiplomaticState.Neutral);
        a.Refresh(); a.Energy = 0; a.Generation = 0; a.Cooldowns.Clear();
        var empty = new CombatWorld(); relations.Set(1, 3, DiplomaticState.War);
        empty.Step(1f / 60, new[] { a, c }, relations);
        Check(!empty.Projectiles.Any(p => p.Owner == a), "No energy means no shot");

        var shielded = Vessel(8, new Vector2(300, 0));
        Attach(shielded.Ship, shielded.Ship.Core!, 0, "ra_51", 0); shielded.Refresh();
        var hull = shielded.Ship.Blocks.Sum(x => x.Health);
        var shield = shielded.Shield;
        empty.ApplyDamage(shielded, null, 20, Vector2.Right);
        Check(shielded.Shield == shield - 20 && shielded.Ship.Blocks.Sum(x => x.Health) == hull, "Shield intercepts damage before blocks");
        new CombatWorld().Step(0.1f, new[] { shielded }, relations);
        Check(shielded.Shield > shield - 20, "Connected shields regenerate using energy");
        var beamShip = Vessel(1, Vector2.Zero);
        var cannon = beamShip.Ship.Blocks.First(x => x.Spec.NumId == 56);
        var pos = cannon.Position; var angle = cannon.Angle; beamShip.Ship.RemoveBlock(cannon); beamShip.Ship.AddBlock("beam", pos, angle);
        relations.Set(1, 3, DiplomaticState.War);
        var lasers = new CombatWorld(); lasers.Step(0.016f, new[] { beamShip, c }, relations);
        Check(lasers.Beams.Count > 0, "Beam weapons use immediate ray hits");

        var stationShip = UniverseTraffic.BuildStation();
        var station = new CombatVessel { Name = "Station", Ship = stationShip, FactionId = FactionRelations.League,
            Position = Vector2.Zero, PreviousPosition = Vector2.Zero, CollisionStatic = true };
        station.Refresh();
        Check(stationShip.GetConnectedComponents().Count == 1 && station.Active.Any(b => b.Spec.IsWeapon),
            "Stations are connected combat bodies with working weapons");
        Check(station.ShieldCapacity == 0, "Stations do not receive planet-like shield protection");
        var shipAtStation = Vessel(FactionRelations.Player, new Vector2(1, 0));
        shipAtStation.Velocity = Vector2.Left * 100;
        CombatWorld.ResolveCollisions(new[] { station, shipAtStation });
        Check(!CombatWorld.Collides(station, shipAtStation)
            && shipAtStation.Velocity.Dot((shipAtStation.Position - station.Position).Normalized()) >= 0,
            "Ships collide with station hulls and cannot pass through them");

        var hostile = Vessel(FactionRelations.Red, new Vector2(300, 0));
        var stationRelations = new FactionRelations();
        var stationCombat = new CombatWorld();
        stationCombat.Step(1f / 60, new[] { station, hostile }, stationRelations);
        Check(!stationCombat.Projectiles.Any(p => p.Owner == station), "Station weapons stay idle while neutral");
        stationRelations.Set(FactionRelations.League, FactionRelations.Red, DiplomaticState.War);
        stationCombat.Step(1f / 60, new[] { station, hostile }, stationRelations);
        Check(stationCombat.Projectiles.Any(p => p.Owner == station), "Station weapons engage factions at war");
    }
    private void DamageAndResources()
    {
        var ship = new ModularShip(); var core = ship.AddBlock("core", Vector2.Zero, 0);
        var bridge = Attach(ship, core, 5, "hull", 0);
        Attach(ship, bridge, 2, "thruster", 0);
        Check(ship.GetThrusterMounts().Count == 1, "Connected engine initially contributes thrust");
        var fragments = ship.DamageBlock(bridge, 10000);
        Check(fragments.Count == 1 && ship.GetThrusterMounts().Count == 0 && fragments[0].Blocks.Any(b => b.Spec.IsThruster), "Breaking a bridge creates physical detached engines and removes thrust");
        var rockShip = new ModularShip(); var rockBlock = rockShip.AddBlock("hull", Vector2.Zero, 0);
        var mineral = new CombatVessel { Name = "Ore", Ship = rockShip, Position = Vector2.Zero, Mineral = true, DepositId = "ore-1" };
        var combat = new CombatWorld();
        combat.ApplyDamage(mineral, rockBlock, 10000, Vector2.Right);
        Check(!mineral.Alive && combat.Resources.Count == 1, "Mining produces a resource pocket");
        Check(combat.Collect(new Vector2(1000, 0), 100) == 0, "Distant resources cannot be collected");
        Check(combat.Collect(Vector2.Zero, 100) == 90 && combat.Collect(Vector2.Zero, 100) == 0, "Resource pocket pays out once");

        var rammer = Vessel(FactionRelations.Player, new Vector2(0, 0)); rammer.Velocity = Vector2.Right * 400;
        var target = Vessel(FactionRelations.League, new Vector2(62, 0));
        var ramHull = rammer.Ship.Blocks.Sum(b => b.Health); var targetHull = target.Ship.Blocks.Sum(b => b.Health);
        var ramHealth = rammer.Ship.Blocks.ToDictionary(b => b, b => b.Health);
        var targetHealth = target.Ship.Blocks.ToDictionary(b => b, b => b.Health);
        var ramCombat = new CombatWorld();
        rammer.Position += rammer.Velocity * (1f / 60);
        // Capture each block's world X at the moment of impact; the contact face faces the other ship.
        var ramWorld = rammer.Ship.Blocks.ToDictionary(b => b,
            b => (rammer.Position + (b.Position - rammer.Ship.CenterOfMass())).X);
        var targetWorld = target.Ship.Blocks.ToDictionary(b => b,
            b => (target.Position + (b.Position - target.Ship.CenterOfMass())).X);
        ramCombat.Step(1f / 60, new[] { rammer, target }, new FactionRelations());
        Check(rammer.Ship.Blocks.Sum(b => b.Health) < ramHull && target.Ship.Blocks.Sum(b => b.Health) < targetHull,
            "High-speed ramming damages both hulls when relative speed exceeds the threshold");
        // The damaged block must be the SAT contact, not a guess by velocity direction.
        // Geometric proof: the hit block's world X lies in the hull overlap window
        // [target's leftmost face, rammer's rightmost face] — i.e. it was actually touching.
        var overlapMin = targetWorld.Values.Min();   // target's leftmost face (toward rammer)
        var overlapMax = ramWorld.Values.Max();       // rammer's rightmost face (toward target)
        var ramDestroyed = ramHealth.Keys.Except(rammer.Ship.Blocks).ToList();
        var targetDamaged = target.Ship.Blocks.Where(b => b.Health < targetHealth[b]).ToList();
        Check(ramDestroyed.Count == 1 && ramWorld[ramDestroyed[0]] >= overlapMin && ramWorld[ramDestroyed[0]] <= overlapMax,
            "Ramming destroys the block actually touching the target (in the hull overlap window), not a guess by velocity direction");
        Check(targetDamaged.Count == 1 && targetWorld[targetDamaged[0]] >= overlapMin && targetWorld[targetDamaged[0]] <= overlapMax,
            "Ramming damages the block actually touching the rammer (in the hull overlap window), not a guess by velocity direction");
        var slowTarget = Vessel(FactionRelations.League, new Vector2(10, 0));
        var slowHull = slowTarget.Ship.Blocks.Sum(b => b.Health);
        var gentle = Vessel(FactionRelations.Player, new Vector2(0, 0)); gentle.Velocity = Vector2.Right * 50;
        new CombatWorld().Step(1f / 60, new[] { gentle, slowTarget }, new FactionRelations());
        Check(slowTarget.Ship.Blocks.Sum(b => b.Health) == slowHull, "Below-threshold relative speed deals no collision damage");

        var surface = Vessel(FactionRelations.Player, Vector2.Zero);
        var surfaceHull = surface.Ship.Blocks.Sum(b => b.Health);
        new CombatWorld().SurfaceImpact(surface, 200, Vector2.Right);
        Check(surface.Ship.Blocks.Sum(b => b.Health) < surfaceHull, "Hitting a stellar surface always deals base damage");
    }
    private void Campaign()
    {
        var view = new UniverseView(); var ship = new ModularShip(); ship.SeedReferenceShip(); view.SetShip(ship); AddChild(view);
        view.SetPhysicsProcess(false);
        Check(view.Traffic.Vessels.All(v => !v.PlayerOwned && !v.Escort), "Player starts alone; old teammates belong to independent factions");
        Check(!view.Progress.Established && !view.ManufactureFleetShip(), "Independent captain cannot spawn a free faction fleet");
        Check(!view.Progress.Diplomacy.Hostile(1, 8) && !view.Progress.Diplomacy.Hostile(1, 3),
            "Captain starts independent and is neutral to every faction");
        Check(view.AidFaction(8) && view.Progress.Diplomacy.Friendly(1, 8), "Paid aid changes diplomatic relations");
        view.ChangeRelation(8, DiplomaticState.War);
        Check(!view.AidFaction(8), "Aid does not silently bypass an active war");
        Check(!view.ToggleDock(), "Warring station rejects docking");
        view.ChangeRelation(8, DiplomaticState.Neutral);

        ship.DamageBlock(ship.Core!, 10000);
        view.Progress.Resources = 50;
        var commits = 0;
        view.Progress.Changed += () => commits++;
        Check(!view.TryRespawn() && ship.Core is null && view.Progress.Resources == 50 && commits == 0,
            "Core loss cannot respawn without the fee");
        view.Progress.Resources = 100;
        foreach (var station in view.Traffic.StationActors.Values) station.Ship.DamageBlock(station.Ship.Core!, 10000);
        Check(!view.TryRespawn() && ship.Core is null && view.Progress.Resources == 100 && commits == 0,
            "No live non-hostile site means no rebuild and no debit");
        view.QueueFree();

        var respawnView = new UniverseView(); var respawnShip = new ModularShip(); respawnShip.SeedReferenceShip();
        respawnView.SetShip(respawnShip); AddChild(respawnView); respawnView.SetPhysicsProcess(false);
        respawnShip.DamageBlock(respawnShip.Core!, 10000);
        Check(respawnView.TryRespawn() && respawnShip.HasLiveCore && respawnView.Progress.Resources == 150
            && respawnView.PlayerVelocity.IsZeroApprox(),
            "Successful station respawn rebuilds the command core for 100 R and clears momentum");
        Check(respawnView.PlayerCombat is not null && respawnView.Traffic.StationActors.Values.All(station =>
                !station.Alive || !CombatWorld.Collides(respawnView.PlayerCombat, station)),
            "Respawn clearance accounts for the station hull");

        respawnView.Progress.Collect(1000);
        Check(respawnView.EstablishBase(), "Outpost can be established after respawn");
        respawnView.ChangeRelation(FactionRelations.League, DiplomaticState.War);
        respawnShip.DamageBlock(respawnShip.Core!, 10000);
        respawnView.Progress.Collect(UniverseView.RespawnCost);
        Check(respawnView.TryRespawn() && respawnShip.HasLiveCore && respawnView.Progress.Resources == 150,
            "Own outpost remains available when other stations are hostile");
        var baseField = typeof(UniverseView).GetField("_playerBase", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var outpost = (CombatVessel)baseField.GetValue(respawnView)!;
        Check(!CombatWorld.Collides(respawnView.PlayerCombat!, outpost)
            && respawnView.PlayerPosition.DistanceTo(new Vector2(respawnView.Progress.BaseX, respawnView.Progress.BaseY)) > outpost.Ship.BoundingRadius(),
            "Outpost respawn is outside the outpost hull");

        respawnView.ChangeRelation(FactionRelations.League, DiplomaticState.Neutral);
        respawnView._Input(new InputEventKey { Keycode = Key.Tab, Pressed = true });
        Check(respawnView.TableOpen, "Tab enters command mode");
        var own = respawnView.Traffic.AddOwned(respawnShip.Clone(), respawnView.PlayerPosition + Vector2.Right * 400, "Owned");
        Check(own.PlayerOwned && respawnView.Traffic.Vessels.Where(v => v != own).All(v => !v.PlayerOwned), "Friendliness never grants fleet ownership");
        respawnView.Traffic.Load(Vector2I.Right, new Vector2(144000, 0));
        Check(respawnView.Traffic.Vessels.Count(v => v.PlayerOwned) == 1 && respawnView.Traffic.Vessels.Contains(own), "Owned fleet travels with the captain");
        respawnView.Traffic.Load(Vector2I.Zero, respawnView.PlayerPosition);
        Check(respawnView.Traffic.Vessels.Count(v => v.PlayerOwned) == 1, "Revisiting cells never duplicates owned ships");
        respawnView.QueueFree();
    }
    private void DeathRespawnPhysics()
    {
        var view = new UniverseView(); var ship = new ModularShip(); ship.SeedReferenceShip();
        view.SetShip(ship); AddChild(view); view.SetPhysicsProcess(false);
        view.Progress.Resources = 0;
        view.Progress.Collect(UniverseView.RespawnCost);
        ship.DamageBlock(ship.Core!, 10000);
        Check(ship.Core is null && !ship.HasLiveCore, "Core is destroyed before respawn");
        // Physics tick after death must not crash (no NaN propagation, no missing actor refs).
        view._PhysicsProcess(1.0 / 60);
        Check(float.IsFinite(view.PlayerPosition.X) && float.IsFinite(view.PlayerPosition.Y),
            "Death does not destabilize physics: position stays finite");
        // Enter after death triggers respawn via the campaign input path.
        view._Input(new InputEventKey { Keycode = Key.Enter, Pressed = true });
        Check(ship.HasLiveCore, "Enter after core loss respawns the captain at a friendly station");
        Check(view.Progress.Resources == 0, "Respawn debits the 100 R fee through the input path");
        view.QueueFree();
    }
    private void RepairAndRegrow()
    {
        var ship = new ModularShip(); ship.SeedReferenceShip();
        var fullHealth = ship.Blocks.Sum(b => b.Health);
        var blockCount = ship.Blocks.Count;
        // Damage but do not destroy: TickRepair heals over time and costs resources.
        ship.Blocks[3].Health = 1;
        var damaged = ship.Blocks[3];
        var cost = ship.TickRepair(1f, 1000);
        Check(damaged.Health > 1 && cost > 0, "Damaged blocks heal toward full at a resource cost");
        // No resources: healing stops and no blocks regrow.
        ship.Blocks[3].Health = 1;
        var noCost = ship.TickRepair(1f, 0);
        Check(noCost == 0, "Without resources, repair halts");
        // Destroy a non-core block: it regrows from a live neighbor when resources allow.
        var target = ship.Blocks.First(b => !b.Spec.IsCore && b.Health > 0);
        var targetShape = target.ShapeId; var targetPos = target.Position; var targetAngle = target.Angle;
        ship.DamageBlock(target, 100000);
        Check(!ship.Blocks.Any(b => b.ShapeId == targetShape && b.Position == targetPos), "Block is destroyed and absent from Blocks");
        var regrowCost = ship.TickRepair(1f, 10000);
        Check(regrowCost > 0 && ship.Blocks.Any(b => b.ShapeId == targetShape && b.Position == targetPos && b.Health > 0),
            "Destroyed blocks regrow from a live neighbor when resources are available");
        // A block with no live neighbor cannot regrow (isolated blueprint slot).
        foreach (var b in ship.Blocks.ToList()) ship.DamageBlock(b, 100000);
        Check(ship.Blocks.Count == 0, "All blocks destroyed for isolation test");
        var isoCost = ship.TickRepair(1f, 10000);
        Check(isoCost == 0, "Isolated blueprint slots never regrow without a live neighbor");
    }
}
