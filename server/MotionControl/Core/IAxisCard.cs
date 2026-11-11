namespace MotionControl.Core;

/// <summary>
/// 轴卡抽象: 运动控制器只面向本接口, 便于在 模拟轴卡 / 固高 GEN EtherCAT 之间切换。
/// </summary>
public interface IAxisCard : IDisposable
{
    void Initialize();

    /// <summary>下达单轴定位目标 (梯形速度规划由卡内完成)</summary>
    void SetTarget(int axis, double positionMm, double velocityMmS);

    /// <summary>直接设定轴位置 (插补模式下由控制器逐周期写入)</summary>
    void SetPosition(int axis, double positionMm, double velocityMmS);

    AxisStatus Read(int axis);

    /// <summary>急停: 全部轴立即停</summary>
    void StopAll();

    /// <summary>周期推进 (模拟卡内部积分; 真实 EtherCAT 卡内由固件完成, 空操作)</summary>
    void Tick(double dt);
}
