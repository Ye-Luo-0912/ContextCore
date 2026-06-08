using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

/// <summary>上下文操作事件的严重级别。</summary>
public enum ContextEventLevel
{
    /// <summary>跟踪级别，用于最细粒度的诊断信息。</summary>
    Trace,
    /// <summary>信息级别，正常业务流程记录。</summary>
    Information,
    /// <summary>警告级别，需关注但不影响主流程。</summary>
    Warning,
    /// <summary>错误级别，操作未能成功完成。</summary>
    Error
}

/// <summary>上下文验证问题的严重程度。</summary>
public enum ContextValidationSeverity
{
    /// <summary>提示性信息。</summary>
    Info,
    /// <summary>警告，建议修正。</summary>
    Warning,
    /// <summary>错误，必须修正。</summary>
    Error
}

/// <summary>描述一次上下文操作的事件记录，用于审计与监控。</summary>
public sealed class ContextOperationEvent
{
    /// <summary>事件唯一标识符。</summary>
    public string EventId { get; init; } = string.Empty;

    /// <summary>所属操作的 ID。</summary>
    public string OperationId { get; init; } = string.Empty;

    /// <summary>操作名称。</summary>
    public string OperationName { get; init; } = string.Empty;

    /// <summary>所属工作空间 ID。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>所属集合 ID（可空）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>事件级别。</summary>
    public ContextEventLevel Level { get; init; } = ContextEventLevel.Information;

    /// <summary>事件消息。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>操作耗时（可空）。</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>附加元数据键值对。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();

    /// <summary>事件创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>表示单条验证问题。</summary>
public sealed class ContextValidationIssue
{
    /// <summary>问题代码。</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>可读描述信息。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>问题所在的字段路径（可空）。</summary>
    public string? Path { get; init; }

    /// <summary>严重程度。</summary>
    public ContextValidationSeverity Severity { get; init; } = ContextValidationSeverity.Error;
}

/// <summary>封装验证结果及所有问题列表。</summary>
public sealed class ContextValidationResult
{
    /// <summary>指示验证是否通过。</summary>
    public bool Succeeded { get; init; }

    /// <summary>验证问题列表。</summary>
    public IReadOnlyList<ContextValidationIssue> Issues { get; init; } = Array.Empty<ContextValidationIssue>();
}

/// <summary>上下文事件接收器，负责将操作事件持久化或转发到外部系统。</summary>
public interface IContextEventSink
{
    /// <summary>异步发送一条操作事件。</summary>
    Task EmitAsync(ContextOperationEvent operationEvent, CancellationToken cancellationToken = default);
}

/// <summary>提供对上下文条目、记忆条目及打包请求的合法性验证。</summary>
public interface IContextValidationService
{
    /// <summary>验证上下文条目的合法性。</summary>
    ContextValidationResult ValidateContextItem(ContextItem item);

    /// <summary>验证记忆条目的合法性。</summary>
    ContextValidationResult ValidateMemoryItem(ContextMemoryItem item);

    /// <summary>验证打包请求的合法性。</summary>
    ContextValidationResult ValidatePackageRequest(ContextPackageRequest request);
}

/// <summary>
/// 上下文运行时服务的核心接口，协调摄取、记忆管理与打包操作。
/// </summary>
public interface IContextRuntimeService
{
    /// <summary>通过统一输入命令摄取上下文条目，并应用输入层标准化、校验、哈希和顺序治理。</summary>
    Task<ContextInputIngestionResult> IngestAsync(ContextInputCommand command, CancellationToken cancellationToken = default);

    /// <summary>摄取上下文条目并完成标准化处理。</summary>
    Task<ContextItem> IngestAsync(ContextItem item, CancellationToken cancellationToken = default);

    /// <summary>将条目写入工作记忆层。</summary>
    Task<ContextMemoryItem> AddWorkingMemoryAsync(
        ContextMemoryItem item,
        CancellationToken cancellationToken = default);

    /// <summary>将工作记忆晋升为稳定记忆。</summary>
    Task<ContextPromotionRecord> PromoteMemoryAsync(
        string workspaceId,
        string collectionId,
        string sourceMemoryId,
        string strategy,
        string? reason = null,
        double confidence = 1.0,
        CancellationToken cancellationToken = default,
        string? reviewer = null);

    /// <summary>构建上下文包，用于向模型提供结构化输入。</summary>
    Task<ContextPackage> BuildPackageAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 构建上下文包并返回完整决策日志（含 <see cref="ContextCore.Abstractions.Models.RetrievalPlan"/>），
    /// 供调用方将 Plan 透传到后续的 <see cref="ContextRetrievalRequest.Plan"/>。
    /// </summary>
    Task<ContextPackageBuildResult> BuildPackageDetailedAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken = default);
}
