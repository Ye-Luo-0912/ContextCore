using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.IntegrationTests.TestFixtures;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.IntegrationTests;

// ===========================================================================
// 事件流规模恢复集成测试
//
// 目标：证明"10,000 条以上事件可以分页恢复"：
// 1. 通过 AppendBatchAsync 分 10 批写入 10,000 条事件（哈希链连续）；
// 2. 以每页 1,000 条分页读取（ReadAsync(fromSequence, take)），共 10 页；
// 3. 验证：恢复条数 = 10,000、最后 sequence = 9,999、跨分页边界的哈希链完整。
//
// 设计原则：
// - 使用真实 Postgres stores（PostgresAgentRunStore / PostgresAgentRunEventStore）。
// - Docker/Postgres 不可用时 Assert.Inconclusive 跳过。
// - 独立 tablePrefix 避免数据交叉污染。
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Production-Evidence")]
[TestCategory("Integration")]
[TestCategory("Postgres")]
[TestCategory("DockerRequired")]
[TestCategory("ScaleE2E")]
public sealed class R29H_ProductionEvidenceScaleE2ETests : IAsyncDisposable
{
    private readonly PostgresE2EFixture _pg = new();

    [TestInitialize]
    public async Task InitializeAsync() => await _pg.StartAsync();

    [TestCleanup]
    public Task CleanupAsync() => _pg.DisposeAsync().AsTask();

    [TestMethod]
    public async Task E2E_TenThousandEvents_PaginatedRecovery_ChainIntact()
    {
        if (_pg.ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。此结果不证明生产证据通过。"); return; }

        var (factory, migrationRunner, serializer) = _pg.CreateInfrastructure("10k_");
        try
        {
            await migrationRunner.MigrateAsync();
            var runStore = new PostgresAgentRunStore(factory, serializer, migrationRunner);
            var eventStore = new PostgresAgentRunEventStore(factory, serializer, migrationRunner);

            var run = BuildRun("10k 事件分页恢复测试");
            await runStore.CreateAsync(run);

            // ── 写入 10,000 条事件（10 批 × 1,000 条，哈希链连续）──
            const int totalEvents = 10_000;
            const int batchSize = 1_000;
            string? prevHash = null;

            for (var batchStart = 0; batchStart < totalEvents; batchStart += batchSize)
            {
                var batch = new List<AgentRunEvent>(batchSize);
                for (var i = 0; i < batchSize; i++)
                {
                    var seq = batchStart + i;
                    var evt = AgentRunEventChain.BuildEvent(
                        run.RunId, run.WorkspaceId, seq,
                        AgentRunEventType.ObservationAppended, run.State,
                        JsonSerializer.Serialize(new { seq, payload = $"event-{seq}" }),
                        prevHash);
                    prevHash = evt.ContentHash;
                    batch.Add(evt);
                }
                await eventStore.AppendBatchAsync(
                    batch, runStateUpdate: null, checkpointCursor: null, checkpointBody: null);
            }

            // ── 分页恢复：每页 1,000 条，共 10 页 ──
            var recovered = new List<AgentRunEvent>(totalEvents);
            const int pageSize = 1_000;
            for (var from = 0; ; from += pageSize)
            {
                var page = await eventStore.ReadAsync(run.WorkspaceId, run.RunId, from, pageSize);
                if (page.Count == 0)
                {
                    break;
                }
                recovered.AddRange(page);
                if (page.Count < pageSize)
                {
                    break;
                }
            }

            // ── 断言 1：恢复条数精确 = 10,000 ──
            Assert.AreEqual(totalEvents, recovered.Count,
                $"应恢复 {totalEvents} 条事件，实际 {recovered.Count}。");

            // ── 断言 2：最后 sequence = 9,999（GetLastSequenceAsync 与分页一致）──
            var lastSequence = await eventStore.GetLastSequenceAsync(run.WorkspaceId, run.RunId);
            Assert.AreEqual(totalEvents - 1, lastSequence,
                $"最后 sequence 应为 {totalEvents - 1}，实际 {lastSequence}。");

            // ── 断言 3：哈希链完整（含跨分页边界）──
            Assert.IsNull(recovered[0].PrevChainHash, "链头事件 PrevChainHash 应为 null。");
            for (var i = 1; i < recovered.Count; i++)
            {
                Assert.AreEqual(recovered[i - 1].ContentHash, recovered[i].PrevChainHash,
                    $"事件 {i} 的 PrevChainHash 应指向前一事件 ContentHash（哈希链断裂）。");
            }

            // ── 断言 4：sequence 严格递增且无空洞 ──
            for (var i = 0; i < recovered.Count; i++)
            {
                Assert.AreEqual(i, recovered[i].Sequence,
                    $"分页恢复后事件 {i} 的 Sequence 应为 {i}（无空洞/重复）。");
            }
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    private static AgentRun BuildRun(string task) => new()
    {
        RunId = "run-10k-" + Guid.NewGuid().ToString("N"),
        WorkspaceId = "ws-10k-prodevidence",
        SessionId = "session-10k-prodevidence",
        Task = task,
        State = AgentRunState.Created,
        Turn = 0,
        ModelCallsUsed = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 0, MaxModelCalls = 5 }
    };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _pg.DisposeAsync();
    }
}
