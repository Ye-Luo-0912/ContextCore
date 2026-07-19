using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 生产实现：<see cref="IDecisionEvidenceProvider"/>。
/// 通过 <see cref="IRetrievalTraceStore"/> / <see cref="IContextPackageBuildTraceStore"/> 中的 trace artifact
/// 为 <see cref="ContextDecisionRecord"/> 解析结构化证据。
/// </summary>
/// <remarks>
/// <para>该实现替代已删除的 NullDecisionEvidenceProvider：未接入证据提供者时审计报告标记 NotConfigured，
/// 接入但 trace 缺失时标记 Incomplete，所有候选都有对应 trace 时标记 Complete，trace store 抛异常时标记 Failed。</para>
/// <para>本实现只读 trace artifact，不触发任何运行时变更。两个 store 都可以为 null（测试场景或部分 provider
/// 不支持该 store 时，对应路径返回 Incomplete）。</para>
/// </remarks>
public sealed class TraceBackedDecisionEvidenceProvider : IDecisionEvidenceProvider
{
    private readonly IRetrievalTraceStore? _retrievalTraceStore;
    private readonly IContextPackageBuildTraceStore? _packageBuildTraceStore;
    private readonly int _lookupTake;

    /// <summary>
    /// 构造 trace-backed 证据提供者。
    /// </summary>
    /// <param name="retrievalTraceStore">检索 trace store（可空）。Package 决策不依赖此 store。</param>
    /// <param name="packageBuildTraceStore">包构建 trace store（可空）。Retrieval 决策不依赖此 store。</param>
    /// <param name="lookupTake">从 trace store 查询最近 N 条记录用于匹配 DecisionId，默认 100。</param>
    public TraceBackedDecisionEvidenceProvider(
        IRetrievalTraceStore? retrievalTraceStore = null,
        IContextPackageBuildTraceStore? packageBuildTraceStore = null,
        int lookupTake = 100)
    {
        if (lookupTake <= 0) throw new ArgumentOutOfRangeException(nameof(lookupTake), lookupTake, "lookupTake must be positive");
        _retrievalTraceStore = retrievalTraceStore;
        _packageBuildTraceStore = packageBuildTraceStore;
        _lookupTake = lookupTake;
    }

