using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreFormalRetrievalPromotionApprovalPolicyAuthorityMatrixTests
{
    [TestMethod]
    public void PolicyAuthorityMatrix_AllScenarios_PassAsExpected()
    {
        var report = new FormalRetrievalPromotionApprovalPolicyAuthorityMatrixRunner().Run(
            rtPassed: true,
            p15Passed: true,
            mainlineEvidencePresent: false,
            mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalPolicyAuthorityMatrixOptions { IsGate = true });

        Assert.IsTrue(report.PolicyAuthorityMatrixPassed, string.Join(",", report.BlockedReasons));
        Assert.IsTrue(report.GatePassed);
        Assert.IsTrue(report.TotalCases >= 5, $"TotalCases={report.TotalCases}");
        Assert.AreEqual(report.TotalCases, report.PassedCases);
        Assert.AreEqual(0, report.FailedCases);
    }

    [TestMethod]
    public void PolicyAuthorityMatrix_RuleBranches_AllCovered()
    {
        var report = new FormalRetrievalPromotionApprovalPolicyAuthorityMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalPolicyAuthorityMatrixOptions { IsGate = true });

        AssertCase(report, "TrustChainBrokenInput",
            expectedEffect: PolicyAuthorityEffects.Deny,
            expectedStatus: PolicyAuthorityStatuses.PolicyAuthorityUnreachable,
            expectedRule: PolicyAuthorityRules.NoTrustChain,
            expectedResolved: false);

        AssertCase(report, "FixtureTrustModeBlocksGrant",
            expectedEffect: PolicyAuthorityEffects.Deny,
            expectedStatus: PolicyAuthorityStatuses.PolicyAuthorityResolved,
            expectedRule: PolicyAuthorityRules.FixtureTrustModeCannotAuthorizeProduction,
            expectedResolved: true);

        AssertCase(report, "ScopeOutOfAuthority",
            expectedEffect: PolicyAuthorityEffects.Deny,
            expectedStatus: PolicyAuthorityStatuses.PolicyAuthorityResolved,
            expectedRule: PolicyAuthorityRules.ScopeOutOfAuthority,
            expectedResolved: true);

        AssertCase(report, "CapabilityNotInPolicyAuthority",
            expectedEffect: PolicyAuthorityEffects.Indeterminate,
            expectedStatus: PolicyAuthorityStatuses.PolicyAuthorityResolved,
            expectedRule: PolicyAuthorityRules.CapabilityNotInPolicyAuthority,
            expectedResolved: true);

        AssertCase(report, "CleanProductionGrant",
            expectedEffect: PolicyAuthorityEffects.Grant,
            expectedStatus: PolicyAuthorityStatuses.PolicyAuthorityResolved,
            expectedRule: PolicyAuthorityRules.AuthorizedByPolicy,
            expectedResolved: true);
    }

    [TestMethod]
    public void PolicyAuthorityMatrix_GrantNeverApplied_AcrossEveryCase()
    {
        var report = new FormalRetrievalPromotionApprovalPolicyAuthorityMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalPolicyAuthorityMatrixOptions { IsGate = true });

        // 每一个 case，即便 effect=Grant，GrantNotApplied 必须 true。
        foreach (var c in report.Cases)
        {
            Assert.IsTrue(c.GrantNotApplied, $"{c.CaseName}: GrantNotApplied must be true. effect={c.ActualEffect}");
        }

        // matrix 级聚合不变量。
        Assert.IsFalse(report.GrantApplied);
        Assert.IsFalse(report.CapabilityGrantWritten);
    }

    [TestMethod]
    public void PolicyAuthorityMatrix_NoManualReviewContract_AllFalse()
    {
        var report = new FormalRetrievalPromotionApprovalPolicyAuthorityMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalPolicyAuthorityMatrixOptions { IsGate = true });

        Assert.IsFalse(report.ManualReviewRequired);
        Assert.IsFalse(report.ApprovalSealed);
        Assert.IsFalse(report.CapabilityGrantWritten);
        Assert.IsFalse(report.GrantApplied);
        Assert.IsFalse(report.PromotionToMainlinePerformed);
        Assert.IsFalse(report.EvidenceCopiedToMainline);
        Assert.IsFalse(report.TrustRegistryCopiedToMainline);
        Assert.IsFalse(report.MainlineEvidencePresent);
        Assert.IsFalse(report.MainlineTrustRegistryPresent);
    }

    [TestMethod]
    public void PolicyAuthorityMatrix_SafetyInvariants_AllFalse()
    {
        var report = new FormalRetrievalPromotionApprovalPolicyAuthorityMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalPolicyAuthorityMatrixOptions { IsGate = true });

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
    public void PolicyAuthorityMatrix_MainlineEvidencePresent_BlocksMatrix()
    {
        var report = new FormalRetrievalPromotionApprovalPolicyAuthorityMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: true, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalPolicyAuthorityMatrixOptions { IsGate = true });

        Assert.IsFalse(report.PolicyAuthorityMatrixPassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "MainlineEvidencePresent");
    }

    [TestMethod]
    public void PolicyAuthorityMatrix_RuntimeGateNotPassed_BlocksMatrix()
    {
        var report = new FormalRetrievalPromotionApprovalPolicyAuthorityMatrixRunner().Run(
            rtPassed: false, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalPolicyAuthorityMatrixOptions { IsGate = true });

        Assert.IsFalse(report.PolicyAuthorityMatrixPassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeChangeGateNotPassed");
    }

    private static void AssertCase(
        FormalRetrievalPromotionApprovalPolicyAuthorityMatrixReport report,
        string caseName,
        string expectedEffect,
        string expectedStatus,
        string expectedRule,
        bool expectedResolved)
    {
        var item = report.Cases.Single(c => c.CaseName == caseName);
        Assert.IsTrue(item.PassedAsExpected, $"{caseName}: PassedAsExpected");
        Assert.AreEqual(expectedEffect, item.ActualEffect, $"{caseName}: effect");
        Assert.AreEqual(expectedStatus, item.ActualStatus, $"{caseName}: status");
        Assert.AreEqual(expectedRule, item.ActualRuleName, $"{caseName}: rule");
        Assert.AreEqual(expectedResolved, item.ActualIsResolved, $"{caseName}: resolved");
        Assert.IsTrue(item.GrantNotApplied, $"{caseName}: GrantNotApplied");
    }
}
