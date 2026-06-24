using ContextCore.Abstractions.Models;
using System.Text;

namespace ContextCore.Core.Services;

/// <summary>
/// V6.7 shadow merge stability freeze / promotion decision.
/// 仅冻结 preview merge 的多轮稳定性结论；不应用 add/remove，不写 formal package，不改变 runtime 或 vector binding。
/// </summary>
public sealed class ShadowMergeStabilityFreezeRunner
{
    public ShadowMergeStabilityFreezeReport BuildFreeze(
        SourceDiverseShadowAdapterValidationReport? v66Gate,
        ShadowCandidateMergePreviewReport? v67Gate,
        ShadowCandidateMergePreviewObservationReport? observationGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ShadowMergeStabilityFreezeOptions? options = null)
    {
        options ??= new ShadowMergeStabilityFreezeOptions();
        return Build(v66Gate, v67Gate, observationGate, runtimeChangeGate, options, promotionDecision: false);
    }

    public ShadowMergeStabilityFreezeReport BuildPromotionDecision(
        SourceDiverseShadowAdapterValidationReport? v66Gate,
        ShadowCandidateMergePreviewReport? v67Gate,
        ShadowCandidateMergePreviewObservationReport? observationGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ShadowMergeStabilityFreezeOptions? options = null)
    {
        options ??= new ShadowMergeStabilityFreezeOptions();
        return Build(v66Gate, v67Gate, observationGate, runtimeChangeGate, options, promotionDecision: true);
    }

    public static string BuildMarkdown(string title, ShadowMergeStabilityFreezeReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine();
        b.AppendLine($"- FreezePassed: `{r.FreezePassed}`");
        b.AppendLine($"- PromotionDecisionPassed: `{r.PromotionDecisionPassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}`");
        b.AppendLine($"- PromotionDecision: `{r.PromotionDecision}`");
        b.AppendLine($"- NextAllowedPhase: `{r.NextAllowedPhase}`");
        b.AppendLine($"- AllowedMode: `{r.AllowedMode}`");
        b.AppendLine($"- V66GatePassed: `{r.V66GatePassed}`");
        b.AppendLine($"- V67GatePassed: `{r.V67GatePassed}`");
        b.AppendLine($"- ObservationGatePassed: `{r.ObservationGatePassed}`");
        b.AppendLine($"- RuntimeChangeGatePassed: `{r.RuntimeChangeGatePassed}`");
        b.AppendLine($"- ObservationRunCount: `{r.ObservationRunCount}`");
        b.AppendLine($"- SampleObservationCount: `{r.SampleObservationCount}`");
        b.AppendLine($"- DeterministicPreviewStable: `{r.DeterministicPreviewStable}`");
        b.AppendLine($"- DistinctStableSignatureCount: `{r.DistinctStableSignatureCount}`");
        b.AppendLine($"- PreviewAddCount min/max: `{r.PreviewAddCountMin}` / `{r.PreviewAddCountMax}`");
        b.AppendLine($"- PreviewRemoveCount min/max: `{r.PreviewRemoveCountMin}` / `{r.PreviewRemoveCountMax}`");
        b.AppendLine($"- AppliedAdd/Remove max: `{r.AppliedAddCountMax}` / `{r.AppliedRemoveCountMax}`");
        b.AppendLine($"- Risk/MustNot/Lifecycle max: `{r.RiskAfterPolicyMax}` / `{r.MustNotHitRiskAfterPolicyMax}` / `{r.LifecycleRiskAfterPolicyMax}`");
        b.AppendLine($"- TokenDeltaTotalMax/TokenDeltaMaxMax: `{r.TokenDeltaTotalMax}` / `{r.TokenDeltaMaxMax}`");
        b.AppendLine($"- PriorityInversionCountTotal: `{r.PriorityInversionCountTotal}`");
        b.AppendLine($"- SectionMismatchCountTotal: `{r.SectionMismatchCountTotal}`");
        b.AppendLine($"- FormalSelectedSetChanged: `{r.FormalSelectedSetChanged}`");
        b.AppendLine($"- FormalOutputChangedMax: `{r.FormalOutputChangedMax}`");
        b.AppendLine($"- FormalPackageWritten: `{r.FormalPackageWritten}`");
        b.AppendLine($"- PackageOutputChanged: `{r.PackageOutputChanged}`");
        b.AppendLine($"- PackingPolicyChanged: `{r.PackingPolicyChanged}`");
        b.AppendLine($"- RuntimeMutated: `{r.RuntimeMutated}`");
        b.AppendLine($"- VectorStoreBindingChanged: `{r.VectorStoreBindingChanged}`");
        b.AppendLine($"- FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}`");
        b.AppendLine($"- RuntimeSwitchAllowed: `{r.RuntimeSwitchAllowed}`");
        b.AppendLine($"- ReadyForRuntimeSwitch: `{r.ReadyForRuntimeSwitch}`");
        AppendList(b, "AllowedActions", r.AllowedActions);
        AppendList(b, "ForbiddenActions", r.ForbiddenActions);
        AppendList(b, "BlockedReasons", r.BlockedReasons);
        b.AppendLine();
        b.AppendLine("This decision freezes preview merge stability only. It does not apply the preview delta, mutate the formal selected set, write a formal package, alter PackingPolicy/package output, switch runtime, or bind a formal vector store.");
        return b.ToString();
    }

