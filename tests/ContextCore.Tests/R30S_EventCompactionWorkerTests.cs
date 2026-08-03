using ContextCore.Abstractions;
using ContextCore.Service.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ContextCore.Tests;

// ===========================================================================
// Event 快照自动压缩后台 worker 单元测试（性能优化 WP-P4）
//
// 覆盖范围：
// - WorkerBackoff 共享指数退避（成功间隔复位 / 翻倍 / 封顶 / 指数封顶）
// - ModelStateReconcilerWorker.ComputeBackoffDelay 委托共享实现（回归保护）
// - AgentRunEventCompactionOptions 默认值
// - 压缩 worker 行为：无候选不动作 / 按 LastSequence 全量折叠 / 单 Run 失败
//   不影响其他 Run / compactor 缺失或禁用时自退出
//
// 设计：
// RecordingCompactor 模拟 IAgentRunEventCompactor：压缩成功后将该 Run 移出
// 后续候选（模拟热表折叠后不再超阈值）；失败 Run 保留以便观察重试。
// 不连接真实 PostgreSQL；FindCandidatesAsync 的 SQL 语义由
// ContextCore.IntegrationTests 覆盖（需 Testcontainers）。
// ===========================================================================

[TestClass]
[TestCategory("R29")]
public sealed class R30S_EventCompactionWorkerTests
{
    // =========================================================================
    // WorkerBackoff 共享退避
    // =========================================================================

    [TestMethod]
    public void WorkerBackoff_ReturnsSuccessInterval_WhenNoFailures()
    {
        var options = new AgentRunEventCompactionOptions
        {
            PollInterval = TimeSpan.FromSeconds(30),
            BackoffBaseDelay = TimeSpan.FromSeconds(1),
            BackoffMaxDelay = TimeSpan.FromSeconds(8)
        };

        Assert.AreEqual(
            TimeSpan.FromSeconds(30),
            WorkerBackoff.Compute(
                options.PollInterval, options.BackoffBaseDelay, options.BackoffMaxDelay,
                options.MaxRetryCount, consecutiveFailures: 0));
        Assert.AreEqual(
            TimeSpan.FromSeconds(30),
            WorkerBackoff.Compute(
                options.PollInterval, options.BackoffBaseDelay, options.BackoffMaxDelay,
                options.MaxRetryCount, consecutiveFailures: -1));
    }

    [TestMethod]
    public void WorkerBackoff_DoublesPerFailure_AndCapsAtMaxDelay()
    {
        var options = new AgentRunEventCompactionOptions
        {
            PollInterval = TimeSpan.FromSeconds(30),
            BackoffBaseDelay = TimeSpan.FromSeconds(1),
            BackoffMaxDelay = TimeSpan.FromSeconds(8)
        };

        // 第 1 次失败：base；第 2 次：2×base；第 3 次：4×base。
        Assert.AreEqual(TimeSpan.FromSeconds(1), Compute(options, 1));
        Assert.AreEqual(TimeSpan.FromSeconds(2), Compute(options, 2));
        Assert.AreEqual(TimeSpan.FromSeconds(4), Compute(options, 3));
        // 第 4 次起达到 8×base，封顶 BackoffMaxDelay。
        Assert.AreEqual(TimeSpan.FromSeconds(8), Compute(options, 4));
        Assert.AreEqual(TimeSpan.FromSeconds(8), Compute(options, 5));

        static TimeSpan Compute(AgentRunEventCompactionOptions o, int failures) =>
            WorkerBackoff.Compute(
                o.PollInterval, o.BackoffBaseDelay, o.BackoffMaxDelay, o.MaxRetryCount, failures);
    }

    [TestMethod]
    public void WorkerBackoff_CapsExponentAfterMaxRetryCount()
    {
        var options = new AgentRunEventCompactionOptions
        {
            PollInterval = TimeSpan.FromSeconds(30),
            BackoffBaseDelay = TimeSpan.FromSeconds(2),
            BackoffMaxDelay = TimeSpan.FromSeconds(64),
            MaxRetryCount = 4
        };

        // MaxRetryCount=4 → 指数封顶：超过 4 次连续失败后保持 8×base（=16s），不继续翻倍。
        Assert.AreEqual(TimeSpan.FromSeconds(16), Compute(options, 4));
        Assert.AreEqual(TimeSpan.FromSeconds(16), Compute(options, 10));

        static TimeSpan Compute(AgentRunEventCompactionOptions o, int failures) =>
            WorkerBackoff.Compute(
                o.PollInterval, o.BackoffBaseDelay, o.BackoffMaxDelay, o.MaxRetryCount, failures);
    }

