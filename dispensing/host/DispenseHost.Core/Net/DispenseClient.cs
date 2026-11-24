using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DispenseHost.CadImport;
using DispenseHost.PathPlan;

namespace DispenseHost.Net;

// ---------------------------------------------------------------------------
// 服务端下发消息的结构化 DTO (供上位机 UI / 自动测试消费)
// ---------------------------------------------------------------------------

public sealed record AxisStateDto(
    double Pos, double Vel, string State,
    bool Home, bool LimPos, bool LimNeg);

public sealed record RtcpStateDto(bool Enabled, bool Calibrated, double ResidualMm, double ToolLength);

public sealed record MachineStateDto(
    double T,
    AxisStateDto X, AxisStateDto Y, AxisStateDto Z,
    bool Valve, string Mode,
    bool Collision, string CollisionMsg, bool Homing,
    AxisStateDto? A = null, AxisStateDto? C = null,   // P5: A/C 摆头轴 (五轴机型)
    RtcpStateDto? Rtcp = null)                        // P5: RTCP 状态 (enabled/calibrated/residual/toolLength)
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

/// <summary>P9: 模型与生产配置/命令的明确应答。</summary>
public sealed record ModelAckDto(string Type, string ModelId, bool Ok, string? Error);
public sealed record ProductionAckDto(string Type, string ProjectId, long Revision, bool Ok, string? Error);

