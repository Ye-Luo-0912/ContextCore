using System.Collections.Concurrent;
using System.Text.Json;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.Learning.V14_0;

namespace ContextCore.Tests;

/// <summary>
/// 验收测试：验证 PackageTraceRecorder.MapTraceFields 不再使用 magic byte + 字符串 switch，
/// 改为枚举化映射。验收：
/// - 已知 kind → 正确枚举值（与历史 byte 输出兼容）
/// - 未匹配 kind → 显式 Unknown(0)（不再静默默认 Raw(1)）
/// - JSON 序列化输出数值（与历史 byte 兼容）
/// - 新增 section/kind 不会静默落入错误默认值
/// </summary>
[TestClass]
[TestCategory("Contract")]
public sealed class ContextCoreTraceSchemaEnumTests
{
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
        => new(sink, () => "op-enum-test", () => "req-enum-test");

    private static PackageTraceCandidate MakeCandidate(string id, string kind)
    {
        // PackageTraceCandidate.FromMemory 接受 (ContextMemoryItem item, string kind, double score, int? estimatedTokens)
        var item = new ContextMemoryItem
        {
            Id = id,
            WorkspaceId = "ws",
            CollectionId = "col",
            Type = kind,
            Content = "test content"
        };
        return PackageTraceCandidate.FromMemory(item, kind: kind, score: 1.0, estimatedTokens: 10);
    }

