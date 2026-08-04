using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// Retrieval 结果投影器
//
// 将 ContextDecisionResult 投影为 ContextRetrievalResult，作为 Engine 输出
// 与现有 Retrieval 主链出口 DTO 之间的桥梁。 阶段仅做格式投影，
// 不改变决策结果（envelope 集合不变）。
//
// 设计原则：
// 1. Projector 仅做格式投影，不调用 Engine 或 Storage；纯内存转换。
// 2. Projector 是幂等的：相同 Result 产生相同 DTO。
// 3. envelope.CandidateId → ContextRetrievalCandidate.CandidateId
// envelope.Utility.FinalScore → ContextRetrievalCandidate.Score
// envelope.Safety.BlockReasonCode → ContextRetrievalDecision.Reason（自由文本兼容）
// ===========================================================================

/// <summary>
/// Retrieval 结果投影器。将 Engine 输出的 envelope 集合投影为
/// <see cref="ContextRetrievalResult"/>，保持与现有 Retrieval 主链出口 DTO 兼容。
/// </summary>
/// <remarks>
/// 当 AllocationDecision.IsTruncated=true 时，使用 IContentTruncator
/// 真正截断 Material.Content，并重新计算 ActualTokens。
/// </remarks>
public sealed class RetrievalResultProjector : IResultProjector<ContextRetrievalResult>
{
    private readonly IContentTruncator _contentTruncator;

    /// <summary>
    /// 构造 RetrievalResultProjector。
    /// </summary>
    /// <param name="contentTruncator">
    /// 内容截断器。null 时回退到 tokenizerResolver 或 <see cref="DefaultContentTruncator"/>。
    /// </param>
    /// <param name="tokenizerResolver">
    /// tokenizer 解析器（可选）。contentTruncator 为 null 且 tokenizerResolver 非空时，
    /// 使用 <see cref="TokenizerContentTruncator"/>（真正按 BPE/CJK 截断）。
    /// </param>
    /// <param name="modelName">tokenizer 使用的模型名（可选）。</param>
    public RetrievalResultProjector(
        IContentTruncator? contentTruncator = null,
        IContextTokenizerResolver? tokenizerResolver = null,
        string? modelName = null)
    {
        // 优先级 contentTruncator > tokenizerResolver > DefaultContentTruncator
        _contentTruncator = contentTruncator
            ?? (tokenizerResolver is not null
                ? new TokenizerContentTruncator(tokenizerResolver, modelName)
                : new DefaultContentTruncator());
    }

    /// <summary>
    /// 将决策结果投影为 ContextRetrievalResult。
    /// </summary>
    public ContextRetrievalResult Project(ContextDecisionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var selectedItems = result.SelectedEnvelopes
            .Select(ProjectToRetrievalCandidate)
            .ToList();

        var droppedItems = result.DroppedEnvelopes
            .Select(ProjectToRetrievalDecision)
            .ToList();

        return new ContextRetrievalResult
        {
            OperationId = result.RequestId,
            Succeeded = true,
            SelectedItems = selectedItems,
            DroppedItems = droppedItems,
            EstimatedTokens = result.Outcome.EffectiveTokens,
            CreatedAt = result.DecidedAt,
            Metadata = new Dictionary<string, string>
            {
                ["policyVersion"] = result.PolicyVersion,
                ["modelEnabled"] = result.ModelEnabled.ToString().ToLowerInvariant(),
                ["safetyGateBlocked"] = result.Outcome.SafetyGateBlockedCount.ToString(),
                ["budgetExceeded"] = result.Outcome.BudgetExceededCount.ToString()
            }
        };
    }

    /// <summary>
    /// 从完整执行结果投影为 ContextRetrievalResult。
    /// </summary>
    /// <remarks>
    /// 便捷重载：从 execution 提取 Decision + WorkingSet，委托到
    /// <see cref="Project(ContextDecisionResult, CandidateWorkingSet)"/>。
    /// 确保 Projector 始终从 WorkingSet 恢复 Material 正文，不丢失候选内容。
    /// </remarks>
    public ContextRetrievalResult Project(ContextDecisionExecutionResult execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        return Project(execution.Decision, execution.WorkingSet);
    }

