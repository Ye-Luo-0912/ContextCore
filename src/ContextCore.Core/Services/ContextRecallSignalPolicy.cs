using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 多层召回信号策略。
/// 该类集中管理“哪些信号可以参与相关性判断”和“长期稳定记忆如何注入”，避免 Builder 直接沉淀规则细节。
/// </summary>
internal static class ContextRecallSignalPolicy
{
    private static readonly string[] LongTermMemoryKeywords =
    [
        "preference",
        "偏好",
        "project",
        "项目",
        "background",
        "背景",
        "style",
        "风格",
        "safety",
        "security",
        "安全",
        "密钥",
        "secret",
        "boundary",
        "边界",
        "principle",
        "原则",
        "constraint",
        "约束",
        "rule",
        "规则",
        "world",
        "世界观",
        "设定",
        "performance",
        "性能",
        "test",
        "测试",
        "risk",
        "风险"
    ];

    public static bool IsSpecificRecallAnchor(ContextAnchor anchor)
    {
        // workspace / collection 表达检索边界，不能直接作为内容相关性分数。
        return !string.Equals(anchor.Source, "request.workspace", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(anchor.Source, "request.collection", StringComparison.OrdinalIgnoreCase);
    }

    public static StableMemoryInjectionScore ScoreStableMemoryForInjection(
        ContextMemoryItem item,
        IReadOnlyList<ContextAnchor> anchors,
        IReadOnlyList<string> workingSignals,
        string searchText)
    {
        var score = item.Importance * 8 + item.Confidence * 5;
        if (IsLongTermMemoryCategory(searchText))
        {
            score += 8;
        }

        var hasCurrentSignal = false;
        foreach (var anchor in anchors)
        {
            if (!IsSpecificRecallAnchor(anchor))
            {
                continue;
            }

            if (ContainsSignal(searchText, anchor.Name)
                || anchor.Aliases.Any(alias => ContainsSignal(searchText, alias)))
            {
                var isQueryAnchor = string.Equals(anchor.Source, "request.query", StringComparison.OrdinalIgnoreCase);
                score += anchor.Weight * (isQueryAnchor ? 13 : 9);
                hasCurrentSignal = true;
            }
        }

        var workingSignalScore = 0.0;
        foreach (var signal in workingSignals)
        {
            if (ContainsSignal(searchText, signal))
            {
                workingSignalScore += 6;
                hasCurrentSignal = true;
            }
        }

        // 工作记忆信号只做辅助，避免长期层凭借通用工作信号覆盖当前 query 的直接命中。
        score += Math.Min(workingSignalScore, 18.0);

        return new StableMemoryInjectionScore(score, hasCurrentSignal);
    }

    public static bool IsLongTermMemoryCategory(string searchText)
    {
        foreach (var keyword in LongTermMemoryKeywords)
        {
            if (ContainsSignal(searchText, keyword))
            {
                return true;
            }
        }

        return false;
    }

    public static IReadOnlyList<string> CreateWorkingMemorySignals(IReadOnlyList<ContextMemoryItem> workingMemory)
    {
        var signals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var memory in workingMemory.Take(8))
        {
            foreach (var tag in memory.Tags.Take(8))
            {
                AddSignal(signals, tag);
            }

            foreach (var value in memory.Metadata.Values.Take(12))
            {
                AddSignal(signals, value);
            }

            foreach (var term in SplitSignalTerms(memory.Content).Take(12))
            {
                AddSignal(signals, term);
            }

            if (signals.Count >= 64)
            {
                break;
            }
        }

        return signals.ToArray();
    }

    private static IEnumerable<string> SplitSignalTerms(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var buffer = new List<char>();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || IsCjk(ch))
            {
                buffer.Add(ch);
                continue;
            }

            foreach (var term in FlushSignalTerm(buffer))
            {
                yield return term;
            }
        }

        foreach (var term in FlushSignalTerm(buffer))
        {
            yield return term;
        }
    }

    private static IEnumerable<string> FlushSignalTerm(List<char> buffer)
    {
        if (buffer.Count == 0)
        {
            yield break;
        }

        var term = new string(buffer.ToArray()).Trim();
        buffer.Clear();
        if (term.Length >= 2)
        {
            yield return term;
        }
    }

    private static void AddSignal(ISet<string> signals, string? signal)
    {
        if (IsUsefulStableMemorySignal(signal))
        {
            signals.Add(signal!.Trim());
        }
    }

    private static bool IsUsefulStableMemorySignal(string? signal)
    {
        if (string.IsNullOrWhiteSpace(signal))
        {
            return false;
        }

        var value = signal.Trim();
        if (value.Length < 2
            || value.StartsWith("source:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("relation:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !value.Equals("memory", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("task-state", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("active", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("completed", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("blocked", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsSignal(string searchText, string? signal)
    {
        return !string.IsNullOrWhiteSpace(signal)
            && searchText.Contains(signal.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCjk(char ch)
    {
        return ch is >= '\u4e00' and <= '\u9fff';
    }
}

internal readonly record struct StableMemoryInjectionScore(double Score, bool HasCurrentSignal);
