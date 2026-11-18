using DispenseHost.CadImport;
using DispenseHost.Net;

namespace DispenseHost.PathPlan;

/// <summary>
/// 出胶工艺时序 (统一下发到每一段; 对应服务端 ValveTiming):
///   OpenDelayMs   开阀前延时 —— 等胶路到达针头, 防起点缺胶
///   EarlyCloseMs  提前关胶   —— 减速段胶量同步, 防末端堆胶
///   CloseDelayMs  关胶保持   —— 等回吸完成再抬枪, 防拖尾
///   RetractMm     退枪高度   —— 抬枪附加高度, 防挂胶
/// </summary>
public sealed record DispenseTiming(double OpenDelayMs = 0, double EarlyCloseMs = 0,
    double CloseDelayMs = 0, double RetractMm = 0);

/// <summary>
/// 轨迹 → 上传步骤装配器。
/// 把 DXF 实体 / 网格切片轮廓 / 示教路径统一转成可直接上传的点胶程序,
/// 可整体附加工艺时序 (P1), 也可逐段覆盖。
/// </summary>
public static class JobAssembler
{
    /// <summary>平面路径 → 上传步骤 (可附加统一时序)</summary>
    public static List<DispenseClient.UploadStep> Assemble(
        List<List<Point3D>> paths, double feed, bool valveOn = true, DispenseTiming? timing = null)
        => paths.Where(p => p.Count >= 2).Select(p => MakeStep(p, feed, valveOn, timing)).ToList();

    /// <summary>DXF 实体 → 上传步骤 (图层/Z 已由导入器解析)</summary>
    public static List<DispenseClient.UploadStep> Assemble(
        List<DxfPath> entities, double feed, bool valveOn = true, DispenseTiming? timing = null)
        => entities.Where(e => e.Points.Count >= 2).Select(e => MakeStep(e.Points, feed, valveOn, timing)).ToList();

    /// <summary>多 Z 分层切片 → 上传步骤 (每层轮廓依次成段, 自动附加逐层递进的进给)</summary>
    public static List<DispenseClient.UploadStep> Assemble(
        List<(double Z, List<List<Point3D>> Chains)> layers, double feed,
        bool valveOn = true, DispenseTiming? timing = null)
    {
        var steps = new List<DispenseClient.UploadStep>();
        foreach (var (_, chains) in layers)
            steps.AddRange(Assemble(chains, feed, valveOn, timing));
        return steps;
    }

    private static DispenseClient.UploadStep MakeStep(List<Point3D> pts, double feed, bool valveOn, DispenseTiming? t)
        => t is null
            ? new DispenseClient.UploadStep("dispense", pts, feed, valveOn)
            : new DispenseClient.UploadStep("dispense", pts, feed, valveOn,
                OpenDelayMs: t.OpenDelayMs, EarlyCloseMs: t.EarlyCloseMs,
                CloseDelayMs: t.CloseDelayMs, RetractMm: t.RetractMm);
}
