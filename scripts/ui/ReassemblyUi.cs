using Godot;
using System;

/// <summary>
/// Reassembly 标志性工业矢量风格 UI 自绘核心辅助工具集。
/// 提供切角线框、刻度角标、分段能量条、极坐标雷达盘、航向矢量准星等高品质矢量原语。
/// </summary>
public static class ReassemblyUi
{
    public static readonly Color VoidDark = new("#182532e6");
    public static readonly Color VoidBlack = new("#101b24");
    public static readonly Color PanelBg = new("#1c2d3cd9");
    public static readonly Color PanelBorder = new("#3d627a");
    public static readonly Color PanelBorderHighlight = new("#6cb5d9");

    public static readonly Color TerranBlue = new("#1b4b8a");
    public static readonly Color TerranSteel = new("#aaaaaa");
    public static readonly Color PointCyan = new("#48d6c8");
    public static readonly Color CreditGold = new("#f8bc04");
    public static readonly Color ResourcePink = new("#d252a5");
    public static readonly Color PhosphorGreen = new("#79c842");
    public static readonly Color TacticalAmber = new("#e1a71c");
    public static readonly Color DangerRed = new("#e84242");

    public static readonly Color CyanHighlight = PointCyan;
    public static readonly Color CyanMuted = new("#5894aa");
    public static readonly Color CyanGrid = new("#254556");

    public static readonly Color TextWhite = new("#f0f5f8");
    public static readonly Color TextSteel = new("#c2d5e0");
    public static readonly Color TextMuted = new("#829cad");

    /// <summary>
    /// 绘制 45° 精密切角线框面板（支持背景填充、高亮边界、四角切角对齐刻度标）。
    /// </summary>
    public static void DrawChamferFrame(
        CanvasItem item,
        Rect2 rect,
        Color bg,
        Color border,
        float chamfer = 8.0f,
        bool cornerBrackets = true,
        float borderWidth = 1.0f)
    {
        var x0 = rect.Position.X;
        var y0 = rect.Position.Y;
        var x1 = rect.End.X;
        var y1 = rect.End.Y;

        var c = Mathf.Min(chamfer, Mathf.Min(rect.Size.X, rect.Size.Y) * 0.4f);

        // 8边形切角多边形点集 (顺时针，左上开始)
        var poly = new Vector2[]
        {
            new(x0 + c, y0),
            new(x1 - c, y0),
            new(x1, y0 + c),
            new(x1, y1 - c),
            new(x1 - c, y1),
            new(x0 + c, y1),
            new(x0, y1 - c),
            new(x0, y0 + c)
        };

        if (bg.A > 0.001f)
        {
            item.DrawColoredPolygon(poly, bg);
        }

        // 外框闭合多边形
        var loop = new Vector2[poly.Length + 1];
        Array.Copy(poly, loop, poly.Length);
        loop[^1] = poly[0];
        item.DrawPolyline(loop, border, borderWidth);

        // 绘制高科技四角微切对齐标 (Corner Brackets)
        if (cornerBrackets && rect.Size.X > 24 && rect.Size.Y > 24)
        {
            var bracketColor = border.Lerp(CyanHighlight, 0.5f);
            var bLen = Mathf.Min(6.0f, c * 0.8f);

            // 左上角切角高亮刻度
            item.DrawLine(new Vector2(x0 + c, y0), new Vector2(x0 + c + bLen, y0), bracketColor, borderWidth + 1.0f);
            item.DrawLine(new Vector2(x0, y0 + c), new Vector2(x0, y0 + c + bLen), bracketColor, borderWidth + 1.0f);

            // 右上角切角高亮刻度
            item.DrawLine(new Vector2(x1 - c, y0), new Vector2(x1 - c - bLen, y0), bracketColor, borderWidth + 1.0f);
            item.DrawLine(new Vector2(x1, y0 + c), new Vector2(x1, y0 + c + bLen), bracketColor, borderWidth + 1.0f);

            // 右下角
            item.DrawLine(new Vector2(x1 - c, y1), new Vector2(x1 - c - bLen, y1), bracketColor, borderWidth + 1.0f);
            item.DrawLine(new Vector2(x1, y1 - c), new Vector2(x1, y1 - c - bLen), bracketColor, borderWidth + 1.0f);

            // 左下角
            item.DrawLine(new Vector2(x0 + c, y1), new Vector2(x0 + c + bLen, y1), bracketColor, borderWidth + 1.0f);
            item.DrawLine(new Vector2(x0, y1 - c), new Vector2(x0, y1 - c - bLen), bracketColor, borderWidth + 1.0f);
        }
    }

    /// <summary>
    /// 绘制 Reassembly 经典分段能量/状态槽 (Segmented Meter)。
    /// </summary>
    public static void DrawSegmentedMeter(
        CanvasItem item,
        Vector2 position,
        Vector2 size,
        float ratio,
        Color activeColor,
        Color emptyColor,
        int segments = 10,
        float gap = 2.0f)
    {
        ratio = Mathf.Clamp(ratio, 0.0f, 1.0f);
        if (segments <= 0) segments = 10;
        var totalGap = gap * (segments - 1);
        var segWidth = Mathf.Max(1.0f, (size.X - totalGap) / segments);
        var activeCount = Mathf.RoundToInt(ratio * segments);

        for (var i = 0; i < segments; i++)
        {
            var x = position.X + i * (segWidth + gap);
            var rect = new Rect2(x, position.Y, segWidth, size.Y);
            var filled = i < activeCount;
            var col = filled ? activeColor : emptyColor;
            item.DrawRect(rect, col);
            if (filled)
            {
                // 顶部微亮高光线
                item.DrawLine(new Vector2(x, position.Y), new Vector2(x + segWidth, position.Y), activeColor.Lerp(Colors.White, 0.4f), 1.0f);
            }
        }
    }

