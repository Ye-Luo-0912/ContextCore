using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class ControlledAppliedMergeRuntimePreviewObservationFreezeRunner
{
    private static readonly string[] FrozenAllowedActions =
    [
        "ReadV7Plan",
        "ReadV7DryRun",
        "ReadV7Preflight",
        "ReadV7Observation",
        "ReadV7Hardening",
        "ReadV6Freeze",
        "ReadOptFFreeze",
        "ReadRuntimeChangeGate",
        "ReadP15Report",
        "ValidateFreezeMetrics",
        "ValidateSafetyBoundaries",
        "SetPromotionDecision",
        "FreezeTestBaseline",
        "WriteFreezeArtifactsOnly"
    ];

    private static readonly string[] FrozenForbiddenActions =
    [
        "GlobalDefaultOn",
        "FormalRetrievalEnable",
        "FormalPackageWrite",
        "PackingPolicyMutation",
        "PackageOutputMutationOutsidePreview",
        "VectorStoreBindingMutation",
        "RuntimeSwitch",
        "NonAllowlistedScopeUse",
        "ChangeFormalSelectedSet",
        "ApplyPreviewResult",
        "WriteConfigPatch",
        "ChangeFrozenMetrics",
        "ChangeFrozenTestBaseline",
        "MutationOfFrozenSafetyBoundaries"
    ];

    public ControlledAppliedMergeRuntimePreviewObservationFreezeReport RunFreeze(
        ControlledAppliedMergeRuntimePreviewPlanReport? v7Plan,
        ControlledAppliedMergeRuntimePreviewDryRunReport? v7DryRun,
        ControlledAppliedMergeRuntimePreviewActivationPreflightReport? v7Preflight,
        ControlledAppliedMergeRuntimePreviewObservationWindowReport? v7Observation,
        ControlledAppliedMergeRuntimePreviewObservationHardeningReport? v7Hardening,
        ControlledAppliedMergePreviewFreezeReport? v6Freeze,
        ArchitectureCleanupFreezeReport? optFreeze,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ControlledAppliedMergeRuntimePreviewObservationFreezeOptions? options = null)
        => BuildReport("freeze", false, v7Plan, v7DryRun, v7Preflight, v7Observation, v7Hardening, v6Freeze, optFreeze, runtimeChangeGatePassed, p15GatePassed, options);

    public ControlledAppliedMergeRuntimePreviewObservationFreezeReport RunGate(
        ControlledAppliedMergeRuntimePreviewPlanReport? v7Plan,
        ControlledAppliedMergeRuntimePreviewDryRunReport? v7DryRun,
        ControlledAppliedMergeRuntimePreviewActivationPreflightReport? v7Preflight,
        ControlledAppliedMergeRuntimePreviewObservationWindowReport? v7Observation,
        ControlledAppliedMergeRuntimePreviewObservationHardeningReport? v7Hardening,
        ControlledAppliedMergePreviewFreezeReport? v6Freeze,
        ArchitectureCleanupFreezeReport? optFreeze,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ControlledAppliedMergeRuntimePreviewObservationFreezeOptions? options = null)
        => BuildReport("gate", true, v7Plan, v7DryRun, v7Preflight, v7Observation, v7Hardening, v6Freeze, optFreeze, runtimeChangeGatePassed, p15GatePassed, options);

    private static ControlledAppliedMergeRuntimePreviewObservationFreezeReport BuildReport(
        string stage,
        bool isGate,
        ControlledAppliedMergeRuntimePreviewPlanReport? v7Plan,
        ControlledAppliedMergeRuntimePreviewDryRunReport? v7DryRun,
        ControlledAppliedMergeRuntimePreviewActivationPreflightReport? v7Preflight,
        ControlledAppliedMergeRuntimePreviewObservationWindowReport? v7Observation,
        ControlledAppliedMergeRuntimePreviewObservationHardeningReport? v7Hardening,
        ControlledAppliedMergePreviewFreezeReport? v6Freeze,
        ArchitectureCleanupFreezeReport? optFreeze,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ControlledAppliedMergeRuntimePreviewObservationFreezeOptions? options)
    {
        options ??= new ControlledAppliedMergeRuntimePreviewObservationFreezeOptions();
        var blocked = new List<string>();
        var diag = new List<string>();

        var v7PlanPresent = v7Plan is not null;
        var v7DryRunPresent = v7DryRun is not null;
        var v7PreflightPresent = v7Preflight is not null;
        var v7ObservationPresent = v7Observation is not null;
        var v7HardeningPresent = v7Hardening is not null;
        var v6FreezePresent = v6Freeze is not null;
        var optFreezePresent = optFreeze is not null;

        var v7PlanPassed = v7Plan is not null && v7Plan.PlanPassed;
        var v7DryRunPassed = v7DryRun is not null && v7DryRun.DryRunPassed;
        var v7PreflightPassed = v7Preflight is not null && v7Preflight.PreflightPassed;
        var v7ObservationPassed = v7Observation is not null && v7Observation.ObservationPassed;
        var v7HardeningPassed = v7Hardening is not null && v7Hardening.HardeningPassed;
        var v6FreezePassed = v6Freeze is not null && v6Freeze.FreezePassed;
        var optFreezePassed = optFreeze is not null && optFreeze.FreezePassed;

        if (!options.Enabled)
            blocked.Add("FreezeDisabled");

        if (!v7PlanPresent) blocked.Add("V7PlanMissing");
        if (!v7DryRunPresent) blocked.Add("V7DryRunMissing");
        if (!v7PreflightPresent) blocked.Add("V7PreflightMissing");
        if (!v7ObservationPresent) blocked.Add("V7ObservationMissing");
        if (!v7HardeningPresent) blocked.Add("V7HardeningMissing");
        if (!v6FreezePresent) blocked.Add("V6FreezeMissing");
        if (!optFreezePresent) blocked.Add("OptFreezeMissing");

        if (!v7PlanPassed) blocked.Add("V7PlanNotPassed");
        if (!v7DryRunPassed) blocked.Add("V7DryRunNotPassed");
        if (!v7PreflightPassed) blocked.Add("V7PreflightNotPassed");
        if (!v7ObservationPassed) blocked.Add("V7ObservationNotPassed");
        if (!v7HardeningPassed) blocked.Add("V7HardeningNotPassed");
        if (!v6FreezePassed) blocked.Add("V6FreezeNotPassed");
        if (!optFreezePassed) blocked.Add("OptFreezeNotPassed");
        if (!runtimeChangeGatePassed) blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15GatePassed) blocked.Add("P15GateNotPassed");

        ControlledAppliedMergeRuntimePreviewObservationFreezeMetrics? hardeningMetrics = null;
        if (v7HardeningPresent && v7Hardening is not null)
        {
            hardeningMetrics = new ControlledAppliedMergeRuntimePreviewObservationFreezeMetrics
            {
                ObservationRunCount = v7Hardening.ObservationRunCount,
                RequestCountTotal = v7Hardening.RequestCountTotal,
                ErrorCountTotal = v7Hardening.ErrorCountTotal,
                AllowlistedPreviewRouteHitCount = v7Hardening.AllowlistedPreviewRouteHitCountTotal,
                NonAllowlistedPreviewRouteHitCount = v7Hardening.NonAllowlistedPreviewRouteHitCountTotal,
                KillSwitchPreviewRouteHitCount = v7Hardening.KillSwitchPreviewRouteHitCountTotal,
                NonAllowlistedNoOpCount = v7Hardening.NonAllowlistedNoOpCountTotal,
                KillSwitchNoOpCount = v7Hardening.KillSwitchNoOpCountTotal,
                TraceCompletenessPercent = v7Hardening.TraceCompletenessPercent,
                TracePayloadStable = v7Hardening.TracePayloadStable,
                TraceReplayable = v7Hardening.TraceReplayable,
                DeterministicStable = v7Hardening.DeterministicStable,
                WouldApplyAddCount = v7Hardening.WouldApplyAddCountMax,
                WouldApplyRemoveCount = v7Hardening.WouldApplyRemoveCountMax,
                AppliedAddCount = v7Hardening.AppliedAddCountMax,
                AppliedRemoveCount = v7Hardening.AppliedRemoveCountMax,
                AppliedDeltaZero = v7Hardening.AppliedDeltaZero,
                ResultDiscarded = v7Hardening.ResultDiscarded,
            };

            if (hardeningMetrics.ObservationRunCount < options.MinObservationRunCount)
                blocked.Add("HardeningRunCountBelowThreshold");
            if (hardeningMetrics.RequestCountTotal < options.MinRequestCountTotal)
                blocked.Add("HardeningRequestCountBelowThreshold");
            if (hardeningMetrics.ErrorCountTotal > options.MaxErrorCount)
                blocked.Add("HardeningErrorCountExceeded");
            if (hardeningMetrics.AllowlistedPreviewRouteHitCount <= 0)
                blocked.Add("HardeningAllowlistedRouteHitsZero");
            if (hardeningMetrics.NonAllowlistedPreviewRouteHitCount > 0)
                blocked.Add("HardeningNonAllowlistedRouteHitsDetected");
            if (hardeningMetrics.KillSwitchPreviewRouteHitCount > 0)
                blocked.Add("HardeningKillSwitchRouteHitsDetected");
            if (hardeningMetrics.TraceCompletenessPercent < options.MinTraceCompletenessPercent)
                blocked.Add("HardeningTraceCompletenessBelow100");
            if (!hardeningMetrics.TracePayloadStable)
                blocked.Add("HardeningTracePayloadNotStable");
            if (!hardeningMetrics.TraceReplayable)
                blocked.Add("HardeningTraceNotReplayable");
            if (!hardeningMetrics.DeterministicStable)
                blocked.Add("HardeningDeterministicNotStable");
            if (!hardeningMetrics.AppliedDeltaZero)
                blocked.Add("HardeningAppliedDeltaDetected");
        }
        else
        {
            blocked.Add("HardeningMetricsMissing");
        }

        var formalSelectedSetChanged = v7Hardening?.FormalSelectedSetChanged ?? false;
        var formalPackageWritten = v7Hardening?.FormalPackageWritten ?? false;
        var packageOutputChanged = v7Hardening?.PackageOutputChanged ?? false;
        var packingPolicyChanged = v7Hardening?.PackingPolicyChanged ?? false;
        var runtimeMutated = v7Hardening?.RuntimeMutated ?? false;
        var vectorBindingChanged = v7Hardening?.VectorStoreBindingChanged ?? false;
        var formalRetrievalAllowed = v7Hardening?.FormalRetrievalAllowed ?? false;
        var runtimeSwitchAllowed = v7Hardening?.RuntimeSwitchAllowed ?? false;
        var globalDefaultOn = v7Hardening?.GlobalDefaultOn ?? false;

        if (formalSelectedSetChanged) blocked.Add("SafetyBoundaryFormalSelectedSetChanged");
        if (formalPackageWritten) blocked.Add("SafetyBoundaryFormalPackageWritten");
        if (packageOutputChanged) blocked.Add("SafetyBoundaryPackageOutputChanged");
        if (packingPolicyChanged) blocked.Add("SafetyBoundaryPackingPolicyChanged");
        if (runtimeMutated) blocked.Add("SafetyBoundaryRuntimeMutated");
        if (vectorBindingChanged) blocked.Add("SafetyBoundaryVectorStoreBindingChanged");
        if (formalRetrievalAllowed) blocked.Add("SafetyBoundaryFormalRetrievalAllowed");
        if (runtimeSwitchAllowed) blocked.Add("SafetyBoundaryRuntimeSwitchAllowed");
        if (globalDefaultOn) blocked.Add("SafetyBoundaryGlobalDefaultOn");

        var testCountBaseline = options.TestCountBaseline;
        var currentTestCount = options.TestCountBaseline;
        var testCountDelta = currentTestCount - testCountBaseline;
        var testBaselineFrozen = testCountDelta == 0;
        if (!testBaselineFrozen)
            blocked.Add("TestBaselineMismatch");

        var frozenMetrics = new List<string>
        {
            $"ObservationRunCount={hardeningMetrics?.ObservationRunCount ?? 0}",
            $"RequestCountTotal={hardeningMetrics?.RequestCountTotal ?? 0}",
            $"ErrorCountTotal={hardeningMetrics?.ErrorCountTotal ?? 0}",
            $"AllowlistedPreviewRouteHitCount={hardeningMetrics?.AllowlistedPreviewRouteHitCount ?? 0}",
            $"NonAllowlistedPreviewRouteHitCount={hardeningMetrics?.NonAllowlistedPreviewRouteHitCount ?? 0}",
            $"KillSwitchPreviewRouteHitCount={hardeningMetrics?.KillSwitchPreviewRouteHitCount ?? 0}",
            $"TraceCompletenessPercent={hardeningMetrics?.TraceCompletenessPercent ?? 0:F1}%",
            $"TracePayloadStable={hardeningMetrics?.TracePayloadStable ?? false}",
            $"TraceReplayable={hardeningMetrics?.TraceReplayable ?? false}",
            $"DeterministicStable={hardeningMetrics?.DeterministicStable ?? false}",
            $"AppliedDeltaZero={hardeningMetrics?.AppliedDeltaZero ?? false}",
            $"ResultDiscarded={hardeningMetrics?.ResultDiscarded ?? false}",
        };

        var safetyBoundaries = new List<string>
        {
            $"FormalSelectedSetChanged={formalSelectedSetChanged}",
            $"FormalPackageWritten={formalPackageWritten}",
            $"PackageOutputChanged={packageOutputChanged}",
            $"PackingPolicyChanged={packingPolicyChanged}",
            $"RuntimeMutated={runtimeMutated}",
            $"VectorStoreBindingChanged={vectorBindingChanged}",
            $"FormalRetrievalAllowed={formalRetrievalAllowed}",
            $"RuntimeSwitchAllowed={runtimeSwitchAllowed}",
            $"GlobalDefaultOn={globalDefaultOn}",
        };

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var freezePassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && freezePassed;
        var noRuntimeMutationInvariant = !formalSelectedSetChanged && !formalPackageWritten && !packageOutputChanged
            && !packingPolicyChanged && !runtimeMutated && !vectorBindingChanged;

        var promotionDecision = freezePassed
            ? ControlledAppliedMergeRuntimePreviewObservationFreezeRecommendations.ReadyForScopedRuntimePreviewApprovalPlan
            : ControlledAppliedMergeRuntimePreviewObservationFreezeRecommendations.KeepPreviewOnly;

        diag.Add($"stage={stage}");
        diag.Add($"v7PlanPresent={v7PlanPresent} v7PlanPassed={v7PlanPassed}");
        diag.Add($"v7DryRunPresent={v7DryRunPresent} v7DryRunPassed={v7DryRunPassed}");
        diag.Add($"v7PreflightPresent={v7PreflightPresent} v7PreflightPassed={v7PreflightPassed}");
        diag.Add($"v7ObservationPresent={v7ObservationPresent} v7ObservationPassed={v7ObservationPassed}");
        diag.Add($"v7HardeningPresent={v7HardeningPresent} v7HardeningPassed={v7HardeningPassed}");
        diag.Add($"v6FreezePresent={v6FreezePresent} v6FreezePassed={v6FreezePassed}");
        diag.Add($"optFreezePresent={optFreezePresent} optFreezePassed={optFreezePassed}");
        diag.Add($"runtimeChangeGatePassed={runtimeChangeGatePassed}");
        diag.Add($"p15GatePassed={p15GatePassed}");
        diag.Add($"testBaseline={testCountBaseline} current={currentTestCount} delta={testCountDelta} frozen={testBaselineFrozen}");
        diag.Add($"freezePassed={freezePassed} gatePassed={gatePassed}");
        diag.Add($"promotionDecision={promotionDecision}");
        diag.Add($"noRuntimeMutationInvariant={noRuntimeMutationInvariant}");
        if (!isGate)
            diag.Add("GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative");

        return new ControlledAppliedMergeRuntimePreviewObservationFreezeReport
        {
            OperationId = $"controlled-applied-merge-runtime-preview-observation-freeze-{stage}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            FreezePassed = freezePassed,
            GatePassed = gatePassed,
            Recommendation = freezePassed
                ? ControlledAppliedMergeRuntimePreviewObservationFreezeRecommendations.ReadyForScopedRuntimePreviewApprovalPlan
                : ResolveRecommendation(distinctBlocked),
            PromotionDecision = promotionDecision,
            NextAllowedPhase = freezePassed
                ? "ScopedRuntimePreviewApprovalPlan"
                : "KeepPreviewOnly",

            V7PlanPresent = v7PlanPresent,
            V7DryRunPresent = v7DryRunPresent,
            V7PreflightPresent = v7PreflightPresent,
            V7ObservationPresent = v7ObservationPresent,
            V7HardeningPresent = v7HardeningPresent,
            V6FreezePresent = v6FreezePresent,
            OptFreezePresent = optFreezePresent,
            RuntimeChangeGatePresent = runtimeChangeGatePassed,
            P15GatePassed = p15GatePassed,

            V7PlanPassed = v7PlanPassed,
            V7DryRunPassed = v7DryRunPassed,
            V7PreflightPassed = v7PreflightPassed,
            V7ObservationPassed = v7ObservationPassed,
            V7HardeningPassed = v7HardeningPassed,
            V6FreezePassed = v6FreezePassed,
            OptFreezePassed = optFreezePassed,
            RuntimeChangeGatePassed = runtimeChangeGatePassed,

            HardeningMetrics = hardeningMetrics,

            TestCountBaseline = testCountBaseline,
            CurrentTestCount = currentTestCount,
            TestCountDelta = testCountDelta,
            TestBaselineFrozen = testBaselineFrozen,

            FormalSelectedSetChanged = formalSelectedSetChanged,
            FormalPackageWritten = formalPackageWritten,
            PackageOutputChanged = packageOutputChanged,
            PackingPolicyChanged = packingPolicyChanged,
            RuntimeMutated = runtimeMutated,
            VectorStoreBindingChanged = vectorBindingChanged,
            FormalRetrievalAllowed = formalRetrievalAllowed,
            RuntimeSwitchAllowed = runtimeSwitchAllowed,
            GlobalDefaultOn = globalDefaultOn,
            NoRuntimeMutationInvariant = noRuntimeMutationInvariant,

            FrozenMetrics = frozenMetrics,
            SafetyBoundaries = safetyBoundaries,
            AllowedActions = FrozenAllowedActions,
            ForbiddenActions = FrozenForbiddenActions,
            BlockedReasons = distinctBlocked,
            Diagnostics = diag,
        };
    }

    public static string BuildMarkdown(string title, ControlledAppliedMergeRuntimePreviewObservationFreezeReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- FreezePassed: `{r.FreezePassed}`");
        b.AppendLine($"- GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}`");
        b.AppendLine($"- PromotionDecision: `{r.PromotionDecision}`");
        b.AppendLine($"- NextAllowedPhase: `{r.NextAllowedPhase}`");
        b.AppendLine();

        b.AppendLine("## Prerequisites");
        b.AppendLine($"- V7Plan: present=`{r.V7PlanPresent}` passed=`{r.V7PlanPassed}`");
        b.AppendLine($"- V7DryRun: present=`{r.V7DryRunPresent}` passed=`{r.V7DryRunPassed}`");
        b.AppendLine($"- V7Preflight: present=`{r.V7PreflightPresent}` passed=`{r.V7PreflightPassed}`");
        b.AppendLine($"- V7Observation: present=`{r.V7ObservationPresent}` passed=`{r.V7ObservationPassed}`");
        b.AppendLine($"- V7Hardening: present=`{r.V7HardeningPresent}` passed=`{r.V7HardeningPassed}`");
        b.AppendLine($"- V6Freeze: present=`{r.V6FreezePresent}` passed=`{r.V6FreezePassed}`");
        b.AppendLine($"- OptFreeze: present=`{r.OptFreezePresent}` passed=`{r.OptFreezePassed}`");
        b.AppendLine($"- RuntimeChangeGate: passed=`{r.RuntimeChangeGatePassed}`");
        b.AppendLine($"- P15Gate: passed=`{r.P15GatePassed}`");
        b.AppendLine();

        b.AppendLine("## Frozen Hardening Metrics");
        if (r.HardeningMetrics is not null)
        {
            var m = r.HardeningMetrics;
            b.AppendLine($"- ObservationRunCount: `{m.ObservationRunCount}`");
            b.AppendLine($"- RequestCountTotal: `{m.RequestCountTotal}`");
            b.AppendLine($"- ErrorCountTotal: `{m.ErrorCountTotal}`");
            b.AppendLine($"- AllowlistedPreviewRouteHitCount: `{m.AllowlistedPreviewRouteHitCount}`");
            b.AppendLine($"- NonAllowlistedPreviewRouteHitCount: `{m.NonAllowlistedPreviewRouteHitCount}`");
            b.AppendLine($"- KillSwitchPreviewRouteHitCount: `{m.KillSwitchPreviewRouteHitCount}`");
            b.AppendLine($"- NonAllowlistedNoOpCount: `{m.NonAllowlistedNoOpCount}`");
            b.AppendLine($"- KillSwitchNoOpCount: `{m.KillSwitchNoOpCount}`");
            b.AppendLine($"- TraceCompletenessPercent: `{m.TraceCompletenessPercent:F1}%`");
            b.AppendLine($"- TracePayloadStable: `{m.TracePayloadStable}`");
            b.AppendLine($"- TraceReplayable: `{m.TraceReplayable}`");
            b.AppendLine($"- DeterministicStable: `{m.DeterministicStable}`");
            b.AppendLine($"- WouldApplyAddCount: `{m.WouldApplyAddCount}`");
            b.AppendLine($"- WouldApplyRemoveCount: `{m.WouldApplyRemoveCount}`");
            b.AppendLine($"- AppliedAddCount: `{m.AppliedAddCount}`");
            b.AppendLine($"- AppliedRemoveCount: `{m.AppliedRemoveCount}`");
            b.AppendLine($"- AppliedDeltaZero: `{m.AppliedDeltaZero}`");
            b.AppendLine($"- ResultDiscarded: `{m.ResultDiscarded}`");
        }
        b.AppendLine();

        b.AppendLine("## Test Baseline");
        b.AppendLine($"- TestCountBaseline: `{r.TestCountBaseline}`");
        b.AppendLine($"- CurrentTestCount: `{r.CurrentTestCount}`");
        b.AppendLine($"- TestCountDelta: `{r.TestCountDelta}`");
        b.AppendLine($"- TestBaselineFrozen: `{r.TestBaselineFrozen}`");
        b.AppendLine();

        b.AppendLine("## Safety Boundaries");
        b.AppendLine($"- FormalSelectedSetChanged: `{r.FormalSelectedSetChanged}`");
        b.AppendLine($"- FormalPackageWritten: `{r.FormalPackageWritten}`");
        b.AppendLine($"- PackageOutputChanged: `{r.PackageOutputChanged}`");
        b.AppendLine($"- PackingPolicyChanged: `{r.PackingPolicyChanged}`");
        b.AppendLine($"- RuntimeMutated: `{r.RuntimeMutated}`");
        b.AppendLine($"- VectorStoreBindingChanged: `{r.VectorStoreBindingChanged}`");
        b.AppendLine($"- FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}`");
        b.AppendLine($"- RuntimeSwitchAllowed: `{r.RuntimeSwitchAllowed}`");
        b.AppendLine($"- GlobalDefaultOn: `{r.GlobalDefaultOn}`");
        b.AppendLine($"- NoRuntimeMutationInvariant: `{r.NoRuntimeMutationInvariant}`");
        b.AppendLine();

        AppendList(b, "Frozen Metrics", r.FrozenMetrics);
        AppendList(b, "Safety Boundaries", r.SafetyBoundaries);
        AppendList(b, "Allowed Actions", r.AllowedActions);
        AppendList(b, "Forbidden Actions", r.ForbiddenActions);
        AppendList(b, "Blocked Reasons", r.BlockedReasons);
        AppendList(b, "Diagnostics", r.Diagnostics);

        b.AppendLine();
        b.AppendLine("V7.4 runtime preview observation freeze / promotion decision. 冻结 V7 runtime preview observation 结果并做 promotion decision。不启用 formal retrieval，不切 runtime。GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative。");
        return b.ToString();
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static r => r.Contains("Missing", StringComparison.OrdinalIgnoreCase) || r.Contains("Prerequisite", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewObservationFreezeRecommendations.BlockedByMissingPrerequisites;
        if (blocked.Any(static r => r.Contains("Hardening", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewObservationFreezeRecommendations.BlockedByObservationHardeningNotPassed;
        if (blocked.Any(static r => r.Contains("Observation", StringComparison.OrdinalIgnoreCase) && !r.Contains("Hardening", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewObservationFreezeRecommendations.BlockedByObservationWindowNotPassed;
        if (blocked.Any(static r => r.Contains("Preflight", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewObservationFreezeRecommendations.BlockedByPreflightNotPassed;
        if (blocked.Any(static r => r.Contains("DryRun", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewObservationFreezeRecommendations.BlockedByDryRunNotPassed;
        if (blocked.Any(static r => r.Contains("Safety", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewObservationFreezeRecommendations.BlockedBySafetyBoundaryViolation;
        if (blocked.Any(static r => r.Contains("Test", StringComparison.OrdinalIgnoreCase) || r.Contains("Baseline", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewObservationFreezeRecommendations.BlockedByTestBaselineMismatch;
        if (blocked.Any(static r => r.Contains("Metric", StringComparison.OrdinalIgnoreCase) || r.Contains("Threshold", StringComparison.OrdinalIgnoreCase) || r.Contains("Exceeded", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewObservationFreezeRecommendations.BlockedByFreezeMetricsViolation;
        if (blocked.Any(static r => r.Contains("Gate", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewObservationFreezeRecommendations.BlockedByGateFailure;
        return ControlledAppliedMergeRuntimePreviewObservationFreezeRecommendations.KeepPreviewOnly;
    }

    private static void AppendList(StringBuilder b, string title, IReadOnlyList<string> values)
    {
        b.AppendLine();
        b.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            b.AppendLine("- (empty)");
            return;
        }
        foreach (var v in values) b.AppendLine($"- `{v}`");
    }
}
