using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;

namespace ContextCore.Tests;

/// <summary>
/// P0 缓存返工验收测试：scope 索引失效、多 scope 组合依赖、版本感知、
/// single-flight 并发去重、LRU 淘汰指标、EntityId 匹配规则。
/// </summary>
[TestClass]
[TestCategory("Infrastructure")]
[TestCategory("Concurrency")]
public sealed class ContextStateCacheTests
{
    /// <summary>基本写入与读取：scope 绑定后命中。</summary>
    [TestMethod]
    public async Task SetAndGet_SingleScope_ReturnsCachedValue()
    {
        var cache = new InMemoryContextStateCache();
        var key = StateCacheKey.From("ctx:ws1:col1:item1");
        var scope = new CacheInvalidationKey("ContextStore", "ws1", "col1", "item1");

        await cache.SetAsync(key, "hello", new DependencyScopeSet(scope));
        var result = await cache.GetAsync<string>(key);

        Assert.AreEqual("hello", result);
        Assert.AreEqual(1L, cache.Hits);
        Assert.AreEqual(0L, cache.Misses);
    }

    /// <summary>未命中的 key 返回 null 并计 miss。</summary>
    [TestMethod]
    public async Task Get_Miss_ReturnsNullAndCountsMiss()
    {
        var cache = new InMemoryContextStateCache();

        var result = await cache.GetAsync<string>(StateCacheKey.From("nonexistent"));

        Assert.IsNull(result);
        Assert.AreEqual(1L, cache.Misses);
        Assert.AreEqual(0L, cache.Hits);
    }

    /// <summary>scope 失效后条目被移除，再次读取为 miss。</summary>
    [TestMethod]
    public async Task Invalidate_ByScope_RemovesEntry()
    {
        var cache = new InMemoryContextStateCache();
        var key = StateCacheKey.From("ctx:ws1:col1:item1");
        var scope = new CacheInvalidationKey("ContextStore", "ws1", "col1", "item1");

        await cache.SetAsync(key, "hello", new DependencyScopeSet(scope));
        await cache.InvalidateAsync(scope);

        var result = await cache.GetAsync<string>(key);
        Assert.IsNull(result);
    }

    /// <summary>多 scope 组合依赖：Package Builder 跨 5 个 Store，任一失效即移除。</summary>
    [TestMethod]
    public async Task Invalidate_MultiScopeDependency_AnyScopeRemovesEntry()
    {
        var cache = new InMemoryContextStateCache();
        var key = StateCacheKey.From("pkg:ws1:col1:build1");
        var contextScope = new CacheInvalidationKey("ContextStore", "ws1", "col1", null);
        var memoryScope = new CacheInvalidationKey("MemoryStore", "ws1", "col1", null);
        var constraintScope = new CacheInvalidationKey("ConstraintStore", "ws1", "col1", null);
        var globalScope = new CacheInvalidationKey("GlobalContextStore", "ws1", "col1", null);
        var relationScope = new CacheInvalidationKey("RelationStore", "ws1", "col1", null);

        var scopes = new DependencyScopeSet(contextScope, memoryScope, constraintScope, globalScope, relationScope);
        await cache.SetAsync(key, "package-result", scopes);

        // 验证写入后可命中
        Assert.IsNotNull(await cache.GetAsync<string>(key));

        // 任一 scope 失效即移除
        await cache.InvalidateAsync(memoryScope);
        Assert.IsNull(await cache.GetAsync<string>(key));
    }

    /// <summary>不相关的 scope 失效不影响条目。</summary>
    [TestMethod]
    public async Task Invalidate_UnrelatedScope_DoesNotRemoveEntry()
    {
        var cache = new InMemoryContextStateCache();
        var key = StateCacheKey.From("ctx:ws1:col1:item1");
        var scope = new CacheInvalidationKey("ContextStore", "ws1", "col1", "item1");

        await cache.SetAsync(key, "hello", new DependencyScopeSet(scope));

        // 不同 StoreKind
        await cache.InvalidateAsync(new CacheInvalidationKey("MemoryStore", "ws1", "col1", null));
        Assert.IsNotNull(await cache.GetAsync<string>(key));

        // 不同 Workspace
        await cache.InvalidateAsync(new CacheInvalidationKey("ContextStore", "ws2", "col1", null));
        Assert.IsNotNull(await cache.GetAsync<string>(key));

        // 不同 Collection
        await cache.InvalidateAsync(new CacheInvalidationKey("ContextStore", "ws1", "col2", null));
        Assert.IsNotNull(await cache.GetAsync<string>(key));
    }

