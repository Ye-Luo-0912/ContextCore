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

                    WriteTraceRow(candidate, sectionName, false, "referenced by duplicate section", selectedByScoring: true);
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
                    WriteTraceRow(candidate, sectionName, true, sectionResult.Reason);
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
                    WriteTraceRow(candidate, sectionName, false, dropReason, selectedByScoring: true);
                }
            }
        }
        else
        {
            foreach (var candidate in candidates)
            {
                droppedItems.Add(CreateDropped(candidate, sectionResult.Reason));
                WriteTraceRow(candidate, sectionName, false, sectionResult.Reason, selectedByScoring: false);
            }
        }
    }

    internal void WriteTraceRow(PackageTraceCandidate c, string section, bool included, string reason,
        bool selectedByScoring = true)
    {
        if (!_traceSink.Enabled) return;
        RuntimeCandidateTraceRow? row = null;
        try
        {
            var kind = c.Kind;
            var (srcType, auth, stratType, chan) = MapTraceFields(kind, section, c);
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
                TraceSource = (byte)3, // PackageTrace
                DeterministicScore = c.Score,
                StrategyScore = c.Score,
                FinalScore = c.Score,
                SelectedByScoring = selectedByScoring,
                IncludedInPackage = included,
                DroppedReason = included ? "" : reason,
                TokenCost = c.EstimatedTokens,
                Section = section
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

    private static (byte sourceType, byte authority, byte strategyType, byte retrievalChannel) MapTraceFields(
        string kind, string section, PackageTraceCandidate c)
    {
        var kindLower = kind?.ToLowerInvariant() ?? section?.ToLowerInvariant() ?? "";
        var sectionLower = section?.ToLowerInvariant() ?? "";

        byte sourceType = kindLower switch
        {
            "raw" or "legacy" => 1,
            "current_task" => 6,
            "hard_constraint" or "soft_constraint" or "merged_constraint" => 3,
            "working_memory" or "stable_memory" or "historical_context" => 2,
            "global_context" => 4,
            "recent_context" => 5,
            "related_context" => 7,
            _ => 1
        };

        byte authority = kindLower switch
        {
            "raw" or "legacy" or "recent_context" => 2,
            "current_task" => 5,
            "hard_constraint" or "soft_constraint" or "merged_constraint" or "constraints" => 1,
            "working_memory" => 5,
            "stable_memory" => 1,
            "global_context" => 1,
            "related_context" => 4,
            "historical_context" => 3,
            _ => 1
        };

        byte strategyType = kindLower switch
        {
            "current_task" => 4,
            "hard_constraint" or "soft_constraint" or "merged_constraint" or "constraints" => 3,
            "working_memory" or "recent_context" => 1,
            "stable_memory" => 2,
            "global_context" => 2,
            "related_context" => 5,
            "raw" or "legacy" => 1,
            _ => 1
        };

        byte retrievalChannel = sectionLower switch
        {
            "raw" or "legacy" => sectionLower == "legacy" ? (byte)4 : (byte)4,
            "current_task" => (byte)5,
            "hard_constraints" or "soft_constraints" or "constraints" => kindLower.Contains("constraint") ? (byte)6 : (byte)2,
            "working_memory" or "stable_memory" or "global_context" or "historical_context" => (byte)2,
            "recent_context" => (byte)4,
            "related_context" => (byte)3,
            _ => (byte)2
        };
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
