using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Policy;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// 查询覆盖扩展契约测试。
/// 覆盖：短语锚定、显式别名、时效限定三类受控查询扩展——
/// 每种扩展只从输入文本的显式标记提取（引号/括号/同指标记，不做全局同义词膨胀），
/// 带 query provenance（Reason）、有界、去重、确定性；检索级验证短语/别名查询
/// 在长任务词元预算把特征碎片挤掉后仍能召回目标文档，且不引入 forbidden 特征词。
/// </summary>
[TestClass]
[TestCategory("LR2D")]
[TestCategory("Retrieval")]
public sealed class QueryCoverageExpansionTests
{
    private readonly DefaultAgentRetrievalQueryPlanner _planner = new();

    // ── 短语锚定 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 验证：引号/书名号内的整体短语单独成 Keyword 问句并保留 provenance。
    /// </summary>
    [TestMethod]
    public void Plan_PhraseAnchor_QuotedPhraseBecomesWholePhraseQuery()
    {
        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "梳理「两阶段提交」协议中的协调者角色、参与者角色、提交表决、超时处理与补偿机制",
            TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 0, MaxModelCalls = 30 }
        });

        var phraseQuery = plan.ControlledQueries.Single(query => query.Reason == "短语锚定");
        Assert.AreEqual("两阶段提交", phraseQuery.Text, "引号内短语应整体成问句，不被词元化打散。");
        Assert.AreEqual(AgentRetrievalQueryType.Keyword, phraseQuery.Type, "短语锚定问句应为关键词召回。");
        Assert.AreEqual(0.9, phraseQuery.Weight, 0.001, "短语锚定问句应有显式权重。");
        Assert.IsTrue(plan.ControlledQueries[0].Text.Contains("「两阶段提交」", StringComparison.Ordinal),
            "原始任务问句仍保留（短语锚定是额外问句，不替换任务）。");
    }

    /// <summary>
    /// 验证：成功工具观察结果里的引号短语也会被提取为锚定问句（观察词元提取会丢掉短语结构）。
    /// </summary>
    [TestMethod]
    public void Plan_PhraseAnchor_FromObservationResult()
    {
        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "summarize project notes",
            ToolObservations = new[]
            {
                new ToolObservation
                {
                    ToolName = "echo",
                    Succeeded = true,
                    Result = "AmberCompass-17 文件说明「GIN 索引」配置要点"
                }
            }
        });

        var phraseQuery = plan.ControlledQueries.FirstOrDefault(query => query.Reason == "短语锚定");
        Assert.IsNotNull(phraseQuery, "观察结果里的引号短语应成为锚定问句。");
        Assert.AreEqual("GIN 索引", phraseQuery!.Text, "引号短语整体保留，不带观察套话。");
    }

    /// <summary>
    /// 验证：短语锚定有界且去重（超上限不无限膨胀；完全重复不重复占名额）。
    /// </summary>
    [TestMethod]
    public void Plan_PhraseAnchor_BoundedAndDeduplicated()
    {
        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "对比「两阶段提交」与「两阶段提交」、并评估「补偿机制」与「检查点推进」",
            TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 0, MaxModelCalls = 30 }
        });

        var phraseQueries = plan.ControlledQueries
            .Where(query => query.Reason == "短语锚定")
            .Select(query => query.Text)
            .ToList();
        Assert.IsTrue(phraseQueries.Count <= DefaultAgentRetrievalQueryPlanner.MaxPhraseAnchorQueries,
            "短语锚定问句应封顶 MaxPhraseAnchorQueries。");
        Assert.AreEqual(phraseQueries.Distinct(StringComparer.Ordinal).Count(), phraseQueries.Count,
            "完全重复的短语不应重复占名额。");
    }

    /// <summary>
    /// 验证：没有引号标记时不产生任何短语锚定问句（无证据不膨胀）。
    /// </summary>
    [TestMethod]
    public void Plan_PhraseAnchor_NoQuotes_NoPhraseQuery()
    {
        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "系统怎么保证多节点同时写入不打架",
            TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 0, MaxModelCalls = 30 }
        });

        Assert.IsFalse(plan.ControlledQueries.Any(query => query.Reason == "短语锚定"),
            "没有引号/书名号标记时不应凭空制造短语问句。");
    }

    // ── 显式别名 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 验证：括号同指（A（B））里的 B 是显式别名，生成别名问句并保留 provenance。
    /// </summary>
    [TestMethod]
    public void Plan_Alias_ParenthesizedCoReference()
    {
        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "审计日志（Audit Trail）的保留期与导出格式",
            TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 0, MaxModelCalls = 30 }
        });

        var aliasQuery = plan.ControlledQueries.Single(query => query.Reason == "显式别名");
        Assert.AreEqual("Audit Trail", aliasQuery.Text, "括号内同指名应成为别名问句。");
        Assert.AreEqual(AgentRetrievalQueryType.Keyword, aliasQuery.Type, "别名问句应为关键词召回。");
    }

    /// <summary>
    /// 验证：显式同指标记（A 又称 B）里的 B 也是别名证据。
    /// </summary>
    [TestMethod]
    public void Plan_Alias_MarkerForm()
    {
        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "审计轨迹又称AuditTrail，保留 90 天",
            TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 0, MaxModelCalls = 30 }
        });

        var aliasQuery = plan.ControlledQueries.FirstOrDefault(query => query.Reason == "显式别名");
        Assert.IsNotNull(aliasQuery, "显式同指标记后的名字应成为别名问句。");
        Assert.AreEqual("AuditTrail", aliasQuery!.Text, "标记后的名字作为别名问句。");
    }

    /// <summary>
    /// 验证：纯中文括号注释（详见下文）不是别名，不产生查询；无同指标记时不产生别名问句。
    /// </summary>
    [TestMethod]
    public void Plan_Alias_NoExplicitMarker_NoAliasQuery()
    {
        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "多节点同时写入的冲突处理（详见下文）方案",
            TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 0, MaxModelCalls = 30 }
        });

        Assert.IsFalse(plan.ControlledQueries.Any(query => query.Reason == "显式别名"),
            "纯中文括号注释与无同指标记都不应制造别名问句（无证据不做同义词膨胀）。");
    }

    /// <summary>
    /// 验证：不做全局同义词膨胀——任务里没有同指证据时，不引入文档标题里的同义改写词。
    /// </summary>
    [TestMethod]
    public void Plan_Alias_NoGlobalSynonymExpansion()
    {
        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "系统怎么保证多节点同时写入不打架",
            TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 0, MaxModelCalls = 30 }
        });

        // "并发写入/状态冲突/最终一致"是目标文档标题的同义改写词，但任务文本里没有同指证据，
        // 规划器不得凭空补上（那会让 forbidden 文档也更容易被命中）。
        Assert.IsFalse(
            plan.ControlledQueries.Any(query => query.Text.Contains("并发写入", StringComparison.Ordinal)
                || query.Text.Contains("状态冲突", StringComparison.Ordinal)
                || query.Text.Contains("最终一致", StringComparison.Ordinal)),
            "没有同指证据时不得做全局同义词膨胀。");
    }

    /// <summary>
    /// 验证：别名问句有界（超上限不无限膨胀）。
    /// </summary>
    [TestMethod]
    public void Plan_Alias_Bounded()
    {
        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "审计日志（Audit Trail）与预写日志（WAL）及向量时钟（Vector Clock）的对比",
            TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 0, MaxModelCalls = 30 }
        });

        var aliasQueries = plan.ControlledQueries
            .Where(query => query.Reason == "显式别名")
            .ToList();
        Assert.IsTrue(aliasQueries.Count <= DefaultAgentRetrievalQueryPlanner.MaxAliasQueries,
            "别名问句应封顶 MaxAliasQueries。");
    }

    // ── 时效限定 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 验证：生命周期/时间限定短语单独成低权重问句并保留 provenance。
    /// </summary>
    [TestMethod]
    public void Plan_TimeQualifier_BecomesLowWeightQuery()
    {
        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "评估当前生效的检索优化方案哪版最合适",
            TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 0, MaxModelCalls = 30 }
        });

        var qualifierQuery = plan.ControlledQueries.Single(query => query.Reason == "时效限定");
        Assert.AreEqual("当前生效", qualifierQuery.Text, "限定短语应单独成问句。");
        Assert.AreEqual(0.5, qualifierQuery.Weight, 0.001, "时效限定问句应为低权重。");
        Assert.AreEqual(AgentRetrievalQueryType.Keyword, qualifierQuery.Type, "时效限定问句应为关键词召回。");
    }

    /// <summary>
    /// 验证：时效限定问句有界（多个限定词只取一个）。
    /// </summary>
    [TestMethod]
    public void Plan_TimeQualifier_Bounded()
    {
        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "当前生效的旧版方案与新版方案对比，已废弃的不作数",
            TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 0, MaxModelCalls = 30 }
        });

        var qualifierQueries = plan.ControlledQueries
            .Where(query => query.Reason == "时效限定")
            .ToList();
        Assert.AreEqual(1, qualifierQueries.Count, "多个限定词只保留一个（上限 MaxTimeQualifierQueries）。");
        Assert.AreEqual("当前生效", qualifierQueries[0].Text, "按文本出现顺序取第一个限定词。");
    }

    /// <summary>
    /// 验证：没有限定词时不产生时效限定问句。
    /// </summary>
    [TestMethod]
    public void Plan_TimeQualifier_NoQualifier_NoQuery()
    {
        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "summarize project notes",
            TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 0, MaxModelCalls = 30 }
        });

        Assert.IsFalse(plan.ControlledQueries.Any(query => query.Reason == "时效限定"),
            "没有生命周期/时间限定短语时不产生时效限定问句。");
    }

    // ── 综合不变量 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 验证：所有扩展问句带 provenance，且整体仍受 MaxControlledQueries 约束、确定性幂等。
    /// </summary>
    [TestMethod]
    public void Plan_Expansions_ProvenanceBoundedAndDeterministic()
    {
        var input = new AgentRetrievalPlannerInput
        {
            OriginalTask = "梳理「两阶段提交」协议，审计日志（Audit Trail）的当前生效版本",
            LatestAssistantIntent = "重点看预写日志（WAL）与已废弃方案的区别",
            TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 0, MaxModelCalls = 30 }
        };

        var plan = _planner.Plan(input);
        var rerun = _planner.Plan(input);

        Assert.IsTrue(plan.ControlledQueries.All(query => !string.IsNullOrWhiteSpace(query.Reason)),
            "每条受控查询都应保留 provenance（Reason 非空）。");
        Assert.IsTrue(plan.ControlledQueries.Count <= DefaultAgentRetrievalQueryPlanner.MaxControlledQueries,
            "扩展后查询集仍应受控有界。");
        CollectionAssert.AreEqual(
            plan.ControlledQueries.Select(q => q.Text).ToArray(),
            rerun.ControlledQueries.Select(q => q.Text).ToArray(),
            "相同输入应产生相同计划（确定性幂等）。");
    }

    /// <summary>
    /// 验证：失败观察里的 ID 只进排除集，不因扩展机制被重新拿去搜索。
    /// </summary>
    [TestMethod]
    public void Plan_FailedId_NotRerecalled_WithExpansions()
    {
        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "梳理「两阶段提交」协议",
            ToolObservations = new[]
            {
                new ToolObservation { ToolName = "echo", Succeeded = false, Error = "未找到 id:gone-1" }
            }
        });

        CollectionAssert.Contains(plan.ExcludedIds.ToList(), "gone-1",
            "失败观察里的 ID 应进入排除集。");
        Assert.IsFalse(
            plan.ControlledQueries.Any(query => query.Text.Contains("gone-1", StringComparison.OrdinalIgnoreCase)),
            "失败 ID 不应被任何扩展机制重新拿去搜索。");
    }

    /// <summary>
    /// 验证：扩展不引入输入文本之外的词（hard-negative 保护：不会凭空补上 forbidden 文档特征词）。
    /// </summary>
    [TestMethod]
    public void Plan_Expansion_NeverFabricatesTermsOutsideInput()
    {
        var input = "多节点同时写入状态冲突如何解决";
        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = input,
            TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 0, MaxModelCalls = 30 }
        });

        var inputTerms = new HashSet<string>(
            System.Text.RegularExpressions.Regex.Split(input, @"[^\p{L}\p{N}_\-]+")
                .Where(term => term.Length >= 2),
            StringComparer.OrdinalIgnoreCase);
        foreach (var query in plan.ControlledQueries)
        {
            if (query.Reason is "短语锚定" or "显式别名" or "时效限定")
            {
                Assert.IsTrue(
                    input.Contains(query.Text, StringComparison.OrdinalIgnoreCase),
                    $"扩展问句 [{query.Text}] 必须来自输入文本，不得凭空生成。");
            }
        }
    }

    // ── 检索级覆盖（长任务词元预算 + hard negative）────────────────────────────

    /// <summary>
    /// 验证：长任务里引号短语被匹配器词元预算挤掉时，短语锚定问句仍能召回目标文档；
    /// 共享词元的噪声文档不被短语问句命中。
    /// </summary>
    [TestMethod]
    public async Task Retrieval_PhraseQuery_RecallsDoc_BeyondTaskTermBudget()
    {
        var store = new InMemoryContextStore();
        await store.SaveAsync(new ContextItem
        {
            Id = "two-phase-commit",
            WorkspaceId = "ws",
            CollectionId = "col",
            Type = "spec",
            Title = "两阶段提交协议规范",
            Content = "原子性 一致性 全局提交 协议规范"
        });
        await store.SaveAsync(new ContextItem
        {
            Id = "gin-tuning",
            WorkspaceId = "ws",
            CollectionId = "col",
            Type = "perf",
            Title = "PostgreSQL GIN 索引性能调优",
            Content = "GIN 索引 模糊匹配 pg_trgm 中文分词"
        });

        // 长任务：目标短语「两阶段提交」位于词元预算（12）之后，单独用任务文本无法命中。
        var longTask = "梳理事务协调中关于失败恢复、表决超时、参与者失联、数据补齐、日志回放、"
            + "检查点推进、重试上限与「两阶段提交」协议的整体行为";

        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = longTask,
            TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 0, MaxModelCalls = 30 }
        });
        Assert.IsTrue(plan.ControlledQueries.Any(query => query.Reason == "短语锚定"),
            "长任务里的引号短语应生成短语锚定问句。");

        var provider = new LexicalCandidateProvider(store);

        // 只用任务文本（无短语问句）：目标文档不在词元预算内 → 漏召回。
        var taskOnly = await provider.ExecuteAsync(MakeContext(
            queryText: longTask,
            queryTexts: new[] { longTask },
            includeContent: false));
        Assert.IsFalse(
            taskOnly.Envelopes.Any(item => item.CanonicalKey.EntityId == "two-phase-commit"),
            "词元预算外：任务文本单独检索应漏召回目标文档（验证短语问句的必要性）。");

        // 按计划检索（任务 + 短语锚定）：短语问句命中目标文档；噪声文档不被短语问句命中。
        var withPlan = await provider.ExecuteAsync(MakeContext(
            queryText: longTask,
            queryTexts: plan.ControlledQueries.Select(query => query.Text).ToArray(),
            includeContent: false));
        Assert.IsTrue(
            withPlan.Envelopes.Any(item => item.CanonicalKey.EntityId == "two-phase-commit"),
            "短语锚定问句应召回词元预算外的目标文档。");
        Assert.IsFalse(
            withPlan.Envelopes.Any(item => item.CanonicalKey.EntityId == "gin-tuning"),
            "短语问句不命中共享任务词元但无短语的噪声文档。");
    }

    /// <summary>
    /// 验证：括号同指别名在长任务词元预算外时，别名问句仍能按别名名召回目标文档。
    /// </summary>
    [TestMethod]
    public async Task Retrieval_AliasQuery_RecallsDoc_ByAliasName()
    {
        var store = new InMemoryContextStore();
        await store.SaveAsync(new ContextItem
        {
            Id = "audit-en",
            WorkspaceId = "ws",
            CollectionId = "col",
            Type = "spec",
            Title = "Audit Trail Policy",
            Content = "audit export policy retention permissions"
        });
        await store.SaveAsync(new ContextItem
        {
            Id = "audit-cn",
            WorkspaceId = "ws",
            CollectionId = "col",
            Type = "spec",
            Title = "审计日志中文保留策略",
            Content = "审计日志 保留期 导出格式"
        });

        // 长任务：别名「Audit Trail」位于词元预算之后，单独用任务文本无法按别名命中。
        var longTask = "审计日志的保留期、导出格式、权限控制、合规审计要求、脱敏规则、"
            + "存储周期（Audit Trail）与查询权限";

        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = longTask,
            TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 0, MaxModelCalls = 30 }
        });
        var aliasQuery = plan.ControlledQueries.FirstOrDefault(query => query.Reason == "显式别名");
        Assert.IsNotNull(aliasQuery, "括号同指应生成别名问句。");
        Assert.AreEqual("Audit Trail", aliasQuery!.Text, "别名问句应为括号内同指名。");

        var provider = new LexicalCandidateProvider(store);

        var taskOnly = await provider.ExecuteAsync(MakeContext(
            queryText: longTask,
            queryTexts: new[] { longTask },
            includeContent: false));
        Assert.IsFalse(
            taskOnly.Envelopes.Any(item => item.CanonicalKey.EntityId == "audit-en"),
            "词元预算外：任务文本单独检索应漏按别名命中的文档。");

        var withPlan = await provider.ExecuteAsync(MakeContext(
            queryText: longTask,
            queryTexts: plan.ControlledQueries.Select(query => query.Text).ToArray(),
            includeContent: false));
        Assert.IsTrue(
            withPlan.Envelopes.Any(item => item.CanonicalKey.EntityId == "audit-en"),
            "别名问句应按别名名召回目标文档。");
    }

    // ── 上下文构造 ───────────────────────────────────────────────────────────

    private static CandidateProviderContext MakeContext(
        string queryText,
        IReadOnlyList<string>? queryTexts,
        bool includeContent)
    {
        var bundle = DefaultPolicyBundleFactory.Create();
        var snapshot = new EffectivePolicySnapshot
        {
            Reference = new ResolvedPolicyReference
            {
                BundleId = bundle.BundleId,
                BundleVersion = bundle.Version,
                BundleContentHash = DefaultResolvedPolicyProvider.DefaultContentHash,
                ActivationEpoch = DefaultResolvedPolicyProvider.DefaultActivationEpoch
            },
            Safety = bundle.Safety,
            Budget = bundle.Budget,
            Routing = bundle.Routing,
            FeatureSchemaVersion = bundle.Policies.DecisionSchemaVersion,
            ResolutionScope = new ContextDecisionScope("ws", "col")
        };

        return new CandidateProviderContext(
            Request: new ContextDecisionRuntimeRequest
            {
                RequestId = "req-query-coverage",
                Scope = new ContextDecisionScope("ws", "col"),
                Purpose = ContextDecisionPurpose.AgentContext,
                QueryText = queryText,
                TokenBudget = 4096,
                TopK = 10,
                RetrievalInput = new RetrievalInput
                {
                    IncludeContent = includeContent,
                    QueryTexts = queryTexts ?? Array.Empty<string>()
                }
            },
            Policy: snapshot,
            Routing: new ExpertRoutingDecision
            {
                Expert = RetrievalExpert.Lexical,
                Enabled = true,
                TopK = 10,
                TokenBudget = 4096,
                Weight = 1.0,
                ReasonCode = "test"
            },
            AdaptationContext: new CandidateAdaptationContext
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                ObservedAt = DateTimeOffset.UtcNow
            });
    }
}
