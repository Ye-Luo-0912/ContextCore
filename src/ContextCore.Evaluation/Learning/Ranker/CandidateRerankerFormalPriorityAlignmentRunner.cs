using System.Globalization;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>Formal priority feature alignment 离线审计；只做 shadow 对照，不改变正式排序。</summary>
public sealed class CandidateRerankerFormalPriorityAlignmentRunner
{
    public const string PolicyVersion = "candidate-reranker-formal-priority-alignment-cr1.4/v1";
    public const string DefaultOutputDirectory = "learning/ranker";
    public const string A3ReportFileName = "candidate-reranker-formal-priority-alignment-a3.json";
    public const string ExtendedReportFileName = "candidate-reranker-formal-priority-alignment-extended.json";
    public const string MarkdownReportFileName = "candidate-reranker-formal-priority-alignment.md";

    private readonly CandidateRerankerShadowEvalRunner _shadowRunner;
    private readonly FormalPriorityFeatureExtractor _featureExtractor;

    public CandidateRerankerFormalPriorityAlignmentRunner(
        CandidateRerankerShadowEvalRunner? shadowRunner = null,
        FormalPriorityFeatureExtractor? featureExtractor = null)
    {
        _featureExtractor = featureExtractor ?? new FormalPriorityFeatureExtractor();
        _shadowRunner = shadowRunner ?? new CandidateRerankerShadowEvalRunner(formalPriorityFeatureExtractor: _featureExtractor);
    }

    public CandidateRerankerFormalPriorityAlignmentReport Build(
        ContextEvalReport evalReport,
        string datasetName,
        CandidateRerankerShadowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(evalReport);
        var baseline = _shadowRunner.Build(
            evalReport,
            datasetName,
            WithProfile(options, CandidateRerankerShadowProfiles.BaselineLifecycleAware));
        var formalPriority = _shadowRunner.Build(
            evalReport,
            datasetName,
            WithProfile(options, CandidateRerankerShadowProfiles.FormalPriorityAwareV1));
        var withAbstain = _shadowRunner.Build(
            evalReport,
            datasetName,
            WithProfile(options, CandidateRerankerShadowProfiles.FormalPriorityAwareWithAbstainV1));

        return BuildFromShadowReports(baseline, formalPriority, withAbstain, datasetName);
    }

    public CandidateRerankerFormalPriorityAlignmentReport BuildFromShadowReports(
        CandidateRerankerShadowEvalReport baseline,
        CandidateRerankerShadowEvalReport formalPriority,
        CandidateRerankerShadowEvalReport withAbstain,
        string datasetName)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(formalPriority);
        ArgumentNullException.ThrowIfNull(withAbstain);

