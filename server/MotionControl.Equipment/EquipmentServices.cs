using MotionControl.Hardware.Abstractions;

namespace MotionControl.Equipment;

public enum MachineOperationSource { ManualOperation, Homing, ProductionFlow, Script, Maintenance, System }

public interface IMachineOperationLease : IAsyncDisposable
{
    MachineOperationSource Source { get; }
    CancellationToken CancellationToken { get; }
}

public interface IMachineOperationCoordinator
{
    Task<IMachineOperationLease> AcquireAsync(MachineOperationSource source, CancellationToken cancellationToken = default);
    IDisposable Enter(IMachineOperationLease lease);
    void CancelCurrent();
}

/// <summary>脚本持有环境租约，平台内部调用通过 AsyncLocal 沿用，避免脚本自锁。</summary>
public sealed class MachineOperationCoordinator : IMachineOperationCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncLocal<OperationLease?> _current = new();
    private OperationLease? _active;

    public async Task<IMachineOperationLease> AcquireAsync(MachineOperationSource source, CancellationToken cancellationToken = default)
    {
        if (_current.Value is not null) return new NestedLease(_current.Value);
        await _gate.WaitAsync(cancellationToken);
        var lease = new OperationLease(this, source);
        _active = lease;
        return lease;
    }

    public IDisposable Enter(IMachineOperationLease lease)
    {
        if (lease is not OperationLease operation) throw new ArgumentException("租约类型不属于当前协调器", nameof(lease));
        _current.Value = operation;
        return new LeaseScope(this, operation);
    }

    public void CancelCurrent() => _active?.Cancel();

    private void Release(OperationLease lease)
    {
        if (ReferenceEquals(_current.Value, lease)) _current.Value = null;
        if (ReferenceEquals(_active, lease)) _active = null;
        _gate.Release();
    }

    private sealed class LeaseScope(MachineOperationCoordinator owner, OperationLease lease) : IDisposable
    {
        public void Dispose()
        {
            if (ReferenceEquals(owner._current.Value, lease)) owner._current.Value = null;
        }
    }

    private sealed class NestedLease(OperationLease inner) : IMachineOperationLease
    {
        public MachineOperationSource Source => inner.Source;
        public CancellationToken CancellationToken => inner.CancellationToken;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class OperationLease(MachineOperationCoordinator owner, MachineOperationSource source) : IMachineOperationLease
    {
        private readonly CancellationTokenSource _cancellation = new();
        private int _disposed;
        public MachineOperationSource Source { get; } = source;
        public CancellationToken CancellationToken => _cancellation.Token;
        public void Cancel() { if (!_cancellation.IsCancellationRequested) _cancellation.Cancel(); }
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Cancel();
                owner.Release(this);
                _cancellation.Dispose();
            }
            return ValueTask.CompletedTask;
        }
    }
}

public interface IEquipmentService : IAsyncDisposable
{
    string Endpoint { get; set; }
    string ChannelName { get; }
    EquipmentConnectionState ConnectionState { get; }
    EquipmentSnapshot Snapshot { get; }
    IMachineOperationCoordinator Operations { get; }
    event EventHandler<EquipmentConnectionChangedEventArgs>? ConnectionChanged;
    event EventHandler<EquipmentSnapshotChangedEventArgs>? SnapshotChanged;
    event EventHandler<EquipmentEventReceivedEventArgs>? EventReceived;
    Task<EquipmentResult> ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<EquipmentResult> ExecuteAsync(EquipmentCommand command, MachineOperationSource source, CancellationToken cancellationToken = default);
}

/// <summary>设备无关服务：组合独立轴卡和激光控制卡，不识别虚拟或真实实现。</summary>
public sealed class LaserEquipmentService : IEquipmentService
{
    private readonly IMotionAxisCardPort _motion;
    private readonly ILaserControlPort _laser;
    private EquipmentSnapshot _motionSnapshot = EquipmentSnapshot.Empty;
    private EquipmentSnapshot _laserSnapshot = EquipmentSnapshot.Empty;
    private EquipmentSnapshot _current = EquipmentSnapshot.Empty;
    private long _version;

