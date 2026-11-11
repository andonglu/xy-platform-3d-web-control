using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DispenseHost.CadImport;

namespace DispenseHost.Net;

// ---------------------------------------------------------------------------
// 服务端下发消息的结构化 DTO (供上位机 UI / 自动测试消费)
// ---------------------------------------------------------------------------

public sealed record AxisStateDto(
    double Pos, double Vel, string State,
    bool Home, bool LimPos, bool LimNeg);

public sealed record MachineStateDto(
    double T,
    AxisStateDto X, AxisStateDto Y, AxisStateDto Z,
    bool Valve, string Mode,
    bool Collision, string CollisionMsg, bool Homing)
{
    public static readonly MachineStateDto Empty = new(
        0,
        new AxisStateDto(0, 0, "Offline", false, false, false),
        new AxisStateDto(0, 0, "Offline", false, false, false),
        new AxisStateDto(0, 0, "Offline", false, false, false),
        false, "Offline", false, "", false);
}

public sealed record AxisConfigDto(
    string Name, double TravelMin, double TravelMax,
    double HomePos, double LimitNeg, double LimitPos,
    double HomeSensor, double HomeVel);

public sealed record PathPreviewDto(string Name, List<Point3D> Points);

public sealed record ForbiddenZoneDto(
    string Name, double XMin, double XMax,
    double YMin, double YMax, double ZMin, double ZMax);

/// <summary>
/// 上位机 WebSocket 客户端: 把规划好的路径上传到服务端 (端口 8530)。
///
/// 与激光项目 (8520) 完全独立，可同时连接。
/// 增强点 (GUI 上位机 / 自动测试需要):
///   - 大消息按帧字节拼接后再 UTF8 解码 (避免多字节汉字跨帧被切断)
///   - 结构化 state / path / config / zones 事件
///   - 发送串行化 (SemaphoreSlim), 避免并发 SendAsync 冲突
///   - UploadStepsAsync 支持每段独立 feed / valveOn
/// </summary>
public sealed class DispenseClient : IDisposable
{
    private readonly string _url;
    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public event Action<string>? OnLog;
    public event Action<bool>? OnConnected;
    public event Action<string>? OnEvent;             // 格式化后的事件 "[level] msg"
    public event Action<string, string>? OnEventData; // (level, msg) 原始
    public event Action<string>? OnCollision;         // 撞机报警
    public event Action<string>? OnConfig;            // 可读的轴配置文本 (兼容 CLI)
    public event Action<MachineStateDto>? OnState;
    public event Action<PathPreviewDto>? OnPath;
    public event Action<AxisConfigDto[]>? OnConfigData;
    public event Action<ForbiddenZoneDto[]>? OnZones;

    public bool IsConnected => _ws?.State == WebSocketState.Open;
    public MachineStateDto LastState { get; private set; } = MachineStateDto.Empty;

