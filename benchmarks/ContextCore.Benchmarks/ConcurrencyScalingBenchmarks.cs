using BenchmarkDotNet.Attributes;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Benchmarks;

// 并发扩展基准：在相同条件下测量不同并发级别的串行 vs 并行构建吞吐与延迟。
// ConcurrencyLevel=1 为串行基线（一次一个构建），>1 为并行构建。
// 使用延迟装饰器模拟真实存储 I/O（1ms/query 近似 FileSystem），使并行优势可见。
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class ConcurrencyScalingBenchmarks
{
    private const string WorkspaceId = "bench-ws";
    private const string CollectionId = "bench-col";
    private const int QueryDelayMs = 1; // 近似 FileSystem per-query 延迟

    private BasicContextPackageBuilder _builder = null!;
    private ContextPackageRequest _request = null!;

    // 并发级别：1=串行，4=中等并发，16=高并发，64=极端并发
    [Params(1, 4, 16, 64)]
    public int ConcurrencyLevel { get; set; }

    [Params(50)]
    public int ItemCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var rawContextStore = new InMemoryContextStore();
        var rawMemoryStore = new InMemoryMemoryStore();
        var rawConstraintStore = new InMemoryConstraintStore();
        var rawGlobalStore = new InMemoryGlobalContextStore();
        var rawRelationStore = new InMemoryRelationStore();

        PopulateStores(rawContextStore, rawMemoryStore, rawConstraintStore, rawGlobalStore);

        // 用延迟装饰器包装，模拟真实存储 I/O 延迟
        IContextStore contextStore = new DelayedContextStore(rawContextStore, QueryDelayMs);
        IMemoryStore memoryStore = new DelayedMemoryStore(rawMemoryStore, QueryDelayMs);
        IConstraintStore constraintStore = new DelayedConstraintStore(rawConstraintStore, QueryDelayMs);
        IGlobalContextStore globalStore = new DelayedGlobalContextStore(rawGlobalStore, QueryDelayMs);

        _builder = new BasicContextPackageBuilder(
            contextStore,
            constraintStore,
            globalStore,
            memoryStore,
            rawRelationStore,
            workingMemoryService: rawMemoryStore);

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
            QueryText = "并发扩展基准测试 concurrency scaling benchmark",
            RequiredTags = ["task"],
            TokenBudget = 4000,
            Mode = ContextPackageMode.None,
            Policy = policy
        };
    }

    // 串行/并行构建：ConcurrencyLevel=1 时串行执行，>1 时 Task.WhenAll 并行执行。
    // 每次构建内部都是 6 路 Task.WhenAll 并行预取（builder 固有行为）。
    [Benchmark]
    public async Task Build_Concurrent()
    {
        if (ConcurrencyLevel == 1)
        {
            var result = await _builder.BuildDetailedAsync(_request, CancellationToken.None);
            _ = result.Package.Sections.Count;
        }
        else
        {
            var tasks = new Task[ConcurrencyLevel];
            for (int i = 0; i < ConcurrencyLevel; i++)
            {
                tasks[i] = Task.Run(async () =>
                {
                    var result = await _builder.BuildDetailedAsync(_request, CancellationToken.None);
                    _ = result.Package.Sections.Count;
                });
            }
            await Task.WhenAll(tasks);
        }
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
}

// 延迟装饰器（与 ParallelPrefetchBenchmarks 共享模式，独立声明避免跨文件依赖）

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
