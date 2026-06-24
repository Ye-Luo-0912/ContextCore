using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Planning;

namespace ContextCore.Core.Services;

/// <summary>向量召回损失离线审计 runner；只读取 shadow 结果和索引 metadata，不影响正式检索。</summary>
public sealed class VectorRecallLossAuditRunner
{
    private const int DiagnosticTopK = 1000;
    private const double LowSeparationThreshold = 0.03;

    private readonly VectorQueryPreviewService? _previewService;
    private readonly PlanningIntentDetector _intentDetector;

    public VectorRecallLossAuditRunner(
        VectorQueryPreviewService? previewService = null,
        PlanningIntentDetector? intentDetector = null)
    {
        _previewService = previewService;
        _intentDetector = intentDetector ?? new PlanningIntentDetector();
    }

    public async Task<VectorRecallLossAuditReport> RunAsync(
        IReadOnlyList<ContextEvalSample> samples,
        IReadOnlyList<VectorIndexEntry> indexEntries,
        string workspaceId,
        string collectionId,
        int topK = 10,
        string? layer = null,
        string? itemKind = null,
        double? minSimilarity = null,
        string? profileId = null,
        double lowConfidenceThreshold = 0.25,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(indexEntries);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        if (_previewService is null)
        {
            throw new InvalidOperationException("VectorRecallLossAuditRunner requires VectorQueryPreviewService when RunAsync is used.");
        }

        var operationId = $"vector-recall-loss-audit-{Guid.NewGuid():N}";
        var configuredSamples = new List<VectorQueryShadowEvalSample>();
        var broadPreviews = new Dictionary<string, VectorQueryPreviewResult>(StringComparer.OrdinalIgnoreCase);
        var resolvedProfileId = string.IsNullOrWhiteSpace(profileId)
            ? VectorQueryProfileIds.NormalV1
            : profileId;

        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var configured = await _previewService.PreviewAsync(new VectorQueryPreviewRequest
            {
                OperationId = $"{operationId}:{sample.Id}:configured",
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                QueryText = sample.Query,
                TopK = topK,
                ProfileId = resolvedProfileId,
                Layer = layer,
                ItemKind = itemKind,
                MinSimilarity = minSimilarity,
                IncludeVector = false,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["mode"] = sample.Mode,
                    ["createdFrom"] = "vector_recall_loss_audit"
                }
            }, cancellationToken).ConfigureAwait(false);
            configuredSamples.Add(VectorQueryShadowEvalRunner.BuildSampleResult(sample, configured, lowConfidenceThreshold));