    [TestMethod]
    public void ModelStateReconcilerWorker_ComputeBackoffDelay_DelegatesToSharedHelper()
    {
        var options = new ModelStateReconcilerOptions
        {
            PollInterval = TimeSpan.FromSeconds(10),
            BackoffBaseDelay = TimeSpan.FromSeconds(2),
            BackoffMaxDelay = TimeSpan.FromSeconds(16),
            MaxRetryCount = 4
        };

        // 委托共享实现后语义不变：成功间隔 / 翻倍 / 指数封顶。
        Assert.AreEqual(TimeSpan.FromSeconds(10), ModelStateReconcilerWorker.ComputeBackoffDelay(options, 0));
        Assert.AreEqual(TimeSpan.FromSeconds(2), ModelStateReconcilerWorker.ComputeBackoffDelay(options, 1));
        Assert.AreEqual(TimeSpan.FromSeconds(4), ModelStateReconcilerWorker.ComputeBackoffDelay(options, 2));
        Assert.AreEqual(TimeSpan.FromSeconds(8), ModelStateReconcilerWorker.ComputeBackoffDelay(options, 3));
        Assert.AreEqual(TimeSpan.FromSeconds(16), ModelStateReconcilerWorker.ComputeBackoffDelay(options, 4));
        Assert.AreEqual(TimeSpan.FromSeconds(16), ModelStateReconcilerWorker.ComputeBackoffDelay(options, 9));
    }

    // =========================================================================
    // Options 默认值
    // =========================================================================

    [TestMethod]
    public void AgentRunEventCompactionOptions_Defaults_AreSane()
    {
        var options = new AgentRunEventCompactionOptions();

        Assert.IsTrue(options.Enabled, "默认应启用自动压缩。");
        Assert.AreEqual(1000, options.MinEventCount, "默认阈值 1000 事件。");
        Assert.AreEqual(20, options.MaxRunsPerPass, "默认每轮最多 20 个 Run。");
        Assert.AreEqual(TimeSpan.FromMinutes(1), options.PollInterval);
        Assert.AreEqual(TimeSpan.FromSeconds(5), options.BackoffBaseDelay);
        Assert.AreEqual(TimeSpan.FromMinutes(5), options.BackoffMaxDelay);
        Assert.AreEqual(8, options.MaxRetryCount);
    }

    // =========================================================================
    // Worker 行为
    // =========================================================================

