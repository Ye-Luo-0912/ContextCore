using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// V6.8 controlled shadow merge proposal。只冻结未来 controlled merge preview 的准入合约；
/// 不应用 delta、不写 formal package、不改变 runtime / PackingPolicy / package output / vector binding。
/// </summary>
public sealed class ControlledShadowMergeProposalRunner
{
    private static readonly string[] DefaultObservationConditions =
    [
        "RequestCount",
        "BaselineSelectedSetCount",
        "PreviewMergedSetCount",
        "PreviewAddCount",
        "PreviewRemoveCount",
        "AppliedAddCountMustRemainZero",
        "AppliedRemoveCountMustRemainZero",
        "TokenDeltaTotal",
        "TokenDeltaMax",
        "PriorityInversionCount",
        "SectionMismatchCount",
        "RiskAfterPolicy",
        "MustNotHitRiskAfterPolicy",
        "LifecycleRiskAfterPolicy",
        "FormalOutputChanged",
        "FormalSelectedSetChanged",
        "FormalPackageWritten",
        "PackageOutputChanged",
        "PackingPolicyChanged",
        "RuntimeMutated",
        "VectorStoreBindingChanged",
        "NonAllowlistedScopeLeakCount",
        "KillSwitchTriggered",
        "RollbackVerified",
        "TraceCompleteness"
    ];

    private static readonly string[] DefaultStopConditions =
    [
        "RiskAfterPolicy > 0",
        "MustNotHitRiskAfterPolicy > 0",
        "LifecycleRiskAfterPolicy > 0",
        "FormalOutputChanged > 0",
        "FormalSelectedSetChanged=true",
        "AppliedAddCount > 0",
        "AppliedRemoveCount > 0",
        "FormalPackageWritten=true",
        "PackageOutputChanged=true",
        "PackingPolicyChanged=true",
        "RuntimeMutated=true",
        "VectorStoreBindingChanged=true",
        "NonAllowlistedScopeLeakCount > 0",
        "PriorityInversionCount > 0",
        "SectionMismatchCount > 0",
        "TokenDeltaTotal > configured budget",
        "TokenDeltaMax > configured budget",
        "TraceCompleteness < 100%",
        "KillSwitchUnavailable",
        "RollbackUnavailable"
    ];

    public ControlledShadowMergeProposalReport BuildProposal(
        SourceDiverseShadowAdapterValidationReport? v66Gate,
        ShadowCandidateMergePreviewReport? v67Gate,
        ShadowCandidateMergePreviewObservationReport? observationGate,
        ShadowMergeStabilityFreezeReport? promotionDecision,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ControlledShadowMergeProposalOptions? options = null)
        => BuildReport(v66Gate, v67Gate, observationGate, promotionDecision, runtimeChangeGate, options, gateMode: false);

    public ControlledShadowMergeProposalReport BuildGate(
        SourceDiverseShadowAdapterValidationReport? v66Gate,
        ShadowCandidateMergePreviewReport? v67Gate,
        ShadowCandidateMergePreviewObservationReport? observationGate,
        ShadowMergeStabilityFreezeReport? promotionDecision,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ControlledShadowMergeProposalOptions? options = null)
        => BuildReport(v66Gate, v67Gate, observationGate, promotionDecision, runtimeChangeGate, options, gateMode: true);

