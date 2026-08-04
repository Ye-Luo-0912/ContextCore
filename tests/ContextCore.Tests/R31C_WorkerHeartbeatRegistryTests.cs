using ContextCore.Service;
using ContextCore.Service.Extensions;
using ContextCore.Service.Hosting;
using ContextCore.Service.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ContextCore.Tests;

// ===========================================================================
// Worker Heartbeat Registry 验收测试
//
// 目标：ProductionRuntimeWorkerRegistry 记录并暴露 per-worker 运行时状态
// （last successful cycle / last error / current backoff / queue lag / lease status），
// Readiness 的 GetRegisteredWorkers 填充这些维度，供 Readiness / Admission 验证。
// ===========================================================================

[TestClass]
[TestCategory("R29")]
public sealed class R31C_WorkerHeartbeatRegistryTests
{
    private const string ClaimerType = nameof(PostgresPendingRunClaimer);

    [TestMethod]
    public void Registry_MarkCycleSucceeded_RecordsLastCycleAndClearsError()
    {
        var registry = new ProductionRuntimeWorkerRegistry();

        registry.RecordFailure(ClaimerType, "boom", TimeSpan.FromSeconds(30));
        registry.MarkCycleSucceeded(ClaimerType);

        var state = registry.GetWorkerRuntimeState(ClaimerType);
        Assert.IsNotNull(state, "上报后应能查询到状态。");
        Assert.IsNotNull(state!.LastCycleAtUtc, "成功周期应记录时间。");
        Assert.IsNull(state.LastError, "成功周期应清空上次错误。");
        Assert.IsNull(state.CurrentBackoff, "成功周期应清空退避。");
    }

    [TestMethod]
    public void Registry_RecordFailure_SetsErrorAndBackoff()
    {
        var registry = new ProductionRuntimeWorkerRegistry();

        registry.RecordFailure(ClaimerType, "boom", TimeSpan.FromSeconds(30));

        var state = registry.GetWorkerRuntimeState(ClaimerType);
        Assert.IsNotNull(state);
        Assert.AreEqual("boom", state!.LastError);
        Assert.AreEqual(TimeSpan.FromSeconds(30), state.CurrentBackoff);
    }

    [TestMethod]
    public void Registry_SetQueueLagAndLeaseStatus_Reported()
    {
        var registry = new ProductionRuntimeWorkerRegistry();

        registry.SetQueueLag(ClaimerType, 42);
        registry.SetLeaseStatus(ClaimerType, "polling");

        var state = registry.GetWorkerRuntimeState(ClaimerType);
        Assert.AreEqual(42, state!.QueueLag, "应记录队列积压量。");
        Assert.AreEqual("polling", state.LeaseStatus, "应记录租约状态。");
    }

    [TestMethod]
    public void Registry_GetWorkerRuntimeStates_ReturnsAllReported()
    {
        var registry = new ProductionRuntimeWorkerRegistry();
        registry.SetLeaseStatus(ClaimerType, "polling");
        registry.MarkCycleSucceeded("OtherWorker");

        var states = registry.GetWorkerRuntimeStates();
        Assert.AreEqual(2, states.Count, "应返回全部已上报状态的 Worker。");
        CollectionAssert.Contains(states.Select(s => s.WorkerType).ToArray(), ClaimerType);
    }

    [TestMethod]
    public void Readiness_GetRegisteredWorkers_IncludesRuntimeState()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Provider"] = "filesystem",
                ["ContextCoreRuntime:Profile"] = "Development"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddContextCore();
        services.AddContextCoreRuntime(config);
        services.AddSingleton<IHostApplicationLifetime>(new TestHostApplicationLifetime());
        var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<ProductionRuntimeWorkerRegistry>();
        // 模拟 Claimer 上报：失败 + 退避 + 队列积压 + 租约状态；RecoveryWorker 上报成功周期
        registry.RecordFailure(ClaimerType, "领取失败", TimeSpan.FromSeconds(15));
        registry.SetQueueLag(ClaimerType, 7);
        registry.SetLeaseStatus(ClaimerType, "backoff");
        registry.MarkCycleSucceeded(nameof(AgentRunRecoveryWorker));

        var readiness = provider.GetRequiredService<ProductionRuntimeReadinessService>();
        var workers = readiness.GetRegisteredWorkers();

        var claimer = workers.FirstOrDefault(w => w.Type == ClaimerType);
        Assert.IsNotNull(claimer, "应包含 PendingRunClaimer Worker。");
        Assert.AreEqual("领取失败", claimer!.LastError, "Readiness 应暴露 last error。");
        Assert.AreEqual(TimeSpan.FromSeconds(15), claimer.CurrentBackoff, "Readiness 应暴露 current backoff。");
        Assert.AreEqual(7, claimer.QueueLag, "Readiness 应暴露 queue lag。");
        Assert.AreEqual("backoff", claimer.LeaseStatus, "Readiness 应暴露 lease status。");

        var recovery = workers.FirstOrDefault(w => w.Type == nameof(AgentRunRecoveryWorker));
        Assert.IsNotNull(recovery);
        Assert.IsNotNull(recovery!.LastCycleAtUtc, "Readiness 应暴露 last successful cycle。");
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void TriggerApplicationStarted() => _started.Cancel();

        public void StopApplication()
        {
        }
    }
}
