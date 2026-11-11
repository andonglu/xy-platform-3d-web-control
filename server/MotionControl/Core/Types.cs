namespace MotionControl.Core;

public enum AxisState { Idle, Moving, Homing, Faulted }

public readonly record struct Point2D(double X, double Y);
public readonly record struct Point3D(double X, double Y, double Z);

public readonly record struct AxisStatus(double Pos, double Vel, AxisState State);

/// <summary>轴序号: 0=X, 1=Y, 2=Z</summary>
public static class Axes
{
    public const int X = 0, Y = 1, Z = 2;
    public static readonly string[] Names = ["x", "y", "z"];
    public static int FromName(string name) => name.ToLowerInvariant() switch
    {
        "x" => X, "y" => Y, "z" => Z, _ => -1,
    };
}
