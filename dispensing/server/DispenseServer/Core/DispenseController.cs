using DispenseServer.Collision;

namespace DispenseServer.Core;

/// <summary>
/// 3D 点胶运动控制器: 接收上位机上传的路径，执行 3D 插补运动。
/// 与激光项目的 MotionController 完全独立。
///
/// 工作流程:
///   上位机 → 上传路径 (upload) → 服务端执行 (run)
///   执行: PTP 到起点 → 下降到工作高度 → 开胶 → 沿 3D 路径插补 → 关胶 → 抬升
///   撞机检测每周期运行，触发即停机报警
///
/// 撞机恢复流程:
///   1. 撞机触发 → EmergencyStop (轴 Faulted) → CollisionAlarm=true
///   2. reset_alarm → ClearFault (轴 Idle, 位置不动) → CollisionAlarm=false
///   3. home → 三轴朝零点开关慢速运动 → 碰到开关 → 修正到 HomePos
///   4. 恢复正常运动
/// </summary>
public sealed class DispenseController
{
    private readonly IDispenseAxisCard _card;
    private readonly CollisionChecker _collision;
    private readonly I3DInterpolationEngine _engine;

    public bool ValveOn { get; private set; }
    public bool CollisionAlarm { get; private set; }
    public string? CollisionMessage { get; private set; }

    private sealed class RunState
    {
        public List<DispensePath3D> Paths = new();
        public int SegIdx;          // 当前 DispensePath3D 索引
        public double S, V, Total, Feed;
        public List<I3DPath> CurrentSegs = new();
        public int SubIdx;
        public double SubS, SubTotal;
        public ValveTiming Timing;          // 当前段的出胶时序参数
        public double PhaseTimer;           // OpenDelay / CloseDelay 相计时
        public bool ValveClosedEarly;       // 提前关胶是否已触发
        public List<Point3D[]>? SegmentNormals;  // P4: 当前段各子段的起/终点法向
        public double LastA, LastC;              // P4: 上一插补周期的 A/C 角 (角速度估算)
        public bool RtcpWarned, AxisLimitWarned; // P4: 单次告警锁
        public enum Phase { PtpStart, Descend, OpenDelay, Dispensing, CloseDelay, Ascending, Done }
        public Phase CurrentPhase = Phase.PtpStart;
        public Point3D StartPoint;
    }
    private RunState? _run;
    private readonly int[] _jogDir = new int[Axes.Count];
    private bool _homing;
    private int _homeDoneCount;
    private double _t, _emitAcc;

    public event Action<object>? OnState;
    public event Action<object>? OnPath;
    public event Action<string, string>? OnEvent;   // (level, msg)

    private const double PenDownZ = 8;   // 点胶工作高度
    private const double PenUpZ = 30;    // 安全高度
    private const double MaxAccel = 600; // mm/s² (点胶路径插补加减速)

    // ---- 出胶时序诊断 (供验证程序与 UI 读取) ----
    /// <summary>最近一次关阀时距路径终点的剩余弧长 (mm); -1 表示本轮未发生关阀</summary>
    public double LastValveOffRemaining { get; private set; } = -1;
    /// <summary>最近一次关阀时的插补速度 (mm/s)</summary>
    public double LastValveOffVelocity { get; private set; }

    // ---- P4: 五轴 RTCP 运动学 ----
    /// <summary>控制器名义运动学模型 (RTCP 逆解用; 标定后逼近真实几何)</summary>
    public FiveAxisKinematics Kinematics { get; } = new();
    /// <summary>真实机床几何 (模拟物理世界的"真值", 出厂带误差; 验证程序以此计算实际 TCP)</summary>
    public FiveAxisKinematics RealGeometry { get; } = new()
    {
        // 出厂误差: A 轴轴线偏 0.9°, C 轴偏 0.7°, 枢轴偏移 (0.3, -0.2, 0.15)mm
        AxisA = FiveAxisKinematics.Rotate(new Point3D(1, 0, 0), new Point3D(0, 0, 1), 0.9 * Math.PI / 180),
        AxisC = FiveAxisKinematics.Rotate(new Point3D(0, 0, 1), new Point3D(1, 0, 0), -0.7 * Math.PI / 180),
        PivotError = new Point3D(0.3, -0.2, 0.15),
    };
    public bool RtcpEnabled { get; private set; }
    public double LastCalibResidual { get; private set; } = -1;
    /// <summary>P8: 相邻编程点允许的最大 A/C 姿态变化, 超限拒绝执行</summary>
    public double MaxPoseStepDeg { get; set; } = 30;

