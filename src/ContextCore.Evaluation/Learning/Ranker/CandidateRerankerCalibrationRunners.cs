using System.Globalization;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>Candidate reranker score 分布审计；只读取 shadow eval，不改变正式排序。</summary>
public sealed class CandidateRerankerScoreDistributionRunner
{
    public const string PolicyVersion = "candidate-reranker-score-distribution-cr1.3/v1";
    public const string DefaultOutputDirectory = "learning/ranker";
    public const string A3ReportFileName = "candidate-reranker-score-distribution-a3.json";
    public const string ExtendedReportFileName = "candidate-reranker-score-distribution-extended.json";
    public const string MarkdownReportFileName = "candidate-reranker-score-distribution.md";
    public const double DefaultLowMarginThreshold = 1.0;

    private readonly CandidateRerankerShadowEvalRunner _shadowRunner;

    public CandidateRerankerScoreDistributionRunner(CandidateRerankerShadowEvalRunner? shadowRunner = null)
    {
        _shadowRunner = shadowRunner ?? new CandidateRerankerShadowEvalRunner();
    }

    public CandidateRerankerScoreDistributionReport Build(
        ContextEvalReport evalReport,
        string datasetName,
        CandidateRerankerShadowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(evalReport);
        var shadowReport = _shadowRunner.Build(evalReport, datasetName, NormalizeOptions(options));
        return BuildFromShadowReport(shadowReport, datasetName);
    }

    public CandidateRerankerScoreDistributionReport BuildFromShadowReport(
        CandidateRerankerShadowEvalReport shadowReport,
        string datasetName,
        double lowMarginThreshold = DefaultLowMarginThreshold)
    {
        ArgumentNullException.ThrowIfNull(shadowReport);
        var scores = shadowReport.SampleResults
            .SelectMany(static sample => sample.Trace.ScoreBreakdown)
            .ToArray();
        var sampleResults = shadowReport.SampleResults
            .Select(sample => BuildSample(sample, lowMarginThreshold))
            .ToArray();
        var scoreValues = scores
            .Select(static score => score.LifecycleAwareScore)
            .ToArray();

        return new CandidateRerankerScoreDistributionReport
        {
            OperationId = $"candidate-reranker-score-distribution-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            DatasetName = datasetName,
            Samples = shadowReport.Samples,
            CandidateCount = scores.Length,
            ScoreMean = Mean(scoreValues),
            ScoreStdDev = StdDev(scoreValues),
            ScoreMin = scoreValues.Length == 0 ? 0 : scoreValues.Min(),
            ScoreMax = scoreValues.Length == 0 ? 0 : scoreValues.Max(),
            Top1MarginAverage = Mean(sampleResults.Select(static sample => sample.Top1Margin)),
            Top1MarginForRegressions = Mean(sampleResults
                .Where(static sample => sample.WouldRegress)
                .Select(static sample => sample.Top1Margin)),
            Top1MarginForImprovements = Mean(sampleResults
                .Where(static sample => sample.WouldImprove)
                .Select(static sample => sample.Top1Margin)),
            ScoreOverlapMustHitVsNonHit = ComputeScoreOverlap(scores),
            FeatureContributionByType = BuildFeatureContribution(scores),
            DominantFeatureCount = sampleResults.Count(static sample => !string.IsNullOrWhiteSpace(sample.DominantFeature)),
            LowMarginDecisionCount = sampleResults.Count(static sample => sample.LowMarginDecision),
            LowMarginThreshold = lowMarginThreshold,
            Recommendation = Recommend(shadowReport, sampleResults),
            SampleResults = sampleResults,
            PolicyVersion = PolicyVersion
        };
    }

    public static string BuildMarkdownReport(
        CandidateRerankerScoreDistributionReport a3,
        CandidateRerankerScoreDistributionReport extended)
    {
        ArgumentNullException.ThrowIfNull(a3);
        ArgumentNullException.ThrowIfNull(extended);
        var builder = new StringBuilder();
        builder.AppendLine("# Candidate Reranker Score Distribution");
        builder.AppendLine();
        builder.AppendLine($"Generated: {DateTimeOffset.UtcNow:O}");
        AppendReport(builder, "A3", a3);
        AppendReport(builder, "Extended", extended);
        return builder.ToString();
    }

