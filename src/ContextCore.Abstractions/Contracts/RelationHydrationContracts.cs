namespace ContextCore.Abstractions;

/// <summary>
/// Selected 关系批量水合请求。
/// <para>
/// 客户端在检索/决策阶段选定关系 ID 后，通过本请求按 ID 拉取完整 Relation
/// （Metadata/SourceRefs/Provenance 等），避免为候选枚举阶段生成完整 JSON。
/// </para>
/// </summary>
public sealed class RelationHydrationRequest
{
    /// <summary>调用方操作 ID（用于审计与错误追踪；可为空）。</summary>
    public string OperationId { get; init; } = string.Empty;

    /// <summary>工作空间 ID（必填）。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>可选集合过滤；为空时跨集合按 ID 查找。</summary>
    public string? CollectionId { get; init; }

    /// <summary>需要 hydrate 的关系 ID 列表（必填，至少 1 个；自动去重）。</summary>
    public IReadOnlyList<string> RelationIds { get; init; } = Array.Empty<string>();
}

/// <summary>单条 Selected 关系水合结果。</summary>
public sealed class RelationHydrationEntry
{
    /// <summary>关系 ID。</summary>
    public string RelationId { get; init; } = string.Empty;

    /// <summary>来源条目 ID。</summary>
    public string SourceId { get; init; } = string.Empty;

    /// <summary>目标条目 ID。</summary>
    public string TargetId { get; init; } = string.Empty;

    /// <summary>关系类型名称（如 "references"）。</summary>
    public string RelationType { get; init; } = string.Empty;

    /// <summary>关系权重。</summary>
    public double Weight { get; init; }

    /// <summary>置信度（0～1）。</summary>
    public double Confidence { get; init; }

    /// <summary>生命周期状态（RelationLifecycles 值）。</summary>
    public string Lifecycle { get; init; } = string.Empty;

    /// <summary>审核状态（RelationReviewStatuses 值）。</summary>
    public string ReviewStatus { get; init; } = string.Empty;

    /// <summary>来源引用 ID 列表。</summary>
    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    /// <summary>附加元数据（完整 Metadata JSON 字段）。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>来源标识（创建此关系的来源操作或服务名称）。</summary>
    public string? Provenance { get; init; }
}

/// <summary>Selected 关系批量水合响应。</summary>
public sealed class RelationHydrationResponse
{
    /// <summary>调用方操作 ID（回显）。</summary>
    public string OperationId { get; init; } = string.Empty;

    /// <summary>工作空间 ID（回显）。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>集合过滤（回显）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>去重后的请求关系 ID 数（恒等于 HydratedCount + MissingCount）。</summary>
    public int RequestedCount { get; init; }

    /// <summary>成功水合的关系数。</summary>
    public int HydratedCount { get; init; }

    /// <summary>未命中的关系 ID 数。</summary>
    public int MissingCount { get; init; }

    /// <summary>
    /// 数据来源："relation-hydration-store"（探测到批量水合能力）或
    /// "relation-store-fallback"（逐条 <see cref="IRelationStore.GetAsync"/> 回退）。
    /// </summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>水合成功的关系列表（保持请求顺序）。</summary>
    public IReadOnlyList<RelationHydrationEntry> Relations { get; init; } = Array.Empty<RelationHydrationEntry>();

    /// <summary>未命中的关系 ID 列表（保持请求顺序）。</summary>
    public IReadOnlyList<string> MissingIds { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Selected 关系批量水合服务。
/// <para>
/// 优先探测 <see cref="IRelationHydrationStore"/>（批量单次存储访问）；未实现时
/// 回退到 <see cref="IRelationStore.GetAsync"/> 逐条获取。探测语义与
/// <see cref="IRelationStreamStore"/> 一致：调用方 <c>as IRelationHydrationStore</c>，
/// 不强制要求存储实现额外接口。
/// </para>
/// </summary>
public interface ISelectedRelationHydrationService
{
    /// <summary>
    /// 按关系 ID 批量水合完整 Relation。
    /// </summary>
    /// <param name="request">水合请求（WorkspaceId + RelationIds 必填）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>水合响应（含来源、命中/缺失统计与完整关系列表）。</returns>
    Task<RelationHydrationResponse> HydrateAsync(
        RelationHydrationRequest request,
        CancellationToken cancellationToken = default);
}
