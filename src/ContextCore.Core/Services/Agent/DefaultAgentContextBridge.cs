using System.Diagnostics;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Agent;

// ===========================================================================
// DefaultAgentContextBridge — 默认 Agent Context 桥接器实现。
//
// 实现 IAgentContextBridge 契约：
//   1. 将 AgentContextBridgeRequest 转换为 ContextPackageRequest；
//   2. 调用 IContextPackageBuilder.BuildDetailedAsync；
//   3. 将 ContextPackageBuildResult 映射为 AgentContextSnapshot：
//      - Sections：ContextPackageSection → AgentContextSection
//        (Name → SectionName, Content → Content, SourceRefs[0] → Source,
//         Priority → SortOrder, EstimatedTokens → ActualTokens/TokenBudget)
//      - DecisionRequestIds：SelectedItems.Select(i => i.ItemId)
//      - SnapshotId：ContextPackage.PackageId
//      - ActualTokens：ContextPackage.EstimatedTokens
//   4. 失败语义：ContextCore 构建异常直接抛出（fail-closed）；
//      调用方决定是否回退到 base provider 的纯 injection-based snapshot。
//
// 设计边界：
//   - Bridge 无状态；线程安全；
//   - 不缓存；每次调用都重新构建；
//   - 不修改 IContextPackageBuilder 的输入/输出；
//   - ToolCallRefs / ConstraintIds 留空（由 base provider 在合并阶段填充）。
// ===========================================================================

/// <summary>
/// <see cref="IAgentContextBridge"/> 的默认实现。
/// </summary>
/// <remarks>
/// 桥接 Agent Runtime 与 ContextCore 上下文构建管线。
/// 无状态；线程安全；不缓存。
/// </remarks>
public sealed class DefaultAgentContextBridge : IAgentContextBridge
{
    private readonly IContextPackageBuilder _packageBuilder;
    private readonly TimeProvider _timeProvider;

    /// <summary>构造 bridge。</summary>
    /// <param name="packageBuilder">ContextCore 包构建器（必填）。</param>
    /// <param name="timeProvider">时间提供者（可选，默认 <see cref="TimeProvider.System"/>）。</param>
    public DefaultAgentContextBridge(
        IContextPackageBuilder packageBuilder,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(packageBuilder);
        _packageBuilder = packageBuilder;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<AgentContextBridgeResponse> BuildSnapshotAsync(
        AgentContextBridgeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TokenBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"TokenBudget must be > 0；收到 {request.TokenBudget}");
        }
        cancellationToken.ThrowIfCancellationRequested();

        // 构造 ContextPackageRequest
        var packageRequest = new ContextPackageRequest
        {
            WorkspaceId = request.Session.WorkspaceId,
            CollectionId = request.Session.CollectionId ?? string.Empty,
            QueryText = request.QueryText,
            RequiredTags = request.RequiredTags,
            RequiredTypes = request.RequiredTypes,
            TokenBudget = request.TokenBudget,
            IncludeRecent = request.IncludeRecent
        };

        // 调用 ContextCore 构建管线（fail-closed：异常直接抛出）
        var stopwatch = Stopwatch.StartNew();
        var buildResult = await _packageBuilder.BuildDetailedAsync(packageRequest, cancellationToken)
            .ConfigureAwait(false);
        stopwatch.Stop();

        // 映射为 AgentContextSnapshot
        var snapshot = MapToSnapshot(buildResult, request);

        return new AgentContextBridgeResponse
        {
            Snapshot = snapshot,
            BuildResult = buildResult,
            Duration = stopwatch.Elapsed
        };
    }

    private AgentContextSnapshot MapToSnapshot(
        ContextPackageBuildResult buildResult,
        AgentContextBridgeRequest request)
    {
        var package = buildResult.Package;
        var now = _timeProvider.GetUtcNow();

        // Section 映射：ContextPackageSection → AgentContextSection
        var sections = package.Sections
            .Select((s, index) => new AgentContextSection
            {
                SectionName = s.Name,
                SortOrder = s.Priority != 0 ? s.Priority : index,
                TokenBudget = request.TokenBudget, // 全局预算分摊到每个 section（粗略）
                ActualTokens = EstimateTokens(s.Content),
                Content = s.Content,
                Source = s.SourceRefs.Count > 0 ? s.SourceRefs[0] : "ContextCore"
            })
            .ToList();

        // DecisionRequestIds：从 SelectedItems 提取 ItemId
        var decisionIds = buildResult.SelectedItems
            .Select(d => d.ItemId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // ConstraintIds：从 SelectedItems 的 Type 字段提取（粗略映射）
        // 注意：ContextCore 的 Constraint 概念与 Agent 的 ConstraintId 不同；
        // 此处留空，由 base provider 在合并阶段填充。
        var constraintIds = Array.Empty<string>();

        // ToolCallRefs：留空（bridge 不涉及 tool 调用）
        var toolCallRefs = new Dictionary<string, string>(StringComparer.Ordinal);

        return new AgentContextSnapshot
        {
            SnapshotId = !string.IsNullOrEmpty(package.PackageId)
                ? $"agent-bridge-{package.PackageId}"
                : $"agent-bridge-{Guid.NewGuid():N}",
            Session = request.Session,
            CreatedAt = now,
            TokenBudget = request.TokenBudget,
            ActualTokens = package.EstimatedTokens,
            Sections = sections,
            DecisionRequestIds = decisionIds,
            ConstraintIds = constraintIds,
            ToolCallRefs = toolCallRefs,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source"] = "DefaultAgentContextBridge",
                ["buildId"] = buildResult.BuildId,
                ["packageId"] = package.PackageId,
                ["selectedCount"] = buildResult.SelectedItems.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["droppedCount"] = buildResult.DroppedItems.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["sectionCount"] = package.Sections.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        };
    }

    private static int EstimateTokens(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return 0;
        }
        // 粗略估算：1 token ≈ 4 chars（与 DefaultAgentWorkspaceContextProvider 一致）
        return (content.Length + 3) / 4;
    }
}
