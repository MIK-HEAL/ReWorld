using Godot;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

/// <summary>
/// 游戏内 TCP 桥：监听 127.0.0.1（默认 9817 端口，可用环境变量 REWORLD_AI_PORT 覆盖），
/// 按行收发 JSON 请求/响应，转发到 GameCommandApi。协议：
/// 请求 {"id":n,"cmd":"...","args":{...}}\n → 响应 {"id":n,"ok":true,"result":{...}}\n
/// 或 {"id":n,"ok":false,"error":"..."}\n。所有游戏状态读写都在主线程（_Process）执行，
/// 网络线程只做 IO，线程间经 ConcurrentQueue + TaskCompletionSource 交接。
/// </summary>
public sealed partial class AiBridge : Node
{
    public const int DefaultPort = 9817;
    public static AiBridge? Instance { get; private set; }

    private readonly GameCommandApi _api;
    private TcpListener? _listener;
    private readonly ConcurrentQueue<Request> _queue = new();

    private sealed record Request(long Id, string Command, JsonObject? Args, TaskCompletionSource<JsonObject> Completion);

    public AiBridge(UniverseView view)
    {
        _api = new GameCommandApi(view);
        Name = "AiBridge";
    }

    public override async void _Ready()
    {
        Instance = this;
        var port = DefaultPort;
        var configured = OS.GetEnvironment("REWORLD_AI_PORT");
        if (int.TryParse(configured, out var value) && value is > 0 and < 65536) port = value;
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
        }
        catch (Exception error)
        {
            GD.PushWarning($"[AiBridge] unable to listen on 127.0.0.1:{port}: {error.Message}");
            return;
        }
        GD.Print($"[AiBridge] listening on 127.0.0.1:{port}");
        _ = AcceptLoop();
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        _listener?.Stop();
        _listener = null;
    }

    private async Task AcceptLoop()
    {
        while (_listener is not null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch { return; }
            _ = ServeClient(client);
        }
    }

    private async Task ServeClient(TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, new UTF8Encoding(false));
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                while (client.Connected)
                {
                    var line = await reader.ReadLineAsync();
                    if (line is null) break;
                    line = line.Trim();
                    if (line.Length == 0) continue;
                    long id;
                    string command;
                    JsonObject? args;
                    try
                    {
                        if (JsonNode.Parse(line) is not JsonObject request)
                        {
                            await writer.WriteLineAsync(Error(0, "request must be a JSON object").ToJsonString());
                            continue;
                        }
                        id = request["id"]?.GetValue<long>() ?? 0;
                        command = request["cmd"]?.GetValue<string>() ?? "";
                        args = request["args"] as JsonObject;
                    }
                    catch (Exception malformed)
                    {
                        await writer.WriteLineAsync(Error(0, $"malformed request: {malformed.Message}").ToJsonString());
                        continue;
                    }
                    var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _queue.Enqueue(new Request(id, command, args, completion));
                    var response = await completion.Task;
                    await writer.WriteLineAsync(response.ToJsonString());
                }
            }
        }
        catch
        {
            // 客户端断开/网络异常属正常关闭路径：静默结束本连接的服务协程。
        }
    }

    public override void _Process(double delta)
    {
        while (_queue.TryDequeue(out var request))
        {
            JsonObject response;
            try
            {
                var result = _api.Execute(request.Command, request.Args);
                response = new JsonObject { ["id"] = request.Id, ["ok"] = true, ["result"] = result };
            }
            catch (Exception error)
            {
                response = new JsonObject { ["id"] = request.Id, ["ok"] = false, ["error"] = error.Message };
            }
            request.Completion.SetResult(response);
        }
    }

    private static JsonObject Error(long id, string message)
        => new() { ["id"] = id, ["ok"] = false, ["error"] = message };
}
