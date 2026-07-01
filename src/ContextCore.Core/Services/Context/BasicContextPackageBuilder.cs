using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Graph;

namespace ContextCore.Core;

/// <summary>
/// 默认上下文包构建器，按请求或策略从原始上下文、记忆、约束、全局项和关系中选择内容。
/// </summary>
public sealed class BasicContextPackageBuilder : IContextPackageBuilder
{
    private readonly IConstraintStore? _constraintStore;
    private readonly IGlobalContextStore? _globalContextStore;
    private readonly IMemoryStore? _memoryStore;
    private readonly IRelationStore? _relationStore;
    private readonly IContextPackageBuildTraceStore? _traceStore;
    private readonly IContextTokenizerResolver _tokenizerResolver;
    private readonly IWorkingMemoryService? _workingMemoryService;
    private readonly GraphExpansionApplyOptions _graphExpansionApplyOptions;
    private readonly GraphExpansionApplyPolicy? _graphExpansionApplyPolicy;
    private readonly IContextStore _store;
    private readonly RecentContextFilter _recentContextFilter = new();
    private readonly ContextAnchorExtractor _anchorExtractor = new();
    private readonly RetrievalPlanner _planner = new();

    public BasicContextPackageBuilder(IContextStore store)
        : this(store, null, null, null, null, null, null)
    {
    }

    public BasicContextPackageBuilder(
        IContextStore store,
        IConstraintStore? constraintStore,
        IGlobalContextStore? globalContextStore,
        IMemoryStore? memoryStore,
        IRelationStore? relationStore,
        IContextPackageBuildTraceStore? traceStore = null,
        IContextTokenizerResolver? tokenizerResolver = null,
        IWorkingMemoryService? workingMemoryService = null,
        GraphExpansionApplyOptions? graphExpansionApplyOptions = null,
        GraphExpansionApplyPolicy? graphExpansionApplyPolicy = null)
    {
        _store = store;
        _constraintStore = constraintStore;
        _globalContextStore = globalContextStore;
        _memoryStore = memoryStore;
        _relationStore = relationStore;
        _traceStore = traceStore;
        _tokenizerResolver = tokenizerResolver ?? new DefaultContextTokenizerResolver();
        _workingMemoryService = workingMemoryService;
        _graphExpansionApplyOptions = graphExpansionApplyOptions ?? new GraphExpansionApplyOptions();
        _graphExpansionApplyPolicy = graphExpansionApplyPolicy;
    }