    /// <summary>
    /// 将决策结果 + 候选正文 sidecar 投影为 ContextRetrievalResult。
    /// 从 workingSet.Materials 恢复候选 Content；从 result.AllocationDecisions
    /// 消费 Section / IncludedTokens / IsTruncated（如有）。
    /// 当 IsTruncated=true 时，使用 IContentTruncator 截断 Content 并重算 ActualTokens。
    /// </summary>
    public ContextRetrievalResult Project(ContextDecisionResult result, CandidateWorkingSet workingSet)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(workingSet);

        // 构建 CanonicalKey → AllocationDecision 索引，用于恢复 Section / IncludedTokens
        var allocationByKey = result.AllocationDecisions
            .ToDictionary(d => d.CandidateKey, d => d);

        var selectedItems = result.SelectedEnvelopes
            .Select(env => ProjectToRetrievalCandidateWithMaterial(env, workingSet, allocationByKey))
            .ToList();

        var droppedItems = result.DroppedEnvelopes
            .Select(ProjectToRetrievalDecision)
            .ToList();

        // 重算总 token 数（含 section 分隔符 token）
        // Engine Outcome.EstimatedTokens 仅含候选 token 之和，不计分隔符；
        // Projector 输出时附加分隔符 token（每个候选间一个 "\n---\n" 约 3 token）。
        var candidateTokenSum = selectedItems.Sum(c => c.EstimatedTokens);
        var totalTokensWithSeparators = TokenCostHelper.CountWithSeparators(
            candidateTokenSum, selectedItems.Count);

        // 传播 Engine Outcome.Diagnostics 到输出 Metadata（不丢失诊断）。
        // 诊断键加 "diag." 前缀以避免与既有 Metadata 键冲突。
        var metadata = new Dictionary<string, string>
        {
            ["policyVersion"] = result.PolicyVersion,
            ["modelEnabled"] = result.ModelEnabled.ToString().ToLowerInvariant(),
            ["safetyGateBlocked"] = result.Outcome.SafetyGateBlockedCount.ToString(),
            ["budgetExceeded"] = result.Outcome.BudgetExceededCount.ToString()
        };
        foreach (var (key, value) in result.Outcome.Diagnostics)
        {
            metadata[$"diag.{key}"] = value;
        }

