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
            Catalog(); Technology(); DiplomacyAndWeapons(); DamageAndResources(); Campaign();
            GD.Print($"FACTION_COMBAT_CHECKS_PASS: {_checks} assertions; catalog={ShipShapeLibrary.Shapes.Count}, registrations={ShipShapeLibrary.RegisteredCount}");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
    private void Catalog()
    {
        Check(ShipShapeLibrary.ReferencedCount >= 483, "Every source ID is represented in the catalog");
        Check(ShipShapeLibrary.BuildOrder.Distinct().Count() == ShipShapeLibrary.Shapes.Count, "Every module has one accessible palette entry");
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
    private void Technology()
    {
        var progress = new PlayerProgress { Resources = 0 };
        Check(!progress.CanBuild("shield") && !progress.TryUnlock("shield") && progress.Resources == 0, "Insufficient resources cannot unlock or debit");
        var cost = PlayerProgress.UnlockCost(ShipShapeLibrary.Get("shield"));
        progress.Collect(cost);
        Check(progress.TryUnlock("shield") && progress.CanBuild("shield") && progress.Resources == 0, "Unlock spends exact resources");
        Check(progress.TryUnlock("shield") && progress.Resources == 0, "Repeated unlock is free and idempotent");
        var builder = new ShipBuilder { Progress = progress }; AddChild(builder);
        builder.SelectShape("beam");
        Check(!builder.UnlockSelected(), "Editor cannot bypass purchase gate");
        progress.Collect(PlayerProgress.UnlockCost(ShipShapeLibrary.Get("beam")));
        Check(builder.UnlockSelected(), "Editor purchases selected module");
        builder._Input(new InputEventKey { Keycode = Key.Z, CtrlPressed = true, Pressed = true });
        Check(progress.CanBuild("beam") && progress.Resources == 0, "Blueprint undo cannot refund permanent technology");
        var path = "res://.godot/faction-progress-test.json";
        progress.Diplomacy.Set(1, 8, DiplomaticState.War); progress.MinedDeposits.Add("test-ore"); progress.Save(path);
        var loaded = PlayerProgress.Load(path);
        Check(loaded.CanBuild("beam") && loaded.Resources == 0 && loaded.Diplomacy.AtWar(8, 1) && loaded.MinedDeposits.Contains("test-ore"), "Currency, technology, diplomacy and mining survive save reload");
        builder.QueueFree();
    }
    private void DiplomacyAndWeapons()
    {
        var relations = new FactionRelations();
        var a = Vessel(1, Vector2.Zero); var b = Vessel(8, new Vector2(400, 0));
        var actors = new[] { a, b }; var combat = new CombatWorld();
        for (var i = 0; i < 120; i++) combat.Step(1f / 60, actors, relations);
        Check(combat.Projectiles.Count == 0 && a.CombatTarget is null && b.CombatTarget is null, "Different factions stay peaceful without war");
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
        a.Refresh(); a.Energy = 0; a.Generation = 0; a.Cooldowns.Clear();
        var empty = new CombatWorld(); relations.Set(1, 3, DiplomaticState.War);
        empty.Step(1f / 60, new[] { a, c }, relations);
        Check(!empty.Projectiles.Any(p => p.Owner == a), "No energy means no shot");

        var shielded = Vessel(8, new Vector2(300, 0));
        Attach(shielded.Ship, shielded.Ship.Core!, 0, "shield", 4); shielded.Refresh();
        var hull = shielded.Ship.Blocks.Sum(x => x.Health);
        var shield = shielded.Shield;
        empty.ApplyDamage(shielded, null, 20, Vector2.Right);
        Check(shielded.Shield == shield - 20 && shielded.Ship.Blocks.Sum(x => x.Health) == hull, "Shield intercepts damage before blocks");
        new CombatWorld().Step(0.1f, new[] { shielded }, relations);
        Check(shielded.Shield > shield - 20, "Connected shields regenerate using energy");
        var beamShip = Vessel(1, Vector2.Zero);
        var cannon = beamShip.Ship.Blocks.First(x => x.ShapeId == "cannon");
        var pos = cannon.Position; var angle = cannon.Angle; beamShip.Ship.RemoveBlock(cannon); beamShip.Ship.AddBlock("beam", pos, angle);
        relations.Set(1, 3, DiplomaticState.War);
        var lasers = new CombatWorld(); lasers.Step(0.016f, new[] { beamShip, c }, relations);
        Check(lasers.Beams.Count > 0, "Beam weapons use immediate ray hits");
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
    }
    private void Campaign()
    {
        var view = new UniverseView(); var ship = new ModularShip(); ship.SeedReferenceShip(); view.SetShip(ship); AddChild(view);
        view.SetPhysicsProcess(false);
        Check(view.Traffic.Vessels.All(v => !v.PlayerOwned && !v.Escort), "Player starts alone; old teammates belong to independent factions");
        Check(!view.Progress.Established && !view.ManufactureFleetShip(), "Independent captain cannot spawn a free faction fleet");
        Check(!view.Progress.Diplomacy.AtWar(1, 8), "Initial station access is peaceful");
        Check(view.AidFaction(8) && view.Progress.Diplomacy.Friendly(1, 8), "Paid aid changes diplomatic relations");
        view.ChangeRelation(8, DiplomaticState.War);
        Check(!view.AidFaction(8), "Aid does not silently bypass an active war");
        Check(!view.ToggleDock(), "Warring station rejects docking");
        view.ChangeRelation(8, DiplomaticState.Neutral);
        view._Input(new InputEventKey { Keycode = Key.Tab, Pressed = true });
        Check(view.TableOpen, "Tab enters command mode");
        var own = view.Traffic.AddOwned(ship.Clone(), view.PlayerPosition + Vector2.Right * 400, "Owned");
        Check(own.PlayerOwned && view.Traffic.Vessels.Where(v => v != own).All(v => !v.PlayerOwned), "Friendliness never grants fleet ownership");
        view.Traffic.Load(Vector2I.Right, new Vector2(144000, 0));
        Check(view.Traffic.Vessels.Count(v => v.PlayerOwned) == 1 && view.Traffic.Vessels.Contains(own), "Owned fleet travels with the captain");
        view.Traffic.Load(Vector2I.Zero, view.PlayerPosition);
        Check(view.Traffic.Vessels.Count(v => v.PlayerOwned) == 1, "Revisiting cells never duplicates owned ships");
        view.QueueFree();
    }
}