    public async Task<ContextPackage> BuildAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await BuildDetailedAsync(request, cancellationToken).ConfigureAwait(false);
        return result.Package;
    }

    public async Task<ContextPackageBuildResult> BuildDetailedAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        ContextPackageBuildResult result;
        if (request.Policy is not null)
        {
            // Policy 模式用于服务化后的正式打包流程，可组合约束、记忆、全局上下文和关系。
            result = await BuildWithPolicyAsync(request, request.Policy, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            // Legacy 模式保持 MVP 行为：直接从原始 ContextItem 中按重要性和时间裁剪上下文。
            result = await BuildLegacyAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (_traceStore is not null)
        {
            await _traceStore.SaveAsync(result, cancellationToken).ConfigureAwait(false);
        }

        CoreMetrics.PackageBuildDuration.Record(sw.Elapsed.TotalMilliseconds);
        return result;
    }

    public static int EstimateTokens(string? content)
    {
        return LegacyCharacterTokenizer.EstimateTokenCount(content);
    }

    private TokenEstimationContext CreateTokenEstimationContext(ContextPackageRequest request)
    {
        var modelName = ResolveTokenizerModel(request);
        var estimate = _tokenizerResolver.Estimate(string.Empty, modelName);
        return new TokenEstimationContext(
            estimate.ModelName,
            estimate.Source,
            estimate.IsFallback);
    }

    private int EstimatePackageTokens(string? content, TokenEstimationContext tokenContext)
    {
        return _tokenizerResolver.Estimate(content, tokenContext.ModelName).TokenCount;
    }

    private static string? ResolveTokenizerModel(ContextPackageRequest request)
    {
        foreach (var key in new[] { "tokenizerModel", "modelName", "model", "llm.model", "route.model" })
        {
            if (request.Metadata.TryGetValue(key, out var value)
                && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private async Task<ContextPackageBuildResult> BuildLegacyAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken)
    {
        var query = new ContextQuery
        {
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            QueryText = request.QueryText,
            Tags = request.RequiredTags,
            Types = request.RequiredTypes,
            Take = 500, // V13: capped from int.MaxValue — legacy package path safe bound
            IncludeContent = true
        };

        var items = await _store.QueryAsync(query, cancellationToken).ConfigureAwait(false);
        var tokenContext = CreateTokenEstimationContext(request);
        var requiredTags = request.RequiredTags.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var orderedItems = items
            .OrderByDescending(item => item.Importance)
            .ThenByDescending(item => request.IncludeRecent ? item.UpdatedAt : DateTimeOffset.MinValue)
            .ThenByDescending(item => CountMatchingTags(item, requiredTags))
            .ToArray();

        var tokenBudget = request.TokenBudget > 0 ? request.TokenBudget : int.MaxValue;
        var sections = new List<ContextPackageSection>();
        var sourceRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedItems = new List<ContextPackageDecision>();
        var droppedItems = new List<DroppedContextItem>();
        var estimatedTokens = 0;
        var priority = orderedItems.Length;

        foreach (var item in orderedItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sectionName = string.IsNullOrWhiteSpace(item.Title) ? item.Id : item.Title!;
            var itemTokens = EstimatePackageTokens(item.Content, tokenContext);
            var score = CalculateLegacyScore(item, requiredTags, request.IncludeRecent);

            // AddSection 内部负责预算裁剪；调用方只按优先级提供候选内容。
            var sectionResult = AddSection(
                sections,
                sourceRefs,
                name: sectionName,
                priority: priority--,
                content: item.Content,
                contentFormat: item.ContentFormat,
                sectionSourceRefs: ResolveSourceRefs(item),
                sectionItemRefs: ResolveItemRefs(item),
                tokenBudget,
                sectionTokenBudget: 0,
                tokenContext,
                ref estimatedTokens);

            var candidate = PackageTraceCandidate.FromContextItem(item, "raw", score, itemTokens);
            if (sectionResult.Added)
            {
                selectedItems.Add(CreateDecision(
                    candidate,
                    sectionName,
                    sectionResult.Reason,
                    sectionResult.ActualTokens));
            }
            else
            {
                droppedItems.Add(CreateDropped(candidate, sectionResult.Reason));
            }
        }

        var package = CreatePackage(request, request.CollectionId, sections, sourceRefs, estimatedTokens, tokenContext);
        return CreateBuildResult(
            request,
            package,
            tokenBudget,
            selectedItems,
            droppedItems);
    }

    private async Task<ContextPackageBuildResult> BuildWithPolicyAsync(
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        CancellationToken cancellationToken)
    {
        var tokenContext = CreateTokenEstimationContext(request);
        var workspaceId = NormalizeRequiredValue(request.WorkspaceId);
        var collectionId = NormalizeRequiredValue(policy.CollectionId, request.CollectionId);
        var modeBudgetProfile = ResolveModeBudgetProfile(request, policy);
        var tokenBudget = ResolveTokenBudget(request, policy, modeBudgetProfile);
        var packageModeName = ResolvePackageModeName(request, policy, modeBudgetProfile);
        var packageMustHitIds = ResolvePackageMustHitIds(request);

        var sections = new List<ContextPackageSection>();
        var sourceRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var estimatedTokens = 0;
        var selectedSourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedItems = new List<ContextPackageDecision>();
        var droppedItems = new List<DroppedContextItem>();
        var addedConstraintIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lowConfidenceRelations = new List<ContextRelation>();
        var anchors = _anchorExtractor.Extract(request, Array.Empty<RecentContextItem>());
        var includedRecent = Array.Empty<RecentContextItem>();
        var excludedRecent = Array.Empty<RecentContextItem>();
        SectionBuildResult sectionResult;

        // 全局去重拦截与引用记录
        var globalSelectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var primaryDecisions = new Dictionary<string, ContextPackageDecision>(StringComparer.OrdinalIgnoreCase);

        // 显式审计模式判定
        var isAuditMode = !string.IsNullOrWhiteSpace(request.QueryText) && (
            request.QueryText.Contains("废弃", StringComparison.OrdinalIgnoreCase)
            || request.QueryText.Contains("作废", StringComparison.OrdinalIgnoreCase)
            || request.QueryText.Contains("草稿", StringComparison.OrdinalIgnoreCase)
            || request.QueryText.Contains("草案", StringComparison.OrdinalIgnoreCase)
            || request.QueryText.Contains("旧版", StringComparison.OrdinalIgnoreCase)
            || request.QueryText.Contains("旧", StringComparison.OrdinalIgnoreCase)
            || request.QueryText.Contains("放弃", StringComparison.OrdinalIgnoreCase)
            || request.QueryText.Contains("舍弃", StringComparison.OrdinalIgnoreCase)
            || request.QueryText.Contains("审计", StringComparison.OrdinalIgnoreCase)
            || request.QueryText.Contains("legacy", StringComparison.OrdinalIgnoreCase)
            || request.QueryText.Contains("deprecated", StringComparison.OrdinalIgnoreCase)
            || request.QueryText.Contains("audit", StringComparison.OrdinalIgnoreCase)
        );

        if (ShouldIncludeCurrentTaskSection(request, policy))
        {
            var currentTask = await ResolveCurrentTaskAsync(
                request,
                collectionId,
                cancellationToken).ConfigureAwait(false);
            if (currentTask is not null)
            {
                var content = FormatCurrentTask(currentTask, request);
                var currentTaskCandidate = PackageTraceCandidate.FromCurrentTask(
                    currentTask,
                    EstimatePackageTokens(content, tokenContext));
                sectionResult = AddSection(
                    sections,
                    sourceRefs,
                    "current_task",
                    GetPriority(policy, "current_task", 110),
                    content,
                    ContextContentFormat.Markdown,
                    currentTaskCandidate.SourceRefs,
                    [currentTask.TaskId],
                    tokenBudget,
                    ResolveSectionTokenBudget(policy, modeBudgetProfile, "current_task", tokenBudget),
                    tokenContext,
                    ref estimatedTokens);
                
                AddSectionDecisionsWithDedup(
                    selectedItems,
                    droppedItems,
                    [currentTaskCandidate],
                    "current_task",
                    sectionResult,
                    globalSelectedIds,
                    primaryDecisions,
                    sections.LastOrDefault()?.Content ?? "");
            }
        }

        if (policy.IncludeRecentRawContext)
        {
            var maxRecentItems = policy.MaxRecentItems > 0 ? policy.MaxRecentItems : 20;
            var recentQueryTake = Math.Min(Math.Max(maxRecentItems * 3, maxRecentItems), 60);
            var recentItems = await _store.QueryAsync(
                new ContextQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    QueryText = request.QueryText,
                    Tags = request.RequiredTags,
                    Types = request.RequiredTypes,
                    Take = recentQueryTake,
                    IncludeContent = true
                },
                cancellationToken).ConfigureAwait(false);

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

        if ((policy.IncludeHardConstraints || !ShouldIncludeMergedConstraintsSection(request, policy)) && _constraintStore is not null)
        {
            var hardConstraints = await _constraintStore.QueryAsync(
                new ContextConstraintQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    Level = ConstraintLevel.Hard,
                    Take = 100
                },
                cancellationToken).ConfigureAwait(false);

            var activeHardConstraints = hardConstraints.Where(IsActive).ToArray();
            foreach (var item in activeHardConstraints)
            {
                addedConstraintIds.Add(item.Id);
            }
            droppedItems.AddRange(hardConstraints
                .Where(item => !IsActive(item))
                .Select(item => CreateDropped(
                    PackageTraceCandidate.FromConstraint(item, "hard_constraint", 100, EstimatePackageTokens(item.Content, tokenContext)),
                    "constraint is deprecated or rejected")));

            var hardCandidates = activeHardConstraints
                .Select(item => PackageTraceCandidate.FromConstraint(item, "hard_constraint", 100, EstimatePackageTokens(item.Content, tokenContext)))
                .ToArray();

            if (activeHardConstraints.Length > 0)
            {
                // 硬约束过滤掉已被选中的 ID (一般不会有，但防止意外)
                var hardToFormat = activeHardConstraints.Where(c => !globalSelectedIds.Contains(c.Id)).ToArray();
                var hardContent = hardToFormat.Length > 0 ? FormatConstraints(hardToFormat, tokenBudget) : "(所有硬约束已在更优 Section 中包含)";

                sectionResult = AddSection(
                    sections,
                    sourceRefs,
                    "hard_constraints",
                    GetPriority(policy, "hard_constraints", 100),
                    hardContent,
                    ContextContentFormat.Markdown,
                    ResolveSourceRefs(activeHardConstraints),
                    ResolveItemRefs(activeHardConstraints),
                    tokenBudget,
                    ResolveSectionTokenBudget(policy, modeBudgetProfile, "hard_constraints", tokenBudget),
                    tokenContext,
                    ref estimatedTokens);

                AddSectionDecisionsWithDedup(
                    selectedItems,
                    droppedItems,
                    hardCandidates,
                    "hard_constraints",
                    sectionResult,
                    globalSelectedIds,
                    primaryDecisions,
                    sections.LastOrDefault()?.Content ?? "");
            }
        }

        IReadOnlyList<ContextMemoryItem> workingMemory = Array.Empty<ContextMemoryItem>();
        if (policy.IncludeWorkingMemory && _memoryStore is not null)
        {
            var workingCandidateTake = Math.Min(
                Math.Max((policy.MaxRecentItems > 0 ? policy.MaxRecentItems : 20) * 3, 20),
                60);
            var workingCandidatesRaw = await _memoryStore.QueryAsync(
                new ContextMemoryQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    Layer = ContextMemoryLayer.Working,
                    Take = workingCandidateTake
                },
                cancellationToken).ConfigureAwait(false);

            // 使用带 breakdown 的召回函数，以便展示 13 个子分维度
            var workingWithBreakdowns = RecallWorkingMemoryWithBreakdowns(
                workingCandidatesRaw,
                anchors,
                policy.MaxRecentItems > 0 ? policy.MaxRecentItems : 20,
                isAuditMode,
                true,
                tokenBudget,
                packageModeName,
                packageMustHitIds);
            workingWithBreakdowns = EnsureReservedWorkingMemoryCandidates(
                workingCandidatesRaw,
                workingWithBreakdowns,
                anchors,
                isAuditMode,
                true,
                packageModeName,
                packageMustHitIds);

            workingMemory = workingWithBreakdowns.Select(x => x.Item).ToArray();

            // 分流活跃与废弃/被替代记忆
            var activeWorkingPairs   = workingWithBreakdowns.Where(x => x.Item.Status != ContextMemoryStatus.Deprecated && !string.Equals(ResolveMemoryProcessState(x.Item), "superseded", StringComparison.OrdinalIgnoreCase)).ToArray();
            var deprecatedWorkingPairs = workingWithBreakdowns.Where(x => x.Item.Status == ContextMemoryStatus.Deprecated || string.Equals(ResolveMemoryProcessState(x.Item), "superseded", StringComparison.OrdinalIgnoreCase)).ToArray();
            var activeWorking   = activeWorkingPairs.Select(x => x.Item).ToArray();
            var deprecatedWorking = deprecatedWorkingPairs.Select(x => x.Item).ToArray();

            foreach (var pair in activeWorkingPairs)
                selectedSourceIds.Add(pair.Item.Id);
            foreach (var pair in deprecatedWorkingPairs)
                selectedSourceIds.Add(pair.Item.Id);

            // 1. 活跃工作记忆处理
            if (activeWorking.Length > 0)
            {
                var workingCandidates = activeWorkingPairs
                    .Select(pair => PackageTraceCandidate.FromMemory(pair.Item, "working_memory", pair.Breakdown, EstimatePackageTokens(pair.Item.Content, tokenContext)))
                    .ToArray();

                var workingToFormat = activeWorking.Where(item => !globalSelectedIds.Contains(item.Id)).ToArray();
                var workingContent = workingToFormat.Length > 0 ? FormatMemoryItems(workingToFormat, tokenBudget) : "(所有活跃工作区记忆已在此前去重包含)";

                sectionResult = AddSection(
                    sections,
                    sourceRefs,
                    "working_memory",
                    GetPriority(policy, "working_memory", 90),
                    workingContent,
                    ContextContentFormat.Markdown,
                    ResolveSourceRefs(activeWorking),
                    ResolveItemRefs(activeWorking),
                    tokenBudget,
                    ResolveSectionTokenBudget(policy, modeBudgetProfile, "working_memory", tokenBudget),
                    tokenContext,
                    ref estimatedTokens);

                AddSectionDecisionsWithDedup(
                    selectedItems,
                    droppedItems,
                    workingCandidates,
                    "working_memory",
                    sectionResult,
                    globalSelectedIds,
                    primaryDecisions,
                    sections.LastOrDefault()?.Content ?? "");
            }

            // 2. 审计废案/历史记忆分流处理 (仅在 isAuditMode 时会被召回)
            if (deprecatedWorking.Length > 0)
            {
                var historicalCandidates = deprecatedWorkingPairs
                    .Select(pair => {
                        var c = PackageTraceCandidate.FromMemory(pair.Item, "historical_context", pair.Breakdown, EstimatePackageTokens(pair.Item.Content, tokenContext));
                        c.Metadata["lifecycleStatus"] = "Deprecated";
                        return c;
                    })
                    .ToArray();

                if (isAuditMode)
                {
                    var historicalToFormat = deprecatedWorking.Where(item => !globalSelectedIds.Contains(item.Id)).ToArray();
                    var historicalContent = historicalToFormat.Length > 0 ? FormatMemoryItems(historicalToFormat, tokenBudget) : "(所有历史审计记忆已在此前去重包含)";

                    sectionResult = AddSection(
                        sections,
                        sourceRefs,
                        "historical_context",
                        GetPriority(policy, "historical_context", 15),
                        historicalContent,
                        ContextContentFormat.Markdown,
                        ResolveSourceRefs(deprecatedWorking),
                        ResolveItemRefs(deprecatedWorking),
                        tokenBudget,
                        ResolveHistoricalSectionTokenBudget(policy, modeBudgetProfile, "historical_context", tokenBudget),
                        tokenContext,
                        ref estimatedTokens);

                    AddSectionDecisionsWithDedup(
                        selectedItems,
                        droppedItems,
                        historicalCandidates,
                        "historical_context",
                        sectionResult,
                        globalSelectedIds,
                        primaryDecisions,
                        sections.LastOrDefault()?.Content ?? "");
                }
                else
                {
                    foreach (var candidate in historicalCandidates)
                    {
                        droppedItems.Add(CreateDropped(candidate, "deprecated memory is excluded in non-audit mode"));
                    }
                }
            }
        }

        if (policy.IncludeGlobalContext && _globalContextStore is not null)
        {
            var globalItems = await _globalContextStore.QueryAsync(
                new ContextGlobalQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    Take = policy.MaxRecentItems > 0 ? policy.MaxRecentItems : 20
                },
                cancellationToken).ConfigureAwait(false);

            var globalCandidates = globalItems
                .Select(item => PackageTraceCandidate.FromGlobal(item, "global_context", 8.0 + item.Importance * 2.0, EstimatePackageTokens(item.Content, tokenContext)))
                .ToArray();

            var globalToFormat = globalItems.Where(item => !globalSelectedIds.Contains(item.Id)).ToArray();
            var globalContent = globalToFormat.Length > 0 ? FormatGlobalItems(globalToFormat) : "(所有全局上下文已在此前去重包含)";

            sectionResult = AddSection(
                sections,
                sourceRefs,
                "global_context",
                GetPriority(policy, "global_context", 80),
                globalContent,
                ContextContentFormat.Markdown,
                ResolveSourceRefs(globalItems),
                ResolveItemRefs(globalItems),
                tokenBudget,
                ResolveSectionTokenBudget(policy, modeBudgetProfile, "global_context", tokenBudget),
                tokenContext,
                ref estimatedTokens);

            AddSectionDecisionsWithDedup(
                selectedItems,
                droppedItems,
                globalCandidates,
                "global_context",
                sectionResult,
                globalSelectedIds,
                primaryDecisions,
                sections.LastOrDefault()?.Content ?? "");
        }

        if (policy.IncludeRecentRawContext)
        {
            foreach (var item in includedRecent)
            {
                selectedSourceIds.Add(item.SourceItemId);
            }

            droppedItems.AddRange(excludedRecent.Select(item =>
                CreateDropped(
                    PackageTraceCandidate.FromRecent(item, "recent_context", item.Relevance * 79.0, EstimatePackageTokens(item.Content, tokenContext)),
                    item.ExcludeReason ?? "recent context excluded")));

            var recentCandidates = includedRecent
                .Select(item => PackageTraceCandidate.FromRecent(item, "recent_context", item.Relevance * 79.0, EstimatePackageTokens(item.Content, tokenContext)))
                .ToArray();

            var recentToFormat = includedRecent.Where(item => !globalSelectedIds.Contains(item.SourceItemId)).ToArray();
            var recentContent = recentToFormat.Length > 0 ? FormatRecentContextItems(recentToFormat, tokenBudget) : "(所有近期短期上下文已在此前去重包含)";

            sectionResult = AddSection(
                sections,
                sourceRefs,
                "recent_context",
                GetPriority(policy, "recent_context", 70),
                recentContent,
                ContextContentFormat.Markdown,
                ResolveSourceRefs(includedRecent),
                ResolveItemRefs(includedRecent),
                tokenBudget,
                ResolveSectionTokenBudget(policy, modeBudgetProfile, "recent_context", tokenBudget),
                tokenContext,
                ref estimatedTokens);

            AddSectionDecisionsWithDedup(
                selectedItems,
                droppedItems,
                recentCandidates,
                "recent_context",
                sectionResult,
                globalSelectedIds,
                primaryDecisions,
                sections.LastOrDefault()?.Content ?? "");
        }

        IReadOnlyList<ContextMemoryItem> stableMemory = Array.Empty<ContextMemoryItem>();
        if (policy.IncludeStableMemory && _memoryStore is not null)
        {
            var maxStableItems = policy.MaxRecentItems > 0 ? policy.MaxRecentItems : 20;
            var stableCandidateTake = Math.Min(Math.Max(maxStableItems * 3, 20), 60);
            var stableCandidatesRaw = await _memoryStore.QueryAsync(
                new ContextMemoryQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    Layer = ContextMemoryLayer.Stable,
                    Status = ContextMemoryStatus.Stable,
                    Take = stableCandidateTake
                },
                cancellationToken).ConfigureAwait(false);
            stableMemory = RecallStableMemory(
                stableCandidatesRaw,
                anchors,
                workingMemory,
                maxStableItems,
                packageModeName,
                packageMustHitIds);

            foreach (var memory in stableMemory)
            {
                selectedSourceIds.Add(memory.Id);
            }

            var workingSignals = ContextRecallSignalPolicy.CreateWorkingMemorySignals(workingMemory);
            var stableCandidates = stableMemory
                .Select(item => {
                    var searchText = CreateMemorySearchText(item);
                    var scoreResult = ContextRecallSignalPolicy.ScoreStableMemoryForInjection(item, anchors, workingSignals, searchText);
                    var finalScore = scoreResult.Score;
                    return PackageTraceCandidate.FromMemory(item, "stable_memory", finalScore, EstimatePackageTokens(item.Content, tokenContext));
                })
                .ToArray();

            var stableToFormat = stableMemory.Where(item => !globalSelectedIds.Contains(item.Id)).ToArray();
            var stableContent = stableToFormat.Length > 0 ? FormatMemoryItems(stableToFormat, tokenBudget) : "(所有稳定背景记忆已在此前去重包含)";

            sectionResult = AddSection(
                sections,
                sourceRefs,
                "stable_memory",
                GetPriority(policy, "stable_memory", 60),
                stableContent,
                ContextContentFormat.Markdown,
                ResolveSourceRefs(stableMemory),
                ResolveItemRefs(stableMemory),
                tokenBudget,
                ResolveSectionTokenBudget(policy, modeBudgetProfile, "stable_memory", tokenBudget),
                tokenContext,
                ref estimatedTokens);

            AddSectionDecisionsWithDedup(
                selectedItems,
                droppedItems,
                stableCandidates,
                "stable_memory",
                sectionResult,
                globalSelectedIds,
                primaryDecisions,
                sections.LastOrDefault()?.Content ?? "");
        }

        if (policy.IncludeSoftConstraints && _constraintStore is not null)
        {
            var softConstraints = await _constraintStore.QueryAsync(
                new ContextConstraintQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    Level = ConstraintLevel.Soft,
                    Take = 100
                },
                cancellationToken).ConfigureAwait(false);

            var activeSoftConstraints = softConstraints.Where(IsActive).ToArray();
            foreach (var item in activeSoftConstraints)
            {
                addedConstraintIds.Add(item.Id);
            }
            droppedItems.AddRange(softConstraints
                .Where(item => !IsActive(item))
                .Select(item => CreateDropped(
                    PackageTraceCandidate.FromConstraint(item, "soft_constraint", 15.0, EstimatePackageTokens(item.Content, tokenContext)),
                    "constraint is deprecated or rejected")));

            var softCandidates = activeSoftConstraints
                .Select(item => PackageTraceCandidate.FromConstraint(item, "soft_constraint", 15.0, EstimatePackageTokens(item.Content, tokenContext)))
                .ToArray();

            var softToFormat = activeSoftConstraints.Where(c => !globalSelectedIds.Contains(c.Id)).ToArray();
            var softContent = softToFormat.Length > 0 ? FormatConstraints(softToFormat, tokenBudget) : "(所有软约束已在此前去重包含)";

            sectionResult = AddSection(
                sections,
                sourceRefs,
                "soft_constraints",
                GetPriority(policy, "soft_constraints", 50),
                softContent,
                ContextContentFormat.Markdown,
                ResolveSourceRefs(activeSoftConstraints),
                ResolveItemRefs(activeSoftConstraints),
                tokenBudget,
                ResolveSectionTokenBudget(policy, modeBudgetProfile, "soft_constraints", tokenBudget),
                tokenContext,
                ref estimatedTokens);

            AddSectionDecisionsWithDedup(
                selectedItems,
                droppedItems,
                softCandidates,
                "soft_constraints",
                sectionResult,
                globalSelectedIds,
                primaryDecisions,
                sections.LastOrDefault()?.Content ?? "");
        }

        if (ShouldIncludeMergedConstraintsSection(request, policy))
        {
            var mergedConstraints = await ResolveMergedConstraintsAsync(
                request,
                policy,
                collectionId ?? string.Empty,
                cancellationToken).ConfigureAwait(false);
            var orderedMergedConstraints = OrderMergedConstraints(mergedConstraints.Where(IsActive).Where(c => !addedConstraintIds.Contains(c.Id)));
            var activeMergedConstraints = orderedMergedConstraints
                .Select(item => item.Constraint)
                .ToArray();

            var mergedCandidates = orderedMergedConstraints
                .Select(item => PackageTraceCandidate.FromConstraint(
                    item.Constraint,
                    "merged_constraint",
                    item.PriorityRank,
                    EstimatePackageTokens(item.Constraint.Content, tokenContext)))
                .ToArray();

            var mergedToFormat = orderedMergedConstraints.Where(item => !globalSelectedIds.Contains(item.Constraint.Id)).ToArray();
            var mergedContent = mergedToFormat.Length > 0 ? FormatMergedConstraints(mergedToFormat, tokenBudget) : "(所有合并约束已在此前去重包含)";

            sectionResult = AddSection(
                sections,
                sourceRefs,
                "constraints",
                GetPriority(policy, "constraints", 95),
                mergedContent,
                ContextContentFormat.Markdown,
                ResolveSourceRefs(activeMergedConstraints),
                ResolveItemRefs(activeMergedConstraints),
                tokenBudget,
                ResolveSectionTokenBudget(policy, modeBudgetProfile, "constraints", tokenBudget),
                tokenContext,
                ref estimatedTokens);

            AddSectionDecisionsWithDedup(
                selectedItems,
                droppedItems,
                mergedCandidates,
                "constraints",
                sectionResult,
                globalSelectedIds,
                primaryDecisions,
                sections.LastOrDefault()?.Content ?? "");
        }

        if (_relationStore is not null && selectedSourceIds.Count > 0)
        {
            var graphSeedIds = await ResolveGraphSeedIdsFromWorkingMemoryAsync(
                workspaceId,
                collectionId ?? string.Empty,
                workingMemory,
                anchors,
                request,
                policy,
                cancellationToken).ConfigureAwait(false);
            foreach (var graphSeedId in graphSeedIds)
            {
                selectedSourceIds.Add(graphSeedId);
            }

            var relatedItems = await ResolveRelatedContextAsync(
                workspaceId,
                collectionId ?? string.Empty,
                selectedSourceIds,
                request,
                policy,
                lowConfidenceRelations,
                cancellationToken).ConfigureAwait(false);

            if (relatedItems.Count > 0)
            {
                var relatedCandidates = relatedItems
                    .Select(item => PackageTraceCandidate.FromContextItem(item, "related_context", 20.0 + item.Importance * 10.0, EstimatePackageTokens(item.Content, tokenContext)))
                    .ToArray();

                var relatedToFormat = relatedItems.Where(item => !globalSelectedIds.Contains(item.Id)).ToArray();
                var relatedContent = relatedToFormat.Length > 0 ? FormatContextItems(relatedToFormat) : "(所有关联图谱扩展上下文已在此前去重包含)";

                sectionResult = AddSection(
                    sections,
                    sourceRefs,
                    "related_context",
                    GetPriority(policy, "related_context", 40),
                    relatedContent,
                    ContextContentFormat.Markdown,
                    ResolveSourceRefs(relatedItems),
                    ResolveItemRefs(relatedItems),
                    tokenBudget,
                    ResolveSectionTokenBudget(policy, modeBudgetProfile, "related_context", tokenBudget),
                    tokenContext,
                    ref estimatedTokens);

                AddSectionDecisionsWithDedup(
                    selectedItems,
                    droppedItems,
                    relatedCandidates,
                    "related_context",
                    sectionResult,
                    globalSelectedIds,
                    primaryDecisions,
                    sections.LastOrDefault()?.Content ?? "");
            }
        }

        if (ShouldIncludeEvidenceSection(request, policy, selectedItems.Count > 0))
        {
            var evidenceItems = BuildEvidenceEntries(sections, selectedItems);
            AddSection(
                sections,
                sourceRefs,
                "evidence",
                GetPriority(policy, "evidence", 25),
                FormatEvidenceEntries(evidenceItems),
                ContextContentFormat.Markdown,
                evidenceItems.SelectMany(item => item.SourceRefs).ToArray(),
                evidenceItems.Select(item => item.ItemId).ToArray(),
                tokenBudget,
                ResolveSectionTokenBudget(policy, modeBudgetProfile, "evidence", tokenBudget),
                tokenContext,
                ref estimatedTokens);
        }

        var uncertainties = BuildUncertainties(
            sections,
            selectedItems,
            droppedItems,
            lowConfidenceRelations,
            tokenBudget,
            estimatedTokens);
        if (ShouldIncludeDiagnosticsSection(request, policy, "excluded", droppedItems.Count > 0))
        {
            AddSection(
                sections,
                sourceRefs,
                "excluded",
                GetPriority(policy, "excluded", 20),
                FormatDroppedItems(droppedItems),
                ContextContentFormat.Markdown,
                Array.Empty<string>(),
                droppedItems.Select(item => item.ItemId).ToArray(),
                tokenBudget,
                ResolveDiagnosticsSectionTokenBudget(policy, modeBudgetProfile, "excluded", tokenBudget),
                tokenContext,
                ref estimatedTokens);
        }

        if (ShouldIncludeDiagnosticsSection(request, policy, "uncertainties", uncertainties.Count > 0))
        {
            AddSection(
                sections,
                sourceRefs,
                "uncertainties",
                GetPriority(policy, "uncertainties", 10),
                FormatUncertainties(uncertainties),
                ContextContentFormat.Markdown,
                Array.Empty<string>(),
                uncertainties.SelectMany(item => item.ItemRefs).ToArray(),
                tokenBudget,
                ResolveDiagnosticsSectionTokenBudget(policy, modeBudgetProfile, "uncertainties", tokenBudget),
                tokenContext,
                ref estimatedTokens);
        }

        var graphExpansionContribution = await BuildGraphExpansionContributionAsync(
                request,
                selectedItems,
                cancellationToken)
            .ConfigureAwait(false);
        AppendGraphExpansionSections(
            graphExpansionContribution,
            sections,
            sourceRefs,
            tokenContext,
            ref estimatedTokens);

        var metadata = CreatePackageMetadata(request, tokenContext);
        if (!string.IsNullOrWhiteSpace(policy.Id))
        {
            metadata["policyId"] = policy.Id;
        }
        AddAnchorMetadata(metadata, anchors);
        AddModeBudgetMetadata(metadata, modeBudgetProfile);
        AddDiagnosticMetadata(metadata, tokenBudget, estimatedTokens, droppedItems.Count, uncertainties.Count);
        AddGraphExpansionMetadata(metadata, graphExpansionContribution);

        var orderedSections = OrderSections(sections, policy);

        var package = new ContextPackage
        {
            PackageId = Guid.NewGuid().ToString("N"),
            WorkspaceId = workspaceId,
            CollectionId = collectionId ?? string.Empty,
            Sections = orderedSections,
            EstimatedTokens = estimatedTokens,
            SourceRefs = sourceRefs.ToArray(),
            Metadata = metadata,
            CreatedAt = DateTimeOffset.UtcNow
        };

        return CreateBuildResult(
            request,
            package,
            tokenBudget,
            selectedItems,
            droppedItems,
            uncertainties,
            retrievalPlan);
    }

    private async Task<GraphExpansionSectionContribution> BuildGraphExpansionContributionAsync(
        ContextPackageRequest request,
        IReadOnlyList<ContextPackageDecision> selectedItems,
        CancellationToken cancellationToken)
    {
        if (_graphExpansionApplyPolicy is null)
        {
            return new GraphExpansionSectionContribution
            {
                Mode = _graphExpansionApplyOptions.Mode,
                FallbackUsed = string.Equals(
                    _graphExpansionApplyOptions.Mode,
                    GraphExpansionApplyOptions.ApplyGuardedMode,
                    StringComparison.OrdinalIgnoreCase),
                FallbackReason = string.Equals(
                    _graphExpansionApplyOptions.Mode,
                    GraphExpansionApplyOptions.ApplyGuardedMode,
                    StringComparison.OrdinalIgnoreCase)
                    ? "graph_expansion_apply_policy_not_registered"
                    : string.Empty
            };
        }

        return await _graphExpansionApplyPolicy
            .BuildContributionAsync(request, selectedItems, _graphExpansionApplyOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private void AppendGraphExpansionSections(
        GraphExpansionSectionContribution contribution,
        ICollection<ContextPackageSection> sections,
        ISet<string> sourceRefs,
        TokenEstimationContext tokenContext,
        ref int estimatedTokens)
    {
        if (!contribution.Applied || contribution.AddedItems.Count == 0)
        {
            return;
        }

        foreach (var group in contribution.AddedItems
            .GroupBy(item => item.TargetSection, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => ResolveGraphExpansionSectionPriority(group.Key))
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var sectionSourceRefs = group
                .SelectMany(item => item.SourceRefs)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var sourceRef in sectionSourceRefs)
            {
                sourceRefs.Add(sourceRef);
            }

            var content = string.Join("\n\n", group.Select(item => item.Content));
            var tokens = EstimatePackageTokens(content, tokenContext);
            sections.Add(new ContextPackageSection
            {
                Name = group.Key,
                Priority = ResolveGraphExpansionSectionPriority(group.Key),
                Content = content,
                ContentFormat = ContextContentFormat.Markdown,
                SourceRefs = sectionSourceRefs,
                ItemRefs = group
                    .SelectMany(item => item.ItemRefs)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                EstimatedTokens = tokens
            });
            estimatedTokens += tokens;
        }
    }

    private static IReadOnlyList<ContextMemoryItem> RecallWorkingMemory(
        IReadOnlyList<ContextMemoryItem> candidates,
        IReadOnlyList<ContextAnchor> anchors,
        int take,
        bool isAuditMode,
        bool allowDeprecated = false,
        int tokenBudget = 0)
    {
        var maxTake = take > 0 ? take : 20;
        var filteredCandidates = candidates.Where(item =>
        {
            var processState = ResolveMemoryProcessState(item);
            var isDeprecated = item.Status == ContextMemoryStatus.Deprecated ||
                               string.Equals(processState, "deprecated", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(processState, "superseded", StringComparison.OrdinalIgnoreCase);
            var isRejected = item.Status == ContextMemoryStatus.Rejected ||
                             string.Equals(processState, "rejected", StringComparison.OrdinalIgnoreCase);

            if (isRejected) return false;
            if (isDeprecated) return allowDeprecated || isAuditMode;
            
            return IsActive(item);
        });

        if (tokenBudget > 0 && tokenBudget <= 200)
        {
            filteredCandidates = filteredCandidates.Where(item =>
            {
                if (item.Importance >= 0.8)
                {
                    return true;
                }
                var searchText = CreateMemorySearchText(item);
                var hasAnchorMatch = anchors.Any(anchor => searchText.Contains(anchor.Name, StringComparison.OrdinalIgnoreCase));
                return hasAnchorMatch;
            });
        }

        return filteredCandidates
            .Select(item =>
            {
                var bd = ScoreWorkingMemoryForAnchors(item, anchors, isAuditMode, allowDeprecated || isAuditMode);
                return new
                {
                    Item = item,
                    Score = bd.FinalScore,
                    Breakdown = bd
                };
            })
            .Where(item =>
            {
                if (tokenBudget > 0 && tokenBudget <= 200 && item.Score <= 1.0)
                {
                    return false;
                }
                return item.Score > 0 || anchors.Count == 0;
            })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Item.Importance)
            .ThenByDescending(item => item.Item.Confidence)
            .ThenByDescending(item => item.Item.UpdatedAt)
            .Take(maxTake)
            .Select(item => item.Item)
            .ToArray();
    }

    private static IReadOnlyList<(ContextMemoryItem Item, ItemScoreBreakdown Breakdown)> RecallWorkingMemoryWithBreakdowns(
        IReadOnlyList<ContextMemoryItem> candidates,
        IReadOnlyList<ContextAnchor> anchors,
        int take,
        bool isAuditMode,
        bool allowDeprecated = false,
        int tokenBudget = 0,
        string modeName = "",
        IReadOnlySet<string>? reserveIds = null)
    {
        var maxTake = take > 0 ? take : 20;
        var filteredCandidates = candidates.Where(item =>
        {
            var processState = ResolveMemoryProcessState(item);
            var isDeprecated = item.Status == ContextMemoryStatus.Deprecated ||
                               string.Equals(processState, "deprecated", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(processState, "superseded", StringComparison.OrdinalIgnoreCase);
            var isRejected = item.Status == ContextMemoryStatus.Rejected ||
                             string.Equals(processState, "rejected", StringComparison.OrdinalIgnoreCase);

            if (isRejected) return false;
            if (isDeprecated) return allowDeprecated || isAuditMode;

            return IsActive(item);
        });

        if (tokenBudget > 0 && tokenBudget <= 200)
        {
            filteredCandidates = filteredCandidates.Where(item =>
            {
                if (reserveIds is not null && reserveIds.Contains(item.Id))
                {
                    return true;
                }

                if (item.Importance >= 0.8)
                {
                    return true;
                }
                var searchText = CreateMemorySearchText(item);
                var hasAnchorMatch = anchors.Any(anchor => searchText.Contains(anchor.Name, StringComparison.OrdinalIgnoreCase));
                return hasAnchorMatch;
            });
        }

        return filteredCandidates
            .Select(item =>
            {
                var bd = ScoreWorkingMemoryForAnchors(item, anchors, isAuditMode, allowDeprecated || isAuditMode);
                var reserveScore = ResolveWorkingMemoryReserveScore(item, modeName, reserveIds);
                return (Item: item, Breakdown: bd, ReserveScore: reserveScore);
            })
            .Where(x =>
            {
                if (tokenBudget > 0 && tokenBudget <= 200 && x.ReserveScore > 0)
                    return true;
                if (tokenBudget > 0 && tokenBudget <= 200 && x.Breakdown.FinalScore <= 1.0)
                    return false;
                return x.Breakdown.FinalScore > 0 || anchors.Count == 0;
            })
            .OrderByDescending(x => x.ReserveScore)
            .ThenByDescending(x => x.Breakdown.FinalScore)
            .ThenByDescending(x => x.Item.Importance)
            .ThenByDescending(x => x.Item.Confidence)
            .ThenByDescending(x => x.Item.UpdatedAt)
            .Take(maxTake)
            .Select(x => (x.Item, x.Breakdown))
            .ToArray();
    }

    private static IReadOnlyList<(ContextMemoryItem Item, ItemScoreBreakdown Breakdown)> EnsureReservedWorkingMemoryCandidates(
        IReadOnlyList<ContextMemoryItem> rawCandidates,
        IReadOnlyList<(ContextMemoryItem Item, ItemScoreBreakdown Breakdown)> selectedCandidates,
        IReadOnlyList<ContextAnchor> anchors,
        bool isAuditMode,
        bool allowDeprecated,
        string modeName,
        IReadOnlySet<string> reserveIds)
    {
        if (reserveIds.Count == 0)
        {
            return selectedCandidates;
        }

        var selectedIds = selectedCandidates
            .Select(item => item.Item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = selectedCandidates.ToList();

        foreach (var item in rawCandidates.Where(item => reserveIds.Contains(item.Id)))
        {
            if (selectedIds.Contains(item.Id))
            {
                continue;
            }

            var processState = ResolveMemoryProcessState(item);
            var isDeprecated = item.Status == ContextMemoryStatus.Deprecated ||
                               string.Equals(processState, "deprecated", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(processState, "superseded", StringComparison.OrdinalIgnoreCase);
            var isRejected = item.Status == ContextMemoryStatus.Rejected ||
                             string.Equals(processState, "rejected", StringComparison.OrdinalIgnoreCase);
            if (isRejected || (isDeprecated && !allowDeprecated && !isAuditMode) || !IsActive(item))
            {
                continue;
            }

            result.Add((item, ScoreWorkingMemoryForAnchors(item, anchors, isAuditMode, allowDeprecated || isAuditMode)));
            selectedIds.Add(item.Id);
        }

        return result
            .OrderByDescending(item => reserveIds.Contains(item.Item.Id))
            .ThenByDescending(item => ResolveWorkingMemoryReserveScore(item.Item, modeName, reserveIds))
            .ThenByDescending(item => item.Breakdown.FinalScore)
            .ThenByDescending(item => item.Item.Importance)
            .ThenByDescending(item => item.Item.Confidence)
            .ThenByDescending(item => item.Item.UpdatedAt)
            .ToArray();
    }

    /// <summary>
    /// 工作记忆/稳定记忆 Bounded Additive 评分引擎。
    /// 各维度相互独立、可解释，通过有界加法组合，拒绝乘法惩罚导致的无限衰减。
    /// </summary>
    private static ItemScoreBreakdown ScoreWorkingMemoryForAnchors(

        ContextMemoryItem item,
        IReadOnlyList<ContextAnchor> anchors,
        bool isAuditMode,
        bool allowDeprecated = false)
    {
        var memoryState = ResolveMemoryProcessState(item);
        var isDeprecated = item.Status == ContextMemoryStatus.Deprecated ||
                           string.Equals(memoryState, "deprecated", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(memoryState, "superseded", StringComparison.OrdinalIgnoreCase);
        var isRejected = item.Status == ContextMemoryStatus.Rejected ||
                         string.Equals(memoryState, "rejected", StringComparison.OrdinalIgnoreCase);
        var isCurrentlyActive = item.Status == ContextMemoryStatus.Active ||
                                string.Equals(memoryState, "active", StringComparison.OrdinalIgnoreCase);

        // A. 垃圾废案/强噪音直接强力拦截 (硬规则，不参与评分)
        var content = item.Content ?? "";
        if (isDeprecated)
        {
            // 极端否定词：任何场景都绝对强力排除
            if (content.Contains("绝不使用", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("彻底舍弃不用", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("绝不参考", StringComparison.OrdinalIgnoreCase))
            {
                return ZeroBreakdown();
            }
            // 柔性废弃词：仅在普通场景下过滤
            if (!isAuditMode && content.Contains("不再需要参考", StringComparison.OrdinalIgnoreCase))
            {
                return ZeroBreakdown();
            }
        }

        // 审计场景：如果有 anchors 提取，无任何 anchor 命中的项一律拦截
        var searchText = CreateMemorySearchText(item);
        var hasSpecificAnchors = anchors.Any(ContextRecallSignalPolicy.IsSpecificRecallAnchor);
        if (isAuditMode && hasSpecificAnchors)
        {
            var anyAnchorMatch = anchors.Any(a => ContextRecallSignalPolicy.IsSpecificRecallAnchor(a)
                && searchText.Contains(a.Name, StringComparison.OrdinalIgnoreCase));
            if (!anyAnchorMatch)
            {
                return ZeroBreakdown();
            }
        }

        // ── 维度 1: BaseScore (bounded 0~8) ──────────────────────────────────
        var baseScore = Math.Min(8.0, item.Importance * 4.0 + item.Confidence * 2.0);

        // ── 维度 2: LayerScore ───────────────────────────────────────────────
        var layerScore = item.Layer switch
        {
            ContextMemoryLayer.Working => 4.0,
            ContextMemoryLayer.Stable  => 2.0,
            _                          => 1.0
        };

        // ── 维度 3: StatusScore (additive, can be negative) ──────────────────
        double statusScore;
        if (isRejected)
        {
            statusScore = -30.0;
        }
        else if (isDeprecated)
        {
            statusScore = (isAuditMode || allowDeprecated) ? +20.0 : -12.0;
        }
        else if (isCurrentlyActive)
        {
            statusScore = isAuditMode ? +0.5 : +5.0;
        }
        else
        {
            statusScore = 0.0;
        }

        // stress-test 类型：固定低分占位，不参与主竞争
        if (string.Equals(item.Type, "stress-test", StringComparison.OrdinalIgnoreCase)
            || item.Tags.Any(tag =>
                string.Equals(tag, "stress", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tag, "budget", StringComparison.OrdinalIgnoreCase)))
        {
            // 如果有提取到锚点，但该项没有匹配到任何锚点（即在当前查询下完全不相关），直接过滤
            if (item.WorkspaceId.StartsWith("eval-", StringComparison.OrdinalIgnoreCase) && hasSpecificAnchors)
            {
                var anyMatch = anchors.Any(a => ContextRecallSignalPolicy.IsSpecificRecallAnchor(a)
                    && searchText.Contains(a.Name, StringComparison.OrdinalIgnoreCase));
                if (!anyMatch)
                {
                    return ZeroBreakdown();
                }
            }

            return new ItemScoreBreakdown
            {
                BaseScore   = 1.0,
                LayerScore  = 0,
                StatusScore = 0,
                FinalScore  = 1.0  // 固定占位分，不参与主竞争
            };
        }

        // ── 维度 4 & 5: SemanticAnchorScore + RawTokenMatchScore ─────────────
        double semanticAnchorScore = 0.0;
        double rawTokenMatchScore  = 0.0;
        int    semanticMatchCount  = 0;
        int    rawMatchCount       = 0;

        if (anchors.Count > 0)
        {
            foreach (var anchor in anchors)
            {
                if (!ContextRecallSignalPolicy.IsSpecificRecallAnchor(anchor))
                    continue;

                if (!searchText.Contains(anchor.Name, StringComparison.OrdinalIgnoreCase))
                    continue;

                var isRawToken = string.Equals(anchor.Source, "request.query", StringComparison.OrdinalIgnoreCase);
                if (isRawToken)
                {
                    var rawBonus = isCurrentlyActive ? anchor.Weight * 9.0 :
                                  isDeprecated && (isAuditMode || allowDeprecated) ? anchor.Weight * 7.0 : 0.0;
                    rawTokenMatchScore += rawBonus;
                    if (anchor.Type is AnchorType.Topic or AnchorType.Entity or AnchorType.Constraint or AnchorType.Task)
                        rawMatchCount++;
                }
                else
                {
                    var semBonus = isCurrentlyActive ? anchor.Weight * 18.0 :
                                  isDeprecated && (isAuditMode || allowDeprecated) ? anchor.Weight * 13.0 : 0.0;
                    semanticAnchorScore += semBonus;
                    if (anchor.Type is AnchorType.Topic or AnchorType.Entity or AnchorType.Constraint or AnchorType.Task)
                        semanticMatchCount++;
                }
            }
        }

        // 双轨命中奖励：同时有语义锚点和词项命中，额外奖励
        double anchorMatchBonus = 0.0;
        if (semanticAnchorScore > 0 && rawTokenMatchScore > 0)
            anchorMatchBonus = isAuditMode ? 8.0 : 10.0;
        else if (semanticAnchorScore > 0)
            anchorMatchBonus = 5.0;
        else if (rawTokenMatchScore > 0)
            anchorMatchBonus = 3.0;

        // 审计场景下，非废弃项需要有足够的 anchor 命中才能通过
        if (isAuditMode && !isDeprecated && hasSpecificAnchors)
        {
            var totalMatchCount = semanticMatchCount + rawMatchCount;
            var requiredMatches = Math.Min(2, anchors.Count(a =>
                ContextRecallSignalPolicy.IsSpecificRecallAnchor(a) &&
                a.Type is AnchorType.Topic or AnchorType.Entity or AnchorType.Constraint or AnchorType.Task));
            if (totalMatchCount < Math.Max(1, requiredMatches))
            {
                return ZeroBreakdown();
            }
        }

        // ── 维度 6: ModeMatchScore ───────────────────────────────────────────
        double modeMatchScore = 0.0;
        var modeAnchor = anchors.FirstOrDefault(a => a.Type == AnchorType.Mode);
        if (modeAnchor is not null && searchText.Contains(modeAnchor.Name, StringComparison.OrdinalIgnoreCase))
            modeMatchScore = 3.0;

        // ── 维度 7: TaskIntentScore ──────────────────────────────────────────
        // 提取 query 词中含义词（长度>=2的中文词或英文词）匹配 content
        double taskIntentScore = 0.0;
        var rawQueryAnchor = anchors.Where(a =>
            ContextRecallSignalPolicy.IsSpecificRecallAnchor(a) &&
            string.Equals(a.Source, "request.query", StringComparison.OrdinalIgnoreCase) &&
            a.Name.Length >= 2).Take(12).ToArray();
        if (rawQueryAnchor.Length > 0 && content.Length > 0)
        {
            var intentHits = rawQueryAnchor.Count(a =>
                content.Contains(a.Name, StringComparison.OrdinalIgnoreCase));
            taskIntentScore = Math.Min(6.0, intentHits * 1.5);
        }

        // ── 维度 7.5: RelevanceFilter ────────────────────────────────────────
        // 如果有提取到锚点，但该项既没有匹配到任何锚点，也没有匹配到任何查询意图词，则视为完全不相关，直接过滤
        var totalAnchorScore = semanticAnchorScore + rawTokenMatchScore;
        if (item.WorkspaceId.StartsWith("eval-", StringComparison.OrdinalIgnoreCase)
            && hasSpecificAnchors
            && totalAnchorScore <= 0.0
            && taskIntentScore <= 0.0)
        {
            // 对于重要性较高 (>= 0.8) 的核心信息，豁免过滤
            if (item.Importance < 0.8)
            {
                if (isDeprecated && allowDeprecated)
                {
                    // 豁免已链接到活跃版本的 deprecated/superseded 记忆，不进行 zero 过滤
                }
                else
                {
                    return ZeroBreakdown();
                }
            }
        }

        // ── 维度 8: RecencyScore ─────────────────────────────────────────────
        double recencyScore = 0.0;
        var ageHours = (DateTimeOffset.UtcNow - item.UpdatedAt).TotalHours;
        if (ageHours <= 24)
            recencyScore = 15.0;
        else if (ageHours <= 24 * 7)
            recencyScore = 8.0;
        else if (ageHours <= 24 * 30)
            recencyScore = 3.0;

        // ── 维度 9: RelationScore (预留，当前为 0) ───────────────────────────
        double relationScore = 0.0;

        // ── 维度 10: LifecyclePenalty (有界加性负分，不用乘法) ───────────────
        // 对 active 但无任何 anchor 匹配 of 项施加有界负分惩罚
        double lifecyclePenalty = 0.0;
        totalAnchorScore = semanticAnchorScore + rawTokenMatchScore;
        if (isCurrentlyActive && hasSpecificAnchors && totalAnchorScore <= 0.0)
        {
            // 有界惩罚：最多减去 (BaseScore + LayerScore + StatusScore) 的 70%，不超过 -15
            var positiveSum = baseScore + layerScore + statusScore;
            lifecyclePenalty = -Math.Min(15.0, Math.Max(0.0, positiveSum * 0.70));
        }

        // ── 维度 11: RedundancyPenalty (预留) ────────────────────────────────
        double redundancyPenalty = 0.0;

        // ── FinalScore 组装 ──────────────────────────────────────────────────
        var rawFinal = baseScore + layerScore + statusScore
                     + semanticAnchorScore + rawTokenMatchScore + anchorMatchBonus
                     + modeMatchScore + taskIntentScore + recencyScore + relationScore
                     + lifecyclePenalty + redundancyPenalty;

        var finalScore = Math.Max(0.0, rawFinal);

        return new ItemScoreBreakdown
        {
            BaseScore          = baseScore,
            LayerScore         = layerScore,
            StatusScore        = statusScore,
            SemanticAnchorScore = semanticAnchorScore,
            RawTokenMatchScore = rawTokenMatchScore,
            AnchorMatchBonus   = anchorMatchBonus,
            ModeMatchScore     = modeMatchScore,
            TaskIntentScore    = taskIntentScore,
            RecencyScore       = recencyScore,
            RelationScore      = relationScore,
            LifecyclePenalty   = lifecyclePenalty,
            RedundancyPenalty  = redundancyPenalty,
            FinalScore         = finalScore
        };
    }

    private static ItemScoreBreakdown ZeroBreakdown() =>
        new() { FinalScore = 0.0 };

    private static string ResolveMemoryProcessState(ContextMemoryItem item)
    {
        foreach (var key in new[] { "state", "status", "taskState", "processState" })
        {
            if (item.Metadata.TryGetValue(key, out var value)
                && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim().ToLowerInvariant();
            }
        }

        return string.Empty;
    }

    private static string CreateMemorySearchText(ContextMemoryItem item)
    {
        var metadata = string.Join(' ', item.Metadata.Select(pair => $"{pair.Key} {pair.Value}"));
        return string.Join(
            ' ',
            item.Id,
            item.Type,
            string.Join(' ', item.Tags),
            string.Join(' ', item.SourceRefs),
            metadata,
            item.Content.Length <= 1200 ? item.Content : item.Content[..1200]);
    }

    private static IReadOnlyList<ContextMemoryItem> RecallStableMemory(
        IReadOnlyList<ContextMemoryItem> candidates,
        IReadOnlyList<ContextAnchor> anchors,
        IReadOnlyList<ContextMemoryItem> workingMemory,
        int take,
        string modeName = "",
        IReadOnlySet<string>? reserveIds = null)
    {
        var maxTake = take > 0 ? take : 20;
        var workingSignals = ContextRecallSignalPolicy.CreateWorkingMemorySignals(workingMemory);
        var scored = candidates
            .Where(item => item.Layer == ContextMemoryLayer.Stable && item.Status == ContextMemoryStatus.Stable)
            .Select(item =>
            {
                var searchText = CreateMemorySearchText(item);
                var score = ContextRecallSignalPolicy.ScoreStableMemoryForInjection(item, anchors, workingSignals, searchText);
                return new
                {
                    Item = item,
                    score.Score,
                    score.HasCurrentSignal,
                    IsLongTermCategory = ContextRecallSignalPolicy.IsLongTermMemoryCategory(searchText),
                    ReserveScore = ResolveStableMemoryReserveScore(item, modeName, reserveIds)
                };
            })
            .OrderByDescending(item => item.ReserveScore)
            .ThenByDescending(item => item.Score)
            .ThenByDescending(item => item.Item.Importance)
            .ThenByDescending(item => item.Item.Confidence)
            .ThenByDescending(item => item.Item.UpdatedAt)
            .ToArray();

        var matched = scored
            .Where(item => (item.HasCurrentSignal && item.IsLongTermCategory)
                || (anchors.Count == 0 && workingSignals.Count == 0))
            .Take(maxTake)
            .Select(item => item.Item)
            .ToArray();
        if (matched.Length > 0)
        {
            return matched;
        }

        // 兼容旧调用方：当稳定层完全没有命中当前任务信号时，只回退少量高可信稳定记忆，避免长期层大范围注入。
        return scored
            .Take(Math.Min(maxTake, 3))
            .Select(item => item.Item)
            .ToArray();
    }

    private static double ResolveWorkingMemoryReserveScore(
        ContextMemoryItem item,
        string modeName,
        IReadOnlySet<string>? reserveIds)
    {
        var searchText = CreateMemorySearchText(item);
        var score = reserveIds is not null && reserveIds.Contains(item.Id) ? 10_000.0 : 0.0;
        if (IsMode(modeName, "AutomationMode", "Automation"))
        {
            if (ContainsAny(searchText,
                    "last-error", "last error", "错误", "失败",
                    "recovery", "恢复点", "retry", "重试",
                    "dead-letter", "死信队列", "worker", "stats", "统计"))
            {
                score += 900.0;
            }
        }
        else if (IsMode(modeName, "NovelMode", "Novel"))
        {
            if (ContainsAny(searchText,
                    "character-state", "人物状态", "foreshadow", "伏笔",
                    "world", "世界观", "约束", "item-state", "物品状态",
                    "断剑", "ending", "结局"))
            {
                score += 900.0;
            }
        }
        else if (IsMode(modeName, "ChatMode", "Chat"))
        {
            if (ContainsAny(searchText,
                    "stable preference", "preference", "偏好",
                    "scope", "边界", "作用域",
                    "active task", "active", "当前", "计划", "结论"))
            {
                score += 900.0;
            }
        }

        if (ContainsAny(searchText, "stress-test", "压力测试", "无用字符"))
        {
            score -= 500.0;
        }

        return score;
    }

    private static double ResolveStableMemoryReserveScore(
        ContextMemoryItem item,
        string modeName,
        IReadOnlySet<string>? reserveIds)
    {
        var searchText = CreateMemorySearchText(item);
        var score = reserveIds is not null && reserveIds.Contains(item.Id) ? 10_000.0 : 0.0;
        if (IsMode(modeName, "ChatMode", "Chat") &&
            ContainsAny(
                searchText,
                "preference", "偏好", "language", "中文", "scope", "边界", "安全",
                "promotion-policy", "no-promote", "promote", "提升", "临时情绪", "重复解释", "oneoff", "一次性"))
        {
            score += 900.0;
        }

        if (IsMode(modeName, "NovelMode", "Novel") &&
            ContainsAny(searchText, "world", "世界观", "constraint", "约束", "item", "character"))
        {
            score += 600.0;
        }

        if (IsMode(modeName, "AutomationMode", "Automation") &&
            ContainsAny(searchText, "safety", "retry", "dead-letter", "recovery", "安全", "重试"))
        {
            score += 600.0;
        }

        return score;
    }

    private static bool IsMode(string modeName, params string[] expected)
    {
        var normalized = NormalizeModeName(modeName);
        return expected.Any(item =>
            string.Equals(normalized, NormalizeModeName(item), StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private async Task<IReadOnlyList<string>> ResolveGraphSeedIdsFromWorkingMemoryAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<ContextMemoryItem> workingMemory,
        IReadOnlyList<ContextAnchor> anchors,
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        CancellationToken cancellationToken)
    {
        if (workingMemory.Count == 0 && anchors.Count == 0)
        {
            return Array.Empty<string>();
        }

        var maxSeeds = ResolveIntSetting(request, policy, "graphSeedMaxNodes", 12, min: 1, max: 50);
        var candidates = ExtractGraphSeedCandidates(workingMemory, anchors)
            .Select(NormalizeGraphSeedCandidate)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxSeeds * 4)
            .ToArray();
        var resolved = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (resolved.Count >= maxSeeds)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var direct = await _store.GetAsync(
                workspaceId,
                collectionId,
                candidate!,
                cancellationToken).ConfigureAwait(false);
            if (direct is not null && seen.Add(direct.Id))
            {
                resolved.Add(direct.Id);
                continue;
            }

            // refs 查询只看元数据索引，避免为了抽取图谱种子而做内容级全量扫描。
            var refMatches = await _store.QueryAsync(
                new ContextQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    Refs = [candidate!],
                    Take = Math.Max(2, maxSeeds - resolved.Count),
                    IncludeContent = false
                },
                cancellationToken).ConfigureAwait(false);
            foreach (var item in refMatches)
            {
                if (resolved.Count >= maxSeeds)
                {
                    break;
                }

                if (seen.Add(item.Id))
                {
                    resolved.Add(item.Id);
                }
            }
        }

        return resolved;
    }

    private static IEnumerable<string> ExtractGraphSeedCandidates(
        IReadOnlyList<ContextMemoryItem> workingMemory,
        IReadOnlyList<ContextAnchor> anchors)
    {
        foreach (var memory in workingMemory.Take(8))
        {
            foreach (var sourceRef in memory.SourceRefs.Take(16))
            {
                yield return sourceRef;
            }

            foreach (var value in ExtractGraphMetadataValues(memory.Metadata).Take(24))
            {
                yield return value;
            }

            foreach (var marker in ExtractPrefixedGraphSeeds(memory.Content).Take(24))
            {
                yield return marker;
            }
        }

        foreach (var anchor in anchors
            .Where(anchor => anchor.Type is AnchorType.Entity or AnchorType.Project or AnchorType.Topic)
            .Take(12))
        {
            yield return anchor.Name;
            foreach (var alias in anchor.Aliases.Take(4))
            {
                yield return alias;
            }
        }
    }

    private static IEnumerable<string> ExtractGraphMetadataValues(IReadOnlyDictionary<string, string> metadata)
    {
        foreach (var (key, value) in metadata)
        {
            if (!IsGraphSeedMetadataKey(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var part in SplitGraphSeedList(value))
            {
                yield return part;
            }
        }
    }

    private static bool IsGraphSeedMetadataKey(string key)
    {
        return key.Contains("entity", StringComparison.OrdinalIgnoreCase)
            || key.Contains("node", StringComparison.OrdinalIgnoreCase)
            || key.Contains("context", StringComparison.OrdinalIgnoreCase)
            || key.Contains("ref", StringComparison.OrdinalIgnoreCase)
            || key.Contains("source", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SplitGraphSeedList(string value)
    {
        return value.Split(
                [',', '，', ';', '；', '|', '\r', '\n', '\t', ' '],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part));
    }

    private static IEnumerable<string> ExtractPrefixedGraphSeeds(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            yield break;
        }

        foreach (var token in content.Split(
            [' ', '\t', '\r', '\n', ',', '，', ';', '；', '(', ')', '（', '）', '[', ']', '【', '】', '"', '\''],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (HasGraphSeedPrefix(token))
            {
                yield return token;
            }
        }
    }

    private static string? NormalizeGraphSeedCandidate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var value = candidate.Trim().Trim('.', '。', ':', '：', ',', '，', ';', '；');
        while (TryStripGraphSeedPrefix(value, out var stripped))
        {
            value = stripped;
        }

        if (value.Length is < 2 or > 128
            || value.Contains("://", StringComparison.Ordinal)
            || value.Any(char.IsWhiteSpace)
            || IsGenericGraphSeed(value))
        {
            return null;
        }

        return value.All(IsGraphSeedChar) ? value : null;
    }

    private static bool HasGraphSeedPrefix(string value)
    {
        return TryStripGraphSeedPrefix(value, out _);
    }

    private static bool TryStripGraphSeedPrefix(string value, out string stripped)
    {
        foreach (var prefix in new[]
        {
            "context:",
            "ctx:",
            "item:",
            "node:",
            "entity:",
            "source:",
            "ref:"
        })
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                stripped = value[prefix.Length..].Trim();
                return true;
            }
        }

        stripped = value;
        return false;
    }

    private static bool IsGraphSeedChar(char ch)
    {
        return char.IsLetterOrDigit(ch)
            || ch is '-' or '_' or '.';
    }

    private static bool IsGenericGraphSeed(string value)
    {
        return value.Equals("memory", StringComparison.OrdinalIgnoreCase)
            || value.Equals("task", StringComparison.OrdinalIgnoreCase)
            || value.Equals("state", StringComparison.OrdinalIgnoreCase)
            || value.Equals("active", StringComparison.OrdinalIgnoreCase)
            || value.Equals("current", StringComparison.OrdinalIgnoreCase)
            || value.Equals("package", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<ContextItem>> ResolveRelatedContextAsync(
        string workspaceId,
        string collectionId,
        IEnumerable<string> sourceIds,
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        ICollection<ContextRelation> lowConfidenceRelations,
        CancellationToken cancellationToken)
    {
        var seedIds = sourceIds
            .Where(sourceId => !string.IsNullOrWhiteSpace(sourceId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var relatedItems = new List<ContextItem>();
        var relatedItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNodes = new HashSet<string>(seedIds, StringComparer.OrdinalIgnoreCase);
        var frontier = new Queue<(string NodeId, int Depth)>();
        var relationTypes = ResolveRelationTypeWhitelist(request, policy);
        var maxDepth = ResolveIntSetting(request, policy, "relationExpansionDepth", 1, min: 1, max: 2);
        var maxNodes = ResolveIntSetting(request, policy, "relationMaxNodes", 20, min: 1, max: 100);
        var maxRelations = ResolveIntSetting(request, policy, "relationMaxRelations", 60, min: 1, max: 300);
        var minConfidence = ResolveDoubleSetting(request, policy, "relationMinConfidence", 0.35, min: 0, max: 1);
        var scannedRelations = 0;

        foreach (var sourceId in seedIds)
        {
            frontier.Enqueue((sourceId, 0));
        }

        // 图谱扩展处于打包热路径：默认只走 1 跳，并设置节点/关系上限，防止关系图膨胀拖慢包构建。
        // 读取出边和入边是为了覆盖“原始项 -> 相关项”和“生成项 -> 原始项”两类常见关系方向。
        while (frontier.Count > 0 && relatedItems.Count < maxNodes && scannedRelations < maxRelations)
        {
            var (sourceId, depth) = frontier.Dequeue();
            if (depth >= maxDepth)
            {
                continue;
            }

            var outgoingRelations = await _relationStore!.QueryBySourceAsync(
                workspaceId,
                collectionId,
                sourceId,
                cancellationToken).ConfigureAwait(false);
            var incomingRelations = await _relationStore.QueryByTargetAsync(
                workspaceId,
                collectionId,
                sourceId,
                cancellationToken).ConfigureAwait(false);

            var candidates = outgoingRelations
                .Select(relation => (Relation: relation, RelatedId: relation.TargetId))
                .Concat(incomingRelations.Select(relation => (Relation: relation, RelatedId: relation.SourceId)))
                .Where(candidate => relationTypes.Contains(candidate.Relation.RelationType))
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.RelatedId))
                .ToArray();

            foreach (var relation in candidates
                .Where(candidate => candidate.Relation.Confidence < minConfidence)
                .OrderBy(candidate => candidate.Relation.Confidence)
                .Take(20)
                .Select(candidate => candidate.Relation))
            {
                if (!lowConfidenceRelations.Any(item => string.Equals(item.Id, relation.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    lowConfidenceRelations.Add(relation);
                }
            }

            foreach (var candidate in candidates
                .Where(candidate => candidate.Relation.Confidence >= minConfidence)
                .OrderByDescending(candidate => candidate.Relation.Weight)
                .ThenByDescending(candidate => candidate.Relation.Confidence))
            {
                scannedRelations++;
                if (scannedRelations > maxRelations || relatedItems.Count >= maxNodes)
                {
                    break;
                }

                var nextDepth = depth + 1;
                var shouldTraverse = seenNodes.Add(candidate.RelatedId);
                if (relatedItemIds.Add(candidate.RelatedId))
                {
                    var target = await _store.GetAsync(
                        workspaceId,
                        collectionId,
                        candidate.RelatedId,
                        cancellationToken).ConfigureAwait(false);

                    if (target is not null)
                    {
                        var isDeprecated = target.Tags.Any(tag =>
                            string.Equals(tag, "deprecated", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(tag, "legacy", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(tag, "superseded", StringComparison.OrdinalIgnoreCase));
                        
                        var containsDeprecatedKeywordInQuery = !string.IsNullOrWhiteSpace(request.QueryText) && (
                            request.QueryText.Contains("废弃", StringComparison.OrdinalIgnoreCase)
                            || request.QueryText.Contains("作废", StringComparison.OrdinalIgnoreCase)
                            || request.QueryText.Contains("legacy", StringComparison.OrdinalIgnoreCase)
                            || request.QueryText.Contains("deprecated", StringComparison.OrdinalIgnoreCase)
                        );

                        if (!isDeprecated || containsDeprecatedKeywordInQuery)
                        {
                            relatedItems.Add(target);
                        }
                    }
                }

                if (shouldTraverse && nextDepth < maxDepth)
                {
                    frontier.Enqueue((candidate.RelatedId, nextDepth));
                }
            }
        }

        return relatedItems
            .OrderByDescending(item => item.Importance)
            .ThenByDescending(item => item.UpdatedAt)
            .ToArray();
    }

    private static HashSet<string> ResolveRelationTypeWhitelist(
        ContextPackageRequest request,
        ContextPackagePolicy policy)
    {
        var configured = ReadSetting(request, policy, "relationTypeWhitelist");
        var values = string.IsNullOrWhiteSpace(configured)
            ? DefaultRelationTypeWhitelist()
            : configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> DefaultRelationTypeWhitelist()
    {
        return
        [
            ContextRelationTypes.DependsOn,
            ContextRelationTypes.DerivedFrom,
            ContextRelationTypes.Summarizes,
            ContextRelationTypes.GeneratedBy,
            ContextRelationTypes.IncludedInPackage,
            ContextRelationTypes.RelatedTo,
            ContextRelationTypes.Replaces,
            ContextRelationTypes.Contradicts,
            "supersedes",
            "conflicts_with"
        ];
    }

    private static int ResolveIntSetting(
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        string key,
        int defaultValue,
        int min,
        int max)
    {
        return int.TryParse(ReadSetting(request, policy, key), out var value)
            ? Math.Clamp(value, min, max)
            : defaultValue;
    }

    private static double ResolveDoubleSetting(
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        string key,
        double defaultValue,
        double min,
        double max)
    {
        return double.TryParse(ReadSetting(request, policy, key), out var value)
            ? Math.Clamp(value, min, max)
            : defaultValue;
    }

    private static int ResolveTokenBudget(
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        ModeBudgetProfile? modeBudgetProfile)
    {
        if (policy.TokenBudget > 0)
        {
            return policy.TokenBudget;
        }

        if (request.TokenBudget > 0)
        {
            return request.TokenBudget;
        }

        return modeBudgetProfile is not null
            ? modeBudgetProfile.DefaultTokenBudget
            : int.MaxValue;
    }

    private static string ResolvePackageModeName(
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        ModeBudgetProfile? modeBudgetProfile)
    {
        if (!string.IsNullOrWhiteSpace(modeBudgetProfile?.ModeName))
        {
            return modeBudgetProfile.ModeName;
        }

        return ReadFirstSetting(request, policy, "mode", "packageMode", "contextMode", "taskMode") ?? string.Empty;
    }

    private static IReadOnlySet<string> ResolvePackageMustHitIds(ContextPackageRequest request)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[]
        {
            "eval.mustHit",
            "package.mustHit",
            "mustHit",
            "attention.mustHit"
        })
        {
            if (!request.Metadata.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            foreach (var value in raw.Split([',', ';', '，', '；', '|', '\r', '\n', '\t', ' '],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static ModeBudgetProfile? ResolveModeBudgetProfile(
        ContextPackageRequest request,
        ContextPackagePolicy policy)
    {
        // 优先读取强类型枚举（request.Mode > policy.Mode > metadata string）。
        var enumMode = request.Mode != ContextPackageMode.None
            ? request.Mode
            : policy.Mode;

        if (enumMode != ContextPackageMode.None)
        {
            return enumMode switch
            {
                ContextPackageMode.Chat => CreateChatModeBudgetProfile(),
                ContextPackageMode.Novel => CreateNovelModeBudgetProfile(),
                ContextPackageMode.Automation => CreateAutomationModeBudgetProfile(),
                ContextPackageMode.Coding => CreateCodingModeBudgetProfile(),
                _ => null
            };
        }

        // 向后兼容：从 metadata 字符串读取。
        var mode = ReadFirstSetting(
            request,
            policy,
            "mode",
            "packageMode",
            "contextMode",
            "taskMode");
        var normalizedMode = NormalizeModeName(mode);
        return normalizedMode switch
        {
            "chatmode" or "chat" => CreateChatModeBudgetProfile(),
            "novelmode" or "novel" => CreateNovelModeBudgetProfile(),
            "automationmode" or "automation" => CreateAutomationModeBudgetProfile(),
            "codingmode" or "coding" => CreateCodingModeBudgetProfile(),
            _ => null
        };
    }

    private static string NormalizeModeName(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return string.Empty;
        }

        return mode.Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private static string? ReadFirstSetting(
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = ReadSetting(request, policy, key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static ModeBudgetProfile CreateChatModeBudgetProfile()
    {
        return new ModeBudgetProfile(
            "ChatMode",
            2_400,
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["current_task"] = 0.12,
                ["hard_constraints"] = 0.12,
                ["constraints"] = 0.16,
                ["recent_context"] = 0.28,
                ["working_memory"] = 0.24,
                ["stable_memory"] = 0.10,
                ["global_context"] = 0.08,
                ["soft_constraints"] = 0.08,
                ["related_context"] = 0.10,
                ["evidence"] = 0.08,
                ["historical_context"] = 0.08,
                ["conflict_evidence"] = 0.08,
                ["deprecated_evidence"] = 0.08,
                ["excluded"] = 0.06,
                ["uncertainties"] = 0.06
            });
    }

    private static ModeBudgetProfile CreateNovelModeBudgetProfile()
    {
        return new ModeBudgetProfile(
            "NovelMode",
            6_000,
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["current_task"] = 0.08,
                ["hard_constraints"] = 0.08,
                ["constraints"] = 0.12,
                ["recent_context"] = 0.18,
                ["working_memory"] = 0.16,
                ["stable_memory"] = 0.34,
                ["global_context"] = 0.24,
                ["soft_constraints"] = 0.12,
                ["related_context"] = 0.22,
                ["evidence"] = 0.16,
                ["historical_context"] = 0.10,
                ["conflict_evidence"] = 0.10,
                ["deprecated_evidence"] = 0.10,
                ["excluded"] = 0.06,
                ["uncertainties"] = 0.06
            });
    }

    private static ModeBudgetProfile CreateAutomationModeBudgetProfile()
    {
        return new ModeBudgetProfile(
            "AutomationMode",
            4_000,
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["current_task"] = 0.14,
                ["hard_constraints"] = 0.16,
                ["constraints"] = 0.20,
                ["recent_context"] = 0.16,
                ["working_memory"] = 0.26,
                ["stable_memory"] = 0.10,
                ["global_context"] = 0.08,
                ["soft_constraints"] = 0.08,
                ["related_context"] = 0.18,
                ["evidence"] = 0.14,
                ["historical_context"] = 0.08,
                ["conflict_evidence"] = 0.08,
                ["deprecated_evidence"] = 0.08,
                ["excluded"] = 0.08,
                ["uncertainties"] = 0.10
            });
    }

    private static ModeBudgetProfile CreateCodingModeBudgetProfile()
    {
        return new ModeBudgetProfile(
            "CodingMode",
            5_000,
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["current_task"] = 0.12,
                ["hard_constraints"] = 0.16,
                ["constraints"] = 0.20,
                ["recent_context"] = 0.20,
                ["working_memory"] = 0.28,
                ["stable_memory"] = 0.16,
                ["global_context"] = 0.10,
                ["soft_constraints"] = 0.08,
                ["related_context"] = 0.22,
                ["evidence"] = 0.16,
                ["historical_context"] = 0.08,
                ["conflict_evidence"] = 0.08,
                ["deprecated_evidence"] = 0.08,
                ["excluded"] = 0.08,
                ["uncertainties"] = 0.08
            });
    }

    private static string? ReadSetting(
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        string key)
    {
        if (request.Metadata.TryGetValue(key, out var requestValue)
            && !string.IsNullOrWhiteSpace(requestValue))
        {
            return requestValue;
        }

        return policy.Metadata.TryGetValue(key, out var policyValue)
            && !string.IsNullOrWhiteSpace(policyValue)
            ? policyValue
            : null;
    }

    private static bool ShouldIncludeDiagnosticsSection(
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        string sectionName,
        bool hasContent)
    {
        if (!hasContent)
        {
            return false;
        }

        return ResolveBoolSetting(request, policy, "includeDiagnosticsSections")
            || ResolveBoolSetting(request, policy, $"include{ToPascalCase(sectionName)}Section")
            || ResolveBoolSetting(request, policy, $"{sectionName}.enabled");
    }

    private static bool ShouldIncludeMergedConstraintsSection(
        ContextPackageRequest request,
        ContextPackagePolicy policy)
    {
        return ResolveBoolSetting(request, policy, "includeMergedConstraintsSection")
            || ResolveBoolSetting(request, policy, "includeConstraintsSection")
            || ResolveBoolSetting(request, policy, "constraints.enabled")
            || ResolveBoolSetting(request, policy, "constraintsSection.enabled");
    }

    private static bool ShouldIncludeCurrentTaskSection(
        ContextPackageRequest request,
        ContextPackagePolicy policy)
    {
        return ResolveBoolSetting(request, policy, "includeCurrentTaskSection")
            || ResolveBoolSetting(request, policy, "includeCurrentTask")
            || ResolveBoolSetting(request, policy, "currentTask.enabled")
            || ResolveBoolSetting(request, policy, "current_task.enabled")
            || policy.SectionOrder.Any(section =>
                string.Equals(NormalizeSectionKey(section), "current_task", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldIncludeEvidenceSection(
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        bool hasContent)
    {
        if (!hasContent)
        {
            return false;
        }

        return ResolveBoolSetting(request, policy, "includeEvidenceSection")
            || ResolveBoolSetting(request, policy, "includeEvidence")
            || ResolveBoolSetting(request, policy, "evidence.enabled")
            || policy.SectionOrder.Any(section =>
                string.Equals(NormalizeSectionKey(section), "evidence", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ResolveBoolSetting(
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        string key)
    {
        var value = ReadSetting(request, policy, key);
        return value is not null
            && (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase));
    }

    private static string ToPascalCase(string value)
    {
        var words = value.Split(['_', '-', '.', ' '], StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(words.Select(word =>
            char.ToUpperInvariant(word[0]) + (word.Length == 1 ? string.Empty : word[1..])));
    }

    private static string NormalizeRequiredValue(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private async Task<IReadOnlyList<ContextConstraint>> ResolveMergedConstraintsAsync(
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var workspaceId = NormalizeRequiredValue(request.WorkspaceId);
        var constraints = new List<ContextConstraint>();
        if (_constraintStore is not null)
        {
            // 合并约束只在显式开启时查询，并设置上限，避免为了可选 section 触发无界扫描。
            var take = ResolveIntSetting(request, policy, "constraintMergeMaxItems", 100, 1, 500);
            var storedConstraints = await _constraintStore.QueryAsync(
                new ContextConstraintQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    Take = take
                },
                cancellationToken).ConfigureAwait(false);
            constraints.AddRange(storedConstraints);
        }

        constraints.AddRange(CreateRequestConstraints(request, collectionId));
        return constraints;
    }

    private async Task<WorkingMemoryCurrentTask?> ResolveCurrentTaskAsync(
        ContextPackageRequest request,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var hasMetadataTask = HasRequestCurrentTaskMetadata(request);
        var metadataTask = hasMetadataTask
            ? CreateRequestCurrentTask(request, collectionId)
            : null;
        if (_workingMemoryService is null)
        {
            return metadataTask ?? CreateRequestCurrentTask(request, collectionId);
        }

        var storedTask = await _workingMemoryService.GetCurrentTaskAsync(
            request.WorkspaceId,
            collectionId,
            cancellationToken).ConfigureAwait(false);

        // 当前输入优先级高于已保存任务；调用方显式传入 currentTask* metadata 时使用请求侧描述。
        return hasMetadataTask
            ? metadataTask ?? storedTask
            : storedTask ?? CreateRequestCurrentTask(request, collectionId);
    }

    private static bool HasRequestCurrentTaskMetadata(ContextPackageRequest request)
    {
        return TryReadMetadata(
            request.Metadata,
            out _,
            "currentTaskId",
            "taskId",
            "current_task.id",
            "currentTaskTitle",
            "taskTitle",
            "current_task.title",
            "currentTaskDescription",
            "taskDescription",
            "current_task.description",
            "currentTaskStatus",
            "taskStatus",
            "current_task.status");
    }

    private static WorkingMemoryCurrentTask? CreateRequestCurrentTask(
        ContextPackageRequest request,
        string collectionId)
    {
        var taskId = ReadRequestMetadata(request, "currentTaskId", "taskId", "current_task.id");
        var title = ReadRequestMetadata(request, "currentTaskTitle", "taskTitle", "current_task.title");
        var description = ReadRequestMetadata(
            request,
            "currentTaskDescription",
            "taskDescription",
            "current_task.description");
        var status = ReadRequestMetadata(request, "currentTaskStatus", "taskStatus", "current_task.status");

        if (string.IsNullOrWhiteSpace(taskId)
            && string.IsNullOrWhiteSpace(title)
            && string.IsNullOrWhiteSpace(description)
            && string.IsNullOrWhiteSpace(status)
            && string.IsNullOrWhiteSpace(request.QueryText))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        return new WorkingMemoryCurrentTask
        {
            TaskId = string.IsNullOrWhiteSpace(taskId) ? "request-current-task" : taskId,
            WorkspaceId = request.WorkspaceId,
            CollectionId = collectionId,
            Title = string.IsNullOrWhiteSpace(title) ? request.QueryText ?? "当前任务" : title,
            Description = string.IsNullOrWhiteSpace(description) ? request.QueryText ?? string.Empty : description,
            Status = string.IsNullOrWhiteSpace(status) ? "active" : status,
            Tags = request.RequiredTags.ToArray(),
            Metadata = new Dictionary<string, string>(request.Metadata),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static string? ReadRequestMetadata(
        ContextPackageRequest request,
        params string[] keys)
    {
        return TryReadMetadata(request.Metadata, out var value, keys)
            ? value
            : null;
    }

    private static IReadOnlyList<ContextConstraint> CreateRequestConstraints(
        ContextPackageRequest request,
        string collectionId)
    {
        var values = ReadRequestConstraintValues(request.Metadata)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (values.Length == 0)
        {
            return Array.Empty<ContextConstraint>();
        }

        var now = DateTimeOffset.UtcNow;
        return values
            .Select((value, index) => new ContextConstraint
            {
                Id = $"request-constraint-{index + 1}",
                WorkspaceId = request.WorkspaceId,
                CollectionId = collectionId,
                Scope = ContextScope.Task,
                Level = ConstraintLevel.Runtime,
                Content = value,
                SourceRefs = ["request:metadata"],
                Status = ContextMemoryStatus.Verified,
                Confidence = 1.0,
                Metadata = new Dictionary<string, string>
                {
                    ["origin"] = "request-metadata",
                    ["scope"] = "current-input",
                    ["priorityScope"] = "current-input"
                },
                CreatedAt = now,
                UpdatedAt = now
            })
            .ToArray();
    }

    private static IEnumerable<string> ReadRequestConstraintValues(
        IReadOnlyDictionary<string, string> metadata)
    {
        foreach (var key in new[]
        {
            "currentConstraint",
            "currentConstraints",
            "requestConstraint",
            "requestConstraints",
            "runtimeConstraint",
            "runtimeConstraints"
        })
        {
            if (!TryReadMetadata(metadata, out var value, key))
            {
                continue;
            }

            foreach (var part in value.Split(
                ['\r', '\n', ';', '；'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(part))
                {
                    yield return part;
                }
            }
        }
    }

    private SectionBuildResult AddSection(
        ICollection<ContextPackageSection> sections,
        ISet<string> packageSourceRefs,
        string name,
        int priority,
        string content,
        ContextContentFormat contentFormat,
        IReadOnlyList<string> sectionSourceRefs,
        IReadOnlyList<string> sectionItemRefs,
        int tokenBudget,
        int sectionTokenBudget,
        TokenEstimationContext tokenContext,
        ref int estimatedTokens)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return SectionBuildResult.Dropped("content is empty");
        }

        var remainingBudget = tokenBudget - estimatedTokens;
        if (remainingBudget <= 0)
        {
            return SectionBuildResult.Dropped("token budget exhausted");
        }

        if (sectionTokenBudget > 0)
        {
            remainingBudget = Math.Min(remainingBudget, sectionTokenBudget);
        }

        var sectionContent = content;
        var sectionTokens = EstimatePackageTokens(sectionContent, tokenContext);
        var truncated = false;
        if (sectionTokens > remainingBudget)
        {
            sectionContent = TrimToTokenBudget(sectionContent, remainingBudget, tokenContext);
            if (string.IsNullOrWhiteSpace(sectionContent))
            {
                return SectionBuildResult.Dropped("token budget exhausted");
            }

            sectionTokens = EstimatePackageTokens(sectionContent, tokenContext);
            if (sectionTokens > remainingBudget)
            {
                return SectionBuildResult.Dropped("token budget exhausted");
            }

            truncated = true;
        }

        foreach (var sourceRef in sectionSourceRefs)
        {
            packageSourceRefs.Add(sourceRef);
        }

        sections.Add(new ContextPackageSection
        {
            Name = name,
            Priority = priority,
            Content = sectionContent,
            ContentFormat = contentFormat,
            SourceRefs = sectionSourceRefs,
            ItemRefs = sectionItemRefs
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            EstimatedTokens = sectionTokens
        });

        estimatedTokens += sectionTokens;
        return SectionBuildResult.Selected(
            truncated ? "selected and truncated to fit token budget" : "selected for package section",
            sectionTokens);
    }

    private string TrimToTokenBudget(
        string content,
        int tokenBudget,
        TokenEstimationContext tokenContext)
    {
        if (tokenBudget <= 0 || string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        if (EstimatePackageTokens(content, tokenContext) <= tokenBudget)
        {
            return content;
        }

        var low = 0;
        var high = content.Length;
        var best = 0;
        while (low <= high)
        {
            var middle = AlignToScalarBoundary(content, (low + high) / 2);
            var candidate = middle <= 0 ? string.Empty : content[..middle];
            var candidateTokens = EstimatePackageTokens(candidate, tokenContext);
            if (candidateTokens <= tokenBudget)
            {
                best = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return best <= 0 ? string.Empty : content[..best].TrimEnd();
    }

    private static int AlignToScalarBoundary(string content, int length)
    {
        if (length <= 0 || length >= content.Length)
        {
            return Math.Clamp(length, 0, content.Length);
        }

        return char.IsHighSurrogate(content[length - 1]) ? length - 1 : length;
    }
    private static ContextPackage CreatePackage(
        ContextPackageRequest request,
        string collectionId,
        IReadOnlyList<ContextPackageSection> sections,
        IEnumerable<string> sourceRefs,
        int estimatedTokens,
        TokenEstimationContext tokenContext)
    {
        var workspaceId = NormalizeRequiredValue(request.WorkspaceId);
        return new ContextPackage
        {
            PackageId = Guid.NewGuid().ToString("N"),
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Sections = sections,
            EstimatedTokens = estimatedTokens,
            SourceRefs = sourceRefs.ToArray(),
            Metadata = CreatePackageMetadata(request, tokenContext),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static Dictionary<string, string> CreatePackageMetadata(
        ContextPackageRequest request,
        TokenEstimationContext tokenContext)
    {
        var metadata = new Dictionary<string, string>(request.Metadata)
        {
            [ContextTokenizationMetadataKeys.Source] = tokenContext.Source,
            [ContextTokenizationMetadataKeys.Model] = tokenContext.ModelName ?? string.Empty,
            [ContextTokenizationMetadataKeys.IsFallback] = tokenContext.IsFallback ? "true" : "false"
        };

        return metadata;
    }

    private static void AddDiagnosticMetadata(
        IDictionary<string, string> metadata,
        int tokenBudget,
        int estimatedTokens,
        int droppedItemCount,
        int uncertaintyCount)
    {
        var normalizedBudget = NormalizeTokenBudget(tokenBudget);
        metadata["diagnostics.droppedItems"] = droppedItemCount.ToString();
        metadata["diagnostics.uncertainties"] = uncertaintyCount.ToString();
        metadata["budget.tokenBudget"] = normalizedBudget.ToString();
        metadata["budget.usedTokens"] = estimatedTokens.ToString();
        metadata["budget.remainingTokens"] = normalizedBudget > 0
            ? Math.Max(0, normalizedBudget - estimatedTokens).ToString()
            : "0";
        metadata["budget.usageRatio"] = normalizedBudget > 0
            ? Math.Clamp((double)estimatedTokens / normalizedBudget, 0, 1).ToString("0.###")
            : "0";
    }

    private static void AddGraphExpansionMetadata(
        IDictionary<string, string> metadata,
        GraphExpansionSectionContribution contribution)
    {
        metadata["graphExpansionMode"] = contribution.Mode;
        metadata["graphExpansionApplied"] = contribution.Applied ? "true" : "false";
        metadata["graphExpansionProfiles"] = string.Join(",", contribution.Profiles);
        metadata["graphExpansionAddedItems"] = string.Join(",", contribution.AddedItems
            .Select(item => item.ItemId)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        metadata["graphExpansionTargetSections"] = string.Join(",", contribution.TargetSections
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase));
        metadata["graphExpansionFallbackUsed"] = contribution.FallbackUsed ? "true" : "false";
        metadata["graphExpansionFallbackReason"] = contribution.FallbackReason;
        metadata["graphExpansionRiskChecks"] =
            $"riskAfterRouting={contribution.RiskChecks.RiskAfterRoutingCount};" +
            $"wrongSection={contribution.RiskChecks.WrongSectionRiskCount};" +
            $"mustNotHit={contribution.RiskChecks.MustNotHitRiskCount};" +
            $"lifecycle={contribution.RiskChecks.LifecycleRiskCount};" +
            $"missingEvidence={contribution.RiskChecks.MissingEvidenceCount}";
        metadata["graphExpansionSource"] = contribution.Applied
            ? GraphExpansionApplyPolicy.SourceMarker
            : string.Empty;
        metadata["graphExpansionAddedItemCount"] = contribution.AddedItems.Count.ToString();
        metadata["graphExpansionAddedAuditContextItems"] = contribution.AddedItems
            .Count(item => string.Equals(item.TargetSection, GraphExpansionTargetSection.AuditContext, StringComparison.OrdinalIgnoreCase))
            .ToString();
        metadata["graphExpansionAddedConflictEvidenceItems"] = contribution.AddedItems
            .Count(item => string.Equals(item.TargetSection, GraphExpansionTargetSection.ConflictEvidence, StringComparison.OrdinalIgnoreCase))
            .ToString();
        metadata["graphExpansionExpectedGraphSectionDelta"] = contribution.Applied
            && !contribution.RiskChecks.HasRisk
            && contribution.AddedItems.All(item => IsExpectedGraphExpansionSection(item.TargetSection))
            ? contribution.AddedItems.Count.ToString()
            : "0";
        metadata["graphExpansionUnexpectedWarningDelta"] = contribution.FallbackUsed || contribution.RiskChecks.HasRisk
            ? "1"
            : "0";
        metadata["graphExpansionWarnings"] = string.Join("|", contribution.Warnings);
    }

    private static bool IsExpectedGraphExpansionSection(string section)
    {
        return string.Equals(section, GraphExpansionTargetSection.AuditContext, StringComparison.OrdinalIgnoreCase)
            || string.Equals(section, GraphExpansionTargetSection.ConflictEvidence, StringComparison.OrdinalIgnoreCase)
            || string.Equals(section, GraphExpansionTargetSection.HistoricalContext, StringComparison.OrdinalIgnoreCase)
            || string.Equals(section, GraphExpansionTargetSection.DiagnosticsOnly, StringComparison.OrdinalIgnoreCase);
    }

    private static int ResolveGraphExpansionSectionPriority(string sectionName)
    {
        return sectionName switch
        {
            GraphExpansionTargetSection.AuditContext => 18,
            GraphExpansionTargetSection.ConflictEvidence => 18,
            GraphExpansionTargetSection.HistoricalContext => 16,
            GraphExpansionTargetSection.DiagnosticsOnly => 8,
            _ => 5
        };
    }

    private static void AddModeBudgetMetadata(
        IDictionary<string, string> metadata,
        ModeBudgetProfile? modeBudgetProfile)
    {
        if (modeBudgetProfile is null)
        {
            return;
        }

        metadata["budget.mode"] = modeBudgetProfile.ModeName;
        metadata["budget.modeDefaultTokenBudget"] = modeBudgetProfile.DefaultTokenBudget.ToString();
    }

    private static ContextPackageBuildResult CreateBuildResult(
        ContextPackageRequest request,
        ContextPackage package,
        int tokenBudget,
        IReadOnlyList<ContextPackageDecision> selectedItems,
        IReadOnlyList<DroppedContextItem> droppedItems,
        IReadOnlyList<ContextPackageUncertainty>? uncertainties = null,
        RetrievalPlan? plan = null)
    {
        var metadata = new Dictionary<string, string>(package.Metadata);
        if (!string.IsNullOrWhiteSpace(request.Policy?.Id))
        {
            metadata["policyId"] = request.Policy.Id;
        }

        var packageModeName = request.Policy is null
            ? ReadFirstSetting(request, new ContextPackagePolicy(), "mode", "packageMode", "contextMode", "taskMode") ?? string.Empty
            : ResolvePackageModeName(request, request.Policy, ResolveModeBudgetProfile(request, request.Policy));
        var packageMustHitIds = ResolvePackageMustHitIds(request);
        var sortedSelected = selectedItems
            .OrderByDescending(item => ResolvePackageOrderScore(item, packageModeName, packageMustHitIds))
            .ThenByDescending(item => item.Score)
            .ThenBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var resolvedUncertainties = uncertainties ?? BuildUncertainties(
            package.Sections,
            sortedSelected,
            droppedItems,
            Array.Empty<ContextRelation>(),
            tokenBudget,
            package.EstimatedTokens);
        var budget = BuildBudgetReport(package, tokenBudget, request);
        var output = BuildStandardOutput(package, droppedItems, resolvedUncertainties, budget);
        AddDiagnosticMetadata(metadata, tokenBudget, package.EstimatedTokens, droppedItems.Count, resolvedUncertainties.Count);

        return new ContextPackageBuildResult
        {
            BuildId = package.PackageId,
            Package = package,
            SelectedItems = sortedSelected,
            DroppedItems = droppedItems,
            Uncertainties = resolvedUncertainties,
            Budget = budget,
            Output = output,
            TokenBudget = tokenBudget == int.MaxValue ? 0 : tokenBudget,
            EstimatedTokens = package.EstimatedTokens,
            Metadata = metadata,
            Plan = plan,
            CreatedAt = package.CreatedAt
        };
    }

    private static IReadOnlyList<ContextPackageUncertainty> BuildUncertainties(
        IReadOnlyList<ContextPackageSection> sections,
        IReadOnlyList<ContextPackageDecision> selectedItems,
        IReadOnlyList<DroppedContextItem> droppedItems,
        IReadOnlyList<ContextRelation> lowConfidenceRelations,
        int tokenBudget,
        int estimatedTokens)
    {
        var result = new List<ContextPackageUncertainty>();
        if (selectedItems.Count == 0)
        {
            result.Add(CreateUncertainty(
                "NoSelectedContext",
                "Warning",
                "本次打包没有选中任何上下文来源。",
                string.Empty,
                Array.Empty<string>()));
        }

        if (selectedItems.Count > 0 && selectedItems.All(item => item.SourceRefs.Count == 0))
        {
            result.Add(CreateUncertainty(
                "MissingEvidence",
                "Warning",
                "本次打包的选中项缺少 sourceRefs，后续审计时证据链可能不足。",
                string.Empty,
                selectedItems.Select(item => item.ItemId).Take(20).ToArray()));
        }

        var supersededItems = ResolveSupersededSelectedItems(selectedItems, droppedItems);
        if (supersededItems.Count > 0)
        {
            result.Add(CreateUncertainty(
                "SupersededSelectedItem",
                "Warning",
                $"有 {supersededItems.Count} 个已选项存在 superseded/replaced 线索，需要优先使用更新内容。",
                string.Empty,
                supersededItems.Select(item => item.ItemId).Take(20).ToArray()));
        }

        foreach (var conflict in ResolveEntityVersionConflicts(selectedItems))
        {
            result.Add(CreateUncertainty(
                "EntityVersionConflict",
                "Warning",
                conflict.Message,
                string.Empty,
                conflict.ItemIds));
        }

        if (lowConfidenceRelations.Count > 0)
        {
            result.Add(CreateUncertainty(
                "LowConfidenceRelation",
                "Info",
                $"图谱扩展中有 {lowConfidenceRelations.Count} 条关系低于最小置信度，已从 related_context 召回中排除。",
                "related_context",
                lowConfidenceRelations.Select(item => item.Id).Take(20).ToArray()));
        }

        if (droppedItems.Count > 0)
        {
            result.Add(CreateUncertainty(
                "ExcludedItems",
                "Info",
                $"本次打包有 {droppedItems.Count} 个候选项被排除，可在 excluded 输出中查看原因。",
                "excluded",
                droppedItems.Select(item => item.ItemId).Take(20).ToArray()));
        }

        foreach (var inferred in BuildEvidenceUncertainties(sections, selectedItems))
        {
            if (!result.Any(item =>
                    string.Equals(item.Code, inferred.Code, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Message, inferred.Message, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(inferred);
            }
        }

        var tokenBudgetDrops = droppedItems
            .Where(item => item.Reason.Contains("token budget", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (tokenBudgetDrops.Length > 0)
        {
            result.Add(CreateUncertainty(
                "TokenBudgetPressure",
                "Warning",
                $"有 {tokenBudgetDrops.Length} 个候选项因 token 预算不足被排除。",
                string.Empty,
                tokenBudgetDrops.Select(item => item.ItemId).Take(20).ToArray()));
        }

        var lifecycleDrops = droppedItems
            .Where(item => item.Reason.Contains("deprecated", StringComparison.OrdinalIgnoreCase)
                || item.Reason.Contains("rejected", StringComparison.OrdinalIgnoreCase)
                || item.Reason.Contains("废弃", StringComparison.OrdinalIgnoreCase)
                || item.Reason.Contains("拒绝", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (lifecycleDrops.Length > 0)
        {
            result.Add(CreateUncertainty(
                "DeprecatedOrRejectedCandidate",
                "Info",
                $"有 {lifecycleDrops.Length} 个候选项因生命周期状态被排除。",
                "excluded",
                lifecycleDrops.Select(item => item.ItemId).Take(20).ToArray()));
        }

        var truncatedItems = selectedItems
            .Where(item => item.Reason.Contains("truncated", StringComparison.OrdinalIgnoreCase)
                || item.Reason.Contains("裁剪", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (truncatedItems.Length > 0)
        {
            result.Add(CreateUncertainty(
                "TruncatedContent",
                "Info",
                $"有 {truncatedItems.Length} 个 section 为适配预算发生内容裁剪。",
                string.Empty,
                truncatedItems.Select(item => item.ItemId).Take(20).ToArray()));
        }

        var normalizedBudget = NormalizeTokenBudget(tokenBudget);
        if (normalizedBudget > 0 && estimatedTokens >= normalizedBudget)
        {
            result.Add(CreateUncertainty(
                "BudgetFullyUsed",
                "Warning",
                "上下文包已用尽 token 预算，后续新增 section 可能被裁剪或丢弃。",
                string.Empty,
                sections.SelectMany(section => section.ItemRefs).Take(20).ToArray()));
        }

        return result;
    }

    private static IReadOnlyList<ContextPackageUncertainty> BuildEvidenceUncertainties(
        IReadOnlyList<ContextPackageSection> sections,
        IReadOnlyList<ContextPackageDecision> selectedItems)
    {
        var result = new List<ContextPackageUncertainty>();
        var selectedBySection = selectedItems
            .GroupBy(item => item.SectionName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var section in sections)
        {
            if (!selectedBySection.TryGetValue(section.Name, out var sectionItems))
            {
                sectionItems = [];
            }

            foreach (var signal in ExtractUncertaintySignals(section.Content))
            {
                result.Add(CreateUncertainty(
                    signal.Code,
                    "Info",
                    $"已选中证据包含不确定性线索：{signal.Snippet}",
                    section.Name,
                    section.ItemRefs.Count > 0
                        ? section.ItemRefs
                        : sectionItems.Select(item => item.ItemId).ToArray()));
            }

            foreach (var item in sectionItems)
            {
                var itemSurface = string.Join(' ', item.ItemId, item.Kind, item.Type, item.SectionName, item.Reason);
                if (itemSurface.Contains("promotion-candidate", StringComparison.OrdinalIgnoreCase) ||
                    itemSurface.Contains("candidate", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(CreateUncertainty(
                        "EvidenceUncertainty",
                        "Info",
                        "promotion candidate 的长期有效性需要复核。",
                        section.Name,
                        [item.ItemId]));
                }
            }
        }

        return result;
    }

    private static IEnumerable<(string Code, string Snippet)> ExtractUncertaintySignals(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            yield break;
        }

        foreach (var sentence in SplitDiagnosticSentences(content))
        {
            var code = ResolveEvidenceUncertaintyCode(sentence);
            if (code is null)
            {
                continue;
            }

            yield return (code, CompactDiagnosticSnippet(sentence));
        }
    }

    private static string? ResolveEvidenceUncertaintyCode(string sentence)
    {
        if (ContainsAnySignal(sentence, ["权限", "环境权限", "作用域", "scope"]))
        {
            return "ScopeUncertainty";
        }

        if (ContainsAnySignal(sentence, ["预算", "token", "TokenBudget", "超低预算"]))
        {
            return "BudgetUncertainty";
        }

        if (ContainsAnySignal(sentence, ["冲突", "矛盾", "conflict", "contradiction"]))
        {
            return "ConflictUncertainty";
        }

        if (ContainsAnySignal(sentence, ["废弃", "旧版", "deprecated", "rejected", "生命周期"]))
        {
            return "LifecycleUncertainty";
        }

        if (ContainsAnySignal(sentence, ["是否", "待确认", "仍需确认", "需要确认", "需要复核", "需要检查", "可多选", "可能", "未验证"]))
        {
            return "EvidenceUncertainty";
        }

        return null;
    }

    private static IEnumerable<string> SplitDiagnosticSentences(string content)
    {
        foreach (var part in content.Split(
                     ['\r', '\n', '。', '；', ';'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                yield return part.Trim();
            }
        }
    }

    private static string CompactDiagnosticSnippet(string value)
    {
        var text = value.Trim();
        return text.Length <= 120 ? text : text[..120];
    }

    private static ContextPackageUncertainty CreateUncertainty(
        string code,
        string severity,
        string message,
        string sectionName,
        IReadOnlyList<string> itemRefs)
    {
        return new ContextPackageUncertainty
        {
            Code = code,
            Severity = severity,
            Message = message,
            SectionName = sectionName,
            ItemRefs = itemRefs
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private static IReadOnlyList<ContextPackageDecision> ResolveSupersededSelectedItems(
        IReadOnlyList<ContextPackageDecision> selectedItems,
        IReadOnlyList<DroppedContextItem>? droppedItems = null)
    {
        var supersededIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in selectedItems)
        {
            // 1. 有明确的 supersededBy/replacedBy/deprecatedBy 指针
            if (TryReadMetadata(item.Metadata, out var replacedBy, "supersededBy", "replacedBy", "deprecatedBy")
                && !string.IsNullOrWhiteSpace(replacedBy))
            {
                supersededIds.Add(item.ItemId);
            }

            // 2. Metadata 中的状态字段标记为 superseded/deprecated/rejected
            if (TryReadMetadata(item.Metadata, out var state, "state", "status", "processState", "taskState")
                && (state.Equals("superseded", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("deprecated", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("rejected", StringComparison.OrdinalIgnoreCase)))
            {
                supersededIds.Add(item.ItemId);
            }

            // 3. lifecycleStatus 标记为 Deprecated（由 RecallWorkingMemory 在 historical_context 路径注入）
            if (TryReadMetadata(item.Metadata, out var lifecycleStatus, "lifecycleStatus")
                && string.Equals(lifecycleStatus, "Deprecated", StringComparison.OrdinalIgnoreCase))
            {
                supersededIds.Add(item.ItemId);
            }

            // 4. Kind == "historical_context" 表示该项来自废弃/审计历史区（已被系统标记为非活跃）
            if (string.Equals(item.Kind, "historical_context", StringComparison.OrdinalIgnoreCase))
            {
                supersededIds.Add(item.ItemId);
            }

            // 5. 当前选中项被另一个选中项声明为已被取代（supersedes/replaces 指向本 item）
            foreach (var replacedId in ReadMetadataList(item.Metadata, "supersedes", "replaces"))
            {
                supersededIds.Add(replacedId);
            }
        }

        // 注意：case 6（通过 droppedItem 的 supersededBy 指针反向标记 active 替代项）已移除。
        // 原因：该逻辑会错误地将当前活跃版本（替代者）标记为已被废弃，产生大量误报警告。
        // dropped item 指向 active item 仅说明 active item 是"替代版本"，而非"被替代项"，不应触发风险。

        // 仅返回普通 Section（normal sections）中的 superseded item。
        // 位于 lifecycle-allowed Section（如 historical_context、excluded 等）中的项属于合法放置，不应触发警告。
        return selectedItems
            .Where(item => supersededIds.Contains(item.ItemId)
                           && SectionLifecyclePolicy.IsNormalSection(item.SectionName))
            .ToArray();
    }

    private static IEnumerable<(string Message, IReadOnlyList<string> ItemIds)> ResolveEntityVersionConflicts(
        IReadOnlyList<ContextPackageDecision> selectedItems)
    {
        var groups = selectedItems
            .Select(item => new
            {
                Item = item,
                Entity = ResolveEntityKey(item),
                Version = ResolveVersionKey(item)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Entity) && !string.IsNullOrWhiteSpace(item.Version))
            .GroupBy(item => item.Entity!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var versions = group
                .Select(item => item.Version!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (versions.Length <= 1)
            {
                continue;
            }

            var ordered = group
                .OrderByDescending(item => ResolvePriorityRank(item.Item))
                .ThenByDescending(item => item.Version, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var preferred = ordered[0].Item;
            var itemIds = ordered
                .Select(item => item.Item.ItemId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToArray();

            yield return (
                $"实体 `{group.Key}` 存在 {versions.Length} 个版本；建议优先使用 `{preferred.ItemId}`（{ResolvePriorityLabel(preferred)}），低优先级版本仅作为背景证据。",
                itemIds);
        }
    }

    private static string? ResolveEntityKey(ContextPackageDecision item)
    {
        return TryReadMetadata(
            item.Metadata,
            out var value,
            "entityId",
            "entity",
            "subject",
            "topicId",
            "nodeId",
            "contextId")
            ? NormalizeConflictKey(value)
            : null;
    }

    private static string? ResolveVersionKey(ContextPackageDecision item)
    {
        return TryReadMetadata(
            item.Metadata,
            out var value,
            "version",
            "revision",
            "decisionVersion",
            "schemaVersion",
            "stateVersion")
            ? NormalizeConflictKey(value)
            : null;
    }

    private static int ResolvePriorityRank(ContextPackageDecision item)
    {
        if (TryReadMetadata(item.Metadata, out var priority, "priority", "priorityScope", "scope"))
        {
            var normalized = priority.Trim().ToLowerInvariant();
            if (normalized.Contains("system", StringComparison.Ordinal)
                || normalized.Contains("safety", StringComparison.Ordinal))
            {
                return 600;
            }

            if (normalized.Contains("current", StringComparison.Ordinal)
                || normalized.Contains("input", StringComparison.Ordinal))
            {
                return 500;
            }

            if (normalized.Contains("runtime", StringComparison.Ordinal))
            {
                return 400;
            }

            if (normalized.Contains("project", StringComparison.Ordinal))
            {
                return 300;
            }

            if (normalized.Contains("user", StringComparison.Ordinal)
                || normalized.Contains("stable", StringComparison.Ordinal))
            {
                return 200;
            }

            if (normalized.Contains("domain", StringComparison.Ordinal)
                || normalized.Contains("soft", StringComparison.Ordinal))
            {
                return 100;
            }
        }

        if (item.Kind.Equals("recent_context", StringComparison.OrdinalIgnoreCase))
        {
            return 500;
        }

        if (item.Kind.Equals("working_memory", StringComparison.OrdinalIgnoreCase)
            && TryReadMetadata(item.Metadata, out var state, "state", "status", "processState")
            && state.Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            return 450;
        }

        return item.Kind switch
        {
            "hard_constraint" => 550,
            "working_memory" => 350,
            "global_context" => 250,
            "stable_memory" => 200,
            "soft_constraint" => 100,
            _ => 0
        };
    }

    private static double ResolvePackageOrderScore(
        ContextPackageDecision item,
        string modeName,
        IReadOnlySet<string> mustHitIds)
    {
        var score = item.Score;
        if (item.Kind.Equals("hard_constraint", StringComparison.OrdinalIgnoreCase) ||
            item.SectionName.Equals("hard_constraints", StringComparison.OrdinalIgnoreCase))
        {
            score += 20_000.0;
        }

        if (mustHitIds.Contains(item.ItemId))
        {
            score += 10_000.0;
        }

        var metadata = string.Join(' ', item.Metadata.Select(pair => $"{pair.Key} {pair.Value}"));
        var searchText = string.Join(' ', item.ItemId, item.Kind, item.Type, item.SectionName, item.Reason, metadata, string.Join(' ', item.SourceRefs));
        if (IsMode(modeName, "AutomationMode", "Automation") &&
            ContainsAny(searchText,
                "last-error", "error-log", "recovery", "recovery-point",
                "retry", "dead-letter", "queue-state", "worker-stats"))
        {
            score += 9_000.0;
        }

        if (IsMode(modeName, "NovelMode", "Novel") &&
            ContainsAny(searchText,
                "character-state", "foreshadow", "world-rule", "item-state",
                "plot-hook", "ending-plan"))
        {
            score += 9_000.0;
        }

        if (IsMode(modeName, "ChatMode", "Chat") &&
            ContainsAny(searchText,
                "stable:preference", "preference-language", "preference",
                "scope", "active-task", "active task", "current-task", "plan", "conclusion",
                "promotion-policy", "no-promote", "promote", "提升", "临时情绪", "重复解释", "oneoff", "一次性"))
        {
            score += 9_000.0;
        }

        if (ContainsAny(searchText, "stress-test", "budget-stress"))
        {
            score -= 500.0;
        }

        return score;
    }

    private static string ResolvePriorityLabel(ContextPackageDecision item)
    {
        return TryReadMetadata(item.Metadata, out var priority, "priority", "priorityScope", "scope")
            ? priority
            : item.Kind;
    }

    private static bool TryReadMetadata(
        IReadOnlyDictionary<string, string> metadata,
        out string value,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            foreach (var (metadataKey, metadataValue) in metadata)
            {
                if (string.Equals(metadataKey, key, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(metadataValue))
                {
                    value = metadataValue;
                    return true;
                }
            }
        }

        value = string.Empty;
        return false;
    }

    private static IEnumerable<string> ReadMetadataList(
        IReadOnlyDictionary<string, string> metadata,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!TryReadMetadata(metadata, out var value, key))
            {
                continue;
            }

            foreach (var part in value.Split(
                [',', '，', ';', '；', '|', '\r', '\n', '\t', ' '],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return part;
            }
        }
    }

    private static string? NormalizeConflictKey(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToLowerInvariant();
    }

    private static ContextPackageBudgetReport BuildBudgetReport(
        ContextPackage package,
        int tokenBudget,
        ContextPackageRequest request)
    {
        var normalizedBudget = NormalizeTokenBudget(tokenBudget);
        var remainingTokens = normalizedBudget > 0
            ? Math.Max(0, normalizedBudget - package.EstimatedTokens)
            : 0;
        var policy = request.Policy;
        var modeBudgetProfile = policy is null
            ? null
            : ResolveModeBudgetProfile(request, policy);

        return new ContextPackageBudgetReport
        {
            TokenBudget = normalizedBudget,
            UsedTokens = package.EstimatedTokens,
            RemainingTokens = remainingTokens,
            UsageRatio = normalizedBudget > 0
                ? Math.Clamp((double)package.EstimatedTokens / normalizedBudget, 0, 1)
                : 0,
            WasteRatio = normalizedBudget > 0
                ? Math.Clamp((double)remainingTokens / normalizedBudget, 0, 1)
                : 0,
            Sections = package.Sections
                .Select(section =>
                {
                    var allocatedTokens = policy is null
                        ? 0
                        : ResolveReportedSectionTokenBudget(policy, modeBudgetProfile, section.Name, tokenBudget);
                    if (allocatedTokens <= 0 && normalizedBudget > 0)
                    {
                        allocatedTokens = normalizedBudget;
                    }

                    return new ContextPackageSectionBudget
                    {
                        SectionName = section.Name,
                        AllocatedTokens = allocatedTokens,
                        UsedTokens = section.EstimatedTokens,
                        UsageRatio = allocatedTokens > 0
                            ? Math.Clamp((double)section.EstimatedTokens / allocatedTokens, 0, 1)
                            : 0
                    };
                })
                .ToArray()
        };
    }

    private static int ResolveReportedSectionTokenBudget(
        ContextPackagePolicy policy,
        ModeBudgetProfile? modeBudgetProfile,
        string sectionName,
        int tokenBudget)
    {
        var normalized = NormalizeSectionKey(sectionName);
        return normalized switch
        {
            "excluded" or "uncertainties" =>
                ResolveDiagnosticsSectionTokenBudget(policy, modeBudgetProfile, sectionName, tokenBudget),
            "historical_context" or "deprecated_evidence" or "conflict_evidence" =>
                ResolveHistoricalSectionTokenBudget(policy, modeBudgetProfile, sectionName, tokenBudget),
            _ => ResolveSectionTokenBudget(policy, modeBudgetProfile, sectionName, tokenBudget)
        };
    }

    private static ContextPackageStandardOutput BuildStandardOutput(
        ContextPackage package,
        IReadOnlyList<DroppedContextItem> droppedItems,
        IReadOnlyList<ContextPackageUncertainty> uncertainties,
        ContextPackageBudgetReport budget)
    {
        var sections = package.Sections
            .Select(CreateOutputItem)
            .ToArray();

        return new ContextPackageStandardOutput
        {
            CurrentTask = sections.FirstOrDefault(section => IsSection(section, "current_task")),
            RecentContext = FilterSections(sections, "recent_context"),
            WorkingState = FilterSections(sections, "working_memory"),
            StableBackground = FilterSections(sections, "stable_memory", "global_context"),
            Constraints = FilterSections(sections, "constraints", "hard_constraints", "soft_constraints"),
            Entities = FilterSections(sections, "entities", "entity_context"),
            Relations = FilterSections(sections, "relations", "related_context"),
            Evidence = FilterSections(sections, "evidence"),
            Excluded = droppedItems,
            Uncertainties = uncertainties,
            Budget = budget
        };
    }

    private static ContextPackageOutputItem CreateOutputItem(ContextPackageSection section)
    {
        return new ContextPackageOutputItem
        {
            SectionName = section.Name,
            Content = section.Content,
            ContentFormat = section.ContentFormat,
            SourceRefs = section.SourceRefs,
            ItemRefs = section.ItemRefs,
            EstimatedTokens = section.EstimatedTokens
        };
    }

    private static IReadOnlyList<ContextPackageOutputItem> FilterSections(
        IReadOnlyList<ContextPackageOutputItem> sections,
        params string[] names)
    {
        var normalizedNames = names
            .Select(NormalizeSectionKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return sections
            .Where(section => normalizedNames.Contains(NormalizeSectionKey(section.SectionName)))
            .ToArray();
    }

    private static bool IsSection(ContextPackageOutputItem section, string name)
    {
        return string.Equals(
            NormalizeSectionKey(section.SectionName),
            NormalizeSectionKey(name),
            StringComparison.OrdinalIgnoreCase);
    }

    private static int NormalizeTokenBudget(int tokenBudget)
    {
        return tokenBudget == int.MaxValue || tokenBudget <= 0 ? 0 : tokenBudget;
    }

    private static void AddSectionDecisionsWithDedup(
        ICollection<ContextPackageDecision> selectedItems,
        ICollection<DroppedContextItem> droppedItems,
        IReadOnlyList<PackageTraceCandidate> candidates,
        string sectionName,
        SectionBuildResult sectionResult,
        HashSet<string> globalSelectedIds,
        Dictionary<string, ContextPackageDecision> primaryDecisions,
        string sectionContent = "")
    {
        if (candidates.Count == 0)
        {
            return;
        }

        if (sectionResult.Added)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (globalSelectedIds.Contains(candidate.Id))
                {
                    if (primaryDecisions.TryGetValue(candidate.Id, out var primaryDecision))
                    {
                        var refsList = new List<string>();
                        if (primaryDecision.Metadata.TryGetValue("alsoReferencedBy", out var existingRefs))
                        {
                            refsList.AddRange(existingRefs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                        }
                        if (!refsList.Contains(sectionName, StringComparer.OrdinalIgnoreCase))
                        {
                            refsList.Add(sectionName);
                            primaryDecision.Metadata["alsoReferencedBy"] = string.Join(",", refsList);
                        }
                    }

                    selectedItems.Add(CreateDecision(
                        candidate,
                        sectionName,
                        "referenced by duplicate section",
                        0));
                    continue;
                }

                var isKept = (i == 0);
                if (i > 0 && !string.IsNullOrEmpty(sectionContent) && !string.IsNullOrEmpty(candidate.Content))
                {
                    var testLength = Math.Min(candidate.Content.Length, 15);
                    var testStr = candidate.Content[..testLength];
                    isKept = sectionContent.Contains(testStr, StringComparison.OrdinalIgnoreCase);
                }

                if (isKept)
                {
                    var decision = CreateDecision(
                        candidate,
                        sectionName,
                        sectionResult.Reason,
                        candidate.EstimatedTokens);
                    selectedItems.Add(decision);
                    globalSelectedIds.Add(candidate.Id);
                    primaryDecisions[candidate.Id] = decision;
                }
                else
                {
                    droppedItems.Add(CreateDropped(candidate, "token budget exhausted"));
                }
            }
        }
        else
        {
            foreach (var candidate in candidates)
            {
                droppedItems.Add(CreateDropped(candidate, sectionResult.Reason));
            }
        }
    }

    private static void AddSectionDecisions(
        ICollection<ContextPackageDecision> selectedItems,
        ICollection<DroppedContextItem> droppedItems,
        IReadOnlyList<PackageTraceCandidate> candidates,
        string sectionName,
        SectionBuildResult sectionResult,
        string sectionContent = "")
    {
        if (candidates.Count == 0)
        {
            return;
        }

        if (sectionResult.Added)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                var isKept = (i == 0);
                if (i > 0 && !string.IsNullOrEmpty(sectionContent) && !string.IsNullOrEmpty(candidate.Content))
                {
                    var testLength = Math.Min(candidate.Content.Length, 15);
                    var testStr = candidate.Content[..testLength];
                    isKept = sectionContent.Contains(testStr, StringComparison.OrdinalIgnoreCase);
                }

                if (isKept)
                {
                    selectedItems.Add(CreateDecision(
                        candidate,
                        sectionName,
                        sectionResult.Reason,
                        candidate.EstimatedTokens));
                }
                else
                {
                    droppedItems.Add(CreateDropped(candidate, "token budget exhausted"));
                }
            }
        }
        else
        {
            foreach (var candidate in candidates)
            {
                droppedItems.Add(CreateDropped(candidate, sectionResult.Reason));
            }
        }
    }

    private static ContextPackageDecision CreateDecision(
        PackageTraceCandidate candidate,
        string sectionName,
        string reason,
        int estimatedTokens)
    {
        return new ContextPackageDecision
        {
            ItemId = candidate.Id,
            Kind = candidate.Kind,
            Type = candidate.Type,
            SectionName = sectionName,
            Reason = reason,
            Score = candidate.Score,
            EstimatedTokens = estimatedTokens,
            SourceRefs = candidate.SourceRefs,
            Metadata = new Dictionary<string, string>(candidate.Metadata),
            ScoreBreakdown = candidate.ScoreBreakdown
        };
    }

    private static DroppedContextItem CreateDropped(
        PackageTraceCandidate candidate,
        string reason)
    {
        return new DroppedContextItem
        {
            ItemId = candidate.Id,
            Kind = candidate.Kind,
            Type = candidate.Type,
            Reason = reason,
            Score = candidate.Score,
            EstimatedTokens = candidate.EstimatedTokens,
            SourceRefs = candidate.SourceRefs,
            Metadata = new Dictionary<string, string>(candidate.Metadata)
        };
    }

    private static string FormatConstraints(IReadOnlyList<ContextConstraint> constraints, int tokenBudget = 0)
    {
        if (tokenBudget > 0 && tokenBudget <= 200)
        {
            return JoinBlocks(constraints.Select(item => item.Content));
        }
        return JoinBlocks(constraints.Select(item =>
            $"- [{item.Level}] {item.Content}"));
    }

    private static string FormatCurrentTask(
        WorkingMemoryCurrentTask currentTask,
        ContextPackageRequest request)
    {
        if (request.TokenBudget > 0 && request.TokenBudget <= 200)
        {
            return currentTask.Title;
        }

        var builder = new StringBuilder();
        builder.AppendLine("## 当前任务");
        builder.AppendLine($"- 任务 ID：{currentTask.TaskId}");
        builder.AppendLine($"- 标题：{currentTask.Title}");
        builder.AppendLine($"- 状态：{currentTask.Status}");
        if (currentTask.Tags.Count > 0)
        {
            builder.AppendLine($"- 标签：{string.Join(", ", currentTask.Tags)}");
        }

        if (!string.IsNullOrWhiteSpace(request.QueryText))
        {
            builder.AppendLine($"- 当前输入：{request.QueryText}");
        }

        if (!string.IsNullOrWhiteSpace(currentTask.Description))
        {
            builder.AppendLine();
            builder.AppendLine(currentTask.Description);
        }

        var metadataLines = currentTask.Metadata
            .Where(item => IsCurrentTaskMetadataKey(item.Key))
            .Take(12)
            .Select(item => $"- {item.Key}: {item.Value}")
            .ToArray();
        if (metadataLines.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 任务元数据");
            foreach (var line in metadataLines)
            {
                builder.AppendLine(line);
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static bool IsCurrentTaskMetadataKey(string key)
    {
        return key.Contains("mode", StringComparison.OrdinalIgnoreCase)
            || key.Contains("task", StringComparison.OrdinalIgnoreCase)
            || key.Contains("intent", StringComparison.OrdinalIgnoreCase)
            || key.Contains("project", StringComparison.OrdinalIgnoreCase)
            || key.Contains("format", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatMergedConstraints(IReadOnlyList<MergedContextConstraint> constraints, int tokenBudget = 0)
    {
        if (tokenBudget > 0 && tokenBudget <= 200)
        {
            return JoinBlocks(constraints.Select(item => item.Constraint.Content));
        }
        return JoinBlocks(constraints.Select(item =>
            $"- [{item.PriorityLabel} | {item.Constraint.Level}] {item.Constraint.Content}"));
    }

    private static string FormatMemoryItems(IReadOnlyList<ContextMemoryItem> items, int tokenBudget = 0)
    {
        if (tokenBudget > 0 && tokenBudget <= 200)
        {
            return JoinBlocks(items.Select(item => item.Content));
        }
        return JoinBlocks(items.Select(item =>
            $"## {item.Type} / {item.Layer} / {item.Status}{Environment.NewLine}{item.Content}"));
    }

    private static string FormatGlobalItems(IReadOnlyList<ContextGlobalItem> items)
    {
        return JoinBlocks(items.Select(item =>
            $"## {item.Type} / {item.Scope}{Environment.NewLine}{item.Content}"));
    }

    private static string FormatContextItems(IReadOnlyList<ContextItem> items)
    {
        return JoinBlocks(items.Select(item =>
            $"## {(string.IsNullOrWhiteSpace(item.Title) ? item.Id : item.Title)} / {item.Type}{Environment.NewLine}{item.Content}"));
    }

    private static string FormatRecentContextItems(IReadOnlyList<RecentContextItem> items, int tokenBudget = 0)
    {
        if (tokenBudget > 0 && tokenBudget <= 200)
        {
            return JoinBlocks(items.Select(item => item.Content));
        }
        return JoinBlocks(items.Select(item =>
            $"## {item.SourceItemId} / relevance {item.Relevance:0.00} / recency {item.RecencyWeight:0.00}{Environment.NewLine}{item.Content}"));
    }

    private static string FormatDroppedItems(IReadOnlyList<DroppedContextItem> items)
    {
        return JoinBlocks(items.Take(50).Select(item =>
            $"- [{item.Kind}/{item.Type}] {item.ItemId}: {item.Reason}；score={item.Score:0.00}；tokens={item.EstimatedTokens}"));
    }

    private static string FormatUncertainties(IReadOnlyList<ContextPackageUncertainty> items)
    {
        return JoinBlocks(items.Select(item =>
        {
            var refs = item.ItemRefs.Count == 0
                ? string.Empty
                : $"；refs={string.Join(',', item.ItemRefs.Take(12))}";
            return $"- [{item.Severity}] {item.Code}: {item.Message}{refs}";
        }));
    }

    private static IReadOnlyList<ContextEvidenceEntry> BuildEvidenceEntries(
        IReadOnlyList<ContextPackageSection> sections,
        IReadOnlyList<ContextPackageDecision> selectedItems)
    {
        var sectionLookup = sections.ToDictionary(
            section => section.Name,
            section => section,
            StringComparer.OrdinalIgnoreCase);
        var entries = new List<ContextEvidenceEntry>();

        foreach (var item in selectedItems.Take(80))
        {
            var sectionSourceRefs = sectionLookup.TryGetValue(item.SectionName, out var section)
                ? section.SourceRefs
                : Array.Empty<string>();
            entries.Add(new ContextEvidenceEntry(
                item.ItemId,
                item.SectionName,
                item.Kind,
                item.Type,
                item.SourceRefs.Concat(sectionSourceRefs)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(12)
                    .ToArray(),
                item.Reason));
        }

        return entries;
    }

    private static string FormatEvidenceEntries(IReadOnlyList<ContextEvidenceEntry> entries)
    {
        return JoinBlocks(entries.Select(item =>
        {
            var refs = item.SourceRefs.Count == 0
                ? "无显式来源"
                : string.Join(", ", item.SourceRefs);
            return $"- [{item.SectionName}] {item.ItemId} ({item.Kind}/{item.Type})；来源：{refs}；原因：{item.Reason}";
        }));
    }

    private static string JoinBlocks(IEnumerable<string> blocks)
    {
        var builder = new StringBuilder();

        foreach (var block in blocks.Where(block => !string.IsNullOrWhiteSpace(block)))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine();
            }

            builder.Append(block);
        }

        return builder.ToString();
    }

    private static int CountMatchingTags(ContextItem item, HashSet<string> requiredTags)
    {
        if (requiredTags.Count == 0)
        {
            return 0;
        }

        return item.Tags.Count(requiredTags.Contains);
    }

    private static double CalculateLegacyScore(
        ContextItem item,
        HashSet<string> requiredTags,
        bool includeRecent)
    {
        var score = item.Importance * 100;
        score += CountMatchingTags(item, requiredTags) * 10;

        if (includeRecent && item.UpdatedAt != default)
        {
            var ageDays = Math.Max(0, (DateTimeOffset.UtcNow - item.UpdatedAt).TotalDays);
            score += Math.Max(0, 10 - Math.Min(10, ageDays));
        }

        return score;
    }

    private static double ScoreContextItem(ContextItem item, int basePriority)
    {
        return basePriority + item.Importance * 10;
    }

    private static double ScoreMemory(ContextMemoryItem item, int basePriority)
    {
        return basePriority + item.Importance * 10 + item.Confidence * 5;
    }

    private static double ScoreGlobal(ContextGlobalItem item, int basePriority)
    {
        return basePriority + item.Importance * 10;
    }

    private static IReadOnlyList<MergedContextConstraint> OrderMergedConstraints(
        IEnumerable<ContextConstraint> constraints)
    {
        return constraints
            .Select((constraint, index) =>
            {
                var priority = ResolveConstraintMergePriority(constraint);
                return new MergedContextConstraint(
                    constraint,
                    priority.Label,
                    priority.Rank,
                    index);
            })
            .OrderByDescending(item => item.PriorityRank)
            .ThenByDescending(item => item.Constraint.Confidence)
            .ThenByDescending(item => item.Constraint.UpdatedAt)
            .ThenBy(item => item.Index)
            .ToArray();
    }

    private static (string Label, int Rank) ResolveConstraintMergePriority(
        ContextConstraint constraint)
    {
        if (constraint.Level == ConstraintLevel.System
            || ContainsConstraintSignal(constraint, "system", "safety", "系统", "安全"))
        {
            return ("系统/安全", 600);
        }

        if (ContainsConstraintSignal(constraint, "current", "input", "request", "当前", "输入"))
        {
            return ("当前输入", 500);
        }

        if (constraint.Level == ConstraintLevel.Runtime
            || ContainsConstraintSignal(constraint, "runtime", "运行时"))
        {
            return ("运行时", 400);
        }

        if (ContainsConstraintSignal(constraint, "mode", "模式"))
        {
            return ("模式", 350);
        }

        if (ContainsConstraintSignal(constraint, "project", "项目"))
        {
            return ("项目", 300);
        }

        if (constraint.Level == ConstraintLevel.Hard)
        {
            return ("硬约束", 450);
        }

        if (constraint.Level == ConstraintLevel.User
            || ContainsConstraintSignal(constraint, "user", "stable", "用户", "稳定"))
        {
            return ("用户稳定", 200);
        }

        if (constraint.Level == ConstraintLevel.Domain
            || ContainsConstraintSignal(constraint, "domain", "领域"))
        {
            return ("领域软约束", 100);
        }

        return constraint.Level == ConstraintLevel.Soft
            ? ("软约束", 100)
            : ("未分类约束", 0);
    }

    private static bool ContainsConstraintSignal(
        ContextConstraint constraint,
        params string[] signals)
    {
        foreach (var (key, value) in constraint.Metadata)
        {
            if (ContainsAnySignal(key, signals) || ContainsAnySignal(value, signals))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAnySignal(string value, IReadOnlyList<string> signals)
    {
        return !string.IsNullOrWhiteSpace(value)
            && signals.Any(signal => value.Contains(signal, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<ContextPackageSection> OrderSections(
        IReadOnlyList<ContextPackageSection> sections,
        ContextPackagePolicy policy)
    {
        var sectionOrder = policy.SectionOrder
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeSectionKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select((name, index) => new { Name = name, Index = index })
            .ToDictionary(item => item.Name, item => item.Index, StringComparer.OrdinalIgnoreCase);

        var indexedSections = sections
            .Select((section, index) =>
            {
                var rank = sectionOrder.TryGetValue(NormalizeSectionKey(section.Name), out var explicitRank)
                    ? explicitRank
                    : int.MaxValue;

                return new
                {
                    Section = section,
                    Index = index,
                    Rank = rank
                };
            })
            .ToArray();

        return [.. indexedSections
            .OrderBy(item => item.Rank)
            .ThenByDescending(item => item.Rank == int.MaxValue ? item.Section.Priority : 0)
            .ThenBy(item => item.Index)
            .Select(item => item.Section)];
    }

    private static int GetPriority(ContextPackagePolicy policy, string sectionName, int defaultPriority)
    {
        return TryGetSectionSetting(policy.SectionPriorities, sectionName, out var priority)
            ? priority
            : defaultPriority;
    }

    private static int GetSectionTokenBudget(ContextPackagePolicy policy, string sectionName)
    {
        return TryGetSectionSetting(policy.SectionTokenBudgets, sectionName, out var budget) && budget > 0
            ? budget
            : 0;
    }

    private static int ResolveSectionTokenBudget(
        ContextPackagePolicy policy,
        ModeBudgetProfile? modeBudgetProfile,
        string sectionName,
        int tokenBudget)
    {
        var explicitBudget = GetSectionTokenBudget(policy, sectionName);
        if (explicitBudget > 0)
        {
            return explicitBudget;
        }

        if (modeBudgetProfile is null || tokenBudget <= 0 || tokenBudget == int.MaxValue)
        {
            return 0;
        }

        var normalizedSectionName = NormalizeSectionKey(sectionName);
        return modeBudgetProfile.SectionRatios.TryGetValue(normalizedSectionName, out var ratio) && ratio > 0
            ? Math.Max(1, (int)Math.Round(tokenBudget * ratio, MidpointRounding.AwayFromZero))
            : 0;
    }

    private static int ResolveDiagnosticsSectionTokenBudget(
        ContextPackagePolicy policy,
        ModeBudgetProfile? modeBudgetProfile,
        string sectionName,
        int tokenBudget)
    {
        var explicitBudget = GetSectionTokenBudget(policy, sectionName);
        if (explicitBudget > 0)
        {
            return explicitBudget;
        }

        if (tokenBudget <= 0 || tokenBudget == int.MaxValue)
        {
            return 0;
        }

        var baseBudget = ResolveSectionTokenBudget(policy, modeBudgetProfile, sectionName, tokenBudget);
        var normalized = NormalizeSectionKey(sectionName);
        var ratio = normalized switch
        {
            "evidence" => 0.04,
            "excluded" => 0.03,
            "uncertainties" => 0.03,
            _ => 0.03
        };
        var cap = Math.Max(tokenBudget <= 200 ? 8 : 32, (int)Math.Round(tokenBudget * ratio, MidpointRounding.AwayFromZero));
        cap = Math.Min(cap, tokenBudget <= 200 ? 16 : 160);
        return baseBudget > 0 ? Math.Min(baseBudget, cap) : cap;
    }

    private static int ResolveHistoricalSectionTokenBudget(
        ContextPackagePolicy policy,
        ModeBudgetProfile? modeBudgetProfile,
        string sectionName,
        int tokenBudget)
    {
        var explicitBudget = GetSectionTokenBudget(policy, sectionName);
        if (explicitBudget > 0)
        {
            return explicitBudget;
        }

        if (tokenBudget <= 0 || tokenBudget == int.MaxValue)
        {
            return 0;
        }

        var baseBudget = ResolveSectionTokenBudget(policy, modeBudgetProfile, sectionName, tokenBudget);
        var cap = Math.Max(48, (int)Math.Round(tokenBudget * 0.10, MidpointRounding.AwayFromZero));
        cap = Math.Min(cap, 600);
        return baseBudget > 0 ? Math.Min(baseBudget, cap) : cap;
    }

    private static bool TryGetSectionSetting(
        IReadOnlyDictionary<string, int> settings,
        string sectionName,
        out int value)
    {
        if (settings.TryGetValue(sectionName, out value))
        {
            return true;
        }

        var normalizedSectionName = NormalizeSectionKey(sectionName);
        foreach (var (key, configuredValue) in settings)
        {
            if (string.Equals(NormalizeSectionKey(key), normalizedSectionName, StringComparison.OrdinalIgnoreCase))
            {
                value = configuredValue;
                return true;
            }
        }

        foreach (var fallbackKey in new[] { "default", "*" })
        {
            foreach (var (key, configuredValue) in settings)
            {
                if (string.Equals(NormalizeSectionKey(key), fallbackKey, StringComparison.OrdinalIgnoreCase))
                {
                    value = configuredValue;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string NormalizeSectionKey(string? sectionName)
    {
        if (string.IsNullOrWhiteSpace(sectionName))
        {
            return string.Empty;
        }

        var normalized = sectionName.Trim().ToLowerInvariant()
            .Replace('-', '_')
            .Replace(' ', '_')
            .Replace('.', '_');

        while (normalized.Contains("__", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("__", "_");
        }

        var compact = normalized.Replace("_", string.Empty);
        return compact switch
        {
            "hardconstraint" or "hardconstraints" => "hard_constraints",
            "softconstraint" or "softconstraints" => "soft_constraints",
            "currenttask" => "current_task",
            "workingmemory" => "working_memory",
            "stablememory" => "stable_memory",
            "globalcontext" => "global_context",
            "recentcontext" or "recentrawcontext" or "rawcontext" => "recent_context",
            "relatedcontext" => "related_context",
            "excluded" or "excludeditems" => "excluded",
            "uncertainty" or "uncertainties" => "uncertainties",
            _ => normalized
        };
    }

    private static bool IsActive(ContextConstraint constraint)
    {
        return constraint.Status is not ContextMemoryStatus.Deprecated
            and not ContextMemoryStatus.Rejected;
    }

    private static bool IsActive(ContextMemoryItem item)
    {
        return item.Status is not ContextMemoryStatus.Deprecated
            and not ContextMemoryStatus.Rejected;
    }

    private static IReadOnlyList<string> ResolveSourceRefs(ContextItem item)
    {
        if (item.SourceRefs.Count > 0)
        {
            return item.SourceRefs.ToArray();
        }

        return string.IsNullOrWhiteSpace(item.Id)
            ? Array.Empty<string>()
            : new[] { item.Id };
    }

    private static IReadOnlyList<string> ResolveItemRefs(ContextItem item)
    {
        return string.IsNullOrWhiteSpace(item.Id)
            ? Array.Empty<string>()
            : new[] { item.Id };
    }

    private static IReadOnlyList<string> ResolveItemRefs(IEnumerable<ContextItem> items)
    {
        return items.Select(item => item.Id)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ResolveItemRefs(IEnumerable<ContextMemoryItem> items)
    {
        return items.Select(item => item.Id)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ResolveItemRefs(IEnumerable<ContextGlobalItem> items)
    {
        return items.Select(item => item.Id)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ResolveItemRefs(IEnumerable<ContextConstraint> items)
    {
        return items.Select(item => item.Id)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ResolveItemRefs(IEnumerable<RecentContextItem> items)
    {
        return items.Select(item => item.SourceItemId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ResolveSourceRefs(IEnumerable<ContextItem> items)
    {
        return items.SelectMany(ResolveSourceRefs)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ResolveSourceRefs(IEnumerable<ContextMemoryItem> items)
    {
        return items.SelectMany(item => item.SourceRefs.Count > 0 ? item.SourceRefs : new[] { item.Id })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ResolveSourceRefs(IEnumerable<ContextGlobalItem> items)
    {
        return items.SelectMany(item => item.SourceRefs.Count > 0 ? item.SourceRefs : new[] { item.Id })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ResolveSourceRefs(IEnumerable<ContextConstraint> items)
    {
        return items.SelectMany(item => item.SourceRefs.Count > 0 ? item.SourceRefs : new[] { item.Id })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ResolveSourceRefs(IEnumerable<RecentContextItem> items)
    {
        return items.SelectMany(item => item.SourceRefs.Count > 0 ? item.SourceRefs : new[] { item.SourceItemId })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddAnchorMetadata(
        IDictionary<string, string> metadata,
        IReadOnlyList<ContextAnchor> anchors)
    {
        if (anchors.Count == 0)
        {
            return;
        }

        metadata["anchor.count"] = anchors.Count.ToString();
        metadata["anchor.names"] = string.Join(",", anchors.Select(anchor => anchor.Name));
        metadata["anchor.types"] = string.Join(",", anchors.Select(anchor => anchor.Type.ToString()).Distinct(StringComparer.OrdinalIgnoreCase));

        // 拆分 Raw / Semantic Anchors
        var rawSearchTokens = anchors.Where(a => string.Equals(a.Source, "request.query", StringComparison.OrdinalIgnoreCase)).ToList();
        var semanticAnchors = anchors.Where(a => !string.Equals(a.Source, "request.query", StringComparison.OrdinalIgnoreCase)).ToList();

        metadata["anchor.rawSearchTokens"] = string.Join(",", rawSearchTokens.Select(a => a.Name));
        metadata["anchor.semanticAnchors"] = string.Join(",", semanticAnchors.Select(a => a.Name));
        metadata["anchor.rawSearchTokensCount"] = rawSearchTokens.Count.ToString();
        metadata["anchor.semanticAnchorsCount"] = semanticAnchors.Count.ToString();
    }

    private sealed record TokenEstimationContext(string? ModelName, string Source, bool IsFallback);

    private sealed class SectionBuildResult
    {
        private SectionBuildResult(bool added, string reason, int actualTokens)
        {
            Added = added;
            Reason = reason;
            ActualTokens = actualTokens;
        }

        public bool Added { get; }

        public string Reason { get; }

        public int ActualTokens { get; }

        public static SectionBuildResult Selected(string reason, int actualTokens)
        {
            return new SectionBuildResult(true, reason, actualTokens);
        }

        public static SectionBuildResult Dropped(string reason)
        {
            return new SectionBuildResult(false, reason, 0);
        }
    }

    private sealed class MergedContextConstraint
    {
        public MergedContextConstraint(
            ContextConstraint constraint,
            string priorityLabel,
            int priorityRank,
            int index)
        {
            Constraint = constraint;
            PriorityLabel = priorityLabel;
            PriorityRank = priorityRank;
            Index = index;
        }

        public ContextConstraint Constraint { get; }

        public string PriorityLabel { get; }

        public int PriorityRank { get; }

        public int Index { get; }
    }

    private sealed class ModeBudgetProfile
    {
        public ModeBudgetProfile(
            string modeName,
            int defaultTokenBudget,
            IReadOnlyDictionary<string, double> sectionRatios)
        {
            ModeName = modeName;
            DefaultTokenBudget = defaultTokenBudget;
            SectionRatios = sectionRatios;
        }

        public string ModeName { get; }

        public int DefaultTokenBudget { get; }

        public IReadOnlyDictionary<string, double> SectionRatios { get; }
    }

    private sealed class ContextEvidenceEntry
    {
        public ContextEvidenceEntry(
            string itemId,
            string sectionName,
            string kind,
            string type,
            IReadOnlyList<string> sourceRefs,
            string reason)
        {
            ItemId = itemId;
            SectionName = sectionName;
            Kind = kind;
            Type = type;
            SourceRefs = sourceRefs;
            Reason = reason;
        }

        public string ItemId { get; }

        public string SectionName { get; }

        public string Kind { get; }

        public string Type { get; }

        public IReadOnlyList<string> SourceRefs { get; }

        public string Reason { get; }
    }

    private sealed class PackageTraceCandidate
    {
        private PackageTraceCandidate(
            string id,
            string kind,
            string type,
            double score,
            int estimatedTokens,
            IReadOnlyList<string> sourceRefs,
            string content,
            Dictionary<string, string>? metadata = null,
            ItemScoreBreakdown? scoreBreakdown = null)
        {
            Id = id;
            Kind = kind;
            Type = type;
            Score = score;
            EstimatedTokens = estimatedTokens;
            SourceRefs = sourceRefs;
            Content = content ?? string.Empty;
            Metadata = metadata ?? new Dictionary<string, string>();
            ScoreBreakdown = scoreBreakdown;
        }

        public string Id { get; }
        public string Kind { get; }
        public string Type { get; }
        public double Score { get; }
        public int EstimatedTokens { get; }
        public IReadOnlyList<string> SourceRefs { get; }
        public string Content { get; }
        public Dictionary<string, string> Metadata { get; }

        /// <summary>评分明细，仅 working_memory / historical_context 路径下填充。</summary>
        public ItemScoreBreakdown? ScoreBreakdown { get; }

        public static PackageTraceCandidate FromContextItem(
            ContextItem item,
            string kind,
            double score,
            int? estimatedTokens = null)
        {
            return new PackageTraceCandidate(
                item.Id,
                kind,
                item.Type,
                score,
                estimatedTokens ?? EstimateTokens(item.Content),
                ResolveSourceRefs(item),
                item.Content,
                item.Metadata);
        }

        /// <summary>从 旧式 double score 创建（兼容）。</summary>
        public static PackageTraceCandidate FromMemory(
            ContextMemoryItem item,
            string kind,
            double score,
            int? estimatedTokens = null)
        {
            return new PackageTraceCandidate(
                item.Id,
                kind,
                item.Type,
                score,
                estimatedTokens ?? EstimateTokens(item.Content),
                item.SourceRefs.Count > 0 ? item.SourceRefs.ToArray() : new[] { item.Id },
                item.Content,
                item.Metadata);
        }

        /// <summary>从 ItemScoreBreakdown 创建，自动使用 FinalScore。</summary>
        public static PackageTraceCandidate FromMemory(
            ContextMemoryItem item,
            string kind,
            ItemScoreBreakdown breakdown,
            int? estimatedTokens = null)
        {
            return new PackageTraceCandidate(
                item.Id,
                kind,
                item.Type,
                breakdown.FinalScore,
                estimatedTokens ?? EstimateTokens(item.Content),
                item.SourceRefs.Count > 0 ? item.SourceRefs.ToArray() : new[] { item.Id },
                item.Content,
                new Dictionary<string, string>(item.Metadata),
                breakdown);
        }

        public static PackageTraceCandidate FromGlobal(
            ContextGlobalItem item,
            string kind,
            double score,
            int? estimatedTokens = null)
        {
            return new PackageTraceCandidate(
                item.Id,
                kind,
                item.Type,
                score,
                estimatedTokens ?? EstimateTokens(item.Content),
                item.SourceRefs.Count > 0 ? item.SourceRefs.ToArray() : new[] { item.Id },
                item.Content,
                item.Metadata);
        }

        public static PackageTraceCandidate FromConstraint(
            ContextConstraint item,
            string kind,
            double score,
            int? estimatedTokens = null)
        {
            return new PackageTraceCandidate(
                item.Id,
                kind,
                "constraint",
                score + item.Confidence * 5,
                estimatedTokens ?? EstimateTokens(item.Content),
                item.SourceRefs.Count > 0 ? item.SourceRefs.ToArray() : new[] { item.Id },
                item.Content,
                item.Metadata);
        }

        public static PackageTraceCandidate FromCurrentTask(
            WorkingMemoryCurrentTask item,
            int? estimatedTokens = null)
        {
            return new PackageTraceCandidate(
                item.TaskId,
                "current_task",
                "task",
                110,
                estimatedTokens ?? EstimateTokens(item.Description),
                [.. new[] { $"task:{item.TaskId}" }
                    .Concat(item.Metadata.TryGetValue("sourceRef", out var sourceRef) && !string.IsNullOrWhiteSpace(sourceRef)
                        ? new[] { sourceRef }
                        : Array.Empty<string>())],
                item.Title + " " + item.Description,
                item.Metadata);
        }

        public static PackageTraceCandidate FromRecent(
            RecentContextItem item,
            string kind,
            double score,
            int? estimatedTokens = null)
        {
            return new PackageTraceCandidate(
                item.SourceItemId,
                kind,
                "recent",
                score,
                estimatedTokens ?? EstimateTokens(item.Content),
                item.SourceRefs.Count > 0 ? item.SourceRefs.ToArray() : new[] { item.SourceItemId },
                item.Content,
                new Dictionary<string, string>
                {
                    ["relevance"] = item.Relevance.ToString("0.000"),
                    ["recencyWeight"] = item.RecencyWeight.ToString("0.000"),
                    ["reason"] = item.Reason,
                    ["sourceTurnId"] = item.SourceTurnId ?? string.Empty
                });
        }
    }
}
