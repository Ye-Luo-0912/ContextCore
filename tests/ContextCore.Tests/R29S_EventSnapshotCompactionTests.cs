using ContextCore.Abstractions;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.Tests;

/// <summary>
/// Agent Run 事件流快照与压缩（Event Snapshot &amp; Compaction）验收测试。
///
/// 覆盖：
/// 1. Schema 迁移验证：v54 版本 + agent_run_event_snapshots / agent_run_events_archive 新表定义存在
/// 2. ComputeFold 纯函数：前缀折叠 / 钳制 / 空流防御 / 乱序输入稳定排序
///
/// 不连接真实 PostgreSQL 数据库；端到端持久化语义（事务归档 / 热表删除 / 快照 UPSERT）
/// 由 ContextCore.IntegrationTests 覆盖（需 Testcontainers），与 PostgresAgentRunEventStore
/// 约定一致（纯 SQL 生成 + 参数校验 + 哈希链语义均在无连接路径验证）。
/// </summary>
[TestClass]
[TestCategory("Storage")]
[TestCategory("R29")]
public sealed class R29S_EventSnapshotCompactionTests
{
    // =========================================================================
    // Part 1: Schema 迁移验证（v54）
    // =========================================================================

    [TestMethod]
    public void MigrationSql_ShouldCreateSnapshotAndArchiveTables()
    {
        var sql = PostgresMigrationRunner.BuildMigrationSql(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=contextcore;Username=contextcore;Password=contextcore",
            TablePrefix = "cc_",
            EnablePgVectorExtension = false
        });

        // 归档表：结构复制自 agent_run_events（不含 prev_chain_hash 链接），PK 幂等防重复归档。
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_agent_run_events_archive");
        StringAssert.Contains(sql, "PRIMARY KEY (workspace_id, run_id, sequence)");
        // 快照表：per-run 单行（PK = workspace_id + run_id），锚点 + 链头哈希 + 状态摘要 + 折叠计数。
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_agent_run_event_snapshots");
        StringAssert.Contains(sql, "anchor_sequence integer NOT NULL");
        StringAssert.Contains(sql, "chain_head_hash text");
        StringAssert.Contains(sql, "state_json text NOT NULL DEFAULT ''");
        StringAssert.Contains(sql, "folded_event_count integer NOT NULL DEFAULT 0");
        StringAssert.Contains(sql, "archived_row_count integer NOT NULL DEFAULT 0");
        StringAssert.Contains(sql, "compacted_at timestamptz NOT NULL");
    }

    [TestMethod]
    public void RequiredTables_IncludeSnapshotAndArchiveSuffixes()
    {
        CollectionAssert.Contains(
            PostgresMigrationRunner.RequiredOperationalTableSuffixes.ToList(),
            "agent_run_event_snapshots");
        CollectionAssert.Contains(
            PostgresMigrationRunner.RequiredOperationalTableSuffixes.ToList(),
            "agent_run_events_archive");
    }

    [TestMethod]
    public void SchemaVersion_IsV63()
    {
        Assert.AreEqual("cc-schema-v64", PostgresMigrationRunner.SchemaVersion);
    }

    // =========================================================================
    // Part 2: ComputeFold 纯函数
    // =========================================================================

    [TestMethod]
    public void ComputeFold_FoldsPrefixAndKeepsAnchor()
    {
        var events = BuildEvents(count: 5); // sequence 0..4

        var (anchor, archived, folded) = PostgresAgentRunEventCompactor.ComputeFold(events, upToSequence: 2);

        // 锚点 = sequence 2（保留在热表作为新链头）；折叠前缀 [0, 1] 归档。
        Assert.AreEqual(2, anchor.Sequence);
        Assert.AreEqual(2, folded);
        Assert.AreEqual(2, archived.Count);
        Assert.AreEqual(0, archived[0].Sequence);
        Assert.AreEqual(1, archived[1].Sequence);
    }

    [TestMethod]
    public void ComputeFold_ClampsUpToSequenceToLastEvent()
    {
        var events = BuildEvents(count: 4); // sequence 0..3

        var (anchor, archived, folded) = PostgresAgentRunEventCompactor.ComputeFold(events, upToSequence: 10);

        // upToSequence 超过流末尾 → 钳制到最后事件（锚点 = 最后事件）。
        Assert.AreEqual(3, anchor.Sequence);
        Assert.AreEqual(3, folded);
        Assert.AreEqual(3, archived.Count);
    }

    [TestMethod]
    public void ComputeFold_ZeroUpToSequence_ArchivesNothing()
    {
        var events = BuildEvents(count: 5);

        var (anchor, archived, folded) = PostgresAgentRunEventCompactor.ComputeFold(events, upToSequence: 0);

        // 锚点 = sequence 0；无折叠事件（等价于空压缩，仅建立快照）。
        Assert.AreEqual(0, anchor.Sequence);
        Assert.AreEqual(0, folded);
        Assert.AreEqual(0, archived.Count);
    }

    [TestMethod]
    public void ComputeFold_ThrowsOnEmptyStream()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            PostgresAgentRunEventCompactor.ComputeFold(Array.Empty<AgentRunEvent>(), upToSequence: 3));
    }

    [TestMethod]
    public void ComputeFold_UnsortedInput_ReturnsAscendingArchive()
    {
        var events = BuildEvents(count: 5); // sequence 0..4
        var reversed = events.Reverse().ToList(); // 4..0 乱序输入

        var (anchor, archived, folded) = PostgresAgentRunEventCompactor.ComputeFold(reversed, upToSequence: 2);

        // 防御排序：归档事件按 sequence 升序返回，锚点取 sequence == upToSequence 的事件。
        Assert.AreEqual(2, anchor.Sequence);
        Assert.AreEqual(2, folded);
        Assert.AreEqual(0, archived[0].Sequence);
        Assert.AreEqual(1, archived[1].Sequence);
    }

    // =========================================================================
    // 辅助
    // =========================================================================

    private static IReadOnlyList<AgentRunEvent> BuildEvents(int count)
    {
        var list = new List<AgentRunEvent>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(new AgentRunEvent
            {
                EventId = $"evt-{i}",
                RunId = "run-1",
                WorkspaceId = "ws-1",
                Sequence = i,
                EventType = AgentRunEventType.RunCreated,
                State = AgentRunState.Created,
                Payload = $"payload-{i}",
                ContentHash = $"hash-{i}",
                PrevChainHash = i == 0 ? null : $"hash-{i - 1}",
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(i)
            });
        }
        return list;
    }
}
