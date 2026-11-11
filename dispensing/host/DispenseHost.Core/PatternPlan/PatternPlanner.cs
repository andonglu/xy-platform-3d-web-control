using DispenseHost.CadImport;
using DispenseHost.Net;

namespace DispenseHost.PatternPlan;

public enum PatternKind { Circle, Rectangle }

/// <summary>
/// 平面点胶图案参数。Z 为点胶工作高度，必须与服务端的 PenDownZ (8mm) 一致，
/// 否则下降到位后第一帧会出现高度跳变。
/// </summary>
public sealed class PatternSpec
{
    public PatternKind Kind { get; set; } = PatternKind.Circle;
    public string Name { get; set; } = "圆形";

    // 通用
    public double CenterX { get; set; } = 0;
    public double CenterY { get; set; } = 0;
    public double Z { get; set; } = PatternPlanner.WorkHeight;
    public double Feed { get; set; } = 60;      // mm/s
    public bool ValveOn { get; set; } = true;
    public bool Closed { get; set; } = true;

    // 圆形
    public double Radius { get; set; } = 30;    // mm
    public int Segments { get; set; } = 0;      // 0 = 按弦高误差自动分段
    public double ChordTolerance { get; set; } = 0.05; // mm
    public double StartAngleDeg { get; set; } = 0;
    public bool CounterClockwise { get; set; } = true;

    // 矩形
    public double Width { get; set; } = 80;     // mm
    public double Height { get; set; } = 50;    // mm
    public double CornerRadius { get; set; } = 0; // 0 = 直角
    public int CornerSegments { get; set; } = 6;

    public PatternSpec Clone() => (PatternSpec)MemberwiseClone();

    public string Summary => Kind switch
    {
        PatternKind.Circle => $"圆心({CenterX:F0},{CenterY:F0}) R{Radius:F0} Z{Z:F0}",
        PatternKind.Rectangle => $"中心({CenterX:F0},{CenterY:F0}) {Width:F0}×{Height:F0}" +
                                 (CornerRadius > 0 ? $" R角{CornerRadius:F0}" : "") + $" Z{Z:F0}",
        _ => Name,
    };
}

/// <summary>规划结果: 一条可直接上传的 3D 折线 + 统计信息</summary>
public sealed class PlannedPattern
{
    public required string Name { get; init; }
    public required PatternKind Kind { get; init; }
    public required PatternSpec Spec { get; init; }
    public required List<Point3D> Points { get; init; }
    public double Length { get; init; }         // mm
    public double Feed { get; init; }
    public double Z { get; init; }
    public bool ValveOn { get; init; }
    public double MinX { get; init; }
    public double MaxX { get; init; }
    public double MinY { get; init; }
    public double MaxY { get; init; }
    public double EstimatedSeconds { get; init; }

    public int PointCount => Points.Count;
    public bool IsClosed => Points.Count > 2 &&
        (Points[0] - Points[^1]).Length < 1e-6;
}

/// <summary>一个点胶作业 = 若干图案按顺序执行</summary>
public sealed class PatternJob
{
    public List<PlannedPattern> Patterns { get; } = new();
    public int TotalPoints => Patterns.Sum(p => p.PointCount);
    public double TotalLength => Patterns.Sum(p => p.Length);

    public void Add(PlannedPattern p) => Patterns.Add(p);
    public void RemoveAt(int i) { if (i >= 0 && i < Patterns.Count) Patterns.RemoveAt(i); }
    public void Clear() => Patterns.Clear();

    public List<DispenseClient.UploadStep> ToUploadSteps() =>
        Patterns.Where(p => p.PointCount >= 2)
                .Select(p => new DispenseClient.UploadStep("dispense", p.Points, p.Feed, p.ValveOn))
                .ToList();

    public List<string> ValidateAgainst(AxisConfigDto[]? configs)
    {
        var warnings = new List<string>();
        if (configs is null || configs.Length < 3) return warnings;

        AxisConfigDto? Find(string n) => configs.FirstOrDefault(c =>
            string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase));

        var cx = Find("x"); var cy = Find("y"); var cz = Find("z");
        foreach (var p in Patterns)
        {
            if (cx is not null && (p.MinX < cx.TravelMin || p.MaxX > cx.TravelMax))
                warnings.Add($"{p.Name}: X 范围 {p.MinX:F1}~{p.MaxX:F1} 超出服务端行程 {cx.TravelMin}~{cx.TravelMax}");
            if (cy is not null && (p.MinY < cy.TravelMin || p.MaxY > cy.TravelMax))
                warnings.Add($"{p.Name}: Y 范围 {p.MinY:F1}~{p.MaxY:F1} 超出服务端行程 {cy.TravelMin}~{cy.TravelMax}");
            if (cz is not null && (p.Z < cz.TravelMin || p.Z > cz.TravelMax))
                warnings.Add($"{p.Name}: Z={p.Z:F1} 超出服务端行程 {cz.TravelMin}~{cz.TravelMax}");
            if (Math.Abs(p.Z - PatternPlanner.WorkHeight) > 0.01)
                warnings.Add($"{p.Name}: Z={p.Z:F1} 与服务端点胶工作高度 {PatternPlanner.WorkHeight}mm 不一致，可能产生高度跳变");
            if (p.PointCount < 2) warnings.Add($"{p.Name}: 点数不足");
            if (p.Feed <= 0) warnings.Add($"{p.Name}: 进给速度必须 > 0");
        }
        return warnings;
    }
}

/// <summary>
/// 平面点胶图案规划器: 把参数化的圆形 / 矩形转换成服务端可执行的 3D 折线路径。
/// 服务端只负责插补执行, 所有几何计算都在上位机完成。
/// </summary>
public static class PatternPlanner
{
    /// <summary>服务端 DispenseController.PenDownZ, 平面点胶图案的固定 Z</summary>
    public const double WorkHeight = 8.0;

