using Godot;
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

public partial class MainNavigationChecks : Node
{
    private int _checks;

    private void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private async Task Press(Key key, bool echo = false)
    {
        Input.ParseInputEvent(new InputEventKey { Keycode = key, Pressed = true, Echo = echo });
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Input.ParseInputEvent(new InputEventKey { Keycode = key, Pressed = false });
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private void CheckPage(CanvasItem page, bool active)
    {
        Check(page.Visible == active && page.CanProcess() == active,
            $"{page.Name}: visibility and processing match page state");
        Check(page.ProcessMode == (active ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled),
            $"{page.Name}: hidden page cannot process or fly");
    }

    public override async void _Ready()
    {
        try
        {
            var main = GD.Load<PackedScene>("res://scenes/main/Main.tscn").Instantiate<Main>();
            AddChild(main);
            var builder = main.GetNode<ShipBuilder>("ShipBuilder");
            var universe = main.GetNode<UniverseView>("Universe");
            Check(main.GetChildCount() == 2, "Shell creates only the universe and modular builder");
            CheckPage(universe, true);
            CheckPage(builder, false);
            var shipField = typeof(UniverseView).GetField("_ship", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Check(ReferenceEquals(shipField.GetValue(universe), builder.Ship), "Flight and editor share the same ship");
            await Press(Key.R);
            Check(universe.ControlScheme == ShipFlightController.ControlScheme.Cardinal, "R changes flight mode in the universe");
            await Press(Key.Tab);
            Check(universe.TableOpen, "Tab opens fleet table without opening builder");
            await Press(Key.Tab);
            Check(!universe.TableOpen, "Tab returns from command table");

            foreach (var key in new[] { Key.Tab, Key.B, Key.Space, Key.Escape })
            {
                await Press(key);
                CheckPage(universe, true);
                CheckPage(builder, false);
            }

            await Press(Key.M);
            CheckPage(universe, false);
            CheckPage(builder, true);
            await Press(Key.R);
            Check(universe.ControlScheme == ShipFlightController.ControlScheme.Cardinal,
                "Builder rotation cannot change the hidden universe flight mode");
            var position = universe.PlayerPosition;
            var block = builder.Ship.Blocks.First(b => !b.Spec.IsCore);
            builder.Ship.RemoveBlock(block);
            var remaining = builder.Ship.Blocks.Count;
            foreach (var key in new[] { Key.Tab, Key.B, Key.Space })
            {
                await Press(key);
                CheckPage(builder, true);
                CheckPage(universe, false);
            }
            await Press(Key.M, echo: true);
            CheckPage(builder, true);
            Check(universe.PlayerPosition == position, "Universe remains frozen while editing");
            await Press(Key.Escape);
            CheckPage(universe, true);
            CheckPage(builder, false);
            Check(universe.ControlScheme == ShipFlightController.ControlScheme.Cardinal, "Flight mode survives M editor round trip");
            await Press(Key.M);
            Check(builder.Ship.Blocks.Count == remaining, "Completed ship edits survive page switches");
            Check(ReferenceEquals(shipField.GetValue(universe), builder.Ship), "Page switches never replace the shared ship");
            await Press(Key.M);
            CheckPage(universe, true);
            CheckPage(builder, false);
            Check(main.GetChildCount() == 2, "Removed shortcuts never create factory, route or test pages");
            await Press(Key.J);
            Check(universe.ChartOpen, "J opens the star chart in flight");
            await Press(Key.M);
            CheckPage(builder, true);
            Check(!universe.ChartOpen, "M closes the navigation overlay before editing");
            await Press(Key.M);
            Check(universe.BeginWarp(Vector2I.Right), "Shared ship can power its installed warp engine");
            await Press(Key.M);
            CheckPage(universe, true);
            CheckPage(builder, false);
            await Press(Key.Escape);
            Check(universe.Warp.State == WarpDrive.Phase.Ready, "Esc cancels charging through Main input routing");

            var factory = new FactoryModuleGraph();
            Check(FactoryModuleGraph.Definitions.Keys.OrderBy(k => k).SequenceEqual(
                new[] { "battery", "cargo", "core", "miner", "radiator", "reactor", "refinery", "solar" }),
                "Only useful factory modules are retained");
            Check(factory.AddModule("thruster", Vector2I.Zero) is null, "Factory backend contains no alternate ship engines");
            factory.AddModule("core", Vector2I.Zero);
            var solar = factory.AddModule("solar", Vector2I.Right)!;
            Check(factory.IsOperational(solar) && factory.GetMetrics().Generation == 6, "Retained factory links supply power");
            Check(factory.AddModule("miner", Vector2I.Right) is null, "Retained factory footprints reject overlap");
            Check(factory.MoveModuleAt(Vector2I.Right, new Vector2I(8, 8)) && !factory.IsOperational(solar),
                "Disconnected factory branches stop contributing");
            Check(factory.GetMetrics().Generation == 1, "Disconnected production leaves core generation only");
            Check(!ShipShapeLibrary.BuildOrder.Any(id => id is "miner" or "refinery" or "solar" or "battery"),
                "Factory production modules are absent from the ship palette");

            GD.Print($"MAIN_NAVIGATION_CHECKS_PASS: {_checks} assertions");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
    }
}
