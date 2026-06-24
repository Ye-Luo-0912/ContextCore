using System.Globalization;
using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 针对 lifecycle-filtered mustHit 的 metadata repair preview；只生成计划，不写入 vector index 或 runtime store。
/// </summary>
public sealed class VectorLifecycleMetadataRepairPlanRunner
{
    private static readonly string[] ProvenanceKeys =
    [
        "provenance",
        "provenanceId",
        "provenanceSource",
        "sourceRef",
        "sourceRefs",
        "sourceReference",
        "sourceReferences",
        "sourceFingerprint",
        "generatedBy",
        "createdBy",
        "origin"
    ];

    private static readonly string[] ReviewKeys =
    [
        "reviewStatus",
        "status",
        "approvalStatus",
        "lifecycleReviewStatus",
        VectorSourceLifecycleMetadataResolver.BackfilledReviewStatusKey
    ];

    private static readonly string[] ReplacementKeys =
    [
        "replacementState",
        "supersededBy",
        "replacedBy",
        "replacementItemId",
        "replacementId",
        "superseded_by",
        "replaced_by"
    ];

    private static readonly string[] RelationEvidenceKeys =
    [
        "relationEvidence",
        "relationEvidenceRefs",
        "relationRefs",
        "replacementRelation",
        "conflictRelation"
    ];

