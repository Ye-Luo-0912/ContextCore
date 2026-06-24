using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// V6.11 controlled shadow merge observation window。在 V6.10 dry-run gate 约束下做多轮观察；
/// 只写 shadow observation artifact，不应用 merge，不改变 formal selected set、PackingPolicy、package output、runtime 或 vector binding。
/// </summary>
public sealed class ControlledShadowMergeObservationWindowRunner
{
    public ControlledShadowMergeObservationWindowReport RunObservation(
        ControlledShadowMergeProposalReport? proposal,
        SourceDiverseShadowAdapterValidationReport? v66Gate,
        ShadowCandidateMergePreviewReport? v67Gate,
        ShadowCandidateMergePreviewObservationReport? observationGate,
        ControlledShadowMergeDryRunGateReport? dryRunGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ControlledShadowMergeObservationWindowOptions? options = null)
        => BuildReport(proposal, v66Gate, v67Gate, observationGate, dryRunGate, runtimeChangeGate, options, gateMode: false);

    public ControlledShadowMergeObservationWindowReport RunGate(
        ControlledShadowMergeProposalReport? proposal,
        SourceDiverseShadowAdapterValidationReport? v66Gate,
        ShadowCandidateMergePreviewReport? v67Gate,
        ShadowCandidateMergePreviewObservationReport? observationGate,
        ControlledShadowMergeDryRunGateReport? dryRunGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ControlledShadowMergeObservationWindowOptions? options = null)
        => BuildReport(proposal, v66Gate, v67Gate, observationGate, dryRunGate, runtimeChangeGate, options, gateMode: true);

