using DispenseHost.CadImport;

namespace DispenseHost.PathPlan;

/// <summary>
/// 模型空间变换: 用户设定位置/旋转/缩放, 生成变换矩阵应用到网格。
/// </summary>
public sealed class ModelTransform
{
    /// <summary>平移 (mm)</summary>
    public Point3D Position { get; set; } = new(0, 0, 0);

    /// <summary>旋转角度 (度): 绕 X/Y/Z 轴</summary>
    public Point3D RotationDeg { get; set; } = new(0, 0, 0);

    /// <summary>缩放</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>应用到点</summary>
    public Point3D Apply(Point3D p)
    {
        double sx = p.X * Scale;
        double sy = p.Y * Scale;
        double sz = p.Z * Scale;

        double rx = RotationDeg.X * Math.PI / 180;
        double ry = RotationDeg.Y * Math.PI / 180;
        double rz = RotationDeg.Z * Math.PI / 180;

        // 绕 X 轴
        (sy, sz) = (sy * Math.Cos(rx) - sz * Math.Sin(rx), sy * Math.Sin(rx) + sz * Math.Cos(rx));
        // 绕 Y 轴
        (sx, sz) = (sx * Math.Cos(ry) + sz * Math.Sin(ry), -sx * Math.Sin(ry) + sz * Math.Cos(ry));
        // 绕 Z 轴
        (sx, sy) = (sx * Math.Cos(rz) - sy * Math.Sin(rz), sx * Math.Sin(rz) + sy * Math.Cos(rz));

        return new Point3D(sx + Position.X, sy + Position.Y, sz + Position.Z);
    }

    /// <summary>应用到整个网格</summary>
    public Mesh ApplyTo(Mesh mesh)
    {
        var result = new Mesh();
        foreach (var tri in mesh.Triangles)
        {
            result.Triangles.Add(new Triangle(
                Apply(tri.Normal),
                Apply(tri.V0),
                Apply(tri.V1),
                Apply(tri.V2)));
        }
        return result;
    }
}

/// <summary>P6: 曲面加工路径, 每个路径点携带同位置的单位法向</summary>
public sealed record SurfacePath(List<Point3D> Points, List<Point3D> Normals);

/// <summary>
/// 点胶路径生成器: 从变换后的网格提取边界边, 串成连续路径。
/// 可选: 等高线切片 (Z 分层) / 边界轮廓 / 投影到 XY 平面。
/// </summary>
public sealed class PathGenerator
{
    /// <summary>
    /// 从网格边界边生成点胶路径 (3D 折线)。
    /// 自动串接相邻边形成连续路径。
    /// </summary>
    /// <param name="mesh">变换后的网格</param>
    /// <param name="zOffset">Z 方向偏移 (点胶高度补偿)</param>
    /// <param name="simplifyTol">简化容差 (mm), 0 = 不简化</param>
    public List<List<Point3D>> GenerateBoundaryPaths(Mesh mesh, double zOffset = 0, double simplifyTol = 0.1)
    {
        var edges = mesh.GetBoundaryEdges();
        if (edges.Count == 0) return new List<List<Point3D>>();

        // 串接相邻边
        var chains = ChainEdges(edges);

        // 简化 + Z 偏移
        var result = new List<List<Point3D>>();
        foreach (var chain in chains)
        {
            var simplified = simplifyTol > 0 ? Simplify(chain, simplifyTol) : chain;
            var offset = simplified.Select(p => new Point3D(p.X, p.Y, p.Z + zOffset)).ToList();
            if (offset.Count >= 2)
                result.Add(offset);
        }
        return result;
    }

    /// <summary>
    /// 等高线切片: 在指定 Z 高度切网格, 生成 2D 截面轮廓。
    /// 用于分层点胶 (如 Z 方向多层涂胶)。
    /// </summary>
    public List<List<Point3D>> GenerateSlice(Mesh mesh, double zHeight)
        => SliceAt(mesh, zHeight);

