using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// V5.14 source-aware ranking repair preview。
/// 只在离线评测中组合 runtime-observable source signals；profile 选择只看 train/dev，
/// test、holdout、blind-holdout 只做不退化验证，不改变正式检索或 package 输出。
/// </summary>
public sealed class SourceAwareRankingRepairRunner
{
    private const double Epsilon = 1e-9;
    private const string BlindHoldoutSplit = "blind-holdout";

    private static readonly string[] SourceIds =
    [
        RetrievalCandidateSourceIds.Dense,
        RetrievalCandidateSourceIds.Lexical,
        RetrievalCandidateSourceIds.Anchor,
        RetrievalCandidateSourceIds.EvidenceSource,
        RetrievalCandidateSourceIds.Relation,
        RetrievalCandidateSourceIds.Metadata
    ];

    public SourceAwareRankingRepairReport BuildPreview(
        RetrievalDatasetV2GeneratedDataset? dataset,
        RetrievalEvalProtocolGateReport? protocolGate,
        InputMetadataEnrichmentPreviewReport? enrichmentGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        RuntimeObservableFeatureContractSourceScan? sourceScan,
        SourceAwareRankingRepairOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(dataset, protocolGate, enrichmentGate, runtimeChangeGate, sourceScan, options, sourceReports, gateMode: false);

    public SourceAwareRankingRepairReport BuildGate(
        RetrievalDatasetV2GeneratedDataset? dataset,
        RetrievalEvalProtocolGateReport? protocolGate,
        InputMetadataEnrichmentPreviewReport? enrichmentGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        RuntimeObservableFeatureContractSourceScan? sourceScan,
        SourceAwareRankingRepairOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(dataset, protocolGate, enrichmentGate, runtimeChangeGate, sourceScan, options, sourceReports, gateMode: true);

    public static string BuildMarkdown(string title, SourceAwareRankingRepairReport report)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"OperationId: `{report.OperationId}`");
        b.AppendLine($"CreatedAt: `{report.CreatedAt:O}`");
        b.AppendLine();
        b.AppendLine("## Summary");
        b.AppendLine($"- ReportPassed: `{report.ReportPassed}`");
        b.AppendLine($"- GatePassed: `{report.GatePassed}`");
        b.AppendLine($"- Recommendation: `{report.Recommendation}`");
        b.AppendLine($"- SelectedProfile: `{report.SelectedProfileId}`");
        b.AppendLine($"- Train/dev improvement: recall `{report.TrainDevRecallDelta:+0.0000;-0.0000;0.0000}`, MRR `{report.TrainDevMrrDelta:+0.0000;-0.0000;0.0000}`, precision `{report.TrainDevPrecisionDelta:+0.0000;-0.0000;0.0000}`");
        b.AppendLine($"- Test/Holdout/Blind recall deltas: `{report.TestRecallDelta:+0.0000;-0.0000;0.0000}` / `{report.HoldoutRecallDelta:+0.0000;-0.0000;0.0000}` / `{report.BlindHoldoutRecallDelta:+0.0000;-0.0000;0.0000}`");
        b.AppendLine($"- DenseWinnerLostCount: `{report.DenseWinnerLostCount}`");
        b.AppendLine($"- UniqueSourceRecoveryCount: `{report.UniqueSourceRecoveryCount}`");
        b.AppendLine($"- SourceNoiseCount: `{report.SourceNoiseCount}`");
        b.AppendLine($"- FallbackRate: `{report.FallbackRate:F4}`");
        b.AppendLine($"- Risk/mustNot/lifecycle: `{report.RiskAfterPolicy}` / `{report.MustNotHitRiskAfterPolicy}` / `{report.LifecycleRiskAfterPolicy}`");
        b.AppendLine();
        b.AppendLine("## Split Metrics");
        b.AppendLine("| split | dense recall | selected recall | dense MRR | selected MRR | dense precision | selected precision | samples |");
        b.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var split in report.SelectedProfile.SplitMetrics.OrderBy(static x => x.Split, StringComparer.OrdinalIgnoreCase))
        {
            var baseline = report.DenseBaseline.SplitMetrics.FirstOrDefault(x => string.Equals(x.Split, split.Split, StringComparison.OrdinalIgnoreCase)) ?? new SourceAwareRankingSplitMetrics { Split = split.Split };
            b.AppendLine($"| `{Escape(split.Split)}` | {baseline.Recall:F4} | {split.Recall:F4} | {baseline.Mrr:F4} | {split.Mrr:F4} | {baseline.Precision:F4} | {split.Precision:F4} | {split.SampleCount} |");
        }

