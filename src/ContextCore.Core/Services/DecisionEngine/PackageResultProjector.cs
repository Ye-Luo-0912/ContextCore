using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// R18-2：Package 结果投影器
//
// 将 ContextDecisionResult 投影为 ContextPackageBuildResult，作为 Engine 输出
// 与现有 Package 主链出口 DTO 之间的桥梁。R18-2 阶段仅做格式投影，
// 不改变决策结果（envelope 集合不变）。
//
// 设计原则：
//   1. Projector 仅做格式投影，不调用 Engine 或 Storage；纯内存转换。
//   2. Projector 是幂等的：相同 Result 产生相同 DTO。
//   3. envelope.CandidateId → ContextPackageDecision.ItemId
//      envelope.Utility.FinalScore → ContextPackageDecision.Score
//      envelope.Safety.BlockReasonCode → DroppedContextItem.Reason（自由文本兼容）
//      envelope.Source → ContextPackageDecision.Kind（字符串映射）
// ===========================================================================

/// <summary>
/// R18-2：Package 结果投影器。将 Engine 输出的 envelope 集合投影为
/// <see cref="ContextPackageBuildResult"/>，保持与现有 Package 主链出口 DTO 兼容。
/// </summary>
public sealed class PackageResultProjector : IResultProjector<ContextPackageBuildResult>
{
    /// <summary>
    /// 将决策结果投影为 ContextPackageBuildResult。
    /// </summary>
    public ContextPackageBuildResult Project(ContextDecisionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var selectedItems = result.SelectedEnvelopes
            .Select(ProjectToPackageDecision)
            .ToList();

        var droppedItems = result.DroppedEnvelopes
            .Select(ProjectToDroppedItem)
            .ToList();

        return new ContextPackageBuildResult
        {
            BuildId = result.RequestId,
            SelectedItems = selectedItems,
            DroppedItems = droppedItems,
            Metadata = new Dictionary<string, string>
            {
                ["policyVersion"] = result.PolicyVersion,
                ["modelEnabled"] = result.ModelEnabled.ToString().ToLowerInvariant(),
                ["safetyGateBlocked"] = result.Outcome.SafetyGateBlockedCount.ToString(),
                ["budgetExceeded"] = result.Outcome.BudgetExceededCount.ToString()
            }
        };
    }

    private static ContextPackageDecision ProjectToPackageDecision(ContextCandidateEnvelope envelope)
    {
        return new ContextPackageDecision
        {
            ItemId = envelope.CandidateId,
            Kind = ResolveKindString(envelope.Source),
            Type = envelope.Type,
            SectionName = ResolveSectionName(envelope.Source),
            Reason = ResolveSelectReason(envelope),
            Score = envelope.Utility.FinalScore,
            EstimatedTokens = envelope.EstimatedTokens,
            SourceRefs = envelope.ProvenanceRefs
                .Where(r => !string.IsNullOrEmpty(r.RefId))
                .Select(r => r.RefId)
                .ToList()
        };
    }

    private static DroppedContextItem ProjectToDroppedItem(ContextCandidateEnvelope envelope)
    {
        return new DroppedContextItem
        {
            ItemId = envelope.CandidateId,
            Kind = ResolveKindString(envelope.Source),
            Reason = ResolveDropReason(envelope)
        };
    }

    private static string ResolveKindString(ContextCandidateSource source) => source switch
    {
        ContextCandidateSource.Mandatory => "hard_constraint",
        ContextCandidateSource.Constraint => "hard_constraint",
        ContextCandidateSource.WorkingMemory => "working_memory",
        ContextCandidateSource.StableMemory => "stable_memory",
        ContextCandidateSource.Lexical or ContextCandidateSource.Semantic or
        ContextCandidateSource.Recency => "recent_context",
        ContextCandidateSource.Graph => "related_context",
        ContextCandidateSource.GlobalContext => "global_context",
        ContextCandidateSource.RelatedContext => "related_context",
        _ => "raw"
    };

    private static string ResolveSectionName(ContextCandidateSource source) => source switch
    {
        ContextCandidateSource.Mandatory or ContextCandidateSource.Constraint => "hard_constraints",
        ContextCandidateSource.WorkingMemory => "working_memory",
        ContextCandidateSource.StableMemory => "stable_memory",
        ContextCandidateSource.Lexical or ContextCandidateSource.Semantic or
        ContextCandidateSource.Recency => "recent_context",
        ContextCandidateSource.Graph or ContextCandidateSource.RelatedContext => "related_context",
        ContextCandidateSource.GlobalContext => "global_context",
        _ => "recent_context"
    };

    private static string ResolveSelectReason(ContextCandidateEnvelope envelope)
    {
        // Source=Mandatory / Constraint 视为 mandatory 类候选；
        // Safety.IsMandatory / IsHardConstraint 同样视为 mandatory 类。
        if (envelope.Source == ContextCandidateSource.Mandatory || envelope.Safety.IsMandatory) return "mandatory";
        if (envelope.Source == ContextCandidateSource.Constraint || envelope.Safety.IsHardConstraint) return "hard constraint";
        return envelope.Utility.ModelScore.HasValue ? "model-weighted" : "selected by utility";
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
