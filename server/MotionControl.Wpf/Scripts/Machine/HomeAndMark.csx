Log("脚本：回零并加工一件五角星");
if (Machine.GetSnapshot().Production.Phase == "Faulted")
{
    var reset = await Machine.ResetProductionAsync(CancellationToken);
    if (!reset.Succeeded) throw new Exception(reset.Message);
}
var home = await Machine.HomeAsync(CancellationToken);
if (!home.Succeeded) throw new Exception(home.Message);
var idle = await Machine.WaitForIdleAsync(CancellationToken);
if (!idle.Succeeded) throw new Exception(idle.Message);
await DelayAsync(300);
var started = await Machine.StartProductionAsync("star", 1, CancellationToken);
if (!started.Succeeded) throw new Exception(started.Message);
var completed = await Machine.WaitForProductionCompleteAsync(CancellationToken);
if (!completed.Succeeded) throw new Exception(completed.Message);
Log($"脚本完成：{Machine.GetSnapshot().Production.WorkpieceId}，累计 {Machine.GetSnapshot().Production.Completed} 件");