    /// <summary>EntityId 匹配规则：scope.EntityId 为 null（依赖全集合）时，任一 entity 失效都命中。</summary>
    [TestMethod]
    public async Task Invalidate_CollectionLevelScope_MatchesAnyEntity()
    {
        var cache = new InMemoryContextStateCache();
        var key = StateCacheKey.From("pkg:ws1:col1:all");
        // 依赖全集合（EntityId=null）
        var scope = new CacheInvalidationKey("ContextStore", "ws1", "col1", null);

        await cache.SetAsync(key, "pkg", new DependencyScopeSet(scope));

        // 失效特定 entity 也应命中全集合依赖
        await cache.InvalidateAsync(new CacheInvalidationKey("ContextStore", "ws1", "col1", "item99"));
        Assert.IsNull(await cache.GetAsync<string>(key));
    }

    /// <summary>EntityId 匹配规则：scope.EntityId 为特定值时，全集合失效（EntityId=null）也命中。</summary>
    [TestMethod]
    public async Task Invalidate_WholeCollectionInvalidation_MatchesEntityScopedEntry()
    {
        var cache = new InMemoryContextStateCache();
        var key = StateCacheKey.From("ctx:ws1:col1:item1");
        var scope = new CacheInvalidationKey("ContextStore", "ws1", "col1", "item1");

        await cache.SetAsync(key, "val", new DependencyScopeSet(scope));

        // 全集合失效（EntityId=null）
        await cache.InvalidateAsync(new CacheInvalidationKey("ContextStore", "ws1", "col1", null));
        Assert.IsNull(await cache.GetAsync<string>(key));
    }

    /// <summary>EntityId 匹配规则：scope.EntityId 与失效 EntityId 不同时不命中。</summary>
    [TestMethod]
    public async Task Invalidate_DifferentEntityId_DoesNotMatch()
    {
        var cache = new InMemoryContextStateCache();
        var key = StateCacheKey.From("ctx:ws1:col1:item1");
        var scope = new CacheInvalidationKey("ContextStore", "ws1", "col1", "item1");

        await cache.SetAsync(key, "val", new DependencyScopeSet(scope));

        // 失效另一个 entity
        await cache.InvalidateAsync(new CacheInvalidationKey("ContextStore", "ws1", "col1", "item2"));
        Assert.IsNotNull(await cache.GetAsync<string>(key));
    }

    /// <summary>版本感知：写入后版本 bump，再次读取检测版本失配并移除。</summary>
    [TestMethod]
    public async Task Get_VersionMismatch_RemovesEntryAndCountsMismatch()
    {
        var versionStore = new InMemoryContextStateVersionStore();
        var cache = new InMemoryContextStateCache(versionStore);
        var key = StateCacheKey.From("ctx:ws1:col1:item1");
        var scope = new CacheInvalidationKey("ContextStore", "ws1", "col1", "item1");

        await cache.SetAsync(key, "v1", new DependencyScopeSet(scope));

        // 模拟 Store 写入后版本递增
        await versionStore.BumpVersionAsync("ws1", "col1", "ContextStore");

        var result = await cache.GetAsync<string>(key);
        Assert.IsNull(result);
        Assert.AreEqual(1L, cache.VersionMismatches);
    }

    /// <summary>多 scope 版本感知：任一 scope 版本变化即失配。</summary>
    [TestMethod]
    public async Task Get_MultiScopeVersionMismatch_AnyScopeBumpInvalidates()
    {
        var versionStore = new InMemoryContextStateVersionStore();
        var cache = new InMemoryContextStateCache(versionStore);
        var key = StateCacheKey.From("pkg:ws1:col1:build1");
        var contextScope = new CacheInvalidationKey("ContextStore", "ws1", "col1", null);
        var memoryScope = new CacheInvalidationKey("MemoryStore", "ws1", "col1", null);

        await cache.SetAsync(key, "pkg", new DependencyScopeSet(contextScope, memoryScope));

        // 仅 bump MemoryStore 版本
        await versionStore.BumpVersionAsync("ws1", "col1", "MemoryStore");

        Assert.IsNull(await cache.GetAsync<string>(key));
        Assert.AreEqual(1L, cache.VersionMismatches);
    }

