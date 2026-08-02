using System.Text.Json;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.Agent;

// ===========================================================================
// BridgingAgentWorkspaceContextProvider — 装饰器，合并 Bridge snapshot + base provider injection。
//
// 设计目标（对齐 R23/R24 规格）：
//   1. 装饰任意 IAgentWorkspaceContextProvider（如 DefaultAgentWorkspaceContextProvider）；
//   2. 通过 IAgentContextBridge 调用 ContextCore 检索/打包管线，获取 ContextCore-derived snapshot；
//   3. 同时调用 inner provider，获取 session-level injection/tool-result snapshot；
//   4. 合并两个 snapshot 的 Sections（ContextCore 在前，injection/tool 在后）；
//   5. Token 预算分配（默认 70% 给 ContextCore 检索，30% 给 injection/tool）；
//   6. 失败语义：Bridge 失败时 fail-open（仅用 inner provider，写入 Metadata 标记 bridgeFailed）。
//
// 设计边界：
//   - 不修改 inner provider 的状态；
//   - 不调用 IContextPackageBuilder（通过 IAgentContextBridge 间接调用）；
//   - 合并后的 snapshot 不持久化（由调用方决定持久化）。
// ===========================================================================

/// <summary>
/// <see cref="IAgentWorkspaceContextProvider"/> 的装饰器实现。
/// 将 ContextCore 检索（通过 Bridge）与 session 级注入（inner provider）合并为统一 snapshot。
/// </summary>
/// <remarks>
/// <b>Token 预算分配</b>：默认 70% 给 ContextCore 检索，30% 给 injection/tool；
/// 可通过 <see cref="ContextCoreBudgetRatio"/> 构造参数配置（取值范围 (0, 1)）。
///
/// <b>失败语义</b>：Bridge 失败时 fail-open（仅用 inner provider，写入 Metadata 标记 bridgeFailed=true）。
/// </remarks>
public sealed class BridgingAgentWorkspaceContextProvider : IAgentWorkspaceContextProvider
{
    /// <summary>默认 ContextCore 预算占比（0.70）。</summary>
    public const double DefaultContextCoreBudgetRatio = 0.70;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IAgentContextBridge _bridge;
    private readonly IAgentWorkspaceContextProvider _inner;
    private readonly TimeProvider _timeProvider;
    private readonly double _contextCoreBudgetRatio;

