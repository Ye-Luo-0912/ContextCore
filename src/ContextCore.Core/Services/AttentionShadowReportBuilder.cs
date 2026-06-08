using ContextCore.Abstractions;

namespace ContextCore.Core.Services;

/// <summary>
/// Builds attention shadow rank/diff reports. This is trace-only logic and must not
/// feed back into retrieval ranking or package output.
/// </summary>
internal static class AttentionShadowReportBuilder
{
    public static AttentionShadowReport Build(
        string operationId,
        ContextRetrievalRequest request,
        IReadOnlyList<ContextRetrievalCandidate> rankedCandidates,
        RetrievalPackingResult currentPackingResult,
        IReadOnlyList<ContextAttentionScore> attentionScores)
    {
        if (attentionScores.Count == 0)
        {
            return new AttentionShadowReport
            {
                OperationId = operationId,
                CandidateCount = rankedCandidates.Count,
                SelectedCount = currentPackingResult.SelectedCandidates.Count
            };
        }

        var attentionById = attentionScores
            .GroupBy(score => score.CandidateId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var profileId = attentionScores.FirstOrDefault()?.ProfileId ?? string.Empty;
        var isGuardedProfile = string.Equals(profileId, "guarded-shadow-v1", StringComparison.OrdinalIgnoreCase);
        var mustHitLabels = BuildLabelSet(request, isMustHit: true);
        var mustNotHitLabels = BuildLabelSet(request, isMustHit: false);
        var effectiveAttentionRanks = BuildEffectiveAttentionRanks(
            rankedCandidates,
            attentionById,
            mustNotHitLabels,
            isGuardedProfile);

        var attentionOrderedCandidates = rankedCandidates
            .OrderByDescending(IsMandatory)
            .ThenBy(candidate => effectiveAttentionRanks.GetValueOrDefault(candidate.CandidateId, int.MaxValue))
            .ThenByDescending(candidate => attentionById.TryGetValue(candidate.CandidateId, out var score)
                ? score.FinalAttentionScore
                : 0d)
            .ThenByDescending(candidate => candidate.Score)
            .ToArray();

        var attentionPacking = RetrievalPackingPolicy.Pack(request, attentionOrderedCandidates);
        var selectedByCurrent = currentPackingResult.SelectedCandidates
            .Select(candidate => candidate.CandidateId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedByAttention = attentionPacking.SelectedCandidates
            .Select(candidate => candidate.CandidateId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (isGuardedProfile)
        {
            selectedByAttention.RemoveWhere(candidateId =>
                rankedCandidates.Any(candidate =>
                    string.Equals(candidate.CandidateId, candidateId, StringComparison.OrdinalIgnoreCase)
                    && MatchesAnyLabel(candidate, mustNotHitLabels)));
        }

        var ranks = rankedCandidates
            .Select(candidate =>
            {
                var attentionScore = attentionById.GetValueOrDefault(candidate.CandidateId);
                var currentRank = attentionScore?.CurrentRank ?? ResolveCurrentRank(rankedCandidates, candidate);
                var attentionRank = effectiveAttentionRanks.GetValueOrDefault(candidate.CandidateId, attentionScore?.AttentionRank ?? currentRank);
                var isMustHit = MatchesAnyLabel(candidate, mustHitLabels);
                var isMustNotHit = MatchesAnyLabel(candidate, mustNotHitLabels);
                var reasons = (attentionScore?.Reasons ?? Array.Empty<string>()).ToList();
                if (isGuardedProfile && isMustNotHit)
                {
                    reasons.Add("guarded_must_not_hit_filtered");
                }

                if (isGuardedProfile && IsLifecycleRisk(candidate))
                {
                    reasons.Add("guarded_lifecycle_risk_penalty");
                }

                return new AttentionShadowRank
                {
                    CandidateId = candidate.CandidateId,
                    SourceId = candidate.SourceId,
                    CurrentRank = currentRank,
                    AttentionRank = attentionRank,
                    RankDelta = currentRank - attentionRank,
                    CurrentScore = candidate.Score,
                    AttentionScore = attentionScore?.FinalAttentionScore ?? 0d,
                    Lifecycle = ResolveLifecycle(candidate),
                    ChannelSources = SplitCsv(candidate.Metadata.GetValueOrDefault("channelSources")),
                    RelationPaths = SplitRelationPaths(candidate.Metadata.GetValueOrDefault("relationPaths")),
                    ScoreBreakdown = candidate.Metadata.GetValueOrDefault("scoreBreakdown") ?? string.Empty,
                    SelectedByCurrentPolicy = selectedByCurrent.Contains(candidate.CandidateId),
                    WouldBeSelectedByAttention = selectedByAttention.Contains(candidate.CandidateId),
                    IsMustHit = isMustHit,
                    IsMustNotHit = isMustNotHit,
                    Reasons = reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                };
            })
            .OrderBy(rank => rank.CurrentRank)
            .ToArray();

        var addedByAttention = ranks
            .Where(rank => rank.WouldBeSelectedByAttention && !rank.SelectedByCurrentPolicy)
            .OrderBy(rank => rank.AttentionRank)
            .ToArray();
        var droppedByAttention = ranks
            .Where(rank => rank.SelectedByCurrentPolicy && !rank.WouldBeSelectedByAttention)
            .OrderBy(rank => rank.CurrentRank)
            .ToArray();
        var topPromoted = ranks
            .Where(rank => rank.RankDelta > 0)
            .OrderByDescending(rank => rank.RankDelta)
            .ThenBy(rank => rank.AttentionRank)
            .Take(10)
            .ToArray();
        var topDemoted = ranks
            .Where(rank => rank.RankDelta < 0)
            .OrderBy(rank => rank.RankDelta)
            .ThenBy(rank => rank.CurrentRank)
            .Take(10)
            .ToArray();

        var currentUnion = selectedByCurrent
            .Union(selectedByAttention, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var changedCount = currentUnion.Count(id => selectedByCurrent.Contains(id) != selectedByAttention.Contains(id));
        var mustNotHitPromotedCount = ranks.Count(rank => rank.IsMustNotHit && rank.RankDelta > 0);
        var warnings = BuildWarnings(ranks, addedByAttention, droppedByAttention, mustNotHitPromotedCount);

        return new AttentionShadowReport
        {
            OperationId = operationId,
            CandidateCount = rankedCandidates.Count,
            SelectedCount = currentPackingResult.SelectedCandidates.Count,
            WouldChangeSelectedSet = addedByAttention.Length > 0 || droppedByAttention.Length > 0,
            Ranks = ranks,
            AddedByAttention = addedByAttention,
            DroppedByAttention = droppedByAttention,
            TopPromotedCandidates = topPromoted,
            TopDemotedCandidates = topDemoted,
            MustNotHitPromotedCount = mustNotHitPromotedCount,
            SelectedSetChangeRatio = currentUnion.Length == 0 ? 0d : (double)changedCount / currentUnion.Length,
            Warnings = warnings
        };
    }

    private static IReadOnlyList<string> BuildWarnings(
        IReadOnlyList<AttentionShadowRank> ranks,
        IReadOnlyList<AttentionShadowRank> addedByAttention,
        IReadOnlyList<AttentionShadowRank> droppedByAttention,
        int mustNotHitPromotedCount)
    {
        var warnings = new List<string>();
        if (addedByAttention.Count > 0 || droppedByAttention.Count > 0)
        {
            warnings.Add("attention_selected_set_would_change");
        }

        if (mustNotHitPromotedCount > 0)
        {
            warnings.Add("must_not_hit_promoted");
        }

        if (ranks.Any(rank => rank.IsMustNotHit && rank.WouldBeSelectedByAttention))
        {
            warnings.Add("must_not_hit_would_be_selected");
        }

        if (ranks.Any(rank => rank.IsMustHit && rank.SelectedByCurrentPolicy && !rank.WouldBeSelectedByAttention))
        {
            warnings.Add("must_hit_dropped_by_attention");
        }

        return warnings;
    }

    private static Dictionary<string, int> BuildEffectiveAttentionRanks(
        IReadOnlyList<ContextRetrievalCandidate> rankedCandidates,
        IReadOnlyDictionary<string, ContextAttentionScore> attentionById,
        IReadOnlySet<string> mustNotHitLabels,
        bool isGuardedProfile)
    {
        if (!isGuardedProfile)
        {
            return rankedCandidates
                .Select(candidate =>
                {
                    var rank = attentionById.TryGetValue(candidate.CandidateId, out var score)
                        ? score.AttentionRank
                        : ResolveCurrentRank(rankedCandidates, candidate);
                    return new { candidate.CandidateId, Rank = rank };
                })
                .ToDictionary(item => item.CandidateId, item => item.Rank, StringComparer.OrdinalIgnoreCase);
        }

        return rankedCandidates
            .OrderByDescending(IsMandatory)
            .ThenBy(candidate => ResolveGuardBucket(candidate, attentionById, mustNotHitLabels, isGuardedProfile))
            .ThenBy(candidate => attentionById.TryGetValue(candidate.CandidateId, out var score)
                ? score.AttentionRank
                : int.MaxValue)
            .ThenByDescending(candidate => attentionById.TryGetValue(candidate.CandidateId, out var score)
                ? score.FinalAttentionScore
                : 0d)
            .ThenByDescending(candidate => candidate.Score)
            .Select((candidate, index) => new { candidate.CandidateId, Rank = index + 1 })
            .ToDictionary(item => item.CandidateId, item => item.Rank, StringComparer.OrdinalIgnoreCase);
    }

    private static int ResolveGuardBucket(
        ContextRetrievalCandidate candidate,
        IReadOnlyDictionary<string, ContextAttentionScore> attentionById,
        IReadOnlySet<string> mustNotHitLabels,
        bool isGuardedProfile)
    {
        if (!isGuardedProfile)
        {
            return 0;
        }

        if (MatchesAnyLabel(candidate, mustNotHitLabels))
        {
            return 4;
        }

        if (IsLifecycleRisk(candidate))
        {
            return 3;
        }

        if (attentionById.TryGetValue(candidate.CandidateId, out var score)
            && score.CurrentRank <= 3)
        {
            return 0;
        }

        if (IsMandatory(candidate) || HasExactAnchor(candidate) || IsHardConstraint(candidate))
        {
            return 0;
        }

        return 1;
    }

    private static int ResolveCurrentRank(
        IReadOnlyList<ContextRetrievalCandidate> rankedCandidates,
        ContextRetrievalCandidate candidate)
    {
        for (var i = 0; i < rankedCandidates.Count; i++)
        {
            if (string.Equals(rankedCandidates[i].CandidateId, candidate.CandidateId, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return rankedCandidates.Count + 1;
    }

    private static bool IsMandatory(ContextRetrievalCandidate candidate)
    {
        return candidate.Metadata.TryGetValue("mandatory", out var value)
            && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExactAnchor(ContextRetrievalCandidate candidate)
    {
        return candidate.Metadata.TryGetValue("matchedAnchors", out var value)
            && !string.IsNullOrWhiteSpace(value);
    }

    private static bool IsHardConstraint(ContextRetrievalCandidate candidate)
    {
        if (!candidate.Type.Contains("constraint", StringComparison.OrdinalIgnoreCase)
            && !candidate.Tags.Any(tag => tag.Contains("constraint", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return candidate.Metadata.Any(pair =>
            pair.Value.Contains("hard", StringComparison.OrdinalIgnoreCase)
            || pair.Value.Contains("required", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLifecycleRisk(ContextRetrievalCandidate candidate)
    {
        var lifecycle = ResolveLifecycle(candidate);
        return lifecycle.Contains("rejected", StringComparison.OrdinalIgnoreCase)
            || lifecycle.Contains("deprecated", StringComparison.OrdinalIgnoreCase)
            || lifecycle.Contains("superseded", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveLifecycle(ContextRetrievalCandidate candidate)
    {
        if (candidate.Metadata.TryGetValue("lifecycleStatus", out var lifecycleStatus)
            && !string.IsNullOrWhiteSpace(lifecycleStatus))
        {
            return lifecycleStatus;
        }

        if (candidate.Metadata.TryGetValue("lifecycle", out var lifecycle)
            && !string.IsNullOrWhiteSpace(lifecycle))
        {
            return lifecycle;
        }

        return "Active";
    }

    private static HashSet<string> BuildLabelSet(ContextRetrievalRequest request, bool isMustHit)
    {
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (isMustHit)
        {
            foreach (var id in request.RequiredIds)
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    labels.Add(id.Trim());
                }
            }
        }

        var keys = isMustHit
            ? new[] { "attention.mustHit", "eval.mustHit", "mustHit" }
            : new[] { "attention.mustNotHit", "eval.mustNotHit", "mustNotHit" };

        foreach (var pair in request.Metadata)
        {
            if (!keys.Any(key => string.Equals(key, pair.Key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (var value in SplitLabels(pair.Value))
            {
                labels.Add(value);
            }
        }

        return labels;
    }

    private static IEnumerable<string> SplitLabels(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (var item in value.Split([',', ';', '|', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(item))
            {
                yield return item.Trim();
            }
        }
    }

    private static bool MatchesAnyLabel(ContextRetrievalCandidate candidate, IReadOnlySet<string> labels)
    {
        return labels.Count > 0 && labels.Any(label => MatchesLabel(candidate, label));
    }

    private static bool MatchesLabel(ContextRetrievalCandidate candidate, string label)
    {
        if (string.Equals(candidate.CandidateId, label, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.SourceId, label, StringComparison.OrdinalIgnoreCase)
            || candidate.SourceRefs.Contains(label, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return candidate.Metadata.Any(pair =>
            (string.Equals(pair.Key, "itemId", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pair.Key, "sourceId", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pair.Key, "memoryId", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pair.Key, "contextId", StringComparison.OrdinalIgnoreCase))
            && string.Equals(pair.Value, label, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> SplitCsv(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    private static IReadOnlyList<string> SplitRelationPaths(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }
}
