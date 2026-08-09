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

    // ── WP-AA：迁移链完整版本矩阵（防历史迁移回归）─────────────────────────

    /// <summary>
    /// 迁移链无重叠：后一步 From 必须 ≥ 前一步 To（禁止范围重叠/倒序；历史早期存在
    /// 合法跳段——如 v49→v52，早期版本以累计 DDL 覆盖，故允许前一步 To &lt; 后一步 From）。
    /// </summary>
    [TestMethod]
    public void MigrationMatrix_VersionChain_NoOverlapNoRegression()
    {
        var steps = PostgresMigrationStepRegistry.Steps;

        Assert.IsTrue(steps.Count > 0);
        for (var i = 1; i < steps.Count; i++)
        {
            Assert.IsTrue(
                string.CompareOrdinal(steps[i].FromSchemaVersion, steps[i - 1].ToSchemaVersion) >= 0,
                $"步骤 {steps[i].MigrationId} 的 From（{steps[i].FromSchemaVersion}）不得小于前一步 " +
                $"{steps[i - 1].MigrationId} 的 To（{steps[i - 1].ToSchemaVersion}）——禁止范围重叠/倒序。");
        }
    }

    /// <summary>
    /// 迁移链完整性：MigrationId 全局唯一（防重复注册）、首步 From 与末步 To 覆盖完整范围
    /// （首步 From 应为链条起点，末步 To 应等于 Runner.SchemaVersion）。
    /// </summary>
    [TestMethod]
    public void MigrationMatrix_IdsUnique_AndChainCoversFullRange()
    {
        var steps = PostgresMigrationStepRegistry.Steps;

        var ids = steps.Select(s => s.MigrationId).ToList();
        Assert.AreEqual(ids.Count, ids.Distinct(StringComparer.Ordinal).Count(), "MigrationId 不得重复。");

        // 首步起点：链条第一个迁移步骤（v48 是版本化步骤的最早起点——0002）。
        Assert.AreEqual("cc-schema-v48", steps[0].FromSchemaVersion,
            "版本化步骤链条应从 v48 开始（基线 v1 为累计 DDL，不进入版本链）。");
        Assert.AreEqual(PostgresMigrationRunner.SchemaVersion, steps[^1].ToSchemaVersion,
            "末步 To 必须等于 Runner.SchemaVersion（与漂移测试一致）。");
    }

    /// <summary>
    /// 迁移链步数快照：当前注册表应为 20 步（v48→v49 至 v72→v73，MigrationId 0002~0020）。
    /// 新增迁移时更新本断言——防止意外删除/合并历史步骤（审计链完整性）。
    /// </summary>
    [TestMethod]
    public void MigrationMatrix_StepCountMatchesExpectedChain()
    {
        var steps = PostgresMigrationStepRegistry.Steps;
        Assert.AreEqual(20, steps.Count,
            $"迁移链步数应等于 20（0002~0020 共 19 个版本化步骤）。" +
            $"实际 {steps.Count}——新增/删除步骤时更新本断言。");
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
            Assert.AreEqual("cc-schema-v73", await runner.GetAppliedVersionAsync(CancellationToken.None));

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

    /// <summary>
    /// 回归：全新数据库 + public schema（表名带 schema 限定）执行全量迁移。
    /// 版本化步骤在基线 DDL 前运行：配额持久化步骤先建出单键预留表，
    /// 预留复合键步骤再切换主键——约束名必须用非限定名，否则 DROP CONSTRAINT
    /// 会把 schema 限定误当约束名导致语法错误（备份集成测试在带 schema 的源库上曾因此失败）。
    /// 迁移后两张表的主键均为复合键。
    /// </summary>
    [TestMethod]
    public async Task FreshDatabaseWithPublicSchema_MigratesToCompositeKeys()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — 全新库迁移测试已跳过。");
            return;
        }

        await using (container)
        {
            var options = new PostgresOptions
            {
                ConnectionString = container.GetConnectionString(),
                AutoMigrate = true,
                EnablePgVectorExtension = true,
                SchemaName = "public",
                TablePrefix = "cc_"
            };
            var factory = new PostgresConnectionFactory(options);
            var runner = new PostgresMigrationRunner(factory);

            // 与备份集成测试一致的入口：全新数据库上先跑版本化步骤再跑基线 DDL。
            await runner.MigrateAsync(CancellationToken.None);
            Assert.AreEqual("cc-schema-v73", await runner.GetAppliedVersionAsync(CancellationToken.None));

            await using (var conn = await factory.OpenConnectionAsync(CancellationToken.None))
            {
                var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT c.relname, array_length(pk.conkey, 1)
                    FROM pg_class c
                    JOIN pg_constraint pk ON pk.conrelid = c.oid AND pk.contype = 'p'
                    WHERE c.relname IN ('cc_workspace_quota_reservations', 'cc_agent_run_leases')
                    ORDER BY c.relname;
                    """;
                await using var reader = await cmd.ExecuteReaderAsync(CancellationToken.None);
                var pkeyColumns = new Dictionary<string, int>();
                while (await reader.ReadAsync(CancellationToken.None))
                {
                    pkeyColumns[reader.GetString(0)] = reader.GetInt32(1);
                }

                Assert.AreEqual(2, pkeyColumns["cc_workspace_quota_reservations"],
                    "预留表主键应为 (workspace_id, reservation_id) 复合键。");
                Assert.AreEqual(2, pkeyColumns["cc_agent_run_leases"],
                    "租约表主键应为 (workspace_id, run_id) 复合键。");
            }

            // 验证 v69：Tool Dispatch Journal / Result 工作区复合键（与版本化步骤 0016 目标一致）。
            await using (var conn = await factory.OpenConnectionAsync(CancellationToken.None))
            {
                var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT c.relname, array_length(pk.conkey, 1), pk.conkey::text
                    FROM pg_class c
                    JOIN pg_constraint pk ON pk.conrelid = c.oid AND pk.contype = 'p'
                    WHERE c.relname IN ('cc_tool_dispatch_journal_entries', 'cc_tool_dispatch_results')
                    ORDER BY c.relname;
                    """;
                await using var reader = await cmd.ExecuteReaderAsync(CancellationToken.None);
                var pkeys = new Dictionary<string, (int Columns, string KeyColumns)>();
                while (await reader.ReadAsync(CancellationToken.None))
                {
                    pkeys[reader.GetString(0)] = (reader.GetInt32(1), reader.GetString(2));
                }

                Assert.IsTrue(pkeys.TryGetValue("cc_tool_dispatch_journal_entries", out var journalPk),
                    "journal 表应存在主键。");
                Assert.AreEqual(3, journalPk.Columns, "journal 主键应为 (workspace_id, run_id, request_id) 复合键。");
                Assert.AreEqual(3, pkeys["cc_tool_dispatch_results"].Columns,
                    "结果表主键应为 (workspace_id, run_id, request_id) 复合键。");
            }

            // 幂等键唯一索引作用域：workspace_id + tool_name + idempotency_key
            // （ExternalIdempotencyKey 业务外部操作身份，跨 Run 去重；partial，NOT NULL 才唯一）。
            await using (var conn = await factory.OpenConnectionAsync(CancellationToken.None))
            {
                var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT i.indexdef
                    FROM pg_indexes i
                    WHERE i.tablename = 'cc_tool_dispatch_journal_entries'
                      AND i.indexname LIKE '%idempotency%';
                    """;
                var indexDef = await cmd.ExecuteScalarAsync(CancellationToken.None) as string;
                Assert.IsNotNull(indexDef, "journal 应存在幂等键唯一索引。");
                StringAssert.Contains(indexDef, "workspace_id", "幂等键索引应含 workspace_id 列。");
                StringAssert.Contains(indexDef, "tool_name", "幂等键索引应含 tool_name（provider 命名空间）列。");
                StringAssert.Contains(indexDef, "idempotency_key IS NOT NULL", "幂等键索引应为 partial（仅非空键参与唯一约束）。");
            }

            await factory.DisposeAsync();
        }
    }

    /// <summary>
    /// 架构漂移防线：注册表最后一个步骤的目标版本必须与 Runner 当前 SchemaVersion 一致。
    /// 新增版本化步骤后若忘记同步 SchemaVersion，MigrateAsync 的
    /// "appliedVersion == SchemaVersion → 直接 return" 短路会导致新步骤永不执行
    /// （如 v68 存量库错过 v69 Tool 复合键迁移）；本测试永久禁止此类漂移。
    /// </summary>
    [TestMethod]
    public void SchemaVersion_MatchesRegistryTail()
    {
        var lastStep = PostgresMigrationStepRegistry.Steps.Last();
        Assert.IsNotNull(lastStep, "迁移步骤注册表不应为空。");
        Assert.AreEqual(lastStep.ToSchemaVersion, PostgresMigrationRunner.SchemaVersion,
            "SchemaVersion 必须与注册表最后一个步骤的目标版本一致（防架构漂移）。");
    }

    /// <summary>
    /// 验证 v68 存量库 → 部署当前代码 → 0016 步骤真实执行：
    /// 旧结构（request_id 单键主键 + 全局幂等键唯一索引 + 可空双键列）的
    /// journal / results 表，在 schema_versions 已记录 v68 时，
    /// MigrateAsync 不得被短路跳过——必须升级到 v69：
    /// journal 主键 (workspace_id, run_id, request_id)、results 主键同构、
    /// 幂等键唯一索引作用域 (workspace_id, run_id, idempotency_key)，
    /// 且 Backfill 按 agent_runs 回填双键、未映射行移入隔离表（不静默删除审计真相）。
    /// </summary>
    [TestMethod]
    public async Task ToolDispatchWorkspaceKeyStep_UpgradesV68LegacyDatabase()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — 存量 v68 升级测试已跳过。");
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

            // 构造 v68 存量库：schema_versions 记录 v68 + 旧结构 journal/results + agent_runs。
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
                    CREATE TABLE legacy_tool_dispatch_journal_entries (
                        request_id text NOT NULL PRIMARY KEY,
                        tool_name text NOT NULL DEFAULT '',
                        state smallint NOT NULL DEFAULT 0,
                        idempotency_key text NULL,
                        payload_digest text NULL,
                        external_operation_id text NULL,
                        workspace_id text NULL,
                        run_id text NULL,
                        created_at timestamptz NULL,
                        updated_at timestamptz NULL,
                        diagnostic_note text NULL);
                    CREATE UNIQUE INDEX ix_legacy_tool_dispatch_journal_entries_idempotency
                        ON legacy_tool_dispatch_journal_entries (idempotency_key)
                        WHERE idempotency_key IS NOT NULL;
                    CREATE TABLE legacy_tool_dispatch_results (
                        tool_call_id text NOT NULL,
                        request_id text NOT NULL,
                        idempotency_key text,
                        side_effect text NOT NULL DEFAULT 'None',
                        external_operation_id text,
                        result jsonb,
                        succeeded boolean NOT NULL,
                        error text,
                        duration_ms bigint NOT NULL DEFAULT 0,
                        created_at timestamptz NOT NULL DEFAULT now(),
                        workspace_id text NULL,
                        run_id text NULL,
                        invocation_id text,
                        PRIMARY KEY (request_id));
                    CREATE TABLE legacy_schema_versions (
                        version text NOT NULL,
                        applied_at timestamptz NOT NULL,
                        PRIMARY KEY (version));
                    INSERT INTO legacy_schema_versions (version, applied_at) VALUES ('cc-schema-v68', now());

                    INSERT INTO legacy_agent_runs (workspace_id, run_id, created_at, updated_at)
                    VALUES ('ws-legacy', 'run-legacy-1', now(), now());
                    INSERT INTO legacy_tool_dispatch_journal_entries (request_id, tool_name, state, idempotency_key, run_id)
                    VALUES ('req-legacy-1', 'bank-transfer', 2, 'idem-legacy-1', 'run-legacy-1');
                    INSERT INTO legacy_tool_dispatch_journal_entries (request_id, tool_name, state, run_id)
                    VALUES ('req-orphan-1', 'bank-transfer', 2, 'run-gone-1');
                    INSERT INTO legacy_tool_dispatch_results (request_id, tool_call_id, run_id, succeeded)
                    VALUES ('req-legacy-1', 'tc-1', 'run-legacy-1', true);
                    """;
                await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            }

            // 启动当前代码：MigrateAsync 必须执行 0016 + 0017（不得被 v68 短路跳过）。
            var runner = new PostgresMigrationRunner(factory);
            await runner.MigrateAsync(CancellationToken.None);
            Assert.AreEqual("cc-schema-v73", await runner.GetAppliedVersionAsync(CancellationToken.None),
                "v68 存量库部署当前代码后必须升级到最新 schema。");

            await using (var conn = await factory.OpenConnectionAsync(CancellationToken.None))
            {
                // journal 主键升级为 (workspace_id, run_id, request_id) 复合键。
                var pkCmd = conn.CreateCommand();
                pkCmd.CommandText = """
                    SELECT array_length(pk.conkey, 1)
                    FROM pg_class c
                    JOIN pg_constraint pk ON pk.conrelid = c.oid AND pk.contype = 'p'
                    WHERE c.relname = 'legacy_tool_dispatch_journal_entries';
                    """;
                Assert.AreEqual(3, Convert.ToInt32(await pkCmd.ExecuteScalarAsync(CancellationToken.None)),
                    "journal 主键应为 (workspace_id, run_id, request_id) 复合键。");

                // results 主键同构。
                var resultPkCmd = conn.CreateCommand();
                resultPkCmd.CommandText = """
                    SELECT array_length(pk.conkey, 1)
                    FROM pg_class c
                    JOIN pg_constraint pk ON pk.conrelid = c.oid AND pk.contype = 'p'
                    WHERE c.relname = 'legacy_tool_dispatch_results';
                    """;
                Assert.AreEqual(3, Convert.ToInt32(await resultPkCmd.ExecuteScalarAsync(CancellationToken.None)),
                    "结果表主键应为 (workspace_id, run_id, request_id) 复合键。");

                // 回填：run 存在的行按 agent_runs 回填 workspace_id。
                var backfillCmd = conn.CreateCommand();
                backfillCmd.CommandText = "SELECT workspace_id FROM legacy_tool_dispatch_journal_entries WHERE request_id = 'req-legacy-1';";
                Assert.AreEqual("ws-legacy", await backfillCmd.ExecuteScalarAsync(CancellationToken.None) as string,
                    "Backfill 应按 agent_runs 回填 workspace_id。");

                // 未映射行（run 在 agent_runs 不存在）→ 移入隔离表保留审计真相（不静默删除）。
                var orphanCmd = conn.CreateCommand();
                orphanCmd.CommandText = "SELECT COUNT(1) FROM legacy_tool_dispatch_journal_entries WHERE request_id = 'req-orphan-1';";
                Assert.AreEqual(0L, Convert.ToInt64(await orphanCmd.ExecuteScalarAsync(CancellationToken.None)),
                    "未映射 journal 行应从主表移除（真相已移入隔离表）。");

                var quarantineCmd = conn.CreateCommand();
                quarantineCmd.CommandText = "SELECT quarantine_reason FROM legacy_tool_dispatch_quarantine WHERE request_id = 'req-orphan-1';";
                Assert.AreEqual("unmapped-run", await quarantineCmd.ExecuteScalarAsync(CancellationToken.None) as string,
                    "未映射行必须移入隔离表并标记原因（审计真相不删除）。");

                // 幂等键唯一索引作用域升级为 (workspace_id, tool_name, idempotency_key)。
                var indexCmd = conn.CreateCommand();
                indexCmd.CommandText = """
                    SELECT indexdef FROM pg_indexes
                    WHERE tablename = 'legacy_tool_dispatch_journal_entries'
                      AND indexname = 'ix_legacy_tool_dispatch_journal_entries_idempotency';
                    """;
                var indexDef = await indexCmd.ExecuteScalarAsync(CancellationToken.None) as string;
                Assert.IsNotNull(indexDef, "幂等键唯一索引应保留。");
                StringAssert.Contains(indexDef, "workspace_id", "幂等键索引作用域应含 workspace_id。");
                StringAssert.Contains(indexDef, "tool_name", "幂等键索引作用域应含 tool_name（provider 命名空间）。");

                // 迁移后复合键寻址：同 request_id 跨工作区可各自独立写入（主键含 workspace_id）。
                var insertCmd = conn.CreateCommand();
                insertCmd.CommandText = """
                    INSERT INTO legacy_tool_dispatch_journal_entries (request_id, tool_name, state, workspace_id, run_id)
                    VALUES ('req-legacy-1', 'bank-transfer', 2, 'ws-other', 'run-legacy-1');
                    """;
                await insertCmd.ExecuteNonQueryAsync(CancellationToken.None);
                var countCmd = conn.CreateCommand();
                countCmd.CommandText = "SELECT COUNT(1) FROM legacy_tool_dispatch_journal_entries WHERE request_id = 'req-legacy-1';";
                Assert.AreEqual(2L, Convert.ToInt64(await countCmd.ExecuteScalarAsync(CancellationToken.None)),
                    "同 request_id 跨工作区应各自独立（复合主键隔离）。");
            }

            await factory.DisposeAsync();
        }
    }

    /// <summary>
    /// 验证：v68 存量库存在歧义 run 映射（同一 run_id 对应多个 workspace_id）时，
    /// 迁移必须阻断失败（PreCheck fail-closed）——Tool Journal 是外部副作用审计真相，
    /// 绝不替系统猜映射，要求人工修复后重试。
    /// </summary>
    [TestMethod]
    public async Task ToolDispatchWorkspaceKeyStep_AmbiguousRunMapping_BlocksMigration()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — 歧义映射阻断测试已跳过。");
            return;
        }

        await using (container)
        {
            var options = new PostgresOptions
            {
                ConnectionString = container.GetConnectionString(),
                AutoMigrate = false,
                EnablePgVectorExtension = true,
                TablePrefix = "amb_"
            };
            var factory = new PostgresConnectionFactory(options);

            // 构造 v68 存量库：agent_runs 中 run-amb 同时属于 ws-A 与 ws-B（歧义），
            // journal 旧行只有 run-amb（缺 workspace_id）。
            await using (var conn = await factory.OpenConnectionAsync(CancellationToken.None))
            {
                var cmd = conn.CreateCommand();
                cmd.CommandTimeout = options.CommandTimeoutSeconds;
                cmd.CommandText = """
                    CREATE TABLE amb_agent_runs (
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
                    CREATE TABLE amb_tool_dispatch_journal_entries (
                        request_id text NOT NULL PRIMARY KEY,
                        tool_name text NOT NULL DEFAULT '',
                        state smallint NOT NULL DEFAULT 0,
                        idempotency_key text NULL,
                        payload_digest text NULL,
                        external_operation_id text NULL,
                        workspace_id text NULL,
                        run_id text NULL,
                        created_at timestamptz NULL,
                        updated_at timestamptz NULL,
                        diagnostic_note text NULL);
                    CREATE TABLE amb_schema_versions (
                        version text NOT NULL,
                        applied_at timestamptz NOT NULL,
                        PRIMARY KEY (version));
                    INSERT INTO amb_schema_versions (version, applied_at) VALUES ('cc-schema-v68', now());

                    INSERT INTO amb_agent_runs (workspace_id, run_id, created_at, updated_at)
                    VALUES ('ws-A', 'run-amb', now(), now()), ('ws-B', 'run-amb', now(), now());
                    INSERT INTO amb_tool_dispatch_journal_entries (request_id, tool_name, state, run_id)
                    VALUES ('req-amb-1', 'bank-transfer', 2, 'run-amb');
                    """;
                await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            }

            // 迁移必须被歧义阻断（fail-closed），不得替系统猜映射。
            var runner = new PostgresMigrationRunner(factory);
            var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await runner.MigrateAsync(CancellationToken.None));
            StringAssert.Contains(ex.Message, "歧义 run 映射",
                "歧义映射必须阻断迁移并给出明确原因。");

            // 版本不得推进（迁移失败未完成）。
            Assert.AreEqual("cc-schema-v68", await runner.GetAppliedVersionAsync(CancellationToken.None),
                "迁移被阻断时版本不得推进。");

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