    /// <summary>RTCP 开关: 开=带法向路径按五轴联动 (TCP 轨迹编程); 关=按三轴直线执行</summary>
    public void SetRtcp(bool on)
    {
        if (_run is not null) { OnEvent?.Invoke("warn", "作业运行中无法切换 RTCP"); return; }
        RtcpEnabled = on;
        OnEvent?.Invoke("ok", on ? "RTCP 已开启 (五轴联动, TCP 轨迹控制)" : "RTCP 已关闭 (三轴直线插补)");
    }

    /// <summary>
    /// RTCP 标定: 真实机床 (RealGeometry) 作为测量源, 单高度多姿态探测 + 7 参数
    /// 联合高斯-牛顿拟合 A/C 轴线误差与枢轴误差, 写回名义模型 Kinematics。
    /// 标定后编程 TCP 轨迹与实际 TCP 的系统性偏离消除。
    /// </summary>
    public void CalibrateRtcp()
    {
        if (_run is not null) { OnEvent?.Invoke("error", "作业运行中无法标定"); return; }
        // 探针前先让旋转轴归零, 模拟真实标定流程
        for (int i = 0; i < Axes.Count; i++) { _jogDir[i] = 0; _card.SetTarget(i, 0, 60); }
        double res = Kinematics.Calibrate((pivot, aDeg, cDeg) => RealGeometry.Forward(pivot, aDeg, cDeg));
        LastCalibResidual = res;
        OnEvent?.Invoke("ok", $"RTCP 标定完成: 轴线/枢轴误差已修正, 残差 {res:F4}mm " +
                              $"(A轴=[{Kinematics.AxisA.X:F4},{Kinematics.AxisA.Y:F4},{Kinematics.AxisA.Z:F4}], " +
                              $"C轴=[{Kinematics.AxisC.X:F4},{Kinematics.AxisC.Y:F4},{Kinematics.AxisC.Z:F4}])");
    }

    public DispenseController(IDispenseAxisCard card, CollisionChecker collision, I3DInterpolationEngine engine)
    {
        _card = card;
        _collision = collision;
        _engine = engine;
        _card.Initialize();
        _collision.OnCollision += msg =>
        {
            // 硬撞机 (超出边界/进入禁止区) → 停机报警
            // 软预警 (接近边界/接近禁止区) → 仅提示, 不停机
            bool isHard = msg.StartsWith("[硬撞机]");
            if (isHard)
            {
                CollisionAlarm = true;
                CollisionMessage = msg;
                EmergencyStop();
                OnEvent?.Invoke("error", msg);
            }
            else
            {
                // 软预警用 warn 级别, 不停机
                OnEvent?.Invoke("warn", msg);
            }
        };
    }

    // ---------------- 指令入口 ----------------

    /// <summary>
    /// 回零: 三轴朝零点开关方向慢速运动, 碰到开关后修正到 HomePos。
    /// 撞机报警状态下拒绝执行 (需先 ResetAlarm)。
    /// </summary>
    public void Home()
    {
        if (CollisionAlarm)
        {
            OnEvent?.Invoke("error", "撞机报警未复位，无法回零，请先执行 reset_alarm");
            return;
        }
        _run = null;
        _homeDoneCount = 0;
        for (int i = 0; i < Axes.Count; i++)
        {
            _jogDir[i] = 0;
            _card.StartHome(i);
        }
        _homing = true;
        OnEvent?.Invoke("ok", "回零开始: 五轴朝零点开关运动");
    }

