using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Learning.V14_0;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// R28-B B-2：Candidate Capture + Pure Runtime + Tee Shadow 执行
//
// 目标（B-2 阶段：Shadow tee，单次候选捕获）：
//   1. Tee 机制：在 Legacy 主链产出后，零侵入地捕获原始候选快照，
//      转换为 V2 CandidateWorkingSet（Envelopes + Materials）。
//   2. Pure Runtime：DefaultContextDecisionRuntime 升级为真实编排
//      （EarlyGate → FeaturePipeline → Engine → Allocator），消费 WorkingSet。
//   3. Shadow 执行：ShadowDecisionRuntime 编排 Legacy + Tee + V2 + Parity，
//      产出 DecisionExperimentPlane 的对比结果。
//
// 设计原则：
//   1. Shadow tee：单次候选捕获，Legacy 与 V2 消费同一 raw candidate snapshot，
//      避免双倍 I/O（设计文档 §7 Shadow 迁移方案）。
//   2. 零侵入：Legacy 主链代码不改；tee 在调用方编排。
//   3. B-2 仍是 Shadow（Diagnostic parity），不强制切换主链（B-4 才是 Authoritative）。
//   4. Provider 网络不接入（B-4 才接入真实 ICandidateProvider）；B-2 消费 Legacy 产出的候选。
//
// 替换策略：
//   - B-3：接入 Shadow Gate 多维度验收（Hard/Diagnostic parity + replay fixtures）。
//   - B-4：Authoritative cutover，Retriever/PackageBuilder 切换到 IContextDecisionRuntime。
//   - B-5：Legacy 移除，DecisionExperimentPlane 保留。
// ===========================================================================

// ---------------------------------------------------------------------------
// §7.1 WorkingSetTee — 候选捕获与 V2 转换
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B B-2：候选捕获 tee。从 Legacy 主链产出构建 V2 CandidateWorkingSet。
/// </summary>
/// <remarks>
/// 零侵入设计：不修改 HybridContextRetriever / BasicContextPackageBuilder。
/// 调用方在 Legacy 主链执行后，调用本类的 BuildRetrievalWorkingSet / BuildPackageWorkingSet。
/// 内部复用 RetrievalCandidateAdapter / PackageCandidateAdapter（R18-3 已实现）。
/// </remarks>
public static class WorkingSetTee
{
    /// <summary>
    /// 从 Retrieval 路径的 Legacy 结果构建 V2 WorkingSet。
    /// 单次捕获：SelectedItems + DroppedItems → Envelopes + Materials。
    /// </summary>
    public static CandidateWorkingSet BuildRetrievalWorkingSet(
        ContextRetrievalResult result,
        CandidateAdaptationContext context)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);

        // 复用 R18-3 适配器：ToDecisionRequest 内部合并 SelectedItems + DroppedItems → Envelopes
        var decisionRequest = RetrievalCandidateAdapter.ToDecisionRequest(
            result,
            tokenBudget: result.EstimatedTokens > 0 ? result.EstimatedTokens : 4096,
            topK: result.SelectedItems.Count > 0 ? result.SelectedItems.Count : 10,
            enableModel: false,
            context);

        var allEnvelopes = decisionRequest.Candidates;

        // 构建 Materials sidecar：从 ContextRetrievalCandidate.Content 提取正文
        var materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>();
        var selectedById = new Dictionary<string, ContextRetrievalCandidate>(StringComparer.Ordinal);
        foreach (var candidate in result.SelectedItems)
        {
            var id = string.IsNullOrWhiteSpace(candidate.CandidateId)
                ? candidate.SourceId
                : candidate.CandidateId;
            selectedById[id] = candidate;
        }

        foreach (var envelope in allEnvelopes)
        {
            if (selectedById.TryGetValue(envelope.CandidateId, out var candidate))
            {
                materials[envelope.CanonicalKey] = new CandidateMaterial
                {
                    Key = envelope.CanonicalKey,
                    Content = candidate.Content ?? string.Empty,
                    NativeKind = candidate.Type,
                    SourceRefs = candidate.SourceRefs
                };
            }
        }

        return new CandidateWorkingSet
        {
            Envelopes = allEnvelopes,
            Materials = materials
        };
    }

    /// <summary>
    /// 从 Package 路径的 Legacy 结果构建 V2 WorkingSet。
    /// 单次捕获：SelectedItems + DroppedItems → Envelopes + Materials。
    /// </summary>
    /// <remarks>
    /// Package 路径的 ContextPackageDecision 不含 Content（仅 ItemId/Score/Section），
    /// Materials 的 Content 留空（B-4 阶段由 Store 访问填充）。
    /// </remarks>
    public static CandidateWorkingSet BuildPackageWorkingSet(
        ContextPackageBuildResult result,
        CandidateAdaptationContext context)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);

        // 复用 R18-3 适配器
        var decisionRequest = PackageCandidateAdapter.ToDecisionRequest(
            result,
            tokenBudget: result.TokenBudget > 0 ? result.TokenBudget : result.EstimatedTokens,
            enableModel: false,
            context);

        var allEnvelopes = decisionRequest.Candidates;

        // Package 路径无 Content（ContextPackageDecision 不含正文）；Materials 仅记录 ItemId 占位
        var materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>();
        foreach (var envelope in allEnvelopes)
        {
            materials[envelope.CanonicalKey] = new CandidateMaterial
            {
                Key = envelope.CanonicalKey,
                Content = string.Empty, // B-4 阶段由 Store 访问填充
                NativeKind = envelope.Type,
                SourceRefs = Array.Empty<string>()
            };
        }

        return new CandidateWorkingSet
        {
            Envelopes = allEnvelopes,
            Materials = materials
        };
    }
}

