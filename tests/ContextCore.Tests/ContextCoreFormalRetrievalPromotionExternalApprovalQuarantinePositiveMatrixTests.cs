using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreFormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixTests
{
    [TestMethod]
    public void QuarantinePositiveMatrix_ValidCandidates_AllReachMachineValidatedCandidate()
    {
        var report = new FormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixRunner().Run(
            rtPassed: true,
            p15Passed: true,
            mainlineEvidencePresent: false,
            mainlineRegistryPresent: false,
            new FormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixOptions { IsGate = true });

        Assert.IsTrue(report.PositiveMatrixPassed, string.Join(",", report.BlockedReasons));
        Assert.IsTrue(report.GatePassed);
        Assert.IsTrue(report.TotalCases >= 4, $"TotalCases={report.TotalCases}");
        Assert.AreEqual(report.TotalCases, report.PassedCases);
        Assert.AreEqual(0, report.FailedCases);

        AssertCase(report, "ValidEvidenceCandidate",
            expectedEvidenceStatus: QuarantineCandidatePositiveStatuses.MachineValidatedCandidate,
            expectedRegistryStatus: QuarantineCandidatePositiveStatuses.Missing,
            evidenceShouldBeValid: true,
            registryShouldBeValid: false);

        AssertCase(report, "ValidTrustRegistryCandidate",
            expectedEvidenceStatus: QuarantineCandidatePositiveStatuses.Missing,
            expectedRegistryStatus: QuarantineCandidatePositiveStatuses.MachineValidatedCandidate,
            evidenceShouldBeValid: false,
            registryShouldBeValid: true);

        AssertCase(report, "ValidEvidenceAndRegistryPair",
            expectedEvidenceStatus: QuarantineCandidatePositiveStatuses.MachineValidatedCandidate,
            expectedRegistryStatus: QuarantineCandidatePositiveStatuses.MachineValidatedCandidate,
            evidenceShouldBeValid: true,
            registryShouldBeValid: true);

        AssertCase(report, "ValidMultiRecordTrustRegistry",
            expectedEvidenceStatus: QuarantineCandidatePositiveStatuses.Missing,
            expectedRegistryStatus: QuarantineCandidatePositiveStatuses.MachineValidatedCandidate,
            evidenceShouldBeValid: false,
            registryShouldBeValid: true);

        AssertCase(report, "ValidEvidenceWithMultiRecordRegistry",
            expectedEvidenceStatus: QuarantineCandidatePositiveStatuses.MachineValidatedCandidate,
            expectedRegistryStatus: QuarantineCandidatePositiveStatuses.MachineValidatedCandidate,
            evidenceShouldBeValid: true,
            registryShouldBeValid: true);

        AssertCase(report, "ValidRegistryWithFarFutureValidUntil",
            expectedEvidenceStatus: QuarantineCandidatePositiveStatuses.Missing,
            expectedRegistryStatus: QuarantineCandidatePositiveStatuses.MachineValidatedCandidate,
            evidenceShouldBeValid: false,
            registryShouldBeValid: true);
    }

    [TestMethod]
    public void QuarantinePositiveMatrix_NoManualReviewContract_AllNonApprovalFlagsFalse()
    {
        var report = new FormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixRunner().Run(
            rtPassed: true,
            p15Passed: true,
            mainlineEvidencePresent: false,
            mainlineRegistryPresent: false,
            new FormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixOptions { IsGate = true });

        Assert.IsFalse(report.ManualReviewRequired, "MachineValidatedCandidate must not imply manual review.");
        Assert.IsFalse(report.ApprovalSealed);
        Assert.IsFalse(report.CapabilityGrantWritten);
        Assert.IsFalse(report.PromotionToMainlinePerformed);
        Assert.IsFalse(report.EvidenceCopiedToMainline);
        Assert.IsFalse(report.TrustRegistryCopiedToMainline);
        Assert.IsFalse(report.MainlineEvidencePresent);
        Assert.IsFalse(report.MainlineTrustRegistryPresent);
    }

    [TestMethod]
    public void QuarantinePositiveMatrix_SafetyInvariants_AllFalse()
    {
        var report = new FormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixRunner().Run(
            rtPassed: true,
            p15Passed: true,
            mainlineEvidencePresent: false,
            mainlineRegistryPresent: false,
            new FormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixOptions { IsGate = true });

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
    public void QuarantinePositiveMatrix_PerCase_NotApprovedNotSealedNotPromoted()
    {
        var report = new FormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixRunner().Run(
            rtPassed: true,
            p15Passed: true,
            mainlineEvidencePresent: false,
            mainlineRegistryPresent: false,
            new FormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixOptions { IsGate = true });

        foreach (var c in report.Cases)
        {
            Assert.IsTrue(c.NotApproved, c.CaseName);
            Assert.IsTrue(c.NotSealed, c.CaseName);
            Assert.IsTrue(c.NotPromoted, c.CaseName);
            Assert.AreEqual(0, c.MissingFields.Count, $"{c.CaseName}: {string.Join(",", c.MissingFields)}");
            Assert.AreEqual(0, c.InvalidFields.Count, $"{c.CaseName}: {string.Join(",", c.InvalidFields)}");
        }
    }

    [TestMethod]
    public void QuarantinePositiveMatrix_MainlineEvidencePresent_BlocksMatrix()
    {
        var report = new FormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixRunner().Run(
            rtPassed: true,
            p15Passed: true,
            mainlineEvidencePresent: true,
            mainlineRegistryPresent: false,
            new FormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixOptions { IsGate = true });

        Assert.IsFalse(report.PositiveMatrixPassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "MainlineEvidencePresent");
    }

    [TestMethod]
    public void QuarantinePositiveMatrix_MainlineRegistryPresent_BlocksMatrix()
    {
        var report = new FormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixRunner().Run(
            rtPassed: true,
            p15Passed: true,
            mainlineEvidencePresent: false,
            mainlineRegistryPresent: true,
            new FormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixOptions { IsGate = true });

        Assert.IsFalse(report.PositiveMatrixPassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "MainlineTrustRegistryPresent");
    }

    [TestMethod]
    public void QuarantinePositiveMatrix_RuntimeGateNotPassed_BlocksMatrix()
    {
        var report = new FormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixRunner().Run(
            rtPassed: false,
            p15Passed: true,
            mainlineEvidencePresent: false,
            mainlineRegistryPresent: false,
            new FormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixOptions { IsGate = true });

        Assert.IsFalse(report.PositiveMatrixPassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeChangeGateNotPassed");
    }

    private static void AssertCase(
        FormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixReport report,
        string caseName,
        string expectedEvidenceStatus,
        string expectedRegistryStatus,
        bool evidenceShouldBeValid,
        bool registryShouldBeValid)
    {
        var item = report.Cases.Single(c => c.CaseName == caseName);
        Assert.IsTrue(item.PassedAsExpected, caseName);
        Assert.AreEqual(expectedEvidenceStatus, item.ActualEvidenceStatus, caseName);
        Assert.AreEqual(expectedRegistryStatus, item.ActualRegistryStatus, caseName);
        Assert.IsTrue(item.EvidenceStatusMatched, caseName);
        Assert.IsTrue(item.RegistryStatusMatched, caseName);
        Assert.IsTrue(item.NoMissingFields, caseName);
        Assert.IsTrue(item.NoInvalidFields, caseName);

        if (evidenceShouldBeValid)
        {
            Assert.IsTrue(item.EvidenceCandidateValid, $"{caseName}: EvidenceCandidateValid");
            Assert.IsTrue(item.EvidenceSchemaValid, $"{caseName}: EvidenceSchemaValid");
        }

        if (registryShouldBeValid)
        {
            Assert.IsTrue(item.TrustRegistryCandidateValid, $"{caseName}: TrustRegistryCandidateValid");
            Assert.IsTrue(item.TrustRegistryCandidateSchemaValid, $"{caseName}: TrustRegistryCandidateSchemaValid");
        }
    }
}