        return new ContextRetrievalResult
        {
            OperationId = result.RequestId,
            Succeeded = true,
            SelectedItems = selectedItems,
            DroppedItems = droppedItems,
            // 使用含分隔符的总 token 数（比 Engine Outcome 更精确）
            EstimatedTokens = totalTokensWithSeparators,
            CreatedAt = result.DecidedAt,
            Metadata = metadata
        };
    }

    private ContextRetrievalCandidate ProjectToRetrievalCandidateWithMaterial(
        ContextCandidateEnvelope envelope,
        CandidateWorkingSet workingSet,
        IReadOnlyDictionary<CanonicalCandidateKey, CandidateAllocationDecision> allocationByKey)
    {
        // 从 Material sidecar 恢复 Content
        string content = string.Empty;
        if (workingSet.Materials.TryGetValue(envelope.CanonicalKey, out var material))
        {
            content = material.Content;
        }

        // 从 AllocationDecision 恢复 IncludedTokens / IsTruncated（如有）
        // 优先使用 TokenCost.ContentTokens（精确 token 计数），回退到 EstimatedTokens
        var includedTokens = DecisionOutcomeRecomputer.GetEffectiveTokens(envelope);
        var isTruncated = false;
        if (allocationByKey.TryGetValue(envelope.CanonicalKey, out var decision))
        {
            includedTokens = decision.IncludedTokens;
            isTruncated = decision.IsTruncated;
        }

        // 当 IsTruncated=true 且有 Material 时，真正截断 Content 并重算 ActualTokens
        if (isTruncated && !string.IsNullOrEmpty(content) && includedTokens > 0)
        {
            var truncation = _contentTruncator.Truncate(content, includedTokens);
            content = truncation.TruncatedContent;
            includedTokens = truncation.ActualTokens;
        }

        var reasons = ResolveReasons(envelope);
        if (isTruncated)
        {
            reasons = new List<string>(reasons) { "truncated" }.AsReadOnly();
        }

        return new ContextRetrievalCandidate
        {
            CandidateId = envelope.CandidateId,
            SourceId = envelope.CandidateId,
            Kind = ResolveCandidateKind(envelope.Source),
            Type = envelope.Type,
            Score = envelope.Utility.FinalScore,
            EstimatedTokens = includedTokens,
            Content = content,
            Reasons = reasons,
            SourceRefs = envelope.ProvenanceRefs
                .Where(r => !string.IsNullOrEmpty(r.RefId))
                .Select(r => r.RefId)
                .ToList()
        };
    }

    private static ContextRetrievalCandidate ProjectToRetrievalCandidate(ContextCandidateEnvelope envelope)
    {
        return new ContextRetrievalCandidate
        {
            CandidateId = envelope.CandidateId,
            SourceId = envelope.CandidateId, // envelope 统一身份；SourceId 保持一致
            Kind = ResolveCandidateKind(envelope.Source),
            Type = envelope.Type,
            Score = envelope.Utility.FinalScore,
            EstimatedTokens = DecisionOutcomeRecomputer.GetEffectiveTokens(envelope),
            Reasons = ResolveReasons(envelope),
            SourceRefs = envelope.ProvenanceRefs
                .Where(r => !string.IsNullOrEmpty(r.RefId))
                .Select(r => r.RefId)
                .ToList()
        };
    }

    private static ContextRetrievalDecision ProjectToRetrievalDecision(ContextCandidateEnvelope envelope)
    {
        return new ContextRetrievalDecision
        {
            CandidateId = envelope.CandidateId,
            SourceId = envelope.CandidateId,
            Kind = ResolveCandidateKind(envelope.Source),
            Type = envelope.Type,
            Reason = ResolveDropReason(envelope),
            Score = envelope.Utility.FinalScore,
            EstimatedTokens = DecisionOutcomeRecomputer.GetEffectiveTokens(envelope)
        };
    }

    private static ContextRetrievalCandidateKind ResolveCandidateKind(ContextCandidateSource source) => source switch
    {
        ContextCandidateSource.Mandatory or ContextCandidateSource.Constraint =>
            ContextRetrievalCandidateKind.ContextItem,
        ContextCandidateSource.Lexical or ContextCandidateSource.Semantic or
        ContextCandidateSource.Recency or ContextCandidateSource.GlobalContext or
        ContextCandidateSource.RelatedContext =>
            ContextRetrievalCandidateKind.ContextItem,
        ContextCandidateSource.WorkingMemory or ContextCandidateSource.StableMemory =>
            ContextRetrievalCandidateKind.MemoryItem,
        ContextCandidateSource.Graph =>
            ContextRetrievalCandidateKind.ContextItem,
        _ => ContextRetrievalCandidateKind.ContextItem
    };

    private static IReadOnlyList<string> ResolveReasons(ContextCandidateEnvelope envelope)
    {
        var reasons = new List<string>(2);
        if (envelope.Safety.IsMandatory) reasons.Add("mandatory");
        if (envelope.Utility.ModelScore.HasValue) reasons.Add($"model:{envelope.Utility.ModelArtifactRef}");
        return reasons;
    }

    private static string ResolveDropReason(ContextCandidateEnvelope envelope)
    {
        if (!envelope.Safety.PassesSafetyGate)
        {
            var code = envelope.Safety.BlockReasonCode;
            var detail = envelope.Safety.BlockReasonDetail;
            return string.IsNullOrEmpty(detail) ? code.ToString() : $"{code}: {detail}";
        }
        return "budget exceeded";
    }
}
