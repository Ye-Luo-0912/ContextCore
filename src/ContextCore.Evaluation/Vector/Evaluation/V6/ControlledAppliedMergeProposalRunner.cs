using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// V6.14 controlled applied merge proposal。这里只定义未来 applied merge preview 的准入合约，
/// 不应用 merge、不改变 formal selected set、package、PackingPolicy、runtime 或 vector binding。
/// </summary>
public sealed class ControlledAppliedMergeProposalRunner
{
    private static readonly string[] DefaultApprovalConditions =
    [
        "ApprovalMode must be ControlledAppliedMergePreview",
        "ApprovedBy is required in a later approval phase",
        "Reason is required in a later approval phase",
        "risk acknowledgement is required",
        "rollback acknowledgement is required",
        "kill switch acknowledgement is required",
        "scope acknowledgement is required",
        "observation acknowledgement is required",
        "approval expiry must be enforced",
        "revoked approval must fail closed",
        "explicit confirmation is required before any applied preview dry-run"
    ];

    private static readonly string[] DefaultObservationConditions =
    [
        "RequestCount",
        "BaselineSelectedSetCount",
        "ControlledAppliedPreviewSetCount",
        "PreviewAddCount",
        "PreviewRemoveCount",
        "AppliedAddCountMustRemainZeroUntilLaterGate",
        "AppliedRemoveCountMustRemainZeroUntilLaterGate",
        "TokenDeltaTotal",
        "TokenDeltaMax",
        "PriorityInversionCount",
        "SectionMismatchCount",
        "DroppedRequiredCandidateCount",
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
        "AppliedAddCount > 0 before applied preview gate",
        "AppliedRemoveCount > 0 before applied preview gate",
        "FormalPackageWritten=true",
        "PackageOutputChanged=true",
        "PackingPolicyChanged=true",
        "RuntimeMutated=true",
        "VectorStoreBindingChanged=true",
        "NonAllowlistedScopeLeakCount > 0",
        "PriorityInversionCount > 0",
        "SectionMismatchCount > 0",
        "DroppedRequiredCandidateCount > 0",
        "TokenDeltaTotal > configured budget",
        "TokenDeltaMax > configured budget",
        "TraceCompleteness < 100%",
        "KillSwitchUnavailable",
        "RollbackUnavailable"
    ];

    public ControlledAppliedMergeProposalReport BuildProposal(
        ControlledShadowMergeFreezeReport? promotionDecision,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ControlledAppliedMergeProposalOptions? options = null)
        => BuildReport(promotionDecision, runtimeChangeGate, options, gateMode: false);

    public ControlledAppliedMergeProposalReport BuildGate(
        ControlledShadowMergeFreezeReport? promotionDecision,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ControlledAppliedMergeProposalOptions? options = null)
        => BuildReport(promotionDecision, runtimeChangeGate, options, gateMode: true);

