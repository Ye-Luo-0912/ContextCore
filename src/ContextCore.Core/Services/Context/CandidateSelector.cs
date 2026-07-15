using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Learning.V14_0;

namespace ContextCore.Core;

/// <summary>
/// 候选选择阶段：recent filter、约束分区、记忆召回、候选构造、graph 扩展、
/// evidence/diagnostics section 处理。按原顺序串行处理每个 section，调用
/// <see cref="SectionAssembler"/> 装配 section、<see cref="PackageTraceRecorder"/> 记录决策，
/// 保持与 <see cref="BasicContextPackageBuilder.BuildWithPolicyAsync"/> 字节级一致的变异顺序。
/// </summary>
internal sealed class CandidateSelector
{
    private readonly SectionAssembler _assembler;
    private readonly PackageTraceRecorder _traceRecorder;
    private readonly GraphExpansionCoordinator _graphExpansionCoordinator;
    private readonly Func<string?, TokenEstimationContext, int> _estimateTokens;
    private readonly RecentContextFilter _recentContextFilter = new();
    private readonly ContextAnchorExtractor _anchorExtractor = new();
    private readonly RetrievalPlanner _planner = new();

    internal CandidateSelector(
        SectionAssembler assembler,
        PackageTraceRecorder traceRecorder,
        GraphExpansionCoordinator graphExpansionCoordinator,
        Func<string?, TokenEstimationContext, int> estimateTokens)
    {
        _assembler = assembler;
        _traceRecorder = traceRecorder;
        _graphExpansionCoordinator = graphExpansionCoordinator;
        _estimateTokens = estimateTokens;
    }

