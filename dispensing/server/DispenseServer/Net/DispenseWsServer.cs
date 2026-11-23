using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DispenseServer.Core;
using DispenseServer.Collision;

namespace DispenseServer.Net;

/// <summary>
/// 点胶服务端 WebSocket 服务 (端口 8530, 与激光 8520 分离)。
/// 
/// 下发: hello / state(30Hz) / path(3D) / event / collision_alarm
/// 接收:
///   home / ptp / jog / valve / upload(路径上传) / run / estop / reset_alarm
///   add_zone(添加禁止区) / list_zones
/// </summary>
public sealed class DispenseWsServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly DispenseController _controller;
    private readonly CollisionChecker _collision;
    private readonly ProgramStore? _store;
    private readonly IoController? _io;
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public DispenseWsServer(DispenseController controller, CollisionChecker collision,
        ProgramStore? store = null, IoController? io = null,
        string prefix = "http://localhost:8530/")
    {
        _controller = controller;
        _collision = collision;
        _store = store;
        _io = io;
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
                Console.WriteLine($"[ws-dispense] 客户端接入 {ctx.Request.RemoteEndPoint} (在线 {_clients.Count})");
                _ = SendTo(wsCtx.WebSocket, new { type = "hello", server = "DispenseServer .NET 8 (3D 点胶)", ver = 1 });
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
        var buf = new byte[256 * 1024]; // 256KB, 支持大路径上传
        try
        {
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var res = await ws.ReceiveAsync(buf, _cts.Token);
                if (res.MessageType == WebSocketMessageType.Close) break;

                // 大消息分片接收
                var sb = new StringBuilder();
                sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
                while (!res.EndOfMessage)
                {
                    res = await ws.ReceiveAsync(buf, _cts.Token);
                    sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
                }

                HandleCommand(sb.ToString());
            }
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        finally
        {
            _clients.TryRemove(id, out _);
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { }
            Console.WriteLine($"[ws-dispense] 客户端离开 (在线 {_clients.Count})");
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
                case "valve": _controller.SetValve(root.GetProperty("on").GetBoolean()); break;

                case "upload":
                    HandleUpload(root);
                    break;

                case "run": _controller.RunJob(); break;
                case "estop": _controller.EmergencyStop(); break;
                case "reset_alarm": _controller.ResetAlarm(); break;

                case "add_zone":
                    var zone = new CollisionChecker.Aabb(
                        root.GetProperty("xmin").GetDouble(),
                        root.GetProperty("xmax").GetDouble(),
                        root.GetProperty("ymin").GetDouble(),
                        root.GetProperty("ymax").GetDouble(),
                        root.GetProperty("zmin").GetDouble(),
                        root.GetProperty("zmax").GetDouble(),
                        root.GetProperty("name").GetString() ?? "zone");
                    _collision.AddForbiddenZone(zone);
                    Broadcast(new { type = "event", level = "ok", msg = $"禁止区已添加: {zone.Name}" });
                    break;

                case "list_zones":
                    var zones = _collision.GetForbiddenZones();
                    Broadcast(new { type = "zones", zones = zones.Select(z => new { z.Name, z.XMin, z.XMax, z.YMin, z.YMax, z.ZMin, z.ZMax }) });
                    break;

                case "config_axis":
                {
                    int axisIdx = Axes.FromName(root.GetProperty("axis").GetString() ?? "");
                    if (axisIdx < 0)
                    {
                        Broadcast(new { type = "event", level = "error", msg = "无效轴名" });
                        break;
                    }
                    var cfg = new AxisConfig(
                        TravelMin: root.GetProperty("travelMin").GetDouble(),
                        TravelMax: root.GetProperty("travelMax").GetDouble(),
                        HomePos: root.GetProperty("homePos").GetDouble(),
                        LimitNegative: root.GetProperty("limitNeg").GetDouble(),
                        LimitPositive: root.GetProperty("limitPos").GetDouble(),
                        HomeSensorPos: root.GetProperty("homeSensor").GetDouble(),
                        HomeVel: root.TryGetProperty("homeVel", out var hv) ? hv.GetDouble() : 20);
                    _controller.ConfigureAxis(axisIdx, cfg);
                    break;
                }

                case "get_config":
                    var configs = _controller.GetConfigs();
                    Broadcast(new
                    {
                        type = "config",
                        axes = configs.Select((c, i) => new
                        {
                            name = Axes.Names[i],
                            travelMin = c.TravelMin,
                            travelMax = c.TravelMax,
                            homePos = c.HomePos,
                            limitNeg = c.LimitNegative,
                            limitPos = c.LimitPositive,
                            homeSensor = c.HomeSensorPos,
                            homeVel = c.HomeVel,
                        }),
                    });
                    break;

                // ---------------- 脱机程序存储 (P3) ----------------
                case "program_save": HandleProgramSave(root); break;
                case "program_list":
                {
                    if (_store is null) { Broadcast(new { type = "event", level = "warn", msg = "程序仓库未启用" }); break; }
                    Broadcast(new
                    {
                        type = "programs",
                        programs = _store.List().Select(p => new
                        {
                            id = p.Id, name = p.Name,
                            stepCount = p.StepCount, pointCount = p.PointCount,
                            savedUtc = p.SavedUtc,
                        }),
                    });
                    break;
                }
                case "program_load": HandleProgramLoad(root, run: false); break;
                case "program_run": HandleProgramLoad(root, run: true); break;
                case "program_delete":
                {
                    if (_store is null) { Broadcast(new { type = "event", level = "warn", msg = "程序仓库未启用" }); break; }
                    int id = root.GetProperty("id").GetInt32();
                    if (_store.Delete(id)) Broadcast(new { type = "event", level = "ok", msg = $"程序 {id} 已删除" });
                    else Broadcast(new { type = "event", level = "error", msg = $"程序 {id} 不存在" });
                    break;
                }

                // ---------------- 五轴 RTCP (P4) ----------------
                case "rtcp":
                    _controller.SetRtcp(root.GetProperty("on").GetBoolean());
                    break;
                case "rtcp_calibrate":
                    _controller.CalibrateRtcp();
                    break;
                case "config_machine":
                {
                    // { toolLengthMm }
                    if (root.TryGetProperty("toolLengthMm", out var tl) && tl.GetDouble() > 0)
                    {
                        _controller.Kinematics.ToolLength = tl.GetDouble();
                        _controller.RealGeometry.ToolLength = tl.GetDouble();
                        Broadcast(new { type = "event", level = "ok", msg = $"工具长度已配置: {tl.GetDouble()}mm" });
                    }
                    break;
                }

                // ---------------- 虚拟 IO 注入 (P3) ----------------
                case "io_write":
                {
                    if (_io is null) { Broadcast(new { type = "event", level = "warn", msg = "IO 层未启用" }); break; }
                    string ioId = root.GetProperty("id").GetString() ?? "";
                    bool ioOn = root.GetProperty("on").GetBoolean();
                    string? err = _io.SetInput(ioId, ioOn);
                    if (err is not null) Broadcast(new { type = "event", level = "warn", msg = err });
                    break;
                }
                case "io_list":
                    Broadcast(new
                    {
                        type = "io_defs",
                        inputs = IoController.InputDefs,
                        outputs = IoController.OutputDefs,
                    });
                    break;

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

    /// <summary>program_save: 步骤 → 程序仓库 (原子落盘), 返回程序编号</summary>
    private void HandleProgramSave(JsonElement root)
    {
        if (_store is null) { Broadcast(new { type = "event", level = "warn", msg = "程序仓库未启用" }); return; }
        try
        {
            string name = root.GetProperty("name").GetString() ?? "program";
            var steps = ParseSteps(root.GetProperty("steps"));
            var program = _store.Save(name, steps);
            Broadcast(new { type = "event", level = "ok",
                msg = $"程序已保存: #{program.Id} {program.Name} ({program.StepCount} 步 / {program.PointCount} 点)" });
        }
        catch (Exception ex)
        {
            Broadcast(new { type = "event", level = "error", msg = $"程序保存失败: {ex.Message}" });
        }
    }

    /// <summary>program_load / program_run: 从仓库装载到控制器 (run=true 时立即执行)</summary>
    private void HandleProgramLoad(JsonElement root, bool run)
    {
        if (_store is null) { Broadcast(new { type = "event", level = "warn", msg = "程序仓库未启用" }); return; }
        int id = root.GetProperty("id").GetInt32();
        var program = _store.Get(id);
        if (program is null) { Broadcast(new { type = "event", level = "error", msg = $"程序 {id} 不存在" }); return; }
        var paths = JobBuilder.BuildPaths(program.Steps);
        if (paths.Count == 0) { Broadcast(new { type = "event", level = "error", msg = $"程序 {id} 无有效路径" }); return; }
        if (!_controller.UploadPaths(paths, PathSampler3D.Sample(paths.SelectMany(p => p.Segments)), program.Name)) return;
        Broadcast(new { type = "event", level = "ok", msg = $"程序 {id}《{program.Name}》已装载" });
        if (run)
        {
            _controller.RunJob();
        }
    }

    private static List<JobStepDto> ParseSteps(JsonElement stepsEl)
    {
        var steps = new List<JobStepDto>();
        foreach (var el in stepsEl.EnumerateArray())
        {
            steps.Add(new JobStepDto(
                Kind: el.GetProperty("kind").GetString() ?? "",
                Target: el.TryGetProperty("target", out var t) ? t.Deserialize<double[]>() : null,
                Points: el.TryGetProperty("points", out var p) ? p.Deserialize<double[][]>() : null,
                ArcVia: el.TryGetProperty("arcVia", out var av) ? av.Deserialize<double[]>() : null,
                Feed: el.TryGetProperty("feed", out var f) ? f.GetDouble() : 80,
                ValveOn: el.TryGetProperty("valveOn", out var v) ? v.GetBoolean() : true,
                DwellMs: el.TryGetProperty("dwellMs", out var d) ? d.GetDouble() : 0,
                OpenDelayMs: el.TryGetProperty("openDelayMs", out var od) ? od.GetDouble() : 0,
                EarlyCloseMs: el.TryGetProperty("earlyCloseMs", out var ec) ? ec.GetDouble() : 0,
                CloseDelayMs: el.TryGetProperty("closeDelayMs", out var cd) ? cd.GetDouble() : 0,
                RetractMm: el.TryGetProperty("retractMm", out var rt) ? rt.GetDouble() : 0,
                Normals: el.TryGetProperty("normals", out var nm) ? nm.Deserialize<double[][]>() : null));
        }
        return steps;
    }

    private void HandleUpload(JsonElement root)
    {
        var name = root.GetProperty("name").GetString() ?? "job";
        var steps = ParseSteps(root.GetProperty("steps"));
        var paths = JobBuilder.BuildPaths(steps);
        var preview = PathSampler3D.Sample(paths.SelectMany(p => p.Segments), 1.0);
        _controller.UploadPaths(paths, preview, name);
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
        // Stop/Close 幂等: 监听器可能已随 AcceptLoop 异常退出而释放
        try { _listener.Stop(); } catch (ObjectDisposedException) { }
        try { _listener.Close(); } catch (ObjectDisposedException) { }
    }
}
