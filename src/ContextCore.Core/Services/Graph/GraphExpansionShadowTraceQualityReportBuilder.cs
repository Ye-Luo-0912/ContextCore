using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Graph;

/// <summary>从 runtime graph expansion shadow trace 构建只读质量报告。</summary>
public sealed class GraphExpansionShadowTraceQualityReportBuilder
{
    public const string PolicyVersion = "graph-expansion-shadow-trace-quality/v1";
    private const string Unknown = "Unknown";
    private const int ReadyTraceThreshold = 30;

    public async Task<GraphExpansionShadowTraceQualityReport> BuildAsync(
        IRetrievalTraceStore? traceStore,
        string workspaceId,
        string collectionId,
        int take = 200,
        CancellationToken cancellationToken = default)
    {
        if (traceStore is null || string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(collectionId))
        {
            return Build(Array.Empty<GraphExpansionShadowTraceRecord>(), workspaceId, collectionId);
        }

        var records = await new GraphExpansionShadowTraceExportService(traceStore)
            .QueryAsync(workspaceId, collectionId, take, cancellationToken)
            .ConfigureAwait(false);

        return Build(records, workspaceId, collectionId);
    }

    public GraphExpansionShadowTraceQualityReport Build(
        IReadOnlyList<GraphExpansionShadowTraceRecord> records,
        string? workspaceId = null,
        string? collectionId = null)
    {
        ArgumentNullException.ThrowIfNull(records);

        var materialized = records.ToArray();
        var accepted = materialized.SelectMany(static record => record.AcceptedRelations).ToArray();
        var blocked = materialized.SelectMany(static record => record.BlockedRelations).ToArray();
        var riskAfterRouting = materialized.Sum(static record => record.RiskAfterRouting);
        var wrongSectionRisk = materialized.Sum(static record => record.WrongSectionRisk);
        var mustNotHitRisk = accepted.Count(IsMustNotHitRiskRelation) + blocked.Count(IsMustNotHitRiskRelation);
        var lifecycleRisk = accepted.Count(IsLifecycleRiskRelation) + blocked.Count(IsLifecycleRiskRelation);
        var missingEvidence = blocked.Count(HasMissingEvidenceReason);

        return new GraphExpansionShadowTraceQualityReport
        {
            OperationId = $"graph-shadow-trace-quality-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            WorkspaceId = ResolveWorkspaceId(materialized, workspaceId),
            CollectionId = ResolveCollectionId(materialized, collectionId),
            TraceCount = materialized.Length,
            AcceptedRelationCount = accepted.Length,
            BlockedRelationCount = blocked.Length,
            AuditContextCount = accepted.Count(IsAuditContextRelation),
            ConflictEvidenceCount = accepted.Count(static relation =>
                string.Equals(relation.TargetSection, GraphExpansionTargetSection.ConflictEvidence, StringComparison.OrdinalIgnoreCase)),
            RiskAfterRoutingCount = riskAfterRouting,
            WrongSectionRiskCount = wrongSectionRisk,
            MustNotHitRiskCount = mustNotHitRisk,
            LifecycleRiskCount = lifecycleRisk,
            MissingEvidenceCount = missingEvidence,
            TopRelationTypes = BuildRelationTypeCounts(accepted, blocked),
            TopBlockedReasons = BuildBlockedReasonCounts(blocked),
            Recommendation = Recommend(
                materialized.Length,
                accepted.Length,
                riskAfterRouting,
                wrongSectionRisk,
                mustNotHitRisk,
                lifecycleRisk,
                missingEvidence,
                accepted.Count(IsAuditContextRelation),
                accepted.Count(static relation =>
                    string.Equals(relation.TargetSection, GraphExpansionTargetSection.ConflictEvidence, StringComparison.OrdinalIgnoreCase))),
            PolicyVersion = PolicyVersion
        };
    }

    public static string BuildMarkdownReport(GraphExpansionShadowTraceQualityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Graph Expansion Shadow Trace Quality Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.GeneratedAt:O}");
        builder.AppendLine($"PolicyVersion: `{report.PolicyVersion}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- Workspace: `{Empty(report.WorkspaceId)}`");
        builder.AppendLine($"- Collection: `{Empty(report.CollectionId)}`");
        builder.AppendLine($"- TraceCount: `{report.TraceCount}`");
        builder.AppendLine($"- AcceptedRelationCount: `{report.AcceptedRelationCount}`");
        builder.AppendLine($"- BlockedRelationCount: `{report.BlockedRelationCount}`");
        builder.AppendLine($"- AuditContextCount: `{report.AuditContextCount}`");
        builder.AppendLine($"- ConflictEvidenceCount: `{report.ConflictEvidenceCount}`");
        builder.AppendLine($"- RiskAfterRoutingCount: `{report.RiskAfterRoutingCount}`");
        builder.AppendLine($"- WrongSectionRiskCount: `{report.WrongSectionRiskCount}`");
        builder.AppendLine($"- MustNotHitRiskCount: `{report.MustNotHitRiskCount}`");
        builder.AppendLine($"- LifecycleRiskCount: `{report.LifecycleRiskCount}`");
        builder.AppendLine($"- MissingEvidenceCount: `{report.MissingEvidenceCount}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();

        AppendCounts(builder, "Top Relation Types", report.TopRelationTypes);
        AppendCounts(builder, "Top Blocked Reasons", report.TopBlockedReasons);
        AppendG7ReadinessGate(builder);

        return builder.ToString();
    }

