using System;
using System.Collections.Generic;
using System.Linq;

public enum DiplomaticState { Neutral, Friendly, War, Wanted }

/// <summary>Faction identity is independent of ship design, paint and fleet ownership.</summary>
public sealed class FactionRelations
{
    public const int Player = 1, League = 8, Red = 3, Crystal = 11;
    public static readonly Dictionary<int, string> Names = new()
    {
        [Player] = "独立舰长", [2] = "农夫公社", [Red] = "赤色联合", [4] = "廷克雷尔",
        [5] = "植物群落", [6] = "博格", [7] = "飞蝇", [League] = "联航共同体", [10] = "竞技者",
        [Crystal] = "晶体共同体", [12] = "蜂群", [13] = "棘刺群落", [14] = "深空访客",
        [15] = "哨卫", [16] = "边境居民", [100] = "环境设施"
    };
    public Dictionary<string, DiplomaticState> States { get; set; } = new();
    private Dictionary<string, DiplomaticState>? _cachedStates;
    private readonly Dictionary<long, DiplomaticState> _pairs = new();
    private static long Key(int a, int b) => ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
    private static string Pair(int a, int b) => a < b ? $"{a}:{b}" : $"{b}:{a}";
    public DiplomaticState Get(int a, int b)
    {
        if (a == b) return DiplomaticState.Friendly;
        if (!ReferenceEquals(_cachedStates, States) || _pairs.Count != States.Count)
        {
            _pairs.Clear();
            foreach (var (pair, state) in States)
            {
                var values = pair.Split(':');
                if (values.Length == 2 && int.TryParse(values[0], out var x) && int.TryParse(values[1], out var y))
                    _pairs[Key(x, y)] = state;
            }
            _cachedStates = States;
        }
        return _pairs.GetValueOrDefault(Key(a, b), DiplomaticState.Neutral);
    }
    public bool AtWar(int a, int b) => a != b && Get(a, b) == DiplomaticState.War;
    public bool Hostile(int a, int b) => a != b && Get(a, b) is DiplomaticState.War or DiplomaticState.Wanted;
    public bool Friendly(int a, int b) => a == b || Get(a, b) == DiplomaticState.Friendly;
    public void Set(int a, int b, DiplomaticState state)
    {
        if (a <= 0 || b <= 0 || a == b) return;
        States[Pair(a, b)] = state;
        _pairs[Key(a, b)] = state;
    }
    public string Name(int faction, bool established = false) => faction == Player && established ? "玩家自治派系" : Names.GetValueOrDefault(faction, $"派系 {faction}");
    public static string Label(DiplomaticState state) => state switch
    {
        DiplomaticState.War => "交战",
        DiplomaticState.Wanted => "通缉",
        DiplomaticState.Friendly => "友好",
        _ => "中立"
    };
}
