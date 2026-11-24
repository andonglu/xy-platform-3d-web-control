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
    private readonly ProductionStore? _productionStore;
    private readonly VirtualProductionController? _production;

    private sealed class ClientSession(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<Guid, ClientSession> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public DispenseWsServer(DispenseController controller, CollisionChecker collision,
        ProgramStore? store = null, IoController? io = null,
        string prefix = "http://localhost:8530/",
        ProductionStore? productionStore = null, VirtualProductionController? production = null)
    {
        _controller = controller;
        _collision = collision;
        _store = store;
        _io = io;
        _productionStore = productionStore;
        _production = production;
        _listener.Prefixes.Add(prefix);

        _controller.OnState += msg => Broadcast(msg);
        _controller.OnPath += msg => Broadcast(msg);
        _controller.OnEvent += (level, text) => Broadcast(new { type = "event", level, msg = text });
        if (_production is not null)
        {
            _production.OnSnapshot += snapshot => Broadcast(ProductionMessage(snapshot));
            _production.OnEvent += (level, text) => Broadcast(new { type = "event", level, msg = text });
        }
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
                var session = new ClientSession(wsCtx.WebSocket);
                _clients[id] = session;
                Console.WriteLine($"[ws-dispense] 客户端接入 {ctx.Request.RemoteEndPoint} (在线 {_clients.Count})");
                _ = Task.Run(() => ReceiveLoop(id, session));
            }
            else
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
            }
        }
    }

    private async Task ReceiveLoop(Guid id, ClientSession session)
    {
        var ws = session.Socket;
        var buf = new byte[256 * 1024]; // 256KB, 支持大路径与 48KiB 原始分片上传
        try
        {
            await SendTo(session, new { type = "hello", server = "DispenseServer .NET 8 (3D 点胶)", ver = 1 });
            await ReplayStoredData(session);
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

                HandleCommand(session, sb.ToString());
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

    private void HandleCommand(ClientSession source, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // 旧客户端使用 type，P9 host 固定使用 cmd；两者仅在接收端兼容。
            string type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "" :
                root.TryGetProperty("cmd", out var cmdEl) ? cmdEl.GetString() ?? "" : "";

            switch (type)
            {
                case "home": if (!RejectProductionConflict("home")) _controller.Home(); break;
                case "ptp":
                    if (RejectProductionConflict("ptp")) break;
                    _controller.Ptp(Axes.FromName(root.GetProperty("axis").GetString() ?? ""),
                                    root.GetProperty("pos").GetDouble());
                    break;
                case "jog":
                    if (RejectProductionConflict("jog")) break;
                    _controller.Jog(Axes.FromName(root.GetProperty("axis").GetString() ?? ""),
                                    root.GetProperty("dir").GetInt32());
                    break;
                case "valve":
                    if (!RejectProductionConflict("valve")) _controller.SetValve(root.GetProperty("on").GetBoolean());
                    break;

                case "upload":
                    if (!RejectProductionConflict("upload")) HandleUpload(root);
                    break;

                case "run": if (!RejectProductionConflict("run")) _controller.RunJob(); break;
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
                case "program_load": if (!RejectProductionConflict("program_load")) HandleProgramLoad(root, run: false); break;
                case "program_run": if (!RejectProductionConflict("program_run")) HandleProgramLoad(root, run: true); break;
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
                    if (!RejectProductionConflict("rtcp")) _controller.SetRtcp(root.GetProperty("on").GetBoolean());
                    break;
                case "rtcp_calibrate":
                    if (!RejectProductionConflict("rtcp_calibrate")) _controller.CalibrateRtcp();
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

                // ---------------- 原始模型与连续生产 (P9) ----------------
                case "model_begin": HandleModelBegin(source, root); break;
                case "model_chunk": HandleModelChunk(source, root); break;
                case "model_commit": HandleModelCommit(source, root); break;
                case "production_config": HandleProductionConfig(source, root); break;
                case "production_start":
                case "production_pause":
                case "production_resume":
                case "production_stop":
                case "production_reset":
                case "production_retry":
                case "production_step":
                case "production_confirm":
                    HandleProductionCommand(source, root, type);
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

    private bool RejectProductionConflict(string command)
    {
        if (_production?.IsActive != true) return false;
        Broadcast(new { type = "event", level = "warn",
            msg = $"生产状态 {_production.Snapshot.Phase} 中拒绝冲突指令 {command}" });
        return true;
    }

    private void HandleModelBegin(ClientSession source, JsonElement root)
    {
        string? modelId = null;
        try
        {
            var begin = root.Deserialize<ModelBeginDto>(JsonOpts) ?? throw new InvalidDataException("model_begin 内容为空");
            modelId = begin.ModelId;
            if (_productionStore is null) throw new InvalidOperationException("模型仓库未启用");
            _productionStore.BeginModel(begin);
            SendModelAck(source, modelId, true, null);
            Broadcast(new
            {
                type = "model_begin", cmd = "model_begin", begin.ModelId, begin.FileName, begin.Unit,
                begin.Sha256, begin.TotalBytes, begin.TotalChunks,
            });
        }
        catch (Exception ex) { SendModelAck(source, modelId, false, ex.Message); }
    }

    private void HandleModelChunk(ClientSession source, JsonElement root)
    {
        string? modelId = null;
        try
        {
            var chunk = root.Deserialize<ModelChunkDto>(JsonOpts) ?? throw new InvalidDataException("model_chunk 内容为空");
            modelId = chunk.ModelId;
            if (_productionStore is null) throw new InvalidOperationException("模型仓库未启用");
            _productionStore.AddModelChunk(chunk);
            SendModelAck(source, modelId, true, null);
            Broadcast(new { type = "model_chunk", cmd = "model_chunk", chunk.ModelId, chunk.Index, chunk.Data });
        }
        catch (Exception ex) { SendModelAck(source, modelId, false, ex.Message); }
    }

    private void HandleModelCommit(ClientSession source, JsonElement root)
    {
        string? modelId = null;
        try
        {
            modelId = root.GetProperty("modelId").GetString();
            if (string.IsNullOrWhiteSpace(modelId)) throw new InvalidDataException("模型 ID 不能为空");
            if (_productionStore is null) throw new InvalidOperationException("模型仓库未启用");
            _productionStore.CommitModel(modelId);
            SendModelAck(source, modelId, true, null);
            Broadcast(new { type = "model_commit", cmd = "model_commit", modelId });
        }
        catch (Exception ex) { SendModelAck(source, modelId, false, ex.Message); }
    }

    private static void SendModelAck(ClientSession source, string? modelId, bool ok, string? error)
        => _ = SendTo(source, new { type = "model_ack", modelId = modelId ?? "", ok, error = error ?? "" });

    private void HandleProductionConfig(ClientSession source, JsonElement root)
    {
        string projectId = "";
        long revision = 0;
        try
        {
            var envelope = root.Deserialize<ProductionEnvelope>(JsonOpts)
                           ?? throw new InvalidDataException("production_config 内容为空");
            projectId = envelope.ProjectId ?? "";
            revision = envelope.Revision;
            if (_productionStore is null || _production is null)
                throw new InvalidOperationException("生产仓库或控制器未启用");
            _production.Configure(envelope);       // 先完成路径预检，失败不写存储
            _productionStore.SaveRecipe(envelope); // 再原子保存完整工程
            _ = SendTo(source, new { type = "production_ack", projectId, revision, ok = true, error = "" });
            Broadcast(RecipeMessage(envelope));
        }
        catch (Exception ex)
        {
            _ = SendTo(source, new { type = "production_ack", projectId, revision, ok = false, error = ex.Message });
        }
    }

    private void HandleProductionCommand(ClientSession source, JsonElement root, string command)
    {
        string projectId = root.TryGetProperty("projectId", out var p) ? p.GetString() ?? "" : "";
        long revision = root.TryGetProperty("revision", out var r) && r.TryGetInt64(out var value) ? value : 0;
        bool ok = false;
        string error = "";
        try
        {
            if (_production is null) throw new InvalidOperationException("生产控制器未启用");
            var current = _production.CurrentEnvelope;
            if (!string.IsNullOrEmpty(projectId) && current is not null &&
                (!string.Equals(projectId, current.ProjectId, StringComparison.Ordinal) || revision != current.Revision))
                throw new InvalidOperationException($"工程修订不匹配，服务端当前为 {current.ProjectId} / {current.Revision}");
            ok = command switch
            {
                "production_start" => _production.Start(),
                "production_pause" => _production.Pause(),
                "production_resume" => _production.Resume(),
                "production_stop" => _production.Stop(),
                "production_reset" => ResetProduction(),
                "production_retry" => _production.Retry(),
                "production_step" => _production.Step(),
                "production_confirm" => _production.Confirm(root.GetProperty("signal").GetString() ?? ""),
                _ => false,
            };
            if (!ok) error = _production.Snapshot.Error ?? "当前生产状态不接受该命令";
            Broadcast(ProductionMessage(_production.Snapshot));
        }
        catch (Exception ex) { error = ex.Message; }
        _ = SendTo(source, new { type = "production_ack", projectId, revision, ok, error });
    }

    private bool ResetProduction()
    {
        _production!.Reset();
        return true;
    }

    private static object RecipeMessage(ProductionEnvelope envelope) => new
    {
        type = "production_config", cmd = "production_config", envelope.ProjectId, envelope.Revision,
        envelope.ModelId, envelope.Config, envelope.Steps,
    };

    private static object ProductionMessage(ProductionSnapshot snapshot) => new
    {
        type = "production", snapshot.Phase, snapshot.RunId, snapshot.PieceIndex,
        snapshot.CompletedCount, snapshot.BatchCount, snapshot.Progress,
        snapshot.PartPresent, snapshot.Clamped, snapshot.WorkpiecePose,
        snapshot.Error, snapshot.Paused, snapshot.CanRetry,
    };

    /// <summary>新连接先重放所有已校验模型，再补发当前配方，最后接收实时命令。</summary>
    private async Task ReplayStoredData(ClientSession session)
    {
        if (_productionStore is null) return;
        foreach (var model in _productionStore.ListModels())
        {
            byte[] bytes = _productionStore.ReadModel(model.ModelId);
            await SendTo(session, new
            {
                type = "model_begin", cmd = "model_begin", model.ModelId, model.FileName, model.Unit,
                model.Sha256, model.TotalBytes, model.TotalChunks,
            });
            for (int i = 0; i < model.TotalChunks; i++)
            {
                int offset = i * ProductionStore.ChunkSize;
                int length = (int)Math.Min(ProductionStore.ChunkSize, bytes.LongLength - offset);
                string data = Convert.ToBase64String(bytes, offset, length);
                await SendTo(session, new { type = "model_chunk", cmd = "model_chunk", model.ModelId, index = i, data });
            }
            await SendTo(session, new { type = "model_commit", cmd = "model_commit", model.ModelId });
        }
        if (_productionStore.CurrentRecipe is not null)
            await SendTo(session, RecipeMessage(_productionStore.CurrentRecipe));
        if (_production is not null) await SendTo(session, ProductionMessage(_production.Snapshot));
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
        foreach (var (id, session) in _clients)
        {
            if (session.Socket.State != WebSocketState.Open) { _clients.TryRemove(id, out _); continue; }
            _ = SendTo(session, text);
        }
    }

    private static async Task SendTo(ClientSession session, object msg)
        => await SendTo(session, JsonSerializer.Serialize(msg, JsonOpts));

    private static async Task SendTo(ClientSession session, string text)
    {
        await session.SendLock.WaitAsync();
        try
        {
            if (session.Socket.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(text);
            await session.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        finally { session.SendLock.Release(); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        // Stop/Close 幂等: 监听器可能已随 AcceptLoop 异常退出而释放
        try { _listener.Stop(); } catch (ObjectDisposedException) { }
        try { _listener.Close(); } catch (ObjectDisposedException) { }
    }
}
