using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 提供上下文包构建过程中的遗留评分、约束合并排序和生命周期状态判定。
/// 所有方法均为纯函数，不持有状态。
/// </summary>
internal static class LegacyPackageScorer
{
    private static readonly PackagePriorityProfile PriorityProfile = PackagePriorityProfile.CreateDefault();

    internal static int NormalizeTokenBudget(int tokenBudget)
    {
        return tokenBudget == int.MaxValue || tokenBudget <= 0 ? 0 : tokenBudget;
    }

    internal static IReadOnlyList<MergedContextConstraint> OrderMergedConstraints(
        IEnumerable<ContextConstraint> constraints)
    {
        return constraints
            .Select((constraint, index) =>
            {
                var priority = ResolveConstraintMergePriority(constraint);
                return new MergedContextConstraint(
                    constraint,
                    priority.Label,
                    priority.Rank,
                    index);
            })
            .OrderByDescending(item => item.PriorityRank)
            .ThenByDescending(item => item.Constraint.Confidence)
            .ThenByDescending(item => item.Constraint.UpdatedAt)
            .ThenBy(item => item.Index)
            .ToArray();
    }

    internal static bool IsActive(ContextConstraint constraint)
    {
        return constraint.Status is not ContextMemoryStatus.Deprecated
            and not ContextMemoryStatus.Rejected;
    }

    internal static bool IsActive(ContextMemoryItem item)
    {
        return item.Status is not ContextMemoryStatus.Deprecated
            and not ContextMemoryStatus.Rejected;
    }

    private static (string Label, int Rank) ResolveConstraintMergePriority(
        ContextConstraint constraint)
    {
        if (constraint.Level == ConstraintLevel.System
            || ContainsConstraintSignal(constraint, "system", "safety", "系统", "安全"))
        {
            return ("系统/安全", PriorityProfile.ConstraintMergeRankSystem);
        }

        if (ContainsConstraintSignal(constraint, "current", "input", "request", "当前", "输入"))
        {
            return ("当前输入", PriorityProfile.ConstraintMergeRankCurrent);
        }

        if (constraint.Level == ConstraintLevel.Runtime
            || ContainsConstraintSignal(constraint, "runtime", "运行时"))
        {
            return ("运行时", PriorityProfile.ConstraintMergeRankRuntime);
        }

        if (ContainsConstraintSignal(constraint, "mode", "模式"))
        {
            return ("模式", PriorityProfile.ConstraintMergeRankMode);
        }

        if (ContainsConstraintSignal(constraint, "project", "项目"))
        {
            return ("项目", PriorityProfile.ConstraintMergeRankProject);
        }

        if (constraint.Level == ConstraintLevel.Hard)
        {
            return ("硬约束", PriorityProfile.ConstraintMergeRankHard);
        }

        if (constraint.Level == ConstraintLevel.User
            || ContainsConstraintSignal(constraint, "user", "stable", "用户", "稳定"))
        {
            return ("用户稳定", PriorityProfile.ConstraintMergeRankUser);
        }

        if (constraint.Level == ConstraintLevel.Domain
            || ContainsConstraintSignal(constraint, "domain", "领域"))
        {
            return ("领域软约束", PriorityProfile.ConstraintMergeRankDomain);
        }

        return constraint.Level == ConstraintLevel.Soft
            ? ("软约束", PriorityProfile.ConstraintMergeRankSoft)
            : ("未分类约束", PriorityProfile.ConstraintMergeRankUnclassified);
    }

    private static bool ContainsConstraintSignal(
        ContextConstraint constraint,
        params string[] signals)
    {
        foreach (var (key, value) in constraint.Metadata)
        {
            if (ContainsAnySignal(key, signals) || ContainsAnySignal(value, signals))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAnySignal(string value, IReadOnlyList<string> signals)
    {
        return !string.IsNullOrWhiteSpace(value)
            && signals.Any(signal => value.Contains(signal, StringComparison.OrdinalIgnoreCase));
    }
}
