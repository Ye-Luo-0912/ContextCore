namespace ContextCore.Abstractions.Models;

/// <summary>
/// 定义哪些上下文 Section 是"普通 Section"（不得包含生命周期废弃项）
/// vs "lifecycle-allowed Section"（允许 deprecated/superseded/replaced 项出现的审计/冲突区域）。
///
/// 普通 Section（normal sections）：
///   currentTask / recentContext / workingState / stableBackground / relations / evidence
///
/// Lifecycle-allowed Section（生命周期专属区）：
///   historical_context / deprecated_evidence / conflict_evidence / uncertainties / diagnostics / excluded
/// </summary>
public static class SectionLifecyclePolicy
{
    /// <summary>
    /// 生命周期专属 Section 的规范化键名集合。
    /// 这些 Section 允许 deprecated、superseded、replaced 的 item 合法出现。
    /// </summary>
    private static readonly HashSet<string> LifecycleAllowedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "historical_context",
        "deprecated_evidence",
        "conflict_evidence",
        "uncertainties",
        "diagnostics",
        "excluded",
    };

    /// <summary>
    /// 返回给定 Section 名称是否为生命周期专属 Section。
    /// Lifecycle-allowed Section 允许 deprecated/superseded/replaced item 合法出现。
    /// </summary>
    public static bool IsLifecycleAllowedSection(string? sectionName)
    {
        if (string.IsNullOrWhiteSpace(sectionName)) return false;
        return LifecycleAllowedKeys.Contains(sectionName.Trim());
    }

    /// <summary>
    /// 返回给定 Section 名称是否为普通 Section（不允许 lifecycle item 出现）。
    /// 未识别的 Section 名默认视为普通 Section（更严格）。
    /// </summary>
    public static bool IsNormalSection(string? sectionName)
    {
        return !IsLifecycleAllowedSection(sectionName);
    }
}
