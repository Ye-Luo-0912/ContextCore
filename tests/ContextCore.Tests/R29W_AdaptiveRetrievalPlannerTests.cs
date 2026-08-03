using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Service.Endpoints;
using ContextCore.Storage.InMemory.Stores;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.Tests;

// ===========================================================================
// Adaptive Retrieval Planner —— 自适应检索规划器验收测试
//
// 覆盖范围：
// 1. IRetrievalPlanFeedbackStore（InMemory）：record / list（倒序）/ clear（全部 + 按签名）；
// 2. AdaptiveRetrievalPlanSignature：确定性 / 不同输入不同签名；
// 3. 策略计算：无反馈中性 / 预算超限收敛 / 低命中召回增强 / 样本不足中性；
// 4. 规划器自适应：预算乘数应用与钳制、查询收敛（按权重保留）、召回权重增强、
// 确定性（相同输入 + 相同反馈状态 → 相同计划）、反馈累积后策略生效；
// 5. 迁移 SQL：retrieval_plan_feedback 表 + 签名索引 + RequiredOperationalTableSuffixes；
// 6. 端点处理器：policy / feedback 列表 / 记录反馈 / reset 的状态码与响应形状。
//
// 不连接真实数据库：规划器只依赖 IRetrievalPlanFeedbackStore 接口（InMemory 实现），
// Postgres 侧 SQL 路径由集成测试（ContextCore.IntegrationTests）覆盖。
// ===========================================================================

[TestClass]
[TestCategory("Storage")]
[TestCategory("R29")]
public sealed class R29W_AdaptiveRetrievalPlannerTests
{
    private const string TaskText = "分析 「AlphaProtocol」 的部署状态并整理修复建议";
    private const string IntentText = "先查 AlphaProtocol 的配置项";

    // 服务器响应为 camelCase（Results.Ok 使用 web 默认序列化选项）。
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    // =========================================================================
    // Part 1: IRetrievalPlanFeedbackStore（InMemory）
    // =========================================================================

    [TestMethod]
    public async Task InMemoryStore_Record_ListRecent_ReturnsNewestFirst()
    {
        var store = new InMemoryRetrievalPlanFeedbackStore();
        var signature = "sig:test-1";
        await store.RecordAsync(Feedback(signature, hits: 3, recordedAt: DateTimeOffset.UtcNow.AddMinutes(-2)));
        await store.RecordAsync(Feedback(signature, hits: 1, recordedAt: DateTimeOffset.UtcNow.AddMinutes(-1)));
        await store.RecordAsync(Feedback(signature, hits: 5, recordedAt: DateTimeOffset.UtcNow));

        var entries = await store.ListRecentAsync(signature, limit: 10);

        Assert.AreEqual(3, entries.Count);
        Assert.AreEqual(5, entries[0].HitsReturned, "最新记录应排在最前。");
        Assert.AreEqual(3, entries[2].HitsReturned);
    }

    [TestMethod]
    public async Task InMemoryStore_ListRecent_EmptyWhenNoRecords()
    {
        var store = new InMemoryRetrievalPlanFeedbackStore();

        var entries = await store.ListRecentAsync("sig:missing");

        Assert.AreEqual(0, entries.Count);
    }

    [TestMethod]
    public async Task InMemoryStore_ClearBySignature_RemovesOnlyThatSignature()
    {
        var store = new InMemoryRetrievalPlanFeedbackStore();
        await store.RecordAsync(Feedback("sig:a", hits: 1));
        await store.RecordAsync(Feedback("sig:b", hits: 2));

        var cleared = await store.ClearAsync("sig:a");

        Assert.AreEqual(1, cleared);
        Assert.AreEqual(0, (await store.ListRecentAsync("sig:a")).Count);
        Assert.AreEqual(1, (await store.ListRecentAsync("sig:b")).Count);
    }

    [TestMethod]
    public async Task InMemoryStore_ClearAll_RemovesEverything()
    {
        var store = new InMemoryRetrievalPlanFeedbackStore();
        await store.RecordAsync(Feedback("sig:a", hits: 1));
        await store.RecordAsync(Feedback("sig:b", hits: 2));

        var cleared = await store.ClearAsync();

        Assert.AreEqual(2, cleared);
        Assert.AreEqual(0, (await store.ListRecentAsync("sig:a")).Count);
        Assert.AreEqual(0, (await store.ListRecentAsync("sig:b")).Count);
    }

