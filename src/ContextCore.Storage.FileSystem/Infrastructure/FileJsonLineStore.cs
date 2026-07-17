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
    /// P0-9.1：从 JSONL 文件尾部读取最近的 <paramref name="maxCount"/> 条记录。
    /// append-only 文件中最新记录在文件末尾，从尾部反序列化可在收集够后立即停止，
    /// 避免对大历史文件逐行反序列化。返回顺序为最新在前（文件末尾 → 文件头部）。
    /// </summary>
    /// <remarks>
    /// 读取阶段仍读取全部行（I/O 顺序读，开销低），但只反序列化尾部 maxCount 行。
    /// 损坏行跳过，不计入 maxCount。maxCount <= 0 时返回空列表。
    /// </remarks>
    public async Task<IReadOnlyList<T>> ReadTailAsync<T>(
        string path,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        if (maxCount <= 0)
        {
            return Array.Empty<T>();
        }

        var lines = await _reader.ReadAllLinesAsync(path, cancellationToken)
            .ConfigureAwait(false);

        if (lines.Count == 0)
        {
            return Array.Empty<T>();
        }

        var result = new List<T>(Math.Min(maxCount, lines.Count));
        // 从末尾向前迭代：append-only 文件最新记录在最后，尾部读取可早停。
        for (var i = lines.Count - 1; i >= 0 && result.Count < maxCount; i--)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var item = _serializer.Deserialize<T>(line);
                if (item is not null)
                {
                    result.Add(item);
                }
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }

        return result;
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
    /// 在单个写锁内批量追加多条 JSONL 记录，避免逐条获取锁的开销。
    /// </summary>
    public async Task AppendRangeAsync<T>(
        string path,
        IEnumerable<T> items,
        CancellationToken cancellationToken = default)
    {
        var lines = items.Select(item => _serializer.Serialize(item)).ToArray();
        if (lines.Length == 0)
        {
            return;
        }

        await _writer.AppendLinesAsync(path, lines, cancellationToken)
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

    /// <summary>
    /// 在单个写锁内对 JSONL 文件中的记录执行读改写事务。
    /// 反序列化现有行 → 调用 <paramref name="update"/> 得到新集合 → 原子写回。
    /// 用于替代 Store 中手写的 ReadAsync → 修改 → WriteAsync 模式，保证单路径原子性。
    /// </summary>
    public async Task UpdateAsync<T>(
        string path,
        Func<IReadOnlyList<T>, IReadOnlyList<T>> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        await _writer.UpdateLinesAsync(
            path,
            lines =>
            {
                var existing = DeserializeLines<T>(lines);
                var updated = update(existing);
                return updated.Select(_serializer.Serialize).ToArray();
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
