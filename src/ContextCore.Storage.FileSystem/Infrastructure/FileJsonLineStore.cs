using System.Runtime.CompilerServices;

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
    /// 流式逐行读取并反序列化 JSONL 文件，避免一次性将所有记录载入 List。
    /// 文件不存在时返回空枚举；损坏行（反序列化失败）跳过且不产出。
    /// </summary>
    public async IAsyncEnumerable<T> StreamAsync<T>(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var line in _reader.ReadLinesStreamAsync(path, cancellationToken).ConfigureAwait(false))
        {
            T? item;
            try
            {
                item = _serializer.Deserialize<T>(line);
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }
            if (item is not null)
            {
                yield return item;
            }
        }
    }

    /// <summary>
    /// 从 JSONL 文件尾部读取最近的 <paramref name="maxCount"/> 条记录。
    /// append-only 文件中最新记录在文件末尾，从尾部反序列化可在收集够后立即停止，
    /// 避免对大历史文件逐行反序列化。返回顺序为最新在前（文件末尾 → 文件头部）。
    /// </summary>
    /// <remarks>
    /// 读取使用 <see cref="FileSystemReader.ReadLinesReverseAsync"/>：从文件尾部按块反向 I/O，
    /// 仅读取产出 maxCount 条非空白行所需的尾部字节，不再全量读取整个文件。
    /// 空白行在读取阶段即被跳过；损坏行（反序列化失败）跳过且不计入 maxCount。
    /// maxCount &lt;= 0 时返回空列表。
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

        // 反向 I/O 读取——仅读取尾部所需字节，newest-first，空白行已跳过。
        var lines = await _reader.ReadLinesReverseAsync(path, maxCount, cancellationToken)
            .ConfigureAwait(false);

        if (lines.Count == 0)
        {
            return Array.Empty<T>();
        }

        var result = new List<T>(lines.Count);
        foreach (var line in lines)
        {
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

    /// <summary>
    /// 在单个写锁内对 JSONL 文件执行读改写；回调返回 null 时跳过写入，
    /// 用于 delete-by-id 等"无修改即不写"语义——文件不存在或未匹配目标时不会创建空文件。
    /// </summary>
    /// <returns>true 表示已写入；false 表示回调跳过（未发生磁盘写入）。</returns>
    public async Task<bool> TryUpdateAsync<T>(
        string path,
        Func<IReadOnlyList<T>, IReadOnlyList<T>?> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        return await _writer.TryUpdateLinesAsync(
            path,
            (lines, ct) =>
            {
                var existing = DeserializeLines<T>(lines);
                var updated = update(existing);
                return updated is null ? null : updated.Select(_serializer.Serialize).ToArray();
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
