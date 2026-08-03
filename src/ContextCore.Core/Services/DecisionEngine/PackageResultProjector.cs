using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// Package 结果投影器
//
// 将 ContextDecisionResult 投影为 ContextPackageBuildResult，作为 Engine 输出
// 与现有 Package 主链出口 DTO 之间的桥梁。 阶段仅做格式投影，
// 不改变决策结果（envelope 集合不变）。
//
// 设计原则：
// 1. Projector 仅做格式投影，不调用 Engine 或 Storage；纯内存转换。
// 2. Projector 是幂等的：相同 Result 产生相同 DTO。
// 3. envelope.CandidateId → ContextPackageDecision.ItemId
// envelope.Utility.FinalScore → ContextPackageDecision.Score
// envelope.Safety.BlockReasonCode → DroppedContextItem.Reason（自由文本兼容）
// envelope.Source → ContextPackageDecision.Kind（字符串映射）
// ===========================================================================

/// <summary>
/// Package 结果投影器。将 Engine 输出的 envelope 集合投影为
/// <see cref="ContextPackageBuildResult"/>，保持与现有 Package 主链出口 DTO 兼容。
/// </summary>
/// <remarks>
/// 当 AllocationDecision.IsTruncated=true 时，使用 IContentTruncator
/// 真正截断 Material.Content，并重新计算 ActualTokens（section content + SelectedItems）。
/// </remarks>
public sealed class PackageResultProjector : IResultProjector<ContextPackageBuildResult>
{
    private readonly IContentTruncator _contentTruncator;
    private readonly string? _modelName;

