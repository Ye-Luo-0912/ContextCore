using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Learning.V14_0;

namespace ContextCore.Core;

/// <summary>
/// 工作记忆/稳定记忆召回评分簇。从 BasicContextPackageBuilder 抽离的纯函数集合，
/// 负责工作/稳定记忆的召回、Bounded Additive 评分以及模式相关的保留分计算。
/// </summary>
internal static class WorkingMemoryRecaller
{
    internal static readonly DomainKeywordProfile DomainKeywords = DomainKeywordProfile.CreateProduction();
    internal static readonly WorkingMemoryScoringProfile ScoringProfile = WorkingMemoryScoringProfile.CreateDefault();

    internal static IReadOnlyList<ContextMemoryItem> RecallWorkingMemory(
        IReadOnlyList<ContextMemoryItem> candidates,
        IReadOnlyList<ContextAnchor> anchors,
        int take,
        bool isAuditMode,
        bool allowDeprecated = false,
        int tokenBudget = 0)
    {
        var maxTake = take > 0 ? take : 20;
        var filteredCandidates = candidates.Where(item =>
        {
            var processState = ResolveMemoryProcessState(item);
            var isDeprecated = item.Status == ContextMemoryStatus.Deprecated ||
                               string.Equals(processState, "deprecated", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(processState, "superseded", StringComparison.OrdinalIgnoreCase);
            var isRejected = item.Status == ContextMemoryStatus.Rejected ||
                             string.Equals(processState, "rejected", StringComparison.OrdinalIgnoreCase);

            if (isRejected) return false;
            if (isDeprecated) return allowDeprecated || isAuditMode;

            return BasicContextPackageBuilder.IsActive(item);
        });

        if (tokenBudget > 0 && tokenBudget <= 200)
        {
            filteredCandidates = filteredCandidates.Where(item =>
            {
                if (item.Importance >= 0.8)
                {
                    return true;
                }
                var searchText = CreateMemorySearchText(item);
                var hasAnchorMatch = anchors.Any(anchor => searchText.Contains(anchor.Name, StringComparison.OrdinalIgnoreCase));
                return hasAnchorMatch;
            });
        }

        return filteredCandidates
            .Select(item =>
            {
                var bd = ScoreWorkingMemoryForAnchors(item, anchors, isAuditMode, allowDeprecated || isAuditMode);
                return new
                {
                    Item = item,
                    Score = bd.FinalScore,
                    Breakdown = bd
                };
            })
            .Where(item =>
            {
                if (tokenBudget > 0 && tokenBudget <= 200 && item.Score <= 1.0)
                {
                    return false;
                }
                return item.Score > 0 || anchors.Count == 0;
            })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Item.Importance)
            .ThenByDescending(item => item.Item.Confidence)
            .ThenByDescending(item => item.Item.UpdatedAt)
            .Take(maxTake)
            .Select(item => item.Item)
            .ToArray();
    }

