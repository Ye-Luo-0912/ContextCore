using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>解析 lifecycle sidecar 的 preview/eval effective metadata；不修改 source item，也不接正式检索。</summary>
public sealed class VectorLifecycleSidecarResolver
{
    public VectorLifecycleSidecarResolution Resolve(
        string workspaceId,
        string collectionId,
        string itemId,
        string baseTargetSection,
        VectorSourceLifecycleMetadata baseMetadata,
        IReadOnlyList<VectorLifecycleSidecarMetadataEntry> sidecars)
    {
        ArgumentNullException.ThrowIfNull(baseMetadata);
        ArgumentNullException.ThrowIfNull(sidecars);

        var baseResolution = BuildBaseResolution(workspaceId, collectionId, itemId, baseTargetSection, baseMetadata);
        var matching = sidecars
            .Where(item => Matches(item, workspaceId, collectionId, itemId))
            .ToArray();
        if (matching.Length == 0)
        {
            return baseResolution;
        }

        if (HasConflictingSidecars(matching))
        {
            return Blocked(baseResolution, VectorLifecycleSidecarResolutionSources.Conflict, "SidecarConflictFailClosed");
        }

        var sidecar = matching[0];
        if (sidecar.EvidenceRefs.Count == 0 && sidecar.SourceRefs.Count == 0)
        {
            return Blocked(baseResolution, VectorLifecycleSidecarResolutionSources.Conflict, "MissingEvidenceOrSourceRefs");
        }

        if (IsUnsafeNormalContextOverride(baseMetadata, sidecar))
        {
            return Blocked(baseResolution, VectorLifecycleSidecarResolutionSources.Sidecar, "UnsafeNormalContextSidecarBlocked");
        }

        return new VectorLifecycleSidecarResolution
        {
            ItemId = itemId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            BaseLifecycle = baseResolution.BaseLifecycle,
            BaseReviewStatus = baseResolution.BaseReviewStatus,
            BaseTargetSection = baseResolution.BaseTargetSection,
            EffectiveLifecycle = sidecar.LifecycleOverride,
            EffectiveReviewStatus = sidecar.ReviewStatusOverride,
            EffectiveTargetSection = string.IsNullOrWhiteSpace(sidecar.TargetSectionOverride)
                ? baseTargetSection
                : sidecar.TargetSectionOverride,
            Source = VectorLifecycleSidecarResolutionSources.Sidecar,
            SidecarReviewId = sidecar.SourceReviewId,
            Valid = true,
            Blocked = false,
            SourceItemUnchanged = true,
            Explanation = "Approved sidecar supplied effective metadata for preview/eval only."
        };
    }

    private static VectorLifecycleSidecarResolution BuildBaseResolution(
        string workspaceId,
        string collectionId,
        string itemId,
        string baseTargetSection,
        VectorSourceLifecycleMetadata metadata)
    {
        return new VectorLifecycleSidecarResolution
        {
            ItemId = itemId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            BaseLifecycle = metadata.Lifecycle,
            BaseReviewStatus = metadata.ReviewStatus,
            BaseTargetSection = string.IsNullOrWhiteSpace(baseTargetSection)
                ? VectorQueryTargetSections.Excluded
                : baseTargetSection,
            EffectiveLifecycle = metadata.Lifecycle,
            EffectiveReviewStatus = metadata.ReviewStatus,
            EffectiveTargetSection = string.IsNullOrWhiteSpace(baseTargetSection)
                ? VectorQueryTargetSections.Excluded
                : baseTargetSection,
            Source = VectorLifecycleSidecarResolutionSources.Base,
            Valid = true,
            Blocked = false,
            SourceItemUnchanged = true,
            Explanation = "No approved sidecar override was applied."
        };
    }

    private static VectorLifecycleSidecarResolution Blocked(
        VectorLifecycleSidecarResolution baseResolution,
        string source,
        string blockedReason)
    {
        return new VectorLifecycleSidecarResolution
        {
            ItemId = baseResolution.ItemId,
            WorkspaceId = baseResolution.WorkspaceId,
            CollectionId = baseResolution.CollectionId,
            BaseLifecycle = baseResolution.BaseLifecycle,
            BaseReviewStatus = baseResolution.BaseReviewStatus,
            BaseTargetSection = baseResolution.BaseTargetSection,
            EffectiveLifecycle = baseResolution.BaseLifecycle,
            EffectiveReviewStatus = baseResolution.BaseReviewStatus,
            EffectiveTargetSection = baseResolution.BaseTargetSection,
            Source = source,
            Valid = false,
            Blocked = true,
            BlockedReason = blockedReason,
            SourceItemUnchanged = true,
            Explanation = "Sidecar metadata was rejected for preview/eval and base metadata remained effective."
        };
    }

    private static bool Matches(
        VectorLifecycleSidecarMetadataEntry sidecar,
        string workspaceId,
        string collectionId,
        string itemId)
    {
        return string.Equals(sidecar.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(sidecar.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(sidecar.ItemId, itemId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasConflictingSidecars(IReadOnlyList<VectorLifecycleSidecarMetadataEntry> sidecars)
    {
        if (sidecars.Count <= 1)
        {
            return false;
        }

        var first = sidecars[0];
        return sidecars.Skip(1).Any(item =>
            !string.Equals(item.LifecycleOverride, first.LifecycleOverride, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(item.ReviewStatusOverride, first.ReviewStatusOverride, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(item.TargetSectionOverride, first.TargetSectionOverride, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUnsafeNormalContextOverride(
        VectorSourceLifecycleMetadata baseMetadata,
        VectorLifecycleSidecarMetadataEntry sidecar)
    {
        if (!string.Equals(sidecar.TargetSectionOverride, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (baseMetadata.IsDeprecated || baseMetadata.IsHistorical || baseMetadata.IsRejected || baseMetadata.IsSuperseded)
        {
            return true;
        }

        return !IsNormalContextLifecycle(sidecar.LifecycleOverride);
    }

    private static bool IsNormalContextLifecycle(string lifecycle)
    {
        return string.Equals(lifecycle, "Active", StringComparison.OrdinalIgnoreCase)
               || string.Equals(lifecycle, "Current", StringComparison.OrdinalIgnoreCase)
               || string.Equals(lifecycle, "Stable", StringComparison.OrdinalIgnoreCase);
    }
}
