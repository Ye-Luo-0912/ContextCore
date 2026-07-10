using System.Globalization;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>Candidate reranker 特征完整性离线报告；只读分析，不改变正式输出。</summary>
public sealed class CandidateRerankerFeatureCompletenessRunner
{
    public const string PolicyVersion = "candidate-reranker-feature-completeness-cr1.2/v1";
    public const string DefaultOutputDirectory = "learning/ranker";
    public const string A3ReportFileName = "candidate-reranker-feature-completeness-a3.json";
    public const string ExtendedReportFileName = "candidate-reranker-feature-completeness-extended.json";
    public const string MarkdownReportFileName = "candidate-reranker-feature-completeness.md";

    private readonly CandidateFeatureEnvelopeBuilder _envelopeBuilder;
    private readonly RankerCandidateEligibilityGuard _eligibilityGuard;

    public CandidateRerankerFeatureCompletenessRunner(
        CandidateFeatureEnvelopeBuilder? envelopeBuilder = null,
        RankerCandidateEligibilityGuard? eligibilityGuard = null)
    {
        _envelopeBuilder = envelopeBuilder ?? new CandidateFeatureEnvelopeBuilder();
        _eligibilityGuard = eligibilityGuard ?? new RankerCandidateEligibilityGuard();
    }

    public CandidateRerankerFeatureCompletenessReport Build(
        ContextEvalReport evalReport,
        string datasetName)
    {
        ArgumentNullException.ThrowIfNull(evalReport);
        var samples = evalReport.Results
            .Select(BuildSample)
            .ToArray();
        var decisions = samples
            .SelectMany(static sample => sample.Decisions)
            .ToArray();
        var blockedReasonCounts = decisions
            .SelectMany(static decision => decision.BlockedReasons)
            .GroupBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return new CandidateRerankerFeatureCompletenessReport
        {
            OperationId = $"candidate-reranker-feature-completeness-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            DatasetName = datasetName,
            Samples = samples.Length,
            RawCandidateCount = samples.Sum(static sample => sample.RawCandidateCount),
            RankableCandidateCount = samples.Sum(static sample => sample.RankableCandidateCount),
            BlockedCandidateCount = samples.Sum(static sample => sample.BlockedCandidateCount),
            AuditOnlyCandidateCount = samples.Sum(static sample => sample.AuditOnlyCandidateCount),
            DiagnosticsOnlyCandidateCount = samples.Sum(static sample => sample.DiagnosticsOnlyCandidateCount),
            FeatureCompletenessRate = Average(samples.Select(static sample => sample.FeatureCompletenessRate)),
            MissingFeatureMetadataCount = samples.Sum(static sample => sample.MissingFeatureMetadataCount),
            MissingLifecycleMetadataCount = CountReason(decisions, CandidateRerankerBlockedReasons.MissingLifecycleMetadata),
            MissingReviewStatusCount = CountReason(decisions, CandidateRerankerBlockedReasons.MissingReviewStatus),
            MissingProvenanceCount = CountReason(decisions, CandidateRerankerBlockedReasons.MissingProvenance),
            MissingReplacementMetadataCount = CountReason(decisions, CandidateRerankerBlockedReasons.MissingReplacementMetadata),
            RiskCandidateBlockedBeforeRerank = samples.Sum(static sample => sample.RiskCandidateBlockedBeforeRerank),
            BlockedReasonCounts = blockedReasonCounts,
            EligibilityGuardStatus = RankerCandidateEligibilityGuard.ResolveGuardStatus(decisions),
            Recommendation = Recommend(samples, decisions),
            SampleResults = samples,
            PolicyVersion = PolicyVersion
        };
    }

