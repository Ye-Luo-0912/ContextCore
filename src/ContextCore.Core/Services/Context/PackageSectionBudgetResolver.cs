using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 负责上下文包 section 的排序、优先级解析和 token 预算分配。
/// 所有方法均为纯函数，不持有状态。
/// </summary>
internal static class PackageSectionBudgetResolver
{
    internal static IReadOnlyList<ContextPackageSection> OrderSections(
        IReadOnlyList<ContextPackageSection> sections,
        ContextPackagePolicy policy)
    {
        var sectionOrder = policy.SectionOrder
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeSectionKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select((name, index) => new { Name = name, Index = index })
            .ToDictionary(item => item.Name, item => item.Index, StringComparer.OrdinalIgnoreCase);

        var indexedSections = sections
            .Select((section, index) =>
            {
                var rank = sectionOrder.TryGetValue(NormalizeSectionKey(section.Name), out var explicitRank)
                    ? explicitRank
                    : int.MaxValue;

                return new
                {
                    Section = section,
                    Index = index,
                    Rank = rank
                };
            })
            .ToArray();

        return [.. indexedSections
            .OrderBy(item => item.Rank)
            .ThenByDescending(item => item.Rank == int.MaxValue ? item.Section.Priority : 0)
            .ThenBy(item => item.Index)
            .Select(item => item.Section)];
    }

    internal static int GetPriority(ContextPackagePolicy policy, string sectionName, int defaultPriority)
    {
        return TryGetSectionSetting(policy.SectionPriorities, sectionName, out var priority)
            ? priority
            : defaultPriority;
    }

    internal static int GetSectionTokenBudget(ContextPackagePolicy policy, string sectionName)
    {
        return TryGetSectionSetting(policy.SectionTokenBudgets, sectionName, out var budget) && budget > 0
            ? budget
            : 0;
    }

    internal static int ResolveSectionTokenBudget(
        ContextPackagePolicy policy,
        ModeBudgetProfile? modeBudgetProfile,
        string sectionName,
        int tokenBudget)
    {
        var explicitBudget = GetSectionTokenBudget(policy, sectionName);
        if (explicitBudget > 0)
        {
            return explicitBudget;
        }

        if (modeBudgetProfile is null || tokenBudget <= 0 || tokenBudget == int.MaxValue)
        {
            return 0;
        }

        var normalizedSectionName = NormalizeSectionKey(sectionName);
        return modeBudgetProfile.SectionRatios.TryGetValue(normalizedSectionName, out var ratio) && ratio > 0
            ? Math.Max(1, (int)Math.Round(tokenBudget * ratio, MidpointRounding.AwayFromZero))
            : 0;
    }

    internal static int ResolveDiagnosticsSectionTokenBudget(
        ContextPackagePolicy policy,
        ModeBudgetProfile? modeBudgetProfile,
        string sectionName,
        int tokenBudget)
    {
        var explicitBudget = GetSectionTokenBudget(policy, sectionName);
        if (explicitBudget > 0)
        {
            return explicitBudget;
        }

        if (tokenBudget <= 0 || tokenBudget == int.MaxValue)
        {
            return 0;
        }

        var baseBudget = ResolveSectionTokenBudget(policy, modeBudgetProfile, sectionName, tokenBudget);
        var normalized = NormalizeSectionKey(sectionName);
        var ratio = normalized switch
        {
            "evidence" => 0.04,
            "excluded" => 0.03,
            "uncertainties" => 0.03,
            _ => 0.03
        };
        var cap = Math.Max(tokenBudget <= 200 ? 8 : 32, (int)Math.Round(tokenBudget * ratio, MidpointRounding.AwayFromZero));
        cap = Math.Min(cap, tokenBudget <= 200 ? 16 : 160);
        return baseBudget > 0 ? Math.Min(baseBudget, cap) : cap;
    }

    internal static int ResolveHistoricalSectionTokenBudget(
        ContextPackagePolicy policy,
        ModeBudgetProfile? modeBudgetProfile,
        string sectionName,
        int tokenBudget)
    {
        var explicitBudget = GetSectionTokenBudget(policy, sectionName);
        if (explicitBudget > 0)
        {
            return explicitBudget;
        }

        if (tokenBudget <= 0 || tokenBudget == int.MaxValue)
        {
            return 0;
        }

        var baseBudget = ResolveSectionTokenBudget(policy, modeBudgetProfile, sectionName, tokenBudget);
        var cap = Math.Max(48, (int)Math.Round(tokenBudget * 0.10, MidpointRounding.AwayFromZero));
        cap = Math.Min(cap, 600);
        return baseBudget > 0 ? Math.Min(baseBudget, cap) : cap;
    }

    internal static int ResolveReportedSectionTokenBudget(
        ContextPackagePolicy policy,
        ModeBudgetProfile? modeBudgetProfile,
        string sectionName,
        int tokenBudget)
    {
        var normalized = NormalizeSectionKey(sectionName);
        return normalized switch
        {
            "excluded" or "uncertainties" =>
                ResolveDiagnosticsSectionTokenBudget(policy, modeBudgetProfile, sectionName, tokenBudget),
            "historical_context" or "deprecated_evidence" or "conflict_evidence" =>
                ResolveHistoricalSectionTokenBudget(policy, modeBudgetProfile, sectionName, tokenBudget),
            _ => ResolveSectionTokenBudget(policy, modeBudgetProfile, sectionName, tokenBudget)
        };
    }

    internal static bool TryGetSectionSetting(
        IReadOnlyDictionary<string, int> settings,
        string sectionName,
        out int value)
    {
        if (settings.TryGetValue(sectionName, out value))
        {
            return true;
        }

        var normalizedSectionName = NormalizeSectionKey(sectionName);
        foreach (var (key, configuredValue) in settings)
        {
            if (string.Equals(NormalizeSectionKey(key), normalizedSectionName, StringComparison.OrdinalIgnoreCase))
            {
                value = configuredValue;
                return true;
            }
        }

        foreach (var fallbackKey in new[] { "default", "*" })
        {
            foreach (var (key, configuredValue) in settings)
            {
                if (string.Equals(NormalizeSectionKey(key), fallbackKey, StringComparison.OrdinalIgnoreCase))
                {
                    value = configuredValue;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    internal static string NormalizeSectionKey(string? sectionName)
    {
        if (string.IsNullOrWhiteSpace(sectionName))
        {
            return string.Empty;
        }

        var normalized = sectionName.Trim().ToLowerInvariant()
            .Replace('-', '_')
            .Replace(' ', '_')
            .Replace('.', '_');

        while (normalized.Contains("__", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("__", "_");
        }

        var compact = normalized.Replace("_", string.Empty);
        return compact switch
        {
            "hardconstraint" or "hardconstraints" => "hard_constraints",
            "softconstraint" or "softconstraints" => "soft_constraints",
            "currenttask" => "current_task",
            "workingmemory" => "working_memory",
            "stablememory" => "stable_memory",
            "globalcontext" => "global_context",
            "recentcontext" or "recentrawcontext" or "rawcontext" => "recent_context",
            "relatedcontext" => "related_context",
            "excluded" or "excludeditems" => "excluded",
            "uncertainty" or "uncertainties" => "uncertainties",
            _ => normalized
        };
    }
}
