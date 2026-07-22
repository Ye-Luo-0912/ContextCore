using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// R28-B B-4：Authoritative Cutover — Retrieval → Package → AgentContext
//
// 目标（B-4 阶段：V2 Runtime 成为权威路径，Legacy 降级为可选 fallback）：
//   1. AuthoritativeRetrievalRuntime：V2 执行 + 可选 Shadow parity 校验 + fallback。
//   2. AuthoritativePackageRuntime：V2 执行 + 可选 Shadow parity 校验 + fallback。
//   3. AuthoritativeAgentContextRuntime：V2 执行 + AgentContextProjector 投影。
//   4. CutoverController：控制 Legacy → V2 切换比例（0% = Legacy only，
//      100% = V2 only，中间值 = 按 requestId 哈希分流）。
//
// 设计原则：
//   1. 渐进切换：CutoverController 按 percentage 控制流量比例，支持灰度。
//   2. Fallback 安全：V2 失败时自动回退到 Legacy（fail-open）。
//   3. Parity 监控：切换期间持续 Shadow parity 校验，Divergent 自动回退。
//   4. B-4 不删除 Legacy 代码（B-5 才删除）；Legacy 仍可通过 CutoverPercentage=0 启用。
// ===========================================================================

// ---------------------------------------------------------------------------
// §9.1 CutoverController — 渐进切换控制
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B B-4：Cutover 控制器。按 percentage 控制 Legacy → V2 流量比例。
/// </summary>
/// <remarks>
/// 灰度策略：按 requestId 的稳定哈希决定走 V2 还是 Legacy。
/// 0% = 全部 Legacy；100% = 全部 V2；50% = 一半 V2 一半 Legacy。
/// 哈希稳定性保证同一 requestId 始终走同一路径（便于 Shadow parity 对比）。
/// </remarks>
public sealed class CutoverController
{
    private int _cutoverPercentage;

    /// <summary>构造控制器，默认 cutoverPercentage=0（全部 Legacy）。</summary>
    public CutoverController(int cutoverPercentage = 0)
    {
        if (cutoverPercentage < 0 || cutoverPercentage > 100)
            throw new ArgumentOutOfRangeException(nameof(cutoverPercentage));
        _cutoverPercentage = cutoverPercentage;
    }

    /// <summary>当前 V2 流量百分比（0-100）。</summary>
    public int CutoverPercentage => _cutoverPercentage;

    /// <summary>设置 V2 流量百分比（线程安全）。</summary>
    public void SetCutoverPercentage(int percentage)
    {
        if (percentage < 0 || percentage > 100)
            throw new ArgumentOutOfRangeException(nameof(percentage));
        Interlocked.Exchange(ref _cutoverPercentage, percentage);
    }

    /// <summary>判断给定 requestId 是否应走 V2 路径。</summary>
    /// <remarks>使用 requestId 的稳定哈希（FNV-1a）取模，保证同一 requestId 始终走同一路径。</remarks>
    public bool ShouldUseV2(string requestId)
    {
        var percentage = _cutoverPercentage;
        if (percentage <= 0) return false;
        if (percentage >= 100) return true;

        var hash = StableHash(requestId);
        return (hash % 100) < percentage;
    }

    private static uint StableHash(string value)
    {
        // FNV-1a 32-bit hash（稳定跨进程/跨平台）
        uint hash = 2166136261u;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= 16777619u;
        }
        return hash;
    }
}

// ---------------------------------------------------------------------------
// §9.2 AuthoritativeRetrievalRuntime — Retrieval 权威路径
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B B-4：Retrieval 权威路径运行时。
/// 编排 Legacy Retriever + V2 Runtime + 可选 Shadow parity + fallback。
/// </summary>
public sealed class AuthoritativeRetrievalRuntime
{
    private readonly IContextRetriever _legacyRetriever;
    private readonly IContextDecisionRuntime _v2Runtime;
    private readonly ShadowDecisionRuntime _shadowRuntime;
    private readonly RetrievalResultProjector _retrievalProjector;
    private readonly CutoverController _cutoverController;
    private readonly ShadowGate? _shadowGate;

