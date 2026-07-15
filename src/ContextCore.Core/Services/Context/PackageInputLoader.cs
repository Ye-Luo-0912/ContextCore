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
    /// 加载阶段：按 policy/request 决定需要预取的数据源，并行查询后返回 <see cref="PackageInputs"/>。
    /// 顺序保持与原实现一致：先解析 current_task，再并行预取 6 源，最后解析 merged constraints。
    /// </summary>
    internal async Task<PackageInputs> LoadAsync(
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        CancellationToken cancellationToken)
    {
        var workspaceId = PackagePolicyResolver.NormalizeRequiredValue(request.WorkspaceId);
        var collectionId = PackagePolicyResolver.NormalizeRequiredValue(policy.CollectionId, request.CollectionId);
        var maxRecentItems = policy.MaxRecentItems > 0 ? policy.MaxRecentItems : 20;

        // current_task 解析（与原实现顺序一致：先于 6 源预取）。
        WorkingMemoryCurrentTask? currentTask = null;
        if (PackagePolicyResolver.ShouldIncludeCurrentTaskSection(request, policy))
        {
            currentTask = await ResolveCurrentTaskAsync(
                request,
                collectionId ?? string.Empty,
                cancellationToken).ConfigureAwait(false);
        }

        // P1 性能：recent/hard/working/global/stable/soft 六个独立数据源查询仅依赖
        // workspaceId/collectionId/policy，彼此无依赖。先并行预取原始结果，再按原顺序
        // 处理（filter/anchors/section assembly 仍串行），保证字节级确定性输出不变。
        Task<IReadOnlyList<ContextItem>>? recentItemsTask = policy.IncludeRecentRawContext
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
            ((policy.IncludeHardConstraints || !PackagePolicyResolver.ShouldIncludeMergedConstraintsSection(request, policy)) && _constraintStore is not null)
            ? _constraintStore.QueryAsync(new ContextConstraintQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    Level = ConstraintLevel.Hard,
                    Take = 100
                }, cancellationToken)
            : null;

        Task<IReadOnlyList<ContextMemoryItem>>? workingCandidatesRawTask =
            (policy.IncludeWorkingMemory && _memoryStore is not null)
            ? _memoryStore.QueryAsync(new ContextMemoryQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    Layer = ContextMemoryLayer.Working,
                    Take = Math.Min(Math.Max(maxRecentItems * 3, 20), 60)
                }, cancellationToken)
            : null;

        Task<IReadOnlyList<ContextGlobalItem>>? globalItemsTask =
            (policy.IncludeGlobalContext && _globalContextStore is not null)
            ? _globalContextStore.QueryAsync(new ContextGlobalQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    Take = maxRecentItems
                }, cancellationToken)
            : null;

        Task<IReadOnlyList<ContextMemoryItem>>? stableCandidatesRawTask =
            (policy.IncludeStableMemory && _memoryStore is not null)
            ? _memoryStore.QueryAsync(new ContextMemoryQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    Layer = ContextMemoryLayer.Stable,
                    Status = ContextMemoryStatus.Stable,
                    Take = Math.Min(Math.Max(maxRecentItems * 3, 20), 60)
                }, cancellationToken)
            : null;

        Task<IReadOnlyList<ContextConstraint>>? softConstraintsTask =
            (policy.IncludeSoftConstraints && _constraintStore is not null)
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
        if (PackagePolicyResolver.ShouldIncludeMergedConstraintsSection(request, policy))
        {
            mergedConstraints = await ResolveMergedConstraintsAsync(
                request,
                policy,
                collectionId ?? string.Empty,
                cancellationToken).ConfigureAwait(false);
        }

        return new PackageInputs(
            RecentItems: recentItemsTask is null ? null : await recentItemsTask.ConfigureAwait(false),
            HardConstraints: hardConstraintsTask is null ? null : await hardConstraintsTask.ConfigureAwait(false),
            WorkingCandidatesRaw: workingCandidatesRawTask is null ? null : await workingCandidatesRawTask.ConfigureAwait(false),
            GlobalItems: globalItemsTask is null ? null : await globalItemsTask.ConfigureAwait(false),
            StableCandidatesRaw: stableCandidatesRawTask is null ? null : await stableCandidatesRawTask.ConfigureAwait(false),
            SoftConstraints: softConstraintsTask is null ? null : await softConstraintsTask.ConfigureAwait(false),
            CurrentTask: currentTask,
            MergedConstraints: mergedConstraints);
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
