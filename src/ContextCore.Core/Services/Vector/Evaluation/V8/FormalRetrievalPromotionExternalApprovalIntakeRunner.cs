using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class FormalRetrievalPromotionExternalApprovalIntakeRunner
{
    public FormalRetrievalPromotionExternalApprovalIntakeReport RunIntake(
        FormalRetrievalPromotionApprovalEvidence? evidence,
        FormalRetrievalPromotionApprovalTrustRegistry? trustRegistry,
        FormalRetrievalPromotionApprovalReport? pendingApproval,
        FormalRetrievalPromotionPlanReport? planGate,
        FormalRetrievalPromotionReadinessAuditReport? readinessGate,
        ScopedRuntimePreviewLiveActivationCloseoutReport? closeoutGate,
        bool rtPassed, bool p15Passed,
        FormalRetrievalPromotionExternalApprovalIntakeOptions? opt = null)
        => Build("intake", false, evidence, trustRegistry, pendingApproval, planGate, readinessGate, closeoutGate, rtPassed, p15Passed, opt);

    public FormalRetrievalPromotionExternalApprovalIntakeReport RunGate(
        FormalRetrievalPromotionApprovalEvidence? evidence,
        FormalRetrievalPromotionApprovalTrustRegistry? trustRegistry,
        FormalRetrievalPromotionApprovalReport? pendingApproval,
        FormalRetrievalPromotionPlanReport? planGate,
        FormalRetrievalPromotionReadinessAuditReport? readinessGate,
        ScopedRuntimePreviewLiveActivationCloseoutReport? closeoutGate,
        bool rtPassed, bool p15Passed,
        FormalRetrievalPromotionExternalApprovalIntakeOptions? opt = null)
        => Build("gate", true, evidence, trustRegistry, pendingApproval, planGate, readinessGate, closeoutGate, rtPassed, p15Passed, opt);

    private static FormalRetrievalPromotionExternalApprovalIntakeReport Build(
        string stage, bool isGate,
        FormalRetrievalPromotionApprovalEvidence? evidence,
        FormalRetrievalPromotionApprovalTrustRegistry? trustRegistry,
        FormalRetrievalPromotionApprovalReport? pendingApproval,
        FormalRetrievalPromotionPlanReport? planGate,
        FormalRetrievalPromotionReadinessAuditReport? readinessGate,
        ScopedRuntimePreviewLiveActivationCloseoutReport? closeoutGate,
        bool rtPassed, bool p15Passed,
        FormalRetrievalPromotionExternalApprovalIntakeOptions? opt)
    {
        opt ??= new FormalRetrievalPromotionExternalApprovalIntakeOptions();
        var blocked = new List<string>();
        var diag = new List<string>();
        var now = DateTimeOffset.UtcNow;
        var evidencePresent = evidence is not null;
        var trustPresent = trustRegistry is not null;

        if (!opt.Enabled) blocked.Add("IntakeDisabled");
        if (!rtPassed) blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15Passed) blocked.Add("P15GateNotPassed");

        var evidenceFields = new List<string>();
        var registryFields = new List<string>();
        var evidenceStructureValid = false;
        var registryStructureValid = false;
        var upstreamGateIdsMatch = false;
        var approvalRequestIdBound = false;
        var provenanceRecordMatched = false;

        if (!evidencePresent)
        {
            blocked.Add("ExternalApprovalEvidenceMissing");
            evidenceFields.Add("status=missing");
        }
        else
        {
            evidenceStructureValid = !string.IsNullOrWhiteSpace(evidence!.ApprovalEvidenceId)
                && !string.IsNullOrWhiteSpace(evidence.ApprovedBy)
                && !string.IsNullOrWhiteSpace(evidence.ApprovalId)
                && evidence.ApprovalScopes.Count > 0;
            evidenceFields.Add($"evidenceId={evidence.ApprovalEvidenceId[..Math.Min(12, evidence.ApprovalEvidenceId.Length)]}...");
            evidenceFields.Add($"approvedBy={evidence.ApprovedBy}");
            evidenceFields.Add($"scopes={evidence.ApprovalScopes.Count}");
            evidenceFields.Add($"structureValid={evidenceStructureValid}");

            if (!evidenceStructureValid) blocked.Add("EvidenceStructureInvalid");

            if (pendingApproval is not null)
            {
                approvalRequestIdBound = !string.IsNullOrWhiteSpace(evidence.SourceApprovalRequestId)
                    && string.Equals(evidence.SourceApprovalRequestId, pendingApproval.ApprovalRequestId, StringComparison.OrdinalIgnoreCase);
                evidenceFields.Add($"requestIdBound={approvalRequestIdBound}");
                if (!approvalRequestIdBound) blocked.Add("EvidenceApprovalRequestIdNotBound");
            }

            if (planGate is not null)
            {
                var planMatch = string.Equals(evidence.SourcePromotionPlanGateOperationId, planGate.OperationId, StringComparison.OrdinalIgnoreCase);
                var readinessMatch = readinessGate is not null && string.Equals(evidence.SourceReadinessGateOperationId, readinessGate.OperationId, StringComparison.OrdinalIgnoreCase);
                var closeoutMatch = closeoutGate is not null && string.Equals(evidence.SourceCloseoutGateOperationId, closeoutGate.OperationId, StringComparison.OrdinalIgnoreCase);
                upstreamGateIdsMatch = planMatch && readinessMatch && closeoutMatch;
                evidenceFields.Add($"upstreamGateIdsMatch={upstreamGateIdsMatch}");
                if (!upstreamGateIdsMatch) blocked.Add("EvidenceUpstreamGateIdMismatch");
            }
        }

        if (!trustPresent)
        {
            blocked.Add("TrustRegistryMissing");
            registryFields.Add("status=missing");
        }
        else
        {
            registryStructureValid = !string.IsNullOrWhiteSpace(trustRegistry!.RegistryId)
                && trustRegistry.AllowedSourceKinds.Count > 0;
            registryFields.Add($"registryId={trustRegistry.RegistryId[..Math.Min(12, trustRegistry.RegistryId.Length)]}...");
            registryFields.Add($"sourceKinds={trustRegistry.AllowedSourceKinds.Count}");
            registryFields.Add($"records={trustRegistry.TrustedProvenanceRecords.Count}");
            registryFields.Add($"structureValid={registryStructureValid}");

            if (!registryStructureValid) blocked.Add("RegistryStructureInvalid");

            if (evidencePresent && evidence is not null)
            {
                provenanceRecordMatched = trustRegistry.TrustedProvenanceRecords
                    .Any(r => string.Equals(r.ApprovalEvidenceProvenanceId, evidence.ApprovalEvidenceProvenanceId, StringComparison.OrdinalIgnoreCase));
                registryFields.Add($"provenanceRecordMatched={provenanceRecordMatched}");
                if (!provenanceRecordMatched) blocked.Add("ProvenanceRecordNotFound");
            }
        }

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var intakePassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && intakePassed;

        diag.Add($"stage={stage}");
        diag.Add($"evidencePresent={evidencePresent}");
        diag.Add($"trustPresent={trustPresent}");
        diag.Add($"evidenceStructureValid={evidenceStructureValid}");
        diag.Add($"registryStructureValid={registryStructureValid}");
        diag.Add($"upstreamGateIdsMatch={upstreamGateIdsMatch}");
        diag.Add($"approvalRequestIdBound={approvalRequestIdBound}");
        diag.Add($"provenanceRecordMatched={provenanceRecordMatched}");
        diag.Add($"intakePassed={intakePassed} gatePassed={gatePassed}");
        if (!isGate) diag.Add("GatePassed=false is expected for non-gate artifact");

        return new FormalRetrievalPromotionExternalApprovalIntakeReport
        {
            OperationId = $"frp-intake-{stage}-{Guid.NewGuid():N}",
            CreatedAt = now,
            IntakePassed = intakePassed,
            GatePassed = gatePassed,
            Recommendation = intakePassed
                ? FormalRetrievalPromotionExternalApprovalIntakeRecommendations.ReadyForExternalApprovalProcessing
                : FormalRetrievalPromotionExternalApprovalIntakeRecommendations.BlockedByExternalApprovalMissing,
            NextAllowedPhase = intakePassed ? "ExternalApprovalProcessing" : "KeepPreviewOnly",
            EvidencePresent = evidencePresent,
            EvidencePath = "vector/v8/formal-retrieval-promotion-approval-evidence.json",
            TrustRegistryPresent = trustPresent,
            TrustRegistryPath = "vector/v8/formal-retrieval-promotion-approval-trust-registry.json",
            EvidenceStructureValid = evidenceStructureValid,
            RegistryStructureValid = registryStructureValid,
            UpstreamGateIdsMatch = upstreamGateIdsMatch,
            ApprovalRequestIdBound = approvalRequestIdBound,
            ProvenanceRecordMatched = provenanceRecordMatched,
            EvidenceFields = evidenceFields,
            RegistryFields = registryFields,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            FormalPackageWritten = false,
            GlobalDefaultOn = false,
            NoRuntimeMutationInvariant = true,
            BlockedReasons = distinctBlocked,
            Diagnostics = diag,
        };
    }

    public static string BuildMarkdown(string title, FormalRetrievalPromotionExternalApprovalIntakeReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- IntakePassed: `{r.IntakePassed}`");
        b.AppendLine($"- GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}`");
        b.AppendLine($"- NextAllowedPhase: `{r.NextAllowedPhase}`");
        b.AppendLine();
        b.AppendLine("## External Files");
        b.AppendLine($"- EvidencePresent: `{r.EvidencePresent}` path=`{r.EvidencePath}`");
        b.AppendLine($"- TrustRegistryPresent: `{r.TrustRegistryPresent}` path=`{r.TrustRegistryPath}`");
        b.AppendLine($"- EvidenceStructureValid: `{r.EvidenceStructureValid}`");
        b.AppendLine($"- RegistryStructureValid: `{r.RegistryStructureValid}`");
        b.AppendLine($"- UpstreamGateIdsMatch: `{r.UpstreamGateIdsMatch}`");
        b.AppendLine($"- ApprovalRequestIdBound: `{r.ApprovalRequestIdBound}`");
        b.AppendLine($"- ProvenanceRecordMatched: `{r.ProvenanceRecordMatched}`");
        b.AppendLine();
        b.AppendLine("## Safety");
        b.AppendLine($"- FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}`");
        b.AppendLine($"- NoRuntimeMutationInvariant: `{r.NoRuntimeMutationInvariant}`");
        b.AppendLine();
        b.AppendLine("V8.4 external approval intake。不生成假文件，不启用 formal retrieval。");
        return b.ToString();
    }
}