    public static string BuildMarkdown(string title, ControlledShadowMergeObservationWindowReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"Generated: `{r.CreatedAt:O}`");
        b.AppendLine($"OperationId: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Summary");
        b.AppendLine($"- ObservationPassed: `{r.ObservationPassed}`");
        b.AppendLine($"- GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}`");
        b.AppendLine($"- ProposalId: `{r.ProposalId}`");
        b.AppendLine($"- V6.10 DryRunGatePassed: `{r.DryRunGatePassed}`");
        b.AppendLine($"- ProposalConstraintsApplied: `{r.ProposalConstraintsApplied}`");
        b.AppendLine($"- ObservationRunCount/MinObservationRunCount: `{r.ObservationRunCount}` / `{r.MinObservationRunCount}`");
        b.AppendLine($"- RequestCountTotal/MaxRequestCount: `{r.RequestCountTotal}` / `{r.MaxRequestCount}`");
        b.AppendLine($"- DurationMinutes/MaxDurationMinutes: `{r.DurationMinutes}` / `{r.MaxDurationMinutes}`");
        b.AppendLine($"- ErrorCount/MaxErrorCount: `{r.ErrorCount}` / `{r.MaxErrorCount}`");
        b.AppendLine($"- RequestDurationErrorWindowEnforced: `{r.RequestDurationErrorWindowEnforced}`");
        b.AppendLine($"- SampleObservationCount/MinSampleObservationCount: `{r.SampleObservationCount}` / `{r.MinSampleObservationCount}`");
        b.AppendLine($"- ObservationWindowLimitEnforced: `{r.ObservationWindowLimitEnforced}`");
        b.AppendLine($"- DeterministicDryRunStable: `{r.DeterministicDryRunStable}`");
        b.AppendLine($"- DistinctStableSignatureCount: `{r.DistinctStableSignatureCount}`");
        b.AppendLine($"- PreviewAddCount min/max/total: `{r.PreviewAddCountMin}` / `{r.PreviewAddCountMax}` / `{r.PreviewAddCountTotal}`");
        b.AppendLine($"- PreviewRemoveCount min/max/total: `{r.PreviewRemoveCountMin}` / `{r.PreviewRemoveCountMax}` / `{r.PreviewRemoveCountTotal}`");
        b.AppendLine($"- AppliedAdd/Remove max: `{r.AppliedAddCountMax}` / `{r.AppliedRemoveCountMax}`");
        b.AppendLine($"- Risk/MustNot/Lifecycle max: `{r.RiskAfterPolicyMax}` / `{r.MustNotHitRiskAfterPolicyMax}` / `{r.LifecycleRiskAfterPolicyMax}`");
        b.AppendLine($"- TokenDeltaTotalMax/TokenDeltaMaxMax: `{r.TokenDeltaTotalMax}` / `{r.TokenDeltaMaxMax}`");
        b.AppendLine($"- PriorityInversionCountTotal: `{r.PriorityInversionCountTotal}`");
        b.AppendLine($"- SectionMismatchCountTotal: `{r.SectionMismatchCountTotal}`");
        b.AppendLine($"- DroppedRequiredCandidateCountTotal: `{r.DroppedRequiredCandidateCountTotal}`");
        b.AppendLine($"- RollbackVerified: `{r.RollbackVerified}`");
        b.AppendLine($"- KillSwitchVerified: `{r.KillSwitchVerified}`");
        b.AppendLine($"- AppliedDeltaZero: `{r.AppliedDeltaZero}`");
        b.AppendLine($"- FormalOutputChangedMax: `{r.FormalOutputChangedMax}`");
        b.AppendLine($"- FormalSelectedSetChanged/FormalPackageWritten: `{r.FormalSelectedSetChanged}` / `{r.FormalPackageWritten}`");
        b.AppendLine($"- Package/PackingPolicy/runtime/vector: `{r.PackageOutputChanged}` / `{r.PackingPolicyChanged}` / `{r.RuntimeMutated}` / `{r.VectorStoreBindingChanged}`");
        b.AppendLine($"- UseForRuntime/FormalRetrieval/RuntimeSwitch/ReadyForRuntimeSwitch: `{r.UseForRuntime}` / `{r.FormalRetrievalAllowed}` / `{r.RuntimeSwitchAllowed}` / `{r.ReadyForRuntimeSwitch}`");
        AppendList(b, "SelectedScopes", r.SelectedScopes);
        AppendList(b, "BlockedReasons", r.BlockedReasons);
        b.AppendLine();
        b.AppendLine("Controlled shadow merge observation window only. It repeats V6.10 dry-run gate under proposal constraints and does not apply add/remove, mutate formal output, write formal package, alter PackingPolicy/package output, switch runtime, or bind vector storage.");
        return b.ToString();
    }

    private static ControlledShadowMergeObservationWindowReport BuildReport(
        ControlledShadowMergeProposalReport? proposal,
        SourceDiverseShadowAdapterValidationReport? v66Gate,
        ShadowCandidateMergePreviewReport? v67Gate,
        ShadowCandidateMergePreviewObservationReport? observationGate,
        ControlledShadowMergeDryRunGateReport? dryRunGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ControlledShadowMergeObservationWindowOptions? options,
        bool gateMode)
    {
        options ??= new ControlledShadowMergeObservationWindowOptions();
        var blocked = new List<string>();
        if (!options.Enabled)
            blocked.Add("ControlledShadowMergeObservationWindowDisabled");
        var proposalPassed = proposal?.GatePassed == true && proposal.ProposalPassed;
        if (!proposalPassed)
            blocked.Add("ControlledShadowMergeProposalMissingOrNotPassed");
        if (dryRunGate is null || !dryRunGate.GatePassed || !dryRunGate.DryRunPassed)
            blocked.Add("ControlledShadowMergeDryRunGateMissingOrNotPassed");
        if (v66Gate is null || !v66Gate.GatePassed)
            blocked.Add("V66GateMissingOrNotPassed");
        if (v67Gate is null || !v67Gate.GatePassed)
            blocked.Add("V67GateMissingOrNotPassed");
        if (observationGate is null || !observationGate.GatePassed)
            blocked.Add("ShadowMergeObservationGateMissingOrNotPassed");
        if (runtimeChangeGate is null || !runtimeChangeGate.Passed)
            blocked.Add("RuntimeChangeGateMissingOrNotPassed");        var runCount = options.ObservationRunCount > 0
            ? options.ObservationRunCount
            : proposal?.MinObservationRunCount ?? options.FallbackMinObservationRunCount;
        runCount = Math.Max(0, runCount);
        var minRunCount = proposal?.MinObservationRunCount ?? options.FallbackMinObservationRunCount;
        var minSampleCount = proposal?.MinSampleObservationCount ?? options.FallbackMinSampleObservationCount;
        var maxRequestCount = proposal?.MaxRequestCount ?? options.FallbackMaxRequestCount;
        var maxDurationMinutes = proposal?.MaxDurationMinutes ?? options.FallbackMaxDurationMinutes;
        var maxErrorCount = proposal?.MaxErrorCount ?? options.FallbackMaxErrorCount;
        var maxTokenTotal = proposal?.MaxTokenDeltaTotal ?? options.FallbackMaxTokenDeltaTotal;
        var maxTokenMax = proposal?.MaxTokenDeltaPerSample ?? options.FallbackMaxTokenDeltaPerSample;

        var runs = new List<ControlledShadowMergeObservationWindowRunResult>(runCount);
        var dryRunRunner = new ControlledShadowMergeDryRunGateRunner();
        for (var i = 0; i < runCount; i++)
        {
            var run = dryRunRunner.RunGate(proposal, v66Gate, v67Gate, observationGate, runtimeChangeGate, BuildDryRunOptions(options));
            runs.Add(new ControlledShadowMergeObservationWindowRunResult
            {
                RunIndex = i + 1,
                DryRunPassed = run.DryRunPassed,
                GatePassed = run.GatePassed,
                StableSignature = ComputeStableSignature(run),
                RequestCount = run.RequestCount,
                ProposalConstraintsApplied = run.ProposalConstraintsApplied,
                PreviewAddCount = run.PreviewAddCount,
                PreviewRemoveCount = run.PreviewRemoveCount,
                AppliedAddCount = run.AppliedAddCount,
                AppliedRemoveCount = run.AppliedRemoveCount,
                TokenDeltaTotal = run.TokenDeltaTotal,
                TokenDeltaMax = run.TokenDeltaMax,
                PriorityInversionCount = run.PriorityInversionCount,
                SectionMismatchCount = run.SectionMismatchCount,
                DroppedRequiredCandidateCount = run.DroppedRequiredCandidateCount,
                RiskAfterPolicy = run.RiskAfterPolicy,
                MustNotHitRiskAfterPolicy = run.MustNotHitRiskAfterPolicy,
                LifecycleRiskAfterPolicy = run.LifecycleRiskAfterPolicy,
                FormalOutputChanged = run.FormalOutputChanged,
                FormalSelectedSetChanged = run.FormalSelectedSetChanged,
                FormalPackageWritten = run.FormalPackageWritten,
                PackageOutputChanged = run.PackageOutputChanged,
                PackingPolicyChanged = run.PackingPolicyChanged,
                RuntimeMutated = run.RuntimeMutated,
                VectorStoreBindingChanged = run.VectorStoreBindingChanged,
                RollbackVerified = run.RollbackVerified,
                KillSwitchVerified = run.KillSwitchVerified,
                UseForRuntime = run.UseForRuntime,
                FormalRetrievalAllowed = run.FormalRetrievalAllowed,
                RuntimeSwitchAllowed = run.RuntimeSwitchAllowed,
                ReadyForRuntimeSwitch = run.ReadyForRuntimeSwitch,
                BlockedReasons = run.BlockedReasons
            });
        }

        var failedRunCount = runs.Count(static run => !run.DryRunPassed || !run.GatePassed);
        if (failedRunCount > 0)
            blocked.Add("DryRunGateFailed");
        var proposalConstraintsApplied = runs.Count > 0 && runs.All(static run => run.ProposalConstraintsApplied);
        if (!proposalConstraintsApplied)
            blocked.Add("ProposalConstraintsNotApplied");
        var requestCountTotal = runs.Sum(static run => run.RequestCount);
        var durationMinutes = Math.Max(0, options.SimulatedDurationMinutes);
        var errorCount = Math.Max(0, options.SimulatedErrorCount);
        var requestWindowOk = requestCountTotal > 0 && requestCountTotal <= maxRequestCount && durationMinutes <= maxDurationMinutes && errorCount <= maxErrorCount;
        if (!requestWindowOk)
            blocked.Add("RequestDurationErrorWindowViolation");
        var sampleObservationCount = requestCountTotal;
        var observationWindowOk = runCount >= minRunCount && sampleObservationCount >= minSampleCount;
        if (!observationWindowOk)
            blocked.Add("ObservationWindowConstraintViolation");

        var distinctSignatures = runs.Select(static run => run.StableSignature).Distinct(StringComparer.Ordinal).Count();
        var deterministicStable = runs.Count > 0 && distinctSignatures == 1;
        if (!deterministicStable)
            blocked.Add("DryRunSignatureNotStable");
        var addMin = runs.Count == 0 ? 0 : runs.Min(static run => run.PreviewAddCount);
        var addMax = runs.Count == 0 ? 0 : runs.Max(static run => run.PreviewAddCount);
        var removeMin = runs.Count == 0 ? 0 : runs.Min(static run => run.PreviewRemoveCount);
        var removeMax = runs.Count == 0 ? 0 : runs.Max(static run => run.PreviewRemoveCount);
        var addRemoveStable = runs.Count > 0 && addMin == addMax && removeMin == removeMax && addMin > 0 && removeMin > 0;
        if (!addRemoveStable)
            blocked.Add("PreviewDeltaNotStable");
        var appliedAddMax = runs.Count == 0 ? 0 : runs.Max(static run => run.AppliedAddCount);
        var appliedRemoveMax = runs.Count == 0 ? 0 : runs.Max(static run => run.AppliedRemoveCount);
        var appliedDeltaZero = appliedAddMax == 0 && appliedRemoveMax == 0;
        if (!appliedDeltaZero)
            blocked.Add("AppliedDeltaDetected");
        var riskMax = runs.Count == 0 ? 0 : runs.Max(static run => run.RiskAfterPolicy);
        var mustNotMax = runs.Count == 0 ? 0 : runs.Max(static run => run.MustNotHitRiskAfterPolicy);
        var lifecycleMax = runs.Count == 0 ? 0 : runs.Max(static run => run.LifecycleRiskAfterPolicy);
        if (riskMax != 0 || mustNotMax != 0 || lifecycleMax != 0)
            blocked.Add("RiskDetected");
        var tokenTotalMax = runs.Count == 0 ? 0 : runs.Max(static run => Math.Abs(run.TokenDeltaTotal));
        var tokenMaxMax = runs.Count == 0 ? 0 : runs.Max(static run => run.TokenDeltaMax);
        var priorityInversions = runs.Sum(static run => run.PriorityInversionCount);
        var sectionMismatches = runs.Sum(static run => run.SectionMismatchCount);
        var droppedRequired = runs.Sum(static run => run.DroppedRequiredCandidateCount);
        var tokenSectionOk = tokenTotalMax <= maxTokenTotal && tokenMaxMax <= maxTokenMax && priorityInversions == 0 && sectionMismatches == 0 && droppedRequired == 0;
        if (!tokenSectionOk)
            blocked.Add("TokenSectionPriorityGateViolation");
        var rollbackVerified = runs.Count > 0 && runs.All(static run => run.RollbackVerified);
        var killSwitchVerified = runs.Count > 0 && runs.All(static run => run.KillSwitchVerified);
        if (!rollbackVerified)
            blocked.Add("RollbackUnavailable");
        if (!killSwitchVerified)
            blocked.Add("KillSwitchUnavailable");
        var formalSelectedSetChanged = runs.Any(static run => run.FormalSelectedSetChanged);
        var formalOutputMax = runs.Count == 0 ? 0 : runs.Max(static run => run.FormalOutputChanged);
        var formalPackageWritten = runs.Any(static run => run.FormalPackageWritten);
        var packageOutputChanged = runs.Any(static run => run.PackageOutputChanged);
        var packingPolicyChanged = runs.Any(static run => run.PackingPolicyChanged);
        var runtimeMutated = runs.Any(static run => run.RuntimeMutated);
        var vectorBindingChanged = runs.Any(static run => run.VectorStoreBindingChanged);
        if (formalSelectedSetChanged || formalOutputMax != 0 || formalPackageWritten || packageOutputChanged || packingPolicyChanged || runtimeMutated || vectorBindingChanged)
            blocked.Add("FormalOrRuntimeInvariantChanged");
        var useForRuntime = runs.Any(static run => run.UseForRuntime) || options.UseForRuntime;
        var formalRetrievalAllowed = runs.Any(static run => run.FormalRetrievalAllowed) || options.FormalRetrievalAllowed;
        var runtimeSwitchAllowed = runs.Any(static run => run.RuntimeSwitchAllowed) || options.RuntimeSwitchAllowed;
        var readyForRuntimeSwitch = runs.Any(static run => run.ReadyForRuntimeSwitch) || options.ReadyForRuntimeSwitch;
        if (useForRuntime || formalRetrievalAllowed || runtimeSwitchAllowed || readyForRuntimeSwitch)
            blocked.Add("RuntimeSwitchAttemptDetected");

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase).ToArray();
        var passed = distinctBlocked.Length == 0;        return new ControlledShadowMergeObservationWindowReport
        {
            OperationId = $"controlled-shadow-merge-observation-window-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            ObservationPassed = passed,
            GatePassed = gateMode && passed,
            Recommendation = passed ? ControlledShadowMergeObservationWindowRecommendations.ReadyForControlledShadowMergeObservationFreeze : ResolveRecommendation(distinctBlocked),
            ProposalId = proposal?.ProposalId ?? string.Empty,
            ProposalGatePassed = proposalPassed,
            DryRunGatePassed = dryRunGate?.GatePassed == true,
            ProposalConstraintsApplied = proposalConstraintsApplied,
            SelectedScopes = proposal?.SelectedScopes ?? Array.Empty<string>(),
            ObservationRunCount = runCount,
            MinObservationRunCount = minRunCount,
            FailedRunCount = failedRunCount,
            RequestCountTotal = requestCountTotal,
            MaxRequestCount = maxRequestCount,
            DurationMinutes = durationMinutes,
            MaxDurationMinutes = maxDurationMinutes,
            ErrorCount = errorCount,
            MaxErrorCount = maxErrorCount,
            RequestDurationErrorWindowEnforced = requestWindowOk,
            SampleObservationCount = sampleObservationCount,
            MinSampleObservationCount = minSampleCount,
            ObservationWindowLimitEnforced = observationWindowOk,
            DistinctStableSignatureCount = distinctSignatures,
            DeterministicDryRunStable = deterministicStable,
            PreviewAddRemoveStable = addRemoveStable,
            PreviewAddCountMin = addMin,
            PreviewAddCountMax = addMax,
            PreviewAddCountTotal = runs.Sum(static run => run.PreviewAddCount),
            PreviewRemoveCountMin = removeMin,
            PreviewRemoveCountMax = removeMax,
            PreviewRemoveCountTotal = runs.Sum(static run => run.PreviewRemoveCount),
            AppliedAddCountMax = appliedAddMax,
            AppliedRemoveCountMax = appliedRemoveMax,
            AppliedDeltaZero = appliedDeltaZero,
            RiskAfterPolicyMax = riskMax,
            MustNotHitRiskAfterPolicyMax = mustNotMax,
            LifecycleRiskAfterPolicyMax = lifecycleMax,
            TokenDeltaTotalMax = tokenTotalMax,
            TokenDeltaMaxMax = tokenMaxMax,
            TokenDeltaWithinBudget = tokenTotalMax <= maxTokenTotal && tokenMaxMax <= maxTokenMax,
            PriorityInversionCountTotal = priorityInversions,
            SectionMismatchCountTotal = sectionMismatches,
            DroppedRequiredCandidateCountTotal = droppedRequired,
            RollbackVerified = rollbackVerified,
            KillSwitchVerified = killSwitchVerified,
            FormalSelectedSetChanged = formalSelectedSetChanged,
            FormalOutputChangedMax = formalOutputMax,
            FormalPackageWritten = formalPackageWritten,
            PackageOutputChanged = packageOutputChanged,
            PackingPolicyChanged = packingPolicyChanged,
            RuntimeMutated = runtimeMutated,
            VectorStoreBindingChanged = vectorBindingChanged,
            UseForRuntime = useForRuntime,
            FormalRetrievalAllowed = formalRetrievalAllowed,
            RuntimeSwitchAllowed = runtimeSwitchAllowed,
            ReadyForRuntimeSwitch = readyForRuntimeSwitch,
            Runs = runs,
            BlockedReasons = distinctBlocked
        };
    }

    private static ControlledShadowMergeDryRunGateOptions BuildDryRunOptions(ControlledShadowMergeObservationWindowOptions options) => new()
    {
        Enabled = options.Enabled,
        SimulatedAppliedAddCount = options.SimulatedAppliedAddCount,
        SimulatedAppliedRemoveCount = options.SimulatedAppliedRemoveCount,
        SimulatedTokenDeltaTotal = options.SimulatedTokenDeltaTotal,
        SimulatedTokenDeltaMax = options.SimulatedTokenDeltaMax,
        SimulatedPriorityInversionCount = options.SimulatedPriorityInversionCount,
        SimulatedSectionMismatchCount = options.SimulatedSectionMismatchCount,
        SimulatedDroppedRequiredCandidateCount = options.SimulatedDroppedRequiredCandidateCount,
        RollbackUnavailable = options.RollbackUnavailable,
        KillSwitchUnavailable = options.KillSwitchUnavailable,
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
    };

    private static string ComputeStableSignature(ControlledShadowMergeDryRunGateReport r)
    {
        var b = new StringBuilder();
        b.Append(r.DryRunPassed).Append('|')
            .Append(r.GatePassed).Append('|')
            .Append(r.ProposalConstraintsApplied).Append('|')
            .Append(r.RequestCount).Append('|')
            .Append(r.PreviewAddCount).Append('|')
            .Append(r.PreviewRemoveCount).Append('|')
            .Append(r.AppliedAddCount).Append('|')
            .Append(r.AppliedRemoveCount).Append('|')
            .Append(r.TokenDeltaTotal).Append('|')
            .Append(r.TokenDeltaMax).Append('|')
            .Append(r.PriorityInversionCount).Append('|')
            .Append(r.SectionMismatchCount).Append('|')
            .Append(r.DroppedRequiredCandidateCount).Append('|')
            .Append(r.RiskAfterPolicy).Append('|')
            .Append(r.MustNotHitRiskAfterPolicy).Append('|')
            .Append(r.LifecycleRiskAfterPolicy).Append('|')
            .Append(r.FormalOutputChanged).Append('|')
            .Append(r.PackageOutputChanged).Append('|')
            .Append(r.PackingPolicyChanged).Append('|')
            .Append(r.RuntimeMutated).Append('|')
            .Append(r.VectorStoreBindingChanged).AppendLine();
        foreach (var check in r.ConstraintChecks.OrderBy(static check => check.Key, StringComparer.OrdinalIgnoreCase))
            b.Append(check.Key).Append('=').Append(check.Value).AppendLine();
        foreach (var reason in r.BlockedReasons.OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase))
            b.Append("blocked=").Append(reason).AppendLine();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(b.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Contains("ControlledShadowMergeDryRunGateMissingOrNotPassed", StringComparer.OrdinalIgnoreCase))
            return ControlledShadowMergeObservationWindowRecommendations.BlockedByMissingDryRunGate;
        if (blocked.Contains("ObservationWindowConstraintViolation", StringComparer.OrdinalIgnoreCase) ||
            blocked.Contains("RequestDurationErrorWindowViolation", StringComparer.OrdinalIgnoreCase) ||
            blocked.Contains("ProposalConstraintsNotApplied", StringComparer.OrdinalIgnoreCase))
            return ControlledShadowMergeObservationWindowRecommendations.BlockedByConstraintViolation;
        if (blocked.Contains("DryRunSignatureNotStable", StringComparer.OrdinalIgnoreCase) || blocked.Contains("PreviewDeltaNotStable", StringComparer.OrdinalIgnoreCase))
            return ControlledShadowMergeObservationWindowRecommendations.BlockedByInstability;
        if (blocked.Contains("RiskDetected", StringComparer.OrdinalIgnoreCase))
            return ControlledShadowMergeObservationWindowRecommendations.BlockedByRisk;
        if (blocked.Contains("TokenSectionPriorityGateViolation", StringComparer.OrdinalIgnoreCase))
            return ControlledShadowMergeObservationWindowRecommendations.BlockedByTokenSectionPriority;
        if (blocked.Contains("AppliedDeltaDetected", StringComparer.OrdinalIgnoreCase) ||
            blocked.Contains("FormalOrRuntimeInvariantChanged", StringComparer.OrdinalIgnoreCase) ||
            blocked.Contains("RuntimeSwitchAttemptDetected", StringComparer.OrdinalIgnoreCase))
            return ControlledShadowMergeObservationWindowRecommendations.BlockedByRuntimeInvariant;
        if (blocked.Contains("RollbackUnavailable", StringComparer.OrdinalIgnoreCase) || blocked.Contains("KillSwitchUnavailable", StringComparer.OrdinalIgnoreCase))
            return ControlledShadowMergeObservationWindowRecommendations.BlockedByRollbackOrKillSwitch;
        if (blocked.Contains("DryRunGateFailed", StringComparer.OrdinalIgnoreCase) || blocked.Any(static reason => reason.EndsWith("MissingOrNotPassed", StringComparison.OrdinalIgnoreCase)))
            return ControlledShadowMergeObservationWindowRecommendations.BlockedByMissingGate;
        return ControlledShadowMergeObservationWindowRecommendations.KeepPreviewOnly;
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
}
public sealed class ControlledShadowMergeObservationWindowOptions
{
    public bool Enabled { get; init; } = true;
    public int ObservationRunCount { get; init; }
    public int FallbackMinObservationRunCount { get; init; } = 10;
    public int FallbackMinSampleObservationCount { get; init; } = 120;
    public int FallbackMaxRequestCount { get; init; } = 120;
    public int FallbackMaxDurationMinutes { get; init; } = 30;
    public int FallbackMaxErrorCount { get; init; }
    public int FallbackMaxTokenDeltaTotal { get; init; } = 128;
    public int FallbackMaxTokenDeltaPerSample { get; init; } = 32;
    public int SimulatedDurationMinutes { get; init; }
    public int SimulatedErrorCount { get; init; }
    public int SimulatedAppliedAddCount { get; init; }
    public int SimulatedAppliedRemoveCount { get; init; }
    public int SimulatedTokenDeltaTotal { get; init; }
    public int SimulatedTokenDeltaMax { get; init; }
    public int SimulatedPriorityInversionCount { get; init; }
    public int SimulatedSectionMismatchCount { get; init; }
    public int SimulatedDroppedRequiredCandidateCount { get; init; }
    public bool RollbackUnavailable { get; init; }
    public bool KillSwitchUnavailable { get; init; }
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

public sealed class ControlledShadowMergeObservationWindowRunResult
{
    public int RunIndex { get; init; }
    public bool DryRunPassed { get; init; }
    public bool GatePassed { get; init; }
    public string StableSignature { get; init; } = string.Empty;
    public int RequestCount { get; init; }
    public bool ProposalConstraintsApplied { get; init; }
    public int PreviewAddCount { get; init; }
    public int PreviewRemoveCount { get; init; }
    public int AppliedAddCount { get; init; }
    public int AppliedRemoveCount { get; init; }
    public int TokenDeltaTotal { get; init; }
    public int TokenDeltaMax { get; init; }
    public int PriorityInversionCount { get; init; }
    public int SectionMismatchCount { get; init; }
    public int DroppedRequiredCandidateCount { get; init; }
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
    public bool RollbackVerified { get; init; }
    public bool KillSwitchVerified { get; init; }
    public bool UseForRuntime { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed class ControlledShadowMergeObservationWindowReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ObservationPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = ControlledShadowMergeObservationWindowRecommendations.KeepPreviewOnly;
    public string ProposalId { get; init; } = string.Empty;
    public bool ProposalGatePassed { get; init; }
    public bool DryRunGatePassed { get; init; }
    public bool ProposalConstraintsApplied { get; init; }
    public IReadOnlyList<string> SelectedScopes { get; init; } = Array.Empty<string>();
    public int ObservationRunCount { get; init; }
    public int MinObservationRunCount { get; init; }
    public int FailedRunCount { get; init; }
    public int RequestCountTotal { get; init; }
    public int MaxRequestCount { get; init; }
    public int DurationMinutes { get; init; }
    public int MaxDurationMinutes { get; init; }
    public int ErrorCount { get; init; }
    public int MaxErrorCount { get; init; }
    public bool RequestDurationErrorWindowEnforced { get; init; }
    public int SampleObservationCount { get; init; }
    public int MinSampleObservationCount { get; init; }
    public bool ObservationWindowLimitEnforced { get; init; }
    public int DistinctStableSignatureCount { get; init; }
    public bool DeterministicDryRunStable { get; init; }
    public bool PreviewAddRemoveStable { get; init; }
    public int PreviewAddCountMin { get; init; }
    public int PreviewAddCountMax { get; init; }
    public int PreviewAddCountTotal { get; init; }
    public int PreviewRemoveCountMin { get; init; }
    public int PreviewRemoveCountMax { get; init; }
    public int PreviewRemoveCountTotal { get; init; }
    public int AppliedAddCountMax { get; init; }
    public int AppliedRemoveCountMax { get; init; }
    public bool AppliedDeltaZero { get; init; }
    public int RiskAfterPolicyMax { get; init; }
    public int MustNotHitRiskAfterPolicyMax { get; init; }
    public int LifecycleRiskAfterPolicyMax { get; init; }
    public int TokenDeltaTotalMax { get; init; }
    public int TokenDeltaMaxMax { get; init; }
    public bool TokenDeltaWithinBudget { get; init; }
    public int PriorityInversionCountTotal { get; init; }
    public int SectionMismatchCountTotal { get; init; }
    public int DroppedRequiredCandidateCountTotal { get; init; }
    public bool RollbackVerified { get; init; }
    public bool KillSwitchVerified { get; init; }
    public bool FormalSelectedSetChanged { get; init; }
    public int FormalOutputChangedMax { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool UseForRuntime { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public IReadOnlyList<ControlledShadowMergeObservationWindowRunResult> Runs { get; init; } = Array.Empty<ControlledShadowMergeObservationWindowRunResult>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public static class ControlledShadowMergeObservationWindowRecommendations
{
    public const string ReadyForControlledShadowMergeObservationFreeze = nameof(ReadyForControlledShadowMergeObservationFreeze);
    public const string BlockedByMissingDryRunGate = nameof(BlockedByMissingDryRunGate);
    public const string BlockedByMissingGate = nameof(BlockedByMissingGate);
    public const string BlockedByConstraintViolation = nameof(BlockedByConstraintViolation);
    public const string BlockedByInstability = nameof(BlockedByInstability);
    public const string BlockedByTokenSectionPriority = nameof(BlockedByTokenSectionPriority);
    public const string BlockedByRollbackOrKillSwitch = nameof(BlockedByRollbackOrKillSwitch);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByRuntimeInvariant = nameof(BlockedByRuntimeInvariant);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}