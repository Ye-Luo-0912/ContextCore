using System.Globalization;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>Builds an offline learning dataset quality report from exported JSONL features.</summary>
public sealed class LearningDatasetQualityReportBuilder
{
    public const string PolicyVersion = "learning-dataset-quality/v1";

    public const string DefaultFeatureDirectory = "learning/features";
    public const string PolicyFeedbackFeaturesFileName = "policy-feedback-features.jsonl";
    public const string RankingPairsFileName = "ranking-pairs.jsonl";
    public const string RouterIntentExamplesFileName = "router-intent-examples.jsonl";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task<LearningDatasetQualityReport> BuildAsync(
        string featureDirectory = DefaultFeatureDirectory,
        CancellationToken cancellationToken = default)
    {
        var resolvedDirectory = ResolvePath(featureDirectory);
        var policyFeatures = await ReadJsonLinesAsync<ContextPolicyFeatureExample>(
            Path.Combine(resolvedDirectory, PolicyFeedbackFeaturesFileName),
            cancellationToken).ConfigureAwait(false);
        var rankingPairs = await ReadJsonLinesAsync<RankingPairExample>(
            Path.Combine(resolvedDirectory, RankingPairsFileName),
            cancellationToken).ConfigureAwait(false);
        var routerExamples = await ReadJsonLinesAsync<ContextPolicyFeatureExample>(
            Path.Combine(resolvedDirectory, RouterIntentExamplesFileName),
            cancellationToken).ConfigureAwait(false);

        return Build(policyFeatures, rankingPairs, routerExamples, resolvedDirectory);
    }

