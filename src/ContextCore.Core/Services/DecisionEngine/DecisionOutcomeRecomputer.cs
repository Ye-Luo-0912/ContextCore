using ContextCore.Abstractions;

namespace ContextCore.Core.Services.DecisionEngine;

/// <summary>
/// 决策产出摘要重算器（纯函数）。
/// <para>
/// "结果真相"原则：<see cref="ContextDecisionOutcomeSummary"/> 必须是最终候选分区
/// （SelectedEnvelopes / DroppedEnvelopes）的纯函数——任何调用方（Runtime、审计、replay）
/// 传入同一分区都应得到同一摘要，避免 Engine 计算结果与最终实际保留的候选不一致
/// （如 Late Hydration 移出候选后仍沿用旧计数）。
/// </para>
/// <para>
/// 本类不持有状态、不访问存储；所有派生字段（计数 / token 汇总 / sections）均从
/// 传入的 envelope 分区重算。与 <see cref="UnifiedRuntimeDefaults"/> 的 section 解析
/// （<c>ResolveSectionForAllocation</c>）与 token 读取（<c>GetEffectiveTokens</c>）
/// 共享同一份实现，消除重复逻辑。
/// </para>
/// </summary>
public static class DecisionOutcomeRecomputer
{
    /// <summary>
    /// 基于最终候选分区重算决策摘要。
    /// </summary>
    /// <param name="selectedEnvelopes">最终保留的 Selected 候选（Late Hydration / 预算修复后）。</param>
    /// <param name="droppedEnvelopes">最终 Dropped 候选（Engine Dropped + EarlyRejected + Hydration/Budget Repair Dropped）。</param>
    /// <param name="tokenBudget">请求的 token 预算上限。</param>
    /// <param name="safetyGateBlockedCount">safety gate 拦截数（来自 Engine 决策）。</param>
    /// <param name="budgetExceededCount">budget 拦截数（含预算修复裁剪，不含 hydration 失败）。</param>
    /// <param name="diagnostics">诊断字典；null 时使用空字典。</param>
    /// <param name="exactEffectiveTokens">精确 token 总数覆盖值（如 hydrate 后按真实正文计算的 ExactTokenCount）；
    /// null 时按 Selected 候选的 TokenCost.ContentTokens 汇总（缺失时回退 coarse 估算）。</param>
    /// <param name="sectionsOverride">section 名称集合覆盖值（如 Engine 已按 allocation decision 计算）；
    /// null 时按 Selected 候选的 Source 派生。</param>
    public static ContextDecisionOutcomeSummary Recompute(
        IReadOnlyList<ContextCandidateEnvelope> selectedEnvelopes,
        IReadOnlyList<ContextCandidateEnvelope> droppedEnvelopes,
        int tokenBudget,
        int safetyGateBlockedCount,
        int budgetExceededCount,
        IReadOnlyDictionary<string, string>? diagnostics = null,
        int? exactEffectiveTokens = null,
        IReadOnlyList<string>? sectionsOverride = null)
    {
        ArgumentNullException.ThrowIfNull(selectedEnvelopes);
        ArgumentNullException.ThrowIfNull(droppedEnvelopes);

        return new ContextDecisionOutcomeSummary
        {
            SelectedCount = selectedEnvelopes.Count,
            DroppedCount = droppedEnvelopes.Count,
            EffectiveTokens = exactEffectiveTokens ?? SumEffectiveTokens(selectedEnvelopes),
            TokenBudget = tokenBudget,
            Sections = sectionsOverride ?? RebuildSections(selectedEnvelopes),
            SafetyGateBlockedCount = safetyGateBlockedCount,
            BudgetExceededCount = budgetExceededCount,
            Diagnostics = diagnostics ?? new Dictionary<string, string>(StringComparer.Ordinal)
        };
    }

    /// <summary>汇总 Selected 候选的有效 token 总数（基于 TokenCost.ContentTokens 精确计算）。</summary>
    public static int SumEffectiveTokens(IReadOnlyList<ContextCandidateEnvelope> envelopes)
    {
        var total = 0;
        foreach (var envelope in envelopes)
        {
            total += GetEffectiveTokens(envelope);
        }
        return total;
    }

    /// <summary>
    /// 读取候选的有效 token 数：优先 TokenCost.ContentTokens（精确/估算），
    /// 缺失时回退到 coarse 估算（EstimatedTokens，length/4）。
    /// </summary>
    public static int GetEffectiveTokens(ContextCandidateEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
#pragma warning disable CS0618 // EstimatedTokens 保留为兼容回退；TokenCost 为空时使用
        return envelope.TokenCost?.ContentTokens ?? envelope.EstimatedTokens;
#pragma warning restore CS0618
    }

    /// <summary>按 Selected 候选的 Source 派生 section 名称集合（排序去重）。</summary>
    public static IReadOnlyList<string> RebuildSections(IReadOnlyList<ContextCandidateEnvelope> selectedEnvelopes)
    {
        var sections = new HashSet<string>(StringComparer.Ordinal);
        foreach (var envelope in selectedEnvelopes)
        {
            sections.Add(ResolveSection(envelope));
        }
        return sections.OrderBy(s => s, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// 解析候选所属 section（权威实现）。
    /// 与旧 <c>UnifiedRuntimeDefaults.ResolveSectionForAllocation</c> / hydrator 内联 switch 合并，
    /// 统一为单一来源，避免三处重复。
    /// </summary>
    public static string ResolveSection(ContextCandidateEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return envelope.Source switch
        {
            ContextCandidateSource.Mandatory or ContextCandidateSource.Constraint => "mandatory",
            ContextCandidateSource.WorkingMemory or ContextCandidateSource.StableMemory => "memory",
            ContextCandidateSource.Graph => "relations",
            ContextCandidateSource.GlobalContext => "global",
            ContextCandidateSource.RelatedContext => "related",
            _ => "default"
        };
    }
}
