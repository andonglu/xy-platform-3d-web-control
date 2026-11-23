using DispenseHost.CadImport;
using DispenseHost.Net;
using DispenseHost.PathPlan;
using DispenseServer.Core;
using DispenseServer.Collision;
using DispenseServer.Net;
using System.Net;
using HostPoint = DispenseHost.CadImport.Point3D;
using SrvPoint = DispenseServer.Core.Point3D;

// ---------------------------------------------------------------------------
// P2 验证: DXF 导入 + 多 Z 分层切片 + 程序装配 + 服务端全链路执行
//   [1] DXF 解析几何正确性 (LINE/CIRCLE/ARC/LWPOLYLINE)
//   [2] 圆锥网格分层切片 (Z 越高半径越小)
//   [3] 全链路: DXF + 分层路径 → JobAssembler → DispenseServer 控制器 → 出胶轨迹落在轮廓上
// ---------------------------------------------------------------------------

int failures = 0;
void Check(bool cond, string name, string detail = "")
{
    Console.WriteLine($"  {(cond ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? $"  ({detail})" : "")}");
    if (!cond) failures++;
}

// ---------------- Test 1: DXF 解析 ----------------
Console.WriteLine("[1] DXF 解析 (LINE / CIRCLE / ARC / LWPOLYLINE)");
const string dxf = """
0
SECTION
2
ENTITIES
0
LINE
8
0
10
0.0
20
0.0
30
5.0
11
100.0
21
0.0
31
5.0
0
CIRCLE
8
TOP
10
50.0
20
0.0
40
20.0
0
ARC
8
TOP
10
-50.0
20
0.0
40
20.0
50
0.0
51
180.0
0
LWPOLYLINE
8
0
90
4
70
1
10
0.0
20
0.0
10
10.0
20
0.0
10
10.0
20
10.0
10
0.0
20
10.0
0
ENDSEC
0
EOF
""";
{
    int skipped = -1;
    var paths = DxfImporter.Import(new StringReader(dxf), new DxfImportOptions
    {
        DefaultZ = 8,
        LayerZ = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["TOP"] = 12.0 },
        OnSkipped = n => skipped = n,
    });
    Check(paths.Count == 4, "entity count = 4", $"实际 {paths.Count}");

    var line = paths[0];
    Check(line.Layer == "0" && !line.IsClosed && line.Points.Count == 2
          && Math.Abs(line.Points[1].X - 100) < 1e-6 && Math.Abs(line.Points[0].Z - 5) < 1e-6,
        "LINE 端点与 Z", $"({line.Points[1].X:F1}, Z={line.Points[0].Z:F1})");

    var circle = paths[1];
    double rMin = circle.Points.Min(p => Math.Sqrt((p.X - 50) * (p.X - 50) + p.Y * p.Y));
    double rMax = circle.Points.Max(p => Math.Sqrt((p.X - 50) * (p.X - 50) + p.Y * p.Y));
    Check(circle.IsClosed && Math.Abs(rMin - 20) < 0.2 && Math.Abs(rMax - 20) < 0.2
          && Math.Abs(circle.Points[0].Z - 12) < 1e-6,
        "CIRCLE r=20 @图层TOP Z=12", $"r∈[{rMin:F2},{rMax:F2}], Z={circle.Points[0].Z:F1}, 点={circle.Points.Count}");

    var arc = paths[2];
    double rMinA = arc.Points.Min(p => Math.Sqrt((p.X + 50) * (p.X + 50) + p.Y * p.Y));
    double rMaxA = arc.Points.Max(p => Math.Sqrt((p.X + 50) * (p.X + 50) + p.Y * p.Y));
    var midA = arc.Points[arc.Points.Count / 2];
    Check(!arc.IsClosed && Math.Abs(rMinA - 20) < 0.2 && Math.Abs(rMaxA - 20) < 0.2
          && Math.Abs(midA.Y - 20) < 0.5 && Math.Abs(midA.X + 50) < 0.5,
        "ARC 0→180° 上半圆", $"中点({midA.X:F1},{midA.Y:F1})");

    var pl = paths[3];
    Check(pl.IsClosed && pl.Points.Count == 5
          && pl.Points[0] == pl.Points[^1]
          && Math.Abs(pl.Points[2].Y - 10) < 1e-6 && Math.Abs(pl.Points[0].Z - 8) < 1e-6,
        "LWPOLYLINE 闭合方形 0→10", $"点={pl.Points.Count}");
    Check(skipped == 0, "无跳过实体");
}