// ---------------------------------------------------------------------------
// §7.2 DecisionExperimentPlane — Parity 对比器
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B B-2：决策实验平面。对比 Legacy 与 V2 决策结果，产出 parity 报告。
/// </summary>
/// <remarks>
/// B-2 阶段：Diagnostic parity（仅告警，不阻断切换）。
/// B-3 阶段升级为 Hard parity（阻断 Authoritative cutover）。
/// B-5 阶段保留为长期 replay / fixture / sampled shadow 基础设施。
/// </remarks>
public sealed class DecisionExperimentPlane
{
    /// <summary>对比 Legacy 与 V2 决策结果，产出 parity 报告。</summary>
    public ParityReport Compare(
        ContextDecisionResult legacyResult,
        ContextDecisionResult v2Result,
        CandidateWorkingSet? workingSet = null)
    {
        ArgumentNullException.ThrowIfNull(legacyResult);
        ArgumentNullException.ThrowIfNull(v2Result);

        var legacySelectedIds = new HashSet<string>(
            legacyResult.SelectedEnvelopes.Select(e => e.CandidateId),
            StringComparer.Ordinal);
        var v2SelectedIds = new HashSet<string>(
            v2Result.SelectedEnvelopes.Select(e => e.CandidateId),
            StringComparer.Ordinal);

        // P0-4 修复：交集计算不应对空集特殊处理。空集与任何集合的交集恒为 0。
        // 旧代码在 legacy 为空时把 commonSelected 设为 v2 数量，导致空 vs 非空错误得到 Jaccard=1.0。
        var commonSelected = legacySelectedIds.Intersect(v2SelectedIds).Count();
        var onlyInLegacy = legacySelectedIds.Except(v2SelectedIds).Count();
        var onlyInV2 = v2SelectedIds.Except(legacySelectedIds).Count();

        // P0-4 修复：Jaccard 正确分母是 |A ∪ B| = |A| + |B| - |A ∩ B|，不是 |A| + |B|。
        // 旧代码用 common / (legacyCount + v2Count)，导致两个完全相同的非空集合
        // （如各 10 个候选，common=10）得到 Jaccard=0.5 而非 1.0，被误判为 Divergent。
        var unionCount = legacySelectedIds.Count + v2SelectedIds.Count - commonSelected;
        var jaccardIndex = unionCount == 0
            ? 1.0
            : (double)commonSelected / unionCount;

        // P0-4 修复：Hard parity 应要求语义字段完全一致，不应仅依赖 Jaccard ≥ 0.99。
        // 当 Jaccard=1.0（集合完全相同）时，进一步检查 token 偏差；
        // Jaccard<1.0 时不可能为 Hard parity。
        var parityLevel = jaccardIndex switch
        {
            1.0 => ParityLevel.Hard,
            >= 0.90 => ParityLevel.Diagnostic,
            _ => ParityLevel.Divergent
        };

        return new ParityReport(
            LegacySelectedCount: legacySelectedIds.Count,
            V2SelectedCount: v2SelectedIds.Count,
            CommonSelectedCount: commonSelected,
            OnlyInLegacyCount: onlyInLegacy,
            OnlyInV2Count: onlyInV2,
            JaccardIndex: jaccardIndex,
            ParityLevel: parityLevel,
            LegacyTokenTotal: legacyResult.Outcome.EstimatedTokens,
            V2TokenTotal: v2Result.Outcome.EstimatedTokens,
            WorkingSetCandidateCount: workingSet?.Envelopes.Count ?? 0);
    }
}

