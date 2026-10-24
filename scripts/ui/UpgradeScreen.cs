using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>Persistent Reassembly-style block decryption screen, separate from ship construction.</summary>
public partial class UpgradeScreen : Node2D
{
    private const float ViewWidth = 1280;
    private const float ViewHeight = 720;
    private const int PageSize = 12;
    private const float PanelX = 52;
    private const float PanelY = 28;
    private const float PanelWidth = 1176;
    private const float PanelHeight = 664;
    private const float ListX = 72;
    private const float ListY = 140;
    private const float CardWidth = 360;
    private const float CardHeight = 70;
    private const float ColumnGap = 12;
    private const float RowGap = 6;
    private const float DetailX = 824;

    private Font _font = null!;
    private string _category = "全部";
    private string _selectedId = "";
    private int _page;
    private string _notice = "选择一个模块以查看规格";
    private readonly List<(Rect2 Rect, string Id)> _moduleButtons = new();
    private readonly List<(Rect2 Rect, string Category)> _categoryButtons = new();
    private Rect2 _purchaseButton;
    private Rect2 _previousButton;
    private Rect2 _nextButton;
    private Rect2 _closeButton;

    public PlayerProgress Progress { get; set; } = new();
    public event Action? CloseRequested;

    private string[] Categories => new[] { "全部" }.Concat(ShipShapeLibrary.BuildOrder.Select(ShipShapeLibrary.Get).Select(s => s.Category)
        .Distinct().OrderBy(CategorySort).ThenBy(x => x, StringComparer.Ordinal)).ToArray();
    private string[] FilteredModules() => ShipShapeLibrary.BuildOrder
        .Where(id => _category == "全部" || ShipShapeLibrary.Get(id).Category == _category).ToArray();
    private ShipShapeLibrary.ShapeSpec Selected => ShipShapeLibrary.Shapes.TryGetValue(_selectedId, out var spec)
        ? spec : ShipShapeLibrary.Get(ShipShapeLibrary.BuildOrder[0]);

    public override void _Ready()
    {
        _font = new SystemFont
        {
            FontNames = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Noto Sans CJK SC" },
            AllowSystemFallback = true,
        };
        if (string.IsNullOrEmpty(_selectedId)) _selectedId = ShipShapeLibrary.BuildOrder.FirstOrDefault() ?? "core";
    }

    public override void _Process(double delta) => QueueRedraw();

