using System.Collections.Concurrent;
using System.Text.Json;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.Learning.V14_0;

namespace ContextCore.Tests;

/// <summary>
/// P0-6.3 验收测试：PackageTraceRecorder 写入的 trace row 携带精确的
/// CandidateOutcome + IncludedTokens/OriginalTokens/TruncationRatio，
/// 使下游诊断能区分 Accepted/PartiallyAccepted/Rejected/Dropped 并观察截断比率。
/// </summary>
[TestClass]
[TestCategory("Package")]
public sealed class ContextCoreTraceOutcomeTests
{
    /// <summary>捕获所有 trace 写入的 sink，用于断言每行的 outcome/token 字段。</summary>
    private sealed class CapturingTraceSink : IRuntimeCandidateTraceSink
    {
        private readonly ConcurrentBag<RuntimeCandidateTraceRow> _rows = new();
        public bool Enabled => true;
        public int WriteCount => _rows.Count;
        public IReadOnlyCollection<RuntimeCandidateTraceRow> Rows => _rows;
        public void Write(RuntimeCandidateTraceRow row) => _rows.Add(row);
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static PackageTraceRecorder CreateRecorder(CapturingTraceSink sink)
        => new(sink, () => "op-test", () => "req-test");

    /// <summary>构造 EstimatedTokens 确定的候选（Content 长度不参与断言，仅 EstimatedTokens 用于 trace 字段）。</summary>
    private static PackageTraceCandidate MakeCandidate(string id, int estimatedTokens)
        => PackageTraceCandidate.FromMemory(
            new ContextMemoryItem
            {
                Id = id,
                WorkspaceId = "ws",
                CollectionId = "col",
                Type = "memory",
                Content = new string('x', estimatedTokens * 3),
                ContentFormat = ContextContentFormat.PlainText,
                Status = ContextMemoryStatus.Active
            },
            "working_memory",
            score: 50.0,
            estimatedTokens: estimatedTokens);

    private static void InvokeAddSectionDecisions(
        PackageTraceRecorder recorder,
        IReadOnlyList<PackageTraceCandidate> candidates,
        SectionPackingResult sectionResult,
        out List<ContextPackageDecision> selectedItems,
        out List<DroppedContextItem> droppedItems)
    {
        selectedItems = new List<ContextPackageDecision>();
        droppedItems = new List<DroppedContextItem>();
        var globalSelectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var primaryDecisions = new Dictionary<string, ContextPackageDecision>();
        var itemReferences = new List<ContextPackageItemReference>();
        recorder.AddSectionDecisionsWithDedup(
            selectedItems, droppedItems, candidates, "working_memory", sectionResult,
            globalSelectedIds, primaryDecisions, itemReferences);
    }

    /// <summary>
    /// P0-6.3: Accepted 候选的 trace row 应有 Outcome=Accepted,
    /// IncludedTokens=OriginalTokens, TruncationRatio=1.0, IncludedInPackage=true。
    /// </summary>
    [TestMethod]
    public void TraceRow_AcceptedCandidate_HasFullIncludedTokensAndRatioOne()
    {
        var sink = new CapturingTraceSink();
        var recorder = CreateRecorder(sink);
        var candidates = new[] { MakeCandidate("acc-1", 10), MakeCandidate("acc-2", 20) };
        var sectionResult = SectionPackingResult.Selected(
            reason: "selected for package section",
            actualTokens: 30,
            truncated: false,
            acceptedCandidateIds: new[] { "acc-1", "acc-2" },
            rejectedCandidateIds: Array.Empty<string>());

        InvokeAddSectionDecisions(recorder, candidates, sectionResult, out var selectedItems, out _);

        Assert.AreEqual(2, selectedItems.Count, "两个 Accepted 候选应进入 selectedItems");
        var rows = sink.Rows.ToArray();
        Assert.AreEqual(2, rows.Length);
        var byId = rows.ToDictionary(r => r.CandidateId, r => r);

        var acc1 = byId["acc-1"];
        Assert.AreEqual(RuntimeCandidateOutcome.Accepted, acc1.Outcome);
        Assert.AreEqual(10, acc1.OriginalTokens);
        Assert.AreEqual(10, acc1.IncludedTokens);
        Assert.AreEqual(1.0, acc1.TruncationRatio);
        Assert.IsTrue(acc1.IncludedInPackage);

        var acc2 = byId["acc-2"];
        Assert.AreEqual(RuntimeCandidateOutcome.Accepted, acc2.Outcome);
        Assert.AreEqual(20, acc2.OriginalTokens);
        Assert.AreEqual(20, acc2.IncludedTokens);
        Assert.AreEqual(1.0, acc2.TruncationRatio);
    }

