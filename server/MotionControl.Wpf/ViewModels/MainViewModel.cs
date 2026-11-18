using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using MotionControl.Equipment;
using MotionControl.Hardware.Abstractions;
using MotionControl.Scripting;

namespace MotionControl.Wpf.ViewModels;

public sealed class AxisStatusViewModel(string name) : ViewModelBase
{
    private string _position = "0.00", _velocity = "0.0", _state = "Idle";
    public string Name { get; } = name;
    public string Position { get => _position; private set => Set(ref _position, value); }
    public string Velocity { get => _velocity; private set => Set(ref _velocity, value); }
    public string State { get => _state; private set => Set(ref _state, value); }
    public void Update(AxisEquipmentSnapshot axis)
    { Position = axis.Position.ToString("F2"); Velocity = axis.Velocity.ToString("F1"); State = axis.State; }
}

public sealed record RecipeOption(string Value, string Name);
public sealed record LogEntry(string Time, string Level, string Message);

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly ILaserPlatform _platform;
    private readonly SynchronizationContext _ui;
    private bool _isConnected, _laserOn, _productionRunning, _faulted;
    private string _serverUrl = "ws://127.0.0.1:8080/ws";
    private string _connectionStatus = "未连接", _connectButtonText = "连接";
    private string _mode = "Idle", _phase = "Idle", _workpieceId = "", _recipe = "star";
    private int _completed, _targetCount = 1;
    private double _progress;
    private string _ptpAxis = "x", _ptpPosition = "0", _selectedRecipe = "star", _productionCount = "1";

    public MainViewModel(ILaserPlatform platform, ScriptService scriptService)
    {
        _platform = platform;
        _serverUrl = platform.Endpoint;
        Scripts = new ScriptViewModel(scriptService);
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _platform.ConnectionChanged += (_, e) => _ui.Post(_ => ApplyConnection(e.State == EquipmentConnectionState.Connected, e.Message), null);
        _platform.SnapshotChanged += (_, e) => _ui.Post(_ => UpdateSnapshot(e.Snapshot), null);
        _platform.EventReceived += (_, e) => _ui.Post(_ => Log(e.Level, e.Message), null);

        ConnectCommand = new AsyncRelayCommand(ToggleConnection);
        HomeCommand = new AsyncRelayCommand(() => SendAsync(() => _platform.HomeAsync()), () => IsConnected && !ProductionRunning);
        EstopCommand = new AsyncRelayCommand(() => SendAsync(() => _platform.EmergencyStopAsync()), () => IsConnected);
        PtpCommand = new AsyncRelayCommand(SendPtp, () => IsConnected && !ProductionRunning);
        LaserCommand = new AsyncRelayCommand(() => SendAsync(() => _platform.SetLaserAsync(!LaserOn)), () => IsConnected && !ProductionRunning);
        JogCommand = new RelayCommand(p => SendJog(p, true), _ => IsConnected && !ProductionRunning);
        StopJogCommand = new RelayCommand(p => SendJog(p, false), _ => IsConnected && !ProductionRunning);
        JobCommand = new RelayCommand(p => _ = SendAsync(() => _platform.RunRecipeAsync(p as string ?? "star")), _ => IsConnected && !ProductionRunning);
        StartSingleCommand = new AsyncRelayCommand(StartSingle, () => IsConnected && !ProductionRunning && !Faulted);
        StartContinuousCommand = new AsyncRelayCommand(
            () => SendAsync(() => _platform.StartProductionAsync(SelectedRecipe, 0)),
            () => IsConnected && !ProductionRunning && !Faulted);
        StopProductionCommand = new AsyncRelayCommand(() => SendAsync(() => _platform.StopProductionAsync()), () => IsConnected && ProductionRunning);
        ResetProductionCommand = new AsyncRelayCommand(() => SendAsync(() => _platform.ResetProductionAsync()), () => IsConnected && Faulted);
        OpenBrowserCommand = new RelayCommand(_ => OpenBrowser());
        ClearLogCommand = new RelayCommand(_ => LogEntries.Clear());
    }

    public ScriptViewModel Scripts { get; }
    public ObservableCollection<AxisStatusViewModel> Axes { get; } = [new("X"), new("Y"), new("Z")];
    public ObservableCollection<LogEntry> LogEntries { get; } = [];
    public IReadOnlyList<RecipeOption> Recipes { get; } = [new("star", "五角星 · 直线"), new("circle", "整圆 · 圆弧"), new("combo", "综合图案")];
    public IReadOnlyList<string> AxisNames { get; } = ["x", "y", "z"];
    public string ServerUrl { get => _serverUrl; set => Set(ref _serverUrl, value); }
    public string ConnectionStatus { get => _connectionStatus; private set => Set(ref _connectionStatus, value); }
    public string ConnectButtonText { get => _connectButtonText; private set => Set(ref _connectButtonText, value); }
    public bool IsConnected { get => _isConnected; private set { if (Set(ref _isConnected, value)) InvalidateCommands(); } }
    public string Mode { get => _mode; private set => Set(ref _mode, value); }
    public bool LaserOn { get => _laserOn; private set { if (Set(ref _laserOn, value)) OnPropertyChanged(nameof(LaserButtonText)); } }
    public string LaserButtonText => LaserOn ? "关闭激光" : "开启激光";
    public string PtpAxis { get => _ptpAxis; set => Set(ref _ptpAxis, value); }
    public string PtpPosition { get => _ptpPosition; set => Set(ref _ptpPosition, value); }
    public string SelectedRecipe { get => _selectedRecipe; set => Set(ref _selectedRecipe, value); }
    public string ProductionCount { get => _productionCount; set => Set(ref _productionCount, value); }
    public string PhaseText => PhaseNames.GetValueOrDefault(_phase, _phase);
    public string WorkpieceInfo => string.IsNullOrEmpty(_workpieceId) ? "暂无工件" : $"{_workpieceId} · {RecipeName(_recipe)}";
    public string CompletedText => $"{_completed} 件";
    public double Progress { get => _progress; private set { if (Set(ref _progress, value)) OnPropertyChanged(nameof(ProgressText)); } }
    public string ProgressText => $"{Progress:F0}%";
    public bool ProductionRunning { get => _productionRunning; private set { if (Set(ref _productionRunning, value)) InvalidateCommands(); } }
    public bool Faulted { get => _faulted; private set { if (Set(ref _faulted, value)) InvalidateCommands(); } }
    public string ProductionDetail => Faulted ? "急停 / 等待复位" : ProductionRunning
        ? (_targetCount == 0 ? "连续生产" : $"目标 {_targetCount} 件") : (_phase == "Complete" ? "成品已下料" : "等待启动");

    public ICommand ConnectCommand { get; }
    public ICommand HomeCommand { get; }
    public ICommand EstopCommand { get; }
    public ICommand PtpCommand { get; }
    public ICommand LaserCommand { get; }
    public ICommand JogCommand { get; }
    public ICommand StopJogCommand { get; }
    public ICommand JobCommand { get; }
    public ICommand StartSingleCommand { get; }
    public ICommand StartContinuousCommand { get; }
    public ICommand StopProductionCommand { get; }
    public ICommand ResetProductionCommand { get; }
    public ICommand OpenBrowserCommand { get; }
    public ICommand ClearLogCommand { get; }

    public async Task InitializeAsync() { if (!IsConnected) await ToggleConnection(); }

    private async Task ToggleConnection()
    {
        if (_platform.ConnectionState == EquipmentConnectionState.Connected) { await _platform.DisconnectAsync(); return; }
        ConnectionStatus = "连接中…";
        _platform.Endpoint = ServerUrl;
        var result = await _platform.ConnectAsync();
        if (!result.Succeeded) { ConnectionStatus = "连接失败"; Log("error", result.Message); }
    }

    private void ApplyConnection(bool connected, string message)
    {
        IsConnected = connected;
        ConnectionStatus = connected ? $"已连接 · {_platform.ChannelName}" : "未连接";
        ConnectButtonText = connected ? "断开" : "连接";
        if (!connected) Log("warn", message);
    }

    private async Task SendPtp()
    {
        if (!double.TryParse(PtpPosition, NumberStyles.Float, CultureInfo.CurrentCulture, out double pos) &&
            !double.TryParse(PtpPosition, NumberStyles.Float, CultureInfo.InvariantCulture, out pos))
        { Log("error", "PTP 位置必须是数字"); return; }
        await SendAsync(() => _platform.MoveAxisAsync(PtpAxis, pos));
    }

    private void SendJog(object? parameter, bool start)
    {
        if (parameter is not string text) return;
        var parts = text.Split(',');
        if (parts.Length != 2) return;
        _ = SendAsync(() => _platform.JogAsync(parts[0], start ? int.Parse(parts[1]) : 0));
    }

    private async Task StartSingle()
    {
        if (!int.TryParse(ProductionCount, out int count) || count < 1 || count > 999)
        { Log("error", "生产数量必须是 1~999 的整数"); return; }
        await SendAsync(() => _platform.StartProductionAsync(SelectedRecipe, count));
    }

    private async Task SendAsync(Func<Task<EquipmentResult>> operation)
    {
        try { var result = await operation(); if (!result.Succeeded) Log("error", result.Message); }
        catch (Exception ex) { Log("error", ex.Message); }
    }

    private void UpdateSnapshot(EquipmentSnapshot snapshot)
    {
        for (int i = 0; i < 3; i++) if (snapshot.Axes.TryGetValue(AxisNames[i], out var axis)) Axes[i].Update(axis);
        Mode = snapshot.Mode;
        LaserOn = snapshot.Laser;
        var p = snapshot.Production;
        _phase = p.Phase; _workpieceId = p.WorkpieceId ?? ""; _recipe = p.Recipe;
        _completed = p.Completed; _targetCount = p.TargetCount;
        ProductionRunning = p.Running; Faulted = p.Phase == "Faulted"; Progress = p.PhaseProgress * 100;
        OnPropertyChanged(nameof(PhaseText)); OnPropertyChanged(nameof(WorkpieceInfo));
        OnPropertyChanged(nameof(CompletedText)); OnPropertyChanged(nameof(ProductionDetail));
    }

    private void OpenBrowser()
    {
        try
        {
            string url = ServerUrl.Trim();
            if (!url.StartsWith("ws://") && !url.StartsWith("wss://")) { Log("error", "WebSocket 地址必须以 ws:// 或 wss:// 开头"); return; }
            url = url.StartsWith("wss://") ? "https://" + url[6..] : "http://" + url[5..];
            if (url.EndsWith("/ws")) url = url[..^3];
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) { Log("error", $"无法打开 3D 页面: {ex.Message}"); }
    }

    private void Log(string level, string message)
    {
        LogEntries.Insert(0, new LogEntry(DateTime.Now.ToString("HH:mm:ss"), level, message));
        while (LogEntries.Count > 200) LogEntries.RemoveAt(LogEntries.Count - 1);
    }

    private static string RecipeName(string value) => value switch { "circle" => "整圆", "combo" => "综合图案", _ => "五角星" };
    private static void InvalidateCommands() => CommandManager.InvalidateRequerySuggested();
    private static readonly Dictionary<string, string> PhaseNames = new()
    {
        ["Idle"] = "空闲", ["Feeding"] = "料仓出料", ["Loading"] = "推料上料", ["Clamping"] = "定位夹紧",
        ["Processing"] = "激光加工", ["Unclamping"] = "松夹", ["Unloading"] = "推料下料",
        ["Retracting"] = "推杆回程", ["Complete"] = "生产完成", ["Faulted"] = "故障停机",
    };

    public void Dispose()
    {
        Scripts.Dispose();
        _platform.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
