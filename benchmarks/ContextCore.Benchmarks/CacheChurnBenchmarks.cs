using BenchmarkDotNet.Attributes;
using ContextCore.Abstractions;
using ContextCore.Core;

namespace ContextCore.Benchmarks;

// 缓存 churn 基准：测量高容量下的写入和失效性能。
// 当前实现使用近似 LRU（超容量时全量扫描字典找最旧项，O(N)）。
// 本基准在 10k 容量下测量：
// - 持续写入（触发 LRU 淘汰）的吞吐
// - 按 scope 失效的性能（scope 反向索引 O(M)）
// 结果用于决定是否需要换用 CLOCK/分段近似 LRU。
[MemoryDiagnoser]
public class CacheChurnBenchmarks
{
    private InMemoryContextStateCache _cache = null!;
    private List<StateCacheKey> _keys = null!;
    private List<DependencyScopeSet> _scopes = null!;

    // 容量：10000 是生产场景的典型上限
    [Params(1000, 10000)]
    public int Capacity { get; set; }

    // 写入量：容量 × 2，确保触发 LRU 淘汰
    private int WriteCount => Capacity * 2;

    [GlobalSetup]
    public void Setup()
    {
        _keys = new List<StateCacheKey>(WriteCount);
        _scopes = new List<DependencyScopeSet>(WriteCount);

        var rand = new Random(20260715);
        for (int i = 0; i < WriteCount; i++)
        {
            var ws = $"ws-{i % 100}";
            var col = $"col-{i % 50}";
            _keys.Add(StateCacheKey.From($"pkg:{ws}:{col}:fp-{i}"));
            // 每个 entry 依赖 6 个 scope（模拟 package 缓存的真实场景）
            _scopes.Add(new DependencyScopeSet(
                new CacheInvalidationKey("ContextStore", ws, col, null),
                new CacheInvalidationKey("MemoryStore", ws, col, null),
                new CacheInvalidationKey("ConstraintStore", ws, col, null),
                new CacheInvalidationKey("GlobalContextStore", ws, col, null),
                new CacheInvalidationKey("RelationStore", ws, col, null),
                new CacheInvalidationKey("WorkingMemoryService", ws, col, null)));
        }
    }

    // 每次迭代前重置缓存为空，确保 WriteWithLruEviction 测量的是真正的写入+淘汰路径
    [IterationSetup(Target = nameof(WriteWithLruEviction))]
    public void SetupForWrite()
    {
        _cache = new InMemoryContextStateCache(Capacity);
    }

    // 每次迭代前重置并填满缓存，确保 InvalidateByScope 只测量失效操作
    [IterationSetup(Target = nameof(InvalidateByScope))]
    public void SetupForInvalidate()
    {
        _cache = new InMemoryContextStateCache(Capacity);
        for (int i = 0; i < WriteCount; i++)
        {
            _cache.SetAsync(_keys[i], $"value-{i}", _scopes[i]).GetAwaiter().GetResult();
        }
    }

    // 每次迭代前重置并填充一半缓存，确保 MixedReadWrite 只测量混合读写操作
    [IterationSetup(Target = nameof(MixedReadWrite))]
    public void SetupForMixed()
    {
        _cache = new InMemoryContextStateCache(Capacity);
        for (int i = 0; i < Capacity / 2; i++)
        {
            _cache.SetAsync(_keys[i], $"value-{i}", _scopes[i]).GetAwaiter().GetResult();
        }
    }

    // 持续写入 + LRU 淘汰：写入 Capacity×2 个条目，后半段触发淘汰
    [Benchmark]
    public async Task WriteWithLruEviction()
    {
        for (int i = 0; i < WriteCount; i++)
        {
            await _cache.SetAsync(_keys[i], $"value-{i}", _scopes[i]);
        }
    }

    // 按 scope 失效：对已满的缓存执行 collection 级失效
    // 测量 scope 反向索引的 O(M) 失效性能（填充已在 IterationSetup 完成）
    [Benchmark]
    public async Task InvalidateByScope()
    {
        for (int i = 0; i < 50; i++)
        {
            await _cache.InvalidateAsync(new CacheInvalidationKey("ContextStore", $"ws-{i % 100}", $"col-{i}", null));
        }
    }

    // 混合读写：模拟真实工作负载（80% 读，20% 写）
    // 填充一半已在 IterationSetup 完成
    [Benchmark]
    public async Task MixedReadWrite()
    {
        var rand = new Random(20260715);
        for (int i = 0; i < 5000; i++)
        {
            if (rand.Next(5) == 0)
            {
                // 20% 写
                var idx = rand.Next(WriteCount);
                await _cache.SetAsync(_keys[idx], $"value-{idx}", _scopes[idx]);
            }
            else
            {
                // 80% 读
                var idx = rand.Next(Capacity / 2);
                _ = await _cache.GetAsync<string>(_keys[idx]);
            }
        }
    }
}