    /// <summary>
    /// 选择阶段：消费 <see cref="PackageInputs"/>，按原顺序处理所有 section，
    /// 返回 <see cref="SelectionResult"/>（含 sections、accumulators、anchors、retrievalPlan、uncertainties）。
    /// </summary>
    internal async Task<SelectionResult> SelectCandidatesAsync(
        PackageInputs inputs,
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        TokenEstimationContext tokenContext,
        CancellationToken cancellationToken)
    {
        var workspaceId = PackagePolicyResolver.NormalizeRequiredValue(request.WorkspaceId);
        var collectionId = PackagePolicyResolver.NormalizeRequiredValue(policy.CollectionId, request.CollectionId);
        var modeBudgetProfile = PackagePolicyResolver.ResolveModeBudgetProfile(request, policy);
        var tokenBudget = PackagePolicyResolver.ResolveTokenBudget(request, policy, modeBudgetProfile);
        var packageModeName = PackagePolicyResolver.ResolvePackageModeName(request, policy, modeBudgetProfile);
        var packageMustHitIds = PackagePolicyResolver.ResolvePackageMustHitIds(request);
        var isAuditMode = PackagePolicyResolver.ResolveIsAuditMode(request, policy);
        var maxRecentItems = policy.MaxRecentItems > 0 ? policy.MaxRecentItems : 20;

        var state = new SelectionState();
        // 仅在不包含 recent context 时提前提取 anchors（避免空数组提取被 recent 路径覆盖的浪费）
        var anchors = policy.IncludeRecentRawContext
            ? Array.Empty<ContextAnchor>()
            : _anchorExtractor.Extract(request, Array.Empty<RecentContextItem>());
        var includedRecent = Array.Empty<RecentContextItem>();
        var excludedRecent = Array.Empty<RecentContextItem>();

        // current_task section（第一个装配，此时 accumulators 为空，与原实现一致）
        if (PackagePolicyResolver.ShouldIncludeCurrentTaskSection(request, policy))
        {
            var currentTask = inputs.CurrentTask;
            if (currentTask is not null)
            {
                var content = PackageSectionFormatter.FormatCurrentTask(currentTask, request);
                var currentTaskCandidate = PackageTraceCandidate.FromCurrentTask(
                    currentTask,
                    _estimateTokens(content, tokenContext));
                CommitSection(state, policy, modeBudgetProfile, tokenBudget, tokenContext, new SectionDraft
                {
                    Name = "current_task",
                    DefaultPriority = 110,
                    ContentBlocks = new[] { content },
                    Candidates = new[] { currentTaskCandidate },
                    SourceRefs = currentTaskCandidate.SourceRefs,
                    ItemRefs = new[] { currentTask.TaskId },
                    CandidateIds = new[] { currentTask.TaskId },
                });
            }
        }

        // recent filter + anchors + retrievalPlan
        if (policy.IncludeRecentRawContext)
        {
            var recentItems = inputs.RecentItems!;

            var filteredRecent = _recentContextFilter.Filter(recentItems, request, maxRecentItems, null, anchors);
            includedRecent = filteredRecent
                .Where(item => item.ExcludeReason is null)
                .ToArray();
            excludedRecent = filteredRecent
                .Where(item => item.ExcludeReason is not null)
                .ToArray();
            anchors = _anchorExtractor.Extract(request, filteredRecent);
        }

        // 短期锚定召回计划：基于当前 query + recent context 提前构建
        // 此处 anchors 已包含真实近期上下文，供后续逻辑及 ContextPackageBuildResult.Plan 使用
        var retrievalPlan = _planner.Plan(new ShortTermSnapshot
        {
            WorkspaceId      = request.WorkspaceId ?? string.Empty,
            CollectionId     = collectionId ?? string.Empty,
            CurrentQueryText = request.QueryText ?? string.Empty,
            RecentItems      = includedRecent,
            Anchors          = anchors,
            CreatedAt        = DateTimeOffset.UtcNow
        });

        // hard_constraints
        if (inputs.HardConstraints is not null)
        {
            var hardConstraints = inputs.HardConstraints;

            // 单遍分区：active 和 inactive 一次遍历完成，避免 Where().ToArray() 双重扫描
            var activeHardConstraints = new List<ContextConstraint>();
            var inactiveHardConstraints = new List<ContextConstraint>();
            foreach (var item in hardConstraints)
            {
                if (LegacyPackageScorer.IsActive(item))
                {
                    activeHardConstraints.Add(item);
                    state.AddedConstraintIds.Add(item.Id);
                }
                else
                {
                    inactiveHardConstraints.Add(item);
                }
            }

            foreach (var item in inactiveHardConstraints)
            {
                var c = PackageTraceCandidate.FromConstraint(item, "hard_constraint", 100, _estimateTokens(item.Content, tokenContext));
                _traceRecorder.WriteTraceRow(c, "hard_constraints", false, "constraint is deprecated or rejected", selectedByScoring: false);
                state.DroppedItems.Add(PackageTraceRecorder.CreateDropped(c, "constraint is deprecated or rejected"));
            }

            if (activeHardConstraints.Count > 0)
            {
                var hardCandidates = activeHardConstraints
                    .Select(item => PackageTraceCandidate.FromConstraint(item, "hard_constraint", 100, _estimateTokens(item.Content, tokenContext)))
                    .ToArray();

                var hardToFormat = activeHardConstraints.Where(c => !state.GlobalSelectedIds.Contains(c.Id)).ToArray();
                IEnumerable<string> hardBlocks = hardToFormat.Length > 0
                    ? PackageSectionFormatter.FormatConstraintBlocks(hardToFormat, tokenBudget)
                    : new[] { "(所有硬约束已在更优 Section 中包含)" };

                CommitSection(state, policy, modeBudgetProfile, tokenBudget, tokenContext, new SectionDraft
                {
                    Name = "hard_constraints",
                    DefaultPriority = 100,
                    ContentBlocks = hardBlocks,
                    Candidates = hardCandidates,
                    SourceRefs = ContextItemRefResolver.ResolveSourceRefs(activeHardConstraints),
                    ItemRefs = ContextItemRefResolver.ResolveItemRefs(activeHardConstraints),
                    CandidateIds = ContextItemRefResolver.ResolveItemRefs(activeHardConstraints),
                });
            }
        }

        IReadOnlyList<ContextMemoryItem> workingMemory = Array.Empty<ContextMemoryItem>();
        if (inputs.WorkingCandidatesRaw is not null)
        {
            var workingCandidatesRaw = inputs.WorkingCandidatesRaw;

            // 使用带 breakdown 的召回函数，以便展示 13 个子分维度
            var workingWithBreakdowns = WorkingMemoryRecaller.RecallWorkingMemoryWithBreakdowns(
                workingCandidatesRaw,
                anchors,
                policy.MaxRecentItems > 0 ? policy.MaxRecentItems : 20,
                isAuditMode,
                true,
                tokenBudget,
                packageModeName,
                packageMustHitIds,
                policy.EnableStrictRelevanceFilter);
            workingWithBreakdowns = WorkingMemoryRecaller.EnsureReservedWorkingMemoryCandidates(
                workingCandidatesRaw,
                workingWithBreakdowns,
                anchors,
                isAuditMode,
                true,
                packageModeName,
                packageMustHitIds,
                policy.EnableStrictRelevanceFilter);

            workingMemory = workingWithBreakdowns.Select(x => x.Item).ToArray();

            // 分流活跃与废弃/被替代记忆
            var activeWorkingPairs   = workingWithBreakdowns.Where(x => x.Item.Status != ContextMemoryStatus.Deprecated && !string.Equals(WorkingMemoryRecaller.ResolveMemoryProcessState(x.Item), "superseded", StringComparison.OrdinalIgnoreCase)).ToArray();
            var deprecatedWorkingPairs = workingWithBreakdowns.Where(x => x.Item.Status == ContextMemoryStatus.Deprecated || string.Equals(WorkingMemoryRecaller.ResolveMemoryProcessState(x.Item), "superseded", StringComparison.OrdinalIgnoreCase)).ToArray();
            var activeWorking   = activeWorkingPairs.Select(x => x.Item).ToArray();
            var deprecatedWorking = deprecatedWorkingPairs.Select(x => x.Item).ToArray();

            foreach (var pair in activeWorkingPairs)
                state.SelectedSourceIds.Add(pair.Item.Id);
            foreach (var pair in deprecatedWorkingPairs)
                state.SelectedSourceIds.Add(pair.Item.Id);

            // 1. 活跃工作记忆处理
            if (activeWorking.Length > 0)
            {
                var workingCandidates = activeWorkingPairs
                    .Select(pair => PackageTraceCandidate.FromMemory(pair.Item, "working_memory", pair.Breakdown, _estimateTokens(pair.Item.Content, tokenContext)))
                    .ToArray();

                var workingToFormat = activeWorking.Where(item => !state.GlobalSelectedIds.Contains(item.Id)).ToArray();
                IEnumerable<string> workingBlocks = workingToFormat.Length > 0
                    ? PackageSectionFormatter.FormatMemoryBlocks(workingToFormat, tokenBudget)
                    : new[] { "(所有活跃工作区记忆已在此前去重包含)" };

                CommitSection(state, policy, modeBudgetProfile, tokenBudget, tokenContext, new SectionDraft
                {
                    Name = "working_memory",
                    DefaultPriority = 90,
                    ContentBlocks = workingBlocks,
                    Candidates = workingCandidates,
                    SourceRefs = ContextItemRefResolver.ResolveSourceRefs(activeWorking),
                    ItemRefs = ContextItemRefResolver.ResolveItemRefs(activeWorking),
                    CandidateIds = ContextItemRefResolver.ResolveItemRefs(activeWorking),
                });
            }

            // 2. 审计废案/历史记忆分流处理 (仅在 isAuditMode 时会被召回)
            if (deprecatedWorking.Length > 0)
            {
                if (isAuditMode)
                {
                    // 审计模式：构建候选并召回历史记忆 section
                    var historicalCandidates = deprecatedWorkingPairs
                        .Select(pair => {
                            var c = PackageTraceCandidate.FromMemory(pair.Item, "historical_context", pair.Breakdown, _estimateTokens(pair.Item.Content, tokenContext));
                            c.Metadata["lifecycleStatus"] = "Deprecated";
                            return c;
                        })
                        .ToArray();

                    var historicalToFormat = deprecatedWorking.Where(item => !state.GlobalSelectedIds.Contains(item.Id)).ToArray();
                    IEnumerable<string> historicalBlocks = historicalToFormat.Length > 0
                        ? PackageSectionFormatter.FormatMemoryBlocks(historicalToFormat, tokenBudget)
                        : new[] { "(所有历史审计记忆已在此前去重包含)" };

                    CommitSection(state, policy, modeBudgetProfile, tokenBudget, tokenContext, new SectionDraft
                    {
                        Name = "historical_context",
                        DefaultPriority = 15,
                        BudgetKind = SectionBudgetKind.Historical,
                        ContentBlocks = historicalBlocks,
                        Candidates = historicalCandidates,
                        SourceRefs = ContextItemRefResolver.ResolveSourceRefs(deprecatedWorking),
                        ItemRefs = ContextItemRefResolver.ResolveItemRefs(deprecatedWorking),
                        CandidateIds = ContextItemRefResolver.ResolveItemRefs(deprecatedWorking),
                    });
                }
                else
                {
                    // 非审计模式：仅记录 dropped，跳过 token 估算和候选构建
                    foreach (var pair in deprecatedWorkingPairs)
                    {
                        var c = PackageTraceCandidate.FromMemory(pair.Item, "historical_context", pair.Breakdown, 0);
                        c.Metadata["lifecycleStatus"] = "Deprecated";
                        _traceRecorder.WriteTraceRow(c, "historical_context", false, "deprecated memory is excluded in non-audit mode", selectedByScoring: false);
                        state.DroppedItems.Add(PackageTraceRecorder.CreateDropped(c, "deprecated memory is excluded in non-audit mode"));
                    }
                }
            }
        }

        // global_context
        if (inputs.GlobalItems is not null)
        {
            var globalItems = inputs.GlobalItems;

            var globalCandidates = globalItems
                .Select(item => PackageTraceCandidate.FromGlobal(item, "global_context", 8.0 + item.Importance * 2.0, _estimateTokens(item.Content, tokenContext)))
                .ToArray();

            var globalToFormat = globalItems.Where(item => !state.GlobalSelectedIds.Contains(item.Id)).ToArray();
            IEnumerable<string> globalBlocks = globalToFormat.Length > 0
                ? PackageSectionFormatter.FormatGlobalBlocks(globalToFormat)
                : new[] { "(所有全局上下文已在此前去重包含)" };

            CommitSection(state, policy, modeBudgetProfile, tokenBudget, tokenContext, new SectionDraft
            {
                Name = "global_context",
                DefaultPriority = 80,
                ContentBlocks = globalBlocks,
                Candidates = globalCandidates,
                SourceRefs = ContextItemRefResolver.ResolveSourceRefs(globalItems),
                ItemRefs = ContextItemRefResolver.ResolveItemRefs(globalItems),
                CandidateIds = ContextItemRefResolver.ResolveItemRefs(globalItems),
            });
        }

        // recent_context
        if (policy.IncludeRecentRawContext)
        {
            foreach (var item in includedRecent)
            {
                state.SelectedSourceIds.Add(item.SourceItemId);
            }

            state.DroppedItems.AddRange(excludedRecent.Select(item =>
            {
                var c = PackageTraceCandidate.FromRecent(item, "recent_context", item.Relevance * 79.0, _estimateTokens(item.Content, tokenContext));
                _traceRecorder.WriteTraceRow(c, "recent_context", false, item.ExcludeReason ?? "recent context excluded", selectedByScoring: false);
                return PackageTraceRecorder.CreateDropped(c, item.ExcludeReason ?? "recent context excluded");
            }));

            var recentCandidates = includedRecent
                .Select(item => PackageTraceCandidate.FromRecent(item, "recent_context", item.Relevance * 79.0, _estimateTokens(item.Content, tokenContext)))
                .ToArray();

            var recentToFormat = includedRecent.Where(item => !state.GlobalSelectedIds.Contains(item.SourceItemId)).ToArray();
            IEnumerable<string> recentBlocks = recentToFormat.Length > 0
                ? PackageSectionFormatter.FormatRecentContextBlocks(recentToFormat, tokenBudget)
                : new[] { "(所有近期短期上下文已在此前去重包含)" };

            CommitSection(state, policy, modeBudgetProfile, tokenBudget, tokenContext, new SectionDraft
            {
                Name = "recent_context",
                DefaultPriority = 70,
                ContentBlocks = recentBlocks,
                Candidates = recentCandidates,
                SourceRefs = ContextItemRefResolver.ResolveSourceRefs(includedRecent),
                ItemRefs = ContextItemRefResolver.ResolveItemRefs(includedRecent),
                CandidateIds = ContextItemRefResolver.ResolveItemRefs(includedRecent),
            });
        }

        // stable_memory
        IReadOnlyList<ContextMemoryItem> stableMemory = Array.Empty<ContextMemoryItem>();
        if (inputs.StableCandidatesRaw is not null)
        {
            var maxStableItems = policy.MaxRecentItems > 0 ? policy.MaxRecentItems : 20;
            var stableCandidatesRaw = inputs.StableCandidatesRaw;
            stableMemory = WorkingMemoryRecaller.RecallStableMemory(
                stableCandidatesRaw,
                anchors,
                workingMemory,
                maxStableItems,
                packageModeName,
                packageMustHitIds);

            foreach (var memory in stableMemory)
            {
                state.SelectedSourceIds.Add(memory.Id);
            }

            var workingSignals = ContextRecallSignalPolicy.CreateWorkingMemorySignals(workingMemory);
            var stableCandidates = stableMemory
                .Select(item => {
                    var searchText = WorkingMemoryRecaller.CreateMemorySearchText(item);
                    var scoreResult = ContextRecallSignalPolicy.ScoreStableMemoryForInjection(item, anchors, workingSignals, searchText);
                    var finalScore = scoreResult.Score;
                    return PackageTraceCandidate.FromMemory(item, "stable_memory", finalScore, _estimateTokens(item.Content, tokenContext));
                })
                .ToArray();

            var stableToFormat = stableMemory.Where(item => !state.GlobalSelectedIds.Contains(item.Id)).ToArray();
            IEnumerable<string> stableBlocks = stableToFormat.Length > 0
                ? PackageSectionFormatter.FormatMemoryBlocks(stableToFormat, tokenBudget)
                : new[] { "(所有稳定背景记忆已在此前去重包含)" };

            CommitSection(state, policy, modeBudgetProfile, tokenBudget, tokenContext, new SectionDraft
            {
                Name = "stable_memory",
                DefaultPriority = 60,
                ContentBlocks = stableBlocks,
                Candidates = stableCandidates,
                SourceRefs = ContextItemRefResolver.ResolveSourceRefs(stableMemory),
                ItemRefs = ContextItemRefResolver.ResolveItemRefs(stableMemory),
                CandidateIds = ContextItemRefResolver.ResolveItemRefs(stableMemory),
            });
        }

        // soft_constraints
        if (inputs.SoftConstraints is not null)
        {
            var softConstraints = inputs.SoftConstraints;

            var activeSoftConstraints = softConstraints.Where(LegacyPackageScorer.IsActive).ToArray();
            foreach (var item in activeSoftConstraints)
            {
                state.AddedConstraintIds.Add(item.Id);
            }
            state.DroppedItems.AddRange(softConstraints
                .Where(item => !LegacyPackageScorer.IsActive(item))
                .Select(item => {
                    var c = PackageTraceCandidate.FromConstraint(item, "soft_constraint", 15.0, _estimateTokens(item.Content, tokenContext));
                    _traceRecorder.WriteTraceRow(c, "soft_constraints", false, "constraint is deprecated or rejected", selectedByScoring: false);
                    return PackageTraceRecorder.CreateDropped(c, "constraint is deprecated or rejected");
                }));

            if (activeSoftConstraints.Length > 0)
            {
                var softCandidates = activeSoftConstraints
                    .Select(item => PackageTraceCandidate.FromConstraint(item, "soft_constraint", 15.0, _estimateTokens(item.Content, tokenContext)))
                    .ToArray();

                var softToFormat = activeSoftConstraints.Where(c => !state.GlobalSelectedIds.Contains(c.Id)).ToArray();
                IEnumerable<string> softBlocks = softToFormat.Length > 0
                    ? PackageSectionFormatter.FormatConstraintBlocks(softToFormat, tokenBudget)
                    : new[] { "(所有软约束已在此前去重包含)" };

                CommitSection(state, policy, modeBudgetProfile, tokenBudget, tokenContext, new SectionDraft
                {
                    Name = "soft_constraints",
                    DefaultPriority = 50,
                    ContentBlocks = softBlocks,
                    Candidates = softCandidates,
                    SourceRefs = ContextItemRefResolver.ResolveSourceRefs(activeSoftConstraints),
                    ItemRefs = ContextItemRefResolver.ResolveItemRefs(activeSoftConstraints),
                    CandidateIds = ContextItemRefResolver.ResolveItemRefs(activeSoftConstraints),
                });
            }
        }

        // merged constraints (constraints section)
        if (PackagePolicyResolver.ShouldIncludeMergedConstraintsSection(request, policy))
        {
            var mergedConstraints = inputs.MergedConstraints!;
            var orderedMergedConstraints = LegacyPackageScorer.OrderMergedConstraints(mergedConstraints.Where(LegacyPackageScorer.IsActive).Where(c => !state.AddedConstraintIds.Contains(c.Id)));
            var activeMergedConstraints = orderedMergedConstraints
                .Select(item => item.Constraint)
                .ToArray();

            var mergedCandidates = orderedMergedConstraints
                .Select(item => PackageTraceCandidate.FromConstraint(
                    item.Constraint,
                    "merged_constraint",
                    item.PriorityRank,
                    _estimateTokens(item.Constraint.Content, tokenContext)))
                .ToArray();

            var mergedToFormat = orderedMergedConstraints.Where(item => !state.GlobalSelectedIds.Contains(item.Constraint.Id)).ToArray();
            IEnumerable<string> mergedBlocks = mergedToFormat.Length > 0
                ? PackageSectionFormatter.FormatMergedConstraintBlocks(mergedToFormat, tokenBudget)
                : new[] { "(所有合并约束已在此前去重包含)" };

            CommitSection(state, policy, modeBudgetProfile, tokenBudget, tokenContext, new SectionDraft
            {
                Name = "constraints",
                DefaultPriority = 95,
                ContentBlocks = mergedBlocks,
                Candidates = mergedCandidates,
                SourceRefs = ContextItemRefResolver.ResolveSourceRefs(activeMergedConstraints),
                ItemRefs = ContextItemRefResolver.ResolveItemRefs(activeMergedConstraints),
                CandidateIds = ContextItemRefResolver.ResolveItemRefs(activeMergedConstraints),
            });
        }

        // graph expansion (related_context)
        if (_graphExpansionCoordinator.IsConfigured && state.SelectedSourceIds.Count > 0)
        {
            var graphSeedIds = await _graphExpansionCoordinator.ResolveGraphSeedIdsFromWorkingMemoryAsync(
                workspaceId,
                collectionId ?? string.Empty,
                workingMemory,
                anchors,
                request,
                policy,
                cancellationToken).ConfigureAwait(false);
            foreach (var graphSeedId in graphSeedIds)
            {
                state.SelectedSourceIds.Add(graphSeedId);
            }

            var relatedItems = await _graphExpansionCoordinator.ResolveRelatedContextAsync(
                workspaceId,
                collectionId ?? string.Empty,
                state.SelectedSourceIds,
                request,
                policy,
                state.LowConfidenceRelations,
                cancellationToken).ConfigureAwait(false);

            if (relatedItems.Count > 0)
            {
                var relatedCandidates = relatedItems
                    .Select(item => PackageTraceCandidate.FromContextItem(item, "related_context", 20.0 + item.Importance * 10.0, _estimateTokens(item.Content, tokenContext)))
                    .ToArray();

                var relatedToFormat = relatedItems.Where(item => !state.GlobalSelectedIds.Contains(item.Id)).ToArray();
                IEnumerable<string> relatedBlocks = relatedToFormat.Length > 0
                    ? PackageSectionFormatter.FormatContextItemBlocks(relatedToFormat)
                    : new[] { "(所有关联图谱扩展上下文已在此前去重包含)" };

                CommitSection(state, policy, modeBudgetProfile, tokenBudget, tokenContext, new SectionDraft
                {
                    Name = "related_context",
                    DefaultPriority = 40,
                    ContentBlocks = relatedBlocks,
                    Candidates = relatedCandidates,
                    SourceRefs = ContextItemRefResolver.ResolveSourceRefs(relatedItems),
                    ItemRefs = ContextItemRefResolver.ResolveItemRefs(relatedItems),
                    CandidateIds = ContextItemRefResolver.ResolveItemRefs(relatedItems),
                });
            }
        }

        // evidence section
        if (PackagePolicyResolver.ShouldIncludeEvidenceSection(request, policy, state.SelectedItems.Count > 0))
        {
            var evidenceItems = PackageSectionFormatter.BuildEvidenceEntries(state.Sections, state.SelectedItems);
            _assembler.AddSection(
                state.Sections,
                state.SourceRefs,
                "evidence",
                PackageSectionBudgetResolver.GetPriority(policy, "evidence", 25),
                PackageSectionFormatter.FormatEvidenceEntries(evidenceItems),
                ContextContentFormat.Markdown,
                evidenceItems.SelectMany(item => item.SourceRefs).ToArray(),
                evidenceItems.Select(item => item.ItemId).ToArray(),
                Array.Empty<string>(),
                tokenBudget,
                PackageSectionBudgetResolver.ResolveSectionTokenBudget(policy, modeBudgetProfile, "evidence", tokenBudget),
                tokenContext,
                ref state.EstimatedTokens);
        }

        // uncertainties + diagnostics sections
        var uncertainties = PackageUncertaintyBuilder.BuildUncertainties(
            state.Sections,
            state.SelectedItems,
            state.DroppedItems,
            state.LowConfidenceRelations,
            tokenBudget,
            state.EstimatedTokens);
        if (PackagePolicyResolver.ShouldIncludeDiagnosticsSection(request, policy, "excluded", state.DroppedItems.Count > 0))
        {
            _assembler.AddSection(
                state.Sections,
                state.SourceRefs,
                "excluded",
                PackageSectionBudgetResolver.GetPriority(policy, "excluded", 20),
                PackageSectionFormatter.FormatDroppedItems(state.DroppedItems),
                ContextContentFormat.Markdown,
                Array.Empty<string>(),
                state.DroppedItems.Select(item => item.ItemId).ToArray(),
                Array.Empty<string>(),
                tokenBudget,
                PackageSectionBudgetResolver.ResolveDiagnosticsSectionTokenBudget(policy, modeBudgetProfile, "excluded", tokenBudget),
                tokenContext,
                ref state.EstimatedTokens);
        }

        if (PackagePolicyResolver.ShouldIncludeDiagnosticsSection(request, policy, "uncertainties", uncertainties.Count > 0))
        {
            _assembler.AddSection(
                state.Sections,
                state.SourceRefs,
                "uncertainties",
                PackageSectionBudgetResolver.GetPriority(policy, "uncertainties", 10),
                PackageSectionFormatter.FormatUncertainties(uncertainties),
                ContextContentFormat.Markdown,
                Array.Empty<string>(),
                uncertainties.SelectMany(item => item.ItemRefs).ToArray(),
                Array.Empty<string>(),
                tokenBudget,
                PackageSectionBudgetResolver.ResolveDiagnosticsSectionTokenBudget(policy, modeBudgetProfile, "uncertainties", tokenBudget),
                tokenContext,
                ref state.EstimatedTokens);
        }

        return new SelectionResult(
            Sections: state.Sections,
            SourceRefs: state.SourceRefs,
            EstimatedTokens: state.EstimatedTokens,
            SelectedItems: state.SelectedItems,
            DroppedItems: state.DroppedItems,
            Anchors: anchors,
            RetrievalPlan: retrievalPlan,
            ItemReferences: state.ItemReferences,
            Uncertainties: uncertainties);
    }

