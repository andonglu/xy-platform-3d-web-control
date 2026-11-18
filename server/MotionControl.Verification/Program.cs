using MotionControl.Equipment;
using MotionControl.Hardware.Abstractions;
using MotionControl.Hardware.Adapters;
using MotionControl.Scripting;

static void Ensure(EquipmentResult result, string operation)
{ if (!result.Succeeded) throw new Exception($"{operation}: {result.Message}"); }

async Task RunProduction(IMotionAxisCardPort motion, ILaserControlPort laser, string channel)
{
    Console.WriteLine($"[{channel}] 轴卡 {motion.Endpoint}");
    Console.WriteLine($"[{channel}] 激光器 {laser.Endpoint}");
    await using (motion)
    await using (laser)
    {
        var service = new LaserEquipmentService(motion, laser);
        var platform = new LaserPlatform(service);
        var scripts = new ScriptService(platform);
        scripts.Initialize(Path.Combine(AppContext.BaseDirectory, "Scripts"));
        scripts.LogReceived += (_, e) => Console.WriteLine($"  [script] {e.Message}");
        Ensure(await platform.ConnectAsync(), "ConnectBothCards");

        // 验证激光指令独立经过 ILaserControlPort。
        Ensure(await platform.SetLaserAsync(true), "LaserOn");
        for (int i = 0; i < 30 && !platform.Snapshot.Laser; i++) await Task.Delay(50);
        if (!platform.Snapshot.Laser) throw new Exception("激光控制卡状态未同步");
        Ensure(await platform.SetLaserAsync(false), "LaserOff");
        Console.WriteLine($"[{channel}] 激光独立通道 PASS");

        if (platform.Snapshot.Production.Phase == "Faulted") Ensure(await platform.ResetProductionAsync(), "Reset");
        Ensure(await platform.HomeAsync(), "Home");
        Ensure(await platform.WaitForIdleAsync(TimeSpan.FromSeconds(20)), "WaitHome");
        var script = scripts.Scripts.Single(s => s.Name.EndsWith("VerifyProduction.csx"));
        var result = await scripts.RunAsync(script);
        if (result.State != ScriptState.Completed) throw new Exception($"脚本状态 {result.State}: {result.Message}");
        if (platform.Snapshot.Production.Phase != "Complete") throw new Exception("平台快照未进入 Complete");
        Console.WriteLine($"[{channel}] PASS · {platform.Snapshot.Production.WorkpieceId} · 累计 {platform.Snapshot.Production.Completed} 件");

        if (channel == "real-protocol")
        {
            var wait = scripts.Scripts.Single(s => s.Name.EndsWith("WaitCancel.csx"));
            var running = scripts.RunAsync(wait);
            await Task.Delay(300);
            await scripts.StopAsync();
            var stopped = await running;
            if (stopped.State != ScriptState.Cancelled) throw new Exception($"脚本取消失败: {stopped.State}");
            Console.WriteLine("[script-stop] PASS · 已取消并急停");
            Ensure(await platform.ResetProductionAsync(), "ResetAfterStop");
            Ensure(await platform.HomeAsync(), "HomeAfterStop");
            Ensure(await platform.WaitForIdleAsync(TimeSpan.FromSeconds(20)), "WaitAfterStop");
        }
        await platform.DisconnectAsync();
    }
}

string endpoint = args.Length > 0 ? args[0] : "ws://127.0.0.1:8080/ws";
string motionEndpoint = endpoint.TrimEnd('/') + "/motion";
string laserEndpoint = endpoint.TrimEnd('/') + "/laser";
try
{
    await RunProduction(new VirtualAxisCardPort(motionEndpoint), new VirtualLaserControlPort(laserEndpoint), "virtual");
    // 使用协议兼容网关模拟真实双卡，仅替换两个 Port；服务、平台、脚本完全不变。
    await RunProduction(new RealAxisCardPort(motionEndpoint), new RealLaserControlPort(laserEndpoint), "real-protocol");
    Console.WriteLine("ALL VERIFICATION PASSED");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"VERIFICATION FAILED: {ex}");
    return 1;
}
