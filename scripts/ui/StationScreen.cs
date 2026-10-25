using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>Docked station services: faction diplomacy, shipyard/decryption trade, and per-system contracts.</summary>
public partial class StationScreen : Node2D
{
    private const float ViewWidth = 1280;
    private const float ViewHeight = 720;
    private const float PanelX = 52;
    private const float PanelY = 28;
    private const float PanelWidth = 1176;
    private const float PanelHeight = 664;
    private const int TradePageSize = 6;

    private enum Tab { Diplomacy, Trade, Missions }

    private Font _font = null!;
    private Tab _tab = Tab.Diplomacy;
    private string _notice = "停靠空间站可使用外交、交易与合约服务";
    private int _tradePage;
    private Rect2 _closeButton;
    private Rect2 _manufactureButton;
    private Rect2 _previousButton;
    private Rect2 _nextButton;
    private readonly List<(Rect2 Rect, Tab Tab)> _tabButtons = new();
    private readonly List<(Rect2 Rect, int Faction, string Action)> _diplomacyButtons = new();
    private readonly List<(Rect2 Rect, string Id)> _unlockButtons = new();
    private readonly List<(Rect2 Rect, string Id)> _sellButtons = new();
    private readonly List<(Rect2 Rect, string Id)> _missionButtons = new();
    private Vector2I _missionCell = new(int.MaxValue, int.MaxValue);
    private List<MissionDef> _missions = new();

    public PlayerProgress Progress { get; set; } = new();
    public UniverseView? View { get; set; }
    public event Action? CloseRequested;

    public override void _Ready()
    {
        _font = new SystemFont
        {
            FontNames = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Noto Sans CJK SC" },
            AllowSystemFallback = true,
        };
    }

    public override void _Process(double delta)
    {
        // 空间站页打开时宇宙页停摆：在交易页继续推进市场时钟，行情持续波动、抛售压力缓慢回稳。
        if (_tab == Tab.Trade) Progress.TickMarket((float)delta);
        QueueRedraw();
    }

    private List<MissionDef> CurrentMissions()
    {
        var cell = View?.CurrentCell ?? Vector2I.Zero;
        if (cell != _missionCell)
        {
            _missionCell = cell;
            _missions = MissionCatalog.Generate(cell);
        }
        return _missions;
    }

    private static string[] LockedModules() => ShipShapeLibrary.BuildOrder
        .Where(id => ShipShapeLibrary.Get(id).IsRegistered).ToArray();

    public void ApplyDiplomacy(int faction, string action)
    {
        if (action == "aid")
        {
            var aided = View is not null ? View.AidFaction(faction) : AidLocally(faction);
            _notice = aided ? $"与{Progress.Diplomacy.Name(faction)}的关系：友好"
                : "援助需 50 R，且双方不能处于交战状态";
            return;
        }
        var state = action switch
        {
            "war" => DiplomaticState.War,
            "wanted" => DiplomaticState.Wanted,
            _ => DiplomaticState.Neutral
        };
        if (View is not null) View.ChangeRelation(faction, state);
        else
        {
            Progress.Diplomacy.Set(FactionRelations.Player, faction, state);
            Progress.Commit();
        }
        _notice = $"与{Progress.Diplomacy.Name(faction)}的关系：{FactionRelations.Label(state)}";
    }

    private bool AidLocally(int faction)
    {
        if (Progress.Diplomacy.Hostile(FactionRelations.Player, faction) || !Progress.Spend(50)) return false;
        Progress.Diplomacy.Set(FactionRelations.Player, faction, DiplomaticState.Friendly);
        Progress.Commit();
        return true;
    }

    public bool TryTradeUnlock(string id)
    {
        if (!ShipShapeLibrary.BuildOrder.Contains(id)) return false;
        var spec = ShipShapeLibrary.Get(id);
        if (Progress.CanBuild(id))
        {
            _notice = "该模块已经解密";
            return false;
        }
        var discount = TradeDiscountPercent();
        var cost = PlayerProgress.StationUnlockCost(spec, discount);
        if (Progress.Resources < cost)
        {
            _notice = $"资源不足，还差 {cost - Progress.Resources} R";
            return false;
        }
        if (!Progress.TryStationUnlock(id, discount)) return false;
        var deal = discount < 95 ? $"友好价 {discount} 折" : "空间站优惠价";
        _notice = $"空间站终端已解密：{spec.Label} · {deal} {cost} R";
        return true;
    }

