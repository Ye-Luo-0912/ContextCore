using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// 版本化、追加式反馈事件模型测试。
/// 覆盖：事件携带决策上下文（请求/策略版本、查询/候选/选中 ID、工具结果摘要）；
/// 撤销事件以独立事件追加并保留原事件；正文类元数据键被剥离（正文默认不进入反馈事件）；
/// 相同上下文重复提交幂等去重；撤销事件必须使用 Revoke 类型。
/// </summary>
[TestClass]
[TestCategory("LR4A")]
[TestCategory("Learning")]
public sealed class RuntimeFeedbackEventModelTests
{
    private const string Ws = "ws-feedback";
    private const string Col = "col-feedback";

    /// <summary>
    /// 验证：带决策上下文的反馈事件完整保留请求 ID、策略版本、
    /// 查询/候选/选中 ID 与结构化工具结果，导出 JSONL 也包含这些字段。
    /// </summary>
    [TestMethod]
    public async Task Submit_WithDecisionContext_PreservesVersionAndContext()
    {
        var service = new LearningFeedbackService(new InMemoryLearningFeedbackStore());
        var feedback = CreateFeedback(kind: LearningFeedbackKinds.MissingContext);
        feedback.RequestId = "req-decision-42";
        feedback.PolicyVersion = "policy-bundle-v3";
        feedback.QueryIds = ["query-1", "query-2"];
        feedback.CandidateIds = ["cand-a", "cand-b", "cand-c"];
        feedback.SelectedIds = ["cand-a"];
        feedback.ToolResults =
        [
            new FeedbackToolResult { ToolName = "search", Succeeded = true, EntityIds = ["cand-a", "cand-b"] },
            new FeedbackToolResult { ToolName = "read", Succeeded = false, EntityIds = [] }
        ];

        var result = await service.SubmitAsync(feedback);

        Assert.IsTrue(result.Created);
        Assert.AreEqual(1, result.Event.EventSchemaVersion, "事件模型版本应保留。");
        Assert.AreEqual("req-decision-42", result.Event.RequestId);
        Assert.AreEqual("policy-bundle-v3", result.Event.PolicyVersion);
        CollectionAssert.AreEqual(new[] { "query-1", "query-2" }, result.Event.QueryIds.ToArray());
        CollectionAssert.AreEqual(new[] { "cand-a", "cand-b", "cand-c" }, result.Event.CandidateIds.ToArray());
        CollectionAssert.AreEqual(new[] { "cand-a" }, result.Event.SelectedIds.ToArray());
        Assert.AreEqual(2, result.Event.ToolResults.Count);
        Assert.AreEqual("search", result.Event.ToolResults[0].ToolName);
        Assert.IsTrue(result.Event.ToolResults[0].Succeeded);
        CollectionAssert.AreEqual(new[] { "cand-a", "cand-b" }, result.Event.ToolResults[0].EntityIds.ToArray());
        Assert.IsFalse(result.Event.ToolResults[1].Succeeded);

        var jsonl = await service.ExportJsonLinesAsync(new LearningFeedbackEventQuery
        {
            WorkspaceId = Ws,
            CollectionId = Col
        });
        StringAssert.Contains(jsonl, "req-decision-42", "导出应包含请求 ID。");
        StringAssert.Contains(jsonl, "policy-bundle-v3", "导出应包含策略版本。");
    }

