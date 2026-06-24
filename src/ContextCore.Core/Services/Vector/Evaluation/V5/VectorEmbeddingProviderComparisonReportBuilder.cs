using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>构建 embedding provider 离线比较报告；不接正式 retrieval。</summary>
public static class VectorEmbeddingProviderComparisonReportBuilder
{
    public static VectorEmbeddingProviderComparisonReport Build(
        IReadOnlyList<VectorEmbeddingProviderComparisonResult> providerResults)
    {
        ArgumentNullException.ThrowIfNull(providerResults);
        var results = providerResults.ToArray();
        var primary = results
            .Where(item => item.Diagnostics.All(diagnostic => !diagnostic.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(item => item.SimilaritySeparation)
            .ThenByDescending(item => item.MustHitRecallAfterPolicy)
            .FirstOrDefault()
            ?? results.FirstOrDefault()
            ?? new VectorEmbeddingProviderComparisonResult();

        return new VectorEmbeddingProviderComparisonReport
        {
            OperationId = $"vector-embedding-provider-comparison-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            ProviderId = primary.ProviderId,
            ProviderType = primary.ProviderType,
            EmbeddingModel = primary.EmbeddingModel,
            Dimension = primary.Dimension,
            IndexedItems = primary.IndexedItems,
            QueryCount = primary.QueryCount,
            AverageTopSimilarity = primary.AverageTopSimilarity,
            PositiveAverageSimilarity = primary.PositiveAverageSimilarity,
            NegativeAverageSimilarity = primary.NegativeAverageSimilarity,
            SimilaritySeparation = primary.SimilaritySeparation,
            MustHitRecallAtK = primary.MustHitRecallAfterPolicy,
            MustHitMrrAfterPolicy = primary.MustHitMrrAfterPolicy,
            MustNotHitRiskAfterPolicy = primary.MustNotHitRiskAfterPolicy,
            LifecycleRiskAfterPolicy = primary.LifecycleRiskAfterPolicy,
            EligibleCandidateCount = primary.EligibleCandidateCount,
            NoCandidateCount = primary.NoCandidateCount,
            Recommendation = ResolveOverallRecommendation(results),
            ProviderResults = results,
            Diagnostics = results.SelectMany(item => item.Diagnostics).ToArray(),
            Warnings = results.SelectMany(item => item.Warnings).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    public static VectorEmbeddingProviderComparisonReport Build(
        EmbeddingProviderOptions options,
        VectorIndexStatusResponse status,
        VectorEmbeddingQualityBaselineReport quality,
        VectorQueryShadowEvalReport shadow,
        IReadOnlyList<VectorIndexDiagnostic>? providerDiagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(shadow);

        var result = BuildResult(options, status, quality, shadow, providerDiagnostics);
        var report = Build([result]);

        return new VectorEmbeddingProviderComparisonReport
        {
            OperationId = report.OperationId,
            CreatedAt = report.CreatedAt,
            ProviderId = result.ProviderId,
            ProviderType = result.ProviderType,
            EmbeddingModel = result.EmbeddingModel,
            Dimension = result.Dimension,
            IndexedItems = result.IndexedItems,
            QueryCount = result.QueryCount,
            AverageTopSimilarity = result.AverageTopSimilarity,
            PositiveAverageSimilarity = result.PositiveAverageSimilarity,
            NegativeAverageSimilarity = result.NegativeAverageSimilarity,
            SimilaritySeparation = result.SimilaritySeparation,
            MustHitRecallAtK = result.MustHitRecallAfterPolicy,
            MustHitMrrAfterPolicy = result.MustHitMrrAfterPolicy,
            MustNotHitRiskAfterPolicy = result.MustNotHitRiskAfterPolicy,
            LifecycleRiskAfterPolicy = result.LifecycleRiskAfterPolicy,
            EligibleCandidateCount = result.EligibleCandidateCount,
            NoCandidateCount = result.NoCandidateCount,
            Recommendation = result.Recommendation,
            ProviderResults = report.ProviderResults,
            Diagnostics = result.Diagnostics,
            Warnings = result.Warnings
        };
    }

    public static VectorEmbeddingProviderComparisonResult BuildResult(
        EmbeddingProviderOptions options,
        VectorIndexStatusResponse status,
        VectorEmbeddingQualityBaselineReport quality,
        VectorQueryShadowEvalReport shadow,
        IReadOnlyList<VectorIndexDiagnostic>? providerDiagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(shadow);

        var diagnostics = providerDiagnostics ?? Array.Empty<VectorIndexDiagnostic>();
        var recommendation = diagnostics.Any(item => item.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase))
            ? VectorQueryShadowRecommendations.BlockedByRisk
            : ResolveRecommendation(quality, shadow);

        return new VectorEmbeddingProviderComparisonResult
        {
            ProviderId = string.IsNullOrWhiteSpace(options.ProviderId) ? status.Provider : options.ProviderId,
            ProviderType = string.IsNullOrWhiteSpace(options.ProviderType) ? EmbeddingProviderTypes.DeterministicHash : options.ProviderType,
            EmbeddingModel = string.IsNullOrWhiteSpace(options.EmbeddingModel) ? status.Model : options.EmbeddingModel,
            Dimension = options.Dimension > 0 ? options.Dimension : status.Dimension,
            IndexedItems = status.IndexedCount,
            QueryCount = quality.Samples,
            AverageTopSimilarity = shadow.AverageTopSimilarity,
            PositiveAverageSimilarity = quality.PositiveAverageSimilarity,
            NegativeAverageSimilarity = quality.NegativeAverageSimilarity,
            SimilaritySeparation = quality.SimilaritySeparation,
            MustHitRecallAfterPolicy = shadow.MustHitRecallAfterPolicy,
            MustHitMrrAfterPolicy = CalculateMrr(shadow),
            MustNotHitRiskAfterPolicy = shadow.MustNotHitRiskAfterPolicy,
            LifecycleRiskAfterPolicy = shadow.LifecycleRiskAfterPolicy,
            EligibleCandidateCount = shadow.EligibleCandidateCount,
            NoCandidateCount = shadow.NoCandidateCount,
            Recommendation = recommendation,
            Diagnostics = diagnostics,
            Warnings = BuildWarnings(quality, shadow, diagnostics)
        };
    }

    public static string ToMarkdown(VectorEmbeddingProviderComparisonReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Embedding Provider Comparison");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.CreatedAt:O}");
        builder.AppendLine();
        builder.AppendLine($"- ProviderId: `{report.ProviderId}`");
        builder.AppendLine($"- ProviderType: `{report.ProviderType}`");
        builder.AppendLine($"- EmbeddingModel: `{report.EmbeddingModel}`");
        builder.AppendLine($"- Dimension: `{report.Dimension}`");
        builder.AppendLine($"- IndexedItems: `{report.IndexedItems}`");
        builder.AppendLine($"- QueryCount: `{report.QueryCount}`");
        builder.AppendLine($"- AverageTopSimilarity: `{report.AverageTopSimilarity:F4}`");
        builder.AppendLine($"- PositiveAverageSimilarity: `{report.PositiveAverageSimilarity:F4}`");
        builder.AppendLine($"- NegativeAverageSimilarity: `{report.NegativeAverageSimilarity:F4}`");
        builder.AppendLine($"- SimilaritySeparation: `{report.SimilaritySeparation:F4}`");
        builder.AppendLine($"- MustHitRecallAtK: `{report.MustHitRecallAtK:P2}`");
        builder.AppendLine($"- MustHitMrrAfterPolicy: `{report.MustHitMrrAfterPolicy:F4}`");
        builder.AppendLine($"- MustNotHitRiskAfterPolicy: `{report.MustNotHitRiskAfterPolicy:P2}`");
        builder.AppendLine($"- LifecycleRiskAfterPolicy: `{report.LifecycleRiskAfterPolicy:P2}`");
        builder.AppendLine($"- EligibleCandidateCount: `{report.EligibleCandidateCount}`");
        builder.AppendLine($"- NoCandidateCount: `{report.NoCandidateCount}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Provider Results");
        builder.AppendLine();
        if (report.ProviderResults.Count == 0)
        {
            builder.AppendLine("- none");
        }
        else
        {
            builder.AppendLine("| Provider | Type | Model | Dim | Indexed | Recall | MRR | Sep | Eligible | NoCandidate | Risk | Recommendation |");
            builder.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---|");
            foreach (var result in report.ProviderResults)
            {
                builder.AppendLine($"| {result.ProviderId} | {result.ProviderType} | {result.EmbeddingModel} | {result.Dimension} | {result.IndexedItems} | {result.MustHitRecallAfterPolicy:P2} | {result.MustHitMrrAfterPolicy:F4} | {result.SimilaritySeparation:F4} | {result.EligibleCandidateCount} | {result.NoCandidateCount} | {result.MustNotHitRiskAfterPolicy + result.LifecycleRiskAfterPolicy:P2} | {result.Recommendation} |");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Diagnostics");
        builder.AppendLine();
        if (report.Diagnostics.Count == 0)
        {
            builder.AppendLine("- none");
        }
        else
        {
            builder.AppendLine("| Type | Severity | Message | SuggestedAction |");
            builder.AppendLine("|---|---|---|---|");
            foreach (var diagnostic in report.Diagnostics)
            {
                builder.AppendLine($"| {diagnostic.Type} | {diagnostic.Severity} | {Escape(diagnostic.Message)} | {Escape(diagnostic.SuggestedAction)} |");
            }
        }

        return builder.ToString();
    }

    private static string ResolveRecommendation(
        VectorEmbeddingQualityBaselineReport quality,
        VectorQueryShadowEvalReport shadow)
    {
        if (shadow.RiskAfterPolicy > 0)
        {
            return VectorQueryShadowRecommendations.BlockedByRisk;
        }

        return quality.Recommendation;
    }

    private static string ResolveOverallRecommendation(IReadOnlyList<VectorEmbeddingProviderComparisonResult> results)
    {
        if (results.Count == 0)
        {
            return VectorQueryShadowRecommendations.NeedsMoreIndexedData;
        }

        if (results.Any(item => item.Recommendation == VectorQueryShadowRecommendations.ReadyForRetrievalShadow))
        {
            return VectorQueryShadowRecommendations.ReadyForRetrievalShadow;
        }

        if (results.All(item => item.Diagnostics.Any(diagnostic => diagnostic.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase))))
        {
            return VectorQueryShadowRecommendations.BlockedByRisk;
        }

        if (results.Any(item => item.Recommendation == VectorQueryShadowRecommendations.BlockedByRisk))
        {
            return VectorQueryShadowRecommendations.BlockedByRisk;
        }

        if (results.Any(item => item.Recommendation == VectorQueryShadowRecommendations.KeepPreviewOnly))
        {
            return VectorQueryShadowRecommendations.KeepPreviewOnly;
        }

        if (results.Any(item => item.Recommendation == VectorQueryShadowRecommendations.NeedsRealEmbeddingProvider))
        {
            return VectorQueryShadowRecommendations.NeedsRealEmbeddingProvider;
        }

        return results.First().Recommendation;
    }

    private static double CalculateMrr(VectorQueryShadowEvalReport shadow)
    {
        if (shadow.SampleResults.Count == 0)
        {
            return 0;
        }

        var total = 0.0;
        foreach (var sample in shadow.SampleResults)
        {
            var eligible = sample.Candidates
                .Where(candidate => string.Equals(candidate.EligibilityStatus, VectorCandidateEligibilityStatuses.Eligible, StringComparison.OrdinalIgnoreCase))
                .OrderBy(candidate => candidate.Rank)
                .ToArray();
            var rank = eligible
                .Select((candidate, index) => new { candidate, index })
                .FirstOrDefault(item => sample.MustHitMatchedAfterPolicy.Any(expected => EvalIdMatches(expected, item.candidate.ItemId)))
                ?.index + 1;
            total += rank is null ? 0 : 1.0 / rank.Value;
        }

        return total / shadow.SampleResults.Count;
    }

    private static bool EvalIdMatches(string expected, string candidateId)
    {
        return string.Equals(expected, candidateId, StringComparison.OrdinalIgnoreCase)
               || candidateId.EndsWith(expected, StringComparison.OrdinalIgnoreCase)
               || expected.EndsWith(candidateId, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> BuildWarnings(
        VectorEmbeddingQualityBaselineReport quality,
        VectorQueryShadowEvalReport shadow,
        IReadOnlyList<VectorIndexDiagnostic> diagnostics)
    {
        var warnings = new List<string>();
        if (diagnostics.Count > 0)
        {
            warnings.Add("当前 embedding provider 存在 diagnostics，禁止进入正式 retrieval。");
        }

        if (quality.Recommendation == VectorQueryShadowRecommendations.NeedsRealEmbeddingProvider)
        {
            warnings.Add("当前 embedding 语义区分能力不足，需要真实 provider 或更高质量模型。");
        }

        if (shadow.RiskAfterPolicy > 0)
        {
            warnings.Add("vector candidate policy 后仍存在风险，必须保持 preview-only。");
        }

        return warnings;
    }

    private static string Escape(string value)
    {
        return value.Replace("|", "/");
    }
}
