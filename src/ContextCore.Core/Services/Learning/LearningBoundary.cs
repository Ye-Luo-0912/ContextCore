namespace ContextCore.Core.Services.Learning;

/// <summary>
/// 学习边界：限定模型可以学习的表面，禁止模型自行修改权限/租户/排除/生命周期/
/// 安全 gate/迁移/持久化规则。边界是确定性守卫，不随数据或策略变化。
/// </summary>
public static class LearningBoundary
{
    /// <summary>可学习表面（优先学习清单）。</summary>
    public static readonly IReadOnlySet<string> LearnableSurfaces =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "QueryExpansionSelection", // 查询扩展选择
            "ChannelBudget",           // 通道预算
            "CandidateRerank",         // 候选重排
            "MemoryPromotionSuggestion" // 记忆晋升建议
        };

    /// <summary>禁止学习表面（模型不得自行修改）。</summary>
    public static readonly IReadOnlySet<string> ForbiddenSurfaces =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Permissions",    // 权限
            "Tenant",         // 租户
            "Exclusion",      // 排除
            "Lifecycle",      // 生命周期
            "SafetyGate",     // 安全 gate
            "Migration",      // 迁移
            "Persistence"     // 持久化
        };

    /// <summary>判断指定表面是否允许学习。</summary>
    public static bool IsLearnable(string surface)
    {
        if (string.IsNullOrWhiteSpace(surface))
        {
            return false;
        }

        if (ForbiddenSurfaces.Contains(surface))
        {
            return false;
        }

        return LearnableSurfaces.Contains(surface);
    }

    /// <summary>
    /// 校验一组拟学习表面；返回被边界禁止的表面清单（空 = 全部允许）。
    /// </summary>
    public static IReadOnlyList<string> Validate(IEnumerable<string> proposedSurfaces)
    {
        ArgumentNullException.ThrowIfNull(proposedSurfaces);
        return proposedSurfaces
            .Where(static surface => !string.IsNullOrWhiteSpace(surface))
            .Where(surface => !IsLearnable(surface))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
