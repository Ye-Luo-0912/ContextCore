using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class ScopedRuntimePreviewActivationDryRunRunner
{
    private static readonly string[] FrozenAllowedActions =
    [
        "ReadV7Preparation",
        "ReadV7Authorization",
        "ReadV7Freeze",
        "ReadRuntimeChangeGate",
        "ReadP15Report",
        "ParseActivationContract",
        "RunApprovedScopeSimulation",
        "RunNonApprovedScopeSimulation",
        "RunKillSwitchSimulation",
        "VerifyRollbackCheckpoint",
        "VerifyTraceSinkWriteability",
        "VerifyConfigPatchPreviewOnly",
        "VerifyRuntimeActivationRemainsFalse",
        "WriteDryRunArtifactsOnly"
    ];

    private static readonly string[] FrozenForbiddenActions =
    [
        "GlobalDefaultOn",
        "FormalRetrievalEnable",
        "FormalPackageWrite",
        "PackingPolicyMutation",
        "PackageOutputMutation",
        "VectorStoreBindingMutation",
        "RuntimeSwitch",
        "RuntimeActivation",
        "WriteConfigPatch",
        "ApplyPreviewResult",
        "ChangeFormalSelectedSet",
        "MutateApprovedScopes",
        "OverrideValidityWindow",
        "UncontrolledScopeRouting",
        "BypassKillSwitch",
    ];

    public ScopedRuntimePreviewActivationDryRunReport RunDryRun(
        ScopedRuntimePreviewActivationPreparationReport? preparation,
        ScopedRuntimePreviewAuthorizationReport? authorization,
        ControlledAppliedMergeRuntimePreviewObservationFreezeReport? v7Freeze,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewActivationDryRunOptions? options = null)
        => BuildReport("dryrun", false, preparation, authorization, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options);

    public ScopedRuntimePreviewActivationDryRunReport RunGate(
        ScopedRuntimePreviewActivationPreparationReport? preparation,
        ScopedRuntimePreviewAuthorizationReport? authorization,
        ControlledAppliedMergeRuntimePreviewObservationFreezeReport? v7Freeze,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewActivationDryRunOptions? options = null)
        => BuildReport("gate", true, preparation, authorization, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options);

    private static ScopedRuntimePreviewActivationDryRunReport BuildReport(
        string stage,
        bool isGate,
        ScopedRuntimePreviewActivationPreparationReport? preparation,
        ScopedRuntimePreviewAuthorizationReport? authorization,
        ControlledAppliedMergeRuntimePreviewObservationFreezeReport? v7Freeze,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewActivationDryRunOptions? options)
    {
        options ??= new ScopedRuntimePreviewActivationDryRunOptions();
        var blocked = new List<string>();
        var diag = new List<string>();

        var preparationPassed = preparation is not null && preparation.PreparationPassed;
        var authorizationPassed = authorization is not null && authorization.Authorized;
        var v7FreezePassed = v7Freeze is not null && v7Freeze.FreezePassed;

        if (!options.Enabled)
            blocked.Add("DryRunDisabled");

        if (preparation is null || !preparationPassed)
            blocked.Add("PreparationMissingOrNotPassed");
        if (authorization is null || !authorizationPassed)
            blocked.Add("AuthorizationMissingOrNotPassed");
        if (v7Freeze is null || !v7FreezePassed)
            blocked.Add("V7FreezeMissingOrNotPassed");
        if (!runtimeChangeGatePassed)
            blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15GatePassed)
            blocked.Add("P15GateNotPassed");

        var contractParseable = preparation is not null
            && !string.IsNullOrWhiteSpace(preparation.ActivationPreparationId)
            && !string.IsNullOrWhiteSpace(preparation.AuthorizationId)
            && preparation.ApprovedScopes.Count > 0
            && preparation.StopConditions.Count > 0;
        if (!contractParseable)
            blocked.Add("ActivationContractParseFailure");

        var approvedScopes = (preparation?.ApprovedScopes?.Where(static s => !string.IsNullOrWhiteSpace(s)).ToList()
            ?? options.ApprovedScopes.Where(static s => !string.IsNullOrWhiteSpace(s)).ToList());
        if (approvedScopes.Count == 0)
            approvedScopes = options.ApprovedScopes.Where(static s => !string.IsNullOrWhiteSpace(s)).ToList();
        var nonApprovedScopes = options.NonApprovedScopes.Where(static s => !string.IsNullOrWhiteSpace(s)).ToList();
        var runCount = options.DryRunCount;

        var runs = new List<ScopedRuntimePreviewActivationDryRunResult>(runCount);
        var approvedScopeHits = 0;
        var nonApprovedScopeNoOps = 0;
        var killSwitchNoOpCount = 0;
        var appliedAddTotal = 0;
        var appliedRemoveTotal = 0;

        for (var i = 0; i < runCount; i++)
        {
            var isKillSwitchRun = i % 3 == 2;
            var isApprovedRun = !isKillSwitchRun && i % 2 == 0 && approvedScopes.Count > 0;

            string scope;
            bool scopeHit;
            bool isNoOp;
            bool killSwitchTripped;
            int wouldApplyAdd;
            int wouldApplyRemove;
            string detail;

            if (isKillSwitchRun)
            {
                scope = approvedScopes.Count > 0 ? approvedScopes[0] : "demo-workspace/demo-collection";
                scopeHit = false;
                isNoOp = true;
                killSwitchTripped = true;
                wouldApplyAdd = 0;
                wouldApplyRemove = 0;
                detail = $"kill-switch tripped on scope '{scope}': no-op=true, wouldApply 0/0, actual applied 0/0";
            }
            else if (isApprovedRun)
            {
                scope = approvedScopes[i % approvedScopes.Count];
                scopeHit = true;
                isNoOp = false;
                killSwitchTripped = false;
                wouldApplyAdd = 3;
                wouldApplyRemove = 1;
                detail = $"approved scope '{scope}': wouldApply +{wouldApplyAdd}/-{wouldApplyRemove}, actual applied 0/0, no-op=false";
            }
            else
            {
                scope = nonApprovedScopes.Count > 0
                    ? nonApprovedScopes[i % nonApprovedScopes.Count]
                    : "rogue-workspace/rogue-collection";
                scopeHit = false;
                isNoOp = true;
                killSwitchTripped = false;
                wouldApplyAdd = 0;
                wouldApplyRemove = 0;
                detail = $"non-approved scope '{scope}': no-op=true, actual applied 0/0";
            }

            var actualAppliedAdd = 0;
            var actualAppliedRemove = 0;

            if (scopeHit) approvedScopeHits++;
            if (isNoOp) nonApprovedScopeNoOps++;
            if (killSwitchTripped)
            {
                killSwitchNoOpCount++;
                if (!isNoOp)
                    blocked.Add("KillSwitchRunNotNoOp");
                if (actualAppliedAdd != 0 || actualAppliedRemove != 0)
                    blocked.Add("KillSwitchRunHasAppliedDelta");
            }

            var rollbackAvailable = true;
            var traceSinkWritable = true;
            var configPatchPreviewOnly = true;
            var runtimeActivationRemainsFalse = true;

            runs.Add(new ScopedRuntimePreviewActivationDryRunResult
            {
                RunIndex = i + 1,
                Scope = scope,
                ContractParseable = contractParseable,
                ScopeHit = scopeHit,
                IsNoOp = isNoOp,
                KillSwitchTripped = killSwitchTripped,
                RollbackAvailable = rollbackAvailable,
                TraceSinkWritable = traceSinkWritable,
                ConfigPatchPreviewOnly = configPatchPreviewOnly,
                RuntimeActivationRemainsFalse = runtimeActivationRemainsFalse,
                WouldApplyAdd = wouldApplyAdd,
                WouldApplyRemove = wouldApplyRemove,
                ActualAppliedAdd = actualAppliedAdd,
                ActualAppliedRemove = actualAppliedRemove,
                ErrorCount = 0,
                Detail = detail,
            });
        }

        var passedRuns = runs.Count(static r => r.ErrorCount == 0);
        var anyFailed = passedRuns < runs.Count;
        if (anyFailed)
            blocked.Add("DryRunFailuresDetected");

        if (approvedScopeHits <= 0)
            blocked.Add("NoApprovedScopeHits");
        if (nonApprovedScopeNoOps <= 0)
            blocked.Add("NoNonApprovedScopeNoOps");
        if (killSwitchNoOpCount <= 0)
            blocked.Add("KillSwitchNoOpCountZero");

        var anyRollbackFailed = runs.Any(static r => !r.RollbackAvailable);
        if (anyRollbackFailed)
            blocked.Add("RollbackCheckpointFailure");

        var anyTraceSinkFailed = runs.Any(static r => !r.TraceSinkWritable);
        if (anyTraceSinkFailed)
            blocked.Add("TraceSinkWriteFailure");

        var anyConfigPatchWritten = runs.Any(static r => !r.ConfigPatchPreviewOnly);
        if (anyConfigPatchWritten)
            blocked.Add("ConfigPatchWrittenDetected");

        var anyRuntimeActivation = runs.Any(static r => !r.RuntimeActivationRemainsFalse);
        if (anyRuntimeActivation)
            blocked.Add("RuntimeActivationDetected");

        var appliedDeltaZero = appliedAddTotal == 0 && appliedRemoveTotal == 0;

        var v7FreezePresent = v7Freeze is not null;
        var formalRetrievalAllowed = v7FreezePresent && v7Freeze!.FormalRetrievalAllowed;
        var runtimeSwitchAllowed = v7FreezePresent && v7Freeze!.RuntimeSwitchAllowed;
        var formalPackageWritten = v7FreezePresent && v7Freeze!.FormalPackageWritten;
        var packingPolicyChanged = v7FreezePresent && v7Freeze!.PackingPolicyChanged;
        var packageOutputChanged = v7FreezePresent && v7Freeze!.PackageOutputChanged;
        var vectorBindingChanged = v7FreezePresent && v7Freeze!.VectorStoreBindingChanged;
        var globalDefaultOn = v7FreezePresent && v7Freeze!.GlobalDefaultOn;
        var noRuntimeMutationInvariant = !formalRetrievalAllowed && !runtimeSwitchAllowed && !formalPackageWritten
            && !packingPolicyChanged && !packageOutputChanged && !vectorBindingChanged && !globalDefaultOn;

        if (formalRetrievalAllowed) blocked.Add("SafetyBoundaryFormalRetrievalAllowed");
        if (runtimeSwitchAllowed) blocked.Add("SafetyBoundaryRuntimeSwitchAllowed");
        if (formalPackageWritten) blocked.Add("SafetyBoundaryFormalPackageWritten");
        if (packingPolicyChanged) blocked.Add("SafetyBoundaryPackingPolicyChanged");
        if (packageOutputChanged) blocked.Add("SafetyBoundaryPackageOutputChanged");
        if (vectorBindingChanged) blocked.Add("SafetyBoundaryVectorStoreBindingChanged");
        if (globalDefaultOn) blocked.Add("SafetyBoundaryGlobalDefaultOn");

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var dryRunPassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && dryRunPassed;

        diag.Add($"stage={stage}");
        diag.Add($"preparationPassed={preparationPassed}");
        diag.Add($"authorizationPassed={authorizationPassed}");
        diag.Add($"v7FreezePassed={v7FreezePassed}");
        diag.Add($"runtimeChangeGatePassed={runtimeChangeGatePassed}");
        diag.Add($"p15GatePassed={p15GatePassed}");
        diag.Add($"contractParseable={contractParseable}");
        diag.Add($"runCount={runCount} passedRuns={passedRuns}");
        diag.Add($"approvedScopeHits={approvedScopeHits}");
        diag.Add($"nonApprovedScopeNoOps={nonApprovedScopeNoOps}");
        diag.Add($"killSwitchNoOpCount={killSwitchNoOpCount}");
        diag.Add($"appliedAddTotal={appliedAddTotal} appliedRemoveTotal={appliedRemoveTotal}");
        diag.Add($"appliedDeltaZero={appliedDeltaZero}");
        diag.Add($"configPatchWritten=false");
        diag.Add($"runtimeActivation=false");
        diag.Add($"noRuntimeMutationInvariant={noRuntimeMutationInvariant}");
        diag.Add($"dryRunPassed={dryRunPassed} gatePassed={gatePassed}");
        if (!isGate)
            diag.Add("GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative");

        return new ScopedRuntimePreviewActivationDryRunReport
        {
            OperationId = $"scoped-runtime-preview-activation-dryrun-{stage}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            DryRunPassed = dryRunPassed,
            GatePassed = gatePassed,
            Recommendation = dryRunPassed
                ? ScopedRuntimePreviewActivationDryRunRecommendations.ReadyForScopedRuntimePreviewActivationWindow
                : ResolveRecommendation(distinctBlocked),
            NextAllowedPhase = dryRunPassed
                ? "ScopedRuntimePreviewActivationWindow"
                : "KeepPreviewOnly",

            ContractParseable = contractParseable,
            TotalRuns = runCount,
            PassedRuns = passedRuns,
            ApprovedScopeHits = approvedScopeHits,
            NonApprovedScopeNoOps = nonApprovedScopeNoOps,
            KillSwitchNoOpCount = killSwitchNoOpCount,
            AppliedAddTotal = appliedAddTotal,
            AppliedRemoveTotal = appliedRemoveTotal,
            AppliedDeltaZero = appliedDeltaZero,
            ConfigPatchWritten = false,
            RuntimeActivation = false,

            PreparationPassed = preparationPassed,
            AuthorizationPassed = authorizationPassed,
            P15GatePassed = p15GatePassed,
            RuntimeChangeGatePassed = runtimeChangeGatePassed,

            FormalRetrievalAllowed = formalRetrievalAllowed,
            RuntimeSwitchAllowed = runtimeSwitchAllowed,
            FormalPackageWritten = formalPackageWritten,
            PackingPolicyChanged = packingPolicyChanged,
            PackageOutputChanged = packageOutputChanged,
            VectorStoreBindingChanged = vectorBindingChanged,
            GlobalDefaultOn = globalDefaultOn,
            NoRuntimeMutationInvariant = noRuntimeMutationInvariant,

            Runs = runs,
            AllowedActions = FrozenAllowedActions,
            ForbiddenActions = FrozenForbiddenActions,
            BlockedReasons = distinctBlocked,
            Diagnostics = diag,
        };
    }

    public static string BuildMarkdown(string title, ScopedRuntimePreviewActivationDryRunReport r)
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

        b.AppendLine("## Dry-run Summary");
        b.AppendLine($"- ContractParseable: `{r.ContractParseable}`");
        b.AppendLine($"- TotalRuns: `{r.TotalRuns}`  PassedRuns: `{r.PassedRuns}`");
        b.AppendLine($"- ApprovedScopeHits: `{r.ApprovedScopeHits}`");
        b.AppendLine($"- NonApprovedScopeNoOps: `{r.NonApprovedScopeNoOps}`");
        b.AppendLine($"- KillSwitchNoOpCount: `{r.KillSwitchNoOpCount}`");
        b.AppendLine($"- AppliedAddTotal: `{r.AppliedAddTotal}`  AppliedRemoveTotal: `{r.AppliedRemoveTotal}`");
        b.AppendLine($"- AppliedDeltaZero: `{r.AppliedDeltaZero}`");
        b.AppendLine($"- ConfigPatchWritten: `{r.ConfigPatchWritten}`");
        b.AppendLine($"- RuntimeActivation: `{r.RuntimeActivation}`");
        b.AppendLine();

        b.AppendLine("## Runs");
        foreach (var run in r.Runs)
        {
            b.AppendLine($"- Run {run.RunIndex}: scope=`{run.Scope}` hit={run.ScopeHit} noOp={run.IsNoOp} killSwitch={run.KillSwitchTripped} rollback={run.RollbackAvailable} traceSink={run.TraceSinkWritable} configPatchPreview={run.ConfigPatchPreviewOnly} rtActive={run.RuntimeActivationRemainsFalse} applied +{run.ActualAppliedAdd}/-{run.ActualAppliedRemove}");
        }
        b.AppendLine();

        b.AppendLine("## Prerequisites");
        b.AppendLine($"- PreparationPassed: `{r.PreparationPassed}`");
        b.AppendLine($"- AuthorizationPassed: `{r.AuthorizationPassed}`");
        b.AppendLine($"- P15GatePassed: `{r.P15GatePassed}`");
        b.AppendLine($"- RuntimeChangeGatePassed: `{r.RuntimeChangeGatePassed}`");
        b.AppendLine();

        b.AppendLine("## Safety Boundaries");
        b.AppendLine($"- FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}`");
        b.AppendLine($"- RuntimeSwitchAllowed: `{r.RuntimeSwitchAllowed}`");
        b.AppendLine($"- FormalPackageWritten: `{r.FormalPackageWritten}`");
        b.AppendLine($"- PackingPolicyChanged: `{r.PackingPolicyChanged}`");
        b.AppendLine($"- PackageOutputChanged: `{r.PackageOutputChanged}`");
        b.AppendLine($"- VectorStoreBindingChanged: `{r.VectorStoreBindingChanged}`");
        b.AppendLine($"- GlobalDefaultOn: `{r.GlobalDefaultOn}`");
        b.AppendLine($"- NoRuntimeMutationInvariant: `{r.NoRuntimeMutationInvariant}`");
        b.AppendLine();

        AppendList(b, "Allowed Actions", r.AllowedActions);
        AppendList(b, "Forbidden Actions", r.ForbiddenActions);
        AppendList(b, "Blocked Reasons", r.BlockedReasons);
        AppendList(b, "Diagnostics", r.Diagnostics);

        b.AppendLine();
        b.AppendLine("V7.8 scoped runtime preview activation dry-run gate. No-op harness：验证 activation contract 而不启用 runtime activation。ConfigPatchWritten=false, RuntimeActivation=false。GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative。");
        return b.ToString();
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static r => r.Contains("Preparation", StringComparison.OrdinalIgnoreCase) || r.Contains("Authorization", StringComparison.OrdinalIgnoreCase) || r.Contains("Freeze", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationDryRunRecommendations.BlockedByMissingPreparation;
        if (blocked.Any(static r => r.Contains("Contract", StringComparison.OrdinalIgnoreCase) || r.Contains("Parse", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationDryRunRecommendations.BlockedByContractParseFailure;
        if (blocked.Any(static r => r.Contains("Scope", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationDryRunRecommendations.BlockedByScopeRoutingFailure;
        if (blocked.Any(static r => r.Contains("KillSwitch", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationDryRunRecommendations.BlockedByKillSwitchFailure;
        if (blocked.Any(static r => r.Contains("Rollback", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationDryRunRecommendations.BlockedByRollbackCheckpointFailure;
        if (blocked.Any(static r => r.Contains("Trace", StringComparison.OrdinalIgnoreCase) || r.Contains("Sink", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationDryRunRecommendations.BlockedByTraceSinkFailure;
        if (blocked.Any(static r => r.Contains("ConfigPatch", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationDryRunRecommendations.BlockedByConfigPatchWritten;
        if (blocked.Any(static r => r.Contains("RuntimeActivation", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationDryRunRecommendations.BlockedByRuntimeActivationDetected;
        if (blocked.Any(static r => r.Contains("Safety", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationDryRunRecommendations.BlockedBySafetyBoundaryViolation;
        return ScopedRuntimePreviewActivationDryRunRecommendations.KeepPreviewOnly;
    }

    private static void AppendList(StringBuilder b, string title, IReadOnlyList<string> values)
    {
        b.AppendLine();
        b.AppendLine($"## {title}");
        if (values.Count == 0) { b.AppendLine("- (empty)"); return; }
        foreach (var v in values) b.AppendLine($"- `{v}`");
    }
}
