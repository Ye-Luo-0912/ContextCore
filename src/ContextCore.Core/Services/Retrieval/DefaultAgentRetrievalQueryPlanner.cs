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

    /// <summary>短语锚定查询上限（引号/书名号内的整体短语）。</summary>
    public const int MaxPhraseAnchorQueries = 2;

    /// <summary>显式别名查询上限（文本中成对出现的同指）。</summary>
    public const int MaxAliasQueries = 2;

    /// <summary>时效限定查询上限（生命周期/时间限定短语）。</summary>
    public const int MaxTimeQualifierQueries = 1;

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

    /// <summary>引号 / 书名号内的整体短语锚点（强实体信号，按整体成问句而非拆词元）。</summary>
    private static readonly Regex PhraseAnchorRegex = new(
        @"[“‘「『《〈【]([^”’」』》〉】]{2,48})[”’」』》〉】]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>括号内同指（A（B）/A(B)）：括号内容是与前文同指的显式别名证据。</summary>
    private static readonly Regex ParenthesizedAliasRegex = new(
        @"[\p{L}\p{N}_\-]{2,40}[（(]([^（）()]{2,24})[）)]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>显式同指标记（A 又称 B / A 别名 B 等）：标记后紧随的名字是别名证据。</summary>
    private static readonly Regex AliasMarkerRegex = new(
        @"(?:又称|别名|也就是|亦作|也称|简称)\s*([\p{L}\p{N}_\-]{2,40})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>生命周期 / 时间限定短语：查询里出现时单独保留成低权重问句，防止被长任务词元预算挤掉。</summary>
    private static readonly Regex TimeQualifierRegex = new(
        @"当前生效|现行版本|当前版本|最新版本|最近一次|上一版|已废弃|已过期|过期|作数|新版|旧版|现行|正在推进",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>单个 ASCII 词元（字母/数字/下划线/连字符，无空格）：匹配器原生提取为独立词项。</summary>
    private static readonly Regex SingleAsciiTokenRegex = new(
        @"^[A-Za-z0-9_\-]+$",
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

        // 短语锚定：引号/书名号内的整体短语单独成问句，不被词元化打散。
        // 显式用户标记是强信号，优先级高于未解决目标与图种子。
        TryAddPhraseAnchorQueries(queries, task, intent, goals, observations);

        // 显式别名：只从文本中成对出现的同指（A（B）/A 即 B）提取，不做全局同义词膨胀。
        TryAddAliasQueries(queries, task, intent, goals, observations);

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

        // 时效限定：任务/意图/目标里的生命周期/时间限定短语单独成低权重问句。
        // 限定词通常在长任务里被词元预算挤掉，独立保留后含相同限定词的文档标题仍可命中。
        TryAddTimeQualifierQueries(queries, task, intent, goals);

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

    // 短语锚定查询：引号/书名号内的整体短语单独成 Keyword 问句，不被词元化打散。
    // 只来自输入文本的显式引号标记（任务/意图/目标/成功观察），不是无证据的膨胀；
    // 短语在长任务里可能被匹配器词元预算挤掉，独立成短问句保证短语本身可命中，
    // 因此多字/多词短语不做"已被任务文本覆盖"的跳过（那会取消它的保护作用）。
    // 例外：单个 ASCII 词元（如 AlphaProtocol）会被匹配器原生提取为独立词项，
    // 任务问句里不会因词元化打散，单独成问句是冗余的——若已出现在任务文本中
    // 则不再占查询名额。只按完全重复去重，上限受 MaxPhraseAnchorQueries 约束。
    private static void TryAddPhraseAnchorQueries(
        List<AgentRetrievalQuery> queries,
        string task,
        string intent,
        IReadOnlyList<string> goals,
        IReadOnlyList<ToolObservation> observations)
    {
        var combined = Concat(task, intent, goals);
        if (observations is not null)
        {
            foreach (var observation in observations)
            {
                if (observation is { Succeeded: true } && !string.IsNullOrWhiteSpace(observation.Result))
                {
                    combined = string.Concat(combined, " ", observation.Result);
                }
            }
        }

        var added = 0;
        foreach (Match match in PhraseAnchorRegex.Matches(combined))
        {
            if (queries.Count >= MaxControlledQueries || added >= MaxPhraseAnchorQueries)
            {
                return;
            }
            var phrase = match.Groups[1].Value.Trim();
            if (phrase.Length < 2
                || queries.Any(query => string.Equals(query.Text, phrase, StringComparison.OrdinalIgnoreCase))
                || IsSingleAsciiTokenCoveredByTask(phrase, task))
            {
                continue;
            }
            queries.Add(new AgentRetrievalQuery
            {
                Text = phrase,
                Type = AgentRetrievalQueryType.Keyword,
                Weight = 0.9,
                Reason = "短语锚定"
            });
            added++;
        }
    }

    /// <summary>
    /// 判断锚定短语是否是单个 ASCII 词元且已出现在任务文本中。
    /// 单个 ASCII 词元会被匹配器原生提取为独立词项，无需短语问句保护；
    /// 已出现在任务文本中时单独成问句是冗余的，不再占查询名额。
    /// </summary>
    private static bool IsSingleAsciiTokenCoveredByTask(string phrase, string task)
    {
        if (task.Length == 0 || phrase.Length == 0)
        {
            return false;
        }
        if (!SingleAsciiTokenRegex.IsMatch(phrase))
        {
            return false;
        }
        return task.Contains(phrase, StringComparison.OrdinalIgnoreCase);
    }

    // 显式别名查询：只从文本中成对出现的同指（A（B）/A 又称 B）提取别名，按证据加成；
    // 不做全局同义词膨胀——文本里没有显式同指标记就不产生任何别名查询。
    private static void TryAddAliasQueries(
        List<AgentRetrievalQuery> queries,
        string task,
        string intent,
        IReadOnlyList<string> goals,
        IReadOnlyList<ToolObservation> observations)
    {
        var combined = Concat(task, intent, goals);
        if (observations is not null)
        {
            foreach (var observation in observations)
            {
                if (observation is { Succeeded: true } && !string.IsNullOrWhiteSpace(observation.Result))
                {
                    combined = string.Concat(combined, " ", observation.Result);
                }
            }
        }

        var added = 0;
        foreach (var alias in ExtractExplicitAliases(combined))
        {
            if (queries.Count >= MaxControlledQueries || added >= MaxAliasQueries)
            {
                return;
            }
            if (queries.Any(query => string.Equals(query.Text, alias, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            queries.Add(new AgentRetrievalQuery
            {
                Text = alias,
                Type = AgentRetrievalQueryType.Keyword,
                Weight = 0.85,
                Reason = "显式别名"
            });
            added++;
        }
    }

    private static IEnumerable<string> ExtractExplicitAliases(string text)
    {
        // 括号同指：A（B）里的 B 是 A 的显式别名。括号内容必须是短技术名
        // （含拉丁字母/数字）——纯中文括号注释（详见下文）不是别名，不产生查询。
        foreach (Match match in ParenthesizedAliasRegex.Matches(text))
        {
            var alias = match.Groups[1].Value.Trim();
            if (IsValidAlias(alias))
            {
                yield return alias;
            }
        }

        // 同指标记：A 又称 B / A 别名 B 里 B 是别名（显式标记，含中文别名）。
        foreach (Match match in AliasMarkerRegex.Matches(text))
        {
            var alias = match.Groups[1].Value.Trim();
            if (IsValidAlias(alias))
            {
                yield return alias;
            }
        }
    }

    private static bool IsValidAlias(string alias)
        => alias.Length >= 2
           && alias.Length <= 24
           && !StopWords.Contains(alias)
           && !alias.All(char.IsDigit)
           && alias.Any(ch => char.IsAsciiLetterOrDigit(ch));

    // 时效限定查询：任务/意图/目标里的生命周期/时间限定短语单独成低权重问句。
    // 限定词在长任务里可能被匹配器词元预算挤掉，独立保留后含相同限定词的文档标题仍可命中；
    // 只按完全重复去重，上限受 MaxTimeQualifierQueries 约束。
    private static void TryAddTimeQualifierQueries(
        List<AgentRetrievalQuery> queries,
        string task,
        string intent,
        IReadOnlyList<string> goals)
    {
        var combined = Concat(task, intent, goals);
        var added = 0;
        foreach (Match match in TimeQualifierRegex.Matches(combined))
        {
            if (queries.Count >= MaxControlledQueries || added >= MaxTimeQualifierQueries)
            {
                return;
            }
            var qualifier = match.Value;
            if (queries.Any(query => string.Equals(query.Text, qualifier, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            queries.Add(new AgentRetrievalQuery
            {
                Text = qualifier,
                Type = AgentRetrievalQueryType.Keyword,
                Weight = 0.5,
                Reason = "时效限定"
            });
            added++;
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
