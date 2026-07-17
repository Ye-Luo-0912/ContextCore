namespace ContextCore.Core.Services.Retrieval;

/// <summary>
/// P0-7.2: 对 Task.WhenAll 施加 SemaphoreSlim 节流的辅助方法。
/// 在 Batch API（IContextObjectBatchResolver）落地前，对当前并行 N+1 路径施加并发上限，
/// 防止 VectorTopK=100 时 Postgres 连接池击穿或 FileSystem 锁竞争加剧。
/// 每次 WhenAllAsync 调用创建独立的 SemaphoreSlim，避免跨请求互相阻塞。
/// </summary>
internal static class BoundedFanout
{
    /// <summary>
    /// 对 source 中每个元素调用 selector 生成 Task，按 maxConcurrency 节流并行执行，结果按输入顺序返回。
    /// 快速路径：
    ///   - 输入为空 → 返回空数组
    ///   - maxConcurrency ≤ 1 → 串行执行，无 SemaphoreSlim 开销
    ///   - 输入数量 ≤ maxConcurrency → 直接 Task.WhenAll，无节流开销
    /// 节流路径：为每次调用创建独立 SemaphoreSlim(maxConcurrency)，避免跨请求干扰。
    /// </summary>
    public static async Task<TOutput[]> WhenAllAsync<TInput, TOutput>(
        IEnumerable<TInput> source,
        Func<TInput, CancellationToken, Task<TOutput>> selector,
        int maxConcurrency,
        CancellationToken cancellationToken)
    {
        var inputs = source as IReadOnlyList<TInput> ?? source.ToList();
        if (inputs.Count == 0)
        {
            return Array.Empty<TOutput>();
        }

        // 串行路径：maxConcurrency <= 1 时不引入 SemaphoreSlim 开销
        if (maxConcurrency <= 1)
        {
            var serial = new TOutput[inputs.Count];
            for (var i = 0; i < inputs.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                serial[i] = await selector(inputs[i], cancellationToken).ConfigureAwait(false);
            }
            return serial;
        }

        // 快速并行路径：fanout 在预算内，直接 Task.WhenAll
        if (inputs.Count <= maxConcurrency)
        {
            var directTasks = new Task<TOutput>[inputs.Count];
            for (var i = 0; i < inputs.Count; i++)
            {
                directTasks[i] = selector(inputs[i], cancellationToken);
            }
            return await Task.WhenAll(directTasks).ConfigureAwait(false);
        }

        // 节流路径：每次调用创建独立 SemaphoreSlim
        using var gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var results = new TOutput[inputs.Count];
        var tasks = new Task[inputs.Count];
        for (var i = 0; i < inputs.Count; i++)
        {
            tasks[i] = RunThrottledAsync(inputs[i], i, gate, selector, results, cancellationToken);
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    private static async Task RunThrottledAsync<TInput, TOutput>(
        TInput input,
        int index,
        SemaphoreSlim gate,
        Func<TInput, CancellationToken, Task<TOutput>> selector,
        TOutput[] results,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            results[index] = await selector(input, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