    public void Ptp(int axis, double pos)
    {
        if (_run is not null || axis < 0) return;
        if (CollisionAlarm)
        {
            OnEvent?.Invoke("error", "撞机报警未复位，无法运动，请先执行 reset_alarm");
            return;
        }
        if (_homing)
        {
            OnEvent?.Invoke("warn", "正在回零，无法运动");
            return;
        }
        _jogDir[axis] = 0;
        _card.SetTarget(axis, pos, axis == Axes.Z ? 60 : 120);
    }

    public void Jog(int axis, int dir)
    {
        if (_run is not null || axis < 0) return;
        if (CollisionAlarm)
        {
            OnEvent?.Invoke("error", "撞机报警未复位，无法点动，请先执行 reset_alarm");
            return;
        }
        if (_homing)
        {
            OnEvent?.Invoke("warn", "正在回零，无法点动");
            return;
        }
        _jogDir[axis] = Math.Sign(dir);
    }

    public void SetValve(bool on) => ValveOn = on;

    /// <summary>上传点胶路径; P8 五轴预检查失败时清空作业并返回 false</summary>
    public bool UploadPaths(List<DispensePath3D> paths, List<double[]> preview, string name)
    {
        if (!ValidateFiveAxisPaths(paths))
        {
            _run = null;
            ValveOn = false;
            return false;
        }
        _run = new RunState { Paths = paths, StartPoint = GetFirstPoint(paths) };
        CurrentJobName = name;
        OnPath?.Invoke(new { type = "path", name, points3d = preview });
        OnEvent?.Invoke("ok", $"路径已上传: {paths.Count} 段, 总长 {paths.Sum(p => p.Segments.Sum(s => s.Length)):F1}mm");
        return true;
    }

    private bool ValidateFiveAxisPaths(List<DispensePath3D> paths)
    {
        if (!RtcpEnabled || paths.All(p => p.SegmentNormals is null)) return true;
        var report = FiveAxisPathGuard.Validate(paths, Kinematics, GetConfigs(), MaxPoseStepDeg);
        if (report.IsSafe) return true;
        string details = string.Join("；", report.Errors.Take(3));
        if (report.Errors.Count > 3) details += $"；另 {report.Errors.Count - 3} 项";
        OnEvent?.Invoke("error", $"五轴预检查失败 ({report.Errors.Count} 项): {details}");
        return false;
    }

    /// <summary>当前装载的作业名 (脱机程序运行/上传时更新)</summary>
    public string? CurrentJobName { get; private set; }

    /// <summary>是否有作业在运行 (IO/上位机互锁用)</summary>
    public bool IsRunning => _run is not null;

    /// <summary>任一轴处于 Faulted (IO 报警灯: 撞机报警或急停停轴都算故障)</summary>
    public bool AnyAxisFaulted => Enumerable.Range(0, Axes.Count).Any(i => _card.Read(i).State == AxisState.Faulted);

    /// <summary>附加虚拟 IO 层 (脱机面板/PLC 模拟); 附加后 Tick 驱动其扫描, 状态包携带 io 字段</summary>
    public IoController? Io { get; private set; }
    public void AttachIo(IoController io) => Io = io;

    /// <summary>内部事件通道 (供 IO 层等内部模块上报)</summary>
    public void ReportEvent(string level, string msg) => OnEvent?.Invoke(level, msg);

