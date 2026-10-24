using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;

public partial class ShipFlightChecks : Node
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
            Coordinates();
            Integration();
            Actuators();
            Tracking();
            RapidCursorTurn();
            ViewIntegration();
            CacheAndPerformance();
            GD.Print($"SHIP_FLIGHT_CHECKS_PASS: {_checks} assertions");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
    }

    private void Coordinates()
    {
        var pilot = new ShipFlightController();
        var keys = new HashSet<Key> { Key.W, Key.Up, Key.A, Key.Q };
        var axes = ShipFlightController.ReadControls(keys.Contains);
        Check(axes == new ShipFlightController.Controls(1, -1, -1), "Native W/A/Q input; aliases never double the input");
        keys.UnionWith(new[] { Key.S, Key.D, Key.E });
        Check(ShipFlightController.ReadControls(keys.Contains) == default, "Opposing keys cancel");
        for (var i = 0; i < 32; i++)
        {
            var heading = i * Mathf.Tau / 32;
            var aim = Vector2.Right.Rotated(heading + 0.7f);
            var command = pilot.Resolve(new(1, 0, 0), heading, aim);
            Check(command.LocalTranslation.Rotated(heading).DistanceTo(aim) < 0.0001f,
                "Cursor mode W moves towards cursor independently of the ship heading");
            command = pilot.Resolve(new(0, 1, 0), heading, aim);
            Check(command.ManualTurn == 0 && command.LocalTranslation.Rotated(heading).DistanceTo(new Vector2(-aim.Y, aim.X)) < 0.0001f,
                "Cursor mode A/D strafes rather than acting as a rudder");
            Check(pilot.Resolve(new(1, 1, 1), heading, aim).LocalTranslation.Length() <= 1.0001f,
                "Diagonal and redundant strafe input never boosts engine power");
        }
        pilot.CycleScheme(0);
        Check(pilot.Scheme == ShipFlightController.ControlScheme.Cardinal, "Native cycle is cursor -> cardinal");
        var absolute = pilot.Resolve(new(1, 0, 0), 1.2f, Vector2.Left);
        Check(absolute.LocalTranslation.Rotated(1.2f).DistanceTo(Vector2.Up) < 0.0001f,
            "Cardinal W always moves up the map while the cursor independently turns the ship");
        pilot.CycleScheme(0);
        Check(pilot.Scheme == ShipFlightController.ControlScheme.Keyboard, "Native cycle is cardinal -> keyboard");
        var keyboard = pilot.Resolve(new(1, -1, 1), 1.2f, Vector2.Left);
        Check(keyboard.ManualTurn == -1 && keyboard.HeadingError == 0 &&
            keyboard.LocalTranslation.DistanceTo(new Vector2(1, 1).Normalized()) < 0.0001f,
            "Keyboard mode uses W/S thrust, A/D turn, Q/E strafe and ignores cursor");
        pilot.CycleScheme(0);
        Check(pilot.Scheme == ShipFlightController.ControlScheme.Cursor, "Native mode cycle wraps");
        var boundary = pilot.Resolve(default, Mathf.Pi - 0.01f, Vector2.Right.Rotated(-Mathf.Pi + 0.01f));
        Check(Mathf.Abs(boundary.HeadingError - 0.02f) < 0.0001f, "Cursor steering takes shortest turn across +/- PI");
        var deadzone = pilot.Resolve(default, 0, Vector2.Zero);
        Check(float.IsFinite(deadzone.HeadingError), "Cursor at ship center cannot produce a NaN heading");
    }

    private void Integration()
    {
        foreach (var fps in new[] { 30, 60, 120 })
        {
            var position = Vector2.Zero;
            var velocity = new Vector2(100, -50);
            float heading = 0, spin = 1;
            for (var i = 0; i < fps; i++)
                ShipFlightController.Integrate(ref position, ref velocity, ref heading, ref spin,
                    Vector2.Zero, 0, 1.0f / fps);
            Check(velocity.DistanceTo(new Vector2(80, -40)) < 0.001f && Mathf.Abs(spin - 0.8f) < 0.0001f,
                "Coasting retains native 80 percent velocity per second at all frame rates");
            Check(position.X > 89 && position.X < 90 && heading > 0.89f && heading < 0.9f,
                "Released controls coast smoothly without instant or fixed-rate braking");
        }
        var fullPosition = Vector2.Zero;
        var fullVelocity = Vector2.Zero;
        float fullHeading = 0, fullSpin = 0;
        ShipFlightController.Integrate(ref fullPosition, ref fullVelocity, ref fullHeading, ref fullSpin,
            new Vector2(200, 30), 0.7f, 1);
        var splitPosition = Vector2.Zero;
        var splitVelocity = Vector2.Zero;
        float splitHeading = 0, splitSpin = 0;
        for (var i = 0; i < 120; i++)
            ShipFlightController.Integrate(ref splitPosition, ref splitVelocity, ref splitHeading, ref splitSpin,
                new Vector2(200, 30), 0.7f, 1.0f / 120);
        Check(fullPosition.DistanceTo(splitPosition) < 0.01f && fullVelocity.DistanceTo(splitVelocity) < 0.01f &&
            Mathf.Abs(fullHeading - splitHeading) < 0.001f, "Force integration is independent of substep count");
    }

    private ModularShip Reference()
    {
        var ship = new ModularShip();
        ship.SeedReferenceShip();
        return ship;
    }

    private void Actuators()
    {
        var ship = Reference();
        var pilot = new ShipFlightController();
        var activity = new Dictionary<int, float>();
        var forward = pilot.Evaluate(ship, pilot.Resolve(new(1, 0, 0), 0, Vector2.Right), Vector2.Zero, 0, activity);
        Check(forward.LocalForce.X > 10000 && Mathf.Abs(forward.LocalForce.Y) < 0.01f && Mathf.Abs(forward.Torque) < 10,
            "Straight flight shares the two rear engines without unwanted yaw");
        var recomputedTorque = ship.GetThrusterMounts().Sum(mount =>
            mount.Offset.Cross(mount.Force) * mount.Power * ShipPropulsion.ForceScale * activity[mount.BlockId]);
        Check(Mathf.Abs(recomputedTorque) < 0.01f && Mathf.Abs(forward.Torque - recomputedTorque) < 0.01f,
            $"Straight thrust has zero torque when independently recomputed from active mounts (reported={forward.Torque}, actual={recomputedTorque})");
        var reverse = pilot.Evaluate(ship, pilot.Resolve(new(-1, 0, 0), 0, Vector2.Right), Vector2.Zero, 0, activity);
        Check(reverse.ActiveCount == 0 && reverse.LocalForce.IsZeroApprox(), "Rear-only engines cannot invent reverse thrust");
        var strafe = pilot.Evaluate(ship, pilot.Resolve(new(0, 1, 0), 0, Vector2.Right), Vector2.Zero, 0, activity);
        Check(strafe.LocalForce.IsZeroApprox(), "Rear-only engines cannot invent lateral thrust");
        var turning = pilot.Evaluate(ship, pilot.Resolve(new(1, 0, 0), 0, Vector2.Down), Vector2.Zero, 0, activity);
        Check(turning.Torque > 0 && activity.Values.All(value => value >= 0 && value <= 1),
            "Cursor turns through bounded actuator power");
        var recomputedForce = Vector2.Zero;
        foreach (var mount in ship.GetThrusterMounts())
            recomputedForce += mount.Force * mount.Power * ShipPropulsion.ForceScale * activity[mount.BlockId];
        Check(recomputedForce.DistanceTo(turning.LocalForce) < 0.01f, "Visible engine activity equals the actual applied thrust");
        var brake = pilot.Evaluate(ship, pilot.Resolve(default, 0, Vector2.Right), Vector2.Zero, 1, activity);
        Check(brake.Torque < 0, "Overshoot uses opposing torque to brake angular momentum");

        foreach (var block in ship.Blocks.Where(b => b.Spec.IsThruster)) block.Enabled = false;
        var gyro = pilot.Evaluate(ship, pilot.Resolve(default, 0, Vector2.Down), Vector2.Zero, 0, activity);
        Check(gyro.Torque > 0 && gyro.LocalForce.IsZeroApprox(), "Torquer turns without invented linear thrust");
        foreach (var block in ship.Blocks.Where(b => !b.Spec.IsCore)) block.Enabled = false;
        var inert = pilot.Evaluate(ship, default, Vector2.Zero, 0, activity);
        Check(inert.Torque == 0 && inert.LocalForce.IsZeroApprox() && activity.Values.All(level => level == 0),
            "With detached modules disabled and no control input, native core actuators remain idle");
        ship.Core!.Enabled = false;
        Check(pilot.Evaluate(ship, default, Vector2.Zero, 0, activity) == default, "No live core means no control");

        var omnidirectional = new ModularShip();
        var core = omnidirectional.AddBlock("core", Vector2.Zero, 0);
        foreach (var port in new[] { 0, 2, 4, 5 })
        {
            var (position, angle) = ModularShip.SnapBlock(ShipShapeLibrary.Get("thruster").Ports[0],
                core.Spec.Ports[port], core.Position, core.Angle);
            omnidirectional.AddBlock("thruster", position, angle);
        }
        Check(omnidirectional.GetThrusterMounts().Count == 4, "Test ship has four physically connected cardinal engines");
        foreach (var direction in new[] { Vector2.Right, Vector2.Left, Vector2.Up, Vector2.Down })
        {
            var command = new ShipFlightController.Command(direction, 0, 0);
            var result = pilot.Evaluate(omnidirectional, command, Vector2.Zero, 0, activity);
            // 侧喷口偏离质心且没有转向器，稳住朝向时可用侧推会减少。
            Check(result.LocalForce.Dot(direction) > 0 &&
                result.LocalForce.Dot(direction) >= result.LocalForce.Length() * 0.999f,
                "Installed reverse and lateral engines provide the requested movement");
            Check(activity.Values.All(level => level >= 0 && level <= 1), "Four-way maneuver never overdrives an engine");
        }
        var overspeed = pilot.Evaluate(omnidirectional, new(Vector2.Right, 0, 0), Vector2.Right * 6000, 0, activity);
        Check(overspeed.LocalForce.X < 0, "Exceeding the 5000 speed target uses actual reverse thrust");
        var p = Vector2.Zero;
        var v = Vector2.Right * 6000;
        float h = 0, w = 0;
        ShipFlightController.Integrate(ref p, ref v, ref h, ref w,
            overspeed.LocalForce / omnidirectional.TotalMass, 0, 1.0f / 120);
        Check(v.X > 5000 && v.X < 6000, "Velocity target never instantly clamps momentum from external forces");
    }

    private void Tracking()
    {
        foreach (var step in new[] { 1.0f / 60, 1.0f / 120 })
        {
            var ship = Reference();
            var pilot = new ShipFlightController();
            var activity = new Dictionary<int, float>();
            var position = Vector2.Zero;
            var velocity = Vector2.Zero;
            float heading = 0, spin = 0;
            for (var time = 0.0f; time < 15; time += step)
            {
                var command = pilot.Resolve(default, heading, Vector2.Down);
                var output = pilot.Evaluate(ship, command, velocity.Rotated(-heading), spin, activity);
                ShipFlightController.Integrate(ref position, ref velocity, ref heading, ref spin,
                    output.LocalForce.Rotated(heading) / ship.TotalMass, output.Torque / ship.MomentOfInertia(), step);
                Check(float.IsFinite(heading) && float.IsFinite(spin), "Steering remains finite during a long turn");
            }
            Check(Mathf.Abs(Mathf.Wrap(heading - Mathf.Pi / 2, -Mathf.Pi, Mathf.Pi)) < 0.04f && Mathf.Abs(spin) < 0.03f,
                $"Cursor tracking settles on target with counter-thrust at dt={step}");
        }
    }

    private void RapidCursorTurn()
    {
        // A quick cursor flick must not leave the ship spinning after the
        // pointer stops. This reproduces the high-error input that exposed
        // the under-damped controller on the exported reference hull.
        var ship = Reference();
        var pilot = new ShipFlightController();
        var activity = new Dictionary<int, float>();
        var position = Vector2.Zero;
        var velocity = Vector2.Zero;
        float heading = 0, spin = 0;
        const float step = 1.0f / 120;
        for (var time = 0.0f; time < 0.18f; time += step)
        {
            var command = pilot.Resolve(default, heading, Vector2.Left);
            var output = pilot.Evaluate(ship, command, velocity.Rotated(-heading), spin, activity);
            ShipFlightController.Integrate(ref position, ref velocity, ref heading, ref spin,
                output.LocalForce.Rotated(heading) / ship.TotalMass, output.Torque / ship.MomentOfInertia(), step);
        }
        for (var time = 0.0f; time < 4.0f; time += step)
        {
            var command = pilot.Resolve(default, heading, Vector2.Left);
            var output = pilot.Evaluate(ship, command, velocity.Rotated(-heading), spin, activity);
            ShipFlightController.Integrate(ref position, ref velocity, ref heading, ref spin,
                output.LocalForce.Rotated(heading) / ship.TotalMass, output.Torque / ship.MomentOfInertia(), step);
        }
        Check(Mathf.Abs(Mathf.Wrap(heading - Mathf.Pi, -Mathf.Pi, Mathf.Pi)) < 0.04f && Mathf.Abs(spin) < 0.05f,
            "A fast cursor turn reaches the target and stops instead of spinning continuously");
    }

    private void ViewIntegration()
    {
        var view = new UniverseView();
        view.SetShip(Reference());
        AddChild(view);
        view.SetProcess(false);
        view.SetPhysicsProcess(false);
        view._Input(new InputEventKey { Keycode = Key.R, Pressed = true, Echo = true });
        Check(view.ControlScheme == ShipFlightController.ControlScheme.Cursor, "Held R does not repeat mode switches");
        view._Input(new InputEventKey { Keycode = Key.R, Pressed = true });
        Check(view.ControlScheme == ShipFlightController.ControlScheme.Cardinal, "Flight view wires the native R binding");
        view._Input(new InputEventKey { Keycode = Key.R, Pressed = true, CtrlPressed = true });
        Check(view.ControlScheme == ShipFlightController.ControlScheme.Cardinal, "Modified R does not switch flight controls");
        var before = view.PlayerPosition;
        var beforeHeading = view.PlayerHeading;
        view._PhysicsProcess(1.0 / 60);
        Check(view.PlayerPosition != before && view.PlayerVelocity.Length() > 0,
            "Fixed physics updates gravity and motion while render processing is disabled");
        view.UpdateRenderTransform(0);
        Check(view.RenderPosition == before, "Render interpolation starts at the previous physical position");
        view.UpdateRenderTransform(0.5f);
        Check(view.RenderPosition.DistanceTo(before.Lerp(view.PlayerPosition, 0.5f)) < 0.0001f &&
            Mathf.Abs(Mathf.AngleDifference(view.RenderHeading,
                Mathf.LerpAngle(beforeHeading, view.PlayerHeading, 0.5f))) < 0.0001f,
            "High refresh rendering interpolates both position and the shortest heading arc");
        var actual = view.PlayerPosition;
        view.UpdateRenderTransform(0.9f);
        Check(view.PlayerPosition == actual, "Render interpolation never changes physics or collisions");
        view.ResumeFlight();
        view.UpdateRenderTransform(0);
        Check(view.RenderPosition == actual, "Returning from the builder resets interpolation history");
        view.QueueFree();
    }

    private void CacheAndPerformance()
    {
        var ship = Reference();
        var revision = ship.Revision;
        var engine = ship.Blocks.First(b => b.Spec.IsThruster);
        engine.Enabled = false;
        Check(ship.Revision != revision, "Disabling an actuator invalidates flight and drawing caches");
        revision = ship.Revision;
        engine.Enabled = false;
        engine.Position = engine.Position;
        engine.Angle = engine.Angle;
        Check(ship.Revision == revision, "Unchanged block setters do not repeatedly invalidate caches");
        engine.Position += Vector2.Right;
        Check(ship.Revision != revision, "Dragging a block invalidates caches even before mouse release");
        revision = ship.Revision;
        engine.Angle += 0.2f;
        Check(ship.Revision != revision, "Rotation invalidates cached force directions and polygons");
        var copy = ship.Clone();
        revision = copy.Revision;
        copy.Blocks[0].Angle += 0.1f;
        Check(copy.Revision != revision, "Cloned blocks notify their own ship");
        ship.Restore(copy);
        revision = ship.Revision;
        ship.Blocks[0].Angle += 0.1f;
        Check(ship.Revision != revision, "Restored blocks keep cache invalidation callbacks");

        var large = Reference();
        var host = large.Core!;
        var hostPort = 0;
        for (var i = 0; i < 120; i++)
        {
            var (position, angle) = ModularShip.SnapBlock(ShipShapeLibrary.Get("hull").Ports[0],
                host.Spec.Ports[hostPort], host.Position, host.Angle);
            host = large.AddBlock("hull", position, angle);
            hostPort = 2;
        }
        var pilot = new ShipFlightController();
        var profile = ShipFlightController.Capture(large);
        var activity = new Dictionary<int, float>();
        var command = new ShipFlightController.Command(Vector2.Right, 0.7f, 0);
        for (var i = 0; i < 100; i++) pilot.Evaluate(profile, command, Vector2.Zero, 0, activity);
        var expected = pilot.Evaluate(large, command, Vector2.Zero, 0, activity);
        var actual = pilot.Evaluate(profile, command, Vector2.Zero, 0, activity);
        Check(actual == expected, "Cached profile preserves the complete force allocation result");
        var watch = Stopwatch.StartNew();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 600; i++) pilot.Evaluate(profile, command, Vector2.Zero, 0, activity);
        var cachedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var cachedMilliseconds = watch.Elapsed.TotalMilliseconds / 600;
        Check(cachedBytes < 4096, "Steady flight reuses solver buffers without per-substep heap allocation");
        watch.Restart();
        for (var i = 0; i < 30; i++) pilot.Evaluate(large, command, Vector2.Zero, 0, activity);
        var uncachedMilliseconds = watch.Elapsed.TotalMilliseconds / 30;

        var view = new UniverseView();
        view.SetShip(large);
        AddChild(view);
        view.SetProcess(false);
        view.SetPhysicsProcess(false);
        for (var i = 0; i < 20; i++) view._PhysicsProcess(1.0 / 60);
        allocated = GC.GetAllocatedBytesForCurrentThread();
        watch.Restart();
        for (var i = 0; i < 300; i++) view._PhysicsProcess(1.0 / 60);
        var physicsMilliseconds = watch.Elapsed.TotalMilliseconds / 300;
        var physicsBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Check(physicsBytes < 16384, $"Complete physics ticks do not rebuild topology or allocate solver arrays ({physicsBytes} bytes/300 ticks)");
        var reflectedProfile = typeof(UniverseView).GetField("_flightProfile",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        foreach (var block in large.Blocks.Where(b => b.Spec.IsThruster)) block.Enabled = false;
        view._PhysicsProcess(1.0 / 60);
        var refreshed = (ShipFlightController.FlightProfile)reflectedProfile.GetValue(view)!;
        Check(refreshed.Revision == large.Revision && refreshed.Mounts.Count == 1 && refreshed.Mounts[0].BlockId == large.Core!.Id,
            "Running flight refreshes disabled external engines while retaining the native core actuator");
        var added = large.AddBlock("thruster", Vector2.One * 50000, 0);
        view._PhysicsProcess(1.0 / 60);
        refreshed = (ShipFlightController.FlightProfile)reflectedProfile.GetValue(view)!;
        Check(refreshed.Revision == large.Revision && refreshed.Mounts.Count == 1,
            "A disconnected added engine updates mass without inventing connected thrust");
        large.RemoveBlock(added);
        view._PhysicsProcess(1.0 / 60);
        refreshed = (ShipFlightController.FlightProfile)reflectedProfile.GetValue(view)!;
        Check(refreshed.Revision == large.Revision, "Removing a block refreshes running-flight caches");
        view.QueueFree();
    }
}
