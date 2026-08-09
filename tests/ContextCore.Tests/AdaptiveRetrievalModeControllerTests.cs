using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Service.Endpoints;
using ContextCore.Service.Security;
using ContextCore.Storage.InMemory.Stores;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ContextCore.Tests;

/// <summary>
/// 自适应检索生产开关治理测试（WP-X）：运行时模式切换（Shadow→Active 启用 / 一键回退 Disabled）、
/// 切换审计、planner 动态生效。
/// </summary>
[TestClass]
[TestCategory("R29")]
public sealed class AdaptiveRetrievalModeControllerTests
{
    private const string DefaultWorkspace = "ws-mode";

    [TestMethod]
    public async Task Transition_ShadowToActive_RecordsAudit_AndPlannerApplies()
    {
        var controller = new AdaptiveRetrievalModeController(AdaptiveRetrievalMode.Shadow, "startup");
        var store = new InMemoryRetrievalPlanFeedbackStore();
        var planner = new AdaptiveRetrievalPlanner(
            new DefaultAgentRetrievalQueryPlanner(), store,
            new AdaptiveRetrievalOptions { MinFeedbackSamples = 5 }, controller);

        // 反馈充足（Active 应用前提）。
        var input = Input();
        var signature = AdaptiveRetrievalPlanSignature.Compute(input);
        for (var i = 0; i < 8; i++)
        {
            await store.RecordAsync(Feedback(signature, hits: 0));
        }

        // Shadow：计算不应用（无签名、权重不增强）。
        var shadowPlan = await planner.PlanAsync(input);
        Assert.IsNull(shadowPlan.PlanSignature, "Shadow 模式计划不携带签名（不应用）。");

        // 生产启用：Shadow → Active（经端点）。
        var result = await AdaptiveRetrievalEndpoints.SetModeAsync(controller, Workspace(), new AdaptiveModeSetRequest
        {
            Mode = AdaptiveRetrievalMode.Active,
            Reason = "观察期达标，启用生产"
        });
        var (status, transition) = await ExecuteAsync<AdaptiveModeTransition>(result);
        Assert.AreEqual(StatusCodes.Status200OK, status);
        Assert.AreEqual(AdaptiveRetrievalMode.Shadow, transition!.From, "审计记录来源。");
        Assert.AreEqual(AdaptiveRetrievalMode.Active, transition.To);
        Assert.AreEqual("key-mode", transition.Actor, "审计记录操作者。");
        Assert.AreEqual(AdaptiveRetrievalMode.Active, controller.CurrentMode);

        // Active：planner 动态生效（签名 + 召回增强权重）。
        var activePlan = await planner.PlanAsync(input);
        Assert.AreEqual(signature, activePlan.PlanSignature, "Active 计划携带签名（应用）。");
        var taskQuery = activePlan.ControlledQueries.First(q => q.Text == TaskText);
        Assert.AreEqual(1.25, taskQuery.Weight, 0.0001, "Active 应用召回增强。");
    }

    [TestMethod]
    public async Task OneClickRollback_ToDisabled_FailClosed_StopsLearningSignal()
    {
        var controller = new AdaptiveRetrievalModeController(AdaptiveRetrievalMode.Active, "startup");
        var planner = CreatePlanner(controller);
        var store = new InMemoryRetrievalPlanFeedbackStore();
        var inner = new AdaptiveRetrievalPlanner(new DefaultAgentRetrievalQueryPlanner(), store);
        var signature = AdaptiveRetrievalPlanSignature.Compute(Input());

        // 一键回退：Active → Disabled。
        var result = await AdaptiveRetrievalEndpoints.SetModeAsync(controller, Workspace(), new AdaptiveModeSetRequest
        {
            Mode = AdaptiveRetrievalMode.Disabled,
            Reason = "生产异常，一键回退"
        });
        var (status, transition) = await ExecuteAsync<AdaptiveModeTransition>(result);
        Assert.AreEqual(StatusCodes.Status200OK, status);
        Assert.AreEqual(AdaptiveRetrievalMode.Disabled, transition!.To);

        // Disabled fail-closed：计划透传（无签名），且不写入反馈（无隐式学习）。
        var plan = await planner.PlanAsync(Input());
        Assert.IsNull(plan.PlanSignature, "回退后计划透传（无签名）。");
        await planner.RecordOutcomeAsync(Feedback(signature, hits: 0));
        Assert.AreEqual(0, (await store.ListRecentAsync(DefaultWorkspace, signature)).Count, "回退后不收集学习信号。");

        // 审计历史可查（运维可解释）。
        var history = controller.GetHistory();
        Assert.IsTrue(history.Any(t => t.To == AdaptiveRetrievalMode.Disabled && t.Reason == "生产异常，一键回退"),
            "审计应含回退记录（原因可解释）。");
    }

