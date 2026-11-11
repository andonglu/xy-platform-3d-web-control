using System.Globalization;
using System.Text;

namespace DispenseHost.CadImport;

/// <summary>
/// 轻量 STL 解析器 (ASCII + Binary), 零第三方依赖。
/// 返回三角形列表, 每个三角形含法线和 3 个顶点。
/// </summary>
public sealed record Triangle(Point3D Normal, Point3D V0, Point3D V1, Point3D V2);

public readonly record struct Point3D(double X, double Y, double Z)
{
    public static Point3D operator +(Point3D a, Point3D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Point3D operator -(Point3D a, Point3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Point3D operator *(Point3D a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public static double Dot(Point3D a, Point3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Point3D Cross(Point3D a, Point3D b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public Point3D Normalize()
    {
        double l = Length;
        return l < 1e-12 ? this : new Point3D(X / l, Y / l, Z / l);
    }
}

/// <summary>STL 三角网格</summary>
public sealed class Mesh
{
    public List<Triangle> Triangles { get; } = new();

    /// <summary>计算包围盒</summary>
    public (Point3D Min, Point3D Max) BoundingBox()
    {
        if (Triangles.Count == 0) return (new(0, 0, 0), new(0, 0, 0));
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var t in Triangles)
        {
            foreach (var v in new[] { t.V0, t.V1, t.V2 })
            {
                minX = Math.Min(minX, v.X); maxX = Math.Max(maxX, v.X);
                minY = Math.Min(minY, v.Y); maxY = Math.Max(maxY, v.Y);
                minZ = Math.Min(minZ, v.Z); maxZ = Math.Max(maxZ, v.Z);
            }
        }
        return (new Point3D(minX, minY, minZ), new Point3D(maxX, maxY, maxZ));
    }

    /// <summary>计算所有边缘 (共享边 = 仅被一个三角形使用的边)</summary>
    public List<(Point3D A, Point3D B)> GetBoundaryEdges()
    {
        var edgeCount = new Dictionary<string, (Point3D A, Point3D B, int Count)>();
        foreach (var t in Triangles)
        {
            AddEdge(t.V0, t.V1);
            AddEdge(t.V1, t.V2);
            AddEdge(t.V2, t.V0);
        }
        return edgeCount.Values.Where(e => e.Count == 1).Select(e => (e.A, e.B)).ToList();

        void AddEdge(Point3D a, Point3D b)
        {
            string key = EdgeKey(a, b);
            if (edgeCount.TryGetValue(key, out var existing))
                edgeCount[key] = (existing.A, existing.B, existing.Count + 1);
            else
                edgeCount[key] = (a, b, 1);
        }

        static string EdgeKey(Point3D a, Point3D b)
        {
            // 规范化: 小顶点在前
            if (a.X < b.X || (a.X == b.X && a.Y < b.Y) || (a.X == b.X && a.Y == b.Y && a.Z < b.Z))
                return $"{a.X:F4},{a.Y:F4},{a.Z:F4}-{b.X:F4},{b.Y:F4},{b.Z:F4}";
            return $"{b.X:F4},{b.Y:F4},{b.Z:F4}-{a.X:F4},{a.Y:F4},{a.Z:F4}";
        }
    }
}

public sealed class StlImporter
{
    /// <summary>从文件导入 STL (自动检测 ASCII/Binary)</summary>
    public static Mesh Import(string path)
    {
        var bytes = File.ReadAllBytes(path);
        // ASCII STL 以 "solid" 开头 (但 binary 也可能以 solid 开头, 用大小判断)
        bool isAscii = bytes.Length < 512 && Encoding.ASCII.GetString(bytes).TrimStart().StartsWith("solid", StringComparison.OrdinalIgnoreCase);
        if (!isAscii && bytes.Length >= 84)
        {
            // binary STL: 前 80 字节 header, 然后 4 字节 triangle count
            int triCount = BitConverter.ToInt32(bytes, 80);
            long expectedSize = 84 + triCount * 50;
            isAscii = bytes.Length != expectedSize;
        }

        return isAscii ? ParseAscii(bytes) : ParseBinary(bytes);
    }

    private static Mesh ParseAscii(byte[] bytes)
    {
        var mesh = new Mesh();
        var text = Encoding.ASCII.GetString(bytes);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Point3D normal = default;
        Point3D[] verts = new Point3D[3];
        int vi = 0;

        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.StartsWith("facet normal", StringComparison.OrdinalIgnoreCase))
            {
                var parts = t.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                normal = new Point3D(
                    double.Parse(parts[2], CultureInfo.InvariantCulture),
                    double.Parse(parts[3], CultureInfo.InvariantCulture),
                    double.Parse(parts[4], CultureInfo.InvariantCulture));
                vi = 0;
            }
            else if (t.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
            {
                var parts = t.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                verts[vi++] = new Point3D(
                    double.Parse(parts[1], CultureInfo.InvariantCulture),
                    double.Parse(parts[2], CultureInfo.InvariantCulture),
                    double.Parse(parts[3], CultureInfo.InvariantCulture));
                if (vi == 3)
                {
                    mesh.Triangles.Add(new Triangle(normal, verts[0], verts[1], verts[2]));
                    vi = 0;
                }
            }
        }
        return mesh;
    }

    private static Mesh ParseBinary(byte[] bytes)
    {
        var mesh = new Mesh();
        int triCount = BitConverter.ToInt32(bytes, 80);
        int offset = 84;
        for (int i = 0; i < triCount; i++)
        {
            float nx = BitConverter.ToSingle(bytes, offset);
            float ny = BitConverter.ToSingle(bytes, offset + 4);
            float nz = BitConverter.ToSingle(bytes, offset + 8);
            float v0x = BitConverter.ToSingle(bytes, offset + 12);
            float v0y = BitConverter.ToSingle(bytes, offset + 16);
            float v0z = BitConverter.ToSingle(bytes, offset + 20);
            float v1x = BitConverter.ToSingle(bytes, offset + 24);
            float v1y = BitConverter.ToSingle(bytes, offset + 28);
            float v1z = BitConverter.ToSingle(bytes, offset + 32);
            float v2x = BitConverter.ToSingle(bytes, offset + 36);
            float v2y = BitConverter.ToSingle(bytes, offset + 40);
            float v2z = BitConverter.ToSingle(bytes, offset + 44);
            // offset + 48: attribute byte count (2 bytes), skip
            offset += 50;

            mesh.Triangles.Add(new Triangle(
                new Point3D(nx, ny, nz),
                new Point3D(v0x, v0y, v0z),
                new Point3D(v1x, v1y, v1z),
                new Point3D(v2x, v2y, v2z)));
        }
        return mesh;
    }
}
