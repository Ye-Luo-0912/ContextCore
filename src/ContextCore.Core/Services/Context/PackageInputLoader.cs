using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 包构建输入加载阶段：并行预取所有独立数据源（recent/hard/working/global/stable/soft/current_task/merged constraints），
/// 返回 <see cref="PackageInputs"/>。从 <see cref="BasicContextPackageBuilder.BuildWithPolicyAsync"/> 提取，
/// 保证字节级确定性输出不变。
/// </summary>
/// <remarks>
/// 优化：
/// <list type="bullet">
/// <item><b>R13.2 #1 merged constraint 去重</b>：当 hard_constraints/soft_constraints section 与
/// merged constraints section 同时启用时，Hard/Soft 查询只发一次（Take = max(100, ConstraintMergeMaxItems)），
/// 结果按需切片复用给两个 section。store 排序为 Level→Confidence→UpdatedAt 的稳定序，
/// 故 Take 大值查询的前 N 条等于 Take=N 查询的结果，去重不改变 selected set。</item>
/// <item><b>R13.2 #3 current_task 并行化</b>：current_task 解析仅依赖 request + WorkingMemoryService，
/// 与 6 源预取无依赖，并入 prefetchTasks 集合一次 Task.WhenAll，消除串行 I/O 延迟。</item>
/// <item><b>R13.2 #4 Store call count</b>：<see cref="PackageReadPlanBuilder"/> 记录每次 store 调用，
/// 去重命中计数 DedupHits，最终通过 <see cref="PackageInputs.ReadPlan"/> 暴露到结果。</item>
/// </list>
/// </remarks>
internal sealed class PackageInputLoader
{
    private readonly IContextStore _store;
    private readonly IConstraintStore? _constraintStore;
    private readonly IGlobalContextStore? _globalContextStore;
    private readonly IMemoryStore? _memoryStore;
    private readonly IWorkingMemoryService? _workingMemoryService;

    internal PackageInputLoader(
        IContextStore store,
        IConstraintStore? constraintStore,
        IGlobalContextStore? globalContextStore,
        IMemoryStore? memoryStore,
        IWorkingMemoryService? workingMemoryService)
    {
        _store = store;
        _constraintStore = constraintStore;
        _globalContextStore = globalContextStore;
        _memoryStore = memoryStore;
        _workingMemoryService = workingMemoryService;
    }

    /// <summary>
    /// 加载阶段：按 ResolvedPackageOptions 决定需要预取的数据源，并行查询后返回 <see cref="PackageInputs"/>。
    /// #3：current_task 与 6 源预取 + merged constraints 并行执行（无依赖时）。
    /// Hard/Soft 在 section 与 merged 之间去重，单次查询结果按 Take 切片复用。
    /// </summary>
    internal async Task<PackageInputs> LoadAsync(
        ResolvedPackageOptions options,
        CancellationToken cancellationToken)
    {
        var request = options.Request;
        var workspaceId = options.WorkspaceId;
        var collectionId = options.CollectionId;
        var maxRecentItems = options.MaxRecentItems;
        var planBuilder = new PackageReadPlanBuilder();

        // 决定 Hard/Soft 查询的需求来源与 Take 预算。
        // - hardForSection: hard_constraints section 需要 Hard（IncludeHardConstraints || merged 关闭时默认查 Hard）
        // - hardForMerged: merged section 需要 Hard 分区（仅 merged 启用时）
        // 去重：两者皆需时只发一次，Take = max(100, ConstraintMergeMaxItems)，结果切片复用。
        var hardForSection = (options.IncludeHardConstraints || !options.IncludeMergedConstraintsSection);
        var hardForMerged = options.IncludeMergedConstraintsSection;
        var softForSection = options.IncludeSoftConstraints;
        var softForMerged = options.IncludeMergedConstraintsSection;
        var mergedNeedsAll = options.IncludeMergedConstraintsSection; // All-level 查询（Runtime/System/User/Domain 等）

        // Take 选择：两者皆需取 max；仅 section 需取 100；仅 merged 需取 ConstraintMergeMaxItems。
        var hardTake = hardForSection && hardForMerged
            ? Math.Max(100, options.ConstraintMergeMaxItems)
            : hardForSection
                ? 100
                : options.ConstraintMergeMaxItems;
        var softTake = softForSection && softForMerged
            ? Math.Max(100, options.ConstraintMergeMaxItems)
            : softForSection
                ? 100
                : options.ConstraintMergeMaxItems;
        var hardQueryNeeded = (hardForSection || hardForMerged) && _constraintStore is not null;
        var softQueryNeeded = (softForSection || softForMerged) && _constraintStore is not null;

        // 跟踪去重命中：section 与 merged 同时需要 Hard/Soft 时，跳过了第二次查询。
        if (hardForSection && hardForMerged)
        {
            planBuilder.RecordDedupHit();
        }
        if (softForSection && softForMerged)
        {
            planBuilder.RecordDedupHit();
        }

        // ── current_task（R13.2 #3：与 6 源预取并行）─────────────────────────
        Task<WorkingMemoryCurrentTask?>? currentTaskTask = options.IncludeCurrentTaskSection
            ? ResolveCurrentTaskAsync(request, collectionId ?? string.Empty, planBuilder, cancellationToken)
            : null;

        // ── 6 源并行预取 ───────────────────────────────────────────────────────
        Task<IReadOnlyList<ContextItem>>? recentItemsTask = options.IncludeRecentRawContext
            ? _store.QueryAsync(new ContextQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    QueryText = request.QueryText,
                    Tags = request.RequiredTags,
                    Types = request.RequiredTypes,
                    Take = Math.Min(Math.Max(maxRecentItems * 3, maxRecentItems), 60),
                    IncludeContent = true
                }, cancellationToken)
            : null;
        if (recentItemsTask is not null)
        {
            planBuilder.RecordCall("ContextStore.Query");
        }

