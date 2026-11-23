using System.Drawing.Drawing2D;
using DispenseHost.CadImport;
using DispenseHost.Net;
using DispenseHost.PathPlan;

namespace DispenseStudio;

/// <summary>
/// STL 曲面作业三维预览：模型线框、加工路径、法向、开胶轨迹与针头姿态。
/// 使用 GDI+ 正交投影，不依赖显卡或第三方三维库。
/// </summary>
public sealed class SurfaceCanvas : Control
{
    private const double DefaultYaw = -Math.PI / 4;
    private const double DefaultPitch = Math.PI / 6;
    private static readonly Color Bg = Color.FromArgb(16, 20, 28);
    private static readonly Color MeshColor = Color.FromArgb(105, 125, 150);
    private static readonly Color PathColor = Color.FromArgb(79, 195, 253);
    private static readonly Color NormalColor = Color.FromArgb(120, 210, 140);
    private static readonly Color TrailColor = Color.FromArgb(255, 112, 67);
    private static readonly Color ToolColor = Color.FromArgb(255, 202, 40);
    private static readonly Color TipColor = Color.FromArgb(255, 82, 82);
    private static readonly Color TextColor = Color.FromArgb(180, 195, 215);

    private readonly List<SurfacePath> _paths = new();
    private readonly List<Point3D> _trail = new();
    private readonly Font _font = new("Microsoft YaHei UI", 9f);
    private Mesh? _mesh;
    private Point3D _center;
    private Point3D _pivot;
    private Point3D _tcp;
    private double _radius = 1;
    private double _yaw = DefaultYaw;
    private double _pitch = DefaultPitch;
    private double _zoom = 1;
    private bool _dragging;
    private bool _hasCurrent;
    private bool _hasNeedleBody;
    private Point _lastMouse;

