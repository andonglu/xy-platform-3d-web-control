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

using var server = new DispenseWsServer(controller, collision, "http://localhost:8530/");
server.Start();

Console.WriteLine("3D 点胶服务端 (DispenseServer)");
Console.WriteLine("  轴卡: 模拟轴卡 SimulatedDispenseCard (可配置行程/零点/传感器)");
Console.WriteLine("  插补: 3D 空间直线 / 螺旋线");
Console.WriteLine("  撞机: 工作空间边界 + 禁止区 + 极限传感器检测");
Console.WriteLine("  监听: ws://localhost:8530/ws");
Console.WriteLine("  状态广播: 30 Hz (含传感器信号), Ctrl+C 退出");
Console.WriteLine();
Console.WriteLine("指令: home / ptp / jog / valve / upload / run / estop / reset_alarm");
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
