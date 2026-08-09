using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Service.Hosting;
using ContextCore.Storage.InMemory.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContextCore.Tests;

/// <summary>
/// Decision Commit 可靠链端到端测试（WP-F 接线）：
/// 决策产生点 → IDecisionCommitOutbox（durable）→ DecisionCommitWorker 消费
/// → IDecisionTraceStore 落库（Decision Evidence Plane durable 归档）→ Ack。
/// 崩溃重放语义由 outbox 契约测试覆盖（未 Ack 可重新领取）。
/// </summary>
[TestClass]
[TestCategory("Learning-Event")]
public sealed class DecisionCommitWorkerTests
{
    [TestMethod]
    public async Task Worker_ConsumesCommit_PersistsDecisionRecord()
    {
        var outbox = new InMemoryDecisionCommitOutbox();
        var decisionTrace = new InMemoryDecisionTraceStore();

        var services = new ServiceCollection();
        services.AddSingleton<IDecisionCommitOutbox>(outbox);
        services.AddSingleton<IDecisionTraceStore>(decisionTrace);
        services.AddSingleton(new ContextCoreRuntimeOptions { RunRecoveryInterval = TimeSpan.FromMilliseconds(50) });
        services.AddSingleton(sp => new DecisionCommitWorker(
            sp, sp.GetRequiredService<ContextCoreRuntimeOptions>(), NullLogger<DecisionCommitWorker>.Instance));
        await using var provider = services.BuildServiceProvider();

        var worker = provider.GetRequiredService<DecisionCommitWorker>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);

        // 决策产生点：入队决策提交（record + 物化意图）。
        await outbox.EnqueueAsync(new DecisionCommitOutboxRecord
        {
            DecisionId = "decision-e2e-1",
            WorkspaceId = "ws-e2e",
            CollectionId = "col-e2e",
            CommitType = DecisionCommitType.RecordAndMaterialize,
            Record = new ContextDecisionRecord
            {
                DecisionId = "decision-e2e-1",
                Source = ContextDecisionSource.Retrieval,
                WorkspaceId = "ws-e2e",
                CollectionId = "col-e2e",
                QueryText = "e2e query",
                Candidates = new[]
                {
                    new ContextDecisionCandidate { ItemId = "cand-1", Outcome = ContextDecisionCandidateOutcome.Selected, Reason = "selected" }
                },
                PolicyVersion = "decision-schema/2.0",
                CreatedAt = DateTimeOffset.UtcNow
            },
            CreatedAt = DateTimeOffset.UtcNow
        });

        // 等待 worker 消费并落库（轮询间隔 50ms + 消费处理）。
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        ContextDecisionRecord? persisted = null;
        while (DateTimeOffset.UtcNow < deadline && persisted is null)
        {
            persisted = await decisionTrace.GetAsync("ws-e2e", "col-e2e", "decision-e2e-1");
            if (persisted is null)
            {
                await Task.Delay(50);
            }
        }

        await worker.StopAsync(cts.Token);

        Assert.IsNotNull(persisted, "worker 应消费决策提交并把记录落库（Decision Evidence Plane durable）。");
        Assert.AreEqual("decision-e2e-1", persisted!.DecisionId);
        Assert.AreEqual("e2e query", persisted.QueryText);
        Assert.AreEqual(1, persisted.Candidates.Count, "候选决策列表应保留。");
        Assert.AreEqual("cand-1", persisted.Candidates[0].ItemId);
        Assert.AreEqual(ContextDecisionCandidateOutcome.Selected, persisted.Candidates[0].Outcome);
        Assert.AreEqual("decision-schema/2.0", persisted.PolicyVersion);

        // 已 Ack：outbox 中无待处理条目。
        var pending = await outbox.AcquirePendingAsync(10, "probe", TimeSpan.FromMinutes(1));
        Assert.AreEqual(0, pending.Count, "落库成功后条目应已 Ack（不再被领取）。");
    }

    [TestMethod]
    public async Task Worker_NoOutboxRegistered_ExitsNoop()
    {
        // 非 Postgres provider（无 outbox）：worker 自退出 no-op。
        var services = new ServiceCollection();
        services.AddSingleton(new ContextCoreRuntimeOptions { RunRecoveryInterval = TimeSpan.FromMilliseconds(50) });
        services.AddSingleton(sp => new DecisionCommitWorker(
            sp, sp.GetRequiredService<ContextCoreRuntimeOptions>(), NullLogger<DecisionCommitWorker>.Instance));
        await using var provider = services.BuildServiceProvider();

        var worker = provider.GetRequiredService<DecisionCommitWorker>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StartAsync(cts.Token);
        await Task.Delay(100);
        await worker.StopAsync(cts.Token);
        // 未抛异常即通过（探测到 null outbox 后自退出）。
    }
}