    public static string BuildMarkdown(string title, ControlledShadowMergeProposalReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.CreatedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine($"- ProposalPassed: `{report.ProposalPassed}`");
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- ProposalId: `{report.ProposalId}`");
        builder.AppendLine($"- Mode: `{report.Mode}`");
        builder.AppendLine($"- AllowedMode: `{report.AllowedMode}`");
        builder.AppendLine($"- NextAllowedPhase: `{report.NextAllowedPhase}`");
        builder.AppendLine($"- Gate V6.6/V6.7/observation/promotion/runtime: `{report.V66GatePassed}` / `{report.V67GatePassed}` / `{report.ObservationGatePassed}` / `{report.PromotionDecisionPassed}` / `{report.RuntimeChangeGatePassed}`");
        builder.AppendLine($"- ScopeCount: `{report.ScopeCount}`");
        builder.AppendLine($"- Max request/duration/errors: `{report.MaxRequestCount}` / `{report.MaxDurationMinutes}` / `{report.MaxErrorCount}`");
        builder.AppendLine($"- Max preview add/remove/token total/token max: `{report.MaxPreviewAddCount}` / `{report.MaxPreviewRemoveCount}` / `{report.MaxTokenDeltaTotal}` / `{report.MaxTokenDeltaPerSample}`");
        builder.AppendLine($"- Minimum observation runs/samples: `{report.MinObservationRunCount}` / `{report.MinSampleObservationCount}`");
        builder.AppendLine($"- Observed preview add/remove: `{report.PreviewAddCount}` / `{report.PreviewRemoveCount}`");
        builder.AppendLine($"- Applied add/remove: `{report.AppliedAddCount}` / `{report.AppliedRemoveCount}`");
        builder.AppendLine($"- Risk/must-not/lifecycle: `{report.RiskAfterPolicy}` / `{report.MustNotHitRiskAfterPolicy}` / `{report.LifecycleRiskAfterPolicy}`");
        builder.AppendLine($"- Formal/package/policy/runtime/vector: `{report.FormalOutputChanged}` / `{report.PackageOutputChanged}` / `{report.PackingPolicyChanged}` / `{report.RuntimeMutated}` / `{report.VectorStoreBindingChanged}`");
        builder.AppendLine($"- UseForRuntime/FormalRetrieval/RuntimeSwitch/ReadyForRuntimeSwitch: `{report.UseForRuntime}` / `{report.FormalRetrievalAllowed}` / `{report.RuntimeSwitchAllowed}` / `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- RollbackPlanPresent: `{report.RollbackPlanPresent}`");
        builder.AppendLine($"- KillSwitchPlanPresent: `{report.KillSwitchPlanPresent}`");
        AppendList(builder, "SelectedScopes", report.SelectedScopes);
        AppendMap(builder, "RequiredGateSummary", report.RequiredGateSummary);
        AppendList(builder, "ScopeConditions", report.ScopeConditions);
        AppendList(builder, "LimitConditions", report.LimitConditions);
        AppendList(builder, "GateConditions", report.GateConditions);
        AppendList(builder, "RollbackConditions", report.RollbackConditions);
        AppendList(builder, "KillSwitchConditions", report.KillSwitchConditions);
        AppendList(builder, "ObservationConditions", report.ObservationConditions);
        AppendList(builder, "StopConditions", report.StopConditions);
        AppendList(builder, "AllowedActions", report.AllowedActions);
        AppendList(builder, "ForbiddenActions", report.ForbiddenActions);
        AppendList(builder, "BlockedReasons", report.BlockedReasons);
        builder.AppendLine();
        builder.AppendLine("This proposal only defines the future controlled shadow merge preview contract. It does not apply add/remove, mutate the formal selected set, write a formal package, alter PackingPolicy/package output, switch runtime, or bind a vector store.");
        return builder.ToString();
    }

