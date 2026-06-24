using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Planning;

namespace ContextCore.Core.Services;

/// <summary>Router disagreement 离线分诊与 hard negative 导出；不参与 runtime router 决策。</summary>
public sealed class RouterDisagreementTriageRunner
{
    public const string PolicyVersion = "router-disagreement-triage-r2.1/v1";
    public const string DefaultOutputDirectory = "learning/router";
    public const string A3ReportFileName = "router-disagreement-triage-a3.json";
    public const string ExtendedReportFileName = "router-disagreement-triage-extended.json";
    public const string MarkdownReportFileName = "router-disagreement-triage.md";
    public const string HardNegativesFileName = "router-hard-negatives.jsonl";

    private const double LowConfidenceThreshold = 0.35;
    private const double AmbiguousMarginThreshold = 0.08;
    private const int SparseIntentExampleThreshold = 3;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions JsonLineOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<(RouterDisagreementTriageReport A3, RouterDisagreementTriageReport Extended)> RunAsync(
        string inputPath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var resolvedInput = ResolveExamplesPath(inputPath);
        var resolvedOutput = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(resolvedOutput);

        var examples = await ReadJsonLinesAsync<ContextPolicyFeatureExample>(resolvedInput, cancellationToken)
            .ConfigureAwait(false);
        var a3 = BuildReport(examples, "A3", resolvedInput);
        var extended = BuildReport(examples, "Extended", resolvedInput);
        var hardNegatives = BuildHardNegatives(a3.Disagreements.Concat(extended.Disagreements));

        await File.WriteAllTextAsync(
                Path.Combine(resolvedOutput, A3ReportFileName),
                JsonSerializer.Serialize(a3, JsonOptions),
                cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(
                Path.Combine(resolvedOutput, ExtendedReportFileName),
                JsonSerializer.Serialize(extended, JsonOptions),
                cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(
                Path.Combine(resolvedOutput, MarkdownReportFileName),
                BuildMarkdownReport(a3, extended),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteJsonLinesAsync(
                Path.Combine(resolvedOutput, HardNegativesFileName),
                hardNegatives,
                cancellationToken)
            .ConfigureAwait(false);

        return (a3, extended);
    }

    public RouterDisagreementTriageReport BuildReport(
        IReadOnlyList<ContextPolicyFeatureExample> sourceExamples,
        string datasetName,
        string inputPath = "")
    {
        ArgumentNullException.ThrowIfNull(sourceExamples);

        var examples = sourceExamples
            .Where(static example => string.Equals(example.TaskKind, "RouterIntent", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(example.TaskKind))
            .Where(static example => !string.IsNullOrWhiteSpace(RouterIntentEvaluationRunner.GetIntentLabel(example)))
            .ToArray();
        if (examples.Length == 0)
        {
            return new RouterDisagreementTriageReport
            {
                OperationId = $"router-disagreement-triage-{Guid.NewGuid():N}",
                GeneratedAt = DateTimeOffset.UtcNow,
                DatasetName = datasetName,
                InputPath = inputPath,
                Recommendation = RouterDisagreementTriageRecommendations.NeedsMoreExamples,
                PolicyVersion = PolicyVersion
            };
        }

        var split = SplitExamples(examples);
        var trainExamples = split.TrainExamples.Length == 0 ? examples : split.TrainExamples;
        var testExamples = split.TestExamples.Length == 0 ? examples : split.TestExamples;
        var labelCounts = trainExamples
            .GroupBy(RouterIntentEvaluationRunner.GetIntentLabel, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => NormalizeIntent(group.Key), static group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var runtime = new ExistingRuleBasedRouterBaseline();
        var shadow = new TokenCentroidRouterBaseline();
        runtime.Fit(trainExamples);
        shadow.Fit(trainExamples);

        var details = testExamples
            .Select(example => EvaluateDisagreement(example, runtime, shadow, labelCounts))
            .Where(detail => detail is not null)
            .Cast<RouterDisagreementTriageDetail>()
            .ToArray();
        var hardNegatives = BuildHardNegatives(details);

        return new RouterDisagreementTriageReport
        {
            OperationId = $"router-disagreement-triage-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            DatasetName = datasetName,
            InputPath = inputPath,
            SampleCount = testExamples.Length,
            DisagreementCount = details.Length,
            ShadowFixesRuntime = details.Count(static detail => detail.ShadowFixesRuntime),
            ShadowBreaksRuntime = details.Count(static detail => detail.ShadowBreaksRuntime),
            BothWrongCount = details.Count(static detail => !detail.RuntimeCorrect && !detail.ShadowCorrect),
            LowConfidenceCount = details.Count(static detail => detail.Confidence < LowConfidenceThreshold),
            HardNegativeCount = hardNegatives.Count,
            TopConfusionPairs = CountBy(details, static detail => $"{detail.RuntimeIntent}->{detail.ShadowIntent}"),
            TriageCategoryCounts = CountBy(details, static detail => detail.TriageCategory),
            Disagreements = details,
            Recommendation = Recommend(details, testExamples.Length),
            PolicyVersion = PolicyVersion
        };
    }

    public static IReadOnlyList<RouterHardNegativeExample> BuildHardNegatives(
        IEnumerable<RouterDisagreementTriageDetail> disagreements)
    {
        ArgumentNullException.ThrowIfNull(disagreements);

        var results = new List<RouterHardNegativeExample>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var detail in disagreements)
        {
            AddIfNegative(results, seen, detail, detail.RuntimeIntent);
            AddIfNegative(results, seen, detail, detail.ShadowIntent);
        }

        return results;
    }

    public static string BuildMarkdownReport(
        RouterDisagreementTriageReport a3,
        RouterDisagreementTriageReport extended)
    {
        ArgumentNullException.ThrowIfNull(a3);
        ArgumentNullException.ThrowIfNull(extended);

        var builder = new StringBuilder();
        builder.AppendLine("# Router Disagreement Triage Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {DateTimeOffset.UtcNow:O}");
        builder.AppendLine($"PolicyVersion: `{PolicyVersion}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine("| Dataset | Samples | Disagreements | Fixes | Breaks | BothWrong | LowConfidence | HardNegatives | Recommendation |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var report in new[] { a3, extended })
        {
            builder.AppendLine($"| `{report.DatasetName}` | {report.SampleCount} | {report.DisagreementCount} | {report.ShadowFixesRuntime} | {report.ShadowBreaksRuntime} | {report.BothWrongCount} | {report.LowConfidenceCount} | {report.HardNegativeCount} | `{report.Recommendation}` |");
        }

        builder.AppendLine();
        AppendCounts(builder, "A3 Top Confusion Pairs", a3.TopConfusionPairs);
        AppendCounts(builder, "Extended Top Confusion Pairs", extended.TopConfusionPairs);
        AppendCounts(builder, "A3 Triage Categories", a3.TriageCategoryCounts);
        AppendCounts(builder, "Extended Triage Categories", extended.TriageCategoryCounts);
        AppendDetails(builder, "A3 Disagreements", a3.Disagreements);
        AppendDetails(builder, "Extended Disagreements", extended.Disagreements);

        builder.AppendLine("## Runtime Safety");
        builder.AppendLine();
        builder.AppendLine("- This report is offline-only.");
        builder.AppendLine("- It does not replace the runtime router.");
        builder.AppendLine("- It does not change retrieval, planning, PackingPolicy, scoring, or package output.");
        builder.AppendLine("- Hard negatives are exported as analysis data only.");
        return builder.ToString();
    }

    private static RouterDisagreementTriageDetail? EvaluateDisagreement(
        ContextPolicyFeatureExample example,
        RouterIntentClassifier runtime,
        RouterIntentClassifier shadow,
        IReadOnlyDictionary<string, int> labelCounts)
    {
        var expectedIntent = NormalizeIntent(RouterIntentEvaluationRunner.GetIntentLabel(example));
        var runtimePrediction = runtime.Predict(example);
        var shadowPrediction = shadow.Predict(example);
        var runtimeIntent = NormalizeIntent(runtimePrediction.Intent);
        var shadowIntent = NormalizeIntent(shadowPrediction.Intent);
        if (string.Equals(runtimeIntent, shadowIntent, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var topPredictions = ResolveTopPredictions(shadowPrediction);
        var runtimeCorrect = string.Equals(expectedIntent, runtimeIntent, StringComparison.OrdinalIgnoreCase);
        var shadowCorrect = string.Equals(expectedIntent, shadowIntent, StringComparison.OrdinalIgnoreCase);
        var lowConfidence = shadowPrediction.Confidence < LowConfidenceThreshold;
        var disagreementType = shadowPrediction.Abstained
            ? RouterIntentShadowDisagreementTypes.ShadowAbstained
            : lowConfidence
                ? RouterIntentShadowDisagreementTypes.LowConfidenceDisagreement
                : RouterIntentShadowDisagreementTypes.IntentMismatch;
        var category = ResolveCategory(
            expectedIntent,
            runtimeIntent,
            shadowIntent,
            runtimeCorrect,
            shadowCorrect,
            shadowPrediction.Confidence,
            topPredictions,
            labelCounts);

        return new RouterDisagreementTriageDetail
        {
            SampleId = ResolveSampleId(example),
            QueryText = ResolveQueryText(example),
            Mode = example.Mode,
            ExpectedIntent = expectedIntent,
            RuntimeIntent = runtimeIntent,
            ShadowIntent = shadowIntent,
            RuntimeCorrect = runtimeCorrect,
            ShadowCorrect = shadowCorrect,
            ShadowFixesRuntime = !runtimeCorrect && shadowCorrect,
            ShadowBreaksRuntime = runtimeCorrect && !shadowCorrect,
            Confidence = Math.Clamp(shadowPrediction.Confidence, 0, 1),
            TopPredictions = topPredictions,
            DisagreementType = disagreementType,
            TriageCategory = category,
            RecommendedAction = ResolveRecommendedAction(category)
        };
    }

    private static string ResolveCategory(
        string expectedIntent,
        string runtimeIntent,
        string shadowIntent,
        bool runtimeCorrect,
        bool shadowCorrect,
        double confidence,
        IReadOnlyList<RouterIntentShadowTopPrediction> topPredictions,
        IReadOnlyDictionary<string, int> labelCounts)
    {
        if (!runtimeCorrect && shadowCorrect)
        {
            return RouterDisagreementTriageCategories.ShadowFixesRuntime;
        }

        if (runtimeCorrect && !shadowCorrect)
        {
            return RouterDisagreementTriageCategories.ShadowBreaksRuntime;
        }

        if (!runtimeCorrect && !shadowCorrect)
        {
            return RouterDisagreementTriageCategories.BothWrong;
        }

        if (confidence < LowConfidenceThreshold)
        {
            return RouterDisagreementTriageCategories.LowConfidenceCentroid;
        }

        if (IsSparseIntent(expectedIntent, labelCounts)
            || IsSparseIntent(runtimeIntent, labelCounts)
            || IsSparseIntent(shadowIntent, labelCounts))
        {
            return RouterDisagreementTriageCategories.SparseIntentExamples;
        }

        if (IsBoundaryAmbiguous(topPredictions))
        {
            return RouterDisagreementTriageCategories.IntentBoundaryAmbiguous;
        }

        return RouterDisagreementTriageCategories.NeedsHardNegative;
    }

    private static string ResolveRecommendedAction(string category)
    {
        return category switch
        {
            RouterDisagreementTriageCategories.ShadowFixesRuntime => RouterDisagreementRecommendedActions.ReviewRuntimeBoundary,
            RouterDisagreementTriageCategories.ShadowBreaksRuntime => RouterDisagreementRecommendedActions.AddHardNegative,
            RouterDisagreementTriageCategories.BothWrong => RouterDisagreementRecommendedActions.ClarifyIntentDefinition,
            RouterDisagreementTriageCategories.LowConfidenceCentroid => RouterDisagreementRecommendedActions.CollectMoreExamples,
            RouterDisagreementTriageCategories.SparseIntentExamples => RouterDisagreementRecommendedActions.CollectMoreExamples,
            RouterDisagreementTriageCategories.IntentBoundaryAmbiguous => RouterDisagreementRecommendedActions.ClarifyIntentDefinition,
            RouterDisagreementTriageCategories.NeedsHardNegative => RouterDisagreementRecommendedActions.AddHardNegative,
            RouterDisagreementTriageCategories.NeedsIntentDefinition => RouterDisagreementRecommendedActions.ClarifyIntentDefinition,
            _ => RouterDisagreementRecommendedActions.KeepRuleBased
        };
    }

    private static string Recommend(IReadOnlyList<RouterDisagreementTriageDetail> details, int sampleCount)
    {
        if (sampleCount < 20)
        {
            return RouterDisagreementTriageRecommendations.NeedsMoreExamples;
        }

        if (details.Count == 0)
        {
            return RouterDisagreementTriageRecommendations.ReadyForRouterShadow;
        }

        var breaks = details.Count(static detail => detail.ShadowBreaksRuntime);
        var fixes = details.Count(static detail => detail.ShadowFixesRuntime);
        if (breaks > fixes)
        {
            return RouterDisagreementTriageRecommendations.KeepRuleBased;
        }

        if (details.Any(static detail => string.Equals(
            detail.TriageCategory,
            RouterDisagreementTriageCategories.IntentBoundaryAmbiguous,
            StringComparison.OrdinalIgnoreCase)))
        {
            return RouterDisagreementTriageRecommendations.NeedsIntentBoundaryClarification;
        }

        return RouterDisagreementTriageRecommendations.NeedsHardNegativeDataset;
    }

    private static void AddIfNegative(
        List<RouterHardNegativeExample> results,
        HashSet<string> seen,
        RouterDisagreementTriageDetail detail,
        string negativeIntent)
    {
        var positiveIntent = NormalizeIntent(detail.ExpectedIntent);
        var normalizedNegative = NormalizeIntent(negativeIntent);
        if (string.Equals(positiveIntent, normalizedNegative, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var key = $"{detail.QueryText}|{detail.Mode}|{positiveIntent}|{normalizedNegative}|{detail.SampleId}";
        if (!seen.Add(key))
        {
            return;
        }

        results.Add(new RouterHardNegativeExample
        {
            QueryText = detail.QueryText,
            PositiveIntent = positiveIntent,
            NegativeIntent = normalizedNegative,
            Mode = detail.Mode,
            Reason = detail.RecommendedAction,
            SourceSampleId = detail.SampleId,
            Confidence = Math.Clamp(detail.Confidence, 0, 1)
        });
    }

    private static IReadOnlyList<RouterIntentShadowTopPrediction> ResolveTopPredictions(
        RouterIntentClassifierPrediction prediction)
    {
        if (prediction.TopPredictions.Count > 0)
        {
            return prediction.TopPredictions;
        }

        return
        [
            new RouterIntentShadowTopPrediction
            {
                Intent = NormalizeIntent(prediction.Intent),
                Confidence = Math.Clamp(prediction.Confidence, 0, 1),
                Reason = prediction.Reasons.FirstOrDefault() ?? string.Empty
            }
        ];
    }

    private static bool IsSparseIntent(
        string intent,
        IReadOnlyDictionary<string, int> labelCounts)
    {
        return labelCounts.TryGetValue(NormalizeIntent(intent), out var count)
            && count > 0
            && count < SparseIntentExampleThreshold;
    }

    private static bool IsBoundaryAmbiguous(IReadOnlyList<RouterIntentShadowTopPrediction> topPredictions)
    {
        if (topPredictions.Count < 2)
        {
            return false;
        }

        var ordered = topPredictions
            .OrderByDescending(static item => item.Confidence)
            .ToArray();
        return Math.Abs(ordered[0].Confidence - ordered[1].Confidence) <= AmbiguousMarginThreshold;
    }

    private static string ResolveSampleId(ContextPolicyFeatureExample example)
    {
        return string.IsNullOrWhiteSpace(example.SourceId)
            ? example.ExampleId
            : example.SourceId;
    }

    private static string ResolveQueryText(ContextPolicyFeatureExample example)
    {
        if (example.Metadata.TryGetValue("currentInput", out var currentInput)
            && !string.IsNullOrWhiteSpace(currentInput))
        {
            return currentInput.Trim();
        }

        if (example.Metadata.TryGetValue("queryText", out var queryText)
            && !string.IsNullOrWhiteSpace(queryText))
        {
            return queryText.Trim();
        }

        return string.IsNullOrWhiteSpace(example.InputSummary)
            ? example.Mode
            : example.InputSummary.Trim();
    }

    private static IReadOnlyDictionary<string, int> CountBy<T>(
        IEnumerable<T> rows,
        Func<T, string?> keySelector)
    {
        return rows
            .Select(row => NormalizeIntent(keySelector(row)))
            .GroupBy(static key => key, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);
    }

    private static RouterIntentSplit SplitExamples(IReadOnlyList<ContextPolicyFeatureExample> examples)
    {
        var groups = examples
            .GroupBy(GetGroupKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => StableBucket(group.Key))
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var trainGroups = new List<IGrouping<string, ContextPolicyFeatureExample>>();
        var testGroups = new List<IGrouping<string, ContextPolicyFeatureExample>>();
        foreach (var group in groups)
        {
            if (StableBucket(group.Key) % 100 < 80)
            {
                trainGroups.Add(group);
            }
            else
            {
                testGroups.Add(group);
            }
        }

        if (testGroups.Count == 0 && trainGroups.Count > 1)
        {
            testGroups.Add(trainGroups[^1]);
            trainGroups.RemoveAt(trainGroups.Count - 1);
        }

        if (trainGroups.Count == 0 && testGroups.Count > 1)
        {
            trainGroups.Add(testGroups[0]);
            testGroups.RemoveAt(0);
        }

        return new RouterIntentSplit(
            trainGroups.SelectMany(static group => group).ToArray(),
            testGroups.SelectMany(static group => group).ToArray());
    }

    private static string GetGroupKey(ContextPolicyFeatureExample example)
    {
        var source = string.IsNullOrWhiteSpace(example.SourceId) ? example.ExampleId : example.SourceId;
        return $"{example.SourceType}|{source}";
    }

    private static int StableBucket(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Math.Abs(BitConverter.ToInt32(bytes, 0));
    }

    private static string ResolveExamplesPath(string path)
    {
        return Path.GetFullPath(string.IsNullOrWhiteSpace(path)
            ? Path.Combine(
                LearningDatasetQualityReportBuilder.DefaultFeatureDirectory,
                LearningDatasetQualityReportBuilder.RouterIntentExamplesFileName)
            : path);
    }

    private static async Task<IReadOnlyList<T>> ReadJsonLinesAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return Array.Empty<T>();
        }

        var rows = new List<T>();
        foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var item = JsonSerializer.Deserialize<T>(line, JsonOptions);
            if (item is not null)
            {
                rows.Add(item);
            }
        }

        return rows;
    }

    private static async Task WriteJsonLinesAsync<T>(
        string path,
        IReadOnlyList<T> rows,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            builder.AppendLine(JsonSerializer.Serialize(row, JsonLineOptions));
        }

        await File.WriteAllTextAsync(path, builder.ToString(), cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeIntent(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? PlanningIntentDetector.FuzzyQuestion
            : value.Trim();
    }

    private static void AppendCounts(
        StringBuilder builder,
        string title,
        IReadOnlyDictionary<string, int> counts)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        if (counts.Count == 0)
        {
            builder.AppendLine("- (empty)");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("| Key | Count |");
        builder.AppendLine("|---|---:|");
        foreach (var item in counts.OrderByDescending(static item => item.Value).ThenBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| `{item.Key}` | {item.Value} |");
        }

        builder.AppendLine();
    }

    private static void AppendDetails(
        StringBuilder builder,
        string title,
        IReadOnlyList<RouterDisagreementTriageDetail> details)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        if (details.Count == 0)
        {
            builder.AppendLine("- (empty)");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("| Sample | Mode | Expected | Runtime | Shadow | Confidence | Category | Action |");
        builder.AppendLine("|---|---|---|---|---|---:|---|---|");
        foreach (var detail in details.Take(50))
        {
            builder.AppendLine(FormattableString.Invariant(
                $"| `{detail.SampleId}` | `{detail.Mode}` | `{detail.ExpectedIntent}` | `{detail.RuntimeIntent}` | `{detail.ShadowIntent}` | {detail.Confidence:0.####} | `{detail.TriageCategory}` | `{detail.RecommendedAction}` |"));
        }

        builder.AppendLine();
    }

    private sealed record RouterIntentSplit(
        ContextPolicyFeatureExample[] TrainExamples,
        ContextPolicyFeatureExample[] TestExamples);
}
