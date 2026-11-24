using DispenseServer.Core;
using DispenseServer.Collision;

// ---------------------------------------------------------------------------
// P1 验证: 空间圆弧插补 + 指令级出胶时序 (开阀延时/提前关胶/关胶保持/退枪)
//   直接驱动 DispenseController (120Hz 固定步长), 从轴卡回读位置/阀状态验证工艺行为。
//   预期提前关胶: stopDist = v·t - ½·a·t², feed=100, t=0.2s, a=600 → 剩余 8mm 处关阀。
// ---------------------------------------------------------------------------

int failures = 0;
void Check(bool cond, string name, string detail = "")
{
    Console.WriteLine($"  {(cond ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? $"  ({detail})" : "")}");
    if (!cond) failures++;
}

(IDispenseAxisCard Card, DispenseController Ctl) NewHost()
{
    var card = new SimulatedDispenseCard();
    var collision = new CollisionChecker(card);
    return (card, new DispenseController(card, collision, new DefaultInterpolationEngine()));
}

List<Tick> Run(DispenseController ctl, IDispenseAxisCard card, List<JobStepDto> steps, out bool completed, double maxSec = 30)
{
    bool done = false;
    ctl.OnEvent += (_, msg) => { if (msg == "点胶作业完成") done = true; };
    var paths = JobBuilder.BuildPaths(steps);
    ctl.UploadPaths(paths, PathSampler3D.Sample(paths.SelectMany(p => p.Segments)), "verify");
    ctl.RunJob();
    var log = new List<Tick>();
    const double dt = 1.0 / 120;
    for (int i = 0; i < (int)(maxSec / dt) && !done; i++)
    {
        ctl.Tick(dt);
        log.Add(new Tick(i * dt, ctl.ValveOn, card.Read(Axes.X).Pos, card.Read(Axes.Y).Pos, card.Read(Axes.Z).Pos));
    }
    completed = done;
    return log;
}

double[] P(double x, double y, double z) => [x, y, z];

ProductionEnvelope MakeProductionEnvelope(int batch = 1, bool autoPart = true,
    bool autoClamp = true, int actionDelayMs = 20)
{
    var config = new ProductionConfig("p9-verify", "P9 验证生产",
        new FixtureConfig(AutoPartConfirm: autoPart, AutoClampConfirm: autoClamp),
        new PlacementConfig(new Pose3D(10, 20, 30)),
        new LoadingRecipe(new Pose3D(-80, -40, 20), new Pose3D(80, 40, 20),
            ApproachMm: 15, LiftMm: 20, ActionDelayMs: actionDelayMs, BatchCount: batch));
    return new ProductionEnvelope("p9-verify", 7, "verify-model", config,
    [
        new JobStepDto("dispense", null, [P(0, 0, 8), P(8, 0, 8)], null, 100, true, 0),
    ]);
}

void SaveVerifiedModel(ProductionStore store, byte[] bytes, string modelId = "verify-model")
{
    string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    int count = (bytes.Length + ProductionStore.ChunkSize - 1) / ProductionStore.ChunkSize;
    store.BeginModel(new ModelBeginDto(modelId, "verify.stl", "mm", hash, bytes.LongLength, count));
    for (int i = 0; i < count; i++)
    {
        int offset = i * ProductionStore.ChunkSize;
        int length = Math.Min(ProductionStore.ChunkSize, bytes.Length - offset);
        store.AddModelChunk(new ModelChunkDto(modelId, i, Convert.ToBase64String(bytes, offset, length)));
    }
    store.CommitModel(modelId);
}

bool RunProductionUntil(DispenseController ctl, VirtualProductionController production,
    Func<bool> condition, double maxSec = 30)
{
    const double dt = 1.0 / 120;
    for (int i = 0; i < (int)(maxSec / dt); i++)
    {
        ctl.Tick(dt);
        production.Tick(dt);
        if (condition()) return true;
    }
    return condition();
}

void RunProductionSeconds(DispenseController ctl, VirtualProductionController production, double seconds)
{
    const double dt = 1.0 / 120;
    for (int i = 0; i < (int)(seconds / dt); i++)
    {
        ctl.Tick(dt);
        production.Tick(dt);
    }
}

// ---------------- Test 1: 空间圆弧几何 ----------------
Console.WriteLine("[1] 空间圆弧几何 (三点定圆, 半径 50, 半圆)");
{
    var arc = new Arc3DPath(new Point3D(50, 0, 8), new Point3D(0, 50, 8), new Point3D(-50, 0, 8));
    Check(Math.Abs(arc.Length - Math.PI * 50) < 0.01, "arc.length = r·π", $"{arc.Length:F3}");
    var s = arc.PointAt(0);
    Check(Math.Abs(s.X - 50) < 0.01 && Math.Abs(s.Y) < 0.01, "arc.startpoint");
    var m = arc.PointAt(arc.Length / 2);
    Check(Math.Abs(m.X) < 0.01 && Math.Abs(m.Y - 50) < 0.01 && Math.Abs(m.Z - 8) < 0.01, "arc.midpoint", $"({m.X:F2},{m.Y:F2},{m.Z:F2})");
    var e = arc.PointAt(arc.Length);
    Check(Math.Abs(e.X + 50) < 0.01 && Math.Abs(e.Y) < 0.01, "arc.endpoint", $"({e.X:F2},{e.Y:F2})");
}