    private static Dictionary<string, int> BuildRelationTypeCounts(
        IReadOnlyList<RelationExpansionPreviewRelation> accepted,
        IReadOnlyList<RelationExpansionPreviewRelation> blocked)
    {
        return accepted.Concat(blocked)
            .GroupBy(static relation => Empty(relation.RelationType), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, int> BuildBlockedReasonCounts(
        IReadOnlyList<RelationExpansionPreviewRelation> blocked)
    {
        return blocked
            .SelectMany(static relation => relation.Reasons.Count == 0
                ? new[] { Unknown }
                : relation.Reasons)
            .GroupBy(static reason => Empty(reason), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);
    }

    private static string Recommend(
        int traceCount,
        int acceptedCount,
        int riskAfterRouting,
        int wrongSectionRisk,
        int mustNotHitRisk,
        int lifecycleRisk,
        int missingEvidence,
        int auditContextCount,
        int conflictEvidenceCount)
    {
        if (traceCount == 0 || acceptedCount == 0 || traceCount < ReadyTraceThreshold)
        {
            return GraphExpansionShadowTraceRecommendations.NeedsMoreRealTraces;
        }

        if (riskAfterRouting > 0 || wrongSectionRisk > 0 || mustNotHitRisk > 0 || lifecycleRisk > 0)
        {
            return GraphExpansionShadowTraceRecommendations.BlockedByRisk;
        }

        if (missingEvidence > 0)
        {
            return GraphExpansionShadowTraceRecommendations.NeedsMoreRealTraces;
        }

        if (auditContextCount > 0 && conflictEvidenceCount > 0)
        {
            return GraphExpansionShadowTraceRecommendations.ReadyForGuardedOptIn;
        }

        if (auditContextCount > 0)
        {
            return GraphExpansionShadowTraceRecommendations.ReadyForAuditShadowOnly;
        }

        if (conflictEvidenceCount > 0)
        {
            return GraphExpansionShadowTraceRecommendations.ReadyForConflictShadowOnly;
        }

        return GraphExpansionShadowTraceRecommendations.NeedsMoreRealTraces;
    }

    private static bool IsAuditContextRelation(RelationExpansionPreviewRelation relation)
    {
        return string.Equals(relation.TargetSection, GraphExpansionTargetSection.AuditContext, StringComparison.OrdinalIgnoreCase)
            || string.Equals(relation.TargetSection, GraphExpansionTargetSection.HistoricalContext, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMustNotHitRiskRelation(RelationExpansionPreviewRelation relation)
    {
        return MetadataIsTrue(relation, "mustNotHitRisk")
            || MetadataIsTrue(relation, "wouldAddMustNotHit");
    }

    private static bool IsLifecycleRiskRelation(RelationExpansionPreviewRelation relation)
    {
        return relation.RiskAfterSectionRouting
            || MetadataIsTrue(relation, "lifecycleRisk")
            || string.Equals(relation.TargetLifecycle, StableMemoryLifecycle.Rejected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasMissingEvidenceReason(RelationExpansionPreviewRelation relation)
    {
        return relation.Reasons.Any(static reason =>
            string.Equals(reason, RelationExpansionValidationReasons.MissingEvidence, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MetadataIsTrue(RelationExpansionPreviewRelation relation, string key)
    {
        return relation.Metadata.TryGetValue(key, out var value)
            && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveWorkspaceId(
        IReadOnlyList<GraphExpansionShadowTraceRecord> records,
        string? fallback)
    {
        return records.Select(static record => record.WorkspaceId)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
            ?? fallback
            ?? string.Empty;
    }

    private static string ResolveCollectionId(
        IReadOnlyList<GraphExpansionShadowTraceRecord> records,
        string? fallback)
    {
        return records.Select(static record => record.CollectionId)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
            ?? fallback
            ?? string.Empty;
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

    private static void AppendG7ReadinessGate(StringBuilder builder)
    {
        builder.AppendLine("## G7 Readiness Gate");
        builder.AppendLine();
        builder.AppendLine("进入 G7 前必须全部满足：");
        builder.AppendLine();
        builder.AppendLine("- `TraceCount >= 30`");
        builder.AppendLine("- `AcceptedRelationCount > 0`");
        builder.AppendLine("- `AuditContextCount > 0` 或 `ConflictEvidenceCount > 0`");
        builder.AppendLine("- `RiskAfterRoutingCount = 0`");
        builder.AppendLine("- `WrongSectionRiskCount = 0`");
        builder.AppendLine("- `MustNotHitRiskCount = 0`");
        builder.AppendLine("- `LifecycleRiskCount = 0`");
        builder.AppendLine("- `MissingEvidenceCount = 0`");
        builder.AppendLine();
        builder.AppendLine("采样完整性要求：");
        builder.AppendLine();
        builder.AppendLine("- `TraceCount >= 30` 必须来自不同 operationId 和不同采样意图。");
        builder.AppendLine("- 重复 query 或重复 fixture 只能验证采集链路，不能作为 readiness 依据。");
        builder.AppendLine("- 样本应覆盖 audit/historical routing、conflict evidence routing，以及可解释的 blocked relation。");
        builder.AppendLine("- 被标记为 `duplicateSuppressed=true` 的重复 graph shadow payload 不计入质量评估。");
        builder.AppendLine();
    }

    private static string Empty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? Unknown : value.Trim();
    }
}
