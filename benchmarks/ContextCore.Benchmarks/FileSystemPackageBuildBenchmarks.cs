using BenchmarkDotNet.Attributes;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;

namespace ContextCore.Benchmarks;

// 真实 FileSystem 存储基准：使用 FileXxxStore（非 InMemory）测量真实 I/O 下的构建延迟。
// 验证并行预取在真实磁盘 I/O 下的效果，以及冷构建 vs 缓存命中的实际差距。
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class FileSystemPackageBuildBenchmarks
{
    private const string WorkspaceId = "bench-ws";
    private const string CollectionId = "bench-col";

    private string _rootPath = null!;
    private BasicContextPackageBuilder _builder = null!;
    private BasicContextPackageBuilder _cachedBuilder = null!;
    private ContextPackageRequest _request = null!;

    [Params(10, 50, 200)]
    public int ItemCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // 每次基准运行使用独立的临时目录，结束后清理
        _rootPath = Path.Combine(Path.GetTempPath(), "cc-bench-fs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_rootPath);

        var storageOptions = new FileStorageOptions { RootPath = _rootPath };
        var contextStore = new FileContextStore(storageOptions);
        var memoryStore = new FileMemoryStore(storageOptions);
        var constraintStore = new FileConstraintStore(storageOptions);
        var globalStore = new FileGlobalContextStore(storageOptions);
        var relationStore = new FileRelationStore(storageOptions);

        PopulateStores(contextStore, memoryStore, constraintStore, globalStore);

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
            QueryText = "FileSystem 存储构建基准测试 filesystem storage benchmark",
            RequiredTags = ["task"],
            TokenBudget = 4000,
            Mode = ContextPackageMode.None,
            Policy = policy
        };

        // 无缓存 builder：每次都是冷构建，走真实磁盘 I/O
        _builder = new BasicContextPackageBuilder(
            contextStore,
            constraintStore,
            globalStore,
            memoryStore,
            relationStore,
            workingMemoryService: memoryStore);

        // 带缓存 builder：预热后测量缓存命中路径
        var cache = new InMemoryContextStateCache();
        var accessor = new ContextStateCacheAccessor(cache);
        _cachedBuilder = new BasicContextPackageBuilder(
            contextStore,
            constraintStore,
            globalStore,
            memoryStore,
            relationStore,
            workingMemoryService: memoryStore,
            cacheAccessor: accessor);

        // 预热缓存
        _ = _cachedBuilder.BuildDetailedAsync(_request, CancellationToken.None).GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
        catch
        {
            // 基准清理失败不影响结果
        }
    }

    private static void PopulateStores(
        FileContextStore contextStore,
        FileMemoryStore memoryStore,
        FileConstraintStore constraintStore,
        FileGlobalContextStore globalStore)
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

    // 真实 FileSystem 冷构建：6 次磁盘读取 + filter + assembly
    [Benchmark(Baseline = true)]
    public async Task FileSystem_ColdBuild()
    {
        var result = await _builder.BuildDetailedAsync(_request, CancellationToken.None);
        _ = result.Package.Sections.Count;
    }

    // 缓存命中：跳过磁盘 I/O，验证缓存对 FS 存储的实际加速效果
    [Benchmark]
    public async Task FileSystem_CacheHit()
    {
        var result = await _cachedBuilder.BuildDetailedAsync(_request, CancellationToken.None);
        _ = result.Package.Sections.Count;
    }
}
