using System.Text.Json;

namespace DispenseServer.Core;

/// <summary>存储的加工程序 (脱机文件系统的一个"加工文件")</summary>
public sealed record StoredProgram(
    int Id,
    string Name,
    DateTimeOffset SavedUtc,
    List<JobStepDto> Steps)
{
    public int StepCount => Steps.Count;
    public int PointCount => Steps.Sum(s => s.Points?.Length ?? 0);
}

/// <summary>
/// 脱机程序仓库 —— 模拟真实点胶控制器的"加工文件"存储:
///   程序保存在服务端 data/programs/*.json, 脱机可执行;
///   编号自动递增 (相当于机台拨码/文件号), 经 IO 或 program_run 调用。
/// 保存为原子写 (tmp → bak → move), 启动时全量加载建索引。
/// </summary>
public sealed class ProgramStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _dir;
    private readonly Dictionary<int, StoredProgram> _programs = new();
    private readonly List<string> _loadWarnings = new();
    private int _nextId = 1;

    public ProgramStore(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(_dir);
        Reload();
    }

    public IReadOnlyList<string> LoadWarnings => _loadWarnings;
    public string DirectoryPath => _dir;

    public IReadOnlyList<StoredProgram> List()
        => _programs.Values.OrderBy(p => p.Id).ToList();

    public StoredProgram? Get(int id)
        => _programs.TryGetValue(id, out var p) ? p : null;

    public StoredProgram Save(string name, List<JobStepDto> steps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(steps);
        if (JobBuilder.BuildPaths(steps).Count == 0)
            throw new InvalidOperationException("程序不包含任何有效点胶路径 (需至少一条含 2 点以上的 dispense 步骤)");

        var program = new StoredProgram(_nextId++, name.Trim(), DateTimeOffset.Now, steps);
        _programs[program.Id] = program;
        WriteFile(program);
        return program;
    }

    public bool Delete(int id)
    {
        if (!_programs.Remove(id, out _)) return false;
        string file = FileFor(id);
        if (File.Exists(file))
        {
            try { File.Move(file, file + ".deleted", true); }
            catch { /* 删除失败不阻断内存索引 */ }
        }
        return true;
    }

    /// <summary>重建索引 (启动加载; 支持外部投放程序文件后刷新)</summary>
    public void Reload()
    {
        _programs.Clear();
        _loadWarnings.Clear();
        _nextId = 1;
        foreach (var file in System.IO.Directory.GetFiles(_dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var p = JsonSerializer.Deserialize<StoredProgram>(File.ReadAllText(file), JsonOpts);
                if (p is null || p.Id <= 0 || p.Steps.Count == 0) throw new FormatException("空程序或非法编号");
                _programs[p.Id] = p;
                _nextId = Math.Max(_nextId, p.Id + 1);
            }
            catch (Exception ex)
            {
                _loadWarnings.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }

    private string FileFor(int id) => Path.Combine(_dir, $"{id:D4}.json");

    private void WriteFile(StoredProgram program)
    {
        string file = FileFor(program.Id);
        string tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(program, JsonOpts));
        if (File.Exists(file)) File.Copy(file, file + ".bak", overwrite: true);
        File.Move(tmp, file, overwrite: true);
    }
}
