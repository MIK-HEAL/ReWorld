using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

/// <summary>在有效的 CanvasItem 绘制回调中验证几何缓存与稳定分配量。</summary>
public partial class FlightRenderingChecks : Node2D
{
    private readonly ModularShip _ship = new();
    private readonly Dictionary<int, float> _activity = new();

    public override void _Ready()
    {
        _ship.SeedReferenceShip();
        foreach (var block in _ship.Blocks.Where(b => b.Spec.IsThruster)) _activity[block.Id] = 0.8f;
        QueueRedraw();
    }

    public override void _Draw()
    {
        try
        {
            // 同一画布按不同变换绘制，确保缓存仅存本地几何且缓冲区可安全复用。
            for (var i = 0; i < 20; i++) Paint(i);
            var watch = Stopwatch.StartNew();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 200; i++) Paint(i);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            var milliseconds = watch.Elapsed.TotalMilliseconds / 200;
            if (bytes >= 4096) throw new InvalidOperationException($"Ship drawing allocated {bytes} bytes in steady flight");

            // 改位置、朝向、可用状态后重建一次，再检查后续绘制恢复为稳定分配量。
            var engine = _ship.Blocks.First(b => b.Spec.IsThruster);
            engine.Position += new Vector2(20, 5);
            engine.Angle += 0.4f;
            engine.Enabled = false;
            Paint(0);
            allocated = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 200; i++) Paint(i);
            var changedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            if (changedBytes >= 4096) throw new InvalidOperationException("Edited ship rebuilt geometry on every render");
            _ship.Restore(new ModularShip());
            Paint(0);
            _ship.SeedReferenceShip();
            Paint(0);
            GD.Print($"FLIGHT_RENDERING_CHECKS_PASS: CPU drawing={milliseconds:F3} ms/frame, " +
                $"{bytes} bytes/200 draws, after edit={changedBytes} bytes/200 draws (headless; GPU time excluded)");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
    }

    private void Paint(int frame) => ModularShipVisual.Draw(this, _ship, new Vector2(frame % 32, frame % 17),
        frame * 0.02f, thrusterActivity: _activity);
}
