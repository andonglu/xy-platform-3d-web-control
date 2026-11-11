using System.Drawing;
using System.Drawing.Drawing2D;
using DispenseHost.Net;
using DispenseHost.PatternPlan;

namespace DispenseStudio;

/// <summary>
/// 点胶图案 2D 俯视预览: 工作范围 / 网格 / 禁止区 / 规划路径 / 实时轨迹 / 针尖位置。
/// 工作坐标 X 向右, Y 向上 (屏幕 y 反向)。
/// </summary>
public sealed class PatternCanvas : Control
{
    private static readonly Color Bg = Color.FromArgb(16, 20, 28);
    private static readonly Color GridMinor = Color.FromArgb(30, 38, 50);
    private static readonly Color GridMajor = Color.FromArgb(44, 56, 72);
    private static readonly Color AxisX = Color.FromArgb(120, 60, 60);
    private static readonly Color AxisY = Color.FromArgb(60, 110, 70);
    private static readonly Color BoundsColor = Color.FromArgb(90, 130, 170);
    private static readonly Color CircleColor = Color.FromArgb(79, 195, 253);
    private static readonly Color RectColor = Color.FromArgb(255, 183, 77);
    private static readonly Color TrailColor = Color.FromArgb(255, 112, 67);
    private static readonly Color TipColor = Color.FromArgb(255, 82, 82);
    private static readonly Color TextColor = Color.FromArgb(180, 195, 215);

    private readonly List<PlannedPattern> _patterns = new();
    private readonly List<(double X, double Y)> _trail = new();
    private (double X, double Y) _current;
    private bool _hasCurrent;
    private AxisConfigDto[]? _configs;
    private ForbiddenZoneDto[]? _zones;
    private readonly Font _font = new("Microsoft YaHei UI", 9f);
    private readonly Font _fontSmall = new("Consolas", 8f);

    public bool ShowGrid { get; set; } = true;
    public bool ShowTrail { get; set; } = true;
    public bool ShowZones { get; set; } = true;

