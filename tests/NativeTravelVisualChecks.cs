using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

public partial class NativeTravelVisualChecks : Node
{
    private UniverseView _view = null!;
    private static void Set(UniverseView view, string field, object value)
        => typeof(UniverseView).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, value);
    private async Task Frames(int count = 5)
    { for (var i = 0; i < count; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw); }
    private async Task Press(Key key)
    {
        Input.ParseInputEvent(new InputEventKey { Keycode = key, Pressed = true });
        await Frames(1);
        Input.ParseInputEvent(new InputEventKey { Keycode = key, Pressed = false });
        await Frames(1);
    }
    private void Snapshot(string name)
    {
        var result = GetViewport().GetTexture().GetImage().SavePng($"res://.godot/travel-{name}.png");
        if (result != Error.Ok) throw new InvalidOperationException($"Could not save {name}: {result}");
    }
    public override async void _Ready()
    {
        try
        {
            var ship = new ModularShip(); ship.SeedReferenceShip();
            _view = new UniverseView(); _view.SetShip(ship); AddChild(_view);
            var station = UniverseGenerator.GenerateSystem(Vector2I.Zero).Bodies.First(b => b.Kind == UniverseGenerator.CelestialKind.Station);
            Set(_view, "_playerPosition", UniverseView.DockingPosition(station, ship.BoundingRadius()));
            Set(_view, "_targetBody", station);
            Set(_view, "_cameraZoom", 0.9f);
            _view.ResumeFlight();
            await Frames(15);
            Snapshot("station");
            await Press(Key.F); await Frames();
            if (!_view.IsDocked) throw new InvalidOperationException("F did not dock through input routing");
            Snapshot("dock");
            await Press(Key.F); await Press(Key.J); await Frames();
            if (!_view.ChartOpen) throw new InvalidOperationException("J did not open chart");
            Snapshot("chart");
            await Press(Key.Enter); await Frames();
            if (_view.Warp.State != WarpDrive.Phase.Charging) throw new InvalidOperationException("Enter did not charge");
            Snapshot("charge");
            var departure = _view.PlayerPosition;
            var deadline = Time.GetTicksMsec()+60000;
            var capturedCruise = false;
            var frameTimes = new List<double>();
            var lastFrame = Stopwatch.GetTimestamp();
            while (_view.Warp.LocksFlight && Time.GetTicksMsec() < deadline)
            {
                await Frames(1);
                var timestamp = Stopwatch.GetTimestamp();
                frameTimes.Add((timestamp-lastFrame)*1000.0/Stopwatch.Frequency);
                lastFrame = timestamp;
                if (!capturedCruise && _view.Warp.State == WarpDrive.Phase.Transit && _view.Warp.Progress >= 0.35f)
                {
                    if (_view.PlayerPosition.DistanceTo(departure) < 10000 || _view.PlayerVelocity.Length() < 10000)
                        throw new InvalidOperationException("Cruise did not advance actual world position and velocity");
                    Snapshot("cruise"); capturedCruise = true;
                }
            }
            frameTimes.Sort();
            GD.Print($"NATIVE_WARP_FRAME_TIMES: frames={frameTimes.Count}, P50={frameTimes[frameTimes.Count/2]:F2}ms, " +
                $"P95={frameTimes[(int)(frameTimes.Count*0.95)]:F2}ms, max={frameTimes[^1]:F2}ms");
            if (_view.Warp.LocksFlight || !capturedCruise) throw new InvalidOperationException("Continuous flight did not finish");
            await ToSignal(GetTree().CreateTimer(2.5), SceneTreeTimer.SignalName.Timeout);
            await Frames(); Snapshot("arrival");
            if (UniverseGenerator.WorldToSystemCell(_view.PlayerPosition) != Vector2I.Right)
                throw new InvalidOperationException("Warp did not arrive through normal physics clock");
            await Press(Key.J); await Press(Key.Left); await Press(Key.Left); await Press(Key.Enter);
            if (_view.Warp.State != WarpDrive.Phase.Charging)
                throw new InvalidOperationException($"Return flight did not charge: {_view.Warp.State}, chart={_view.ChartOpen}, " +
                    typeof(UniverseView).GetField("_notice", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_view));
            await ToSignal(GetTree().CreateTimer(4), SceneTreeTimer.SignalName.Timeout);
            var beforeExit = _view.PlayerPosition;
            await Press(Key.Escape);
            if (_view.Warp.State != WarpDrive.Phase.Braking) throw new InvalidOperationException("Esc did not brake midflight");
            await ToSignal(GetTree().CreateTimer(1.8), SceneTreeTimer.SignalName.Timeout);
            if (_view.Warp.LocksFlight || _view.PlayerPosition.DistanceTo(beforeExit) < 1000 ||
                _view.PlayerPosition.DistanceTo(_view.Warp.EndPosition) < 10000)
                throw new InvalidOperationException("Midflight exit did not stop at the actual travelled location");
            await Frames(); Snapshot("exit");
            GD.Print("NATIVE_TRAVEL_VISUAL_CHECKS_PASS: F dock/undock, J chart, Enter continuous flight, live cruise, safe arrival, Esc midflight braking, seven GPU snapshots");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
}
