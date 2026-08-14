using System.Text.RegularExpressions;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.Retrieval;

// ===========================================================================
// DefaultAgentRetrievalQueryPlanner — 受控检索查询规划器（默认实现）
//
// 目标：
// 将 AgentRetrievalPlannerInput 解析为受控的 AgentRetrievalPlan。
// 受控优先：查询数 / 必需-排除 ID / 图种子 / Token 预算全部有硬上限，
// 绝不随对话膨胀为自由检索。
//
// 算法：
// 1. 必需召回 ID：从（原始任务 + 最新意图 + 未解决目标）提取显式
// id:/ref:/uuid: 引用（正则），去重后封顶 MaxRequiredIds。
// 2. 排除 ID：从失败的 Tool 观察（Succeeded=false）的 Error/Result 中
// 提取 ID 引用（"确认不存在"的实体），封顶 MaxExcludedIds。
// 3. 图种子：优先取引号 / 书名号内显式实体锚点，不足时取长词元
// （长度 2..32、非停用词、非纯数字，按长度降序），封顶 MaxGraphSeeds。
// 4. 受控查询集：原始任务（混合）→ 意图（若与任务不同）→ 上一轮 0 命中时
// 任务/意图里未单独成问句的实体样词（Keyword，不加向量）→ 未解决目标
// → 成功工具观察抽出的新实体词 → 成功观察里的显式 id:/ref:/uuid: 引用（Keyword）
// → 图种子文本（有空位且未被现有问句覆盖才加），封顶 MaxControlledQueries 条。
// 观察优先占位：问句跟外部结果走，不用整段结果当问句。
// 图种子文本只是额外 Keyword 查询，不是关系图上的节点 ID。
// 5. Token 预算：TurnBudget.Remaining × 1024，钳制 [512, 8192]；
// 上一轮诊断 BudgetExceeded=true 时减半（受控回退）。
//
// 设计原则：
// - 纯内存、确定性、幂等：相同输入产生相同计划（无随机性、无外部状态）。
// - 不调用任何存储 / 检索执行器；只输出计划，执行由调用方驱动。
// - 空输入仍产出受控计划（空查询集 + 最小预算），绝不抛异常。
// ===========================================================================

/// <summary>
/// 受控检索查询规划器默认实现。
/// </summary>
public sealed class DefaultAgentRetrievalQueryPlanner : IAgentRetrievalQueryPlanner
{
    /// <summary>受控查询集上限。</summary>
    public const int MaxControlledQueries = 4;

    /// <summary>必需召回 ID 上限。</summary>
    public const int MaxRequiredIds = 8;

    /// <summary>排除 ID 上限。</summary>
    public const int MaxExcludedIds = 8;

    /// <summary>图种子上限。</summary>
    public const int MaxGraphSeeds = 6;

    /// <summary>最小 Token 预算。</summary>
    public const int MinTokenBudget = 512;

    /// <summary>最大 Token 预算。</summary>
    public const int MaxTokenBudget = 8192;

    /// <summary>每个剩余 Turn 分配的 Token 数。</summary>
    private const int TokensPerRemainingTurn = 1024;

    /// <summary>图种子词元最小长度。</summary>
    private const int GraphSeedMinLength = 2;

    /// <summary>图种子词元最大长度。</summary>
    private const int GraphSeedMaxLength = 32;

    /// <summary>显式 ID / ref / uuid 引用模式（如 id:abc-123 / ref=XYZ_01）。</summary>
    private static readonly Regex IdReferenceRegex = new(
        @"(?i)\b(?:id|ref|uuid)\s*[:=]\s*([A-Za-z0-9_\-]{4,64})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>常用停用词（中英文），不作为图种子。</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "的", "了", "是", "在", "我", "有", "和", "就", "不", "人", "都", "一", "一个", "这个", "那个", "我们", "你们", "他们",
        "the", "a", "an", "of", "to", "in", "on", "for", "with", "and", "or", "is", "are", "was", "were", "be", "it", "this", "that"
    };