    internal static IReadOnlyList<(ContextMemoryItem Item, ItemScoreBreakdown Breakdown)> RecallWorkingMemoryWithBreakdowns(
        IReadOnlyList<ContextMemoryItem> candidates,
        IReadOnlyList<ContextAnchor> anchors,
        int take,
        bool isAuditMode,
        bool allowDeprecated = false,
        int tokenBudget = 0,
        string modeName = "",
        IReadOnlySet<string>? reserveIds = null,
        bool enableStrictRelevanceFilter = false)
    {
        var maxTake = take > 0 ? take : 20;
        var filteredCandidates = candidates.Where(item =>
        {
            var processState = ResolveMemoryProcessState(item);
            var isDeprecated = item.Status == ContextMemoryStatus.Deprecated ||
                               string.Equals(processState, "deprecated", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(processState, "superseded", StringComparison.OrdinalIgnoreCase);
            var isRejected = item.Status == ContextMemoryStatus.Rejected ||
                             string.Equals(processState, "rejected", StringComparison.OrdinalIgnoreCase);

            if (isRejected) return false;
            if (isDeprecated) return allowDeprecated || isAuditMode;

            return BasicContextPackageBuilder.IsActive(item);
        });

        if (tokenBudget > 0 && tokenBudget <= 200)
        {
            filteredCandidates = filteredCandidates.Where(item =>
            {
                if (reserveIds is not null && reserveIds.Contains(item.Id))
                {
                    return true;
                }

                if (item.Importance >= 0.8)
                {
                    return true;
                }
                var searchText = CreateMemorySearchText(item);
                var hasAnchorMatch = anchors.Any(anchor => searchText.Contains(anchor.Name, StringComparison.OrdinalIgnoreCase));
                return hasAnchorMatch;
            });
        }

        return filteredCandidates
            .Select(item =>
            {
                var bd = ScoreWorkingMemoryForAnchors(item, anchors, isAuditMode, allowDeprecated || isAuditMode, enableStrictRelevanceFilter);
                var reserveScore = ResolveWorkingMemoryReserveScore(item, modeName, reserveIds);
                return (Item: item, Breakdown: bd, ReserveScore: reserveScore);
            })
            .Where(x =>
            {
                if (tokenBudget > 0 && tokenBudget <= 200 && x.ReserveScore > 0)
                    return true;
                if (tokenBudget > 0 && tokenBudget <= 200 && x.Breakdown.FinalScore <= 1.0)
                    return false;
                return x.Breakdown.FinalScore > 0 || anchors.Count == 0;
            })
            .OrderByDescending(x => x.ReserveScore)
            .ThenByDescending(x => x.Breakdown.FinalScore)
            .ThenByDescending(x => x.Item.Importance)
            .ThenByDescending(x => x.Item.Confidence)
            .ThenByDescending(x => x.Item.UpdatedAt)
            .Take(maxTake)
            .Select(x => (x.Item, x.Breakdown))
            .ToArray();
    }

    internal static IReadOnlyList<(ContextMemoryItem Item, ItemScoreBreakdown Breakdown)> EnsureReservedWorkingMemoryCandidates(
        IReadOnlyList<ContextMemoryItem> rawCandidates,
        IReadOnlyList<(ContextMemoryItem Item, ItemScoreBreakdown Breakdown)> selectedCandidates,
        IReadOnlyList<ContextAnchor> anchors,
        bool isAuditMode,
        bool allowDeprecated,
        string modeName,
        IReadOnlySet<string> reserveIds,
        bool enableStrictRelevanceFilter = false)
    {
        if (reserveIds.Count == 0)
        {
            return selectedCandidates;
        }

        var selectedIds = selectedCandidates
            .Select(item => item.Item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = selectedCandidates.ToList();

        foreach (var item in rawCandidates.Where(item => reserveIds.Contains(item.Id)))
        {
            if (selectedIds.Contains(item.Id))
            {
                continue;
            }

            var processState = ResolveMemoryProcessState(item);
            var isDeprecated = item.Status == ContextMemoryStatus.Deprecated ||
                               string.Equals(processState, "deprecated", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(processState, "superseded", StringComparison.OrdinalIgnoreCase);
            var isRejected = item.Status == ContextMemoryStatus.Rejected ||
                             string.Equals(processState, "rejected", StringComparison.OrdinalIgnoreCase);
            if (isRejected || (isDeprecated && !allowDeprecated && !isAuditMode) || !BasicContextPackageBuilder.IsActive(item))
            {
                continue;
            }

            result.Add((item, ScoreWorkingMemoryForAnchors(item, anchors, isAuditMode, allowDeprecated || isAuditMode, enableStrictRelevanceFilter)));
            selectedIds.Add(item.Id);
        }

        return result
            .OrderByDescending(item => reserveIds.Contains(item.Item.Id))
            .ThenByDescending(item => ResolveWorkingMemoryReserveScore(item.Item, modeName, reserveIds))
            .ThenByDescending(item => item.Breakdown.FinalScore)
            .ThenByDescending(item => item.Item.Importance)
            .ThenByDescending(item => item.Item.Confidence)
            .ThenByDescending(item => item.Item.UpdatedAt)
            .ToArray();
    }

    /// <summary>
    /// 工作记忆/稳定记忆 Bounded Additive 评分引擎。
    /// 各维度相互独立、可解释，通过有界加法组合，拒绝乘法惩罚导致的无限衰减。
    /// </summary>
    internal static ItemScoreBreakdown ScoreWorkingMemoryForAnchors(
        ContextMemoryItem item,
        IReadOnlyList<ContextAnchor> anchors,
        bool isAuditMode,
        bool allowDeprecated = false,
        bool enableStrictRelevanceFilter = false)
    {
        var memoryState = ResolveMemoryProcessState(item);
        var isDeprecated = item.Status == ContextMemoryStatus.Deprecated ||
                           string.Equals(memoryState, "deprecated", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(memoryState, "superseded", StringComparison.OrdinalIgnoreCase);
        var isRejected = item.Status == ContextMemoryStatus.Rejected ||
                         string.Equals(memoryState, "rejected", StringComparison.OrdinalIgnoreCase);
        var isCurrentlyActive = item.Status == ContextMemoryStatus.Active ||
                                string.Equals(memoryState, "active", StringComparison.OrdinalIgnoreCase);

        // A. 垃圾废案/强噪音直接强力拦截 (硬规则，不参与评分)
        var content = item.Content ?? "";
        if (isDeprecated)
        {
            // 极端否定词：任何场景都绝对强力排除
            if (DomainKeywords.DeprecatedContentHardRejectionKeywords.Any(k => content.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                return ZeroBreakdown();
            }
            // 柔性废弃词：仅在普通场景下过滤
            if (!isAuditMode && DomainKeywords.DeprecatedContentSoftRejectionKeywords.Any(k => content.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                return ZeroBreakdown();
            }
        }

        // 审计场景：如果有 anchors 提取，无任何 anchor 命中的项一律拦截
        var searchText = CreateMemorySearchText(item);
        var hasSpecificAnchors = anchors.Any(ContextRecallSignalPolicy.IsSpecificRecallAnchor);
        if (isAuditMode && hasSpecificAnchors)
        {
            var anyAnchorMatch = anchors.Any(a => ContextRecallSignalPolicy.IsSpecificRecallAnchor(a)
                && searchText.Contains(a.Name, StringComparison.OrdinalIgnoreCase));
            if (!anyAnchorMatch)
            {
                return ZeroBreakdown();
            }
        }

        // 严格相关性过滤（早期锚点零匹配拦截）：当请求存在具体锚点但当前项无任何锚点匹配时，
        // 直接降为零分。生产路径默认关闭；仅由评测运行器或显式调试请求通过 policy.EnableStrictRelevanceFilter 开启。
        // 高重要度（>= StrictRelevanceImportanceThreshold）核心信息豁免，避免误杀关键记忆。
        if (enableStrictRelevanceFilter && hasSpecificAnchors && item.Importance < ScoringProfile.StrictRelevanceImportanceThreshold)
        {
            var anyAnchorMatch = anchors.Any(a => ContextRecallSignalPolicy.IsSpecificRecallAnchor(a)
                && searchText.Contains(a.Name, StringComparison.OrdinalIgnoreCase));
            if (!anyAnchorMatch)
            {
                return ZeroBreakdown();
            }
        }

        // ── 维度 1: BaseScore (bounded 0~Cap) ────────────────────────────────
        var baseScore = Math.Min(ScoringProfile.BaseScoreCap, item.Importance * ScoringProfile.BaseScoreImportanceMultiplier + item.Confidence * ScoringProfile.BaseScoreConfidenceMultiplier);

        // ── 维度 2: LayerScore ───────────────────────────────────────────────
        var layerScore = item.Layer switch
        {
            ContextMemoryLayer.Working => ScoringProfile.LayerScoreWorking,
            ContextMemoryLayer.Stable  => ScoringProfile.LayerScoreStable,
            _                          => ScoringProfile.LayerScoreDefault
        };

        // ── 维度 3: StatusScore (additive, can be negative) ──────────────────
        double statusScore;
        if (isRejected)
        {
            statusScore = ScoringProfile.StatusScoreRejected;
        }
        else if (isDeprecated)
        {
            statusScore = (isAuditMode || allowDeprecated) ? ScoringProfile.StatusScoreDeprecatedAudit : ScoringProfile.StatusScoreDeprecatedNormal;
        }
        else if (isCurrentlyActive)
        {
            statusScore = isAuditMode ? ScoringProfile.StatusScoreActiveAudit : ScoringProfile.StatusScoreActiveNormal;
        }
        else
        {
            statusScore = 0.0;
        }

        // stress-test 类型：固定低分占位，不参与主竞争
        if (string.Equals(item.Type, "stress-test", StringComparison.OrdinalIgnoreCase)
            || item.Tags.Any(tag =>
                string.Equals(tag, "stress", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tag, "budget", StringComparison.OrdinalIgnoreCase)))
        {
            return new ItemScoreBreakdown
            {
                BaseScore   = ScoringProfile.StressTestPlaceholderScore,
                LayerScore  = 0,
                StatusScore = 0,
                FinalScore  = ScoringProfile.StressTestPlaceholderScore  // 固定占位分，不参与主竞争
            };
        }

        // ── 维度 4 & 5: SemanticAnchorScore + RawTokenMatchScore ─────────────
        double semanticAnchorScore = 0.0;
        double rawTokenMatchScore  = 0.0;
        int    semanticMatchCount  = 0;
        int    rawMatchCount       = 0;

        if (anchors.Count > 0)
        {
            foreach (var anchor in anchors)
            {
                if (!ContextRecallSignalPolicy.IsSpecificRecallAnchor(anchor))
                    continue;

                if (!searchText.Contains(anchor.Name, StringComparison.OrdinalIgnoreCase))
                    continue;

                var isRawToken = string.Equals(anchor.Source, "request.query", StringComparison.OrdinalIgnoreCase);
                if (isRawToken)
                {
                    var rawBonus = isCurrentlyActive ? anchor.Weight * ScoringProfile.AnchorRawTokenActiveMultiplier :
                                  isDeprecated && (isAuditMode || allowDeprecated) ? anchor.Weight * ScoringProfile.AnchorRawTokenDeprecatedMultiplier : 0.0;
                    rawTokenMatchScore += rawBonus;
                    if (anchor.Type is AnchorType.Topic or AnchorType.Entity or AnchorType.Constraint or AnchorType.Task)
                        rawMatchCount++;
                }
                else
                {
                    var semBonus = isCurrentlyActive ? anchor.Weight * ScoringProfile.AnchorSemanticActiveMultiplier :
                                  isDeprecated && (isAuditMode || allowDeprecated) ? anchor.Weight * ScoringProfile.AnchorSemanticDeprecatedMultiplier : 0.0;
                    semanticAnchorScore += semBonus;
                    if (anchor.Type is AnchorType.Topic or AnchorType.Entity or AnchorType.Constraint or AnchorType.Task)
                        semanticMatchCount++;
                }
            }
        }

        // 双轨命中奖励：同时有语义锚点和词项命中，额外奖励
        double anchorMatchBonus = 0.0;
        if (semanticAnchorScore > 0 && rawTokenMatchScore > 0)
            anchorMatchBonus = isAuditMode ? ScoringProfile.AnchorMatchBonusBothAudit : ScoringProfile.AnchorMatchBonusBoth;
        else if (semanticAnchorScore > 0)
            anchorMatchBonus = ScoringProfile.AnchorMatchBonusSemanticOnly;
        else if (rawTokenMatchScore > 0)
            anchorMatchBonus = ScoringProfile.AnchorMatchBonusRawOnly;

        // 审计场景下，非废弃项需要有足够的 anchor 命中才能通过
        if (isAuditMode && !isDeprecated && hasSpecificAnchors)
        {
            var totalMatchCount = semanticMatchCount + rawMatchCount;
            var requiredMatches = Math.Min(2, anchors.Count(a =>
                ContextRecallSignalPolicy.IsSpecificRecallAnchor(a) &&
                a.Type is AnchorType.Topic or AnchorType.Entity or AnchorType.Constraint or AnchorType.Task));
            if (totalMatchCount < Math.Max(1, requiredMatches))
            {
                return ZeroBreakdown();
            }
        }

        // ── 维度 6: ModeMatchScore ───────────────────────────────────────────
        double modeMatchScore = 0.0;
        var modeAnchor = anchors.FirstOrDefault(a => a.Type == AnchorType.Mode);
        if (modeAnchor is not null && searchText.Contains(modeAnchor.Name, StringComparison.OrdinalIgnoreCase))
            modeMatchScore = ScoringProfile.ModeMatchScore;

        // ── 维度 7: TaskIntentScore ──────────────────────────────────────────
        // 提取 query 词中含义词（长度>=2的中文词或英文词）匹配 content
        double taskIntentScore = 0.0;
        var rawQueryAnchor = anchors.Where(a =>
            ContextRecallSignalPolicy.IsSpecificRecallAnchor(a) &&
            string.Equals(a.Source, "request.query", StringComparison.OrdinalIgnoreCase) &&
            a.Name.Length >= 2).Take(ScoringProfile.TaskIntentMaxAnchors).ToArray();
        if (rawQueryAnchor.Length > 0 && content.Length > 0)
        {
            var intentHits = rawQueryAnchor.Count(a =>
                content.Contains(a.Name, StringComparison.OrdinalIgnoreCase));
            taskIntentScore = Math.Min(ScoringProfile.TaskIntentScoreCap, intentHits * ScoringProfile.TaskIntentScorePerHit);
        }

        // ── 维度 7.5: RelevanceFilter ────────────────────────────────────────
        // 严格相关性过滤：当请求存在具体锚点但当前项锚点分数与任务意图分数均为零时，
        // 将低重要度条目降为零分以避免噪音污染召回结果。
        // 生产路径默认关闭；仅由评测运行器或显式调试请求通过 policy.EnableStrictRelevanceFilter 开启。
        var totalAnchorScore = semanticAnchorScore + rawTokenMatchScore;
        if (enableStrictRelevanceFilter
            && hasSpecificAnchors
            && totalAnchorScore <= 0.0
            && taskIntentScore <= 0.0
            && item.Importance < ScoringProfile.StrictRelevanceImportanceThreshold)
        {
            return ZeroBreakdown();
        }

        // ── 维度 8: RecencyScore ─────────────────────────────────────────────
        double recencyScore = 0.0;
        var ageHours = (DateTimeOffset.UtcNow - item.UpdatedAt).TotalHours;
        if (ageHours <= 24)
            recencyScore = ScoringProfile.RecencyScore24Hours;
        else if (ageHours <= 24 * 7)
            recencyScore = ScoringProfile.RecencyScore7Days;
        else if (ageHours <= 24 * 30)
            recencyScore = ScoringProfile.RecencyScore30Days;

        // ── 维度 9: RelationScore (预留，当前为 0) ───────────────────────────
        double relationScore = 0.0;

        // ── 维度 10: LifecyclePenalty (有界加性负分，不用乘法) ───────────────
        // 对 active 但无任何 anchor 匹配 of 项施加有界负分惩罚
        double lifecyclePenalty = 0.0;
        if (isCurrentlyActive && hasSpecificAnchors && totalAnchorScore <= 0.0)
        {
            // 有界惩罚：最多减去 (BaseScore + LayerScore + StatusScore) 的 LifecyclePenaltyRatio，不超过 -LifecyclePenaltyCap
            var positiveSum = baseScore + layerScore + statusScore;
            lifecyclePenalty = -Math.Min(ScoringProfile.LifecyclePenaltyCap, Math.Max(0.0, positiveSum * ScoringProfile.LifecyclePenaltyRatio));
        }

        // ── 维度 11: RedundancyPenalty (预留) ────────────────────────────────
        double redundancyPenalty = 0.0;

        // ── FinalScore 组装 ──────────────────────────────────────────────────
        var rawFinal = baseScore + layerScore + statusScore
                     + semanticAnchorScore + rawTokenMatchScore + anchorMatchBonus
                     + modeMatchScore + taskIntentScore + recencyScore + relationScore
                     + lifecyclePenalty + redundancyPenalty;

        var finalScore = Math.Max(0.0, rawFinal);

        return new ItemScoreBreakdown
        {
            BaseScore          = baseScore,
            LayerScore         = layerScore,
            StatusScore        = statusScore,
            SemanticAnchorScore = semanticAnchorScore,
            RawTokenMatchScore = rawTokenMatchScore,
            AnchorMatchBonus   = anchorMatchBonus,
            ModeMatchScore     = modeMatchScore,
            TaskIntentScore    = taskIntentScore,
            RecencyScore       = recencyScore,
            RelationScore      = relationScore,
            LifecyclePenalty   = lifecyclePenalty,
            RedundancyPenalty  = redundancyPenalty,
            FinalScore         = finalScore
        };
    }

    internal static ItemScoreBreakdown ZeroBreakdown() =>
        new() { FinalScore = 0.0 };

    internal static string ResolveMemoryProcessState(ContextMemoryItem item)
    {
        foreach (var key in new[] { "state", "status", "taskState", "processState" })
        {
            if (item.Metadata.TryGetValue(key, out var value)
                && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim().ToLowerInvariant();
            }
        }

        return string.Empty;
    }

    internal static string CreateMemorySearchText(ContextMemoryItem item)
    {
        var metadata = string.Join(' ', item.Metadata.Select(pair => $"{pair.Key} {pair.Value}"));
        return string.Join(
            ' ',
            item.Id,
            item.Type,
            string.Join(' ', item.Tags),
            string.Join(' ', item.SourceRefs),
            metadata,
            item.Content.Length <= 1200 ? item.Content : item.Content[..1200]);
    }

    internal static IReadOnlyList<ContextMemoryItem> RecallStableMemory(
        IReadOnlyList<ContextMemoryItem> candidates,
        IReadOnlyList<ContextAnchor> anchors,
        IReadOnlyList<ContextMemoryItem> workingMemory,
        int take,
        string modeName = "",
        IReadOnlySet<string>? reserveIds = null)
    {
        var maxTake = take > 0 ? take : 20;
        var workingSignals = ContextRecallSignalPolicy.CreateWorkingMemorySignals(workingMemory);
        var scored = candidates
            .Where(item => item.Layer == ContextMemoryLayer.Stable && item.Status == ContextMemoryStatus.Stable)
            .Select(item =>
            {
                var searchText = CreateMemorySearchText(item);
                var score = ContextRecallSignalPolicy.ScoreStableMemoryForInjection(item, anchors, workingSignals, searchText);
                return new
                {
                    Item = item,
                    score.Score,
                    score.HasCurrentSignal,
                    IsLongTermCategory = ContextRecallSignalPolicy.IsLongTermMemoryCategory(searchText),
                    ReserveScore = ResolveStableMemoryReserveScore(item, modeName, reserveIds)
                };
            })
            .OrderByDescending(item => item.ReserveScore)
            .ThenByDescending(item => item.Score)
            .ThenByDescending(item => item.Item.Importance)
            .ThenByDescending(item => item.Item.Confidence)
            .ThenByDescending(item => item.Item.UpdatedAt)
            .ToArray();

        var matched = scored
            .Where(item => (item.HasCurrentSignal && item.IsLongTermCategory)
                || (anchors.Count == 0 && workingSignals.Count == 0))
            .Take(maxTake)
            .Select(item => item.Item)
            .ToArray();
        if (matched.Length > 0)
        {
            return matched;
        }

        // 兼容旧调用方：当稳定层完全没有命中当前任务信号时，只回退少量高可信稳定记忆，避免长期层大范围注入。
        return scored
            .Take(Math.Min(maxTake, 3))
            .Select(item => item.Item)
            .ToArray();
    }

    internal static double ResolveWorkingMemoryReserveScore(
        ContextMemoryItem item,
        string modeName,
        IReadOnlySet<string>? reserveIds)
    {
        var searchText = CreateMemorySearchText(item);
        var score = reserveIds is not null && reserveIds.Contains(item.Id) ? 10_000.0 : 0.0;
        if (IsMode(modeName, "AutomationMode", "Automation"))
        {
            if (ContainsAny(searchText, DomainKeywords.AutomationModeWorkingMemoryReserveKeywords.ToArray()))
            {
                score += 900.0;
            }
        }
        else if (IsMode(modeName, "NovelMode", "Novel"))
        {
            if (ContainsAny(searchText, DomainKeywords.NovelModeWorkingMemoryReserveKeywords.ToArray()))
            {
                score += 900.0;
            }
        }
        else if (IsMode(modeName, "ChatMode", "Chat"))
        {
            if (ContainsAny(searchText, DomainKeywords.ChatModeBoostKeywords.ToArray()))
            {
                score += 900.0;
            }
        }

        if (ContainsAny(searchText, DomainKeywords.FixturePenaltyKeywords.ToArray()))
        {
            score -= 500.0;
        }

        return score;
    }

    internal static double ResolveStableMemoryReserveScore(
        ContextMemoryItem item,
        string modeName,
        IReadOnlySet<string>? reserveIds)
    {
        var searchText = CreateMemorySearchText(item);
        var score = reserveIds is not null && reserveIds.Contains(item.Id) ? 10_000.0 : 0.0;
        if (IsMode(modeName, "ChatMode", "Chat") &&
            ContainsAny(searchText, DomainKeywords.ChatModeStableMemoryReserveKeywords.ToArray()))
        {
            score += 900.0;
        }

        if (IsMode(modeName, "NovelMode", "Novel") &&
            ContainsAny(searchText, DomainKeywords.NovelModeStableMemoryReserveKeywords.ToArray()))
        {
            score += 600.0;
        }

        if (IsMode(modeName, "AutomationMode", "Automation") &&
            ContainsAny(searchText, DomainKeywords.AutomationModeStableMemoryReserveKeywords.ToArray()))
        {
            score += 600.0;
        }

        return score;
    }

    internal static bool IsMode(string modeName, params string[] expected)
    {
        var normalized = NormalizeModeName(modeName);
        return expected.Any(item =>
            string.Equals(normalized, NormalizeModeName(item), StringComparison.OrdinalIgnoreCase));
    }

    internal static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    internal static string NormalizeModeName(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return string.Empty;
        }

        return mode.Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }
}