// ---------------- Test 2: 出胶时序全参数 ----------------
Console.WriteLine("[2] 指令级出胶时序 (openDelay 100ms / earlyClose 200ms / closeDelay 150ms / retract 5mm)");
{
    var (card, ctl) = NewHost();
    var steps = new List<JobStepDto>
    {
        new("dispense", null, [P(0, 0, 8), P(100, 0, 8)], null, 100, true, 0,
            OpenDelayMs: 100, EarlyCloseMs: 200, CloseDelayMs: 150, RetractMm: 5),
    };
    var log = Run(ctl, card, steps, out bool completed);
    Check(completed, "job.completed");
    const double dt = 1.0 / 120;

    // 开阀延时: Z 到达工作高度(8mm) → 阀开, 应 ≈ 0.1s
    double tWork = -1;
    for (int i = 0; i < log.Count; i++) if (log[i].Z <= 8.05) { tWork = log[i].T; break; }
    double tOn = -1;
    for (int i = 0; i < log.Count; i++) if (log[i].Valve) { tOn = log[i].T; break; }
    Check(tWork >= 0 && tOn >= 0 && Math.Abs((tOn - tWork) - 0.1) < dt * 3, "openDelay ≈ 100ms", $"实际 {(tOn - tWork) * 1000:F1}ms");

    // 提前关胶: 剩余弧长 ≈ v·t - ½·a·t² = 100·0.2 - 300·0.04 = 8mm (巡航段触发)
    Check(ctl.LastValveOffRemaining > 0.1, "earlyClose fired before path end", $"remaining={ctl.LastValveOffRemaining:F2}mm v={ctl.LastValveOffVelocity:F1}");
    Check(Math.Abs(ctl.LastValveOffRemaining - 8) < 1.0, "earlyClose remaining ≈ 8mm", $"实际 {ctl.LastValveOffRemaining:F2}mm");
    Check(Math.Abs(ctl.LastValveOffVelocity - 100) < 1.5, "earlyClose velocity ≈ feed", $"实际 {ctl.LastValveOffVelocity:F1}mm/s");
    double xOff = -1;
    for (int i = 0; i < log.Count; i++) if (!log[i].Valve && log[i].T > tOn) { xOff = log[i].X; break; }
    Check(xOff > 80 && xOff < 99.9, "valve off at X≈92 (not at end)", $"X={xOff:F2}");

    // 关胶保持: 到点(X=100) → Z 开始抬升, 应 ≈ 0.15s
    double tEnd = -1;
    for (int i = 0; i < log.Count; i++) if (log[i].X >= 99.99) { tEnd = log[i].T; break; }
    double tAsc = -1;
    for (int i = 0; i < log.Count; i++) if (log[i].T > tEnd && log[i].Z > 8.1) { tAsc = log[i].T; break; }
    Check(tEnd >= 0 && tAsc >= 0 && Math.Abs((tAsc - tEnd) - 0.15) < dt * 3, "closeDelay ≈ 150ms", $"实际 {(tAsc - tEnd) * 1000:F1}ms");

    // 退枪: 抬枪段最高 Z = 安全高度 30 + 5 = 35 (统计窗口: 开阀之后)
    double zMax = log.Where(t => t.T >= tOn).Max(t => t.Z);
    Check(Math.Abs(zMax - 35) < 0.1, "retract → Zmax = 35", $"实际 {zMax:F2}");
}

// ---------------- Test 3: 向后兼容 (无新参数 = 原行为) ----------------
Console.WriteLine("[3] 默认参数向后兼容");
{
    var (card, ctl) = NewHost();
    var steps = new List<JobStepDto>
    {
        new("dispense", null, [P(0, 0, 8), P(100, 0, 8)], null, 100, true, 0),
    };
    var log = Run(ctl, card, steps, out bool completed);
    Check(completed, "job.completed");
    Check(ctl.LastValveOffRemaining == 0, "valve off exactly at end (remaining=0)");
    double tOn3 = -1;
    for (int i = 0; i < log.Count; i++) if (log[i].Valve) { tOn3 = log[i].T; break; }
    double xOff = -1;
    for (int i = 0; i < log.Count; i++) if (log[i].T > tOn3 && !log[i].Valve) { xOff = log[i].X; break; }
    Check(xOff >= 99.99, "valve off at X=100", $"X={xOff:F2}");
    double zMax = log.Where(t => t.T >= tOn3).Max(t => t.Z);
    Check(Math.Abs(zMax - 30) < 0.1, "ascend to PenUpZ=30 (no retract)", $"实际 {zMax:F2}");
}

// ---------------- Test 4: 圆弧段整机执行 ----------------
Console.WriteLine("[4] 圆弧段经控制器执行 (三点圆弧, Z=8 平面, 半径 50)");
{
    var (card, ctl) = NewHost();
    var steps = new List<JobStepDto>
    {
        new("dispense", null, [P(50, 0, 8), P(-50, 0, 8)], P(0, 50, 8), 100, true, 0),
    };
    var log = Run(ctl, card, steps, out bool completed);
    Check(completed, "job.completed");
    int bad = 0, n = 0;
    foreach (var t in log)
    {
        if (!t.Valve) continue;   // 仅统计开胶段 (空间圆弧轨迹)
        n++;
        double r = Math.Sqrt(t.X * t.X + t.Y * t.Y);
        if (Math.Abs(r - 50) > 0.5 || Math.Abs(t.Z - 8) > 0.3) bad++;
    }
    Check(n > 50, "samples on arc", $"n={n}");
    Check(bad == 0, "all samples on circle r=50, Z=8", $"bad={bad}");
}

