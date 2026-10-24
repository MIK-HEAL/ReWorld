using Godot;

/// <summary>
/// Shared visual tokens for the Control based UI. Keeping the palette and widget
/// construction here prevents individual screens from inventing their own styles.
/// </summary>
public static class UiTheme
{
    public static readonly Color Background = new("#10161f");
    public static readonly Color BackgroundDeep = new("#0b1018");
    public static readonly Color Surface = new("#18202b");
    public static readonly Color SurfaceHover = new("#1e2836");
    public static readonly Color Border = new("#2c3846");
    public static readonly Color BorderHover = new("#3d4c5e");
    public static readonly Color TextPrimary = new("#e8eef4");
    public static readonly Color TextSecondary = new("#9db0c3");
    public static readonly Color TextMuted = new("#64758a");
    public static readonly Color Accent = new("#5ecf9d");
    public static readonly Color Danger = new("#e0697f");
    public static readonly Color Warning = new("#d9a850");
    public static readonly Color Info = new("#6aa8d8");

    public static StyleBoxFlat Box(Color fill, Color border, int borderWidth = 1)
    {
        return new StyleBoxFlat
        {
            BgColor = fill,
            BorderColor = border,
            BorderWidthLeft = borderWidth,
            BorderWidthTop = borderWidth,
            BorderWidthRight = borderWidth,
            BorderWidthBottom = borderWidth,
            CornerRadiusTopLeft = 0,
            CornerRadiusTopRight = 0,
            CornerRadiusBottomLeft = 0,
            CornerRadiusBottomRight = 0,
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 6,
            ContentMarginBottom = 6,
        };
    }

    public static void Panel(Control control, Color? fill = null)
    {
        control.AddThemeStyleboxOverride("panel", Box(fill ?? Surface, Border));
        control.MouseFilter = Control.MouseFilterEnum.Ignore;
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
        button.AddThemeFontSizeOverride("font_size", 11);
        button.AddThemeColorOverride("font_color", TextPrimary);
        button.AddThemeColorOverride("font_hover_color", TextPrimary);
        button.AddThemeColorOverride("font_pressed_color", BackgroundDeep);
        button.AddThemeColorOverride("font_focus_color", TextPrimary);
        button.AddThemeStyleboxOverride("normal", Box(Surface, Border));
        button.AddThemeStyleboxOverride("hover", Box(SurfaceHover, color));
        button.AddThemeStyleboxOverride("pressed", Box(color, color));
        button.AddThemeStyleboxOverride("focus", Box(SurfaceHover, color));
    }
}
