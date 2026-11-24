using System.Drawing.Drawing2D;
using DispenseHost.CadImport;
using DispenseHost.Net;
using DispenseHost.PathPlan;

namespace DispenseStudio;

/// <summary>P9 场景交互：默认旋转，Face/Edge 模式单击选择，Ctrl 单击追加。</summary>
public enum SurfacePickMode { Rotate, Face, Edge }

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
    private FixtureConfig? _fixture;
    private readonly HashSet<int> _selectedFaces = [];
    private readonly HashSet<int> _regionFaces = [];
    private readonly HashSet<int> _selectedEdges = [];
    private readonly HashSet<int> _regionEdges = [];
    private IReadOnlyList<(Point3D A, Point3D B)> _uniqueEdges = [];
    private SurfacePickMode _pickMode = SurfacePickMode.Rotate;

    public event Action<IReadOnlyList<int>>? OnFacesSelected;
    public event Action<IReadOnlyList<int>>? OnEdgesSelected;
    public IReadOnlyList<int> SelectedFaces => _selectedFaces.Order().ToArray();
    public IReadOnlyList<int> SelectedEdges => _selectedEdges.Order().ToArray();
    public SurfacePickMode PickMode => _pickMode;

    public SurfaceCanvas()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Bg;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
    }

    /// <summary>设置模型、路径、夹具和面/边区域高亮，并按场景包围盒重新适配视野。</summary>
    public void SetScene(Mesh? mesh, IEnumerable<SurfacePath> paths,
        FixtureConfig? fixture = null, IEnumerable<int>? regionFaces = null,
        IEnumerable<int>? regionEdges = null)
    {
        _mesh = mesh;
        _uniqueEdges = mesh is null ? [] : PathGenerator.GetUniqueEdges(mesh);
        _fixture = fixture;
        _selectedFaces.Clear();
        _selectedEdges.Clear();
        _regionFaces.Clear();
        _regionEdges.Clear();
        if (regionFaces is not null) _regionFaces.UnionWith(regionFaces);
        if (regionEdges is not null) _regionEdges.UnionWith(regionEdges);
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

    public void SetPickMode(SurfacePickMode mode)
    {
        _pickMode = mode;
        _dragging = false;
        Cursor = mode == SurfacePickMode.Rotate ? Cursors.Default : Cursors.Cross;
        Invalidate();
    }

    public void ClearFaceSelection()
    {
        _selectedFaces.Clear();
        OnFacesSelected?.Invoke(SelectedFaces);
        Invalidate();
    }

    public void ClearEdgeSelection()
    {
        _selectedEdges.Clear();
        OnEdgesSelected?.Invoke(SelectedEdges);
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

    private bool HasScene => (_mesh?.Triangles.Count ?? 0) > 0 || _paths.Count > 0 || _fixture is not null;

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
        foreach (var point in FixturePoints()) Include(point);

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

        // 绘制与拾取共用同一投影，避免视觉位置和实际选中面不一致。
        var project = CreateProjection();
        DrawFixture(g, project);
        DrawMesh(g, project);
        DrawPaths(g, project);
        DrawTrail(g, project);
        DrawTool(g, project);
        DrawOverlay(g);
    }

    private Func<Point3D, PointF> CreateProjection()
    {
        double scale = Math.Min(ClientSize.Width, ClientSize.Height) * 0.38 / _radius * _zoom;
        double cy = Math.Cos(_yaw), sy = Math.Sin(_yaw);
        double cp = Math.Cos(_pitch), sp = Math.Sin(_pitch);
        double screenCx = ClientSize.Width / 2.0, screenCy = ClientSize.Height / 2.0;
        return p =>
        {
            double x = p.X - _center.X, y = p.Y - _center.Y, z = p.Z - _center.Z;
            double rx = cy * x + sy * y;
            double ry = -sy * x + cy * y;
            double up = cp * ry + sp * z;
            return new PointF((float)(screenCx + rx * scale), (float)(screenCy - up * scale));
        };
    }

    private double ViewDepth(Point3D p)
    {
        double x = p.X - _center.X, y = p.Y - _center.Y, z = p.Z - _center.Z;
        double ry = -Math.Sin(_yaw) * x + Math.Cos(_yaw) * y;
        return -Math.Sin(_pitch) * ry + Math.Cos(_pitch) * z;
    }

    private IEnumerable<Point3D> FixturePoints()
    {
        if (_fixture is null) yield break;
        var transform = _fixture.Pose.ToTransform();
        foreach (var point in new[]
        {
            new Point3D(-35, -25, 0), new Point3D(35, -25, 0),
            new Point3D(35, 25, 0), new Point3D(-35, 25, 0),
            new Point3D(45, 0, 0), new Point3D(0, 35, 0), new Point3D(0, 0, 35),
        }) yield return transform.Apply(point);
    }

    private void DrawFixture(Graphics g, Func<Point3D, PointF> project)
    {
        if (_fixture is null) return;
        var transform = _fixture.Pose.ToTransform();
        var corners = new[]
        {
            new Point3D(-35, -25, 0), new Point3D(35, -25, 0),
            new Point3D(35, 25, 0), new Point3D(-35, 25, 0),
        }.Select(p => project(transform.Apply(p))).ToArray();
        using var baseBrush = new SolidBrush(Color.FromArgb(30, 180, 150, 255));
        using var basePen = new Pen(Color.FromArgb(160, 150, 190, 230), 1.5f);
        g.FillPolygon(baseBrush, corners);
        g.DrawPolygon(basePen, corners);
        var origin = transform.Apply(default);
        using var axisPen = new Pen(Color.FromArgb(220, 200, 170, 80), 1.6f);
        g.DrawLine(axisPen, project(origin), project(transform.Apply(new Point3D(45, 0, 0))));
        g.DrawLine(axisPen, project(origin), project(transform.Apply(new Point3D(0, 35, 0))));
        g.DrawLine(axisPen, project(origin), project(transform.Apply(new Point3D(0, 0, 35))));
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

        // 已保存区域和当前人工选择分别着色；索引始终对应原始三角面顺序。
        using var regionBrush = new SolidBrush(Color.FromArgb(48, 80, 220, 160));
        using var selectedBrush = new SolidBrush(Color.FromArgb(92, 255, 190, 50));
        using var selectedPen = new Pen(Color.FromArgb(255, 210, 70), 1.8f);
        foreach (int index in _regionFaces.Concat(_selectedFaces).Distinct())
        {
            if (index < 0 || index >= count) continue;
            var tri = _mesh!.Triangles[index];
            var points = new[] { project(tri.V0), project(tri.V1), project(tri.V2) };
            g.FillPolygon(_selectedFaces.Contains(index) ? selectedBrush : regionBrush, points);
            if (_selectedFaces.Contains(index)) g.DrawPolygon(selectedPen, points);
        }

        // 唯一边索引与 PathGenerator 完全一致，内部边和模型边界均可独立高亮。
        using var regionEdgePen = new Pen(Color.FromArgb(220, 80, 220, 190), 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var selectedEdgePen = new Pen(Color.FromArgb(255, 100, 210), 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        foreach (int index in _regionEdges.Concat(_selectedEdges).Distinct())
        {
            if (index < 0 || index >= _uniqueEdges.Count) continue;
            var edge = _uniqueEdges[index];
            g.DrawLine(_selectedEdges.Contains(index) ? selectedEdgePen : regionEdgePen,
                project(edge.A), project(edge.B));
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
        g.DrawString($"三角面 {triangles:N0} | 路径 {_paths.Count} | 选中面 {_selectedFaces.Count} | 选中边 {_selectedEdges.Count} | 轨迹点 {_trail.Count:N0}",
            _font, brush, 8, 7);
        string help = _pickMode switch
        {
            SurfacePickMode.Face => "面拾取：单击选择 / Ctrl+单击追加  |  双击复位视角",
            SurfacePickMode.Edge => "边拾取：单击最近边 / Ctrl+单击追加  |  双击复位视角",
            _ => "左键拖动旋转  |  滚轮缩放  |  双击复位",
        };
        g.DrawString(help, _font, brush, 8, ClientSize.Height - _font.Height - 7);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _lastMouse = e.Location;
        if (_pickMode != SurfacePickMode.Rotate) return;
        _dragging = true;
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
        if (e.Button != MouseButtons.Left) return;
        if (_pickMode != SurfacePickMode.Rotate)
        {
            if (Math.Abs(e.X - _lastMouse.X) <= 4 && Math.Abs(e.Y - _lastMouse.Y) <= 4)
            {
                bool additive = (ModifierKeys & Keys.Control) != 0;
                if (_pickMode == SurfacePickMode.Face) PickFace(e.Location, additive);
                else PickEdge(e.Location, additive);
            }
            return;
        }
        if (!_dragging) return;
        _dragging = false;
        Cursor = Cursors.Default;
    }

    private void PickFace(Point screen, bool additive)
    {
        if (_mesh is null) return;
        if (!additive) _selectedFaces.Clear();
        var project = CreateProjection();
        int hit = -1;
        double nearest = double.NegativeInfinity;
        for (int i = 0; i < _mesh.Triangles.Count; i++)
        {
            var tri = _mesh.Triangles[i];
            var a = project(tri.V0); var b = project(tri.V1); var c = project(tri.V2);
            if (!PointInTriangle(screen, a, b, c)) continue;
            var center = new Point3D((tri.V0.X + tri.V1.X + tri.V2.X) / 3,
                (tri.V0.Y + tri.V1.Y + tri.V2.Y) / 3, (tri.V0.Z + tri.V1.Z + tri.V2.Z) / 3);
            double depth = ViewDepth(center);
            if (depth > nearest) { nearest = depth; hit = i; }
        }
        if (hit >= 0 && !_selectedFaces.Add(hit)) _selectedFaces.Remove(hit);
        OnFacesSelected?.Invoke(SelectedFaces);
        Invalidate();
    }

    private void PickEdge(Point screen, bool additive)
    {
        if (_mesh is null) return;
        if (!additive) _selectedEdges.Clear();
        var project = CreateProjection();
        int hit = -1;
        double bestDistance = 9.0; // 最大拾取半径 8 像素，保留 1 像素比较余量
        double nearestDepth = double.NegativeInfinity;
        for (int i = 0; i < _uniqueEdges.Count; i++)
        {
            var edge = _uniqueEdges[i];
            double distance = DistanceToSegment(screen, project(edge.A), project(edge.B));
            if (distance > 8) continue;
            double depth = ViewDepth(new Point3D((edge.A.X + edge.B.X) / 2,
                (edge.A.Y + edge.B.Y) / 2, (edge.A.Z + edge.B.Z) / 2));
            if (distance < bestDistance - 0.05 ||
                (Math.Abs(distance - bestDistance) <= 0.05 && depth > nearestDepth))
            {
                hit = i;
                bestDistance = distance;
                nearestDepth = depth;
            }
        }
        if (hit >= 0 && !_selectedEdges.Add(hit)) _selectedEdges.Remove(hit);
        OnEdgesSelected?.Invoke(SelectedEdges);
        Invalidate();
    }

    private static double DistanceToSegment(PointF p, PointF a, PointF b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;
        double t = lengthSquared < 1e-9 ? 0 :
            Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
        double x = a.X + t * dx, y = a.Y + t * dy;
        return Math.Sqrt((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y));
    }

    private static bool PointInTriangle(PointF p, PointF a, PointF b, PointF c)
    {
        double d1 = Cross(p, a, b), d2 = Cross(p, b, c), d3 = Cross(p, c, a);
        bool negative = d1 < -0.01 || d2 < -0.01 || d3 < -0.01;
        bool positive = d1 > 0.01 || d2 > 0.01 || d3 > 0.01;
        return !(negative && positive);
    }

    private static double Cross(PointF p, PointF a, PointF b)
        => (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);

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
