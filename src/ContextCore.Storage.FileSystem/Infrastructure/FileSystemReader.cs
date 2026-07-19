using System.Runtime.CompilerServices;
using System.Text;

namespace ContextCore.Storage.FileSystem;

/// <summary>
/// 文件读取入口。只负责读取，不创建目录、不写入、不修改文件。
/// </summary>
public sealed class FileSystemReader
{
    public bool Exists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.Exists(path);
    }
    public async Task<IReadOnlyList<string>> ReadAllLinesAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return Array.Empty<string>();
        }

        await using var stream = OpenReadStream(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
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

    /// <summary>
    /// P1-7：流式逐行读取 JSONL 文件，避免一次性将所有行载入 List。
    /// 文件不存在时返回空枚举（不抛异常）。空白行在产出前跳过。
    /// </summary>
    public async IAsyncEnumerable<string> ReadLinesStreamAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            yield break;
        }

        await using var stream = OpenReadStream(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line;
            }
        }
    }

    public async Task<string?> ReadAllTextAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = OpenReadStream(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// R13.1 #2：从文件尾部向前反向读取行，按 newest-first 顺序返回最多 <paramref name="maxCount"/> 条非空白行。
    /// 仅读取产出 <paramref name="maxCount"/> 行所需的尾部字节，避免对大历史 append-only 文件全量 I/O。
    /// </summary>
    /// <remarks>
    /// 支持混合的 \n 与 \r\n 行结束符；UTF-8 多字节字符不会在块边界被截断
    /// （\n 为单字节 0x0A，UTF-8 续续字节范围 0x80-0xBF 不含 0x0A，因此在字节流上按 \n 切分是安全的）。
    /// 空白行（含尾部 \n 产生的空行）被跳过且不计入 <paramref name="maxCount"/>。
    /// 文件不存在或 maxCount &lt;= 0 时返回空列表。
    /// </remarks>
    public async Task<IReadOnlyList<string>> ReadLinesReverseAsync(
        string path,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (maxCount <= 0 || !File.Exists(path))
        {
            return Array.Empty<string>();
        }

        await using var stream = OpenRandomReadStream(path);
        var fileLength = stream.Length;
        if (fileLength == 0)
        {
            return Array.Empty<string>();
        }

        const int ChunkSize = 8 * 1024;
        var buffer = new byte[ChunkSize];
        var results = new List<string>(Math.Min(maxCount, 64));
        // 未完成行的尾部字节（其头部在更早的块中）。读取方向是向前回溯，
        // 所以 tail 始终是某行较晚的字节，head 在更早的块中。拼接顺序为 head ++ tail。
        byte[] tail = Array.Empty<byte>();
        var position = fileLength;

        while (results.Count < maxCount && position > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var readSize = (int)Math.Min(ChunkSize, position);
            position -= readSize;
            stream.Position = position;

            var bytesRead = 0;
            while (bytesRead < readSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var n = await stream.ReadAsync(buffer.AsMemory(bytesRead, readSize - bytesRead), cancellationToken)
                    .ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                bytesRead += n;
            }

            // 在本块内从后向前扫描 \n 分隔符。
            // 最后一个 \n 之后到块尾的字节 + tail 构成一条完整行。
            var segmentEnd = bytesRead;
            for (var i = bytesRead - 1; i >= 0; i--)
            {
                if (buffer[i] != (byte)'\n')
                {
                    continue;
                }

                if (TryDecodeLine(buffer, i + 1, segmentEnd - (i + 1), tail, out var line))
                {
                    results.Add(line);
                }

                tail = Array.Empty<byte>();
                segmentEnd = i;

                if (results.Count >= maxCount)
                {
                    return results;
                }
            }

            // 块剩余 [0, segmentEnd) 是某行未完成部分的头部，前置到 tail。
            if (segmentEnd > 0)
            {
                var newTail = new byte[segmentEnd + tail.Length];
                Buffer.BlockCopy(buffer, 0, newTail, 0, segmentEnd);
                Buffer.BlockCopy(tail, 0, newTail, segmentEnd, tail.Length);
                tail = newTail;
            }
        }

        // 到达文件起始：剩余 tail 是文件首行（前面没有 \n）。
        if (results.Count < maxCount && tail.Length > 0)
        {
            if (TryDecodeLine(Array.Empty<byte>(), 0, 0, tail, out var line))
            {
                results.Add(line);
            }
        }

        return results;
    }

    /// <summary>
    /// 组合 head (buffer[offset, offset+count)) + tail，剥离末尾 \r（\r\n 结束符），
    /// 解码为 UTF-8 字符串。全空白行返回 false（不产出，调用方继续扫描下一条）。
    /// </summary>
    private static bool TryDecodeLine(byte[] buffer, int offset, int count, byte[] tail, out string line)
    {
        line = null!;
        var total = count + tail.Length;
        if (total == 0)
        {
            return false;
        }

        // 拼接 head + tail 到单一源以解码。
        byte[] source;
        int srcOffset;
        if (tail.Length == 0)
        {
            source = buffer;
            srcOffset = offset;
        }
        else if (count == 0)
        {
            source = tail;
            srcOffset = 0;
        }
        else
        {
            var combined = new byte[total];
            Buffer.BlockCopy(buffer, offset, combined, 0, count);
            Buffer.BlockCopy(tail, 0, combined, count, tail.Length);
            source = combined;
            srcOffset = 0;
        }

        // 剥离末尾 \r（处理 \r\n 结束符）。
        var len = total;
        if (source[srcOffset + len - 1] == (byte)'\r')
        {
            len--;
            if (len == 0)
            {
                return false;
            }
        }

        line = Encoding.UTF8.GetString(source, srcOffset, len);
        return !string.IsNullOrWhiteSpace(line);
    }

    private static FileStream OpenReadStream(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    /// <summary>
    /// R13.1 #2：反向读取使用的随机访问流。FileOptions.RandomAccess 提示 OS 缓存管理器
    /// 按 Seek 位置访问而非顺序预读，适合从文件尾部向前分块读取。
    /// </summary>
    private static FileStream OpenRandomReadStream(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
    }
}

