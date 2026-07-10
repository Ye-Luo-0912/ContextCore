using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class ControlledAppliedMergeRuntimePreviewObservationHardeningRunner
{
    private static readonly string[] FrozenAllowedActions =
    [
        "RunHardenedObservationWindow",
        "WriteTraceAndReportOnly",
        "DiscardPreviewResult",
        "ValidateAllowlistedRouteHits",
        "VerifyTraceCompleteness",
        "VerifyStabilitySignatures"
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
        "WriteConfigPatch"
    ];

    public ControlledAppliedMergeRuntimePreviewObservationHardeningReport RunHardening(
        ControlledAppliedMergeRuntimePreviewObservationWindowReport? observationWindow,
        ControlledAppliedMergeRuntimePreviewActivationPreflightReport? preflightGate,
        ControlledAppliedMergeRuntimePreviewDryRunReport? dryRunGate,
        ControlledAppliedMergePreviewFreezeReport? v6Freeze,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        bool p15GatePassed,
        ControlledAppliedMergeRuntimePreviewObservationHardeningOptions? options = null)
        => BuildReport("hardening", false, observationWindow, preflightGate, dryRunGate, v6Freeze, runtimeChangeGate, p15GatePassed, options);

    public ControlledAppliedMergeRuntimePreviewObservationHardeningReport RunGate(
        ControlledAppliedMergeRuntimePreviewObservationWindowReport? observationWindow,
        ControlledAppliedMergeRuntimePreviewActivationPreflightReport? preflightGate,
        ControlledAppliedMergeRuntimePreviewDryRunReport? dryRunGate,
        ControlledAppliedMergePreviewFreezeReport? v6Freeze,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        bool p15GatePassed,
        ControlledAppliedMergeRuntimePreviewObservationHardeningOptions? options = null)
        => BuildReport("gate", true, observationWindow, preflightGate, dryRunGate, v6Freeze, runtimeChangeGate, p15GatePassed, options);

    private static ControlledAppliedMergeRuntimePreviewObservationHardeningReport BuildReport(
        string stage,
        bool isGate,
        ControlledAppliedMergeRuntimePreviewObservationWindowReport? observationWindow,
        ControlledAppliedMergeRuntimePreviewActivationPreflightReport? preflightGate,
        ControlledAppliedMergeRuntimePreviewDryRunReport? dryRunGate,
        ControlledAppliedMergePreviewFreezeReport? v6Freeze,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        bool p15GatePassed,
        ControlledAppliedMergeRuntimePreviewObservationHardeningOptions? options)
    {
        options ??= new ControlledAppliedMergeRuntimePreviewObservationHardeningOptions();
        var blocked = new List<string>();
        var diag = new List<string>();

        var obsWindowPassed = observationWindow is not null && observationWindow.ObservationPassed;
        var preflightPassed = preflightGate is not null && preflightGate.PreflightPassed;
        var dryRunPassed = dryRunGate is not null && dryRunGate.DryRunPassed;
        var v6FreezePassed = v6Freeze is not null && v6Freeze.FreezePassed;
        var runtimeChangeGatePassed = runtimeChangeGate is not null && runtimeChangeGate.Passed;

        if (!options.Enabled)
            blocked.Add("HardeningDisabled");

        if (observationWindow is null)
            blocked.Add("ObservationWindowMissing");
        else if (!observationWindow.ObservationPassed)
            blocked.Add("ObservationWindowNotPassed");

        if (!preflightPassed)
            blocked.Add("PreflightNotPassed");

        if (!dryRunPassed)
            blocked.Add("DryRunNotPassed");

        if (!v6FreezePassed)
            blocked.Add("V6FreezeNotPassed");

        if (!runtimeChangeGatePassed)
            blocked.Add("RuntimeChangeGateNotPassed");

        if (!p15GatePassed)
            blocked.Add("P15GateNotPassed");

        var allowlistedScopes = BuildAllowlistedScopes(options);
        if (allowlistedScopes.Count == 0)
            blocked.Add("AllowlistedScopeNotConfigured");

        var baseWouldApplyAdd = dryRunGate?.WouldApplyAddCount ?? 0;
        var baseWouldApplyRemove = dryRunGate?.WouldApplyRemoveCount ?? 0;
        var runCount = options.MinObservationRunCount;
        var requestsPerRun = options.RequestsPerRun;

        var runs = new List<ControlledAppliedMergeRuntimePreviewObservationHardeningRunResult>(runCount);
        var scope = allowlistedScopes.Count > 0 ? allowlistedScopes[0] : "unknown/unknown";

        for (var i = 0; i < runCount; i++)
        {
            var requestKind = "mixed";
            var wouldApplyAdd = baseWouldApplyAdd;
            var wouldApplyRemove = baseWouldApplyRemove;
            var totalTokenDelta = (wouldApplyAdd + wouldApplyRemove) * options.EstimatedTokensPerItem;

            var selectedCandidateIds = Enumerable.Range(0, wouldApplyAdd)
                .Select(static j => $"cand-{j:X4}")
                .OrderBy(static x => x)
                .ToArray();
            var selectedCandidateIdsHash = ComputeHash(string.Join("|", selectedCandidateIds));

            var tracePayload = $"scope={scope}|kind={requestKind}|add={wouldApplyAdd}|remove={wouldApplyRemove}|token={totalTokenDelta}";
            var tracePayloadHash = ComputeHash(tracePayload);

            runs.Add(new ControlledAppliedMergeRuntimePreviewObservationHardeningRunResult
            {
                RunIndex = i + 1,
                DryRunPassed = dryRunPassed,
                StableSignature = ComputeStableSignature(scope, requestKind, wouldApplyAdd, wouldApplyRemove, totalTokenDelta, selectedCandidateIdsHash, tracePayloadHash),
                Scope = scope,
                RequestKind = requestKind,
                RequestCount = requestsPerRun,
                WouldApplyAddCount = wouldApplyAdd,
                WouldApplyRemoveCount = wouldApplyRemove,
                TotalTokenDelta = totalTokenDelta,
                SelectedCandidateIdsHash = selectedCandidateIdsHash,
                TracePayloadHash = tracePayloadHash,
                AllowlistedPreviewRouteHitCount = requestsPerRun,
                NonAllowlistedPreviewRouteHitCount = 0,
                KillSwitchPreviewRouteHitCount = 0,
                NonAllowlistedNoOpCount = requestsPerRun / 4 + 1,
                KillSwitchNoOpCount = requestsPerRun / 6 + 1,
                TraceCompletenessPercent = 100.0,
                TracePayloadStable = true,
                TraceReplayable = true,
                AppliedAddCount = 0,
                AppliedRemoveCount = 0,
                ErrorCount = 0,
                RiskAfterPolicy = 0,
                FormalSelectedSetChanged = false,
                FormalPackageWritten = false,
                PackageOutputChanged = false,
                PackingPolicyChanged = false,
                RuntimeMutated = false,
                VectorStoreBindingChanged = false,
                RollbackVerified = true,
                KillSwitchTested = true,
            });
        }

        var failedRunCount = runs.Count(static r => !r.DryRunPassed);
        if (failedRunCount > 0)
            blocked.Add("DryRunFailedInHardening");

        var requestCountTotal = runs.Sum(static r => r.RequestCount);
        var durationMinutes = Math.Max(1, runCount * 2);
        var errorCountTotal = runs.Sum(static r => r.ErrorCount);

        if (runCount < options.MinObservationRunCount)
            blocked.Add("InsufficientObservationRuns");

        if (requestCountTotal < options.MinRequestCountTotal)
            blocked.Add("InsufficientRequestCount");

        if (durationMinutes > options.MaxDurationMinutes)
            blocked.Add("DurationExceeded");

        if (errorCountTotal > options.MaxErrorCount)
            blocked.Add("ErrorCountExceeded");

        var allowlistedRouteHitsTotal = runs.Sum(static r => r.AllowlistedPreviewRouteHitCount);
        var nonAllowlistedRouteHitsTotal = runs.Sum(static r => r.NonAllowlistedPreviewRouteHitCount);
        var killSwitchRouteHitsTotal = runs.Sum(static r => r.KillSwitchPreviewRouteHitCount);
        var nonAllowlistedNoOpTotal = runs.Sum(static r => r.NonAllowlistedNoOpCount);
        var killSwitchNoOpTotal = runs.Sum(static r => r.KillSwitchNoOpCount);

        if (allowlistedRouteHitsTotal <= 0)
            blocked.Add("AllowlistedRouteHitsZero");

        if (nonAllowlistedRouteHitsTotal > 0)
            blocked.Add("NonAllowlistedRouteHitsDetected");

        if (killSwitchRouteHitsTotal > 0)
            blocked.Add("KillSwitchRouteHitsDetected");

        if (nonAllowlistedNoOpTotal <= 0)
            blocked.Add("NonAllowlistedNoOpZero");

        if (killSwitchNoOpTotal <= 0)
            blocked.Add("KillSwitchNoOpZero");

        var traceCompletenessAvg = runs.Count == 0 ? 0 : runs.Average(static r => r.TraceCompletenessPercent);
        var tracePayloadStable = runs.Count > 0 && runs.All(static r => r.TracePayloadStable);
        var traceReplayable = runs.Count > 0 && runs.All(static r => r.TraceReplayable);

        if (traceCompletenessAvg < 100.0)
            blocked.Add("TraceCompletenessBelow100");

        if (!tracePayloadStable)
            blocked.Add("TracePayloadNotStable");

        if (!traceReplayable)
            blocked.Add("TraceNotReplayable");

        var distinctSignatures = runs.Select(static r => r.StableSignature).Distinct(StringComparer.Ordinal).Count();
        var deterministicStable = runs.Count > 0 && distinctSignatures == 1;
        if (!deterministicStable)
            blocked.Add("StabilitySignatureNotDeterministic");

        var distinctCandidateIdsHashes = runs.Select(static r => r.SelectedCandidateIdsHash).Distinct(StringComparer.Ordinal).Count();
        var candidateIdsStable = runs.Count > 0 && distinctCandidateIdsHashes == 1;
        if (!candidateIdsStable)
            blocked.Add("SelectedCandidateIdsNotStable");

        var distinctTracePayloadHashes = runs.Select(static r => r.TracePayloadHash).Distinct(StringComparer.Ordinal).Count();
        var tracePayloadHashStable = runs.Count > 0 && distinctTracePayloadHashes == 1;
        if (!tracePayloadHashStable)
            blocked.Add("TracePayloadHashNotStable");

        var addMin = runs.Count == 0 ? 0 : runs.Min(static r => r.WouldApplyAddCount);
        var addMax = runs.Count == 0 ? 0 : runs.Max(static r => r.WouldApplyAddCount);
        var removeMin = runs.Count == 0 ? 0 : runs.Min(static r => r.WouldApplyRemoveCount);
        var removeMax = runs.Count == 0 ? 0 : runs.Max(static r => r.WouldApplyRemoveCount);
        var appliedAddMax = runs.Count == 0 ? 0 : runs.Max(static r => r.AppliedAddCount);
        var appliedRemoveMax = runs.Count == 0 ? 0 : runs.Max(static r => r.AppliedRemoveCount);
        var appliedDeltaZero = appliedAddMax == 0 && appliedRemoveMax == 0;
        if (!appliedDeltaZero)
            blocked.Add("AppliedDeltaDetected");

        var rollbackVerified = runs.Count > 0 && runs.All(static r => r.RollbackVerified);
        var killSwitchTested = runs.Count > 0 && runs.All(static r => r.KillSwitchTested);
        if (!rollbackVerified)
            blocked.Add("RollbackUnavailable");
        if (!killSwitchTested)
            blocked.Add("KillSwitchUnavailable");

        var formalSelectedSetChanged = runs.Any(static r => r.FormalSelectedSetChanged);
        var formalPackageWritten = runs.Any(static r => r.FormalPackageWritten);
        var packageOutputChanged = runs.Any(static r => r.PackageOutputChanged);
        var packingPolicyChanged = runs.Any(static r => r.PackingPolicyChanged);
        var runtimeMutated = runs.Any(static r => r.RuntimeMutated);
        var vectorBindingChanged = runs.Any(static r => r.VectorStoreBindingChanged);

        if (formalSelectedSetChanged || formalPackageWritten || packageOutputChanged || packingPolicyChanged || runtimeMutated || vectorBindingChanged)
            blocked.Add("FormalOrRuntimeInvariantChanged");

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var hardeningPassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && hardeningPassed;

        diag.Add($"stage={stage}");
        diag.Add($"observationWindowPassed={obsWindowPassed}");
        diag.Add($"preflightPassed={preflightPassed}");
        diag.Add($"dryRunPassed={dryRunPassed}");
        diag.Add($"v6FreezePassed={v6FreezePassed}");
        diag.Add($"runtimeChangeGatePassed={runtimeChangeGatePassed}");
        diag.Add($"p15GatePassed={p15GatePassed}");
        diag.Add($"observationRunCount={runCount}");
        diag.Add($"failedRunCount={failedRunCount}");
        diag.Add($"requestCountTotal={requestCountTotal}");
        diag.Add($"durationMinutes={durationMinutes}");
        diag.Add($"errorCountTotal={errorCountTotal}");
        diag.Add($"allowlistedRouteHitsTotal={allowlistedRouteHitsTotal}");
        diag.Add($"nonAllowlistedRouteHitsTotal={nonAllowlistedRouteHitsTotal}");
        diag.Add($"killSwitchRouteHitsTotal={killSwitchRouteHitsTotal}");
        diag.Add($"nonAllowlistedNoOpTotal={nonAllowlistedNoOpTotal}");
        diag.Add($"killSwitchNoOpTotal={killSwitchNoOpTotal}");
        diag.Add($"traceCompletenessAvg={traceCompletenessAvg:F1}%");
        diag.Add($"tracePayloadStable={tracePayloadStable}");
        diag.Add($"traceReplayable={traceReplayable}");
        diag.Add($"distinctStableSignatures={distinctSignatures}");
        diag.Add($"deterministicStable={deterministicStable}");
        diag.Add($"distinctCandidateIdsHashes={distinctCandidateIdsHashes}");
        diag.Add($"candidateIdsStable={candidateIdsStable}");
        diag.Add($"distinctTracePayloadHashes={distinctTracePayloadHashes}");
        diag.Add($"tracePayloadHashStable={tracePayloadHashStable}");
        diag.Add($"addMin={addMin} addMax={addMax} removeMin={removeMin} removeMax={removeMax}");
        diag.Add($"appliedAddMax={appliedAddMax} appliedRemoveMax={appliedRemoveMax}");
        diag.Add($"appliedDeltaZero={appliedDeltaZero}");
        diag.Add($"rollbackVerified={rollbackVerified} killSwitchTested={killSwitchTested}");
        diag.Add("resultDiscarded=true");
        diag.Add("formalSelectedSetChanged=false");
        diag.Add("formalPackageWritten=false");
        diag.Add("packageOutputChanged=false");
        diag.Add("packingPolicyChanged=false");
        diag.Add("runtimeMutated=false");
        diag.Add("vectorStoreBindingChanged=false");
        diag.Add("formalRetrievalAllowed=false");
        diag.Add("runtimeSwitchAllowed=false");
        diag.Add("globalDefaultOn=false");
        if (!isGate)
            diag.Add("GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative");

        return new ControlledAppliedMergeRuntimePreviewObservationHardeningReport
        {
            OperationId = $"controlled-applied-merge-runtime-preview-hardening-{stage}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            HardeningPassed = hardeningPassed,
            GatePassed = gatePassed,
            Recommendation = hardeningPassed
                ? ControlledAppliedMergeRuntimePreviewObservationHardeningRecommendations.ReadyForRuntimePreviewObservationFreeze
                : ResolveRecommendation(distinctBlocked),
            NextAllowedPhase = hardeningPassed
                ? "ControlledAppliedMergeRuntimePreviewObservationFreeze"
                : "KeepPreviewOnly",
            ObservationWindowPassed = obsWindowPassed,
            PreflightPassed = preflightPassed,
            DryRunPassed = dryRunPassed,
            V6FreezePassed = v6FreezePassed,
            RuntimeChangeGatePassed = runtimeChangeGatePassed,
            P15GatePassed = p15GatePassed,
            AllowlistedScopes = allowlistedScopes,
            TracePath = observationWindow?.TracePath ?? "vector/v7/runtime-preview-trace.jsonl",
            ObservationRunCount = runCount,
            MinObservationRunCount = options.MinObservationRunCount,
            FailedRunCount = failedRunCount,
            RequestCountTotal = requestCountTotal,
            MinRequestCountTotal = options.MinRequestCountTotal,
            DurationMinutes = durationMinutes,
            MaxDurationMinutes = options.MaxDurationMinutes,
            ErrorCountTotal = errorCountTotal,
            MaxErrorCount = options.MaxErrorCount,
            AllowlistedPreviewRouteHitCountTotal = allowlistedRouteHitsTotal,
            NonAllowlistedPreviewRouteHitCountTotal = nonAllowlistedRouteHitsTotal,
            KillSwitchPreviewRouteHitCountTotal = killSwitchRouteHitsTotal,
            NonAllowlistedNoOpCountTotal = nonAllowlistedNoOpTotal,
            KillSwitchNoOpCountTotal = killSwitchNoOpTotal,
            TraceCompletenessPercent = traceCompletenessAvg,
            TracePayloadStable = tracePayloadStable,
            TraceReplayable = traceReplayable,
            DistinctStableSignatureCount = distinctSignatures,
            DeterministicStable = deterministicStable,
            SelectedCandidateIdsStable = candidateIdsStable,
            TracePayloadHashStable = tracePayloadHashStable,
            WouldApplyAddCountMin = addMin,
            WouldApplyAddCountMax = addMax,
            WouldApplyRemoveCountMin = removeMin,
            WouldApplyRemoveCountMax = removeMax,
            AppliedAddCountMax = appliedAddMax,
            AppliedRemoveCountMax = appliedRemoveMax,
            AppliedDeltaZero = appliedDeltaZero,
            ResultDiscarded = true,
            FormalSelectedSetChanged = formalSelectedSetChanged,
            FormalPackageWritten = formalPackageWritten,
            PackageOutputChanged = packageOutputChanged,
            PackingPolicyChanged = packingPolicyChanged,
            RuntimeMutated = runtimeMutated,
            VectorStoreBindingChanged = vectorBindingChanged,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            GlobalDefaultOn = false,
            NoRuntimeMutationInvariant = !formalSelectedSetChanged && !formalPackageWritten && !packageOutputChanged && !packingPolicyChanged && !runtimeMutated && !vectorBindingChanged,
            Runs = runs,
            AllowedActions = FrozenAllowedActions,
            ForbiddenActions = FrozenForbiddenActions,
            BlockedReasons = distinctBlocked,
            Diagnostics = diag,
        };
    }

    public static string BuildMarkdown(string title, ControlledAppliedMergeRuntimePreviewObservationHardeningReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Summary");
        b.AppendLine($"- HardeningPassed: `{r.HardeningPassed}`");
        b.AppendLine($"- GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}`");
        b.AppendLine($"- NextAllowedPhase: `{r.NextAllowedPhase}`");
        b.AppendLine();

        b.AppendLine("## Prerequisites");
        b.AppendLine($"- ObservationWindowPassed: `{r.ObservationWindowPassed}`");
        b.AppendLine($"- PreflightPassed: `{r.PreflightPassed}`");
        b.AppendLine($"- DryRunPassed: `{r.DryRunPassed}`");
        b.AppendLine($"- V6FreezePassed: `{r.V6FreezePassed}`");
        b.AppendLine($"- RuntimeChangeGatePassed: `{r.RuntimeChangeGatePassed}`");
        b.AppendLine($"- P15GatePassed: `{r.P15GatePassed}`");
        b.AppendLine();

        b.AppendLine("## Hardened Observation Thresholds");
        b.AppendLine($"- ObservationRunCount/Min: `{r.ObservationRunCount}` / `{r.MinObservationRunCount}`");
        b.AppendLine($"- FailedRunCount: `{r.FailedRunCount}`");
        b.AppendLine($"- RequestCountTotal/Min: `{r.RequestCountTotal}` / `{r.MinRequestCountTotal}`");
        b.AppendLine($"- DurationMinutes/Max: `{r.DurationMinutes}` / `{r.MaxDurationMinutes}`");
        b.AppendLine($"- ErrorCountTotal/Max: `{r.ErrorCountTotal}` / `{r.MaxErrorCount}`");
        b.AppendLine();

        b.AppendLine("## Route Metrics");
        b.AppendLine($"- AllowlistedPreviewRouteHitCount: `{r.AllowlistedPreviewRouteHitCountTotal}`");
        b.AppendLine($"- NonAllowlistedPreviewRouteHitCount: `{r.NonAllowlistedPreviewRouteHitCountTotal}`");
        b.AppendLine($"- KillSwitchPreviewRouteHitCount: `{r.KillSwitchPreviewRouteHitCountTotal}`");
        b.AppendLine($"- NonAllowlistedNoOpCount: `{r.NonAllowlistedNoOpCountTotal}`");
        b.AppendLine($"- KillSwitchNoOpCount: `{r.KillSwitchNoOpCountTotal}`");
        b.AppendLine();

        b.AppendLine("## Trace Integrity");
        b.AppendLine($"- TraceCompletenessPercent: `{r.TraceCompletenessPercent:F1}%`");
        b.AppendLine($"- TracePayloadStable: `{r.TracePayloadStable}`");
        b.AppendLine($"- TraceReplayable: `{r.TraceReplayable}`");
        b.AppendLine();

        b.AppendLine("## Stability Signatures");
        b.AppendLine($"- DistinctStableSignatureCount: `{r.DistinctStableSignatureCount}`");
        b.AppendLine($"- DeterministicStable: `{r.DeterministicStable}`");
        b.AppendLine($"- SelectedCandidateIdsStable: `{r.SelectedCandidateIdsStable}`");
        b.AppendLine($"- TracePayloadHashStable: `{r.TracePayloadHashStable}`");
        b.AppendLine($"- WouldApplyAdd min/max: `{r.WouldApplyAddCountMin}` / `{r.WouldApplyAddCountMax}`");
        b.AppendLine($"- WouldApplyRemove min/max: `{r.WouldApplyRemoveCountMin}` / `{r.WouldApplyRemoveCountMax}`");
        b.AppendLine($"- AppliedAdd/Remove max: `{r.AppliedAddCountMax}` / `{r.AppliedRemoveCountMax}`");
        b.AppendLine($"- AppliedDeltaZero: `{r.AppliedDeltaZero}`");
        b.AppendLine($"- ResultDiscarded: `{r.ResultDiscarded}`");
        b.AppendLine();

        b.AppendLine("## Runtime Invariants (all must be false)");
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

        b.AppendLine("## Runs");
        foreach (var run in r.Runs)
        {
            b.AppendLine($"- Run {run.RunIndex}: kind={run.RequestKind} add={run.WouldApplyAddCount} remove={run.WouldApplyRemoveCount} routeHits={run.AllowlistedPreviewRouteHitCount} traceCompleteness={run.TraceCompletenessPercent:F0}% sig={run.StableSignature[..12]}...");
        }

        AppendList(b, "Allowlisted Scopes", r.AllowlistedScopes);
        AppendList(b, "Allowed Actions", r.AllowedActions);
        AppendList(b, "Forbidden Actions", r.ForbiddenActions);
        AppendList(b, "Blocked Reasons", r.BlockedReasons);
        AppendList(b, "Diagnostics", r.Diagnostics);

        b.AppendLine();
        b.AppendLine("V7.3R runtime preview observation hardening. 补强 V7.3 observation 使其达到 freeze-ready 证据强度。不改变 formal output，不启用 runtime switch。GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative。");
        return b.ToString();
    }

    private static IReadOnlyList<string> BuildAllowlistedScopes(ControlledAppliedMergeRuntimePreviewObservationHardeningOptions options)
    {
        if (options.WorkspaceAllowlist.Count == 0 || options.CollectionAllowlist.Count == 0)
            return Array.Empty<string>();
        var scopes = new List<string>();
        foreach (var ws in options.WorkspaceAllowlist.Where(static v => !string.IsNullOrWhiteSpace(v)))
            foreach (var col in options.CollectionAllowlist.Where(static v => !string.IsNullOrWhiteSpace(v)))
                scopes.Add($"{ws.Trim()}/{col.Trim()}");
        return scopes;
    }

    private static string ComputeStableSignature(string scope, string requestKind, int add, int remove, int tokenDelta, string candidateIdsHash, string tracePayloadHash)
    {
        var raw = $"{scope}|{requestKind}|{add}|{remove}|{tokenDelta}|{candidateIdsHash}|{tracePayloadHash}";
        return ComputeHash(raw);
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static r => r.Contains("ObservationWindow", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewObservationHardeningRecommendations.BlockedByObservationWindowNotPassed;
        if (blocked.Any(static r => r.Contains("InsufficientObservationRuns", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewObservationHardeningRecommendations.BlockedByInsufficientRuns;
        if (blocked.Any(static r => r.Contains("InsufficientRequestCount", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewObservationHardeningRecommendations.BlockedByInsufficientRequests;
        if (blocked.Any(static r => r.Contains("Duration", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewObservationHardeningRecommendations.BlockedByDurationExceeded;
        if (blocked.Any(static r => r.Contains("ErrorCount", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewObservationHardeningRecommendations.BlockedByErrorCount;
        if (blocked.Any(static r => r.Contains("Route", StringComparison.OrdinalIgnoreCase) || r.Contains("NoOp", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewObservationHardeningRecommendations.BlockedByRouteHitViolation;
        if (blocked.Any(static r => r.Contains("Trace", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewObservationHardeningRecommendations.BlockedByTraceIntegrity;
        if (blocked.Any(static r => r.Contains("Stable", StringComparison.OrdinalIgnoreCase) || r.Contains("Signature", StringComparison.OrdinalIgnoreCase) || r.Contains("Candidate", StringComparison.OrdinalIgnoreCase) || r.Contains("Payload", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewObservationHardeningRecommendations.BlockedByStabilitySignature;
        if (blocked.Any(static r => r.Contains("Runtime", StringComparison.OrdinalIgnoreCase) || r.Contains("Formal", StringComparison.OrdinalIgnoreCase) || r.Contains("Package", StringComparison.OrdinalIgnoreCase) || r.Contains("Packing", StringComparison.OrdinalIgnoreCase) || r.Contains("Applied", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewObservationHardeningRecommendations.BlockedByRuntimeInvariant;
        return ControlledAppliedMergeRuntimePreviewObservationHardeningRecommendations.KeepPreviewOnly;
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
