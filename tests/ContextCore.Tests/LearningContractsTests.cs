using ContextCore.Abstractions;

namespace ContextCore.Tests;

/// <summary>
/// P8 Learning Loop V1 契约测试：覆盖 Dataset / Model Registry / Canary / Rollback / BaselineComparison 所有公共类型。
/// 验证构造函数、字段约束、版本递增、with 表达式不可变性、null 防御。
/// </summary>
[TestClass]
[TestCategory("P8")]
[TestCategory("Learning")]
public sealed class LearningContractsTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    // ---------- FeatureSchemaVersion / DatasetVersion / ModelArtifactVersion ----------

    [TestMethod]
    public void FeatureSchemaVersion_Initial_Is_v1_0()
    {
        var v = FeatureSchemaVersion.Initial;
        Assert.AreEqual(1, v.Major);
        Assert.AreEqual(0, v.Minor);
        Assert.AreEqual("v1.0", v.ToString());
    }

    [TestMethod]
    public void FeatureSchemaVersion_BumpMinor_Increments_Minor_Only()
    {
        var v = FeatureSchemaVersion.Initial;
        var bumped = v.BumpMinor();
        Assert.AreEqual(1, bumped.Major);
        Assert.AreEqual(1, bumped.Minor);
        Assert.AreEqual(0, v.Minor, "原版本应保持不变（record 不可变）");
    }

    [TestMethod]
    public void FeatureSchemaVersion_BumpMajor_Resets_Minor()
    {
        var v = new FeatureSchemaVersion(2, 5);
        var bumped = v.BumpMajor();
        Assert.AreEqual(3, bumped.Major);
        Assert.AreEqual(0, bumped.Minor);
    }

    [TestMethod]
    public void DatasetVersion_BumpMinor_BumpMajor_Work_As_Expected()
    {
        var v = DatasetVersion.Initial;
        Assert.AreEqual("v1.0", v.ToString());
        Assert.AreEqual(1, v.BumpMinor().Minor);
        Assert.AreEqual(2, v.BumpMajor().Major);
    }

    [TestMethod]
    public void ModelArtifactVersion_BumpMinor_BumpMajor_Work_As_Expected()
    {
        var v = ModelArtifactVersion.Initial;
        Assert.AreEqual("v1.0", v.ToString());
        Assert.AreEqual(1, v.BumpMinor().Minor);
        Assert.AreEqual(2, v.BumpMajor().Major);
    }

    // ---------- DatasetManifest ----------

    [TestMethod]
    public void DatasetManifest_Construction_Required_Fields()
    {
        var m = new DatasetManifest(
            datasetId: "ds-1",
            name: "Test Dataset",
            sourceCorpusDescription: "telemetry:package-build-v2",
            itemCount: 1000,
            hashSha256: "abc123",
            featureSchemaVersion: FeatureSchemaVersion.Initial,
            provenance: DatasetProvenance.RuntimeEvidence,
            createdAt: FixedTime);

        Assert.AreEqual("ds-1", m.DatasetId);
        Assert.AreEqual(1000, m.ItemCount);
        Assert.AreEqual("abc123", m.HashSha256);
        Assert.AreEqual(DatasetProvenance.RuntimeEvidence, m.Provenance);
        Assert.AreEqual(DatasetSplitStrategy.Random, m.SplitStrategy, "默认 split 策略应为 Random");
        Assert.AreEqual(0.7, m.TrainRatio);
        Assert.AreEqual(0.15, m.ValidationRatio);
        Assert.AreEqual(0.15, m.TestRatio);
        Assert.AreEqual(DatasetReviewStatus.Unreviewed, m.ReviewStatus, "默认审核状态应为 Unreviewed");
        Assert.AreEqual(0, m.TrainItemIds.Count, "默认 TrainItemIds 应为空");
    }

    [TestMethod]
    public void DatasetManifest_With_Expression_Preserves_Identity()
    {
        var m = new DatasetManifest(
            "ds-1", "n", "src", 100, "h",
            FeatureSchemaVersion.Initial, DatasetProvenance.RuntimeEvidence, FixedTime);

        var approved = m with
        {
            ReviewStatus = DatasetReviewStatus.Approved,
            ReviewerId = "user-1",
            ReviewedAt = FixedTime
        };

        Assert.AreEqual(m.DatasetId, approved.DatasetId);
        Assert.AreEqual(m.HashSha256, approved.HashSha256);
        Assert.AreEqual(DatasetReviewStatus.Approved, approved.ReviewStatus);
        Assert.AreEqual("user-1", approved.ReviewerId);
        // 原对象保持不变
        Assert.AreEqual(DatasetReviewStatus.Unreviewed, m.ReviewStatus);
    }

    [TestMethod]
    public void DatasetManifest_NullOrWhitespace_DatasetId_Throws()
    {
        Assert.ThrowsException<ArgumentException>(() => new DatasetManifest(
            "", "n", "src", 0, "h",
            FeatureSchemaVersion.Initial, DatasetProvenance.RuntimeEvidence, FixedTime));
    }

    [TestMethod]
    public void DatasetManifest_NullOrWhitespace_Name_Throws()
    {
        Assert.ThrowsException<ArgumentException>(() => new DatasetManifest(
            "ds-1", "", "src", 0, "h",
            FeatureSchemaVersion.Initial, DatasetProvenance.RuntimeEvidence, FixedTime));
    }

    [TestMethod]
    public void DatasetManifest_Negative_ItemCount_Throws()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new DatasetManifest(
            "ds-1", "n", "src", -1, "h",
            FeatureSchemaVersion.Initial, DatasetProvenance.RuntimeEvidence, FixedTime));
    }

    // ---------- DatasetStatistics ----------

    [TestMethod]
    public void DatasetStatistics_Construction_And_Negative_Validation()
    {
        var s = new DatasetStatistics(1000, 700, 150, 150, 200, 100, 700);
        Assert.AreEqual(1000, s.TotalItems);
        Assert.AreEqual(200, s.PositiveLabels);
        Assert.AreEqual(100, s.NegativeLabels);
        Assert.AreEqual(700, s.UnlabeledItems);

        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new DatasetStatistics(-1, 0, 0, 0, 0, 0, 0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new DatasetStatistics(0, 0, 0, 0, -1, 0, 0));
    }

    // ---------- VersionedDataset ----------

    [TestMethod]
    public void VersionedDataset_Construction_Required_Fields()
    {
        var manifest = new DatasetManifest(
            "ds-1", "n", "src", 1000, "h",
            FeatureSchemaVersion.Initial, DatasetProvenance.RuntimeEvidence, FixedTime);
        var stats = new DatasetStatistics(1000, 700, 150, 150, 200, 100, 700);

        var vd = new VersionedDataset(manifest, DatasetVersion.Initial, stats, FixedTime);

        Assert.AreEqual(manifest, vd.Manifest);
        Assert.AreEqual(DatasetVersion.Initial, vd.Version);
        Assert.AreEqual(stats, vd.Statistics);
    }

    [TestMethod]
    public void VersionedDataset_NullManifest_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new VersionedDataset(
            null!, DatasetVersion.Initial,
            new DatasetStatistics(0, 0, 0, 0, 0, 0, 0), FixedTime));
    }

    // ---------- ModelCompatibilityContract ----------

    [TestMethod]
    public void ModelCompatibilityContract_Construction()
    {
        var c = new ModelCompatibilityContract(
            requiredFeatureSchemaVersion: new FeatureSchemaVersion(2, 0),
            compatibilityLevel: ModelCompatibilityLevel.Breaking,
            minRuntimeVersion: "decision-schema/2.0",
            maxRuntimeVersion: null,
            breakingChangeNotes: "Renamed feature column");

        Assert.AreEqual(2, c.RequiredFeatureSchemaVersion.Major);
        Assert.AreEqual(ModelCompatibilityLevel.Breaking, c.CompatibilityLevel);
        Assert.AreEqual("decision-schema/2.0", c.MinRuntimeVersion);
        Assert.IsNull(c.MaxRuntimeVersion);
        Assert.AreEqual("Renamed feature column", c.BreakingChangeNotes);
    }

    // ---------- ModelArtifact ----------

    [TestMethod]
    public void ModelArtifact_Construction_Required_Fields()
    {
        var a = new ModelArtifact(
            modelId: "model-router-1",
            version: ModelArtifactVersion.Initial,
            targetCapability: OptimizationTargetComponent.CostAwareRetrievalRouter,
            artifactUri: "file://models/router-v1.bin",
            createdAt: FixedTime);

        Assert.AreEqual("model-router-1", a.ModelId);
        Assert.AreEqual(ModelArtifactVersion.Initial, a.Version);
        Assert.AreEqual(OptimizationTargetComponent.CostAwareRetrievalRouter, a.TargetCapability);
        Assert.AreEqual(ModelArtifactStatus.Draft, a.Status, "默认状态应为 Draft");
        Assert.IsNull(a.CompatibilityContract);
    }

    [TestMethod]
    public void ModelArtifact_With_Expression_Preserves_Identity()
    {
        var a = new ModelArtifact(
            "m-1", ModelArtifactVersion.Initial,
            OptimizationTargetComponent.PackagePolicy,
            "file://m.bin", FixedTime);

        var staged = a with { Status = ModelArtifactStatus.Staged };

        Assert.AreEqual(a.ModelId, staged.ModelId);
        Assert.AreEqual(ModelArtifactStatus.Staged, staged.Status);
        Assert.AreEqual(ModelArtifactStatus.Draft, a.Status, "原对象应保持不变");
    }

    [TestMethod]
    public void ModelArtifact_NullOrWhitespace_ModelId_Throws()
    {
        Assert.ThrowsException<ArgumentException>(() => new ModelArtifact(
            "", ModelArtifactVersion.Initial,
            OptimizationTargetComponent.PackagePolicy,
            "file://m.bin", FixedTime));
    }

    // ---------- CanaryAssignment ----------

    [TestMethod]
    public void CanaryAssignment_Construction_With_Whitelist_Strategy()
    {
        var a = new CanaryAssignment(
            assignmentId: "ca-1",
            proposalId: "prop-1",
            runId: "run-1",
            strategy: CanaryAssignmentStrategy.Whitelist,
            assignedAt: FixedTime)
        {
            AffectedWorkspaceIds = new[] { "ws-1", "ws-2" },
            WhitelistHash = "sha256:abc"
        };

        Assert.AreEqual("ca-1", a.AssignmentId);
        Assert.AreEqual(CanaryAssignmentStrategy.Whitelist, a.Strategy);
        Assert.AreEqual(2, a.AffectedWorkspaceIds.Count);
        Assert.AreEqual(0.05, a.Percentage, "默认 percentage 应为 5%");
    }

    [TestMethod]
    public void CanaryAssignment_NullOrWhitespace_AssignmentId_Throws()
    {
        Assert.ThrowsException<ArgumentException>(() => new CanaryAssignment(
            "", "p", "r", CanaryAssignmentStrategy.Random, FixedTime));
    }

    // ---------- RollbackRecord ----------

    [TestMethod]
    public void RollbackRecord_Construction_With_Triggered_Condition()
    {
        var r = new RollbackRecord(
            recordId: "rr-1",
            runId: "run-1",
            proposalId: "prop-1",
            reason: RollbackReason.RollbackConditionTriggered,
            triggeredAt: FixedTime)
        {
            TriggeredConditionMetricName = "error_rate",
            TriggeredConditionThreshold = 0.05,
            TriggeredConditionValue = 0.08,
            TriggeredAtStage = OptimizationStage.ScopedCanary
        };

        Assert.AreEqual("rr-1", r.RecordId);
        Assert.AreEqual(RollbackReason.RollbackConditionTriggered, r.Reason);
        Assert.AreEqual("error_rate", r.TriggeredConditionMetricName);
        Assert.AreEqual(0.08, r.TriggeredConditionValue);
        Assert.AreEqual(OptimizationStage.ScopedCanary, r.TriggeredAtStage);
    }

    [TestMethod]
    public void RollbackRecord_NullOrWhitespace_RecordId_Throws()
    {
        Assert.ThrowsException<ArgumentException>(() => new RollbackRecord(
            "", "r", "p", RollbackReason.SystemError, FixedTime));
    }

    // ---------- BaselineComparison ----------

    [TestMethod]
    public void BaselineComparison_Construction()
    {
        var baseline = new Dictionary<string, double> { ["duration_ms"] = 2329.0 };
        var experiment = new Dictionary<string, double> { ["duration_ms"] = 2029.0 };

        var bc = new BaselineComparison(
            comparisonId: "bc-1",
            proposalId: "prop-1",
            baselineMetrics: baseline,
            experimentMetrics: experiment,
            comparedAt: FixedTime)
        {
            JudgeDecision = PromotionDecision.Promote,
            JudgeRationale = "All ExpectedGains matched"
        };

        Assert.AreEqual("bc-1", bc.ComparisonId);
        Assert.AreEqual(2329.0, bc.BaselineMetrics["duration_ms"]);
        Assert.AreEqual(2029.0, bc.ExperimentMetrics["duration_ms"]);
        Assert.AreEqual(PromotionDecision.Promote, bc.JudgeDecision);
    }

    [TestMethod]
    public void BaselineComparison_NullMetrics_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new BaselineComparison(
            "bc-1", "p", null!, new Dictionary<string, double>(), FixedTime));
    }

    // ---------- 枚举完整性 ----------

    [TestMethod]
    public void DatasetSplitStrategy_Has_4_Values()
    {
        Assert.AreEqual(4, Enum.GetValues<DatasetSplitStrategy>().Length);
    }

    [TestMethod]
    public void DatasetReviewStatus_Has_5_Values()
    {
        Assert.AreEqual(5, Enum.GetValues<DatasetReviewStatus>().Length);
    }

    [TestMethod]
    public void DatasetProvenance_Has_4_Values()
    {
        Assert.AreEqual(4, Enum.GetValues<DatasetProvenance>().Length);
    }

    [TestMethod]
    public void ModelArtifactStatus_Has_6_Values()
    {
        Assert.AreEqual(6, Enum.GetValues<ModelArtifactStatus>().Length);
    }

    [TestMethod]
    public void ModelCompatibilityLevel_Has_3_Values()
    {
        Assert.AreEqual(3, Enum.GetValues<ModelCompatibilityLevel>().Length);
    }

    [TestMethod]
    public void CanaryAssignmentStrategy_Has_4_Values()
    {
        Assert.AreEqual(4, Enum.GetValues<CanaryAssignmentStrategy>().Length);
    }

    [TestMethod]
    public void RollbackReason_Has_5_Values()
    {
        Assert.AreEqual(5, Enum.GetValues<RollbackReason>().Length);
    }
}
