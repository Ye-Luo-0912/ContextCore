using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>从 runtime ranker shadow trace 构建 candidate reranker 质量报告。</summary>
public sealed class CandidateRerankerShadowTraceQualityReportBuilder
{
    public const string PolicyVersion = "candidate-reranker-shadow-trace-quality-cr1/v1";
    public const string DefaultOutputDirectory = "learning/ranker";
    public const string ReportFileName = "candidate-reranker-shadow-trace-quality-report.json";
    public const string MarkdownReportFileName = "candidate-reranker-shadow-trace-quality-report.md";
    private const int ReadyTraceThreshold = 30;

    public CandidateRerankerShadowTraceQualityReport Build(
        IReadOnlyList<LifecycleAwareRankerShadowTraceRecord> records,
        string? workspaceId = null,
        string? collectionId = null,
        int recordTopK = 10)
    {
        ArgumentNullException.ThrowIfNull(records);

        var traces = records
            .Select(record => CandidateRerankerShadowTraceFactory.Build(
                record.RetrievalId,
                ResolveMode(record),
                ResolveIntent(record),
                record.Query,
                ToTrace(record),
                recordTopK))
            .ToArray();
        var riskCount = traces.Sum(static trace => trace.LifecycleRiskCount + trace.DeprecatedCandidateCount + trace.MustNotRiskCount);
        var netGain = 0;

        return new CandidateRerankerShadowTraceQualityReport
        {
            OperationId = $"candidate-reranker-shadow-trace-quality-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            WorkspaceId = ResolveWorkspaceId(records, workspaceId),
            CollectionId = ResolveCollectionId(records, collectionId),
            TraceCount = traces.Length,
            CandidateCount = traces.Sum(static trace => trace.CandidateCount),
            WouldChangeTop1Count = traces.Count(static trace => trace.WouldChangeTop1),
            WouldChangeTopKCount = traces.Count(static trace => trace.WouldChangeTopK),
            LifecycleRiskCount = traces.Sum(static trace => trace.LifecycleRiskCount),
            DeprecatedRiskCount = traces.Sum(static trace => trace.DeprecatedCandidateCount),
            MustNotRiskCount = traces.Sum(static trace => trace.MustNotRiskCount),
            NetGain = netGain,
            Recommendation = Recommend(traces.Length, traces.Sum(static trace => trace.CandidateCount), riskCount),
            Traces = [.. traces.Take(100)],
            PolicyVersion = PolicyVersion
        };
    }

    public static string BuildMarkdownReport(CandidateRerankerShadowTraceQualityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Candidate Reranker Shadow Trace Quality Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.GeneratedAt:O}");
        builder.AppendLine($"PolicyVersion: `{report.PolicyVersion}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- Workspace: `{Empty(report.WorkspaceId)}`");
        builder.AppendLine($"- Collection: `{Empty(report.CollectionId)}`");
        builder.AppendLine($"- TraceCount: `{report.TraceCount}`");
        builder.AppendLine($"- CandidateCount: `{report.CandidateCount}`");
        builder.AppendLine($"- WouldChangeTop1Count: `{report.WouldChangeTop1Count}`");
        builder.AppendLine($"- WouldChangeTopKCount: `{report.WouldChangeTopKCount}`");
        builder.AppendLine($"- LifecycleRiskCount: `{report.LifecycleRiskCount}`");
        builder.AppendLine($"- DeprecatedRiskCount: `{report.DeprecatedRiskCount}`");
        builder.AppendLine($"- MustNotRiskCount: `{report.MustNotRiskCount}`");
        builder.AppendLine($"- NetGain: `{report.NetGain}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Recent Traces");
        builder.AppendLine();
        builder.AppendLine("| Request | Mode | Intent | Candidates | ChangeTop1 | ChangeTopK | Risk | FormalTop | ShadowTop |");
        builder.AppendLine("|---|---|---|---:|---:|---:|---:|---|---|");
        foreach (var trace in report.Traces.Take(30))
        {
            var risk = trace.LifecycleRiskCount + trace.DeprecatedCandidateCount + trace.MustNotRiskCount;
            builder.AppendLine($"| `{trace.RequestId}` | `{trace.Mode}` | `{trace.Intent}` | {trace.CandidateCount} | {trace.WouldChangeTop1} | {trace.WouldChangeTopK} | {risk} | `{trace.FormalTopCandidates.FirstOrDefault()?.CandidateId ?? "-"}` | `{trace.ShadowTopCandidates.FirstOrDefault()?.CandidateId ?? "-"}` |");
        }

        builder.AppendLine();
        builder.AppendLine("## Runtime Safety");
        builder.AppendLine();
        builder.AppendLine("- This report reads existing shadow traces only.");
        builder.AppendLine("- It does not change formal retrieval output, selected set, PackingPolicy, or package sections.");
        return builder.ToString();
    }

    private static LifecycleAwareRankerShadowTrace ToTrace(LifecycleAwareRankerShadowTraceRecord record)
    {
        return new LifecycleAwareRankerShadowTrace
        {
            RankerShadowEnabled = true,
            RankerShadowProfile = record.Profile,
            CandidateShadowScores = record.CandidateScores,
            DeprecatedDemotions = record.DeprecatedDemotions,
            VersionConflictFixes = record.VersionConflictFixes,
            MustHitDemotions = record.MustHitDemotions,
            MustNotHitPromotions = record.MustNotHitPromotions
        };
    }

    private static string Recommend(
        int traceCount,
        int candidateCount,
        int riskCount)
    {
        if (traceCount == 0 || candidateCount == 0 || traceCount < ReadyTraceThreshold)
        {
            return CandidateRerankerShadowRecommendations.NeedsMoreRealTraces;
        }

        if (riskCount > 0)
        {
            return CandidateRerankerShadowRecommendations.BlockedByRisk;
        }

        return CandidateRerankerShadowRecommendations.ReadyForRankerShadow;
    }

    private static string ResolveMode(LifecycleAwareRankerShadowTraceRecord record)
    {
        return GetMetadata(record, "rankerShadowQueryMode")
            ?? GetMetadata(record, "mode")
            ?? GetMetadata(record, "planning.mode")
            ?? "Unknown";
    }

    private static string ResolveIntent(LifecycleAwareRankerShadowTraceRecord record)
    {
        return GetMetadata(record, "planningIntent")
            ?? GetMetadata(record, "rankerShadowIntent")
            ?? GetMetadata(record, "intent")
            ?? "Unknown";
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

    private static string Empty(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
    }
}