    /// <summary>
    /// 装配一个 section 并同步记录 trace 决策。封装 AddSectionFromBlocks +
    /// AddSectionDecisionsWithDedup 的重复调用序列，保持各 section 处理顺序一致。
    /// </summary>
    private void CommitSection(
        SelectionState state,
        ContextPackagePolicy policy,
        ModeBudgetProfile? modeBudgetProfile,
        int tokenBudget,
        TokenEstimationContext tokenContext,
        SectionDraft draft)
    {
        var sectionBudget = draft.BudgetKind switch
        {
            SectionBudgetKind.Historical => PackageSectionBudgetResolver.ResolveHistoricalSectionTokenBudget(policy, modeBudgetProfile, draft.Name, tokenBudget),
            SectionBudgetKind.Diagnostics => PackageSectionBudgetResolver.ResolveDiagnosticsSectionTokenBudget(policy, modeBudgetProfile, draft.Name, tokenBudget),
            _ => PackageSectionBudgetResolver.ResolveSectionTokenBudget(policy, modeBudgetProfile, draft.Name, tokenBudget),
        };

        var sectionResult = _assembler.AddSectionFromBlocks(
            state.Sections,
            state.SourceRefs,
            draft.Name,
            PackageSectionBudgetResolver.GetPriority(policy, draft.Name, draft.DefaultPriority),
            draft.ContentBlocks,
            ContextContentFormat.Markdown,
            draft.SourceRefs,
            draft.ItemRefs,
            draft.CandidateIds,
            tokenBudget,
            sectionBudget,
            tokenContext,
            ref state.EstimatedTokens);

        _traceRecorder.AddSectionDecisionsWithDedup(
            state.SelectedItems,
            state.DroppedItems,
            draft.Candidates,
            draft.Name,
            sectionResult,
            state.GlobalSelectedIds,
            state.PrimaryDecisions,
            state.ItemReferences);
    }
}