    private static ControlledShadowMergeProposalReport BuildReport(
        SourceDiverseShadowAdapterValidationReport? v66Gate,
        ShadowCandidateMergePreviewReport? v67Gate,
        ShadowCandidateMergePreviewObservationReport? observationGate,
        ShadowMergeStabilityFreezeReport? promotionDecision,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ControlledShadowMergeProposalOptions? options,
        bool gateMode)
    {
        options ??= new ControlledShadowMergeProposalOptions();
        var workspaceAllowlist = Normalize(options.WorkspaceAllowlist);
        var collectionAllowlist = Normalize(options.CollectionAllowlist);
        var evalScopeAllowlist = Normalize(options.EvalScopeAllowlist);
        var selectedScopes = BuildSelectedScopes(workspaceAllowlist, collectionAllowlist, evalScopeAllowlist);
        var rollbackPlan = Clean(options.RollbackPlan);
        var killSwitchPlan = Clean(options.KillSwitchPlan);
        var observationConditions = Normalize(options.ObservationConditions.Count == 0 ? DefaultObservationConditions : options.ObservationConditions);
        var stopConditions = Normalize(options.StopConditions.Count == 0 ? DefaultStopConditions : options.StopConditions);

        var previewAdd = promotionDecision?.PreviewAddCountMax ?? observationGate?.PreviewAddCountMax ?? v67Gate?.PreviewAddCount ?? 0;
        var previewRemove = promotionDecision?.PreviewRemoveCountMax ?? observationGate?.PreviewRemoveCountMax ?? v67Gate?.PreviewRemoveCount ?? 0;
        var appliedAdd = promotionDecision?.AppliedAddCountMax ?? observationGate?.AppliedAddCountMax ?? v67Gate?.AppliedAddCount ?? 0;
        var appliedRemove = promotionDecision?.AppliedRemoveCountMax ?? observationGate?.AppliedRemoveCountMax ?? v67Gate?.AppliedRemoveCount ?? 0;
        var risk = promotionDecision?.RiskAfterPolicyMax ?? observationGate?.RiskAfterPolicyMax ?? v67Gate?.RiskAfterPolicy ?? 0;
        var mustNot = promotionDecision?.MustNotHitRiskAfterPolicyMax ?? observationGate?.MustNotHitRiskAfterPolicyMax ?? v67Gate?.MustNotHitRiskAfterPolicy ?? 0;
        var lifecycle = promotionDecision?.LifecycleRiskAfterPolicyMax ?? observationGate?.LifecycleRiskAfterPolicyMax ?? v67Gate?.LifecycleRiskAfterPolicy ?? 0;
        var formalOutput = promotionDecision?.FormalOutputChangedMax ?? observationGate?.FormalOutputChangedMax ?? v67Gate?.FormalOutputChanged ?? 0;
        var tokenTotal = promotionDecision?.TokenDeltaTotalMax ?? observationGate?.TokenDeltaTotalMax ?? v67Gate?.TokenDeltaTotal ?? 0;
        var tokenMax = promotionDecision?.TokenDeltaMaxMax ?? observationGate?.TokenDeltaMaxMax ?? v67Gate?.TokenDeltaMax ?? 0;

        var formalSelectedSetChanged = (promotionDecision?.FormalSelectedSetChanged ?? false) || (observationGate?.FormalSelectedSetChanged ?? false) || (v67Gate?.FormalSelectedSetChanged ?? false) || options.FormalSelectedSetChanged;
        var formalPackageWritten = (promotionDecision?.FormalPackageWritten ?? false) || (observationGate?.FormalPackageWritten ?? false) || (v67Gate?.FormalPackageWritten ?? false) || options.FormalPackageWritten;
        var packageOutputChanged = (promotionDecision?.PackageOutputChanged ?? false) || (observationGate?.PackageOutputChanged ?? false) || (v67Gate?.PackageOutputChanged ?? false) || options.PackageOutputChanged;
        var packingPolicyChanged = (promotionDecision?.PackingPolicyChanged ?? false) || (observationGate?.PackingPolicyChanged ?? false) || (v67Gate?.PackingPolicyChanged ?? false) || options.PackingPolicyChanged;
        var runtimeMutated = (promotionDecision?.RuntimeMutated ?? false) || (observationGate?.RuntimeMutated ?? false) || (v67Gate?.RuntimeMutated ?? false) || options.RuntimeMutated;
        var vectorStoreBindingChanged = (promotionDecision?.VectorStoreBindingChanged ?? false) || (observationGate?.VectorStoreBindingChanged ?? false) || (v67Gate?.VectorStoreBindingChanged ?? false) || options.VectorStoreBindingChanged;
        var useForRuntime = (promotionDecision?.UseForRuntime ?? false) || (observationGate?.UseForRuntime ?? false) || (v67Gate?.UseForRuntime ?? false) || options.UseForRuntime;
        var formalRetrievalAllowed = (promotionDecision?.FormalRetrievalAllowed ?? false) || (observationGate?.FormalRetrievalAllowed ?? false) || (v67Gate?.FormalRetrievalAllowed ?? false) || options.FormalRetrievalAllowed;
        var runtimeSwitchAllowed = (promotionDecision?.RuntimeSwitchAllowed ?? false) || (observationGate?.RuntimeSwitchAllowed ?? false) || (v67Gate?.RuntimeSwitchAllowed ?? false) || options.RuntimeSwitchAllowed;
        var readyForRuntimeSwitch = (promotionDecision?.ReadyForRuntimeSwitch ?? false) || (observationGate?.ReadyForRuntimeSwitch ?? false) || (v67Gate?.ReadyForRuntimeSwitch ?? false) || options.ReadyForRuntimeSwitch;

        var blocked = new List<string>();
        if (!options.Enabled)
            blocked.Add("ControlledShadowMergeProposalDisabled");
        if (!string.Equals(options.Mode, ControlledShadowMergeProposalModes.ProposalOnly, StringComparison.OrdinalIgnoreCase))
            blocked.Add("UnsupportedProposalMode");
        if (v66Gate is null || !v66Gate.GatePassed)
            blocked.Add("V66GateMissingOrNotPassed");
        if (v67Gate is null || !v67Gate.GatePassed)
            blocked.Add("V67GateMissingOrNotPassed");
        if (observationGate is null || !observationGate.GatePassed)
            blocked.Add("ObservationGateMissingOrNotPassed");
        if (promotionDecision is null ||
            !promotionDecision.PromotionDecisionPassed ||
            !string.Equals(promotionDecision.PromotionDecision, ShadowMergePromotionDecisions.ReadyForControlledMergeProposal, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("ShadowMergePromotionDecisionMissingOrNotPassed");
        }

        if (runtimeChangeGate is null || !runtimeChangeGate.Passed)
            blocked.Add("RuntimeChangeGateMissingOrNotPassed");
        if (selectedScopes.Count == 0)
            blocked.Add("SelectedScopeNotConfigured");
        if (options.MaxRequestCount <= 0 || options.MaxDurationMinutes <= 0 || options.MaxErrorCount < 0)
            blocked.Add("ExecutionLimitsMissing");
        if (options.MaxPreviewAddCount <= 0 || options.MaxPreviewRemoveCount <= 0 || options.MaxTokenDeltaTotal < 0 || options.MaxTokenDeltaPerSample < 0)
            blocked.Add("MergeLimitsMissing");
        if (previewAdd <= 0 || previewRemove <= 0)
            blocked.Add("PreviewDeltaMissing");
        if (previewAdd > options.MaxPreviewAddCount || previewRemove > options.MaxPreviewRemoveCount)
            blocked.Add("PreviewDeltaLimitExceeded");
        if (tokenTotal > options.MaxTokenDeltaTotal || tokenMax > options.MaxTokenDeltaPerSample)
            blocked.Add("TokenDeltaLimitExceeded");
        if ((promotionDecision?.ObservationRunCount ?? observationGate?.ObservationRunCount ?? 0) < options.MinObservationRunCount ||
            (promotionDecision?.SampleObservationCount ?? observationGate?.SampleObservationCount ?? 0) < options.MinSampleObservationCount)
        {
            blocked.Add("ObservationWindowTooSmall");
        }

        if (string.IsNullOrWhiteSpace(rollbackPlan))
            blocked.Add("RollbackPlanMissing");
        if (string.IsNullOrWhiteSpace(killSwitchPlan))
            blocked.Add("KillSwitchPlanMissing");
        if (observationConditions.Length == 0)
            blocked.Add("ObservationConditionsMissing");
        if (stopConditions.Length == 0)
            blocked.Add("StopConditionsMissing");
        if (appliedAdd != 0 || appliedRemove != 0)
            blocked.Add("AppliedDeltaDetected");
        if (risk != 0 || mustNot != 0 || lifecycle != 0)
            blocked.Add("RiskDetected");
        if (formalOutput != 0 || formalSelectedSetChanged || formalPackageWritten || packageOutputChanged || packingPolicyChanged || runtimeMutated || vectorStoreBindingChanged)
            blocked.Add("RuntimeOrFormalInvariantChanged");
        if (useForRuntime || formalRetrievalAllowed || runtimeSwitchAllowed || readyForRuntimeSwitch)
            blocked.Add("RuntimeSwitchAttemptDetected");
        if (options.AllowAppliedMerge || options.AllowFormalSelectedSetChange || options.AllowFormalPackageWrite || options.AllowPackingPolicyMutation || options.AllowPackageOutputMutation || options.AllowRuntimeMutation || options.AllowVectorStoreBindingMutation)
            blocked.Add("ForbiddenActionAllowed");

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase).ToArray();
        var passed = distinctBlocked.Length == 0;
        return new ControlledShadowMergeProposalReport
        {
            OperationId = $"controlled-shadow-merge-proposal-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            ProposalPassed = passed,
            GatePassed = gateMode && passed,
            Recommendation = passed ? ControlledShadowMergeProposalRecommendations.ReadyForControlledMergePreviewPlan : ResolveRecommendation(distinctBlocked),
            ProposalId = string.IsNullOrWhiteSpace(options.ProposalId) ? BuildStableProposalId(selectedScopes, options.ProfileName) : options.ProposalId.Trim(),
            Mode = options.Mode,
            AllowedMode = passed ? "ControlledShadowMergePreviewProposalOnly" : "PreviewOnly",
            NextAllowedPhase = passed ? "ControlledMergePreviewPlan" : "KeepPreviewOnly",
            V66GatePassed = v66Gate?.GatePassed == true,
            V67GatePassed = v67Gate?.GatePassed == true,
            ObservationGatePassed = observationGate?.GatePassed == true,
            PromotionDecisionPassed = promotionDecision?.PromotionDecisionPassed == true,
            RuntimeChangeGatePassed = runtimeChangeGate?.Passed == true,
            SelectedScopes = selectedScopes,
            ScopeCount = selectedScopes.Count,
            WorkspaceAllowlist = workspaceAllowlist,
            CollectionAllowlist = collectionAllowlist,
            EvalScopeAllowlist = evalScopeAllowlist,
            ProfileName = options.ProfileName,
            MaxRequestCount = options.MaxRequestCount,
            MaxDurationMinutes = options.MaxDurationMinutes,
            MaxErrorCount = options.MaxErrorCount,
            MaxPreviewAddCount = options.MaxPreviewAddCount,
            MaxPreviewRemoveCount = options.MaxPreviewRemoveCount,
            MaxTokenDeltaTotal = options.MaxTokenDeltaTotal,
            MaxTokenDeltaPerSample = options.MaxTokenDeltaPerSample,
            MinObservationRunCount = options.MinObservationRunCount,
            MinSampleObservationCount = options.MinSampleObservationCount,
            PreviewAddCount = previewAdd,
            PreviewRemoveCount = previewRemove,
            AppliedAddCount = appliedAdd,
            AppliedRemoveCount = appliedRemove,
            RiskAfterPolicy = risk,
            MustNotHitRiskAfterPolicy = mustNot,
            LifecycleRiskAfterPolicy = lifecycle,
            FormalOutputChanged = formalOutput,
            FormalSelectedSetChanged = formalSelectedSetChanged,
            FormalPackageWritten = formalPackageWritten,
            PackageOutputChanged = packageOutputChanged,
            PackingPolicyChanged = packingPolicyChanged,
            RuntimeMutated = runtimeMutated,
            VectorStoreBindingChanged = vectorStoreBindingChanged,
            UseForRuntime = useForRuntime,
            FormalRetrievalAllowed = formalRetrievalAllowed,
            RuntimeSwitchAllowed = runtimeSwitchAllowed,
            ReadyForRuntimeSwitch = readyForRuntimeSwitch,
            RollbackPlan = rollbackPlan,
            KillSwitchPlan = killSwitchPlan,
            RollbackPlanPresent = !string.IsNullOrWhiteSpace(rollbackPlan),
            KillSwitchPlanPresent = !string.IsNullOrWhiteSpace(killSwitchPlan),
            RequiredGateSummary = BuildGateSummary(v66Gate, v67Gate, observationGate, promotionDecision, runtimeChangeGate),
            ScopeConditions = BuildScopeConditions(),
            LimitConditions = BuildLimitConditions(options),
            GateConditions = BuildGateConditions(),
            RollbackConditions = BuildRollbackConditions(),
            KillSwitchConditions = BuildKillSwitchConditions(),
            ObservationConditions = observationConditions,
            StopConditions = stopConditions,
            AllowedActions = passed
                ? ["controlled shadow merge preview planning", "scope/limit review", "rollback and kill switch validation", "observation plan review"]
                : Array.Empty<string>(),
            ForbiddenActions =
            [
                "applied merge",
                "formal selected set mutation",
                "formal package write",
                "PackingPolicy mutation",
                "package output mutation",
                "runtime switch",
                "formal retrieval enable",
                "formal IVectorIndexStore binding mutation",
                "global default-on",
                "non-allowlisted scope use"
            ],
            BlockedReasons = distinctBlocked
        };
    }