    /// <summary>开始执行已上传的路径</summary>
    public void RunJob()
    {
        var r = _run;
        if (r is null || r.Paths.Count == 0)
        {
            OnEvent?.Invoke("error", "没有可执行的路径，请先上传");
            return;
        }
        if (CollisionAlarm)
        {
            OnEvent?.Invoke("error", "撞机报警未复位，无法运行，请先执行 reset_alarm + home");
            return;
        }
        if (_homing)
        {
            OnEvent?.Invoke("error", "正在回零，无法运行");
            return;
        }
        // 上传后轴配置/标定可能改变, 每次运行前必须重新做最终安全检查
        if (!ValidateFiveAxisPaths(r.Paths))
        {
            _run = null;
            ValveOn = false;
            return;
        }
        r.CurrentPhase = RunState.Phase.PtpStart;
        r.SegIdx = 0;
        r.S = 0; r.V = 0;
        r.SubIdx = 0; r.SubS = 0;
        r.Timing = ValveTiming.Zero;
        r.PhaseTimer = 0;
        r.ValveClosedEarly = false;
        LastValveOffRemaining = -1;
        LastValveOffVelocity = 0;

        // PTP 到第一段起点
        var first = r.Paths[0];
        var firstPt = first.Segments[0].PointAt(0);
        _card.SetTarget(Axes.X, firstPt.X, 150);
        _card.SetTarget(Axes.Y, firstPt.Y, 150);
        _card.SetTarget(Axes.Z, PenUpZ, 60);
        OnEvent?.Invoke("ok", $"开始执行点胶作业, 起点 ({firstPt.X:F1}, {firstPt.Y:F1}, {firstPt.Z:F1})");
    }

    /// <summary>急停: 停止所有运动, 轴进入 Faulted</summary>
    public void EmergencyStop()
    {
        _run = null;
        _homing = false;
        ValveOn = false;
        _card.StopAll();
        OnEvent?.Invoke("error", "急停触发, 轴已停止 (Faulted)");
    }

    /// <summary>
    /// 复位报警: 清除撞机报警标志, 轴从 Faulted 恢复到 Idle (位置不动)。
    /// 复位后需要执行 Home 回零才能恢复正常运动。
    /// </summary>
    public void ResetAlarm()
    {
        if (!CollisionAlarm && _card.Read(Axes.X).State != AxisState.Faulted)
        {
            OnEvent?.Invoke("warn", "当前无报警需要复位");
            return;
        }
        CollisionAlarm = false;
        CollisionMessage = null;
        _card.ClearFault();  // 轴从 Faulted → Idle, 位置保持
        OnEvent?.Invoke("ok", "撞机报警已复位, 轴已恢复 (位置未变), 建议执行 home 回零");
    }

    /// <summary>配置轴参数 (行程/零点/传感器位置)</summary>
    public void ConfigureAxis(int axis, AxisConfig cfg)
    {
        _card.ConfigureAxis(axis, cfg);
        OnEvent?.Invoke("ok", $"轴 {Axes.Names[axis]} 已配置: 行程 {cfg.TravelMin}~{cfg.TravelMax}, 零点 {cfg.HomePos}, " +
                              $"传感器 N{cfg.LimitNegative}/H{cfg.HomeSensorPos}/P{cfg.LimitPositive}");
    }

    /// <summary>获取所有轴配置</summary>
    public List<AxisConfig> GetConfigs()
    {
        var list = new List<AxisConfig>();
        for (int i = 0; i < Axes.Count; i++) list.Add(_card.GetConfig(i));
        return list;
    }

    private static Point3D GetFirstPoint(List<DispensePath3D> paths)
    {
        if (paths.Count == 0 || paths[0].Segments.Count == 0) return new Point3D(0, 0, PenUpZ);
        return paths[0].Segments[0].PointAt(0);
    }

    // ---------------- 主循环 ----------------

