using System.Globalization;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// Allocator V2.1 默认实现（section rollover + MMR diversity）
//
// 设计原则：
// 1. 继承 IGlobalAllocator（V2.0）：基接口 Allocate 委托给 _baseAllocator，保持向后兼容。
// 2. AllocateWithDiversity（V2.1）：
// a. 按 Section 分组（mandatory / memory / relations / global / related / default）
// b. 每个 section 内分离 mandatory / non-mandatory（mandatory 不受 MMR 影响）
// c. 对 non-mandatory 候选使用 MMR 重排序（Lambda < 1.0 时）
// d. Section 顺序分配：每个 section 获得剩余预算，未用完的 rollover 到下一 section
// e. 合并所有 section 的 decisions，构建最终 AllocationResult
// 3. mandatory 候选始终优先选入（overflow 允许），不被 MMR 重排序。
// 4. 确定性：相同输入产生相同输出（section 顺序固定，tie-break 按 CandidateId 升序）。
// ===========================================================================

/// <summary>
/// Allocator V2.1 默认实现。支持 section rollover + MMR diversity。
/// </summary>
/// <remarks>
/// 默认不替换 V2.0 <see cref="IGlobalAllocator"/>；需要 diversity 的调用方显式注入此实现。
/// 基接口 <see cref="IGlobalAllocator.Allocate(IReadOnlyList{ContextCandidateEnvelope}, EffectivePolicySnapshot)"/>
/// 与 <see cref="IGlobalAllocator.Allocate(IReadOnlyList{ContextCandidateEnvelope}, EffectivePolicySnapshot, AllocationContext)"/>
/// 委托给构造时注入的 <paramref name="baseAllocator"/>，保持 V2.0 行为不变。
/// </remarks>
public sealed class DefaultAllocatorV2_1 : IAllocatorV2_1
{
    private readonly IGlobalAllocator _baseAllocator;
    private readonly IContentTruncator? _contentTruncator;

    /// <summary>
    /// 构造 Allocator V2.1。
    /// </summary>
    /// <param name="baseAllocator">V2.0 基础分配器（基接口 Allocate 委托给它）。</param>
    /// <param name="contentTruncator">内容截断器（可选，V2.1 当前未直接使用，预留 partial truncation 扩展）。</param>
    public DefaultAllocatorV2_1(
        IGlobalAllocator baseAllocator,
        IContentTruncator? contentTruncator = null)
    {
        _baseAllocator = baseAllocator ?? throw new ArgumentNullException(nameof(baseAllocator));
        _contentTruncator = contentTruncator;
    }

    /// <summary>基接口重载：委托给 V2.0 基础分配器。</summary>
    public AllocationResult Allocate(
        IReadOnlyList<ContextCandidateEnvelope> envelopes,
        EffectivePolicySnapshot snapshot)
        => _baseAllocator.Allocate(envelopes, snapshot);

    /// <summary>基接口重载（含 AllocationContext）：委托给 V2.0 基础分配器。</summary>
    public AllocationResult Allocate(
        IReadOnlyList<ContextCandidateEnvelope> envelopes,
        EffectivePolicySnapshot snapshot,
        AllocationContext context)
        => _baseAllocator.Allocate(envelopes, snapshot, context);

    /// <summary>
    /// 使用 MMR diversity 的分配。按 section 分组 → MMR 重排序 → section 顺序分配（含 rollover）。
    /// </summary>
    public AllocationResult AllocateWithDiversity(
        IReadOnlyList<ContextCandidateEnvelope> candidates,
        AllocationContext context,
        DiversityOptions diversityOptions)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(diversityOptions);

        var tokenBudget = context.Budget.DefaultTokenBudget;

        // 空候选集合：返回空结果
        if (candidates.Count == 0)
        {
            return new AllocationResult(
                Selected: Array.Empty<ContextCandidateEnvelope>(),
                Dropped: Array.Empty<ContextCandidateEnvelope>(),
                AllocationDecisions: Array.Empty<CandidateAllocationDecision>(),
                Outcome: new ContextDecisionOutcomeSummary
                {
                    SelectedCount = 0,
                    DroppedCount = 0,
                    EffectiveTokens = 0,
                    TokenBudget = tokenBudget,
                    Sections = Array.Empty<string>(),
                    SafetyGateBlockedCount = 0,
                    BudgetExceededCount = 0
                });
        }

