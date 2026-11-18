using MotionControl.Hardware.Abstractions;

namespace MotionControl.Equipment;

public interface ILaserPlatform : IAsyncDisposable
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
    Task<EquipmentResult> HomeAsync(MachineOperationSource source = MachineOperationSource.Homing, CancellationToken cancellationToken = default);
    Task<EquipmentResult> MoveAxisAsync(string axis, double position, MachineOperationSource source = MachineOperationSource.ManualOperation, CancellationToken cancellationToken = default);
    Task<EquipmentResult> JogAsync(string axis, int direction, MachineOperationSource source = MachineOperationSource.ManualOperation, CancellationToken cancellationToken = default);
    Task<EquipmentResult> SetLaserAsync(bool on, MachineOperationSource source = MachineOperationSource.ManualOperation, CancellationToken cancellationToken = default);
    Task<EquipmentResult> RunRecipeAsync(string recipe, MachineOperationSource source = MachineOperationSource.ManualOperation, CancellationToken cancellationToken = default);
    Task<EquipmentResult> StartProductionAsync(string recipe, int count, MachineOperationSource source = MachineOperationSource.ProductionFlow, CancellationToken cancellationToken = default);
    Task<EquipmentResult> StopProductionAsync(MachineOperationSource source = MachineOperationSource.ManualOperation, CancellationToken cancellationToken = default);
    Task<EquipmentResult> ResetProductionAsync(MachineOperationSource source = MachineOperationSource.ManualOperation, CancellationToken cancellationToken = default);
    Task<EquipmentResult> EmergencyStopAsync(CancellationToken cancellationToken = default);
    Task<EquipmentResult> WaitForIdleAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
    Task<EquipmentResult> WaitForProductionCompleteAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>设备平台：业务校验、动作编排和完成等待。WPF、脚本与流程只调用本层。</summary>
public sealed class LaserPlatform : ILaserPlatform
{
    private static readonly HashSet<string> Recipes = ["star", "circle", "combo"];
    private readonly IEquipmentService _service;

    public LaserPlatform(IEquipmentService service)
    {
        _service = service;
        _service.ConnectionChanged += (_, e) => ConnectionChanged?.Invoke(this, e);
        _service.SnapshotChanged += (_, e) => SnapshotChanged?.Invoke(this, e);
        _service.EventReceived += (_, e) => EventReceived?.Invoke(this, e);
    }

    public string Endpoint { get => _service.Endpoint; set => _service.Endpoint = value; }
    public string ChannelName => _service.ChannelName;
    public EquipmentConnectionState ConnectionState => _service.ConnectionState;
    public EquipmentSnapshot Snapshot => _service.Snapshot;
    public IMachineOperationCoordinator Operations => _service.Operations;
    public event EventHandler<EquipmentConnectionChangedEventArgs>? ConnectionChanged;
    public event EventHandler<EquipmentSnapshotChangedEventArgs>? SnapshotChanged;
    public event EventHandler<EquipmentEventReceivedEventArgs>? EventReceived;
    public Task<EquipmentResult> ConnectAsync(CancellationToken cancellationToken = default) => _service.ConnectAsync(cancellationToken);
    public Task DisconnectAsync(CancellationToken cancellationToken = default) => _service.DisconnectAsync(cancellationToken);
    public Task<EquipmentResult> HomeAsync(MachineOperationSource source = MachineOperationSource.Homing, CancellationToken cancellationToken = default) => _service.ExecuteAsync(EquipmentCommand.Home(), source, cancellationToken);
    public Task<EquipmentResult> MoveAxisAsync(string axis, double position, MachineOperationSource source = MachineOperationSource.ManualOperation, CancellationToken cancellationToken = default) => _service.ExecuteAsync(EquipmentCommand.Ptp(axis, position), source, cancellationToken);
    public Task<EquipmentResult> JogAsync(string axis, int direction, MachineOperationSource source = MachineOperationSource.ManualOperation, CancellationToken cancellationToken = default) => _service.ExecuteAsync(EquipmentCommand.Jog(axis, direction), source, cancellationToken);
    public Task<EquipmentResult> SetLaserAsync(bool on, MachineOperationSource source = MachineOperationSource.ManualOperation, CancellationToken cancellationToken = default) => _service.ExecuteAsync(EquipmentCommand.Laser(on), source, cancellationToken);
    public Task<EquipmentResult> StopProductionAsync(MachineOperationSource source = MachineOperationSource.ManualOperation, CancellationToken cancellationToken = default) => _service.ExecuteAsync(EquipmentCommand.ProductionStop(), source, cancellationToken);
    public Task<EquipmentResult> ResetProductionAsync(MachineOperationSource source = MachineOperationSource.ManualOperation, CancellationToken cancellationToken = default) => _service.ExecuteAsync(EquipmentCommand.ProductionReset(), source, cancellationToken);
    public Task<EquipmentResult> EmergencyStopAsync(CancellationToken cancellationToken = default) => _service.ExecuteAsync(EquipmentCommand.EmergencyStop(), MachineOperationSource.System, cancellationToken);

