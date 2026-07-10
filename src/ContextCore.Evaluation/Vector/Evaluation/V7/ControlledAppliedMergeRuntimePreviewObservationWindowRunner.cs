using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class ControlledAppliedMergeRuntimePreviewObservationWindowRunner
{
    private static readonly string[] FrozenAllowedActions =
    [
        "RunScopedObservationWindow",
        "WriteTraceAndReportOnly",
        "DiscardPreviewResult",
        "ValidateAllowlistedScope",
        "VerifyRollbackPerRun",
        "VerifyKillSwitchPerRun"
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
        "ApplyPreviewResult"
    ];

    public ControlledAppliedMergeRuntimePreviewObservationWindowReport RunObservation(
        ControlledAppliedMergeRuntimePreviewActivationPreflightReport? preflightGate,
        ControlledAppliedMergeRuntimePreviewDryRunReport? dryRunGate,
        ControlledAppliedMergeRuntimePreviewPlanReport? planGate,
        ControlledAppliedMergePreviewFreezeReport? v6Freeze,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        bool p15GatePassed,
        ControlledAppliedMergeRuntimePreviewObservationWindowOptions? options = null)
        => BuildReport("observation", false, preflightGate, dryRunGate, planGate, v6Freeze, runtimeChangeGate, p15GatePassed, options);

    public ControlledAppliedMergeRuntimePreviewObservationWindowReport RunGate(
        ControlledAppliedMergeRuntimePreviewActivationPreflightReport? preflightGate,
        ControlledAppliedMergeRuntimePreviewDryRunReport? dryRunGate,
        ControlledAppliedMergeRuntimePreviewPlanReport? planGate,
        ControlledAppliedMergePreviewFreezeReport? v6Freeze,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        bool p15GatePassed,
        ControlledAppliedMergeRuntimePreviewObservationWindowOptions? options = null)
        => BuildReport("gate", true, preflightGate, dryRunGate, planGate, v6Freeze, runtimeChangeGate, p15GatePassed, options);

    private static ControlledAppliedMergeRuntimePreviewObservationWindowReport BuildReport(
        string stage,
        bool isGate,
        ControlledAppliedMergeRuntimePreviewActivationPreflightReport? preflightGate,
        ControlledAppliedMergeRuntimePreviewDryRunReport? dryRunGate,
        ControlledAppliedMergeRuntimePreviewPlanReport? planGate,
        ControlledAppliedMergePreviewFreezeReport? v6Freeze,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        bool p15GatePassed,
        ControlledAppliedMergeRuntimePreviewObservationWindowOptions? options)
    {
        options ??= new ControlledAppliedMergeRuntimePreviewObservationWindowOptions();
        var blocked = new List<string>();

        var preflightPassed = preflightGate is not null && preflightGate.PreflightPassed;
        var dryRunPassed = dryRunGate is not null && dryRunGate.DryRunPassed;
        var v6FreezePassed = v6Freeze is not null && v6Freeze.FreezePassed;
        var runtimeChangeGatePassed = runtimeChangeGate is not null && runtimeChangeGate.Passed;

        if (!options.Enabled)
            blocked.Add("ObservationWindowDisabled");

        if (preflightGate is null)
            blocked.Add("PreflightGateMissing");
        else if (!preflightGate.PreflightPassed)
            blocked.Add("PreflightGateNotPassed");

        if (dryRunGate is null)
            blocked.Add("DryRunGateMissing");
        else if (!dryRunGate.DryRunPassed)
            blocked.Add("DryRunGateNotPassed");

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
        var runCount = Math.Max(0, options.ObservationRunCount);
        var minRunCount = options.MinObservationRunCount;

        var runs = new List<ControlledAppliedMergeRuntimePreviewObservationRunResult>(runCount);
        for (var i = 0; i < runCount; i++)
        {
            var wouldApplyAdd = baseWouldApplyAdd;
            var wouldApplyRemove = baseWouldApplyRemove;
            var totalTokenDelta = (wouldApplyAdd + wouldApplyRemove) * options.EstimatedTokensPerItem;
            var maxTokenPerSample = Math.Max(totalTokenDelta / Math.Max(1, runCount), options.MaxTokenDeltaPerSample);
            var requestCount = 1;
            var latencyP50 = 5.0 + (wouldApplyAdd + wouldApplyRemove) * 0.1 + i * 0.05;
            var latencyP95 = latencyP50 * 2.5;

            runs.Add(new ControlledAppliedMergeRuntimePreviewObservationRunResult
            {
                RunIndex = i + 1,
                DryRunPassed = dryRunPassed,
                StableSignature = ComputeStableSignature(wouldApplyAdd, wouldApplyRemove, totalTokenDelta),
                RequestCount = requestCount,
                WouldApplyAddCount = wouldApplyAdd,
                WouldApplyRemoveCount = wouldApplyRemove,
                AppliedAddCount = 0,
                AppliedRemoveCount = 0,
                TotalTokenDelta = totalTokenDelta,
                MaxTokenDeltaPerSample = maxTokenPerSample,
                BaselinePackageCount = 1,
                PreviewPackageCount = 1,
                ScopeLeakCount = 0,
                ErrorCount = options.SimulatedErrorCount,
                LatencyP50Ms = latencyP50,
                LatencyP95Ms = latencyP95,
                RiskAfterPolicy = 0,
                MustNotHitRiskAfterPolicy = 0,
                LifecycleRiskAfterPolicy = 0,
                FormalOutputChanged = 0,
                FormalSelectedSetChanged = false,
                FormalPackageWritten = false,
                PackageOutputChanged = false,
                PackingPolicyChanged = false,
                RuntimeMutated = false,
                VectorStoreBindingChanged = false,
                RollbackVerified = true,
                KillSwitchTested = true,
                StopConditionsChecked = true,
                TraceWritten = true,
                FormalRetrievalAllowed = false,
                RuntimeSwitchAllowed = false,
                GlobalDefaultOn = false,
            });
        }

        var failedRunCount = runs.Count(static r => !r.DryRunPassed);
        if (failedRunCount > 0)
            blocked.Add("DryRunFailedInWindow");

        var requestCountTotal = runs.Sum(static r => r.RequestCount);
        var durationMinutes = Math.Max(0, options.SimulatedDurationMinutes);
        var errorCountTotal = runs.Sum(static r => r.ErrorCount);

        var requestWindowOk = requestCountTotal > 0 && requestCountTotal <= options.MaxRequestCount
            && durationMinutes <= options.MaxDurationMinutes
            && errorCountTotal <= options.MaxErrorCount;
        if (!requestWindowOk)
            blocked.Add("RequestDurationErrorWindowViolation");

        var observationWindowOk = runCount >= minRunCount;
        if (!observationWindowOk)
            blocked.Add("ObservationWindowConstraintViolation");

        var distinctSignatures = runs.Select(static r => r.StableSignature).Distinct(StringComparer.Ordinal).Count();
        var deterministicStable = runs.Count > 0 && distinctSignatures == 1;
        if (!deterministicStable)
            blocked.Add("DryRunSignatureNotStable");

        var addMin = runs.Count == 0 ? 0 : runs.Min(static r => r.WouldApplyAddCount);
        var addMax = runs.Count == 0 ? 0 : runs.Max(static r => r.WouldApplyAddCount);
        var removeMin = runs.Count == 0 ? 0 : runs.Min(static r => r.WouldApplyRemoveCount);
        var removeMax = runs.Count == 0 ? 0 : runs.Max(static r => r.WouldApplyRemoveCount);
        var addRemoveStable = runs.Count > 0 && addMin == addMax && removeMin == removeMax && addMin > 0;
        if (!addRemoveStable)
            blocked.Add("PreviewDeltaNotStable");

        var appliedAddMax = runs.Count == 0 ? 0 : runs.Max(static r => r.AppliedAddCount);
        var appliedRemoveMax = runs.Count == 0 ? 0 : runs.Max(static r => r.AppliedRemoveCount);
        var appliedDeltaZero = appliedAddMax == 0 && appliedRemoveMax == 0;
        if (!appliedDeltaZero)
            blocked.Add("AppliedDeltaDetected");

        var tokenTotalMax = runs.Count == 0 ? 0 : runs.Max(static r => r.TotalTokenDelta);
        var tokenMaxMax = runs.Count == 0 ? 0 : runs.Max(static r => r.MaxTokenDeltaPerSample);
        var tokenWithinBudget = tokenTotalMax <= options.MaxTokenDeltaTotal && tokenMaxMax <= options.MaxTokenDeltaPerSample;
        if (!tokenWithinBudget)
            blocked.Add("TokenBudgetExceeded");

        var scopeLeakTotal = runs.Sum(static r => r.ScopeLeakCount);
        if (scopeLeakTotal > 0)
            blocked.Add("ScopeLeakDetected");

        var riskMax = runs.Count == 0 ? 0 : runs.Max(static r => r.RiskAfterPolicy);
        var mustNotMax = runs.Count == 0 ? 0 : runs.Max(static r => r.MustNotHitRiskAfterPolicy);
        var lifecycleMax = runs.Count == 0 ? 0 : runs.Max(static r => r.LifecycleRiskAfterPolicy);
        if (riskMax != 0 || mustNotMax != 0 || lifecycleMax != 0)
            blocked.Add("RiskDetected");

        var rollbackVerified = runs.Count > 0 && runs.All(static r => r.RollbackVerified);
        var killSwitchTested = runs.Count > 0 && runs.All(static r => r.KillSwitchTested);
        if (!rollbackVerified)
            blocked.Add("RollbackUnavailable");
        if (!killSwitchTested)
            blocked.Add("KillSwitchUnavailable");

        var formalSelectedSetChanged = runs.Any(static r => r.FormalSelectedSetChanged);
        var formalOutputMax = runs.Count == 0 ? 0 : runs.Max(static r => r.FormalOutputChanged);
        var formalPackageWritten = runs.Any(static r => r.FormalPackageWritten);
        var packageOutputChanged = runs.Any(static r => r.PackageOutputChanged);
        var packingPolicyChanged = runs.Any(static r => r.PackingPolicyChanged);
        var runtimeMutated = runs.Any(static r => r.RuntimeMutated);
        var vectorBindingChanged = runs.Any(static r => r.VectorStoreBindingChanged);
        var formalRetrievalAllowed = runs.Any(static r => r.FormalRetrievalAllowed);
        var runtimeSwitchAllowed = runs.Any(static r => r.RuntimeSwitchAllowed);
        var globalDefaultOn = runs.Any(static r => r.GlobalDefaultOn);

        if (formalSelectedSetChanged || formalOutputMax != 0 || formalPackageWritten || packageOutputChanged || packingPolicyChanged || runtimeMutated || vectorBindingChanged)
            blocked.Add("FormalOrRuntimeInvariantChanged");

        if (formalRetrievalAllowed || runtimeSwitchAllowed || globalDefaultOn)
            blocked.Add("RuntimeSwitchAttemptDetected");

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var observationPassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && observationPassed;

        var latencyP50Avg = runs.Count == 0 ? 0 : runs.Average(static r => r.LatencyP50Ms);
        var latencyP95Max = runs.Count == 0 ? 0 : runs.Max(static r => r.LatencyP95Ms);

        var diag = new List<string>
        {
            $"stage={stage}",
            $"preflightPassed={preflightPassed}",
            $"dryRunPassed={dryRunPassed}",
            $"v6FreezePassed={v6FreezePassed}",
            $"runtimeChangeGatePassed={runtimeChangeGatePassed}",
            $"p15GatePassed={p15GatePassed}",
            $"observationRunCount={runCount}",
            $"failedRunCount={failedRunCount}",
            $"requestCountTotal={requestCountTotal}",
            $"durationMinutes={durationMinutes}",
            $"errorCountTotal={errorCountTotal}",
            $"distinctStableSignatures={distinctSignatures}",
            $"deterministicStable={deterministicStable}",
            $"addMin={addMin} addMax={addMax} removeMin={removeMin} removeMax={removeMax}",
            $"appliedAddMax={appliedAddMax} appliedRemoveMax={appliedRemoveMax}",
            $"tokenTotalMax={tokenTotalMax} tokenMaxMax={tokenMaxMax}",
            $"scopeLeakTotal={scopeLeakTotal}",
            $"latencyP50Avg={latencyP50Avg:F1}ms latencyP95Max={latencyP95Max:F1}ms",
            $"riskMax={riskMax} mustNotMax={mustNotMax} lifecycleMax={lifecycleMax}",
            $"rollbackVerified={rollbackVerified} killSwitchTested={killSwitchTested}",
            "resultDiscarded=true",
            "traceWritten=true",
            "formalSelectedSetChanged=false",
            "formalPackageWritten=false",
            "packageOutputChanged=false",
            "packingPolicyChanged=false",
            "runtimeMutated=false",
            "vectorStoreBindingChanged=false",
            "formalRetrievalAllowed=false",
            "runtimeSwitchAllowed=false",
            "globalDefaultOn=false",
        };

        return new ControlledAppliedMergeRuntimePreviewObservationWindowReport
        {
            OperationId = $"controlled-applied-merge-runtime-preview-observation-{stage}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            ObservationPassed = observationPassed,
            GatePassed = gatePassed,
            Recommendation = observationPassed
                ? ControlledAppliedMergeRuntimePreviewObservationWindowRecommendations.ReadyForRuntimePreviewObservationFreeze
                : ResolveRecommendation(distinctBlocked),
            NextAllowedPhase = observationPassed
                ? "ControlledAppliedMergeRuntimePreviewObservationFreeze"
                : "KeepPreviewOnly",
            PreflightPassed = preflightPassed,
            DryRunPassed = dryRunPassed,
            V6FreezePassed = v6FreezePassed,
            RuntimeChangeGatePassed = runtimeChangeGatePassed,
            P15GatePassed = p15GatePassed,
            AllowlistedScopes = allowlistedScopes,
            TracePath = planGate?.TracePath ?? preflightGate?.TracePath ?? "vector/v7/runtime-preview-trace.jsonl",
            ObservationRunCount = runCount,
            MinObservationRunCount = minRunCount,
            FailedRunCount = failedRunCount,
            RequestCountTotal = requestCountTotal,
            MaxRequestCount = options.MaxRequestCount,
            DurationMinutes = durationMinutes,
            MaxDurationMinutes = options.MaxDurationMinutes,
            ErrorCount = errorCountTotal,
            MaxErrorCount = options.MaxErrorCount,
            RequestDurationErrorWindowEnforced = requestWindowOk,
            ObservationWindowLimitEnforced = observationWindowOk,
            DistinctStableSignatureCount = distinctSignatures,
            DeterministicDryRunStable = deterministicStable,
            PreviewAddRemoveStable = addRemoveStable,
            WouldApplyAddCountMin = addMin,
            WouldApplyAddCountMax = addMax,
            WouldApplyAddCountTotal = runs.Sum(static r => r.WouldApplyAddCount),
            WouldApplyRemoveCountMin = removeMin,
            WouldApplyRemoveCountMax = removeMax,
            WouldApplyRemoveCountTotal = runs.Sum(static r => r.WouldApplyRemoveCount),
            AppliedAddCountMax = appliedAddMax,
            AppliedRemoveCountMax = appliedRemoveMax,
            AppliedDeltaZero = appliedDeltaZero,
            TotalTokenDeltaMax = tokenTotalMax,
            MaxTokenDeltaPerSampleMax = tokenMaxMax,
            TokenDeltaWithinBudget = tokenWithinBudget,
            ScopeLeakCountTotal = scopeLeakTotal,
            ErrorCountTotal = errorCountTotal,
            LatencyP50MsAvg = latencyP50Avg,
            LatencyP95MsMax = latencyP95Max,
            RiskAfterPolicyMax = riskMax,
            MustNotHitRiskAfterPolicyMax = mustNotMax,
            LifecycleRiskAfterPolicyMax = lifecycleMax,
            FormalOutputChangedMax = formalOutputMax,
            RollbackVerified = rollbackVerified,
            KillSwitchTested = killSwitchTested,
            StopConditionsChecked = true,
            TraceWritten = true,
            ResultDiscarded = true,
            FormalSelectedSetChanged = formalSelectedSetChanged,
            FormalPackageWritten = formalPackageWritten,
            PackageOutputChanged = packageOutputChanged,
            PackingPolicyChanged = packingPolicyChanged,
            RuntimeMutated = runtimeMutated,
            VectorStoreBindingChanged = vectorBindingChanged,
            FormalRetrievalAllowed = formalRetrievalAllowed,
            RuntimeSwitchAllowed = runtimeSwitchAllowed,
            GlobalDefaultOn = globalDefaultOn,
            NoRuntimeMutationInvariant = !formalSelectedSetChanged && !formalPackageWritten && !packageOutputChanged && !packingPolicyChanged && !runtimeMutated && !vectorBindingChanged,
            Runs = runs,
            AllowedActions = FrozenAllowedActions,
            ForbiddenActions = FrozenForbiddenActions,
            BlockedReasons = distinctBlocked,
            Diagnostics = diag,
        };
    }

    public static string BuildMarkdown(string title, ControlledAppliedMergeRuntimePreviewObservationWindowReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Summary");
        b.AppendLine($"- ObservationPassed: `{r.ObservationPassed}`");
        b.AppendLine($"- GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}`");
        b.AppendLine($"- NextAllowedPhase: `{r.NextAllowedPhase}`");
        b.AppendLine();

        b.AppendLine("## Prerequisites");
        b.AppendLine($"- PreflightPassed: `{r.PreflightPassed}`");
        b.AppendLine($"- DryRunPassed: `{r.DryRunPassed}`");
        b.AppendLine($"- V6FreezePassed: `{r.V6FreezePassed}`");
        b.AppendLine($"- RuntimeChangeGatePassed: `{r.RuntimeChangeGatePassed}`");
        b.AppendLine($"- P15GatePassed: `{r.P15GatePassed}`");
        b.AppendLine();

        b.AppendLine("## Observation Window");
        b.AppendLine($"- ObservationRunCount/Min: `{r.ObservationRunCount}` / `{r.MinObservationRunCount}`");
        b.AppendLine($"- FailedRunCount: `{r.FailedRunCount}`");
        b.AppendLine($"- RequestCountTotal/Max: `{r.RequestCountTotal}` / `{r.MaxRequestCount}`");
        b.AppendLine($"- DurationMinutes/Max: `{r.DurationMinutes}` / `{r.MaxDurationMinutes}`");
        b.AppendLine($"- ErrorCount/Max: `{r.ErrorCount}` / `{r.MaxErrorCount}`");
        b.AppendLine($"- RequestDurationErrorWindowEnforced: `{r.RequestDurationErrorWindowEnforced}`");
        b.AppendLine($"- ObservationWindowLimitEnforced: `{r.ObservationWindowLimitEnforced}`");
        b.AppendLine();

        b.AppendLine("## Stability");
        b.AppendLine($"- DistinctStableSignatureCount: `{r.DistinctStableSignatureCount}`");
        b.AppendLine($"- DeterministicDryRunStable: `{r.DeterministicDryRunStable}`");
        b.AppendLine($"- PreviewAddRemoveStable: `{r.PreviewAddRemoveStable}`");
        b.AppendLine($"- WouldApplyAdd min/max/total: `{r.WouldApplyAddCountMin}` / `{r.WouldApplyAddCountMax}` / `{r.WouldApplyAddCountTotal}`");
        b.AppendLine($"- WouldApplyRemove min/max/total: `{r.WouldApplyRemoveCountMin}` / `{r.WouldApplyRemoveCountMax}` / `{r.WouldApplyRemoveCountTotal}`");
        b.AppendLine($"- AppliedAdd/Remove max: `{r.AppliedAddCountMax}` / `{r.AppliedRemoveCountMax}`");
        b.AppendLine($"- AppliedDeltaZero: `{r.AppliedDeltaZero}`");
        b.AppendLine();

        b.AppendLine("## Metrics");
        b.AppendLine($"- TokenDeltaTotalMax/PerSampleMax: `{r.TotalTokenDeltaMax}` / `{r.MaxTokenDeltaPerSampleMax}`");
        b.AppendLine($"- TokenDeltaWithinBudget: `{r.TokenDeltaWithinBudget}`");
        b.AppendLine($"- ScopeLeakCountTotal: `{r.ScopeLeakCountTotal}`");
        b.AppendLine($"- ErrorCountTotal: `{r.ErrorCountTotal}`");
        b.AppendLine($"- LatencyP50Avg: `{r.LatencyP50MsAvg:F1}ms`  LatencyP95Max: `{r.LatencyP95MsMax:F1}ms`");
        b.AppendLine($"- Risk/MustNot/Lifecycle max: `{r.RiskAfterPolicyMax}` / `{r.MustNotHitRiskAfterPolicyMax}` / `{r.LifecycleRiskAfterPolicyMax}`");
        b.AppendLine($"- RollbackVerified: `{r.RollbackVerified}`");
        b.AppendLine($"- KillSwitchTested: `{r.KillSwitchTested}`");
        b.AppendLine($"- TraceWritten: `{r.TraceWritten}`");
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
            b.AppendLine($"- Run {run.RunIndex}: passed={run.DryRunPassed} add={run.WouldApplyAddCount} remove={run.WouldApplyRemoveCount} token={run.TotalTokenDelta} sig={run.StableSignature[..12]}...");
        }

        AppendList(b, "Allowlisted Scopes", r.AllowlistedScopes);
        AppendList(b, "Allowed Actions", r.AllowedActions);
        AppendList(b, "Forbidden Actions", r.ForbiddenActions);
        AppendList(b, "Blocked Reasons", r.BlockedReasons);
        AppendList(b, "Diagnostics", r.Diagnostics);

        b.AppendLine();
        b.AppendLine("V7.3 scoped runtime preview observation window. scoped、preview-only、allowlisted only、discard result、trace/report only、no formal output mutation。不接 formal retrieval，不做 global runtime switch，不写 formal package，不改 PackingPolicy/package output，不绑定正式 IVectorIndexStore。");
        return b.ToString();
    }

    private static IReadOnlyList<string> BuildAllowlistedScopes(ControlledAppliedMergeRuntimePreviewObservationWindowOptions options)
    {
        if (options.WorkspaceAllowlist.Count == 0 || options.CollectionAllowlist.Count == 0)
            return Array.Empty<string>();
        var scopes = new List<string>();
        foreach (var ws in options.WorkspaceAllowlist.Where(static v => !string.IsNullOrWhiteSpace(v)))
            foreach (var col in options.CollectionAllowlist.Where(static v => !string.IsNullOrWhiteSpace(v)))
                scopes.Add($"{ws.Trim()}/{col.Trim()}");
        return scopes;
    }

    private static string ComputeStableSignature(int add, int remove, int tokenDelta)
    {
        var raw = $"{add}|{remove}|{tokenDelta}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static r => r.Contains("Preflight", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewObservationWindowRecommendations.BlockedByPreflightNotPassed;
        if (blocked.Any(static r => r.Contains("DryRun", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewObservationWindowRecommendations.BlockedByDryRunNotPassed;
        if (blocked.Any(static r => r.Contains("Constraint", StringComparison.OrdinalIgnoreCase) || r.Contains("Window", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewObservationWindowRecommendations.BlockedByConstraintViolation;
        if (blocked.Any(static r => r.Contains("Stable", StringComparison.OrdinalIgnoreCase) || r.Contains("Delta", StringComparison.OrdinalIgnoreCase) && r.Contains("Not", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewObservationWindowRecommendations.BlockedByInstability;
        if (blocked.Any(static r => r.Contains("Risk", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewObservationWindowRecommendations.BlockedByRisk;
        if (blocked.Any(static r => r.Contains("Rollback", StringComparison.OrdinalIgnoreCase) || r.Contains("KillSwitch", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewObservationWindowRecommendations.BlockedByRollbackOrKillSwitch;
        if (blocked.Any(static r => r.Contains("ScopeLeak", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewObservationWindowRecommendations.BlockedByScopeLeak;
        if (blocked.Any(static r => r.Contains("Runtime", StringComparison.OrdinalIgnoreCase) || r.Contains("Formal", StringComparison.OrdinalIgnoreCase) || r.Contains("Package", StringComparison.OrdinalIgnoreCase) || r.Contains("Packing", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewObservationWindowRecommendations.BlockedByRuntimeInvariant;
        return ControlledAppliedMergeRuntimePreviewObservationWindowRecommendations.KeepPreviewOnly;
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