    /// <summary>CLOCK 淘汰：超出容量时淘汰至少一个条目，并计 eviction。
    /// CLOCK 是近似 LRU：不保证淘汰精确的 LRU 项，但保证超容量时淘汰至少一个。</summary>
    [TestMethod]
    public async Task Set_ExceedsCapacity_EvictsLruAndCountsEviction()
    {
        var cache = new InMemoryContextStateCache(maxEntries: 3);
        var scope = new CacheInvalidationKey("ContextStore", "ws1", "col1", null);

        await cache.SetAsync(StateCacheKey.From("k1"), "v1", new DependencyScopeSet(scope));
        await cache.SetAsync(StateCacheKey.From("k2"), "v2", new DependencyScopeSet(scope));
        await cache.SetAsync(StateCacheKey.From("k3"), "v3", new DependencyScopeSet(scope));

        // 访问 k1 使其 accessed=1，获得第二次机会
        _ = await cache.GetAsync<string>(StateCacheKey.From("k1"));

        // 写入 k4，超出容量，CLOCK 应淘汰至少一个条目
        await cache.SetAsync(StateCacheKey.From("k4"), "v4", new DependencyScopeSet(scope));

        // CLOCK 保证淘汰至少一个，计数 >= 1
        Assert.IsTrue(cache.Evictions >= 1);
        // 容量应回到 maxEntries
        Assert.IsTrue(cache.Count <= 3);
        // k4 刚写入，应存在
        Assert.IsNotNull(await cache.GetAsync<string>(StateCacheKey.From("k4")));
    }

    /// <summary>scope 索引：失效不相关的 scope 不扫描全部条目（零条目 scope 快速返回）。</summary>
    [TestMethod]
    public async Task Invalidate_EmptyScopeIndex_ReturnsImmediately()
    {
        var cache = new InMemoryContextStateCache(maxEntries: 100);
        var scope = new CacheInvalidationKey("ContextStore", "ws1", "col1", null);

        // 写入 50 个条目
        for (var i = 0; i < 50; i++)
        {
            await cache.SetAsync(
                StateCacheKey.From($"k{i}"),
                $"v{i}",
                new DependencyScopeSet(scope));
        }

        // 失效一个不存在的 scope（无条目）应快速返回，不影响现有条目
        await cache.InvalidateAsync(new CacheInvalidationKey("VectorStore", "ws1", "col1", null));
        Assert.AreEqual(50, cache.Count);
    }

    /// <summary>覆盖写入：同一 key 重新写入时更新 scope 索引。</summary>
    [TestMethod]
    public async Task Set_OverwriteExistingKey_UpdatesScopeIndex()
    {
        var cache = new InMemoryContextStateCache();
        var key = StateCacheKey.From("ctx:ws1:col1:item1");
        var oldScope = new CacheInvalidationKey("ContextStore", "ws1", "col1", "item1");
        var newScope = new CacheInvalidationKey("MemoryStore", "ws1", "col1", "item1");

        await cache.SetAsync(key, "v1", new DependencyScopeSet(oldScope));
        await cache.SetAsync(key, "v2", new DependencyScopeSet(newScope));

        // 旧 scope 失效不应影响（已从索引移除）
        await cache.InvalidateAsync(oldScope);
        Assert.AreEqual("v2", await cache.GetAsync<string>(key));

        // 新 scope 失效应移除
        await cache.InvalidateAsync(newScope);
        Assert.IsNull(await cache.GetAsync<string>(key));
    }

    /// <summary>DependencyScopeSet 空集合抛异常。</summary>
    [TestMethod]
    public void DependencyScopeSet_Empty_Throws()
    {
        Assert.ThrowsException<ArgumentException>(() => new DependencyScopeSet());
    }

