using MotionControl.Core.Interpolation;

namespace MotionControl.Core;

/// <summary>
/// 运动控制器: 面向轴卡接口工作, 与通信层解耦。
/// 指令: home / ptp / jog / laser / job / estop
/// 事件: OnState(30Hz 状态包) / OnPath(作业预览) / OnEvent(日志)
/// </summary>
public sealed class MotionController
{
    private readonly IAxisCard _card;
    private readonly IInterpolationPlanner _planner;

    public bool Laser { get; private set; }

    private sealed class JobState
    {
        public required DemoJob Job;
        public int Idx;
        public IReadOnlyList<IInterpolatedPath>? Paths;
        public double Feed, Total, S, V;
    }
    private JobState? _job;
    private readonly int[] _jogDir = new int[3];
    private bool _homing;

    public event Action<object>? OnState;   // 状态包 (匿名对象, 由通信层序列化)
    public event Action<object>? OnPath;    // { type:"path", name, points }
    public event Action<string, string>? OnEvent; // (level, msg)

    private double _t, _emitAcc;

    public MotionController(IAxisCard card, IInterpolationPlanner planner)
    {
        _card = card;
        _planner = planner;
        _card.Initialize();
    }

    // ---------------- 指令入口 ----------------

    public void Home()
    {
        ClearFault();
        _job = null;
        for (int i = 0; i < 3; i++)
        {
            _jogDir[i] = 0;
            _card.SetTarget(i, 0, 80);
        }
        _homing = true;
        MarkHoming();
        OnEvent?.Invoke("ok", "回零开始");
    }

    public void Ptp(int axis, double pos)
    {
        if (_job is not null || axis < 0) return;
        ClearFault();
        _jogDir[axis] = 0;
        _card.SetTarget(axis, pos, axis == Axes.Z ? 60 : 120);
    }

    public void Jog(int axis, int dir)
    {
        if (_job is not null || axis < 0) return;
        ClearFault();
        _jogDir[axis] = Math.Sign(dir);
    }

    public void SetLaser(bool on) => Laser = on;

    public void StartJob(string name)
    {
        ClearFault();
        var demo = DemoJobs.Build(name, _planner);
        if (demo is null)
        {
            OnEvent?.Invoke("error", $"未知作业: {name}");
            return;
        }
        _job = new JobState { Job = demo };
        OnPath?.Invoke(new { type = "path", name, points = demo.Preview });
        OnEvent?.Invoke("ok", $"作业开始: {name}");
    }

    public void Estop()
    {
        _job = null;
        Laser = false;
        _card.StopAll();
        OnEvent?.Invoke("error", "急停触发, 各轴 Faulted (任意指令复位)");
    }

    private void ClearFault()
    {
        // 模拟卡: Faulted 复位由新指令完成; 真实卡需调用 GT_ClrSts
    }

    private void MarkHoming() { /* 状态在 BuildState 中由 _homing 推导 */ }

    // ---------------- 主循环 ----------------

    public void Tick(double dt)
    {
        _t += dt;
        TickJog(dt);
        TickJob(dt);
        _card.Tick(dt);

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
        for (int i = 0; i < 3; i++)
        {
            if (_jogDir[i] == 0) continue;
            var st = _card.Read(i);
            double next = st.Pos + _jogDir[i] * 50 * dt;
            _card.SetPosition(i, next, _jogDir[i] * 50);
        }
    }

    private void TickJob(double dt)
    {
        var j = _job;
        if (j is null) return;

        // 当前路径段: 按弧长推进, 逐周期写入插补点
        if (j.Paths is not null)
        {
            double dist = j.Total - j.S;
            double vStop = Math.Sqrt(2 * 500 * Math.Max(dist, 0));
            double vWant = Math.Min(j.Feed, vStop);
            double dv = 500 * dt;
            j.V += Math.Clamp(vWant - j.V, -dv, dv);
            j.S += j.V * dt;

            double rest = j.S;
            Point3D pt = default;
            bool found = false;
            foreach (var sub in j.Paths)
            {
                if (rest <= sub.Length) { pt = sub.PointAt(rest); found = true; break; }
                rest -= sub.Length;
            }
            if (!found) pt = j.Paths[^1].PointAt(j.Paths[^1].Length);

            _card.SetPosition(Axes.X, pt.X, j.V);
            _card.SetPosition(Axes.Y, pt.Y, j.V);

            if (j.S >= j.Total)
            {
                j.V = 0;
                j.Paths = null;   // idx 在取步骤时已推进
            }
            return;
        }

        // 等待定位完成
        if (Enumerable.Range(0, 3).Any(i => _card.Read(i).State == AxisState.Moving)) return;

        // 取下一步
        if (j.Idx >= j.Job.Steps.Count)
        {
            OnEvent?.Invoke("ok", $"作业完成: {j.Job.Name}");
            _job = null;
            return;
        }
        var step = j.Job.Steps[j.Idx++];
        switch (step)
        {
            case PtpStep p:
                if (step is PtpStep px && px.X is { } x) _card.SetTarget(Axes.X, x, 160);
                if (step is PtpStep py && py.Y is { } y) _card.SetTarget(Axes.Y, y, 160);
                if (step is PtpStep pz && pz.Z is { } z) _card.SetTarget(Axes.Z, z, 60);
                break;
            case LaserStep l:
                Laser = l.On;
                break;
            case PathStep ps:
                j.Paths = ps.Paths;
                j.Feed = ps.Feed;
                j.Total = ps.Paths.Sum(p => p.Length);
                j.S = 0; j.V = 0;
                break;
        }
    }

    private object BuildState()
    {
        var sx = _card.Read(Axes.X);
        var sy = _card.Read(Axes.Y);
        var sz = _card.Read(Axes.Z);

        object Pack(AxisStatus s, bool homing) => new
        {
            pos = Math.Round(s.Pos, 3),
            vel = Math.Round(s.Vel, 2),
            state = homing ? "Homing" : s.State.ToString(),
        };
        bool anyHoming = _homing;

        return new
        {
            type = "state",
            t = Math.Round(_t, 3),
            x = Pack(sx, anyHoming),
            y = Pack(sy, anyHoming),
            z = Pack(sz, anyHoming),
            laser = Laser,
            mode = _job is not null ? $"Job:{_job.Job.Name}" : (_homing ? "Homing" : "Idle"),
        };
    }
}
