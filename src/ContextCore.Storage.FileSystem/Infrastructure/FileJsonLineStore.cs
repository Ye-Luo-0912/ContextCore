namespace ContextCore.Storage.FileSystem;

/// <summary>
/// 提供基于 JSONL（JSON Lines）格式的文件读写辅助功能。
/// 读取只经过 <see cref="FileSystemReader"/>；写入、追加和 Upsert 只经过 <see cref="FileSystemWriter"/>。
/// </summary>
public sealed class FileJsonLineStore
{
    private readonly FileFormatSerializer _serializer;
    private readonly FileSystemReader _reader;
    private readonly FileSystemWriter _writer;

    /// <summary>
    /// 使用指定的序列化器初始化 <see cref="FileJsonLineStore"/>。
    /// </summary>
    public FileJsonLineStore(FileFormatSerializer serializer)
        : this(serializer, new FileSystemReader(), new FileSystemWriter())
    {
    }

    public FileJsonLineStore(
        FileFormatSerializer serializer,
        FileSystemReader reader,
        FileSystemWriter writer)
    {
        _serializer = serializer;
        _reader = reader;
        _writer = writer;
    }

    /// <summary>
    /// 从 JSONL 文件读取所有记录。文件不存在时返回空列表，损坏行会被跳过。
    /// </summary>
    public async Task<IReadOnlyList<T>> ReadAsync<T>(
        string path,
        CancellationToken cancellationToken = default)
    {
        var lines = await _reader.ReadAllLinesAsync(path, cancellationToken)
            .ConfigureAwait(false);

        return DeserializeLines<T>(lines);
    }

    /// <summary>
    /// 将记录集合完整写入 JSONL 文件（覆盖原有内容）。
    /// </summary>
    public async Task WriteAsync<T>(
        string path,
        IEnumerable<T> items,
        CancellationToken cancellationToken = default)
    {
        var lines = items.Select(item => _serializer.Serialize(item)).ToArray();
        await _writer.WriteAllLinesAtomicAsync(path, lines, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 追加一条 JSONL 记录。追加操作会获取文件写锁，避免多进程交叉写入。
    /// </summary>
    public async Task AppendAsync<T>(
        string path,
        T item,
        CancellationToken cancellationToken = default)
    {
        await _writer.AppendLineAsync(path, _serializer.Serialize(item), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 按键对 JSONL 文件中的记录执行 Upsert（存在则更新，不存在则追加）。
    /// 读改写在同一个写锁内完成，避免并发写入互相覆盖。
    /// </summary>
    public async Task UpsertAsync<T>(
        string path,
        T item,
        Func<T, string> keySelector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        var key = keySelector(item);

        await _writer.UpdateLinesAsync(
            path,
            lines =>
            {
                var existing = DeserializeLines<T>(lines);
                return existing
                    .Where(e => !string.Equals(keySelector(e), key, StringComparison.OrdinalIgnoreCase))
                    .Append(item)
                    .Select(_serializer.Serialize)
                    .ToArray();
            },
            cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<T> DeserializeLines<T>(IReadOnlyList<string> lines)
    {
        var items = new List<T>();
        foreach (var line in lines.Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            try
            {
                var item = _serializer.Deserialize<T>(line);
                if (item is not null)
                {
                    items.Add(item);
                }
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }

        return items;
    }
}
