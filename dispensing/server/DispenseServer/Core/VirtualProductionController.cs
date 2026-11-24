using System.Numerics;

namespace DispenseServer.Core;

/// <summary>
/// P9 虚拟连续生产状态机。所有动画和工艺进度由本类快照驱动；
/// 不连接真实 PLC/机器人，也不以 IsRunning 下降沿推断加工完成。
/// </summary>
public sealed class VirtualProductionController
{
    private readonly DispenseController _controller;
    private readonly ProductionStore _store;
    private ProductionEnvelope? _envelope;
    private List<DispensePath3D> _paths = [];
    private ProductionPhase _phase = ProductionPhase.Idle;
    private ProductionPhase? _pausedPhase;
    private ProductionPhase? _blockedPhase;
    private string? _runId;
    private string? _activeJobRunId;
    private string? _error;
    private int _completedCount;
    private int _pieceIndex;
    private int _targetCount;
    private bool _partPresent;
    private bool _clamped;
    private bool _stepMode;
    private double _phaseTimer;
    private double _emitTimer;
    private Pose3D _workpiecePose = new();

    public VirtualProductionController(DispenseController controller, ProductionStore store)
    {
        _controller = controller;
        _store = store;
        _controller.OnJobCompleted += OnJobCompleted;
        _controller.OnJobFailed += OnJobFailed;
    }

    public event Action<ProductionSnapshot>? OnSnapshot;
    public event Action<string, string>? OnEvent;

    public ProductionEnvelope? CurrentEnvelope => _envelope;
    public bool IsActive => _phase is not ProductionPhase.Idle and not ProductionPhase.Completed;
    public bool IsBlocked => _phase == ProductionPhase.Blocked;
    public ProductionSnapshot Snapshot => BuildSnapshot();

    /// <summary>配置时立即构造路径并复用 UploadPaths 完成现有五轴安全预检。</summary>
    public void Configure(ProductionEnvelope envelope)
    {
        if (IsActive) throw new InvalidOperationException("生产活动或阻止状态未复位，不能替换配方");
        if (_controller.IsExecuting) throw new InvalidOperationException("手动点胶作业运行中，不能替换生产配方");
        ProductionStore.ValidateRecipe(envelope);
        if (_store.GetModel(envelope.ModelId) is null)
            throw new InvalidOperationException($"配方引用的模型 {envelope.ModelId} 尚未同步");

        var paths = JobBuilder.BuildPaths(envelope.Steps);
        try
        {
            if (!_controller.UploadPaths(paths,
                    PathSampler3D.Sample(paths.SelectMany(p => p.Segments)), envelope.Config.Name))
                throw new InvalidOperationException("生产路径未通过服务端安全预检");
        }
        finally
        {
            _controller.ClearPendingJob();
        }

        _envelope = envelope;
        _paths = paths;
        _targetCount = envelope.Config.Loading.BatchCount;
        ResetCycle();
        EmitSnapshot();
    }

    public bool Start()
    {
        if (_envelope is null) return Reject("尚未配置生产工程");
        if (IsActive) return Reject("生产已在运行或等待处理");
        if (_partPresent) return Reject("夹具仍被工件占用，请先复位");
        if (_controller.IsExecuting) return Reject("手动点胶作业运行中");
        _store.ReadModel(_envelope.ModelId); // 启动前再次验证磁盘模型，损坏时阻止运行
        StartCycle(_envelope.Config.FullProduction ? _envelope.Config.Loading.BatchCount : 1, false);
        return true;
    }