    [TestMethod]
    public async Task CompactionWorker_NoCandidates_DoesNotCompactAnything()
    {
        var compactor = new RecordingCompactor();
        using var worker = CreateWorker(compactor, pollIntervalMs: 30);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(120);
            Assert.AreEqual(0, compactor.CompactionCalls.Count, "无候选时不应调用压缩。");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task CompactionWorker_CompactsCandidates_UpToLastSequence()
    {
        var compactor = new RecordingCompactor();
        compactor.Candidates.AddRange(
        [
            new AgentRunCompactionCandidate("ws1", "run-a", EventCount: 1200, LastSequence: 99),
            new AgentRunCompactionCandidate("ws2", "run-b", EventCount: 1100, LastSequence: 42)
        ]);
        using var worker = CreateWorker(compactor, pollIntervalMs: 30);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => compactor.CompactionCalls.Count >= 2, "两个候选 Run 应在首轮被压缩。");

            // 每个候选折叠到其 LastSequence（全量折叠），而非依赖 -1 哨兵只锚定首事件。
            Assert.AreEqual(2, compactor.CompactionCalls.Count);
            Assert.AreEqual("run-a", compactor.CompactionCalls[0].RunId);
            Assert.AreEqual(99, compactor.CompactionCalls[0].UpToSequence);
            Assert.AreEqual("run-b", compactor.CompactionCalls[1].RunId);
            Assert.AreEqual(42, compactor.CompactionCalls[1].UpToSequence);

            // 阈值与限量透传。
            Assert.AreEqual(1000, compactor.FindCalls[0].MinEventCount);
            Assert.AreEqual(20, compactor.FindCalls[0].Limit);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task CompactionWorker_SingleRunFailure_DoesNotBlockOtherRuns()
    {
        var compactor = new RecordingCompactor();
        compactor.Candidates.AddRange(
        [
            new AgentRunCompactionCandidate("ws1", "run-fail", EventCount: 1500, LastSequence: 5),
            new AgentRunCompactionCandidate("ws2", "run-ok", EventCount: 1300, LastSequence: 7)
        ]);
        compactor.FailRunIds.Add("run-fail");
        using var worker = CreateWorker(compactor, pollIntervalMs: 30);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            // run-ok 应被压缩（失败 Run 不影响其他 Run）。
            await WaitForAsync(
                () => compactor.CompactionCalls.Any(c => c.RunId == "run-ok"),
                "run-ok 不应受 run-fail 失败影响。");

            // 失败 Run 保留在候选，下一轮仍会被尝试。
            await WaitForAsync(
                () => compactor.CompactionCalls.Count(c => c.RunId == "run-fail") >= 2,
                "失败 Run 应保留候选并在后续轮次重试。");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task CompactionWorker_NullCompactor_ExitsImmediately()
    {
        // 可选注入：compactor 未注册时以默认 null 注入（MS DI 可空注解不构成可选）。
        using var worker = new AgentRunEventCompactionWorker(
            new TestOptionsMonitor<AgentRunEventCompactionOptions>(new AgentRunEventCompactionOptions()),
            NullLogger<AgentRunEventCompactionWorker>.Instance);

        // 非 Postgres provider：compactor 缺失 → 自退出，Start/Stop 均无异常。
        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task CompactionWorker_DisabledOption_ExitsImmediately()
    {
        var compactor = new RecordingCompactor();
        var options = new AgentRunEventCompactionOptions { Enabled = false };
        using var worker = new AgentRunEventCompactionWorker(
            new TestOptionsMonitor<AgentRunEventCompactionOptions>(options),
            NullLogger<AgentRunEventCompactionWorker>.Instance,
            compactor);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(80);
        Assert.AreEqual(0, compactor.FindCalls.Count, "禁用时不应执行任何扫描。");
        await worker.StopAsync(CancellationToken.None);
    }

    // =========================================================================
    // 辅助
    // =========================================================================

    private static AgentRunEventCompactionWorker CreateWorker(RecordingCompactor compactor, int pollIntervalMs)
    {
        var options = new AgentRunEventCompactionOptions
        {
            Enabled = true,
            PollInterval = TimeSpan.FromMilliseconds(pollIntervalMs),
            MinEventCount = 1000,
            MaxRunsPerPass = 20,
            BackoffBaseDelay = TimeSpan.FromMilliseconds(30),
            BackoffMaxDelay = TimeSpan.FromMilliseconds(200),
            MaxRetryCount = 8
        };
        return new AgentRunEventCompactionWorker(
            new TestOptionsMonitor<AgentRunEventCompactionOptions>(options),
            NullLogger<AgentRunEventCompactionWorker>.Instance,
            compactor);
    }

    private static async Task WaitForAsync(Func<bool> condition, string message, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(20);
        }
        Assert.Fail(message);
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private readonly T _value;

        public TestOptionsMonitor(T value)
        {
            _value = value;
        }

        public T CurrentValue => _value;

        public T Get(string? name) => _value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>
    /// 记录型假 compactor：压缩成功后把该 Run 移出后续候选（模拟热表折叠后不再超阈值）；
    /// 失败 Run 保留在候选，供观察重试。
    /// </summary>
    private sealed class RecordingCompactor : IAgentRunEventCompactor
    {
        public List<AgentRunCompactionCandidate> Candidates { get; } = new();

        public HashSet<string> FailRunIds { get; } = new(StringComparer.Ordinal);

        public List<(string RunId, int UpToSequence)> CompactionCalls { get; } = new();

        public List<(int MinEventCount, int Limit)> FindCalls { get; } = new();

        public Task<IReadOnlyList<AgentRunCompactionCandidate>> FindCandidatesAsync(
            int minEventCount,
            int limit,
            CancellationToken cancellationToken = default)
        {
            FindCalls.Add((minEventCount, limit));
            return Task.FromResult<IReadOnlyList<AgentRunCompactionCandidate>>(
                new List<AgentRunCompactionCandidate>(Candidates));
        }

        public Task<AgentRunCompactionResult> CompactAsync(
            string workspaceId,
            string runId,
            int upToSequence,
            CancellationToken cancellationToken = default)
        {
            CompactionCalls.Add((runId, upToSequence));
            if (FailRunIds.Contains(runId))
            {
                throw new InvalidOperationException($"模拟压缩失败：{runId}");
            }

            // 压缩成功：热表折叠后不再超阈值，移出后续候选。
            Candidates.RemoveAll(c => c.RunId == runId);
            return Task.FromResult(new AgentRunCompactionResult(
                workspaceId, runId, upToSequence, FoldedEventCount: 1, ArchivedRowCount: 1,
                ChainHeadHash: "hash", DateTimeOffset.UtcNow));
        }

        public ValueTask<AgentRunEventSnapshot?> GetSnapshotAsync(
            string workspaceId,
            string runId,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<AgentRunEventSnapshot?>(null);
        }

        public ValueTask<IReadOnlyList<AgentRunEvent>> GetArchivedEventsAsync(
            string workspaceId,
            string runId,
            int fromSequence = 0,
            int take = 1000,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<IReadOnlyList<AgentRunEvent>>(Array.Empty<AgentRunEvent>());
        }
    }
}
