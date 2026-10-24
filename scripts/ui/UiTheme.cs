using Godot;

/// <summary>
/// Game HUD tokens: light engineering space, dark edge-mounted tools and amber
/// selection brackets. Geometry is drawn in code; the world remains the focus.
/// </summary>
public static class UiTheme
{
    // ---- Palette -------------------------------------------------------
    public static readonly Color BackgroundDeep = new("#071019");
    public static readonly Color Background = new("#0b151f");
    public static readonly Color Surface = new("#101f2a");
    public static readonly Color SurfaceRaised = new("#172c39");
    public static readonly Color SurfaceHover = new("#214151");
    public static readonly Color Border = new("#2d5263");
    public static readonly Color BorderHover = new("#6bb7cf");
    public static readonly Color TextPrimary = new("#e7f2f4");
    public static readonly Color TextSecondary = new("#a9c4cf");
    public static readonly Color TextMuted = new("#6e8d9c");
    public static readonly Color Accent = new("#e2a64e");
    public static readonly Color AccentDeep = new("#a86b2b");
    public static readonly Color AccentSoft = new("#294655");
    public static readonly Color Header = new("#0b1721");
    public static readonly Color CanvasInk = new("#d9e9ed");
    public static readonly Color OnDark = new("#f0f7f5");
    public static readonly Color OnDarkMuted = new("#a6c6c7");
    public static readonly Color Success = new("#6dc7af");
    public static readonly Color Danger = new("#d94f4f");
    public static readonly Color Warning = new("#d99a2b");
    public static readonly Color Info = new("#3f9fd8");
    public static readonly Color Purple = new("#8a6cc4");

    // ---- Primitives ----------------------------------------------------
    public static StyleBoxFlat Box(
        Color fill,
        Color border,
        int borderWidth = 1,
        int radius = 0,
        int shadowSize = 0,
        float marginX = 10.0f,
        float marginY = 6.0f)
    {
        return new StyleBoxFlat
        {
            BgColor = fill,
            BorderColor = border,
            BorderWidthLeft = borderWidth,
            BorderWidthTop = borderWidth,
            BorderWidthRight = borderWidth,
            BorderWidthBottom = borderWidth,
            CornerRadiusTopLeft = radius,
            CornerRadiusTopRight = radius,
            CornerRadiusBottomLeft = radius,
            CornerRadiusBottomRight = radius,
            ContentMarginLeft = marginX,
            ContentMarginRight = marginX,
            ContentMarginTop = marginY,
            ContentMarginBottom = marginY,
            ShadowColor = new Color(0.0f, 0.0f, 0.0f, 0.0f),
            ShadowSize = 0,
            AntiAliasing = true,
        };
    }

    private static StyleBoxFlat PanelBox(Color fill) => Box(fill, Border, 1, 0, 0);

    public static void Panel(Control control, Color? fill = null)
    {
        control.AddThemeStyleboxOverride("panel", PanelBox(fill ?? Surface));
        control.MouseFilter = Control.MouseFilterEnum.Ignore;
    }

    /// <summary>Small flat colour tab used to mark a title or section, Reassembly style.</summary>
    public static Panel AccentBar(Control parent, Vector2 position, Vector2 size, Color color, int radius = 0)
    {
        var bar = new Panel { Position = position, Size = size };
        bar.AddThemeStyleboxOverride("panel", Box(color, color, 0, radius));
        bar.MouseFilter = Control.MouseFilterEnum.Ignore;
        parent.AddChild(bar);
        return bar;
    }

    public static void Label(Label label, int size = 12, Color? color = null)
    {
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color ?? TextPrimary);
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
    }

    public static void Button(Button button, Color? accent = null)
    {
        var color = accent ?? Accent;
        const int radius = 0;
        button.FocusMode = Control.FocusModeEnum.None;
        button.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        button.AddThemeFontSizeOverride("font_size", 14);
        button.AddThemeColorOverride("font_color", TextPrimary);
        button.AddThemeColorOverride("font_hover_color", TextPrimary);
        button.AddThemeColorOverride("font_pressed_color", TextPrimary);
        button.AddThemeColorOverride("font_disabled_color", TextMuted);
        button.AddThemeColorOverride("font_focus_color", TextPrimary);
        button.AddThemeStyleboxOverride("normal", Box(Surface, Border, 1, radius));
        button.AddThemeStyleboxOverride("hover", Box(SurfaceHover, color, 1, radius));
        button.AddThemeStyleboxOverride("pressed", Box(AccentSoft, color, 2, radius));
        button.AddThemeStyleboxOverride("hover_pressed", Box(AccentSoft, color, 2, radius));
        button.AddThemeStyleboxOverride("disabled", Box(SurfaceRaised, Border, 1, radius));
        button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
    }

    public static void PrimaryButton(Button button)
    {
        Button(button);
        button.AddThemeStyleboxOverride("normal", Box(Accent, Accent, 0, 0));
        button.AddThemeStyleboxOverride("hover", Box(new Color("#efac5a"), AccentDeep, 1, 0));
        button.AddThemeStyleboxOverride("pressed", Box(new Color("#d68a32"), AccentDeep, 1, 0));
        foreach (var state in new[] { "font_color", "font_hover_color", "font_pressed_color" })
            button.AddThemeColorOverride(state, CanvasInk);
    }

    public static HudMeter Progress(Control parent, Vector2 position, Color fill, float width = 272.0f, int height = 8)
    {
        var bar = new HudMeter
        {
            Position = position,
            Size = new Vector2(width, height),
            MinValue = 0,
            MaxValue = 100,
            FillColor = fill,
        };
        parent.AddChild(bar);
        return bar;
    }
}
