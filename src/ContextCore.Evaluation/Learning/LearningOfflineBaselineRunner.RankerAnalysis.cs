using System.Globalization;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed partial class LearningOfflineBaselineRunner
{
    private const string FeatureLifecycle = "lifecycle";
    private const string FeatureRecency = "recency";
    private const string FeatureChannelSource = "channel source";
    private const string FeatureRelationPath = "relation path";
    private const string FeatureShortTermMatch = "short-term match";
    private const string FeatureStableMatch = "stable match";
    private const string FeatureConstraintMatch = "constraint match";
    private const string FeatureKeywordMatch = "keyword match";
    private const string FeatureSemanticAnchorMatch = "semantic anchor match";
    private const string FeatureImportance = "importance";

    private static readonly IReadOnlyList<string> RankerAblationFeatures =
    [
        FeatureLifecycle,
        FeatureRecency,
        FeatureChannelSource,
        FeatureRelationPath,
        FeatureShortTermMatch,
        FeatureStableMatch,
        FeatureConstraintMatch,
        FeatureKeywordMatch,
        FeatureSemanticAnchorMatch,
        FeatureImportance
    ];

    public async Task<RankerFeatureAblationReport> RunRankerAblationAsync(
        string inputPath,
        string jsonOutputPath,
        string markdownOutputPath,
        CancellationToken cancellationToken = default)
    {
        var resolvedInputPath = ResolvePath(inputPath);
        var pairs = await ReadJsonLinesAsync<RankingPairExample>(resolvedInputPath, cancellationToken)
            .ConfigureAwait(false);
        var report = BuildRankerAblationReport(pairs, resolvedInputPath);
        await WriteReportAsync(report, BuildRankerAblationMarkdownReport(report), jsonOutputPath, markdownOutputPath, cancellationToken)
            .ConfigureAwait(false);
        return report;
    }

    public async Task<RankerWeightSweepReport> RunRankerWeightSweepAsync(
        string inputPath,
        string jsonOutputPath,
        string markdownOutputPath,
        CancellationToken cancellationToken = default)
    {
        var resolvedInputPath = ResolvePath(inputPath);
        var pairs = await ReadJsonLinesAsync<RankingPairExample>(resolvedInputPath, cancellationToken)
            .ConfigureAwait(false);
        var report = BuildRankerWeightSweepReport(pairs, resolvedInputPath);
        await WriteReportAsync(report, BuildRankerWeightSweepMarkdownReport(report), jsonOutputPath, markdownOutputPath, cancellationToken)
            .ConfigureAwait(false);
        return report;
    }

    public async Task<RankerResidualErrorAuditReport> RunRankerResidualAuditAsync(
        string inputPath,
        string jsonOutputPath,
        string markdownOutputPath,
        CancellationToken cancellationToken = default)
    {
        var resolvedInputPath = ResolvePath(inputPath);
        var pairs = await ReadJsonLinesAsync<RankingPairExample>(resolvedInputPath, cancellationToken)
            .ConfigureAwait(false);
        var report = BuildRankerResidualAuditReport(pairs, resolvedInputPath);
        await WriteReportAsync(report, BuildRankerResidualAuditMarkdownReport(report), jsonOutputPath, markdownOutputPath, cancellationToken)
            .ConfigureAwait(false);
        return report;
    }

    public RankerFeatureAblationReport BuildRankerAblationReport(
        IReadOnlyList<RankingPairExample> pairs,
        string inputPath = "")
    {
        ArgumentNullException.ThrowIfNull(pairs);

        var notReadyReasons = BuildRankerNotReadyReasons(pairs);
        var split = SplitRankingPairs(pairs);
        var baseline = pairs.Count == 0
            ? EmptyRankerScenario(SimpleFeatureWeightedBaseline)
            : EvaluateRankerScenario(SimpleFeatureWeightedBaseline, split.Test, ScorePairByWeightedFeatures);

        var ablations = pairs.Count == 0
            ? Array.Empty<RankerFeatureAblationResult>()
            : RankerAblationFeatures
                .Select(feature => BuildAblationResult(feature, split.Test, baseline))
                .ToArray();

        return new RankerFeatureAblationReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            InputPath = inputPath,
            PairCount = pairs.Count,
            Ready = notReadyReasons.Count == 0,
            Status = notReadyReasons.Count == 0 ? LearningDatasetReadinessStatus.Ready : LearningDatasetReadinessStatus.NotReady,
            NotReadyReasons = notReadyReasons,
            Split = split.Summary,
            Baseline = baseline.Metrics,
            Ablations = ablations,
            PolicyVersion = PolicyVersion
        };
    }

    public RankerWeightSweepReport BuildRankerWeightSweepReport(
        IReadOnlyList<RankingPairExample> pairs,
        string inputPath = "")
    {
        ArgumentNullException.ThrowIfNull(pairs);

        var notReadyReasons = BuildRankerNotReadyReasons(pairs);
        var split = SplitRankingPairs(pairs);
        var baselineWeights = CreateDefaultRankerFeatureWeights();
        var baseline = pairs.Count == 0
            ? EmptyRankerScenario(SimpleFeatureWeightedBaseline)
            : EvaluateRankerScenario(SimpleFeatureWeightedBaseline, split.Test, ScorePairByWeightedFeatures);
        var results = pairs.Count == 0
            ? Array.Empty<RankerWeightSweepResult>()
            : BuildWeightSweepConfigurations(baselineWeights)
                .Select(config => BuildWeightSweepResult(config, split.Test, baseline))
                .OrderByDescending(result => result.PairwiseAccuracy)
                .ThenBy(result => result.FalsePositiveRate)
                .ThenBy(result => result.ConfigurationId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        var best = results.FirstOrDefault() ?? new RankerWeightSweepResult();

        return new RankerWeightSweepReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            InputPath = inputPath,
            PairCount = pairs.Count,
            Ready = notReadyReasons.Count == 0,
            Status = notReadyReasons.Count == 0 ? LearningDatasetReadinessStatus.Ready : LearningDatasetReadinessStatus.NotReady,
            NotReadyReasons = notReadyReasons,
            Split = split.Summary,
            BaselineWeights = baselineWeights,
            Baseline = baseline.Metrics,
            BestResult = best,
            SweepResults = results,
            PolicyVersion = PolicyVersion
        };
    }

    public RankerResidualErrorAuditReport BuildRankerResidualAuditReport(
        IReadOnlyList<RankingPairExample> pairs,
        string inputPath = "")
    {
        ArgumentNullException.ThrowIfNull(pairs);

        var notReadyReasons = BuildRankerNotReadyReasons(pairs);
        var split = SplitRankingPairs(pairs);
        var baseline = pairs.Count == 0
            ? EmptyRankerScenario(SimpleFeatureWeightedBaseline)
            : EvaluateRankerScenario(SimpleFeatureWeightedBaseline, split.Test, ScorePairByWeightedFeatures);
        var failures = baseline.Outcomes.Values
            .Where(static outcome => !outcome.Succeeded)
            .Select(BuildResidualFailureDetail)
            .OrderBy(detail => detail.Margin)
            .ThenBy(detail => detail.EvalSampleId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new RankerResidualErrorAuditReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            InputPath = inputPath,
            PairCount = pairs.Count,
            Ready = notReadyReasons.Count == 0,
            Status = notReadyReasons.Count == 0 ? LearningDatasetReadinessStatus.Ready : LearningDatasetReadinessStatus.NotReady,
            NotReadyReasons = notReadyReasons,
            Split = split.Summary,
            Baseline = baseline.Metrics,
            Failures = failures,
            FailureClusters = BuildResidualFailureClusters(failures),
            FeatureConflicts = BuildFeatureConflictSummaries(failures),
            HardNegativeRecommendations = BuildHardNegativeRecommendations(failures),
            PolicyVersion = PolicyVersion
        };
    }

    public static string BuildRankerAblationMarkdownReport(RankerFeatureAblationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Ranker Feature Ablation Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Input: `{report.InputPath}`");
        builder.AppendLine($"Policy: `{report.PolicyVersion}`");
        builder.AppendLine($"Status: `{report.Status}`");
        builder.AppendLine($"Pairs: `{report.PairCount}`");
        AppendNotReady(builder, report.NotReadyReasons);
        AppendSplit(builder, report.Split);
        builder.AppendLine();
        builder.AppendLine("## Baseline");
        builder.AppendLine();
        builder.AppendLine($"- Baseline: `{report.Baseline.BaselineName}`");
        builder.AppendLine($"- PairwiseAccuracy: `{FormatPercent(report.Baseline.PairwiseAccuracy)}`");
        builder.AppendLine($"- FPR/FNR: `{FormatPercent(report.Baseline.FalsePositiveRate)}` / `{FormatPercent(report.Baseline.FalseNegativeRate)}`");
        builder.AppendLine();
        builder.AppendLine("## Ablations");
        builder.AppendLine();
        builder.AppendLine("| Disabled Feature | PairwiseAccuracy | Delta | FPR | FNR | Fixed | Newly Failed | Top Cluster |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---|");
        foreach (var result in report.Ablations)
        {
            builder.AppendLine($"| {result.DisabledFeature} | {FormatPercent(result.PairwiseAccuracy)} | {FormatSignedPercent(result.AccuracyDelta)} | {FormatPercent(result.FalsePositiveRate)} | {FormatPercent(result.FalseNegativeRate)} | {result.TopFixedExamples.Count} | {result.TopNewlyFailedExamples.Count} | {FormatTopCluster(result.FailureClusters)} |");
        }

        AppendAblationDetails(builder, report.Ablations);
        return builder.ToString();
    }

    public static string BuildRankerWeightSweepMarkdownReport(RankerWeightSweepReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Ranker Weight Sweep Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Input: `{report.InputPath}`");
        builder.AppendLine($"Policy: `{report.PolicyVersion}`");
        builder.AppendLine($"Status: `{report.Status}`");
        builder.AppendLine($"Pairs: `{report.PairCount}`");
        AppendNotReady(builder, report.NotReadyReasons);
        AppendSplit(builder, report.Split);
        builder.AppendLine();
        builder.AppendLine("## Baseline");
        builder.AppendLine();
        builder.AppendLine($"- Baseline: `{report.Baseline.BaselineName}`");
        builder.AppendLine($"- PairwiseAccuracy: `{FormatPercent(report.Baseline.PairwiseAccuracy)}`");
        builder.AppendLine($"- Baseline weights: `{FormatWeights(report.BaselineWeights)}`");
        builder.AppendLine();
        builder.AppendLine("## Best Result");
        builder.AppendLine();
        builder.AppendLine($"- Configuration: `{report.BestResult.ConfigurationId}`");
        builder.AppendLine($"- PairwiseAccuracy: `{FormatPercent(report.BestResult.PairwiseAccuracy)}`");
        builder.AppendLine($"- Delta: `{FormatSignedPercent(report.BestResult.AccuracyDelta)}`");
        builder.AppendLine($"- Recommendation: `{report.BestResult.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Sweep Results");
        builder.AppendLine();
        builder.AppendLine("| Config | Parameter | Value | PairwiseAccuracy | Delta | WinOverBaseline | FPR | FNR | Fixed | Newly Failed | Recommendation |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var result in report.SweepResults)
        {
            builder.AppendLine($"| {result.ConfigurationId} | {result.ParameterName} | {Format(result.ParameterValue)} | {FormatPercent(result.PairwiseAccuracy)} | {FormatSignedPercent(result.AccuracyDelta)} | {FormatPercent(result.WinRateOverBaseline)} | {FormatPercent(result.FalsePositiveRate)} | {FormatPercent(result.FalseNegativeRate)} | {result.TopFixedExamples.Count} | {result.TopNewlyFailedExamples.Count} | {result.Recommendation} |");
        }

        AppendSweepDetails(builder, report.SweepResults.Take(10).ToArray());
        return builder.ToString();
    }

    public static string BuildRankerResidualAuditMarkdownReport(RankerResidualErrorAuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Ranker Residual Error Audit Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Input: `{report.InputPath}`");
        builder.AppendLine($"Policy: `{report.PolicyVersion}`");
        builder.AppendLine($"Status: `{report.Status}`");
        builder.AppendLine($"Pairs: `{report.PairCount}`");
        AppendNotReady(builder, report.NotReadyReasons);
        AppendSplit(builder, report.Split);
        builder.AppendLine();
        builder.AppendLine("## Baseline");
        builder.AppendLine();
        builder.AppendLine($"- Baseline: `{report.Baseline.BaselineName}`");
        builder.AppendLine($"- PairwiseAccuracy: `{FormatPercent(report.Baseline.PairwiseAccuracy)}`");
        builder.AppendLine($"- Residual failures: `{report.Failures.Count}`");
        builder.AppendLine();
        builder.AppendLine("## Failure Clusters");
        AppendResidualClusters(builder, report.FailureClusters);
        builder.AppendLine();
        builder.AppendLine("## Feature Conflicts");
        AppendFeatureConflicts(builder, report.FeatureConflicts);
        builder.AppendLine();
        builder.AppendLine("## Hard Negative Recommendations");
        AppendHardNegativeRecommendations(builder, report.HardNegativeRecommendations);
        builder.AppendLine();
        builder.AppendLine("## Residual Failure Details");
        AppendResidualFailures(builder, report.Failures);
        return builder.ToString();
    }

    private static IReadOnlyList<string> BuildRankerNotReadyReasons(IReadOnlyList<RankingPairExample> pairs)
    {
        var reasons = new List<string>();
        if (pairs.Count == 0)
        {
            reasons.Add("ranking-pairs is empty");
        }

        var split = SplitRankingPairs(pairs);
        if (split.GroupCount < 2 && pairs.Count > 0)
        {
            reasons.Add("less than two sample groups; grouped holdout split is not meaningful");
        }

        return reasons;
    }

    private static RankerFeatureAblationResult BuildAblationResult(
        string disabledFeature,
        IReadOnlyList<RankingPairExample> testPairs,
        RankerScenarioEvaluation baseline)
    {
        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { disabledFeature };
        var weights = CreateDefaultRankerFeatureWeights();
        var scenario = EvaluateRankerScenario(
            $"Disable {disabledFeature}",
            testPairs,
            pair => ScorePairByTunableFeatures(pair, weights, disabled));
        var comparison = BuildScenarioComparison(baseline, scenario);
        return new RankerFeatureAblationResult
        {
            FeatureName = disabledFeature,
            DisabledFeature = disabledFeature,
            PairwiseAccuracy = scenario.Metrics.PairwiseAccuracy,
            AccuracyDelta = scenario.Metrics.PairwiseAccuracy - baseline.Metrics.PairwiseAccuracy,
            Auc = scenario.Metrics.Auc,
            FalsePositiveRate = scenario.Metrics.FalsePositiveRate,
            FalseNegativeRate = scenario.Metrics.FalseNegativeRate,
            FailureClusters = BuildFailureClusters(scenario.Outcomes.Values),
            TopFixedExamples = comparison.Fixed,
            TopNewlyFailedExamples = comparison.NewlyFailed
        };
    }

    private static RankerWeightSweepResult BuildWeightSweepResult(
        RankerWeightSweepConfiguration config,
        IReadOnlyList<RankingPairExample> testPairs,
        RankerScenarioEvaluation baseline)
    {
        var scenario = EvaluateRankerScenario(
            config.ConfigurationId,
            testPairs,
            pair => ScorePairByTunableFeatures(pair, config.Weights, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        var comparison = BuildScenarioComparison(baseline, scenario);
        return new RankerWeightSweepResult
        {
            ConfigurationId = config.ConfigurationId,
            ParameterName = config.ParameterName,
            ParameterValue = config.ParameterValue,
            Weights = config.Weights,
            PairwiseAccuracy = scenario.Metrics.PairwiseAccuracy,
            AccuracyDelta = scenario.Metrics.PairwiseAccuracy - baseline.Metrics.PairwiseAccuracy,
            Auc = scenario.Metrics.Auc,
            WinRateOverBaseline = SafeDivide(comparison.Fixed.Count, baseline.Outcomes.Count),
            FalsePositiveRate = scenario.Metrics.FalsePositiveRate,
            FalseNegativeRate = scenario.Metrics.FalseNegativeRate,
            FailureClusters = BuildFailureClusters(scenario.Outcomes.Values),
            TopFixedExamples = comparison.Fixed,
            TopNewlyFailedExamples = comparison.NewlyFailed,
            Recommendation = BuildWeightRecommendation(scenario.Metrics.PairwiseAccuracy - baseline.Metrics.PairwiseAccuracy, comparison.NewlyFailed.Count)
        };
    }

    private static RankerScenarioEvaluation EvaluateRankerScenario(
        string baselineName,
        IReadOnlyList<RankingPairExample> testPairs,
        Func<RankingPairExample, CandidatePairScore> score)
    {
        var outcomes = new Dictionary<string, RankerPairOutcome>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<RankerBaselineFailureExample>();
        var pairwiseOutcomes = new List<double>();
        var negativeWins = 0;
        var positiveNotWinning = 0;

        foreach (var pair in testPairs)
        {
            var pairScore = score(pair);
            var margin = pairScore.PositiveScore - pairScore.NegativeScore;
            var succeeded = margin > 0.0000001;
            var tied = Math.Abs(margin) < 0.0000001;
            var outcome = succeeded ? 1 : tied ? 0.5 : 0;
            pairwiseOutcomes.Add(outcome);
            if (pairScore.NegativeScore > pairScore.PositiveScore)
            {
                negativeWins++;
            }

            if (!succeeded)
            {
                positiveNotWinning++;
                failures.Add(new RankerBaselineFailureExample
                {
                    EvalSampleId = pair.EvalSampleId,
                    Mode = pair.Mode,
                    Intent = pair.Intent,
                    PositiveCandidateId = pair.PositiveCandidateId,
                    NegativeCandidateId = pair.NegativeCandidateId,
                    PositiveScore = pairScore.PositiveScore,
                    NegativeScore = pairScore.NegativeScore,
                    Reason = tied ? "tie" : "negative candidate outranked positive candidate"
                });
            }

            var key = BuildFailureKey(pair.EvalSampleId, pair.PositiveCandidateId, pair.NegativeCandidateId);
            outcomes[key] = new RankerPairOutcome(
                pair,
                pairScore.PositiveScore,
                pairScore.NegativeScore,
                margin,
                succeeded,
                ClassifyFailureCluster(pair));
        }

        return new RankerScenarioEvaluation(
            new RankerBaselineResult
            {
                BaselineName = baselineName,
                PairwiseAccuracy = pairwiseOutcomes.Count == 0 ? 0 : pairwiseOutcomes.Average(),
                Auc = pairwiseOutcomes.Count == 0 ? null : pairwiseOutcomes.Average(),
                FalsePositiveRate = SafeDivide(negativeWins, testPairs.Count),
                FalseNegativeRate = SafeDivide(positiveNotWinning, testPairs.Count),
                TopFailureExamples = failures
                    .OrderByDescending(item => item.NegativeScore - item.PositiveScore)
                    .ThenBy(item => item.EvalSampleId, StringComparer.OrdinalIgnoreCase)
                    .Take(10)
                    .ToArray()
            },
            outcomes);
    }

    private static RankerScenarioComparison BuildScenarioComparison(
        RankerScenarioEvaluation baseline,
        RankerScenarioEvaluation candidate)
    {
        var fixedExamples = new List<RankerComparisonExample>();
        var newlyFailedExamples = new List<RankerComparisonExample>();
        foreach (var pair in baseline.Outcomes)
        {
            if (!candidate.Outcomes.TryGetValue(pair.Key, out var candidateOutcome))
            {
                continue;
            }

            var baselineOutcome = pair.Value;
            if (!baselineOutcome.Succeeded && candidateOutcome.Succeeded)
            {
                fixedExamples.Add(BuildComparisonExample(baselineOutcome, candidateOutcome, "baseline failed; candidate fixed"));
            }
            else if (baselineOutcome.Succeeded && !candidateOutcome.Succeeded)
            {
                newlyFailedExamples.Add(BuildComparisonExample(baselineOutcome, candidateOutcome, "baseline passed; candidate newly failed"));
            }
        }

        return new RankerScenarioComparison(
            fixedExamples
                .OrderByDescending(item => (item.CandidatePositiveScore - item.CandidateNegativeScore) - (item.BaselinePositiveScore - item.BaselineNegativeScore))
                .ThenBy(item => item.EvalSampleId, StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToArray(),
            newlyFailedExamples
                .OrderBy(item => (item.CandidatePositiveScore - item.CandidateNegativeScore) - (item.BaselinePositiveScore - item.BaselineNegativeScore))
                .ThenBy(item => item.EvalSampleId, StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToArray());
    }

    private static IReadOnlyList<RankerFailureClusterSummary> BuildFailureClusters(
        IEnumerable<RankerPairOutcome> outcomes)
    {
        return outcomes
            .Where(static outcome => !outcome.Succeeded)
            .GroupBy(static outcome => outcome.FailureCluster, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RankerFailureClusterSummary
            {
                Cluster = group.Key,
                Count = group.Count(),
                ExampleIds = group
                    .Select(static outcome => outcome.Pair.EvalSampleId)
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                    .Take(10)
                    .ToArray()
            })
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Cluster, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static RankerResidualFailureDetail BuildResidualFailureDetail(RankerPairOutcome outcome)
    {
        var pair = outcome.Pair;
        var snapshot = pair.FeatureSnapshot;
        var positiveRawScore = ParseDouble(snapshot, "positiveScore");
        var negativeRawScore = ParseDouble(snapshot, "negativeScore");
        var positiveKeyword = ResolveKeywordMatchScore(snapshot, "positive", positiveRawScore);
        var negativeKeyword = ResolveKeywordMatchScore(snapshot, "negative", negativeRawScore);
        var positiveSemantic = ResolveSemanticAnchorMatchScore(snapshot, "positive", positiveRawScore, positiveKeyword);
        var negativeSemantic = ResolveSemanticAnchorMatchScore(snapshot, "negative", negativeRawScore, negativeKeyword);
        return new RankerResidualFailureDetail
        {
            EvalSampleId = pair.EvalSampleId,
            Mode = pair.Mode,
            Intent = pair.Intent,
            PositiveCandidateId = pair.PositiveCandidateId,
            NegativeCandidateId = pair.NegativeCandidateId,
            PositiveScore = outcome.PositiveScore,
            NegativeScore = outcome.NegativeScore,
            Margin = outcome.Margin,
            PositiveKeywordMatchScore = positiveKeyword,
            NegativeKeywordMatchScore = negativeKeyword,
            PositiveSemanticAnchorMatchScore = positiveSemantic,
            NegativeSemanticAnchorMatchScore = negativeSemantic,
            PositiveSelected = ParseBool(snapshot, "positiveSelected"),
            NegativeSelected = ParseBool(snapshot, "negativeSelected"),
            PositiveRank = ParseInt(snapshot, "positiveRank"),
            NegativeRank = ParseInt(snapshot, "negativeRank"),
            PositiveKind = GetString(snapshot, "positiveKind"),
            NegativeKind = GetString(snapshot, "negativeKind"),
            PositiveSection = GetString(snapshot, "positiveSection"),
            NegativeSection = GetString(snapshot, "negativeSection"),
            FailureCluster = outcome.FailureCluster,
            ProbableCause = BuildResidualProbableCause(outcome.FailureCluster, positiveKeyword, negativeKeyword, positiveSemantic, negativeSemantic)
        };
    }

    private static IReadOnlyList<RankerResidualFailureCluster> BuildResidualFailureClusters(
        IReadOnlyList<RankerResidualFailureDetail> failures)
    {
        return failures
            .GroupBy(static failure => failure.FailureCluster, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RankerResidualFailureCluster
            {
                Cluster = group.Key,
                Count = group.Count(),
                AverageMargin = group.Average(static failure => failure.Margin),
                ExampleIds = group
                    .Select(static failure => failure.EvalSampleId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                    .Take(10)
                    .ToArray(),
                ProbableCause = BuildClusterProbableCause(group.Key)
            })
            .OrderByDescending(static cluster => cluster.Count)
            .ThenBy(static cluster => cluster.Cluster, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<RankerFeatureConflictSummary> BuildFeatureConflictSummaries(
        IReadOnlyList<RankerResidualFailureDetail> failures)
    {
        if (failures.Count == 0)
        {
            return Array.Empty<RankerFeatureConflictSummary>();
        }

        return
        [
            BuildFeatureConflict(
                "KeywordMatch",
                failures,
                static failure => failure.PositiveKeywordMatchScore,
                static failure => failure.NegativeKeywordMatchScore,
                "Keyword feature favors the negative candidate when average delta is below zero."),
            BuildFeatureConflict(
                "SemanticAnchorMatch",
                failures,
                static failure => failure.PositiveSemanticAnchorMatchScore,
                static failure => failure.NegativeSemanticAnchorMatchScore,
                "Semantic anchor feature overmatches the negative candidate when average delta is below zero."),
            BuildFeatureConflict(
                "Rank",
                failures,
                static failure => failure.PositiveRank,
                static failure => failure.NegativeRank,
                "Lower rank is better; positive rank should be lower than negative rank when both are selected."),
            BuildFeatureConflict(
                "Selection",
                failures,
                static failure => failure.PositiveSelected ? 1 : 0,
                static failure => failure.NegativeSelected ? 1 : 0,
                "Selected-state conflict indicates historical or deprecated negatives were selected offline.")
        ];
    }

    private static RankerFeatureConflictSummary BuildFeatureConflict(
        string featureName,
        IReadOnlyList<RankerResidualFailureDetail> failures,
        Func<RankerResidualFailureDetail, double> positiveSelector,
        Func<RankerResidualFailureDetail, double> negativeSelector,
        string interpretation)
    {
        var positive = failures.Average(positiveSelector);
        var negative = failures.Average(negativeSelector);
        return new RankerFeatureConflictSummary
        {
            FeatureName = featureName,
            FailureCount = failures.Count,
            AveragePositiveValue = positive,
            AverageNegativeValue = negative,
            AverageDelta = positive - negative,
            Interpretation = interpretation
        };
    }

    private static IReadOnlyList<RankerHardNegativeRecommendation> BuildHardNegativeRecommendations(
        IReadOnlyList<RankerResidualFailureDetail> failures)
    {
        var deprecated = failures
            .Where(static failure => string.Equals(failure.FailureCluster, "DeprecatedNoise", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (deprecated.Length == 0)
        {
            return Array.Empty<RankerHardNegativeRecommendation>();
        }

        return
        [
            BuildHardNegativeRecommendation(
                "DeprecatedSameKeyword",
                deprecated,
                "Deprecated negatives share query keywords with current positives.",
                "Generate pairs where deprecated/historical items share the same keyword surface as active positives."),
            BuildHardNegativeRecommendation(
                "VersionConflict",
                deprecated,
                "Residual failures include older version candidates outranking active versions.",
                "Add hard negatives that pair v1/old/deprecated items against v2/latest/current positives."),
            BuildHardNegativeRecommendation(
                "HistoricalSelectedNoise",
                deprecated,
                "Historical candidates can still be attractive when selected/rank features are strong.",
                "Add selected historical/deprecated negatives with explicit lifecycle markers to teach demotion offline."),
            BuildHardNegativeRecommendation(
                "WeakLifecycleMarker",
                deprecated,
                "Lifecycle markers are not strong enough in eval-only ranking pairs.",
                "Add negatives with weak or missing deprecated markers and require positive active/current evidence."),
            BuildHardNegativeRecommendation(
                "SemanticAnchorOvermatch",
                deprecated,
                "Semantic anchor surface can overmatch deprecated negatives.",
                "Add semantically similar deprecated negatives that differ only by lifecycle/version state.")
        ];
    }

    private static RankerHardNegativeRecommendation BuildHardNegativeRecommendation(
        string type,
        IReadOnlyList<RankerResidualFailureDetail> failures,
        string reason,
        string suggestedAction)
    {
        return new RankerHardNegativeRecommendation
        {
            RecommendationType = type,
            Cluster = "DeprecatedNoise",
            Count = failures.Count,
            Reason = reason,
            SuggestedAction = suggestedAction,
            ExampleIds = failures
                .Select(static failure => failure.EvalSampleId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToArray()
        };
    }

    private static double ResolveKeywordMatchScore(
        IReadOnlyDictionary<string, string> snapshot,
        string prefix,
        double rawScore)
    {
        var explicitScore = ParseDouble(snapshot, $"{prefix}KeywordMatchScore");
        return explicitScore != 0 ? explicitScore : rawScore * 0.5;
    }

    private static double ResolveSemanticAnchorMatchScore(
        IReadOnlyDictionary<string, string> snapshot,
        string prefix,
        double rawScore,
        double keywordScore)
    {
        var explicitScore = ParseDouble(snapshot, $"{prefix}SemanticAnchorMatchScore");
        return explicitScore != 0 ? explicitScore : rawScore - keywordScore;
    }

    private static string BuildResidualProbableCause(
        string cluster,
        double positiveKeyword,
        double negativeKeyword,
        double positiveSemantic,
        double negativeSemantic)
    {
        return cluster switch
        {
            "DeprecatedNoise" when negativeKeyword >= positiveKeyword || negativeSemantic >= positiveSemantic
                => "Deprecated negative has comparable or stronger keyword/semantic match than the active positive.",
            "DeprecatedNoise"
                => "Deprecated lifecycle signal is not sufficient to overcome the negative candidate score.",
            "KeywordNoise"
                => "Keyword overlap favors a low-value negative candidate.",
            "VersionConflict"
                => "Older version candidate outranks current version evidence.",
            "WrongLifecycle"
                => "Lifecycle status should demote the negative candidate but does not in the offline baseline.",
            "LowRecency"
                => "Recency/current-state signal is too weak for the positive candidate.",
            "RelationEvidenceMissing"
                => "Relation or constraint evidence is missing from the offline feature snapshot.",
            _ => "Residual failure requires additional hard negative examples or richer feature coverage."
        };
    }

    private static string BuildClusterProbableCause(string cluster)
    {
        return cluster switch
        {
            "DeprecatedNoise" => "Deprecated or historical negatives retain high lexical/semantic similarity.",
            "KeywordNoise" => "Keyword-only overlap can make low-value negatives competitive.",
            "VersionConflict" => "Version markers are not separable enough in current pair features.",
            "WrongLifecycle" => "Lifecycle markers are weak or absent in current pair features.",
            "LowRecency" => "Recent/current evidence is not strongly represented.",
            "RelationEvidenceMissing" => "Relation evidence is absent or too sparse.",
            _ => "Mixed residual failure pattern."
        };
    }

    private static RankerComparisonExample BuildComparisonExample(
        RankerPairOutcome baseline,
        RankerPairOutcome candidate,
        string reason)
    {
        return new RankerComparisonExample
        {
            EvalSampleId = baseline.Pair.EvalSampleId,
            Mode = baseline.Pair.Mode,
            Intent = baseline.Pair.Intent,
            PositiveCandidateId = baseline.Pair.PositiveCandidateId,
            NegativeCandidateId = baseline.Pair.NegativeCandidateId,
            BaselinePositiveScore = baseline.PositiveScore,
            BaselineNegativeScore = baseline.NegativeScore,
            CandidatePositiveScore = candidate.PositiveScore,
            CandidateNegativeScore = candidate.NegativeScore,
            FailureCluster = baseline.FailureCluster,
            Reason = reason
        };
    }

    private static CandidatePairScore ScorePairByTunableFeatures(
        RankingPairExample pair,
        RankerFeatureWeights weights,
        IReadOnlySet<string> disabledFeatures)
    {
        return new CandidatePairScore(
            ScoreCandidateByTunableFeatures(pair, "positive", weights, disabledFeatures),
            ScoreCandidateByTunableFeatures(pair, "negative", weights, disabledFeatures));
    }

    private static double ScoreCandidateByTunableFeatures(
        RankingPairExample pair,
        string prefix,
        RankerFeatureWeights weights,
        IReadOnlySet<string> disabledFeatures)
    {
        var snapshot = pair.FeatureSnapshot;
        var rawScore = ParseDouble(snapshot, $"{prefix}Score");
        var keywordScore = ParseDouble(snapshot, $"{prefix}KeywordMatchScore");
        var semanticScore = ParseDouble(snapshot, $"{prefix}SemanticAnchorMatchScore");
        if (keywordScore == 0 && semanticScore == 0)
        {
            keywordScore = rawScore * 0.5;
            semanticScore = rawScore - keywordScore;
        }

        var score = 0.0;
        if (!IsDisabled(disabledFeatures, FeatureKeywordMatch))
        {
            score += keywordScore;
        }

        if (!IsDisabled(disabledFeatures, FeatureSemanticAnchorMatch))
        {
            score += semanticScore;
        }

        if (ParseBool(snapshot, $"{prefix}Selected"))
        {
            score += 8;
        }

        var rank = ParseInt(snapshot, $"{prefix}Rank");
        if (rank > 0)
        {
            score += 4.0 / rank;
        }

        var section = GetString(snapshot, $"{prefix}Section");
        var kind = GetString(snapshot, $"{prefix}Kind");
        if (!IsDisabled(disabledFeatures, FeatureChannelSource))
        {
            if (section.Contains("constraints", StringComparison.OrdinalIgnoreCase)
                && !IsDisabled(disabledFeatures, FeatureConstraintMatch))
            {
                score += 2;
            }
            else if (section.Contains("working", StringComparison.OrdinalIgnoreCase)
                && !IsDisabled(disabledFeatures, FeatureShortTermMatch))
            {
                score += 2;
            }
            else if (section.Contains("stable", StringComparison.OrdinalIgnoreCase)
                && !IsDisabled(disabledFeatures, FeatureStableMatch))
            {
                score += 1;
            }
        }

        if (!IsDisabled(disabledFeatures, FeatureLifecycle)
            && IsBaselineLifecycleNoise(kind))
        {
            score -= weights.LifecyclePenaltyWeight;
        }

        var candidateText = BuildCandidateText(pair, prefix);
        if (!IsDisabled(disabledFeatures, FeatureRecency))
        {
            score += weights.RecencyWeight * DetectRecency(candidateText, section, kind);
        }

        if (!IsDisabled(disabledFeatures, FeatureRelationPath))
        {
            score += weights.RelationEvidenceBoost * DetectRelationEvidence(pair, prefix, candidateText);
        }

        if (!IsDisabled(disabledFeatures, FeatureImportance))
        {
            score += DetectImportance(snapshot, prefix);
        }

        score += weights.CurrentVersionBoost * DetectCurrentVersion(candidateText);
        score += weights.ActiveStatusBoost * DetectActiveStatus(candidateText, section, kind);
        score -= weights.NoiseKeywordPenalty * DetectKeywordNoise(candidateText);
        score += weights.StablePreferenceBoost * DetectStablePreference(candidateText, section, kind);
        return score;
    }

    private static IReadOnlyList<RankerWeightSweepConfiguration> BuildWeightSweepConfigurations(
        RankerFeatureWeights baseline)
    {
        var configs = new List<RankerWeightSweepConfiguration>
        {
            new("default", "default", 0, baseline)
        };
        AddSweep(configs, "lifecyclePenaltyWeight", [0, 2, 4, 6, 8], value => WithLifecyclePenaltyWeight(baseline, value));
        AddSweep(configs, "recencyWeight", [0, 0.5, 1, 2], value => WithRecencyWeight(baseline, value));
        AddSweep(configs, "currentVersionBoost", [0, 1, 2, 3], value => WithCurrentVersionBoost(baseline, value));
        AddSweep(configs, "activeStatusBoost", [0, 1, 2, 3], value => WithActiveStatusBoost(baseline, value));
        AddSweep(configs, "noiseKeywordPenalty", [0, 2, 4, 6], value => WithNoiseKeywordPenalty(baseline, value));
        AddSweep(configs, "relationEvidenceBoost", [0, 1, 2, 3], value => WithRelationEvidenceBoost(baseline, value));
        AddSweep(configs, "stablePreferenceBoost", [0, 1, 2, 3], value => WithStablePreferenceBoost(baseline, value));
        return configs
            .GroupBy(static item => item.ConfigurationId, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
    }

    private static void AddSweep(
        List<RankerWeightSweepConfiguration> configs,
        string parameterName,
        IReadOnlyList<double> values,
        Func<double, RankerFeatureWeights> buildWeights)
    {
        foreach (var value in values)
        {
            configs.Add(new RankerWeightSweepConfiguration(
                $"{parameterName}={Format(value)}",
                parameterName,
                value,
                buildWeights(value)));
        }
    }

    private static RankerFeatureWeights CreateDefaultRankerFeatureWeights()
        => new()
        {
            LifecyclePenaltyWeight = 4,
            RecencyWeight = 0,
            CurrentVersionBoost = 0,
            ActiveStatusBoost = 0,
            NoiseKeywordPenalty = 0,
            RelationEvidenceBoost = 0,
            StablePreferenceBoost = 0
        };

    private static RankerFeatureWeights WithLifecyclePenaltyWeight(RankerFeatureWeights baseline, double value)
        => CopyWeights(baseline, lifecyclePenaltyWeight: value);

    private static RankerFeatureWeights WithRecencyWeight(RankerFeatureWeights baseline, double value)
        => CopyWeights(baseline, recencyWeight: value);

    private static RankerFeatureWeights WithCurrentVersionBoost(RankerFeatureWeights baseline, double value)
        => CopyWeights(baseline, currentVersionBoost: value);

    private static RankerFeatureWeights WithActiveStatusBoost(RankerFeatureWeights baseline, double value)
        => CopyWeights(baseline, activeStatusBoost: value);

    private static RankerFeatureWeights WithNoiseKeywordPenalty(RankerFeatureWeights baseline, double value)
        => CopyWeights(baseline, noiseKeywordPenalty: value);

    private static RankerFeatureWeights WithRelationEvidenceBoost(RankerFeatureWeights baseline, double value)
        => CopyWeights(baseline, relationEvidenceBoost: value);

    private static RankerFeatureWeights WithStablePreferenceBoost(RankerFeatureWeights baseline, double value)
        => CopyWeights(baseline, stablePreferenceBoost: value);

    private static RankerFeatureWeights CopyWeights(
        RankerFeatureWeights baseline,
        double? lifecyclePenaltyWeight = null,
        double? recencyWeight = null,
        double? currentVersionBoost = null,
        double? activeStatusBoost = null,
        double? noiseKeywordPenalty = null,
        double? relationEvidenceBoost = null,
        double? stablePreferenceBoost = null)
        => new()
        {
            LifecyclePenaltyWeight = lifecyclePenaltyWeight ?? baseline.LifecyclePenaltyWeight,
            RecencyWeight = recencyWeight ?? baseline.RecencyWeight,
            CurrentVersionBoost = currentVersionBoost ?? baseline.CurrentVersionBoost,
            ActiveStatusBoost = activeStatusBoost ?? baseline.ActiveStatusBoost,
            NoiseKeywordPenalty = noiseKeywordPenalty ?? baseline.NoiseKeywordPenalty,
            RelationEvidenceBoost = relationEvidenceBoost ?? baseline.RelationEvidenceBoost,
            StablePreferenceBoost = stablePreferenceBoost ?? baseline.StablePreferenceBoost
        };

    private static string BuildWeightRecommendation(double accuracyDelta, int newlyFailedCount)
    {
        if (newlyFailedCount > 0)
        {
            return "DoNotUseOffline";
        }

        if (accuracyDelta > 0.0000001)
        {
            return "OfflineCandidate";
        }

        if (Math.Abs(accuracyDelta) < 0.0000001)
        {
            return "Neutral";
        }

        return "Regressed";
    }

    private static string ClassifyFailureCluster(RankingPairExample pair)
    {
        var negativeText = BuildCandidateText(pair, "negative");
        var positiveText = BuildCandidateText(pair, "positive");
        var allText = string.Join(' ', pair.Query, pair.Reason, positiveText, negativeText);
        if (ContainsAny(negativeText, "rejected", "lifecycle", "invalid", "inactive", "blocked"))
        {
            return "WrongLifecycle";
        }

        if (ContainsAny(negativeText, "deprecated", "historical", "superseded", "obsolete", "废弃", "作废", "过期"))
        {
            return "DeprecatedNoise";
        }

        if (ContainsAny(negativeText, "noise", "keyword", "噪音", "关键词"))
        {
            return "KeywordNoise";
        }

        if (ContainsAny(allText, "version", "v1", "v2", "old", "latest", "current", "版本", "旧版", "新版", "替代", "覆盖"))
        {
            return "VersionConflict";
        }

        if (ContainsAny(positiveText, "recent", "current", "latest", "last", "working", "最近", "当前", "恢复点"))
        {
            return "LowRecency";
        }

        if (ContainsAny(allText, "relation", "evidence", "constraint", "conflict", "关联", "关系", "证据", "约束", "冲突"))
        {
            return "RelationEvidenceMissing";
        }

        return "Other";
    }

    private static bool IsBaselineLifecycleNoise(string kind)
    {
        return kind.Contains("historical", StringComparison.OrdinalIgnoreCase)
            || kind.Contains("deprecated", StringComparison.OrdinalIgnoreCase);
    }

    private static double DetectRecency(string text, string section, string kind)
    {
        return ContainsAny(text, "recent", "current", "latest", "last", "v2", "最近", "当前", "最新", "恢复点")
            || section.Contains("recent", StringComparison.OrdinalIgnoreCase)
            || section.Contains("working", StringComparison.OrdinalIgnoreCase)
            || kind.Contains("recent", StringComparison.OrdinalIgnoreCase)
            || kind.Contains("working", StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;
    }

    private static double DetectRelationEvidence(RankingPairExample pair, string prefix, string candidateText)
    {
        var explicitPathCount = ParseDouble(pair.FeatureSnapshot, $"{prefix}RelationPathCount");
        if (explicitPathCount > 0)
        {
            return explicitPathCount;
        }

        return ContainsAny(string.Join(' ', pair.Query, pair.Reason, candidateText), "relation", "evidence", "constraint", "conflict", "关联", "关系", "证据", "约束", "冲突")
            ? 1
            : 0;
    }

    private static double DetectImportance(IReadOnlyDictionary<string, string> snapshot, string prefix)
    {
        var importance = ParseDouble(snapshot, $"{prefix}Importance");
        if (importance > 0)
        {
            return importance;
        }

        return ParseBool(snapshot, $"{prefix}Selected") ? 0.5 : 0;
    }

    private static double DetectCurrentVersion(string text)
    {
        return ContainsAny(text, "v2", "latest", "current", "new", "confirmed", "最新", "当前", "新版", "确认")
            ? 1
            : 0;
    }

    private static double DetectActiveStatus(string text, string section, string kind)
    {
        return ContainsAny(text, "active", "current", "working", "confirmed", "accepted", "活跃", "当前", "确认")
            || section.Contains("working", StringComparison.OrdinalIgnoreCase)
            || kind.Contains("working", StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;
    }

    private static double DetectKeywordNoise(string text)
    {
        return ContainsAny(text, "noise", "keyword", "old-topic", "噪音", "关键词")
            ? 1
            : 0;
    }

    private static double DetectStablePreference(string text, string section, string kind)
    {
        return ContainsAny(text, "preference", "stable", "confirmed-rule", "偏好", "长期", "稳定")
            || section.Contains("stable", StringComparison.OrdinalIgnoreCase)
            || kind.Contains("preference", StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;
    }

    private static bool IsDisabled(IReadOnlySet<string> disabledFeatures, string feature)
        => disabledFeatures.Contains(feature);

    private static string BuildCandidateText(RankingPairExample pair, string prefix)
    {
        var candidateId = string.Equals(prefix, "positive", StringComparison.OrdinalIgnoreCase)
            ? pair.PositiveCandidateId
            : pair.NegativeCandidateId;
        return string.Join(
            ' ',
            candidateId,
            GetString(pair.FeatureSnapshot, $"{prefix}Kind"),
            GetString(pair.FeatureSnapshot, $"{prefix}Section"),
            pair.Reason,
            pair.Query);
    }

    private static bool ContainsAny(string value, params string[] patterns)
    {
        return patterns.Any(pattern => value.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatTopCluster(IReadOnlyList<RankerFailureClusterSummary> clusters)
    {
        var top = clusters.FirstOrDefault();
        return top is null || string.IsNullOrWhiteSpace(top.Cluster)
            ? "-"
            : $"{top.Cluster} ({top.Count})";
    }

    private static string FormatSignedPercent(double value)
    {
        var sign = value > 0 ? "+" : string.Empty;
        return sign + FormatPercent(value);
    }

    private static string FormatWeights(RankerFeatureWeights weights)
        => string.Join(
            ", ",
            $"lifecyclePenaltyWeight={Format(weights.LifecyclePenaltyWeight)}",
            $"recencyWeight={Format(weights.RecencyWeight)}",
            $"currentVersionBoost={Format(weights.CurrentVersionBoost)}",
            $"activeStatusBoost={Format(weights.ActiveStatusBoost)}",
            $"noiseKeywordPenalty={Format(weights.NoiseKeywordPenalty)}",
            $"relationEvidenceBoost={Format(weights.RelationEvidenceBoost)}",
            $"stablePreferenceBoost={Format(weights.StablePreferenceBoost)}");

    private static RankerScenarioEvaluation EmptyRankerScenario(string baselineName)
        => new(
            EmptyRankerBaseline(baselineName),
            new Dictionary<string, RankerPairOutcome>(StringComparer.OrdinalIgnoreCase));

    private static void AppendAblationDetails(
        StringBuilder builder,
        IReadOnlyList<RankerFeatureAblationResult> ablations)
    {
        foreach (var result in ablations)
        {
            builder.AppendLine();
            builder.AppendLine($"## {result.DisabledFeature}");
            AppendFailureClusters(builder, result.FailureClusters);
            AppendComparisonExamples(builder, "Top Fixed Examples", result.TopFixedExamples);
            AppendComparisonExamples(builder, "Top Newly Failed Examples", result.TopNewlyFailedExamples);
        }
    }

    private static void AppendSweepDetails(
        StringBuilder builder,
        IReadOnlyList<RankerWeightSweepResult> results)
    {
        foreach (var result in results)
        {
            builder.AppendLine();
            builder.AppendLine($"## {result.ConfigurationId}");
            builder.AppendLine();
            builder.AppendLine($"- Weights: `{FormatWeights(result.Weights)}`");
            AppendFailureClusters(builder, result.FailureClusters);
            AppendComparisonExamples(builder, "Top Fixed Examples", result.TopFixedExamples);
            AppendComparisonExamples(builder, "Top Newly Failed Examples", result.TopNewlyFailedExamples);
        }
    }

    private static void AppendFailureClusters(
        StringBuilder builder,
        IReadOnlyList<RankerFailureClusterSummary> clusters)
    {
        builder.AppendLine();
        builder.AppendLine("### Failure Clusters");
        if (clusters.Count == 0)
        {
            builder.AppendLine("- (none)");
            return;
        }

        builder.AppendLine("| Cluster | Count | Examples |");
        builder.AppendLine("|---|---:|---|");
        foreach (var cluster in clusters)
        {
            builder.AppendLine($"| {cluster.Cluster} | {cluster.Count} | {string.Join(", ", cluster.ExampleIds)} |");
        }
    }

    private static void AppendComparisonExamples(
        StringBuilder builder,
        string title,
        IReadOnlyList<RankerComparisonExample> examples)
    {
        builder.AppendLine();
        builder.AppendLine($"### {title}");
        if (examples.Count == 0)
        {
            builder.AppendLine("- (none)");
            return;
        }

        builder.AppendLine("| Sample | Mode | Intent | Positive | Negative | BaselineMargin | CandidateMargin | Cluster | Reason |");
        builder.AppendLine("|---|---|---|---|---|---:|---:|---|---|");
        foreach (var example in examples)
        {
            var baselineMargin = example.BaselinePositiveScore - example.BaselineNegativeScore;
            var candidateMargin = example.CandidatePositiveScore - example.CandidateNegativeScore;
            builder.AppendLine($"| {example.EvalSampleId} | {example.Mode} | {example.Intent} | {example.PositiveCandidateId} | {example.NegativeCandidateId} | {Format(baselineMargin)} | {Format(candidateMargin)} | {example.FailureCluster} | {example.Reason} |");
        }
    }

    private static void AppendResidualClusters(
        StringBuilder builder,
        IReadOnlyList<RankerResidualFailureCluster> clusters)
    {
        if (clusters.Count == 0)
        {
            builder.AppendLine("- (none)");
            return;
        }

        builder.AppendLine();
        builder.AppendLine("| Cluster | Count | Avg Margin | Examples | Probable Cause |");
        builder.AppendLine("|---|---:|---:|---|---|");
        foreach (var cluster in clusters)
        {
            builder.AppendLine($"| {cluster.Cluster} | {cluster.Count} | {Format(cluster.AverageMargin)} | {string.Join(", ", cluster.ExampleIds)} | {cluster.ProbableCause} |");
        }
    }

    private static void AppendFeatureConflicts(
        StringBuilder builder,
        IReadOnlyList<RankerFeatureConflictSummary> conflicts)
    {
        if (conflicts.Count == 0)
        {
            builder.AppendLine("- (none)");
            return;
        }

        builder.AppendLine("| Feature | Failures | Positive Avg | Negative Avg | Delta | Interpretation |");
        builder.AppendLine("|---|---:|---:|---:|---:|---|");
        foreach (var conflict in conflicts)
        {
            builder.AppendLine($"| {conflict.FeatureName} | {conflict.FailureCount} | {Format(conflict.AveragePositiveValue)} | {Format(conflict.AverageNegativeValue)} | {Format(conflict.AverageDelta)} | {conflict.Interpretation} |");
        }
    }

    private static void AppendHardNegativeRecommendations(
        StringBuilder builder,
        IReadOnlyList<RankerHardNegativeRecommendation> recommendations)
    {
        if (recommendations.Count == 0)
        {
            builder.AppendLine("- (none)");
            return;
        }

        builder.AppendLine("| Type | Cluster | Count | Examples | Reason | Suggested Action |");
        builder.AppendLine("|---|---|---:|---|---|---|");
        foreach (var recommendation in recommendations)
        {
            builder.AppendLine($"| {recommendation.RecommendationType} | {recommendation.Cluster} | {recommendation.Count} | {string.Join(", ", recommendation.ExampleIds)} | {recommendation.Reason} | {recommendation.SuggestedAction} |");
        }
    }

    private static void AppendResidualFailures(
        StringBuilder builder,
        IReadOnlyList<RankerResidualFailureDetail> failures)
    {
        if (failures.Count == 0)
        {
            builder.AppendLine("- (none)");
            return;
        }

        builder.AppendLine("| Sample | Mode | Intent | Positive | Negative | PosScore | NegScore | Margin | PosKeyword | NegKeyword | PosSemantic | NegSemantic | PosSelected | NegSelected | PosRank | NegRank | PosKind | NegKind | PosSection | NegSection | Cluster | Probable Cause |");
        builder.AppendLine("|---|---|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|---|---:|---:|---|---|---|---|---|---|");
        foreach (var failure in failures)
        {
            builder.AppendLine($"| {failure.EvalSampleId} | {failure.Mode} | {failure.Intent} | {failure.PositiveCandidateId} | {failure.NegativeCandidateId} | {Format(failure.PositiveScore)} | {Format(failure.NegativeScore)} | {Format(failure.Margin)} | {Format(failure.PositiveKeywordMatchScore)} | {Format(failure.NegativeKeywordMatchScore)} | {Format(failure.PositiveSemanticAnchorMatchScore)} | {Format(failure.NegativeSemanticAnchorMatchScore)} | {failure.PositiveSelected} | {failure.NegativeSelected} | {failure.PositiveRank} | {failure.NegativeRank} | {failure.PositiveKind} | {failure.NegativeKind} | {failure.PositiveSection} | {failure.NegativeSection} | {failure.FailureCluster} | {failure.ProbableCause} |");
        }
    }

    private sealed record RankerScenarioEvaluation(
        RankerBaselineResult Metrics,
        IReadOnlyDictionary<string, RankerPairOutcome> Outcomes);

    private sealed record RankerPairOutcome(
        RankingPairExample Pair,
        double PositiveScore,
        double NegativeScore,
        double Margin,
        bool Succeeded,
        string FailureCluster);

    private sealed record RankerScenarioComparison(
        IReadOnlyList<RankerComparisonExample> Fixed,
        IReadOnlyList<RankerComparisonExample> NewlyFailed);

    private sealed record RankerWeightSweepConfiguration(
        string ConfigurationId,
        string ParameterName,
        double ParameterValue,
        RankerFeatureWeights Weights);
}
