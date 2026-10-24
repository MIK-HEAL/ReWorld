using Godot;

/// <summary>Resolution-independent silhouettes for the module library.</summary>
public partial class ModuleGlyph : Control
{
    public string Kind { get; set; } = "core";
    public override void _Draw()
    {
        var definition = ModuleGraph.Definitions[Kind];
        var color = new Color(definition.ColorHex);
        var center = Size * 0.5f;
        var footprint = new Vector2(definition.Size.X, definition.Size.Y);
        var unit = Mathf.Min((Size.X - 6) / footprint.X, (Size.Y - 6) / footprint.Y);
        var rect = new Rect2(center - footprint * unit * 0.5f, footprint * unit);
        if (Kind == "shield")
        {
            DrawCircle(center, unit * 0.48f, color);
            DrawArc(center, unit * 0.3f, 0, Mathf.Tau, 32, UiTheme.Header, 1.5f, true);
            return;
        }
        if (Kind == "thruster")
        {
            DrawColoredPolygon(new[] { new Vector2(rect.End.X, center.Y), rect.Position, new Vector2(rect.Position.X, rect.End.Y) }, color);
            DrawLine(new Vector2(rect.Position.X + 4, center.Y), center, UiTheme.Header, 2, true);
            return;
        }
        DrawStyleBox(UiTheme.Box(color, color.Darkened(0.18f), 1, 2), rect);
        switch (Kind)
        {
            case "solar":
            case "radiator":
                for (var i = 1; i < 4; i++)
                    DrawLine(new Vector2(rect.Position.X + rect.Size.X * i / 4, rect.Position.Y + 2), new Vector2(rect.Position.X + rect.Size.X * i / 4, rect.End.Y - 2), color.Darkened(0.35f), 1);
                break;
            case "laser":
            case "cannon":
            case "beam":
            case "railgun":
            case "missile":
            case "point_defense":
            case "miner":
                DrawLine(center - new Vector2(6, 0), new Vector2(rect.End.X + 2, center.Y), UiTheme.Header, 3, true);
                break;
            case "cargo":
            case "hull":
            case "armor":
                DrawLine(rect.Position + Vector2.One * 4, rect.End - Vector2.One * 4, color.Darkened(0.35f), 1, true);
                DrawLine(new Vector2(rect.End.X - 4, rect.Position.Y + 4), new Vector2(rect.Position.X + 4, rect.End.Y - 4), color.Darkened(0.35f), 1, true);
                break;
            case "generator":
            case "reactor":
            case "capacitor":
                DrawCircle(center, Mathf.Min(rect.Size.X, rect.Size.Y) * 0.24f, UiTheme.Header);
                DrawCircle(center, Mathf.Min(rect.Size.X, rect.Size.Y) * 0.38f, UiTheme.Header, false, 1.0f);
                break;
            case "sensor":
                DrawArc(center, Mathf.Min(rect.Size.X, rect.Size.Y) * 0.36f, -Mathf.Pi * 0.75f, Mathf.Pi * 0.75f, 14, UiTheme.Header, 2.0f);
                DrawLine(center, center + Vector2.Right * rect.Size.X * 0.42f, UiTheme.Header, 2.0f);
                break;
            case "gyro":
                DrawCircle(center, Mathf.Min(rect.Size.X, rect.Size.Y) * 0.3f, UiTheme.Header, false, 2.0f);
                DrawLine(center - Vector2.Up * 7, center + Vector2.Up * 7, UiTheme.Header, 2.0f);
                break;
            default:
                DrawCircle(center, Mathf.Min(rect.Size.X, rect.Size.Y) * 0.22f, color.Darkened(0.4f));
                break;
        }
    }
}
