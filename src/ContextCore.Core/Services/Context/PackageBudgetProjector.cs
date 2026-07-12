using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 构建上下文包的预算报告和标准输出投影（budget report + standard output）。
/// 所有方法均为纯函数，不持有状态。
/// </summary>
internal static class PackageBudgetProjector
{
    internal static ContextPackageBudgetReport BuildBudgetReport(
        ContextPackage package,
        int tokenBudget,
        ContextPackageRequest request)
    {
        var normalizedBudget = NormalizeTokenBudget(tokenBudget);
        var remainingTokens = normalizedBudget > 0
            ? Math.Max(0, normalizedBudget - package.EstimatedTokens)
            : 0;
        var policy = request.Policy;
        var modeBudgetProfile = policy is null
            ? null
            : PackagePolicyResolver.ResolveModeBudgetProfile(request, policy);

        return new ContextPackageBudgetReport
        {
            TokenBudget = normalizedBudget,
            UsedTokens = package.EstimatedTokens,
            RemainingTokens = remainingTokens,
            UsageRatio = normalizedBudget > 0
                ? Math.Clamp((double)package.EstimatedTokens / normalizedBudget, 0, 1)
                : 0,
            WasteRatio = normalizedBudget > 0
                ? Math.Clamp((double)remainingTokens / normalizedBudget, 0, 1)
                : 0,
            Sections = package.Sections
                .Select(section =>
                {
                    var allocatedTokens = policy is null
                        ? 0
                        : PackageSectionBudgetResolver.ResolveReportedSectionTokenBudget(policy, modeBudgetProfile, section.Name, tokenBudget);
                    if (allocatedTokens <= 0 && normalizedBudget > 0)
                    {
                        allocatedTokens = normalizedBudget;
                    }

                    return new ContextPackageSectionBudget
                    {
                        SectionName = section.Name,
                        AllocatedTokens = allocatedTokens,
                        UsedTokens = section.EstimatedTokens,
                        UsageRatio = allocatedTokens > 0
                            ? Math.Clamp((double)section.EstimatedTokens / allocatedTokens, 0, 1)
                            : 0
                    };
                })
                .ToArray()
        };
    }

    internal static ContextPackageStandardOutput BuildStandardOutput(
        ContextPackage package,
        IReadOnlyList<DroppedContextItem> droppedItems,
        IReadOnlyList<ContextPackageUncertainty> uncertainties,
        ContextPackageBudgetReport budget)
    {
        var sections = package.Sections
            .Select(CreateOutputItem)
            .ToArray();

        return new ContextPackageStandardOutput
        {
            CurrentTask = sections.FirstOrDefault(section => IsSection(section, "current_task")),
            RecentContext = FilterSections(sections, "recent_context"),
            WorkingState = FilterSections(sections, "working_memory"),
            StableBackground = FilterSections(sections, "stable_memory", "global_context"),
            Constraints = FilterSections(sections, "constraints", "hard_constraints", "soft_constraints"),
            Entities = FilterSections(sections, "entities", "entity_context"),
            Relations = FilterSections(sections, "relations", "related_context"),
            Evidence = FilterSections(sections, "evidence"),
            Excluded = droppedItems,
            Uncertainties = uncertainties,
            Budget = budget
        };
    }

    private static ContextPackageOutputItem CreateOutputItem(ContextPackageSection section)
    {
        return new ContextPackageOutputItem
        {
            SectionName = section.Name,
            Content = section.Content,
            ContentFormat = section.ContentFormat,
            SourceRefs = section.SourceRefs,
            ItemRefs = section.ItemRefs,
            EstimatedTokens = section.EstimatedTokens
        };
    }

    private static IReadOnlyList<ContextPackageOutputItem> FilterSections(
        IReadOnlyList<ContextPackageOutputItem> sections,
        params string[] names)
    {
        var normalizedNames = names
            .Select(PackageSectionBudgetResolver.NormalizeSectionKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return sections
            .Where(section => normalizedNames.Contains(PackageSectionBudgetResolver.NormalizeSectionKey(section.SectionName)))
            .ToArray();
    }

    private static bool IsSection(ContextPackageOutputItem section, string name)
    {
        return string.Equals(
            PackageSectionBudgetResolver.NormalizeSectionKey(section.SectionName),
            PackageSectionBudgetResolver.NormalizeSectionKey(name),
            StringComparison.OrdinalIgnoreCase);
    }

    private static int NormalizeTokenBudget(int tokenBudget)
    {
        return tokenBudget == int.MaxValue || tokenBudget <= 0 ? 0 : tokenBudget;
    }
}
