using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class FormalRetrievalPromotionExternalApprovalDryRunNegativeMatrixRunner
{
    public FormalRetrievalPromotionExternalApprovalDryRunNegativeMatrixReport Run(
        bool rtPassed, bool p15Passed,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent,
        FormalRetrievalPromotionApprovalEvidence? fixtureEvidence,
        FormalRetrievalPromotionApprovalTrustRegistry? fixtureRegistry,
        FormalRetrievalPromotionApprovalReport? pendingApproval,
        FormalRetrievalPromotionPlanReport? planGate,
        FormalRetrievalPromotionReadinessAuditReport? readinessGate,
        ScopedRuntimePreviewLiveActivationCloseoutReport? closeoutGate,
        bool intakeBlocked,
        FormalRetrievalPromotionExternalApprovalDryRunMatrixOptions? opt = null)
    {
        opt ??= new FormalRetrievalPromotionExternalApprovalDryRunMatrixOptions();
        var now = DateTimeOffset.UtcNow;
        var isGate = opt.IsGate;
        var dryRunner = new FormalRetrievalPromotionExternalApprovalDryRunRunner();
        var cases = new List<FormalRetrievalPromotionExternalApprovalDryRunNegativeCase>();
        var baseEvidence = fixtureEvidence;
        var baseRegistry = fixtureRegistry;

        foreach (var scenario in NegativeScenarios)
        {
            var (evidence, registry) = ModifyFixtures(baseEvidence, baseRegistry, scenario);
            var report = dryRunner.RunGate(
                mainlineEvidencePresent, mainlineRegistryPresent,
                evidence is not null, registry is not null,
                evidence, registry,
                pendingApproval, planGate, readinessGate, closeoutGate,
                intakeBlocked, intakeBlocked,
                rtPassed, p15Passed);

            var failedAsExpected = !report.DryRunPassed && report.BlockedReasons.Any(r =>
                string.Equals(r, scenario.ExpectedReason, StringComparison.OrdinalIgnoreCase));

            cases.Add(new FormalRetrievalPromotionExternalApprovalDryRunNegativeCase
            {
                CaseName = scenario.CaseName,
                ExpectedBlockedReason = scenario.ExpectedReason,
                ActualBlockedReasons = report.BlockedReasons,
                FailedAsExpected = failedAsExpected,
            });
        }

        var passedCases = cases.Count(c => c.FailedAsExpected);
        var failedCases = cases.Count - passedCases;
        var matrixPassed = failedCases == 0;
        var gatePassed = isGate && matrixPassed;
        var blocked = new List<string>();
        if (!matrixPassed) blocked.Add("NegativeMatrixFailed");

        return new FormalRetrievalPromotionExternalApprovalDryRunNegativeMatrixReport
        {
            OperationId = $"frp-neg-matrix-{Guid.NewGuid():N}",
            CreatedAt = now,
            MatrixPassed = matrixPassed,
            GatePassed = gatePassed,
            Recommendation = matrixPassed ? "NegativeMatrixPassed" : "NegativeMatrixFailed",
            TotalCases = cases.Count,
            PassedCases = passedCases,
            FailedCases = failedCases,
            Cases = cases,
            FormalRetrievalAllowed = false,
            NoRuntimeMutationInvariant = true,
            BlockedReasons = blocked,
            Diagnostics = new List<string> { $"total={cases.Count} passed={passedCases} failed={failedCases}" },
        };
    }

    private static readonly (string CaseName, string ExpectedReason)[] NegativeScenarios =
    [
        ("SourceKindMismatch", "FixtureSourceKindMismatch"),
        ("ProvidedByMismatch", "FixtureProvidedByMismatch"),
        ("TrustRecordExpired", "FixtureTrustRecordExpired"),
        ("ChecksumMismatch", "FixtureChecksumMismatch"),
        ("RecordApprovalRequestMismatch", "FixtureTrustRecordApprovalRequestMismatch"),
        ("RecordBoundGateMismatch", "FixtureTrustRecordBoundGateMismatch"),
        ("SourceGateIdsMismatch", "FixtureSourceGateIdsMismatch"),
    ];

    private static (FormalRetrievalPromotionApprovalEvidence?, FormalRetrievalPromotionApprovalTrustRegistry?) ModifyFixtures(
        FormalRetrievalPromotionApprovalEvidence? evidence,
        FormalRetrievalPromotionApprovalTrustRegistry? registry,
        (string CaseName, string ExpectedReason) scenario)
    {
        if (evidence is null || registry is null) return (evidence, registry);

        FormalRetrievalPromotionApprovalEvidence modEv;
        FormalRetrievalPromotionApprovalTrustRegistry modReg;

        var records = registry.TrustedProvenanceRecords.Select((r, i) =>
            i == 0 && scenario.CaseName == "TrustRecordExpired"
                ? new FormalRetrievalPromotionApprovalTrustedProvenanceRecord
                {
                    ApprovalEvidenceProvenanceId = r.ApprovalEvidenceProvenanceId,
                    ApprovalEvidenceSourceKind = r.ApprovalEvidenceSourceKind,
                    ApprovalEvidenceProvidedBy = r.ApprovalEvidenceProvidedBy,
                    ApprovalEvidenceChecksum = r.ApprovalEvidenceChecksum,
                    SourceApprovalRequestId = r.SourceApprovalRequestId,
                    BoundPendingApprovalGateOperationId = r.BoundPendingApprovalGateOperationId,
                    AllowedScopes = r.AllowedScopes,
                    TrustMode = r.TrustMode,
                    ValidUntil = DateTimeOffset.UtcNow.AddDays(-1),
                }
                : r).ToList();

        switch (scenario.CaseName)
        {
            case "SourceKindMismatch":
                modEv = CopyEvidence(evidence, sourceKind: "wrong-kind");
                modReg = new FormalRetrievalPromotionApprovalTrustRegistry { RegistryId = registry.RegistryId, IsFixture = registry.IsFixture, RegistryCreatedAt = registry.RegistryCreatedAt, AllowedSourceKinds = new List<string> { "only-this-kind" }, TrustedProvenanceRecords = records };
                break;
            case "ProvidedByMismatch":
                modEv = CopyEvidence(evidence, providedBy: "WrongPerson");
                modReg = registry;
                break;
            case "TrustRecordExpired":
                modEv = evidence;
                modReg = new FormalRetrievalPromotionApprovalTrustRegistry { RegistryId = registry.RegistryId, IsFixture = registry.IsFixture, RegistryCreatedAt = registry.RegistryCreatedAt, AllowedSourceKinds = registry.AllowedSourceKinds, TrustedProvenanceRecords = records };
                break;
            case "ChecksumMismatch":
                modEv = CopyEvidence(evidence, checksum: "wrong-checksum-999");
                modReg = registry;
                break;
            case "RecordApprovalRequestMismatch":
                modEv = CopyEvidence(evidence, srcReqId: "wrong-request-id");
                modReg = registry;
                break;
            case "RecordBoundGateMismatch":
                modEv = CopyEvidence(evidence, boundGateId: "wrong-bound-gate-id");
                modReg = registry;
                break;
            case "SourceGateIdsMismatch":
                modEv = CopyEvidence(evidence, planGateId: "wrong-plan-id");
                modReg = registry;
                break;
            default:
                modEv = evidence;
                modReg = registry;
                break;
        }

        return (modEv, modReg);
    }

    private static FormalRetrievalPromotionApprovalEvidence CopyEvidence(
        FormalRetrievalPromotionApprovalEvidence e,
        string sourceKind = "", string providedBy = "", string checksum = "",
        string srcReqId = "", string boundGateId = "", string planGateId = "")
    {
        return new FormalRetrievalPromotionApprovalEvidence
        {
            ApprovalEvidenceId = e.ApprovalEvidenceId, ApprovedBy = e.ApprovedBy, ApprovalId = e.ApprovalId,
            ApprovalScopes = e.ApprovalScopes, ApprovalSource = e.ApprovalSource, ApprovalTimestamp = e.ApprovalTimestamp,
            SourcePromotionPlanGateOperationId = string.IsNullOrEmpty(planGateId) ? e.SourcePromotionPlanGateOperationId : planGateId,
            SourceReadinessGateOperationId = e.SourceReadinessGateOperationId,
            SourceCloseoutGateOperationId = e.SourceCloseoutGateOperationId,
            OperatorStatement = e.OperatorStatement, EvidenceCreatedAt = e.EvidenceCreatedAt,
            ApprovalEvidenceSourceKind = string.IsNullOrEmpty(sourceKind) ? e.ApprovalEvidenceSourceKind : sourceKind,
            ApprovalEvidenceProvenanceId = e.ApprovalEvidenceProvenanceId,
            ApprovalEvidenceProvidedBy = string.IsNullOrEmpty(providedBy) ? e.ApprovalEvidenceProvidedBy : providedBy,
            ApprovalEvidenceProvidedAt = e.ApprovalEvidenceProvidedAt,
            ApprovalEvidenceTrustMode = e.ApprovalEvidenceTrustMode,
            ApprovalEvidenceIsExternal = e.ApprovalEvidenceIsExternal, IsFixture = e.IsFixture,
            ApprovalEvidenceChecksum = string.IsNullOrEmpty(checksum) ? e.ApprovalEvidenceChecksum : checksum,
            SourceApprovalRequestId = string.IsNullOrEmpty(srcReqId) ? e.SourceApprovalRequestId : srcReqId,
            BoundPendingApprovalGateOperationId = string.IsNullOrEmpty(boundGateId) ? e.BoundPendingApprovalGateOperationId : boundGateId,
        };
    }

    public static string BuildMarkdown(string title, FormalRetrievalPromotionExternalApprovalDryRunNegativeMatrixReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- MatrixPassed: `{r.MatrixPassed}` GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Total: `{r.TotalCases}` Passed: `{r.PassedCases}` Failed: `{r.FailedCases}`");
        b.AppendLine();
        b.AppendLine("## Negative Cases");
        foreach (var c in r.Cases)
            b.AppendLine($"- `{c.CaseName}`: expected=`{c.ExpectedBlockedReason}` failedAsExpected=`{c.FailedAsExpected}` actual=`{string.Join(", ", c.ActualBlockedReasons)}`");
        b.AppendLine();
        b.AppendLine("V8.7 dry-run negative matrix。");
        return b.ToString();
    }
}

public sealed class FormalRetrievalPromotionExternalApprovalDryRunMatrixOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