    /// <summary>
    /// P0-6.3: PartiallyAccepted 候选的 trace row 应有 Outcome=PartiallyAccepted,
    /// IncludedTokens=PartiallyAcceptedIncludedTokens, 0 &lt; TruncationRatio &lt; 1。
    /// 同一 section 内的 Rejected 候选应有 Outcome=Rejected, IncludedTokens=0, TruncationRatio=0。
    /// </summary>
    [TestMethod]
    public void TraceRow_PartiallyAcceptedAndRejected_OutcomeAndTokenFieldsArePrecise()
    {
        var sink = new CapturingTraceSink();
        var recorder = CreateRecorder(sink);
        var candidates = new[]
        {
            MakeCandidate("acc-1", 10),
            MakeCandidate("part-1", 20),
            MakeCandidate("rej-1", 15)
        };
        var sectionResult = SectionPackingResult.Selected(
            reason: "selected and truncated to fit token budget",
            actualTokens: 24,
            truncated: true,
            acceptedCandidateIds: new[] { "acc-1" },
            rejectedCandidateIds: new[] { "rej-1" },
            partiallyAcceptedCandidateId: "part-1",
            partiallyAcceptedIncludedTokens: 7);

        InvokeAddSectionDecisions(recorder, candidates, sectionResult, out var selectedItems, out var droppedItems);

        // acc-1 与 part-1 进 selectedItems，rej-1 进 droppedItems
        Assert.AreEqual(2, selectedItems.Count);
        Assert.AreEqual(1, droppedItems.Count);

        var rows = sink.Rows.ToArray();
        Assert.AreEqual(3, rows.Length);
        var byId = rows.ToDictionary(r => r.CandidateId, r => r);

        var part = byId["part-1"];
        Assert.AreEqual(RuntimeCandidateOutcome.PartiallyAccepted, part.Outcome);
        Assert.AreEqual(20, part.OriginalTokens);
        Assert.AreEqual(7, part.IncludedTokens);
        Assert.IsTrue(part.TruncationRatio > 0 && part.TruncationRatio < 1.0,
            $"PartiallyAccepted 的 TruncationRatio 应在 (0,1) 区间，实际={part.TruncationRatio}");
        Assert.IsTrue(part.IncludedInPackage, "PartiallyAccepted 仍应 IncludedInPackage=true");
        // 7/20 = 0.35
        Assert.AreEqual(0.35, Math.Round(part.TruncationRatio, 2), 0.001,
            "TruncationRatio 应等于 IncludedTokens/OriginalTokens = 7/20");

        var rej = byId["rej-1"];
        Assert.AreEqual(RuntimeCandidateOutcome.Rejected, rej.Outcome);
        Assert.AreEqual(15, rej.OriginalTokens);
        Assert.AreEqual(0, rej.IncludedTokens);
        Assert.AreEqual(0.0, rej.TruncationRatio);
        Assert.IsFalse(rej.IncludedInPackage);

        // acc-1 仍为完整 Accepted
        var acc = byId["acc-1"];
        Assert.AreEqual(RuntimeCandidateOutcome.Accepted, acc.Outcome);
        Assert.AreEqual(1.0, acc.TruncationRatio);
    }

    /// <summary>
    /// P0-6.3: Section 被 Dropped（如预算耗尽）时，所有候选 Outcome=Dropped,
    /// IncludedTokens=0, TruncationRatio=0, IncludedInPackage=false, SelectedByScoring=false。
    /// </summary>
    [TestMethod]
    public void TraceRow_DroppedSection_AllCandidatesHaveDroppedOutcome()
    {
        var sink = new CapturingTraceSink();
        var recorder = CreateRecorder(sink);
        var candidates = new[] { MakeCandidate("drop-1", 10), MakeCandidate("drop-2", 20) };
        var sectionResult = SectionPackingResult.Dropped("token budget exhausted");

        InvokeAddSectionDecisions(recorder, candidates, sectionResult, out _, out var droppedItems);

        Assert.AreEqual(2, droppedItems.Count);
        var rows = sink.Rows.ToArray();
        Assert.AreEqual(2, rows.Length);
        foreach (var row in rows)
        {
            Assert.AreEqual(RuntimeCandidateOutcome.Dropped, row.Outcome);
            Assert.AreEqual(0, row.IncludedTokens);
            Assert.AreEqual(0.0, row.TruncationRatio);
            Assert.IsFalse(row.IncludedInPackage);
            Assert.IsFalse(row.SelectedByScoring);
        }
    }

