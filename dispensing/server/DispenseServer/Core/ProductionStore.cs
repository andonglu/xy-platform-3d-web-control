using System.Security.Cryptography;
using System.Text.Json;

namespace DispenseServer.Core;

/// <summary>
/// P9 原始模型与生产配方仓库。模型按原始字节保存，配方保存完整路径步骤；
/// 所有提交均采用临时文件原子替换，重启只恢复数据，不恢复生产状态。
/// </summary>
public sealed class ProductionStore
{
    public const int ChunkSize = 48 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private sealed class PendingModel
    {
        public required ModelBeginDto Begin { get; init; }
        public required byte[]?[] Chunks { get; init; }
    }

    private readonly string _dir;
    private readonly string _modelDir;
    private readonly string _recipeFile;
    private readonly object _sync = new();
    private readonly Dictionary<string, PendingModel> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StoredModel> _models = new(StringComparer.OrdinalIgnoreCase);

    public ProductionStore(string directory)
    {
        _dir = directory;
        _modelDir = Path.Combine(directory, "models");
        _recipeFile = Path.Combine(directory, "recipe.json");
        Directory.CreateDirectory(_modelDir);
        Reload();
    }

    public ProductionEnvelope? CurrentRecipe { get; private set; }
    public IReadOnlyList<string> LoadWarnings { get; private set; } = [];

