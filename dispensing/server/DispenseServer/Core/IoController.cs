namespace DispenseServer.Core;

/// <summary>虚拟 IO 状态 (输入=外部信号注入, 输出=控制器状态回显, 全部可在状态包中观察)</summary>
public sealed record IoState(
    bool Start, bool Pause, bool Reset, int SelectBcd,
    bool Running, bool Done, bool Error, bool Valve);

/// <summary>
/// 脱机点胶机的面板/PLC IO 模拟 —— 与真实机台一致的行为闭环:
///   输入: start(上升沿运行 BCD 选定程序) / pause(停止, 急停语义) / reset(复位报警)
///         sel0..sel3 (程序号 BCD 拨码, 0=未选择)
///   输出: running(运行中) / done(完成 1s 脉冲) / error(报警) / valve(胶阀回显)
/// 输入由 io_write 指令注入 (模拟外部按钮/PLC), 自动生产运行中由控制器接管时拒绝手动写入。
/// </summary>
public sealed class IoController
{
    public static readonly IReadOnlyDictionary<string, string> InputDefs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["start"] = "启动 (上升沿运行 BCD 选定程序)",
        ["pause"] = "停止 (急停语义)",
        ["reset"] = "复位报警",
        ["sel0"] = "程序号 BCD bit0",
        ["sel1"] = "程序号 BCD bit1",
        ["sel2"] = "程序号 BCD bit2",
        ["sel3"] = "程序号 BCD bit3",
    };

    public static readonly IReadOnlyDictionary<string, string> OutputDefs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["running"] = "运行中",
        ["done"] = "加工完成 (1s 脉冲)",
        ["error"] = "报警",
        ["valve"] = "胶阀 (回显)",
    };

    private readonly ProgramStore _store;
    private readonly DispenseController _ctl;
    private bool _prevStart, _prevPause, _prevReset;
    private double _donePulse;

    public IoController(ProgramStore store, DispenseController controller)
    {
        _store = store;
        _ctl = controller;
    }

    // ---- 输入点 (外部注入) ----
    public bool InStart { get; private set; }
    public bool InPause { get; private set; }
    public bool InReset { get; private set; }
    public bool InSel0 { get; private set; }
    public bool InSel1 { get; private set; }
    public bool InSel2 { get; private set; }
    public bool InSel3 { get; private set; }
    public int SelectBcd => (InSel0 ? 1 : 0) | (InSel1 ? 2 : 0) | (InSel2 ? 4 : 0) | (InSel3 ? 8 : 0);

    /// <summary>完成脉冲剩余时间 (秒), 供验证/状态读取</summary>
    public double DonePulseRemain => _donePulse;

    /// <summary>写输入点; 返回错误消息或 null</summary>
    public string? SetInput(string id, bool on)
    {
        switch (id.ToLowerInvariant())
        {
            case "start": InStart = on; return null;
            case "pause": InPause = on; return null;
            case "reset": InReset = on; return null;
            case "sel0": InSel0 = on; return null;
            case "sel1": InSel1 = on; return null;
            case "sel2": InSel2 = on; return null;
            case "sel3": InSel3 = on; return null;
            default: return $"未知输入点: {id}（可用: {string.Join(", ", InputDefs.Keys)}）";
        }
    }

    /// <summary>作业完成通知 → done 输出 1s 脉冲</summary>
    public void NotifyJobDone() => _donePulse = 1.0;

    public void Tick(double dt)
    {
        // 上升沿检测 (按钮/PLC 信号语义)
        if (InStart && !_prevStart) TryStartSelected();
        if (InPause && !_prevPause) _ctl.EmergencyStop();
        if (InReset && !_prevReset) _ctl.ResetAlarm();
        _prevStart = InStart; _prevPause = InPause; _prevReset = InReset;

        if (_donePulse > 0) _donePulse = Math.Max(0, _donePulse - dt);
    }

    private void TryStartSelected()
    {
        if (_ctl.IsRunning) { _ctl.ReportEvent("warn", "IO 启动被拒绝: 已有作业在运行"); return; }
        if (SelectBcd == 0) { _ctl.ReportEvent("error", "IO 启动失败: BCD 程序号为 0 (未选择)"); return; }
        var program = _store.Get(SelectBcd);
        if (program is null) { _ctl.ReportEvent("error", $"IO 启动失败: 程序 {SelectBcd} 不存在"); return; }

        var paths = JobBuilder.BuildPaths(program.Steps);
        if (paths.Count == 0) { _ctl.ReportEvent("error", $"IO 启动失败: 程序 {SelectBcd} 无有效路径"); return; }
        _ctl.UploadPaths(paths, PathSampler3D.Sample(paths.SelectMany(p => p.Segments)), program.Name);
        _ctl.RunJob();
        _ctl.ReportEvent("ok", $"IO 启动程序 {program.Id}: {program.Name}");
    }

    public IoState State() => new(
        InStart, InPause, InReset, SelectBcd,
        _ctl.IsRunning, _donePulse > 0, _ctl.CollisionAlarm || _ctl.AnyAxisFaulted, _ctl.ValveOn);
}
