namespace ContextCore.Storage.FileSystem;

/// <summary>
/// Trace store 共享的 QueryRecent 尾部读取辅助。
/// 按文件 mtime 降序枚举，累积到 budget = take*2 条记录后停止读更多文件，
/// 避免随历史分片总量线性恶化。最终排序由调用方完成。
/// </summary>
internal static class TraceQueryHelper
{
    public static async Task<IReadOnlyList<T>> ReadRecentAsync<T>(
        IReadOnlyList<string> paths,
        int take,
        FileJsonLineStore jsonLines,
        Func<T, string> keySelector,
        Func<T, bool>? filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(jsonLines);
        ArgumentNullException.ThrowIfNull(keySelector);

        var count = take > 0 ? take : 50;
        // 安全余量：append-only + 日期分片下，同一文件内 CreatedAt 单调递增，
        // 但跨文件（legacy/并发写入）可能乱序，保留 2x 余量保证最终 Take 后排序正确。
        var budget = count * 2;
        var results = new List<T>(Math.Min(budget, 256));
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (results.Count >= budget)
            {
                break;
            }

            var records = await jsonLines.ReadAsync<T>(path, cancellationToken)
                .ConfigureAwait(false);
            foreach (var record in records)
            {
                if (filter is not null && !filter(record))
                {
                    continue;
                }

                var key = keySelector(record);
                if (string.IsNullOrWhiteSpace(key))
                {
                    key = Guid.NewGuid().ToString("N");
                }

                if (keys.Add(key))
                {
                    results.Add(record);
                }
            }
        }

        return results;
    }
}