    public static string BuildMarkdownReport(
        CandidateRerankerFeatureCompletenessReport a3,
        CandidateRerankerFeatureCompletenessReport extended)
    {
        ArgumentNullException.ThrowIfNull(a3);
        ArgumentNullException.ThrowIfNull(extended);

        var builder = new StringBuilder();
        builder.AppendLine("# Candidate Reranker Feature Completeness");
        builder.AppendLine();
        builder.AppendLine($"Generated: {DateTimeOffset.UtcNow:O}");
        AppendReport(builder, "A3", a3);
        AppendReport(builder, "Extended", extended);
        return builder.ToString();
    }

    private CandidateRerankerFeatureCompletenessSample BuildSample(ContextEvalResult result)
    {
        var raw = ResolveSelectedDiagnostics(result)
            .Concat(ResolveDroppedDiagnostics(result, result.SelectedItemDiagnostics.Count > 0
                ? result.SelectedItemDiagnostics.Count
                : result.SelectedIds.Count))
            .ToArray();
        var envelopes = _envelopeBuilder.BuildMany(raw);
        var decisions = _eligibilityGuard.Evaluate(envelopes);

        return new CandidateRerankerFeatureCompletenessSample
        {
            SampleId = result.SampleId,
            Mode = result.Mode,
            Intent = ResolveIntent(result),
            RawCandidateCount = raw.Length,
            RankableCandidateCount = decisions.Count(RankerCandidateEligibilityGuard.IsRankable),
            BlockedCandidateCount = decisions.Count(static decision =>
                string.Equals(decision.Status, CandidateRerankerEligibilityStatuses.Blocked, StringComparison.OrdinalIgnoreCase)),
            AuditOnlyCandidateCount = decisions.Count(static decision =>
                string.Equals(decision.Status, CandidateRerankerEligibilityStatuses.AuditOnly, StringComparison.OrdinalIgnoreCase)),
            DiagnosticsOnlyCandidateCount = decisions.Count(static decision =>
                string.Equals(decision.Status, CandidateRerankerEligibilityStatuses.DiagnosticsOnly, StringComparison.OrdinalIgnoreCase)),
            FeatureCompletenessRate = Average(decisions.Select(static decision => decision.Envelope.FeatureCompleteness)),
            MissingFeatureMetadataCount = decisions.Count(static decision =>
                decision.BlockedReasons.Contains(CandidateRerankerBlockedReasons.MissingLifecycleMetadata, StringComparer.OrdinalIgnoreCase)
                || decision.BlockedReasons.Contains(CandidateRerankerBlockedReasons.IncompleteFeatureEnvelope, StringComparer.OrdinalIgnoreCase)),
            RiskCandidateBlockedBeforeRerank = decisions.Count(RankerCandidateEligibilityGuard.IsNonRankableRisk),
            Decisions = decisions
        };
    }

    private static IReadOnlyList<ContextEvalItemDiagnostic> ResolveSelectedDiagnostics(ContextEvalResult result)
    {
        if (result.SelectedItemDiagnostics.Count > 0)
        {
            return [.. result.SelectedItemDiagnostics.Select((item, index) => EnsureRank(item, index + 1))];
        }

        return [.. result.SelectedIds
            .Select((id, index) => new ContextEvalItemDiagnostic
            {
                ItemId = id,
                Rank = index + 1,
                IsMustHit = result.MustHit.Any(mustHit => IsId(mustHit, id)),
                IsMustNotHit = result.MustNotHit.Any(mustNotHit => IsId(mustNotHit, id))
            })];
    }

    private static IReadOnlyList<ContextEvalItemDiagnostic> ResolveDroppedDiagnostics(
        ContextEvalResult result,
        int selectedCount)
    {
        if (result.DroppedItemDiagnostics.Count > 0)
        {
            return [.. result.DroppedItemDiagnostics.Select((item, index) => EnsureRank(item, selectedCount + index + 1))];
        }

        return [.. result.ExcludedIds
            .Select((id, index) => new ContextEvalItemDiagnostic
            {
                ItemId = id,
                Rank = selectedCount + index + 1,
                IsMustHit = result.MustHit.Any(mustHit => IsId(mustHit, id)),
                IsMustNotHit = result.MustNotHit.Any(mustNotHit => IsId(mustNotHit, id))
            })];
    }

