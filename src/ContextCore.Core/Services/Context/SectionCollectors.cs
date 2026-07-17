using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Learning.V14_0;

namespace ContextCore.Core;

/// <summary>
/// P0-6.4: 跨 SectionCollector 共享的可变局部状态。承载 collector 之间传递的中间产物
/// （anchors / retrievalPlan / workingMemory / includedRecent / excludedRecent），
/// 使 <see cref="CandidateSelector"/> 编排层无需逐 collector 传递十几个 ref 参数。
/// </summary>
internal sealed class SelectionContext
{
    /// <summary>锚点：由 ShortTermSignalCollector 设置，供 Recall / Expansion collector 消费。</summary>
    internal IReadOnlyList<ContextAnchor> Anchors { get; set; } = Array.Empty<ContextAnchor>();

    /// <summary>recent filter 保留的短期上下文条目。由 ShortTermSignalCollector 设置，供 Recall collector 装配 recent_context section。</summary>
    internal IReadOnlyList<RecentContextItem> IncludedRecent { get; set; } = Array.Empty<RecentContextItem>();

    /// <summary>recent filter 排除的短期上下文条目。由 ShortTermSignalCollector 设置，供 Recall collector 记录 dropped。</summary>
    internal IReadOnlyList<RecentContextItem> ExcludedRecent { get; set; } = Array.Empty<RecentContextItem>();

    /// <summary>活跃工作记忆条目。由 ShortTermSignalCollector 设置，供 Recall（stable_memory 召回）/ Expansion（graph seed）消费。</summary>
    internal IReadOnlyList<ContextMemoryItem> WorkingMemory { get; set; } = Array.Empty<ContextMemoryItem>();

    /// <summary>短期锚定召回计划。由 ShortTermSignalCollector 设置，作为 SelectionResult.RetrievalPlan 返回。</summary>
    internal RetrievalPlan? RetrievalPlan { get; set; }
}

/// <summary>
/// P0-6.4: SectionCollector 基类，封装所有 collector 共享的依赖（assembler / traceRecorder / estimateTokens）
/// 与 section 装配 + 决策记录的重复序列 <see cref="CommitSection"/>。
/// </summary>
internal abstract class SectionCollectorBase
{
    protected readonly SectionAssembler Assembler;
    protected readonly PackageTraceRecorder TraceRecorder;
    protected readonly Func<string?, TokenEstimationContext, int> EstimateTokens;

    protected SectionCollectorBase(
        SectionAssembler assembler,
        PackageTraceRecorder traceRecorder,
        Func<string?, TokenEstimationContext, int> estimateTokens)
    {
        Assembler = assembler;
        TraceRecorder = traceRecorder;
        EstimateTokens = estimateTokens;
    }

