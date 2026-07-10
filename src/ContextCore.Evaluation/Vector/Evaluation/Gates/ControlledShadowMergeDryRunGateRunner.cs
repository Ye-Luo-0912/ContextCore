using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// V6.10 controlled shadow merge dry-run gate。把 proposal 中冻结的 scope、limit、rollback、kill switch 与 observation 条件
/// 真正套到 dry-run 上；不应用 delta，不写 formal package，不改变 formal selected set、PackingPolicy、package output、runtime 或 vector binding。
/// </summary>
public sealed class ControlledShadowMergeDryRunGateRunner
{
    public ControlledShadowMergeDryRunGateReport RunDryRun(
        ControlledShadowMergeProposalReport? proposal,
        SourceDiverseShadowAdapterValidationReport? v66Gate,
        ShadowCandidateMergePreviewReport? v67Gate,
        ShadowCandidateMergePreviewObservationReport? observationGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ControlledShadowMergeDryRunGateOptions? options = null)
        => BuildReport(proposal, v66Gate, v67Gate, observationGate, runtimeChangeGate, options, gateMode: false);

    public ControlledShadowMergeDryRunGateReport RunGate(
        ControlledShadowMergeProposalReport? proposal,
        SourceDiverseShadowAdapterValidationReport? v66Gate,
        ShadowCandidateMergePreviewReport? v67Gate,
        ShadowCandidateMergePreviewObservationReport? observationGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ControlledShadowMergeDryRunGateOptions? options = null)
        => BuildReport(proposal, v66Gate, v67Gate, observationGate, runtimeChangeGate, options, gateMode: true);

