using ContextCore.Abstractions.Models;
using System.Text;

namespace ContextCore.Core.Services;

/// <summary>
/// V6.13 controlled shadow merge freeze / promotion decision。冻结 V6.11 observation window 结果，
/// 只允许进入下一阶段 proposal，不应用 merge，不改变 formal selected set、package、PackingPolicy、runtime 或 vector binding。
/// </summary>
public sealed class ControlledShadowMergeFreezeRunner
{
    public ControlledShadowMergeFreezeReport BuildFreeze(
        ControlledShadowMergeObservationWindowReport? observationWindowGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ControlledShadowMergeFreezeOptions? options = null)
        => BuildReport(observationWindowGate, runtimeChangeGate, options, promotionDecision: false);

    public ControlledShadowMergeFreezeReport BuildPromotionDecision(
        ControlledShadowMergeObservationWindowReport? observationWindowGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ControlledShadowMergeFreezeOptions? options = null)
        => BuildReport(observationWindowGate, runtimeChangeGate, options, promotionDecision: true);

    public static string BuildMarkdown(string title, ControlledShadowMergeFreezeReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.CreatedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine($"- FreezePassed: `{report.FreezePassed}`");
        builder.AppendLine($"- PromotionDecisionPassed: `{report.PromotionDecisionPassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- PromotionDecision: `{report.PromotionDecision}`");
        builder.AppendLine($"- NextAllowedPhase: `{report.NextAllowedPhase}`");
        builder.AppendLine($"- AllowedMode: `{report.AllowedMode}`");
        builder.AppendLine($"- ObservationWindowGatePassed: `{report.ObservationWindowGatePassed}`");
        builder.AppendLine($"- RuntimeChangeGatePassed: `{report.RuntimeChangeGatePassed}`");
        builder.AppendLine($"- ProposalId: `{report.ProposalId}`");
        builder.AppendLine($"- ObservationRunCount/MinObservationRunCount: `{report.ObservationRunCount}` / `{report.MinObservationRunCount}`");
        builder.AppendLine($"- RequestCountTotal/MaxRequestCount: `{report.RequestCountTotal}` / `{report.MaxRequestCount}`");
        builder.AppendLine($"- SampleObservationCount/MinSampleObservationCount: `{report.SampleObservationCount}` / `{report.MinSampleObservationCount}`");
        builder.AppendLine($"- ProposalConstraintsApplied: `{report.ProposalConstraintsApplied}`");
        builder.AppendLine($"- RequestDurationErrorWindowEnforced: `{report.RequestDurationErrorWindowEnforced}`");
        builder.AppendLine($"- ObservationWindowLimitEnforced: `{report.ObservationWindowLimitEnforced}`");
        builder.AppendLine($"- DeterministicDryRunStable: `{report.DeterministicDryRunStable}`");
        builder.AppendLine($"- PreviewAddRemoveStable: `{report.PreviewAddRemoveStable}`");
        builder.AppendLine($"- PreviewAdd min/max: `{report.PreviewAddCountMin}` / `{report.PreviewAddCountMax}`");
        builder.AppendLine($"- PreviewRemove min/max: `{report.PreviewRemoveCountMin}` / `{report.PreviewRemoveCountMax}`");
        builder.AppendLine($"- AppliedAdd/Remove max: `{report.AppliedAddCountMax}` / `{report.AppliedRemoveCountMax}`");
        builder.AppendLine($"- Risk/MustNot/Lifecycle max: `{report.RiskAfterPolicyMax}` / `{report.MustNotHitRiskAfterPolicyMax}` / `{report.LifecycleRiskAfterPolicyMax}`");
        builder.AppendLine($"- TokenDeltaTotalMax/TokenDeltaMaxMax: `{report.TokenDeltaTotalMax}` / `{report.TokenDeltaMaxMax}`");
        builder.AppendLine($"- PriorityInversionCountTotal: `{report.PriorityInversionCountTotal}`");
        builder.AppendLine($"- SectionMismatchCountTotal: `{report.SectionMismatchCountTotal}`");
        builder.AppendLine($"- DroppedRequiredCandidateCountTotal: `{report.DroppedRequiredCandidateCountTotal}`");
        builder.AppendLine($"- RollbackVerified: `{report.RollbackVerified}`");
        builder.AppendLine($"- KillSwitchVerified: `{report.KillSwitchVerified}`");
        builder.AppendLine($"- FormalSelectedSetChanged/FormalOutputChangedMax/FormalPackageWritten: `{report.FormalSelectedSetChanged}` / `{report.FormalOutputChangedMax}` / `{report.FormalPackageWritten}`");
        builder.AppendLine($"- Package/PackingPolicy/runtime/vector: `{report.PackageOutputChanged}` / `{report.PackingPolicyChanged}` / `{report.RuntimeMutated}` / `{report.VectorStoreBindingChanged}`");
        builder.AppendLine($"- UseForRuntime/FormalRetrieval/RuntimeSwitch/ReadyForRuntimeSwitch: `{report.UseForRuntime}` / `{report.FormalRetrievalAllowed}` / `{report.RuntimeSwitchAllowed}` / `{report.ReadyForRuntimeSwitch}`");
        AppendList(builder, "AllowedActions", report.AllowedActions);
        AppendList(builder, "ForbiddenActions", report.ForbiddenActions);
        AppendList(builder, "BlockedReasons", report.BlockedReasons);
        builder.AppendLine();
        builder.AppendLine("Controlled shadow merge freeze only. It does not apply preview add/remove, mutate formal output, write a formal package, alter PackingPolicy/package output, switch runtime, or bind vector storage.");
        return builder.ToString();
    }

