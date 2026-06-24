using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Planning;

namespace ContextCore.Core.Services;

/// <summary>Router shadow trace 与离线 shadow eval 报告构建器。</summary>
public sealed class RouterIntentShadowReportBuilder
{
    public const string PolicyVersion = "router-intent-shadow-r2/v1";
    public const string DefaultOutputDirectory = "learning/router";
    public const string TraceQualityReportFileName = "router-shadow-trace-quality-report.json";
    public const string TraceQualityMarkdownFileName = "router-shadow-trace-quality-report.md";
    public const string ShadowEvalA3FileName = "router-intent-shadow-eval-a3.json";
    public const string ShadowEvalExtendedFileName = "router-intent-shadow-eval-extended.json";
    public const string ShadowEvalMarkdownFileName = "router-intent-shadow-eval.md";
    public const string TraceFileName = "router-shadow-traces.jsonl";

    private const double LowConfidenceThreshold = 0.35;
    private const int ReadyTraceThreshold = 30;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task<RouterShadowTraceQualityReport> RunTraceQualityAsync(
        string workspaceId,
        string collectionId,
        string outputDirectory,
        string? inputPath = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedOutput = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(resolvedOutput);
        var traces = await ReadJsonLinesAsync<RouterIntentShadowTrace>(
                ResolveTracePath(inputPath, resolvedOutput),
                cancellationToken)
            .ConfigureAwait(false);
        var report = BuildTraceQualityReport(
            traces
                .Where(trace => string.IsNullOrWhiteSpace(workspaceId)
                    || string.Equals(trace.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
                .Where(trace => string.IsNullOrWhiteSpace(collectionId)
                    || string.Equals(trace.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase))
                .ToArray(),
            workspaceId,
            collectionId);

        await WriteReportAsync(
                Path.Combine(resolvedOutput, TraceQualityReportFileName),
                report,
                cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(
                Path.Combine(resolvedOutput, TraceQualityMarkdownFileName),
                BuildTraceQualityMarkdownReport(report),
                cancellationToken)
            .ConfigureAwait(false);
        return report;
    }

    public async Task<(RouterIntentShadowEvalReport A3, RouterIntentShadowEvalReport Extended)> RunShadowEvalAsync(
        string inputPath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var resolvedInput = ResolveExamplesPath(inputPath);
        var resolvedOutput = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(resolvedOutput);
        var examples = await ReadJsonLinesAsync<ContextPolicyFeatureExample>(resolvedInput, cancellationToken)
            .ConfigureAwait(false);

        var a3 = BuildShadowEvalReport(examples, "A3", resolvedInput);
        var extended = BuildShadowEvalReport(examples, "Extended", resolvedInput);

        await WriteReportAsync(Path.Combine(resolvedOutput, ShadowEvalA3FileName), a3, cancellationToken)
            .ConfigureAwait(false);
        await WriteReportAsync(Path.Combine(resolvedOutput, ShadowEvalExtendedFileName), extended, cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(
                Path.Combine(resolvedOutput, ShadowEvalMarkdownFileName),
                BuildShadowEvalMarkdownReport(a3, extended),
                cancellationToken)
            .ConfigureAwait(false);

        return (a3, extended);
    }

    public RouterShadowTraceQualityReport BuildTraceQualityReport(
        IReadOnlyList<RouterIntentShadowTrace> traces,
        string? workspaceId = null,
        string? collectionId = null)
    {
        ArgumentNullException.ThrowIfNull(traces);
        var rows = traces.ToArray();
        var disagreementCount = rows.Count(static trace => !trace.Agreement);
        return new RouterShadowTraceQualityReport
        {
            OperationId = $"router-shadow-trace-quality-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            WorkspaceId = ResolveWorkspace(rows, workspaceId),
            CollectionId = ResolveCollection(rows, collectionId),
            TraceCount = rows.Length,
            AgreementRate = Ratio(rows.Count(static trace => trace.Agreement), rows.Length),
            DisagreementRate = Ratio(disagreementCount, rows.Length),
            LowConfidenceCount = rows.Count(static trace => trace.LowConfidence),
            AbstainCount = rows.Count(static trace => trace.Abstained),
            DisagreementByIntent = CountBy(rows.Where(static trace => !trace.Agreement), static trace => trace.RuntimeIntent),
            LowConfidenceByIntent = CountBy(rows.Where(static trace => trace.LowConfidence), static trace => trace.RuntimeIntent),
            TopConfusionPairs = BuildConfusionPairs(rows),
            Recommendation = RecommendTrace(rows.Length, disagreementCount, rows.Count(static trace => trace.LowConfidence)),
            PolicyVersion = PolicyVersion
        };
    }

    public RouterIntentShadowEvalReport BuildShadowEvalReport(
        IReadOnlyList<ContextPolicyFeatureExample> sourceExamples,
        string datasetName,
        string inputPath)
    {
        ArgumentNullException.ThrowIfNull(sourceExamples);
        var examples = sourceExamples
            .Where(static example => string.Equals(example.TaskKind, "RouterIntent", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(example.TaskKind))
            .Where(static example => !string.IsNullOrWhiteSpace(RouterIntentEvaluationRunner.GetIntentLabel(example)))
            .ToArray();
        if (examples.Length == 0)
        {
            return new RouterIntentShadowEvalReport
            {
                OperationId = $"router-intent-shadow-eval-{Guid.NewGuid():N}",
                GeneratedAt = DateTimeOffset.UtcNow,
                DatasetName = datasetName,
                InputPath = inputPath,
                Recommendation = RouterShadowRecommendations.NeedsMoreExamples,
                PolicyVersion = PolicyVersion
            };
        }

        var split = SplitExamples(examples);
        var runtime = new ExistingRuleBasedRouterBaseline();
        var shadow = new TokenCentroidRouterBaseline();
        runtime.Fit(split.TrainExamples);
        shadow.Fit(split.TrainExamples);
        var test = split.TestExamples.Length == 0 ? examples : split.TestExamples;
        var samples = test.Select(example => EvaluateSample(example, runtime, shadow)).ToArray();
        var disagreementCount = samples.Count(static sample => !sample.Agreement);
        var lowConfidenceCount = samples.Count(static sample => sample.ShadowConfidence < LowConfidenceThreshold);
        var abstainCount = samples.Count(static sample => string.Equals(
            sample.DisagreementType,
            RouterIntentShadowDisagreementTypes.ShadowAbstained,
            StringComparison.OrdinalIgnoreCase));
        var fixes = samples.Count(static sample => !sample.RuntimeCorrect && sample.ShadowCorrect);
        var breaks = samples.Count(static sample => sample.RuntimeCorrect && !sample.ShadowCorrect);

        return new RouterIntentShadowEvalReport
        {
            OperationId = $"router-intent-shadow-eval-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            DatasetName = datasetName,
            InputPath = inputPath,
            SampleCount = samples.Length,
            AgreementRate = Ratio(samples.Count(static sample => sample.Agreement), samples.Length),
            DisagreementRate = Ratio(disagreementCount, samples.Length),
            LowConfidenceCount = lowConfidenceCount,
            AbstainCount = abstainCount,
            DisagreementByIntent = CountBy(samples.Where(static sample => !sample.Agreement), static sample => sample.RuntimeIntent),
            LowConfidenceByIntent = CountBy(samples.Where(static sample => sample.ShadowConfidence < LowConfidenceThreshold), static sample => sample.RuntimeIntent),
            TopConfusionPairs = BuildEvalConfusionPairs(samples),
            ShadowFixesRuntime = fixes,
            ShadowBreaksRuntime = breaks,
            NetGain = fixes - breaks,
            PerIntentGain = BuildPerIntentGain(samples),
            PerIntentRegression = BuildPerIntentRegression(samples),
            Samples = samples.Take(100).ToArray(),
            Recommendation = RecommendEval(samples.Length, fixes, breaks, lowConfidenceCount),
            PolicyVersion = PolicyVersion
        };
    }

    public static string BuildTraceQualityMarkdownReport(RouterShadowTraceQualityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine("# Router Shadow Trace Quality Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.GeneratedAt:O}");
        builder.AppendLine($"PolicyVersion: `{report.PolicyVersion}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- Workspace: `{Empty(report.WorkspaceId)}`");
        builder.AppendLine($"- Collection: `{Empty(report.CollectionId)}`");
        builder.AppendLine($"- TraceCount: `{report.TraceCount}`");
        builder.AppendLine($"- AgreementRate: `{FormatPercent(report.AgreementRate)}`");
        builder.AppendLine($"- DisagreementRate: `{FormatPercent(report.DisagreementRate)}`");
        builder.AppendLine($"- LowConfidenceCount: `{report.LowConfidenceCount}`");
        builder.AppendLine($"- AbstainCount: `{report.AbstainCount}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        AppendCounts(builder, "Top Confusion Pairs", report.TopConfusionPairs);
        AppendCounts(builder, "Disagreement By Intent", report.DisagreementByIntent);
        AppendCounts(builder, "Low Confidence By Intent", report.LowConfidenceByIntent);
        return builder.ToString();
    }

    public static string BuildShadowEvalMarkdownReport(
        RouterIntentShadowEvalReport a3,
        RouterIntentShadowEvalReport extended)
    {
        ArgumentNullException.ThrowIfNull(a3);
        ArgumentNullException.ThrowIfNull(extended);
        var builder = new StringBuilder();
        builder.AppendLine("# Router Intent Shadow Eval Report");
        builder.AppendLine();
        builder.AppendLine("| Dataset | Samples | Agreement | Disagreement | LowConfidence | Abstain | Fixes | Breaks | NetGain | Recommendation |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var report in new[] { a3, extended })
        {
            builder.AppendLine($"| `{report.DatasetName}` | {report.SampleCount} | {FormatPercent(report.AgreementRate)} | {FormatPercent(report.DisagreementRate)} | {report.LowConfidenceCount} | {report.AbstainCount} | {report.ShadowFixesRuntime} | {report.ShadowBreaksRuntime} | {report.NetGain} | `{report.Recommendation}` |");
        }

        builder.AppendLine();
        builder.AppendLine("## Top Confusion Pairs");
        builder.AppendLine();
        AppendCounts(builder, "A3", a3.TopConfusionPairs);
        AppendCounts(builder, "Extended", extended.TopConfusionPairs);
        builder.AppendLine("## Runtime Safety");
        builder.AppendLine();
        builder.AppendLine("- Shadow eval is offline-only.");
        builder.AppendLine("- It does not replace the runtime router.");
        builder.AppendLine("- It does not change retrieval, planning, PackingPolicy, scoring, or package output.");
        return builder.ToString();
    }

    private static RouterIntentShadowEvalSample EvaluateSample(
        ContextPolicyFeatureExample example,
        RouterIntentClassifier runtime,
        RouterIntentClassifier shadow)
    {
        var actual = NormalizeIntent(RouterIntentEvaluationRunner.GetIntentLabel(example));
        var runtimePrediction = runtime.Predict(example);
        var shadowPrediction = shadow.Predict(example);
        var runtimeIntent = NormalizeIntent(runtimePrediction.Intent);
        var shadowIntent = NormalizeIntent(shadowPrediction.Intent);
        var agreement = string.Equals(runtimeIntent, shadowIntent, StringComparison.OrdinalIgnoreCase);
        var lowConfidence = shadowPrediction.Confidence < LowConfidenceThreshold;

        return new RouterIntentShadowEvalSample
        {
            ExampleId = example.ExampleId,
            Mode = example.Mode,
            ActualIntent = actual,
            RuntimeIntent = runtimeIntent,
            ShadowIntent = shadowIntent,
            ShadowConfidence = Math.Clamp(shadowPrediction.Confidence, 0, 1),
            Agreement = agreement,
            RuntimeCorrect = string.Equals(actual, runtimeIntent, StringComparison.OrdinalIgnoreCase),
            ShadowCorrect = string.Equals(actual, shadowIntent, StringComparison.OrdinalIgnoreCase),
            DisagreementType = agreement
                ? RouterIntentShadowDisagreementTypes.Agreement
                : shadowPrediction.Abstained
                    ? RouterIntentShadowDisagreementTypes.ShadowAbstained
                    : lowConfidence
                        ? RouterIntentShadowDisagreementTypes.LowConfidenceDisagreement
                        : RouterIntentShadowDisagreementTypes.IntentMismatch
        };
    }

    private static IReadOnlyDictionary<string, int> BuildPerIntentGain(
        IReadOnlyList<RouterIntentShadowEvalSample> samples)
    {
        return CountBy(
            samples.Where(static sample => !sample.RuntimeCorrect && sample.ShadowCorrect),
            static sample => sample.ActualIntent);
    }

    private static IReadOnlyDictionary<string, int> BuildPerIntentRegression(
        IReadOnlyList<RouterIntentShadowEvalSample> samples)
    {
        return CountBy(
            samples.Where(static sample => sample.RuntimeCorrect && !sample.ShadowCorrect),
            static sample => sample.ActualIntent);
    }

    private static IReadOnlyDictionary<string, int> BuildConfusionPairs(
        IReadOnlyList<RouterIntentShadowTrace> traces)
    {
        return CountBy(
            traces.Where(static trace => !trace.Agreement),
            static trace => $"{NormalizeIntent(trace.RuntimeIntent)}->{NormalizeIntent(trace.ShadowIntent)}");
    }

    private static IReadOnlyDictionary<string, int> BuildEvalConfusionPairs(
        IReadOnlyList<RouterIntentShadowEvalSample> samples)
    {
        return CountBy(
            samples.Where(static sample => !sample.Agreement),
            static sample => $"{NormalizeIntent(sample.RuntimeIntent)}->{NormalizeIntent(sample.ShadowIntent)}");
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

    private static string RecommendTrace(int traceCount, int disagreementCount, int lowConfidenceCount)
    {
        if (traceCount < ReadyTraceThreshold)
        {
            return RouterShadowRecommendations.NeedsMoreRealTraces;
        }

        if (lowConfidenceCount > traceCount / 2)
        {
            return RouterShadowRecommendations.BlockedByLowRecall;
        }

        if (disagreementCount > traceCount / 3)
        {
            return RouterShadowRecommendations.NeedsIntentBoundaryClarification;
        }

        return RouterShadowRecommendations.ReadyForRouterShadow;
    }

    private static string RecommendEval(int sampleCount, int fixes, int breaks, int lowConfidenceCount)
    {
        if (sampleCount < 20)
        {
            return RouterShadowRecommendations.NeedsMoreExamples;
        }

        if (lowConfidenceCount > sampleCount / 2)
        {
            return RouterShadowRecommendations.BlockedByLowRecall;
        }

        if (fixes >= breaks)
        {
            return RouterShadowRecommendations.ReadyForRouterShadow;
        }

        return RouterShadowRecommendations.KeepRuleBased;
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

        var train = trainGroups.SelectMany(static group => group).ToArray();
        var test = testGroups.SelectMany(static group => group).ToArray();
        return new RouterIntentSplit(train, test);
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

    private static async Task WriteReportAsync<T>(
        string path,
        T report,
        CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, JsonOptions), cancellationToken)
            .ConfigureAwait(false);
    }

    private static string ResolveExamplesPath(string path)
    {
        return Path.GetFullPath(string.IsNullOrWhiteSpace(path)
            ? Path.Combine(
                LearningDatasetQualityReportBuilder.DefaultFeatureDirectory,
                LearningDatasetQualityReportBuilder.RouterIntentExamplesFileName)
            : path);
    }

    private static string ResolveTracePath(string? inputPath, string outputDirectory)
    {
        return Path.GetFullPath(string.IsNullOrWhiteSpace(inputPath)
            ? Path.Combine(outputDirectory, TraceFileName)
            : inputPath);
    }

    private static string ResolveWorkspace(
        IReadOnlyList<RouterIntentShadowTrace> traces,
        string? fallback)
    {
        return traces.Select(static trace => trace.WorkspaceId)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
            ?? fallback
            ?? string.Empty;
    }

    private static string ResolveCollection(
        IReadOnlyList<RouterIntentShadowTrace> traces,
        string? fallback)
    {
        return traces.Select(static trace => trace.CollectionId)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
            ?? fallback
            ?? string.Empty;
    }

    private static string NormalizeIntent(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? PlanningIntentDetector.FuzzyQuestion
            : value.Trim();
    }

    private static double Ratio(int count, int total)
    {
        return total == 0 ? 0 : (double)count / total;
    }

    private static string FormatPercent(double value)
    {
        return value.ToString("P2", CultureInfo.InvariantCulture);
    }

    private static string Empty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();
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

    private sealed record RouterIntentSplit(
        ContextPolicyFeatureExample[] TrainExamples,
        ContextPolicyFeatureExample[] TestExamples);
}
