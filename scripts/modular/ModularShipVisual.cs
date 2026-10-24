using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// 模块化舰体的可复用渲染器（以 Reassembly 为准）。
///
/// 把 ModularShip 的每个方块按其 ShapeSpec 多边形（旋转后）绘制到调用方的画布上，
/// 包括：填充 + 描边、连接线、推进器喷口、武器炮口、核心高亮、损坏/失连变暗。
///
/// 这是一个纯静态绘图辅助类，不持有节点。供宇宙航行视图使用，
/// 确保「在编辑器里拼出的多边形舰体」与「运行时看到的舰体」完全一致。
/// </summary>
public static class ModularShipVisual
{
    /// <summary>绘制整艘舰体到当前 CanvasItem 画布（须已设置好世界变换）。</summary>
    /// <param name="canvas">目标 CanvasItem（用于 Draw* 调用）。</param>
    /// <param name="ship">要绘制的舰体。</param>
    /// <param name="worldPos">舰体质心/原点在世界坐标的位置。</param>
    /// <param name="heading">舰体朝向（弧度，绕 worldPos 旋转）。</param>
    /// <param name="drawPorts">是否绘制端口标记（编辑器用，运行时关闭）。</param>
    /// <param name="thrusterActivity">推进器激活度映射（块 Id → 0~1），用于喷口火焰。</param>
    public static void Draw(
        CanvasItem canvas,
        ModularShip ship,
        Vector2 worldPos,
        float heading,
        bool drawPorts = false,
        IReadOnlyDictionary<int, float>? thrusterActivity = null)
    {
        if (ship is null || ship.Blocks.Count == 0)
            return;

        // 以质心为原点，把每个方块的世界位置 = worldPos + (block.Position - com).Rotated(heading)。
        var com = ship.CenterOfMass();
        var mainComponent = ship.GetMainComponent();

        // 1. 连接线（端口重合的方块之间）。
        foreach (var (a, b) in ship.GetConnections())
        {
            var aWorld = worldPos + (a.Position - com).Rotated(heading);
            var bWorld = worldPos + (b.Position - com).Rotated(heading);
            canvas.DrawLine(aWorld, bWorld, new Color("#36586a", 0.9f), 3.0f);
        }

        // 2. 方块多边形。
        foreach (var block in ship.Blocks)
        {
            var spec = block.Spec;
            var center = worldPos + (block.Position - com).Rotated(heading);
            var angle = block.Angle + heading;
            var vertices = spec.Vertices()
                .Select(v => center + v.Rotated(angle))
                .ToArray();

            var color = spec.Color.Lerp(new Color("#f0f4f0"), 0.15f);
            if (!block.Enabled || !mainComponent.Contains(block))
                color = color.Darkened(0.55f);

            canvas.DrawColoredPolygon(vertices, color);
            canvas.DrawPolyline(vertices.Append(vertices[0]).ToArray(), new Color("#f0f4f0", 0.75f), 1.2f);

            // 核心高亮。
            if (spec.IsCore)
            {
                var radius = spec.Radius * 0.4f;
                var diamond = new[]
                {
                    center + new Vector2(0, -radius).Rotated(heading),
                    center + new Vector2(radius, 0).Rotated(heading),
                    center + new Vector2(0, radius).Rotated(heading),
                    center + new Vector2(-radius, 0).Rotated(heading),
                };
                canvas.DrawPolyline(diamond.Append(diamond[0]).ToArray(), new Color("#fff0b2"), 1.5f);
            }
            else if (spec.IsThruster)
            {
                var activity = thrusterActivity is not null && thrusterActivity.TryGetValue(block.Id, out var a) ? a : 0.0f;
                DrawEngine(canvas, spec, center, angle, activity);
            }
            else if (spec.IsWeapon)
            {
                // 武器：炮口方向。
                var fireDir = spec.WeaponDirection.Rotated(angle);
                canvas.DrawLine(center, center + fireDir * spec.Radius, new Color("#ffe9a6", 0.7f), 2.0f);
            }

            // 失连方块标记。
            if (!mainComponent.Contains(block))
            {
                canvas.DrawLine(center + new Vector2(-7, -7), center + new Vector2(7, 7), new Color("#f07c8e", 0.8f), 2.0f);
                canvas.DrawLine(center + new Vector2(7, -7), center + new Vector2(-7, 7), new Color("#f07c8e", 0.8f), 2.0f);
            }
        }

        // 3. 端口标记（可选）。
        if (drawPorts)
        {
            foreach (var block in ship.Blocks)
            {
                if (!block.Enabled)
                    continue;
                foreach (var port in block.Spec.Ports)
                {
                    if (!port.CanAttach) continue;
                    var world = worldPos + (block.Position - com).Rotated(heading) + port.LocalPosition.Rotated(block.Angle + heading);
                    canvas.DrawCircle(world, 2.5f, new Color("#7bb7e8", 0.6f));
                }
            }
        }
    }

    /// <summary>编辑器与飞行共用喷口几何；宽度取真实的后边，排气沿局部 -X。</summary>
    public static void DrawEngine(CanvasItem canvas, ShipShapeLibrary.ShapeSpec spec,
        Vector2 center, float angle, float activity = 0)
    {
        Vector2 At(Vector2 point) => center + point.Rotated(angle);
        var nozzle = spec.ExhaustPosition;
        var width = spec.ExhaustHalfWidth * 0.82f;
        var mouthTop = nozzle + Vector2.Up * width;
        var mouthBottom = nozzle + Vector2.Down * width;
        canvas.DrawLine(At(mouthTop), At(mouthBottom), new Color("#95aeac"), 2.2f, true);
        // 内部收缩的喷管使短梯形引擎在缩小时也能读出前后方向。
        var throat = new Vector2(-nozzle.X * 0.45f, 0);
        canvas.DrawLine(At(mouthTop), At(throat + Vector2.Up * width * 0.35f), new Color("#75ceac", 0.65f), 1.2f, true);
        canvas.DrawLine(At(mouthBottom), At(throat + Vector2.Down * width * 0.35f), new Color("#75ceac", 0.65f), 1.2f, true);
        if (activity <= 0.01f) return;
        var length = spec.Radius * (0.55f + 1.5f * activity);
        canvas.DrawColoredPolygon(new[]
        {
            At(mouthTop), At(nozzle + Vector2.Left * length), At(mouthBottom),
        }, new Color("#75ceac", 0.3f + activity * 0.35f));
        canvas.DrawColoredPolygon(new[]
        {
            At(nozzle + Vector2.Up * width * 0.4f),
            At(nozzle + Vector2.Left * length * 0.65f),
            At(nozzle + Vector2.Down * width * 0.4f),
        }, new Color("#d7fff0", 0.8f));
    }
}
