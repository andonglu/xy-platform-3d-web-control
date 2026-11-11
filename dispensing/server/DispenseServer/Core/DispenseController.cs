using DispenseServer.Collision;

namespace DispenseServer.Core;

/// <summary>
/// 3D 点胶运动控制器: 接收上位机上传的路径，执行 3D 插补运动。
/// 与激光项目的 MotionController 完全独立。
/// 
/// 工作流程:
///   上位机 → 上传路径 (upload) → 服务端执行 (run)
///   执行: PTP 到起点 → 下降到工作高度 → 开胶 → 沿 3D 路径插补 → 关胶 → 抬升
///   撞机检测每周期运行，触发即停机报警
/// </summary>
public sealed class DispenseController
{
    private readonly IDispenseAxisCard _card;
    private readonly CollisionChecker _collision;
    private readonly I3DInterpolationEngine _engine;

    public bool ValveOn { get; private set; }
    public bool CollisionAlarm { get; private set; }
    public string? CollisionMessage { get; private set; }

    private sealed class RunState
    {
        public List<DispensePath3D> Paths = new();
        public int SegIdx;          // 当前 DispensePath3D 索引
        public double S, V, Total, Feed;
        public List<I3DPath> CurrentSegs = new();
        public int SubIdx;
        public double SubS, SubTotal;
        public enum Phase { PtpStart, Descend, Dispensing, Ascending, Done }
        public Phase CurrentPhase = Phase.PtpStart;
        public Point3D StartPoint;
    }
    private RunState? _run;
    private readonly int[] _jogDir = new int[3];
    private bool _homing;
    private double _t, _emitAcc;

    public event Action<object>? OnState;
    public event Action<object>? OnPath;
    public event Action<string, string>? OnEvent;   // (level, msg)

    private const double PenDownZ = 8;   // 点胶工作高度
    private const double PenUpZ = 30;    // 安全高度
    private const double MaxAccel = 600; // mm/s²

    public DispenseController(IDispenseAxisCard card, CollisionChecker collision, I3DInterpolationEngine engine)
    {
        _card = card;
        _collision = collision;
        _engine = engine;
        _card.Initialize();
        _collision.OnCollision += msg =>
        {
            // 硬撞机 (超出边界/进入禁止区) → 停机报警
            // 软预警 (接近边界/接近禁止区) → 仅提示, 不停机
            bool isHard = msg.StartsWith("[硬撞机]");
            if (isHard)
            {
                CollisionAlarm = true;
                CollisionMessage = msg;
                EmergencyStop();
                OnEvent?.Invoke("error", msg);
            }
            else
            {
                // 软预警用 warn 级别, 不停机
                OnEvent?.Invoke("warn", msg);
            }
        };
    }

    // ---------------- 指令入口 ----------------

    public void Home()
    {
        ClearFault();
        _run = null;
        for (int i = 0; i < 3; i++)
        {
            _jogDir[i] = 0;
            _card.SetTarget(i, 0, 80);
        }
        _homing = true;
        OnEvent?.Invoke("ok", "回零开始");
    }

    public void Ptp(int axis, double pos)
    {
        if (_run is not null || axis < 0) return;
        ClearFault();
        _jogDir[axis] = 0;
        _card.SetTarget(axis, pos, axis == Axes.Z ? 60 : 120);
    }

    public void Jog(int axis, int dir)
    {
        if (_run is not null || axis < 0) return;
        ClearFault();
        _jogDir[axis] = Math.Sign(dir);
    }

    public void SetValve(bool on) => ValveOn = on;

    /// <summary>上传点胶路径 (从上位机 DTO 构建)</summary>
    public void UploadPaths(List<DispensePath3D> paths, List<double[]> preview, string name)
    {
        _run = new RunState { Paths = paths, StartPoint = GetFirstPoint(paths) };
        OnPath?.Invoke(new { type = "path", name, points3d = preview });
        OnEvent?.Invoke("ok", $"路径已上传: {paths.Count} 段, 总长 {paths.Sum(p => p.Segments.Sum(s => s.Length)):F1}mm");
    }

