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

    // ── R13.4 #2：Event Sink 观测管线指标 ─────────────────────────────────
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

    // ── P2：Pending 计数 OTel 指标（HA 场景下区分本实例趋势 vs 全局精确） ──────
    // 以下共享状态由 PendingCountMetricsService 后台服务定期更新：
    //   - local_*：本实例 Interlocked 维护的近似计数（不反映 DB backlog 或其他实例操作）；
    //   - global_*：DB COUNT(*) 查询的精确值（跨实例累积）。
    // ObservableGauge 在 OTel 抓取时回调读取共享状态。所有值在写入时已 clamp 到 ≥ 0。

    private static volatile int _localPendingInstructionCount;
    private static volatile int _localPendingResultCount;
    private static volatile int _localPendingOutboxCount;
    private static volatile int _globalPendingInstructionCount;
    private static volatile int _globalPendingResultCount;
    private static volatile int _globalPendingOutboxCount;

    /// <summary>
    /// 本实例 pending 计数趋势值——<b>不可用于调度/安全判断</b>。
    /// 由 <see cref="Hosting.PendingCountMetricsService"/> 定期从 transport/outbox 的本地 counter 采样后更新。
    /// </summary>
    public static readonly ObservableGauge<int> LocalPendingCount =
        _meter.CreateObservableGauge(
            "contextcore.local_pending_count",
            ObserveLocalPending,
            unit: "{items}",
            description: "本实例 pending 计数趋势值（不反映 DB backlog 或其他实例）；不可用于调度/安全判断");

    /// <summary>
    /// 全局精确 pending 计数（DB COUNT，跨实例累积）——可用于调度/安全判断。
    /// 由 <see cref="Hosting.PendingCountMetricsService"/> 定期查询 DB 后更新。
    /// </summary>
    public static readonly ObservableGauge<int> GlobalPendingCount =
        _meter.CreateObservableGauge(
            "contextcore.global_pending_count",
            ObserveGlobalPending,
            unit: "{items}",
            description: "全局精确 pending 计数（DB COUNT，跨实例累积）；可用于调度/安全判断");

    /// <summary>Pending 计数队列类型（OTel 指标 tag）。</summary>
    public enum PendingQueueTag : byte
    {
        /// <summary>Durable Transport inbox 指令队列。</summary>
        Instruction = 0,

        /// <summary>Durable Transport outbox 结果队列。</summary>
        Result = 1,

        /// <summary>Kernel Result Outbox（fallback 投递）队列。</summary>
        Outbox = 2
    }

    private static IEnumerable<Measurement<int>> ObserveLocalPending()
    {
        yield return new Measurement<int>(_localPendingInstructionCount, new KeyValuePair<string, object?>("queue", "instruction"));
        yield return new Measurement<int>(_localPendingResultCount, new KeyValuePair<string, object?>("queue", "result"));
        yield return new Measurement<int>(_localPendingOutboxCount, new KeyValuePair<string, object?>("queue", "outbox"));
    }

    private static IEnumerable<Measurement<int>> ObserveGlobalPending()
    {
        yield return new Measurement<int>(_globalPendingInstructionCount, new KeyValuePair<string, object?>("queue", "instruction"));
        yield return new Measurement<int>(_globalPendingResultCount, new KeyValuePair<string, object?>("queue", "result"));
        yield return new Measurement<int>(_globalPendingOutboxCount, new KeyValuePair<string, object?>("queue", "outbox"));
    }

    /// <summary>
    /// 更新本实例 pending 趋势值（由 <see cref="Hosting.PendingCountMetricsService"/> 采样后调用）。
    /// 负数会被 <see cref="Math.Max(int, int)"/> clamp 到 0，防止并发竞态下导出负值。
    /// </summary>
    /// <param name="queue">队列类型。</param>
    /// <param name="value">本实例趋势值（可能为负，会被 clamp）。</param>
    public static void SetLocalPendingCount(PendingQueueTag queue, int value)
    {
        var clamped = Math.Max(0, value);
        switch (queue)
        {
            case PendingQueueTag.Instruction:
                _localPendingInstructionCount = clamped;
                break;
            case PendingQueueTag.Result:
                _localPendingResultCount = clamped;
                break;
            case PendingQueueTag.Outbox:
                _localPendingOutboxCount = clamped;
                break;
        }
    }

    /// <summary>
    /// 更新全局精确 pending 值（由 <see cref="Hosting.PendingCountMetricsService"/> 查询 DB 后调用）。
    /// 负数会被 <see cref="Math.Max(int, int)"/> clamp 到 0。
    /// </summary>
    /// <param name="queue">队列类型。</param>
    /// <param name="value">DB 精确值（≥ 0）。</param>
    public static void SetGlobalPendingCount(PendingQueueTag queue, int value)
    {
        var clamped = Math.Max(0, value);
        switch (queue)
        {
            case PendingQueueTag.Instruction:
                _globalPendingInstructionCount = clamped;
                break;
            case PendingQueueTag.Result:
                _globalPendingResultCount = clamped;
                break;
            case PendingQueueTag.Outbox:
                _globalPendingOutboxCount = clamped;
                break;
        }
    }

    // ── P0-6-7：Durable Transport 重试与死信指标 ──────────────────────────
    // 以下共享状态由 PendingCountMetricsService 后台服务定期从 DB 查询后更新。
    // ObservableGauge 在 OTel 抓取时回调读取共享状态。

    private static volatile int _globalDeadLetterCount;

    /// <summary>
    /// 全局死信队列（DLQ）行数（DB COUNT，跨实例累积）。
    /// 由 <see cref="Hosting.PendingCountMetricsService"/> 定期查询 kernel_transport_dead_letter 表后更新。
    /// 持续增长表明消费者持续失败并超过 max_attempts，需人工介入（重投或丢弃）。
    /// </summary>
    public static readonly ObservableGauge<int> GlobalDeadLetterCount =
        _meter.CreateObservableGauge(
            "contextcore.durable_transport.dead_letter_count",
            () => new Measurement<int>(Math.Max(0, _globalDeadLetterCount)),
            unit: "{items}",
            description: "Durable Transport 死信队列行数（DB COUNT，跨实例累积）；持续增长需人工介入");

    /// <summary>
    /// 更新全局死信队列行数（由 <see cref="Hosting.PendingCountMetricsService"/> 查询 DB 后调用）。
    /// 负数会被 clamp 到 0。
    /// </summary>
    /// <param name="value">DB 精确值（≥ 0）。</param>
    public static void SetGlobalDeadLetterCount(int value)
    {
        _globalDeadLetterCount = Math.Max(0, value);
    }

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