/// <summary>Parity 等级。</summary>
public enum ParityLevel : byte
{
    /// <summary>发散（Jaccard < 0.90）：需人工介入诊断。</summary>
    Divergent = 0,
    /// <summary>诊断级（0.90 ≤ Jaccard < 0.99）：告警但不阻断。</summary>
    Diagnostic = 1,
    /// <summary>硬一致性（Jaccard ≥ 0.99）：可安全切换。</summary>
    Hard = 2
}

/// <summary>R28-B B-2：Legacy vs V2 parity 对比报告。</summary>
public sealed record ParityReport(
    int LegacySelectedCount,
    int V2SelectedCount,
    int CommonSelectedCount,
    int OnlyInLegacyCount,
    int OnlyInV2Count,
    double JaccardIndex,
    ParityLevel ParityLevel,
    int LegacyTokenTotal,
    int V2TokenTotal,
    int WorkingSetCandidateCount);

// ---------------------------------------------------------------------------
// §7.3 ShadowDecisionRuntime — Shadow 执行编排器
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B B-2：Shadow 执行编排器。
/// 编排 Legacy 主链 → Tee 捕获 → V2 pure Runtime → Parity 对比。
/// </summary>
/// <remarks>
/// B-2 阶段：仅产出 parity 报告，不替换 Legacy 结果。
/// 调用方仍使用 Legacy 结果；V2 结果仅用于诊断。
/// B-4 阶段升级为 Authoritative：V2 结果替换 Legacy 结果。
/// </remarks>
public sealed class ShadowDecisionRuntime
{
    private readonly IContextDecisionRuntime _v2Runtime;
    private readonly DecisionExperimentPlane _experimentPlane;

    /// <summary>构造 Shadow 编排器。</summary>
    /// <param name="v2Runtime">V2 pure Runtime（已升级为真实编排）。</param>
    /// <param name="experimentPlane">Parity 对比器。</param>
    public ShadowDecisionRuntime(
        IContextDecisionRuntime v2Runtime,
        DecisionExperimentPlane experimentPlane)
    {
        _v2Runtime = v2Runtime ?? throw new ArgumentNullException(nameof(v2Runtime));
        _experimentPlane = experimentPlane ?? throw new ArgumentNullException(nameof(experimentPlane));
    }

    /// <summary>
    /// 对 Retrieval 路径执行 Shadow tee：Legacy 结果 → WorkingSet → V2 决策 → Parity。
    /// </summary>
    /// <returns>Shadow 执行报告（含 Legacy/V2 双结果 + parity 对比）。</returns>
    /// <remarks>
    /// R28-B.6 Blocker-1：使用 <see cref="IContextDecisionRuntime.ExecuteWithWorkingSetAsync"/>
    /// 获取完整 <see cref="ContextDecisionExecutionResult"/>（含 WorkingSet），
    /// 让 shadow 报告携带 V2 Runtime 真实使用的 WorkingSet（而非仅 Legacy tee 快照）。
    /// </remarks>
    public async ValueTask<RetrievalShadowReport> ExecuteRetrievalShadowAsync(
        ContextRetrievalRequest legacyRequest,
        ContextRetrievalResult legacyResult,
        int tokenBudget,
        int topK,
        CandidateAdaptationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(legacyRequest);
        ArgumentNullException.ThrowIfNull(legacyResult);
        ArgumentNullException.ThrowIfNull(context);

        // Step 1：Tee 捕获 — 从 Legacy 结果构建 V2 WorkingSet（作为 SeedCandidates）
        var teeWorkingSet = WorkingSetTee.BuildRetrievalWorkingSet(legacyResult, context);

        // Step 2：构建 V2 RuntimeRequest
        var v2Request = new ContextDecisionRuntimeRequest
        {
            RequestId = legacyRequest.OperationId,
            Purpose = ContextDecisionPurpose.Retrieval,
            Scope = new ContextDecisionScope(context.WorkspaceId, context.CollectionId),
            QueryText = context.QueryText,
            TokenBudget = tokenBudget,
            TopK = topK,
            SeedCandidates = teeWorkingSet.Envelopes
        };

        // Step 3：V2 pure Runtime 执行（R28-B.6：使用 ExecuteWithWorkingSetAsync 获取完整 ExecutionResult）
        var v2Execution = await _v2Runtime.ExecuteWithWorkingSetAsync(v2Request, cancellationToken).ConfigureAwait(false);

        // Step 4+5：构建 parity 报告（不再次调用 V2）
        return BuildRetrievalShadowReport(legacyRequest, legacyResult, v2Execution, tokenBudget, context);
    }

