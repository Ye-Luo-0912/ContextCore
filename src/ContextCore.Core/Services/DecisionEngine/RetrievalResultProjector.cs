using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// R18-2：Retrieval 结果投影器
//
// 将 ContextDecisionResult 投影为 ContextRetrievalResult，作为 Engine 输出
// 与现有 Retrieval 主链出口 DTO 之间的桥梁。R18-2 阶段仅做格式投影，
// 不改变决策结果（envelope 集合不变）。
//
// 设计原则：
//   1. Projector 仅做格式投影，不调用 Engine 或 Storage；纯内存转换。
//   2. Projector 是幂等的：相同 Result 产生相同 DTO。
//   3. envelope.CandidateId → ContextRetrievalCandidate.CandidateId
//      envelope.Utility.FinalScore → ContextRetrievalCandidate.Score
//      envelope.Safety.BlockReasonCode → ContextRetrievalDecision.Reason（自由文本兼容）
// ===========================================================================

/// <summary>
/// R18-2：Retrieval 结果投影器。将 Engine 输出的 envelope 集合投影为
/// <see cref="ContextRetrievalResult"/>，保持与现有 Retrieval 主链出口 DTO 兼容。
/// </summary>
public sealed class RetrievalResultProjector : IResultProjector<ContextRetrievalResult>
{
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
            EstimatedTokens = result.Outcome.EstimatedTokens,
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
    /// P0-7：将决策结果 + 候选正文 sidecar 投影为 ContextRetrievalResult。
    /// 从 workingSet.Materials 恢复候选 Content；从 result.AllocationDecisions
    /// 消费 Section / IncludedTokens / IsTruncated（如有）。
    /// </summary>
    public ContextRetrievalResult Project(ContextDecisionResult result, CandidateWorkingSet workingSet)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(workingSet);

        // P0-7：构建 CanonicalKey → AllocationDecision 索引，用于恢复 Section / IncludedTokens
        var allocationByKey = result.AllocationDecisions
            .ToDictionary(d => d.CandidateKey, d => d);

        var selectedItems = result.SelectedEnvelopes
            .Select(env => ProjectToRetrievalCandidateWithMaterial(env, workingSet, allocationByKey))
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
            EstimatedTokens = result.Outcome.EstimatedTokens,
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

    private static ContextRetrievalCandidate ProjectToRetrievalCandidateWithMaterial(
        ContextCandidateEnvelope envelope,
        CandidateWorkingSet workingSet,
        IReadOnlyDictionary<CanonicalCandidateKey, CandidateAllocationDecision> allocationByKey)
    {
        // P0-7：从 Material sidecar 恢复 Content
        string content = string.Empty;
        if (workingSet.Materials.TryGetValue(envelope.CanonicalKey, out var material))
        {
            content = material.Content;
        }

        // P0-7：从 AllocationDecision 恢复 IncludedTokens / IsTruncated（如有）
        var includedTokens = envelope.EstimatedTokens;
        var isTruncated = false;
        if (allocationByKey.TryGetValue(envelope.CanonicalKey, out var decision))
        {
            includedTokens = decision.IncludedTokens;
            isTruncated = decision.IsTruncated;
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
            EstimatedTokens = envelope.EstimatedTokens,
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
            EstimatedTokens = envelope.EstimatedTokens
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
