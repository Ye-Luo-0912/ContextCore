using ContextCore.Abstractions;
using ContextCore.Core.Services.Retrieval;

namespace ContextCore.Tests;

// ===========================================================================
// Agent 受控检索查询规划器生产验收测试
//
// 验证 P1-8 的受控检索规划设计：
//   1. Plan_ControlledQuerySet_Bounded — 查询集有界（≤ MaxControlledQueries）且
//      按 任务→意图→未解决目标→图种子 的顺序受控生成。
//   2. Plan_RequiredIds_FromExplicitReferences — 从任务/意图/目标提取显式 ID 引用
//      （去重 + 封顶 MaxRequiredIds）。
//   3. Plan_ExcludedIds_FromFailedToolObservations — 失败 Tool 观察推导排除 ID，
//      成功观察不产生排除项（封顶 MaxExcludedIds）。
//   4. Plan_GraphSeeds_FromAnchorsAndTokens — 图种子取引号/书名号内显式锚点，
//      补充长词元，封顶 MaxGraphSeeds。
//   5. Plan_TokenBudget_FromTurnBudget — Token 预算由剩余 Turn 推导并钳制上下限。
//   6. Plan_DiagnosticBackoff_ReducesBudget — 上一轮预算超限时受控回退减半。
//   7. Plan_Deterministic_SameInputSameOutput — 相同输入产生相同计划（幂等）。
//   8. Plan_EmptyTask_ReturnsMinimalControlledPlan — 空任务产出最小受控计划，不抛异常。
//   9. Plan_NullInput_Throws — null 输入抛 ArgumentNullException。
//
// 设计原则：
//   - 使用真实实现 DefaultAgentRetrievalQueryPlanner（纯内存、确定性）。
//   - 断言聚焦受控性质（上限、去重、顺序、回退），不依赖脆弱的具体种子文本。
//   - 中文注释。
// ===========================================================================

[TestClass]
[TestCategory("Agent")]
[TestCategory("Retrieval")]
public sealed class R29M_AgentRetrievalQueryPlannerTests
{
    private readonly IAgentRetrievalQueryPlanner _planner = new DefaultAgentRetrievalQueryPlanner();

    /// <summary>
    /// 验证：查询集受控有界——任务（混合）→ 意图（关键词）→ 未解决目标（向量）
    /// → 图种子锚定查询（关键词），总数不超过 MaxControlledQueries。
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

        // 顺序：原始任务（混合）→ 最新意图（关键词）→ 未解决目标（向量）
        Assert.AreEqual(input.OriginalTask, plan.ControlledQueries[0].Text, "首条查询应为原始任务。");
        Assert.AreEqual(AgentRetrievalQueryType.Hybrid, plan.ControlledQueries[0].Type, "原始任务查询应为混合召回。");
        Assert.AreEqual(input.LatestAssistantIntent, plan.ControlledQueries[1].Text, "第二条查询应为最新意图。");
        Assert.AreEqual(AgentRetrievalQueryType.Keyword, plan.ControlledQueries[1].Type, "意图查询应为关键词召回。");
        Assert.AreEqual(AgentRetrievalQueryType.Vector, plan.ControlledQueries[2].Type, "未解决目标查询应为向量召回。");

        // 图种子锚定查询存在（作为受控补充，不超上限）
        if (plan.ControlledQueries.Count > 3)
        {
            Assert.AreEqual(AgentRetrievalQueryType.Keyword, plan.ControlledQueries[3].Type,
                "图种子锚定查询应为关键词召回。");
            Assert.AreEqual("图种子锚定查询", plan.ControlledQueries[3].Reason,
                "图种子锚定查询的原因应标注来源。");
        }
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

    /// <summary>
    /// 验证：null 输入抛 ArgumentNullException（fail-fast）。
    /// </summary>
    [TestMethod]
    public void Plan_NullInput_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(() => _planner.Plan(null!));
    }
}
