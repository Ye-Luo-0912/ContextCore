using System.Globalization;

namespace ContextCore.Storage.FileSystem;

/// <summary>
/// Trace 日期分片 retention 清理器。
/// 在 SaveAsync 中按时间间隔（1 小时）触发，删除超过 <see cref="FileStorageOptions.TraceRetentionDays"/> 的 yyyyMMdd 分片目录。
/// 线程安全（CAS 占用清理槽位），fail-open（清理失败不影响写入）。
/// </summary>
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
    public void MaybePurge(string traceDirectory, CancellationToken cancellationToken)
    {
        if (_retentionDays <= 0 || string.IsNullOrEmpty(traceDirectory))
        {
            return;
        }

        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        var lastTicks = Interlocked.Read(ref _lastPurgeTicks);
        if (nowTicks - lastTicks < PurgeIntervalTicks)
        {
            return;
        }

        // CAS 占用清理槽位，避免多线程并发重复清理
        if (Interlocked.CompareExchange(ref _lastPurgeTicks, nowTicks, lastTicks) != lastTicks)
        {
            return;
        }

        try
        {
            PurgeExpiredShards(traceDirectory, cancellationToken);
        }
        catch
        {
            // fail-open: retention 失败不影响写入路径
        }
    }

    private void PurgeExpiredShards(string traceDirectory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(traceDirectory))
        {
            return;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-_retentionDays);

        foreach (var dir in Directory.EnumerateDirectories(traceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
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
