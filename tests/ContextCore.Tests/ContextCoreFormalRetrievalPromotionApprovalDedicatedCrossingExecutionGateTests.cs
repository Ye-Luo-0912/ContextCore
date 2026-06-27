using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreFormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateTests
{
    private const string TestCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;
    private const string TestScope = "demo-workspace/demo-collection";

    [TestMethod]
    public void CrossingExecution_AllUpstreamClean_GatePasses_WithFiveArtifactsRecorded()
    {
        var written = new List<string>();
        var dryRun = FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateRunner.BuildCleanDryRunReport(TestCapability, TestScope, gatePassed: true);
        var preCrossing = FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunRunner.BuildCleanPreCrossingReport(TestCapability, TestScope, gatePassed: true);
        var report = new FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateRunner().Run(
            dryRun, preCrossing,
            rtPassed: true, p15Passed: true,
            mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            realPathExists: _ => false,
            realWriter: decision => new FormalRetrievalPromotionApprovalDedicatedCrossingArtifactWriter.WriteResult
            {
                AllArtifactsWritten = true,
                WrittenPaths = decision.PlannedArtifactPaths
            },
            new FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateOptions { IsGate = true });

        Assert.IsTrue(report.DedicatedCrossingExecutionGatePassed, string.Join(",", report.BlockedReasons));
        Assert.IsTrue(report.GatePassed);
        Assert.IsTrue(report.TotalCases >= 15);
        Assert.AreEqual(report.TotalCases, report.PassedCases);
        Assert.AreEqual(0, report.FailedCases);
        Assert.IsTrue(report.Crossed);
        Assert.IsTrue(report.ArtifactOnly);
        Assert.IsTrue(report.CapabilityGrantWritten);
        Assert.IsTrue(report.ConfigPatchWritten);
        Assert.IsTrue(report.RollbackSnapshotWritten);
        Assert.IsTrue(report.AuditLogWritten);
        Assert.IsTrue(report.RevocationRecordWritten);
        Assert.AreEqual(5, report.WrittenArtifactPaths.Count);
    }

    [TestMethod]
    public void CrossingExecution_RuntimeAndFormalRetrievalNeverActivated()
    {
        var report = RunWithMockWriter(writerSucceeds: true);

        // V8.18 关键不变量 — 即便 Crossed=true，runtime / formal retrieval 都还没动。
        Assert.IsFalse(report.RuntimeActivation);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.FormalPackageWritten);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.VectorStoreBindingChanged);
        Assert.IsFalse(report.GlobalDefaultOn);
        Assert.IsFalse(report.ConfigPatchAppliedToRuntime);
        Assert.IsTrue(report.NoRuntimeMutationInvariant);

        // 每个 matrix case 也必须保持 RuntimeActivation=false / FormalRetrievalAllowed=false。
        foreach (var c in report.Cases)
        {
            Assert.IsFalse(c.RuntimeActivation, $"{c.CaseName}: RuntimeActivation must be false");
            Assert.IsFalse(c.FormalRetrievalAllowed, $"{c.CaseName}: FormalRetrievalAllowed must be false");
            Assert.IsTrue(c.ArtifactOnly, $"{c.CaseName}: ArtifactOnly must be true");
        }
    }

    [TestMethod]
    public void CrossingExecution_StatusBranches_BothCovered()
    {
        var report = RunWithMockWriter(writerSucceeds: true);
        Assert.IsTrue(report.ExecutedCases >= 1);
        Assert.IsTrue(report.BlockedCases >= 1);
    }

    [TestMethod]
    public void CrossingExecution_NegativeCases_AllSpecifiedReasonsFire()
    {
        var report = RunWithMockWriter(writerSucceeds: true);

        AssertBlocked(report, "DryRunGateMissing", CrossingExecutionBlockedReasons.DryRunGateMissing);
        AssertBlocked(report, "DryRunGateNotPassed", CrossingExecutionBlockedReasons.DryRunGateNotPassed);
        AssertBlocked(report, "NoCrossingDryRunReadyCase", CrossingExecutionBlockedReasons.NoCrossingDryRunReadyCase);
        AssertBlocked(report, "DryRunOnlyFalse", CrossingExecutionBlockedReasons.DryRunOnlyFalse);
        AssertBlocked(report, "CrossingExecutionAllowedTrueInDryRun", CrossingExecutionBlockedReasons.CrossingExecutionAllowedTrueInDryRun);
        AssertBlocked(report, "PlannedArtifactCountNotFive", CrossingExecutionBlockedReasons.PlannedArtifactCountNotFive);
        AssertBlocked(report, "PlannedArtifactOutsideAllowedDirectory", CrossingExecutionBlockedReasons.PlannedArtifactOutsideAllowedDirectory);
        AssertBlocked(report, "PlannedArtifactAlreadyExists", CrossingExecutionBlockedReasons.PlannedArtifactAlreadyExists);
        AssertBlocked(report, "GlobalScope", CrossingExecutionBlockedReasons.GlobalScopeForbidden);
        AssertBlocked(report, "CapabilityMismatch", CrossingExecutionBlockedReasons.CapabilityMismatch);
        AssertBlocked(report, "RuntimeGateNotPassed", CrossingExecutionBlockedReasons.RuntimeChangeGateNotPassed);
        AssertBlocked(report, "P15GateNotPassed", CrossingExecutionBlockedReasons.P15GateNotPassed);
        AssertBlocked(report, "MainlineEvidencePresent", CrossingExecutionBlockedReasons.MainlineEvidencePresent);
        AssertBlocked(report, "MainlineTrustRegistryPresent", CrossingExecutionBlockedReasons.MainlineTrustRegistryPresent);
        AssertBlocked(report, "WriteFailureSimulated", CrossingExecutionBlockedReasons.WriteFailureSimulated);
    }

    [TestMethod]
    public void CrossingExecution_PositiveCase_PlannedFiveArtifactsAllUnderDedicatedCrossingDirectory()
    {
        var report = RunWithMockWriter(writerSucceeds: true);
        var positive = report.Cases.Single(c => c.CaseName == "AllUpstreamClean");
        Assert.AreEqual(CrossingExecutionStatuses.DedicatedCrossingExecuted, positive.ActualStatus);
        Assert.AreEqual(5, positive.PlannedArtifactPaths.Count);
        Assert.IsTrue(positive.DecisionCrossed);
        foreach (var path in positive.PlannedArtifactPaths)
        {
            Assert.IsTrue(CrossingExecutionAllowedDirectory.IsUnder(path),
                $"Planned path '{path}' must be under {CrossingExecutionAllowedDirectory.Value}/");
        }
    }

    [TestMethod]
    public void CrossingExecution_NoMainlineFilesWritten()
    {
        var report = RunWithMockWriter(writerSucceeds: true);
        Assert.IsFalse(report.EvidenceCopiedToMainline);
        Assert.IsFalse(report.TrustRegistryCopiedToMainline);
        Assert.IsFalse(report.MainlineEvidencePresent);
        Assert.IsFalse(report.MainlineTrustRegistryPresent);
    }

    [TestMethod]
    public void CrossingExecution_NoManualReviewContract_AllFalse()
    {
        var report = RunWithMockWriter(writerSucceeds: true);
        Assert.IsFalse(report.ManualReviewRequired);
        Assert.IsFalse(report.ApprovalSealed);
        Assert.IsFalse(report.GrantApplied);
        Assert.IsFalse(report.ApplicationApplied);
        Assert.IsFalse(report.RollbackActivated);
        Assert.IsFalse(report.PromotionToMainlinePerformed);
    }

    [TestMethod]
    public void CrossingExecution_RealUpstreamMissing_BlocksGate()
    {
        var report = new FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateRunner().Run(
            loadedDryRunReport: null,
            loadedPreCrossingReport: null,
            rtPassed: true, p15Passed: true,
            mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            realPathExists: _ => false,
            realWriter: _ => new FormalRetrievalPromotionApprovalDedicatedCrossingArtifactWriter.WriteResult { AllArtifactsWritten = true },
            new FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateOptions { IsGate = true });

        Assert.IsFalse(report.DedicatedCrossingExecutionGatePassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RealDryRunGateArtifactMissing");
        // 没有 dry-run upstream → 不该 Crossed。
        Assert.IsFalse(report.Crossed);
        Assert.AreEqual(0, report.WrittenArtifactPaths.Count);
    }

    [TestMethod]
    public void CrossingExecution_RealArtifactsAlreadyExist_BlocksGate()
    {
        // 模拟磁盘上已存在 — 防覆盖契约。
        var dryRun = FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateRunner.BuildCleanDryRunReport(TestCapability, TestScope, gatePassed: true);
        var preCrossing = FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunRunner.BuildCleanPreCrossingReport(TestCapability, TestScope, gatePassed: true);
        var report = new FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateRunner().Run(
            dryRun, preCrossing,
            rtPassed: true, p15Passed: true,
            mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            realPathExists: _ => true, // ← 所有 path 都已存在
            realWriter: _ => new FormalRetrievalPromotionApprovalDedicatedCrossingArtifactWriter.WriteResult { AllArtifactsWritten = false },
            new FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateOptions { IsGate = true });

        Assert.IsFalse(report.DedicatedCrossingExecutionGatePassed);
        Assert.IsFalse(report.Crossed);
        Assert.IsFalse(report.CapabilityGrantWritten);
        CollectionAssert.Contains(report.BlockedReasons.ToList(),
            $"RealCrossingExecution:{CrossingExecutionBlockedReasons.PlannedArtifactAlreadyExists}");
    }

    private static FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateReport RunWithMockWriter(bool writerSucceeds)
    {
        var dryRun = FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateRunner.BuildCleanDryRunReport(TestCapability, TestScope, gatePassed: true);
        var preCrossing = FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunRunner.BuildCleanPreCrossingReport(TestCapability, TestScope, gatePassed: true);
        return new FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateRunner().Run(
            dryRun, preCrossing,
            rtPassed: true, p15Passed: true,
            mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            realPathExists: _ => false,
            realWriter: decision => new FormalRetrievalPromotionApprovalDedicatedCrossingArtifactWriter.WriteResult
            {
                AllArtifactsWritten = writerSucceeds,
                WrittenPaths = writerSucceeds ? decision.PlannedArtifactPaths : Array.Empty<string>()
            },
            new FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateOptions { IsGate = true });
    }

    private static void AssertBlocked(
        FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateReport report,
        string caseName,
        string expectedReason)
    {
        var c = report.Cases.Single(x => x.CaseName == caseName);
        Assert.AreEqual(CrossingExecutionStatuses.DedicatedCrossingExecutionBlocked, c.ActualStatus, caseName);
        CollectionAssert.Contains(c.ActualBlockedReasons.ToList(), expectedReason, caseName);
        Assert.IsTrue(c.BlockedReasonMatched, caseName);
        Assert.IsTrue(c.PassedAsExpected, caseName);
        Assert.IsFalse(c.RuntimeActivation, caseName);
        Assert.IsFalse(c.FormalRetrievalAllowed, caseName);
        Assert.IsTrue(c.ArtifactOnly, caseName);
    }
}