    private static ShadowMergeStabilityFreezeReport Build(
        SourceDiverseShadowAdapterValidationReport? v66Gate,
        ShadowCandidateMergePreviewReport? v67Gate,
        ShadowCandidateMergePreviewObservationReport? observationGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ShadowMergeStabilityFreezeOptions options,
        bool promotionDecision)
    {
        var blocked = new List<string>();
        if (v66Gate is null || !v66Gate.GatePassed)
            blocked.Add("V66GateMissingOrNotPassed");
        if (v67Gate is null || !v67Gate.GatePassed)
            blocked.Add("V67GateMissingOrNotPassed");
        if (observationGate is null || !observationGate.GatePassed)
            blocked.Add("ObservationGateMissingOrNotPassed");
        if (runtimeChangeGate is null || !runtimeChangeGate.Passed)
            blocked.Add("RuntimeChangeGateMissingOrNotPassed");

        var observationRunCount = observationGate?.ObservationRunCount ?? 0;
        var sampleObservationCount = observationGate?.SampleObservationCount ?? 0;
        if (observationRunCount < options.MinimumObservationRunCount ||
            sampleObservationCount < options.MinimumSampleObservationCount)
        {
            blocked.Add("InsufficientObservationWindow");
        }

        var stable = observationGate?.DeterministicPreviewStable == true &&
                     observationGate.DistinctStableSignatureCount == 1 &&
                     observationGate.PreviewAddRemoveStable;
        if (!stable)
            blocked.Add("PreviewMergeNotStable");

        var addMin = observationGate?.PreviewAddCountMin ?? 0;
        var addMax = observationGate?.PreviewAddCountMax ?? 0;
        var removeMin = observationGate?.PreviewRemoveCountMin ?? 0;
        var removeMax = observationGate?.PreviewRemoveCountMax ?? 0;
        if (addMin <= 0 || removeMin <= 0 || addMin != addMax || removeMin != removeMax)
            blocked.Add("PreviewDeltaNotStable");

        var appliedAddMax = observationGate?.AppliedAddCountMax ?? 0;
        var appliedRemoveMax = observationGate?.AppliedRemoveCountMax ?? 0;
        if (appliedAddMax != 0 || appliedRemoveMax != 0)
            blocked.Add("AppliedDeltaDetected");

        var riskMax = observationGate?.RiskAfterPolicyMax ?? 0;
        var mustNotMax = observationGate?.MustNotHitRiskAfterPolicyMax ?? 0;
        var lifecycleMax = observationGate?.LifecycleRiskAfterPolicyMax ?? 0;
        if (riskMax != 0 || mustNotMax != 0 || lifecycleMax != 0)
            blocked.Add("RiskDetected");

        var tokenDeltaTotalMax = observationGate?.TokenDeltaTotalMax ?? 0;
        var tokenDeltaMaxMax = observationGate?.TokenDeltaMaxMax ?? 0;
        if (tokenDeltaTotalMax > options.TokenDeltaBudget || tokenDeltaMaxMax > options.TokenDeltaMaxBudget)
            blocked.Add("TokenDeltaBudgetExceeded");

        var priorityInversions = observationGate?.PriorityInversionCountTotal ?? 0;
        var sectionMismatches = observationGate?.SectionMismatchCountTotal ?? 0;
        if (priorityInversions != 0 || sectionMismatches != 0)
            blocked.Add("PriorityOrSectionMismatch");

        var formalSelectedSetChanged = (observationGate?.FormalSelectedSetChanged ?? false) || options.FormalSelectedSetChanged;
        var formalOutputChangedMax = Math.Max(observationGate?.FormalOutputChangedMax ?? 0, options.FormalOutputChanged);
        var formalPackageWritten = (observationGate?.FormalPackageWritten ?? false) || options.FormalPackageWritten;
        var packageOutputChanged = (observationGate?.PackageOutputChanged ?? false) || options.PackageOutputChanged;
        var packingPolicyChanged = (observationGate?.PackingPolicyChanged ?? false) || options.PackingPolicyChanged;
        var runtimeMutated = (observationGate?.RuntimeMutated ?? false) || options.RuntimeMutated;
        var vectorStoreBindingChanged = (observationGate?.VectorStoreBindingChanged ?? false) || options.VectorStoreBindingChanged;
        if (formalSelectedSetChanged ||
            formalOutputChangedMax != 0 ||
            formalPackageWritten ||
            packageOutputChanged ||
            packingPolicyChanged ||
            runtimeMutated ||
            vectorStoreBindingChanged)
        {
            blocked.Add("RuntimeOrFormalInvariantChanged");
        }

        var useForRuntime = (observationGate?.UseForRuntime ?? false) || options.UseForRuntime;
        var formalRetrievalAllowed = (observationGate?.FormalRetrievalAllowed ?? false) || options.FormalRetrievalAllowed;
        var runtimeSwitchAllowed = (observationGate?.RuntimeSwitchAllowed ?? false) || options.RuntimeSwitchAllowed;
        var readyForRuntimeSwitch = (observationGate?.ReadyForRuntimeSwitch ?? false) || options.ReadyForRuntimeSwitch;
        if (useForRuntime)
            blocked.Add("UseForRuntimeAttemptDetected");
        if (formalRetrievalAllowed)
            blocked.Add("FormalRetrievalAttemptDetected");
        if (runtimeSwitchAllowed)
            blocked.Add("RuntimeSwitchAttemptDetected");
        if (readyForRuntimeSwitch)
            blocked.Add("ReadyForRuntimeSwitchAttemptDetected");

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static reason => reason).ToArray();
        var freezePassed = distinctBlocked.Length == 0;
        var promotionPassed = promotionDecision && freezePassed;
        return new ShadowMergeStabilityFreezeReport
        {
            OperationId = $"shadow-merge-stability-freeze-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            FreezePassed = freezePassed,
            PromotionDecisionPassed = promotionPassed,
            Recommendation = ResolveRecommendation(distinctBlocked, freezePassed, promotionDecision),
            PromotionDecision = freezePassed
                ? ShadowMergePromotionDecisions.ReadyForControlledMergeProposal
                : ShadowMergePromotionDecisions.KeepPreviewOnly,
            NextAllowedPhase = freezePassed ? "ControlledMergeProposal" : "KeepPreviewOnly",
            AllowedMode = promotionPassed ? "ControlledMergeProposalOnly" : "PreviewMergeFreezeOnly",
            V66GatePassed = v66Gate?.GatePassed == true,
            V67GatePassed = v67Gate?.GatePassed == true,
            ObservationGatePassed = observationGate?.GatePassed == true,
            RuntimeChangeGatePassed = runtimeChangeGate?.Passed == true,
            ObservationRunCount = observationRunCount,
            SampleObservationCount = sampleObservationCount,
            DeterministicPreviewStable = observationGate?.DeterministicPreviewStable ?? false,
            DistinctStableSignatureCount = observationGate?.DistinctStableSignatureCount ?? 0,
            PreviewAddRemoveStable = observationGate?.PreviewAddRemoveStable ?? false,
            PreviewAddCountMin = addMin,
            PreviewAddCountMax = addMax,
            PreviewRemoveCountMin = removeMin,
            PreviewRemoveCountMax = removeMax,
            AppliedAddCountMax = appliedAddMax,
            AppliedRemoveCountMax = appliedRemoveMax,
            RiskAfterPolicyMax = riskMax,
            MustNotHitRiskAfterPolicyMax = mustNotMax,
            LifecycleRiskAfterPolicyMax = lifecycleMax,
            TokenDeltaTotalMax = tokenDeltaTotalMax,
            TokenDeltaMaxMax = tokenDeltaMaxMax,
            PriorityInversionCountTotal = priorityInversions,
            SectionMismatchCountTotal = sectionMismatches,
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
                ? new[] { "controlled merge proposal planning", "preview merge review", "rollback plan validation" }
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
                ? ShadowMergeStabilityFreezeRecommendations.ReadyForControlledMergeProposal
                : ShadowMergeStabilityFreezeRecommendations.ReadyForShadowMergePromotionDecision;
        if (blocked.Contains("V66GateMissingOrNotPassed", StringComparer.OrdinalIgnoreCase) ||
            blocked.Contains("V67GateMissingOrNotPassed", StringComparer.OrdinalIgnoreCase) ||
            blocked.Contains("ObservationGateMissingOrNotPassed", StringComparer.OrdinalIgnoreCase) ||
            blocked.Contains("RuntimeChangeGateMissingOrNotPassed", StringComparer.OrdinalIgnoreCase))
        {
            return ShadowMergeStabilityFreezeRecommendations.BlockedByMissingGate;
        }

        if (blocked.Contains("RiskDetected", StringComparer.OrdinalIgnoreCase))
            return ShadowMergeStabilityFreezeRecommendations.BlockedByRisk;
        if (blocked.Contains("PreviewMergeNotStable", StringComparer.OrdinalIgnoreCase) ||
            blocked.Contains("PreviewDeltaNotStable", StringComparer.OrdinalIgnoreCase) ||
            blocked.Contains("InsufficientObservationWindow", StringComparer.OrdinalIgnoreCase))
        {
            return ShadowMergeStabilityFreezeRecommendations.BlockedByInstability;
        }

        if (blocked.Contains("PriorityOrSectionMismatch", StringComparer.OrdinalIgnoreCase))
            return ShadowMergeStabilityFreezeRecommendations.BlockedByPriorityOrSection;
        if (blocked.Contains("TokenDeltaBudgetExceeded", StringComparer.OrdinalIgnoreCase))
            return ShadowMergeStabilityFreezeRecommendations.BlockedByTokenBudget;
        if (blocked.Contains("AppliedDeltaDetected", StringComparer.OrdinalIgnoreCase) ||
            blocked.Contains("RuntimeOrFormalInvariantChanged", StringComparer.OrdinalIgnoreCase) ||
            blocked.Any(static reason => reason.EndsWith("AttemptDetected", StringComparison.OrdinalIgnoreCase)))
        {
            return ShadowMergeStabilityFreezeRecommendations.BlockedByRuntimeInvariant;
        }

        return ShadowMergeStabilityFreezeRecommendations.KeepPreviewOnly;
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

public sealed class ShadowMergeStabilityFreezeOptions
{
    public int MinimumObservationRunCount { get; init; } = 10;
    public int MinimumSampleObservationCount { get; init; } = 120;
    public int TokenDeltaBudget { get; init; } = 128;
    public int TokenDeltaMaxBudget { get; init; } = 32;
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

public sealed class ShadowMergeStabilityFreezeReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool FreezePassed { get; init; }
    public bool PromotionDecisionPassed { get; init; }
    public string Recommendation { get; init; } = ShadowMergeStabilityFreezeRecommendations.KeepPreviewOnly;
    public string PromotionDecision { get; init; } = ShadowMergePromotionDecisions.KeepPreviewOnly;
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";
    public string AllowedMode { get; init; } = "PreviewMergeFreezeOnly";
    public bool V66GatePassed { get; init; }
    public bool V67GatePassed { get; init; }
    public bool ObservationGatePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }
    public int ObservationRunCount { get; init; }
    public int SampleObservationCount { get; init; }
    public bool DeterministicPreviewStable { get; init; }
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

public static class ShadowMergeStabilityFreezeRecommendations
{
    public const string ReadyForShadowMergePromotionDecision = nameof(ReadyForShadowMergePromotionDecision);
    public const string ReadyForControlledMergeProposal = nameof(ReadyForControlledMergeProposal);
    public const string BlockedByMissingGate = nameof(BlockedByMissingGate);
    public const string BlockedByInstability = nameof(BlockedByInstability);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByPriorityOrSection = nameof(BlockedByPriorityOrSection);
    public const string BlockedByTokenBudget = nameof(BlockedByTokenBudget);
    public const string BlockedByRuntimeInvariant = nameof(BlockedByRuntimeInvariant);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

public static class ShadowMergePromotionDecisions
{
    public const string ReadyForControlledMergeProposal = nameof(ReadyForControlledMergeProposal);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