    public SurfaceCanvas()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Bg;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
    }

    /// <summary>设置模型与曲面路径，并按场景包围盒重新适配视野。</summary>
    public void SetScene(Mesh? mesh, IEnumerable<SurfacePath> paths)
    {
        _mesh = mesh;
        _paths.Clear();
        if (paths is not null)
        {
            foreach (var path in paths)
                if (path.Points.Count > 0) _paths.Add(path);
        }
        _trail.Clear();
        FitScene();
        ResetView();
        Invalidate();
    }

    /// <summary>更新机器状态；胶阀打开时记录实际 TCP，而不是 XYZ 枢轴。</summary>
    public void SetCurrent(MachineStateDto state)
    {
        _pivot = new Point3D(state.X.Pos, state.Y.Pos, state.Z.Pos);
        _tcp = _pivot;
        _hasNeedleBody = false;

        // AC 双摆头理想几何：XYZ 是旋转枢轴，刀长方向决定真实针尖。
        if (state.Rtcp is { Enabled: true } rtcp && state.A is { } a && state.C is { } c)
        {
            double ar = a.Pos * Math.PI / 180;
            double cr = c.Pos * Math.PI / 180;
            var t = new Point3D(
                -Math.Sin(ar) * Math.Sin(cr),
                Math.Sin(ar) * Math.Cos(cr),
                -Math.Cos(ar));
            _tcp = _pivot + t * rtcp.ToolLength;
            _hasNeedleBody = true;
        }

        _hasCurrent = true;
        if (state.Valve && (_trail.Count == 0 || (_tcp - _trail[^1]).Length > 0.15))
            _trail.Add(_tcp);
        if (_trail.Count > 12000) _trail.RemoveRange(0, 3000);
        Invalidate();
    }

    /// <summary>清除开胶实际轨迹，不影响规划路径和当前针头。</summary>
    public void ClearTrail()
    {
        _trail.Clear();
        Invalidate();
    }

    private bool HasScene => (_mesh?.Triangles.Count ?? 0) > 0 || _paths.Count > 0;

    private void ResetView()
    {
        _yaw = DefaultYaw;
        _pitch = DefaultPitch;
        _zoom = 1;
    }

    private void FitScene()
    {
        bool any = false;
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;

        void Include(Point3D p)
        {
            any = true;
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
        }

        if (_mesh is not null)
        {
            foreach (var tri in _mesh.Triangles)
            {
                Include(tri.V0); Include(tri.V1); Include(tri.V2);
            }
        }
        foreach (var path in _paths)
            foreach (var point in path.Points) Include(point);

        if (!any)
        {
            _center = default;
            _radius = 1;
            return;
        }

        _center = new Point3D((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
        double dx = maxX - minX, dy = maxY - minY, dz = maxZ - minZ;
        _radius = Math.Max(Math.Sqrt(dx * dx + dy * dy + dz * dz) / 2, 0.5);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        using var brush = new SolidBrush(Bg);
        pevent.Graphics.FillRectangle(brush, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (ClientSize.Width < 24 || ClientSize.Height < 24) return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        if (!HasScene)
        {
            using var brush = new SolidBrush(TextColor);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("暂无曲面场景\n请导入 STL 并生成曲面路径", _font, brush, ClientRectangle, format);
            return;
        }

        // 包围球适配可保证旋转后场景仍在视野内；屏幕 Y 轴向下。
        double scale = Math.Min(ClientSize.Width, ClientSize.Height) * 0.38 / _radius * _zoom;
        double cy = Math.Cos(_yaw), sy = Math.Sin(_yaw);
        double cp = Math.Cos(_pitch), sp = Math.Sin(_pitch);
        double screenCx = ClientSize.Width / 2.0, screenCy = ClientSize.Height / 2.0;
        PointF Project(Point3D p)
        {
            double x = p.X - _center.X, y = p.Y - _center.Y, z = p.Z - _center.Z;
            double rx = cy * x + sy * y;
            double ry = -sy * x + cy * y;
            double up = cp * ry + sp * z;
            return new PointF((float)(screenCx + rx * scale), (float)(screenCy - up * scale));
        }

        DrawMesh(g, Project);
        DrawPaths(g, Project);
        DrawTrail(g, Project);
        DrawTool(g, Project);
        DrawOverlay(g);
    }

    private void DrawMesh(Graphics g, Func<Point3D, PointF> project)
    {
        int count = _mesh?.Triangles.Count ?? 0;
        if (count == 0) return;

        // 大型 STL 均匀抽样，避免每次重绘遍历全部三角面。
        int shown = Math.Min(count, 1800);
        using var pen = new Pen(MeshColor, 0.8f);
        for (int n = 0; n < shown; n++)
        {
            var tri = _mesh!.Triangles[(int)((long)n * count / shown)];
            var a = project(tri.V0);
            var b = project(tri.V1);
            var c = project(tri.V2);
            g.DrawLine(pen, a, b);
            g.DrawLine(pen, b, c);
            g.DrawLine(pen, c, a);
        }
    }

    private void DrawPaths(Graphics g, Func<Point3D, PointF> project)
    {
        using var pathPen = new Pen(PathColor, 2.2f) { LineJoin = LineJoin.Round };
        using var normalPen = new Pen(NormalColor, 1.1f);
        using var pointBrush = new SolidBrush(PathColor);
        double normalLength = Math.Max(_radius * 0.045, 0.5);

        foreach (var path in _paths)
        {
            int normalCount = Math.Min(path.Points.Count, path.Normals.Count);
            int marks = Math.Min(normalCount, 24); // 每条路径最多画 24 根法向短线
            for (int k = 0; k < marks; k++)
            {
                int index = (int)((long)k * normalCount / marks);
                var normal = path.Normals[index];
                if (normal.Length < 1e-9) continue;
                var start = path.Points[index];
                g.DrawLine(normalPen, project(start), project(start + normal.Normalize() * normalLength));
            }

            if (path.Points.Count == 1)
            {
                var p = project(path.Points[0]);
                g.FillEllipse(pointBrush, p.X - 3, p.Y - 3, 6, 6);
                continue;
            }
            var points = new PointF[path.Points.Count];
            for (int i = 0; i < points.Length; i++) points[i] = project(path.Points[i]);
            g.DrawLines(pathPen, points);
        }
    }

    private void DrawTrail(Graphics g, Func<Point3D, PointF> project)
    {
        if (_trail.Count < 2) return;
        var points = new PointF[_trail.Count];
        for (int i = 0; i < points.Length; i++) points[i] = project(_trail[i]);
        using var pen = new Pen(TrailColor, 2.5f) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, points);
    }

    private void DrawTool(Graphics g, Func<Point3D, PointF> project)
    {
        if (!_hasCurrent) return;
        var tip = project(_tcp);
        if (_hasNeedleBody)
        {
            var pivot = project(_pivot);
            using var bodyPen = new Pen(ToolColor, 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var headBrush = new SolidBrush(ToolColor);
            g.DrawLine(bodyPen, pivot, tip);
            g.FillEllipse(headBrush, pivot.X - 5, pivot.Y - 5, 10, 10);
        }

        using var tipBrush = new SolidBrush(TipColor);
        using var tipPen = new Pen(TipColor, 1.5f);
        g.FillEllipse(tipBrush, tip.X - 3.5f, tip.Y - 3.5f, 7, 7);
        g.DrawEllipse(tipPen, tip.X - 7, tip.Y - 7, 14, 14);
    }

    private void DrawOverlay(Graphics g)
    {
        using var brush = new SolidBrush(TextColor);
        int triangles = _mesh?.Triangles.Count ?? 0;
        g.DrawString($"三角面 {triangles:N0} | 路径 {_paths.Count} | 轨迹点 {_trail.Count:N0}",
            _font, brush, 8, 7);
        g.DrawString("左键拖动旋转  |  滚轮缩放  |  双击复位", _font, brush,
            8, ClientSize.Height - _font.Height - 7);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        _lastMouse = e.Location;
        Cursor = Cursors.SizeAll;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        _yaw += (e.X - _lastMouse.X) * 0.012;
        _pitch = Math.Clamp(_pitch + (e.Y - _lastMouse.Y) * 0.012,
            -Math.PI / 2 + 0.02, Math.PI / 2 - 0.02);
        _lastMouse = e.Location;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !_dragging) return;
        _dragging = false;
        Cursor = Cursors.Default;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _zoom = Math.Clamp(_zoom * Math.Pow(1.15, e.Delta / 120.0), 0.1, 30);
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left) return;
        ResetView();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _font.Dispose();
        base.Dispose(disposing);
    }
}
