using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>Query intent expansion 的离线 shadow runner；不改变正式 retrieval、scoring 或 package。</summary>
public sealed class VectorQueryExpansionShadowRunner
{
    private const double ReadinessRecallThreshold = 0.80;
    private const double LowConfidenceThreshold = 0.25;

    private readonly VectorQueryPreviewService? _previewService;
    private readonly VectorQueryExpansionService _expansionService;

    public VectorQueryExpansionShadowRunner(
        VectorQueryPreviewService? previewService,
        VectorQueryExpansionService? expansionService = null)
    {
        _previewService = previewService;
        _expansionService = expansionService ?? new VectorQueryExpansionService();
    }

    public async Task<VectorQueryExpansionShadowReport> RunAsync(
        IReadOnlyList<ContextEvalSample> samples,
        string workspaceId,
        string collectionId,
        int topK = 10,
        string? vectorProfileId = null,
        double? minSimilarity = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        if (_previewService is null)
        {
            return NewUnavailableReport(samples.Count, string.Empty, string.Empty, "当前 runner 未注册 VectorQueryPreviewService。");
        }

        var operationId = $"vector-query-expansion-shadow-{Guid.NewGuid():N}";
        var resolvedVectorProfile = string.IsNullOrWhiteSpace(vectorProfileId)
            ? VectorQueryProfileIds.NormalV1
            : vectorProfileId;
        var expansionProfiles = _expansionService.GetProfiles();
        var baselineSamples = new List<VectorQueryShadowEvalSample>();
        var expandedSamples = expansionProfiles.ToDictionary(
            profile => profile.ProfileId,
            _ => new List<VectorQueryShadowEvalSample>(),
            StringComparer.OrdinalIgnoreCase);
        var recoveredByProfile = expansionProfiles.ToDictionary(
            profile => profile.ProfileId,
            _ => 0,
            StringComparer.OrdinalIgnoreCase);
        var newRiskByProfile = expansionProfiles.ToDictionary(
            profile => profile.ProfileId,
            _ => 0,
            StringComparer.OrdinalIgnoreCase);
        var queryIntentRecoveredByProfile = expansionProfiles.ToDictionary(
            profile => profile.ProfileId,
            _ => 0,
            StringComparer.OrdinalIgnoreCase);
        var providerId = string.Empty;
        var embeddingModel = string.Empty;
        var warnings = new List<string>();

        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var baselinePreview = await PreviewAsync(
                operationId,
                sample,
                sample.Query,
                workspaceId,
                collectionId,
                resolvedVectorProfile,
                topK,
                minSimilarity,
                "raw",
                cancellationToken).ConfigureAwait(false);
            providerId = ResolveProviderId(providerId, baselinePreview);
            embeddingModel = ResolveEmbeddingModel(embeddingModel, baselinePreview);
            var baselineEval = VectorQueryShadowEvalRunner.BuildSampleResult(sample, baselinePreview, LowConfidenceThreshold);
            baselineSamples.Add(baselineEval);

            foreach (var profile in expansionProfiles)
            {
                var expansion = _expansionService.Expand(BuildExpansionRequest(sample), profile.ProfileId);
                warnings.AddRange(expansion.Warnings);
                var expandedPreview = await PreviewAsync(
                    operationId,
                    sample,
                    expansion.ExpandedQuery,
                    workspaceId,
                    collectionId,
                    resolvedVectorProfile,
                    topK,
                    minSimilarity,
                    profile.ProfileId,
                    cancellationToken).ConfigureAwait(false);
                providerId = ResolveProviderId(providerId, expandedPreview);
                embeddingModel = ResolveEmbeddingModel(embeddingModel, expandedPreview);
                var expandedEval = VectorQueryShadowEvalRunner.BuildSampleResult(sample, expandedPreview, LowConfidenceThreshold);
                expandedSamples[profile.ProfileId].Add(expandedEval);

                var recovered = CountRecoveredMustHits(baselineEval, expandedEval);
                recoveredByProfile[profile.ProfileId] += recovered;
                if (expandedEval.RiskAfterPolicy > baselineEval.RiskAfterPolicy)
                {
                    newRiskByProfile[profile.ProfileId]++;
                }

                if (recovered > 0 && HasRuntimeIntent(sample) && ProfileUsesIntent(profile))
                {
                    queryIntentRecoveredByProfile[profile.ProfileId] += recovered;
                }
            }
        }