// ---------------- Test 2: 多 Z 分层切片 ----------------
Console.WriteLine("[2] 圆锥网格多 Z 分层切片 (Z↑ → 半径↓)");
static Mesh MakeCone(double r, double h, int n = 32)
{
    var mesh = new Mesh();
    var apex = new HostPoint(0, 0, h);
    var rim = Enumerable.Range(0, n).Select(i =>
    {
        double a = 2 * Math.PI * i / n;
        return new HostPoint(r * Math.Cos(a), r * Math.Sin(a), 0);
    }).ToArray();
    var down = new HostPoint(0, 0, -1);
    for (int i = 0; i < n; i++)
    {
        var a = rim[i];
        var b = rim[(i + 1) % n];
        mesh.Triangles.Add(new Triangle(down, new HostPoint(0, 0, 0), b, a));
        var normal = HostPoint.Cross(b - apex, a - apex).Normalize();
        mesh.Triangles.Add(new Triangle(normal, apex, a, b));
    }
    return mesh;
}

List<(double Z, List<List<HostPoint>> Chains)> layers;
{
    var cone = MakeCone(r: 40, h: 40);
    var gen = new PathGenerator();
    layers = gen.GenerateLayeredSlice(cone, zStart: 10, zEnd: 30, dz: 10);
    Check(layers.Count == 3, "层数 = 3", $"实际 {layers.Count}");
    double[] expectR = [30, 20, 10];
    for (int i = 0; i < layers.Count; i++)
    {
        var (z, chains) = layers[i];
        double maxR = chains.SelectMany(c => c).Max(p => Math.Sqrt(p.X * p.X + p.Y * p.Y));
        bool zOk = chains.SelectMany(c => c).All(p => Math.Abs(p.Z - z) < 1e-6);
        Check(chains.Count >= 1 && zOk && Math.Abs(maxR - expectR[i]) < 0.5,
            $"层 Z={z:F0}: 单闭合轮廓 r≈{expectR[i]}", $"{chains.Count} 条, maxR={maxR:F2}, 点数 {chains.Sum(c => c.Count)}");
    }
}