    public VectorLifecycleMetadataRepairPlanReport BuildReport(
        VectorEligibilityRecallLossTriageReport triageReport,
        IReadOnlyList<VectorReindexSourceItem> sourceItems,
        IReadOnlyList<VectorIndexEntry> indexedEntries)
    {
        ArgumentNullException.ThrowIfNull(triageReport);
        ArgumentNullException.ThrowIfNull(sourceItems);
        ArgumentNullException.ThrowIfNull(indexedEntries);

        var sourceById = sourceItems
            .Where(static item => !string.IsNullOrWhiteSpace(item.ItemId))
            .GroupBy(static item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        var entryById = indexedEntries
            .Where(static item => !string.IsNullOrWhiteSpace(item.ItemId))
            .GroupBy(static item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderByDescending(item => item.UpdatedAt).First(),
                StringComparer.OrdinalIgnoreCase);

        var skipped = triageReport.Details.Count(static detail => !IsMetadataRepairTriage(detail));
        var candidates = triageReport.Details
            .Where(IsMetadataRepairTriage)
            .Select(detail => BuildCandidate(detail, FindSource(sourceById, detail.MustHitItemId), FindEntry(entryById, detail.MustHitItemId)))
            .OrderBy(static item => item.ForbiddenReason.Length == 0 ? 1 : 0)
            .ThenBy(static item => item.RequiresHumanReview ? 0 : 1)
            .ThenBy(static item => item.SampleId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.MustHitItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return BuildReport(triageReport, candidates, skipped);
    }

    public static VectorLifecycleMetadataRepairPlanSummaryReport BuildSummary(
        IReadOnlyList<VectorLifecycleMetadataRepairPlanReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        var candidateCount = reports.Sum(static item => item.CandidateCount);
        var auto = reports.Sum(static item => item.AutoRepairableCount);
        var risk = reports.Sum(static item => item.RiskAfterRepairEstimate);

        return new VectorLifecycleMetadataRepairPlanSummaryReport
        {
            OperationId = $"vector-lifecycle-metadata-repair-plan-summary-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            Reports = reports.ToArray(),
            CandidateCount = candidateCount,
            AutoRepairableCount = auto,
            HumanReviewRequiredCount = reports.Sum(static item => item.HumanReviewRequiredCount),
            ForbiddenRepairCount = reports.Sum(static item => item.ForbiddenRepairCount),
            CorrectlyBlockedSkippedCount = reports.Sum(static item => item.CorrectlyBlockedSkippedCount),
            EstimatedRecallRecovery = reports.Sum(static item => item.EstimatedRecallRecovery),
            RiskAfterRepairEstimate = risk,
            Recommendation = Recommend(candidateCount, auto, reports.Sum(static item => item.HumanReviewRequiredCount), reports.Sum(static item => item.ForbiddenRepairCount), risk),
            FormalRetrievalAllowed = false,
            UseForRuntime = false
        };
    }

    public static string BuildMarkdownReport(VectorLifecycleMetadataRepairPlanReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine($"# Vector Lifecycle Metadata Repair Plan - {report.DatasetName}");
        builder.AppendLine();
        AppendReport(builder, report);
        return builder.ToString();
    }

    public static string BuildMarkdownSummary(VectorLifecycleMetadataRepairPlanSummaryReport summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Lifecycle Metadata Repair Plan Summary");
        builder.AppendLine();
        builder.AppendLine($"Generated: {summary.CreatedAt:O}");
        builder.AppendLine($"- Recommendation: `{summary.Recommendation}`");
        builder.AppendLine($"- CandidateCount: `{summary.CandidateCount}`");
        builder.AppendLine($"- AutoRepairableCount: `{summary.AutoRepairableCount}`");
        builder.AppendLine($"- HumanReviewRequiredCount: `{summary.HumanReviewRequiredCount}`");
        builder.AppendLine($"- ForbiddenRepairCount: `{summary.ForbiddenRepairCount}`");
        builder.AppendLine($"- CorrectlyBlockedSkippedCount: `{summary.CorrectlyBlockedSkippedCount}`");
        builder.AppendLine($"- EstimatedRecallRecovery: `{summary.EstimatedRecallRecovery.ToString("F2", CultureInfo.InvariantCulture)}`");
        builder.AppendLine($"- RiskAfterRepairEstimate: `{summary.RiskAfterRepairEstimate}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{summary.FormalRetrievalAllowed}`");
        builder.AppendLine($"- UseForRuntime: `{summary.UseForRuntime}`");
        builder.AppendLine();
        builder.AppendLine("| Dataset | Candidates | Auto | HumanReview | Forbidden | CorrectlyBlockedSkipped | EstimatedRecallRecovery | RiskEstimate | Recommendation |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var report in summary.Reports)
        {
            builder.AppendLine($"| {report.DatasetName} | {report.CandidateCount} | {report.AutoRepairableCount} | {report.HumanReviewRequiredCount} | {report.ForbiddenRepairCount} | {report.CorrectlyBlockedSkippedCount} | {report.EstimatedRecallRecovery.ToString("F2", CultureInfo.InvariantCulture)} | {report.RiskAfterRepairEstimate} | {report.Recommendation} |");
        }

        foreach (var report in summary.Reports)
        {
            builder.AppendLine();
            AppendReport(builder, report);
        }

        return builder.ToString();
    }

    private static VectorLifecycleMetadataRepairCandidate BuildCandidate(
        VectorEligibilityRecallLossTriageDetail detail,
        VectorReindexSourceItem? source,
        VectorIndexEntry? entry)
    {
        var metadata = MergeMetadata(source, entry);
        var provenanceAvailable = detail.SourceRefs.Count > 0 || HasAnyMetadataValue(metadata, ProvenanceKeys);
        var relationEvidenceAvailable = detail.EvidenceRefs.Count > 0 || HasAnyMetadataValue(metadata, RelationEvidenceKeys);
        var reviewStatus = FirstNonEmpty(detail.ReviewStatus, Get(metadata, ReviewKeys));
        var reviewEvidenceAvailable = IsActiveReviewStatus(reviewStatus);
        var replacementState = FirstNonEmpty(detail.ReplacementState, Get(metadata, "replacementState"));
        var sourceRejectedOrDeprecated = ContainsAnyToken(GetCombinedSourceState(detail, metadata), "rejected", "deprecated", "historical", "legacy");
        var conflictingReplacement = ContainsAnyToken(GetCombinedReplacementState(replacementState, metadata), "superseded", "replaced", "deprecated", "historical", "conflict");
        var evalOnly = !provenanceAvailable && detail.EvidenceRefs.Count == 0 && !relationEvidenceAvailable && source is null && entry is null;

        var decision = DecideRepair(
            detail,
            provenanceAvailable,
            reviewEvidenceAvailable,
            sourceRejectedOrDeprecated,
            conflictingReplacement,
            evalOnly);

        return new VectorLifecycleMetadataRepairCandidate
        {
            DatasetName = detail.DatasetName,
            SampleId = detail.SampleId,
            MustHitItemId = detail.MustHitItemId,
            ItemKind = FirstNonEmpty(detail.ItemKind, source?.ItemKind, entry?.ItemKind),
            Layer = FirstNonEmpty(detail.Layer, source?.Layer, entry?.Layer),
            CurrentLifecycle = detail.Lifecycle,
            ProposedLifecycle = decision.ProposedLifecycle,
            CurrentReviewStatus = reviewStatus,
            ProposedReviewStatus = decision.ProposedReviewStatus,
            CurrentTargetSection = detail.CurrentTargetSection,
            ProposedTargetSection = decision.ProposedTargetSection,
            EvidenceRefs = detail.EvidenceRefs,
            SourceRefs = detail.SourceRefs,
            ProvenanceAvailable = provenanceAvailable,
            RelationEvidenceAvailable = relationEvidenceAvailable,
            ReviewEvidenceAvailable = reviewEvidenceAvailable,
            RepairConfidence = decision.Confidence,
            RepairReason = decision.Reason,
            CanAutoRepair = decision.CanAutoRepair,
            RequiresHumanReview = decision.RequiresHumanReview,
            ForbiddenReason = decision.ForbiddenReason
        };
    }

    private static RepairDecision DecideRepair(
        VectorEligibilityRecallLossTriageDetail detail,
        bool provenanceAvailable,
        bool reviewEvidenceAvailable,
        bool sourceRejectedOrDeprecated,
        bool conflictingReplacement,
        bool evalOnly)
    {
        if (IsUnsafeLifecycle(detail.Lifecycle))
        {
            return Forbidden("UnsafeLifecycle", "当前 lifecycle 已指向 deprecated / historical / superseded，不能自动修复为 normal_context。");
        }

        if (sourceRejectedOrDeprecated)
        {
            return Forbidden("SourceRejectedOrDeprecated", "source metadata 标记为 rejected / deprecated / historical，不能自动修复。");
        }

        if (conflictingReplacement)
        {
            return Forbidden("ConflictingReplacementRelation", "replacement / relation evidence 存在 superseded、replaced 或冲突信号，不能自动修复。");
        }

        if (evalOnly)
        {
            return Human("OnlyEvalLabelSupportsRepair", "只有 eval label 支撑 repair，缺少运行时 provenance / review evidence。");
        }

        if (!provenanceAvailable)
        {
            return Human("MissingProvenance", "缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。");
        }

        if (!reviewEvidenceAvailable)
        {
            return Human("MissingActiveReviewEvidence", "缺少 Active / Stable / Current reviewStatus 支撑。");
        }

        return new RepairDecision(
            "Active",
            "Current",
            VectorQueryTargetSections.NormalContext,
            0.85,
            "provenance 与 Active/Stable/Current review evidence 均可用，且未发现 replacement / rejected / deprecated 风险。",
            CanAutoRepair: true,
            RequiresHumanReview: false,
            ForbiddenReason: string.Empty);
    }

    private static VectorLifecycleMetadataRepairPlanReport BuildReport(
        VectorEligibilityRecallLossTriageReport triageReport,
        IReadOnlyList<VectorLifecycleMetadataRepairCandidate> candidates,
        int skipped)
    {
        var candidateCount = candidates.Count;
        var auto = candidates.Count(static item => item.CanAutoRepair);
        var human = candidates.Count(static item => item.RequiresHumanReview);
        var forbidden = candidates.Count(static item =>
            !item.CanAutoRepair
            && !item.RequiresHumanReview
            && !string.IsNullOrWhiteSpace(item.ForbiddenReason));
        var risk = candidates.Count(static item => item.CanAutoRepair && !string.Equals(item.ProposedTargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase));

        return new VectorLifecycleMetadataRepairPlanReport
        {
            OperationId = $"vector-lifecycle-metadata-repair-plan-{triageReport.DatasetName.ToLowerInvariant()}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            DatasetName = triageReport.DatasetName,
            ProviderId = triageReport.ProviderId,
            EmbeddingModel = triageReport.EmbeddingModel,
            Dimension = triageReport.Dimension,
            FormalRetrievalAllowed = false,
            UseForRuntime = false,
            CandidateCount = candidateCount,
            AutoRepairableCount = auto,
            HumanReviewRequiredCount = human,
            ForbiddenRepairCount = forbidden,
            CorrectlyBlockedSkippedCount = skipped,
            EstimatedRecallRecovery = auto,
            RiskAfterRepairEstimate = risk,
            Recommendation = Recommend(candidateCount, auto, human, forbidden, risk),
            Candidates = candidates
        };
    }

    private static string Recommend(int candidateCount, int auto, int human, int forbidden, int risk)
    {
        if (risk > 0 || (candidateCount > 0 && forbidden == candidateCount))
        {
            return VectorLifecycleMetadataRepairPlanRecommendations.UnsafeToRepair;
        }

        if (human > 0)
        {
            return VectorLifecycleMetadataRepairPlanRecommendations.NeedsHumanReview;
        }

        if (auto > 0)
        {
            return VectorLifecycleMetadataRepairPlanRecommendations.ReadyForMetadataRepairPreview;
        }

        return VectorLifecycleMetadataRepairPlanRecommendations.KeepPreviewOnly;
    }

    private static void AppendReport(StringBuilder builder, VectorLifecycleMetadataRepairPlanReport report)
    {
        builder.AppendLine($"## {report.DatasetName}");
        builder.AppendLine();
        builder.AppendLine($"- ProviderId: `{report.ProviderId}`");
        builder.AppendLine($"- EmbeddingModel: `{report.EmbeddingModel}`");
        builder.AppendLine($"- Dimension: `{report.Dimension}`");
        builder.AppendLine($"- CandidateCount: `{report.CandidateCount}`");
        builder.AppendLine($"- AutoRepairableCount: `{report.AutoRepairableCount}`");
        builder.AppendLine($"- HumanReviewRequiredCount: `{report.HumanReviewRequiredCount}`");
        builder.AppendLine($"- ForbiddenRepairCount: `{report.ForbiddenRepairCount}`");
        builder.AppendLine($"- CorrectlyBlockedSkippedCount: `{report.CorrectlyBlockedSkippedCount}`");
        builder.AppendLine($"- EstimatedRecallRecovery: `{report.EstimatedRecallRecovery.ToString("F2", CultureInfo.InvariantCulture)}`");
        builder.AppendLine($"- RiskAfterRepairEstimate: `{report.RiskAfterRepairEstimate}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine();
        builder.AppendLine("| Sample | MustHit | CurrentLifecycle | ProposedLifecycle | CurrentReview | ProposedReview | CurrentSection | ProposedSection | Provenance | RelationEvidence | ReviewEvidence | Confidence | Auto | HumanReview | ForbiddenReason | RepairReason |");
        builder.AppendLine("|---|---|---|---|---|---|---|---|---:|---:|---:|---:|---:|---:|---|---|");
        foreach (var candidate in report.Candidates.Take(160))
        {
            builder.AppendLine($"| {Escape(candidate.SampleId)} | {Escape(candidate.MustHitItemId)} | {Escape(candidate.CurrentLifecycle)} | {Escape(candidate.ProposedLifecycle)} | {Escape(candidate.CurrentReviewStatus)} | {Escape(candidate.ProposedReviewStatus)} | {Escape(candidate.CurrentTargetSection)} | {Escape(candidate.ProposedTargetSection)} | {candidate.ProvenanceAvailable} | {candidate.RelationEvidenceAvailable} | {candidate.ReviewEvidenceAvailable} | {candidate.RepairConfidence.ToString("F2", CultureInfo.InvariantCulture)} | {candidate.CanAutoRepair} | {candidate.RequiresHumanReview} | {Escape(candidate.ForbiddenReason)} | {Escape(candidate.RepairReason)} |");
        }
    }

    private static bool IsMetadataRepairTriage(VectorEligibilityRecallLossTriageDetail detail)
    {
        return string.Equals(detail.TriageCategory, VectorEligibilityRecallLossTriageCategories.MetadataLifecycleRepairNeeded, StringComparison.OrdinalIgnoreCase)
               || string.Equals(detail.TriageCategory, VectorEligibilityRecallLossTriageCategories.ReviewStatusRepairNeeded, StringComparison.OrdinalIgnoreCase)
               || string.Equals(detail.TriageCategory, VectorEligibilityRecallLossTriageCategories.ReplacementStateRepairNeeded, StringComparison.OrdinalIgnoreCase);
    }

    private static VectorReindexSourceItem? FindSource(
        IReadOnlyDictionary<string, VectorReindexSourceItem> sourceById,
        string itemId)
    {
        return sourceById.TryGetValue(itemId, out var source)
            ? source
            : null;
    }

    private static VectorIndexEntry? FindEntry(
        IReadOnlyDictionary<string, VectorIndexEntry> entryById,
        string itemId)
    {
        return entryById.TryGetValue(itemId, out var entry)
            ? entry
            : null;
    }

    private static Dictionary<string, string> MergeMetadata(VectorReindexSourceItem? source, VectorIndexEntry? entry)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source is not null)
        {
            foreach (var pair in source.Metadata)
            {
                metadata[pair.Key] = pair.Value;
            }
        }

        if (entry is not null)
        {
            foreach (var pair in entry.Metadata)
            {
                metadata[pair.Key] = pair.Value;
            }
        }

        return metadata;
    }

    private static bool HasAnyMetadataValue(IReadOnlyDictionary<string, string> metadata, IReadOnlyList<string> keys)
    {
        return keys.Any(key => metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value));
    }

    private static string Get(IReadOnlyDictionary<string, string> metadata, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value) && value != "-")
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string GetCombinedSourceState(
        VectorEligibilityRecallLossTriageDetail detail,
        IReadOnlyDictionary<string, string> metadata)
    {
        return string.Join(' ', detail.Lifecycle, detail.ReviewStatus, Get(metadata, "lifecycle", "sourceType", "sourceKind", "tags", "sourceTags", "reviewStatus", "status"));
    }

