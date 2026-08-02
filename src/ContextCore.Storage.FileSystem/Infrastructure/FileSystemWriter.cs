using System.Text;

namespace ContextCore.Storage.FileSystem;

/// <summary>
/// 文件写入入口。所有覆盖写、追加写和读改写事务都必须从这里经过。
/// </summary>
public sealed class FileSystemWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly FileLockProvider _locks;

    public FileSystemWriter()
        : this(new FileLockProvider())
    {
    }

    public FileSystemWriter(FileLockProvider locks)
    {
        _locks = locks;
    }

    public async Task WriteAllLinesAtomicAsync(
        string path,
        IReadOnlyList<string> lines,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _locks.AcquireWriteLockAsync(path, cancellationToken).ConfigureAwait(false);
        await WriteAllLinesAtomicUnlockedAsync(path, lines, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteAllTextAtomicAsync(
        string path,
        string text,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _locks.AcquireWriteLockAsync(path, cancellationToken).ConfigureAwait(false);
        await WriteAllTextAtomicUnlockedAsync(path, text, cancellationToken).ConfigureAwait(false);
    }

    public async Task AppendLineAsync(
        string path,
        string line,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _locks.AcquireWriteLockAsync(path, cancellationToken).ConfigureAwait(false);
        EnsureDirectory(path);

        await using var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous);
        await using var writer = new StreamWriter(stream, Utf8NoBom);
        await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 在单个写锁内批量追加多行，避免逐行获取锁的开销。
    /// </summary>
    public async Task AppendLinesAsync(
        string path,
        IReadOnlyList<string> lines,
        CancellationToken cancellationToken = default)
    {
        if (lines.Count == 0)
        {
            return;
        }

        await using var lease = await _locks.AcquireWriteLockAsync(path, cancellationToken).ConfigureAwait(false);
        EnsureDirectory(path);

        await using var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous);
        await using var writer = new StreamWriter(stream, Utf8NoBom);
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateLinesAsync(
        string path,
        Func<IReadOnlyList<string>, IReadOnlyList<string>> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        await using var lease = await _locks.AcquireWriteLockAsync(path, cancellationToken).ConfigureAwait(false);
        var existing = await ReadAllLinesUnlockedAsync(path, cancellationToken).ConfigureAwait(false);
        var updated = update(existing);
        await WriteAllLinesAtomicUnlockedAsync(path, updated, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 在跨进程写锁内执行读-改-写事务，并允许在锁内执行副作用（如写入相邻的 raw content 文件）。
    /// 锁定对象是 <paramref name="lockPath"/> 对应的 .lock 哨兵文件——同路径的 RMW 会序列化，
    /// 消除"读后写前另一进程覆盖"的 lost update。
    /// </summary>
    /// <typeparam name="T">读阶段返回的上下文类型，传递给 <paramref name="modify"/> 与 <paramref name="write"/>。</typeparam>
    /// <param name="lockPath">作为锁对象的文件路径（通常是主 metadata 文件，如 items.jsonl）。</param>
    /// <param name="read">在锁内执行的读阶段，返回上下文。</param>
    /// <param name="modify">在锁内执行的修改阶段，接收上下文，返回要写入的行列表。</param>
    /// <param name="write">在锁内执行的最终写入阶段（在 metadata 原子替换之后调用，便于清理 cache 或写入 sidecar）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task UpdateWithSideEffectsAsync<T>(
        string lockPath,
        Func<CancellationToken, Task<T>> read,
        Func<T, CancellationToken, Task<IReadOnlyList<string>>> modify,
        Func<T, CancellationToken, Task> write,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(modify);
        ArgumentNullException.ThrowIfNull(write);

        await using var lease = await _locks.AcquireWriteLockAsync(lockPath, cancellationToken).ConfigureAwait(false);
        var context = await read(cancellationToken).ConfigureAwait(false);
        var updated = await modify(context, cancellationToken).ConfigureAwait(false);
        await WriteAllLinesAtomicUnlockedAsync(lockPath, updated, cancellationToken).ConfigureAwait(false);
        await write(context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 在跨进程写锁内执行读-改-写事务，使用同步修改函数（无额外副作用文件）。
    /// 与 <see cref="UpdateLinesAsync(string, Func{IReadOnlyList{string}, IReadOnlyList{string}}, CancellationToken)"/>
    /// 的区别：本方法暴露读阶段的 CancellationToken，便于读阶段也响应取消。
    /// </summary>
    public async Task UpdateLinesAsync(
        string path,
        Func<IReadOnlyList<string>, CancellationToken, IReadOnlyList<string>> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        await using var lease = await _locks.AcquireWriteLockAsync(path, cancellationToken).ConfigureAwait(false);
        var existing = await ReadAllLinesUnlockedAsync(path, cancellationToken).ConfigureAwait(false);
        var updated = update(existing, cancellationToken);
        await WriteAllLinesAtomicUnlockedAsync(path, updated, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 在跨进程写锁内执行读-改-写事务；回调返回 null 时跳过写入，避免在文件不存在或
    /// "未找到目标" 场景下创建空文件。用于 delete-by-id 等"无修改即不写"语义的 RMW。
    /// </summary>
    /// <returns>true 表示已写入；false 表示回调跳过（未发生磁盘写入）。</returns>
    public async Task<bool> TryUpdateLinesAsync(
        string path,
        Func<IReadOnlyList<string>, CancellationToken, IReadOnlyList<string>?> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        await using var lease = await _locks.AcquireWriteLockAsync(path, cancellationToken).ConfigureAwait(false);
        var existing = await ReadAllLinesUnlockedAsync(path, cancellationToken).ConfigureAwait(false);
        var updated = update(existing, cancellationToken);
        if (updated is null)
        {
            return false;
        }

        await WriteAllLinesAtomicUnlockedAsync(path, updated, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task DeleteIfExistsAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _locks.AcquireWriteLockAsync(path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static async Task<IReadOnlyList<string>> ReadAllLinesUnlockedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return Array.Empty<string>();
        }

        var lines = new List<string>();
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            lines.Add(line);
        }

        return lines;
    }

    private static async Task WriteAllLinesAtomicUnlockedAsync(
        string path,
        IReadOnlyList<string> lines,
        CancellationToken cancellationToken)
    {
        // 流式逐行写入临时文件，避免 string.Join 产生的大字符串分配。
        // 旧实现使用 string.Join(Environment.NewLine, lines) 拼接全量文本后再 WriteAllTextAsync，
        // 大文件写入时同时持有行数组、拼接后的完整字符串和 UTF-8 编码缓冲。
        // 新实现直接打开临时文件逐行写入，然后 atomic replace。
        EnsureDirectory(path);
        CleanupStaleTempFiles(path);

        var tempPath = CreateTempPath(path);
        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous))
            await using (var writer = new StreamWriter(stream, Utf8NoBom))
            {
                foreach (var line in lines)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                }

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            ReplaceWithTemp(path, tempPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static async Task WriteAllTextAtomicUnlockedAsync(
        string path,
        string text,
        CancellationToken cancellationToken)
    {
        EnsureDirectory(path);
        CleanupStaleTempFiles(path);

        var tempPath = CreateTempPath(path);
        try
        {
            await File.WriteAllTextAsync(tempPath, text, Utf8NoBom, cancellationToken).ConfigureAwait(false);
            ReplaceWithTemp(path, tempPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static void ReplaceWithTemp(string path, string tempPath)
    {
        if (File.Exists(path))
        {
            try
            {
                File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                return;
            }
            catch (PlatformNotSupportedException)
            {
            }
            catch (IOException)
            {
            }
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private static string CreateTempPath(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? ".";
        var fileName = Path.GetFileName(path);
        return Path.Combine(directory, $"{fileName}.tmp.{Guid.NewGuid():N}");
    }

    private static void CleanupStaleTempFiles(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        var prefix = Path.GetFileName(path) + ".tmp.";
        foreach (var tempFile in Directory.EnumerateFiles(directory, prefix + "*"))
        {
            try
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(tempFile);
                if (age > TimeSpan.FromMinutes(30))
                {
                    File.Delete(tempFile);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void EnsureDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}