    /// <summary>单步推进一个动作；自动确认打开时也停在下一个可操作阶段。</summary>
    public bool Step()
    {
        if (_envelope is null) return Reject("尚未配置生产工程");
        if (_phase == ProductionPhase.Paused) return Reject("暂停中不能单步，请先继续或停止");
        if (_phase == ProductionPhase.Blocked) return Reject("当前步骤已阻止，请重试或复位");

        if (_phase is ProductionPhase.Idle or ProductionPhase.Completed)
        {
            if (_partPresent) return Reject("夹具仍被工件占用，请先复位");
            StartCycle(1, true);
            return true;
        }

        switch (_phase)
        {
            case ProductionPhase.WaitingLoad:
                if (!_envelope.Config.Fixture.AutoPartConfirm && !_partPresent)
                    return Reject($"等待人工确认 {_envelope.Config.Fixture.PartSignal}");
                BeginLoading();
                return true;
            case ProductionPhase.WaitingClamp:
                _clamped = true;
                SetPhase(ProductionPhase.Ready);
                return true;
            case ProductionPhase.Ready:
                return BeginDispensing();
            case ProductionPhase.WaitingUnload:
                BeginUnloading();
                return true;
            default:
                return Reject($"当前阶段 {_phase} 正在执行或无需单步推进");
        }
    }

    public bool Confirm(string signal)
    {
        if (_envelope is null) return Reject("尚未配置生产工程");
        if (_phase == ProductionPhase.Paused) return Reject("暂停中不能确认信号");
        var fixture = _envelope.Config.Fixture;
        if (signal.Equals("PART_PRESENT", StringComparison.OrdinalIgnoreCase) ||
            signal.Equals(fixture.PartSignal, StringComparison.OrdinalIgnoreCase))
        {
            if (_phase != ProductionPhase.WaitingLoad)
                return Reject($"当前阶段 {_phase} 不接受工件到位信号");
            _partPresent = true;
            BeginLoading();
            return true;
        }
        if (signal.Equals("CLAMP_OK", StringComparison.OrdinalIgnoreCase) ||
            signal.Equals(fixture.ClampSignal, StringComparison.OrdinalIgnoreCase))
        {
            if (_phase != ProductionPhase.WaitingClamp || !_partPresent)
                return Reject("工件未放置或当前阶段不接受夹紧信号");
            _clamped = true;
            SetPhase(ProductionPhase.Ready);
            return true;
        }
        return Reject($"未知生产确认信号: {signal}");
    }

    public bool Pause()
    {
        if (_phase == ProductionPhase.Paused) return true;
        if (!IsActive || _phase == ProductionPhase.Blocked)
            return Reject("当前没有可暂停的生产动作");
        _pausedPhase = _phase;
        _phase = ProductionPhase.Paused;
        _controller.SoftPause();
        EmitSnapshot();
        return true;
    }

    public bool Resume()
    {
        if (_phase != ProductionPhase.Paused || _pausedPhase is null)
            return Reject("当前生产未暂停");
        _phase = _pausedPhase.Value;
        _pausedPhase = null;
        _controller.SoftResume();
        EmitSnapshot();
        return true;
    }

    /// <summary>停止关阀并保留工件/夹紧占用，操作员必须显式重试或复位。</summary>
    public bool Stop()
    {
        if (_phase == ProductionPhase.Idle) return true;
        var origin = _phase == ProductionPhase.Paused ? _pausedPhase ?? ProductionPhase.WaitingLoad : _phase;
        if (_phase == ProductionPhase.Completed && !_partPresent)
        {
            _controller.CancelJob("生产停止");
            return true;
        }
        _activeJobRunId = null;
        _blockedPhase = origin;
        _phase = ProductionPhase.Blocked;
        _error = "生产已人工停止；请从当前步骤重试或复位";
        _controller.CancelJob("生产停止");
        EmitSnapshot();
        return true;
    }

    public bool Retry()
    {
        if (_phase != ProductionPhase.Blocked || _blockedPhase is null)
            return Reject("当前没有可重试的阻止步骤");
        var phase = _blockedPhase.Value;
        _error = null;
        _blockedPhase = null;
        _phase = phase;
        _activeJobRunId = null;
        _controller.SetValve(false);

        // 点胶运行已经停止或失败，重试当前件时重新上传并从头执行当前加工作业。
        if (phase == ProductionPhase.Dispensing)
        {
            if (!_partPresent || !_clamped)
            {
                _phase = !_partPresent ? ProductionPhase.WaitingLoad : ProductionPhase.WaitingClamp;
            }
            else
            {
                _phase = ProductionPhase.Ready;
                if (!_stepMode) return BeginDispensing();
            }
        }
        EmitSnapshot();
        return true;
    }