    /// <summary>
    /// 装配一个 section 并同步记录 trace 决策。封装 AddSectionFromSegments +
    /// AddSectionDecisionsWithDedup 的重复调用序列，保持各 section 处理顺序一致。
    /// </summary>
    protected void CommitSection(
        SelectionState state,
        ResolvedPackageOptions options,
        SectionDraft draft)
    {
        var policy = options.Policy;
        var modeBudgetProfile = options.ModeBudgetProfile;
        var tokenBudget = options.TokenBudget;
        var tokenContext = options.TokenContext;

        var sectionBudget = draft.BudgetKind switch
        {
            SectionBudgetKind.Historical => PackageSectionBudgetResolver.ResolveHistoricalSectionTokenBudget(policy, modeBudgetProfile, draft.Name, tokenBudget),
            SectionBudgetKind.Diagnostics => PackageSectionBudgetResolver.ResolveDiagnosticsSectionTokenBudget(policy, modeBudgetProfile, draft.Name, tokenBudget),
            _ => PackageSectionBudgetResolver.ResolveSectionTokenBudget(policy, modeBudgetProfile, draft.Name, tokenBudget),
        };

        var sectionResult = Assembler.AddSectionFromSegments(
            state.Sections,
            state.SourceRefs,
            draft.Name,
            PackageSectionBudgetResolver.GetPriority(policy, draft.Name, draft.DefaultPriority),
            draft.Segments,
            draft.FallbackContent,
            ContextContentFormat.Markdown,
            draft.SourceRefs,
            draft.ItemRefs,
            tokenBudget,
            sectionBudget,
            tokenContext,
            ref state.EstimatedTokens);

        TraceRecorder.AddSectionDecisionsWithDedup(
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
/// P0-6.4: 短期信号收集器。处理 current_task、recent filter + anchors + retrievalPlan、
/// hard_constraints、working_memory、historical_context。
/// 产出 <see cref="SelectionContext"/> 的 anchors / retrievalPlan / workingMemory / includedRecent / excludedRecent，
/// 供后续 Recall 与 Expansion collector 消费。
/// 保持与原 CandidateSelector.SelectCandidatesAsync 字节级一致的变异顺序。
/// </summary>
internal sealed class ShortTermSignalCollector : SectionCollectorBase
{
    private readonly RecentContextFilter _recentContextFilter = new();
    private readonly ContextAnchorExtractor _anchorExtractor = new();
    private readonly RetrievalPlanner _planner = new();

    internal ShortTermSignalCollector(
        SectionAssembler assembler,
        PackageTraceRecorder traceRecorder,
        Func<string?, TokenEstimationContext, int> estimateTokens)
        : base(assembler, traceRecorder, estimateTokens)
    {
    }

    internal void Collect(
        SelectionState state,
        SelectionContext ctx,
        PackageInputs inputs,
        ResolvedPackageOptions options)
    {
        var request = options.Request;
        var collectionId = options.CollectionId;
        var tokenBudget = options.TokenBudget;
        var tokenContext = options.TokenContext;
        var packageModeName = options.PackageModeName;
        var packageMustHitIds = options.PackageMustHitIds;
        var isAuditMode = options.IsAuditMode;
        var maxRecentItems = options.MaxRecentItems;

        // 仅在不包含 recent context 时提前提取 anchors（避免空数组提取被 recent 路径覆盖的浪费）
        var anchors = options.IncludeRecentRawContext
            ? Array.Empty<ContextAnchor>()
            : _anchorExtractor.Extract(request, Array.Empty<RecentContextItem>());
        var includedRecent = Array.Empty<RecentContextItem>();
        var excludedRecent = Array.Empty<RecentContextItem>();

        // current_task section（第一个装配，此时 accumulators 为空，与原实现一致）
        if (options.IncludeCurrentTaskSection)
        {
            var currentTask = inputs.CurrentTask;
            if (currentTask is not null)
            {
                var content = PackageSectionFormatter.FormatCurrentTask(currentTask, request);
                var currentTaskCandidate = PackageTraceCandidate.FromCurrentTask(
                    currentTask,
                    EstimateTokens(content, tokenContext));
                CommitSection(state, options, new SectionDraft
                {
                    Name = "current_task",
                    DefaultPriority = 110,
                    Segments = new[] { new CandidateSegment(currentTask.TaskId, content, currentTaskCandidate.SourceRefs, new[] { currentTask.TaskId }) },
                    Candidates = new[] { currentTaskCandidate },
                    SourceRefs = currentTaskCandidate.SourceRefs,
                    ItemRefs = new[] { currentTask.TaskId },
                });
            }
        }

        // recent filter + anchors + retrievalPlan
        if (options.IncludeRecentRawContext)
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
                var c = PackageTraceCandidate.FromConstraint(item, "hard_constraint", 100, EstimateTokens(item.Content, tokenContext));
                TraceRecorder.WriteTraceRow(c, "hard_constraints", RuntimeCandidateOutcome.Dropped,
                    includedTokens: 0, originalTokens: c.EstimatedTokens,
                    reason: "constraint is deprecated or rejected", selectedByScoring: false);
                state.DroppedItems.Add(PackageTraceRecorder.CreateDropped(c, "constraint is deprecated or rejected"));
            }

            if (activeHardConstraints.Count > 0)
            {
                var hardCandidates = activeHardConstraints
                    .Select(item => PackageTraceCandidate.FromConstraint(item, "hard_constraint", 100, EstimateTokens(item.Content, tokenContext)))
                    .ToArray();

                var hardToFormat = activeHardConstraints.Where(c => !state.GlobalSelectedIds.Contains(c.Id)).ToArray();

                CommitSection(state, options, new SectionDraft
                {
                    Name = "hard_constraints",
                    DefaultPriority = 100,
                    Segments = PackageSectionFormatter.FormatConstraintSegments(hardToFormat, tokenBudget),
                    FallbackContent = "(所有硬约束已在更优 Section 中包含)",
                    Candidates = hardCandidates,
                    SourceRefs = ContextItemRefResolver.ResolveSourceRefs(activeHardConstraints),
                    ItemRefs = ContextItemRefResolver.ResolveItemRefs(activeHardConstraints),
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
                maxRecentItems,
                isAuditMode,
                true,
                tokenBudget,
                packageModeName,
                packageMustHitIds,
                options.EnableStrictRelevanceFilter);
            workingWithBreakdowns = WorkingMemoryRecaller.EnsureReservedWorkingMemoryCandidates(
                workingCandidatesRaw,
                workingWithBreakdowns,
                anchors,
                isAuditMode,
                true,
                packageModeName,
                packageMustHitIds,
                options.EnableStrictRelevanceFilter);

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
                    .Select(pair => PackageTraceCandidate.FromMemory(pair.Item, "working_memory", pair.Breakdown, EstimateTokens(pair.Item.Content, tokenContext)))
                    .ToArray();

                var workingToFormat = activeWorking.Where(item => !state.GlobalSelectedIds.Contains(item.Id)).ToArray();

                CommitSection(state, options, new SectionDraft
                {
                    Name = "working_memory",
                    DefaultPriority = 90,
                    Segments = PackageSectionFormatter.FormatMemorySegments(workingToFormat, tokenBudget),
                    FallbackContent = "(所有活跃工作区记忆已在此前去重包含)",
                    Candidates = workingCandidates,
                    SourceRefs = ContextItemRefResolver.ResolveSourceRefs(activeWorking),
                    ItemRefs = ContextItemRefResolver.ResolveItemRefs(activeWorking),
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
                            var c = PackageTraceCandidate.FromMemory(pair.Item, "historical_context", pair.Breakdown, EstimateTokens(pair.Item.Content, tokenContext));
                            c.Metadata["lifecycleStatus"] = "Deprecated";
                            return c;
                        })
                        .ToArray();

                    var historicalToFormat = deprecatedWorking.Where(item => !state.GlobalSelectedIds.Contains(item.Id)).ToArray();

                    CommitSection(state, options, new SectionDraft
                    {
                        Name = "historical_context",
                        DefaultPriority = 15,
                        BudgetKind = SectionBudgetKind.Historical,
                        Segments = PackageSectionFormatter.FormatMemorySegments(historicalToFormat, tokenBudget),
                        FallbackContent = "(所有历史审计记忆已在此前去重包含)",
                        Candidates = historicalCandidates,
                        SourceRefs = ContextItemRefResolver.ResolveSourceRefs(deprecatedWorking),
                        ItemRefs = ContextItemRefResolver.ResolveItemRefs(deprecatedWorking),
                    });
                }
                else
                {
                    // 非审计模式：仅记录 dropped，跳过 token 估算和候选构建
                    foreach (var pair in deprecatedWorkingPairs)
                    {
                        var c = PackageTraceCandidate.FromMemory(pair.Item, "historical_context", pair.Breakdown, 0);
                        c.Metadata["lifecycleStatus"] = "Deprecated";
                        TraceRecorder.WriteTraceRow(c, "historical_context", RuntimeCandidateOutcome.Dropped,
                            includedTokens: 0, originalTokens: c.EstimatedTokens,
                            reason: "deprecated memory is excluded in non-audit mode", selectedByScoring: false);
                        state.DroppedItems.Add(PackageTraceRecorder.CreateDropped(c, "deprecated memory is excluded in non-audit mode"));
                    }
                }
            }
        }

        // 将跨 collector 共享的中间产物写回 SelectionContext
        ctx.Anchors = anchors;
        ctx.IncludedRecent = includedRecent;
        ctx.ExcludedRecent = excludedRecent;
        ctx.WorkingMemory = workingMemory;
        ctx.RetrievalPlan = retrievalPlan;
    }
}

/// <summary>
/// P0-6.4: 召回类 section 收集器。处理 global_context、recent_context section、stable_memory、
/// soft_constraints、merged constraints(constraints section)。消费 ShortTermSignalCollector 产出的
/// anchors / workingMemory / includedRecent / excludedRecent。
/// </summary>
internal sealed class RecallSectionCollector : SectionCollectorBase
{
    internal RecallSectionCollector(
        SectionAssembler assembler,
        PackageTraceRecorder traceRecorder,
        Func<string?, TokenEstimationContext, int> estimateTokens)
        : base(assembler, traceRecorder, estimateTokens)
    {
    }

