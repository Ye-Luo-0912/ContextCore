using System.Globalization;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// R28-B.8.1：Allocator V2.1 默认实现（section rollover + MMR diversity）
//
// 设计原则：
//   1. 继承 IGlobalAllocator（V2.0）：基接口 Allocate 委托给 _baseAllocator，保持向后兼容。
//   2. AllocateWithDiversity（V2.1）：
//      a. 按 Section 分组（mandatory / memory / relations / global / related / default）
//      b. 每个 section 内分离 mandatory / non-mandatory（mandatory 不受 MMR 影响）
//      c. 对 non-mandatory 候选使用 MMR 重排序（Lambda < 1.0 时）
//      d. Section 顺序分配：每个 section 获得剩余预算，未用完的 rollover 到下一 section
//      e. 合并所有 section 的 decisions，构建最终 AllocationResult
//   3. mandatory 候选始终优先选入（overflow 允许），不被 MMR 重排序。
//   4. 确定性：相同输入产生相同输出（section 顺序固定，tie-break 按 CandidateId 升序）。
// ===========================================================================

/// <summary>
/// R28-B.8.1：Allocator V2.1 默认实现。支持 section rollover + MMR diversity。
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
                    EstimatedTokens = 0,
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

        var estimatedTokens = sectionResults.Sum(s => s.AllocatedTokens);
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

        var outcome = new ContextDecisionOutcomeSummary
        {
            SelectedCount = selected.Count,
            DroppedCount = dropped.Count,
            EstimatedTokens = estimatedTokens,
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
    /// 启用 rollover 时：当前 section 获得全部剩余预算，未用完的按 RolloverRatio 结转到下一 section。
    /// 禁用 rollover 时：每个 section 获得等分预算，未用完的不结转。
    /// </summary>
    private static IReadOnlyList<SectionAllocationResult> AllocateSectionsWithRollover(
        IReadOnlyList<IGrouping<string, ContextCandidateEnvelope>> sectionGroups,
        int totalBudget,
        DiversityOptions options)
    {
        var results = new List<SectionAllocationResult>(sectionGroups.Count);
        var remainingBudget = totalBudget;
        var sectionCount = sectionGroups.Count;
        // 禁用 rollover 时每个 section 的等分预算
        var perSectionBudget = sectionCount > 0 ? totalBudget / sectionCount : totalBudget;
        var borrowedFromPrevious = 0;

        foreach (var section in sectionGroups)
        {
            // 启用 rollover：当前 section 获得全部剩余预算
            // 禁用 rollover：当前 section 获得等分预算
            var sectionBudget = options.EnableSectionRollover ? remainingBudget : perSectionBudget;
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

            // 在 section 预算内分配（mandatory overflow 允许，non-mandatory 尊重预算）
            var (allocated, usedTokens) = AllocateWithinSection(ordered, sectionBudget, section.Key);
            var rollover = Math.Max(0, sectionBudget - usedTokens);

            results.Add(new SectionAllocationResult
            {
                Section = section.Key,
                AllocatedTokens = usedTokens,
                BudgetLimit = sectionBudget,
                RolloverTokens = rollover,
                BorrowedTokens = borrowedFromPrevious,
                Decisions = allocated
            });

            // 计算下一 section 的 borrowed / remaining
            if (options.EnableSectionRollover)
            {
                // rollover 到下一 section（按 RolloverRatio 缩放）
                borrowedFromPrevious = (int)(rollover * options.RolloverRatio);
                remainingBudget = borrowedFromPrevious;
            }
            else
            {
                // 禁用 rollover：下一 section 获得独立的等分预算，未用完的不结转
                borrowedFromPrevious = 0;
                remainingBudget = Math.Max(0, remainingBudget - perSectionBudget);
            }
        }

        return results;
    }

    /// <summary>
    /// 在 section 预算内分配候选。尊重候选顺序（mandatory 优先，然后 MMR 重排序后的 non-mandatory）。
    /// mandatory 候选始终选入（overflow 允许）；non-mandatory 候选按预算截断或丢弃。
    /// </summary>
    private static (List<CandidateAllocationDecision> Decisions, int UsedTokens) AllocateWithinSection(
        IReadOnlyList<ContextCandidateEnvelope> candidates,
        int sectionBudget,
        string sectionName)
    {
        var decisions = new List<CandidateAllocationDecision>(candidates.Count);
        var usedTokens = 0;

        foreach (var envelope in candidates)
        {
            var isMandatory = envelope.Safety.IsMandatory || envelope.Safety.IsHardConstraint;

            if (isMandatory)
            {
                // mandatory：始终选入（overflow 允许，与 AllowOverflowWithDiagnostic 语义一致）
                decisions.Add(new CandidateAllocationDecision
                {
                    CandidateKey = envelope.CanonicalKey,
                    Section = sectionName,
                    IncludedTokens = envelope.EstimatedTokens,
                    IsTruncated = false,
                    ReasonCode = CandidateDecisionReasonCode.SelectedMandatory
                });
                usedTokens += envelope.EstimatedTokens;
                continue;
            }

            // non-mandatory：尊重 section 预算
            if (usedTokens + envelope.EstimatedTokens <= sectionBudget)
            {
                // 完全包含
                decisions.Add(new CandidateAllocationDecision
                {
                    CandidateKey = envelope.CanonicalKey,
                    Section = sectionName,
                    IncludedTokens = envelope.EstimatedTokens,
                    IsTruncated = false,
                    ReasonCode = CandidateDecisionReasonCode.SelectedHighestUtility
                });
                usedTokens += envelope.EstimatedTokens;
            }
            else
            {
                var remaining = sectionBudget - usedTokens;
                if (remaining > 0)
                {
                    // partial truncation：候选被截断到剩余预算内
                    decisions.Add(new CandidateAllocationDecision
                    {
                        CandidateKey = envelope.CanonicalKey,
                        Section = sectionName,
                        IncludedTokens = remaining,
                        IsTruncated = true,
                        ReasonCode = CandidateDecisionReasonCode.SelectedHighestUtility
                    });
                    usedTokens += remaining;
                }
                else
                {
                    // 预算耗尽：完全丢弃
                    decisions.Add(new CandidateAllocationDecision
                    {
                        CandidateKey = envelope.CanonicalKey,
                        Section = sectionName,
                        IncludedTokens = 0,
                        IsTruncated = false,
                        ReasonCode = CandidateDecisionReasonCode.TokenBudgetExceeded
                    });
                }
            }
        }

        return (decisions, usedTokens);
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
