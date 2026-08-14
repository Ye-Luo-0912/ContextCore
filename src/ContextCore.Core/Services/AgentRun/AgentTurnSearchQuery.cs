using ContextCore.Abstractions;
using ContextCore.Core.Services.Retrieval;

namespace ContextCore.Core.Services.AgentRunRuntime;

// 本轮检索问句：计划里的查询分开列出，Lexical 按条搜。
// 拼成一句只用于诊断 QueryText。搜到不等于进工作集。

internal static class AgentTurnSearchQuery
{
    public const int MaxObservationSnippetChars = ObservationQueryText.MaxSnippetChars;

    /// <summary>
    /// 收集本轮要交给 Lexical 的查询。计划条目优先，成功观察补上新实体词，有界。
    /// </summary>
    public static IReadOnlyList<string> CollectQueries(
        IReadOnlyList<AgentRetrievalQuery>? planned,
        string? fallback,
        IReadOnlyList<ToolObservation>? observations)
    {
        var list = new List<string>(DefaultAgentRetrievalQueryPlanner.MaxControlledQueries);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? text)
        {
            var trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length == 0
                || !seen.Add(trimmed)
                || list.Count >= DefaultAgentRetrievalQueryPlanner.MaxControlledQueries)
            {
                return;
            }

            list.Add(trimmed);
        }

        if (planned is not null)
        {
            foreach (var query in planned)
            {
                TryAdd(query.Text);
            }
        }

        if (list.Count == 0)
        {
            TryAdd(fallback);
        }

        foreach (var distinctive in ObservationQueryText.DistinctiveQueries(string.Join(" ", list), observations))
        {
            TryAdd(distinctive);
        }

        return list;
    }

    /// <summary>
    /// 把受控查询集并成一条 QueryText。没有新词的条目跳过。
    /// </summary>
    public static string MergeQueries(IReadOnlyList<AgentRetrievalQuery>? queries, string? fallback)
        => MergeQueries(queries is null ? null : queries.Select(query => query.Text).ToArray(), fallback);

    public static string MergeQueries(IReadOnlyList<string>? texts, string? fallback)
    {
        if (texts is null || texts.Count == 0)
        {
            return (fallback ?? string.Empty).Trim();
        }

        var text = string.Empty;
        foreach (var candidate in texts)
        {
            var extra = (candidate ?? string.Empty).Trim();
            if (extra.Length == 0 || !AddsSearchTerms(text, extra))
            {
                continue;
            }

            text = text.Length == 0 ? extra : text + " " + extra;
        }

        return text.Length == 0 ? (fallback ?? string.Empty).Trim() : text;
    }

    /// <summary>
    /// 拼出诊断用 QueryText。成功观察只并入新实体词。
    /// </summary>
    public static string Compose(string? baseQuery, IReadOnlyList<ToolObservation>? observations)
        => MergeQueries(CollectQueries(null, baseQuery, observations), baseQuery);

    /// <summary>
    /// 从上一轮决策抽出规划器用的诊断。没有上一轮则空列表。
    /// </summary>
    public static IReadOnlyList<AgentRetrievalDiagnostic> DiagnosticsFrom(
        ContextDecisionExecutionResult? last)
    {
        if (last is null)
        {
            return Array.Empty<AgentRetrievalDiagnostic>();
        }

        var outcome = last.Decision.Outcome;
        var selected = last.Decision.SelectedEnvelopes;
        double? highest = null;
        if (selected.Count > 0)
        {
            highest = selected.Max(envelope => envelope.Utility.FinalScore);
        }

        var budget = outcome.TokenBudget;
        var exceeded = outcome.BudgetExceededCount > 0
            || (budget > 0 && outcome.EffectiveTokens > budget);

        return new[]
        {
            new AgentRetrievalDiagnostic
            {
                QueryText = last.NormalizedRequest?.QueryText ?? string.Empty,
                HitsReturned = outcome.SelectedCount,
                HighestScore = highest,
                BudgetExceeded = exceeded
            }
        };
    }

    private static bool AddsSearchTerms(string baseQuery, string snippet)
    {
        if (string.IsNullOrEmpty(baseQuery))
        {
            return true;
        }

        if (baseQuery.Contains(snippet, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var term in EnumerateTerms(snippet))
        {
            if (!baseQuery.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateTerms(string text)
    {
        var current = new List<char>();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || ch is >= '\u4e00' and <= '\u9fff')
            {
                current.Add(ch);
                continue;
            }

            if (current.Count >= 2)
            {
                yield return new string(current.ToArray());
            }

            current.Clear();
        }

        if (current.Count >= 2)
        {
            yield return new string(current.ToArray());
        }
    }

    /// <summary>
    /// 用工具观察当外部证据：有观察才算有效信号，质量是成功率。
    /// 没有观察时不能用打分器分数冒充准不准。
    /// </summary>
    public static (bool Effective, double Quality, double Confidence) ToolEvidence(
        IReadOnlyList<ToolObservation>? observations)
    {
        if (observations is null || observations.Count == 0)
        {
            return (false, 0.0, 0.5);
        }

        // 与规划同窗口：只用最近若干条观察算成功率，整个 Run 的古代失败不打没质量。
        var windowStart = Math.Max(0, observations.Count - ObservationQueryText.MaxObservationWindow);
        var succeeded = 0;
        for (var i = observations.Count - 1; i >= windowStart; i--)
        {
            if (observations[i] is { Succeeded: true })
            {
                succeeded++;
            }
        }

        var windowedCount = observations.Count - windowStart;
        return (true, succeeded / (double)windowedCount, 1.0);
    }
}
