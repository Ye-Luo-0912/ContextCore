using System.Collections.Concurrent;

namespace ContextCore.Storage.FileSystem;

/// <summary>
/// 为文件写入提供同进程和多进程锁。
/// 同进程使用内存信号量降低竞争，多进程使用相邻 lock 文件的独占句柄。
/// </summary>
/// <remarks>
/// P0-9.3：旧实现使用静态 ConcurrentDictionary&lt;string, SemaphoreSlim&gt;，
/// GetOrAdd 创建 SemaphoreSlim 但从不移除，每个曾经写入过的文件路径都会永久保留一个信号量。
/// 日期分片持续增长后，锁对象数量也会持续增长（内存泄漏）。
/// 新实现使用引用计数的 LockEntry：WaitAsync 前递增 RefCount（包含等待中的线程），
/// Dispose 时释放信号量并递减 RefCount；RefCount 归零时从字典 CAS 移除。
/// </remarks>
public sealed class FileLockProvider
{
    private static readonly ConcurrentDictionary<string, LockEntry> LocalLocks = new(StringComparer.OrdinalIgnoreCase);

    public async ValueTask<FileWriteLease> AcquireWriteLockAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var entry = LocalLocks.GetOrAdd(fullPath, _ => new LockEntry());
        // P0-9.3: 在 WaitAsync 之前递增引用计数，这样计数包含正在等待的线程。
        // 持有者释放时若 RefCount 仍 > 1，说明有等待者，不移除。
        lock (entry)
        {
            entry.RefCount++;
        }

        try
        {
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // WaitAsync 失败（如取消）：信号量未获取，只需递减引用计数。
            lock (entry)
            {
                entry.RefCount--;
                MaybeReclaimLock(fullPath, entry);
            }
            throw;
        }

        try
        {
            var lockPath = fullPath + ".lock";
            var directory = Path.GetDirectoryName(lockPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var stream = await OpenLockStreamAsync(lockPath, cancellationToken).ConfigureAwait(false);
            return new FileWriteLease(entry, stream, fullPath);
        }
        catch
        {
            // 跨进程锁获取失败：已获取内存信号量，需释放后递减引用计数。
            entry.Gate.Release();
            lock (entry)
            {
                entry.RefCount--;
                MaybeReclaimLock(fullPath, entry);
            }
            throw;
        }
    }

    /// <summary>
    /// P0-9.3：释放租约时调用。释放内存信号量并递减引用计数；
    /// 若 RefCount 归零（无等待者），从静态字典 CAS 移除 LockEntry，避免锁对象无限增长。
    /// </summary>
    internal static void ReleaseEntry(string fullPath, LockEntry entry)
    {
        entry.Gate.Release();
        lock (entry)
        {
            entry.RefCount--;
            MaybeReclaimLock(fullPath, entry);
        }
    }

    /// <summary>
    /// P0-9.3：引用计数归零时尝试从字典回收 LockEntry。
    /// 使用 ICollection.Remove（CAS 语义）：只在字典中的值仍为当前 entry 实例时移除，
    /// 避免误删其他线程新创建的 LockEntry。
    /// </summary>
    private static void MaybeReclaimLock(string fullPath, LockEntry entry)
    {
        if (entry.RefCount > 0)
        {
            return;
        }

        // CAS 移除：KeyValuePair.Equals 对引用类型使用引用相等，
        // 只有当字典中的值仍是当前 entry 实例时才移除。
        ((ICollection<KeyValuePair<string, LockEntry>>)LocalLocks)
            .Remove(new KeyValuePair<string, LockEntry>(fullPath, entry));
    }

    private static async Task<FileStream> OpenLockStreamAsync(
        string lockPath,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// P0-9.3：引用计数的锁条目。RefCount 在 lock(this) 保护下访问。
/// Gate 信号量保证同进程互斥；RefCount 跟踪所有持有或正在等待该锁的线程数。
/// </summary>
internal sealed class LockEntry
{
    public readonly SemaphoreSlim Gate = new(1, 1);
    public int RefCount;
}

/// <summary>文件写锁租约，释放时关闭多进程锁文件并释放本地信号量。</summary>
public sealed class FileWriteLease : IAsyncDisposable
{
    private readonly LockEntry _entry;
    private readonly FileStream _stream;
    private readonly string _fullPath;
    private bool _disposed;

    internal FileWriteLease(LockEntry entry, FileStream stream, string fullPath)
    {
        _entry = entry;
        _stream = stream;
        _fullPath = fullPath;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stream.DisposeAsync().ConfigureAwait(false);
        // P0-9.3：通过 ReleaseEntry 统一释放信号量 + 递减引用计数 + 可能回收。
        FileLockProvider.ReleaseEntry(_fullPath, _entry);
    }
}
