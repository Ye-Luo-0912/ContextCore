using ContextCore.Abstractions;

namespace ContextCore.Core.Services.Retrieval;

/// <summary>
/// P0-7.2: Retrieval 通道读取 fanout 的并发上限配置。
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
    /// 根据 ContextStore/MemoryStore 实际类型推断合适的 fanout 上限。
    /// 通过 namespace 字符串匹配避免引入对 storage 实现层的编译期依赖。
    /// <list type="bullet">
    ///   <item>FileSystem: 2（store 自身 _gate(1,1) 已串行化，外层 fanout 不应再加压）</item>
    ///   <item>InMemory:   16（CPU-only，无明显 I/O 竞争）</item>
    ///   <item>Postgres:    8（典型连接池大小 100，留充足余量）</item>
    ///   <item>其他/Remote: 4（保守默认，远程 provider 风险更高）</item>
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