    internal void Collect(
        SelectionState state,
        SelectionContext ctx,
        PackageInputs inputs,
        ResolvedPackageOptions options)
    {
        var tokenBudget = options.TokenBudget;
        var tokenContext = options.TokenContext;
        var packageModeName = options.PackageModeName;
        var packageMustHitIds = options.PackageMustHitIds;
        var maxRecentItems = options.MaxRecentItems;
        var anchors = ctx.Anchors;
        var workingMemory = ctx.WorkingMemory;
        var includedRecent = ctx.IncludedRecent;
        var excludedRecent = ctx.ExcludedRecent;

        // global_context
        if (inputs.GlobalItems is not null)
        {
            var globalItems = inputs.GlobalItems;

            var globalCandidates = globalItems
                .Select(item => PackageTraceCandidate.FromGlobal(item, "global_context", 8.0 + item.Importance * 2.0, EstimateTokens(item.Content, tokenContext)))
                .ToArray();

            var globalToFormat = globalItems.Where(item => !state.GlobalSelectedIds.Contains(item.Id)).ToArray();

            CommitSection(state, options, new SectionDraft
            {
                Name = "global_context",
                DefaultPriority = 80,
                Segments = PackageSectionFormatter.FormatGlobalSegments(globalToFormat),
                FallbackContent = "(所有全局上下文已在此前去重包含)",
                Candidates = globalCandidates,
                SourceRefs = ContextItemRefResolver.ResolveSourceRefs(globalItems),
                ItemRefs = ContextItemRefResolver.ResolveItemRefs(globalItems),
            });
        }

        // recent_context
        if (options.IncludeRecentRawContext)
        {
            foreach (var item in includedRecent)
            {
                state.SelectedSourceIds.Add(item.SourceItemId);
            }

            state.DroppedItems.AddRange(excludedRecent.Select(item =>
            {
                var c = PackageTraceCandidate.FromRecent(item, "recent_context", item.Relevance * 79.0, EstimateTokens(item.Content, tokenContext));
                TraceRecorder.WriteTraceRow(c, "recent_context", RuntimeCandidateOutcome.Dropped,
                    includedTokens: 0, originalTokens: c.EstimatedTokens,
                    reason: item.ExcludeReason ?? "recent context excluded", selectedByScoring: false);
                return PackageTraceRecorder.CreateDropped(c, item.ExcludeReason ?? "recent context excluded");
            }));

            var recentCandidates = includedRecent
                .Select(item => PackageTraceCandidate.FromRecent(item, "recent_context", item.Relevance * 79.0, EstimateTokens(item.Content, tokenContext)))
                .ToArray();

            var recentToFormat = includedRecent.Where(item => !state.GlobalSelectedIds.Contains(item.SourceItemId)).ToArray();

            CommitSection(state, options, new SectionDraft
            {
                Name = "recent_context",
                DefaultPriority = 70,
                Segments = PackageSectionFormatter.FormatRecentContextSegments(recentToFormat, tokenBudget),
                FallbackContent = "(所有近期短期上下文已在此前去重包含)",
                Candidates = recentCandidates,
                SourceRefs = ContextItemRefResolver.ResolveSourceRefs(includedRecent),
                ItemRefs = ContextItemRefResolver.ResolveItemRefs(includedRecent),
            });
        }

        // stable_memory
        if (inputs.StableCandidatesRaw is not null)
        {
            var stableCandidatesRaw = inputs.StableCandidatesRaw;
            var stableMemory = WorkingMemoryRecaller.RecallStableMemory(
                stableCandidatesRaw,
                anchors,
                workingMemory,
                maxRecentItems,
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
                    return PackageTraceCandidate.FromMemory(item, "stable_memory", finalScore, EstimateTokens(item.Content, tokenContext));
                })
                .ToArray();

            var stableToFormat = stableMemory.Where(item => !state.GlobalSelectedIds.Contains(item.Id)).ToArray();

            CommitSection(state, options, new SectionDraft
            {
                Name = "stable_memory",
                DefaultPriority = 60,
                Segments = PackageSectionFormatter.FormatMemorySegments(stableToFormat, tokenBudget),
                FallbackContent = "(所有稳定背景记忆已在此前去重包含)",
                Candidates = stableCandidates,
                SourceRefs = ContextItemRefResolver.ResolveSourceRefs(stableMemory),
                ItemRefs = ContextItemRefResolver.ResolveItemRefs(stableMemory),
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
                    var c = PackageTraceCandidate.FromConstraint(item, "soft_constraint", 15.0, EstimateTokens(item.Content, tokenContext));
                    TraceRecorder.WriteTraceRow(c, "soft_constraints", RuntimeCandidateOutcome.Dropped,
                        includedTokens: 0, originalTokens: c.EstimatedTokens,
                        reason: "constraint is deprecated or rejected", selectedByScoring: false);
                    return PackageTraceRecorder.CreateDropped(c, "constraint is deprecated or rejected");
                }));