/// <summary>
/// 选择阶段共享的可变状态：所有 section 装配过程中读写的 accumulators 集中在此，
/// 避免向 <see cref="CandidateSelector.CommitSection"/> 传递十几个 ref 参数。
/// </summary>
internal sealed class SelectionState
{
    internal List<ContextPackageSection> Sections { get; } = new();
    internal HashSet<string> SourceRefs { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal int EstimatedTokens;
    internal HashSet<string> SelectedSourceIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal List<ContextPackageDecision> SelectedItems { get; } = new();
    internal List<DroppedContextItem> DroppedItems { get; } = new();
    internal HashSet<string> AddedConstraintIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal List<ContextRelation> LowConfidenceRelations { get; } = new();
    internal HashSet<string> GlobalSelectedIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, ContextPackageDecision> PrimaryDecisions { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal List<ContextPackageItemReference> ItemReferences { get; } = new();
}

/// <summary>
/// 轻量 section 草稿：描述单个 section 装配所需的全部输入（名称、优先级、内容块、
/// 候选列表、引用、预算类别），由 <see cref="CandidateSelector.CommitSection"/> 消费。
/// </summary>
internal sealed class SectionDraft
{
    internal required string Name { get; init; }
    internal int DefaultPriority { get; init; }
    internal IEnumerable<string> ContentBlocks { get; init; } = Array.Empty<string>();
    internal IReadOnlyList<PackageTraceCandidate> Candidates { get; init; } = Array.Empty<PackageTraceCandidate>();
    internal IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();
    internal IReadOnlyList<string> ItemRefs { get; init; } = Array.Empty<string>();
    internal IReadOnlyList<string> CandidateIds { get; init; } = Array.Empty<string>();
    internal SectionBudgetKind BudgetKind { get; init; } = SectionBudgetKind.Normal;
}

internal enum SectionBudgetKind
{
    Normal,
    Historical,
    Diagnostics
}

/// <summary>
/// 选择阶段产出：已装配的 sections、accumulators（selected/dropped/itemReferences）、
/// anchors、retrievalPlan 以及 uncertainties。供 <see cref="ResultProjector"/> 构建最终结果。
/// </summary>
internal sealed record SelectionResult(
    List<ContextPackageSection> Sections,
    HashSet<string> SourceRefs,
    int EstimatedTokens,
    List<ContextPackageDecision> SelectedItems,
    List<DroppedContextItem> DroppedItems,
    IReadOnlyList<ContextAnchor> Anchors,
    RetrievalPlan? RetrievalPlan,
    List<ContextPackageItemReference> ItemReferences,
    IReadOnlyList<ContextPackageUncertainty> Uncertainties);
