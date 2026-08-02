using ContextCore.Abstractions;
using ContextCore.Core.Services.MemoryEvolution;

namespace ContextCore.Tests;

// ===========================================================================
// 用户反馈接入（API + ledger 写入）验收测试
//
// 目标：
//   验证用户显式反馈（thumbs up/down / 评分修正 / 文本反馈 / 举报）通过 UserFeedbackService
//   写入 IUserFeedbackLedger，并通过查询 API 验证条目可见。
//
// 设计原则（对齐 R29 §9.1 第 4 项：反馈接入）：
//   1. 反馈与 Utility Ledger 条目通过 (workspace_id, collection_id, decision_id, candidate_item_id) 关联。
//   2. 反馈不修改原始 ledger 条目（append-only 语义），独立写入 user_feedback_entries。
//   3. InMemory 实现跳过关联校验以保持测试友好；Postgres 实现做 EXISTS 校验。
//   4. 幂等键由调用方提供或自动生成；InMemory 不去重（append-only）。
//   5. P8 硬边界：用户反馈是观察信号；导出器需保留"无反馈"样本以避免偏差。
//
// 验收点：
//   - ThumbsUp 提交后 FeedbackValue = +1.0
//   - ThumbsDown 提交后 FeedbackValue = -1.0
//   - Report 提交后 FeedbackValue = -1.0
//   - TextFeedback 必须提供 FeedbackText，否则抛出 ArgumentException
//   - ScoreCorrection 必须提供 FeedbackValue 在 [0.0, 1.0]，否则抛出 ArgumentException
//   - Kind = Unknown 抛出 ArgumentException
//   - WorkspaceId / CollectionId / DecisionId / CandidateItemId 为空时抛出 ArgumentException
//   - 自动生成 FeedbackEntryId / GivenAt / IdempotencyKey
//   - 重复提交同 IdempotencyKey 在 InMemory 中追加（append-only，不去重）
//   - QueryFeedbackAsync 按 workspace/decision/candidate/kind/givenBy/since/until 过滤生效
//   - QueryFeedbackAsync 按 GivenAt 降序返回
//   - GetLatestFeedbackForCandidateAsync 返回最新反馈（按 GivenAt 降序取首条）
//   - GetLatestFeedbackForCandidateAsync 候选无反馈时返回 null
//   - DI 注册：UserFeedbackService 通过构造注入 IUserFeedbackLedger 可解析
// ===========================================================================

[TestClass]
[TestCategory("R29")]
[TestCategory("WP-E-5")]
public sealed class R29E_UserFeedbackAcceptanceTests
{
    private static readonly string Ws = "ws-wpe5";
    private static readonly string Col = "col-wpe5";

    // =====================================================================
    // Part 1：FeedbackValue 自动推导（ThumbsUp / ThumbsDown / Report / TextFeedback）
    // =====================================================================

    [TestMethod]
    public async Task SubmitAsync_ThumbsUp_DerivesFeedbackValueAsPositiveOne()
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();
        var service = new UserFeedbackService(ledger);

        var result = await service.SubmitAsync(MakeRequest(kind: UserFeedbackKind.ThumbsUp));

