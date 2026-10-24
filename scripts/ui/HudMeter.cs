using Godot;

/// <summary>A thin HUD meter without the text-driven minimum height of ProgressBar.</summary>
public partial class HudMeter : Godot.Range
{
    public Color FillColor { get; set; } = UiTheme.Accent;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        ValueChanged += _ => QueueRedraw();
        Resized += QueueRedraw;
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color("#405462"));
        var fraction = MaxValue <= MinValue ? 0 : Mathf.Clamp((float)((Value - MinValue) / (MaxValue - MinValue)), 0, 1);
        DrawRect(new Rect2(Vector2.Zero, new Vector2(Size.X * fraction, Size.Y)), FillColor);
    }
}
