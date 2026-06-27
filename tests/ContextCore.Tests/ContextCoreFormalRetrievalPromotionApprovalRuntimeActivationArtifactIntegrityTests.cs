using System.Text.Json;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreFormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityTests
{
    private const string TestCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;
    private const string TestScope = "demo-workspace/demo-collection";
    private const string TestGrantId = "frp-grant-fixture-001";

    [TestMethod]
    public void RuntimeActivationArtifactIntegrity_AllUpstreamClean_Passes()
    {
        using var fixture = CreateFixture();
        var report = fixture.Runner.Run(fixture.WriteOutReport, fixture.GuardedReport, fixture.RuntimeReport, true, true, false, false, new FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityOptions { IsGate = true });
        Assert.IsTrue(report.RuntimeActivationArtifactIntegrityPassed, string.Join(',', report.BlockedReasons));
        Assert.IsTrue(report.GatePassed);
        Assert.IsTrue(report.TotalCases >= 30);
        Assert.AreEqual(report.TotalCases, report.PassedCases);
        Assert.AreEqual(0, report.FailedCases);
        Assert.AreEqual(5, report.ContentVerifiedArtifactCount);
        Assert.IsTrue(report.AllRuntimeActivationArtifactsContentVerified);
        Assert.IsTrue(report.LiveActivationDryRunContractComplete);
    }

    [TestMethod]
    public void RuntimeActivationArtifactIntegrity_AllNegativeCases_BlockAsExpected()
    {
        using var fixture = CreateFixture();
        var report = fixture.Runner.Run(fixture.WriteOutReport, fixture.GuardedReport, fixture.RuntimeReport, true, true, false, false, new FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityOptions { IsGate = true });
        foreach (var blockedCase in report.Cases.Where(static c => c.ExpectedStatus == RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked))
        {
            Assert.AreEqual(RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, blockedCase.ActualStatus, blockedCase.CaseName);
            Assert.IsTrue(blockedCase.BlockedReasonMatched, blockedCase.CaseName);
            Assert.IsTrue(blockedCase.PassedAsExpected, blockedCase.CaseName);
        }
    }

    [TestMethod]
    public void RuntimeActivationArtifactIntegrity_SafetyInvariants_AllRemainFalse()
    {
        using var fixture = CreateFixture();
        var report = fixture.Runner.Run(fixture.WriteOutReport, fixture.GuardedReport, fixture.RuntimeReport, true, true, false, false, new FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityOptions { IsGate = true });
        Assert.IsFalse(report.RuntimeActivation);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.ConfigPatchAppliedToRuntime);
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
    public void RuntimeActivationArtifactIntegrity_UpstreamMissing_BlocksGate()
    {
        var guarded = FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutRunner.BuildCleanGuardedRuntimeActivationDryRunReport();
        var runtime = FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityRunner.BuildCleanRuntimeActivationDryRunReport();
        var report = new FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityRunner().Run(null, guarded, runtime, true, true, false, false, new FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityOptions { IsGate = true });
        Assert.IsFalse(report.RuntimeActivationArtifactIntegrityPassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RealGuardedRuntimeActivationArtifactWriteOutGateArtifactMissing");
    }

    private static FixtureContext CreateFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-v822-" + Guid.NewGuid().ToString("N"));
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
            PlannedArtifactPaths = [Path.Combine(root, "runtime-switch.json"), Path.Combine(root, "activation-audit.jsonl"), Path.Combine(root, "runtime-guard-manifest.json"), Path.Combine(root, "scope-enforcement-manifest.json"), Path.Combine(root, "activation-rollback-binding.json")]
        };
        var writeResult = FormalRetrievalPromotionApprovalRuntimeActivationArtifactWriter.WriteAll(decision, DateTimeOffset.Parse("2026-06-27T12:00:00Z"));
        var writeOut = new FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport
        {
            OperationId = "fixture-v822",
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
            NoRuntimeMutationInvariant = true
        };
        return new FixtureContext(root, new FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityRunner(), writeOut, guarded, runtime);
    }

    private sealed record FixtureContext(string Root, FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityRunner Runner, FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport WriteOutReport, FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunReport GuardedReport, FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport RuntimeReport) : IDisposable
    {
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
