using ContextCore.Abstractions;

namespace ContextCore.Core;

/// <summary>
/// 将事件转发给多个 <see cref="IContextEventSink"/> 实例的复合接收器。
/// 每个 sink 独立 try/catch，避免单个 sink 失败阻断后续 sink。
/// <see cref="ContextEventSinkKind.BestEffort"/> sink 的失败被吞掉（fail-open）；
/// <see cref="ContextEventSinkKind.Required"/> sink 的失败聚合成 <see cref="AggregateException"/> 在遍历结束后抛出（fail-closed）。
/// </summary>
public sealed class CompositeContextEventSink : IContextEventSink
{
    private readonly IReadOnlyList<IContextEventSink> _sinks;
    private readonly ContextEventSinkKind _kind;

    public CompositeContextEventSink(IEnumerable<IContextEventSink> sinks)
    {
        _sinks = sinks.Where(sink => sink is not null).ToArray();
        // P0-8：复合接收器的 Kind 取所有子 sink 的最严格值——只要有一个子 sink 为 Required，
        // 复合接收器就声明为 Required。这样外层装饰器（如 BoundedChannelContextEventSink）
        // 会绕过有界通道、同步转发事件，确保审计事件不会被通道满时丢弃。
        _kind = _sinks.Any(sink => sink.Kind == ContextEventSinkKind.Required)
            ? ContextEventSinkKind.Required
            : ContextEventSinkKind.BestEffort;
    }

    /// <summary>
    /// P0-8：取所有子 sink 的最严格 Kind。只要有一个子 sink 为 Required，复合接收器即为 Required。
    /// 这样外层 <see cref="BoundedChannelContextEventSink"/> 会绕过通道、同步转发事件，
    /// 确保审计事件（FileContextEventSink / PostgresContextEventSink）不会被通道满时丢弃。
    /// 复合接收器内部仍按子 sink 的 Kind 分别处理失败：
    /// BestEffort 子 sink 失败被吞掉（fail-open），Required 子 sink 失败聚合成 <see cref="AggregateException"/> 抛出（fail-closed）。
    /// </summary>
    public ContextEventSinkKind Kind => _kind;

    public async Task EmitAsync(
        ContextOperationEvent operationEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationEvent);

        List<Exception>? requiredErrors = null;

        foreach (var sink in _sinks)
        {
            try
            {
                await sink.EmitAsync(operationEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // BestEffort sink 失败：fail-open，记录但不阻断后续 sink，也不向上抛出。
                // Required sink 失败：收集到聚合异常中，遍历结束后统一抛出，保证调用方感知审计落盘失败。
                if (sink.Kind == ContextEventSinkKind.Required)
                {
                    (requiredErrors ??= new List<Exception>()).Add(ex);
                }
            }
        }

        if (requiredErrors is not null)
        {
            throw new AggregateException(
                "Required context event sink(s) failed; see inner exceptions.",
                requiredErrors);
        }
    }

    /// <summary>
    /// R13.4 #1：批量写入转发。每个子 sink 独立 try/catch，与 <see cref="EmitAsync"/> 保持
    /// 相同的 fail-open（BestEffort）/ fail-closed（Required）语义。
    /// 调用本方法的子 sink 如果支持批量 I/O（File / Postgres），可在单次锁/单次 round-trip 内完成写入。
    /// </summary>
    public async Task EmitBatchAsync(
        IReadOnlyList<ContextOperationEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return;
        }

        List<Exception>? requiredErrors = null;

        foreach (var sink in _sinks)
        {
            try
            {
                await sink.EmitBatchAsync(events, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (sink.Kind == ContextEventSinkKind.Required)
                {
                    (requiredErrors ??= new List<Exception>()).Add(ex);
                }
            }
        }

        if (requiredErrors is not null)
        {
            throw new AggregateException(
                "Required context event sink(s) failed; see inner exceptions.",
                requiredErrors);
        }
    }
}
