using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Graph;

/// <summary>根据 preview 结果构建 relation expansion profile shadow 报告。</summary>
public sealed class RelationExpansionProfileShadowReportBuilder
{
    private readonly RelationExpansionProfileRegistry _profileRegistry;
    private readonly RelationExpansionPreviewService _previewService;

    public RelationExpansionProfileShadowReportBuilder(
        RelationExpansionProfileRegistry profileRegistry,
        RelationExpansionPreviewService previewService)
    {
        _profileRegistry = profileRegistry;
        _previewService = previewService;
    }

    public async Task<RelationExpansionProfileShadowReport> BuildAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<string> itemIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentNullException.ThrowIfNull(itemIds);

        var samples = new List<RelationExpansionProfileShadowSample>();
        foreach (var itemId in itemIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var profile in _profileRegistry.GetAll())
            {
                var preview = await _previewService.PreviewAsync(new RelationExpansionPreviewRequest
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    ItemId = itemId,
                    ProfileId = profile.ProfileId
                }, cancellationToken).ConfigureAwait(false);

                samples.Add(new RelationExpansionProfileShadowSample
                {
                    ItemId = itemId,
                    ProfileId = profile.ProfileId,
                    AcceptedCount = preview.AcceptedCount,
                    BlockedCount = preview.BlockedCount,
                    TopBlockedReasons = preview.BlockedRelations
                        .SelectMany(relation => relation.Reasons)
                        .GroupBy(reason => reason, StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(group => group.Count())
                        .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                        .Take(5)
                        .Select(group => $"{group.Key}:{group.Count()}")
                        .ToArray(),
                    Warnings = preview.Warnings.ToArray(),
                    BlockedByBackwardReplacementTraversal = CountReason(preview.BlockedRelations, RelationExpansionValidationReasons.BackwardReplacementTraversalBlocked),
                    BlockedByDeprecatedTarget = CountReason(preview.BlockedRelations, RelationExpansionValidationReasons.DeprecatedTargetBlocked),
                    BlockedByHistoricalTarget = CountReason(preview.BlockedRelations, RelationExpansionValidationReasons.HistoricalTargetBlocked),
                    AllowedTowardLatest = preview.AcceptedRelations.Count(IsTowardLatest),
                    BlockedTowardHistorical = preview.BlockedRelations.Count(IsTowardHistorical),
                    HistoricalAllowedOnlyInAudit = preview.AcceptedRelations.Count(IsHistoricalAllowedOnlyInAudit),
                    AcceptedToNormalContext = preview.AcceptedRelations.Count(IsNormalContext),
                    AcceptedToHistoricalContext = preview.AcceptedRelations.Count(IsHistoricalContext),
                    AcceptedToAuditContext = preview.AcceptedRelations.Count(IsAuditContext),
                    AcceptedToConflictEvidence = preview.AcceptedRelations.Count(IsConflictEvidence),
                    AcceptedToDiagnosticsOnly = preview.AcceptedRelations.Count(IsDiagnosticsOnly),
                    RiskIfNormalSelected = preview.AcceptedRelations.Count(relation => relation.RiskIfNormalSelected),
                    RiskAfterSectionRouting = preview.AcceptedRelations.Count(relation => relation.RiskAfterSectionRouting),
                    HistoricalAuditExpansion = preview.AcceptedRelations.Count(IsHistoricalAuditExpansion),
                    ConflictEvidenceExpansion = preview.AcceptedRelations.Count(IsConflictEvidence),
                    WrongSectionRisk = preview.AcceptedRelations.Count(relation => relation.RiskAfterSectionRouting)
                        + CountReason(preview.BlockedRelations, RelationExpansionValidationReasons.BlockedByWrongSectionRisk)
                });
            }
        }

        var profileSummaries = samples
            .GroupBy(sample => sample.ProfileId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RelationExpansionProfileShadowProfileSummary
            {
                ProfileId = group.Key,
                SampleCount = group.Count(),
                AcceptedRelationCount = group.Sum(sample => sample.AcceptedCount),
                BlockedRelationCount = group.Sum(sample => sample.BlockedCount),
                BlockReasonCounts = group
                    .SelectMany(sample => sample.TopBlockedReasons)
                    .Select(ParseReasonCount)
                    .GroupBy(item => item.Reason, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Sum(item => item.Count),
                        StringComparer.OrdinalIgnoreCase),
                BlockedByBackwardReplacementTraversal = group.Sum(sample => sample.BlockedByBackwardReplacementTraversal),
                BlockedByDeprecatedTarget = group.Sum(sample => sample.BlockedByDeprecatedTarget),
                BlockedByHistoricalTarget = group.Sum(sample => sample.BlockedByHistoricalTarget),
                AllowedTowardLatest = group.Sum(sample => sample.AllowedTowardLatest),
                BlockedTowardHistorical = group.Sum(sample => sample.BlockedTowardHistorical),
                HistoricalAllowedOnlyInAudit = group.Sum(sample => sample.HistoricalAllowedOnlyInAudit),
                AcceptedToNormalContext = group.Sum(sample => sample.AcceptedToNormalContext),
                AcceptedToHistoricalContext = group.Sum(sample => sample.AcceptedToHistoricalContext),
                AcceptedToAuditContext = group.Sum(sample => sample.AcceptedToAuditContext),
                AcceptedToConflictEvidence = group.Sum(sample => sample.AcceptedToConflictEvidence),
                AcceptedToDiagnosticsOnly = group.Sum(sample => sample.AcceptedToDiagnosticsOnly),
                RiskIfNormalSelected = group.Sum(sample => sample.RiskIfNormalSelected),
                RiskAfterSectionRouting = group.Sum(sample => sample.RiskAfterSectionRouting),
                HistoricalAuditExpansion = group.Sum(sample => sample.HistoricalAuditExpansion),
                ConflictEvidenceExpansion = group.Sum(sample => sample.ConflictEvidenceExpansion),
                WrongSectionRisk = group.Sum(sample => sample.WrongSectionRisk)
            })
            .OrderBy(summary => summary.ProfileId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new RelationExpansionProfileShadowReport
        {
            CreatedAt = DateTimeOffset.UtcNow,
            ProfileCount = profileSummaries.Length,
            SampleCount = samples.Count,
            AcceptedRelationCount = samples.Sum(sample => sample.AcceptedCount),
            BlockedRelationCount = samples.Sum(sample => sample.BlockedCount),
            Profiles = profileSummaries,
            Samples = samples
                .OrderBy(sample => sample.ProfileId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(sample => sample.ItemId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Warnings = samples
                .SelectMany(sample => sample.Warnings)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    public static string BuildMarkdownReport(RelationExpansionProfileShadowReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var lines = new List<string>
        {
            "# Relation Expansion Profile Shadow Report",
            string.Empty,
            $"Generated: {report.CreatedAt:O}",
            string.Empty,
            "## Summary",
            string.Empty,
            $"- Profile count: `{report.ProfileCount}`",
            $"- Sample count: `{report.SampleCount}`",
            $"- Accepted relations: `{report.AcceptedRelationCount}`",
            $"- Blocked relations: `{report.BlockedRelationCount}`",
            string.Empty,
            "## Profiles",
            string.Empty,
            "| Profile | Samples | Accepted | Blocked | Normal | Historical | Audit | Conflict | Diagnostics | Risk If Normal | Risk After Routing | Historical Audit | Conflict Evidence | Wrong Section | Top Block Reasons |",
            "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|"
        };

        foreach (var profile in report.Profiles)
        {
            var reasons = profile.BlockReasonCounts.Count == 0
                ? "-"
                : string.Join("<br>", profile.BlockReasonCounts
                    .OrderByDescending(item => item.Value)
                    .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(item => $"{item.Key}: {item.Value}"));
            lines.Add($"| {profile.ProfileId} | {profile.SampleCount} | {profile.AcceptedRelationCount} | {profile.BlockedRelationCount} | {profile.AcceptedToNormalContext} | {profile.AcceptedToHistoricalContext} | {profile.AcceptedToAuditContext} | {profile.AcceptedToConflictEvidence} | {profile.AcceptedToDiagnosticsOnly} | {profile.RiskIfNormalSelected} | {profile.RiskAfterSectionRouting} | {profile.HistoricalAuditExpansion} | {profile.ConflictEvidenceExpansion} | {profile.WrongSectionRisk} | {reasons} |");
        }

        lines.AddRange(
        [
            string.Empty,
            "## Samples",
            string.Empty,
            "| Item | Profile | Accepted | Blocked | Normal | Historical | Audit | Conflict | Risk If Normal | Risk After Routing | Wrong Section | Top Block Reasons |",
            "|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|"
        ]);

        foreach (var sample in report.Samples)
        {
            var reasons = sample.TopBlockedReasons.Count == 0
                ? "-"
                : string.Join("<br>", sample.TopBlockedReasons);
            lines.Add($"| {sample.ItemId} | {sample.ProfileId} | {sample.AcceptedCount} | {sample.BlockedCount} | {sample.AcceptedToNormalContext} | {sample.AcceptedToHistoricalContext} | {sample.AcceptedToAuditContext} | {sample.AcceptedToConflictEvidence} | {sample.RiskIfNormalSelected} | {sample.RiskAfterSectionRouting} | {sample.WrongSectionRisk} | {reasons} |");
        }

        if (report.Warnings.Count > 0)
        {
            lines.AddRange(
            [
                string.Empty,
                "## Warnings",
                string.Empty
            ]);
            lines.AddRange(report.Warnings.Select(warning => $"- {warning}"));
        }

        lines.Add(string.Empty);
        return string.Join(Environment.NewLine, lines);
    }

    private static (string Reason, int Count) ParseReasonCount(string value)
    {
        var index = value.LastIndexOf(':');
        if (index <= 0 || index == value.Length - 1 || !int.TryParse(value[(index + 1)..], out var count))
        {
            return (value, 1);
        }

        return (value[..index], count);
    }

    private static int CountReason(
        IEnumerable<RelationExpansionPreviewRelation> relations,
        string reason)
    {
        return relations.Count(relation => relation.Reasons.Contains(reason, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsTowardLatest(RelationExpansionPreviewRelation relation)
    {
        return string.Equals(relation.TraversalDirection, RelationTraversalDirections.TowardLatest, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTowardHistorical(RelationExpansionPreviewRelation relation)
    {
        return string.Equals(relation.TraversalDirection, RelationTraversalDirections.TowardHistorical, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHistoricalAllowedOnlyInAudit(RelationExpansionPreviewRelation relation)
    {
        return relation.Warnings.Contains(RelationExpansionValidationReasons.HistoricalAllowedOnlyInAudit, StringComparer.OrdinalIgnoreCase)
            || string.Equals(relation.TargetSection, RelationExpansionTargetSections.AuditHistorical, StringComparison.OrdinalIgnoreCase)
            && IsHistoricalLifecycle(relation.TargetLifecycle);
    }

    private static bool IsHistoricalAuditExpansion(RelationExpansionPreviewRelation relation)
    {
        return (IsHistoricalLifecycle(relation.TargetLifecycle) || relation.RiskIfNormalSelected)
            && (IsAuditContext(relation) || IsHistoricalContext(relation));
    }

    private static bool IsNormalContext(RelationExpansionPreviewRelation relation)
    {
        return string.Equals(relation.TargetSection, GraphExpansionTargetSection.NormalContext, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHistoricalContext(RelationExpansionPreviewRelation relation)
    {
        return string.Equals(relation.TargetSection, GraphExpansionTargetSection.HistoricalContext, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAuditContext(RelationExpansionPreviewRelation relation)
    {
        return string.Equals(relation.TargetSection, GraphExpansionTargetSection.AuditContext, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConflictEvidence(RelationExpansionPreviewRelation relation)
    {
        return string.Equals(relation.TargetSection, GraphExpansionTargetSection.ConflictEvidence, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDiagnosticsOnly(RelationExpansionPreviewRelation relation)
    {
        return string.Equals(relation.TargetSection, GraphExpansionTargetSection.DiagnosticsOnly, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHistoricalLifecycle(string lifecycle)
    {
        return string.Equals(lifecycle, StableMemoryLifecycle.Deprecated, StringComparison.OrdinalIgnoreCase)
            || string.Equals(lifecycle, StableMemoryLifecycle.Superseded, StringComparison.OrdinalIgnoreCase)
            || string.Equals(lifecycle, "Historical", StringComparison.OrdinalIgnoreCase);
    }
}
