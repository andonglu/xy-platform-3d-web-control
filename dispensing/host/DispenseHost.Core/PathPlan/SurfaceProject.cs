using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DispenseHost.CadImport;

namespace DispenseHost.PathPlan;

/// <summary>P7: 曲面工程参数, 保存后可用相同模型与工艺重新生成作业</summary>
public sealed record SurfaceProjectSettings(
    double ZStart, double ZEnd, double LayerHeight, double NormalOffset,
    double Feed, DispenseTiming Timing, ModelTransform Transform,
    double PointSpacing = 0, int NormalSmoothPasses = 0,
    double NormalSmoothStrength = 0.5, double MaxPoseStepDeg = 30);

/// <summary>P9: 人工位姿输入; 旋转顺序固定为 Rx → Ry → Rz</summary>
public sealed record Pose3D(double X = 0, double Y = 0, double Z = 0,
    double Rx = 0, double Ry = 0, double Rz = 0)
{
    public ModelTransform ToTransform(double scale = 1) => new()
    {
        Position = new Point3D(X, Y, Z), RotationDeg = new Point3D(Rx, Ry, Rz), Scale = scale,
    };
}

/// <summary>P9: 原始 STL 资产; 内容随工程保存, 服务端按哈希校验</summary>
public sealed record ModelAsset(string Id, string FileName, string Unit, string Sha256, string Base64)
{
    public static ModelAsset FromFile(string path, string unit = "mm")
    {
        var bytes = File.ReadAllBytes(path);
        string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new ModelAsset(sha[..16], Path.GetFileName(path), unit, sha, Convert.ToBase64String(bytes));
    }

    public byte[] GetBytes() => Convert.FromBase64String(Base64);

    [JsonIgnore]
    public double UnitToMm => Unit.ToLowerInvariant() switch
    {
        "mm" => 1, "cm" => 10, "inch" => 25.4,
        _ => throw new InvalidDataException($"不支持的模型单位: {Unit}"),
    };

    public void ValidateContent()
    {
        var bytes = GetBytes();
        string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (sha != Sha256) throw new InvalidDataException("模型内容 SHA-256 校验失败");
        _ = UnitToMm;
    }
}

/// <summary>P9: 夹具在机器中的安装位置与人工确定的固定/检测方式</summary>
public sealed record FixtureConfig(
    string Name = "A 号定位夹具",
    Pose3D? MachinePose = null,
    string Fixing = "定位销 + 气动压紧",
    string PartSignal = "PART_PRESENT",
    string ClampSignal = "CLAMP_OK",
    bool AutoPartConfirm = true,
    bool AutoClampConfirm = true)
{
    [JsonIgnore]
    public Pose3D Pose => MachinePose ?? new Pose3D();
}

/// <summary>P9: 工件相对夹具的放置</summary>
public sealed record PlacementConfig(Pose3D? RelativePose = null, double Scale = 1, bool FlipNormals = false)
{
    [JsonIgnore]
    public Pose3D Pose => RelativePose ?? new Pose3D();
}

/// <summary>P9: 虚拟上下料配方; 所有位姿均为机器坐标</summary>
public sealed record LoadingRecipe(
    Pose3D? PickPose = null,
    Pose3D? UnloadPose = null,
    double ApproachMm = 30,
    double LiftMm = 50,
    int ActionDelayMs = 300,
    int BatchCount = 1)
{
    [JsonIgnore]
    public Pose3D Pick => PickPose ?? new Pose3D();
    [JsonIgnore]
    public Pose3D Unload => UnloadPose ?? new Pose3D();
}

/// <summary>P9: 完整生产配置; 工艺编辑只在上位机</summary>
public sealed record ProductionConfig(
    string ProjectId, string Name, FixtureConfig Fixture, PlacementConfig Placement,
    LoadingRecipe Loading, bool FullProduction = true)
{
    public static ProductionConfig CreateDefault() => new(
        Guid.NewGuid().ToString("N"), "P9 生产工程", new FixtureConfig(), new PlacementConfig(), new LoadingRecipe());
}

/// <summary>
/// P9: 区域几何来源。Indices 对 Slice/Boundary 为空, 对 Faces 为三角形索引,
/// 对 Edges 为网格唯一边索引; 模型哈希变化后必须重新确认。
/// </summary>
public sealed record RegionSource(string Kind, int[] Indices)
{
    public static RegionSource Slice() => new("Slice", []);
    public static RegionSource Boundary() => new("Boundary", []);
    public static RegionSource Faces(IEnumerable<int> indices) => new("Faces", indices.Distinct().Order().ToArray());
    public static RegionSource Edges(IEnumerable<int> indices) => new("Edges", indices.Distinct().Order().ToArray());
}

/// <summary>P7/P9: 一个曲面区域及其生成后的路径</summary>
public sealed record SurfaceJobItem(
    string Name, double Feed, DispenseTiming Timing, SurfacePath Path,
    RegionSource? Source = null, SurfaceProjectSettings? Settings = null, string Fingerprint = "");

/// <summary>P9: 模型/位姿/区域参数指纹; 加载和生产前据此识别失效路径</summary>
public static class ProductionFingerprint
{
    public static string Compute(ModelAsset? model, ProductionConfig? production,
        RegionSource? source, SurfaceProjectSettings settings)
    {
        var data = new
        {
            Model = model is null ? null : new { model.Id, model.Sha256, model.Unit },
            production?.Fixture.Pose, production?.Placement, source, settings,
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(data))).ToLowerInvariant();
    }

    public static bool IsCurrent(SurfaceProject project, SurfaceJobItem item)
        => project.Production is null && string.IsNullOrEmpty(item.Fingerprint)
           || item.Source is not null && item.Settings is not null
           && item.Fingerprint == Compute(project.Model, project.Production, item.Source, item.Settings);
}

/// <summary>P7/P9: 可重复加载的曲面加工与生产工程</summary>
public sealed record SurfaceProject(
    int Version, string SourceStl, SurfaceProjectSettings Settings, List<SurfaceJobItem> Items,
    ModelAsset? Model = null, ProductionConfig? Production = null);

public static class SurfaceProjectStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static void Save(string filePath, SurfaceProject project)
    {
        Validate(project);
        string full = Path.GetFullPath(filePath);
        string temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(project with { Version = 2 }, JsonOpts));
            File.Move(temp, full, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static SurfaceProject Load(string filePath)
    {
        var project = JsonSerializer.Deserialize<SurfaceProject>(File.ReadAllText(filePath), JsonOpts)
                      ?? throw new InvalidDataException("曲面工程文件为空或格式无效");
        Validate(project);
        return project;
    }

    private static void Validate(SurfaceProject project)
    {
        if (project.Settings is null || project.Items is null)
            throw new InvalidDataException("工程缺少设置或区域列表");
        project.Model?.ValidateContent();
        if (project.Production is not null)
        {
            if (string.IsNullOrWhiteSpace(project.Production.ProjectId))
                throw new InvalidDataException("生产工程 ID 不能为空");
            if (project.Production.Placement.Scale <= 0)
                throw new InvalidDataException("工件缩放必须大于 0");
            if (project.Production.Loading.BatchCount <= 0)
                throw new InvalidDataException("批次数量必须大于 0");
        }
    }
}