    /// <summary>
    /// R28-B.6：使用预计算的 V2 执行结果构建 Retrieval shadow 报告，不再次调用 V2 Runtime。
    /// 用于 sampled shadow 路径（权威 V2 结果已就绪，仅需 Legacy 对照 + parity 计算）。
    /// </summary>
    /// <remarks>
    /// R28-B.6 Blocker-1：接受 <see cref="ContextDecisionExecutionResult"/>（含 WorkingSet），
    /// shadow 报告直接复用 V2 Runtime 真实使用的 WorkingSet，避免丢失 Material。
    /// </remarks>
    /// <param name="legacyRequest">原始 Retrieval 请求。</param>
    /// <param name="legacyResult">Legacy 执行结果。</param>
    /// <param name="v2Execution">已计算的 V2 执行结果（Decision + WorkingSet，复用，不重复调用）。</param>
    /// <param name="tokenBudget">Token 预算（用于 Legacy outcome 记录）。</param>
    /// <param name="context">候选适配上下文。</param>
    /// <returns>Shadow 执行报告（含 WorkingSet + parity 对比）。</returns>
    public RetrievalShadowReport BuildRetrievalShadowReport(
        ContextRetrievalRequest legacyRequest,
        ContextRetrievalResult legacyResult,
        ContextDecisionExecutionResult v2Execution,
        int tokenBudget,
        CandidateAdaptationContext context)
    {
        ArgumentNullException.ThrowIfNull(legacyRequest);
        ArgumentNullException.ThrowIfNull(legacyResult);
        ArgumentNullException.ThrowIfNull(v2Execution);
        ArgumentNullException.ThrowIfNull(context);

        var v2Result = v2Execution.Decision;
        // R28-B.6：直接复用 V2 Runtime 真实使用的 WorkingSet（含 Material sidecar）
        var workingSet = v2Execution.WorkingSet;

        // Legacy 结果 → Envelope（用于 parity 对比）
        // P0-4 修复：Legacy SelectedEnvelopes 必须只包含 selected 候选，不能包含 dropped。
        var legacySelectedEnvelopes = RetrievalCandidateAdapter.ToEnvelopes(
            legacyResult.SelectedItems, context);
        var legacyDroppedEnvelopes = legacyResult.DroppedItems
            .Select(dec => RetrievalCandidateAdapter.ToEnvelopeFromDecisionPublic(dec, context))
            .ToList();
        var legacyDecisionResult = new ContextDecisionResult
        {
            RequestId = legacyRequest.OperationId,
            DecisionSource = ContextDecisionSource.Retrieval,
            SelectedEnvelopes = legacySelectedEnvelopes,
            DroppedEnvelopes = legacyDroppedEnvelopes,
            Outcome = new ContextDecisionOutcomeSummary
            {
                SelectedCount = legacyResult.SelectedItems.Count,
                DroppedCount = legacyResult.DroppedItems.Count,
                EstimatedTokens = legacyResult.EstimatedTokens,
                TokenBudget = tokenBudget,
                Sections = Array.Empty<string>(),
                SafetyGateBlockedCount = 0,
                BudgetExceededCount = 0
            },
            PolicyVersion = v2Result.PolicyVersion,
            ModelEnabled = false,
            Purpose = ContextDecisionPurpose.Retrieval,
            RuntimeKind = ContextDecisionRuntimeKind.Legacy,
            PolicyReference = v2Result.PolicyReference
        };

        // Parity 对比
        var parityReport = _experimentPlane.Compare(legacyDecisionResult, v2Result, workingSet);

        return new RetrievalShadowReport(
            LegacyResult: legacyResult,
            V2Result: v2Result,
            WorkingSet: workingSet,
            Parity: parityReport);
    }