    public LearningDatasetQualityReport Build(
        IReadOnlyList<ContextPolicyFeatureExample> policyFeedbackFeatures,
        IReadOnlyList<RankingPairExample> rankingPairs,
        IReadOnlyList<ContextPolicyFeatureExample> routerIntentExamples,
        string featureDirectory = DefaultFeatureDirectory)
    {
        ArgumentNullException.ThrowIfNull(policyFeedbackFeatures);
        ArgumentNullException.ThrowIfNull(rankingPairs);
        ArgumentNullException.ThrowIfNull(routerIntentExamples);

        var positiveCount = policyFeedbackFeatures.Count(IsPositive);
        var negativeCount = policyFeedbackFeatures.Count(IsNegative);
        var neutralCount = policyFeedbackFeatures.Count(IsNeutral);
        var sourceTypeCounts = BuildSourceTypeCounts(policyFeedbackFeatures, rankingPairs, routerIntentExamples);
        var modeCounts = CountStrings(
            policyFeedbackFeatures.Select(item => item.Mode)
                .Concat(rankingPairs.Select(item => item.Mode))
                .Concat(routerIntentExamples.Select(item => item.Mode)));
        var intentCounts = CountStrings(
            policyFeedbackFeatures.Select(item => item.Intent)
                .Concat(rankingPairs.Select(item => item.Intent))
                .Concat(routerIntentExamples.Select(item => item.Intent)));
        var labelCounts = CountStrings(
            policyFeedbackFeatures.Select(item => item.Label)
                .Concat(routerIntentExamples.Select(item => item.Label)));
        var dataRisks = BuildDataRisks(
            policyFeedbackFeatures.Count,
            rankingPairs.Count,
            routerIntentExamples.Count,
            positiveCount,
            negativeCount,
            intentCounts.Count,
            modeCounts.Count);
        var taskReadiness = BuildTaskReadiness(
            policyFeedbackFeatures,
            rankingPairs,
            routerIntentExamples,
            positiveCount,
            negativeCount,
            intentCounts.Count,
            modeCounts.Count);

        return new LearningDatasetQualityReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            FeatureDirectory = ResolvePath(featureDirectory),
            PolicyFeedbackFeatureCount = policyFeedbackFeatures.Count,
            RankingPairCount = rankingPairs.Count,
            RouterIntentExampleCount = routerIntentExamples.Count,
            PositiveCount = positiveCount,
            NegativeCount = negativeCount,
            NeutralCount = neutralCount,
            SourceTypeCounts = sourceTypeCounts,
            ModeCounts = modeCounts,
            IntentCounts = intentCounts,
            LabelCounts = labelCounts,
            DataRisks = dataRisks,
            TaskReadiness = taskReadiness,
            RecommendedNextAction = BuildRecommendedNextAction(dataRisks, taskReadiness),
            PolicyVersion = PolicyVersion
        };
    }

    public async Task WriteAsync(
        LearningDatasetQualityReport report,
        string jsonPath,
        string markdownPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        await WriteTextAsync(jsonPath, JsonSerializer.Serialize(report, JsonOptions), cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(markdownPath, BuildMarkdownReport(report), cancellationToken)
            .ConfigureAwait(false);
    }

    public static string BuildMarkdownReport(LearningDatasetQualityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Learning Dataset Quality Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Feature directory: `{report.FeatureDirectory}`");
        builder.AppendLine($"Policy version: `{report.PolicyVersion}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- Policy feedback features: `{report.PolicyFeedbackFeatureCount}`");
        builder.AppendLine($"- Ranking pairs: `{report.RankingPairCount}`");
        builder.AppendLine($"- Router intent examples: `{report.RouterIntentExampleCount}`");
        builder.AppendLine($"- Positive / Negative / Neutral: `{report.PositiveCount}` / `{report.NegativeCount}` / `{report.NeutralCount}`");
        builder.AppendLine();
        builder.AppendLine("## Data Risks");
        builder.AppendLine();
        if (report.DataRisks.Count == 0)
        {
            builder.AppendLine("- (none)");
        }
        else
        {
            foreach (var risk in report.DataRisks)
            {
                builder.AppendLine($"- `{risk}`");
            }
        }

        AppendCounts(builder, "Source Type Counts", report.SourceTypeCounts);
        AppendCounts(builder, "Mode Counts", report.ModeCounts);
        AppendCounts(builder, "Intent Counts", report.IntentCounts);
        AppendCounts(builder, "Label Counts", report.LabelCounts);

        builder.AppendLine();
        builder.AppendLine("## Task Readiness");
        builder.AppendLine();
        builder.AppendLine("| Task | Status | Ready | Reasons | Next Action |");
        builder.AppendLine("|---|---|---:|---|---|");
        foreach (var readiness in report.TaskReadiness.Values.OrderBy(item => item.TaskName, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {readiness.TaskName} | {readiness.Status} | {(readiness.Ready ? "yes" : "no")} | {JoinInline(readiness.Reasons)} | {readiness.RecommendedNextAction} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Recommended Next Action");
        builder.AppendLine();
        builder.AppendLine(report.RecommendedNextAction);
        return builder.ToString();
    }

    private static async Task<IReadOnlyList<T>> ReadJsonLinesAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return Array.Empty<T>();
        }

        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        if (lines.Length == 0)
        {
            return Array.Empty<T>();
        }

        var records = new List<T>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var record = JsonSerializer.Deserialize<T>(line, JsonOptions);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        return records;
    }

    private static IReadOnlyDictionary<string, int> BuildSourceTypeCounts(
        IReadOnlyList<ContextPolicyFeatureExample> policyFeedbackFeatures,
        IReadOnlyList<RankingPairExample> rankingPairs,
        IReadOnlyList<ContextPolicyFeatureExample> routerIntentExamples)
    {
        var values = policyFeedbackFeatures.Select(item => item.SourceType)
            .Concat(routerIntentExamples.Select(item => item.SourceType))
            .Concat(rankingPairs.Select(static _ => "RankingPair"));
        return CountStrings(values);
    }

    private static IReadOnlyDictionary<string, int> CountStrings(IEnumerable<string> values)
    {
        return values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> BuildDataRisks(
        int policyFeatureCount,
        int rankingPairCount,
        int routerIntentExampleCount,
        int positiveCount,
        int negativeCount,
        int intentCount,
        int modeCount)
    {
        var risks = new List<string>();
        if (policyFeatureCount == 0)
        {
            risks.Add(LearningDatasetDataRisks.NoPolicyFeedback);
        }

        if (policyFeatureCount == 0 && (rankingPairCount > 0 || routerIntentExampleCount > 0))
        {
            risks.Add(LearningDatasetDataRisks.EvalOnlyDataset);
        }

        if (negativeCount == 0)
        {
            risks.Add(LearningDatasetDataRisks.MissingNegativeSamples);
        }

        var supervisedCount = positiveCount + negativeCount;
        if (supervisedCount > 0)
        {
            var minClass = Math.Min(positiveCount, negativeCount);
            var maxClass = Math.Max(positiveCount, negativeCount);
            if (minClass == 0 || maxClass / Math.Max(1.0, minClass) >= 4)
            {
                risks.Add(LearningDatasetDataRisks.ClassImbalance);
            }
        }

        if (intentCount < 4)
        {
            risks.Add(LearningDatasetDataRisks.LowIntentCoverage);
        }

        if (modeCount < 3)
        {
            risks.Add(LearningDatasetDataRisks.LowModeCoverage);
        }

        return risks.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyDictionary<string, LearningDatasetTaskReadiness> BuildTaskReadiness(
        IReadOnlyList<ContextPolicyFeatureExample> policyFeedbackFeatures,
        IReadOnlyList<RankingPairExample> rankingPairs,
        IReadOnlyList<ContextPolicyFeatureExample> routerIntentExamples,
        int positiveCount,
        int negativeCount,
        int intentCount,
        int modeCount)
    {
        var readiness = new Dictionary<string, LearningDatasetTaskReadiness>(StringComparer.OrdinalIgnoreCase)
        {
            [LearningDatasetTaskNames.RouterIntentClassifier] = BuildRouterReadiness(routerIntentExamples.Count, intentCount, modeCount),
            [LearningDatasetTaskNames.CandidateReranker] = BuildCandidateRerankerReadiness(rankingPairs.Count, modeCount),
            [LearningDatasetTaskNames.PromotionJudge] = BuildSourceJudgeReadiness(
                LearningDatasetTaskNames.PromotionJudge,
                policyFeedbackFeatures,
                positiveCount,
                negativeCount,
                static source => source.Contains("Promotion", StringComparison.OrdinalIgnoreCase),
                "Collect accepted and rejected promotion review examples."),
            [LearningDatasetTaskNames.ConstraintGapJudge] = BuildSourceJudgeReadiness(
                LearningDatasetTaskNames.ConstraintGapJudge,
                policyFeedbackFeatures,
                positiveCount,
                negativeCount,
                static source => source.Contains("ConstraintGap", StringComparison.OrdinalIgnoreCase)
                    || source.Contains("CandidateConstraint", StringComparison.OrdinalIgnoreCase),
                "Collect accepted and rejected constraint gap / candidate constraint review examples."),
            [LearningDatasetTaskNames.AttentionScorer] = BuildAttentionReadiness(policyFeedbackFeatures)
        };

        return readiness;
    }

    private static LearningDatasetTaskReadiness BuildRouterReadiness(
        int routerIntentExampleCount,
        int intentCount,
        int modeCount)
    {
        var reasons = new List<string>
        {
            $"routerIntentExamples={routerIntentExampleCount.ToString(CultureInfo.InvariantCulture)}",
            $"intents={intentCount.ToString(CultureInfo.InvariantCulture)}",
            $"modes={modeCount.ToString(CultureInfo.InvariantCulture)}"
        };

        if (routerIntentExampleCount >= 100 && intentCount >= 4 && modeCount >= 3)
        {
            return Ready(
                LearningDatasetTaskNames.RouterIntentClassifier,
                reasons,
                "Use only for offline router-intent analysis; keep online router disabled.");
        }

        if (routerIntentExampleCount >= 20)
        {
            return Limited(
                LearningDatasetTaskNames.RouterIntentClassifier,
                reasons,
                "Increase mode and intent coverage before classifier training.");
        }

        return NotReady(
            LearningDatasetTaskNames.RouterIntentClassifier,
            reasons,
            "Export more planning shadow router intent examples.");
    }

    private static LearningDatasetTaskReadiness BuildCandidateRerankerReadiness(
        int rankingPairCount,
        int modeCount)
    {
        var reasons = new List<string>
        {
            $"rankingPairs={rankingPairCount.ToString(CultureInfo.InvariantCulture)}",
            $"modes={modeCount.ToString(CultureInfo.InvariantCulture)}"
        };

        if (rankingPairCount >= 100 && modeCount >= 3)
        {
            return Ready(
                LearningDatasetTaskNames.CandidateReranker,
                reasons,
                "Use for offline reranker analysis only; do not change retrieval scoring.");
        }

        if (rankingPairCount > 0)
        {
            return Limited(
                LearningDatasetTaskNames.CandidateReranker,
                reasons,
                "Add more ranking pairs and mode coverage before model work.");
        }

        return NotReady(
            LearningDatasetTaskNames.CandidateReranker,
            reasons,
            "Generate eval-derived ranking pairs first.");
    }

    private static LearningDatasetTaskReadiness BuildSourceJudgeReadiness(
        string taskName,
        IReadOnlyList<ContextPolicyFeatureExample> policyFeedbackFeatures,
        int positiveCount,
        int negativeCount,
        Func<string, bool> sourcePredicate,
        string nextAction)
    {
        var sourceCount = policyFeedbackFeatures.Count(item => sourcePredicate(item.SourceType));
        var reasons = new List<string>
        {
            $"sourceExamples={sourceCount.ToString(CultureInfo.InvariantCulture)}",
            $"positive={positiveCount.ToString(CultureInfo.InvariantCulture)}",
            $"negative={negativeCount.ToString(CultureInfo.InvariantCulture)}"
        };

        if (sourceCount >= 20 && positiveCount > 0 && negativeCount > 0)
        {
            return Ready(taskName, reasons, "Use for offline judge dataset review only.");
        }

        if (sourceCount > 0)
        {
            return Limited(taskName, reasons, nextAction);
        }

        return NotReady(taskName, reasons, nextAction);
    }

    private static LearningDatasetTaskReadiness BuildAttentionReadiness(
        IReadOnlyList<ContextPolicyFeatureExample> policyFeedbackFeatures)
    {
        var sourceCount = policyFeedbackFeatures.Count(item =>
            item.SourceType.Contains("Attention", StringComparison.OrdinalIgnoreCase)
            || item.Metadata.Keys.Any(key => key.Contains("attention", StringComparison.OrdinalIgnoreCase)));
        var reasons = new List<string>
        {
            $"attentionFeedbackExamples={sourceCount.ToString(CultureInfo.InvariantCulture)}"
        };

        return sourceCount >= 20
            ? Ready(
                LearningDatasetTaskNames.AttentionScorer,
                reasons,
                "Use for offline attention scorer analysis only; keep attention default off.")
            : NotReady(
                LearningDatasetTaskNames.AttentionScorer,
                reasons,
                "Collect explicit attention order quality feedback before scorer work.");
    }

    private static LearningDatasetTaskReadiness Ready(
        string taskName,
        IReadOnlyList<string> reasons,
        string nextAction)
        => new()
        {
            TaskName = taskName,
            Ready = true,
            Status = LearningDatasetReadinessStatus.Ready,
            Reasons = reasons,
            RecommendedNextAction = nextAction
        };

    private static LearningDatasetTaskReadiness Limited(
        string taskName,
        IReadOnlyList<string> reasons,
        string nextAction)
        => new()
        {
            TaskName = taskName,
            Ready = false,
            Status = LearningDatasetReadinessStatus.Limited,
            Reasons = reasons,
            RecommendedNextAction = nextAction
        };

    private static LearningDatasetTaskReadiness NotReady(
        string taskName,
        IReadOnlyList<string> reasons,
        string nextAction)
        => new()
        {
            TaskName = taskName,
            Ready = false,
            Status = LearningDatasetReadinessStatus.NotReady,
            Reasons = reasons,
            RecommendedNextAction = nextAction
        };

    private static string BuildRecommendedNextAction(
        IReadOnlyList<string> risks,
        IReadOnlyDictionary<string, LearningDatasetTaskReadiness> taskReadiness)
    {
        if (risks.Contains(LearningDatasetDataRisks.NoPolicyFeedback, StringComparer.OrdinalIgnoreCase))
        {
            return "Collect and export human review feedback before PromotionJudge, ConstraintGapJudge, or AttentionScorer work.";
        }

        if (risks.Contains(LearningDatasetDataRisks.MissingNegativeSamples, StringComparer.OrdinalIgnoreCase))
        {
            return "Add rejected examples so judge datasets have negative labels.";
        }

        if (risks.Contains(LearningDatasetDataRisks.LowIntentCoverage, StringComparer.OrdinalIgnoreCase)
            || risks.Contains(LearningDatasetDataRisks.LowModeCoverage, StringComparer.OrdinalIgnoreCase))
        {
            return "Broaden planning shadow and eval exports before classifier or reranker tuning.";
        }

        if (taskReadiness.Values.Any(item => !item.Ready))
        {
            return "Keep this dataset in offline analysis mode and fill the not-ready task gaps.";
        }

        return "Dataset is ready for offline analysis gates; do not connect it to online policy without a separate opt-in phase.";
    }

    private static bool IsPositive(ContextPolicyFeatureExample example)
        => example.Accepted
            || string.Equals(example.Label, PolicyFeedbackLabels.Positive, StringComparison.OrdinalIgnoreCase);

    private static bool IsNegative(ContextPolicyFeatureExample example)
        => example.Rejected
            || string.Equals(example.Label, PolicyFeedbackLabels.Negative, StringComparison.OrdinalIgnoreCase);

    private static bool IsNeutral(ContextPolicyFeatureExample example)
        => !IsPositive(example)
            && !IsNegative(example)
            && string.Equals(example.Label, PolicyFeedbackLabels.Neutral, StringComparison.OrdinalIgnoreCase);

    private static string ResolvePath(string path)
    {
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
    }

    private static async Task WriteTextAsync(
        string path,
        string text,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(fullPath, text, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    private static void AppendCounts(
        StringBuilder builder,
        string title,
        IReadOnlyDictionary<string, int> counts)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        if (counts.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        builder.AppendLine("| Key | Count |");
        builder.AppendLine("|---|---:|");
        foreach (var pair in counts.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {pair.Key} | {pair.Value} |");
        }
    }

    private static string JoinInline(IReadOnlyList<string> values)
        => values.Count == 0 ? "-" : string.Join("; ", values);
}
