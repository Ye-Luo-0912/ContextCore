using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.MemoryEvolution;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// Learning 闭环端到端验收（WP-H）：Decision → Learning Materialization →
/// DatasetSnapshot 工件 → 校准导出 → 工件可重建。
/// 闭环证明：Learning 数据可被严格证明"完整、可重建、可追责"；
/// Replay（按 SnapshotId 重建）与 Canary/Promotion（既有组件，见各自测试）为闭环下游。
/// </summary>
[TestClass]
[TestCategory("Learning-Event")]
public sealed class LearningPipelineE2ETests
{
    private const string Ws = "ws-learning-e2e";
    private const string Collection = "col-learning-e2e";

    [TestMethod]
    public async Task LearningLoop_DecisionToSnapshotToCalibration_Closes()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ledgerStore = new InMemoryUtilityLedgerStore();
        var conflictSetStore = new InMemoryConflictSetStore();
        var artifactStore = new InMemoryLearningArtifactStore();

        // 1. 决策执行 → Learning 物化（Utility Ledger + ConflictSet）。
        var materializer = new UtilityLedgerMaterializer(ledgerStore, conflictSetStore);
        var decision = BuildDecisionResult(decisionId: "decision-e2e", selectedCount: 2, droppedCount: 1);
        await materializer.MaterializeAsync(decision, Ws, Collection, cts.Token);

        var ledgerEntries = await ledgerStore.QueryAsync(new UtilityLedgerQuery { WorkspaceId = Ws }, cts.Token);
        Assert.AreEqual(3, ledgerEntries.Count, "决策 3 个候选全部物化到 ledger。");

        // 2. 训练数据导出 → DatasetSnapshot（完整性 / 血缘 / 内容哈希）。
        var exporter = new TrainingDataExporter(ledgerStore);
        using var trainingDir = new TempDirectory();
        var export = await exporter.ExportAsync(new TrainingDataExportRequest
        {
            WorkspaceId = Ws,
            CollectionId = Collection,
            OutputDirectory = trainingDir.Path,
            ModelArtifactId = "model-e2e-001"
        }, cts.Token);

        Assert.AreEqual(3, export.EntryCount, "全部物化样本导出。");
        var snapshot = export.DatasetSnapshot;
        Assert.IsNotNull(snapshot, "导出应携带数据集快照。");
        Assert.AreEqual(3, snapshot!.MaterializedCount);
        Assert.AreEqual(1.0, snapshot.CompletenessRatio!.Value, 0.0001, "完整率 = 物化 / 输入。");
        Assert.AreEqual(1, snapshot.LineageDecisionCount, "血缘覆盖 1 个源决策（本闭环单一决策）。");
        Assert.IsFalse(string.IsNullOrWhiteSpace(snapshot.ContentHash), "内容哈希存在。");
        Assert.AreEqual("model-e2e-001", snapshot.ModelArtifactId, "模型版本追责。");

        // 3. Learning Artifact Plane：工件落库 → 按 SnapshotId 重建（Replay 入口，可重现）。
        await artifactStore.SaveAsync(new DatasetSnapshotArtifact
        {
            Snapshot = snapshot,
            DataFilePath = export.DataFilePath,
            ManifestFilePath = export.ManifestFilePath,
            StoredAt = DateTimeOffset.UtcNow
        }, cts.Token);

        var replay = await artifactStore.GetAsync(Ws, snapshot.SnapshotId, cts.Token);
        Assert.IsNotNull(replay, "按快照 ID 点查命中（可重建）。");
        Assert.AreEqual(snapshot.SnapshotId, replay!.Snapshot.SnapshotId);
        Assert.AreEqual(snapshot.ContentHash, replay.Snapshot.ContentHash, "重建后内容哈希一致（内容可验证）。");
        Assert.AreEqual(export.DataFilePath, replay.DataFilePath, "物化文件路径可重建。");

