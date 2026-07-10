using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>Vector + ranker fusion 的离线 shadow runner；只读评估，不改变正式检索、排序或打包。</summary>
public sealed class VectorRankerFusionShadowRunner
{
    private const int CandidatePoolFloor = 50;
    private const double ReadinessRecallThreshold = 0.80;

    private static readonly string[] Strategies =
    [
        VectorRankerFusionStrategies.VectorOnly,
        VectorRankerFusionStrategies.RankerOnly,
        VectorRankerFusionStrategies.UnionThenRank,
        VectorRankerFusionStrategies.VectorBoostedRanker,
        VectorRankerFusionStrategies.RankerFilteredVector,
        VectorRankerFusionStrategies.LifecycleAwareFusion
    ];

    private readonly VectorQueryPreviewService _previewService;

    public VectorRankerFusionShadowRunner(VectorQueryPreviewService previewService)
    {
        _previewService = previewService;
    }

    public async Task<VectorRankerFusionShadowReport> RunAsync(
        IReadOnlyList<ContextEvalSample> samples,
        string workspaceId,
        string collectionId,
        int topK = 10,
        string? profileId = null,
        double? minSimilarity = null,
        double lowConfidenceThreshold = 0.25,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);

        var operationId = $"vector-ranker-fusion-shadow-{Guid.NewGuid():N}";
        var finalTopK = Math.Clamp(topK > 0 ? topK : 10, 1, 100);
        var poolTopK = Math.Clamp(Math.Max(CandidatePoolFloor, finalTopK * 5), finalTopK, 1000);
        var resolvedProfileId = string.IsNullOrWhiteSpace(profileId)
            ? VectorQueryProfileIds.NormalV1
            : profileId;
        var samplesByStrategy = Strategies.ToDictionary(
            strategy => strategy,
            _ => new List<VectorRankerFusionShadowSample>(),
            StringComparer.OrdinalIgnoreCase);
        var providerId = string.Empty;
        var embeddingModel = string.Empty;

        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preview = await _previewService.PreviewAsync(new VectorQueryPreviewRequest
            {
                OperationId = $"{operationId}:{sample.Id}",
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                QueryText = sample.Query,
                TopK = poolTopK,
                ProfileId = resolvedProfileId,
                MinSimilarity = minSimilarity,
                IncludeVector = false,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["mode"] = sample.Mode,
                    ["createdFrom"] = "vector_ranker_fusion_shadow"
                }
            }, cancellationToken).ConfigureAwait(false);

            providerId = ResolveProviderId(providerId, preview);
            embeddingModel = ResolveEmbeddingModel(embeddingModel, preview);
            var vectorOnly = BuildVectorOnlyPreview(preview, finalTopK);
            var vectorOnlyEval = VectorQueryShadowEvalRunner.BuildSampleResult(sample, vectorOnly, lowConfidenceThreshold);
            foreach (var strategy in Strategies)
            {
                var fusedPreview = string.Equals(strategy, VectorRankerFusionStrategies.VectorOnly, StringComparison.OrdinalIgnoreCase)
                    ? vectorOnly
                    : BuildFusionPreview(preview, strategy, finalTopK);
                var fusedEval = VectorQueryShadowEvalRunner.BuildSampleResult(sample, fusedPreview, lowConfidenceThreshold);
                samplesByStrategy[strategy].Add(BuildSample(sample, strategy, resolvedProfileId, finalTopK, minSimilarity, vectorOnlyEval, fusedEval, fusedPreview));
            }
        }

        var results = samplesByStrategy
            .Select(item => BuildStrategyResult(item.Key, resolvedProfileId, finalTopK, minSimilarity, item.Value))
            .OrderByDescending(item => item.Recommendation == VectorQueryShadowRecommendations.ReadyForRetrievalShadow)
            .ThenBy(item => item.MustNotHitRiskFusion)
            .ThenBy(item => item.LifecycleRiskFusion)
            .ThenByDescending(item => item.MustHitRecallFusion)
            .ThenByDescending(item => item.MustHitMrrFusion)
            .ToArray();
        var best = SelectBestResult(results);

        return new VectorRankerFusionShadowReport
        {
            OperationId = operationId,
            CreatedAt = DateTimeOffset.UtcNow,
            Samples = samples.Count,
            ProviderId = providerId,
            EmbeddingModel = embeddingModel,
            ProfileId = resolvedProfileId,
            TopK = finalTopK,
            MinSimilarity = minSimilarity,
            Results = results,
            BestResult = best,
            Recommendation = best?.Recommendation ?? VectorQueryShadowRecommendations.KeepPreviewOnly,
            FormalOutputChanged = 0,
            Warnings = BuildWarnings(best, poolTopK, finalTopK)
        };
    }

    public static VectorRankerFusionShadowReport NewUnavailableReport(
        int sampleCount,
        string providerId,
        string embeddingModel,
        string reason)
    {
        return new VectorRankerFusionShadowReport
        {
            OperationId = $"vector-ranker-fusion-shadow-unavailable-{Guid.NewGuid():N}",
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
        VectorRankerFusionShadowReport a3,
        VectorRankerFusionShadowReport extended)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Vector + Ranker Fusion Shadow Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {DateTimeOffset.UtcNow:O}");
        builder.AppendLine();
        AppendReport(builder, "A3", a3);
        AppendReport(builder, "Extended", extended);
        return builder.ToString();
    }

    private static void AppendReport(StringBuilder builder, string title, VectorRankerFusionShadowReport report)
    {
        builder.AppendLine($"## {title} Summary");
        builder.AppendLine();
        builder.AppendLine($"- Samples: `{report.Samples}`");
        builder.AppendLine($"- Provider: `{Empty(report.ProviderId)}`");
        builder.AppendLine($"- Model: `{Empty(report.EmbeddingModel)}`");
        builder.AppendLine($"- Profile: `{report.ProfileId}`");
        builder.AppendLine($"- TopK: `{report.TopK}`");
        builder.AppendLine($"- MinSimilarity: `{(report.MinSimilarity is null ? "-" : report.MinSimilarity.Value.ToString("F2"))}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        if (report.BestResult is not null)
        {
            builder.AppendLine($"- BestStrategy: `{report.BestResult.Strategy}`");
            builder.AppendLine($"- BestRecallFusion: `{report.BestResult.MustHitRecallFusion:P2}`");
            builder.AppendLine($"- BestMRRFusion: `{report.BestResult.MustHitMrrFusion:F4}`");
            builder.AppendLine($"- BestRiskFusion: `{report.BestResult.MustNotHitRiskFusion:P2}`");
            builder.AppendLine($"- BestLifecycleRisk: `{report.BestResult.LifecycleRiskFusion:P2}`");
        }

        builder.AppendLine();
        builder.AppendLine("| Strategy | Vector Recall | Fusion Recall | Vector MRR | Fusion MRR | Fusion Risk | Lifecycle Risk | Gain | Recommendation |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var result in report.Results)
        {
            builder.AppendLine($"| {result.Strategy} | {result.MustHitRecallVectorOnly:P2} | {result.MustHitRecallFusion:P2} | {result.MustHitMrrVectorOnly:F4} | {result.MustHitMrrFusion:F4} | {result.MustNotHitRiskFusion:P2} | {result.LifecycleRiskFusion:P2} | {result.RecallGain:P2} | {result.Recommendation} |");
        }

        builder.AppendLine();
        builder.AppendLine("### Top Fixed Samples");
        builder.AppendLine();
        builder.AppendLine("| Strategy | Sample | Mode | Intent | Gained | RecallGain |");
        builder.AppendLine("|---|---|---|---|---|---:|");
        foreach (var sample in report.Results.SelectMany(result => result.TopFixedSamples.Select(item => (result.Strategy, Sample: item))).Take(20))
        {
            builder.AppendLine($"| {sample.Strategy} | {sample.Sample.SampleId} | {sample.Sample.Mode} | {sample.Sample.Intent} | {Ids(sample.Sample.MustHitGained)} | {sample.Sample.RecallGain:P2} |");
        }

        builder.AppendLine();
        builder.AppendLine("### Newly Risky Samples");
        builder.AppendLine();
        builder.AppendLine("| Strategy | Sample | Mode | Intent | RiskDelta | Items |");
        builder.AppendLine("|---|---|---|---|---:|---|");
        foreach (var sample in report.Results.SelectMany(result => result.NewlyRiskySamples.Select(item => (result.Strategy, Sample: item))).Take(20))
        {
            builder.AppendLine($"| {sample.Strategy} | {sample.Sample.SampleId} | {sample.Sample.Mode} | {sample.Sample.Intent} | {sample.Sample.RiskDelta} | {Ids(sample.Sample.NewlyRiskyItems)} |");
        }

        if (report.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("### Warnings");
            foreach (var warning in report.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        builder.AppendLine();
    }

    private static VectorQueryPreviewResult BuildVectorOnlyPreview(
        VectorQueryPreviewResult preview,
        int finalTopK)
    {
        var candidates = preview.Candidates
            .OrderBy(candidate => candidate.Rank)
            .ThenByDescending(candidate => candidate.Similarity)
            .Take(finalTopK)
            .Select((candidate, index) => CloneCandidate(candidate, index + 1))
            .ToArray();
        return ClonePreview(preview, candidates, finalTopK);
    }

    private static VectorQueryPreviewResult BuildFusionPreview(
        VectorQueryPreviewResult preview,
        string strategy,
        int finalTopK)
    {
        var eligible = preview.Candidates
            .Where(IsEligible)
            .Select(candidate => new ScoredCandidate(candidate, BuildRankerScore(candidate)))
            .ToArray();
        var ordered = strategy switch
        {
            VectorRankerFusionStrategies.RankerOnly => eligible
                .OrderByDescending(item => item.Score.Value)
                .ThenByDescending(item => item.Candidate.Similarity),
            VectorRankerFusionStrategies.UnionThenRank => eligible
                .OrderByDescending(item => item.Score.Value + item.Candidate.Similarity)
                .ThenBy(item => item.Candidate.Rank),
            VectorRankerFusionStrategies.VectorBoostedRanker => eligible
                .OrderByDescending(item => item.Score.Value + item.Candidate.Similarity * 2.0)
                .ThenBy(item => item.Candidate.Rank),
            VectorRankerFusionStrategies.RankerFilteredVector => eligible
                .Where(item => item.Score.Value >= 0)
                .OrderBy(item => item.Candidate.Rank)
                .ThenByDescending(item => item.Candidate.Similarity),
            VectorRankerFusionStrategies.LifecycleAwareFusion => eligible
                .OrderByDescending(item => item.Score.Value + item.Candidate.Similarity + ResolveLifecycleBonus(item.Candidate))
                .ThenBy(item => item.Candidate.Rank),
            _ => eligible
                .OrderBy(item => item.Candidate.Rank)
                .ThenByDescending(item => item.Candidate.Similarity)
        };
        var candidates = ordered
            .Take(finalTopK)
            .Select((item, index) => CloneCandidate(
                item.Candidate,
                index + 1,
                item.Score.Value,
                item.Score.Value + item.Candidate.Similarity,
                item.Score.Reasons))
            .ToArray();
        return ClonePreview(preview, candidates, finalTopK);
    }

    private static VectorRankerFusionShadowSample BuildSample(
        ContextEvalSample sample,
        string strategy,
        string profileId,
        int topK,
        double? minSimilarity,
        VectorQueryShadowEvalSample vectorOnly,
        VectorQueryShadowEvalSample fusion,
        VectorQueryPreviewResult fusedPreview)
    {
        var vectorRecall = vectorOnly.MustHitCount == 0
            ? 1.0
            : (double)vectorOnly.MustHitHitCountAfterPolicy / vectorOnly.MustHitCount;
        var fusionRecall = fusion.MustHitCount == 0
            ? 1.0
            : (double)fusion.MustHitHitCountAfterPolicy / fusion.MustHitCount;
        var gained = fusion.MustHitMatchedAfterPolicy
            .Where(item => !vectorOnly.MustHitMatchedAfterPolicy.Contains(item, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var lost = vectorOnly.MustHitMatchedAfterPolicy
            .Where(item => !fusion.MustHitMatchedAfterPolicy.Contains(item, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var newlyRisky = fusion.MustNotHitMatchedAfterPolicy
            .Concat(fusion.LifecycleRiskItemsAfterPolicy)
            .Where(item => !vectorOnly.MustNotHitMatchedAfterPolicy.Contains(item, StringComparer.OrdinalIgnoreCase)
                           && !vectorOnly.LifecycleRiskItemsAfterPolicy.Contains(item, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var riskDelta = fusion.RiskAfterPolicy - vectorOnly.RiskAfterPolicy;

        return new VectorRankerFusionShadowSample
        {
            SampleId = sample.Id,
            Mode = sample.Mode,
            Intent = ResolveIntent(sample),
            QueryText = sample.Query,
            Strategy = strategy,
            ProfileId = profileId,
            TopK = topK,
            MinSimilarity = minSimilarity,
            VectorCandidateCount = vectorOnly.EligibleCandidateCount,
            FusionCandidateCount = fusion.EligibleCandidateCount,
            MustHitCount = fusion.MustHitCount,
            MustHitVectorOnlyHitCount = vectorOnly.MustHitHitCountAfterPolicy,
            MustHitFusionHitCount = fusion.MustHitHitCountAfterPolicy,
            MustHitMrrVectorOnly = CalculateSampleMrr(sample.MustHit, vectorOnly.Candidates),
            MustHitMrrFusion = CalculateSampleMrr(sample.MustHit, fusion.Candidates),
            MustNotHitVectorOnlyCount = vectorOnly.MustNotHitHitCountAfterPolicy,
            MustNotHitFusionCount = fusion.MustNotHitHitCountAfterPolicy,
            LifecycleRiskFusionCount = fusion.LifecycleRiskAfterPolicy,
            RecallGain = fusionRecall - vectorRecall,
            RiskDelta = riskDelta,
            IsFixed = gained.Length > 0 && riskDelta <= 0,
            IsNewlyRisky = riskDelta > 0 || newlyRisky.Length > 0,
            MustHitGained = gained,
            MustHitLost = lost,
            NewlyRiskyItems = newlyRisky,
            TopCandidates = fusedPreview.Candidates
                .Take(10)
                .Select(ToFusionCandidate)
                .ToArray()
        };
    }

    private static VectorRankerFusionStrategyResult BuildStrategyResult(
        string strategy,
        string profileId,
        int topK,
        double? minSimilarity,
        IReadOnlyList<VectorRankerFusionShadowSample> samples)
    {
        var mustHitCount = samples.Sum(item => item.MustHitCount);
        var vectorHits = samples.Sum(item => item.MustHitVectorOnlyHitCount);
        var fusionHits = samples.Sum(item => item.MustHitFusionHitCount);
        var mustNotVectorHits = samples.Sum(item => item.MustNotHitVectorOnlyCount);
        var mustNotFusionHits = samples.Sum(item => item.MustNotHitFusionCount);
        var lifecycleRisk = samples.Sum(item => item.LifecycleRiskFusionCount);
        var fusionCandidateCount = samples.Sum(item => item.FusionCandidateCount);
        var recommendation = Recommend(samples.Count, mustHitCount, fusionHits, mustNotFusionHits, lifecycleRisk, samples.Count(item => item.IsNewlyRisky));

        return new VectorRankerFusionStrategyResult
        {
            Strategy = strategy,
            ProfileId = profileId,
            TopK = topK,
            MinSimilarity = minSimilarity,
            Samples = samples.Count,
            VectorCandidateCount = samples.Sum(item => item.VectorCandidateCount),
            FusionCandidateCount = fusionCandidateCount,
            MustHitRecallVectorOnly = mustHitCount == 0 ? 1.0 : (double)vectorHits / mustHitCount,
            MustHitRecallFusion = mustHitCount == 0 ? 1.0 : (double)fusionHits / mustHitCount,
            MustHitMrrVectorOnly = samples.Count == 0 ? 0 : samples.Average(item => item.MustHitMrrVectorOnly),
            MustHitMrrFusion = samples.Count == 0 ? 0 : samples.Average(item => item.MustHitMrrFusion),
            MustNotHitRiskVectorOnly = samples.Count == 0 ? 0 : (double)mustNotVectorHits / samples.Count,
            MustNotHitRiskFusion = samples.Count == 0 ? 0 : (double)mustNotFusionHits / samples.Count,
            LifecycleRiskFusion = fusionCandidateCount == 0 ? 0 : (double)lifecycleRisk / fusionCandidateCount,
            RecallGain = mustHitCount == 0 ? 0 : (double)(fusionHits - vectorHits) / mustHitCount,
            RiskDelta = samples.Count == 0 ? 0 : samples.Average(item => item.RiskDelta),
            TopFixedSamples = samples
                .Where(item => item.IsFixed)
                .OrderByDescending(item => item.RecallGain)
                .ThenBy(item => item.SampleId, StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToArray(),
            NewlyRiskySamples = samples
                .Where(item => item.IsNewlyRisky)
                .OrderByDescending(item => item.RiskDelta)
                .ThenBy(item => item.SampleId, StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToArray(),
            Recommendation = recommendation
        };
    }

    private static VectorRankerFusionStrategyResult? SelectBestResult(IReadOnlyList<VectorRankerFusionStrategyResult> results)
    {
        return results
            .OrderByDescending(item => item.Recommendation == VectorQueryShadowRecommendations.ReadyForRetrievalShadow)
            .ThenBy(item => item.MustNotHitRiskFusion)
            .ThenBy(item => item.LifecycleRiskFusion)
            .ThenBy(item => item.NewlyRiskySamples.Count)
            .ThenByDescending(item => item.MustHitRecallFusion)
            .ThenByDescending(item => item.MustHitMrrFusion)
            .FirstOrDefault();
    }

    private static string Recommend(
        int sampleCount,
        int mustHitCount,
        int fusionHits,
        int mustNotFusionHits,
        int lifecycleRisk,
        int newlyRiskySamples)
    {
        if (sampleCount == 0 || mustHitCount == 0)
        {
            return VectorQueryShadowRecommendations.NeedsMoreIndexedData;
        }

        if (mustNotFusionHits > 0 || lifecycleRisk > 0 || newlyRiskySamples > 0)
        {
            return VectorQueryShadowRecommendations.BlockedByRisk;
        }

        var recall = (double)fusionHits / mustHitCount;
        if (recall >= ReadinessRecallThreshold)
        {
            return VectorQueryShadowRecommendations.ReadyForRetrievalShadow;
        }

        return recall >= 0.70
            ? VectorQueryShadowRecommendations.NeedsFusionTuning
            : VectorQueryShadowRecommendations.NeedsBetterEmbedding;
    }

    private static RankerScore BuildRankerScore(VectorQueryPreviewCandidate candidate)
    {
        var score = candidate.Similarity;
        var reasons = new List<string>();
        if (candidate.Metadata.TryGetValue("importance", out var importanceText)
            && double.TryParse(importanceText, out var importance))
        {
            score += Math.Clamp(importance, 0, 10) / 10.0;
            reasons.Add("importance");
        }

        if (candidate.Metadata.TryGetValue("confidence", out var confidenceText)
            && double.TryParse(confidenceText, out var confidence))
        {
            score += Math.Clamp(confidence, 0, 1) * 0.5;
            reasons.Add("confidence");
        }

        var lifecycle = ResolveLifecycle(candidate);
        if (IsActiveLifecycle(lifecycle))
        {
            score += 1.0;
            reasons.Add("activeLifecycle");
        }
        else if (IsRiskLifecycle(lifecycle))
        {
            score -= 3.0;
            reasons.Add("riskLifecycle");
        }
        else if (string.Equals(lifecycle, "Candidate", StringComparison.OrdinalIgnoreCase))
        {
            score -= 0.3;
            reasons.Add("candidateLifecycle");
        }

        if (candidate.Metadata.TryGetValue("reviewStatus", out var reviewStatus)
            && string.Equals(reviewStatus, "Reviewed", StringComparison.OrdinalIgnoreCase))
        {
            score += 0.4;
            reasons.Add("reviewed");
        }

        if (candidate.IsDuplicate || candidate.IsStale || candidate.IsOrphan || candidate.RiskAfterPolicy)
        {
            score -= 4.0;
            reasons.Add("diagnosticRisk");
        }

        if (string.Equals(candidate.TargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.2;
            reasons.Add("normalTarget");
        }
        else if (string.Equals(candidate.TargetSection, VectorQueryTargetSections.DiagnosticsOnly, StringComparison.OrdinalIgnoreCase))
        {
            score -= 1.0;
            reasons.Add("diagnosticsOnly");
        }

        return new RankerScore(score, reasons);
    }

    private static double ResolveLifecycleBonus(VectorQueryPreviewCandidate candidate)
    {
        var lifecycle = ResolveLifecycle(candidate);
        if (IsActiveLifecycle(lifecycle))
        {
            return 1.0;
        }

        if (IsRiskLifecycle(lifecycle))
        {
            return -4.0;
        }

        return 0;
    }

    private static VectorQueryPreviewResult ClonePreview(
        VectorQueryPreviewResult preview,
        IReadOnlyList<VectorQueryPreviewCandidate> candidates,
        int topK)
    {
        return new VectorQueryPreviewResult
        {
            OperationId = preview.OperationId,
            WorkspaceId = preview.WorkspaceId,
            CollectionId = preview.CollectionId,
            QueryText = preview.QueryText,
            TopK = topK,
            ProfileId = preview.ProfileId,
            Layer = preview.Layer,
            ItemKind = preview.ItemKind,
            MinSimilarity = preview.MinSimilarity,
            Candidates = candidates,
            Diagnostics = preview.Diagnostics,
            Warnings = preview.Warnings,
            CreatedAt = preview.CreatedAt
        };
    }

    private static VectorQueryPreviewCandidate CloneCandidate(
        VectorQueryPreviewCandidate candidate,
        int rank,
        double? rankerScore = null,
        double? fusionScore = null,
        IReadOnlyList<string>? scoreReasons = null)
    {
        var metadata = new Dictionary<string, string>(candidate.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["rankerScore"] = (rankerScore ?? candidate.Similarity).ToString("F6"),
            ["fusionScore"] = (fusionScore ?? candidate.Similarity).ToString("F6")
        };
        if (scoreReasons is { Count: > 0 })
        {
            metadata["fusionScoreReasons"] = string.Join(",", scoreReasons);
        }

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
            EligibilityStatus = candidate.EligibilityStatus,
            BlockedReasons = candidate.BlockedReasons,
            TargetSection = candidate.TargetSection,
            RiskIfNormalSelected = candidate.RiskIfNormalSelected,
            RiskAfterPolicy = candidate.RiskAfterPolicy,
            Metadata = metadata
        };
    }

    private static VectorRankerFusionCandidate ToFusionCandidate(VectorQueryPreviewCandidate candidate)
    {
        return new VectorRankerFusionCandidate
        {
            ItemId = candidate.ItemId,
            VectorRank = candidate.RawRank,
            FusionRank = candidate.Rank,
            Similarity = candidate.Similarity,
            RankerScore = TryGetDouble(candidate.Metadata, "rankerScore"),
            FusionScore = TryGetDouble(candidate.Metadata, "fusionScore"),
            Lifecycle = ResolveLifecycle(candidate),
            Layer = candidate.Layer,
            ItemKind = candidate.ItemKind,
            TargetSection = candidate.TargetSection,
            RiskAfterPolicy = candidate.RiskAfterPolicy,
            ScoreReasons = candidate.Metadata.TryGetValue("fusionScoreReasons", out var reasons)
                ? reasons.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : Array.Empty<string>()
        };
    }

    private static double CalculateSampleMrr(
        IReadOnlyList<string> expectedIds,
        IReadOnlyList<VectorQueryPreviewCandidate> candidates)
    {
        if (expectedIds.Count == 0)
        {
            return 1.0;
        }

        var rank = candidates
            .Where(IsEligible)
            .OrderBy(candidate => candidate.Rank)
            .Select((candidate, index) => new { candidate, index })
            .FirstOrDefault(item => expectedIds.Any(expected => EvalIdMatches(expected, item.candidate.ItemId)))
            ?.index + 1;
        return rank is null ? 0 : 1.0 / rank.Value;
    }

    private static string ResolveIntent(ContextEvalSample sample)
    {
        if (sample.Metadata.TryGetValue("intent", out var intent) && !string.IsNullOrWhiteSpace(intent))
        {
            return intent;
        }

        if (sample.Metadata.TryGetValue("planningIntent", out var planningIntent) && !string.IsNullOrWhiteSpace(planningIntent))
        {
            return planningIntent;
        }

        return "Unknown";
    }

    private static string ResolveProviderId(string current, VectorQueryPreviewResult preview)
    {
        return string.IsNullOrWhiteSpace(current)
            ? preview.Candidates.FirstOrDefault()?.EmbeddingProvider ?? string.Empty
            : current;
    }

    private static string ResolveEmbeddingModel(string current, VectorQueryPreviewResult preview)
    {
        return string.IsNullOrWhiteSpace(current)
            ? preview.Candidates.FirstOrDefault()?.EmbeddingModel ?? string.Empty
            : current;
    }

    private static string ResolveLifecycle(VectorQueryPreviewCandidate candidate)
    {
        if (candidate.Metadata.TryGetValue("resolvedLifecycle", out var resolved)
            && !string.IsNullOrWhiteSpace(resolved))
        {
            return resolved;
        }

        return candidate.Metadata.TryGetValue("lifecycle", out var lifecycle)
            ? lifecycle
            : string.Empty;
    }

    private static bool IsActiveLifecycle(string lifecycle)
    {
        return string.Equals(lifecycle, "Active", StringComparison.OrdinalIgnoreCase)
               || string.Equals(lifecycle, "Current", StringComparison.OrdinalIgnoreCase)
               || string.Equals(lifecycle, "Stable", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRiskLifecycle(string lifecycle)
    {
        return string.Equals(lifecycle, "Deprecated", StringComparison.OrdinalIgnoreCase)
               || string.Equals(lifecycle, "Superseded", StringComparison.OrdinalIgnoreCase)
               || string.Equals(lifecycle, "Historical", StringComparison.OrdinalIgnoreCase)
               || string.Equals(lifecycle, "Rejected", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEligible(VectorQueryPreviewCandidate candidate)
    {
        return string.Equals(candidate.EligibilityStatus, VectorCandidateEligibilityStatuses.Eligible, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EvalIdMatches(string expected, string candidateId)
    {
        return !string.IsNullOrWhiteSpace(expected)
               && !string.IsNullOrWhiteSpace(candidateId)
               && (string.Equals(expected, candidateId, StringComparison.OrdinalIgnoreCase)
                   || candidateId.EndsWith(expected, StringComparison.OrdinalIgnoreCase)
                   || expected.EndsWith(candidateId, StringComparison.OrdinalIgnoreCase));
    }

    private static double TryGetDouble(IReadOnlyDictionary<string, string> metadata, string key)
    {
        return metadata.TryGetValue(key, out var value) && double.TryParse(value, out var parsed)
            ? parsed
            : 0;
    }

    private static IReadOnlyList<string> BuildWarnings(
        VectorRankerFusionStrategyResult? best,
        int poolTopK,
        int finalTopK)
    {
        var warnings = new List<string>
        {
            $"fusion shadow 使用候选池 topK={poolTopK}，最终评估 topK={finalTopK}；报告不改变正式 retrieval/package 输出。"
        };
        if (best is null)
        {
            warnings.Add("未生成可用 fusion 策略结果。");
        }
        else if (!string.Equals(best.Recommendation, VectorQueryShadowRecommendations.ReadyForRetrievalShadow, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("best fusion 策略尚未满足 retrieval shadow readiness，不得接入正式检索。");
        }

        return warnings;
    }

    private static string Empty(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private static string Ids(IReadOnlyList<string> ids)
    {
        return ids.Count == 0 ? "-" : string.Join("<br>", ids.Take(5));
    }

    private sealed record RankerScore(double Value, IReadOnlyList<string> Reasons);

    private sealed record ScoredCandidate(VectorQueryPreviewCandidate Candidate, RankerScore Score);
}
