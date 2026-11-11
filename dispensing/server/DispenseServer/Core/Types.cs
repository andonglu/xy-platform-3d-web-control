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
    void StopAll();
    void Tick(double dt);
    /// <summary>工作空间边界 (各轴 min/max)，用于撞机检测</summary>
    (double Min, double Max) GetLimit(int axis);
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

public static class Axes
{
    public const int X = 0, Y = 1, Z = 2;
    public static readonly string[] Names = ["x", "y", "z"];
    public static int FromName(string name) => name.ToLowerInvariant() switch
    {
        "x" => X, "y" => Y, "z" => Z, _ => -1,
    };
}
