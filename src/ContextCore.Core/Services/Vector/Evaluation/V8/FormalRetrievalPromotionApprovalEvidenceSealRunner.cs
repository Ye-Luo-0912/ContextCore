using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class FormalRetrievalPromotionApprovalEvidenceSealRunner
{
    private static readonly string[] FrozenAllowedActions =
    [
        "ReadApprovalEvidenceFile", "ReadV8ApprovalPendingGate", "ReadV8PlanGate", "ReadV8ReadinessGate",
        "ReadV7CloseoutGate", "ReadP15Report", "ReadRuntimeChangeGate",
        "ValidateEvidenceProvenance", "ValidateEvidenceIsExternal",
        "ValidateBoundPendingApprovalGate", "ValidateScopeSubset", "ValidateSourceGateIdMatch",
        "SealApprovalEvidence", "WriteSealArtifactsOnly"
    ];

    private static readonly string[] FrozenForbiddenActions =
    [
        "GlobalDefaultOn", "FormalRetrievalEnable", "FormalPackageWrite", "PackingPolicyMutation",
        "PackageOutputMutation", "VectorStoreBindingMutation", "RuntimeSwitch", "RuntimeActivation",
        "WriteConfigPatch", "FabricateApprovalEvidence", "BypassEvidenceProvenance",
        "AutoApproveWithoutExternalEvidence", "OverrideSealRecord",
    ];

    public FormalRetrievalPromotionApprovalEvidenceSealReport RunSeal(
        FormalRetrievalPromotionApprovalEvidence? evidence,
        FormalRetrievalPromotionApprovalTrustRegistry? trustRegistry,
        FormalRetrievalPromotionApprovalReport? approvalArtifact,
        FormalRetrievalPromotionPlanReport? planGate,
        FormalRetrievalPromotionReadinessAuditReport? readinessGate,
        ScopedRuntimePreviewLiveActivationCloseoutReport? closeoutGate,
        bool rtPassed, bool p15Passed,
        FormalRetrievalPromotionApprovalEvidenceSealOptions? options = null)
        => BuildReport("seal", false, evidence, trustRegistry, approvalArtifact, planGate, readinessGate, closeoutGate, rtPassed, p15Passed, options);

    public FormalRetrievalPromotionApprovalEvidenceSealReport RunGate(
        FormalRetrievalPromotionApprovalEvidence? evidence,
        FormalRetrievalPromotionApprovalTrustRegistry? trustRegistry,
        FormalRetrievalPromotionApprovalReport? approvalArtifact,
        FormalRetrievalPromotionPlanReport? planGate,
        FormalRetrievalPromotionReadinessAuditReport? readinessGate,
        ScopedRuntimePreviewLiveActivationCloseoutReport? closeoutGate,
        bool rtPassed, bool p15Passed,
        FormalRetrievalPromotionApprovalEvidenceSealOptions? options = null)
        => BuildReport("gate", true, evidence, trustRegistry, approvalArtifact, planGate, readinessGate, closeoutGate, rtPassed, p15Passed, options);

    private static FormalRetrievalPromotionApprovalEvidenceSealReport BuildReport(
        string stage, bool isGate,
        FormalRetrievalPromotionApprovalEvidence? evidence,
        FormalRetrievalPromotionApprovalTrustRegistry? trustRegistry,
        FormalRetrievalPromotionApprovalReport? approvalArtifact,
        FormalRetrievalPromotionPlanReport? planGate,
        FormalRetrievalPromotionReadinessAuditReport? readinessGate,
        ScopedRuntimePreviewLiveActivationCloseoutReport? closeoutGate,
        bool rtPassed, bool p15Passed,
        FormalRetrievalPromotionApprovalEvidenceSealOptions? options)
    {
        options ??= new FormalRetrievalPromotionApprovalEvidenceSealOptions();
        var blocked = new List<string>();
        var diag = new List<string>();
        var now = DateTimeOffset.UtcNow;

        var evidencePresent = evidence is not null;
        var planGatePassed = planGate is not null && planGate.GatePassed;
        var readinessGatePassed = readinessGate is not null && readinessGate.GatePassed;
        var closeoutGatePassed = closeoutGate is not null && closeoutGate.GatePassed;

        if (!options.Enabled) blocked.Add("SealDisabled");
        if (!planGatePassed) blocked.Add("PlanGateNotPassed");
        if (!readinessGatePassed) blocked.Add("ReadinessGateNotPassed");
        if (!closeoutGatePassed) blocked.Add("CloseoutGateNotPassed");
        if (!rtPassed) blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15Passed) blocked.Add("P15GateNotPassed");

        var boundPendingGateVerified = false;
        var pendingReasonsManualOnly = false;
        if (approvalArtifact is not null)
        {
            boundPendingGateVerified = approvalArtifact.ApprovalGatePassed == false
                && approvalArtifact.GatePassed == false
                && approvalArtifact.ApprovalGranted == false
                && string.Equals(approvalArtifact.NextAllowedPhase, "KeepPreviewOnly", StringComparison.OrdinalIgnoreCase);

            var allowedManualReasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "ManualApprovalMissing", "ApprovalIdMissing", "ApprovalScopeMissing" };
            var actualReasons = approvalArtifact.BlockedReasons
                .Where(static r => !string.IsNullOrWhiteSpace(r))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            pendingReasonsManualOnly = actualReasons.Count > 0 && actualReasons.IsSubsetOf(allowedManualReasons);

            if (!boundPendingGateVerified) blocked.Add("BoundPendingGateNotInBlockedState");
            if (!pendingReasonsManualOnly) blocked.Add("PendingGateBlockedReasonsNotManualOnly");
        }
        else
        {
            blocked.Add("BoundPendingGateMissing");
        }

        if (!evidencePresent)
        {
            blocked.Add("ApprovalEvidenceMissing");
            diag.Add("approvalEvidenceMissing=true");
        }

        var approvedBy = "";
        var approvalId = "";
        var approvalScopes = Array.Empty<string>();
        var approvalSource = "";
        var scopeSubsetValidated = false;
        var sourceGateIdsMatch = false;
        var isExternal = false;
        var evidenceSourceKind = "";
        var evidenceProvidedBy = "";

        if (evidencePresent && evidence is not null)
        {
            approvedBy = evidence.ApprovedBy;
            approvalId = evidence.ApprovalId;
            approvalScopes = evidence.ApprovalScopes?.ToArray() ?? Array.Empty<string>();
            approvalSource = evidence.ApprovalSource;
            isExternal = evidence.ApprovalEvidenceIsExternal;
            evidenceSourceKind = evidence.ApprovalEvidenceSourceKind;
            evidenceProvidedBy = evidence.ApprovalEvidenceProvidedBy;

            if (!isExternal) blocked.Add("EvidenceNotExternal");
            if (string.IsNullOrWhiteSpace(evidenceSourceKind)) blocked.Add("EvidenceSourceKindMissing");
            if (string.IsNullOrWhiteSpace(evidenceProvidedBy)) blocked.Add("EvidenceProvidedByMissing");
            if (string.IsNullOrWhiteSpace(evidence.ApprovalEvidenceProvenanceId)) blocked.Add("EvidenceProvenanceIdMissing");
            if (string.IsNullOrWhiteSpace(evidence.ApprovalEvidenceTrustMode)) blocked.Add("EvidenceTrustModeMissing");

            if (string.IsNullOrWhiteSpace(approvedBy)) blocked.Add("EvidenceApprovedByEmpty");
            if (string.IsNullOrWhiteSpace(approvalId)) blocked.Add("EvidenceApprovalIdEmpty");
            if (approvalScopes.Length == 0) blocked.Add("EvidenceApprovalScopesEmpty");

            var planScopes = planGate?.ApprovedScopes ?? Array.Empty<string>();
            scopeSubsetValidated = approvalScopes.Length > 0 && planScopes.Count > 0
                && approvalScopes.All(s => planScopes.Any(p => string.Equals(p, s, StringComparison.OrdinalIgnoreCase)));
            if (!scopeSubsetValidated) blocked.Add("EvidenceScopeNotSubsetOfPlan");

            var planGateOpId = planGate?.OperationId ?? "";
            var readinessGateOpId = readinessGate?.OperationId ?? "";
            var closeoutGateOpId = closeoutGate?.OperationId ?? "";
            sourceGateIdsMatch = !string.IsNullOrWhiteSpace(evidence.SourcePromotionPlanGateOperationId)
                && string.Equals(evidence.SourcePromotionPlanGateOperationId, planGateOpId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(evidence.SourceReadinessGateOperationId, readinessGateOpId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(evidence.SourceCloseoutGateOperationId, closeoutGateOpId, StringComparison.OrdinalIgnoreCase);
            if (!sourceGateIdsMatch) blocked.Add("EvidenceSourceGateIdMismatch");

            if (approvalArtifact is not null)
            {
                if (string.IsNullOrWhiteSpace(evidence.SourceApprovalRequestId))
                    blocked.Add("EvidenceSourceApprovalRequestIdMissing");
                else if (!string.Equals(evidence.SourceApprovalRequestId, approvalArtifact.ApprovalRequestId, StringComparison.OrdinalIgnoreCase))
                    blocked.Add("EvidenceSourceApprovalRequestIdMismatch");

                if (string.IsNullOrWhiteSpace(evidence.BoundPendingApprovalGateOperationId))
                    blocked.Add("EvidenceBoundPendingGateIdMissing");
                else if (!string.Equals(evidence.BoundPendingApprovalGateOperationId, approvalArtifact.OperationId, StringComparison.OrdinalIgnoreCase))
                    blocked.Add("EvidenceBoundPendingGateIdMismatch");
            }

            if (trustRegistry is null)
            {
                blocked.Add("ApprovalTrustRegistryMissing");
            }
            else if (trustRegistry.AllowedSourceKinds.Count == 0 || trustRegistry.TrustedProvenanceRecords.Count == 0)
            {
                blocked.Add("ApprovalTrustRegistryInvalid");
            }
            else
            {
                var provenanceRecord = trustRegistry.TrustedProvenanceRecords
                    .FirstOrDefault(r => string.Equals(r.ApprovalEvidenceProvenanceId, evidence.ApprovalEvidenceProvenanceId, StringComparison.OrdinalIgnoreCase));

                if (provenanceRecord is null)
                {
                    blocked.Add("ApprovalEvidenceProvenanceUntrusted");
                }
                else
                {
                    if (!trustRegistry.AllowedSourceKinds.Any(k => string.Equals(k, evidence.ApprovalEvidenceSourceKind, StringComparison.OrdinalIgnoreCase)))
                        blocked.Add("ApprovalEvidenceSourceKindUntrusted");

                    if (!string.Equals(evidence.ApprovalEvidenceProvidedBy, provenanceRecord.ApprovalEvidenceProvidedBy, StringComparison.OrdinalIgnoreCase))
                        blocked.Add("ApprovalEvidenceProvidedByMismatch");

                    if (!string.Equals(evidence.ApprovalEvidenceChecksum, provenanceRecord.ApprovalEvidenceChecksum, StringComparison.OrdinalIgnoreCase))
                        blocked.Add("ApprovalEvidenceChecksumMismatch");

                    if (!string.Equals(evidence.SourceApprovalRequestId, provenanceRecord.SourceApprovalRequestId, StringComparison.OrdinalIgnoreCase))
                        blocked.Add("ApprovalEvidenceRequestIdMismatch");

                    if (!string.Equals(evidence.BoundPendingApprovalGateOperationId, provenanceRecord.BoundPendingApprovalGateOperationId, StringComparison.OrdinalIgnoreCase))
                        blocked.Add("ApprovalEvidenceBoundGateIdMismatch");

                    if (provenanceRecord.ValidUntil != default && DateTimeOffset.UtcNow > provenanceRecord.ValidUntil)
                        blocked.Add("ApprovalEvidenceTrustRecordExpired");

                    var trustScopes = provenanceRecord.AllowedScopes;
                    var scopeSubsetOfTrust = approvalScopes.Length > 0 && trustScopes.Count > 0
                        && approvalScopes.All(s => trustScopes.Any(t => string.Equals(t, s, StringComparison.OrdinalIgnoreCase)));
                    if (!scopeSubsetOfTrust) blocked.Add("ApprovalEvidenceScopeNotTrusted");
                }

                isExternal = true;
                evidenceSourceKind = evidence.ApprovalEvidenceSourceKind;
                evidenceProvidedBy = evidence.ApprovalEvidenceProvidedBy;
            }
        }

        var configPatchWritten = evidencePresent && evidence?.ApprovalEvidenceChecksum?.Contains("cfg-patch") == true;
        if (configPatchWritten) blocked.Add("SafetyBoundaryConfigPatchWritten");

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var sealPassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && sealPassed;

        diag.Add($"stage={stage}");
        diag.Add($"evidencePresent={evidencePresent}");
        diag.Add($"boundPendingGateVerified={boundPendingGateVerified}");
        diag.Add($"pendingReasonsManualOnly={pendingReasonsManualOnly}");
        diag.Add($"isExternal={isExternal}");
        diag.Add($"scopeSubsetValidated={scopeSubsetValidated}");
        diag.Add($"sourceGateIdsMatch={sourceGateIdsMatch}");
        diag.Add($"sealPassed={sealPassed} gatePassed={gatePassed}");
        if (!isGate) diag.Add("GatePassed=false is expected for non-gate artifact");

        return new FormalRetrievalPromotionApprovalEvidenceSealReport
        {
            OperationId = $"frp-evidence-seal-{stage}-{Guid.NewGuid():N}",
            CreatedAt = now,
            SealPassed = sealPassed,
            GatePassed = gatePassed,
            Recommendation = sealPassed
                ? FormalRetrievalPromotionApprovalEvidenceSealRecommendations.EvidenceSealedManualApprovalComplete
                : FormalRetrievalPromotionApprovalEvidenceSealRecommendations.BlockedByEvidenceMissing,
            NextAllowedPhase = sealPassed ? "FormalRetrievalPromotionEvidenceSealed" : "KeepPreviewOnly",

            EvidencePresent = evidencePresent,
            EvidencePath = "vector/v8/formal-retrieval-promotion-approval-evidence.json",
            ApprovalEvidenceId = evidence?.ApprovalEvidenceId ?? "",
            ApprovedBy = approvedBy,
            ApprovalId = approvalId,
            ApprovalScopes = approvalScopes,
            ApprovalSource = approvalSource,
            ScopeSubsetValidated = scopeSubsetValidated,
            SourceGateIdsMatch = sourceGateIdsMatch,

            ApprovalEvidenceIsExternal = isExternal,
            ApprovalEvidenceSourceKind = evidenceSourceKind,
            ApprovalEvidenceProvidedBy = evidenceProvidedBy,
            BoundPendingApprovalGateVerified = boundPendingGateVerified,
            PendingApprovalBlockedReasonsManualOnly = pendingReasonsManualOnly,
            SourceApprovalRequestIdMatched = false,
            BoundPendingApprovalGateIdMatched = false,
            TrustAnchorPresent = trustRegistry is not null,
            EvidenceProvenanceTrusted = !blocked.Contains("ApprovalEvidenceProvenanceUntrusted", StringComparer.OrdinalIgnoreCase),
            EvidenceChecksumMatched = !blocked.Contains("ApprovalEvidenceChecksumMismatch", StringComparer.OrdinalIgnoreCase),

            V8ApprovalPendingGatePresent = approvalArtifact is not null,
            V8PlanGatePassed = planGatePassed,
            V8ReadinessGatePassed = readinessGatePassed,
            V7CloseoutGatePassed = closeoutGatePassed,
            P15GatePassed = p15Passed,
            RuntimeChangeGatePassed = rtPassed,

            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            VectorStoreBindingChanged = false,
            GlobalDefaultOn = false,
            ConfigPatchWritten = false,
            RuntimeActivation = false,
            NoRuntimeMutationInvariant = true,

            AllowedActions = FrozenAllowedActions,
            ForbiddenActions = FrozenForbiddenActions,
            BlockedReasons = distinctBlocked,
            Diagnostics = diag,
        };
    }

    public static string BuildMarkdown(string title, FormalRetrievalPromotionApprovalEvidenceSealReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- SealPassed: `{r.SealPassed}`");
        b.AppendLine($"- GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}`");
        b.AppendLine($"- NextAllowedPhase: `{r.NextAllowedPhase}`");
        b.AppendLine();
        b.AppendLine("## Evidence Seal");
        b.AppendLine($"- EvidencePresent: `{r.EvidencePresent}`");
        b.AppendLine($"- EvidenceIsExternal: `{r.ApprovalEvidenceIsExternal}`");
        b.AppendLine($"- EvidenceSourceKind: `{r.ApprovalEvidenceSourceKind}`");
        b.AppendLine($"- EvidenceProvidedBy: `{r.ApprovalEvidenceProvidedBy}`");
        b.AppendLine($"- ApprovedBy: `{r.ApprovedBy}`");
        b.AppendLine($"- ScopeSubsetValidated: `{r.ScopeSubsetValidated}`");
        b.AppendLine($"- SourceGateIdsMatch: `{r.SourceGateIdsMatch}`");
        b.AppendLine($"- BoundPendingGateVerified: `{r.BoundPendingApprovalGateVerified}`");
        b.AppendLine();
        b.AppendLine("## Safety Boundaries");
        b.AppendLine($"- FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}`");
        b.AppendLine($"- ConfigPatchWritten: `{r.ConfigPatchWritten}`");
        b.AppendLine($"- PackageOutputChanged: `{r.PackageOutputChanged}`");
        b.AppendLine($"- NoRuntimeMutationInvariant: `{r.NoRuntimeMutationInvariant}`");
        b.AppendLine();
        b.AppendLine("V8.3R formal retrieval promotion approval evidence seal。Provenance validation + pending gate binding。FormalRetrievalAllowed=false。");
        return b.ToString();
    }
}
