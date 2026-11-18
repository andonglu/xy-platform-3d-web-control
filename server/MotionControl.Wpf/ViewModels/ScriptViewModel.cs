using System.Collections.ObjectModel;
using System.Windows.Input;
using MotionControl.Scripting;

namespace MotionControl.Wpf.ViewModels;

public sealed class ScriptViewModel : ViewModelBase, IDisposable
{
    private readonly ScriptService _service;
    private readonly SynchronizationContext _ui;
    private ScriptDescriptor? _selectedScript;
    private string _editorText = "", _statusText = "请选择脚本";
    private bool _isRunning;

    public ScriptViewModel(ScriptService service)
    {
        _service = service;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _service.LogReceived += OnLog;
        _service.StateChanged += OnStateChanged;
        ReloadCommand = new RelayCommand(_ => Reload(), _ => !IsRunning);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => SelectedScript is not null && !IsRunning);
        RunCommand = new AsyncRelayCommand(RunAsync, () => SelectedScript is not null && !IsRunning && SelectedScript.State != ScriptState.CompileError);
        StopCommand = new AsyncRelayCommand(_service.StopAsync, () => IsRunning);
        ClearLogCommand = new RelayCommand(_ => Logs.Clear());
        Refresh();
    }

    public ObservableCollection<ScriptDescriptor> Items { get; } = [];
    public ObservableCollection<LogEntry> Logs { get; } = [];
    public ScriptDescriptor? SelectedScript
    {
        get => _selectedScript;
        set
        {
            if (Set(ref _selectedScript, value))
            {
                EditorText = value?.Source ?? "";
                StatusText = value is null ? "请选择脚本" : value.DisplayState;
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
    public string EditorText { get => _editorText; set => Set(ref _editorText, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public bool IsRunning { get => _isRunning; private set { if (Set(ref _isRunning, value)) CommandManager.InvalidateRequerySuggested(); } }
    public ICommand ReloadCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand RunCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ClearLogCommand { get; }

    private void Reload()
    {
        if (!_service.Reload()) { StatusText = "运行期间不能重载"; return; }
        Refresh();
    }

    private async Task SaveAsync()
    {
        if (SelectedScript is null) return;
        try
        {
            string path = SelectedScript.FullPath;
            await _service.SaveAsync(SelectedScript, EditorText);
            Refresh(path);
            StatusText = "保存并编译检查完成";
        }
        catch (Exception ex) { StatusText = ex.Message; Log("error", ex.Message); }
    }

    private async Task RunAsync()
    {
        if (SelectedScript is null) return;
        try
        {
            await SaveAsync();
            string path = SelectedScript.FullPath;
            var script = _service.Scripts.FirstOrDefault(s => s.FullPath == path);
            if (script is null || script.State == ScriptState.CompileError) { StatusText = script?.Error ?? "脚本不存在"; return; }
            var result = await _service.RunAsync(script);
            StatusText = result.Message;
            Log(result.State == ScriptState.Completed ? "ok" : "warn", result.Message);
        }
        catch (Exception ex) { StatusText = ex.Message; Log("error", ex.Message); }
    }

    private void Refresh(string? selectPath = null)
    {
        string? path = selectPath ?? SelectedScript?.FullPath;
        Items.Clear();
        foreach (var script in _service.Scripts) Items.Add(script);
        SelectedScript = Items.FirstOrDefault(s => s.FullPath == path) ?? Items.FirstOrDefault();
    }

    private void OnLog(object? sender, ScriptLogEventArgs e) => _ui.Post(_ => Log("info", e.Message), null);
    private void OnStateChanged(object? sender, EventArgs e) => _ui.Post(_ =>
    {
        IsRunning = _service.IsRunning;
        if (IsRunning) StatusText = $"正在运行: {_service.RunningScript?.Name}";
        else if (SelectedScript is not null) StatusText = SelectedScript.DisplayState;
    }, null);

    private void Log(string level, string message)
    {
        Logs.Insert(0, new LogEntry(DateTime.Now.ToString("HH:mm:ss"), level, message));
        while (Logs.Count > 300) Logs.RemoveAt(Logs.Count - 1);
    }

    public void Dispose()
    {
        _service.LogReceived -= OnLog;
        _service.StateChanged -= OnStateChanged;
    }
}
