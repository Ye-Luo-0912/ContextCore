using ContextCore.Abstractions;

namespace ContextCore.Core;

/// <summary>空操作事件接收器，丢弃所有事件，适合测试和最小化场景。</summary>
public sealed class NullContextEventSink : IContextEventSink
{
    public static NullContextEventSink Instance { get; } = new();

    private NullContextEventSink()
    {
    }

    public Task EmitAsync(
        ContextOperationEvent operationEvent,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}

/// <summary>将 <see cref="ContextOperationEvent"/> 保存在内存列表中的事件接收器，适合测试和审计场景。</summary>
public sealed class InMemoryContextEventSink : IContextEventSink
{
    private readonly object _gate = new();
    private readonly List<ContextOperationEvent> _events = new();

    public IReadOnlyList<ContextOperationEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    public Task EmitAsync(
        ContextOperationEvent operationEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationEvent);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _events.Add(operationEvent);
        }

        return Task.CompletedTask;
    }
}
