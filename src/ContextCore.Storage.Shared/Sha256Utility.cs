using System.Security.Cryptography;
using System.Text;

namespace ContextCore.Storage.Shared;

/// <summary>
/// P1-2：跨层共享的 SHA-256 工具，供备份清单、artifact 校验等使用。
/// 统一 hex 编码（小写）与流式读取语义，避免每个调用点重复实现。
/// </summary>
/// <remarks>
/// 设计选择：
/// <list type="bullet">
/// <item>小写 hex 输出，与 <see cref="ContextCoreDataLayout.ComputeContentHash"/> 既有契约一致。</item>
/// <item>流式读取避免一次性将大文件载入内存；与 <c>FileSystemReader</c> 的流式取向对齐。</item>
/// <item>不缓存结果——每次调用都重新计算，调用方按需缓存。</item>
/// <item>线程安全：静态方法无共享可变状态。</item>
/// </list>
/// </remarks>
public static class Sha256Utility
{
    /// <summary>
    /// 计算文件流的 SHA-256 哈希（hex 小写）。
    /// 流会被读取到末尾但不被 dispose，调用方负责释放。
    /// </summary>
    public static string HashStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// 计算文件的 SHA-256 哈希（hex 小写）。文件不存在时抛 <see cref="FileNotFoundException"/>。
    /// 内部以 <c>FileShare.ReadWrite | Delete</c> 打开，与运行时并发读取兼容。
    /// </summary>
    public static string HashFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("File not found while computing SHA-256.", path);
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return HashStream(stream);
    }

    /// <summary>
    /// 计算字符串 UTF-8 编码字节的 SHA-256 哈希（hex 小写）。
    /// 供 manifest 字段（如元数据指纹）使用；不应直接用于大文件。
    /// </summary>
    public static string HashString(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }
}