    [TestMethod]
    public async Task SetMode_InvalidMode_Returns400()
    {
        var controller = new AdaptiveRetrievalModeController();
        var result = await AdaptiveRetrievalEndpoints.SetModeAsync(controller, Workspace(), null!);
        var (status, _) = await ExecuteAsync<ContextCoreErrorResponse>(result);
        Assert.AreEqual(StatusCodes.Status400BadRequest, status);
    }

    [TestMethod]
    public async Task GetMode_ReportsCurrentAndHistory()
    {
        var controller = new AdaptiveRetrievalModeController(AdaptiveRetrievalMode.Disabled, "startup");
        controller.Transition(AdaptiveRetrievalMode.Shadow, "ops-1");
        controller.Transition(AdaptiveRetrievalMode.Active, "ops-2");

        var result = await AdaptiveRetrievalEndpoints.GetModeAsync(controller);
        var (status, response) = await ExecuteAsync<AdaptiveModeStatusResponse>(result);

        Assert.AreEqual(StatusCodes.Status200OK, status);
        Assert.AreEqual(AdaptiveRetrievalMode.Active, response!.CurrentMode);
        Assert.IsTrue(response.History.Count >= 3, "审计含初始化 + 两次切换。");
    }

    // ── 辅助 ─────────────────────────────────────────────────────────────

    private static AdaptiveRetrievalPlanner CreatePlanner(AdaptiveRetrievalModeController controller)
        => new(new DefaultAgentRetrievalQueryPlanner(), new InMemoryRetrievalPlanFeedbackStore(), null, controller);

    private static AgentRetrievalPlannerInput Input() => new()
    {
        OriginalTask = TaskText,
        LatestAssistantIntent = TaskText,
        UnresolvedGoals = new[] { TaskText },
        WorkspaceId = DefaultWorkspace,
        CollectionId = "col-mode",
        Purpose = "agent-context",
        PolicyVersion = "policy/v1",
        RetrievalProfile = "default",
        TaskClass = "analysis",
        TurnBudget = new AgentTurnBudget { MaxTurns = 8, TurnsUsed = 0, MaxModelCalls = 8 }
    };

    private static RetrievalPlanFeedback Feedback(string signature, int hits) => new()
    {
        PlanSignature = signature,
        WorkspaceId = DefaultWorkspace,
        CollectionId = "col-mode",
        Purpose = "agent-context",
        PolicyVersion = "policy/v1",
        RetrievalProfile = "default",
        TaskClass = "analysis",
        QueryText = TaskText,
        HitsReturned = hits,
        Effective = true,
        RecordedAtUtc = DateTimeOffset.UtcNow,
        Source = RetrievalFeedbackSource.Runtime,
        Confidence = 1.0,
        OutcomeQuality = 1.0
    };

    private const string TaskText = "分析季度营收报告并给出下季度预测";

    private static FixedWorkspaceAccessor Workspace() => new();

    private static DefaultHttpContext Http()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.TraceIdentifier = "test-trace";
        httpContext.Response.Body = new MemoryStream();
        httpContext.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        return httpContext;
    }

    private static async Task<(int Status, T? Body)> ExecuteAsync<T>(IResult result) where T : class
    {
        var context = Http();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        var body = await System.Text.Json.JsonSerializer.DeserializeAsync<T>(
            context.Response.Body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        return (context.Response.StatusCode, body);
    }

    private sealed class FixedWorkspaceAccessor : IWorkspaceContextAccessor
    {
        public WorkspaceContext? Current => new()
        {
            WorkspaceId = DefaultWorkspace,
            Source = "test",
            ApiKeyId = "key-mode",
            Roles = new[] { WorkspaceRole.Operator },
            IsAuthenticated = true
        };

        public void Set(WorkspaceContext context) { }

        public void Clear() { }
    }
}
