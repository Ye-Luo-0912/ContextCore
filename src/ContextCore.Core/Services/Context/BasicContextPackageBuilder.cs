using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Learning.V14_0;

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
    private readonly IDecisionTraceStore? _decisionTraceStore;
    private readonly IContextTokenizerResolver _tokenizerResolver;
    private readonly IWorkingMemoryService? _workingMemoryService;
    private readonly GraphExpansionApplyOptions _graphExpansionApplyOptions;
    private readonly GraphExpansionApplyPolicy? _graphExpansionApplyPolicy;
    private readonly IContextStore _store;
    private readonly IRuntimeCandidateTraceSink _runtimeCandidateTraceSink;
    private readonly RelationTraversalEngine? _traversalEngine;
    private readonly PackageTraceRecorder _traceRecorder;
    private readonly AsyncLocal<string?> _currentOperationId = new();
    private readonly AsyncLocal<string?> _currentRequestId = new();
    private readonly RecentContextFilter _recentContextFilter = new();
    private readonly ContextAnchorExtractor _anchorExtractor = new();
    private readonly RetrievalPlanner _planner = new();
    private int _decisionTraceWriteFailures;
    private DateTimeOffset _decisionTraceLastFailureAt;
    private string? _decisionTraceLastFailureCategory;

    /// <summary>decision trace 写入失败次数（fail-open，不影响正式 package 输出）。</summary>
    public int DecisionTraceWriteFailures => _decisionTraceWriteFailures;

    /// <summary>decision trace 最近一次写入失败时间；无失败则为 null。</summary>
    public DateTimeOffset? DecisionTraceLastFailureAt =>
        _decisionTraceWriteFailures > 0 ? _decisionTraceLastFailureAt : null;

    /// <summary>decision trace 最近一次写入失败的异常类别（Type.Name）；无失败则为 null。</summary>
    public string? DecisionTraceLastFailureCategory => _decisionTraceLastFailureCategory;

    /// <summary>decision trace sink 类型名（用于诊断报告）；未配置则为 null。</summary>
    public string? DecisionTraceSinkType => _decisionTraceStore?.GetType().FullName;

    /// <summary>observability 是否处于降级状态（任一 trace 路径存在写入失败）。</summary>
    public bool IsObservabilityDegraded =>
        _decisionTraceWriteFailures > 0 || _traceRecorder.TraceWriteFailures > 0;

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
        GraphExpansionApplyPolicy? graphExpansionApplyPolicy = null,
        IDecisionTraceStore? decisionTraceStore = null,
        IRuntimeCandidateTraceSink? runtimeCandidateTraceSink = null,
        RelationTraversalEngine? traversalEngine = null)
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
        _decisionTraceStore = decisionTraceStore;
        _runtimeCandidateTraceSink = runtimeCandidateTraceSink ?? new NullRuntimeCandidateTraceSink();
        _traversalEngine = traversalEngine;
        _traceRecorder = new PackageTraceRecorder(
            _runtimeCandidateTraceSink,
            () => _currentOperationId.Value,
            () => _currentRequestId.Value);
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

        // TRACE-01: 设置请求级 trace 上下文（AsyncLocal），替代全局静态状态。
        var prevOpId = _currentOperationId.Value;
        var prevReqId = _currentRequestId.Value;
        _currentOperationId.Value = request.OperationId ?? Guid.NewGuid().ToString("N");
        _currentRequestId.Value = request.RequestId ?? Guid.NewGuid().ToString("N");
        try
        {
            return await BuildDetailedCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _currentOperationId.Value = prevOpId;
            _currentRequestId.Value = prevReqId;
        }
    }

    private async Task<ContextPackageBuildResult> BuildDetailedCoreAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken)
    {
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

        // V17.0: 投影只读 decision trace，不改变 result。
        if (_decisionTraceStore is not null)
        {
            try
            {
                var decisionRecord = ContextDecisionProjector.ProjectPackage(result);
                await _decisionTraceStore.SaveAsync(decisionRecord, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // decision trace 写入失败不得影响正式 package 输出，但需记录降级指标。
                Interlocked.Increment(ref _decisionTraceWriteFailures);
                _decisionTraceLastFailureAt = DateTimeOffset.UtcNow;
                _decisionTraceLastFailureCategory = ex.GetType().Name;
            }
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
            .ThenByDescending(item => LegacyPackageScorer.CountMatchingTags(item, requiredTags))
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
            var score = LegacyPackageScorer.CalculateLegacyScore(item, requiredTags, request.IncludeRecent);

            // AddSection 内部负责预算裁剪；调用方只按优先级提供候选内容。
            var sectionResult = AddSection(
                sections,
                sourceRefs,
                name: sectionName,
                priority: priority--,
                content: item.Content,
                contentFormat: item.ContentFormat,
                sectionSourceRefs: ContextItemRefResolver.ResolveSourceRefs(item),
                sectionItemRefs: ContextItemRefResolver.ResolveItemRefs(item),
                candidateIds: ContextItemRefResolver.ResolveItemRefs(item),
                tokenBudget,
                sectionTokenBudget: 0,
                tokenContext,
                ref estimatedTokens);

            var candidate = PackageTraceCandidate.FromContextItem(item, "raw", score, itemTokens);
            if (sectionResult.Added)
            {
                selectedItems.Add(PackageTraceRecorder.CreateDecision(
                    candidate,
                    sectionName,
                    sectionResult.Reason,
                    sectionResult.ActualTokens));
                _traceRecorder.WriteTraceRow(candidate, sectionName, true, sectionResult.Reason, selectedByScoring: true);
            }
                else
                {
                    droppedItems.Add(PackageTraceRecorder.CreateDropped(candidate, "token budget exhausted"));
                    _traceRecorder.WriteTraceRow(candidate, sectionName, false, "token budget exhausted", selectedByScoring: true);
                }
        }

        var package = PackageMetadataBuilder.CreatePackage(request, request.CollectionId, sections, sourceRefs, estimatedTokens, tokenContext);
        return CreateBuildResult(
            request,
            package,
            tokenBudget,
            selectedItems,
            droppedItems,
            traceRecorder: _traceRecorder);
    }

    private async Task<ContextPackageBuildResult> BuildWithPolicyAsync(
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        CancellationToken cancellationToken)
    {
        var tokenContext = CreateTokenEstimationContext(request);
        var workspaceId = PackagePolicyResolver.NormalizeRequiredValue(request.WorkspaceId);
        var collectionId = PackagePolicyResolver.NormalizeRequiredValue(policy.CollectionId, request.CollectionId);
        var modeBudgetProfile = PackagePolicyResolver.ResolveModeBudgetProfile(request, policy);
        var tokenBudget = PackagePolicyResolver.ResolveTokenBudget(request, policy, modeBudgetProfile);
        var packageModeName = PackagePolicyResolver.ResolvePackageModeName(request, policy, modeBudgetProfile);
        var packageMustHitIds = PackagePolicyResolver.ResolvePackageMustHitIds(request);

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
        SectionPackingResult sectionResult;

        // 全局去重拦截与引用记录
        var globalSelectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var primaryDecisions = new Dictionary<string, ContextPackageDecision>(StringComparer.OrdinalIgnoreCase);
        var itemReferences = new List<ContextPackageItemReference>();

        // 显式审计模式判定
        var isAuditMode = !string.IsNullOrWhiteSpace(request.QueryText)
            && WorkingMemoryRecaller.DomainKeywords.AuditModeKeywords.Any(k => request.QueryText.Contains(k, StringComparison.OrdinalIgnoreCase));

        if (PackagePolicyResolver.ShouldIncludeCurrentTaskSection(request, policy))
        {
            var currentTask = await ResolveCurrentTaskAsync(
                request,
                collectionId,
                cancellationToken).ConfigureAwait(false);
            if (currentTask is not null)
            {
                var content = PackageSectionFormatter.FormatCurrentTask(currentTask, request);
                var currentTaskCandidate = PackageTraceCandidate.FromCurrentTask(
                    currentTask,
                    EstimatePackageTokens(content, tokenContext));
                sectionResult = AddSection(
                    sections,
                    sourceRefs,
                    "current_task",
                    PackageSectionBudgetResolver.GetPriority(policy, "current_task", 110),
                    content,
                    ContextContentFormat.Markdown,
                    currentTaskCandidate.SourceRefs,
                    [currentTask.TaskId],
                    [currentTask.TaskId],
                    tokenBudget,
                    PackageSectionBudgetResolver.ResolveSectionTokenBudget(policy, modeBudgetProfile, "current_task", tokenBudget),
                    tokenContext,
                    ref estimatedTokens);
                
                _traceRecorder.AddSectionDecisionsWithDedup(
                    selectedItems,
                    droppedItems,
                    [currentTaskCandidate],
                    "current_task",
                    sectionResult,
                    globalSelectedIds,
                    primaryDecisions,
                    itemReferences);
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

        if ((policy.IncludeHardConstraints || !PackagePolicyResolver.ShouldIncludeMergedConstraintsSection(request, policy)) && _constraintStore is not null)
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

            var activeHardConstraints = hardConstraints.Where(LegacyPackageScorer.IsActive).ToArray();
            foreach (var item in activeHardConstraints)
            {
                addedConstraintIds.Add(item.Id);
            }
            droppedItems.AddRange(hardConstraints
                .Where(item => !LegacyPackageScorer.IsActive(item))
                .Select(item => {
                    var c = PackageTraceCandidate.FromConstraint(item, "hard_constraint", 100, EstimatePackageTokens(item.Content, tokenContext));
                    _traceRecorder.WriteTraceRow(c, "hard_constraints", false, "constraint is deprecated or rejected", selectedByScoring: false);
                    return PackageTraceRecorder.CreateDropped(c, "constraint is deprecated or rejected");
                }));

            var hardCandidates = activeHardConstraints
                .Select(item => PackageTraceCandidate.FromConstraint(item, "hard_constraint", 100, EstimatePackageTokens(item.Content, tokenContext)))
                .ToArray();

            if (activeHardConstraints.Length > 0)
            {
                // 硬约束过滤掉已被选中的 ID (一般不会有，但防止意外)
                var hardToFormat = activeHardConstraints.Where(c => !globalSelectedIds.Contains(c.Id)).ToArray();
                var hardContent = hardToFormat.Length > 0 ? PackageSectionFormatter.FormatConstraints(hardToFormat, tokenBudget) : "(所有硬约束已在更优 Section 中包含)";

                sectionResult = AddSection(
                    sections,
                    sourceRefs,
                    "hard_constraints",
                    PackageSectionBudgetResolver.GetPriority(policy, "hard_constraints", 100),
                    hardContent,
                    ContextContentFormat.Markdown,
                    ContextItemRefResolver.ResolveSourceRefs(activeHardConstraints),
                    ContextItemRefResolver.ResolveItemRefs(activeHardConstraints),
                    ContextItemRefResolver.ResolveItemRefs(activeHardConstraints),
                    tokenBudget,
                    PackageSectionBudgetResolver.ResolveSectionTokenBudget(policy, modeBudgetProfile, "hard_constraints", tokenBudget),
                    tokenContext,
                    ref estimatedTokens);

                _traceRecorder.AddSectionDecisionsWithDedup(
                    selectedItems,
                    droppedItems,
                    hardCandidates,
                    "hard_constraints",
                    sectionResult,
                    globalSelectedIds,
                    primaryDecisions,
                    itemReferences);
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
                var workingContent = workingToFormat.Length > 0 ? PackageSectionFormatter.FormatMemoryItems(workingToFormat, tokenBudget) : "(所有活跃工作区记忆已在此前去重包含)";

                sectionResult = AddSection(
                    sections,
                    sourceRefs,
                    "working_memory",
                    PackageSectionBudgetResolver.GetPriority(policy, "working_memory", 90),
                    workingContent,
                    ContextContentFormat.Markdown,
                    ContextItemRefResolver.ResolveSourceRefs(activeWorking),
                    ContextItemRefResolver.ResolveItemRefs(activeWorking),
                    ContextItemRefResolver.ResolveItemRefs(activeWorking),
                    tokenBudget,
                    PackageSectionBudgetResolver.ResolveSectionTokenBudget(policy, modeBudgetProfile, "working_memory", tokenBudget),
                    tokenContext,
                    ref estimatedTokens);

                _traceRecorder.AddSectionDecisionsWithDedup(
                    selectedItems,
                    droppedItems,
                    workingCandidates,
                    "working_memory",
                    sectionResult,
                    globalSelectedIds,
                    primaryDecisions,
                    itemReferences);
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
                    var historicalContent = historicalToFormat.Length > 0 ? PackageSectionFormatter.FormatMemoryItems(historicalToFormat, tokenBudget) : "(所有历史审计记忆已在此前去重包含)";

                    sectionResult = AddSection(
                        sections,
                        sourceRefs,
                        "historical_context",
                        PackageSectionBudgetResolver.GetPriority(policy, "historical_context", 15),
                        historicalContent,
                        ContextContentFormat.Markdown,
                        ContextItemRefResolver.ResolveSourceRefs(deprecatedWorking),
                        ContextItemRefResolver.ResolveItemRefs(deprecatedWorking),
                        ContextItemRefResolver.ResolveItemRefs(deprecatedWorking),
                        tokenBudget,
                        PackageSectionBudgetResolver.ResolveHistoricalSectionTokenBudget(policy, modeBudgetProfile, "historical_context", tokenBudget),
                        tokenContext,
                        ref estimatedTokens);

                    _traceRecorder.AddSectionDecisionsWithDedup(
                        selectedItems,
                        droppedItems,
                        historicalCandidates,
                        "historical_context",
                        sectionResult,
                        globalSelectedIds,
                        primaryDecisions,
                        itemReferences);
                }
                else
                {
                    foreach (var candidate in historicalCandidates)
                    {
                        _traceRecorder.WriteTraceRow(candidate, "historical_context", false, "deprecated memory is excluded in non-audit mode", selectedByScoring: false);
                        droppedItems.Add(PackageTraceRecorder.CreateDropped(candidate, "deprecated memory is excluded in non-audit mode"));
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
            var globalContent = globalToFormat.Length > 0 ? PackageSectionFormatter.FormatGlobalItems(globalToFormat) : "(所有全局上下文已在此前去重包含)";

            sectionResult = AddSection(
                sections,
                sourceRefs,
                "global_context",
                PackageSectionBudgetResolver.GetPriority(policy, "global_context", 80),
                globalContent,
                ContextContentFormat.Markdown,
                ContextItemRefResolver.ResolveSourceRefs(globalItems),
                ContextItemRefResolver.ResolveItemRefs(globalItems),
                ContextItemRefResolver.ResolveItemRefs(globalItems),
                tokenBudget,
                PackageSectionBudgetResolver.ResolveSectionTokenBudget(policy, modeBudgetProfile, "global_context", tokenBudget),
                tokenContext,
                ref estimatedTokens);

            _traceRecorder.AddSectionDecisionsWithDedup(
                selectedItems,
                droppedItems,
                globalCandidates,
                "global_context",
                sectionResult,
                globalSelectedIds,
                primaryDecisions,
            itemReferences);
        }

        if (policy.IncludeRecentRawContext)
        {
            foreach (var item in includedRecent)
            {
                selectedSourceIds.Add(item.SourceItemId);
            }

            droppedItems.AddRange(excludedRecent.Select(item =>
            {
                var c = PackageTraceCandidate.FromRecent(item, "recent_context", item.Relevance * 79.0, EstimatePackageTokens(item.Content, tokenContext));
                _traceRecorder.WriteTraceRow(c, "recent_context", false, item.ExcludeReason ?? "recent context excluded", selectedByScoring: false);
                return PackageTraceRecorder.CreateDropped(c, item.ExcludeReason ?? "recent context excluded");
            }));

            var recentCandidates = includedRecent
                .Select(item => PackageTraceCandidate.FromRecent(item, "recent_context", item.Relevance * 79.0, EstimatePackageTokens(item.Content, tokenContext)))
                .ToArray();

            var recentToFormat = includedRecent.Where(item => !globalSelectedIds.Contains(item.SourceItemId)).ToArray();
            var recentContent = recentToFormat.Length > 0 ? PackageSectionFormatter.FormatRecentContextItems(recentToFormat, tokenBudget) : "(所有近期短期上下文已在此前去重包含)";

            sectionResult = AddSection(
                sections,
                sourceRefs,
                "recent_context",
                PackageSectionBudgetResolver.GetPriority(policy, "recent_context", 70),
                recentContent,
                ContextContentFormat.Markdown,
                ContextItemRefResolver.ResolveSourceRefs(includedRecent),
                ContextItemRefResolver.ResolveItemRefs(includedRecent),
                ContextItemRefResolver.ResolveItemRefs(includedRecent),
                tokenBudget,
                PackageSectionBudgetResolver.ResolveSectionTokenBudget(policy, modeBudgetProfile, "recent_context", tokenBudget),
                tokenContext,
                ref estimatedTokens);

            _traceRecorder.AddSectionDecisionsWithDedup(
                selectedItems,
                droppedItems,
                recentCandidates,
                "recent_context",
                sectionResult,
                globalSelectedIds,
                primaryDecisions,
            itemReferences);
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
            stableMemory = WorkingMemoryRecaller.RecallStableMemory(
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
                    var searchText = WorkingMemoryRecaller.CreateMemorySearchText(item);
                    var scoreResult = ContextRecallSignalPolicy.ScoreStableMemoryForInjection(item, anchors, workingSignals, searchText);
                    var finalScore = scoreResult.Score;
                    return PackageTraceCandidate.FromMemory(item, "stable_memory", finalScore, EstimatePackageTokens(item.Content, tokenContext));
                })
                .ToArray();

            var stableToFormat = stableMemory.Where(item => !globalSelectedIds.Contains(item.Id)).ToArray();
            var stableContent = stableToFormat.Length > 0 ? PackageSectionFormatter.FormatMemoryItems(stableToFormat, tokenBudget) : "(所有稳定背景记忆已在此前去重包含)";

            sectionResult = AddSection(
                sections,
                sourceRefs,
                "stable_memory",
                PackageSectionBudgetResolver.GetPriority(policy, "stable_memory", 60),
                stableContent,
                ContextContentFormat.Markdown,
                ContextItemRefResolver.ResolveSourceRefs(stableMemory),
                ContextItemRefResolver.ResolveItemRefs(stableMemory),
                ContextItemRefResolver.ResolveItemRefs(stableMemory),
                tokenBudget,
                PackageSectionBudgetResolver.ResolveSectionTokenBudget(policy, modeBudgetProfile, "stable_memory", tokenBudget),
                tokenContext,
                ref estimatedTokens);

            _traceRecorder.AddSectionDecisionsWithDedup(
                selectedItems,
                droppedItems,
                stableCandidates,
                "stable_memory",
                sectionResult,
                globalSelectedIds,
                primaryDecisions,
            itemReferences);
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

            var activeSoftConstraints = softConstraints.Where(LegacyPackageScorer.IsActive).ToArray();
            foreach (var item in activeSoftConstraints)
            {
                addedConstraintIds.Add(item.Id);
            }
            droppedItems.AddRange(softConstraints
                .Where(item => !LegacyPackageScorer.IsActive(item))
                .Select(item => {
                    var c = PackageTraceCandidate.FromConstraint(item, "soft_constraint", 15.0, EstimatePackageTokens(item.Content, tokenContext));
                    _traceRecorder.WriteTraceRow(c, "soft_constraints", false, "constraint is deprecated or rejected", selectedByScoring: false);
                    return PackageTraceRecorder.CreateDropped(c, "constraint is deprecated or rejected");
                }));

            var softCandidates = activeSoftConstraints
                .Select(item => PackageTraceCandidate.FromConstraint(item, "soft_constraint", 15.0, EstimatePackageTokens(item.Content, tokenContext)))
                .ToArray();

            var softToFormat = activeSoftConstraints.Where(c => !globalSelectedIds.Contains(c.Id)).ToArray();
            var softContent = softToFormat.Length > 0 ? PackageSectionFormatter.FormatConstraints(softToFormat, tokenBudget) : "(所有软约束已在此前去重包含)";

            sectionResult = AddSection(
                sections,
                sourceRefs,
                "soft_constraints",
                PackageSectionBudgetResolver.GetPriority(policy, "soft_constraints", 50),
                softContent,
                ContextContentFormat.Markdown,
                ContextItemRefResolver.ResolveSourceRefs(activeSoftConstraints),
                ContextItemRefResolver.ResolveItemRefs(activeSoftConstraints),
                ContextItemRefResolver.ResolveItemRefs(activeSoftConstraints),
                tokenBudget,
                PackageSectionBudgetResolver.ResolveSectionTokenBudget(policy, modeBudgetProfile, "soft_constraints", tokenBudget),
                tokenContext,
                ref estimatedTokens);

            _traceRecorder.AddSectionDecisionsWithDedup(
                selectedItems,
                droppedItems,
                softCandidates,
                "soft_constraints",
                sectionResult,
                globalSelectedIds,
                primaryDecisions,
            itemReferences);
        }

        if (PackagePolicyResolver.ShouldIncludeMergedConstraintsSection(request, policy))
        {
            var mergedConstraints = await ResolveMergedConstraintsAsync(
                request,
                policy,
                collectionId ?? string.Empty,
                cancellationToken).ConfigureAwait(false);
            var orderedMergedConstraints = LegacyPackageScorer.OrderMergedConstraints(mergedConstraints.Where(LegacyPackageScorer.IsActive).Where(c => !addedConstraintIds.Contains(c.Id)));
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
            var mergedContent = mergedToFormat.Length > 0 ? PackageSectionFormatter.FormatMergedConstraints(mergedToFormat, tokenBudget) : "(所有合并约束已在此前去重包含)";

            sectionResult = AddSection(
                sections,
                sourceRefs,
                "constraints",
                PackageSectionBudgetResolver.GetPriority(policy, "constraints", 95),
                mergedContent,
                ContextContentFormat.Markdown,
                ContextItemRefResolver.ResolveSourceRefs(activeMergedConstraints),
                ContextItemRefResolver.ResolveItemRefs(activeMergedConstraints),
                ContextItemRefResolver.ResolveItemRefs(activeMergedConstraints),
                tokenBudget,
                PackageSectionBudgetResolver.ResolveSectionTokenBudget(policy, modeBudgetProfile, "constraints", tokenBudget),
                tokenContext,
                ref estimatedTokens);

            _traceRecorder.AddSectionDecisionsWithDedup(
                selectedItems,
                droppedItems,
                mergedCandidates,
                "constraints",
                sectionResult,
                globalSelectedIds,
                primaryDecisions,
            itemReferences);
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
                var relatedContent = relatedToFormat.Length > 0 ? PackageSectionFormatter.FormatContextItems(relatedToFormat) : "(所有关联图谱扩展上下文已在此前去重包含)";

                sectionResult = AddSection(
                    sections,
                    sourceRefs,
                    "related_context",
                    PackageSectionBudgetResolver.GetPriority(policy, "related_context", 40),
                    relatedContent,
                    ContextContentFormat.Markdown,
                    ContextItemRefResolver.ResolveSourceRefs(relatedItems),
                    ContextItemRefResolver.ResolveItemRefs(relatedItems),
                    ContextItemRefResolver.ResolveItemRefs(relatedItems),
                    tokenBudget,
                    PackageSectionBudgetResolver.ResolveSectionTokenBudget(policy, modeBudgetProfile, "related_context", tokenBudget),
                    tokenContext,
                    ref estimatedTokens);

                _traceRecorder.AddSectionDecisionsWithDedup(
                    selectedItems,
                    droppedItems,
                    relatedCandidates,
                    "related_context",
                    sectionResult,
                    globalSelectedIds,
                    primaryDecisions,
                    itemReferences);
            }
        }

        if (PackagePolicyResolver.ShouldIncludeEvidenceSection(request, policy, selectedItems.Count > 0))
        {
            var evidenceItems = PackageSectionFormatter.BuildEvidenceEntries(sections, selectedItems);
            AddSection(
                sections,
                sourceRefs,
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
                ref estimatedTokens);
        }

        var uncertainties = PackageUncertaintyBuilder.BuildUncertainties(
            sections,
            selectedItems,
            droppedItems,
            lowConfidenceRelations,
            tokenBudget,
            estimatedTokens);
        if (PackagePolicyResolver.ShouldIncludeDiagnosticsSection(request, policy, "excluded", droppedItems.Count > 0))
        {
            AddSection(
                sections,
                sourceRefs,
                "excluded",
                PackageSectionBudgetResolver.GetPriority(policy, "excluded", 20),
                PackageSectionFormatter.FormatDroppedItems(droppedItems),
                ContextContentFormat.Markdown,
                Array.Empty<string>(),
                droppedItems.Select(item => item.ItemId).ToArray(),
                Array.Empty<string>(),
                tokenBudget,
                PackageSectionBudgetResolver.ResolveDiagnosticsSectionTokenBudget(policy, modeBudgetProfile, "excluded", tokenBudget),
                tokenContext,
                ref estimatedTokens);
        }

        if (PackagePolicyResolver.ShouldIncludeDiagnosticsSection(request, policy, "uncertainties", uncertainties.Count > 0))
        {
            AddSection(
                sections,
                sourceRefs,
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

        var metadata = PackageMetadataBuilder.CreatePackageMetadata(request, tokenContext);
        if (!string.IsNullOrWhiteSpace(policy.Id))
        {
            metadata["policyId"] = policy.Id;
        }
        ContextItemRefResolver.AddAnchorMetadata(metadata, anchors);
        PackageMetadataBuilder.AddModeBudgetMetadata(metadata, modeBudgetProfile);
        PackageMetadataBuilder.AddDiagnosticMetadata(metadata, tokenBudget, estimatedTokens, droppedItems.Count, uncertainties.Count);
        PackageMetadataBuilder.AddGraphExpansionMetadata(metadata, graphExpansionContribution);

        var orderedSections = PackageSectionBudgetResolver.OrderSections(sections, policy);

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
            retrievalPlan,
            traceRecorder: _traceRecorder,
            itemReferences: itemReferences);
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
            .OrderBy(group => PackageMetadataBuilder.ResolveGraphExpansionSectionPriority(group.Key))
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
                Priority = PackageMetadataBuilder.ResolveGraphExpansionSectionPriority(group.Key),
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

        var maxSeeds = PackagePolicyResolver.ResolveIntSetting(request, policy, "graphSeedMaxNodes", 12, min: 1, max: 50);
        var candidates = GraphSeedResolver.ExtractGraphSeedCandidates(workingMemory, anchors)
            .Select(GraphSeedResolver.NormalizeGraphSeedCandidate)
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
        if (seedIds.Length == 0)
        {
            return Array.Empty<ContextItem>();
        }

        var relationTypes = PackagePolicyResolver.ResolveRelationTypeWhitelist(request, policy);
        var maxDepth = PackagePolicyResolver.ResolveIntSetting(request, policy, "relationExpansionDepth", 1, min: 1, max: 2);
        var maxNodes = PackagePolicyResolver.ResolveIntSetting(request, policy, "relationMaxNodes", 20, min: 1, max: 100);
        var maxRelations = PackagePolicyResolver.ResolveIntSetting(request, policy, "relationMaxRelations", 60, min: 1, max: 300);
        var minConfidence = PackagePolicyResolver.ResolveDoubleSetting(request, policy, "relationMinConfidence", 0.35, min: 0, max: 1);

        // 通过统一遍历引擎执行双向 BFS；engine 不过滤置信度（MinConfidence=0），由 caller 做置信度过滤和 low-confidence 收集。
        var profile = new RelationExpansionProfile
        {
            ProfileId = "package-builder",
            Mode = "Normal",
            MaxDepth = maxDepth,
            MaxFanout = Math.Max(20, maxRelations),
            AllowedRelationTypes = [..relationTypes],
            MinConfidence = 0.0,
            AllowDeprecatedRelations = true,
            AllowCandidateRelations = true,
            AllowRejectedRelations = true,
            RequireEvidence = false
        };

        var traversalRequest = new RelationTraversalRequest
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Seeds = seedIds.Select(seedId => new RelationTraversalSeed(seedId)).ToArray(),
            Profile = profile,
            Direction = RelationDirection.Both,
            MaxNodesOverride = maxNodes,
            MaxRelationsOverride = maxRelations
        };

        var engine = _traversalEngine ?? new RelationTraversalEngine(_relationStore);
        var traversalResult = await engine.TraverseAsync(traversalRequest, cancellationToken).ConfigureAwait(false);

        var relatedItems = new List<ContextItem>();
        var relatedItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // low-confidence 关系收集：在置信度过滤前采集，按置信度升序取前 20，去重。
        foreach (var relation in traversalResult.Edges
            .Where(edge => edge.Relation.Confidence < minConfidence)
            .OrderBy(edge => edge.Relation.Confidence)
            .Take(20)
            .Select(edge => edge.Relation))
        {
            if (!lowConfidenceRelations.Any(item => string.Equals(item.Id, relation.Id, StringComparison.OrdinalIgnoreCase)))
            {
                lowConfidenceRelations.Add(relation);
            }
        }

        var containsDeprecatedKeywordInQuery = !string.IsNullOrWhiteSpace(request.QueryText) && (
            request.QueryText.Contains("废弃", StringComparison.OrdinalIgnoreCase)
            || request.QueryText.Contains("作废", StringComparison.OrdinalIgnoreCase)
            || request.QueryText.Contains("legacy", StringComparison.OrdinalIgnoreCase)
            || request.QueryText.Contains("deprecated", StringComparison.OrdinalIgnoreCase));

        var scannedRelations = 0;
        foreach (var edge in traversalResult.Edges
            .Where(e => e.Relation.Confidence >= minConfidence)
            .OrderByDescending(e => e.Relation.Weight)
            .ThenByDescending(e => e.Relation.Confidence))
        {
            scannedRelations++;
            if (scannedRelations > maxRelations || relatedItems.Count >= maxNodes)
            {
                break;
            }

            var relatedId = edge.NeighborId;
            if (string.IsNullOrWhiteSpace(relatedId) || !relatedItemIds.Add(relatedId))
            {
                continue;
            }

            var target = await _store.GetAsync(
                workspaceId,
                collectionId,
                relatedId,
                cancellationToken).ConfigureAwait(false);

            if (target is null)
            {
                continue;
            }

            var isDeprecated = target.Tags.Any(tag =>
                string.Equals(tag, "deprecated", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tag, "legacy", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tag, "superseded", StringComparison.OrdinalIgnoreCase));

            if (!isDeprecated || containsDeprecatedKeywordInQuery)
            {
                relatedItems.Add(target);
            }
        }

        return relatedItems
            .OrderByDescending(item => item.Importance)
            .ThenByDescending(item => item.UpdatedAt)
            .ToArray();
    }

    private async Task<IReadOnlyList<ContextConstraint>> ResolveMergedConstraintsAsync(
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var workspaceId = PackagePolicyResolver.NormalizeRequiredValue(request.WorkspaceId);
        var constraints = new List<ContextConstraint>();
        if (_constraintStore is not null)
        {
            // 合并约束只在显式开启时查询，并设置上限，避免为了可选 section 触发无界扫描。
            var take = PackagePolicyResolver.ResolveIntSetting(request, policy, "constraintMergeMaxItems", 100, 1, 500);
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

        constraints.AddRange(RequestTaskResolver.CreateRequestConstraints(request, collectionId));
        return constraints;
    }

    private async Task<WorkingMemoryCurrentTask?> ResolveCurrentTaskAsync(
        ContextPackageRequest request,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var hasMetadataTask = RequestTaskResolver.HasRequestCurrentTaskMetadata(request);
        var metadataTask = hasMetadataTask
            ? RequestTaskResolver.CreateRequestCurrentTask(request, collectionId)
            : null;
        if (_workingMemoryService is null)
        {
            return metadataTask ?? RequestTaskResolver.CreateRequestCurrentTask(request, collectionId);
        }

        var storedTask = await _workingMemoryService.GetCurrentTaskAsync(
            request.WorkspaceId,
            collectionId,
            cancellationToken).ConfigureAwait(false);

        // 当前输入优先级高于已保存任务；调用方显式传入 currentTask* metadata 时使用请求侧描述。
        return hasMetadataTask
            ? metadataTask ?? storedTask
            : storedTask ?? RequestTaskResolver.CreateRequestCurrentTask(request, collectionId);
    }

    private SectionPackingResult AddSection(
        ICollection<ContextPackageSection> sections,
        ISet<string> packageSourceRefs,
        string name,
        int priority,
        string content,
        ContextContentFormat contentFormat,
        IReadOnlyList<string> sectionSourceRefs,
        IReadOnlyList<string> sectionItemRefs,
        IReadOnlyList<string> candidateIds,
        int tokenBudget,
        int sectionTokenBudget,
        TokenEstimationContext tokenContext,
        ref int estimatedTokens)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return SectionPackingResult.Dropped("content is empty");
        }

        var remainingBudget = tokenBudget - estimatedTokens;
        if (remainingBudget <= 0)
        {
            return SectionPackingResult.Dropped("token budget exhausted");
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
                return SectionPackingResult.Dropped("token budget exhausted");
            }

            sectionTokens = EstimatePackageTokens(sectionContent, tokenContext);
            if (sectionTokens > remainingBudget)
            {
                return SectionPackingResult.Dropped("token budget exhausted");
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

        // 精确候选接受/拒绝判定：
        // - Section 被加入 package 时，所有候选均标记为 accepted。
        //   Truncated 标志指示内容是否因 token 预算被裁剪。
        //   裁剪时的精确归属由 AddSectionDecisionsWithDedup 根据 Truncated 标志处理
        //   （仅保留首个新候选，避免低价值候选取代 MustHit 项）。
        // - Section 未被加入时，所有候选均标记为 rejected。
        // 这取代了旧的字符串前缀猜测（7.2），并提供精确的候选 ID 列表（6.2）。
        var validCandidateIds = candidateIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        IReadOnlyList<string> acceptedIds = validCandidateIds;
        IReadOnlyList<string> rejectedIds = Array.Empty<string>();

        return SectionPackingResult.Selected(
            truncated ? "selected and truncated to fit token budget" : "selected for package section",
            sectionTokens,
            truncated,
            acceptedIds,
            rejectedIds);
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

    private static ContextPackageBuildResult CreateBuildResult(
        ContextPackageRequest request,
        ContextPackage package,
        int tokenBudget,
        IReadOnlyList<ContextPackageDecision> selectedItems,
        IReadOnlyList<DroppedContextItem> droppedItems,
        IReadOnlyList<ContextPackageUncertainty>? uncertainties = null,
        RetrievalPlan? plan = null,
        PackageTraceRecorder? traceRecorder = null,
        IReadOnlyList<ContextPackageItemReference>? itemReferences = null)
    {
        var metadata = new Dictionary<string, string>(package.Metadata);
        if (!string.IsNullOrWhiteSpace(request.Policy?.Id))
        {
            metadata["policyId"] = request.Policy.Id;
        }

        var packageModeName = request.Policy is null
            ? PackagePolicyResolver.ReadFirstSetting(request, new ContextPackagePolicy(), "mode", "packageMode", "contextMode", "taskMode") ?? string.Empty
            : PackagePolicyResolver.ResolvePackageModeName(request, request.Policy, PackagePolicyResolver.ResolveModeBudgetProfile(request, request.Policy));
        var packageMustHitIds = PackagePolicyResolver.ResolvePackageMustHitIds(request);
        var sortedSelected = selectedItems
            .OrderByDescending(item => PackageUncertaintyBuilder.ResolvePackageOrderScore(item, packageModeName, packageMustHitIds))
            .ThenByDescending(item => item.Score)
            .ThenBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var resolvedUncertainties = uncertainties ?? PackageUncertaintyBuilder.BuildUncertainties(
            package.Sections,
            sortedSelected,
            droppedItems,
            Array.Empty<ContextRelation>(),
            tokenBudget,
            package.EstimatedTokens);
        var budget = PackageBudgetProjector.BuildBudgetReport(package, tokenBudget, request);
        var output = PackageBudgetProjector.BuildStandardOutput(package, droppedItems, resolvedUncertainties, budget);
        PackageMetadataBuilder.AddDiagnosticMetadata(metadata, tokenBudget, package.EstimatedTokens, droppedItems.Count, resolvedUncertainties.Count);
        if (traceRecorder is not null)
        {
            PackageMetadataBuilder.AddTraceHealthMetadata(metadata, traceRecorder);
        }

        return new ContextPackageBuildResult
        {
            BuildId = package.PackageId,
            Package = package,
            SelectedItems = sortedSelected,
            ItemReferences = itemReferences ?? Array.Empty<ContextPackageItemReference>(),
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
}

internal sealed record TokenEstimationContext(string? ModelName, string Source, bool IsFallback);

internal sealed class MergedContextConstraint
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

internal sealed class ContextEvidenceEntry
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
