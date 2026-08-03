namespace ContextCore.Abstractions;

/// <summary>
/// 存储 Provider 类型枚举——替代字符串 "filesystem"/"postgres"/"memory" 判断的单一事实源。
/// </summary>
public enum StorageProviderKind
{
    /// <summary>未知（未配置或运行时未注入）。</summary>
    Unknown = 0,

    /// <summary>InMemory：仅进程内、无持久化、不支持跨进程。</summary>
    InMemory = 1,

    /// <summary>FileSystem：文件系统持久化，单进程强一致、跨进程 advisory。</summary>
    FileSystem = 2,

    /// <summary>PostgreSQL：关系数据库，多进程强一致、支持事务。</summary>
    Postgres = 3
}

/// <summary>
/// 存储 Provider 运行时能力契约——替代各处对 "filesystem"/"postgres"/"memory" 字符串的判断。
///
/// 设计原则：
/// - Provider 自己声明能力，避免调用方按字符串或 namespace 推断（脆弱、易遗漏）。
/// - 能力字段为强类型 enum/bool，调用方一次 DI 注入即可查询，无需在多处复制字符串判断。
/// - 该接口不暴露能力"等级"（性能、吞吐等），只描述"是否支持"——后者是客观契约。
/// - 能力在注册时固定，运行时不变；若需动态降级，应通过独立 health 通道而非本接口。
/// </summary>
public interface IStoreRuntimeCapabilities
{
    /// <summary>当前激活的存储 Provider 能力描述。永不返回 null。</summary>
    StorageExecutionProfile Profile { get; }
}

/// <summary>
/// 存储 Provider 执行能力描述——对 IStoreRuntimeCapabilities.Profile 的强类型表达。
/// 替代字符串 "filesystem"/"postgres"/"memory" 判断的单一事实源。
/// </summary>
public sealed class StorageExecutionProfile
{
    /// <summary>
    /// 默认 InMemory 能力：仅适用于测试与开发场景，不支持多进程、不支持事务、不支持持久化。
    /// </summary>
    public static StorageExecutionProfile InMemory { get; } = new()
    {
        ProviderKind = StorageProviderKind.InMemory,
        SupportsParallelReads = true,         // ConcurrentDictionary 天然线程安全
        SupportsParallelWrites = true,         // ConcurrentDictionary 天然线程安全
        SupportsBatchWrites = false,           // 无批量写入 API
        SupportsTransactions = false,          // 无事务概念
        SupportsCrossProcessSafety = false,    // 进程内数据，跨进程不共享
        SupportsSnapshotReuse = true,          // 进程内读取廉价，本就等价于快照
        IsPersistent = false,                  // 进程退出即丢失
        MaxRecommendedConcurrency = 64,        // ConcurrentDictionary 可承受较高并发
        RecommendedReadFanout = 16,            // CPU-only，无明显 I/O 竞争
        RecommendedBatchSize = 64              // 进程内集合切片，无需节流
    };

    /// <summary>
    /// FileSystem 能力：单进程一致，跨进程仅"advisory"。
    /// </summary>
    public static StorageExecutionProfile FileSystem { get; } = new()
    {
        ProviderKind = StorageProviderKind.FileSystem,
        SupportsParallelReads = true,          // 多线程读文件安全
        SupportsParallelWrites = false,        // FileLockProvider 串行化同路径写
        SupportsBatchWrites = true,            // AppendRangeAsync / UpsertAsync
        SupportsTransactions = false,          // 无跨文件事务
        SupportsCrossProcessSafety = false,    // advisory 锁仅标记多进程，不阻断
        SupportsSnapshotReuse = true,          // 按 last-write-time 复用快照
        IsPersistent = true,
        MaxRecommendedConcurrency = 8,         // 磁盘 I/O 串行化，过高并发收益递减
        RecommendedReadFanout = 2,             // store 自身 _gate(1,1) 串行化写，外层 fanout 不应再加压
        RecommendedBatchSize = 32             // 单次 AppendRange 行数上限，避免长锁持有
    };

    /// <summary>
    /// PostgreSQL 能力：真正的多进程安全、支持事务、支持批量写入。
    /// </summary>
    public static StorageExecutionProfile Postgres { get; } = new()
    {
        ProviderKind = StorageProviderKind.Postgres,
        SupportsParallelReads = true,          // MVCC 天然支持
        SupportsParallelWrites = true,         // 行级锁，并发写安全
        SupportsBatchWrites = true,            // COPY / batch INSERT
        SupportsTransactions = true,           // 原生 BEGIN/COMMIT/ROLLBACK
        SupportsCrossProcessSafety = true,     // 通过连接池 + ACID 保证
        SupportsSnapshotReuse = false,         // 数据库已自有缓存，应用层快照易 stale
        IsPersistent = true,
        MaxRecommendedConcurrency = 32,        // 连接池默认上限附近
        RecommendedReadFanout = 8,             // 连接池典型 100，留充足余量
        RecommendedBatchSize = 128             // COPY 批量大小，单次 round-trip 效率高
    };

    /// <summary>Provider 类型枚举，替代字符串判断的唯一入口。</summary>
    public StorageProviderKind ProviderKind { get; init; }

    /// <summary>是否支持多线程并发读（同一文件/表）。</summary>
    public bool SupportsParallelReads { get; init; }

    /// <summary>是否支持多线程并发写（不会破坏数据一致性）。</summary>
    public bool SupportsParallelWrites { get; init; }

    /// <summary>是否支持批量写入 API（如 COPY、AppendRangeAsync）。</summary>
    public bool SupportsBatchWrites { get; init; }

    /// <summary>是否支持事务（跨多个操作的原子提交/回滚）。</summary>
    public bool SupportsTransactions { get; init; }

    /// <summary>
    /// 是否支持跨进程安全——true 表示跨进程并发写入有强一致性保证；
    /// false 表示仅进程内一致，跨进程写入需调用方自行协调（如 advisory lock + 单实例）。
    /// 参见 FileSystemInstanceGuard。
    /// </summary>
    public bool SupportsCrossProcessSafety { get; init; }

    /// <summary>
    /// 是否支持应用层快照复用（如 FileConstraintStore 的 last-write-time 快照）。
    /// 数据库 Provider 通常为 false（已有自身缓存，应用层快照易 stale）。
    /// </summary>
    public bool SupportsSnapshotReuse { get; init; }

    /// <summary>数据是否持久化（进程重启后仍可读）。</summary>
    public bool IsPersistent { get; init; }

    /// <summary>
    /// 推荐的最大并发数。超过该值时性能不再线性增长，可能因锁竞争或资源争用而下降。
    /// 仅作建议，非硬限制。
    /// </summary>
    public int MaxRecommendedConcurrency { get; init; }

    /// <summary>
    /// 推荐的单次 Task.WhenAll 读取 fanout 上限。
    /// 调用方（如 RetrievalFanoutOptions.Resolve）应按此值初始化 SemaphoreSlim，
    /// 避免在 VectorTopK 较大时击穿 Postgres 连接池或加剧 FileSystem 锁竞争。
    /// 当 <see cref="SupportsParallelReads"/> 为 false 时，调用方应强制使用 1（串行）。
    /// </summary>
    public int RecommendedReadFanout { get; init; }

    /// <summary>
    /// 推荐的批量写入单次切片大小。
    /// 调用方在向支持 <see cref="SupportsBatchWrites"/> 的 Provider 提交大量记录时，
    /// 应按此值分片，避免单次 round-trip 过大或长锁持有。
    /// 不支持批量写的 Provider 应忽略此值。
    /// </summary>
    public int RecommendedBatchSize { get; init; }
}
