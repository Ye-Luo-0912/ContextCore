using System.Globalization;
using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>构建 sidecar-aware eligibility preview/recheck/quality 报告；不接正式 retrieval。</summary>
public sealed class VectorSidecarEligibilityPreviewRunner
{
    private readonly VectorLifecycleSidecarResolver _resolver;

    public VectorSidecarEligibilityPreviewRunner(VectorLifecycleSidecarResolver? resolver = null)
    {
        _resolver = resolver ?? new VectorLifecycleSidecarResolver();
    }

    public VectorSidecarEligibilityPreviewReport BuildReport(
        IReadOnlyList<VectorLifecycleMetadataReviewCandidate> candidates,
        IReadOnlyList<VectorLifecycleSidecarMetadataEntry> sidecars,
        string mode)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(sidecars);

        var approvedCandidateIds = candidates
            .Where(static item => string.Equals(item.Status, VectorLifecycleMetadataReviewCandidateStatuses.ApprovedForSidecar, StringComparison.OrdinalIgnoreCase))
            .Select(static item => item.CandidateId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var activeSidecars = sidecars
            .Where(item => string.IsNullOrWhiteSpace(item.SourceCandidateId) || approvedCandidateIds.Contains(item.SourceCandidateId))
            .ToArray();
        var resolutions = new List<VectorLifecycleSidecarResolution>(Math.Max(candidates.Count, activeSidecars.Length));

        foreach (var candidate in candidates)
        {
            var metadata = BuildBaseMetadata(candidate);
            resolutions.Add(_resolver.Resolve(
                candidate.WorkspaceId,
                candidate.CollectionId,
                candidate.MustHitItemId,
                candidate.CurrentTargetSection,
                metadata,
                activeSidecars));
        }

        foreach (var sidecar in activeSidecars)
        {
            if (candidates.Any(candidate => Matches(candidate, sidecar)))
            {
                continue;
            }

            resolutions.Add(new VectorLifecycleSidecarResolution
            {
                ItemId = sidecar.ItemId,
                WorkspaceId = sidecar.WorkspaceId,
                CollectionId = sidecar.CollectionId,
                Source = VectorLifecycleSidecarResolutionSources.NotFound,
                Valid = false,
                Blocked = true,
                BlockedReason = "SidecarWithoutReviewCandidate",
                SourceItemUnchanged = true,
                Explanation = "Sidecar entry was not matched to a review candidate in this preview scope."
            });
        }

        var unsafeBlocked = resolutions.Count(static item =>
            string.Equals(item.BlockedReason, "UnsafeNormalContextSidecarBlocked", StringComparison.OrdinalIgnoreCase));
        var conflictBlocked = resolutions.Count(static item =>
            string.Equals(item.Source, VectorLifecycleSidecarResolutionSources.Conflict, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Source, VectorLifecycleSidecarResolutionSources.NotFound, StringComparison.OrdinalIgnoreCase));
        var changed = resolutions.Count(static item =>
            string.Equals(item.Source, VectorLifecycleSidecarResolutionSources.Sidecar, StringComparison.OrdinalIgnoreCase)
            && item.Valid
            && (!string.Equals(item.BaseLifecycle, item.EffectiveLifecycle, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(item.BaseReviewStatus, item.EffectiveReviewStatus, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(item.BaseTargetSection, item.EffectiveTargetSection, StringComparison.OrdinalIgnoreCase)));
        var recommendation = ResolveRecommendation(activeSidecars.Length, unsafeBlocked, conflictBlocked);

        return new VectorSidecarEligibilityPreviewReport
        {
            OperationId = $"vector-sidecar-eligibility-{mode.ToLowerInvariant()}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            Mode = VectorSidecarEligibilityModes.SidecarAwareEligibility,
            CandidateCount = candidates.Count,
            SidecarEntryCount = sidecars.Count,
            ApprovedSidecarCount = activeSidecars.Length,
            PendingReviewCount = candidates.Count(static item => string.Equals(item.Status, VectorLifecycleMetadataReviewCandidateStatuses.PendingReview, StringComparison.OrdinalIgnoreCase)),
            EffectiveMetadataChangedCount = changed,
            UnsafeSidecarBlockedCount = unsafeBlocked,
            ConflictSidecarBlockedCount = conflictBlocked,
            SourceItemUnchanged = resolutions.All(static item => item.SourceItemUnchanged),
            RecallBeforeSidecar = 0,
            RecallAfterSidecar = 0,
            RiskBeforeSidecar = 0,
            RiskAfterSidecar = unsafeBlocked > 0 ? unsafeBlocked : 0,
            FormalOutputChanged = 0,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            Recommendation = recommendation,
            Resolutions = resolutions,
            Diagnostics = BuildDiagnostics(activeSidecars.Length, unsafeBlocked, conflictBlocked)
        };
    }

    public static string BuildMarkdown(VectorSidecarEligibilityPreviewReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Sidecar Eligibility Preview");
        builder.AppendLine();
        builder.AppendLine($"- OperationId: {report.OperationId}");
        builder.AppendLine($"- Mode: {report.Mode}");
        builder.AppendLine($"- CandidateCount: {report.CandidateCount}");
        builder.AppendLine($"- SidecarEntryCount: {report.SidecarEntryCount}");
        builder.AppendLine($"- ApprovedSidecarCount: {report.ApprovedSidecarCount}");
        builder.AppendLine($"- PendingReviewCount: {report.PendingReviewCount}");
        builder.AppendLine($"- EffectiveMetadataChangedCount: {report.EffectiveMetadataChangedCount}");
        builder.AppendLine($"- UnsafeSidecarBlockedCount: {report.UnsafeSidecarBlockedCount}");
        builder.AppendLine($"- ConflictSidecarBlockedCount: {report.ConflictSidecarBlockedCount}");
        builder.AppendLine($"- SourceItemUnchanged: {report.SourceItemUnchanged}");
        builder.AppendLine($"- RecallBeforeSidecar: {report.RecallBeforeSidecar.ToString("P2", CultureInfo.InvariantCulture)}");
        builder.AppendLine($"- RecallAfterSidecar: {report.RecallAfterSidecar.ToString("P2", CultureInfo.InvariantCulture)}");
        builder.AppendLine($"- RiskBeforeSidecar: {report.RiskBeforeSidecar}");
        builder.AppendLine($"- RiskAfterSidecar: {report.RiskAfterSidecar}");
        builder.AppendLine($"- FormalOutputChanged: {report.FormalOutputChanged}");
        builder.AppendLine($"- UseForRuntime: {report.UseForRuntime}");
        builder.AppendLine($"- FormalRetrievalAllowed: {report.FormalRetrievalAllowed}");
        builder.AppendLine($"- Recommendation: {report.Recommendation}");
        if (report.Diagnostics.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Diagnostics");
            foreach (var diagnostic in report.Diagnostics)
            {
                builder.AppendLine($"- {diagnostic}");
            }
        }

        if (report.Resolutions.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Recent Resolutions");
            foreach (var item in report.Resolutions.Take(10))
            {
                builder.AppendLine($"- {item.ItemId}: source={item.Source}; target={item.EffectiveTargetSection}; blocked={item.Blocked}; reason={item.BlockedReason}");
            }
        }

        return builder.ToString();
    }

    private static VectorSourceLifecycleMetadata BuildBaseMetadata(VectorLifecycleMetadataReviewCandidate candidate)
    {
        var lifecycle = candidate.CurrentLifecycle ?? string.Empty;
        return new VectorSourceLifecycleMetadata
        {
            Lifecycle = lifecycle,
            ReviewStatus = candidate.CurrentReviewStatus ?? string.Empty,
            Layer = candidate.Layer ?? string.Empty,
            ItemKind = candidate.ItemKind ?? string.Empty,
            IsKnownLifecycle = !string.IsNullOrWhiteSpace(lifecycle),
            HasReviewStatus = !string.IsNullOrWhiteSpace(candidate.CurrentReviewStatus),
            IsDeprecated = IsLifecycle(lifecycle, "Deprecated"),
            IsHistorical = IsLifecycle(lifecycle, "Historical"),
            IsRejected = IsLifecycle(lifecycle, "Rejected"),
            IsSuperseded = IsLifecycle(lifecycle, "Superseded"),
            IsLifecycleMetadataComplete = !string.IsNullOrWhiteSpace(lifecycle)
        };
    }

    private static bool Matches(
        VectorLifecycleMetadataReviewCandidate candidate,
        VectorLifecycleSidecarMetadataEntry sidecar)
    {
        return string.Equals(candidate.WorkspaceId, sidecar.WorkspaceId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(candidate.CollectionId, sidecar.CollectionId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(candidate.MustHitItemId, sidecar.ItemId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLifecycle(string lifecycle, string expected)
    {
        return string.Equals(lifecycle, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveRecommendation(
        int approvedSidecarCount,
        int unsafeBlocked,
        int conflictBlocked)
    {
        if (unsafeBlocked > 0)
        {
            return "BlockedByUnsafeSidecar";
        }

        if (conflictBlocked > 0)
        {
            return "BlockedByConflictSidecar";
        }

        if (approvedSidecarCount == 0)
        {
            return "NoApprovedSidecarEntries";
        }

        return "ReadyForHumanReviewBatch";
    }

    private static IReadOnlyList<string> BuildDiagnostics(
        int approvedSidecarCount,
        int unsafeBlocked,
        int conflictBlocked)
    {
        var diagnostics = new List<string>(3);
        if (approvedSidecarCount == 0)
        {
            diagnostics.Add("NoApprovedSidecarEntries");
        }

        if (unsafeBlocked > 0)
        {
            diagnostics.Add("Unsafe sidecar entries were blocked fail-closed.");
        }

        if (conflictBlocked > 0)
        {
            diagnostics.Add("Conflicting or unmatched sidecar entries were blocked fail-closed.");
        }

        return diagnostics;
    }
}
