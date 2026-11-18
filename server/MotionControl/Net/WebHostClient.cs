using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace MotionControl.Net;

/// <summary>
/// 上位机 WebSocket 客户端模式：连接 Node Web 服务并控制其中的模拟设备。
/// 不使用真实轴卡；原 WsServer 单机模式仍保留。
/// </summary>
public sealed class WebHostClient : IDisposable
{
    private readonly ClientWebSocket _ws = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task RunAsync(string url, string? command = null, int watchSeconds = 20)
    {
        await _ws.ConnectAsync(new Uri(url), CancellationToken.None);
        Console.WriteLine($"已连接 Web 服务: {url}");
        await Send(new { type = "register", role = "host" });

        using var cts = new CancellationTokenSource();
        var receiveTask = ReceiveLoop(cts.Token);
        if (command is not null)
        {
            var msg = ParseCommand(command);
            if (msg is null) throw new ArgumentException($"无法解析命令: {command}");
            await Send(msg);
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, watchSeconds)), cts.Token);
        }
        else
        {
            PrintHelp();
            while (_ws.State == WebSocketState.Open)
            {
                var line = Console.ReadLine();
                if (line is null || line.Trim().Equals("quit", StringComparison.OrdinalIgnoreCase)) break;
                if (line.Trim().Equals("help", StringComparison.OrdinalIgnoreCase)) { PrintHelp(); continue; }
                if (string.IsNullOrWhiteSpace(line)) continue;
                var msg = ParseCommand(line);
                if (msg is not null) await Send(msg);
            }
        }

        if (_ws.State == WebSocketState.Open)
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "上位机退出", CancellationToken.None);
        cts.Cancel();
        try { await receiveTask; } catch (OperationCanceledException) { }
    }

    private async Task Send(object msg)
    {
        var json = JsonSerializer.Serialize(msg, JsonOpts);
        Console.WriteLine($">> {json}");
        await _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        string lastState = "";
        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                using var packet = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    packet.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(packet.ToArray());
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var type = root.GetProperty("type").GetString();
                switch (type)
                {
                    case "hello":
                        Console.WriteLine($"<< 服务: {root.GetProperty("server").GetString()}");
                        break;
                    case "event":
                        Console.WriteLine($"<< [{root.GetProperty("level").GetString()}] {root.GetProperty("msg").GetString()}");
                        break;
                    case "path":
                        Console.WriteLine($"<< 路径: {root.GetProperty("name").GetString()}");
                        break;
                    case "state":
                    {
                        string mode = root.TryGetProperty("mode", out var m) ? m.GetString() ?? "" : "";
                        string phase = "", workpiece = "";
                        int completed = 0;
                        if (root.TryGetProperty("production", out var p))
                        {
                            phase = p.TryGetProperty("phase", out var ph) ? ph.GetString() ?? "" : "";
                            workpiece = p.TryGetProperty("workpieceId", out var w) ? w.GetString() ?? "" : "";
                            completed = p.TryGetProperty("completed", out var c) ? c.GetInt32() : 0;
                        }
                        string summary = $"{mode} | {phase} | {workpiece} | 完成 {completed}";
                        if (summary != lastState)
                        {
                            Console.WriteLine($"<< {summary}");
                            lastState = summary;
                        }
                        break;
                    }
                }
            }
        }
        catch (WebSocketException) when (ct.IsCancellationRequested) { }
    }

    private static object? ParseCommand(string line)
    {
        var p = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (p.Length == 0) return null;
        string cmd = p[0].ToLowerInvariant();
        switch (cmd)
        {
            case "home": return new { type = "home" };
            case "estop": return new { type = "estop" };
            case "stop": return new { type = "production_stop" };
            case "reset": return new { type = "production_reset" };
            case "star": case "circle": case "combo": return new { type = "job", name = cmd };
            case "laser" when p.Length == 2:
                if (p[1] is not ("on" or "off")) break;
                return new { type = "laser", on = p[1] == "on" };
            case "ptp" when p.Length == 3 && double.TryParse(p[2], out double pos):
                return new { type = "ptp", axis = p[1].ToLowerInvariant(), pos };
            case "jog" when p.Length == 3 && int.TryParse(p[2], out int dir):
                return new { type = "jog", axis = p[1].ToLowerInvariant(), dir };
            case "auto" when p.Length >= 2:
            {
                int count = 1;
                if (p.Length >= 3 && (!int.TryParse(p[2], out count) || count < 1)) break;
                return new { type = "production_start", recipe = p[1].ToLowerInvariant(), count };
            }
            case "continuous":
            {
                string recipe = p.Length >= 2 ? p[1].ToLowerInvariant() : "star";
                return new { type = "production_start", recipe, count = 0 };
            }
        }
        Console.WriteLine($"无效命令: {line}（输入 help 查看命令）");
        return null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("命令: home | star | circle | combo | laser on/off | ptp x 20 | jog x 1");
        Console.WriteLine("      auto star [数量] | continuous [star|circle|combo] | stop | reset | estop");
        Console.WriteLine("      help | quit");
    }

    public void Dispose() => _ws.Dispose();
}
