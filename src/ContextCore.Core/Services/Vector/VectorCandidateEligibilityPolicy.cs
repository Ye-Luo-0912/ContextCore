using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>向量候选资格策略；只依赖运行时元数据和向量诊断。</summary>
public sealed class VectorCandidateEligibilityPolicy
{
    private const string UnsupportedSourceType = nameof(UnsupportedSourceType);
    private readonly VectorSourceLifecycleMetadataResolver _lifecycleResolver;

    public VectorCandidateEligibilityPolicy(VectorSourceLifecycleMetadataResolver? lifecycleResolver = null)
    {
        _lifecycleResolver = lifecycleResolver ?? new VectorSourceLifecycleMetadataResolver();
    }

    public VectorCandidateEligibilityResult Evaluate(
        VectorQueryProfile profile,
        VectorIndexEntry entry,
        double similarity,
        IReadOnlyList<string> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var blockedReasons = new List<string>();
        AddSimilarityReason(profile, similarity, blockedReasons);
        AddProfileFilterReasons(profile, entry, blockedReasons);
        AddDiagnosticReasons(diagnostics, blockedReasons);
        var lifecycle = _lifecycleResolver.Resolve(entry);
        AddLifecycleReasons(profile, lifecycle, blockedReasons);

        var diagnosticsOnlyRisk = IsDiagnosticsOnlyItemKind(profile, entry);
        var lifecycleRisk = IsLifecycleRisk(lifecycle);
        var diagnosticsRisk = diagnostics.Any(IsBlockingDiagnostic);
        var lifecycleMetadataRisk = !lifecycle.IsKnownLifecycle
                                    || !lifecycle.IsLifecycleMetadataComplete
                                    || lifecycle.LegacySourceWithoutLifecycle
                                    || lifecycle.DeprecatedSourceWithoutLifecycle;
        var riskIfNormalSelected = lifecycleRisk || lifecycleMetadataRisk || diagnosticsRisk || diagnosticsOnlyRisk;
        var targetSection = ResolveTargetSection(profile, lifecycle, blockedReasons);
        var riskAfterPolicy = blockedReasons.Count == 0
                              && string.Equals(targetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase)
                              && riskIfNormalSelected;

        return new VectorCandidateEligibilityResult
        {
            CandidateId = entry.ItemId,
            EligibilityStatus = blockedReasons.Count == 0
                ? VectorCandidateEligibilityStatuses.Eligible
                : VectorCandidateEligibilityStatuses.Blocked,
            BlockedReasons = blockedReasons
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            TargetSection = targetSection,
            RiskIfNormalSelected = riskIfNormalSelected,
            RiskAfterPolicy = riskAfterPolicy
        };
    }

    private static void AddSimilarityReason(
        VectorQueryProfile profile,
        double similarity,
        ICollection<string> blockedReasons)
    {
        if (similarity < profile.MinSimilarity)
        {
            blockedReasons.Add(VectorCandidateBlockedReason.SimilarityBelowThreshold);
        }
    }

    private static void AddProfileFilterReasons(
        VectorQueryProfile profile,
        VectorIndexEntry entry,
        ICollection<string> blockedReasons)
    {
        if (!Allows(profile.AllowedLayers, entry.Layer))
        {
            blockedReasons.Add(VectorCandidateBlockedReason.UnsupportedLayer);
        }

        if (!Allows(profile.AllowedItemKinds, entry.ItemKind))
        {
            blockedReasons.Add(VectorCandidateBlockedReason.UnsupportedItemKind);
        }

        if (profile.AllowedSourceTypes.Count > 0
            && !Allows(profile.AllowedSourceTypes, new VectorSourceLifecycleMetadataResolver().Resolve(entry).SourceType))
        {
            blockedReasons.Add(UnsupportedSourceType);
        }

        if (IsDiagnosticsOnlyItemKind(profile, entry))
        {
            blockedReasons.Add(VectorCandidateBlockedReason.DiagnosticsOnlyItemKindBlocked);
        }
    }

