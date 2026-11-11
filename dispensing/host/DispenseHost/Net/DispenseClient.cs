using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DispenseHost.CadImport;
using DispenseHost.PathPlan;

namespace DispenseHost.Net;

/// <summary>
/// 上位机 WebSocket 客户端: 把规划好的路径上传到服务端 (端口 8530)。
/// </summary>
public sealed class DispenseClient : IDisposable
{
    private readonly string _url;
    private ClientWebSocket? _ws;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public event Action<string>? OnLog;
    public event Action<bool>? OnConnected;
    public event Action<string>? OnEvent;     // 服务端事件消息
    public event Action<string>? OnCollision; // 撞机报警
    public event Action<string>? OnConfig;    // 服务端轴配置响应

    public DispenseClient(string url = "ws://localhost:8530/ws") => _url = url;

    public async Task ConnectAsync()
    {
        _ws = new ClientWebSocket();
        // 绕过系统代理 (避免 HTTPS_PROXY 导致连接失败)
        _ws.Options.Proxy = null;
        await _ws.ConnectAsync(new Uri(_url), CancellationToken.None);
        OnConnected?.Invoke(true);
        OnLog?.Invoke($"已连接服务端 {_url}");
        _ = Task.Run(ReceiveLoop);
    }

    private async Task ReceiveLoop()
    {
        var buf = new byte[64 * 1024];
        try
        {
            while (_ws?.State == WebSocketState.Open)
            {
                var res = await _ws.ReceiveAsync(buf, CancellationToken.None);
                if (res.MessageType == WebSocketMessageType.Close) break;
                var text = Encoding.UTF8.GetString(buf, 0, res.Count);
                HandleMessage(text);
            }
        }
        catch { }
        OnConnected?.Invoke(false);
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string type = root.GetProperty("type").GetString() ?? "";
            switch (type)
            {
                case "hello":
                    OnLog?.Invoke($"服务端: {root.GetProperty("server").GetString()}");
                    break;
                case "event":
                    var level = root.GetProperty("level").GetString();
                    var msg = root.GetProperty("msg").GetString();
                    OnEvent?.Invoke($"[{level}] {msg}");
                    if (level == "error" && msg?.Contains("撞机") == true)
                        OnCollision?.Invoke(msg);
                    break;
                case "state":
                    // 状态更新由 UI 层处理, 这里不处理
                    break;
                case "config":
                    var axesEl = root.GetProperty("axes");
                    var sb = new StringBuilder();
                    foreach (var ax in axesEl.EnumerateArray())
                    {
                        sb.AppendLine($"  {ax.GetProperty("name")}: 行程 {ax.GetProperty("travelMin")}~{ax.GetProperty("travelMax")}, " +
                                      $"零点 {ax.GetProperty("homePos")}, 传感器 N{ax.GetProperty("limitNeg")}/H{ax.GetProperty("homeSensor")}/P{ax.GetProperty("limitPos")}");
                    }
                    OnConfig?.Invoke(sb.ToString());
                    break;
            }
        }
        catch { }
    }

    /// <summary>上传点胶路径到服务端</summary>
    public async Task UploadPathsAsync(string jobName, List<List<Point3D>> paths, double feed = 80)
    {
        if (_ws?.State != WebSocketState.Open)
        {
            OnLog?.Invoke("未连接服务端，无法上传");
            return;
        }

        var steps = new List<object>();
        foreach (var path in paths)
        {
            if (path.Count < 2) continue;
            steps.Add(new
            {
                kind = "dispense",
                points = path.Select(p => new[] { p.X, p.Y, p.Z }).ToArray(),
                feed,
                valveOn = true
            });
        }

        var payload = JsonSerializer.Serialize(new { type = "upload", name = jobName, steps }, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(payload);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        OnLog?.Invoke($"路径已上传: {jobName}, {paths.Count} 段");
    }

    public async Task RunJobAsync()
    {
        await SendAsync(new { type = "run" });
        OnLog?.Invoke("已发送执行指令");
    }

    public async Task HomeAsync() => await SendAsync(new { type = "home" });
    public async Task EstopAsync() => await SendAsync(new { type = "estop" });
    public async Task ResetAlarmAsync() => await SendAsync(new { type = "reset_alarm" });

    /// <summary>配置服务端轴参数 (行程/零点/传感器位置)</summary>
    public async Task ConfigureAxisAsync(string axis, double travelMin, double travelMax,
        double homePos, double limitNeg, double limitPos, double homeSensor, double homeVel = 20)
    {
        await SendAsync(new
        {
            type = "config_axis",
            axis,
            travelMin, travelMax, homePos, limitNeg, limitPos, homeSensor, homeVel,
        });
        OnLog?.Invoke($"已配置轴 {axis}: 行程 {travelMin}~{travelMax}, 零点 {homePos}, 传感器 N{limitNeg}/H{homeSensor}/P{limitPos}");
    }

    /// <summary>查询服务端轴配置</summary>
    public async Task GetConfigAsync() => await SendAsync(new { type = "get_config" });

    private async Task SendAsync(object msg)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var text = JsonSerializer.Serialize(msg, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(text);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public void Dispose()
    {
        _ws?.Dispose();
    }
}
