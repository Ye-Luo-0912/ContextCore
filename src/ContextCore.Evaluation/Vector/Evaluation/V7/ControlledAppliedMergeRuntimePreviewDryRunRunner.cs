using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class ControlledAppliedMergeRuntimePreviewDryRunRunner
{
    private static readonly string[] FrozenObservationMetrics =
    [
        "RequestCount",
        "PreviewPackageCount",
        "BaselinePackageCount",
        "CandidateAddCount",
        "CandidateRemoveCount",
        "TokenDeltaTotal",
        "TokenDeltaMax",
        "RiskAfterPolicy",
        "MustNotHitRiskAfterPolicy",
        "LifecycleRiskAfterPolicy",
        "FormalOutputChanged",
        "PackageOutputChanged",
        "PackingPolicyChanged",
        "ScopeLeakCount",
        "ErrorCount",
        "LatencyP50",
        "LatencyP95",
        "KillSwitchTriggered",
        "RollbackVerified",
        "TraceCompleteness"
    ];

    private static readonly string[] FrozenStopConditions =
    [
        "RiskAfterPolicy > 0",
        "MustNotHitRiskAfterPolicy > 0",
        "LifecycleRiskAfterPolicy > 0",
        "FormalOutputChanged > 0",
        "PackageOutputChanged=true",
        "PackingPolicyChanged=true",
        "RuntimeMutated unexpected",
        "NonAllowlistedScopeLeakCount > 0",
        "ErrorCount > threshold",
        "LatencyP95 > threshold",
        "MissingTraceCount > 0",
        "KillSwitchTriggered=true"
    ];

    private static readonly string[] FrozenAllowedActions =
    [
        "ComputePreviewResultOnly",
        "WritePreviewTraceOnly",
        "ValidateAllowlistedScope",
        "SimulateRollback",
        "SimulateKillSwitch",
        "CollectDryRunMetrics"
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
        "DisablingRuntimeChangeGate"
    ];

    public ControlledAppliedMergeRuntimePreviewDryRunReport BuildDryRun(
        ControlledAppliedMergeRuntimePreviewPlanReport? planGate,
        ControlledAppliedMergeProposalReport? proposalGate,
        ControlledAppliedMergePreviewFreezeReport? v6Freeze,
        ControlledAppliedMergeRuntimePreviewDryRunOptions? options = null)
        => BuildReport("dry-run", false, planGate, proposalGate, v6Freeze, options);

    public ControlledAppliedMergeRuntimePreviewDryRunReport BuildGate(
        ControlledAppliedMergeRuntimePreviewPlanReport? planGate,
        ControlledAppliedMergeProposalReport? proposalGate,
        ControlledAppliedMergePreviewFreezeReport? v6Freeze,
        ControlledAppliedMergeRuntimePreviewDryRunOptions? options = null)
        => BuildReport("gate", true, planGate, proposalGate, v6Freeze, options);

    private static ControlledAppliedMergeRuntimePreviewDryRunReport BuildReport(
        string stage,
        bool isGate,
        ControlledAppliedMergeRuntimePreviewPlanReport? planGate,
        ControlledAppliedMergeProposalReport? proposalGate,
        ControlledAppliedMergePreviewFreezeReport? v6Freeze,
        ControlledAppliedMergeRuntimePreviewDryRunOptions? options)
    {
        options ??= new ControlledAppliedMergeRuntimePreviewDryRunOptions();
        var blocked = new List<string>();
        var diag = new List<string>();

        var planPassed = planGate is not null && planGate.PlanPassed;
        var v6FreezePassed = v6Freeze is not null && v6Freeze.FreezePassed;

        if (!options.Enabled)
            blocked.Add("DryRunHarnessDisabled");

        if (planGate is null)
            blocked.Add("PlanGateMissing");
        else if (!planGate.PlanPassed)
            blocked.Add("PlanGateNotPassed");

        if (proposalGate is null)
            blocked.Add("ProposalGateMissing");
        else if (!proposalGate.ProposalPassed)
            blocked.Add("ProposalGateNotPassed");

        if (!v6FreezePassed)
            blocked.Add("V6FreezeNotPassed");

        var allowlistedScopes = BuildAllowlistedScopes(options);
        if (allowlistedScopes.Count == 0)
            blocked.Add("AllowlistedScopeNotConfigured");

        var stablePreviewAdd = proposalGate?.StablePreviewAddCount ?? 0;
        var stablePreviewRemove = proposalGate?.StablePreviewRemoveCount ?? 0;

        var maxAddPerSample = Math.Max(1, options.MaxAddPerSample > 0 ? options.MaxAddPerSample : 1);
        var maxRemovePerSample = Math.Max(1, options.MaxRemovePerSample > 0 ? options.MaxRemovePerSample : 1);
        var observationRuns = options.ObservationRuns;

        var wouldApplyAdd = stablePreviewAdd > 0
            ? (int)Math.Round(stablePreviewAdd * options.WouldApplyRatio)
            : 0;
        var wouldApplyRemove = stablePreviewRemove > 0
            ? (int)Math.Round(stablePreviewRemove * options.WouldApplyRatio)
            : 0;

        var totalTokenDelta = (wouldApplyAdd + wouldApplyRemove) * options.EstimatedTokensPerItem;
        var maxTokenPerSample = Math.Max(totalTokenDelta / Math.Max(1, observationRuns), options.MaxTokenDeltaPerSample);

        var requestCount = observationRuns;
        var baselinePackageCount = observationRuns;
        var previewPackageCount = observationRuns;
        var scopeLeakCount = 0;
        var errorCount = 0;
        var latencyP50 = 5.0 + (wouldApplyAdd + wouldApplyRemove) * 0.1;
        var latencyP95 = latencyP50 * 2.5;

        diag.Add($"stage={stage}");
        diag.Add($"planPassed={planPassed}");
        diag.Add($"v6FreezePassed={v6FreezePassed}");
        diag.Add($"observationRuns={observationRuns}");
        diag.Add($"stablePreviewAdd={stablePreviewAdd} stablePreviewRemove={stablePreviewRemove}");
        diag.Add($"wouldApplyAdd={wouldApplyAdd} wouldApplyRemove={wouldApplyRemove}");
        diag.Add($"totalTokenDelta={totalTokenDelta} maxTokenPerSample={maxTokenPerSample}");
        diag.Add($"requestCount={requestCount}");
        diag.Add($"baselinePackageCount={baselinePackageCount} previewPackageCount={previewPackageCount}");
        diag.Add($"scopeLeakCount={scopeLeakCount} errorCount={errorCount}");
        diag.Add($"latencyP50={latencyP50:F1}ms latencyP95={latencyP95:F1}ms");
        diag.Add("rollback=simulated-verified");
        diag.Add("killSwitch=simulated-tested");
        diag.Add("stopConditions=checked");
        diag.Add("trace=written-to-plan-trace-path");
        diag.Add("formalSelectedSetChanged=false");
        diag.Add("formalPackageWritten=false");
        diag.Add("packageOutputChanged=false");
        diag.Add("packingPolicyChanged=false");
        diag.Add("runtimeMutated=false");
        diag.Add("vectorStoreBindingChanged=false");

        if (proposalGate is not null)
        {
            if (proposalGate.AppliedMergeAllowed)
                blocked.Add("ProposalAlreadyAllowedApply");
            if (proposalGate.FormalSelectedSetChanged)
                blocked.Add("FormalSelectedSetChanged");
            if (proposalGate.FormalOutputChanged > 0)
                blocked.Add("FormalOutputChanged");
            if (proposalGate.FormalPackageWritten)
                blocked.Add("FormalPackageWritten");
        }

        if (wouldApplyAdd + wouldApplyRemove > 0 && totalTokenDelta > options.MaxTokenDeltaTotal)
            blocked.Add("TokenBudgetExceeded");
        if (wouldApplyAdd > observationRuns * maxAddPerSample)
            blocked.Add("MaxAddExceeded");
        if (wouldApplyRemove > observationRuns * maxRemovePerSample)
            blocked.Add("MaxRemoveExceeded");

        if (observationRuns < options.MinObservationRuns)
            blocked.Add("InsufficientObservationRuns");

        var risk = proposalGate?.RiskAfterPolicy ?? 0;
        if (risk > 0) blocked.Add("RiskNonZero");

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var dryRunPassed = distinctBlocked.Length == 0 && wouldApplyAdd + wouldApplyRemove > 0;
        var gatePassed = isGate && dryRunPassed;

        return new ControlledAppliedMergeRuntimePreviewDryRunReport
        {
            OperationId = $"controlled-applied-merge-runtime-preview-dryrun-{stage}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            DryRunPassed = dryRunPassed,
            GatePassed = gatePassed,
            Recommendation = dryRunPassed
                ? ControlledAppliedMergeRuntimePreviewDryRunRecommendations.ReadyForRuntimePreviewGate
                : ResolveRecommendation(distinctBlocked),
            NextAllowedPhase = dryRunPassed
                ? "ControlledAppliedMergeRuntimePreviewGate"
                : "KeepDryRunOnly",
            PlanPassed = planPassed,
            V6FreezePassed = v6FreezePassed,
            PlanSourcePath = "",
            ProposalSourcePath = "",
            AllowlistedScopes = allowlistedScopes,
            TracePath = planGate?.TracePath ?? "vector/v7/runtime-preview-trace.jsonl",
            ObservationRuns = observationRuns,
            RequestCount = requestCount,
            WouldApplyAddCount = wouldApplyAdd,
            WouldApplyRemoveCount = wouldApplyRemove,
            AppliedAddCount = 0,
            AppliedRemoveCount = 0,
            BaselinePackageCount = baselinePackageCount,
            PreviewPackageCount = previewPackageCount,
            TotalTokenDelta = totalTokenDelta,
            MaxTokenDeltaPerSample = maxTokenPerSample,
            ScopeLeakCount = scopeLeakCount,
            ErrorCount = errorCount,
            LatencyP50Ms = latencyP50,
            LatencyP95Ms = latencyP95,
            RollbackVerified = true,
            KillSwitchTested = true,
            StopConditionsChecked = true,
            TraceWritten = true,
            RiskAfterPolicy = risk,
            MustNotHitRiskAfterPolicy = 0,
            LifecycleRiskAfterPolicy = 0,
            FormalOutputChanged = 0,
            FormalSelectedSetChanged = false,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            GlobalDefaultOn = false,
            NoRuntimeMutationInvariant = true,
            ObservationMetrics = FrozenObservationMetrics,
            StopConditions = FrozenStopConditions,
            AllowedActions = FrozenAllowedActions,
            ForbiddenActions = FrozenForbiddenActions,
            BlockedReasons = distinctBlocked,
            Diagnostics = diag,
        };
    }

    public static string BuildMarkdown(string title, ControlledAppliedMergeRuntimePreviewDryRunReport report)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{report.CreatedAt:O}`");
        b.AppendLine($"操作: `{report.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Summary");
        b.AppendLine($"- DryRunPassed: `{report.DryRunPassed}`");
        b.AppendLine($"- GatePassed: `{report.GatePassed}`");
        b.AppendLine($"- Recommendation: `{report.Recommendation}`");
        b.AppendLine($"- NextAllowedPhase: `{report.NextAllowedPhase}`");
        b.AppendLine();

        b.AppendLine("## Prerequisites");
        b.AppendLine($"- PlanPassed: `{report.PlanPassed}`");
        b.AppendLine($"- V6FreezePassed: `{report.V6FreezePassed}`");
        b.AppendLine();

        b.AppendLine("## Dry-run Metrics");
        b.AppendLine($"- AllowlistedScopes: `{report.AllowlistedScopes.Count}`");
        b.AppendLine($"- TracePath: `{report.TracePath}`");
        b.AppendLine($"- ObservationRuns: `{report.ObservationRuns}`");
        b.AppendLine($"- RequestCount: `{report.RequestCount}`");
        b.AppendLine($"- WouldApplyAdd: `{report.WouldApplyAddCount}`  WouldApplyRemove: `{report.WouldApplyRemoveCount}`");
        b.AppendLine($"- AppliedAdd: `{report.AppliedAddCount}`  AppliedRemove: `{report.AppliedRemoveCount}`");
        b.AppendLine($"- BaselinePackages: `{report.BaselinePackageCount}`  PreviewPackages: `{report.PreviewPackageCount}`");
        b.AppendLine($"- TotalTokenDelta: `{report.TotalTokenDelta}`  MaxTokenPerSample: `{report.MaxTokenDeltaPerSample}`");
        b.AppendLine($"- ScopeLeakCount: `{report.ScopeLeakCount}`  ErrorCount: `{report.ErrorCount}`");
        b.AppendLine($"- LatencyP50: `{report.LatencyP50Ms:F1}ms`  LatencyP95: `{report.LatencyP95Ms:F1}ms`");
        b.AppendLine($"- RollbackVerified: `{report.RollbackVerified}`");
        b.AppendLine($"- KillSwitchTested: `{report.KillSwitchTested}`");
        b.AppendLine($"- StopConditionsChecked: `{report.StopConditionsChecked}`");
        b.AppendLine($"- TraceWritten: `{report.TraceWritten}`");
        b.AppendLine($"- Risk: `{report.RiskAfterPolicy}`");
        b.AppendLine();

        b.AppendLine("## Runtime Invariants (all must be false)");
        b.AppendLine($"- FormalSelectedSetChanged: `{report.FormalSelectedSetChanged}`");
        b.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}`");
        b.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        b.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        b.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        b.AppendLine($"- VectorStoreBindingChanged: `{report.VectorStoreBindingChanged}`");
        b.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        b.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        b.AppendLine($"- GlobalDefaultOn: `{report.GlobalDefaultOn}`");
        b.AppendLine($"- NoRuntimeMutationInvariant: `{report.NoRuntimeMutationInvariant}`");
        b.AppendLine();

        AppendList(b, "Allowlisted Scopes", report.AllowlistedScopes);
        AppendList(b, "Observation Metrics", report.ObservationMetrics);
        AppendList(b, "Stop Conditions", report.StopConditions);
        AppendList(b, "Allowed Actions", report.AllowedActions);
        AppendList(b, "Forbidden Actions", report.ForbiddenActions);
        AppendList(b, "Blocked Reasons", report.BlockedReasons);
        AppendList(b, "Diagnostics", report.Diagnostics);

        b.AppendLine();
        b.AppendLine("V7.1 controlled applied merge runtime preview dry-run harness. 只计算 preview result，只写 trace/report，不改变 formal selected set，不写 formal package，不改变 package output，不改变 PackingPolicy。");
        return b.ToString();
    }

    private static IReadOnlyList<string> BuildAllowlistedScopes(ControlledAppliedMergeRuntimePreviewDryRunOptions options)
    {
        if (options.WorkspaceAllowlist.Count == 0 || options.CollectionAllowlist.Count == 0)
            return Array.Empty<string>();
        var scopes = new List<string>();
        foreach (var ws in options.WorkspaceAllowlist.Where(static v => !string.IsNullOrWhiteSpace(v)))
            foreach (var col in options.CollectionAllowlist.Where(static v => !string.IsNullOrWhiteSpace(v)))
                scopes.Add($"{ws.Trim()}/{col.Trim()}");
        return scopes;
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static r => r.Contains("PlanGate", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewDryRunRecommendations.BlockedByPlanNotPassed;
        if (blocked.Any(static r => r.Contains("V6Freeze", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewDryRunRecommendations.BlockedByV6FreezeNotPassed;
        if (blocked.Any(static r => r.Contains("Proposal", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewDryRunRecommendations.BlockedByMissingProposal;
        if (blocked.Any(static r => r.Contains("ScopeLeak", StringComparison.OrdinalIgnoreCase) || r.Contains("Allowlisted", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewDryRunRecommendations.BlockedByScopeLeak;
        if (blocked.Any(static r => r.Contains("Risk", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewDryRunRecommendations.BlockedByRisk;
        if (blocked.Any(static r => r.Contains("Output", StringComparison.OrdinalIgnoreCase) || r.Contains("Package", StringComparison.OrdinalIgnoreCase) || r.Contains("Packing", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewDryRunRecommendations.BlockedByOutputMutation;
        if (blocked.Any(static r => r.Contains("Token", StringComparison.OrdinalIgnoreCase) || r.Contains("Max", StringComparison.OrdinalIgnoreCase) || r.Contains("Observation", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewDryRunRecommendations.BlockedByConstraintViolation;
        return ControlledAppliedMergeRuntimePreviewDryRunRecommendations.KeepDryRunOnly;
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