        var formalBySample = formalPriority.SampleResults
            .GroupBy(static sample => sample.SampleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        var abstainBySample = withAbstain.SampleResults
            .GroupBy(static sample => sample.SampleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        var samples = baseline.SampleResults
            .Select(sample => BuildSample(
                sample,
                formalBySample.TryGetValue(sample.SampleId, out var formalSample) ? formalSample : null,
                abstainBySample.TryGetValue(sample.SampleId, out var abstainSample) ? abstainSample : null))
            .ToArray();
        var recovered = samples.Where(static sample => sample.Recovered).ToArray();
        var risk = formalPriority.LifecycleRiskCount + formalPriority.DeprecatedRiskCount + formalPriority.MustNotRiskCount;

        return new CandidateRerankerFormalPriorityAlignmentReport
        {
            OperationId = $"candidate-reranker-formal-priority-alignment-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            DatasetName = datasetName,
            Samples = baseline.Samples,
            RegressionCount = baseline.WouldRegressCount,
            FormalPriorityMismatchCount = samples.Count(static sample => sample.FormalPriorityComparison.HasMismatch),
            RecoveredCount = recovered.Length,
            RecoveredByLayerPriority = recovered.Count(static sample => sample.RecoveredFeatureSet.LayerPriority > 0),
            RecoveredBySourcePriority = recovered.Count(static sample => sample.RecoveredFeatureSet.SourcePriority > 0),
            RecoveredByCurrentTaskBoost = recovered.Count(static sample => sample.RecoveredFeatureSet.CurrentTaskBoost > 0),
            RecoveredByConstraintRelevance = recovered.Count(static sample => sample.RecoveredFeatureSet.ConstraintRelevance > 0),
            RecoveredByStableMemoryBias = recovered.Count(static sample => sample.RecoveredFeatureSet.StableMemoryBias > 0),
            UnexplainedMismatchCount = samples.Count(static sample => sample.UnexplainedMismatch),
            AbstainCount = withAbstain.AbstainCount,
            NetGainAfterAbstain = withAbstain.NetGainAfterAbstain,
            Recommendation = Recommend(
                baseline.WouldRegressCount,
                recovered.Length,
                samples.Count(static sample => sample.UnexplainedMismatch),
                risk),
            SampleResults = samples,
            PolicyVersion = PolicyVersion
        };
    }

    public static string BuildMarkdownReport(
        CandidateRerankerFormalPriorityAlignmentReport a3,
        CandidateRerankerFormalPriorityAlignmentReport extended)
    {
        ArgumentNullException.ThrowIfNull(a3);
        ArgumentNullException.ThrowIfNull(extended);
        var builder = new StringBuilder();
        builder.AppendLine("# Candidate Reranker Formal Priority Alignment");
        builder.AppendLine();
        builder.AppendLine($"Generated: {DateTimeOffset.UtcNow:O}");
        AppendReport(builder, "A3", a3);
        AppendReport(builder, "Extended", extended);
        return builder.ToString();
    }

    private CandidateRerankerFormalPriorityAlignmentSample BuildSample(
        CandidateRerankerShadowEvalSample baseline,
        CandidateRerankerShadowEvalSample? formalPriority,
        CandidateRerankerShadowEvalSample? withAbstain)
    {
        var formalTop = FindScore(baseline.Trace.ScoreBreakdown, baseline.FormalTopCandidateId);
        var baselineShadowTop = FindScore(baseline.Trace.ScoreBreakdown, baseline.ShadowTopCandidateId);
        var comparison = _featureExtractor.Compare(formalTop, baselineShadowTop);
        var recovered = baseline.WouldRegress && formalPriority is not null && !formalPriority.WouldRegress;
        var abstained = withAbstain?.Abstained == true;
        var unexplained = baseline.WouldRegress && !recovered && !abstained;

        return new CandidateRerankerFormalPriorityAlignmentSample
        {
            SampleId = baseline.SampleId,
            Mode = baseline.Mode,
            Intent = baseline.Intent,
            BaselineShadowTop1 = baseline.ShadowTopCandidateId,
            FormalPriorityShadowTop1 = formalPriority?.ShadowTopCandidateId ?? string.Empty,
            FormalTop1 = baseline.FormalTopCandidateId,
            BaselineRegressed = baseline.WouldRegress,
            Recovered = recovered,
            Abstained = abstained,
            UnexplainedMismatch = unexplained,
            BaselineShadowMrr = baseline.ShadowMrr,
            FormalPriorityShadowMrr = formalPriority?.ShadowMrr ?? 0,
            FormalMrr = baseline.FormalMrr,
            RecoveredFeatureSet = recovered ? comparison.FormalTopFeatures : new CandidateRerankerFormalPriorityFeatureSet(),
            FormalPriorityComparison = comparison,
            RecommendedAction = ResolveRecommendedAction(recovered, abstained, unexplained)
        };
    }

    private static CandidateRerankerShadowOptions WithProfile(
        CandidateRerankerShadowOptions? options,
        string profile)
    {
        var source = options ?? new CandidateRerankerShadowOptions();
        return new CandidateRerankerShadowOptions
        {
            Enabled = source.Enabled,
            TraceCollectionEnabled = source.TraceCollectionEnabled,
            ShadowRanker = source.ShadowRanker,
            ShadowProfile = profile,
            MaxCandidatesPerTrace = source.MaxCandidatesPerTrace,
            RecordTopK = source.RecordTopK,
            RecordWouldChange = source.RecordWouldChange
        };
    }

    private static LifecycleAwareRankerShadowCandidateScore? FindScore(
        IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> scores,
        string candidateId)
    {
        return string.IsNullOrWhiteSpace(candidateId)
            ? null
            : scores.FirstOrDefault(score => string.Equals(score.CandidateId, candidateId, StringComparison.OrdinalIgnoreCase));
    }

    private static string Recommend(
        int regressionCount,
        int recoveredCount,
        int unexplainedCount,
        int risk)
    {
        if (risk > 0)
        {
            return CandidateRerankerShadowRecommendations.BlockedByRisk;
        }

        if (regressionCount == 0)
        {
            return CandidateRerankerShadowRecommendations.ReadyForRankerShadow;
        }

        if (recoveredCount > 0 || unexplainedCount > 0)
        {
            return CandidateRerankerShadowRecommendations.NeedsFeatureTuning;
        }

        return CandidateRerankerShadowRecommendations.KeepFormalRanking;
    }

    private static string ResolveRecommendedAction(bool recovered, bool abstained, bool unexplained)
    {
        if (recovered)
        {
            return "Keep formal-priority-aware profile in shadow analysis and collect listwise evidence before opt-in.";
        }

        if (abstained)
        {
            return "Keep formal ranking for low-margin decisions; abstain before any guarded opt-in.";
        }

        if (unexplained)
        {
            return "Keep formal ranking and add missing mode / intent / package priority features offline.";
        }

        return "Keep formal ranking.";
    }

    private static void AppendReport(
        StringBuilder builder,
        string title,
        CandidateRerankerFormalPriorityAlignmentReport report)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        builder.AppendLine($"- Samples: `{report.Samples}`");
        builder.AppendLine($"- RegressionCount: `{report.RegressionCount}`");
        builder.AppendLine($"- FormalPriorityMismatchCount: `{report.FormalPriorityMismatchCount}`");
        builder.AppendLine($"- RecoveredCount: `{report.RecoveredCount}`");
        builder.AppendLine($"- RecoveredByLayerPriority: `{report.RecoveredByLayerPriority}`");
        builder.AppendLine($"- RecoveredBySourcePriority: `{report.RecoveredBySourcePriority}`");
        builder.AppendLine($"- RecoveredByCurrentTaskBoost: `{report.RecoveredByCurrentTaskBoost}`");
        builder.AppendLine($"- RecoveredByConstraintRelevance: `{report.RecoveredByConstraintRelevance}`");
        builder.AppendLine($"- RecoveredByStableMemoryBias: `{report.RecoveredByStableMemoryBias}`");
        builder.AppendLine($"- UnexplainedMismatchCount: `{report.UnexplainedMismatchCount}`");
        builder.AppendLine($"- AbstainCount: `{report.AbstainCount}`");
        builder.AppendLine($"- NetGainAfterAbstain: `{report.NetGainAfterAbstain}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("| Sample | Mode | Intent | FormalTop | BaselineShadowTop | FormalPriorityTop | Recovered | Abstain | Unexplained | Action |");
        builder.AppendLine("|---|---|---|---|---|---|---:|---:|---:|---|");
        foreach (var sample in report.SampleResults
                     .Where(static sample => sample.BaselineRegressed || sample.Recovered || sample.Abstained || sample.UnexplainedMismatch)
                     .Take(40))
        {
            builder.AppendLine($"| `{sample.SampleId}` | `{sample.Mode}` | `{sample.Intent}` | `{sample.FormalTop1}` | `{sample.BaselineShadowTop1}` | `{sample.FormalPriorityShadowTop1}` | {sample.Recovered} | {sample.Abstained} | {sample.UnexplainedMismatch} | {sample.RecommendedAction} |");
        }
    }
}
