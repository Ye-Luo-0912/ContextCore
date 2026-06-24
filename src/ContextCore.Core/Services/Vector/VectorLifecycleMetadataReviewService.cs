using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 处理 vector lifecycle metadata 人工 review 决策；只写 review 历史和 sidecar，不修改业务 source item。
/// </summary>
public sealed class VectorLifecycleMetadataReviewService
{
    public const string GeneratedBy = "vector-lifecycle-metadata-review-service/v1";

    private readonly IVectorLifecycleMetadataReviewCandidateStore _candidateStore;
    private readonly IVectorLifecycleMetadataReviewStore _reviewStore;
    private readonly IVectorLifecycleSidecarMetadataStore _sidecarStore;

    public VectorLifecycleMetadataReviewService(
        IVectorLifecycleMetadataReviewCandidateStore candidateStore,
        IVectorLifecycleMetadataReviewStore reviewStore,
        IVectorLifecycleSidecarMetadataStore sidecarStore)
    {
        _candidateStore = candidateStore;
        _reviewStore = reviewStore;
        _sidecarStore = sidecarStore;
    }

    public async Task<VectorLifecycleMetadataReviewResult> ReviewAsync(
        VectorLifecycleMetadataReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CandidateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Decision);

        var candidate = await _candidateStore.GetAsync(request.CandidateId, cancellationToken)
            .ConfigureAwait(false);
        if (candidate is null)
        {
            return Failed(request, "CandidateNotFound");
        }

        var normalizedDecision = NormalizeDecision(request.Decision);
        if (string.IsNullOrWhiteSpace(normalizedDecision))
        {
            return Failed(request, "UnsupportedDecision");
        }

        if (string.Equals(normalizedDecision, VectorLifecycleMetadataReviewDecisions.ApproveForSidecar, StringComparison.Ordinal))
        {
            var validationError = ValidateApproveRequest(request, candidate);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                return await PersistBlockedReviewAsync(candidate, request, normalizedDecision, validationError, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var now = DateTimeOffset.UtcNow;
        var status = ToStatus(normalizedDecision);
        var reviewId = BuildReviewId(candidate.CandidateId, normalizedDecision, request.Reviewer, request.Reason, EffectiveLifecycle(request, candidate), EffectiveReviewStatus(request, candidate), EffectiveTargetSection(request, candidate));
        var sidecar = string.Equals(normalizedDecision, VectorLifecycleMetadataReviewDecisions.ApproveForSidecar, StringComparison.Ordinal)
            ? BuildSidecarEntry(candidate, request, reviewId, now)
            : null;
        var record = BuildRecord(candidate, request, normalizedDecision, status, reviewId, now, sidecarWritten: sidecar is not null, unsafeApprovalBlocked: false, blockedReason: string.Empty);

        await _reviewStore.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        if (sidecar is not null)
        {
            await _sidecarStore.SaveAsync(sidecar, cancellationToken).ConfigureAwait(false);
        }

        await _candidateStore.SaveAsync(CopyCandidateWithStatus(candidate, status), cancellationToken)
            .ConfigureAwait(false);

        return new VectorLifecycleMetadataReviewResult
        {
            OperationId = $"vector-lifecycle-metadata-review-{Guid.NewGuid():N}",
            CreatedAt = now,
            Succeeded = true,
            CandidateId = candidate.CandidateId,
            Decision = normalizedDecision,
            CandidateStatus = status,
            SidecarWritten = sidecar is not null,
            SourceItemUnchanged = true,
            Review = record,
            SidecarEntry = sidecar
        };
    }

    public Task<IReadOnlyList<VectorLifecycleMetadataReviewRecord>> ListReviewsAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
        => _reviewStore.ListAsync(candidateId, cancellationToken);

    public Task<IReadOnlyList<VectorLifecycleSidecarMetadataEntry>> ListSidecarAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
        => _sidecarStore.QueryAsync(workspaceId, collectionId, cancellationToken);

    public async Task<VectorLifecycleMetadataReviewSummaryReport> BuildSummaryAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        var candidates = await _candidateStore.QueryAsync(new VectorLifecycleMetadataReviewCandidateQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Limit = 10_000
        }, cancellationToken).ConfigureAwait(false);
        var reviews = await _reviewStore.QueryAsync(workspaceId, collectionId, cancellationToken)
            .ConfigureAwait(false);
        var sidecars = await _sidecarStore.QueryAsync(workspaceId, collectionId, cancellationToken)
            .ConfigureAwait(false);

