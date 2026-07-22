using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Retrieval;

// ===========================================================================
// R20-2：DefaultRetrievalRouter 实现。
//
// 目标：
//   实现 IRetrievalRouter 接口，将 (Request, Mask, PolicyBundle) 三元组
//   解析为 per-Expert 的 ExpertRoutingDecisionSet。
//
// 算法（V1 简化版）：
//   1. 解析 Routing/Budget profile（PolicyOverride > bundle）
//   2. 解析 totalTokenBudget / totalTopK（request > bundle default > hardcoded）
//   3. 应用 bundle.Routing.EnabledExperts 过滤 mask（空 = 全部启用）
//   4. 获取非 Mandatory 启用 Expert 数量 N
//   5. Budget-Aware 平均分配（V1）：
//      - Mandatory/Constraint: 不参与分配（TokenBudget=totalTokenBudget, TopK=totalTopK）
//      - 其他启用 Expert: perExpertTokenBudget = totalTokenBudget / N
//                        perExpertTopK = max(1, totalTopK / N)
//      - 禁用 Expert: TokenBudget=0, TopK=0, Enabled=false
//   6. 为所有 8 个 Expert 生成 ExpertRoutingDecision，按枚举顺序输出
//
// 设计原则：
//   1. 纯内存计算，无存储 I/O；幂等（相同输入相同输出）。
//   2. Mandatory / Constraint 永远 Enabled=true，不参与 budget 分配。
//   3. bundle.Routing.EnabledExperts 非空时：仅列出的 Expert + Mandatory/Constraint 启用。
//   4. PolicyOverride.RoutingOverride 完整替换 bundle.Routing。
//   5. V1 简化：平均分配；V2+ 可加入 per-Expert 质量—成本曲线模型。
//
// 与 Engine 集成：
//   R20-2 阶段不强制 Engine 调用 Router；Router 可独立使用。
//   Engine 可在 DecideAsync 前调用 Router，按 ExpertRoutingDecisionSet
//   对 envelope 分组应用 TopK / TokenBudget 上限（留待 R20-3+）。
// ===========================================================================

/// <summary>
/// R20-2：默认 Multi-Expert 检索路由器实现。
/// </summary>
/// <remarks>
/// V1 简化版：Budget-Aware TopK 模拟为非 Mandatory Expert 平均分配。
/// V2+ 可注入 per-Expert 质量—成本曲线模型（IRetrievalExpertProfile）。
/// </remarks>
public sealed class DefaultRetrievalRouter : IRetrievalRouter
{
    /// <summary>默认 Router 标识（用于 ExpertRoutingDecisionSet.RouterId）。</summary>
    public const string DefaultRouterId = "default-router";

    /// <summary>默认 Router 版本号（用于 ExpertRoutingDecisionSet.RouterVersion）。</summary>
    public const string DefaultRouterVersion = "v1";

    /// <summary>hardcoded 默认总 token 预算（与 DefaultPolicyBundleFactory 对齐）。</summary>
    private const int HardcodedDefaultTokenBudget = 8000;

    /// <summary>hardcoded 默认总 TopK（与 DefaultPolicyBundleFactory 对齐）。</summary>
    private const int HardcodedDefaultTopK = 50;

