using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>向量查询 profile sweep runner；只做离线评估，不接正式检索。</summary>
public sealed class VectorQueryProfileSweepRunner
{
    private const int BaseTopK = 1000;
    private const double LowConfidenceThreshold = 0.25;
    private const double LowSeparationThreshold = 0.03;

    private static readonly string[] DefaultProfiles =
    [
        VectorQueryProfileIds.NormalV1,
        VectorQueryProfileIds.CurrentTaskV1,
        VectorQueryProfileIds.AuditV1,
        VectorQueryProfileIds.DiagnosticsV1
    ];

    private static readonly int[] DefaultTopKs = [3, 5, 10, 20];

    private static readonly double[] DefaultMinSimilarities = [0.10, 0.20, 0.30, 0.40, 0.50, 0.60, 0.70, 0.80];

    private static readonly string[] DefaultLayerFilters =
    [
        VectorQueryLayerFilters.All,
        VectorQueryLayerFilters.StableOnly,
        VectorQueryLayerFilters.CandidateStable,
        VectorQueryLayerFilters.ExcludeHistorical
    ];

    private readonly VectorQueryPreviewService _previewService;
    private readonly VectorQueryProfileRegistry _profileRegistry;
    private readonly VectorCandidateEligibilityPolicy _eligibilityPolicy;

    public VectorQueryProfileSweepRunner(
        VectorQueryPreviewService previewService,
        VectorQueryProfileRegistry? profileRegistry = null,
        VectorCandidateEligibilityPolicy? eligibilityPolicy = null)
    {
        _previewService = previewService;
        _profileRegistry = profileRegistry ?? new VectorQueryProfileRegistry();
        _eligibilityPolicy = eligibilityPolicy ?? new VectorCandidateEligibilityPolicy();
    }

    public async Task<VectorQueryProfileSweepReport> RunAsync(
        IReadOnlyList<ContextEvalSample> samples,
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);

