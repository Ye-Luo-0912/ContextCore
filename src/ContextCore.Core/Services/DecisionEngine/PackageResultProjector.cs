using System.Text;
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
/// <remarks>
/// R28-B.6 Impl-1：当 AllocationDecision.IsTruncated=true 时，使用 IContentTruncator
/// 真正截断 Material.Content，并重新计算 ActualTokens（section content + SelectedItems）。
/// </remarks>
public sealed class PackageResultProjector : IResultProjector<ContextPackageBuildResult>
{
    private readonly IContentTruncator _contentTruncator;

    /// <summary>
    /// 构造 PackageResultProjector。
    /// </summary>
    /// <param name="contentTruncator">
    /// R28-B.6 Impl-1：内容截断器。null 时回退到 tokenizerResolver 或 <see cref="DefaultContentTruncator"/>。
    /// </param>
    /// <param name="tokenizerResolver">
    /// R28-B.6 P0-6：tokenizer 解析器（可选）。contentTruncator 为 null 且 tokenizerResolver 非空时，
    /// 使用 <see cref="TokenizerContentTruncator"/>（真正按 BPE/CJK 截断）。
    /// </param>
    /// <param name="modelName">tokenizer 使用的模型名（可选）。</param>
    public PackageResultProjector(
        IContentTruncator? contentTruncator = null,
        IContextTokenizerResolver? tokenizerResolver = null,
        string? modelName = null)
    {
        // R28-B.6 P0-6：优先级 contentTruncator > tokenizerResolver > DefaultContentTruncator
        _contentTruncator = contentTruncator
            ?? (tokenizerResolver is not null
                ? new TokenizerContentTruncator(tokenizerResolver, modelName)
                : new DefaultContentTruncator());
    }

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
    /// R28-B.6 Blocker-2：真正构建 ContextPackage（含 Sections/PackageId/SourceRefs/CreatedAt）+
    /// ContextPackageStandardOutput，赋值到 BuildResult.Package。
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

        // R28-B.6 Blocker-2：按 AllocationDecision.Section 分组，构建 ContextPackageSection 列表
        var sectionGroups = result.SelectedEnvelopes
            .Select(env =>
            {
                var section = ResolveSectionName(env.Source);
                var includedTokens = env.EstimatedTokens;
                var isTruncated = false;
                if (allocationByKey.TryGetValue(env.CanonicalKey, out var decision))
                {
                    section = decision.Section;
                    includedTokens = decision.IncludedTokens;
                    isTruncated = decision.IsTruncated;
                }
                return new
                {
                    Envelope = env,
                    Section = section,
                    IncludedTokens = includedTokens,
                    IsTruncated = isTruncated
                };
            })
            .GroupBy(x => x.Section, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        var sections = new List<ContextPackageSection>(sectionGroups.Count);
        var allSourceRefs = new List<string>();
        var totalUsedTokens = 0;

        foreach (var group in sectionGroups)
        {
            var sectionContentBuilder = new StringBuilder();
            var sectionItemRefs = new List<string>();
            var sectionSourceRefs = new List<string>();
            var sectionTokens = 0;
            var separatorTokens = 0;

            foreach (var item in group)
            {
                sectionItemRefs.Add(item.Envelope.CandidateId);

                // 从 Material sidecar 恢复正文
                if (workingSet.Materials.TryGetValue(item.Envelope.CanonicalKey, out var material))
                {
                    // R28-B.6 P0-6：分隔符 "\n\n" 的 token 预留（2 token/候选，避免后续追加时超出预算）
                    var isNotFirst = sectionContentBuilder.Length > 0;
                    if (isNotFirst)
                    {
                        sectionContentBuilder.Append("\n\n");
                        separatorTokens += 2;
                    }

                    // R28-B.6 Impl-1 + P0-6：当 IsTruncated=true 时，真正截断 Material.Content 并重算 ActualTokens。
                    // 截断时减去分隔符预留预算（2 token），确保 section 总 token 不超出 IncludedTokens。
                    var contentToAppend = material.Content;
                    if (item.IsTruncated && !string.IsNullOrEmpty(contentToAppend) && item.IncludedTokens > 0)
                    {
                        var effectiveBudget = isNotFirst
                            ? Math.Max(1, item.IncludedTokens - 2)
                            : item.IncludedTokens;
                        var truncation = _contentTruncator.Truncate(contentToAppend, effectiveBudget);
                        contentToAppend = truncation.TruncatedContent;
                        sectionTokens += truncation.ActualTokens;
                    }
                    else
                    {
                        sectionTokens += item.IncludedTokens;
                    }

                    sectionContentBuilder.Append(contentToAppend);
                    foreach (var sr in material.SourceRefs)
                    {
                        sectionSourceRefs.Add(sr);
                        allSourceRefs.Add(sr);
                    }
                }
                else
                {
                    sectionTokens += item.IncludedTokens;
                }

                // 加入 envelope 的 ProvenanceRefs
                foreach (var provenanceRef in item.Envelope.ProvenanceRefs)
                {
                    if (!string.IsNullOrEmpty(provenanceRef.RefId))
                    {
                        sectionSourceRefs.Add(provenanceRef.RefId);
                        allSourceRefs.Add(provenanceRef.RefId);
                    }
                }
            }

            // R28-B.6 P0-6：section 总 token = 候选正文 token + 分隔符预留 token
            var totalSectionTokens = sectionTokens + separatorTokens;
            sections.Add(new ContextPackageSection
            {
                Name = group.Key,
                Priority = sections.Count,
                Content = sectionContentBuilder.ToString(),
                ContentFormat = ContextContentFormat.PlainText,
                SourceRefs = sectionSourceRefs.Distinct(StringComparer.Ordinal).ToList(),
                ItemRefs = sectionItemRefs,
                EstimatedTokens = totalSectionTokens
            });
            totalUsedTokens += totalSectionTokens;
        }

        // R28-B.6 Blocker-2：构建 ContextPackage（含 PackageId/WorkspaceId/CollectionId/Sections/EstimatedTokens/SourceRefs/CreatedAt）
        // WorkspaceId/CollectionId 从 SelectedEnvelopes 的首个 CanonicalKey 推导（result 不携带 Scope）
        var firstEnvelope = result.SelectedEnvelopes.FirstOrDefault();
        var packageWorkspaceId = firstEnvelope?.CanonicalKey.WorkspaceId ?? string.Empty;
        var packageCollectionId = firstEnvelope?.CanonicalKey.CollectionId ?? string.Empty;

        var package = new ContextPackage
        {
            PackageId = $"pkg-{result.RequestId}",
            WorkspaceId = packageWorkspaceId,
            CollectionId = packageCollectionId,
            Sections = sections,
            EstimatedTokens = totalUsedTokens,
            SourceRefs = allSourceRefs.Distinct(StringComparer.Ordinal).ToList(),
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["policyVersion"] = result.PolicyVersion,
                ["modelEnabled"] = result.ModelEnabled.ToString().ToLowerInvariant(),
                ["purpose"] = result.Purpose.ToString(),
                ["runtimeKind"] = result.RuntimeKind.ToString()
            },
            CreatedAt = result.DecidedAt
        };