    public static PlannedPattern Plan(PatternSpec s)
    {
        if (s.Feed <= 0) throw new ArgumentException("进给速度必须 > 0");

        var points = s.Kind switch
        {
            PatternKind.Circle => PlanCircle(s),
            PatternKind.Rectangle => PlanRectangle(s),
            _ => throw new ArgumentOutOfRangeException(nameof(s), "未知图案类型"),
        };

        double length = 0;
        for (int i = 0; i < points.Count - 1; i++)
            length += (points[i + 1] - points[i]).Length;

        // 粗略耗时: 插补时间 + 梯形加减速 + PTP/下降/抬升开销
        double estimated = length / s.Feed + 2 * s.Feed / 600.0 + 1.5;

        return new PlannedPattern
        {
            Name = s.Name,
            Kind = s.Kind,
            Spec = s.Clone(),
            Points = points,
            Length = length,
            Feed = s.Feed,
            Z = s.Z,
            ValveOn = s.ValveOn,
            MinX = points.Min(p => p.X),
            MaxX = points.Max(p => p.X),
            MinY = points.Min(p => p.Y),
            MaxY = points.Max(p => p.Y),
            EstimatedSeconds = estimated,
        };
    }

    private static List<Point3D> PlanCircle(PatternSpec s)
    {
        if (s.Radius <= 0) throw new ArgumentException("圆半径必须 > 0");
        int n = s.Segments > 0
            ? Math.Clamp(s.Segments, 3, 2000)
            : AutoSegments(s.Radius, s.ChordTolerance);
        double a0 = s.StartAngleDeg * Math.PI / 180.0;
        double sign = s.CounterClockwise ? 1 : -1;

        var pts = new List<Point3D>(n + 1);
        for (int i = 0; i < n; i++)
        {
            double a = a0 + sign * 2 * Math.PI * i / n;
            pts.Add(new Point3D(
                s.CenterX + s.Radius * Math.Cos(a),
                s.CenterY + s.Radius * Math.Sin(a),
                s.Z));
        }
        if (s.Closed) pts.Add(pts[0]);
        return pts;
    }

    private static int AutoSegments(double radius, double chordTol)
    {
        double tol = Math.Clamp(chordTol, 0.001, radius * 0.5);
        double cos = 1 - tol / radius;
        double half = Math.Acos(Math.Clamp(cos, -1, 1));
        int n = (int)Math.Ceiling(Math.PI / Math.Max(half, 1e-6));
        return Math.Clamp(n, 12, 720);
    }

    private static List<Point3D> PlanRectangle(PatternSpec s)
    {
        if (s.Width <= 0 || s.Height <= 0) throw new ArgumentException("矩形宽高必须 > 0");
        double hw = s.Width / 2, hh = s.Height / 2;
        double l = s.CenterX - hw, r = s.CenterX + hw;
        double b = s.CenterY - hh, t = s.CenterY + hh;

        var pts = new List<Point3D>();
        double cr = Math.Clamp(s.CornerRadius, 0, Math.Min(hw, hh));

        if (cr <= 1e-9)
        {
            // 直角矩形: 逆时针, 从左下角起, 闭合
            pts.Add(new Point3D(l, b, s.Z));
            pts.Add(new Point3D(r, b, s.Z));
            pts.Add(new Point3D(r, t, s.Z));
            pts.Add(new Point3D(l, t, s.Z));
        }
        else
        {
            int seg = Math.Clamp(s.CornerSegments, 2, 90);
            // 四个圆角, 从右下角底边切点开始逆时针
            (double Cx, double Cy, double A0)[] corners =
            [
                (r - cr, b + cr, -90),   // 右下
                (r - cr, t - cr, 0),    // 右上
                (l + cr, t - cr, 90),   // 左上
                (l + cr, b + cr, 180),  // 左下
            ];
            foreach (var (cx, cy, a0) in corners)
            {
                for (int i = 0; i <= seg; i++)
                {
                    double a = (a0 + 90.0 * i / seg) * Math.PI / 180.0;
                    pts.Add(new Point3D(cx + cr * Math.Cos(a), cy + cr * Math.Sin(a), s.Z));
                }
            }
        }

        if (s.Closed) pts.Add(pts[0]);
        return pts;
    }

    /// <summary>默认演示作业: 圆形 + 矩形 (平面图案测试用)</summary>
    public static PatternJob CreateDemoJob()
    {
        var job = new PatternJob();
        job.Add(Plan(new PatternSpec
        {
            Kind = PatternKind.Circle, Name = "圆形 R30",
            CenterX = 0, CenterY = 0, Radius = 30, Z = WorkHeight, Feed = 60,
        }));
        job.Add(Plan(new PatternSpec
        {
            Kind = PatternKind.Rectangle, Name = "矩形 80x50",
            CenterX = 0, CenterY = 0, Width = 80, Height = 50, Z = WorkHeight, Feed = 60,
        }));
        return job;
    }

    /// <summary>点到折线的最短距离 (XY 平面), 用于实测轨迹与规划路径的偏差分析</summary>
    public static double DistanceToPathXY(double px, double py, IReadOnlyList<Point3D> poly)
    {
        double best = double.MaxValue;
        for (int i = 0; i < poly.Count - 1; i++)
        {
            var a = poly[i]; var b = poly[i + 1];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            double t = len2 < 1e-12 ? 0 : Math.Clamp(((px - a.X) * dx + (py - a.Y) * dy) / len2, 0, 1);
            double qx = a.X + t * dx, qy = a.Y + t * dy;
            double d = Math.Sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
            if (d < best) best = d;
        }
        return best;
    }
}