// ---------------- Test 5: 脱机程序存储 + IO 启动 (P3) ----------------
Console.WriteLine("[5] 脱机程序存储 + 虚拟 IO 启动");
{
    string dir = Path.Combine(Path.GetTempPath(), $"dispense-p3-{Guid.NewGuid():N}");
    var store = new ProgramStore(dir);

    var steps1 = new List<JobStepDto>
    {
        new("dispense", null, [P(0, 0, 8), P(80, 0, 8)], null, 100, true, 0, EarlyCloseMs: 200),
    };
    var steps2 = new List<JobStepDto>
    {
        new("dispense", null, [P(0, 20, 8), P(0, 50, 8), P(30, 50, 8)], null, 60, true, 0),
    };
    var p1 = store.Save("程序一", steps1);
    var p2 = store.Save("程序二", steps2);
    Check(p1.Id == 1 && p2.Id == 2, "程序编号递增", $"#{p1.Id}, #{p2.Id}");
    Check(store.List().Count == 2 && store.Get(1)?.Name == "程序一", "list / get by id");
    Check(File.Exists(Path.Combine(dir, "0001.json")), "原子落盘 0001.json");

    bool threw = false;
    try { store.Save("空程序", []); } catch (InvalidOperationException) { threw = true; }
    Check(threw, "空程序保存被拒绝");

    // 磁盘持久化: 新实例加载同一目录
    var store2 = new ProgramStore(dir);
    Check(store2.Get(2)?.Steps.Count == 1 && store2.Get(2)?.Name == "程序二", "重启后从磁盘恢复程序");
    Check(store2.Delete(2) && store2.Get(2) is null, "delete 生效");

    // IO 全闭环: BCD 选程序 → start 上升沿 → 运行 → done 脉冲; pause/reset
    var card = new SimulatedDispenseCard();
    var ctl = new DispenseController(card, new CollisionChecker(card), new DefaultInterpolationEngine());
    var io = new IoController(store2, ctl);
    ctl.AttachIo(io);
    var events = new List<string>();
    ctl.OnEvent += (level, msg) =>
    {
        events.Add($"[{level}] {msg}");
        if (msg == "点胶作业完成") io.NotifyJobDone();
    };

    const double dt5 = 1.0 / 120;
    void RunSec(double sec) { for (int i = 0; i < (int)(sec / dt5); i++) ctl.Tick(dt5); }

    // BCD=9 (不存在) → 报错不运行
    io.SetInput("sel3", true);                       // BCD = 8
    io.SetInput("sel0", true);                       // BCD = 9
    io.SetInput("start", true); RunSec(0.05);
    io.SetInput("start", false); RunSec(0.05);
    Check(!ctl.IsRunning && events.Any(e => e.Contains("程序 9 不存在")),
        "BCD=9 不存在 → 报错不运行");

    // BCD=1 → start → 运行程序一 (含提前关胶时序, 经存储往返保留)
    io.SetInput("sel3", false); io.SetInput("sel0", true);   // BCD = 1
    io.SetInput("start", true); RunSec(0.05); io.SetInput("start", false); RunSec(0.02);
    Check(ctl.IsRunning && ctl.CurrentJobName == "程序一", "IO 启动程序一", ctl.CurrentJobName ?? "");
    Check(io.State().Running, "输出 running=true");

    // 运行中重复 start 被拒绝 (上升沿互锁; 脉冲间隙需 RunSec 让边沿检测吃到下降沿)
    events.Clear();
    io.SetInput("start", true); RunSec(0.05); io.SetInput("start", false); RunSec(0.02);
    Check(events.Any(e => e.Contains("拒绝")), "运行中重复 start 被拒绝");

    bool done = false;
    for (int i = 0; i < (int)(20 / dt5) && !done; i++)
    {
        ctl.Tick(dt5);
        if (io.DonePulseRemain > 0) done = true;
    }
    Check(done, "作业完成 → done 1s 脉冲");
    double pulse = io.DonePulseRemain;
    Check(pulse > 0.5 && pulse <= 1.0, "脉冲宽度 ≈1s", $"剩余 {pulse:F2}s");
    RunSec(1.2);
    Check(io.DonePulseRemain == 0, "脉冲 1s 后熄灭");
    // 存储往返后提前关胶时序保留 (feed=100 → 关阀点应早于终点)
    Check(ctl.LastValveOffRemaining > 0.5, "stored earlyClose preserved", $"remaining={ctl.LastValveOffRemaining:F2}mm");

    // pause → 急停; reset → 复位
    io.SetInput("start", true); RunSec(0.05); io.SetInput("start", false); RunSec(0.02);
    Check(ctl.IsRunning, "再次启动");
    io.SetInput("pause", true); RunSec(0.05); io.SetInput("pause", false); RunSec(0.02);
    Check(card.Read(Axes.X).State == AxisState.Faulted && io.State().Error, "pause → 急停 Faulted + error 输出");
    io.SetInput("reset", true); RunSec(0.05); io.SetInput("reset", false); RunSec(0.02);
    Check(card.Read(Axes.X).State != AxisState.Faulted && !io.State().Error, "reset → 报警复位");

    try { Directory.Delete(dir, true); } catch { /* 清理失败无碍 */ }
}

