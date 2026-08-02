namespace ContextCore.Storage.FileSystem;

/// <summary>
/// Trace store 共享的 QueryRecent 尾部读取辅助。
/// 按文件 mtime 降序枚举，从每个文件尾部反序列化（append-only 下最新记录在末尾），
/// 累积到 budget = take*2 条记录后停止读更多文件，避免随历史分片总量线性恶化。
/// 最终排序由调用方完成。
/// </summary>
/// <remarks>
/// 旧实现调用 ReadAsync 全量反序列化每个分片文件，大历史文件开销随行数线性增长。
/// 新实现调用 ReadTailAsync，每个文件只反序列化尾部剩余预算行数的记录，收集够后立即停止。
/// </remarks>
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

            // 只反序列化文件尾部剩余预算行数的记录，不再全量反序列化。
            // append-only 下最新记录在文件末尾，从尾部读取可在收集够后早停。
            var remaining = budget - results.Count;
            var records = await jsonLines.ReadTailAsync<T>(path, remaining, cancellationToken)
                .ConfigureAwait(false);

            // ReadTailAsync 返回最新在前（文件末尾 → 头部），直接追加保持 newest-first 顺序。
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
