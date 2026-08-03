namespace ContextCore.Inference.Onnx;

/// <summary>
/// 推理并发默认值按 Execution Provider profile 解析。
/// CPU 与 GPU 的并发模型不同：单 GPU 会话内 session.Run 在单一 stream 上串行执行，
/// 按 ProcessorCount 配置并发槽位只会引入线程争抢与上下文切换，无法提升吞吐。
/// </summary>
internal static class InferenceConcurrencyProfiles
{
    /// <summary>
    /// 计算未显式配置（配置值 ≤ 0）时的默认并发槽位数。
    /// </summary>
    /// <param name="executionProvider">目标 Execution Provider。</param>
    /// <returns>
    /// CPU：<see cref="Environment.ProcessorCount"/>（沿用历史行为）；
    /// GPU（CUDA / TensorRT / DirectML）：1（单会话串行执行，避免过度订阅）。
    /// </returns>
    public static int ResolveDefaultConcurrency(OnnxExecutionProvider executionProvider)
    {
        return executionProvider == OnnxExecutionProvider.CPU
            ? Environment.ProcessorCount
            : 1;
    }
}