        var baselineReport = VectorQueryShadowEvalRunner.BuildReport($"{operationId}:baseline", baselineSamples);
        var results = expansionProfiles
            .Select(profile => BuildResult(
                profile.ProfileId,
                baselineSamples,
                baselineReport,
                expandedSamples[profile.ProfileId],
                recoveredByProfile[profile.ProfileId],
                newRiskByProfile[profile.ProfileId],
                queryIntentRecoveredByProfile[profile.ProfileId]))
            .OrderByDescending(result => result.Recommendation == VectorQueryShadowRecommendations.ReadyForRetrievalShadow)
            .ThenBy(result => result.RiskAfterPolicy)
            .ThenBy(result => result.NewRiskCount)
            .ThenByDescending(result => result.RecallAfterExpansion)
            .ThenByDescending(result => result.MrrAfterExpansion)
            .ToArray();
        var best = SelectBestResult(results);

        return new VectorQueryExpansionShadowReport
        {
            OperationId = operationId,
            CreatedAt = DateTimeOffset.UtcNow,
            Samples = samples.Count,
            ProviderId = providerId,
            EmbeddingModel = embeddingModel,
            VectorProfileId = resolvedVectorProfile,
            TopK = Math.Clamp(topK > 0 ? topK : 10, 1, 1000),
            MinSimilarity = minSimilarity,
            Results = results,
            BestResult = best,
            Recommendation = best?.Recommendation ?? VectorQueryShadowRecommendations.KeepPreviewOnly,
            FormalOutputChanged = 0,
            Warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    public static VectorQueryExpansionShadowReport NewUnavailableReport(
        int sampleCount,
        string providerId,
        string embeddingModel,
        string reason)
    {
        return new VectorQueryExpansionShadowReport
        {
            OperationId = $"vector-query-expansion-shadow-unavailable-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            Samples = sampleCount,
            ProviderId = providerId,
            EmbeddingModel = embeddingModel,
            Recommendation = VectorQueryShadowRecommendations.NeedsMoreIndexedData,
            FormalOutputChanged = 0,
            Warnings = string.IsNullOrWhiteSpace(reason) ? Array.Empty<string>() : [reason]
        };
    }

    public static string BuildMarkdownReport(
        VectorQueryExpansionShadowReport a3,
        VectorQueryExpansionShadowReport extended)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Query Expansion Shadow Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {DateTimeOffset.UtcNow:O}");
        builder.AppendLine();
        AppendReport(builder, "A3", a3);
        AppendReport(builder, "Extended", extended);
        return builder.ToString();
    }

