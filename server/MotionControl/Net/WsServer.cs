using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MotionControl.Core;

namespace MotionControl.Net;

/// <summary>
/// WebSocket 服务 (System.Net.WebSockets + HttpListener, 零第三方依赖)。
///   监听 ws://localhost:8520/ws
///   下发: hello / state(30Hz) / path / event   (JSON)
///   接收: home / ptp / jog / laser / job / estop
/// </summary>
public sealed class WsServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly MotionController _controller;
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public WsServer(MotionController controller, string prefix = "http://localhost:8520/")
    {
        _controller = controller;
        _listener.Prefixes.Add(prefix);

        _controller.OnState += msg => Broadcast(msg);
        _controller.OnPath += msg => Broadcast(msg);
        _controller.OnEvent += (level, text) => Broadcast(new { type = "event", level, msg = text });
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }

            if (ctx.Request.IsWebSocketRequest && ctx.Request.Url?.AbsolutePath == "/ws")
            {
                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                var id = Guid.NewGuid();
                _clients[id] = wsCtx.WebSocket;
                Console.WriteLine($"[ws] 客户端接入 {ctx.Request.RemoteEndPoint} (在线 {_clients.Count})");
                _ = SendTo(wsCtx.WebSocket, new { type = "hello", server = "MotionControl .NET 8 (模拟轴卡)", ver = 1 });
                _ = Task.Run(() => ReceiveLoop(id, wsCtx.WebSocket));
            }
            else
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
            }
        }
    }

    private async Task ReceiveLoop(Guid id, WebSocket ws)
    {
        var buf = new byte[8192];
        try
        {
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var res = await ws.ReceiveAsync(buf, _cts.Token);
                if (res.MessageType == WebSocketMessageType.Close) break;
                HandleCommand(Encoding.UTF8.GetString(buf, 0, res.Count));
            }
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        finally
        {
            _clients.TryRemove(id, out _);
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
            catch { }
            Console.WriteLine($"[ws] 客户端离开 (在线 {_clients.Count})");
        }
    }

    private void HandleCommand(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string type = root.GetProperty("type").GetString() ?? "";
            switch (type)
            {
                case "home": _controller.Home(); break;
                case "ptp":
                    _controller.Ptp(Axes.FromName(root.GetProperty("axis").GetString() ?? ""),
                                    root.GetProperty("pos").GetDouble());
                    break;
                case "jog":
                    _controller.Jog(Axes.FromName(root.GetProperty("axis").GetString() ?? ""),
                                    root.GetProperty("dir").GetInt32());
                    break;
                case "laser": _controller.SetLaser(root.GetProperty("on").GetBoolean()); break;
                case "job": _controller.StartJob(root.GetProperty("name").GetString() ?? ""); break;
                case "estop": _controller.Estop(); break;
                default:
                    Broadcast(new { type = "event", level = "warn", msg = $"未知指令: {type}" });
                    break;
            }
        }
        catch (Exception ex)
        {
            Broadcast(new { type = "event", level = "error", msg = $"指令解析失败: {ex.Message}" });
        }
    }

    public void Broadcast(object msg)
    {
        string text = JsonSerializer.Serialize(msg, JsonOpts);
        foreach (var (id, ws) in _clients)
        {
            if (ws.State != WebSocketState.Open) { _clients.TryRemove(id, out _); continue; }
            _ = SendTo(ws, text);
        }
    }

    private static async Task SendTo(WebSocket ws, object msg)
        => await SendTo(ws, JsonSerializer.Serialize(msg, JsonOpts));

    private static async Task SendTo(WebSocket ws, string text)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (WebSocketException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
    }
}