    public static string BuildMarkdown(string title, ControlledAppliedMergeProposalReport report)
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
        builder.AppendLine($"- RequiredApprovalMode: `{report.RequiredApprovalMode}`");
        builder.AppendLine($"- Mode: `{report.Mode}`");
        builder.AppendLine($"- AllowedMode: `{report.AllowedMode}`");
        builder.AppendLine($"- NextAllowedPhase: `{report.NextAllowedPhase}`");
        builder.AppendLine($"- PromotionDecisionGatePassed: `{report.PromotionDecisionGatePassed}`");
        builder.AppendLine($"- RuntimeChangeGatePassed: `{report.RuntimeChangeGatePassed}`");
        builder.AppendLine($"- ScopeCount: `{report.ScopeCount}`");
        builder.AppendLine($"- Max request/duration/errors: `{report.MaxRequestCount}` / `{report.MaxDurationMinutes}` / `{report.MaxErrorCount}`");
        builder.AppendLine($"- Max applied add/remove/token total/token max: `{report.MaxAppliedAddCount}` / `{report.MaxAppliedRemoveCount}` / `{report.MaxTokenDeltaTotal}` / `{report.MaxTokenDeltaPerSample}`");
        builder.AppendLine($"- Stable preview add/remove: `{report.StablePreviewAddCount}` / `{report.StablePreviewRemoveCount}`");
        builder.AppendLine($"- Applied add/remove: `{report.AppliedAddCount}` / `{report.AppliedRemoveCount}`");
        builder.AppendLine($"- ManualApprovalRequired/ApprovalPlanPresent: `{report.ManualApprovalRequired}` / `{report.ApprovalPlanPresent}`");
        builder.AppendLine($"- Rollback/KillSwitch present: `{report.RollbackPlanPresent}` / `{report.KillSwitchPlanPresent}`");
        builder.AppendLine($"- Risk/must-not/lifecycle: `{report.RiskAfterPolicy}` / `{report.MustNotHitRiskAfterPolicy}` / `{report.LifecycleRiskAfterPolicy}`");
        builder.AppendLine($"- Formal selected/formal output/formal package: `{report.FormalSelectedSetChanged}` / `{report.FormalOutputChanged}` / `{report.FormalPackageWritten}`");
        builder.AppendLine($"- Package/PackingPolicy/runtime/vector: `{report.PackageOutputChanged}` / `{report.PackingPolicyChanged}` / `{report.RuntimeMutated}` / `{report.VectorStoreBindingChanged}`");
        builder.AppendLine($"- AppliedMergeAllowed/FormalPackageWriteAllowed: `{report.AppliedMergeAllowed}` / `{report.FormalPackageWriteAllowed}`");
        builder.AppendLine($"- UseForRuntime/FormalRetrieval/RuntimeSwitch/ReadyForRuntimeSwitch: `{report.UseForRuntime}` / `{report.FormalRetrievalAllowed}` / `{report.RuntimeSwitchAllowed}` / `{report.ReadyForRuntimeSwitch}`");
        AppendList(builder, "SelectedScopes", report.SelectedScopes);
        AppendMap(builder, "RequiredGateSummary", report.RequiredGateSummary);
        AppendList(builder, "ScopeConditions", report.ScopeConditions);
        AppendList(builder, "LimitConditions", report.LimitConditions);
        AppendList(builder, "ApprovalConditions", report.ApprovalConditions);
        AppendList(builder, "RollbackConditions", report.RollbackConditions);
        AppendList(builder, "KillSwitchConditions", report.KillSwitchConditions);
        AppendList(builder, "ObservationConditions", report.ObservationConditions);
        AppendList(builder, "StopConditions", report.StopConditions);
        AppendList(builder, "AllowedActions", report.AllowedActions);
        AppendList(builder, "ForbiddenActions", report.ForbiddenActions);
        AppendList(builder, "BlockedReasons", report.BlockedReasons);
        builder.AppendLine();
        builder.AppendLine("This proposal only defines the future controlled applied merge preview contract. It does not apply add/remove, mutate the formal selected set, write a formal package, alter PackingPolicy/package output, switch runtime, or bind a vector store.");
        return builder.ToString();
    }

    private static ControlledAppliedMergeProposalReport BuildReport(
        ControlledShadowMergeFreezeReport? promotionDecision,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ControlledAppliedMergeProposalOptions? options,
        bool gateMode)
    {
        options ??= new ControlledAppliedMergeProposalOptions();
        var workspaceAllowlist = Normalize(options.WorkspaceAllowlist);
        var collectionAllowlist = Normalize(options.CollectionAllowlist);
        var evalScopeAllowlist = Normalize(options.EvalScopeAllowlist);
        var selectedScopes = BuildSelectedScopes(workspaceAllowlist, collectionAllowlist, evalScopeAllowlist);
        var rollbackPlan = Clean(options.RollbackPlan);
        var killSwitchPlan = Clean(options.KillSwitchPlan);
        var approvalConditions = Normalize(options.ApprovalConditions.Count == 0 ? DefaultApprovalConditions : options.ApprovalConditions);
        var observationConditions = Normalize(options.ObservationConditions.Count == 0 ? DefaultObservationConditions : options.ObservationConditions);
        var stopConditions = Normalize(options.StopConditions.Count == 0 ? DefaultStopConditions : options.StopConditions);

        var stablePreviewAdd = promotionDecision?.PreviewAddCountMax ?? 0;
        var stablePreviewRemove = promotionDecision?.PreviewRemoveCountMax ?? 0;
        var appliedAdd = Math.Max(promotionDecision?.AppliedAddCountMax ?? 0, options.SimulatedAppliedAddCount);
        var appliedRemove = Math.Max(promotionDecision?.AppliedRemoveCountMax ?? 0, options.SimulatedAppliedRemoveCount);
        var risk = Math.Max(promotionDecision?.RiskAfterPolicyMax ?? 0, options.RiskAfterPolicy);
        var mustNot = Math.Max(promotionDecision?.MustNotHitRiskAfterPolicyMax ?? 0, options.MustNotHitRiskAfterPolicy);
        var lifecycle = Math.Max(promotionDecision?.LifecycleRiskAfterPolicyMax ?? 0, options.LifecycleRiskAfterPolicy);
        var formalOutput = Math.Max(promotionDecision?.FormalOutputChangedMax ?? 0, options.FormalOutputChanged);
        var tokenTotal = Math.Max(promotionDecision?.TokenDeltaTotalMax ?? 0, options.SimulatedTokenDeltaTotal);
        var tokenMax = Math.Max(promotionDecision?.TokenDeltaMaxMax ?? 0, options.SimulatedTokenDeltaMax);
        var priorityInversion = Math.Max(promotionDecision?.PriorityInversionCountTotal ?? 0, options.PriorityInversionCount);
        var sectionMismatch = Math.Max(promotionDecision?.SectionMismatchCountTotal ?? 0, options.SectionMismatchCount);
        var droppedRequired = Math.Max(promotionDecision?.DroppedRequiredCandidateCountTotal ?? 0, options.DroppedRequiredCandidateCount);
        var formalSelectedSetChanged = (promotionDecision?.FormalSelectedSetChanged ?? false) || options.FormalSelectedSetChanged;
        var formalPackageWritten = (promotionDecision?.FormalPackageWritten ?? false) || options.FormalPackageWritten;
        var packageOutputChanged = (promotionDecision?.PackageOutputChanged ?? false) || options.PackageOutputChanged;
        var packingPolicyChanged = (promotionDecision?.PackingPolicyChanged ?? false) || options.PackingPolicyChanged;
        var runtimeMutated = (promotionDecision?.RuntimeMutated ?? false) || options.RuntimeMutated;
        var vectorStoreBindingChanged = (promotionDecision?.VectorStoreBindingChanged ?? false) || options.VectorStoreBindingChanged;
        var useForRuntime = (promotionDecision?.UseForRuntime ?? false) || options.UseForRuntime;
        var formalRetrievalAllowed = (promotionDecision?.FormalRetrievalAllowed ?? false) || options.FormalRetrievalAllowed;
        var runtimeSwitchAllowed = (promotionDecision?.RuntimeSwitchAllowed ?? false) || options.RuntimeSwitchAllowed;
        var readyForRuntimeSwitch = (promotionDecision?.ReadyForRuntimeSwitch ?? false) || options.ReadyForRuntimeSwitch;

        var blocked = new List<string>();
        if (!options.Enabled)
            blocked.Add("ControlledAppliedMergeProposalDisabled");
        if (!string.Equals(options.Mode, ControlledAppliedMergeProposalModes.ProposalOnly, StringComparison.OrdinalIgnoreCase))
            blocked.Add("UnsupportedProposalMode");
        if (promotionDecision is null ||
            !promotionDecision.PromotionDecisionPassed ||
            !string.Equals(promotionDecision.PromotionDecision, ControlledShadowMergePromotionDecisions.ReadyForControlledAppliedMergeProposal, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("ControlledShadowMergePromotionDecisionMissingOrNotPassed");
        }

        if (runtimeChangeGate is null || !runtimeChangeGate.Passed)
            blocked.Add("RuntimeChangeGateMissingOrNotPassed");
        if (selectedScopes.Count == 0)
            blocked.Add("SelectedScopeNotConfigured");
        if (options.MaxRequestCount <= 0 || options.MaxDurationMinutes <= 0 || options.MaxErrorCount < 0)
            blocked.Add("ExecutionLimitsMissing");
        if (options.MaxAppliedAddCount <= 0 || options.MaxAppliedRemoveCount <= 0 || options.MaxTokenDeltaTotal < 0 || options.MaxTokenDeltaPerSample < 0)
            blocked.Add("AppliedMergeLimitsMissing");
        if (stablePreviewAdd <= 0 || stablePreviewRemove <= 0)
            blocked.Add("StablePreviewDeltaMissing");
        if (stablePreviewAdd > options.MaxAppliedAddCount || stablePreviewRemove > options.MaxAppliedRemoveCount)
            blocked.Add("AppliedPreviewLimitExceeded");
        if (tokenTotal > options.MaxTokenDeltaTotal || tokenMax > options.MaxTokenDeltaPerSample)
            blocked.Add("TokenDeltaLimitExceeded");
        if ((promotionDecision?.ObservationRunCount ?? 0) < options.MinObservationRunCount ||
            (promotionDecision?.SampleObservationCount ?? 0) < options.MinSampleObservationCount)
        {
            blocked.Add("ObservationWindowTooSmall");
        }

        if (!options.ManualApprovalRequired || approvalConditions.Length == 0)
            blocked.Add("ApprovalPlanMissing");
        if (!string.Equals(options.RequiredApprovalMode, ControlledAppliedMergeApprovalModes.ControlledAppliedMergePreview, StringComparison.OrdinalIgnoreCase))
            blocked.Add("WrongApprovalMode");
        if (string.IsNullOrWhiteSpace(rollbackPlan))
            blocked.Add("RollbackPlanMissing");
        if (string.IsNullOrWhiteSpace(killSwitchPlan))
            blocked.Add("KillSwitchPlanMissing");
        if (observationConditions.Length == 0 || stopConditions.Length == 0)
            blocked.Add("ObservationPlanMissing");
        if (appliedAdd != 0 || appliedRemove != 0)
            blocked.Add("AppliedDeltaDetected");
        if (risk != 0 || mustNot != 0 || lifecycle != 0)
            blocked.Add("RiskDetected");
        if (priorityInversion != 0 || sectionMismatch != 0 || droppedRequired != 0)
            blocked.Add("TokenSectionPriorityGateViolation");
        if (formalOutput != 0 || formalSelectedSetChanged || formalPackageWritten || packageOutputChanged || packingPolicyChanged || runtimeMutated || vectorStoreBindingChanged)
            blocked.Add("RuntimeOrFormalInvariantChanged");
        if (useForRuntime || formalRetrievalAllowed || runtimeSwitchAllowed || readyForRuntimeSwitch)
            blocked.Add("RuntimeSwitchAttemptDetected");
        if (options.AllowAppliedMerge || options.AllowFormalSelectedSetChange || options.AllowFormalPackageWrite || options.AllowPackingPolicyMutation || options.AllowPackageOutputMutation || options.AllowRuntimeMutation || options.AllowVectorStoreBindingMutation)
            blocked.Add("ForbiddenActionAllowed");

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase).ToArray();
        var passed = distinctBlocked.Length == 0;
        return new ControlledAppliedMergeProposalReport
        {
            OperationId = $"controlled-applied-merge-proposal-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            ProposalPassed = passed,
            GatePassed = gateMode && passed,
            Recommendation = passed ? ControlledAppliedMergeProposalRecommendations.ReadyForControlledAppliedMergeDryRunGate : ResolveRecommendation(distinctBlocked),
            ProposalId = string.IsNullOrWhiteSpace(options.ProposalId) ? BuildStableProposalId(selectedScopes, options.ProfileName) : options.ProposalId.Trim(),
            Mode = options.Mode,
            AllowedMode = passed ? "ControlledAppliedMergeProposalOnly" : "KeepPreviewOnly",
            NextAllowedPhase = passed ? "ControlledAppliedMergeDryRunGate" : "KeepPreviewOnly",
            RequiredPreviousPhase = "V6.13 Controlled Shadow Merge Freeze / Promotion Decision",
            RequiredApprovalMode = options.RequiredApprovalMode,
            PromotionDecisionGatePassed = promotionDecision?.PromotionDecisionPassed == true,
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
            MaxAppliedAddCount = options.MaxAppliedAddCount,
            MaxAppliedRemoveCount = options.MaxAppliedRemoveCount,
            MaxTokenDeltaTotal = options.MaxTokenDeltaTotal,
            MaxTokenDeltaPerSample = options.MaxTokenDeltaPerSample,
            MinObservationRunCount = options.MinObservationRunCount,
            MinSampleObservationCount = options.MinSampleObservationCount,
            StablePreviewAddCount = stablePreviewAdd,
            StablePreviewRemoveCount = stablePreviewRemove,
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
            AppliedMergeAllowed = options.AllowAppliedMerge,
            FormalSelectedSetChangeAllowed = options.AllowFormalSelectedSetChange,
            FormalPackageWriteAllowed = options.AllowFormalPackageWrite,
            PackingPolicyMutationAllowed = options.AllowPackingPolicyMutation,
            PackageOutputMutationAllowed = options.AllowPackageOutputMutation,
            RuntimeMutationAllowed = options.AllowRuntimeMutation,
            VectorStoreBindingMutationAllowed = options.AllowVectorStoreBindingMutation,
            ManualApprovalRequired = options.ManualApprovalRequired,
            ApprovalPlanPresent = options.ManualApprovalRequired && approvalConditions.Length > 0,
            RollbackPlan = rollbackPlan,
            KillSwitchPlan = killSwitchPlan,
            RollbackPlanPresent = !string.IsNullOrWhiteSpace(rollbackPlan),
            KillSwitchPlanPresent = !string.IsNullOrWhiteSpace(killSwitchPlan),
            RequiredGateSummary = BuildGateSummary(promotionDecision, runtimeChangeGate),
            ScopeConditions = BuildScopeConditions(),
            LimitConditions = BuildLimitConditions(options),
            ApprovalConditions = approvalConditions,
            RollbackConditions = BuildRollbackConditions(),
            KillSwitchConditions = BuildKillSwitchConditions(),
            ObservationConditions = observationConditions,
            StopConditions = stopConditions,
            AllowedActions = passed ? ["controlled applied merge preview proposal review", "manual approval planning", "scope/limit review", "rollback and kill switch validation", "observation plan review"] : Array.Empty<string>(),
            ForbiddenActions = ["applied merge before explicit later gate", "formal selected set mutation", "formal package write", "PackingPolicy mutation", "package output mutation", "runtime switch", "formal retrieval enable", "formal IVectorIndexStore binding mutation", "global default-on", "non-allowlisted scope use"],
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
        ControlledShadowMergeFreezeReport? promotionDecision,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["V6.13 controlled shadow merge promotion decision"] = promotionDecision?.PromotionDecisionPassed == true ? "Passed" : "MissingOrFailed",
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

    private static IReadOnlyList<string> BuildLimitConditions(ControlledAppliedMergeProposalOptions options) =>
    [
        $"MaxRequestCount <= {options.MaxRequestCount}",
        $"MaxDurationMinutes <= {options.MaxDurationMinutes}",
        $"MaxErrorCount <= {options.MaxErrorCount}",
        $"MaxAppliedAddCount <= {options.MaxAppliedAddCount}",
        $"MaxAppliedRemoveCount <= {options.MaxAppliedRemoveCount}",
        $"MaxTokenDeltaTotal <= {options.MaxTokenDeltaTotal}",
        $"MaxTokenDeltaPerSample <= {options.MaxTokenDeltaPerSample}",
        $"MinObservationRunCount >= {options.MinObservationRunCount}",
        $"MinSampleObservationCount >= {options.MinSampleObservationCount}",
        "AppliedAddCount must remain zero until a later explicit applied-preview gate",
        "AppliedRemoveCount must remain zero until a later explicit applied-preview gate"
    ];

    private static IReadOnlyList<string> BuildRollbackConditions() =>
    [
        "rollback plan must be present before any controlled applied merge preview",
        "rollback must return selected scope to baseline",
        "rollback must preserve historical trace artifacts",
        "rollback must not write formal package or mutate package output"
    ];

    private static IReadOnlyList<string> BuildKillSwitchConditions() =>
    [
        "kill switch plan must be present before any controlled applied merge preview",
        "kill switch must fail closed to baseline",
        "kill switch must not affect non-allowlisted baseline requests",
        "kill switch state must be traceable in observation artifacts"
    ];

    private static string ResolveRecommendation(IReadOnlyList<string> blockedReasons)
    {
        if (blockedReasons.Contains("SelectedScopeNotConfigured", StringComparer.OrdinalIgnoreCase))
            return ControlledAppliedMergeProposalRecommendations.NeedsScopeConfiguration;
        if (blockedReasons.Any(static reason => reason.EndsWith("MissingOrNotPassed", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeProposalRecommendations.BlockedByMissingPromotionDecision;
        if (blockedReasons.Contains("ExecutionLimitsMissing", StringComparer.OrdinalIgnoreCase) ||
            blockedReasons.Contains("AppliedMergeLimitsMissing", StringComparer.OrdinalIgnoreCase) ||
            blockedReasons.Contains("StablePreviewDeltaMissing", StringComparer.OrdinalIgnoreCase) ||
            blockedReasons.Contains("AppliedPreviewLimitExceeded", StringComparer.OrdinalIgnoreCase) ||
            blockedReasons.Contains("TokenDeltaLimitExceeded", StringComparer.OrdinalIgnoreCase) ||
            blockedReasons.Contains("ObservationWindowTooSmall", StringComparer.OrdinalIgnoreCase))
        {
            return ControlledAppliedMergeProposalRecommendations.BlockedByMissingLimit;
        }

        if (blockedReasons.Contains("ApprovalPlanMissing", StringComparer.OrdinalIgnoreCase) ||
            blockedReasons.Contains("WrongApprovalMode", StringComparer.OrdinalIgnoreCase))
        {
            return ControlledAppliedMergeProposalRecommendations.BlockedByMissingApprovalPlan;
        }

        if (blockedReasons.Contains("RollbackPlanMissing", StringComparer.OrdinalIgnoreCase))
            return ControlledAppliedMergeProposalRecommendations.BlockedByMissingRollbackPlan;
        if (blockedReasons.Contains("KillSwitchPlanMissing", StringComparer.OrdinalIgnoreCase))
            return ControlledAppliedMergeProposalRecommendations.BlockedByMissingKillSwitch;
        if (blockedReasons.Contains("ObservationPlanMissing", StringComparer.OrdinalIgnoreCase))
            return ControlledAppliedMergeProposalRecommendations.BlockedByMissingObservationPlan;
        if (blockedReasons.Contains("RiskDetected", StringComparer.OrdinalIgnoreCase))
            return ControlledAppliedMergeProposalRecommendations.BlockedByRisk;
        if (blockedReasons.Contains("TokenSectionPriorityGateViolation", StringComparer.OrdinalIgnoreCase) ||
            blockedReasons.Contains("RuntimeOrFormalInvariantChanged", StringComparer.OrdinalIgnoreCase) ||
            blockedReasons.Contains("RuntimeSwitchAttemptDetected", StringComparer.OrdinalIgnoreCase) ||
            blockedReasons.Contains("AppliedDeltaDetected", StringComparer.OrdinalIgnoreCase) ||
            blockedReasons.Contains("ForbiddenActionAllowed", StringComparer.OrdinalIgnoreCase))
        {
            return ControlledAppliedMergeProposalRecommendations.BlockedByRuntimeInvariant;
        }

        return ControlledAppliedMergeProposalRecommendations.KeepPreviewOnly;
    }

    private static string BuildStableProposalId(IReadOnlyList<string> selectedScopes, string profileName)
    {
        var input = string.Join("|", selectedScopes) + "|" + profileName + "|controlled-applied-merge-preview";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return "camp-" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
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

public sealed class ControlledAppliedMergeProposalOptions
{
    public bool Enabled { get; init; } = true;
    public string Mode { get; init; } = ControlledAppliedMergeProposalModes.ProposalOnly;
    public string ProposalId { get; init; } = string.Empty;
    public string RequiredApprovalMode { get; init; } = ControlledAppliedMergeApprovalModes.ControlledAppliedMergePreview;
    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();
    public string ProfileName { get; init; } = "post-scoring-risk-gated-v1";
    public int MaxRequestCount { get; init; } = 120;
    public int MaxDurationMinutes { get; init; } = 30;
    public int MaxErrorCount { get; init; }
    public int MaxAppliedAddCount { get; init; } = 7;
    public int MaxAppliedRemoveCount { get; init; } = 7;
    public int MaxTokenDeltaTotal { get; init; } = 128;
    public int MaxTokenDeltaPerSample { get; init; } = 32;
    public int MinObservationRunCount { get; init; } = 10;
    public int MinSampleObservationCount { get; init; } = 120;
    public bool ManualApprovalRequired { get; init; } = true;
    public IReadOnlyList<string> ApprovalConditions { get; init; } = Array.Empty<string>();
    public string RollbackPlan { get; init; } = "Disable controlled applied merge preview and keep baseline formal selected set.";
    public string KillSwitchPlan { get; init; } = "Set controlled applied merge preview kill switch to disabled and fail closed to baseline.";
    public IReadOnlyList<string> ObservationConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> StopConditions { get; init; } = Array.Empty<string>();
    public int SimulatedAppliedAddCount { get; init; }
    public int SimulatedAppliedRemoveCount { get; init; }
    public int SimulatedTokenDeltaTotal { get; init; }
    public int SimulatedTokenDeltaMax { get; init; }
    public int PriorityInversionCount { get; init; }
    public int SectionMismatchCount { get; init; }
    public int DroppedRequiredCandidateCount { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public int FormalOutputChanged { get; init; }
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
public sealed class ControlledAppliedMergeProposalReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ProposalPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = ControlledAppliedMergeProposalRecommendations.KeepPreviewOnly;
    public string ProposalId { get; init; } = string.Empty;
    public string Mode { get; init; } = ControlledAppliedMergeProposalModes.ProposalOnly;
    public string AllowedMode { get; init; } = "KeepPreviewOnly";
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";
    public string RequiredPreviousPhase { get; init; } = string.Empty;
    public string RequiredApprovalMode { get; init; } = ControlledAppliedMergeApprovalModes.ControlledAppliedMergePreview;
    public bool PromotionDecisionGatePassed { get; init; }
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
    public int MaxAppliedAddCount { get; init; }
    public int MaxAppliedRemoveCount { get; init; }
    public int MaxTokenDeltaTotal { get; init; }
    public int MaxTokenDeltaPerSample { get; init; }
    public int MinObservationRunCount { get; init; }
    public int MinSampleObservationCount { get; init; }
    public int StablePreviewAddCount { get; init; }
    public int StablePreviewRemoveCount { get; init; }
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
    public bool AppliedMergeAllowed { get; init; }
    public bool FormalSelectedSetChangeAllowed { get; init; }
    public bool FormalPackageWriteAllowed { get; init; }
    public bool PackingPolicyMutationAllowed { get; init; }
    public bool PackageOutputMutationAllowed { get; init; }
    public bool RuntimeMutationAllowed { get; init; }
    public bool VectorStoreBindingMutationAllowed { get; init; }
    public bool ManualApprovalRequired { get; init; }
    public bool ApprovalPlanPresent { get; init; }
    public string RollbackPlan { get; init; } = string.Empty;
    public string KillSwitchPlan { get; init; } = string.Empty;
    public bool RollbackPlanPresent { get; init; }
    public bool KillSwitchPlanPresent { get; init; }
    public IReadOnlyDictionary<string, string> RequiredGateSummary { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> ScopeConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LimitConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ApprovalConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RollbackConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> KillSwitchConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ObservationConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> StopConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public static class ControlledAppliedMergeProposalModes
{
    public const string ProposalOnly = nameof(ProposalOnly);
}

public static class ControlledAppliedMergeApprovalModes
{
    public const string ControlledAppliedMergePreview = nameof(ControlledAppliedMergePreview);
}

public static class ControlledAppliedMergeProposalRecommendations
{
    public const string ReadyForControlledAppliedMergeDryRunGate = nameof(ReadyForControlledAppliedMergeDryRunGate);
    public const string NeedsScopeConfiguration = nameof(NeedsScopeConfiguration);
    public const string BlockedByMissingPromotionDecision = nameof(BlockedByMissingPromotionDecision);
    public const string BlockedByMissingLimit = nameof(BlockedByMissingLimit);
    public const string BlockedByMissingApprovalPlan = nameof(BlockedByMissingApprovalPlan);
    public const string BlockedByMissingRollbackPlan = nameof(BlockedByMissingRollbackPlan);
    public const string BlockedByMissingKillSwitch = nameof(BlockedByMissingKillSwitch);
    public const string BlockedByMissingObservationPlan = nameof(BlockedByMissingObservationPlan);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByRuntimeInvariant = nameof(BlockedByRuntimeInvariant);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}