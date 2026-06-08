using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>Runs offline baselines over exported learning feature JSONL files.</summary>
public sealed partial class LearningOfflineBaselineRunner
{
    public const string PolicyVersion = "learning-offline-baseline/v1";

    public const string MajorityClassBaseline = "MajorityClassBaseline";
    public const string RuleBasedBaseline = "RuleBasedBaseline";
    public const string RuleScoreBaseline = "RuleScoreBaseline";
    public const string SimpleFeatureWeightedBaseline = "SimpleFeatureWeightedBaseline";
    public const string LifecycleAwareFeatureBaseline = "LifecycleAwareFeatureBaseline";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task<RouterIntentBaselineReport> RunRouterAsync(
        string inputPath,
        string jsonOutputPath,
        string markdownOutputPath,
        CancellationToken cancellationToken = default)
    {
        var resolvedInputPath = ResolvePath(inputPath);
        var examples = await ReadJsonLinesAsync<ContextPolicyFeatureExample>(resolvedInputPath, cancellationToken)
            .ConfigureAwait(false);
        var report = BuildRouterReport(examples, resolvedInputPath);
        await WriteReportAsync(report, BuildRouterMarkdownReport(report), jsonOutputPath, markdownOutputPath, cancellationToken)
            .ConfigureAwait(false);
        return report;
    }

    public async Task<RankerBaselineReport> RunRankerAsync(
        string inputPath,
        string jsonOutputPath,
        string markdownOutputPath,
        CancellationToken cancellationToken = default)
    {
        var resolvedInputPath = ResolvePath(inputPath);
        var pairs = await ReadJsonLinesAsync<RankingPairExample>(resolvedInputPath, cancellationToken)
            .ConfigureAwait(false);
        var report = BuildRankerReport(pairs, resolvedInputPath);
        await WriteReportAsync(report, BuildRankerMarkdownReport(report), jsonOutputPath, markdownOutputPath, cancellationToken)
            .ConfigureAwait(false);
        return report;
    }