    /// <summary>与停靠站派系的关系决定解密折扣：友好 85 折，否则 95 折。</summary>
    private int TradeDiscountPercent()
    {
        var faction = View?.DockedStationFaction ?? 0;
        return faction != 0 && Progress.Diplomacy.Get(FactionRelations.Player, faction) == DiplomaticState.Friendly ? 85 : 95;
    }

    public bool TryManufacture()
    {
        if (View is null) return false;
        if (View.ManufactureDocked())
        {
            _notice = "舰船已交付自治舰队 · Tab 指挥";
            return true;
        }
        _notice = "制造失败：需停靠、存活核心、已解锁蓝图、足够资源，舰队上限 8 艘";
        return false;
    }

    /// <summary>出售库存资源：按当前行情价结算成 R；抛售会立即压低该资源的后续行情。</summary>
    public bool TrySellResource(string kind)
    {
        var stock = Progress.ResourceStock.GetValueOrDefault(kind);
        if (stock <= 0)
        {
            _notice = $"{PlayerProgress.ResourceLabel(kind)}库存不足 · 工厂产出会自动入库";
            return false;
        }
        var price = PlayerProgress.ResourcePrice(kind, Progress.MarketPressureOf(kind), Progress.MarketTime);
        var income = (int)(stock * price);
        if (income <= 0)
        {
            _notice = "行情过低 · 稍后再售";
            return false;
        }
        Progress.ResourceStock[kind] = 0;
        Progress.AddMarketPressure(kind, stock * 0.003f);
        Progress.Collect(income);
        _notice = $"售出{PlayerProgress.ResourceLabel(kind)} {stock:N0} · 单价 {price:0.00} R · 入账 {income:N0} R";
        return true;
    }

    public bool TryAcceptMission(string id)
    {
        if (!Progress.AcceptMission(id)) return false;
        _notice = "合约已接取 · 进度会自动记录";
        return true;
    }

