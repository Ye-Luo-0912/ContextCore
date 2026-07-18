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

/// <summary>
/// 上下文事件接收器的故障语义，用于在复合事件接收器中决定失败处理方式。
/// </summary>
public enum ContextEventSinkKind
{
    /// <summary>
    /// 尽力而为（fail-open）：sink 失败不应阻断业务或后续 sink。
    /// 适用于日志、指标、运行事件等可降级的遥测。
    /// </summary>
    BestEffort,
    /// <summary>
    /// 必须成功（fail-closed）：sink 失败需要向调用方抛出聚合异常。
    /// 仅用于明确要求审计落盘成功的安全操作。
    /// </summary>
    Required
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

    /// <summary>受影响实体的类型（如 "ContextItem"、"MemoryItem"、"Relation"）。可空，用于审计与失效路由。</summary>
    public string? EntityType { get; init; }

    /// <summary>受影响实体的 ID。可空，集合级操作时为 null。</summary>
    public string? EntityId { get; init; }

    /// <summary>对实体执行的操作（如 "Save"、"Upsert"、"Delete"、"Promote"、"Build"）。可空。</summary>
    public string? Operation { get; init; }

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

    /// <summary>
    /// R13.4 #1：批量写入路径。实现应覆盖以利用 File/Postgres 的批量 I/O（单次锁、单次 round-trip）。
    /// 默认实现为逐条调用 <see cref="EmitAsync"/>，适用于不支持批量写入的 sink。
    /// </summary>
    /// <param name="events">要批量写入的事件列表。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    async Task EmitBatchAsync(
        IReadOnlyList<ContextOperationEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (var evt in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EmitAsync(evt, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// sink 的故障语义。默认 <see cref="ContextEventSinkKind.BestEffort"/>，即 sink 失败时不应阻断业务或后续 sink。
    /// 仅审计/安全相关 sink 需重写为 <see cref="ContextEventSinkKind.Required"/>，使其失败时由复合接收器聚合并向上抛出。
    /// </summary>
    ContextEventSinkKind Kind => ContextEventSinkKind.BestEffort;
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
