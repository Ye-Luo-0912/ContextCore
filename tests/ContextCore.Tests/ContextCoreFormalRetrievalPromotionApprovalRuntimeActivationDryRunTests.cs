using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreFormalRetrievalPromotionApprovalRuntimeActivationDryRunTests
{
    private const string TestCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;
    private const string TestScope = "demo-workspace/demo-collection";
    private const string TestGrantId = "frp-grant-fixture-001";

    [TestMethod]
    public void RuntimeActivationDryRun_AllUpstreamClean_DryRunPasses()
    {
        var report = RunClean();
        Assert.IsTrue(report.RuntimeActivationDryRunPassed, string.Join(",", report.BlockedReasons));
        Assert.IsTrue(report.GatePassed);
        Assert.IsTrue(report.TotalCases >= 20, $"TotalCases={report.TotalCases}");
        Assert.AreEqual(report.TotalCases, report.PassedCases);
        Assert.AreEqual(0, report.FailedCases);
        Assert.AreEqual(TestGrantId, report.BoundGrantId);
        Assert.AreEqual(TestCapability, report.BoundCapability);
        Assert.AreEqual(TestScope, report.BoundScope);
        Assert.IsTrue(report.UpstreamExecutionGatePresent);
        Assert.IsTrue(report.UpstreamExecutionGatePassed);
    }

    [TestMethod]
    public void RuntimeActivationDryRun_StatusBranches_BothCovered()
    {
        var report = RunClean();
        Assert.IsTrue(report.ReadyCases >= 1);
        Assert.IsTrue(report.BlockedCases >= 1);
    }

    [TestMethod]
    public void RuntimeActivationDryRun_PlannedContract_FullyPopulated()
    {
        var report = RunClean();
        Assert.AreEqual("GuardedScopeOnly", report.PlannedActivationContract.PlannedRuntimeActivationMode);
        Assert.AreEqual(TestCapability, report.PlannedActivationContract.PlannedCapability);
        Assert.AreEqual(TestScope, report.PlannedActivationContract.PlannedScope);
        Assert.IsFalse(string.IsNullOrWhiteSpace(report.PlannedActivationContract.PlannedConfigPatchSourcePath));
        Assert.IsFalse(string.IsNullOrWhiteSpace(report.PlannedActivationContract.PlannedRuntimeSwitchPath));
        Assert.IsFalse(string.IsNullOrWhiteSpace(report.PlannedActivationContract.PlannedActivationAuditPath));
        Assert.IsFalse(string.IsNullOrWhiteSpace(report.PlannedActivationContract.PlannedRollbackReference));
        Assert.IsFalse(string.IsNullOrWhiteSpace(report.PlannedActivationContract.PlannedRevocationReference));

        // Planned paths must NOT exist on disk — this matrix never writes them.
        Assert.IsFalse(File.Exists(report.PlannedActivationContract.PlannedRuntimeSwitchPath));
        Assert.IsFalse(File.Exists(report.PlannedActivationContract.PlannedActivationAuditPath));
    }

    [TestMethod]
    public void RuntimeActivationDryRun_NegativeCases_AllSpecifiedReasonsFire()
    {
        var report = RunClean();

        AssertBlocked(report, "CrossingExecutionGateMissing", RuntimeActivationDryRunBlockedReasons.CrossingExecutionGateMissing);
        AssertBlocked(report, "CrossingExecutionGateNotPassed", RuntimeActivationDryRunBlockedReasons.CrossingExecutionGateNotPassed);
        AssertBlocked(report, "CrossingNotTrueInUpstream", RuntimeActivationDryRunBlockedReasons.CrossingNotTrueInUpstream);
        AssertBlocked(report, "ArtifactOnlyFalseInUpstream", RuntimeActivationDryRunBlockedReasons.ArtifactOnlyFalseInUpstream);
        AssertBlocked(report, "CapabilityGrantWrittenFalseInUpstream", RuntimeActivationDryRunBlockedReasons.CapabilityGrantWrittenFalseInUpstream);
        AssertBlocked(report, "ConfigPatchWrittenFalseInUpstream", RuntimeActivationDryRunBlockedReasons.ConfigPatchWrittenFalseInUpstream);
        AssertBlocked(report, "RuntimeActivationTrueInUpstream", RuntimeActivationDryRunBlockedReasons.RuntimeActivationTrueInUpstream);
        AssertBlocked(report, "FormalRetrievalAllowedTrueInUpstream", RuntimeActivationDryRunBlockedReasons.FormalRetrievalAllowedTrueInUpstream);
        AssertBlocked(report, "RuntimeSwitchAllowedTrueInUpstream", RuntimeActivationDryRunBlockedReasons.RuntimeSwitchAllowedTrueInUpstream);
        AssertBlocked(report, "GrantArtifactMissing", RuntimeActivationDryRunBlockedReasons.GrantArtifactMissing);
        AssertBlocked(report, "ConfigPatchArtifactMissing", RuntimeActivationDryRunBlockedReasons.ConfigPatchArtifactMissing);
        AssertBlocked(report, "RollbackSnapshotArtifactMissing", RuntimeActivationDryRunBlockedReasons.RollbackSnapshotArtifactMissing);
        AssertBlocked(report, "AuditLogArtifactMissing", RuntimeActivationDryRunBlockedReasons.AuditLogArtifactMissing);
        AssertBlocked(report, "RevocationRecordArtifactMissing", RuntimeActivationDryRunBlockedReasons.RevocationRecordArtifactMissing);
        AssertBlocked(report, "GrantCapabilityMismatch", RuntimeActivationDryRunBlockedReasons.GrantCapabilityMismatch);
        AssertBlocked(report, "GrantScopeMismatch", RuntimeActivationDryRunBlockedReasons.GrantScopeMismatch);
        AssertBlocked(report, "ConfigPatchSourceGrantIdMismatch", RuntimeActivationDryRunBlockedReasons.ConfigPatchSourceGrantIdMismatch);
        AssertBlocked(report, "RollbackSourceGrantIdMismatch", RuntimeActivationDryRunBlockedReasons.RollbackSourceGrantIdMismatch);
        AssertBlocked(report, "AuditGrantIdMismatch", RuntimeActivationDryRunBlockedReasons.AuditGrantIdMismatch);
        AssertBlocked(report, "RevocationGrantIdMismatch", RuntimeActivationDryRunBlockedReasons.RevocationGrantIdMismatch);
        AssertBlocked(report, "RevocationAlreadyRevoked", RuntimeActivationDryRunBlockedReasons.RevocationAlreadyRevoked);
        AssertBlocked(report, "RuntimeGateNotPassed", RuntimeActivationDryRunBlockedReasons.RuntimeChangeGateNotPassed);
        AssertBlocked(report, "P15GateNotPassed", RuntimeActivationDryRunBlockedReasons.P15GateNotPassed);
        AssertBlocked(report, "MainlineEvidencePresent", RuntimeActivationDryRunBlockedReasons.MainlineEvidencePresent);
        AssertBlocked(report, "MainlineTrustRegistryPresent", RuntimeActivationDryRunBlockedReasons.MainlineTrustRegistryPresent);
    }

    [TestMethod]
    public void RuntimeActivationDryRun_SixTierInvariant_AcrossEveryCase()
    {
        var report = RunClean();

        // 6 重 invariant：每个 case 都必须 ActivationDryRunOnly=true + 5 个 runtime flag=false。
        foreach (var c in report.Cases)
        {
            Assert.IsTrue(c.ActivationDryRunOnly, $"{c.CaseName}: ActivationDryRunOnly must be true");
            Assert.IsFalse(c.RuntimeActivationAllowed, $"{c.CaseName}: RuntimeActivationAllowed must be false");
            Assert.IsFalse(c.RuntimeActivation, $"{c.CaseName}: RuntimeActivation must be false");
            Assert.IsFalse(c.FormalRetrievalAllowed, $"{c.CaseName}: FormalRetrievalAllowed must be false");
            Assert.IsFalse(c.RuntimeSwitchAllowed, $"{c.CaseName}: RuntimeSwitchAllowed must be false");
            Assert.IsFalse(c.ConfigPatchAppliedToRuntime, $"{c.CaseName}: ConfigPatchAppliedToRuntime must be false");
        }

        // matrix 级 invariants
        Assert.IsTrue(report.ActivationDryRunOnly);
        Assert.IsFalse(report.RuntimeActivationAllowed);
        Assert.IsFalse(report.RuntimeActivation);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.ConfigPatchAppliedToRuntime);
    }

    [TestMethod]
    public void RuntimeActivationDryRun_CarryV818State_PreservedInReport()
    {
        var report = RunClean();
        Assert.IsTrue(report.Crossed);
        Assert.IsTrue(report.ArtifactOnly);
        Assert.IsTrue(report.CapabilityGrantWritten);
        Assert.IsTrue(report.ConfigPatchWritten);
        Assert.IsTrue(report.RollbackSnapshotWritten);
        Assert.IsTrue(report.AuditLogWritten);
        Assert.IsTrue(report.RevocationRecordWritten);
    }

    [TestMethod]
    public void RuntimeActivationDryRun_NoMainlineFilesWritten()
    {
        var report = RunClean();
        Assert.IsFalse(report.EvidenceCopiedToMainline);
        Assert.IsFalse(report.TrustRegistryCopiedToMainline);
        Assert.IsFalse(report.MainlineEvidencePresent);
        Assert.IsFalse(report.MainlineTrustRegistryPresent);
    }

    [TestMethod]
    public void RuntimeActivationDryRun_SafetyInvariants_AllFalse()
    {
        var report = RunClean();
        Assert.IsFalse(report.FormalPackageWritten);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.VectorStoreBindingChanged);
        Assert.IsFalse(report.GlobalDefaultOn);
        Assert.IsTrue(report.NoRuntimeMutationInvariant);
    }

    [TestMethod]
    public void RuntimeActivationDryRun_RealExecutionGateMissing_BlocksGate()
    {
        var report = new FormalRetrievalPromotionApprovalRuntimeActivationDryRunRunner().Run(
            loadedExecutionReport: null,
            loadedGrant: null,
            loadedConfigPatch: null,
            loadedRollbackSnapshot: null,
            loadedAuditEvent: null,
            loadedRevocation: null,
            rtPassed: true, p15Passed: true,
            mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            configPatchSourcePath: null,
            rollbackSnapshotPath: null,
            revocationRecordPath: null,
            new FormalRetrievalPromotionApprovalRuntimeActivationDryRunOptions { IsGate = true });

        Assert.IsFalse(report.RuntimeActivationDryRunPassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RealCrossingExecutionGateArtifactMissing");
    }

    private static FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport RunClean()
    {
        var execution = FormalRetrievalPromotionApprovalRuntimeActivationDryRunRunner.BuildCleanExecutionReport();
        var grant = FormalRetrievalPromotionApprovalRuntimeActivationDryRunRunner.BuildCleanGrant(TestGrantId, TestCapability, TestScope);
        var configPatch = FormalRetrievalPromotionApprovalRuntimeActivationDryRunRunner.BuildCleanConfigPatch(TestGrantId, TestCapability, TestScope);
        var rollback = FormalRetrievalPromotionApprovalRuntimeActivationDryRunRunner.BuildCleanRollback(TestGrantId, TestCapability, TestScope);
        var audit = FormalRetrievalPromotionApprovalRuntimeActivationDryRunRunner.BuildCleanAudit(TestGrantId, TestCapability, TestScope);
        var revocation = FormalRetrievalPromotionApprovalRuntimeActivationDryRunRunner.BuildCleanRevocation(TestGrantId, TestCapability, TestScope);
        return new FormalRetrievalPromotionApprovalRuntimeActivationDryRunRunner().Run(
            execution, grant, configPatch, rollback, audit, revocation,
            rtPassed: true, p15Passed: true,
            mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            configPatchSourcePath: "vector/v8/dedicated-crossing/runtime-config-patch-fixture.json",
            rollbackSnapshotPath: "vector/v8/dedicated-crossing/rollback-snapshot-fixture.json",
            revocationRecordPath: "vector/v8/dedicated-crossing/revocation-record-fixture.json",
            new FormalRetrievalPromotionApprovalRuntimeActivationDryRunOptions { IsGate = true });
    }

    private static void AssertBlocked(
        FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport report,
        string caseName,
        string expectedReason)
    {
        var c = report.Cases.Single(x => x.CaseName == caseName);
        Assert.AreEqual(RuntimeActivationDryRunStatuses.RuntimeActivationDryRunBlocked, c.ActualStatus, caseName);
        CollectionAssert.Contains(c.ActualBlockedReasons.ToList(), expectedReason, caseName);
        Assert.IsTrue(c.BlockedReasonMatched, caseName);
        Assert.IsTrue(c.PassedAsExpected, caseName);
        Assert.IsTrue(c.ActivationDryRunOnly, caseName);
        Assert.IsFalse(c.RuntimeActivation, caseName);
        Assert.IsFalse(c.FormalRetrievalAllowed, caseName);
    }
}
