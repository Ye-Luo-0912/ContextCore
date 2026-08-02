using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Storage.FileSystem;

namespace ContextCore.Tests;

/// <summary>
/// 验证 BoundedFanout 节流和 RetrievalFanoutOptions 自动解析。
/// 在 Batch API 落地前，所有 Task.WhenAll 回退路径必须经过 SemaphoreSlim 上限，避免 Postgres 连接池击穿。
/// </summary>
[TestClass]
[TestCategory("Infrastructure")]
public sealed class BoundedFanoutTests
{
    /// <summary>
    /// maxConcurrency=1 时强制串行：所有任务的执行不重叠。
    /// </summary>
    [TestMethod]
    public async Task WhenAllAsync_MaxConcurrency1_ExecutesSerially()
    {
        var concurrent = 0;
        var maxObserved = 0;
        var gate = new object();

        var results = await BoundedFanout.WhenAllAsync(
            Enumerable.Range(0, 5),
            async (n, ct) =>
            {
                Interlocked.Increment(ref concurrent);
                lock (gate)
                {
                    if (concurrent > maxObserved) maxObserved = concurrent;
                }

                await Task.Delay(20, ct);
                Interlocked.Decrement(ref concurrent);
                return n * 10;
            },
            maxConcurrency: 1,
            CancellationToken.None);

        CollectionAssert.AreEqual(new[] { 0, 10, 20, 30, 40 }, results);
        Assert.AreEqual(1, maxObserved, "maxConcurrency=1 必须保证串行执行");
    }

    /// <summary>
    /// maxConcurrency 较小但输入数量较大时，并发上限严格不超 maxConcurrency。
    /// </summary>
    [TestMethod]
    public async Task WhenAllAsync_ThrottlesConcurrencyToConfiguredLimit()
    {
        const int inputCount = 20;
        const int maxConcurrency = 3;
        var concurrent = 0;
        var maxObserved = 0;
        var gate = new object();

        var results = await BoundedFanout.WhenAllAsync(
            Enumerable.Range(0, inputCount),
            async (n, ct) =>
            {
                Interlocked.Increment(ref concurrent);
                lock (gate)
                {
                    if (concurrent > maxObserved) maxObserved = concurrent;
                }

                await Task.Delay(15, ct);
                Interlocked.Decrement(ref concurrent);
                return n;
            },
            maxConcurrency,
            CancellationToken.None);

        Assert.AreEqual(inputCount, results.Length, "结果数量必须等于输入数量");
        CollectionAssert.AreEqual(Enumerable.Range(0, inputCount).ToArray(), results, "结果必须按输入顺序返回");
        Assert.IsTrue(maxObserved <= maxConcurrency, $"并发不应超过 {maxConcurrency}，实际 {maxObserved}");
        Assert.IsTrue(maxObserved >= 2, $"应观察到实际并发，实际最大 {maxObserved}");
    }

    /// <summary>
    /// 输入数量 ≤ maxConcurrency 时走快速 Task.WhenAll 路径，但仍正确返回。
    /// </summary>
    [TestMethod]
    public async Task WhenAllAsync_InputFitsBudget_UsesFastPathDirectly()
    {
        var results = await BoundedFanout.WhenAllAsync(
            new[] { "a", "b", "c" },
            (s, ct) => Task.FromResult(s.ToUpperInvariant()),
            maxConcurrency: 8,
            CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "A", "B", "C" }, results);
    }

    /// <summary>
    /// 空输入返回空数组，不分配 SemaphoreSlim。
    /// </summary>
    [TestMethod]
    public async Task WhenAllAsync_EmptyInput_ReturnsEmptyArray()
    {
        var results = await BoundedFanout.WhenAllAsync(
            Array.Empty<int>(),
            (n, ct) => Task.FromResult(n),
            maxConcurrency: 4,
            CancellationToken.None);

        Assert.AreEqual(0, results.Length);
    }

    /// <summary>
    /// selector 内的异常应通过 Task.WhenAll 聚合后抛出（不丢失异常）。
    /// </summary>
    [TestMethod]
    public async Task WhenAllAsync_ExceptionInSelector_PropagatesViaWhenAll()
    {
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await BoundedFanout.WhenAllAsync(
                new[] { 1, 2, 3 },
                async (n, ct) =>
                {
                    await Task.CompletedTask;
                    if (n == 2)
                    {
                        throw new InvalidOperationException("boom");
                    }
                    return n;
                },
                maxConcurrency: 2,
                CancellationToken.None);
        });
    }

    /// <summary>
    /// 取消令牌触发时，节流路径应抛出 OperationCanceledException 或其派生类
    /// （Task.WhenAll 聚合后通常包装为 TaskCanceledException）。
    /// </summary>
    [TestMethod]
    public async Task WhenAllAsync_CancellationRequested_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // TaskCanceledException 派生自 OperationCanceledException，使用基类捕获更稳健
        OperationCanceledException? thrown = null;
        try
        {
            await BoundedFanout.WhenAllAsync(
                Enumerable.Range(0, 20),
                async (n, ct) =>
                {
                    await Task.Delay(100, ct);
                    return n;
                },
                maxConcurrency: 2,
                cts.Token);
        }
        catch (OperationCanceledException ex)
        {
            thrown = ex;
        }

        Assert.IsNotNull(thrown, "取消时应抛出 OperationCanceledException 或其派生类");
    }
}