        // R28-B.6 Blocker-2：构建稳定的 ContextPackageStandardOutput
        var standardOutput = BuildStandardOutput(result, sections);

        return new ContextPackageBuildResult
        {
            BuildId = result.RequestId,
            Package = package,
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
            Output = standardOutput,
            TokenBudget = result.Outcome.TokenBudget,
            EstimatedTokens = result.Outcome.EstimatedTokens,
            Metadata = new Dictionary<string, string>
            {
                ["policyVersion"] = result.PolicyVersion,
                ["modelEnabled"] = result.ModelEnabled.ToString().ToLowerInvariant(),
                ["safetyGateBlocked"] = result.Outcome.SafetyGateBlockedCount.ToString(),
                ["budgetExceeded"] = result.Outcome.BudgetExceededCount.ToString()
            },
            CreatedAt = result.DecidedAt
        };
    }

    /// <summary>
    /// R28-B.6 Blocker-2：构建稳定的 ContextPackageStandardOutput。
    /// 按 section 名称映射到标准 schema 的 7 个分组（CurrentTask/RecentContext/WorkingState/
    /// StableBackground/Constraints/Entities/Relations/Evidence）。
    /// </summary>
    private static ContextPackageStandardOutput BuildStandardOutput(
        ContextDecisionResult result,
        IReadOnlyList<ContextPackageSection> sections)
    {
        var recentContext = new List<ContextPackageOutputItem>();
        var workingState = new List<ContextPackageOutputItem>();
        var stableBackground = new List<ContextPackageOutputItem>();
        var constraints = new List<ContextPackageOutputItem>();
        var entities = new List<ContextPackageOutputItem>();
        var relations = new List<ContextPackageOutputItem>();
        var evidence = new List<ContextPackageOutputItem>();

        foreach (var section in sections)
        {
            var outputItem = new ContextPackageOutputItem
            {
                SectionName = section.Name,
                Content = section.Content,
                ContentFormat = section.ContentFormat,
                SourceRefs = section.SourceRefs,
                ItemRefs = section.ItemRefs,
                EstimatedTokens = section.EstimatedTokens
            };

            // 按 section 名称映射到标准 schema 分组
            switch (section.Name)
            {
                case "mandatory":
                case "constraint":
                    constraints.Add(outputItem);
                    break;
                case "working_memory":
                case "memory":
                    workingState.Add(outputItem);
                    break;
                case "stable_memory":
                    stableBackground.Add(outputItem);
                    break;
                case "recent_context":
                case "global":
                case "global_context":
                case "related":
                    recentContext.Add(outputItem);
                    break;
                case "relations":
                case "related_context":
                    relations.Add(outputItem);
                    break;
                default:
                    recentContext.Add(outputItem);
                    break;
            }
        }

        return new ContextPackageStandardOutput
        {
            RecentContext = recentContext,
            WorkingState = workingState,
            StableBackground = stableBackground,
            Constraints = constraints,
            Entities = entities,
            Relations = relations,
            Evidence = evidence,
            Excluded = result.DroppedEnvelopes.Select(ProjectToDroppedItem).ToList(),
            Budget = new ContextPackageBudgetReport
            {
                TokenBudget = result.Outcome.TokenBudget,
                UsedTokens = result.Outcome.EstimatedTokens,
                RemainingTokens = Math.Max(0, result.Outcome.TokenBudget - result.Outcome.EstimatedTokens),
                UsageRatio = result.Outcome.TokenBudget > 0
                    ? (double)result.Outcome.EstimatedTokens / result.Outcome.TokenBudget
                    : 0
            }
        };
    }

    private ContextPackageDecision ProjectToPackageDecisionWithMaterial(
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

        // R28-B.6 Impl-1：当 IsTruncated=true 且有 Material 时，真正截断 Content 并重算 ActualTokens
        if (isTruncated && !string.IsNullOrEmpty(content) && includedTokens > 0)
        {
            var truncation = _contentTruncator.Truncate(content, includedTokens);
            content = truncation.TruncatedContent;
            includedTokens = truncation.ActualTokens;
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