        // Hard 单次查询，Take = max(100, ConstraintMergeMaxItems)，section 与 merged 共享结果。
        Task<IReadOnlyList<ContextConstraint>>? hardConstraintsTask = hardQueryNeeded
            ? _constraintStore!.QueryAsync(new ContextConstraintQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    Level = ConstraintLevel.Hard,
                    Take = hardTake
                }, cancellationToken)
            : null;
        if (hardConstraintsTask is not null)
        {
            planBuilder.RecordCall("ConstraintStore.Query(Hard)");
        }

        // 每层独立查询：Working 与 Stable 各自拥有独立的 Take 预算与 store 层过滤。
        // 不使用"全局 Take 后分区"——那会让多数层占满共享预算，少数层可能拿到 0 候选，
        // 从而改变 Package selected set（参见 P0 修复 4.2）。
        // FileMemoryStore 内部 SemaphoreSlim 串行化两次查询的代价可接受；若需进一步优化，
        // 应由 provider 内部复用同一文件快照，而非在 loader 层做全局 Take。
        Task<IReadOnlyList<ContextMemoryItem>>? workingCandidatesRawTask =
            (options.IncludeWorkingMemory && _memoryStore is not null)
            ? _memoryStore.QueryAsync(new ContextMemoryQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    Layer = ContextMemoryLayer.Working,
                    Take = Math.Min(Math.Max(maxRecentItems * 3, 20), 60)
                }, cancellationToken)
            : null;
        if (workingCandidatesRawTask is not null)
        {
            planBuilder.RecordCall("MemoryStore.Query(Working)");
        }

        Task<IReadOnlyList<ContextMemoryItem>>? stableCandidatesRawTask =
            (options.IncludeStableMemory && _memoryStore is not null)
            ? _memoryStore.QueryAsync(new ContextMemoryQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    Layer = ContextMemoryLayer.Stable,
                    Status = ContextMemoryStatus.Stable,
                    Take = Math.Min(Math.Max(maxRecentItems * 3, 20), 60)
                }, cancellationToken)
            : null;
        if (stableCandidatesRawTask is not null)
        {
            planBuilder.RecordCall("MemoryStore.Query(Stable)");
        }

        Task<IReadOnlyList<ContextGlobalItem>>? globalItemsTask =
            (options.IncludeGlobalContext && _globalContextStore is not null)
            ? _globalContextStore.QueryAsync(new ContextGlobalQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    Take = maxRecentItems
                }, cancellationToken)
            : null;
        if (globalItemsTask is not null)
        {
            planBuilder.RecordCall("GlobalContextStore.Query");
        }

        // Soft 单次查询，section 与 merged 共享结果。
        Task<IReadOnlyList<ContextConstraint>>? softConstraintsTask = softQueryNeeded
            ? _constraintStore!.QueryAsync(new ContextConstraintQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    Level = ConstraintLevel.Soft,
                    Take = softTake
                }, cancellationToken)
            : null;
        if (softConstraintsTask is not null)
        {
            planBuilder.RecordCall("ConstraintStore.Query(Soft)");
        }

        // merged All-level 查询（Runtime/System/User/Domain 等次要级别）。
        // 此查询独立于 Hard/Soft，捕获它们覆盖不到的级别，无法与 section 共享。
        Task<IReadOnlyList<ContextConstraint>>? allLevelsTask =
            (mergedNeedsAll && _constraintStore is not null)
            ? _constraintStore.QueryAsync(new ContextConstraintQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    Take = options.ConstraintMergeMaxItems
                }, cancellationToken)
            : null;
        if (allLevelsTask is not null)
        {
            planBuilder.RecordCall("ConstraintStore.Query(All)");
        }

        // #3：current_task + 6 源 + merged All 一起并行（彼此无依赖）。
        var prefetchTasks = new List<Task>();
        if (currentTaskTask is not null) prefetchTasks.Add(currentTaskTask);
        if (recentItemsTask is not null) prefetchTasks.Add(recentItemsTask);
        if (hardConstraintsTask is not null) prefetchTasks.Add(hardConstraintsTask);
        if (workingCandidatesRawTask is not null) prefetchTasks.Add(workingCandidatesRawTask);
        if (globalItemsTask is not null) prefetchTasks.Add(globalItemsTask);
        if (stableCandidatesRawTask is not null) prefetchTasks.Add(stableCandidatesRawTask);
        if (softConstraintsTask is not null) prefetchTasks.Add(softConstraintsTask);
        if (allLevelsTask is not null) prefetchTasks.Add(allLevelsTask);
        if (prefetchTasks.Count > 0)
        {
            await Task.WhenAll(prefetchTasks).ConfigureAwait(false);
        }

        // 每层/级已独立查询，store 层已应用 Layer/Level/Status 过滤与各自 Take，直接 await 即可。
        var workingMemory = workingCandidatesRawTask is null ? null : await workingCandidatesRawTask.ConfigureAwait(false);
        var stableMemory = stableCandidatesRawTask is null ? null : await stableCandidatesRawTask.ConfigureAwait(false);

        // section 用 Take=100 切片；仅当 section 启用时赋值，否则保持 null（merged 独占结果）。
        // store 排序稳定（Hard-first, Confidence-desc, UpdatedAt-desc），切片结果与原独立查询完全一致。
        IReadOnlyList<ContextConstraint>? hardConstraints =
            (hardConstraintsTask is not null && hardForSection)
            ? SliceConstraints(hardConstraintsTask.Result, 100)
            : null;
        IReadOnlyList<ContextConstraint>? softConstraints =
            (softConstraintsTask is not null && softForSection)
            ? SliceConstraints(softConstraintsTask.Result, 100)
            : null;

        IReadOnlyList<ContextConstraint>? mergedConstraints = null;
        if (options.IncludeMergedConstraintsSection)
        {
            mergedConstraints = await ResolveMergedConstraintsAsync(
                options,
                collectionId ?? string.Empty,
                hardConstraintsTask?.Result,
                softConstraintsTask?.Result,
                allLevelsTask?.Result,
                cancellationToken).ConfigureAwait(false);
        }

        return new PackageInputs(
            RecentItems: recentItemsTask is null ? null : await recentItemsTask.ConfigureAwait(false),
            HardConstraints: hardConstraints,
            WorkingCandidatesRaw: workingMemory,
            GlobalItems: globalItemsTask is null ? null : await globalItemsTask.ConfigureAwait(false),
            StableCandidatesRaw: stableMemory,
            SoftConstraints: softConstraints,
            CurrentTask: currentTaskTask is null ? null : await currentTaskTask.ConfigureAwait(false),
            MergedConstraints: mergedConstraints,
            ReadPlan: planBuilder.Build());
    }

    /// <summary>
    /// 按 Take 切片约束列表。store 已按稳定序返回，前 N 条与 Take=N 独立查询结果一致。
    /// </summary>
    private static IReadOnlyList<ContextConstraint> SliceConstraints(
        IReadOnlyList<ContextConstraint> source,
        int take)
    {
        if (source.Count <= take)
        {
            return source;
        }
        var sliced = new ContextConstraint[take];
        for (var i = 0; i < take; i++)
        {
            sliced[i] = source[i];
        }
        return sliced;
    }

    /// <summary>
    /// merged constraints 合并。Hard/Soft 结果来自 section 共享查询（已去重），
    /// All-level 查询独立捕获次要级别。按 Hard → Soft → All 顺序合并去重后追加 request-derived constraints。
    /// </summary>
    private async Task<IReadOnlyList<ContextConstraint>> ResolveMergedConstraintsAsync(
        ResolvedPackageOptions options,
        string collectionId,
        IReadOnlyList<ContextConstraint>? hardResult,
        IReadOnlyList<ContextConstraint>? softResult,
        IReadOnlyList<ContextConstraint>? allLevelsResult,
        CancellationToken cancellationToken)
    {
        if (_constraintStore is null)
        {
            return RequestTaskResolver.CreateRequestConstraints(options.Request, collectionId);
        }

        // Hard/Soft 已由 LoadAsync 顶层查询得到，merged 复用切片结果（Take = ConstraintMergeMaxItems）。
        var mergedHard = hardResult is null
            ? Array.Empty<ContextConstraint>()
            : SliceConstraints(hardResult, options.ConstraintMergeMaxItems);
        var mergedSoft = softResult is null
            ? Array.Empty<ContextConstraint>()
            : SliceConstraints(softResult, options.ConstraintMergeMaxItems);
        var mergedAll = allLevelsResult ?? Array.Empty<ContextConstraint>();

        // 合并顺序：Hard → Soft → All（次要级别）。OrderMergedConstraints 后续按 PriorityRank
        // 重排，此处顺序仅影响同分 tie-break 的 Index。
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var constraints = new List<ContextConstraint>();
        foreach (var item in mergedHard.Concat(mergedSoft).Concat(mergedAll))
        {
            if (seenIds.Add(item.Id))
            {
                constraints.Add(item);
            }
        }

        constraints.AddRange(RequestTaskResolver.CreateRequestConstraints(options.Request, collectionId));
        await Task.CompletedTask.ConfigureAwait(false);
        return constraints;
    }

    private async Task<WorkingMemoryCurrentTask?> ResolveCurrentTaskAsync(
        ContextPackageRequest request,
        string collectionId,
        PackageReadPlanBuilder planBuilder,
        CancellationToken cancellationToken)
    {
        planBuilder.RecordCall("WorkingMemoryService.GetCurrentTask");
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
}

