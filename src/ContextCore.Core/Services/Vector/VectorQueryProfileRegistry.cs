using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>提供向量查询预览的默认安全配置；只服务预览和影子评估，不接正式检索。</summary>
public sealed class VectorQueryProfileRegistry
{
    private readonly IReadOnlyDictionary<string, VectorQueryProfile> _profiles;

    public VectorQueryProfileRegistry()
        : this(CreateDefaultProfiles())
    {
    }

    public VectorQueryProfileRegistry(IReadOnlyList<VectorQueryProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        _profiles = profiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile.ProfileId))
            .GroupBy(profile => profile.ProfileId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToDictionary(profile => profile.ProfileId, profile => profile, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<VectorQueryProfile> GetProfiles()
    {
        return _profiles.Values
            .OrderBy(profile => profile.ProfileId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public VectorQueryProfile Resolve(string? profileId)
    {
        if (!string.IsNullOrWhiteSpace(profileId)
            && _profiles.TryGetValue(profileId, out var profile))
        {
            return profile;
        }

        return _profiles[VectorQueryProfileIds.NormalV1];
    }

    private static IReadOnlyList<VectorQueryProfile> CreateDefaultProfiles()
    {
        return
        [
            new VectorQueryProfile
            {
                ProfileId = VectorQueryProfileIds.NormalV1,
                MinSimilarity = 0.25,
                DiagnosticsOnlyItemKinds = ["stress-test"],
                RequireKnownLifecycle = true,
                RequireCompleteLifecycleMetadata = true,
                AllowCandidateLifecycle = false,
                DefaultTargetSection = VectorQueryTargetSections.NormalContext,
                HistoricalTargetSection = VectorQueryTargetSections.Excluded
            },
            new VectorQueryProfile
            {
                ProfileId = VectorQueryProfileIds.CurrentTaskV1,
                MinSimilarity = 0.20,
                DiagnosticsOnlyItemKinds = ["stress-test"],
                RequireKnownLifecycle = true,
                RequireCompleteLifecycleMetadata = true,
                AllowCandidateLifecycle = true,
                DefaultTargetSection = VectorQueryTargetSections.WorkingContext,
                HistoricalTargetSection = VectorQueryTargetSections.Excluded
            },
            new VectorQueryProfile
            {
                ProfileId = VectorQueryProfileIds.AuditV1,
                MinSimilarity = 0.20,
                AllowDeprecatedCandidates = true,
                AllowHistoricalCandidates = true,
                AllowCandidateLifecycle = true,
                DiagnosticsOnlyItemKinds = ["stress-test"],
                RequireKnownLifecycle = false,
                RequireCompleteLifecycleMetadata = false,
                DefaultTargetSection = VectorQueryTargetSections.AuditContext,
                HistoricalTargetSection = VectorQueryTargetSections.AuditContext
            },
            new VectorQueryProfile
            {
                ProfileId = VectorQueryProfileIds.DiagnosticsV1,
                MinSimilarity = 0.10,
                AllowDeprecatedCandidates = true,
                AllowHistoricalCandidates = true,
                AllowRejectedCandidates = true,
                AllowCandidateLifecycle = true,
                RequireKnownLifecycle = false,
                RequireCompleteLifecycleMetadata = false,
                DefaultTargetSection = VectorQueryTargetSections.DiagnosticsOnly,
                HistoricalTargetSection = VectorQueryTargetSections.DiagnosticsOnly,
                DiagnosticsTargetSection = VectorQueryTargetSections.DiagnosticsOnly
            }
        ];
    }
}