// ---------------- Test 6: 五轴 RTCP + 标定 (P4) ----------------
Console.WriteLine("[6] 五轴 RTCP + 标定 (AC 双摆头, 出厂误差 A0.9°/C0.7°/枢轴偏移)");
{
    var card = new SimulatedDispenseCard();
    var ctl = new DispenseController(card, new CollisionChecker(card), new DefaultInterpolationEngine());
    var real = ctl.RealGeometry;   // 真实机床几何 (真值)

    // 运动学自洽: 逆解 → 正解 = 原 TCP
    var kin = ctl.Kinematics;
    var s0 = kin.SolveTcp(new Point3D(10, 20, 30), new Point3D(0, 0.5, -0.866));
    var f0 = kin.Forward(s0.pivot, s0.aDeg, s0.cDeg);
    Check(Math.Abs(f0.X - 10) < 1e-6 && Math.Abs(f0.Y - 20) < 1e-6 && Math.Abs(f0.Z - 30) < 1e-6,
        "RTCP 逆解→正解自洽", $"A={s0.aDeg:F1}° C={s0.cDeg:F1}°");
    Check(Math.Abs(s0.aDeg - 30) < 0.01, "A = acos(-nz) = 30°", $"{s0.aDeg:F3}°");

    // 圆柱面路径: θ 0→60° (A 轴 0→60°), 法向 (0, sinθ, -cosθ)
    // R=60: 针尖 z 最高 38mm, 枢轴 Z 最高 68mm, 距 Z 正极限传感器 (78mm) 有 9mm 余量
    const double Rc = 60;
    var pts = new List<double[]>();
    var nrm = new List<double[]>();
    for (int i = 0; i <= 4; i++)
    {
        double th = (60.0 / 4 * i) * Math.PI / 180;
        pts.Add([-40 + 20.0 * i, Rc * Math.Sin(th), 8 + Rc * (1 - Math.Cos(th))]);
        nrm.Add([0, Math.Sin(th), -Math.Cos(th)]);
    }

    double DistToPath(Point3D p)
    {
        double best = double.MaxValue;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var a2 = new Point3D(pts[i][0], pts[i][1], pts[i][2]);
            var b2 = new Point3D(pts[i + 1][0], pts[i + 1][1], pts[i + 1][2]);
            var ab = b2 - a2;
            double t = Math.Clamp(Point3D.Dot(p - a2, ab) / Math.Max(Point3D.Dot(ab, ab), 1e-9), 0, 1);
            best = Math.Min(best, (p - (a2 + ab * t)).Length);
        }
        return best;
    }

    List<(double px, double py, double pz, double a, double c)> RunSurface()
    {
        var steps = new List<JobStepDto>
        {
            new("dispense", null, pts.ToArray(), null, 80, true, 0, Normals: nrm.ToArray()),
        };
        var paths = JobBuilder.BuildPaths(steps);
        bool done = false;
        var evs = new List<string>();
        ctl.OnEvent += (l, m) =>
        {
            evs.Add($"[{l}] {m}");
            if (m == "点胶作业完成") done = true;
        };
        ctl.UploadPaths(paths, PathSampler3D.Sample(paths.SelectMany(p => p.Segments)), "曲面");
        ctl.RunJob();
        var log = new List<(double px, double py, double pz, double a, double c)>();
        for (int i = 0; i < 120 * 30 && !done; i++)
        {
            ctl.Tick(1.0 / 120);
            if (ctl.ValveOn)
                log.Add((card.Read(Axes.X).Pos, card.Read(Axes.Y).Pos, card.Read(Axes.Z).Pos,
                         card.Read(Axes.A).Pos, card.Read(Axes.C).Pos));
        }
        if (!done) Console.WriteLine("    events: " + string.Join(" | ", evs.Take(8)));
        if (log.Count > 0)
            Console.WriteLine($"    samples: X[{log.Min(s => s.px):F1},{log.Max(s => s.px):F1}] " +
                              $"Y[{log.Min(s => s.py):F1},{log.Max(s => s.py):F1}] " +
                              $"Z[{log.Min(s => s.pz):F1},{log.Max(s => s.pz):F1}] " +
                              $"A[{log.Min(s => s.a):F1},{log.Max(s => s.a):F1}] C[{log.Min(s => s.c):F1},{log.Max(s => s.c):F1}]");
        Check(done, "surface job.completed");
        return log;
    }

    // 标定前: 名义模型理想, 真实机床带误差 → TCP 系统性偏离
    ctl.SetRtcp(true);
    var log1 = RunSurface();
    Check(log1.Count > 30, "valve-on samples", $"n={log1.Count}");
    double dev1 = log1.Max(s => real.Forward(new Point3D(s.px, s.py, s.pz), s.a, s.c).Length > 0
        ? DistToPath(real.Forward(new Point3D(s.px, s.py, s.pz), s.a, s.c)) : 0);
    Check(dev1 > 0.1, "标定前 TCP 系统性偏离 > 0.1mm", $"max={dev1:F3}mm");

    // 标定: 单高度多姿态探测 + 7 参数联合高斯-牛顿拟合
    ctl.CalibrateRtcp();
    Check(ctl.LastCalibResidual < 0.05, "标定残差 < 0.05mm", $"{ctl.LastCalibResidual:F4}");
    Check(ctl.Kinematics.Calibrated, "标定状态置位");
    var axA = ctl.Kinematics.AxisA;
    Check(Math.Abs(axA.Y - real.AxisA.Y) < 1e-3 && Math.Abs(axA.Z - real.AxisA.Z) < 1e-3,
        "A 轴拟合 ≈ 真实轴线", $"εy={axA.Y:F5} εz={axA.Z:F5} (真实 {real.AxisA.Y:F5}/{real.AxisA.Z:F5})");

    // 标定后: 偏离消除
    var log2 = RunSurface();
    double dev2 = log2.Max(s => DistToPath(real.Forward(new Point3D(s.px, s.py, s.pz), s.a, s.c)));
    Check(dev2 < 0.05, "标定后 TCP 偏离 < 0.05mm", $"max={dev2:F4}mm");

    // A 轴随曲面法向 0→60° 联动
    double aMin = log2.Min(s => s.a), aMax = log2.Max(s => s.a);
    Check(aMax - aMin > 55, "A 轴随曲面摆动 0→60°", $"A∈[{aMin:F1},{aMax:F1}]");

    // RTCP 关闭 + 法向路径 → 告警并按三轴执行
    var ev2 = new List<string>();
    ctl.OnEvent += (_, m) => ev2.Add(m);
    ctl.SetRtcp(false);
    RunSurface();
    Check(ev2.Any(m => m.Contains("RTCP 未开启")), "RTCP 关闭 + 法向路径 → 告警");
    ctl.SetRtcp(true);

    // P8: A 轴行程超限从"运行中限位"提升为"上传时拒绝"
    var ev3 = new List<string>();
    ctl.OnEvent += (_, m) => ev3.Add(m);
    var steep = new List<JobStepDto>
    {
        new("dispense", null, [[0, 0, 40], [30, 0, 40]], null, 60, true, 0,
            Normals: [[0, 0.87, 0.5], [0, 0.87, 0.5]]),
    };
    var steepPaths = JobBuilder.BuildPaths(steep);
    bool accepted3 = ctl.UploadPaths(steepPaths, PathSampler3D.Sample(steepPaths.SelectMany(p => p.Segments)), "steep");
    Check(!accepted3 && !ctl.IsRunning && ev3.Any(m => m.Contains("五轴预检查失败") && m.Contains("超出行程")),
        "A 轴超限 (120°>105°) → 上传拒绝且不进入执行队列");
}

