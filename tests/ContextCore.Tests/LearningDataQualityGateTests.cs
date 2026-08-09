using ContextCore.Abstractions;
using ContextCore.Core.Services.MemoryEvolution;

namespace ContextCore.Tests;

/// <summary>
/// Learning 数据质量闸门测试（WP-T）：空数据集阻断、样本缺失警告、标签不平衡警告、正常通过。
/// </summary>
[TestClass]
[TestCategory("Learning-Event")]
public sealed class LearningDataQualityGateTests
{
    private static DatasetSnapshotReport BuildSnapshot(
        int inputEvidenceCount, int materializedCount, int missingCount) => new()
    {
        SnapshotId = "snapshot-quality",
        SchemaVersion = "training-data-export/v1",
        CreatedAt = DateTimeOffset.UtcNow,
        WorkspaceId = "ws-quality",
        CollectionId = "col-quality",
        ModelArtifactId = "model-quality",
        InputEvidenceCount = inputEvidenceCount,
        MaterializedCount = materializedCount,
        CompletenessRatio = inputEvidenceCount > 0
            ? (double)materializedCount / inputEvidenceCount
            : null,
        MissingCount = missingCount,
        MissingReasons = Array.Empty<string>(),
        ContentHash = "quality-hash",
        PolicyVersions = new[] { "policy/v1" },
        LineageDecisionCount = 1
    };

    [TestMethod]
    public void Evaluate_HealthyDataset_Passes()
    {
        var gate = new LearningDataQualityGate();
        var snapshot = BuildSnapshot(inputEvidenceCount: 100, materializedCount: 98, missingCount: 2);

        var report = gate.Evaluate(snapshot, positiveCount: 60, negativeCount: 38);

        Assert.AreEqual(LearningDataQualityVerdict.Passed, report.Verdict, "正常数据集应通过。");
        Assert.AreEqual(0, report.Issues.Count);
        Assert.AreEqual(0.02, report.MissingRatio!.Value, 0.0001, "缺失率 2%。");
    }

    [TestMethod]
    public void Evaluate_EmptyDataset_Blocks()
    {
        var gate = new LearningDataQualityGate();
        var snapshot = BuildSnapshot(inputEvidenceCount: 100, materializedCount: 0, missingCount: 100);

        var report = gate.Evaluate(snapshot, positiveCount: 0, negativeCount: 0);

        Assert.AreEqual(LearningDataQualityVerdict.Blocked, report.Verdict, "空数据集必须阻断。");
        Assert.IsTrue(report.Issues.Any(i => i.Contains("空")), "阻断原因可解释（空数据集）。");
    }

    [TestMethod]
    public void Evaluate_HighMissingRatio_Warns()
    {
        var gate = new LearningDataQualityGate();
        // 缺失率 30% > 10% 阈值。
        var snapshot = BuildSnapshot(inputEvidenceCount: 100, materializedCount: 70, missingCount: 30);

        var report = gate.Evaluate(snapshot, positiveCount: 40, negativeCount: 30);

        Assert.AreEqual(LearningDataQualityVerdict.Warning, report.Verdict, "高缺失率应警告。");
        Assert.IsTrue(report.Issues.Any(i => i.Contains("缺失率")), "警告原因可解释。");
    }

    [TestMethod]
    public void Evaluate_ImbalancedLabels_Warns()
    {
        var gate = new LearningDataQualityGate();
        var snapshot = BuildSnapshot(inputEvidenceCount: 100, materializedCount: 100, missingCount: 0);

        // 正样本占比 1%（100 条中 1 条正）< 5% 阈值 → 标签不平衡。
        var report = gate.Evaluate(snapshot, positiveCount: 1, negativeCount: 99);

        Assert.AreEqual(LearningDataQualityVerdict.Warning, report.Verdict, "标签极端不平衡应警告。");
        Assert.IsTrue(report.Issues.Any(i => i.Contains("标签不平衡")), "警告原因可解释。");
    }

    [TestMethod]
    public void Evaluate_UnknownInputEvidence_NoMissingPenalty()
    {
        var gate = new LearningDataQualityGate();
        // 输入不可确定（null）→ 缺失率不检查（不伪造完整性惩罚）。
        var snapshot = new DatasetSnapshotReport
        {
            SnapshotId = "snapshot-quality",
            SchemaVersion = "training-data-export/v1",
            CreatedAt = DateTimeOffset.UtcNow,
            WorkspaceId = "ws-quality",
            CollectionId = "col-quality",
            ModelArtifactId = "model-quality",
            InputEvidenceCount = null,
            MaterializedCount = 50,
            CompletenessRatio = null,
            MissingCount = null,
            MissingReasons = Array.Empty<string>(),
            ContentHash = "quality-hash",
            PolicyVersions = new[] { "policy/v1" },
            LineageDecisionCount = 1
        };

        var report = gate.Evaluate(snapshot, positiveCount: 30, negativeCount: 20);

        Assert.AreEqual(LearningDataQualityVerdict.Passed, report.Verdict, "输入不可确定时不因缺失惩罚。");
        Assert.IsNull(report.MissingRatio);
    }
}