    public void Tick(double dt)
    {
        _t += dt;

        TickJog(dt);
        TickRun(dt);
        _card.Tick(dt);
        Io?.Tick(dt);

        // 撞机检测 (每周期), 只在非报警时检测
        if (!CollisionAlarm)
        {
            var (triggered, isHard) = _collision.Check();
            if (triggered && isHard)
            {
                // 硬撞机: 停机+报警 (CollisionChecker.OnCollision 回调会处理)
            }
            // 软预警由 OnCollision 事件回调触发 OnEvent, 不停机
        }

        // 传感器极限触发检测 (轴卡自身的极限传感器, 独立于 CollisionChecker)
        if (!CollisionAlarm)
        {
            for (int i = 0; i < Axes.Count; i++)
            {
                var st = _card.Read(i);
                var sen = _card.ReadSensors(i);
                if (st.State == AxisState.Faulted && sen.AnyLimit)
                {
                    // 极限传感器触发的 Faulted -> 设置撞机报警
                    string axisName = Axes.Names[i];
                    string limitName = sen.LimitPositive ? "正极限" : "负极限";
                    CollisionAlarm = true;
                    CollisionMessage = $"[硬撞机] 轴 {axisName} 触发{limitName}传感器! 位置 {st.Pos:F1}mm";
                    EmergencyStop();
                    OnEvent?.Invoke("error", CollisionMessage);
                    break;
                }
            }
        }

        // 回零完成检测: 三轴都回到 Idle (碰到零点开关后 StopHome 会设 Idle)
        if (_homing)
        {
            int doneCount = 0;
            for (int i = 0; i < Axes.Count; i++)
            {
                var st = _card.Read(i);
                if (st.State != AxisState.Homing && st.State != AxisState.Moving)
                    doneCount++;
            }
            if (doneCount == Axes.Count)
            {
                _homing = false;
                // 检查是否有轴回零失败 (Faulted)
                bool anyFaulted = false;
                for (int i = 0; i < Axes.Count; i++)
                {
                    if (_card.Read(i).State == AxisState.Faulted)
                    {
                        anyFaulted = true;
                        OnEvent?.Invoke("error", $"轴 {Axes.Names[i]} 回零失败 (可能撞到极限)");
                    }
                }
                if (!anyFaulted)
                    OnEvent?.Invoke("ok", "回零完成, 五轴已在零点位置");
            }
        }

        _emitAcc += dt;
        if (_emitAcc >= 1.0 / 30)
        {
            _emitAcc = 0;
            OnState?.Invoke(BuildState());
        }
    }

    private void TickJog(double dt)
    {
        if (_run is not null || _homing) return;
        for (int i = 0; i < Axes.Count; i++)
        {
            if (_jogDir[i] == 0) continue;
            var st = _card.Read(i);
            double next = st.Pos + _jogDir[i] * 50 * dt;
            _card.SetPosition(i, next, _jogDir[i] * 50);
        }
    }

