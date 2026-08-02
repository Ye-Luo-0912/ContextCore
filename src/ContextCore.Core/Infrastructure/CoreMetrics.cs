using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace ContextCore.Core;

/// <summary>
/// ContextCore.Core 遥测仪表盘（基于 <see cref="System.Diagnostics.Metrics.Meter"/>）。
/// 使用 static readonly 字段发布到任意已注册的 MeterListener（包括 OpenTelemetry）。
/// </summary>
public static class CoreMetrics
{
    private static readonly Meter _meter = new("ContextCore.Core", "1.0");

    /// <summary>上下文包构建耗时（毫秒）。</summary>
    public static readonly Histogram<double> PackageBuildDuration =
        _meter.CreateHistogram<double>(
            "contextcore.package.build.duration",
            unit: "ms",
            description: "上下文包（ContextPackage）构建端到端耗时");

    /// <summary>混合检索耗时（毫秒）。</summary>
    public static readonly Histogram<double> RetrievalDuration =
        _meter.CreateHistogram<double>(
            "contextcore.retrieval.duration",
            unit: "ms",
            description: "HybridContextRetriever 检索端到端耗时");

    // ── Provider 性能计时拆分 ──────────────────────────────────────────
    // 以下 Histogram 用于将 Provider 调用耗时拆分为 queue / execution / hydration /
    // tokenization 四段。Circuit Breaker 仅依据 execution（RecordProviderTime）做熔断判定，
    // queue_ms 仅作诊断指标，不参与熔断（避免本地并发饱和时误判 Semantic/Graph Store 变慢）。

    /// <summary>Provider 等待 Semaphore 的排队耗时（毫秒，诊断用，不参与熔断）。</summary>
    public static readonly Histogram<double> ProviderQueueDuration =
        _meter.CreateHistogram<double>(
            "contextcore.provider.queue.duration",
            unit: "ms",
            description: "Provider 等待调用方 SemaphoreSlim 的排队耗时；不参与 Circuit Breaker 熔断判定");

    /// <summary>Provider 实际执行耗时（毫秒，参与熔断判定）。</summary>
    public static readonly Histogram<double> ProviderExecutionDuration =
        _meter.CreateHistogram<double>(
            "contextcore.provider.execution.duration",
            unit: "ms",
            description: "Provider 实际执行耗时（不含 Semaphore 排队）；参与 Circuit Breaker 熔断判定");

    /// <summary>Provider 正文 hydration 耗时（毫秒，诊断用）。</summary>
    public static readonly Histogram<double> ProviderHydrationDuration =
        _meter.CreateHistogram<double>(
            "contextcore.provider.hydration.duration",
            unit: "ms",
            description: "Provider 正文 hydration（按 SourceKind 批量查询 store）耗时；诊断用");

    /// <summary>Provider tokenize 耗时（毫秒，诊断用）。</summary>
    public static readonly Histogram<double> ProviderTokenizationDuration =
        _meter.CreateHistogram<double>(
            "contextcore.provider.tokenization.duration",
            unit: "ms",
            description: "Provider 调用 tokenizer 计算 TokenCost 的耗时；诊断用");

    /// <summary>LLM 压缩耗时（毫秒）。</summary>
    public static readonly Histogram<double> CompressionDuration =
        _meter.CreateHistogram<double>(
            "contextcore.compression.duration",
            unit: "ms",
            description: "LlmContextCompressor 压缩端到端耗时（含模型调用）");

    /// <summary>压缩消耗 Token 数（仅在成功时计入）。</summary>
    public static readonly Counter<long> CompressionTokens =
        _meter.CreateCounter<long>(
            "contextcore.compression.tokens",
            unit: "{tokens}",
            description: "LLM 压缩消耗的 Token 总数（inputTokens + outputTokens）");

    // ── Event Sink 观测管线指标 ─────────────────────────────────
    // 以下计数器由 BoundedChannelContextEventSink 记录，反映 BestEffort 事件通道的背压与健康度。
    // Required sink 不走通道，不参与这些计数器。

    /// <summary>因通道满而被丢弃的事件数（仅 BestEffort 路径）。</summary>
    public static readonly Counter<long> EventSinkDropped =
        _meter.CreateCounter<long>(
            "contextcore.eventsink.dropped",
            unit: "{events}",
            description: "BoundedChannelContextEventSink 因通道满而丢弃的事件数");

    /// <summary>批量写入失败的次数（仅 BestEffort 路径，fail-open 吞掉异常）。</summary>
    public static readonly Counter<long> EventSinkErrors =
        _meter.CreateCounter<long>(
            "contextcore.eventsink.errors",
            unit: "{batches}",
            description: "BoundedChannelContextEventSink 批量写入失败的次数");

