using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Reassembly 工业矢量风格的开始页面：星域漂移背景 + 切角面板标题 + 主菜单。
/// 提供「新的战役 / 继续战役 / 退出游戏」三项，支持鼠标与键盘操作。
/// </summary>
public partial class StartScreen : Node2D
{
    private sealed class MenuItem
    {
        public MenuItem(string label, Action action)
        {
            Label = label;
            Action = action;
        }

        public string Label;
        public Action Action;
        public Rect2 Rect;
    }

    private sealed class Star
    {
        public Vector2 Position; // 0..1 归一化坐标
        public float Speed;      // 像素/秒，向下漂移
        public float Size;
        public float Alpha;
    }

    private const float PanelWidth = 560;
    private const float PanelHeight = 420;
    private const float ItemWidth = 320;
    private const float ItemHeight = 52;
    private const float ItemGap = 14;

    private Font _font = null!;
    private readonly List<MenuItem> _items = new();
    private readonly List<Star> _stars = new();
    private int _selected;
    private double _time;

    public event Action? NewGameRequested;
    public event Action? ContinueRequested;
    public event Action? QuitRequested;

    public override void _Ready()
    {
        Name = "StartScreen";
        _font = new SystemFont
        {
            FontNames = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Noto Sans CJK SC" },
            AllowSystemFallback = true,
        };

        _items.Add(new MenuItem("新的战役", () => NewGameRequested?.Invoke()));
        if (PlayerProgress.SaveExists())
            _items.Add(new MenuItem("继续战役", () => ContinueRequested?.Invoke()));
        _items.Add(new MenuItem("退出游戏", () => QuitRequested?.Invoke()));

        // 固定种子的星域背景，保证每次启动观感一致。
        var rng = new RandomNumberGenerator();
        rng.Seed = 20261024;
        for (var i = 0; i < 150; i++)
        {
            _stars.Add(new Star
            {
                Position = new Vector2(rng.Randf(), rng.Randf()),
                Speed = rng.RandfRange(6f, 42f),
                Size = rng.RandfRange(1f, 2.2f),
                Alpha = rng.RandfRange(0.15f, 0.65f),
            });
        }
    }

