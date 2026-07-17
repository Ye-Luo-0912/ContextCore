using System.Collections.Concurrent;

namespace ContextCore.Storage.FileSystem;

/// <summary>
/// R13.1 #6：FileSystem 单实例与多进程支持边界守护（advisory，不阻断）。
/// </summary>
/// <remarks>
/// FileSystem 后端的并发边界（R13.1 #1–#5 后）：
/// <list type="bullet">
/// <item><b>跨进程安全（单文件）</b>：所有写入经 <see cref="FileLockProvider"/> 获取
/// <c>&lt;file&gt;.lock</c> 独占句柄（Enqueue / Dequeue / Ack / Nack / Append / Upsert / Update），
/// 读取经 <see cref="System.IO.FileShare"/>.ReadWrite。单文件读改写原子。</item>
/// <item><b>进程内（每进程独立，多进程下退化）</b>：
/// <see cref="Stores.FileContextJobQueue"/> 的 JobId→路径索引（<c>_jobPathIndex</c>，未命中回退扫描）、
/// <see cref="FileTraceJanitor"/> 的清理节流（<c>_lastPurgeTicks</c>，每进程各自 fire-and-forget）、
/// 以及 <c>ContextStateCache</c>（生产已关闭，见 R13.0）。这些在多进程下命中率下降但正确性不变。</item>
/// <item><b>无跨进程保证</b>：跨文件一致性（raw content + metadata 双文件）无事务原子性；
/// 进程崩溃可能留下 orphan raw 或指向不存在 raw 的 metadata。</item>
/// </list>
/// 本守护在进程首次访问某 root 时尝试独占 <c>&lt;root&gt;/.context-core-instance.lock</c>：
/// 获取成功 → <see cref="IsMultiProcessDetected"/>=false（本进程是该 root 主实例）；
/// 获取失败（IOException，他进程已持有）→ <see cref="IsMultiProcessDetected"/>=true。
/// 这是 advisory：不抛异常、不阻断——文件锁仍保证单文件写入正确性，
/// 但进程内优化命中率会下降。守护按 root 路径在进程内单例缓存（同进程多 store 共享同一结果，
/// 避免本进程第二个 store 自锁误报）。锁句柄以 <see cref="System.IO.FileShare"/>.Read 共享：
/// 他进程可读 sentinel 存在但不可写/独占 → 检测命中。锁句柄在进程内按 root 单例持有至进程退出；
/// 测试需在删除 root 目录前调用 <see cref="ResetCacheForTests"/> 释放句柄（持有期间目录不可删）。
/// </remarks>
public sealed class FileSystemInstanceGuard
{
    private static readonly ConcurrentDictionary<string, FileSystemInstanceGuard> s_cache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly FileStream? _lease;
    private readonly bool _isMultiProcessDetected;

    private FileSystemInstanceGuard(FileStream? lease, bool isMultiProcessDetected)
    {
        _lease = lease;
        _isMultiProcessDetected = isMultiProcessDetected;
    }

    /// <summary>
    /// 是否检测到另一进程已持有同一 root 的实例锁。
    /// true 表示多进程模式：文件锁仍保证单文件写入正确性，但进程内优化（索引/缓存/节流）命中率下降。
    /// </summary>
    public bool IsMultiProcessDetected => _isMultiProcessDetected;

    /// <summary>
    /// 获取或创建指定 root 的进程内单例守护。首次调用尝试独占 sentinel 锁文件并缓存结果；
    /// 同进程后续调用直接返回缓存，避免本进程内自锁误报。
    /// </summary>
    public static FileSystemInstanceGuard GetOrCreate(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var fullPath = Path.GetFullPath(rootPath);
        return s_cache.GetOrAdd(fullPath, static path => Create(path));
    }

    private static FileSystemInstanceGuard Create(string fullPath)
    {
        Directory.CreateDirectory(fullPath);
        var sentinel = Path.Combine(fullPath, ".context-core-instance.lock");
        try
        {
            var stream = new FileStream(
                sentinel,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                // 他进程可读（看到锁存在）但不可写/独占 → 检测命中。
                FileShare.Read,
                bufferSize: 1,
                FileOptions.Asynchronous);
            return new FileSystemInstanceGuard(stream, isMultiProcessDetected: false);
        }
        catch (IOException)
        {
            // 他进程已持有该 sentinel（不可写/独占）：advisory 标记，不抛。
            return new FileSystemInstanceGuard(lease: null, isMultiProcessDetected: true);
        }
    }

    /// <summary>测试用：清空进程内单例缓存并释放持有的锁句柄。生产代码不应调用。</summary>
    internal static void ResetCacheForTests()
    {
        foreach (var guard in s_cache.Values)
        {
            guard._lease?.Dispose();
        }
        s_cache.Clear();
    }
}
