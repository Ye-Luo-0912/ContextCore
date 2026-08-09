using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// Decision Commit Outbox 测试（WP-E：Decision Record + Evidence 引用 +
/// Learning Materialization Intent 经 Durable Outbox 连成可靠链）：
/// 1. 入队幂等（同 (workspace_id, decision_id) 只保留一条，重放覆盖为待处理）；
/// 2. 领取 + Ack（CAS 租约；错误 token 拒绝）；
/// 3. 失败重试 → 达上限转死信（不丢决策，供运维排查）；
/// 4. 未 Ack 崩溃语义：领取后不 Ack，条目可被重新领取（重放）。
/// </summary>
[TestClass]
[TestCategory("Learning-Event")]
public sealed class DecisionCommitOutboxTests
{
    private static DecisionCommitOutboxRecord BuildCommit(string decisionId, string workspaceId = "ws-dc")
        => new()
        {
            DecisionId = decisionId,
            WorkspaceId = workspaceId,
            CollectionId = "col-dc",
            CommitType = DecisionCommitType.RecordAndMaterialize,
            Record = new ContextDecisionRecord
            {
                DecisionId = decisionId,
                Source = ContextDecisionSource.Retrieval,
                WorkspaceId = workspaceId,
                CollectionId = "col-dc",
                QueryText = "test",
                Candidates = Array.Empty<ContextDecisionCandidate>(),
                CreatedAt = DateTimeOffset.UtcNow,
                PolicyVersion = "policy/v1"
            },
            EvidenceRef = "sig:abc",
            CreatedAt = DateTimeOffset.UtcNow
        };

    [TestMethod]
    public async Task Enqueue_IsIdempotentByWorkspaceAndDecisionId()
    {
        var outbox = new InMemoryDecisionCommitOutbox();

        await outbox.EnqueueAsync(BuildCommit("decision-1"));
        await outbox.EnqueueAsync(BuildCommit("decision-1") with
        {
            EvidenceRef = "sig:updated",
            CommitType = DecisionCommitType.RecordOnly
        });

        var pending = await outbox.AcquirePendingAsync(10, "worker-1", TimeSpan.FromMinutes(1));
        Assert.AreEqual(1, pending.Count, "同 (workspace, decision_id) 幂等——只保留一条。");
        Assert.AreEqual("sig:updated", pending[0].EvidenceRef, "重放覆盖为最新提交内容。");
        Assert.AreEqual(DecisionCommitType.RecordOnly, pending[0].CommitType);
    }

    [TestMethod]
    public async Task AcquireAndAck_ValidatesLeaseToken()
    {
        var outbox = new InMemoryDecisionCommitOutbox();
        await outbox.EnqueueAsync(BuildCommit("decision-2"));

        var pending = await outbox.AcquirePendingAsync(10, "worker-1", TimeSpan.FromMinutes(1));
        Assert.AreEqual(1, pending.Count);
        Assert.AreEqual(2, pending[0].State, "领取后进入处理中（租约）。");

        // 错误 token → Ack 拒绝。
        Assert.IsFalse(await outbox.AckAsync(pending[0].OutboxId, "wrong-token"), "错误租约 token 拒绝 Ack。");

        // 正确 token → Ack 成功；再次领取无条目。
        Assert.IsTrue(await outbox.AckAsync(pending[0].OutboxId, pending[0].LeaseToken!), "正确租约 token Ack 成功。");
        var again = await outbox.AcquirePendingAsync(10, "worker-1", TimeSpan.FromMinutes(1));
        Assert.AreEqual(0, again.Count, "已 Ack 条目不再被领取。");
    }

    [TestMethod]
    public async Task UnackedCommit_AfterLeaseExpiry_IsReclaimable()
    {
        // 崩溃语义：领取后未 Ack（worker 崩溃）→ 租约过期后可被重新领取（重放，不丢决策）。
        var outbox = new InMemoryDecisionCommitOutbox();
        await outbox.EnqueueAsync(BuildCommit("decision-3"));

        var first = await outbox.AcquirePendingAsync(10, "worker-crashed", TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(1, first.Count);

        await Task.Delay(50);

        var reclaim = await outbox.AcquirePendingAsync(10, "worker-2", TimeSpan.FromMinutes(1));
        Assert.AreEqual(1, reclaim.Count, "未 Ack 且租约过期 → 可重放领取。");
        Assert.AreEqual("decision-3", reclaim[0].DecisionId);
    }

    [TestMethod]
    public async Task MarkFailed_RetriesThenDeadLetters()
    {
        // 失败重试：未达上限保持可重试；达上限转死信（记录保留供运维排查）。
        var outbox = new InMemoryDecisionCommitOutbox();
        await outbox.EnqueueAsync(BuildCommit("decision-4"));

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var batch = await outbox.AcquirePendingAsync(10, "worker-1", TimeSpan.FromMinutes(1));
            Assert.AreEqual(1, batch.Count, $"第 {attempt + 1} 次尝试应可领取（未达上限）。");
            await outbox.MarkFailedAsync(batch[0].OutboxId, batch[0].LeaseToken!, $"failure-{attempt + 1}");
        }

        // 第 6 次：已达死信上限，不再被领取。
        var dead = await outbox.AcquirePendingAsync(10, "worker-1", TimeSpan.FromMinutes(1));
        Assert.AreEqual(0, dead.Count, "达上限后转入死信，不再自动领取。");
    }

    [TestMethod]
    public async Task CommitCarriesRecordAndEvidenceRef()
    {
        // 可靠链载荷：决策记录（durable 归档本体）+ 证据引用（Evidence Manifest 关联）。
        var outbox = new InMemoryDecisionCommitOutbox();
        var commit = BuildCommit("decision-5");
        await outbox.EnqueueAsync(commit);

        var pending = await outbox.AcquirePendingAsync(10, "worker-1", TimeSpan.FromMinutes(1));
        Assert.AreEqual(1, pending.Count);
        Assert.AreEqual("decision-5", pending[0].Record.DecisionId, "载荷携带决策记录。");
        Assert.AreEqual("policy/v1", pending[0].Record.PolicyVersion);
        Assert.AreEqual("sig:abc", pending[0].EvidenceRef, "载荷携带证据引用。");
        Assert.AreEqual(DecisionCommitType.RecordAndMaterialize, pending[0].CommitType, "载荷携带物化意图。");
    }
}
