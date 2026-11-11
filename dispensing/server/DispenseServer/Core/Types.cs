namespace DispenseServer.Core;

/// <summary>
/// 点胶服务端轴卡抽象 —— 与激光服务端分离，独立实例。
/// 支持 3D 空间三轴(XYZ)联动。
/// </summary>
public interface IDispenseAxisCard : IDisposable
{
    void Initialize();
    void SetTarget(int axis, double posMm, double velMmS);
    void SetPosition(int axis, double posMm, double velMmS);
    AxisStatus Read(int axis);
    SensorState ReadSensors(int axis);
    void StopAll();
    void ClearFault();
    void Tick(double dt);
    /// <summary>工作空间边界 (各轴 min/max)，取自配置</summary>
    (double Min, double Max) GetLimit(int axis);
    /// <summary>配置轴参数 (行程/零点/传感器位置)</summary>
    void ConfigureAxis(int axis, AxisConfig cfg);
    /// <summary>获取轴配置</summary>
    AxisConfig GetConfig(int axis);
    /// <summary>启动回零 (朝零点开关方向慢速运动)</summary>
    void StartHome(int axis);
}

public enum AxisState { Idle, Moving, Homing, Faulted }

public readonly record struct Point3D(double X, double Y, double Z)
{
    public static Point3D operator +(Point3D a, Point3D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Point3D operator -(Point3D a, Point3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Point3D operator *(Point3D a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
}

public readonly record struct AxisStatus(double Pos, double Vel, AxisState State);

/// <summary>
/// 传感器信号状态（真实轴卡的 IO 信号）。
/// 零点开关、正极限、负极限三个信号，运动到对应位置时信号变化。
/// </summary>
public readonly record struct SensorState(bool Home, bool LimitPositive, bool LimitNegative)
{
    /// <summary>任意极限触发（撞机保护）</summary>
    public bool AnyLimit => LimitPositive || LimitNegative;
}

/// <summary>
/// 轴配置：行程范围、零点位置、正负极限传感器位置。
/// 所有坐标单位 mm，传感器位置必须在行程范围内。
/// </summary>
/// <param name="TravelMin">行程下限（机械最小位置）</param>
/// <param name="TravelMax">行程上限（机械最大位置）</param>
/// <param name="HomePos">零点位置（回零后停靠的位置）</param>
/// <param name="LimitNegative">负极限传感器位置（碰到此位置 LimitNegative 信号 = true）</param>
/// <param name="LimitPositive">正极限传感器位置（碰到此位置 LimitPositive 信号 = true）</param>
/// <param name="HomeSensorPos">零点开关位置（回零时碰到此位置 Home 信号 = true）</param>
/// <param name="HomeVel">回零速度 mm/s</param>
public sealed record AxisConfig(
    double TravelMin,
    double TravelMax,
    double HomePos,
    double LimitNegative,
    double LimitPositive,
    double HomeSensorPos,
    double HomeVel = 20);

public static class Axes
{
    public const int X = 0, Y = 1, Z = 2;
    public static readonly string[] Names = ["x", "y", "z"];
    public static int FromName(string name) => name.ToLowerInvariant() switch
    {
        "x" => X, "y" => Y, "z" => Z, _ => -1,
    };
}
