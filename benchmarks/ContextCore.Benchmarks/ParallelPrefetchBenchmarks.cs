using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Benchmarks;

// 并行预取独立基准：验证 BasicContextPackageBuilder 的 6 路 Task.WhenAll 是否真正降低延迟。
// InMemory store 无 I/O 延迟，并行无优势；本基准用延迟装饰器模拟真实存储（FS/Postgres）的 per-query 延迟，
// 对比"无延迟（InMemory 直接）"与"有延迟（每 query 1ms）"下并行预取的效果。
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class ParallelPrefetchBenchmarks
{
    private const string WorkspaceId = "bench-ws";
    private const string CollectionId = "bench-col";

    private BasicContextPackageBuilder _inMemoryBuilder = null!;
    private BasicContextPackageBuilder _delayedBuilder = null!;
    private ContextPackageRequest _request = null!;

    [Params(50)]
    public int ItemCount { get; set; }

    // 模拟每个 store query 的 I/O 延迟（毫秒）。1ms 近似 FS；5ms 近似 Postgres 网络。
    [Params(0, 1, 5)]
    public int QueryDelayMs { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // 无延迟：纯 InMemory，并行预取无优势（CPU 绑定，并行反而有线程调度开销）
        _inMemoryBuilder = BuildBuilder(useDelay: false);

        // 有延迟：用 DelayedStore 装饰，每个 query 延迟 QueryDelayMs
        _delayedBuilder = BuildBuilder(useDelay: true);

        var policy = new ContextPackagePolicy
        {
            Id = "bench-policy-all",
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Name = "BenchAllSections",
            Mode = ContextPackageMode.None,
            TokenBudget = 4000,
            IncludeRecentRawContext = true,
            IncludeHardConstraints = true,
            IncludeSoftConstraints = true,
            IncludeWorkingMemory = true,
            IncludeStableMemory = true,
            IncludeGlobalContext = true,
            MaxRecentItems = 20
        };

        _request = new ContextPackageRequest
        {
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            QueryText = "并行预取基准测试 parallel prefetch benchmark",
            RequiredTags = ["task"],
            TokenBudget = 4000,
            Mode = ContextPackageMode.None,
            Policy = policy
        };
    }

    private BasicContextPackageBuilder BuildBuilder(bool useDelay)
    {
        // 先用 InMemory store 装填数据
        var rawContextStore = new InMemoryContextStore();
        var rawMemoryStore = new InMemoryMemoryStore();
        var rawConstraintStore = new InMemoryConstraintStore();
        var rawGlobalStore = new InMemoryGlobalContextStore();
        var rawRelationStore = new InMemoryRelationStore();

        PopulateStores(rawContextStore, rawMemoryStore, rawConstraintStore, rawGlobalStore);

        // 无延迟直接用原 store；有延迟用装饰器包装
        IContextStore contextStore = useDelay ? new DelayedContextStore(rawContextStore, QueryDelayMs) : rawContextStore;
        IMemoryStore memoryStore = useDelay ? new DelayedMemoryStore(rawMemoryStore, QueryDelayMs) : rawMemoryStore;
        IConstraintStore constraintStore = useDelay ? new DelayedConstraintStore(rawConstraintStore, QueryDelayMs) : rawConstraintStore;
        IGlobalContextStore globalStore = useDelay ? new DelayedGlobalContextStore(rawGlobalStore, QueryDelayMs) : rawGlobalStore;
        IRelationStore relationStore = rawRelationStore; // relation 不在预取路径

        return new BasicContextPackageBuilder(
            contextStore,
            constraintStore,
            globalStore,
            memoryStore,
            relationStore,
            workingMemoryService: rawMemoryStore);
    }

    private static void PopulateStores(
        InMemoryContextStore contextStore,
        InMemoryMemoryStore memoryStore,
        InMemoryConstraintStore constraintStore,
        InMemoryGlobalContextStore globalStore)
    {
        var now = DateTimeOffset.UtcNow;
        var rand = new Random(20260715);

        for (int i = 0; i < 50; i++)
        {
            var createdAt = now.AddDays(-rand.Next(0, 90));
            contextStore.SaveAsync(new ContextItem
            {
                Id = $"ctx-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Type = "note",
                Title = $"条目 {i}",
                Content = $"内容 {i} " + new string('x', 200 + rand.Next(0, 400)),
                ContentFormat = ContextContentFormat.Markdown,
                Tags = ["task", "package"],
                Importance = 0.3 + rand.NextDouble() * 0.7,
                CreatedAt = createdAt,
                UpdatedAt = createdAt
            }).GetAwaiter().GetResult();
        }

        for (int i = 0; i < 10; i++)
        {
            memoryStore.SaveAsync(new ContextMemoryItem
            {
                Id = $"mem-stable-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Layer = ContextMemoryLayer.Stable,
                Status = ContextMemoryStatus.Stable,
                Type = "fact",
                Content = $"稳定记忆 #{i}",
                ContentFormat = ContextContentFormat.Markdown,
                Tags = ["task"],
                Importance = 0.7,
                CreatedAt = now,
                UpdatedAt = now
            }).GetAwaiter().GetResult();

            memoryStore.SaveAsync(new ContextMemoryItem
            {
                Id = $"mem-working-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Layer = ContextMemoryLayer.Working,
                Status = ContextMemoryStatus.Active,
                Type = "note",
                Content = $"工作记忆 #{i}",
                ContentFormat = ContextContentFormat.Markdown,
                Tags = ["task"],
                Importance = 0.8,
                CreatedAt = now,
                UpdatedAt = now
            }).GetAwaiter().GetResult();
        }

        for (int i = 0; i < 10; i++)
        {
            constraintStore.SaveAsync(new ContextConstraint
            {
                Id = $"con-hard-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Scope = ContextScope.Collection,
                Level = ConstraintLevel.Hard,
                Content = $"硬约束 #{i}",
                Status = ContextMemoryStatus.Active,
                Confidence = 0.9,
                CreatedAt = now,
                UpdatedAt = now
            }).GetAwaiter().GetResult();

            constraintStore.SaveAsync(new ContextConstraint
            {
                Id = $"con-soft-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Scope = ContextScope.Collection,
                Level = ConstraintLevel.Soft,
                Content = $"软约束 #{i}",
                Status = ContextMemoryStatus.Active,
                Confidence = 0.7,
                CreatedAt = now,
                UpdatedAt = now
            }).GetAwaiter().GetResult();
        }

        for (int i = 0; i < 5; i++)
        {
            globalStore.SaveAsync(new ContextGlobalItem
            {
                Id = $"global-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = null,
                Scope = ContextScope.Workspace,
                Type = "preference",
                Content = $"全局上下文 #{i}",
                ContentFormat = ContextContentFormat.Markdown,
                Tags = ["task"],
                Importance = 0.6,
                CreatedAt = now,
                UpdatedAt = now
            }).GetAwaiter().GetResult();
        }
    }

    // 无延迟基准：InMemory 直接，并行预取无 I/O 优势
    [Benchmark(Baseline = true)]
    public async Task NoDelay_ParallelPrefetch()
    {
        var result = await _inMemoryBuilder.BuildDetailedAsync(_request, CancellationToken.None);
        _ = result.Package.Sections.Count;
    }

    // 有延迟基准：每 query 延迟 QueryDelayMs，6 路并行应比串行快 ~6×
    [Benchmark]
    public async Task WithDelay_ParallelPrefetch()
    {
        var result = await _delayedBuilder.BuildDetailedAsync(_request, CancellationToken.None);
        _ = result.Package.Sections.Count;
    }

    // 并发场景：4 路并发构建，每路内部 6 路并行预取，验证高并发下是否放大存储压力
    [Benchmark]
    public async Task WithDelay_Concurrent4_ParallelPrefetch()
    {
        var tasks = new Task[4];
        for (int i = 0; i < 4; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                var result = await _delayedBuilder.BuildDetailedAsync(_request, CancellationToken.None);
                _ = result.Package.Sections.Count;
            });
        }
        await Task.WhenAll(tasks);
    }
}

