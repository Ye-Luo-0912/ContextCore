using System.Collections.Concurrent;
using System.Reflection;
using ContextCore.Storage.FileSystem;

namespace ContextCore.Tests;

/// <summary>
/// FileLockProvider retired-entry 竞态修复测试。
/// 验证 GetOrAdd + RefCount++ 原子化后：
/// - 同进程 Gate 互斥正确（并发 acquirer 共享同一 LockEntry）
/// - retire + recreate 不残留已退休 entry（RefCount 会计正确）
/// - 高并发快速 acquire/release 循环不泄漏 entry、不破坏互斥
/// </summary>
[TestClass]
[TestCategory("Storage")]
[TestCategory("Concurrency")]
public sealed class FileLockProviderTests
{
    private string? _rootPath;

    [TestInitialize]
    public void Initialize()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "cc-lockprov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_rootPath is not null && Directory.Exists(_rootPath))
        {
            try { Directory.Delete(_rootPath, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── 反射辅助：访问 FileLockProvider 内部静态状态 ──────────────────────

    // LockEntry 是 internal 类型，不能直接用作泛型参数。
    // 用非泛型 IDictionary 接口访问 LocalLocks（ConcurrentDictionary 实现 IDictionary）。
    private static System.Collections.IDictionary GetLocalLocks()
    {
        var field = typeof(FileLockProvider)
            .GetField("LocalLocks", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("FileLockProvider.LocalLocks 静态字段缺失");
        return (System.Collections.IDictionary)field.GetValue(null)!;
    }

    private static int GetEntryRefCount(object entry)
    {
        var field = entry.GetType()
            .GetField("RefCount", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("LockEntry.RefCount 字段缺失");
        return (int)field.GetValue(entry)!;
    }

    // ── 基本正确性 ──────────────────────────────────────────────────────

    /// <summary>
    /// 基本获取-释放。释放后 entry 从 LocalLocks 回收（RefCount 归零）。
    /// </summary>
    [TestMethod]
    public async Task AcquireAndRelease_EntryRetiredAfterRelease()
    {
        var provider = new FileLockProvider();
        var filePath = Path.Combine(_rootPath!, "basic.txt");

        var lease = await provider.AcquireWriteLockAsync(filePath);
        await lease.DisposeAsync();

        // 释放后 LocalLocks 应不含此路径（RefCount 归零 → CAS 移除）
        var locks = GetLocalLocks();
        var fullPath = Path.GetFullPath(filePath);
        Assert.IsFalse(locks.Contains(fullPath),
            "释放后 entry 应被回收（RefCount 归零 → 从 LocalLocks 移除），避免锁对象无限增长");
    }

    /// <summary>
    /// 持有期间 entry 保留在 LocalLocks（RefCount > 0）。
    /// </summary>
    [TestMethod]
    public async Task Acquire_EntryRetainedWhileHeld()
    {
        var provider = new FileLockProvider();
        var filePath = Path.Combine(_rootPath!, "held.txt");

        var lease = await provider.AcquireWriteLockAsync(filePath);
        try
        {
            var locks = GetLocalLocks();
            var fullPath = Path.GetFullPath(filePath);
            Assert.IsTrue(locks.Contains(fullPath),
                "持有期间 entry 必须保留在 LocalLocks");
            Assert.AreEqual(1, GetEntryRefCount(locks[fullPath]!),
                "单个持有者 RefCount 应为 1");
        }
        finally
        {
            await lease.DisposeAsync();
        }
    }

    /// <summary>
    /// retire 后重新 acquire 创建全新 LockEntry（不复活已退休 entry）。
    /// 验证两次 acquire 获取不同的 LockEntry 实例（旧 entry 已被 GC 回收）。
    /// </summary>
    [TestMethod]
    public async Task RetireAndReacquire_CreatesFreshEntry()
    {
        var provider = new FileLockProvider();
        var filePath = Path.Combine(_rootPath!, "recycle.txt");
        var fullPath = Path.GetFullPath(filePath);
        var locks = GetLocalLocks();

        var lease1 = await provider.AcquireWriteLockAsync(filePath);
        var entry1 = locks.Contains(fullPath) ? locks[fullPath]! : null;
        await lease1.DisposeAsync();
        // entry1 现已退休（从 LocalLocks 移除）

        var lease2 = await provider.AcquireWriteLockAsync(filePath);
        var entry2 = locks.Contains(fullPath) ? locks[fullPath]! : null;
        try
        {
            Assert.IsNotNull(entry1);
            Assert.IsNotNull(entry2);
            Assert.IsFalse(ReferenceEquals(entry1, entry2),
                "retire 后重新 acquire 必须创建全新 LockEntry，不得复活已退休的 entry");
        }
        finally
        {
            await lease2.DisposeAsync();
        }
    }

    // ── 并发互斥：同路径 Gate 串行化 ─────────────────────────────────────

    /// <summary>
    /// 同路径并发 acquire 必须串行化——同一时刻只有一个持有者。
    /// 用共享计数器验证互斥：进入临界区时 counter++（应==1），退出时 counter--。
    /// 若进程内 Gate 互斥失效（retired-entry 竞态），counter 可能在某时刻 > 1。
    /// 注：.lock 文件（FileShare.None）提供兜底，但 Gate 失效会导致 25ms 文件锁自旋性能退化；
    /// 此测试通过短临界区 + 高并发放大 Gate 失效的概率（若存在）。
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task ConcurrentAcquire_SamePath_MutualExclusionHolds()
    {
        var provider = new FileLockProvider();
        var filePath = Path.Combine(_rootPath!, "mutex.txt");

        var concurrentViolation = 0;
        var completedAcquires = 0;
        const int threadCount = 8;
        const int iterationsPerThread = 50;

        var tasks = Enumerable.Range(0, threadCount).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < iterationsPerThread; i++)
            {
                await using var lease = await provider.AcquireWriteLockAsync(filePath);
                // 临界区：counter 必须为 0（无其他持有者）
                var current = Interlocked.Increment(ref concurrentViolation);
                if (current != 1)
                {
                    // 互斥被破坏——立即中断
                    Assert.Fail($"进程内 Gate 互斥失效：并发持有者数 {current} > 1（retired-entry 竞态）");
                }
                // 模拟极短临界区，让其他线程有机会在释放后立即竞争
                Interlocked.Decrement(ref concurrentViolation);
                Interlocked.Increment(ref completedAcquires);
            }
        }));

        await Task.WhenAll(tasks);

        Assert.AreEqual(threadCount * iterationsPerThread, completedAcquires,
            "所有 acquire-release 循环应完成");
        Assert.AreEqual(0, concurrentViolation,
            "最终计数器应归零（所有临界区已退出）");

        // 全部完成后 LocalLocks 应为空（所有 entry 已回收）
        Assert.AreEqual(0, GetLocalLocks().Count,
            "全部释放后 LocalLocks 应为空（所有 LockEntry 已回收）");
    }

    /// <summary>
    /// 并发 acquirer 共享同一 LockEntry——验证进程内 Gate 互斥的结构性前提。
    /// 线程 A 持有 lease 时，线程 B 尝试 acquire 应阻塞在 A 的 Gate 上（而非创建新 entry）。
    /// 通过反射验证 LocalLocks 中该路径只有一个 entry（A 和 B 共享）。
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ConcurrentAcquire_SharedLockEntry_SingleEntryInDict()
    {
        var provider = new FileLockProvider();
        var filePath = Path.Combine(_rootPath!, "shared.txt");
        var fullPath = Path.GetFullPath(filePath);
        var locks = GetLocalLocks();

        // A 持有 lease
        var leaseA = await provider.AcquireWriteLockAsync(filePath);
        try
        {
            // B 尝试 acquire（应阻塞在 A 的 Gate 上）
            var bStarted = new TaskCompletionSource<bool>();
            var bAcquired = new TaskCompletionSource<bool>();
            var bTask = Task.Run(async () =>
            {
                bStarted.TrySetResult(true);
                await using var leaseB = await provider.AcquireWriteLockAsync(filePath);
                bAcquired.TrySetResult(true);
            });

            // 等 B 启动
            await bStarted.Task;
            // 给 B 时间进入 Gate.WaitAsync（阻塞）
            await Task.Delay(200);

            // 此时 A 持有、B 等待：LocalLocks 应只有一个 entry，RefCount=2（A 持有 + B 等待）
            Assert.IsTrue(locks.Contains(fullPath),
                "并发 acquire 期间 LocalLocks 必须包含该路径的 entry");
            Assert.AreEqual(2, GetEntryRefCount(locks[fullPath]!),
                "A 持有 + B 等待时 RefCount 应为 2（包含等待中的线程）");
            Assert.IsFalse(bAcquired.Task.IsCompleted,
                "B 应阻塞在 A 的 Gate 上（未获取 lease）");

            // 释放 A → B 应获取
            await leaseA.DisposeAsync();
            await bAcquired.Task;

            // B 持有期间：RefCount=1，entry 仍存在
            Assert.IsTrue(locks.Contains(fullPath),
                "B 持有期间 entry 应保留");
            Assert.AreEqual(1, GetEntryRefCount(locks[fullPath]!),
                "A 释放后、B 持有时 RefCount 应为 1");

            await bTask;
        }
        finally
        {
            // 若上面失败，确保 leaseA 释放
            try { await leaseA.DisposeAsync(); } catch { /* already disposed */ }
        }

        // 全部释放后 entry 回收
        Assert.IsFalse(locks.Contains(fullPath),
            "全部释放后 entry 应被回收");
    }

    // ── 高并发 retire+recreate 压力（竞态高发场景）──────────────────────

    /// <summary>
    /// 高并发快速 acquire/release 循环——这是 retired-entry 竞态的高发场景
    /// （每次 release 后 RefCount 归零 → entry 退休 → 下次 acquire 创建新 entry）。
    /// 验证：(1) 无异常；(2) 最终 LocalLocks 为空（无 entry 泄漏）；(3) RefCount 会计正确。
    /// 若竞态存在，可能出现 entry 泄漏（已退休 entry 的 RefCount 被复活后无法回收）或互斥失效。
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task Stress_RapidAcquireRelease_NoLeakNoCorruption()
    {
        var provider = new FileLockProvider();
        var filePath = Path.Combine(_rootPath!, "stress.txt");

        const int threadCount = 16;
        const int iterationsPerThread = 100;

        var violations = 0;
        var completed = 0;

        var tasks = Enumerable.Range(0, threadCount).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < iterationsPerThread; i++)
            {
                await using var lease = await provider.AcquireWriteLockAsync(filePath);
                var current = Interlocked.Increment(ref violations);
                if (current != 1)
                {
                    Assert.Fail($"压力测试互斥失效：并发持有者 {current} > 1");
                }
                Interlocked.Decrement(ref violations);
                Interlocked.Increment(ref completed);
            }
        }));

        await Task.WhenAll(tasks);

        Assert.AreEqual(threadCount * iterationsPerThread, completed);
        Assert.AreEqual(0, GetLocalLocks().Count,
            "压力测试后 LocalLocks 应为空——无 entry 泄漏（retired-entry 竞态会导致已退休 entry RefCount 复活且无法回收）");
    }

    /// <summary>
    /// 多路径并发无干扰——不同路径的 acquire 互不阻塞。
    /// 验证全局锁 lock(LocalLocks) 不过度串行化不同路径（临界区仅 O(1) 查找+自增）。
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ConcurrentAcquire_DifferentPaths_NoInterference()
    {
        var provider = new FileLockProvider();
        const int pathCount = 10;
        const int iterationsPerPath = 20;

        var completed = 0;
        var tasks = Enumerable.Range(0, pathCount).Select(idx => Task.Run(async () =>
        {
            var filePath = Path.Combine(_rootPath!, $"diff-{idx}.txt");
            for (var i = 0; i < iterationsPerPath; i++)
            {
                await using var lease = await provider.AcquireWriteLockAsync(filePath);
                Interlocked.Increment(ref completed);
            }
        }));

        await Task.WhenAll(tasks);

        Assert.AreEqual(pathCount * iterationsPerPath, completed);
        Assert.AreEqual(0, GetLocalLocks().Count,
            "全部释放后不同路径的 entry 均应回收");
    }

    /// <summary>
    /// 取消等待中的 acquire 不破坏 RefCount 会计。
    /// WaitAsync 被取消时递减 RefCount 并回收（若归零）。
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task CancelledAcquireWhileWaiting_ReleasesRefCount()
    {
        var provider = new FileLockProvider();
        var filePath = Path.Combine(_rootPath!, "cancel.txt");
        var fullPath = Path.GetFullPath(filePath);
        var locks = GetLocalLocks();

        // A 持有 lease（阻塞后续 acquire）
        var leaseA = await provider.AcquireWriteLockAsync(filePath);
        try
        {
            // B 尝试 acquire，会被 Gate 阻塞
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
            {
                await provider.AcquireWriteLockAsync(filePath, cts.Token);
            });

            // B 取消后 RefCount 应回到 1（仅 A 持有），entry 仍在
            Assert.IsTrue(locks.Contains(fullPath));
            Assert.AreEqual(1, GetEntryRefCount(locks[fullPath]!),
                "B 取消后 RefCount 应为 1（仅 A 持有，B 的 RefCount 已递减回收）");
        }
        finally
        {
            await leaseA.DisposeAsync();
        }

        Assert.IsFalse(locks.Contains(fullPath),
            "A 释放后 entry 应回收");
    }
}
