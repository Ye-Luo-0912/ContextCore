using ContextCore.Abstractions;
using ContextCore.Core.Services.Retrieval;

namespace ContextCore.Tests;

// ===========================================================================
// Agent 受控检索查询规划器生产验收测试
//
// 验证 的受控检索规划设计：
// 1. Plan_ControlledQuerySet_Bounded — 查询集有界（≤ MaxControlledQueries）且
// 按 任务→意图→（上一轮 0 命中时的实体词）→成功工具观察→未解决目标→图种子 的顺序受控生成。
// 2. Plan_RequiredIds_FromExplicitReferences — 从任务/意图/目标提取显式 ID 引用
// （去重 + 封顶 MaxRequiredIds）。
// 3. Plan_ExcludedIds_FromFailedToolObservations — 失败 Tool 观察推导排除 ID，
// 成功观察不产生排除项（封顶 MaxExcludedIds）。
// 4. Plan_GraphSeeds_FromAnchorsAndTokens — 图种子取引号/书名号内显式锚点，
// 补充长词元，封顶 MaxGraphSeeds。
// 5. Plan_TokenBudget_FromTurnBudget — Token 预算由剩余 Turn 推导并钳制上下限。
// 6. Plan_DiagnosticBackoff_ReducesBudget — 上一轮预算超限时受控回退减半。
// 7. Plan_Deterministic_SameInputSameOutput — 相同输入产生相同计划（幂等）。
// 8. Plan_EmptyTask_ReturnsMinimalControlledPlan — 空任务产出最小受控计划，不抛异常。
// 9. Plan_NullInput_Throws — null 输入抛 ArgumentNullException。
// 10. Plan_ObservationQueries_PreferNewestWhenSlotsLimited — 名额不够时最新成功观察优先占查询名额。
// 11. Plan_EmptyRecall_AddsEntityWordQuery_AndHonestReason — 上一轮 0 命中时把任务/意图里的
// 实体样词单独加成 Keyword 问句，Reason 如实标注且不提向量。
//
// 设计原则：
// - 使用真实实现 DefaultAgentRetrievalQueryPlanner（纯内存、确定性）。
// - 断言聚焦受控性质（上限、去重、顺序、回退），不依赖脆弱的具体种子文本。
// - 中文注释。
// ===========================================================================

[TestClass]
[TestCategory("Agent")]
[TestCategory("Retrieval")]
public sealed class R29M_AgentRetrievalQueryPlannerTests
{
    private readonly IAgentRetrievalQueryPlanner _planner = new DefaultAgentRetrievalQueryPlanner();

    /// <summary>
    /// 验证：查询集受控有界——任务（混合）→ 意图（关键词）→ 未解决目标（关键词逐条）
    /// → 成功工具观察实体词 → 图种子锚定查询（关键词），总数不超过 MaxControlledQueries。
    /// </summary>
    [TestMethod]
    public void Plan_ControlledQuerySet_Bounded()
    {
        var input = new AgentRetrievalPlannerInput
        {
            OriginalTask = "调研量子计算在金融风控中的应用",
            LatestAssistantIntent = "需要补充量子计算硬件的成熟度数据",
            UnresolvedGoals = new[] { "找到量子退火商用案例", "评估误差纠正进展" },
            TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 0, MaxModelCalls = 30 }
        };

        var plan = _planner.Plan(input);

        Assert.IsTrue(plan.ControlledQueries.Count <= DefaultAgentRetrievalQueryPlanner.MaxControlledQueries,
            "查询集应受控有界（不超过 MaxControlledQueries）。");
        Assert.IsTrue(plan.ControlledQueries.Count >= 3, "任务 + 意图 + 未解决目标应各产出一条查询。");

        // 顺序：原始任务（混合）→ 最新意图（关键词）→ 未解决目标（关键词逐条）
        Assert.AreEqual(input.OriginalTask, plan.ControlledQueries[0].Text, "首条查询应为原始任务。");
        Assert.AreEqual(AgentRetrievalQueryType.Hybrid, plan.ControlledQueries[0].Type, "原始任务查询应为混合召回。");
        Assert.AreEqual(input.LatestAssistantIntent, plan.ControlledQueries[1].Text, "第二条查询应为最新意图。");
        Assert.AreEqual(AgentRetrievalQueryType.Keyword, plan.ControlledQueries[1].Type, "意图查询应为关键词召回。");

