using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Infrastructure;

namespace ContextCore.Core.Infrastructure;

/// <summary>
/// P5-0.4: IRetrievalTraceStore 的异步写入 decorator。
/// 将 SaveAsync 移出请求关键路径，QueryRecentAsync 仍委托底层 store。
/// </summary>
public sealed class AsyncRetrievalTraceStore : IRetrievalTraceStore, IAsyncDisposable
{
    private readonly IRetrievalTraceStore _inner;
    private readonly AsyncTraceWriter<ContextRetrievalTrace> _writer;

    public AsyncRetrievalTraceStore(IRetrievalTraceStore inner, TraceWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _writer = new AsyncTraceWriter<ContextRetrievalTrace>(
            (trace, ct) => _inner.SaveAsync(trace, ct),
            options);
    }

    public Task SaveAsync(ContextRetrievalTrace trace, CancellationToken cancellationToken = default)
        => _writer.SaveAsync(trace, cancellationToken).AsTask();

    public Task<IReadOnlyList<ContextRetrievalTrace>> QueryRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
        => _inner.QueryRecentAsync(workspaceId, collectionId, take, cancellationToken);

    public long DroppedCount => _writer.DroppedCount;
    public long WrittenCount => _writer.WrittenCount;

    public ValueTask DisposeAsync() => _writer.DisposeAsync();
}

/// <summary>
/// P5-0.4: IContextPackageBuildTraceStore 的异步写入 decorator。
/// </summary>
public sealed class AsyncContextPackageBuildTraceStore : IContextPackageBuildTraceStore, IAsyncDisposable
{
    private readonly IContextPackageBuildTraceStore _inner;
    private readonly AsyncTraceWriter<ContextPackageBuildResult> _writer;

    public AsyncContextPackageBuildTraceStore(IContextPackageBuildTraceStore inner, TraceWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _writer = new AsyncTraceWriter<ContextPackageBuildResult>(
            (result, ct) => _inner.SaveAsync(result, ct),
            options);
    }

    public Task SaveAsync(ContextPackageBuildResult result, CancellationToken cancellationToken = default)
        => _writer.SaveAsync(result, cancellationToken).AsTask();

    public Task<IReadOnlyList<ContextPackageBuildResult>> QueryRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
        => _inner.QueryRecentAsync(workspaceId, collectionId, take, cancellationToken);

    public long DroppedCount => _writer.DroppedCount;
    public long WrittenCount => _writer.WrittenCount;

    public ValueTask DisposeAsync() => _writer.DisposeAsync();
}

/// <summary>
/// P5-0.4: IDecisionTraceStore 的异步写入 decorator。
/// </summary>
public sealed class AsyncDecisionTraceStore : IDecisionTraceStore, IAsyncDisposable
{
    private readonly IDecisionTraceStore _inner;
    private readonly AsyncTraceWriter<ContextDecisionRecord> _writer;

    public AsyncDecisionTraceStore(IDecisionTraceStore inner, TraceWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _writer = new AsyncTraceWriter<ContextDecisionRecord>(
            (record, ct) => _inner.SaveAsync(record, ct),
            options);
    }

    public Task SaveAsync(ContextDecisionRecord record, CancellationToken cancellationToken = default)
        => _writer.SaveAsync(record, cancellationToken).AsTask();

    public Task<IReadOnlyList<ContextDecisionRecord>> QueryRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
        => _inner.QueryRecentAsync(workspaceId, collectionId, take, cancellationToken);

    public long DroppedCount => _writer.DroppedCount;
    public long WrittenCount => _writer.WrittenCount;

    public ValueTask DisposeAsync() => _writer.DisposeAsync();
}
