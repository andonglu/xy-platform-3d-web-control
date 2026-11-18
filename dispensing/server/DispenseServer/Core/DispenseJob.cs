using DispenseServer.Collision;

namespace DispenseServer.Core;

/// <summary>
/// 点胶作业定义: 由上位机 CAD 路径规划后上传，服务端只执行。
/// 每条路径段是 3D 空间路径，带进给速度和胶阀开关。
/// </summary>
public sealed record DispensePath3D(
    List<I3DPath> Segments,
    double Feed,        // mm/s
    bool DispenseOn,    // 该段是否开胶
    ValveTiming Timing = default,   // 指令级出胶时序参数 (可选, 默认零 = 原行为)
    List<Point3D[]>? SegmentNormals = null);  // P4: 每子段 [起点法向,终点法向] (RTCP 五轴)

/// <summary>
/// 指令级出胶时序参数 (对应真实点胶机"每条运动指令独立工艺参数"):
///   OpenDelay  — 下降到工作高度后、开阀前等待时间 (等胶路到达针头)
///   EarlyClose — 提前关胶: 剩余弧长 ≤ 提前量 时关阀, 减速段胶量同步, 末端不堆胶
///   CloseDelay — 到点后保持关胶的时间 (回吸完成) 再抬枪
///   Retract    — 抬枪时额外抬升高度 (退枪高度, 防拖尾挂胶)
/// </summary>
public readonly record struct ValveTiming(double OpenDelaySec, double EarlyCloseSec, double CloseDelaySec, double RetractMm)
{
    public static ValveTiming Zero => default;
    public bool HasOpenDelay => OpenDelaySec > 0;
    public bool HasEarlyClose => EarlyCloseSec > 0;
    public bool HasCloseDelay => CloseDelaySec > 0;
    public bool HasRetract => RetractMm > 0;
}

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
    double[][]? Points,    // [[x,y,z],...] dispense path polyline (2 点时配合 ArcVia 表示圆弧)
    double[]? ArcVia,      // [x,y,z] 圆弧中间点 (三点定圆弧, 可选)
    double Feed,           // mm/s
    bool ValveOn,          // for valve / dispense
    double DwellMs,        // for dwell
    double OpenDelayMs = 0,    // 开阀前延时 (胶到达针头)
    double EarlyCloseMs = 0,   // 提前关胶时间 (剩余弧长 ≤ 提前量时关阀)
    double CloseDelayMs = 0,   // 到点后关胶保持时间 (回吸完成再抬枪)
    double RetractMm = 0,      // 退枪高度 (抬枪额外抬升)
    double[][]? Normals = null);  // P4: 与 points 一一对应的曲面法向 [nx,ny,nz] (RTCP)

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

            var timing = new ValveTiming(
                Math.Max(0, step.OpenDelayMs) / 1000.0,
                Math.Max(0, step.EarlyCloseMs) / 1000.0,
                Math.Max(0, step.CloseDelayMs) / 1000.0,
                Math.Max(0, step.RetractMm));
            double feed = step.Feed > 0 ? step.Feed : 80;

            // 三点圆弧: Points=[起点,终点] + ArcVia=中间点
            if (step.ArcVia is { Length: 3 } && step.Points.Length == 2)
            {
                var arc = new Arc3DPath(ToPoint(step.Points[0]), ToPoint(step.ArcVia), ToPoint(step.Points[1]));
                result.Add(new DispensePath3D([arc], feed, step.ValveOn, timing));
                continue;
            }

            // P4: 曲面法向 (与 points 一一对应) → 每子段 [起点法向, 终点法向]
            List<Point3D[]>? segmentNormals = null;
            if (step.Normals is not null && step.Normals.Length == step.Points.Length)
            {
                segmentNormals = new List<Point3D[]>();
                for (int i = 0; i < step.Points.Length - 1; i++)
                    segmentNormals.Add([ToPoint(step.Normals[i]), ToPoint(step.Normals[i + 1])]);
            }

            // 折线: 相邻点空间直线连接
            var segs = new List<I3DPath>();
            for (int i = 0; i < step.Points.Length - 1; i++)
            {
                segs.Add(new Line3DPath(ToPoint(step.Points[i]), ToPoint(step.Points[i + 1])));
            }
            result.Add(new DispensePath3D(segs, feed, step.ValveOn, timing, segmentNormals));
        }
        return result;
    }

    private static Point3D ToPoint(double[] arr) =>
        new(arr.Length > 0 ? arr[0] : 0, arr.Length > 1 ? arr[1] : 0, arr.Length > 2 ? arr[2] : 0);
}