    public static string BuildMarkdown(string title, ControlledShadowMergeDryRunGateReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"Generated: `{r.CreatedAt:O}`");
        b.AppendLine($"OperationId: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Summary");
        b.AppendLine($"- DryRunPassed: `{r.DryRunPassed}`");
        b.AppendLine($"- GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}`");
        b.AppendLine($"- ProposalId: `{r.ProposalId}`");
        b.AppendLine($"- ProposalGatePassed: `{r.ProposalGatePassed}`");
        b.AppendLine($"- ProposalConstraintsApplied: `{r.ProposalConstraintsApplied}`");
        b.AppendLine($"- ScopeCount: `{r.ScopeCount}`");
        b.AppendLine($"- RequestCount/MaxRequestCount: `{r.RequestCount}` / `{r.MaxRequestCount}`");
        b.AppendLine($"- DurationMinutes/MaxDurationMinutes: `{r.DurationMinutes}` / `{r.MaxDurationMinutes}`");
        b.AppendLine($"- ErrorCount/MaxErrorCount: `{r.ErrorCount}` / `{r.MaxErrorCount}`");
        b.AppendLine($"- RequestDurationErrorLimitEnforced: `{r.RequestDurationErrorLimitEnforced}`");
        b.AppendLine($"- ObservationRunCount/MinObservationRunCount: `{r.ObservationRunCount}` / `{r.MinObservationRunCount}`");
        b.AppendLine($"- SampleObservationCount/MinSampleObservationCount: `{r.SampleObservationCount}` / `{r.MinSampleObservationCount}`");
        b.AppendLine($"- ObservationWindowLimitEnforced: `{r.ObservationWindowLimitEnforced}`");
        b.AppendLine($"- Observation/Stop condition counts: `{r.ObservationConditionCount}` / `{r.StopConditionCount}`");
        b.AppendLine($"- ObservationPlanConstraintPresent: `{r.ObservationPlanConstraintPresent}`");
        b.AppendLine($"- DryRunPreviewGenerated: `{r.DryRunPreviewGenerated}`");
        b.AppendLine($"- PreviewAdd/Remove: `{r.PreviewAddCount}` / `{r.PreviewRemoveCount}`");
        b.AppendLine($"- Proposal max add/remove: `{r.MaxPreviewAddCount}` / `{r.MaxPreviewRemoveCount}`");
        b.AppendLine($"- AddRemoveLimitEnforced: `{r.AddRemoveLimitEnforced}`");
        b.AppendLine($"- TokenDeltaTotal/Max: `{r.TokenDeltaTotal}` / `{r.TokenDeltaMax}`");
        b.AppendLine($"- Proposal max token total/sample: `{r.MaxTokenDeltaTotal}` / `{r.MaxTokenDeltaPerSample}`");
        b.AppendLine($"- TokenSectionPriorityGatePassed: `{r.TokenSectionPriorityGatePassed}`");
        b.AppendLine($"- PriorityInversionCount: `{r.PriorityInversionCount}`");
        b.AppendLine($"- SectionMismatchCount: `{r.SectionMismatchCount}`");
        b.AppendLine($"- DroppedRequiredCandidateCount: `{r.DroppedRequiredCandidateCount}`");
        b.AppendLine($"- RollbackPlanPresent/Verified: `{r.RollbackPlanPresent}` / `{r.RollbackVerified}`");
        b.AppendLine($"- KillSwitchAvailable/Verified: `{r.KillSwitchAvailable}` / `{r.KillSwitchVerified}`");
        b.AppendLine($"- AppliedAdd/Remove: `{r.AppliedAddCount}` / `{r.AppliedRemoveCount}`");
        b.AppendLine($"- Risk/must-not/lifecycle: `{r.RiskAfterPolicy}` / `{r.MustNotHitRiskAfterPolicy}` / `{r.LifecycleRiskAfterPolicy}`");
        b.AppendLine($"- FormalOutputChanged: `{r.FormalOutputChanged}`");
        b.AppendLine($"- FormalSelectedSetChanged/FormalPackageWritten: `{r.FormalSelectedSetChanged}` / `{r.FormalPackageWritten}`");
        b.AppendLine($"- Package/PackingPolicy/runtime/vector: `{r.PackageOutputChanged}` / `{r.PackingPolicyChanged}` / `{r.RuntimeMutated}` / `{r.VectorStoreBindingChanged}`");
        b.AppendLine($"- UseForRuntime/FormalRetrieval/RuntimeSwitch/ReadyForRuntimeSwitch: `{r.UseForRuntime}` / `{r.FormalRetrievalAllowed}` / `{r.RuntimeSwitchAllowed}` / `{r.ReadyForRuntimeSwitch}`");
        AppendList(b, "SelectedScopes", r.SelectedScopes);
        AppendMap(b, "ConstraintChecks", r.ConstraintChecks);
        AppendList(b, "BlockedReasons", r.BlockedReasons);
        b.AppendLine();
        b.AppendLine("Controlled shadow merge dry-run gate only. It verifies proposal constraints against preview artifacts and does not apply add/remove, mutate formal output, write formal package, alter PackingPolicy/package output, switch runtime, or bind vector storage.");
        return b.ToString();
    }

    private static ControlledShadowMergeDryRunGateReport BuildReport(
        ControlledShadowMergeProposalReport? proposal,
        SourceDiverseShadowAdapterValidationReport? v66Gate,
        ShadowCandidateMergePreviewReport? v67Gate,
        ShadowCandidateMergePreviewObservationReport? observationGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ControlledShadowMergeDryRunGateOptions? options,
        bool gateMode)
    {
        options ??= new ControlledShadowMergeDryRunGateOptions();
        var blocked = new List<string>();
        if (!options.Enabled)
            blocked.Add("ControlledShadowMergeDryRunDisabled");

        var proposalPassed = proposal?.GatePassed == true && proposal.ProposalPassed;
        if (!proposalPassed)
            blocked.Add("ControlledShadowMergeProposalMissingOrNotPassed");
        if (v66Gate is null || !v66Gate.GatePassed)
            blocked.Add("V66GateMissingOrNotPassed");
        if (v67Gate is null || !v67Gate.GatePassed)
            blocked.Add("V67GateMissingOrNotPassed");
        if (observationGate is null || !observationGate.GatePassed)
            blocked.Add("ObservationGateMissingOrNotPassed");
        if (runtimeChangeGate is null || !runtimeChangeGate.Passed)
            blocked.Add("RuntimeChangeGateMissingOrNotPassed");

        var preview = v66Gate is null ? null : new ShadowCandidateMergePreviewRunner().RunPreview(v66Gate, new ShadowCandidateMergePreviewOptions
        {
            GateMode = true,
            TokenDeltaBudget = proposal?.MaxTokenDeltaTotal ?? options.FallbackMaxTokenDeltaTotal,
            TokenDeltaMaxBudget = proposal?.MaxTokenDeltaPerSample ?? options.FallbackMaxTokenDeltaPerSample,
            RiskAfterPolicy = options.RiskAfterPolicy,
            MustNotHitRiskAfterPolicy = options.MustNotHitRiskAfterPolicy,
            LifecycleRiskAfterPolicy = options.LifecycleRiskAfterPolicy,
            UseForRuntime = options.UseForRuntime,
            FormalRetrievalAllowed = options.FormalRetrievalAllowed,
            RuntimeSwitchAllowed = options.RuntimeSwitchAllowed,
            ReadyForRuntimeSwitch = options.ReadyForRuntimeSwitch,
            FormalSelectedSetChanged = options.FormalSelectedSetChanged,
            FormalOutputChanged = options.FormalOutputChanged,
            FormalPackageWritten = options.FormalPackageWritten,
            PackageOutputChanged = options.PackageOutputChanged,
            PackingPolicyChanged = options.PackingPolicyChanged,
            RuntimeMutated = options.RuntimeMutated,
            VectorStoreBindingChanged = options.VectorStoreBindingChanged
        });

        if (preview is null || !preview.PreviewMergedSetGenerated)
            blocked.Add("DryRunPreviewMissing");
        if (preview is not null && !preview.GatePassed)
            blocked.Add("DryRunPreviewGateFailed");

        var requestCount = preview?.SampleCount ?? v67Gate?.SampleCount ?? v66Gate?.SampleCount ?? 0;
        var maxRequestCount = proposal?.MaxRequestCount ?? options.FallbackMaxRequestCount;
        var durationMinutes = Math.Max(0, options.SimulatedDurationMinutes);
        var maxDurationMinutes = proposal?.MaxDurationMinutes ?? options.FallbackMaxDurationMinutes;
        var errorCount = Math.Max(0, options.SimulatedErrorCount);
        var maxErrorCount = proposal?.MaxErrorCount ?? options.FallbackMaxErrorCount;
        var requestDurationErrorWithinLimit = requestCount > 0 && requestCount <= maxRequestCount && durationMinutes <= maxDurationMinutes && errorCount <= maxErrorCount;
        var observationRunCount = observationGate?.ObservationRunCount ?? 0;
        var minObservationRunCount = proposal?.MinObservationRunCount ?? options.FallbackMinObservationRunCount;
        var sampleObservationCount = observationGate?.SampleObservationCount ?? 0;
        var minSampleObservationCount = proposal?.MinSampleObservationCount ?? options.FallbackMinSampleObservationCount;
        var observationWindowWithinLimit = observationRunCount >= minObservationRunCount && sampleObservationCount >= minSampleObservationCount;
        var observationConditionCount = proposal?.ObservationConditions.Count ?? 0;
        var stopConditionCount = proposal?.StopConditions.Count ?? 0;
        var proposalConditionPlanPresent = observationConditionCount > 0 && stopConditionCount > 0;

        if (!requestDurationErrorWithinLimit)
            blocked.Add("RequestDurationErrorLimitViolation");
        if (!observationWindowWithinLimit)
            blocked.Add("ObservationWindowConstraintViolation");
        if (!proposalConditionPlanPresent)
            blocked.Add("ObservationOrStopConditionsMissing");
        var previewAdd = preview?.PreviewAddCount ?? v67Gate?.PreviewAddCount ?? 0;
        var previewRemove = preview?.PreviewRemoveCount ?? v67Gate?.PreviewRemoveCount ?? 0;
        var maxAdd = proposal?.MaxPreviewAddCount ?? options.FallbackMaxPreviewAddCount;
        var maxRemove = proposal?.MaxPreviewRemoveCount ?? options.FallbackMaxPreviewRemoveCount;
        var addRemoveWithinLimit = previewAdd > 0 && previewRemove > 0 && previewAdd <= maxAdd && previewRemove <= maxRemove;
        if (!addRemoveWithinLimit)
            blocked.Add("AddRemoveLimitViolation");

        var tokenTotal = (preview?.TokenDeltaTotal ?? v67Gate?.TokenDeltaTotal ?? 0) + options.SimulatedTokenDeltaTotal;
        var tokenMax = Math.Max(preview?.TokenDeltaMax ?? v67Gate?.TokenDeltaMax ?? 0, Math.Abs(options.SimulatedTokenDeltaMax));
        var maxTokenTotal = proposal?.MaxTokenDeltaTotal ?? options.FallbackMaxTokenDeltaTotal;
        var maxTokenMax = proposal?.MaxTokenDeltaPerSample ?? options.FallbackMaxTokenDeltaPerSample;
        var priorityInversions = (preview?.PriorityInversionCount ?? v67Gate?.PriorityInversionCount ?? 0) + Math.Max(0, options.SimulatedPriorityInversionCount);
        var sectionMismatch = (preview?.SectionMismatchCount ?? v67Gate?.SectionMismatchCount ?? 0) + Math.Max(0, options.SimulatedSectionMismatchCount);
        var droppedRequired = (preview?.DroppedRequiredCandidateCount ?? v67Gate?.DroppedRequiredCandidateCount ?? 0) + Math.Max(0, options.SimulatedDroppedRequiredCandidateCount);
        var tokenSectionPriorityPassed = Math.Abs(tokenTotal) <= maxTokenTotal && tokenMax <= maxTokenMax && priorityInversions == 0 && sectionMismatch == 0 && droppedRequired == 0;
        if (!tokenSectionPriorityPassed)
            blocked.Add("TokenSectionPriorityGateViolation");

        var rollbackPresent = proposal?.RollbackPlanPresent == true && !string.IsNullOrWhiteSpace(proposal.RollbackPlan);
        var killSwitchPresent = proposal?.KillSwitchPlanPresent == true && !string.IsNullOrWhiteSpace(proposal.KillSwitchPlan);
        var appliedAdd = (preview?.AppliedAddCount ?? v67Gate?.AppliedAddCount ?? 0) + Math.Max(0, options.SimulatedAppliedAddCount);
        var appliedRemove = (preview?.AppliedRemoveCount ?? v67Gate?.AppliedRemoveCount ?? 0) + Math.Max(0, options.SimulatedAppliedRemoveCount);
        var formalSelectedSetChanged = (preview?.FormalSelectedSetChanged ?? v67Gate?.FormalSelectedSetChanged ?? false) || options.FormalSelectedSetChanged;
        var formalOutput = Math.Max(preview?.FormalOutputChanged ?? v67Gate?.FormalOutputChanged ?? 0, options.FormalOutputChanged);
        var formalPackageWritten = (preview?.FormalPackageWritten ?? v67Gate?.FormalPackageWritten ?? false) || options.FormalPackageWritten;
        var packageOutputChanged = (preview?.PackageOutputChanged ?? v67Gate?.PackageOutputChanged ?? false) || options.PackageOutputChanged;
        var packingPolicyChanged = (preview?.PackingPolicyChanged ?? v67Gate?.PackingPolicyChanged ?? false) || options.PackingPolicyChanged;
        var runtimeMutated = (preview?.RuntimeMutated ?? v67Gate?.RuntimeMutated ?? false) || options.RuntimeMutated;
        var vectorBindingChanged = (preview?.VectorStoreBindingChanged ?? v67Gate?.VectorStoreBindingChanged ?? false) || options.VectorStoreBindingChanged;
        var useForRuntime = (preview?.UseForRuntime ?? false) || options.UseForRuntime;
        var formalRetrievalAllowed = (preview?.FormalRetrievalAllowed ?? false) || options.FormalRetrievalAllowed;
        var runtimeSwitchAllowed = (preview?.RuntimeSwitchAllowed ?? false) || options.RuntimeSwitchAllowed;
        var readyForRuntimeSwitch = (preview?.ReadyForRuntimeSwitch ?? false) || options.ReadyForRuntimeSwitch;
        var risk = Math.Max(preview?.RiskAfterPolicy ?? 0, options.RiskAfterPolicy);
        var mustNot = Math.Max(preview?.MustNotHitRiskAfterPolicy ?? 0, options.MustNotHitRiskAfterPolicy);
        var lifecycle = Math.Max(preview?.LifecycleRiskAfterPolicy ?? 0, options.LifecycleRiskAfterPolicy);

        if (!rollbackPresent || options.RollbackUnavailable)
            blocked.Add("RollbackUnavailable");
        if (!killSwitchPresent || options.KillSwitchUnavailable)
            blocked.Add("KillSwitchUnavailable");
        if (appliedAdd != 0 || appliedRemove != 0)
            blocked.Add("AppliedDeltaDetected");
        if (risk != 0 || mustNot != 0 || lifecycle != 0)
            blocked.Add("RiskDetected");
        if (formalSelectedSetChanged || formalOutput != 0 || formalPackageWritten || packageOutputChanged || packingPolicyChanged || runtimeMutated || vectorBindingChanged)
            blocked.Add("FormalOrRuntimeInvariantChanged");
        if (useForRuntime || formalRetrievalAllowed || runtimeSwitchAllowed || readyForRuntimeSwitch)
            blocked.Add("RuntimeSwitchAttemptDetected");

        var constraintsApplied = proposalPassed &&
            requestDurationErrorWithinLimit &&
            observationWindowWithinLimit &&
            proposalConditionPlanPresent &&
            addRemoveWithinLimit &&
            tokenSectionPriorityPassed &&
            rollbackPresent &&
            killSwitchPresent &&
            (proposal?.SelectedScopes.Count ?? 0) > 0;
        if (!constraintsApplied)
            blocked.Add("ProposalConstraintsNotApplied");

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static r => r, StringComparer.OrdinalIgnoreCase).ToArray();
        var passed = distinctBlocked.Length == 0;
        return new ControlledShadowMergeDryRunGateReport
        {
            OperationId = $"controlled-shadow-merge-dry-run-gate-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            DryRunPassed = passed,
            GatePassed = gateMode && passed,
            Recommendation = passed ? ControlledShadowMergeDryRunGateRecommendations.ReadyForControlledShadowMergeObservation : ResolveRecommendation(distinctBlocked),
            ProposalId = proposal?.ProposalId ?? string.Empty,
            ProposalGatePassed = proposalPassed,
            ProposalConstraintsApplied = constraintsApplied,
            ScopeCount = proposal?.ScopeCount ?? 0,
            RequestCount = requestCount,
            MaxRequestCount = maxRequestCount,
            DurationMinutes = durationMinutes,
            MaxDurationMinutes = maxDurationMinutes,
            ErrorCount = errorCount,
            MaxErrorCount = maxErrorCount,
            RequestDurationErrorLimitEnforced = requestDurationErrorWithinLimit,
            ObservationRunCount = observationRunCount,
            MinObservationRunCount = minObservationRunCount,
            SampleObservationCount = sampleObservationCount,
            MinSampleObservationCount = minSampleObservationCount,
            ObservationWindowLimitEnforced = observationWindowWithinLimit,
            ObservationConditionCount = observationConditionCount,
            StopConditionCount = stopConditionCount,
            ObservationPlanConstraintPresent = proposalConditionPlanPresent,
            SelectedScopes = proposal?.SelectedScopes ?? Array.Empty<string>(),
            DryRunPreviewGenerated = preview?.PreviewMergedSetGenerated == true,
            PreviewAddCount = previewAdd,
            PreviewRemoveCount = previewRemove,
            MaxPreviewAddCount = maxAdd,
            MaxPreviewRemoveCount = maxRemove,
            AddRemoveLimitEnforced = addRemoveWithinLimit,
            TokenDeltaTotal = tokenTotal,
            TokenDeltaMax = tokenMax,
            MaxTokenDeltaTotal = maxTokenTotal,
            MaxTokenDeltaPerSample = maxTokenMax,
            TokenSectionPriorityGatePassed = tokenSectionPriorityPassed,
            PriorityInversionCount = priorityInversions,
            SectionMismatchCount = sectionMismatch,
            DroppedRequiredCandidateCount = droppedRequired,
            RollbackPlanPresent = rollbackPresent,
            RollbackVerified = rollbackPresent && !options.RollbackUnavailable && !runtimeMutated && !packageOutputChanged && !formalPackageWritten,
            KillSwitchAvailable = killSwitchPresent && !options.KillSwitchUnavailable,
            KillSwitchVerified = killSwitchPresent && !options.KillSwitchUnavailable && !runtimeMutated,
            KillSwitchTriggered = options.KillSwitchTriggered,
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
            VectorStoreBindingChanged = vectorBindingChanged,
            UseForRuntime = useForRuntime,
            FormalRetrievalAllowed = formalRetrievalAllowed,
            RuntimeSwitchAllowed = runtimeSwitchAllowed,
            ReadyForRuntimeSwitch = readyForRuntimeSwitch,
            ConstraintChecks = BuildConstraintChecks(
                proposal,
                requestDurationErrorWithinLimit,
                observationWindowWithinLimit,
                proposalConditionPlanPresent,
                addRemoveWithinLimit,
                tokenSectionPriorityPassed,
                rollbackPresent,
                killSwitchPresent),
            BlockedReasons = distinctBlocked
        };
    }

    private static IReadOnlyDictionary<string, string> BuildConstraintChecks(
        ControlledShadowMergeProposalReport? proposal,
        bool requestDurationErrorWithinLimit,
        bool observationWindowWithinLimit,
        bool proposalConditionPlanPresent,
        bool addRemoveWithinLimit,
        bool tokenSectionPriorityPassed,
        bool rollbackPresent,
        bool killSwitchPresent) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["proposal gate"] = proposal?.GatePassed == true ? "Passed" : "MissingOrFailed",
            ["explicit scope"] = (proposal?.ScopeCount ?? 0) > 0 ? "Passed" : "Missing",
            ["request/duration/error limit"] = requestDurationErrorWithinLimit ? "Passed" : "Failed",
            ["observation window"] = observationWindowWithinLimit ? "Passed" : "Failed",
            ["observation/stop condition plan"] = proposalConditionPlanPresent ? "Passed" : "Missing",
            ["add/remove limit"] = addRemoveWithinLimit ? "Passed" : "Failed",
            ["token/section/priority gate"] = tokenSectionPriorityPassed ? "Passed" : "Failed",
            ["rollback plan"] = rollbackPresent ? "Passed" : "Missing",
            ["kill switch plan"] = killSwitchPresent ? "Passed" : "Missing"
        };

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Contains("ControlledShadowMergeProposalMissingOrNotPassed", StringComparer.OrdinalIgnoreCase))
            return ControlledShadowMergeDryRunGateRecommendations.BlockedByMissingProposal;
        if (blocked.Contains("AddRemoveLimitViolation", StringComparer.OrdinalIgnoreCase))
            return ControlledShadowMergeDryRunGateRecommendations.BlockedByAddRemoveLimit;
        if (blocked.Contains("TokenSectionPriorityGateViolation", StringComparer.OrdinalIgnoreCase))
            return ControlledShadowMergeDryRunGateRecommendations.BlockedByTokenSectionPriority;
        if (blocked.Contains("RequestDurationErrorLimitViolation", StringComparer.OrdinalIgnoreCase) ||
            blocked.Contains("ObservationWindowConstraintViolation", StringComparer.OrdinalIgnoreCase) ||
            blocked.Contains("ObservationOrStopConditionsMissing", StringComparer.OrdinalIgnoreCase))
        {
            return ControlledShadowMergeDryRunGateRecommendations.BlockedByConstraintViolation;
        }
        if (blocked.Contains("RollbackUnavailable", StringComparer.OrdinalIgnoreCase) || blocked.Contains("KillSwitchUnavailable", StringComparer.OrdinalIgnoreCase))
            return ControlledShadowMergeDryRunGateRecommendations.BlockedByRollbackOrKillSwitch;
        if (blocked.Contains("RiskDetected", StringComparer.OrdinalIgnoreCase))
            return ControlledShadowMergeDryRunGateRecommendations.BlockedByRisk;
        if (blocked.Contains("FormalOrRuntimeInvariantChanged", StringComparer.OrdinalIgnoreCase) ||
            blocked.Contains("RuntimeSwitchAttemptDetected", StringComparer.OrdinalIgnoreCase) ||
            blocked.Contains("AppliedDeltaDetected", StringComparer.OrdinalIgnoreCase))
        {
            return ControlledShadowMergeDryRunGateRecommendations.BlockedByRuntimeInvariant;
        }

        if (blocked.Any(static reason => reason.EndsWith("MissingOrNotPassed", StringComparison.OrdinalIgnoreCase)) ||
            blocked.Contains("DryRunPreviewMissing", StringComparer.OrdinalIgnoreCase) ||
            blocked.Contains("DryRunPreviewGateFailed", StringComparer.OrdinalIgnoreCase) ||
            blocked.Contains("ProposalConstraintsNotApplied", StringComparer.OrdinalIgnoreCase))
        {
            return ControlledShadowMergeDryRunGateRecommendations.BlockedByConstraintViolation;
        }

        return ControlledShadowMergeDryRunGateRecommendations.KeepPreviewOnly;
    }

    private static void AppendList(StringBuilder b, string title, IReadOnlyList<string> items)
    {
        b.AppendLine();
        b.AppendLine($"## {title}");
        if (items.Count == 0)
        {
            b.AppendLine("- (empty)");
            return;
        }

        foreach (var item in items)
            b.AppendLine($"- `{item}`");
    }

    private static void AppendMap(StringBuilder b, string title, IReadOnlyDictionary<string, string> items)
    {
        b.AppendLine();
        b.AppendLine($"## {title}");
        if (items.Count == 0)
        {
            b.AppendLine("- (empty)");
            return;
        }

        foreach (var item in items.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
            b.AppendLine($"- `{item.Key}`: `{item.Value}`");
    }
}

