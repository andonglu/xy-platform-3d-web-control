using System.Diagnostics;
using System.Text;
using DispenseHost.Net;
using DispenseHost.PatternPlan;

namespace DispenseHost.Testing;

public sealed class SelfTestOptions
{
    public string ServerUrl { get; set; } = "ws://localhost:8530/ws";
    public double CircleRadius { get; set; } = 30;
    public double RectWidth { get; set; } = 80;
    public double RectHeight { get; set; } = 50;
    public double CenterX { get; set; } = 0;
    public double CenterY { get; set; } = 0;
    public double Z { get; set; } = PatternPlanner.WorkHeight;
    public double Feed { get; set; } = 60;
    public int CircleSegments { get; set; } = 72;
    public bool IncludeCombined { get; set; } = true;
    public bool HomeBeforeEach { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 60;
}

public sealed record TestCaseResult(
    string Name, int Segments, int Points, double LengthMm, double Feed,
    int EchoPoints, bool Uploaded, bool Completed,
    double ElapsedSeconds, int MotionSamples,
    double MaxDeviationMm, double MaxZErrorMm,
    bool CollisionOccurred, string Notes)
{
    public bool Passed => Uploaded && Completed && !CollisionOccurred &&
                          MotionSamples > 0 && MaxDeviationMm < 1.5 && MaxZErrorMm < 0.5;
}

public sealed record TestReport(
    DateTime StartedAt, string ServerUrl, List<TestCaseResult> Cases, double TotalSeconds)
{
    public bool AllPassed => Cases.Count > 0 && Cases.All(c => c.Passed);

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("===== 平面点胶图案自动测试报告 =====");
        sb.AppendLine($"时间: {StartedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"服务端: {ServerUrl}");
        sb.AppendLine($"用例数: {Cases.Count}, 总耗时: {TotalSeconds:F1}s, 结论: {(AllPassed ? "全部通过" : "存在失败")}");
        sb.AppendLine();
        sb.AppendLine($"{"用例",-18}{"段",-5}{"点数",-8}{"长度mm",-11}{"回显",-7}{"采样",-7}{"偏差mm",-11}{"Z误差mm",-11}{"耗时s",-9}结果");
        sb.AppendLine(new string('-', 100));
        foreach (var c in Cases)
        {
            sb.AppendLine($"{c.Name,-18}{c.Segments,-5}{c.Points,-8}{c.LengthMm,-11:F1}" +
                          $"{c.EchoPoints,-7}{c.MotionSamples,-7}{c.MaxDeviationMm,-11:F3}" +
                          $"{c.MaxZErrorMm,-11:F3}{c.ElapsedSeconds,-9:F2}{(c.Passed ? "PASS" : "FAIL")}");
            if (!string.IsNullOrWhiteSpace(c.Notes))
                foreach (var line in c.Notes.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    sb.AppendLine($"    - {line.Trim()}");
        }
        sb.AppendLine();
        sb.AppendLine("说明:");
        sb.AppendLine("  - 偏差   = 服务端 30Hz 状态中「胶阀开启」期间实测 XY 到规划路径的最短距离");
        sb.AppendLine("  - Z 误差 = 实测 Z 与图案工作高度的最大差值");
        sb.AppendLine("  - 判定   = 已上传 + 作业完成 + 无撞机 + 有运动采样 + 偏差<1.5mm + Z误差<0.5mm");
        return sb.ToString();
    }
}

/// <summary>
/// 一键自动测试: 在上位机内生成平面圆形 / 矩形图案, 上传真实服务端并实际执行,
/// 采集 30Hz 状态轨迹, 校验服务端插补结果与规划路径的一致性。
/// GUI 按钮与 --selftest 命令行共用此实现。
/// </summary>
public sealed class SelfTestRunner
{
    public event Action<string>? OnLog;
    public event Action<TestCaseResult>? OnCaseCompleted;

    private readonly DispenseClient _client;
    private readonly SelfTestOptions _opt;

    public SelfTestRunner(DispenseClient client, SelfTestOptions? options = null)
    {
        _client = client;
        _opt = options ?? new SelfTestOptions();
    }

    private void Log(string s) => OnLog?.Invoke(s);

    public async Task<TestReport> RunAsync(CancellationToken ct = default)
    {
        var swTotal = Stopwatch.StartNew();
        var started = DateTime.Now;
        var cases = new List<TestCaseResult>();

        if (!_client.IsConnected)
        {
            Log($"连接服务端 {_opt.ServerUrl} ...");
            await _client.ConnectAsync(ct);
        }
        if (!_client.IsConnected) throw new InvalidOperationException("服务端未连接，自动测试中止");

        // 读取服务端轴配置用于范围校验
        var cfgWaiter = ValueWaiter<AxisConfigDto[]>.Start(
            h => _client.OnConfigData += h, h => _client.OnConfigData -= h,
            TimeSpan.FromSeconds(5));
        await _client.GetConfigAsync();
        var (cfgOk, cfg) = await cfgWaiter.WaitTask;
        AxisConfigDto[]? configs = cfgOk ? cfg : null;
        Log(cfgOk
            ? $"服务端轴配置: X[{configs![0].TravelMin},{configs[0].TravelMax}] " +
              $"Y[{configs[1].TravelMin},{configs[1].TravelMax}] Z[{configs[2].TravelMin},{configs[2].TravelMax}]"
            : "读取轴配置超时，跳过范围校验");

        var circle = PatternPlanner.Plan(new PatternSpec
        {
            Kind = PatternKind.Circle, Name = "圆形",
            CenterX = _opt.CenterX, CenterY = _opt.CenterY,
            Radius = _opt.CircleRadius, Segments = _opt.CircleSegments,
            Z = _opt.Z, Feed = _opt.Feed,
        });
        var rect = PatternPlanner.Plan(new PatternSpec
        {
            Kind = PatternKind.Rectangle, Name = "矩形",
            CenterX = _opt.CenterX, CenterY = _opt.CenterY,
            Width = _opt.RectWidth, Height = _opt.RectHeight,
            Z = _opt.Z, Feed = _opt.Feed,
        });

        var circleJob = new PatternJob();
        circleJob.Add(circle);
        var rectJob = new PatternJob();
        rectJob.Add(rect);
        var plan = new List<(string Name, PatternJob Job)>
        {
            ("圆形图案", circleJob),
            ("矩形图案", rectJob),
        };
        if (_opt.IncludeCombined)
        {
            var combo = new PatternJob();
            combo.Add(circle); combo.Add(rect);
            plan.Add(("圆形+矩形组合", combo));
        }

        foreach (var (name, job) in plan)
        {
            ct.ThrowIfCancellationRequested();
            Log($"--- 用例: {name} ({job.Patterns.Count} 段, {job.TotalPoints} 点, {job.TotalLength:F1}mm) ---");
            var result = await RunCaseAsync(name, job, configs, ct);
            cases.Add(result);
            OnCaseCompleted?.Invoke(result);
            Log($"    结果: {(result.Passed ? "PASS" : "FAIL")}  偏差 {result.MaxDeviationMm:F3}mm, " +
                $"Z 误差 {result.MaxZErrorMm:F3}mm, 采样 {result.MotionSamples}, 耗时 {result.ElapsedSeconds:F2}s");
        }

        swTotal.Stop();
        return new TestReport(started, _opt.ServerUrl, cases, swTotal.Elapsed.TotalSeconds);
    }

    private async Task<TestCaseResult> RunCaseAsync(
        string name, PatternJob job, AxisConfigDto[]? configs, CancellationToken ct)
    {
        var notes = new List<string>();
        foreach (var w in job.ValidateAgainst(configs)) notes.Add("警告: " + w);

        // 确保设备处于可运行状态: 清除残留报警 -> 回零
        if (_client.LastState.Collision)
        {
            Log("    检测到残留撞机报警, 执行 reset_alarm");
            await _client.ResetAlarmAsync();
            await Task.Delay(200, ct);
        }
        if (_opt.HomeBeforeEach)
        {
            var homeWaiter = MessageWaiter.Start(_client, TimeSpan.FromSeconds(20),
                (_, m) => m.Contains("回零完成") || m.Contains("回零失败"));
            await _client.HomeAsync();
            var home = await homeWaiter.WaitTask;
            if (!home.Ok) notes.Add("回零等待超时");
            else if (home.Msg.Contains("失败")) notes.Add("回零失败: " + home.Msg);
        }

        // 订阅 path 回显 + state 采样 (胶阀开启 = 真实点胶运动)
        int echoPoints = 0;
        void OnPath(PathPreviewDto p) => echoPoints = p.Points.Count;
        var motion = new List<MachineStateDto>();
        void OnState(MachineStateDto s) { if (s.Valve) motion.Add(s); }

        _client.OnPath += OnPath;
        _client.OnState += OnState;

        var sw = Stopwatch.StartNew();
        bool collision = false;
        string completionMsg;
        try
        {
            await _client.UploadStepsAsync("selftest_" + name, job.ToUploadSteps());
            await Task.Delay(150, ct);

            var doneWaiter = MessageWaiter.Start(_client, TimeSpan.FromSeconds(_opt.TimeoutSeconds),
                (_, m) => m.Contains("点胶作业完成") || m.Contains("急停") ||
                          m.Contains("因撞机中止") || m.Contains("没有可执行的路径"));
            await _client.RunJobAsync();
            var done = await doneWaiter.WaitTask;
            sw.Stop();

            completionMsg = done.Ok ? done.Msg : "超时未完成";
            if (!done.Ok) notes.Add($"执行超时 ({_opt.TimeoutSeconds}s)");
        }
        catch (Exception ex)
        {
            sw.Stop();
            completionMsg = "异常: " + ex.Message;
            notes.Add(completionMsg);
        }
        finally
        {
            _client.OnPath -= OnPath;
            _client.OnState -= OnState;
        }

        bool completed = completionMsg.Contains("点胶作业完成");
        if (!completed && !notes.Any(n => n.Contains("异常"))) notes.Add("结束事件: " + completionMsg);
        collision = completionMsg.Contains("撞机") || completionMsg.Contains("急停") ||
                    _client.LastState.Collision;
        if (collision) notes.Add("执行期间发生撞机/急停");

        double maxDev = 0, maxZErr = 0;
        if (motion.Count > 0)
        {
            foreach (var s in motion)
            {
                double d = job.Patterns.Min(p => PatternPlanner.DistanceToPathXY(s.X.Pos, s.Y.Pos, p.Points));
                if (d > maxDev) maxDev = d;
                double ze = Math.Abs(s.Z.Pos - _opt.Z);
                if (ze > maxZErr) maxZErr = ze;
            }
        }
        else notes.Add("未采集到开胶期间的运动状态样本");

        return new TestCaseResult(
            Name: name,
            Segments: job.Patterns.Count,
            Points: job.TotalPoints,
            LengthMm: job.TotalLength,
            Feed: _opt.Feed,
            EchoPoints: echoPoints,
            Uploaded: echoPoints > 0,
            Completed: completed,
            ElapsedSeconds: sw.Elapsed.TotalSeconds,
            MotionSamples: motion.Count,
            MaxDeviationMm: maxDev,
            MaxZErrorMm: maxZErr,
            CollisionOccurred: collision,
            Notes: string.Join("\n", notes));
    }
}

/// <summary>单值事件等待器 (config / path 等)</summary>
internal sealed class ValueWaiter<T>
{
    private readonly Action _unsubscribe;
    private readonly TaskCompletionSource<(bool Ok, T Value)> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<(bool Ok, T Value)> WaitTask => _tcs.Task;

    private ValueWaiter(Action unsubscribe, TimeSpan timeout)
    {
        _unsubscribe = unsubscribe;
        _ = TimeoutAsync(timeout);
    }

    private async Task TimeoutAsync(TimeSpan timeout)
    {
        await Task.Delay(timeout);
        if (_tcs.TrySetResult((false, default!))) _unsubscribe();
    }

    private void Signal(T value)
    {
        if (_tcs.TrySetResult((true, value))) _unsubscribe();
    }

    public static ValueWaiter<T> Start(
        Action<Action<T>> subscribe, Action<Action<T>> unsubscribe, TimeSpan timeout)
    {
        ValueWaiter<T>? w = null;
        Action<T> handler = v => w?.Signal(v);
        w = new ValueWaiter<T>(() => unsubscribe(handler), timeout);
        subscribe(handler);
        return w;
    }
}

/// <summary>服务端 event 消息等待器 (level, msg)</summary>
internal sealed class MessageWaiter
{
    private readonly Action _unsubscribe;
    private readonly TaskCompletionSource<(bool Ok, string Level, string Msg)> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<(bool Ok, string Level, string Msg)> WaitTask => _tcs.Task;

    private MessageWaiter(Action unsubscribe, TimeSpan timeout)
    {
        _unsubscribe = unsubscribe;
        _ = TimeoutAsync(timeout);
    }

    private async Task TimeoutAsync(TimeSpan timeout)
    {
        await Task.Delay(timeout);
        if (_tcs.TrySetResult((false, "", ""))) _unsubscribe();
    }

    private void Signal(string level, string msg)
    {
        if (_tcs.TrySetResult((true, level, msg))) _unsubscribe();
    }

    public static MessageWaiter Start(
        DispenseClient client, TimeSpan timeout, Func<string, string, bool> predicate)
    {
        MessageWaiter? w = null;
        void Handler(string l, string m) { if (predicate(l, m)) w?.Signal(l, m); }
        Action<string, string> handler = Handler;
        w = new MessageWaiter(() => client.OnEventData -= handler, timeout);
        client.OnEventData += handler;
        return w;
    }
}
