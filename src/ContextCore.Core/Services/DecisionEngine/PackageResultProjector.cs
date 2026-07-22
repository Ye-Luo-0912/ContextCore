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

    /// <summary>
    /// P0-7：将决策结果 + 候选正文 sidecar 投影为 ContextPackageBuildResult。
    /// 从 workingSet.Materials 恢复候选 Content；从 result.AllocationDecisions
    /// 消费 Section / IncludedTokens / IsTruncated 构建 section + token 分配。
    /// </summary>
    public ContextPackageBuildResult Project(ContextDecisionResult result, CandidateWorkingSet workingSet)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(workingSet);

        // P0-7：构建 CanonicalKey → AllocationDecision 索引
        var allocationByKey = result.AllocationDecisions
            .ToDictionary(d => d.CandidateKey, d => d);

        var selectedItems = result.SelectedEnvelopes
            .Select(env => ProjectToPackageDecisionWithMaterial(env, workingSet, allocationByKey))
            .ToList();

        var droppedItems = result.DroppedEnvelopes
            .Select(ProjectToDroppedItem)
            .ToList();

        // P0-7：从 AllocationDecisions 构建完整 section 列表 + token 分配
        var sectionBudgets = result.AllocationDecisions
            .GroupBy(d => d.Section)
            .Select(g => new ContextPackageSectionBudget
            {
                SectionName = g.Key,
                AllocatedTokens = g.Sum(d => d.IncludedTokens),
                UsedTokens = g.Sum(d => d.IncludedTokens),
                UsageRatio = result.Outcome.TokenBudget > 0
                    ? (double)g.Sum(d => d.IncludedTokens) / result.Outcome.TokenBudget
                    : 0
            })
            .ToList();

        return new ContextPackageBuildResult
        {
            BuildId = result.RequestId,
            SelectedItems = selectedItems,
            DroppedItems = droppedItems,
            Budget = new ContextPackageBudgetReport
            {
                TokenBudget = result.Outcome.TokenBudget,
                UsedTokens = result.Outcome.EstimatedTokens,
                RemainingTokens = Math.Max(0, result.Outcome.TokenBudget - result.Outcome.EstimatedTokens),
                UsageRatio = result.Outcome.TokenBudget > 0
                    ? (double)result.Outcome.EstimatedTokens / result.Outcome.TokenBudget
                    : 0,
                Sections = sectionBudgets
            },
            Metadata = new Dictionary<string, string>
            {
                ["policyVersion"] = result.PolicyVersion,
                ["modelEnabled"] = result.ModelEnabled.ToString().ToLowerInvariant(),
                ["safetyGateBlocked"] = result.Outcome.SafetyGateBlockedCount.ToString(),
                ["budgetExceeded"] = result.Outcome.BudgetExceededCount.ToString()
            }
        };
    }

    private static ContextPackageDecision ProjectToPackageDecisionWithMaterial(
        ContextCandidateEnvelope envelope,
        CandidateWorkingSet workingSet,
        IReadOnlyDictionary<CanonicalCandidateKey, CandidateAllocationDecision> allocationByKey)
    {
        // P0-7：从 AllocationDecision 消费 Section / IncludedTokens / IsTruncated
        var section = ResolveSectionName(envelope.Source);
        var includedTokens = envelope.EstimatedTokens;
        var isTruncated = false;
        if (allocationByKey.TryGetValue(envelope.CanonicalKey, out var decision))
        {
            section = decision.Section;
            includedTokens = decision.IncludedTokens;
            isTruncated = decision.IsTruncated;
        }

        // P0-7：从 Material sidecar 恢复候选 Content
        string content = string.Empty;
        if (workingSet.Materials.TryGetValue(envelope.CanonicalKey, out var material))
        {
            content = material.Content;
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["truncated"] = isTruncated.ToString().ToLowerInvariant()
        };

        return new ContextPackageDecision
        {
            ItemId = envelope.CandidateId,
            Kind = ResolveKindString(envelope.Source),
            Type = envelope.Type,
            SectionName = section,
            Reason = ResolveSelectReason(envelope),
            Score = envelope.Utility.FinalScore,
            EstimatedTokens = includedTokens,
            SourceRefs = envelope.ProvenanceRefs
                .Where(r => !string.IsNullOrEmpty(r.RefId))
                .Select(r => r.RefId)
                .ToList(),
            Metadata = metadata
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

    // P0-7 修复：不再将 Mandatory / Constraint 重新映射为 "hard_constraint" / "hard_constraints" section。
    // 之前已修复 Soft/Mixed Constraint 升硬语义；此处保持 source 原始语义，
    // 由 ConstraintLevel（通过 ResolveConstraintLevel(candidate.Metadata)）决定 IsMandatory / IsHardConstraint。
    // Section / Kind 按候选来源类型映射，不引入 "hard_constraint" 字面量。
    private static string ResolveKindString(ContextCandidateSource source) => source switch
    {
        ContextCandidateSource.Mandatory => "mandatory",
        ContextCandidateSource.Constraint => "constraint",
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
        ContextCandidateSource.Mandatory => "mandatory",
        ContextCandidateSource.Constraint => "constraint",
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
        // P0-7：保留 Safety.IsMandatory / IsHardConstraint 的语义判定，
        // 但不通过 Kind/Section 字面量升硬；仅用于 Reason 文本。
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