    /// <summary>
    /// 多 Z 分层切片: 在 [zStart, zEnd] 区间按 dz 步距逐层切网格。
    /// 每层轮廓串成"下降→涂胶→抬升"的步骤序列即可模拟 3D 打印式涂胶/围坝填角。
    /// 返回按层分组的轮廓链 (Z 从低到高)。
    /// </summary>
    public List<(double Z, List<List<Point3D>> Chains)> GenerateLayeredSlice(
        Mesh mesh, double zStart, double zEnd, double dz)
    {
        if (dz <= 0) throw new ArgumentException("dz 必须大于 0", nameof(dz));
        var layers = new List<(double Z, List<List<Point3D>> Chains)>();
        for (double z = zStart; z <= zEnd + 1e-9; z += dz)
            layers.Add((Math.Round(z, 6), SliceAt(mesh, z)));
        return layers;
    }

    /// <summary>
    /// P6: STL 曲面 → 等高层加工路径 + 逐点法向。
    /// 切片交线始终携带来源三角面的法向; normalOffset 可沿法向生成等距偏置面路径。
    /// 法向按顶点绕序的右手定则重建, 避免 STL 文件中的法向缺失或反向。
    /// </summary>
    public List<SurfacePath> GenerateSurfacePaths(
        Mesh mesh, double zStart, double zEnd, double dz, double normalOffset = 0)
    {
        if (dz <= 0) throw new ArgumentException("dz 必须大于 0", nameof(dz));
        var result = new List<SurfacePath>();
        for (double z = zStart; z <= zEnd + 1e-9; z += dz)
        {
            var segments = SliceSegmentsAt(mesh, z);
            var chains = ChainTaggedEdges(segments);
            foreach (var chain in chains)
            {
                if (chain.Count < 2) continue;
                var normals = chain.Select(p => p.Tag).ToList();
                var points = chain.Select(p => p.Point + p.Tag * normalOffset).ToList();
                result.Add(new SurfacePath(points, normals));
            }
        }
        return result;
    }

    private List<List<Point3D>> SliceAt(Mesh mesh, double zHeight)
        => ChainEdges(SliceSegmentsAt(mesh, zHeight).Select(s => (s.A, s.B)).ToList());

    private static List<(Point3D A, Point3D B, Point3D Tag)> SliceSegmentsAt(Mesh mesh, double zHeight)
    {
        var intersections = new List<(Point3D A, Point3D B, Point3D Tag)>();
        foreach (var tri in mesh.Triangles)
        {
            var seg = SliceTriangle(tri, zHeight);
            if (!seg.HasValue) continue;
            var normal = Point3D.Cross(tri.V1 - tri.V0, tri.V2 - tri.V0).Normalize();
            if (normal.Length < 1e-9) continue;
            intersections.Add((seg.Value.A, seg.Value.B, normal));
        }
        return intersections;
    }

    /// <summary>
    /// 投影到 XY 平面: 把 3D 路径投影成 2D, Z 固定。
    /// 用于 "平面点胶" 模式 (忽略 Z 变化)。
    /// </summary>
    public List<List<Point3D>> ProjectToXY(List<List<Point3D>> paths, double fixedZ)
    {
        var result = new List<List<Point3D>>();
        foreach (var path in paths)
        {
            result.Add(path.Select(p => new Point3D(p.X, p.Y, fixedZ)).ToList());
        }
        return result;
    }

    // ---- 辅助: 串接边 (泛型 Tag 用于让曲面交点携带来源面法向) ----
    private static List<List<Point3D>> ChainEdges(List<(Point3D A, Point3D B)> edges)
        => ChainTaggedEdges(edges.Select(e => (e.A, e.B, 0)).ToList())
            .Select(chain => chain.Select(p => p.Point).ToList()).ToList();

