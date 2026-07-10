using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>安全召回恢复离线 runner；只重放 vector preview 候选，不改变正式检索或打包。</summary>
public sealed class VectorSafeRecallRecoveryRunner
{
    private const int DiagnosticTopK = 1000;
    private const double ReadinessRecallThreshold = 0.80;

    private static readonly string[] SweepProfiles =
    [
        VectorQueryProfileIds.NormalV1,
        VectorQueryProfileIds.CurrentTaskV1,
        VectorQueryProfileIds.AuditV1,
        VectorQueryProfileIds.DiagnosticsV1
    ];

    private static readonly int[] SweepTopKs = [10, 20, 30, 50];

    private static readonly double[] SweepMinSimilarities = [0.05, 0.10, 0.15, 0.20, 0.30];

    private static readonly string[] SweepLayerFilters =
    [
        VectorQueryLayerFilters.StableOnly,
        VectorQueryLayerFilters.CandidateStable,
        VectorQueryLayerFilters.ExcludeHistorical
    ];

    private readonly VectorQueryPreviewService _previewService;
    private readonly VectorQueryProfileRegistry _profileRegistry;
    private readonly VectorCandidateEligibilityPolicy _eligibilityPolicy;
    private readonly VectorSourceLifecycleMetadataResolver _lifecycleResolver;
    private readonly VectorRecallLossAuditRunner _recallLossRunner;

    public VectorSafeRecallRecoveryRunner(
        VectorQueryPreviewService previewService,
        VectorQueryProfileRegistry? profileRegistry = null,
        VectorCandidateEligibilityPolicy? eligibilityPolicy = null,
        VectorSourceLifecycleMetadataResolver? lifecycleResolver = null)
    {
        _previewService = previewService;
        _profileRegistry = profileRegistry ?? new VectorQueryProfileRegistry();
        _eligibilityPolicy = eligibilityPolicy ?? new VectorCandidateEligibilityPolicy();
        _lifecycleResolver = lifecycleResolver ?? new VectorSourceLifecycleMetadataResolver();
        _recallLossRunner = new VectorRecallLossAuditRunner(previewService);
    }

    public async Task<VectorSafeRecallRecoveryReport> RunAsync(
        IReadOnlyList<ContextEvalSample> samples,
        IReadOnlyList<VectorIndexEntry> indexEntries,
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(indexEntries);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);

