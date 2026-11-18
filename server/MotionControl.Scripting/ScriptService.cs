using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using MotionControl.Equipment;
using MotionControl.Hardware.Abstractions;

namespace MotionControl.Scripting;

public enum ScriptScope { Stage, Machine }
public enum ScriptState { Ready, CompileError, Running, Completed, Cancelled, Faulted }

public sealed class ScriptDescriptor : INotifyPropertyChanged
{
    private ScriptState _state;
    private string _error = "";
    internal ScriptDescriptor(string name, string fullPath, ScriptScope scope, string source)
    { Name = name; FullPath = fullPath; Scope = scope; Source = source; }
    public string Name { get; }
    public string FullPath { get; }
    public ScriptScope Scope { get; }
    public string Source { get; internal set; }
    public ScriptState State { get => _state; private set { if (Set(ref _state, value)) OnPropertyChanged(nameof(DisplayState)); } }
    public string Error { get => _error; private set => Set(ref _error, value); }
    public string DisplayState => State == ScriptState.CompileError ? $"CompileError: {Error}" : State.ToString();
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void SetState(ScriptState state, string error = "") { Error = error; State = state; OnPropertyChanged(nameof(DisplayState)); }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; OnPropertyChanged(name); return true; }
    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class ScriptGlobals
{
    private readonly Action<string> _log;
    public ScriptGlobals(ILaserMachineScriptApi machine, Action<string> log, CancellationToken cancellationToken)
    { Machine = machine; _log = log; CancellationToken = cancellationToken; }
    public ILaserMachineScriptApi Machine { get; }
    public CancellationToken CancellationToken { get; }
    public void Log(string message) => _log(message);
    public Task DelayAsync(int milliseconds) => Task.Delay(milliseconds, CancellationToken);
}

public sealed record ScriptRunResult(ScriptState State, string Message);
public sealed class ScriptLogEventArgs(string message) : EventArgs { public string Message { get; } = message; }

/// <summary>发现、编译、运行和停止受信任本地 C# 脚本；设备访问只经过受限 Machine 门面。</summary>
public sealed class ScriptService
{
    private readonly ILaserPlatform _platform;
    private readonly object _sync = new();
    private readonly List<ScriptDescriptor> _scripts = [];
    private readonly Dictionary<string, Script<object>> _compiled = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cancellation;
    private string? _root;

    public ScriptService(ILaserPlatform platform) => _platform = platform;
    public IReadOnlyList<ScriptDescriptor> Scripts => _scripts;
    public ScriptDescriptor? RunningScript { get; private set; }
    public bool IsRunning => RunningScript is not null;
    public event EventHandler? StateChanged;
    public event EventHandler<ScriptLogEventArgs>? LogReceived;

    public void Initialize(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(Path.Combine(_root, "Stage"));
        Directory.CreateDirectory(Path.Combine(_root, "Machine"));
        Reload();
    }

    public bool Reload()
    {
        lock (_sync)
        {
            if (IsRunning || _root is null) return false;
            _scripts.Clear(); _compiled.Clear();
            foreach (string file in Directory.GetFiles(_root, "*.csx", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
            {
                string relative = Path.GetRelativePath(_root, file);
                var descriptor = new ScriptDescriptor(relative, file, relative.StartsWith("Machine", StringComparison.OrdinalIgnoreCase) ? ScriptScope.Machine : ScriptScope.Stage, File.ReadAllText(file));
                _scripts.Add(descriptor);
                try
                {
                    var compiled = Compile(descriptor.Source);
                    _compiled[file] = compiled;
                    descriptor.SetState(ScriptState.Ready);
                }
                catch (Exception ex) { descriptor.SetState(ScriptState.CompileError, ex.Message); }
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
    }

    public async Task SaveAsync(ScriptDescriptor script, string source)
    {
        if (IsRunning) throw new InvalidOperationException("脚本运行期间不能保存");
        string temp = script.FullPath + ".tmp";
        await File.WriteAllTextAsync(temp, source);
        if (File.Exists(script.FullPath)) File.Copy(script.FullPath, script.FullPath + ".bak", true);
        File.Move(temp, script.FullPath, true);
        script.Source = source;
        Reload();
    }

    public async Task<ScriptRunResult> RunAsync(ScriptDescriptor script, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (IsRunning) throw new InvalidOperationException($"已有脚本运行: {RunningScript?.Name}");
            if (!_compiled.TryGetValue(script.FullPath, out _)) throw new InvalidOperationException($"脚本未通过编译: {script.Error}");
            RunningScript = script;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }
        var ct = _cancellation.Token;
        script.SetState(ScriptState.Running);
        StateChanged?.Invoke(this, EventArgs.Empty);
        LogReceived?.Invoke(this, new($"开始运行脚本: {script.Name}"));
        try
        {
            await using var lease = await _platform.Operations.AcquireAsync(MachineOperationSource.Script, ct);
            using var leaseScope = _platform.Operations.Enter(lease);
            using var leaseRegistration = lease.CancellationToken.Register(_cancellation.Cancel);
            var globals = new ScriptGlobals(new LaserMachineScriptApi(_platform), message => LogReceived?.Invoke(this, new(message)), ct);
            await _compiled[script.FullPath].RunAsync(globals, ct);
            script.SetState(ScriptState.Completed);
            return new(ScriptState.Completed, "脚本执行完成");
        }
        catch (OperationCanceledException)
        {
            script.SetState(ScriptState.Cancelled);
            return new(ScriptState.Cancelled, "脚本已取消");
        }
        catch (Exception ex)
        {
            script.SetState(ScriptState.Faulted, ex.Message);
            return new(ScriptState.Faulted, ex.Message);
        }
        finally
        {
            LogReceived?.Invoke(this, new($"脚本结束: {script.Name} · {script.State}"));
            RunningScript = null;
            _cancellation?.Dispose(); _cancellation = null;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task StopAsync()
    {
        var cancellation = _cancellation;
        if (cancellation is null) return;
        cancellation.Cancel();
        await _platform.EmergencyStopAsync();
    }

    private static Script<object> Compile(string source)
    {
        var options = ScriptOptions.Default
            .WithReferences(typeof(object).Assembly, typeof(Task).Assembly, typeof(Enumerable).Assembly,
                typeof(EquipmentResult).Assembly, typeof(ILaserMachineScriptApi).Assembly, typeof(ScriptGlobals).Assembly)
            .WithImports("System", "System.Linq", "System.Threading", "System.Threading.Tasks",
                "MotionControl.Hardware.Abstractions", "MotionControl.Equipment");
        var script = CSharpScript.Create<object>(source, options, typeof(ScriptGlobals));
        var errors = script.Compile().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Select(e => e.ToString())));
        return script;
    }
}
