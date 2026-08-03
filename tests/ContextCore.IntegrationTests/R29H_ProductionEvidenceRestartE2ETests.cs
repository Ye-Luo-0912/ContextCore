using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContextCore.IntegrationTests.TestFixtures;

namespace ContextCore.IntegrationTests;

// ===========================================================================
// Production Evidence 进程重启恢复 E2E 测试
//
// 目标：补齐现有 4 个 E2E 测试的缺口——现有"崩溃恢复"测试通过 SQL 重置状态模拟崩溃，
// 非真实的进程退出/重启。本测试通过 WebApplicationFactory 的 Dispose 与重建模拟真实进程重启：
// Factory 启动 → 写入 Run → Dispose（模拟进程退出）→ Factory 启动（同一 PG）→ 验证状态连续性
//
// 与既有 E2E_RealPostgres_CrashRecovery_MidToolExecution 的区别：
// - 现有测试：同一进程内用 SQL 重置 Run 状态，再创建新 Actor 恢复执行。
// - 本测试：完全 Dispose Web 主机（DI 容器、HostedService、连接池全部销毁），
// 再用同一 PG 重新启动新主机，验证数据持久化跨进程重启。
//
// 测试覆盖：
// 1. E2E_Restart_RunPersistsAcrossProcessRestart — Run 持久化跨进程重启
// 2. E2E_Restart_IdempotencyKeySurvivesRestart — 幂等键跨进程重启仍去重
// 3. E2E_Restart_LearningOutboxSurvivesRestart — Learning Outbox 跨进程重启持久
// 4. E2E_Restart_NewProcessCanReadOldRuns — 新进程能读取旧进程写入的 Run
//
// 设计原则：
// - 使用同一 PostgresE2EFixture（同一 PG 容器），两个 Factory 实例先后启动。
// - Factory Dispose 后 PG 数据保留；Factory 启动时 AutoBootstrap migration 是 no-op。
// - Docker/Postgres 不可用时 Assert.Inconclusive 跳过。
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Production-Evidence")]
[TestCategory("Integration")]
[TestCategory("Postgres")]
[TestCategory("DockerRequired")]
[TestCategory("ProcessRestart")]
public sealed class R29H_ProductionEvidenceRestartE2ETests : IAsyncDisposable
{
    private readonly PostgresE2EFixture _pg = new();

