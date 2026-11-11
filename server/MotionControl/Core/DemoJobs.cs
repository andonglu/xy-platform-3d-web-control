using MotionControl.Core.Interpolation;

namespace MotionControl.Core;

public abstract record JobStep;
public sealed record PtpStep(double? X = null, double? Y = null, double? Z = null) : JobStep;
public sealed record LaserStep(bool On) : JobStep;
public sealed record PathStep(IReadOnlyList<IInterpolatedPath> Paths, double Feed) : JobStep;

public sealed record DemoJob(string Name, IReadOnlyList<JobStep> Steps, List<double[]> Preview);

/// <summary>内置演示作业 (与 Web 端内置模拟的图案一致)</summary>
public static class DemoJobs
{
    private const double PenDown = 8;  // 工作高度 mm
    private const double PenUp = 30;   // 安全高度 mm

    public static DemoJob? Build(string name, IInterpolationPlanner planner) => name switch
    {
        "star" => Star(planner),
        "circle" => Circle(planner),
        "combo" => Combo(planner),
        _ => null,
    };

    private static DemoJob Star(IInterpolationPlanner planner)
    {
        const double R = 60, r = 24;
        var v = new Point2D[10];
        for (int i = 0; i < 10; i++)
        {
            double rad = i % 2 == 0 ? R : r;
            double a = -Math.PI / 2 + i * Math.PI / 5;
            v[i] = new Point2D(rad * Math.Cos(a), rad * Math.Sin(a));
        }
        var paths = new List<IInterpolatedPath>();
        for (int i = 0; i < 10; i++)
        {
            var p0 = v[i]; var p1 = v[(i + 1) % 10];
            paths.Add(planner.Plan(new Point3D(p0.X, p0.Y, PenDown),
                new LineSegment(new Point3D(p1.X, p1.Y, PenDown), 120)));
        }
        var steps = new List<JobStep>
        {
            new PtpStep(Z: PenUp),
            new PtpStep(X: v[0].X, Y: v[0].Y),
            new PtpStep(Z: PenDown),
            new LaserStep(true),
            new PathStep(paths, 120),
            new LaserStep(false),
            new PtpStep(Z: PenUp),
        };
        return new DemoJob("star", steps, PathSampler.Sample(paths));
    }

    private static DemoJob Circle(IInterpolationPlanner planner)
    {
        var c1 = planner.Plan(new Point3D(50, 0, PenDown),
            new ArcSegment(new Point2D(0, 0), new Point2D(-50, 0), Clockwise: false, Feed: 100));
        var c2 = planner.Plan(new Point3D(-50, 0, PenDown),
            new ArcSegment(new Point2D(0, 0), new Point2D(50, 0), Clockwise: false, Feed: 100));
        var dia = planner.Plan(new Point3D(-50, 0, PenDown),
            new LineSegment(new Point3D(50, 0, PenDown), 100));
        var circ = new List<IInterpolatedPath> { c1, c2 };
        var steps = new List<JobStep>
        {
            new PtpStep(Z: PenUp),
            new PtpStep(X: 50, Y: 0),
            new PtpStep(Z: PenDown),
            new LaserStep(true),
            new PathStep(circ, 100),
            new PathStep(new List<IInterpolatedPath> { dia }, 100),
            new LaserStep(false),
            new PtpStep(Z: PenUp),
        };
        return new DemoJob("circle", steps, PathSampler.Sample([.. circ, dia]));
    }

    private static DemoJob Combo(IInterpolationPlanner planner)
    {
        const double w = 140, h = 100, r = 15;
        double x0 = -w / 2, x1 = w / 2, y0 = -h / 2, y1 = h / 2;

        List<IInterpolatedPath> Seg(params (Point3D from, MotionSegment seg)[] defs)
        {
            var list = new List<IInterpolatedPath>();
            foreach (var (from, seg) in defs) list.Add(planner.Plan(from, seg));
            return list;
        }
        Point3D P(double x, double y) => new(x, y, PenDown);

        var box = Seg(
            (P(x0 + r, y1), new LineSegment(P(x1 - r, y1))),
            (P(x1 - r, y1), new ArcSegment(new Point2D(x1 - r, y1 - r), P2(x1, y1 - r), true)),
            (P(x1, y1 - r), new LineSegment(P(x1, y0 + r))),
            (P(x1, y0 + r), new ArcSegment(new Point2D(x1 - r, y0 + r), P2(x1 - r, y0), true)),
            (P(x1 - r, y0), new LineSegment(P(x0 + r, y0))),
            (P(x0 + r, y0), new ArcSegment(new Point2D(x0 + r, y0 + r), P2(x0, y0 + r), true)),
            (P(x0, y0 + r), new LineSegment(P(x0, y1 - r))),
            (P(x0, y1 - r), new ArcSegment(new Point2D(x0 + r, y1 - r), P2(x0 + r, y1), true)));
        var inner = Seg((P(26, 0), new ArcSegment(new Point2D(0, 0), P2(-26, 0), false)),
                        (P(-26, 0), new ArcSegment(new Point2D(0, 0), P2(26, 0), false)));
        var cross1 = Seg((P(x0, y0), new LineSegment(P(x1, y1), 160)));
        var cross2 = Seg((P(x1, y0), new LineSegment(P(x0, y1), 160)));

        var steps = new List<JobStep>
        {
            new PtpStep(Z: PenUp),
            new PtpStep(X: x0 + r, Y: y1),
            new PtpStep(Z: PenDown),
            new LaserStep(true),
            new PathStep(box, 140),
            new LaserStep(false),
            new PtpStep(X: 26, Y: 0),
            new LaserStep(true),
            new PathStep(inner, 100),
            new LaserStep(false),
            new PtpStep(X: x0, Y: y0),
            new LaserStep(true),
            new PathStep(cross1, 160),
            new LaserStep(false),
            new PtpStep(X: x1, Y: y0),
            new LaserStep(true),
            new PathStep(cross2, 160),
            new LaserStep(false),
            new PtpStep(Z: PenUp),
        };
        var all = new List<IInterpolatedPath>();
        all.AddRange(box); all.AddRange(inner); all.AddRange(cross1); all.AddRange(cross2);
        return new DemoJob("combo", steps, PathSampler.Sample(all));

        static Point2D P2(double x, double y) => new(x, y);
    }
}
