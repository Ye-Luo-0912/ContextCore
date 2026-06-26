using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class FormalRetrievalPromotionExternalApprovalDryRunRunner
{
    public FormalRetrievalPromotionExternalApprovalDryRunReport RunDryRun(
        bool mainlineEvidenceExists, bool mainlineRegistryExists,
        bool fixtureEvidencePresent, bool fixtureRegistryPresent,
        FormalRetrievalPromotionApprovalEvidence? fixtureEvidence,
        FormalRetrievalPromotionApprovalTrustRegistry? fixtureRegistry,
        FormalRetrievalPromotionApprovalReport? pendingApproval,
        FormalRetrievalPromotionPlanReport? planGate,
        FormalRetrievalPromotionReadinessAuditReport? readinessGate,
        ScopedRuntimePreviewLiveActivationCloseoutReport? closeoutGate,
        bool rtPassed, bool p15Passed,
        FormalRetrievalPromotionExternalApprovalDryRunOptions? opt = null)
        => Build("dryrun", false, mainlineEvidenceExists, mainlineRegistryExists,
            fixtureEvidencePresent, fixtureRegistryPresent, fixtureEvidence, fixtureRegistry,
            pendingApproval, planGate, readinessGate, closeoutGate, rtPassed, p15Passed, opt);

    public FormalRetrievalPromotionExternalApprovalDryRunReport RunGate(
        bool mainlineEvidenceExists, bool mainlineRegistryExists,
        bool fixtureEvidencePresent, bool fixtureRegistryPresent,
        FormalRetrievalPromotionApprovalEvidence? fixtureEvidence,
        FormalRetrievalPromotionApprovalTrustRegistry? fixtureRegistry,
        FormalRetrievalPromotionApprovalReport? pendingApproval,
        FormalRetrievalPromotionPlanReport? planGate,
        FormalRetrievalPromotionReadinessAuditReport? readinessGate,
        ScopedRuntimePreviewLiveActivationCloseoutReport? closeoutGate,
        bool rtPassed, bool p15Passed,
        FormalRetrievalPromotionExternalApprovalDryRunOptions? opt = null)
        => Build("gate", true, mainlineEvidenceExists, mainlineRegistryExists,
            fixtureEvidencePresent, fixtureRegistryPresent, fixtureEvidence, fixtureRegistry,
            pendingApproval, planGate, readinessGate, closeoutGate, rtPassed, p15Passed, opt);

    private static FormalRetrievalPromotionExternalApprovalDryRunReport Build(
        string stage, bool isGate,
        bool mainlineEvidenceExists, bool mainlineRegistryExists,
        bool fixtureEvidencePresent, bool fixtureRegistryPresent,
        FormalRetrievalPromotionApprovalEvidence? fixtureEvidence,
        FormalRetrievalPromotionApprovalTrustRegistry? fixtureRegistry,
        FormalRetrievalPromotionApprovalReport? pendingApproval,
        FormalRetrievalPromotionPlanReport? planGate,
        FormalRetrievalPromotionReadinessAuditReport? readinessGate,
        ScopedRuntimePreviewLiveActivationCloseoutReport? closeoutGate,
        bool rtPassed, bool p15Passed,
        FormalRetrievalPromotionExternalApprovalDryRunOptions? opt)
    {
        opt ??= new FormalRetrievalPromotionExternalApprovalDryRunOptions();
        var blocked = new List<string>();
        var diag = new List<string>();
        var now = DateTimeOffset.UtcNow;

        if (mainlineEvidenceExists) blocked.Add("MainlineEvidencePresent");
        if (mainlineRegistryExists) blocked.Add("MainlineTrustRegistryPresent");
        if (!fixtureEvidencePresent) blocked.Add("FixtureEvidenceMissing");
        if (!fixtureRegistryPresent) blocked.Add("FixtureTrustRegistryMissing");
        if (!rtPassed) blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15Passed) blocked.Add("P15GateNotPassed");

        var evidenceValid = false;
        var registryValid = false;
        var sourceIdsMatch = false;
        var requestIdMatch = false;
        var provenanceFound = false;
        var checksumMatch = false;
        var sourceKindMatch = false;
        var providedByMatch = false;
        var scopeMatch = false;
        var notExpired = false;

        if (fixtureEvidencePresent && fixtureRegistryPresent
            && fixtureEvidence is not null && fixtureRegistry is not null)
        {
            evidenceValid = !string.IsNullOrWhiteSpace(fixtureEvidence.ApprovalEvidenceId)
                && !string.IsNullOrWhiteSpace(fixtureEvidence.ApprovedBy)
                && fixtureEvidence.ApprovalScopes.Count > 0;
            registryValid = !string.IsNullOrWhiteSpace(fixtureRegistry.RegistryId)
                && fixtureRegistry.TrustedProvenanceRecords.Count > 0;

            if (!evidenceValid) blocked.Add("FixtureEvidenceStructureInvalid");
            if (!registryValid) blocked.Add("FixtureRegistryStructureInvalid");

            if (pendingApproval is not null)
            {
                requestIdMatch = string.Equals(fixtureEvidence.SourceApprovalRequestId, pendingApproval.ApprovalRequestId, StringComparison.OrdinalIgnoreCase);
                var boundMatch = string.Equals(fixtureEvidence.BoundPendingApprovalGateOperationId, pendingApproval.OperationId, StringComparison.OrdinalIgnoreCase);
                if (!requestIdMatch || !boundMatch) blocked.Add("FixtureNoRealApprovalRequestBinding");
            }

            if (planGate is not null)
            {
                var pm = string.Equals(fixtureEvidence.SourcePromotionPlanGateOperationId, planGate.OperationId, StringComparison.OrdinalIgnoreCase);
                var rm = readinessGate is not null && string.Equals(fixtureEvidence.SourceReadinessGateOperationId, readinessGate.OperationId, StringComparison.OrdinalIgnoreCase);
                var cm = closeoutGate is not null && string.Equals(fixtureEvidence.SourceCloseoutGateOperationId, closeoutGate.OperationId, StringComparison.OrdinalIgnoreCase);
                sourceIdsMatch = pm && rm && cm;
                if (!sourceIdsMatch) blocked.Add("FixtureSourceGateIdMismatch");
            }

            if (registryValid)
            {
                var record = fixtureRegistry.TrustedProvenanceRecords
                    .FirstOrDefault(r => string.Equals(r.ApprovalEvidenceProvenanceId, fixtureEvidence.ApprovalEvidenceProvenanceId, StringComparison.OrdinalIgnoreCase));
                provenanceFound = record is not null;
                if (provenanceFound && record is not null)
                {
                    sourceKindMatch = fixtureRegistry.AllowedSourceKinds.Any(k => string.Equals(k, fixtureEvidence.ApprovalEvidenceSourceKind, StringComparison.OrdinalIgnoreCase));
                    providedByMatch = string.Equals(fixtureEvidence.ApprovalEvidenceProvidedBy, record.ApprovalEvidenceProvidedBy, StringComparison.OrdinalIgnoreCase);
                    checksumMatch = string.Equals(fixtureEvidence.ApprovalEvidenceChecksum, record.ApprovalEvidenceChecksum, StringComparison.OrdinalIgnoreCase);
                    notExpired = record.ValidUntil == default || now <= record.ValidUntil;

                    var trustScopes = record.AllowedScopes;
                    var evScopes = fixtureEvidence.ApprovalScopes;
                    scopeMatch = evScopes.Count > 0 && trustScopes.Count > 0
                        && evScopes.All(s => trustScopes.Any(t => string.Equals(t, s, StringComparison.OrdinalIgnoreCase)));
                }

                if (!provenanceFound) blocked.Add("FixtureProvenanceRecordNotFound");
                if (!sourceKindMatch) blocked.Add("FixtureSourceKindMismatch");
                if (!providedByMatch) blocked.Add("FixtureProvidedByMismatch");
                if (!checksumMatch) blocked.Add("FixtureChecksumMismatch");
                if (!scopeMatch) blocked.Add("FixtureScopeMismatch");
                if (!notExpired) blocked.Add("FixtureTrustRecordExpired");
            }
        }

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var dryRunPassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && dryRunPassed;

        diag.Add($"stage={stage}");
        diag.Add($"mainlineEvidenceExists={mainlineEvidenceExists}");
        diag.Add($"mainlineRegistryExists={mainlineRegistryExists}");
        diag.Add($"fixtureEvidencePresent={fixtureEvidencePresent}");
        diag.Add($"fixtureRegistryPresent={fixtureRegistryPresent}");
        diag.Add($"evidenceValid={evidenceValid}");
        diag.Add($"sourceIdsMatch={sourceIdsMatch}");
        diag.Add($"provenanceFound={provenanceFound}");
        diag.Add($"checksumMatch={checksumMatch}");
        diag.Add($"dryRunPassed={dryRunPassed} gatePassed={gatePassed}");
        if (!isGate) diag.Add("GatePassed=false is expected for non-gate artifact");

        return new FormalRetrievalPromotionExternalApprovalDryRunReport
        {
            OperationId = $"frp-dryrun-{stage}-{Guid.NewGuid():N}",
            CreatedAt = now,
            DryRunPassed = dryRunPassed,
            GatePassed = gatePassed,
            Recommendation = dryRunPassed
                ? FormalRetrievalPromotionExternalApprovalDryRunRecommendations.FixtureDryRunValidationPassed
                : FormalRetrievalPromotionExternalApprovalDryRunRecommendations.BlockedByFixtureMissing,
            NextAllowedPhase = dryRunPassed ? "ExternalApprovalDryRunComplete" : "KeepPreviewOnly",
            FixtureIsolationVerified = !mainlineEvidenceExists && !mainlineRegistryExists,
            MainlineIntakeStillBlocked = mainlineEvidenceExists == false && mainlineRegistryExists == false,
            FixtureEvidencePresent = fixtureEvidencePresent,
            FixtureTrustRegistryPresent = fixtureRegistryPresent,
            EvidenceStructureValid = evidenceValid,
            RegistryStructureValid = registryValid,
            SourceGateIdsMatch = sourceIdsMatch,
            ProvenanceRecordFound = provenanceFound,
            ChecksumMatched = checksumMatch,
            P15GatePassed = p15Passed,
            RuntimeChangeGatePassed = rtPassed,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            FormalPackageWritten = false,
            GlobalDefaultOn = false,
            NoRuntimeMutationInvariant = true,
            BlockedReasons = distinctBlocked,
            Diagnostics = diag,
        };
    }

    public static string BuildMarkdown(string title, FormalRetrievalPromotionExternalApprovalDryRunReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- DryRunPassed: `{r.DryRunPassed}`");
        b.AppendLine($"- GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}`");
        b.AppendLine($"- NextAllowedPhase: `{r.NextAllowedPhase}`");
        b.AppendLine();
        b.AppendLine("## Fixture Validation");
        b.AppendLine($"- FixtureIsolationVerified: `{r.FixtureIsolationVerified}`");
        b.AppendLine($"- MainlineIntakeStillBlocked: `{r.MainlineIntakeStillBlocked}`");
        b.AppendLine($"- EvidenceStructureValid: `{r.EvidenceStructureValid}`");
        b.AppendLine($"- RegistryStructureValid: `{r.RegistryStructureValid}`");
        b.AppendLine($"- SourceGateIdsMatch: `{r.SourceGateIdsMatch}`");
        b.AppendLine($"- ProvenanceRecordFound: `{r.ProvenanceRecordFound}`");
        b.AppendLine($"- ChecksumMatched: `{r.ChecksumMatched}`");
        b.AppendLine();
        b.AppendLine("## Safety");
        b.AppendLine($"- FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}`");
        b.AppendLine();
        b.AppendLine("V8.6 external approval dry-run。Fixture-isolated positive path，不启用 formal retrieval。");
        return b.ToString();
    }
}