    /// <summary>
    /// 验证：撤销事件作为独立事件追加，原事件保持不变；撤销时间自动补齐；
    /// 可按 Revoke 类型查询到撤销事件。
    /// </summary>
    [TestMethod]
    public async Task Submit_WithRevocation_AppendsDistinctEvent()
    {
        var service = new LearningFeedbackService(new InMemoryLearningFeedbackStore());
        var original = CreateFeedback(kind: LearningFeedbackKinds.Useful);
        var created = await service.SubmitAsync(original);

        var revocation = CreateFeedback(kind: LearningFeedbackKinds.Revoke, targetId: "candidate-1");
        revocation.RevokesFeedbackId = created.Event.FeedbackId;

        var revoked = await service.SubmitAsync(revocation);

        Assert.IsTrue(revoked.Created, "撤销事件应作为新事件追加。");
        Assert.AreEqual(created.Event.FeedbackId, revoked.Event.RevokesFeedbackId);
        Assert.IsNotNull(revoked.Event.RevokedAt, "撤销时间应自动补齐。");
        Assert.AreNotEqual(created.Event.FeedbackId, revoked.Event.FeedbackId, "撤销事件应有独立 ID。");

        var rows = await service.ListAsync(new LearningFeedbackEventQuery
        {
            WorkspaceId = Ws,
            CollectionId = Col,
            Limit = int.MaxValue
        });
        Assert.AreEqual(2, rows.Count, "原事件与撤销事件都应保留。");
        Assert.IsTrue(rows.Any(item => item.FeedbackId == created.Event.FeedbackId), "原事件不应被覆盖。");
        Assert.IsTrue(rows.Any(item => item.FeedbackKind == LearningFeedbackKinds.Revoke), "应能查到撤销事件。");

        var revocations = await service.ListAsync(new LearningFeedbackEventQuery
        {
            WorkspaceId = Ws,
            CollectionId = Col,
            FeedbackKind = LearningFeedbackKinds.Revoke,
            Limit = int.MaxValue
        });
        Assert.AreEqual(1, revocations.Count);
        Assert.AreEqual(created.Event.FeedbackId, revocations[0].RevokesFeedbackId);
    }

    /// <summary>
    /// 验证：正文类元数据键（content/body 等）被剥离并产生告警，事件不携带正文。
    /// </summary>
    [TestMethod]
    public async Task Submit_WithBodyLikeMetadata_StripsContentKeys()
    {
        var service = new LearningFeedbackService(new InMemoryLearningFeedbackStore());
        var feedback = CreateFeedback(kind: LearningFeedbackKinds.NotUseful);
        feedback.Metadata["content"] = "候选正文不应该进入反馈事件";
        feedback.Metadata["body"] = "同样不应该";

        var result = await service.SubmitAsync(feedback);

        Assert.IsFalse(result.Event.Metadata.ContainsKey("content"), "content 键应被剥离。");
        Assert.IsFalse(result.Event.Metadata.ContainsKey("body"), "body 键应被剥离。");
        Assert.IsTrue(result.Event.Metadata.ContainsKey("source"), "非正文键应保留。");
        Assert.IsTrue(result.Warnings.Any(warning => warning.Contains("content", StringComparison.Ordinal)),
            "应产生剥离正文键的告警。");
    }

    /// <summary>
    /// 验证：相同上下文的重复提交幂等去重（FeedbackId 即幂等键），不会产生重复事件。
    /// </summary>
    [TestMethod]
    public async Task Submit_IdenticalContextResubmission_IsIdempotent()
    {
        var service = new LearningFeedbackService(new InMemoryLearningFeedbackStore());
        var feedback = CreateFeedback(kind: LearningFeedbackKinds.WrongCandidate);
        feedback.RequestId = "req-idempotent-1";
        feedback.CandidateIds = ["cand-x"];

        var created = await service.SubmitAsync(feedback);
        var replaced = await service.SubmitAsync(feedback);

        Assert.IsTrue(created.Created);
        Assert.IsTrue(replaced.DuplicateReplaced, "相同上下文的重复提交应去重。");
        Assert.AreEqual(created.Event.FeedbackId, replaced.Event.FeedbackId);

        var rows = await service.ListAsync(new LearningFeedbackEventQuery
        {
            WorkspaceId = Ws,
            CollectionId = Col,
            Limit = int.MaxValue
        });
        Assert.AreEqual(1, rows.Count, "追加式日志对相同逻辑事件只保留一条。");
    }