/// <summary>
/// #4：读路径调用计数器。记录每个 store kind + 用途的查询次数与去重命中。
/// </summary>
internal sealed class PackageReadPlanBuilder
{
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private int _dedupHits;

    public void RecordCall(string key)
    {
        _counts.TryGetValue(key, out var current);
        _counts[key] = current + 1;
    }

    public void RecordDedupHit()
    {
        _dedupHits++;
    }

    public PackageReadPlan Build()
    {
        return new PackageReadPlan
        {
            StoreCallCounts = _counts,
            DedupHits = _dedupHits
        };
    }
}

/// <summary>
/// 加载阶段产出：所有已预取的 store 数据 + current task + merged constraints + read plan。
/// 字段为 null 表示对应数据源未启用（policy 关闭或 store 未配置），与原实现中 null Task 语义一致。
/// </summary>
internal sealed record PackageInputs(
    IReadOnlyList<ContextItem>? RecentItems,
    IReadOnlyList<ContextConstraint>? HardConstraints,
    IReadOnlyList<ContextMemoryItem>? WorkingCandidatesRaw,
    IReadOnlyList<ContextGlobalItem>? GlobalItems,
    IReadOnlyList<ContextMemoryItem>? StableCandidatesRaw,
    IReadOnlyList<ContextConstraint>? SoftConstraints,
    WorkingMemoryCurrentTask? CurrentTask,
    IReadOnlyList<ContextConstraint>? MergedConstraints,
    PackageReadPlan ReadPlan);