        // 每个未解决目标单独一条关键词查询，不拼成一句、不标向量
        Assert.AreEqual(AgentRetrievalQueryType.Keyword, plan.ControlledQueries[2].Type,
            "未解决目标查询应为关键词召回（逐条，不再拼成向量）。");
        Assert.AreEqual("未解决目标", plan.ControlledQueries[2].Reason,
            "未解决目标查询的原因应标注来源。");
        Assert.AreEqual(input.UnresolvedGoals[0], plan.ControlledQueries[2].Text,
            "每个未解决目标应单独占一条查询。");
        if (plan.ControlledQueries.Count > 3)
        {
            Assert.AreEqual(input.UnresolvedGoals[1], plan.ControlledQueries[3].Text,
                "第二个未解决目标也应单独占一条查询。");
        }
    }

    /// <summary>
    /// 验证：未解决目标逐条加成 Keyword 查询，不拼成一句、不标 Vector。
    /// </summary>
    [TestMethod]
    public void Plan_UnresolvedGoals_PerGoalKeyword()
    {
        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "summarize project notes",
            UnresolvedGoals = new[] { "AmberCompass-17", "PurpleBicycle-42" }
        });

        var goalQueries = plan.ControlledQueries
            .Where(query => query.Reason == "未解决目标")
            .Select(query => query.Text)
            .ToList();
        CollectionAssert.Contains(goalQueries, "AmberCompass-17",
            "找回问句应含被裁掉条目的实体词。");
        CollectionAssert.Contains(goalQueries, "PurpleBicycle-42",
            "每个未解决目标各占一条找回问句。");
        Assert.IsTrue(goalQueries.All(text => !text.Contains(' ', StringComparison.Ordinal)),
            "未解决目标不应拼成一句。");
        Assert.IsFalse(plan.ControlledQueries.Any(query => query.Type == AgentRetrievalQueryType.Vector),
            "未解决目标不再拼成单条向量查询（默认没有向量）。");
    }

    /// <summary>
    /// 验证：必需召回 ID 从任务/意图/未解决目标的显式引用（id:/ref:/uuid:）提取，
    /// 去重后封顶 MaxRequiredIds。
    /// </summary>
    [TestMethod]
    public void Plan_RequiredIds_FromExplicitReferences()
    {
        var input = new AgentRetrievalPlannerInput
        {
            OriginalTask = "对比 id:alpha-123 与 id:beta_456 的检索质量",
            LatestAssistantIntent = "ref=gamma7890 也应纳入对比",
            UnresolvedGoals = new[] { "完成 uuid:delta-0001 的评估" }
        };

        var plan = _planner.Plan(input);

        CollectionAssert.AreEquivalent(
            new[] { "alpha-123", "beta_456", "gamma7890", "delta-0001" },
            plan.RequiredIds.ToList(),
            "应提取任务/意图/目标中的全部显式 ID 引用。");
    }

    /// <summary>
    /// 验证：重复 ID 引用去重，且超过上限时截断到 MaxRequiredIds。
    /// </summary>
    [TestMethod]
    public void Plan_RequiredIds_DeduplicatedAndCapped()
    {
        // 重复引用 → 去重
        var dupInput = new AgentRetrievalPlannerInput
        {
            OriginalTask = "id:alpha-123 与 id:alpha-123 相同",
            LatestAssistantIntent = "再次引用 id:alpha-123"
        };
        var dupPlan = _planner.Plan(dupInput);
        Assert.AreEqual(1, dupPlan.RequiredIds.Count, "重复 ID 引用应去重。");

        // 12 个不同 ID → 截断到上限 8
        var manyIds = string.Join(" ", Enumerable.Range(0, 12).Select(i => $"id:cap-{i:0000}"));
        var capPlan = _planner.Plan(new AgentRetrievalPlannerInput { OriginalTask = manyIds });
        Assert.AreEqual(DefaultAgentRetrievalQueryPlanner.MaxRequiredIds, capPlan.RequiredIds.Count,
            "必需 ID 应封顶 MaxRequiredIds。");
    }

    /// <summary>
    /// 验证：排除 ID 仅来自失败的 Tool 观察（确认不存在的实体），
    /// 成功观察不产生排除项，且封顶 MaxExcludedIds。
    /// </summary>
    [TestMethod]
    public void Plan_ExcludedIds_FromFailedToolObservations()
    {
        var input = new AgentRetrievalPlannerInput
        {
            OriginalTask = "检索实体",
            ToolObservations = new[]
            {
                new ToolObservation { ToolName = "lookup", ToolCallId = "t1", Succeeded = false, Error = "未找到 id:missing-01" },
                new ToolObservation { ToolName = "lookup", ToolCallId = "t2", Succeeded = true, Result = "已找到 id:found-02" },
                new ToolObservation { ToolName = "lookup", ToolCallId = "t3", Succeeded = false, Error = "ref=nope_03 不存在" }
            }
        };

        var plan = _planner.Plan(input);

        CollectionAssert.AreEquivalent(
            new[] { "missing-01", "nope_03" },
            plan.ExcludedIds.ToList(),
            "排除 ID 应只包含失败观察中确认不存在的实体。");
        CollectionAssert.DoesNotContain(plan.ExcludedIds.ToList(), "found-02",
            "成功观察的 ID 不应进入排除集。");
    }

    /// <summary>
    /// 验证：排除 ID 封顶 MaxExcludedIds。
    /// </summary>
    [TestMethod]
    public void Plan_ExcludedIds_Capped()
    {
        var observations = Enumerable.Range(0, 12)
            .Select(i => new ToolObservation
            {
                ToolName = "lookup",
                ToolCallId = $"t{i}",
                Succeeded = false,
                Error = $"缺失 id:miss-{i:0000}"
            })
            .ToArray();

        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "检索实体",
            ToolObservations = observations
        });

        Assert.AreEqual(DefaultAgentRetrievalQueryPlanner.MaxExcludedIds, plan.ExcludedIds.Count,
            "排除 ID 应封顶 MaxExcludedIds。");
    }

    /// <summary>
    /// 验证：图种子优先取引号/书名号内显式锚点，补充长词元，封顶 MaxGraphSeeds。
    /// </summary>
    [TestMethod]
    public void Plan_GraphSeeds_FromAnchorsAndTokens()
    {
        var input = new AgentRetrievalPlannerInput
        {
            OriginalTask = "研究《量子计算》与「超导比特」的成熟度",
            LatestAssistantIntent = "重点关注 id:hardware-01 的进展"
        };

        var plan = _planner.Plan(input);

        CollectionAssert.Contains(plan.GraphSeeds.ToList(), "量子计算", "书名号内锚点应成为图种子。");
        CollectionAssert.Contains(plan.GraphSeeds.ToList(), "超导比特", "引号内锚点应成为图种子。");
        Assert.IsTrue(plan.GraphSeeds.Count <= DefaultAgentRetrievalQueryPlanner.MaxGraphSeeds,
            "图种子应封顶 MaxGraphSeeds。");
    }

    /// <summary>
    /// 验证：Token 预算由剩余 Turn 推导并钳制在 [MinTokenBudget, MaxTokenBudget]。
    /// </summary>
    [TestMethod]
    public void Plan_TokenBudget_FromTurnBudget()
    {
        // 剩余 2 轮 → 2 × 1024 = 2048
        var p1 = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "任务",
            TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 8, MaxModelCalls = 30 }
        });
        Assert.AreEqual(2048, p1.TokenBudget, "剩余 2 轮的 Token 预算应为 2048。");

        // 剩余 0 轮 → 最小预算
        var p2 = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "任务",
            TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 10, MaxModelCalls = 30 }
        });
        Assert.AreEqual(DefaultAgentRetrievalQueryPlanner.MinTokenBudget, p2.TokenBudget,
            "剩余 0 轮时预算应钳制到最小预算。");

        // 未配置 TurnBudget → 默认值（剩余 4 轮语义）
        var p3 = _planner.Plan(new AgentRetrievalPlannerInput { OriginalTask = "任务" });
        Assert.AreEqual(4096, p3.TokenBudget, "未配置 TurnBudget 时预算应为默认 4096。");

        // 剩余 100 轮 → 钳制到最大预算
        var p4 = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "任务",
            TurnBudget = new AgentTurnBudget { MaxTurns = 100, TurnsUsed = 0, MaxModelCalls = 300 }
        });
        Assert.AreEqual(DefaultAgentRetrievalQueryPlanner.MaxTokenBudget, p4.TokenBudget,
            "超大剩余轮次的预算应钳制到最大预算。");
    }

    /// <summary>
    /// 验证：上一轮检索预算超限（BudgetExceeded=true）时，本轮预算受控回退减半。
    /// </summary>
    [TestMethod]
    public void Plan_DiagnosticBackoff_ReducesBudget()
    {
        var input = new AgentRetrievalPlannerInput
        {
            OriginalTask = "任务",
            TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 6, MaxModelCalls = 30 },
            PreviousRetrievalDiagnostics = new[]
            {
                new AgentRetrievalDiagnostic { QueryText = "任务", HitsReturned = 3, BudgetExceeded = true }
            }
        };

        var plan = _planner.Plan(input);

        Assert.AreEqual(2048, plan.TokenBudget, "预算超限后本轮应回退减半（4096 → 2048）。");
        StringAssert.Contains(plan.Reason, "回退", "计划说明应标注受控回退。");
    }

    /// <summary>
    /// 验证：相同输入产生相同计划（确定性 / 幂等，无随机性）。
    /// </summary>
    [TestMethod]
    public void Plan_Deterministic_SameInputSameOutput()
    {
        var input = new AgentRetrievalPlannerInput
        {
            OriginalTask = "调研量子计算在金融风控中的应用",
            LatestAssistantIntent = "需要补充量子计算硬件的成熟度数据",
            UnresolvedGoals = new[] { "找到量子退火商用案例" },
            ToolObservations = new[]
            {
                new ToolObservation { ToolName = "lookup", ToolCallId = "t1", Succeeded = false, Error = "未找到 id:missing-01" }
            },
            TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 3, MaxModelCalls = 30 }
        };

        var planA = _planner.Plan(input);
        var planB = _planner.Plan(input);

        Assert.AreEqual(planA.TokenBudget, planB.TokenBudget, "Token 预算应确定。");
        Assert.AreEqual(planA.Reason, planB.Reason, "计划说明应确定。");
        CollectionAssert.AreEqual(
            planA.ControlledQueries.Select(q => q.Text).ToList(),
            planB.ControlledQueries.Select(q => q.Text).ToList(),
            "查询集应确定。");
        CollectionAssert.AreEqual(planA.RequiredIds.ToList(), planB.RequiredIds.ToList(), "必需 ID 应确定。");
        CollectionAssert.AreEqual(planA.ExcludedIds.ToList(), planB.ExcludedIds.ToList(), "排除 ID 应确定。");
        CollectionAssert.AreEqual(planA.GraphSeeds.ToList(), planB.GraphSeeds.ToList(), "图种子应确定。");
    }

    /// <summary>
    /// 验证：空任务仍产出最小受控计划（空查询集 + 最小预算），不抛异常。
    /// </summary>
    [TestMethod]
    public void Plan_EmptyTask_ReturnsMinimalControlledPlan()
    {
        var plan = _planner.Plan(new AgentRetrievalPlannerInput { OriginalTask = string.Empty });

        Assert.AreEqual(0, plan.ControlledQueries.Count, "空任务不应生成检索查询。");
        Assert.AreEqual(0, plan.RequiredIds.Count, "空任务不应有必需 ID。");
        Assert.AreEqual(0, plan.ExcludedIds.Count, "空任务不应有排除 ID。");
        Assert.AreEqual(DefaultAgentRetrievalQueryPlanner.MinTokenBudget, plan.TokenBudget,
            "空任务应只给最小 Token 预算。");
        StringAssert.Contains(plan.Reason, "原始任务为空", "计划说明应标注任务为空。");
    }

    [TestMethod]
    public void Plan_SuccessfulToolObservation_AddsQuery_FailedDoesNot()
    {
        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "summarize project notes",
            ToolObservations = new[]
            {
                new ToolObservation { ToolName = "echo", Succeeded = false, Error = "未找到 id:keep-1" },
                new ToolObservation { ToolName = "echo", Succeeded = true, Result = "AmberCompass-17 found in notes" }
            }
        });

        Assert.AreEqual("summarize project notes", plan.ControlledQueries[0].Text);
        CollectionAssert.Contains(plan.ExcludedIds.ToList(), "keep-1");
        var observationQuery = plan.ControlledQueries.FirstOrDefault(query => query.Reason == "成功工具观察");
        Assert.IsNotNull(observationQuery, "成功工具观察应成为受控查询，而不是靠固定词表。");
        Assert.AreEqual("AmberCompass-17", observationQuery!.Text, "观察查询只保留新实体词，不带 found/notes。");
        Assert.IsFalse(
            plan.ControlledQueries.Any(query => query.Text.Contains("keep-1", StringComparison.Ordinal)),
            "失败观察里的 ID 只排除，不拿去再搜。");
    }

    /// <summary>
    /// 验证：查询名额不够时，成功工具观察按时间倒序占名额（最新优先，最旧让位）。
    /// </summary>
    [TestMethod]
    public void Plan_ObservationQueries_PreferNewestWhenSlotsLimited()
    {
        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "summarize project notes",
            ToolObservations = new[]
            {
                new ToolObservation { ToolName = "echo", Succeeded = true, Result = "OldToken-1 found" },
                new ToolObservation { ToolName = "echo", Succeeded = true, Result = "MiddleToken-2 found" },
                new ToolObservation { ToolName = "echo", Succeeded = true, Result = "MiddleToken-3 found" },
                new ToolObservation { ToolName = "echo", Succeeded = true, Result = "AmberCompass-17 found in notes" }
            }
        });

        Assert.AreEqual("summarize project notes", plan.ControlledQueries[0].Text, "首条查询应为原始任务。");
        var observationQueries = plan.ControlledQueries
            .Where(query => query.Reason == "成功工具观察")
            .Select(query => query.Text)
            .ToList();
        Assert.AreEqual(DefaultAgentRetrievalQueryPlanner.MaxControlledQueries, plan.ControlledQueries.Count,
            "任务占一条后，观察实体词应填满剩余受控名额。");
        CollectionAssert.Contains(observationQueries, "AmberCompass-17",
            "名额不够时最新成功观察应优先占查询名额。");
        Assert.IsFalse(observationQueries.Contains("OldToken-1"),
            "名额不够时最旧的观察应让位，而不是只留下最早的结果。");
    }

    /// <summary>
    /// 验证：排除 ID 只取最近窗口且最新失败优先——旧的 id:missing 不再占排除名额。
    /// </summary>
    [TestMethod]
    public void Plan_ExcludedIds_PreferNewestFailures_WhenWindowExceeded()
    {
        var observations = Enumerable.Range(1, 9)
            .Select(i => new ToolObservation
            {
                ToolName = "lookup",
                ToolCallId = $"t{i}",
                Succeeded = false,
                Error = $"缺失 id:miss-{i}"
            })
            .ToArray();

        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "检索实体",
            ToolObservations = observations
        });

        Assert.AreEqual(DefaultAgentRetrievalQueryPlanner.MaxExcludedIds, plan.ExcludedIds.Count,
            "排除 ID 应封顶 MaxExcludedIds。");
        CollectionAssert.Contains(plan.ExcludedIds.ToList(), "miss-9",
            "最新失败应先占排除名额。");
        Assert.IsFalse(plan.ExcludedIds.Contains("miss-1"),
            "窗口外的最旧失败不再进排除列表。");
    }

    /// <summary>
    /// 验证：查询只来自最近若干条成功观察，窗口外的旧实体不再占查询名额。
    /// </summary>
    [TestMethod]
    public void Plan_ObservationQueries_UseRecentWindow_NotAllHistory()
    {
        var observations = Enumerable.Range(1, 9)
            .Select(i => new ToolObservation
            {
                ToolName = "echo",
                Succeeded = true,
                Result = $"Token-{i} found"
            })
            .ToArray();

        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "summarize notes",
            ToolObservations = observations
        });

        var observationQueries = plan.ControlledQueries
            .Where(query => query.Reason == "成功工具观察")
            .Select(query => query.Text)
            .ToList();
        CollectionAssert.Contains(observationQueries, "Token-9",
            "最新成功观察应进入查询。");
        Assert.IsFalse(observationQueries.Contains("Token-1"),
            "窗口外的旧观察不再占查询名额。");
    }

    /// <summary>
    /// 验证：图种子词元已被任务查询覆盖时不再占查询名额（任务套话不重复搜）。
    /// </summary>
    [TestMethod]
    public void Plan_GraphSeedQuery_SkipsWordsAlreadyCoveredByTask()
    {
        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "summarize project notes"
        });

        Assert.AreEqual("summarize project notes", plan.ControlledQueries[0].Text, "首条查询应为原始任务。");
        var seedQueries = plan.ControlledQueries
            .Where(query => query.Reason == "图种子锚定查询")
            .ToList();
        Assert.IsFalse(
            seedQueries.Any(query => query.Text == "summarize" || query.Text == "project"),
            "任务里已有的词元不应再作为图种子单独查询（重复检索无新信息）。");
    }

    /// <summary>
    /// 验证：观察实体问句保留，图种子条目不得把观察结果整段带上。
    /// </summary>
    [TestMethod]
    public void Plan_GraphSeedQuery_SkipsCoveredWords_KeepsObservationEntity()
    {
        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "summarize project notes",
            ToolObservations = new[]
            {
                new ToolObservation { ToolName = "echo", Succeeded = true, Result = "AmberCompass-17 found in notes" }
            }
        });

        var observationQuery = plan.ControlledQueries.Single(query => query.Reason == "成功工具观察");
        Assert.AreEqual("AmberCompass-17", observationQuery.Text, "观察查询只保留新实体词。");
        Assert.AreEqual(2, plan.ControlledQueries.Count,
            "任务里的词元都被任务查询覆盖，图种子不应再占查询名额。");
        var seedQueries = plan.ControlledQueries
            .Where(query => query.Reason == "图种子锚定查询")
            .ToList();
        Assert.IsFalse(
            seedQueries.Any(query => query.Text.Contains("found", StringComparison.Ordinal)
                || query.Text.Contains("notes", StringComparison.Ordinal)),
            "图种子条目不得把观察结果里的套话整段带上。");
    }

    /// <summary>
    /// 验证：引号/书名号内显式实体锚点仍保留在查询集或图种子里（显式锚点优先）。
    /// </summary>
    [TestMethod]
    public void Plan_QuotedEntity_RemainsInGraphSeedsOrQueries()
    {
        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "summarize 《AmberCompass-17》 project notes"
        });

        const string entity = "AmberCompass-17";
        Assert.IsTrue(
            plan.GraphSeeds.Contains(entity)
            || plan.ControlledQueries.Any(query => query.Text.Contains(entity, StringComparison.Ordinal)),
            "引号/书名号内实体应保留在查询集或图种子里（显式锚点优先）。");
    }

    /// <summary>
    /// 验证：null 输入抛 ArgumentNullException（fail-fast）。
    /// </summary>
    [TestMethod]
    public void Plan_NullInput_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(() => _planner.Plan(null!));
    }

    /// <summary>
    /// 验证：上一轮 0 命中（HitsReturned=0）时，任务/意图里的实体样词单独加成 Keyword 问句，
    /// 计划说明如实标注 0 命中且不再提「向量」。
    /// </summary>
    [TestMethod]
    public void Plan_EmptyRecall_AddsEntityWordQuery_AndHonestReason()
    {
        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "调研 PurpleBicycle-42 项目在金融风控中的应用",
            LatestAssistantIntent = "补充 GreenGadget-7 的进展",
            PreviousRetrievalDiagnostics = new[]
            {
                new AgentRetrievalDiagnostic
                {
                    QueryText = "调研 PurpleBicycle-42 项目在金融风控中的应用",
                    HitsReturned = 0
                }
            }
        });

        var entityQueries = plan.ControlledQueries
            .Where(query => query.Reason == "空召回实体词")
            .Select(query => query.Text)
            .ToList();
        CollectionAssert.Contains(entityQueries, "PurpleBicycle-42",
            "0 命中后任务里的实体样词应单独成问句。");
        CollectionAssert.Contains(entityQueries, "GreenGadget-7",
            "意图里的实体样词同样应单独成问句。");
        var entityQuery = plan.ControlledQueries.First(query => query.Text == "PurpleBicycle-42");
        Assert.AreEqual(AgentRetrievalQueryType.Keyword, entityQuery.Type,
            "空召回恢复问句应为关键词召回，不标向量。");
        Assert.IsFalse(plan.Reason.Contains("向量", StringComparison.Ordinal),
            "计划说明不得再谎称增加向量查询。");
        StringAssert.Contains(plan.Reason, "0 命中",
            "计划说明应如实标注上一轮 0 命中。");
    }

    /// <summary>
    /// 验证：空召回恢复只在上一轮 0 命中时触发；上一轮有命中时不拆实体词。
    /// </summary>
    [TestMethod]
    public void Plan_EmptyRecall_OnlyWhenPreviousRoundHadZeroHits()
    {
        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "调研 PurpleBicycle-42 项目",
            PreviousRetrievalDiagnostics = new[]
            {
                new AgentRetrievalDiagnostic
                {
                    QueryText = "调研 PurpleBicycle-42 项目",
                    HitsReturned = 2,
                    HighestScore = 0.6
                }
            }
        });

        Assert.IsFalse(
            plan.ControlledQueries.Any(query => query.Reason == "空召回实体词"),
            "上一轮有命中时不应拆出实体词恢复问句。");
        Assert.IsFalse(plan.Reason.Contains("0 命中", StringComparison.Ordinal),
            "上一轮有命中时说明不应写 0 命中。");
    }

    /// <summary>
    /// 验证：任务本身已是实体词单独问句时，空召回不重复加成同一问句。
    /// </summary>
    [TestMethod]
    public void Plan_EmptyRecall_SkipsTermsAlreadyQueried()
    {
        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "PurpleBicycle-42",
            PreviousRetrievalDiagnostics = new[]
            {
                new AgentRetrievalDiagnostic { QueryText = "PurpleBicycle-42", HitsReturned = 0 }
            }
        });

        Assert.AreEqual(1, plan.ControlledQueries.Count,
            "任务本身就是实体词问句，不应再重复加成。");
    }

    /// <summary>
    /// 验证：成功工具观察里的显式 id: 引用按条加成 Keyword 问句（精确取 ID 本身），
    /// 不钉 RequiredIds、不进 ExcludedIds。
    /// </summary>
    [TestMethod]
    public void Plan_SuccessfulIdReference_BecomesQuery_NotRequiredOrExcluded()
    {
        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "summarize project notes",
            ToolObservations = new[]
            {
                new ToolObservation { ToolName = "lookup", ToolCallId = "t1", Succeeded = true, Result = "found id:alpha in notes" }
            }
        });

        var idQuery = plan.ControlledQueries.FirstOrDefault(query => query.Reason == "成功工具观察 ID");
        Assert.IsNotNull(idQuery, "成功观察里的显式 id: 引用应单独加成问句。");
        Assert.AreEqual("alpha", idQuery!.Text, "问句应精确取 ID 本身，不带 found/notes。");
        Assert.AreEqual(AgentRetrievalQueryType.Keyword, idQuery.Type, "成功 ID 问句应为关键词召回。");
        CollectionAssert.DoesNotContain(plan.RequiredIds.ToList(), "alpha",
            "成功 ID 不应钉进 RequiredIds（分配器仍可按预算忘掉）。");
        CollectionAssert.DoesNotContain(plan.ExcludedIds.ToList(), "alpha",
            "成功观察的 ID 不是确认不存在，不应进排除集。");
    }

    /// <summary>
    /// 验证：成功 ID 已是单独问句时不再重复加成；失败观察的 ID 只排除、不进问句。
    /// </summary>
    [TestMethod]
    public void Plan_FailedIdReference_OnlyExcluded_AndAlreadyQueriedNotDuplicated()
    {
        var plan = _planner.Plan(new AgentRetrievalPlannerInput
        {
            OriginalTask = "alpha",
            ToolObservations = new[]
            {
                new ToolObservation { ToolName = "lookup", ToolCallId = "t1", Succeeded = false, Error = "未找到 id:missing-01" },
                new ToolObservation { ToolName = "lookup", ToolCallId = "t2", Succeeded = true, Result = "found id:alpha in notes" }
            }
        });

        Assert.IsFalse(
            plan.ControlledQueries.Any(query => query.Reason == "成功工具观察 ID"),
            "任务本身已是该 ID 单独问句时，成功 ID 不应重复加成。");
        CollectionAssert.Contains(plan.ExcludedIds.ToList(), "missing-01",
            "失败观察的 ID 仍只进排除集。");
        Assert.IsFalse(
            plan.ControlledQueries.Any(query =>
                query.Text.Contains("missing-01", StringComparison.Ordinal)),
            "失败观察的 ID 不应变成搜索问句。");
        CollectionAssert.DoesNotContain(plan.RequiredIds.ToList(), "alpha",
            "成功 ID 不钉 RequiredIds。");
    }
}