        // 按 section 分组（确定性顺序：按 section 优先级 + 字母序）
        var sectionGroups = candidates
            .GroupBy(c => ResolveSectionName(c))
            .OrderBy(g => SectionPriority(g.Key))
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        // 逐 section 分配（含 rollover）
        var sectionResults = AllocateSectionsWithRollover(
            sectionGroups, tokenBudget, diversityOptions);

        // 合并所有 section 的 decisions
        var allDecisions = sectionResults.SelectMany(s => s.Decisions).ToList();
        var selectedKeys = allDecisions
            .Where(d => d.IncludedTokens > 0)
            .Select(d => d.CandidateKey)
            .ToHashSet();

        // 按原始候选顺序构建 selected / dropped（保持输入顺序，便于 trace 溯源）
        var selected = candidates.Where(c => selectedKeys.Contains(c.CanonicalKey)).ToList();
        var dropped = candidates.Where(c => !selectedKeys.Contains(c.CanonicalKey)).ToList();

        // 接入 MandatoryOverflowPolicy。
        // section 分配阶段 mandatory 候选始终选入（AllowOverflowWithDiagnostic 语义），
        // 此处在 section 分配完成后统一检查总 mandatory token 是否超出总预算：
        // - FailClosed：收集溢出 mandatory 候选 ID + 总 token 需求，抛 MandatoryContextWindowExceededException
        // （Runtime 不捕获，让请求真正失败，fail-closed 语义）
        // - RejectLowestAuthorityMandatory：按 FinalScore 升序（最低优先级优先）拒绝 mandatory 候选，
        // 直到总 mandatory token 降至预算内；被拒绝的候选移入 dropped，decision 标记 TokenBudgetExceeded
        // - AllowOverflowWithDiagnostic（默认）：当前行为不变，仅在诊断中记录溢出量
        var mandatoryOverflowPolicy = context.MandatoryOverflowPolicy;
        var mandatorySelected = selected
            .Where(e => e.Safety.IsMandatory || e.Safety.IsHardConstraint)
            .ToList();
        var mandatoryTotalTokens = mandatorySelected.Sum(GetEffectiveTokens);
        var mandatoryOverflowTokens = Math.Max(0, mandatoryTotalTokens - tokenBudget);
        var hardWindowViolated = false;

        if (mandatoryOverflowTokens > 0)
        {
            switch (mandatoryOverflowPolicy)
            {
                case MandatoryOverflowPolicy.FailClosed:
                    // 硬窗口 fail-closed — 收集溢出 mandatory 候选 ID + 总 token 需求，抛异常
                    var overflowedIds = mandatorySelected
                        .OrderByDescending(e => e.Utility.FinalScore) // 高优先级在前，末尾即最低优先级（用于诊断）
                        .Select(e => e.CandidateId)
                        .ToList();
                    throw new MandatoryContextWindowExceededException(
                        mandatoryTokens: mandatoryTotalTokens,
                        budgetLimit: tokenBudget,
                        overflowedCandidateIds: overflowedIds);

                case MandatoryOverflowPolicy.RejectLowestAuthorityMandatory:
                    // 按 FinalScore 升序（最低优先级优先）拒绝 mandatory 候选，直到总 token 降至预算内
                    var rejectable = mandatorySelected
                        .OrderBy(e => e.Utility.FinalScore)
                        .ThenBy(e => e.CandidateId, StringComparer.Ordinal)
                        .ToList();
                    foreach (var envelope in rejectable)
                    {
                        if (mandatoryTotalTokens <= tokenBudget) break;
                        var envTokens = GetEffectiveTokens(envelope);
                        mandatoryTotalTokens -= envTokens;
                        // 将候选从 selected 移入 dropped
                        selected.Remove(envelope);
                        dropped.Add(envelope);
                        // 更新 decision：IncludedTokens=0，reason=TokenBudgetExceeded
                        var decisionIdx = allDecisions.FindIndex(d => d.CandidateKey == envelope.CanonicalKey);
                        if (decisionIdx >= 0)
                        {
                            allDecisions[decisionIdx] = allDecisions[decisionIdx] with
                            {
                                IncludedTokens = 0,
                                IsTruncated = false,
                                ReasonCode = CandidateDecisionReasonCode.TokenBudgetExceeded
                            };
                        }
                    }
                    hardWindowViolated = true;
                    break;

                case MandatoryOverflowPolicy.AllowOverflowWithDiagnostic:
                default:
                    // 允许溢出但记录诊断（Package/Retrieval 默认语义），当前行为不变
                    break;
            }
        }