    public void Reset()
    {
        _activeJobRunId = null;
        _phase = ProductionPhase.Idle;
        _controller.CancelJob("生产复位");
        ResetCycle();
        EmitSnapshot();
    }

    public void Tick(double dt)
    {
        if (dt <= 0) return;
        // 急停在任何生产阶段都允许；没有活动点胶 RunId 时由轴故障兜底阻止状态机。
        if (IsActive && _phase != ProductionPhase.Blocked && (_controller.CollisionAlarm || _controller.AnyAxisFaulted))
            Block("生产被急停或轴故障阻止");
        if (_phase != ProductionPhase.Paused && _phase != ProductionPhase.Blocked)
        {
            switch (_phase)
            {
                case ProductionPhase.WaitingLoad:
                    if (!_stepMode && _envelope?.Config.Fixture.AutoPartConfirm == true) BeginLoading();
                    break;
                case ProductionPhase.Loading:
                    TickLoading(dt);
                    break;
                case ProductionPhase.WaitingClamp:
                    if (!_stepMode && _envelope?.Config.Fixture.AutoClampConfirm == true)
                    {
                        _clamped = true;
                        SetPhase(ProductionPhase.Ready);
                    }
                    break;
                case ProductionPhase.Ready:
                    if (!_stepMode) BeginDispensing();
                    break;
                case ProductionPhase.WaitingUnload:
                    if (!_stepMode) BeginUnloading();
                    break;
                case ProductionPhase.Unloading:
                    TickUnloading(dt);
                    break;
            }
        }

        _emitTimer += dt;
        if (_emitTimer >= 1.0 / 30)
        {
            _emitTimer = 0;
            EmitSnapshot();
        }
    }

    private void StartCycle(int count, bool stepMode)
    {
        ResetCycle();
        _runId = Guid.NewGuid().ToString("N");
        _targetCount = count;
        _stepMode = stepMode;
        _pieceIndex = 1;
        if (_envelope!.Config.FullProduction)
        {
            SetPhase(ProductionPhase.WaitingLoad);
        }
        else
        {
            // 非完整生产用于已人工装夹好的单件路径验证，不模拟搬运。
            _partPresent = true;
            _clamped = true;
            _workpiecePose = FinalPose();
            SetPhase(ProductionPhase.Ready);
        }
    }

    private void BeginLoading()
    {
        if (_phase != ProductionPhase.WaitingLoad) return;
        _partPresent = true;
        _clamped = false;
        _pieceIndex = _completedCount + 1;
        _workpiecePose = _envelope!.Config.Loading.Pick;
        SetPhase(ProductionPhase.Loading);
    }

    private void TickLoading(double dt)
    {
        double duration = _envelope!.Config.Loading.ActionDelayMs / 1000.0;
        _phaseTimer += dt;
        double t = duration <= 0 ? 1 : Math.Clamp(_phaseTimer / duration, 0, 1);
        _workpiecePose = LoadingPose(t);
        if (t < 1) return;
        _workpiecePose = FinalPose();
        SetPhase(ProductionPhase.WaitingClamp);
    }

    private bool BeginDispensing()
    {
        if (_envelope is null || !_partPresent || !_clamped)
            return Block("点胶前置条件失败：工件未到位或未夹紧");
        if (!_controller.UploadPaths(_paths,
                PathSampler3D.Sample(_paths.SelectMany(p => p.Segments)), _envelope.Config.Name))
            return Block("当前件路径重新预检失败");
        _controller.RunJob();
        _activeJobRunId = _controller.CurrentRunId;
        if (_activeJobRunId is null)
            return Block(_controller.CollisionMessage ?? "点胶控制器未接受运行命令");
        _error = null;
        SetPhase(ProductionPhase.Dispensing);
        return true;
    }

