using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContextCore.Evaluation.Learning;

/// <summary>单条离线评测预测：事件 ID + 是否正确。</summary>
public sealed record OfflineEvaluationPrediction(string EventId, bool Correct);

/// <summary>某个切片上的基线 vs 候选准确率。</summary>
public sealed record OfflineEvaluationSlice(
    string Name,
    int SampleCount,
    double BaselineAccuracy,
    double CandidateAccuracy,
    double Improvement);

/// <summary>
/// 离线训练/评测结果：只读隔离 test 数据上的基线 vs 候选对比；
/// 工件内容寻址（数据集版本 + 代码版本 + 特征 schema），保证可重复构建与追溯。
/// </summary>
public sealed record OfflineEvaluationResult(
    string ArtifactId,
    string SnapshotId,
    string CodeVersion,
    string FeatureSchemaVersion,
    int SampleCount,
    double BaselineAccuracy,
    double CandidateAccuracy,
    double Improvement,
    bool CandidateBetterOrEqual,
    IReadOnlyList<OfflineEvaluationSlice> Slices);

/// <summary>
/// 离线训练与评测 harness：在隔离的 test 数据上比较候选策略与当前 deterministic
/// baseline（以及简单 heuristic，由调用方作为另一组预测传入），除平均准确率外
/// 按切片检查长尾、租户、语言、数据新旧和 hard negatives（切片由调用方按事件标注）。
/// 工件包含数据版本（快照 ID）、代码版本、特征 schema 与输入指纹，可重复构建。
/// </summary>
public static class OfflineLearningEvaluationHarness
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 评估基线 vs 候选在 test 数据上的准确率与各切片差异。
    /// </summary>
    /// <param name="baseline">基线（当前 deterministic 策略）预测。</param>
    /// <param name="candidate">候选策略预测（与 baseline 逐条对应）。</param>
    /// <param name="sliceByEventId">事件 ID → 切片名（如 tenant:xx / language:xx / recent / stale / hard-negative）。</param>
    /// <param name="snapshotId">数据版本（输入快照 ID）。</param>
    /// <param name="featureSchemaVersion">特征 schema 版本。</param>
    /// <param name="codeVersion">代码版本；缺省取当前程序集版本。</param>
    public static OfflineEvaluationResult Evaluate(
        IReadOnlyList<OfflineEvaluationPrediction> baseline,
        IReadOnlyList<OfflineEvaluationPrediction> candidate,
        IReadOnlyDictionary<string, string> sliceByEventId,
        string snapshotId,
        string featureSchemaVersion,
        string? codeVersion = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(sliceByEventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);

        if (baseline.Count != candidate.Count)
        {
            throw new ArgumentException("baseline 与 candidate 预测必须逐条对应。", nameof(candidate));
        }

        var resolvedCodeVersion = string.IsNullOrWhiteSpace(codeVersion)
            ? typeof(OfflineLearningEvaluationHarness).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                ?? "unknown"
            : codeVersion.Trim();

        var slices = BuildSlices(baseline, candidate, sliceByEventId);
        var overall = slices.First(slice => slice.Name == "overall");
        var improvement = overall.CandidateAccuracy - overall.BaselineAccuracy;

        var artifactId = BuildArtifactId(
            snapshotId,
            resolvedCodeVersion,
            featureSchemaVersion,
            baseline,
            candidate,
            sliceByEventId);

        return new OfflineEvaluationResult(
            ArtifactId: artifactId,
            SnapshotId: snapshotId,
            CodeVersion: resolvedCodeVersion,
            FeatureSchemaVersion: featureSchemaVersion,
            SampleCount: baseline.Count,
            BaselineAccuracy: overall.BaselineAccuracy,
            CandidateAccuracy: overall.CandidateAccuracy,
            Improvement: improvement,
            CandidateBetterOrEqual: improvement >= -1e-9,
            Slices: slices);
    }

    private static IReadOnlyList<OfflineEvaluationSlice> BuildSlices(
        IReadOnlyList<OfflineEvaluationPrediction> baseline,
        IReadOnlyList<OfflineEvaluationPrediction> candidate,
        IReadOnlyDictionary<string, string> sliceByEventId)
    {
        var groups = new Dictionary<string, List<(bool BaselineCorrect, bool CandidateCorrect)>>(StringComparer.OrdinalIgnoreCase)
        {
            ["overall"] = new()
        };
        foreach (var pair in baseline.Zip(candidate))
        {
            groups["overall"].Add((pair.First.Correct, pair.Second.Correct));
            var sliceName = sliceByEventId.TryGetValue(pair.First.EventId, out var name) && !string.IsNullOrWhiteSpace(name)
                ? name.Trim()
                : "unlabeled";
            if (!groups.TryGetValue(sliceName, out var list))
            {
                list = new List<(bool, bool)>();
                groups[sliceName] = list;
            }

            list.Add((pair.First.Correct, pair.Second.Correct));
        }

        return groups
            .Select(pair => new OfflineEvaluationSlice(
                Name: pair.Key,
                SampleCount: pair.Value.Count,
                BaselineAccuracy: Accuracy(pair.Value.Select(item => item.BaselineCorrect)),
                CandidateAccuracy: Accuracy(pair.Value.Select(item => item.CandidateCorrect)),
                Improvement: Accuracy(pair.Value.Select(item => item.CandidateCorrect))
                    - Accuracy(pair.Value.Select(item => item.BaselineCorrect))))
            .OrderBy(slice => slice.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static double Accuracy(IEnumerable<bool> correct)
    {
        var values = correct.ToArray();
        return values.Length == 0 ? 0.0 : values.Count(value => value) / (double)values.Length;
    }

    private static string BuildArtifactId(
        string snapshotId,
        string codeVersion,
        string featureSchemaVersion,
        IReadOnlyList<OfflineEvaluationPrediction> baseline,
        IReadOnlyList<OfflineEvaluationPrediction> candidate,
        IReadOnlyDictionary<string, string> sliceByEventId)
    {
        var input = string.Join(
            "\u001f",
            snapshotId,
            codeVersion,
            featureSchemaVersion,
            JsonSerializer.Serialize(baseline, JsonOptions),
            JsonSerializer.Serialize(candidate, JsonOptions),
            JsonSerializer.Serialize(sliceByEventId, JsonOptions));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return "eval_" + Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }
}
