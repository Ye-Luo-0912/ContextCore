using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>组织 lifecycle metadata 人工 review batch；只做导出、校验和 apply preview，不写真实 sidecar。</summary>
public sealed class VectorLifecycleMetadataReviewBatchService
{
    private const string GeneratedBy = "vector-lifecycle-metadata-review-batch-service/v1";

    public VectorLifecycleMetadataReviewBatch CreateBatch(
        string workspaceId,
        string collectionId,
        IReadOnlyList<VectorLifecycleMetadataReviewCandidate> candidates,
        string createdBy,
        string reviewInstructions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentNullException.ThrowIfNull(candidates);

        var pending = candidates
            .Where(static item => string.Equals(item.Status, VectorLifecycleMetadataReviewCandidateStatuses.PendingReview, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static item => item.Layer, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.ItemKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.MustHitItemId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var batchId = BuildBatchId(workspaceId, collectionId, pending);

        return new VectorLifecycleMetadataReviewBatch
        {
            BatchId = batchId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            CandidateIds = pending.Select(static item => item.CandidateId).ToArray(),
            CandidateCount = pending.Length,
            Status = VectorLifecycleMetadataReviewBatchStatuses.Draft,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? "local-eval" : createdBy.Trim(),
            ReviewInstructions = string.IsNullOrWhiteSpace(reviewInstructions)
                ? "Review each candidate manually. Leave ReviewerDecision empty until evidence is checked."
                : reviewInstructions.Trim(),
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["generatedBy"] = GeneratedBy,
                ["autoApprove"] = "false",
                ["realSidecarWrite"] = "false",
                ["runtimeEffect"] = "false",
                ["formalRetrievalAllowed"] = "false"
            }
        };
    }

    public IReadOnlyList<VectorLifecycleMetadataReviewSheetRow> ExportReviewSheet(
        VectorLifecycleMetadataReviewBatch batch,
        IReadOnlyList<VectorLifecycleMetadataReviewCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(candidates);
        var byId = candidates.ToDictionary(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase);
        return batch.CandidateIds
            .Where(byId.ContainsKey)
            .Select(id => ToReviewSheetRow(byId[id]))
            .ToArray();
    }

    public VectorLifecycleMetadataReviewBatchImportResult BuildImportResult(
        string batchId,
        IReadOnlyList<VectorLifecycleMetadataReviewSheetRow> rows,
        IReadOnlyList<string>? diagnostics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentNullException.ThrowIfNull(rows);
        return new VectorLifecycleMetadataReviewBatchImportResult
        {
            BatchId = batchId,
            ImportedAt = DateTimeOffset.UtcNow,
            RowCount = rows.Count,
            DecisionCount = rows.Count(static item => !string.IsNullOrWhiteSpace(item.ReviewerDecision)),
            Imported = true,
            Diagnostics = diagnostics?.ToArray() ?? Array.Empty<string>()
        };
    }

    public VectorLifecycleMetadataReviewBatchValidationReport Validate(
        VectorLifecycleMetadataReviewBatch batch,
        IReadOnlyList<VectorLifecycleMetadataReviewCandidate> candidates,
        IReadOnlyList<VectorLifecycleMetadataReviewSheetRow> rows)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(rows);

        var byCandidate = candidates.ToDictionary(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase);
        var issues = new List<VectorLifecycleMetadataReviewBatchValidationIssue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var approvals = 0;
        var rejected = 0;
        var needsEvidence = 0;
        var superseded = 0;
        var unsafeCount = 0;
        var missingEvidence = 0;
        var missingReviewer = 0;
        var missingReason = 0;

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.CandidateId))
            {
                AddIssue(issues, row.CandidateId, "MissingCandidateId", "Review sheet row is missing CandidateId.");
                continue;
            }

            if (!seen.Add(row.CandidateId))
            {
                AddIssue(issues, row.CandidateId, "DuplicateCandidateDecision", "Duplicate candidate decisions are not allowed; last-write-wins is disabled.");
                continue;
            }

            if (!byCandidate.TryGetValue(row.CandidateId, out var candidate))
            {
                AddIssue(issues, row.CandidateId, "UnknownCandidate", "Review sheet row does not match a batch candidate.");
                continue;
            }

            var decision = NormalizeDecision(row.ReviewerDecision);
            if (string.IsNullOrWhiteSpace(decision))
            {
                continue;
            }

            if (!IsKnownDecision(decision))
            {
                AddIssue(issues, row.CandidateId, "UnknownDecision", $"Unknown reviewer decision: {row.ReviewerDecision}");
                continue;
            }

            if (string.Equals(decision, VectorLifecycleMetadataReviewDecisions.ApproveForSidecar, StringComparison.OrdinalIgnoreCase))
            {
                approvals++;
                if (string.IsNullOrWhiteSpace(row.Reviewer))
                {
                    missingReviewer++;
                    AddIssue(issues, row.CandidateId, "MissingReviewer", "ApproveForSidecar requires reviewer.");
                }

                if (string.IsNullOrWhiteSpace(row.ReviewerReason))
                {
                    missingReason++;
                    AddIssue(issues, row.CandidateId, "MissingReviewerReason", "ApproveForSidecar requires reviewer reason.");
                }

                if (row.EvidenceRefs.Count == 0 && row.SourceRefs.Count == 0)
                {
                    missingEvidence++;
                    AddIssue(issues, row.CandidateId, "MissingEvidenceOrSourceRefs", "ApproveForSidecar requires evidenceRefs or sourceRefs.");
                }

                if (IsUnsafeNormalContextApproval(candidate, row))
                {
                    unsafeCount++;
                    AddIssue(issues, row.CandidateId, "UnsafeNormalContextApproval", "Deprecated, historical, superseded, rejected, or non-active lifecycle cannot approve into normal_context.");
                }

                if (IsEvalLabelOnly(row))
                {
                    AddIssue(issues, row.CandidateId, "OnlyEvalLabelInsufficient", "Eval label alone is not sufficient to approve sidecar metadata.");
                }
            }
            else if (string.Equals(decision, VectorLifecycleMetadataReviewDecisions.Reject, StringComparison.OrdinalIgnoreCase))
            {
                rejected++;
            }
            else if (string.Equals(decision, VectorLifecycleMetadataReviewDecisions.NeedsEvidence, StringComparison.OrdinalIgnoreCase))
            {
                needsEvidence++;
            }
            else if (string.Equals(decision, VectorLifecycleMetadataReviewDecisions.Supersede, StringComparison.OrdinalIgnoreCase))
            {
                superseded++;
            }
        }

        var recommendation = ResolveValidationRecommendation(rows, approvals, needsEvidence, issues, unsafeCount, missingEvidence);
        return new VectorLifecycleMetadataReviewBatchValidationReport
        {
            BatchId = batch.BatchId,
            CreatedAt = DateTimeOffset.UtcNow,
            CandidateCount = batch.CandidateCount,
            RowCount = rows.Count,
            DecisionCount = rows.Count(static item => !string.IsNullOrWhiteSpace(item.ReviewerDecision)),
            ApprovalCount = approvals,
            RejectCount = rejected,
            NeedsEvidenceCount = needsEvidence,
            SupersedeCount = superseded,
            ValidationErrorCount = issues.Count,
            UnsafeDecisionCount = unsafeCount,
            MissingEvidenceCount = missingEvidence,
            MissingReviewerCount = missingReviewer,
            MissingReviewerReasonCount = missingReason,
            LastWriteWins = false,
            Recommendation = recommendation,
            Issues = issues,
            FormalRetrievalAllowed = false,
            UseForRuntime = false
        };
    }

    public VectorLifecycleMetadataReviewBatchApplyPreviewReport BuildApplyPreview(
        VectorLifecycleMetadataReviewBatch batch,
        IReadOnlyList<VectorLifecycleMetadataReviewCandidate> candidates,
        IReadOnlyList<VectorLifecycleMetadataReviewSheetRow> rows,
        VectorLifecycleMetadataReviewBatchValidationReport validation)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(validation);

        var byCandidate = candidates.ToDictionary(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase);
        var blockedCandidateIds = validation.Issues
            .Select(static item => item.CandidateId)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var approvedRows = rows
            .Where(static item => string.Equals(NormalizeDecision(item.ReviewerDecision), VectorLifecycleMetadataReviewDecisions.ApproveForSidecar, StringComparison.OrdinalIgnoreCase))
            .Where(item => byCandidate.ContainsKey(item.CandidateId))
            .Where(item => !blockedCandidateIds.Contains(item.CandidateId))
            .ToArray();
        var normal = 0;
        var audit = 0;
        var historical = 0;
        var diagnostics = 0;
        var changed = 0;
        foreach (var row in approvedRows)
        {
            var candidate = byCandidate[row.CandidateId];
            var target = ResolveTargetSection(row, candidate);
            if (string.Equals(target, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase))
            {
                normal++;
            }
            else if (string.Equals(target, VectorQueryTargetSections.AuditContext, StringComparison.OrdinalIgnoreCase))
            {
                audit++;
            }
            else if (string.Equals(target, VectorQueryTargetSections.HistoricalContext, StringComparison.OrdinalIgnoreCase))
            {
                historical++;
            }
            else if (string.Equals(target, VectorQueryTargetSections.DiagnosticsOnly, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics++;
            }

            if (!string.Equals(candidate.CurrentLifecycle, candidate.ProposedLifecycle, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(candidate.CurrentTargetSection, target, StringComparison.OrdinalIgnoreCase))
            {
                changed++;
            }
        }

        return new VectorLifecycleMetadataReviewBatchApplyPreviewReport
        {
            BatchId = batch.BatchId,
            CreatedAt = DateTimeOffset.UtcNow,
            CandidateCount = batch.CandidateCount,
            DecisionCount = validation.DecisionCount,
            WouldWriteSidecarEntryCount = approvedRows.Length,
            UnsafeBlockedCount = validation.UnsafeDecisionCount,
            NormalContextApprovalCount = normal,
            AuditContextApprovalCount = audit,
            HistoricalContextApprovalCount = historical,
            DiagnosticsOnlyApprovalCount = diagnostics,
            EffectiveMetadataChangedCount = changed,
            RealSidecarWritten = false,
            FormalRetrievalAllowed = false,
            UseForRuntime = false,
            Recommendation = validation.ValidationErrorCount > 0
                ? validation.UnsafeDecisionCount > 0
                    ? "BlockedByUnsafeDecision"
                    : validation.MissingEvidenceCount > 0
                        ? "BlockedByMissingEvidence"
                        : "BlockedByValidationError"
                : approvedRows.Length > 0
                    ? "ReadyForSidecarApply"
                    : validation.NeedsEvidenceCount > 0
                        ? "NeedsEvidence"
                        : "NeedsReviewerInput",
            Diagnostics = validation.ValidationErrorCount > 0
                ? validation.Issues.Select(static item => $"{item.CandidateId}:{item.Reason}").ToArray()
                : approvedRows.Length > 0
                    ? ["Apply preview only; no real sidecar was written."]
                    : ["No approved decisions were present in the review sheet."]
        };
    }

    public static VectorLifecycleMetadataReviewBatch WithStatus(
        VectorLifecycleMetadataReviewBatch batch,
        string status)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return new VectorLifecycleMetadataReviewBatch
        {
            BatchId = batch.BatchId,
            WorkspaceId = batch.WorkspaceId,
            CollectionId = batch.CollectionId,
            CandidateIds = batch.CandidateIds.ToArray(),
            CandidateCount = batch.CandidateCount,
            Status = status,
            CreatedAt = batch.CreatedAt,
            CreatedBy = batch.CreatedBy,
            ReviewInstructions = batch.ReviewInstructions,
            Metadata = new Dictionary<string, string>(batch.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    public static string BuildReviewSheetMarkdown(
        VectorLifecycleMetadataReviewBatch batch,
        IReadOnlyList<VectorLifecycleMetadataReviewSheetRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Lifecycle Metadata Review Batch");
        builder.AppendLine();
        builder.AppendLine($"- BatchId: `{batch.BatchId}`");
        builder.AppendLine($"- WorkspaceId: `{batch.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{batch.CollectionId}`");
        builder.AppendLine($"- CandidateCount: `{batch.CandidateCount}`");
        builder.AppendLine($"- Status: `{batch.Status}`");
        builder.AppendLine($"- Instructions: {batch.ReviewInstructions}");
        builder.AppendLine();
        builder.AppendLine("| CandidateId | MustHitItemId | CurrentLifecycle | ProposedLifecycle | CurrentTargetSection | ProposedTargetSection | ReviewerDecision | Reviewer |");
        builder.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var row in rows)
        {
            builder.AppendLine($"| `{row.CandidateId}` | `{row.MustHitItemId}` | `{row.CurrentLifecycle}` | `{row.ProposedLifecycle}` | `{row.CurrentTargetSection}` | `{row.ProposedTargetSection}` | `{row.ReviewerDecision}` | `{row.Reviewer}` |");
        }

        return builder.ToString();
    }

    public static string BuildValidationMarkdown(VectorLifecycleMetadataReviewBatchValidationReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Lifecycle Metadata Review Batch Validation");
        builder.AppendLine();
        builder.AppendLine($"- BatchId: `{report.BatchId}`");
        builder.AppendLine($"- CandidateCount: `{report.CandidateCount}`");
        builder.AppendLine($"- RowCount: `{report.RowCount}`");
        builder.AppendLine($"- DecisionCount: `{report.DecisionCount}`");
        builder.AppendLine($"- ApprovalCount: `{report.ApprovalCount}`");
        builder.AppendLine($"- RejectCount: `{report.RejectCount}`");
        builder.AppendLine($"- NeedsEvidenceCount: `{report.NeedsEvidenceCount}`");
        builder.AppendLine($"- SupersedeCount: `{report.SupersedeCount}`");
        builder.AppendLine($"- ValidationErrorCount: `{report.ValidationErrorCount}`");
        builder.AppendLine($"- UnsafeDecisionCount: `{report.UnsafeDecisionCount}`");
        builder.AppendLine($"- MissingEvidenceCount: `{report.MissingEvidenceCount}`");
        builder.AppendLine($"- MissingReviewerCount: `{report.MissingReviewerCount}`");
        builder.AppendLine($"- MissingReviewerReasonCount: `{report.MissingReviewerReasonCount}`");
        builder.AppendLine($"- LastWriteWins: `{report.LastWriteWins}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        if (report.Issues.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Issues");
            foreach (var issue in report.Issues)
            {
                builder.AppendLine($"- `{issue.CandidateId}` {issue.Reason}: {issue.Message}");
            }
        }

        return builder.ToString();
    }

    public static string BuildImportSmokeMarkdown(VectorLifecycleMetadataReviewBatchImportSmokeReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Lifecycle Metadata Review Batch Import Smoke");
        builder.AppendLine();
        builder.AppendLine($"- OperationId: `{report.OperationId}`");
        builder.AppendLine($"- BatchId: `{report.BatchId}`");
        builder.AppendLine($"- SmokePassed: `{report.SmokePassed}`");
        builder.AppendLine($"- ImportedRowCount: `{report.ImportedRowCount}`");
        builder.AppendLine($"- ValidDecisionCount: `{report.ValidDecisionCount}`");
        builder.AppendLine($"- InvalidDecisionCount: `{report.InvalidDecisionCount}`");
        builder.AppendLine($"- DuplicateDecisionBlockedCount: `{report.DuplicateDecisionBlockedCount}`");
        builder.AppendLine($"- UnknownDecisionBlockedCount: `{report.UnknownDecisionBlockedCount}`");
        builder.AppendLine($"- MissingReviewerBlockedCount: `{report.MissingReviewerBlockedCount}`");
        builder.AppendLine($"- MissingReasonBlockedCount: `{report.MissingReasonBlockedCount}`");
        builder.AppendLine($"- MissingEvidenceBlockedCount: `{report.MissingEvidenceBlockedCount}`");
        builder.AppendLine($"- UnsafeNormalContextBlockedCount: `{report.UnsafeNormalContextBlockedCount}`");
        builder.AppendLine($"- WouldWriteSidecarCount: `{report.WouldWriteSidecarCount}`");
        builder.AppendLine($"- ActualSidecarWriteCount: `{report.ActualSidecarWriteCount}`");
        builder.AppendLine($"- SourceItemUnchanged: `{report.SourceItemUnchanged}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- StatusFlow: `{report.InitialStatus} -> {report.ExportedStatus} -> {report.ImportedStatus} -> {report.ValidatedStatus}`");
        builder.AppendLine($"- ValidationRecommendation: `{report.ValidationRecommendation}`");
        builder.AppendLine($"- ApplyPreviewRecommendation: `{report.ApplyPreviewRecommendation}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        if (report.Diagnostics.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Diagnostics");
            foreach (var diagnostic in report.Diagnostics)
            {
                builder.AppendLine($"- {diagnostic}");
            }
        }

        return builder.ToString();
    }

    public static string BuildApplyPreviewMarkdown(VectorLifecycleMetadataReviewBatchApplyPreviewReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Lifecycle Metadata Review Batch Apply Preview");
        builder.AppendLine();
        builder.AppendLine($"- BatchId: `{report.BatchId}`");
        builder.AppendLine($"- CandidateCount: `{report.CandidateCount}`");
        builder.AppendLine($"- DecisionCount: `{report.DecisionCount}`");
        builder.AppendLine($"- WouldWriteSidecarEntryCount: `{report.WouldWriteSidecarEntryCount}`");
        builder.AppendLine($"- UnsafeBlockedCount: `{report.UnsafeBlockedCount}`");
        builder.AppendLine($"- Normal/Audit/Historical/Diagnostics: `{report.NormalContextApprovalCount}/{report.AuditContextApprovalCount}/{report.HistoricalContextApprovalCount}/{report.DiagnosticsOnlyApprovalCount}`");
        builder.AppendLine($"- EffectiveMetadataChangedCount: `{report.EffectiveMetadataChangedCount}`");
        builder.AppendLine($"- RealSidecarWritten: `{report.RealSidecarWritten}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        if (report.Diagnostics.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Diagnostics");
            foreach (var diagnostic in report.Diagnostics)
            {
                builder.AppendLine($"- {diagnostic}");
            }
        }

        return builder.ToString();
    }

    private static VectorLifecycleMetadataReviewSheetRow ToReviewSheetRow(VectorLifecycleMetadataReviewCandidate candidate)
    {
        return new VectorLifecycleMetadataReviewSheetRow
        {
            CandidateId = candidate.CandidateId,
            MustHitItemId = candidate.MustHitItemId,
            CurrentLifecycle = candidate.CurrentLifecycle,
            ProposedLifecycle = candidate.ProposedLifecycle,
            CurrentTargetSection = candidate.CurrentTargetSection,
            ProposedTargetSection = candidate.ProposedTargetSection,
            EvidenceRefs = candidate.EvidenceRefs.ToArray(),
            SourceRefs = candidate.SourceRefs.ToArray(),
            RepairReason = candidate.RepairReason,
            ReviewerDecision = string.Empty,
            ReviewerReason = string.Empty,
            Reviewer = string.Empty,
            TargetSectionOverride = candidate.ProposedTargetSection,
            Notes = string.Empty
        };
    }

    private static string BuildBatchId(
        string workspaceId,
        string collectionId,
        IReadOnlyList<VectorLifecycleMetadataReviewCandidate> candidates)
    {
        var input = string.Join('|', candidates.Select(static item => item.CandidateId));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{workspaceId}|{collectionId}|{input}"));
        return "vlmrb-" + Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }

    private static void AddIssue(
        List<VectorLifecycleMetadataReviewBatchValidationIssue> issues,
        string candidateId,
        string reason,
        string message)
    {
        issues.Add(new VectorLifecycleMetadataReviewBatchValidationIssue
        {
            CandidateId = candidateId,
            Severity = "Error",
            Reason = reason,
            Message = message
        });
    }

    private static string NormalizeDecision(string decision)
    {
        return decision.Trim();
    }

    private static bool IsKnownDecision(string decision)
    {
        return string.Equals(decision, VectorLifecycleMetadataReviewDecisions.ApproveForSidecar, StringComparison.OrdinalIgnoreCase)
               || string.Equals(decision, VectorLifecycleMetadataReviewDecisions.Reject, StringComparison.OrdinalIgnoreCase)
               || string.Equals(decision, VectorLifecycleMetadataReviewDecisions.NeedsEvidence, StringComparison.OrdinalIgnoreCase)
               || string.Equals(decision, VectorLifecycleMetadataReviewDecisions.Supersede, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnsafeNormalContextApproval(
        VectorLifecycleMetadataReviewCandidate candidate,
        VectorLifecycleMetadataReviewSheetRow row)
    {
        var target = ResolveTargetSection(row, candidate);
        if (!string.Equals(target, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsLifecycle(candidate.CurrentLifecycle, "Deprecated")
               || IsLifecycle(candidate.CurrentLifecycle, "Historical")
               || IsLifecycle(candidate.CurrentLifecycle, "Superseded")
               || IsLifecycle(candidate.CurrentLifecycle, "Rejected")
               || !IsNormalLifecycle(candidate.ProposedLifecycle);
    }

    private static string ResolveTargetSection(
        VectorLifecycleMetadataReviewSheetRow row,
        VectorLifecycleMetadataReviewCandidate candidate)
    {
        return string.IsNullOrWhiteSpace(row.TargetSectionOverride)
            ? candidate.ProposedTargetSection
            : row.TargetSectionOverride.Trim();
    }

    private static bool IsLifecycle(string lifecycle, string expected)
    {
        return string.Equals(lifecycle, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNormalLifecycle(string lifecycle)
    {
        return IsLifecycle(lifecycle, "Active")
               || IsLifecycle(lifecycle, "Current")
               || IsLifecycle(lifecycle, "Stable");
    }

    private static bool IsEvalLabelOnly(VectorLifecycleMetadataReviewSheetRow row)
    {
        return row.EvidenceRefs.Count == 0
               && row.SourceRefs.Count == 0
               && row.Notes.Contains("eval label", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveValidationRecommendation(
        IReadOnlyList<VectorLifecycleMetadataReviewSheetRow> rows,
        int approvalCount,
        int needsEvidenceDecisionCount,
        IReadOnlyList<VectorLifecycleMetadataReviewBatchValidationIssue> issues,
        int unsafeCount,
        int missingEvidence)
    {
        if (unsafeCount > 0)
        {
            return "BlockedByUnsafeDecision";
        }

        if (missingEvidence > 0)
        {
            return "BlockedByMissingEvidence";
        }

        if (issues.Count > 0)
        {
            return "BlockedByValidationError";
        }

        if (rows.All(static item => string.IsNullOrWhiteSpace(item.ReviewerDecision)))
        {
            return "NeedsReviewerInput";
        }

        if (approvalCount == 0 && needsEvidenceDecisionCount > 0)
        {
            return "NeedsEvidence";
        }

        return approvalCount > 0 ? "ReadyForSidecarApply" : "ReadyForManualReview";
    }
}
