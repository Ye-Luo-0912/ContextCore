using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// R18-2 / R19-3：统一决策引擎默认实现（DefaultContextDecisionEngine）
//
// 目标：
//   实现 IContextDecisionEngine 接口，编排 envelope 集合的
//   safety gate → utility scoring → budget allocation 三个阶段，
//   输出 SelectedEnvelopes + DroppedEnvelopes 集合。
//
// 设计原则：
//   1. 不替换 HybridContextRetriever / BasicContextPackageBuilder 两条主链。
//      Engine 仅作为可选编排路径，由调用方在 adapter（R18-3/R18-4）
//      阶段决定是否接入。
//   2. Engine 是纯内存编排，不调用任何存储；候选 envelope 由调用方传入。
//   3. Engine 是幂等的：相同 Request 产生相同 Result（确定性 tie-break）。
//   4. Engine 失败时回退到 deterministic policy（ModelConfidence=0 + FinalScore=DeterministicScore），
//      不抛异常（除非 Request 本身非法）。
//   5. R19-3：可选注入 IPolicyRegistry。当 registry 可用时，Engine 通过
//      GetActiveBundleAsync(workspaceId, collectionId) 解析当前激活 bundle，
//      应用 Safety/Budget/Routing 三个 profile。未注入时使用 hardcoded defaults
//      保持向后兼容。
//
// 阶段化处理流程：
//   1. PolicyBundle 解析（R19-3 新增）：
//      若 _policyRegistry 可用且 request.PolicyBundleId 为空 → 调用
//      GetActiveBundleAsync(request.WorkspaceId, request.CollectionId) 解析激活 bundle。
//      应用 per-request PolicyOverride（受限：仅 Budget + Routing.EnableModelScoring）。
//   2. SafetyGate：根据 envelope.Safety + bundle.Safety 分离 passing / blocked
//      - 候选 PassesSafetyGate=false（adapter 预先标记）→ 直接 blocked
//      - IsSuperseded / IsRequiredTagMismatch → 永远 blocked（不受 bundle 控制）
//      - IsDeprecatedUsedByActiveChain && !bundle.Safety.AllowDeprecatedUsedByActiveChain → blocked
//      - IsDuplicate && !bundle.Safety.AllowDuplicateReference → blocked
//   3. UtilityScoring：应用 deterministic scoring（mandatory 优先 + score + tie-break）
//      + 可选 model scoring（bundle.Routing.EnableModelScoring + ModelConfidenceThreshold）
//      + Model failure 精确回退（FinalScore=DeterministicScore, ModelScore=null）
//   4. BudgetAllocation：根据 DecisionSource 选择不同策略
//      - Retrieval：全局硬上限（按 TopK + TokenBudget 截断）
//      - Package：section 级分层比例分配（R18-2 阶段使用简化版，section ratios 留待 R20）
//      - bundle.Budget.DefaultTokenBudget / DefaultTopK 作为 request 字段为空时的兜底
//   5. 输出：SelectedEnvelopes（按 FinalScore 降序 + CandidateId 升序）
//      + DroppedEnvelopes（含 BlockReasonCode）
//      + PolicyVersion（来自 bundle.Policies.DecisionSchemaVersion）
//      + ModelVersion（来自 bundle.Routing.ModelArtifactId 或候选 ModelArtifactRef）
// ===========================================================================

/// <summary>
/// R18-2 / R19-3：默认决策引擎实现。编排 envelope 集合的 safety gate → utility scoring →
/// budget allocation 三个阶段。可选注入 IPolicyRegistry 以应用 PolicyBundle 三个 profile。
/// </summary>
public sealed class DefaultContextDecisionEngine : IContextDecisionEngine
{
    private readonly IPolicyRegistry? _policyRegistry;

    /// <summary>构造默认 Engine（无 PolicyRegistry；使用 hardcoded defaults，向后兼容 R18-2 行为）。</summary>
    public DefaultContextDecisionEngine()
        : this(policyRegistry: null)
    {
    }

    /// <summary>构造 Engine 并注入可选 PolicyRegistry。</summary>
    /// <param name="policyRegistry">策略注册表（null 时使用 hardcoded defaults）。</param>
    public DefaultContextDecisionEngine(IPolicyRegistry? policyRegistry)
    {
        _policyRegistry = policyRegistry;
    }