    private static ControlledShadowMergeFreezeReport BuildReport(
        ControlledShadowMergeObservationWindowReport? observationWindowGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ControlledShadowMergeFreezeOptions? options,
        bool promotionDecision)
    {
        options ??= new ControlledShadowMergeFreezeOptions();
        var blocked = new List<string>();
        if (!options.Enabled)
            blocked.Add("ControlledShadowMergeFreezeDisabled");
        if (observationWindowGate is null || !observationWindowGate.GatePassed || !observationWindowGate.ObservationPassed)
            blocked.Add("ObservationWindowGateMissingOrNotPassed");
        if (runtimeChangeGate is null || !runtimeChangeGate.Passed)
            blocked.Add("RuntimeChangeGateMissingOrNotPassed");

        var proposalConstraintsApplied = observationWindowGate?.ProposalConstraintsApplied == true;
        if (!proposalConstraintsApplied)
            blocked.Add("ProposalConstraintsNotApplied");
        var requestWindowOk = observationWindowGate?.RequestDurationErrorWindowEnforced == true;
        if (!requestWindowOk)
            blocked.Add("RequestDurationErrorWindowViolation");
        var observationWindowOk = observationWindowGate?.ObservationWindowLimitEnforced == true &&
            (observationWindowGate?.ObservationRunCount ?? 0) >= options.MinimumObservationRunCount &&
            (observationWindowGate?.SampleObservationCount ?? 0) >= options.MinimumSampleObservationCount;
        if (!observationWindowOk)
            blocked.Add("ObservationWindowConstraintViolation");
        var deterministicStable = observationWindowGate?.DeterministicDryRunStable == true &&
            observationWindowGate.DistinctStableSignatureCount == 1;
        if (!deterministicStable)
            blocked.Add("DryRunSignatureNotStable");
        var previewAddCountMin = observationWindowGate?.PreviewAddCountMin ?? 0;
        var previewAddCountMax = observationWindowGate?.PreviewAddCountMax ?? 0;
        var previewRemoveCountMin = observationWindowGate?.PreviewRemoveCountMin ?? 0;
        var previewRemoveCountMax = observationWindowGate?.PreviewRemoveCountMax ?? 0;
        var previewAddRemoveStable = observationWindowGate?.PreviewAddRemoveStable == true &&
            previewAddCountMin > 0 &&
            previewRemoveCountMin > 0 &&
            previewAddCountMin == previewAddCountMax &&
            previewRemoveCountMin == previewRemoveCountMax;
        if (!previewAddRemoveStable)
            blocked.Add("PreviewDeltaNotStable");

        var appliedAddMax = Math.Max(observationWindowGate?.AppliedAddCountMax ?? 0, options.AppliedAddCountMax);
        var appliedRemoveMax = Math.Max(observationWindowGate?.AppliedRemoveCountMax ?? 0, options.AppliedRemoveCountMax);
        if (appliedAddMax != 0 || appliedRemoveMax != 0)
            blocked.Add("AppliedDeltaDetected");
        var riskMax = Math.Max(observationWindowGate?.RiskAfterPolicyMax ?? 0, options.RiskAfterPolicyMax);
        var mustNotMax = Math.Max(observationWindowGate?.MustNotHitRiskAfterPolicyMax ?? 0, options.MustNotHitRiskAfterPolicyMax);
        var lifecycleMax = Math.Max(observationWindowGate?.LifecycleRiskAfterPolicyMax ?? 0, options.LifecycleRiskAfterPolicyMax);
        if (riskMax != 0 || mustNotMax != 0 || lifecycleMax != 0)
            blocked.Add("RiskDetected");
        var tokenTotalMax = Math.Max(observationWindowGate?.TokenDeltaTotalMax ?? 0, options.TokenDeltaTotalMax);
        var tokenMaxMax = Math.Max(observationWindowGate?.TokenDeltaMaxMax ?? 0, options.TokenDeltaMaxMax);
        if (tokenTotalMax > options.TokenDeltaTotalBudget || tokenMaxMax > options.TokenDeltaMaxBudget)
            blocked.Add("TokenDeltaBudgetExceeded");
        var priorityInversionTotal = Math.Max(observationWindowGate?.PriorityInversionCountTotal ?? 0, options.PriorityInversionCountTotal);
        var sectionMismatchTotal = Math.Max(observationWindowGate?.SectionMismatchCountTotal ?? 0, options.SectionMismatchCountTotal);
        var droppedRequiredTotal = Math.Max(observationWindowGate?.DroppedRequiredCandidateCountTotal ?? 0, options.DroppedRequiredCandidateCountTotal);
        if (priorityInversionTotal != 0 || sectionMismatchTotal != 0 || droppedRequiredTotal != 0)
            blocked.Add("TokenSectionPriorityGateViolation");
        var rollbackVerified = observationWindowGate?.RollbackVerified == true && !options.RollbackUnavailable;
        var killSwitchVerified = observationWindowGate?.KillSwitchVerified == true && !options.KillSwitchUnavailable;
        if (!rollbackVerified)
            blocked.Add("RollbackUnavailable");
        if (!killSwitchVerified)
            blocked.Add("KillSwitchUnavailable");

        var formalSelectedSetChanged = (observationWindowGate?.FormalSelectedSetChanged ?? false) || options.FormalSelectedSetChanged;
        var formalOutputChangedMax = Math.Max(observationWindowGate?.FormalOutputChangedMax ?? 0, options.FormalOutputChangedMax);
        var formalPackageWritten = (observationWindowGate?.FormalPackageWritten ?? false) || options.FormalPackageWritten;
        var packageOutputChanged = (observationWindowGate?.PackageOutputChanged ?? false) || options.PackageOutputChanged;
        var packingPolicyChanged = (observationWindowGate?.PackingPolicyChanged ?? false) || options.PackingPolicyChanged;
        var runtimeMutated = (observationWindowGate?.RuntimeMutated ?? false) || options.RuntimeMutated;
        var vectorStoreBindingChanged = (observationWindowGate?.VectorStoreBindingChanged ?? false) || options.VectorStoreBindingChanged;
        if (formalSelectedSetChanged || formalOutputChangedMax != 0 || formalPackageWritten || packageOutputChanged || packingPolicyChanged || runtimeMutated || vectorStoreBindingChanged)
            blocked.Add("FormalOrRuntimeInvariantChanged");
        var useForRuntime = (observationWindowGate?.UseForRuntime ?? false) || options.UseForRuntime;
        var formalRetrievalAllowed = (observationWindowGate?.FormalRetrievalAllowed ?? false) || options.FormalRetrievalAllowed;
        var runtimeSwitchAllowed = (observationWindowGate?.RuntimeSwitchAllowed ?? false) || options.RuntimeSwitchAllowed;
        var readyForRuntimeSwitch = (observationWindowGate?.ReadyForRuntimeSwitch ?? false) || options.ReadyForRuntimeSwitch;
        if (useForRuntime || formalRetrievalAllowed || runtimeSwitchAllowed || readyForRuntimeSwitch)
            blocked.Add("RuntimeSwitchAttemptDetected");

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase).ToArray();
        var freezePassed = distinctBlocked.Length == 0;
        var promotionPassed = promotionDecision && freezePassed;
        return new ControlledShadowMergeFreezeReport
        {
            OperationId = $"controlled-shadow-merge-freeze-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            FreezePassed = freezePassed,
            PromotionDecisionPassed = promotionPassed,
            Recommendation = ResolveRecommendation(distinctBlocked, freezePassed, promotionDecision),
            PromotionDecision = freezePassed ? ControlledShadowMergePromotionDecisions.ReadyForControlledAppliedMergeProposal : ControlledShadowMergePromotionDecisions.KeepPreviewOnly,
            NextAllowedPhase = freezePassed ? "ControlledAppliedMergeProposal" : "KeepPreviewOnly",
            AllowedMode = promotionPassed ? "ControlledAppliedMergeProposalOnly" : "ControlledShadowMergeFreezeOnly",
            ProposalId = observationWindowGate?.ProposalId ?? string.Empty,
            ObservationWindowGatePassed = observationWindowGate?.GatePassed == true,
            RuntimeChangeGatePassed = runtimeChangeGate?.Passed == true,
            ProposalConstraintsApplied = proposalConstraintsApplied,
            ObservationRunCount = observationWindowGate?.ObservationRunCount ?? 0,
            MinObservationRunCount = Math.Max(options.MinimumObservationRunCount, observationWindowGate?.MinObservationRunCount ?? 0),
            RequestCountTotal = observationWindowGate?.RequestCountTotal ?? 0,
            MaxRequestCount = observationWindowGate?.MaxRequestCount ?? 0,
            RequestDurationErrorWindowEnforced = requestWindowOk,
            SampleObservationCount = observationWindowGate?.SampleObservationCount ?? 0,
            MinSampleObservationCount = Math.Max(options.MinimumSampleObservationCount, observationWindowGate?.MinSampleObservationCount ?? 0),
            ObservationWindowLimitEnforced = observationWindowOk,
            DeterministicDryRunStable = deterministicStable,
            DistinctStableSignatureCount = observationWindowGate?.DistinctStableSignatureCount ?? 0,
            PreviewAddRemoveStable = previewAddRemoveStable,
            PreviewAddCountMin = previewAddCountMin,
            PreviewAddCountMax = previewAddCountMax,
            PreviewRemoveCountMin = previewRemoveCountMin,
            PreviewRemoveCountMax = previewRemoveCountMax,
            AppliedAddCountMax = appliedAddMax,
            AppliedRemoveCountMax = appliedRemoveMax,
            RiskAfterPolicyMax = riskMax,
            MustNotHitRiskAfterPolicyMax = mustNotMax,
            LifecycleRiskAfterPolicyMax = lifecycleMax,
            TokenDeltaTotalMax = tokenTotalMax,
            TokenDeltaMaxMax = tokenMaxMax,
            PriorityInversionCountTotal = priorityInversionTotal,
            SectionMismatchCountTotal = sectionMismatchTotal,
            DroppedRequiredCandidateCountTotal = droppedRequiredTotal,
            RollbackVerified = rollbackVerified,
            KillSwitchVerified = killSwitchVerified,
            FormalSelectedSetChanged = formalSelectedSetChanged,
            FormalOutputChangedMax = formalOutputChangedMax,
            FormalPackageWritten = formalPackageWritten,
            PackageOutputChanged = packageOutputChanged,
            PackingPolicyChanged = packingPolicyChanged,
            RuntimeMutated = runtimeMutated,
            VectorStoreBindingChanged = vectorStoreBindingChanged,
            UseForRuntime = useForRuntime,
            FormalRetrievalAllowed = formalRetrievalAllowed,
            RuntimeSwitchAllowed = runtimeSwitchAllowed,
            ReadyForRuntimeSwitch = readyForRuntimeSwitch,
            AllowedActions = freezePassed
                ? new[] { "controlled applied merge proposal planning", "scope/limit review", "rollback and kill switch validation" }
                : Array.Empty<string>(),
            ForbiddenActions = new[]
            {
                "applied merge",
                "formal selected set mutation",
                "formal package write",
                "PackingPolicy mutation",
                "package output mutation",
                "runtime switch",
                "formal retrieval enable",
                "formal IVectorIndexStore binding mutation",
                "global default-on"
            },
            BlockedReasons = distinctBlocked
        };
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked, bool freezePassed, bool promotionDecision)
    {
        if (freezePassed)
            return promotionDecision
                ? ControlledShadowMergeFreezeRecommendations.ReadyForControlledAppliedMergeProposal
                : ControlledShadowMergeFreezeRecommendations.ReadyForControlledShadowMergePromotionDecision;
        if (blocked.Any(static reason => reason.EndsWith("MissingOrNotPassed", StringComparison.OrdinalIgnoreCase)))
            return ControlledShadowMergeFreezeRecommendations.BlockedByMissingGate;
        if (blocked.Contains("RiskDetected", StringComparer.OrdinalIgnoreCase))
            return ControlledShadowMergeFreezeRecommendations.BlockedByRisk;
        if (blocked.Contains("DryRunSignatureNotStable", StringComparer.OrdinalIgnoreCase) || blocked.Contains("PreviewDeltaNotStable", StringComparer.OrdinalIgnoreCase))
            return ControlledShadowMergeFreezeRecommendations.BlockedByInstability;
        if (blocked.Contains("ProposalConstraintsNotApplied", StringComparer.OrdinalIgnoreCase) || blocked.Contains("RequestDurationErrorWindowViolation", StringComparer.OrdinalIgnoreCase) || blocked.Contains("ObservationWindowConstraintViolation", StringComparer.OrdinalIgnoreCase))
            return ControlledShadowMergeFreezeRecommendations.BlockedByConstraintViolation;
        if (blocked.Contains("TokenDeltaBudgetExceeded", StringComparer.OrdinalIgnoreCase) || blocked.Contains("TokenSectionPriorityGateViolation", StringComparer.OrdinalIgnoreCase))
            return ControlledShadowMergeFreezeRecommendations.BlockedByTokenSectionPriority;
        if (blocked.Contains("RollbackUnavailable", StringComparer.OrdinalIgnoreCase) || blocked.Contains("KillSwitchUnavailable", StringComparer.OrdinalIgnoreCase))
            return ControlledShadowMergeFreezeRecommendations.BlockedByRollbackOrKillSwitch;
        if (blocked.Contains("AppliedDeltaDetected", StringComparer.OrdinalIgnoreCase) || blocked.Contains("FormalOrRuntimeInvariantChanged", StringComparer.OrdinalIgnoreCase) || blocked.Contains("RuntimeSwitchAttemptDetected", StringComparer.OrdinalIgnoreCase))
            return ControlledShadowMergeFreezeRecommendations.BlockedByRuntimeInvariant;
        return ControlledShadowMergeFreezeRecommendations.KeepPreviewOnly;
    }

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
}

