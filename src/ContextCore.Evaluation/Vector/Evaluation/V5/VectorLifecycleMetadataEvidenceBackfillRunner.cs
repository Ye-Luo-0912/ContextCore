using System.Globalization;
using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>只读 evidence/provenance backfill 预览；不写 sidecar，不修改 source item。</summary>
public sealed class VectorLifecycleMetadataEvidenceBackfillRunner
{
    public VectorLifecycleMetadataEvidenceBackfillReport BuildReport(
        VectorLifecycleMetadataReviewBatch batch,
        IReadOnlyList<VectorLifecycleMetadataReviewCandidate> candidates,
        IReadOnlyDictionary<string, VectorLifecycleMetadataEvidenceSourceSnapshot> sourceSnapshots,
        string batchPath,
        string mode)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(sourceSnapshots);

        var allowed = batch.CandidateIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var details = candidates
            .Where(candidate => allowed.Count == 0 || allowed.Contains(candidate.CandidateId))
            .OrderBy(candidate => Array.IndexOf(batch.CandidateIds.ToArray(), candidate.CandidateId))
            .Select(candidate => BuildCandidateStatus(candidate, ResolveSnapshot(candidate, sourceSnapshots)))
            .ToArray();

        var evidenceFound = details.Count(static item => item.EvidenceFound);
        var sourceRefFound = details.Count(static item => item.SourceRefFound);
        var provenanceFound = details.Count(static item => item.ProvenanceFound);
        var autoRepairable = details.Count(static item => item.CanReclassifyAsAutoRepairable);
        var needsEvidence = details.Count(static item => item.ShouldRemainNeedsEvidence);
        var forbidden = details.Count(static item => item.ForbiddenToRepair);
        var conflicts = details.Count(static item => item.ReplacementConflictFound);
        var stillHuman = details.Count(static item => item.StillNeedsHumanReview);

