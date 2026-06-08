using ContextCore.Abstractions;

namespace ContextCore.Core;

/// <summary>
/// 将事件转发给多个 <see cref="IContextEventSink"/> 实例的复合接收器。
/// </summary>
public sealed class CompositeContextEventSink : IContextEventSink
{
    private readonly IReadOnlyList<IContextEventSink> _sinks;

    public CompositeContextEventSink(IEnumerable<IContextEventSink> sinks)
    {
        _sinks = sinks.Where(sink => sink is not null).ToArray();
    }

    public async Task EmitAsync(
        ContextOperationEvent operationEvent,
        CancellationToken cancellationToken = default)
    {
        foreach (var sink in _sinks)
        {
            await sink.EmitAsync(operationEvent, cancellationToken).ConfigureAwait(false);
        }
    }
}