// ---------------- [7] P8 五轴路径安全 ----------------
Console.WriteLine("[7] P8 服务层: C 轴连续化 + 全程序姿态/行程预检查");
{
    var card8 = new SimulatedDispenseCard();
    var ctl8 = new DispenseController(card8, new CollisionChecker(card8), new DefaultInterpolationEngine());
    ctl8.SetRtcp(true);
    // 扩程仅用于验证跨 ±180° 连续运动; 默认 ±180° 的不可达作业由后续用例验证拒绝
    card8.ConfigureAxis(Axes.C, new AxisConfig(-540, 540, 0, -538, 538, 0, 45));
    static double[] NormalForC(double c)
    {
        double r = c * Math.PI / 180;
        return [-Math.Sin(r), Math.Cos(r), 0];
    }
    var seamSteps8 = new List<JobStepDto>
    {
        new("dispense", null, [[0, 0, 40], [15, 0, 40], [30, 0, 40]], null, 60, true, 0,
            Normals: [NormalForC(179), NormalForC(-179), NormalForC(-178)]),
    };
    var seamPaths8 = JobBuilder.BuildPaths(seamSteps8);
    var report8 = FiveAxisPathGuard.Validate(seamPaths8, ctl8.Kinematics, ctl8.GetConfigs(), 30);
    Check(report8.IsSafe && report8.MaxCStepDeg < 2.1,
        "服务端预检将 179→-179° 识别为连续 2°", $"maxC={report8.MaxCStepDeg:F2}°");

    bool done8 = false;
    ctl8.OnEvent += (_, msg) => { if (msg == "点胶作业完成") done8 = true; };
    bool accepted8 = ctl8.UploadPaths(seamPaths8, PathSampler3D.Sample(seamPaths8.SelectMany(p => p.Segments)), "p8-seam");
    ctl8.RunJob();
    var cSamples8 = new List<double>();
    for (int i = 0; i < 120 * 30 && !done8; i++)
    {
        ctl8.Tick(1.0 / 120);
        if (ctl8.ValveOn) cSamples8.Add(card8.Read(Axes.C).Pos);
    }
    double maxTickC8 = cSamples8.Zip(cSamples8.Skip(1), (a, b) => Math.Abs(b - a)).DefaultIfEmpty(999).Max();
    Check(accepted8 && done8 && cSamples8.Any(c => c > 180) && maxTickC8 < 3,
        "实际执行跨 ±180° 不反转且输出展开角", $"Cmax={cSamples8.DefaultIfEmpty().Max():F2}°, tickΔ={maxTickC8:F3}°");

    var jumpPaths8 = JobBuilder.BuildPaths(
    [
        new JobStepDto("dispense", null, [[0, 0, 40], [30, 0, 40]], null, 60, true, 0,
            Normals: [NormalForC(0), NormalForC(90)]),
    ]);
    var jumpEvents8 = new List<string>();
    ctl8.OnEvent += (_, msg) => jumpEvents8.Add(msg);
    bool acceptedJump8 = ctl8.UploadPaths(jumpPaths8, PathSampler3D.Sample(jumpPaths8.SelectMany(p => p.Segments)), "p8-jump");
    Check(!acceptedJump8 && !ctl8.IsRunning && jumpEvents8.Any(m => m.Contains("姿态突变")),
        "服务端拒绝真实大姿态跳变");

    // 上传时安全不代表配置变更后仍安全, RunJob 必须再次检查
    var constantPaths8 = JobBuilder.BuildPaths(
    [
        new JobStepDto("dispense", null, [[0, 0, 40], [30, 0, 40]], null, 60, true, 0,
            Normals: [NormalForC(179), NormalForC(179)]),
    ]);
    bool acceptedBeforeConfig8 = ctl8.UploadPaths(constantPaths8, [], "p8-revalidate");
    card8.ConfigureAxis(Axes.C, new AxisConfig(-10, 10, 0, -9, 9, 0, 45));
    ctl8.RunJob();
    Check(acceptedBeforeConfig8 && !ctl8.IsRunning && jumpEvents8.Any(m => m.Contains("C=179") && m.Contains("超出行程")),
        "上传后轴行程变更, 运行前重新拒绝");
}

