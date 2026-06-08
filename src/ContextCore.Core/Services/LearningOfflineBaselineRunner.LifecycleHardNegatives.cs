using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services;

public sealed partial class LearningOfflineBaselineRunner
{
    private const string HardNegativeDeprecatedSameKeyword = "DeprecatedSameKeyword";
    private const string HardNegativeVersionConflict = "VersionConflict";
    private const string HardNegativeHistoricalSelectedNoise = "HistoricalSelectedNoise";
    private const string HardNegativeWeakLifecycleMarker = "WeakLifecycleMarker";
    private const string HardNegativeSemanticAnchorOvermatch = "SemanticAnchorOvermatch";
    private const string HardNegativeKeywordNoise = "KeywordNoise";
    private const string ExpectedPositivePreference = "PositiveOverNegative";
    private const double LifecycleTargetAccuracyFloor = 0.9047619047619048;

    private static readonly JsonSerializerOptions JsonLineOptions = new(JsonSerializerDefaults.Web);

    public async Task<HardNegativeDatasetReport> RunHardNegativeGenerationAsync(
        string residualAuditPath,
        string jsonLinesOutputPath,
        string reportJsonOutputPath,
        string reportMarkdownOutputPath,
        CancellationToken cancellationToken = default)
    {
        var resolvedAuditPath = ResolvePath(residualAuditPath);
        RankerResidualErrorAuditReport? sourceReport = null;
        var notReadyReasons = new List<string>();
        if (!File.Exists(resolvedAuditPath))
        {
            notReadyReasons.Add($"residual audit report not found: {resolvedAuditPath}");
        }
        else
        {
            var json = await File.ReadAllTextAsync(resolvedAuditPath, cancellationToken).ConfigureAwait(false);
            sourceReport = JsonSerializer.Deserialize<RankerResidualErrorAuditReport>(json, JsonOptions);
            if (sourceReport is null)
            {
                notReadyReasons.Add($"residual audit report deserialize failed: {resolvedAuditPath}");
            }
        }

        var report = sourceReport is null
            ? BuildEmptyHardNegativeReport(resolvedAuditPath, ResolvePath(jsonLinesOutputPath), notReadyReasons)
            : BuildHardNegativeReport(sourceReport, resolvedAuditPath, ResolvePath(jsonLinesOutputPath));

        await WriteHardNegativeJsonLinesAsync(report.Examples, jsonLinesOutputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteReportAsync(report, BuildHardNegativeMarkdownReport(report), reportJsonOutputPath, reportMarkdownOutputPath, cancellationToken)
            .ConfigureAwait(false);
        return report;
    }

    public HardNegativeDatasetReport BuildHardNegativeReport(
        RankerResidualErrorAuditReport sourceReport,
        string sourceAuditPath = "",
        string jsonLinesOutputPath = "")
    {
        ArgumentNullException.ThrowIfNull(sourceReport);

        var notReadyReasons = sourceReport.Failures.Count == 0
            ? new[] { "residual audit contains no failures" }
            : Array.Empty<string>();
        var examples = BuildHardNegativeExamples(sourceReport.Failures);
        return new HardNegativeDatasetReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            SourceAuditPath = sourceAuditPath,
            OutputPath = jsonLinesOutputPath,
            Ready = notReadyReasons.Length == 0,
            Status = notReadyReasons.Length == 0 ? LearningDatasetReadinessStatus.Ready : LearningDatasetReadinessStatus.NotReady,
            NotReadyReasons = notReadyReasons,
            SourceFailureCount = sourceReport.Failures.Count,
            ExampleCount = examples.Count,
            TypeCounts = examples
                .GroupBy(static example => example.HardNegativeType, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase),
            ClusterCounts = examples
                .GroupBy(static example => example.Metadata.TryGetValue("failureCluster", out var cluster) ? cluster : "Unknown", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase),
            Examples = examples,
            PolicyVersion = PolicyVersion
        };
    }

