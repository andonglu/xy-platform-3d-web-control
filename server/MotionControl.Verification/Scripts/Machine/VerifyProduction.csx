Log("开始脚本化生产验证");
var started = await Machine.StartProductionAsync("star", 1, CancellationToken);
if (!started.Succeeded) throw new Exception(started.Message);
var completed = await Machine.WaitForProductionCompleteAsync(CancellationToken);
if (!completed.Succeeded) throw new Exception(completed.Message);
Log("脚本化生产验证完成");
