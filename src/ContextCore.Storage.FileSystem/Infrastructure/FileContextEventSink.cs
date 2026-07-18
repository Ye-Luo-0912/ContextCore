using ContextCore.Abstractions;

namespace ContextCore.Storage.FileSystem;

/// <summary>
/// 将上下文操作事件追加写入到本地日志文件的事件接收器。
/// 每个工作空间的事件记录在独立的 JSONL 日志文件中，以便后期审计与排查。
/// </summary>
public sealed class FileContextEventSink : IContextEventSink
{
    private readonly string _logsRoot;
    private readonly FileFormatSerializer _serializer = new();
    private readonly FileSystemWriter _writer;

    /// <summary>
    /// 使用指定的日志根目录初始化 <see cref="FileContextEventSink"/>。
    /// </summary>
    /// <param name="logsRoot">日志文件的根目录路径。</param>
    public FileContextEventSink(string logsRoot)
        : this(logsRoot, new FileSystemWriter())
    {
    }

    public FileContextEventSink(string logsRoot, FileSystemWriter writer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsRoot);
        _logsRoot = logsRoot;
        _writer = writer;
    }

    /// <summary>
    /// 将事件序列化后追加写入对应工作空间的日志文件。
    /// </summary>
    /// <param name="operationEvent">要记录的操作事件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task EmitAsync(
        ContextOperationEvent operationEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationEvent);

        var logPath = GetLogPath(operationEvent.WorkspaceId);
        var line = _serializer.Serialize(operationEvent);
        await _writer.AppendLineAsync(logPath, line, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// R13.4 #1：批量写入路径。按工作空间分组后，每组一次性调用 <see cref="FileSystemWriter.AppendLinesAsync"/>
    /// 在单个写锁内追加所有行，避免逐行获取锁的开销。
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

        // 按工作空间分组：不同 workspace 落入不同文件，无法共享一次锁
        var groupedByWorkspace = events
            .GroupBy(evt => evt.WorkspaceId)
            .ToArray();

        foreach (var group in groupedByWorkspace)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logPath = GetLogPath(group.Key);
            var lines = new List<string>(group.Count());
            foreach (var evt in group)
            {
                lines.Add(_serializer.Serialize(evt));
            }
            await _writer.AppendLinesAsync(logPath, lines, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private string GetLogPath(string workspaceId)
    {
        var date = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        return Path.Combine(_logsRoot, workspaceId, $"events-{date}.jsonl");
    }
}
