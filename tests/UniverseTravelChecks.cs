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
            Check(!drive.Advance(WarpDrive.ChargeSeconds+1) && drive.State == WarpDrive.Phase.Transit, "Charge transitions to physical flight");
            Check(drive.TravelledDistance > 0 && drive.RemainingDistance > 0 && drive.Speed > 0, "Flight advances distance at nonzero speed");
            var abortPosition = drive.Position;
            Check(drive.Cancel() && drive.State == WarpDrive.Phase.Braking, "Can disengage during flight");
            Check(!drive.Advance(10) && !drive.LocksFlight && drive.Aborted, "Emergency braking returns control without arrival");
            Check(drive.Position.DistanceTo(abortPosition) > 0 && drive.RemainingDistance > 0, "Disengaging continues braking along the current route");
            drive.Engage(Vector2I.Zero, Vector2I.Right, true, false, true);
            Check(drive.Advance(60) && drive.State == WarpDrive.Phase.Ready, "Large time step completes physical flight once");
            Check(drive.Speed == 0 && drive.RemainingDistance == 0, "Arrival completes the traversed distance at zero speed");
            Check(!drive.Advance(20), "Arrival does not fire twice");

            var ship = new ModularShip(); ship.SeedReferenceShip();
            var view = new UniverseView(); view.SetShip(ship); AddChild(view);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            // Every adjacent destination must work from the initial spawn and
            // station approaches, including negative world coordinates.
            foreach (var source in new[] { Vector2I.Zero, new Vector2I(-2,-1), new Vector2I(1,2) })
            {
                var originSystem = UniverseGenerator.GenerateSystem(source);
                var originStation = originSystem.Bodies.First(b => b.Kind == UniverseGenerator.CelestialKind.Station);
                var start = source == Vector2I.Zero ? view.PlayerPosition
                    : UniverseView.DockingPosition(originStation, ship.BoundingRadius()) + Vector2.Right*500;
                for (var x = -1; x <= 1; x++)
                for (var y = -1; y <= 1; y++)
                {
                    if (x == 0 && y == 0) continue;
                    var target = source + new Vector2I(x,y);
                    var station = UniverseGenerator.GenerateSystem(target).Bodies.First(b => b.Kind == UniverseGenerator.CelestialKind.Station);
                    var finish = UniverseView.DockingPosition(station, ship.BoundingRadius()) + Vector2.Right*500;
                    var route = WarpRoutePlanner.Plan(start, finish, ship.BoundingRadius());
                    Check(route is not null, $"Safe route exists from {source} to {target}");
                    var solids = WarpRoutePlanner.Obstacles(start, finish, ship.BoundingRadius());
                    Check(route!.Zip(route!.Skip(1)).All(pair => WarpRoutePlanner.ClearSegment(pair.First, pair.Second, solids)),
                        $"All route segments clear solids from {source} to {target}");
                    var flight = new WarpDrive();
                    flight.Engage(source, target, true, false, true, route);
                    var sweptClear = true;
                    while (flight.LocksFlight)
                    {
                        var before = flight.Position;
                        flight.Advance(1f/60);
                        sweptClear &= WarpRoutePlanner.ClearSegment(before, flight.Position, solids);
                    }
                    Check(sweptClear, $"Actual physics steps clear corners from {source} to {target}");
                }
            }
            var coarse = new WarpDrive(); var fine = new WarpDrive();
            coarse.Engage(Vector2I.Zero, Vector2I.Right, true, false, true);
            fine.Engage(Vector2I.Zero, Vector2I.Right, true, false, true);
            for (var i = 0; i < 150; i++) coarse.Advance(1f/30);
            for (var i = 0; i < 600; i++) fine.Advance(1f/120);
            Check(coarse.Position.DistanceTo(fine.Position) < 3 && Math.Abs(coarse.Speed-fine.Speed) < 1,
                "Travel distance and acceleration are independent of frame rate");
            var homeStations = UniverseGenerator.GenerateSystem(Vector2I.Zero)
                .Bodies.Count(b => b.Kind == UniverseGenerator.CelestialKind.Station);
            Check(view.Traffic.Stations.Count == homeStations && view.Traffic.Vessels.Count == 4 + homeStations * 2,
                "Stations and independent faction traffic match the generated system");
            Check(view.Traffic.Vessels.All(v => !v.PlayerOwned && !v.Escort), "Player starts alone without faction NPC escorts");
            foreach (var station in view.Traffic.Stations.Values)
            {
                Check(station.GetConnectedComponents().Count == 1 && station.Core is not null, "Station is one actual modular cluster");
                Check(station.Blocks.All(b => station.CanPlace(b.ShapeId, b.Position, b.Angle, b)), "Station modules share edges without overlapping");
            }
            Check(UniverseGenerator.GenerateEnemyFactions, "Enemy faction traffic is enabled");
            Check(view.Traffic.StationActors.Values.All(station => station.CollisionStatic &&
                station.Ship.Blocks.Any(block => block.Spec.IsWeapon)),
                "Generated stations are static collision bodies with offensive modules");
            foreach (var vessel in view.Traffic.Vessels)
                Check(vessel.Ship.GetConnectedComponents().Count == 1 && vessel.Ship.Blocks.All(b => vessel.Ship.CanPlace(b.ShapeId, b.Position, b.Angle, b)),
                    "Friendly ship modules are connected and do not overlap");
            Check(view.Traffic.Vessels.Where(v => v.Name == "联航运输舰").All(v => v.Ship.Blocks.Any(b => b.Spec.NumId == 201) && !v.Ship.Blocks.Any(b => b.Spec.IsWeapon)),
                "Transports carry actual cargo modules instead of cannons");
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
            var engine = ship.Blocks.First(b => b.ShapeId == "hyperdrive");
            engine.Enabled = false;
            Check(!view.BeginWarp(Vector2I.Right), "Disabled warp engine cannot jump");
            engine.Enabled = true;
            var mounting = engine.Position;
            engine.Position += Vector2.One * 400;
            Check(!view.BeginWarp(Vector2I.Right), "Disconnected warp engine cannot jump");
            engine.Position = mounting;
            Check(view.BeginWarp(Vector2I.Right), "Undocked ship engages warp");
            view.SetPhysicsProcess(false);
            var departure = view.PlayerPosition;
            var obstacles = WarpRoutePlanner.Obstacles(departure, view.Warp.EndPosition, ship.BoundingRadius());
            var largestStep = 0f;
            var movingTicks = 0;
            var peakSpeed = 0f;
            var sawInterstellarSpace = false;
            for (var i = 0; i < 4000 && view.Warp.LocksFlight; i++)
            {
                var previous = view.PlayerPosition;
                view._PhysicsProcess(1.0/60);
                var step = view.PlayerPosition.DistanceTo(previous);
                largestStep = Math.Max(largestStep, step);
                if (step > 0) movingTicks++;
                peakSpeed = Math.Max(peakSpeed, view.PlayerVelocity.Length());
                var cell = UniverseGenerator.WorldToSystemCell(view.PlayerPosition);
                sawInterstellarSpace |= view.PlayerPosition.DistanceTo(UniverseGenerator.SystemCenter(cell)) > UniverseGenerator.SystemRadius;
                Check(WarpRoutePlanner.ClearSegment(previous, view.PlayerPosition, obstacles), "Swept warp movement stays outside solid bodies");
            }
            Check(!view.Warp.LocksFlight && movingTicks > 200, "Flight traverses hundreds of actual physics ticks");
            Check(largestStep <= WarpDrive.CruiseSpeed/60+1, "No frame teleports beyond the speed budget");
            Check(peakSpeed > 10000 && sawInterstellarSpace, "Ship actually accelerates through interstellar empty space");
            var destination = UniverseGenerator.GenerateSystem(Vector2I.Right);
            Check(UniverseGenerator.WorldToSystemCell(view.PlayerPosition) == Vector2I.Right, "Arrives in selected system");
            Check(view.PlayerVelocity.IsZeroApprox() && view.PlayerAngularVelocity == 0, "Arrival clears momentum");
            view.UpdateRenderTransform(0.5f);
            Check(view.RenderPosition.DistanceTo(view.PlayerPosition) < 2, "Smooth arrival preserves interpolation history");
            Check(destination.Bodies.All(b => !b.HasSurface || b.SurfaceDistance(view.PlayerPosition) > ship.BoundingRadius() + 180), "Arrival clears every solid body");
            var expectedVessels = 4 + destination.Bodies.Count(b => b.Kind == UniverseGenerator.CelestialKind.Station) * 2
                + destination.Bodies.Count(b => b.Kind == UniverseGenerator.CelestialKind.PirateZone) * 4;
            Check(view.Traffic.Cell == Vector2I.Right && view.Traffic.Vessels.Count == expectedVessels,
                "Traffic reloads without accumulating fleets");
            var arrival = view.PlayerPosition;
            view._PhysicsProcess(2);
            Check(view.Warp.State == WarpDrive.Phase.Ready, "Cooldown ends in flight");
            Check(view.PlayerPosition.DistanceTo(arrival) < 20, "No second teleport");
            Check(view.BeginWarp(Vector2I.Zero), "Return route starts from the actual arrival position");
            view._PhysicsProcess(WarpDrive.ChargeSeconds);
            for (var i = 0; i < 180; i++) view._PhysicsProcess(1.0/60);
            var cancelledAt = view.PlayerPosition;
            Check(view.Warp.Cancel(), "Midflight disengage accepted by world flight");
            for (var i = 0; i < 120 && view.Warp.LocksFlight; i++) view._PhysicsProcess(1.0/60);
            Check(!view.Warp.LocksFlight && view.PlayerVelocity.IsZeroApprox(), "Disengage brakes to rest before returning normal control");
            Check(view.PlayerPosition.DistanceTo(cancelledAt) > 1000 && view.PlayerPosition.DistanceTo(view.Warp.EndPosition) > 10000,
                "Disengage stops along the route rather than jumping to the destination");
            for (var i = 0; i < 8; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var draws = view.RetainedDrawCount;
            for (var i = 0; i < 8; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(draws > 0 && view.RetainedDrawCount == draws, "Static celestial commands remain cached over moving render frames");
            Commands();
            GD.Print($"UNIVERSE_TRAVEL_CHECKS_PASS: {_checks} assertions");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }

    private void Commands()
    {
        var bodies = Array.Empty<UniverseGenerator.CelestialBody>();
        var traffic = new UniverseTraffic();
        var ship = new ModularShip();
        ship.SeedReferenceShip();
        var vessel = traffic.AddOwned(ship, new Vector2(5000, 5000), "测试舰");
        // 驻守：Hold 优先于护航锚点，舰船钉在原地。
        vessel.Hold = true; vessel.Escort = false;
        var post = vessel.Position;
        for (var i = 0; i < 90; i++) traffic.Step(1f / 60, post + Vector2.Right * 5000, 0, bodies);
        Check(vessel.Position.DistanceTo(post) < 5, "Hold commands anchor the vessel at its post");
        Check(vessel.CommandLabel == "驻守", "Hold state reports the garrison label");
        // 移动指令：沿指令方向推进。
        vessel.Hold = false;
        vessel.CommandDestination = post + Vector2.Right * 3000;
        var peakSpeed = 0f;
        for (var i = 0; i < 240; i++)
        { traffic.Step(1f / 60, post, 0, bodies); peakSpeed = Mathf.Max(peakSpeed, vessel.Velocity.Length()); }
        Check(vessel.Position.DistanceTo(post) > 300 && (vessel.Position - post).Dot(Vector2.Right) > 0,
            "Move commands drive the vessel along the ordered destination");
        Check(peakSpeed > 500, "Commanded cruise reaches high speed instead of crawling");
        // 到达即停：继续推进后舰船应在目的地附近刹停并转驻守，而不是全速绕圈。
        var destination = post + Vector2.Right * 3000;
        // 高速巡航会有一次过冲再回稳，给足 60 秒收敛窗口。
        for (var i = 0; i < 3600; i++) traffic.Step(1f / 60, post, 0, bodies);
        Check(vessel.CommandDestination is null && vessel.Hold, "Vessels switch to hold after reaching the ordered destination");
        Check(vessel.Position.DistanceTo(destination) < 150 && vessel.Velocity.Length() < 30,
            "Vessels brake to a stop near the ordered destination");
        // 集火优先级：攻击指令压过移动指令。
        var enemyShip = new ModularShip();
        enemyShip.SeedReferenceShip();
        var enemy = new UniverseTraffic.Vessel { Name = "敌舰", Ship = enemyShip,
            Anchor = post + Vector2.Right * 1200, Position = post + Vector2.Right * 1200,
            PreviousPosition = post + Vector2.Right * 1200, FactionId = FactionRelations.Pirates,
            Radius = enemyShip.BoundingRadius() };
        vessel.CommandDestination = post + Vector2.Left * 3000;
        vessel.CombatTarget = enemy;
        for (var i = 0; i < 240; i++)
        { enemy.Position = enemy.PreviousPosition = post + Vector2.Right * 1200; traffic.Step(1f / 60, post, 0, bodies); }
        Check(vessel.CommandLabel == "攻击", "Attack state reports the engage label");
        Check((vessel.Position - post).Dot(Vector2.Right) > 0, "Attack command overrides the ordered move destination");
        vessel.CombatTarget = null; vessel.CommandDestination = null; vessel.Escort = true; vessel.Hold = false;
        Check(vessel.CommandLabel == "护航", "Escort state reports the follow label");
        // 旗舰指挥：自动航行直接构造 Command（与驾驶模式解耦）——朝目标旋转推进、到达转驻守、驻守主动制动。
        var flagship = new UniverseView(); flagship.SetShip(ship); AddChild(flagship);
        Set(flagship, "_playerPosition", Vector2.Zero);
        Set(flagship, "_playerVelocity", Vector2.Zero);
        Set(flagship, "_playerHeading", -Mathf.Pi / 2); // 舰首朝上而目标在右：必须产生向右的旋转误差
        Set(flagship, "_playerCommand", Vector2.Right * 2000);
        var autopilot = typeof(UniverseView).GetMethod("AutopilotCommand", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var commandField = typeof(UniverseView).GetField("_playerCommand", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var holdField = typeof(UniverseView).GetField("_playerHold", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var move = (ShipFlightController.Command?)autopilot.Invoke(flagship, null);
        Check(move is { } mc && mc.HeadingError > 0.5f && mc.LocalTranslation.Length() > 0,
            "Flagship autopilot rotates and thrusts toward the commanded destination");
        Set(flagship, "_playerPosition", Vector2.Right * 1950);
        autopilot.Invoke(flagship, null);
        Check(commandField.GetValue(flagship) is null && (bool)holdField.GetValue(flagship)!,
            "Flagship clears the move command and switches to hold on arrival");
        Set(flagship, "_playerHeading", 0f);
        Set(flagship, "_playerVelocity", Vector2.Right * 300);
        var brake = (ShipFlightController.Command?)autopilot.Invoke(flagship, null);
        Check(brake is { } bc && bc.LocalTranslation.X < 0, "Flagship hold command brakes against current velocity");
    }
}