    public LaserEquipmentService(IMotionAxisCardPort motion, ILaserControlPort laser, IMachineOperationCoordinator? operations = null)
    {
        _motion = motion; _laser = laser;
        Operations = operations ?? new MachineOperationCoordinator();
        _motion.ConnectionChanged += (_, e) => OnConnection(e);
        _laser.ConnectionChanged += (_, e) => OnConnection(e);
        _motion.SnapshotChanged += (_, e) => { _motionSnapshot = e.Snapshot; PublishSnapshot(); };
        _laser.SnapshotChanged += (_, e) => { _laserSnapshot = e.Snapshot; PublishSnapshot(); };
        _motion.EventReceived += (_, e) => EventReceived?.Invoke(this, new(e.Level, $"[轴卡] {e.Message}"));
        _laser.EventReceived += (_, e) => EventReceived?.Invoke(this, new(e.Level, $"[激光器] {e.Message}"));
    }

    public string Endpoint { get => _motion.Endpoint; set => _motion.Endpoint = value; }
    public string ChannelName => $"{_motion.Name} + {_laser.Name}";
    public EquipmentConnectionState ConnectionState
    {
        get
        {
            if (_motion.State == EquipmentConnectionState.Faulted || _laser.State == EquipmentConnectionState.Faulted) return EquipmentConnectionState.Faulted;
            if (_motion.State == EquipmentConnectionState.Connected && _laser.State == EquipmentConnectionState.Connected) return EquipmentConnectionState.Connected;
            if (_motion.State == EquipmentConnectionState.Connecting || _laser.State == EquipmentConnectionState.Connecting) return EquipmentConnectionState.Connecting;
            return EquipmentConnectionState.Disconnected;
        }
    }
    public EquipmentSnapshot Snapshot => _current;
    public IMachineOperationCoordinator Operations { get; }
    public event EventHandler<EquipmentConnectionChangedEventArgs>? ConnectionChanged;
    public event EventHandler<EquipmentSnapshotChangedEventArgs>? SnapshotChanged;
    public event EventHandler<EquipmentEventReceivedEventArgs>? EventReceived;

    public async Task<EquipmentResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var motionResult = await _motion.ConnectAsync(cancellationToken);
        if (!motionResult.Succeeded) return EquipmentResult.Failure($"轴卡连接失败: {motionResult.Message}", "MotionConnectFailed");
        var laserResult = await _laser.ConnectAsync(cancellationToken);
        if (!laserResult.Succeeded)
        {
            await _motion.DisconnectAsync(cancellationToken);
            return EquipmentResult.Failure($"激光控制卡连接失败: {laserResult.Message}", "LaserConnectFailed");
        }
        return EquipmentResult.Success("轴卡与激光控制卡均已连接");
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _laser.DisconnectAsync(cancellationToken);
        await _motion.DisconnectAsync(cancellationToken);
    }

    public async Task<EquipmentResult> ExecuteAsync(EquipmentCommand command, MachineOperationSource source, CancellationToken cancellationToken = default)
    {
        if (command.Type == "estop")
        {
            Operations.CancelCurrent();
            var results = await Task.WhenAll(
                _motion.ExecuteAsync(command, cancellationToken),
                _laser.ExecuteAsync(EquipmentCommand.Laser(false), cancellationToken));
            return results.All(r => r.Succeeded) ? EquipmentResult.Success("轴卡已停止，激光已关闭")
                : EquipmentResult.Failure(string.Join("；", results.Where(r => !r.Succeeded).Select(r => r.Message)), "StopFailed");
        }
        await using var lease = await Operations.AcquireAsync(source, cancellationToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.CancellationToken);
        return command.Type == "laser"
            ? await _laser.ExecuteAsync(command, linked.Token)
            : await _motion.ExecuteAsync(command, linked.Token);
    }

    private void OnConnection(EquipmentConnectionChangedEventArgs e)
        => ConnectionChanged?.Invoke(this, new(ConnectionState, e.Message));

    private void PublishSnapshot()
    {
        _current = _motionSnapshot with
        {
            Version = ++_version,
            Laser = _laserSnapshot.Laser,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        SnapshotChanged?.Invoke(this, new(_current));
    }

    public async ValueTask DisposeAsync()
    {
        await _laser.DisposeAsync();
        await _motion.DisposeAsync();
    }
}
