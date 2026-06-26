using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreFormalRetrievalPromotionApprovalGrantApplicationMatrixTests
{
    [TestMethod]
    public void GrantApplicationMatrix_AllScenarios_PassAsExpected()
    {
        var report = new FormalRetrievalPromotionApprovalGrantApplicationMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalGrantApplicationMatrixOptions { IsGate = true });

        Assert.IsTrue(report.GrantApplicationMatrixPassed, string.Join(",", report.BlockedReasons));
        Assert.IsTrue(report.GatePassed);
        Assert.IsTrue(report.TotalCases >= 7, $"TotalCases={report.TotalCases}");
        Assert.AreEqual(report.TotalCases, report.PassedCases);
        Assert.AreEqual(0, report.FailedCases);
    }

    [TestMethod]
    public void GrantApplicationMatrix_StatusBranches_AllCovered()
    {
        var report = new FormalRetrievalPromotionApprovalGrantApplicationMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalGrantApplicationMatrixOptions { IsGate = true });

        Assert.IsTrue(report.NotApplicableCases >= 1);
        Assert.IsTrue(report.BlockedCases >= 1);
        Assert.IsTrue(report.ReadyCases >= 1);
    }

    [TestMethod]
    public void GrantApplicationMatrix_PreconditionIsolation_EachIndividuallyDetected()
    {
        var report = new FormalRetrievalPromotionApprovalGrantApplicationMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalGrantApplicationMatrixOptions { IsGate = true });

        // 每个 precondition 都应该有一个 case 只缺它（PreconditionsMissing.Count == 1）。
        var isolatedMissing = report.Cases
            .Where(c => c.ActualPreconditionsMissing.Count == 1)
            .Select(c => c.ActualPreconditionsMissing[0])
            .ToHashSet(StringComparer.Ordinal);

        foreach (var p in GrantApplicationPreconditions.AllInOrder)
        {
            Assert.IsTrue(isolatedMissing.Contains(p), $"Precondition '{p}' must have an isolated negative case.");
        }
    }

    [TestMethod]
    public void GrantApplicationMatrix_ApplicationNeverApplied_AcrossEveryCaseIncludingReady()
    {
        var report = new FormalRetrievalPromotionApprovalGrantApplicationMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalGrantApplicationMatrixOptions { IsGate = true });

        foreach (var c in report.Cases)
        {
            Assert.IsTrue(c.ApplicationNotApplied, $"{c.CaseName}: ApplicationNotApplied must be true. status={c.ActualStatus}");
        }

        // matrix 级 invariant — 即便 ReadyCases >= 1，ApplicationApplied 仍 false。
        Assert.IsTrue(report.ReadyCases >= 1, "Need at least one Ready case to test the Ready != Applied invariant.");
        Assert.IsFalse(report.ApplicationApplied);
        Assert.IsFalse(report.GrantApplied);
        Assert.IsFalse(report.CapabilityGrantWritten);
    }

    [TestMethod]
    public void GrantApplicationMatrix_DenyInput_NotApplicableEvenWithAllPreconditionsMet()
    {
        var report = new FormalRetrievalPromotionApprovalGrantApplicationMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalGrantApplicationMatrixOptions { IsGate = true });

        var deny = report.Cases.Single(c => c.CaseName == "DenyInputNotApplicable");
        Assert.AreEqual(GrantApplicationStatuses.GrantApplicationNotApplicable, deny.ActualStatus);
        Assert.AreEqual(0, deny.ActualPreconditionsMet.Count, "Deny inputs must not evaluate preconditions.");
        Assert.AreEqual(0, deny.ActualPreconditionsMissing.Count);
    }

    [TestMethod]
    public void GrantApplicationMatrix_IndeterminateInput_NotApplicableEvenWithAllPreconditionsMet()
    {
        var report = new FormalRetrievalPromotionApprovalGrantApplicationMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalGrantApplicationMatrixOptions { IsGate = true });

        var indeterminate = report.Cases.Single(c => c.CaseName == "IndeterminateInputNotApplicable");
        Assert.AreEqual(GrantApplicationStatuses.GrantApplicationNotApplicable, indeterminate.ActualStatus);
        Assert.AreEqual(0, indeterminate.ActualPreconditionsMet.Count);
        Assert.AreEqual(0, indeterminate.ActualPreconditionsMissing.Count);
    }

    [TestMethod]
    public void GrantApplicationMatrix_ReadyCase_AllFivePreconditionsPresent()
    {
        var report = new FormalRetrievalPromotionApprovalGrantApplicationMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalGrantApplicationMatrixOptions { IsGate = true });

        var ready = report.Cases.Single(c => c.CaseName == "GrantAllPreconditionsMet_Ready_ButNotApplied");
        Assert.AreEqual(GrantApplicationStatuses.GrantApplicationReady, ready.ActualStatus);
        Assert.AreEqual(GrantApplicationPreconditions.AllInOrder.Count, ready.ActualPreconditionsMet.Count);
        Assert.AreEqual(0, ready.ActualPreconditionsMissing.Count);
        Assert.IsTrue(ready.ApplicationNotApplied);
    }

    [TestMethod]
    public void GrantApplicationMatrix_NoManualReviewContract_AllFalse()
    {
        var report = new FormalRetrievalPromotionApprovalGrantApplicationMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalGrantApplicationMatrixOptions { IsGate = true });

        Assert.IsFalse(report.ManualReviewRequired);
        Assert.IsFalse(report.ApprovalSealed);
        Assert.IsFalse(report.CapabilityGrantWritten);
        Assert.IsFalse(report.GrantApplied);
        Assert.IsFalse(report.ApplicationApplied);
        Assert.IsFalse(report.PromotionToMainlinePerformed);
        Assert.IsFalse(report.EvidenceCopiedToMainline);
        Assert.IsFalse(report.TrustRegistryCopiedToMainline);
        Assert.IsFalse(report.MainlineEvidencePresent);
        Assert.IsFalse(report.MainlineTrustRegistryPresent);
    }

    [TestMethod]
    public void GrantApplicationMatrix_SafetyInvariants_AllFalse()
    {
        var report = new FormalRetrievalPromotionApprovalGrantApplicationMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalGrantApplicationMatrixOptions { IsGate = true });

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
    public void GrantApplicationMatrix_MainlineEvidencePresent_BlocksMatrix()
    {
        var report = new FormalRetrievalPromotionApprovalGrantApplicationMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: true, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalGrantApplicationMatrixOptions { IsGate = true });

        Assert.IsFalse(report.GrantApplicationMatrixPassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "MainlineEvidencePresent");
    }
}