/// <summary>
/// 验证 RetrievalFanoutOptions.Resolve 按 store namespace 自动推断 fanout 上限。
/// 扩展为优先消费 IStoreRuntimeCapabilities，namespace 推断退化为回退路径。
/// </summary>
[TestClass]
[TestCategory("Infrastructure")]
public sealed class RetrievalFanoutOptionsTests
{
    [TestMethod]
    public void Resolve_WithFileSystemStore_ReturnsFileSystemFanout()
    {
        var storage = new FileStorageOptions
        {
            RootPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fanout-test-" + Guid.NewGuid().ToString("N"))
        };
        var store = new ContextCore.Storage.FileSystem.Stores.FileContextStore(storage);

        var options = RetrievalFanoutOptions.Resolve(store, memoryStore: null);

        Assert.AreEqual(2, options.MaxReadFanout, "FileSystem store 应推断为 2");
    }

    [TestMethod]
    public void Resolve_WithInMemoryStore_ReturnsInMemoryFanout()
    {
        var store = new ContextCore.Storage.InMemory.Stores.InMemoryContextStore();

        var options = RetrievalFanoutOptions.Resolve(store, memoryStore: null);

        Assert.AreEqual(16, options.MaxReadFanout, "InMemory store 应推断为 16");
    }

    [TestMethod]
    public void Resolve_WithUnknownStore_ReturnsConservativeDefault()
    {
        var store = new FakeUnknownNamespaceStore();

        var options = RetrievalFanoutOptions.Resolve(store, memoryStore: null);

        Assert.AreEqual(4, options.MaxReadFanout, "未知 namespace 的 store 应保守推断为 4");
    }

    [TestMethod]
    public void Resolve_WithMixedStores_TakesMin()
    {
        // InMemory(16) 与 FileSystem(2) 混合应取 min=2
        var contextStore = new ContextCore.Storage.InMemory.Stores.InMemoryContextStore();
        var storage = new FileStorageOptions
        {
            RootPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fanout-test-mem-" + Guid.NewGuid().ToString("N"))
        };
        var memoryStore = new ContextCore.Storage.FileSystem.Stores.FileMemoryStore(storage);

        var options = RetrievalFanoutOptions.Resolve(contextStore, memoryStore);

        Assert.AreEqual(2, options.MaxReadFanout, "混合 store 应取 min");
    }

    [TestMethod]
    public void Resolve_WithBothNullStores_ReturnsDefault()
    {
        var options = RetrievalFanoutOptions.Resolve(contextStore: null, memoryStore: null);

        Assert.AreEqual(RetrievalFanoutOptions.Default.MaxReadFanout, options.MaxReadFanout);
    }

    [TestMethod]
    public void Resolve_WithMemoryStoreNull_UsesContextStoreOnly()
    {
        // memoryStore 为 null 时不参与 min 计算
        var contextStore = new ContextCore.Storage.InMemory.Stores.InMemoryContextStore();

        var options = RetrievalFanoutOptions.Resolve(contextStore, memoryStore: null);

        Assert.AreEqual(16, options.MaxReadFanout, "memoryStore 为 null 时应按 contextStore 推断");
    }