    /// <summary>
    /// 构造 PackageResultProjector。
    /// </summary>
    /// <param name="contentTruncator">
    /// 内容截断器。null 时回退到 tokenizerResolver 或 <see cref="DefaultContentTruncator"/>。
    /// </param>
    /// <param name="tokenizerResolver">
    /// tokenizer 解析器（可选）。contentTruncator 为 null 且 tokenizerResolver 非空时，
    /// 使用 <see cref="TokenizerContentTruncator"/>（真正按 BPE/CJK 截断）。
    /// </param>
    /// <param name="modelName">tokenizer 使用的模型名（可选）。</param>
    public PackageResultProjector(
        IContentTruncator? contentTruncator = null,
        IContextTokenizerResolver? tokenizerResolver = null,
        string? modelName = null)
    {
        // 优先级 contentTruncator > tokenizerResolver > DefaultContentTruncator
        _contentTruncator = contentTruncator
            ?? (tokenizerResolver is not null
                ? new TokenizerContentTruncator(tokenizerResolver, modelName)
                : new DefaultContentTruncator());
        // 保存 modelName，用于 CountTokens 统一口径计算
        _modelName = modelName;
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
    /// 将决策结果 + 候选正文 sidecar 投影为 ContextPackageBuildResult。
    /// 从 workingSet.Materials 恢复候选 Content；从 result.AllocationDecisions
    /// 消费 Section / IncludedTokens / IsTruncated 构建 section + token 分配。
    /// 真正构建 ContextPackage（含 Sections/PackageId/SourceRefs/CreatedAt）+
    /// ContextPackageStandardOutput，赋值到 BuildResult.Package。
    /// </summary>
    /// <remarks>
    /// 此重载不携带 Scope，空 Package 时 WorkspaceId/CollectionId 将为空。
    /// 调用方应优先使用 <see cref="Project(ContextDecisionResult, CandidateWorkingSet, ContextDecisionScope)"/>
    /// 传入真实 Scope，避免空 Package 丢失 Scope。
    /// </remarks>
    public ContextPackageBuildResult Project(ContextDecisionResult result, CandidateWorkingSet workingSet)
    {
        return Project(result, workingSet, scope: default);
    }

    /// <summary>
    /// 从完整执行结果投影为 ContextPackageBuildResult。
    /// </summary>
    /// <remarks>
    /// 便捷重载：从 execution 提取 Decision + WorkingSet + Scope。
    /// 关键修复：使用 execution.Scope 而非 default，避免空 Package 丢失 Scope
    /// （候选为空时仍能从 execution.Scope 获取 WorkspaceId/CollectionId）。
    /// 从 execution.NormalizedRequest.PackageInput 提取
    /// Mode/Policy/IncludeRecent/IsAuditMode 语义，传递给内部 Project 重载，
    /// 让 Projector 能按调用方意图过滤 recent_context section 并写入审计/模式元数据。
    /// </remarks>
    public ContextPackageBuildResult Project(ContextDecisionExecutionResult execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        // 从标准化请求中提取 PackageInput 语义
        var packageInput = execution.NormalizedRequest?.PackageInput;
        return Project(execution.Decision, execution.WorkingSet, execution.Scope, packageInput);
    }

    /// <summary>
    /// 将决策结果 + 候选正文 sidecar + 作用域投影为 ContextPackageBuildResult。
    /// 空 Package（无选中候选）时从 scope 获取 WorkspaceId/CollectionId，而非从候选反推
    /// （候选为空时反推会丢失 Scope）。
    /// 此重载不携带 PackageInput，等价于 packageInput=null（不做
    /// recent_context 过滤、不写 mode/auditMode 元数据），保持向后兼容。
    /// </summary>
    public ContextPackageBuildResult Project(
        ContextDecisionResult result,
        CandidateWorkingSet workingSet,
        ContextDecisionScope scope)
    {
        return Project(result, workingSet, scope, packageInput: null);
    }

    /// <summary>
    /// 将决策结果 + 候选正文 sidecar + 作用域 + PackageInput 语义
    /// 投影为 ContextPackageBuildResult。
    /// </summary>
    /// <remarks>
    /// PackageInput 语义消费：
    /// 1. <see cref="PackageInput.IncludeRecent"/>=false 时，过滤掉 recent_context section
    /// （与 Legacy BasicContextPackageBuilder 中 IncludeRecentRawContext=false 行为对齐）。
    /// 2. <see cref="PackageInput.Mode"/> ≠ None 时，写入 package.Metadata["mode"] 供下游消费。
    /// 3. <see cref="PackageInput.IsAuditMode"/> 解析为 true 时，写入 metadata["isAuditMode"]="true"。
    /// 4. <see cref="PackageInput.Policy"/> 非 null 时，写入 metadata["packagePolicyId"] 供 trace。
    /// 空 Package（无选中候选）时从 scope 获取 WorkspaceId/CollectionId，而非从候选反推
    /// （候选为空时反推会丢失 Scope）。
    /// </remarks>
    public ContextPackageBuildResult Project(
        ContextDecisionResult result,
        CandidateWorkingSet workingSet,
        ContextDecisionScope scope,
        PackageInput? packageInput)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(workingSet);

        // 构建 CanonicalKey → AllocationDecision 索引
        var allocationByKey = result.AllocationDecisions
            .ToDictionary(d => d.CandidateKey, d => d);

        var selectedItems = result.SelectedEnvelopes
            .Select(env => ProjectToPackageDecisionWithMaterial(env, workingSet, allocationByKey))
            .ToList();

        var droppedItems = result.DroppedEnvelopes
            .Select(ProjectToDroppedItem)
            .ToList();

        // 从 AllocationDecisions 构建完整 section 列表 + token 分配
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

        // 按 AllocationDecision.Section 分组，构建 ContextPackageSection 列表
        var sectionGroups = result.SelectedEnvelopes
            .Select(env =>
            {
                var section = ResolveSectionName(env.Source);
                // 优先使用 TokenCost.ContentTokens（精确 token 计数），回退到 EstimatedTokens
                var includedTokens = env.TokenCost?.ContentTokens ?? env.EstimatedTokens;
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
                    // 分隔符 "\n\n" 的 token 预留（2 token/候选，避免后续追加时超出预算）
                    var isNotFirst = sectionContentBuilder.Length > 0;
                    if (isNotFirst)
                    {
                        sectionContentBuilder.Append("\n\n");
                        separatorTokens += 2;
                    }

                    // 当 IsTruncated=true 时，真正截断 Material.Content 并重算 ActualTokens。
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

            // section 总 token = 候选正文 token + 分隔符预留 token
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

        // 消费 PackageInput.IncludeRecent 语义。
        // IncludeRecent=false 时过滤掉 recent_context section（与 Legacy
        // BasicContextPackageBuilder 中 IncludeRecentRawContext=false 行为对齐），
        // 并从 totalUsedTokens 中扣除被过滤 section 的 token 数，保持 token 计账一致。
        if (packageInput is { IncludeRecent: false } && sections.Count > 0)
        {
            var filteredSections = new List<ContextPackageSection>(sections.Count);
            var removedTokens = 0;
            foreach (var s in sections)
            {
                if (IsRecentContextSection(s.Name))
                {
                    removedTokens += s.EstimatedTokens;
                }
                else
                {
                    filteredSections.Add(s);
                }
            }
            if (filteredSections.Count != sections.Count)
            {
                sections = filteredSections;
                totalUsedTokens -= removedTokens;
            }
        }

        // 构建 ContextPackage（含 PackageId/WorkspaceId/CollectionId/Sections/EstimatedTokens/SourceRefs/CreatedAt）
        // 空 Package（无选中候选）时从 scope 获取 WorkspaceId/CollectionId，而非候选反推
        // （候选为空时 firstEnvelope 为 null，反推会丢失 Scope）
        var firstEnvelope = result.SelectedEnvelopes.FirstOrDefault();
        var packageWorkspaceId = firstEnvelope?.CanonicalKey.WorkspaceId ?? scope.WorkspaceId;
        var packageCollectionId = firstEnvelope?.CanonicalKey.CollectionId ?? scope.CollectionId;

        // 解析 PackageInput 语义为 metadata 键值，供下游 trace/审计消费。
        var packageMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["policyVersion"] = result.PolicyVersion,
            ["modelEnabled"] = result.ModelEnabled.ToString().ToLowerInvariant(),
            ["purpose"] = result.Purpose.ToString(),
            ["runtimeKind"] = result.RuntimeKind.ToString()
        };
        ApplyPackageInputMetadata(packageMetadata, packageInput);

        var package = new ContextPackage
        {
            PackageId = $"pkg-{result.RequestId}",
            WorkspaceId = packageWorkspaceId,
            CollectionId = packageCollectionId,
            Sections = sections,
            EstimatedTokens = totalUsedTokens,
            SourceRefs = allSourceRefs.Distinct(StringComparer.Ordinal).ToList(),
            Metadata = packageMetadata,
            CreatedAt = result.DecidedAt
        };

        // BuildResult 的 token 字段使用 Projector 截断后的真实 token 数
        // （package.EstimatedTokens = totalUsedTokens），而非 Allocator 的预估值
        // （result.Outcome.EstimatedTokens）。统一事实源，避免双重报告。
        var actualUsedTokens = package.EstimatedTokens;

        // 构建稳定的 ContextPackageStandardOutput
        var standardOutput = BuildStandardOutput(result, sections, actualUsedTokens);

        // 传播 Engine Outcome.Diagnostics 到输出 Metadata（不丢失诊断）。
        // 诊断键加 "diag." 前缀以避免与既有 Metadata 键冲突。
        // 同时传播 PackageInput 语义（mode/isAuditMode/packagePolicyId）。
        var metadata = new Dictionary<string, string>
        {
            ["policyVersion"] = result.PolicyVersion,
            ["modelEnabled"] = result.ModelEnabled.ToString().ToLowerInvariant(),
            ["safetyGateBlocked"] = result.Outcome.SafetyGateBlockedCount.ToString(),
            ["budgetExceeded"] = result.Outcome.BudgetExceededCount.ToString()
        };
        ApplyPackageInputMetadata(metadata, packageInput);
        foreach (var (key, value) in result.Outcome.Diagnostics)
        {
            metadata[$"diag.{key}"] = value;
        }

        return new ContextPackageBuildResult
        {
            BuildId = result.RequestId,
            Package = package,
            SelectedItems = selectedItems,
            DroppedItems = droppedItems,
            Budget = new ContextPackageBudgetReport
            {
                TokenBudget = result.Outcome.TokenBudget,
                UsedTokens = actualUsedTokens,
                RemainingTokens = Math.Max(0, result.Outcome.TokenBudget - actualUsedTokens),
                UsageRatio = result.Outcome.TokenBudget > 0
                    ? (double)actualUsedTokens / result.Outcome.TokenBudget
                    : 0,
                Sections = sectionBudgets
            },
            Output = standardOutput,
            TokenBudget = result.Outcome.TokenBudget,
            EstimatedTokens = actualUsedTokens,
            Metadata = metadata,
            CreatedAt = result.DecidedAt
        };
    }

    /// <summary>
    /// 重建 ContextPackageSection，替换 Content 和 EstimatedTokens。
    /// ContextPackageSection 是 class with init，无法原地修改，需创建新实例。
    /// </summary>
    private static ContextPackageSection RebuildSection(ContextPackageSection section, string content, int tokens)
    {
        return new ContextPackageSection
        {
            Name = section.Name,
            Priority = section.Priority,
            Content = content,
            ContentFormat = section.ContentFormat,
            SourceRefs = section.SourceRefs,
            ItemRefs = section.ItemRefs,
            EstimatedTokens = tokens
        };
    }

    /// <summary>
    /// 判断 section 是否属于 recent_context 分组。
    /// 与 <see cref="BuildStandardOutput"/> 中 recent_context 分组映射保持一致，
    /// 包含 recent_context / global / global_context / related 几个历史别名。
    /// </summary>
    private static bool IsRecentContextSection(string sectionName)
    {
        return sectionName switch
        {
            "recent_context" or "global" or "global_context" or "related" => true,
            _ => false
        };
    }

    /// <summary>
    /// 将 PackageInput 语义写入 metadata 字典（原地修改）。
    /// 仅写入有意义的字段，避免在 metadata 中留下空值或 None 噪音。
    /// </summary>
    /// <remarks>
    /// 写入的键：
    /// - "mode"：当 Mode ≠ None 时（值为枚举名，如 "Chat"/"Novel"/"Automation"/"Coding"）。
    /// - "isAuditMode"：当解析后为 true 时（值为 "true"；false 或 null 不写入，避免噪音）。
    /// - "packagePolicyId"：当 Policy 非 null 且 Id 非空时（用于 trace 关联显式策略）。
    /// - "includeRecent"：当为 false 时（值为 "false"；true 是默认值，不写入）。
    /// </remarks>
    private static void ApplyPackageInputMetadata(Dictionary<string, string> metadata, PackageInput? packageInput)
    {
        if (packageInput is null) return;

        if (packageInput.Mode != ContextPackageMode.None)
        {
            metadata["mode"] = packageInput.Mode.ToString();
        }

        // 审计模式解析：request.IsAuditMode=true 即启用（与 Legacy ResolveIsAuditMode 语义一致：
        // 任一为 true 即启用）。PackageInput.IsAuditMode 为 nullable，true 时启用。
        if (packageInput.IsAuditMode is true)
        {
            metadata["isAuditMode"] = "true";
        }

        if (!string.IsNullOrEmpty(packageInput.Policy?.Id))
        {
            metadata["packagePolicyId"] = packageInput.Policy!.Id;
        }

        if (!packageInput.IncludeRecent)
        {
            metadata["includeRecent"] = "false";
        }
    }

    /// <summary>
    /// 构建稳定的 ContextPackageStandardOutput。
    /// 按 section 名称映射到标准 schema 的 7 个分组（CurrentTask/RecentContext/WorkingState/
    /// StableBackground/Constraints/Entities/Relations/Evidence）。
    /// Budget 的 token 字段使用 Projector 截断后的真实 token 数（actualUsedTokens），
    /// 而非 Allocator 预估值（result.Outcome.EstimatedTokens）。
    /// </summary>
    private static ContextPackageStandardOutput BuildStandardOutput(
        ContextDecisionResult result,
        IReadOnlyList<ContextPackageSection> sections,
        int actualUsedTokens)
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
                UsedTokens = actualUsedTokens,
                RemainingTokens = Math.Max(0, result.Outcome.TokenBudget - actualUsedTokens),
                UsageRatio = result.Outcome.TokenBudget > 0
                    ? (double)actualUsedTokens / result.Outcome.TokenBudget
                    : 0
            }
        };
    }

    private ContextPackageDecision ProjectToPackageDecisionWithMaterial(
        ContextCandidateEnvelope envelope,
        CandidateWorkingSet workingSet,
        IReadOnlyDictionary<CanonicalCandidateKey, CandidateAllocationDecision> allocationByKey)
    {
        // 从 AllocationDecision 消费 Section / IncludedTokens / IsTruncated
        var section = ResolveSectionName(envelope.Source);
        // 优先使用 TokenCost.ContentTokens（精确 token 计数），回退到 EstimatedTokens
        var includedTokens = envelope.TokenCost?.ContentTokens ?? envelope.EstimatedTokens;
        var isTruncated = false;
        if (allocationByKey.TryGetValue(envelope.CanonicalKey, out var decision))
        {
            section = decision.Section;
            includedTokens = decision.IncludedTokens;
            isTruncated = decision.IsTruncated;
        }

        // 从 Material sidecar 恢复候选 Content
        string content = string.Empty;
        if (workingSet.Materials.TryGetValue(envelope.CanonicalKey, out var material))
        {
            content = material.Content;
        }

        // 当 IsTruncated=true 且有 Material 时，真正截断 Content 并重算 ActualTokens
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

    // 修复：不再将 Mandatory / Constraint 重新映射为 "hard_constraint" / "hard_constraints" section。
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
        // 保留 Safety.IsMandatory / IsHardConstraint 的语义判定，
        // 但不通过 Kind/Section 字面量升硬；仅用于 Reason 文本。
        if (envelope.Source == ContextCandidateSource.Mandatory || envelope.Safety.IsMandatory) return "mandatory";
        if (envelope.Source == ContextCandidateSource.Constraint || envelope.Safety.IsHardConstraint)
        {
            // 根据 ConstraintLevel 返回不同解释，区分 hard/soft/mixed/system
            // （之前统一返回 "hard constraint"，导致 soft constraint 被误报为 hard）
            return envelope.Safety.ConstraintLevel switch
            {
                ConstraintLevel.Hard => "hard constraint",
                ConstraintLevel.Soft => "soft constraint",
                ConstraintLevel.Mixed => "mixed constraint",
                ConstraintLevel.System => "system constraint",
                _ => "hard constraint"
            };
        }
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