    public RouterIntentBaselineReport BuildRouterReport(
        IReadOnlyList<ContextPolicyFeatureExample> examples,
        string inputPath = "")
    {
        ArgumentNullException.ThrowIfNull(examples);

        var notReadyReasons = new List<string>();
        if (examples.Count == 0)
        {
            notReadyReasons.Add("router-intent-examples is empty");
        }

        var split = SplitRouterExamples(examples);
        if (split.GroupCount < 2 && examples.Count > 0)
        {
            notReadyReasons.Add("less than two sample groups; grouped holdout split is not meaningful");
        }

        var baselineResults = examples.Count == 0
            ? Array.Empty<RouterIntentBaselineResult>()
            : new[]
            {
                EvaluateRouterBaseline(
                    MajorityClassBaseline,
                    split.Test,
                    example => PredictMajorityIntent(split.Train, example)),
                EvaluateRouterBaseline(
                    RuleBasedBaseline,
                    split.Test,
                    PredictRuleBasedIntent)
            };
        var best = baselineResults
            .OrderByDescending(item => item.Accuracy)
            .ThenByDescending(item => item.MacroF1)
            .FirstOrDefault();

        return new RouterIntentBaselineReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            InputPath = inputPath,
            SampleCount = examples.Count,
            Ready = notReadyReasons.Count == 0,
            Status = notReadyReasons.Count == 0 ? LearningDatasetReadinessStatus.Ready : LearningDatasetReadinessStatus.NotReady,
            NotReadyReasons = notReadyReasons,
            Split = split.Summary,
            Baselines = baselineResults,
            BestBaseline = best?.BaselineName ?? string.Empty,
            PolicyVersion = PolicyVersion
        };
    }

    public RankerBaselineReport BuildRankerReport(
        IReadOnlyList<RankingPairExample> pairs,
        string inputPath = "")
    {
        ArgumentNullException.ThrowIfNull(pairs);

        var notReadyReasons = new List<string>();
        if (pairs.Count == 0)
        {
            notReadyReasons.Add("ranking-pairs is empty");
        }

        var split = SplitRankingPairs(pairs);
        if (split.GroupCount < 2 && pairs.Count > 0)
        {
            notReadyReasons.Add("less than two sample groups; grouped holdout split is not meaningful");
        }

        var rule = pairs.Count == 0
            ? EmptyRankerBaseline(RuleScoreBaseline)
            : EvaluateRankerBaseline(RuleScoreBaseline, split.Test, ScorePairByRule, null);
        var ruleFailureKeys = pairs.Count == 0
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : split.Test
                .Where(static pair =>
                {
                    var score = ScorePairByRule(pair);
                    return score.PositiveScore <= score.NegativeScore;
                })
                .Select(static pair => BuildFailureKey(pair.EvalSampleId, pair.PositiveCandidateId, pair.NegativeCandidateId))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var weighted = pairs.Count == 0
            ? EmptyRankerBaseline(SimpleFeatureWeightedBaseline)
            : EvaluateRankerBaseline(SimpleFeatureWeightedBaseline, split.Test, ScorePairByWeightedFeatures, ruleFailureKeys);

        var baselineResults = pairs.Count == 0
            ? Array.Empty<RankerBaselineResult>()
            : new[] { rule, weighted };
        var best = baselineResults
            .OrderByDescending(item => item.PairwiseAccuracy)
            .ThenBy(item => item.FalsePositiveRate)
            .FirstOrDefault();

        return new RankerBaselineReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            InputPath = inputPath,
            PairCount = pairs.Count,
            Ready = notReadyReasons.Count == 0,
            Status = notReadyReasons.Count == 0 ? LearningDatasetReadinessStatus.Ready : LearningDatasetReadinessStatus.NotReady,
            NotReadyReasons = notReadyReasons,
            Split = split.Summary,
            Baselines = baselineResults,
            BestBaseline = best?.BaselineName ?? string.Empty,
            PolicyVersion = PolicyVersion
        };
    }

    public static string BuildRouterMarkdownReport(RouterIntentBaselineReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Router Intent Offline Baseline Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Input: `{report.InputPath}`");
        builder.AppendLine($"Policy: `{report.PolicyVersion}`");
        builder.AppendLine($"Status: `{report.Status}`");
        builder.AppendLine($"Samples: `{report.SampleCount}`");
        builder.AppendLine($"Best baseline: `{(string.IsNullOrWhiteSpace(report.BestBaseline) ? "-" : report.BestBaseline)}`");
        AppendNotReady(builder, report.NotReadyReasons);
        AppendSplit(builder, report.Split);

        builder.AppendLine();
        builder.AppendLine("## Baselines");
        builder.AppendLine();
        builder.AppendLine("| Baseline | Accuracy | MacroF1 |");
        builder.AppendLine("|---|---:|---:|");
        foreach (var result in report.Baselines)
        {
            builder.AppendLine($"| {result.BaselineName} | {FormatPercent(result.Accuracy)} | {Format(result.MacroF1)} |");
        }

        foreach (var result in report.Baselines)
        {
            builder.AppendLine();
            builder.AppendLine($"## {result.BaselineName}");
            AppendDoubleCounts(builder, "Per Intent Precision", result.PerIntentPrecision);
            AppendDoubleCounts(builder, "Per Intent Recall", result.PerIntentRecall);
            AppendConfusionMatrix(builder, result.ConfusionMatrix);
        }

        return builder.ToString();
    }

    public static string BuildRankerMarkdownReport(RankerBaselineReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Ranker Offline Baseline Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Input: `{report.InputPath}`");
        builder.AppendLine($"Policy: `{report.PolicyVersion}`");
        builder.AppendLine($"Status: `{report.Status}`");
        builder.AppendLine($"Pairs: `{report.PairCount}`");
        builder.AppendLine($"Best baseline: `{(string.IsNullOrWhiteSpace(report.BestBaseline) ? "-" : report.BestBaseline)}`");
        AppendNotReady(builder, report.NotReadyReasons);
        AppendSplit(builder, report.Split);

        builder.AppendLine();
        builder.AppendLine("## Baselines");
        builder.AppendLine();
        builder.AppendLine("| Baseline | PairwiseAccuracy | AUC | WinRateOverRule | FPR | FNR |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var result in report.Baselines)
        {
            builder.AppendLine($"| {result.BaselineName} | {FormatPercent(result.PairwiseAccuracy)} | {(result.Auc.HasValue ? Format(result.Auc.Value) : "-")} | {FormatPercent(result.WinRateOverRule)} | {FormatPercent(result.FalsePositiveRate)} | {FormatPercent(result.FalseNegativeRate)} |");
        }

        foreach (var result in report.Baselines)
        {
            builder.AppendLine();
            builder.AppendLine($"## {result.BaselineName} Top Failure Examples");
            if (result.TopFailureExamples.Count == 0)
            {
                builder.AppendLine("- (none)");
                continue;
            }

            builder.AppendLine("| Sample | Mode | Intent | Positive | Negative | PositiveScore | NegativeScore | Reason |");
            builder.AppendLine("|---|---|---|---|---|---:|---:|---|");
            foreach (var failure in result.TopFailureExamples)
            {
                builder.AppendLine($"| {failure.EvalSampleId} | {failure.Mode} | {failure.Intent} | {failure.PositiveCandidateId} | {failure.NegativeCandidateId} | {Format(failure.PositiveScore)} | {Format(failure.NegativeScore)} | {failure.Reason} |");
            }
        }

        return builder.ToString();
    }

    private static RouterIntentBaselineResult EvaluateRouterBaseline(
        string baselineName,
        IReadOnlyList<ContextPolicyFeatureExample> testExamples,
        Func<ContextPolicyFeatureExample, string> predict)
    {
        var actuals = testExamples.Select(GetIntentLabel).ToArray();
        var predictions = testExamples.Select(predict).ToArray();
        var labels = actuals.Concat(predictions)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var confusion = BuildConfusionMatrix(actuals, predictions, labels);
        var precision = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var recall = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var f1 = new List<double>();

        foreach (var label in labels)
        {
            var truePositive = confusion.TryGetValue(label, out var row) && row.TryGetValue(label, out var tp) ? tp : 0;
            var falsePositive = labels.Sum(actual => actual.Equals(label, StringComparison.OrdinalIgnoreCase)
                ? 0
                : confusion.TryGetValue(actual, out var fpRow) && fpRow.TryGetValue(label, out var fp) ? fp : 0);
            var falseNegative = confusion.TryGetValue(label, out var fnRow)
                ? fnRow.Where(pair => !pair.Key.Equals(label, StringComparison.OrdinalIgnoreCase)).Sum(pair => pair.Value)
                : 0;
            precision[label] = SafeDivide(truePositive, truePositive + falsePositive);
            recall[label] = SafeDivide(truePositive, truePositive + falseNegative);
            var denominator = precision[label] + recall[label];
            f1.Add(denominator <= 0 ? 0 : 2 * precision[label] * recall[label] / denominator);
        }

        return new RouterIntentBaselineResult
        {
            BaselineName = baselineName,
            Accuracy = SafeDivide(actuals.Zip(predictions).Count(pair => Matches(pair.First, pair.Second)), actuals.Length),
            MacroF1 = f1.Count == 0 ? 0 : f1.Average(),
            PerIntentPrecision = precision,
            PerIntentRecall = recall,
            ConfusionMatrix = confusion.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyDictionary<string, int>)pair.Value,
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private static RankerBaselineResult EvaluateRankerBaseline(
        string baselineName,
        IReadOnlyList<RankingPairExample> testPairs,
        Func<RankingPairExample, CandidatePairScore> score,
        IReadOnlySet<string>? ruleFailureKeys)
    {
        var outcomes = new List<double>();
        var negativeWins = 0;
        var positiveNotWinning = 0;
        var winOverRule = 0;
        var failures = new List<RankerBaselineFailureExample>();

        foreach (var pair in testPairs)
        {
            var pairScore = score(pair);
            var outcome = pairScore.PositiveScore > pairScore.NegativeScore
                ? 1
                : Math.Abs(pairScore.PositiveScore - pairScore.NegativeScore) < 0.0000001 ? 0.5 : 0;
            outcomes.Add(outcome);
            if (pairScore.NegativeScore > pairScore.PositiveScore)
            {
                negativeWins++;
            }

            if (pairScore.PositiveScore <= pairScore.NegativeScore)
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
                    Reason = pairScore.PositiveScore == pairScore.NegativeScore
                        ? "tie"
                        : "negative candidate outranked positive candidate"
                });
            }

            if (outcome >= 1
                && ruleFailureKeys is not null
                && ruleFailureKeys.Contains(BuildFailureKey(pair.EvalSampleId, pair.PositiveCandidateId, pair.NegativeCandidateId)))
            {
                winOverRule++;
            }
        }

        return new RankerBaselineResult
        {
            BaselineName = baselineName,
            PairwiseAccuracy = outcomes.Count == 0 ? 0 : outcomes.Average(),
            Auc = outcomes.Count == 0 ? null : outcomes.Average(),
            WinRateOverRule = SafeDivide(winOverRule, testPairs.Count),
            FalsePositiveRate = SafeDivide(negativeWins, testPairs.Count),
            FalseNegativeRate = SafeDivide(positiveNotWinning, testPairs.Count),
            TopFailureExamples = failures
                .OrderByDescending(item => item.NegativeScore - item.PositiveScore)
                .ThenBy(item => item.EvalSampleId, StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToArray()
        };
    }

    private static RankerBaselineResult EmptyRankerBaseline(string baselineName)
        => new()
        {
            BaselineName = baselineName
        };

    private static Dictionary<string, Dictionary<string, int>> BuildConfusionMatrix(
        IReadOnlyList<string> actuals,
        IReadOnlyList<string> predictions,
        IReadOnlyList<string> labels)
    {
        var confusion = labels.ToDictionary(
            label => label,
            _ => labels.ToDictionary(label => label, _ => 0, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        foreach (var (actual, prediction) in actuals.Zip(predictions))
        {
            if (!confusion.ContainsKey(actual))
            {
                confusion[actual] = labels.ToDictionary(label => label, _ => 0, StringComparer.OrdinalIgnoreCase);
            }

            if (!confusion[actual].ContainsKey(prediction))
            {
                confusion[actual][prediction] = 0;
            }

            confusion[actual][prediction]++;
        }

        return confusion;
    }

    private static string PredictMajorityIntent(
        IReadOnlyList<ContextPolicyFeatureExample> trainExamples,
        ContextPolicyFeatureExample _)
    {
        return trainExamples
            .GroupBy(GetIntentLabel, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key)
            .FirstOrDefault() ?? PlanningIntentDetector.FuzzyQuestion;
    }

    private static string PredictRuleBasedIntent(ContextPolicyFeatureExample example)
    {
        if (example.Mode.Contains("Automation", StringComparison.OrdinalIgnoreCase))
        {
            return PlanningIntentDetector.AutomationRecovery;
        }

        if (example.Mode.Contains("Coding", StringComparison.OrdinalIgnoreCase))
        {
            return PlanningIntentDetector.CodingTask;
        }

        if (example.Mode.Contains("Novel", StringComparison.OrdinalIgnoreCase))
        {
            return PlanningIntentDetector.NovelGeneration;
        }

        if (example.ChannelSources.Any(source => source.Contains("historical", StringComparison.OrdinalIgnoreCase)
                || source.Contains("deprecated", StringComparison.OrdinalIgnoreCase)))
        {
            return PlanningIntentDetector.AuditDeprecated;
        }

        if (example.ConstraintMatchScore > 0
            || example.ChannelSources.Any(source => source.Contains("constraint", StringComparison.OrdinalIgnoreCase)))
        {
            return PlanningIntentDetector.ConflictCheck;
        }

        if (example.StableMatchScore > example.ShortTermMatchScore
            && example.StableMatchScore > 0)
        {
            return PlanningIntentDetector.LongTermPreference;
        }

        if (example.ShortTermMatchScore > 0
            || example.ChannelSources.Any(source => source.Contains("working", StringComparison.OrdinalIgnoreCase)
                || source.Contains("short", StringComparison.OrdinalIgnoreCase)))
        {
            return PlanningIntentDetector.CurrentTask;
        }

        return PlanningIntentDetector.FuzzyQuestion;
    }

    private static CandidatePairScore ScorePairByRule(RankingPairExample pair)
    {
        return new CandidatePairScore(
            ScoreCandidateByRule(pair.FeatureSnapshot, "positive"),
            ScoreCandidateByRule(pair.FeatureSnapshot, "negative"));
    }

    private static CandidatePairScore ScorePairByWeightedFeatures(RankingPairExample pair)
    {
        return new CandidatePairScore(
            ScoreCandidateByWeightedFeatures(pair.FeatureSnapshot, "positive"),
            ScoreCandidateByWeightedFeatures(pair.FeatureSnapshot, "negative"));
    }

    private static double ScoreCandidateByRule(
        IReadOnlyDictionary<string, string> snapshot,
        string prefix)
    {
        var score = ParseDouble(snapshot, $"{prefix}Score");
        if (ParseBool(snapshot, $"{prefix}Selected"))
        {
            score += 5;
        }

        var rank = ParseInt(snapshot, $"{prefix}Rank");
        if (rank > 0)
        {
            score += 2.0 / rank;
        }

        return score;
    }

    private static double ScoreCandidateByWeightedFeatures(
        IReadOnlyDictionary<string, string> snapshot,
        string prefix)
    {
        var score = ParseDouble(snapshot, $"{prefix}Score");
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
        if (section.Contains("constraints", StringComparison.OrdinalIgnoreCase)
            || section.Contains("working", StringComparison.OrdinalIgnoreCase))
        {
            score += 2;
        }
        else if (section.Contains("stable", StringComparison.OrdinalIgnoreCase))
        {
            score += 1;
        }

        var kind = GetString(snapshot, $"{prefix}Kind");
        if (kind.Contains("historical", StringComparison.OrdinalIgnoreCase)
            || kind.Contains("deprecated", StringComparison.OrdinalIgnoreCase))
        {
            score -= 4;
        }

        return score;
    }

    private static RouterSplitResult SplitRouterExamples(IReadOnlyList<ContextPolicyFeatureExample> examples)
    {
        var split = SplitByGroup(examples, example => ResolveRouterGroupKey(example));
        return new RouterSplitResult(
            split.Train,
            split.Test,
            split.GroupCount,
            new LearningBaselineSplitSummary
            {
                Strategy = "DeterministicGroupHash80_20",
                GroupKey = "SourceId or metadata sampleId",
                TrainGroupCount = split.TrainGroupCount,
                TestGroupCount = split.TestGroupCount,
                TrainExampleCount = split.Train.Count,
                TestExampleCount = split.Test.Count
            });
    }

    private static RankerSplitResult SplitRankingPairs(IReadOnlyList<RankingPairExample> pairs)
    {
        var split = SplitByGroup(pairs, pair => string.IsNullOrWhiteSpace(pair.EvalSampleId) ? pair.Query : pair.EvalSampleId);
        return new RankerSplitResult(
            split.Train,
            split.Test,
            split.GroupCount,
            new LearningBaselineSplitSummary
            {
                Strategy = "DeterministicGroupHash80_20",
                GroupKey = "EvalSampleId",
                TrainGroupCount = split.TrainGroupCount,
                TestGroupCount = split.TestGroupCount,
                TrainExampleCount = split.Train.Count,
                TestExampleCount = split.Test.Count
            });
    }

    private static GenericSplitResult<T> SplitByGroup<T>(
        IReadOnlyList<T> items,
        Func<T, string> groupKey)
    {
        var groups = items
            .GroupBy(item => string.IsNullOrWhiteSpace(groupKey(item)) ? "__unknown__" : groupKey(item), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (groups.Length == 0)
        {
            return new GenericSplitResult<T>([], [], 0, 0, 0);
        }

        var testGroups = groups
            .Where(group => StableBucket(group.Key) == 0)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (testGroups.Count == 0 && groups.Length > 1)
        {
            testGroups.Add(groups[^1].Key);
        }

        if (testGroups.Count == groups.Length && groups.Length > 1)
        {
            testGroups.Remove(groups[0].Key);
        }

        var train = new List<T>();
        var test = new List<T>();
        foreach (var group in groups)
        {
            if (testGroups.Contains(group.Key))
            {
                test.AddRange(group);
            }
            else
            {
                train.AddRange(group);
            }
        }

        if (train.Count == 0 && test.Count > 0)
        {
            train.AddRange(test);
        }

        if (test.Count == 0 && train.Count > 0)
        {
            test.AddRange(train);
        }

        return new GenericSplitResult<T>(train, test, groups.Length, groups.Length - testGroups.Count, testGroups.Count);
    }

    private static string ResolveRouterGroupKey(ContextPolicyFeatureExample example)
    {
        if (example.Metadata.TryGetValue("sampleId", out var sampleId) && !string.IsNullOrWhiteSpace(sampleId))
        {
            return sampleId;
        }

        return string.IsNullOrWhiteSpace(example.SourceId) ? example.ExampleId : example.SourceId;
    }

    private static string GetIntentLabel(ContextPolicyFeatureExample example)
    {
        if (!string.IsNullOrWhiteSpace(example.Label))
        {
            return example.Label;
        }

        return string.IsNullOrWhiteSpace(example.Intent)
            ? PlanningIntentDetector.FuzzyQuestion
            : example.Intent;
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

    private static async Task WriteReportAsync<T>(
        T report,
        string markdown,
        string jsonOutputPath,
        string markdownOutputPath,
        CancellationToken cancellationToken)
    {
        await WriteTextAsync(jsonOutputPath, JsonSerializer.Serialize(report, JsonOptions), cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(markdownOutputPath, markdown, cancellationToken).ConfigureAwait(false);
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

    private static string ResolvePath(string path)
    {
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
    }

    private static int StableBucket(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return bytes[0] % 5;
    }

    private static bool Matches(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static double SafeDivide(double numerator, double denominator)
        => denominator <= 0 ? 0 : numerator / denominator;

    private static double ParseDouble(IReadOnlyDictionary<string, string> snapshot, string key)
    {
        return snapshot.TryGetValue(key, out var value)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
    }

    private static int ParseInt(IReadOnlyDictionary<string, string> snapshot, string key)
    {
        return snapshot.TryGetValue(key, out var value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
    }

    private static bool ParseBool(IReadOnlyDictionary<string, string> snapshot, string key)
    {
        return snapshot.TryGetValue(key, out var value)
            && bool.TryParse(value, out var parsed)
            && parsed;
    }

    private static string GetString(IReadOnlyDictionary<string, string> snapshot, string key)
    {
        return snapshot.TryGetValue(key, out var value) ? value : string.Empty;
    }

    private static string BuildFailureKey(string sampleId, string positiveId, string negativeId)
        => $"{sampleId}\u001f{positiveId}\u001f{negativeId}";

    private static void AppendNotReady(StringBuilder builder, IReadOnlyList<string> reasons)
    {
        if (reasons.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("## Not Ready Reasons");
        foreach (var reason in reasons)
        {
            builder.AppendLine($"- {reason}");
        }
    }

    private static void AppendSplit(StringBuilder builder, LearningBaselineSplitSummary split)
    {
        builder.AppendLine();
        builder.AppendLine("## Train/Test Split");
        builder.AppendLine();
        builder.AppendLine($"- Strategy: `{split.Strategy}`");
        builder.AppendLine($"- Group key: `{split.GroupKey}`");
        builder.AppendLine($"- Train groups/examples: `{split.TrainGroupCount}` / `{split.TrainExampleCount}`");
        builder.AppendLine($"- Test groups/examples: `{split.TestGroupCount}` / `{split.TestExampleCount}`");
    }

    private static void AppendDoubleCounts(
        StringBuilder builder,
        string title,
        IReadOnlyDictionary<string, double> counts)
    {
        builder.AppendLine();
        builder.AppendLine($"### {title}");
        if (counts.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        builder.AppendLine("| Intent | Value |");
        builder.AppendLine("|---|---:|");
        foreach (var pair in counts.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {pair.Key} | {Format(pair.Value)} |");
        }
    }

    private static void AppendConfusionMatrix(
        StringBuilder builder,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> confusion)
    {
        builder.AppendLine();
        builder.AppendLine("### Confusion Matrix");
        if (confusion.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        var predictions = confusion.Values
            .SelectMany(row => row.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        builder.Append("| Actual \\ Predicted |");
        foreach (var prediction in predictions)
        {
            builder.Append($" {prediction} |");
        }

        builder.AppendLine();
        builder.Append("|---|");
        foreach (var _ in predictions)
        {
            builder.Append("---:|");
        }

        builder.AppendLine();
        foreach (var row in confusion.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append($"| {row.Key} |");
            foreach (var prediction in predictions)
            {
                builder.Append($" {(row.Value.TryGetValue(prediction, out var value) ? value : 0)} |");
            }

            builder.AppendLine();
        }
    }

    private static string Format(double value)
        => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string FormatPercent(double value)
        => value.ToString("P2", CultureInfo.InvariantCulture);

    private readonly record struct CandidatePairScore(double PositiveScore, double NegativeScore);

    private sealed record GenericSplitResult<T>(
        IReadOnlyList<T> Train,
        IReadOnlyList<T> Test,
        int GroupCount,
        int TrainGroupCount,
        int TestGroupCount);

    private sealed record RouterSplitResult(
        IReadOnlyList<ContextPolicyFeatureExample> Train,
        IReadOnlyList<ContextPolicyFeatureExample> Test,
        int GroupCount,
        LearningBaselineSplitSummary Summary);

    private sealed record RankerSplitResult(
        IReadOnlyList<RankingPairExample> Train,
        IReadOnlyList<RankingPairExample> Test,
        int GroupCount,
        LearningBaselineSplitSummary Summary);
}
