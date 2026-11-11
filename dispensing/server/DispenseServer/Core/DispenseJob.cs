using DispenseServer.Collision;

namespace DispenseServer.Core;

/// <summary>
/// 点胶作业定义: 由上位机 CAD 路径规划后上传，服务端只执行。
/// 每条路径段是 3D 空间路径，带进给速度和胶阀开关。
/// </summary>
public sealed record DispensePath3D(
    List<I3DPath> Segments,
    double Feed,        // mm/s
    bool DispenseOn);   // 该段是否开胶

public sealed record DispenseJob(
    string Name,
    List<DispensePath3D> Paths,
    List<double[]> Preview3D);  // [[x,y,z], ...]

// ---------------------------------------------------------------------------
// JobStep: 轻量序列化 DTO, 从上位机 JSON 上传
// ---------------------------------------------------------------------------

public sealed record JobStepDto(
    string Kind,           // "ptp" | "dispense" | "valve" | "dwell"
    double[]? Target,      // [x,y,z] for ptp
    double[][]? Points,    // [[x,y,z],...] dispense path polyline
    double Feed,           // mm/s
    bool ValveOn,          // for valve / dispense
    double DwellMs);       // for dwell

public sealed record DispenseJobDto(string Name, List<JobStepDto> Steps);

/// <summary>
/// 把上位机上传的 DTO 转成执行器可用的 3D 路径列表。
/// 相邻 polyline 点之间用空间直线连接。
/// </summary>
public static class JobBuilder
{
    public static List<DispensePath3D> BuildPaths(List<JobStepDto> steps)
    {
        var result = new List<DispensePath3D>();
        foreach (var step in steps)
        {
            if (step.Kind != "dispense" || step.Points is null || step.Points.Length < 2) continue;

            var segs = new List<I3DPath>();
            for (int i = 0; i < step.Points.Length - 1; i++)
            {
                var p0 = ToPoint(step.Points[i]);
                var p1 = ToPoint(step.Points[i + 1]);
                segs.Add(new Line3DPath(p0, p1));
            }
            result.Add(new DispensePath3D(segs, step.Feed > 0 ? step.Feed : 80, step.ValveOn));
        }
        return result;
    }

    private static Point3D ToPoint(double[] arr) =>
        new(arr.Length > 0 ? arr[0] : 0, arr.Length > 1 ? arr[1] : 0, arr.Length > 2 ? arr[2] : 0);
}
