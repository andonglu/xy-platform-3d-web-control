namespace DispenseServer.Core;

/// <summary>
/// 3D 空间插补路径抽象 —— 弧长参数化，支持空间直线、空间圆弧、螺旋线。
/// 与激光项目的 2D 插补完全独立，这里所有路径都是 3D 的。
/// </summary>
public interface I3DPath
{
    double Length { get; }
    Point3D PointAt(double s);
}

/// <summary>空间直线</summary>
public sealed class Line3DPath : I3DPath
{
    private readonly Point3D _from, _to;
    public Line3DPath(Point3D from, Point3D to)
    {
        _from = from;
        _to = to;
        Length = Math.Sqrt(
            (to.X - from.X) * (to.X - from.X) +
            (to.Y - from.Y) * (to.Y - from.Y) +
            (to.Z - from.Z) * (to.Z - from.Z));
    }

    public double Length { get; }

    public Point3D PointAt(double s)
    {
        double t = Length == 0 ? 0 : Math.Min(s / Length, 1);
        return new Point3D(_from.X + (_to.X - _from.X) * t, _from.Y + (_to.Y - _from.Y) * t, _from.Z + (_to.Z - _from.Z) * t);
    }
}

/// <summary>空间圆弧: 三点定义平面圆弧 (起点/中间点/终点)</summary>
public sealed class Arc3DPath : I3DPath
{
    private readonly Point3D _p0, _p1, _p2;
    private readonly Point3D _center;
    private readonly double _radius;
    private readonly double _a0, _a1, _sweep;
    private readonly Point3D _normal;

    public Arc3DPath(Point3D p0, Point3D p1, Point3D p2)
    {
        _p0 = p0; _p1 = p1; _p2 = p2;

        // 三点求圆心和法线
        var v1 = p1 - p0;
        var v2 = p2 - p0;
        var normal = Cross(v1, v2);
        double nlen = normal.Length;
        if (nlen < 1e-9) throw new ArgumentException("三点共线，无法定义圆弧");
        _normal = new Point3D(normal.X / nlen, normal.Y / nlen, normal.Z / nlen);

        // 求圆心: 在平面局部坐标系求
        var u = v1;
        double ulen = u.Length;
        var uN = new Point3D(u.X / ulen, u.Y / ulen, u.Z / ulen);
        var vN = Cross(_normal, uN);
        double b = ulen; // p1 in local = (b, 0)
        // p2 in local:
        var v2Vec = p2 - p0;
        double cx = v2Vec.X * uN.X + v2Vec.Y * uN.Y + v2Vec.Z * uN.Z;
        double cy = v2Vec.X * vN.X + v2Vec.Y * vN.Y + v2Vec.Z * vN.Z;
        // 圆心 local: (b/2, (cx^2+cy^2 - b*cx) / (2*cy))
        double centerX = b / 2;
        double centerY = (cx * cx + cy * cy - b * cx) / (2 * cy);
        _center = p0 + uN * centerX + vN * centerY;
        _radius = Math.Sqrt(centerX * centerX + centerY * centerY);

        _a0 = Math.Atan2(centerY, centerX); // p0 angle in local
        // p2 angle in local:
        double p2lx = cx - centerX, p2ly = cy - centerY;
        _a1 = Math.Atan2(p2ly, p2lx);

        // sweep: 选最短弧 (经过 p1)
        double sweep1 = _a1 - _a0;
        // normalize to [-PI, PI]
        while (sweep1 > Math.PI) sweep1 -= 2 * Math.PI;
        while (sweep1 < -Math.PI) sweep1 += 2 * Math.PI;

        // 检查 p1 (local angle b/2 - centerX, -centerY) 是否在弧上
        double p1lx = b - centerX, p1ly = -centerY;
        double aMid = Math.Atan2(p1ly, p1lx);
        double midInSweep = (aMid - _a0) * Math.Sign(sweep1);
        while (midInSweep < 0) midInSweep += 2 * Math.PI;
        if (midInSweep > Math.Abs(sweep1))
            sweep1 = sweep1 > 0 ? sweep1 - 2 * Math.PI : sweep1 + 2 * Math.PI;

        _sweep = sweep1;
    }

    public double Length => Math.Abs(_radius * _sweep);

    public Point3D PointAt(double s)
    {
        double t = Length == 0 ? 0 : Math.Min(s / Length, 1);
        double a = _a0 + _sweep * t;
        // local point
        double lx = _radius * Math.Cos(a);
        double ly = _radius * Math.Sin(a);
        // transform back: center + lx*uN + ly*vN
        var uN = _p1 - _p0;
        double ulen = uN.Length;
        var u = new Point3D(uN.X / ulen, uN.Y / ulen, uN.Z / ulen);
        var v = Cross(_normal, u);
        return _center + u * lx + v * ly;
    }

    private static Point3D Cross(Point3D a, Point3D b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}

/// <summary>螺旋线: XY 圆弧 + Z 线性同步</summary>
public sealed class Helix3DPath : I3DPath
{
    private readonly I3DPath _planar;
    private readonly double _z0, _z1;

    public Helix3DPath(I3DPath planar, double z0, double z1)
    {
        _planar = planar;
        _z0 = z0;
        _z1 = z1;
    }

    public double Length => _planar.Length;

    public Point3D PointAt(double s)
    {
        var p = _planar.PointAt(s);
        double t = Length == 0 ? 0 : Math.Min(s / Length, 1);
        return p with { Z = _z0 + (_z1 - _z0) * t };
    }
}

/// <summary>由路径列表采样出 3D 点列 (用于预览)</summary>
public static class PathSampler3D
{
    public static List<double[]> Sample(IEnumerable<I3DPath> paths, double step = 1.0)
    {
        var pts = new List<double[]>();
        foreach (var p in paths)
        {
            int n = Math.Max(2, (int)Math.Ceiling(p.Length / step));
            for (int i = 0; i <= n; i++)
            {
                var q = p.PointAt(i / (double)n * p.Length);
                pts.Add([Math.Round(q.X, 3), Math.Round(q.Y, 3), Math.Round(q.Z, 3)]);
            }
        }
        return pts;
    }
}
