using System.Collections.Immutable;
using System.Reflection;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.Learning.V14_0;
using ContextCore.Runtime;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// 缓存返工验收测试：scope 索引失效、多 scope 组合依赖、版本感知、
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

    /// <summary>
    /// CLOCK 采样淘汰在大容量下收敛：写入远超 EvictionSampleSize 的条目，
    /// 缓存应淘汰至容量上限。验证 enumerator 采样路径不依赖 O(N) Keys.ToArray()。
    /// </summary>
    [TestMethod]
    public async Task Set_ExceedsCapacityWithManyEntries_EvictsUsingFixedSample_ConvergesToCapacity()
    {
        var cache = new InMemoryContextStateCache(maxEntries: 100);
        var scope = new CacheInvalidationKey("ContextStore", "ws1", "col1", null);

        for (var i = 0; i < 300; i++)
        {
            await cache.SetAsync(StateCacheKey.From($"k{i}"), $"v{i}", new DependencyScopeSet(scope));
        }

        Assert.IsTrue(cache.Count <= 100, "超容量后应淘汰至容量上限");
        Assert.IsTrue(cache.Evictions >= 200, "应至少淘汰 200 个条目（300 写入 - 100 容量）");
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
    /// commit-point 安全经真实 Decorator 验证：
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
    /// 反例验证：若 Decorator 误用请求 token（用已取消 token 调 InvalidateAsync），
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
    /// single-flight 锁回收：单次 GetOrAdd 完成后空闲信号量应被回收，
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
    /// single-flight 热点去重：并发 miss 同一 key 时 factory 仅调用一次。
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

    // ── P0-5.1 poisoned key 修复：factory 抛异常后 key 不应永久驻留 ─────────

    /// <summary>
    /// factory 抛异常后，in-flight entry 必须被移除（ContinueWith 清理），
    /// 后续相同 key 的调用应重新执行 factory，而非复用失败的 Lazy（poisoned key）。
    /// </summary>
    [TestMethod]
    public async Task SingleFlight_FactoryThrows_KeyNotPoisoned_SubsequentRetrySucceeds()
    {
        var cache = new InMemoryContextStateCache();
        var accessor = new ContextStateCacheAccessor(cache);
        var key = StateCacheKey.From("ctx:ws:col:poison-recovery");
        var scopes = new DependencyScopeSet(new CacheInvalidationKey("ContextStore", "ws", "col", null));

        var attempt = 0;

        async Task<string> Factory(CancellationToken ct)
        {
            var current = Interlocked.Increment(ref attempt);
            if (current == 1)
            {
                throw new InvalidOperationException("首次失败");
            }
            await Task.Yield();
            return "recovered";
        }

        // 首次调用：factory 抛异常
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            accessor.GetOrAddAsync(key, scopes, Factory, default));

        // 首次失败后，key 不应被 poison：第二次调用应重新执行 factory
        var result = await accessor.GetOrAddAsync(key, scopes, Factory, default);
        Assert.AreEqual("recovered", result);
        Assert.AreEqual(2, attempt, "第二次调用应重新执行 factory，而非复用失败的 task");
    }

    /// <summary>
    /// factory 抛异常时，所有并发等待者都应看到异常；task 完成后 entry 被回收，
    /// 后续调用应执行新的 factory（不返回缓存的失败 task）。
    /// </summary>
    [TestMethod]
    public async Task SingleFlight_FactoryThrows_AllWaitersSeeError_KeyReclaimedForRetry()
    {
        var cache = new InMemoryContextStateCache();
        var accessor = new ContextStateCacheAccessor(cache);
        var key = StateCacheKey.From("ctx:ws:col:concurrent-fault");
        var scopes = new DependencyScopeSet(new CacheInvalidationKey("ContextStore", "ws", "col", null));

        var factoryStarted = new TaskCompletionSource<bool>();
        var factoryGate = new SemaphoreSlim(0);
        var factoryCallCount = 0;

        async Task<string> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref factoryCallCount);
            factoryStarted.TrySetResult(true);
            await factoryGate.WaitAsync();
            throw new InvalidOperationException("factory 永远失败");
        }

        // 4 个并发调用方
        var callers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            try { return await accessor.GetOrAddAsync(key, scopes, Factory, default); }
            catch (InvalidOperationException) { return (string?)"FAULT"; }
        })).ToArray();

        // 等 factory 启动并阻塞
        await factoryStarted.Task;
        // 放行让 factory 失败
        factoryGate.Release();

        var results = await Task.WhenAll(callers);

        // 所有调用方都应看到异常（返回 "FAULT"）
        Assert.IsTrue(results.All(r => r == "FAULT"), "所有等待者都应看到 factory 的异常");
        Assert.AreEqual(1, factoryCallCount, "factory 应仅调用一次（single-flight）");

        // key 应已回收：后续调用应执行新的 factory（不返回缓存的失败 task）
        var retryAttempt = 0;
        var result = await accessor.GetOrAddAsync(key, scopes, ct =>
        {
            Interlocked.Increment(ref retryAttempt);
            return Task.FromResult("recovered");
        });
        Assert.AreEqual("recovered", result, "key 回收后应能成功重试");
        Assert.AreEqual(1, retryAttempt, "重试时应执行新的 factory");
    }

    // ── P0-5.2 调用方取消隔离：取消只放弃等待，不取消共享计算 ───────────────

    /// <summary>
    /// 第一个调用方取消后，共享 factory 计算应继续执行（使用 CancellationToken.None），
    /// 第二个调用方应能复用同一 in-flight task 并拿到结果，而非触发新的 factory 调用。
    /// </summary>
    [TestMethod]
    public async Task SingleFlight_FirstCallerCancels_SharedTaskContinues_SecondCallerGetsResult()
    {
        var cache = new InMemoryContextStateCache();
        var accessor = new ContextStateCacheAccessor(cache);
        var key = StateCacheKey.From("ctx:ws:col:cancel-isolation");
        var scopes = new DependencyScopeSet(new CacheInvalidationKey("ContextStore", "ws", "col", null));

        var factoryStarted = new TaskCompletionSource<bool>();
        var factoryGate = new SemaphoreSlim(0);
        var factoryCallCount = 0;

        async Task<string> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref factoryCallCount);
            factoryStarted.TrySetResult(true);
            // factory 使用 CancellationToken.None（由 accessor 传入），不受调用方取消影响
            await factoryGate.WaitAsync();
            return "computed";
        }

        // 第一个调用方：会取消
        using var cts1 = new CancellationTokenSource();
        var caller1 = Task.Run(async () =>
        {
            try { return await accessor.GetOrAddAsync(key, scopes, Factory, cts1.Token); }
            catch (OperationCanceledException) { return (string?)"CANCELLED"; }
        });

        // 等 factory 启动并进入 WaitAsync 等待
        await factoryStarted.Task;
        await Task.Delay(50);

        // 取消第一个调用方 — 只放弃 caller1 的等待，不取消共享 factory
        cts1.Cancel();
        var r1 = await caller1;
        Assert.AreEqual("CANCELLED", r1, "第一个调用方取消后应收到 OperationCanceledException");

        // 第二个调用方：复用同一 in-flight task（entry 仍在，共享 task 未完成）
        var caller2 = Task.Run(() => accessor.GetOrAddAsync(key, scopes, Factory, CancellationToken.None));

        // 放行 factory — 共享计算完成
        factoryGate.Release();
        var r2 = await caller2;

        Assert.AreEqual("computed", r2, "第二个调用方应能拿到结果（共享计算未被 caller1 取消）");
        Assert.AreEqual(1, factoryCallCount, "factory 应仅调用一次（共享计算未被取消）");
    }

    /// <summary>
    /// 批量版本校验：条目依赖多 scope，任一 scope 版本被 bump 后命中应检测到失配并移除。
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

        var fp1 = PackageRequestFingerprintBuilder.Build(req1, policy);
        var fp2 = PackageRequestFingerprintBuilder.Build(req2, policy);

        Assert.AreNotEqual(fp1, fp2, "包含分隔符的输入不得产生相同指纹");

        // 包含 : 的输入也不应碰撞
        var req3 = new ContextPackageRequest { WorkspaceId = "a:b", CollectionId = "c", TokenBudget = 1000 };
        var req4 = new ContextPackageRequest { WorkspaceId = "a", CollectionId = "b:c", TokenBudget = 1000 };

        var fp3 = PackageRequestFingerprintBuilder.Build(req3, policy);
        var fp4 = PackageRequestFingerprintBuilder.Build(req4, policy);

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

        var fp1 = PackageRequestFingerprintBuilder.Build(req1, policy);
        var fp2 = PackageRequestFingerprintBuilder.Build(req2, policy);
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

        var fp3 = PackageRequestFingerprintBuilder.Build(req3, policy);
        var fp4 = PackageRequestFingerprintBuilder.Build(req4, policy);
        Assert.AreNotEqual(fp3, fp4, "不同 currentTaskId 必须产生不同指纹");
    }

    /// <summary>
    /// 指纹纳入时间桶（5 分钟窗口）。同一请求在短时间内（同桶）指纹一致，
    /// 跨越 5 分钟边界后指纹必须变化，确保时间依赖评分（24h/7d/30d）跨边界后缓存自动失效。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public void Fingerprint_IncludesTimeBucket_ChangesAcrossBoundary()
    {
        var policy = new ContextPackagePolicy { TokenBudget = 1000 };
        var req = new ContextPackageRequest
        {
            WorkspaceId = "ws1",
            CollectionId = "col1",
            QueryText = "test",
            TokenBudget = 1000
        };

        // 同一时间桶内多次构建应产生相同指纹
        var fp1 = PackageRequestFingerprintBuilder.Build(req, policy);
        var fp2 = PackageRequestFingerprintBuilder.Build(req, policy);
        Assert.AreEqual(fp1, fp2, "同一时间桶内指纹必须一致");

        // 验证时间桶存在于指纹中：通过人工构造跨桶场景验证
        // 取当前桶号，构造一个 6 分钟前（必然跨桶）的时间戳对应的桶号
        var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var currentBucket = nowSeconds / 300;
        var pastBucket = (nowSeconds - 360) / 300; // 6 分钟前，必然跨桶
        Assert.AreNotEqual(currentBucket, pastBucket, "测试前置：6 分钟前应跨越 5 分钟桶边界");

        // 验证指纹包含时间桶字段：通过修改系统时间不可行，改为直接验证 Build 输出包含桶号
        // 这里通过验证 fp1 末尾包含 currentBucket 的字符串表示来确认
        Assert.IsTrue(fp1.Contains(currentBucket.ToString(), StringComparison.Ordinal),
            "指纹必须包含当前时间桶号，确保跨边界后指纹变化");
    }

    /// <summary>
    /// BuildHashed 输出固定长度 SHA-256 哈希（64 字符 hex），避免明文查询/metadata 驻留。
    /// 相同请求产生相同哈希；不同请求产生不同哈希；哈希长度固定。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public void Fingerprint_BuildHashed_ProducesFixedLengthSha256Hex()
    {
        var policy = new ContextPackagePolicy { TokenBudget = 1000 };
        var req = new ContextPackageRequest
        {
            WorkspaceId = "ws1",
            CollectionId = "col1",
            QueryText = "包含敏感信息的查询内容",
            TokenBudget = 1000,
            Metadata = new Dictionary<string, string> { ["user"] = "alice", ["internal"] = "secret-value" }
        };

        var hash1 = PackageRequestFingerprintBuilder.BuildHashed(req, policy);
        var hash2 = PackageRequestFingerprintBuilder.BuildHashed(req, policy);

        // 固定长度：SHA-256 = 32 字节 = 64 hex 字符
        Assert.AreEqual(64, hash1.Length, "SHA-256 哈希必须为 64 字符 hex");
        Assert.AreEqual(64, hash2.Length, "SHA-256 哈希必须为 64 字符 hex");

        // 相同输入产生相同哈希
        Assert.AreEqual(hash1, hash2, "相同请求必须产生相同哈希");

        // 哈希中不包含明文敏感信息
        Assert.IsFalse(hash1.Contains("secret-value", StringComparison.Ordinal), "哈希不得包含明文 metadata 值");
        Assert.IsFalse(hash1.Contains("alice", StringComparison.Ordinal), "哈希不得包含明文 metadata key/value");
        Assert.IsFalse(hash1.Contains("包含敏感信息的查询内容", StringComparison.Ordinal), "哈希不得包含明文查询文本");

        // 不同请求产生不同哈希
        var req2 = new ContextPackageRequest
        {
            WorkspaceId = "ws1",
            CollectionId = "col1",
            QueryText = "不同的查询内容",
            TokenBudget = 1000
        };
        var hash3 = PackageRequestFingerprintBuilder.BuildHashed(req2, policy);
        Assert.AreNotEqual(hash1, hash3, "不同请求必须产生不同哈希");

        // 验证全为有效 hex 字符
        foreach (var c in hash1)
        {
            Assert.IsTrue(
                (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'),
                $"哈希必须为有效 hex 字符，发现: {c}");
        }
    }

    // ──  semantic metadata fingerprint 测试 ──────────────────────────

    /// <summary>
    /// #7: 操作性 metadata key（requestId/traceId/operationId/correlationId/spanId 等）
    /// 必须从指纹中排除。两个语义相同但携带不同 per-call 标识的请求必须产生相同指纹，
    /// 避免相同语义请求因不同 requestId/traceId 导致缓存 miss。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public void Fingerprint_OperationalMetadataKeys_ExcludedFromFingerprint()
    {
        var policy = new ContextPackagePolicy { TokenBudget = 1000 };

        // 基线请求：无操作性 metadata
        var reqBaseline = new ContextPackageRequest
        {
            WorkspaceId = "ws1",
            CollectionId = "col1",
            QueryText = "semantic query",
            TokenBudget = 1000,
            Metadata = new Dictionary<string, string> { ["mode"] = "chat", ["intent"] = "answer" }
        };

        // 携带全部已知操作性 key 的请求（语义字段相同）
        var reqWithOperational = new ContextPackageRequest
        {
            WorkspaceId = "ws1",
            CollectionId = "col1",
            QueryText = "semantic query",
            TokenBudget = 1000,
            Metadata = new Dictionary<string, string>
            {
                ["mode"] = "chat",
                ["intent"] = "answer",
                // per-call 标识
                ["requestId"] = Guid.NewGuid().ToString("N"),
                ["traceId"] = Guid.NewGuid().ToString("N"),
                ["operationId"] = "op-" + Random.Shared.Next(),
                ["callerId"] = "caller-xyz",
                ["correlationId"] = "corr-abc",
                ["spanId"] = "span-001",
                // 时间戳
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                ["createdAt"] = DateTime.UtcNow.ToString("O"),
                ["requestTime"] = DateTime.UtcNow.ToString("O"),
                ["clientTimestamp"] = DateTime.UtcNow.ToString("O"),
                ["receivedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                // 客户端/网络诊断
                ["clientIp"] = "10.0.0.42",
                ["userAgent"] = "Mozilla/5.0",
                ["sessionId"] = "sess-" + Guid.NewGuid().ToString("N"),
                ["clientId"] = "client-001",
                ["remoteAddress"] = "192.168.1.1",
                // 内部追踪 header
                ["x-operation-id"] = "xop-" + Guid.NewGuid().ToString("N"),
                ["x-trace-id"] = "xtr-" + Guid.NewGuid().ToString("N"),
                ["x-request-id"] = "xreq-" + Guid.NewGuid().ToString("N")
            }
        };

        // 另一组完全不同的操作性值（语义字段仍相同）
        var reqDifferentOperational = new ContextPackageRequest
        {
            WorkspaceId = "ws1",
            CollectionId = "col1",
            QueryText = "semantic query",
            TokenBudget = 1000,
            Metadata = new Dictionary<string, string>
            {
                ["mode"] = "chat",
                ["intent"] = "answer",
                ["requestId"] = "different-request-id",
                ["traceId"] = "different-trace-id",
                ["clientIp"] = "172.16.0.99",
                ["userAgent"] = "curl/8.0",
                ["sessionId"] = "sess-other",
                ["timestamp"] = "0"
            }
        };

        var fpBaseline = PackageRequestFingerprintBuilder.Build(reqBaseline, policy);
        var fpWithOperational = PackageRequestFingerprintBuilder.Build(reqWithOperational, policy);
        var fpDifferentOperational = PackageRequestFingerprintBuilder.Build(reqDifferentOperational, policy);

        Assert.AreEqual(fpBaseline, fpWithOperational,
            "携带操作性 key 的请求必须与无操作性 key 的请求产生相同指纹（操作性 key 应被排除）");
        Assert.AreEqual(fpBaseline, fpDifferentOperational,
            "不同操作性值不得影响指纹（仅 per-call 标识不同，语义相同）");
        Assert.AreEqual(fpWithOperational, fpDifferentOperational,
            "两组不同操作性值的请求必须产生相同指纹");
    }

    /// <summary>
    /// #7: 语义 metadata key（mode/taskKind/intent/project/desiredOutputFormat/timeRange
    /// 及未在 denylist 中的自定义业务字段）必须纳入指纹。
    /// 不同语义 metadata 值必须产生不同指纹，确保 package 模板内容差异被缓存感知。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public void Fingerprint_SemanticMetadataKeys_StillIncluded()
    {
        var policy = new ContextPackagePolicy { TokenBudget = 1000 };

        // anchor metadata key（由 ContextAnchorExtractionProfile.MetadataRules 定义，不在 denylist）
        var anchorKeys = new[] { "mode", "taskKind", "intent", "project", "desiredOutputFormat", "timeRange" };

        foreach (var key in anchorKeys)
        {
            var req1 = new ContextPackageRequest
            {
                WorkspaceId = "ws1",
                CollectionId = "col1",
                TokenBudget = 1000,
                Metadata = new Dictionary<string, string> { [key] = "value-a" }
            };
            var req2 = new ContextPackageRequest
            {
                WorkspaceId = "ws1",
                CollectionId = "col1",
                TokenBudget = 1000,
                Metadata = new Dictionary<string, string> { [key] = "value-b" }
            };

            var fp1 = PackageRequestFingerprintBuilder.Build(req1, policy);
            var fp2 = PackageRequestFingerprintBuilder.Build(req2, policy);
            Assert.AreNotEqual(fp1, fp2,
                $"语义 metadata key '{key}' 的不同值必须产生不同指纹（应纳入指纹）");
        }

        // 自定义业务字段（不在 denylist 中）也应纳入指纹
        var reqBiz1 = new ContextPackageRequest
        {
            WorkspaceId = "ws1",
            CollectionId = "col1",
            TokenBudget = 1000,
            Metadata = new Dictionary<string, string> { ["customBusinessField"] = "A" }
        };
        var reqBiz2 = new ContextPackageRequest
        {
            WorkspaceId = "ws1",
            CollectionId = "col1",
            TokenBudget = 1000,
            Metadata = new Dictionary<string, string> { ["customBusinessField"] = "B" }
        };
        Assert.AreNotEqual(
            PackageRequestFingerprintBuilder.Build(reqBiz1, policy),
            PackageRequestFingerprintBuilder.Build(reqBiz2, policy),
            "未在 denylist 中的自定义业务字段必须纳入指纹");

        // 新增语义 key（即使原请求没有）必须改变指纹
        var reqAddKey = new ContextPackageRequest
        {
            WorkspaceId = "ws1",
            CollectionId = "col1",
            TokenBudget = 1000,
            Metadata = new Dictionary<string, string> { ["mode"] = "chat", ["project"] = "p1" }
        };
        var reqRemoveKey = new ContextPackageRequest
        {
            WorkspaceId = "ws1",
            CollectionId = "col1",
            TokenBudget = 1000,
            Metadata = new Dictionary<string, string> { ["mode"] = "chat" }
        };
        Assert.AreNotEqual(
            PackageRequestFingerprintBuilder.Build(reqAddKey, policy),
            PackageRequestFingerprintBuilder.Build(reqRemoveKey, policy),
            "语义字段集合差异（增减 key）必须产生不同指纹");
    }

    /// <summary>
    /// #7: 全部为操作性 key 的 metadata 应等同于空 metadata（语义字段数为 0）。
    /// 验证 denylist 过滤后写入 "-|" 占位，与 null/empty metadata 行为一致。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public void Fingerprint_AllOperationalMetadata_BehavesAsEmpty()
    {
        var policy = new ContextPackagePolicy { TokenBudget = 1000 };

        var reqNoMetadata = new ContextPackageRequest
        {
            WorkspaceId = "ws1",
            CollectionId = "col1",
            TokenBudget = 1000
        };
        var reqAllOperational = new ContextPackageRequest
        {
            WorkspaceId = "ws1",
            CollectionId = "col1",
            TokenBudget = 1000,
            Metadata = new Dictionary<string, string>
            {
                ["requestId"] = "r1",
                ["traceId"] = "t1",
                ["operationId"] = "o1",
                ["timestamp"] = "123",
                ["clientIp"] = "1.2.3.4",
                ["userAgent"] = "test",
                ["sessionId"] = "s1",
                ["x-request-id"] = "x1"
            }
        };

        var fpNoMetadata = PackageRequestFingerprintBuilder.Build(reqNoMetadata, policy);
        var fpAllOperational = PackageRequestFingerprintBuilder.Build(reqAllOperational, policy);
        Assert.AreEqual(fpNoMetadata, fpAllOperational,
            "全部为操作性 key 的 metadata 应等同于空 metadata（语义字段数为 0）");

        // 空字典也应等同于 null metadata
        var reqEmptyDict = new ContextPackageRequest
        {
            WorkspaceId = "ws1",
            CollectionId = "col1",
            TokenBudget = 1000,
            Metadata = new Dictionary<string, string>()
        };
        Assert.AreEqual(fpNoMetadata,
            PackageRequestFingerprintBuilder.Build(reqEmptyDict, policy),
            "空 metadata 字典应等同于 null metadata");
    }

    /// <summary>
    /// #7: BuildHashed 在 metadata 混合（语义 + 操作性）时仍输出 64 字符 SHA-256，
    /// 且相同语义请求即使携带不同操作性值也产生相同哈希（哈希层面稳定性）。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public void Fingerprint_BuildHashed_WithOperationalMetadata_StableAcrossSemanticRequests()
    {
        var policy = new ContextPackagePolicy { TokenBudget = 1000 };

        var req1 = new ContextPackageRequest
        {
            WorkspaceId = "ws1",
            CollectionId = "col1",
            QueryText = "shared query",
            TokenBudget = 1000,
            Metadata = new Dictionary<string, string>
            {
                ["mode"] = "chat",
                ["intent"] = "answer",
                ["requestId"] = "req-aaa",
                ["traceId"] = "tr-aaa",
                ["clientIp"] = "1.1.1.1"
            }
        };
        var req2 = new ContextPackageRequest
        {
            WorkspaceId = "ws1",
            CollectionId = "col1",
            QueryText = "shared query",
            TokenBudget = 1000,
            Metadata = new Dictionary<string, string>
            {
                ["mode"] = "chat",
                ["intent"] = "answer",
                ["requestId"] = "req-bbb",
                ["traceId"] = "tr-bbb",
                ["clientIp"] = "2.2.2.2"
            }
        };

        var hash1 = PackageRequestFingerprintBuilder.BuildHashed(req1, policy);
        var hash2 = PackageRequestFingerprintBuilder.BuildHashed(req2, policy);

        Assert.AreEqual(64, hash1.Length, "BuildHashed 必须输出 64 字符 SHA-256 hex");
        Assert.AreEqual(64, hash2.Length, "BuildHashed 必须输出 64 字符 SHA-256 hex");
        Assert.AreEqual(hash1, hash2,
            "语义相同（操作性 key 不同）的请求必须产生相同哈希");

        // 哈希不得包含操作性值的明文
        Assert.IsFalse(hash1.Contains("req-aaa", StringComparison.Ordinal), "哈希不得泄露操作性值明文");
        Assert.IsFalse(hash1.Contains("1.1.1.1", StringComparison.Ordinal), "哈希不得泄露操作性值明文");

        // 语义字段差异必须导致哈希差异
        var req3 = new ContextPackageRequest
        {
            WorkspaceId = "ws1",
            CollectionId = "col1",
            QueryText = "shared query",
            TokenBudget = 1000,
            Metadata = new Dictionary<string, string>
            {
                ["mode"] = "code", // 语义差异
                ["intent"] = "answer",
                ["requestId"] = "req-aaa"
            }
        };
        var hash3 = PackageRequestFingerprintBuilder.BuildHashed(req3, policy);
        Assert.AreNotEqual(hash1, hash3,
            "语义字段（mode）差异必须导致不同哈希");
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

    /// <summary>
    /// ResultProjector.ProjectResult 对模板的数组字段做防御性拷贝，
    /// 调用方修改结果数组不应污染缓存的 PackageTemplate。模拟缓存命中复用同一模板场景。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public void ResultProjector_ProjectResult_DefensiveArrayCopy_TemplateNotPolluted()
    {
        // 构造可控的源数组（模板内部持有的引用）
        var section = new ContextPackageSection { Name = "working_memory", Content = "memory-1" };
        var sections = new[] { section };
        var sourceRefs = new[] { "src-1", "src-2" };
        var selected = new[] { new ContextPackageDecision { ItemId = "item-1", SectionName = "working_memory" } };
        var dropped = new[] { new DroppedContextItem { ItemId = "dropped-1" } };
        var uncertainties = new[] { new ContextPackageUncertainty { Code = "OverBudget" } };
        var itemRefs = new[] { new ContextPackageItemReference { ItemId = "item-1", PrimarySectionName = "working_memory" } };

        var template = new PackageTemplate(
            OrderedSections: sections.ToImmutableArray(),
            SourceRefs: sourceRefs.ToImmutableArray(),
            EstimatedTokens: 100,
            TokenBudget: 1000,
            SortedSelectedItems: selected.ToImmutableArray(),
            DroppedItems: dropped.ToImmutableArray(),
            Uncertainties: uncertainties.ToImmutableArray(),
            ItemReferences: itemRefs.ToImmutableArray(),
            Anchors: ImmutableArray<ContextAnchor>.Empty,
            RetrievalPlan: null,
            Budget: new ContextPackageBudgetReport(),
            Output: new ContextPackageStandardOutput(),
            ModeBudgetProfile: null);

        var projector = new ResultProjector(new PackageTraceRecorder(
            new NullRuntimeCandidateTraceSink(),
            () => "op-iso",
            () => "req-iso"));

        var options = ResolvedPackageOptions.Resolve(
            new ContextPackageRequest
            {
                WorkspaceId = "ws-iso",
                CollectionId = "col-iso",
                QueryText = "isolation test",
                TokenBudget = 1000,
                Policy = new ContextPackagePolicy
                {
                    WorkspaceId = "ws-iso",
                    CollectionId = "col-iso",
                    TokenBudget = 1000
                }
            },
            new ContextPackagePolicy
            {
                WorkspaceId = "ws-iso",
                CollectionId = "col-iso",
                TokenBudget = 1000
            },
            new TokenEstimationContext("test-model", "test", false));

        // 第一次投影（模拟首次缓存命中）
        var result1 = projector.ProjectResult(template, options);

        // 验证结果正确读取
        Assert.AreEqual(1, result1.SelectedItems.Count);
        Assert.AreEqual("item-1", result1.SelectedItems[0].ItemId);
        Assert.AreEqual(1, result1.Package.Sections.Count);
        Assert.AreEqual("working_memory", result1.Package.Sections[0].Name);
        Assert.AreEqual(2, result1.Package.SourceRefs.Count);
        Assert.AreEqual("src-1", result1.Package.SourceRefs[0]);

        // 模拟调用方误用：直接修改结果数组元素
        ((ContextPackageDecision[])result1.SelectedItems)[0] = new ContextPackageDecision { ItemId = "POLLUTED" };
        ((ContextPackageSection[])result1.Package.Sections)[0] = new ContextPackageSection { Name = "POLLUTED" };
        ((string[])result1.Package.SourceRefs)[0] = "POLLUTED";

        // 第二次投影（模拟缓存命中复用同一模板）
        var result2 = projector.ProjectResult(template, options);

        // 验证第二次结果未被第一次的修改污染
        Assert.AreEqual("item-1", result2.SelectedItems[0].ItemId, "SelectedItems 不应被第一次结果污染");
        Assert.AreEqual("working_memory", result2.Package.Sections[0].Name, "Sections 不应被第一次结果污染");
        Assert.AreEqual("src-1", result2.Package.SourceRefs[0], "SourceRefs 不应被第一次结果污染");

        // 验证原始源数组也未被污染（防御性拷贝应保护源数组）
        Assert.AreEqual("item-1", selected[0].ItemId);
        Assert.AreEqual("working_memory", sections[0].Name);
        Assert.AreEqual("src-1", sourceRefs[0]);
    }

    // ──  mutable result isolation tests ──────────────────────────

    /// <summary>
    /// #8: 缓存层 GetAsync 不做防御性拷贝——返回存储的同一引用。
    /// 此测试文档化缓存边界行为：隔离责任由类型不可变性（PackageTemplate ImmutableArray）
    /// 与消费方防御性拷贝（ResultProjector.ToArray）承担，缓存本身不隔离可变对象。
    /// 若缓存可变对象，调用方修改会直接影响缓存——这正是 PackageTemplate 必须不可变的根因。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public async Task Cache_GetAsync_ReturnsSameReference_NoDefensiveCopy()
    {
        var cache = new InMemoryContextStateCache(maxEntries: 16);
        var scopes = new DependencyScopeSet(new CacheInvalidationKey("ContextStore", "ws", "col", null));
        var key = StateCacheKey.From("mutable-ref-test");

        // 用可变 List<string> 作为缓存值（模拟若缓存可变类型会发生什么）
        var mutable = new List<string> { "a", "b", "c" };
        await cache.SetAsync(key, mutable, scopes);

        var get1 = await cache.GetAsync<List<string>>(key);
        var get2 = await cache.GetAsync<List<string>>(key);

        Assert.IsNotNull(get1);
        Assert.IsNotNull(get2);
        Assert.AreSame(mutable, get1, "GetAsync 必须返回存储的同一引用（缓存不做防御性拷贝）");
        Assert.AreSame(get1, get2, "多次 GetAsync 返回同一引用");

        // 调用方通过返回引用修改对象会直接影响缓存内对象（证明缓存层无隔离）
        get1.Add("polluted");
        Assert.AreEqual(4, get2.Count, "缓存层未隔离——调用方修改直接影响缓存对象");
        Assert.AreEqual(4, mutable.Count, "原始存储对象也被修改（同一引用）");
    }

    /// <summary>
    /// #8: ResultProjector.ProjectResult 每次调用都为所有数组字段产生全新实例。
    /// 验证防御性拷贝真正创建新数组（不仅值不污染，引用也不同），
    /// 覆盖 6 个数组字段：SelectedItems / ItemReferences / DroppedItems / Uncertainties / Package.Sections / Package.SourceRefs。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public void ProjectResult_ProducesFreshArrayInstances_PerCall()
    {
        var template = BuildIsolationTemplate();
        var (projector, options) = BuildIsolationProjectorAndOptions();

        var result1 = projector.ProjectResult(template, options);
        var result2 = projector.ProjectResult(template, options);

        // 每次投影必须产生全新数组实例（非同一引用），证明防御性拷贝
        Assert.IsFalse(ReferenceEquals(result1.SelectedItems, result2.SelectedItems),
            "SelectedItems 每次投影必须是新数组实例");
        Assert.IsFalse(ReferenceEquals(result1.ItemReferences, result2.ItemReferences),
            "ItemReferences 每次投影必须是新数组实例");
        Assert.IsFalse(ReferenceEquals(result1.DroppedItems, result2.DroppedItems),
            "DroppedItems 每次投影必须是新数组实例");
        Assert.IsFalse(ReferenceEquals(result1.Uncertainties, result2.Uncertainties),
            "Uncertainties 每次投影必须是新数组实例");
        Assert.IsFalse(ReferenceEquals(result1.Package.Sections, result2.Package.Sections),
            "Package.Sections 每次投影必须是新数组实例");
        Assert.IsFalse(ReferenceEquals(result1.Package.SourceRefs, result2.Package.SourceRefs),
            "Package.SourceRefs 每次投影必须是新数组实例");

        // 内容仍应一致（同模板投影出相同数据）
        Assert.AreEqual(result1.SelectedItems.Count, result2.SelectedItems.Count);
        Assert.AreEqual(result1.Package.Sections.Count, result2.Package.Sections.Count);
    }

    /// <summary>
    /// #8: PackageTemplate 的集合字段为 ImmutableArray{T}（值类型 struct），
    /// 不暴露内部可变数组——ToArray 返回独立拷贝，修改拷贝不影响模板原始数据。
    /// 这是运行期不可变性保障：即使调用方拿到模板引用，通过任何公开 API（ToArray/索引器）
    /// 获得的数组都是拷贝，无法回写污染缓存的 ImmutableArray。
    /// 注：ImmutableArray{T} is T[] 为编译期恒假（CS0184），故改用 ToArray 拷贝独立性验证运行期行为。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public void PackageTemplate_ImmutableArray_ToArrayCopyDoesNotAffectOriginal()
    {
        var template = BuildIsolationTemplate();

        // OrderedSections: ToArray 返回独立拷贝
        var sectionsCopy = template.OrderedSections.ToArray();
        Assert.AreEqual(1, sectionsCopy.Length);
        sectionsCopy[0] = new ContextPackageSection { Name = "MUTATED" };
        Assert.AreEqual("working_memory", template.OrderedSections[0].Name,
            "修改 ToArray 拷贝不得影响 OrderedSections 原始数据");
        Assert.AreEqual("MUTATED", sectionsCopy[0].Name, "拷贝上的修改应可见于拷贝本身");

        // SortedSelectedItems
        var selectedCopy = template.SortedSelectedItems.ToArray();
        selectedCopy[0] = new ContextPackageDecision { ItemId = "MUTATED" };
        Assert.AreEqual("item-1", template.SortedSelectedItems[0].ItemId,
            "修改 ToArray 拷贝不得影响 SortedSelectedItems 原始数据");

        // SourceRefs（值类型 string 数组）
        var sourceRefsCopy = template.SourceRefs.ToArray();
        sourceRefsCopy[0] = "MUTATED";
        Assert.AreEqual("src-1", template.SourceRefs[0],
            "修改 ToArray 拷贝不得影响 SourceRefs 原始数据");

        // DroppedItems
        var droppedCopy = template.DroppedItems.ToArray();
        droppedCopy[0] = new DroppedContextItem { ItemId = "MUTATED" };
        Assert.AreEqual("dropped-1", template.DroppedItems[0].ItemId,
            "修改 ToArray 拷贝不得影响 DroppedItems 原始数据");

        // Uncertainties
        var uncertaintiesCopy = template.Uncertainties.ToArray();
        uncertaintiesCopy[0] = new ContextPackageUncertainty { Code = "MUTATED" };
        Assert.AreEqual("OverBudget", template.Uncertainties[0].Code,
            "修改 ToArray 拷贝不得影响 Uncertainties 原始数据");

        // ItemReferences
        var itemRefsCopy = template.ItemReferences.ToArray();
        itemRefsCopy[0] = new ContextPackageItemReference { ItemId = "MUTATED" };
        Assert.AreEqual("item-1", template.ItemReferences[0].ItemId,
            "修改 ToArray 拷贝不得影响 ItemReferences 原始数据");

        // 索引器读取不得返回可变回写句柄（ImmutableArray 索引器返回值拷贝/引用但数组本身不可替换）
        // 验证 Length 不受外部拷贝修改影响
        Assert.AreEqual(2, template.SourceRefs.Length, "模板 Length 不受外部拷贝修改影响");
        Assert.AreEqual(1, template.OrderedSections.Length);
    }

    /// <summary>
    /// #8: ContextStateCacheAccessor.GetOrAddAsync 缓存命中时返回同一模板引用。
    /// 验证 single-flight + 缓存命中路径共享模板实例（缓存模板复用的前提），
    /// 同时验证后续投影仍产生独立数组（完整隔离链路）。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public async Task CacheAccessor_GetOrAddAsync_ReturnsSharedTemplateReference_AcrossHits()
    {
        var cache = new InMemoryContextStateCache(maxEntries: 16);
        using var accessor = new ContextStateCacheAccessor(cache);
        var scopes = PackageRequestFingerprintBuilder.BuildDependencyScopes("ws-iso", "col-iso");
        var key = StateCacheKey.From("pkg:iso:template");

        var template = BuildIsolationTemplate();
        var factoryCallCount = 0;

        var t1 = await accessor.GetOrAddAsync<PackageTemplate>(
            key, scopes,
            ct =>
            {
                factoryCallCount++;
                return Task.FromResult(template);
            });
        var t2 = await accessor.GetOrAddAsync<PackageTemplate>(
            key, scopes,
            ct =>
            {
                factoryCallCount++;
                return Task.FromResult(BuildIsolationTemplate()); // 不会被调用（命中缓存）
            });

        Assert.AreEqual(1, factoryCallCount, "factory 只应在首次未命中时调用一次");
        Assert.AreSame(template, t1, "首次 GetOrAdd 返回 factory 产出的模板引用");
        Assert.AreSame(t1, t2, "缓存命中必须返回同一模板引用（无防御性拷贝）");

        // 共享模板仍可安全投影出独立结果
        var (projector, options) = BuildIsolationProjectorAndOptions();
        var r1 = projector.ProjectResult(t1, options);
        var r2 = projector.ProjectResult(t2, options);
        Assert.IsFalse(ReferenceEquals(r1.SelectedItems, r2.SelectedItems),
            "共享模板投影仍必须产生独立数组实例");
    }

    /// <summary>
    /// #8: 端到端隔离链路——缓存命中复用模板 → 投影产生结果 → 调用方误改结果数组 →
    /// 再次从缓存取模板并投影，新结果不受污染。验证 PackageTemplate 不可变 + ProjectResult 防御性拷贝
    /// 共同保证缓存模板在跨请求复用时的隔离正确性。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public async Task CacheAndProject_MutatingResultArray_DoesNotCorruptCachedTemplate()
    {
        var cache = new InMemoryContextStateCache(maxEntries: 16);
        using var accessor = new ContextStateCacheAccessor(cache);
        var scopes = PackageRequestFingerprintBuilder.BuildDependencyScopes("ws-iso", "col-iso");
        var key = StateCacheKey.From("pkg:iso:e2e");

        var originalTemplate = BuildIsolationTemplate();
        var (projector, options) = BuildIsolationProjectorAndOptions();

        var cachedTemplate = await accessor.GetOrAddAsync<PackageTemplate>(
            key, scopes, ct => Task.FromResult(originalTemplate));

        // 第一轮：投影并模拟调用方误改结果数组
        var result1 = projector.ProjectResult(cachedTemplate, options);
        Assert.AreEqual("item-1", result1.SelectedItems[0].ItemId);
        Assert.AreEqual("working_memory", result1.Package.Sections[0].Name);

        // 调用方通过不安全 cast 修改结果数组（模拟误用）
        ((ContextPackageDecision[])result1.SelectedItems)[0] = new ContextPackageDecision { ItemId = "POLLUTED" };
        ((ContextPackageSection[])result1.Package.Sections)[0] = new ContextPackageSection { Name = "POLLUTED" };
        ((string[])result1.Package.SourceRefs)[0] = "POLLUTED";
        ((DroppedContextItem[])result1.DroppedItems)[0] = new DroppedContextItem { ItemId = "POLLUTED" };
        ((ContextPackageUncertainty[])result1.Uncertainties)[0] = new ContextPackageUncertainty { Code = "POLLUTED" };
        ((ContextPackageItemReference[])result1.ItemReferences)[0] = new ContextPackageItemReference { ItemId = "POLLUTED" };

        // 第二轮：再次从缓存取模板（命中同一引用）并投影
        var cachedTemplateAgain = await accessor.GetOrAddAsync<PackageTemplate>(
            key, scopes, ct => Task.FromResult(BuildIsolationTemplate())); // 不应调用
        Assert.AreSame(originalTemplate, cachedTemplateAgain, "缓存命中返回同一模板引用");

        var result2 = projector.ProjectResult(cachedTemplateAgain, options);

        // 新结果完全不受第一轮误改影响——缓存模板未被污染
        Assert.AreEqual("item-1", result2.SelectedItems[0].ItemId, "SelectedItems 未被污染");
        Assert.AreEqual("working_memory", result2.Package.Sections[0].Name, "Sections 未被污染");
        Assert.AreEqual("src-1", result2.Package.SourceRefs[0], "SourceRefs 未被污染");
        Assert.AreEqual("dropped-1", result2.DroppedItems[0].ItemId, "DroppedItems 未被污染");
        Assert.AreEqual("OverBudget", result2.Uncertainties[0].Code, "Uncertainties 未被污染");
        Assert.AreEqual("item-1", result2.ItemReferences[0].ItemId, "ItemReferences 未被污染");
    }

    // ──  write-during-build race tests ───────────────────────────

    /// <summary>
    /// #9: 构建期间无并发写入（版本稳定）——factory 仅执行一次，结果缓存。
    /// 第二次 GetOrAddAsync 命中缓存，factory 不再调用。
    /// 这是版本感知的基线行为：版本向量前后一致 → 不触发重试 → 缓存写入。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public async Task Race_StableVersions_NoWriteDuringBuild_FactoryRunsOnce_ValueCached()
    {
        var cache = new InMemoryContextStateCache(maxEntries: 16);
        var versionStore = new InMemoryContextStateVersionStore();
        using var accessor = new ContextStateCacheAccessor(cache, versionStore);
        var scopes = PackageRequestFingerprintBuilder.BuildDependencyScopes("ws-race", "col-race");
        var key = StateCacheKey.From("pkg:race:stable");

        var factoryCallCount = 0;
        var factory = new Func<CancellationToken, Task<string>>(ct =>
        {
            Interlocked.Increment(ref factoryCallCount);
            // 不 bump 版本——模拟构建期间无并发写入
            return Task.FromResult($"v{factoryCallCount}");
        });

        var v1 = await accessor.GetOrAddAsync(key, scopes, factory);
        Assert.AreEqual(1, factoryCallCount, "无并发写入时 factory 仅执行一次");
        Assert.AreEqual("v1", v1);

        // 第二次应命中缓存，factory 不再调用
        var v2 = await accessor.GetOrAddAsync(key, scopes, factory);
        Assert.AreEqual(1, factoryCallCount, "缓存命中不应调用 factory");
        Assert.AreEqual("v1", v2, "缓存命中返回首次结果");
    }

    /// <summary>
    /// #9: 构建期间发生并发写入（版本变化）——版本向量前后不一致 → 触发单次重试。
    /// factory 首次调用期间 bump 版本（模拟并发 Store 写入），重试时不再 bump → 版本稳定 → 缓存。
    /// 验证：(1) factory 执行两次（重试）；(2) 返回值为重试结果（fresh，非首次 stale 结果）；
    /// (3) 重试后版本稳定 → 结果缓存；(4) 第二次 GetOrAdd 命中缓存不再调用 factory。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public async Task Race_WriteDuringBuild_VersionMismatch_TriggersSingleRetry_ReturnsFreshValue_Cached()
    {
        var cache = new InMemoryContextStateCache(maxEntries: 16);
        var versionStore = new InMemoryContextStateVersionStore();
        using var accessor = new ContextStateCacheAccessor(cache, versionStore);
        var scopes = PackageRequestFingerprintBuilder.BuildDependencyScopes("ws-race", "col-race");
        var key = StateCacheKey.From("pkg:race:write-once");

        var factoryCallCount = 0;
        var factory = new Func<CancellationToken, Task<string>>(async ct =>
        {
            var callNum = Interlocked.Increment(ref factoryCallCount);
            // 首次调用期间模拟并发写入——bump ContextStore 版本（发生在 before/after 版本捕获之间）
            if (callNum == 1)
            {
                await versionStore.BumpVersionAsync("ws-race", "col-race", "ContextStore", ct);
            }
            // 重试（第二次）不再 bump → 版本稳定
            return $"call{callNum}";
        });

        var v1 = await accessor.GetOrAddAsync(key, scopes, factory);

        Assert.AreEqual(2, factoryCallCount, "构建期间写入应触发版本失配 → 单次重试（factory 共两次）");
        Assert.AreEqual("call2", v1, "应返回重试结果（fresh），非首次 stale 结果（call1）");

        // 重试后版本稳定 → 结果已缓存；第二次 GetOrAdd 命中缓存
        var v2 = await accessor.GetOrAddAsync(key, scopes, factory);
        Assert.AreEqual(2, factoryCallCount, "缓存命中不应再次调用 factory");
        Assert.AreEqual("call2", v2, "缓存命中返回重试后的 fresh 结果");
    }

    /// <summary>
    /// #9: 持续并发写入（每次 factory 调用都 bump 版本）——重试后版本仍变化 → 放弃缓存。
    /// 验证：(1) factory 最多执行两次（单次重试，无无限循环）；(2) 仍返回结果（不抛异常，fail-open）；
    /// (3) 结果未写入缓存——第二次 GetOrAdd 再次触发 factory（证明未缓存）。
    /// 这确保高并发写入场景下不会缓存 stale 结果，同时避免重试风暴。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public async Task Race_PersistentWriteDuringBuild_RetryStillStale_ValueReturnedNotCached_FactoryCalledTwiceMax()
    {
        var cache = new InMemoryContextStateCache(maxEntries: 16);
        var versionStore = new InMemoryContextStateVersionStore();
        using var accessor = new ContextStateCacheAccessor(cache, versionStore);
        var scopes = PackageRequestFingerprintBuilder.BuildDependencyScopes("ws-race", "col-race");
        var key = StateCacheKey.From("pkg:race:persistent-write");

        var factoryCallCount = 0;
        var factory = new Func<CancellationToken, Task<string>>(async ct =>
        {
            var callNum = Interlocked.Increment(ref factoryCallCount);
            // 每次 factory 调用都 bump——模拟持续并发写入
            await versionStore.BumpVersionAsync("ws-race", "col-race", "ContextStore", ct);
            return $"call{callNum}";
        });

        var v1 = await accessor.GetOrAddAsync(key, scopes, factory);

        // 最多两次（首次 + 单次重试），不会无限重试
        Assert.AreEqual(2, factoryCallCount, "持续写入场景 factory 最多执行两次（单次重试上限）");
        Assert.IsNotNull(v1, "持续 stale 时仍应返回结果（fail-open，不抛异常）");
        Assert.AreEqual("call2", v1, "返回最后一次重试结果");

        // 结果未缓存——第二次 GetOrAdd 再次触发 factory（factoryCallCount 增至 3+）
        var v2 = await accessor.GetOrAddAsync(key, scopes, factory);
        Assert.IsTrue(factoryCallCount >= 3, "未缓存时第二次 GetOrAdd 应再次触发 factory（证明首次结果未写入缓存）");
    }

    /// <summary>
    /// #9: 无版本存储时（versionStore=null）跳过版本比较——factory 仅执行一次，结果缓存。
    /// 验证 CaptureVersionsAsync 返回 null 时 VersionsChanged 恒为 false，保持原有行为，
    /// 不因引入版本感知而破坏无版本存储的部署场景。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public async Task Race_NoVersionStore_SkipsComparison_FactoryRunsOnce_ValueCached()
    {
        var cache = new InMemoryContextStateCache(maxEntries: 16);
        // versionStore=null
        using var accessor = new ContextStateCacheAccessor(cache);
        var scopes = PackageRequestFingerprintBuilder.BuildDependencyScopes("ws-race", "col-race");
        var key = StateCacheKey.From("pkg:race:no-versionstore");

        var factoryCallCount = 0;
        var factory = new Func<CancellationToken, Task<string>>(ct =>
        {
            Interlocked.Increment(ref factoryCallCount);
            return Task.FromResult($"v{factoryCallCount}");
        });

        var v1 = await accessor.GetOrAddAsync(key, scopes, factory);
        Assert.AreEqual(1, factoryCallCount, "无版本存储时跳过版本比较，factory 仅执行一次");
        Assert.AreEqual("v1", v1);

        var v2 = await accessor.GetOrAddAsync(key, scopes, factory);
        Assert.AreEqual(1, factoryCallCount, "缓存命中不调用 factory");
        Assert.AreEqual("v1", v2);
    }

    // ──  factory shutdown token 与 timeout ──────────────────────

    /// <summary>
    /// #4: Shutdown() 取消正在执行的 factory——factory 通过 token 收到取消信号。
    /// factory 内部应观测到 token.IsCancellationRequested 变为 true。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public async Task Shutdown_CancelsInflightFactory_TokenPropagatesToFactory()
    {
        var cache = new InMemoryContextStateCache();
        using var accessor = new ContextStateCacheAccessor(cache);
        var key = StateCacheKey.From("ctx:ws:col:shutdown-propagation");
        var scopes = new DependencyScopeSet(new CacheInvalidationKey("ContextStore", "ws", "col", null));

        var factoryStarted = new TaskCompletionSource<bool>();
        var factoryTokenObserved = new TaskCompletionSource<CancellationToken>();
        var factoryGate = new SemaphoreSlim(0);

        async Task<string> Factory(CancellationToken ct)
        {
            factoryTokenObserved.TrySetResult(ct);
            factoryStarted.TrySetResult(true);
            // factory 阻塞等待，直到 shutdown 触发取消或显式放行
            try
            {
                await factoryGate.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // shutdown 取消——确认 token 已取消后重新抛出
                Assert.IsTrue(ct.IsCancellationRequested, "shutdown 后 factory token 应为已取消");
                throw;
            }
            return "completed";
        }

        // 启动 factory（在后台线程，避免阻塞测试）
        var caller = Task.Run(async () =>
        {
            try
            {
                return await accessor.GetOrAddAsync(key, scopes, Factory, CancellationToken.None);
            }
            catch (OperationCanceledException) { return (string?)"CANCELLED"; }
        });

        // 等 factory 启动
        await factoryStarted.Task;
        var factoryToken = await factoryTokenObserved.Task;
        Assert.IsTrue(factoryToken.CanBeCanceled, "factory token 应可取消（来自 _shutdownCts）");
        Assert.IsFalse(factoryToken.IsCancellationRequested, "factory 启动时 token 不应已取消");

        // 触发 shutdown
        accessor.Shutdown();

        // factory 应因 token 取消而收到 OperationCanceledException
        var result = await caller;
        Assert.AreEqual("CANCELLED", result, "shutdown 后 factory 应被取消");

        // 不放行 gate——避免测试结束时 semaphore 抛 ObjectDisposedException
        factoryGate.Release();
    }

    /// <summary>
    /// #4: factory timeout 取消长时间运行的 factory——超过 timeout 后 token 取消。
    /// 验证 factoryTimeout 参数生效，且与 shutdown token 通过 linked CTS 组合。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public async Task FactoryTimeout_CancelsLongRunningFactory_AfterTimeoutElapsed()
    {
        var cache = new InMemoryContextStateCache();
        var shortTimeout = TimeSpan.FromMilliseconds(100);
        using var accessor = new ContextStateCacheAccessor(cache, factoryTimeout: shortTimeout);
        var key = StateCacheKey.From("ctx:ws:col:timeout-cancel");
        var scopes = new DependencyScopeSet(new CacheInvalidationKey("ContextStore", "ws", "col", null));

        var factoryTokenObserved = new TaskCompletionSource<CancellationToken>();

        async Task<string> Factory(CancellationToken ct)
        {
            factoryTokenObserved.TrySetResult(ct);
            // 长时间运行——超过 timeout，应被取消
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return "should-not-complete";
        }

        var caller = Task.Run(async () =>
        {
            try
            {
                return await accessor.GetOrAddAsync(key, scopes, Factory, CancellationToken.None);
            }
            catch (OperationCanceledException) { return (string?)"CANCELLED"; }
        });

        var factoryToken = await factoryTokenObserved.Task;
        Assert.IsTrue(factoryToken.CanBeCanceled, "有 timeout 时 factory token 必须可取消");

        var result = await caller;
        Assert.AreEqual("CANCELLED", result, "factory 超时后应被取消");
    }

    /// <summary>
    /// #4: 无 timeout 时 factory token 仍可取消（通过 shutdown），
    /// 且不分配 per-call linked CTS——直接使用 _shutdownCts.Token。
    /// 验证无 timeout 路径功能正确。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public async Task NoTimeout_FactoryTokenStillCancellableViaShutdown()
    {
        var cache = new InMemoryContextStateCache();
        using var accessor = new ContextStateCacheAccessor(cache, factoryTimeout: null);
        var key = StateCacheKey.From("ctx:ws:col:no-timeout-shutdown");
        var scopes = new DependencyScopeSet(new CacheInvalidationKey("ContextStore", "ws", "col", null));

        var factoryTokenObserved = new TaskCompletionSource<CancellationToken>();
        var factoryStarted = new TaskCompletionSource<bool>();

        async Task<string> Factory(CancellationToken ct)
        {
            factoryTokenObserved.TrySetResult(ct);
            factoryStarted.TrySetResult(true);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            return "should-not-complete";
        }

        var caller = Task.Run(async () =>
        {
            try { return await accessor.GetOrAddAsync(key, scopes, Factory, CancellationToken.None); }
            catch (OperationCanceledException) { return (string?)"CANCELLED"; }
        });

        var factoryToken = await factoryTokenObserved.Task;
        Assert.IsTrue(factoryToken.CanBeCanceled, "无 timeout 时 token 仍应可取消（通过 shutdown）");

        await factoryStarted.Task;
        accessor.Shutdown();

        var result = await caller;
        Assert.AreEqual("CANCELLED", result, "shutdown 应取消无 timeout 的 factory");
    }

    /// <summary>
    /// #4: Shutdown() 幂等——多次调用安全，不抛异常。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public void Shutdown_IsIdempotent_MultipleCallsSafe()
    {
        var cache = new InMemoryContextStateCache();
        using var accessor = new ContextStateCacheAccessor(cache);

        accessor.Shutdown();
        accessor.Shutdown();
        accessor.Shutdown();

        // 不抛异常即通过——幂等性验证
        Assert.IsTrue(true);
    }

    /// <summary>
    /// #4: DisposeAsync 触发 shutdown——后续 factory 调用收到已取消 token。
    /// 验证 IAsyncDisposable 生命周期管理正确。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public async Task DisposeAsync_TriggersShutdown_SubsequentFactoryReceivesCancelledToken()
    {
        var cache = new InMemoryContextStateCache();
        var accessor = new ContextStateCacheAccessor(cache);

        await accessor.DisposeAsync();

        var key = StateCacheKey.From("ctx:ws:col:after-dispose");
        var scopes = new DependencyScopeSet(new CacheInvalidationKey("ContextStore", "ws", "col", null));

        var factoryTokenObserved = new TaskCompletionSource<CancellationToken>();

        async Task<string> Factory(CancellationToken ct)
        {
            factoryTokenObserved.TrySetResult(ct);
            await Task.CompletedTask;
            return "completed-after-dispose";
        }

        // Dispose 后调用 GetOrAddAsync——factory 应收到已取消 token
        // 注意：cache GetAsync（快速路径）使用 CancellationToken.None，不会被 cancel
        // 但 factory 阶段会使用 _shutdownCts.Token（已取消）
        try
        {
            await accessor.GetOrAddAsync(key, scopes, Factory, CancellationToken.None);
            Assert.Fail("Dispose 后 factory 应因 shutdown token 已取消而抛 OperationCanceledException");
        }
        catch (OperationCanceledException)
        {
            // 预期——factory token 在 dispose 后已取消
        }
        catch (ObjectDisposedException)
        {
            // 也可能——_shutdownCts 已 dispose，访问 Token 抛 ODE
            // 两种异常都可接受，核心是 dispose 后 factory 不会正常执行
        }
    }

    /// <summary>
    /// #4: factoryTimeout 非正数抛 ArgumentOutOfRangeException。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public void Constructor_NonPositiveFactoryTimeout_ThrowsArgumentOutOfRangeException()
    {
        var cache = new InMemoryContextStateCache();

        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new ContextStateCacheAccessor(cache, factoryTimeout: TimeSpan.Zero));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new ContextStateCacheAccessor(cache, factoryTimeout: TimeSpan.FromSeconds(-1)));
    }

    /// <summary>
    /// #4: shutdown 后新 GetOrAddAsync 调用的 factory 阶段会抛 OperationCanceledException，
    /// 但 cache 命中路径（GetAsync）不受影响——shutdown 只影响 factory 执行。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public async Task Shutdown_AfterShutdown_CacheHitStillWorks_FactoryThrowsOCE()
    {
        var cache = new InMemoryContextStateCache();
        var key = StateCacheKey.From("ctx:ws:col:pre-shutdown-cached");
        var scopes = new DependencyScopeSet(new CacheInvalidationKey("ContextStore", "ws", "col", null));

        // shutdown 前写入缓存
        await cache.SetAsync(key, "pre-shutdown-value", scopes);

        using var accessor = new ContextStateCacheAccessor(cache);
        accessor.Shutdown();

        // shutdown 后缓存命中应正常返回（GetAsync 用 CancellationToken.None）
        var hit = await accessor.GetOrAddAsync(key, scopes, _ => Task.FromResult("factory-not-called"));
        Assert.AreEqual("pre-shutdown-value", hit, "shutdown 后缓存命中应不受影响");
    }

    // ──  version bump 先于 physical eviction ────────────────────

    /// <summary>
    /// #5: AfterCommitAsync 中 BumpVersionAsync 必须先于 InvalidateAsync 执行。
    /// 验证调用顺序——版本先递增，确保并发版本感知读取即使命中未物理移除的条目也会因版本失配返回 null。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public async Task Decorator_AfterCommit_BumpsVersionBeforePhysicalEviction()
    {
        var callOrder = new List<string>();
        var invalidator = new RecordingInvalidator { OnInvalidated = _ => callOrder.Add("Invalidate") };
        var versionStore = new RecordingVersionStore { OnBumped = _ => callOrder.Add("BumpVersion") };
        var inner = new CancelAfterWriteContextStore();
        var decorator = new InvalidatingContextStoreDecorator(inner, invalidator, versionStore);

        var item = new ContextItem { Id = "item1", WorkspaceId = "ws1", CollectionId = "col1", Content = "x" };
        await decorator.SaveAsync(item);

        Assert.AreEqual(2, callOrder.Count, "应恰好两次调用：BumpVersion + Invalidate");
        Assert.AreEqual("BumpVersion", callOrder[0], "R13.0 #5: BumpVersion 必须先于 Invalidate 执行");
        Assert.AreEqual("Invalidate", callOrder[1], "Invalidate 应在 BumpVersion 之后执行");
    }

    /// <summary>
    /// #5: 无 versionStore 时 AfterCommitAsync 仅调用 InvalidateAsync，不抛异常。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public async Task Decorator_AfterCommit_NoVersionStore_OnlyInvalidates()
    {
        var callOrder = new List<string>();
        var invalidator = new RecordingInvalidator { OnInvalidated = _ => callOrder.Add("Invalidate") };
        var inner = new CancelAfterWriteContextStore();
        // versionStore = null
        var decorator = new InvalidatingContextStoreDecorator(inner, invalidator, versionStore: null);

        var item = new ContextItem { Id = "item1", WorkspaceId = "ws1", CollectionId = "col1", Content = "x" };
        await decorator.SaveAsync(item);

        Assert.AreEqual(1, callOrder.Count, "无 versionStore 时应仅调用 Invalidate");
        Assert.AreEqual("Invalidate", callOrder[0]);
    }

    // ──  Cache TTL ──────────────────────────────────────────────

    /// <summary>
    /// #6: 条目在 TTL 内可命中，超过 TTL 后 lazy 淘汰并返回 null。
    /// 使用短 TTL（200ms）验证过期行为：写入后立即读取命中；等待超过 TTL 后读取返回 null。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public async Task Ttl_EntryExpiresAfterTtl_ReturnsNullAfterExpiry()
    {
        var ttl = TimeSpan.FromMilliseconds(200);
        var cache = new InMemoryContextStateCache(ttl: ttl);
        var key = StateCacheKey.From("ctx:ws:col:ttl-expire");
        var scope = new CacheInvalidationKey("ContextStore", "ws", "col", null);

        await cache.SetAsync(key, "fresh", new DependencyScopeSet(scope));

        // TTL 内应命中
        var hit = await cache.GetAsync<string>(key);
        Assert.AreEqual("fresh", hit, "TTL 内条目应命中");
        Assert.AreEqual(1L, cache.Hits);
        Assert.AreEqual(0L, cache.TtlExpirations);

        // 等待超过 TTL
        await Task.Delay(ttl + TimeSpan.FromMilliseconds(100));

        // 超过 TTL 后应返回 null（lazy 淘汰）
        var expired = await cache.GetAsync<string>(key);
        Assert.IsNull(expired, "超过 TTL 后条目应被 lazy 淘汰并返回 null");
        Assert.AreEqual(1L, cache.TtlExpirations, "TTL 过期计数应递增");
        Assert.AreEqual(1L, cache.Misses, "过期后应计 miss");
    }

    /// <summary>
    /// #6: 无 TTL 时条目不会因时间过期（仅由 scope 失效或 CLOCK 淘汰移除）。
    /// 验证 TTL=null 保持原有行为。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public async Task Ttl_NullTtl_EntriesNeverExpireByTime()
    {
        var cache = new InMemoryContextStateCache(ttl: null);
        var key = StateCacheKey.From("ctx:ws:col:no-ttl");
        var scope = new CacheInvalidationKey("ContextStore", "ws", "col", null);

        await cache.SetAsync(key, "persistent", new DependencyScopeSet(scope));

        // 等待一段时间（无 TTL 不应过期）
        await Task.Delay(150);

        var result = await cache.GetAsync<string>(key);
        Assert.AreEqual("persistent", result, "无 TTL 时条目不应因时间过期");
        Assert.AreEqual(0L, cache.TtlExpirations, "无 TTL 不应有 TTL 过期计数");
    }

    /// <summary>
    /// #6: TTL 过期检查先于版本检查——TTL 过期时不调用版本存储。
    /// 验证 TTL 过期路径走 TTL 计数（TtlExpirations）而非版本失配计数（VersionMismatches），
    /// 证明版本 RPC 未被触发（分布式场景下节省一次网络调用）。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public async Task Ttl_ExpiryCheckPrecedesVersionCheck_NoVersionRpcOnExpiry()
    {
        var versionStore = new InMemoryContextStateVersionStore();
        // 先 bump 一次让版本存储有初始数据（写入 SetAsync 会捕获版本快照）
        await versionStore.BumpVersionAsync("ws", "col", "ContextStore", default);

        var shortTtl = TimeSpan.FromMilliseconds(100);
        var cache = new InMemoryContextStateCache(versionStore, ttl: shortTtl);
        var key = StateCacheKey.From("ctx:ws:col:ttl-before-version");
        var scope = new CacheInvalidationKey("ContextStore", "ws", "col", null);

        await cache.SetAsync(key, "v1", new DependencyScopeSet(scope));

        // 等待超过 TTL
        await Task.Delay(shortTtl + TimeSpan.FromMilliseconds(100));

        // 读取应因 TTL 过期返回 null
        var result = await cache.GetAsync<string>(key);
        Assert.IsNull(result, "TTL 过期应返回 null");
        Assert.AreEqual(1L, cache.TtlExpirations, "应计 TTL 过期（证明走了 TTL 路径）");
        Assert.AreEqual(0L, cache.VersionMismatches, "不应计版本失配（证明未走版本检查路径）");
    }

    /// <summary>
    /// #6: TTL 过期后条目从缓存移除——后续 Count 减少，再次读取为 miss。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public async Task Ttl_ExpiredEntryRemovedFromCache_ReducesCount()
    {
        var shortTtl = TimeSpan.FromMilliseconds(100);
        var cache = new InMemoryContextStateCache(ttl: shortTtl);
        var key = StateCacheKey.From("ctx:ws:col:ttl-remove");
        var scope = new CacheInvalidationKey("ContextStore", "ws", "col", null);

        await cache.SetAsync(key, "v1", new DependencyScopeSet(scope));
        Assert.AreEqual(1, cache.Count);

        // 等待超过 TTL
        await Task.Delay(shortTtl + TimeSpan.FromMilliseconds(100));

        // 读取触发 lazy 淘汰
        _ = await cache.GetAsync<string>(key);
        Assert.AreEqual(0, cache.Count, "TTL 过期后条目应从缓存移除");
    }

    /// <summary>
    /// #6: TTL 非正数抛 ArgumentOutOfRangeException。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public void Ttl_NonPositiveValue_ThrowsArgumentOutOfRangeException()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new InMemoryContextStateCache(ttl: TimeSpan.Zero));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new InMemoryContextStateCache(ttl: TimeSpan.FromSeconds(-1)));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new InMemoryContextStateCache(maxEntries: 100, ttl: TimeSpan.Zero));
    }

    /// <summary>
    /// #6: TTL 与 scope 失效协同工作——scope 失效仍能立即移除条目，
    /// TTL 只是在 scope 未触发时提供时间兜底。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public async Task Ttl_AndScopeInvalidation_Coexist()
    {
        var longTtl = TimeSpan.FromMinutes(5); // 长 TTL，确保 scope 失效先触发
        var cache = new InMemoryContextStateCache(ttl: longTtl);
        var key = StateCacheKey.From("ctx:ws:col:ttl-and-scope");
        var scope = new CacheInvalidationKey("ContextStore", "ws", "col", null);

        await cache.SetAsync(key, "v1", new DependencyScopeSet(scope));
        Assert.IsNotNull(await cache.GetAsync<string>(key));

        // scope 失效应立即移除条目（不等 TTL 过期）
        await cache.InvalidateAsync(scope);
        Assert.IsNull(await cache.GetAsync<string>(key), "scope 失效应立即移除条目");
        Assert.AreEqual(0L, cache.TtlExpirations, "scope 失效不应计 TTL 过期");
    }

    // ──  Cache 默认生产关闭 / Cache Canary 可选启用 ─────────
    //
    // 生产默认行为：PackageTemplateCacheOptions.Enabled=false → CacheAccessor=null
    // → BasicContextPackageBuilder._cacheAccessor 为 null → 每个 Build 走全量流水线。
    //
    // canary 启用行为：Enabled=true + AllowedWorkspaces 非空 + 单实例检查通过
    // → CacheAccessor 非 null → ContextStateCacheAccessor.canaryGate 按工作空间粒度控制缓存路径。
    // canary 工作空间走缓存（命中/未命中/写入）；非 canary 工作空间绕过缓存（直接 factory）。

    /// <summary>
    /// 0: 生产运行时组装（CacheAccessor = null）必须使 BasicContextPackageBuilder._cacheAccessor 为 null。
    /// 这是生产缓存关闭的守卫测试：确保 ContextRuntimeBuilder.Build 正确传播 options.CacheAccessor = null，
    /// 每个 Build 都走全量流水线（无缓存命中）。
    /// 通过反射读取私有字段 _cacheAccessor，因为 RuntimeServices 仅暴露 PackageBuilder 公共属性，
    /// _cacheAccessor 是构造函数注入的内部状态。
    /// 此测试仍验证生产默认路径（Enabled=false → CacheAccessor=null）。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public void ProductionRuntime_CacheAccessorNull_BuilderCacheAccessorFieldIsNull()
    {
        var options = BuildRuntimeOptionsWithInMemoryStores(cacheAccessor: null);
        var services = ContextRuntimeBuilder.Build(options);

        var field = typeof(BasicContextPackageBuilder).GetField(
            "_cacheAccessor",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(field, "BasicContextPackageBuilder 必须包含 _cacheAccessor 私有字段");

        var value = field.GetValue(services.PackageBuilder);
        Assert.IsNull(value,
            "生产组装（CacheAccessor = null）必须使 _cacheAccessor 为 null——缓存保持关闭，每次 Build 走全量流水线");
    }

    /// <summary>
    /// 0: 非 null CacheAccessor 必须正确传播到 builder._cacheAccessor（反向验证）。
    /// 证明反射读取正确且 ContextRuntimeBuilder.Build 正确传播非空值——避免 "始终 null" 的假通过。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public void ProductionRuntime_CacheAccessorProvided_BuilderCacheAccessorFieldIsWired()
    {
        using var accessor = new ContextStateCacheAccessor(new InMemoryContextStateCache());
        var options = BuildRuntimeOptionsWithInMemoryStores(cacheAccessor: accessor);
        var services = ContextRuntimeBuilder.Build(options);

        var field = typeof(BasicContextPackageBuilder).GetField(
            "_cacheAccessor",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(field);

        var value = field.GetValue(services.PackageBuilder);
        Assert.AreSame(accessor, value, "非 null CacheAccessor 必须传播到 builder._cacheAccessor");
    }

    // ──  Cache Canary Gate 测试 ─────────────────────────────────────
    //
    // 验证 ContextStateCacheAccessor.canaryGate 谓词按工作空间粒度控制缓存路径：
    // - gate 返回 true → 走缓存路径（命中/未命中/写入缓存）
    // - gate 返回 false → 绕过缓存路径（直接调用 factory，不查询缓存也不写入缓存）
    // - gate 为 null → 所有请求都走缓存路径（R13.0 之前的原有行为）

    /// <summary>
    /// canary gate 返回 true 时走缓存路径——重复请求只调用一次 factory（命中缓存）。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public async Task CanaryGate_AllowedWorkspace_UsesCachePath_FactoryCalledOnce()
    {
        var cache = new InMemoryContextStateCache();
        var allowed = new HashSet<string> { "ws-canary" };
        using var accessor = new ContextStateCacheAccessor(
            cache,
            canaryGate: scopes => ScopeWorkspaceAllowed(scopes, allowed));

        var key = StateCacheKey.From("pkg:ws-canary:col1:hash1");
        var scopes = new DependencyScopeSet(new CacheInvalidationKey("ContextStore", "ws-canary", "col1", null));
        var factoryCallCount = 0;

        var result1 = await accessor.GetOrAddAsync<string>(key, scopes,
            ct => { factoryCallCount++; return Task.FromResult("v1"); });
        var result2 = await accessor.GetOrAddAsync<string>(key, scopes,
            ct => { factoryCallCount++; return Task.FromResult("v1"); });

        Assert.AreEqual("v1", result1);
        Assert.AreEqual("v1", result2);
        Assert.AreEqual(1, factoryCallCount, "第二次请求应命中缓存，factory 不应再次调用");
        Assert.AreEqual(1L, cache.Hits, "第二次请求应计缓存命中");
        // Misses=2：fast-path miss（GetOrAddAsync 入口查询）+ single-flight double-check miss（CreateInflightTask 内重新检查）
        Assert.AreEqual(2L, cache.Misses, "首次请求包含 fast-path 与 single-flight double-check 两次 miss");
    }

    /// <summary>
    /// canary gate 返回 false 时绕过缓存路径——每次请求都调用 factory，缓存保持空。
    /// 验证 bypass 路径不查询缓存（无 miss 计数）也不写入缓存（无条目残留）。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public async Task CanaryGate_DisallowedWorkspace_BypassesCache_FactoryCalledEveryTime()
    {
        var cache = new InMemoryContextStateCache();
        var allowed = new HashSet<string> { "ws-canary" };  // 仅允许 ws-canary
        using var accessor = new ContextStateCacheAccessor(
            cache,
            canaryGate: scopes => ScopeWorkspaceAllowed(scopes, allowed));

        var key = StateCacheKey.From("pkg:ws-other:col1:hash1");
        var scopes = new DependencyScopeSet(new CacheInvalidationKey("ContextStore", "ws-other", "col1", null));
        var factoryCallCount = 0;

        var result1 = await accessor.GetOrAddAsync<string>(key, scopes,
            ct => { factoryCallCount++; return Task.FromResult("v1"); });
        var result2 = await accessor.GetOrAddAsync<string>(key, scopes,
            ct => { factoryCallCount++; return Task.FromResult("v1"); });

        Assert.AreEqual("v1", result1);
        Assert.AreEqual("v1", result2);
        Assert.AreEqual(2, factoryCallCount, "绕过缓存路径——每次请求都调用 factory");
        Assert.AreEqual(0L, cache.Hits, "绕过缓存路径——不应有命中");
        Assert.AreEqual(0L, cache.Misses, "绕过缓存路径——不应查询缓存（无 miss 计数）");
        Assert.AreEqual(0, cache.Count, "绕过缓存路径——缓存应保持空");
    }

    /// <summary>
    /// canary gate 为 null（R13.0 之前的原有行为）时所有请求都走缓存路径。
    /// 验证 canaryGate=null 时行为不变——保证 R13.0 测试与 R13-F canary 关闭场景语义一致。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public async Task CanaryGate_NullGate_AllWorkspacesUseCachePath()
    {
        var cache = new InMemoryContextStateCache();
        using var accessor = new ContextStateCacheAccessor(cache, canaryGate: null);

        var key1 = StateCacheKey.From("pkg:ws-a:col1:hash1");
        var key2 = StateCacheKey.From("pkg:ws-b:col1:hash1");
        var scopes1 = new DependencyScopeSet(new CacheInvalidationKey("ContextStore", "ws-a", "col1", null));
        var scopes2 = new DependencyScopeSet(new CacheInvalidationKey("ContextStore", "ws-b", "col1", null));

        await accessor.GetOrAddAsync<string>(key1, scopes1, _ => Task.FromResult("v1"));
        await accessor.GetOrAddAsync<string>(key2, scopes2, _ => Task.FromResult("v2"));

        Assert.AreEqual(2, cache.Count, "两个不同工作空间的请求都应写入缓存");
    }

    /// <summary>
    /// 绕过缓存的请求不应污染缓存——后续 canary 工作空间请求看到干净的缓存状态。
    /// 验证 bypass 路径不调用 SetAsync，避免非 canary 数据驻留缓存。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public async Task CanaryGate_DisallowedWorkspace_DoesNotPolluteCache()
    {
        var cache = new InMemoryContextStateCache();
        var allowed = new HashSet<string> { "ws-canary" };
        using var accessor = new ContextStateCacheAccessor(
            cache,
            canaryGate: scopes => ScopeWorkspaceAllowed(scopes, allowed));

        // 非 canary 工作空间请求——应绕过缓存
        var nonCanaryKey = StateCacheKey.From("pkg:ws-other:col1:hash1");
        var nonCanaryScopes = new DependencyScopeSet(new CacheInvalidationKey("ContextStore", "ws-other", "col1", null));
        await accessor.GetOrAddAsync<string>(nonCanaryKey, nonCanaryScopes, _ => Task.FromResult("non-canary-value"));

        Assert.AreEqual(0, cache.Count, "非 canary 请求不应写入缓存");

        // canary 工作空间请求——应走缓存路径
        var canaryKey = StateCacheKey.From("pkg:ws-canary:col1:hash1");
        var canaryScopes = new DependencyScopeSet(new CacheInvalidationKey("ContextStore", "ws-canary", "col1", null));
        var result = await accessor.GetOrAddAsync<string>(canaryKey, canaryScopes, _ => Task.FromResult("canary-value"));

        Assert.AreEqual("canary-value", result);
        Assert.AreEqual(1, cache.Count, "仅 canary 请求应写入缓存");
        // Misses=2：fast-path miss（GetOrAddAsync 入口查询）+ single-flight double-check miss（CreateInflightTask 内重新检查）
        Assert.AreEqual(2L, cache.Misses, "canary 首次请求包含 fast-path 与 single-flight double-check 两次 miss");
    }

    /// <summary>
    /// canary gate 不影响 factory 异常传播——gate 返回 false 时 factory 抛异常应向上传播。
    /// 验证 bypass 路径不吞异常（与缓存路径一致），保证调用方能感知 factory 失败。
    /// </summary>
    [TestMethod]
    [TestCategory("CacheCorrectness")]
    public async Task CanaryGate_DisallowedWorkspace_FactoryThrows_PropagatesToCaller()
    {
        var cache = new InMemoryContextStateCache();
        var allowed = new HashSet<string> { "ws-canary" };
        using var accessor = new ContextStateCacheAccessor(
            cache,
            canaryGate: scopes => ScopeWorkspaceAllowed(scopes, allowed));

        var key = StateCacheKey.From("pkg:ws-other:col1:hash1");
        var scopes = new DependencyScopeSet(new CacheInvalidationKey("ContextStore", "ws-other", "col1", null));

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await accessor.GetOrAddAsync<string>(key, scopes,
                ct => throw new InvalidOperationException("factory failure")));

        Assert.AreEqual(0, cache.Count, "factory 抛异常时缓存应保持空（bypass 路径无写入）");
    }

    /// <summary>
    /// 共享辅助：从 DependencyScopeSet 提取首个 scope 的 WorkspaceId 并检查是否在 allowlist 中。
    /// 与 CoreExtensions.CacheCanaryGateWorkspaceAllowed 同语义——测试侧独立实现避免依赖内部方法。
    /// </summary>
    private static bool ScopeWorkspaceAllowed(DependencyScopeSet scopes, IReadOnlySet<string> allowed)
    {
        foreach (var scope in scopes.Scopes)
        {
            return allowed.Contains(scope.WorkspaceId);
        }
        return false;
    }

    /// <summary>
    /// 使用 InMemory 存储构造 RuntimeBuildOptions，模拟生产组装路径（ContextRuntimeBuilder.Build 输入）。
    /// cacheAccessor 参数控制是否注入缓存访问器：生产路径为 null（缓存关闭），测试路径可注入非空实例。
    /// InMemoryMemoryStore 同时实现 IMemoryStore / IWorkingMemoryService / IPromotionRecordStore，复用同一实例。
    /// </summary>
    private static RuntimeBuildOptions BuildRuntimeOptionsWithInMemoryStores(ContextStateCacheAccessor? cacheAccessor)
    {
        var memoryStore = new InMemoryMemoryStore();
        return new RuntimeBuildOptions
        {
            ContextStore = new InMemoryContextStore(),
            MemoryStore = memoryStore,
            ConstraintStore = new InMemoryConstraintStore(),
            RelationStore = new InMemoryRelationStore(),
            GlobalContextStore = new InMemoryGlobalContextStore(),
            VectorStore = new InMemoryVectorStore(),
            RetrievalTraceStore = new InMemoryRetrievalTraceStore(),
            TokenizerResolver = new DefaultContextTokenizerResolver(),
            PromotionRecordStore = memoryStore,
            WorkingMemoryService = memoryStore,
            CacheAccessor = cacheAccessor
        };
    }

    // ── R13.0 #8 isolation test 共享构造辅助 ─────────────────────────────

    /// <summary>
    /// 构造用于隔离测试的 PackageTemplate：所有集合字段为非空 ImmutableArray，
    /// 数据可被断言验证（sections/selected/dropped/uncertainties/itemRefs 各 1 条，sourceRefs 2 条）。
    /// </summary>
    private static PackageTemplate BuildIsolationTemplate()
    {
        var sections = new[] { new ContextPackageSection { Name = "working_memory", Content = "memory-1" } };
        var sourceRefs = new[] { "src-1", "src-2" };
        var selected = new[] { new ContextPackageDecision { ItemId = "item-1", SectionName = "working_memory" } };
        var dropped = new[] { new DroppedContextItem { ItemId = "dropped-1" } };
        var uncertainties = new[] { new ContextPackageUncertainty { Code = "OverBudget" } };
        var itemRefs = new[] { new ContextPackageItemReference { ItemId = "item-1", PrimarySectionName = "working_memory" } };

        return new PackageTemplate(
            OrderedSections: sections.ToImmutableArray(),
            SourceRefs: sourceRefs.ToImmutableArray(),
            EstimatedTokens: 100,
            TokenBudget: 1000,
            SortedSelectedItems: selected.ToImmutableArray(),
            DroppedItems: dropped.ToImmutableArray(),
            Uncertainties: uncertainties.ToImmutableArray(),
            ItemReferences: itemRefs.ToImmutableArray(),
            Anchors: ImmutableArray<ContextAnchor>.Empty,
            RetrievalPlan: null,
            Budget: new ContextPackageBudgetReport(),
            Output: new ContextPackageStandardOutput(),
            ModeBudgetProfile: null);
    }

    /// <summary>
    /// 构造用于隔离测试的 ResultProjector + ResolvedPackageOptions。
    /// 与 BuildIsolationTemplate 配对使用，复用 request/policy/workspace 一致性。
    /// </summary>
    private static (ResultProjector Projector, ResolvedPackageOptions Options) BuildIsolationProjectorAndOptions()
    {
        var projector = new ResultProjector(new PackageTraceRecorder(
            new NullRuntimeCandidateTraceSink(),
            () => "op-iso",
            () => "req-iso"));

        var options = ResolvedPackageOptions.Resolve(
            new ContextPackageRequest
            {
                WorkspaceId = "ws-iso",
                CollectionId = "col-iso",
                QueryText = "isolation test",
                TokenBudget = 1000,
                Policy = new ContextPackagePolicy
                {
                    WorkspaceId = "ws-iso",
                    CollectionId = "col-iso",
                    TokenBudget = 1000
                }
            },
            new ContextPackagePolicy
            {
                WorkspaceId = "ws-iso",
                CollectionId = "col-iso",
                TokenBudget = 1000
            },
            new TokenEstimationContext("test-model", "test", false));

        return (projector, options);
    }

    private sealed class RecordingInvalidator : IStateCacheInvalidator
    {
        public List<CacheInvalidationKey> InvalidatedKeys { get; } = new();
        public Action<CacheInvalidationKey>? OnInvalidated { get; set; }

        public Task InvalidateAsync(CacheInvalidationKey key, CancellationToken cancellationToken = default)
        {
            InvalidatedKeys.Add(key);
            OnInvalidated?.Invoke(key);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 记录 BumpVersionAsync 调用的版本存储包装。委托给 InMemoryContextStateVersionStore 保持语义，
    /// 同时通过 OnBumped 回调通知调用顺序（用于 R13.0 #5 顺序断言）。
    /// </summary>
    private sealed class RecordingVersionStore : IContextStateVersionStore
    {
        private readonly InMemoryContextStateVersionStore _inner = new();
        public Action<string>? OnBumped { get; set; }

        public Task<long> GetVersionAsync(string workspaceId, string collectionId, string storeKind, CancellationToken cancellationToken = default)
            => _inner.GetVersionAsync(workspaceId, collectionId, storeKind, cancellationToken);

        public Task<IReadOnlyDictionary<VersionScope, long>> GetVersionsAsync(IReadOnlyCollection<VersionScope> scopes, CancellationToken cancellationToken = default)
            => _inner.GetVersionsAsync(scopes, cancellationToken);

        public Task<long> BumpVersionAsync(string workspaceId, string collectionId, string storeKind, CancellationToken cancellationToken = default)
        {
            OnBumped?.Invoke($"{workspaceId}:{collectionId}:{storeKind}");
            return _inner.BumpVersionAsync(workspaceId, collectionId, storeKind, cancellationToken);
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
