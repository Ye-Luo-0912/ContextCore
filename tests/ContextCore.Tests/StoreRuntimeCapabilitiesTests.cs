using ContextCore.Abstractions;
using ContextCore.Runtime;
using ContextCore.Service.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.Tests;

/// <summary>
/// IStoreRuntimeCapabilities / StorageExecutionProfile 单元测试。
/// 验证各 provider 的能力描述与 DI 注册路径。
/// </summary>
[TestClass]
[TestCategory("Contract")]
[TestCategory("Storage")]
public sealed class StoreRuntimeCapabilitiesTests
{
    /// <summary>
    /// InMemory 能力：并行读写支持、无事务、无跨进程、无持久化、无快照复用收益。
    /// </summary>
    [TestMethod]
    public void InMemory_Profile_MatchesExpectedCapabilities()
    {
        var caps = new StoreRuntimeCapabilities(StorageProviderKind.InMemory);

        Assert.AreEqual(StorageProviderKind.InMemory, caps.Profile.ProviderKind);
        Assert.IsTrue(caps.Profile.SupportsParallelReads, "InMemory 应支持并行读（ConcurrentDictionary）");
        Assert.IsTrue(caps.Profile.SupportsParallelWrites, "InMemory 应支持并行写");
        Assert.IsFalse(caps.Profile.SupportsBatchWrites, "InMemory 无批量写入 API");
        Assert.IsFalse(caps.Profile.SupportsTransactions, "InMemory 无事务概念");
        Assert.IsFalse(caps.Profile.SupportsCrossProcessSafety, "InMemory 进程内数据不跨进程");
        Assert.IsTrue(caps.Profile.SupportsSnapshotReuse, "InMemory 进程内读取即等价于快照");
        Assert.IsFalse(caps.Profile.IsPersistent, "InMemory 进程退出即丢失");
        Assert.AreEqual(64, caps.Profile.MaxRecommendedConcurrency);
    }

    /// <summary>
    /// FileSystem 能力：并行读支持、并行写不支持（FileLockProvider 串行化）、批量写入、advisory 跨进程、持久化。
    /// </summary>
    [TestMethod]
    public void FileSystem_Profile_MatchesExpectedCapabilities()
    {
        var caps = new StoreRuntimeCapabilities(StorageProviderKind.FileSystem);

        Assert.AreEqual(StorageProviderKind.FileSystem, caps.Profile.ProviderKind);
        Assert.IsTrue(caps.Profile.SupportsParallelReads, "FileSystem 应支持并行读");
        Assert.IsFalse(caps.Profile.SupportsParallelWrites, "FileSystem 不支持并行写（FileLockProvider 串行化同路径）");
        Assert.IsTrue(caps.Profile.SupportsBatchWrites, "FileSystem 支持 AppendRangeAsync / UpsertAsync");
        Assert.IsFalse(caps.Profile.SupportsTransactions, "FileSystem 无跨文件事务");
        Assert.IsFalse(caps.Profile.SupportsCrossProcessSafety, "FileSystem 跨进程仅 advisory，参见 R13.1 #6");
        Assert.IsTrue(caps.Profile.SupportsSnapshotReuse, "R13.2 #2 已实现 last-write-time 快照复用");
        Assert.IsTrue(caps.Profile.IsPersistent, "FileSystem 数据持久化");
        Assert.AreEqual(8, caps.Profile.MaxRecommendedConcurrency);
    }

    /// <summary>
    /// Postgres 能力：并行读写支持、事务、跨进程安全、持久化；不支持应用层快照（数据库已自有缓存）。
    /// </summary>
    [TestMethod]
    public void Postgres_Profile_MatchesExpectedCapabilities()
    {
        var caps = new StoreRuntimeCapabilities(StorageProviderKind.Postgres);

        Assert.AreEqual(StorageProviderKind.Postgres, caps.Profile.ProviderKind);
        Assert.IsTrue(caps.Profile.SupportsParallelReads, "Postgres MVCC 支持并行读");
        Assert.IsTrue(caps.Profile.SupportsParallelWrites, "Postgres 行级锁支持并行写");
        Assert.IsTrue(caps.Profile.SupportsBatchWrites, "Postgres 支持 COPY / batch INSERT");
        Assert.IsTrue(caps.Profile.SupportsTransactions, "Postgres 原生 BEGIN/COMMIT/ROLLBACK");
        Assert.IsTrue(caps.Profile.SupportsCrossProcessSafety, "Postgres 通过连接池 + ACID 保证跨进程安全");
        Assert.IsFalse(caps.Profile.SupportsSnapshotReuse, "数据库已自有缓存，应用层快照易 stale");
        Assert.IsTrue(caps.Profile.IsPersistent, "Postgres 数据持久化");
        Assert.AreEqual(32, caps.Profile.MaxRecommendedConcurrency);
    }

