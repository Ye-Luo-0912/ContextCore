using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Learning.V14_0;

namespace ContextCore.Core;

internal sealed class PackageTraceRecorder
{
    private readonly IRuntimeCandidateTraceSink _traceSink;
    private readonly Func<string?> _getOperationId;
    private readonly Func<string?> _getRequestId;
    private int _traceMapFailures;
    private int _traceSinkWriteFailures;
    private DateTimeOffset _lastFailureAt;
    private string? _lastFailureCategory;

    public int TraceMapFailures => _traceMapFailures;
    public int TraceSinkWriteFailures => _traceSinkWriteFailures;
    public int TraceWriteFailures => _traceMapFailures + _traceSinkWriteFailures;

    /// <summary>最近一次 trace 写入失败时间；无失败则为 null。</summary>
    public DateTimeOffset? LastFailureAt =>
        (_traceMapFailures + _traceSinkWriteFailures) > 0 ? _lastFailureAt : null;

    /// <summary>最近一次 trace 写入失败的异常类别（Type.Name）；无失败则为 null。</summary>
    public string? LastFailureCategory => _lastFailureCategory;

    /// <summary>trace sink 类型名（用于诊断报告）。</summary>
    public string? SinkType => _traceSink?.GetType().FullName;

    public PackageTraceRecorder(
        IRuntimeCandidateTraceSink traceSink,
        Func<string?> getOperationId,
        Func<string?> getRequestId)
    {
        _traceSink = traceSink;
        _getOperationId = getOperationId;
        _getRequestId = getRequestId;
    }

    /// <summary>
    /// OPT-1: kind → SourceType 枚举映射。替代原 magic byte switch。
    /// 未匹配 kind 显式返回 Unknown(0) 而非静默默认 Raw(1)，便于下游检测 schema 演进缺口。
    /// </summary>
    private static RuntimeCandidateSourceType MapSourceType(string kindLower) => kindLower switch
    {
        "raw" or "legacy" => RuntimeCandidateSourceType.Raw,
        "current_task" => RuntimeCandidateSourceType.CurrentTask,
        "hard_constraint" or "soft_constraint" or "merged_constraint" => RuntimeCandidateSourceType.Constraint,
        "working_memory" or "stable_memory" or "historical_context" => RuntimeCandidateSourceType.Memory,
        "global_context" => RuntimeCandidateSourceType.GlobalContext,
        "recent_context" => RuntimeCandidateSourceType.RecentContext,
        "related_context" => RuntimeCandidateSourceType.RelatedContext,
        _ => RuntimeCandidateSourceType.Unknown
    };

    /// <summary>OPT-1: kind → AuthorityLevel 枚举映射。</summary>
    private static CandidateAuthorityLevel MapAuthority(string kindLower) => kindLower switch
    {
        "raw" or "legacy" or "recent_context" => CandidateAuthorityLevel.UserAttached,
        "current_task" => CandidateAuthorityLevel.Authoritative,
        "hard_constraint" or "soft_constraint" or "merged_constraint" or "constraints" => CandidateAuthorityLevel.HardRequirement,
        "working_memory" => CandidateAuthorityLevel.Authoritative,
        "stable_memory" => CandidateAuthorityLevel.HardRequirement,
        "global_context" => CandidateAuthorityLevel.HardRequirement,
        "related_context" => CandidateAuthorityLevel.Inferred,
        "historical_context" => CandidateAuthorityLevel.Reference,
        _ => CandidateAuthorityLevel.Unknown
    };

    /// <summary>OPT-1: kind → StrategyType 枚举映射。</summary>
    private static CandidateStrategyType MapStrategyType(string kindLower) => kindLower switch
    {
        "current_task" => CandidateStrategyType.Current,
        "hard_constraint" or "soft_constraint" or "merged_constraint" or "constraints" => CandidateStrategyType.Constraint,
        "working_memory" or "recent_context" => CandidateStrategyType.Recent,
        "stable_memory" => CandidateStrategyType.Stable,
        "global_context" => CandidateStrategyType.Stable,
        "related_context" => CandidateStrategyType.Related,
        "raw" or "legacy" => CandidateStrategyType.Recent,
        _ => CandidateStrategyType.Unknown
    };

    /// <summary>
    /// OPT-1: (section, kind) → RetrievalChannel 枚举映射。
    /// 注意：原 byte 映射中 constraints section 的非 constraint kind 会回退到 Memory；此处保留相同语义。
    /// </summary>
    private static RuntimeCandidateRetrievalChannel MapRetrievalChannel(string sectionLower, string kindLower) => sectionLower switch
    {
        "raw" or "legacy" => RuntimeCandidateRetrievalChannel.Keyword,
        "current_task" => RuntimeCandidateRetrievalChannel.Anchor,
        "hard_constraints" or "soft_constraints" or "constraints" =>
            kindLower.Contains("constraint")
                ? RuntimeCandidateRetrievalChannel.Constraint
                : RuntimeCandidateRetrievalChannel.Memory,
        "working_memory" or "stable_memory" or "global_context" or "historical_context" => RuntimeCandidateRetrievalChannel.Memory,
        "recent_context" => RuntimeCandidateRetrievalChannel.Keyword,
        "related_context" => RuntimeCandidateRetrievalChannel.Graph,
        _ => RuntimeCandidateRetrievalChannel.Memory
    };