    private static string GetCombinedReplacementState(string replacementState, IReadOnlyDictionary<string, string> metadata)
    {
        var values = ReplacementKeys
            .Select(key => Get(metadata, key))
            .Where(static value => !string.IsNullOrWhiteSpace(value));
        return string.Join(' ', new[] { replacementState }.Concat(values));
    }

    private static bool IsActiveReviewStatus(string reviewStatus)
    {
        return ContainsAnyToken(reviewStatus, "active", "stable", "current", "approved");
    }

    private static bool IsUnsafeLifecycle(string lifecycle)
    {
        return ContainsAnyToken(lifecycle, "deprecated", "historical", "legacy", "superseded", "replaced", "rejected");
    }

    private static bool ContainsAnyToken(string value, params string[] tokens)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var token in value.Split([',', ';', '|', ' ', '/', '\\', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (tokens.Any(expected => string.Equals(token, expected, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static RepairDecision Human(string reason, string message)
    {
        return new RepairDecision(
            string.Empty,
            string.Empty,
            VectorQueryTargetSections.DiagnosticsOnly,
            0,
            message,
            CanAutoRepair: false,
            RequiresHumanReview: true,
            ForbiddenReason: reason);
    }

    private static RepairDecision Forbidden(string reason, string message)
    {
        return new RepairDecision(
            string.Empty,
            string.Empty,
            VectorQueryTargetSections.Excluded,
            0,
            message,
            CanAutoRepair: false,
            RequiresHumanReview: false,
            ForbiddenReason: reason);
    }

    private static string Escape(string value)
    {
        return value.Replace("|", "\\|", StringComparison.Ordinal);
    }

    private sealed record RepairDecision(
        string ProposedLifecycle,
        string ProposedReviewStatus,
        string ProposedTargetSection,
        double Confidence,
        string Reason,
        bool CanAutoRepair,
        bool RequiresHumanReview,
        string ForbiddenReason);
}