        // 4. 校准数据导出（正负样本比例；校准拟合下游消费）。
        var calibrationExporter = new CalibrationDataExporter(ledgerStore);
        using var calibrationDir = new TempDirectory();
        var calibration = await calibrationExporter.ExportAsync(new CalibrationDataExportRequest
        {
            WorkspaceId = Ws,
            CollectionId = Collection,
            OutputDirectory = calibrationDir.Path,
            ModelArtifactId = "model-e2e-001",
            RequireModelScore = false
        }, cts.Token);

        Assert.AreEqual(3, calibration.EntryCount, "校准数据导出全部样本。");
        Assert.AreEqual(2, calibration.PositiveCount, "正样本（选中）2 条。");
        Assert.AreEqual(1, calibration.NegativeCount, "负样本（丢弃）1 条。");
        Assert.IsFalse(string.IsNullOrWhiteSpace(calibration.Sha256Hash), "校准 manifest 内容哈希存在。");
    }

    [TestMethod]
    public async Task LearningLoop_SnapshotId_DeterministicAcrossExports()
    {
        // 可重现性：相同输入条件两次导出 → 相同快照 ID（Replay/审计的确定性基石）。
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ledgerStore = new InMemoryUtilityLedgerStore();
        var conflictSetStore = new InMemoryConflictSetStore();
        var materializer = new UtilityLedgerMaterializer(ledgerStore, conflictSetStore);

        await materializer.MaterializeAsync(
            BuildDecisionResult("decision-repro", selectedCount: 1, droppedCount: 1), Ws, Collection, cts.Token);

        var exporter = new TrainingDataExporter(ledgerStore);
        using var dir1 = new TempDirectory();
        using var dir2 = new TempDirectory();
        var request = new TrainingDataExportRequest
        {
            WorkspaceId = Ws,
            CollectionId = Collection,
            OutputDirectory = dir1.Path,
            ModelArtifactId = "model-repro"
        };

        var first = await exporter.ExportAsync(request, cts.Token);
        var second = await exporter.ExportAsync(request with { OutputDirectory = dir2.Path }, cts.Token);

        Assert.AreEqual(first.DatasetSnapshot!.SnapshotId, second.DatasetSnapshot!.SnapshotId,
            "相同输入条件 → 相同快照 ID（可重现）。");
        Assert.AreEqual(first.Sha256Hash, second.Sha256Hash,
            "相同输入 → 相同内容哈希（内容确定性）。");
    }

    /// <summary>临时目录（测试后自动清理）。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "cc-learning-e2e-" + Guid.NewGuid().ToString("N"));
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

    private static ContextDecisionResult BuildDecisionResult(
        string decisionId, int selectedCount, int droppedCount) => new()
    {
        RequestId = decisionId,
        DecisionSource = ContextDecisionSource.Retrieval,
        PolicyVersion = ContextDecisionPolicyVersions.DecisionSchemaV2_0,
        SelectedEnvelopes = Enumerable.Range(0, selectedCount)
            .Select(i => new ContextCandidateEnvelope
            {
                CandidateId = $"cand-sel-{i}",
                Source = ContextCandidateSource.WorkingMemory,
                CanonicalKey = CanonicalCandidateKey.Create(Ws, Collection, "memory", $"cand-sel-{i}", "v1"),
                Utility = new CandidateUtilityScore { DeterministicScore = 0.9 - i * 0.1, FinalScore = 0.9 - i * 0.1 }
            })
            .ToArray(),
        DroppedEnvelopes = Enumerable.Range(0, droppedCount)
            .Select(i => new ContextCandidateEnvelope
            {
                CandidateId = $"cand-drop-{i}",
                Source = ContextCandidateSource.WorkingMemory,
                CanonicalKey = CanonicalCandidateKey.Create(Ws, Collection, "memory", $"cand-drop-{i}", "v1"),
                Safety = new CandidateSafetyState { BlockReasonCode = CandidateDecisionReasonCode.TokenBudgetExceeded }
            })
            .ToArray(),
        Outcome = new ContextDecisionOutcomeSummary
        {
            SelectedCount = selectedCount,
            DroppedCount = droppedCount
        }
    };
}
