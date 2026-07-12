using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>Vector query preview 的 eval shadow runner；只读评估，不改变正式 retrieval/package。</summary>
public sealed class VectorQueryShadowEvalRunner
{
    private readonly VectorQueryPreviewService _previewService;

    public VectorQueryShadowEvalRunner(VectorQueryPreviewService previewService)
    {
        _previewService = previewService;
    }

    public async Task<VectorQueryShadowEvalReport> RunAsync(
        IReadOnlyList<ContextEvalSample> samples,
        string workspaceId,
        string collectionId,
        int topK = 10,
        string? layer = null,
        string? itemKind = null,
        double? minSimilarity = null,
        double lowConfidenceThreshold = 0.25,
        string? profileId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);

        var operationId = $"vector-query-shadow-eval-{Guid.NewGuid():N}";
        var sampleResults = new List<VectorQueryShadowEvalSample>();
        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preview = await _previewService.PreviewAsync(new VectorQueryPreviewRequest
            {
                OperationId = $"{operationId}:{sample.Id}",
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                QueryText = sample.Query,
                TopK = topK,
                ProfileId = string.IsNullOrWhiteSpace(profileId)
                    ? VectorQueryProfileIds.NormalV1
                    : profileId,
                Layer = layer,
                ItemKind = itemKind,
                MinSimilarity = minSimilarity,
                IncludeVector = false,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sampleId"] = sample.Id,
                    ["mode"] = sample.Mode,
                    ["createdFrom"] = "vector_query_shadow_eval"
                }
            }, cancellationToken).ConfigureAwait(false);

            sampleResults.Add(BuildSampleResult(sample, preview, lowConfidenceThreshold));
        }

        return BuildReport(operationId, sampleResults);
    }

    public static VectorQueryShadowEvalReport BuildReport(
        string operationId,
        IReadOnlyList<VectorQueryShadowEvalSample> samples,
        IReadOnlyList<string>? warnings = null)
    {
        var sampleCount = samples.Count;
        var candidateCount = samples.Sum(sample => sample.CandidateCount);
        var rawCandidateCount = samples.Sum(sample => sample.RawCandidateCount);
        var eligibleCandidateCount = samples.Sum(sample => sample.EligibleCandidateCount);
        var blockedCandidateCount = samples.Sum(sample => sample.BlockedCandidateCount);
        var totalMustHit = samples.Sum(sample => sample.MustHitCount);
        var totalMustHitHits = samples.Sum(sample => sample.MustHitHitCount);
        var totalMustHitHitsBefore = samples.Sum(sample => sample.MustHitHitCountBeforePolicy);
        var totalMustHitHitsAfter = samples.Sum(sample => sample.MustHitHitCountAfterPolicy);
        var totalMustNot = samples.Sum(sample => sample.MustNotHitCount);
        var totalMustNotHits = samples.Sum(sample => sample.MustNotHitHitCount);
        var totalMustNotHitsBefore = samples.Sum(sample => sample.MustNotHitHitCountBeforePolicy);
        var totalMustNotHitsAfter = samples.Sum(sample => sample.MustNotHitHitCountAfterPolicy);
        var lifecycleRisk = samples.Sum(sample => sample.LifecycleRiskCount);
        var lifecycleRiskBefore = samples.Sum(sample => sample.LifecycleRiskBeforePolicy);
        var lifecycleRiskAfter = samples.Sum(sample => sample.LifecycleRiskAfterPolicy);
        var riskBefore = samples.Sum(sample => sample.RiskBeforePolicy);
        var riskAfter = samples.Sum(sample => sample.RiskAfterPolicy);
        var deprecatedHits = samples.Sum(sample => sample.DeprecatedHitCount);
        var duplicateHits = samples.Sum(sample => sample.DuplicateHitCount);
        var noCandidateCount = samples.Count(sample => sample.RawCandidateCount == 0);
        var lowConfidenceCount = samples.Count(sample => sample.LowConfidence);
        var topSimilarities = samples
            .Where(sample => sample.TopSimilarity > 0)
            .Select(sample => sample.TopSimilarity)
            .ToArray();
        var noiseClusters = BuildNoiseClusters(samples);
        var blockedByReason = samples
            .SelectMany(sample => sample.BlockedByReason)
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Value), StringComparer.OrdinalIgnoreCase);

        return new VectorQueryShadowEvalReport
        {
            OperationId = operationId,
            CreatedAt = DateTimeOffset.UtcNow,
            Samples = sampleCount,
            IndexedCoverage = sampleCount == 0 ? 0 : (double)(sampleCount - noCandidateCount) / sampleCount,
            QueryCount = sampleCount,
            CandidateCount = candidateCount,
            RawCandidateCount = rawCandidateCount,
            EligibleCandidateCount = eligibleCandidateCount,
            BlockedCandidateCount = blockedCandidateCount,
            RiskBeforePolicy = riskBefore,
            RiskAfterPolicy = riskAfter,
            MustHitRecallBeforePolicy = totalMustHit == 0 ? 1.0 : (double)totalMustHitHitsBefore / totalMustHit,
            MustHitRecallAfterPolicy = totalMustHit == 0 ? 1.0 : (double)totalMustHitHitsAfter / totalMustHit,
            MustNotHitRiskBeforePolicy = totalMustNot == 0 ? 0 : (double)totalMustNotHitsBefore / totalMustNot,
            MustNotHitRiskAfterPolicy = totalMustNot == 0 ? 0 : (double)totalMustNotHitsAfter / totalMustNot,
            LifecycleRiskBeforePolicy = rawCandidateCount == 0 ? 0 : (double)lifecycleRiskBefore / rawCandidateCount,
            LifecycleRiskAfterPolicy = eligibleCandidateCount == 0 ? 0 : (double)lifecycleRiskAfter / eligibleCandidateCount,
            MustHitRecallAtK = totalMustHit == 0 ? 1.0 : (double)totalMustHitHits / totalMustHit,
            MustNotHitRiskAtK = totalMustNot == 0 ? 0 : (double)totalMustNotHits / totalMustNot,
            LifecycleRiskAtK = eligibleCandidateCount == 0 ? 0 : (double)lifecycleRisk / eligibleCandidateCount,
            DeprecatedHitCount = deprecatedHits,
            DuplicateHitCount = duplicateHits,
            AverageTopSimilarity = topSimilarities.Length == 0 ? 0 : topSimilarities.Average(),
            NoCandidateCount = noCandidateCount,
            LowConfidenceCount = lowConfidenceCount,
            TopNoiseClusters = noiseClusters,
            BlockedByReason = blockedByReason,
            Recommendation = Recommend(sampleCount, rawCandidateCount, eligibleCandidateCount, noCandidateCount, lowConfidenceCount, totalMustHit, totalMustHitHitsAfter, riskAfter),
            FormalOutputChanged = 0,
            SampleResults = samples,
            Warnings = warnings ?? Array.Empty<string>()
        };
    }

    public static string BuildMarkdownReport(
        VectorQueryShadowEvalReport a3,
        VectorQueryShadowEvalReport extended)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Query Shadow Eval Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {DateTimeOffset.UtcNow:O}");
        builder.AppendLine();
        AppendReport(builder, "A3", a3);
        AppendReport(builder, "Extended", extended);
        return builder.ToString();
    }

    private static void AppendReport(
        StringBuilder builder,
        string title,
        VectorQueryShadowEvalReport report)
    {
        builder.AppendLine($"## {title} Summary");
        builder.AppendLine();
        builder.AppendLine($"- Samples: `{report.Samples}`");
        builder.AppendLine($"- IndexedCoverage: `{report.IndexedCoverage:P2}`");
        builder.AppendLine($"- RawCandidateCount: `{report.RawCandidateCount}`");
        builder.AppendLine($"- EligibleCandidateCount: `{report.EligibleCandidateCount}`");
        builder.AppendLine($"- BlockedCandidateCount: `{report.BlockedCandidateCount}`");
        builder.AppendLine($"- RiskBeforePolicy: `{report.RiskBeforePolicy}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- MustHitRecallBeforePolicy: `{report.MustHitRecallBeforePolicy:P2}`");
        builder.AppendLine($"- MustHitRecallAfterPolicy: `{report.MustHitRecallAfterPolicy:P2}`");
        builder.AppendLine($"- MustNotHitRiskBeforePolicy: `{report.MustNotHitRiskBeforePolicy:P2}`");
        builder.AppendLine($"- MustNotHitRiskAfterPolicy: `{report.MustNotHitRiskAfterPolicy:P2}`");
        builder.AppendLine($"- LifecycleRiskBeforePolicy: `{report.LifecycleRiskBeforePolicy:P2}`");
        builder.AppendLine($"- LifecycleRiskAfterPolicy: `{report.LifecycleRiskAfterPolicy:P2}`");
        builder.AppendLine($"- DeprecatedHitCount: `{report.DeprecatedHitCount}`");
        builder.AppendLine($"- DuplicateHitCount: `{report.DuplicateHitCount}`");
        builder.AppendLine($"- AverageTopSimilarity: `{report.AverageTopSimilarity:F4}`");
        builder.AppendLine($"- NoCandidateCount: `{report.NoCandidateCount}`");
        builder.AppendLine($"- LowConfidenceCount: `{report.LowConfidenceCount}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("| Noise Cluster | Count |");
        builder.AppendLine("|---|---:|");
        foreach (var cluster in report.TopNoiseClusters.OrderByDescending(item => item.Value).ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {cluster.Key} | {cluster.Value} |");
        }

        builder.AppendLine();
        builder.AppendLine("| Blocked Reason | Count |");
        builder.AppendLine("|---|---:|");
        foreach (var reason in report.BlockedByReason.OrderByDescending(item => item.Value).ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {reason.Key} | {reason.Value} |");
        }

        builder.AppendLine();
        builder.AppendLine($"## {title} Sample Details");
        builder.AppendLine();
        builder.AppendLine("| Sample | Mode | Raw | Eligible | Blocked | MustHit Before/After | Risk Before/After | TopSimilarity | Recommendation |");
        builder.AppendLine("|---|---|---:|---:|---:|---|---|---:|---|");
        foreach (var sample in report.SampleResults.Take(80))
        {
            builder.AppendLine($"| {sample.SampleId} | {sample.Mode} | {sample.RawCandidateCount} | {sample.EligibleCandidateCount} | {sample.BlockedCandidateCount} | {Ids(sample.MustHitMatchedBeforePolicy)}/{Ids(sample.MustHitMatchedAfterPolicy)} | {sample.RiskBeforePolicy}/{sample.RiskAfterPolicy} | {sample.TopSimilarity:F4} | {sample.Recommendation} |");
        }

        builder.AppendLine();
    }

    public static VectorQueryShadowEvalSample BuildSampleResult(
        ContextEvalSample sample,
        VectorQueryPreviewResult preview,
        double lowConfidenceThreshold)
    {
        var candidates = preview.Candidates;
        var eligibleCandidates = candidates
            .Where(candidate => string.Equals(candidate.EligibilityStatus, VectorCandidateEligibilityStatuses.Eligible, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var rawCandidateIds = candidates.Select(candidate => candidate.ItemId).ToArray();
        var eligibleCandidateIds = eligibleCandidates.Select(candidate => candidate.ItemId).ToArray();
        var mustHitMatchedBefore = sample.MustHit
            .Where(expected => rawCandidateIds.Any(candidateId => EvalIdMatches(expected, candidateId)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var mustHitMatchedAfter = sample.MustHit
            .Where(expected => eligibleCandidateIds.Any(candidateId => EvalIdMatches(expected, candidateId)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var mustHitMissing = sample.MustHit
            .Where(expected => !mustHitMatchedAfter.Contains(expected, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var mustNotHitMatchedBefore = sample.MustNotHit
            .Where(expected => rawCandidateIds.Any(candidateId => EvalIdMatches(expected, candidateId)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var mustNotHitMatchedAfter = sample.MustNotHit
            .Where(expected => eligibleCandidateIds.Any(candidateId => EvalIdMatches(expected, candidateId)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var lifecycleRiskBefore = candidates
            .Where(candidate => candidate.IsLifecycleRisk || candidate.IsStale || candidate.IsOrphan)
            .Select(candidate => candidate.ItemId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var lifecycleRiskAfter = eligibleCandidates
            .Where(candidate => candidate.IsLifecycleRisk || candidate.IsStale || candidate.IsOrphan || candidate.RiskAfterPolicy)
            .Select(candidate => candidate.ItemId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var riskBefore = candidates
            .Where(candidate => candidate.RiskIfNormalSelected)
            .Select(candidate => candidate.ItemId)
            .Concat(mustNotHitMatchedBefore)
            .Concat(lifecycleRiskBefore)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var riskAfter = eligibleCandidates
            .Where(candidate => candidate.RiskAfterPolicy)
            .Select(candidate => candidate.ItemId)
            .Concat(mustNotHitMatchedAfter)
            .Concat(lifecycleRiskAfter)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var blockedByReason = candidates
            .SelectMany(candidate => candidate.BlockedReasons)
            .GroupBy(reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var topSimilarity = candidates.Count == 0 ? 0 : candidates.Max(candidate => candidate.Similarity);
        var lowConfidence = candidates.Count > 0 && topSimilarity < lowConfidenceThreshold;

        return new VectorQueryShadowEvalSample
        {
            SampleId = sample.Id,
            Mode = sample.Mode,
            QueryText = sample.Query,
            TopK = preview.TopK,
            CandidateCount = candidates.Count,
            RawCandidateCount = candidates.Count,
            EligibleCandidateCount = eligibleCandidates.Length,
            BlockedCandidateCount = candidates.Count - eligibleCandidates.Length,
            MustHitCount = sample.MustHit.Count,
            MustHitHitCount = mustHitMatchedAfter.Length,
            MustHitHitCountBeforePolicy = mustHitMatchedBefore.Length,
            MustHitHitCountAfterPolicy = mustHitMatchedAfter.Length,
            MustNotHitCount = sample.MustNotHit.Count,
            MustNotHitHitCount = mustNotHitMatchedAfter.Length,
            MustNotHitHitCountBeforePolicy = mustNotHitMatchedBefore.Length,
            MustNotHitHitCountAfterPolicy = mustNotHitMatchedAfter.Length,
            LifecycleRiskCount = lifecycleRiskAfter.Length,
            LifecycleRiskBeforePolicy = lifecycleRiskBefore.Length,
            LifecycleRiskAfterPolicy = lifecycleRiskAfter.Length,
            RiskBeforePolicy = riskBefore,
            RiskAfterPolicy = riskAfter,
            DeprecatedHitCount = candidates.Count(IsDeprecatedHit),
            DuplicateHitCount = candidates.Count(candidate => candidate.IsDuplicate),
            TopSimilarity = topSimilarity,
            LowConfidence = lowConfidence,
            MustHitMatched = mustHitMatchedAfter,
            MustHitMatchedBeforePolicy = mustHitMatchedBefore,
            MustHitMatchedAfterPolicy = mustHitMatchedAfter,
            MustHitMissing = mustHitMissing,
            MustNotHitMatched = mustNotHitMatchedAfter,
            MustNotHitMatchedBeforePolicy = mustNotHitMatchedBefore,
            MustNotHitMatchedAfterPolicy = mustNotHitMatchedAfter,
            LifecycleRiskItems = lifecycleRiskAfter,
            LifecycleRiskItemsBeforePolicy = lifecycleRiskBefore,
            LifecycleRiskItemsAfterPolicy = lifecycleRiskAfter,
            BlockedByReason = blockedByReason,
            Candidates = candidates,
            Recommendation = RecommendSample(candidates.Count, eligibleCandidates.Length, lowConfidence, mustHitMatchedAfter.Length, sample.MustHit.Count, riskAfter)
        };
    }

    private static IReadOnlyDictionary<string, int> BuildNoiseClusters(IReadOnlyList<VectorQueryShadowEvalSample> samples)
    {
        var clusters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Add(clusters, "NoCandidate", samples.Count(sample => sample.RawCandidateCount == 0));
        Add(clusters, "NoEligibleCandidate", samples.Count(sample => sample.RawCandidateCount > 0 && sample.EligibleCandidateCount == 0));
        Add(clusters, "LowConfidence", samples.Count(sample => sample.LowConfidence));
        Add(clusters, "MustNotHitBeforePolicy", samples.Sum(sample => sample.MustNotHitHitCountBeforePolicy));
        Add(clusters, "MustNotHitAfterPolicy", samples.Sum(sample => sample.MustNotHitHitCountAfterPolicy));
        Add(clusters, "LifecycleRiskBeforePolicy", samples.Sum(sample => sample.LifecycleRiskBeforePolicy));
        Add(clusters, "LifecycleRiskAfterPolicy", samples.Sum(sample => sample.LifecycleRiskAfterPolicy));
        Add(clusters, "DeprecatedHit", samples.Sum(sample => sample.DeprecatedHitCount));
        Add(clusters, "DuplicateHit", samples.Sum(sample => sample.DuplicateHitCount));
        Add(clusters, "StaleHit", samples.Sum(sample => sample.Candidates.Count(candidate => candidate.IsStale)));
        Add(clusters, "OrphanHit", samples.Sum(sample => sample.Candidates.Count(candidate => candidate.IsOrphan)));
        return clusters
            .Where(item => item.Value > 0)
            .OrderByDescending(item => item.Value)
            .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static string Recommend(
        int samples,
        int rawCandidateCount,
        int eligibleCandidateCount,
        int noCandidateCount,
        int lowConfidenceCount,
        int totalMustHit,
        int totalMustHitHits,
        int riskAfterPolicy)
    {
        if (samples == 0 || rawCandidateCount == 0 || noCandidateCount > samples / 2)
        {
            return VectorQueryShadowRecommendations.NeedsMoreIndexedData;
        }

        if (riskAfterPolicy > 0)
        {
            return VectorQueryShadowRecommendations.BlockedByRisk;
        }

        if (eligibleCandidateCount == 0)
        {
            return VectorQueryShadowRecommendations.NeedsPolicyTuning;
        }

        var recall = totalMustHit == 0 ? 1.0 : (double)totalMustHitHits / totalMustHit;
        if (lowConfidenceCount > samples / 4 || recall < 0.5)
        {
            return VectorQueryShadowRecommendations.NeedsPolicyTuning;
        }

        return recall >= 0.8
            ? VectorQueryShadowRecommendations.ReadyForRetrievalShadow
            : VectorQueryShadowRecommendations.KeepPreviewOnly;
    }

    private static string RecommendSample(
        int rawCandidateCount,
        int eligibleCandidateCount,
        bool lowConfidence,
        int mustHitHits,
        int mustHitCount,
        int riskAfterPolicy)
    {
        if (rawCandidateCount == 0)
        {
            return VectorQueryShadowRecommendations.NeedsMoreIndexedData;
        }

        if (riskAfterPolicy > 0)
        {
            return VectorQueryShadowRecommendations.BlockedByRisk;
        }

        if (eligibleCandidateCount == 0)
        {
            return VectorQueryShadowRecommendations.NeedsPolicyTuning;
        }

        if (lowConfidence || (mustHitCount > 0 && mustHitHits == 0))
        {
            return VectorQueryShadowRecommendations.NeedsPolicyTuning;
        }

        return VectorQueryShadowRecommendations.KeepPreviewOnly;
    }

    private static bool EvalIdMatches(string expected, string candidateId)
    {
        return string.Equals(expected, candidateId, StringComparison.OrdinalIgnoreCase)
               || candidateId.EndsWith(expected, StringComparison.OrdinalIgnoreCase)
               || expected.EndsWith(candidateId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDeprecatedHit(VectorQueryPreviewCandidate candidate)
    {
        return candidate.IsLifecycleRisk
               || string.Equals(candidate.Layer, "historical_context", StringComparison.OrdinalIgnoreCase)
               || string.Equals(candidate.Layer, "deprecated_evidence", StringComparison.OrdinalIgnoreCase)
               || candidate.Metadata.TryGetValue("lifecycle", out var lifecycle)
               && string.Equals(lifecycle, "Deprecated", StringComparison.OrdinalIgnoreCase);
    }

    private static void Add(IDictionary<string, int> target, string key, int count)
    {
        if (count > 0)
        {
            target[key] = count;
        }
    }

    private static string Ids(IReadOnlyList<string> ids)
    {
        return ids.Count == 0 ? "-" : string.Join("<br>", ids.Take(5));
    }
}
