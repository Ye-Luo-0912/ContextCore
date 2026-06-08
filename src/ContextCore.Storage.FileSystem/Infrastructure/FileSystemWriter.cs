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
        var text = string.Join(Environment.NewLine, lines);
        if (lines.Count > 0)
        {
            text += Environment.NewLine;
        }

        await WriteAllTextAtomicUnlockedAsync(path, text, cancellationToken).ConfigureAwait(false);
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


