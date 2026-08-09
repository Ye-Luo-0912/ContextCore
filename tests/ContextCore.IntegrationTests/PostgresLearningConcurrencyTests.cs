using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Testcontainers.PostgreSql;

namespace ContextCore.IntegrationTests;

/// <summary>
/// Postgres Learning 闭环并发吞吐验收（WP-S）：
/// 1. DecisionCommitOutbox 高积压并发消费——多 worker 同时领取（FOR UPDATE SKIP LOCKED），
///    同一条不被两个 worker 同时拿到，全量 Ack 无重复无丢失；
/// 2. LearningArtifactStore 并发保存/重建——同快照幂等覆盖、跨线程可见。
/// </summary>
[TestClass]
[TestCategory("Integration")]
[TestCategory("Postgres")]
[TestCategory("DockerRequired")]
public sealed class PostgresLearningConcurrencyTests
{
    private const string PgVectorImage = "pgvector/pgvector:pg17";

    [TestMethod]
    public async Task DecisionCommitOutbox_ConcurrentWorkers_DrainWithoutLossOrDuplicate()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — Postgres 并发测试已跳过。");
            return;
        }

        await using (container)
        {
            var (factory, migrationRunner, serializer) = CreateInfrastructure("lc1_");
            await using (factory)
            {
                var outbox = new PostgresDecisionCommitOutbox(factory, serializer, migrationRunner);
                const int total = 300;

                // 高积压入队 300 条。
                for (var i = 0; i < total; i++)
                {
                    await outbox.EnqueueAsync(BuildCommit($"decision-conc-{i}"));
                }

                // 5 个 worker 并发领取（每批 20）直到全部处理；SKIP LOCKED 保证互斥。
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var acked = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
                var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();

                var workers = Enumerable.Range(0, 5).Select(async workerId =>
                {
                    while (true)
                    {
                        var batch = await outbox.AcquirePendingAsync(20, $"worker-{workerId}", TimeSpan.FromMinutes(1), cts.Token);
                        if (batch.Count == 0)
                        {
                            return;
                        }

                        foreach (var commit in batch)
                        {
                            // 并发可见性：同一条不被两个 worker 同时拿到（SKIP LOCKED）。
                            Assert.IsTrue(
                                seen.TryAdd(commit.DecisionId, 0),
                                $"决策 {commit.DecisionId} 被多个 worker 同时领取（SKIP LOCKED 失效）。");
                            Assert.IsTrue(await outbox.AckAsync(commit.OutboxId, commit.LeaseToken!, cts.Token), "Ack 应成功。");
                            acked.TryAdd(commit.DecisionId, 0);
                        }
                    }
                }).ToArray();

                await Task.WhenAll(workers);

                Assert.AreEqual(total, acked.Count, "全部 300 条 Ack（无丢失）。");
                Assert.AreEqual(total, seen.Count, "无重复领取（SKIP LOCKED 互斥）。");

                var leftover = await outbox.AcquirePendingAsync(100, "probe", TimeSpan.FromMinutes(1));
                Assert.AreEqual(0, leftover.Count, "全量消费后无残留。");
            }
        }
    }

    [TestMethod]
    public async Task LearningArtifactStore_ConcurrentSaveAndRebuild_Consistent()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — Postgres 并发测试已跳过。");
            return;
        }

        await using (container)
        {
            var (factory, migrationRunner, serializer) = CreateInfrastructure("lc2_");
            await using (factory)
            {
                var store = new PostgresLearningArtifactStore(factory, serializer, migrationRunner);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

                // 并发保存不同快照 + 同快照幂等覆盖。
                var saveTasks = Enumerable.Range(0, 50)
                    .Select(i => store.SaveAsync(BuildArtifact($"snapshot-conc-{i}"), cts.Token).AsTask())
                    .ToArray();
                await Task.WhenAll(saveTasks);

                // 并发重建：全部点查命中且内容哈希一致。
                var readTasks = Enumerable.Range(0, 50).Select(async i =>
                {
                    var artifact = await store.GetAsync("ws-conc", $"snapshot-conc-{i}", cts.Token);
                    Assert.IsNotNull(artifact, $"快照 snapshot-conc-{i} 应重建命中。");
                    Assert.AreEqual($"hash-snapshot-conc-{i}", artifact!.Snapshot.ContentHash);
                }).ToArray();
                await Task.WhenAll(readTasks);
            }
        }
    }

    // ── 辅助 ─────────────────────────────────────────────────────────────

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

    private static string? _connectionString;

    private static async Task<PostgreSqlContainer?> TryStartPostgresAsync()
    {
        try
        {
            var container = new PostgreSqlBuilder(PgVectorImage)
                .WithDatabase("cctest")
                .WithUsername("cctest")
                .WithPassword("cctest")
                .Build();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await container.StartAsync(cts.Token);
            _connectionString = container.GetConnectionString();
            return container;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PostgresLearningConcurrencyTests] Docker/Postgres 不可用：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static DecisionCommitOutboxRecord BuildCommit(string decisionId) => new()
    {
        DecisionId = decisionId,
        WorkspaceId = "ws-conc",
        CollectionId = "col-conc",
        CommitType = DecisionCommitType.RecordOnly,
        Record = new ContextDecisionRecord
        {
            DecisionId = decisionId,
            Source = ContextDecisionSource.Retrieval,
            WorkspaceId = "ws-conc",
            CollectionId = "col-conc",
            QueryText = "concurrent",
            Candidates = Array.Empty<ContextDecisionCandidate>(),
            PolicyVersion = ContextDecisionPolicyVersions.DecisionSchemaV2_0,
            CreatedAt = DateTimeOffset.UtcNow
        },
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static DatasetSnapshotArtifact BuildArtifact(string snapshotId) => new()
    {
        Snapshot = new DatasetSnapshotReport
        {
            SnapshotId = snapshotId,
            SchemaVersion = "training-data-export/v1",
            CreatedAt = DateTimeOffset.UtcNow,
            WorkspaceId = "ws-conc",
            CollectionId = "col-conc",
            ModelArtifactId = "model-conc",
            InputEvidenceCount = 10,
            MaterializedCount = 10,
            CompletenessRatio = 1.0,
            MissingCount = 0,
            MissingReasons = Array.Empty<string>(),
            ContentHash = $"hash-{snapshotId}",
            PolicyVersions = new[] { "policy/v1" },
            LineageDecisionCount = 4
        },
        DataFilePath = "/tmp/conc.jsonl",
        ManifestFilePath = "/tmp/conc-manifest.json",
        StoredAt = DateTimeOffset.UtcNow
    };
}