public sealed class ControlledShadowMergeDryRunGateOptions
{
    public bool Enabled { get; init; } = true;
    public int FallbackMaxRequestCount { get; init; } = 120;
    public int FallbackMaxDurationMinutes { get; init; } = 30;
    public int FallbackMaxErrorCount { get; init; }
    public int FallbackMinObservationRunCount { get; init; } = 10;
    public int FallbackMinSampleObservationCount { get; init; } = 120;
    public int FallbackMaxPreviewAddCount { get; init; } = 10;
    public int FallbackMaxPreviewRemoveCount { get; init; } = 10;
    public int FallbackMaxTokenDeltaTotal { get; init; } = 128;
    public int FallbackMaxTokenDeltaPerSample { get; init; } = 32;
    public int SimulatedAppliedAddCount { get; init; }
    public int SimulatedAppliedRemoveCount { get; init; }
    public int SimulatedDurationMinutes { get; init; }
    public int SimulatedErrorCount { get; init; }
    public int SimulatedTokenDeltaTotal { get; init; }
    public int SimulatedTokenDeltaMax { get; init; }
    public int SimulatedPriorityInversionCount { get; init; }
    public int SimulatedSectionMismatchCount { get; init; }
    public int SimulatedDroppedRequiredCandidateCount { get; init; }
    public bool RollbackUnavailable { get; init; }
    public bool KillSwitchUnavailable { get; init; }
    public bool KillSwitchTriggered { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public bool UseForRuntime { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public bool FormalSelectedSetChanged { get; init; }
    public int FormalOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
}

public sealed class ControlledShadowMergeDryRunGateReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool DryRunPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = ControlledShadowMergeDryRunGateRecommendations.KeepPreviewOnly;
    public string ProposalId { get; init; } = string.Empty;
    public bool ProposalGatePassed { get; init; }
    public bool ProposalConstraintsApplied { get; init; }
    public int ScopeCount { get; init; }
    public int RequestCount { get; init; }
    public int MaxRequestCount { get; init; }
    public int DurationMinutes { get; init; }
    public int MaxDurationMinutes { get; init; }
    public int ErrorCount { get; init; }
    public int MaxErrorCount { get; init; }
    public bool RequestDurationErrorLimitEnforced { get; init; }
    public int ObservationRunCount { get; init; }
    public int MinObservationRunCount { get; init; }
    public int SampleObservationCount { get; init; }
    public int MinSampleObservationCount { get; init; }
    public bool ObservationWindowLimitEnforced { get; init; }
    public int ObservationConditionCount { get; init; }
    public int StopConditionCount { get; init; }
    public bool ObservationPlanConstraintPresent { get; init; }
    public IReadOnlyList<string> SelectedScopes { get; init; } = Array.Empty<string>();
    public bool DryRunPreviewGenerated { get; init; }
    public int PreviewAddCount { get; init; }
    public int PreviewRemoveCount { get; init; }
    public int MaxPreviewAddCount { get; init; }
    public int MaxPreviewRemoveCount { get; init; }
    public bool AddRemoveLimitEnforced { get; init; }
    public int TokenDeltaTotal { get; init; }
    public int TokenDeltaMax { get; init; }
    public int MaxTokenDeltaTotal { get; init; }
    public int MaxTokenDeltaPerSample { get; init; }
    public bool TokenSectionPriorityGatePassed { get; init; }
    public int PriorityInversionCount { get; init; }
    public int SectionMismatchCount { get; init; }
    public int DroppedRequiredCandidateCount { get; init; }
    public bool RollbackPlanPresent { get; init; }
    public bool RollbackVerified { get; init; }
    public bool KillSwitchAvailable { get; init; }
    public bool KillSwitchVerified { get; init; }
    public bool KillSwitchTriggered { get; init; }
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
    public IReadOnlyDictionary<string, string> ConstraintChecks { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public static class ControlledShadowMergeDryRunGateRecommendations
{
    public const string ReadyForControlledShadowMergeObservation = nameof(ReadyForControlledShadowMergeObservation);
    public const string BlockedByMissingProposal = nameof(BlockedByMissingProposal);
    public const string BlockedByConstraintViolation = nameof(BlockedByConstraintViolation);
    public const string BlockedByAddRemoveLimit = nameof(BlockedByAddRemoveLimit);
    public const string BlockedByTokenSectionPriority = nameof(BlockedByTokenSectionPriority);
    public const string BlockedByRollbackOrKillSwitch = nameof(BlockedByRollbackOrKillSwitch);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByRuntimeInvariant = nameof(BlockedByRuntimeInvariant);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


