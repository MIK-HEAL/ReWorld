using Godot;
using System.Reflection;
using System.Threading.Tasks;

/// <summary>
/// 深空背景视觉探针：带渲染窗口运行（不要 --headless），
/// 截取远景（最小缩放，验证星云包裹星系）与近景（默认缩放，验证渐变+星空）两张 PNG。
/// 输出：res://.godot/bg-far.png 与 res://.godot/bg-near.png，打印 BACKGROUND_PROBE_DONE。
/// </summary>
public partial class BackgroundProbe : Node
{
    private static void Set(UniverseView view, string field, object value)
        => typeof(UniverseView).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, value);
    private static float GetFloat(UniverseView view, string field)
        => (float)typeof(UniverseView).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;

    private async Task Frames(int count = 5)
    {
        for (var i = 0; i < count; i++)
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
    }

    private void Snapshot(string name)
    {
        var result = GetViewport().GetTexture().GetImage().SavePng($"res://.godot/bg-{name}.png");
        if (result != Error.Ok) GD.PushError($"BACKGROUND_PROBE: save {name} failed: {result}");
    }

    public override async void _Ready()
    {
        var ship = new ModularShip();
        ship.SeedReferenceShip();
        var view = new UniverseView { Name = "Universe" };
        view.SetShip(ship);
        AddChild(view);

        // 远景：缩放到最小（ZoomMin），整星系 + 外围星云云絮应弥漫画面。
        Set(view, "_cameraZoom", 0.008f);
        view.ResumeFlight();
        await Frames(20);
        Snapshot("far");

        // 近景：默认缩放（0.65），渐变底 + 彩色闪烁星空 + 亮星十字光芒。
        Set(view, "_cameraZoom", 0.65f);
        view.ResumeFlight();
        await Frames(20);
        Snapshot("near");

        GD.Print($"BACKGROUND_PROBE_DONE zoom_min_check={GetFloat(view, "_cameraZoom"):0.000}");
        GetTree().Quit();
    }
}
