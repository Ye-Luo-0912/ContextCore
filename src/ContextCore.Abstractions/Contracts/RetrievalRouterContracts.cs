using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

// ===========================================================================
// Multi-Expert 路由器接口契约（Retrieval Router Interface Contracts）
//
// 目标：
// 在 RetrievalExpert 契约之上定义 Router 接口，让调用方可以
// 通过统一入口（IRetrievalRouter.Route）将 (Request, Mask, PolicyBundle)
// 解析为 per-Expert 的 ExpertRoutingDecisionSet，承载每个 Expert 的
// Enabled / TopK / TokenBudget / Weight 决策。
//
// 设计原则（用户澄清）：
// 1. Router 不替换 HybridContextRetriever； 阶段 Router 作为可选
// 编排路径接入，由调用方决定是否启用。Router 仅输出决策集合，
// 不直接调用 channel executor。
// 2. Router 是纯内存计算，不调用任何存储；PolicyBundle 由调用方传入。
// 3. Router 是幂等的：相同 (Request, Mask, Bundle) 三元组产生相同
// ExpertRoutingDecisionSet（确定性，无随机性）。
// 4. Budget-Aware TopK 模拟（用户澄清）：V1 简化为"非 Mandatory
// Expert 平均分配总预算"；V2+ 可加入 per-Expert 质量—成本曲线模型。
// 5. Expert-level ablation（用户澄清）：Router 通过 Mask 接受
// "哪些 Expert 关闭"作为输入，但 Mandatory / Constraint 永远强制启用。
// Router 不做全量 candidate LOO（leave-one-out）模拟，仅按 Mask
// 输出 Enabled 决策；ablation 实验由调用方在更上层执行。
// 6. PolicyBundle.Routing.EnabledExperts 作为 bundle 级过滤：空列表 = 全部启用，
// 非空列表 = 仅列出的 Expert + 强制 Mandatory/Constraint 启用。
// per-request PolicyOverride.RoutingOverride 完整替换 bundle.Routing。
//
// 与 Engine 集成的关系：
// Engine.DecideAsync 在 阶段直接读取 bundle.Routing/Bundle.Budget
// 应用到 envelope 决策。 引入 Router 后，Engine 可以在 DecideAsync
// 之前调用 Router.Route(...) 得到 per-Expert 决策集合，再据此对 envelope
// 分组应用 TopK / TokenBudget 上限。 阶段不强制 Engine 调用 Router；
// Router 作为可选编排路径，调用方（如 HybridContextRetriever wrapper
// 或 EvaluationHost）可以独立使用。
//
// 子阶段进度：
// RetrievalExpert 枚举 + ExpertRoutingDecision + Mask + 5 channel 对齐。
// （当前）：IRetrievalRouter 接口 + DefaultRetrievalRouter 实现。
// +：Router 接入 HybridContextRetriever（可选）+ per-Expert 质量—成本曲线模型。
// ===========================================================================

/// <summary>
/// Multi-Expert 检索路由器接口。
/// 将 (Request, Mask, PolicyBundle) 三元组解析为 per-Expert 的 ExpertRoutingDecisionSet。
/// </summary>
/// <remarks>
/// 设计原则：
/// 1. Router 是纯内存计算，不调用任何存储；PolicyBundle 由调用方传入。
/// 2. Router 是幂等的：相同输入产生相同输出（确定性 tie-break）。
/// 3. Mandatory / Constraint 两个 Expert 永远 Enabled=true（用户澄清：safety gate
/// 准入与 budget 限制正交）。
/// 4. Budget-Aware TopK 模拟：V1 简化为平均分配；V2+ 可加入曲线模型。
/// 5. Router 不直接调用 HybridContextRetriever 的 channel executor；
/// 调用方根据 ExpertRoutingDecisionSet 自行决定 channel 执行策略。
/// </remarks>
[Obsolete("R28-B: 已被 IRouter 取代，将在后续版本移除。")]
public interface IRetrievalRouter
{
    /// <summary>
    /// 将 (Request, Mask, PolicyBundle) 三元组解析为 per-Expert 的 ExpertRoutingDecisionSet。
    /// </summary>
    /// <param name="request">决策请求（提供 TokenBudget / TopK / PolicyOverride / WorkspaceId / CollectionId）。</param>
    /// <param name="mask">Expert 位掩码（调用方控制；Mandatory/Constraint 永远启用）。</param>
    /// <param name="bundle">策略包（提供 Budget/Routing profile 兜底）；null 时使用 hardcoded defaults。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Expert 路由决策集合（包含所有 8 个 Expert 的决策，按枚举顺序）。</returns>
    /// <exception cref="System.ArgumentNullException">request 为 null 时抛出。</exception>
    /// <exception cref="System.OperationCanceledException">cancellationToken 取消时抛出。</exception>
    ExpertRoutingDecisionSet Route(
        ContextDecisionRequest request,
        RetrievalExpertMask mask,
        ContextPolicyBundle? bundle,
        CancellationToken cancellationToken = default);
}
