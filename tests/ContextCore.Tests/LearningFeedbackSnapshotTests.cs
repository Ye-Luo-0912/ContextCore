using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Evaluation.Learning;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// 可回放数据集（反馈事件快照）测试。
/// 覆盖：快照内容寻址不可变（相同输入同 ID、不同输入不同 ID）；训练/评测按事件 ID
/// 稳定哈希分桶且互不重叠；删除请求与已撤销事件被排除但保留在 lineage 中；
/// 按策略版本离线重放；校验函数重算输入指纹保证可追溯性。
/// </summary>
[TestClass]
[TestCategory("LR4C")]
[TestCategory("Learning")]
public sealed class LearningFeedbackSnapshotTests
{
    private const string Ws = "ws-snapshot";
    private const string Col = "col-snapshot";

    /// <summary>
    /// 验证：相同输入产出相同 SnapshotId（内容寻址，快照不可变）。
    /// </summary>
    [TestMethod]
    public void Build_SameInputs_SameSnapshotId()
    {
        var events = CreateEvents(4, policyVersion: "policy-v1");
        var deletions = new[] { new FeedbackDeletionRequest { FeedbackId = "event-1", Reason = "用户删除" } };

        var first = new LearningFeedbackSnapshotBuilder().Build(events, deletions, evalFraction: 0.2);
        var second = new LearningFeedbackSnapshotBuilder().Build(events, deletions, evalFraction: 0.2);

        Assert.AreEqual(first.SnapshotId, second.SnapshotId, "相同输入应产出相同快照 ID。");
        Assert.AreEqual(first.LineageSignature, second.LineageSignature);
    }

    /// <summary>
    /// 验证：输入变化（新增事件 / 删除请求变化）产出不同 SnapshotId。
    /// </summary>
    [TestMethod]
    public void Build_DifferentInputs_DifferentSnapshotId()
    {
        var events = CreateEvents(4, policyVersion: "policy-v1");
        var builder = new LearningFeedbackSnapshotBuilder();

        var baseline = builder.Build(events, Array.Empty<FeedbackDeletionRequest>(), evalFraction: 0.2);
        var withExtraEvent = builder.Build(
            [.. events, CreateEvent("event-extra", "policy-v1")],
            Array.Empty<FeedbackDeletionRequest>(),
            evalFraction: 0.2);
        var withDeletion = builder.Build(
            events,
            [new FeedbackDeletionRequest { FeedbackId = "event-1", Reason = "删除" }],
            evalFraction: 0.2);

        Assert.AreNotEqual(baseline.SnapshotId, withExtraEvent.SnapshotId, "新增事件应改变快照 ID。");
        Assert.AreNotEqual(baseline.SnapshotId, withDeletion.SnapshotId, "删除请求应改变快照 ID。");
    }

    /// <summary>
    /// 验证：训练/调参/评测分桶互不重叠、计数守恒、同一事件分桶稳定。
    /// </summary>
    [TestMethod]
    public void Build_Split_IsDisjointAndStable()
    {
        var events = CreateEvents(40, policyVersion: "policy-v1");
        var builder = new LearningFeedbackSnapshotBuilder();

        var first = builder.Build(events, Array.Empty<FeedbackDeletionRequest>(), devFraction: 0.1, evalFraction: 0.2);
        var second = builder.Build(events, Array.Empty<FeedbackDeletionRequest>(), devFraction: 0.1, evalFraction: 0.2);

        var trainIds = first.SplitAssignment
            .Where(pair => pair.Value == LearningSnapshotSplit.Train)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var devIds = first.SplitAssignment
            .Where(pair => pair.Value == LearningSnapshotSplit.Dev)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var evalIds = first.SplitAssignment
            .Where(pair => pair.Value == LearningSnapshotSplit.Eval)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.AreEqual(0, trainIds.Intersect(devIds).Count(), "训练与调参集合不得重叠。");
        Assert.AreEqual(0, trainIds.Intersect(evalIds).Count(), "训练与评测集合不得重叠。");
        Assert.AreEqual(0, devIds.Intersect(evalIds).Count(), "调参与评测集合不得重叠。");
        Assert.AreEqual(first.Events.Count, first.TrainCount + first.DevCount + first.EvalCount, "计数应守恒。");
        Assert.IsTrue(first.TrainCount > 0 && first.DevCount > 0 && first.EvalCount > 0, "三个分桶都应有事件。");
        Assert.IsTrue(
            first.SplitAssignment
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .SequenceEqual(second.SplitAssignment.OrderBy(pair => pair.Key, StringComparer.Ordinal)),
            "同一事件分桶应稳定。");
    }

