using System.Runtime.InteropServices;

namespace MotionControl.Core;

/// <summary>
/// 固高 GEN 系列 EtherCAT 轴卡 (部署期) —— P/Invoke 预留骨架。
/// 开发期请使用 <see cref="SimulatedAxisCard"/>; 接入真卡时实现各方法,
/// 函数名对应固高 GTS 运动控制库 (gts.dll), 以实际 SDK 头文件为准。
/// </summary>
public sealed class GtsGenAxisCard : IAxisCard
{
    // —— 固高 GTS 库 P/Invoke 声明示例 (按需启用) ——
    // [DllImport("gts.dll", CallingConvention = CallingConvention.StdCall)]
    // private static extern short GT_Open(int cardNum, int channel);
    // [DllImport("gts.dll", CallingConvention = CallingConvention.StdCall)]
    // private static extern short GT_PrflTrap(short profile);
    // [DllImport("gts.dll", CallingConvention = CallingConvention.StdCall)]
    // private static extern short GT_SetPos(short profile, int pos);

    private const double PulsePerMm = 1000; // 按实际丝杠导程/编码器线数配置

    public void Initialize()
    {
        // TODO: GT_Open → GT_LoadConfig("GTS800.cfg") → GT_ClrSts → GT_AxisOn
        throw new NotSupportedException(
            "真实轴卡接入待实施: 请安装固高 GTS SDK 并补全 P/Invoke 调用, 开发期使用 SimulatedAxisCard。");
    }

    public void SetTarget(int axis, double positionMm, double velocityMmS)
        => throw new NotSupportedException();

    public void SetPosition(int axis, double positionMm, double velocityMmS)
        => throw new NotSupportedException();

    public AxisStatus Read(int axis) => throw new NotSupportedException();

    public void StopAll() { /* TODO: GT_Stop(0x07, 0) */ }

    public void Tick(double dt) { /* EtherCAT 周期由主站栈驱动, 无需软件积分 */ }

    public void Dispose() { /* TODO: GT_AxisOff → GT_Close */ }
}