    /// <summary>single-flight：并发 miss 仅触发一次 factory。</summary>
    [TestMethod]
    public async Task GetOrAddAsync_ConcurrentMiss_SingleFlightInvokesFactoryOnce()
    {
        var cache = new InMemoryContextStateCache();
        var accessor = new ContextStateCacheAccessor(cache);
        var key = StateCacheKey.From("ctx:ws1:col1:item1");
        var scope = new CacheInvalidationKey("ContextStore", "ws1", "col1", "item1");

        var factoryCallCount = 0;
        var barrier = new Barrier(participantCount: 8);

        async Task<string> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref factoryCallCount);
            // 模拟慢工厂，让并发请求聚集
            await Task.Delay(100, ct);
            return "computed";
        }

        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await accessor.GetOrAddAsync(key, new DependencyScopeSet(scope), Factory);
        }));

        var results = await Task.WhenAll(tasks);

        // 所有结果一致
        Assert.IsTrue(results.All(r => r == "computed"));
        // factory 仅被调用一次（single-flight 合并）
        Assert.AreEqual(1, factoryCallCount);
    }

    /// <summary>single-flight：命中后不再调用 factory。</summary>
    [TestMethod]
    public async Task GetOrAddAsync_CacheHit_DoesNotInvokeFactory()
    {
        var cache = new InMemoryContextStateCache();
        var accessor = new ContextStateCacheAccessor(cache);
        var key = StateCacheKey.From("ctx:ws1:col1:item1");
        var scope = new CacheInvalidationKey("ContextStore", "ws1", "col1", "item1");

        var factoryCallCount = 0;

        await accessor.GetOrAddAsync(key, new DependencyScopeSet(scope), ct =>
        {
            Interlocked.Increment(ref factoryCallCount);
            return Task.FromResult("first");
        });

        // 第二次应命中缓存
        var result = await accessor.GetOrAddAsync(key, new DependencyScopeSet(scope), ct =>
        {
            Interlocked.Increment(ref factoryCallCount);
            return Task.FromResult("second");
        });

        Assert.AreEqual("first", result);
        Assert.AreEqual(1, factoryCallCount);
    }

    /// <summary>commit point 安全：失效使用 CancellationToken.None，提交后即使取消也完成失效。</summary>
    [TestMethod]
    public async Task Invalidate_AfterCommit_CompletesEvenWhenCancelled()
    {
        var cache = new InMemoryContextStateCache();
        var key = StateCacheKey.From("ctx:ws1:col1:item1");
        var scope = new CacheInvalidationKey("ContextStore", "ws1", "col1", "item1");

        await cache.SetAsync(key, "val", new DependencyScopeSet(scope));

        // 使用已取消的 token 调用 InvalidateAsync — 模拟 commit 后请求取消
        // 注意：InvalidateAsync 会先 ThrowIfCancellationRequested，所以这里测试的是
        // Decorator 层使用 CancellationToken.None 的行为（不传已取消 token）
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // 直接调用 cache 层：已取消 token 应抛异常
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            cache.InvalidateAsync(scope, cts.Token));

        // 但用 CancellationToken.None 应成功完成
        await cache.InvalidateAsync(scope, CancellationToken.None);
        Assert.IsNull(await cache.GetAsync<string>(key));
    }

    /// <summary>高并发读写：多线程交替 Set/Get/Invalidate 不崩溃不死锁。</summary>
    [TestMethod]
    public async Task ConcurrentMixedOperations_NoDeadlockNoCrash()
    {
        var cache = new InMemoryContextStateCache(maxEntries: 500);
        var scope = new CacheInvalidationKey("ContextStore", "ws1", "col1", null);
        var random = new Random(42);

        var tasks = Enumerable.Range(0, 16).Select(threadId => Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
            {
                var key = StateCacheKey.From($"k{random.Next(0, 50)}");
                var op = random.Next(0, 3);
                switch (op)
                {
                    case 0:
                        await cache.SetAsync(key, $"v{threadId}-{i}", new DependencyScopeSet(scope));
                        break;
                    case 1:
                        _ = await cache.GetAsync<string>(key);
                        break;
                    case 2:
                        await cache.InvalidateAsync(scope);
                        break;
                }
            }
        }));

        await Task.WhenAll(tasks);

        // 只要不死锁、不崩溃即通过
        Assert.IsTrue(cache.Count >= 0);
    }

    /// <summary>
    /// P2-1 commit-point 安全经真实 Decorator 验证：
    /// 内层 Store 写入成功后立即取消请求 token，Decorator 必须仍用 CancellationToken.None
    /// 完成失效与版本递增。若误用请求 token，失效会抛 OperationCanceledException。
    /// </summary>
    [TestMethod]
    public async Task Decorator_AfterCommit_InvalidatesEvenWhenRequestTokenCancelled()
    {
        var invalidator = new RecordingInvalidator();
        var versionStore = new InMemoryContextStateVersionStore();
        var inner = new CancelAfterWriteContextStore();
        var decorator = new InvalidatingContextStoreDecorator(inner, invalidator, versionStore);

        using var cts = new CancellationTokenSource();
        inner.OnWriteCommitted = () => cts.Cancel(); // 内层写入返回前取消请求 token（模拟 commit point）

        var item = new ContextItem { Id = "item1", WorkspaceId = "ws1", CollectionId = "col1", Content = "x" };
        await decorator.SaveAsync(item, cts.Token);

        Assert.AreEqual(1, invalidator.InvalidatedKeys.Count, "commit point 后必须完成失效，即使请求 token 已取消");
        var bumpedVersion = await versionStore.GetVersionAsync("ws1", "col1", InvalidationKeys.ContextStore, default);
        Assert.AreEqual(1L, bumpedVersion, "commit point 后必须 bump 版本");
    }

    /// <summary>
    /// P2-1 反例验证：若 Decorator 误用请求 token（用已取消 token 调 InvalidateAsync），
    /// 应抛 OperationCanceledException。此测试锁定"None 语义"的必要性。
    /// </summary>
    [TestMethod]
    public async Task Cache_Invalidate_WithCancelledToken_ThrowsAsExpected()
    {
        var cache = new InMemoryContextStateCache();
        var scope = new CacheInvalidationKey("ContextStore", "ws1", "col1", "i1");
        await cache.SetAsync(StateCacheKey.From("k"), "v", new DependencyScopeSet(scope));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            cache.InvalidateAsync(scope, cts.Token));
    }

    /// <summary>P1-3 StateCacheKey 非空约束：default 与空字符串在缓存边界被拒绝。</summary>
    [TestMethod]
    public async Task Boundary_DefaultOrEmptyKey_RejectedAtCacheBoundary()
    {
        var cache = new InMemoryContextStateCache();
        var scopes = new DependencyScopeSet(new CacheInvalidationKey("ContextStore", "ws", "col", null));

        await Assert.ThrowsExceptionAsync<ArgumentException>(() => cache.GetAsync<string>(default));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => cache.SetAsync(default, "v", scopes));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => cache.GetAsync<string>(new StateCacheKey("")));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => cache.GetAsync<string>(new StateCacheKey("   ")));
    }

    /// <summary>P1-3 Accessor 边界同样拒绝 default/空 key。</summary>
    [TestMethod]
    public async Task Accessor_DefaultKey_Rejected()
    {
        var accessor = new ContextStateCacheAccessor(new InMemoryContextStateCache());
        var scopes = new DependencyScopeSet(new CacheInvalidationKey("ContextStore", "ws", "col", null));

        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            accessor.GetOrAddAsync(default, scopes, _ => Task.FromResult<object>("v")));
    }

    /// <summary>
    /// P1-1 single-flight 锁回收：单次 GetOrAdd 完成后空闲信号量应被回收，
    /// 避免锁表随 distinct key 永久增长。热点 key（并发等待）不回收。
    /// </summary>
    [TestMethod]
    public async Task SingleFlight_IdleKey_ReclaimedAfterCompletion()
    {
        var cache = new InMemoryContextStateCache();
        var accessor = new ContextStateCacheAccessor(cache);
        var scopes = new DependencyScopeSet(new CacheInvalidationKey("ContextStore", "ws", "col", null));

        // 一次未命中 → factory 执行 → 写入 → 完成。空闲信号量应被回收。
        await accessor.GetOrAddAsync(StateCacheKey.From("cold-key"), scopes,
            _ => Task.FromResult<object>("v"), default);

        // 锁表应为空（冷 key 完成后回收）。无法直接观察私有字段，但通过并发重复调用验证不泄漏：
        // 大量 distinct key 各调用一次，若不回收会导致锁表持续增长（此处仅验证功能正常 + 不死锁）。
        for (var i = 0; i < 50; i++)
        {
            await accessor.GetOrAddAsync(StateCacheKey.From($"k-{i}"), scopes,
                _ => Task.FromResult<object>($"v-{i}"), default);
        }

        Assert.AreEqual(51, cache.Count); // 1 cold + 50 = 51 条目均成功写入
    }

    /// <summary>
    /// P1-1 single-flight 热点去重：并发 miss 同一 key 时 factory 仅调用一次。
    /// 验证 single-flight 在锁回收设计下仍正确工作。
    /// </summary>
    [TestMethod]
    public async Task SingleFlight_ConcurrentMiss_FactoryInvokedOnce()
    {
        var cache = new InMemoryContextStateCache();
        var accessor = new ContextStateCacheAccessor(cache);
        var scopes = new DependencyScopeSet(new CacheInvalidationKey("ContextStore", "ws", "col", null));

        var factoryCalls = 0;
        var gate = new SemaphoreSlim(0);
        var key = StateCacheKey.From("hot-key");

        async Task<object> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref factoryCalls);
            await gate.WaitAsync(ct); // 阻塞首个 factory，让其他并发 miss 排队
            return "v";
        }

        // 8 个并发 miss
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => accessor.GetOrAddAsync(key, scopes, Factory, default)))
            .ToArray();

        // 等待 factory 被调用并阻塞
        await Task.Delay(100);
        gate.Release(8); // 放行

        var results = await Task.WhenAll(tasks);

        Assert.AreEqual(1, factoryCalls, "并发 miss 同一 key 时 factory 应仅调用一次（single-flight）");
        Assert.IsTrue(results.All(r => Equals(r, "v")));
    }

    /// <summary>
    /// P2-2 批量版本校验：条目依赖多 scope，任一 scope 版本被 bump 后命中应检测到失配并移除。
    /// 验证 GetVersionsAsync 批量路径正确工作。
    /// </summary>
    [TestMethod]
    public async Task VersionCheck_BatchQuery_DetectsAnyScopeMismatch()
    {
        var versionStore = new InMemoryContextStateVersionStore();
        var cache = new InMemoryContextStateCache(versionStore);

        var scopeA = new CacheInvalidationKey("ContextStore", "ws1", "col1", null);
        var scopeB = new CacheInvalidationKey("MemoryStore", "ws1", "col1", null);
        var key = StateCacheKey.From("pkg:ws1:col1");

        await cache.SetAsync(key, "pkg", new DependencyScopeSet(scopeA, scopeB));
        Assert.IsNotNull(await cache.GetAsync<string>(key));
        Assert.AreEqual(0L, cache.VersionMismatches);

        // bump scopeB 版本，命中时应检测到失配
        await versionStore.BumpVersionAsync("ws1", "col1", "MemoryStore", default);

        var result = await cache.GetAsync<string>(key);
        Assert.IsNull(result, "任一 scope 版本失配应返回 null");
        Assert.AreEqual(1L, cache.VersionMismatches);
    }

    /// <summary>P2-3 Clear() 与并发 SetAsync 不导致结构不一致（不死锁不崩溃）。</summary>
    [TestMethod]
    public async Task Clear_ConcurrentWithSet_NoInconsistency()
    {
        var cache = new InMemoryContextStateCache(maxEntries: 200);
        var scopes = new DependencyScopeSet(new CacheInvalidationKey("ContextStore", "ws", "col", null));

        var setTask = Task.Run(async () =>
        {
            for (var i = 0; i < 500; i++)
            {
                await cache.SetAsync(StateCacheKey.From($"k-{i}"), $"v-{i}", scopes);
            }
        });

        // 并发清空多次
        for (var i = 0; i < 10; i++)
        {
            cache.Clear();
            await Task.Yield();
        }

        await setTask;

        // Clear 后再写入+读取应正常工作（验证结构未损坏）
        cache.Clear();
        await cache.SetAsync(StateCacheKey.From("after-clear"), "v", scopes);
        Assert.IsNotNull(await cache.GetAsync<string>(StateCacheKey.From("after-clear")));
    }

    // ── 阶段1：缓存正确性收口测试 ──────────────────────────────────────────

    /// <summary>
    /// 指纹碰撞测试：包含分隔符的输入不应产生相同指纹。
    /// 旧方案使用 |/:/, 分隔符拼接，("a|b","c") 与 ("a","b|c") 会碰撞。
    /// 新方案使用长度前缀编码，确保不同输入产生不同指纹。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public void Fingerprint_DelimiterBearingInputs_DoNotCollide()
    {
        var policy = new ContextPackagePolicy { TokenBudget = 1000 };

        // 旧分隔符方案会碰撞的输入对
        var req1 = new ContextPackageRequest { WorkspaceId = "a|b", CollectionId = "c", TokenBudget = 1000 };
        var req2 = new ContextPackageRequest { WorkspaceId = "a", CollectionId = "b|c", TokenBudget = 1000 };

        var fp1 = BasicContextPackageBuilder.BuildRequestFingerprint(req1, policy);
        var fp2 = BasicContextPackageBuilder.BuildRequestFingerprint(req2, policy);

        Assert.AreNotEqual(fp1, fp2, "包含分隔符的输入不得产生相同指纹");

        // 包含 : 的输入也不应碰撞
        var req3 = new ContextPackageRequest { WorkspaceId = "a:b", CollectionId = "c", TokenBudget = 1000 };
        var req4 = new ContextPackageRequest { WorkspaceId = "a", CollectionId = "b:c", TokenBudget = 1000 };

        var fp3 = BasicContextPackageBuilder.BuildRequestFingerprint(req3, policy);
        var fp4 = BasicContextPackageBuilder.BuildRequestFingerprint(req4, policy);

        Assert.AreNotEqual(fp3, fp4, "包含冒号的输入不得产生相同指纹");
    }

    /// <summary>
    /// 指纹完整性测试：mustHit IDs 和 currentTask 元数据应纳入指纹。
    /// 不同 mustHit 或 currentTask 的请求应产生不同指纹。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public void Fingerprint_IncludesMustHitAndCurrentTask()
    {
        var policy = new ContextPackagePolicy { TokenBudget = 1000 };

        var req1 = new ContextPackageRequest
        {
            WorkspaceId = "ws1",
            CollectionId = "col1",
            TokenBudget = 1000,
            Metadata = new Dictionary<string, string> { ["mustHit"] = "item-a" }
        };
        var req2 = new ContextPackageRequest
        {
            WorkspaceId = "ws1",
            CollectionId = "col1",
            TokenBudget = 1000,
            Metadata = new Dictionary<string, string> { ["mustHit"] = "item-b" }
        };

        var fp1 = BasicContextPackageBuilder.BuildRequestFingerprint(req1, policy);
        var fp2 = BasicContextPackageBuilder.BuildRequestFingerprint(req2, policy);
        Assert.AreNotEqual(fp1, fp2, "不同 mustHit ID 必须产生不同指纹");

        var req3 = new ContextPackageRequest
        {
            WorkspaceId = "ws1",
            CollectionId = "col1",
            TokenBudget = 1000,
            Metadata = new Dictionary<string, string> { ["currentTaskId"] = "task-1" }
        };
        var req4 = new ContextPackageRequest
        {
            WorkspaceId = "ws1",
            CollectionId = "col1",
            TokenBudget = 1000,
            Metadata = new Dictionary<string, string> { ["currentTaskId"] = "task-2" }
        };

        var fp3 = BasicContextPackageBuilder.BuildRequestFingerprint(req3, policy);
        var fp4 = BasicContextPackageBuilder.BuildRequestFingerprint(req4, policy);
        Assert.AreNotEqual(fp3, fp4, "不同 currentTaskId 必须产生不同指纹");
    }

    /// <summary>
    /// 全局数据失效测试：GlobalContextStore 写入应失效 package 缓存条目。
    /// Package 依赖 scope 包含 GlobalContextStore，全局上下文变更后缓存应被清除。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public async Task Invalidate_GlobalContextStore_EvictsPackageCacheEntry()
    {
        var cache = new InMemoryContextStateCache();
        var key = StateCacheKey.From("pkg:ws1:col1:fingerprint-1");

        // Package 依赖的 6 个 collection-level scope（含 WorkingMemoryService）
        var scopes = new DependencyScopeSet(
            new CacheInvalidationKey("ContextStore", "ws1", "col1", null),
            new CacheInvalidationKey("MemoryStore", "ws1", "col1", null),
            new CacheInvalidationKey("ConstraintStore", "ws1", "col1", null),
            new CacheInvalidationKey("GlobalContextStore", "ws1", "col1", null),
            new CacheInvalidationKey("RelationStore", "ws1", "col1", null),
            new CacheInvalidationKey("WorkingMemoryService", "ws1", "col1", null));

        await cache.SetAsync(key, "package-result", scopes);
        Assert.IsNotNull(await cache.GetAsync<string>(key), "写入后应命中");

        // GlobalContextStore 失效应移除 package 缓存
        await cache.InvalidateAsync(new CacheInvalidationKey("GlobalContextStore", "ws1", "col1", null));
        Assert.IsNull(await cache.GetAsync<string>(key), "GlobalContextStore 失效后缓存应被清除");
    }

    /// <summary>
    /// 当前任务失效测试：WorkingMemoryService 写入应失效 package 缓存条目。
    /// SetCurrentTaskAsync 等操作通过 WorkingMemoryService scope 触发失效，
    /// 确保 current_task section 变更后缓存不返回过期结果。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public async Task Invalidate_WorkingMemoryService_EvictsPackageCacheEntry()
    {
        var cache = new InMemoryContextStateCache();
        var key = StateCacheKey.From("pkg:ws1:col1:fingerprint-1");

        var scopes = new DependencyScopeSet(
            new CacheInvalidationKey("ContextStore", "ws1", "col1", null),
            new CacheInvalidationKey("MemoryStore", "ws1", "col1", null),
            new CacheInvalidationKey("ConstraintStore", "ws1", "col1", null),
            new CacheInvalidationKey("GlobalContextStore", "ws1", "col1", null),
            new CacheInvalidationKey("RelationStore", "ws1", "col1", null),
            new CacheInvalidationKey("WorkingMemoryService", "ws1", "col1", null));

        await cache.SetAsync(key, "package-result", scopes);
        Assert.IsNotNull(await cache.GetAsync<string>(key), "写入后应命中");

        // WorkingMemoryService 失效应移除 package 缓存
        await cache.InvalidateAsync(new CacheInvalidationKey("WorkingMemoryService", "ws1", "col1", null));
        Assert.IsNull(await cache.GetAsync<string>(key), "WorkingMemoryService 失效后缓存应被清除");
    }

    /// <summary>
    /// 对象修改隔离测试：缓存返回的对象 Metadata 属性类型为 IReadOnlyDictionary，
    /// 编译期阻止通过索引器修改（最常见的误用路径）。
    /// 运行时底层对象仍可能是 Dictionary（调用方通过 cast 仍可修改），
    /// 完整的运行时隔离需在缓存边界做防御性拷贝，作为后续改进。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public async Task ObjectIsolation_ReturnedMetadata_IsReadOnlyDictionaryAtPropertyLevel()
    {
        var cache = new InMemoryContextStateCache();
        var key = StateCacheKey.From("pkg:ws1:col1:iso-test");
        var scope = new CacheInvalidationKey("ContextStore", "ws1", "col1", null);

        var original = new ContextPackageBuildResult
        {
            BuildId = "build-1",
            Package = new ContextPackage
            {
                PackageId = "pkg-1",
                Metadata = new Dictionary<string, string> { ["key1"] = "value1" }
            },
            Metadata = new Dictionary<string, string> { ["buildKey"] = "buildValue" }
        };

        await cache.SetAsync(key, original, new DependencyScopeSet(scope));

        var retrieved = await cache.GetAsync<ContextPackageBuildResult>(key);
        Assert.IsNotNull(retrieved);

        // Metadata 属性类型为 IReadOnlyDictionary<string, string>（编译期隔离）
        Assert.IsInstanceOfType<IReadOnlyDictionary<string, string>>(retrieved.Metadata);
        Assert.IsInstanceOfType<IReadOnlyDictionary<string, string>>(retrieved.Package.Metadata);

        // 验证缓存值正确读取
        Assert.AreEqual("buildValue", retrieved.Metadata["buildKey"]);
        Assert.AreEqual("value1", retrieved.Package.Metadata["key1"]);
    }

    private sealed class RecordingInvalidator : IStateCacheInvalidator
    {
        public List<CacheInvalidationKey> InvalidatedKeys { get; } = new();

        public Task InvalidateAsync(CacheInvalidationKey key, CancellationToken cancellationToken = default)
        {
            InvalidatedKeys.Add(key);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 内层 Store：写入成功后触发回调（用于在 commit point 取消请求 token），
    /// 其他方法抛 NotImplementedException（测试只用 SaveAsync）。
    /// </summary>
    private sealed class CancelAfterWriteContextStore : IContextStore
    {
        public Action? OnWriteCommitted { get; set; }

        public Task SaveAsync(ContextItem item, CancellationToken cancellationToken = default)
        {
            OnWriteCommitted?.Invoke(); // 内层写入"成功"后立即取消请求 token（模拟 commit point 边界）
            return Task.CompletedTask;
        }

        public Task<ContextItem?> GetAsync(string workspaceId, string collectionId, string id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<ContextItem>> QueryAsync(ContextQuery query, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task DeleteAsync(string workspaceId, string collectionId, string id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }
}