    /// <summary>
    /// 验证：删除请求命中的事件不进快照数据，但保留在 lineage 的源事件与删除清单中。
    /// </summary>
    [TestMethod]
    public void Build_DeletionRequest_ExcludesEvent()
    {
        var events = CreateEvents(4, policyVersion: "policy-v1");
        var snapshot = new LearningFeedbackSnapshotBuilder().Build(
            events,
            [new FeedbackDeletionRequest { FeedbackId = "event-2", Reason = "用户删除" }],
            evalFraction: 0.2);

        Assert.IsFalse(snapshot.Events.Any(item => item.FeedbackId == "event-2"), "被删除事件不应进入快照数据。");
        CollectionAssert.Contains(snapshot.DeletedEventIds.ToArray(), "event-2");
        CollectionAssert.Contains(snapshot.SourceEventIds.ToArray(), "event-2", "lineage 应保留被删除事件 ID。");
        Assert.AreEqual(3, snapshot.Events.Count);
    }

    /// <summary>
    /// 验证：被撤销事件与其撤销记录都不进入快照数据，撤销目标保留在 lineage 中。
    /// </summary>
    [TestMethod]
    public void Build_RevokedEvent_IsExcluded()
    {
        var events = CreateEvents(3, policyVersion: "policy-v1");
        events.Add(CreateEvent("event-revoke", "policy-v1", kind: LearningFeedbackKinds.Revoke, revokesFeedbackId: "event-1"));

        var snapshot = new LearningFeedbackSnapshotBuilder().Build(events, Array.Empty<FeedbackDeletionRequest>(), evalFraction: 0.2);

        Assert.IsFalse(snapshot.Events.Any(item => item.FeedbackId == "event-1"), "被撤销事件不应进入快照数据。");
        Assert.IsFalse(snapshot.Events.Any(item => item.FeedbackId == "event-revoke"), "撤销记录本身不应进入快照数据。");
        CollectionAssert.Contains(snapshot.RevokedEventIds.ToArray(), "event-1");
        Assert.AreEqual(2, snapshot.Events.Count);
    }

    /// <summary>
    /// 验证：按策略版本离线重放只返回该版本的事件；快照限定版本时不返回其他版本。
    /// </summary>
    [TestMethod]
    public void Replay_FiltersByPolicyVersion()
    {
        var events = CreateEvents(3, policyVersion: "policy-v1");
        events.Add(CreateEvent("event-v2", "policy-v2"));
        var snapshot = new LearningFeedbackSnapshotBuilder().Build(events, Array.Empty<FeedbackDeletionRequest>(), evalFraction: 0.2);

        var v1 = LearningFeedbackSnapshotBuilder.Replay(snapshot, "policy-v1");
        var v2 = LearningFeedbackSnapshotBuilder.Replay(snapshot, "policy-v2");

        Assert.AreEqual(3, v1.Count, "重放 v1 应只返回 v1 事件。");
        Assert.IsTrue(v1.All(item => item.PolicyVersion == "policy-v1"));
        Assert.AreEqual(1, v2.Count);
        Assert.AreEqual("event-v2", v2[0].FeedbackId);

        var limited = new LearningFeedbackSnapshotBuilder().Build(
            events,
            Array.Empty<FeedbackDeletionRequest>(),
            evalFraction: 0.2,
            policyVersion: "policy-v1");
        Assert.AreEqual(0, LearningFeedbackSnapshotBuilder.Replay(limited, "policy-v2").Count,
            "快照限定 v1 时重放 v2 应返回空。");
    }

