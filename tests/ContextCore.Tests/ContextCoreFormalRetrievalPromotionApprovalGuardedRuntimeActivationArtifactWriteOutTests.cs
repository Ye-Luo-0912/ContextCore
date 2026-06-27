using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreFormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutTests
{
    private const string TestCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;
    private const string TestScope = "demo-workspace/demo-collection";
    private const string TestGrantId = "frp-grant-fixture-001";

    [TestMethod]
    public void GuardedRuntimeActivationArtifactWriteOut_AllUpstreamClean_Passes()
    {
        var report = RunCleanWithMockWriter();

        Assert.IsTrue(report.GuardedRuntimeActivationArtifactWriteOutPassed, string.Join(',', report.BlockedReasons));
        Assert.IsTrue(report.GatePassed);
        Assert.IsTrue(report.TotalCases >= 25, $"TotalCases={report.TotalCases}");
        Assert.AreEqual(report.TotalCases, report.PassedCases);
        Assert.AreEqual(0, report.FailedCases);
        Assert.IsTrue(report.WrittenCases >= 1);
        Assert.IsTrue(report.BlockedCases >= 1);
        Assert.AreEqual(TestGrantId, report.BoundGrantId);
        Assert.AreEqual(TestCapability, report.BoundCapability);
        Assert.AreEqual(TestScope, report.BoundScope);
        Assert.AreEqual(5, report.WrittenArtifactPaths.Count);
        Assert.IsTrue(report.RuntimeActivationArtifactsWritten);
    }

    [TestMethod]
    public void GuardedRuntimeActivationArtifactWriteOut_AllNegativeCases_BlockAsExpected()
    {
        var report = RunCleanWithMockWriter();

        foreach (var blockedCase in report.Cases.Where(static c => c.ExpectedStatus == GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked))
        {
            Assert.AreEqual(GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactWriteOutBlocked, blockedCase.ActualStatus, blockedCase.CaseName);
            Assert.IsTrue(blockedCase.BlockedReasonMatched, blockedCase.CaseName);
            Assert.IsTrue(blockedCase.PassedAsExpected, blockedCase.CaseName);
            Assert.IsFalse(blockedCase.RuntimeActivation, blockedCase.CaseName);
            Assert.IsFalse(blockedCase.FormalRetrievalAllowed, blockedCase.CaseName);
            Assert.IsFalse(blockedCase.RuntimeSwitchAllowed, blockedCase.CaseName);
            Assert.IsFalse(blockedCase.PackageOutputChanged, blockedCase.CaseName);
        }
    }

    [TestMethod]
    public void GuardedRuntimeActivationArtifactWriteOut_SafetyInvariants_AllRemainFalse()
    {
        var report = RunCleanWithMockWriter();

        Assert.IsTrue(report.RuntimeActivationArtifactsWritten);
        Assert.IsFalse(report.RuntimeActivation);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.ConfigPatchAppliedToRuntime);
        Assert.IsFalse(report.FormalPackageWritten);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.VectorStoreBindingChanged);
        Assert.IsFalse(report.GlobalDefaultOn);
        Assert.IsFalse(report.EvidenceCopiedToMainline);
        Assert.IsFalse(report.TrustRegistryCopiedToMainline);
        Assert.IsFalse(report.PromotionToMainlinePerformed);
        Assert.IsFalse(report.MainlineEvidencePresent);
        Assert.IsFalse(report.MainlineTrustRegistryPresent);
        Assert.IsTrue(report.NoRuntimeMutationInvariant);
    }

    [TestMethod]
    public void GuardedRuntimeActivationArtifactWriter_WritesExpectedPayloads()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "cc-v821-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var decision = new GuardedRuntimeActivationArtifactWriteOutDecision
            {
                Status = GuardedRuntimeActivationArtifactWriteOutStatuses.GuardedRuntimeActivationArtifactsWritten,
                BoundGrantId = TestGrantId,
                BoundCapability = TestCapability,
                BoundScope = TestScope,
                PlannedGuardedActivationContract = new GuardedRuntimeActivationWriteContract
                {
                    ReferencedRollbackSnapshotPath = "vector/v8/dedicated-crossing/rollback-snapshot-FormalRetrievalActivation-demo-workspace-demo-collection.json",
                    ReferencedRevocationRecordPath = "vector/v8/dedicated-crossing/revocation-record-FormalRetrievalActivation-demo-workspace-demo-collection.json",
                    ReferencedConfigPatchSourcePath = "vector/v8/dedicated-crossing/runtime-config-patch-FormalRetrievalActivation-demo-workspace-demo-collection.json"
                },
                PlannedArtifactPaths = new[]
                {
                    Path.Combine(tempRoot, "runtime-switch.json"),
                    Path.Combine(tempRoot, "activation-audit.jsonl"),
                    Path.Combine(tempRoot, "runtime-guard-manifest.json"),
                    Path.Combine(tempRoot, "scope-enforcement-manifest.json"),
                    Path.Combine(tempRoot, "activation-rollback-binding.json")
                }
            };

            var result = FormalRetrievalPromotionApprovalRuntimeActivationArtifactWriter.WriteAll(decision, DateTimeOffset.Parse("2026-06-27T12:00:00Z"));

            Assert.IsTrue(result.AllArtifactsWritten);
            Assert.AreEqual(5, result.WrittenPaths.Count);

            var runtimeSwitch = System.Text.Json.JsonSerializer.Deserialize<GuardedRuntimeActivationRuntimeSwitchArtifactContent>(File.ReadAllText(decision.PlannedArtifactPaths[0]));
            Assert.IsNotNull(runtimeSwitch);
            Assert.AreEqual(TestGrantId, runtimeSwitch.BoundGrantId);
            Assert.AreEqual(TestCapability, runtimeSwitch.Capability);
            Assert.AreEqual(TestScope, runtimeSwitch.Scope);
            Assert.AreEqual("GuardedArtifactOnly", runtimeSwitch.SwitchMode);
            Assert.IsFalse(runtimeSwitch.ApplyToRuntime);
            Assert.IsFalse(runtimeSwitch.RuntimeActivation);
            Assert.IsFalse(runtimeSwitch.FormalRetrievalAllowed);
            Assert.IsFalse(runtimeSwitch.RuntimeSwitchAllowed);

            var auditLine = File.ReadLines(decision.PlannedArtifactPaths[1]).First();
            var auditEvent = System.Text.Json.JsonSerializer.Deserialize<GuardedRuntimeActivationAuditArtifactEvent>(auditLine);
            Assert.IsNotNull(auditEvent);
            Assert.AreEqual("GuardedRuntimeActivationArtifactWriteOut", auditEvent.EventType);
            Assert.AreEqual(TestGrantId, auditEvent.BoundGrantId);
            Assert.IsTrue(auditEvent.RuntimeActivationArtifactsWritten);
            Assert.IsFalse(auditEvent.RuntimeActivation);
            Assert.IsFalse(auditEvent.FormalRetrievalAllowed);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public void GuardedRuntimeActivationArtifactWriteOut_UpstreamMissing_BlocksGate()
    {
        var report = new FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutRunner().Run(
            loadedGuardedDryRunReport: null,
            rtPassed: true,
            p15Passed: true,
            mainlineEvidencePresent: false,
            mainlineRegistryPresent: false,
            realPathExists: path => path.Contains("vector/v8/dedicated-crossing/", StringComparison.Ordinal),
            realWriter: _ => new FormalRetrievalPromotionApprovalRuntimeActivationArtifactWriter.WriteResult(), opt: new FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutOptions { IsGate = true });

        Assert.IsFalse(report.GuardedRuntimeActivationArtifactWriteOutPassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RealGuardedRuntimeActivationDryRunGateArtifactMissing");
    }

    private static FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport RunCleanWithMockWriter()
    {
        var upstream = FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutRunner.BuildCleanGuardedRuntimeActivationDryRunReport();
        return new FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutRunner().Run(
            upstream,
            rtPassed: true,
            p15Passed: true,
            mainlineEvidencePresent: false,
            mainlineRegistryPresent: false,
            realPathExists: path => path.Contains("vector/v8/dedicated-crossing/", StringComparison.Ordinal),
            realWriter: decision => new FormalRetrievalPromotionApprovalRuntimeActivationArtifactWriter.WriteResult
            {
                AllArtifactsWritten = true,
                WrittenPaths = decision.PlannedArtifactPaths.ToArray()
            }, opt: new FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutOptions { IsGate = true });
    }
}
