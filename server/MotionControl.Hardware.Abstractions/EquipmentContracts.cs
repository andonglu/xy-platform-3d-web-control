namespace MotionControl.Hardware.Abstractions;

public enum EquipmentConnectionState { Disconnected, Connecting, Connected, Faulted, Disconnecting }
public enum EquipmentPortKind { Virtual, Physical }

public sealed record EquipmentResult(bool Succeeded, string Message = "", string? ErrorCode = null)
{
    public static EquipmentResult Success(string message = "") => new(true, message);
    public static EquipmentResult Failure(string message, string? errorCode = null) => new(false, message, errorCode);
}

public sealed record AxisEquipmentSnapshot(double Position, double Velocity, string State)
{
    public static AxisEquipmentSnapshot Empty { get; } = new(0, 0, "Idle");
}

public sealed record ProductionEquipmentSnapshot(
    string Phase,
    bool Running,
    bool StopRequested,
    string Recipe,
    int TargetCount,
    int Completed,
    string? WorkpieceId,
    double? WorkpieceX,
    double PusherX,
    double Clamp,
    double PhaseProgress)
{
    public static ProductionEquipmentSnapshot Empty { get; } =
        new("Idle", false, false, "star", 1, 0, null, null, -305, 0, 0);
}

public sealed record EquipmentSnapshot(
    long Version,
    IReadOnlyDictionary<string, AxisEquipmentSnapshot> Axes,
    bool Laser,
    string Mode,
    ProductionEquipmentSnapshot Production,
    DateTimeOffset UpdatedUtc)
{
    public static EquipmentSnapshot Empty { get; } = new(
        0,
        new Dictionary<string, AxisEquipmentSnapshot> { ["x"] = AxisEquipmentSnapshot.Empty, ["y"] = AxisEquipmentSnapshot.Empty, ["z"] = AxisEquipmentSnapshot.Empty },
        false, "Idle", ProductionEquipmentSnapshot.Empty, DateTimeOffset.UtcNow);
}

public sealed record EquipmentCommand(
    string Type,
    string? Axis = null,
    double? Position = null,
    int? Direction = null,
    bool? On = null,
    string? Recipe = null,
    int? Count = null)
{
    public static EquipmentCommand Home() => new("home");
    public static EquipmentCommand Ptp(string axis, double position) => new("ptp", axis, position);
    public static EquipmentCommand Jog(string axis, int direction) => new("jog", axis, Direction: direction);
    public static EquipmentCommand Laser(bool on) => new("laser", On: on);
    public static EquipmentCommand Job(string recipe) => new("job", Recipe: recipe);
    public static EquipmentCommand ProductionStart(string recipe, int count) => new("production_start", Recipe: recipe, Count: count);
    public static EquipmentCommand ProductionStop() => new("production_stop");
    public static EquipmentCommand ProductionReset() => new("production_reset");
    public static EquipmentCommand EmergencyStop() => new("estop");
}

public sealed class EquipmentConnectionChangedEventArgs(EquipmentConnectionState state, string message) : EventArgs
{
    public EquipmentConnectionState State { get; } = state;
    public string Message { get; } = message;
}

public sealed class EquipmentSnapshotChangedEventArgs(EquipmentSnapshot snapshot) : EventArgs
{
    public EquipmentSnapshot Snapshot { get; } = snapshot;
}

public sealed class EquipmentEventReceivedEventArgs(string level, string message) : EventArgs
{
    public string Level { get; } = level;
    public string Message { get; } = message;
}

/// <summary>
/// 硬件 Port 契约。3D 虚拟服务和真实设备网关均实现此接口；上层只见命令和强类型快照。
/// </summary>
public interface IEquipmentPort : IAsyncDisposable
{
    string Id { get; }
    string Name { get; }
    EquipmentPortKind Kind { get; }
    string Endpoint { get; set; }
    EquipmentConnectionState State { get; }
    EquipmentSnapshot Snapshot { get; }
    event EventHandler<EquipmentConnectionChangedEventArgs>? ConnectionChanged;
    event EventHandler<EquipmentSnapshotChangedEventArgs>? SnapshotChanged;
    event EventHandler<EquipmentEventReceivedEventArgs>? EventReceived;
    Task<EquipmentResult> ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<EquipmentResult> ExecuteAsync(EquipmentCommand command, CancellationToken cancellationToken = default);
}

/// <summary>运动轴卡：回零、定位、点动、插补作业与生产机构控制。</summary>
public interface IMotionAxisCardPort : IEquipmentPort { }

/// <summary>激光控制卡：出光/关光及激光器状态。</summary>
public interface ILaserControlPort : IEquipmentPort { }

public sealed record EquipmentPorts(IMotionAxisCardPort Motion, ILaserControlPort Laser);