    public async Task<LifecycleAwareRankerReport> RunLifecycleAwareRankerAsync(
        string inputPath,
        string jsonOutputPath,
        string markdownOutputPath,
        CancellationToken cancellationToken = default)
    {
        var resolvedInputPath = ResolvePath(inputPath);
        var pairs = await ReadJsonLinesAsync<RankingPairExample>(resolvedInputPath, cancellationToken)
            .ConfigureAwait(false);
        var report = BuildLifecycleAwareRankerReport(pairs, resolvedInputPath);
        await WriteReportAsync(report, BuildLifecycleAwareRankerMarkdownReport(report), jsonOutputPath, markdownOutputPath, cancellationToken)
            .ConfigureAwait(false);
        return report;
    }

    public LifecycleAwareRankerReport BuildLifecycleAwareRankerReport(
        IReadOnlyList<RankingPairExample> pairs,
        string inputPath = "")
    {
        ArgumentNullException.ThrowIfNull(pairs);

        var notReadyReasons = BuildRankerNotReadyReasons(pairs);
        var split = SplitRankingPairs(pairs);
        if (pairs.Count == 0)
        {
            return new LifecycleAwareRankerReport
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                InputPath = inputPath,
                PairCount = pairs.Count,
                Ready = false,
                Status = LearningDatasetReadinessStatus.NotReady,
                NotReadyReasons = notReadyReasons,
                Split = split.Summary,
                PolicyVersion = PolicyVersion
            };
        }

        var rule = EvaluateRankerScenario(RuleScoreBaseline, split.Test, ScorePairByRule);
        var simple = EvaluateRankerScenario(SimpleFeatureWeightedBaseline, split.Test, ScorePairByWeightedFeatures);
        var lifecycle = EvaluateRankerScenario(LifecycleAwareFeatureBaseline, split.Test, ScorePairByLifecycleAwareFeatures);
        var results = new[]
        {
            BuildLifecycleAwareRankerResult(rule, simple),
            BuildLifecycleAwareRankerResult(simple, simple),
            BuildLifecycleAwareRankerResult(lifecycle, simple)
        };
        var best = results
            .OrderByDescending(static item => item.PairwiseAccuracy)
            .ThenBy(static item => item.FalsePositiveRate)
            .First();
        var lifecycleResult = results.Single(static item => item.BaselineName == LifecycleAwareFeatureBaseline);
        var targetFailures = BuildLifecycleTargetFailures(simple.Metrics, lifecycleResult);