    private void OnJobCompleted(DispenseRunCompleted result)
    {
        if (_phase != ProductionPhase.Dispensing || result.RunId != _activeJobRunId) return;
        _activeJobRunId = null;
        _controller.SetValve(false);
        if (_envelope!.Config.FullProduction)
        {
            _clamped = false; // 加工完成后先松夹，再进入下料动作
            SetPhase(ProductionPhase.WaitingUnload);
        }
        else
        {
            _partPresent = false;
            _clamped = false;
            FinishPiece();
        }
    }

    private void OnJobFailed(DispenseRunFailed result)
    {
        if (result.RunId != _activeJobRunId) return;
        _activeJobRunId = null;
        Block($"点胶运行失败: {result.Error}");
    }

    private void BeginUnloading()
    {
        if (_phase != ProductionPhase.WaitingUnload) return;
        if (!_partPresent)
        {
            Block("下料被阻止：夹具中没有已加工工件");
            return;
        }
        _clamped = false;
        SetPhase(ProductionPhase.Unloading);
    }

    private void TickUnloading(double dt)
    {
        double duration = _envelope!.Config.Loading.ActionDelayMs / 1000.0;
        _phaseTimer += dt;
        double t = duration <= 0 ? 1 : Math.Clamp(_phaseTimer / duration, 0, 1);
        _workpiecePose = UnloadingPose(t);
        if (t < 1) return;
        _partPresent = false;
        _clamped = false;
        FinishPiece();
    }

    private void FinishPiece()
    {
        _completedCount++;
        _pieceIndex = _completedCount;
        if (_completedCount >= _targetCount)
        {
            _workpiecePose = _envelope!.Config.Loading.Unload;
            _phase = ProductionPhase.Completed;
            _phaseTimer = 0;
            OnEvent?.Invoke("ok", $"生产批次完成: {_completedCount}/{_targetCount}");
            EmitSnapshot();
        }
        else
        {
            _workpiecePose = _envelope!.Config.Loading.Pick;
            SetPhase(ProductionPhase.WaitingLoad);
        }
    }

    private bool Block(string reason)
    {
        if (_phase != ProductionPhase.Blocked)
            _blockedPhase = _phase == ProductionPhase.Paused ? _pausedPhase ?? ProductionPhase.WaitingLoad : _phase;
        _pausedPhase = null;
        _activeJobRunId = null;
        _phase = ProductionPhase.Blocked;
        _error = reason;
        _controller.CancelJob("生产步骤阻止");
        _controller.SetValve(false);
        OnEvent?.Invoke("error", reason);
        EmitSnapshot();
        return false;
    }

    private bool Reject(string reason)
    {
        OnEvent?.Invoke("warn", reason);
        return false;
    }

    private void SetPhase(ProductionPhase phase)
    {
        _phase = phase;
        _phaseTimer = 0;
        EmitSnapshot();
    }

    private void ResetCycle()
    {
        _pausedPhase = null;
        _blockedPhase = null;
        _activeJobRunId = null;
        _runId = null;
        _error = null;
        _completedCount = 0;
        _pieceIndex = 0;
        _partPresent = false;
        _clamped = false;
        _stepMode = false;
        _phase = ProductionPhase.Idle;
        _phaseTimer = 0;
        _workpiecePose = _envelope?.Config.Loading.Pick ?? new Pose3D();
    }

