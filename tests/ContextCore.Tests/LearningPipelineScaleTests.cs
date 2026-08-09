using ContextCore.Abstractions;
using ContextCore.Core.Services.MemoryEvolution;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// Learning 闭环规模/压测（WP-R）：大数据量下的快照导出、工件重建、outbox 全链路吞吐。
/// 验证上限行为：数据量大时快照完整性/哈希/重建仍正确，outbox 批量领取-Ack 无残留。
/// </summary>
[TestClass]
[TestCategory("Learning-Event")]
public sealed class LearningPipelineScaleTests
{
    private const string Ws = "ws-scale";

    [TestMethod]
    public async Task SnapshotExport_AtScale_PreservesCompletenessAndRebuilds()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        const int entryCount = 10_000;

        // 大数据量 ledger（10k 条，选中/丢弃混合）。
        var ledgerStore = new InMemoryUtilityLedgerStore();
        var entries = new List<UtilityLedgerEntry>(entryCount);
        for (var i = 0; i < entryCount; i++)
        {
            var selected = i % 3 != 0;
            entries.Add(new UtilityLedgerEntry
            {
                EntryId = $"e-{i}",
                WorkspaceId = Ws,
                CollectionId = "col-scale",
                CandidateItemId = $"item-{i}",
                Expert = RetrievalExpert.Semantic,
                UtilityContribution = selected ? 0.9 : 0.1,
                DeterministicScore = selected ? 0.9 : 0.1,
                FinalScore = selected ? 0.88 : 0.1,
                IsSelected = selected,
                DropReasonCode = selected ? null : "below-threshold",
                DecisionId = $"decision-{i % 100}",
                PolicyVersion = "policy/v1",
                MaterializedAt = DateTimeOffset.UtcNow.AddMinutes(-i)
            });
        }
        await ledgerStore.AppendEntriesAsync(entries, CancellationToken.None);

        // 导出（10k 条）→ 快照完整性。
        var exporter = new TrainingDataExporter(ledgerStore);
        using var tempDir = new TempDirectory();
        var export = await exporter.ExportAsync(new TrainingDataExportRequest
        {
            WorkspaceId = Ws,
            OutputDirectory = tempDir.Path,
            ModelArtifactId = "model-scale"
        }, cts.Token);

        Assert.AreEqual(entryCount, export.EntryCount, "全量导出 10k 条。");
        var snapshot = export.DatasetSnapshot!;
        Assert.AreEqual(entryCount, snapshot.MaterializedCount);
        Assert.AreEqual(1.0, snapshot.CompletenessRatio!.Value, 0.0001, "完整率 = 物化 / 输入。");
        Assert.IsFalse(string.IsNullOrWhiteSpace(snapshot.ContentHash), "内容哈希存在。");
        Assert.AreEqual(100, snapshot.LineageDecisionCount, "血缘覆盖 100 个源决策。");

        // 工件落库 → 重建一致（Replay 规模路径）。
        var artifactStore = new InMemoryLearningArtifactStore();
        await artifactStore.SaveAsync(new DatasetSnapshotArtifact
        {
            Snapshot = snapshot,
            DataFilePath = export.DataFilePath,
            ManifestFilePath = export.ManifestFilePath,
            StoredAt = DateTimeOffset.UtcNow
        }, cts.Token);

        var rebuilt = await artifactStore.GetAsync(Ws, snapshot.SnapshotId, cts.Token);
        Assert.IsNotNull(rebuilt, "按 SnapshotId 重建命中。");
        Assert.AreEqual(snapshot.ContentHash, rebuilt!.Snapshot.ContentHash, "重建后内容哈希一致。");
    }

    [TestMethod]
    public async Task DecisionCommitOutbox_AtScale_DrainsFullyWithoutLoss()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        const int commitCount = 2_000;
        var outbox = new InMemoryDecisionCommitOutbox();

        // 批量入队 2k 条决策提交。
        for (var i = 0; i < commitCount; i++)
        {
            await outbox.EnqueueAsync(new DecisionCommitOutboxRecord
            {
                DecisionId = $"decision-scale-{i}",
                WorkspaceId = Ws,
                CollectionId = "col-scale",
                CommitType = DecisionCommitType.RecordOnly,
                Record = new ContextCore.Abstractions.Models.ContextDecisionRecord
                {
                    DecisionId = $"decision-scale-{i}",
                    Source = ContextCore.Abstractions.Models.ContextDecisionSource.Retrieval,
                    WorkspaceId = Ws,
                    CollectionId = "col-scale",
                    QueryText = $"query-{i}",
                    Candidates = Array.Empty<ContextCore.Abstractions.Models.ContextDecisionCandidate>(),
                    PolicyVersion = "decision-schema/2.0",
                    CreatedAt = DateTimeOffset.UtcNow
                },
                CreatedAt = DateTimeOffset.UtcNow
            }, cts.Token);
        }

        // 全量领取（20/批）→ Ack，无残留（吞吐路径）。
        var acked = 0;
        while (acked < commitCount)
        {
            var batch = await outbox.AcquirePendingAsync(20, "worker-scale", TimeSpan.FromMinutes(1), cts.Token);
            Assert.IsTrue(batch.Count > 0, "未取完前每批都应非空（队列不丢）。");
            foreach (var commit in batch)
            {
                Assert.IsTrue(await outbox.AckAsync(commit.OutboxId, commit.LeaseToken!, cts.Token), "Ack 应成功。");
            }
            acked += batch.Count;
        }

        Assert.AreEqual(commitCount, acked, "全部 2k 条领取并 Ack。");
        var leftover = await outbox.AcquirePendingAsync(20, "probe", TimeSpan.FromMinutes(1), cts.Token);
        Assert.AreEqual(0, leftover.Count, "全量消费后无残留。");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "cc-learning-scale-" + Guid.NewGuid().ToString("N"));
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