    public bool TryPurchase(string id)
    {
        if (!ShipShapeLibrary.BuildOrder.Contains(id)) return false;
        var spec = ShipShapeLibrary.Get(id);
        if (!spec.IsRegistered)
        {
            _notice = "尚无完整原版规格，暂不可解密";
            return false;
        }
        if (Progress.CanBuild(id))
        {
            _notice = "该模块已经解密";
            return false;
        }
        var cost = PlayerProgress.UnlockCost(spec);
        if (Progress.Resources < cost)
        {
            _notice = $"资源不足，还差 {cost - Progress.Resources} R";
            return false;
        }
        if (!Progress.TryUnlock(id)) return false;
        _notice = $"已解密：{spec.Label}";
        return true;
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click)
            return;
        if (_closeButton.HasPoint(click.Position))
            CloseRequested?.Invoke();
        else if (_purchaseButton.HasPoint(click.Position))
            TryPurchase(_selectedId);
        else if (_previousButton.HasPoint(click.Position))
            _page = Math.Max(0, _page - 1);
        else if (_nextButton.HasPoint(click.Position))
            _page = Math.Min(MaxPage(), _page + 1);
        else
        {
            foreach (var (rect, category) in _categoryButtons)
                if (rect.HasPoint(click.Position))
                {
                    _category = category;
                    _page = 0;
                    GetViewport().SetInputAsHandled();
                    return;
                }
            foreach (var (rect, id) in _moduleButtons)
                if (rect.HasPoint(click.Position))
                {
                    _selectedId = id;
                    _notice = Progress.CanBuild(id) ? "该模块已经解密" : "模块规格待解密";
                    GetViewport().SetInputAsHandled();
                    return;
                }
            if (!new Rect2(PanelX, PanelY, PanelWidth, PanelHeight).HasPoint(click.Position))
                return;
        }
        GetViewport().SetInputAsHandled();
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, new Vector2(ViewWidth, ViewHeight)), new Color("#050b12", 0.82f));
        ReassemblyUi.DrawChamferFrame(this, new Rect2(PanelX, PanelY, PanelWidth, PanelHeight),
            ReassemblyUi.VoidDark, ReassemblyUi.CyanMuted, 12, true, 1.4f);
        DrawHeader();
        DrawCategories();
        DrawModuleList();
        DrawSelectedModule();
    }

    private void DrawHeader()
    {
        DrawString(_font, new Vector2(PanelX + 24, PanelY + 34), "查看升级  /  科技数据库",
            fontSize: 17, modulate: ReassemblyUi.TextWhite);
        DrawString(_font, new Vector2(PanelX + 24, PanelY + 57), "解密模块规格并永久加入造舰元件板",
            fontSize: 12, modulate: ReassemblyUi.TextMuted);
        DrawString(_font, new Vector2(PanelX + PanelWidth - 210, PanelY + 38), $"R  {Progress.Resources:N0}",
            fontSize: 17, modulate: ReassemblyUi.TacticalAmber);
        _closeButton = new Rect2(PanelX + PanelWidth - 48, PanelY + 12, 30, 30);
        ReassemblyUi.DrawChamferFrame(this, _closeButton, new Color("#0b1821"), ReassemblyUi.CyanMuted, 4, false, 1);
        DrawString(_font, _closeButton.Position + new Vector2(9, 21), "×", fontSize: 18, modulate: ReassemblyUi.TextSteel);
        DrawLine(new Vector2(PanelX + 20, PanelY + 70), new Vector2(PanelX + PanelWidth - 20, PanelY + 70),
            ReassemblyUi.CyanGrid, 1);
    }

    private void DrawCategories()
    {
        _categoryButtons.Clear();
        var x = ListX;
        var y = PanelY + 88;
        foreach (var category in Categories)
        {
            var width = 63.0f;
            var rect = new Rect2(x, y, width, 25);
            _categoryButtons.Add((rect, category));
            var active = category == _category;
            ReassemblyUi.DrawChamferFrame(this, rect, active ? new Color("#12303a") : new Color("#0a141c"),
                active ? ReassemblyUi.CyanHighlight : ReassemblyUi.PanelBorder, 3, false, active ? 1.2f : 0.8f);
            DrawString(_font, rect.Position + new Vector2(0, 17), ReassemblyUi.FitText(_font, category, 11, width - 6),
                HorizontalAlignment.Center, width, 11,
                active ? ReassemblyUi.CyanHighlight : ReassemblyUi.TextMuted);
            x += width + 2;
        }
    }

    private void DrawModuleList()
    {
        _moduleButtons.Clear();
        var entries = FilteredModules();
        _page = Math.Clamp(_page, 0, MaxPage());
        var pageEntries = entries.Skip(_page * PageSize).Take(PageSize);
        var index = 0;
        foreach (var id in pageEntries)
        {
            var col = index % 2;
            var row = index / 2;
            index++;
            var rect = new Rect2(ListX + col * (CardWidth + ColumnGap), ListY + row * (CardHeight + RowGap), CardWidth, CardHeight);
            var spec = ShipShapeLibrary.Get(id);
            var unlocked = Progress.CanBuild(id);
            var selected = id == _selectedId;
            _moduleButtons.Add((rect, id));
            ReassemblyUi.DrawChamferFrame(this, rect,
                selected ? new Color("#102936") : new Color("#09141d"),
                selected ? ReassemblyUi.CyanHighlight : unlocked ? ReassemblyUi.CyanMuted * 0.6f : ReassemblyUi.PanelBorder,
                5, false, selected ? 1.4f : 0.9f);
            ModularShipVisual.DrawIcon(this, spec, rect.Position + new Vector2(32, 35), 23, encrypted: !unlocked);
            DrawString(_font, rect.Position + new Vector2(64, 25), ReassemblyUi.FitText(_font, spec.Label, 13, CardWidth - 155),
                HorizontalAlignment.Left, CardWidth - 155, 13,
                unlocked ? ReassemblyUi.TextSteel : ReassemblyUi.TextMuted);
            DrawString(_font, rect.Position + new Vector2(64, 45),
                ReassemblyUi.FitText(_font, $"{ShapeName(spec)}  ·  {spec.Category}", 10, CardWidth - 155),
                HorizontalAlignment.Left, CardWidth - 155, 10, ReassemblyUi.TextMuted);
            DrawString(_font, rect.Position + new Vector2(CardWidth - 72, 39), unlocked ? "已解密" : "待解密",
                HorizontalAlignment.Center, 58, 10, unlocked ? ReassemblyUi.PhosphorGreen : ReassemblyUi.TextMuted);
        }

        DrawString(_font, new Vector2(ListX, PanelY + PanelHeight - 25),
            $"模块 {entries.Length}    第 {_page + 1} / {MaxPage() + 1} 页", fontSize: 11, modulate: ReassemblyUi.TextMuted);
        _previousButton = new Rect2(ListX + 440, PanelY + PanelHeight - 48, 70, 28);
        _nextButton = new Rect2(ListX + 518, PanelY + PanelHeight - 48, 70, 28);
        ReassemblyUi.DrawChamferFrame(this, _previousButton, new Color("#0a141c"), ReassemblyUi.PanelBorder, 3, false, 0.8f);
        ReassemblyUi.DrawChamferFrame(this, _nextButton, new Color("#0a141c"), ReassemblyUi.PanelBorder, 3, false, 0.8f);
        DrawString(_font, _previousButton.Position + new Vector2(0, 19), "上页", HorizontalAlignment.Center, 70, 11, ReassemblyUi.TextSteel);
        DrawString(_font, _nextButton.Position + new Vector2(0, 19), "下页", HorizontalAlignment.Center, 70, 11, ReassemblyUi.TextSteel);
    }

    private void DrawSelectedModule()
    {
        var rect = new Rect2(DetailX, ListY, PanelX + PanelWidth - DetailX - 24, 446);
        ReassemblyUi.DrawChamferFrame(this, rect, new Color("#08141d"), ReassemblyUi.CyanMuted * 0.65f, 7, false, 1);
        var spec = Selected;
        var unlocked = Progress.CanBuild(spec.Id);
        DrawString(_font, new Vector2(rect.Position.X + 18, rect.Position.Y + 24), "方块规格",
            fontSize: 10, modulate: ReassemblyUi.CyanHighlight);
        ModularShipVisual.DrawIcon(this, spec, new Vector2(rect.Position.X + rect.Size.X * 0.5f, rect.Position.Y + 112),
            82, encrypted: !unlocked);
        DrawString(_font, new Vector2(rect.Position.X + 18, rect.Position.Y + 222), ReassemblyUi.FitText(_font, spec.Label, 20, rect.Size.X - 36),
            HorizontalAlignment.Center, rect.Size.X - 36, 20, ReassemblyUi.TextWhite);
        DrawString(_font, new Vector2(rect.Position.X + 18, rect.Position.Y + 247),
            $"{ShapeName(spec)}  ·  SCALE {spec.Scale}", HorizontalAlignment.Center, rect.Size.X - 36, 11, ReassemblyUi.TextMuted);

        var detailY = rect.Position.Y + 278;
        var first = spec.Cannon is not null ? $"武器伤害  {spec.Cannon.Damage:0}    射程  {spec.Cannon.Range:0}"
            : spec.IsThruster ? $"推力  {spec.Thrust:0}    推力类型  {(spec.NativeFeatures.Contains("FIN") ? "稳定翼" : "推进器")}"
            : spec.Shield is not null ? $"护盾  {spec.Shield.Strength:0}    再生  {spec.Shield.Regen:0}"
            : $"耐久  {spec.Durability:0.##}    结构点数  {spec.Points:0}";
        DrawString(_font, new Vector2(rect.Position.X + 18, detailY), first, fontSize: 11, modulate: ReassemblyUi.TextSteel);
        DrawString(_font, new Vector2(rect.Position.X + 18, detailY + 21),
            $"容量 {spec.Capacity:0.##}    密度 {spec.Density:0.##}    质量 {spec.Mass:0}", fontSize: 10, modulate: ReassemblyUi.TextMuted);
        DrawDescription(spec.Description, new Vector2(rect.Position.X + 18, detailY + 50), rect.Size.X - 36);

        _purchaseButton = new Rect2(rect.Position.X + 16, rect.End.Y - 54, rect.Size.X - 32, 38);
        var cost = PlayerProgress.UnlockCost(spec);
        var canAfford = Progress.Resources >= cost;
        var buttonColor = unlocked ? ReassemblyUi.CyanMuted : canAfford ? ReassemblyUi.CyanHighlight : ReassemblyUi.PanelBorder;
        ReassemblyUi.DrawChamferFrame(this, _purchaseButton,
            unlocked ? new Color("#0b171f") : new Color("#10232c"), buttonColor, 5, false, 1.0f);
        DrawString(_font, _purchaseButton.Position + new Vector2(0, 24), unlocked ? "已解密 · 可在造舰页建造" : $"解密模块    {cost:N0} R",
            HorizontalAlignment.Center, _purchaseButton.Size.X, 13,
            unlocked ? ReassemblyUi.PhosphorGreen : canAfford ? ReassemblyUi.TextWhite : ReassemblyUi.TextMuted);
        DrawString(_font, new Vector2(rect.Position.X + 18, rect.End.Y + 24),
            ReassemblyUi.FitText(_font, _notice, 11, rect.Size.X - 36),
            HorizontalAlignment.Left, rect.Size.X - 36, 11,
            _notice.Contains("不足", StringComparison.Ordinal) ? ReassemblyUi.TacticalAmber : ReassemblyUi.TextMuted);
    }

    private void DrawDescription(string description, Vector2 position, float width)
    {
        if (string.IsNullOrWhiteSpace(description)) return;
        var line = "";
        var y = position.Y;
        var maxCharacters = Math.Max(8, (int)(width / 12));
        foreach (var character in description)
        {
            if (line.Length >= maxCharacters)
            {
                DrawString(_font, new Vector2(position.X, y), line, fontSize: 10, modulate: ReassemblyUi.TextMuted);
                y += 15;
                line = character.ToString();
                if (y > position.Y + 45) break;
            }
            else line += character;
        }
        if (line.Length > 0 && y <= position.Y + 45)
            DrawString(_font, new Vector2(position.X, y), line, fontSize: 10, modulate: ReassemblyUi.TextMuted);
    }

    private int MaxPage() => Math.Max(0, (FilteredModules().Length - 1) / PageSize);

    private static string ShapeName(ShipShapeLibrary.ShapeSpec spec) =>
        !string.IsNullOrWhiteSpace(spec.NativeShapeName) ? spec.NativeShapeName
        : spec.NumId == 30001 ? "REWORLD"
        : spec.NativeShape > 0 ? $"原版方块 {spec.NativeShape}" : "自制方块";

    private static int CategorySort(string category) => category switch
    {
        "全部" => 0, "核心" => 1, "骨架" => 2, "机动" => 3, "武器" => 4,
        "防御" => 5, "能源" => 6, "制造" => 7, "物流" => 8, "系统" => 9, "散热" => 10, _ => 20,
    };
}
