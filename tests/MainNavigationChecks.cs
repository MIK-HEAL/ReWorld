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
            // The campaign save may carry hostile diplomatic relations from a previous run,
            // which would lock the builder via TravelLocksBuilder. Reset to a clean save.
            var savePath = ProjectSettings.GlobalizePath("user://reworld_campaign.json");
            if (System.IO.File.Exists(savePath)) System.IO.File.Delete(savePath);
            var main = GD.Load<PackedScene>("res://scenes/main/Main.tscn").Instantiate<Main>();
            AddChild(main);
            Check(main.GetNodeOrNull("StartScreen") is not null, "Shell boots into the start screen");
            Check(main.GetChildCount() == 1, "Game pages are created only after start selection");
            await Press(Key.Enter);
            Check(main.GetChildCount() == 6, "Shell creates the universe, builder, upgrade, station, factory and AI bridge");
            var builder = main.GetNode<ShipBuilder>("ShipBuilder");
            var universe = main.GetNode<UniverseView>("Universe");
            var upgrades = main.GetNode<UpgradeScreen>("Upgrades");
            var station = main.GetNode<StationScreen>("Station");
            var factoryScreen = main.GetNode<FactoryBuilderScreen>("FactoryBuilder");
            Check(main.GetChildCount() == 6, "Shell creates the universe, builder, upgrade, station, factory and AI bridge");
            CheckPage(universe, true);
            CheckPage(builder, false);
            CheckPage(upgrades, false);
            CheckPage(station, false);
            CheckPage(factoryScreen, false);
            var shipField = typeof(UniverseView).GetField("_ship", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Check(ReferenceEquals(shipField.GetValue(universe), builder.Ship), "Flight and editor share the same ship");
            await Press(Key.R);
            Check(universe.ControlScheme == ShipFlightController.ControlScheme.Cardinal, "R changes flight mode in the universe");
            await Press(Key.Tab);
            Check(universe.TableOpen, "Tab opens fleet table without opening builder");
            await Press(Key.Tab);
            Check(!universe.TableOpen, "Tab returns from command table");
            await Press(Key.U);
            CheckPage(upgrades, true);
            CheckPage(universe, false);
            CheckPage(builder, false);
            await Press(Key.Escape);
            CheckPage(universe, true);

            foreach (var key in new[] { Key.Tab, Key.B, Key.Space, Key.Escape })
            {
                await Press(key);
                CheckPage(universe, true);
                CheckPage(builder, false);
            }

            await Press(Key.M);
            CheckPage(universe, false);
            CheckPage(builder, true);
            await Press(Key.U);
            CheckPage(upgrades, true);
            await Press(Key.Escape);
            CheckPage(builder, true);
            CheckPage(universe, false);
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
            Check(main.GetChildCount() == 6, "Navigation keeps one independent station screen without duplicate pages");
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

            // 模块化工厂蓝图：资源产出、存档、修改与波动行情（地表建造界面写入的数据层）。
            var blueprint = new System.Collections.Generic.List<FactoryModuleData>
            {
                new() { Kind = "core", X = 0, Y = 0 },
                new() { Kind = "miner", X = 1, Y = 0 },
                new() { Kind = "refinery", X = 3, Y = 0 },
            };
            var production = PlayerProgress.BlueprintProduction("iron", blueprint);
            Check(Mathf.Abs(production.Ore - 0.8f) < 1e-4f && Mathf.Abs(production.Alloy - 0.45f) < 1e-4f,
                "Blueprint production: miner ore 0.8/s and refinery alloy 0.45/s");
            Check(PlayerProgress.BlueprintProduction("iron", new System.Collections.Generic.List<FactoryModuleData>())
                == (0f, 0f), "Empty blueprints produce nothing");
            var progress = new PlayerProgress { Resources = 10_000 };
            var built = progress.TryAddFactoryWithBlueprint("planet:x", "iron", blueprint);
            Check(progress.FactoryCountOf("planet:x") == 1 && progress.Factories[0].Slots.Count == 1,
                "Blueprint factory persists per planet slot");
            Check(Mathf.Abs(built.Ore - 0.8f) < 1e-4f && Mathf.Abs(built.Alloy - 0.45f) < 1e-4f,
                "New slots lock the blueprint production rates");
            Check(Mathf.Abs(progress.FactoryIncomeRate()) < 1e-6f,
                "Modular slots produce resources instead of direct R income");
            var rates = progress.FactoryResourceRates();
            Check(Mathf.Abs(rates.Ore - 0.8f) < 1e-4f && Mathf.Abs(rates.Alloy - 0.45f) < 1e-4f,
                "Aggregate resource rates follow the modular slots");
            progress.TryUpdateFactorySlot("planet:x", 0, new System.Collections.Generic.List<FactoryModuleData>
            {
                new() { Kind = "core", X = 0, Y = 0 },
                new() { Kind = "miner", X = 1, Y = 0 },
            });
            Check(Mathf.Abs(progress.Factories[0].Slots[0].OreRate - 0.8f) < 1e-4f &&
                Mathf.Abs(progress.Factories[0].Slots[0].AlloyRate) < 1e-4f,
                "Editing a slot re-locks its blueprint production");
            var legacy = new PlayerProgress();
            legacy.Factories.Add(new FactoryData { PlanetId = "planet:y", Biome = "crystal", Count = 1 });
            Check(Mathf.Abs(legacy.FactoryIncomeRate() - 0.16f) < 1e-4f,
                "Legacy count-based factories keep the biome R rate");
            // 波动行情：基价 × 周期波动 × 供需系数；抛售压价、市场随时间回稳。
            Check(Mathf.Abs(PlayerProgress.ResourcePrice("iron", 0f, 0f) - 1f) < 1e-4f,
                "Iron trades at base price with no pressure at neutral time");
            Check(PlayerProgress.ResourcePrice("iron", 0.45f, 0f) < 0.56f,
                "Oversupply pressure cuts the trading price");
            var high = PlayerProgress.ResourcePrice("alloy", 0f, 0f);
            var low = PlayerProgress.ResourcePrice("alloy", 0f, Mathf.Pi / 0.07f);
            Check(Mathf.Abs(high - low) > 0.2f, "Market prices swing over the wave period");
            progress.AddMarketPressure("iron", 0.1f);
            progress.TickMarket(10f);
            Check(progress.MarketPressureOf("iron") == 0f,
                "Market pressure decays back toward equilibrium over time");

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
