using DispenseServer.Core;

namespace DispenseServer.Core;

/// <summary>
/// 模拟轴卡 —— 点胶专用，三轴工作空间 X:[-150,150] Y:[-150,150] Z:[0,80]
/// 梯形速度规划，支持 SetPosition (插补直写) 和 SetTarget (定位)。
/// </summary>
public sealed class SimulatedDispenseCard : IDispenseAxisCard
{
    private sealed class AxisSim(double min, double max)
    {
        public double Min = min, Max = max;
        public double Pos, Vel;
        public double? Target;
        public double TargetVel;
        public AxisState State = AxisState.Idle;
        public const double Accel = 800; // mm/s²
    }

    private readonly AxisSim[] _axes =
    [
        new AxisSim(-150, 150), // X
        new AxisSim(-150, 150), // Y
        new AxisSim(0, 80),     // Z
    ];

    public void Initialize() { }

    public void Dispose() { }

    public void SetTarget(int axis, double posMm, double velMmS)
    {
        var a = _axes[axis];
        a.Target = Math.Clamp(posMm, a.Min, a.Max);
        a.TargetVel = velMmS;
        a.State = AxisState.Moving;
    }

    public void SetPosition(int axis, double posMm, double velMmS)
    {
        var a = _axes[axis];
        a.Pos = Math.Clamp(posMm, a.Min, a.Max);
        a.Vel = velMmS;
        a.Target = null;
        a.State = AxisState.Moving;
    }

    public AxisStatus Read(int axis)
    {
        var a = _axes[axis];
        return new AxisStatus(a.Pos, a.Vel, a.State);
    }

    public (double Min, double Max) GetLimit(int axis) => (_axes[axis].Min, _axes[axis].Max);

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
            double vStop = Math.Sqrt(2 * AxisSim.Accel * Math.Abs(dist));
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
