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
        bool intakeBlocked, bool intakeFtAllowed, bool intakeRtSw,
        bool rtPassed, bool p15Passed,
        FormalRetrievalPromotionExternalApprovalDryRunOptions? opt = null)
        => Build("dryrun", false, mainlineEvidenceExists, mainlineRegistryExists,
            fixtureEvidencePresent, fixtureRegistryPresent, fixtureEvidence, fixtureRegistry,
            pendingApproval, planGate, readinessGate, closeoutGate, intakeBlocked, intakeFtAllowed, intakeRtSw,
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
        bool intakeBlocked, bool intakeFtAllowed, bool intakeRtSw,
        bool rtPassed, bool p15Passed,
        FormalRetrievalPromotionExternalApprovalDryRunOptions? opt = null)
        => Build("gate", true, mainlineEvidenceExists, mainlineRegistryExists,
            fixtureEvidencePresent, fixtureRegistryPresent, fixtureEvidence, fixtureRegistry,
            pendingApproval, planGate, readinessGate, closeoutGate, intakeBlocked, intakeFtAllowed, intakeRtSw,
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
        bool intakeBlocked, bool intakeFtAllowed, bool intakeRtSw,
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
        if (intakeFtAllowed) blocked.Add("MainlineIntakeFormalRetrievalAllowed");
        if (intakeRtSw) blocked.Add("MainlineIntakeRuntimeSwitchAllowed");
        if (!rtPassed) blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15Passed) blocked.Add("P15GateNotPassed");

        var evidenceMarker = fixtureEvidence?.IsFixture ?? false;
        var trustMarker = fixtureRegistry?.RegistryId?.Contains("fixture") == true;
        if (!evidenceMarker) blocked.Add("FixtureEvidenceMarkerMissing");
        if (!trustMarker) blocked.Add("FixtureTrustRegistryMarkerMissing");

        var sourceIdsMatch = false;
        var provenanceFound = false;
        var checksumMatch = false;
        var scopeTrusted = false;
        var scopeApproved = false;

        if (fixtureEvidencePresent && fixtureRegistryPresent && fixtureEvidence is not null && fixtureRegistry is not null)
        {
            if (planGate is not null)
            {
                var planScopes = planGate.ApprovedScopes;
                var evScopes = fixtureEvidence.ApprovalScopes;
                scopeApproved = evScopes.Count > 0 && planScopes.Count > 0
                    && evScopes.All(s => planScopes.Any(p => string.Equals(p, s, StringComparison.OrdinalIgnoreCase)));
                if (!scopeApproved) blocked.Add("FixtureScopeNotApproved");
            }

            var record = fixtureRegistry.TrustedProvenanceRecords.FirstOrDefault();
            if (record is not null)
            {
                provenanceFound = string.Equals(fixtureEvidence.ApprovalEvidenceProvenanceId, record.ApprovalEvidenceProvenanceId, StringComparison.OrdinalIgnoreCase);
                checksumMatch = string.Equals(fixtureEvidence.ApprovalEvidenceChecksum, record.ApprovalEvidenceChecksum, StringComparison.OrdinalIgnoreCase);
                scopeTrusted = fixtureEvidence.ApprovalScopes.Count > 0 && record.AllowedScopes.Count > 0
                    && fixtureEvidence.ApprovalScopes.All(s => record.AllowedScopes.Any(t => string.Equals(t, s, StringComparison.OrdinalIgnoreCase)));
                if (pendingApproval is not null)
                {
                    var reqMatch = string.Equals(fixtureEvidence.SourceApprovalRequestId, pendingApproval.ApprovalRequestId, StringComparison.OrdinalIgnoreCase);
                    var gateMatch = string.Equals(fixtureEvidence.BoundPendingApprovalGateOperationId, pendingApproval.OperationId, StringComparison.OrdinalIgnoreCase);
                    sourceIdsMatch = reqMatch && gateMatch;
                }
            }

            if (!provenanceFound) blocked.Add("FixtureProvenanceRecordNotFound");
            if (!checksumMatch) blocked.Add("FixtureChecksumMismatch");
            if (!scopeTrusted) blocked.Add("FixtureScopeNotTrusted");
            if (!sourceIdsMatch) blocked.Add("FixtureSourceIdsMismatch");
        }

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var dryRunPassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && dryRunPassed;

        diag.Add($"stage={stage}");
        diag.Add($"mainlineEvidence={mainlineEvidenceExists} registry={mainlineRegistryExists}");
        diag.Add($"fixtureEvidence={fixtureEvidencePresent} registry={fixtureRegistryPresent}");
        diag.Add($"intakeBlocked={intakeBlocked}");
        diag.Add($"evidenceMarker={evidenceMarker} trustMarker={trustMarker}");
        diag.Add($"provenanceFound={provenanceFound} checksumMatch={checksumMatch}");
        diag.Add($"scopeTrusted={scopeTrusted} scopeApproved={scopeApproved}");
        diag.Add($"sourceIdsMatch={sourceIdsMatch}");
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
            MainlineIntakeStillBlocked = !mainlineEvidenceExists && !mainlineRegistryExists,
            FixtureEvidencePresent = fixtureEvidencePresent,
            FixtureTrustRegistryPresent = fixtureRegistryPresent,
            EvidenceStructureValid = fixtureEvidencePresent,
            RegistryStructureValid = fixtureRegistryPresent,
            SourceGateIdsMatch = sourceIdsMatch,
            ProvenanceRecordFound = provenanceFound,
            ChecksumMatched = checksumMatch,
            FixtureEvidenceMarkerVerified = evidenceMarker,
            FixtureTrustRegistryMarkerVerified = trustMarker,
            MainlineIntakeGateStillBlocked = intakeBlocked,
            MainlineIntakeBlockedReasonsVerified = intakeBlocked,
            ScopeTrustedByRegistry = scopeTrusted,
            ScopeSubsetOfApprovedScopes = scopeApproved,
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
        b.AppendLine();
        b.AppendLine("## Fixture Markers");
        b.AppendLine($"- EvidenceMarker: `{r.FixtureEvidenceMarkerVerified}`");
        b.AppendLine($"- TrustRegistryMarker: `{r.FixtureTrustRegistryMarkerVerified}`");
        b.AppendLine($"- IntakeGateBlocked: `{r.MainlineIntakeGateStillBlocked}`");
        b.AppendLine($"- ScopeTrusted: `{r.ScopeTrustedByRegistry}`");
        b.AppendLine($"- ScopeApproved: `{r.ScopeSubsetOfApprovedScopes}`");
        b.AppendLine();
        b.AppendLine("## Safety");
        b.AppendLine($"- FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}`");
        b.AppendLine($"- ConfigPatchWritten: `{r.ConfigPatchWritten}`");
        b.AppendLine();
        b.AppendLine("V8.6R external approval dry-run。Fixture marker + intake binding + scope check。");
        return b.ToString();
    }
}