        Assert.AreEqual(1.0, result.Entry.FeedbackValue);
        Assert.AreEqual(UserFeedbackKind.ThumbsUp, result.Entry.Kind);
        Assert.IsTrue(result.Created);
    }

    [TestMethod]
    public async Task SubmitAsync_ThumbsDown_DerivesFeedbackValueAsNegativeOne()
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();
        var service = new UserFeedbackService(ledger);

        var result = await service.SubmitAsync(MakeRequest(kind: UserFeedbackKind.ThumbsDown));

        Assert.AreEqual(-1.0, result.Entry.FeedbackValue);
        Assert.AreEqual(UserFeedbackKind.ThumbsDown, result.Entry.Kind);
    }

    [TestMethod]
    public async Task SubmitAsync_Report_DerivesFeedbackValueAsNegativeOne()
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();
        var service = new UserFeedbackService(ledger);

        var result = await service.SubmitAsync(MakeRequest(kind: UserFeedbackKind.Report));

        Assert.AreEqual(-1.0, result.Entry.FeedbackValue);
        Assert.AreEqual(UserFeedbackKind.Report, result.Entry.Kind);
    }

    [TestMethod]
    public async Task SubmitAsync_TextFeedback_DerivesFeedbackValueAsZero()
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();
        var service = new UserFeedbackService(ledger);

        var result = await service.SubmitAsync(MakeRequest(
            kind: UserFeedbackKind.TextFeedback,
            feedbackText: "这个结果不太相关"));

        Assert.AreEqual(0.0, result.Entry.FeedbackValue);
        Assert.AreEqual(UserFeedbackKind.TextFeedback, result.Entry.Kind);
        Assert.AreEqual("这个结果不太相关", result.Entry.FeedbackText);
    }

    // =====================================================================
    // Part 2：ScoreCorrection 校验
    // =====================================================================

    [TestMethod]
    public async Task SubmitAsync_ScoreCorrection_WithValidValue_WritesFeedbackValue()
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();
        var service = new UserFeedbackService(ledger);

        var result = await service.SubmitAsync(MakeRequest(
            kind: UserFeedbackKind.ScoreCorrection,
            feedbackValue: 0.42));

        Assert.AreEqual(0.42, result.Entry.FeedbackValue);
        Assert.AreEqual(UserFeedbackKind.ScoreCorrection, result.Entry.Kind);
    }

    [TestMethod]
    [DataRow(0.0, DisplayName = "下界 0.0")]
    [DataRow(1.0, DisplayName = "上界 1.0")]
    public async Task SubmitAsync_ScoreCorrection_BoundaryValues_AreAccepted(double value)
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();
        var service = new UserFeedbackService(ledger);

        var result = await service.SubmitAsync(MakeRequest(
            kind: UserFeedbackKind.ScoreCorrection,
            feedbackValue: value));

        Assert.AreEqual(value, result.Entry.FeedbackValue);
    }

    [TestMethod]
    [DataRow(-0.01, DisplayName = "略低于下界")]
    [DataRow(1.01, DisplayName = "略高于上界")]
    [DataRow(double.NaN, DisplayName = "NaN")]
    [DataRow(double.PositiveInfinity, DisplayName = "+Infinity")]
    public async Task SubmitAsync_ScoreCorrection_OutOfRange_ThrowsArgumentException(double invalidValue)
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();
        var service = new UserFeedbackService(ledger);

        await Assert.ThrowsExceptionAsync<ArgumentException>(() => service.SubmitAsync(MakeRequest(
            kind: UserFeedbackKind.ScoreCorrection,
            feedbackValue: invalidValue)));
    }

    [TestMethod]
    public async Task SubmitAsync_ScoreCorrection_WithoutFeedbackValue_ThrowsArgumentException()
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();
        var service = new UserFeedbackService(ledger);

        await Assert.ThrowsExceptionAsync<ArgumentException>(() => service.SubmitAsync(MakeRequest(
            kind: UserFeedbackKind.ScoreCorrection,
            feedbackValue: null)));
    }

    // =====================================================================
    // Part 3：必填字段校验
    // =====================================================================

    [TestMethod]
    public async Task SubmitAsync_TextFeedback_WithoutFeedbackText_ThrowsArgumentException()
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();
        var service = new UserFeedbackService(ledger);

        await Assert.ThrowsExceptionAsync<ArgumentException>(() => service.SubmitAsync(MakeRequest(
            kind: UserFeedbackKind.TextFeedback,
            feedbackText: null)));
    }

    [TestMethod]
    public async Task SubmitAsync_UnknownKind_ThrowsArgumentException()
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();
        var service = new UserFeedbackService(ledger);

        await Assert.ThrowsExceptionAsync<ArgumentException>(() => service.SubmitAsync(MakeRequest(
            kind: UserFeedbackKind.Unknown)));
    }

    [TestMethod]
    public async Task SubmitAsync_EmptyWorkspaceId_ThrowsArgumentException()
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();
        var service = new UserFeedbackService(ledger);

        var badRequest = new UserFeedbackSubmitRequest
        {
            WorkspaceId = string.Empty,
            CollectionId = Col,
            DecisionId = "dec-1",
            CandidateItemId = "item-1",
            Kind = UserFeedbackKind.ThumbsUp
        };

        await Assert.ThrowsExceptionAsync<ArgumentException>(() => service.SubmitAsync(badRequest));
    }

    // =====================================================================
    // Part 4：自动生成字段
    // =====================================================================

    [TestMethod]
    public async Task SubmitAsync_AutoGenerates_FeedbackEntryId_GivenAt_IdempotencyKey()
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();
        var service = new UserFeedbackService(ledger);

        var result = await service.SubmitAsync(MakeRequest(kind: UserFeedbackKind.ThumbsUp));

        Assert.IsTrue(result.Entry.FeedbackEntryId.StartsWith("feedback-", StringComparison.Ordinal));
        Assert.IsTrue(result.Entry.IdempotencyKey.StartsWith("fb-idem-", StringComparison.Ordinal));
        Assert.IsTrue(result.Entry.GivenAt <= DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.IsTrue(result.Entry.GivenAt >= DateTimeOffset.UtcNow.AddSeconds(-5));
    }

    [TestMethod]
    public async Task SubmitAsync_PreservesCallerProvided_IdempotencyKey()
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();
        var service = new UserFeedbackService(ledger);

        var result = await service.SubmitAsync(MakeRequest(
            kind: UserFeedbackKind.ThumbsUp,
            idempotencyKey: "client-key-12345"));

        Assert.AreEqual("client-key-12345", result.Entry.IdempotencyKey);
    }

    // =====================================================================
    // Part 5：Append-only 语义（InMemory 不去重）
    // =====================================================================

    [TestMethod]
    public async Task SubmitAsync_DuplicateIdempotencyKey_AppendsBothEntries_InMemory()
    {
        // InMemory 实现是 append-only：同 IdempotencyKey 重复提交保留两条历史快照。
        // Postgres 实现通过 ON CONFLICT DO UPDATE 覆盖（保留最新一条）。
        var ledger = new InMemoryUserFeedbackLedgerStore();
        var service = new UserFeedbackService(ledger);

        await service.SubmitAsync(MakeRequest(
            kind: UserFeedbackKind.ThumbsUp,
            idempotencyKey: "dup-key-1"));
        await service.SubmitAsync(MakeRequest(
            kind: UserFeedbackKind.ThumbsUp,
            idempotencyKey: "dup-key-1"));

        var entries = await ledger.QueryFeedbackAsync(new UserFeedbackQuery { WorkspaceId = Ws });
        Assert.AreEqual(2, entries.Count, "InMemory 应保留两条历史快照（append-only 语义）。");
    }

    // =====================================================================
    // Part 6：QueryFeedbackAsync 过滤与排序
    // =====================================================================

    [TestMethod]
    public async Task QueryFeedbackAsync_ReturnsEntriesInDescendingOrderByGivenAt()
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();
        var service = new UserFeedbackService(ledger);
        var t1 = DateTimeOffset.UtcNow;
        var t2 = t1.AddSeconds(1);
        var t3 = t1.AddSeconds(2);

        await WriteFeedbackDirectAsync(ledger, "fb-1", "dec-1", "item-1", UserFeedbackKind.ThumbsUp, givenAt: t1);
        await WriteFeedbackDirectAsync(ledger, "fb-2", "dec-1", "item-1", UserFeedbackKind.ThumbsDown, givenAt: t3);
        await WriteFeedbackDirectAsync(ledger, "fb-3", "dec-1", "item-1", UserFeedbackKind.ThumbsUp, givenAt: t2);

        var results = await ledger.QueryFeedbackAsync(new UserFeedbackQuery { WorkspaceId = Ws });

        Assert.AreEqual(3, results.Count);
        Assert.AreEqual("fb-2", results[0].FeedbackEntryId); // t3 最新
        Assert.AreEqual("fb-3", results[1].FeedbackEntryId); // t2
        Assert.AreEqual("fb-1", results[2].FeedbackEntryId); // t1 最旧
    }

    [TestMethod]
    public async Task QueryFeedbackAsync_FiltersByDecisionId()
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();

        await WriteFeedbackDirectAsync(ledger, "fb-1", "dec-A", "item-1", UserFeedbackKind.ThumbsUp);
        await WriteFeedbackDirectAsync(ledger, "fb-2", "dec-B", "item-2", UserFeedbackKind.ThumbsUp);

        var results = await ledger.QueryFeedbackAsync(new UserFeedbackQuery
        {
            WorkspaceId = Ws,
            DecisionId = "dec-A"
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("fb-1", results[0].FeedbackEntryId);
    }

    [TestMethod]
    public async Task QueryFeedbackAsync_FiltersByCandidateItemId()
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();

        await WriteFeedbackDirectAsync(ledger, "fb-1", "dec-1", "item-A", UserFeedbackKind.ThumbsUp);
        await WriteFeedbackDirectAsync(ledger, "fb-2", "dec-1", "item-B", UserFeedbackKind.ThumbsUp);

        var results = await ledger.QueryFeedbackAsync(new UserFeedbackQuery
        {
            WorkspaceId = Ws,
            CandidateItemId = "item-B"
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("fb-2", results[0].FeedbackEntryId);
    }

    [TestMethod]
    public async Task QueryFeedbackAsync_FiltersByKind()
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();

        await WriteFeedbackDirectAsync(ledger, "fb-1", "dec-1", "item-1", UserFeedbackKind.ThumbsUp);
        await WriteFeedbackDirectAsync(ledger, "fb-2", "dec-1", "item-2", UserFeedbackKind.ThumbsDown);

        var upResults = await ledger.QueryFeedbackAsync(new UserFeedbackQuery
        {
            WorkspaceId = Ws,
            Kind = UserFeedbackKind.ThumbsUp
        });
        var downResults = await ledger.QueryFeedbackAsync(new UserFeedbackQuery
        {
            WorkspaceId = Ws,
            Kind = UserFeedbackKind.ThumbsDown
        });

        Assert.AreEqual(1, upResults.Count);
        Assert.AreEqual("fb-1", upResults[0].FeedbackEntryId);
        Assert.AreEqual(1, downResults.Count);
        Assert.AreEqual("fb-2", downResults[0].FeedbackEntryId);
    }

    [TestMethod]
    public async Task QueryFeedbackAsync_FiltersByGivenBy()
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();

        await WriteFeedbackDirectAsync(ledger, "fb-1", "dec-1", "item-1", UserFeedbackKind.ThumbsUp, givenBy: "alice");
        await WriteFeedbackDirectAsync(ledger, "fb-2", "dec-1", "item-2", UserFeedbackKind.ThumbsUp, givenBy: "bob");

        var results = await ledger.QueryFeedbackAsync(new UserFeedbackQuery
        {
            WorkspaceId = Ws,
            GivenBy = "alice"
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("fb-1", results[0].FeedbackEntryId);
    }

    [TestMethod]
    public async Task QueryFeedbackAsync_FiltersBySinceUntil()
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();
        var t1 = DateTimeOffset.UtcNow;
        var t2 = t1.AddSeconds(10);
        var t3 = t1.AddSeconds(20);

        await WriteFeedbackDirectAsync(ledger, "fb-1", "dec-1", "item-1", UserFeedbackKind.ThumbsUp, givenAt: t1);
        await WriteFeedbackDirectAsync(ledger, "fb-2", "dec-1", "item-1", UserFeedbackKind.ThumbsUp, givenAt: t2);
        await WriteFeedbackDirectAsync(ledger, "fb-3", "dec-1", "item-1", UserFeedbackKind.ThumbsUp, givenAt: t3);

        var sinceResults = await ledger.QueryFeedbackAsync(new UserFeedbackQuery
        {
            WorkspaceId = Ws,
            Since = t2
        });
        var untilResults = await ledger.QueryFeedbackAsync(new UserFeedbackQuery
        {
            WorkspaceId = Ws,
            Until = t2
        });

        Assert.AreEqual(2, sinceResults.Count); // t2, t3
        Assert.AreEqual(2, untilResults.Count); // t1, t2
    }

    [TestMethod]
    public async Task QueryFeedbackAsync_RespectsTakeLimit()
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();

        await WriteFeedbackDirectAsync(ledger, "fb-1", "dec-1", "item-1", UserFeedbackKind.ThumbsUp);
        await WriteFeedbackDirectAsync(ledger, "fb-2", "dec-1", "item-1", UserFeedbackKind.ThumbsUp);
        await WriteFeedbackDirectAsync(ledger, "fb-3", "dec-1", "item-1", UserFeedbackKind.ThumbsUp);

        var results = await ledger.QueryFeedbackAsync(new UserFeedbackQuery
        {
            WorkspaceId = Ws,
            Take = 2
        });

        Assert.AreEqual(2, results.Count);
    }

    [TestMethod]
    public async Task QueryFeedbackAsync_TakeZero_ReturnsAll()
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();

        await WriteFeedbackDirectAsync(ledger, "fb-1", "dec-1", "item-1", UserFeedbackKind.ThumbsUp);
        await WriteFeedbackDirectAsync(ledger, "fb-2", "dec-1", "item-1", UserFeedbackKind.ThumbsUp);

        var results = await ledger.QueryFeedbackAsync(new UserFeedbackQuery
        {
            WorkspaceId = Ws,
            Take = 0
        });

        Assert.AreEqual(2, results.Count);
    }

    [TestMethod]
    public async Task QueryFeedbackAsync_FiltersByCollectionId()
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();

        await WriteFeedbackDirectAsync(ledger, "fb-1", "dec-1", "item-1", UserFeedbackKind.ThumbsUp,
            collectionId: "col-A");
        await WriteFeedbackDirectAsync(ledger, "fb-2", "dec-1", "item-2", UserFeedbackKind.ThumbsUp,
            collectionId: "col-B");

        var results = await ledger.QueryFeedbackAsync(new UserFeedbackQuery
        {
            WorkspaceId = Ws,
            CollectionId = "col-A"
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("fb-1", results[0].FeedbackEntryId);
    }

    // =====================================================================
    // Part 7：GetLatestFeedbackForCandidateAsync
    // =====================================================================

    [TestMethod]
    public async Task GetLatestFeedbackForCandidateAsync_ReturnsLatestByGivenAt()
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();
        var t1 = DateTimeOffset.UtcNow;
        var t2 = t1.AddSeconds(5);

        await WriteFeedbackDirectAsync(ledger, "fb-1", "dec-1", "item-1", UserFeedbackKind.ThumbsUp, givenAt: t1);
        await WriteFeedbackDirectAsync(ledger, "fb-2", "dec-1", "item-1", UserFeedbackKind.ThumbsDown, givenAt: t2);

        var latest = await ledger.GetLatestFeedbackForCandidateAsync(Ws, Col, "item-1");

        Assert.IsNotNull(latest);
        Assert.AreEqual("fb-2", latest.FeedbackEntryId);
        Assert.AreEqual(UserFeedbackKind.ThumbsDown, latest.Kind);
    }

    [TestMethod]
    public async Task GetLatestFeedbackForCandidateAsync_NoFeedback_ReturnsNull()
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();

        var latest = await ledger.GetLatestFeedbackForCandidateAsync(Ws, Col, "no-feedback-item");

        Assert.IsNull(latest);
    }

    [TestMethod]
    public async Task GetLatestFeedbackForCandidateAsync_DistinguishesByCandidate()
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();

        await WriteFeedbackDirectAsync(ledger, "fb-1", "dec-1", "item-A", UserFeedbackKind.ThumbsUp);
        await WriteFeedbackDirectAsync(ledger, "fb-2", "dec-1", "item-B", UserFeedbackKind.ThumbsDown);

        var latestA = await ledger.GetLatestFeedbackForCandidateAsync(Ws, Col, "item-A");
        var latestB = await ledger.GetLatestFeedbackForCandidateAsync(Ws, Col, "item-B");

        Assert.IsNotNull(latestA);
        Assert.IsNotNull(latestB);
        Assert.AreEqual(UserFeedbackKind.ThumbsUp, latestA.Kind);
        Assert.AreEqual(UserFeedbackKind.ThumbsDown, latestB.Kind);
    }

    // =====================================================================
    // Part 8：Metadata 透传
    // =====================================================================

    [TestMethod]
    public async Task SubmitAsync_PreservesMetadata()
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();
        var service = new UserFeedbackService(ledger);

        var metadata = new Dictionary<string, string>
        {
            ["source"] = "web-ui",
            ["sessionId"] = "sess-abc",
            ["variant"] = "v2"
        };

        var result = await service.SubmitAsync(MakeRequest(
            kind: UserFeedbackKind.ThumbsUp,
            metadata: metadata));

        Assert.AreEqual(3, result.Entry.Metadata.Count);
        Assert.AreEqual("web-ui", result.Entry.Metadata["source"]);
        Assert.AreEqual("sess-abc", result.Entry.Metadata["sessionId"]);
        Assert.AreEqual("v2", result.Entry.Metadata["variant"]);
    }

    [TestMethod]
    public async Task SubmitAsync_NullMetadata_DefaultsToEmptyDictionary()
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();
        var service = new UserFeedbackService(ledger);

        var result = await service.SubmitAsync(MakeRequest(
            kind: UserFeedbackKind.ThumbsUp,
            metadata: null));

        Assert.IsNotNull(result.Entry.Metadata);
        Assert.AreEqual(0, result.Entry.Metadata.Count);
    }

    // =====================================================================
    // Part 9：TimeProvider 注入（测试可注入固定时间）
    // =====================================================================

    [TestMethod]
    public async Task SubmitAsync_WithTimeProvider_UsesProvidedUtcNow()
    {
        var ledger = new InMemoryUserFeedbackLedgerStore();
        var fixedTime = DateTimeOffset.Parse("2026-03-15T10:30:00Z");
        var timeProvider = new FixedTimeProvider(fixedTime);
        var service = new UserFeedbackService(ledger, timeProvider);

        var result = await service.SubmitAsync(MakeRequest(kind: UserFeedbackKind.ThumbsUp));

        Assert.AreEqual(fixedTime, result.Entry.GivenAt);
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static UserFeedbackSubmitRequest MakeRequest(
        UserFeedbackKind kind,
        string? feedbackText = null,
        double? feedbackValue = null,
        string? idempotencyKey = null,
        string? givenBy = null,
        string workspaceId = "ws-wpe5",
        string collectionId = "col-wpe5",
        string decisionId = "dec-wpe5",
        string candidateItemId = "item-wpe5",
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return new UserFeedbackSubmitRequest
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            DecisionId = decisionId,
            CandidateItemId = candidateItemId,
            Kind = kind,
            FeedbackValue = feedbackValue,
            FeedbackText = feedbackText,
            GivenBy = givenBy,
            IdempotencyKey = idempotencyKey,
            Metadata = metadata
        };
    }

    private static Task WriteFeedbackDirectAsync(
        InMemoryUserFeedbackLedgerStore ledger,
        string feedbackEntryId,
        string decisionId,
        string candidateItemId,
        UserFeedbackKind kind,
        double? feedbackValue = null,
        string? feedbackText = null,
        string? givenBy = null,
        DateTimeOffset? givenAt = null,
        string? idempotencyKey = null,
        string collectionId = "col-wpe5")
    {
        var value = feedbackValue ?? kind switch
        {
            UserFeedbackKind.ThumbsUp => 1.0,
            UserFeedbackKind.ThumbsDown => -1.0,
            UserFeedbackKind.Report => -1.0,
            UserFeedbackKind.TextFeedback => 0.0,
            _ => 0.0
        };

        return ledger.AppendFeedbackAsync(new UserFeedbackEntry
        {
            FeedbackEntryId = feedbackEntryId,
            WorkspaceId = Ws,
            CollectionId = collectionId,
            DecisionId = decisionId,
            CandidateItemId = candidateItemId,
            Kind = kind,
            FeedbackValue = value,
            FeedbackText = feedbackText,
            GivenBy = givenBy,
            GivenAt = givenAt ?? DateTimeOffset.UtcNow,
            IdempotencyKey = idempotencyKey ?? "idem-" + feedbackEntryId
        });
    }

    /// <summary>测试用 TimeProvider，固定返回构造时给定的时间。</summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _fixedTime;

        public FixedTimeProvider(DateTimeOffset fixedTime)
        {
            _fixedTime = fixedTime;
        }

        public override DateTimeOffset GetUtcNow() => _fixedTime;
    }
}
