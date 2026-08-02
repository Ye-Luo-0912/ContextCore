using System.Diagnostics;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// #5：Package 冷路径 p95 与 allocation gate——
/// 建立 InMemory 与 FileSystem 冷构建路径的回归闸门，避免 R13.2 之后的变更意外恶化性能。
///
/// 设计原则：
/// - allocation gate 严格（基线 × 2，allocation 是确定性的，机器无关）。
/// - latency gate 宽松（基线 × 20，仅检测灾难性回归，避免 CI 抖动 flake）。
/// - 所有测试使用 [DoNotParallelize] 避免并行执行污染 GC 测量。
/// - 测试体 warmup 3 次后再测量，消除 JIT 编译开销。
///
/// 基线（dbf963f, 2026-07-17）：
///   BuildDetailed_Cold / InMemory / 50 items  : 2846.6 μs mean, 924.54 KB allocated
///   BuildDetailed_Cold / InMemory / 200 items : 5823.6 μs mean, 1605.76 KB allocated
///   FileSystem_AppCacheMiss_OsFileCacheWarm / 50 items  : 19015.1 μs mean, 1538.63 KB allocated
///   FileSystem_AppCacheMiss_OsFileCacheWarm / 200 items : 72199.4 μs mean, 4272.09 KB allocated
///
/// 后预期：
/// - InMemory 分配应保持或略降（dedup 减少中间 list，但 PackageReadPlan 新增少量字段，互相抵消）
/// - FileSystem 分配应下降（R13.2 #2 snapshot cache 减少 4 次重复文件读取与反序列化）
/// </summary>
[TestClass]
[TestCategory("Performance")]
[TestCategory("Package")]
public sealed class PackageColdPathPerformanceGateTests
{
    private const string WorkspaceId = "perf-gate-ws";
    private const string CollectionId = "perf-gate-col";

    private const int WarmupIterations = 3;
    private const int LatencySampleCount = 20;

    /// <summary>
    /// 基线 InMemory 50 items 924 KB → gate 2000 KB（×2.2 头部空间）。
    /// 应保持在此线下，验证 #1+#3+#4 的 dedup 与 PackageReadPlan 新增字段未引入分配恶化。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task ColdPath_InMemory_50Items_AllocationUnderGate()
    {
        var (builder, request) = SetupInMemoryBuilder(itemCount: 50);

        // Warmup：触发 JIT 编译、内部 delegate cache 初始化
        for (var i = 0; i < WarmupIterations; i++)
        {
            _ = await builder.BuildDetailedAsync(request, CancellationToken.None);
        }

        // Measure：强制 GC 后采样 process-wide allocation
        ForceFullGc();
        var before = GC.GetTotalAllocatedBytes(precise: true);

        _ = await builder.BuildDetailedAsync(request, CancellationToken.None);

        var after = GC.GetTotalAllocatedBytes(precise: true);
        var allocatedBytes = after - before;
        var allocatedKb = allocatedBytes / 1024.0;

        Console.WriteLine($"[Perf Gate] InMemory 50-item cold path allocation: {allocatedKb:F1} KB");

        const long GateBytes = 2_000_000; // 2 MB
        Assert.IsTrue(allocatedBytes < GateBytes,
            $"InMemory 50-item cold path allocated {allocatedKb:F1} KB, exceeds gate {GateBytes / 1024.0:F1} KB " +
            $"(R12-F.1 baseline 924.54 KB)");
    }

    /// <summary>
    /// 基线 InMemory 200 items 1605 KB → gate 3200 KB（×2 头部空间）。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task ColdPath_InMemory_200Items_AllocationUnderGate()
    {
        var (builder, request) = SetupInMemoryBuilder(itemCount: 200);

        for (var i = 0; i < WarmupIterations; i++)
        {
            _ = await builder.BuildDetailedAsync(request, CancellationToken.None);
        }

        ForceFullGc();
        var before = GC.GetTotalAllocatedBytes(precise: true);

        _ = await builder.BuildDetailedAsync(request, CancellationToken.None);

        var after = GC.GetTotalAllocatedBytes(precise: true);
        var allocatedBytes = after - before;
        var allocatedKb = allocatedBytes / 1024.0;

        Console.WriteLine($"[Perf Gate] InMemory 200-item cold path allocation: {allocatedKb:F1} KB");

        const long GateBytes = 3_200_000; // 3.2 MB
        Assert.IsTrue(allocatedBytes < GateBytes,
            $"InMemory 200-item cold path allocated {allocatedKb:F1} KB, exceeds gate {GateBytes / 1024.0:F1} KB " +
            $"(R12-F.1 baseline 1605.76 KB)");
    }