    private void TickRun(double dt)
    {
        var r = _run;
        if (r is null) return;

        // 撞机时中止作业
        if (CollisionAlarm)
        {
            _run = null;
            ValveOn = false;
            OnEvent?.Invoke("error", "点胶作业因撞机中止");
            return;
        }

        switch (r.CurrentPhase)
        {
            case RunState.Phase.PtpStart:
                // 等待 PTP 到位 (XY 到起点, Z 在安全高度)
                if (Enumerable.Range(0, Axes.Count).Any(i => _card.Read(i).State == AxisState.Moving)) return;
                // P4 五轴: RTCP 开且带法向时, 下降目标必须是逆解枢轴位置 (含 L·t 补偿),
                // 否则枢轴被降到 TCP 高度, 针尖穿出工作面, 开阀瞬间 TCP 偏离达一个工具长
                var segNormals0 = r.Paths[r.SegIdx].SegmentNormals;
                if (segNormals0 is not null && RtcpEnabled)
                {
                    var sol = Kinematics.SolveTcp(r.Paths[r.SegIdx].Segments[0].PointAt(0), segNormals0[0][0]);
                    _card.SetTarget(Axes.X, sol.pivot.X, 40);
                    _card.SetTarget(Axes.Y, sol.pivot.Y, 40);
                    _card.SetTarget(Axes.Z, sol.pivot.Z, 40);
                    // P8: 段间 PTP 选靠近 C 行程中心的等价角, 给段内连续展开预留空间
                    var (cMin, cMax) = _card.GetLimit(Axes.C);
                    double startC = FiveAxisPathGuard.CenterInitialAngle(sol.cDeg, cMin, cMax);
                    _card.SetTarget(Axes.A, sol.aDeg, 90);
                    _card.SetTarget(Axes.C, startC, 120);
                }
                else
                {
                    // 下降到该段起点的工作高度 (分层 3D 路径时各层 Z 不同, 不再固定 PenDownZ)
                    double workZ = r.Paths[r.SegIdx].Segments[0].PointAt(0).Z;
                    _card.SetTarget(Axes.Z, workZ, 40);
                }
                r.CurrentPhase = RunState.Phase.Descend;
                break;

            case RunState.Phase.Descend:
                if (Enumerable.Range(0, Axes.Count).Any(i => _card.Read(i).State == AxisState.Moving)) return;
                // 装载本段出胶时序参数, 再决定直接开胶还是先等开阀延时
                r.Timing = r.Paths[r.SegIdx].Timing;
                r.ValveClosedEarly = false;
                r.PhaseTimer = 0;
                if (r.Timing.HasOpenDelay)
                {
                    r.CurrentPhase = RunState.Phase.OpenDelay;
                }
                else
                {
                    BeginDispensing(r);
                }
                break;

            case RunState.Phase.OpenDelay:
                // 开阀延时: 等胶路充满针头, 防止起点缺胶
                r.PhaseTimer += dt;
                if (r.PhaseTimer >= r.Timing.OpenDelaySec) BeginDispensing(r);
                break;

            case RunState.Phase.Dispensing:
                TickDispensing(r, dt);
                break;

            case RunState.Phase.CloseDelay:
                // 关胶保持: 等回吸完成再抬枪, 防拖尾
                r.PhaseTimer += dt;
                if (r.PhaseTimer >= r.Timing.CloseDelaySec) BeginAscend(r);
                break;

            case RunState.Phase.Ascending:
                if (_card.Read(Axes.Z).State == AxisState.Moving) return;
                // 下一段 or 完成
                r.SegIdx++;
                if (r.SegIdx >= r.Paths.Count)
                {
                    r.CurrentPhase = RunState.Phase.Done;
                    ValveOn = false;
                    OnEvent?.Invoke("ok", "点胶作业完成");
                    _run = null;
                    return;
                }
                // PTP 到下一段起点
                var nextFirst = r.Paths[r.SegIdx].Segments[0].PointAt(0);
                _card.SetTarget(Axes.X, nextFirst.X, 150);
                _card.SetTarget(Axes.Y, nextFirst.Y, 150);
                _card.SetTarget(Axes.Z, PenUpZ, 60);
                r.CurrentPhase = RunState.Phase.PtpStart;
                break;
        }
    }

    /// <summary>开胶并进入路径插补 (Descend/OpenDelay 相的汇合点)</summary>
    private void BeginDispensing(RunState r)
    {
        ValveOn = r.Paths[r.SegIdx].DispenseOn;
        r.CurrentSegs = r.Paths[r.SegIdx].Segments;
        r.Total = r.CurrentSegs.Sum(s => s.Length);
        r.S = 0; r.V = 0;
        r.SubIdx = 0; r.SubS = 0;
        r.SubTotal = r.CurrentSegs[0].Length;
        r.Feed = r.Paths[r.SegIdx].Feed;
        r.SegmentNormals = r.Paths[r.SegIdx].SegmentNormals;
        r.RtcpWarned = false;
        r.AxisLimitWarned = false;
        r.LastA = _card.Read(Axes.A).Pos;
        r.LastC = _card.Read(Axes.C).Pos;
        r.CurrentPhase = RunState.Phase.Dispensing;
    }

    /// <summary>抬枪: 安全高度 + 该段退枪高度 (RetractMm)</summary>
    private void BeginAscend(RunState r)
    {
        _card.SetTarget(Axes.Z, PenUpZ + r.Timing.RetractMm, 60);
        r.CurrentPhase = RunState.Phase.Ascending;
    }