    [TestInitialize]
    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
    }

    [TestCleanup]
    public async Task CleanupAsync()
    {
        await _pg.DisposeAsync();
    }

    private static bool ShouldSkip(PostgresE2EFixture pg) => pg.ShouldSkip;

    // =======================================================================
    // 测试 1：Run 持久化跨进程重启 — Factory 写入 → Dispose → Factory 读取
    // =======================================================================

    [TestMethod]
    public async Task E2E_Restart_RunPersistsAcrossProcessRestart()
    {
        if (ShouldSkip(_pg)) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。此结果不证明生产证据通过。"); return; }

        string? runId;

        // ── 进程 ：创建 Run ──
        await using (var factory1 = new ProductionEvidenceWebFactory(_pg.ConnectionString))
        {
            using var client1 = factory1.CreateClient();
            var createRequest = new
            {
                Task = "进程重启持久化测试",
                                TimeoutSeconds = 60
            };
            using var response1 = await client1.PostAsJsonAsync("/api/agents/runs", createRequest);
            Assert.IsTrue(
                response1.StatusCode == HttpStatusCode.Created ||
                response1.StatusCode == HttpStatusCode.Accepted,
                $"进程 #1 POST 应返回 201/202，实际 {response1.StatusCode}。");
            var run1 = await response1.Content.ReadFromJsonAsync<JsonElement>();
            runId = run1.GetProperty("runId").GetString();
            Assert.IsFalse(string.IsNullOrEmpty(runId), "进程 #1 应返回 runId。");
        } // factory1 Dispose — 模拟进程 退出（DI 容器、连接池全部销毁）

        // ── 进程 ：重新启动，验证 Run 仍然存在 ──
        await using var factory2 = new ProductionEvidenceWebFactory(_pg.ConnectionString);
        using var client2 = factory2.CreateClient();

        using var response2 = await client2.GetAsync($"/api/agents/runs/{runId}");
        Assert.AreEqual(HttpStatusCode.OK, response2.StatusCode,
            $"进程 #2 GET 应返回 200 OK（Run 跨进程持久化），实际 {response2.StatusCode}。");

        var run2 = await response2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(runId, run2.GetProperty("runId").GetString(),
            "进程 #2 取回的 runId 应与进程 #1 创建的一致。");
        Assert.AreEqual("进程重启持久化测试", run2.GetProperty("task").GetString(),
            "进程 #2 取回的 task 应与进程 #1 创建的一致。");
    }

    // =======================================================================
    // 测试 2：幂等键跨进程重启仍去重 — 进程 用相同 key 应返回同一 Run
    // =======================================================================

    [TestMethod]
    public async Task E2E_Restart_IdempotencyKeySurvivesRestart()
    {
        if (ShouldSkip(_pg)) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。此结果不证明生产证据通过。"); return; }

        var idempotencyKey = "restart-idem-" + Guid.NewGuid().ToString("N");
        string? runId1;

        // ── 进程 ：用 IdempotencyKey 创建 Run ──
        await using (var factory1 = new ProductionEvidenceWebFactory(_pg.ConnectionString))
        {
            using var client1 = factory1.CreateClient();
            var createRequest = new
            {
                Task = "幂等键跨重启测试",
                                IdempotencyKey = idempotencyKey,
                TimeoutSeconds = 60
            };
            using var response1 = await client1.PostAsJsonAsync("/api/agents/runs", createRequest);
            Assert.IsTrue(
                response1.StatusCode == HttpStatusCode.Created ||
                response1.StatusCode == HttpStatusCode.Accepted,
                $"进程 #1 POST 应返回 201/202，实际 {response1.StatusCode}。");
            var run1 = await response1.Content.ReadFromJsonAsync<JsonElement>();
            runId1 = run1.GetProperty("runId").GetString();
        }

        // ── 进程 ：用相同 IdempotencyKey 创建（应返回同一 Run，200 OK）──
        await using var factory2 = new ProductionEvidenceWebFactory(_pg.ConnectionString);
        using var client2 = factory2.CreateClient();

        var createRequest2 = new
        {
            Task = "幂等键跨重启测试",
                        IdempotencyKey = idempotencyKey,
            TimeoutSeconds = 60
        };
        using var response2 = await client2.PostAsJsonAsync("/api/agents/runs", createRequest2);
        Assert.AreEqual(HttpStatusCode.OK, response2.StatusCode,
            $"进程 #2 用相同 IdempotencyKey 应返回 200 OK（幂等返回已有），实际 {response2.StatusCode}。");

        var run2 = await response2.Content.ReadFromJsonAsync<JsonElement>();
        var runId2 = run2.GetProperty("runId").GetString();
        Assert.AreEqual(runId1, runId2,
            "进程 #2 用相同 IdempotencyKey 应返回同一 runId（跨进程幂等契约）。");

        // ── 断言：数据库中只有 1 条记录 ──
        await using var connection = await _pg.OpenRawConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM cc_agent_runs WHERE run_id = @runId";
        command.Parameters.AddWithValue("runId", runId1!);
        var count = (long)(await command.ExecuteScalarAsync())!;
        Assert.AreEqual(1L, count,
            $"跨进程重启后数据库中应有 1 条记录（幂等去重），实际 {count}。");
    }

    // =======================================================================
    // 测试 3：Learning Outbox 跨进程重启持久 — 进程 入队 → 进程 能 Acquire
    // =======================================================================

    [TestMethod]
    public async Task E2E_Restart_LearningOutboxSurvivesRestart()
    {
        if (ShouldSkip(_pg)) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。此结果不证明生产证据通过。"); return; }

        var eventId = "restart-learn-" + Guid.NewGuid().ToString("N");

        // ── 进程 ：直接通过 PG 入队 Learning Event ──
        // （HTTP API 未暴露 Learning Outbox 入队端点，用直连 PG 模拟进程 的 Learning Dispatcher 写入）
        var (factory1, migrationRunner1, serializer1) = _pg.CreateInfrastructure("restart1_");
        try
        {
            await migrationRunner1.MigrateAsync();
            var outboxStore1 = new ContextCore.Storage.Postgres.Stores.PostgresLearningEventOutboxStore(
                factory1, serializer1, migrationRunner1);

            var now = DateTimeOffset.UtcNow;
            await outboxStore1.EnqueueAsync(new ContextCore.Abstractions.LearningEventOutboxRecord
            {
                EventId = eventId,
                WorkspaceId = "ws-restart-learn",
                CollectionId = "col-restart-learn",
                DecisionId = "decision-" + eventId,
                Payload = """{"decisionId":"decision-1","score":0.9}""",
                State = ContextCore.Abstractions.LearningEventOutboxStates.Pending,
                RetryCount = 0,
                MaxRetryCount = 5,
                CreatedAt = now,
                UpdatedAt = now
            });

            // 断言入队成功
            var counts1 = await outboxStore1.CountByStateAsync();
            Assert.IsTrue(counts1.TryGetValue(ContextCore.Abstractions.LearningEventOutboxStates.Pending, out var pending1) && pending1 >= 1,
                $"进程 #1 入队后应至少有 1 条 Pending，实际 Pending={pending1}。");
        }
        finally
        {
            await factory1.DisposeAsync();
        }

        // ── 进程 ：重新连接同一 PG，AcquirePending 应能取到进程 入队的记录 ──
        var (factory2, migrationRunner2, serializer2) = _pg.CreateInfrastructure("restart1_");
        try
        {
            await migrationRunner2.MigrateAsync(); // no-op（schema 已存在）
            var outboxStore2 = new ContextCore.Storage.Postgres.Stores.PostgresLearningEventOutboxStore(
                factory2, serializer2, migrationRunner2);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var acquired = await outboxStore2.AcquirePendingAsync(
                limit: 10,
                owner: "worker-restart-2",
                leaseDuration: TimeSpan.FromMinutes(2),
                cts.Token);

            // ── 断言：进程 能取到进程 入队的记录 ──
            var found = acquired.FirstOrDefault(r => r.EventId == eventId);
            Assert.IsNotNull(found,
                "进程 #2 应能 Acquire 到进程 #1 入队的 Learning Event（跨进程持久化）。");
            Assert.AreEqual(ContextCore.Abstractions.LearningEventOutboxStates.Processing, found!.State,
                "Acquire 后状态应为 Processing。");
            Assert.AreEqual("worker-restart-2", found.LeaseOwner,
                "LeaseOwner 应为进程 #2 的 worker。");
            Assert.IsFalse(string.IsNullOrEmpty(found.LeaseToken),
                "应分配了新的 LeaseToken。");
        }
        finally
        {
            await factory2.DisposeAsync();
        }
    }

    // =======================================================================
    // 测试 4：新进程能读取旧进程写入的多个 Run — 批量持久化验证
    // =======================================================================

    [TestMethod]
    public async Task E2E_Restart_NewProcessCanReadOldRuns()
    {
        if (ShouldSkip(_pg)) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。此结果不证明生产证据通过。"); return; }

        var runIds = new List<string>();

        // ── 进程 ：创建 3 个 Run ──
        await using (var factory1 = new ProductionEvidenceWebFactory(_pg.ConnectionString))
        {
            using var client1 = factory1.CreateClient();
            for (var i = 0; i < 3; i++)
            {
                var createRequest = new
                {
                    Task = $"批量持久化测试 #{i + 1}",
                                        TimeoutSeconds = 60
                };
                using var response = await client1.PostAsJsonAsync("/api/agents/runs", createRequest);
                Assert.IsTrue(
                    response.StatusCode == HttpStatusCode.Created ||
                    response.StatusCode == HttpStatusCode.Accepted,
                    $"进程 #1 POST #{i + 1} 应返回 201/202，实际 {response.StatusCode}。");
                var run = await response.Content.ReadFromJsonAsync<JsonElement>();
                runIds.Add(run.GetProperty("runId").GetString()!);
            }
        }

        // ── 进程 ：验证 3 个 Run 都能读取 ──
        await using var factory2 = new ProductionEvidenceWebFactory(_pg.ConnectionString);
        using var client2 = factory2.CreateClient();

        foreach (var runId in runIds)
        {
            using var response = await client2.GetAsync($"/api/agents/runs/{runId}");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                $"进程 #2 GET runId={runId} 应返回 200 OK，实际 {response.StatusCode}。");
            var run = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual(runId, run.GetProperty("runId").GetString(),
                "进程 #2 取回的 runId 应一致。");
        }

        // ── 断言：数据库中有 3 条记录 ──
        await using var connection = await _pg.OpenRawConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM cc_agent_runs WHERE task LIKE '批量持久化测试 #%'";
        var count = (long)(await command.ExecuteScalarAsync())!;
        Assert.AreEqual(3L, count,
            $"进程 #2 应能读到 3 条记录（跨进程批量持久化），实际 {count}。");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _pg.DisposeAsync();
    }
}
