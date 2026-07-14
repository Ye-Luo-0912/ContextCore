using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 解析上下文包请求和策略中的配置项：token 预算、mode、mustHit ID、
/// 关系白名单、section 启用开关等。所有方法均为纯函数，不持有状态。
/// </summary>
internal static class PackagePolicyResolver
{
    private static readonly ModeBudgetProfileRegistry ModeBudgetProfiles = ModeBudgetProfileRegistry.CreateDefault();

    internal static string NormalizeRequiredValue(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    internal static HashSet<string> ResolveRelationTypeWhitelist(
        ContextPackageRequest request,
        ContextPackagePolicy policy)
    {
        var configured = ReadSetting(request, policy, "relationTypeWhitelist");
        var values = string.IsNullOrWhiteSpace(configured)
            ? DefaultRelationTypeWhitelist()
            : configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<string> DefaultRelationTypeWhitelist()
    {
        return
        [
            ContextRelationTypes.DependsOn,
            ContextRelationTypes.DerivedFrom,
            ContextRelationTypes.Summarizes,
            ContextRelationTypes.GeneratedBy,
            ContextRelationTypes.IncludedInPackage,
            ContextRelationTypes.RelatedTo,
            ContextRelationTypes.Replaces,
            ContextRelationTypes.Contradicts,
            ContextRelationTypes.Supersedes,
            ContextRelationTypes.ConflictsWith
        ];
    }

    internal static int ResolveIntSetting(
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        string key,
        int defaultValue,
        int min,
        int max)
    {
        return int.TryParse(ReadSetting(request, policy, key), out var value)
            ? Math.Clamp(value, min, max)
            : defaultValue;
    }

    internal static double ResolveDoubleSetting(
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        string key,
        double defaultValue,
        double min,
        double max)
    {
        return double.TryParse(ReadSetting(request, policy, key), out var value)
            ? Math.Clamp(value, min, max)
            : defaultValue;
    }

    internal static int ResolveTokenBudget(
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        ModeBudgetProfile? modeBudgetProfile)
    {
        if (policy.TokenBudget > 0)
        {
            return policy.TokenBudget;
        }

        if (request.TokenBudget > 0)
        {
            return request.TokenBudget;
        }

        return modeBudgetProfile is not null
            ? modeBudgetProfile.DefaultTokenBudget
            : int.MaxValue;
    }

    internal static string ResolvePackageModeName(
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        ModeBudgetProfile? modeBudgetProfile)
    {
        if (!string.IsNullOrWhiteSpace(modeBudgetProfile?.ModeName))
        {
            return modeBudgetProfile.ModeName;
        }

        return ReadFirstSetting(request, policy, "mode", "packageMode", "contextMode", "taskMode") ?? string.Empty;
    }

    internal static IReadOnlySet<string> ResolvePackageMustHitIds(ContextPackageRequest request)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[]
        {
            "eval.mustHit",
            "package.mustHit",
            "mustHit",
            "attention.mustHit"
        })
        {
            if (!request.Metadata.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            foreach (var value in raw.Split([',', ';', '，', '；', '|', '\r', '\n', '\t', ' '],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                values.Add(value);
            }
        }

        return values;
    }

    internal static ModeBudgetProfile? ResolveModeBudgetProfile(
        ContextPackageRequest request,
        ContextPackagePolicy policy)
    {
        // 优先读取强类型枚举（request.Mode > policy.Mode > metadata string）。
        var enumMode = request.Mode != ContextPackageMode.None
            ? request.Mode
            : policy.Mode;

        if (enumMode != ContextPackageMode.None)
        {
            return ModeBudgetProfiles.Resolve(enumMode);
        }

        // 向后兼容：从 metadata 字符串读取。
        var mode = ReadFirstSetting(
            request,
            policy,
            "mode",
            "packageMode",
            "contextMode",
            "taskMode");
        var normalizedMode = WorkingMemoryRecaller.NormalizeModeName(mode);
        return ModeBudgetProfiles.Resolve(normalizedMode);
    }

    internal static string? ReadFirstSetting(
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = ReadSetting(request, policy, key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    internal static string? ReadSetting(
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        string key)
    {
        if (request.Metadata.TryGetValue(key, out var requestValue)
            && !string.IsNullOrWhiteSpace(requestValue))
        {
            return requestValue;
        }

        return policy.Metadata.TryGetValue(key, out var policyValue)
            && !string.IsNullOrWhiteSpace(policyValue)
            ? policyValue
            : null;
    }

    internal static bool ShouldIncludeDiagnosticsSection(
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        string sectionName,
        bool hasContent)
    {
        if (!hasContent)
        {
            return false;
        }

        return ResolveBoolSetting(request, policy, "includeDiagnosticsSections")
            || ResolveBoolSetting(request, policy, $"include{ToPascalCase(sectionName)}Section")
            || ResolveBoolSetting(request, policy, $"{sectionName}.enabled");
    }

    internal static bool ShouldIncludeMergedConstraintsSection(
        ContextPackageRequest request,
        ContextPackagePolicy policy)
    {
        return ResolveBoolSetting(request, policy, "includeMergedConstraintsSection")
            || ResolveBoolSetting(request, policy, "includeConstraintsSection")
            || ResolveBoolSetting(request, policy, "constraints.enabled")
            || ResolveBoolSetting(request, policy, "constraintsSection.enabled");
    }

    internal static bool ShouldIncludeCurrentTaskSection(
        ContextPackageRequest request,
        ContextPackagePolicy policy)
    {
        return ResolveBoolSetting(request, policy, "includeCurrentTaskSection")
            || ResolveBoolSetting(request, policy, "includeCurrentTask")
            || ResolveBoolSetting(request, policy, "currentTask.enabled")
            || ResolveBoolSetting(request, policy, "current_task.enabled")
            || policy.SectionOrder.Any(section =>
                string.Equals(PackageSectionBudgetResolver.NormalizeSectionKey(section), "current_task", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool ShouldIncludeEvidenceSection(
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        bool hasContent)
    {
        if (!hasContent)
        {
            return false;
        }

        return ResolveBoolSetting(request, policy, "includeEvidenceSection")
            || ResolveBoolSetting(request, policy, "includeEvidence")
            || ResolveBoolSetting(request, policy, "evidence.enabled")
            || policy.SectionOrder.Any(section =>
                string.Equals(PackageSectionBudgetResolver.NormalizeSectionKey(section), "evidence", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool ResolveBoolSetting(
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        string key)
    {
        var value = ReadSetting(request, policy, key);
        return value is not null
            && (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase));
    }

    internal static string ToPascalCase(string value)
    {
        var words = value.Split(['_', '-', '.', ' '], StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(words.Select(word =>
            char.ToUpperInvariant(word[0]) + (word.Length == 1 ? string.Empty : word[1..])));
    }

    /// <summary>
    /// 解析审计模式信号：使用 request/policy 上的显式 <see cref="ContextPackageRequest.IsAuditMode"/>
    /// 任一为 true 即启用；任一为 false（且无 true）即关闭；两者均 null 时默认 false（不再读取 QueryText 关键词推断）。
    /// </summary>
    internal static bool ResolveIsAuditMode(
        ContextPackageRequest request,
        ContextPackagePolicy policy)
    {
        if (request.IsAuditMode is true || policy.IsAuditMode is true)
        {
            return true;
        }

        if (request.IsAuditMode is false || policy.IsAuditMode is false)
        {
            return false;
        }

        // 显式信号均缺失（null）时默认关闭，不再回退到 QueryText 关键词推断。
        return false;
    }
}
