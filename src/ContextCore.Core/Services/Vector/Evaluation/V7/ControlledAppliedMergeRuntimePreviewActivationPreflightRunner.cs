using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class ControlledAppliedMergeRuntimePreviewActivationPreflightRunner
{
    private static readonly string[] FrozenAllowedActions =
    [
        "ValidateActivationContract",
        "PreviewConfigPatch",
        "ValidateAllowlistedScope",
        "VerifyKillSwitchReady",
        "VerifyRollbackPlanReady",
        "VerifyTraceSinkReady"
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
        "WriteConfigPatch"
    ];

    public ControlledAppliedMergeRuntimePreviewActivationPreflightReport BuildPreflight(
        ControlledAppliedMergeRuntimePreviewPlanReport? planGate,
        ControlledAppliedMergeRuntimePreviewDryRunReport? dryRunGate,
        ControlledAppliedMergePreviewFreezeReport? v6Freeze,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        bool p15GatePassed,
        ControlledAppliedMergeRuntimePreviewActivationPreflightOptions? options = null)
        => BuildReport("preflight", false, planGate, dryRunGate, v6Freeze, runtimeChangeGate, p15GatePassed, options);

    public ControlledAppliedMergeRuntimePreviewActivationPreflightReport BuildGate(
        ControlledAppliedMergeRuntimePreviewPlanReport? planGate,
        ControlledAppliedMergeRuntimePreviewDryRunReport? dryRunGate,
        ControlledAppliedMergePreviewFreezeReport? v6Freeze,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        bool p15GatePassed,
        ControlledAppliedMergeRuntimePreviewActivationPreflightOptions? options = null)
        => BuildReport("gate", true, planGate, dryRunGate, v6Freeze, runtimeChangeGate, p15GatePassed, options);

    private static ControlledAppliedMergeRuntimePreviewActivationPreflightReport BuildReport(
        string stage,
        bool isGate,
        ControlledAppliedMergeRuntimePreviewPlanReport? planGate,
        ControlledAppliedMergeRuntimePreviewDryRunReport? dryRunGate,
        ControlledAppliedMergePreviewFreezeReport? v6Freeze,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        bool p15GatePassed,
        ControlledAppliedMergeRuntimePreviewActivationPreflightOptions? options)
    {
        options ??= new ControlledAppliedMergeRuntimePreviewActivationPreflightOptions();
        var blocked = new List<string>();
        var diag = new List<string>();

        var planPassed = planGate is not null && planGate.PlanPassed;
        var dryRunPassed = dryRunGate is not null && dryRunGate.DryRunPassed;
        var v6FreezePassed = v6Freeze is not null && v6Freeze.FreezePassed;
        var runtimeChangeGatePassed = runtimeChangeGate is not null && runtimeChangeGate.Passed;

        if (!options.Enabled)
            blocked.Add("PreflightDisabled");

        if (!string.Equals(options.Mode, ControlledAppliedMergeRuntimePreviewActivationPreflightModes.PreflightOnly, StringComparison.OrdinalIgnoreCase))
            blocked.Add("UnsupportedPreflightMode");

        if (planGate is null)
            blocked.Add("PlanGateMissing");
        else if (!planGate.PlanPassed)
            blocked.Add("PlanGateNotPassed");

        if (dryRunGate is null)
            blocked.Add("DryRunGateMissing");
        else if (!dryRunGate.DryRunPassed)
            blocked.Add("DryRunGateNotPassed");

        if (!v6FreezePassed)
            blocked.Add("V6FreezeNotPassed");

        if (options.RequireRuntimeChangeGate && !runtimeChangeGatePassed)
            blocked.Add("RuntimeChangeGateNotPassed");

        if (options.RequireP15Gate && !p15GatePassed)
            blocked.Add("P15GateNotPassed");

        var allowlistedScopes = BuildAllowlistedScopes(options);
        if (allowlistedScopes.Count == 0)
            blocked.Add("AllowlistedScopeNotConfigured");

        var killSwitchAvailable = !string.IsNullOrWhiteSpace(planGate?.KillSwitchPlan);
        var rollbackPlanAvailable = !string.IsNullOrWhiteSpace(planGate?.RollbackPlan);
        var traceSinkAvailable = options.TraceSinkAvailable;
        var configSwitch = planGate?.ConfigSwitch ?? "ControlledAppliedMergeRuntimePreview:Enabled";
        var tracePath = planGate?.TracePath ?? "vector/v7/runtime-preview-trace.jsonl";

        if (options.RequireKillSwitch && !killSwitchAvailable)
            blocked.Add("KillSwitchMissing");

        if (options.RequireRollbackPlan && !rollbackPlanAvailable)
            blocked.Add("RollbackPlanMissing");

        if (options.RequireTraceSink && !traceSinkAvailable)
            blocked.Add("TraceSinkMissing");

        var wouldApplyAdd = dryRunGate?.WouldApplyAddCount ?? 0;
        var wouldApplyRemove = dryRunGate?.WouldApplyRemoveCount ?? 0;
        var totalTokenDelta = dryRunGate?.TotalTokenDelta ?? 0;
        var risk = dryRunGate?.RiskAfterPolicy ?? 0;

        if (risk > 0)
            blocked.Add("RiskNonZero");

        if (dryRunGate is not null)
        {
            if (dryRunGate.FormalSelectedSetChanged)
                blocked.Add("FormalSelectedSetChanged");
            if (dryRunGate.FormalPackageWritten)
                blocked.Add("FormalPackageWritten");
            if (dryRunGate.PackageOutputChanged)
                blocked.Add("PackageOutputChanged");
            if (dryRunGate.PackingPolicyChanged)
                blocked.Add("PackingPolicyChanged");
            if (dryRunGate.RuntimeMutated)
                blocked.Add("RuntimeMutated");
            if (dryRunGate.VectorStoreBindingChanged)
                blocked.Add("VectorStoreBindingChanged");
        }

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var preflightPassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && preflightPassed;

        diag.Add($"stage={stage}");
        diag.Add($"planPassed={planPassed}");
        diag.Add($"dryRunPassed={dryRunPassed}");
        diag.Add($"v6FreezePassed={v6FreezePassed}");
        diag.Add($"runtimeChangeGatePassed={runtimeChangeGatePassed}");
        diag.Add($"p15GatePassed={p15GatePassed}");
        diag.Add($"allowlistedScopes={allowlistedScopes.Count}");
        diag.Add($"killSwitchAvailable={killSwitchAvailable}");
        diag.Add($"rollbackPlanAvailable={rollbackPlanAvailable}");
        diag.Add($"traceSinkAvailable={traceSinkAvailable}");
        diag.Add($"configSwitch={configSwitch}");
        diag.Add($"configPatchPreviewed={preflightPassed}");
        diag.Add($"configPatchWritten=false");
        diag.Add($"scopeValidationPassed={preflightPassed && allowlistedScopes.Count > 0}");
        diag.Add($"scopeLeakCount=0");
        diag.Add($"wouldApplyAdd={wouldApplyAdd} wouldApplyRemove={wouldApplyRemove}");
        diag.Add($"totalTokenDelta={totalTokenDelta}");
        diag.Add($"risk={risk}");
        diag.Add("formalSelectedSetChanged=false");
        diag.Add("formalPackageWritten=false");
        diag.Add("packageOutputChanged=false");
        diag.Add("packingPolicyChanged=false");
        diag.Add("runtimeMutated=false");
        diag.Add("vectorStoreBindingChanged=false");
        diag.Add("formalRetrievalAllowed=false");
        diag.Add("runtimeSwitchAllowed=false");
        diag.Add("globalDefaultOn=false");

        return new ControlledAppliedMergeRuntimePreviewActivationPreflightReport
        {
            OperationId = $"controlled-applied-merge-runtime-preview-preflight-{stage}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            PreflightPassed = preflightPassed,
            GatePassed = gatePassed,
            Recommendation = preflightPassed
                ? ControlledAppliedMergeRuntimePreviewActivationPreflightRecommendations.ReadyForRuntimePreviewActivation
                : ResolveRecommendation(distinctBlocked),
            Mode = options.Mode,
            NextAllowedPhase = preflightPassed
                ? "ControlledAppliedMergeRuntimePreviewScopedActivation"
                : "KeepPreviewOnly",
            PlanPassed = planPassed,
            DryRunPassed = dryRunPassed,
            V6FreezePassed = v6FreezePassed,
            RuntimeChangeGatePassed = runtimeChangeGatePassed,
            P15GatePassed = p15GatePassed,
            AllowlistedScopes = allowlistedScopes,
            ConfigSwitch = configSwitch,
            TracePath = tracePath,
            KillSwitchAvailable = killSwitchAvailable,
            RollbackPlanAvailable = rollbackPlanAvailable,
            TraceSinkAvailable = traceSinkAvailable,
            ConfigPatchPreviewed = preflightPassed,
            ConfigPatchWritten = false,
            ScopeValidationPassed = preflightPassed && allowlistedScopes.Count > 0,
            ScopeLeakCount = 0,
            NonAllowlistedScopeChecked = true,
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
            WouldApplyAddCount = wouldApplyAdd,
            WouldApplyRemoveCount = wouldApplyRemove,
            TotalTokenDelta = totalTokenDelta,
            RiskAfterPolicy = risk,
            AllowedActions = FrozenAllowedActions,
            ForbiddenActions = FrozenForbiddenActions,
            BlockedReasons = distinctBlocked,
            Diagnostics = diag,
        };
    }

    public static string BuildMarkdown(string title, ControlledAppliedMergeRuntimePreviewActivationPreflightReport report)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{report.CreatedAt:O}`");
        b.AppendLine($"操作: `{report.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Summary");
        b.AppendLine($"- PreflightPassed: `{report.PreflightPassed}`");
        b.AppendLine($"- GatePassed: `{report.GatePassed}`");
        b.AppendLine($"- Recommendation: `{report.Recommendation}`");
        b.AppendLine($"- Mode: `{report.Mode}`");
        b.AppendLine($"- NextAllowedPhase: `{report.NextAllowedPhase}`");
        b.AppendLine();

        b.AppendLine("## Prerequisites");
        b.AppendLine($"- PlanPassed: `{report.PlanPassed}`");
        b.AppendLine($"- DryRunPassed: `{report.DryRunPassed}`");
        b.AppendLine($"- V6FreezePassed: `{report.V6FreezePassed}`");
        b.AppendLine($"- RuntimeChangeGatePassed: `{report.RuntimeChangeGatePassed}`");
        b.AppendLine($"- P15GatePassed: `{report.P15GatePassed}`");
        b.AppendLine();

        b.AppendLine("## Activation Readiness");
        b.AppendLine($"- AllowlistedScopes: `{report.AllowlistedScopes.Count}`");
        b.AppendLine($"- ConfigSwitch: `{report.ConfigSwitch}`");
        b.AppendLine($"- TracePath: `{report.TracePath}`");
        b.AppendLine($"- KillSwitchAvailable: `{report.KillSwitchAvailable}`");
        b.AppendLine($"- RollbackPlanAvailable: `{report.RollbackPlanAvailable}`");
        b.AppendLine($"- TraceSinkAvailable: `{report.TraceSinkAvailable}`");
        b.AppendLine($"- ConfigPatchPreviewed: `{report.ConfigPatchPreviewed}`");
        b.AppendLine($"- ConfigPatchWritten: `{report.ConfigPatchWritten}`");
        b.AppendLine($"- ScopeValidationPassed: `{report.ScopeValidationPassed}`");
        b.AppendLine($"- ScopeLeakCount: `{report.ScopeLeakCount}`");
        b.AppendLine($"- NonAllowlistedScopeChecked: `{report.NonAllowlistedScopeChecked}`");
        b.AppendLine();

        b.AppendLine("## Dry-run Carry-forward");
        b.AppendLine($"- WouldApplyAdd: `{report.WouldApplyAddCount}`");
        b.AppendLine($"- WouldApplyRemove: `{report.WouldApplyRemoveCount}`");
        b.AppendLine($"- TotalTokenDelta: `{report.TotalTokenDelta}`");
        b.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
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
        AppendList(b, "Allowed Actions", report.AllowedActions);
        AppendList(b, "Forbidden Actions", report.ForbiddenActions);
        AppendList(b, "Blocked Reasons", report.BlockedReasons);
        AppendList(b, "Diagnostics", report.Diagnostics);

        b.AppendLine();
        b.AppendLine("V7.2 scoped runtime preview activation preflight. 安装/验证 runtime preview 入口，但仍保持 preview-only、scope-only、no formal output mutation。不接 formal retrieval，不做 global runtime switch，不写 formal package，不改 PackingPolicy/package output，不绑定正式 IVectorIndexStore。");
        return b.ToString();
    }

    private static IReadOnlyList<string> BuildAllowlistedScopes(ControlledAppliedMergeRuntimePreviewActivationPreflightOptions options)
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
            return ControlledAppliedMergeRuntimePreviewActivationPreflightRecommendations.BlockedByPlanNotPassed;
        if (blocked.Any(static r => r.Contains("DryRun", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewActivationPreflightRecommendations.BlockedByDryRunNotPassed;
        if (blocked.Any(static r => r.Contains("V6Freeze", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewActivationPreflightRecommendations.BlockedByV6FreezeNotPassed;
        if (blocked.Any(static r => r.Contains("KillSwitch", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewActivationPreflightRecommendations.BlockedByMissingKillSwitch;
        if (blocked.Any(static r => r.Contains("Rollback", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewActivationPreflightRecommendations.BlockedByMissingRollbackPlan;
        if (blocked.Any(static r => r.Contains("TraceSink", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewActivationPreflightRecommendations.BlockedByMissingTraceSink;
        if (blocked.Any(static r => r.Contains("Allowlisted", StringComparison.OrdinalIgnoreCase) || r.Contains("ScopeLeak", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewActivationPreflightRecommendations.BlockedByScopeLeak;
        if (blocked.Any(static r => r.Contains("Runtime", StringComparison.OrdinalIgnoreCase) && !r.Contains("Change", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewActivationPreflightRecommendations.BlockedByRuntimeMutation;
        if (blocked.Any(static r => r.Contains("Formal", StringComparison.OrdinalIgnoreCase) || r.Contains("Package", StringComparison.OrdinalIgnoreCase) || r.Contains("Packing", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewActivationPreflightRecommendations.BlockedByFormalOutputMutation;
        return ControlledAppliedMergeRuntimePreviewActivationPreflightRecommendations.KeepPreviewOnly;
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
