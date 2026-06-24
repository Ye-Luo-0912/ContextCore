using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// Runs lifecycle-aware ranker shadow scoring for an explicit debug request.
/// The service never mutates retrieval order, selected IDs, or packing decisions.
/// </summary>
public sealed class LifecycleAwareRankerDebugService
{
    private readonly IContextRetriever _retriever;
    private readonly LifecycleAwareRankerShadowScorer _scorer;

    public LifecycleAwareRankerDebugService(
        IContextRetriever retriever,
        LifecycleAwareRankerShadowScorer scorer)
    {
        _retriever = retriever;
        _scorer = scorer;
    }

    public async Task<LifecycleAwareRankerShadowDebugResponse> DebugAsync(
        LifecycleAwareRankerShadowDebugRequest request,
        string? profile = null,
        bool debugEndpointEnabled = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!debugEndpointEnabled)
        {
            throw new InvalidOperationException("Lifecycle-aware ranker shadow debug endpoint is disabled.");
        }

        if (string.IsNullOrWhiteSpace(request.WorkspaceId))
        {
            throw new ArgumentException("workspaceId is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.CollectionId))
        {
            throw new ArgumentException("collectionId is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Query))
        {
            throw new ArgumentException("query is required.", nameof(request));
        }

        var operationId = Guid.NewGuid().ToString("N");
        var retrievalRequest = new ContextRetrievalRequest
        {
            OperationId = operationId,
            WorkspaceId = request.WorkspaceId.Trim(),
            CollectionId = request.CollectionId.Trim(),
            QueryText = request.Query.Trim(),
            TopK = request.TopK > 0 ? request.TopK : 10,
            CandidateTake = request.CandidateTake > 0 ? request.CandidateTake : 50,
            TokenBudget = request.TokenBudget > 0 ? request.TokenBudget : 4000,
            IncludeKeywordRecall = true,
            IncludeVectorRecall = false,
            IncludeRelationExpansion = true,
            IncludeWorkingMemory = true,
            IncludeStableMemory = true,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["source"] = "ranker-shadow-debug",
                ["debugOnly"] = "true",
                ["mode"] = string.IsNullOrWhiteSpace(request.Mode) ? "ChatMode" : request.Mode.Trim()
            }
        };

        var result = await _retriever.RetrieveAsync(retrievalRequest, cancellationToken).ConfigureAwait(false);
        var selected = result.SelectedItems
            .Select((candidate, index) => ToDiagnostic(candidate, index + 1, selected: true))
            .ToArray();
        var candidatesById = result.Trace.Candidates
            .GroupBy(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        var dropped = result.DroppedItems
            .Select((decision, index) => ToDiagnostic(decision, index + 1, candidatesById))
            .ToArray();
        var trace = _scorer.Score(
            selected,
            dropped,
            new LifecycleAwareRankerShadowOptions
            {
                Enabled = true,
                Profile = string.IsNullOrWhiteSpace(profile)
                    ? LifecycleAwareRankerShadowScorer.DefaultProfile
                    : profile.Trim()
            });

        var candidateScores = FilterCandidateScores(
                trace.CandidateShadowScores,
                request.CandidateIds,
                request.IncludeLifecycleDetails)
            .ToArray();
        var legacySelected = result.SelectedItems
            .Select(static item => ResolveItemId(item.CandidateId, item.SourceId))
            .ToArray();

        return new LifecycleAwareRankerShadowDebugResponse
        {
            OperationId = operationId,
            RetrievalOperationId = result.OperationId,
            WorkspaceId = request.WorkspaceId.Trim(),
            CollectionId = request.CollectionId.Trim(),
            Query = request.Query.Trim(),
            Mode = string.IsNullOrWhiteSpace(request.Mode) ? "ChatMode" : request.Mode.Trim(),
            RankerShadowEnabled = true,
            DebugEndpointEnabled = debugEndpointEnabled,
            RankerShadowProfile = trace.RankerShadowProfile,
            FormalOutputChanged = false,
            SelectedSetChanged = false,
            LegacySelectedIds = legacySelected,
            FinalSelectedIds = legacySelected,
            CandidateScores = candidateScores,
            DeprecatedDemotions = FilterTraceItems(trace.DeprecatedDemotions, candidateScores),
            HistoricalDemotions = candidateScores
                .Where(static item => item.ScoreDelta < 0
                    && (item.LifecycleFeatures.IsHistorical || item.LifecycleFeatures.HistoricalSectionOnly))
                .OrderBy(static item => item.ShadowRank)
                .ToArray(),
            CurrentActivePromotions = candidateScores
                .Where(static item => item.ScoreDelta > 0
                    && (item.LifecycleFeatures.IsCurrentVersion
                        || item.Reason.Contains("current_version_boost", StringComparison.OrdinalIgnoreCase)
                        || item.Reason.Contains("supersedes_relation_boost", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(static item => item.ShadowRank)
                .ToArray(),
            VersionConflictFixes = FilterTraceItems(trace.VersionConflictFixes, candidateScores),
            MustHitDemotions = FilterTraceItems(trace.MustHitDemotions, candidateScores),
            MustNotHitPromotions = FilterTraceItems(trace.MustNotHitPromotions, candidateScores),
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["debugOnly"] = "true",
                ["formalOutputChanged"] = "false",
                ["selectedSetChanged"] = "false",
                ["includeVectorRecall"] = "false",
                ["policyVersion"] = LifecycleAwareRankerShadowScorer.PolicyVersion
            }
        };
    }

    private static IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> FilterCandidateScores(
        IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> scores,
        IReadOnlyList<string> candidateIds,
        bool includeLifecycleDetails)
    {
        var filtered = candidateIds.Count == 0
            ? scores
            : scores
                .Where(item => candidateIds.Contains(item.CandidateId, StringComparer.OrdinalIgnoreCase))
                .ToArray();

        return includeLifecycleDetails
            ? filtered
            : filtered.Select(static item => new LifecycleAwareRankerShadowCandidateScore
            {
                CandidateId = item.CandidateId,
                Kind = item.Kind,
                Type = item.Type,
                SectionName = item.SectionName,
                Selected = item.Selected,
                IsMustHit = item.IsMustHit,
                IsMustNotHit = item.IsMustNotHit,
                LegacyRank = item.LegacyRank,
                ShadowRank = item.ShadowRank,
                RankDelta = item.RankDelta,
                LegacyScore = item.LegacyScore,
                LifecycleAwareScore = item.LifecycleAwareScore,
                ScoreDelta = item.ScoreDelta,
                Reason = item.Reason,
                DemotionReasons = item.DemotionReasons,
                PromotionReasons = item.PromotionReasons
            }).ToArray();
    }

    private static IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> FilterTraceItems(
        IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> source,
        IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> allowed)
    {
        if (source.Count == 0 || allowed.Count == 0)
        {
            return Array.Empty<LifecycleAwareRankerShadowCandidateScore>();
        }

        var allowedIds = allowed
            .Select(static item => item.CandidateId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return source
            .Where(item => allowedIds.Contains(item.CandidateId))
            .ToArray();
    }

    private static ContextEvalItemDiagnostic ToDiagnostic(
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

    private static ContextEvalItemDiagnostic ToDiagnostic(
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