    private static CandidateRerankerShadowOptions NormalizeOptions(CandidateRerankerShadowOptions? options)
    {
        return options ?? new CandidateRerankerShadowOptions
        {
            Enabled = false,
            TraceCollectionEnabled = false,
            ShadowRanker = "LifecycleAwareFeatureBaseline",
            MaxCandidatesPerTrace = 50,
            RecordTopK = 10,
            RecordWouldChange = true
        };
    }

    private static CandidateRerankerScoreDistributionSample BuildSample(
        CandidateRerankerShadowEvalSample sample,
        double lowMarginThreshold)
    {
        var scores = sample.Trace.ScoreBreakdown.ToArray();
        var scoreValues = scores.Select(static score => score.LifecycleAwareScore).ToArray();
        var margin = ComputeTop1Margin(scores);
        return new CandidateRerankerScoreDistributionSample
        {
            SampleId = sample.SampleId,
            Mode = sample.Mode,
            Intent = sample.Intent,
            CandidateCount = scores.Length,
            ScoreMean = Mean(scoreValues),
            ScoreStdDev = StdDev(scoreValues),
            ScoreMin = scoreValues.Length == 0 ? 0 : scoreValues.Min(),
            ScoreMax = scoreValues.Length == 0 ? 0 : scoreValues.Max(),
            Top1Margin = margin,
            LowMarginDecision = scores.Length > 1 && margin <= lowMarginThreshold,
            WouldImprove = sample.WouldImprove,
            WouldRegress = sample.WouldRegress,
            DominantFeature = ResolveDominantFeature(scores)
        };
    }

    private static double ComputeTop1Margin(IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> scores)
    {
        var top = scores
            .OrderByDescending(static score => score.LifecycleAwareScore)
            .ThenBy(static score => score.LegacyRank)
            .Take(2)
            .Select(static score => score.LifecycleAwareScore)
            .ToArray();
        return top.Length < 2 ? 0 : top[0] - top[1];
    }

    private static double ComputeScoreOverlap(IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> scores)
    {
        var mustHit = scores.Where(static score => score.IsMustHit).Select(static score => score.LifecycleAwareScore).ToArray();
        var nonHit = scores.Where(static score => !score.IsMustHit).Select(static score => score.LifecycleAwareScore).ToArray();
        if (mustHit.Length == 0 || nonHit.Length == 0)
        {
            return 0;
        }

        var overlapMin = Math.Max(mustHit.Min(), nonHit.Min());
        var overlapMax = Math.Min(mustHit.Max(), nonHit.Max());
        var unionMin = Math.Min(mustHit.Min(), nonHit.Min());
        var unionMax = Math.Max(mustHit.Max(), nonHit.Max());
        var union = unionMax - unionMin;
        if (union <= 0)
        {
            return overlapMax >= overlapMin ? 1 : 0;
        }

        return Math.Max(0, overlapMax - overlapMin) / union;
    }