    public override void _Process(double delta)
    {
        _time += delta;
        var size = GetViewportRect().Size;
        foreach (var star in _stars)
        {
            star.Position.Y += star.Speed * (float)delta / size.Y;
            if (star.Position.Y > 1f) star.Position.Y -= 1f;
        }
        QueueRedraw();
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseMotion motion)
        {
            var index = HoverIndex(motion.Position);
            if (index >= 0)
            {
                _selected = index;
                Input.SetDefaultCursorShape(Input.CursorShape.PointingHand);
            }
            else
            {
                Input.SetDefaultCursorShape(Input.CursorShape.Arrow);
            }
            return;
        }
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click)
        {
            var index = HoverIndex(click.Position);
            if (index >= 0)
            {
                _selected = index;
                GetViewport().SetInputAsHandled();
                Activate(_selected);
            }
            return;
        }
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        switch (key.Keycode)
        {
            case Key.Up or Key.W:
                _selected = (_selected - 1 + _items.Count) % _items.Count;
                GetViewport().SetInputAsHandled();
                break;
            case Key.Down or Key.S:
                _selected = (_selected + 1) % _items.Count;
                GetViewport().SetInputAsHandled();
                break;
            case Key.Enter or Key.KpEnter or Key.Space:
                GetViewport().SetInputAsHandled();
                Activate(_selected);
                break;
        }
    }

    private int HoverIndex(Vector2 point)
    {
        for (var i = 0; i < _items.Count; i++)
            if (_items[i].Rect.HasPoint(point))
                return i;
        return -1;
    }

    private void Activate(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        _items[index].Action();
    }

    public override void _Draw()
    {
        var size = GetViewportRect().Size;
        DrawRect(new Rect2(Vector2.Zero, size), ReassemblyUi.VoidBlack);

        // 漂移星域
        foreach (var star in _stars)
        {
            var pos = star.Position * size;
            DrawCircle(pos, star.Size, new Color(ReassemblyUi.TextSteel, star.Alpha));
        }

        // 稀疏工业网格
        var gridColor = new Color(ReassemblyUi.CyanGrid, 0.35f);
        for (var x = 0f; x < size.X; x += 96f)
            DrawLine(new Vector2(x, 0), new Vector2(x, size.Y), gridColor, 1f);
        for (var y = 0f; y < size.Y; y += 96f)
            DrawLine(new Vector2(0, y), new Vector2(size.X, y), gridColor, 1f);

        // 中央切角主面板
        var panel = new Rect2(new Vector2((size.X - PanelWidth) / 2f, (size.Y - PanelHeight) / 2f),
            new Vector2(PanelWidth, PanelHeight));
        ReassemblyUi.DrawChamferFrame(this, panel, ReassemblyUi.PanelBg, ReassemblyUi.PanelBorder, 14f);

        // 标题
        var title = "ReWorld";
        var titleSize = 72;
        var titleWidth = _font.GetStringSize(title, fontSize: titleSize).X;
        var titlePos = new Vector2(panel.GetCenter().X - titleWidth / 2f, panel.Position.Y + 118f);
        DrawString(_font, titlePos + new Vector2(2, 2), title, HorizontalAlignment.Left, -1, titleSize,
            new Color(ReassemblyUi.PanelBorder, 0.55f));
        DrawString(_font, titlePos, title, HorizontalAlignment.Left, -1, titleSize, ReassemblyUi.TextWhite);

        var subtitle = "模块化造舰 · 星域探索 · 派系战争";
        var subWidth = _font.GetStringSize(subtitle, fontSize: 14).X;
        DrawString(_font, new Vector2(panel.GetCenter().X - subWidth / 2f, panel.Position.Y + 150f),
            subtitle, HorizontalAlignment.Left, -1, 14, ReassemblyUi.TextMuted);

        // 标题下分隔线 + 刻度
        var ruleY = panel.Position.Y + 174f;
        var ruleLeft = panel.Position.X + 44f;
        var ruleRight = panel.End.X - 44f;
        DrawLine(new Vector2(ruleLeft, ruleY), new Vector2(ruleRight, ruleY),
            ReassemblyUi.PanelBorder, 1f);
        for (var x = ruleLeft; x <= ruleRight; x += 24f)
            DrawLine(new Vector2(x, ruleY), new Vector2(x, ruleY + (x % 96f == 0 ? 6f : 3f)),
                new Color(ReassemblyUi.CyanHighlight, 0.5f), 1f);

        // 菜单项
        var menuTop = panel.Position.Y + 206f;
        var menuLeft = panel.GetCenter().X - ItemWidth / 2f;
        for (var i = 0; i < _items.Count; i++)
        {
            var rect = new Rect2(new Vector2(menuLeft, menuTop + i * (ItemHeight + ItemGap)),
                new Vector2(ItemWidth, ItemHeight));
            _items[i].Rect = rect;
            var selected = i == _selected;
            var border = selected ? ReassemblyUi.PanelBorderHighlight : ReassemblyUi.PanelBorder;
            var bg = selected ? new Color(ReassemblyUi.TerranBlue, 0.55f) : new Color(ReassemblyUi.VoidDark, 0.8f);
            ReassemblyUi.DrawChamferFrame(this, rect, bg, border, 8f,
                cornerBrackets: selected, borderWidth: selected ? 1.5f : 1f);

            var label = selected ? $"> {_items[i].Label}" : _items[i].Label;
            var labelSize = 20;
            var labelWidth = _font.GetStringSize(label, fontSize: labelSize).X;
            DrawString(_font,
                new Vector2(rect.GetCenter().X - labelWidth / 2f, rect.GetCenter().Y + 7f),
                label, HorizontalAlignment.Left, -1, labelSize,
                selected ? ReassemblyUi.TextWhite : ReassemblyUi.TextSteel);
        }

        // 操作提示
        var hint = "↑↓ 选择　Enter 确认　鼠标可点击";
        var hintWidth = _font.GetStringSize(hint, fontSize: 12).X;
        DrawString(_font, new Vector2(panel.GetCenter().X - hintWidth / 2f, panel.End.Y - 24f),
            hint, HorizontalAlignment.Left, -1, 12, ReassemblyUi.TextMuted);

        // 底部版本信息
        var version = $"ReWorld · Godot 4 · {DateTime.Now:yyyy-MM-dd}";
        DrawString(_font, new Vector2(16, size.Y - 14), version, HorizontalAlignment.Left, -1, 12,
            new Color(ReassemblyUi.TextMuted, 0.7f));

        // 顶部装饰准星线
        var cx = panel.GetCenter().X;
        var blink = 0.5f + 0.5f * (float)Mathf.Sin(_time * 2.2);
        DrawLine(new Vector2(cx - 60f, panel.Position.Y - 18f), new Vector2(cx - 18f, panel.Position.Y - 18f),
            new Color(ReassemblyUi.PointCyan, blink), 1.5f);
        DrawLine(new Vector2(cx + 18f, panel.Position.Y - 18f), new Vector2(cx + 60f, panel.Position.Y - 18f),
            new Color(ReassemblyUi.PointCyan, blink), 1.5f);
        DrawCircle(new Vector2(cx, panel.Position.Y - 18f), 2.5f, new Color(ReassemblyUi.PointCyan, blink));
    }
}