    /// <summary>
    /// Unknown kind 时退回最保守的 InMemory profile（避免 NRE）。
    /// </summary>
    [TestMethod]
    public void Unknown_Kind_FallsBackToInMemory()
    {
        var caps = new StoreRuntimeCapabilities(StorageProviderKind.Unknown);

        Assert.AreEqual(StorageProviderKind.InMemory, caps.Profile.ProviderKind,
            "未知 kind 退回 InMemory（最保守）");
    }

    /// <summary>
    /// StorageExecutionProfile 预设实例是不可变的：InMemory/FileSystem/Postgres 单例字段值稳定。
    /// 多次访问返回同一引用，调用方可安全缓存。
    /// </summary>
    [TestMethod]
    public void Predefined_Profiles_AreStableSingletons()
    {
        Assert.AreSame(StorageExecutionProfile.InMemory, StorageExecutionProfile.InMemory);
        Assert.AreSame(StorageExecutionProfile.FileSystem, StorageExecutionProfile.FileSystem);
        Assert.AreSame(StorageExecutionProfile.Postgres, StorageExecutionProfile.Postgres);

        // 不同 ProviderKind 对应不同预设实例
        Assert.AreNotSame(StorageExecutionProfile.InMemory, StorageExecutionProfile.FileSystem);
        Assert.AreNotSame(StorageExecutionProfile.FileSystem, StorageExecutionProfile.Postgres);
        Assert.AreNotSame(StorageExecutionProfile.InMemory, StorageExecutionProfile.Postgres);
    }

    /// <summary>
    /// 关键：通过 AddContextStorage 注册后，IStoreRuntimeCapabilities 可从 DI 解析。
    /// FileSystem provider → Profile.ProviderKind == FileSystem。
    /// </summary>
    [TestMethod]
    public void AddContextStorage_FileSystem_RegistersCapabilities()
    {
        var services = new ServiceCollection();
        var options = new ContextCore.Service.StorageOptions
        {
            Provider = "filesystem",
            RootPath = Path.GetTempPath()
        };

        services.AddContextStorage(options);

        var sp = services.BuildServiceProvider();
        var caps = sp.GetService<IStoreRuntimeCapabilities>();

        Assert.IsNotNull(caps, "IStoreRuntimeCapabilities 应被注册");
        Assert.AreEqual(StorageProviderKind.FileSystem, caps!.Profile.ProviderKind);
    }

    /// <summary>
    /// Memory provider → Profile.ProviderKind == InMemory。
    /// </summary>
    [TestMethod]
    public void AddContextStorage_Memory_RegistersCapabilities()
    {
        var services = new ServiceCollection();
        var options = new ContextCore.Service.StorageOptions
        {
            Provider = "memory"
        };

        services.AddContextStorage(options);

        var sp = services.BuildServiceProvider();
        var caps = sp.GetService<IStoreRuntimeCapabilities>();

        Assert.IsNotNull(caps);
        Assert.AreEqual(StorageProviderKind.InMemory, caps!.Profile.ProviderKind);
    }

    /// <summary>
    /// 未知 provider 应抛 InvalidOperationException（与原行为一致）。
    /// </summary>
    [TestMethod]
    public void AddContextStorage_UnknownProvider_Throws()
    {
        var services = new ServiceCollection();
        var options = new ContextCore.Service.StorageOptions
        {
            Provider = "unknown-storage"
        };

        Assert.ThrowsException<InvalidOperationException>(() => services.AddContextStorage(options));
    }

    /// <summary>
    /// 能力字段组合验证：FileSystem 不支持事务 + 不支持跨进程 → 应被调用方识别为"仅单进程一致"。
    /// 这是 R13.3 #2（batch size / max concurrency / parallel read safety / consistency / transaction support）的输入。
    /// </summary>
    [TestMethod]
    public void FileSystem_ConsistencyCapabilities_CorrectForSingleProcessUse()
    {
        var profile = StorageExecutionProfile.FileSystem;

        Assert.IsFalse(profile.SupportsTransactions);
        Assert.IsFalse(profile.SupportsCrossProcessSafety);
        Assert.IsTrue(profile.SupportsParallelReads);
        Assert.IsFalse(profile.SupportsParallelWrites);
    }

    /// <summary>
    /// 能力字段组合验证：Postgres 支持事务 + 跨进程 → 应被调用方识别为"多进程强一致"。
    /// </summary>
    [TestMethod]
    public void Postgres_ConsistencyCapabilities_CorrectForMultiProcessUse()
    {
        var profile = StorageExecutionProfile.Postgres;

        Assert.IsTrue(profile.SupportsTransactions);
        Assert.IsTrue(profile.SupportsCrossProcessSafety);
        Assert.IsTrue(profile.SupportsParallelReads);
        Assert.IsTrue(profile.SupportsParallelWrites);
    }
}
