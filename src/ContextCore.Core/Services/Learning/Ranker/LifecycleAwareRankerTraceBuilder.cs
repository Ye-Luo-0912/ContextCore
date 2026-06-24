using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// Builds lifecycle-aware ranker shadow traces from already produced retrieval candidates.
/// It is diagnostic-only and never changes retrieval ordering or packing decisions.
/// </summary>
public sealed class LifecycleAwareRankerTraceBuilder
{
    private readonly LifecycleAwareRankerShadowScorer _scorer;

    public LifecycleAwareRankerTraceBuilder(LifecycleAwareRankerShadowScorer scorer)
    {
        _scorer = scorer;
    }

    public LifecycleAwareRankerShadowTrace Build(
        IReadOnlyList<ContextRetrievalCandidate> selectedItems,
        IReadOnlyList<ContextRetrievalDecision> droppedItems,
        IReadOnlyList<ContextRetrievalCandidate> traceCandidates,
        LifecycleAwareRankerShadowOptions options)
    {
        ArgumentNullException.ThrowIfNull(selectedItems);
        ArgumentNullException.ThrowIfNull(droppedItems);
        ArgumentNullException.ThrowIfNull(traceCandidates);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return new LifecycleAwareRankerShadowTrace
            {
                RankerShadowEnabled = false,
                RankerShadowProfile = ResolveProfile(options)
            };
        }

        var selected = selectedItems
            .Select((candidate, index) => ToDiagnostic(candidate, index + 1, selected: true))
            .ToArray();
        var candidatesById = traceCandidates
            .GroupBy(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        var dropped = droppedItems
            .Select((decision, index) => ToDiagnostic(decision, index + 1, candidatesById))
            .ToArray();
        var trace = _scorer.Score(selected, dropped, options);

        return LimitTrace(trace, options.MaxCandidatesPerTrace);
    }

    private static LifecycleAwareRankerShadowTrace LimitTrace(
        LifecycleAwareRankerShadowTrace trace,
        int maxCandidates)
    {
        var limit = maxCandidates > 0 ? maxCandidates : 50;
        var scores = trace.CandidateShadowScores
            .OrderBy(static item => item.LegacyRank)
            .ThenBy(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();
        var allowed = scores
            .Select(static item => item.CandidateId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new LifecycleAwareRankerShadowTrace
        {
            RankerShadowEnabled = trace.RankerShadowEnabled,
            RankerShadowProfile = trace.RankerShadowProfile,
            CandidateShadowScores = scores,
            DeprecatedDemotions = Filter(trace.DeprecatedDemotions, allowed),
            VersionConflictFixes = Filter(trace.VersionConflictFixes, allowed),
            MustHitDemotions = Filter(trace.MustHitDemotions, allowed),
            MustNotHitPromotions = Filter(trace.MustNotHitPromotions, allowed)
        };
    }

    private static IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> Filter(
        IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> items,
        HashSet<string> allowed)
    {
        return items
            .Where(item => allowed.Contains(item.CandidateId))
            .ToArray();
    }

    public static ContextEvalItemDiagnostic ToDiagnostic(
        ContextRetrievalCandidate candidate,
        int rank,
        bool selected)
    {
        var reasonParts = candidate.Reasons
            .Concat(candidate.Tags)
            .Concat(candidate.Metadata.Select(static pair => $"{pair.Key}={pair.Value}"))
            .Append(selected ? "selected=true" : "selected=false")
            .ToArray();

        return new ContextEvalItemDiagnostic
        {
            ItemId = ResolveItemId(candidate.CandidateId, candidate.SourceId),
            Kind = ResolveKind(candidate),
            Type = candidate.Type,
            SectionName = ResolveSectionName(candidate.Metadata, ResolveKind(candidate)),
            Reason = string.Join(' ', reasonParts),
            Score = candidate.Score,
            EstimatedTokens = candidate.EstimatedTokens,
            Rank = rank,
            SourceRefs = candidate.SourceRefs.ToArray()
        };
    }

    public static ContextEvalItemDiagnostic ToDiagnostic(
        ContextRetrievalDecision decision,
        int rank,
        IReadOnlyDictionary<string, ContextRetrievalCandidate> candidatesById)
    {
        candidatesById.TryGetValue(decision.CandidateId, out var candidate);
        var metadata = candidate?.Metadata ?? decision.Metadata;
        var sourceRefs = candidate?.SourceRefs ?? Array.Empty<string>();
        var reasonParts = new List<string>
        {
            decision.Reason,
            "selected=false"
        };
        if (candidate is not null)
        {
            reasonParts.AddRange(candidate.Reasons);
            reasonParts.AddRange(candidate.Tags);
        }

        reasonParts.AddRange(metadata.Select(static pair => $"{pair.Key}={pair.Value}"));

        return new ContextEvalItemDiagnostic
        {
            ItemId = ResolveItemId(decision.CandidateId, decision.SourceId),
            Kind = ResolveKind(decision.Kind, metadata),
            Type = decision.Type,
            SectionName = ResolveSectionName(metadata, ResolveKind(decision.Kind, metadata)),
            Reason = string.Join(' ', reasonParts),
            Score = decision.Score,
            EstimatedTokens = decision.EstimatedTokens,
            Rank = rank,
            SourceRefs = sourceRefs.ToArray()
        };
    }

    private static string ResolveProfile(LifecycleAwareRankerShadowOptions options)
    {
        return string.IsNullOrWhiteSpace(options.Profile)
            ? LifecycleAwareRankerShadowScorer.DefaultProfile
            : options.Profile;
    }

    private static string ResolveItemId(string candidateId, string sourceId)
    {
        return string.IsNullOrWhiteSpace(sourceId) ? candidateId : sourceId;
    }

    private static string ResolveKind(ContextRetrievalCandidate candidate)
    {
        return ResolveKind(candidate.Kind, candidate.Metadata);
    }

    private static string ResolveKind(
        ContextRetrievalCandidateKind kind,
        IReadOnlyDictionary<string, string> metadata)
    {
        if (metadata.TryGetValue("memoryLayer", out var memoryLayer) && !string.IsNullOrWhiteSpace(memoryLayer))
        {
            return memoryLayer;
        }

        if (metadata.TryGetValue("candidateSourceKind", out var candidateSourceKind) && !string.IsNullOrWhiteSpace(candidateSourceKind))
        {
            return candidateSourceKind;
        }

        return kind.ToString();
    }

    private static string ResolveSectionName(
        IReadOnlyDictionary<string, string> metadata,
        string fallbackKind)
    {
        foreach (var key in new[]
        {
            "section",
            "sectionName",
            "planningSection",
            "constraintSection",
            "packageSection"
        })
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        if (metadata.TryGetValue("memoryLayer", out var memoryLayer))
        {
            return memoryLayer;
        }

        return fallbackKind;
    }
}
