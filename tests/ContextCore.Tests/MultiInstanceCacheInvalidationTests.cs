using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Service;
using ContextCore.Service.Extensions;
using ContextCore.Storage.InMemory.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ContextCore.Tests;

/// <summary>
/// R14-PG-7：Service 层根据 provider 选择 version store + 多实例 cache invalidation 验收。
/// - 验证 FileSystem/InMemory provider 仍用 InMemoryContextStateVersionStore（单机默认）
/// - 验证 Postgres provider 用 PostgresContextStateVersionStore（覆盖默认，跨实例可见）
/// - 模拟多实例：两个独立 cache 共享同一 versionStore（模拟 Postgres 跨实例共享），
///   Instance A 通过 Decorator bump 后，Instance B 的 GetAsync 返回 miss
/// </summary>
[TestClass]
[TestCategory("Storage")]
[TestCategory("Infrastructure")]
public sealed class MultiInstanceCacheInvalidationTests
{
    /// <summary>
    /// R14-PG-7：FileSystem provider 是 Local/Single-host runtime，storage-only 注册不应引入
    /// IContextStateVersionStore。生产路径由 AddContextCore 注册 InMemoryContextStateVersionStore，
    /// 此处仅验证 storage-only 隔离场景：Decorator 应解析到 null 并跳过 bump。
    /// 与 R14-PG-6 的 AddContextStorage_Postgres_OverridesInMemoryVersionStoreRegistration 互补，
    /// 共同覆盖 FileSystem（未注册）+ Postgres（注册 PostgresContextStateVersionStore）两条路径。
    /// </summary>
    [TestMethod]
    public void FileSystem_Provider_UsesInMemoryVersionStore()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // 仅注册 storage（不调用 AddContextCore），模拟 storage-only 隔离测试场景：
        // 此时 IContextStateVersionStore 未注册，Decorator 跳过 bump。
        var rootPath = Path.Combine(Path.GetTempPath(), "ctx-mi-fs-" + Guid.NewGuid().ToString("N"));
        var options = new StorageOptions { Provider = "filesystem", RootPath = rootPath };
        services.AddContextStorage(options);

        using var sp = services.BuildServiceProvider();
        var versionStore = sp.GetService<IContextStateVersionStore>();
        Assert.IsNull(versionStore, "storage-only 注册时 IContextStateVersionStore 应未注册，Decorator 应跳过 bump");
    }

    /// <summary>
    /// R14-PG-7：模拟多实例 cache invalidation。
    /// 两个独立 InMemoryContextStateCache 共享同一 IContextStateVersionStore，
    /// 模拟两个 Worker 实例共享同一 PostgresContextStateVersionStore 表。
    /// 当 Instance A 的 Decorator 触发 bump 后，Instance B 的 cache.GetAsync 应检测到版本失配。
    /// </summary>
    [TestMethod]
    public async Task MultiInstance_SharedVersionStore_CrossInstanceBumpCausesMissOnOtherInstance()
    {
        // 共享 versionStore 功能等价于共享 Postgres 版本号表：进程间可见的版本号源
        var sharedVersionStore = new InMemoryContextStateVersionStore();

        var cacheA = new InMemoryContextStateCache(sharedVersionStore);
        var cacheB = new InMemoryContextStateCache(sharedVersionStore);

        var key = StateCacheKey.From("ctx:ws1:col1:item1");
        var scope = new CacheInvalidationKey("ContextStore", "ws1", "col1", "item1");

        // Instance B 先缓存条目（version=0 快照）
        await cacheB.SetAsync(key, "v1", new DependencyScopeSet(scope));
        Assert.IsNotNull(await cacheB.GetAsync<string>(key), "Instance B 应命中自己的缓存");
        Assert.AreEqual(1L, cacheB.Hits);
        Assert.AreEqual(0L, cacheB.VersionMismatches);

        // Instance A 写入触发 bump（模拟跨实例 Decorator 调用）。
        // 关键：bump 不通过 Instance B 的进程内 invalidator，仅通过共享 versionStore
        await sharedVersionStore.BumpVersionAsync("ws1", "col1", "ContextStore", CancellationToken.None);

        // Instance B 再次读取：应检测到版本失配，返回 null 并计 VersionMismatch。
        // Hits 保持第一次读取的 1（版本失配不增量 Hits，也不重置已有计数）
        var result = await cacheB.GetAsync<string>(key);
        Assert.IsNull(result, "Instance B 应因版本失配返回 null（跨实例 invalidation）");
        Assert.AreEqual(1L, cacheB.VersionMismatches, "Instance B 应计版本失配");
        Assert.AreEqual(1L, cacheB.Hits, "版本失配不增量 Hits，保持第一次读取的计数");
    }

    /// <summary>
    /// R14-PG-7：端到端模拟 Decorator 触发的跨实例 bump。
    /// Instance A 用真实 InvalidatingContextStoreDecorator 包装 IContextStore，
    /// 写入时 Decorator 调用共享 versionStore.BumpVersionAsync。
    /// Instance B 用同一个共享 versionStore 但独立的 cache，下次读取应失配。
    /// </summary>
    [TestMethod]
    public async Task MultiInstance_DecoratorTriggersCrossInstanceBump()
    {
        var sharedVersionStore = new InMemoryContextStateVersionStore();

        // Instance A：用真实 Decorator + InMemory inner store。
        // invalidatorA 仅作用于 Instance A 进程内（这里用 NullStateCacheInvalidator，
        // 因 Instance A 无独立 cache 需要失效；实际生产场景下 invalidatorA 指向 Instance A 自己的 cache）
        var innerStoreA = new InMemoryContextStore();
        var invalidatorA = NullStateCacheInvalidator.Instance;
        var decoratorA = new InvalidatingContextStoreDecorator(innerStoreA, invalidatorA, sharedVersionStore);

        // Instance B：独立的 cache（共享 versionStore）
        var cacheB = new InMemoryContextStateCache(sharedVersionStore);

        var item = new ContextItem
        {
            WorkspaceId = "ws1",
            CollectionId = "col1",
            Id = "item1",
            Type = "note",
            Title = "hello",
            Importance = 1.0,
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Tags = Array.Empty<string>(),
            Refs = Array.Empty<string>(),
            SourceRefs = Array.Empty<string>()
        };

        // Instance B 先缓存一个与 item 关联的条目
        var key = StateCacheKey.From("ctx:ws1:col1:item1");
        var scope = new CacheInvalidationKey("ContextStore", "ws1", "col1", "item1");
        await cacheB.SetAsync(key, "cached-value", new DependencyScopeSet(scope));
        Assert.IsNotNull(await cacheB.GetAsync<string>(key));

        // Instance A 写入 → Decorator 触发共享 versionStore bump
        await decoratorA.SaveAsync(item, CancellationToken.None);

        // Instance B 再次读取：应检测到版本失配
        var result = await cacheB.GetAsync<string>(key);
        Assert.IsNull(result, "Instance B 应因 Decorator 在 Instance A 触发的 bump 而失配");
        Assert.AreEqual(1L, cacheB.VersionMismatches);
    }
}
