using Godot;
using System.Threading.Tasks;

/// <summary>
/// MCP 端到端联调用的常驻场景：构建一个可游玩的宇宙（舰船 + UniverseView + AiBridge，
/// 默认端口 9817），保持运行供 stdio MCP 客户端探测，60 秒后自动退出。
/// </summary>
public partial class AiBridgeHold : Node
{
    public override async void _Ready()
    {
        var ship = new ModularShip();
        ship.SeedReferenceShip();
        var view = new UniverseView();
        view.SetShip(ship);
        AddChild(view);
        AddChild(new AiBridge(view));
        await Task.Delay(60_000);
        GetTree().Quit();
    }
}
