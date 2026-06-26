using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreFormalRetrievalPromotionApprovalTrustChainValidationMatrixTests
{
    [TestMethod]
    public void TrustChainValidationMatrix_AllScenarios_PassAsExpected()
    {
        var report = new FormalRetrievalPromotionApprovalTrustChainValidationMatrixRunner().Run(
            rtPassed: true,
            p15Passed: true,
            mainlineEvidencePresent: false,
            mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalTrustChainValidationMatrixOptions { IsGate = true });

        Assert.IsTrue(report.ChainValidationPassed, string.Join(",", report.BlockedReasons));
        Assert.IsTrue(report.GatePassed);
        Assert.IsTrue(report.TotalCases >= 8, $"TotalCases={report.TotalCases}");
        Assert.IsTrue(report.PositiveCases >= 1);
        Assert.IsTrue(report.NegativeCases >= 7);
        Assert.AreEqual(report.TotalCases, report.PassedCases);
        Assert.AreEqual(0, report.FailedCases);
    }

    [TestMethod]
    public void TrustChainValidationMatrix_PositiveCase_TrustChainValidated()
    {
        var report = new FormalRetrievalPromotionApprovalTrustChainValidationMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalTrustChainValidationMatrixOptions { IsGate = true });

        var positive = report.Cases.Single(c => c.CaseName == "ChainComplete");
        Assert.AreEqual(TrustChainValidationStatuses.TrustChainValidated, positive.ActualStatus);
        Assert.IsTrue(positive.ActualChainComplete);
        Assert.AreEqual(0, positive.ActualMismatchReasons.Count);
        Assert.AreEqual(0, positive.ActualMismatchFields.Count);
        Assert.AreEqual(0, positive.MatchedRecordIndex);
        Assert.AreEqual("fixture-provenance-trustchain-001", positive.MatchedProvenanceId);
    }

    [TestMethod]
    public void TrustChainValidationMatrix_NegativeCases_StatusAndReasonMatch()
    {
        var report = new FormalRetrievalPromotionApprovalTrustChainValidationMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalTrustChainValidationMatrixOptions { IsGate = true });

        AssertNegative(report, "ProvenanceIdNotInRegistry",
            TrustChainMismatchReasons.EvidenceProvenanceNotFoundInRegistry,
            nameof(ContextCore.Abstractions.Models.FormalRetrievalPromotionApprovalEvidence.ApprovalEvidenceProvenanceId));

        AssertNegative(report, "SourceKindMismatchWithRecord",
            TrustChainMismatchReasons.EvidenceSourceKindMismatch,
            nameof(ContextCore.Abstractions.Models.FormalRetrievalPromotionApprovalEvidence.ApprovalEvidenceSourceKind));

        AssertNegative(report, "SourceKindNotInAllowedKinds",
            TrustChainMismatchReasons.EvidenceSourceKindNotAllowed,
            nameof(ContextCore.Abstractions.Models.FormalRetrievalPromotionApprovalEvidence.ApprovalEvidenceSourceKind));

        AssertNegative(report, "ChecksumMismatch",
            TrustChainMismatchReasons.EvidenceChecksumMismatch,
            nameof(ContextCore.Abstractions.Models.FormalRetrievalPromotionApprovalEvidence.ApprovalEvidenceChecksum));

        AssertNegative(report, "ProvidedByMismatch",
            TrustChainMismatchReasons.EvidenceProvidedByMismatch,
            nameof(ContextCore.Abstractions.Models.FormalRetrievalPromotionApprovalEvidence.ApprovalEvidenceProvidedBy));

        AssertNegative(report, "SourceApprovalRequestIdMismatch",
            TrustChainMismatchReasons.EvidenceSourceApprovalRequestIdMismatch,
            nameof(ContextCore.Abstractions.Models.FormalRetrievalPromotionApprovalEvidence.SourceApprovalRequestId));

        AssertNegative(report, "BoundPendingApprovalGateOperationIdMismatch",
            TrustChainMismatchReasons.EvidenceBoundPendingApprovalGateOperationIdMismatch,
            nameof(ContextCore.Abstractions.Models.FormalRetrievalPromotionApprovalEvidence.BoundPendingApprovalGateOperationId));

        AssertNegative(report, "TrustModeMismatch",
            TrustChainMismatchReasons.EvidenceTrustModeMismatch,
            nameof(ContextCore.Abstractions.Models.FormalRetrievalPromotionApprovalEvidence.ApprovalEvidenceTrustMode));

        AssertNegative(report, "ApprovalScopesNotSubsetOfRecord",
            TrustChainMismatchReasons.EvidenceApprovalScopesNotSubsetOfRecord,
            nameof(ContextCore.Abstractions.Models.FormalRetrievalPromotionApprovalEvidence.ApprovalScopes));

        AssertNegative(report, "ApprovalTimestampAfterValidUntil",
            TrustChainMismatchReasons.EvidenceApprovalTimestampAfterRecordValidUntil,
            nameof(ContextCore.Abstractions.Models.FormalRetrievalPromotionApprovalEvidence.ApprovalTimestamp));
    }

    [TestMethod]
    public void TrustChainValidationMatrix_NoManualReviewContract_AllNonApprovalFlagsFalse()
    {
        var report = new FormalRetrievalPromotionApprovalTrustChainValidationMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalTrustChainValidationMatrixOptions { IsGate = true });

        Assert.IsFalse(report.ManualReviewRequired);
        Assert.IsFalse(report.ApprovalSealed);
        Assert.IsFalse(report.CapabilityGrantWritten);
        Assert.IsFalse(report.PromotionToMainlinePerformed);
        Assert.IsFalse(report.EvidenceCopiedToMainline);
        Assert.IsFalse(report.TrustRegistryCopiedToMainline);
        Assert.IsFalse(report.MainlineEvidencePresent);
        Assert.IsFalse(report.MainlineTrustRegistryPresent);
    }

    [TestMethod]
    public void TrustChainValidationMatrix_SafetyInvariants_AllFalse()
    {
        var report = new FormalRetrievalPromotionApprovalTrustChainValidationMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalTrustChainValidationMatrixOptions { IsGate = true });

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
    public void TrustChainValidationMatrix_MainlineEvidencePresent_BlocksMatrix()
    {
        var report = new FormalRetrievalPromotionApprovalTrustChainValidationMatrixRunner().Run(
            rtPassed: true, p15Passed: true, mainlineEvidencePresent: true, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalTrustChainValidationMatrixOptions { IsGate = true });

        Assert.IsFalse(report.ChainValidationPassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "MainlineEvidencePresent");
    }

    [TestMethod]
    public void TrustChainValidationMatrix_RuntimeGateNotPassed_BlocksMatrix()
    {
        var report = new FormalRetrievalPromotionApprovalTrustChainValidationMatrixRunner().Run(
            rtPassed: false, p15Passed: true, mainlineEvidencePresent: false, mainlineRegistryPresent: false,
            new FormalRetrievalPromotionApprovalTrustChainValidationMatrixOptions { IsGate = true });

        Assert.IsFalse(report.ChainValidationPassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeChangeGateNotPassed");
    }

    private static void AssertNegative(
        FormalRetrievalPromotionApprovalTrustChainValidationMatrixReport report,
        string caseName,
        string expectedReason,
        string expectedField)
    {
        var item = report.Cases.Single(c => c.CaseName == caseName);
        Assert.IsTrue(item.PassedAsExpected, $"{caseName}: PassedAsExpected");
        Assert.AreEqual(TrustChainValidationStatuses.TrustChainBroken, item.ActualStatus, caseName);
        Assert.IsFalse(item.ActualChainComplete, caseName);
        CollectionAssert.Contains(item.ActualMismatchReasons.ToList(), expectedReason, caseName);
        CollectionAssert.Contains(item.ActualMismatchFields.ToList(), expectedField, caseName);
        Assert.IsTrue(item.MismatchReasonMatched, caseName);
        Assert.IsTrue(item.MismatchFieldMatched, caseName);
    }
}
