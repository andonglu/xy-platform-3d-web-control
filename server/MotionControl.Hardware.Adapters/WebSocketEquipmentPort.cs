using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MotionControl.Hardware.Abstractions;

namespace MotionControl.Hardware.Adapters;

/// <summary>JSON/WebSocket 硬件通道。真实与虚拟 Port 只在装配配置中区分。</summary>
public abstract class WebSocketEquipmentPort : IEquipmentPort
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private string _endpoint;
    private EquipmentSnapshot _snapshot = EquipmentSnapshot.Empty;
    private long _version;

    protected WebSocketEquipmentPort(string id, string name, EquipmentPortKind kind, string endpoint)
    {
        Id = id; Name = name; Kind = kind; _endpoint = endpoint;
    }

    public string Id { get; }
    public string Name { get; }
    public EquipmentPortKind Kind { get; }
    public string Endpoint { get => _endpoint; set => _endpoint = value; }
    public EquipmentConnectionState State { get; private set; } = EquipmentConnectionState.Disconnected;
    public EquipmentSnapshot Snapshot => _snapshot;
    public event EventHandler<EquipmentConnectionChangedEventArgs>? ConnectionChanged;
    public event EventHandler<EquipmentSnapshotChangedEventArgs>? SnapshotChanged;
    public event EventHandler<EquipmentEventReceivedEventArgs>? EventReceived;

    public async Task<EquipmentResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        await DisconnectAsync(cancellationToken);
        SetState(EquipmentConnectionState.Connecting, "正在连接设备通道");
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ws = ws; _cts = cts;
        try
        {
            await ws.ConnectAsync(new Uri(Endpoint), cts.Token);
            SetState(EquipmentConnectionState.Connected, "设备通道已连接");
            _ = ReceiveLoop(ws, cts.Token);
            var sent = await SendJson(new { type = "register", role = "host" }, cts.Token);
            return sent.Succeeded ? EquipmentResult.Success("设备通道已连接") : sent;
        }
        catch (Exception ex)
        {
            await DisconnectAsync(CancellationToken.None);
            SetState(EquipmentConnectionState.Faulted, ex.Message);
            return EquipmentResult.Failure(ex.Message, "ConnectFailed");
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        bool hadConnection = _ws is not null;
        var ws = _ws; var cts = _cts;
        _ws = null; _cts = null;
        if (cts is not null) cts.Cancel();
        if (ws is not null)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "上位机断开", cancellationToken);
            }
            catch { }
            ws.Dispose();
        }
        if (cts is not null) cts.Dispose();
        if (hadConnection) SetState(EquipmentConnectionState.Disconnected, "设备通道已断开");
    }

    protected virtual bool Supports(string commandType) => true;

    public async Task<EquipmentResult> ExecuteAsync(EquipmentCommand command, CancellationToken cancellationToken = default)
    {
        if (!Supports(command.Type)) return EquipmentResult.Failure($"{Name} 不支持命令 {command.Type}", "UnsupportedCommand");
        object dto = command.Type switch
        {
            "home" => new { type = "home" },
            "ptp" => new { type = "ptp", axis = command.Axis, pos = command.Position },
            "jog" => new { type = "jog", axis = command.Axis, dir = command.Direction },
            "laser" => new { type = "laser", on = command.On },
            "job" => new { type = "job", name = command.Recipe },
            "production_start" => new { type = "production_start", recipe = command.Recipe, count = command.Count },
            "production_stop" => new { type = "production_stop" },
            "production_reset" => new { type = "production_reset" },
            "estop" => new { type = "estop" },
            _ => throw new NotSupportedException($"未知设备命令: {command.Type}")
        };
        return await SendJson(dto, cancellationToken);
    }

    private async Task<EquipmentResult> SendJson(object dto, CancellationToken ct)
    {
        var ws = _ws;
        if (ws?.State != WebSocketState.Open) return EquipmentResult.Failure("设备通道未连接", "Disconnected");
        try
        {
            string json = JsonSerializer.Serialize(dto);
            await _sendLock.WaitAsync(ct);
            try { await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct); }
            finally { _sendLock.Release(); }
            return EquipmentResult.Success("指令已发送");
        }
        catch (Exception ex) { return EquipmentResult.Failure(ex.Message, "SendFailed"); }
    }

    private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                using var packet = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    packet.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                ProcessMessage(Encoding.UTF8.GetString(packet.ToArray()));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { EventReceived?.Invoke(this, new("error", ex.Message)); }
        finally
        {
            if (ReferenceEquals(_ws, ws))
            {
                _ws = null; _cts = null;
                SetState(EquipmentConnectionState.Disconnected, "设备通道已断开");
            }
        }
    }

    private void ProcessMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        switch (root.GetProperty("type").GetString())
        {
            case "hello":
                EventReceived?.Invoke(this, new("ok", root.GetProperty("server").GetString() ?? "设备服务已连接"));
                break;
            case "event":
                EventReceived?.Invoke(this, new(root.GetProperty("level").GetString() ?? "info", root.GetProperty("msg").GetString() ?? ""));
                break;
            case "path":
                EventReceived?.Invoke(this, new("info", $"收到加工路径: {root.GetProperty("name").GetString()}"));
                break;
            case "state":
                var axes = new Dictionary<string, AxisEquipmentSnapshot>();
                foreach (string axis in new[] { "x", "y", "z" })
                {
                    var a = root.GetProperty(axis);
                    axes[axis] = new(a.GetProperty("pos").GetDouble(), a.GetProperty("vel").GetDouble(), a.GetProperty("state").GetString() ?? "Idle");
                }
                var p = root.GetProperty("production");
                var production = new ProductionEquipmentSnapshot(
                    p.GetProperty("phase").GetString() ?? "Idle",
                    p.GetProperty("running").GetBoolean(),
                    p.GetProperty("stopRequested").GetBoolean(),
                    p.GetProperty("recipe").GetString() ?? "star",
                    p.GetProperty("targetCount").GetInt32(),
                    p.GetProperty("completed").GetInt32(),
                    p.TryGetProperty("workpieceId", out var id) ? id.GetString() : null,
                    p.TryGetProperty("workpieceX", out var wx) && wx.ValueKind == JsonValueKind.Number ? wx.GetDouble() : null,
                    p.GetProperty("pusherX").GetDouble(), p.GetProperty("clamp").GetDouble(), p.GetProperty("phaseProgress").GetDouble());
                _snapshot = new EquipmentSnapshot(
                    ++_version, axes,
                    root.GetProperty("laser").GetBoolean(), root.GetProperty("mode").GetString() ?? "Idle",
                    production, DateTimeOffset.UtcNow);
                SnapshotChanged?.Invoke(this, new(_snapshot));
                break;
        }
    }

    private void SetState(EquipmentConnectionState state, string message)
    {
        State = state;
        ConnectionChanged?.Invoke(this, new(state, message));
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _sendLock.Dispose();
    }
}