    /// <inheritdoc />
    public AgentRetrievalPlan Plan(AgentRetrievalPlannerInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        var task = (input.OriginalTask ?? string.Empty).Trim();
        var intent = (input.LatestAssistantIntent ?? string.Empty).Trim();
        var goals = input.UnresolvedGoals ?? Array.Empty<string>();
        var diagnostics = input.PreviousRetrievalDiagnostics ?? Array.Empty<AgentRetrievalDiagnostic>();
        var observations = input.ToolObservations ?? Array.Empty<ToolObservation>();

        // 1. 必需召回 ID（任务 + 意图 + 目标中的显式引用，去重封顶）
        var requiredIds = ExtractIdReferences(
            Concat(task, intent, goals), MaxRequiredIds);

        // 2. 排除 ID（失败 Tool 观察中确认不存在的实体，去重封顶）
        var excludedIds = ExtractExcludedIds(observations, MaxExcludedIds);

        // 3. 图种子（显式锚点优先 + 长词元补充，封顶）
        var graphSeeds = ExtractGraphSeeds(task, intent, goals, MaxGraphSeeds);

        // 4. 受控查询集（有界；上一轮 0 命中时先拆实体词，成功观察的实体词优先于图种子）
        var emptyRecall = diagnostics.Any(d => d is { HitsReturned: 0 });
        var queries = BuildControlledQueries(task, intent, goals, graphSeeds, observations, emptyRecall);

        // 5. Token 预算（Turn 预算推导 + 诊断回退；任务为空时只给最小预算）
        var tokenBudget = string.IsNullOrWhiteSpace(task)
            ? MinTokenBudget
            : ComputeTokenBudget(input.TurnBudget, diagnostics);

        // 6. TopK：候选召回上限（由 Token 预算确定性推导并钳制——检索不再以
        //    "不设 TopK"运行；Decision Runtime 原生消费计划值）。
        var topK = string.IsNullOrWhiteSpace(task)
            ? 4
            : Math.Clamp(tokenBudget / 512, 4, 20);

        // 7. 计划说明（中文，供审计与调试）
        var reason = BuildReason(
            queries.Count, requiredIds.Count, excludedIds.Count,
            graphSeeds.Count, tokenBudget, diagnostics, string.IsNullOrWhiteSpace(task));

        return new AgentRetrievalPlan
        {
            ControlledQueries = queries,
            RequiredIds = requiredIds,
            ExcludedIds = excludedIds,
            GraphSeeds = graphSeeds,
            TokenBudget = tokenBudget,
            TopK = topK,
            Reason = reason
        };
    }

    // ── 受控查询集 ───────────────────────────────────────────────────────────