// ---------------- Test 3: 全链路 (装配 → 服务端执行) ----------------
Console.WriteLine("[3] 全链路: DXF + 分层切片 → JobAssembler → DispenseServer 执行");
{
    var card = new SimulatedDispenseCard();
    var ctl = new DispenseController(card, new CollisionChecker(card), new DefaultInterpolationEngine());

    // 装配: DXF 圆 (图层 TOP → Z=12) + 圆锥三层切片, 统一附 P1 时序 (提前关胶 200ms)
    var dxfEntities = DxfImporter.Import(new StringReader(dxf), new DxfImportOptions
    {
        LayerZ = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["TOP"] = 12.0 },
    });
    var circleOnly = dxfEntities
        .Where(e => e.IsClosed && Math.Abs(e.Points[0].Z - 12) < 1e-6).ToList();
    var steps = new List<DispenseClient.UploadStep>();
    steps.AddRange(JobAssembler.Assemble(circleOnly, feed: 100));
    steps.AddRange(JobAssembler.Assemble(layers, feed: 80,
        timing: new DispenseTiming(EarlyCloseMs: 200, CloseDelayMs: 100, RetractMm: 3)));

    // 上传并执行 (直接调用控制器, 不经过 WebSocket)
    var dtoSteps = steps.Select(s => new JobStepDto(
        Kind: s.Kind,
        Target: null,
        Points: s.Points.Select(p => new[] { p.X, p.Y, p.Z }).ToArray(),
        ArcVia: null,
        Feed: s.Feed,
        ValveOn: s.ValveOn,
        DwellMs: 0,
        OpenDelayMs: s.OpenDelayMs,
        EarlyCloseMs: s.EarlyCloseMs,
        CloseDelayMs: s.CloseDelayMs,
        RetractMm: s.RetractMm)).ToList();

    bool done = false;
    ctl.OnEvent += (_, msg) => { if (msg == "点胶作业完成") done = true; };
    var paths = JobBuilder.BuildPaths(dtoSteps);
    Check(paths.Count == steps.Count, "JobBuilder 段数", $"{paths.Count} 段");
    ctl.UploadPaths(paths, PathSampler3D.Sample(paths.SelectMany(p => p.Segments)), "p2-verify");
    ctl.RunJob();

    var log = new List<(double X, double Y, double Z, bool Valve)>();
    const double dtick = 1.0 / 120;
    for (int i = 0; i < (int)(60 / dtick) && !done; i++)
    {
        ctl.Tick(dtick);
        log.Add((card.Read(Axes.X).Pos, card.Read(Axes.Y).Pos, card.Read(Axes.Z).Pos, ctl.ValveOn));
    }
    Check(done, "job.completed (DXF 圆 + 两层切片)");

    // 开胶采样必须落在 DXF 圆 (Z=12) 或某一层 Z 平面 (10/20/30) 上
    var zPlanes = new[] { 12.0, 10.0, 20.0, 30.0 };
    int bad = 0, n = 0;
    foreach (var s in log)
    {
        if (!s.Valve) continue;
        n++;
        if (zPlanes.All(z => Math.Abs(s.Z - z) > 0.5)) bad++;
    }
    Check(n > 100, "valve-on samples collected", $"n={n}");
    Check(bad == 0, "all valve-on samples on expected Z planes", $"bad={bad}");

    // 提前关胶在末段生效 (末段 feed=80, a·t=120 > feed → 胶尾=制动距离 5.33mm 处关阀)
    Check(ctl.LastValveOffRemaining > 0.5, "earlyClose active on final segment",
        $"remaining={ctl.LastValveOffRemaining:F2}mm");

    // 退枪: 最后一段 retract 3mm → 抬枪最高 Z = 30+3 = 33 (统计窗口: 开阀后)
    int onIdx = log.FindIndex(t => t.Valve);
    double zMax = log.Skip(onIdx).Max(t => t.Z);
    Check(Math.Abs(zMax - 33) < 0.1, "retract → Zmax = 33", $"实际 {zMax:F2}");
}

