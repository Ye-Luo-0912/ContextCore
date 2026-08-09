using ContextCore.Abstractions;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// 自适应检索生产形态验收（WP-J）：
/// 1. Shadow → Active 切换：Shadow 计算策略但不应用（计划无签名、预算不变）；
///    Active 应用策略（PlanSignature 填充 + TokenBudget 按乘数调整）——同一条
///    反馈状态下的行为差异可观测；
/// 2. 策略应用可观测：PlanAsync 返回计划与 GetPolicyForSignature 一致
///    （审计/端点可复现同一策略）；
/// 3. Disabled fail-closed：不读写反馈存储（RecordOutcome no-op、计划透传无签名）；
/// 4. 回退语义：反馈不足 → 中性默认 + Note 说明（绝不猜测）。
/// </summary>
[TestClass]
[TestCategory("R29")]
public sealed class R36_AdaptiveRetrievalProductionTests
{
    private const string DefaultWorkspace = "ws-production";
    private const string TaskText = "分析季度营收报告并给出下季度预测";

    [TestMethod]
    public async Task ShadowToActive_Switch_AppliesPolicyOnlyInActive()
    {
        // 同一条反馈状态：Shadow 不应用（预算不变、无签名），Active 应用（预算收缩 + 签名）。
        var input = Input();
        var signature = AdaptiveRetrievalPlanSignature.Compute(input);

        // 阶段 1：Shadow 模式。
        var (shadowPlanner, shadowStore) = CreatePlanner(new AdaptiveRetrievalOptions
        {
            Mode = AdaptiveRetrievalMode.Shadow,
            MinFeedbackSamples = 5
        });
        for (var i = 0; i < 8; i++)
        {
            await shadowStore.RecordAsync(Feedback(signature, hits: 4, budgetExceeded: true));
        }
        var shadowPlan = await shadowPlanner.PlanAsync(input);
        Assert.AreEqual(8192, shadowPlan.TokenBudget, "Shadow 不应用预算乘数（8192 基础预算）。");
        Assert.IsNull(shadowPlan.PlanSignature, "Shadow 计划不携带自适应签名（未应用）。");

        // 策略已计算（Shadow 也读反馈计算策略，仅不应用）——可观测。
        var shadowPolicy = await shadowPlanner.GetPolicyForSignatureAsync(DefaultWorkspace, signature);
        Assert.AreEqual(0.75, shadowPolicy.TokenBudgetMultiplier, 0.0001, "Shadow 照常计算策略（观察学习信号）。");

        // 阶段 2：Active 模式（同反馈状态）。
        var (activePlanner, activeStore) = CreatePlanner(new AdaptiveRetrievalOptions
        {
            Mode = AdaptiveRetrievalMode.Active,
            MinFeedbackSamples = 5
        });
        for (var i = 0; i < 8; i++)
        {
            await activeStore.RecordAsync(Feedback(signature, hits: 4, budgetExceeded: true));
        }
        var activePlan = await activePlanner.PlanAsync(input);
        Assert.AreEqual(6144, activePlan.TokenBudget, "Active 应用预算乘数：8192 × 0.75 = 6144。");
        Assert.AreEqual(signature, activePlan.PlanSignature, "Active 计划携带自适应签名（可审计）。");
        StringAssert.Contains(activePlan.Reason, "[自适应]");
    }

    [TestMethod]
    public async Task Active_PlanSignature_MatchesPolicyQuery()
    {
        // 可观测性：PlanAsync 应用的计划与 GetPolicyForSignature 返回的策略一致
        // （运维端点可复现同一策略状态）。
        var (planner, store) = CreatePlanner(new AdaptiveRetrievalOptions
        {
            Mode = AdaptiveRetrievalMode.Active,
            MinFeedbackSamples = 5
        });
        var input = Input();
        var signature = AdaptiveRetrievalPlanSignature.Compute(input);
        for (var i = 0; i < 8; i++)
        {
            await store.RecordAsync(Feedback(signature, hits: 0));
        }

        var plan = await planner.PlanAsync(input);
        var policy = await planner.GetPolicyForSignatureAsync(DefaultWorkspace, signature);

        Assert.AreEqual(signature, plan.PlanSignature, "计划签名与策略签名一致。");
        Assert.AreEqual(signature, policy.PlanSignature);
        Assert.AreEqual(1.25, policy.RecallBoostMultiplier, 0.0001, "命中偏低 → 召回增强 1.25。");
        // 计划查询权重已应用召回增强（1.0 × 1.25）。
        var taskQuery = plan.ControlledQueries.First(q => q.Text == TaskText);
        Assert.AreEqual(1.25, taskQuery.Weight, 0.0001, "Active 计划真实应用召回增强权重。");
    }