    /// <summary>
    /// 基线 FileSystem 50 items 1538 KB → gate 3000 KB（×2 头部空间）。
    /// 后应明显下降（snapshot cache 减少 4 次重复反序列化），gate 仍维持 ×2 防止回归。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task ColdPath_FileSystem_50Items_AllocationUnderGate()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "cc-perf-gate-fs-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(rootPath);
            var (builder, request) = SetupFileSystemBuilder(rootPath, itemCount: 50);

            for (var i = 0; i < WarmupIterations; i++)
            {
                _ = await builder.BuildDetailedAsync(request, CancellationToken.None);
            }

            ForceFullGc();
            var before = GC.GetTotalAllocatedBytes(precise: true);

            _ = await builder.BuildDetailedAsync(request, CancellationToken.None);

            var after = GC.GetTotalAllocatedBytes(precise: true);
            var allocatedBytes = after - before;
            var allocatedKb = allocatedBytes / 1024.0;

            Console.WriteLine($"[Perf Gate] FileSystem 50-item cold path allocation: {allocatedKb:F1} KB");

            const long GateBytes = 3_000_000; // 3 MB
            Assert.IsTrue(allocatedBytes < GateBytes,
                $"FileSystem 50-item cold path allocated {allocatedKb:F1} KB, exceeds gate {GateBytes / 1024.0:F1} KB " +
                $"(R12-F.1 baseline 1538.63 KB, R13.2 #2 snapshot cache 后应进一步下降)");
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                try { Directory.Delete(rootPath, recursive: true); }
                catch { /* 句柄延迟回收，忽略 */ }
            }
        }
    }

    /// <summary>
    /// InMemory 50 items p95 延迟应低于 50ms（基线 mean 2.8ms × ~18 头部空间）。
    /// 仅检测灾难性回归；非严格的 perf 回归——那由 BenchmarkDotNet 在 release build 中检测。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task ColdPath_InMemory_50Items_P95LatencyUnderGate()
    {
        var (builder, request) = SetupInMemoryBuilder(itemCount: 50);

        // 额外 warmup：3 次
        for (var i = 0; i < WarmupIterations; i++)
        {
            _ = await builder.BuildDetailedAsync(request, CancellationToken.None);
        }

        var samples = new double[LatencySampleCount];
        for (var i = 0; i < LatencySampleCount; i++)
        {
            var sw = Stopwatch.StartNew();
            _ = await builder.BuildDetailedAsync(request, CancellationToken.None);
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        // p95：取第 95 百分位（20 样本时即第 19 个，索引 19 = 95th percentile rounded up）
        var p95Index = (int)Math.Ceiling(LatencySampleCount * 0.95) - 1;
        var p95 = samples[p95Index];
        var min = samples[0];
        var median = samples[LatencySampleCount / 2];

        Console.WriteLine($"[Perf Gate] InMemory 50-item cold path: min={min:F2}ms, median={median:F2}ms, p95={p95:F2}ms");

        const double GateMs = 50.0;
        Assert.IsTrue(p95 < GateMs,
            $"InMemory 50-item cold path p95={p95:F2}ms exceeds gate {GateMs}ms " +
            $"(min={min:F2}ms, median={median:F2}ms; baseline mean 2.8ms, p95 ~5ms)");
    }

    private static (BasicContextPackageBuilder builder, ContextPackageRequest request) SetupInMemoryBuilder(int itemCount)
    {
        var contextStore = new InMemoryContextStore();
        var memoryStore = new InMemoryMemoryStore();
        var constraintStore = new InMemoryConstraintStore();
        var globalStore = new InMemoryGlobalContextStore();
        var relationStore = new InMemoryRelationStore();

        PopulateStores(contextStore, memoryStore, constraintStore, globalStore, itemCount);

        var builder = new BasicContextPackageBuilder(
            contextStore,
            constraintStore,
            globalStore,
            memoryStore,
            relationStore,
            workingMemoryService: memoryStore);

        return (builder, CreateRequest());
    }

    private static (BasicContextPackageBuilder builder, ContextPackageRequest request) SetupFileSystemBuilder(string rootPath, int itemCount)
    {
        var options = new FileStorageOptions { RootPath = rootPath };
        var contextStore = new FileContextStore(options);
        var memoryStore = new FileMemoryStore(options);
        var constraintStore = new FileConstraintStore(options);
        var globalStore = new FileGlobalContextStore(options);
        var relationStore = new FileRelationStore(options);

        PopulateStores(contextStore, memoryStore, constraintStore, globalStore, itemCount);

        var builder = new BasicContextPackageBuilder(
            contextStore,
            constraintStore,
            globalStore,
            memoryStore,
            relationStore,
            workingMemoryService: memoryStore);

        return (builder, CreateRequest());
    }

    private static ContextPackageRequest CreateRequest()
    {
        return new ContextPackageRequest
        {
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            QueryText = "Package cold path perf gate benchmark 上下文包冷路径性能闸门",
            RequiredTags = ["task"],
            TokenBudget = 4000,
            Mode = ContextPackageMode.None,
            Policy = new ContextPackagePolicy
            {
                Id = "perf-gate-policy-all",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Name = "PerfGateAllSections",
                Mode = ContextPackageMode.None,
                TokenBudget = 4000,
                IncludeRecentRawContext = true,
                IncludeHardConstraints = true,
                IncludeSoftConstraints = true,
                IncludeWorkingMemory = true,
                IncludeStableMemory = true,
                IncludeGlobalContext = true,
                MaxRecentItems = 20
            }
        };
    }

    private static void PopulateStores(
        InMemoryContextStore contextStore,
        InMemoryMemoryStore memoryStore,
        InMemoryConstraintStore constraintStore,
        InMemoryGlobalContextStore globalStore,
        int itemCount)
    {
        var now = DateTimeOffset.UtcNow;
        var rand = new Random(20260715); // 固定种子保证可复现

        for (var i = 0; i < itemCount; i++)
        {
            var createdAt = now.AddDays(-rand.Next(0, 90)).AddMinutes(-rand.Next(0, 1440));
            contextStore.SaveAsync(new ContextItem
            {
                Id = $"ctx-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Type = "note",
                Title = $"条目 {i}",
                Content = "内容 " + new string('x', 200 + rand.Next(0, 400)),
                ContentFormat = ContextContentFormat.Markdown,
                Tags = ["task", "package"],
                Importance = 0.3f + (float)(rand.NextDouble() * 0.7),
                CreatedAt = createdAt,
                UpdatedAt = createdAt
            }).GetAwaiter().GetResult();
        }

        var stableCount = Math.Max(1, itemCount / 5);
        for (var i = 0; i < stableCount; i++)
        {
            memoryStore.SaveAsync(new ContextMemoryItem
            {
                Id = $"mem-stable-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Layer = ContextMemoryLayer.Stable,
                Status = ContextMemoryStatus.Stable,
                Type = "fact",
                Content = $"稳定记忆 #{i}",
                ContentFormat = ContextContentFormat.Markdown,
                Tags = ["task"],
                Importance = 0.7,
                CreatedAt = now,
                UpdatedAt = now
            }).GetAwaiter().GetResult();
        }

        var workingCount = Math.Max(1, itemCount / 10);
        for (var i = 0; i < workingCount; i++)
        {
            memoryStore.SaveAsync(new ContextMemoryItem
            {
                Id = $"mem-working-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Layer = ContextMemoryLayer.Working,
                Status = ContextMemoryStatus.Active,
                Type = "note",
                Content = $"工作记忆 #{i}",
                ContentFormat = ContextContentFormat.Markdown,
                Tags = ["task"],
                Importance = 0.7,
                CreatedAt = now,
                UpdatedAt = now
            }).GetAwaiter().GetResult();
        }

        for (var i = 0; i < 10; i++)
        {
            constraintStore.SaveAsync(new ContextConstraint
            {
                Id = $"con-hard-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Scope = ContextScope.Collection,
                Level = ConstraintLevel.Hard,
                Content = $"硬约束 #{i}",
                Status = ContextMemoryStatus.Active,
                Confidence = 0.9,
                CreatedAt = now,
                UpdatedAt = now
            }).GetAwaiter().GetResult();

            constraintStore.SaveAsync(new ContextConstraint
            {
                Id = $"con-soft-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Scope = ContextScope.Collection,
                Level = ConstraintLevel.Soft,
                Content = $"软约束 #{i}",
                Status = ContextMemoryStatus.Active,
                Confidence = 0.7,
                CreatedAt = now,
                UpdatedAt = now
            }).GetAwaiter().GetResult();
        }

        for (var i = 0; i < 5; i++)
        {
            globalStore.SaveAsync(new ContextGlobalItem
            {
                Id = $"global-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = null,
                Scope = ContextScope.Workspace,
                Type = "preference",
                Content = $"全局上下文 #{i}",
                ContentFormat = ContextContentFormat.Markdown,
                Tags = ["task"],
                Importance = 0.7,
                CreatedAt = now,
                UpdatedAt = now
            }).GetAwaiter().GetResult();
        }
    }

    private static void PopulateStores(
        FileContextStore contextStore,
        FileMemoryStore memoryStore,
        FileConstraintStore constraintStore,
        FileGlobalContextStore globalStore,
        int itemCount)
    {
        var now = DateTimeOffset.UtcNow;
        var rand = new Random(20260715);

        for (var i = 0; i < itemCount; i++)
        {
            var createdAt = now.AddDays(-rand.Next(0, 90)).AddMinutes(-rand.Next(0, 1440));
            contextStore.SaveAsync(new ContextItem
            {
                Id = $"ctx-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Type = "note",
                Title = $"条目 {i}",
                Content = "内容 " + new string('x', 200 + rand.Next(0, 400)),
                ContentFormat = ContextContentFormat.Markdown,
                Tags = ["task", "package"],
                Importance = 0.3f + (float)(rand.NextDouble() * 0.7),
                CreatedAt = createdAt,
                UpdatedAt = createdAt
            }).GetAwaiter().GetResult();
        }

        var stableCount = Math.Max(1, itemCount / 5);
        for (var i = 0; i < stableCount; i++)
        {
            memoryStore.SaveAsync(new ContextMemoryItem
            {
                Id = $"mem-stable-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Layer = ContextMemoryLayer.Stable,
                Status = ContextMemoryStatus.Stable,
                Type = "fact",
                Content = $"稳定记忆 #{i}",
                ContentFormat = ContextContentFormat.Markdown,
                Tags = ["task"],
                Importance = 0.7,
                CreatedAt = now,
                UpdatedAt = now
            }).GetAwaiter().GetResult();
        }

        var workingCount = Math.Max(1, itemCount / 10);
        for (var i = 0; i < workingCount; i++)
        {
            memoryStore.SaveAsync(new ContextMemoryItem
            {
                Id = $"mem-working-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Layer = ContextMemoryLayer.Working,
                Status = ContextMemoryStatus.Active,
                Type = "note",
                Content = $"工作记忆 #{i}",
                ContentFormat = ContextContentFormat.Markdown,
                Tags = ["task"],
                Importance = 0.7,
                CreatedAt = now,
                UpdatedAt = now
            }).GetAwaiter().GetResult();
        }

        for (var i = 0; i < 10; i++)
        {
            constraintStore.SaveAsync(new ContextConstraint
            {
                Id = $"con-hard-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Scope = ContextScope.Collection,
                Level = ConstraintLevel.Hard,
                Content = $"硬约束 #{i}",
                Status = ContextMemoryStatus.Active,
                Confidence = 0.9,
                CreatedAt = now,
                UpdatedAt = now
            }).GetAwaiter().GetResult();

            constraintStore.SaveAsync(new ContextConstraint
            {
                Id = $"con-soft-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Scope = ContextScope.Collection,
                Level = ConstraintLevel.Soft,
                Content = $"软约束 #{i}",
                Status = ContextMemoryStatus.Active,
                Confidence = 0.7,
                CreatedAt = now,
                UpdatedAt = now
            }).GetAwaiter().GetResult();
        }

        for (var i = 0; i < 5; i++)
        {
            globalStore.SaveAsync(new ContextGlobalItem
            {
                Id = $"global-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = null,
                Scope = ContextScope.Workspace,
                Type = "preference",
                Content = $"全局上下文 #{i}",
                ContentFormat = ContextContentFormat.Markdown,
                Tags = ["task"],
                Importance = 0.7,
                CreatedAt = now,
                UpdatedAt = now
            }).GetAwaiter().GetResult();
        }
    }

    private static void ForceFullGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
