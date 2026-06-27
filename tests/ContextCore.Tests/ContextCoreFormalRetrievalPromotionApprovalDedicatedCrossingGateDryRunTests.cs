using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreFormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunTests
{
    private const string TestCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;
    private const string TestScope = "demo-workspace/demo-collection";

    [TestMethod]
    public void CrossingDryRun_AllUpstreamClean_MatrixPasses()
    {
        var preCrossing = FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunRunner.BuildCleanPreCrossingReport(TestCapability, TestScope, gatePassed: true);
        var report = new FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunRunner().Run(
            preCrossing, rtPassed: true, p15Passed: true,
            mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            realConfigPatchPathAlreadyExists: false,
            new FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunOptions { IsGate = true });

        Assert.IsTrue(report.CrossingDryRunMatrixPassed, string.Join(",", report.BlockedReasons));
        Assert.IsTrue(report.GatePassed);
        Assert.IsTrue(report.TotalCases >= 15, $"TotalCases={report.TotalCases}");
        Assert.AreEqual(report.TotalCases, report.PassedCases);
        Assert.AreEqual(0, report.FailedCases);
        Assert.IsTrue(report.UpstreamPreCrossingGatePresent);
        Assert.IsTrue(report.UpstreamPreCrossingGatePassed);
        Assert.IsTrue(report.UpstreamPreCrossingFinalGatePassed);
    }

    [TestMethod]
    public void CrossingDryRun_StatusBranches_BothCovered()
    {
        var report = RunClean();
        Assert.IsTrue(report.ReadyCases >= 1);
        Assert.IsTrue(report.BlockedCases >= 1);
    }

    [TestMethod]
    public void CrossingDryRun_PositiveCase_FivePlannedArtifactPaths()
    {
        var report = RunClean();
        var positive = report.Cases.Single(c => c.CaseName == "AllAlignedReady");
        Assert.AreEqual(CrossingDryRunStatuses.CrossingDryRunReady, positive.ActualStatus);
        Assert.AreEqual(5, positive.PlannedArtifacts.Count);
        Assert.IsTrue(positive.DryRunOnly);
        Assert.IsFalse(positive.CrossingExecutionAllowed);
        Assert.IsTrue(positive.NotCrossed);
        Assert.IsTrue(positive.ApplicationNotApplied);
        Assert.IsTrue(positive.RollbackNotActivated);
        Assert.AreEqual(TestCapability, positive.BoundCapability);
        Assert.AreEqual(TestScope, positive.BoundScope);

        // Verify each planned path is non-empty and follows naming convention.
        Assert.IsFalse(string.IsNullOrWhiteSpace(positive.Contract.PlannedCapabilityGrantPath));
        Assert.IsFalse(string.IsNullOrWhiteSpace(positive.Contract.PlannedRuntimeConfigPatchPath));
        Assert.IsFalse(string.IsNullOrWhiteSpace(positive.Contract.PlannedRollbackSnapshotPath));
        Assert.IsFalse(string.IsNullOrWhiteSpace(positive.Contract.PlannedAuditLogPath));
        Assert.IsFalse(string.IsNullOrWhiteSpace(positive.Contract.PlannedRevocationRecordPath));
    }

    [TestMethod]
    public void CrossingDryRun_PlannedPathsNeverWrittenToDisk()
    {
        var report = RunClean();
        var positive = report.Cases.Single(c => c.CaseName == "AllAlignedReady");

        // Verify the planned paths do NOT exist on disk — V8.17 must not write any artifact.
        foreach (var path in positive.PlannedArtifacts)
        {
            Assert.IsFalse(File.Exists(path), $"Planned artifact path '{path}' must not be written to disk by V8.17.");
        }
    }

    [TestMethod]
    public void CrossingDryRun_NegativeCases_AllSpecifiedReasonsFire()
    {
        var report = RunClean();

        AssertBlocked(report, "PreCrossingGateMissing", CrossingDryRunBlockedReasons.PreCrossingGateMissing);
        AssertBlocked(report, "PreCrossingGateNotPassed", CrossingDryRunBlockedReasons.PreCrossingGateNotPassed);
        AssertBlocked(report, "NoPreCrossingReadyCase", CrossingDryRunBlockedReasons.NoPreCrossingReadyCase);
        AssertBlocked(report, "CapabilityMismatch", CrossingDryRunBlockedReasons.CapabilityMismatch);
        AssertBlocked(report, "EmptyScope", CrossingDryRunBlockedReasons.EmptyScope);
        AssertBlocked(report, "GlobalScope", CrossingDryRunBlockedReasons.GlobalScopeForbidden);
        AssertBlocked(report, "UpstreamCrossedTrue", CrossingDryRunBlockedReasons.UpstreamCrossedTrue);
        AssertBlocked(report, "UpstreamApplicationApplied", CrossingDryRunBlockedReasons.UpstreamApplicationApplied);
        AssertBlocked(report, "UpstreamRollbackActivated", CrossingDryRunBlockedReasons.UpstreamRollbackActivated);
        AssertBlocked(report, "RuntimeGateNotPassed", CrossingDryRunBlockedReasons.RuntimeChangeGateNotPassed);
        AssertBlocked(report, "P15GateNotPassed", CrossingDryRunBlockedReasons.P15GateNotPassed);
        AssertBlocked(report, "MainlineEvidencePresent", CrossingDryRunBlockedReasons.MainlineEvidencePresent);
        AssertBlocked(report, "MainlineTrustRegistryPresent", CrossingDryRunBlockedReasons.MainlineTrustRegistryPresent);
        AssertBlocked(report, "ConfigPatchPathWouldOverwrite", CrossingDryRunBlockedReasons.PlannedConfigPatchPathWouldOverwrite);
        AssertBlocked(report, "RollbackSnapshotPathMissing", CrossingDryRunBlockedReasons.PlannedRollbackSnapshotPathMissing);
    }

    [TestMethod]
    public void CrossingDryRun_QuintupleInvariant_AcrossEveryCase()
    {
        var report = RunClean();

        // 5 重不变量：每个 case DryRunOnly=true + CrossingExecutionAllowed=false + Crossed=false + ApplicationApplied=false + RollbackActivated=false。
        foreach (var c in report.Cases)
        {
            Assert.IsTrue(c.DryRunOnly, $"{c.CaseName}: DryRunOnly must be true");
            Assert.IsFalse(c.CrossingExecutionAllowed, $"{c.CaseName}: CrossingExecutionAllowed must be false");
            Assert.IsTrue(c.NotCrossed, $"{c.CaseName}: NotCrossed must be true");
            Assert.IsTrue(c.ApplicationNotApplied, $"{c.CaseName}: ApplicationNotApplied must be true");
            Assert.IsTrue(c.RollbackNotActivated, $"{c.CaseName}: RollbackNotActivated must be true");
        }

        Assert.IsTrue(report.ReadyCases >= 1);
        Assert.IsTrue(report.DryRunOnly);
        Assert.IsFalse(report.CrossingExecutionAllowed);
        Assert.IsFalse(report.Crossed);
        Assert.IsFalse(report.ApplicationApplied);
        Assert.IsFalse(report.RollbackActivated);
        Assert.IsFalse(report.CapabilityGrantWritten);
        Assert.IsFalse(report.GrantApplied);
        Assert.IsFalse(report.ConfigPatchWritten);
        Assert.IsFalse(report.RuntimeActivation);
    }

    [TestMethod]
    public void CrossingDryRun_NoManualReviewContract_AllFalse()
    {
        var report = RunClean();

        Assert.IsFalse(report.ManualReviewRequired);
        Assert.IsFalse(report.ApprovalSealed);
        Assert.IsFalse(report.CapabilityGrantWritten);
        Assert.IsFalse(report.GrantApplied);
        Assert.IsFalse(report.ApplicationApplied);
        Assert.IsFalse(report.RollbackActivated);
        Assert.IsFalse(report.Crossed);
        Assert.IsFalse(report.PromotionToMainlinePerformed);
        Assert.IsFalse(report.EvidenceCopiedToMainline);
        Assert.IsFalse(report.TrustRegistryCopiedToMainline);
        Assert.IsFalse(report.MainlineEvidencePresent);
        Assert.IsFalse(report.MainlineTrustRegistryPresent);
    }

    [TestMethod]
    public void CrossingDryRun_SafetyInvariants_AllFalse()
    {
        var report = RunClean();

        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.FormalPackageWritten);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.VectorStoreBindingChanged);
        Assert.IsFalse(report.GlobalDefaultOn);
        Assert.IsFalse(report.ConfigPatchWritten);
        Assert.IsFalse(report.RuntimeActivation);
        Assert.IsTrue(report.NoRuntimeMutationInvariant);
    }

    [TestMethod]
    public void CrossingDryRun_RealUpstreamMissing_BlocksMatrix()
    {
        var report = new FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunRunner().Run(
            loadedPreCrossingReport: null,
            rtPassed: true, p15Passed: true,
            mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            realConfigPatchPathAlreadyExists: false,
            new FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunOptions { IsGate = true });

        Assert.IsFalse(report.CrossingDryRunMatrixPassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RealPreCrossingGateArtifactMissing");
    }

    [TestMethod]
    public void CrossingDryRun_RealConfigPatchPathAlreadyExists_BlocksMatrix()
    {
        var preCrossing = FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunRunner.BuildCleanPreCrossingReport(TestCapability, TestScope, gatePassed: true);
        var report = new FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunRunner().Run(
            preCrossing, rtPassed: true, p15Passed: true,
            mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            realConfigPatchPathAlreadyExists: true,
            new FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunOptions { IsGate = true });

        Assert.IsFalse(report.CrossingDryRunMatrixPassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RealConfigPatchPathWouldOverwrite");
    }

    private static FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunReport RunClean()
    {
        var preCrossing = FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunRunner.BuildCleanPreCrossingReport(TestCapability, TestScope, gatePassed: true);
        return new FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunRunner().Run(
            preCrossing, rtPassed: true, p15Passed: true,
            mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            realConfigPatchPathAlreadyExists: false,
            new FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunOptions { IsGate = true });
    }

    private static void AssertBlocked(
        FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunReport report,
        string caseName,
        string expectedReason)
    {
        var c = report.Cases.Single(x => x.CaseName == caseName);
        Assert.AreEqual(CrossingDryRunStatuses.CrossingDryRunBlocked, c.ActualStatus, caseName);
        CollectionAssert.Contains(c.ActualBlockedReasons.ToList(), expectedReason, caseName);
        Assert.IsTrue(c.BlockedReasonMatched, caseName);
        Assert.IsTrue(c.PassedAsExpected, caseName);
        Assert.IsTrue(c.DryRunOnly, caseName);
        Assert.IsFalse(c.CrossingExecutionAllowed, caseName);
    }
}
