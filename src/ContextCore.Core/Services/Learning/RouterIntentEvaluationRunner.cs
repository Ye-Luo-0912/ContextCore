using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Planning;

namespace ContextCore.Core.Services;

public abstract class RouterIntentClassifier
{
    public abstract string ClassifierName { get; }

    public virtual void Fit(IReadOnlyList<ContextPolicyFeatureExample> examples)
    {
    }

    public abstract RouterIntentClassifierPrediction Predict(ContextPolicyFeatureExample example);
}

public sealed class ExistingRuleBasedRouterBaseline : RouterIntentClassifier
{
    private readonly PlanningIntentDetector _detector = new();

    public override string ClassifierName => RouterIntentClassifierBaselineNames.ExistingRuleBasedRouterBaseline;

    public override RouterIntentClassifierPrediction Predict(ContextPolicyFeatureExample example)
    {
        ArgumentNullException.ThrowIfNull(example);

        var snapshot = new ContextPlanningSnapshot
        {
            WorkspaceId = example.WorkspaceId,
            CollectionId = example.CollectionId,
            SessionId = example.Metadata.TryGetValue("sessionId", out var sessionId) ? sessionId : string.Empty
        };
        var detection = _detector.Detect(snapshot, BuildInputText(example), example.Mode);
        return new RouterIntentClassifierPrediction
        {
            Intent = detection.Intent,
            Confidence = Clamp01(detection.Confidence),
            Abstained = detection.Confidence < 0.2,
            Reasons = detection.Reasons
        };
    }

    private static string BuildInputText(ContextPolicyFeatureExample example)
    {
        var builder = new StringBuilder();
        AppendIfPresent(builder, example.InputSummary);
        if (example.Metadata.TryGetValue("currentInput", out var currentInput))
        {
            AppendIfPresent(builder, currentInput);
        }

        if (example.Metadata.TryGetValue("queryText", out var queryText))
        {
            AppendIfPresent(builder, queryText);
        }

        return builder.Length == 0 ? example.Mode : builder.ToString();
    }

    private static void AppendIfPresent(StringBuilder builder, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append(' ');
        }

        builder.Append(value.Trim());
    }

    private static double Clamp01(double value)
    {
        if (value <= 0)
        {
            return 0;
        }

        return value >= 1 ? 1 : value;
    }
}

public sealed class TokenCentroidRouterBaseline : RouterIntentClassifier
{
    private readonly Dictionary<string, Dictionary<string, double>> _centroids =
        new(StringComparer.OrdinalIgnoreCase);

    public override string ClassifierName => RouterIntentClassifierBaselineNames.TokenCentroidRouterBaseline;

    public override void Fit(IReadOnlyList<ContextPolicyFeatureExample> examples)
    {
        ArgumentNullException.ThrowIfNull(examples);
        _centroids.Clear();

        foreach (var example in examples)
        {
            var label = RouterIntentEvaluationRunner.GetIntentLabel(example);
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            var vector = BuildFeatureVector(example);
            if (vector.Count == 0)
            {
                continue;
            }

            if (!_centroids.TryGetValue(label, out var centroid))
            {
                centroid = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                _centroids[label] = centroid;
            }

            foreach (var pair in vector)
            {
                centroid[pair.Key] = centroid.GetValueOrDefault(pair.Key) + pair.Value;
            }
        }

        foreach (var centroid in _centroids.Values)
        {
            NormalizeInPlace(centroid);
        }
    }