// ---------------- Test 4: P5 五轴 RTCP 全 WebSocket 链路 ----------------
Console.WriteLine("[4] P5 五轴: WS 客户端 → RTCP 开关/标定 → 带法向曲面执行 → TCP 精度");
{
    var card5 = new SimulatedDispenseCard();
    var collision5 = new CollisionChecker(card5);
    var ctl5 = new DispenseController(card5, collision5, new DefaultInterpolationEngine());
    using var ws = new DispenseWsServer(ctl5, collision5, prefix: "http://127.0.0.1:8531/");
    bool wsStarted = false;
    try { ws.Start(); wsStarted = true; }
    catch (HttpListenerException ex)
    {
        Console.WriteLine($"  SKIP  当前环境 HTTP.sys 不可用 ({ex.Message})，五轴 WS 链路验证需在本机非沙盒环境运行");
    }
    if (wsStarted)
    {
    using var drive = new CancellationTokenSource();
    _ = Task.Run(() =>
    {
        while (!drive.IsCancellationRequested)
        {
            ctl5.Tick(1.0 / 120);   // 碰撞检测由控制器 Tick 内部驱动
            Thread.Sleep(8);
        }
    });

    static async Task WaitUntil(Func<bool> cond, int timeoutMs = 3000)
    {
        int t0 = Environment.TickCount;
        while (!cond() && Environment.TickCount - t0 < timeoutMs) await Task.Delay(20);
    }
    static double DistToPolyline(SrvPoint p, List<HostPoint> pts)
    {
        double best = double.MaxValue;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var a = new SrvPoint(pts[i].X, pts[i].Y, pts[i].Z);
            var b = new SrvPoint(pts[i + 1].X, pts[i + 1].Y, pts[i + 1].Z);
            var ab = b - a;
            double t = Math.Clamp(SrvPoint.Dot(p - a, ab) / Math.Max(SrvPoint.Dot(ab, ab), 1e-9), 0, 1);
            best = Math.Min(best, (p - (a + ab * t)).Length);
        }
        return best;
    }

    using var client = new DispenseClient("ws://127.0.0.1:8531/ws");
    try
    {
    var doneTcs = new TaskCompletionSource();
    var calibTcs = new TaskCompletionSource();
    client.OnEventData += (_, msg) =>
    {
        if (msg == "点胶作业完成") doneTcs.TrySetResult();
        if (msg.StartsWith("RTCP 标定完成")) calibTcs.TrySetResult();
    };
    var samples = new List<(double X, double Y, double Z, double A, double C)>();
    client.OnState += st =>
    {
        if (st.Valve && st.A is not null && st.C is not null)
            lock (samples) samples.Add((st.X.Pos, st.Y.Pos, st.Z.Pos, st.A.Pos, st.C.Pos));
    };

    await client.ConnectAsync();
    Check(client.IsConnected, "WS 连接服务端 (8531)");

    await client.RtcpAsync(true);
    await WaitUntil(() => client.LastState.Rtcp?.Enabled == true);
    Check(client.LastState.Rtcp?.Enabled == true, "rtcp on → 快照 enabled=true");

    await client.RtcpCalibrateAsync();
    await Task.WhenAny(calibTcs.Task, Task.Delay(8000));
    Check(calibTcs.Task.IsCompleted, "rtcp_calibrate → 标定完成事件");
    await WaitUntil(() => client.LastState.Rtcp?.Calibrated == true);
    var rt = client.LastState.Rtcp;
    Check(rt?.Calibrated == true && rt.ResidualMm < 0.05, "快照 calibrated + 残差 < 0.05mm",
        $"residual={rt?.ResidualMm:E2}mm, toolLength={rt?.ToolLength:F0}mm");

    // 圆柱面: X 向进给, Y/Z 沿 R=60 圆弧, 法向随弧变化 (与服务层 [6] 同场景)
    const double Rc = 60;
    var pts5 = new List<HostPoint>();
    var nrm5 = new List<HostPoint>();
    for (int i = 0; i <= 4; i++)
    {
        double th = (60.0 / 4 * i) * Math.PI / 180;
        pts5.Add(new HostPoint(-40 + 20.0 * i, Rc * Math.Sin(th), 8 + Rc * (1 - Math.Cos(th))));
        nrm5.Add(new HostPoint(0, Math.Sin(th), -Math.Cos(th)));
    }
    await client.UploadStepsAsync("p5-surface", [
        new DispenseClient.UploadStep("dispense", pts5, 80, true, Normals: nrm5)]);
    await client.RunJobAsync();
    await Task.WhenAny(doneTcs.Task, Task.Delay(30000));
    Check(doneTcs.Task.IsCompleted, "带法向曲面作业经 WS 完成");

    int nSamples;
    lock (samples) nSamples = samples.Count;
    Check(nSamples > 20, "五轴状态样本 (A/C 角经快照回传)", $"n={nSamples}");

    double aMin = 0, aMax = 0;
    lock (samples)
    {
        if (nSamples > 0)
        {
            aMin = samples.Min(s => s.A);
            aMax = samples.Max(s => s.A);
        }
    }
    Check(aMax - aMin > 30, "A 轴随曲面法向摆动 (>30°)", $"A∈[{aMin:F1}°, {aMax:F1}°]");

    // 用服务端真实机床几何核算 TCP 偏离 (标定后应≈0)
    double maxDev = 0;
    lock (samples)
        foreach (var s in samples)
            maxDev = Math.Max(maxDev, DistToPolyline(
                ctl5.RealGeometry.Forward(new SrvPoint(s.X, s.Y, s.Z), s.A, s.C), pts5));
    Check(maxDev < 0.05, "标定后真实 TCP 偏离 < 0.05mm (全 WS 链路)", $"max={maxDev:F4}mm");
    }
    catch (Exception ex) { Check(false, "P5 链路异常", ex.Message); }
    drive.Cancel();
    }
}

