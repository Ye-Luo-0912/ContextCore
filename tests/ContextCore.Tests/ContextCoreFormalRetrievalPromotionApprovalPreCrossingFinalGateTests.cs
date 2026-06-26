using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreFormalRetrievalPromotionApprovalPreCrossingFinalGateTests
{
    private const string TestCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;
    private const string TestScope = "demo-workspace/demo-collection";

    [TestMethod]
    public void PreCrossingFinalGate_AllUpstreamsPassed_MatrixPasses()
    {
        var report = new FormalRetrievalPromotionApprovalPreCrossingFinalGateRunner().Run(
            FormalRetrievalPromotionApprovalPreCrossingFinalGateRunner.BuildCleanGrantApplicationReport(TestCapability, TestScope, gatePassed: true),
            FormalRetrievalPromotionApprovalPreCrossingFinalGateRunner.BuildCleanRollbackReadinessReport(TestCapability, TestScope, gatePassed: true),
            FormalRetrievalPromotionApprovalPreCrossingFinalGateRunner.BuildCleanOperatorSignOffReport(TestCapability, TestScope, gatePassed: true),
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalPreCrossingFinalGateOptions { IsGate = true });

        Assert.IsTrue(report.PreCrossingFinalGatePassed, string.Join(",", report.BlockedReasons));
        Assert.IsTrue(report.GatePassed);
        Assert.IsTrue(report.TotalCases >= 10, $"TotalCases={report.TotalCases}");
        Assert.AreEqual(report.TotalCases, report.PassedCases);
        Assert.AreEqual(0, report.FailedCases);
        Assert.IsTrue(report.UpstreamGrantApplicationGatePassed);
        Assert.IsTrue(report.UpstreamRollbackReadinessGatePassed);
        Assert.IsTrue(report.UpstreamOperatorSignOffGatePassed);
        Assert.AreEqual(TestCapability, report.BoundCapability);
        Assert.AreEqual(TestScope, report.BoundScope);
        Assert.IsTrue(report.CapabilityScopeAligned);
    }

    [TestMethod]
    public void PreCrossingFinalGate_StatusBranches_BothCovered()
    {
        var report = RunCleanReal();
        Assert.IsTrue(report.ReadyCases >= 1);
        Assert.IsTrue(report.BlockedCases >= 1);
    }

    [TestMethod]
    public void PreCrossingFinalGate_PositiveCase_PreCrossingReadyWithBoundCapabilityScope()
    {
        var report = RunCleanReal();
        var positive = report.Cases.Single(c => c.CaseName == "AllAlignedReady");
        Assert.AreEqual(PreCrossingStatuses.PreCrossingReady, positive.ActualStatus);
        Assert.IsTrue(positive.GrantApplicationReady);
        Assert.IsTrue(positive.RollbackReady);
        Assert.IsTrue(positive.OperatorSignOffRecorded);
        Assert.IsTrue(positive.CapabilityScopeAligned);
        Assert.AreEqual(TestCapability, positive.BoundCapability);
        Assert.AreEqual(TestScope, positive.BoundScope);
        Assert.IsTrue(positive.NotCrossed);
        Assert.IsTrue(positive.ApplicationNotApplied);
        Assert.IsTrue(positive.RollbackNotActivated);
    }

    [TestMethod]
    public void PreCrossingFinalGate_NegativeCases_AllSpecifiedReasonsFire()
    {
        var report = RunCleanReal();

        AssertBlockedReason(report, "GrantApplicationGateMissing", PreCrossingBlockedReasons.GrantApplicationGateMissing);
        AssertBlockedReason(report, "RollbackReadinessGateMissing", PreCrossingBlockedReasons.RollbackReadinessGateMissing);
        AssertBlockedReason(report, "OperatorSignOffGateMissing", PreCrossingBlockedReasons.OperatorSignOffGateMissing);
        AssertBlockedReason(report, "GrantApplicationGateNotPassed", PreCrossingBlockedReasons.GrantApplicationGateNotPassed);
        AssertBlockedReason(report, "RollbackReadinessGateNotPassed", PreCrossingBlockedReasons.RollbackReadinessGateNotPassed);
        AssertBlockedReason(report, "OperatorSignOffGateNotPassed", PreCrossingBlockedReasons.OperatorSignOffGateNotPassed);
        AssertBlockedReason(report, "GrantApplicationNoReadyCase", PreCrossingBlockedReasons.GrantApplicationNoReadyCase);
        AssertBlockedReason(report, "RollbackReadinessNoReadyCase", PreCrossingBlockedReasons.RollbackReadinessNoReadyCase);
        AssertBlockedReason(report, "OperatorSignOffNoRecordedCase", PreCrossingBlockedReasons.OperatorSignOffNoRecordedCase);
        AssertBlockedReason(report, "CapabilityMismatchAcrossGates", PreCrossingBlockedReasons.CapabilityMismatchAcrossUpstreamGates);
        AssertBlockedReason(report, "ScopeMismatchAcrossGates", PreCrossingBlockedReasons.ScopeMismatchAcrossUpstreamGates);
        AssertBlockedReason(report, "RuntimeGateNotPassed", PreCrossingBlockedReasons.RuntimeChangeGateNotPassed);
        AssertBlockedReason(report, "P15GateNotPassed", PreCrossingBlockedReasons.P15GateNotPassed);
        AssertBlockedReason(report, "MainlineEvidencePresentBlocks", PreCrossingBlockedReasons.MainlineEvidencePresent);
    }

    [TestMethod]
    public void PreCrossingFinalGate_NoCrossing_AcrossEveryCaseIncludingReady()
    {
        var report = RunCleanReal();

        // 四重不变量：每个 case 都满足 Crossed=false + ApplicationApplied=false + RollbackActivated=false。
        foreach (var c in report.Cases)
        {
            Assert.IsTrue(c.NotCrossed, $"{c.CaseName}: NotCrossed must be true");
            Assert.IsTrue(c.ApplicationNotApplied, $"{c.CaseName}: ApplicationNotApplied must be true");
            Assert.IsTrue(c.RollbackNotActivated, $"{c.CaseName}: RollbackNotActivated must be true");
        }

        Assert.IsTrue(report.ReadyCases >= 1);
        Assert.IsFalse(report.Crossed);
        Assert.IsFalse(report.ApplicationApplied);
        Assert.IsFalse(report.RollbackActivated);
        Assert.IsFalse(report.CapabilityGrantWritten);
        Assert.IsFalse(report.GrantApplied);
    }

    [TestMethod]
    public void PreCrossingFinalGate_NoManualReviewContract_AllFalse()
    {
        var report = RunCleanReal();

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
    public void PreCrossingFinalGate_SafetyInvariants_AllFalse()
    {
        var report = RunCleanReal();

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
    public void PreCrossingFinalGate_RealGrantApplicationArtifactMissing_BlocksMatrix()
    {
        var report = new FormalRetrievalPromotionApprovalPreCrossingFinalGateRunner().Run(
            loadedGrantApplicationReport: null,
            FormalRetrievalPromotionApprovalPreCrossingFinalGateRunner.BuildCleanRollbackReadinessReport(TestCapability, TestScope, gatePassed: true),
            FormalRetrievalPromotionApprovalPreCrossingFinalGateRunner.BuildCleanOperatorSignOffReport(TestCapability, TestScope, gatePassed: true),
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalPreCrossingFinalGateOptions { IsGate = true });

        Assert.IsFalse(report.PreCrossingFinalGatePassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RealGrantApplicationGateArtifactMissing");
    }

    [TestMethod]
    public void PreCrossingFinalGate_RealMainlineEvidencePresent_BlocksMatrix()
    {
        var report = new FormalRetrievalPromotionApprovalPreCrossingFinalGateRunner().Run(
            FormalRetrievalPromotionApprovalPreCrossingFinalGateRunner.BuildCleanGrantApplicationReport(TestCapability, TestScope, gatePassed: true),
            FormalRetrievalPromotionApprovalPreCrossingFinalGateRunner.BuildCleanRollbackReadinessReport(TestCapability, TestScope, gatePassed: true),
            FormalRetrievalPromotionApprovalPreCrossingFinalGateRunner.BuildCleanOperatorSignOffReport(TestCapability, TestScope, gatePassed: true),
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: true, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalPreCrossingFinalGateOptions { IsGate = true });

        Assert.IsFalse(report.PreCrossingFinalGatePassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "MainlineEvidencePresent");
    }

    private static FormalRetrievalPromotionApprovalPreCrossingFinalGateReport RunCleanReal() =>
        new FormalRetrievalPromotionApprovalPreCrossingFinalGateRunner().Run(
            FormalRetrievalPromotionApprovalPreCrossingFinalGateRunner.BuildCleanGrantApplicationReport(TestCapability, TestScope, gatePassed: true),
            FormalRetrievalPromotionApprovalPreCrossingFinalGateRunner.BuildCleanRollbackReadinessReport(TestCapability, TestScope, gatePassed: true),
            FormalRetrievalPromotionApprovalPreCrossingFinalGateRunner.BuildCleanOperatorSignOffReport(TestCapability, TestScope, gatePassed: true),
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalPreCrossingFinalGateOptions { IsGate = true });

    private static void AssertBlockedReason(
        FormalRetrievalPromotionApprovalPreCrossingFinalGateReport report,
        string caseName,
        string expectedReason)
    {
        var c = report.Cases.Single(x => x.CaseName == caseName);
        Assert.AreEqual(PreCrossingStatuses.PreCrossingBlocked, c.ActualStatus, caseName);
        CollectionAssert.Contains(c.ActualBlockedReasons.ToList(), expectedReason, caseName);
        Assert.IsTrue(c.BlockedReasonMatched, caseName);
        Assert.IsTrue(c.PassedAsExpected, caseName);
    }
}