            if (activeSoftConstraints.Length > 0)
            {
                var softCandidates = activeSoftConstraints
                    .Select(item => PackageTraceCandidate.FromConstraint(item, "soft_constraint", 15.0, EstimateTokens(item.Content, tokenContext)))
                    .ToArray();

                var softToFormat = activeSoftConstraints.Where(c => !state.GlobalSelectedIds.Contains(c.Id)).ToArray();

                CommitSection(state, options, new SectionDraft
                {
                    Name = "soft_constraints",
                    DefaultPriority = 50,
                    Segments = PackageSectionFormatter.FormatConstraintSegments(softToFormat, tokenBudget),
                    FallbackContent = "(所有软约束已在此前去重包含)",
                    Candidates = softCandidates,
                    SourceRefs = ContextItemRefResolver.ResolveSourceRefs(activeSoftConstraints),
                    ItemRefs = ContextItemRefResolver.ResolveItemRefs(activeSoftConstraints),
                });
            }
        }

        // merged constraints (constraints section)
        if (options.IncludeMergedConstraintsSection)
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
                    EstimateTokens(item.Constraint.Content, tokenContext)))
                .ToArray();

            var mergedToFormat = orderedMergedConstraints.Where(item => !state.GlobalSelectedIds.Contains(item.Constraint.Id)).ToArray();

            CommitSection(state, options, new SectionDraft
            {
                Name = "constraints",
                DefaultPriority = 95,
                Segments = PackageSectionFormatter.FormatMergedConstraintSegments(mergedToFormat, tokenBudget),
                FallbackContent = "(所有合并约束已在此前去重包含)",
                Candidates = mergedCandidates,
                SourceRefs = ContextItemRefResolver.ResolveSourceRefs(activeMergedConstraints),
                ItemRefs = ContextItemRefResolver.ResolveItemRefs(activeMergedConstraints),
            });
        }
    }
}