    internal void AddSectionDecisionsWithDedup(
        ICollection<ContextPackageDecision> selectedItems,
        ICollection<DroppedContextItem> droppedItems,
        IReadOnlyList<PackageTraceCandidate> candidates,
        string sectionName,
        SectionPackingResult sectionResult,
        HashSet<string> globalSelectedIds,
        Dictionary<string, ContextPackageDecision> primaryDecisions,
        ICollection<ContextPackageItemReference> itemReferences)
    {
        if (candidates.Count == 0)
        {
            return;
        }

        if (sectionResult.Added)
        {
            // 使用 SectionPackingResult 的精确候选归属：
            // - AcceptedCandidateIds：完整保留进 section 输出的候选
            // - PartiallyAcceptedCandidateId：因 token 预算截断仅部分保留的候选
            // - 其余候选：未保留，按 Truncated 标志选择 drop reason
            // 替代旧的"只保留首个新候选"启发式与基于字符串前缀的猜测。
            var fullyAcceptedIds = new HashSet<string>(
                sectionResult.AcceptedCandidateIds,
                StringComparer.OrdinalIgnoreCase);
            var partiallyAcceptedId = sectionResult.PartiallyAcceptedCandidateId;

            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (globalSelectedIds.Contains(candidate.Id))
                {
                    // 候选已被其他 section 选入：记录 section-level attribution 到独立集合，
                    // 不再添加第二条 selected decision，也不再污染 primary decision.Metadata（7.1）。
                    if (primaryDecisions.TryGetValue(candidate.Id, out var primaryDecision))
                    {
                        itemReferences.Add(new ContextPackageItemReference
                        {
                            ItemId = candidate.Id,
                            PrimarySectionName = primaryDecision.SectionName,
                            ReferencingSectionName = sectionName,
                            Reason = "referenced by duplicate section"
                        });
                    }

                    WriteTraceRow(candidate, sectionName, RuntimeCandidateOutcome.Rejected,
                        includedTokens: 0, originalTokens: candidate.EstimatedTokens,
                        reason: "referenced by duplicate section", selectedByScoring: true);
                    continue;
                }

                var isFullyAccepted = fullyAcceptedIds.Contains(candidate.Id);
                var isPartiallyAccepted = partiallyAcceptedId is not null
                    && string.Equals(candidate.Id, partiallyAcceptedId, StringComparison.OrdinalIgnoreCase);

                if (isFullyAccepted || isPartiallyAccepted)
                {
                    var decision = CreateDecision(
                        candidate,
                        sectionName,
                        sectionResult.Reason,
                        candidate.EstimatedTokens);
                    // 精确区分 Accepted/PartiallyAccepted 并填充 IncludedTokens/OriginalTokens/TruncationRatio。
                    // - Accepted: IncludedTokens = OriginalTokens = candidate.EstimatedTokens（无截断，ratio=1.0）
                    // - PartiallyAccepted: IncludedTokens = sectionResult.PartiallyAcceptedIncludedTokens（截断后保留量）
                    if (isPartiallyAccepted)
                    {
                        WriteTraceRow(candidate, sectionName, RuntimeCandidateOutcome.PartiallyAccepted,
                            includedTokens: sectionResult.PartiallyAcceptedIncludedTokens,
                            originalTokens: candidate.EstimatedTokens,
                            reason: sectionResult.Reason);
                    }
                    else
                    {
                        WriteTraceRow(candidate, sectionName, RuntimeCandidateOutcome.Accepted,
                            includedTokens: candidate.EstimatedTokens,
                            originalTokens: candidate.EstimatedTokens,
                            reason: sectionResult.Reason);
                    }
                    selectedItems.Add(decision);
                    globalSelectedIds.Add(candidate.Id);
                    primaryDecisions[candidate.Id] = decision;
                }
                else
                {
                    var dropReason = sectionResult.Truncated
                        ? "candidate not retained after token budget truncation"
                        : "content not retained in section output";
                    droppedItems.Add(CreateDropped(candidate, dropReason));
                    WriteTraceRow(candidate, sectionName, RuntimeCandidateOutcome.Rejected,
                        includedTokens: 0, originalTokens: candidate.EstimatedTokens,
                        reason: dropReason, selectedByScoring: true);
                }
            }
        }
        else
        {
            foreach (var candidate in candidates)
            {
                droppedItems.Add(CreateDropped(candidate, sectionResult.Reason));
                WriteTraceRow(candidate, sectionName, RuntimeCandidateOutcome.Dropped,
                    includedTokens: 0, originalTokens: candidate.EstimatedTokens,
                    reason: sectionResult.Reason, selectedByScoring: false);
            }
        }
    }

    /// <summary>
    /// 写入单条候选 trace 行。P0-6.3: 以 RuntimeCandidateOutcome + IncludedTokens/OriginalTokens 替代 bool included，
    /// 使下游诊断能区分 Accepted/PartiallyAccepted/Rejected/Dropped 并观察截断比率。
    /// </summary>
    /// <param name="c">候选。</param>
    /// <param name="section">section 名。</param>
    /// <param name="outcome">候选归属结果（Accepted/PartiallyAccepted/Rejected/Dropped）。</param>
    /// <param name="includedTokens">候选实际保留进 section 输出的 token 数（截断后）。</param>
    /// <param name="originalTokens">候选原始估算 token 数（截断前）。</param>
    /// <param name="reason">drop reason（included 时忽略）。</param>
    /// <param name="selectedByScoring">是否经评分选择。</param>
    internal void WriteTraceRow(
        PackageTraceCandidate c,
        string section,
        RuntimeCandidateOutcome outcome,
        int includedTokens,
        int originalTokens,
        string reason,
        bool selectedByScoring = true)
    {
        if (!_traceSink.Enabled) return;
        RuntimeCandidateTraceRow? row = null;
        try
        {
            var kind = c.Kind;
            var (srcType, auth, stratType, chan) = MapTraceFields(kind, section, c);
            var included = outcome == RuntimeCandidateOutcome.Accepted
                || outcome == RuntimeCandidateOutcome.PartiallyAccepted;
            var ratio = originalTokens > 0
                ? includedTokens / (double)originalTokens
                : 0.0;
            row = new RuntimeCandidateTraceRow
            {
                OperationId = _getOperationId() ?? "unknown",
                RequestId = _getRequestId() ?? "unknown",
                CandidateId = c.Id,
                SourceId = c.Id,
                SourceType = srcType,
                Authority = auth,
                StrategyType = stratType,
                RetrievalChannel = chan,
                TraceSource = RuntimeCandidateTraceSource.PackageTrace,
                DeterministicScore = c.Score,
                StrategyScore = c.Score,
                FinalScore = c.Score,
                SelectedByScoring = selectedByScoring,
                IncludedInPackage = included,
                DroppedReason = included ? "" : reason,
                TokenCost = c.EstimatedTokens,
                Section = section,
                Outcome = outcome,
                OriginalTokens = originalTokens,
                IncludedTokens = includedTokens,
                TruncationRatio = ratio
            };
        }
        catch (Exception ex)
        {
            /* field mapping failure must not affect main flow */
            Interlocked.Increment(ref _traceMapFailures);
            _lastFailureAt = DateTimeOffset.UtcNow;
            _lastFailureCategory = ex.GetType().Name;
            return;
        }

        try
        {
            _traceSink.Write(row);
        }
        catch (Exception ex)
        {
            /* sink write failure must not affect main flow */
            Interlocked.Increment(ref _traceSinkWriteFailures);
            _lastFailureAt = DateTimeOffset.UtcNow;
            _lastFailureCategory = ex.GetType().Name;
        }
    }

    /// <summary>
    /// OPT-1: 聚合 (SourceType, Authority, StrategyType, RetrievalChannel) 枚举映射。
    /// 替代原返回 byte tuple 的实现。未匹配 kind/section 显式落入 Unknown(0)，
    /// 下游消费者可检测 0 值识别 schema 演进缺口，而非静默使用错误的默认值。
    /// </summary>
    private static (RuntimeCandidateSourceType sourceType, CandidateAuthorityLevel authority, CandidateStrategyType strategyType, RuntimeCandidateRetrievalChannel retrievalChannel) MapTraceFields(
        string kind, string section, PackageTraceCandidate c)
    {
        var kindLower = kind?.ToLowerInvariant() ?? section?.ToLowerInvariant() ?? "";
        var sectionLower = section?.ToLowerInvariant() ?? "";

        var sourceType = MapSourceType(kindLower);
        var authority = MapAuthority(kindLower);
        var strategyType = MapStrategyType(kindLower);
        var retrievalChannel = MapRetrievalChannel(sectionLower, kindLower);
        return (sourceType, authority, strategyType, retrievalChannel);
    }

    internal static ContextPackageDecision CreateDecision(
        PackageTraceCandidate candidate,
        string sectionName,
        string reason,
        int estimatedTokens)
    {
        return new ContextPackageDecision
        {
            ItemId = candidate.Id,
            Kind = candidate.Kind,
            Type = candidate.Type,
            SectionName = sectionName,
            Reason = reason,
            Score = candidate.Score,
            EstimatedTokens = estimatedTokens,
            SourceRefs = candidate.SourceRefs,
            Metadata = new Dictionary<string, string>(candidate.Metadata),
            ScoreBreakdown = candidate.ScoreBreakdown
        };
    }

    internal static DroppedContextItem CreateDropped(
        PackageTraceCandidate candidate,
        string reason)
    {
        return new DroppedContextItem
        {
            ItemId = candidate.Id,
            Kind = candidate.Kind,
            Type = candidate.Type,
            Reason = reason,
            Score = candidate.Score,
            EstimatedTokens = candidate.EstimatedTokens,
            SourceRefs = candidate.SourceRefs,
            Metadata = new Dictionary<string, string>(candidate.Metadata)
        };
    }
}
