using System.Globalization;

namespace DispenseHost.CadImport;

/// <summary>DXF 导入结果: 一条多段线 (图层/闭合/点列)</summary>
public sealed record DxfPath(string Layer, bool IsClosed, List<Point3D> Points);

/// <summary>
/// 轻量 ASCII DXF 解析器 (R12 及以后), 零第三方依赖。
/// 支持实体: LINE / CIRCLE / ARC / LWPOLYLINE / POLYLINE(+VERTEX)。
/// 圆弧按弦高误差离散为多段线 (与 PathGenerator 输出同构, 可直接上传)。
/// 不支持: SPLINE / ELLIPSE / INSERT(块引用) / 3DFACE 等, 会计入跳过数并报告。
/// </summary>
public static class DxfImporter
{
    public static List<DxfPath> Import(string filePath, DxfImportOptions? options = null)
        => Import(new StreamReader(filePath), options);

    public static List<DxfPath> Import(TextReader reader, DxfImportOptions? options = null)
    {
        options ??= new DxfImportOptions();
        var paths = new List<DxfPath>();
        int skipped = 0;

        bool inEntities = false;
        string entityType = "";
        var codes = new Dictionary<int, List<string>>();
        // POLYLINE / VERTEX / SEQEND 三段式收集
        bool inPolyline = false;
        bool polyClosed = false;
        string polyLayer = "";
        var polyVerts = new List<Point3D>();

        void FlushEntity()
        {
            string S(int code, int idx = 0, string def = "") =>
                codes.TryGetValue(code, out var list) && idx < list.Count ? list[idx] : def;
            double D(int code, int idx = 0)
                => double.Parse(S(code, idx, "0"), CultureInfo.InvariantCulture);
            bool Has(int code) => codes.ContainsKey(code);
            string Layer() => S(8, 0, "0");
            double ZFor(string layer) =>
                options.LayerZ.TryGetValue(layer, out var z) ? z : options.DefaultZ;
            Point3D Pt(int xCode, int yCode, int zCode, double z) =>
                new(D(xCode), D(yCode), Has(zCode) ? D(zCode) : z);

            switch (entityType)
            {
                case "LINE":
                {
                    string layer = Layer();
                    double z = ZFor(layer);
                    var path = new List<Point3D> { Pt(10, 20, 30, z), Pt(11, 21, 31, z) };
                    paths.Add(new DxfPath(layer, false, path));
                    break;
                }
                case "CIRCLE":
                {
                    string layer = Layer();
                    double z = ZFor(layer);
                    var c = Pt(10, 20, 30, z);
                    paths.Add(new DxfPath(layer, true,
                        TessellateArc(c, D(40), 0, 360, z, options.ChordErrorMm)));
                    break;
                }
                case "ARC":
                {
                    string layer = Layer();
                    double z = ZFor(layer);
                    var c = Pt(10, 20, 30, z);
                    paths.Add(new DxfPath(layer, false,
                        TessellateArc(c, D(40), D(50), D(51), z, options.ChordErrorMm)));
                    break;
                }
                case "LWPOLYLINE":
                {
                    string layer = Layer();
                    double z = ZFor(layer);
                    if (!codes.TryGetValue(10, out var xs) || !codes.TryGetValue(20, out var ys)) break;
                    var path = new List<Point3D>();
                    for (int i = 0; i < xs.Count && i < ys.Count; i++)
                        path.Add(new Point3D(
                            double.Parse(xs[i], CultureInfo.InvariantCulture),
                            double.Parse(ys[i], CultureInfo.InvariantCulture), z));
                    bool closed = (int.TryParse(S(70, 0, "0"), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int flags) && (flags & 1) != 0);
                    if (closed && path.Count > 1) path.Add(path[0]);
                    paths.Add(new DxfPath(layer, closed, path));
                    break;
                }
                case "POLYLINE":
                    inPolyline = true;
                    polyClosed = (int.TryParse(S(70, 0, "0"), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int pf) && (pf & 1) != 0);
                    polyLayer = Layer();
                    polyVerts.Clear();
                    break;
                case "VERTEX":
                    if (inPolyline)
                        polyVerts.Add(new Point3D(D(10), D(20), Has(30) ? D(30) : options.DefaultZ));
                    break;
                case "SEQEND":
                    if (inPolyline && polyVerts.Count >= 2)
                    {
                        if (polyClosed) polyVerts.Add(polyVerts[0]);
                        paths.Add(new DxfPath(polyLayer, polyClosed, polyVerts));
                    }
                    inPolyline = false;
                    break;
                case "":
                    break;   // 无实体累积
                default:
                    skipped++;   // SPLINE / ELLIPSE / INSERT / ...
                    break;
            }
            codes.Clear();
        }

        while (ReadPair(reader, out int code, out string value))
        {
            if (code == 0 && !inEntities && value == "SECTION")
            {
                // 下一对 (2, 节名)
                if (ReadPair(reader, out int c2, out string section) && c2 == 2)
                    inEntities = section.Equals("ENTITIES", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inEntities) continue;
            if (code == 0 && value == "ENDSEC") { FlushEntity(); break; }
            if (code == 0)
            {
                FlushEntity();
                entityType = value;
                continue;
            }
            if (!codes.TryGetValue(code, out var list)) codes[code] = list = new List<string>();
            list.Add(value);
        }
        FlushEntity();
        if (inPolyline && polyVerts.Count >= 2)   // 缺 SEQEND 的容错
            paths.Add(new DxfPath(polyLayer, polyClosed, polyVerts));

        options.OnSkipped?.Invoke(skipped);
        return paths;
    }

    /// <summary>圆弧离散: 按弦高误差计算步进角, 保证相邻点间弓高 ≤ chordErrorMm</summary>
    public static List<Point3D> TessellateArc(Point3D center, double radius, double a0Deg, double a1Deg,
        double z, double chordErrorMm)
    {
        double sweep = a1Deg - a0Deg;
        while (sweep < 0) sweep += 360;
        double r = Math.Max(radius, 1e-6);
        double sagitta = Math.Max(chordErrorMm, 1e-4);
        double cosHalf = 1 - Math.Min(2, sagitta / r);           // sagitta = r(1-cos(θ/2))
        double stepDeg = 2 * Math.Acos(Math.Clamp(cosHalf, -1, 1)) * 180 / Math.PI;
        if (double.IsNaN(stepDeg) || stepDeg < 0.1) stepDeg = 0.1;
        if (stepDeg > 45) stepDeg = 45;
        int n = Math.Max(2, (int)Math.Ceiling(sweep / stepDeg) + 1);
        var pts = new List<Point3D>(n);
        for (int i = 0; i < n; i++)
        {
            double a = (a0Deg + sweep * i / (n - 1)) * Math.PI / 180;
            pts.Add(new Point3D(center.X + r * Math.Cos(a), center.Y + r * Math.Sin(a), z));
        }
        return pts;
    }

    private static bool ReadPair(TextReader reader, out int code, out string value)
    {
        code = 0; value = "";
        string? l1 = reader.ReadLine();
        if (l1 is null) return false;
        string? l2 = reader.ReadLine();
        if (l2 is null) return false;
        if (!int.TryParse(l1.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
            return ReadPair(reader, out code, out value);   // 容错: 跳过坏行对
        value = l2.Trim();
        return true;
    }
}

public sealed record DxfImportOptions
{
    /// <summary>无 Z 信息实体使用的默认高度</summary>
    public double DefaultZ { get; init; } = 8;
    /// <summary>圆弧离散弦高误差 (mm)</summary>
    public double ChordErrorMm { get; init; } = 0.2;
    /// <summary>图层 → 高度映射 (如 "TOP" → 12.0)</summary>
    public IReadOnlyDictionary<string, double> LayerZ { get; init; } =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    /// <summary>跳过实体数回调</summary>
    public Action<int>? OnSkipped { get; init; }
}