    /// <summary>
    /// 将 (Request, Mask, PolicyBundle) 三元组解析为 per-Expert 的 ExpertRoutingDecisionSet。
    /// </summary>
    public ExpertRoutingDecisionSet Route(
        ContextDecisionRequest request,
        RetrievalExpertMask mask,
        ContextPolicyBundle? bundle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // 1. 解析 routing / budget profile
        //    P0-3 修复：原 `request.PolicyOverride?.RoutingOverride ?? bundle?.Routing` 会
        //    完整替换 RoutingProfile，允许 Request 修改 ModelArtifactId / 模型权重 / confidence
        //    threshold / EnabledExperts。现改为 ApplyRoutingOverride 仅合并 EnableModelScoring 字段，
        //    其余字段保留 bundle 默认（与 DefaultContextDecisionEngine.ApplyRoutingOverride 对齐）。
        var routing = ApplyRoutingOverride(bundle?.Routing, request.PolicyOverride?.RoutingOverride);
        var budget = ApplyBudgetOverride(bundle?.Budget, request.PolicyOverride?.BudgetOverride);

        // 2. 解析 totalTokenBudget / totalTopK（request > bundle default > hardcoded）
        var totalTokenBudget = ResolveTotalTokenBudget(request, budget);
        var totalTopK = ResolveTotalTopK(request, budget);

        // 3. 应用 bundle.Routing.EnabledExperts 过滤 mask
        //    空列表 = 全部启用（mask 保持原样）
        //    非空列表 = 仅列出的 Expert + Mandatory/Constraint 启用
        var effectiveMask = ApplyEnabledExpertsFilter(mask, routing);

        // 4. 获取非 Mandatory 启用 Expert 数量 N（用于 budget 平均分配）
        var enabledExperts = effectiveMask.GetEnabledExperts();
        var nonMandatoryEnabledCount = enabledExperts.Count(
            e => e != RetrievalExpert.Mandatory && e != RetrievalExpert.Constraint);

        // 5. Budget-Aware 平均分配（V1 简化版）
        //    Mandatory/Constraint 不参与分配（独立占用 totalTokenBudget / totalTopK）
        //    其他启用 Expert 平均分配
        var perExpertTokenBudget = nonMandatoryEnabledCount > 0
            ? totalTokenBudget / nonMandatoryEnabledCount
            : 0;
        var perExpertTopK = nonMandatoryEnabledCount > 0
            ? Math.Max(1, totalTopK / nonMandatoryEnabledCount)
            : 0;

        // 6. 为所有 8 个 Expert 生成 ExpertRoutingDecision（按枚举顺序）
        var decisions = new List<ExpertRoutingDecision>(8);
        foreach (var expert in Enum.GetValues<RetrievalExpert>())
        {
            if (expert == RetrievalExpert.Unknown)
            {
                continue;
            }

            var isEnabled = effectiveMask.IsEnabled(expert);
            var isMandatory = expert == RetrievalExpert.Mandatory
                || expert == RetrievalExpert.Constraint;

            // 检查 mask 是否原本允许此 Expert（用于 ReasonCode 区分）
            var maskAllowed = mask.IsEnabled(expert);
            // 检查 routing.EnabledExperts 是否限制此 Expert
            var routingAllowed = IsAllowedByRouting(expert, routing);

            string reasonCode;
            string? disabledReason = null;

            if (isMandatory)
            {
                reasonCode = "mandatory-always-enabled";
            }
            else if (!isEnabled)
            {
                // 区分禁用来源：mask vs routing.EnabledExperts
                if (!maskAllowed)
                {
                    reasonCode = "ablation-disabled";
                    disabledReason = "disabled by retrieval expert mask (ablation)";
                }
                else if (!routingAllowed)
                {
                    reasonCode = "policy-disabled";
                    disabledReason = "disabled by PolicyBundle.Routing.EnabledExperts";
                }
                else
                {
                    reasonCode = "ablation-disabled";
                    disabledReason = "disabled by retrieval expert mask";
                }
            }
            else
            {
                reasonCode = "default";
            }

            var decisionTopK = isMandatory
                ? totalTopK
                : (isEnabled ? perExpertTopK : 0);
            var decisionTokenBudget = isMandatory
                ? totalTokenBudget
                : (isEnabled ? perExpertTokenBudget : 0);

            decisions.Add(new ExpertRoutingDecision
            {
                Expert = expert,
                Enabled = isEnabled,
                TopK = decisionTopK,
                TokenBudget = decisionTokenBudget,
                Weight = 1.0,
                ReasonCode = reasonCode,
                DisabledReason = disabledReason,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["totalTokenBudget"] = totalTokenBudget.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["totalTopK"] = totalTopK.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["nonMandatoryEnabledCount"] = nonMandatoryEnabledCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["perExpertTokenBudget"] = perExpertTokenBudget.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["perExpertTopK"] = perExpertTopK.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }
            });
        }

