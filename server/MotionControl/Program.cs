using MotionControl.Core;
using MotionControl.Core.Interpolation;
using MotionControl.Net;

// ---------------------------------------------------------------------------
// 三轴运动平台上位机 (阶段一: 模拟轴卡 + WebSocket 广播)
//   运行后监听 ws://localhost:8520/ws, Web 前端连接即可实时同步。
//   Ctrl+C 退出。
// ---------------------------------------------------------------------------

// 开发期: 模拟轴卡; 部署期: 替换为 new GtsGenAxisCard() 即可
IAxisCard card = new SimulatedAxisCard();
var planner = new InterpolationPlanner();
var controller = new MotionController(card, planner);

using var server = new WsServer(controller, "http://localhost:8520/");
server.Start();

Console.WriteLine("三轴运动平台上位机 (阶段一 MVP)");
Console.WriteLine("  轴卡: 模拟轴卡 SimulatedAxisCard (可替换 GtsGenAxisCard)");
Console.WriteLine("  插补: InterpolationPlanner (直线/圆弧; 预留空间直线/螺旋线)");
Console.WriteLine("  监听: ws://localhost:8520/ws");
Console.WriteLine("  状态广播: 30 Hz, Ctrl+C 退出");

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
