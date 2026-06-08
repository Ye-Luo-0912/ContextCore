using System.Collections.Concurrent;

namespace ContextCore.Storage.FileSystem;

/// <summary>
/// 为文件写入提供同进程和多进程锁。
/// 同进程使用内存信号量降低竞争，多进程使用相邻 lock 文件的独占句柄。
/// </summary>
public sealed class FileLockProvider
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> LocalLocks = new(StringComparer.OrdinalIgnoreCase);

    public async ValueTask<FileWriteLease> AcquireWriteLockAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var gate = LocalLocks.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var lockPath = fullPath + ".lock";
            var directory = Path.GetDirectoryName(lockPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var stream = await OpenLockStreamAsync(lockPath, cancellationToken).ConfigureAwait(false);
            return new FileWriteLease(gate, stream);
        }
        catch
        {
            gate.Release();
            throw;
        }
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

/// <summary>文件写锁租约，释放时关闭多进程锁文件并释放本地信号量。</summary>
public sealed class FileWriteLease : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate;
    private readonly FileStream _stream;
    private bool _disposed;

    internal FileWriteLease(SemaphoreSlim gate, FileStream stream)
    {
        _gate = gate;
        _stream = stream;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stream.DisposeAsync().ConfigureAwait(false);
        _gate.Release();
    }
}
