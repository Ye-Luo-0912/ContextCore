using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Benchmarks;

// 基准：测量 BasicContextPackageBuilder.BuildDetailedAsync —— 最热的读路径
// （6 次 store 查询 + filter + assembly）。包含"缓存前"冷构建基线与"缓存后"命中/并发场景。
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class PackageBuildBenchmarks
{
    private const string WorkspaceId = "bench-ws";
    private const string CollectionId = "bench-col";

    private InMemoryContextStore _contextStore = null!;
    private InMemoryMemoryStore _memoryStore = null!;
    private InMemoryConstraintStore _constraintStore = null!;
    private InMemoryGlobalContextStore _globalContextStore = null!;
    private InMemoryRelationStore _relationStore = null!;
    private BasicContextPackageBuilder _builder = null!;
    private BasicContextPackageBuilder _cachedBuilder = null!;
    private ContextPackageRequest _request = null!;

    // 数据规模：ContextItem 数量。其余 store 按比例缩放。
    [Params(10, 50, 200)]
    public int ItemCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _contextStore = new InMemoryContextStore();
        _memoryStore = new InMemoryMemoryStore();
        _constraintStore = new InMemoryConstraintStore();
        _globalContextStore = new InMemoryGlobalContextStore();
        _relationStore = new InMemoryRelationStore();

        PopulateStores();

        // 构造一个包含全部 section 的策略（recent + hard + soft + working + stable + global），
        // 以便每次构建都命中全部 6 次 store 查询。
        var policy = new ContextPackagePolicy
        {
            Id = "bench-policy-all",
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Name = "BenchAllSections",
            Description = "基准策略：启用全部 Include 标志以触发 6 次 store 查询。",
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
            QueryText = "上下文包构建基准测试 package build benchmark",
            RequiredTags = ["task"],
            TokenBudget = 4000,
            Mode = ContextPackageMode.None,
            Policy = policy
        };

        // 无缓存 builder：每次都是冷构建。
        // InMemoryMemoryStore 同时实现 IWorkingMemoryService，直接传入。
        _builder = new BasicContextPackageBuilder(
            _contextStore,
            _constraintStore,
            _globalContextStore,
            _memoryStore,
            _relationStore,
            workingMemoryService: _memoryStore);

        // 带缓存 builder：共享同一组 store，命中时跳过 build + trace。
        var cache = new InMemoryContextStateCache();
        var accessor = new ContextStateCacheAccessor(cache);
        _cachedBuilder = new BasicContextPackageBuilder(
            _contextStore,
            _constraintStore,
            _globalContextStore,
            _memoryStore,
            _relationStore,
            workingMemoryService: _memoryStore,
            cacheAccessor: accessor);

        // 预热缓存：首次 miss 填充后，后续 CacheHit 基准全部命中（无写入失效）。
        _ = _cachedBuilder.BuildDetailedAsync(_request, CancellationToken.None).GetAwaiter().GetResult();
    }

    private void PopulateStores()
    {
        var now = DateTimeOffset.UtcNow;
        var rand = new Random(20260715); // 固定种子保证可复现

        // 1) ContextItems：ItemCount 条，内容 200-800 字符（中英混合）
        for (int i = 0; i < ItemCount; i++)
        {
            var createdAt = now.AddDays(-rand.Next(0, 90)).AddMinutes(-rand.Next(0, 1440));
            var content = BuildItemContent(i, rand);
            var item = new ContextItem
            {
                Id = $"ctx-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Type = "note",
                Title = $"上下文条目 {i} / context item {i}",
                Content = content,
                ContentFormat = ContextContentFormat.Markdown,
                Tags = ["task", "package"],
                Importance = 0.3 + rand.NextDouble() * 0.7, // 0.3 - 1.0
                CreatedAt = createdAt,
                UpdatedAt = createdAt.AddMinutes(rand.Next(1, 600))
            };
            _contextStore.SaveAsync(item).GetAwaiter().GetResult();
        }

        // 2) ContextMemoryItems：Stable 层 ItemCount/5 条
        var stableCount = Math.Max(1, ItemCount / 5);
        for (int i = 0; i < stableCount; i++)
        {
            var createdAt = now.AddDays(-rand.Next(0, 180));
            var memory = new ContextMemoryItem
            {
                Id = $"mem-stable-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Layer = ContextMemoryLayer.Stable,
                Status = ContextMemoryStatus.Stable,
                Type = "fact",
                Content = $"稳定记忆 stable memory fact #{i}：项目基线与长期约定 {BuildItemContent(i, rand)}",
                ContentFormat = ContextContentFormat.Markdown,
                Tags = ["task"],
                Importance = 0.5 + rand.NextDouble() * 0.5,
                CreatedAt = createdAt,
                UpdatedAt = createdAt
            };
            _memoryStore.SaveAsync(memory).GetAwaiter().GetResult();
        }

        // 3) ContextMemoryItems：Working 层 ItemCount/10 条
        var workingCount = Math.Max(1, ItemCount / 10);
        for (int i = 0; i < workingCount; i++)
        {
            var createdAt = now.AddHours(-rand.Next(1, 48));
            var memory = new ContextMemoryItem
            {
                Id = $"mem-working-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Layer = ContextMemoryLayer.Working,
                Status = ContextMemoryStatus.Active,
                Type = "note",
                Content = $"工作记忆 working memory #{i}：当前会话活跃信息 {BuildItemContent(i, rand)}",
                ContentFormat = ContextContentFormat.Markdown,
                Tags = ["task"],
                Importance = 0.6 + rand.NextDouble() * 0.4,
                CreatedAt = createdAt,
                UpdatedAt = createdAt
            };
            _memoryStore.SaveAsync(memory).GetAwaiter().GetResult();
        }

        // 4) 约束：10 hard + 10 soft（Status=Active 以保证被计入包，而非丢弃）
        for (int i = 0; i < 10; i++)
        {
            var hard = new ContextConstraint
            {
                Id = $"con-hard-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Scope = ContextScope.Collection,
                Level = ConstraintLevel.Hard,
                Content = $"硬约束 hard constraint #{i}：必须遵守的输出格式与安全边界。",
                Status = ContextMemoryStatus.Active,
                Confidence = 0.9,
                CreatedAt = now,
                UpdatedAt = now
            };
            _constraintStore.SaveAsync(hard).GetAwaiter().GetResult();

            var soft = new ContextConstraint
            {
                Id = $"con-soft-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Scope = ContextScope.Collection,
                Level = ConstraintLevel.Soft,
                Content = $"软约束 soft constraint #{i}：尽量遵守的风格与简洁性偏好。",
                Status = ContextMemoryStatus.Active,
                Confidence = 0.7,
                CreatedAt = now,
                UpdatedAt = now
            };
            _constraintStore.SaveAsync(soft).GetAwaiter().GetResult();
        }

        // 5) 全局上下文：5 条
        for (int i = 0; i < 5; i++)
        {
            var global = new ContextGlobalItem
            {
                Id = $"global-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = null,
                Scope = ContextScope.Workspace,
                Type = "preference",
                Content = $"全局上下文 global context #{i}：跨集合共享的用户偏好与项目设定。",
                ContentFormat = ContextContentFormat.Markdown,
                Tags = ["task"],
                Importance = 0.5 + rand.NextDouble() * 0.5,
                CreatedAt = now,
                UpdatedAt = now
            };
            _globalContextStore.SaveAsync(global).GetAwaiter().GetResult();
        }
    }

    // 生成 200-800 字符的中英混合内容。
    private static string BuildItemContent(int seed, Random rand)
    {
        var length = 200 + rand.Next(0, 600);
        var sb = new System.Text.StringBuilder(length + 16);
        var phrases = new[]
        {
            "上下文包构建", "package build", "基准测试", "benchmark",
            "记忆召回", "memory recall", "约束注入", "constraint injection",
            "全局偏好", "global preference", "工作记忆", "working memory",
            "稳定记忆", "stable memory", "近期上下文", "recent context"
        };
        while (sb.Length < length)
        {
            sb.Append(phrases[rand.Next(phrases.Length)]);
            sb.Append(' ');
            sb.Append('#');
            sb.Append(seed);
            sb.Append(' ');
        }
        return sb.ToString(0, Math.Min(sb.Length, length));
    }

    // 冷构建基线：无缓存，每次都走完整 6 次 store 查询 + filter + assembly。
    [Benchmark(Baseline = true)]
    public async Task BuildDetailed_Cold()
    {
        var result = await _builder.BuildDetailedAsync(_request, CancellationToken.None);
        // 防止死代码消除
        _ = result.Package.Sections.Count;
    }

    // 并发变体：在同一个 builder 实例上模拟并发读，用于评估锁/争用开销（缓存前）。
    [Benchmark]
    public async Task BuildDetailed_Concurrent8()
    {
        var tasks = new Task[8];
        for (int i = 0; i < 8; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                var result = await _builder.BuildDetailedAsync(_request, CancellationToken.None);
                _ = result.Package.Sections.Count;
            });
        }
        await Task.WhenAll(tasks);
    }

    // 缓存命中：GlobalSetup 已预热，基准体测量纯命中路径
    // （GetOrAddAsync 快速路径：字典查找 + 版本校验）。
    [Benchmark]
    public async Task BuildDetailed_CacheHit()
    {
        var result = await _cachedBuilder.BuildDetailedAsync(_request, CancellationToken.None);
        _ = result.Package.Sections.Count;
    }

    // 并发命中：8 路并发读同一热 key，全部命中缓存。用于暴露全局 LRU 锁争用。
    [Benchmark]
    public async Task BuildDetailed_CacheHit_Concurrent8()
    {
        var tasks = new Task[8];
        for (int i = 0; i < 8; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                var result = await _cachedBuilder.BuildDetailedAsync(_request, CancellationToken.None);
                _ = result.Package.Sections.Count;
            });
        }
        await Task.WhenAll(tasks);
    }
}

public class Program
{
    public static void Main(string[] args)
        => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
