using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreFormalRetrievalPromotionApprovalOperatorSignOffMatrixTests
{
    [TestMethod]
    public void OperatorSignOffMatrix_AllScenarios_PassAsExpected()
    {
        var report = new FormalRetrievalPromotionApprovalOperatorSignOffMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalOperatorSignOffMatrixOptions { IsGate = true });

        Assert.IsTrue(report.OperatorSignOffMatrixPassed, string.Join(",", report.BlockedReasons));
        Assert.IsTrue(report.GatePassed);
        Assert.IsTrue(report.TotalCases >= 9, $"TotalCases={report.TotalCases}");
        Assert.AreEqual(report.TotalCases, report.PassedCases);
        Assert.AreEqual(0, report.FailedCases);
    }

    [TestMethod]
    public void OperatorSignOffMatrix_StatusBranches_AllCovered()
    {
        var report = new FormalRetrievalPromotionApprovalOperatorSignOffMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalOperatorSignOffMatrixOptions { IsGate = true });

        Assert.IsTrue(report.NotApplicableCases >= 1);
        Assert.IsTrue(report.InsufficientCases >= 1);
        Assert.IsTrue(report.RecordedCases >= 1);
    }

    [TestMethod]
    public void OperatorSignOffMatrix_EachCredentialElement_IsolatedlyDetected()
    {
        var report = new FormalRetrievalPromotionApprovalOperatorSignOffMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalOperatorSignOffMatrixOptions { IsGate = true });

        var isolatedMissing = report.Cases
            .Where(c => c.ActualCredentialElementsMissing.Count == 1)
            .Select(c => c.ActualCredentialElementsMissing[0])
            .ToHashSet(StringComparer.Ordinal);

        foreach (var e in OperatorSignOffElements.AllInOrder)
        {
            Assert.IsTrue(isolatedMissing.Contains(e), $"Sign-off element '{e}' must have an isolated negative case.");
        }
    }

    [TestMethod]
    public void OperatorSignOffMatrix_NoCrossover_AcrossEveryCaseIncludingRecorded()
    {
        var report = new FormalRetrievalPromotionApprovalOperatorSignOffMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalOperatorSignOffMatrixOptions { IsGate = true });

        // 三重不变量：每个 case Crossed=false AND ApplicationApplied=false AND RollbackActivated=false。
        foreach (var c in report.Cases)
        {
            Assert.IsTrue(c.NotCrossed, $"{c.CaseName}: NotCrossed must be true. status={c.ActualStatus}");
            Assert.IsTrue(c.ApplicationNotApplied, $"{c.CaseName}: ApplicationNotApplied must be true");
            Assert.IsTrue(c.RollbackNotActivated, $"{c.CaseName}: RollbackNotActivated must be true");
        }

        // matrix 级三重不变量。Recorded 至少 1 个，但 Crossed 仍 false。
        Assert.IsTrue(report.RecordedCases >= 1);
        Assert.IsFalse(report.Crossed);
        Assert.IsFalse(report.ApplicationApplied);
        Assert.IsFalse(report.RollbackActivated);
        Assert.IsFalse(report.GrantApplied);
        Assert.IsFalse(report.CapabilityGrantWritten);
    }

    [TestMethod]
    public void OperatorSignOffMatrix_NotApplicableSources_BothCovered()
    {
        var report = new FormalRetrievalPromotionApprovalOperatorSignOffMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalOperatorSignOffMatrixOptions { IsGate = true });

        // 至少一个 NotApplicable 来自 application 未就绪，一个来自 rollback 未就绪 — 验证双门约束。
        var fromAppNotReady = report.Cases.Single(c => c.CaseName == "NotApplicableFromApplicationNotReady");
        Assert.AreEqual(OperatorSignOffStatuses.OperatorSignOffNotApplicable, fromAppNotReady.ActualStatus);
        Assert.AreNotEqual(GrantApplicationStatuses.GrantApplicationReady, fromAppNotReady.InputApplicationStatus);

        var fromRollbackNotReady = report.Cases.Single(c => c.CaseName == "NotApplicableFromRollbackNotReady");
        Assert.AreEqual(OperatorSignOffStatuses.OperatorSignOffNotApplicable, fromRollbackNotReady.ActualStatus);
        Assert.AreEqual(GrantApplicationStatuses.GrantApplicationReady, fromRollbackNotReady.InputApplicationStatus);
        Assert.AreNotEqual(RollbackReadinessStatuses.RollbackReady, fromRollbackNotReady.InputRollbackStatus);
    }

    [TestMethod]
    public void OperatorSignOffMatrix_RecordedCase_AllFiveElementsPresent()
    {
        var report = new FormalRetrievalPromotionApprovalOperatorSignOffMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalOperatorSignOffMatrixOptions { IsGate = true });

        var recorded = report.Cases.Single(c => c.CaseName == "RecordedButNoCrossover");
        Assert.AreEqual(OperatorSignOffStatuses.OperatorSignOffRecorded, recorded.ActualStatus);
        Assert.AreEqual(OperatorSignOffElements.AllInOrder.Count, recorded.ActualCredentialElementsMet.Count);
        Assert.AreEqual(0, recorded.ActualCredentialElementsMissing.Count);
        Assert.IsTrue(recorded.NotCrossed);
        Assert.IsTrue(recorded.ApplicationNotApplied);
        Assert.IsTrue(recorded.RollbackNotActivated);
    }

    [TestMethod]
    public void OperatorSignOffMatrix_NoManualReviewContract_AllFalse()
    {
        var report = new FormalRetrievalPromotionApprovalOperatorSignOffMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalOperatorSignOffMatrixOptions { IsGate = true });

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
    public void OperatorSignOffMatrix_SafetyInvariants_AllFalse()
    {
        var report = new FormalRetrievalPromotionApprovalOperatorSignOffMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalOperatorSignOffMatrixOptions { IsGate = true });

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
    public void OperatorSignOffMatrix_MainlineEvidencePresent_BlocksMatrix()
    {
        var report = new FormalRetrievalPromotionApprovalOperatorSignOffMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: true, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalOperatorSignOffMatrixOptions { IsGate = true });

        Assert.IsFalse(report.OperatorSignOffMatrixPassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "MainlineEvidencePresent");
    }
}