        var operationId = $"vector-safe-recall-recovery-{Guid.NewGuid():N}";
        var baseline = await _recallLossRunner.RunAsync(
            samples,
            indexEntries,
            workspaceId,
            collectionId,
            topK: 10,
            profileId: VectorQueryProfileIds.NormalV1,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var basePreviews = await BuildBasePreviewsAsync(
            operationId,
            samples,
            workspaceId,
            collectionId,
            VectorQueryProfileIds.DiagnosticsV1,
            cancellationToken).ConfigureAwait(false);
        var normalBroadPreviews = await BuildBasePreviewsAsync(
            operationId,
            samples,
            workspaceId,
            collectionId,
            VectorQueryProfileIds.NormalV1,
            cancellationToken).ConfigureAwait(false);

        var belowTopKMisses = baseline.MissedMustHits
            .Where(item => string.Equals(item.MissReason, VectorRecallLossMissReasons.BelowTopK, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var sweepResults = BuildSweepResults(samples, basePreviews, belowTopKMisses);
        var blockedAudit = BuildBlockedAudit(baseline, normalBroadPreviews, indexEntries);
        var best = SelectBestSafeSweep(sweepResults);

        return new VectorSafeRecallRecoveryReport
        {
            OperationId = operationId,
            CreatedAt = DateTimeOffset.UtcNow,
            Samples = samples.Count,
            ProviderId = baseline.ProviderId,
            EmbeddingModel = baseline.EmbeddingModel,
            BaselineRecallAfterPolicy = baseline.MustHitRecallAfterPolicy,
            BaselineMrrAfterPolicy = baseline.MustHitMrrAfterPolicy,
            BaselineRiskAfterPolicy = baseline.RiskAfterPolicy,
            BelowTopKMissCount = belowTopKMisses.Length,
            BlockedMustHitCount = blockedAudit.Count,
            SweepResults = sweepResults,
            BestSafeSweep = best,
            BlockedMustHitAudit = blockedAudit,
            BlockedClassificationCounts = blockedAudit
                .GroupBy(item => item.Classification, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            Recommendation = Recommend(baseline, best, blockedAudit),
            FormalOutputChanged = 0,
            Warnings = BuildWarnings(baseline, best, blockedAudit)
        };
    }

    public static string BuildMarkdownReport(
        VectorSafeRecallRecoveryReport a3,
        VectorSafeRecallRecoveryReport extended)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Safe Recall Recovery Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {DateTimeOffset.UtcNow:O}");
        builder.AppendLine();
        AppendReport(builder, "A3", a3);
        AppendReport(builder, "Extended", extended);
        return builder.ToString();
    }

    public static VectorRetrievalShadowReadinessGateReport BuildReadinessGate(
        VectorQueryShadowEvalReport a3,
        VectorQueryShadowEvalReport extended,
        VectorRankerFusionShadowReport? a3Fusion = null,
        VectorRankerFusionShadowReport? extendedFusion = null,
        VectorQueryExpansionShadowReport? a3Expansion = null,
        VectorQueryExpansionShadowReport? extendedExpansion = null)
    {
        var conditions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["A3RiskAfterPolicyZero"] = a3.RiskAfterPolicy == 0,
            ["A3MustNotHitRiskAfterPolicyZero"] = a3.MustNotHitRiskAfterPolicy == 0,
            ["A3LifecycleRiskAfterPolicyZero"] = a3.LifecycleRiskAfterPolicy == 0,
            ["A3RecallAtLeast80Percent"] = a3.MustHitRecallAfterPolicy >= ReadinessRecallThreshold,
            ["ExtendedRiskAfterPolicyZero"] = extended.RiskAfterPolicy == 0,
            ["ExtendedRecallAtLeast80Percent"] = extended.MustHitRecallAfterPolicy >= ReadinessRecallThreshold,
            ["FormalOutputChangedZero"] = a3.FormalOutputChanged == 0 && extended.FormalOutputChanged == 0
        };
        var a3BestFusion = a3Fusion?.BestResult;
        var extendedBestFusion = extendedFusion?.BestResult;
        if (a3BestFusion is not null && extendedBestFusion is not null)
        {
            conditions["A3FusionRecallAtLeast80Percent"] = a3BestFusion.MustHitRecallFusion >= ReadinessRecallThreshold;
            conditions["ExtendedFusionRecallAtLeast80Percent"] = extendedBestFusion.MustHitRecallFusion >= ReadinessRecallThreshold;
            conditions["FusionRiskAfterPolicyZero"] = a3BestFusion.MustNotHitRiskFusion == 0
                                                       && extendedBestFusion.MustNotHitRiskFusion == 0;
            conditions["FusionLifecycleRiskZero"] = a3BestFusion.LifecycleRiskFusion == 0
                                                     && extendedBestFusion.LifecycleRiskFusion == 0;
            conditions["FusionNewlyRiskySamplesZero"] = a3BestFusion.NewlyRiskySamples.Count == 0
                                                        && extendedBestFusion.NewlyRiskySamples.Count == 0;
            conditions["FusionFormalOutputChangedZero"] = (a3Fusion?.FormalOutputChanged ?? 0) == 0
                                                          && (extendedFusion?.FormalOutputChanged ?? 0) == 0;
        }

        var a3BestExpansion = a3Expansion?.BestResult;
        var extendedBestExpansion = extendedExpansion?.BestResult;
        if (a3BestExpansion is not null && extendedBestExpansion is not null)
        {
            conditions["A3ExpandedRecallAtLeast80Percent"] = a3BestExpansion.RecallAfterExpansion >= ReadinessRecallThreshold;
            conditions["ExtendedExpandedRecallAtLeast80Percent"] = extendedBestExpansion.RecallAfterExpansion >= ReadinessRecallThreshold;
            conditions["ExpandedRiskAfterPolicyZero"] = a3BestExpansion.RiskAfterPolicy == 0
                                                        && extendedBestExpansion.RiskAfterPolicy == 0;
            conditions["ExpandedMustNotHitRiskAfterPolicyZero"] = a3BestExpansion.MustNotHitRiskAfterPolicy == 0
                                                                  && extendedBestExpansion.MustNotHitRiskAfterPolicy == 0;
            conditions["ExpandedLifecycleRiskAfterPolicyZero"] = a3BestExpansion.LifecycleRiskAfterPolicy == 0
                                                                 && extendedBestExpansion.LifecycleRiskAfterPolicy == 0;
            conditions["ExpansionFormalOutputChangedZero"] = (a3Expansion?.FormalOutputChanged ?? 0) == 0
                                                             && (extendedExpansion?.FormalOutputChanged ?? 0) == 0;
        }

        var failReasons = conditions
            .Where(item => !item.Value)
            .Select(item => item.Key)
            .ToArray();

        return new VectorRetrievalShadowReadinessGateReport
        {
            OperationId = $"vector-retrieval-shadow-readiness-gate-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            Passed = failReasons.Length == 0,
            A3RecallAfterPolicy = a3.MustHitRecallAfterPolicy,
            A3RiskAfterPolicy = a3.RiskAfterPolicy,
            A3MustNotHitRiskAfterPolicy = a3.MustNotHitRiskAfterPolicy,
            A3LifecycleRiskAfterPolicy = a3.LifecycleRiskAfterPolicy,
            A3FormalOutputChanged = a3.FormalOutputChanged,
            ExtendedRecallAfterPolicy = extended.MustHitRecallAfterPolicy,
            ExtendedRiskAfterPolicy = extended.RiskAfterPolicy,
            ExtendedMustNotHitRiskAfterPolicy = extended.MustNotHitRiskAfterPolicy,
            ExtendedLifecycleRiskAfterPolicy = extended.LifecycleRiskAfterPolicy,
            ExtendedFormalOutputChanged = extended.FormalOutputChanged,
            A3FusionRecallAfterPolicy = a3BestFusion?.MustHitRecallFusion ?? 0,
            A3FusionRiskAfterPolicy = a3BestFusion is null
                ? 0
                : (a3BestFusion.MustNotHitRiskFusion > 0 ? 1 : 0),
            A3FusionLifecycleRiskAfterPolicy = a3BestFusion?.LifecycleRiskFusion ?? 0,
            A3FusionNewlyRiskySamples = a3BestFusion?.NewlyRiskySamples.Count ?? 0,
            ExtendedFusionRecallAfterPolicy = extendedBestFusion?.MustHitRecallFusion ?? 0,
            ExtendedFusionRiskAfterPolicy = extendedBestFusion is null
                ? 0
                : (extendedBestFusion.MustNotHitRiskFusion > 0 ? 1 : 0),
            ExtendedFusionLifecycleRiskAfterPolicy = extendedBestFusion?.LifecycleRiskFusion ?? 0,
            ExtendedFusionNewlyRiskySamples = extendedBestFusion?.NewlyRiskySamples.Count ?? 0,
            A3ExpandedRecallAfterPolicy = a3BestExpansion?.RecallAfterExpansion ?? 0,
            A3ExpandedRiskAfterPolicy = a3BestExpansion?.RiskAfterPolicy ?? 0,
            A3ExpandedMustNotHitRiskAfterPolicy = a3BestExpansion?.MustNotHitRiskAfterPolicy ?? 0,
            A3ExpandedLifecycleRiskAfterPolicy = a3BestExpansion?.LifecycleRiskAfterPolicy ?? 0,
            ExtendedExpandedRecallAfterPolicy = extendedBestExpansion?.RecallAfterExpansion ?? 0,
            ExtendedExpandedRiskAfterPolicy = extendedBestExpansion?.RiskAfterPolicy ?? 0,
            ExtendedExpandedMustNotHitRiskAfterPolicy = extendedBestExpansion?.MustNotHitRiskAfterPolicy ?? 0,
            ExtendedExpandedLifecycleRiskAfterPolicy = extendedBestExpansion?.LifecycleRiskAfterPolicy ?? 0,
            Conditions = conditions,
            FailReasons = failReasons,
            Warnings = failReasons.Length == 0
                ? Array.Empty<string>()
                : ["readiness gate 未通过时不得把 vector 接入 retrieval shadow。P15 gate 仍需由 scripts/eval-gate-p15.ps1 单独验证。"]
        };
    }

    public static string BuildGateMarkdown(VectorRetrievalShadowReadinessGateReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Retrieval Shadow Readiness Gate");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.CreatedAt:O}");
        builder.AppendLine();
        builder.AppendLine($"- Passed: `{report.Passed}`");
        builder.AppendLine($"- A3RecallAfterPolicy: `{report.A3RecallAfterPolicy:P2}`");
        builder.AppendLine($"- A3RiskAfterPolicy: `{report.A3RiskAfterPolicy}`");
        builder.AppendLine($"- A3MustNotHitRiskAfterPolicy: `{report.A3MustNotHitRiskAfterPolicy:P2}`");
        builder.AppendLine($"- A3LifecycleRiskAfterPolicy: `{report.A3LifecycleRiskAfterPolicy:P2}`");
        builder.AppendLine($"- ExtendedRecallAfterPolicy: `{report.ExtendedRecallAfterPolicy:P2}`");
        builder.AppendLine($"- ExtendedRiskAfterPolicy: `{report.ExtendedRiskAfterPolicy}`");
        if (report.A3FusionRecallAfterPolicy > 0 || report.ExtendedFusionRecallAfterPolicy > 0)
        {
            builder.AppendLine($"- A3FusionRecallAfterPolicy: `{report.A3FusionRecallAfterPolicy:P2}`");
            builder.AppendLine($"- A3FusionRiskAfterPolicy: `{report.A3FusionRiskAfterPolicy}`");
            builder.AppendLine($"- A3FusionLifecycleRiskAfterPolicy: `{report.A3FusionLifecycleRiskAfterPolicy:P2}`");
            builder.AppendLine($"- A3FusionNewlyRiskySamples: `{report.A3FusionNewlyRiskySamples}`");
            builder.AppendLine($"- ExtendedFusionRecallAfterPolicy: `{report.ExtendedFusionRecallAfterPolicy:P2}`");
            builder.AppendLine($"- ExtendedFusionRiskAfterPolicy: `{report.ExtendedFusionRiskAfterPolicy}`");
            builder.AppendLine($"- ExtendedFusionLifecycleRiskAfterPolicy: `{report.ExtendedFusionLifecycleRiskAfterPolicy:P2}`");
            builder.AppendLine($"- ExtendedFusionNewlyRiskySamples: `{report.ExtendedFusionNewlyRiskySamples}`");
        }
        if (report.A3ExpandedRecallAfterPolicy > 0 || report.ExtendedExpandedRecallAfterPolicy > 0)
        {
            builder.AppendLine($"- A3ExpandedRecallAfterPolicy: `{report.A3ExpandedRecallAfterPolicy:P2}`");
            builder.AppendLine($"- A3ExpandedRiskAfterPolicy: `{report.A3ExpandedRiskAfterPolicy}`");
            builder.AppendLine($"- A3ExpandedMustNotHitRiskAfterPolicy: `{report.A3ExpandedMustNotHitRiskAfterPolicy:P2}`");
            builder.AppendLine($"- A3ExpandedLifecycleRiskAfterPolicy: `{report.A3ExpandedLifecycleRiskAfterPolicy:P2}`");
            builder.AppendLine($"- ExtendedExpandedRecallAfterPolicy: `{report.ExtendedExpandedRecallAfterPolicy:P2}`");
            builder.AppendLine($"- ExtendedExpandedRiskAfterPolicy: `{report.ExtendedExpandedRiskAfterPolicy}`");
            builder.AppendLine($"- ExtendedExpandedMustNotHitRiskAfterPolicy: `{report.ExtendedExpandedMustNotHitRiskAfterPolicy:P2}`");
            builder.AppendLine($"- ExtendedExpandedLifecycleRiskAfterPolicy: `{report.ExtendedExpandedLifecycleRiskAfterPolicy:P2}`");
        }

        builder.AppendLine();
        builder.AppendLine("| Condition | Passed |");
        builder.AppendLine("|---|---:|");
        foreach (var condition in report.Conditions.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {condition.Key} | {condition.Value} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Fail Reasons");
        if (report.FailReasons.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var reason in report.FailReasons)
            {
                builder.AppendLine($"- {reason}");
            }
        }

        return builder.ToString();
    }

