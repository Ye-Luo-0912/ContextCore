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
        bool intakeBlocked, bool intakeHasRequiredReasons,
        bool rtPassed, bool p15Passed,
        FormalRetrievalPromotionExternalApprovalDryRunOptions? opt = null)
        => Build("dryrun", false, mainlineEvidenceExists, mainlineRegistryExists,
            fixtureEvidencePresent, fixtureRegistryPresent, fixtureEvidence, fixtureRegistry,
            pendingApproval, planGate, readinessGate, closeoutGate, intakeBlocked, intakeHasRequiredReasons,
            rtPassed, p15Passed, opt);

    public FormalRetrievalPromotionExternalApprovalDryRunReport RunGate(
        bool mainlineEvidenceExists, bool mainlineRegistryExists,
        bool fixtureEvidencePresent, bool fixtureRegistryPresent,
        FormalRetrievalPromotionApprovalEvidence? fixtureEvidence,
        FormalRetrievalPromotionApprovalTrustRegistry? fixtureRegistry,
        FormalRetrievalPromotionApprovalReport? pendingApproval,
        FormalRetrievalPromotionPlanReport? planGate,
        FormalRetrievalPromotionReadinessAuditReport? readinessGate,
        ScopedRuntimePreviewLiveActivationCloseoutReport? closeoutGate,
        bool intakeBlocked, bool intakeHasRequiredReasons,
        bool rtPassed, bool p15Passed,
        FormalRetrievalPromotionExternalApprovalDryRunOptions? opt = null)
        => Build("gate", true, mainlineEvidenceExists, mainlineRegistryExists,
            fixtureEvidencePresent, fixtureRegistryPresent, fixtureEvidence, fixtureRegistry,
            pendingApproval, planGate, readinessGate, closeoutGate, intakeBlocked, intakeHasRequiredReasons,
            rtPassed, p15Passed, opt);

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
        bool intakeBlocked, bool intakeHasRequiredReasons,
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
        if (!intakeBlocked) blocked.Add("MainlineIntakeNotBlocked");
        if (!intakeHasRequiredReasons) blocked.Add("MainlineIntakeBlockedReasonsNotVerified");
        if (!rtPassed) blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15Passed) blocked.Add("P15GateNotPassed");

        var evidenceMarker = fixtureEvidence?.IsFixture ?? false;
        var trustMarker = fixtureRegistry?.IsFixture ?? false;
        if (!evidenceMarker) blocked.Add("FixtureEvidenceMarkerMissing");
        if (!trustMarker) blocked.Add("FixtureTrustRegistryMarkerMissing");

        var evidenceValid = fixtureEvidence is not null
            && !string.IsNullOrWhiteSpace(fixtureEvidence.ApprovalEvidenceId)
            && !string.IsNullOrWhiteSpace(fixtureEvidence.ApprovedBy)
            && fixtureEvidence.ApprovalScopes.Count > 0;
        var registryValid = fixtureRegistry is not null
            && !string.IsNullOrWhiteSpace(fixtureRegistry.RegistryId)
            && fixtureRegistry.AllowedSourceKinds.Count > 0
            && fixtureRegistry.TrustedProvenanceRecords.Count > 0;

        var recordStructureValid = false;
        var sourceKindTrusted = false;
        var providedByVerified = false;
        var trustRecordNotExpired = false;
        var trustRecReqIdMatched = false;
        var trustRecBoundGateMatched = false;

        var sourceGateIdsMatch = false;
        var approvalRequestBinding = false;
        var provenanceFound = false;
        var checksumMatch = false;
        var scopeTrusted = false;
        var scopeApproved = false;

        if (!evidenceValid) blocked.Add("FixtureEvidenceStructureInvalid");
        if (!registryValid) blocked.Add("FixtureRegistryStructureInvalid");

        if (evidenceValid && planGate is not null && readinessGate is not null && closeoutGate is not null
            && fixtureEvidence is not null)
        {
            sourceGateIdsMatch = string.Equals(fixtureEvidence.SourcePromotionPlanGateOperationId, planGate.OperationId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(fixtureEvidence.SourceReadinessGateOperationId, readinessGate.OperationId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(fixtureEvidence.SourceCloseoutGateOperationId, closeoutGate.OperationId, StringComparison.OrdinalIgnoreCase);

            if (pendingApproval is not null)
            {
                approvalRequestBinding = string.Equals(fixtureEvidence.SourceApprovalRequestId, pendingApproval.ApprovalRequestId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(fixtureEvidence.BoundPendingApprovalGateOperationId, pendingApproval.OperationId, StringComparison.OrdinalIgnoreCase);
            }

            var planScopes = planGate.ApprovedScopes;
            var evScopes = fixtureEvidence.ApprovalScopes;
            scopeApproved = evScopes.Count > 0 && planScopes.Count > 0
                && evScopes.All(s => planScopes.Any(p => string.Equals(p, s, StringComparison.OrdinalIgnoreCase)));
        }

        if (!sourceGateIdsMatch) blocked.Add("FixtureSourceGateIdsMismatch");
        if (!approvalRequestBinding) blocked.Add("FixtureApprovalRequestBindingFailed");
        if (!scopeApproved) blocked.Add("FixtureScopeNotApproved");

        if (registryValid && fixtureRegistry is not null && fixtureEvidence is not null)
        {
            var record = fixtureRegistry.TrustedProvenanceRecords
                .FirstOrDefault(r => string.Equals(r.ApprovalEvidenceProvenanceId, fixtureEvidence.ApprovalEvidenceProvenanceId, StringComparison.OrdinalIgnoreCase));
            provenanceFound = record is not null;

            if (provenanceFound && record is not null)
            {
                recordStructureValid = !string.IsNullOrWhiteSpace(record.ApprovalEvidenceSourceKind)
                    && !string.IsNullOrWhiteSpace(record.ApprovalEvidenceProvidedBy)
                    && !string.IsNullOrWhiteSpace(record.ApprovalEvidenceChecksum)
                    && !string.IsNullOrWhiteSpace(record.SourceApprovalRequestId)
                    && !string.IsNullOrWhiteSpace(record.BoundPendingApprovalGateOperationId)
                    && record.AllowedScopes.Count > 0
                    && !string.IsNullOrWhiteSpace(record.TrustMode)
                    && record.ValidUntil != default;
                if (!recordStructureValid) blocked.Add("FixtureRecordStructureInvalid");

                sourceKindTrusted = fixtureRegistry.AllowedSourceKinds.Any(k => string.Equals(k, fixtureEvidence.ApprovalEvidenceSourceKind, StringComparison.OrdinalIgnoreCase));
                providedByVerified = string.Equals(fixtureEvidence.ApprovalEvidenceProvidedBy, record.ApprovalEvidenceProvidedBy, StringComparison.OrdinalIgnoreCase);
                trustRecordNotExpired = record.ValidUntil == default || now <= record.ValidUntil;
                trustRecReqIdMatched = string.Equals(fixtureEvidence.SourceApprovalRequestId, record.SourceApprovalRequestId, StringComparison.OrdinalIgnoreCase);
                trustRecBoundGateMatched = string.Equals(fixtureEvidence.BoundPendingApprovalGateOperationId, record.BoundPendingApprovalGateOperationId, StringComparison.OrdinalIgnoreCase);

                checksumMatch = string.Equals(fixtureEvidence.ApprovalEvidenceChecksum, record.ApprovalEvidenceChecksum, StringComparison.OrdinalIgnoreCase);
                scopeTrusted = fixtureEvidence.ApprovalScopes.Count > 0 && record.AllowedScopes.Count > 0
                    && fixtureEvidence.ApprovalScopes.All(s => record.AllowedScopes.Any(t => string.Equals(t, s, StringComparison.OrdinalIgnoreCase)));

                if (!sourceKindTrusted) blocked.Add("FixtureSourceKindMismatch");
                if (!providedByVerified) blocked.Add("FixtureProvidedByMismatch");
                if (!trustRecordNotExpired) blocked.Add("FixtureTrustRecordExpired");
                if (!trustRecReqIdMatched) blocked.Add("FixtureTrustRecordApprovalRequestMismatch");
                if (!trustRecBoundGateMatched) blocked.Add("FixtureTrustRecordBoundGateMismatch");
            }

            if (!provenanceFound) blocked.Add("FixtureProvenanceRecordNotFound");
            if (!checksumMatch) blocked.Add("FixtureChecksumMismatch");
            if (!scopeTrusted) blocked.Add("FixtureScopeNotTrusted");
        }

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var dryRunPassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && dryRunPassed;

        diag.Add($"stage={stage}");
        diag.Add($"mainlineEvidence={mainlineEvidenceExists} registry={mainlineRegistryExists}");
        diag.Add($"fixtureEvidence={fixtureEvidencePresent} registry={fixtureRegistryPresent}");
        diag.Add($"intakeBlocked={intakeBlocked} reasonsVerified={intakeHasRequiredReasons}");
        diag.Add($"evidenceMarker={evidenceMarker} trustMarker={trustMarker}");
        diag.Add($"evidenceValid={evidenceValid} registryValid={registryValid}");
        diag.Add($"sourceGateIdsMatch={sourceGateIdsMatch} approvalBinding={approvalRequestBinding}");
        diag.Add($"provenanceFound={provenanceFound} checksumMatch={checksumMatch}");
        diag.Add($"scopeTrusted={scopeTrusted} scopeApproved={scopeApproved}");
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
            MainlineIntakeStillBlocked = intakeBlocked,
            FixtureEvidencePresent = fixtureEvidencePresent,
            FixtureTrustRegistryPresent = fixtureRegistryPresent,
            EvidenceStructureValid = evidenceValid,
            RegistryStructureValid = registryValid,
            SourceGateIdsMatch = sourceGateIdsMatch,
            ProvenanceRecordFound = provenanceFound,
            ChecksumMatched = checksumMatch,
            FixtureEvidenceMarkerVerified = evidenceMarker,
            FixtureTrustRegistryMarkerVerified = trustMarker,
            MainlineIntakeGateStillBlocked = intakeBlocked,
            MainlineIntakeBlockedReasonsVerified = intakeHasRequiredReasons,
            ScopeTrustedByRegistry = scopeTrusted,
            ScopeSubsetOfApprovedScopes = scopeApproved,
            ApprovalRequestBindingMatched = approvalRequestBinding,
            SourceKindTrusted = sourceKindTrusted,
            ProvidedByMatched = providedByVerified,
            TrustRecordNotExpired = trustRecordNotExpired,
            RecordStructureValid = recordStructureValid,
            IntakeSafetyFieldsVerified = intakeHasRequiredReasons,
            TrustRecordApprovalRequestIdMatched = trustRecReqIdMatched,
            TrustRecordBoundGateIdMatched = trustRecBoundGateMatched,
            P15GatePassed = p15Passed,
            RuntimeChangeGatePassed = rtPassed,
            FormalRetrievalAllowed = false, RuntimeSwitchAllowed = false, FormalPackageWritten = false,
            PackageOutputChanged = false, PackingPolicyChanged = false, VectorStoreBindingChanged = false,
            GlobalDefaultOn = false, ConfigPatchWritten = false, RuntimeActivation = false,
            NoRuntimeMutationInvariant = true,
            BlockedReasons = distinctBlocked, Diagnostics = diag,
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
        b.AppendLine($"- DryRunPassed: `{r.DryRunPassed}`  GatePassed: `{r.GatePassed}`");
        b.AppendLine();
        b.AppendLine("## Binding Verification");
        b.AppendLine($"- EvidenceMarker: `{r.FixtureEvidenceMarkerVerified}`  TrustMarker: `{r.FixtureTrustRegistryMarkerVerified}`");
        b.AppendLine($"- SourceGateIdsMatch: `{r.SourceGateIdsMatch}` (plan/readiness/closeout)");
        b.AppendLine($"- ApprovalRequestBinding: `{r.ApprovalRequestBindingMatched}` (request/gate)");
        b.AppendLine($"- IntakeBlocked: `{r.MainlineIntakeGateStillBlocked}`  ReasonsVerified: `{r.MainlineIntakeBlockedReasonsVerified}`");
        b.AppendLine($"- ScopeTrusted: `{r.ScopeTrustedByRegistry}`  ScopeApproved: `{r.ScopeSubsetOfApprovedScopes}`");
        b.AppendLine();
        b.AppendLine("## Safety");
        b.AppendLine($"- FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}`");
        b.AppendLine($"- ConfigPatchWritten: `{r.ConfigPatchWritten}`");
        b.AppendLine();
        b.AppendLine("V8.6R2 dry-run binding semantics hardening。");
        return b.ToString();
    }
}
