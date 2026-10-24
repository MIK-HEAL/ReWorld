using Godot;
using System;
using System.Collections.Generic;
using System.Reflection;

/// <summary>Run without --headless for real window frame pacing, including the render driver.</summary>
public partial class NativeFlightBenchmark : Node
{
    private UniverseView _view = null!;
    private int _frames;
    private readonly List<double> _samples = new(900);
    private readonly List<double> _cpuSamples = new(900);
    private int _beforeDraws;
    public override void _Ready()
    {
        var ship = new ModularShip(); ship.SeedReferenceShip();
        _view = new UniverseView(); _view.SetShip(ship); AddChild(_view);
        typeof(UniverseView).GetField("_showPerformance", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_view, true);
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.W, Pressed = true });
    }
    public override void _Process(double delta)
    {
        _frames++;
        if (_frames == 120) _beforeDraws = _view.RetainedDrawCount;
        if (_frames > 120)
        {
            _samples.Add(delta * 1000);
            _cpuSamples.Add(Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000);
        }
        if (_frames < 720) return;
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.W, Pressed = false });
        _samples.Sort(); _cpuSamples.Sort();
        GD.Print($"NATIVE_FLIGHT_BENCHMARK: driver={DisplayServer.GetName()}, " +
            $"frames={_samples.Count}, frame P50={_samples[_samples.Count/2]:F2}ms, " +
            $"P95={_samples[(int)(_samples.Count*0.95)]:F2}ms, " +
            $"CPU P95={_cpuSamples[(int)(_cpuSamples.Count*0.95)]:F2}ms, static redraws={_view.RetainedDrawCount-_beforeDraws}");
        GetTree().Quit();
    }
}