    /// <summary>
    /// P0-6.3: 被其他 section 已选入的重复候选在本 section 应标记为 Rejected（section-level attribution），
    /// 且 IncludedTokens=0。验证 globalSelectedIds 路径的 outcome 分配。
    /// </summary>
    [TestMethod]
    public void TraceRow_DuplicateSectionCandidate_MarkedRejectedWithZeroIncludedTokens()
    {
        var sink = new CapturingTraceSink();
        var recorder = CreateRecorder(sink);
        var candidates = new[] { MakeCandidate("dup-1", 12) };
        var sectionResult = SectionPackingResult.Selected(
            reason: "selected for package section",
            actualTokens: 12,
            truncated: false,
            acceptedCandidateIds: new[] { "dup-1" },
            rejectedCandidateIds: Array.Empty<string>());

        var selectedItems = new List<ContextPackageDecision>();
        var droppedItems = new List<DroppedContextItem>();
        var globalSelectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dup-1" };
        var primaryDecisions = new Dictionary<string, ContextPackageDecision>
        {
            ["dup-1"] = new ContextPackageDecision { ItemId = "dup-1", SectionName = "stable_memory" }
        };
        var itemReferences = new List<ContextPackageItemReference>();

        recorder.AddSectionDecisionsWithDedup(
            selectedItems, droppedItems, candidates, "working_memory", sectionResult,
            globalSelectedIds, primaryDecisions, itemReferences);

        Assert.AreEqual(0, selectedItems.Count, "重复候选不应再产生 selected decision");
        Assert.AreEqual(0, droppedItems.Count, "重复候选不应进入 droppedItems（已由其他 section 负责）");
        Assert.AreEqual(1, itemReferences.Count, "应记录 section-level attribution 引用");

        var row = sink.Rows.Single();
        Assert.AreEqual(RuntimeCandidateOutcome.Rejected, row.Outcome);
        Assert.AreEqual(0, row.IncludedTokens);
        Assert.AreEqual(12, row.OriginalTokens);
        Assert.AreEqual(0.0, row.TruncationRatio);
        Assert.IsFalse(row.IncludedInPackage);
        Assert.AreEqual("referenced by duplicate section", row.DroppedReason);
    }

    /// <summary>
    /// P0-6.3: ToJsonLine 应包含 outcome/originalTokens/includedTokens/truncationRatio 四个新字段。
    /// 验证 trace 行 JSON 序列化对下游诊断消费者可见。
    /// </summary>
    [TestMethod]
    public void TraceRow_ToJsonLine_IncludesOutcomeAndTokenFields()
    {
        var row = new RuntimeCandidateTraceRow
        {
            OperationId = "op-1",
            RequestId = "req-1",
            CandidateId = "c-1",
            Section = "working_memory",
            Outcome = RuntimeCandidateOutcome.PartiallyAccepted,
            OriginalTokens = 20,
            IncludedTokens = 7,
            TruncationRatio = 0.35
        };
        var json = row.ToJsonLine();

        StringAssert.Contains(json, "\"outcome\":\"PartiallyAccepted\"");
        StringAssert.Contains(json, "\"originalTokens\":20");
        StringAssert.Contains(json, "\"includedTokens\":7");
        StringAssert.Contains(json, "\"truncationRatio\":0.35");

        // 验证 JSON 可被反序列化且字段值正确
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.AreEqual("PartiallyAccepted", root.GetProperty("outcome").GetString());
        Assert.AreEqual(20, root.GetProperty("originalTokens").GetInt32());
        Assert.AreEqual(7, root.GetProperty("includedTokens").GetInt32());
        Assert.AreEqual(0.35, root.GetProperty("truncationRatio").GetDouble(), 0.0001);
    }

    /// <summary>
    /// P0-6.3: OriginalTokens=0 时 TruncationRatio 应为 0（不除零）。
    /// 验证 WriteTraceRow 内部的 TruncationRatio 计算对零原始 token 安全。
    /// </summary>
    [TestMethod]
    public void TraceRow_ZeroOriginalTokens_TruncationRatioIsZeroNotNaN()
    {
        var sink = new CapturingTraceSink();
        var recorder = CreateRecorder(sink);
        // 构造 EstimatedTokens=0 的候选（如非审计模式下历史记忆路径）
        var candidates = new[] { MakeCandidate("zero-tok", 0) };
        var sectionResult = SectionPackingResult.Selected(
            reason: "selected for package section",
            actualTokens: 0,
            truncated: false,
            acceptedCandidateIds: new[] { "zero-tok" },
            rejectedCandidateIds: Array.Empty<string>());

        InvokeAddSectionDecisions(recorder, candidates, sectionResult, out _, out _);

        var row = sink.Rows.Single();
        Assert.AreEqual(RuntimeCandidateOutcome.Accepted, row.Outcome);
        Assert.AreEqual(0, row.OriginalTokens);
        Assert.AreEqual(0, row.IncludedTokens);
        Assert.AreEqual(0.0, row.TruncationRatio, "OriginalTokens=0 时 TruncationRatio 应为 0（不除零/NaN）");
    }
}