        var operationId = $"vector-query-profile-sweep-{Guid.NewGuid():N}";
        var basePreviews = new List<BasePreview>();
        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preview = await _previewService.PreviewAsync(new VectorQueryPreviewRequest
            {
                OperationId = $"{operationId}:{sample.Id}",
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                QueryText = sample.Query,
                TopK = BaseTopK,
                ProfileId = VectorQueryProfileIds.DiagnosticsV1,
                IncludeVector = false,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["mode"] = sample.Mode,
                    ["createdFrom"] = "vector_query_profile_sweep"
                }
            }, cancellationToken).ConfigureAwait(false);
            basePreviews.Add(new BasePreview(sample, preview));
        }

        var results = new List<VectorQueryProfileSweepResult>();
        foreach (var profileId in DefaultProfiles)
        {
            var baseProfile = _profileRegistry.Resolve(profileId);
            foreach (var topK in DefaultTopKs)
            {
                foreach (var minSimilarity in DefaultMinSimilarities)
                {
                    var profile = WithMinSimilarity(baseProfile, minSimilarity);
                    foreach (var layerFilter in DefaultLayerFilters)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        results.Add(BuildResult(basePreviews, profile, topK, minSimilarity, layerFilter));
                    }
                }
            }
        }

        var best = SelectBest(results);
        return new VectorQueryProfileSweepReport
        {
            OperationId = operationId,
            CreatedAt = DateTimeOffset.UtcNow,
            Samples = samples.Count,
            Results = results,
            BestResult = best,
            Recommendation = best?.Recommendation ?? VectorQueryShadowRecommendations.NeedsMoreIndexedData,
            Warnings = BuildWarnings(results)
        };
    }

    public async Task<VectorEmbeddingQualityBaselineReport> BuildEmbeddingQualityBaselineAsync(
        IReadOnlyList<ContextEvalSample> samples,
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var operationId = $"vector-embedding-quality-baseline-{Guid.NewGuid():N}";
        var positives = new List<double>();
        var negatives = new List<double>();
        var pairCount = 0;
        var mustHitTotal = 0;
        var mustHitHit = 0;
        var mustNotTotal = 0;
        var mustNotHit = 0;
        var model = string.Empty;
        var provider = string.Empty;

        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preview = await _previewService.PreviewAsync(new VectorQueryPreviewRequest
            {
                OperationId = $"{operationId}:{sample.Id}",
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                QueryText = sample.Query,
                TopK = 20,
                ProfileId = VectorQueryProfileIds.DiagnosticsV1,
                IncludeVector = false,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["mode"] = sample.Mode,
                    ["createdFrom"] = "vector_embedding_quality_baseline"
                }
            }, cancellationToken).ConfigureAwait(false);

            var candidates = preview.Candidates;
            model = candidates.FirstOrDefault()?.EmbeddingModel ?? model;
            provider = candidates.FirstOrDefault()?.EmbeddingProvider ?? provider;
            var samplePositives = candidates
                .Where(candidate => sample.MustHit.Any(expected => EvalIdMatches(expected, candidate.ItemId)))
                .Select(candidate => candidate.Similarity)
                .ToArray();
            var sampleNegatives = candidates
                .Where(candidate => sample.MustNotHit.Any(expected => EvalIdMatches(expected, candidate.ItemId)))
                .Select(candidate => candidate.Similarity)
                .ToArray();
            positives.AddRange(samplePositives);
            negatives.AddRange(sampleNegatives);
            pairCount += samplePositives.Length * sampleNegatives.Length;
            mustHitTotal += sample.MustHit.Count;
            mustHitHit += sample.MustHit.Count(expected => candidates.Any(candidate => EvalIdMatches(expected, candidate.ItemId)));
            mustNotTotal += sample.MustNotHit.Count;
            mustNotHit += sample.MustNotHit.Count(expected => candidates.Any(candidate => EvalIdMatches(expected, candidate.ItemId)));
        }

        var positiveAverage = positives.Count == 0 ? 0 : positives.Average();
        var negativeAverage = negatives.Count == 0 ? 0 : negatives.Average();
        var separation = positiveAverage - negativeAverage;
        return new VectorEmbeddingQualityBaselineReport
        {
            OperationId = operationId,
            CreatedAt = DateTimeOffset.UtcNow,
            Samples = samples.Count,
            PairCount = pairCount,
            PositiveHitCount = positives.Count,
            NegativeHitCount = negatives.Count,
            PositiveAverageSimilarity = positiveAverage,
            NegativeAverageSimilarity = negativeAverage,
            SimilaritySeparation = separation,
            MustHitRecallAt20 = mustHitTotal == 0 ? 1.0 : (double)mustHitHit / mustHitTotal,
            MustNotHitRiskAt20 = mustNotTotal == 0 ? 0 : (double)mustNotHit / mustNotTotal,
            EmbeddingProvider = provider,
            EmbeddingModel = model,
            Recommendation = RecommendEmbeddingQuality(positives.Count, negatives.Count, separation)
        };
    }

    public static string BuildMarkdownReport(
        VectorQueryProfileSweepReport a3,
        VectorQueryProfileSweepReport extended,
        VectorEmbeddingQualityBaselineReport quality)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Query Profile Sweep Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {DateTimeOffset.UtcNow:O}");
        builder.AppendLine();
        AppendSweep(builder, "A3", a3);
        AppendSweep(builder, "Extended", extended);
        AppendQuality(builder, quality);
        return builder.ToString();
    }

    public static string BuildEmbeddingQualityMarkdown(VectorEmbeddingQualityBaselineReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Embedding Quality Baseline");
        builder.AppendLine();
        AppendQuality(builder, report);
        return builder.ToString();
    }

    private VectorQueryProfileSweepResult BuildResult(
        IReadOnlyList<BasePreview> basePreviews,
        VectorQueryProfile profile,
        int topK,
        double minSimilarity,
        string layerFilter)
    {
        var samples = new List<VectorQueryShadowEvalSample>();
        var positiveSimilarities = new List<double>();
        var negativeSimilarities = new List<double>();
        foreach (var item in basePreviews)
        {
            var candidates = item.Preview.Candidates
                .Where(candidate => MatchesLayerFilter(candidate, layerFilter))
                .Take(topK)
                .Select((candidate, index) => ReevaluateCandidate(candidate, profile, index + 1))
                .ToArray();
            var preview = new VectorQueryPreviewResult
            {
                OperationId = $"{item.Preview.OperationId}:{profile.ProfileId}:{topK}:{minSimilarity:F2}:{layerFilter}",
                WorkspaceId = item.Preview.WorkspaceId,
                CollectionId = item.Preview.CollectionId,
                QueryText = item.Preview.QueryText,
                TopK = topK,
                ProfileId = profile.ProfileId,
                MinSimilarity = minSimilarity,
                Candidates = candidates,
                Diagnostics = item.Preview.Diagnostics,
                CreatedAt = item.Preview.CreatedAt
            };
            samples.Add(VectorQueryShadowEvalRunner.BuildSampleResult(item.Sample, preview, LowConfidenceThreshold));
            positiveSimilarities.AddRange(candidates
                .Where(candidate => item.Sample.MustHit.Any(expected => EvalIdMatches(expected, candidate.ItemId)))
                .Select(candidate => candidate.Similarity));
            negativeSimilarities.AddRange(candidates
                .Where(candidate => item.Sample.MustNotHit.Any(expected => EvalIdMatches(expected, candidate.ItemId)))
                .Select(candidate => candidate.Similarity));
        }

        var summary = VectorQueryShadowEvalRunner.BuildReport("vector-query-profile-sweep-result", samples);
        var riskByType = VectorResidualRiskAuditRunner.BuildRiskTypeCounts(samples);
        var riskMargin = VectorResidualRiskAuditRunner.CalculateRiskSimilarityMargin(samples);
        var positiveAverage = positiveSimilarities.Count == 0 ? 0 : positiveSimilarities.Average();
        var negativeAverage = negativeSimilarities.Count == 0 ? 0 : negativeSimilarities.Average();
        var separation = positiveAverage - negativeAverage;
        return new VectorQueryProfileSweepResult
        {
            ConfigurationId = $"{profile.ProfileId}:top{topK}:min{minSimilarity:F2}:{layerFilter}",
            Samples = summary.Samples,
            ProfileId = profile.ProfileId,
            TopK = topK,
            MinSimilarity = minSimilarity,
            LayerFilter = layerFilter,
            RawCandidateCount = summary.RawCandidateCount,
            EligibleCandidateCount = summary.EligibleCandidateCount,
            BlockedCandidateCount = summary.BlockedCandidateCount,
            MustHitRecallBeforePolicy = summary.MustHitRecallBeforePolicy,
            MustHitRecallAfterPolicy = summary.MustHitRecallAfterPolicy,
            MustHitMrrAfterPolicy = CalculateMrr(samples),
            MustNotHitRiskBeforePolicy = summary.MustNotHitRiskBeforePolicy,
            MustNotHitRiskAfterPolicy = summary.MustNotHitRiskAfterPolicy,
            LifecycleRiskBeforePolicy = summary.LifecycleRiskBeforePolicy,
            LifecycleRiskAfterPolicy = summary.LifecycleRiskAfterPolicy,
            RiskAfterPolicy = summary.RiskAfterPolicy,
            NoCandidateCount = summary.NoCandidateCount,
            LowConfidenceCount = summary.LowConfidenceCount,
            AverageTopSimilarity = summary.AverageTopSimilarity,
            PositiveAverageSimilarity = positiveAverage,
            NegativeAverageSimilarity = negativeAverage,
            SimilaritySeparation = separation,
            TopNoiseClusters = summary.TopNoiseClusters,
            BlockedByReason = summary.BlockedByReason,
            RiskAfterPolicyByType = riskByType,
            RecallLossAfterRepair = Math.Max(0, summary.MustHitRecallBeforePolicy - summary.MustHitRecallAfterPolicy),
            SimilarityMarginForRiskCandidates = riskMargin,
            Recommendation = RecommendSweepResult(summary, separation, riskByType)
        };
    }

    private VectorQueryPreviewCandidate ReevaluateCandidate(
        VectorQueryPreviewCandidate candidate,
        VectorQueryProfile profile,
        int rank)
    {
        var entry = new VectorIndexEntry
        {
            EntryId = candidate.EntryId,
            ItemId = candidate.ItemId,
            ItemKind = candidate.ItemKind,
            Layer = candidate.Layer,
            WorkspaceId = string.Empty,
            CollectionId = string.Empty,
            ContentHash = candidate.ContentHash,
            EmbeddingModel = candidate.EmbeddingModel,
            EmbeddingProvider = candidate.EmbeddingProvider,
            Dimension = candidate.Dimension,
            Metadata = new Dictionary<string, string>(candidate.Metadata, StringComparer.OrdinalIgnoreCase)
        };
        var eligibility = _eligibilityPolicy.Evaluate(profile, entry, candidate.Similarity, candidate.Diagnostics);
        return new VectorQueryPreviewCandidate
        {
            CandidateId = candidate.CandidateId,
            EntryId = candidate.EntryId,
            ItemId = candidate.ItemId,
            ItemKind = candidate.ItemKind,
            Layer = candidate.Layer,
            Rank = rank,
            RawRank = candidate.RawRank,
            Similarity = candidate.Similarity,
            ContentHash = candidate.ContentHash,
            EmbeddingModel = candidate.EmbeddingModel,
            EmbeddingProvider = candidate.EmbeddingProvider,
            Dimension = candidate.Dimension,
            IsDuplicate = candidate.IsDuplicate,
            IsStale = candidate.IsStale,
            IsOrphan = candidate.IsOrphan,
            IsLifecycleRisk = candidate.IsLifecycleRisk,
            Diagnostics = candidate.Diagnostics,
            EligibilityStatus = eligibility.EligibilityStatus,
            BlockedReasons = eligibility.BlockedReasons,
            TargetSection = eligibility.TargetSection,
            RiskIfNormalSelected = eligibility.RiskIfNormalSelected,
            RiskAfterPolicy = eligibility.RiskAfterPolicy,
            Metadata = new Dictionary<string, string>(candidate.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static VectorQueryProfile WithMinSimilarity(VectorQueryProfile profile, double minSimilarity)
    {
        return new VectorQueryProfile
        {
            ProfileId = profile.ProfileId,
            MinSimilarity = minSimilarity,
            AllowedLayers = profile.AllowedLayers,
            AllowedItemKinds = profile.AllowedItemKinds,
            AllowedSourceTypes = profile.AllowedSourceTypes,
            DiagnosticsOnlyItemKinds = profile.DiagnosticsOnlyItemKinds,
            RequireKnownLifecycle = profile.RequireKnownLifecycle,
            RequireCompleteLifecycleMetadata = profile.RequireCompleteLifecycleMetadata,
            AllowDeprecatedCandidates = profile.AllowDeprecatedCandidates,
            AllowHistoricalCandidates = profile.AllowHistoricalCandidates,
            AllowRejectedCandidates = profile.AllowRejectedCandidates,
            AllowCandidateLifecycle = profile.AllowCandidateLifecycle,
            DefaultTargetSection = profile.DefaultTargetSection,
            HistoricalTargetSection = profile.HistoricalTargetSection,
            DiagnosticsTargetSection = profile.DiagnosticsTargetSection
        };
    }

    private static VectorQueryProfileSweepResult? SelectBest(IReadOnlyList<VectorQueryProfileSweepResult> results)
    {
        return results
            .Where(result => result.RiskAfterPolicy == 0)
            .OrderByDescending(result => result.MustHitRecallAfterPolicy)
            .ThenByDescending(result => result.MustHitMrrAfterPolicy)
            .ThenByDescending(result => result.SimilaritySeparation)
            .ThenByDescending(result => result.EligibleCandidateCount)
            .ThenBy(result => result.TopK)
            .FirstOrDefault()
            ?? results.OrderBy(result => result.RiskAfterPolicy).FirstOrDefault();
    }

    private static IReadOnlyList<string> BuildWarnings(IReadOnlyList<VectorQueryProfileSweepResult> results)
    {
        if (results.Count == 0)
        {
            return ["未生成任何 sweep 结果。"];
        }

        var best = SelectBest(results);
        if (best is null)
        {
            return ["未找到可比较的 sweep 结果。"];
        }

        return best.Recommendation == VectorQueryShadowRecommendations.ReadyForRetrievalShadow
            ? Array.Empty<string>()
            : [$"当前最佳配置仍为 {best.Recommendation}，本阶段不得接入正式 retrieval。"];
    }

    private static string RecommendSweepResult(
        VectorQueryShadowEvalReport summary,
        double separation,
        IReadOnlyDictionary<string, int> riskByType)
    {
        if (summary.RawCandidateCount == 0 || summary.NoCandidateCount > summary.Samples / 2)
        {
            return VectorQueryShadowRecommendations.NeedsMoreIndexedData;
        }

        if (summary.RiskAfterPolicy > 0)
        {
            if (IsMetadataRepairable(riskByType))
            {
                return VectorQueryShadowRecommendations.NeedsPolicyTuning;
            }

            return RequiresReranker(riskByType)
                ? VectorQueryShadowRecommendations.RequiresReranker
                : VectorQueryShadowRecommendations.BlockedByRisk;
        }

        if (summary.EligibleCandidateCount == 0)
        {
            return VectorQueryShadowRecommendations.NeedsPolicyTuning;
        }

        if (separation <= LowSeparationThreshold && summary.MustHitRecallAfterPolicy < 0.5)
        {
            return VectorQueryShadowRecommendations.NeedsRealEmbeddingProvider;
        }

        if (summary.MustHitRecallAfterPolicy >= 0.8 && separation > LowSeparationThreshold)
        {
            return VectorQueryShadowRecommendations.ReadyForRetrievalShadow;
        }

        return summary.MustHitRecallAfterPolicy >= 0.5
            ? VectorQueryShadowRecommendations.KeepPreviewOnly
            : VectorQueryShadowRecommendations.NeedsPolicyTuning;
    }

    private static bool IsMetadataRepairable(IReadOnlyDictionary<string, int> riskByType)
    {
        return riskByType.Count > 0
               && riskByType.Keys.All(key =>
                   string.Equals(key, VectorResidualRiskTypes.DeprecatedMetadataGap, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(key, VectorResidualRiskTypes.LifecycleMetadataGap, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(key, VectorResidualRiskTypes.SupersededItemAllowed, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(key, VectorResidualRiskTypes.HistoricalItemAllowed, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(key, VectorResidualRiskTypes.ProfileTooBroad, StringComparison.OrdinalIgnoreCase));
    }

    private static bool RequiresReranker(IReadOnlyDictionary<string, int> riskByType)
    {
        return riskByType.Keys.Any(key =>
            string.Equals(key, VectorResidualRiskTypes.SemanticOvermatch, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, VectorResidualRiskTypes.SameTopicWrongIntent, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, VectorResidualRiskTypes.LowMarginAmbiguity, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, VectorResidualRiskTypes.RequiresReranker, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, VectorResidualRiskTypes.RequiresHumanPolicyDecision, StringComparison.OrdinalIgnoreCase));
    }

    private static string RecommendEmbeddingQuality(int positiveCount, int negativeCount, double separation)
    {
        if (positiveCount == 0)
        {
            return VectorQueryShadowRecommendations.NeedsMoreIndexedData;
        }

        if (negativeCount > 0 && separation <= LowSeparationThreshold)
        {
            return VectorQueryShadowRecommendations.NeedsRealEmbeddingProvider;
        }

        return VectorQueryShadowRecommendations.KeepPreviewOnly;
    }

    private static double CalculateMrr(IReadOnlyList<VectorQueryShadowEvalSample> samples)
    {
        if (samples.Count == 0)
        {
            return 0;
        }

        var total = 0.0;
        foreach (var sample in samples)
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

        return total / samples.Count;
    }

    private static bool MatchesLayerFilter(VectorQueryPreviewCandidate candidate, string filter)
    {
        return filter switch
        {
            VectorQueryLayerFilters.StableOnly => IsStableLayer(candidate.Layer),
            VectorQueryLayerFilters.CandidateStable => IsStableLayer(candidate.Layer) || IsCandidateLayer(candidate.Layer),
            VectorQueryLayerFilters.ExcludeHistorical => !IsHistoricalCandidate(candidate),
            _ => true
        };
    }

    private static bool IsStableLayer(string layer)
    {
        return layer.Contains("stable", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCandidateLayer(string layer)
    {
        return layer.Contains("candidate", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHistoricalCandidate(VectorQueryPreviewCandidate candidate)
    {
        return candidate.Layer.Contains("historical", StringComparison.OrdinalIgnoreCase)
               || candidate.Layer.Contains("deprecated", StringComparison.OrdinalIgnoreCase)
               || candidate.Metadata.TryGetValue("lifecycle", out var lifecycle)
               && (string.Equals(lifecycle, "Historical", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(lifecycle, "Deprecated", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(lifecycle, "Superseded", StringComparison.OrdinalIgnoreCase));
    }

    private static bool EvalIdMatches(string expected, string candidateId)
    {
        return string.Equals(expected, candidateId, StringComparison.OrdinalIgnoreCase)
               || candidateId.EndsWith(expected, StringComparison.OrdinalIgnoreCase)
               || expected.EndsWith(candidateId, StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendSweep(StringBuilder builder, string title, VectorQueryProfileSweepReport report)
    {
        builder.AppendLine($"## {title} Sweep");
        builder.AppendLine();
        builder.AppendLine($"- Samples: `{report.Samples}`");
        builder.AppendLine($"- ProviderId: `{(string.IsNullOrWhiteSpace(report.ProviderId) ? "-" : report.ProviderId)}`");
        builder.AppendLine($"- ProviderType: `{(string.IsNullOrWhiteSpace(report.ProviderType) ? "-" : report.ProviderType)}`");
        builder.AppendLine($"- EmbeddingModel: `{(string.IsNullOrWhiteSpace(report.EmbeddingModel) ? "-" : report.EmbeddingModel)}`");
        builder.AppendLine($"- Dimension: `{report.Dimension}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- ResultCount: `{report.Results.Count}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        if (report.BestResult is not null)
        {
            builder.AppendLine($"- Best: `{report.BestResult.ConfigurationId}`");
            builder.AppendLine($"- BestRecallAfter: `{report.BestResult.MustHitRecallAfterPolicy:P2}`");
            builder.AppendLine($"- BestMRRAfter: `{report.BestResult.MustHitMrrAfterPolicy:F4}`");
            builder.AppendLine($"- BestRiskAfter: `{report.BestResult.RiskAfterPolicy}`");
            builder.AppendLine($"- BestSeparation: `{report.BestResult.SimilaritySeparation:F4}`");
        }

        builder.AppendLine();
        builder.AppendLine("| Profile | TopK | MinSim | Layer | RecallAfter | MRR | RiskAfter | Separation | Recommendation |");
        builder.AppendLine("|---|---:|---:|---|---:|---:|---:|---:|---|");
        foreach (var result in report.Results
                     .OrderByDescending(item => item.MustHitRecallAfterPolicy)
                     .ThenByDescending(item => item.MustHitMrrAfterPolicy)
                     .ThenBy(item => item.RiskAfterPolicy)
                     .Take(20))
        {
            builder.AppendLine($"| {result.ProfileId} | {result.TopK} | {result.MinSimilarity:F2} | {result.LayerFilter} | {result.MustHitRecallAfterPolicy:P2} | {result.MustHitMrrAfterPolicy:F4} | {result.RiskAfterPolicy} | {result.SimilaritySeparation:F4} | {result.Recommendation} |");
        }

        builder.AppendLine();
    }

    private static void AppendQuality(StringBuilder builder, VectorEmbeddingQualityBaselineReport report)
    {
        builder.AppendLine("## Embedding Quality Baseline");
        builder.AppendLine();
        builder.AppendLine($"- Samples: `{report.Samples}`");
        builder.AppendLine($"- Provider: `{report.EmbeddingProvider}`");
        builder.AppendLine($"- Model: `{report.EmbeddingModel}`");
        builder.AppendLine($"- PositiveAverageSimilarity: `{report.PositiveAverageSimilarity:F4}`");
        builder.AppendLine($"- NegativeAverageSimilarity: `{report.NegativeAverageSimilarity:F4}`");
        builder.AppendLine($"- SimilaritySeparation: `{report.SimilaritySeparation:F4}`");
        builder.AppendLine($"- MustHitRecallAt20: `{report.MustHitRecallAt20:P2}`");
        builder.AppendLine($"- MustNotHitRiskAt20: `{report.MustNotHitRiskAt20:P2}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
    }

    private sealed record BasePreview(ContextEvalSample Sample, VectorQueryPreviewResult Preview);
}

/// <summary>向量查询 sweep 的层过滤器标识。</summary>
public static class VectorQueryLayerFilters
{
    public const string All = "all";

    public const string StableOnly = "stable-only";

    public const string CandidateStable = "candidate-stable";

    public const string ExcludeHistorical = "exclude-historical";
}