    /// <summary>
    /// 绘制 Reassembly 标志性圆形极坐标雷达盘（含同心环、动态扫描光带与十字方位基准）。
    /// </summary>
    public static void DrawRadarDisk(
        CanvasItem item,
        Vector2 center,
        float radius,
        float sweepAngle,
        Color ringColor,
        Color sweepColor)
    {
        // 1. 背景暗盘
        item.DrawCircle(center, radius, new Color(0.01f, 0.04f, 0.08f, 0.88f));

        // 2. 3 层等距同心参考圆环
        for (var i = 1; i <= 3; i++)
        {
            var r = radius * (i / 3.0f);
            var alpha = i == 3 ? 0.8f : 0.4f;
            item.DrawArc(center, r, 0, Mathf.Tau, 48, ringColor * new Color(1, 1, 1, alpha), 1.0f);
        }

        // 3. 十字方位细线与四方刻度
        var axisColor = ringColor * new Color(1, 1, 1, 0.35f);
        item.DrawLine(center - new Vector2(radius, 0), center + new Vector2(radius, 0), axisColor, 1.0f);
        item.DrawLine(center - new Vector2(0, radius), center + new Vector2(0, radius), axisColor, 1.0f);

        // 4. 雷达动态扫描光线与余晖
        var sweepDir = Vector2.Right.Rotated(sweepAngle);
        item.DrawLine(center, center + sweepDir * radius, sweepColor, 1.5f);

        // 扫描扇形余晖 (向前展开微弱余晖线)
        for (var trail = 1; trail <= 6; trail++)
        {
            var trailAngle = sweepAngle - trail * 0.06f;
            var trailAlpha = (1.0f - trail / 7.0f) * 0.25f;
            var trailDir = Vector2.Right.Rotated(trailAngle);
            item.DrawLine(center, center + trailDir * radius, sweepColor * new Color(1, 1, 1, trailAlpha), 1.0f);
        }

        // 5. 外圈切角微标刻度
        for (var deg = 0; deg < 360; deg += 30)
        {
            var rad = Mathf.DegToRad(deg);
            var outer = center + Vector2.Right.Rotated(rad) * radius;
            var inner = center + Vector2.Right.Rotated(rad) * (radius - ((deg % 90 == 0) ? 6.0f : 3.0f));
            item.DrawLine(inner, outer, ringColor * 0.7f, 1.0f);
        }
    }

    /// <summary>
    /// 绘制平视动态航向矢量标（顺飞 +、逆飞 x）与目标追踪菱形框。
    /// </summary>
    public static void DrawFlightReticle(
        CanvasItem item,
        Vector2 screenCenter,
        Vector2 velocity,
        float heading,
        Vector2? targetScreenPos,
        string targetLabel,
        float targetDistance,
        Font font)
    {
        var speed = velocity.Length();

        // 1. 顺飞 (Prograde) 矢量准星
        if (speed > 8.0f)
        {
            var progradeDir = velocity.Normalized();
            // 在屏幕中心沿速度方向投影一定偏移 (最大 80px)
            var progradeOffset = progradeDir * Mathf.Min(80.0f, speed * 0.25f);
            var progradePos = screenCenter + progradeOffset;

            // 顺飞圆形十字标 (Reassembly 风格)
            item.DrawArc(progradePos, 6.0f, 0, Mathf.Tau, 16, CyanHighlight * 0.75f, 1.2f);
            item.DrawLine(progradePos - new Vector2(9, 0), progradePos + new Vector2(9, 0), CyanHighlight * 0.85f, 1.0f);
            item.DrawLine(progradePos - new Vector2(0, 9), progradePos + new Vector2(0, 9), CyanHighlight * 0.85f, 1.0f);

            // 逆飞 (Retrograde) 准星
            var retrogradePos = screenCenter - progradeOffset;
            item.DrawArc(retrogradePos, 6.0f, 0, Mathf.Tau, 16, TacticalAmber * 0.65f, 1.0f);
            item.DrawLine(retrogradePos - new Vector2(5, 5), retrogradePos + new Vector2(5, 5), TacticalAmber * 0.75f, 1.0f);
            item.DrawLine(retrogradePos - new Vector2(-5, 5), retrogradePos + new Vector2(-5, 5), TacticalAmber * 0.75f, 1.0f);
        }

        // 2. 目标平视追踪框
        if (targetScreenPos.HasValue)
        {
            var tPos = targetScreenPos.Value;
            var bracketSize = 16.0f;
            var boxCol = CyanHighlight;

            // 菱形框
            var diamond = new Vector2[]
            {
                new(tPos.X, tPos.Y - bracketSize),
                new(tPos.X + bracketSize, tPos.Y),
                new(tPos.X, tPos.Y + bracketSize),
                new(tPos.X - bracketSize, tPos.Y),
                new(tPos.X, tPos.Y - bracketSize)
            };
            item.DrawPolyline(diamond, boxCol * 0.7f, 1.2f);

            // 目标信息悬浮标牌
            if (!string.IsNullOrEmpty(targetLabel) && font is not null)
            {
                var labelText = $"{targetLabel.ToUpperInvariant()} // {targetDistance:0}M";
                item.DrawString(font, tPos + new Vector2(bracketSize + 6, 4), labelText, HorizontalAlignment.Left, -1, 10, CyanHighlight);
            }
        }
    }
}