    private static IReadOnlyList<string> BuildSelectedScopes(
        IReadOnlyList<string> workspaceAllowlist,
        IReadOnlyList<string> collectionAllowlist,
        IReadOnlyList<string> evalScopeAllowlist)
    {
        if (workspaceAllowlist.Count == 0 || collectionAllowlist.Count == 0 || evalScopeAllowlist.Count == 0)
            return Array.Empty<string>();

        var scopes = new List<string>(workspaceAllowlist.Count * collectionAllowlist.Count * evalScopeAllowlist.Count);
        foreach (var workspace in workspaceAllowlist)
        foreach (var collection in collectionAllowlist)
        foreach (var evalScope in evalScopeAllowlist)
            scopes.Add($"{workspace}/{collection}/{evalScope}");

        return scopes.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyDictionary<string, string> BuildGateSummary(
        SourceDiverseShadowAdapterValidationReport? v66Gate,
        ShadowCandidateMergePreviewReport? v67Gate,
        ShadowCandidateMergePreviewObservationReport? observationGate,
        ShadowMergeStabilityFreezeReport? promotionDecision,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["V6.6 source-diverse shadow adapter validation"] = v66Gate?.GatePassed == true ? "Passed" : "MissingOrFailed",
            ["V6.7 shadow candidate merge preview"] = v67Gate?.GatePassed == true ? "Passed" : "MissingOrFailed",
            ["V6.7 shadow merge observation"] = observationGate?.GatePassed == true ? "Passed" : "MissingOrFailed",
            ["Shadow merge promotion decision"] = promotionDecision?.PromotionDecisionPassed == true ? "Passed" : "MissingOrFailed",
            ["Runtime change gate"] = runtimeChangeGate?.Passed == true ? "Passed" : "MissingOrFailed"
        };

    private static IReadOnlyList<string> BuildScopeConditions() =>
    [
        "workspace allowlist must be explicit",
        "collection allowlist must be explicit",
        "eval scope allowlist must be explicit",
        "non-allowlisted scope must remain baseline",
        "non-allowlisted scope leak count must remain zero",
        "global default-on is forbidden"
    ];

    private static IReadOnlyList<string> BuildLimitConditions(ControlledShadowMergeProposalOptions options) =>
    [
        $"MaxRequestCount <= {options.MaxRequestCount}",
        $"MaxDurationMinutes <= {options.MaxDurationMinutes}",
        $"MaxErrorCount <= {options.MaxErrorCount}",
        $"MaxPreviewAddCount <= {options.MaxPreviewAddCount}",
        $"MaxPreviewRemoveCount <= {options.MaxPreviewRemoveCount}",
        $"MaxTokenDeltaTotal <= {options.MaxTokenDeltaTotal}",
        $"MaxTokenDeltaPerSample <= {options.MaxTokenDeltaPerSample}",
        $"MinObservationRunCount >= {options.MinObservationRunCount}",
        $"MinSampleObservationCount >= {options.MinSampleObservationCount}"
    ];

    private static IReadOnlyList<string> BuildGateConditions() =>
    [
        "V6.6 gate passed",
        "V6.7 preview gate passed",
        "V6.7 observation gate passed",
        "shadow merge promotion decision passed",
        "runtime-change gate passed",
        "risk/mustNot/lifecycle remain zero",
        "formal/package/PackingPolicy/runtime/vector binding invariants unchanged"
    ];

    private static IReadOnlyList<string> BuildRollbackConditions() =>
    [
        "rollback plan must be present before any controlled preview route",
        "rollback must return selected scope to baseline",
        "rollback must not delete historical trace artifacts",
        "rollback must not write formal package or mutate package output"
    ];

    private static IReadOnlyList<string> BuildKillSwitchConditions() =>
    [
        "kill switch plan must be present before any controlled preview route",
        "kill switch must fail closed to baseline",
        "kill switch must not affect non-allowlisted baseline requests",
        "kill switch state must be traceable in observation artifacts"
    ];

    private static string ResolveRecommendation(IReadOnlyList<string> blockedReasons)
    {
        if (blockedReasons.Contains("SelectedScopeNotConfigured", StringComparer.OrdinalIgnoreCase))
            return ControlledShadowMergeProposalRecommendations.NeedsScopeConfiguration;
        if (blockedReasons.Any(static reason => reason.EndsWith("MissingOrNotPassed", StringComparison.OrdinalIgnoreCase)))
            return ControlledShadowMergeProposalRecommendations.BlockedByMissingGate;
        if (blockedReasons.Contains("ExecutionLimitsMissing", StringComparer.OrdinalIgnoreCase) ||
            blockedReasons.Contains("MergeLimitsMissing", StringComparer.OrdinalIgnoreCase) ||
            blockedReasons.Contains("PreviewDeltaLimitExceeded", StringComparer.OrdinalIgnoreCase) ||
            blockedReasons.Contains("TokenDeltaLimitExceeded", StringComparer.OrdinalIgnoreCase) ||
            blockedReasons.Contains("ObservationWindowTooSmall", StringComparer.OrdinalIgnoreCase))
        {
            return ControlledShadowMergeProposalRecommendations.BlockedByMissingLimit;
        }

        if (blockedReasons.Contains("RollbackPlanMissing", StringComparer.OrdinalIgnoreCase))
            return ControlledShadowMergeProposalRecommendations.BlockedByMissingRollbackPlan;
        if (blockedReasons.Contains("KillSwitchPlanMissing", StringComparer.OrdinalIgnoreCase))
            return ControlledShadowMergeProposalRecommendations.BlockedByMissingKillSwitch;
        if (blockedReasons.Contains("ObservationConditionsMissing", StringComparer.OrdinalIgnoreCase) ||
            blockedReasons.Contains("StopConditionsMissing", StringComparer.OrdinalIgnoreCase))
        {
            return ControlledShadowMergeProposalRecommendations.BlockedByMissingObservationPlan;
        }

        if (blockedReasons.Contains("RiskDetected", StringComparer.OrdinalIgnoreCase))
            return ControlledShadowMergeProposalRecommendations.BlockedByRisk;
        if (blockedReasons.Contains("RuntimeOrFormalInvariantChanged", StringComparer.OrdinalIgnoreCase) ||
            blockedReasons.Contains("RuntimeSwitchAttemptDetected", StringComparer.OrdinalIgnoreCase) ||
            blockedReasons.Contains("AppliedDeltaDetected", StringComparer.OrdinalIgnoreCase) ||
            blockedReasons.Contains("ForbiddenActionAllowed", StringComparer.OrdinalIgnoreCase))
        {
            return ControlledShadowMergeProposalRecommendations.BlockedByRuntimeInvariant;
        }

        return ControlledShadowMergeProposalRecommendations.KeepPreviewOnly;
    }

    private static string BuildStableProposalId(IReadOnlyList<string> selectedScopes, string profileName)
    {
        var input = string.Join("|", selectedScopes) + "|" + profileName;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return "csm-" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static string[] Normalize(IReadOnlyList<string> values) =>
        values
            .Select(Clean)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string Clean(string? value) => (value ?? string.Empty).Trim();

    private static void AppendList(StringBuilder builder, string title, IReadOnlyList<string> items)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (items.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var item in items)
            builder.AppendLine($"- `{item}`");
    }

    private static void AppendMap(StringBuilder builder, string title, IReadOnlyDictionary<string, string> items)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (items.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var item in items.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
            builder.AppendLine($"- `{item.Key}`: `{item.Value}`");
    }
}

public sealed class ControlledShadowMergeProposalOptions
{
    public bool Enabled { get; init; } = true;
    public string Mode { get; init; } = ControlledShadowMergeProposalModes.ProposalOnly;
    public string ProposalId { get; init; } = string.Empty;
    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();
    public string ProfileName { get; init; } = "post-scoring-risk-gated-v1";
    public int MaxRequestCount { get; init; } = 120;
    public int MaxDurationMinutes { get; init; } = 30;
    public int MaxErrorCount { get; init; }
    public int MaxPreviewAddCount { get; init; } = 10;
    public int MaxPreviewRemoveCount { get; init; } = 10;
    public int MaxTokenDeltaTotal { get; init; } = 128;
    public int MaxTokenDeltaPerSample { get; init; } = 32;
    public int MinObservationRunCount { get; init; } = 10;
    public int MinSampleObservationCount { get; init; } = 120;
    public string RollbackPlan { get; init; } = "Disable controlled shadow merge preview and keep the baseline formal selected set.";
    public string KillSwitchPlan { get; init; } = "Set controlled shadow merge preview kill switch to disabled and fail closed to baseline.";
    public IReadOnlyList<string> ObservationConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> StopConditions { get; init; } = Array.Empty<string>();
    public bool UseForRuntime { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public bool FormalSelectedSetChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool AllowAppliedMerge { get; init; }
    public bool AllowFormalSelectedSetChange { get; init; }
    public bool AllowFormalPackageWrite { get; init; }
    public bool AllowPackingPolicyMutation { get; init; }
    public bool AllowPackageOutputMutation { get; init; }
    public bool AllowRuntimeMutation { get; init; }
    public bool AllowVectorStoreBindingMutation { get; init; }
}

public sealed class ControlledShadowMergeProposalReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ProposalPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = ControlledShadowMergeProposalRecommendations.KeepPreviewOnly;
    public string ProposalId { get; init; } = string.Empty;
    public string Mode { get; init; } = ControlledShadowMergeProposalModes.ProposalOnly;
    public string AllowedMode { get; init; } = "PreviewOnly";
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";
    public bool V66GatePassed { get; init; }
    public bool V67GatePassed { get; init; }
    public bool ObservationGatePassed { get; init; }
    public bool PromotionDecisionPassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }
    public IReadOnlyList<string> SelectedScopes { get; init; } = Array.Empty<string>();
    public int ScopeCount { get; init; }
    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();
    public string ProfileName { get; init; } = string.Empty;
    public int MaxRequestCount { get; init; }
    public int MaxDurationMinutes { get; init; }
    public int MaxErrorCount { get; init; }
    public int MaxPreviewAddCount { get; init; }
    public int MaxPreviewRemoveCount { get; init; }
    public int MaxTokenDeltaTotal { get; init; }
    public int MaxTokenDeltaPerSample { get; init; }
    public int MinObservationRunCount { get; init; }
    public int MinSampleObservationCount { get; init; }
    public int PreviewAddCount { get; init; }
    public int PreviewRemoveCount { get; init; }
    public int AppliedAddCount { get; init; }
    public int AppliedRemoveCount { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public int FormalOutputChanged { get; init; }
    public bool FormalSelectedSetChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool UseForRuntime { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public string RollbackPlan { get; init; } = string.Empty;
    public string KillSwitchPlan { get; init; } = string.Empty;
    public bool RollbackPlanPresent { get; init; }
    public bool KillSwitchPlanPresent { get; init; }
    public IReadOnlyDictionary<string, string> RequiredGateSummary { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> ScopeConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LimitConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> GateConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RollbackConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> KillSwitchConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ObservationConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> StopConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public static class ControlledShadowMergeProposalModes
{
    public const string ProposalOnly = nameof(ProposalOnly);
}

public static class ControlledShadowMergeProposalRecommendations
{
    public const string ReadyForControlledMergePreviewPlan = nameof(ReadyForControlledMergePreviewPlan);
    public const string NeedsScopeConfiguration = nameof(NeedsScopeConfiguration);
    public const string BlockedByMissingGate = nameof(BlockedByMissingGate);
    public const string BlockedByMissingLimit = nameof(BlockedByMissingLimit);
    public const string BlockedByMissingRollbackPlan = nameof(BlockedByMissingRollbackPlan);
    public const string BlockedByMissingKillSwitch = nameof(BlockedByMissingKillSwitch);
    public const string BlockedByMissingObservationPlan = nameof(BlockedByMissingObservationPlan);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByRuntimeInvariant = nameof(BlockedByRuntimeInvariant);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}