        return new ExpertRoutingDecisionSet
        {
            Decisions = decisions,
            DecidedAt = DateTimeOffset.UtcNow,
            RouterId = DefaultRouterId,
            RouterVersion = DefaultRouterVersion,
            TotalTokenBudget = totalTokenBudget
        };
    }

    // -----------------------------------------------------------------------
    // 解析 totalTokenBudget / totalTopK
    // -----------------------------------------------------------------------

    private static int ResolveTotalTokenBudget(ContextDecisionRequest request, BudgetProfile? budget)
    {
        if (request.TokenBudget > 0)
        {
            return request.TokenBudget;
        }
        if (budget is { DefaultTokenBudget: > 0 })
        {
            return budget.DefaultTokenBudget;
        }
        return HardcodedDefaultTokenBudget;
    }

    private static int ResolveTotalTopK(ContextDecisionRequest request, BudgetProfile? budget)
    {
        if (request.TopK > 0 && request.TopK != int.MaxValue)
        {
            return request.TopK;
        }
        if (budget is { DefaultTopK: > 0 })
        {
            return budget.DefaultTopK;
        }
        return HardcodedDefaultTopK;
    }

    // -----------------------------------------------------------------------
    // 应用 bundle.Routing.EnabledExperts 过滤 mask
    // -----------------------------------------------------------------------

    private static RetrievalExpertMask ApplyEnabledExpertsFilter(
        RetrievalExpertMask mask,
        RoutingProfile? routing)
    {
        // 空 EnabledExperts = 全部启用（mask 保持原样）
        if (routing is null || routing.EnabledExperts.Count == 0)
        {
            return mask;
        }

        // 非空 EnabledExperts = 仅列出的 Expert + Mandatory/Constraint 启用
        var allowedExperts = ParseEnabledExperts(routing.EnabledExperts);

        // 对每个非 Mandatory/Constraint Expert，若不在 allowed 列表则禁用
        var result = mask;
        foreach (var expert in new[]
        {
            RetrievalExpert.Lexical,
            RetrievalExpert.Semantic,
            RetrievalExpert.WorkingMemory,
            RetrievalExpert.StableMemory,
            RetrievalExpert.Graph,
            RetrievalExpert.Recency
        })
        {
            if (!allowedExperts.Contains(expert))
            {
                result = result.With(expert, enabled: false);
            }
        }

        return result;
    }

    private static bool IsAllowedByRouting(RetrievalExpert expert, RoutingProfile? routing)
    {
        if (routing is null || routing.EnabledExperts.Count == 0)
        {
            return true; // 无限制
        }
        if (expert == RetrievalExpert.Mandatory || expert == RetrievalExpert.Constraint)
        {
            return true; // 强制启用
        }
        var allowed = ParseEnabledExperts(routing.EnabledExperts);
        return allowed.Contains(expert);
    }

    private static HashSet<RetrievalExpert> ParseEnabledExperts(IReadOnlyList<string> enabledExperts)
    {
        var set = new HashSet<RetrievalExpert>();
        foreach (var name in enabledExperts)
        {
            if (Enum.TryParse<RetrievalExpert>(name, ignoreCase: true, out var expert)
                && expert != RetrievalExpert.Unknown)
            {
                set.Add(expert);
            }
        }
        // 强制包含 Mandatory / Constraint（即使 bundle 未列出）
        set.Add(RetrievalExpert.Mandatory);
        set.Add(RetrievalExpert.Constraint);
        return set;
    }

    // -----------------------------------------------------------------------
    // P0-3：受限 override 合并（与 DefaultContextDecisionEngine 对齐）
    // -----------------------------------------------------------------------

    /// <summary>
    /// P0-3：将 RequestBudgetOverride 的字段合并到 bundle 的 BudgetProfile，
    /// 仅覆盖非空字段（TokenBudget / TopK / SectionRatios），不替换整个 profile。
    /// </summary>
    private static BudgetProfile? ApplyBudgetOverride(
        BudgetProfile? baseProfile,
        RequestBudgetOverride? budgetOverride)
    {
        if (baseProfile is null) return null;
        if (budgetOverride is null) return baseProfile;
        return baseProfile with
        {
            DefaultTokenBudget = budgetOverride.TokenBudget ?? baseProfile.DefaultTokenBudget,
            DefaultTopK = budgetOverride.TopK ?? baseProfile.DefaultTopK,
            SectionRatios = budgetOverride.SectionRatios ?? baseProfile.SectionRatios
        };
    }

    /// <summary>
    /// P0-3：将 RequestRoutingOverride 的字段合并到 bundle 的 RoutingProfile，
    /// 仅覆盖 EnableModelScoring（非空时），不替换整个 profile。
    /// </summary>
    private static RoutingProfile? ApplyRoutingOverride(
        RoutingProfile? baseProfile,
        RequestRoutingOverride? routingOverride)
    {
        if (baseProfile is null) return null;
        if (routingOverride is null) return baseProfile;
        return baseProfile with
        {
            EnableModelScoring = routingOverride.EnableModelScoring ?? baseProfile.EnableModelScoring
        };
    }
}
