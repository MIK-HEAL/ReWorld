using Godot;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

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
    private sealed class BlockGeometry
    {
        public required ModularShip.Block Block;
        public required Vector2 Center;
        public required Vector2[] LocalVertices;
        public required Vector2[] Vertices;
        public required Vector2[] Outline;
        public required Vector2[] Shadow;
        public required Vector2[] Inset;
        public required Vector2[] InsetOutline;
        public readonly Vector2[] Diamond = new Vector2[5];
        public readonly Vector2[] Flame = new Vector2[3];
        public readonly Vector2[] FlameCore = new Vector2[3];
    }

    private sealed class GeometryCache
    {
        public ulong Revision;
        public bool Initialized;
        public Vector2 Center;
        public HashSet<ModularShip.Block> MainComponent = new();
        public List<(ModularShip.Block A, ModularShip.Block B)> Connections = new();
        public BlockGeometry[] Blocks = Array.Empty<BlockGeometry>();

        public void Refresh(ModularShip ship)
        {
            Center = ship.CenterOfMass();
            MainComponent = new HashSet<ModularShip.Block>(ship.GetMainComponent());
            Connections = ship.GetConnections();
            Blocks = new BlockGeometry[ship.Blocks.Count];
            for (var i = 0; i < Blocks.Length; i++)
            {
                var block = ship.Blocks[i];
                var source = block.Spec.Vertices();
                var local = new Vector2[source.Length];
                for (var j = 0; j < source.Length; j++) local[j] = source[j].Rotated(block.Angle);
                Blocks[i] = new BlockGeometry
                {
                    Block = block, Center = block.Position - Center, LocalVertices = local,
                    Vertices = new Vector2[source.Length], Outline = new Vector2[source.Length + 1],
                    Shadow = new Vector2[source.Length], Inset = new Vector2[source.Length],
                    InsetOutline = new Vector2[source.Length + 1],
                };
            }
            Revision = ship.Revision;
            Initialized = true;
        }
    }

    private static readonly ConditionalWeakTable<ModularShip, GeometryCache> Geometry = new();
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
        var geometry = Geometry.GetValue(ship, static _ => new GeometryCache());
        if (!geometry.Initialized || geometry.Revision != ship.Revision) geometry.Refresh(ship);
        var com = geometry.Center;
        var mainComponent = geometry.MainComponent;

        // 1. 连接线（端口重合的方块之间）。
        foreach (var (a, b) in geometry.Connections)
        {
            var aWorld = worldPos + (a.Position - com).Rotated(heading);
            var bWorld = worldPos + (b.Position - com).Rotated(heading);
            canvas.DrawLine(aWorld, bWorld, new Color("#122c38", 0.9f), 7.0f);
            canvas.DrawLine(aWorld, bWorld, new Color("#3c8c99", 0.55f), 1.2f);
        }

        // 2. 方块多边形。
        foreach (var item in geometry.Blocks)
        {
            var block = item.Block;
            var spec = block.Spec;
            var center = worldPos + item.Center.Rotated(heading);
            var angle = block.Angle + heading;
            var vertices = item.Vertices;
            for (var i = 0; i < vertices.Length; i++)
                item.Outline[i] = vertices[i] = center + item.LocalVertices[i].Rotated(heading);
            item.Outline[^1] = vertices[0];

            var color = spec.Color.Lerp(new Color("#172431"), 0.32f);
            if (!block.Enabled || !mainComponent.Contains(block))
                color = color.Darkened(0.55f);

            var shadow = item.Shadow;
            for (var i = 0; i < vertices.Length; i++) shadow[i] = vertices[i] + new Vector2(2, 3);
            canvas.DrawColoredPolygon(shadow, new Color("#010306", 0.75f));
            canvas.DrawColoredPolygon(vertices, color);
            var inset = item.Inset;
            for (var i = 0; i < vertices.Length; i++) inset[i] = center + (vertices[i] - center) * 0.78f;
            canvas.DrawColoredPolygon(inset, color.Lightened(0.12f));
            canvas.DrawPolyline(item.Outline, new Color("#9bbac1", 0.68f), 1.2f);
            var insetOutline = item.InsetOutline;
            inset.CopyTo(insetOutline, 0);
            insetOutline[^1] = inset[0];
            canvas.DrawPolyline(insetOutline, new Color("#0b1218", 0.92f), 1.0f);

            var axis = Vector2.Right.Rotated(angle);
            var cross = axis.Rotated(Mathf.Pi * 0.5f);
            if (!spec.IsCore && !spec.IsThruster && !spec.IsWeapon)
            {
                canvas.DrawLine(center - axis * spec.Radius * 0.24f, center + axis * spec.Radius * 0.24f, new Color("#b0cbd0", 0.35f), 1.0f);
                canvas.DrawCircle(center + cross * spec.Radius * 0.22f, 1.4f, new Color("#b0cbd0", 0.7f));
            }

            // 核心高亮。
            if (spec.IsCore)
            {
                var radius = spec.Radius * 0.4f;
                var diamond = item.Diamond;
                diamond[0] = center + new Vector2(0, -radius).Rotated(heading);
                diamond[1] = center + new Vector2(radius, 0).Rotated(heading);
                diamond[2] = center + new Vector2(0, radius).Rotated(heading);
                diamond[3] = center + new Vector2(-radius, 0).Rotated(heading);
                diamond[4] = diamond[0];
                canvas.DrawPolyline(diamond, new Color("#eab66b"), 1.6f);
                canvas.DrawCircle(center, radius * 0.36f, new Color("#8ce8df", 0.12f));
            }
            else if (spec.IsThruster)
            {
                var activity = thrusterActivity is not null && thrusterActivity.TryGetValue(block.Id, out var a) ? a : 0.0f;
                DrawEngine(canvas, spec, center, angle, activity, item.Flame, item.FlameCore);
            }
            else if (spec.IsWeapon)
            {
                // 武器：炮口方向。
                var fireDir = spec.WeaponDirection.Rotated(angle);
                canvas.DrawLine(center, center + fireDir * spec.Radius, new Color("#eab66b", 0.82f), 2.0f);
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
        => DrawEngine(canvas, spec, center, angle, activity, null, null);

    private static void DrawEngine(CanvasItem canvas, ShipShapeLibrary.ShapeSpec spec,
        Vector2 center, float angle, float activity, Vector2[]? flame, Vector2[]? flameCore)
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
        flame ??= new Vector2[3];
        flameCore ??= new Vector2[3];
        flame[0] = At(mouthTop);
        flame[1] = At(nozzle + Vector2.Left * length);
        flame[2] = At(mouthBottom);
        canvas.DrawColoredPolygon(flame, new Color("#75ceac", 0.3f + activity * 0.35f));
        flameCore[0] = At(nozzle + Vector2.Up * width * 0.4f);
        flameCore[1] = At(nozzle + Vector2.Left * length * 0.65f);
        flameCore[2] = At(nozzle + Vector2.Down * width * 0.4f);
        canvas.DrawColoredPolygon(flameCore, new Color("#d7fff0", 0.8f));
    }
}
