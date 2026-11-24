using System.Text.Json.Serialization;

namespace DispenseServer.Core;

/// <summary>P9: 人工位姿输入；旋转顺序与上位机一致，固定为 Rx → Ry → Rz。</summary>
public sealed record Pose3D(
    double X = 0, double Y = 0, double Z = 0,
    double Rx = 0, double Ry = 0, double Rz = 0);

/// <summary>P9: 夹具安装位置、固定方式与虚拟到位信号。</summary>
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

/// <summary>P9: 工件相对夹具的放置变换。</summary>
public sealed record PlacementConfig(
    Pose3D? RelativePose = null,
    double Scale = 1,
    bool FlipNormals = false)
{
    [JsonIgnore]
    public Pose3D Pose => RelativePose ?? new Pose3D();
}

/// <summary>P9: 虚拟上下料配方；所有位姿均使用机器坐标。</summary>
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

/// <summary>P9: 完整生产配置；工艺编辑仍只在上位机完成。</summary>
public sealed record ProductionConfig(
    string ProjectId,
    string Name,
    FixtureConfig Fixture,
    PlacementConfig Placement,
    LoadingRecipe Loading,
    bool FullProduction = true);

/// <summary>上位机一次性上传的生产工程与完整路径步骤。</summary>
public sealed record ProductionEnvelope(
    string ProjectId,
    long Revision,
    string ModelId,
    ProductionConfig Config,
    List<JobStepDto> Steps);

/// <summary>原始模型同步的首帧；Data 使用原始 48KiB 分片的 Base64。</summary>
public sealed record ModelBeginDto(
    string ModelId,
    string FileName,
    string Unit,
    string Sha256,
    long TotalBytes,
    int TotalChunks);

/// <summary>原始模型同步的一个分片，索引从 0 开始。</summary>
public sealed record ModelChunkDto(string ModelId, int Index, string Data);

/// <summary>服务端已完整校验并持久化的模型元数据。</summary>
public sealed record StoredModel(
    string ModelId,
    string FileName,
    string Unit,
    string Sha256,
    long TotalBytes,
    int TotalChunks,
    DateTimeOffset SavedUtc);

public enum ProductionPhase
{
    Idle,
    WaitingLoad,
    Loading,
    WaitingClamp,
    Ready,
    Dispensing,
    WaitingUnload,
    Unloading,
    Completed,
    Paused,
    Blocked,
}

/// <summary>P9: 推送给 Web/上位机的唯一生产动画与操作状态来源。</summary>
public sealed record ProductionSnapshot(
    string Phase,
    string? RunId,
    int PieceIndex,
    int CompletedCount,
    int BatchCount,
    double Progress,
    bool PartPresent,
    bool Clamped,
    Pose3D WorkpiecePose,
    string? Error,
    bool Paused,
    bool CanRetry);