    /// <inheritdoc/>
    public async Task<DecisionEvidenceResult> ResolveEvidenceAsync(
        ContextDecisionRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var decisionId = record.DecisionId;
        var evidence = new List<DecisionEvidence>(record.Candidates.Count);
        var missing = new List<string>();

        // 按 source 分派到对应 trace store。Retrieval 决策只查 IRetrievalTraceStore；
        // Package 决策只查 IContextPackageBuildTraceStore。
        ContextTraceMatch? match = record.Source switch
        {
            ContextDecisionSource.Retrieval when _retrievalTraceStore is not null
                => await MatchRetrievalTraceAsync(_retrievalTraceStore, record, cancellationToken).ConfigureAwait(false),
            ContextDecisionSource.Package when _packageBuildTraceStore is not null
                => await MatchPackageBuildTraceAsync(_packageBuildTraceStore, record, cancellationToken).ConfigureAwait(false),
            _ => null
        };

        // 收集每个候选的证据。matched 集合按 ItemId 索引，便于 O(1) 查找。
        var matchedEvidenceByItemId = match?.EvidenceByItemId ?? new Dictionary<string, DecisionEvidence>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in record.Candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.ItemId))
            {
                missing.Add(candidate.ItemId);
                continue;
            }

            if (matchedEvidenceByItemId.TryGetValue(candidate.ItemId, out var e))
            {
                evidence.Add(e);
            }
            else
            {
                missing.Add(candidate.ItemId);
            }
        }

        var isComplete = match is not null
            && match.EvidenceByItemId.Count > 0
            && missing.Count == 0
            && evidence.Count == record.Candidates.Count;

        return new DecisionEvidenceResult
        {
            DecisionId = decisionId,
            Evidence = evidence,
            IsComplete = isComplete,
            MissingItemIds = missing.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ResolvedAt = DateTimeOffset.UtcNow
        };
    }

    private async Task<ContextTraceMatch> MatchRetrievalTraceAsync(
        IRetrievalTraceStore store,
        ContextDecisionRecord record,
        CancellationToken cancellationToken)
    {
        var traces = await store.QueryRecentAsync(record.WorkspaceId, record.CollectionId, _lookupTake, cancellationToken)
            .ConfigureAwait(false);

        ContextRetrievalTrace? matched = null;
        foreach (var t in traces)
        {
            if (string.Equals(t.RetrievalId, record.DecisionId, StringComparison.OrdinalIgnoreCase))
            {
                matched = t;
                break;
            }
        }

        if (matched is null)
        {
            return new ContextTraceMatch(new Dictionary<string, DecisionEvidence>(StringComparer.OrdinalIgnoreCase));
        }

        var evidenceByItemId = new Dictionary<string, DecisionEvidence>(StringComparer.OrdinalIgnoreCase);

        foreach (var sel in matched.SelectedItems)
        {
            AddRetrievalEvidence(evidenceByItemId, sel, ContextDecisionCandidateOutcome.Selected, matched);
        }

        foreach (var drop in matched.DroppedItems)
        {
            AddRetrievalEvidence(evidenceByItemId, drop, ContextDecisionCandidateOutcome.Dropped, matched);
        }

        // 候选层面也补一条（如果 selected/dropped 没有覆盖到 trace.Candidates 里的项）
        foreach (var c in matched.Candidates)
        {
            var itemId = string.IsNullOrWhiteSpace(c.CandidateId) ? c.SourceId : c.CandidateId;
            if (string.IsNullOrWhiteSpace(itemId)) continue;
            if (evidenceByItemId.ContainsKey(itemId)) continue;

            evidenceByItemId[itemId] = new DecisionEvidence
            {
                ItemId = itemId,
                PrimaryRationale = "candidate-in-trace-only",
                Confidence = ComputeConfidence(c.Score),
                EvidenceRefs = BuildEvidenceRefs(matched.RetrievalId, c.SourceRefs),
                Provenance = "retrieval-trace-candidate"
            };
        }

        return new ContextTraceMatch(evidenceByItemId);
    }

    private async Task<ContextTraceMatch> MatchPackageBuildTraceAsync(
        IContextPackageBuildTraceStore store,
        ContextDecisionRecord record,
        CancellationToken cancellationToken)
    {
        var builds = await store.QueryRecentAsync(record.WorkspaceId, record.CollectionId, _lookupTake, cancellationToken)
            .ConfigureAwait(false);

        ContextPackageBuildResult? matched = null;
        foreach (var b in builds)
        {
            if (string.Equals(b.BuildId, record.DecisionId, StringComparison.OrdinalIgnoreCase))
            {
                matched = b;
                break;
            }
        }

        if (matched is null)
        {
            return new ContextTraceMatch(new Dictionary<string, DecisionEvidence>(StringComparer.OrdinalIgnoreCase));
        }

        var evidenceByItemId = new Dictionary<string, DecisionEvidence>(StringComparer.OrdinalIgnoreCase);

        foreach (var sel in matched.SelectedItems)
        {
            if (string.IsNullOrWhiteSpace(sel.ItemId)) continue;
            if (evidenceByItemId.ContainsKey(sel.ItemId)) continue;

            var uncertaintyCodes = MatchUncertainties(matched.Uncertainties, sel.ItemId);

            evidenceByItemId[sel.ItemId] = new DecisionEvidence
            {
                ItemId = sel.ItemId,
                PrimaryRationale = string.IsNullOrWhiteSpace(sel.Reason) ? "selected" : sel.Reason,
                SecondaryRationales = uncertaintyCodes,
                Confidence = ComputeConfidence(sel.Score),
                EvidenceRefs = BuildEvidenceRefs(matched.BuildId, sel.SourceRefs),
                Provenance = "package-build-trace-selected"
            };
        }

        foreach (var drop in matched.DroppedItems)
        {
            if (string.IsNullOrWhiteSpace(drop.ItemId)) continue;
            if (evidenceByItemId.ContainsKey(drop.ItemId)) continue;

            var uncertaintyCodes = MatchUncertainties(matched.Uncertainties, drop.ItemId);

            evidenceByItemId[drop.ItemId] = new DecisionEvidence
            {
                ItemId = drop.ItemId,
                PrimaryRationale = string.IsNullOrWhiteSpace(drop.Reason) ? "dropped" : drop.Reason,
                SecondaryRationales = uncertaintyCodes,
                Confidence = ComputeConfidence(drop.Score),
                EvidenceRefs = BuildEvidenceRefs(matched.BuildId, Array.Empty<string>()),
                Provenance = "package-build-trace-dropped"
            };
        }

        return new ContextTraceMatch(evidenceByItemId);
    }

    private static void AddRetrievalEvidence(
        Dictionary<string, DecisionEvidence> evidenceByItemId,
        ContextRetrievalDecision decision,
        ContextDecisionCandidateOutcome outcome,
        ContextRetrievalTrace trace)
    {
        var itemId = string.IsNullOrWhiteSpace(decision.CandidateId) ? decision.SourceId : decision.CandidateId;
        if (string.IsNullOrWhiteSpace(itemId)) return;
        if (evidenceByItemId.ContainsKey(itemId)) return;

        var stageNames = trace.Stages
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .Select(s => s.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        evidenceByItemId[itemId] = new DecisionEvidence
        {
            ItemId = itemId,
            PrimaryRationale = string.IsNullOrWhiteSpace(decision.Reason)
                ? (outcome == ContextDecisionCandidateOutcome.Selected ? "selected" : "dropped")
                : decision.Reason,
            SecondaryRationales = stageNames,
            Confidence = ComputeConfidence(decision.Score),
            EvidenceRefs = BuildEvidenceRefs(trace.RetrievalId, Array.Empty<string>()),
            Provenance = outcome == ContextDecisionCandidateOutcome.Selected
                ? "retrieval-trace-selected"
                : "retrieval-trace-dropped"
        };
    }

    private static IReadOnlyList<string> MatchUncertainties(IReadOnlyList<ContextPackageUncertainty> uncertainties, string itemId)
    {
        if (uncertainties.Count == 0) return Array.Empty<string>();
        var result = new List<string>();
        foreach (var u in uncertainties)
        {
            if (u.ItemRefs.Contains(itemId, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(string.IsNullOrWhiteSpace(u.Code) ? u.Severity : u.Code);
            }
        }
        return result.Count == 0 ? Array.Empty<string>() : result;
    }

    private static double ComputeConfidence(double score)
    {
        // 把 [0,1] 区间的分数映射为置信度；负数或 NaN 视为 0，>1 视为 1。
        if (double.IsNaN(score) || score <= 0) return 0.0;
        if (score >= 1) return 1.0;
        return score;
    }

    private static string[] BuildEvidenceRefs(string traceId, IReadOnlyList<string> sourceRefs)
    {
        // trace ID 始终作为第一条证据引用，便于溯源到具体 trace artifact。
        if (sourceRefs.Count == 0) return new[] { traceId };
        var result = new string[sourceRefs.Count + 1];
        result[0] = traceId;
        for (var i = 0; i < sourceRefs.Count; i++) result[i + 1] = sourceRefs[i];
        return result;
    }

    private sealed class ContextTraceMatch
    {
        public Dictionary<string, DecisionEvidence> EvidenceByItemId { get; }

        public ContextTraceMatch(Dictionary<string, DecisionEvidence> evidenceByItemId)
        {
            EvidenceByItemId = evidenceByItemId;
        }
    }
}