    // =========================================================================
    // Part 2: 计划签名
    // =========================================================================

    [TestMethod]
    public void Signature_IsDeterministic_AndDiffersByInput()
    {
        var input = Input();
        var same = Input();

        var s1 = AdaptiveRetrievalPlanSignature.Compute(input);
        var s2 = AdaptiveRetrievalPlanSignature.Compute(same);
        var other = AdaptiveRetrievalPlanSignature.Compute(Input(originalTask: "完全不同的任务"));

        Assert.AreEqual(s1, s2, "相同输入应产生相同签名。");
        Assert.AreNotEqual(s1, other, "不同输入应产生不同签名。");
        StringAssert.StartsWith(s1, "sig:");
    }

    // =========================================================================
    // Part 3: 策略计算
    // =========================================================================

    [TestMethod]
    public async Task Policy_NoFeedback_IsNeutral()
    {
        var (planner, _) = CreatePlanner();
        var signature = AdaptiveRetrievalPlanSignature.Compute(Input());

        var policy = await planner.GetPolicyForSignatureAsync(signature);

        Assert.AreEqual(1.0, policy.TokenBudgetMultiplier);
        Assert.AreEqual(1.0, policy.QueryConvergenceMultiplier);
        Assert.AreEqual(1.0, policy.RecallBoostMultiplier);
        Assert.AreEqual(0, policy.FeedbackSampleCount);
    }

    [TestMethod]
    public async Task Policy_BudgetExceeded_Converges()
    {
        var (planner, store) = CreatePlanner();
        var signature = AdaptiveRetrievalPlanSignature.Compute(Input());
        for (var i = 0; i < 3; i++)
        {
            await store.RecordAsync(Feedback(signature, hits: 4, budgetExceeded: true));
        }

        var policy = await planner.GetPolicyForSignatureAsync(signature);

        Assert.AreEqual(0.75, policy.TokenBudgetMultiplier);
        Assert.AreEqual(0.75, policy.QueryConvergenceMultiplier);
        Assert.AreEqual(1.0, policy.RecallBoostMultiplier);
    }

    [TestMethod]
    public async Task Policy_LowHits_BoostsRecall()
    {
        var (planner, store) = CreatePlanner();
        var signature = AdaptiveRetrievalPlanSignature.Compute(Input());
        for (var i = 0; i < 3; i++)
        {
            await store.RecordAsync(Feedback(signature, hits: 0));
        }

        var policy = await planner.GetPolicyForSignatureAsync(signature);

        Assert.AreEqual(1.0, policy.TokenBudgetMultiplier);
        Assert.AreEqual(1.0, policy.QueryConvergenceMultiplier);
        Assert.AreEqual(1.25, policy.RecallBoostMultiplier);
    }

    [TestMethod]
    public async Task Policy_InsufficientSamples_IsNeutral()
    {
        var (planner, store) = CreatePlanner();
        var signature = AdaptiveRetrievalPlanSignature.Compute(Input());
        await store.RecordAsync(Feedback(signature, hits: 4, budgetExceeded: true));
        await store.RecordAsync(Feedback(signature, hits: 4, budgetExceeded: true));

        var policy = await planner.GetPolicyForSignatureAsync(signature);

        Assert.AreEqual(1.0, policy.TokenBudgetMultiplier, "样本数 < 3 时不应触发收敛。");
        Assert.AreEqual(2, policy.FeedbackSampleCount);
    }

    // =========================================================================
    // Part 4: 规划器自适应（PlanAsync）
    // =========================================================================