    public Task<EquipmentResult> RunRecipeAsync(string recipe, MachineOperationSource source = MachineOperationSource.ManualOperation, CancellationToken cancellationToken = default)
    {
        return Recipes.Contains(recipe)
            ? _service.ExecuteAsync(EquipmentCommand.Job(recipe), source, cancellationToken)
            : Task.FromResult(EquipmentResult.Failure($"未知加工程序: {recipe}", "InvalidRecipe"));
    }

    public Task<EquipmentResult> StartProductionAsync(string recipe, int count, MachineOperationSource source = MachineOperationSource.ProductionFlow, CancellationToken cancellationToken = default)
    {
        if (!Recipes.Contains(recipe)) return Task.FromResult(EquipmentResult.Failure($"未知加工程序: {recipe}", "InvalidRecipe"));
        if (count < 0 || count > 999) return Task.FromResult(EquipmentResult.Failure("生产数量必须为 0~999", "InvalidCount"));
        return _service.ExecuteAsync(EquipmentCommand.ProductionStart(recipe, count), source, cancellationToken);
    }

    public async Task<EquipmentResult> WaitForIdleAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await Task.Delay(150, cancellationToken);
        bool completed = await WaitUntil(s =>
            s.Production.Phase == "Faulted" ||
            (!s.Production.Running && s.Mode == "Idle" && s.Axes.Values.All(a => a.State != "Moving" && a.State != "Homing")),
            timeout, cancellationToken);
        if (!completed) return EquipmentResult.Failure("等待设备空闲超时", "Timeout");
        return Snapshot.Production.Phase == "Faulted"
            ? EquipmentResult.Failure("设备处于故障状态", "Faulted")
            : EquipmentResult.Success("设备已空闲");
    }

    public async Task<EquipmentResult> WaitForProductionCompleteAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        // 先确认新一轮已经开始，避免把上一轮的 Complete 误当成本轮完成。
        bool started = await WaitUntil(s => s.Production.Running || s.Production.Phase == "Faulted",
            TimeSpan.FromSeconds(10), cancellationToken);
        if (!started) return EquipmentResult.Failure("自动生产未进入运行状态", "NotStarted");
        if (Snapshot.Production.Phase == "Faulted") return EquipmentResult.Failure("生产处于故障状态", "Faulted");
        bool completed = await WaitUntil(s => s.Production.Phase is "Complete" or "Faulted", timeout, cancellationToken);
        if (!completed) return EquipmentResult.Failure("等待自动生产完成超时", "Timeout");
        return Snapshot.Production.Phase == "Complete"
            ? EquipmentResult.Success($"生产完成 {Snapshot.Production.Completed} 件")
            : EquipmentResult.Failure("生产处于故障状态", "Faulted");
    }

    private async Task<bool> WaitUntil(Func<EquipmentSnapshot, bool> predicate, TimeSpan timeout, CancellationToken ct)
    {
        if (predicate(Snapshot)) return true;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, EquipmentSnapshotChangedEventArgs e)
        {
            if (predicate(e.Snapshot)) completion.TrySetResult(true);
        }
        SnapshotChanged += Handler;
        try
        {
            using var timeoutTask = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutTask.CancelAfter(timeout);
            try { await completion.Task.WaitAsync(timeoutTask.Token); return true; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        }
        finally { SnapshotChanged -= Handler; }
    }

    public async ValueTask DisposeAsync() => await _service.DisposeAsync();
}