    private static ContextEvalItemDiagnostic EnsureRank(ContextEvalItemDiagnostic item, int fallbackRank)
    {
        return item.Rank > 0
            ? item
            : new ContextEvalItemDiagnostic
            {
                ItemId = item.ItemId,
                Kind = item.Kind,
                Type = item.Type,
                SectionName = item.SectionName,
                Reason = item.Reason,
                Score = item.Score,
                EstimatedTokens = item.EstimatedTokens,
                Rank = fallbackRank,
                IsMustHit = item.IsMustHit,
                IsMustNotHit = item.IsMustNotHit,
                SourceRefs = item.SourceRefs
            };
    }

    private static string ResolveIntent(ContextEvalResult result)
    {
        return result.PackageMetadata.TryGetValue("planningIntent", out var intent) && !string.IsNullOrWhiteSpace(intent)
            ? intent
            : "Unknown";
    }

    private static bool IsId(string expected, string actual)
    {
        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)
            || actual.Contains(expected, StringComparison.OrdinalIgnoreCase)
            || expected.Contains(actual, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountReason(
        IReadOnlyList<RankerCandidateEligibilityDecision> decisions,
        string reason)
    {
        return decisions.Count(decision => decision.BlockedReasons.Contains(reason, StringComparer.OrdinalIgnoreCase));
    }

    private static double Average(IEnumerable<double> values)
    {
        var materialized = values.ToArray();
        return materialized.Length == 0 ? 0 : materialized.Average();
    }

    private static string Recommend(
        IReadOnlyList<CandidateRerankerFeatureCompletenessSample> samples,
        IReadOnlyList<RankerCandidateEligibilityDecision> decisions)
    {
        if (samples.Count == 0 || decisions.Count == 0)
        {
            return CandidateRerankerShadowRecommendations.NeedsMoreRealTraces;
        }

        var completeness = Average(decisions.Select(static decision => decision.Envelope.FeatureCompleteness));
        if (completeness < 0.75)
        {
            return "NeedsMetadataAlignment";
        }

        if (decisions.Any(RankerCandidateEligibilityGuard.IsNonRankableRisk))
        {
            return "ReadyForGuardedShadowEval";
        }

        return CandidateRerankerShadowRecommendations.ReadyForRankerShadow;
    }

    private static void AppendReport(
        StringBuilder builder,
        string title,
        CandidateRerankerFeatureCompletenessReport report)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        builder.AppendLine($"- Samples: `{report.Samples}`");
        builder.AppendLine($"- RawCandidateCount: `{report.RawCandidateCount}`");
        builder.AppendLine($"- RankableCandidateCount: `{report.RankableCandidateCount}`");
        builder.AppendLine($"- BlockedCandidateCount: `{report.BlockedCandidateCount}`");
        builder.AppendLine($"- AuditOnlyCandidateCount: `{report.AuditOnlyCandidateCount}`");
        builder.AppendLine($"- DiagnosticsOnlyCandidateCount: `{report.DiagnosticsOnlyCandidateCount}`");
        builder.AppendLine($"- FeatureCompletenessRate: `{report.FeatureCompletenessRate.ToString("P2", CultureInfo.InvariantCulture)}`");
        builder.AppendLine($"- MissingFeatureMetadataCount: `{report.MissingFeatureMetadataCount}`");
        builder.AppendLine($"- RiskCandidateBlockedBeforeRerank: `{report.RiskCandidateBlockedBeforeRerank}`");
        builder.AppendLine($"- EligibilityGuardStatus: `{report.EligibilityGuardStatus}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("### Blocked Reasons");
        if (report.BlockedReasonCounts.Count == 0)
        {
            builder.AppendLine("- none");
        }
        else
        {
            foreach (var pair in report.BlockedReasonCounts
                         .OrderByDescending(static item => item.Value)
                         .ThenBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- `{pair.Key}`: `{pair.Value}`");
            }
        }
    }
}