    // --- R13.3 #2：能力驱动路径（FromProfile）---

    /// <summary>
    /// FromProfile：InMemory 预设的 RecommendedReadFanout=16，应被原样采用。
    /// </summary>
    [TestMethod]
    public void FromProfile_InMemory_UsesRecommendedReadFanout()
    {
        var options = RetrievalFanoutOptions.FromProfile(StorageExecutionProfile.InMemory);

        Assert.AreEqual(16, options.MaxReadFanout);
    }

    /// <summary>
    /// FromProfile：FileSystem 预设的 RecommendedReadFanout=2，应被原样采用。
    /// </summary>
    [TestMethod]
    public void FromProfile_FileSystem_UsesRecommendedReadFanout()
    {
        var options = RetrievalFanoutOptions.FromProfile(StorageExecutionProfile.FileSystem);

        Assert.AreEqual(2, options.MaxReadFanout);
    }

    /// <summary>
    /// FromProfile：Postgres 预设的 RecommendedReadFanout=8，应被原样采用。
    /// </summary>
    [TestMethod]
    public void FromProfile_Postgres_UsesRecommendedReadFanout()
    {
        var options = RetrievalFanoutOptions.FromProfile(StorageExecutionProfile.Postgres);

        Assert.AreEqual(8, options.MaxReadFanout);
    }

    /// <summary>
    /// FromProfile：SupportsParallelReads=false 时强制 fanout=1（串行），
    /// 即便 RecommendedReadFanout 是更大的值。
    /// </summary>
    [TestMethod]
    public void FromProfile_NotSupportsParallelReads_ForcesSerial()
    {
        var serialProfile = new StorageExecutionProfile
        {
            ProviderKind = StorageProviderKind.Unknown,
            SupportsParallelReads = false,
            RecommendedReadFanout = 16
        };

        var options = RetrievalFanoutOptions.FromProfile(serialProfile);

        Assert.AreEqual(1, options.MaxReadFanout,
            "SupportsParallelReads=false 时强制走串行路径，MaxReadFanout=1");
    }

    /// <summary>
    /// FromProfile：RecommendedReadFanout <= 0 时被 Math.Max(1, ...) 兜底为 1，
    /// 避免 SemaphoreSlim(0) 抛异常。
    /// </summary>
    [TestMethod]
    public void FromProfile_ZeroOrNegativeFanout_ClampedToOne()
    {
        var profile = new StorageExecutionProfile
        {
            ProviderKind = StorageProviderKind.Unknown,
            SupportsParallelReads = true,
            RecommendedReadFanout = 0
        };

        var options = RetrievalFanoutOptions.FromProfile(profile);

        Assert.AreEqual(1, options.MaxReadFanout);
    }