    private static List<List<(Point3D Point, T Tag)>> ChainTaggedEdges<T>(
        List<(Point3D A, Point3D B, T Tag)> edges)
    {
        var remaining = new List<(Point3D A, Point3D B, T Tag)>(edges);
        var chains = new List<List<(Point3D Point, T Tag)>>();

        while (remaining.Count > 0)
        {
            var chain = new List<(Point3D Point, T Tag)>();
            var first = remaining[0];
            chain.Add((first.A, first.Tag));
            chain.Add((first.B, first.Tag));
            remaining.RemoveAt(0);

            // 向前延伸
            bool found = true;
            while (found)
            {
                found = false;
                var tail = chain[^1].Point;
                for (int i = 0; i < remaining.Count; i++)
                {
                    var e = remaining[i];
                    if (Dist(e.A, tail) < 0.01) { chain.Add((e.B, e.Tag)); remaining.RemoveAt(i); found = true; break; }
                    if (Dist(e.B, tail) < 0.01) { chain.Add((e.A, e.Tag)); remaining.RemoveAt(i); found = true; break; }
                }
            }

            // 向后延伸
            found = true;
            while (found)
            {
                found = false;
                var head = chain[0].Point;
                for (int i = 0; i < remaining.Count; i++)
                {
                    var e = remaining[i];
                    if (Dist(e.B, head) < 0.01) { chain.Insert(0, (e.A, e.Tag)); remaining.RemoveAt(i); found = true; break; }
                    if (Dist(e.A, head) < 0.01) { chain.Insert(0, (e.B, e.Tag)); remaining.RemoveAt(i); found = true; break; }
                }
            }

            chains.Add(chain);
        }
        return chains;
    }

    private static double Dist(Point3D a, Point3D b) => (a - b).Length;

    // ---- 辅助: 点简化 (Douglas-Peucker) ----
    private static List<Point3D> Simplify(List<Point3D> pts, double tol)
    {
        if (pts.Count <= 2) return pts;
        var result = new List<Point3D> { pts[0] };
        SimplizeRec(pts, 0, pts.Count - 1, tol, result);
        result.Add(pts[^1]);
        return result;
    }

    private static void SimplizeRec(List<Point3D> pts, int s, int e, double tol, List<Point3D> outList)
    {
        if (e - s < 2) return;
        double maxDist = 0;
        int maxIdx = 0;
        var a = pts[s];
        var b = pts[e];
        for (int i = s + 1; i < e; i++)
        {
            double d = PointLineDist(pts[i], a, b);
            if (d > maxDist) { maxDist = d; maxIdx = i; }
        }
        if (maxDist > tol)
        {
            SimplizeRec(pts, s, maxIdx, tol, outList);
            outList.Add(pts[maxIdx]);
            SimplizeRec(pts, maxIdx, e, tol, outList);
        }
    }

    private static double PointLineDist(Point3D p, Point3D a, Point3D b)
    {
        var ab = b - a;
        double abLen = ab.Length;
        if (abLen < 1e-9) return (p - a).Length;
        var ap = p - a;
        double t = Math.Clamp(Point3D.Dot(ap, ab) / (abLen * abLen), 0, 1);
        var proj = a + ab * t;
        return (p - proj).Length;
    }

    // ---- 辅助: 三角形切片 ----
    private static (Point3D A, Point3D B)? SliceTriangle(Triangle tri, double z)
    {
        var verts = new[] { tri.V0, tri.V1, tri.V2 };
        var crossings = new List<Point3D>();
        for (int i = 0; i < 3; i++)
        {
            var a = verts[i];
            var b = verts[(i + 1) % 3];
            if ((a.Z <= z && b.Z >= z) || (a.Z >= z && b.Z <= z))
            {
                double dz = b.Z - a.Z;
                if (Math.Abs(dz) < 1e-9) continue;
                double t = (z - a.Z) / dz;
                crossings.Add(new Point3D(
                    a.X + (b.X - a.X) * t,
                    a.Y + (b.Y - a.Y) * t,
                    z));
            }
        }
        if (crossings.Count < 2) return null;
        return (crossings[0], crossings[1]);
    }
}