    private static Dictionary<string, double> BuildFeatureContribution(
        IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> scores)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var score in scores)
        {
            var reasons = SplitReasons(score.Reason)
                .Where(static reason => !string.Equals(reason, "no_lifecycle_adjustment", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (reasons.Length == 0)
            {
                continue;
            }

            var contribution = score.ScoreDelta / reasons.Length;
            foreach (var reason in reasons)
            {
                result[reason] = result.TryGetValue(reason, out var current)
                    ? current + contribution
                    : contribution;
            }
        }

        return result;
    }

    private static string ResolveDominantFeature(
        IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> scores)
    {
        var contributions = BuildFeatureContribution(scores);
        if (contributions.Count == 0)
        {
            return string.Empty;
        }

        var total = contributions.Values.Sum(static value => Math.Abs(value));
        if (total <= 0)
        {
            return string.Empty;
        }

        var dominant = contributions
            .OrderByDescending(static item => Math.Abs(item.Value))
            .First();
        return Math.Abs(dominant.Value) / total >= 0.5 ? dominant.Key : string.Empty;
    }

    private static string Recommend(
        CandidateRerankerShadowEvalReport shadowReport,
        IReadOnlyList<CandidateRerankerScoreDistributionSample> samples)
    {
        if (shadowReport.Samples == 0 || shadowReport.CandidateCount == 0)
        {
            return CandidateRerankerShadowRecommendations.NeedsMoreRealTraces;
        }

        var risk = shadowReport.LifecycleRiskCount + shadowReport.DeprecatedRiskCount + shadowReport.MustNotRiskCount;
        if (risk > 0 || shadowReport.RiskCandidateInShadowTopK > 0)
        {
            return CandidateRerankerShadowRecommendations.BlockedByRisk;
        }

        if (samples.Any(static sample => sample.LowMarginDecision)
            || shadowReport.WouldRegressCount > 0)
        {
            return CandidateRerankerShadowRecommendations.NeedsFeatureTuning;
        }

        return shadowReport.NetGain > 0
            ? CandidateRerankerShadowRecommendations.ReadyForRankerShadow
            : CandidateRerankerShadowRecommendations.KeepFormalRanking;
    }

    private static IReadOnlyList<string> SplitReasons(string reason)
    {
        return string.IsNullOrWhiteSpace(reason)
            ? Array.Empty<string>()
            : reason.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static double Mean(IEnumerable<double> values)
    {
        var materialized = values.ToArray();
        return materialized.Length == 0 ? 0 : materialized.Average();
    }

    private static double StdDev(IEnumerable<double> values)
    {
        var materialized = values.ToArray();
        if (materialized.Length == 0)
        {
            return 0;
        }

        var mean = materialized.Average();
        return Math.Sqrt(materialized.Sum(value => Math.Pow(value - mean, 2)) / materialized.Length);
    }

    private static void AppendReport(
        StringBuilder builder,
        string title,
        CandidateRerankerScoreDistributionReport report)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        builder.AppendLine($"- Samples: `{report.Samples}`");
        builder.AppendLine($"- CandidateCount: `{report.CandidateCount}`");
        builder.AppendLine($"- ScoreMean: `{report.ScoreMean.ToString("0.####", CultureInfo.InvariantCulture)}`");
        builder.AppendLine($"- ScoreStdDev: `{report.ScoreStdDev.ToString("0.####", CultureInfo.InvariantCulture)}`");
        builder.AppendLine($"- ScoreMin: `{report.ScoreMin.ToString("0.####", CultureInfo.InvariantCulture)}`");
        builder.AppendLine($"- ScoreMax: `{report.ScoreMax.ToString("0.####", CultureInfo.InvariantCulture)}`");
        builder.AppendLine($"- Top1MarginAverage: `{report.Top1MarginAverage.ToString("0.####", CultureInfo.InvariantCulture)}`");
        builder.AppendLine($"- Top1MarginForRegressions: `{report.Top1MarginForRegressions.ToString("0.####", CultureInfo.InvariantCulture)}`");
        builder.AppendLine($"- Top1MarginForImprovements: `{report.Top1MarginForImprovements.ToString("0.####", CultureInfo.InvariantCulture)}`");
        builder.AppendLine($"- ScoreOverlapMustHitVsNonHit: `{report.ScoreOverlapMustHitVsNonHit.ToString("0.####", CultureInfo.InvariantCulture)}`");
        builder.AppendLine($"- DominantFeatureCount: `{report.DominantFeatureCount}`");
        builder.AppendLine($"- LowMarginDecisionCount: `{report.LowMarginDecisionCount}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("### Feature Contribution By Type");
        if (report.FeatureContributionByType.Count == 0)
        {
            builder.AppendLine("- none");
        }
        else
        {
            foreach (var pair in report.FeatureContributionByType
                         .OrderByDescending(static item => Math.Abs(item.Value))
                         .ThenBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
                         .Take(12))
            {
                builder.AppendLine($"- `{pair.Key}`: `{pair.Value.ToString("0.####", CultureInfo.InvariantCulture)}`");
            }
        }
    }
}

/// <summary>Candidate reranker listwise calibration 审计；只读比较 formal priority 与 shadow 排序。</summary>
public sealed class CandidateRerankerListwiseCalibrationRunner
{
    public const string PolicyVersion = "candidate-reranker-listwise-calibration-cr1.3/v1";
    public const string DefaultOutputDirectory = "learning/ranker";
    public const string A3ReportFileName = "candidate-reranker-listwise-calibration-a3.json";
    public const string ExtendedReportFileName = "candidate-reranker-listwise-calibration-extended.json";
    public const string MarkdownReportFileName = "candidate-reranker-listwise-calibration.md";
    public const double LowMarginThreshold = CandidateRerankerScoreDistributionRunner.DefaultLowMarginThreshold;
    public const double SharpMarginThreshold = 25.0;

    private readonly CandidateRerankerShadowEvalRunner _shadowRunner;

    public CandidateRerankerListwiseCalibrationRunner(CandidateRerankerShadowEvalRunner? shadowRunner = null)
    {
        _shadowRunner = shadowRunner ?? new CandidateRerankerShadowEvalRunner();
    }

    public CandidateRerankerListwiseCalibrationReport Build(
        ContextEvalReport evalReport,
        string datasetName,
        CandidateRerankerShadowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(evalReport);
        var shadowReport = _shadowRunner.Build(evalReport, datasetName, NormalizeOptions(options));
        return BuildFromShadowReport(shadowReport, datasetName);
    }

    public CandidateRerankerListwiseCalibrationReport BuildFromShadowReport(
        CandidateRerankerShadowEvalReport shadowReport,
        string datasetName)
    {
        ArgumentNullException.ThrowIfNull(shadowReport);
        var samples = shadowReport.SampleResults
            .Select(BuildSample)
            .ToArray();
        var issueCounts = samples
            .GroupBy(static sample => sample.CalibrationIssue, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var reasonSummary = samples
            .Where(static sample => !string.IsNullOrWhiteSpace(sample.RegressionReason))
            .GroupBy(static sample => sample.RegressionReason, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return new CandidateRerankerListwiseCalibrationReport
        {
            OperationId = $"candidate-reranker-listwise-calibration-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            DatasetName = datasetName,
            Samples = shadowReport.Samples,
            CandidateCount = samples.Sum(static sample => sample.CandidateCount),
            MustHitCandidateCount = samples.Sum(static sample => sample.MustHitCandidateCount),
            RegressionCount = samples.Count(static sample => !string.Equals(sample.CalibrationIssue, CandidateRerankerCalibrationIssues.KeepFormalRanking, StringComparison.OrdinalIgnoreCase)),
            LowMarginDecisionCount = samples.Count(static sample => string.Equals(sample.CalibrationIssue, CandidateRerankerCalibrationIssues.LowMarginAmbiguity, StringComparison.OrdinalIgnoreCase)),
            FormalPriorityMismatchCount = samples.Count(static sample => sample.FormalPriorityComparison.HasMismatch),
            AverageTop1Margin = Mean(samples.Select(static sample => sample.Top1Margin)),
            AverageTopKOverlap = Mean(samples.Select(static sample => sample.TopKOverlap)),
            FormalOutputChanged = false,
            CalibrationIssueCounts = issueCounts,
            RegressionReasonSummary = reasonSummary,
            Recommendation = Recommend(shadowReport, issueCounts),
            SampleResults = samples,
            PolicyVersion = PolicyVersion
        };
    }

    public static string BuildMarkdownReport(
        CandidateRerankerListwiseCalibrationReport a3,
        CandidateRerankerListwiseCalibrationReport extended)
    {
        ArgumentNullException.ThrowIfNull(a3);
        ArgumentNullException.ThrowIfNull(extended);
        var builder = new StringBuilder();
        builder.AppendLine("# Candidate Reranker Listwise Calibration");
        builder.AppendLine();
        builder.AppendLine($"Generated: {DateTimeOffset.UtcNow:O}");
        AppendReport(builder, "A3", a3);
        AppendReport(builder, "Extended", extended);
        return builder.ToString();
    }

    private static CandidateRerankerShadowOptions NormalizeOptions(CandidateRerankerShadowOptions? options)
    {
        return options ?? new CandidateRerankerShadowOptions
        {
            Enabled = false,
            TraceCollectionEnabled = false,
            ShadowRanker = "LifecycleAwareFeatureBaseline",
            MaxCandidatesPerTrace = 50,
            RecordTopK = 10,
            RecordWouldChange = true
        };
    }

    private static CandidateRerankerListwiseCalibrationSample BuildSample(CandidateRerankerShadowEvalSample sample)
    {
        var scores = sample.Trace.ScoreBreakdown.ToArray();
        var formalTop = sample.Trace.FormalTopCandidates.FirstOrDefault();
        var shadowTop = sample.Trace.ShadowTopCandidates.FirstOrDefault();
        var formalTopScore = FindScore(scores, formalTop?.CandidateId);
        var shadowTopScore = FindScore(scores, shadowTop?.CandidateId);
        var priority = BuildFormalPriorityComparison(formalTopScore, shadowTopScore);
        var top1Margin = ComputeTop1Margin(scores);
        var regressionReason = ResolveRegressionReason(sample, top1Margin, priority);
        var issue = ResolveCalibrationIssue(sample, top1Margin, priority, regressionReason);

        return new CandidateRerankerListwiseCalibrationSample
        {
            SampleId = sample.SampleId,
            Mode = sample.Mode,
            Intent = sample.Intent,
            CandidateCount = scores.Length,
            MustHitCandidateCount = scores.Count(static score => score.IsMustHit),
            FormalTop1 = formalTop?.CandidateId ?? sample.FormalTopCandidateId,
            ShadowTop1 = shadowTop?.CandidateId ?? sample.ShadowTopCandidateId,
            MustHitBestRankFormal = ResolveBestMustHitRank(sample.Trace.FormalTopCandidates),
            MustHitBestRankShadow = ResolveBestMustHitRank(sample.Trace.ShadowTopCandidates),
            Top1Margin = top1Margin,
            TopKOverlap = ComputeTopKOverlap(sample.Trace.FormalTopCandidates, sample.Trace.ShadowTopCandidates),
            RegressionReason = regressionReason,
            CalibrationIssue = issue,
            RecommendedAction = ResolveRecommendedAction(issue),
            FormalPriorityComparison = priority
        };
    }

    private static string ResolveRegressionReason(
        CandidateRerankerShadowEvalSample sample,
        double top1Margin,
        CandidateRerankerFormalPriorityComparison priority)
    {
        if (!sample.WouldRegress)
        {
            return string.Empty;
        }

        if (sample.FormalTop1Correct && !sample.ShadowTop1Correct)
        {
            return CandidateRerankerRegressionReasons.PairwiseToListwiseMismatch;
        }

        if (top1Margin <= LowMarginThreshold)
        {
            return CandidateRerankerCalibrationIssues.LowMarginAmbiguity;
        }

        if (priority.HasMismatch)
        {
            return CandidateRerankerCalibrationIssues.FormalRankingHasImplicitPriority;
        }

        if (sample.MissingFeatureMetadataCount > 0)
        {
            return CandidateRerankerRegressionReasons.MissingFeatureMetadata;
        }

        if (sample.ShadowMrr + double.Epsilon < sample.FormalMrr)
        {
            return CandidateRerankerRegressionReasons.ScoreScaleMismatch;
        }

        return CandidateRerankerRegressionReasons.RequiresFeatureTuning;
    }

    private static string ResolveCalibrationIssue(
        CandidateRerankerShadowEvalSample sample,
        double top1Margin,
        CandidateRerankerFormalPriorityComparison priority,
        string regressionReason)
    {
        if (!sample.WouldRegress)
        {
            return CandidateRerankerCalibrationIssues.KeepFormalRanking;
        }

        if (top1Margin <= LowMarginThreshold)
        {
            return CandidateRerankerCalibrationIssues.LowMarginAmbiguity;
        }

        if (sample.FormalTop1Correct && !sample.ShadowTop1Correct)
        {
            return CandidateRerankerCalibrationIssues.PairwiseToListwiseMismatch;
        }

        if (priority.HasMismatch)
        {
            return CandidateRerankerCalibrationIssues.FormalRankingHasImplicitPriority;
        }

        if (string.IsNullOrWhiteSpace(sample.Intent)
            || string.Equals(sample.Intent, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return CandidateRerankerCalibrationIssues.MissingIntentFeature;
        }

        if (string.IsNullOrWhiteSpace(sample.Mode))
        {
            return CandidateRerankerCalibrationIssues.MissingModeFeature;
        }

        if (top1Margin >= SharpMarginThreshold)
        {
            return CandidateRerankerCalibrationIssues.ScoreScaleTooSharp;
        }

        if (string.Equals(regressionReason, CandidateRerankerRegressionReasons.ScoreScaleMismatch, StringComparison.OrdinalIgnoreCase))
        {
            return CandidateRerankerCalibrationIssues.ScoreScaleTooFlat;
        }

        return CandidateRerankerCalibrationIssues.RequiresFeatureCalibration;
    }

    private static CandidateRerankerFormalPriorityComparison BuildFormalPriorityComparison(
        LifecycleAwareRankerShadowCandidateScore? formalTop,
        LifecycleAwareRankerShadowCandidateScore? shadowTop)
    {
        return new FormalPriorityFeatureExtractor().Compare(formalTop, shadowTop);
    }

    private static string MetadataSurface(LifecycleAwareRankerShadowCandidateScore score)
    {
        return string.Join(' ', score.Kind, score.Type, score.SectionName, score.Reason);
    }

    private static int LayerScore(string surface)
    {
        if (ContainsToken(surface, "constraint"))
        {
            return 5;
        }

        if (ContainsToken(surface, "working"))
        {
            return 4;
        }

        if (ContainsToken(surface, "stable"))
        {
            return 3;
        }

        if (ContainsToken(surface, "candidate"))
        {
            return 2;
        }

        return 1;
    }

    private static bool ContainsToken(string value, string token)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeText(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private static LifecycleAwareRankerShadowCandidateScore? FindScore(
        IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> scores,
        string? candidateId)
    {
        return string.IsNullOrWhiteSpace(candidateId)
            ? null
            : scores.FirstOrDefault(score => string.Equals(score.CandidateId, candidateId, StringComparison.OrdinalIgnoreCase));
    }

    private static int ResolveBestMustHitRank(IReadOnlyList<CandidateRerankerShadowCandidateRef> candidates)
    {
        return candidates
            .Where(static candidate => candidate.IsMustHit)
            .Select(static candidate => candidate.Rank)
            .DefaultIfEmpty(0)
            .Min();
    }

    private static double ComputeTop1Margin(IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> scores)
    {
        var top = scores
            .OrderByDescending(static score => score.LifecycleAwareScore)
            .ThenBy(static score => score.LegacyRank)
            .Take(2)
            .Select(static score => score.LifecycleAwareScore)
            .ToArray();
        return top.Length < 2 ? 0 : top[0] - top[1];
    }

    private static double ComputeTopKOverlap(
        IReadOnlyList<CandidateRerankerShadowCandidateRef> formal,
        IReadOnlyList<CandidateRerankerShadowCandidateRef> shadow)
    {
        var formalIds = formal.Select(static item => item.CandidateId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shadowIds = shadow.Select(static item => item.CandidateId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var total = Math.Max(formalIds.Count, shadowIds.Count);
        if (total == 0)
        {
            return 0;
        }

        formalIds.IntersectWith(shadowIds);
        return (double)formalIds.Count / total;
    }

    private static string ResolveRecommendedAction(string issue)
    {
        return issue switch
        {
            CandidateRerankerCalibrationIssues.PairwiseToListwiseMismatch =>
                "Audit pairwise baseline against topK listwise objective before opt-in.",
            CandidateRerankerCalibrationIssues.ScoreScaleTooFlat =>
                "Inspect score normalization; flat scores should stay shadow-only.",
            CandidateRerankerCalibrationIssues.ScoreScaleTooSharp =>
                "Inspect dominant feature weights before any guarded opt-in.",
            CandidateRerankerCalibrationIssues.LowMarginAmbiguity =>
                "Keep formal ranking and collect more listwise labels for low-margin cases.",
            CandidateRerankerCalibrationIssues.DominantPenaltyOverpowersRelevance =>
                "Audit lifecycle penalty contribution before feature tuning.",
            CandidateRerankerCalibrationIssues.MissingIntentFeature =>
                "Add intent feature coverage to offline ranker input before tuning.",
            CandidateRerankerCalibrationIssues.MissingModeFeature =>
                "Add mode feature coverage to offline ranker input before tuning.",
            CandidateRerankerCalibrationIssues.FormalRankingHasImplicitPriority =>
                "Model formal package priority features before comparing shadow rank.",
            CandidateRerankerCalibrationIssues.RequiresFeatureCalibration =>
                "Keep shadow-only and calibrate features offline.",
            _ => "Keep formal ranking."
        };
    }

    private static string Recommend(
        CandidateRerankerShadowEvalReport shadowReport,
        IReadOnlyDictionary<string, int> issueCounts)
    {
        if (shadowReport.Samples == 0)
        {
            return CandidateRerankerShadowRecommendations.NeedsMoreRealTraces;
        }

        var risk = shadowReport.LifecycleRiskCount + shadowReport.DeprecatedRiskCount + shadowReport.MustNotRiskCount;
        if (risk > 0 || shadowReport.RiskCandidateInShadowTopK > 0)
        {
            return CandidateRerankerShadowRecommendations.BlockedByRisk;
        }

        if (issueCounts.Any(pair =>
                !string.Equals(pair.Key, CandidateRerankerCalibrationIssues.KeepFormalRanking, StringComparison.OrdinalIgnoreCase)))
        {
            return CandidateRerankerShadowRecommendations.NeedsFeatureTuning;
        }

        return shadowReport.NetGain > 0
            ? CandidateRerankerShadowRecommendations.ReadyForRankerShadow
            : CandidateRerankerShadowRecommendations.KeepFormalRanking;
    }

    private static double Mean(IEnumerable<double> values)
    {
        var materialized = values.ToArray();
        return materialized.Length == 0 ? 0 : materialized.Average();
    }

    private static void AppendReport(
        StringBuilder builder,
        string title,
        CandidateRerankerListwiseCalibrationReport report)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        builder.AppendLine($"- Samples: `{report.Samples}`");
        builder.AppendLine($"- CandidateCount: `{report.CandidateCount}`");
        builder.AppendLine($"- MustHitCandidateCount: `{report.MustHitCandidateCount}`");
        builder.AppendLine($"- RegressionCount: `{report.RegressionCount}`");
        builder.AppendLine($"- LowMarginDecisionCount: `{report.LowMarginDecisionCount}`");
        builder.AppendLine($"- FormalPriorityMismatchCount: `{report.FormalPriorityMismatchCount}`");
        builder.AppendLine($"- AverageTop1Margin: `{report.AverageTop1Margin.ToString("0.####", CultureInfo.InvariantCulture)}`");
        builder.AppendLine($"- AverageTopKOverlap: `{report.AverageTopKOverlap.ToString("0.####", CultureInfo.InvariantCulture)}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("### Calibration Issues");
        foreach (var pair in report.CalibrationIssueCounts
                     .OrderByDescending(static item => item.Value)
                     .ThenBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- `{pair.Key}`: `{pair.Value}`");
        }

        builder.AppendLine();
        builder.AppendLine("| Sample | Mode | Intent | FormalTop1 | ShadowTop1 | FormalBest | ShadowBest | Margin | Overlap | Issue | Action |");
        builder.AppendLine("|---|---|---|---|---|---:|---:|---:|---:|---|---|");
        foreach (var sample in report.SampleResults
                     .Where(static sample => !string.Equals(sample.CalibrationIssue, CandidateRerankerCalibrationIssues.KeepFormalRanking, StringComparison.OrdinalIgnoreCase))
                     .Take(40))
        {
            builder.AppendLine($"| `{sample.SampleId}` | `{sample.Mode}` | `{sample.Intent}` | `{sample.FormalTop1}` | `{sample.ShadowTop1}` | {sample.MustHitBestRankFormal} | {sample.MustHitBestRankShadow} | {sample.Top1Margin:0.####} | {sample.TopKOverlap:0.####} | `{sample.CalibrationIssue}` | {sample.RecommendedAction} |");
        }
    }
}