        return new LifecycleAwareRankerReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            InputPath = inputPath,
            PairCount = pairs.Count,
            Ready = notReadyReasons.Count == 0,
            Status = notReadyReasons.Count == 0 ? LearningDatasetReadinessStatus.Ready : LearningDatasetReadinessStatus.NotReady,
            NotReadyReasons = notReadyReasons,
            Split = split.Summary,
            Baselines = results,
            BestBaseline = best.BaselineName,
            BaselineAccuracy = simple.Metrics.PairwiseAccuracy,
            BaselineResidualFailures = simple.Outcomes.Values.Count(static outcome => !outcome.Succeeded),
            BaselineDeprecatedNoiseFailures = CountFailureCluster(simple, "DeprecatedNoise"),
            TargetPassed = targetFailures.Count == 0,
            TargetFailures = targetFailures,
            PolicyVersion = PolicyVersion
        };
    }

    public static LifecycleAwareFeatureSet ExtractLifecycleAwareFeatures(
        RankingPairExample pair,
        string prefix)
    {
        ArgumentNullException.ThrowIfNull(pair);

        var candidateText = BuildCandidateText(pair, prefix);
        var section = GetString(pair.FeatureSnapshot, $"{prefix}Section");
        var kind = GetString(pair.FeatureSnapshot, $"{prefix}Kind");
        return ExtractLifecycleAwareFeatures(candidateText, section, kind);
    }

    public static string BuildHardNegativeMarkdownReport(HardNegativeDatasetReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Ranker Hard Negative Dataset Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Source audit: `{report.SourceAuditPath}`");
        builder.AppendLine($"Output: `{report.OutputPath}`");
        builder.AppendLine($"Policy: `{report.PolicyVersion}`");
        builder.AppendLine($"Status: `{report.Status}`");
        builder.AppendLine($"Source failures: `{report.SourceFailureCount}`");
        builder.AppendLine($"Hard negatives: `{report.ExampleCount}`");
        AppendNotReady(builder, report.NotReadyReasons);
        AppendIntDictionary(builder, "Type Counts", report.TypeCounts);
        AppendIntDictionary(builder, "Cluster Counts", report.ClusterCounts);

        builder.AppendLine();
        builder.AppendLine("## Examples");
        if (report.Examples.Count == 0)
        {
            builder.AppendLine("- (none)");
            return builder.ToString();
        }

        builder.AppendLine("| Type | Sample | Positive | Negative | Reason |");
        builder.AppendLine("|---|---|---|---|---|");
        foreach (var example in report.Examples.Take(40))
        {
            builder.AppendLine($"| {example.HardNegativeType} | {example.SourceSampleId} | {example.PositiveCandidateId} | {example.NegativeCandidateId} | {example.Reason} |");
        }

        return builder.ToString();
    }

    public static string BuildLifecycleAwareRankerMarkdownReport(LifecycleAwareRankerReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Lifecycle-aware Ranker Offline Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Input: `{report.InputPath}`");
        builder.AppendLine($"Policy: `{report.PolicyVersion}`");
        builder.AppendLine($"Status: `{report.Status}`");
        builder.AppendLine($"Pairs: `{report.PairCount}`");
        builder.AppendLine($"Best baseline: `{(string.IsNullOrWhiteSpace(report.BestBaseline) ? "-" : report.BestBaseline)}`");
        builder.AppendLine($"Target passed: `{report.TargetPassed}`");
        builder.AppendLine($"Simple baseline: accuracy `{FormatPercent(report.BaselineAccuracy)}`, residual `{report.BaselineResidualFailures}`, DeprecatedNoise `{report.BaselineDeprecatedNoiseFailures}`");
        AppendNotReady(builder, report.NotReadyReasons);
        AppendSplit(builder, report.Split);

        if (report.TargetFailures.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Target Failures");
            foreach (var failure in report.TargetFailures)
            {
                builder.AppendLine($"- {failure}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Baselines");
        builder.AppendLine();
        builder.AppendLine("| Baseline | PairwiseAccuracy | AUC | WinOverSimple | FPR | FNR | ResidualFailures | DeprecatedNoise |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var result in report.Baselines)
        {
            builder.AppendLine($"| {result.BaselineName} | {FormatPercent(result.PairwiseAccuracy)} | {(result.Auc.HasValue ? Format(result.Auc.Value) : "-")} | {FormatPercent(result.WinRateOverSimple)} | {FormatPercent(result.FalsePositiveRate)} | {FormatPercent(result.FalseNegativeRate)} | {result.ResidualFailures} | {result.DeprecatedNoiseFailures} |");
        }

        foreach (var result in report.Baselines)
        {
            builder.AppendLine();
            builder.AppendLine($"## {result.BaselineName}");
            AppendFailureClusters(builder, result.FailureClusters);
            AppendComparisonExamples(builder, "Top Fixed Examples vs Simple", result.TopFixedExamples);
            AppendComparisonExamples(builder, "Top Newly Failed Examples vs Simple", result.TopNewlyFailedExamples);
        }

        return builder.ToString();
    }

    private static HardNegativeDatasetReport BuildEmptyHardNegativeReport(
        string sourceAuditPath,
        string jsonLinesOutputPath,
        IReadOnlyList<string> notReadyReasons)
        => new()
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            SourceAuditPath = sourceAuditPath,
            OutputPath = jsonLinesOutputPath,
            Ready = false,
            Status = LearningDatasetReadinessStatus.NotReady,
            NotReadyReasons = notReadyReasons,
            PolicyVersion = PolicyVersion
        };

    private static IReadOnlyList<HardNegativeExample> BuildHardNegativeExamples(
        IReadOnlyList<RankerResidualFailureDetail> failures)
    {
        var examples = new List<HardNegativeExample>();
        foreach (var failure in failures)
        {
            foreach (var type in ResolveHardNegativeTypes(failure))
            {
                examples.Add(BuildHardNegativeExample(failure, type));
            }
        }

        return examples
            .GroupBy(static example => example.ExampleId, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static example => example.SourceSampleId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static example => example.HardNegativeType, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ResolveHardNegativeTypes(RankerResidualFailureDetail failure)
    {
        var types = new List<string>();
        if (string.Equals(failure.FailureCluster, "DeprecatedNoise", StringComparison.OrdinalIgnoreCase))
        {
            types.Add(HardNegativeDeprecatedSameKeyword);
            types.Add(HardNegativeVersionConflict);
            types.Add(HardNegativeHistoricalSelectedNoise);
            types.Add(HardNegativeWeakLifecycleMarker);
            types.Add(HardNegativeSemanticAnchorOvermatch);
        }

        var negativeText = string.Join(' ', failure.NegativeCandidateId, failure.NegativeKind, failure.NegativeSection, failure.ProbableCause);
        if (string.Equals(failure.FailureCluster, "KeywordNoise", StringComparison.OrdinalIgnoreCase)
            || ContainsAny(negativeText, "noise", "keyword", "噪音", "关键词"))
        {
            types.Add(HardNegativeKeywordNoise);
        }

        if (ContainsAny(string.Join(' ', failure.PositiveCandidateId, failure.NegativeCandidateId, failure.ProbableCause), "v1", "v2", "version", "old", "latest", "current", "旧版", "新版")
            && !types.Contains(HardNegativeVersionConflict, StringComparer.OrdinalIgnoreCase))
        {
            types.Add(HardNegativeVersionConflict);
        }

        return types
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static HardNegativeExample BuildHardNegativeExample(
        RankerResidualFailureDetail failure,
        string hardNegativeType)
    {
        var positiveFeatures = BuildResidualFeatureDictionary(
            failure.PositiveCandidateId,
            failure.PositiveKind,
            failure.PositiveSection,
            failure.PositiveScore,
            failure.PositiveKeywordMatchScore,
            failure.PositiveSemanticAnchorMatchScore,
            failure.PositiveSelected,
            failure.PositiveRank);
        var negativeFeatures = BuildResidualFeatureDictionary(
            failure.NegativeCandidateId,
            failure.NegativeKind,
            failure.NegativeSection,
            failure.NegativeScore,
            failure.NegativeKeywordMatchScore,
            failure.NegativeSemanticAnchorMatchScore,
            failure.NegativeSelected,
            failure.NegativeRank);
        var key = string.Join('|', failure.EvalSampleId, hardNegativeType, failure.PositiveCandidateId, failure.NegativeCandidateId);
        return new HardNegativeExample
        {
            ExampleId = $"hn-{StableId(key)}",
            WorkspaceId = "offline-eval",
            CollectionId = "learning-baseline",
            SourceSampleId = failure.EvalSampleId,
            Mode = failure.Mode,
            Intent = failure.Intent,
            PositiveCandidateId = failure.PositiveCandidateId,
            NegativeCandidateId = failure.NegativeCandidateId,
            HardNegativeType = hardNegativeType,
            Reason = BuildHardNegativeReason(failure, hardNegativeType),
            PositiveFeatures = positiveFeatures,
            NegativeFeatures = negativeFeatures,
            ExpectedPreference = ExpectedPositivePreference,
            CreatedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["source"] = "ranker-residual-audit",
                ["failureCluster"] = failure.FailureCluster,
                ["probableCause"] = failure.ProbableCause,
                ["margin"] = Format(failure.Margin)
            }
        };
    }

    private static Dictionary<string, string> BuildResidualFeatureDictionary(
        string candidateId,
        string kind,
        string section,
        double score,
        double keywordScore,
        double semanticScore,
        bool selected,
        int rank)
    {
        var lifecycle = ExtractLifecycleAwareFeatures(string.Join(' ', candidateId, kind, section), section, kind);
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["candidateId"] = candidateId,
            ["kind"] = kind,
            ["section"] = section,
            ["score"] = Format(score),
            ["keywordMatchScore"] = Format(keywordScore),
            ["semanticAnchorMatchScore"] = Format(semanticScore),
            ["selected"] = selected ? "true" : "false",
            ["rank"] = rank.ToString(CultureInfo.InvariantCulture),
            ["isDeprecated"] = lifecycle.IsDeprecated ? "true" : "false",
            ["isSuperseded"] = lifecycle.IsSuperseded ? "true" : "false",
            ["isHistorical"] = lifecycle.IsHistorical ? "true" : "false",
            ["isRejected"] = lifecycle.IsRejected ? "true" : "false",
            ["hasReplacement"] = lifecycle.HasReplacement ? "true" : "false",
            ["hasSupersedesRelation"] = lifecycle.HasSupersedesRelation ? "true" : "false",
            ["versionDistance"] = Format(lifecycle.VersionDistance),
            ["isCurrentVersion"] = lifecycle.IsCurrentVersion ? "true" : "false",
            ["lifecycleConfidence"] = Format(lifecycle.LifecycleConfidence),
            ["historicalSectionOnly"] = lifecycle.HistoricalSectionOnly ? "true" : "false"
        };
    }

    private static string BuildHardNegativeReason(
        RankerResidualFailureDetail failure,
        string hardNegativeType)
        => hardNegativeType switch
        {
            HardNegativeDeprecatedSameKeyword => "Deprecated or historical negative shares strong keyword/semantic surface with the positive.",
            HardNegativeVersionConflict => "Older version or historical negative outranked current positive evidence.",
            HardNegativeHistoricalSelectedNoise => "Historical negative remains competitive despite selected/rank evidence favoring the positive.",
            HardNegativeWeakLifecycleMarker => "Lifecycle marker was not strong enough to demote the negative in the offline baseline.",
            HardNegativeSemanticAnchorOvermatch => "Semantic anchor score overmatched a deprecated or historical negative.",
            HardNegativeKeywordNoise => "Keyword overlap favored a low-value or noisy negative candidate.",
            _ => failure.ProbableCause
        };

    private static CandidatePairScore ScorePairByLifecycleAwareFeatures(RankingPairExample pair)
    {
        return new CandidatePairScore(
            ScoreCandidateByLifecycleAwareFeatures(pair, "positive"),
            ScoreCandidateByLifecycleAwareFeatures(pair, "negative"));
    }

    private static double ScoreCandidateByLifecycleAwareFeatures(
        RankingPairExample pair,
        string prefix)
    {
        var baseScore = ScoreCandidateByWeightedFeatures(pair.FeatureSnapshot, prefix);
        var features = ExtractLifecycleAwareFeatures(pair, prefix);
        var adjustment = 0.0;

        if (features.IsRejected)
        {
            adjustment -= 40;
        }

        if (features.IsDeprecated)
        {
            adjustment -= 22;
        }

        if (features.IsSuperseded)
        {
            adjustment -= 22;
        }

        if (features.IsHistorical)
        {
            adjustment -= 16;
        }

        if (features.HistoricalSectionOnly)
        {
            adjustment -= 6;
        }

        if (features.HasReplacement && !features.IsCurrentVersion)
        {
            adjustment -= 8;
        }

        if (features.IsCurrentVersion)
        {
            adjustment += 12;
        }

        if (features.HasSupersedesRelation && features.IsCurrentVersion)
        {
            adjustment += 4;
        }

        if (IsAuditLike(pair) && string.Equals(prefix, "positive", StringComparison.OrdinalIgnoreCase))
        {
            adjustment += Math.Min(28, features.LifecycleConfidence * 28);
        }

        return baseScore + adjustment;
    }

    private static LifecycleAwareRankerResult BuildLifecycleAwareRankerResult(
        RankerScenarioEvaluation scenario,
        RankerScenarioEvaluation simpleBaseline)
    {
        var comparison = BuildScenarioComparison(simpleBaseline, scenario);
        var clusters = BuildFailureClusters(scenario.Outcomes.Values);
        return new LifecycleAwareRankerResult
        {
            BaselineName = scenario.Metrics.BaselineName,
            PairwiseAccuracy = scenario.Metrics.PairwiseAccuracy,
            Auc = scenario.Metrics.Auc,
            WinRateOverSimple = SafeDivide(comparison.Fixed.Count, simpleBaseline.Outcomes.Count),
            FalsePositiveRate = scenario.Metrics.FalsePositiveRate,
            FalseNegativeRate = scenario.Metrics.FalseNegativeRate,
            ResidualFailures = scenario.Outcomes.Values.Count(static outcome => !outcome.Succeeded),
            DeprecatedNoiseFailures = clusters
                .Where(static cluster => string.Equals(cluster.Cluster, "DeprecatedNoise", StringComparison.OrdinalIgnoreCase))
                .Sum(static cluster => cluster.Count),
            FailureClusters = clusters,
            TopFixedExamples = comparison.Fixed,
            TopNewlyFailedExamples = comparison.NewlyFailed,
            TopFailureExamples = scenario.Metrics.TopFailureExamples
        };
    }

    private static IReadOnlyList<string> BuildLifecycleTargetFailures(
        RankerBaselineResult simple,
        LifecycleAwareRankerResult lifecycle)
    {
        var failures = new List<string>();
        if (lifecycle.PairwiseAccuracy <= Math.Max(simple.PairwiseAccuracy, LifecycleTargetAccuracyFloor) + 0.0000001)
        {
            failures.Add($"PairwiseAccuracy did not improve above simple baseline ({FormatPercent(simple.PairwiseAccuracy)}).");
        }

        if (lifecycle.ResidualFailures > 1)
        {
            failures.Add($"ResidualFailures is {lifecycle.ResidualFailures}, expected <= 1.");
        }

        if (lifecycle.DeprecatedNoiseFailures > 1)
        {
            failures.Add($"DeprecatedNoise is {lifecycle.DeprecatedNoiseFailures}, expected <= 1.");
        }

        if (lifecycle.FalsePositiveRate > simple.FalsePositiveRate + 0.0000001)
        {
            failures.Add($"FalsePositiveRate rose from {FormatPercent(simple.FalsePositiveRate)} to {FormatPercent(lifecycle.FalsePositiveRate)}.");
        }

        if (lifecycle.FalseNegativeRate > simple.FalseNegativeRate + 0.0000001)
        {
            failures.Add($"FalseNegativeRate rose from {FormatPercent(simple.FalseNegativeRate)} to {FormatPercent(lifecycle.FalseNegativeRate)}.");
        }

        return failures;
    }

    private static int CountFailureCluster(
        RankerScenarioEvaluation scenario,
        string cluster)
    {
        return scenario.Outcomes.Values
            .Count(outcome => !outcome.Succeeded
                && string.Equals(outcome.FailureCluster, cluster, StringComparison.OrdinalIgnoreCase));
    }

    private static LifecycleAwareFeatureSet ExtractLifecycleAwareFeatures(
        string candidateText,
        string section,
        string kind)
    {
        var text = string.Join(' ', candidateText, section, kind);
        var isDeprecated = ContainsAny(text, "deprecated", "obsolete", "废弃", "作废", "过期");
        var isSuperseded = ContainsAny(text, "superseded", "supersede", "覆盖", "被替代");
        var isHistorical = ContainsAny(text, "historical", "history", "old", "legacy", "v1", "旧", "历史")
            || section.Contains("historical", StringComparison.OrdinalIgnoreCase)
            || kind.Contains("historical", StringComparison.OrdinalIgnoreCase);
        var isRejected = ContainsAny(text, "rejected", "invalid", "blocked", "inactive", "否决", "拒绝", "无效");
        var hasReplacement = ContainsAny(text, "replacement", "replaced", "replaces", "替代", "新版", "latest", "current", "v2");
        var hasSupersedesRelation = ContainsAny(text, "supersedes", "superseded", "replacement", "replaced", "覆盖", "替代");
        var versionDistance = ResolveVersionDistance(text);
        var isCurrentVersion = ContainsAny(text, "v2", "latest", "current", "new", "active", "confirmed", "当前", "最新", "新版", "确认");
        var riskSignals = new[]
        {
            isDeprecated,
            isSuperseded,
            isHistorical,
            isRejected,
            hasReplacement && !isCurrentVersion
        }.Count(static value => value);
        var lifecycleConfidence = isCurrentVersion && riskSignals == 0
            ? 0.7
            : Math.Min(1, 0.25 + riskSignals * 0.22 + (versionDistance > 0 ? 0.2 : 0));
        var historicalSectionOnly = section.Contains("historical", StringComparison.OrdinalIgnoreCase)
            && !isDeprecated
            && !isSuperseded
            && !isRejected;

        return new LifecycleAwareFeatureSet
        {
            IsDeprecated = isDeprecated,
            IsSuperseded = isSuperseded,
            IsHistorical = isHistorical,
            IsRejected = isRejected,
            HasReplacement = hasReplacement,
            HasSupersedesRelation = hasSupersedesRelation,
            VersionDistance = versionDistance,
            IsCurrentVersion = isCurrentVersion,
            LifecycleConfidence = lifecycleConfidence,
            HistoricalSectionOnly = historicalSectionOnly
        };
    }

    private static double ResolveVersionDistance(string text)
    {
        if (ContainsAny(text, "latest", "current", "new", "最新", "当前", "新版"))
        {
            return 0;
        }

        var matches = Regex.Matches(text, @"\bv(?<version>\d+)\b", RegexOptions.IgnoreCase);
        if (matches.Count == 0)
        {
            return ContainsAny(text, "old", "legacy", "旧", "历史") ? 1 : 0;
        }

        var maxVersion = matches
            .Select(static match => int.TryParse(match.Groups["version"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) ? version : 0)
            .DefaultIfEmpty(0)
            .Max();
        return maxVersion <= 1 ? 1 : 0;
    }

    private static bool IsAuditLike(RankingPairExample pair)
    {
        return string.Equals(pair.Intent, PlanningIntentDetector.AuditDeprecated, StringComparison.OrdinalIgnoreCase)
            || ContainsAny(string.Join(' ', pair.Query, pair.Reason, pair.Intent), "audit", "deprecated", "historical", "history", "obsolete", "审计", "废弃", "历史");
    }

    private static async Task WriteHardNegativeJsonLinesAsync(
        IReadOnlyList<HardNegativeExample> examples,
        string path,
        CancellationToken cancellationToken)
    {
        var lines = examples.Select(static example => JsonSerializer.Serialize(example, JsonLineOptions));
        await WriteTextAsync(path, string.Join(Environment.NewLine, lines), cancellationToken).ConfigureAwait(false);
    }

    private static void AppendIntDictionary(
        StringBuilder builder,
        string title,
        IReadOnlyDictionary<string, int> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            builder.AppendLine("- (none)");
            return;
        }

        builder.AppendLine("| Key | Count |");
        builder.AppendLine("|---|---:|");
        foreach (var item in values.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {item.Key} | {item.Value} |");
        }
    }

    private static string StableId(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
