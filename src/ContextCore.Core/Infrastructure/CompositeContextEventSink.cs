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

    public CompositeContextEventSink(IEnumerable<IContextEventSink> sinks)
    {
        _sinks = sinks.Where(sink => sink is not null).ToArray();
    }

    /// <summary>
    /// 复合接收器自身声明为 BestEffort：其内部已按 sink 的 <see cref="IContextEventSink.Kind"/> 分别处理失败，
    /// 不希望外层再把整次 EmitAsync 当作必须成功的审计操作。
    /// </summary>
    public ContextEventSinkKind Kind => ContextEventSinkKind.BestEffort;

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
}
