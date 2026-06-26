using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreFormalRetrievalPromotionExternalApprovalQuarantineNegativeMatrixTests
{
    [TestMethod]
    public void QuarantineNegativeMatrix_UsesRealCandidateJsonAndMatchesExpectedFields()
    {
        var report = new FormalRetrievalPromotionExternalApprovalQuarantineNegativeMatrixRunner().Run(
            rtPassed: true,
            p15Passed: true,
            new FormalRetrievalPromotionExternalApprovalQuarantineMatrixOptions { IsGate = true });

        Assert.IsTrue(report.MatrixPassed, string.Join(",", report.BlockedReasons));
        Assert.IsTrue(report.GatePassed);
        Assert.AreEqual(8, report.TotalCases);
        Assert.AreEqual(8, report.PassedCases);
        Assert.AreEqual(0, report.FailedCases);

        AssertCase(report, "EvidenceMissingField", expectedMissing: "ApprovalId", expectedInvalid: null, expectedReason: "EvidenceCandidateSchemaInvalid");
        AssertCase(report, "EvidenceEmptyScopes", expectedMissing: null, expectedInvalid: "ApprovalScopes", expectedReason: "EvidenceCandidateSchemaInvalid");
        AssertCase(report, "EvidenceDefaultTime", expectedMissing: null, expectedInvalid: "ApprovalTimestamp", expectedReason: "EvidenceCandidateSchemaInvalid");
        AssertCase(report, "RegistryMissingRecords", expectedMissing: "TrustedProvenanceRecords", expectedInvalid: null, expectedReason: "TrustRegistryCandidateInvalid");
        AssertCase(report, "RegistryEmptySourceKinds", expectedMissing: null, expectedInvalid: "AllowedSourceKinds", expectedReason: "TrustRegistryCandidateSchemaInvalid");
        AssertCase(report, "RecordMissingChecksum", expectedMissing: "TrustedProvenanceRecords[0].ApprovalEvidenceChecksum", expectedInvalid: null, expectedReason: "TrustRegistryCandidateSchemaInvalid");
        AssertCase(report, "RecordMissingProvidedBy", expectedMissing: "TrustedProvenanceRecords[1].ApprovalEvidenceProvidedBy", expectedInvalid: null, expectedReason: "TrustRegistryCandidateSchemaInvalid");
        AssertCase(report, "RecordDefaultValidUntil", expectedMissing: null, expectedInvalid: "TrustedProvenanceRecords[1].ValidUntil", expectedReason: "TrustRegistryCandidateSchemaInvalid");
    }

    [TestMethod]
    public void QuarantineNegativeMatrix_KeepsSafetyAndMainlineInvariantsFalse()
    {
        var report = new FormalRetrievalPromotionExternalApprovalQuarantineNegativeMatrixRunner().Run(
            rtPassed: true,
            p15Passed: true,
            new FormalRetrievalPromotionExternalApprovalQuarantineMatrixOptions { IsGate = true });

        Assert.IsFalse(report.PromotionToMainlinePerformed);
        Assert.IsFalse(report.MainlineEvidencePresent);
        Assert.IsFalse(report.MainlineTrustRegistryPresent);
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

    private static void AssertCase(
        FormalRetrievalPromotionExternalApprovalQuarantineNegativeMatrixReport report,
        string caseName,
        string? expectedMissing,
        string? expectedInvalid,
        string expectedReason)
    {
        var item = report.Cases.Single(c => c.CaseName == caseName);
        Assert.IsTrue(item.FailedAsExpected, caseName);
        CollectionAssert.Contains(item.ActualBlockedReasons.ToList(), expectedReason, caseName);

        if (expectedMissing is not null)
        {
            Assert.IsTrue(item.MissingFieldMatched, caseName);
            CollectionAssert.Contains(item.ActualMissingFields.ToList(), expectedMissing, caseName);
        }

        if (expectedInvalid is not null)
        {
            Assert.IsTrue(item.InvalidFieldMatched, caseName);
            CollectionAssert.Contains(item.ActualInvalidFields.ToList(), expectedInvalid, caseName);
        }
    }
}
