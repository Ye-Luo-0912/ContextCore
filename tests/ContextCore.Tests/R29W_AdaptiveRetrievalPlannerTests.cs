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
        for (var i = 0; i < 10; i++)
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
        for (var i = 0; i < 10; i++)
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

        Assert.AreEqual(1.0, policy.TokenBudgetMultiplier, "样本数 < 10 时不应触发收敛。");
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
        var (planner, store) = CreatePlanner(new AdaptiveRetrievalOptions { Mode = AdaptiveRetrievalMode.Active });
        var input = Input();
        var signature = AdaptiveRetrievalPlanSignature.Compute(input);
        for (var i = 0; i < 10; i++)
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
        var (planner, store) = CreatePlanner(new AdaptiveRetrievalOptions { Mode = AdaptiveRetrievalMode.Active });
        var input = Input(remainingTurns: 1);
        var signature = AdaptiveRetrievalPlanSignature.Compute(input);
        for (var i = 0; i < 10; i++)
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
        var (planner, store) = CreatePlanner(new AdaptiveRetrievalOptions { Mode = AdaptiveRetrievalMode.Active });
        var input = Input();
        var signature = AdaptiveRetrievalPlanSignature.Compute(input);
        for (var i = 0; i < 10; i++)
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
        var (planner, store) = CreatePlanner(new AdaptiveRetrievalOptions { Mode = AdaptiveRetrievalMode.Active });
        var input = Input();
        var signature = AdaptiveRetrievalPlanSignature.Compute(input);
        for (var i = 0; i < 10; i++)
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
        var (planner, store) = CreatePlanner(new AdaptiveRetrievalOptions { Mode = AdaptiveRetrievalMode.Active });
        var input = Input();
        var signature = AdaptiveRetrievalPlanSignature.Compute(input);

        var before = await planner.PlanAsync(input);
        Assert.AreEqual(4096, before.TokenBudget);

        for (var i = 0; i < 10; i++)
        {
            await planner.RecordOutcomeAsync(Feedback(signature, hits: 4, budgetExceeded: true));
        }

        var after = await planner.PlanAsync(input);
        Assert.AreEqual(3072, after.TokenBudget, "反馈累积 10 条后策略应生效。");
        Assert.AreEqual(10, (await store.ListRecentAsync(signature)).Count);
    }

    // =========================================================================
    // Part 4b: P0-16 加固（租户隔离签名 / 模式 / 清洗 / 幂等 / 可信度加权）
    // =========================================================================

    [TestMethod]
    public void Signature_IsSha256_AndIsolatesWorkspaces()
    {
        var sA = AdaptiveRetrievalPlanSignature.Compute(Input(workspaceId: "ws-1"));
        var sB = AdaptiveRetrievalPlanSignature.Compute(Input(workspaceId: "ws-2"));

        Assert.AreEqual(4 + 64, sA.Length, "SHA-256 输出应为 sig: + 64 位小写十六进制。");
        StringAssert.StartsWith(sA, "sig:");
        Assert.AreNotEqual(sA, sB, "相同任务文本、不同 Workspace 必须产生不同签名（跨租户隔离）。");
    }

    [TestMethod]
    public void Signature_AllTenantDimensions_AreIncluded()
    {
        // 每个租户维度单独变化都应改变签名。
        Assert.AreNotEqual(
            AdaptiveRetrievalPlanSignature.Compute(Input(collectionId: "col-1")),
            AdaptiveRetrievalPlanSignature.Compute(Input(collectionId: "col-2")));
        Assert.AreNotEqual(
            AdaptiveRetrievalPlanSignature.Compute(Input(purpose: "p1")),
            AdaptiveRetrievalPlanSignature.Compute(Input(purpose: "p2")));
        Assert.AreNotEqual(
            AdaptiveRetrievalPlanSignature.Compute(Input(policyVersion: "v1")),
            AdaptiveRetrievalPlanSignature.Compute(Input(policyVersion: "v2")));
        Assert.AreNotEqual(
            AdaptiveRetrievalPlanSignature.Compute(Input(retrievalProfile: "r1")),
            AdaptiveRetrievalPlanSignature.Compute(Input(retrievalProfile: "r2")));
        Assert.AreNotEqual(
            AdaptiveRetrievalPlanSignature.Compute(Input(taskClass: "t1")),
            AdaptiveRetrievalPlanSignature.Compute(Input(taskClass: "t2")));
    }

    [TestMethod]
    public async Task Plan_DisabledMode_ReturnsBasePlan_IgnoringFeedback()
    {
        var (planner, store) = CreatePlanner(new AdaptiveRetrievalOptions { Mode = AdaptiveRetrievalMode.Disabled });
        var input = Input();
        var signature = AdaptiveRetrievalPlanSignature.Compute(input);
        for (var i = 0; i < 10; i++)
        {
            await store.RecordAsync(Feedback(signature, hits: 4, budgetExceeded: true));
        }

        var plan = await planner.PlanAsync(input);

        Assert.AreEqual(4096, plan.TokenBudget, "Disabled 模式（默认 fail-closed）不应应用自适应策略。");
        Assert.AreEqual(4, plan.ControlledQueries.Count);
        Assert.IsFalse(plan.Reason.Contains("[自适应]", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Plan_ShadowMode_ComputesPolicyButDoesNotApply()
    {
        var (planner, store) = CreatePlanner(new AdaptiveRetrievalOptions { Mode = AdaptiveRetrievalMode.Shadow });
        var input = Input();
        var signature = AdaptiveRetrievalPlanSignature.Compute(input);
        for (var i = 0; i < 10; i++)
        {
            await store.RecordAsync(Feedback(signature, hits: 4, budgetExceeded: true));
        }

        var plan = await planner.PlanAsync(input);
        var policy = await planner.GetPolicyForSignatureAsync(signature);

        Assert.AreEqual(4096, plan.TokenBudget, "Shadow 模式只观察，不应用策略。");
        Assert.AreEqual(4, plan.ControlledQueries.Count);
        Assert.AreEqual(0.75, policy.TokenBudgetMultiplier, "Shadow 模式仍应计算策略（观察学习信号）。");
    }

    [TestMethod]
    public async Task Planner_SanitizesFeedback_OnRecord()
    {
        var (planner, store) = CreatePlanner();
        const string signature = "sig:sanitize";

        await planner.RecordOutcomeAsync(new RetrievalPlanFeedback
        {
            PlanSignature = signature,
            HitsReturned = 100000,
            BudgetExceeded = true,
            Effective = true,
            RecordedAtUtc = DateTimeOffset.UtcNow,
            Confidence = 5.0,
            OutcomeQuality = -2.0,
            Source = (RetrievalFeedbackSource)999
        });

        var stored = await store.ListRecentAsync(signature);
        Assert.AreEqual(1, stored.Count);
        Assert.AreEqual(100, stored[0].HitsReturned, "命中数应钳制到 MaxHitsClamp=100。");
        Assert.AreEqual(1.0, stored[0].Confidence, "置信度应钳制到 [0,1]。");
        Assert.AreEqual(0.0, stored[0].OutcomeQuality, "结果质量应钳制到 [0,1]。");
        Assert.AreEqual(RetrievalFeedbackSource.Runtime, stored[0].Source, "非法 Source 应回退 Runtime。");
        Assert.IsFalse(string.IsNullOrWhiteSpace(stored[0].FeedbackId), "缺省 FeedbackId 应由规划器生成。");
    }

    [TestMethod]
    public async Task Store_Dedupe_ByIdempotencyKey_KeepsFirst()
    {
        var store = new InMemoryRetrievalPlanFeedbackStore();
        const string signature = "sig:dedupe";
        await store.RecordAsync(Feedback(signature, hits: 3, idempotencyKey: "op-1", recordedAt: DateTimeOffset.UtcNow.AddMinutes(-1)));
        await store.RecordAsync(Feedback(signature, hits: 9, idempotencyKey: "op-1", recordedAt: DateTimeOffset.UtcNow));

        var entries = await store.ListRecentAsync(signature);

        Assert.AreEqual(1, entries.Count, "相同 (PlanSignature, IdempotencyKey) 只保留首条（重放无副作用）。");
        Assert.AreEqual(3, entries[0].HitsReturned);
    }

    [TestMethod]
    public async Task Policy_EffectiveOnly_IgnoresIneffectiveSamples()
    {
        var (planner, store) = CreatePlanner();
        var signature = AdaptiveRetrievalPlanSignature.Compute(Input());
        for (var i = 0; i < 10; i++)
        {
            await store.RecordAsync(Feedback(signature, hits: 4, budgetExceeded: true, effective: false));
        }
        for (var i = 0; i < 3; i++)
        {
            await store.RecordAsync(Feedback(signature, hits: 5));
        }

        var policy = await planner.GetPolicyForSignatureAsync(signature);

        Assert.AreEqual(1.0, policy.TokenBudgetMultiplier, "Ineffective 样本不得参与策略计算。");
        Assert.AreEqual(3, policy.FeedbackSampleCount, "只有 Effective 样本计入样本数。");
    }

    [TestMethod]
    public async Task Policy_TimeDecay_DampensOldSamples()
    {
        var (planner, store) = CreatePlanner();
        var signature = AdaptiveRetrievalPlanSignature.Compute(Input());
        var now = DateTimeOffset.UtcNow;
        // 10 条 10 天前的超限样本（衰减 0.5^10 ≈ 0.001）+ 1 条最新的健康样本。
        for (var i = 0; i < 10; i++)
        {
            await store.RecordAsync(Feedback(signature, hits: 4, budgetExceeded: true, recordedAt: now.AddDays(-10)));
        }
        await store.RecordAsync(Feedback(signature, hits: 5, recordedAt: now));

        var policy = await planner.GetPolicyForSignatureAsync(signature);

        Assert.AreEqual(1.0, policy.TokenBudgetMultiplier, "陈旧超限样本经时间衰减后不应触发收敛。");
        Assert.AreEqual(11, policy.FeedbackSampleCount);
    }

    [TestMethod]
    public async Task Policy_PerSubjectCap_LimitsSingleSourceContribution()
    {
        var (planner, store) = CreatePlanner();
        var signature = AdaptiveRetrievalPlanSignature.Compute(Input());
        // 单一攻击者主体：10 条超限样本（无封顶时主导策略）。
        for (var i = 0; i < 10; i++)
        {
            await store.RecordAsync(Feedback(signature, hits: 4, budgetExceeded: true, subject: "attacker"));
        }
        // 6 条健康样本（不同主体）。
        for (var i = 0; i < 6; i++)
        {
            await store.RecordAsync(Feedback(signature, hits: 5, subject: "worker-" + i));
        }

        var policy = await planner.GetPolicyForSignatureAsync(signature);

        // 封顶后：attacker 5 条 + 健康 6 条 = 11 ≥ 10；超限率 = 5/11 ≈ 0.45 < 0.5 → 中性。
        Assert.AreEqual(1.0, policy.TokenBudgetMultiplier, "单主体贡献封顶后，攻击者不应主导策略。");
        Assert.AreEqual(11, policy.FeedbackSampleCount);
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
        // P0-16 加固列与幂等唯一索引。
        StringAssert.Contains(sql, "feedback_id text NULL");
        StringAssert.Contains(sql, "idempotency_key text NULL");
        StringAssert.Contains(sql, "source smallint NOT NULL DEFAULT 0");
        StringAssert.Contains(sql, "confidence double precision NOT NULL DEFAULT 1.0");
        StringAssert.Contains(sql, "outcome_quality double precision NOT NULL DEFAULT 1.0");
        StringAssert.Contains(sql, "subject text NULL");
        StringAssert.Contains(sql, "cc_retrieval_plan_feedback_idempotency");
        StringAssert.Contains(sql, "WHERE idempotency_key IS NOT NULL");
    }

    [TestMethod]
    public void Migration_0008_RetrievalPlanFeedbackHardening_Contract()
    {
        var step = PostgresMigrationStepRegistry.Steps
            .Single(s => s.MigrationId == "0008_retrieval_plan_feedback_hardening");

        Assert.AreEqual("cc-schema-v60", step.FromSchemaVersion);
        Assert.AreEqual("cc-schema-v61", step.ToSchemaVersion);
        CollectionAssert.AreEqual(
            new[] { PostgresMigrationStage.Online },
            step.Stages.ToArray(),
            "v60→v61 应为单 Online 阶段（ADD COLUMN IF NOT EXISTS / CREATE UNIQUE INDEX IF NOT EXISTS，幂等）。");
        StringAssert.Contains(step.Description, "idempotency_key");
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
            planner: null, signature: "sig:x", originalTask: null, latestAssistantIntent: null, goals: null,
            collectionId: null, purpose: null, policyVersion: null, retrievalProfile: null, taskClass: null,
            Http(), CancellationToken.None));

        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, status);
    }

    [TestMethod]
    public async Task PolicyEndpoint_ReturnsPolicy()
    {
        var (planner, _) = CreatePlanner();

        var (status, body) = await ExecuteAsync(await AdaptiveRetrievalEndpoints.GetPolicyAsync(
            planner, signature: null, originalTask: TaskText, latestAssistantIntent: IntentText, goals: null,
            collectionId: null, purpose: null, policyVersion: null, retrievalProfile: null, taskClass: null,
            Http(), CancellationToken.None));

        Assert.AreEqual(StatusCodes.Status200OK, status);
        var policy = JsonSerializer.Deserialize<AdaptiveRetrievalPolicy>(body, JsonWeb);
        Assert.IsNotNull(policy);
        Assert.AreEqual(1.0, policy!.TokenBudgetMultiplier);
    }

    [TestMethod]
    public async Task PolicyEndpoint_ResolvesWorkspaceFromContext_IntoSignature()
    {
        var (planner, _) = CreatePlanner();
        var httpContext = HttpWithWorkspace("ws-from-context");

        var (status, body) = await ExecuteAsync(httpContext, await AdaptiveRetrievalEndpoints.GetPolicyAsync(
            planner, signature: null, originalTask: TaskText, latestAssistantIntent: IntentText, goals: "确认 AlphaProtocol 是否已部署",
            collectionId: null, purpose: null, policyVersion: null, retrievalProfile: null, taskClass: null,
            httpContext, CancellationToken.None));

        Assert.AreEqual(StatusCodes.Status200OK, status);
        var policy = JsonSerializer.Deserialize<AdaptiveRetrievalPolicy>(body, JsonWeb);
        Assert.IsNotNull(policy);
        // 签名必须包含请求上下文中的 Workspace（P0-16 跨租户隔离）。
        Assert.AreEqual(
            AdaptiveRetrievalPlanSignature.Compute(Input(workspaceId: "ws-from-context")),
            policy!.PlanSignature);
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

    private static (AdaptiveRetrievalPlanner Planner, InMemoryRetrievalPlanFeedbackStore Store) CreatePlanner(
        AdaptiveRetrievalOptions? options = null)
    {
        var store = new InMemoryRetrievalPlanFeedbackStore();
        var planner = new AdaptiveRetrievalPlanner(new DefaultAgentRetrievalQueryPlanner(), store, options);
        return (planner, store);
    }

    private static AgentRetrievalPlannerInput Input(
        string? originalTask = null,
        int? remainingTurns = null,
        string? workspaceId = null,
        string? collectionId = null,
        string? purpose = null,
        string? policyVersion = null,
        string? retrievalProfile = null,
        string? taskClass = null)
    {
        var input = new AgentRetrievalPlannerInput
        {
            OriginalTask = originalTask ?? TaskText,
            LatestAssistantIntent = IntentText,
            UnresolvedGoals = new[] { "确认 AlphaProtocol 是否已部署" },
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Purpose = purpose,
            PolicyVersion = policyVersion,
            RetrievalProfile = retrievalProfile,
            TaskClass = taskClass
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
        bool effective = true,
        DateTimeOffset? recordedAt = null,
        string? idempotencyKey = null,
        string? subject = null,
        double confidence = 1.0,
        double outcomeQuality = 1.0) => new()
    {
        PlanSignature = signature,
        QueryText = "查询文本",
        HitsReturned = hits,
        BudgetExceeded = budgetExceeded,
        Effective = effective,
        RecordedAtUtc = recordedAt ?? DateTimeOffset.UtcNow,
        IdempotencyKey = idempotencyKey,
        Subject = subject,
        Confidence = confidence,
        OutcomeQuality = outcomeQuality
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

    /// <summary>带 Workspace 上下文访问器的 HttpContext（模拟 WorkspaceContextMiddleware 已填充）。</summary>
    private static DefaultHttpContext HttpWithWorkspace(string workspaceId)
    {
        var httpContext = Http();
        var accessor = new FixedWorkspaceContextAccessor();
        accessor.Set(new WorkspaceContext { WorkspaceId = workspaceId, Source = "test" });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkspaceContextAccessor>(accessor);
        httpContext.RequestServices = services.BuildServiceProvider();
        return httpContext;
    }

    private static async Task<(int Status, string Body)> ExecuteAsync(IResult result)
        => await ExecuteAsync(Http(), result);

    private static async Task<(int Status, string Body)> ExecuteAsync(DefaultHttpContext httpContext, IResult result)
    {
        await result.ExecuteAsync(httpContext);
        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body);
        var body = await reader.ReadToEndAsync();
        return ((int)httpContext.Response.StatusCode, body);
    }

    /// <summary>固定 Workspace 上下文访问器（测试桩）。</summary>
    private sealed class FixedWorkspaceContextAccessor : IWorkspaceContextAccessor
    {
        public WorkspaceContext? Current { get; private set; }

        public void Set(WorkspaceContext context) => Current = context;

        public void Clear() => Current = null;
    }
}