            var broad = await _previewService.PreviewAsync(new VectorQueryPreviewRequest
            {
                OperationId = $"{operationId}:{sample.Id}:diagnostic",
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                QueryText = sample.Query,
                TopK = DiagnosticTopK,
                ProfileId = resolvedProfileId,
                MinSimilarity = minSimilarity,
                IncludeVector = false,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["mode"] = sample.Mode,
                    ["createdFrom"] = "vector_recall_loss_audit_diagnostic"
                }
            }, cancellationToken).ConfigureAwait(false);
            broadPreviews[sample.Id] = broad;
        }

        return BuildReport(
            operationId,
            samples,
            configuredSamples,
            broadPreviews,
            indexEntries,
            resolvedProfileId,
            topK,
            minSimilarity,
            layer,
            itemKind);
    }

    public VectorRecallLossAuditReport BuildReport(
        string operationId,
        IReadOnlyList<ContextEvalSample> evalSamples,
        IReadOnlyList<VectorQueryShadowEvalSample> configuredSamples,
        IReadOnlyDictionary<string, VectorQueryPreviewResult> broadPreviews,
        IReadOnlyList<VectorIndexEntry> indexEntries,
        string profileId,
        int topK,
        double? minSimilarity,
        string? layerFilter,
        string? itemKindFilter,
        IReadOnlyList<string>? warnings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(evalSamples);
        ArgumentNullException.ThrowIfNull(configuredSamples);
        ArgumentNullException.ThrowIfNull(broadPreviews);
        ArgumentNullException.ThrowIfNull(indexEntries);

        var evalById = evalSamples
            .Where(sample => !string.IsNullOrWhiteSpace(sample.Id))
            .GroupBy(sample => sample.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var sampleInfos = configuredSamples
            .Select(sample => new SampleInfo(
                evalById.TryGetValue(sample.SampleId, out var evalSample)
                    ? evalSample
                    : new ContextEvalSample
                    {
                        Id = sample.SampleId,
                        Mode = sample.Mode,
                        Query = sample.QueryText,
                        MustHit = sample.MustHitMissing
                    },
                sample,
                broadPreviews.TryGetValue(sample.SampleId, out var broad)
                    ? broad
                    : new VectorQueryPreviewResult()))
            .ToArray();

        var misses = sampleInfos
            .SelectMany(info => BuildMisses(
                info,
                indexEntries,
                profileId,
                topK,
                minSimilarity,
                layerFilter,
                itemKindFilter))
            .ToArray();
        var summary = VectorQueryShadowEvalRunner.BuildReport(operationId, configuredSamples);
        var firstCandidate = configuredSamples
            .SelectMany(sample => sample.Candidates)
            .FirstOrDefault();
        var firstEntry = indexEntries.FirstOrDefault();
        var missReasonCounts = misses
            .GroupBy(item => item.MissReason, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var modeReadiness = BuildReadinessReport(
            $"{operationId}:mode",
            "Mode",
            sampleInfos,
            misses,
            info => info.Configured.Mode);
        var intentReadiness = BuildReadinessReport(
            $"{operationId}:intent",
            "Intent",
            sampleInfos,
            misses,
            info => ResolveIntent(info.EvalSample));

        return new VectorRecallLossAuditReport
        {
            OperationId = operationId,
            CreatedAt = DateTimeOffset.UtcNow,
            Samples = configuredSamples.Count,
            ProviderId = firstCandidate?.EmbeddingProvider ?? firstEntry?.EmbeddingProvider ?? string.Empty,
            EmbeddingModel = firstCandidate?.EmbeddingModel ?? firstEntry?.EmbeddingModel ?? string.Empty,
            ProfileId = profileId,
            TopK = topK,
            MinSimilarity = minSimilarity,
            LayerFilter = layerFilter ?? string.Empty,
            ItemKindFilter = itemKindFilter ?? string.Empty,
            MissedMustHitCount = misses.Length,
            MustHitRecallAfterPolicy = summary.MustHitRecallAfterPolicy,
            MustHitMrrAfterPolicy = CalculateMrr(configuredSamples),
            RiskAfterPolicy = summary.RiskAfterPolicy,
            NoCandidateCount = summary.NoCandidateCount,
            MissedMustHits = misses,
            MissReasonCounts = missReasonCounts,
            ModeReadiness = modeReadiness,
            IntentReadiness = intentReadiness,
            Recommendation = Recommend(summary, misses, CalculateMrr(configuredSamples)),
            FormalOutputChanged = 0,
            Warnings = warnings ?? BuildWarnings(summary, misses)
        };
    }

    public static string BuildMarkdownReport(
        VectorRecallLossAuditReport a3,
        VectorRecallLossAuditReport extended)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Recall Loss Audit Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {DateTimeOffset.UtcNow:O}");
        builder.AppendLine();
        AppendReport(builder, "A3", a3);
        AppendReport(builder, "Extended", extended);
        return builder.ToString();
    }

    private IReadOnlyList<VectorRecallLossMiss> BuildMisses(
        SampleInfo info,
        IReadOnlyList<VectorIndexEntry> indexEntries,
        string profileId,
        int topK,
        double? minSimilarity,
        string? layerFilter,
        string? itemKindFilter)
    {
        var misses = new List<VectorRecallLossMiss>();
        var intent = ResolveIntent(info.EvalSample);
        foreach (var mustHit in info.EvalSample.MustHit
                     .Where(item => !string.IsNullOrWhiteSpace(item))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (info.Configured.MustHitMatchedAfterPolicy.Any(matched => EvalIdMatches(mustHit, matched)))
            {
                continue;
            }

            var configuredCandidate = FindMatchingCandidate(info.Configured.Candidates, mustHit);
            var broadCandidate = FindMatchingCandidate(info.BroadPreview.Candidates, mustHit);
            var indexEntry = FindMatchingEntry(indexEntries, mustHit);
            var candidateForReason = configuredCandidate ?? broadCandidate;
            var eligibleRank = configuredCandidate is null
                ? 0
                : ResolveEligibleRank(info.Configured.Candidates, configuredCandidate.ItemId);
            var blockedReasons = candidateForReason?.BlockedReasons ?? Array.Empty<string>();
            var missReason = ClassifyMissReason(
                info.Configured,
                mustHit,
                indexEntry,
                configuredCandidate,
                broadCandidate,
                layerFilter,
                itemKindFilter);

            misses.Add(new VectorRecallLossMiss
            {
                SampleId = info.Configured.SampleId,
                Mode = info.Configured.Mode,
                Intent = intent,
                QueryText = info.Configured.QueryText,
                MustHitItemId = mustHit,
                WasIndexed = indexEntry is not null,
                WasRawCandidate = configuredCandidate is not null,
                RawRank = configuredCandidate?.RawRank ?? broadCandidate?.RawRank ?? 0,
                RawSimilarity = configuredCandidate?.Similarity ?? broadCandidate?.Similarity ?? 0,
                WasEligibleCandidate = configuredCandidate is not null && IsEligible(configuredCandidate),
                EligibleRank = eligibleRank,
                BlockedReason = blockedReasons.Count == 0 ? string.Empty : string.Join(",", blockedReasons),
                MissReason = missReason,
                ProfileId = profileId,
                TopK = topK,
                MinSimilarity = minSimilarity,
                LayerFilter = layerFilter ?? string.Empty,
                ItemKindFilter = itemKindFilter ?? string.Empty
            });
        }

        return misses;
    }

    private string ClassifyMissReason(
        VectorQueryShadowEvalSample sample,
        string mustHit,
        VectorIndexEntry? indexEntry,
        VectorQueryPreviewCandidate? configuredCandidate,
        VectorQueryPreviewCandidate? broadCandidate,
        string? layerFilter,
        string? itemKindFilter)
    {
        if (indexEntry is null)
        {
            return VectorRecallLossMissReasons.NotIndexed;
        }

        if (sample.RawCandidateCount == 0 && broadCandidate is null)
        {
            return VectorRecallLossMissReasons.NoCandidateGenerated;
        }

        if (!string.IsNullOrWhiteSpace(layerFilter)
            && !MatchesFilter(indexEntry.Layer, layerFilter)
            && broadCandidate is not null)
        {
            return VectorRecallLossMissReasons.LayerFilterExcluded;
        }

        if (!string.IsNullOrWhiteSpace(itemKindFilter)
            && !MatchesFilter(indexEntry.ItemKind, itemKindFilter)
            && broadCandidate is not null)
        {
            return VectorRecallLossMissReasons.ItemKindFilterExcluded;
        }

        var candidate = configuredCandidate ?? broadCandidate;
        if (candidate is not null)
        {
            if (candidate.BlockedReasons.Contains(VectorCandidateBlockedReason.SimilarityBelowThreshold, StringComparer.OrdinalIgnoreCase))
            {
                return VectorRecallLossMissReasons.BelowSimilarityThreshold;
            }

            if (!IsEligible(candidate))
            {
                return VectorRecallLossMissReasons.BlockedByEligibilityPolicy;
            }

            if (configuredCandidate is null && candidate.RawRank > sample.TopK)
            {
                return VectorRecallLossMissReasons.BelowTopK;
            }
        }

        if (broadCandidate is not null && configuredCandidate is null)
        {
            return VectorRecallLossMissReasons.BelowTopK;
        }

        if (HasLowSeparation(sample, mustHit))
        {
            return VectorRecallLossMissReasons.LowSimilaritySeparation;
        }

        return VectorRecallLossMissReasons.RequiresRankerFusion;
    }

    private VectorIntentReadinessReport BuildReadinessReport(
        string operationId,
        string groupBy,
        IReadOnlyList<SampleInfo> samples,
        IReadOnlyList<VectorRecallLossMiss> misses,
        Func<SampleInfo, string> keySelector)
    {
        var buckets = samples
            .GroupBy(info => NormalizeGroupKey(keySelector(info)), StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildReadinessBucket(group.Key, groupBy, group.ToArray(), misses))
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new VectorIntentReadinessReport
        {
            OperationId = operationId,
            CreatedAt = DateTimeOffset.UtcNow,
            GroupBy = groupBy,
            Buckets = buckets,
            Recommendation = RecommendBuckets(buckets)
        };
    }

    private VectorIntentReadinessBucket BuildReadinessBucket(
        string key,
        string groupBy,
        IReadOnlyList<SampleInfo> samples,
        IReadOnlyList<VectorRecallLossMiss> misses)
    {
        var configured = samples.Select(item => item.Configured).ToArray();
        var mustHitTotal = configured.Sum(item => item.MustHitCount);
        var mustHitHit = configured.Sum(item => item.MustHitHitCountAfterPolicy);
        var bucketMisses = misses
            .Where(miss => groupBy.Equals("Mode", StringComparison.OrdinalIgnoreCase)
                ? string.Equals(miss.Mode, key, StringComparison.OrdinalIgnoreCase)
                : string.Equals(miss.Intent, key, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var missReasons = bucketMisses
            .GroupBy(item => item.MissReason, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var risk = configured.Sum(item => item.RiskAfterPolicy);
        var recall = mustHitTotal == 0 ? 1.0 : (double)mustHitHit / mustHitTotal;
        var mrr = CalculateMrr(configured);

        return new VectorIntentReadinessBucket
        {
            Key = key,
            Mode = groupBy.Equals("Mode", StringComparison.OrdinalIgnoreCase) ? key : string.Empty,
            Intent = groupBy.Equals("Intent", StringComparison.OrdinalIgnoreCase) ? key : string.Empty,
            Samples = configured.Length,
            MustHitRecallAfterPolicy = recall,
            MustHitMrrAfterPolicy = mrr,
            RiskAfterPolicy = risk,
            NoCandidateCount = configured.Count(item => item.RawCandidateCount == 0),
            TopMissReasons = missReasons,
            Recommendation = RecommendReadiness(recall, mrr, risk, missReasons)
        };
    }

    private string ResolveIntent(ContextEvalSample sample)
    {
        if (sample.Metadata.TryGetValue("intent", out var intent)
            && !string.IsNullOrWhiteSpace(intent))
        {
            return intent.Trim();
        }

        var snapshot = new ContextPlanningSnapshot
        {
            WorkspaceId = "eval",
            CollectionId = "vector"
        };
        return _intentDetector.Detect(snapshot, sample.Query, sample.Mode).Intent;
    }

    private static string Recommend(
        VectorQueryShadowEvalReport summary,
        IReadOnlyList<VectorRecallLossMiss> misses,
        double mrr)
    {
        if (summary.RiskAfterPolicy > 0)
        {
            return VectorQueryShadowRecommendations.BlockedByRisk;
        }

        if (summary.Samples == 0 || summary.RawCandidateCount == 0 || summary.NoCandidateCount > summary.Samples / 2)
        {
            return VectorQueryShadowRecommendations.NeedsMoreIndexedData;
        }

        if (summary.MustHitRecallAfterPolicy >= 0.8 && mrr >= 0.5)
        {
            return VectorQueryShadowRecommendations.ReadyForRetrievalShadow;
        }

        var reasonCounts = misses
            .GroupBy(item => item.MissReason, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        if (reasonCounts.ContainsKey(VectorRecallLossMissReasons.RequiresRankerFusion)
            || reasonCounts.ContainsKey(VectorRecallLossMissReasons.LowSimilaritySeparation))
        {
            return VectorQueryShadowRecommendations.NeedsRankerFusion;
        }

        if (reasonCounts.ContainsKey(VectorRecallLossMissReasons.BelowTopK)
            || reasonCounts.ContainsKey(VectorRecallLossMissReasons.BelowSimilarityThreshold)
            || reasonCounts.ContainsKey(VectorRecallLossMissReasons.LayerFilterExcluded)
            || reasonCounts.ContainsKey(VectorRecallLossMissReasons.ItemKindFilterExcluded))
        {
            return VectorQueryShadowRecommendations.NeedsProfileTuning;
        }

        return VectorQueryShadowRecommendations.KeepPreviewOnly;
    }

    private static string RecommendReadiness(
        double recall,
        double mrr,
        int riskAfterPolicy,
        IReadOnlyDictionary<string, int> missReasons)
    {
        if (riskAfterPolicy > 0)
        {
            return VectorQueryShadowRecommendations.BlockedByRisk;
        }

        if (recall >= 0.8 && mrr >= 0.5)
        {
            return VectorQueryShadowRecommendations.ReadyForRetrievalShadow;
        }

        if (missReasons.ContainsKey(VectorRecallLossMissReasons.NotIndexed)
            || missReasons.ContainsKey(VectorRecallLossMissReasons.NoCandidateGenerated))
        {
            return VectorQueryShadowRecommendations.NeedsMoreIndexedData;
        }

        if (missReasons.ContainsKey(VectorRecallLossMissReasons.RequiresRankerFusion)
            || missReasons.ContainsKey(VectorRecallLossMissReasons.LowSimilaritySeparation))
        {
            return VectorQueryShadowRecommendations.NeedsRankerFusion;
        }

        if (missReasons.ContainsKey(VectorRecallLossMissReasons.BelowTopK)
            || missReasons.ContainsKey(VectorRecallLossMissReasons.BelowSimilarityThreshold)
            || missReasons.ContainsKey(VectorRecallLossMissReasons.LayerFilterExcluded)
            || missReasons.ContainsKey(VectorRecallLossMissReasons.ItemKindFilterExcluded)
            || missReasons.ContainsKey(VectorRecallLossMissReasons.BlockedByEligibilityPolicy))
        {
            return VectorQueryShadowRecommendations.NeedsProfileTuning;
        }

        return VectorQueryShadowRecommendations.KeepPreviewOnly;
    }

    private static string RecommendBuckets(IReadOnlyList<VectorIntentReadinessBucket> buckets)
    {
        if (buckets.Count == 0)
        {
            return VectorQueryShadowRecommendations.NeedsMoreIndexedData;
        }

        if (buckets.Any(item => item.Recommendation == VectorQueryShadowRecommendations.BlockedByRisk))
        {
            return VectorQueryShadowRecommendations.BlockedByRisk;
        }

        if (buckets.All(item => item.Recommendation == VectorQueryShadowRecommendations.ReadyForRetrievalShadow))
        {
            return VectorQueryShadowRecommendations.ReadyForRetrievalShadow;
        }

        if (buckets.Any(item => item.Recommendation == VectorQueryShadowRecommendations.NeedsRankerFusion))
        {
            return VectorQueryShadowRecommendations.NeedsRankerFusion;
        }

        if (buckets.Any(item => item.Recommendation == VectorQueryShadowRecommendations.NeedsPolicyTuning))
        {
            return VectorQueryShadowRecommendations.NeedsProfileTuning;
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
            if (sample.MustHitCount == 0)
            {
                total += 1.0;
                continue;
            }

            var eligible = sample.Candidates
                .Where(IsEligible)
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

    private static int ResolveEligibleRank(IReadOnlyList<VectorQueryPreviewCandidate> candidates, string itemId)
    {
        var eligible = candidates
            .Where(IsEligible)
            .OrderBy(candidate => candidate.Rank)
            .ToArray();
        var index = Array.FindIndex(eligible, candidate => string.Equals(candidate.ItemId, itemId, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? 0 : index + 1;
    }

    private static bool HasLowSeparation(VectorQueryShadowEvalSample sample, string mustHit)
    {
        var matching = FindMatchingCandidate(sample.Candidates, mustHit);
        if (matching is null)
        {
            return false;
        }

        var nearestOther = sample.Candidates
            .Where(candidate => !EvalIdMatches(mustHit, candidate.ItemId))
            .OrderByDescending(candidate => candidate.Similarity)
            .FirstOrDefault();
        return nearestOther is not null
               && Math.Abs(matching.Similarity - nearestOther.Similarity) <= LowSeparationThreshold;
    }

    private static VectorQueryPreviewCandidate? FindMatchingCandidate(
        IReadOnlyList<VectorQueryPreviewCandidate> candidates,
        string expected)
    {
        return candidates
            .OrderBy(candidate => candidate.RawRank == 0 ? int.MaxValue : candidate.RawRank)
            .ThenByDescending(candidate => candidate.Similarity)
            .FirstOrDefault(candidate => EvalIdMatches(expected, candidate.ItemId));
    }

    private static VectorIndexEntry? FindMatchingEntry(
        IReadOnlyList<VectorIndexEntry> entries,
        string expected)
    {
        return entries.FirstOrDefault(entry => EvalIdMatches(expected, entry.ItemId));
    }

    private static bool IsEligible(VectorQueryPreviewCandidate candidate)
    {
        return string.Equals(candidate.EligibilityStatus, VectorCandidateEligibilityStatuses.Eligible, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesFilter(string value, string expected)
    {
        return string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EvalIdMatches(string expected, string candidateId)
    {
        return !string.IsNullOrWhiteSpace(expected)
               && !string.IsNullOrWhiteSpace(candidateId)
               && (string.Equals(expected, candidateId, StringComparison.OrdinalIgnoreCase)
                   || candidateId.EndsWith(expected, StringComparison.OrdinalIgnoreCase)
                   || expected.EndsWith(candidateId, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeGroupKey(string? key)
    {
        return string.IsNullOrWhiteSpace(key) ? "Unknown" : key.Trim();
    }

    private static IReadOnlyList<string> BuildWarnings(
        VectorQueryShadowEvalReport summary,
        IReadOnlyList<VectorRecallLossMiss> misses)
    {
        var warnings = new List<string>();
        if (summary.RiskAfterPolicy > 0)
        {
            warnings.Add("当前 vector shadow policy 后仍存在风险，不允许进入 retrieval shadow。");
        }

        if (misses.Any(item => item.MissReason == VectorRecallLossMissReasons.RequiresRankerFusion))
        {
            warnings.Add("部分 mustHit 已进入候选但仍需要融合排序或 reranker 才能稳定靠前。");
        }

        return warnings;
    }

    private static void AppendReport(
        StringBuilder builder,
        string title,
        VectorRecallLossAuditReport report)
    {
        builder.AppendLine($"## {title} Summary");
        builder.AppendLine();
        builder.AppendLine($"- Samples: `{report.Samples}`");
        builder.AppendLine($"- Provider: `{report.ProviderId}`");
        builder.AppendLine($"- Model: `{report.EmbeddingModel}`");
        builder.AppendLine($"- Profile: `{report.ProfileId}`");
        builder.AppendLine($"- TopK: `{report.TopK}`");
        builder.AppendLine($"- MinSimilarity: `{(report.MinSimilarity is null ? "-" : report.MinSimilarity.Value.ToString("F2"))}`");
        builder.AppendLine($"- MustHitRecallAfterPolicy: `{report.MustHitRecallAfterPolicy:P2}`");
        builder.AppendLine($"- MustHitMrrAfterPolicy: `{report.MustHitMrrAfterPolicy:F4}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- MissedMustHitCount: `{report.MissedMustHitCount}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("| Miss Reason | Count |");
        builder.AppendLine("|---|---:|");
        foreach (var reason in report.MissReasonCounts
                     .OrderByDescending(item => item.Value)
                     .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {reason.Key} | {reason.Value} |");
        }

        AppendReadiness(builder, $"{title} Mode Readiness", report.ModeReadiness);
        AppendReadiness(builder, $"{title} Intent Readiness", report.IntentReadiness);

        builder.AppendLine($"## {title} Missed MustHit Details");
        builder.AppendLine();
        builder.AppendLine("| Sample | Mode | Intent | MustHit | Indexed | RawRank | RawSim | EligibleRank | MissReason | BlockedReason |");
        builder.AppendLine("|---|---|---|---|---:|---:|---:|---:|---|---|");
        foreach (var miss in report.MissedMustHits.Take(100))
        {
            builder.AppendLine($"| {miss.SampleId} | {miss.Mode} | {miss.Intent} | {miss.MustHitItemId} | {(miss.WasIndexed ? "yes" : "no")} | {miss.RawRank} | {miss.RawSimilarity:F4} | {miss.EligibleRank} | {miss.MissReason} | {Sanitize(miss.BlockedReason)} |");
        }

        builder.AppendLine();
    }

    private static void AppendReadiness(
        StringBuilder builder,
        string title,
        VectorIntentReadinessReport report)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("| Key | Samples | RecallAfter | MRR | RiskAfter | NoCandidate | TopMissReasons | Recommendation |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---|---|");
        foreach (var bucket in report.Buckets.OrderBy(bucket => bucket.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {bucket.Key} | {bucket.Samples} | {bucket.MustHitRecallAfterPolicy:P2} | {bucket.MustHitMrrAfterPolicy:F4} | {bucket.RiskAfterPolicy} | {bucket.NoCandidateCount} | {FormatReasons(bucket.TopMissReasons)} | {bucket.Recommendation} |");
        }

        builder.AppendLine();
    }

    private static string FormatReasons(IReadOnlyDictionary<string, int> reasons)
    {
        return reasons.Count == 0
            ? "-"
            : string.Join("<br>", reasons.Select(item => $"{item.Key}:{item.Value}"));
    }

    private static string Sanitize(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value.Replace("|", "/", StringComparison.Ordinal);
    }

    private sealed record SampleInfo(
        ContextEvalSample EvalSample,
        VectorQueryShadowEvalSample Configured,
        VectorQueryPreviewResult BroadPreview);
}