// ---------------- [8] P9 模型分片、哈希与持久化 ----------------
Console.WriteLine("[8] P9 模型缺片/长度/SHA-256/重传与存储恢复");
{
    string dir = Path.Combine(Path.GetTempPath(), $"dispense-p9-store-{Guid.NewGuid():N}");
    var store = new ProductionStore(dir);
    var bytes = new byte[ProductionStore.ChunkSize + 137];
    new Random(9021).NextBytes(bytes);
    string modelId = "hash-retry-model";
    string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    var begin = new ModelBeginDto(modelId, "raw.stl", "mm", hash, bytes.LongLength, 2);
    store.BeginModel(begin);
    store.AddModelChunk(new ModelChunkDto(modelId, 0,
        Convert.ToBase64String(bytes, 0, ProductionStore.ChunkSize)));

    bool missingRejected = false;
    try { store.CommitModel(modelId); } catch (InvalidDataException ex) { missingRejected = ex.Message.Contains("缺少分片"); }
    Check(missingRejected && store.GetModel(modelId) is null, "缺片提交被拒绝且不生成有效模型");

    bool badLengthRejected = false;
    try { store.AddModelChunk(new ModelChunkDto(modelId, 1, Convert.ToBase64String(new byte[1]))); }
    catch (InvalidDataException ex) { badLengthRejected = ex.Message.Contains("长度"); }
    Check(badLengthRejected, "尾片必须严格匹配声明长度");

    var corrupted = (byte[])bytes.Clone();
    corrupted[^1] ^= 0x5a;
    store.AddModelChunk(new ModelChunkDto(modelId, 1,
        Convert.ToBase64String(corrupted, ProductionStore.ChunkSize, 137)));
    bool hashRejected = false;
    try { store.CommitModel(modelId); } catch (InvalidDataException ex) { hashRejected = ex.Message.Contains("SHA-256"); }
    Check(hashRejected && store.GetModel(modelId) is null, "完整但哈希错误的内容不能落盘为有效模型");

    // 中断/失败后从 model_begin 重传，同 ID 旧分片不能污染新会话。
    store.BeginModel(begin);
    store.AddModelChunk(new ModelChunkDto(modelId, 0,
        Convert.ToBase64String(bytes, 0, ProductionStore.ChunkSize)));
    store.AddModelChunk(new ModelChunkDto(modelId, 1,
        Convert.ToBase64String(bytes, ProductionStore.ChunkSize, 137)));
    var saved = store.CommitModel(modelId);
    Check(saved.TotalChunks == 2 && store.ReadModel(modelId).AsSpan().SequenceEqual(bytes),
        "重新 begin 后重传成功，48KiB 原始分片逐字节还原");
    Check(File.Exists(Path.Combine(dir, "models", modelId + ".stl")) &&
          File.Exists(Path.Combine(dir, "models", modelId + ".json")), "模型字节与元数据均已持久化");
    var reloaded = new ProductionStore(dir);
    Check(reloaded.GetModel(modelId)?.Sha256 == hash &&
          reloaded.ReadModel(modelId).AsSpan().SequenceEqual(bytes), "重启加载后再次校验模型内容与哈希");
    try { Directory.Delete(dir, true); } catch { }
}