// 延迟装饰器：每个 query 方法延迟 delayMs 毫秒，模拟真实存储 I/O。
// Get/Save 不延迟（只在 query 路径产生 I/O 延迟，与预取路径一致）。

file sealed class DelayedContextStore : IContextStore
{
    private readonly IContextStore _inner;
    private readonly int _delayMs;
    public DelayedContextStore(IContextStore inner, int delayMs) { _inner = inner; _delayMs = delayMs; }

    public Task SaveAsync(ContextItem item, CancellationToken ct = default) => _inner.SaveAsync(item, ct);
    public Task<ContextItem?> GetAsync(string ws, string col, string id, CancellationToken ct = default) => _inner.GetAsync(ws, col, id, ct);
    public async Task<IReadOnlyList<ContextItem>> QueryAsync(ContextQuery query, CancellationToken ct = default)
    {
        if (_delayMs > 0) await Task.Run(() => Thread.Sleep(_delayMs), ct).ConfigureAwait(false);
        return await _inner.QueryAsync(query, ct).ConfigureAwait(false);
    }
    public Task DeleteAsync(string ws, string col, string id, CancellationToken ct = default) => _inner.DeleteAsync(ws, col, id, ct);
}

file sealed class DelayedMemoryStore : IMemoryStore
{
    private readonly IMemoryStore _inner;
    private readonly int _delayMs;
    public DelayedMemoryStore(IMemoryStore inner, int delayMs) { _inner = inner; _delayMs = delayMs; }

    public Task SaveAsync(ContextMemoryItem item, CancellationToken ct = default) => _inner.SaveAsync(item, ct);
    public Task<ContextMemoryItem?> GetAsync(string ws, string col, string id, CancellationToken ct = default) => _inner.GetAsync(ws, col, id, ct);
    public async Task<IReadOnlyList<ContextMemoryItem>> QueryAsync(ContextMemoryQuery query, CancellationToken ct = default)
    {
        if (_delayMs > 0) await Task.Run(() => Thread.Sleep(_delayMs), ct).ConfigureAwait(false);
        return await _inner.QueryAsync(query, ct).ConfigureAwait(false);
    }
    public Task UpdateStatusAsync(string ws, string col, string id, ContextMemoryStatus status, CancellationToken ct = default) => _inner.UpdateStatusAsync(ws, col, id, status, ct);
}