/// <summary>
/// P0-6.4: 图谱扩展与诊断 section 收集器。处理 related_context（graph 扩展，async）、evidence、
/// excluded、uncertainties。消费 ShortTermSignalCollector 产出的 anchors / workingMemory 与 state.SelectedSourceIds。
/// 返回 uncertainties 供 SelectionResult 使用。
/// </summary>
internal sealed class ExpansionDiagnosticsCollector : SectionCollectorBase
{
    private readonly GraphExpansionCoordinator _graphExpansionCoordinator;

    internal ExpansionDiagnosticsCollector(
        SectionAssembler assembler,
        PackageTraceRecorder traceRecorder,
        Func<string?, TokenEstimationContext, int> estimateTokens,
        GraphExpansionCoordinator graphExpansionCoordinator)
        : base(assembler, traceRecorder, estimateTokens)
    {
        _graphExpansionCoordinator = graphExpansionCoordinator;
    }

    internal async Task<IReadOnlyList<ContextPackageUncertainty>> CollectAsync(
        SelectionState state,
        SelectionContext ctx,
        PackageInputs inputs,
        ResolvedPackageOptions options,
        CancellationToken cancellationToken)
    {
        var request = options.Request;
        var policy = options.Policy;
        var workspaceId = options.WorkspaceId;
        var collectionId = options.CollectionId;
        var tokenBudget = options.TokenBudget;
        var tokenContext = options.TokenContext;
        var modeBudgetProfile = options.ModeBudgetProfile;
        var anchors = ctx.Anchors;
        var workingMemory = ctx.WorkingMemory;

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
                    .Select(item => PackageTraceCandidate.FromContextItem(item, "related_context", 20.0 + item.Importance * 10.0, EstimateTokens(item.Content, tokenContext)))
                    .ToArray();

                var relatedToFormat = relatedItems.Where(item => !state.GlobalSelectedIds.Contains(item.Id)).ToArray();

                CommitSection(state, options, new SectionDraft
                {
                    Name = "related_context",
                    DefaultPriority = 40,
                    Segments = PackageSectionFormatter.FormatContextItemSegments(relatedToFormat),
                    FallbackContent = "(所有关联图谱扩展上下文已在此前去重包含)",
                    Candidates = relatedCandidates,
                    SourceRefs = ContextItemRefResolver.ResolveSourceRefs(relatedItems),
                    ItemRefs = ContextItemRefResolver.ResolveItemRefs(relatedItems),
                });
            }
        }

        // evidence section
        if (options.ShouldIncludeEvidenceSection(state.SelectedItems.Count > 0))
        {
            var evidenceItems = PackageSectionFormatter.BuildEvidenceEntries(state.Sections, state.SelectedItems);
            Assembler.AddSection(
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
        if (options.ShouldIncludeDiagnosticsSection("excluded", state.DroppedItems.Count > 0))
        {
            Assembler.AddSection(
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

        if (options.ShouldIncludeDiagnosticsSection("uncertainties", uncertainties.Count > 0))
        {
            Assembler.AddSection(
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

        return uncertainties;
    }
}