    public IReadOnlyList<StoredModel> ListModels()
    {
        lock (_sync) return _models.Values.OrderBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public StoredModel? GetModel(string modelId)
    {
        lock (_sync) return _models.TryGetValue(modelId, out var model) ? model : null;
    }

    public byte[] ReadModel(string modelId)
    {
        var model = GetModel(modelId) ?? throw new FileNotFoundException($"模型 {modelId} 不存在");
        string path = ModelPath(model.ModelId);
        var bytes = File.ReadAllBytes(path);
        if (bytes.LongLength != model.TotalBytes ||
            !Hash(bytes).Equals(model.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"模型 {modelId} 的磁盘内容校验失败");
        return bytes;
    }

    /// <summary>开始或重新开始一个模型上传；同 ID 的未完成旧分片会被明确替换。</summary>
    public ModelBeginDto BeginModel(ModelBeginDto begin)
    {
        ValidateModelBegin(begin);
        lock (_sync)
            _pending[begin.ModelId] = new PendingModel
            {
                Begin = begin with { Sha256 = begin.Sha256.ToLowerInvariant() },
                Chunks = new byte[begin.TotalChunks][],
            };
        return begin;
    }

    /// <summary>接收原始 48KiB 分片；重复发送相同分片是幂等操作，内容不同则拒绝。</summary>
    public void AddModelChunk(ModelChunkDto chunk)
    {
        PendingModel pending;
        lock (_sync)
        {
            if (!_pending.TryGetValue(chunk.ModelId, out pending!))
                throw new InvalidOperationException($"模型 {chunk.ModelId} 尚未开始上传");

            if (chunk.Index < 0 || chunk.Index >= pending.Begin.TotalChunks)
                throw new InvalidDataException($"分片索引 {chunk.Index} 超出 0~{pending.Begin.TotalChunks - 1}");

            byte[] bytes;
            try { bytes = Convert.FromBase64String(chunk.Data ?? ""); }
            catch (FormatException ex) { throw new InvalidDataException("模型分片不是有效 Base64", ex); }

            long offset = (long)chunk.Index * ChunkSize;
            int expected = (int)Math.Min(ChunkSize, pending.Begin.TotalBytes - offset);
            if (bytes.Length != expected)
                throw new InvalidDataException($"分片 {chunk.Index} 长度应为 {expected} 字节，实际 {bytes.Length} 字节");

            var old = pending.Chunks[chunk.Index];
            if (old is not null && !old.AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException($"分片 {chunk.Index} 已存在且内容不同，请重新执行 model_begin");
            pending.Chunks[chunk.Index] = bytes;
        }
    }

    /// <summary>缺片、总长度或 SHA-256 不匹配均不会改动已持久化模型。</summary>
    public StoredModel CommitModel(string modelId)
    {
        PendingModel pending;
        byte[] content;
        lock (_sync)
        {
            if (!_pending.TryGetValue(modelId, out pending!))
                throw new InvalidOperationException($"模型 {modelId} 尚未开始上传");
            int missing = Array.FindIndex(pending.Chunks, c => c is null);
            if (missing >= 0) throw new InvalidDataException($"模型缺少分片 {missing}");

            content = new byte[pending.Begin.TotalBytes];
            for (int i = 0; i < pending.Chunks.Length; i++)
                pending.Chunks[i]!.CopyTo(content, (long)i * ChunkSize);
            string actualHash = Hash(content);
            if (!actualHash.Equals(pending.Begin.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"模型 SHA-256 不匹配，期望 {pending.Begin.Sha256}，实际 {actualHash}");

            var stored = new StoredModel(pending.Begin.ModelId, pending.Begin.FileName,
                pending.Begin.Unit, actualHash, content.LongLength, pending.Begin.TotalChunks, DateTimeOffset.Now);
            AtomicWriteBytes(ModelPath(modelId), content);
            AtomicWriteText(MetadataPath(modelId), JsonSerializer.Serialize(stored, JsonOpts));
            _models[modelId] = stored;
            _pending.Remove(modelId);
            return stored;
        }
    }

    public void SaveRecipe(ProductionEnvelope envelope)
    {
        ValidateRecipe(envelope);
        lock (_sync)
        {
            if (!_models.ContainsKey(envelope.ModelId))
                throw new InvalidOperationException($"配方引用的模型 {envelope.ModelId} 尚未同步");
            AtomicWriteText(_recipeFile, JsonSerializer.Serialize(envelope, JsonOpts));
            CurrentRecipe = envelope;
        }
    }

    public static void ValidateRecipe(ProductionEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (string.IsNullOrWhiteSpace(envelope.ProjectId)) throw new InvalidDataException("工程 ID 不能为空");
        if (envelope.Revision < 0) throw new InvalidDataException("工程修订号不能为负数");
        if (string.IsNullOrWhiteSpace(envelope.ModelId)) throw new InvalidDataException("模型 ID 不能为空");
        var config = envelope.Config ?? throw new InvalidDataException("缺少生产配置");
        if (!string.Equals(config.ProjectId, envelope.ProjectId, StringComparison.Ordinal))
            throw new InvalidDataException("配置内工程 ID 与信封不一致");
        if (string.IsNullOrWhiteSpace(config.Name)) throw new InvalidDataException("工程名称不能为空");
        if (config.Fixture is null || config.Placement is null || config.Loading is null)
            throw new InvalidDataException("夹具、工件放置和上下料配置不能为空");
        if (string.IsNullOrWhiteSpace(config.Fixture.Name) || string.IsNullOrWhiteSpace(config.Fixture.Fixing))
            throw new InvalidDataException("夹具名称和机械固定方式不能为空");
        if (string.IsNullOrWhiteSpace(config.Fixture.PartSignal) || string.IsNullOrWhiteSpace(config.Fixture.ClampSignal))
            throw new InvalidDataException("到位与夹紧信号名称不能为空");
        if (!double.IsFinite(config.Placement.Scale) || config.Placement.Scale <= 0)
            throw new InvalidDataException("工件缩放必须大于 0");
        if (config.Loading.BatchCount <= 0) throw new InvalidDataException("批次数量必须大于 0");
        if (config.Loading.ActionDelayMs < 0) throw new InvalidDataException("动作延时不能为负数");
        if (!double.IsFinite(config.Loading.ApproachMm) || config.Loading.ApproachMm < 0 ||
            !double.IsFinite(config.Loading.LiftMm) || config.Loading.LiftMm < 0)
            throw new InvalidDataException("接近与抬升高度不能为负数");
        ValidatePose(config.Fixture.Pose, "夹具位姿");
        ValidatePose(config.Placement.Pose, "工件位姿");
        ValidatePose(config.Loading.Pick, "抓取位姿");
        ValidatePose(config.Loading.Unload, "下料位姿");
        if (envelope.Steps is null || JobBuilder.BuildPaths(envelope.Steps).Count == 0)
            throw new InvalidDataException("生产工程不包含有效点胶路径");
    }

    private void Reload()
    {
        var warnings = new List<string>();
        _models.Clear();
        _pending.Clear();
        CurrentRecipe = null;
        foreach (string file in Directory.GetFiles(_modelDir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var model = JsonSerializer.Deserialize<StoredModel>(File.ReadAllText(file), JsonOpts)
                            ?? throw new InvalidDataException("模型元数据为空");
                ValidateModelBegin(new ModelBeginDto(model.ModelId, model.FileName, model.Unit,
                    model.Sha256, model.TotalBytes, model.TotalChunks));
                _ = ReadModelDirect(model);
                _models[model.ModelId] = model;
            }
            catch (Exception ex) { warnings.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
        }

        if (File.Exists(_recipeFile))
        {
            try
            {
                var recipe = JsonSerializer.Deserialize<ProductionEnvelope>(File.ReadAllText(_recipeFile), JsonOpts)
                             ?? throw new InvalidDataException("生产配方为空");
                ValidateRecipe(recipe);
                if (!_models.ContainsKey(recipe.ModelId))
                    throw new InvalidDataException($"引用模型 {recipe.ModelId} 不存在或已损坏");
                CurrentRecipe = recipe;
            }
            catch (Exception ex) { warnings.Add($"recipe.json: {ex.Message}"); }
        }
        LoadWarnings = warnings;
    }

    private StoredModel ReadModelDirect(StoredModel model)
    {
        var bytes = File.ReadAllBytes(ModelPath(model.ModelId));
        if (bytes.LongLength != model.TotalBytes || !Hash(bytes).Equals(model.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("模型字节数或 SHA-256 校验失败");
        return model;
    }

    private static void ValidateModelBegin(ModelBeginDto begin)
    {
        ArgumentNullException.ThrowIfNull(begin);
        if (string.IsNullOrWhiteSpace(begin.ModelId) || begin.ModelId.Length > 96 ||
            begin.ModelId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new InvalidDataException("模型 ID 只能包含字母、数字、连字符和下划线，且长度不能超过 96");
        if (string.IsNullOrWhiteSpace(begin.FileName) || begin.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("模型文件名无效");
        if (!new[] { "mm", "cm", "inch" }.Contains(begin.Unit, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"不支持的模型单位: {begin.Unit}");
        if (begin.Sha256 is null || begin.Sha256.Length != 64 || begin.Sha256.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("模型 SHA-256 必须是 64 位十六进制字符串");
        if (begin.TotalBytes <= 0 || begin.TotalBytes > int.MaxValue)
            throw new InvalidDataException("模型字节数必须大于 0 且不能超过内存验证上限");
        int chunks = checked((int)((begin.TotalBytes + ChunkSize - 1) / ChunkSize));
        if (begin.TotalChunks != chunks)
            throw new InvalidDataException($"总分片数应为 {chunks}，实际 {begin.TotalChunks}");
    }

    private static void ValidatePose(Pose3D pose, string name)
    {
        if (new[] { pose.X, pose.Y, pose.Z, pose.Rx, pose.Ry, pose.Rz }.Any(v => !double.IsFinite(v)))
            throw new InvalidDataException($"{name}包含非有限数值");
    }

    private string ModelPath(string id) => Path.Combine(_modelDir, id + ".stl");
    private string MetadataPath(string id) => Path.Combine(_modelDir, id + ".json");

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void AtomicWriteBytes(string path, byte[] bytes)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void AtomicWriteText(string path, string text)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, text); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