file sealed class DelayedConstraintStore : IConstraintStore
{
    private readonly IConstraintStore _inner;
    private readonly int _delayMs;
    public DelayedConstraintStore(IConstraintStore inner, int delayMs) { _inner = inner; _delayMs = delayMs; }

    public Task SaveAsync(ContextConstraint item, CancellationToken ct = default) => _inner.SaveAsync(item, ct);
    public Task<ContextConstraint?> GetAsync(string constraintId, CancellationToken ct = default) => _inner.GetAsync(constraintId, ct);
    public async Task<IReadOnlyList<ContextConstraint>> QueryAsync(ContextConstraintQuery query, CancellationToken ct = default)
    {
        if (_delayMs > 0) await Task.Run(() => Thread.Sleep(_delayMs), ct).ConfigureAwait(false);
        return await _inner.QueryAsync(query, ct).ConfigureAwait(false);
    }
}

file sealed class DelayedGlobalContextStore : IGlobalContextStore
{
    private readonly IGlobalContextStore _inner;
    private readonly int _delayMs;
    public DelayedGlobalContextStore(IGlobalContextStore inner, int delayMs) { _inner = inner; _delayMs = delayMs; }

    public Task SaveAsync(ContextGlobalItem item, CancellationToken ct = default) => _inner.SaveAsync(item, ct);
    public async Task<IReadOnlyList<ContextGlobalItem>> QueryAsync(ContextGlobalQuery query, CancellationToken ct = default)
    {
        if (_delayMs > 0) await Task.Run(() => Thread.Sleep(_delayMs), ct).ConfigureAwait(false);
        return await _inner.QueryAsync(query, ct).ConfigureAwait(false);
    }
}
