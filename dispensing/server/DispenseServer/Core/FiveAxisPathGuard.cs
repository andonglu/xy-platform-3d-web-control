namespace DispenseServer.Core;

/// <summary>P8: 服务端五轴作业预检查结果</summary>
public sealed record FiveAxisSafetyReport(
    List<string> Errors, int CheckedPoints, double MaxAStepDeg, double MaxCStepDeg)
{
    public bool IsSafe => Errors.Count == 0;
}

/// <summary>
/// P8: 用实际 RTCP 标定模型和轴配置做全程序执行前检查。
/// C 角在每条出胶段内连续展开, 段间 PTP 可选择靠近行程中心的等价角。
/// </summary>
public static class FiveAxisPathGuard
{
    public static double UnwrapAngleDeg(double angle, double reference)
        => reference + Math.IEEERemainder(angle - reference, 360);

    public static double CenterInitialAngle(double angle, double min, double max)
    {
        double center = (min + max) / 2;
        return angle + Math.Round((center - angle) / 360) * 360;
    }

    public static FiveAxisSafetyReport Validate(
        IEnumerable<DispensePath3D> paths, FiveAxisKinematics kinematics,
        IReadOnlyList<AxisConfig> configs, double maxPoseStepDeg = 30)
    {
        if (maxPoseStepDeg <= 0) throw new ArgumentException("最大姿态步长必须大于 0", nameof(maxPoseStepDeg));
        var errors = new List<string>();
        int checkedPoints = 0, pathNo = 0;
        double maxAStep = 0, maxCStep = 0;
        var travelReported = new HashSet<string>();

        foreach (var path in paths)
        {
            pathNo++;
            if (path.SegmentNormals is null) continue;
            if (path.SegmentNormals.Count != path.Segments.Count)
            {
                errors.Add($"路径 {pathNo}: 子段与法向数量不一致");
                continue;
            }

            double? previousA = null, previousC = null;
            bool poseReported = false;
            int sampleNo = 0;
            for (int segNo = 0; segNo < path.Segments.Count; segNo++)
            {
                var segment = path.Segments[segNo];
                var normals = path.SegmentNormals[segNo];
                if (normals.Length < 2)
                {
                    errors.Add($"路径 {pathNo} 子段 {segNo + 1}: 法向不完整");
                    continue;
                }
                if (segNo == 0) Process(segment.PointAt(0), normals[0]);
                Process(segment.PointAt(segment.Length), normals[1]);
            }
            continue;

            void Process(Point3D tcp, Point3D normal)
            {
                sampleNo++;
                if (normal.Length < 1e-9)
                {
                    errors.Add($"路径 {pathNo} 样本 {sampleNo}: 法向为零");
                    return;
                }
                var sol = kinematics.SolveTcp(tcp, normal);
                bool singular = Math.Sin(sol.aDeg * Math.PI / 180) < 1e-8;
                double c;
                if (previousC is null)
                    c = CenterInitialAngle(sol.cDeg, configs[Axes.C].TravelMin, configs[Axes.C].TravelMax);
                else
                    c = singular ? previousC.Value : UnwrapAngleDeg(sol.cDeg, previousC.Value);

                CheckTravel(Axes.X, sol.pivot.X, sampleNo);
                CheckTravel(Axes.Y, sol.pivot.Y, sampleNo);
                CheckTravel(Axes.Z, sol.pivot.Z, sampleNo);
                CheckTravel(Axes.A, sol.aDeg, sampleNo);
                CheckTravel(Axes.C, c, sampleNo);

                if (previousA is not null && previousC is not null)
                {
                    double da = Math.Abs(sol.aDeg - previousA.Value);
                    double dc = Math.Abs(c - previousC.Value);
                    maxAStep = Math.Max(maxAStep, da);
                    maxCStep = Math.Max(maxCStep, dc);
                    if ((da > maxPoseStepDeg || dc > maxPoseStepDeg) && !poseReported)
                    {
                        errors.Add($"路径 {pathNo} 样本 {sampleNo}: 姿态突变 A={da:F2}° / C={dc:F2}°，" +
                                   $"超过 {maxPoseStepDeg:F2}°，请减小布点间距或平滑法向");
                        poseReported = true;
                    }
                }
                previousA = sol.aDeg;
                previousC = c;
                checkedPoints++;
            }
        }
        return new FiveAxisSafetyReport(errors, checkedPoints, maxAStep, maxCStep);

        void CheckTravel(int axis, double value, int sample)
        {
            var cfg = configs[axis];
            if (value >= cfg.TravelMin && value <= cfg.TravelMax) return;
            if (!travelReported.Add($"{pathNo}:{axis}")) return;
            errors.Add($"路径 {pathNo} 样本 {sample}: {Axes.Names[axis].ToUpperInvariant()}={value:F3} " +
                       $"超出行程 [{cfg.TravelMin:F3}, {cfg.TravelMax:F3}]");
        }
    }
}