    private void TickDispensing(RunState r, double dt)
    {
        // 梯形速度规划 (减速距离约束)
        double dist = r.Total - r.S;
        double vStop = Math.Sqrt(2 * MaxAccel * Math.Max(dist, 0));
        double vWant = Math.Min(r.Feed, vStop);
        double dv = MaxAccel * dt;
        r.V += Math.Clamp(vWant - r.V, -dv, dv);
        r.S += r.V * dt;
        r.SubS += r.V * dt;

        // 提前关胶: 剩余弧长 ≤ 胶尾长度 时关阀, 保证胶路末端恰好到终点、不堆胶。
        // 胶尾 = 关阀后针头在胶水继续流出的时间 t 内移动的距离, 两种工况:
        //   ① t 秒内未停止 (v/a > t): 胶尾 = v·t - ½·a·t²   (匀减速段)
        //   ② t 秒内已停止 (v/a ≤ t): 胶尾 = v²/(2a)        (制动距离;
        //      停车后剩余胶量堆积在终点, 胶尾不再增长)
        // 注意不能用 min/max 统一 —— 必须按时停/未停分档, 否则低进给时
        // (v·t-½at² < 制动距离) 会判成"胶尾过短"而永远不到关阀点。
        if (ValveOn && r.Timing.HasEarlyClose)
        {
            double t = r.Timing.EarlyCloseSec;
            double stopTime = r.V / MaxAccel;
            double stopDist = stopTime <= t
                ? r.V * r.V / (2 * MaxAccel)
                : Math.Max(0, r.V * t - 0.5 * MaxAccel * t * t);
            double remaining = r.Total - r.S;
            if (remaining <= stopDist)
            {
                ValveOn = false;
                r.ValveClosedEarly = true;
                LastValveOffRemaining = remaining;
                LastValveOffVelocity = r.V;
            }
        }

        // 在当前子段内取插补点
        while (r.SubS > r.SubTotal && r.SubIdx < r.CurrentSegs.Count - 1)
        {
            r.SubS -= r.SubTotal;
            r.SubIdx++;
            r.SubTotal = r.CurrentSegs[r.SubIdx].Length;
        }

        double localS = Math.Min(r.SubS, r.SubTotal);
        var pt = r.CurrentSegs[r.SubIdx].PointAt(localS);

        if (r.SegmentNormals is not null && RtcpEnabled)
        {
            // P4 RTCP 五轴联动: 插补点 + 插值法向 → 逆解出枢轴位置与 A/C 角
            var nn = r.SegmentNormals[r.SubIdx];
            double frac = r.SubTotal <= 0 ? 0 : Math.Min(r.SubS / r.SubTotal, 1);
            var n = (nn[0] + (nn[1] - nn[0]) * frac).Normalize();
            var sol = Kinematics.SolveTcp(pt, n);
            // P8: C 轴只在出胶段内展开到上一周期 ±180° 邻域; 跨 ±180° 不再反转 358°
            bool singular = Math.Sin(sol.aDeg * Math.PI / 180) < 1e-8;
            double cDeg = singular ? r.LastC : FiveAxisPathGuard.UnwrapAngleDeg(sol.cDeg, r.LastC);
            double safeDt = Math.Max(dt, 1e-6);
            double angVelA = Math.Abs(sol.aDeg - r.LastA) / safeDt;
            double angVelC = Math.Abs(cDeg - r.LastC) / safeDt;
            r.LastA = sol.aDeg; r.LastC = cDeg;
            var (aMin, aMax) = _card.GetLimit(Axes.A);
            var (cMin, cMax) = _card.GetLimit(Axes.C);
            if (!r.AxisLimitWarned && (sol.aDeg < aMin || sol.aDeg > aMax || cDeg < cMin || cDeg > cMax))
            {
                r.AxisLimitWarned = true;
                OnEvent?.Invoke("warn", $"A/C 角 ({sol.aDeg:F1}°, {cDeg:F1}°) 超出行程 [{aMin}~{aMax}]/[{cMin}~{cMax}], 已限位, TCP 将偏离轨迹");
            }
            _card.SetPosition(Axes.X, sol.pivot.X, r.V);
            _card.SetPosition(Axes.Y, sol.pivot.Y, r.V);
            _card.SetPosition(Axes.Z, sol.pivot.Z, r.V);
            _card.SetPosition(Axes.A, sol.aDeg, angVelA);
            _card.SetPosition(Axes.C, cDeg, angVelC);
        }
        else
        {
            if (r.SegmentNormals is not null && !r.RtcpWarned)
            {
                r.RtcpWarned = true;
                OnEvent?.Invoke("warn", "路径含曲面法向但 RTCP 未开启, 按三轴直线执行 (姿态不控制)");
            }
            _card.SetPosition(Axes.X, pt.X, r.V);
            _card.SetPosition(Axes.Y, pt.Y, r.V);
            _card.SetPosition(Axes.Z, pt.Z, r.V);
        }

        if (r.S >= r.Total)
        {
            r.V = 0;
            if (ValveOn)
            {
                ValveOn = false;
                LastValveOffRemaining = 0;
                LastValveOffVelocity = 0;
            }
            // 关胶保持 (回吸完成) 后再抬枪
            r.PhaseTimer = 0;
            if (r.Timing.HasCloseDelay)
            {
                r.CurrentPhase = RunState.Phase.CloseDelay;
            }
            else
            {
                BeginAscend(r);
            }
        }
    }

