using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 包构建输入加载阶段：并行预取 6 个数据源（recent/hard/working/global/stable/soft）、
/// 当前任务（current_task）以及合并约束（merged constraints），返回 <see cref="PackageInputs"/>。
/// 从 <see cref="BasicContextPackageBuilder.BuildWithPolicyAsync"/> 提取，保证字节级确定性输出不变。
/// </summary>
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
    /// 顺序保持与原实现一致：先解析 current_task，再并行预取 6 源，最后解析 merged constraints。
    /// </summary>
    internal async Task<PackageInputs> LoadAsync(
        ResolvedPackageOptions options,
        CancellationToken cancellationToken)
    {
        var request = options.Request;
        var workspaceId = options.WorkspaceId;
        var collectionId = options.CollectionId;
        var maxRecentItems = options.MaxRecentItems;

        // current_task 解析（与原实现顺序一致：先于 6 源预取）。
        WorkingMemoryCurrentTask? currentTask = null;
        if (options.IncludeCurrentTaskSection)
        {
            currentTask = await ResolveCurrentTaskAsync(
                request,
                collectionId ?? string.Empty,
                cancellationToken).ConfigureAwait(false);
        }

        // P1 性能：recent/hard/working/global/stable/soft 六个独立数据源查询仅依赖
        // workspaceId/collectionId/policy，彼此无依赖。先并行预取原始结果，再按原顺序
        // 处理（filter/anchors/section assembly 仍串行），保证字节级确定性输出不变。
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

        Task<IReadOnlyList<ContextConstraint>>? hardConstraintsTask =
            ((options.IncludeHardConstraints || !options.IncludeMergedConstraintsSection) && _constraintStore is not null)
            ? _constraintStore.QueryAsync(new ContextConstraintQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    Level = ConstraintLevel.Hard,
                    Take = 100
                }, cancellationToken)
            : null;

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

        Task<IReadOnlyList<ContextGlobalItem>>? globalItemsTask =
            (options.IncludeGlobalContext && _globalContextStore is not null)
            ? _globalContextStore.QueryAsync(new ContextGlobalQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    Take = maxRecentItems
                }, cancellationToken)
            : null;

        // Hard 与 Soft 各自独立查询，保证独立的 Take 预算与 store 层 Level 过滤。
        // 理由同上：全局 Take 后分区会让某一级独占预算，改变 selected set（P0 4.2）。
        Task<IReadOnlyList<ContextConstraint>>? softConstraintsTask =
            (options.IncludeSoftConstraints && _constraintStore is not null)
            ? _constraintStore.QueryAsync(new ContextConstraintQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    Level = ConstraintLevel.Soft,
                    Take = 100
                }, cancellationToken)
            : null;

        var prefetchTasks = new List<Task>();
        if (recentItemsTask is not null) prefetchTasks.Add(recentItemsTask);
        if (hardConstraintsTask is not null) prefetchTasks.Add(hardConstraintsTask);
        if (workingCandidatesRawTask is not null) prefetchTasks.Add(workingCandidatesRawTask);
        if (globalItemsTask is not null) prefetchTasks.Add(globalItemsTask);
        if (stableCandidatesRawTask is not null) prefetchTasks.Add(stableCandidatesRawTask);
        if (softConstraintsTask is not null) prefetchTasks.Add(softConstraintsTask);
        if (prefetchTasks.Count > 0)
        {
            await Task.WhenAll(prefetchTasks).ConfigureAwait(false);
        }

        // merged constraints 解析（仅在对应 section 启用时）。
        IReadOnlyList<ContextConstraint>? mergedConstraints = null;
        if (options.IncludeMergedConstraintsSection)
        {
            mergedConstraints = await ResolveMergedConstraintsAsync(
                options,
                collectionId ?? string.Empty,
                cancellationToken).ConfigureAwait(false);
        }

        // 每层/级已独立查询，store 层已应用 Layer/Level/Status 过滤与各自 Take，直接 await 即可。
        var workingMemory = workingCandidatesRawTask is null ? null : await workingCandidatesRawTask.ConfigureAwait(false);
        var stableMemory = stableCandidatesRawTask is null ? null : await stableCandidatesRawTask.ConfigureAwait(false);
        var hardConstraints = hardConstraintsTask is null ? null : await hardConstraintsTask.ConfigureAwait(false);
        var softConstraints = softConstraintsTask is null ? null : await softConstraintsTask.ConfigureAwait(false);

        return new PackageInputs(
            RecentItems: recentItemsTask is null ? null : await recentItemsTask.ConfigureAwait(false),
            HardConstraints: hardConstraints,
            WorkingCandidatesRaw: workingMemory,
            GlobalItems: globalItemsTask is null ? null : await globalItemsTask.ConfigureAwait(false),
            StableCandidatesRaw: stableMemory,
            SoftConstraints: softConstraints,
            CurrentTask: currentTask,
            MergedConstraints: mergedConstraints);
    }

    private async Task<IReadOnlyList<ContextConstraint>> ResolveMergedConstraintsAsync(
        ResolvedPackageOptions options,
        string collectionId,
        CancellationToken cancellationToken)
    {
        if (_constraintStore is null)
        {
            return RequestTaskResolver.CreateRequestConstraints(options.Request, collectionId);
        }

        var take = options.ConstraintMergeMaxItems;

        // R12.4A #2：Per-level quota — Hard 与 Soft 各自独立查询，防止某一级独占共享 Take 预算。
        // 旧实现用 Level=null + Take=N 单一查询：若 store 有 N 个 Hard + 少量 Soft，
        // Hard 会填满 Take 预算导致 Soft 完全缺席 merged section。
        // 新实现并行查询 Hard + Soft + All（Level=null），各自拥有独立 Take 预算：
        //   - Hard 查询确保 Hard 约束被召回（至多 take 条）
        //   - Soft 查询确保 Soft 约束被召回（至多 take 条）
        //   - All 查询捕获 Runtime/System/User/Domain 等次要级别
        // 合并后按 ID 去重（Hard/Soft 优先加入，All 查询的重复项被跳过）。
        // 三路查询并行执行，延迟与原单查询相当。
        var hardTask = _constraintStore.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = options.WorkspaceId,
            CollectionId = collectionId,
            Level = ConstraintLevel.Hard,
            Take = take
        }, cancellationToken);

        var softTask = _constraintStore.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = options.WorkspaceId,
            CollectionId = collectionId,
            Level = ConstraintLevel.Soft,
            Take = take
        }, cancellationToken);

        var allLevelsTask = _constraintStore.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = options.WorkspaceId,
            CollectionId = collectionId,
            Take = take
        }, cancellationToken);

        await Task.WhenAll(hardTask, softTask, allLevelsTask).ConfigureAwait(false);

        // 合并顺序：Hard → Soft → All（次要级别）。OrderMergedConstraints 后续按 PriorityRank
        // 重排，此处顺序仅影响同分 tie-break 的 Index。
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var constraints = new List<ContextConstraint>();
        foreach (var item in hardTask.Result.Concat(softTask.Result).Concat(allLevelsTask.Result))
        {
            if (seenIds.Add(item.Id))
            {
                constraints.Add(item);
            }
        }

        constraints.AddRange(RequestTaskResolver.CreateRequestConstraints(options.Request, collectionId));
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
}

/// <summary>
/// 加载阶段产出：所有已预取的 store 数据 + current task + merged constraints。
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
    IReadOnlyList<ContextConstraint>? MergedConstraints);
