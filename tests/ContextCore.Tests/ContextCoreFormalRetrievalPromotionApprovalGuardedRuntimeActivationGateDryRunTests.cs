using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreFormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunTests
{
    private const string TestCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;
    private const string TestScope = "demo-workspace/demo-collection";
    private const string TestGrantId = "frp-grant-fixture-001";

    [TestMethod]
    public void GuardedRuntimeActivationGateDryRun_AllUpstreamClean_Passes()
    {
        var report = RunClean();

        Assert.IsTrue(report.GuardedRuntimeActivationDryRunPassed, string.Join(',', report.BlockedReasons));
        Assert.IsTrue(report.GatePassed);
        Assert.IsTrue(report.TotalCases >= 30, $"TotalCases={report.TotalCases}");
        Assert.AreEqual(report.TotalCases, report.PassedCases);
        Assert.AreEqual(0, report.FailedCases);
        Assert.IsTrue(report.ReadyCases >= 1);
        Assert.IsTrue(report.BlockedCases >= 1);
        Assert.AreEqual(TestGrantId, report.BoundGrantId);
        Assert.AreEqual(TestCapability, report.BoundCapability);
        Assert.AreEqual(TestScope, report.BoundScope);
        Assert.IsTrue(report.UpstreamActivationDryRunGatePresent);
        Assert.IsTrue(report.UpstreamActivationDryRunGatePassed);
    }

    [TestMethod]
    public void GuardedRuntimeActivationGateDryRun_AllNegativeCases_BlockAsExpected()
    {
        var report = RunClean();

        foreach (var blockedCase in report.Cases.Where(static c => c.ExpectedStatus == GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked))
        {
            Assert.AreEqual(GuardedRuntimeActivationDryRunStatuses.GuardedRuntimeActivationDryRunBlocked, blockedCase.ActualStatus, blockedCase.CaseName);
            Assert.IsTrue(blockedCase.BlockedReasonMatched, blockedCase.CaseName);
            Assert.IsTrue(blockedCase.PassedAsExpected, blockedCase.CaseName);
            Assert.IsFalse(blockedCase.RuntimeActivationWriteAllowed, blockedCase.CaseName);
            Assert.IsFalse(blockedCase.RuntimeActivation, blockedCase.CaseName);
            Assert.IsFalse(blockedCase.FormalRetrievalAllowed, blockedCase.CaseName);
            Assert.IsFalse(blockedCase.RuntimeSwitchAllowed, blockedCase.CaseName);
        }
    }

    [TestMethod]
    public void GuardedRuntimeActivationGateDryRun_PlannedContract_PopulatedButNotWritten()
    {
        var report = RunClean();
        var contract = report.PlannedGuardedActivationContract;

        Assert.AreEqual(TestCapability, contract.PlannedCapability);
        Assert.AreEqual(TestScope, contract.PlannedScope);
        Assert.IsFalse(string.IsNullOrWhiteSpace(contract.PlannedRuntimeSwitchArtifactPath));
        Assert.IsFalse(string.IsNullOrWhiteSpace(contract.PlannedActivationAuditArtifactPath));
        Assert.IsFalse(string.IsNullOrWhiteSpace(contract.PlannedRuntimeGuardManifestPath));
        Assert.IsFalse(string.IsNullOrWhiteSpace(contract.PlannedScopeEnforcementManifestPath));
        Assert.IsFalse(string.IsNullOrWhiteSpace(contract.PlannedActivationRollbackBindingPath));
        StringAssert.StartsWith(contract.PlannedRuntimeSwitchArtifactPath, GuardedRuntimeActivationAllowedDirectory.Value);
        StringAssert.StartsWith(contract.PlannedActivationAuditArtifactPath, GuardedRuntimeActivationAllowedDirectory.Value);
        Assert.IsFalse(File.Exists(contract.PlannedRuntimeSwitchArtifactPath));
        Assert.IsFalse(File.Exists(contract.PlannedActivationAuditArtifactPath));
        Assert.IsFalse(File.Exists(contract.PlannedRuntimeGuardManifestPath));
        Assert.IsFalse(File.Exists(contract.PlannedScopeEnforcementManifestPath));
        Assert.IsFalse(File.Exists(contract.PlannedActivationRollbackBindingPath));
    }

    [TestMethod]
    public void GuardedRuntimeActivationGateDryRun_SafetyInvariants_AllRemainFalse()
    {
        var report = RunClean();

        Assert.IsFalse(report.RuntimeActivationWriteAllowed);
        Assert.IsFalse(report.RuntimeActivation);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.ConfigPatchAppliedToRuntime);
        Assert.IsFalse(report.FormalPackageWritten);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.VectorStoreBindingChanged);
        Assert.IsFalse(report.GlobalDefaultOn);
        Assert.IsFalse(report.MainlineEvidencePresent);
        Assert.IsFalse(report.MainlineTrustRegistryPresent);
        Assert.IsFalse(report.PromotionToMainlinePerformed);
        Assert.IsTrue(report.NoRuntimeMutationInvariant);
        Assert.IsTrue(report.Crossed);
        Assert.IsTrue(report.ArtifactOnly);
        Assert.IsTrue(report.CapabilityGrantWritten);
        Assert.IsTrue(report.ConfigPatchWritten);
        Assert.IsTrue(report.RollbackSnapshotWritten);
        Assert.IsTrue(report.AuditLogWritten);
        Assert.IsTrue(report.RevocationRecordWritten);
    }

    [TestMethod]
    public void GuardedRuntimeActivationGateDryRun_UpstreamMissing_BlocksGate()
    {
        var report = new FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunRunner().Run(
            loadedActivationDryRunReport: null,
            rtPassed: true,
            p15Passed: true,
            mainlineEvidencePresent: false,
            mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunOptions { IsGate = true });

        Assert.IsFalse(report.GuardedRuntimeActivationDryRunPassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RealActivationDryRunGateArtifactMissing");
    }

    private static FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunReport RunClean()
    {
        var upstream = FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunRunner.BuildCleanActivationDryRunReport();
        return new FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunRunner().Run(
            upstream,
            rtPassed: true,
            p15Passed: true,
            mainlineEvidencePresent: false,
            mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunOptions { IsGate = true });
    }
}