    private static void InvokeAddSectionDecisions(
        PackageTraceRecorder recorder,
        IReadOnlyList<PackageTraceCandidate> candidates,
        string sectionName,
        out System.Collections.Generic.List<ContextPackageDecision> selectedItems,
        out System.Collections.Generic.List<DroppedContextItem> droppedItems)
    {
        selectedItems = new();
        droppedItems = new();
        var sectionResult = SectionPackingResult.Selected(
            reason: "selected",
            actualTokens: 10,
            truncated: false,
            acceptedCandidateIds: candidates.Select(c => c.Id).ToArray(),
            rejectedCandidateIds: System.Array.Empty<string>());

        recorder.AddSectionDecisionsWithDedup(
            selectedItems: selectedItems,
            droppedItems: droppedItems,
            candidates: candidates,
            sectionName: sectionName,
            sectionResult: sectionResult,
            globalSelectedIds: new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase),
            primaryDecisions: new System.Collections.Generic.Dictionary<string, ContextPackageDecision>(StringComparer.OrdinalIgnoreCase),
            itemReferences: new System.Collections.Generic.List<ContextPackageItemReference>());
    }

    /// <summary> 枚举类型存在且为 : byte（保证 JSON 序列化兼容）。</summary>
    [TestMethod]
    public void TraceSchema_Enums_HaveByteUnderlyingType()
    {
        Assert.AreEqual(typeof(byte), Enum.GetUnderlyingType(typeof(RuntimeCandidateSourceType)));
        Assert.AreEqual(typeof(byte), Enum.GetUnderlyingType(typeof(CandidateAuthorityLevel)));
        Assert.AreEqual(typeof(byte), Enum.GetUnderlyingType(typeof(CandidateStrategyType)));
        Assert.AreEqual(typeof(byte), Enum.GetUnderlyingType(typeof(RuntimeCandidateRetrievalChannel)));
    }

    /// <summary> RuntimeCandidateSourceType 枚举值与历史 byte 数值对齐。</summary>
    [TestMethod]
    public void TraceSchema_SourceType_ValuesMatchHistoricBytes()
    {
        Assert.AreEqual(0, (byte)RuntimeCandidateSourceType.Unknown);
        Assert.AreEqual(1, (byte)RuntimeCandidateSourceType.Raw);
        Assert.AreEqual(2, (byte)RuntimeCandidateSourceType.Memory);
        Assert.AreEqual(3, (byte)RuntimeCandidateSourceType.Constraint);
        Assert.AreEqual(4, (byte)RuntimeCandidateSourceType.GlobalContext);
        Assert.AreEqual(5, (byte)RuntimeCandidateSourceType.RecentContext);
        Assert.AreEqual(6, (byte)RuntimeCandidateSourceType.CurrentTask);
        Assert.AreEqual(7, (byte)RuntimeCandidateSourceType.RelatedContext);
    }

    /// <summary> CandidateAuthorityLevel 枚举值与历史 byte 数值对齐。</summary>
    [TestMethod]
    public void TraceSchema_Authority_ValuesMatchHistoricBytes()
    {
        Assert.AreEqual(0, (byte)CandidateAuthorityLevel.Unknown);
        Assert.AreEqual(1, (byte)CandidateAuthorityLevel.HardRequirement);
        Assert.AreEqual(2, (byte)CandidateAuthorityLevel.UserAttached);
        Assert.AreEqual(3, (byte)CandidateAuthorityLevel.Reference);
        Assert.AreEqual(4, (byte)CandidateAuthorityLevel.Inferred);
        Assert.AreEqual(5, (byte)CandidateAuthorityLevel.Authoritative);
    }

    /// <summary> CandidateStrategyType 枚举值与历史 byte 数值对齐。</summary>
    [TestMethod]
    public void TraceSchema_StrategyType_ValuesMatchHistoricBytes()
    {
        Assert.AreEqual(0, (byte)CandidateStrategyType.Unknown);
        Assert.AreEqual(1, (byte)CandidateStrategyType.Recent);
        Assert.AreEqual(2, (byte)CandidateStrategyType.Stable);
        Assert.AreEqual(3, (byte)CandidateStrategyType.Constraint);
        Assert.AreEqual(4, (byte)CandidateStrategyType.Current);
        Assert.AreEqual(5, (byte)CandidateStrategyType.Related);
    }

    /// <summary> 已知 kind "current_task" → SourceType=CurrentTask / Authority=Authoritative / StrategyType=Current。</summary>
    [TestMethod]
    public void TraceSchema_KnownKind_CurrentTask_MapsToExpectedEnums()
    {
        var sink = new CapturingTraceSink();
        var recorder = CreateRecorder(sink);
        var candidates = new[] { MakeCandidate("ct-1", "current_task") };

        InvokeAddSectionDecisions(recorder, candidates, "current_task", out _, out _);

        var row = sink.Rows.Single();
        Assert.AreEqual(RuntimeCandidateSourceType.CurrentTask, row.SourceType);
        Assert.AreEqual(CandidateAuthorityLevel.Authoritative, row.Authority);
        Assert.AreEqual(CandidateStrategyType.Current, row.StrategyType);
        Assert.AreEqual(RuntimeCandidateRetrievalChannel.Anchor, row.RetrievalChannel);
    }

    /// <summary> 已知 kind "hard_constraint" → SourceType=Constraint / Authority=HardRequirement / StrategyType=Constraint。</summary>
    [TestMethod]
    public void TraceSchema_KnownKind_HardConstraint_MapsToExpectedEnums()
    {
        var sink = new CapturingTraceSink();
        var recorder = CreateRecorder(sink);
        var candidates = new[] { MakeCandidate("hc-1", "hard_constraint") };

        InvokeAddSectionDecisions(recorder, candidates, "hard_constraints", out _, out _);

        var row = sink.Rows.Single();
        Assert.AreEqual(RuntimeCandidateSourceType.Constraint, row.SourceType);
        Assert.AreEqual(CandidateAuthorityLevel.HardRequirement, row.Authority);
        Assert.AreEqual(CandidateStrategyType.Constraint, row.StrategyType);
        Assert.AreEqual(RuntimeCandidateRetrievalChannel.Constraint, row.RetrievalChannel);
    }

    /// <summary> 已知 kind "working_memory" → SourceType=Memory / Authority=Authoritative / StrategyType=Recent。</summary>
    [TestMethod]
    public void TraceSchema_KnownKind_WorkingMemory_MapsToExpectedEnums()
    {
        var sink = new CapturingTraceSink();
        var recorder = CreateRecorder(sink);
        var candidates = new[] { MakeCandidate("wm-1", "working_memory") };

        InvokeAddSectionDecisions(recorder, candidates, "working_memory", out _, out _);

        var row = sink.Rows.Single();
        Assert.AreEqual(RuntimeCandidateSourceType.Memory, row.SourceType);
        Assert.AreEqual(CandidateAuthorityLevel.Authoritative, row.Authority);
        Assert.AreEqual(CandidateStrategyType.Recent, row.StrategyType);
        Assert.AreEqual(RuntimeCandidateRetrievalChannel.Memory, row.RetrievalChannel);
    }

    /// <summary> 已知 kind "related_context" → SourceType=RelatedContext / Authority=Inferred / StrategyType=Related / Channel=Graph。</summary>
    [TestMethod]
    public void TraceSchema_KnownKind_RelatedContext_MapsToExpectedEnums()
    {
        var sink = new CapturingTraceSink();
        var recorder = CreateRecorder(sink);
        var candidates = new[] { MakeCandidate("rc-1", "related_context") };

        InvokeAddSectionDecisions(recorder, candidates, "related_context", out _, out _);

        var row = sink.Rows.Single();
        Assert.AreEqual(RuntimeCandidateSourceType.RelatedContext, row.SourceType);
        Assert.AreEqual(CandidateAuthorityLevel.Inferred, row.Authority);
        Assert.AreEqual(CandidateStrategyType.Related, row.StrategyType);
        Assert.AreEqual(RuntimeCandidateRetrievalChannel.Graph, row.RetrievalChannel);
    }

    /// <summary> 未匹配 kind "future_unknown_kind" → 显式 Unknown(0)，不再静默默认 Raw(1)。</summary>
    [TestMethod]
    public void TraceSchema_UnknownKind_ReturnsUnknownEnums_NotSilentDefault()
    {
        var sink = new CapturingTraceSink();
        var recorder = CreateRecorder(sink);
        var candidates = new[] { MakeCandidate("uk-1", "future_unknown_kind") };

        InvokeAddSectionDecisions(recorder, candidates, "future_unknown_section", out _, out _);

        var row = sink.Rows.Single();
        Assert.AreEqual(RuntimeCandidateSourceType.Unknown, row.SourceType,
            "未匹配 kind 必须显式返回 Unknown(0)，而非静默默认 Raw(1)");
        Assert.AreEqual(CandidateAuthorityLevel.Unknown, row.Authority,
            "未匹配 kind 必须显式返回 Unknown(0)，而非静默默认 HardRequirement(1)");
        Assert.AreEqual(CandidateStrategyType.Unknown, row.StrategyType,
            "未匹配 kind 必须显式返回 Unknown(0)，而非静默默认 Recent(1)");
        // RetrievalChannel 默认仍是 Memory(2) — 与历史 byte 默认 _=>2 保持一致
        Assert.AreEqual(RuntimeCandidateRetrievalChannel.Memory, row.RetrievalChannel);
    }

    /// <summary> TraceSource 枚举 PackageTrace=3（替代 magic byte (byte)3）。</summary>
    [TestMethod]
    public void TraceSchema_TraceSource_PackageTraceValueIsThree()
    {
        var sink = new CapturingTraceSink();
        var recorder = CreateRecorder(sink);
        var candidates = new[] { MakeCandidate("ts-1", "working_memory") };

        InvokeAddSectionDecisions(recorder, candidates, "working_memory", out _, out _);

        var row = sink.Rows.Single();
        Assert.AreEqual(RuntimeCandidateTraceSource.PackageTrace, row.TraceSource);
        Assert.AreEqual(3, (byte)row.TraceSource);
    }

    /// <summary> ToJsonLine 仍输出数值（与历史 byte 序列化兼容）。</summary>
    [TestMethod]
    public void TraceSchema_ToJsonLine_SerializesAsNumericByteValues()
    {
        var row = new RuntimeCandidateTraceRow
        {
            OperationId = "op-1",
            RequestId = "req-1",
            CandidateId = "c-1",
            SourceType = RuntimeCandidateSourceType.CurrentTask,
            Authority = CandidateAuthorityLevel.Authoritative,
            StrategyType = CandidateStrategyType.Current,
            RetrievalChannel = RuntimeCandidateRetrievalChannel.Anchor,
            TraceSource = RuntimeCandidateTraceSource.PackageTrace
        };
        var json = row.ToJsonLine();

        // 反序列化为 JsonElement 检查数值类型与值
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.AreEqual(JsonValueKind.Number, root.GetProperty("sourceType").ValueKind);
        Assert.AreEqual(6, root.GetProperty("sourceType").GetInt32());
        Assert.AreEqual(5, root.GetProperty("authority").GetInt32());
        Assert.AreEqual(4, root.GetProperty("strategyType").GetInt32());
        Assert.AreEqual(5, root.GetProperty("retrievalChannel").GetInt32());
        Assert.AreEqual(3, root.GetProperty("traceSource").GetInt32());
    }
}