    /// <summary>构造 Retrieval 权威路径运行时。</summary>
    public AuthoritativeRetrievalRuntime(
        IContextRetriever legacyRetriever,
        IContextDecisionRuntime v2Runtime,
        ShadowDecisionRuntime shadowRuntime,
        RetrievalResultProjector retrievalProjector,
        CutoverController cutoverController,
        ShadowGate? shadowGate = null)
    {
        _legacyRetriever = legacyRetriever ?? throw new ArgumentNullException(nameof(legacyRetriever));
        _v2Runtime = v2Runtime ?? throw new ArgumentNullException(nameof(v2Runtime));
        _shadowRuntime = shadowRuntime ?? throw new ArgumentNullException(nameof(shadowRuntime));
        _retrievalProjector = retrievalProjector ?? throw new ArgumentNullException(nameof(retrievalProjector));
        _cutoverController = cutoverController ?? throw new ArgumentNullException(nameof(cutoverController));
        _shadowGate = shadowGate;
    }

    /// <summary>
    /// 执行 Retrieval。按 CutoverController 决定走 V2 或 Legacy。
    /// V2 路径：Legacy 仍执行（Shadow tee）+ V2 执行 + parity 校验。
    /// V2 失败或 parity Divergent 时自动回退到 Legacy 结果。
    /// </summary>
    public async Task<ContextRetrievalResult> RetrieveAsync(
        ContextRetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Legacy 总是执行（Shadow tee 需要其结果；fallback 也需要）
        var legacyResult = await _legacyRetriever.RetrieveAsync(request, cancellationToken).ConfigureAwait(false);

        var useV2 = _cutoverController.ShouldUseV2(request.OperationId);
        if (!useV2)
        {
            return legacyResult;
        }

        // V2 路径：Shadow tee 捕获 + V2 执行 + parity 校验
        try
        {
            var context = new CandidateAdaptationContext
            {
                WorkspaceId = request.WorkspaceId,
                CollectionId = request.CollectionId,
                RequestId = request.OperationId,
                QueryText = request.QueryText,
                ObservedAt = DateTimeOffset.UtcNow
            };

            var tokenBudget = legacyResult.EstimatedTokens > 0 ? legacyResult.EstimatedTokens : 4096;
            var topK = request.TopK > 0 ? request.TopK : 10;

            var shadowReport = await _shadowRuntime.ExecuteRetrievalShadowAsync(
                request, legacyResult, tokenBudget, topK, context, cancellationToken).ConfigureAwait(false);

            // Parity 校验（B-3 Hard gate）
            if (_shadowGate is not null)
            {
                var gateResult = _shadowGate.Evaluate(shadowReport.Parity);
                if (gateResult.OverallLevel == ParityLevel.Divergent)
                {
                    // Divergent → 回退到 Legacy
                    return legacyResult;
                }
            }

            // Parity 通过 → 使用 V2 结果（通过 Projector 投影为 ContextRetrievalResult）
            return _retrievalProjector.Project(shadowReport.V2Result);
        }
        catch
        {
            // V2 失败 → 回退到 Legacy（fail-open）
            return legacyResult;
        }
    }
}

// ---------------------------------------------------------------------------
// §9.3 AuthoritativePackageRuntime — Package 权威路径
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B B-4：Package 权威路径运行时。
/// 编排 Legacy PackageBuilder + V2 Runtime + 可选 Shadow parity + fallback。
/// </summary>
public sealed class AuthoritativePackageRuntime
{
    private readonly IContextPackageBuilder _legacyPackageBuilder;
    private readonly IContextDecisionRuntime _v2Runtime;
    private readonly ShadowDecisionRuntime _shadowRuntime;
    private readonly PackageResultProjector _packageProjector;
    private readonly CutoverController _cutoverController;
    private readonly ShadowGate? _shadowGate;

