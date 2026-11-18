using DispenseServer.Core;
using DispenseServer.Collision;
using DispenseServer.Net;

// ---------------------------------------------------------------------------
// 3D 点胶服务端 (端口 8530, 与激光项目 8520 分离)
//   功能: 接收上位机上传的 3D 路径, 执行空间插补, 撞机检测报警
//   Ctrl+C 退出
// ---------------------------------------------------------------------------

IDispenseAxisCard card = new SimulatedDispenseCard();
var engine = new DefaultInterpolationEngine();
var collision = new CollisionChecker(card);
collision.SetSafetyMargin(5.0);

// 预置一个禁止区示例 (模拟夹具)
collision.AddForbiddenZone(new CollisionChecker.Aabb(-160, -155, -160, 160, 0, 80, "左墙"));
collision.AddForbiddenZone(new CollisionChecker.Aabb(155, 160, -160, 160, 0, 80, "右墙"));

var controller = new DispenseController(card, collision, engine);

// 脱机程序仓库 + 虚拟 IO (P3): 程序存 data/programs/, 经 WS 或 IO 启动执行
var store = new ProgramStore(Path.Combine(AppContext.BaseDirectory, "data", "programs"));
var io = new IoController(store, controller);
controller.AttachIo(io);
controller.OnEvent += (_, msg) => { if (msg == "点胶作业完成") io.NotifyJobDone(); };
foreach (var w in store.LoadWarnings) Console.WriteLine($"[程序仓库] 跳过损坏文件: {w}");

using var server = new DispenseWsServer(controller, collision, store, io, "http://localhost:8530/");
server.Start();

Console.WriteLine("3D 点胶服务端 (DispenseServer)");
Console.WriteLine("  轴卡: 模拟轴卡 SimulatedDispenseCard (可配置行程/零点/传感器)");
Console.WriteLine("  插补: 3D 空间直线 / 空间圆弧(三点) / 螺旋线");
Console.WriteLine("  出胶时序: 开阀延时 openDelayMs / 提前关胶 earlyCloseMs / 关胶保持 closeDelayMs / 退枪高度 retractMm");
Console.WriteLine("  脱机程序: program_save/list/load/run/delete → data/programs/");
Console.WriteLine("  虚拟 IO: io_write start/pause/reset/sel0..3 · io_list · 完成 1s 脉冲");
Console.WriteLine("  五轴 RTCP: rtcp on/off · rtcp_calibrate · dispense 带 normals → AC 双摆头联动");
Console.WriteLine("  真实机床误差: A/C 轴线偏 0.9°/0.7° + 枢轴偏移, 标定后修正");
Console.WriteLine("  撞机: 工作空间边界 + 禁止区 + 极限传感器检测");
Console.WriteLine("  监听: ws://localhost:8530/ws");
Console.WriteLine("  状态广播: 30 Hz (含传感器信号 + io + rtcp), Ctrl+C 退出");
Console.WriteLine();
Console.WriteLine("指令: home / ptp / jog / valve / upload / run / estop / reset_alarm");
Console.WriteLine("      program_save/list/load/run/delete / io_write / io_list");
Console.WriteLine("      rtcp / rtcp_calibrate / config_machine");
Console.WriteLine("      add_zone / list_zones / config_axis / get_config");
Console.WriteLine();
Console.WriteLine("撞机恢复流程: reset_alarm (清报警+轴恢复Idle) → home (回零) → 正常运动");

// 固定周期控制环 (120 Hz)
using var quit = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };

var sw = System.Diagnostics.Stopwatch.StartNew();
double last = sw.Elapsed.TotalSeconds;
const double dt = 1.0 / 120;
while (!quit.IsSet)
{
    double now = sw.Elapsed.TotalSeconds;
    double elapsed = now - last;
    if (elapsed >= dt)
    {
        last = now;
        controller.Tick(Math.Min(elapsed, 0.1));
    }
    Thread.Sleep(1);
}

Console.WriteLine("已退出。");