// ---------------- Test 5: P6 STL 曲面 → 带法向路径 → 五轴执行 ----------------
Console.WriteLine("[5] P6 STL 曲面: 等高层路径 + 法向偏置 + RTCP 真实 TCP 贴合");
{
    const double radius = 30;
    static Mesh MakeCylinderPatch(double r, double h, int n = 12)
    {
        var mesh = new Mesh();
        for (int i = 0; i < n; i++)
        {
            double a0 = (-60 + 120.0 * i / n) * Math.PI / 180;
            double a1 = (-60 + 120.0 * (i + 1) / n) * Math.PI / 180;
            var b0 = new HostPoint(r * Math.Cos(a0), r * Math.Sin(a0), 0);
            var b1 = new HostPoint(r * Math.Cos(a1), r * Math.Sin(a1), 0);
            var t0 = new HostPoint(b0.X, b0.Y, h);
            var t1 = new HostPoint(b1.X, b1.Y, h);
            foreach (var tri in new[] { new Triangle(default, b0, b1, t1), new Triangle(default, b0, t1, t0) })
            {
                var normal = HostPoint.Cross(tri.V1 - tri.V0, tri.V2 - tri.V0).Normalize();
                mesh.Triangles.Add(tri with { Normal = normal });
            }
        }
        return mesh;
    }

    static double DistToPolyline6(SrvPoint p, List<HostPoint> pts)
    {
        double best = double.MaxValue;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var a = new SrvPoint(pts[i].X, pts[i].Y, pts[i].Z);
            var b = new SrvPoint(pts[i + 1].X, pts[i + 1].Y, pts[i + 1].Z);
            var ab = b - a;
            double t = Math.Clamp(SrvPoint.Dot(p - a, ab) / Math.Max(SrvPoint.Dot(ab, ab), 1e-9), 0, 1);
            best = Math.Min(best, (p - (a + ab * t)).Length);
        }
        return best;
    }

    var gen6 = new PathGenerator();
    var cylinder = MakeCylinderPatch(radius, h: 40);
    var surface = gen6.GenerateSurfacePaths(cylinder, zStart: 20, zEnd: 20, dz: 10);
    Check(surface.Count == 1 && surface[0].Points.Count >= 10,
        "圆柱曲面切片生成单条加工路径", $"路径={surface.Count}, 点={surface.Sum(p => p.Points.Count)}");

    var surfacePath = surface[0];
    bool paired = surfacePath.Normals.Count == surfacePath.Points.Count;
    double minDot = 1;
    for (int i = 0; i < surfacePath.Points.Count; i++)
    {
        var p = surfacePath.Points[i];
        var expected = new HostPoint(p.X / radius, p.Y / radius, 0).Normalize();
        minDot = Math.Min(minDot, HostPoint.Dot(expected, surfacePath.Normals[i]));
    }
    Check(paired && minDot > 0.99, "逐点法向与圆柱解析法向对齐 (点积 > 0.99)", $"minDot={minDot:F5}");

    var offset = gen6.GenerateSurfacePaths(cylinder, 20, 20, 10, normalOffset: 2);
    bool offsetOk = offset.Count == 1;
    if (offsetOk)
    {
        for (int i = 0; i < surfacePath.Points.Count; i++)
        {
            var expected = surfacePath.Points[i] + surfacePath.Normals[i] * 2;
            if ((offset[0].Points[i] - expected).Length > 1e-8 ||
                (offset[0].Normals[i] - surfacePath.Normals[i]).Length > 1e-8)
                offsetOk = false;
        }
    }
    Check(offsetOk, "normalOffset=2mm → 等距偏置且法向不变");

    var steps6 = JobAssembler.Assemble(surface, feed: 80);
    Check(steps6.Count == 1 && steps6[0].Normals?.Count == steps6[0].Points.Count,
        "JobAssembler 保留逐点法向");

    var dtoSteps6 = steps6.Select(s => new JobStepDto(
        Kind: s.Kind, Target: null,
        Points: s.Points.Select(p => new[] { p.X, p.Y, p.Z }).ToArray(),
        ArcVia: null, Feed: s.Feed, ValveOn: s.ValveOn, DwellMs: 0,
        Normals: s.Normals?.Select(n => new[] { n.X, n.Y, n.Z }).ToArray())).ToList();
    var paths6 = JobBuilder.BuildPaths(dtoSteps6);
    Check(paths6.Count == 1 && paths6[0].SegmentNormals?.Count == surfacePath.Points.Count - 1,
        "服务端 JobBuilder 收到全部子段法向");

    var card6 = new SimulatedDispenseCard();
    var ctl6 = new DispenseController(card6, new CollisionChecker(card6), new DefaultInterpolationEngine());
    ctl6.SetRtcp(true);
    ctl6.CalibrateRtcp();
    Check(ctl6.Kinematics.Calibrated && ctl6.LastCalibResidual < 0.001,
        "P6 执行前 RTCP 标定", $"residual={ctl6.LastCalibResidual:E2}mm");

    bool done6 = false;
    ctl6.OnEvent += (_, msg) => { if (msg == "点胶作业完成") done6 = true; };
    ctl6.UploadPaths(paths6, PathSampler3D.Sample(paths6.SelectMany(p => p.Segments)), "p6-stl-surface");
    ctl6.RunJob();
    var samples6 = new List<(double X, double Y, double Z, double A, double C)>();
    const double dt6 = 1.0 / 120;
    for (int i = 0; i < (int)(30 / dt6) && !done6 && !ctl6.CollisionAlarm; i++)
    {
        ctl6.Tick(dt6);
        if (ctl6.ValveOn)
            samples6.Add((card6.Read(Axes.X).Pos, card6.Read(Axes.Y).Pos, card6.Read(Axes.Z).Pos,
                card6.Read(Axes.A).Pos, card6.Read(Axes.C).Pos));
    }
    Check(done6 && !ctl6.CollisionAlarm, "STL 曲面法向作业内存链路完成");
    Check(samples6.Count > 20, "采集五轴开胶样本", $"n={samples6.Count}");

    double maxDev6 = 0;
    foreach (var s in samples6)
        maxDev6 = Math.Max(maxDev6, DistToPolyline6(
            ctl6.RealGeometry.Forward(new SrvPoint(s.X, s.Y, s.Z), s.A, s.C), surfacePath.Points));
    Check(maxDev6 < 0.05, "标定后真实 TCP 贴合 STL 加工路径 (<0.05mm)", $"max={maxDev6:F4}mm");

    if (samples6.Count > 0)
    {
        double cRange = samples6.Max(s => s.C) - samples6.Min(s => s.C);
        double maxAError = samples6.Max(s => Math.Abs(s.A - 90));
        Check(cRange > 100 && maxAError < 0.01,
            "C 轴随圆柱法向回转, A 轴保持 90°", $"C范围={cRange:F1}°, A误差={maxAError:E2}°");
    }
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL VERIFICATION PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
