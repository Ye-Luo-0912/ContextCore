using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreFormalRetrievalPromotionApprovalRollbackReadinessMatrixTests
{
    [TestMethod]
    public void RollbackReadinessMatrix_AllScenarios_PassAsExpected()
    {
        var report = new FormalRetrievalPromotionApprovalRollbackReadinessMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalRollbackReadinessMatrixOptions { IsGate = true });

        Assert.IsTrue(report.RollbackReadinessMatrixPassed, string.Join(",", report.BlockedReasons));
        Assert.IsTrue(report.GatePassed);
        Assert.IsTrue(report.TotalCases >= 7, $"TotalCases={report.TotalCases}");
        Assert.AreEqual(report.TotalCases, report.PassedCases);
        Assert.AreEqual(0, report.FailedCases);
    }

    [TestMethod]
    public void RollbackReadinessMatrix_StatusBranches_AllCovered()
    {
        var report = new FormalRetrievalPromotionApprovalRollbackReadinessMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalRollbackReadinessMatrixOptions { IsGate = true });

        Assert.IsTrue(report.NotApplicableCases >= 1);
        Assert.IsTrue(report.IncompleteCases >= 1);
        Assert.IsTrue(report.ReadyCases >= 1);
    }

    [TestMethod]
    public void RollbackReadinessMatrix_EachRollbackElement_IsolatedlyDetected()
    {
        var report = new FormalRetrievalPromotionApprovalRollbackReadinessMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalRollbackReadinessMatrixOptions { IsGate = true });

        var isolatedMissing = report.Cases
            .Where(c => c.ActualRollbackElementsMissing.Count == 1)
            .Select(c => c.ActualRollbackElementsMissing[0])
            .ToHashSet(StringComparer.Ordinal);

        foreach (var e in RollbackElements.AllInOrder)
        {
            Assert.IsTrue(isolatedMissing.Contains(e), $"Rollback element '{e}' must have an isolated negative case.");
        }
    }

    [TestMethod]
    public void RollbackReadinessMatrix_NothingActivated_AcrossEveryCaseIncludingReady()
    {
        var report = new FormalRetrievalPromotionApprovalRollbackReadinessMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalRollbackReadinessMatrixOptions { IsGate = true });

        foreach (var c in report.Cases)
        {
            Assert.IsTrue(c.RollbackNotActivated, $"{c.CaseName}: RollbackNotActivated must be true. status={c.ActualStatus}");
            Assert.IsTrue(c.ApplicationNotApplied, $"{c.CaseName}: ApplicationNotApplied must be true. status={c.ActualStatus}");
        }

        // matrix 级 — Ready 至少有一个，但仍未触发回滚或应用。
        Assert.IsTrue(report.ReadyCases >= 1);
        Assert.IsFalse(report.RollbackActivated);
        Assert.IsFalse(report.ApplicationApplied);
        Assert.IsFalse(report.GrantApplied);
        Assert.IsFalse(report.CapabilityGrantWritten);
    }

    [TestMethod]
    public void RollbackReadinessMatrix_NonReadyApplication_AlwaysNotApplicable()
    {
        var report = new FormalRetrievalPromotionApprovalRollbackReadinessMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalRollbackReadinessMatrixOptions { IsGate = true });

        var fromBlocked = report.Cases.Single(c => c.CaseName == "NotApplicableFromBlockedApplication");
        var fromNa = report.Cases.Single(c => c.CaseName == "NotApplicableFromNotApplicableApplication");

        foreach (var c in new[] { fromBlocked, fromNa })
        {
            Assert.AreEqual(RollbackReadinessStatuses.RollbackReadinessNotApplicable, c.ActualStatus, c.CaseName);
            // 即便上游 Application 不是 Ready，且我们送进了完整的 rollback prep，仍应 NotApplicable，并不评估元素。
            Assert.AreEqual(0, c.ActualRollbackElementsMet.Count, c.CaseName);
            Assert.AreEqual(0, c.ActualRollbackElementsMissing.Count, c.CaseName);
        }
    }

    [TestMethod]
    public void RollbackReadinessMatrix_ReadyCase_AllFiveElementsPresent()
    {
        var report = new FormalRetrievalPromotionApprovalRollbackReadinessMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalRollbackReadinessMatrixOptions { IsGate = true });

        var ready = report.Cases.Single(c => c.CaseName == "RollbackReadyButNothingActivated");
        Assert.AreEqual(RollbackReadinessStatuses.RollbackReady, ready.ActualStatus);
        Assert.AreEqual(RollbackElements.AllInOrder.Count, ready.ActualRollbackElementsMet.Count);
        Assert.AreEqual(0, ready.ActualRollbackElementsMissing.Count);
        Assert.IsTrue(ready.RollbackNotActivated);
        Assert.IsTrue(ready.ApplicationNotApplied);
    }

    [TestMethod]
    public void RollbackReadinessMatrix_NoManualReviewContract_AllFalse()
    {
        var report = new FormalRetrievalPromotionApprovalRollbackReadinessMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalRollbackReadinessMatrixOptions { IsGate = true });

        Assert.IsFalse(report.ManualReviewRequired);
        Assert.IsFalse(report.ApprovalSealed);
        Assert.IsFalse(report.CapabilityGrantWritten);
        Assert.IsFalse(report.GrantApplied);
        Assert.IsFalse(report.ApplicationApplied);
        Assert.IsFalse(report.RollbackActivated);
        Assert.IsFalse(report.PromotionToMainlinePerformed);
        Assert.IsFalse(report.EvidenceCopiedToMainline);
        Assert.IsFalse(report.TrustRegistryCopiedToMainline);
        Assert.IsFalse(report.MainlineEvidencePresent);
        Assert.IsFalse(report.MainlineTrustRegistryPresent);
    }

    [TestMethod]
    public void RollbackReadinessMatrix_SafetyInvariants_AllFalse()
    {
        var report = new FormalRetrievalPromotionApprovalRollbackReadinessMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalRollbackReadinessMatrixOptions { IsGate = true });

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
    public void RollbackReadinessMatrix_MainlineEvidencePresent_BlocksMatrix()
    {
        var report = new FormalRetrievalPromotionApprovalRollbackReadinessMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: true, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalRollbackReadinessMatrixOptions { IsGate = true });

        Assert.IsFalse(report.RollbackReadinessMatrixPassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "MainlineEvidencePresent");
    }
}