    public bool TryClaimMission(string id)
    {
        if (!Progress.ClaimMission(id))
        {
            _notice = "合约目标尚未达成";
            return false;
        }
        var def = MissionCatalog.Find(id);
        _notice = $"合约完成 · 奖励 {def?.Reward ?? 0} R 已到账";
        return true;
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click)
            return;
        if (_closeButton.HasPoint(click.Position))
            CloseRequested?.Invoke();
        else if (HitAny(_tabButtons, click.Position, tab => { _tab = tab; _notice = ""; }))
        { }
        else if (_tab == Tab.Diplomacy && HitAny(_diplomacyButtons, click.Position,
            hit => ApplyDiplomacy(hit.Faction, hit.Action)))
        { }
        else if (_tab == Tab.Trade && _manufactureButton.HasPoint(click.Position))
            TryManufacture();
        else if (_tab == Tab.Trade && _previousButton.HasPoint(click.Position))
            _tradePage = Math.Max(0, _tradePage - 1);
        else if (_tab == Tab.Trade && _nextButton.HasPoint(click.Position))
            _tradePage = Math.Min(TradeMaxPage(), _tradePage + 1);
        else if (_tab == Tab.Trade && HitAny(_sellButtons, click.Position, id => TrySellResource(id)))
        { }
        else if (_tab == Tab.Trade && HitAny(_unlockButtons, click.Position, id => TryTradeUnlock(id)))
        { }
        else if (_tab == Tab.Missions && HitAny(_missionButtons, click.Position, id =>
        {
            var state = Progress.MissionStateFor(id);
            if (!state.Accepted) TryAcceptMission(id);
            else TryClaimMission(id);
        }))
        { }
        else if (!new Rect2(PanelX, PanelY, PanelWidth, PanelHeight).HasPoint(click.Position))
            return;
        GetViewport().SetInputAsHandled();
    }

    private bool HitAny<T>(List<(Rect2 Rect, T Value)> buttons, Vector2 point, Action<T> action)
    {
        foreach (var (rect, value) in buttons)
            if (rect.HasPoint(point))
            {
                action(value);
                return true;
            }
        return false;
    }
    private bool HitAny(List<(Rect2 Rect, int Faction, string Action)> buttons, Vector2 point,
        Action<(int Faction, string Action)> action)
    {
        foreach (var (rect, faction, act) in buttons)
            if (rect.HasPoint(point))
            {
                action((faction, act));
                return true;
            }
        return false;
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, new Vector2(ViewWidth, ViewHeight)), new Color("#050b12", 0.82f));
        ReassemblyUi.DrawChamferFrame(this, new Rect2(PanelX, PanelY, PanelWidth, PanelHeight),
            ReassemblyUi.VoidDark, ReassemblyUi.CyanMuted, 12, true, 1.4f);
        DrawHeader();
        DrawTabs();
        switch (_tab)
        {
            case Tab.Diplomacy: DrawDiplomacy(); break;
            case Tab.Trade: DrawTrade(); break;
            case Tab.Missions: DrawMissions(); break;
        }
        if (_notice.Length > 0)
            DrawString(_font, new Vector2(PanelX + 24, PanelY + PanelHeight - 18),
                ReassemblyUi.FitText(_font, _notice, 12, PanelWidth - 48),
                HorizontalAlignment.Left, PanelWidth - 48, 12,
                _notice.Contains("不足", StringComparison.Ordinal) || _notice.Contains("失败", StringComparison.Ordinal)
                    ? ReassemblyUi.TacticalAmber : ReassemblyUi.TextMuted);
    }

    private void DrawHeader()
    {
        var station = View?.DockedStationName ?? "空间站";
        var owner = View is not null && View.DockedStationFaction != 0
            ? $" · {Progress.Diplomacy.Name(View.DockedStationFaction)}"
            : "";
        DrawString(_font, new Vector2(PanelX + 24, PanelY + 34),
            ReassemblyUi.FitText(_font, $"空间站服务  /  {station}{owner}", 17, 820),
            HorizontalAlignment.Left, 820, 17, ReassemblyUi.TextWhite);
        DrawString(_font, new Vector2(PanelX + 24, PanelY + 57), "外交 · 交易 · 合约一站办理",
            fontSize: 12, modulate: ReassemblyUi.TextMuted);
        DrawString(_font, new Vector2(PanelX + PanelWidth - 210, PanelY + 38), $"R  {Progress.Resources:N0}",
            fontSize: 17, modulate: ReassemblyUi.TacticalAmber);
        _closeButton = new Rect2(PanelX + PanelWidth - 48, PanelY + 12, 30, 30);
        ReassemblyUi.DrawChamferFrame(this, _closeButton, new Color("#0b1821"), ReassemblyUi.CyanMuted, 4, false, 1);
        DrawString(_font, _closeButton.Position + new Vector2(9, 21), "×", fontSize: 18, modulate: ReassemblyUi.TextSteel);
        DrawLine(new Vector2(PanelX + 20, PanelY + 70), new Vector2(PanelX + PanelWidth - 20, PanelY + 70),
            ReassemblyUi.CyanGrid, 1);
    }

    private void DrawTabs()
    {
        _tabButtons.Clear();
        var entries = new[] { (Tab.Diplomacy, "派系外交"), (Tab.Trade, "商店交易"), (Tab.Missions, "合约任务") };
        var x = PanelX + 24;
        foreach (var (tab, label) in entries)
        {
            var rect = new Rect2(x, PanelY + 82, 120, 28);
            _tabButtons.Add((rect, tab));
            var active = tab == _tab;
            ReassemblyUi.DrawChamferFrame(this, rect, active ? new Color("#12303a") : new Color("#0a141c"),
                active ? ReassemblyUi.CyanHighlight : ReassemblyUi.PanelBorder, 3, false, active ? 1.2f : 0.8f);
            DrawString(_font, rect.Position + new Vector2(0, 19), label, HorizontalAlignment.Center, 120, 12,
                active ? ReassemblyUi.CyanHighlight : ReassemblyUi.TextMuted);
            x += 126;
        }
    }

    private Color FactionColor(int faction) =>
        Progress.Diplomacy.Get(FactionRelations.Player, faction) switch
        {
            DiplomaticState.War or DiplomaticState.Wanted => new Color("#f46a6a"),
            DiplomaticState.Friendly => new Color("#6acbe0"),
            _ => new Color("#d6bd7d")
        };

    private void DrawDiplomacy()
    {
        _diplomacyButtons.Clear();
        DrawString(_font, new Vector2(PanelX + 24, PanelY + 132),
            "援助建立友好关系；停战立即停止双方主动开火。无端袭击中立/友好派系舰船会被自动通缉。",
            fontSize: 12, modulate: ReassemblyUi.TextSteel);
        DrawString(_font, new Vector2(PanelX + 300, PanelY + 114), "关系",
            fontSize: 10, modulate: ReassemblyUi.TextMuted);
        DrawString(_font, new Vector2(PanelX + 420, PanelY + 114), "击杀台账",
            fontSize: 10, modulate: ReassemblyUi.TextMuted);
        var factions = FactionRelations.Names.Keys
            .Where(f => f != FactionRelations.Player && f != 100 && f != FactionRelations.Pirates)
            .OrderBy(f => f).ToArray();
        var y = PanelY + 146f;
        foreach (var faction in factions)
        {
            var state = Progress.Diplomacy.Get(FactionRelations.Player, faction);
            var kills = Progress.Kills.GetValueOrDefault(faction);
            var row = new Rect2(PanelX + 24, y, PanelWidth - 48, 30);
            ReassemblyUi.DrawChamferFrame(this, row, new Color("#09141d"), ReassemblyUi.PanelBorder, 4, false, 0.7f);
            DrawString(_font, row.Position + new Vector2(14, 21),
                ReassemblyUi.FitText(_font, Progress.Diplomacy.Name(faction), 13, 260),
                HorizontalAlignment.Left, 260, 13, FactionColor(faction));
            DrawString(_font, row.Position + new Vector2(300, 21), FactionRelations.Label(state),
                fontSize: 12, modulate: ReassemblyUi.TextSteel);
            DrawString(_font, row.Position + new Vector2(420, 21), kills > 0 ? $"累计击杀 {kills}" : "无",
                fontSize: 11, modulate: kills > 0 ? ReassemblyUi.TacticalAmber : ReassemblyUi.TextMuted);
            if (UniverseGenerator.MajorStationFactions.Contains(faction))
                DrawString(_font, row.Position + new Vector2(520, 21), "运营星系站点",
                    fontSize: 11, modulate: ReassemblyUi.PhosphorGreen);
            var action = state is DiplomaticState.War or DiplomaticState.Wanted ? "peace" : "war";
            foreach (var item in new[]
            {
                (X: row.End.X - 320f, Action: action, Text: action == "war" ? "宣战" : "停战"),
                (X: row.End.X - 220f, Action: "wanted", Text: "通缉"),
                (X: row.End.X - 120f, Action: "aid", Text: "援助 50 R")
            })
            {
                var rect = new Rect2(item.X, y + 3, 96, 24);
                _diplomacyButtons.Add((rect, faction, item.Action));
                ReassemblyUi.DrawChamferFrame(this, rect, ReassemblyUi.VoidDark,
                    item.Action == "war" ? ReassemblyUi.TacticalAmber : ReassemblyUi.CyanMuted, 3, false, 0.9f);
                DrawString(_font, rect.Position + new Vector2(0, 17), item.Text,
                    HorizontalAlignment.Center, 96, 11, ReassemblyUi.TextWhite);
            }
            y += 34;
        }
    }

    /// <summary>资源交易区：工厂产出的碎料/合金入库后在此按行情出售换 R。</summary>
    private void DrawResourceExchange()
    {
        _sellButtons.Clear();
        var rect = new Rect2(PanelX + 24, PanelY + 112, PanelWidth - 48, 88);
        ReassemblyUi.DrawChamferFrame(this, rect, new Color("#0d2029"), ReassemblyUi.CyanMuted, 6, true, 1);
        DrawString(_font, rect.Position + new Vector2(16, 22), "资源交易 // 工厂产出自动入库 · 行情周期波动 · 抛售压价后缓慢回稳",
            fontSize: 14, modulate: ReassemblyUi.CyanHighlight);
        var rowY = rect.Position.Y + 34;
        foreach (var kind in new[] { "iron", "alloy" })
        {
            var stock = Progress.ResourceStock.GetValueOrDefault(kind);
            var price = PlayerProgress.ResourcePrice(kind, Progress.MarketPressureOf(kind), Progress.MarketTime);
            var rising = PlayerProgress.ResourcePriceRising(kind, Progress.MarketTime);
            var row = new Rect2(rect.Position.X + 16, rowY, rect.Size.X - 32, 44);
            ReassemblyUi.DrawChamferFrame(this, row, new Color("#09141d"),
                stock > 0 ? ReassemblyUi.PanelBorder : new Color("#16303d"), 4, false, 0.8f);
            var tint = kind == "alloy" ? new Color("#e58c55") : new Color("#65c18c");
            DrawCircle(row.Position + new Vector2(16, 22), 5, tint);
            DrawString(_font, row.Position + new Vector2(30, 17), PlayerProgress.ResourceLabel(kind),
                fontSize: 12, modulate: ReassemblyUi.TextSteel);
            DrawString(_font, row.Position + new Vector2(170, 17), $"库存 {stock:N0}",
                fontSize: 11, modulate: stock > 0 ? ReassemblyUi.TextWhite : ReassemblyUi.TextMuted);
            DrawString(_font, row.Position + new Vector2(300, 17), $"单价 {price:0.00} R",
                fontSize: 11, modulate: ReassemblyUi.TextSteel);
            DrawString(_font, row.Position + new Vector2(396, 17), rising ? "↑" : "↓",
                fontSize: 12, modulate: rising ? ReassemblyUi.PhosphorGreen : ReassemblyUi.TacticalAmber);
            DrawString(_font, row.Position + new Vector2(414, 17),
                Progress.MarketPressureOf(kind) > 0.05f ? "供给过剩" : "供需平稳",
                fontSize: 10, modulate: Progress.MarketPressureOf(kind) > 0.05f ? ReassemblyUi.TacticalAmber : ReassemblyUi.TextMuted);
            var sell = new Rect2(row.End.X - 130, row.Position.Y + 8, 120, 28);
            _sellButtons.Add((sell, kind));
            ReassemblyUi.DrawChamferFrame(this, sell, stock > 0 ? new Color("#10232c") : new Color("#0a141c"),
                stock > 0 ? ReassemblyUi.CyanHighlight : ReassemblyUi.PanelBorder, 3, false, 1);
            DrawString(_font, sell.Position + new Vector2(0, 19), stock > 0 ? "全部出售" : "无货",
                HorizontalAlignment.Center, 120, 11, stock > 0 ? ReassemblyUi.TextWhite : ReassemblyUi.TextMuted);
            rowY += 48;
        }
    }

    private void DrawTrade()
    {
        _unlockButtons.Clear();
        DrawResourceExchange();
        var shipY = PanelY + 212f;
        var service = new Rect2(PanelX + 24, shipY, PanelWidth - 48, 78);
        ReassemblyUi.DrawChamferFrame(this, service, new Color("#0a1c26"), ReassemblyUi.CyanMuted, 6, true, 1);
        DrawString(_font, service.Position + new Vector2(16, 24), "船坞制造 // 按当前蓝图复制一艘自治舰",
            fontSize: 14, modulate: ReassemblyUi.CyanHighlight);
        var mass = View?.ShipMass ?? 0;
        var price = Math.Max(150, (int)(mass * 2));
        DrawString(_font, service.Position + new Vector2(16, 48),
            View is null ? "需停靠空间站后使用" : $"当前蓝图质量 {mass:0} · 交付价 {price:N0} R · 舰队上限 8 艘",
            fontSize: 11, modulate: ReassemblyUi.TextSteel);
        _manufactureButton = new Rect2(service.End.X - 190, shipY + 20, 170, 38);
        ReassemblyUi.DrawChamferFrame(this, _manufactureButton, new Color("#10232c"),
            ReassemblyUi.CyanHighlight, 5, false, 1);
        DrawString(_font, _manufactureButton.Position + new Vector2(0, 24), "制造舰船",
            HorizontalAlignment.Center, 170, 13, ReassemblyUi.TextWhite);

        var listY = shipY + 90;
        var stationFaction = View?.DockedStationFaction ?? 0;
        var stationFactionName = stationFaction != 0 ? Progress.Diplomacy.Name(stationFaction) : "空间站";
        var friendly = TradeDiscountPercent() < 95;
        DrawString(_font, new Vector2(PanelX + 24, listY - 8),
            stationFaction != 0
                ? $"解密终端 // 与{stationFactionName}关系{(friendly ? "友好" : "中立")} · {(friendly ? "友好价 85 折" : "标准价 95 折")} · 点击直接解密"
                : "解密终端 // 空间站优惠价 · 点击模块直接解密",
            fontSize: 12, modulate: friendly ? ReassemblyUi.PhosphorGreen : ReassemblyUi.TextSteel);
        var locked = LockedModules().Where(id => !Progress.CanBuild(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        _tradePage = Math.Clamp(_tradePage, 0, TradeMaxPage());
        var page = locked.Skip(_tradePage * TradePageSize).Take(TradePageSize);
        var index = 0;
        foreach (var id in page)
        {
            var col = index % 2;
            var rowIndex = index / 2;
            index++;
            var rect = new Rect2(PanelX + 24 + col * 556, listY + 14 + rowIndex * 66, 544, 58);
            var spec = ShipShapeLibrary.Get(id);
            var cost = PlayerProgress.StationUnlockCost(spec, TradeDiscountPercent());
            var afford = Progress.Resources >= cost;
            _unlockButtons.Add((rect, id));
            ReassemblyUi.DrawChamferFrame(this, rect, new Color("#09141d"),
                afford ? ReassemblyUi.CyanMuted : ReassemblyUi.PanelBorder, 5, false, 0.9f);
            ModularShipVisual.DrawIcon(this, spec, rect.Position + new Vector2(30, 29), 20, encrypted: true);
            DrawString(_font, rect.Position + new Vector2(60, 24),
                ReassemblyUi.FitText(_font, spec.Label, 13, 330),
                HorizontalAlignment.Left, 330, 13, ReassemblyUi.TextSteel);
            DrawString(_font, rect.Position + new Vector2(60, 44),
                ReassemblyUi.FitText(_font, $"{spec.Category} · 档案价 {PlayerProgress.UnlockCost(spec):N0} R", 10, 330),
                HorizontalAlignment.Left, 330, 10, ReassemblyUi.TextMuted);
            DrawString(_font, rect.Position + new Vector2(rect.Size.X - 120, 34),
                $"{cost:N0} R", HorizontalAlignment.Center, 100, 13,
                afford ? ReassemblyUi.TacticalAmber : ReassemblyUi.TextMuted);
        }
        if (locked.Length == 0)
            DrawString(_font, new Vector2(PanelX + 24, listY + 40), "所有模块均已解密",
                fontSize: 13, modulate: ReassemblyUi.PhosphorGreen);
        DrawString(_font, new Vector2(PanelX + 24, PanelY + PanelHeight - 52),
            $"待解密 {locked.Length}    第 {_tradePage + 1} / {TradeMaxPage() + 1} 页",
            fontSize: 11, modulate: ReassemblyUi.TextMuted);
        _previousButton = new Rect2(PanelX + PanelWidth - 190, PanelY + PanelHeight - 66, 70, 28);
        _nextButton = new Rect2(PanelX + PanelWidth - 112, PanelY + PanelHeight - 66, 70, 28);
        ReassemblyUi.DrawChamferFrame(this, _previousButton, new Color("#0a141c"), ReassemblyUi.PanelBorder, 3, false, 0.8f);
        ReassemblyUi.DrawChamferFrame(this, _nextButton, new Color("#0a141c"), ReassemblyUi.PanelBorder, 3, false, 0.8f);
        DrawString(_font, _previousButton.Position + new Vector2(0, 19), "上页", HorizontalAlignment.Center, 70, 11, ReassemblyUi.TextSteel);
        DrawString(_font, _nextButton.Position + new Vector2(0, 19), "下页", HorizontalAlignment.Center, 70, 11, ReassemblyUi.TextSteel);
    }

    private int TradeMaxPage()
    {
        var locked = LockedModules().Count(id => !Progress.CanBuild(id));
        return Math.Max(0, (locked - 1) / TradePageSize);
    }

    private void DrawMissions()
    {
        _missionButtons.Clear();
        var system = View?.CurrentCell ?? Vector2I.Zero;
        var issuerName = FactionRelations.Names.GetValueOrDefault(
            UniverseGenerator.StationFactionOf(system), "站方");
        DrawString(_font, new Vector2(PanelX + 24, PanelY + 132),
            $"本星系（{UniverseGenerator.SystemName(system)}）由{issuerName}签发的合约 · 接取后自动记录进度 · 任意站领奖",
            fontSize: 12, modulate: ReassemblyUi.TextSteel);
        var y = PanelY + 150f;
        foreach (var def in CurrentMissions())
        {
            var state = Progress.MissionStateFor(def.Id);
            var card = new Rect2(PanelX + 24, y, PanelWidth - 48, 118);
            var claimed = state.Claimed;
            var ready = Progress.MissionReadyToClaim(def.Id);
            ReassemblyUi.DrawChamferFrame(this, card, claimed ? new Color("#071018") : new Color("#0a1c26"),
                ready ? ReassemblyUi.TacticalAmber : claimed ? ReassemblyUi.PanelBorder : ReassemblyUi.CyanMuted,
                7, false, 1);
            DrawString(_font, card.Position + new Vector2(16, 28),
                ReassemblyUi.FitText(_font, def.Title, 15, 520),
                HorizontalAlignment.Left, 520, 15,
                claimed ? ReassemblyUi.TextMuted : ReassemblyUi.TextWhite);
            DrawString(_font, card.Position + new Vector2(16, 52),
                ReassemblyUi.FitText(_font, def.Detail, 12, 700),
                HorizontalAlignment.Left, 700, 12, ReassemblyUi.TextSteel);
            DrawString(_font, card.Position + new Vector2(16, 76),
                $"奖励 {def.Reward:N0} R",
                fontSize: 12, modulate: ReassemblyUi.TacticalAmber);
            var issuerLabel = FactionRelations.Names.GetValueOrDefault(def.Issuer, "站方");
            DrawString(_font, card.Position + new Vector2(180, 76),
                ReassemblyUi.FitText(_font, $"委托方：{issuerLabel}", 11, 300),
                HorizontalAlignment.Left, 300, 11,
                Progress.Diplomacy.Get(FactionRelations.Player, def.Issuer) switch
                {
                    DiplomaticState.War or DiplomaticState.Wanted => new Color("#f46a6a"),
                    DiplomaticState.Friendly => ReassemblyUi.CyanHighlight,
                    _ => ReassemblyUi.TextMuted
                });
            var ratio = def.Goal > 0 ? Math.Clamp((float)state.Progress / def.Goal, 0, 1) : 0;
            ReassemblyUi.DrawSegmentedMeter(this, card.Position + new Vector2(16, 92), new Vector2(520, 10),
                ratio, claimed ? ReassemblyUi.PhosphorGreen : ReassemblyUi.CyanHighlight, new Color("#163242"), 20, 2);
            DrawString(_font, card.Position + new Vector2(556, 103), $"{state.Progress} / {def.Goal}",
                fontSize: 11, modulate: ReassemblyUi.TextMuted);
            var button = new Rect2(card.End.X - 190, y + 40, 170, 38);
            if (!claimed)
            {
                _missionButtons.Add((button, def.Id));
                var label = !state.Accepted ? "接取合约" : ready ? $"领取 {def.Reward:N0} R" : "进行中…";
                var active = !state.Accepted || ready;
                ReassemblyUi.DrawChamferFrame(this, button, active ? new Color("#10232c") : new Color("#0a141c"),
                    ready ? ReassemblyUi.TacticalAmber : active ? ReassemblyUi.CyanHighlight : ReassemblyUi.PanelBorder,
                    5, false, 1);
                DrawString(_font, button.Position + new Vector2(0, 24), label,
                    HorizontalAlignment.Center, 170, 13,
                    active ? ReassemblyUi.TextWhite : ReassemblyUi.TextMuted);
            }
            else
            {
                DrawString(_font, button.Position + new Vector2(0, 24), "已完成",
                    HorizontalAlignment.Center, 170, 13, ReassemblyUi.PhosphorGreen);
            }
            y += 130;
        }
    }
}
