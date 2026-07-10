using System.Globalization;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>Builds read-only quality reports from captured runtime lifecycle-aware ranker shadow traces.</summary>
public sealed class RankerShadowTraceQualityReportBuilder
{
    public const string PolicyVersion = "ranker-shadow-trace-quality/v1";
    private const string Unknown = "Unknown";
    private const int ReadyTraceThreshold = 30;
    private const int MaxRiskSamples = 25;

    public async Task<RankerShadowTraceQualityReport> BuildAsync(
        IRetrievalTraceStore? traceStore,
        string workspaceId,
        string collectionId,
        int take = 200,
        CancellationToken cancellationToken = default)
    {
        if (traceStore is null || string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(collectionId))
        {
            return Build(Array.Empty<LifecycleAwareRankerShadowTraceRecord>(), workspaceId, collectionId);
        }

        var records = await new RankerShadowTraceExportService(traceStore)
            .QueryAsync(workspaceId, collectionId, take, cancellationToken)
            .ConfigureAwait(false);

        return Build(records, workspaceId, collectionId);
    }

    public RankerShadowTraceQualityReport Build(
        IReadOnlyList<LifecycleAwareRankerShadowTraceRecord> records,
        string? workspaceId = null,
        string? collectionId = null)
    {
        ArgumentNullException.ThrowIfNull(records);

        var materialized = records.ToArray();
        var scores = materialized.SelectMany(static record => record.CandidateScores).ToArray();
        var riskSamples = BuildRiskSamples(materialized);
        var traceCount = materialized.Length;
        var candidateScoreCount = scores.Length;

        return new RankerShadowTraceQualityReport
        {
            OperationId = $"ranker-shadow-trace-quality-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            WorkspaceId = ResolveWorkspaceId(materialized, workspaceId),
            CollectionId = ResolveCollectionId(materialized, collectionId),
            TraceCount = traceCount,
            CandidateScoreCount = candidateScoreCount,
            DeprecatedDemotionCount = scores.Count(IsDeprecatedDemotion),
            HistoricalDemotionCount = scores.Count(IsHistoricalDemotion),
            VersionConflictFixCount = materialized.Sum(static record => record.VersionConflictFixes.Count),
            CurrentVersionPromotionCount = scores.Count(IsCurrentVersionPromotion),
            MustHitDemotedCount = scores.Count(IsMustHitDemotion),
            MustNotHitPromotedCount = scores.Count(IsMustNotHitPromotion),
            LifecycleViolationCount = scores.Count(IsLifecycleViolation),
            AverageScoreDelta = Average(scores.Select(static score => score.ScoreDelta)),
            MaxPositiveDelta = scores.Length == 0 ? 0 : scores.Max(static score => Math.Max(0, score.ScoreDelta)),
            MaxNegativeDelta = scores.Length == 0 ? 0 : scores.Min(static score => Math.Min(0, score.ScoreDelta)),
            ModeBreakdown = BuildBreakdown(materialized, ResolveMode),
            IntentBreakdown = BuildBreakdown(materialized, ResolveIntent),
            RiskSamples = riskSamples,
            RecommendedNextStep = Recommend(
                traceCount,
                candidateScoreCount,
                riskSamples.Count,
                scores.Count(IsLifecycleViolation),
                scores.Count(IsDeprecatedDemotion),
                materialized.Sum(static record => record.VersionConflictFixes.Count)),
            PolicyVersion = PolicyVersion
        };
    }

    public static string BuildMarkdownReport(RankerShadowTraceQualityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Ranker Shadow Trace Quality Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.GeneratedAt:O}");
        builder.AppendLine($"PolicyVersion: `{report.PolicyVersion}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- Workspace: `{Empty(report.WorkspaceId)}`");
        builder.AppendLine($"- Collection: `{Empty(report.CollectionId)}`");
        builder.AppendLine($"- TraceCount: `{report.TraceCount}`");
        builder.AppendLine($"- CandidateScoreCount: `{report.CandidateScoreCount}`");
        builder.AppendLine($"- DeprecatedDemotionCount: `{report.DeprecatedDemotionCount}`");
        builder.AppendLine($"- HistoricalDemotionCount: `{report.HistoricalDemotionCount}`");
        builder.AppendLine($"- VersionConflictFixCount: `{report.VersionConflictFixCount}`");
        builder.AppendLine($"- CurrentVersionPromotionCount: `{report.CurrentVersionPromotionCount}`");
        builder.AppendLine($"- MustHitDemotedCount: `{report.MustHitDemotedCount}`");
        builder.AppendLine($"- MustNotHitPromotedCount: `{report.MustNotHitPromotedCount}`");
        builder.AppendLine($"- LifecycleViolationCount: `{report.LifecycleViolationCount}`");
        builder.AppendLine($"- AverageScoreDelta: `{Format(report.AverageScoreDelta)}`");
        builder.AppendLine($"- MaxPositiveDelta: `{Format(report.MaxPositiveDelta)}`");
        builder.AppendLine($"- MaxNegativeDelta: `{Format(report.MaxNegativeDelta)}`");
        builder.AppendLine($"- RecommendedNextStep: `{report.RecommendedNextStep}`");
        builder.AppendLine();

