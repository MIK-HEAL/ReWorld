using Godot;
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

public partial class AiBridgeChecks : Node
{
    private int _checks;

    private void Check(bool condition, string message)
    { _checks++; if (!condition) throw new InvalidOperationException(message); }

    private static JsonObject Args(params (string Key, JsonNode? Value)[] entries)
    {
        var args = new JsonObject();
        foreach (var (key, value) in entries) args[key] = value;
        return args;
    }

    public override async void _Ready()
    {
        try
        {
            var ship = new ModularShip();
            ship.SeedReferenceShip();
            var view = new UniverseView();
            view.SetShip(ship);
            // AddChild 同步触发 _Ready（天体/服务就绪），但物理帧尚未运行：
            // 必须抢在母星引力把船加速出 180 m/s 降落阈值之前发出降落命令。
            AddChild(view);
            OS.SetEnvironment("REWORLD_AI_PORT", "9917");
            AddChild(new AiBridge(view));

            using var client = new GameClient(9917);

            // toggle_dock 必须最先执行：母星引力井会让船在几秒内超过降落的 180 m/s 速度上限，
            // 连接后立即测试才能落在有效的速度窗口内。
            var docked = (JsonObject)(await client.CallAsync("toggle_dock"))["result"]!;
            Check(docked["ok"]!.GetValue<bool>(),
                $"toggle_dock lands on the home planet (notice: {docked["notice"]?.GetValue<string>()})");
            var dockState = (JsonObject)(await client.CallAsync("get_state"))["result"]!;
            Check(dockState["docked"]!.GetValue<string>() == "planet", "get_state reports a planet landing");
            var takeoff = (JsonObject)(await client.CallAsync("takeoff"))["result"]!;
            Check(takeoff["ok"]!.GetValue<bool>(), "takeoff leaves the surface");
            dockState = (JsonObject)(await client.CallAsync("get_state"))["result"]!;
            Check(dockState["docked"]!.GetValue<string>() == "none", "get_state reports free flight after takeoff");

            // get_state：初始快照。
            var state = (JsonObject)(await client.CallAsync("get_state"))["result"]!;
            Check(state["system"]!.GetValue<string>() == "启明星系", "get_state reports the home system");
            Check(state["cell"]!["x"]!.GetValue<int>() == 0 && state["cell"]!["y"]!.GetValue<int>() == 0,
                "get_state reports the origin cell");
            Check(state["alive"]!.GetValue<bool>(), "get_state reports a live core");
            Check(state["docked"]!.GetValue<string>() == "none", "player starts undocked");
            Check(state["warp"]!["state"]!.GetValue<string>() == "ready", "warp starts ready");
            var startX = state["position"]!["x"]!.GetValue<float>();
            var startY = state["position"]!["y"]!.GetValue<float>();

            // get_ship：模块化舰体概要。
            var shipState = (JsonObject)(await client.CallAsync("get_ship"))["result"]!;
            Check(shipState["block_count"]!.GetValue<int>() > 0, "get_ship lists the reference ship");
            Check(shipState["hyperdrive"]!.GetValue<bool>(), "reference ship carries a hyperdrive");
            Check(shipState["weapons"]!.GetValue<int>() > 0, "reference ship carries weapons");

            // list_bodies：母星星系天体（含空间站与行星）。
            var bodies = (JsonObject)(await client.CallAsync("list_bodies"))["result"]!;
            var bodyArray = (JsonArray)bodies["bodies"]!;
            Check(bodyArray.Count > 0, "list_bodies returns bodies");
            var hasStation = false;
            var hasPlanet = false;
            foreach (var node in bodyArray)
            {
                var kind = node!["kind"]!.GetValue<string>();
                hasStation |= kind == "station";
                hasPlanet |= kind == "planet";
            }
            Check(hasStation && hasPlanet, "home system contains a station and planets");

            // list_vessels：星系交通与站体 actor。
            var vessels = (JsonObject)(await client.CallAsync("list_vessels"))["result"]!;
            var vesselArray = (JsonArray)vessels["vessels"]!;
            Check(vesselArray.Count > 0, "list_vessels returns traffic");
            foreach (var node in vesselArray)
                Check(node!["faction_name"] is not null && node["relation"] is not null,
                    "vessel entries carry faction and relation labels");

            // list_linked_systems：8 个相邻星系 + 直航标志。
            var linked = (JsonObject)(await client.CallAsync("list_linked_systems"))["result"]!;
            var linkedArray = (JsonArray)linked["systems"]!;
            Check(linkedArray.Count == 8, "list_linked_systems covers the 3x3 neighbourhood minus origin");
            Check(linked["origin"]!["x"]!.GetValue<int>() == 0, "linked systems report the origin cell");

            // get_missions + accept：接取首个合约，未达成时不可领赏。
            var missions = (JsonObject)(await client.CallAsync("get_missions"))["result"]!;
            var missionArray = (JsonArray)missions["missions"]!;
            Check(missionArray.Count > 0, "get_missions returns contracts");
            var missionId = missionArray[0]!["id"]!.GetValue<string>();
            var accepted = (JsonObject)(await client.CallAsync("accept_mission",
                Args(("id", (JsonNode?)missionId))))["result"]!;
            Check(accepted["ok"]!.GetValue<bool>(), "accept_mission accepts a contract");
            var claimedEarly = (JsonObject)(await client.CallAsync("claim_mission",
                Args(("id", (JsonNode?)missionId))))["result"]!;
            Check(!claimedEarly["ok"]!.GetValue<bool>(), "claim_mission refuses an unfinished contract");

            // get_relations：三大派系初始中立。
            var relations = (JsonObject)(await client.CallAsync("get_relations"))["result"]!;
            var factionArray = (JsonArray)relations["factions"]!;
            Check(factionArray.Count == 3, "get_relations covers the three major factions");
            foreach (var node in factionArray)
                Check(node!["state"]!.GetValue<string>() == "中立", "major factions start neutral");

            // set_flight：注入持续推力，物理帧推进后位置/速度变化。
            var flight = (JsonObject)(await client.CallAsync("set_flight",
                Args(("forward", (JsonNode?)1f))))["result"]!;
            Check(flight["forward"]!.GetValue<float>() == 1f, "set_flight clamps and stores controls");
            for (var i = 0; i < 40; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            state = (JsonObject)(await client.CallAsync("get_state"))["result"]!;
            var movedX = state["position"]!["x"]!.GetValue<float>();
            var movedY = state["position"]!["y"]!.GetValue<float>();
            Check(MathF.Abs(movedX - startX) + MathF.Abs(movedY - startY) > 1f, "injected thrust moves the ship");
            Check(state["speed"]!.GetValue<float>() > 0, "injected thrust builds speed");

            // pause/step：暂停后位置冻结，step 预算恢复推进。
            await client.CallAsync("set_flight", Args(("forward", (JsonNode?)0f)));
            await client.CallAsync("pause", Args(("paused", (JsonNode?)true)));
            state = (JsonObject)(await client.CallAsync("get_state"))["result"]!;
            Check(state["paused"]!.GetValue<bool>(), "pause freezes the world");
            var freezeX = state["position"]!["x"]!.GetValue<float>();
            var freezeY = state["position"]!["y"]!.GetValue<float>();
            for (var i = 0; i < 30; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            state = (JsonObject)(await client.CallAsync("get_state"))["result"]!;
            Check(state["position"]!["x"]!.GetValue<float>() == freezeX &&
                  state["position"]!["y"]!.GetValue<float>() == freezeY, "paused world does not advance");
            await client.CallAsync("step", Args(("seconds", (JsonNode?)0.5f)));
            for (var i = 0; i < 30; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            state = (JsonObject)(await client.CallAsync("get_state"))["result"]!;
            Check(state["position"]!["x"]!.GetValue<float>() != freezeX ||
                  state["position"]!["y"]!.GetValue<float>() != freezeY, "step budget advances the world");
            await client.CallAsync("pause", Args(("paused", (JsonNode?)false)));
            state = (JsonObject)(await client.CallAsync("get_state"))["result"]!;
            Check(!state["paused"]!.GetValue<bool>(), "pause resumes the world");

            // begin_warp：充能并锁飞行，cancel 随时可退。
            var warp = (JsonObject)(await client.CallAsync("begin_warp",
                Args(("cell", (JsonNode?)new JsonObject { ["x"] = 1, ["y"] = 0 }))))["result"]!;
            Check(warp["engaged"]!.GetValue<bool>(), "begin_warp engages the hyperdrive");
            state = (JsonObject)(await client.CallAsync("get_state"))["result"]!;
            Check(state["warp"]!["state"]!.GetValue<string>() != "ready", "warp leaves the ready phase");
            Check(state["warp"]!["destination"]!["x"]!.GetValue<int>() == 1, "warp reports the destination");
            var cancelled = (JsonObject)(await client.CallAsync("cancel_warp"))["result"]!;
            Check(cancelled["ok"]!.GetValue<bool>(), "cancel_warp aborts the flight");

            // list_techs/unlock_tech：解锁一个未解密模块并复核。
            var techs = (JsonObject)(await client.CallAsync("list_techs"))["result"]!;
            var techArray = (JsonArray)techs["techs"]!;
            string? lockedId = null;
            foreach (var node in techArray)
                if (node!["unlocked"]!.GetValue<bool>() == false) { lockedId = node["id"]!.GetValue<string>(); break; }
            if (lockedId is not null)
            {
                var unlocked = (JsonObject)(await client.CallAsync("unlock_tech",
                    Args(("id", (JsonNode?)lockedId))))["result"]!;
                Check(unlocked["unlocked"]!.GetValue<bool>(), "unlock_tech unlocks a locked module");
                techs = (JsonObject)(await client.CallAsync("list_techs"))["result"]!;
                techArray = (JsonArray)techs["techs"]!;
                var found = false;
                foreach (var node in techArray)
                    if (node!["id"]!.GetValue<string>() == lockedId) found = node["unlocked"]!.GetValue<bool>();
                Check(found, "list_techs reflects the new unlock");
            }

            // 未知命令：结构化报错。
            var unknown = await client.CallAsync("does_not_exist");
            Check(unknown["ok"]!.GetValue<bool>() == false &&
                  unknown["error"]!.GetValue<string>().Contains("unknown"), "unknown commands report an error");

            GD.Print($"AI_BRIDGE_CHECKS PASS ({_checks} checks)");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PrintErr($"AI_BRIDGE_CHECKS FAIL ({_checks} checks): {error.Message}\n{error.StackTrace}");
            GetTree().Quit(1);
        }
    }

    /// <summary>同步语义的阻塞客户端仅用于协程外部；测试内一律用 CallAsync 交给线程池，
    /// 让主线程的 _Process（AiBridge 命令队列）持续推进。</summary>
    private sealed class GameClient : IDisposable
    {
        private readonly TcpClient _tcp = new();
        private readonly StreamWriter _writer;
        private readonly StreamReader _reader;
        private long _id;

        public GameClient(int port)
        {
            _tcp.Connect("127.0.0.1", port);
            var encoding = new UTF8Encoding(false);
            _writer = new StreamWriter(_tcp.GetStream(), encoding) { AutoFlush = true };
            _reader = new StreamReader(_tcp.GetStream(), encoding);
        }

        public async Task<JsonObject> CallAsync(string command, JsonObject? args = null)
        {
            var request = new JsonObject { ["id"] = ++_id, ["cmd"] = command, ["args"] = args ?? new JsonObject() };
            await _writer.WriteLineAsync(request.ToJsonString());
            string? line;
            while ((line = await _reader.ReadLineAsync()) is not null)
            {
                if (line.Trim().Length == 0) continue;
                if (JsonNode.Parse(line) is not JsonObject response)
                    throw new InvalidOperationException($"malformed response: {line}");
                if (response["id"]!.GetValue<long>() != _id)
                    throw new InvalidOperationException($"response id mismatch: {line}");
                return response;
            }
            throw new InvalidOperationException("connection closed before a response arrived");
        }

        public void Dispose()
        {
            _writer.Dispose();
            _reader.Dispose();
            _tcp.Dispose();
        }
    }
}
