using Godot;

/// <summary>
/// UniverseView 的 AI 注入点（GameCommandApi / AiBridge 专用）。
/// 三项驾驶注入全部为可空值类型或 bool：_PhysicsProcess 热路径只做字段检查，零堆分配。
/// </summary>
public partial class UniverseView
{
    /// <summary>AI 持续飞行控制（前进/平移/横移，语义随 ControlScheme）；null 时回落键盘输入。</summary>
    public ShipFlightController.Controls? AiControls;
    /// <summary>AI 世界坐标瞄准点：同时作为驾驶朝向（Cursor/Cardinal 方案）与开火瞄准；null 时回落鼠标光标。</summary>
    public Vector2? AiAimPoint;
    /// <summary>AI 手动开火开关：为真时武器向 AiAimPoint（或最近敌舰）持续射击。</summary>
    public bool AiFiring;
    /// <summary>AI 暂停：为真时本帧物理（交通/战斗/飞行/收益）整体挂起，直到 step 预算耗尽或恢复。</summary>
    public bool AiPaused;
    /// <summary>step 命令注入的暂停推进预算（秒）：暂停期间每物理帧扣减，耗尽即恢复挂起。</summary>
    public float AiStepBudget;
    /// <summary>当前玩家舰体（由 Main 从造舰编辑器传入；测试场景直接 SetShip）。</summary>
    public ModularShip? Ship => _ship;
    /// <summary>最近一次 HUD 提示文案（游戏内 Notify 的只读镜像）：行动命令的失败原因等。</summary>
    public string AiNotice => _notice;
}