        AppendBreakdown(builder, "Mode Breakdown", report.ModeBreakdown.Values);
        AppendBreakdown(builder, "Intent Breakdown", report.IntentBreakdown.Values);
        AppendRiskSamples(builder, report.RiskSamples);

        return builder.ToString();
    }

    private static IReadOnlyDictionary<string, RankerShadowTraceQualityBreakdown> BuildBreakdown(
        IReadOnlyList<LifecycleAwareRankerShadowTraceRecord> records,
        Func<LifecycleAwareRankerShadowTraceRecord, string> keySelector)
    {
        return records
            .GroupBy(record => NormalizeKey(keySelector(record)), StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => BuildBreakdown(group.Key, group.ToArray()),
                StringComparer.OrdinalIgnoreCase);
    }

    private static RankerShadowTraceQualityBreakdown BuildBreakdown(
        string key,
        IReadOnlyList<LifecycleAwareRankerShadowTraceRecord> records)
    {
        var scores = records.SelectMany(static record => record.CandidateScores).ToArray();
        return new RankerShadowTraceQualityBreakdown
        {
            Key = key,
            TraceCount = records.Count,
            CandidateScoreCount = scores.Length,
            DeprecatedDemotionCount = scores.Count(IsDeprecatedDemotion),
            HistoricalDemotionCount = scores.Count(IsHistoricalDemotion),
            VersionConflictFixCount = records.Sum(static record => record.VersionConflictFixes.Count),
            CurrentVersionPromotionCount = scores.Count(IsCurrentVersionPromotion),
            MustHitDemotedCount = scores.Count(IsMustHitDemotion),
            MustNotHitPromotedCount = scores.Count(IsMustNotHitPromotion),
            AverageScoreDelta = Average(scores.Select(static score => score.ScoreDelta))
        };
    }

    private static IReadOnlyList<RankerShadowTraceRiskSample> BuildRiskSamples(
        IReadOnlyList<LifecycleAwareRankerShadowTraceRecord> records)
    {
        var risks = new List<RankerShadowTraceRiskSample>();
        foreach (var record in records)
        {
            var mode = ResolveMode(record);
            var intent = ResolveIntent(record);
            foreach (var score in record.CandidateScores)
            {
                if (IsMustHitDemotion(score))
                {
                    risks.Add(BuildRiskSample(record, score, mode, intent, "MustHitDemoted"));
                }

                if (IsMustNotHitPromotion(score))
                {
                    risks.Add(BuildRiskSample(record, score, mode, intent, "MustNotHitPromoted"));
                }
            }
        }

        return risks
            .OrderBy(static sample => sample.RiskType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static sample => sample.RetrievalId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static sample => sample.CandidateId, StringComparer.OrdinalIgnoreCase)
            .Take(MaxRiskSamples)
            .ToArray();
    }

    private static RankerShadowTraceRiskSample BuildRiskSample(
        LifecycleAwareRankerShadowTraceRecord record,
        LifecycleAwareRankerShadowCandidateScore score,
        string mode,
        string intent,
        string riskType)
    {
        return new RankerShadowTraceRiskSample
        {
            RetrievalId = record.RetrievalId,
            WorkspaceId = record.WorkspaceId,
            CollectionId = record.CollectionId,
            Query = record.Query,
            Mode = NormalizeKey(mode),
            Intent = NormalizeKey(intent),
            RiskType = riskType,
            CandidateId = score.CandidateId,
            ScoreDelta = score.ScoreDelta,
            Reason = score.Reason
        };
    }

    private static string Recommend(
        int traceCount,
        int candidateScoreCount,
        int riskCount,
        int lifecycleViolationCount,
        int deprecatedDemotionCount,
        int versionConflictFixCount)
    {
        if (traceCount == 0 || candidateScoreCount == 0 || traceCount < ReadyTraceThreshold)
        {
            return RankerShadowTraceRecommendedNextSteps.NeedsMoreRealTraces;
        }

        if (riskCount > 0 || lifecycleViolationCount > 0)
        {
            return RankerShadowTraceRecommendedNextSteps.BlockedByRisk;
        }

        if (deprecatedDemotionCount > 0 || versionConflictFixCount > 0)
        {
            return RankerShadowTraceRecommendedNextSteps.ReadyForGuardedOptIn;
        }

        return RankerShadowTraceRecommendedNextSteps.KeepShadowOnly;
    }

    private static bool IsDeprecatedDemotion(LifecycleAwareRankerShadowCandidateScore score)
    {
        return score.ScoreDelta < 0
            && (score.LifecycleFeatures.IsDeprecated
                || score.LifecycleFeatures.IsSuperseded
                || ContainsReason(score, "deprecated")
                || ContainsReason(score, "superseded"));
    }

    private static bool IsHistoricalDemotion(LifecycleAwareRankerShadowCandidateScore score)
    {
        return score.ScoreDelta < 0
            && (score.LifecycleFeatures.IsHistorical
                || score.LifecycleFeatures.HistoricalSectionOnly
                || ContainsReason(score, "historical"));
    }

    private static bool IsCurrentVersionPromotion(LifecycleAwareRankerShadowCandidateScore score)
    {
        return score.ScoreDelta > 0
            && (score.LifecycleFeatures.IsCurrentVersion
                || ContainsReason(score, "current_version")
                || ContainsReason(score, "supersedes_relation"));
    }

    private static bool IsMustHitDemotion(LifecycleAwareRankerShadowCandidateScore score)
    {
        return score.IsMustHit && (score.ScoreDelta < 0 || score.RankDelta < 0);
    }

    private static bool IsMustNotHitPromotion(LifecycleAwareRankerShadowCandidateScore score)
    {
        return score.IsMustNotHit && (score.ScoreDelta > 0 || score.RankDelta > 0);
    }

    private static bool IsLifecycleViolation(LifecycleAwareRankerShadowCandidateScore score)
    {
        return score.Selected && (score.IsMustNotHit || score.LifecycleFeatures.IsRejected);
    }

    private static bool ContainsReason(LifecycleAwareRankerShadowCandidateScore score, string value)
    {
        return score.Reason.Contains(value, StringComparison.OrdinalIgnoreCase)
            || score.DemotionReasons.Any(reason => reason.Contains(value, StringComparison.OrdinalIgnoreCase))
            || score.PromotionReasons.Any(reason => reason.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveMode(LifecycleAwareRankerShadowTraceRecord record)
    {
        return GetMetadata(record, "rankerShadowQueryMode")
            ?? GetMetadata(record, "mode")
            ?? GetMetadata(record, "planning.mode")
            ?? Unknown;
    }

    private static string ResolveIntent(LifecycleAwareRankerShadowTraceRecord record)
    {
        return GetMetadata(record, "planningIntent")
            ?? GetMetadata(record, "rankerShadowIntent")
            ?? GetMetadata(record, "intent")
            ?? Unknown;
    }

    private static string? GetMetadata(LifecycleAwareRankerShadowTraceRecord record, string key)
    {
        return record.Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static string ResolveWorkspaceId(
        IReadOnlyList<LifecycleAwareRankerShadowTraceRecord> records,
        string? fallback)
    {
        return records.Select(static record => record.WorkspaceId)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
            ?? fallback
            ?? string.Empty;
    }

    private static string ResolveCollectionId(
        IReadOnlyList<LifecycleAwareRankerShadowTraceRecord> records,
        string? fallback)
    {
        return records.Select(static record => record.CollectionId)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
            ?? fallback
            ?? string.Empty;
    }

    private static string NormalizeKey(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? Unknown : value.Trim();
    }

    private static double Average(IEnumerable<double> values)
    {
        var materialized = values.ToArray();
        return materialized.Length == 0 ? 0 : materialized.Average();
    }

    private static void AppendBreakdown(
        StringBuilder builder,
        string title,
        IEnumerable<RankerShadowTraceQualityBreakdown> rows)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        builder.AppendLine("| Key | Traces | Candidates | Deprecated | Historical | VersionFix | CurrentPromotion | MustHitDemoted | MustNotHitPromoted | AvgDelta |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var row in rows.OrderBy(static row => row.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| `{row.Key}` | {row.TraceCount} | {row.CandidateScoreCount} | {row.DeprecatedDemotionCount} | {row.HistoricalDemotionCount} | {row.VersionConflictFixCount} | {row.CurrentVersionPromotionCount} | {row.MustHitDemotedCount} | {row.MustNotHitPromotedCount} | {Format(row.AverageScoreDelta)} |");
        }
        builder.AppendLine();
    }

    private static void AppendRiskSamples(
        StringBuilder builder,
        IReadOnlyList<RankerShadowTraceRiskSample> riskSamples)
    {
        builder.AppendLine("## Risk Samples");
        builder.AppendLine();
        if (riskSamples.Count == 0)
        {
            builder.AppendLine("No risk samples.");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("| Retrieval | Mode | Intent | Risk | Candidate | Delta | Reason |");
        builder.AppendLine("|---|---|---|---|---|---:|---|");
        foreach (var sample in riskSamples)
        {
            builder.AppendLine($"| `{sample.RetrievalId}` | `{sample.Mode}` | `{sample.Intent}` | `{sample.RiskType}` | `{sample.CandidateId}` | {Format(sample.ScoreDelta)} | {sample.Reason} |");
        }
        builder.AppendLine();
    }

    private static string Format(double value)
    {
        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static string Empty(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? Unknown : value;
    }
}