    /// <summary>构造 bridging provider。</summary>
    /// <param name="bridge">ContextCore 检索桥接器（必填）。</param>
    /// <param name="inner">被装饰的 inner provider（必填）。</param>
    /// <param name="timeProvider">时间提供者（可选，默认 <see cref="TimeProvider.System"/>）。</param>
    /// <param name="contextCoreBudgetRatio">
    /// ContextCore 预算占比（取值范围 (0, 1)）；默认 0.70。
    /// </param>
    public BridgingAgentWorkspaceContextProvider(
        IAgentContextBridge bridge,
        IAgentWorkspaceContextProvider inner,
        TimeProvider? timeProvider = null,
        double contextCoreBudgetRatio = DefaultContextCoreBudgetRatio)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(inner);
        if (contextCoreBudgetRatio <= 0 || contextCoreBudgetRatio >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(contextCoreBudgetRatio),
                contextCoreBudgetRatio,
                "contextCoreBudgetRatio 必须在 (0, 1) 范围内");
        }

        _bridge = bridge;
        _inner = inner;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _contextCoreBudgetRatio = contextCoreBudgetRatio;
    }

    /// <summary>ContextCore 预算占比（只读，供测试）。</summary>
    public double ContextCoreBudgetRatio => _contextCoreBudgetRatio;

    /// <inheritdoc />
    public async Task<AgentContextSnapshotRef> GetContextSnapshotAsync(
        AgentSessionId sessionId,
        int tokenBudget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (tokenBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenBudget), tokenBudget,
                "Token budget must be > 0");
        }
        cancellationToken.ThrowIfCancellationRequested();

        // 预算分配
        var ccBudget = Math.Max(1, (int)(tokenBudget * _contextCoreBudgetRatio));
        var innerBudget = Math.Max(1, tokenBudget - ccBudget);

        AgentContextSnapshot? ccSnapshot = null;
        string? bridgeError = null;
        try
        {
            var bridgeResponse = await _bridge.BuildSnapshotAsync(
                new AgentContextBridgeRequest
                {
                    Session = sessionId,
                    TokenBudget = ccBudget
                },
                cancellationToken).ConfigureAwait(false);
            ccSnapshot = bridgeResponse.Snapshot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // fail-open：记录错误，仅用 inner provider
            bridgeError = ex.GetType().Name + ": " + ex.Message;
        }
        cancellationToken.ThrowIfCancellationRequested();

        // 调用 inner provider
        var innerRef = await _inner.GetContextSnapshotAsync(sessionId, innerBudget, cancellationToken)
            .ConfigureAwait(false);
        var innerSnapshot = DeserializeSnapshot(innerRef.ContentJson);

        // 合并 sections：ContextCore 在前，inner 在后
        var mergedSections = new List<AgentContextSection>();
        if (ccSnapshot is not null)
        {
            // 重写 cc section 的 SortOrder，确保排序连续
            var ccOffset = 0;
            foreach (var s in ccSnapshot.Sections.OrderBy(s => s.SortOrder))
            {
                mergedSections.Add(s with { SortOrder = ccOffset++ });
            }
        }
        var innerOffset = mergedSections.Count;
        foreach (var s in innerSnapshot.Sections.OrderBy(s => s.SortOrder))
        {
            mergedSections.Add(s with { SortOrder = innerOffset++ });
        }

        // 合并 DecisionRequestIds / ConstraintIds / ToolCallRefs
        var decisionIds = new List<string>();
        if (ccSnapshot is not null)
        {
            decisionIds.AddRange(ccSnapshot.DecisionRequestIds);
        }
        decisionIds.AddRange(innerSnapshot.DecisionRequestIds);
        decisionIds = decisionIds.Distinct(StringComparer.Ordinal).ToList();

        var constraintIds = new List<string>();
        if (ccSnapshot is not null)
        {
            constraintIds.AddRange(ccSnapshot.ConstraintIds);
        }
        constraintIds.AddRange(innerSnapshot.ConstraintIds);
        constraintIds = constraintIds.Distinct(StringComparer.Ordinal).ToList();

        var toolCallRefs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ccSnapshot is not null)
        {
            foreach (var kv in ccSnapshot.ToolCallRefs)
            {
                toolCallRefs[kv.Key] = kv.Value;
            }
        }
        foreach (var kv in innerSnapshot.ToolCallRefs)
        {
            toolCallRefs[kv.Key] = kv.Value;
        }

        // 合并 token 计数
        var actualTokens = (ccSnapshot?.ActualTokens ?? 0) + innerSnapshot.ActualTokens;

        var now = _timeProvider.GetUtcNow();
        var mergedSnapshot = new AgentContextSnapshot
        {
            SnapshotId = $"bridge-merged-{Guid.NewGuid():N}",
            Session = sessionId,
            CreatedAt = now,
            TokenBudget = tokenBudget,
            ActualTokens = actualTokens,
            Sections = mergedSections,
            DecisionRequestIds = decisionIds,
            ConstraintIds = constraintIds,
            ToolCallRefs = toolCallRefs,
            Metadata = BuildMetadata(ccSnapshot, innerSnapshot, bridgeError)
        };

        var contentJson = JsonSerializer.Serialize(mergedSnapshot, JsonOptions);
        return new AgentContextSnapshotRef
        {
            SnapshotId = mergedSnapshot.SnapshotId,
            Session = sessionId,
            CreatedAt = now,
            ActualTokens = actualTokens,
            TokenBudget = tokenBudget,
            ContentJson = contentJson,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source"] = "BridgingAgentWorkspaceContextProvider",
                ["ccSnapshotId"] = ccSnapshot?.SnapshotId ?? "",
                ["innerSnapshotId"] = innerSnapshot.SnapshotId,
                ["ccSectionCount"] = (ccSnapshot?.Sections.Count ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["innerSectionCount"] = innerSnapshot.Sections.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["bridgeFailed"] = bridgeError is not null ? "true" : "false"
            }
        };
    }

    /// <inheritdoc />
    public Task InjectAsync(
        AgentSessionId sessionId,
        AgentContextInjection injection,
        CancellationToken cancellationToken = default)
    {
        // 直接委托给 inner provider（Bridging provider 不持有独立状态）
        return _inner.InjectAsync(sessionId, injection, cancellationToken);
    }

    /// <inheritdoc />
    public Task IngestToolResultAsync(
        AgentSessionId sessionId,
        string toolCallId,
        string toolName,
        string resultJson,
        CancellationToken cancellationToken = default)
    {
        // 直接委托给 inner provider
        return _inner.IngestToolResultAsync(sessionId, toolCallId, toolName, resultJson, cancellationToken);
    }

    private static IReadOnlyDictionary<string, string> BuildMetadata(
        AgentContextSnapshot? ccSnapshot,
        AgentContextSnapshot innerSnapshot,
        string? bridgeError)
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source"] = "BridgingAgentWorkspaceContextProvider",
            ["ccSnapshotId"] = ccSnapshot?.SnapshotId ?? "",
            ["innerSnapshotId"] = innerSnapshot.SnapshotId,
            ["mergedSectionCount"] = ((ccSnapshot?.Sections.Count ?? 0) + innerSnapshot.Sections.Count).ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (bridgeError is not null)
        {
            meta["bridgeFailed"] = "true";
            // 截断错误消息避免 metadata 膨胀
            meta["bridgeError"] = bridgeError.Length > 200 ? bridgeError.Substring(0, 200) : bridgeError;
        }
        else
        {
            meta["bridgeFailed"] = "false";
        }
        return meta;
    }

    private static AgentContextSnapshot DeserializeSnapshot(string json)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        return JsonSerializer.Deserialize<AgentContextSnapshot>(json, options)
            ?? throw new InvalidOperationException("Failed to deserialize inner snapshot");
    }
}