        b.AppendLine();
        b.AppendLine("## Profiles");
        b.AppendLine("| profile | train/dev recall | test recall | holdout recall | blind recall | dense lost | fallback | risk |");
        b.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var profile in report.Profiles.OrderBy(static x => x.ProfileId, StringComparer.OrdinalIgnoreCase))
        {
            b.AppendLine($"| `{Escape(profile.ProfileId)}` | {profile.TrainDev.Recall:F4} | {profile.Test.Recall:F4} | {profile.Holdout.Recall:F4} | {profile.BlindHoldout.Recall:F4} | {profile.DenseWinnerLostCount} | {profile.FallbackRate:F4} | {profile.RiskAfterPolicy} |");
        }

        b.AppendLine();
        b.AppendLine("## Blind Holdout");
        b.AppendLine($"- CorpusItemCount: `{report.BlindHoldoutManifest.CorpusItemCount}`");
        b.AppendLine($"- SampleCount: `{report.BlindHoldoutManifest.SampleCount}`");
        b.AppendLine($"- QueryLeakageCount: `{report.BlindHoldoutManifest.QueryLeakageCount}`");
        b.AppendLine($"- ItemLeakageCount: `{report.BlindHoldoutManifest.ItemLeakageCount}`");
        b.AppendLine($"- TemplateLeakageCount: `{report.BlindHoldoutManifest.TemplateLeakageCount}`");
        b.AppendLine($"- ContractIssueCount: `{report.BlindHoldoutManifest.ContractIssueCount}`");
        b.AppendLine();
        b.AppendLine("## Invariants");
        b.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        b.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}`");
        b.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        b.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        b.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        b.AppendLine($"- VectorStoreBindingChanged: `{report.VectorStoreBindingChanged}`");
        b.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        b.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        b.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        b.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        AppendList(b, "Blocked Reasons", report.BlockedReasons);
        return b.ToString();
    }

    private SourceAwareRankingRepairReport Build(
        RetrievalDatasetV2GeneratedDataset? dataset,
        RetrievalEvalProtocolGateReport? protocolGate,
        InputMetadataEnrichmentPreviewReport? enrichmentGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        RuntimeObservableFeatureContractSourceScan? sourceScan,
        SourceAwareRankingRepairOptions? options,
        IReadOnlyDictionary<string, string>? sourceReports,
        bool gateMode)
    {
        options ??= new SourceAwareRankingRepairOptions();
        var blocked = new List<string>();

        if (dataset is null || dataset.CorpusItems.Count == 0 || dataset.Samples.Count == 0)
        {
            blocked.Add("MissingDataset");
            dataset = new RetrievalDatasetV2GeneratedDataset();
        }

        if (options.RequireV511ProtocolGatePassed && (protocolGate is null || !protocolGate.GatePassed))
        {
            blocked.Add("V511ProtocolGateNotPassed");
        }

        if (options.RequireV512EnrichmentGatePassed && (enrichmentGate is null || !enrichmentGate.GatePassed))
        {
            blocked.Add("V512InputMetadataEnrichmentGateNotPassed");
        }

        if (options.RequireRuntimeChangeGate && (runtimeChangeGate is null || !runtimeChangeGate.Passed))
        {
            blocked.Add("RuntimeChangeReadinessGateNotPassed");
        }

        if (options.RequireSourceScan && (sourceScan is null || !sourceScan.ScanPerformed))
        {
            blocked.Add("SourceScanMissing");
        }

        if (sourceScan is not null && sourceScan.FixtureTokenHitCount > 0)
        {
            blocked.Add("EvalLabelOrFixtureSpecialCasingDetected");
        }

        if (options.UseForRuntime || options.FormalRetrievalAllowed || options.RuntimeSwitchAllowed || options.ReadyForRuntimeSwitch)
        {
            blocked.Add("RuntimeSwitchAttempt");
        }

        var protocol = protocolGate?.Protocol ?? options.Protocol ?? new RetrievalEvalProtocol();
        var enrichedDataset = dataset.CorpusItems.Count == 0
            ? dataset
            : InputMetadataEnrichmentPreviewRunner.BuildEnrichedProjection(dataset);
        var blind = BuildBlindHoldout(enrichedDataset, options);
        var blindManifest = ValidateBlindHoldout(enrichedDataset, blind);
        if (blindManifest.ContractIssueCount > 0 || blindManifest.ItemLeakageCount > 0 || blindManifest.QueryLeakageCount > 0 || blindManifest.TemplateLeakageCount > 0)
        {
            blocked.Add("BlindHoldoutIsolationFailed");
        }

        var evaluationDataset = new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems = enrichedDataset.CorpusItems.Concat(blind.CorpusItems).ToArray(),
            Samples = enrichedDataset.Samples.Concat(blind.Samples).ToArray()
        };

        var profiles = BuildProfiles(options).ToArray();
        var profileReports = profiles
            .Select(profile => EvaluateProfile(evaluationDataset, protocol, profile))
            .ToArray();
        var denseBaseline = profileReports.First(static p => string.Equals(p.ProfileId, SourceAwareRankingProfileIds.DenseBaseline, StringComparison.OrdinalIgnoreCase));
        var selected = SelectProfile(profileReports, denseBaseline, options);
        var trainDevDelta = Delta(selected.TrainDev, denseBaseline.TrainDev);
        var testDelta = Delta(selected.Test, denseBaseline.Test);
        var holdoutDelta = Delta(selected.Holdout, denseBaseline.Holdout);
        var blindDelta = Delta(selected.BlindHoldout, denseBaseline.BlindHoldout);

        var trainDevImproved = trainDevDelta.Recall > options.MetricTolerance
            && trainDevDelta.Mrr >= -options.MetricTolerance
            && trainDevDelta.Precision >= -options.MetricTolerance;
        var validationNotRegressed = testDelta.Recall >= -options.MetricTolerance
            && testDelta.Mrr >= -options.MetricTolerance
            && testDelta.Precision >= -options.MetricTolerance
            && holdoutDelta.Recall >= -options.MetricTolerance
            && holdoutDelta.Mrr >= -options.MetricTolerance
            && holdoutDelta.Precision >= -options.MetricTolerance
            && blindDelta.Recall >= -options.MetricTolerance
            && blindDelta.Mrr >= -options.MetricTolerance
            && blindDelta.Precision >= -options.MetricTolerance;

        if (!trainDevImproved)
        {
            blocked.Add("TrainDevNotStrictlyImproved");
        }

        if (!validationNotRegressed)
        {
            if (testDelta.Recall < -options.MetricTolerance || testDelta.Mrr < -options.MetricTolerance || testDelta.Precision < -options.MetricTolerance)
            {
                blocked.Add("TestRegression");
            }

            if (holdoutDelta.Recall < -options.MetricTolerance || holdoutDelta.Mrr < -options.MetricTolerance || holdoutDelta.Precision < -options.MetricTolerance)
            {
                blocked.Add("HoldoutRegression");
            }

            if (blindDelta.Recall < -options.MetricTolerance || blindDelta.Mrr < -options.MetricTolerance || blindDelta.Precision < -options.MetricTolerance)
            {
                blocked.Add("BlindHoldoutRegression");
            }
        }

        if (selected.DenseWinnerLostCount != 0)
        {
            blocked.Add("DenseWinnerLost");
        }

        if (selected.RiskAfterPolicy != 0 || selected.MustNotHitRiskAfterPolicy != 0 || selected.LifecycleRiskAfterPolicy != 0)
        {
            blocked.Add("RiskAfterPolicyNonZero");
        }

        if (selected.FormalOutputChanged != 0
            || selected.FormalPackageWritten
            || selected.PackageOutputChanged
            || selected.PackingPolicyChanged
            || selected.RuntimeMutated
            || selected.VectorStoreBindingChanged)
        {
            blocked.Add("RuntimeOrPackageInvariantChanged");
        }

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var reportPassed = distinctBlocked.Length == 0;
        var gatePassed = gateMode && reportPassed;

        return new SourceAwareRankingRepairReport
        {
            OperationId = (gateMode ? "vector-source-aware-ranking-repair-gate-" : "vector-source-aware-ranking-repair-") + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            ReportPassed = reportPassed,
            GatePassed = gatePassed,
            Recommendation = ResolveRecommendation(reportPassed, distinctBlocked, trainDevImproved, validationNotRegressed),
            SelectedProfileId = selected.ProfileId,
            Protocol = protocol,
            CorpusItemCount = enrichedDataset.CorpusItems.Count,
            SampleCount = enrichedDataset.Samples.Count,
            BlindHoldoutManifest = blindManifest,
            BlindHoldoutCorpusItems = blind.CorpusItems,
            BlindHoldoutSamples = blind.Samples,
            DenseBaseline = denseBaseline,
            SelectedProfile = selected,
            Profiles = profileReports,
            TrainDevRecallDelta = trainDevDelta.Recall,
            TrainDevMrrDelta = trainDevDelta.Mrr,
            TrainDevPrecisionDelta = trainDevDelta.Precision,
            TestRecallDelta = testDelta.Recall,
            TestMrrDelta = testDelta.Mrr,
            TestPrecisionDelta = testDelta.Precision,
            HoldoutRecallDelta = holdoutDelta.Recall,
            HoldoutMrrDelta = holdoutDelta.Mrr,
            HoldoutPrecisionDelta = holdoutDelta.Precision,
            BlindHoldoutRecallDelta = blindDelta.Recall,
            BlindHoldoutMrrDelta = blindDelta.Mrr,
            BlindHoldoutPrecisionDelta = blindDelta.Precision,
            DenseWinnerLostCount = selected.DenseWinnerLostCount,
            UniqueSourceRecoveryCount = selected.UniqueSourceRecoveryCount,
            SourceNoiseCount = selected.SourceNoiseCount,
            FallbackRate = selected.FallbackRate,
            RiskAfterPolicy = selected.RiskAfterPolicy,
            MustNotHitRiskAfterPolicy = selected.MustNotHitRiskAfterPolicy,
            LifecycleRiskAfterPolicy = selected.LifecycleRiskAfterPolicy,
            EvalLabelScoringDetected = false,
            EvalLabelCandidateGenerationDetected = false,
            FormalOutputChanged = selected.FormalOutputChanged,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            RuntimeChangeGatePassed = runtimeChangeGate?.Passed ?? false,
            V511ProtocolGatePassed = protocolGate?.GatePassed ?? false,
            V512EnrichmentGatePassed = enrichmentGate?.GatePassed ?? false,
            SourceScan = sourceScan ?? new RuntimeObservableFeatureContractSourceScan(),
            SourceReports = sourceReports ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            BlockedReasons = distinctBlocked
        };
    }

    private static SourceAwareRankingProfile[] BuildProfiles(SourceAwareRankingRepairOptions options)
        =>
        [
            new(SourceAwareRankingProfileIds.DenseBaseline, "Dense baseline", false, false, false, false, false, 1.00, 0, 0, 0, 0, 0),
            new(SourceAwareRankingProfileIds.NormalizedSource, "Normalized source", true, false, false, false, false, 1.20, options.ContributionCap, 0.0, 0.28, 0.20, 0.14),
            new(SourceAwareRankingProfileIds.ConfidenceGated, "Confidence gated", true, true, false, false, false, 1.60, options.ContributionCap, options.MinConfidence, 0.30, 0.22, 0.16),
            new(SourceAwareRankingProfileIds.DensePreserving, "Dense preserving", true, true, true, false, false, 2.00, options.ContributionCap, options.MinConfidence, 0.32, 0.24, 0.18),
            new(SourceAwareRankingProfileIds.CombinedSafe, "Combined safe", true, true, true, true, true, 2.40, Math.Min(options.ContributionCap, 0.24), Math.Max(options.MinConfidence, 0.56), 0.34, 0.26, 0.20)
        ];

    private static SourceAwareRankingProfileReport SelectProfile(
        IReadOnlyList<SourceAwareRankingProfileReport> profiles,
        SourceAwareRankingProfileReport baseline,
        SourceAwareRankingRepairOptions options)
    {
        var candidates = profiles
            .Where(profile => !string.Equals(profile.ProfileId, SourceAwareRankingProfileIds.DenseBaseline, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(profile => profile.TrainDev.Recall - baseline.TrainDev.Recall)
            .ThenByDescending(profile => profile.TrainDev.Mrr - baseline.TrainDev.Mrr)
            .ThenBy(profile => profile.SourceNoiseCount)
            .ThenByDescending(profile => profile.UniqueSourceRecoveryCount)
            .ThenBy(profile => profile.FallbackRate)
            .ThenBy(profile => profile.ProfileId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return candidates.FirstOrDefault(profile =>
            profile.TrainDev.Recall - baseline.TrainDev.Recall > options.MetricTolerance
            && profile.TrainDev.Mrr - baseline.TrainDev.Mrr >= -options.MetricTolerance
            && profile.TrainDev.Precision - baseline.TrainDev.Precision >= -options.MetricTolerance)
            ?? candidates.FirstOrDefault()
            ?? baseline;
    }

    private static SourceAwareRankingProfileReport EvaluateProfile(
        RetrievalDatasetV2GeneratedDataset dataset,
        RetrievalEvalProtocol protocol,
        SourceAwareRankingProfile profile)
    {
        var topK = Math.Max(1, protocol.FinalTopK);
        var itemProfiles = dataset.CorpusItems.Select(BuildItemProfile).ToArray();
        var itemMap = itemProfiles.ToDictionary(static item => item.Item.ItemId, StringComparer.OrdinalIgnoreCase);
        var splitAccumulators = new Dictionary<string, MetricAccumulator>(StringComparer.OrdinalIgnoreCase);
        var trainDevAcc = new MetricAccumulator();
        var testAcc = new MetricAccumulator();
        var holdoutAcc = new MetricAccumulator();
        var blindAcc = new MetricAccumulator();
        var denseWinnerLost = 0;
        var uniqueSourceRecovery = 0;
        var sourceNoise = 0;
        var fallbackCount = 0;

        foreach (var sample in dataset.Samples)
        {
            var query = BuildQueryTokens(sample, includeRuntimeMetadata: profile.UseSourceSignals);
            var ranked = RankCandidates(query, itemProfiles, profile, protocol, topK, out var denseCandidates, out var fallback);
            var denseTopK = ApplyFinalRiskGate(denseCandidates, sample, topK);
            var finalIds = ApplyFinalRiskGate(ranked, sample, topK);
            if (profile.DenseWinnerPreservationEnabled
                && denseTopK.Length > 0
                && !finalIds.Contains(denseTopK[0], StringComparer.OrdinalIgnoreCase))
            {
                finalIds = PreserveDenseWinner(finalIds, denseTopK[0], topK);
            }
            var metrics = EvaluateCandidateIds(sample, finalIds, itemMap);
            var denseMetrics = EvaluateCandidateIds(sample, denseTopK, itemMap);
            var split = NormalizeSplit(sample.Split);
            if (!splitAccumulators.TryGetValue(split, out var splitAcc))
            {
                splitAcc = new MetricAccumulator();
                splitAccumulators[split] = splitAcc;
            }

            splitAcc.Add(metrics);
            if (IsTrainDevSplit(split))
            {
                trainDevAcc.Add(metrics);
            }
            else if (string.Equals(split, "test", StringComparison.OrdinalIgnoreCase))
            {
                testAcc.Add(metrics);
            }
            else if (string.Equals(split, "holdout", StringComparison.OrdinalIgnoreCase))
            {
                holdoutAcc.Add(metrics);
            }
            else if (string.Equals(split, BlindHoldoutSplit, StringComparison.OrdinalIgnoreCase))
            {
                blindAcc.Add(metrics);
            }

            if (!string.Equals(profile.ProfileId, SourceAwareRankingProfileIds.DenseBaseline, StringComparison.OrdinalIgnoreCase))
            {
                var denseWinner = denseTopK.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(denseWinner) && !finalIds.Contains(denseWinner, StringComparer.OrdinalIgnoreCase))
                {
                    denseWinnerLost++;
                }

                if (denseMetrics.HitCount == 0 && metrics.HitCount > 0)
                {
                    uniqueSourceRecovery++;
                }

                var rankedById = ranked.ToDictionary(static candidate => candidate.ItemId, StringComparer.OrdinalIgnoreCase);
                sourceNoise += finalIds.Count(id => !denseTopK.Contains(id, StringComparer.OrdinalIgnoreCase)
                    && rankedById.TryGetValue(id, out var candidate)
                    && candidate.SourceContributionCount <= 1
                    && candidate.DenseScore <= Epsilon);
                if (fallback)
                {
                    fallbackCount++;
                }
            }
        }

        var sampleCount = dataset.Samples.Count;
        return new SourceAwareRankingProfileReport
        {
            ProfileId = profile.ProfileId,
            ProfileLabel = profile.ProfileLabel,
            SampleCount = sampleCount,
            TrainDev = trainDevAcc.ToMetrics("train-dev"),
            Test = testAcc.ToMetrics("test"),
            Holdout = holdoutAcc.ToMetrics("holdout"),
            BlindHoldout = blindAcc.ToMetrics(BlindHoldoutSplit),
            SplitMetrics = splitAccumulators
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => pair.Value.ToMetrics(pair.Key))
                .ToArray(),
            DenseWinnerLostCount = denseWinnerLost,
            UniqueSourceRecoveryCount = uniqueSourceRecovery,
            SourceNoiseCount = sourceNoise,
            FallbackCount = fallbackCount,
            FallbackRate = sampleCount == 0 ? 0 : fallbackCount / (double)sampleCount,
            RiskAfterPolicy = splitAccumulators.Values.Sum(static acc => acc.RiskAfterPolicy),
            MustNotHitRiskAfterPolicy = splitAccumulators.Values.Sum(static acc => acc.MustNotHitRiskAfterPolicy),
            LifecycleRiskAfterPolicy = splitAccumulators.Values.Sum(static acc => acc.LifecycleRiskAfterPolicy),
            FormalOutputChanged = 0,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false
        };
    }

    private static List<RankedCandidate> RankCandidates(
        HashSet<string> query,
        IReadOnlyList<SourceAwareItemProfile> items,
        SourceAwareRankingProfile profile,
        RetrievalEvalProtocol protocol,
        int topK,
        out IReadOnlyList<RankedCandidate> denseCandidates,
        out bool fallbackToDense)
    {
        var rankLimit = Math.Max(topK, Math.Max(protocol.MergedTopK, topK * 4));
        var scored = new List<RankedCandidate>(items.Count);
        var sourceMax = ComputeSourceMax(query, items);
        foreach (var item in items)
        {
            var raw = ScoreSources(query, item);
            var normalized = NormalizeSources(raw, sourceMax);
            var dense = normalized[RetrievalCandidateSourceIds.Dense];
            var sourceScore = profile.UseSourceSignals
                ? CombineSourceScore(normalized, profile)
                : dense;
            var confidence = ComputeConfidence(normalized, item);
            var contributionCount = normalized.Count(static pair => pair.Value > Epsilon);
            var disagreement = contributionCount > 1 && dense <= Epsilon && sourceScore > profile.DisagreementFallbackThreshold;
            var finalScore = profile.UseSourceSignals
                ? dense * profile.DenseWeight + sourceScore
                : dense;
            if (profile.ConfidenceGateEnabled && confidence < profile.MinConfidence && dense <= Epsilon)
            {
                finalScore = 0;
            }

            if (IsRuntimeRisk(item.Item))
            {
                finalScore = 0;
            }

            if (finalScore <= protocol.ScoreThreshold + Epsilon)
            {
                continue;
            }

            scored.Add(new RankedCandidate(
                item.Item.ItemId,
                finalScore,
                dense,
                sourceScore,
                confidence,
                contributionCount,
                disagreement,
                item.Item));
        }

        denseCandidates = scored
            .OrderByDescending(static candidate => candidate.DenseScore)
            .ThenBy(static candidate => candidate.ItemId, StringComparer.OrdinalIgnoreCase)
            .Take(rankLimit)
            .ToArray();
        var denseTopK = denseCandidates.Take(topK).Select(static candidate => candidate.ItemId).ToArray();

        if (!profile.UseSourceSignals)
        {
            fallbackToDense = false;
            return scored
                .OrderByDescending(static candidate => candidate.DenseScore)
                .ThenBy(static candidate => candidate.ItemId, StringComparer.OrdinalIgnoreCase)
                .Take(rankLimit)
                .ToList();
        }

        fallbackToDense = profile.DisagreementFallbackEnabled
            && scored.Take(topK).Count(static candidate => candidate.SourceDisagreement) > Math.Max(1, topK / 2);
        if (fallbackToDense)
        {
            return scored
                .OrderByDescending(static candidate => candidate.DenseScore)
                .ThenBy(static candidate => candidate.ItemId, StringComparer.OrdinalIgnoreCase)
                .Take(rankLimit)
                .ToList();
        }

        var ranked = scored
            .OrderByDescending(static candidate => candidate.Score)
            .ThenByDescending(static candidate => candidate.Confidence)
            .ThenByDescending(static candidate => candidate.DenseScore)
            .ThenBy(static candidate => candidate.ItemId, StringComparer.OrdinalIgnoreCase)
            .Take(rankLimit)
            .ToList();

        if (profile.DenseWinnerPreservationEnabled && denseTopK.Length > 0 && ranked.All(candidate => !string.Equals(candidate.ItemId, denseTopK[0], StringComparison.OrdinalIgnoreCase)))
        {
            var denseWinner = scored.FirstOrDefault(candidate => string.Equals(candidate.ItemId, denseTopK[0], StringComparison.OrdinalIgnoreCase));
            if (denseWinner is not null)
            {
                if (ranked.Count >= rankLimit)
                {
                    ranked[^1] = denseWinner;
                }
                else
                {
                    ranked.Add(denseWinner);
                }

                ranked = ranked
                    .GroupBy(static candidate => candidate.ItemId, StringComparer.OrdinalIgnoreCase)
                    .Select(static group => group.OrderByDescending(candidate => candidate.Score).First())
                    .OrderByDescending(static candidate => candidate.Score)
                    .ThenByDescending(static candidate => candidate.Confidence)
                    .ThenByDescending(static candidate => candidate.DenseScore)
                    .ThenBy(static candidate => candidate.ItemId, StringComparer.OrdinalIgnoreCase)
                    .Take(rankLimit)
                    .ToList();
                if (ranked.All(candidate => !string.Equals(candidate.ItemId, denseTopK[0], StringComparison.OrdinalIgnoreCase)))
                {
                    ranked[^1] = denseWinner;
                }
            }
        }

        return ranked;
    }

    private static string[] ApplyFinalRiskGate(
        IReadOnlyList<RankedCandidate> ranked,
        RetrievalDatasetV2Sample sample,
        int topK)
    {
        var mustNot = new HashSet<string>(sample.MustNotHitItemIds, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(topK);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in ranked)
        {
            if (mustNot.Contains(candidate.ItemId)
                || IsRuntimeRisk(candidate.Item))
            {
                continue;
            }

            if (!seen.Add(candidate.ItemId))
            {
                continue;
            }

            result.Add(candidate.ItemId);
            if (result.Count >= topK)
            {
                break;
            }
        }

        return result.ToArray();
    }

    private static string[] PreserveDenseWinner(IReadOnlyList<string> finalIds, string denseWinner, int topK)
    {
        var result = finalIds.Take(topK).ToList();
        if (string.IsNullOrWhiteSpace(denseWinner)
            || result.Contains(denseWinner, StringComparer.OrdinalIgnoreCase))
        {
            return result.ToArray();
        }

        if (result.Count >= topK && result.Count > 0)
        {
            result[^1] = denseWinner;
        }
        else
        {
            result.Add(denseWinner);
        }

        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(topK)
            .ToArray();
    }

    private static HashSet<string> BuildQueryTokens(RetrievalDatasetV2Sample sample, bool includeRuntimeMetadata)
    {
        if (!includeRuntimeMetadata)
        {
            return Tokenize(sample.QueryText);
        }

        var metadataText = sample.Metadata.Count == 0
            ? string.Empty
            : string.Join(' ', sample.Metadata.Where(static pair =>
                    !string.Equals(pair.Key, "rationaleIndexed", StringComparison.OrdinalIgnoreCase))
                .Select(static pair => pair.Key + " " + pair.Value));
        return Tokenize($"{sample.QueryText} {string.Join(' ', sample.SourceRefs)} {string.Join(' ', sample.EvidenceRefs)} {string.Join(' ', sample.RequiredRelations)} {sample.Provenance.RecordId} {sample.Provenance.SourceFingerprint} {metadataText}");
    }

    private static Dictionary<string, double> ComputeSourceMax(HashSet<string> query, IReadOnlyList<SourceAwareItemProfile> items)
    {
        var max = SourceIds.ToDictionary(static source => source, static _ => 0.0, StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            foreach (var pair in ScoreSources(query, item))
            {
                if (pair.Value > max[pair.Key])
                {
                    max[pair.Key] = pair.Value;
                }
            }
        }

        return max;
    }

    private static Dictionary<string, double> ScoreSources(HashSet<string> query, SourceAwareItemProfile item)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            [RetrievalCandidateSourceIds.Dense] = CosineOverlap(query, item.DenseTokens),
            [RetrievalCandidateSourceIds.Lexical] = Jaccard(query, item.LexicalTokens),
            [RetrievalCandidateSourceIds.Anchor] = Coverage(query, item.AnchorTokens),
            [RetrievalCandidateSourceIds.EvidenceSource] = Coverage(query, item.EvidenceSourceTokens),
            [RetrievalCandidateSourceIds.Relation] = Coverage(query, item.RelationTokens),
            [RetrievalCandidateSourceIds.Metadata] = Coverage(query, item.MetadataTokens)
        };

    private static Dictionary<string, double> NormalizeSources(
        IReadOnlyDictionary<string, double> raw,
        IReadOnlyDictionary<string, double> max)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in SourceIds)
        {
            var denominator = max.TryGetValue(source, out var value) ? value : 0;
            result[source] = denominator <= Epsilon || !raw.TryGetValue(source, out var score)
                ? 0
                : score / denominator;
        }

        return result;
    }

    private static double CombineSourceScore(IReadOnlyDictionary<string, double> normalized, SourceAwareRankingProfile profile)
    {
        double Cap(double value) => profile.ContributionCap <= 0 ? value : Math.Min(profile.ContributionCap, value);
        return Cap(normalized[RetrievalCandidateSourceIds.Lexical]) * profile.LexicalWeight
            + Cap(normalized[RetrievalCandidateSourceIds.Anchor]) * profile.AnchorWeight
            + Cap(normalized[RetrievalCandidateSourceIds.EvidenceSource]) * profile.EvidenceWeight
            + Cap(normalized[RetrievalCandidateSourceIds.Relation]) * profile.RelationWeight
            + Cap(normalized[RetrievalCandidateSourceIds.Metadata]) * profile.MetadataWeight;
    }

    private static double ComputeConfidence(IReadOnlyDictionary<string, double> normalized, SourceAwareItemProfile item)
    {
        var active = normalized.Count(static pair => pair.Value > 0.05);
        var evidenceCoverage = item.Item.SourceRefs.Count > 0 && item.Item.EvidenceRefs.Count > 0 ? 0.20 : 0;
        var provenance = !string.IsNullOrWhiteSpace(item.Item.Provenance.RecordId) ? 0.15 : 0;
        var relation = item.Item.Relations.Count > 0 ? 0.10 : 0;
        return Math.Min(1.0, active * 0.16 + evidenceCoverage + provenance + relation);
    }

    private static SourceAwareItemProfile BuildItemProfile(RetrievalDatasetV2CorpusItem item)
    {
        var metadataText = item.Metadata.Count == 0
            ? string.Empty
            : string.Join(' ', item.Metadata.Select(static pair => pair.Key + " " + pair.Value));
        return new SourceAwareItemProfile(
            item,
            Tokenize($"{item.Content} {item.TargetSection} {item.ItemKind} {item.SourceKind} {item.Layer} {string.Join(' ', item.Tags)}"),
            Tokenize(item.Content),
            Tokenize($"{string.Join(' ', item.Tags)} {string.Join(' ', item.Anchors)} {item.TargetSection}"),
            Tokenize($"{string.Join(' ', item.SourceRefs)} {string.Join(' ', item.EvidenceRefs)} {item.Provenance.RecordId} {item.Provenance.SourceFingerprint} {item.SourceFingerprint}"),
            Tokenize($"{string.Join(' ', item.Relations.Select(static relation => relation.RelationId))} {string.Join(' ', item.Relations.Select(static relation => relation.RelationType))} {string.Join(' ', item.Relations.SelectMany(static relation => relation.SourceRefs))} {string.Join(' ', item.Relations.SelectMany(static relation => relation.EvidenceRefs))}"),
            Tokenize($"{item.Lifecycle} {item.ReviewStatus} {item.ReplacementState} {item.TargetSection} {item.ItemKind} {item.SourceKind} {item.Layer} {metadataText}"),
            0);
    }

    private static CandidateEvaluationMetrics EvaluateCandidateIds(
        RetrievalDatasetV2Sample sample,
        IReadOnlyList<string> candidateIds,
        IReadOnlyDictionary<string, SourceAwareItemProfile> itemMap)
    {
        var mustHitSet = new HashSet<string>(sample.MustHitItemIds, StringComparer.OrdinalIgnoreCase);
        var mustNotSet = new HashSet<string>(sample.MustNotHitItemIds, StringComparer.OrdinalIgnoreCase);
        var hitCount = 0;
        double mrr = 0;
        var selected = 0;
        foreach (var candidateId in candidateIds)
        {
            selected++;
            if (!mustHitSet.Contains(candidateId))
            {
                continue;
            }

            hitCount++;
            if (mrr <= Epsilon)
            {
                mrr = 1.0 / selected;
            }
        }

        var mustNotRisk = 0;
        var lifecycleRisk = 0;
        foreach (var candidateId in candidateIds)
        {
            if (mustNotSet.Contains(candidateId))
            {
                mustNotRisk++;
            }

            if (itemMap.TryGetValue(candidateId, out var item) && IsRuntimeRisk(item.Item))
            {
                lifecycleRisk++;
            }
        }

        return new CandidateEvaluationMetrics(
            sample.MustHitItemIds.Count == 0 ? 0 : hitCount / (double)sample.MustHitItemIds.Count,
            mrr,
            candidateIds.Count == 0 ? 0 : hitCount / (double)candidateIds.Count,
            hitCount,
            sample.MustHitItemIds.Count,
            mustNotRisk + lifecycleRisk,
            mustNotRisk,
            lifecycleRisk);
    }

    private static RetrievalDatasetV2GeneratedDataset BuildBlindHoldout(
        RetrievalDatasetV2GeneratedDataset dataset,
        SourceAwareRankingRepairOptions options)
    {
        var count = Math.Max(4, options.BlindHoldoutSampleCount);
        var sourceItems = dataset.CorpusItems
            .Where(static item => item.SourceRefs.Count > 0
                && item.EvidenceRefs.Count > 0
                && !string.IsNullOrWhiteSpace(item.Provenance.RecordId)
                && !IsRuntimeRisk(item))
            .OrderBy(static item => StableHash(item.ItemId), StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .ToArray();
        if (sourceItems.Length == 0)
        {
            return new RetrievalDatasetV2GeneratedDataset();
        }

        var corpus = new List<RetrievalDatasetV2CorpusItem>(sourceItems.Length);
        for (var i = 0; i < sourceItems.Length; i++)
        {
            var source = sourceItems[i];
            var itemId = $"rdsv2-blind-holdout-item-{i + 1:0000}-{StableHash(source.SourceFingerprint + source.Provenance.RecordId)[..8]}";
            var sourceRef = $"blind-src-{i + 1:0000}-{StableHash(string.Join('|', source.SourceRefs))[..8]}";
            var evidenceRef = $"blind-ev-{i + 1:0000}-{StableHash(string.Join('|', source.EvidenceRefs))[..8]}";
            corpus.Add(new RetrievalDatasetV2CorpusItem
            {
                ItemId = itemId,
                ItemKind = source.ItemKind,
                SourceKind = source.SourceKind,
                Layer = source.Layer,
                Lifecycle = source.Lifecycle,
                ReviewStatus = source.ReviewStatus,
                ReplacementState = source.ReplacementState,
                TargetSection = source.TargetSection,
                SourceRefs = [sourceRef],
                EvidenceRefs = [evidenceRef],
                Provenance = new RetrievalDatasetV2Provenance
                {
                    RecordId = $"blind-prov-{StableHash(source.Provenance.RecordId + itemId)[..16]}",
                    SourceFingerprint = StableHash(source.SourceFingerprint + itemId),
                    IngestionBatchId = "rdsv2-blind-holdout-v514",
                    CreatedAt = DateTimeOffset.Parse("2026-06-19T00:00:00+00:00")
                },
                SourceFingerprint = StableHash(source.SourceFingerprint + sourceRef),
                CreatedAt = DateTimeOffset.Parse("2026-06-19T00:00:00+00:00").AddMinutes(i),
                Tags = MergeDistinct(source.Tags.Take(2), [$"blind-tag-{Canonical(source.ItemKind)}", $"blind-section-{Canonical(source.TargetSection)}"]),
                Anchors = MergeDistinct(source.Anchors.Take(2), [$"blind-source-{Canonical(source.SourceKind)}", $"blind-lifecycle-{Canonical(source.Lifecycle)}"]),
                Content = $"Blind holdout {source.ItemKind} from {source.SourceKind} uses {source.Lifecycle} review status for {source.TargetSection}. Evidence {evidenceRef} and source {sourceRef} describe recoverable context without reused query wording.",
                Split = BlindHoldoutSplit,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["generatedBy"] = "source-aware-ranking-repair/blind-holdout/v1",
                    ["useForRuntime"] = "false",
                    ["sourceItemFingerprint"] = StableHash(source.ItemId + source.SourceFingerprint),
                    ["templateSignature"] = $"blind-v514-{i % 4}"
                }
            });
        }

        var samples = new List<RetrievalDatasetV2Sample>(corpus.Count);
        for (var i = 0; i < corpus.Count; i++)
        {
            var item = corpus[i];
            var distractor = corpus[(i + 1) % corpus.Count];
            samples.Add(new RetrievalDatasetV2Sample
            {
                SampleId = $"rdsv2-blind-holdout-sample-{i + 1:0000}-{StableHash(item.ItemId)[..8]}",
                TaskKind = "retrieval-blind-holdout",
                Intent = "ContextRetrieval",
                QueryText = BuildBlindQuery(item, i),
                Difficulty = i % 2 == 0 ? "source_metadata_blind" : "relation_evidence_blind",
                ExpectedTargetSection = item.TargetSection,
                MustHitItemIds = [item.ItemId],
                MustNotHitItemIds = [distractor.ItemId],
                Rationale = "Blind holdout validation uses generated provenance/evidence consistency and is not indexed.",
                NegativeDistractorIds = [distractor.ItemId],
                RequiredRelations = item.Relations.Select(static relation => relation.RelationId).ToArray(),
                ExpectedLifecycleBehavior = "active_or_stable_only",
                Split = BlindHoldoutSplit,
                SourceRefs = item.SourceRefs,
                EvidenceRefs = item.EvidenceRefs,
                Provenance = new RetrievalDatasetV2Provenance
                {
                    RecordId = $"blind-sample-prov-{StableHash(item.ItemId + i)[..16]}",
                    SourceFingerprint = StableHash(item.SourceFingerprint + i),
                    IngestionBatchId = "rdsv2-blind-holdout-v514",
                    CreatedAt = DateTimeOffset.Parse("2026-06-19T01:00:00+00:00").AddMinutes(i)
                },
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["generatedBy"] = "source-aware-ranking-repair/blind-holdout/v1",
                    ["useForRuntime"] = "false",
                    ["rationaleIndexed"] = "false",
                    ["templateSignature"] = $"blind-query-v514-{i % 4}"
                }
            });
        }

        for (var i = 0; i < corpus.Count; i++)
        {
            var current = corpus[i];
            var next = corpus[(i + 1) % corpus.Count];
            corpus[i] = CopyWithRelations(current,
            [
                new RetrievalDatasetV2Relation
                {
                    RelationId = $"rdsv2-blind-holdout-rel-{i + 1:0000}",
                    SourceItemId = current.ItemId,
                    TargetItemId = next.ItemId,
                    RelationType = i % 2 == 0 ? "supports" : "contrasts",
                    SourceRefs = current.SourceRefs.Concat(next.SourceRefs).ToArray(),
                    EvidenceRefs = current.EvidenceRefs.Concat(next.EvidenceRefs).ToArray()
                }
            ]);
        }

        return new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems = corpus,
            Samples = samples
        };
    }

    private static string BuildBlindQuery(RetrievalDatasetV2CorpusItem item, int index)
    {
        var source = item.SourceRefs.FirstOrDefault() ?? "source";
        var evidence = item.EvidenceRefs.FirstOrDefault() ?? "evidence";
        return (index % 4) switch
        {
            0 => $"Use evidence marker {evidence} with {item.SourceKind} metadata to locate {item.TargetSection} guidance.",
            1 => $"Find the {item.ItemKind} entry linked to source marker {source} and {item.Lifecycle} lifecycle metadata.",
            2 => $"Which {item.SourceKind} context carries provenance for {item.TargetSection} using evidence {evidence}?",
            _ => $"Return the reviewed {item.ItemKind} context with source {source} and stable lifecycle signal."
        };
    }

    private static SourceAwareBlindHoldoutManifest ValidateBlindHoldout(
        RetrievalDatasetV2GeneratedDataset existing,
        RetrievalDatasetV2GeneratedDataset blind)
    {
        var existingItemIds = existing.CorpusItems.Select(static item => item.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var blindItemIds = blind.CorpusItems.Select(static item => item.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingQueries = existing.Samples.Select(static sample => Canonical(sample.QueryText)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingTemplates = existing.Samples.Select(static sample => TemplateSignature(sample.QueryText)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var itemLeakage = blindItemIds.Count(existingItemIds.Contains);
        var queryLeakage = blind.Samples.Count(sample => existingQueries.Contains(Canonical(sample.QueryText)));
        var templateLeakage = blind.Samples.Count(sample => existingTemplates.Contains(TemplateSignature(sample.QueryText)));
        var issueCount = 0;
        foreach (var item in blind.CorpusItems)
        {
            if (string.IsNullOrWhiteSpace(item.ItemId)
                || item.SourceRefs.Count == 0
                || item.EvidenceRefs.Count == 0
                || string.IsNullOrWhiteSpace(item.Provenance.RecordId)
                || string.IsNullOrWhiteSpace(item.SourceFingerprint)
                || string.IsNullOrWhiteSpace(item.Lifecycle)
                || string.IsNullOrWhiteSpace(item.TargetSection))
            {
                issueCount++;
            }
        }

        foreach (var sample in blind.Samples)
        {
            if (sample.MustHitItemIds.Count == 0
                || sample.MustNotHitItemIds.Count == 0
                || sample.MustHitItemIds.Any(id => !blindItemIds.Contains(id))
                || sample.MustNotHitItemIds.Any(id => !blindItemIds.Contains(id))
                || sample.MustHitItemIds.Intersect(sample.MustNotHitItemIds, StringComparer.OrdinalIgnoreCase).Any()
                || sample.SourceRefs.Count == 0
                || sample.EvidenceRefs.Count == 0
                || string.IsNullOrWhiteSpace(sample.Provenance.RecordId)
                || blindItemIds.Any(id => sample.QueryText.Contains(id, StringComparison.OrdinalIgnoreCase))
                || existingItemIds.Any(id => sample.QueryText.Contains(id, StringComparison.OrdinalIgnoreCase)))
            {
                issueCount++;
            }
        }

        return new SourceAwareBlindHoldoutManifest
        {
            DatasetId = "rdsv2-blind-holdout-v514-" + StableHash(string.Join('|', blind.Samples.Select(static sample => sample.SampleId)))[..16],
            CorpusItemCount = blind.CorpusItems.Count,
            SampleCount = blind.Samples.Count,
            QueryLeakageCount = queryLeakage,
            ItemLeakageCount = itemLeakage,
            TemplateLeakageCount = templateLeakage,
            ContractIssueCount = issueCount,
            Split = BlindHoldoutSplit,
            GeneratedBy = "source-aware-ranking-repair/blind-holdout/v1",
            UseForRuntime = false
        };
    }

    private static RetrievalDatasetV2CorpusItem CopyWithRelations(
        RetrievalDatasetV2CorpusItem item,
        IReadOnlyList<RetrievalDatasetV2Relation> relations)
        => new()
        {
            ItemId = item.ItemId,
            ItemKind = item.ItemKind,
            SourceKind = item.SourceKind,
            Layer = item.Layer,
            Lifecycle = item.Lifecycle,
            ReviewStatus = item.ReviewStatus,
            ReplacementState = item.ReplacementState,
            TargetSection = item.TargetSection,
            SourceRefs = item.SourceRefs,
            EvidenceRefs = item.EvidenceRefs,
            Provenance = item.Provenance,
            SourceFingerprint = item.SourceFingerprint,
            CreatedAt = item.CreatedAt,
            Relations = relations,
            Tags = item.Tags,
            Anchors = item.Anchors,
            Content = item.Content,
            Split = item.Split,
            Metadata = item.Metadata
        };

    private static bool IsRuntimeRisk(RetrievalDatasetV2CorpusItem item)
        => string.Equals(item.TargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase)
            && (!IsActiveLifecycle(item.Lifecycle)
                || string.Equals(item.ReplacementState, "superseded", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.ReviewStatus, "rejected", StringComparison.OrdinalIgnoreCase));

    private static bool IsActiveLifecycle(string value)
        => string.Equals(value, "Active", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Current", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Stable", StringComparison.OrdinalIgnoreCase);

    private static string ResolveRecommendation(
        bool reportPassed,
        IReadOnlyList<string> blocked,
        bool trainDevImproved,
        bool validationNotRegressed)
    {
        if (reportPassed)
        {
            return SourceAwareRankingRepairRecommendations.ReadyForSourceAwareRankingFreeze;
        }

        if (blocked.Any(static reason => reason.Contains("Risk", StringComparison.OrdinalIgnoreCase)))
        {
            return SourceAwareRankingRepairRecommendations.BlockedByRisk;
        }

        if (blocked.Any(static reason => reason.Contains("Runtime", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("Package", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("Invariant", StringComparison.OrdinalIgnoreCase)))
        {
            return SourceAwareRankingRepairRecommendations.BlockedByRuntimeInvariant;
        }

        if (blocked.Any(static reason => reason.Contains("Protocol", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("Gate", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("SourceScan", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("SpecialCasing", StringComparison.OrdinalIgnoreCase)))
        {
            return SourceAwareRankingRepairRecommendations.BlockedByProtocolMismatch;
        }

        if (!trainDevImproved)
        {
            return SourceAwareRankingRepairRecommendations.BlockedByTrainDevNoImprovement;
        }

        if (!validationNotRegressed)
        {
            return blocked.Any(static reason => reason.Contains("Blind", StringComparison.OrdinalIgnoreCase))
                ? SourceAwareRankingRepairRecommendations.BlockedByBlindHoldoutRegression
                : SourceAwareRankingRepairRecommendations.BlockedByHoldoutRegression;
        }

        if (blocked.Any(static reason => reason.Contains("DenseWinner", StringComparison.OrdinalIgnoreCase)))
        {
            return SourceAwareRankingRepairRecommendations.BlockedByDenseWinnerLoss;
        }

        return SourceAwareRankingRepairRecommendations.KeepPreviewOnly;
    }

    private static (double Recall, double Mrr, double Precision) Delta(
        SourceAwareRankingSplitMetrics selected,
        SourceAwareRankingSplitMetrics baseline)
        => (selected.Recall - baseline.Recall, selected.Mrr - baseline.Mrr, selected.Precision - baseline.Precision);

    private static string NormalizeSplit(string split)
        => string.IsNullOrWhiteSpace(split) ? "train" : split;

    private static bool IsTrainDevSplit(string split)
        => string.Equals(split, "train", StringComparison.OrdinalIgnoreCase)
            || string.Equals(split, "dev", StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> Tokenize(string? value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
            {
                builder.Append(char.ToLowerInvariant(ch));
                continue;
            }

            FlushToken(builder, result);
        }

        FlushToken(builder, result);
        return result;
    }

    private static void FlushToken(StringBuilder builder, ISet<string> result)
    {
        if (builder.Length == 0)
        {
            return;
        }

        result.Add(builder.ToString());
        builder.Clear();
    }

    private static double CosineOverlap(HashSet<string> query, HashSet<string> item)
    {
        if (query.Count == 0 || item.Count == 0)
        {
            return 0;
        }

        return query.Count(item.Contains) / Math.Sqrt(query.Count * item.Count);
    }

    private static double Coverage(HashSet<string> query, HashSet<string> item)
    {
        if (query.Count == 0 || item.Count == 0)
        {
            return 0;
        }

        return query.Count(item.Contains) / (double)query.Count;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var overlap = a.Count(b.Contains);
        var union = a.Count + b.Count - overlap;
        return union == 0 ? 0 : overlap / (double)union;
    }

    private static IReadOnlyList<string> MergeDistinct(IEnumerable<string> current, IEnumerable<string> extra)
        => current
            .Concat(extra)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string StableHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Canonical(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var separator = false;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                separator = false;
                continue;
            }

            if (!separator)
            {
                builder.Append('-');
                separator = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static string TemplateSignature(string queryText)
        => string.Join(' ', Tokenize(queryText).Where(static token => !token.Any(char.IsDigit)).OrderBy(static token => token, StringComparer.OrdinalIgnoreCase).Take(10));

    private static string Escape(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal);

    private static void AppendList(StringBuilder builder, string title, IReadOnlyList<string> items)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (items.Count == 0)
        {
            builder.AppendLine("- (none)");
            return;
        }

        foreach (var item in items)
        {
            builder.AppendLine($"- `{Escape(item)}`");
        }
    }

    private sealed record SourceAwareRankingProfile(
        string ProfileId,
        string ProfileLabel,
        bool UseSourceSignals,
        bool ConfidenceGateEnabled,
        bool DenseWinnerPreservationEnabled,
        bool DisagreementFallbackEnabled,
        bool FinalRiskGateEnabled,
        double DenseWeight,
        double ContributionCap,
        double MinConfidence,
        double EvidenceWeight,
        double RelationWeight,
        double MetadataWeight)
    {
        public double LexicalWeight { get; init; } = 0.25;
        public double AnchorWeight { get; init; } = 0.20;
        public double DisagreementFallbackThreshold { get; init; } = 0.72;
    }

    private sealed record SourceAwareItemProfile(
        RetrievalDatasetV2CorpusItem Item,
        HashSet<string> DenseTokens,
        HashSet<string> LexicalTokens,
        HashSet<string> AnchorTokens,
        HashSet<string> EvidenceSourceTokens,
        HashSet<string> RelationTokens,
        HashSet<string> MetadataTokens,
        int SourceContributionCount)
    {
        public double DenseScore { get; init; }
    }

    private sealed record RankedCandidate(
        string ItemId,
        double Score,
        double DenseScore,
        double SourceScore,
        double Confidence,
        int SourceContributionCount,
        bool SourceDisagreement,
        RetrievalDatasetV2CorpusItem Item);

    private sealed record CandidateEvaluationMetrics(
        double Recall,
        double Mrr,
        double Precision,
        int HitCount,
        int MustHitCount,
        int RiskAfterPolicy,
        int MustNotHitRiskAfterPolicy,
        int LifecycleRiskAfterPolicy);

    private sealed class MetricAccumulator
    {
        private double _recallSum;
        private double _mrrSum;
        private double _precisionSum;
        private int _sampleCount;
        private int _hitCount;
        private int _mustHitCount;

        public int RiskAfterPolicy { get; private set; }
        public int MustNotHitRiskAfterPolicy { get; private set; }
        public int LifecycleRiskAfterPolicy { get; private set; }

        public void Add(CandidateEvaluationMetrics metrics)
        {
            _recallSum += metrics.Recall;
            _mrrSum += metrics.Mrr;
            _precisionSum += metrics.Precision;
            _sampleCount++;
            _hitCount += metrics.HitCount;
            _mustHitCount += metrics.MustHitCount;
            RiskAfterPolicy += metrics.RiskAfterPolicy;
            MustNotHitRiskAfterPolicy += metrics.MustNotHitRiskAfterPolicy;
            LifecycleRiskAfterPolicy += metrics.LifecycleRiskAfterPolicy;
        }

        public SourceAwareRankingSplitMetrics ToMetrics(string split)
            => new()
            {
                Split = split,
                SampleCount = _sampleCount,
                Recall = _sampleCount == 0 ? 0 : _recallSum / _sampleCount,
                Mrr = _sampleCount == 0 ? 0 : _mrrSum / _sampleCount,
                Precision = _sampleCount == 0 ? 0 : _precisionSum / _sampleCount,
                HitCount = _hitCount,
                MustHitCount = _mustHitCount,
                RiskAfterPolicy = RiskAfterPolicy,
                MustNotHitRiskAfterPolicy = MustNotHitRiskAfterPolicy,
                LifecycleRiskAfterPolicy = LifecycleRiskAfterPolicy
            };
    }
}

public sealed class SourceAwareRankingRepairOptions
{
    public RetrievalEvalProtocol? Protocol { get; init; }
    public bool RequireV511ProtocolGatePassed { get; init; } = true;
    public bool RequireV512EnrichmentGatePassed { get; init; } = true;
    public bool RequireRuntimeChangeGate { get; init; } = true;
    public bool RequireSourceScan { get; init; } = true;
    public int BlindHoldoutSampleCount { get; init; } = 24;
    public double ContributionCap { get; init; } = 0.34;
    public double MinConfidence { get; init; } = 0.46;
    public double MetricTolerance { get; init; } = 1e-9;
    public bool UseForRuntime { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
}

public sealed class SourceAwareRankingRepairReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ReportPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = SourceAwareRankingRepairRecommendations.KeepPreviewOnly;
    public string SelectedProfileId { get; init; } = string.Empty;
    public RetrievalEvalProtocol Protocol { get; init; } = new();
    public int CorpusItemCount { get; init; }
    public int SampleCount { get; init; }
    public SourceAwareBlindHoldoutManifest BlindHoldoutManifest { get; init; } = new();
    public IReadOnlyList<RetrievalDatasetV2CorpusItem> BlindHoldoutCorpusItems { get; init; } = Array.Empty<RetrievalDatasetV2CorpusItem>();
    public IReadOnlyList<RetrievalDatasetV2Sample> BlindHoldoutSamples { get; init; } = Array.Empty<RetrievalDatasetV2Sample>();
    public SourceAwareRankingProfileReport DenseBaseline { get; init; } = new();
    public SourceAwareRankingProfileReport SelectedProfile { get; init; } = new();
    public IReadOnlyList<SourceAwareRankingProfileReport> Profiles { get; init; } = Array.Empty<SourceAwareRankingProfileReport>();
    public double TrainDevRecallDelta { get; init; }
    public double TrainDevMrrDelta { get; init; }
    public double TrainDevPrecisionDelta { get; init; }
    public double TestRecallDelta { get; init; }
    public double TestMrrDelta { get; init; }
    public double TestPrecisionDelta { get; init; }
    public double HoldoutRecallDelta { get; init; }
    public double HoldoutMrrDelta { get; init; }
    public double HoldoutPrecisionDelta { get; init; }
    public double BlindHoldoutRecallDelta { get; init; }
    public double BlindHoldoutMrrDelta { get; init; }
    public double BlindHoldoutPrecisionDelta { get; init; }
    public int DenseWinnerLostCount { get; init; }
    public int UniqueSourceRecoveryCount { get; init; }
    public int SourceNoiseCount { get; init; }
    public double FallbackRate { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public bool EvalLabelScoringDetected { get; init; }
    public bool EvalLabelCandidateGenerationDetected { get; init; }
    public int FormalOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public bool UseForRuntime { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }
    public bool V511ProtocolGatePassed { get; init; }
    public bool V512EnrichmentGatePassed { get; init; }
    public RuntimeObservableFeatureContractSourceScan SourceScan { get; init; } = new();
    public IReadOnlyDictionary<string, string> SourceReports { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed class SourceAwareRankingProfileReport
{
    public string ProfileId { get; init; } = string.Empty;
    public string ProfileLabel { get; init; } = string.Empty;
    public int SampleCount { get; init; }
    public SourceAwareRankingSplitMetrics TrainDev { get; init; } = new() { Split = "train-dev" };
    public SourceAwareRankingSplitMetrics Test { get; init; } = new() { Split = "test" };
    public SourceAwareRankingSplitMetrics Holdout { get; init; } = new() { Split = "holdout" };
    public SourceAwareRankingSplitMetrics BlindHoldout { get; init; } = new() { Split = "blind-holdout" };
    public IReadOnlyList<SourceAwareRankingSplitMetrics> SplitMetrics { get; init; } = Array.Empty<SourceAwareRankingSplitMetrics>();
    public int DenseWinnerLostCount { get; init; }
    public int UniqueSourceRecoveryCount { get; init; }
    public int SourceNoiseCount { get; init; }
    public int FallbackCount { get; init; }
    public double FallbackRate { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public int FormalOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
}

public sealed class SourceAwareRankingSplitMetrics
{
    public string Split { get; init; } = string.Empty;
    public int SampleCount { get; init; }
    public double Recall { get; init; }
    public double Mrr { get; init; }
    public double Precision { get; init; }
    public int HitCount { get; init; }
    public int MustHitCount { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
}

public sealed class SourceAwareBlindHoldoutManifest
{
    public string DatasetId { get; init; } = string.Empty;
    public int CorpusItemCount { get; init; }
    public int SampleCount { get; init; }
    public string Split { get; init; } = "blind-holdout";
    public int QueryLeakageCount { get; init; }
    public int ItemLeakageCount { get; init; }
    public int TemplateLeakageCount { get; init; }
    public int ContractIssueCount { get; init; }
    public string GeneratedBy { get; init; } = string.Empty;
    public bool UseForRuntime { get; init; }
}

public static class SourceAwareRankingProfileIds
{
    public const string DenseBaseline = "dense-baseline";
    public const string NormalizedSource = "normalized-source";
    public const string ConfidenceGated = "confidence-gated";
    public const string DensePreserving = "dense-preserving";
    public const string CombinedSafe = "combined-safe";
}

public static class SourceAwareRankingRepairRecommendations
{
    public const string ReadyForSourceAwareRankingFreeze = nameof(ReadyForSourceAwareRankingFreeze);
    public const string BlockedByTrainDevNoImprovement = nameof(BlockedByTrainDevNoImprovement);
    public const string BlockedByHoldoutRegression = nameof(BlockedByHoldoutRegression);
    public const string BlockedByBlindHoldoutRegression = nameof(BlockedByBlindHoldoutRegression);
    public const string BlockedByDenseWinnerLoss = nameof(BlockedByDenseWinnerLoss);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByProtocolMismatch = nameof(BlockedByProtocolMismatch);
    public const string BlockedByRuntimeInvariant = nameof(BlockedByRuntimeInvariant);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}