        // 重新计算 estimatedTokens（RejectLowestAuthorityMandatory 可能移除了 mandatory 候选，
        // 不能直接用 sectionResults.AllocatedTokens 总和，需基于最终 decisions 的 IncludedTokens）
        var estimatedTokens = allDecisions.Where(d => d.IncludedTokens > 0).Sum(d => d.IncludedTokens);
        var sections = sectionResults.Select(s => s.Section).ToList();

        // 构建 diagnostics（记录 V2.1 特有信息）
        var diagnostics = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AllocatorVersion"] = "V2.1",
            ["DiversityLambda"] = diversityOptions.Lambda.ToString(CultureInfo.InvariantCulture),
            ["SectionRolloverEnabled"] = diversityOptions.EnableSectionRollover.ToString().ToLowerInvariant(),
            ["RolloverRatio"] = diversityOptions.RolloverRatio.ToString(CultureInfo.InvariantCulture),
            ["SectionCount"] = sectionResults.Count.ToString(CultureInfo.InvariantCulture)
        };

        // 记录每个 section 的 rollover 诊断
        foreach (var sr in sectionResults)
        {
            diagnostics[$"section.{sr.Section}.allocated"] = sr.AllocatedTokens.ToString(CultureInfo.InvariantCulture);
            diagnostics[$"section.{sr.Section}.rollover"] = sr.RolloverTokens.ToString(CultureInfo.InvariantCulture);
            diagnostics[$"section.{sr.Section}.borrowed"] = sr.BorrowedTokens.ToString(CultureInfo.InvariantCulture);
        }

        // 记录 mandatory overflow 诊断（与 V2.0 DefaultGlobalAllocator 诊断字段对齐）
        if (mandatoryOverflowTokens > 0 || hardWindowViolated)
        {
            diagnostics["MandatoryOverflowTokens"] = mandatoryOverflowTokens.ToString(CultureInfo.InvariantCulture);
            diagnostics["MandatoryOverflowPolicy"] = mandatoryOverflowPolicy.ToString();
            diagnostics["HardWindowViolated"] = hardWindowViolated.ToString().ToLowerInvariant();
            diagnostics["Purpose"] = context.Purpose.ToString();
        }

        var outcome = new ContextDecisionOutcomeSummary
        {
            SelectedCount = selected.Count,
            DroppedCount = dropped.Count,
            EffectiveTokens = estimatedTokens,
            TokenBudget = tokenBudget,
            Sections = sections,
            SafetyGateBlockedCount = 0,
            BudgetExceededCount = dropped.Count,
            Diagnostics = diagnostics
        };

        return new AllocationResult(selected, dropped, allDecisions, outcome);
    }

    /// <summary>
    /// 逐 section 分配，含 rollover 逻辑。
    /// 修正：原实现"第一个 section 获得全部剩余预算 → 下一 section 只获得前一 section
    /// 剩余量 × ratio"会让靠后的 section 饿死。改为两轮分配：
    /// 1. 第一轮：每个 section 获得 minimum reserve（totalBudget × SectionReserveRatio），
    /// 在 reserve 内做基础分配，收集未使用的预算（unused reserve）。
    /// 2. 第二轮：把所有 unused reserve 汇总，按 section 顺序（已 MMR 排序）重新分配给
    /// 仍有候选被丢弃的 section，直到耗尽或全部 section 满足。
    /// 启用 rollover 时执行两轮；禁用时只执行第一轮（等分预算，不结转）。
    /// </summary>
    private static IReadOnlyList<SectionAllocationResult> AllocateSectionsWithRollover(
        IReadOnlyList<IGrouping<string, ContextCandidateEnvelope>> sectionGroups,
        int totalBudget,
        DiversityOptions options)
    {
        var results = new List<SectionAllocationResult>(sectionGroups.Count);
        var sectionCount = sectionGroups.Count;
        if (sectionCount == 0)
        {
            return results;
        }

        // 每个 section 的 minimum reserve = totalBudget × SectionReserveRatio。
        // SectionReserveRatio 默认 0.1（每个 section 至少获得 10% 总预算）。
        // 各 section reserve 之和不超过 totalBudget；若超过则按比例缩放。
        // 修正：禁用 rollover 时使用等分预算（totalBudget / sectionCount）以匹配
        // "每个 section 获得等分预算，不结转"语义；启用 rollover 时使用小 reserve + 全局 pool。
        var reserveRatio = Math.Clamp(options.SectionReserveRatio, 0.0, 1.0);
        int perSectionReserve;
        if (!options.EnableSectionRollover)
        {
            // 禁用 rollover：等分预算（无第二轮，无结转）
            perSectionReserve = sectionCount > 0 ? totalBudget / sectionCount : totalBudget;
        }
        else
        {
            // 启用 rollover：小 reserve（默认 10% × 总预算 ÷ section 数），剩余进全局 pool
            var totalReserve = (int)(totalBudget * reserveRatio);
            perSectionReserve = sectionCount > 0 ? totalReserve / sectionCount : totalReserve;
        }

        // 第一轮：每个 section 在 reserve 内分配，收集未使用 reserve。
        // sectionOrderedCandidates 保留每个 section 已排序的候选列表（第二轮复用）。
        var sectionStates = new List<SectionState>(sectionCount);

        var idx = 0;
        foreach (var section in sectionGroups)
        {
            var sectionCandidates = section.ToList();

            // 分离 mandatory / non-mandatory（mandatory 不受 MMR 影响）
            var mandatory = sectionCandidates
                .Where(e => e.Safety.IsMandatory || e.Safety.IsHardConstraint)
                .ToList();
            var nonMandatory = sectionCandidates
                .Where(e => !e.Safety.IsMandatory && !e.Safety.IsHardConstraint)
                .ToList();

            // MMR 重排序（仅对 non-mandatory，且 Lambda < 1.0 时）
            if (options.Lambda < 1.0 && nonMandatory.Count > 1)
            {
                nonMandatory = MmrDiversityScorer.RerankWithMmr(
                    nonMandatory, options.Lambda, nonMandatory.Count).ToList();
            }
            else
            {
                // Lambda=1.0（纯 relevance）：按 FinalScore 降序排序（tie-break CandidateId 升序）
                nonMandatory = nonMandatory
                    .OrderByDescending(e => e.Utility.FinalScore)
                    .ThenBy(e => e.CandidateId, StringComparer.Ordinal)
                    .ToList();
            }

            // 合并：mandatory 优先，然后（MMR 重排序后的）non-mandatory
            var ordered = mandatory.Concat(nonMandatory).ToList();

            // 第一轮：在 perSectionReserve 内分配
            // 修正：rollover 启用时禁用 partial truncation，候选未完整匹配 reserve 时整候选
            // 进入 remaining 等待 round 2 全局 pool 分配（避免 reserve 过小导致候选被截断到 reserve 大小）。
            // rollover 禁用时启用 partial truncation，等分预算内允许截断（无 round 2 兜底）。
            var firstRoundAllowTruncation = !options.EnableSectionRollover;
            var (firstRoundDecisions, firstRoundUsed, firstRoundRemainingCandidates) =
                AllocateWithinSectionWithTracking(ordered, perSectionReserve, section.Key, firstRoundAllowTruncation);

            sectionStates.Add(new SectionState
            {
                Section = section.Key,
                OrderedCandidates = ordered,
                Decisions = firstRoundDecisions,
                UsedTokens = firstRoundUsed,
                BudgetLimit = perSectionReserve,
                RemainingCandidates = firstRoundRemainingCandidates,
                BorrowedTokens = 0
            });
            idx++;
        }

        // 第二轮：把全局剩余预算（totalBudget - totalUsed）按 RolloverRatio 缩放后，按 section 顺序
        // 重新分配给仍有候选被丢弃的 section。
        // 原实现"下一 section 只获得前一 section 剩余量 × ratio"会让靠后 section 饿死；
        // 改为 round 1 各 section 仅消费自身 reserve，剩余全部预算汇总到全局 pool 供 round 2 分配。
        // 关键修正：
        // - pool = (totalBudget - totalUsed) × RolloverRatio（含未消费的 reserve + 完全未分配的预算余额）
        // - 否则单 section + 高 token 候选场景下 reserve 用不完、又拿不到主预算，会被饿死。
        // - RolloverRatio < 1 时按比例缩放结转量（保留"仅结转 50%"语义）。
        var totalUsedAfterRound1 = sectionStates.Sum(s => s.UsedTokens);
        var rawRemaining = totalBudget - totalUsedAfterRound1;
        var rolloverRatio = Math.Clamp(options.RolloverRatio, 0.0, 1.0);
        var globalRemainingBudget = (int)(rawRemaining * rolloverRatio);
        if (options.EnableSectionRollover && globalRemainingBudget > 0)
        {
            var pool = globalRemainingBudget;
            foreach (var state in sectionStates)
            {
                if (pool <= 0) break;
                if (state.RemainingCandidates.Count == 0) continue;

                // 在剩余候选中继续分配，使用 pool 中的预算（允许 partial truncation）
                var (secondRoundDecisions, secondRoundUsed, secondRoundRemaining) =
                    AllocateWithinSectionWithTracking(
                        state.RemainingCandidates, pool, state.Section, allowPartialTruncation: true);
                state.Decisions.AddRange(secondRoundDecisions);
                state.UsedTokens += secondRoundUsed;
                state.BudgetLimit += secondRoundUsed; // 实际获得的预算扩展
                state.BorrowedTokens = secondRoundUsed; // 从全局 pool 借入
                state.RemainingCandidates = secondRoundRemaining;
                pool -= secondRoundUsed;
            }
        }

        // round 2 结束后仍有候选未选入 → 生成 TokenBudgetExceeded decision（IncludedTokens=0）
        // 以便下游 trace / parity 检查能看到明确的"预算耗尽"记录。
        foreach (var state in sectionStates)
        {
            foreach (var envelope in state.RemainingCandidates)
            {
                state.Decisions.Add(new CandidateAllocationDecision
                {
                    CandidateKey = envelope.CanonicalKey,
                    Section = state.Section,
                    IncludedTokens = 0,
                    IsTruncated = false,
                    ReasonCode = CandidateDecisionReasonCode.TokenBudgetExceeded
                });
            }
            // 清空 remaining 防止重复统计
            state.RemainingCandidates.Clear();
        }

        // 构建最终 SectionAllocationResult
        foreach (var state in sectionStates)
        {
            var rollover = Math.Max(0, state.BudgetLimit - state.UsedTokens);
            results.Add(new SectionAllocationResult
            {
                Section = state.Section,
                AllocatedTokens = state.UsedTokens,
                BudgetLimit = state.BudgetLimit,
                RolloverTokens = rollover,
                BorrowedTokens = state.BorrowedTokens,
                Decisions = state.Decisions
            });
        }

        return results;
    }

    /// <summary>
    /// section 内分配，返回 decisions + used tokens + 仍可分配的候选（用于第二轮）。
    /// 与原 AllocateWithinSection 不同：返回 remaining 候选列表，便于第二轮 rollover 复用。
    /// </summary>
    /// <param name="allowPartialTruncation">
    /// 修正：round 1（rollover 启用）传 false——候选未完整匹配 reserve 时
    /// 不做截断，整候选送入 remaining 等待 round 2 全局 pool 分配；
    /// round 1（rollover 禁用）和 round 2 传 true——允许在剩余预算内做 partial truncation。
    /// </param>
    private static (List<CandidateAllocationDecision> Decisions, int UsedTokens, List<ContextCandidateEnvelope> Remaining)
        AllocateWithinSectionWithTracking(
            IReadOnlyList<ContextCandidateEnvelope> candidates,
            int sectionBudget,
            string sectionName,
            bool allowPartialTruncation = true)
    {
        var decisions = new List<CandidateAllocationDecision>(candidates.Count);
        var usedTokens = 0;
        var remaining = new List<ContextCandidateEnvelope>();

        foreach (var envelope in candidates)
        {
            var isMandatory = envelope.Safety.IsMandatory || envelope.Safety.IsHardConstraint;

            // 使用 EffectiveTokens（TokenCost 优先）替代 EstimatedTokens（length/4 粗估）。
            // 原实现用 EstimatedTokens 导致中文/JSON/代码场景严重低估 token 成本。
            var effectiveTokens = GetEffectiveTokens(envelope);

            if (isMandatory)
            {
                // mandatory：始终选入（overflow 允许，与 AllowOverflowWithDiagnostic 语义一致）
                decisions.Add(new CandidateAllocationDecision
                {
                    CandidateKey = envelope.CanonicalKey,
                    Section = sectionName,
                    IncludedTokens = effectiveTokens,
                    IsTruncated = false,
                    ReasonCode = CandidateDecisionReasonCode.SelectedMandatory
                });
                usedTokens += effectiveTokens;
                continue;
            }

            // non-mandatory：尊重 section 预算
            if (usedTokens + effectiveTokens <= sectionBudget)
            {
                // 完全包含
                decisions.Add(new CandidateAllocationDecision
                {
                    CandidateKey = envelope.CanonicalKey,
                    Section = sectionName,
                    IncludedTokens = effectiveTokens,
                    IsTruncated = false,
                    ReasonCode = CandidateDecisionReasonCode.SelectedHighestUtility
                });
                usedTokens += effectiveTokens;
            }
            else
            {
                var remainingBudget = sectionBudget - usedTokens;
                if (allowPartialTruncation && remainingBudget > 0)
                {
                    // partial truncation：候选被截断到剩余预算内
                    // 仅在 round 2 或 rollover 禁用的 round 1 启用，避免 reserve 过小导致候选被截断到 reserve 大小
                    decisions.Add(new CandidateAllocationDecision
                    {
                        CandidateKey = envelope.CanonicalKey,
                        Section = sectionName,
                        IncludedTokens = remainingBudget,
                        IsTruncated = true,
                        ReasonCode = CandidateDecisionReasonCode.SelectedHighestUtility
                    });
                    usedTokens += remainingBudget;
                }
                else
                {
                    // 预算耗尽或不允许截断：候选保留给第二轮（不立即生成 dropped decision）
                    remaining.Add(envelope);
                }
            }
        }

        return (decisions, usedTokens, remaining);
    }

    /// <summary>
    /// 获取候选的有效 token 数（与 DefaultGlobalAllocator.GetEffectiveTokens 语义一致）。
    /// 优先使用 CandidateTokenCost.ContentTokens（基于 IContextTokenizer 精确计算），
    /// 回退到 EstimatedTokens（length/4 粗估）。
    /// </summary>
    private static int GetEffectiveTokens(ContextCandidateEnvelope envelope)
        => DecisionOutcomeRecomputer.GetEffectiveTokens(envelope);

    /// <summary>section 分配中间状态（便于第二轮 rollover 复用）。</summary>
    private sealed class SectionState
    {
        public string Section { get; init; } = string.Empty;
        public List<ContextCandidateEnvelope> OrderedCandidates { get; init; } = new();
        public List<CandidateAllocationDecision> Decisions { get; init; } = new();
        public int UsedTokens { get; set; }
        public int BudgetLimit { get; set; }
        public List<ContextCandidateEnvelope> RemainingCandidates { get; set; } = new();
        public int BorrowedTokens { get; set; }
    }

    /// <summary>
    /// 将候选映射到 section 名（与 DefaultGlobalAllocator.ResolveSection 语义对齐）。
    /// </summary>
    private static string ResolveSectionName(ContextCandidateEnvelope envelope)
    {
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

    /// <summary>
    /// section 优先级（决定分配顺序）。mandatory 最优先，default 最后。
    /// </summary>
    private static int SectionPriority(string section) => section switch
    {
        "mandatory" => 0,
        "memory" => 1,
        "relations" => 2,
        "global" => 3,
        "related" => 4,
        _ => 99
    };
}