    /// <summary>已成功提交的批量写入次数（仅 BestEffort 路径）。</summary>
    public static readonly Counter<long> EventSinkBatchEmits =
        _meter.CreateCounter<long>(
            "contextcore.eventsink.batch_emits",
            unit: "{batches}",
            description: "BoundedChannelContextEventSink 已成功提交的批量写入次数");

    // ── Circuit Breaker 指标 ──────────────────────────────────────────────
    // 状态迁移 Counter（事件流）+ 当前状态 ObservableGauge（快照）。
    // 由 DefaultComponentHealthRegistry 在状态迁移时调用 RecordCircuitBreakerTransition。
    // ObservableGauge 在 OTel 抓取时回调枚举 _cbStates 注册表，统计 Open/HalfOpen 数量。

    /// <summary>
    /// Circuit Breaker 状态迁移次数（Counter，带 component 与 new_state tag）。
    /// 每次 Closed→Open、Open→HalfOpen、HalfOpen→Closed 等迁移时 +1。
    /// </summary>
    public static readonly Counter<long> CircuitBreakerTransitions =
        _meter.CreateCounter<long>(
            "contextcore.circuit_breaker.transitions",
            unit: "{transitions}",
            description: "Circuit Breaker 状态迁移次数（Closed→Open、Open→HalfOpen、HalfOpen→Closed 等）");

    /// <summary>
    /// 当前 CB 状态注册表：(component, scopeKey) → state 字符串（"Open"/"HalfOpen"）。
    /// Closed 状态不写入（移除），以节省内存；枚举时未出现的 key 默认为 Closed。
    /// 由 <see cref="RecordCircuitBreakerTransition"/> 在状态迁移时更新。
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> _cbStates = new();

    /// <summary>
    /// 当前处于 Open 状态的 Circuit Breaker 数量（跨所有 component 与 scope）。
    /// </summary>
    public static readonly ObservableGauge<int> CircuitBreakerOpenCount =
        _meter.CreateObservableGauge(
            "contextcore.circuit_breaker.open_count",
            () => CountCbStates("Open"),
            unit: "{circuits}",
            description: "当前处于 Open 状态的 Circuit Breaker 数量（跨所有 component 与 scope）");

    /// <summary>
    /// 当前处于 HalfOpen 状态的 Circuit Breaker 数量（跨所有 component 与 scope）。
    /// </summary>
    public static readonly ObservableGauge<int> CircuitBreakerHalfOpenCount =
        _meter.CreateObservableGauge(
            "contextcore.circuit_breaker.halfopen_count",
            () => CountCbStates("HalfOpen"),
            unit: "{circuits}",
            description: "当前处于 HalfOpen 状态的 Circuit Breaker 数量（跨所有 component 与 scope）");

    /// <summary>
    /// 记录 Circuit Breaker 状态迁移事件并更新当前状态注册表。
    /// </summary>
    /// <param name="component">组件标识（如 "Inference"、"Provider.Semantic"）。</param>
    /// <param name="scopeKey">scope 标识（用于 per-scope 状态追踪）。</param>
    /// <param name="newState">新状态名称（"Closed"/"Open"/"HalfOpen"）。</param>
    public static void RecordCircuitBreakerTransition(string component, string scopeKey, string newState)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(newState);

        CircuitBreakerTransitions.Add(1,
            new KeyValuePair<string, object?>("component", component),
            new KeyValuePair<string, object?>("new_state", newState));

        // 更新当前状态注册表：Closed 移除以节省内存；Open/HalfOpen 保留
        var key = component + "|" + scopeKey;
        if (string.Equals(newState, "Closed", StringComparison.Ordinal))
        {
            _cbStates.TryRemove(key, out _);
        }
        else
        {
            _cbStates[key] = newState;
        }
    }

    /// <summary>
    /// 记录 Circuit Breaker 状态迁移事件（2 参数重载，scopeKey 默认为 "default"）。
    /// 供旧调用方（未区分 scope）使用，等价于 scopeKey="default" 的 3 参数版本。
    /// </summary>
    /// <param name="component">组件标识。</param>
    /// <param name="newState">新状态名称（"Closed"/"Open"/"HalfOpen"）。</param>
    public static void RecordCircuitBreakerTransition(string component, string newState)
        => RecordCircuitBreakerTransition(component, "default", newState);

    /// <summary>统计指定状态的 CB 数量（ObservableGauge 回调用）。</summary>
    private static int CountCbStates(string targetState)
    {
        var count = 0;
        foreach (var kvp in _cbStates)
        {
            if (string.Equals(kvp.Value, targetState, StringComparison.Ordinal))
            {
                count++;
            }
        }
        return count;
    }
}
