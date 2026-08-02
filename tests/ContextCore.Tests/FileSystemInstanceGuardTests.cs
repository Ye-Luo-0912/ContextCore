using ContextCore.Storage.FileSystem;

namespace ContextCore.Tests;

/// <summary>
/// #6：验证 FileSystemInstanceGuard 的单实例检测与多进程 advisory 边界。
/// </summary>
[TestClass]
[TestCategory("FileSystem")]
public sealed class FileSystemInstanceGuardTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        // 静态缓存跨测试持久，每用例清空以隔离，并释放持有的句柄让临时目录可被删除。
        FileSystemInstanceGuard.ResetCacheForTests();
        _root = Path.Combine(Path.GetTempPath(), "contextcore-guard-tests", Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        FileSystemInstanceGuard.ResetCacheForTests();
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { /* 句柄延迟回收，忽略 */ }
        }
    }

    [TestMethod]
    public void GetOrCreate_OnFreshRoot_AcquiresAndReportsSingleInstance()
    {
        var guard = FileSystemInstanceGuard.GetOrCreate(_root);

        Assert.IsFalse(guard.IsMultiProcessDetected, "无人占用时本进程应成功获取 sentinel，报告单实例");
        Assert.IsTrue(File.Exists(Path.Combine(_root, ".context-core-instance.lock")),
            "sentinel 锁文件应在首次获取时创建");
    }

    [TestMethod]
    public void GetOrCreate_WhenSentinelHeldExclusively_ReportsMultiProcess()
    {
        // 模拟他进程已独占 sentinel：以 FileShare.None 持有，模拟跨进程占用。
        Directory.CreateDirectory(_root);
        var sentinel = Path.Combine(_root, ".context-core-instance.lock");
        using var holder = new FileStream(
            sentinel, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, bufferSize: 1, FileOptions.None);

        var guard = FileSystemInstanceGuard.GetOrCreate(_root);

        Assert.IsTrue(guard.IsMultiProcessDetected, "sentinel 被他进程独占时应检测到多进程");
    }

    [TestMethod]
    public void GetOrCreate_SameProcessSameRoot_ReturnsCachedInstance()
    {
        var first = FileSystemInstanceGuard.GetOrCreate(_root);
        var second = FileSystemInstanceGuard.GetOrCreate(_root);

        Assert.AreSame(first, second, "同进程同 root 应返回缓存的同一实例，避免本进程内自锁误报");
        Assert.IsFalse(second.IsMultiProcessDetected, "缓存结果应保持单实例判定");
    }

    [TestMethod]
    public void ResetCacheForTests_ReleasesSentinelHandle_AllowsRootDeletion()
    {
        // #6：sentinel 以 FileShare.Read 共享（他进程不可写/独占），
        // 持有期间 Directory.Delete(recursive) 会被锁文件阻塞。
        // ResetCacheForTests 释放句柄后目录才可递归删除——验证清理契约。
        var guard = FileSystemInstanceGuard.GetOrCreate(_root);
        Assert.IsFalse(guard.IsMultiProcessDetected);

        // 释放前：持有句柄时递归删除会抛 IOException（锁文件被占用）。
        Assert.ThrowsException<IOException>(() => Directory.Delete(_root, recursive: true),
            "持有 sentinel 句柄期间递归删除 root 应被锁文件阻塞");

        // 释放句柄：ResetCacheForTests 关闭并清空进程内单例缓存。
        FileSystemInstanceGuard.ResetCacheForTests();

        Directory.Delete(_root, recursive: true);
        Assert.IsFalse(Directory.Exists(_root), "释放句柄后 root 目录应可递归删除");
    }

    [TestMethod]
    public void GetOrCreate_DistinctRoots_AreIndependent()
    {
        var otherRoot = Path.Combine(Path.GetTempPath(), "contextcore-guard-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var a = FileSystemInstanceGuard.GetOrCreate(_root);
            var b = FileSystemInstanceGuard.GetOrCreate(otherRoot);

            Assert.AreNotSame(a, b, "不同 root 应是独立的守护实例");
            Assert.IsFalse(a.IsMultiProcessDetected);
            Assert.IsFalse(b.IsMultiProcessDetected);
        }
        finally
        {
            if (Directory.Exists(otherRoot))
            {
                try { Directory.Delete(otherRoot, recursive: true); }
                catch { }
            }
        }
    }
}