    private async Task<VectorQueryPreviewResult> PreviewAsync(
        string operationId,
        ContextEvalSample sample,
        string queryText,
        string workspaceId,
        string collectionId,
        string vectorProfileId,
        int topK,
        double? minSimilarity,
        string pass,
        CancellationToken cancellationToken)
    {
        return await _previewService!.PreviewAsync(new VectorQueryPreviewRequest
        {
            OperationId = $"{operationId}:{sample.Id}:{pass}",
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            QueryText = queryText,
            TopK = Math.Clamp(topK > 0 ? topK : 10, 1, 1000),
            ProfileId = vectorProfileId,
            MinSimilarity = minSimilarity,
            IncludeVector = false,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["mode"] = sample.Mode,
                ["intent"] = ResolveRuntimeIntent(sample),
                ["createdFrom"] = "vector_query_expansion_shadow"
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static VectorQueryExpansionRequest BuildExpansionRequest(ContextEvalSample sample)
    {
        var metadata = FilterRuntimeMetadata(sample.Metadata);
        var intent = ResolveRuntimeIntent(sample);
        var routerIntent = ResolveMetadata(sample.Metadata, "routerIntent", "planningIntent");
        var taskKind = ResolveMetadata(sample.Metadata, "taskKind", "task");

        return new VectorQueryExpansionRequest
        {
            QueryText = sample.Query,
            Mode = sample.Mode,
            Intent = intent,
            RouterIntent = routerIntent,
            TaskKind = taskKind,
            QueryAnchors = VectorMissSetRepresentationAuditRunner.ExtractAnchors(sample.Query, 16),
            WorkingMemoryAnchors = SplitMetadataValues(metadata, "workingAnchor", "workingMemoryAnchor"),
            ConstraintHints = SplitMetadataValues(metadata, "constraintHint", "constraintAnchor"),
            RequestMetadata = metadata
        };
    }

    private static VectorQueryExpansionShadowResult BuildResult(
        string profileId,
        IReadOnlyList<VectorQueryShadowEvalSample> baselineSamples,
        VectorQueryShadowEvalReport baselineReport,
        IReadOnlyList<VectorQueryShadowEvalSample> expandedSamples,
        int recoveredMissCount,
        int newRiskCount,
        int queryIntentMissingRecovered)
    {
        var expandedReport = VectorQueryShadowEvalRunner.BuildReport($"vector-query-expansion-shadow:{profileId}", expandedSamples);
        var mrrBefore = CalculateMrr(baselineSamples);
        var mrrAfter = CalculateMrr(expandedSamples);
        var recommendation = Recommend(expandedReport, recoveredMissCount, newRiskCount);

        return new VectorQueryExpansionShadowResult
        {
            ExpansionProfile = profileId,
            Samples = expandedSamples.Count,
            RecallBeforeExpansion = baselineReport.MustHitRecallAfterPolicy,
            RecallAfterExpansion = expandedReport.MustHitRecallAfterPolicy,
            MrrBeforeExpansion = mrrBefore,
            MrrAfterExpansion = mrrAfter,
            RiskAfterPolicy = expandedReport.RiskAfterPolicy,
            MustNotHitRiskAfterPolicy = expandedReport.MustNotHitRiskAfterPolicy,
            LifecycleRiskAfterPolicy = expandedReport.LifecycleRiskAfterPolicy,
            RecoveredMissCount = recoveredMissCount,
            NewRiskCount = newRiskCount,
            QueryIntentMissingRecovered = queryIntentMissingRecovered,
            NoCandidateCount = expandedReport.NoCandidateCount,
            Recommendation = recommendation
        };
    }

    private static VectorQueryExpansionShadowResult? SelectBestResult(
        IReadOnlyList<VectorQueryExpansionShadowResult> results)
    {
        return results
            .OrderByDescending(result => result.Recommendation == VectorQueryShadowRecommendations.ReadyForRetrievalShadow)
            .ThenBy(result => result.RiskAfterPolicy)
            .ThenBy(result => result.NewRiskCount)
            .ThenByDescending(result => result.RecallAfterExpansion)
            .ThenByDescending(result => result.MrrAfterExpansion)
            .FirstOrDefault();
    }

    private static string Recommend(
        VectorQueryShadowEvalReport report,
        int recoveredMissCount,
        int newRiskCount)
    {
        if (report.Samples == 0 || report.RawCandidateCount == 0 || report.NoCandidateCount > report.Samples / 2)
        {
            return VectorQueryShadowRecommendations.NeedsMoreIndexedData;
        }

        if (report.RiskAfterPolicy > 0
            || report.MustNotHitRiskAfterPolicy > 0
            || report.LifecycleRiskAfterPolicy > 0
            || newRiskCount > 0)
        {
            return VectorQueryShadowRecommendations.BlockedByRisk;
        }

        if (report.MustHitRecallAfterPolicy >= ReadinessRecallThreshold)
        {
            return VectorQueryShadowRecommendations.ReadyForRetrievalShadow;
        }

        return recoveredMissCount > 0
            ? VectorQueryShadowRecommendations.NeedsProfileTuning
            : VectorQueryShadowRecommendations.NeedsBetterEmbedding;
    }

    private static int CountRecoveredMustHits(
        VectorQueryShadowEvalSample baseline,
        VectorQueryShadowEvalSample expanded)
    {
        return baseline.MustHitMissing
            .Count(missing => expanded.MustHitMatchedAfterPolicy.Any(matched => EvalIdMatches(missing, matched)));
    }

    private static double CalculateMrr(IReadOnlyList<VectorQueryShadowEvalSample> samples)
    {
        if (samples.Count == 0)
        {
            return 0;
        }

        return samples.Average(sample =>
        {
            var ranks = sample.MustHitMatchedAfterPolicy
                .Select(matched => sample.Candidates
                    .Where(candidate => EvalIdMatches(matched, candidate.ItemId))
                    .Select(candidate => candidate.Rank > 0 ? candidate.Rank : candidate.RawRank)
                    .Where(rank => rank > 0)
                    .DefaultIfEmpty(0)
                    .Min())
                .Where(rank => rank > 0)
                .ToArray();
            return ranks.Length == 0 ? 0 : 1.0 / ranks.Min();
        });
    }

    private static bool EvalIdMatches(string expected, string candidateId)
    {
        return string.Equals(expected, candidateId, StringComparison.OrdinalIgnoreCase)
               || candidateId.EndsWith(expected, StringComparison.OrdinalIgnoreCase)
               || expected.EndsWith(candidateId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ProfileUsesIntent(VectorQueryExpansionProfile profile)
    {
        return profile.IncludeIntent || profile.IncludeTaskKind || profile.IncludePlanningContext;
    }

    private static bool HasRuntimeIntent(ContextEvalSample sample)
    {
        return !string.IsNullOrWhiteSpace(ResolveRuntimeIntent(sample))
               || !string.IsNullOrWhiteSpace(ResolveMetadata(sample.Metadata, "routerIntent", "planningIntent", "taskKind", "task"));
    }

    private static string ResolveRuntimeIntent(ContextEvalSample sample)
    {
        return ResolveMetadata(sample.Metadata, "routerIntent", "planningIntent", "intent");
    }

    private static string ResolveMetadata(
        IReadOnlyDictionary<string, string> metadata,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static Dictionary<string, string> FilterRuntimeMetadata(
        IReadOnlyDictionary<string, string> metadata)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in metadata)
        {
            if (IsForbiddenEvalMetadataKey(item.Key) || string.IsNullOrWhiteSpace(item.Value))
            {
                continue;
            }

            result[item.Key] = item.Value;
        }

        return result;
    }

    private static bool IsForbiddenEvalMetadataKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return true;
        }

        return key.Contains("sample", StringComparison.OrdinalIgnoreCase)
               || key.Contains("item", StringComparison.OrdinalIgnoreCase)
               || key.Contains("fixture", StringComparison.OrdinalIgnoreCase)
               || key.Contains("file", StringComparison.OrdinalIgnoreCase)
               || key.Contains("mustHit", StringComparison.OrdinalIgnoreCase)
               || key.Contains("mustNot", StringComparison.OrdinalIgnoreCase)
               || key.Contains("label", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> SplitMetadataValues(
        IReadOnlyDictionary<string, string> metadata,
        params string[] keys)
    {
        return metadata
            .Where(item => keys.Any(key => item.Key.Contains(key, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(item => item.Value.Split([' ', ',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolveProviderId(string current, VectorQueryPreviewResult preview)
    {
        return !string.IsNullOrWhiteSpace(current)
            ? current
            : preview.Candidates.FirstOrDefault()?.EmbeddingProvider ?? string.Empty;
    }

    private static string ResolveEmbeddingModel(string current, VectorQueryPreviewResult preview)
    {
        return !string.IsNullOrWhiteSpace(current)
            ? current
            : preview.Candidates.FirstOrDefault()?.EmbeddingModel ?? string.Empty;
    }

    private static void AppendReport(
        StringBuilder builder,
        string title,
        VectorQueryExpansionShadowReport report)
    {
        builder.AppendLine($"## {title} Summary");
        builder.AppendLine();
        builder.AppendLine($"- Samples: `{report.Samples}`");
        builder.AppendLine($"- Provider: `{Empty(report.ProviderId)}`");
        builder.AppendLine($"- Model: `{Empty(report.EmbeddingModel)}`");
        builder.AppendLine($"- VectorProfile: `{report.VectorProfileId}`");
        builder.AppendLine($"- TopK: `{report.TopK}`");
        builder.AppendLine($"- MinSimilarity: `{(report.MinSimilarity is null ? "-" : report.MinSimilarity.Value.ToString("F2"))}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        if (report.BestResult is not null)
        {
            builder.AppendLine($"- BestExpansionProfile: `{report.BestResult.ExpansionProfile}`");
            builder.AppendLine($"- RecallBefore: `{report.BestResult.RecallBeforeExpansion:P2}`");
            builder.AppendLine($"- RecallAfter: `{report.BestResult.RecallAfterExpansion:P2}`");
            builder.AppendLine($"- MRRBefore: `{report.BestResult.MrrBeforeExpansion:F4}`");
            builder.AppendLine($"- MRRAfter: `{report.BestResult.MrrAfterExpansion:F4}`");
            builder.AppendLine($"- RiskAfterPolicy: `{report.BestResult.RiskAfterPolicy}`");
        }

        builder.AppendLine();
        builder.AppendLine("| Profile | Recall Before | Recall After | MRR Before | MRR After | Risk | MustNot Risk | Lifecycle Risk | Recovered | Intent Recovered | New Risk | Recommendation |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var result in report.Results)
        {
            builder.AppendLine($"| {result.ExpansionProfile} | {result.RecallBeforeExpansion:P2} | {result.RecallAfterExpansion:P2} | {result.MrrBeforeExpansion:F4} | {result.MrrAfterExpansion:F4} | {result.RiskAfterPolicy} | {result.MustNotHitRiskAfterPolicy:P2} | {result.LifecycleRiskAfterPolicy:P2} | {result.RecoveredMissCount} | {result.QueryIntentMissingRecovered} | {result.NewRiskCount} | {result.Recommendation} |");
        }

        if (report.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("### Warnings");
            foreach (var warning in report.Warnings.Take(20))
            {
                builder.AppendLine($"- {warning}");
            }
        }

        builder.AppendLine();
    }

    private static string Empty(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }
}