public sealed class ControlledShadowMergeFreezeOptions
{
    public bool Enabled { get; init; } = true;
    public int MinimumObservationRunCount { get; init; } = 10;
    public int MinimumSampleObservationCount { get; init; } = 120;
    public int TokenDeltaTotalBudget { get; init; } = 128;
    public int TokenDeltaMaxBudget { get; init; } = 32;
    public int AppliedAddCountMax { get; init; }
    public int AppliedRemoveCountMax { get; init; }
    public int RiskAfterPolicyMax { get; init; }
    public int MustNotHitRiskAfterPolicyMax { get; init; }
    public int LifecycleRiskAfterPolicyMax { get; init; }
    public int TokenDeltaTotalMax { get; init; }
    public int TokenDeltaMaxMax { get; init; }
    public int PriorityInversionCountTotal { get; init; }
    public int SectionMismatchCountTotal { get; init; }
    public int DroppedRequiredCandidateCountTotal { get; init; }
    public bool RollbackUnavailable { get; init; }
    public bool KillSwitchUnavailable { get; init; }
    public bool UseForRuntime { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public bool FormalSelectedSetChanged { get; init; }
    public int FormalOutputChangedMax { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
}

public sealed class ControlledShadowMergeFreezeReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool FreezePassed { get; init; }
    public bool PromotionDecisionPassed { get; init; }
    public string Recommendation { get; init; } = ControlledShadowMergeFreezeRecommendations.KeepPreviewOnly;
    public string PromotionDecision { get; init; } = ControlledShadowMergePromotionDecisions.KeepPreviewOnly;
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";
    public string AllowedMode { get; init; } = "ControlledShadowMergeFreezeOnly";
    public string ProposalId { get; init; } = string.Empty;
    public bool ObservationWindowGatePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }
    public bool ProposalConstraintsApplied { get; init; }
    public int ObservationRunCount { get; init; }
    public int MinObservationRunCount { get; init; }
    public int RequestCountTotal { get; init; }
    public int MaxRequestCount { get; init; }
    public bool RequestDurationErrorWindowEnforced { get; init; }
    public int SampleObservationCount { get; init; }
    public int MinSampleObservationCount { get; init; }
    public bool ObservationWindowLimitEnforced { get; init; }
    public bool DeterministicDryRunStable { get; init; }
    public int DistinctStableSignatureCount { get; init; }
    public bool PreviewAddRemoveStable { get; init; }
    public int PreviewAddCountMin { get; init; }
    public int PreviewAddCountMax { get; init; }
    public int PreviewRemoveCountMin { get; init; }
    public int PreviewRemoveCountMax { get; init; }
    public int AppliedAddCountMax { get; init; }
    public int AppliedRemoveCountMax { get; init; }
    public int RiskAfterPolicyMax { get; init; }
    public int MustNotHitRiskAfterPolicyMax { get; init; }
    public int LifecycleRiskAfterPolicyMax { get; init; }
    public int TokenDeltaTotalMax { get; init; }
    public int TokenDeltaMaxMax { get; init; }
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
    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public static class ControlledShadowMergeFreezeRecommendations
{
    public const string ReadyForControlledShadowMergePromotionDecision = nameof(ReadyForControlledShadowMergePromotionDecision);
    public const string ReadyForControlledAppliedMergeProposal = nameof(ReadyForControlledAppliedMergeProposal);
    public const string BlockedByMissingGate = nameof(BlockedByMissingGate);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByInstability = nameof(BlockedByInstability);
    public const string BlockedByConstraintViolation = nameof(BlockedByConstraintViolation);
    public const string BlockedByTokenSectionPriority = nameof(BlockedByTokenSectionPriority);
    public const string BlockedByRollbackOrKillSwitch = nameof(BlockedByRollbackOrKillSwitch);
    public const string BlockedByRuntimeInvariant = nameof(BlockedByRuntimeInvariant);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

public static class ControlledShadowMergePromotionDecisions
{
    public const string ReadyForControlledAppliedMergeProposal = nameof(ReadyForControlledAppliedMergeProposal);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}
