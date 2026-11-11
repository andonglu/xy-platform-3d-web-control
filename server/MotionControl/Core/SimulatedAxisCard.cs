namespace MotionControl.Core;

/// <summary>
/// 模拟轴卡: 三轴独立梯形速度规划 (加速 → 巡航 → 按减速距离收敛)。
/// </summary>
public sealed class SimulatedAxisCard : IAxisCard
{
    private sealed class AxisSim(double min, double max)
    {
        public double Min = min, Max = max;
        public double Pos, Vel;
        public double? Target;
        public double TargetVel;
        public AxisState State = AxisState.Idle;
        public const double Accel = 600; // mm/s²
    }

    private readonly AxisSim[] _axes =
    [
        new AxisSim(-100, 100), // X
        new AxisSim(-100, 100), // Y
        new AxisSim(0, 60),     // Z
    ];

    public void Initialize() { /* 模拟卡无需初始化 */ }

    public void Dispose() { /* 无托管外资源 */ }

    public void SetTarget(int axis, double positionMm, double velocityMmS)
    {
        var a = _axes[axis];
        a.Target = Math.Clamp(positionMm, a.Min, a.Max);
        a.TargetVel = velocityMmS;
        a.State = AxisState.Moving;
    }

    public void SetPosition(int axis, double positionMm, double velocityMmS)
    {
        var a = _axes[axis];
        a.Pos = Math.Clamp(positionMm, a.Min, a.Max);
        a.Vel = velocityMmS;
        a.Target = null;
        a.State = AxisState.Moving;
    }

    public AxisStatus Read(int axis)
    {
        var a = _axes[axis];
        return new AxisStatus(a.Pos, a.Vel, a.State);
    }

    public void StopAll()
    {
        foreach (var a in _axes)
        {
            a.Vel = 0;
            a.Target = null;
            a.State = AxisState.Faulted;
        }
    }

    public void Tick(double dt)
    {
        foreach (var a in _axes)
        {
            if (a.State == AxisState.Faulted) continue;

            // 插补直写模式 (无目标): 控制器本周期未写入 → 速度衰减到 0 并回 Idle
            if (a.Target is null)
            {
                if (a.State == AxisState.Moving)
                {
                    double dv0 = AxisSim.Accel * dt;
                    a.Vel = Math.Abs(a.Vel) <= dv0 ? 0 : a.Vel - Math.Sign(a.Vel) * dv0;
                    if (a.Vel == 0) a.State = AxisState.Idle;
                }
                continue;
            }

            double target = a.Target.Value;
            double dist = target - a.Pos;
            double dir = Math.Sign(dist);
            double vStop = Math.Sqrt(2 * AxisSim.Accel * Math.Abs(dist)); // 减速距离约束
            double vWant = dir * Math.Min(a.TargetVel, vStop);
            double dv = AxisSim.Accel * dt;
            a.Vel += Math.Clamp(vWant - a.Vel, -dv, dv);
            a.Pos += a.Vel * dt;

            bool overshoot = (dir > 0 && a.Pos >= target) || (dir < 0 && a.Pos <= target);
            bool settled = Math.Abs(dist) < 0.02 && Math.Abs(a.Vel) < 2;
            if (overshoot || settled)
            {
                a.Pos = target;
                a.Vel = 0;
                a.Target = null;
                a.State = AxisState.Idle;
            }
        }
    }
}