    /// <summary>开始执行已上传的路径</summary>
    public void RunJob()
    {
        var r = _run;
        if (r is null || r.Paths.Count == 0)
        {
            OnEvent?.Invoke("error", "没有可执行的路径，请先上传");
            return;
        }
        if (CollisionAlarm)
        {
            OnEvent?.Invoke("error", "撞机报警未复位，无法运行");
            return;
        }
        r.CurrentPhase = RunState.Phase.PtpStart;
        r.SegIdx = 0;
        r.S = 0; r.V = 0;
        r.SubIdx = 0; r.SubS = 0;

        // PTP 到第一段起点
        var first = r.Paths[0];
        var firstPt = first.Segments[0].PointAt(0);
        _card.SetTarget(Axes.X, firstPt.X, 150);
        _card.SetTarget(Axes.Y, firstPt.Y, 150);
        _card.SetTarget(Axes.Z, PenUpZ, 60);
        OnEvent?.Invoke("ok", $"开始执行点胶作业, 起点 ({firstPt.X:F1}, {firstPt.Y:F1}, {firstPt.Z:F1})");
    }

    public void EmergencyStop()
    {
        _run = null;
        ValveOn = false;
        _card.StopAll();
        OnEvent?.Invoke("error", "急停触发, 撞机报警或用户中止");
    }

    public void ResetAlarm()
    {
        CollisionAlarm = false;
        CollisionMessage = null;
        _card.StopAll();
        // 重新初始化轴卡 (清除 Faulted)
        for (int i = 0; i < 3; i++) _card.SetPosition(i, _card.Read(i).Pos, 0);
        OnEvent?.Invoke("ok", "撞机报警已复位");
    }

    private void ClearFault()
    {
        if (CollisionAlarm)
        {
            OnEvent?.Invoke("warn", "撞机报警未复位，请先 ResetAlarm");
            return;
        }
    }

    private static Point3D GetFirstPoint(List<DispensePath3D> paths)
    {
        if (paths.Count == 0 || paths[0].Segments.Count == 0) return new Point3D(0, 0, PenUpZ);
        return paths[0].Segments[0].PointAt(0);
    }

    // ---------------- 主循环 ----------------

    public void Tick(double dt)
    {
        _t += dt;

        TickJog(dt);
        TickRun(dt);
        _card.Tick(dt);

        // 撞机检测 (每周期)
        if (!CollisionAlarm)
        {
            var (triggered, isHard) = _collision.Check();
            if (triggered && isHard)
            {
                // 硬撞机: 停机+报警 (CollisionChecker.OnCollision 回调会处理)
            }
            // 软预警由 OnCollision 事件回调触发 OnEvent, 不停机
        }

        if (_homing && Enumerable.Range(0, 3).All(i => _card.Read(i).State != AxisState.Moving && _card.Read(i).State != AxisState.Homing))
        {
            _homing = false;
            OnEvent?.Invoke("ok", "回零完成");
        }

        _emitAcc += dt;
        if (_emitAcc >= 1.0 / 30)
        {
            _emitAcc = 0;
            OnState?.Invoke(BuildState());
        }
    }

    private void TickJog(double dt)
    {
        if (_run is not null) return;
        for (int i = 0; i < 3; i++)
        {
            if (_jogDir[i] == 0) continue;
            var st = _card.Read(i);
            double next = st.Pos + _jogDir[i] * 50 * dt;
            _card.SetPosition(i, next, _jogDir[i] * 50);
        }
    }

