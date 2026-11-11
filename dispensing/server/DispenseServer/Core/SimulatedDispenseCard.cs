using DispenseServer.Core;

namespace DispenseServer.Core;

/// <summary>
/// 模拟轴卡 —— 点胶专用，三轴 X/Y/Z。
/// 支持轴配置（行程/零点/传感器位置）、梯形速度规划、回零逻辑、传感器信号触发。
/// 撞机（触及极限传感器）时自动 Faulted。
/// </summary>
public sealed class SimulatedDispenseCard : IDispenseAxisCard
{
    private sealed class AxisSim
    {
        public AxisConfig Cfg;
        public double Pos, Vel;
        public double? Target;
        public double TargetVel;
        public AxisState State = AxisState.Idle;
        public double HomeDir = 1;     // 回零方向 (+1 朝正/ -1 朝负)
        public bool HomeSignalLatch;   // 回零时零点信号锁存

        public AxisSim(AxisConfig cfg)
        {
            Cfg = cfg;
            Pos = cfg.HomePos;  // 默认在零点
        }

        public const double Accel = 800; // mm/s²
        public const double SensorTolerance = 0.5; // mm, 传感器触发容差
    }

    // 默认配置: X/Y 对称 ±150, Z 0~80, 零点在行程中间
    private readonly AxisSim[] _axes =
    [
        new AxisSim(new AxisConfig(-150, 150, 0, -148, 148, 0, 20)),   // X: 零点在 0, 负极限 -148, 正极限 +148
        new AxisSim(new AxisConfig(-150, 150, 0, -148, 148, 0, 20)),   // Y
        new AxisSim(new AxisConfig(0, 80, 40, 2, 78, 40, 15)),         // Z: 行程 0~80, 零点 40, 负极限 2, 正极限 78
    ];

    public void Initialize()
    {
        // 轴默认在零点位置, Idle 状态
        foreach (var a in _axes) { a.State = AxisState.Idle; }
    }

    public void Dispose() { }

    public void ConfigureAxis(int axis, AxisConfig cfg)
    {
        var a = _axes[axis];
        a.Cfg = cfg;
        // 如果当前位置不在新行程内, 移到零点
        if (a.Pos < cfg.TravelMin || a.Pos > cfg.TravelMax)
            a.Pos = cfg.HomePos;
    }

    public AxisConfig GetConfig(int axis) => _axes[axis].Cfg;

    public void SetTarget(int axis, double posMm, double velMmS)
    {
        var a = _axes[axis];
        if (a.State == AxisState.Faulted) return;  // Faulted 不响应运动指令
        a.Target = Math.Clamp(posMm, a.Cfg.TravelMin, a.Cfg.TravelMax);
        a.TargetVel = Math.Abs(velMmS);
        a.State = AxisState.Moving;
    }

    public void SetPosition(int axis, double posMm, double velMmS)
    {
        var a = _axes[axis];
        if (a.State == AxisState.Faulted) return;
        a.Pos = Math.Clamp(posMm, a.Cfg.TravelMin, a.Cfg.TravelMax);
        a.Vel = velMmS;
        a.Target = null;
        a.State = AxisState.Moving;
    }

    public AxisStatus Read(int axis)
    {
        var a = _axes[axis];
        return new AxisStatus(a.Pos, a.Vel, a.State);
    }

    /// <summary>
    /// 读取传感器信号: 当轴位置接近传感器位置时信号为 true。
    /// </summary>
    public SensorState ReadSensors(int axis)
    {
        var a = _axes[axis];
        double tol = AxisSim.SensorTolerance;
        bool home = Math.Abs(a.Pos - a.Cfg.HomeSensorPos) < tol;
        bool limPos = Math.Abs(a.Pos - a.Cfg.LimitPositive) < tol;
        bool limNeg = Math.Abs(a.Pos - a.Cfg.LimitNegative) < tol;
        return new SensorState(home, limPos, limNeg);
    }

    public (double Min, double Max) GetLimit(int axis) => (_axes[axis].Cfg.TravelMin, _axes[axis].Cfg.TravelMax);

