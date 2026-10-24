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
        public required ShroudGeometry[] Shrouds;
        public readonly Vector2[] Flame = new Vector2[3];
        public readonly Vector2[] FlameCore = new Vector2[3];
    }

    private sealed class ShroudGeometry
    {
        public readonly Vector2[] Vertices = new Vector2[4];
        public readonly Vector2[] Outline = new Vector2[5];
    }

    private sealed class GeometryCache
    {
        public ulong Revision;
        public bool Initialized;
        public Vector2 Center;
        public HashSet<ModularShip.Block> MainComponent = new();
        public BlockGeometry[] Blocks = Array.Empty<BlockGeometry>();

        public void Refresh(ModularShip ship)
        {
            Center = ship.CenterOfMass();
            MainComponent = new HashSet<ModularShip.Block>(ship.GetMainComponent());
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
                    Shrouds = Enumerable.Range(0, block.Spec.Shrouds.Count).Select(_ => new ShroudGeometry()).ToArray(),
                };
            }
            Revision = ship.Revision;
            Initialized = true;
        }
    }

    private static readonly ConditionalWeakTable<ModularShip, GeometryCache> Geometry = new();

    public static Color ResolveFillColor(ShipShapeLibrary.ShapeSpec spec, int variation = 0)
    {
        var elapsed = Time.GetTicksMsec() * 0.001f;
        var timeA = spec.NumId * 0.037f + variation * 0.11f;
        var blend = 0.5f + 0.5f * Mathf.Sin(0.5f * (elapsed + timeA));
        return spec.FillColor.Lerp(spec.FillColor1, blend);
    }

    public static void DrawIcon(CanvasItem canvas, ShipShapeLibrary.ShapeSpec spec, Vector2 center,
        float targetRadius, bool encrypted = false)
    {
        var scale = targetRadius / Mathf.Max(1, spec.Radius);
        var vertices = spec.Vertices().Select(v => center + v * scale).ToArray();
        var fill = ResolveFillColor(spec);
        var line = spec.LineColor;
        if (encrypted)
        {
            fill = fill.Lerp(new Color("#19242c"), 0.62f);
            line.A *= 0.68f;
        }
        canvas.DrawColoredPolygon(vertices, fill);
        canvas.DrawPolyline(vertices.Append(vertices[0]).ToArray(), line, 1.0f);
        if (encrypted)
        {
            // Reassembly's unavailable technology is still recognizable by its
            // silhouette. Use a restrained research overlay instead of a large
            // lock glyph: diagonal scan bands plus a small acquisition reticle.
            var bounds = vertices.Aggregate(new Rect2(vertices[0], Vector2.Zero),
                (rect, point) => rect.Expand(point));
            var hatch = new Color("#8ab7c8", 0.32f);
            var step = Mathf.Max(3.0f, targetRadius * 0.34f);
            for (var y = bounds.Position.Y - bounds.Size.X; y < bounds.End.Y + bounds.Size.X; y += step)
            {
                var start = new Vector2(bounds.Position.X, y);
                var end = new Vector2(bounds.End.X, y + bounds.Size.X);
                canvas.DrawLine(start, end, hatch, Mathf.Max(0.6f, targetRadius * 0.045f));
            }
            var reticle = new Color("#b4d9df", 0.62f);
            var arm = Mathf.Max(2.0f, targetRadius * 0.22f);
            var gap = Mathf.Max(1.0f, targetRadius * 0.12f);
            canvas.DrawLine(center + new Vector2(-arm, 0), center + new Vector2(-gap, 0), reticle, 0.8f);
            canvas.DrawLine(center + new Vector2(gap, 0), center + new Vector2(arm, 0), reticle, 0.8f);
            canvas.DrawLine(center + new Vector2(0, -arm), center + new Vector2(0, -gap), reticle, 0.8f);
            canvas.DrawLine(center + new Vector2(0, gap), center + new Vector2(0, arm), reticle, 0.8f);
        }
        else if (spec.Shrouds.Count > 0) DrawShrouds(canvas, spec, center, 0, scale);
    }

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

        // Reassembly renders the block silhouette with its two animated fill colors
        // and one faction line color. It does not inset or panelize the polygon.
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

            var color = ResolveFillColor(spec, block.Id);
            color = color.Lerp(new Color("#5e292b"), 0.65f * (1 - Mathf.Clamp(block.Health / spec.Hull, 0, 1)));
            if (!block.Enabled || !mainComponent.Contains(block))
                color = color.Darkened(0.55f);

            canvas.DrawColoredPolygon(vertices, color);
            var lineColor = spec.LineColor;
            canvas.DrawPolyline(item.Outline, lineColor, 1.0f);

            if (spec.HasNativeThruster)
            {
                var activity = thrusterActivity is not null && thrusterActivity.TryGetValue(block.Id, out var a) ? a : 0.0f;
                DrawEngine(canvas, spec, center, angle, activity, item.Flame, item.FlameCore);
            }
            else if (spec.IsWeapon || spec.Shrouds.Count > 0)
                DrawShrouds(canvas, spec, center, angle, 1, item.Shrouds);

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

    public static void DrawWeapon(CanvasItem canvas, ShipShapeLibrary.ShapeSpec spec, Vector2 center,
        float angle, float width = 2.0f)
    {
        DrawShrouds(canvas, spec, center, angle);
    }

    private static void DrawEngine(CanvasItem canvas, ShipShapeLibrary.ShapeSpec spec,
        Vector2 center, float angle, float activity, Vector2[]? flame, Vector2[]? flameCore)
    {
        Vector2 At(Vector2 point) => center + point.Rotated(angle);
        if (activity <= 0.01f) return;
        var nozzle = spec.ExhaustPosition;
        var width = spec.ExhaustHalfWidth;
        var mouthTop = nozzle + Vector2.Up * width;
        var mouthBottom = nozzle + Vector2.Down * width;
        var length = spec.Radius * (0.55f + 1.5f * activity);
        flame ??= new Vector2[3];
        flameCore ??= new Vector2[3];
        flame[0] = At(mouthTop);
        flame[1] = At(nozzle + Vector2.Left * length);
        flame[2] = At(mouthBottom);
        var flameColor = spec.ThrusterColor.Lerp(spec.ThrusterColor1, Mathf.Clamp(activity, 0, 1));
        flameColor.A *= Mathf.Clamp(activity, 0, 1);
        canvas.DrawColoredPolygon(flame, flameColor);

        // 白色烟雾粒子轨迹（对齐 Reassembly 排气尾迹：白烟小圆点沿排气方向扩散淡出）。
        var exhaust = Vector2.Left.Rotated(angle);
        var side = exhaust.Rotated(Mathf.Pi * 0.5f);
        var smoke = spec.ThrusterColor.Lerp(Colors.White, 0.75f);
        var time = Time.GetTicksMsec() * 0.001f;
        var particleCount = 10 + Mathf.RoundToInt(Mathf.Clamp(activity, 0, 1) * 14);
        var trailLength = spec.Radius * (1.6f + 2.4f * activity);
        for (var i = 0; i < particleCount; i++)
        {
            // 每个粒子按自身相位 0→1 走完轨迹；时间偏移错开形成连续烟带。
            var phase = Mathf.PosMod(time * (0.55f + activity * 0.9f) + i * 0.618f + spec.NumId * 0.037f, 1.0f);
            var wobble = Mathf.Sin(time * 3.1f + i * 2.17f + spec.NumId) * width * (0.25f + phase * 0.9f);
            var point = center + nozzle.Rotated(angle) + exhaust * (trailLength * phase) + side * wobble;
            // 烟雾随存续膨胀、淡出，但前半段保持较高不透明度以保证可见。
            var fade = (1.0f - phase) * (1.0f - phase) * Mathf.Clamp(activity * 1.6f, 0, 1);
            var color = smoke;
            color.A *= fade;
            var radius = width * (0.10f + phase * 0.16f);
            canvas.DrawCircle(point, Mathf.Max(1.2f, radius), color);
        }
    }

    private static void DrawShrouds(CanvasItem canvas, ShipShapeLibrary.ShapeSpec spec, Vector2 center, float angle,
        float geometryScale = 1, ShroudGeometry[]? buffers = null)
    {
        if (spec.Shrouds.Count == 0) return;
        var direction = spec.WeaponDirection.Rotated(angle);
        var side = direction.Rotated(Mathf.Pi * 0.5f);
        var originOffset = spec.BarrelOffset * ShipShapeLibrary.DisplayScale * geometryScale;
        for (var i = 0; i < spec.Shrouds.Count; i++)
        {
            var shroud = spec.Shrouds[i];
            var origin = center + (originOffset + new Vector2(shroud.Offset.X, shroud.Offset.Y) * ShipShapeLibrary.DisplayScale * geometryScale).Rotated(angle);
            var length = Mathf.Max(2, shroud.Size.X * ShipShapeLibrary.DisplayScale * geometryScale);
            var half = Mathf.Max(1, shroud.Size.Y * ShipShapeLibrary.DisplayScale * geometryScale * 0.5f);
            var taper = Mathf.Clamp(shroud.Taper, -0.7f, 0.7f);
            var near = half * (1.0f - taper);
            var far = half * (1.0f + taper);
            var polygon = buffers is not null && i < buffers.Length ? buffers[i].Vertices : new Vector2[4];
            polygon[0] = origin - side * near;
            polygon[1] = origin + side * near;
            polygon[2] = origin + direction * length + side * far;
            polygon[3] = origin + direction * length - side * far;
            var phase = shroud.TriColorId == 1 ? 1.0f : 0.0f;
            var otherPhase = shroud.TriColor1Id == 1 ? 1.0f : 0.0f;
            var blend = 0.5f + 0.5f * Mathf.Sin(0.5f * (Time.GetTicksMsec() * 0.001f + spec.NumId * 0.037f));
            var fill = spec.FillColor.Lerp(spec.FillColor1, Mathf.Lerp(phase, otherPhase, blend));
            canvas.DrawColoredPolygon(polygon, fill);
            var lineColor = spec.LineColor;
            lineColor.A = Mathf.Max(lineColor.A, 0.35f);
            var outline = buffers is not null && i < buffers.Length ? buffers[i].Outline : new Vector2[5];
            Array.Copy(polygon, outline, polygon.Length);
            outline[^1] = polygon[0];
            canvas.DrawPolyline(outline, lineColor, 1.0f);
        }
    }
}