        return BuildSummary(candidates, reviews, sidecars);
    }

    public static VectorLifecycleMetadataReviewSummaryReport BuildSummary(
        IReadOnlyList<VectorLifecycleMetadataReviewCandidate> candidates,
        IReadOnlyList<VectorLifecycleMetadataReviewRecord> reviews,
        IReadOnlyList<VectorLifecycleSidecarMetadataEntry> sidecars)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(reviews);
        ArgumentNullException.ThrowIfNull(sidecars);

        return new VectorLifecycleMetadataReviewSummaryReport
        {
            OperationId = $"vector-lifecycle-metadata-review-summary-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CandidateCount = candidates.Count,
            PendingCount = CountStatus(candidates, VectorLifecycleMetadataReviewCandidateStatuses.PendingReview),
            ApprovedForSidecarCount = CountStatus(candidates, VectorLifecycleMetadataReviewCandidateStatuses.ApprovedForSidecar),
            RejectedCount = CountStatus(candidates, VectorLifecycleMetadataReviewCandidateStatuses.Rejected),
            NeedsEvidenceCount = CountStatus(candidates, VectorLifecycleMetadataReviewCandidateStatuses.NeedsEvidence),
            SupersededCount = CountStatus(candidates, VectorLifecycleMetadataReviewCandidateStatuses.Superseded),
            SidecarEntryCount = sidecars.Count,
            NormalContextApprovalCount = CountTarget(sidecars, VectorQueryTargetSections.NormalContext),
            AuditContextApprovalCount = CountTarget(sidecars, VectorQueryTargetSections.AuditContext),
            HistoricalContextApprovalCount = CountTarget(sidecars, VectorQueryTargetSections.HistoricalContext),
            DiagnosticsOnlyApprovalCount = CountTarget(sidecars, VectorQueryTargetSections.DiagnosticsOnly),
            UnsafeApprovalBlockedCount = reviews.Count(static item => item.UnsafeApprovalBlocked),
            Recommendation = sidecars.Count > 0 ? "ReadyForSidecarReevaluation" : VectorLifecycleMetadataRepairPlanRecommendations.NeedsHumanReview,
            FormalRetrievalAllowed = false,
            UseForRuntime = false,
            RecentReviews =
            [
                .. reviews
                    .OrderByDescending(static item => item.ReviewedAt)
                    .Take(20)
            ]
        };
    }

    public static VectorLifecycleMetadataSidecarPreviewReport BuildSidecarPreview(
        IReadOnlyList<VectorLifecycleSidecarMetadataEntry> sidecars)
    {
        ArgumentNullException.ThrowIfNull(sidecars);
        return new VectorLifecycleMetadataSidecarPreviewReport
        {
            OperationId = $"vector-lifecycle-metadata-sidecar-preview-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            SidecarEntryCount = sidecars.Count,
            NormalContextEntryCount = CountTarget(sidecars, VectorQueryTargetSections.NormalContext),
            AuditContextEntryCount = CountTarget(sidecars, VectorQueryTargetSections.AuditContext),
            HistoricalContextEntryCount = CountTarget(sidecars, VectorQueryTargetSections.HistoricalContext),
            DiagnosticsOnlyEntryCount = CountTarget(sidecars, VectorQueryTargetSections.DiagnosticsOnly),
            SourceItemUnchanged = true,
            FormalRetrievalAllowed = false,
            UseForRuntime = false,
            Entries =
            [
                .. sidecars
                    .OrderByDescending(static item => item.CreatedAt)
                    .Take(100)
            ]
        };
    }

    public static string BuildMarkdownSummary(VectorLifecycleMetadataReviewSummaryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Lifecycle Metadata Review Summary");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.CreatedAt:O}");
        builder.AppendLine($"- CandidateCount: `{report.CandidateCount}`");
        builder.AppendLine($"- PendingCount: `{report.PendingCount}`");
        builder.AppendLine($"- ApprovedForSidecarCount: `{report.ApprovedForSidecarCount}`");
        builder.AppendLine($"- RejectedCount: `{report.RejectedCount}`");
        builder.AppendLine($"- NeedsEvidenceCount: `{report.NeedsEvidenceCount}`");
        builder.AppendLine($"- SupersededCount: `{report.SupersededCount}`");
        builder.AppendLine($"- SidecarEntryCount: `{report.SidecarEntryCount}`");
        builder.AppendLine($"- NormalContextApprovalCount: `{report.NormalContextApprovalCount}`");
        builder.AppendLine($"- AuditContextApprovalCount: `{report.AuditContextApprovalCount}`");
        builder.AppendLine($"- HistoricalContextApprovalCount: `{report.HistoricalContextApprovalCount}`");
        builder.AppendLine($"- DiagnosticsOnlyApprovalCount: `{report.DiagnosticsOnlyApprovalCount}`");
        builder.AppendLine($"- UnsafeApprovalBlockedCount: `{report.UnsafeApprovalBlockedCount}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine();
        builder.AppendLine("## Recent Reviews");
        builder.AppendLine();
        builder.AppendLine("| ReviewId | CandidateId | Decision | Status | Reviewer | Sidecar | Blocked | TargetSection |");
        builder.AppendLine("|---|---|---|---|---|---:|---|---|");
        foreach (var review in report.RecentReviews)
        {
            builder.AppendLine($"| {Escape(review.ReviewId)} | {Escape(review.CandidateId)} | {Escape(review.Decision)} | {Escape(review.ResultStatus)} | {Escape(review.Reviewer)} | {review.SidecarWritten} | {Escape(review.BlockedReason)} | {Escape(review.ProposedTargetSection)} |");
        }

        return builder.ToString();
    }

    public static string BuildMarkdownSidecarPreview(VectorLifecycleMetadataSidecarPreviewReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Lifecycle Metadata Sidecar Preview");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.CreatedAt:O}");
        builder.AppendLine($"- SidecarEntryCount: `{report.SidecarEntryCount}`");
        builder.AppendLine($"- NormalContextEntryCount: `{report.NormalContextEntryCount}`");
        builder.AppendLine($"- AuditContextEntryCount: `{report.AuditContextEntryCount}`");
        builder.AppendLine($"- HistoricalContextEntryCount: `{report.HistoricalContextEntryCount}`");
        builder.AppendLine($"- DiagnosticsOnlyEntryCount: `{report.DiagnosticsOnlyEntryCount}`");
        builder.AppendLine($"- SourceItemUnchanged: `{report.SourceItemUnchanged}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine();
        builder.AppendLine("## Entries");
        builder.AppendLine();
        builder.AppendLine("| ItemId | Lifecycle | ReviewStatus | TargetSection | ReviewId | Reviewer |");
        builder.AppendLine("|---|---|---|---|---|---|");
        foreach (var entry in report.Entries)
        {
            builder.AppendLine($"| {Escape(entry.ItemId)} | {Escape(entry.LifecycleOverride)} | {Escape(entry.ReviewStatusOverride)} | {Escape(entry.TargetSectionOverride)} | {Escape(entry.SourceReviewId)} | {Escape(entry.Reviewer)} |");
        }

        return builder.ToString();
    }

    public static string BuildMarkdownSmoke(VectorLifecycleMetadataReviewSmokeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Lifecycle Metadata Review Smoke Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.CreatedAt:O}");
        builder.AppendLine($"- ApprovedSidecarWritten: `{report.ApprovedSidecarWritten}`");
        builder.AppendLine($"- RejectSkippedSidecar: `{report.RejectSkippedSidecar}`");
        builder.AppendLine($"- NeedsEvidenceSkippedSidecar: `{report.NeedsEvidenceSkippedSidecar}`");
        builder.AppendLine($"- SupersedeSkippedSidecar: `{report.SupersedeSkippedSidecar}`");
        builder.AppendLine($"- SourceItemUnchanged: `{report.SourceItemUnchanged}`");
        builder.AppendLine($"- UnsafeNormalContextApprovalBlocked: `{report.UnsafeNormalContextApprovalBlocked}`");
        builder.AppendLine($"- SidecarEntryCount: `{report.SidecarEntryCount}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        return builder.ToString();
    }

    private async Task<VectorLifecycleMetadataReviewResult> PersistBlockedReviewAsync(
        VectorLifecycleMetadataReviewCandidate candidate,
        VectorLifecycleMetadataReviewRequest request,
        string normalizedDecision,
        string blockedReason,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var reviewId = BuildReviewId(candidate.CandidateId, normalizedDecision, request.Reviewer, request.Reason, EffectiveLifecycle(request, candidate), EffectiveReviewStatus(request, candidate), EffectiveTargetSection(request, candidate));
        var record = BuildRecord(
            candidate,
            request,
            normalizedDecision,
            candidate.Status,
            reviewId,
            now,
            sidecarWritten: false,
            unsafeApprovalBlocked: true,
            blockedReason);
        await _reviewStore.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        return new VectorLifecycleMetadataReviewResult
        {
            OperationId = $"vector-lifecycle-metadata-review-{Guid.NewGuid():N}",
            CreatedAt = now,
            Succeeded = false,
            CandidateId = candidate.CandidateId,
            Decision = normalizedDecision,
            CandidateStatus = candidate.Status,
            SidecarWritten = false,
            SourceItemUnchanged = true,
            UnsafeApprovalBlocked = true,
            BlockedReason = blockedReason,
            Review = record,
            Diagnostics = [blockedReason]
        };
    }

    private static VectorLifecycleMetadataReviewRecord BuildRecord(
        VectorLifecycleMetadataReviewCandidate candidate,
        VectorLifecycleMetadataReviewRequest request,
        string decision,
        string status,
        string reviewId,
        DateTimeOffset now,
        bool sidecarWritten,
        bool unsafeApprovalBlocked,
        string blockedReason)
        => new()
        {
            ReviewId = reviewId,
            CandidateId = candidate.CandidateId,
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            MustHitItemId = candidate.MustHitItemId,
            Decision = decision,
            ResultStatus = status,
            Reviewer = request.Reviewer,
            Reason = request.Reason,
            ProposedLifecycle = EffectiveLifecycle(request, candidate),
            ProposedReviewStatus = EffectiveReviewStatus(request, candidate),
            ProposedTargetSection = EffectiveTargetSection(request, candidate),
            EvidenceRefs = EffectiveEvidenceRefs(request, candidate),
            SourceRefs = EffectiveSourceRefs(request, candidate),
            SidecarWritten = sidecarWritten,
            UnsafeApprovalBlocked = unsafeApprovalBlocked,
            BlockedReason = blockedReason,
            ReviewedAt = now,
            Metadata = BuildReviewMetadata(request)
        };

    private static VectorLifecycleSidecarMetadataEntry BuildSidecarEntry(
        VectorLifecycleMetadataReviewCandidate candidate,
        VectorLifecycleMetadataReviewRequest request,
        string reviewId,
        DateTimeOffset now)
        => new()
        {
            ItemId = candidate.MustHitItemId,
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            LifecycleOverride = EffectiveLifecycle(request, candidate),
            ReviewStatusOverride = EffectiveReviewStatus(request, candidate),
            TargetSectionOverride = EffectiveTargetSection(request, candidate),
            SourceReviewId = reviewId,
            SourceCandidateId = candidate.CandidateId,
            Reviewer = request.Reviewer,
            Reason = request.Reason,
            EvidenceRefs = EffectiveEvidenceRefs(request, candidate),
            SourceRefs = EffectiveSourceRefs(request, candidate),
            CreatedAt = now,
            PolicyVersion = "vector-lifecycle-sidecar/v1",
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["generatedBy"] = GeneratedBy,
                ["sourceCandidateId"] = candidate.CandidateId,
                ["sourceSampleId"] = candidate.SourceSampleId,
                ["sourceEvalSet"] = candidate.SourceEvalSet,
                ["sourceItemUnchanged"] = bool.TrueString,
                ["formalRetrievalAllowed"] = bool.FalseString,
                ["useForRuntime"] = bool.FalseString
            }
        };

    private static string ValidateApproveRequest(
        VectorLifecycleMetadataReviewRequest request,
        VectorLifecycleMetadataReviewCandidate candidate)
    {
        if (!request.Confirmed)
        {
            return "MissingYESConfirmation";
        }

        if (string.IsNullOrWhiteSpace(request.Reviewer))
        {
            return "MissingReviewer";
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return "MissingReason";
        }

        if (string.IsNullOrWhiteSpace(request.ProposedLifecycle)
            || string.IsNullOrWhiteSpace(request.ProposedReviewStatus)
            || string.IsNullOrWhiteSpace(request.ProposedTargetSection))
        {
            return "MissingProposedMetadata";
        }

        if (EffectiveEvidenceRefs(request, candidate).Count == 0 && EffectiveSourceRefs(request, candidate).Count == 0)
        {
            return "MissingEvidenceOrSourceRefs";
        }

        var targetSection = EffectiveTargetSection(request, candidate);
        if (string.Equals(targetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase))
        {
            if (!IsNormalContextLifecycleAllowed(EffectiveLifecycle(request, candidate)))
            {
                return "NormalContextRequiresActiveCurrentOrStable";
            }

            if (IsDeprecatedHistoricalOrSuperseded(candidate.CurrentLifecycle)
                || IsDeprecatedHistoricalOrSuperseded(candidate.CurrentReviewStatus))
            {
                return "DeprecatedHistoricalOrSupersededCannotApproveToNormalContext";
            }
        }

        return string.Empty;
    }

    private static bool IsNormalContextLifecycleAllowed(string lifecycle)
        => lifecycle.Equals("Active", StringComparison.OrdinalIgnoreCase)
            || lifecycle.Equals("Current", StringComparison.OrdinalIgnoreCase)
            || lifecycle.Equals("Stable", StringComparison.OrdinalIgnoreCase);

    private static bool IsDeprecatedHistoricalOrSuperseded(string value)
        => value.Equals("Deprecated", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Historical", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Superseded", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Rejected", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDecision(string decision)
    {
        if (decision.Equals(VectorLifecycleMetadataReviewDecisions.ApproveForSidecar, StringComparison.OrdinalIgnoreCase))
        {
            return VectorLifecycleMetadataReviewDecisions.ApproveForSidecar;
        }

        if (decision.Equals(VectorLifecycleMetadataReviewDecisions.Reject, StringComparison.OrdinalIgnoreCase))
        {
            return VectorLifecycleMetadataReviewDecisions.Reject;
        }

        if (decision.Equals(VectorLifecycleMetadataReviewDecisions.NeedsEvidence, StringComparison.OrdinalIgnoreCase))
        {
            return VectorLifecycleMetadataReviewDecisions.NeedsEvidence;
        }

        if (decision.Equals(VectorLifecycleMetadataReviewDecisions.Supersede, StringComparison.OrdinalIgnoreCase))
        {
            return VectorLifecycleMetadataReviewDecisions.Supersede;
        }

        return string.Empty;
    }

    private static string ToStatus(string decision)
        => decision switch
        {
            VectorLifecycleMetadataReviewDecisions.ApproveForSidecar => VectorLifecycleMetadataReviewCandidateStatuses.ApprovedForSidecar,
            VectorLifecycleMetadataReviewDecisions.Reject => VectorLifecycleMetadataReviewCandidateStatuses.Rejected,
            VectorLifecycleMetadataReviewDecisions.NeedsEvidence => VectorLifecycleMetadataReviewCandidateStatuses.NeedsEvidence,
            VectorLifecycleMetadataReviewDecisions.Supersede => VectorLifecycleMetadataReviewCandidateStatuses.Superseded,
            _ => VectorLifecycleMetadataReviewCandidateStatuses.PendingReview
        };

    private static VectorLifecycleMetadataReviewCandidate CopyCandidateWithStatus(
        VectorLifecycleMetadataReviewCandidate candidate,
        string status)
        => new()
        {
            CandidateId = candidate.CandidateId,
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            SourceSampleId = candidate.SourceSampleId,
            SourceEvalSet = candidate.SourceEvalSet,
            MustHitItemId = candidate.MustHitItemId,
            ItemKind = candidate.ItemKind,
            Layer = candidate.Layer,
            CurrentLifecycle = candidate.CurrentLifecycle,
            CurrentReviewStatus = candidate.CurrentReviewStatus,
            CurrentTargetSection = candidate.CurrentTargetSection,
            ProposedLifecycle = candidate.ProposedLifecycle,
            ProposedReviewStatus = candidate.ProposedReviewStatus,
            ProposedTargetSection = candidate.ProposedTargetSection,
            RepairReason = candidate.RepairReason,
            EvidenceRefs = [.. candidate.EvidenceRefs],
            SourceRefs = [.. candidate.SourceRefs],
            ProvenanceAvailable = candidate.ProvenanceAvailable,
            RelationEvidenceAvailable = candidate.RelationEvidenceAvailable,
            ReviewEvidenceAvailable = candidate.ReviewEvidenceAvailable,
            RiskIfApproved = [.. candidate.RiskIfApproved],
            RiskIfRejected = [.. candidate.RiskIfRejected],
            RequiresHumanReview = candidate.RequiresHumanReview,
            Status = status,
            CreatedAt = candidate.CreatedAt,
            Metadata = new Dictionary<string, string>(candidate.Metadata, StringComparer.OrdinalIgnoreCase)
        };

    private static VectorLifecycleMetadataReviewResult Failed(
        VectorLifecycleMetadataReviewRequest request,
        string reason)
        => new()
        {
            OperationId = $"vector-lifecycle-metadata-review-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            Succeeded = false,
            CandidateId = request.CandidateId,
            Decision = request.Decision,
            CandidateStatus = string.Empty,
            SidecarWritten = false,
            SourceItemUnchanged = true,
            BlockedReason = reason,
            Diagnostics = [reason]
        };

    private static IReadOnlyList<string> EffectiveEvidenceRefs(
        VectorLifecycleMetadataReviewRequest request,
        VectorLifecycleMetadataReviewCandidate candidate)
        => request.EvidenceRefs.Count > 0 ? request.EvidenceRefs.ToArray() : candidate.EvidenceRefs.ToArray();

    private static IReadOnlyList<string> EffectiveSourceRefs(
        VectorLifecycleMetadataReviewRequest request,
        VectorLifecycleMetadataReviewCandidate candidate)
        => request.SourceRefs.Count > 0 ? request.SourceRefs.ToArray() : candidate.SourceRefs.ToArray();

    private static string EffectiveLifecycle(VectorLifecycleMetadataReviewRequest request, VectorLifecycleMetadataReviewCandidate candidate)
        => string.IsNullOrWhiteSpace(request.ProposedLifecycle) ? candidate.ProposedLifecycle : request.ProposedLifecycle;

    private static string EffectiveReviewStatus(VectorLifecycleMetadataReviewRequest request, VectorLifecycleMetadataReviewCandidate candidate)
        => string.IsNullOrWhiteSpace(request.ProposedReviewStatus) ? candidate.ProposedReviewStatus : request.ProposedReviewStatus;

    private static string EffectiveTargetSection(VectorLifecycleMetadataReviewRequest request, VectorLifecycleMetadataReviewCandidate candidate)
        => string.IsNullOrWhiteSpace(request.ProposedTargetSection) ? candidate.ProposedTargetSection : request.ProposedTargetSection;

    private static Dictionary<string, string> BuildReviewMetadata(VectorLifecycleMetadataReviewRequest request)
    {
        var metadata = new Dictionary<string, string>(request.Metadata ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
        {
            ["generatedBy"] = GeneratedBy,
            ["sourceItemUnchanged"] = bool.TrueString,
            ["formalRetrievalAllowed"] = bool.FalseString,
            ["useForRuntime"] = bool.FalseString
        };
        return metadata;
    }

    private static string BuildReviewId(
        string candidateId,
        string decision,
        string reviewer,
        string reason,
        string lifecycle,
        string reviewStatus,
        string targetSection)
    {
        var canonical = string.Join(
            '|',
            candidateId.Trim().ToLowerInvariant(),
            decision.Trim().ToLowerInvariant(),
            reviewer.Trim().ToLowerInvariant(),
            reason.Trim().ToLowerInvariant(),
            lifecycle.Trim().ToLowerInvariant(),
            reviewStatus.Trim().ToLowerInvariant(),
            targetSection.Trim().ToLowerInvariant());
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "vlm-review-record-" + Convert.ToHexString(bytes).ToLowerInvariant()[..32];
    }

    private static int CountStatus(IReadOnlyList<VectorLifecycleMetadataReviewCandidate> candidates, string status)
        => candidates.Count(item => string.Equals(item.Status, status, StringComparison.OrdinalIgnoreCase));

    private static int CountTarget(IReadOnlyList<VectorLifecycleSidecarMetadataEntry> sidecars, string target)
        => sidecars.Count(item => string.Equals(item.TargetSectionOverride, target, StringComparison.OrdinalIgnoreCase));

    private static string Escape(string? value)
        => (value ?? string.Empty)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}
