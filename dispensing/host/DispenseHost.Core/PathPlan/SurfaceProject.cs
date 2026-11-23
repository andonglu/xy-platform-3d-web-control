using System.Text.Json;
using DispenseHost.CadImport;

namespace DispenseHost.PathPlan;

/// <summary>P7: 曲面工程参数, 保存后可用相同模型与工艺重新生成作业</summary>
public sealed record SurfaceProjectSettings(
    double ZStart, double ZEnd, double LayerHeight, double NormalOffset,
    double Feed, DispenseTiming Timing, ModelTransform Transform);

/// <summary>P7: 已加入作业的一条曲面路径</summary>
public sealed record SurfaceJobItem(string Name, double Feed, DispenseTiming Timing, SurfacePath Path);

/// <summary>P7: 可重复加载的曲面加工工程</summary>
public sealed record SurfaceProject(
    int Version, string SourceStl, SurfaceProjectSettings Settings, List<SurfaceJobItem> Items);

public static class SurfaceProjectStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static void Save(string filePath, SurfaceProject project)
        => File.WriteAllText(filePath, JsonSerializer.Serialize(project, JsonOpts));

    public static SurfaceProject Load(string filePath)
        => JsonSerializer.Deserialize<SurfaceProject>(File.ReadAllText(filePath), JsonOpts)
           ?? throw new InvalidDataException("曲面工程文件为空或格式无效");
}