    [TestMethod]
    public async Task Plan_NoFeedback_ReturnsBasePlan()
    {
        var (planner, _) = CreatePlanner();
        var input = Input();

        var plan = await planner.PlanAsync(input);

        Assert.AreEqual(4096, plan.TokenBudget, "TurnBudget Remaining=4 → 基础预算 4096（无自适应调整）。");
        Assert.AreEqual(4, plan.ControlledQueries.Count);
        Assert.IsFalse(plan.Reason.Contains("[自适应]", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Plan_BudgetExceeded_ReducesBudgetAndConvergesQueries()
    {
        var (planner, store) = CreatePlanner();
        var input = Input();
        var signature = AdaptiveRetrievalPlanSignature.Compute(input);
        for (var i = 0; i < 3; i++)
        {
            await store.RecordAsync(Feedback(signature, hits: 4, budgetExceeded: true));
        }

        var plan = await planner.PlanAsync(input);

        Assert.AreEqual(3072, plan.TokenBudget, "4096 × 0.75 = 3072。");
        Assert.AreEqual(3, plan.ControlledQueries.Count, "查询收敛：4 条 → 保留 3 条。");
        Assert.IsFalse(plan.ControlledQueries.Any(q => q.Text == "AlphaProtocol"),
            "收敛应丢弃权重最低的图种子锚定查询。");
        StringAssert.Contains(plan.Reason, "[自适应]");
    }

    [TestMethod]
    public async Task Plan_BudgetClamped_AtMinTokenBudget()
    {
        var (planner, store) = CreatePlanner();
        var input = Input(remainingTurns: 1);
        var signature = AdaptiveRetrievalPlanSignature.Compute(input);
        for (var i = 0; i < 3; i++)
        {
            await store.RecordAsync(Feedback(signature, hits: 4, budgetExceeded: true));
        }

        var plan = await planner.PlanAsync(input);

        // 基础预算：1 × 1024 = 1024（无诊断回退）→ ×0.75 = 768；用诊断回退的输入确保钳制路径。
        Assert.IsTrue(plan.TokenBudget >= DefaultAgentRetrievalQueryPlanner.MinTokenBudget,
            "Token 预算不应低于最小预算 512。");
    }

    [TestMethod]
    public async Task Plan_LowHits_BoostsQueryWeights()
    {
        var (planner, store) = CreatePlanner();
        var input = Input();
        var signature = AdaptiveRetrievalPlanSignature.Compute(input);
        for (var i = 0; i < 3; i++)
        {
            await store.RecordAsync(Feedback(signature, hits: 0));
        }

        var plan = await planner.PlanAsync(input);

        var taskQuery = plan.ControlledQueries.Single(q => q.Text == TaskText);
        Assert.AreEqual(1.25, taskQuery.Weight, "基础权重 1.0 × 1.25 = 1.25。");
    }

    [TestMethod]
    public async Task Plan_Deterministic_GivenSameFeedbackState()
    {
        var (planner, store) = CreatePlanner();
        var input = Input();
        var signature = AdaptiveRetrievalPlanSignature.Compute(input);
        for (var i = 0; i < 3; i++)
        {
            await store.RecordAsync(Feedback(signature, hits: 4, budgetExceeded: true));
        }

        var plan1 = await planner.PlanAsync(input);
        var plan2 = await planner.PlanAsync(input);

        Assert.AreEqual(plan1.TokenBudget, plan2.TokenBudget);
        Assert.AreEqual(plan1.ControlledQueries.Count, plan2.ControlledQueries.Count);
        CollectionAssert.AreEqual(
            plan1.ControlledQueries.Select(q => q.Text).ToArray(),
            plan2.ControlledQueries.Select(q => q.Text).ToArray());
    }

    [TestMethod]
    public async Task Plan_FeedbackAccumulates_ThenPolicyApplies()
    {
        var (planner, store) = CreatePlanner();
        var input = Input();
        var signature = AdaptiveRetrievalPlanSignature.Compute(input);

        var before = await planner.PlanAsync(input);
        Assert.AreEqual(4096, before.TokenBudget);

        for (var i = 0; i < 3; i++)
        {
            await planner.RecordOutcomeAsync(Feedback(signature, hits: 4, budgetExceeded: true));
        }

        var after = await planner.PlanAsync(input);
        Assert.AreEqual(3072, after.TokenBudget, "反馈累积 3 条后策略应生效。");
        Assert.AreEqual(3, (await store.ListRecentAsync(signature)).Count);
    }

    // =========================================================================
    // Part 5: 迁移 SQL
    // =========================================================================

    [TestMethod]
    public void MigrationSql_IncludesRetrievalPlanFeedbackTable()
    {
        var sql = BuildSql();

        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_retrieval_plan_feedback");
        StringAssert.Contains(sql, "plan_signature text NOT NULL");
        StringAssert.Contains(sql, "hits_returned integer NOT NULL DEFAULT 0");
        StringAssert.Contains(sql, "budget_exceeded boolean NOT NULL DEFAULT false");
        StringAssert.Contains(sql, "retrieval_plan_feedback_signature");
    }

    [TestMethod]
    public void RequiredOperationalTables_IncludeRetrievalPlanFeedback()
    {
        CollectionAssert.Contains(
            PostgresMigrationRunner.RequiredOperationalTableSuffixes.ToList(),
            "retrieval_plan_feedback");
    }

    // =========================================================================
    // Part 6: 端点处理器
    // =========================================================================

    [TestMethod]
    public async Task PolicyEndpoint_NoPlanner_Returns503()
    {
        var (status, _) = await ExecuteAsync(await AdaptiveRetrievalEndpoints.GetPolicyAsync(
            planner: null, signature: "sig:x", originalTask: null, latestAssistantIntent: null, goals: null, Http(), CancellationToken.None));

        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, status);
    }

    [TestMethod]
    public async Task PolicyEndpoint_ReturnsPolicy()
    {
        var (planner, _) = CreatePlanner();

        var (status, body) = await ExecuteAsync(await AdaptiveRetrievalEndpoints.GetPolicyAsync(
            planner, signature: null, originalTask: TaskText, latestAssistantIntent: IntentText, goals: null, Http(), CancellationToken.None));

        Assert.AreEqual(StatusCodes.Status200OK, status);
        var policy = JsonSerializer.Deserialize<AdaptiveRetrievalPolicy>(body, JsonWeb);
        Assert.IsNotNull(policy);
        Assert.AreEqual(1.0, policy!.TokenBudgetMultiplier);
    }

    [TestMethod]
    public async Task FeedbackListEndpoint_BlankSignature_Returns400()
    {
        var (planner, _) = CreatePlanner();

        var (status, _) = await ExecuteAsync(await AdaptiveRetrievalEndpoints.ListFeedbackAsync(
            planner, signature: "  ", limit: 10, Http(), CancellationToken.None));

        Assert.AreEqual(StatusCodes.Status400BadRequest, status);
    }

    [TestMethod]
    public async Task FeedbackListEndpoint_ReturnsEntries()
    {
        var (planner, store) = CreatePlanner();
        var signature = AdaptiveRetrievalPlanSignature.Compute(Input());
        await store.RecordAsync(Feedback(signature, hits: 2));

        var (status, body) = await ExecuteAsync(await AdaptiveRetrievalEndpoints.ListFeedbackAsync(
            planner, signature: signature, limit: 10, Http(), CancellationToken.None));

        Assert.AreEqual(StatusCodes.Status200OK, status);
        var response = JsonSerializer.Deserialize<AdaptiveRetrievalFeedbackListResponse>(body, JsonWeb);
        Assert.IsNotNull(response);
        Assert.AreEqual(1, response!.Count);
    }

    [TestMethod]
    public async Task RecordFeedbackEndpoint_BlankSignature_Returns400()
    {
        var (planner, _) = CreatePlanner();

        var (status, _) = await ExecuteAsync(await AdaptiveRetrievalEndpoints.RecordFeedbackAsync(
            planner, new RecordAdaptiveRetrievalFeedbackRequest { PlanSignature = "  " }, Http(), CancellationToken.None));

        Assert.AreEqual(StatusCodes.Status400BadRequest, status);
    }

    [TestMethod]
    public async Task RecordFeedbackEndpoint_RecordsAndReturns200()
    {
        var (planner, store) = CreatePlanner();
        var signature = "sig:record-test";

        var (status, body) = await ExecuteAsync(await AdaptiveRetrievalEndpoints.RecordFeedbackAsync(
            planner, new RecordAdaptiveRetrievalFeedbackRequest
            {
                PlanSignature = signature,
                QueryText = "测试查询",
                HitsReturned = 3,
                BudgetExceeded = true
            }, Http(), CancellationToken.None));

        Assert.AreEqual(StatusCodes.Status200OK, status);
        var recorded = JsonSerializer.Deserialize<AdaptiveRetrievalFeedbackRecordResponse>(body, JsonWeb);
        Assert.IsTrue(recorded!.Recorded);
        var stored = await store.ListRecentAsync(signature);
        Assert.AreEqual(1, stored.Count);
        Assert.AreEqual(3, stored[0].HitsReturned);
        Assert.IsTrue(stored[0].BudgetExceeded);
    }

    [TestMethod]
    public async Task ResetEndpoint_NoPlanner_Returns503()
    {
        var (status, _) = await ExecuteAsync(await AdaptiveRetrievalEndpoints.ResetAsync(
            planner: null, signature: null, Http(), CancellationToken.None));

        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, status);
    }

    [TestMethod]
    public async Task ResetEndpoint_ClearsAndReturnsCount()
    {
        var (planner, store) = CreatePlanner();
        var signature = "sig:reset-test";
        await store.RecordAsync(Feedback(signature, hits: 1));

        var (status, body) = await ExecuteAsync(await AdaptiveRetrievalEndpoints.ResetAsync(
            planner, signature: signature, Http(), CancellationToken.None));

        Assert.AreEqual(StatusCodes.Status200OK, status);
        var response = JsonSerializer.Deserialize<AdaptiveRetrievalResetResponse>(body, JsonWeb);
        Assert.AreEqual(1, response!.Cleared);
        Assert.AreEqual(signature, response.Scope);
        Assert.AreEqual(0, (await store.ListRecentAsync(signature)).Count);
    }

    // =========================================================================
    // 辅助
    // =========================================================================

    private static (AdaptiveRetrievalPlanner Planner, InMemoryRetrievalPlanFeedbackStore Store) CreatePlanner()
    {
        var store = new InMemoryRetrievalPlanFeedbackStore();
        var planner = new AdaptiveRetrievalPlanner(new DefaultAgentRetrievalQueryPlanner(), store);
        return (planner, store);
    }

    private static AgentRetrievalPlannerInput Input(
        string? originalTask = null,
        int? remainingTurns = null)
    {
        var input = new AgentRetrievalPlannerInput
        {
            OriginalTask = originalTask ?? TaskText,
            LatestAssistantIntent = IntentText,
            UnresolvedGoals = new[] { "确认 AlphaProtocol 是否已部署" }
        };
        if (remainingTurns is not null)
        {
            input = input with { TurnBudget = new AgentTurnBudget { MaxTurns = remainingTurns.Value, TurnsUsed = 0 } };
        }
        return input;
    }

    private static RetrievalPlanFeedback Feedback(
        string signature,
        int hits,
        bool budgetExceeded = false,
        DateTimeOffset? recordedAt = null) => new()
    {
        PlanSignature = signature,
        QueryText = "查询文本",
        HitsReturned = hits,
        BudgetExceeded = budgetExceeded,
        Effective = true,
        RecordedAtUtc = recordedAt ?? DateTimeOffset.UtcNow
    };

    private static string BuildSql() => PostgresMigrationRunner.BuildMigrationSql(new PostgresOptions
    {
        ConnectionString = "Host=localhost;Database=contextcore;Username=contextcore;Password=contextcore",
        TablePrefix = "cc_",
        EnablePgVectorExtension = true
    });

    private static DefaultHttpContext Http()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.TraceIdentifier = "test-trace";
        httpContext.Response.Body = new MemoryStream();
        // .NET 10 的 Ok<T>/JsonHttpResult<T>.ExecuteAsync 需要从 RequestServices 解析 ILoggerFactory。
        httpContext.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        return httpContext;
    }

    private static async Task<(int Status, string Body)> ExecuteAsync(IResult result)
    {
        var httpContext = Http();
        await result.ExecuteAsync(httpContext);
        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body);
        var body = await reader.ReadToEndAsync();
        return ((int)httpContext.Response.StatusCode, body);
    }
}