    public DispenseClient(string url = "ws://localhost:8530/ws") => _url = url;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected) return;
        _ws?.Dispose();
        _ws = new ClientWebSocket();
        // 绕过系统代理 (避免 HTTPS_PROXY 导致连接失败)
        _ws.Options.Proxy = null;
        await _ws.ConnectAsync(new Uri(_url), ct);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        OnConnected?.Invoke(true);
        OnLog?.Invoke($"已连接服务端 {_url}");
        _ = Task.Run(() => ReceiveLoop(_cts.Token));
    }

    public async Task DisconnectAsync()
    {
        var ws = _ws;
        if (ws is null) return;
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { }
        _cts?.Cancel();
        _ws = null;
        OnConnected?.Invoke(false);
        LastState = MachineStateDto.Empty;
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        try
        {
            while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult res;
                do
                {
                    res = await _ws.ReceiveAsync(buf, ct);
                    if (res.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf, 0, res.Count);
                } while (!res.EndOfMessage);

                // 按字节拼接后一次性解码, 避免 UTF8 多字节字符跨帧被切断
                var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                HandleMessage(text);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex) { OnLog?.Invoke($"接收异常: {ex.Message}"); }
        if (_ws?.State == WebSocketState.Open) OnConnected?.Invoke(false);
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
                {
                    var level = root.GetProperty("level").GetString() ?? "info";
                    var msg = root.GetProperty("msg").GetString() ?? "";
                    OnEvent?.Invoke($"[{level}] {msg}");
                    OnEventData?.Invoke(level, msg);
                    if (level == "error" && msg.Contains("撞机"))
                        OnCollision?.Invoke(msg);
                    break;
                }

                case "state":
                {
                    var st = ParseState(root);
                    LastState = st;
                    OnState?.Invoke(st);
                    break;
                }

                case "path":
                {
                    var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var pts = new List<Point3D>();
                    if (root.TryGetProperty("points3d", out var arr))
                    {
                        foreach (var p in arr.EnumerateArray())
                        {
                            if (p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 3)
                                pts.Add(new Point3D(p[0].GetDouble(), p[1].GetDouble(), p[2].GetDouble()));
                        }
                    }
                    OnPath?.Invoke(new PathPreviewDto(name, pts));
                    break;
                }

                case "config":
                {
                    var list = new List<AxisConfigDto>();
                    var sb = new StringBuilder();
                    foreach (var ax in root.GetProperty("axes").EnumerateArray())
                    {
                        var c = new AxisConfigDto(
                            ax.GetProperty("name").GetString() ?? "",
                            ax.GetProperty("travelMin").GetDouble(),
                            ax.GetProperty("travelMax").GetDouble(),
                            ax.GetProperty("homePos").GetDouble(),
                            ax.GetProperty("limitNeg").GetDouble(),
                            ax.GetProperty("limitPos").GetDouble(),
                            ax.GetProperty("homeSensor").GetDouble(),
                            ax.TryGetProperty("homeVel", out var hv) ? hv.GetDouble() : 20);
                        list.Add(c);
                        sb.AppendLine($"  {c.Name}: 行程 {c.TravelMin}~{c.TravelMax}, " +
                                      $"零点 {c.HomePos}, 传感器 N{c.LimitNeg}/H{c.HomeSensor}/P{c.LimitPos}");
                    }
                    OnConfig?.Invoke(sb.ToString());
                    OnConfigData?.Invoke(list.ToArray());
                    break;
                }

                case "zones":
                {
                    var list = new List<ForbiddenZoneDto>();
                    foreach (var z in root.GetProperty("zones").EnumerateArray())
                        list.Add(new ForbiddenZoneDto(
                            z.GetProperty("name").GetString() ?? "",
                            z.GetProperty("xMin").GetDouble(), z.GetProperty("xMax").GetDouble(),
                            z.GetProperty("yMin").GetDouble(), z.GetProperty("yMax").GetDouble(),
                            z.GetProperty("zMin").GetDouble(), z.GetProperty("zMax").GetDouble()));
                    OnZones?.Invoke(list.ToArray());
                    OnEvent?.Invoke($"[info] 禁止区 {list.Count} 个");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"消息解析失败: {ex.Message}");
        }
    }

    private static MachineStateDto ParseState(JsonElement root)
    {
        static bool Sensor(JsonElement e, string name) =>
            e.TryGetProperty("sensors", out var s) && s.TryGetProperty(name, out var v) && v.GetBoolean();

        static AxisStateDto Axis(JsonElement e) => new(
            e.TryGetProperty("pos", out var p) ? p.GetDouble() : 0,
            e.TryGetProperty("vel", out var v) ? v.GetDouble() : 0,
            e.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "",
            Sensor(e, "home"), Sensor(e, "limPos"), Sensor(e, "limNeg"));

        return new MachineStateDto(
            root.TryGetProperty("t", out var t) ? t.GetDouble() : 0,
            Axis(root.GetProperty("x")), Axis(root.GetProperty("y")), Axis(root.GetProperty("z")),
            root.TryGetProperty("valve", out var va) && va.GetBoolean(),
            root.TryGetProperty("mode", out var m) ? m.GetString() ?? "" : "",
            root.TryGetProperty("collision", out var c) && c.GetBoolean(),
            root.TryGetProperty("collisionMsg", out var cm) ? cm.GetString() ?? "" : "",
            root.TryGetProperty("homing", out var hm) && hm.GetBoolean());
    }

    // ---------------- 上传 ----------------

    public sealed record UploadStep(string Kind, List<Point3D> Points, double Feed, bool ValveOn);

    /// <summary>上传点胶路径 (沿用 CLI 接口, 统一进给速度)</summary>
    public async Task UploadPathsAsync(string jobName, List<List<Point3D>> paths, double feed = 80)
        => await UploadStepsAsync(jobName,
            paths.Where(p => p.Count >= 2).Select(p => new UploadStep("dispense", p, feed, true)).ToList());

    /// <summary>上传作业: 每段可独立设置进给速度与胶阀</summary>
    public async Task UploadStepsAsync(string jobName, List<UploadStep> steps)
    {
        if (!IsConnected) { OnLog?.Invoke("未连接服务端，无法上传"); return; }

        var payloadSteps = steps.Select(s => new
        {
            kind = s.Kind,
            points = s.Points.Select(p => new[] { p.X, p.Y, p.Z }).ToArray(),
            feed = s.Feed,
            valveOn = s.ValveOn,
        }).ToArray();

        var payload = JsonSerializer.Serialize(new { type = "upload", name = jobName, steps = payloadSteps }, JsonOpts);
        await SendTextAsync(payload);
        int pts = steps.Sum(s => s.Points.Count);
        OnLog?.Invoke($"路径已上传: {jobName}, {steps.Count} 段 / {pts} 点 / {payload.Length / 1024.0:F1} KB");
    }

    // ---------------- 指令 ----------------

    public async Task RunJobAsync()
    {
        await SendAsync(new { type = "run" });
        OnLog?.Invoke("已发送执行指令");
    }

    public async Task HomeAsync() => await SendAsync(new { type = "home" });
    public async Task EstopAsync() => await SendAsync(new { type = "estop" });
    public async Task ResetAlarmAsync() => await SendAsync(new { type = "reset_alarm" });
    public async Task ValveAsync(bool on) => await SendAsync(new { type = "valve", on });
    public async Task PtpAsync(string axis, double pos) => await SendAsync(new { type = "ptp", axis, pos });
    public async Task JogAsync(string axis, int dir) => await SendAsync(new { type = "jog", axis, dir });
    public async Task ListZonesAsync() => await SendAsync(new { type = "list_zones" });

    public async Task AddZoneAsync(string name, double xmin, double xmax,
        double ymin, double ymax, double zmin, double zmax)
        => await SendAsync(new { type = "add_zone", name, xmin, xmax, ymin, ymax, zmin, zmax });

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

    private Task SendAsync(object msg) => SendTextAsync(JsonSerializer.Serialize(msg, JsonOpts));

    private async Task SendTextAsync(string text)
    {
        var ws = _ws;
        if (ws?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        await _sendLock.WaitAsync();
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally { _sendLock.Release(); }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _ws?.Dispose();
        _sendLock.Dispose();
    }
}
