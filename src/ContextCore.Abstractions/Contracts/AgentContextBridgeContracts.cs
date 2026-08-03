using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

// ===========================================================================
// Agent Context Bridge 契约
//
// 目标：
// 1. 桥接 Agent Runtime 与 ContextCore 检索/打包管线：
// 将 Agent session 的查询请求转换为 ContextPackageRequest，
// 调用 IContextPackageBuilder.BuildDetailedAsync，
// 将 ContextPackageBuildResult 映射为 AgentContextSnapshot。
// 2. 解耦：Bridge 不持有 session 状态；仅负责一次性的 request→snapshot 转换。
// 3. 失败语义：ContextCore 构建失败时抛异常（fail-closed）；
// 调用方决定是否回退到 base provider 的纯 injection-based snapshot。
//
// 设计边界：
// - Bridge 不修改 IContextPackageBuilder 的输入/输出；
// - Bridge 不缓存（每次调用都重新构建）；
// - Section 映射：ContextPackageSection.Name → AgentContextSection.SectionName，
// Content → Content，SourceRefs[0] → Source，EstimatedTokens → ActualTokens；
// - DecisionRequestIds 从 SelectedItems 提取（ItemId 字段）；
// - SnapshotId = ContextPackage.PackageId（保证可追溯）。
// ===========================================================================

/// <summary>
/// Agent Context 桥接器。将 ContextCore 检索/打包结果转换为 <see cref="AgentContextSnapshot"/>。
/// </summary>
/// <remarks>
/// 桥接 Agent Runtime 与 ContextCore 上下文构建管线。
/// Bridge 无状态；每次调用都重新构建。
/// </remarks>
public interface IAgentContextBridge
{
    /// <summary>基于 Agent session 的查询请求构建 <see cref="AgentContextSnapshot"/>。</summary>
    /// <param name="request">桥接请求（含 session + 查询参数 + token 预算）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>包含 <see cref="AgentContextSnapshot"/> 的桥接响应。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> 为 null。</exception>
    /// <exception cref="InvalidOperationException">session 不存在或 ContextCore 构建失败。</exception>
    Task<AgentContextBridgeResponse> BuildSnapshotAsync(
        AgentContextBridgeRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Agent Context 桥接请求。</summary>
public sealed record AgentContextBridgeRequest
{
    /// <summary>Agent session 标识（必填）。</summary>
    public required AgentSessionId Session { get; init; }

    /// <summary>查询文本（可空；null = 不基于查询检索）。</summary>
    public string? QueryText { get; init; }

    /// <summary>Token 预算（必填，> 0）。</summary>
    public required int TokenBudget { get; init; }

    /// <summary>必需标签过滤（默认空）。</summary>
    public IReadOnlyList<string> RequiredTags { get; init; } = Array.Empty<string>();

    /// <summary>必需类型过滤（默认空）。</summary>
    public IReadOnlyList<string> RequiredTypes { get; init; } = Array.Empty<string>();

    /// <summary>是否包含最近条目（默认 true）。</summary>
    public bool IncludeRecent { get; init; } = true;

    /// <summary>附加元数据（默认空）。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>Agent Context 桥接响应。</summary>
public sealed record AgentContextBridgeResponse
{
    /// <summary>构建的 Agent 上下文快照。</summary>
    public required AgentContextSnapshot Snapshot { get; init; }

    /// <summary>底层 ContextCore 构建结果（供审计/调试）。</summary>
    public required ContextPackageBuildResult BuildResult { get; init; }

    /// <summary>桥接耗时。</summary>
    public TimeSpan Duration { get; init; }
}