    /// <summary>
    /// FromProfile：null profile 抛 ArgumentNullException。
    /// </summary>
    [TestMethod]
    public void FromProfile_NullProfile_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            RetrievalFanoutOptions.FromProfile(null!));
    }

    // --- R13.3 #2：IStoreRuntimeCapabilities 路径优先 ---

    /// <summary>
    /// Resolve：store 实现 IStoreRuntimeCapabilities 时，使用 Profile.RecommendedReadFanout。
    /// </summary>
    [TestMethod]
    public void Resolve_StoreImplementsCapabilities_UsesProfileFanout()
    {
        var contextStore = new CapableContextStore(StorageExecutionProfile.FileSystem);
        var memoryStore = new CapableMemoryStore(StorageExecutionProfile.FileSystem);

        var options = RetrievalFanoutOptions.Resolve(contextStore, memoryStore);

        Assert.AreEqual(2, options.MaxReadFanout,
            "FileSystem profile.RecommendedReadFanout = 2");
    }

    /// <summary>
    /// Resolve：store 实现 IStoreRuntimeCapabilities 且 SupportsParallelReads=false 时强制 fanout=1。
    /// </summary>
    [TestMethod]
    public void Resolve_CapableStoreNotSupportingParallelReads_ForcesSerial()
    {
        var serialProfile = new StorageExecutionProfile
        {
            ProviderKind = StorageProviderKind.Unknown,
            SupportsParallelReads = false,
            RecommendedReadFanout = 16
        };
        var contextStore = new CapableContextStore(serialProfile);

        var options = RetrievalFanoutOptions.Resolve(contextStore, null);

        Assert.AreEqual(1, options.MaxReadFanout);
    }

    /// <summary>
    /// Resolve：一个 store 实现能力契约、另一个为 null 时，按非 null 一方推断。
    /// </summary>
    [TestMethod]
    public void Resolve_OneCapableStoreOneNull_UsesCapableStoreProfile()
    {
        var contextStore = new CapableContextStore(StorageExecutionProfile.Postgres);

        var options = RetrievalFanoutOptions.Resolve(contextStore, null);

        Assert.AreEqual(8, options.MaxReadFanout);
    }

    /// <summary>
    /// Resolve：两个 store 都实现能力契约时取 min。
    /// 例如 InMemory(16) + FileSystem(2) → 2。
    /// </summary>
    [TestMethod]
    public void Resolve_TwoCapableStores_TakesMin()
    {
        var contextStore = new CapableContextStore(StorageExecutionProfile.FileSystem);
        var memoryStore = new CapableMemoryStore(StorageExecutionProfile.InMemory);

        var options = RetrievalFanoutOptions.Resolve(contextStore, memoryStore);

        Assert.AreEqual(2, options.MaxReadFanout, "min(2, 16) = 2");
    }

    private sealed class FakeUnknownNamespaceStore : IContextStore
    {
        public Task SaveAsync(ContextItem item, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<ContextItem?> GetAsync(string workspaceId, string collectionId, string id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<ContextItem>> QueryAsync(ContextQuery query, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task DeleteAsync(string workspaceId, string collectionId, string id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// 包装 IContextStore，附加 IStoreRuntimeCapabilities 实现，用于测试能力驱动路径。
    /// 所有 IContextStore 方法转发给内部 InMemoryContextStore，避免重写存储语义。
    /// </summary>
    private sealed class CapableContextStore : IContextStore, IStoreRuntimeCapabilities
    {
        private readonly ContextCore.Storage.InMemory.Stores.InMemoryContextStore _inner = new();

        public CapableContextStore(StorageExecutionProfile profile)
        {
            Profile = profile;
        }

        public StorageExecutionProfile Profile { get; }

        public Task SaveAsync(ContextItem item, CancellationToken cancellationToken = default)
            => _inner.SaveAsync(item, cancellationToken);

        public Task<ContextItem?> GetAsync(string workspaceId, string collectionId, string id, CancellationToken cancellationToken = default)
            => _inner.GetAsync(workspaceId, collectionId, id, cancellationToken);

        public Task<IReadOnlyList<ContextItem>> QueryAsync(ContextQuery query, CancellationToken cancellationToken = default)
            => _inner.QueryAsync(query, cancellationToken);

        public Task DeleteAsync(string workspaceId, string collectionId, string id, CancellationToken cancellationToken = default)
            => _inner.DeleteAsync(workspaceId, collectionId, id, cancellationToken);
    }

    /// <summary>
    /// 包装 IMemoryStore，附加 IStoreRuntimeCapabilities 实现，用于测试能力驱动路径。
    /// </summary>
    private sealed class CapableMemoryStore : IMemoryStore, IStoreRuntimeCapabilities
    {
        private readonly ContextCore.Storage.InMemory.InMemoryMemoryStore _inner = new();

        public CapableMemoryStore(StorageExecutionProfile profile)
        {
            Profile = profile;
        }

        public StorageExecutionProfile Profile { get; }

        public Task SaveAsync(ContextMemoryItem item, CancellationToken cancellationToken = default)
            => _inner.SaveAsync(item, cancellationToken);

        public Task<ContextMemoryItem?> GetAsync(string workspaceId, string collectionId, string id, CancellationToken cancellationToken = default)
            => _inner.GetAsync(workspaceId, collectionId, id, cancellationToken);

        public Task<IReadOnlyList<ContextMemoryItem>> QueryAsync(ContextMemoryQuery query, CancellationToken cancellationToken = default)
            => _inner.QueryAsync(query, cancellationToken);

        public Task UpdateStatusAsync(string workspaceId, string collectionId, string id, ContextMemoryStatus status, CancellationToken cancellationToken = default)
            => _inner.UpdateStatusAsync(workspaceId, collectionId, id, status, cancellationToken);
    }
}
