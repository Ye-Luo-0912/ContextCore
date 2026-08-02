using ContextCore.Abstractions;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Testcontainers.PostgreSql;

namespace ContextCore.IntegrationTests;

/// <summary>
/// R29 WP-B-3：PostgresAgentCheckpointStore 端到端集成测试（Testcontainers）。
/// 验证持久化 Agent Checkpoint Store 的 Full/Delta 链路持久化与崩溃恢复。
/// </summary>
/// <remarks>
/// R28-G P1-5 delta 链路由恢复路径（事件流 + checkpoint 链）通过标准 IAgentCheckpointStore.GetAsync 走链，
/// Store 不需感知 delta 语义 — 只持久化完整 AgentCheckpoint blob。本测试验证 Store 正确持久化
/// Full/Delta 两种 checkpoint，且通过 GetAsync 可恢复整条链。
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("Postgres")]
[TestCategory("DockerRequired")]
public sealed class PostgresAgentCheckpointStoreDeltaTests
{
    private const string PgVectorImage = "pgvector/pgvector:pg17";

    private static PostgreSqlContainer? _container;
    private static string? _connectionString;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext _)
    {
        if (!await PostgresIntegrationTests.IsDockerAvailableAsync())
        {
            Console.WriteLine("[PostgresAgentCheckpointStoreDeltaTests] Docker 不可用，所有测试将标记为 Inconclusive。");
            return;
        }

        _container = new PostgreSqlBuilder(PgVectorImage)
            .WithDatabase("cctest")
            .WithUsername("cctest")
            .WithPassword("cctest")
            .Build();

        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static async Task ClassCleanup()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private static bool ShouldSkip => _connectionString is null;

    private static (PostgresConnectionFactory factory, PostgresMigrationRunner migrationRunner, PostgresJsonSerializer serializer) CreateInfrastructure(string prefix)
    {
        var options = new PostgresOptions
        {
            ConnectionString = _connectionString!,
            AutoMigrate = true,
            EnablePgVectorExtension = true,
            TablePrefix = prefix
        };
        var factory = new PostgresConnectionFactory(options);
        var serializer = new PostgresJsonSerializer();
        var migrationRunner = new PostgresMigrationRunner(factory);
        return (factory, migrationRunner, serializer);
    }

    private static AgentSessionId MakeSession(string workspaceId = "ws-delta")
        => new()
        {
            Value = "session-delta-1",
            RuntimeKind = AgentRuntimeKind.Unknown,
            WorkspaceId = workspaceId,
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static AgentCheckpoint MakeCheckpoint(
        string checkpointId,
        AgentSessionId session,
        string stateJson,
        string? snapshotId = null)
        => new()
        {
            CheckpointId = checkpointId,
            Session = session,
            CreatedAt = DateTimeOffset.UtcNow,
            SnapshotId = snapshotId,
            StateJson = stateJson
        };

    [TestMethod]
    public async Task SaveAndGet_FullCheckpoint_RoundtripsAllFields()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("acp1_");
        try
        {
            var store = new PostgresAgentCheckpointStore(factory, serializer, migrationRunner);
            var session = MakeSession();
            // 模拟 Full mode 的 StateJson（包含 KernelCheckpointStateDto 字段）
            var stateJson = """
{"SnapshotId":"snap-1","Mode":0,"BaseCheckpointId":null,"LastSequence":2,"CommittedResults":[{"RequestId":"r1","Succeeded":true,"Result":"p1","Sequence":1}],"PendingResults":[]}
""";
            var checkpoint = MakeCheckpoint("ckpt-full-1", session, stateJson, snapshotId: "snap-1");

            await store.SaveAsync(checkpoint);

            var fetched = await store.GetAsync(session.WorkspaceId, "ckpt-full-1");
            Assert.IsNotNull(fetched, "GetAsync 应返回持久化的 checkpoint。");
            Assert.AreEqual("ckpt-full-1", fetched!.CheckpointId);
            Assert.AreEqual(session.WorkspaceId, fetched.Session.WorkspaceId);
            Assert.AreEqual("snap-1", fetched.SnapshotId);
            Assert.AreEqual(stateJson, fetched.StateJson, "StateJson 应原样往返。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SaveAndGet_DeltaCheckpoint_RoundtripsBaseCheckpointId()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("acp2_");
        try
        {
            var store = new PostgresAgentCheckpointStore(factory, serializer, migrationRunner);
            var session = MakeSession();
            // Delta mode 的 StateJson（Mode=1, BaseCheckpointId 指向 Full）
            var stateJson = """
{"SnapshotId":"snap-2","Mode":1,"BaseCheckpointId":"ckpt-full-1","LastSequence":3,"CommittedResults":[{"RequestId":"r2","Succeeded":true,"Result":"p2","Sequence":3}],"PendingResults":[]}
""";
            var checkpoint = MakeCheckpoint("ckpt-delta-1", session, stateJson, snapshotId: "snap-2");

            await store.SaveAsync(checkpoint);

            var fetched = await store.GetAsync(session.WorkspaceId, "ckpt-delta-1");
            Assert.IsNotNull(fetched);
            Assert.AreEqual("ckpt-delta-1", fetched!.CheckpointId);
            StringAssert.Contains(fetched.StateJson, "\"Mode\":1");
            StringAssert.Contains(fetched.StateJson, "\"BaseCheckpointId\":\"ckpt-full-1\"");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task DeltaChainRecovery_NewStoreInstanceWalksFullThenDelta()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("acp3_");
        try
        {
            // 第一个 store 实例（模拟崩溃前的进程）
            var store1 = new PostgresAgentCheckpointStore(factory, serializer, migrationRunner);
            var session = MakeSession();

            // 保存 Full checkpoint（基链）
            var fullStateJson = """
{"SnapshotId":"snap-full","Mode":0,"BaseCheckpointId":null,"LastSequence":1,"CommittedResults":[{"RequestId":"r-base","Succeeded":true,"Result":"base-payload","Sequence":1}],"PendingResults":[]}
""";
            await store1.SaveAsync(MakeCheckpoint("ckpt-base", session, fullStateJson, "snap-full"));

            // 保存 Delta checkpoint（增量）
            var deltaStateJson = """
{"SnapshotId":"snap-delta","Mode":1,"BaseCheckpointId":"ckpt-base","LastSequence":2,"CommittedResults":[{"RequestId":"r-new","Succeeded":true,"Result":"delta-payload","Sequence":2}],"PendingResults":[]}
""";
            await store1.SaveAsync(MakeCheckpoint("ckpt-delta", session, deltaStateJson, "snap-delta"));

            // 模拟进程崩溃：丢弃 store1，创建新实例（同一数据库）
            var store2 = new PostgresAgentCheckpointStore(factory, serializer, migrationRunner);

            // 新实例通过 GetAsync 恢复 Delta checkpoint
            var deltaFetched = await store2.GetAsync(session.WorkspaceId, "ckpt-delta");
            Assert.IsNotNull(deltaFetched, "崩溃恢复后应能读取 Delta checkpoint。");
            StringAssert.Contains(deltaFetched!.StateJson, "\"Mode\":1");
            StringAssert.Contains(deltaFetched.StateJson, "\"BaseCheckpointId\":\"ckpt-base\"");

            // 模拟恢复路径递归走链：通过 BaseCheckpointId 读取 Full checkpoint
            var baseFetched = await store2.GetAsync(session.WorkspaceId, "ckpt-base");
            Assert.IsNotNull(baseFetched, "通过 BaseCheckpointId 应能读取 Full checkpoint。");
            StringAssert.Contains(baseFetched!.StateJson, "\"Mode\":0");
            StringAssert.Contains(baseFetched.StateJson, "\"r-base\"");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ListAsync_ReturnsCheckpointsBySessionOrderedByCreatedDesc()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("acp4_");
        try
        {
            var store = new PostgresAgentCheckpointStore(factory, serializer, migrationRunner);
            var session = MakeSession();

            // 保存 3 个 checkpoint（时间递增）
            await store.SaveAsync(MakeCheckpoint("ckpt-a", session, "{\"Mode\":0}"));
            await Task.Delay(20); // 确保 created_at 不同
            await store.SaveAsync(MakeCheckpoint("ckpt-b", session, "{\"Mode\":1}"));
            await Task.Delay(20);
            await store.SaveAsync(MakeCheckpoint("ckpt-c", session, "{\"Mode\":1}"));

            var list = await store.ListAsync(session, take: 10);
            Assert.AreEqual(3, list.Count, "应返回该 session 的所有 checkpoint。");
            // 按 created_at DESC 排序：最新的在前
            Assert.AreEqual("ckpt-c", list[0].CheckpointId);
            Assert.AreEqual("ckpt-b", list[1].CheckpointId);
            Assert.AreEqual("ckpt-a", list[2].CheckpointId);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task DeleteAsync_RemovesCheckpointAndReturnsTrue()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("acp5_");
        try
        {
            var store = new PostgresAgentCheckpointStore(factory, serializer, migrationRunner);
            var session = MakeSession();

            await store.SaveAsync(MakeCheckpoint("ckpt-del", session, "{\"Mode\":0}"));

            var deleted = await store.DeleteAsync(session.WorkspaceId, "ckpt-del");
            Assert.IsTrue(deleted, "DeleteAsync 应返回 true。");

            var fetched = await store.GetAsync(session.WorkspaceId, "ckpt-del");
            Assert.IsNull(fetched, "删除后 GetAsync 应返回 null。");

            // 重复删除返回 false
            var deletedAgain = await store.DeleteAsync(session.WorkspaceId, "ckpt-del");
            Assert.IsFalse(deletedAgain, "删除不存在的 checkpoint 应返回 false。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SaveAsync_IsIdempotentUpsertSamePrimaryKey()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("acp6_");
        try
        {
            var store = new PostgresAgentCheckpointStore(factory, serializer, migrationRunner);
            var session = MakeSession();

            // 同 checkpointId 保存两次（第二次覆盖）
            await store.SaveAsync(MakeCheckpoint("ckpt-upsert", session, "{\"Mode\":0,\"v\":1}"));
            await store.SaveAsync(MakeCheckpoint("ckpt-upsert", session, "{\"Mode\":1,\"v\":2}"));

            var fetched = await store.GetAsync(session.WorkspaceId, "ckpt-upsert");
            Assert.IsNotNull(fetched);
            StringAssert.Contains(fetched!.StateJson, "\"v\":2", "第二次 SaveAsync 应覆盖第一次。");
            StringAssert.Contains(fetched.StateJson, "\"Mode\":1");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }
}