    /// <summary>
    /// 验证：重放可再按训练/调参/评测分桶过滤。
    /// </summary>
    [TestMethod]
    public void Replay_RespectsSplit()
    {
        var events = CreateEvents(40, policyVersion: "policy-v1");
        var snapshot = new LearningFeedbackSnapshotBuilder().Build(
            events,
            Array.Empty<FeedbackDeletionRequest>(),
            devFraction: 0.1,
            evalFraction: 0.2);

        var train = LearningFeedbackSnapshotBuilder.Replay(snapshot, null, LearningSnapshotSplit.Train);
        var dev = LearningFeedbackSnapshotBuilder.Replay(snapshot, null, LearningSnapshotSplit.Dev);
        var eval = LearningFeedbackSnapshotBuilder.Replay(snapshot, null, LearningSnapshotSplit.Eval);

        Assert.AreEqual(snapshot.TrainCount, train.Count);
        Assert.AreEqual(snapshot.DevCount, dev.Count);
        Assert.AreEqual(snapshot.EvalCount, eval.Count);
        Assert.IsTrue(train.All(item => snapshot.SplitAssignment[item.FeedbackId] == LearningSnapshotSplit.Train));
        Assert.IsTrue(dev.All(item => snapshot.SplitAssignment[item.FeedbackId] == LearningSnapshotSplit.Dev));
        Assert.IsTrue(eval.All(item => snapshot.SplitAssignment[item.FeedbackId] == LearningSnapshotSplit.Eval));
    }

    /// <summary>
    /// 验证：校验函数用原始事件集重算指纹可追溯；事件集变化后校验失败。
    /// </summary>
    [TestMethod]
    public void Verify_RecomputesLineageSignature()
    {
        var events = CreateEvents(4, policyVersion: "policy-v1");
        var snapshot = new LearningFeedbackSnapshotBuilder().Build(events, Array.Empty<FeedbackDeletionRequest>(), evalFraction: 0.2);

        Assert.IsTrue(LearningFeedbackSnapshotBuilder.Verify(snapshot, events), "原始事件集应通过可追溯校验。");

        var tampered = events.ToList();
        tampered.RemoveAt(0);
        Assert.IsFalse(LearningFeedbackSnapshotBuilder.Verify(snapshot, tampered), "事件集变化后校验应失败。");
    }

    /// <summary>
    /// 验证：从反馈事件存储构建快照（覆盖存储读取路径）。
    /// </summary>
    [TestMethod]
    public async Task BuildAsync_ReadsFromStore()
    {
        var store = new InMemoryLearningFeedbackStore();
        var service = new LearningFeedbackService(store);
        for (var i = 1; i <= 3; i++)
        {
            await service.SubmitAsync(new LearningFeedbackEvent
            {
                WorkspaceId = Ws,
                CollectionId = Col,
                Source = "runtime",
                SourceOperationId = $"operation-{i}",
                CapabilityId = ShadowCapabilityIds.VectorRetrieval,
                TargetId = $"candidate-{i}",
                TargetType = LearningFeedbackTargetType.VectorCandidate.ToString(),
                FeedbackKind = LearningFeedbackKinds.Useful,
                PolicyVersion = "policy-store-v1"
            });
        }

        var snapshot = await new LearningFeedbackSnapshotBuilder().BuildAsync(
            store,
            new LearningFeedbackEventQuery { WorkspaceId = Ws, CollectionId = Col, Limit = int.MaxValue },
            Array.Empty<FeedbackDeletionRequest>(),
            evalFraction: 0.2);

        Assert.AreEqual(3, snapshot.Events.Count, "存储中的 3 条反馈事件都应进入快照。");
        Assert.IsTrue(snapshot.Events.All(item => item.PolicyVersion == "policy-store-v1"));
    }

    // ── 构造 ────────────────────────────────────────────────────────────────

    private static List<LearningFeedbackEvent> CreateEvents(int count, string policyVersion)
    {
        return Enumerable.Range(1, count)
            .Select(i => CreateEvent($"event-{i}", policyVersion))
            .ToList();
    }

    private static LearningFeedbackEvent CreateEvent(
        string feedbackId,
        string policyVersion,
        string kind = LearningFeedbackKinds.Useful,
        string revokesFeedbackId = "")
        => new()
        {
            FeedbackId = feedbackId,
            WorkspaceId = Ws,
            CollectionId = Col,
            Source = "runtime",
            SourceOperationId = $"operation-{feedbackId}",
            CapabilityId = ShadowCapabilityIds.VectorRetrieval,
            TargetId = $"candidate-{feedbackId}",
            TargetType = LearningFeedbackTargetType.VectorCandidate.ToString(),
            FeedbackKind = kind,
            PolicyVersion = policyVersion,
            RevokesFeedbackId = revokesFeedbackId
        };
}
