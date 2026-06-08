using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.ControlRoom.Services;

/// <summary>
/// 评测不确定性匹配解析器。
/// 仅用于 PackageUncertaintyHit 判定和诊断输出，不影响 warning 触发逻辑。
/// </summary>
public static class UncertaintyMatchResolver
{
    private static readonly Dictionary<string, string[]> CodeAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["SupersededSelectedItem"] = ["DeprecatedOrRejectedCandidate", "SupersededItem", "Deprecated", "Superseded"],
            ["DeprecatedItemSelected"] = ["DeprecatedOrRejectedCandidate"],
            ["RejectedCandidate"]      = ["DeprecatedOrRejectedCandidate"],
            ["ConflictingVersions"]    = ["ConflictingContext", "Conflict", "ConflictEvidence"],
            ["ConflictEvidence"]       = ["ConflictingContext", "Conflict"],
            ["LowConfidence"]          = ["LowConfidenceContext", "LowConfidenceRelation"],
            ["OverBudget"]             = ["TokenBudgetPressure", "BudgetExceeded", "NearTokenBudget"],
            ["MissingContext"]         = ["MissingRequiredContext", "MissingEvidence", "NoSelectedContext"],
        };

    private static readonly HashSet<string> LifecycleFamilyCodes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "SupersededSelectedItem", "DeprecatedItemSelected",
            "Superseded", "Deprecated",
            "RejectedCandidate", "DeprecatedOrRejectedCandidate",
        };

    private static readonly HashSet<string> ConflictFamilyCodes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "ConflictingVersions", "ConflictEvidence", "Conflict", "Contradiction",
        };

    private static readonly Dictionary<string, string[]> SemanticAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["长期有效性需要复核"] = ["promotion candidate", "promotion-candidate", "候选", "提升候选", "长期有效", "复核"],
            ["样本窗口是否足够大"] = ["样本窗口", "最近失败", "失败趋势", "最近统计", "统计"],
            ["负责人是否已配置"] = ["负责人", "处理人", "谁处理", "归属", "死信队列", "dead-letter"],
            ["失败是否可自动修复"] = ["失败是否可自动修复", "可执行修复建议", "自动修复待确认", "修复建议"],
            ["恢复点之后的外部状态是否变化"] = ["恢复点", "外部状态", "环境快照", "恢复前检查", "precheck"],
            ["命令是否受当前环境权限限制"] = ["权限限制", "当前环境权限", "命令可能受当前环境权限限制"],
            ["伏笔是否已经兑现需要检查"] = ["伏笔", "兑现", "需要检查"],
            ["兑现方式可多选"] = ["兑现方式可多选", "可多选", "伏笔兑现"],
            ["失败是否由输出文案变更导致"] = ["输出文案", "断言", "变更导致"],
        };

    public static bool IsSatisfied(
        string expectedCode,
        IReadOnlyList<ContextPackageUncertainty> uncertainties,
        IReadOnlyList<ContextPackageDecision> selectedItems,
        IReadOnlyList<DroppedContextItem> droppedItems,
        IReadOnlyList<ContextPackageSection>? sections = null,
        ContextPackageStandardOutput? output = null)
    {
        return Resolve(expectedCode, uncertainties, selectedItems, droppedItems, sections, output).Satisfied;
    }

    public static UncertaintyMatchResolution Resolve(
        string expectedCode,
        IReadOnlyList<ContextPackageUncertainty> uncertainties,
        IReadOnlyList<ContextPackageDecision> selectedItems,
        IReadOnlyList<DroppedContextItem> droppedItems,
        IReadOnlyList<ContextPackageSection>? sections = null,
        ContextPackageStandardOutput? output = null)
    {
        if (string.IsNullOrWhiteSpace(expectedCode))
        {
            return new UncertaintyMatchResolution(expectedCode, true, "empty_expected", string.Empty);
        }

        var aliases = CodeAliases.TryGetValue(expectedCode, out var aliasValues)
            ? aliasValues
            : Array.Empty<string>();
        var semanticAliases = ResolveSemanticAliases(expectedCode);

        var uncertaintyMatch = FindUncertaintyMatch(expectedCode, aliases, semanticAliases, uncertainties);
        if (uncertaintyMatch is not null)
        {
            return uncertaintyMatch;
        }

        var diagnosticsMatch = FindSectionMatch(
            expectedCode,
            sections,
            semanticAliases,
            "diagnostics",
            "uncertainties",
            "risk_flags");
        if (diagnosticsMatch is not null)
        {
            return diagnosticsMatch with { Source = "diagnostics" };
        }

        if (ConflictFamilyCodes.Contains(expectedCode))
        {
            if (selectedItems.Any(item => IsSection(item.SectionName, "conflict_evidence")) ||
                HasSection(sections, "conflict_evidence") ||
                HasOutputSection(output?.Evidence, "conflict_evidence"))
            {
                return new UncertaintyMatchResolution(
                    expectedCode,
                    true,
                    "conflict_evidence",
                    "conflict evidence section present");
            }
        }

        if (LifecycleFamilyCodes.Contains(expectedCode))
        {
            if (droppedItems.Any(HasLifecycleExcludedReason))
            {
                return new UncertaintyMatchResolution(
                    expectedCode,
                    true,
                    "excluded_reason",
                    "deprecated/rejected item excluded");
            }

            if (selectedItems.Any(item =>
                    SectionLifecyclePolicy.IsLifecycleAllowedSection(item.SectionName) &&
                    item.Metadata.ContainsKey("lifecycleStatus")))
            {
                return new UncertaintyMatchResolution(
                    expectedCode,
                    true,
                    "historical_context",
                    "lifecycle item selected in lifecycle-allowed section");
            }

            if (HasSection(sections, "deprecated_evidence", "historical_context") ||
                HasOutputSection(output?.Evidence, "deprecated_evidence", "historical_context"))
            {
                return new UncertaintyMatchResolution(
                    expectedCode,
                    true,
                    "deprecated_evidence",
                    "lifecycle evidence section present");
            }
        }

        var historicalMatch = FindSectionMatch(
            expectedCode,
            sections,
            semanticAliases,
            "historical_context",
            "deprecated_evidence",
            "conflict_evidence");
        if (historicalMatch is not null)
        {
            return historicalMatch;
        }

        var evidenceMatch = FindOutputMatch(expectedCode, semanticAliases, output?.Evidence, "conflict_evidence", "deprecated_evidence", "historical_context");
        if (evidenceMatch is not null)
        {
            return evidenceMatch;
        }

        var excludedMatch = FindDroppedReasonMatch(expectedCode, semanticAliases, droppedItems);
        if (excludedMatch is not null)
        {
            return excludedMatch;
        }

        var riskFlagMatch = FindRiskFlagMatch(expectedCode, semanticAliases, selectedItems, droppedItems);
        if (riskFlagMatch is not null)
        {
            return riskFlagMatch;
        }

        var wrongSectionMatch = FindSectionMatch(
            expectedCode,
            sections,
            semanticAliases,
            "current_task",
            "hard_constraints",
            "constraints",
            "working_memory",
            "stable_memory",
            "recent_context",
            "related_context",
            "evidence");
        if (wrongSectionMatch is not null)
        {
            return wrongSectionMatch with
            {
                Satisfied = false,
                Source = wrongSectionMatch.Source,
                FailureType = "UncertaintyPresentButWrongSection"
            };
        }

        if (semanticAliases.Count > 0 && SurfaceContainsAnyAlias(semanticAliases, uncertainties, selectedItems, droppedItems, sections, output))
        {
            return new UncertaintyMatchResolution(
                expectedCode,
                false,
                "alias_surface",
                string.Join(",", semanticAliases.Take(6)),
                "UncertaintyPresentButAliasMismatch");
        }

        return new UncertaintyMatchResolution(
            expectedCode,
            false,
            string.Empty,
            string.Empty,
            ResolveMissingUncertaintyType(expectedCode));
    }

    private static UncertaintyMatchResolution? FindUncertaintyMatch(
        string expectedCode,
        IReadOnlyList<string> aliases,
        IReadOnlyList<string> semanticAliases,
        IReadOnlyList<ContextPackageUncertainty> uncertainties)
    {
        foreach (var uncertainty in uncertainties)
        {
            if (MatchesExpected(expectedCode, semanticAliases, uncertainty.Code) ||
                MatchesExpected(expectedCode, semanticAliases, uncertainty.Message) ||
                MatchesExpected(expectedCode, semanticAliases, uncertainty.SectionName) ||
                uncertainty.ItemRefs.Any(item => MatchesExpected(expectedCode, semanticAliases, item)) ||
                uncertainty.Metadata.Any(item =>
                    MatchesExpected(expectedCode, semanticAliases, item.Key) ||
                    MatchesExpected(expectedCode, semanticAliases, item.Value)))
            {
                return new UncertaintyMatchResolution(
                    expectedCode,
                    true,
                    "uncertainties",
                    uncertainty.Code);
            }

            if (aliases.Any(alias => string.Equals(uncertainty.Code, alias, StringComparison.OrdinalIgnoreCase)))
            {
                return new UncertaintyMatchResolution(
                    expectedCode,
                    true,
                    "uncertainties",
                    $"{expectedCode}->{uncertainty.Code}");
            }
        }

        return null;
    }

    private static UncertaintyMatchResolution? FindSectionMatch(
        string expectedCode,
        IReadOnlyList<ContextPackageSection>? sections,
        IReadOnlyList<string> semanticAliases,
        params string[] sectionNames)
    {
        if (sections is null)
        {
            return null;
        }

        foreach (var section in sections.Where(section => IsSection(section.Name, sectionNames)))
        {
            var text = JoinSurface(
                section.Name,
                section.Content,
                section.ItemRefs,
                section.SourceRefs);
            if (MatchesExpected(expectedCode, semanticAliases, text))
            {
                return new UncertaintyMatchResolution(expectedCode, true, section.Name, section.Name);
            }
        }

        return null;
    }

    private static UncertaintyMatchResolution? FindOutputMatch(
        string expectedCode,
        IReadOnlyList<string> semanticAliases,
        IReadOnlyList<ContextPackageOutputItem>? items,
        params string[] sectionNames)
    {
        if (items is null)
        {
            return null;
        }

        foreach (var item in items.Where(item => IsSection(item.SectionName, sectionNames)))
        {
            var text = JoinSurface(
                item.SectionName,
                item.Content,
                item.ItemRefs,
                item.SourceRefs);
            if (MatchesExpected(expectedCode, semanticAliases, text))
            {
                return new UncertaintyMatchResolution(expectedCode, true, item.SectionName, item.SectionName);
            }
        }

        return null;
    }

    private static UncertaintyMatchResolution? FindDroppedReasonMatch(
        string expectedCode,
        IReadOnlyList<string> semanticAliases,
        IReadOnlyList<DroppedContextItem> droppedItems)
    {
        foreach (var item in droppedItems)
        {
            var text = JoinSurface(item.ItemId, item.Kind, item.Type, item.Reason, item.SourceRefs, item.Metadata);
            if (MatchesExpected(expectedCode, semanticAliases, text))
            {
                return new UncertaintyMatchResolution(
                    expectedCode,
                    true,
                    "excluded_reason",
                    item.ItemId);
            }
        }

        return null;
    }

    private static UncertaintyMatchResolution? FindRiskFlagMatch(
        string expectedCode,
        IReadOnlyList<string> semanticAliases,
        IReadOnlyList<ContextPackageDecision> selectedItems,
        IReadOnlyList<DroppedContextItem> droppedItems)
    {
        foreach (var item in selectedItems)
        {
            var text = JoinRiskMetadata(item.Metadata);
            if (MatchesExpected(expectedCode, semanticAliases, text))
            {
                return new UncertaintyMatchResolution(expectedCode, true, "risk_flags", item.ItemId);
            }
        }

        foreach (var item in droppedItems)
        {
            var text = JoinRiskMetadata(item.Metadata);
            if (MatchesExpected(expectedCode, semanticAliases, text))
            {
                return new UncertaintyMatchResolution(expectedCode, true, "risk_flags", item.ItemId);
            }
        }

        return null;
    }

    private static bool HasOutputSection(IReadOnlyList<ContextPackageOutputItem>? items, params string[] sectionNames) =>
        items is not null && items.Any(item => IsSection(item.SectionName, sectionNames));

    private static bool HasSection(IReadOnlyList<ContextPackageSection>? sections, params string[] sectionNames) =>
        sections is not null && sections.Any(section => IsSection(section.Name, sectionNames));

    private static bool HasLifecycleExcludedReason(DroppedContextItem item) =>
        item.Reason.Contains("deprecated", StringComparison.OrdinalIgnoreCase) ||
        item.Reason.Contains("rejected", StringComparison.OrdinalIgnoreCase) ||
        item.Reason.Contains("废弃", StringComparison.OrdinalIgnoreCase) ||
        item.Reason.Contains("拒绝", StringComparison.OrdinalIgnoreCase) ||
        item.Reason.Contains("遗留", StringComparison.OrdinalIgnoreCase);

    private static string JoinRiskMetadata(Dictionary<string, string> metadata)
    {
        var riskFields = metadata
            .Where(item =>
                item.Key.Contains("risk", StringComparison.OrdinalIgnoreCase) ||
                item.Key.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
                item.Key.Contains("uncertainty", StringComparison.OrdinalIgnoreCase) ||
                item.Key.Contains("lifecycle", StringComparison.OrdinalIgnoreCase))
            .Select(item => $"{item.Key} {item.Value}");
        return string.Join(" ", riskFields);
    }

    private static string JoinSurface(params object?[] values)
    {
        var sb = new StringBuilder();
        foreach (var value in values)
        {
            switch (value)
            {
                case null:
                    continue;
                case string text:
                    sb.Append(' ').Append(text);
                    break;
                case IEnumerable<string> strings:
                    sb.Append(' ').AppendJoin(' ', strings);
                    break;
                case Dictionary<string, string> metadata:
                    foreach (var item in metadata)
                    {
                        sb.Append(' ').Append(item.Key).Append(' ').Append(item.Value);
                    }
                    break;
            }
        }

        return sb.ToString();
    }

    private static IReadOnlyList<string> ResolveSemanticAliases(string expectedCode)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (SemanticAliases.TryGetValue(expectedCode, out var configured))
        {
            foreach (var alias in configured)
            {
                aliases.Add(alias);
            }
        }

        foreach (var (key, values) in SemanticAliases)
        {
            if (ContainsExpected(key, expectedCode) || ContainsExpected(expectedCode, key))
            {
                aliases.Add(key);
                foreach (var value in values)
                {
                    aliases.Add(value);
                }
            }
        }

        if (expectedCode.Contains("权限", StringComparison.OrdinalIgnoreCase))
        {
            aliases.Add("权限限制");
            aliases.Add("当前环境权限");
        }

        if (expectedCode.Contains("负责人", StringComparison.OrdinalIgnoreCase))
        {
            aliases.Add("处理人");
            aliases.Add("归属");
        }

        if (expectedCode.Contains("复核", StringComparison.OrdinalIgnoreCase))
        {
            aliases.Add("候选");
            aliases.Add("promotion-candidate");
        }

        if (expectedCode.Contains("可多选", StringComparison.OrdinalIgnoreCase))
        {
            aliases.Add("可多选");
        }

        if (expectedCode.Contains("预算", StringComparison.OrdinalIgnoreCase) ||
            expectedCode.Contains("budget", StringComparison.OrdinalIgnoreCase))
        {
            aliases.Add("TokenBudgetPressure");
            aliases.Add("token budget");
        }

        return aliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .ToArray();
    }

    private static bool MatchesExpected(
        string expected,
        IReadOnlyList<string> aliases,
        string text)
    {
        return ContainsExpected(expected, text) ||
               aliases.Any(alias => ContainsExpected(alias, text));
    }

    private static bool SurfaceContainsAnyAlias(
        IReadOnlyList<string> aliases,
        IReadOnlyList<ContextPackageUncertainty> uncertainties,
        IReadOnlyList<ContextPackageDecision> selectedItems,
        IReadOnlyList<DroppedContextItem> droppedItems,
        IReadOnlyList<ContextPackageSection>? sections,
        ContextPackageStandardOutput? output)
    {
        if (aliases.Count == 0)
        {
            return false;
        }

        var surface = JoinSurface(
            uncertainties.Select(item => JoinSurface(item.Code, item.Message, item.SectionName, item.ItemRefs, item.Metadata)),
            selectedItems.Select(item => JoinSurface(item.ItemId, item.Kind, item.Type, item.SectionName, item.Reason, item.SourceRefs, item.Metadata)),
            droppedItems.Select(item => JoinSurface(item.ItemId, item.Kind, item.Type, item.Reason, item.SourceRefs, item.Metadata)),
            sections?.Select(item => JoinSurface(item.Name, item.Content, item.ItemRefs, item.SourceRefs)) ?? Array.Empty<string>(),
            output?.Evidence.Select(item => JoinSurface(item.SectionName, item.Content, item.ItemRefs, item.SourceRefs)) ?? Array.Empty<string>());

        return aliases.Any(alias => ContainsExpected(alias, surface));
    }

    private static string ResolveMissingUncertaintyType(string expectedCode)
    {
        var normalized = NormalizeText(expectedCode);
        if (ContainsExpected("废弃", expectedCode) ||
            ContainsExpected("旧版", expectedCode) ||
            ContainsExpected("deprecated", expectedCode) ||
            ContainsExpected("lifecycle", expectedCode))
        {
            return "MissingLifecycleUncertainty";
        }

        if (ContainsExpected("冲突", expectedCode) ||
            ContainsExpected("矛盾", expectedCode) ||
            ContainsExpected("conflict", expectedCode))
        {
            return "MissingConflictUncertainty";
        }

        if (ContainsExpected("预算", expectedCode) ||
            ContainsExpected("budget", expectedCode) ||
            ContainsExpected("token", expectedCode))
        {
            return "MissingBudgetUncertainty";
        }

        if (ContainsExpected("权限", expectedCode) ||
            ContainsExpected("环境", expectedCode) ||
            ContainsExpected("scope", expectedCode) ||
            ContainsExpected("作用域", expectedCode))
        {
            return "MissingScopeUncertainty";
        }

        if (normalized.Contains("证据", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("样本", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("验证", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("复核", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("确认", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("是否", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("可多选", StringComparison.OrdinalIgnoreCase))
        {
            return "MissingEvidenceUncertainty";
        }

        return "MissingEvidenceUncertainty";
    }

    private static bool ContainsExpected(string expected, string text)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalizedExpected = NormalizeText(expected);
        var normalizedText = NormalizeText(text);
        if (normalizedExpected.Length == 0 || normalizedText.Length == 0)
        {
            return false;
        }

        if (normalizedText.Contains(normalizedExpected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var tokens = ExtractTokens(expected).ToArray();
        if (tokens.Length == 0)
        {
            return false;
        }

        var matched = tokens.Count(token => normalizedText.Contains(token, StringComparison.OrdinalIgnoreCase));
        var required = tokens.Length <= 2
            ? tokens.Length
            : Math.Max(2, (int)Math.Ceiling(tokens.Length * 0.60));
        return matched >= required;
    }

    private static IEnumerable<string> ExtractTokens(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length >= 2)
            {
                tokens.Add(current.ToString());
            }

            current.Clear();
        }

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || IsCjk(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                Flush();
            }
        }

        Flush();

        var normalized = NormalizeText(text);
        if (normalized.Any(IsCjk))
        {
            for (var i = 0; i < normalized.Length - 1; i++)
            {
                var token = normalized.Substring(i, 2);
                if (!token.Any(IsWeakChineseChar))
                {
                    tokens.Add(token);
                }
            }
        }

        return tokens.Where(token => token.Length >= 2);
    }

    private static string NormalizeText(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || IsCjk(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }

    private static bool IsCjk(char ch) =>
        ch >= '\u4e00' && ch <= '\u9fff';

    private static bool IsWeakChineseChar(char ch) =>
        ch is '的' or '了' or '在' or '是' or '和' or '与' or '或' or '而' or '及'
            or '并' or '不' or '无' or '应' or '需' or '须' or '得' or '要' or '能'
            or '否' or '时' or '后' or '前' or '本' or '次';

    private static bool IsSection(string actual, params string[] expected)
    {
        var normalized = NormalizeSection(actual);
        return expected.Any(item =>
            string.Equals(normalized, NormalizeSection(item), StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeSection(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim()
                .Replace("-", "_", StringComparison.Ordinal)
                .ToLowerInvariant();
}

public sealed record UncertaintyMatchResolution(
    string ExpectedCode,
    bool Satisfied,
    string Source,
    string Detail,
    string FailureType = "");
