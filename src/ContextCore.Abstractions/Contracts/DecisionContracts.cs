using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

/// <summary>
/// 决策证据提供者契约。
/// 为 <see cref="ContextDecisionRecord"/> 解析结构化证据（<see cref="DecisionEvidence"/>），
/// 供 <c>ContextDecisionAuditRunner</c> 在审计时消费，验证决策的可解释性。
/// </summary>
/// <remarks>
/// 该契约是可选的：当未注册实现或实现返回 <see cref="DecisionEvidenceResult.IsComplete"/>=false 时，
/// 审计应标记 evidence-incomplete 而非失败。实现不得触发任何运行时变更。
/// </remarks>
public interface IDecisionEvidenceProvider
{
    /// <summary>
    /// 为指定决策记录解析证据。
    /// </summary>
    /// <param name="record">决策记录（含 selected/dropped 候选列表）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 证据解析结果。<see cref="DecisionEvidenceResult.Evidence"/> 按 <see cref="ContextDecisionCandidate.ItemId"/>
    /// 对应候选；未解析到的候选 ItemId 列于 <see cref="DecisionEvidenceResult.MissingItemIds"/>。
    /// </returns>
    Task<DecisionEvidenceResult> ResolveEvidenceAsync(
        ContextDecisionRecord record,
        CancellationToken cancellationToken = default);
}