    private static void AddDiagnosticReasons(
        IReadOnlyList<string> diagnostics,
        ICollection<string> blockedReasons)
    {
        if (diagnostics.Contains(VectorIndexDiagnosticTypes.DuplicateVectorEntry, StringComparer.OrdinalIgnoreCase))
        {
            blockedReasons.Add(VectorCandidateBlockedReason.DuplicateVectorEntryBlocked);
        }

        if (diagnostics.Contains(VectorIndexDiagnosticTypes.OrphanVectorEntry, StringComparer.OrdinalIgnoreCase))
        {
            blockedReasons.Add(VectorCandidateBlockedReason.OrphanVectorEntryBlocked);
        }

        if (diagnostics.Contains(VectorIndexDiagnosticTypes.DimensionMismatch, StringComparer.OrdinalIgnoreCase))
        {
            blockedReasons.Add(VectorCandidateBlockedReason.DimensionMismatchBlocked);
        }

        if (diagnostics.Contains(VectorIndexDiagnosticTypes.ProviderMismatch, StringComparer.OrdinalIgnoreCase)
            || diagnostics.Contains(VectorIndexDiagnosticTypes.EmbeddingModelMismatch, StringComparer.OrdinalIgnoreCase)
            || diagnostics.Contains(VectorIndexDiagnosticTypes.NormalizationMismatch, StringComparer.OrdinalIgnoreCase))
        {
            blockedReasons.Add(VectorCandidateBlockedReason.StaleEmbeddingBlocked);
        }

        if (diagnostics.Contains(VectorIndexDiagnosticTypes.StaleEmbedding, StringComparer.OrdinalIgnoreCase)
            || diagnostics.Contains(VectorIndexDiagnosticTypes.ContentHashMismatch, StringComparer.OrdinalIgnoreCase))
        {
            blockedReasons.Add(VectorCandidateBlockedReason.StaleEmbeddingBlocked);
        }
    }

    private static void AddLifecycleReasons(
        VectorQueryProfile profile,
        VectorSourceLifecycleMetadata lifecycle,
        ICollection<string> blockedReasons)
    {
        if (profile.RequireKnownLifecycle && !lifecycle.IsKnownLifecycle)
        {
            blockedReasons.Add(VectorCandidateBlockedReason.UnknownLifecycleBlocked);
        }

        if (profile.RequireCompleteLifecycleMetadata && !lifecycle.IsLifecycleMetadataComplete)
        {
            blockedReasons.Add(VectorCandidateBlockedReason.LifecycleMetadataIncompleteBlocked);
        }

        if (profile.RequireKnownLifecycle && lifecycle.LegacySourceWithoutLifecycle)
        {
            blockedReasons.Add(VectorCandidateBlockedReason.LegacySourceRequiresLifecycleMetadata);
        }

        if (profile.RequireKnownLifecycle && lifecycle.DeprecatedSourceWithoutLifecycle)
        {
            blockedReasons.Add(VectorCandidateBlockedReason.LegacySourceRequiresLifecycleMetadata);
        }

        if (profile.RequireCompleteLifecycleMetadata && lifecycle.MissingReplacementInfo)
        {
            blockedReasons.Add(VectorCandidateBlockedReason.ReplacementMetadataMissingBlocked);
        }

        if (lifecycle.RequiresAuditProfile && !profile.AllowHistoricalCandidates && !profile.AllowDeprecatedCandidates)
        {
            blockedReasons.Add(VectorCandidateBlockedReason.HistoricalSourceRequiresAuditProfile);
        }

        if (lifecycle.IsRejected && !profile.AllowRejectedCandidates)
        {
            blockedReasons.Add(VectorCandidateBlockedReason.RejectedCandidateBlocked);
            return;
        }

        if (lifecycle.IsDeprecated && !profile.AllowDeprecatedCandidates)
        {
            blockedReasons.Add(VectorCandidateBlockedReason.DeprecatedCandidateBlocked);
        }

        if ((lifecycle.IsHistorical || lifecycle.IsSuperseded) && !profile.AllowHistoricalCandidates)
        {
            blockedReasons.Add(VectorCandidateBlockedReason.HistoricalCandidateBlocked);
        }

        if (IsCandidateLifecycle(lifecycle.Lifecycle) && !profile.AllowCandidateLifecycle)
        {
            blockedReasons.Add(VectorCandidateBlockedReason.CandidateLifecycleBlocked);
        }
    }