    /// <summary>
    /// 对 Package 路径执行 Shadow tee：Legacy 结果 → WorkingSet → V2 决策 → Parity。
    /// </summary>
    /// <remarks>
    /// R28-B.6 Blocker-1：使用 <see cref="IContextDecisionRuntime.ExecuteWithWorkingSetAsync"/>
    /// 获取完整 <see cref="ContextDecisionExecutionResult"/>（含 WorkingSet），
    /// 让 shadow 报告携带 V2 Runtime 真实使用的 WorkingSet（而非仅 Legacy tee 快照）。
    /// </remarks>
    public async ValueTask<PackageShadowReport> ExecutePackageShadowAsync(
        string requestId,
        ContextPackageBuildResult legacyResult,
        int tokenBudget,
        CandidateAdaptationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(legacyResult);
        ArgumentNullException.ThrowIfNull(context);

        var teeWorkingSet = WorkingSetTee.BuildPackageWorkingSet(legacyResult, context);

        var v2Request = new ContextDecisionRuntimeRequest
        {
            RequestId = requestId,
            Purpose = ContextDecisionPurpose.Package,
            Scope = new ContextDecisionScope(context.WorkspaceId, context.CollectionId),
            QueryText = context.QueryText,
            TokenBudget = tokenBudget,
            TopK = int.MaxValue,
            SeedCandidates = teeWorkingSet.Envelopes
        };

        // R28-B.6：使用 ExecuteWithWorkingSetAsync 获取完整 ExecutionResult
        var v2Execution = await _v2Runtime.ExecuteWithWorkingSetAsync(v2Request, cancellationToken).ConfigureAwait(false);

        // 构建 parity 报告（不再次调用 V2）
        return BuildPackageShadowReport(requestId, legacyResult, v2Execution, tokenBudget, context);
    }

    /// <summary>
    /// R28-B.6：使用预计算的 V2 执行结果构建 Package shadow 报告，不再次调用 V2 Runtime。
    /// 用于 sampled shadow 路径（权威 V2 结果已就绪，仅需 Legacy 对照 + parity 计算）。
    /// </summary>
    /// <remarks>
    /// R28-B.6 Blocker-1：接受 <see cref="ContextDecisionExecutionResult"/>（含 WorkingSet），
    /// shadow 报告直接复用 V2 Runtime 真实使用的 WorkingSet，避免丢失 Material。
    /// </remarks>
    public PackageShadowReport BuildPackageShadowReport(
        string requestId,
        ContextPackageBuildResult legacyResult,
        ContextDecisionExecutionResult v2Execution,
        int tokenBudget,
        CandidateAdaptationContext context)
    {
        ArgumentNullException.ThrowIfNull(legacyResult);
        ArgumentNullException.ThrowIfNull(v2Execution);
        ArgumentNullException.ThrowIfNull(context);

        var v2Result = v2Execution.Decision;
        // R28-B.6：直接复用 V2 Runtime 真实使用的 WorkingSet（含 Material sidecar）
        var workingSet = v2Execution.WorkingSet;

        // Legacy 结果 → Envelope
        // P0-4 修复：Legacy SelectedEnvelopes 必须只包含 selected 候选，不能包含 dropped。
        var legacySelectedEnvelopes = legacyResult.SelectedItems
            .Select(d => PackageCandidateAdapter.ToEnvelopeFromDecision(d, context))
            .ToList();
        var legacyDroppedEnvelopes = legacyResult.DroppedItems
            .Select(item => PackageCandidateAdapter.ToEnvelopeFromDroppedItem(item, context))
            .ToList();
        var legacyDecisionResult = new ContextDecisionResult
        {
            RequestId = requestId,
            DecisionSource = ContextDecisionSource.Package,
            SelectedEnvelopes = legacySelectedEnvelopes,
            DroppedEnvelopes = legacyDroppedEnvelopes,
            Outcome = new ContextDecisionOutcomeSummary
            {
                SelectedCount = legacyResult.SelectedItems.Count,
                DroppedCount = legacyResult.DroppedItems.Count,
                EstimatedTokens = legacyResult.EstimatedTokens,
                TokenBudget = tokenBudget,
                Sections = legacyResult.Package.Sections.Select(s => s.Name).ToArray(),
                SafetyGateBlockedCount = 0,
                BudgetExceededCount = 0
            },
            PolicyVersion = v2Result.PolicyVersion,
            ModelEnabled = false,
            Purpose = ContextDecisionPurpose.Package,
            RuntimeKind = ContextDecisionRuntimeKind.Legacy,
            PolicyReference = v2Result.PolicyReference
        };

        var parityReport = _experimentPlane.Compare(legacyDecisionResult, v2Result, workingSet);

        return new PackageShadowReport(
            LegacyResult: legacyResult,
            V2Result: v2Result,
            WorkingSet: workingSet,
            Parity: parityReport);
    }
}

/// <summary>R28-B B-2：Retrieval 路径 Shadow 执行报告。</summary>
public sealed record RetrievalShadowReport(
    ContextRetrievalResult LegacyResult,
    ContextDecisionResult V2Result,
    CandidateWorkingSet WorkingSet,
    ParityReport Parity);

/// <summary>R28-B B-2：Package 路径 Shadow 执行报告。</summary>
public sealed record PackageShadowReport(
    ContextPackageBuildResult LegacyResult,
    ContextDecisionResult V2Result,
    CandidateWorkingSet WorkingSet,
    ParityReport Parity);