/// <summary>P9: 服务端生产状态快照，是上位机生产状态显示的唯一来源。</summary>
public sealed record ProductionSnapshotDto(
    string Phase, string? RunId, int PieceIndex, int CompletedCount, int BatchCount,
    double Progress, bool PartPresent, bool Clamped, Pose3D WorkpiecePose,
    string? Error, bool Paused, bool CanRetry);

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
    private readonly SemaphoreSlim _productionIoLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AckResult>> _pendingAcks = new();
    private CancellationTokenSource? _cts;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
    private sealed record AckResult(bool Ok, string? Error);

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
    public event Action<ProgramInfoDto[]>? OnPrograms;   // program_list 应答
    public event Action<ProductionSnapshotDto>? OnProduction;

    public sealed record ProgramInfoDto(int Id, string Name, int StepCount, int PointCount, DateTimeOffset SavedUtc);

    public bool IsConnected => _ws?.State == WebSocketState.Open;
    public MachineStateDto LastState { get; private set; } = MachineStateDto.Empty;
    public ProductionSnapshotDto? LastProduction { get; private set; }

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
        FailPendingAcks("连接已断开");
        OnConnected?.Invoke(false);
        LastState = MachineStateDto.Empty;
        LastProduction = null;
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
        FailPendingAcks("接收循环结束，同步或生产命令未完成");
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

                case "programs":
                {
                    var list = new List<ProgramInfoDto>();
                    foreach (var p in root.GetProperty("programs").EnumerateArray())
                        list.Add(new ProgramInfoDto(
                            p.GetProperty("id").GetInt32(),
                            p.GetProperty("name").GetString() ?? "",
                            p.TryGetProperty("stepCount", out var sc) ? sc.GetInt32() : 0,
                            p.TryGetProperty("pointCount", out var pc) ? pc.GetInt32() : 0,
                            p.TryGetProperty("savedUtc", out var su) && su.TryGetDateTimeOffset(out var dt) ? dt : DateTimeOffset.MinValue));
                    OnPrograms?.Invoke(list.ToArray());
                    break;
                }

                case "model_ack":
                {
                    string modelId = root.GetProperty("modelId").GetString() ?? "";
                    bool ok = root.TryGetProperty("ok", out var success) && success.GetBoolean();
                    string? error = root.TryGetProperty("error", out var err) ? err.GetString() : null;
                    TryCompleteAck("model:" + modelId, new AckResult(ok, error));
                    break;
                }

                case "production_ack":
                {
                    string projectId = root.GetProperty("projectId").GetString() ?? "";
                    long revision = root.TryGetProperty("revision", out var rev) ? rev.GetInt64() : 0;
                    bool ok = root.TryGetProperty("ok", out var success) && success.GetBoolean();
                    string? error = root.TryGetProperty("error", out var err) ? err.GetString() : null;
                    TryCompleteAck(ProductionAckKey(projectId, revision), new AckResult(ok, error));
                    break;
                }

                case "production":
                {
                    var snapshot = JsonSerializer.Deserialize<ProductionSnapshotDto>(root.GetRawText(), JsonOpts);
                    if (snapshot is not null)
                    {
                        LastProduction = snapshot;
                        OnProduction?.Invoke(snapshot);
                    }
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

        static RtcpStateDto? ParseRtcp(JsonElement e)
        {
            if (!e.TryGetProperty("rtcp", out var r)) return null;
            return new RtcpStateDto(
                r.TryGetProperty("enabled", out var en) && en.GetBoolean(),
                r.TryGetProperty("calibrated", out var ca) && ca.GetBoolean(),
                r.TryGetProperty("residualMm", out var re) ? re.GetDouble() : 0,
                r.TryGetProperty("toolLength", out var tl) ? tl.GetDouble() : 60);
        }

        return new MachineStateDto(
            root.TryGetProperty("t", out var t) ? t.GetDouble() : 0,
            Axis(root.GetProperty("x")), Axis(root.GetProperty("y")), Axis(root.GetProperty("z")),
            root.TryGetProperty("valve", out var va) && va.GetBoolean(),
            root.TryGetProperty("mode", out var m) ? m.GetString() ?? "" : "",
            root.TryGetProperty("collision", out var c) && c.GetBoolean(),
            root.TryGetProperty("collisionMsg", out var cm) ? cm.GetString() ?? "" : "",
            root.TryGetProperty("homing", out var hm) && hm.GetBoolean(),
            root.TryGetProperty("a", out var a2) ? Axis(a2) : null,
            root.TryGetProperty("c", out var c2) ? Axis(c2) : null,
            ParseRtcp(root));
    }

    // ---------------- 上传 ----------------

    /// <summary>
    /// 上传步骤: 每段独立进给/胶阀, 可选携带指令级出胶时序参数
    /// (openDelayMs/earlyCloseMs/closeDelayMs/retractMm, 服务端 P1 起支持, 默认 0 = 原行为)。
    /// </summary>
    public sealed record UploadStep(string Kind, List<Point3D> Points, double Feed, bool ValveOn,
        double OpenDelayMs = 0, double EarlyCloseMs = 0, double CloseDelayMs = 0, double RetractMm = 0,
        List<Point3D>? Normals = null);   // P5: 与 Points 一一对应的曲面法向 (RTCP 五轴, 服务端 P4 起支持)

    /// <summary>上传点胶路径 (沿用 CLI 接口, 统一进给速度)</summary>
    public async Task UploadPathsAsync(string jobName, List<List<Point3D>> paths, double feed = 80)
        => await UploadStepsAsync(jobName,
            paths.Where(p => p.Count >= 2).Select(p => new UploadStep("dispense", p, feed, true)).ToList());

    /// <summary>上传作业: 每段可独立设置进给速度与胶阀</summary>
    public async Task UploadStepsAsync(string jobName, List<UploadStep> steps)
    {
        if (!IsConnected) { OnLog?.Invoke("未连接服务端，无法上传"); return; }

        // 时序字段默认 0 不下发, 保持旧服务端兼容
        var payloadSteps = steps.Select(s =>
        {
            var dict = new Dictionary<string, object?>
            {
                ["kind"] = s.Kind,
                ["points"] = s.Points.Select(p => new[] { p.X, p.Y, p.Z }).ToArray(),
                ["feed"] = s.Feed,
                ["valveOn"] = s.ValveOn,
            };
            if (s.OpenDelayMs > 0) dict["openDelayMs"] = s.OpenDelayMs;
            if (s.EarlyCloseMs > 0) dict["earlyCloseMs"] = s.EarlyCloseMs;
            if (s.CloseDelayMs > 0) dict["closeDelayMs"] = s.CloseDelayMs;
            if (s.RetractMm > 0) dict["retractMm"] = s.RetractMm;
            if (s.Normals is not null) dict["normals"] = s.Normals.Select(n => new[] { n.X, n.Y, n.Z }).ToArray();
            return dict;
        }).ToArray();

        var payload = JsonSerializer.Serialize(new { type = "upload", name = jobName, steps = payloadSteps }, JsonOpts);
        await SendTextAsync(payload);
        int pts = steps.Sum(s => s.Points.Count);
        OnLog?.Invoke($"路径已上传: {jobName}, {steps.Count} 段 / {pts} 点 / {payload.Length / 1024.0:F1} KB");
    }

    // ---------------- P9 原始模型与生产配方 ----------------

    /// <summary>原始 STL 按 48KiB 字节分片同步；begin、每个 chunk、commit 均等待服务端 ACK。</summary>
    public async Task UploadModelAsync(ModelAsset model, TimeSpan? ackTimeout = null, CancellationToken ct = default)
    {
        if (model is null) throw new ArgumentNullException(nameof(model));
        model.ValidateContent();
        byte[] bytes = model.GetBytes();
        if (bytes.Length == 0) throw new InvalidDataException("不能同步空模型");
        if (string.IsNullOrWhiteSpace(model.Id) || string.IsNullOrWhiteSpace(model.FileName))
            throw new InvalidDataException("模型 ID 和文件名不能为空");

        await _productionIoLock.WaitAsync(ct);
        try
        {
            int totalChunks = (bytes.Length + 49152 - 1) / 49152;
            await SendWithAckAsync(new
            {
                type = "model_begin", cmd = "model_begin", modelId = model.Id,
                fileName = model.FileName, unit = model.Unit, sha256 = model.Sha256,
                totalBytes = (long)bytes.Length, totalChunks,
            }, "model:" + model.Id, "model_begin", ackTimeout, ct);

            for (int index = 0; index < totalChunks; index++)
            {
                int offset = index * 49152;
                int count = Math.Min(49152, bytes.Length - offset);
                string data = Convert.ToBase64String(bytes, offset, count);
                await SendWithAckAsync(new
                {
                    type = "model_chunk", cmd = "model_chunk", modelId = model.Id, index, data,
                }, "model:" + model.Id, $"model_chunk[{index}]", ackTimeout, ct);
            }
            await SendWithAckAsync(new { type = "model_commit", cmd = "model_commit", modelId = model.Id },
                "model:" + model.Id, "model_commit", ackTimeout, ct);
            OnLog?.Invoke($"模型同步成功: {model.FileName}, {bytes.Length:N0} 字节 / {totalChunks} 分片");
        }
        finally { _productionIoLock.Release(); }
    }

    /// <summary>上传完整生产配置及由现有 UploadStep 组成的路径，等待相同工程修订号 ACK。</summary>
    public async Task UploadProductionConfigAsync(string projectId, long revision, string modelId,
        ProductionConfig config, List<UploadStep> steps, TimeSpan? ackTimeout = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(modelId))
            throw new ArgumentException("生产工程 ID 和模型 ID 不能为空");
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        if (steps.Count == 0) throw new InvalidDataException("生产配置至少需要一个有效路径步骤");
        var payloadSteps = steps.Select(s => BuildProductionStep(s)).ToArray();

        await _productionIoLock.WaitAsync(ct);
        try
        {
            await SendWithAckAsync(new
            {
                type = "production_config", cmd = "production_config", projectId, revision, modelId, config,
                steps = payloadSteps,
            }, ProductionAckKey(projectId, revision), "production_config", ackTimeout, ct);
            OnLog?.Invoke($"生产配置同步成功: {config.Name}, 修订 {revision}, {steps.Count} 段");
        }
        finally { _productionIoLock.Release(); }
    }

    public Task ProductionStartAsync(string projectId, long revision, CancellationToken ct = default)
        => ProductionCommandAsync("production_start", projectId, revision, null, ct);
    public Task ProductionPauseAsync(string projectId, long revision, CancellationToken ct = default)
        => ProductionCommandAsync("production_pause", projectId, revision, null, ct);
    public Task ProductionResumeAsync(string projectId, long revision, CancellationToken ct = default)
        => ProductionCommandAsync("production_resume", projectId, revision, null, ct);
    public Task ProductionStopAsync(string projectId, long revision, CancellationToken ct = default)
        => ProductionCommandAsync("production_stop", projectId, revision, null, ct);
    public Task ProductionResetAsync(string projectId, long revision, CancellationToken ct = default)
        => ProductionCommandAsync("production_reset", projectId, revision, null, ct);
    public Task ProductionRetryAsync(string projectId, long revision, CancellationToken ct = default)
        => ProductionCommandAsync("production_retry", projectId, revision, null, ct);
    public Task ProductionStepAsync(string projectId, long revision, CancellationToken ct = default)
        => ProductionCommandAsync("production_step", projectId, revision, null, ct);
    public Task ProductionConfirmAsync(string projectId, long revision, string signal, CancellationToken ct = default)
        => ProductionCommandAsync("production_confirm", projectId, revision, signal, ct);

    private async Task ProductionCommandAsync(string command, string projectId, long revision,
        string? signal, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentException("生产工程 ID 不能为空", nameof(projectId));
        if (command == "production_confirm" && string.IsNullOrWhiteSpace(signal))
            throw new ArgumentException("人工确认信号不能为空", nameof(signal));
        await _productionIoLock.WaitAsync(ct);
        try
        {
            await SendWithAckAsync(new
            {
                type = command, cmd = command, projectId, revision, signal,
            }, ProductionAckKey(projectId, revision), command, null, ct);
            OnLog?.Invoke($"生产命令已接受: {command}{(signal is null ? "" : " / " + signal)}");
        }
        finally { _productionIoLock.Release(); }
    }

    private static Dictionary<string, object?> BuildProductionStep(UploadStep s)
    {
        var dict = new Dictionary<string, object?>
        {
            ["kind"] = s.Kind,
            ["points"] = s.Points.Select(p => new[] { p.X, p.Y, p.Z }).ToArray(),
            ["feed"] = s.Feed,
            ["valveOn"] = s.ValveOn,
            ["openDelayMs"] = s.OpenDelayMs,
            ["earlyCloseMs"] = s.EarlyCloseMs,
            ["closeDelayMs"] = s.CloseDelayMs,
            ["retractMm"] = s.RetractMm,
        };
        if (s.Normals is not null)
            dict["normals"] = s.Normals.Select(n => new[] { n.X, n.Y, n.Z }).ToArray();
        return dict;
    }

    private static string ProductionAckKey(string projectId, long revision) => $"production:{projectId}:{revision}";

    private async Task SendWithAckAsync(object message, string key, string operation,
        TimeSpan? timeout, CancellationToken ct)
    {
        if (!IsConnected) throw new InvalidOperationException("未连接服务端，无法执行 " + operation);
        var waiter = new TaskCompletionSource<AckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingAcks.TryAdd(key, waiter)) throw new InvalidOperationException("相同同步或生产命令正在等待 ACK");
        try
        {
            await SendTextAsync(JsonSerializer.Serialize(message, JsonOpts));
            var ack = await waiter.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(15), ct);
            if (!ack.Ok) throw new InvalidOperationException($"{operation} 被服务端拒绝: {ack.Error ?? "未知原因"}");
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{operation} 等待 ACK 超时", ex);
        }
        finally { _pendingAcks.TryRemove(key, out _); }
    }

    private void TryCompleteAck(string key, AckResult ack)
    {
        if (_pendingAcks.TryGetValue(key, out var waiter)) waiter.TrySetResult(ack);
    }

    private void FailPendingAcks(string reason)
    {
        foreach (var pair in _pendingAcks) pair.Value.TrySetException(new InvalidOperationException(reason));
        _pendingAcks.Clear();
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

    // ---------------- 脱机程序 + 虚拟 IO (P3) ----------------

    /// <summary>把当前步骤保存为脱机程序 (服务端原子落盘, 返回程序编号事件)</summary>
    public async Task ProgramSaveAsync(string name, List<UploadStep> steps)
    {
        var payloadSteps = steps.Select(s => new
        {
            kind = s.Kind,
            points = s.Points.Select(p => new[] { p.X, p.Y, p.Z }).ToArray(),
            feed = s.Feed,
            valveOn = s.ValveOn,
            openDelayMs = s.OpenDelayMs,
            earlyCloseMs = s.EarlyCloseMs,
            closeDelayMs = s.CloseDelayMs,
            retractMm = s.RetractMm,
            normals = s.Normals is null ? null : s.Normals.Select(n => new[] { n.X, n.Y, n.Z }).ToArray(),
        }).ToArray();
        await SendAsync(new { type = "program_save", name, steps = payloadSteps });
        OnLog?.Invoke($"程序保存已发送: {name}");
    }

    public async Task ProgramListAsync() => await SendAsync(new { type = "program_list" });
    public async Task ProgramLoadAsync(int id) => await SendAsync(new { type = "program_load", id });
    public async Task ProgramRunAsync(int id) => await SendAsync(new { type = "program_run", id });
    public async Task ProgramDeleteAsync(int id) => await SendAsync(new { type = "program_delete", id });
    public async Task IoWriteAsync(string id, bool on) => await SendAsync(new { type = "io_write", id, on });
    public async Task IoListAsync() => await SendAsync(new { type = "io_list" });

    // ---------------- 五轴 RTCP (P5) ----------------

    /// <summary>RTCP 五轴模式开关 (服务端作业空闲时才允许切换)</summary>
    public async Task RtcpAsync(bool on) => await SendAsync(new { type = "rtcp", on });

    /// <summary>RTCP 标定: 服务端多姿态探测 + 7 参数联合拟合, 残差经事件与快照下发</summary>
    public async Task RtcpCalibrateAsync() => await SendAsync(new { type = "rtcp_calibrate" });

    /// <summary>配置工具长度 (枢轴到针尖距离, mm)</summary>
    public async Task ConfigMachineAsync(double toolLengthMm)
        => await SendAsync(new { type = "config_machine", toolLengthMm });

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
        FailPendingAcks("客户端已释放");
        _ws?.Dispose();
        _sendLock.Dispose();
        _productionIoLock.Dispose();
    }
}
