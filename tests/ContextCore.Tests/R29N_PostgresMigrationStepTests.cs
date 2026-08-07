using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Testcontainers.PostgreSql;

namespace ContextCore.Tests;

// ===========================================================================
// PostgreSQL 迁移 Runner 拆分生产验收测试
//
// 验证 Migration Runner 拆分设计：
// 1. MigrationStepRegistry_ContainsVersionOrderedSteps — 步骤注册表非空、
// 按版本升序排列，每个步骤声明 MigrationId / From / To / Stages。
// 2. ToolDispatchResultsResultKeyStep_DeclaresV48ToV49WithThreeStages —
// v48→v49 步骤的阶段顺序为 Online → Backfill → ConstraintValidate。
// 3. MigrationMetrics_RecordWithoutThrowing — 迁移指标（DDL / 锁等待 /
// 失败版本 / 已应用步骤）在无监听器时记录不抛异常。
//
// 说明：步骤的 PreCheck / ExecuteStage 需要真实 NpgsqlConnection（pg catalog
// 查询），不在无 Postgres 的单元测试中执行；此处验证注册表接线与指标基础设施。
// ===========================================================================

[TestClass]
[TestCategory("Storage")]
[TestCategory("Postgres")]
[TestCategory("Migration")]
public sealed class R29N_PostgresMigrationStepTests
{
    [TestMethod]
    public void MigrationStepRegistry_ContainsVersionOrderedSteps()
    {
        var steps = PostgresMigrationStepRegistry.Steps;

        Assert.IsTrue(steps.Count > 0, "版本化迁移步骤注册表不应为空。");
        for (var i = 1; i < steps.Count; i++)
        {
            var previous = steps[i - 1];
            var current = steps[i];
            Assert.IsTrue(
                string.CompareOrdinal(previous.FromSchemaVersion, current.FromSchemaVersion) < 0,
                "步骤应按 FromSchemaVersion 升序排列。");
        }

        foreach (var step in steps)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(step.MigrationId), "MigrationId 不应为空。");
            Assert.IsFalse(string.IsNullOrWhiteSpace(step.FromSchemaVersion), "FromSchemaVersion 不应为空。");
            Assert.IsFalse(string.IsNullOrWhiteSpace(step.ToSchemaVersion), "ToSchemaVersion 不应为空。");
            Assert.IsFalse(string.IsNullOrWhiteSpace(step.Description), "Description 不应为空。");
            Assert.IsTrue(step.Stages.Count > 0, "Stages 不应为空。");
        }
    }

    [TestMethod]
    public void ToolDispatchResultsResultKeyStep_DeclaresV48ToV49WithThreeStages()
    {
        var step = PostgresMigrationStepRegistry.Steps
            .OfType<PostgresMigrationToolDispatchResultsResultKey>()
            .Single();

        Assert.AreEqual("0002_tool_dispatch_results_result_key", step.MigrationId);
        Assert.AreEqual("cc-schema-v48", step.FromSchemaVersion);
        Assert.AreEqual("cc-schema-v49", step.ToSchemaVersion);
        CollectionAssert.AreEqual(
            new[]
            {
                PostgresMigrationStage.Online,
                PostgresMigrationStage.Backfill,
                PostgresMigrationStage.ConstraintValidate
            },
            step.Stages.ToArray());
    }

    [TestMethod]
    public void MigrationMetrics_RecordWithoutThrowing()
    {
        // 无 MeterListener 时记录为 no-op；验证仪表盘静态初始化与 Record/Add 不抛异常。
        PostgresMigrationMetrics.DdlDuration.Record(1.5);
        PostgresMigrationMetrics.LockWaitDuration.Record(0.25);
        PostgresMigrationMetrics.FailedVersions.Add(
            1,
            new KeyValuePair<string, object?>("version", "cc-schema-v51"));
        PostgresMigrationMetrics.StepsApplied.Add(
            1,
            new KeyValuePair<string, object?>("step", "0002_tool_dispatch_results_result_key"));
    }

    // ── 存量库真实迁移（Testcontainers）：0014 agent_run_leases 工作区复合键 ──

    /// <summary>
    /// 验证 v66 → v67 步骤在真实 Postgres 存量库上的三阶段执行：
    /// 旧结构（run_id 主键、无 workspace_id）加列 → 按 agent_runs 回填 → 孤儿租约删除 →
    /// NOT NULL + 复合主键切换；迁移后租约按 (workspace_id, run_id) 复合键正常寻址。
    /// </summary>
    [TestMethod]
    public async Task AgentRunLeaseWorkspaceKeyStep_UpgradesLegacyTableWithBackfill()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — 存量迁移测试已跳过。");
            return;
        }

        await using (container)
        {
            var options = new PostgresOptions
            {
                ConnectionString = container.GetConnectionString(),
                AutoMigrate = false,
                EnablePgVectorExtension = true,
                TablePrefix = "legacy_"
            };
            var factory = new PostgresConnectionFactory(options);

            // 建旧结构：agent_runs 最小表（供回填 join）+ 无 workspace_id 的 agent_run_leases。
            await using (var conn = await factory.OpenConnectionAsync(CancellationToken.None))
            {
                var cmd = conn.CreateCommand();
                cmd.CommandTimeout = options.CommandTimeoutSeconds;
                cmd.CommandText = """
                    CREATE TABLE legacy_agent_runs (
                        workspace_id text NOT NULL,
                        run_id text NOT NULL,
                        session_id text NOT NULL DEFAULT '',
                        task text NOT NULL DEFAULT '',
                        state smallint NOT NULL DEFAULT 0,
                        turn integer NOT NULL DEFAULT 0,
                        priority integer NOT NULL DEFAULT 0,
                        max_retries integer NOT NULL DEFAULT 0,
                        retry_count integer NOT NULL DEFAULT 0,
                        next_retry_at timestamptz NULL,
                        created_at timestamptz NOT NULL,
                        updated_at timestamptz NOT NULL,
                        finished_at timestamptz NULL,
                        failure_reason text NULL,
                        final_answer text NULL,
                        turn_budget_json text NULL,
                        cost_budget_json text NULL,
                        last_checkpoint_id text NULL,
                        last_checkpoint_sequence integer NULL,
                        idempotency_key text NULL,
                        claim_owner text NULL,
                        claim_token text NULL,
                        claim_expires_at timestamptz NULL,
                        claim_attempt integer NOT NULL DEFAULT 0,
                        data jsonb NOT NULL DEFAULT jsonb_build_object(),
                        PRIMARY KEY (workspace_id, run_id));
                    CREATE TABLE legacy_agent_run_leases (
                        run_id text NOT NULL,
                        owner text NOT NULL,
                        lease_token text NOT NULL,
                        fencing_token bigint NOT NULL DEFAULT 1,
                        acquired_at timestamptz NOT NULL,
                        lease_expires_at timestamptz NOT NULL,
                        PRIMARY KEY (run_id));
                    INSERT INTO legacy_agent_runs (workspace_id, run_id, session_id, state, created_at, updated_at, data)
                    VALUES ('ws-legacy', 'run-legacy-1', 'sess-1', 0, now(), now(), '{}'::jsonb);
                    INSERT INTO legacy_agent_run_leases (run_id, owner, lease_token, fencing_token, acquired_at, lease_expires_at)
                    VALUES ('run-legacy-1', 'node-a', 'tok-1', 1, now(), now() + interval '1 hour');
                    INSERT INTO legacy_agent_run_leases (run_id, owner, lease_token, fencing_token, acquired_at, lease_expires_at)
                    VALUES ('run-orphan-1', 'node-b', 'tok-2', 1, now(), now() + interval '1 hour');
                    """;
                await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            }

            var runner = new PostgresMigrationRunner(factory);
            var result = await runner.ApplyMigrationsAsync(confirm: true, CancellationToken.None);
            Assert.IsTrue(result.Applied, "迁移应成功应用。");
            Assert.AreEqual("cc-schema-v68", await runner.GetAppliedVersionAsync(CancellationToken.None));

            // 回填：run-legacy-1 的 workspace_id 来自 agent_runs。
            await using (var conn = await factory.OpenConnectionAsync(CancellationToken.None))
            {
                var wsCmd = conn.CreateCommand();
                wsCmd.CommandText = "SELECT workspace_id FROM legacy_agent_run_leases WHERE run_id = 'run-legacy-1';";
                Assert.AreEqual("ws-legacy", await wsCmd.ExecuteScalarAsync(CancellationToken.None) as string, "租约应回填所属工作区。");

                var orphanCmd = conn.CreateCommand();
                orphanCmd.CommandText = "SELECT COUNT(1) FROM legacy_agent_run_leases WHERE run_id = 'run-orphan-1';";
                Assert.AreEqual(0L, Convert.ToInt64(await orphanCmd.ExecuteScalarAsync(CancellationToken.None)), "孤儿租约应被删除。");

                var pkCmd = conn.CreateCommand();
                pkCmd.CommandText = """
                    SELECT count(*) FROM pg_index i
                    JOIN pg_class c ON c.oid = i.indrelid
                    JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = ANY(i.indkey)
                    WHERE c.relname = 'legacy_agent_run_leases'
                      AND i.indisprimary AND a.attname = 'workspace_id';
                    """;
                Assert.IsTrue(Convert.ToInt64(await pkCmd.ExecuteScalarAsync(CancellationToken.None)) > 0,
                    "主键应升级为包含 workspace_id 的复合键。");
            }

            // 迁移后复合键寻址正常：同 run 不同 workspace 的租约互不干扰。
            var lease = new PostgresAgentRunLease(factory, new PostgresJsonSerializer(), runner);

            // 存量回填的租约仍由原 owner 持有（token tok-1）；先按原 token 释放，再验证复合键获取。
            await lease.ReleaseAsync("ws-legacy", "run-legacy-1", "tok-1", CancellationToken.None);
            var a = await lease.TryAcquireAsync("ws-legacy", "run-legacy-1", TimeSpan.FromMinutes(5), "node-a", CancellationToken.None);
            Assert.IsNotNull(a, "释放后应可按复合键重新获取。");
            var cross = await lease.TryAcquireAsync("ws-other", "run-legacy-1", TimeSpan.FromMinutes(5), "node-b", CancellationToken.None);
            Assert.IsNotNull(cross, "不同工作区相同 runId 应各自独立寻址（复合键隔离）。");
            await lease.ReleaseAsync("ws-legacy", "run-legacy-1", a!.LeaseToken, CancellationToken.None);
            await lease.ReleaseAsync("ws-other", "run-legacy-1", cross!.LeaseToken, CancellationToken.None);

            await factory.DisposeAsync();
        }
    }

    private static async Task<PostgreSqlContainer?> TryStartPostgresAsync()
    {
        const string pgVectorImage = "pgvector/pgvector:pg17";
        try
        {
            var container = new PostgreSqlBuilder(pgVectorImage)
                .WithDatabase("cctest")
                .WithUsername("cctest")
                .WithPassword("cctest")
                .Build();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await container.StartAsync(cts.Token);
            return container;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[R29N_PostgresMigrationStepTests] Docker/Postgres 不可用：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
