using System.Text.Json;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreFormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunTests
{
    private const string TestCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;
    private const string TestScope = "demo-workspace/demo-collection";
    private const string TestGrantId = "frp-grant-fixture-001";

    [TestMethod]
    public void LiveRuntimeActivationExecutionDryRun_AllUpstreamClean_Passes()
    {
        using var fixture = CreateFixture();
        var report = fixture.Runner.Run(fixture.IntegrityReport, fixture.WriteOutReport, true, true, false, false, new FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunOptions { IsGate = true });
        Assert.IsTrue(report.LiveRuntimeActivationExecutionDryRunPassed, string.Join(',', report.BlockedReasons));
        Assert.IsTrue(report.GatePassed);
        Assert.IsTrue(report.TotalCases >= 25);
        Assert.AreEqual(report.TotalCases, report.PassedCases);
        Assert.AreEqual(0, report.FailedCases);
        Assert.IsTrue(report.ProbeExecuted);
        Assert.IsFalse(report.RuntimeStateChanged);
        Assert.IsFalse(report.RuntimeActivation);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.IsTrue(report.NoRuntimeMutationInvariant);
    }

    [TestMethod]
    public void LiveRuntimeActivationExecutionDryRun_AllNegativeCases_BlockAsExpected()
    {
        using var fixture = CreateFixture();
        var report = fixture.Runner.Run(fixture.IntegrityReport, fixture.WriteOutReport, true, true, false, false, new FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunOptions { IsGate = true });
        foreach (var blockedCase in report.Cases.Where(static c => c.ExpectedStatus == LiveRuntimeActivationExecutionDryRunStatuses.Blocked))
        {
            Assert.AreEqual(LiveRuntimeActivationExecutionDryRunStatuses.Blocked, blockedCase.ActualStatus, blockedCase.CaseName);
            Assert.IsTrue(blockedCase.BlockedReasonMatched, blockedCase.CaseName);
            Assert.IsTrue(blockedCase.PassedAsExpected, blockedCase.CaseName);
        }
    }

    [TestMethod]
    public void LiveRuntimeActivationExecutionDryRun_SafetyInvariants_AllRemainFalse()
    {
        using var fixture = CreateFixture();
        var report = fixture.Runner.Run(fixture.IntegrityReport, fixture.WriteOutReport, true, true, false, false, new FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunOptions { IsGate = true });
        Assert.IsFalse(report.RuntimeActivation);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.FormalPackageWritten);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.VectorStoreBindingChanged);
        Assert.IsFalse(report.GlobalDefaultOn);
        Assert.IsFalse(report.PromotionToMainlinePerformed);
        Assert.IsFalse(report.MainlineEvidencePresent);
        Assert.IsFalse(report.MainlineTrustRegistryPresent);
        Assert.IsTrue(report.NoRuntimeMutationInvariant);
    }

    [TestMethod]
    public void LiveRuntimeActivationExecutionDryRun_UpstreamMissing_BlocksGate()
    {
        using var fixture = CreateFixture();
        var report = fixture.Runner.Run(null, fixture.WriteOutReport, true, true, false, false, new FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunOptions { IsGate = true });
        Assert.IsFalse(report.LiveRuntimeActivationExecutionDryRunPassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RealRuntimeActivationArtifactIntegrityGateArtifactMissing");
    }

    private static FixtureContext CreateFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-v823-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var guarded = FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutRunner.BuildCleanGuardedRuntimeActivationDryRunReport();
        var runtime = FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityRunner.BuildCleanRuntimeActivationDryRunReport();
        var configPath = Path.Combine(root, "runtime-config-patch.json");
        var rollbackPath = Path.Combine(root, "rollback-snapshot.json");
        var revocationPath = Path.Combine(root, "revocation-record.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(FormalRetrievalPromotionApprovalRuntimeActivationDryRunRunner.BuildCleanConfigPatch(TestGrantId, TestCapability, TestScope)));
        File.WriteAllText(rollbackPath, JsonSerializer.Serialize(FormalRetrievalPromotionApprovalRuntimeActivationDryRunRunner.BuildCleanRollback(TestGrantId, TestCapability, TestScope)));
        File.WriteAllText(revocationPath, JsonSerializer.Serialize(FormalRetrievalPromotionApprovalRuntimeActivationDryRunRunner.BuildCleanRevocation(TestGrantId, TestCapability, TestScope)));
        var decision = new GuardedRuntimeActivationArtifactWriteOutDecision
        {
            Status = GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactsWritten,
            BoundGrantId = TestGrantId,
            BoundCapability = TestCapability,
            BoundScope = TestScope,
            SourceGuardedRuntimeActivationDryRunOperationId = guarded.OperationId,
            PlannedGuardedActivationContract = new GuardedRuntimeActivationWriteContract
            {
                ReferencedConfigPatchSourcePath = configPath,
                ReferencedRollbackSnapshotPath = rollbackPath,
                ReferencedRevocationRecordPath = revocationPath
            },
            PlannedArtifactPaths =
            [
                Path.Combine(root, "runtime-switch.json"),
                Path.Combine(root, "activation-audit.jsonl"),
                Path.Combine(root, "runtime-guard-manifest.json"),
                Path.Combine(root, "scope-enforcement-manifest.json"),
                Path.Combine(root, "activation-rollback-binding.json")
            ]
        };
        var writeResult = FormalRetrievalPromotionApprovalRuntimeActivationArtifactWriter.WriteAll(decision, DateTimeOffset.Parse("2026-06-27T12:00:00Z"));
        var writeOut = new FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport
        {
            OperationId = "fixture-v823-writeout",
            CreatedAt = DateTimeOffset.Parse("2026-06-27T12:00:00Z"),
            GuardedRuntimeActivationArtifactWriteOutPassed = true,
            GatePassed = true,
            TotalCases = 27,
            PassedCases = 27,
            FailedCases = 0,
            WrittenCases = 1,
            BlockedCases = 26,
            BoundGrantId = TestGrantId,
            BoundCapability = TestCapability,
            BoundScope = TestScope,
            PlannedGuardedActivationContract = decision.PlannedGuardedActivationContract,
            UpstreamGuardedRuntimeActivationDryRunGatePresent = true,
            UpstreamGuardedRuntimeActivationDryRunGatePassed = true,
            WrittenArtifactPaths = writeResult.WrittenPaths,
            RuntimeActivationArtifactsWritten = true,
            RuntimeActivation = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ConfigPatchAppliedToRuntime = false,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            VectorStoreBindingChanged = false,
            GlobalDefaultOn = false,
            ArtifactOnly = true,
            CapabilityGrantWritten = true,
            ConfigPatchWritten = true,
            RollbackSnapshotWritten = true,
            AuditLogWritten = true,
            RevocationRecordWritten = true,
            NoRuntimeMutationInvariant = true
        };
        var integrity = new FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityRunner().Run(writeOut, guarded, runtime, true, true, false, false, new FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityOptions { IsGate = true });
        return new FixtureContext(root, new FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunRunner(), writeOut, integrity);
    }

    private sealed record FixtureContext(string Root, FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunRunner Runner, FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport WriteOutReport, FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityReport IntegrityReport) : IDisposable
    {
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