    private void TickRun(double dt)
    {
        var r = _run;
        if (r is null) return;

        switch (r.CurrentPhase)
        {
            case RunState.Phase.PtpStart:
                // 等待 PTP 到位 (XY 到起点, Z 在安全高度)
                if (Enumerable.Range(0, 3).Any(i => _card.Read(i).State == AxisState.Moving)) return;
                // 下降到工作高度
                _card.SetTarget(Axes.Z, PenDownZ, 40);
                r.CurrentPhase = RunState.Phase.Descend;
                break;

            case RunState.Phase.Descend:
                if (_card.Read(Axes.Z).State == AxisState.Moving) return;
                // 开胶
                ValveOn = r.Paths[r.SegIdx].DispenseOn;
                r.CurrentSegs = r.Paths[r.SegIdx].Segments;
                r.Total = r.CurrentSegs.Sum(s => s.Length);
                r.S = 0; r.V = 0;
                r.SubIdx = 0; r.SubS = 0;
                r.SubTotal = r.CurrentSegs[0].Length;
                r.Feed = r.Paths[r.SegIdx].Feed;
                r.CurrentPhase = RunState.Phase.Dispensing;
                break;

            case RunState.Phase.Dispensing:
                TickDispensing(r, dt);
                break;

            case RunState.Phase.Ascending:
                if (_card.Read(Axes.Z).State == AxisState.Moving) return;
                // 下一段 or 完成
                r.SegIdx++;
                if (r.SegIdx >= r.Paths.Count)
                {
                    r.CurrentPhase = RunState.Phase.Done;
                    ValveOn = false;
                    OnEvent?.Invoke("ok", "点胶作业完成");
                    _run = null;
                    return;
                }
                // PTP 到下一段起点
                var nextFirst = r.Paths[r.SegIdx].Segments[0].PointAt(0);
                _card.SetTarget(Axes.X, nextFirst.X, 150);
                _card.SetTarget(Axes.Y, nextFirst.Y, 150);
                _card.SetTarget(Axes.Z, PenUpZ, 60);
                r.CurrentPhase = RunState.Phase.PtpStart;
                break;
        }
    }

    private void TickDispensing(RunState r, double dt)
    {
        // 梯形速度规划 (减速距离约束)
        double dist = r.Total - r.S;
        double vStop = Math.Sqrt(2 * MaxAccel * Math.Max(dist, 0));
        double vWant = Math.Min(r.Feed, vStop);
        double dv = MaxAccel * dt;
        r.V += Math.Clamp(vWant - r.V, -dv, dv);
        r.S += r.V * dt;
        r.SubS += r.V * dt;

        // 在当前子段内取插补点
        while (r.SubS > r.SubTotal && r.SubIdx < r.CurrentSegs.Count - 1)
        {
            r.SubS -= r.SubTotal;
            r.SubIdx++;
            r.SubTotal = r.CurrentSegs[r.SubIdx].Length;
        }

        double localS = Math.Min(r.SubS, r.SubTotal);
        var pt = r.CurrentSegs[r.SubIdx].PointAt(localS);

        _card.SetPosition(Axes.X, pt.X, r.V);
        _card.SetPosition(Axes.Y, pt.Y, r.V);
        _card.SetPosition(Axes.Z, pt.Z, r.V);

        if (r.S >= r.Total)
        {
            r.V = 0;
            ValveOn = false;
            // 抬升到安全高度
            _card.SetTarget(Axes.Z, PenUpZ, 60);
            r.CurrentPhase = RunState.Phase.Ascending;
        }
    }

    private object BuildState()
    {
        var sx = _card.Read(Axes.X);
        var sy = _card.Read(Axes.Y);
        var sz = _card.Read(Axes.Z);

        object Pack(AxisStatus s) => new
        {
            pos = Math.Round(s.Pos, 3),
            vel = Math.Round(s.Vel, 2),
            state = _homing ? "Homing" : (CollisionAlarm ? "Faulted" : s.State.ToString()),
        };

        return new
        {
            type = "state",
            t = Math.Round(_t, 3),
            x = Pack(sx),
            y = Pack(sy),
            z = Pack(sz),
            valve = ValveOn,
            mode = _run is not null ? $"Dispense:{_run.CurrentPhase}" : (_homing ? "Homing" : "Idle"),
            collision = CollisionAlarm,
            collisionMsg = CollisionMessage ?? "",
        };
    }
}

/// <summary>3D 插补引擎接口 (预留扩展: 未来可接入 S 形加减速/NURBS)</summary>
public interface I3DInterpolationEngine
{
    // 当前实现直接用 I3DPath, 此接口保留给未来复杂插补算法
    List<I3DPath> Convert(List<DispensePath3D> paths);
}

public sealed class DefaultInterpolationEngine : I3DInterpolationEngine
{
    public List<I3DPath> Convert(List<DispensePath3D> paths)
    {
        var all = new List<I3DPath>();
        foreach (var p in paths) all.AddRange(p.Segments);
        return all;
    }
}