// ---------------- [9] P9 两件连续生产与配方恢复 ----------------
Console.WriteLine("[9] P9 两件连续生产、每件重跑及类型化完成事件");
{
    string dir = Path.Combine(Path.GetTempPath(), $"dispense-p9-batch-{Guid.NewGuid():N}");
    var store = new ProductionStore(dir);
    SaveVerifiedModel(store, [1, 2, 3, 4, 5, 6]);
    var envelope = MakeProductionEnvelope(batch: 2);
    store.SaveRecipe(envelope);
    var restored = new ProductionStore(dir);
    Check(restored.CurrentRecipe?.Revision == 7 && restored.CurrentRecipe.Steps.Count == 1,
        "完整配方与步骤经原子文件恢复");

    var (card, ctl) = NewHost();
    var production = new VirtualProductionController(ctl, restored);
    Check(production.Snapshot.Phase == "Idle" && production.Snapshot.RunId is null,
        "服务重启只恢复工程，不自动续产");
    production.Configure(restored.CurrentRecipe!);
    var runIds = new List<string>();
    int legacyDone = 0;
    bool reachedMachinePose = false;
    ctl.OnJobCompleted += e => runIds.Add(e.RunId);
    ctl.OnEvent += (_, msg) => { if (msg == "点胶作业完成") legacyDone++; };
    production.OnSnapshot += s =>
    {
        if (s.Phase == "WaitingClamp" && Math.Abs(s.WorkpiecePose.X - 10) < 0.01 &&
            Math.Abs(s.WorkpiecePose.Y - 20) < 0.01 && Math.Abs(s.WorkpiecePose.Z - 30) < 0.01)
            reachedMachinePose = true;
    };
    Check(production.Start(), "production_start 接受已预检配方");
    bool done = RunProductionUntil(ctl, production, () => production.Snapshot.Phase == "Completed");
    RunProductionSeconds(ctl, production, 0.1); // 完成后多跑若干周期，事件不得重复
    var snapshot = production.Snapshot;
    Check(done && snapshot.CompletedCount == 2 && snapshot.BatchCount == 2 && snapshot.Progress == 1,
        "两件自动完成上料→夹紧→点胶→下料", $"completed={snapshot.CompletedCount}");
    Check(runIds.Count == 2 && runIds.Distinct().Count() == 2 && legacyDone == 2,
        "每件生成独立运行 ID，完成事件每运行恰好一次", $"events={runIds.Count}");
    Check(reachedMachinePose && !snapshot.PartPresent && !snapshot.Clamped,
        "上料动画到达机器放置位姿，完成后夹具空闲");

    // 旋转夹具: 工件先在夹具内平移 10mm, 再随夹具 Rz90°, 终点必须为 (100,10,0)
    var rotated = MakeProductionEnvelope(autoClamp: false, actionDelayMs: 1) with
    {
        Config = MakeProductionEnvelope(autoClamp: false, actionDelayMs: 1).Config with
        {
            Fixture = new FixtureConfig(MachinePose: new Pose3D(100, 0, 0, Rz: 90), AutoClampConfirm: false),
            Placement = new PlacementConfig(new Pose3D(10, 0, 0)),
        },
    };
    production.Configure(rotated);
    Check(production.Start(), "旋转夹具配方接受");
    bool reached = RunProductionUntil(ctl, production, () => production.Snapshot.Phase == "WaitingClamp");
    var p = production.Snapshot.WorkpiecePose;
    Check(reached && Math.Abs(p.X - 100) < 0.001 && Math.Abs(p.Y - 10) < 0.001 &&
        Math.Abs(p.Rz - 90) < 0.001,
        "组合位姿顺序与上位机一致", $"({p.X:F3},{p.Y:F3},{p.Z:F3}) Rz={p.Rz:F3}");
    production.Reset();
    try { Directory.Delete(dir, true); } catch { }
}

// ---------------- [10] P9 人工信号阻断 ----------------
Console.WriteLine("[10] P9 工件到位与夹紧信号阻断");
{
    string dir = Path.Combine(Path.GetTempPath(), $"dispense-p9-signal-{Guid.NewGuid():N}");
    var store = new ProductionStore(dir);
    SaveVerifiedModel(store, [9, 8, 7]);
    var (card, ctl) = NewHost();
    var production = new VirtualProductionController(ctl, store);
    production.Configure(MakeProductionEnvelope(autoPart: false, autoClamp: false));
    production.Start();
    RunProductionSeconds(ctl, production, 0.05);
    Check(production.Snapshot.Phase == "WaitingLoad" && !ctl.IsExecuting,
        "AutoPartConfirm=false 时不得跳过到位确认");
    Check(!production.Confirm("CLAMP_OK") && production.Snapshot.Phase == "WaitingLoad",
        "错误阶段/错误顺序的夹紧信号被拒绝");
    Check(production.Confirm("PART_PRESENT"), "production_confirm(PART_PRESENT) 启动上料");
    bool waitingClamp = RunProductionUntil(ctl, production, () => production.Snapshot.Phase == "WaitingClamp", 2);
    RunProductionSeconds(ctl, production, 0.1);
    Check(waitingClamp && !production.Snapshot.Clamped && !ctl.IsExecuting,
        "AutoClampConfirm=false 时停在待夹紧且不点胶");
    Check(production.Confirm("CLAMP_OK"), "production_confirm(CLAMP_OK) 满足加工前置条件");
    bool done = RunProductionUntil(ctl, production, () => production.Snapshot.Phase == "Completed");
    Check(done && production.Snapshot.CompletedCount == 1, "信号依次确认后单件完整完成");
    try { Directory.Delete(dir, true); } catch { }
}

