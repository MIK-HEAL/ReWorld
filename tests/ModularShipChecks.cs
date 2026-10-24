using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>Headless regression scene: port geometry, assembly, propulsion and blueprint round trip.</summary>
public partial class ModularShipChecks : Node
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
            Geometry();
            Assembly();
            EngineStacking();
            Propulsion();
            EditorInteraction();
            GD.Print($"MODULAR_CHECKS_PASS: {_checks} assertions");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
    }

    private void Geometry()
    {
        foreach (var spec in ShipShapeLibrary.Shapes.Values)
        {
            var vertices = spec.Vertices();
            foreach (var port in spec.Ports)
            {
                var onEdge = false;
                for (var i = 0; i < vertices.Length; i++)
                {
                    var a = vertices[i];
                    var b = vertices[(i + 1) % vertices.Length];
                    var tangent = b - a;
                    var fraction = (port.Offset - a).Dot(tangent) / tangent.LengthSquared();
                    if (fraction < 0 || fraction > 1) continue;
                    if ((a + tangent * fraction).DistanceTo(port.Offset) > 0.001f) continue;
                    onEdge = Mathf.Abs(tangent.Normalized().Dot(port.Normal)) < 0.001f;
                }
                Check(onEdge, $"{spec.Id}: port is not on an edge with a perpendicular normal");
            }
        }
        var small = ShipShapeLibrary.Get("thruster");
        var medium = ShipShapeLibrary.Get("thruster_medium");
        var large = ShipShapeLibrary.Get("thruster_large");
        Check(small.Thrust == 20000 && medium.Thrust == 40000 && large.Thrust == 80000, "Native thrust values");
        Check(small.Scale == 2 && medium.Scale == 3 && large.Scale == 4, "Native engine scale levels");
        Check(small.Vertices()[0].DistanceTo(new Vector2(3.2135562f, -4.5f)) < 0.001f, "202 binary vertex");
        Check(medium.Vertices()[1].DistanceTo(new Vector2(-5, -24.820509f)) < 0.001f, "203 binary vertex");
        Check(large.Vertices()[1].DistanceTo(new Vector2(-7.5f, -35.980762f)) < 0.001f, "204 binary vertex");
        Check(small.Ports.Count(p => p.CanAttach) == 2, "Native stackable engine has two usable ports");
        Check(!small.Ports[1].CanConnect(ShipShapeLibrary.Get("hull").Ports[0]), "Exhaust must not accept hull connections");
        Check(small.Ports[0].Type == ShipShapeLibrary.PortType.ThrusterIn &&
            small.Ports[1].Type == ShipShapeLibrary.PortType.ThrusterOut, "Native shape 18 port types are IN=16 / OUT=8");
    }

    private void Assembly()
    {
        var ship = new ModularShip();
        ship.SeedReferenceShip();
        Check(ship.GetMainComponent().Count == ship.Blocks.Count, "Reference ship must be connected");
        foreach (var block in ship.Blocks)
            Check(ship.CanPlace(block.ShapeId, block.Position, block.Angle, block), $"Reference overlaps: {block.Id}/{block.ShapeId}");
        Check(!ship.CanPlace("hull", Vector2.Zero, 0), "Overlapping placement must fail");
        Check(ship.GetThrusterMounts().All(m => m.Force.Dot(Vector2.Right) > 0.999f), "Reference engines face forward");
        var copy = ShipDesign.FromShip(ship).ToShip();
        Check(copy.Blocks.Count == ship.Blocks.Count && copy.GetMainComponent().Count == ship.Blocks.Count, "Blueprint round trip");
        var clone = ship.Clone();
        Check(clone.Blocks.All(b => b.Spec.IsCore || b.Health > 0), "Placed modules have health");
        Check(clone.Blocks.Any(b => b.IsPortConnected(0)), "Clone rebuilds port occupancy");
    }

    private void EngineStacking()
    {
        var sizes = new[] { "thruster", "thruster_medium", "thruster_large" };
        foreach (var heading in new[] { 0f, 0.7f, Mathf.Pi, -1.2f })
        foreach (var frontSize in sizes)
        foreach (var rearSize in sizes)
        {
            var ship = new ModularShip();
            var core = ship.AddBlock("core", Vector2.Zero, heading);
            ModularShip.Block Attach(ModularShip.Block host, int hostPort, string id)
            {
                var port = ShipShapeLibrary.Get(id).Ports[0];
                var target = host.Spec.Ports[hostPort];
                Check(port.CanConnect(target) && target.CanConnect(port), "Native port compatibility is symmetric");
                var (position, angle) = ModularShip.SnapBlock(port, target, host.Position, host.Angle);
                Check(ship.CanPlace(id, position, angle), "Stacked engine polygons touch without overlapping");
                return ship.AddBlock(id, position, angle);
            }
            var front = Attach(core, 5, frontSize);
            var rear = Attach(front, 1, rearSize);
            Check(front.IsPortConnected(1) && rear.IsPortConnected(0), "Stack occupies both matching engine ports");
            Check(ModularShip.AreConnected(front, rear) && ship.GetMainComponent().Count == 3, "Stack stays connected to core at any heading");
            Check(front.Spec.ThrustDirection.Rotated(front.Angle).Dot(rear.Spec.ThrustDirection.Rotated(rear.Angle)) > 0.999f,
                "Stacked engines must face the same way");
            var expectedPower = front.Spec.Thrust + rear.Spec.Thrust;
            var activity = new Dictionary<int, float>();
            var thrust = ShipPropulsion.Evaluate(ship, Vector2.Right.Rotated(front.Angle), 0, activity);
            Check(thrust.ActiveCount == 2 && Mathf.Abs(thrust.LocalForce.Length() - expectedPower * ShipPropulsion.ForceScale) < 0.1f,
                "Every connected engine in a stack contributes its thrust");
            var loaded = ShipDesign.FromShip(ship).ToShip();
            Check(loaded.GetMainComponent().Count == 3 && loaded.TotalThrust == expectedPower, "Blueprint retains engine stack and thrust");
            ship.RemoveBlock(front);
            Check(ship.GetThrusterMounts().Count == 0, "Breaking the stack disconnects the rear engine from propulsion");
        }
        var engine = ShipShapeLibrary.Get("thruster");
        Check(!engine.Ports[0].CanConnect(engine.Ports[0]), "Engine fronts cannot join head to head");
        Check(!engine.Ports[1].CanConnect(engine.Ports[1]), "Engine exhausts cannot join tail to tail");
    }

    private void Propulsion()
    {
        var ship = new ModularShip();
        ship.SeedReferenceShip();
        var activity = new Dictionary<int, float>();
        var idle = ShipPropulsion.Evaluate(ship, Vector2.Zero, 0, activity);
        Check(idle.LocalForce.IsZeroApprox() && idle.Torque == 0, "Idle has no force or magic turning");
        var forward = ShipPropulsion.Evaluate(ship, Vector2.Right, 0, activity);
        Check(forward.LocalForce.X > 0 && Mathf.Abs(forward.LocalForce.Y) < 0.01f, "Forward thrust");
        var reverse = ShipPropulsion.Evaluate(ship, Vector2.Left, 0, activity);
        Check(reverse.ActiveCount == 0 && reverse.LocalForce.IsZeroApprox(), "Rear engines cannot reverse");
        var turn = ShipPropulsion.Evaluate(ship, Vector2.Zero, 1, activity);
        var combined = ShipPropulsion.Evaluate(ship, Vector2.Right, 1, activity);
        Check(turn.Torque > 0, "Positive yaw uses positive r cross F or torquer");
        Check(activity.Values.All(a => a <= 1) && combined.LocalForce.X <= forward.LocalForce.X + 0.01f, "Combined input must not double-fire an engine");
        Check(ship.MomentOfInertia() > 0 && float.IsFinite(ship.MomentOfInertia()), "Finite inertia");
        foreach (var mount in ship.GetThrusterMounts())
            foreach (var heading in new[] { 0.4f, 1.7f, -2.4f })
                Check(Mathf.Abs(mount.Offset.Rotated(heading).Cross(mount.Force.Rotated(heading)) - mount.Offset.Cross(mount.Force)) < 0.001f, "Yaw torque must be invariant under heading");
        foreach (var block in ship.Blocks.Where(b => !b.Spec.IsCore)) block.Enabled = false;
        var disabled = ShipPropulsion.Evaluate(ship, Vector2.Right, 1, activity);
        Check(disabled.LocalForce.IsZeroApprox() && disabled.Torque == 0 && activity.Count == 0, "Disabled modules do not propel or turn");
    }

    private void EditorInteraction()
    {
        var builder = new ShipBuilder();
        AddChild(builder);
        builder.SetProcess(false);
        var initial = builder.Ship.Clone();
        var engine = builder.Ship.Blocks.First(b => b.Spec.IsThruster);
        var cursor = engine.Position - builder.Ship.CenterOfMass() + new Vector2(640, 360);
        void Mouse(MouseButton button, bool pressed, Vector2 position, bool alt = false) =>
            builder._Input(new InputEventMouseButton { ButtonIndex = button, Pressed = pressed, Position = position, AltPressed = alt });
        void KeyPress(Key key, bool ctrl = false) =>
            builder._Input(new InputEventKey { Keycode = key, Pressed = true, CtrlPressed = ctrl });
        Mouse(MouseButton.Left, true, cursor);
        Mouse(MouseButton.Left, false, cursor);
        Check(builder.Ship.Blocks.Count == initial.Blocks.Count, "Click-release must preserve block");
        Mouse(MouseButton.Right, true, cursor);
        Check(builder.Ship.Blocks.Count == initial.Blocks.Count, "Right click must rotate rather than delete");
        KeyPress(Key.Delete);
        Check(builder.Ship.Blocks.Count == initial.Blocks.Count - 1, "Delete removes selected block");
        KeyPress(Key.Z, true);
        Check(builder.Ship.Blocks.Count == initial.Blocks.Count, "Undo deletion");
        KeyPress(Key.Y, true);
        Check(builder.Ship.Blocks.Count == initial.Blocks.Count - 1, "Redo deletion");
        KeyPress(Key.Z, true);
        Mouse(MouseButton.Left, true, cursor);
        builder._Input(new InputEventMouseMotion { Position = new Vector2(50, 650) });
        Mouse(MouseButton.Left, false, new Vector2(50, 650));
        Check(builder.Ship.Blocks.Zip(initial.Blocks).All(pair => pair.First.Position.DistanceTo(pair.Second.Position) < 0.001f), "Invalid drag restores original blueprint");
        builder.SelectShape("thruster");
        Mouse(MouseButton.WheelUp, true, new Vector2(500, 360), true);
        Check(builder.SelectedShapeId == "thruster_medium", "Alt wheel selects native medium engine");
        Mouse(MouseButton.WheelUp, true, new Vector2(500, 360), true);
        Check(builder.SelectedShapeId == "thruster_large", "Alt wheel selects native large engine");
        Mouse(MouseButton.WheelDown, true, new Vector2(500, 360), true);
        Check(builder.SelectedShapeId == "thruster_medium", "Alt wheel down selects smaller engine");
        Check(builder.Ship.Blocks.Count == initial.Blocks.Count, "Scaling a palette item must not mutate ship");

        // Exercise actual mouse placement and Alt-drag through the editor, rather
        // than bypassing snapping with AddBlock for the stacked engines.
        var stack = new ModularShip();
        var core = stack.AddBlock("core", Vector2.Zero, 0);
        var (mountPosition, mountAngle) = ModularShip.SnapBlock(ShipShapeLibrary.Get("thruster").Ports[0],
            core.Spec.Ports[5], core.Position, core.Angle);
        stack.AddBlock("thruster", mountPosition, mountAngle);
        builder.Ship.Restore(stack);
        foreach (var size in new[] { "thruster_medium", "thruster_large" })
        {
            builder.Progress.Collect(1000);
            Check(builder.Progress.TryUnlock(size), "Engine placement fixture purchases its technology");
            builder.FocusShip();
            var host = builder.Ship.Blocks.Last();
            var (position, angle) = ModularShip.SnapBlock(ShipShapeLibrary.Get(size).Ports[0],
                host.Spec.Ports[1], host.Position, host.Angle);
            var screen = position - builder.Ship.CenterOfMass() + new Vector2(640, 360);
            var before = builder.Ship.Blocks.Count;
            builder.SelectShape(size);
            Mouse(MouseButton.Left, true, screen);
            Mouse(MouseButton.Left, false, screen);
            var placed = builder.Ship.Blocks.Last();
            Check(builder.Ship.Blocks.Count == before + 1 && placed.ShapeId == size && placed.Position.DistanceTo(position) < 0.001f,
                "Mouse places next engine onto the previous exhaust port");
            Check(Mathf.Abs(Mathf.Wrap(placed.Angle - angle, -Mathf.Pi, Mathf.Pi)) < 0.001f,
                "Editor stack preserves thrust direction");
            Check(builder.Ship.GetMainComponent().Count == builder.Ship.Blocks.Count, "Editor stack connects to core");
        }
        KeyPress(Key.Z, true);
        Check(builder.Ship.Blocks.Count == 3 && builder.Ship.GetMainComponent().Count == 3, "Undo stacked placement");
        KeyPress(Key.Y, true);
        Check(builder.Ship.Blocks.Count == 4 && builder.Ship.GetMainComponent().Count == 4, "Redo stacked placement");
        builder.FocusShip();
        var last = builder.Ship.Blocks.Last();
        var camera = builder.Ship.CenterOfMass();
        var start = last.Position - camera + new Vector2(640, 360);
        var (clonePosition, _) = ModularShip.SnapBlock(last.Spec.Ports[0], last.Spec.Ports[1], last.Position, last.Angle);
        var drop = clonePosition - camera + new Vector2(640, 360);
        Mouse(MouseButton.Left, true, start, true);
        builder._Input(new InputEventMouseMotion { Position = drop });
        Mouse(MouseButton.Left, false, drop);
        Check(builder.Ship.Blocks.Count == 5 && builder.Ship.Blocks.Last().Position.DistanceTo(clonePosition) < 0.001f,
            "Alt-drag duplicates an engine onto the end of its stack");
        Check(builder.Ship.GetMainComponent().Count == 5 && builder.Ship.TotalThrust == 220000,
            "Editor clone stack contributes all four engines");
        builder.QueueFree();
    }
}