    public override RouterIntentClassifierPrediction Predict(ContextPolicyFeatureExample example)
    {
        ArgumentNullException.ThrowIfNull(example);

        var vector = BuildFeatureVector(example);
        if (vector.Count == 0 || _centroids.Count == 0)
        {
            return new RouterIntentClassifierPrediction
            {
                Intent = PlanningIntentDetector.FuzzyQuestion,
                Confidence = 0,
                Abstained = true,
                Reasons = ["no token vector available"]
            };
        }

        NormalizeInPlace(vector);

        var scores = new List<(string Intent, double Score)>();
        var bestIntent = string.Empty;
        var bestScore = double.NegativeInfinity;
        var secondScore = double.NegativeInfinity;
        foreach (var pair in _centroids)
        {
            var score = Cosine(vector, pair.Value);
            scores.Add((pair.Key, score));
            if (score > bestScore)
            {
                secondScore = bestScore;
                bestScore = score;
                bestIntent = pair.Key;
            }
            else if (score > secondScore)
            {
                secondScore = score;
            }
        }

        var margin = double.IsNegativeInfinity(secondScore) ? Math.Max(0, bestScore) : Math.Max(0, bestScore - secondScore);
        var confidence = Math.Clamp(Math.Max(bestScore, 0) * 0.75 + margin * 0.25, 0, 1);
        return new RouterIntentClassifierPrediction
        {
            Intent = string.IsNullOrWhiteSpace(bestIntent) ? PlanningIntentDetector.FuzzyQuestion : bestIntent,
            Confidence = confidence,
            Abstained = confidence <= 0,
            Reasons = [$"centroidScore={bestScore.ToString("0.####", CultureInfo.InvariantCulture)}"],
            TopPredictions = scores
                .OrderByDescending(static item => item.Score)
                .ThenBy(static item => item.Intent, StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .Select(static item => new RouterIntentShadowTopPrediction
                {
                    Intent = item.Intent,
                    Confidence = Math.Clamp(item.Score, 0, 1),
                    Reason = $"centroidScore={item.Score.ToString("0.####", CultureInfo.InvariantCulture)}"
                })
                .ToArray()
        };
    }

    private static Dictionary<string, double> BuildFeatureVector(ContextPolicyFeatureExample example)
    {
        var vector = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        AddTokens(vector, "text", example.InputSummary, 1.0);
        AddTokens(vector, "mode", example.Mode, 0.75);
        AddTokens(vector, "task", example.TaskKind, 0.5);
        AddTokens(vector, "source", example.SourceType, 0.35);
        AddTokens(vector, "candidateKind", example.CandidateKind, 0.35);
        AddTokens(vector, "candidateLayer", example.CandidateLayer, 0.25);
        foreach (var channel in example.ChannelSources)
        {
            AddTokens(vector, "channel", channel, 0.35);
        }

        AddNumericFeature(vector, "relationPath", example.RelationPathCount);
        AddNumericFeature(vector, "keywordScore", example.KeywordMatchScore);
        AddNumericFeature(vector, "semanticScore", example.SemanticAnchorMatchScore);
        AddNumericFeature(vector, "shortTermScore", example.ShortTermMatchScore);
        AddNumericFeature(vector, "stableScore", example.StableMatchScore);
        AddNumericFeature(vector, "constraintScore", example.ConstraintMatchScore);
        return vector;
    }

    private static void AddTokens(Dictionary<string, double> vector, string prefix, string? text, double weight)
    {
        foreach (var token in Tokenize(text))
        {
            var key = $"{prefix}:{token}";
            vector[key] = vector.GetValueOrDefault(key) + weight;
        }
    }

    private static IEnumerable<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var builder = new StringBuilder();
        var cjkWindow = new Queue<char>();
        foreach (var rune in text.EnumerateRunes())
        {
            var runeText = rune.ToString().ToLowerInvariant();
            if (IsAsciiTokenRune(rune.Value))
            {
                builder.Append(runeText);
                cjkWindow.Clear();
                continue;
            }

            if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }

            if (IsCjkRune(rune.Value))
            {
                var ch = runeText[0];
                yield return runeText;
                cjkWindow.Enqueue(ch);
                while (cjkWindow.Count > 2)
                {
                    cjkWindow.Dequeue();
                }

                if (cjkWindow.Count == 2)
                {
                    yield return new string(cjkWindow.ToArray());
                }
            }
            else
            {
                cjkWindow.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static bool IsAsciiTokenRune(int value)
    {
        return value is >= 'a' and <= 'z'
            || value is >= 'A' and <= 'Z'
            || value is >= '0' and <= '9'
            || value == '#'
            || value == '_'
            || value == '-';
    }

    private static bool IsCjkRune(int value)
    {
        return value is >= 0x3400 and <= 0x4DBF
            || value is >= 0x4E00 and <= 0x9FFF
            || value is >= 0xF900 and <= 0xFAFF;
    }

    private static void AddNumericFeature(Dictionary<string, double> vector, string name, double value)
    {
        if (value <= 0)
        {
            return;
        }

        var bucket = value switch
        {
            < 0.25 => "low",
            < 0.75 => "medium",
            _ => "high"
        };
        var key = $"numeric:{name}:{bucket}";
        vector[key] = vector.GetValueOrDefault(key) + 0.2;
    }

    private static void NormalizeInPlace(Dictionary<string, double> vector)
    {
        var norm = Math.Sqrt(vector.Values.Sum(value => value * value));
        if (norm <= 0)
        {
            return;
        }

        foreach (var key in vector.Keys.ToArray())
        {
            vector[key] /= norm;
        }
    }

    private static double Cosine(IReadOnlyDictionary<string, double> left, IReadOnlyDictionary<string, double> right)
    {
        if (left.Count > right.Count)
        {
            return Cosine(right, left);
        }

        var score = 0.0;
        foreach (var pair in left)
        {
            if (right.TryGetValue(pair.Key, out var other))
            {
                score += pair.Value * other;
            }
        }

        return score;
    }
}

public sealed class RouterIntentEvaluationRunner
{
    public const string PolicyVersion = "router-intent-classifier-r1/v1";
    public const string DefaultOutputDirectory = "learning/router";
    public const string ReportFileName = "router-intent-baseline-report.json";
    public const string MarkdownReportFileName = "router-intent-baseline-report.md";
    public const string ConfusionMatrixFileName = "router-intent-confusion-matrix.json";

    private const double LowConfidenceThreshold = 0.35;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task<RouterIntentClassifierBaselineReport> RunAsync(
        string inputPath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var resolvedInputPath = ResolvePath(inputPath);
        var resolvedOutputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(resolvedOutputDirectory);

        var examples = await ReadJsonLinesAsync<ContextPolicyFeatureExample>(resolvedInputPath, cancellationToken)
            .ConfigureAwait(false);
        var report = BuildReport(examples, resolvedInputPath);
        var matrixReport = BuildConfusionMatrixReport(report, resolvedInputPath);

        await File.WriteAllTextAsync(
                Path.Combine(resolvedOutputDirectory, ReportFileName),
                JsonSerializer.Serialize(report, JsonOptions),
                cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(
                Path.Combine(resolvedOutputDirectory, MarkdownReportFileName),
                BuildMarkdownReport(report),
                cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(
                Path.Combine(resolvedOutputDirectory, ConfusionMatrixFileName),
                JsonSerializer.Serialize(matrixReport, JsonOptions),
                cancellationToken)
            .ConfigureAwait(false);

        return report;
    }

    public RouterIntentClassifierBaselineReport BuildReport(
        IReadOnlyList<ContextPolicyFeatureExample> sourceExamples,
        string inputPath = "")
    {
        ArgumentNullException.ThrowIfNull(sourceExamples);

        var examples = sourceExamples
            .Where(example => string.Equals(example.TaskKind, "RouterIntent", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(example.TaskKind))
            .Where(example => !string.IsNullOrWhiteSpace(GetIntentLabel(example)))
            .ToArray();
        if (examples.Length == 0)
        {
            return new RouterIntentClassifierBaselineReport
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                InputPath = inputPath,
                SampleCount = 0,
                Ready = false,
                Status = LearningDatasetReadinessStatus.NotReady,
                NotReadyReasons = ["router-intent-examples is empty"],
                Recommendation = RouterIntentClassifierRecommendations.NeedsMoreExamples,
                PolicyVersion = PolicyVersion
            };
        }

        var labels = examples
            .Select(GetIntentLabel)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (examples.Length < 12 || labels.Length < 2)
        {
            return new RouterIntentClassifierBaselineReport
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                InputPath = inputPath,
                SampleCount = examples.Length,
                Ready = false,
                Status = LearningDatasetReadinessStatus.NotReady,
                NotReadyReasons =
                [
                    examples.Length < 12 ? "router-intent-examples has fewer than 12 examples" : "router intent coverage has fewer than 2 labels"
                ],
                Recommendation = examples.Length < 12
                    ? RouterIntentClassifierRecommendations.NeedsMoreExamples
                    : RouterIntentClassifierRecommendations.NeedsIntentBoundaryClarification,
                PolicyVersion = PolicyVersion
            };
        }

        var split = SplitExamples(examples);
        var classifiers = new RouterIntentClassifier[]
        {
            new ExistingRuleBasedRouterBaseline(),
            new TokenCentroidRouterBaseline()
        };
        var results = new List<RouterIntentClassifierBaselineResult>();
        foreach (var classifier in classifiers)
        {
            classifier.Fit(split.TrainExamples);
            results.Add(Evaluate(classifier, split.TestExamples.Length == 0 ? examples : split.TestExamples, labels));
        }

        var best = results
            .OrderByDescending(item => item.MacroF1)
            .ThenByDescending(item => item.Accuracy)
            .First();
        var recommendation = RecommendReport(best, examples.Length, labels.Length);
        return new RouterIntentClassifierBaselineReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            InputPath = inputPath,
            SampleCount = examples.Length,
            Ready = true,
            Status = LearningDatasetReadinessStatus.Ready,
            Split = split.Summary,
            Baselines = results,
            BestBaseline = best.BaselineName,
            Recommendation = recommendation,
            PolicyVersion = PolicyVersion
        };
    }

    public static string GetIntentLabel(ContextPolicyFeatureExample example)
    {
        if (!string.IsNullOrWhiteSpace(example.Label))
        {
            return example.Label.Trim();
        }

        return string.IsNullOrWhiteSpace(example.Intent)
            ? PlanningIntentDetector.FuzzyQuestion
            : example.Intent.Trim();
    }

    public static string BuildMarkdownReport(RouterIntentClassifierBaselineReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Router Intent Classifier Baseline Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- Input: `{report.InputPath}`");
        builder.AppendLine($"- Samples: `{report.SampleCount}`");
        builder.AppendLine($"- Status: `{report.Status}`");
        builder.AppendLine($"- Best baseline: `{(string.IsNullOrWhiteSpace(report.BestBaseline) ? "-" : report.BestBaseline)}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- Policy version: `{report.PolicyVersion}`");
        if (report.NotReadyReasons.Count > 0)
        {
            builder.AppendLine($"- Not ready reasons: {string.Join(", ", report.NotReadyReasons)}");
        }

        builder.AppendLine();
        builder.AppendLine("## Split");
        builder.AppendLine();
        builder.AppendLine($"- Strategy: `{report.Split.Strategy}`");
        builder.AppendLine($"- Group key: `{report.Split.GroupKey}`");
        builder.AppendLine($"- Train: `{report.Split.TrainExampleCount}` examples / `{report.Split.TrainGroupCount}` groups");
        builder.AppendLine($"- Test: `{report.Split.TestExampleCount}` examples / `{report.Split.TestGroupCount}` groups");
        builder.AppendLine();
        builder.AppendLine("## Baselines");
        builder.AppendLine();
        builder.AppendLine("| Baseline | Accuracy | MacroF1 | LowConfidence | Abstain | CurrentTask | FuzzyQuestion | CodingTask | NovelGeneration | AutomationRecovery | Recommendation |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var baseline in report.Baselines)
        {
            builder.AppendLine(FormattableString.Invariant(
                $"| {baseline.BaselineName} | {baseline.Accuracy:P2} | {baseline.MacroF1:0.####} | {baseline.LowConfidenceCount} | {baseline.AbstainCount} | {baseline.CurrentTaskRecall:P2} | {baseline.FuzzyQuestionRecall:P2} | {baseline.CodingTaskRecall:P2} | {baseline.NovelGenerationRecall:P2} | {baseline.AutomationRecoveryRecall:P2} | {baseline.Recommendation} |"));
        }

        var best = report.Baselines.FirstOrDefault(item => string.Equals(item.BaselineName, report.BestBaseline, StringComparison.OrdinalIgnoreCase));
        if (best is not null)
        {
            builder.AppendLine();
            builder.AppendLine("## Best Baseline Confusion Matrix");
            builder.AppendLine();
            foreach (var actual in best.ConfusionMatrix.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                var cells = actual.Value
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => $"{pair.Key}={pair.Value}");
                builder.AppendLine($"- {actual.Key}: {string.Join(", ", cells)}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Runtime Safety");
        builder.AppendLine();
        builder.AppendLine("- This report is offline-only.");
        builder.AppendLine("- It does not replace the runtime planning router.");
        builder.AppendLine("- It does not change retrieval, planning, PackingPolicy, scoring, or package output.");
        return builder.ToString();
    }

    private static RouterIntentClassifierBaselineResult Evaluate(
        RouterIntentClassifier classifier,
        IReadOnlyList<ContextPolicyFeatureExample> testExamples,
        IReadOnlyList<string> labels)
    {
        var confusion = labels.ToDictionary(
            label => label,
            _ => labels.ToDictionary(label => label, _ => 0, StringComparer.OrdinalIgnoreCase) as IReadOnlyDictionary<string, int>,
            StringComparer.OrdinalIgnoreCase);
        var mutableConfusion = labels.ToDictionary(
            label => label,
            _ => labels.ToDictionary(label => label, _ => 0, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        var correct = 0;
        var lowConfidence = 0;
        var abstain = 0;
        foreach (var example in testExamples)
        {
            var actual = GetIntentLabel(example);
            var prediction = classifier.Predict(example);
            var predicted = string.IsNullOrWhiteSpace(prediction.Intent)
                ? PlanningIntentDetector.FuzzyQuestion
                : prediction.Intent;
            if (!mutableConfusion.ContainsKey(actual))
            {
                mutableConfusion[actual] = labels.ToDictionary(label => label, _ => 0, StringComparer.OrdinalIgnoreCase);
            }

            if (!mutableConfusion[actual].ContainsKey(predicted))
            {
                mutableConfusion[actual][predicted] = 0;
            }

            mutableConfusion[actual][predicted]++;
            if (string.Equals(actual, predicted, StringComparison.OrdinalIgnoreCase))
            {
                correct++;
            }

            if (prediction.Confidence < LowConfidenceThreshold)
            {
                lowConfidence++;
            }

            if (prediction.Abstained)
            {
                abstain++;
            }
        }

        confusion = mutableConfusion.ToDictionary(
            pair => pair.Key,
            pair => pair.Value as IReadOnlyDictionary<string, int>,
            StringComparer.OrdinalIgnoreCase);
        var precision = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var recall = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var f1Values = new List<double>();
        foreach (var label in labels)
        {
            var truePositive = mutableConfusion.GetValueOrDefault(label)?.GetValueOrDefault(label) ?? 0;
            var actualCount = mutableConfusion.GetValueOrDefault(label)?.Values.Sum() ?? 0;
            var predictedCount = mutableConfusion.Values.Sum(row => row.GetValueOrDefault(label));
            var labelPrecision = predictedCount == 0 ? 0 : (double)truePositive / predictedCount;
            var labelRecall = actualCount == 0 ? 0 : (double)truePositive / actualCount;
            precision[label] = labelPrecision;
            recall[label] = labelRecall;
            f1Values.Add(labelPrecision + labelRecall == 0
                ? 0
                : 2 * labelPrecision * labelRecall / (labelPrecision + labelRecall));
        }

        var result = new RouterIntentClassifierBaselineResult
        {
            BaselineName = classifier.ClassifierName,
            Accuracy = testExamples.Count == 0 ? 0 : (double)correct / testExamples.Count,
            MacroF1 = f1Values.Count == 0 ? 0 : f1Values.Average(),
            PerIntentPrecision = precision,
            PerIntentRecall = recall,
            ConfusionMatrix = confusion,
            LowConfidenceCount = lowConfidence,
            AbstainCount = abstain,
            CurrentTaskRecall = recall.GetValueOrDefault(PlanningIntentDetector.CurrentTask),
            FuzzyQuestionRecall = recall.GetValueOrDefault(PlanningIntentDetector.FuzzyQuestion),
            CodingTaskRecall = recall.GetValueOrDefault(PlanningIntentDetector.CodingTask),
            NovelGenerationRecall = recall.GetValueOrDefault(PlanningIntentDetector.NovelGeneration),
            AutomationRecoveryRecall = recall.GetValueOrDefault(PlanningIntentDetector.AutomationRecovery)
        };
        return WithRecommendation(result, RecommendBaseline(result));
    }

    private static RouterIntentClassifierBaselineResult WithRecommendation(
        RouterIntentClassifierBaselineResult result,
        string recommendation)
    {
        return new RouterIntentClassifierBaselineResult
        {
            BaselineName = result.BaselineName,
            Accuracy = result.Accuracy,
            MacroF1 = result.MacroF1,
            PerIntentPrecision = result.PerIntentPrecision,
            PerIntentRecall = result.PerIntentRecall,
            ConfusionMatrix = result.ConfusionMatrix,
            LowConfidenceCount = result.LowConfidenceCount,
            AbstainCount = result.AbstainCount,
            CurrentTaskRecall = result.CurrentTaskRecall,
            FuzzyQuestionRecall = result.FuzzyQuestionRecall,
            CodingTaskRecall = result.CodingTaskRecall,
            NovelGenerationRecall = result.NovelGenerationRecall,
            AutomationRecoveryRecall = result.AutomationRecoveryRecall,
            Recommendation = recommendation
        };
    }

    private static string RecommendBaseline(RouterIntentClassifierBaselineResult result)
    {
        if (result.MacroF1 < 0.4 || GetMinimumTrackedRecall(result) < 0.35)
        {
            return RouterIntentClassifierRecommendations.BlockedByLowRecall;
        }

        if (result.Accuracy >= 0.7 && result.MacroF1 >= 0.65 && GetMinimumTrackedRecall(result) >= 0.5)
        {
            return RouterIntentClassifierRecommendations.ReadyForRouterShadow;
        }

        return string.Equals(result.BaselineName, RouterIntentClassifierBaselineNames.ExistingRuleBasedRouterBaseline, StringComparison.OrdinalIgnoreCase)
            ? RouterIntentClassifierRecommendations.KeepRuleBased
            : RouterIntentClassifierRecommendations.NeedsIntentBoundaryClarification;
    }

    private static string RecommendReport(RouterIntentClassifierBaselineResult best, int sampleCount, int labelCount)
    {
        if (sampleCount < 50)
        {
            return RouterIntentClassifierRecommendations.NeedsMoreExamples;
        }

        if (labelCount < 3)
        {
            return RouterIntentClassifierRecommendations.NeedsNegativeSamples;
        }

        return best.Recommendation;
    }

    private static double GetMinimumTrackedRecall(RouterIntentClassifierBaselineResult result)
    {
        var values = new[]
        {
            result.CurrentTaskRecall,
            result.FuzzyQuestionRecall,
            result.CodingTaskRecall,
            result.NovelGenerationRecall,
            result.AutomationRecoveryRecall
        };
        var activeValues = values.Where(value => value > 0).ToArray();
        return activeValues.Length == 0 ? 0 : activeValues.Min();
    }

    private static RouterIntentConfusionMatrixReport BuildConfusionMatrixReport(
        RouterIntentClassifierBaselineReport report,
        string inputPath)
    {
        var best = report.Baselines.FirstOrDefault(item => string.Equals(item.BaselineName, report.BestBaseline, StringComparison.OrdinalIgnoreCase))
            ?? report.Baselines.FirstOrDefault()
            ?? new RouterIntentClassifierBaselineResult();
        var intents = best.ConfusionMatrix.Keys
            .Concat(best.ConfusionMatrix.Values.SelectMany(row => row.Keys))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(intent => intent, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new RouterIntentConfusionMatrixReport
        {
            GeneratedAt = report.GeneratedAt,
            InputPath = inputPath,
            BaselineName = best.BaselineName,
            Intents = intents,
            ConfusionMatrix = best.ConfusionMatrix
        };
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
            var bucket = StableBucket(group.Key) % 100;
            if (bucket < 80)
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

        var train = trainGroups.SelectMany(group => group).ToArray();
        var test = testGroups.SelectMany(group => group).ToArray();
        return new RouterIntentSplit(
            train,
            test,
            new LearningBaselineSplitSummary
            {
                Strategy = "DeterministicGroupHash80_20",
                GroupKey = "SourceType+SourceId",
                TrainGroupCount = trainGroups.Count,
                TestGroupCount = testGroups.Count,
                TrainExampleCount = train.Length,
                TestExampleCount = test.Length
            });
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

    private static string ResolvePath(string path)
    {
        return Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? LearningDatasetQualityReportBuilder.RouterIntentExamplesFileName : path);
    }

    private static async Task<IReadOnlyList<T>> ReadJsonLinesAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return Array.Empty<T>();
        }

        var results = new List<T>();
        foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var item = JsonSerializer.Deserialize<T>(line, JsonOptions);
            if (item is not null)
            {
                results.Add(item);
            }
        }

        return results;
    }

    private sealed record RouterIntentSplit(
        ContextPolicyFeatureExample[] TrainExamples,
        ContextPolicyFeatureExample[] TestExamples,
        LearningBaselineSplitSummary Summary);
}
