using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// R18-2：统一决策引擎默认实现（DefaultContextDecisionEngine）
//
// 目标：
//   实现 IContextDecisionEngine 接口，编排 envelope 集合的
//   safety gate → utility scoring → budget allocation 三个阶段，
//   输出 SelectedEnvelopes + DroppedEnvelopes 集合。
//
// 设计原则：
//   1. 不替换 HybridContextRetriever / BasicContextPackageBuilder 两条主链。
//      R18-2 阶段 Engine 仅作为可选编排路径，由调用方在 adapter（R18-3/R18-4）
//      阶段决定是否接入。
//   2. Engine 是纯内存编排，不调用任何存储；候选 envelope 由调用方传入。
//   3. Engine 是幂等的：相同 Request 产生相同 Result（确定性 tie-break）。
//   4. Engine 失败时回退到 deterministic policy（ModelConfidence=0 + FinalScore=DeterministicScore），
//      不抛异常（除非 Request 本身非法）。
//   5. 不实现具体 PolicyBundle 加载（R19-1 才引入）；当前使用 hardcoded defaults。
//
// 阶段化处理流程：
//   1. SafetyGate：根据 envelope.Safety.PassesSafetyGate 分离 passing / blocked
//   2. UtilityScoring：应用 deterministic scoring（mandatory 优先 + score + tie-break）
//      + 可选 model scoring（R19-1 后由 PolicyBundle 注入；当前仅 deterministic）
//   3. BudgetAllocation：根据 DecisionSource 选择不同策略
//      - Retrieval：全局硬上限（按 TopK + TokenBudget 截断）
//      - Package：section 级分层比例分配（R18-2 阶段使用简化版，section ratios 留待 R19-1）
//   4. 输出：SelectedEnvelopes（按 FinalScore 降序 + CandidateId 升序）
//      + DroppedEnvelopes（含 BlockReasonCode）
// ===========================================================================

/// <summary>
/// R18-2：默认决策引擎实现。编排 envelope 集合的 safety gate → utility scoring →
/// budget allocation 三个阶段。不依赖具体存储；纯内存编排。
/// </summary>
public sealed class DefaultContextDecisionEngine : IContextDecisionEngine
{
    /// <summary>
    /// 对候选 envelope 集合执行 safety gate → utility scoring → budget allocation 决策。
    /// </summary>
    public Task<ContextDecisionResult> DecideAsync(
        ContextDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // 阶段 1：Safety Gate — 分离 passing / blocked
        var passing = new List<ContextCandidateEnvelope>();
        var blocked = new List<ContextCandidateEnvelope>();
        foreach (var envelope in request.Candidates)
        {
            if (envelope.Safety.PassesSafetyGate)
            {
                passing.Add(envelope);
            }
            else
            {
                blocked.Add(envelope);
            }
        }

        // 阶段 2：Utility Scoring — 应用 deterministic scoring + 确定性 tie-break
        // 排序键：IsMandatory 降序 → FinalScore 降序 → EstimatedTokens 降序 → CandidateId 升序
        // 注意：IsMandatory 不影响 safety gate 准入（已在 SafetyState 注释中说明），
        //       但在排序中强制 mandatory 候选优先于非 mandatory。
        var ordered = passing
            .OrderByDescending(e => e.Safety.IsMandatory || e.Safety.IsHardConstraint)
            .ThenByDescending(e => e.Utility.FinalScore)
            .ThenByDescending(e => e.EstimatedTokens)
            .ThenBy(e => e.CandidateId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 阶段 3：Budget Allocation — 根据 DecisionSource 选择不同策略
        var selected = new List<ContextCandidateEnvelope>();
        var droppedByBudget = new List<ContextCandidateEnvelope>();
        var tokenBudget = request.TokenBudget > 0 ? request.TokenBudget : int.MaxValue;
        var usedTokens = 0;
        var topK = request.TopK > 0 ? request.TopK : int.MaxValue;
        var takenCount = 0;

        foreach (var envelope in ordered)
        {
            // mandatory / hard constraint 候选永远选入（不受 budget 限制）
            var isMandatory = envelope.Safety.IsMandatory || envelope.Safety.IsHardConstraint;

            // TopK 检查（Retrieval 路径）
            if (!isMandatory && takenCount >= topK)
            {
                droppedByBudget.Add(envelope with
                {
                    Safety = envelope.Safety with
                    {
                        BlockReasonCode = CandidateDecisionReasonCode.SectionQuotaExceeded,
                        BlockReasonDetail = $"exceeded TopK={topK}"
                    }
                });
                continue;
            }

            // Token budget 检查（Retrieval 全局硬上限语义）
            if (!isMandatory && usedTokens + envelope.EstimatedTokens > tokenBudget)
            {
                droppedByBudget.Add(envelope with
                {
                    Safety = envelope.Safety with
                    {
                        BlockReasonCode = CandidateDecisionReasonCode.TokenBudgetExceeded,
                        BlockReasonDetail = $"exceeded token budget={tokenBudget}, used={usedTokens}"
                    }
                });
                continue;
            }

            selected.Add(envelope);
            usedTokens += envelope.EstimatedTokens;
            takenCount++;
        }

        // 合并所有 dropped（safety blocked + budget exceeded）
        var allDropped = new List<ContextCandidateEnvelope>(blocked.Count + droppedByBudget.Count);
        allDropped.AddRange(blocked);
        allDropped.AddRange(droppedByBudget);

        // 输出摘要
        var outcome = new ContextDecisionOutcomeSummary
        {
            SelectedCount = selected.Count,
            DroppedCount = allDropped.Count,
            EstimatedTokens = usedTokens,
            TokenBudget = request.TokenBudget,
            Sections = Array.Empty<string>(), // R18-2 不实现 section 分层
            SafetyGateBlockedCount = blocked.Count,
            BudgetExceededCount = droppedByBudget.Count
        };

        // 模型启用标志：Request.EnableModel && 所有候选 Utility.ModelScore 非 null
        // R18-2 阶段不真正调用模型；ModelEnabled=false 表示纯 deterministic 路径
        var modelEnabled = request.EnableModel && selected.Any(e => e.Utility.ModelScore.HasValue);

        var result = new ContextDecisionResult
        {
            RequestId = request.RequestId,
            DecisionSource = request.DecisionSource,
            SelectedEnvelopes = selected,
            DroppedEnvelopes = allDropped,
            Outcome = outcome,
            PolicyVersion = ContextDecisionPolicyVersions.DecisionSchemaV2_0,
            ModelVersion = modelEnabled ? selected.FirstOrDefault(e => e.Utility.ModelArtifactRef != null)?.Utility.ModelArtifactRef : null,
            ModelEnabled = modelEnabled
        };

        return Task.FromResult(result);
    }
}