    /// <summary>
    /// 急停: 停止所有运动, 轴进入 Faulted。
    /// </summary>
    public void StopAll()
    {
        foreach (var a in _axes)
        {
            a.Vel = 0;
            a.Target = null;
            a.State = AxisState.Faulted;
        }
    }

    /// <summary>
    /// 清除 Faulted 状态: 轴恢复到 Idle (位置保持不变)。
    /// 由控制器在 ResetAlarm 时调用。
    /// </summary>
    public void ClearFault()
    {
        foreach (var a in _axes)
        {
            if (a.State == AxisState.Faulted)
            {
                a.Vel = 0;
                a.Target = null;
                a.State = AxisState.Idle;
            }
        }
    }

    /// <summary>
    /// 启动回零: 朝零点开关方向慢速运动, 碰到 Home 信号后停止并修正到 HomePos。
    /// </summary>
    public void StartHome(int axis)
    {
        var a = _axes[axis];
        if (a.State == AxisState.Faulted) return;
        // 决定回零方向: 如果当前位置 > 零点开关位置, 朝负方向; 否则朝正方向
        a.HomeDir = a.Pos > a.Cfg.HomeSensorPos ? -1 : 1;
        a.State = AxisState.Homing;
        a.Target = null;
        a.Vel = a.HomeDir * a.Cfg.HomeVel;
    }

    public void Tick(double dt)
    {
        foreach (var a in _axes)
        {
            switch (a.State)
            {
                case AxisState.Faulted:
                    // Faulted 不运动
                    a.Vel = 0;
                    continue;

                case AxisState.Homing:
                    TickHoming(a, dt);
                    continue;

                case AxisState.Moving:
                    TickMoving(a, dt);
                    continue;

                case AxisState.Idle:
                default:
                    continue;
            }
        }
    }

    private void TickHoming(AxisSim a, double dt)
    {
        // 检查是否到达零点开关
        var sensors = ReadSensorsForAxis(a);
        if (sensors.Home)
        {
            // 到达零点, 停止并修正位置
            a.Pos = a.Cfg.HomePos;
            a.Vel = 0;
            a.State = AxisState.Idle;
            return;
        }

        // 检查是否撞到极限 (回零方向错误)
        if (a.HomeDir > 0 && sensors.LimitPositive)
        {
            a.Vel = 0;
            a.State = AxisState.Faulted;
            return;
        }
        if (a.HomeDir < 0 && sensors.LimitNegative)
        {
            a.Vel = 0;
            a.State = AxisState.Faulted;
            return;
        }

        // 慢速运动
        a.Pos += a.Vel * dt;
        // 不超过行程
        a.Pos = Math.Clamp(a.Pos, a.Cfg.TravelMin, a.Cfg.TravelMax);
    }

    private void TickMoving(AxisSim a, double dt)
    {
        // 检查传感器 (运动中也要检查极限)
        var sensors = ReadSensorsForAxis(a);
        if (sensors.LimitPositive || sensors.LimitNegative)
        {
            // 撞到极限传感器 -> Faulted
            a.Vel = 0;
            a.Target = null;
            a.State = AxisState.Faulted;
            return;
        }

        if (a.Target is null)
        {
            // 插补直写模式: 减速到停
            double dv0 = AxisSim.Accel * dt;
            a.Vel = Math.Abs(a.Vel) <= dv0 ? 0 : a.Vel - Math.Sign(a.Vel) * dv0;
            if (a.Vel == 0) a.State = AxisState.Idle;
            return;
        }

        // 定位模式: 梯形速度规划
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

    private static SensorState ReadSensorsForAxis(AxisSim a)
    {
        double tol = AxisSim.SensorTolerance;
        bool home = Math.Abs(a.Pos - a.Cfg.HomeSensorPos) < tol;
        bool limPos = Math.Abs(a.Pos - a.Cfg.LimitPositive) < tol;
        bool limNeg = Math.Abs(a.Pos - a.Cfg.LimitNegative) < tol;
        return new SensorState(home, limPos, limNeg);
    }
}