// ---------------- [11] P9 软暂停与恢复 ----------------
Console.WriteLine("[11] P9 软暂停冻结加工时间/轴卡并关阀，可原位恢复");
{
    string dir = Path.Combine(Path.GetTempPath(), $"dispense-p9-pause-{Guid.NewGuid():N}");
    var store = new ProductionStore(dir);
    SaveVerifiedModel(store, [4, 5, 6]);
    var (card, ctl) = NewHost();
    var production = new VirtualProductionController(ctl, store);
    production.Configure(MakeProductionEnvelope());
    int completedEvents = 0;
    ctl.OnJobCompleted += _ => completedEvents++;
    production.Start();
    bool dispensing = RunProductionUntil(ctl, production,
        () => production.Snapshot.Phase == "Dispensing" && ctl.ValveOn, 10);
    Check(dispensing, "测试进入实际开阀点胶段");
    Check(production.Pause() && production.Snapshot.Paused && ctl.SoftPaused && !ctl.ValveOn,
        "production_pause 软暂停并立即关阀");
    double x = card.Read(Axes.X).Pos;
    double z = card.Read(Axes.Z).Pos;
    double jobProgress = ctl.JobProgress;
    RunProductionSeconds(ctl, production, 0.15);
    Check(card.Read(Axes.X).Pos == x && card.Read(Axes.Z).Pos == z && ctl.JobProgress == jobProgress,
        "暂停期间轴位置与加工进度完全冻结");
    Check(card.Read(Axes.X).State != AxisState.Faulted && !ctl.CollisionAlarm,
        "软暂停不复用急停且不制造 Faulted");
    Check(production.Resume() && !ctl.SoftPaused && production.Snapshot.Phase == "Dispensing",
        "production_resume 返回原点胶阶段");
    bool done = RunProductionUntil(ctl, production, () => production.Snapshot.Phase == "Completed");
    RunProductionSeconds(ctl, production, 0.05);
    Check(done && completedEvents == 1, "恢复后完成且类型化完成事件不重复");
    try { Directory.Delete(dir, true); } catch { }
}

// ---------------- [12] P9 停止、当前件重试与复位 ----------------
Console.WriteLine("[12] P9 停止保留占用、重试当前步骤、复位清空");
{
    string dir = Path.Combine(Path.GetTempPath(), $"dispense-p9-retry-{Guid.NewGuid():N}");
    var store = new ProductionStore(dir);
    SaveVerifiedModel(store, [7, 7, 7]);
    var (card, ctl) = NewHost();
    var production = new VirtualProductionController(ctl, store);
    production.Configure(MakeProductionEnvelope());
    int completions = 0;
    int failureEvents = 0;
    ctl.OnJobCompleted += _ => completions++;
    ctl.OnJobFailed += _ => failureEvents++;
    production.Start();
    RunProductionUntil(ctl, production, () => production.Snapshot.Phase == "Dispensing" && ctl.ValveOn, 10);
    Check(production.Stop() && production.Snapshot.Phase == "Blocked" && production.Snapshot.CanRetry &&
          production.Snapshot.PartPresent && production.Snapshot.Clamped && !ctl.ValveOn,
        "production_stop 关阀、进入可重试阻止并保留工件/夹紧占用");
    RunProductionSeconds(ctl, production, 0.1);
    Check(production.Snapshot.Phase == "Blocked" && completions == 0 && failureEvents == 1,
        "停止不会伪装成完成，运行失败事件仅一次");
    Check(production.Retry(), "production_retry 接受当前件点胶步骤重试");
    bool done = RunProductionUntil(ctl, production, () => production.Snapshot.Phase == "Completed");
    Check(done && completions == 1 && production.Snapshot.CompletedCount == 1,
        "重试重新 UploadPaths/RunJob 后完成当前件一次");
    production.Start();
    RunProductionUntil(ctl, production, () => production.Snapshot.PartPresent, 2);
    production.Reset();
    var reset = production.Snapshot;
    Check(reset.Phase == "Idle" && reset.RunId is null && reset.PieceIndex == 0 &&
          reset.CompletedCount == 0 && !reset.PartPresent && !reset.Clamped && !reset.Paused && !reset.CanRetry,
        "production_reset 清空工件、夹紧、运行 ID 与批次状态");

    production.Start();
    RunProductionUntil(ctl, production, () => production.Snapshot.Phase == "Dispensing" && ctl.ValveOn, 10);
    production.Pause();
    ctl.EmergencyStop(); // estop 不复用软暂停，暂停期间也必须始终可执行
    production.Tick(1.0 / 120);
    Check(production.Snapshot.Phase == "Blocked" && production.Snapshot.CanRetry &&
          !production.Snapshot.Paused && !ctl.ValveOn && ctl.AnyAxisFaulted,
        "暂停期间急停仍立即生效，并阻止到暂停前的可重试步骤");
    production.Reset();
    ctl.ResetAlarm();
    try { Directory.Delete(dir, true); } catch { }
}

Console.WriteLine("SKIP  P9 真实 WebSocket/HttpListener 链路（按原沙盒限制约定；以上均为实际内存验证，不以假 WS 代替）");
Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL VERIFICATION PASSED (P1-P9 memory; WS SKIP)" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

sealed record Tick(double T, bool Valve, double X, double Y, double Z);
