using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 入口一次解析的包构建选项：将 request + policy 中散落在 Loader、Selector、Projector
/// 的二十多次 <see cref="PackagePolicyResolver"/> 调用集中到此处。
/// 区分影响选择的 metadata（进入缓存 key）与仅用于响应/追踪的 metadata（不进入 key）。
/// </summary>
internal sealed record ResolvedPackageOptions(
    string WorkspaceId,                       // normalized
    string? CollectionId,                     // normalized
    int TokenBudget,                          // resolved
    ModeBudgetProfile? ModeBudgetProfile,     // resolved
    string PackageModeName,                   // resolved
    IReadOnlySet<string> PackageMustHitIds,   // resolved
    bool IsAuditMode,                         // resolved
    int MaxRecentItems,                       // resolved
    TokenEstimationContext TokenContext,      // resolved
    ContextPackagePolicy Policy,              // original policy (for SectionOrder/Priorities/TokenBudgets)
    ContextPackageRequest Request,            // original request (for QueryText/RequiredTags/RequiredTypes/response metadata)
    // Include flags from policy
    bool IncludeRecentRawContext,
    bool IncludeHardConstraints,
    bool IncludeSoftConstraints,
    bool IncludeWorkingMemory,
    bool IncludeStableMemory,
    bool IncludeGlobalContext,
    bool EnableStrictRelevanceFilter,
    // Pre-resolved section inclusion flags (pure bool settings, no hasContent dependency)
    bool IncludeCurrentTaskSection,
    bool IncludeMergedConstraintsSection,
    int ConstraintMergeMaxItems,
    HashSet<string> RelationTypeWhitelist)
{
    /// <summary>
    /// 从 request + policy 一次解析所有构建选项。
    /// </summary>
    internal static ResolvedPackageOptions Resolve(
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        TokenEstimationContext tokenContext)
    {
        var workspaceId = PackagePolicyResolver.NormalizeRequiredValue(request.WorkspaceId);
        var collectionId = PackagePolicyResolver.NormalizeRequiredValue(policy.CollectionId, request.CollectionId);
        var modeBudgetProfile = PackagePolicyResolver.ResolveModeBudgetProfile(request, policy);
        var tokenBudget = PackagePolicyResolver.ResolveTokenBudget(request, policy, modeBudgetProfile);
        var packageModeName = PackagePolicyResolver.ResolvePackageModeName(request, policy, modeBudgetProfile);
        var packageMustHitIds = PackagePolicyResolver.ResolvePackageMustHitIds(request);
        var isAuditMode = PackagePolicyResolver.ResolveIsAuditMode(request, policy);
        var maxRecentItems = policy.MaxRecentItems > 0 ? policy.MaxRecentItems : 20;
        var includeCurrentTask = PackagePolicyResolver.ShouldIncludeCurrentTaskSection(request, policy);
        var includeMergedConstraints = PackagePolicyResolver.ShouldIncludeMergedConstraintsSection(request, policy);
        var constraintMergeMaxItems = PackagePolicyResolver.ResolveIntSetting(request, policy, "constraintMergeMaxItems", 100, 1, 500);
        var relationTypeWhitelist = PackagePolicyResolver.ResolveRelationTypeWhitelist(request, policy);

        return new ResolvedPackageOptions(
            WorkspaceId: workspaceId,
            CollectionId: collectionId,
            TokenBudget: tokenBudget,
            ModeBudgetProfile: modeBudgetProfile,
            PackageModeName: packageModeName,
            PackageMustHitIds: packageMustHitIds,
            IsAuditMode: isAuditMode,
            MaxRecentItems: maxRecentItems,
            TokenContext: tokenContext,
            Policy: policy,
            Request: request,
            IncludeRecentRawContext: policy.IncludeRecentRawContext,
            IncludeHardConstraints: policy.IncludeHardConstraints,
            IncludeSoftConstraints: policy.IncludeSoftConstraints,
            IncludeWorkingMemory: policy.IncludeWorkingMemory,
            IncludeStableMemory: policy.IncludeStableMemory,
            IncludeGlobalContext: policy.IncludeGlobalContext,
            EnableStrictRelevanceFilter: policy.EnableStrictRelevanceFilter,
            IncludeCurrentTaskSection: includeCurrentTask,
            IncludeMergedConstraintsSection: includeMergedConstraints,
            ConstraintMergeMaxItems: constraintMergeMaxItems,
            RelationTypeWhitelist: relationTypeWhitelist);
    }

    /// <summary>
    /// 诊断 section 是否启用（运行时 hasContent 依赖在此处检查）。
    /// </summary>
    internal bool ShouldIncludeDiagnosticsSection(string sectionName, bool hasContent)
    {
        if (!hasContent) return false;
        return PackagePolicyResolver.ResolveBoolSetting(Request, Policy, "includeDiagnosticsSections")
            || PackagePolicyResolver.ResolveBoolSetting(Request, Policy, $"include{PackagePolicyResolver.ToPascalCase(sectionName)}Section")
            || PackagePolicyResolver.ResolveBoolSetting(Request, Policy, $"{sectionName}.enabled");
    }

    /// <summary>
    /// Evidence section 是否启用（运行时 hasContent 依赖在此处检查）。
    /// </summary>
    internal bool ShouldIncludeEvidenceSection(bool hasContent)
    {
        if (!hasContent) return false;
        return PackagePolicyResolver.ResolveBoolSetting(Request, Policy, "includeEvidenceSection")
            || PackagePolicyResolver.ResolveBoolSetting(Request, Policy, "includeEvidence")
            || PackagePolicyResolver.ResolveBoolSetting(Request, Policy, "evidence.enabled")
            || Policy.SectionOrder.Any(section =>
                string.Equals(PackageSectionBudgetResolver.NormalizeSectionKey(section), "evidence", StringComparison.OrdinalIgnoreCase));
    }
}
