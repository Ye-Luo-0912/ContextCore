using System.Diagnostics;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.MemoryEvolution;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Testcontainers.PostgreSql;

namespace ContextCore.IntegrationTests;

/// <summary>
/// Learning 闭环 Postgres 全链路压测（WP-V）：决策提交 → 记录落库 → 物化 ledger →
/// 快照导出 → 工件持久化 → 重建，端到端吞吐与正确性（500 决策规模）。
/// 不硬断言时间（环境相关），报告各阶段耗时并验证全量正确。
/// </summary>
[TestClass]
[TestCategory("Integration")]
[TestCategory("Postgres")]
[TestCategory("DockerRequired")]
public sealed class PostgresLearningPipelineLoadTests
{
    private const string PgVectorImage = "pgvector/pgvector:pg17";
    private const int DecisionCount = 500;

    [TestMethod]
    public async Task FullLoop_500Decisions_EndToEnd_CorrectWithThroughputReport()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — Postgres 压测已跳过。");
            return;
        }

        await using (container)
        {
            var (factory, migrationRunner, serializer) = CreateInfrastructure("load_");
            await using (factory)
            {
                var outbox = new PostgresDecisionCommitOutbox(factory, serializer, migrationRunner);
                var decisionTrace = new PostgresDecisionTraceStore(factory, serializer, migrationRunner);
                var ledgerStore = new PostgresUtilityLedgerStore(factory, serializer, migrationRunner);
                var conflictSetStore = new PostgresConflictSetStore(factory, serializer, migrationRunner);
                var artifactStore = new PostgresLearningArtifactStore(factory, serializer, migrationRunner);

                var timeline = new Dictionary<string, long>();

                // 1. 入队 500 条决策提交（durable）。
                var enqueueSw = Stopwatch.StartNew();
                for (var i = 0; i < DecisionCount; i++)
                {
                    await outbox.EnqueueAsync(BuildCommit($"decision-load-{i}"));
                }
                enqueueSw.Stop();
                timeline["enqueue"] = enqueueSw.ElapsedMilliseconds;

                // 2. 消费：记录落库（全量）。
                var persistSw = Stopwatch.StartNew();
                var persisted = 0;
                while (persisted < DecisionCount)
                {
                    var batch = await outbox.AcquirePendingAsync(50, "worker-load", TimeSpan.FromMinutes(1));
                    Assert.IsTrue(batch.Count > 0, "未消费完前批次非空。");
                    foreach (var commit in batch)
                    {
                        await decisionTrace.SaveAsync(commit.Record);
                        await outbox.AckAsync(commit.OutboxId, commit.LeaseToken!);
                    }
                    persisted += batch.Count;
                }
                persistSw.Stop();
                timeline["persist-records"] = persistSw.ElapsedMilliseconds;
                Assert.AreEqual(DecisionCount, persisted);

                // 3. 物化 ledger（500 决策 × 2 候选 = 1000 条目）。
                var materializeSw = Stopwatch.StartNew();
                var materializer = new UtilityLedgerMaterializer(ledgerStore, conflictSetStore);
                for (var i = 0; i < DecisionCount; i++)
                {
                    await materializer.MaterializeAsync(BuildDecisionResult($"decision-load-{i}"), "ws-load", "col-load");
                }
                materializeSw.Stop();
                timeline["materialize"] = materializeSw.ElapsedMilliseconds;

                var entries = await ledgerStore.QueryAsync(new UtilityLedgerQuery { WorkspaceId = "ws-load", Take = 0 });
                Assert.AreEqual(DecisionCount * 2, entries.Count, "500 决策 × 2 候选全部物化。");

                // 4. 快照导出 + 工件持久化 + 重建。
                var exportSw = Stopwatch.StartNew();
                using var tempDir = new TempDirectory();
                var exporter = new TrainingDataExporter(ledgerStore);
                var export = await exporter.ExportAsync(new TrainingDataExportRequest
                {
                    WorkspaceId = "ws-load",
                    OutputDirectory = tempDir.Path,
                    ModelArtifactId = "model-load"
                });
                exportSw.Stop();
                timeline["export-snapshot"] = exportSw.ElapsedMilliseconds;

                var snapshot = export.DatasetSnapshot!;
                Assert.AreEqual(DecisionCount * 2, snapshot.MaterializedCount, "快照全量物化。");
                Assert.AreEqual(1.0, snapshot.CompletenessRatio!.Value, 0.0001);

                var storeSw = Stopwatch.StartNew();
                await artifactStore.SaveAsync(new DatasetSnapshotArtifact
                {
                    Snapshot = snapshot,
                    DataFilePath = export.DataFilePath,
                    ManifestFilePath = export.ManifestFilePath,
                    StoredAt = DateTimeOffset.UtcNow
                });
                var rebuilt = await artifactStore.GetAsync("ws-load", snapshot.SnapshotId);
                storeSw.Stop();
                timeline["store-and-rebuild"] = storeSw.ElapsedMilliseconds;

                Assert.IsNotNull(rebuilt, "工件重建命中。");
                Assert.AreEqual(snapshot.ContentHash, rebuilt!.Snapshot.ContentHash, "重建内容哈希一致。");

                // 5. 吞吐报告（诊断输出；不做硬断言）。
                Console.WriteLine($"[PostgresLearningPipelineLoadTests] 全链路耗时：");
                foreach (var (stage, ms) in timeline)
                {
                    Console.WriteLine($"  {stage}: {ms} ms");
                }
                Console.WriteLine($"  总样本: {export.EntryCount}，快照重建: {rebuilt.Snapshot.SnapshotId}");
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
            Console.WriteLine($"[PostgresLearningPipelineLoadTests] Docker/Postgres 不可用：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static DecisionCommitOutboxRecord BuildCommit(string decisionId) => new()
    {
        DecisionId = decisionId,
        WorkspaceId = "ws-load",
        CollectionId = "col-load",
        CommitType = DecisionCommitType.RecordAndMaterialize,
        Record = new ContextDecisionRecord
        {
            DecisionId = decisionId,
            Source = ContextDecisionSource.Retrieval,
            WorkspaceId = "ws-load",
            CollectionId = "col-load",
            QueryText = "load query",
            Candidates = Array.Empty<ContextDecisionCandidate>(),
            PolicyVersion = ContextDecisionPolicyVersions.DecisionSchemaV2_0,
            CreatedAt = DateTimeOffset.UtcNow
        },
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
                CandidateId = $"cand-sel-{decisionId}",
                Source = ContextCandidateSource.WorkingMemory,
                CanonicalKey = CanonicalCandidateKey.Create("ws-load", "col-load", "memory", $"cand-sel-{decisionId}", "v1"),
                Utility = new CandidateUtilityScore { DeterministicScore = 0.9, FinalScore = 0.9 }
            }
        },
        DroppedEnvelopes = new[]
        {
            new ContextCandidateEnvelope
            {
                CandidateId = $"cand-drop-{decisionId}",
                Source = ContextCandidateSource.WorkingMemory,
                CanonicalKey = CanonicalCandidateKey.Create("ws-load", "col-load", "memory", $"cand-drop-{decisionId}", "v1"),
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
                System.IO.Path.GetTempPath(), "cc-learning-load-" + Guid.NewGuid().ToString("N"));
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