public abstract class AxisCardWebSocketPort(string id, string name, EquipmentPortKind kind, string endpoint)
    : WebSocketEquipmentPort(id, name, kind, endpoint), IMotionAxisCardPort
{
    protected override bool Supports(string commandType) => commandType != "laser";
}

public abstract class LaserControlWebSocketPort(string id, string name, EquipmentPortKind kind, string endpoint)
    : WebSocketEquipmentPort(id, name, kind, endpoint), ILaserControlPort
{
    protected override bool Supports(string commandType) => commandType == "laser";
}

public sealed class VirtualAxisCardPort(string endpoint = "ws://127.0.0.1:8080/ws/motion")
    : AxisCardWebSocketPort("virtual-axis-card", "3D 虚拟轴卡", EquipmentPortKind.Virtual, endpoint);

public sealed class VirtualLaserControlPort(string endpoint = "ws://127.0.0.1:8080/ws/laser")
    : LaserControlWebSocketPort("virtual-laser-controller", "3D 虚拟激光控制卡", EquipmentPortKind.Virtual, endpoint);

public sealed class RealAxisCardPort(string endpoint = "ws://127.0.0.1:8521/ws/motion")
    : AxisCardWebSocketPort("real-axis-card", "真实运动轴卡", EquipmentPortKind.Physical, endpoint);

public sealed class RealLaserControlPort(string endpoint = "ws://127.0.0.1:8521/ws/laser")
    : LaserControlWebSocketPort("real-laser-controller", "真实激光控制卡", EquipmentPortKind.Physical, endpoint);

public sealed record EquipmentChannelOptions(
    string ActiveChannel,
    string VirtualMotionEndpoint = "ws://127.0.0.1:8080/ws/motion",
    string VirtualLaserEndpoint = "ws://127.0.0.1:8080/ws/laser",
    string RealMotionEndpoint = "ws://127.0.0.1:8521/ws/motion",
    string RealLaserEndpoint = "ws://127.0.0.1:8521/ws/laser");

public static class EquipmentPortFactory
{
    public static EquipmentPorts Create(EquipmentChannelOptions options) =>
        options.ActiveChannel.Equals("real", StringComparison.OrdinalIgnoreCase)
            ? new EquipmentPorts(new RealAxisCardPort(options.RealMotionEndpoint), new RealLaserControlPort(options.RealLaserEndpoint))
            : new EquipmentPorts(new VirtualAxisCardPort(options.VirtualMotionEndpoint), new VirtualLaserControlPort(options.VirtualLaserEndpoint));
}