    private static List<AgentRetrievalQuery> BuildControlledQueries(
        string task,
        string intent,
        IReadOnlyList<string> goals,
        IReadOnlyList<string> graphSeeds,
        IReadOnlyList<ToolObservation> observations,
        bool emptyRecall)
    {
        var queries = new List<AgentRetrievalQuery>(MaxControlledQueries);

        if (!string.IsNullOrWhiteSpace(task))
        {
            queries.Add(new AgentRetrievalQuery
            {
                Text = task,
                Type = AgentRetrievalQueryType.Hybrid,
                Weight = 1.0,
                Reason = "原始任务"
            });
        }

        if (!string.IsNullOrWhiteSpace(intent)
            && !string.Equals(intent, task, StringComparison.Ordinal))
        {
            queries.Add(new AgentRetrievalQuery
            {
                Text = intent,
                Type = AgentRetrievalQueryType.Keyword,
                Weight = 0.8,
                Reason = "最新 Assistant 意图"
            });
        }

        // 上一轮 0 命中时：任务/意图整体问句没搜到东西，把其中还没单独成问句的
        // 实体样词逐条加成 Keyword 再搜（不调用向量；名额仍受上限约束）。
        if (emptyRecall)
        {
            TryAddEmptyRecallEntityQueries(queries, task, intent);
        }

        // 成功工具观察的实体词优先于找回词占名额（最新工具结果先写进问句）。
        TryAddObservationQueries(queries, observations);

        // 未解决目标（上一轮被分配器裁掉的条目）逐条加成 Keyword 查询：
        // 不拼成一句、不标 Vector——默认没有向量，拼句会再次撞词元上限、标题对不上。
        // 与观察实体词相同的覆盖检查，被已有问句覆盖的目标不再占名额。
        var goalsCovered = string.Join(" ", queries.Select(query => query.Text));
        foreach (var goal in goals)
        {
            if (queries.Count >= MaxControlledQueries)
            {
                break;
            }
            var goalText = (goal ?? string.Empty).Trim();
            if (goalText.Length == 0
                || string.Equals(goalText, task, StringComparison.Ordinal)
                || IsCoveredByQueries(goalText, goalsCovered))
            {
                continue;
            }
            queries.Add(new AgentRetrievalQuery
            {
                Text = goalText,
                Type = AgentRetrievalQueryType.Keyword,
                Weight = 0.7,
                Reason = "未解决目标"
            });
            goalsCovered = goalsCovered + " " + goalText;
        }

        var coveredText = string.Join(" ", queries.Select(query => query.Text));
        foreach (var seed in graphSeeds)
        {
            if (queries.Count >= MaxControlledQueries)
            {
                break;
            }
            if (IsCoveredByQueries(seed, coveredText))
            {
                continue;
            }
            queries.Add(new AgentRetrievalQuery
            {
                Text = seed,
                Type = AgentRetrievalQueryType.Keyword,
                Weight = 0.5,
                Reason = "图种子锚定查询"
            });
        }

        return queries;
    }

    private static void TryAddObservationQueries(
        List<AgentRetrievalQuery> queries,
        IReadOnlyList<ToolObservation> observations)
    {
        var covered = string.Join(" ", queries.Select(query => query.Text));
        foreach (var distinctive in ObservationQueryText.DistinctiveQueries(covered, observations))
        {
            if (queries.Count >= MaxControlledQueries)
            {
                return;
            }

            queries.Add(new AgentRetrievalQuery
            {
                Text = distinctive,
                Type = AgentRetrievalQueryType.Keyword,
                Weight = 1.0,
                Reason = "成功工具观察"
            });
        }

        // 成功观察里的显式 id:/ref:/uuid: 引用：工具在说这个 ID 存在，
        // 按条加成 Keyword 问句（上限），不进 RequiredIds。
        TryAddSuccessfulIdQueries(queries, observations);
    }