    /// <summary>构造 Package 权威路径运行时。</summary>
    public AuthoritativePackageRuntime(
        IContextPackageBuilder legacyPackageBuilder,
        IContextDecisionRuntime v2Runtime,
        ShadowDecisionRuntime shadowRuntime,
        PackageResultProjector packageProjector,
        CutoverController cutoverController,
        ShadowGate? shadowGate = null)
    {
        _legacyPackageBuilder = legacyPackageBuilder ?? throw new ArgumentNullException(nameof(legacyPackageBuilder));
        _v2Runtime = v2Runtime ?? throw new ArgumentNullException(nameof(v2Runtime));
        _shadowRuntime = shadowRuntime ?? throw new ArgumentNullException(nameof(shadowRuntime));
        _packageProjector = packageProjector ?? throw new ArgumentNullException(nameof(packageProjector));
        _cutoverController = cutoverController ?? throw new ArgumentNullException(nameof(cutoverController));
        _shadowGate = shadowGate;
    }

    /// <summary>
    /// 执行 Package 构建。按 CutoverController 决定走 V2 或 Legacy。
    /// </summary>
    public async Task<ContextPackageBuildResult> BuildDetailedAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var legacyResult = await _legacyPackageBuilder.BuildDetailedAsync(request, cancellationToken).ConfigureAwait(false);

        var requestId = legacyResult.BuildId;
        var useV2 = _cutoverController.ShouldUseV2(requestId);
        if (!useV2)
        {
            return legacyResult;
        }

        try
        {
            var context = new CandidateAdaptationContext
            {
                WorkspaceId = request.WorkspaceId,
                CollectionId = request.CollectionId,
                RequestId = requestId,
                QueryText = request.QueryText,
                ObservedAt = DateTimeOffset.UtcNow
            };

            var tokenBudget = legacyResult.TokenBudget > 0
                ? legacyResult.TokenBudget
                : legacyResult.EstimatedTokens;

            var shadowReport = await _shadowRuntime.ExecutePackageShadowAsync(
                requestId, legacyResult, tokenBudget, context, cancellationToken).ConfigureAwait(false);

            if (_shadowGate is not null)
            {
                var gateResult = _shadowGate.Evaluate(shadowReport.Parity);
                if (gateResult.OverallLevel == ParityLevel.Divergent)
                {
                    return legacyResult;
                }
            }

            return _packageProjector.Project(shadowReport.V2Result);
        }
        catch
        {
            return legacyResult;
        }
    }
}

// ---------------------------------------------------------------------------
// §9.4 AuthoritativeAgentContextRuntime — AgentContext 权威路径
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B B-4：AgentContext 权威路径运行时。
/// 直接消费 V2 Runtime + AgentContextProjector，无需 Legacy fallback（AgentContext 是新路径）。
/// </summary>
public sealed class AuthoritativeAgentContextRuntime
{
    private readonly IContextDecisionRuntime _v2Runtime;
    private readonly IAgentContextProjector _agentContextProjector;

    /// <summary>构造 AgentContext 权威路径运行时。</summary>
    public AuthoritativeAgentContextRuntime(
        IContextDecisionRuntime v2Runtime,
        IAgentContextProjector agentContextProjector)
    {
        _v2Runtime = v2Runtime ?? throw new ArgumentNullException(nameof(v2Runtime));
        _agentContextProjector = agentContextProjector ?? throw new ArgumentNullException(nameof(agentContextProjector));
    }

    /// <summary>
    /// 执行 AgentContext 构建。直接走 V2 Runtime（无 Legacy fallback）。
    /// </summary>
    /// <param name="request">V2 Runtime 请求。</param>
    /// <param name="workingSet">候选 WorkingSet（含 Envelopes + Materials）。</param>
    public async ValueTask<AgentContextSnapshot> BuildAsync(
        ContextDecisionRuntimeRequest request,
        CandidateWorkingSet workingSet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(workingSet);

        // 合并 WorkingSet 的 Envelopes 到 request 的 SeedCandidates
        var mergedRequest = request with { SeedCandidates = workingSet.Envelopes };
        var result = await _v2Runtime.ExecuteAsync(mergedRequest, cancellationToken).ConfigureAwait(false);
        return _agentContextProjector.Project(result, workingSet);
    }
}
