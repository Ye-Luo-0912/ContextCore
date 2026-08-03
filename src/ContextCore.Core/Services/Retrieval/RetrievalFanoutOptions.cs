using ContextCore.Abstractions;

namespace ContextCore.Core.Services.Retrieval;

/// <summary>
/// Retrieval 通道读取 fanout 的并发上限配置。
/// 在 Batch API（IContextObjectBatchResolver）完成前，对当前 Task.WhenAll 路径施加 SemaphoreSlim 节流，
/// 避免 VectorTopK=100 时 Postgres 连接池击穿或 FileSystem 锁竞争加剧。
/// 默认按 store 类型自动解析；调用方可通过 HybridContextRetriever 构造函数显式覆盖。
/// </summary>
public sealed class RetrievalFanoutOptions
{
    /// <summary>
    /// 单次 Task.WhenAll 的最大并发读取数。
    /// 默认 8，覆盖典型 Postgres 部署（连接池 100，留充足余量）。
    /// FileSystem 推荐 2，InMemory 推荐 16 或更高。
    /// 小于等于 1 时走串行路径；大于实际 fanout 大小时退化为无节流 Task.WhenAll。
    /// </summary>
    public int MaxReadFanout { get; init; } = 8;

    /// <summary>默认实例（MaxReadFanout=8）。</summary>
    public static RetrievalFanoutOptions Default { get; } = new();

    /// <summary>
    /// 从 <see cref="StorageExecutionProfile"/> 派生 fanout 选项。
    /// 当 <paramref name="profile"/> 不支持并发读时强制为 1（串行）；
    /// 否则使用 <see cref="StorageExecutionProfile.RecommendedReadFanout"/>。
    /// </summary>
    public static RetrievalFanoutOptions FromProfile(StorageExecutionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var fanout = profile.SupportsParallelReads
            ? Math.Max(1, profile.RecommendedReadFanout)
            : 1;
        return new RetrievalFanoutOptions { MaxReadFanout = fanout };
    }

    /// <summary>
    /// 根据 store 实际类型推断合适的 fanout 上限。
    /// 优先消费 <see cref="IStoreRuntimeCapabilities"/>，将字符串/namespace 推断降级为回退路径。
    /// <list type="bullet">
    /// <item>当 store 实现 IStoreRuntimeCapabilities 时：使用 Profile.RecommendedReadFanout；
    /// 若 SupportsParallelReads 为 false，强制为 1（串行）。</item>
    /// <item>否则回退到 namespace 字符串匹配（FileSystem=2 / InMemory=16 / Postgres=8 / 其他=4）。</item>
    /// </list>
    /// 不同 store 类型混合时取 min，保证最弱的一方不被压垮。
    /// 任一 store 为 null 时按另一方推断；两者都为 null 时回退到 Default。
    /// </summary>
    public static RetrievalFanoutOptions Resolve(IContextStore? contextStore, IMemoryStore? memoryStore)
    {
        if (contextStore is null && memoryStore is null)
        {
            return Default;
        }

        var contextFanout = DetectFanout(contextStore);
        var memoryFanout = DetectFanout(memoryStore);
        var maxReadFanout = Math.Min(contextFanout, memoryFanout);
        return new RetrievalFanoutOptions { MaxReadFanout = maxReadFanout };
    }

    private static int DetectFanout(object? store)
    {
        if (store is null)
        {
            // store 为 null 时不参与 min 计算
            return int.MaxValue;
        }

        // 优先消费 IStoreRuntimeCapabilities（能力驱动，替代 namespace 字符串检测）
        if (store is IStoreRuntimeCapabilities capable)
        {
            var profile = capable.Profile;
            if (!profile.SupportsParallelReads)
            {
                // Provider 不支持并发读：强制串行
                return 1;
            }
            return profile.RecommendedReadFanout;
        }

        // 回退：namespace 字符串匹配（用于测试替身或未接入能力契约的 Provider）
        var ns = store.GetType().Namespace ?? string.Empty;
        if (ns.Contains("FileSystem", StringComparison.Ordinal))
        {
            return 2;
        }

        if (ns.Contains("InMemory", StringComparison.Ordinal))
        {
            return 16;
        }

        if (ns.Contains("Postgres", StringComparison.Ordinal))
        {
            return 8;
        }

        return 4;
    }
}