    public PatternCanvas()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Bg;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
    }

    public void SetPatterns(IEnumerable<PlannedPattern> patterns)
    {
        _patterns.Clear();
        _patterns.AddRange(patterns);
        Invalidate();
    }

    public void SetConfigs(AxisConfigDto[]? configs) { _configs = configs; Invalidate(); }
    public void SetZones(ForbiddenZoneDto[]? zones) { _zones = zones; Invalidate(); }

    public void SetCurrent(double x, double y, bool recordTrail)
    {
        _current = (x, y);
        _hasCurrent = true;
        if (recordTrail)
        {
            if (_trail.Count == 0 || Math.Abs(_trail[^1].X - x) + Math.Abs(_trail[^1].Y - y) > 0.3)
                _trail.Add((x, y));
            if (_trail.Count > 8000) _trail.RemoveRange(0, 2000);
        }
        Invalidate();
    }

    public void ClearTrail() { _trail.Clear(); Invalidate(); }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        using var br = new SolidBrush(Bg);
        pevent.Graphics.FillRectangle(br, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var (xmin, xmax, ymin, ymax) = ComputeWorldBounds();
        double w = Math.Max(xmax - xmin, 1), h = Math.Max(ymax - ymin, 1);
        int pad = 34;
        double scale = Math.Min((Width - 2.0 * pad) / w, (Height - 2.0 * pad) / h);
        double cx = (xmin + xmax) / 2, cy = (ymin + ymax) / 2;

        PointF S(double x, double y) => new(
            (float)(Width / 2.0 + (x - cx) * scale),
            (float)(Height / 2.0 - (y - cy) * scale));

        if (ShowGrid) DrawGrid(g, S, xmin, xmax, ymin, ymax, scale);
        DrawWorkBounds(g, S, xmin, xmax, ymin, ymax);
        DrawAxes(g, S, xmin, xmax, ymin, ymax);
        if (ShowZones && _zones is not null) DrawZones(g, S);
        DrawPatterns(g, S);
        if (ShowTrail) DrawTrail(g, S);
        DrawTip(g, S);
        DrawOverlay(g, scale, xmin, xmax, ymin, ymax);
    }

    private (double XMin, double XMax, double YMin, double YMax) ComputeWorldBounds()
    {
        double xmin = -100, xmax = 100, ymin = -100, ymax = 100;
        if (_configs is { Length: >= 2 })
        {
            xmin = _configs[0].TravelMin; xmax = _configs[0].TravelMax;
            ymin = _configs[1].TravelMin; ymax = _configs[1].TravelMax;
        }
        if (_patterns.Count > 0)
        {
            // 图案超出工作范围时也纳入视野, 便于发现越界
            foreach (var p in _patterns)
            {
                xmin = Math.Min(xmin, p.MinX - 10); xmax = Math.Max(xmax, p.MaxX + 10);
                ymin = Math.Min(ymin, p.MinY - 10); ymax = Math.Max(ymax, p.MaxY + 10);
            }
        }
        if (xmax - xmin < 20) { xmin -= 10; xmax += 10; }
        if (ymax - ymin < 20) { ymin -= 10; ymax += 10; }
        return (xmin, xmax, ymin, ymax);
    }

    private void DrawGrid(Graphics g, Func<double, double, PointF> s,
        double xmin, double xmax, double ymin, double ymax, double scale)
    {
        // 根据缩放自适应网格步长
        double step = scale > 4 ? 10 : scale > 1.5 ? 20 : scale > 0.7 ? 50 : 100;
        using var minor = new Pen(GridMinor, 1);
        using var major = new Pen(GridMajor, 1);

        for (double x = Math.Ceiling(xmin / step) * step; x <= xmax; x += step)
        {
            var p1 = s(x, ymin); var p2 = s(x, ymax);
            bool isMajor = Math.Abs(x % (step * 5)) < 1e-6;
            g.DrawLine(isMajor ? major : minor, p1, p2);
            if (isMajor && scale > 0.6)
                g.DrawString($"{x:F0}", _fontSmall, Brushes.Gray, p1.X + 2, Height - 18);
        }
        for (double y = Math.Ceiling(ymin / step) * step; y <= ymax; y += step)
        {
            var p1 = s(xmin, y); var p2 = s(xmax, y);
            bool isMajor = Math.Abs(y % (step * 5)) < 1e-6;
            g.DrawLine(isMajor ? major : minor, p1, p2);
            if (isMajor && scale > 0.6)
                g.DrawString($"{y:F0}", _fontSmall, Brushes.Gray, 3, p1.Y - 12);
        }
    }

    private void DrawWorkBounds(Graphics g, Func<double, double, PointF> s,
        double xmin, double xmax, double ymin, double ymax)
    {
        var a = s(xmin, ymax); var b = s(xmax, ymin);
        using var pen = new Pen(BoundsColor, 1.5f) { DashStyle = DashStyle.Dash };
        g.DrawRectangle(pen, a.X, a.Y, b.X - a.X, b.Y - a.Y);
        g.DrawString("工作范围", _font, new SolidBrush(BoundsColor), a.X + 4, a.Y + 3);
    }

    private void DrawAxes(Graphics g, Func<double, double, PointF> s,
        double xmin, double xmax, double ymin, double ymax)
    {
        using var px = new Pen(AxisX, 1.2f);
        using var py = new Pen(AxisY, 1.2f);
        var v1 = s(0, ymin); var v2 = s(0, ymax);
        var h1 = s(xmin, 0); var h2 = s(xmax, 0);
        g.DrawLine(px, v1, v2);
        g.DrawLine(py, h1, h2);
        g.DrawString("X→", _font, new SolidBrush(Color.FromArgb(220, 120, 120)), h2.X - 26, h2.Y - 16);
        g.DrawString("Y↑", _font, new SolidBrush(Color.FromArgb(120, 210, 140)), v2.X + 4, v2.Y + 2);
    }

    private void DrawZones(Graphics g, Func<double, double, PointF> s)
    {
        foreach (var z in _zones!)
        {
            var a = s(z.XMin, z.YMax); var b = s(z.XMax, z.YMin);
            float x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
            float w = Math.Abs(b.X - a.X), h = Math.Abs(b.Y - a.Y);
            using var fill = new SolidBrush(Color.FromArgb(60, 220, 60, 60));
            using var pen = new Pen(Color.FromArgb(200, 90, 90), 1);
            g.FillRectangle(fill, x, y, w, h);
            g.DrawRectangle(pen, x, y, w, h);
            g.DrawString(z.Name, _fontSmall, Brushes.LightPink, x + 2, y + 2);
        }
    }

    private void DrawPatterns(Graphics g, Func<double, double, PointF> s)
    {
        for (int i = 0; i < _patterns.Count; i++)
        {
            var p = _patterns[i];
            var col = p.Kind == PatternKind.Circle ? CircleColor : RectColor;
            using var pen = new Pen(Color.FromArgb(210, col), 2.2f) { LineJoin = LineJoin.Round };
            var pts = p.Points.Select(q => s(q.X, q.Y)).ToArray();
            if (pts.Length >= 2) g.DrawLines(pen, pts);

            // 起点 / 方向箭头
            if (pts.Length > 0)
            {
                using var br = new SolidBrush(Color.FromArgb(120, 220, 140));
                g.FillEllipse(br, pts[0].X - 4, pts[0].Y - 4, 8, 8);
                g.DrawString($"P{i + 1}", _fontSmall, Brushes.White, pts[0].X + 6, pts[0].Y - 6);
            }
        }
    }

    private void DrawTrail(Graphics g, Func<double, double, PointF> s)
    {
        if (_trail.Count < 2) return;
        var pts = _trail.Select(q => s(q.X, q.Y)).ToArray();
        using var pen = new Pen(Color.FromArgb(190, TrailColor), 2.6f) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, pts);
    }

    private void DrawTip(Graphics g, Func<double, double, PointF> s)
    {
        if (!_hasCurrent) return;
        var p = s(_current.X, _current.Y);
        using var pen = new Pen(TipColor, 2);
        g.DrawLine(pen, p.X - 9, p.Y, p.X + 9, p.Y);
        g.DrawLine(pen, p.X, p.Y - 9, p.X, p.Y + 9);
        g.DrawEllipse(pen, p.X - 6, p.Y - 6, 12, 12);
        g.DrawString($"({_current.X:F1}, {_current.Y:F1})", _fontSmall,
            new SolidBrush(TipColor), p.X + 11, p.Y + 4);
    }

    private void DrawOverlay(Graphics g, double scale,
        double xmin, double xmax, double ymin, double ymax)
    {
        string info = $"视野 X[{xmin:F0},{xmax:F0}] Y[{ymin:F0},{ymax:F0}]  比例 {scale:F2} px/mm";
        g.DrawString(info, _font, new SolidBrush(TextColor), 10, 8);

        float y = 30;
        Legend(g, CircleColor, "规划: 圆形", ref y);
        Legend(g, RectColor, "规划: 矩形", ref y);
        Legend(g, TrailColor, "实测轨迹", ref y);
        Legend(g, TipColor, "当前针尖", ref y);
        g.DrawString($"图案 {_patterns.Count} 个 | 轨迹点 {_trail.Count}", _font,
            new SolidBrush(TextColor), 10, y + 6);
    }

    private void Legend(Graphics g, Color c, string text, ref float y)
    {
        using var pen = new Pen(c, 3);
        g.DrawLine(pen, 12, y + 6, 30, y + 6);
        g.DrawString(text, _fontSmall, new SolidBrush(TextColor), 36, y);
        y += 17;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _font.Dispose(); _fontSmall.Dispose(); }
        base.Dispose(disposing);
    }
}