        return new VectorLifecycleMetadataEvidenceBackfillReport
        {
            OperationId = $"vector-lifecycle-metadata-evidence-backfill-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            Mode = string.IsNullOrWhiteSpace(mode) ? "preview" : mode,
            BatchId = batch.BatchId,
            WorkspaceId = batch.WorkspaceId,
            CollectionId = batch.CollectionId,
            BatchPath = batchPath,
            CandidateCount = details.Length,
            EvidenceFoundCount = evidenceFound,
            SourceRefFoundCount = sourceRefFound,
            ProvenanceFoundCount = provenanceFound,
            AutoRepairableAfterBackfillCount = autoRepairable,
            StillHumanReviewRequiredCount = stillHuman,
            NeedsEvidenceCount = needsEvidence,
            ForbiddenRepairCount = forbidden,
            ReplacementConflictCount = conflicts,
            SourceItemUnchanged = true,
            SidecarWritten = false,
            FormalRetrievalAllowed = false,
            UseForRuntime = false,
            Recommendation = ResolveRecommendation(details.Length, autoRepairable, needsEvidence, stillHuman),
            Candidates = details,
            Diagnostics = BuildDiagnostics(details.Length, evidenceFound, sourceRefFound, provenanceFound)
        };
    }

    public static string BuildMarkdown(VectorLifecycleMetadataEvidenceBackfillReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Lifecycle Metadata Evidence Backfill");
        builder.AppendLine();
        builder.AppendLine($"- OperationId: `{report.OperationId}`");
        builder.AppendLine($"- Mode: `{report.Mode}`");
        builder.AppendLine($"- BatchId: `{report.BatchId}`");
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- CandidateCount: `{report.CandidateCount}`");
        builder.AppendLine($"- EvidenceFoundCount: `{report.EvidenceFoundCount}`");
        builder.AppendLine($"- SourceRefFoundCount: `{report.SourceRefFoundCount}`");
        builder.AppendLine($"- ProvenanceFoundCount: `{report.ProvenanceFoundCount}`");
        builder.AppendLine($"- AutoRepairableAfterBackfillCount: `{report.AutoRepairableAfterBackfillCount}`");
        builder.AppendLine($"- StillHumanReviewRequiredCount: `{report.StillHumanReviewRequiredCount}`");
        builder.AppendLine($"- NeedsEvidenceCount: `{report.NeedsEvidenceCount}`");
        builder.AppendLine($"- ForbiddenRepairCount: `{report.ForbiddenRepairCount}`");
        builder.AppendLine($"- ReplacementConflictCount: `{report.ReplacementConflictCount}`");
        builder.AppendLine($"- SourceItemUnchanged: `{report.SourceItemUnchanged}`");
        builder.AppendLine($"- SidecarWritten: `{report.SidecarWritten}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Candidate Status");
        builder.AppendLine();
        builder.AppendLine("| CandidateId | MustHitItemId | Evidence | SourceRef | Provenance | Relation | Review | Conflict | AutoRepairable | NeedsEvidence | Reason |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var candidate in report.Candidates.Take(100))
        {
            builder.AppendLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"| `{Escape(candidate.CandidateId)}` | `{Escape(candidate.MustHitItemId)}` | {candidate.EvidenceFound} | {candidate.SourceRefFound} | {candidate.ProvenanceFound} | {candidate.RelationEvidenceFound} | {candidate.ReviewEvidenceFound} | {candidate.ReplacementConflictFound} | {candidate.CanReclassifyAsAutoRepairable} | {candidate.ShouldRemainNeedsEvidence} | {Escape(candidate.Reason)} |"));
        }

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

    private static VectorLifecycleMetadataEvidenceBackfillCandidateStatus BuildCandidateStatus(
        VectorLifecycleMetadataReviewCandidate candidate,
        VectorLifecycleMetadataEvidenceSourceSnapshot? snapshot)
    {
        var sourceRefs = Merge(candidate.SourceRefs, snapshot?.SourceRefs);
        var evidenceRefs = Merge(candidate.EvidenceRefs, snapshot?.EvidenceRefs, snapshot?.RelationEvidenceRefs, snapshot?.ReviewEvidenceRefs);
        var provenanceRecord = FirstNonEmpty(snapshot?.ProvenanceRecordId, GetMetadata(candidate.Metadata, "provenanceRecordId", "provenanceId", "provenance"));
        var fingerprint = FirstNonEmpty(snapshot?.SourceFingerprint, GetMetadata(candidate.Metadata, "sourceFingerprint", "fingerprint", "contentHash"));
        var sourceKind = FirstNonEmpty(snapshot?.SourceKind, GetMetadata(candidate.Metadata, "sourceKind"), candidate.Layer);
        var itemKind = FirstNonEmpty(snapshot?.ItemKind, candidate.ItemKind);
        var lifecycle = FirstNonEmpty(snapshot?.Lifecycle, candidate.CurrentLifecycle);
        var reviewStatus = FirstNonEmpty(snapshot?.ReviewStatus, candidate.CurrentReviewStatus);
        var replacementState = FirstNonEmpty(snapshot?.ReplacementState, GetMetadata(candidate.Metadata, "replacementState"));

        var sourceRefFound = sourceRefs.Count > 0;
        var relationEvidenceFound = snapshot?.RelationEvidenceRefs.Count > 0 || candidate.RelationEvidenceAvailable;
        var reviewEvidenceFound = snapshot?.ReviewEvidenceRefs.Count > 0 || candidate.ReviewEvidenceAvailable;
        var evidenceFound = evidenceRefs.Count > 0 || relationEvidenceFound || reviewEvidenceFound;
        var provenanceFound = !string.IsNullOrWhiteSpace(provenanceRecord)
                              || !string.IsNullOrWhiteSpace(fingerprint)
                              || candidate.ProvenanceAvailable;
        var replacementConflict = IsReplacementConflict(replacementState);
        var forbidden = replacementConflict || IsForbiddenLifecycle(lifecycle);
        var safeTarget = !string.Equals(candidate.ProposedTargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase)
                         || IsNormalLifecycle(candidate.ProposedLifecycle);
        var hasRepairProposal = !string.IsNullOrWhiteSpace(candidate.ProposedLifecycle)
                                || !string.IsNullOrWhiteSpace(candidate.ProposedReviewStatus)
                                || !string.IsNullOrWhiteSpace(candidate.ProposedTargetSection);
        var autoRepairable = evidenceFound
                             && sourceRefFound
                             && provenanceFound
                             && hasRepairProposal
                             && safeTarget
                             && !forbidden;
        var needsEvidence = !autoRepairable
                            && !forbidden
                            && !evidenceFound
                            && !sourceRefFound
                            && !provenanceFound;
        var stillHuman = !autoRepairable && !needsEvidence && !forbidden;

        return new VectorLifecycleMetadataEvidenceBackfillCandidateStatus
        {
            CandidateId = candidate.CandidateId,
            MustHitItemId = candidate.MustHitItemId,
            SourceSampleId = candidate.SourceSampleId,
            SourceEvalSet = candidate.SourceEvalSet,
            ItemKind = itemKind,
            Layer = candidate.Layer,
            EvidenceFound = evidenceFound,
            SourceRefFound = sourceRefFound,
            ProvenanceFound = provenanceFound,
            RelationEvidenceFound = relationEvidenceFound,
            ReviewEvidenceFound = reviewEvidenceFound,
            ReplacementConflictFound = replacementConflict,
            CanReclassifyAsAutoRepairable = autoRepairable,
            StillNeedsHumanReview = stillHuman,
            ShouldRemainNeedsEvidence = needsEvidence,
            ForbiddenToRepair = forbidden,
            Reason = ResolveReason(autoRepairable, needsEvidence, forbidden, replacementConflict, evidenceFound, sourceRefFound, provenanceFound),
            SourceRefs = sourceRefs,
            EvidenceRefs = evidenceRefs,
            ProvenanceRecordId = provenanceRecord,
            SourceFingerprint = fingerprint,
            SourceKind = sourceKind,
            Lifecycle = lifecycle,
            ReviewStatus = reviewStatus,
            ReplacementState = replacementState
        };
    }

    private static VectorLifecycleMetadataEvidenceSourceSnapshot? ResolveSnapshot(
        VectorLifecycleMetadataReviewCandidate candidate,
        IReadOnlyDictionary<string, VectorLifecycleMetadataEvidenceSourceSnapshot> snapshots)
    {
        return snapshots.TryGetValue(candidate.MustHitItemId, out var snapshot)
            ? snapshot
            : null;
    }

    private static string ResolveRecommendation(
        int candidateCount,
        int autoRepairable,
        int needsEvidence,
        int stillHuman)
    {
        if (candidateCount == 0)
        {
            return "KeepPreviewOnly";
        }

        if (autoRepairable > 0)
        {
            return "ReadyForRepairPlanRerun";
        }

        if (needsEvidence > 0)
        {
            return "NeedsIngestionMetadataBackfill";
        }

        return stillHuman > 0 ? "NeedsHumanReview" : "KeepPreviewOnly";
    }

    private static IReadOnlyList<string> BuildDiagnostics(
        int candidateCount,
        int evidenceFound,
        int sourceRefFound,
        int provenanceFound)
    {
        var diagnostics = new List<string>(capacity: 4);
        if (candidateCount == 0)
        {
            diagnostics.Add("No candidates were loaded from the review batch.");
            return diagnostics;
        }

        if (evidenceFound == 0)
        {
            diagnostics.Add("No evidence refs were found for the loaded candidates.");
        }

        if (sourceRefFound == 0)
        {
            diagnostics.Add("No source refs were found for the loaded candidates.");
        }

        if (provenanceFound == 0)
        {
            diagnostics.Add("No provenance record or source fingerprint was found for the loaded candidates.");
        }

        return diagnostics;
    }

    private static string ResolveReason(
        bool autoRepairable,
        bool needsEvidence,
        bool forbidden,
        bool replacementConflict,
        bool evidenceFound,
        bool sourceRefFound,
        bool provenanceFound)
    {
        if (autoRepairable)
        {
            return "EvidenceBackfilledAndSafeForRepairPlanRerun";
        }

        if (replacementConflict)
        {
            return "ReplacementConflictFound";
        }

        if (forbidden)
        {
            return "ForbiddenLifecycleState";
        }

        if (needsEvidence)
        {
            return "MissingEvidenceSourceRefAndProvenance";
        }

        if (!provenanceFound)
        {
            return "EvidenceFoundButMissingProvenance";
        }

        if (!sourceRefFound)
        {
            return "EvidenceFoundButMissingSourceRef";
        }

        if (!evidenceFound)
        {
            return "SourceRefFoundButMissingEvidence";
        }

        return "EvidenceFoundButStillRequiresHumanReview";
    }

    private static bool IsReplacementConflict(string value)
    {
        return ContainsAny(value, "superseded", "replaced", "deprecated", "conflict");
    }

    private static bool IsForbiddenLifecycle(string value)
    {
        return string.Equals(value, "Deprecated", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "Historical", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "Superseded", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "Rejected", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNormalLifecycle(string value)
    {
        return string.Equals(value, "Active", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "Current", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "Stable", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        return !string.IsNullOrWhiteSpace(value)
               && needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> Merge(params IReadOnlyList<string>?[] lists)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in lists)
        {
            if (list is null)
            {
                continue;
            }

            foreach (var value in list)
            {
                if (!string.IsNullOrWhiteSpace(value) && seen.Add(value.Trim()))
                {
                    result.Add(value.Trim());
                }
            }
        }

        return result;
    }

    private static string GetMetadata(
        IReadOnlyDictionary<string, string> metadata,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string Escape(string? value)
        => (value ?? string.Empty)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}
