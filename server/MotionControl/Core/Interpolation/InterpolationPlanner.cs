namespace MotionControl.Core.Interpolation;

/// <summary>
/// 弧长参数化路径: 2D 与 3D 统一抽象。
/// 控制器以进给速度推进弧长 s ∈ [0, Length], 由 PointAt 取空间点。
/// </summary>
public interface IInterpolatedPath
{
    double Length { get; }
    Point3D PointAt(double s);
}

/// <summary>运动段描述 (规划输入)</summary>
public abstract record MotionSegment(double Feed);

/// <summary>空间直线 (2D 直线即 z 不变)</summary>
public sealed record LineSegment(Point3D To, double Feed = 100) : MotionSegment(Feed);

/// <summary>XY 平面圆弧: 圆心 + 终点 + 方向 (起点取当前位置)</summary>
public sealed record ArcSegment(Point2D Center, Point2D To, bool Clockwise, double Feed = 100) : MotionSegment(Feed);

/// <summary>3D 扩展预留: 空间直线</summary>
public sealed record Line3DSegment(Point3D To, double Feed = 100) : MotionSegment(Feed);

/// <summary>3D 扩展预留: 螺旋线 (XY 圆弧 + Z 同步爬升)</summary>
public sealed record HelixSegment(Point2D Center, Point2D To, double ZTo, bool Clockwise, double Feed = 100)
    : MotionSegment(Feed);

/// <summary>
/// 插补规划器接口 —— 运动控制与具体算法解耦, 替换实现即可切换算法。
/// </summary>
public interface IInterpolationPlanner
{
    IInterpolatedPath Plan(Point3D from, MotionSegment segment);
}

/// <summary>默认实现: 直线 / 圆弧 / 空间直线 / 螺旋线</summary>
public sealed class InterpolationPlanner : IInterpolationPlanner
{
    public IInterpolatedPath Plan(Point3D from, MotionSegment segment) => segment switch
    {
        LineSegment l => new LinePath(from, l.To),
        Line3DSegment l3 => new LinePath(from, l3.To),
        ArcSegment arc => PlanArc(from, arc),
        HelixSegment hx => PlanHelix(from, hx),
        _ => throw new NotSupportedException($"未支持的段类型: {segment.GetType().Name}"),
    };

    private static IInterpolatedPath PlanArc(Point3D from, ArcSegment seg)
    {
        double r0 = Math.Sqrt((from.X - seg.Center.X) * (from.X - seg.Center.X) + (from.Y - seg.Center.Y) * (from.Y - seg.Center.Y));
        double r1 = Math.Sqrt((seg.To.X - seg.Center.X) * (seg.To.X - seg.Center.X) + (seg.To.Y - seg.Center.Y) * (seg.To.Y - seg.Center.Y));
        if (Math.Abs(r0 - r1) > 0.5)
            throw new ArgumentException($"圆弧起点({r0:F2})与终点({r1:F2})半径不一致");
        double a0 = Math.Atan2(from.Y - seg.Center.Y, from.X - seg.Center.X);
        double a1 = Math.Atan2(seg.To.Y - seg.Center.Y, seg.To.X - seg.Center.X);
        double sweep = a1 - a0;
        if (seg.Clockwise && sweep >= 0) sweep -= Math.PI * 2;
        if (!seg.Clockwise && sweep <= 0) sweep += Math.PI * 2;
        return new ArcPath(seg.Center, r0, a0, sweep, from.Z);
    }

    private static IInterpolatedPath PlanHelix(Point3D from, HelixSegment seg)
    {
        var arc = PlanArc(from, new ArcSegment(seg.Center, seg.To, seg.Clockwise, seg.Feed));
        return new HelixPath(arc, from.Z, seg.ZTo);
    }
}

public sealed class LinePath(Point3D from, Point3D to) : IInterpolatedPath
{
    private readonly double _dx = to.X - from.X, _dy = to.Y - from.Y, _dz = to.Z - from.Z;
    public double Length { get; } = Math.Sqrt(
        (to.X - from.X) * (to.X - from.X) + (to.Y - from.Y) * (to.Y - from.Y) + (to.Z - from.Z) * (to.Z - from.Z));

    public Point3D PointAt(double s)
    {
        double t = Length == 0 ? 0 : Math.Min(s / Length, 1);
        return new Point3D(from.X + _dx * t, from.Y + _dy * t, from.Z + _dz * t);
    }
}

public sealed class ArcPath(Point2D center, double radius, double a0, double sweep, double z) : IInterpolatedPath
{
    public double Length { get; } = Math.Abs(radius * sweep);

    public Point3D PointAt(double s)
    {
        double t = Length == 0 ? 0 : Math.Min(s / Length, 1);
        double a = a0 + sweep * t;
        return new Point3D(center.X + radius * Math.Cos(a), center.Y + radius * Math.Sin(a), z);
    }
}

/// <summary>螺旋线: 平面圆弧 + Z 按弧长比例同步</summary>
public sealed class HelixPath(IInterpolatedPath planar, double z0, double z1) : IInterpolatedPath
{
    public double Length => planar.Length;

    public Point3D PointAt(double s)
    {
        var p = planar.PointAt(s);
        double t = Length == 0 ? 0 : Math.Min(s / Length, 1);
        return p with { Z = z0 + (z1 - z0) * t };
    }
}

/// <summary>把路径按弧长等距采样 (用于下发预览点列)</summary>
public static class PathSampler
{
    public static List<double[]> Sample(IEnumerable<IInterpolatedPath> paths, double step = 1.0)
    {
        var pts = new List<double[]>();
        foreach (var p in paths)
        {
            int n = Math.Max(2, (int)Math.Ceiling(p.Length / step));
            for (int i = 0; i <= n; i++)
            {
                var q = p.PointAt(i / (double)n * p.Length);
                pts.Add([Math.Round(q.X, 3), Math.Round(q.Y, 3)]);
            }
        }
        return pts;
    }
}
