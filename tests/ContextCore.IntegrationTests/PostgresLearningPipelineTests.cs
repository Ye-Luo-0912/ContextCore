using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.MemoryEvolution;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Testcontainers.PostgreSql;

namespace ContextCore.IntegrationTests;

/// <summary>
/// Learning 闭环 Postgres 生产持久化验收（WP-I）：
/// Decision Commit Outbox（durable）→ 决策记录落库 → Utility Ledger 物化 →
/// DatasetSnapshot 工件持久化 → 按 SnapshotId 重建（Replay）。
/// 全部组件在真实 Postgres（v72/v73 迁移链）下端到端验证。
/// </summary>
[TestClass]
[TestCategory("Integration")]
[TestCategory("Postgres")]
[TestCategory("DockerRequired")]
public sealed class PostgresLearningPipelineTests
{
    private const string PgVectorImage = "pgvector/pgvector:pg17";

    [TestMethod]
    public async Task DecisionCommitOutbox_Postgres_RoundtripsWithLease()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — Postgres Learning 集成测试已跳过。");
            return;
        }

        await using (container)
        {
            var (factory, migrationRunner, serializer) = CreateInfrastructure("lp1_");
            await using (factory)
            {
                var outbox = new PostgresDecisionCommitOutbox(factory, serializer, migrationRunner);
                var commit = BuildCommit("decision-pg-1");

                // 入队（durable）。
                await outbox.EnqueueAsync(commit);
                // 幂等：重放入队只保留一条。
                await outbox.EnqueueAsync(commit with { EvidenceRef = "sig:replayed" });

                // 领取 + 租约 CAS。
                var pending = await outbox.AcquirePendingAsync(10, "worker-pg", TimeSpan.FromMinutes(1));
                Assert.AreEqual(1, pending.Count, "同 (workspace, decision_id) 幂等。");
                Assert.AreEqual("decision-pg-1", pending[0].DecisionId);
                Assert.AreEqual("sig:replayed", pending[0].EvidenceRef, "重放覆盖为最新内容。");
                Assert.IsFalse(await outbox.AckAsync(pending[0].OutboxId, "wrong-token"), "错误 token 拒绝 Ack。");
                Assert.IsTrue(await outbox.AckAsync(pending[0].OutboxId, pending[0].LeaseToken!), "正确 token Ack 成功。");

                var again = await outbox.AcquirePendingAsync(10, "worker-pg", TimeSpan.FromMinutes(1));
                Assert.AreEqual(0, again.Count, "已 Ack 条目不再领取。");
            }
        }
    }

    [TestMethod]
    public async Task LearningArtifactStore_Postgres_PersistsAndRebuildsSnapshot()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — Postgres Learning 集成测试已跳过。");
            return;
        }

        await using (container)
        {
            var (factory, migrationRunner, serializer) = CreateInfrastructure("lp2_");
            await using (factory)
            {
                var artifactStore = new PostgresLearningArtifactStore(factory, serializer, migrationRunner);
                var snapshot = new DatasetSnapshotReport
                {
                    SnapshotId = "snapshot-pg-1",
                    SchemaVersion = "training-data-export/v1",
                    CreatedAt = DateTimeOffset.UtcNow,
                    WorkspaceId = "ws-lp2",
                    CollectionId = null,
                    ModelArtifactId = "model-pg-1",
                    InputEvidenceCount = 10,
                    MaterializedCount = 8,
                    CompletenessRatio = 0.8,
                    MissingCount = 2,
                    MissingReasons = new[] { "below-threshold" },
                    ContentHash = "pg-hash-1",
                    PolicyVersions = new[] { "policy/v1" },
                    LineageDecisionCount = 4
                };
                var artifact = new DatasetSnapshotArtifact
                {
                    Snapshot = snapshot,
                    DataFilePath = "/tmp/pg-data.jsonl",
                    ManifestFilePath = "/tmp/pg-manifest.json",
                    StoredAt = DateTimeOffset.UtcNow
                };

                await artifactStore.SaveAsync(artifact);

                // 点查重建（Replay 入口）。
                var rebuilt = await artifactStore.GetAsync("ws-lp2", "snapshot-pg-1");
                Assert.IsNotNull(rebuilt, "Postgres 工件按 SnapshotId 点查命中。");
                Assert.AreEqual("snapshot-pg-1", rebuilt!.Snapshot.SnapshotId);
                Assert.AreEqual(0.8, rebuilt.Snapshot.CompletenessRatio!.Value, 0.0001, "完整率持久化保留。");
                Assert.AreEqual("pg-hash-1", rebuilt.Snapshot.ContentHash, "内容哈希持久化保留。");
                Assert.AreEqual("/tmp/pg-data.jsonl", rebuilt.DataFilePath, "物化文件路径保留。");
                Assert.AreEqual(4, rebuilt.Snapshot.LineageDecisionCount);

                // 列表。
                var list = await artifactStore.ListRecentAsync("ws-lp2");
                Assert.AreEqual(1, list.Count, "列表按工作区返回。");

                // 跨工作区隔离。
                Assert.IsNull(await artifactStore.GetAsync("ws-other", "snapshot-pg-1"), "跨工作区不可见。");
            }
        }
    }

    [TestMethod]
    public async Task LearningLoop_Postgres_DecisionCommitToSnapshotArtifact_Closes()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — Postgres Learning 集成测试已跳过。");
            return;
        }

        await using (container)
        {
            var (factory, migrationRunner, serializer) = CreateInfrastructure("lp3_");
            await using (factory)
            {
                // 组件：outbox + decision trace + ledger + artifact（全部 Postgres）。
                var outbox = new PostgresDecisionCommitOutbox(factory, serializer, migrationRunner);
                var decisionTrace = new PostgresDecisionTraceStore(factory, serializer, migrationRunner);
                var ledgerStore = new PostgresUtilityLedgerStore(factory, serializer, migrationRunner);
                var conflictSetStore = new PostgresConflictSetStore(factory, serializer, migrationRunner);
                var artifactStore = new PostgresLearningArtifactStore(factory, serializer, migrationRunner);

                // 1. 决策产生点：入队决策提交（record + 物化意图）。
                var commit = BuildCommit("decision-loop-1");
                await outbox.EnqueueAsync(commit);

                // 2. 消费：决策记录落库（Decision Evidence Plane durable 归档）。
                var pending = await outbox.AcquirePendingAsync(10, "worker-loop", TimeSpan.FromMinutes(1));
                Assert.AreEqual(1, pending.Count);
                await decisionTrace.SaveAsync(pending[0].Record);
                await outbox.AckAsync(pending[0].OutboxId, pending[0].LeaseToken!);

                var persistedRecord = await decisionTrace.GetAsync("ws-lp3", "col-lp3", "decision-loop-1");
                Assert.IsNotNull(persistedRecord, "决策记录 Postgres 落库可点查。");
                Assert.AreEqual("decision-loop-1", persistedRecord!.DecisionId);

                // 3. Learning 物化：决策结果 → Utility Ledger（Postgres）。
                var materializer = new UtilityLedgerMaterializer(ledgerStore, conflictSetStore);
                var decision = BuildDecisionResult("decision-loop-1");
                await materializer.MaterializeAsync(decision, "ws-lp3", "col-lp3");

                var entries = await ledgerStore.QueryAsync(new UtilityLedgerQuery { WorkspaceId = "ws-lp3" });
                Assert.AreEqual(2, entries.Count, "决策 2 个候选物化到 Postgres ledger。");

                // 4. DatasetSnapshot 工件持久化（Postgres）→ 重建。
                using var tempDir = new TempDirectory();
                var exporter = new TrainingDataExporter(ledgerStore);
                var export = await exporter.ExportAsync(new TrainingDataExportRequest
                {
                    WorkspaceId = "ws-lp3",
                    OutputDirectory = tempDir.Path,
                    ModelArtifactId = "model-loop-1"
                });

                var snapshot = export.DatasetSnapshot!;
                await artifactStore.SaveAsync(new DatasetSnapshotArtifact
                {
                    Snapshot = snapshot,
                    DataFilePath = export.DataFilePath,
                    ManifestFilePath = export.ManifestFilePath,
                    StoredAt = DateTimeOffset.UtcNow
                });

                var rebuilt = await artifactStore.GetAsync("ws-lp3", snapshot.SnapshotId);
                Assert.IsNotNull(rebuilt, "Postgres 工件重建命中。");
                Assert.AreEqual(snapshot.ContentHash, rebuilt!.Snapshot.ContentHash, "重建后内容哈希一致（可追责）。");
                Assert.AreEqual(2, rebuilt.Snapshot.MaterializedCount, "闭环快照物化数正确。");
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
            Console.WriteLine($"[PostgresLearningPipelineTests] Docker/Postgres 不可用：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static DecisionCommitOutboxRecord BuildCommit(string decisionId) => new()
    {
        DecisionId = decisionId,
        WorkspaceId = "ws-lp3",
        CollectionId = "col-lp3",
        CommitType = DecisionCommitType.RecordAndMaterialize,
        Record = new ContextDecisionRecord
        {
            DecisionId = decisionId,
            Source = ContextDecisionSource.Retrieval,
            WorkspaceId = "ws-lp3",
            CollectionId = "col-lp3",
            QueryText = "postgres loop query",
            Candidates = Array.Empty<ContextDecisionCandidate>(),
            PolicyVersion = ContextDecisionPolicyVersions.DecisionSchemaV2_0,
            CreatedAt = DateTimeOffset.UtcNow
        },
        EvidenceRef = "sig:pg",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static ContextDecisionResult BuildDecisionResult(string decisionId) => new()
    {
        RequestId = decisionId,
        DecisionSource = ContextDecisionSource.Retrieval,
        PolicyVersion = ContextDecisionPolicyVersions.DecisionSchemaV2_0,
        SelectedEnvelopes = new[]
        {
            new ContextCandidateEnvelope
            {
                CandidateId = "cand-a",
                Source = ContextCandidateSource.WorkingMemory,
                CanonicalKey = CanonicalCandidateKey.Create("ws-lp3", "col-lp3", "memory", "cand-a", "v1"),
                Utility = new CandidateUtilityScore { DeterministicScore = 0.9, FinalScore = 0.9 }
            }
        },
        DroppedEnvelopes = new[]
        {
            new ContextCandidateEnvelope
            {
                CandidateId = "cand-b",
                Source = ContextCandidateSource.WorkingMemory,
                CanonicalKey = CanonicalCandidateKey.Create("ws-lp3", "col-lp3", "memory", "cand-b", "v1"),
                Safety = new CandidateSafetyState { BlockReasonCode = CandidateDecisionReasonCode.TokenBudgetExceeded }
            }
        },
        Outcome = new ContextDecisionOutcomeSummary { SelectedCount = 1, DroppedCount = 1 }
    };

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "cc-learning-pg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // 清理失败忽略（临时目录）。
            }
        }
    }
}
