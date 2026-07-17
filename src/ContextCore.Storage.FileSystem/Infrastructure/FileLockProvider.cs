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
/// R13.1 #1：修复 retired-entry 竞态——原实现 GetOrAdd 返回 entry 与 lock(entry){RefCount++}
/// 之间存在未保护窗口，并发释放者可在此期间回收 entry（从字典 CAS 移除），导致后续 acquirer
/// 持有已退休的 entry 并复活其 RefCount。此时新 acquirer 通过 GetOrAdd 创建全新 entry，
/// 两个 entry 各持独立 SemaphoreSlim，进程内 Gate 互斥失效（退化为 25ms 文件锁自旋）。
/// 修复：用 lock(LocalLocks) 全局锁包裹 GetOrAdd + RefCount++ 使其原子化；
/// 临界区为 O(1) 哈希查找 + int 自增，昂贵操作（Gate.WaitAsync、文件 I/O）仍在锁外。
/// 所有 RefCount 访问统一在 lock(LocalLocks) 下，不再需要 lock(entry)。
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
        // R13.1 #1: GetOrAdd + RefCount++ 必须原子化，否则并发释放者可在两者之间回收 entry，
        // 导致 acquirer 持有已退休 entry（已从字典移除）并复活 RefCount，破坏进程内 Gate 互斥。
        // 全局锁临界区极小（哈希查找 + int 自增），Gate.WaitAsync 与文件 I/O 在锁外执行。
        LockEntry entry;
        lock (LocalLocks)
        {
            entry = LocalLocks.GetOrAdd(fullPath, _ => new LockEntry());
            entry.RefCount++;
        }

        try
        {
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // WaitAsync 失败（如取消）：信号量未获取，只需递减引用计数。
            DecrementAndMaybeReclaim(fullPath, entry);
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
            DecrementAndMaybeReclaim(fullPath, entry);
            throw;
        }
    }

    /// <summary>
    /// P0-9.3：释放租约时调用。释放内存信号量并递减引用计数；
    /// 若 RefCount 归零（无等待者），从静态字典 CAS 移除 LockEntry，避免锁对象无限增长。
    /// R13.1 #1：RefCount 递减 + 回收判定统一在 lock(LocalLocks) 下完成，与 Acquire 路径使用同一锁，
    /// 保证"递减 RefCount → 检查是否归零 → 从字典移除"三步原子化，消除竞态窗口。
    /// </summary>
    internal static void ReleaseEntry(string fullPath, LockEntry entry)
    {
        entry.Gate.Release();
        DecrementAndMaybeReclaim(fullPath, entry);
    }

    /// <summary>
    /// R13.1 #1：统一 RefCount 递减 + 回收逻辑。在 lock(LocalLocks) 下完成，
    /// 与 AcquireWriteLockAsync 的 GetOrAdd+RefCount++ 路径互斥，消除 retired-entry 竞态。
    /// </summary>
    private static void DecrementAndMaybeReclaim(string fullPath, LockEntry entry)
    {
        lock (LocalLocks)
        {
            entry.RefCount--;
            MaybeReclaimLock(fullPath, entry);
        }
    }

    /// <summary>
    /// P0-9.3：引用计数归零时尝试从字典回收 LockEntry。
    /// 使用 ICollection.Remove（CAS 语义）：只在字典中的值仍为当前 entry 实例时移除，
    /// 避免误删其他线程新创建的 LockEntry。
    /// R13.1 #1：调用方已持有 lock(LocalLocks)，此处无需再加锁。
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
