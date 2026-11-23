using DispenseHost.CadImport;
using DispenseHost.Net;

namespace DispenseHost.PathPlan;

/// <summary>P8: 上位机五轴预检查结果, Errors 阻止上传, Warnings 仅提示</summary>
public sealed record SurfaceSafetyReport(
    List<string> Errors, List<string> Warnings,
    int CheckedPoints, double MaxAStepDeg, double MaxCStepDeg)
{
    public bool IsSafe => Errors.Count == 0;
}

/// <summary>
/// P8: 曲面路径五轴预检查。上位机使用理想 AC 模型提前发现问题;
/// 服务端仍须用实际标定参数做执行前最终校验。
/// </summary>
public static class SurfacePathSafety
{
    /// <summary>把任意角度转换到 reference 的 ±180° 邻域, 消除跨 ±180° 的假跳变</summary>
    public static double UnwrapAngleDeg(double angle, double reference)
        => reference + Math.IEEERemainder(angle - reference, 360);

    public static SurfaceSafetyReport Analyze(
        IEnumerable<SurfacePath> paths, IEnumerable<AxisConfigDto>? configs,
        double toolLength, double maxPoseStepDeg = 30)
    {
        if (maxPoseStepDeg <= 0) throw new ArgumentException("最大姿态步长必须大于 0", nameof(maxPoseStepDeg));
        var axes = configs?.ToArray() ?? [];
        var errors = new List<string>();
        var warnings = new List<string>();
        if (axes.Length < 5)
            warnings.Add("轴配置不完整，上位机仅做姿态检查，最终行程以服务端校验为准");

        int checkedPoints = 0, pathIndex = 0;
        double maxAStep = 0, maxCStep = 0;
        var travelReported = new HashSet<string>();
        foreach (var path in paths)
        {
            pathIndex++;
            double? previousC = null, previousA = null;
            bool poseReported = false;
            for (int i = 0; i < path.Points.Count; i++)
            {
                if (i >= path.Normals.Count || path.Normals[i].Length < 1e-9)
                {
                    errors.Add($"路径 {pathIndex} 点 {i + 1}: 法向缺失或为零");
                    continue;
                }

                var n = path.Normals[i].Normalize();
                var tool = n; // 与服务端 FiveAxisKinematics.SolveTcp 一致: 编程法向即工具方向
                double a = Math.Acos(Math.Clamp(-tool.Z, -1, 1)) * 180 / Math.PI;
                double rawC = Math.Atan2(-tool.X, tool.Y) * 180 / Math.PI;
                bool singular = Math.Sin(a * Math.PI / 180) < 1e-8;
                double c;
                if (previousC is null)
                {
                    c = singular ? 0 : rawC;
                    var cAxis = FindAxis(axes, "c");
                    if (cAxis is not null) c = CenterInitialAngle(c, cAxis.TravelMin, cAxis.TravelMax);
                }
                else
                {
                    c = singular ? previousC.Value : UnwrapAngleDeg(rawC, previousC.Value);
                }

                // TCP = 枢轴 + 刀长方向 * 刀长, 所以行程预检应检查枢轴而非 TCP
                var pivot = path.Points[i] - tool * toolLength;
                CheckTravel("X", pivot.X, pathIndex, i);
                CheckTravel("Y", pivot.Y, pathIndex, i);
                CheckTravel("Z", pivot.Z, pathIndex, i);
                CheckTravel("A", a, pathIndex, i);
                CheckTravel("C", c, pathIndex, i);

                if (previousA is not null && previousC is not null)
                {
                    double da = Math.Abs(a - previousA.Value);
                    double dc = Math.Abs(c - previousC.Value);
                    maxAStep = Math.Max(maxAStep, da);
                    maxCStep = Math.Max(maxCStep, dc);
                    if ((da > maxPoseStepDeg || dc > maxPoseStepDeg) && !poseReported)
                    {
                        errors.Add($"路径 {pathIndex} 点 {i + 1}: 姿态突变 A={da:F2}° / C={dc:F2}°，" +
                                   $"超过 {maxPoseStepDeg:F2}°，请减小布点间距或平滑法向");
                        poseReported = true;
                    }
                }
                previousA = a;
                previousC = c;
                checkedPoints++;
            }
        }
        return new SurfaceSafetyReport(errors, warnings, checkedPoints, maxAStep, maxCStep);

        void CheckTravel(string name, double value, int pathNo, int pointNo)
        {
            var axis = FindAxis(axes, name);
            if (axis is null || (value >= axis.TravelMin && value <= axis.TravelMax)) return;
            if (!travelReported.Add($"{pathNo}:{name}")) return;
            errors.Add($"路径 {pathNo} 点 {pointNo + 1}: {name}={value:F3} 超出行程 " +
                       $"[{axis.TravelMin:F3}, {axis.TravelMax:F3}]");
        }
    }

    private static AxisConfigDto? FindAxis(IEnumerable<AxisConfigDto> axes, string name)
        => axes.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    private static double CenterInitialAngle(double angle, double min, double max)
    {
        double center = (min + max) / 2;
        double turns = Math.Round((center - angle) / 360);
        return angle + turns * 360;
    }
}
