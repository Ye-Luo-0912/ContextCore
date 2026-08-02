using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 决策原因码映射器。把 V17.0 自由文本 <see cref="ContextDecisionCandidate.Reason"/> 映射到
/// <see cref="CandidateDecisionReasonCode"/> 枚举，使历史 trace 可被 V2 工具链消费。
/// </summary>
/// <remarks>
/// 映射策略：基于关键词匹配（不区分大小写），优先匹配更具体的原因。
/// 无法识别的 reason 返回 <see cref="CandidateDecisionReasonCode.Unknown"/>。
/// 映射保持只读，不修改输入候选。
/// </remarks>
public static class CandidateDecisionReasonCodeMapper
{
    /// <summary>
    /// 将自由文本原因映射到 <see cref="CandidateDecisionReasonCode"/>。
    /// </summary>
    public static CandidateDecisionReasonCode MapFromReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return CandidateDecisionReasonCode.Unknown;
        }

        var normalized = reason.Trim();

        // Selected 原因：优先级最高
        if (ContainsAny(normalized, "mandatory", "required-tag", "hard-required"))
        {
            return CandidateDecisionReasonCode.SelectedMandatory;
        }
        if (ContainsAny(normalized, "highest-utility", "highest-utility-score", "top-score", "highest-scoring"))
        {
            return CandidateDecisionReasonCode.SelectedHighestUtility;
        }
        if (ContainsAny(normalized, "relation-reserve", "relation-reserved", "expansion-reserve"))
        {
            return CandidateDecisionReasonCode.SelectedRelationReserve;
        }

        // Blocked 原因
        if (ContainsAny(normalized, "lifecycle-blocked", "lifecycle-state", "frozen", "lifecycle-block"))
        {
            return CandidateDecisionReasonCode.LifecycleBlocked;
        }
        if (ContainsAny(normalized, "deprecated-blocked", "deprecated-content", "deprecated-not-referenced"))
        {
            return CandidateDecisionReasonCode.DeprecatedBlocked;
        }
        if (ContainsAny(normalized, "deprecated-used-by-active", "deprecated-referenced"))
        {
            return CandidateDecisionReasonCode.DeprecatedUsedByActiveChain;
        }
        if (ContainsAny(normalized, "required-tag-mismatch", "missing-required-tag", "tag-mismatch"))
        {
            return CandidateDecisionReasonCode.RequiredTagMismatch;
        }

        // Suppressed / Exceeded
        if (ContainsAny(normalized, "duplicate", "same-content-hash", "duplicate-suppressed"))
        {
            return CandidateDecisionReasonCode.DuplicateSuppressed;
        }
        if (ContainsAny(normalized, "duplicate-section", "referenced by duplicate section"))
        {
            return CandidateDecisionReasonCode.DuplicateSectionReference;
        }
        if (ContainsAny(normalized, "section-quota", "section-cap", "per-section-take"))
        {
            return CandidateDecisionReasonCode.SectionQuotaExceeded;
        }
        if (ContainsAny(normalized, "token-budget", "budget-exhausted", "budget-truncation", "truncation"))
        {
            return CandidateDecisionReasonCode.TokenBudgetExceeded;
        }
        if (ContainsAny(normalized, "partial-accepted", "partially-accepted", "partial-truncation"))
        {
            return CandidateDecisionReasonCode.PartiallyAcceptedDueToTruncation;
        }
        if (ContainsAny(normalized, "score-below", "below-threshold", "min-score", "threshold-failed"))
        {
            return CandidateDecisionReasonCode.ScoreBelowThreshold;
        }
        if (ContainsAny(normalized, "superseded", "supersede", "replaced-by-newer"))
        {
            return CandidateDecisionReasonCode.SupersededByCurrentVersion;
        }
        if (ContainsAny(normalized, "evidence-missing", "missing-evidence", "no-evidence"))
        {
            return CandidateDecisionReasonCode.EvidenceMissing;
        }

        return CandidateDecisionReasonCode.Unknown;
    }

    /// <summary>
    /// 从候选的 Reason 字段填充 ReasonCode（若 ReasonCode 仍为 Unknown）。
    /// 幂等：已设置 ReasonCode 的候选不会被覆盖。
    /// </summary>
    public static ContextDecisionCandidate EnrichWithReasonCode(ContextDecisionCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.ReasonCode != CandidateDecisionReasonCode.Unknown)
        {
            return candidate;
        }

        var code = MapFromReason(candidate.Reason);
        return candidate with { ReasonCode = code };
    }

    /// <summary>
    /// 从候选集合中识别次要原因码。同一候选可能命中多个原因，
    /// 主原因由 <see cref="MapFromReason"/> 选取，其余识别结果填入 SecondaryReasonCodes。
    /// </summary>
    public static IReadOnlyList<CandidateDecisionReasonCode> IdentifySecondaryReasons(
        ContextDecisionCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var secondary = new List<CandidateDecisionReasonCode>();
        var reason = candidate.Reason ?? string.Empty;
        var primary = candidate.ReasonCode != CandidateDecisionReasonCode.Unknown
            ? candidate.ReasonCode
            : MapFromReason(reason);

        // 检查 lifecycle 相关次要原因
        if (primary != CandidateDecisionReasonCode.LifecycleBlocked
            && primary != CandidateDecisionReasonCode.DeprecatedBlocked
            && primary != CandidateDecisionReasonCode.DeprecatedUsedByActiveChain
            && ContainsAny(reason, "deprecated", "frozen", "superseded"))
        {
            secondary.Add(CandidateDecisionReasonCode.LifecycleBlocked);
        }

        // 检查 duplicate 相关次要原因
        if (primary != CandidateDecisionReasonCode.DuplicateSuppressed
            && primary != CandidateDecisionReasonCode.DuplicateSectionReference
            && ContainsAny(reason, "duplicate", "same-content"))
        {
            secondary.Add(CandidateDecisionReasonCode.DuplicateSuppressed);
        }

        // 检查 token budget 相关次要原因
        if (primary != CandidateDecisionReasonCode.TokenBudgetExceeded
            && primary != CandidateDecisionReasonCode.PartiallyAcceptedDueToTruncation
            && ContainsAny(reason, "budget", "truncation"))
        {
            secondary.Add(CandidateDecisionReasonCode.TokenBudgetExceeded);
        }

        return secondary;
    }

    private static bool ContainsAny(string haystack, params string[] needles)
    {
        if (string.IsNullOrEmpty(haystack))
        {
            return false;
        }
        foreach (var needle in needles)
        {
            if (string.IsNullOrEmpty(needle))
            {
                continue;
            }
            if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