    // 成功工具观察中的 ID 引用（如 id:keep-1）说明该实体确实存在：
    // 逐条加成 Keyword 问句，方便它不在工作集时靠搜索找回。
    // 失败观察的 ID 走排除路径（ExtractExcludedIds），这里只看成功观察；
    // 与观察实体词同一最近窗口、最新优先；已单独成问句的 ID 不重复加成。
    private static void TryAddSuccessfulIdQueries(
        List<AgentRetrievalQuery> queries,
        IReadOnlyList<ToolObservation> observations)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var windowStart = Math.Max(0, observations.Count - ObservationQueryText.MaxObservationWindow);
        for (var i = observations.Count - 1; i >= windowStart; i--)
        {
            var observation = observations[i];
            if (observation is null || !observation.Succeeded)
            {
                continue;
            }
            var text = string.Concat(observation.Result, " ", observation.Error);
            foreach (Match match in IdReferenceRegex.Matches(text ?? string.Empty))
            {
                var id = match.Groups[1].Value;
                if (!seen.Add(id)
                    || queries.Any(query => string.Equals(query.Text, id, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                if (queries.Count >= MaxControlledQueries)
                {
                    return;
                }
                queries.Add(new AgentRetrievalQuery
                {
                    Text = id,
                    Type = AgentRetrievalQueryType.Keyword,
                    Weight = 1.0,
                    Reason = "成功工具观察 ID"
                });
            }
        }
    }

    // 空召回恢复：上一轮 0 命中时，任务/意图整体没搜到东西，把其中还没单独成问句的
    // 实体样词（带数字/连字符/下划线，与观察实体词同一词元规则）逐条加成 Keyword 再搜。
    // 不调用向量；已是单独问句的词不再重复加成。
    private static void TryAddEmptyRecallEntityQueries(
        List<AgentRetrievalQuery> queries, string task, string intent)
    {
        var combined = string.IsNullOrWhiteSpace(intent)
            ? task
            : string.Concat(task, " ", intent);
        foreach (var term in SplitTerms(combined))
        {
            if (queries.Count >= MaxControlledQueries)
            {
                return;
            }
            if (term.Length < GraphSeedMinLength
                || StopWords.Contains(term)
                || !ObservationQueryText.LooksLikeEntity(term)
                || queries.Any(query => string.Equals(query.Text, term, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            queries.Add(new AgentRetrievalQuery
            {
                Text = term,
                Type = AgentRetrievalQueryType.Keyword,
                Weight = 0.7,
                Reason = "空召回实体词"
            });
        }
    }

    /// <summary>
    /// 图种子是否已被现有查询覆盖：整段包含，或按词元规则每个信息词都已出现。
    /// 被覆盖的种子不再占查询名额（任务套话不重复搜），名额留给新实体。
    /// </summary>
    private static bool IsCoveredByQueries(string seed, string coveredText)
    {
        if (string.IsNullOrWhiteSpace(seed) || string.IsNullOrWhiteSpace(coveredText))
        {
            return false;
        }

        if (coveredText.Contains(seed, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 词元规则：种子的每个信息词（非停用词、长度达标）都已整段出现，
        // 与观察问句的覆盖判断一致。
        var informativeTermCount = 0;
        foreach (var term in SplitTerms(seed))
        {
            if (term.Length < GraphSeedMinLength || StopWords.Contains(term))
            {
                continue;
            }
            informativeTermCount++;
            if (!coveredText.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return informativeTermCount > 0;
    }

    private static IEnumerable<string> SplitTerms(string text)
        => Regex.Split(text, @"[^\p{L}\p{N}_\-]+").Where(term => term.Length > 0);

    // ── ID 提取 ──────────────────────────────────────────────────────────────

    private static List<string> ExtractIdReferences(string text, int max)
    {
        var result = new List<string>(max);
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in IdReferenceRegex.Matches(text))
        {
            var id = match.Groups[1].Value;
            if (seen.Add(id))
            {
                result.Add(id);
                if (result.Count >= max)
                {
                    break;
                }
            }
        }
        return result;
    }

    private static List<string> ExtractExcludedIds(IReadOnlyList<ToolObservation> observations, int max)
    {
        var result = new List<string>(max);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // 只取最近 MaxObservationWindow 条且从最新失败开始：新的 id:missing 先占排除名额，
        // 窗口外的旧失败不再进排除列表。
        var windowStart = Math.Max(0, observations.Count - ObservationQueryText.MaxObservationWindow);
        for (var i = observations.Count - 1; i >= windowStart; i--)
        {
            var observation = observations[i];
            if (observation is null || observation.Succeeded)
            {
                continue;
            }
            var text = string.Concat(observation.Error, " ", observation.Result);
            foreach (Match match in IdReferenceRegex.Matches(text ?? string.Empty))
            {
                var id = match.Groups[1].Value;
                if (seen.Add(id))
                {
                    result.Add(id);
                    if (result.Count >= max)
                    {
                        return result;
                    }
                }
            }
        }
        return result;
    }

    // ── 图种子提取 ───────────────────────────────────────────────────────────

    private static List<string> ExtractGraphSeeds(
        string task, string intent, IReadOnlyList<string> goals, int max)
    {
        var combined = Concat(task, intent, goals);
        var result = new List<string>(max);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // 显式锚点优先：引号 / 书名号 / 尖括号内的实体（强实体信号）
        foreach (Match match in Regex.Matches(combined, @"[“‘「『《〈【]([^”’」』》〉】]{1,64})[”’」』》〉】]", RegexOptions.CultureInvariant))
        {
            var entity = match.Groups[1].Value.Trim();
            if (entity.Length >= 1 && seen.Add(entity))
            {
                result.Add(entity);
                if (result.Count >= max)
                {
                    return result;
                }
            }
        }

        // 长词元补充：按长度降序（长词元更可能是实体名），跳过停用词 / 纯数字
        var tokens = Regex.Split(combined, @"[^\p{L}\p{N}_\-]+")
            .Where(t => t.Length >= GraphSeedMinLength && t.Length <= GraphSeedMaxLength)
            .Where(t => !StopWords.Contains(t))
            .Where(t => !t.All(char.IsDigit))
            .OrderByDescending(t => t.Length)
            .ThenBy(t => t, StringComparer.Ordinal)
            .ToList();

        foreach (var token in tokens)
        {
            if (seen.Add(token))
            {
                result.Add(token);
                if (result.Count >= max)
                {
                    break;
                }
            }
        }

        return result;
    }

    // ── Token 预算 ───────────────────────────────────────────────────────────

    private static int ComputeTokenBudget(
        AgentTurnBudget? turnBudget, IReadOnlyList<AgentRetrievalDiagnostic> diagnostics)
    {
        // 基准：剩余轮次 × 每轮 Token；无 TurnBudget 时用默认值
        var remaining = turnBudget?.Remaining ?? 4;
        var budget = remaining > 0
            ? Math.Clamp(remaining * TokensPerRemainingTurn, MinTokenBudget, MaxTokenBudget)
            : MinTokenBudget;

        // 受控回退：上一轮预算超限 → 减半（不低于最小预算），避免反复撞墙
        var exceeded = diagnostics.Any(d => d is { BudgetExceeded: true });
        if (exceeded)
        {
            budget = Math.Max(MinTokenBudget, budget / 2);
        }

        return budget;
    }

    // ── 计划说明 ─────────────────────────────────────────────────────────────

    private static string BuildReason(
        int queryCount, int requiredCount, int excludedCount, int seedCount,
        int tokenBudget, IReadOnlyList<AgentRetrievalDiagnostic> diagnostics, bool taskEmpty)
    {
        var sb = new System.Text.StringBuilder();
        if (taskEmpty)
        {
            sb.Append("原始任务为空，仅产出最小受控计划；");
        }
        sb.Append($"受控计划：{queryCount} 条查询、{requiredCount} 个必需 ID、{excludedCount} 个排除 ID、{seedCount} 个图种子、Token 预算 {tokenBudget}。");

        if (requiredCount > 0)
        {
            sb.Append("必需 ID 来自任务/意图中的显式引用；");
        }
        if (excludedCount > 0)
        {
            sb.Append("排除 ID 来自失败 Tool 观察；");
        }
        if (diagnostics.Any(d => d is { BudgetExceeded: true }))
        {
            sb.Append("上一轮预算超限，本轮已回退减半。");
        }
        if (diagnostics.Any(d => d is { HitsReturned: 0 }))
        {
            sb.Append("上一轮 0 命中，本轮用尚未覆盖的实体词再搜。");
        }
        return sb.ToString().TrimEnd();
    }

    // ── 文本拼接 ─────────────────────────────────────────────────────────────

    private static string Concat(string task, string intent, IReadOnlyList<string> goals)
    {
        var parts = new List<string>(goals.Count + 2);
        if (!string.IsNullOrWhiteSpace(task)) parts.Add(task);
        if (!string.IsNullOrWhiteSpace(intent)) parts.Add(intent);
        foreach (var goal in goals)
        {
            if (!string.IsNullOrWhiteSpace(goal)) parts.Add(goal);
        }
        return string.Join(" ", parts);
    }
}