    /// <summary>
    /// 对候选 envelope 集合执行 safety gate → utility scoring → budget allocation 决策。
    /// </summary>
    public async Task<ContextDecisionResult> DecideAsync(
        ContextDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // P0-2：解析 PolicyBundle
        // PolicyBundleId 非空 → 精确加载（fail-closed：找不到则抛异常，不静默回退默认 bundle）
        // PolicyBundleId 为空 → 解析 workspace/collection 激活的 bundle
        ContextPolicyBundle? bundle = null;
        if (_policyRegistry is not null)
        {
            if (!string.IsNullOrEmpty(request.PolicyBundleId))
            {
                bundle = await _policyRegistry.GetBundleAsync(
                    request.PolicyBundleId, version: null, cancellationToken).ConfigureAwait(false);
                if (bundle is null)
                {
                    throw new InvalidOperationException(
                        $"PolicyBundle not found: BundleId={request.PolicyBundleId}. " +
                        "Explicit bundle reference must resolve; fail-closed.");
                }
            }
            else
            {
                bundle = await _policyRegistry.GetActiveBundleAsync(
                    request.WorkspaceId, request.CollectionId, cancellationToken).ConfigureAwait(false);
            }
        }

        // P0-3：应用受限 override（合并到 bundle profile，不替换整个 profile）
        // 不允许替换 SafetyProfile；BudgetOverride 仅调整 TokenBudget/TopK/SectionRatios；
        // RoutingOverride 仅调整 EnableModelScoring。
        var safety = bundle?.Safety;
        var budget = ApplyBudgetOverride(bundle?.Budget, request.PolicyOverride?.BudgetOverride);
        var routing = ApplyRoutingOverride(bundle?.Routing, request.PolicyOverride?.RoutingOverride);

        // 阶段 1：Safety Gate — 分离 passing / blocked
        var passing = new List<ContextCandidateEnvelope>();
        var blocked = new List<ContextCandidateEnvelope>();
        foreach (var envelope in request.Candidates)
        {
            var (passes, reason, detail) = EvaluateSafetyGate(envelope.Safety, safety);
            if (passes)
            {
                passing.Add(envelope);
            }
            else
            {
                blocked.Add(envelope with
                {
                    Safety = envelope.Safety with
                    {
                        PassesSafetyGate = false,
                        BlockReasonCode = reason,
                        BlockReasonDetail = detail
                    }
                });
            }
        }

        // 阶段 2：Utility Scoring — 应用 ModelConfidenceThreshold + Model failure 回退
        // enableModel：request.EnableModel && routing.EnableModelScoring（routing=null 时视为 permissive）
        var enableModel = request.EnableModel && (routing?.EnableModelScoring ?? true);
        var scored = passing.Select(e => ApplyUtilityScoring(e, routing, enableModel)).ToList();

        // 排序键：IsMandatory 降序 → FinalScore 降序 → EstimatedTokens 降序 → CandidateId 升序
        // 注意：IsMandatory 不影响 safety gate 准入（已在 SafetyState 注释中说明），
        //       但在排序中强制 mandatory 候选优先于非 mandatory。
        var ordered = scored
            .OrderByDescending(e => e.Safety.IsMandatory || e.Safety.IsHardConstraint)
            .ThenByDescending(e => e.Utility.FinalScore)
            .ThenByDescending(e => e.EstimatedTokens)
            .ThenBy(e => e.CandidateId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 阶段 3：Budget Allocation — bundle.Budget 作为 request 字段为空时的兜底
        var selected = new List<ContextCandidateEnvelope>();
        var droppedByBudget = new List<ContextCandidateEnvelope>();
        var tokenBudget = request.TokenBudget > 0
            ? request.TokenBudget
            : (budget is { DefaultTokenBudget: > 0 } ? budget.DefaultTokenBudget : int.MaxValue);
        var usedTokens = 0;
        var topK = (request.TopK > 0 && request.TopK != int.MaxValue)
            ? request.TopK
            : (budget is { DefaultTopK: > 0 } ? budget.DefaultTopK : int.MaxValue);
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
        var outcomeTokenBudget = request.TokenBudget > 0
            ? request.TokenBudget
            : (budget?.DefaultTokenBudget ?? 0);
        var outcome = new ContextDecisionOutcomeSummary
        {
            SelectedCount = selected.Count,
            DroppedCount = allDropped.Count,
            EstimatedTokens = usedTokens,
            TokenBudget = outcomeTokenBudget,
            Sections = Array.Empty<string>(), // R18-2 不实现 section 分层
            SafetyGateBlockedCount = blocked.Count,
            BudgetExceededCount = droppedByBudget.Count
        };

        // 模型启用标志：enableModel && 至少一个 selected 候选仍保留 ModelScore
        // （ModelConfidence 低于阈值的候选已在 ApplyUtilityScoring 中回退为 null）
        var modelEnabled = enableModel && selected.Any(e => e.Utility.ModelScore.HasValue);
        var modelVersion = modelEnabled
            ? (routing?.ModelArtifactId
               ?? selected.FirstOrDefault(e => e.Utility.ModelArtifactRef != null)?.Utility.ModelArtifactRef)
            : null;

        var result = new ContextDecisionResult
        {
            RequestId = request.RequestId,
            DecisionSource = request.DecisionSource,
            SelectedEnvelopes = selected,
            DroppedEnvelopes = allDropped,
            Outcome = outcome,
            PolicyVersion = bundle?.Policies.DecisionSchemaVersion ?? ContextDecisionPolicyVersions.DecisionSchemaV2_0,
            ModelVersion = modelVersion,
            ModelEnabled = modelEnabled
        };

        return result;
    }

    // -----------------------------------------------------------------------
    // SafetyGate 评估
    // -----------------------------------------------------------------------

    private static (bool Passes, CandidateDecisionReasonCode Reason, string Detail) EvaluateSafetyGate(
        CandidateSafetyState candidate, SafetyProfile? safety)
    {
        // 1. 候选自身 PassesSafetyGate=false（adapter 已预先标记）→ 信任之
        if (!candidate.PassesSafetyGate)
        {
            return (false, candidate.BlockReasonCode, candidate.BlockReasonDetail);
        }

        // 2. 无 bundle → 不应用额外 safety 检查（向后兼容 R18-2 行为）
        if (safety is null)
        {
            return (true, CandidateDecisionReasonCode.Unknown, string.Empty);
        }

        // 3. 应用 bundle SafetyProfile
        // IsSuperseded / IsRequiredTagMismatch 永远阻断（不受 bundle Allow* 字段控制）
        if (candidate.IsSuperseded)
        {
            return (false, CandidateDecisionReasonCode.SupersededByCurrentVersion,
                "superseded by newer version");
        }

        if (candidate.IsRequiredTagMismatch)
        {
            return (false, CandidateDecisionReasonCode.RequiredTagMismatch,
                "missing required tag");
        }

        // IsDeprecatedUsedByActiveChain 受 bundle.Safety.AllowDeprecatedUsedByActiveChain 控制
        if (candidate.IsDeprecatedUsedByActiveChain && !safety.AllowDeprecatedUsedByActiveChain)
        {
            return (false, CandidateDecisionReasonCode.DeprecatedBlocked,
                "deprecated-used-by-active-chain blocked by safety profile");
        }

        // IsDuplicate 受 bundle.Safety.AllowDuplicateReference 控制
        if (candidate.IsDuplicate && !safety.AllowDuplicateReference)
        {
            return (false, CandidateDecisionReasonCode.DuplicateSuppressed,
                "duplicate reference blocked by safety profile");
        }

        return (true, CandidateDecisionReasonCode.Unknown, string.Empty);
    }

    // -----------------------------------------------------------------------
    // Utility Scoring 评估（含 Model failure 精确回退）
    // -----------------------------------------------------------------------

    private static ContextCandidateEnvelope ApplyUtilityScoring(
        ContextCandidateEnvelope envelope,
        RoutingProfile? routing,
        bool enableModel)
    {
        var utility = envelope.Utility;

        // 未启用模型 且 候选有 ModelScore → 精确回退到 deterministic
        // （验收标准 #6：Model failure 时 ModelConfidence=0 + ModelScore=null + ReasonCode="fallback-to-deterministic"）
        if (!enableModel && utility.ModelScore is not null)
        {
            return envelope with
            {
                Utility = utility with
                {
                    FinalScore = utility.DeterministicScore,
                    ModelScore = null,
                    ModelConfidence = 0,
                    ReasonCode = "fallback-to-deterministic"
                }
            };
        }

        // 模型启用但候选无 ModelScore → 保持原样
        if (!enableModel || utility.ModelScore is null)
        {
            return envelope;
        }

        // 应用 ModelConfidenceThreshold（仅当 routing 显式提供时）
        // ModelConfidence < threshold → 回退到 DeterministicScore
        if (routing is not null && utility.ModelConfidence < routing.ModelConfidenceThreshold)
        {
            return envelope with
            {
                Utility = utility with
                {
                    FinalScore = utility.DeterministicScore,
                    ModelScore = null,
                    ModelConfidence = 0,
                    ReasonCode = "fallback-to-deterministic"
                }
            };
        }

        // 不重新计算 FinalScore；保留 envelope 预设值
        // （Engine 信任调用方/adapter 已正确加权；R20 Router 才会真正注入模型权重）
        return envelope;
    }

    // -----------------------------------------------------------------------
    // P0-3：受限 override 合并辅助方法
    // -----------------------------------------------------------------------

    /// <summary>
    /// P0-3：将 RequestBudgetOverride 的字段合并到 bundle 的 BudgetProfile，
    /// 仅覆盖非空字段，不替换整个 profile。
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
