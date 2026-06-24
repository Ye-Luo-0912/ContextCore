using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>短期锚点角色分类策略；集中管理角色阈值和通用审计/冲突信号。</summary>
public sealed class ShortTermAnchorClassificationProfile
{
    public double NegativeWeightThreshold { get; init; } = 0.25;

    public double PrimaryTaskOrIntentWeightThreshold { get; init; } = 0.75;

    public double PrimaryTopicWeightThreshold { get; init; } = 0.75;

    public double PrimaryModeWeightThreshold { get; init; } = 0.90;

    public IReadOnlyList<ShortTermAnchorRoleRule> RoleRules { get; init; } =
        Array.Empty<ShortTermAnchorRoleRule>();

    public static ShortTermAnchorClassificationProfile CreateDefault()
    {
        return new ShortTermAnchorClassificationProfile
        {
            RoleRules =
            [
                new(
                    RetrievalAnchorRole.Audit,
                    ["audit", "deprecated", "legacy", "obsolete", "historical", "history", "审计", "废弃", "历史", "旧版"]),
                new(
                    RetrievalAnchorRole.Conflict,
                    ["conflict", "supersede", "replace", "replaced", "冲突", "替换", "取代"])
            ]
        };
    }
}

/// <summary>根据通用信号词识别召回锚点角色的规则。</summary>
public sealed record ShortTermAnchorRoleRule(
    RetrievalAnchorRole Role,
    IReadOnlyList<string> Signals);