    private object BuildState()
    {
        var sx = _card.Read(Axes.X);
        var sy = _card.Read(Axes.Y);
        var sz = _card.Read(Axes.Z);
        var sa = _card.Read(Axes.A);
        var sc = _card.Read(Axes.C);

        // 传感器信号
        var senX = _card.ReadSensors(Axes.X);
        var senY = _card.ReadSensors(Axes.Y);
        var senZ = _card.ReadSensors(Axes.Z);
        var senA = _card.ReadSensors(Axes.A);
        var senC = _card.ReadSensors(Axes.C);

        object Pack(AxisStatus s, SensorState sen) => new
        {
            pos = Math.Round(s.Pos, 3),
            vel = Math.Round(s.Vel, 2),
            state = _homing ? "Homing" : (CollisionAlarm ? "Faulted" : s.State.ToString()),
            sensors = new { home = sen.Home, limPos = sen.LimitPositive, limNeg = sen.LimitNegative },
        };

        // 虚拟 IO 状态 (附加后才有; 脱机面板/PLC 模拟)
        object? io = Io is null ? null : new
        {
            start = Io.InStart,
            pause = Io.InPause,
            reset = Io.InReset,
            selectBcd = Io.SelectBcd,
            running = Io.State().Running,
            done = Io.State().Done,
            error = Io.State().Error,
            valve = Io.State().Valve,
        };

        return new
        {
            type = "state",
            t = Math.Round(_t, 3),
            x = Pack(sx, senX),
            y = Pack(sy, senY),
            z = Pack(sz, senZ),
            a = Pack(sa, senA),
            c = Pack(sc, senC),
            valve = ValveOn,
            mode = _run is not null ? $"Dispense:{_run.CurrentPhase}" : (_homing ? "Homing" : "Idle"),
            job = CurrentJobName,
            rtcp = new
            {
                enabled = RtcpEnabled,
                calibrated = Kinematics.Calibrated,
                residualMm = LastCalibResidual,
                toolLength = Kinematics.ToolLength,
            },
            collision = CollisionAlarm,
            collisionMsg = CollisionMessage ?? "",
            homing = _homing,
            io,
        };
    }
}

/// <summary>3D 插补引擎接口 (预留扩展: 未来可接入 S 形加减速/NURBS)</summary>
public interface I3DInterpolationEngine
{
    // 当前实现直接用 I3DPath, 此接口保留给未来复杂插补算法
    List<I3DPath> Convert(List<DispensePath3D> paths);
}

public sealed class DefaultInterpolationEngine : I3DInterpolationEngine
{
    public List<I3DPath> Convert(List<DispensePath3D> paths)
    {
        var all = new List<I3DPath>();
        foreach (var p in paths) all.AddRange(p.Segments);
        return all;
    }
}