    [TestMethod]
    public async Task Disabled_DoesNotTouchFeedbackStore_AndPassesThroughPlan()
    {
        // Disabled fail-closed：不读写反馈存储；计划透传（无签名、无调整）。
        var (planner, store) = CreatePlanner(new AdaptiveRetrievalOptions
        {
            Mode = AdaptiveRetrievalMode.Disabled,
            MinFeedbackSamples = 1
        });
        var input = Input();

        // 记录反馈（Disabled 下 RecordOutcome 应 no-op，不写入存储）。
        await planner.RecordOutcomeAsync(Feedback(
            AdaptiveRetrievalPlanSignature.Compute(input), hits: 0, budgetExceeded: true));

        var plan = await planner.PlanAsync(input);
        Assert.AreEqual(8192, plan.TokenBudget, "Disabled 透传基础计划（不应用策略）。");
        Assert.IsNull(plan.PlanSignature, "Disabled 计划无自适应签名。");

        var stored = await store.ListRecentAsync(DefaultWorkspace, AdaptiveRetrievalPlanSignature.Compute(input));
        Assert.AreEqual(0, stored.Count, "Disabled 模式不写入反馈存储（fail-closed，无隐式学习）。");
    }

    [TestMethod]
    public async Task Active_InsufficientFeedback_FallsBackNeutralWithNote()
    {
        // 回退语义：反馈不足 → 中性默认（不猜测），Note 说明样本不足。
        var (planner, store) = CreatePlanner(new AdaptiveRetrievalOptions
        {
            Mode = AdaptiveRetrievalMode.Active,
            MinFeedbackSamples = 10
        });
        var input = Input();
        var signature = AdaptiveRetrievalPlanSignature.Compute(input);
        for (var i = 0; i < 3; i++)
        {
            await store.RecordAsync(Feedback(signature, hits: 0, budgetExceeded: true));
        }

        var plan = await planner.PlanAsync(input);
        var policy = await planner.GetPolicyForSignatureAsync(DefaultWorkspace, signature);

        Assert.AreEqual(8192, plan.TokenBudget, "样本不足 → 中性预算（不收缩）。");
        Assert.AreEqual(1.0, policy.TokenBudgetMultiplier, 0.0001, "样本不足 → 中性乘数。");
        Assert.AreEqual(1.0, policy.RecallBoostMultiplier, 0.0001, "样本不足 → 不增强召回。");
        Assert.AreEqual(3, policy.FeedbackSampleCount);
        StringAssert.Contains(policy.Note, "样本不足", "Note 说明中性原因（审计可解释）。");
    }

    // ── 辅助 ─────────────────────────────────────────────────────────────

    private static (AdaptiveRetrievalPlanner Planner, InMemoryRetrievalPlanFeedbackStore Store) CreatePlanner(
        AdaptiveRetrievalOptions options)
    {
        var store = new InMemoryRetrievalPlanFeedbackStore();
        var planner = new AdaptiveRetrievalPlanner(new DefaultAgentRetrievalQueryPlanner(), store, options);
        return (planner, store);
    }

    private static AgentRetrievalPlannerInput Input() => new()
    {
        OriginalTask = TaskText,
        LatestAssistantIntent = TaskText,
        UnresolvedGoals = new[] { TaskText },
        WorkspaceId = DefaultWorkspace,
        CollectionId = "col-production",
        Purpose = "agent-context",
        PolicyVersion = "policy/v1",
        RetrievalProfile = "default",
        TaskClass = "analysis",
        TurnBudget = new AgentTurnBudget { MaxTurns = 8, TurnsUsed = 0, MaxModelCalls = 8 }
    };

    private static RetrievalPlanFeedback Feedback(
        string signature, int hits, bool budgetExceeded = false) => new()
    {
        PlanSignature = signature,
        WorkspaceId = DefaultWorkspace,
        CollectionId = "col-production",
        Purpose = "agent-context",
        PolicyVersion = "policy/v1",
        RetrievalProfile = "default",
        TaskClass = "analysis",
        QueryText = TaskText,
        HitsReturned = hits,
        BudgetExceeded = budgetExceeded,
        Effective = true,
        RecordedAtUtc = DateTimeOffset.UtcNow,
        Source = RetrievalFeedbackSource.Runtime,
        Confidence = 1.0,
        OutcomeQuality = 1.0
    };
}