    private static string ResolveTargetSection(
        VectorQueryProfile profile,
        VectorSourceLifecycleMetadata lifecycle,
        IReadOnlyCollection<string> blockedReasons)
    {
        if (blockedReasons.Count > 0)
        {
            return blockedReasons.Contains(VectorCandidateBlockedReason.DiagnosticsOnlyItemKindBlocked, StringComparer.OrdinalIgnoreCase)
                ? profile.DiagnosticsTargetSection
                : VectorQueryTargetSections.Excluded;
        }

        if (!lifecycle.IsKnownLifecycle
            && (string.Equals(profile.ProfileId, VectorQueryProfileIds.AuditV1, StringComparison.OrdinalIgnoreCase)
                || string.Equals(profile.ProfileId, VectorQueryProfileIds.DiagnosticsV1, StringComparison.OrdinalIgnoreCase)))
        {
            return profile.DefaultTargetSection;
        }

        return lifecycle.IsHistorical || lifecycle.IsDeprecated || lifecycle.IsSuperseded
            ? profile.HistoricalTargetSection
            : profile.DefaultTargetSection;
    }

    private static bool Allows(IReadOnlyList<string> allowedValues, string? value)
    {
        return allowedValues.Count == 0
               || allowedValues.Any(allowed => string.Equals(allowed, value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLifecycleRisk(VectorSourceLifecycleMetadata lifecycle)
    {
        return lifecycle.IsDeprecated
               || lifecycle.IsRejected
               || lifecycle.IsHistorical
               || lifecycle.IsSuperseded;
    }

    private static bool IsCandidateLifecycle(string lifecycle)
    {
        return string.Equals(lifecycle, "Candidate", StringComparison.OrdinalIgnoreCase)
               || string.Equals(lifecycle, "Pending", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDiagnosticsOnlyItemKind(VectorQueryProfile profile, VectorIndexEntry entry)
    {
        return profile.DiagnosticsOnlyItemKinds.Any(kind =>
            string.Equals(kind, entry.ItemKind, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsBlockingDiagnostic(string diagnostic)
    {
        return string.Equals(diagnostic, VectorIndexDiagnosticTypes.DuplicateVectorEntry, StringComparison.OrdinalIgnoreCase)
               || string.Equals(diagnostic, VectorIndexDiagnosticTypes.OrphanVectorEntry, StringComparison.OrdinalIgnoreCase)
               || string.Equals(diagnostic, VectorIndexDiagnosticTypes.DimensionMismatch, StringComparison.OrdinalIgnoreCase)
               || string.Equals(diagnostic, VectorIndexDiagnosticTypes.StaleEmbedding, StringComparison.OrdinalIgnoreCase)
               || string.Equals(diagnostic, VectorIndexDiagnosticTypes.ContentHashMismatch, StringComparison.OrdinalIgnoreCase)
               || string.Equals(diagnostic, VectorIndexDiagnosticTypes.ProviderMismatch, StringComparison.OrdinalIgnoreCase)
               || string.Equals(diagnostic, VectorIndexDiagnosticTypes.EmbeddingModelMismatch, StringComparison.OrdinalIgnoreCase)
               || string.Equals(diagnostic, VectorIndexDiagnosticTypes.NormalizationMismatch, StringComparison.OrdinalIgnoreCase);
    }
}