    private ProductionSnapshot BuildSnapshot()
    {
        int batch = _targetCount > 0 ? _targetCount : _envelope?.Config.Loading.BatchCount ?? 0;
        var effective = _phase == ProductionPhase.Paused ? _pausedPhase :
            _phase == ProductionPhase.Blocked ? _blockedPhase : _phase;
        double piece = effective switch
        {
            ProductionPhase.Loading => 0.2 * PhaseFraction(),
            ProductionPhase.WaitingClamp => 0.2,
            ProductionPhase.Ready => 0.25,
            ProductionPhase.Dispensing => 0.25 + 0.55 * _controller.JobProgress,
            ProductionPhase.WaitingUnload => 0.8,
            ProductionPhase.Unloading => 0.8 + 0.2 * PhaseFraction(),
            _ => 0,
        };
        double progress = _phase == ProductionPhase.Completed ? 1 :
            batch > 0 ? Math.Clamp((_completedCount + piece) / batch, 0, 1) : 0;
        return new ProductionSnapshot(_phase.ToString(), _runId, _pieceIndex, _completedCount,
            batch, progress, _partPresent, _clamped, _workpiecePose, _error,
            _phase == ProductionPhase.Paused, _phase == ProductionPhase.Blocked && _blockedPhase is not null);
    }

    private double PhaseFraction()
    {
        double duration = _envelope?.Config.Loading.ActionDelayMs / 1000.0 ?? 0;
        return duration <= 0 ? 1 : Math.Clamp(_phaseTimer / duration, 0, 1);
    }

    private void EmitSnapshot() => OnSnapshot?.Invoke(BuildSnapshot());

    private Pose3D FinalPose()
    {
        var fixture = _envelope!.Config.Fixture.Pose;
        var relative = _envelope.Config.Placement.Pose;
        // System.Numerics 使用行向量: 先工件相对夹具, 再夹具到机器, 与上位机一致
        var matrix = PoseMatrix(relative) * PoseMatrix(fixture);
        // 行向量矩阵是列向量矩阵的转置, 欧拉角按下标提取
        double ry = Math.Asin(Math.Clamp(-matrix.M13, -1, 1));
        double rx = Math.Atan2(matrix.M23, matrix.M33);
        double rz = Math.Atan2(matrix.M12, matrix.M11);
        const double factor = 180.0 / Math.PI;
        return new Pose3D(matrix.M41, matrix.M42, matrix.M43, rx * factor, ry * factor, rz * factor);
    }

    private Pose3D LoadingPose(double t)
    {
        var recipe = _envelope!.Config.Loading;
        var final = FinalPose();
        var pickUp = recipe.Pick with { Z = recipe.Pick.Z + recipe.LiftMm };
        var approach = final with { Z = final.Z + recipe.ApproachMm };
        if (t < 1.0 / 3) return Lerp(recipe.Pick, pickUp, t * 3);
        if (t < 2.0 / 3) return Lerp(pickUp, approach, (t - 1.0 / 3) * 3);
        return Lerp(approach, final, (t - 2.0 / 3) * 3);
    }

    private Pose3D UnloadingPose(double t)
    {
        var recipe = _envelope!.Config.Loading;
        var lifted = FinalPose() with { Z = FinalPose().Z + recipe.LiftMm };
        return t < 0.5 ? Lerp(FinalPose(), lifted, t * 2) : Lerp(lifted, recipe.Unload, (t - 0.5) * 2);
    }

    private static Matrix4x4 PoseMatrix(Pose3D p)
    {
        const double factor = Math.PI / 180.0;
        var rotation = Matrix4x4.CreateRotationX((float)(p.Rx * factor)) *
                       Matrix4x4.CreateRotationY((float)(p.Ry * factor)) *
                       Matrix4x4.CreateRotationZ((float)(p.Rz * factor));
        return rotation * Matrix4x4.CreateTranslation((float)p.X, (float)p.Y, (float)p.Z);
    }

    private static Pose3D Lerp(Pose3D a, Pose3D b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new Pose3D(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t,
            a.Z + (b.Z - a.Z) * t, a.Rx + (b.Rx - a.Rx) * t,
            a.Ry + (b.Ry - a.Ry) * t, a.Rz + (b.Rz - a.Rz) * t);
    }
}