    /// <summary>
    /// 验证：带撤销目标的反馈必须使用 Revoke 类型，否则拒绝。
    /// </summary>
    [TestMethod]
    public async Task Submit_RevocationWithoutRevokeKind_IsRejected()
    {
        var service = new LearningFeedbackService(new InMemoryLearningFeedbackStore());
        var feedback = CreateFeedback(kind: LearningFeedbackKinds.Useful);
        feedback.RevokesFeedbackId = "lfb_some-other-event";

        await Assert.ThrowsExceptionAsync<ArgumentException>(() => service.SubmitAsync(feedback));
    }

    /// <summary>
    /// 验证：ID 列表字段裁剪空白、去重并截断超限条目，事件保留规范化结果。
    /// </summary>
    [TestMethod]
    public async Task Submit_ListFields_AreTrimmedDedupedAndCapped()
    {
        var service = new LearningFeedbackService(new InMemoryLearningFeedbackStore());
        var feedback = CreateFeedback(kind: LearningFeedbackKinds.RankingWrong);
        feedback.CandidateIds = new[]
        {
            "  cand-1 ", "cand-1", "cand-2", " ", string.Empty,
            "cand-3", "cand-4", "cand-5", "cand-6", "cand-7"
        };

        var result = await service.SubmitAsync(feedback);

        CollectionAssert.AreEqual(
            new[] { "cand-1", "cand-2", "cand-3", "cand-4", "cand-5", "cand-6", "cand-7" },
            result.Event.CandidateIds.ToArray(),
            "应裁剪空白、去重并丢弃空值。");
    }

    /// <summary>
    /// 验证：文件存储持久化后读回，决策上下文与版本字段完整保留（JSON 往返不丢字段）。
    /// </summary>
    [TestMethod]
    public async Task FileStore_RoundTrip_PreservesDecisionContext()
    {
        var root = Path.Combine(Path.GetTempPath(), "contextcore-feedback-model-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileLearningFeedbackStore(new FileStorageOptions { RootPath = root });
            var service = new LearningFeedbackService(store);
            var feedback = CreateFeedback(kind: LearningFeedbackKinds.Useful);
            feedback.RequestId = "req-file-7";
            feedback.PolicyVersion = "policy-file-v1";
            feedback.QueryIds = ["query-file"];
            feedback.CandidateIds = ["cand-file-1"];
            feedback.SelectedIds = ["cand-file-1"];
            feedback.ToolResults =
            [
                new FeedbackToolResult { ToolName = "search", Succeeded = true, EntityIds = ["cand-file-1"] }
            ];

            var result = await service.SubmitAsync(feedback);
            var found = await store.GetAsync(result.FeedbackId);

            Assert.IsNotNull(found);
            Assert.AreEqual("req-file-7", found.RequestId, "文件存储往返应保留请求 ID。");
            Assert.AreEqual("policy-file-v1", found.PolicyVersion);
            Assert.AreEqual(1, found.EventSchemaVersion);
            CollectionAssert.AreEqual(new[] { "query-file" }, found.QueryIds.ToArray());
            CollectionAssert.AreEqual(new[] { "cand-file-1" }, found.CandidateIds.ToArray());
            Assert.AreEqual(1, found.ToolResults.Count);
            Assert.AreEqual("search", found.ToolResults[0].ToolName);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // ── 构造 ────────────────────────────────────────────────────────────────

    private static LearningFeedbackEvent CreateFeedback(string kind, string targetId = "candidate-1")
        => new()
        {
            WorkspaceId = Ws,
            CollectionId = Col,
            Source = "runtime",
            SourceOperationId = "operation-" + kind,
            CapabilityId = ShadowCapabilityIds.VectorRetrieval,
            TargetId = targetId,
            TargetType = LearningFeedbackTargetType.VectorCandidate.ToString(),
            FeedbackKind = kind,
            FeedbackValue = kind == LearningFeedbackKinds.Useful ? 1.0 : -1.0,
            Confidence = 0.9,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["source"] = "package-preview"
            }
        };
}
