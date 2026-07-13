using System.Globalization;
using System.Text;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Planning;

namespace ContextCore.Core.Services;

/// <summary>
/// P3-01：路由意图分类器基类与实现。
/// 从 RouterIntentEvaluationRunner 提取到 Core，因为 RouterIntentShadowService（运行时）依赖这些类型。
/// RouterIntentEvaluationRunner 保留在 Evaluation 项目中。
/// </summary>
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

        var detection = _detector.Detect(BuildInputText(example), example.Mode);
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
            var label = RouterIntentClassifierLabelResolver.GetIntentLabel(example);
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

/// <summary>
/// P3-01：意图标签解析器。从 RouterIntentEvaluationRunner.GetIntentLabel 提取到 Core，
/// 供 TokenCentroidRouterBaseline 使用。RouterIntentEvaluationRunner 保留在 Evaluation 中。
/// </summary>
public static class RouterIntentClassifierLabelResolver
{
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
}