/// <summary>脚本唯一可用的设备门面，不暴露 Port、WebSocket 或任意 JSON 命令。</summary>
public interface ILaserMachineScriptApi
{
    Task<EquipmentResult> HomeAsync(CancellationToken cancellationToken = default);
    Task<EquipmentResult> MoveAxisAsync(string axis, double position, CancellationToken cancellationToken = default);
    Task<EquipmentResult> SetLaserAsync(bool on, CancellationToken cancellationToken = default);
    Task<EquipmentResult> RunRecipeAsync(string recipe, CancellationToken cancellationToken = default);
    Task<EquipmentResult> StartProductionAsync(string recipe, int count, CancellationToken cancellationToken = default);
    Task<EquipmentResult> StopProductionAsync(CancellationToken cancellationToken = default);
    Task<EquipmentResult> ResetProductionAsync(CancellationToken cancellationToken = default);
    Task<EquipmentResult> EmergencyStopAsync(CancellationToken cancellationToken = default);
    Task<EquipmentResult> WaitForIdleAsync(CancellationToken cancellationToken = default);
    Task<EquipmentResult> WaitForProductionCompleteAsync(CancellationToken cancellationToken = default);
    EquipmentSnapshot GetSnapshot();
}

public sealed class LaserMachineScriptApi(ILaserPlatform platform) : ILaserMachineScriptApi
{
    public Task<EquipmentResult> HomeAsync(CancellationToken ct = default) => platform.HomeAsync(MachineOperationSource.Script, ct);
    public Task<EquipmentResult> MoveAxisAsync(string axis, double position, CancellationToken ct = default) => platform.MoveAxisAsync(axis, position, MachineOperationSource.Script, ct);
    public Task<EquipmentResult> SetLaserAsync(bool on, CancellationToken ct = default) => platform.SetLaserAsync(on, MachineOperationSource.Script, ct);
    public Task<EquipmentResult> RunRecipeAsync(string recipe, CancellationToken ct = default) => platform.RunRecipeAsync(recipe, MachineOperationSource.Script, ct);
    public Task<EquipmentResult> StartProductionAsync(string recipe, int count, CancellationToken ct = default) => platform.StartProductionAsync(recipe, count, MachineOperationSource.Script, ct);
    public Task<EquipmentResult> StopProductionAsync(CancellationToken ct = default) => platform.StopProductionAsync(MachineOperationSource.Script, ct);
    public Task<EquipmentResult> ResetProductionAsync(CancellationToken ct = default) => platform.ResetProductionAsync(MachineOperationSource.Script, ct);
    public Task<EquipmentResult> EmergencyStopAsync(CancellationToken ct = default) => platform.EmergencyStopAsync(ct);
    public Task<EquipmentResult> WaitForIdleAsync(CancellationToken ct = default) => platform.WaitForIdleAsync(TimeSpan.FromSeconds(60), ct);
    public Task<EquipmentResult> WaitForProductionCompleteAsync(CancellationToken ct = default) => platform.WaitForProductionCompleteAsync(TimeSpan.FromMinutes(10), ct);
    public EquipmentSnapshot GetSnapshot() => platform.Snapshot;
}
