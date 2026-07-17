using System.Globalization;

namespace ContextCore.Storage.FileSystem;

/// <summary>
/// Trace 日期分片 retention 清理器。
/// 在 SaveAsync 中按时间间隔（1 小时）触发，删除超过 <see cref="FileStorageOptions.TraceRetentionDays"/> 的 yyyyMMdd 分片目录。
/// 线程安全（CAS 占用清理槽位），fail-open（清理失败不影响写入）。
/// </summary>
/// <remarks>
/// R13.1 #3：retention 按自然日（UTC 日历日）边界判定。cutoff 对齐到今日 UTC 午夜再减
/// <see cref="FileStorageOptions.TraceRetentionDays"/>，分片日期（yyyyMMdd 解析为当日 00:00 UTC）
/// 严格早于 cutoff 才删除。这保证清理决策在一天内任意时刻一致，不再随时分秒漂移：
/// 保留今日与前 N 个完整自然日（共 N+1 天），第 N+1 天前的分片被清理。
/// </remarks>
internal sealed class FileTraceJanitor
{
    private static readonly long PurgeIntervalTicks = TimeSpan.FromHours(1).Ticks;

    private readonly int _retentionDays;
    private long _lastPurgeTicks; // 0 表示从未执行；Interlocked 读/写

    public FileTraceJanitor(FileStorageOptions options)
    {
        _retentionDays = options?.TraceRetentionDays ?? 0;
    }

    /// <summary>
    /// 检查是否到达清理间隔，是则删除过期分片。fail-open，不抛异常。
    /// </summary>
    /// <remarks>
    /// R13.1 #4：retention 不再在 Save 热路径上同步执行。节流检查（Interlocked CAS）仍同步
    /// 且开销极小（O(1)），但命中清理槽位后，实际的目录枚举 + 删除 fire-and-forget 到线程池，
    /// 不阻塞写入线程。返回的 Task 可被调用方（或测试）观察以等待清理完成；未到期或禁用时
    /// 返回 <see cref="Task.CompletedTask"/>。
    /// purge 是 post-commit housekeeping，独立于请求生命周期——不接收请求的 CancellationToken，
    /// 即使原写入请求被取消也应完成（与缓存失效的 CancellationToken.None 约定一致）。
    /// </remarks>
    public Task MaybePurge(string traceDirectory)
    {
        if (_retentionDays <= 0 || string.IsNullOrEmpty(traceDirectory))
        {
            return Task.CompletedTask;
        }

        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        var lastTicks = Interlocked.Read(ref _lastPurgeTicks);
        if (nowTicks - lastTicks < PurgeIntervalTicks)
        {
            return Task.CompletedTask;
        }

        // CAS 占用清理槽位，避免多线程并发重复清理
        if (Interlocked.CompareExchange(ref _lastPurgeTicks, nowTicks, lastTicks) != lastTicks)
        {
            return Task.CompletedTask;
        }

        // R13.1 #4：命中槽位后将昂贵的目录 I/O 移出 Save 热路径，fire-and-forget 到线程池。
        return Task.Run(() =>
        {
            try
            {
                PurgeExpiredShards(traceDirectory);
            }
            catch
            {
                // fail-open: retention 失败不影响写入路径
            }
        });
    }

    private void PurgeExpiredShards(string traceDirectory)
    {
        if (!Directory.Exists(traceDirectory))
        {
            return;
        }

        // R13.1 #3：自然日语义——cutoff 对齐到今日 UTC 午夜再减 retentionDays，
        // 与分片日期（yyyyMMdd 解析为当日 00:00 UTC）按日历日边界比较，
        // 不再随时分秒漂移。保留今日与前 N 个完整自然日（共 N+1 天）。
        var cutoff = new DateTimeOffset(
            DateTimeOffset.UtcNow.Date.AddDays(-_retentionDays),
            TimeSpan.Zero);

        foreach (var dir in Directory.EnumerateDirectories(traceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(dir);
            if (TryParseDateShard(name, out var shardDate) && shardDate < cutoff)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch
                {
                    // 单个分片删除失败（文件占用等）跳过，下次再试
                }
            }
        }
    }

    private static bool TryParseDateShard(string name, out DateTimeOffset date)
    {
        return DateTimeOffset.TryParseExact(
            name,
            "yyyyMMdd",
            DateTimeFormatInfo.InvariantInfo,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out date);
    }
}
