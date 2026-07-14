using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 构建上下文包的不确定性（uncertainty）报告，包括：
/// 被取代项、实体版本冲突、低置信度关系、预算压力、裁剪、生命周期丢弃等。
/// 同时提供 selection ordering 所需的 priority/score 解析。
/// 所有方法均为纯函数，不持有状态。
/// </summary>
internal static class PackageUncertaintyBuilder
{
    private static readonly PackagePriorityProfile PriorityProfile = PackagePriorityProfile.CreateDefault();

    internal static IReadOnlyList<ContextPackageUncertainty> BuildUncertainties(
        IReadOnlyList<ContextPackageSection> sections,
        IReadOnlyList<ContextPackageDecision> selectedItems,
        IReadOnlyList<DroppedContextItem> droppedItems,
        IReadOnlyList<ContextRelation> lowConfidenceRelations,
        int tokenBudget,
        int estimatedTokens)
    {
        var result = new List<ContextPackageUncertainty>();
        if (selectedItems.Count == 0)
        {
            result.Add(CreateUncertainty(
                "NoSelectedContext",
                "Warning",
                "本次打包没有选中任何上下文来源。",
                string.Empty,
                Array.Empty<string>()));
        }

        if (selectedItems.Count > 0 && selectedItems.All(item => item.SourceRefs.Count == 0))
        {
            result.Add(CreateUncertainty(
                "MissingEvidence",
                "Warning",
                "本次打包的选中项缺少 sourceRefs，后续审计时证据链可能不足。",
                string.Empty,
                selectedItems.Select(item => item.ItemId).Take(20).ToArray()));
        }

        var supersededItems = ResolveSupersededSelectedItems(selectedItems, droppedItems);
        if (supersededItems.Count > 0)
        {
            result.Add(CreateUncertainty(
                "SupersededSelectedItem",
                "Warning",
                $"有 {supersededItems.Count} 个已选项存在 superseded/replaced 线索，需要优先使用更新内容。",
                string.Empty,
                supersededItems.Select(item => item.ItemId).Take(20).ToArray()));
        }

        foreach (var conflict in ResolveEntityVersionConflicts(selectedItems))
        {
            result.Add(CreateUncertainty(
                "EntityVersionConflict",
                "Warning",
                conflict.Message,
                string.Empty,
                conflict.ItemIds));
        }

        if (lowConfidenceRelations.Count > 0)
        {
            result.Add(CreateUncertainty(
                "LowConfidenceRelation",
                "Info",
                $"图谱扩展中有 {lowConfidenceRelations.Count} 条关系低于最小置信度，已从 related_context 召回中排除。",
                "related_context",
                lowConfidenceRelations.Select(item => item.Id).Take(20).ToArray()));
        }

        if (droppedItems.Count > 0)
        {
            result.Add(CreateUncertainty(
                "ExcludedItems",
                "Info",
                $"本次打包有 {droppedItems.Count} 个候选项被排除，可在 excluded 输出中查看原因。",
                "excluded",
                droppedItems.Select(item => item.ItemId).Take(20).ToArray()));
        }

        foreach (var inferred in BuildEvidenceUncertainties(sections, selectedItems))
        {
            if (!result.Any(item =>
                    string.Equals(item.Code, inferred.Code, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Message, inferred.Message, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(inferred);
            }
        }

        var tokenBudgetDrops = droppedItems
            .Where(item => item.Reason.Contains("token budget", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (tokenBudgetDrops.Length > 0)
        {
            result.Add(CreateUncertainty(
                "TokenBudgetPressure",
                "Warning",
                $"有 {tokenBudgetDrops.Length} 个候选项因 token 预算不足被排除。",
                string.Empty,
                tokenBudgetDrops.Select(item => item.ItemId).Take(20).ToArray()));
        }

        var lifecycleDrops = droppedItems
            .Where(item => item.Reason.Contains("deprecated", StringComparison.OrdinalIgnoreCase)
                || item.Reason.Contains("rejected", StringComparison.OrdinalIgnoreCase)
                || item.Reason.Contains("废弃", StringComparison.OrdinalIgnoreCase)
                || item.Reason.Contains("拒绝", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (lifecycleDrops.Length > 0)
        {
            result.Add(CreateUncertainty(
                "DeprecatedOrRejectedCandidate",
                "Info",
                $"有 {lifecycleDrops.Length} 个候选项因生命周期状态被排除。",
                "excluded",
                lifecycleDrops.Select(item => item.ItemId).Take(20).ToArray()));
        }

        var truncatedItems = selectedItems
            .Where(item => item.Reason.Contains("truncated", StringComparison.OrdinalIgnoreCase)
                || item.Reason.Contains("裁剪", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (truncatedItems.Length > 0)
        {
            result.Add(CreateUncertainty(
                "TruncatedContent",
                "Info",
                $"有 {truncatedItems.Length} 个 section 为适配预算发生内容裁剪。",
                string.Empty,
                truncatedItems.Select(item => item.ItemId).Take(20).ToArray()));
        }

        var normalizedBudget = NormalizeTokenBudget(tokenBudget);
        if (normalizedBudget > 0 && estimatedTokens >= normalizedBudget)
        {
            result.Add(CreateUncertainty(
                "BudgetFullyUsed",
                "Warning",
                "上下文包已用尽 token 预算，后续新增 section 可能被裁剪或丢弃。",
                string.Empty,
                sections.SelectMany(section => section.ItemRefs).Take(20).ToArray()));
        }

        return result;
    }

    internal static IReadOnlyList<ContextPackageDecision> ResolveSupersededSelectedItems(
        IReadOnlyList<ContextPackageDecision> selectedItems,
        IReadOnlyList<DroppedContextItem>? droppedItems = null)
    {
        var supersededIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in selectedItems)
        {
            // 1. 有明确的 supersededBy/replacedBy/deprecatedBy 指针
            if (TryReadMetadata(item.Metadata, out var replacedBy, "supersededBy", "replacedBy", "deprecatedBy")
                && !string.IsNullOrWhiteSpace(replacedBy))
            {
                supersededIds.Add(item.ItemId);
            }

            // 2. Metadata 中的状态字段标记为 superseded/deprecated/rejected
            if (TryReadMetadata(item.Metadata, out var state, "state", "status", "processState", "taskState")
                && (state.Equals("superseded", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("deprecated", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("rejected", StringComparison.OrdinalIgnoreCase)))
            {
                supersededIds.Add(item.ItemId);
            }

            // 3. lifecycleStatus 标记为 Deprecated（由 RecallWorkingMemory 在 historical_context 路径注入）
            if (TryReadMetadata(item.Metadata, out var lifecycleStatus, "lifecycleStatus")
                && string.Equals(lifecycleStatus, "Deprecated", StringComparison.OrdinalIgnoreCase))
            {
                supersededIds.Add(item.ItemId);
            }

            // 4. Kind == "historical_context" 表示该项来自废弃/审计历史区（已被系统标记为非活跃）
            if (string.Equals(item.Kind, "historical_context", StringComparison.OrdinalIgnoreCase))
            {
                supersededIds.Add(item.ItemId);
            }

            // 5. 当前选中项被另一个选中项声明为已被取代（supersedes/replaces 指向本 item）
            foreach (var replacedId in ReadMetadataList(item.Metadata, "supersedes", "replaces"))
            {
                supersededIds.Add(replacedId);
            }
        }

        // 注意：case 6（通过 droppedItem 的 supersededBy 指针反向标记 active 替代项）已移除。
        // 原因：该逻辑会错误地将当前活跃版本（替代者）标记为已被废弃，产生大量误报警告。
        // dropped item 指向 active item 仅说明 active item 是"替代版本"，而非"被替代项"，不应触发风险。

        // 仅返回普通 Section（normal sections）中的 superseded item。
        // 位于 lifecycle-allowed Section（如 historical_context、excluded 等）中的项属于合法放置，不应触发警告。
        return selectedItems
            .Where(item => supersededIds.Contains(item.ItemId)
                           && SectionLifecyclePolicy.IsNormalSection(item.SectionName))
            .ToArray();
    }

    internal static double ResolvePackageOrderScore(
        ContextPackageDecision item,
        string modeName,
        IReadOnlySet<string> mustHitIds)
    {
        var score = item.Score;
        if (item.Kind.Equals("hard_constraint", StringComparison.OrdinalIgnoreCase) ||
            item.SectionName.Equals("hard_constraints", StringComparison.OrdinalIgnoreCase))
        {
            score += 20_000.0;
        }

        if (mustHitIds.Contains(item.ItemId))
        {
            score += 10_000.0;
        }

        // 模式保留信号权重：基于决策 Metadata["signal"]/["reserve-signal"] 的显式信号匹配，
        // 替代原领域词表的内容关键词匹配。权重来自 ModeReserveWeightProfile.PackageOrderReserveWeights。
        var modeKey = WorkingMemoryRecaller.NormalizeModeName(modeName);
        if (WorkingMemoryRecaller.ReserveWeightProfile.PackageOrderReserveWeights.TryGetValue(modeKey, out var signalWeights))
        {
            foreach (var signal in WorkingMemoryRecaller.ResolveDecisionReserveSignals(item))
            {
                if (signalWeights.TryGetValue(signal, out var weight))
                {
                    score += weight;
                }
            }
        }

        var metadata = string.Join(' ', item.Metadata.Select(pair => $"{pair.Key} {pair.Value}"));
        var searchText = string.Join(' ', item.ItemId, item.Kind, item.Type, item.SectionName, item.Reason, metadata, string.Join(' ', item.SourceRefs));
        if (WorkingMemoryRecaller.ContainsAny(searchText, WorkingMemoryRecaller.DomainKeywords.FixturePenaltyKeywords))
        {
            score -= 500.0;
        }

        return score;
    }

    internal static bool TryReadMetadata(
        IReadOnlyDictionary<string, string> metadata,
        out string value,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            foreach (var (metadataKey, metadataValue) in metadata)
            {
                if (string.Equals(metadataKey, key, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(metadataValue))
                {
                    value = metadataValue;
                    return true;
                }
            }
        }

        value = string.Empty;
        return false;
    }

    internal static IEnumerable<string> ReadMetadataList(
        IReadOnlyDictionary<string, string> metadata,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!TryReadMetadata(metadata, out var value, key))
            {
                continue;
            }

            foreach (var part in value.Split(
                [',', '，', ';', '；', '|', '\r', '\n', '\t', ' '],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return part;
            }
        }
    }

    internal static string? NormalizeConflictKey(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToLowerInvariant();
    }

    private static IReadOnlyList<ContextPackageUncertainty> BuildEvidenceUncertainties(
        IReadOnlyList<ContextPackageSection> sections,
        IReadOnlyList<ContextPackageDecision> selectedItems)
    {
        var result = new List<ContextPackageUncertainty>();
        var selectedBySection = selectedItems
            .GroupBy(item => item.SectionName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var section in sections)
        {
            if (!selectedBySection.TryGetValue(section.Name, out var sectionItems))
            {
                sectionItems = [];
            }

            foreach (var signal in ExtractUncertaintySignals(section.Content))
            {
                result.Add(CreateUncertainty(
                    signal.Code,
                    "Info",
                    $"已选中证据包含不确定性线索：{signal.Snippet}",
                    section.Name,
                    section.ItemRefs.Count > 0
                        ? section.ItemRefs
                        : sectionItems.Select(item => item.ItemId).ToArray()));
            }

            foreach (var item in sectionItems)
            {
                var itemSurface = string.Join(' ', item.ItemId, item.Kind, item.Type, item.SectionName, item.Reason);
                if (itemSurface.Contains("promotion-candidate", StringComparison.OrdinalIgnoreCase) ||
                    itemSurface.Contains("candidate", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(CreateUncertainty(
                        "EvidenceUncertainty",
                        "Info",
                        "promotion candidate 的长期有效性需要复核。",
                        section.Name,
                        [item.ItemId]));
                }
            }
        }

        return result;
    }

    private static IEnumerable<(string Code, string Snippet)> ExtractUncertaintySignals(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            yield break;
        }

        foreach (var sentence in SplitDiagnosticSentences(content))
        {
            var code = ResolveEvidenceUncertaintyCode(sentence);
            if (code is null)
            {
                continue;
            }

            yield return (code, CompactDiagnosticSnippet(sentence));
        }
    }

    private static string? ResolveEvidenceUncertaintyCode(string sentence)
    {
        if (ContainsAnySignal(sentence, ["权限", "环境权限", "作用域", "scope"]))
        {
            return "ScopeUncertainty";
        }

        if (ContainsAnySignal(sentence, ["预算", "token", "TokenBudget", "超低预算"]))
        {
            return "BudgetUncertainty";
        }

        if (ContainsAnySignal(sentence, ["冲突", "矛盾", "conflict", "contradiction"]))
        {
            return "ConflictUncertainty";
        }

        if (ContainsAnySignal(sentence, ["废弃", "旧版", "deprecated", "rejected", "生命周期"]))
        {
            return "LifecycleUncertainty";
        }

        if (ContainsAnySignal(sentence, ["是否", "待确认", "仍需确认", "需要确认", "需要复核", "需要检查", "可多选", "可能", "未验证"]))
        {
            return "EvidenceUncertainty";
        }

        return null;
    }

    private static IEnumerable<string> SplitDiagnosticSentences(string content)
    {
        foreach (var part in content.Split(
                     ['\r', '\n', '。', '；', ';'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                yield return part.Trim();
            }
        }
    }

    private static string CompactDiagnosticSnippet(string value)
    {
        var text = value.Trim();
        return text.Length <= 120 ? text : text[..120];
    }

    private static ContextPackageUncertainty CreateUncertainty(
        string code,
        string severity,
        string message,
        string sectionName,
        IReadOnlyList<string> itemRefs)
    {
        return new ContextPackageUncertainty
        {
            Code = code,
            Severity = severity,
            Message = message,
            SectionName = sectionName,
            ItemRefs = itemRefs
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private static IEnumerable<(string Message, IReadOnlyList<string> ItemIds)> ResolveEntityVersionConflicts(
        IReadOnlyList<ContextPackageDecision> selectedItems)
    {
        var groups = selectedItems
            .Select(item => new
            {
                Item = item,
                Entity = ResolveEntityKey(item),
                Version = ResolveVersionKey(item)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Entity) && !string.IsNullOrWhiteSpace(item.Version))
            .GroupBy(item => item.Entity!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var versions = group
                .Select(item => item.Version!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (versions.Length <= 1)
            {
                continue;
            }

            var ordered = group
                .OrderByDescending(item => ResolvePriorityRank(item.Item))
                .ThenByDescending(item => item.Version, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var preferred = ordered[0].Item;
            var itemIds = ordered
                .Select(item => item.Item.ItemId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToArray();

            yield return (
                $"实体 `{group.Key}` 存在 {versions.Length} 个版本；建议优先使用 `{preferred.ItemId}`（{ResolvePriorityLabel(preferred)}），低优先级版本仅作为背景证据。",
                itemIds);
        }
    }

    private static string? ResolveEntityKey(ContextPackageDecision item)
    {
        return TryReadMetadata(
            item.Metadata,
            out var value,
            "entityId",
            "entity",
            "subject",
            "topicId",
            "nodeId",
            "contextId")
            ? NormalizeConflictKey(value)
            : null;
    }

    private static string? ResolveVersionKey(ContextPackageDecision item)
    {
        return TryReadMetadata(
            item.Metadata,
            out var value,
            "version",
            "revision",
            "decisionVersion",
            "schemaVersion",
            "stateVersion")
            ? NormalizeConflictKey(value)
            : null;
    }

    private static int ResolvePriorityRank(ContextPackageDecision item)
    {
        if (TryReadMetadata(item.Metadata, out var priority, "priority", "priorityScope", "scope"))
        {
            var normalized = priority.Trim().ToLowerInvariant();
            if (normalized.Contains("system", StringComparison.Ordinal)
                || normalized.Contains("safety", StringComparison.Ordinal))
            {
                return PriorityProfile.PriorityRankSystem;
            }

            if (normalized.Contains("current", StringComparison.Ordinal)
                || normalized.Contains("input", StringComparison.Ordinal))
            {
                return PriorityProfile.PriorityRankCurrent;
            }

            if (normalized.Contains("runtime", StringComparison.Ordinal))
            {
                return PriorityProfile.PriorityRankRuntime;
            }

            if (normalized.Contains("project", StringComparison.Ordinal))
            {
                return PriorityProfile.PriorityRankProject;
            }

            if (normalized.Contains("user", StringComparison.Ordinal)
                || normalized.Contains("stable", StringComparison.Ordinal))
            {
                return PriorityProfile.PriorityRankUser;
            }

            if (normalized.Contains("domain", StringComparison.Ordinal)
                || normalized.Contains("soft", StringComparison.Ordinal))
            {
                return PriorityProfile.PriorityRankDomain;
            }
        }

        if (item.Kind.Equals("recent_context", StringComparison.OrdinalIgnoreCase))
        {
            return PriorityProfile.PriorityRankRecentContext;
        }

        if (item.Kind.Equals("working_memory", StringComparison.OrdinalIgnoreCase)
            && TryReadMetadata(item.Metadata, out var state, "state", "status", "processState")
            && state.Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            return PriorityProfile.PriorityRankWorkingMemoryActive;
        }

        return item.Kind switch
        {
            "hard_constraint" => PriorityProfile.PriorityRankHardConstraint,
            "working_memory" => PriorityProfile.PriorityRankWorkingMemory,
            "global_context" => PriorityProfile.PriorityRankGlobalContext,
            "stable_memory" => PriorityProfile.PriorityRankStableMemory,
            "soft_constraint" => PriorityProfile.PriorityRankSoftConstraint,
            _ => 0
        };
    }

    private static string ResolvePriorityLabel(ContextPackageDecision item)
    {
        return TryReadMetadata(item.Metadata, out var priority, "priority", "priorityScope", "scope")
            ? priority
            : item.Kind;
    }

    private static bool ContainsAnySignal(string value, IReadOnlyList<string> signals)
    {
        return !string.IsNullOrWhiteSpace(value)
            && signals.Any(signal => value.Contains(signal, StringComparison.OrdinalIgnoreCase));
    }

    private static int NormalizeTokenBudget(int tokenBudget)
    {
        return tokenBudget == int.MaxValue || tokenBudget <= 0 ? 0 : tokenBudget;
    }
}