    private async Task<IReadOnlyList<BasePreview>> BuildBasePreviewsAsync(
        string operationId,
        IReadOnlyList<ContextEvalSample> samples,
        string workspaceId,
        string collectionId,
        string profileId,
        CancellationToken cancellationToken)
    {
        var previews = new List<BasePreview>();
        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preview = await _previewService.PreviewAsync(new VectorQueryPreviewRequest
            {
                OperationId = $"{operationId}:{sample.Id}:{profileId}:base",
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                QueryText = sample.Query,
                TopK = DiagnosticTopK,
                ProfileId = profileId,
                IncludeVector = false,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["mode"] = sample.Mode,
                    ["createdFrom"] = "vector_safe_recall_recovery"
                }
            }, cancellationToken).ConfigureAwait(false);
            previews.Add(new BasePreview(sample, preview));
        }

        return previews;
    }

    private IReadOnlyList<VectorSafeRecallRecoverySweepResult> BuildSweepResults(
        IReadOnlyList<ContextEvalSample> samples,
        IReadOnlyList<BasePreview> basePreviews,
        IReadOnlyList<VectorRecallLossMiss> belowTopKMisses)
    {
        var results = new List<VectorSafeRecallRecoverySweepResult>();
        foreach (var profileId in SweepProfiles)
        {
            foreach (var topK in SweepTopKs)
            {
                foreach (var minSimilarity in SweepMinSimilarities)
                {
                    foreach (var layerFilter in SweepLayerFilters)
                    {
                        var profile = WithMinSimilarity(_profileRegistry.Resolve(profileId), minSimilarity);
                        var sampleResults = basePreviews
                            .Select(item => BuildSampleResult(item, profile, topK, layerFilter))
                            .ToArray();
                        var summary = VectorQueryShadowEvalRunner.BuildReport("vector-safe-recall-recovery-sweep", sampleResults);
                        var recovered = CountRecoveredBelowTopK(sampleResults, belowTopKMisses);
                        results.Add(new VectorSafeRecallRecoverySweepResult
                        {
                            ConfigurationId = $"{profileId}:top{topK}:min{minSimilarity:F2}:{layerFilter}",
                            ProfileId = profileId,
                            TopK = topK,
                            MinSimilarity = minSimilarity,
                            LayerFilter = layerFilter,
                            BelowTopKMissCount = belowTopKMisses.Count,
                            RecoveredBelowTopKCount = recovered,
                            RecoveryRate = belowTopKMisses.Count == 0 ? 1.0 : (double)recovered / belowTopKMisses.Count,
                            MustHitRecallAfterPolicy = summary.MustHitRecallAfterPolicy,
                            MustHitMrrAfterPolicy = CalculateMrr(sampleResults),
                            RiskAfterPolicy = summary.RiskAfterPolicy,
                            MustNotHitRiskAfterPolicy = summary.MustNotHitRiskAfterPolicy,
                            LifecycleRiskAfterPolicy = summary.LifecycleRiskAfterPolicy,
                            NoCandidateCount = summary.NoCandidateCount,
                            Recommendation = RecommendSweep(summary, recovered, belowTopKMisses.Count)
                        });
                    }
                }
            }
        }

        return results;
    }

    private VectorQueryShadowEvalSample BuildSampleResult(
        BasePreview item,
        VectorQueryProfile profile,
        int topK,
        string layerFilter)
    {
        var candidates = item.Preview.Candidates
            .Where(candidate => MatchesLayerFilter(candidate, layerFilter))
            .Take(topK)
            .Select((candidate, index) => ReevaluateCandidate(candidate, profile, index + 1))
            .ToArray();
        var preview = new VectorQueryPreviewResult
        {
            OperationId = $"{item.Preview.OperationId}:{profile.ProfileId}:{topK}:{layerFilter}",
            WorkspaceId = item.Preview.WorkspaceId,
            CollectionId = item.Preview.CollectionId,
            QueryText = item.Preview.QueryText,
            TopK = topK,
            ProfileId = profile.ProfileId,
            MinSimilarity = profile.MinSimilarity,
            Candidates = candidates,
            Diagnostics = item.Preview.Diagnostics,
            CreatedAt = item.Preview.CreatedAt
        };
        return VectorQueryShadowEvalRunner.BuildSampleResult(item.Sample, preview, 0.25);
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

    private IReadOnlyList<VectorBlockedMustHitAuditRecord> BuildBlockedAudit(
        VectorRecallLossAuditReport baseline,
        IReadOnlyList<BasePreview> normalBroadPreviews,
        IReadOnlyList<VectorIndexEntry> indexEntries)
    {
        var previewBySample = normalBroadPreviews
            .ToDictionary(item => item.Sample.Id, item => item.Preview, StringComparer.OrdinalIgnoreCase);
        return baseline.MissedMustHits
            .Where(item => string.Equals(item.MissReason, VectorRecallLossMissReasons.BlockedByEligibilityPolicy, StringComparison.OrdinalIgnoreCase))
            .Select(miss =>
            {
                previewBySample.TryGetValue(miss.SampleId, out var preview);
                var candidate = preview?.Candidates.FirstOrDefault(item => EvalIdMatches(miss.MustHitItemId, item.ItemId));
                var entry = indexEntries.FirstOrDefault(item => EvalIdMatches(miss.MustHitItemId, item.ItemId));
                return BuildBlockedAuditRecord(miss, candidate, entry);
            })
            .ToArray();
    }

    private VectorBlockedMustHitAuditRecord BuildBlockedAuditRecord(
        VectorRecallLossMiss miss,
        VectorQueryPreviewCandidate? candidate,
        VectorIndexEntry? entry)
    {
        var lifecycle = candidate is not null
            ? _lifecycleResolver.Resolve(candidate)
            : entry is not null
                ? _lifecycleResolver.Resolve(entry)
                : new VectorSourceLifecycleMetadata();
        var blockedReasons = candidate?.BlockedReasons
            ?? (string.IsNullOrWhiteSpace(miss.BlockedReason)
                ? Array.Empty<string>()
                : miss.BlockedReason.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var classification = ClassifyBlockedMustHit(blockedReasons, lifecycle);
        var canBeSafelyAllowed = CanBeSafelyAllowed(classification);

        return new VectorBlockedMustHitAuditRecord
        {
            SampleId = miss.SampleId,
            Mode = miss.Mode,
            Intent = miss.Intent,
            MustHitItemId = miss.MustHitItemId,
            BlockedReasons = blockedReasons,
            ResolvedLifecycle = string.IsNullOrWhiteSpace(lifecycle.Lifecycle) ? "Unknown" : lifecycle.Lifecycle,
            MetadataCompleteness = lifecycle.IsLifecycleMetadataComplete ? "Complete" : "Incomplete",
            ReplacementState = lifecycle.HasReplacementInfo
                ? "HasReplacement"
                : lifecycle.MissingReplacementInfo ? "MissingReplacement" : "NotRequired",
            TargetSection = candidate?.TargetSection ?? VectorQueryTargetSections.Excluded,
            CanBeSafelyAllowed = canBeSafelyAllowed,
            MustRemainBlockedReason = canBeSafelyAllowed ? string.Empty : BuildMustRemainBlockedReason(classification, blockedReasons),
            RecommendedRepair = BuildRecommendedRepair(classification),
            Classification = classification
        };
    }

    private static string ClassifyBlockedMustHit(
        IReadOnlyList<string> blockedReasons,
        VectorSourceLifecycleMetadata lifecycle)
    {
        if (blockedReasons.Any(IsHardDiagnosticBlock))
        {
            return VectorBlockedMustHitClassifications.ShouldRemainBlocked;
        }

        if (blockedReasons.Contains(VectorCandidateBlockedReason.DeprecatedCandidateBlocked, StringComparer.OrdinalIgnoreCase))
        {
            return VectorBlockedMustHitClassifications.DeprecatedMustHitBlockedCorrectly;
        }

        if (blockedReasons.Contains(VectorCandidateBlockedReason.HistoricalSourceRequiresAuditProfile, StringComparer.OrdinalIgnoreCase)
            || blockedReasons.Contains(VectorCandidateBlockedReason.HistoricalCandidateBlocked, StringComparer.OrdinalIgnoreCase)
            || lifecycle.RequiresAuditProfile)
        {
            return VectorBlockedMustHitClassifications.HistoricalMustHitRequiresAuditProfile;
        }

        if (blockedReasons.Contains(VectorCandidateBlockedReason.UnknownLifecycleBlocked, StringComparer.OrdinalIgnoreCase)
            || blockedReasons.Contains(VectorCandidateBlockedReason.LifecycleMetadataIncompleteBlocked, StringComparer.OrdinalIgnoreCase)
            || blockedReasons.Contains(VectorCandidateBlockedReason.ReplacementMetadataMissingBlocked, StringComparer.OrdinalIgnoreCase)
            || blockedReasons.Contains(VectorCandidateBlockedReason.LegacySourceRequiresLifecycleMetadata, StringComparer.OrdinalIgnoreCase))
        {
            return VectorBlockedMustHitClassifications.MetadataRepairNeeded;
        }

        if (blockedReasons.Contains(VectorCandidateBlockedReason.UnsupportedLayer, StringComparer.OrdinalIgnoreCase))
        {
            return VectorBlockedMustHitClassifications.LayerFilterTooStrict;
        }

        if (blockedReasons.Contains(VectorCandidateBlockedReason.UnsupportedItemKind, StringComparer.OrdinalIgnoreCase)
            || blockedReasons.Contains(VectorCandidateBlockedReason.CandidateLifecycleBlocked, StringComparer.OrdinalIgnoreCase)
            || blockedReasons.Contains(VectorCandidateBlockedReason.DiagnosticsOnlyItemKindBlocked, StringComparer.OrdinalIgnoreCase))
        {
            return VectorBlockedMustHitClassifications.ProfileTooNarrow;
        }

        if (blockedReasons.Contains(VectorCandidateBlockedReason.SimilarityBelowThreshold, StringComparer.OrdinalIgnoreCase))
        {
            return VectorBlockedMustHitClassifications.RequiresRankerFusion;
        }

        return VectorBlockedMustHitClassifications.RequiresManualReview;
    }

    private static bool CanBeSafelyAllowed(string classification)
    {
        return string.Equals(classification, VectorBlockedMustHitClassifications.ProfileTooNarrow, StringComparison.OrdinalIgnoreCase)
               || string.Equals(classification, VectorBlockedMustHitClassifications.LayerFilterTooStrict, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildMustRemainBlockedReason(
        string classification,
        IReadOnlyList<string> blockedReasons)
    {
        return classification switch
        {
            VectorBlockedMustHitClassifications.DeprecatedMustHitBlockedCorrectly => "normal profile 不应直接放行 deprecated mustHit；应使用 audit profile 或 audit section。",
            VectorBlockedMustHitClassifications.HistoricalMustHitRequiresAuditProfile => "historical/deprecated mustHit 需要 audit profile，不能进入 normal_context。",
            VectorBlockedMustHitClassifications.MetadataRepairNeeded => "metadata 未完整前不能绕过 lifecycle gate。",
            VectorBlockedMustHitClassifications.ShouldRemainBlocked => $"存在硬诊断阻断：{string.Join(",", blockedReasons)}。",
            _ => "需要人工 review 后才能决定是否调整 profile。"
        };
    }

    private static string BuildRecommendedRepair(string classification)
    {
        return classification switch
        {
            VectorBlockedMustHitClassifications.MetadataRepairNeeded => "补齐 lifecycle/reviewStatus/replacement metadata 后重新 reindex，再重跑 shadow。",
            VectorBlockedMustHitClassifications.ProfileTooNarrow => "仅在保持 risk=0 的前提下评估 profile allowlist 或 diagnostics section routing。",
            VectorBlockedMustHitClassifications.LayerFilterTooStrict => "评估 layer filter 是否过窄；不得放松 lifecycle safety gate。",
            VectorBlockedMustHitClassifications.HistoricalMustHitRequiresAuditProfile => "将该类查询分流到 audit-v1，并保持 targetSection 为 audit_context/diagnostics_only。",
            VectorBlockedMustHitClassifications.DeprecatedMustHitBlockedCorrectly => "保持 normal profile 阻断；仅在 audit path 中观察。",
            VectorBlockedMustHitClassifications.RequiresRankerFusion => "保留 preview-only，等待 ranker fusion 或人工策略判断。",
            VectorBlockedMustHitClassifications.ShouldRemainBlocked => "保持阻断并修复底层 diagnostics。",
            _ => "需要人工 review。"
        };
    }

    private static bool IsHardDiagnosticBlock(string reason)
    {
        return string.Equals(reason, VectorCandidateBlockedReason.RejectedCandidateBlocked, StringComparison.OrdinalIgnoreCase)
               || string.Equals(reason, VectorCandidateBlockedReason.DuplicateVectorEntryBlocked, StringComparison.OrdinalIgnoreCase)
               || string.Equals(reason, VectorCandidateBlockedReason.OrphanVectorEntryBlocked, StringComparison.OrdinalIgnoreCase)
               || string.Equals(reason, VectorCandidateBlockedReason.DimensionMismatchBlocked, StringComparison.OrdinalIgnoreCase)
               || string.Equals(reason, VectorCandidateBlockedReason.StaleEmbeddingBlocked, StringComparison.OrdinalIgnoreCase);
    }

    private static VectorSafeRecallRecoverySweepResult? SelectBestSafeSweep(
        IReadOnlyList<VectorSafeRecallRecoverySweepResult> results)
    {
        return results
            .Where(item => item.RiskAfterPolicy == 0
                           && item.MustNotHitRiskAfterPolicy == 0
                           && item.LifecycleRiskAfterPolicy == 0)
            .OrderByDescending(item => item.MustHitRecallAfterPolicy)
            .ThenByDescending(item => item.RecoveredBelowTopKCount)
            .ThenByDescending(item => item.MustHitMrrAfterPolicy)
            .ThenBy(item => item.TopK)
            .FirstOrDefault()
            ?? results.OrderBy(item => item.RiskAfterPolicy).ThenByDescending(item => item.MustHitRecallAfterPolicy).FirstOrDefault();
    }

    private static int CountRecoveredBelowTopK(
        IReadOnlyList<VectorQueryShadowEvalSample> sampleResults,
        IReadOnlyList<VectorRecallLossMiss> belowTopKMisses)
    {
        return belowTopKMisses.Count(miss =>
            sampleResults.Any(sample =>
                string.Equals(sample.SampleId, miss.SampleId, StringComparison.OrdinalIgnoreCase)
                && sample.MustHitMatchedAfterPolicy.Any(matched => EvalIdMatches(miss.MustHitItemId, matched))));
    }

    private static string Recommend(
        VectorRecallLossAuditReport baseline,
        VectorSafeRecallRecoverySweepResult? best,
        IReadOnlyList<VectorBlockedMustHitAuditRecord> blockedAudit)
    {
        if (baseline.RiskAfterPolicy > 0)
        {
            return VectorQueryShadowRecommendations.BlockedByRisk;
        }

        if (baseline.MustHitRecallAfterPolicy >= ReadinessRecallThreshold)
        {
            return VectorQueryShadowRecommendations.ReadyForRetrievalShadow;
        }

        if (best is not null
            && best.MustHitRecallAfterPolicy >= ReadinessRecallThreshold
            && best.RiskAfterPolicy == 0
            && best.MustNotHitRiskAfterPolicy == 0
            && best.LifecycleRiskAfterPolicy == 0)
        {
            return VectorQueryShadowRecommendations.ReadyForRetrievalShadow;
        }

        if (blockedAudit.Any(item =>
                string.Equals(item.Classification, VectorBlockedMustHitClassifications.HistoricalMustHitRequiresAuditProfile, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Classification, VectorBlockedMustHitClassifications.MetadataRepairNeeded, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Classification, VectorBlockedMustHitClassifications.ProfileTooNarrow, StringComparison.OrdinalIgnoreCase)))
        {
            return VectorQueryShadowRecommendations.NeedsProfileTuning;
        }

        return VectorQueryShadowRecommendations.KeepPreviewOnly;
    }

    private static IReadOnlyList<string> BuildWarnings(
        VectorRecallLossAuditReport baseline,
        VectorSafeRecallRecoverySweepResult? best,
        IReadOnlyList<VectorBlockedMustHitAuditRecord> blockedAudit)
    {
        var warnings = new List<string>();
        if (baseline.RiskAfterPolicy > 0 || best?.RiskAfterPolicy > 0)
        {
            warnings.Add("safe recall recovery 发现 riskAfterPolicy 非 0，不得进入 retrieval shadow。");
        }

        if (blockedAudit.Any(item => !item.CanBeSafelyAllowed))
        {
            warnings.Add("部分被阻断 mustHit 不能在 normal profile 下直接放行，应走 audit/profile tuning 或 metadata repair。");
        }

        return warnings;
    }

    private static string RecommendSweep(
        VectorQueryShadowEvalReport summary,
        int recovered,
        int belowTopKMissCount)
    {
        if (summary.RiskAfterPolicy > 0 || summary.MustNotHitRiskAfterPolicy > 0 || summary.LifecycleRiskAfterPolicy > 0)
        {
            return VectorQueryShadowRecommendations.BlockedByRisk;
        }

        if (summary.MustHitRecallAfterPolicy >= ReadinessRecallThreshold)
        {
            return VectorQueryShadowRecommendations.ReadyForRetrievalShadow;
        }

        return recovered > 0 && belowTopKMissCount > 0
            ? VectorQueryShadowRecommendations.NeedsProfileTuning
            : VectorQueryShadowRecommendations.KeepPreviewOnly;
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
        return !string.IsNullOrWhiteSpace(expected)
               && !string.IsNullOrWhiteSpace(candidateId)
               && (string.Equals(expected, candidateId, StringComparison.OrdinalIgnoreCase)
                   || candidateId.EndsWith(expected, StringComparison.OrdinalIgnoreCase)
                   || expected.EndsWith(candidateId, StringComparison.OrdinalIgnoreCase));
    }

    private static void AppendReport(StringBuilder builder, string title, VectorSafeRecallRecoveryReport report)
    {
        builder.AppendLine($"## {title} Summary");
        builder.AppendLine();
        builder.AppendLine($"- Samples: `{report.Samples}`");
        builder.AppendLine($"- Provider: `{report.ProviderId}`");
        builder.AppendLine($"- Model: `{report.EmbeddingModel}`");
        builder.AppendLine($"- BaselineRecallAfterPolicy: `{report.BaselineRecallAfterPolicy:P2}`");
        builder.AppendLine($"- BaselineMRR: `{report.BaselineMrrAfterPolicy:F4}`");
        builder.AppendLine($"- BaselineRiskAfterPolicy: `{report.BaselineRiskAfterPolicy}`");
        builder.AppendLine($"- BelowTopKMissCount: `{report.BelowTopKMissCount}`");
        builder.AppendLine($"- BlockedMustHitCount: `{report.BlockedMustHitCount}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        if (report.BestSafeSweep is not null)
        {
            builder.AppendLine($"- BestSafeSweep: `{report.BestSafeSweep.ConfigurationId}`");
            builder.AppendLine($"- BestRecallAfterPolicy: `{report.BestSafeSweep.MustHitRecallAfterPolicy:P2}`");
            builder.AppendLine($"- BestRiskAfterPolicy: `{report.BestSafeSweep.RiskAfterPolicy}`");
            builder.AppendLine($"- RecoveredBelowTopK: `{report.BestSafeSweep.RecoveredBelowTopKCount}/{report.BestSafeSweep.BelowTopKMissCount}`");
        }

        builder.AppendLine();
        builder.AppendLine("| Configuration | RecallAfter | MRR | RiskAfter | MustNotRisk | LifecycleRisk | RecoveredBelowTopK | Recommendation |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---|");
        foreach (var result in report.SweepResults
                     .OrderByDescending(item => item.MustHitRecallAfterPolicy)
                     .ThenBy(item => item.RiskAfterPolicy)
                     .Take(20))
        {
            builder.AppendLine($"| {result.ConfigurationId} | {result.MustHitRecallAfterPolicy:P2} | {result.MustHitMrrAfterPolicy:F4} | {result.RiskAfterPolicy} | {result.MustNotHitRiskAfterPolicy:P2} | {result.LifecycleRiskAfterPolicy:P2} | {result.RecoveredBelowTopKCount}/{result.BelowTopKMissCount} | {result.Recommendation} |");
        }

        builder.AppendLine();
        builder.AppendLine("### Blocked MustHit Classification");
        builder.AppendLine();
        builder.AppendLine("| Classification | Count |");
        builder.AppendLine("|---|---:|");
        foreach (var pair in report.BlockedClassificationCounts
                     .OrderByDescending(item => item.Value)
                     .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {pair.Key} | {pair.Value} |");
        }

        builder.AppendLine();
        builder.AppendLine("| Sample | Intent | MustHit | Reasons | Lifecycle | Complete | Replacement | CanAllow | Classification | Repair |");
        builder.AppendLine("|---|---|---|---|---|---|---|---:|---|---|");
        foreach (var item in report.BlockedMustHitAudit.Take(100))
        {
            builder.AppendLine($"| {item.SampleId} | {item.Intent} | {item.MustHitItemId} | {Sanitize(string.Join(",", item.BlockedReasons))} | {item.ResolvedLifecycle} | {item.MetadataCompleteness} | {item.ReplacementState} | {item.CanBeSafelyAllowed} | {item.Classification} | {Sanitize(item.RecommendedRepair)} |");
        }

        builder.AppendLine();
    }

    private static string Sanitize(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value.Replace("|", "/", StringComparison.Ordinal);
    }

    private sealed record BasePreview(ContextEvalSample Sample, VectorQueryPreviewResult Preview);
}
