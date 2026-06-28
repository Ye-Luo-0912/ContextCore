using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

public static class LearningEvidenceCalibratedSelfValidationPackStatuses
{
    public const string LearningEvidenceCalibratedSelfValidationPackReady = nameof(LearningEvidenceCalibratedSelfValidationPackReady);
    public const string LearningEvidenceCalibratedSelfValidationPackBlocked = nameof(LearningEvidenceCalibratedSelfValidationPackBlocked);
}

public static class LearningEvidenceCalibratedSelfValidationPackBlockedReasons
{
    public const string V10PilotGateMissing = nameof(V10PilotGateMissing);
    public const string V10PilotGateNotPassed = nameof(V10PilotGateNotPassed);
    public const string OfflineReplayMissing = nameof(OfflineReplayMissing);
    public const string ShadowCanaryMissing = nameof(ShadowCanaryMissing);
    public const string HardNegativeCandidatesMissing = nameof(HardNegativeCandidatesMissing);
    public const string EvidenceSufficientFalselyTrueUnderSignalLeakage = nameof(EvidenceSufficientFalselyTrueUnderSignalLeakage);
    public const string SignalLeakageRiskIgnored = nameof(SignalLeakageRiskIgnored);
    public const string HumanReviewAsGateAuthorityTrue = nameof(HumanReviewAsGateAuthorityTrue);
    public const string HumanFeedbackAutoIngestTrue = nameof(HumanFeedbackAutoIngestTrue);
    public const string AutoIngestTrue = nameof(AutoIngestTrue);
    public const string RuntimePromotionAppliedTrue = nameof(RuntimePromotionAppliedTrue);
    public const string RuntimePilotExecutionAppliedTrue = nameof(RuntimePilotExecutionAppliedTrue);
    public const string RuntimeRerankerChangedTrue = nameof(RuntimeRerankerChangedTrue);
    public const string RuntimeRouterChangedTrue = nameof(RuntimeRouterChangedTrue);
    public const string ProductionDecisionChangedTrue = nameof(ProductionDecisionChangedTrue);
    public const string PackageOutputChangedTrue = nameof(PackageOutputChangedTrue);
    public const string FormalPackageWrittenTrue = nameof(FormalPackageWrittenTrue);
    public const string GlobalDefaultOnTrue = nameof(GlobalDefaultOnTrue);
    public const string MLAuthorityTrue = nameof(MLAuthorityTrue);
    public const string LLMAuthorityTrue = nameof(LLMAuthorityTrue);
    public const string RuntimeAuthorityTrue = nameof(RuntimeAuthorityTrue);
    public const string GateAuthorityTrue = nameof(GateAuthorityTrue);
    public const string V8ScopedActivationLost = nameof(V8ScopedActivationLost);
    public const string MainlineEvidencePresent = nameof(MainlineEvidencePresent);
    public const string MainlineTrustRegistryPresent = nameof(MainlineTrustRegistryPresent);
    public const string RuntimeChangeGateNotPassed = nameof(RuntimeChangeGateNotPassed);
    public const string P15GateNotPassed = nameof(P15GateNotPassed);
}

public sealed class EvidenceSufficiencyReport
{
    public double OfflineReplayStrength { get; init; }
    public double ShadowCanaryAgreement { get; init; }
    public double FailureClusterCoverage { get; init; }
    public double HardNegativeCandidateCoverage { get; init; }
    public double RouterRiskPenalty { get; init; }
    public double SignalLeakageRiskPenalty { get; init; }
    public double RegressionSafetyScore { get; init; }
    public double RollbackSafetyScore { get; init; }
    public double EvidenceSufficiencyScore { get; init; }
    public double Threshold { get; init; } = 0.7;
    public bool EvidenceSufficient { get; init; }
    public bool SignalLeakageRisk { get; init; }
    public bool AtLeastSignalLeakageSuspected { get; init; }
    public bool HardNegativeEvidenceInsufficient { get; init; }
    public bool RouterRiskHigh { get; init; }
    public IReadOnlyList<string> SubscoreNotes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Risks { get; init; } = Array.Empty<string>();
}

public sealed class DisagreementBucket
{
    public string BucketName { get; init; } = string.Empty;
    public int Count { get; init; }
    public double Rate { get; init; }
    public IReadOnlyList<string> EvidenceSources { get; init; } = Array.Empty<string>();
}

public sealed class DisagreementAnalysis
{
    public string Mode { get; init; } = "DeterministicFeatureBasedAnalysis";
    public int TotalSimulatedQueries { get; init; }
    public int CandidateAgreeWithReferenceCount { get; init; }
    public int CandidateDisagreeWithReferenceCount { get; init; }
    public IReadOnlyList<DisagreementBucket> Buckets { get; init; } = Array.Empty<DisagreementBucket>();
    public bool AIArbitration { get; init; }
    public IReadOnlyList<string> SupportingFailureClusterIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed class HardNegativeReplayReadiness
{
    public bool HardNegativeReplayReady { get; init; }
    public bool HardNegativeCoverageSufficient { get; init; }
    public int HardNegativeCandidateCount { get; init; }
    public int HardNegativeLabeledCount { get; init; }
    public double HardNegativeCoverageRate { get; init; }
    public bool AutoIngest { get; init; }
    public bool CandidatesAreLabeled { get; init; }
    public bool ReadyForReplayDryRunOnly { get; init; } = true;
    public string ReplayMode { get; init; } = "ShadowReplayDryRun";
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed class HumanFeedbackSignalPolicy
{
    public string PolicyVersion { get; init; } = "v10.3-human-feedback-signal/v1";
    public bool HumanReviewAsGateAuthority { get; init; }
    public bool HumanFeedbackAccepted { get; init; } = true;
    public bool HumanFeedbackAutoIngest { get; init; }
    public bool HumanFeedbackRequiresEvidenceBinding { get; init; } = true;
    public bool HumanFeedbackUsedAsTrainingSignalOnly { get; init; } = true;
    public bool HumanReviewBacklogObserved { get; init; }
    public int HumanReviewBacklogQueueEntryCount { get; init; }
    public IReadOnlyList<string> AcceptedSignalTypes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RejectedAuthorityClaims { get; init; } = Array.Empty<string>();
    public string Notes { get; init; } = string.Empty;
}

public sealed class SelfValidationDecision
{
    public string DecisionId { get; init; } = string.Empty;
    public string CreatedAt { get; init; } = string.Empty;
    public bool SelfValidationPassed { get; init; }
    public bool EvidenceSufficient { get; init; }
    public bool SignalLeakageRisk { get; init; }
    public bool HardNegativeEvidenceInsufficient { get; init; }
    public bool RouterRiskHigh { get; init; }
    public bool RuntimePilotExecutionReadyForSeparateGate { get; init; }
    public IReadOnlyList<string> BlockedForRuntimePilotExecutionBy { get; init; } = Array.Empty<string>();
    public bool RuntimePromotionApplied { get; init; }
    public bool RuntimePilotExecutionApplied { get; init; }
    public bool ProductionDecisionChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public IReadOnlyList<string> EvidenceSourcesConsidered { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DecisionNotes { get; init; } = Array.Empty<string>();
    public bool AIArbitration { get; init; }
}

public sealed record LearningEvidenceCalibratedSelfValidationPackContext
{
    public bool V10PilotGatePresent { get; init; }
    public bool V10PilotGatePassed { get; init; }
    public bool OfflineReplayPresent { get; init; }
    public bool ShadowCanaryPresent { get; init; }
    public bool HardNegativeCandidatesPresent { get; init; }
    public int HardNegativeCandidateCount { get; init; }
    public int HardNegativeLabeledCount { get; init; }
    public bool V8ScopedActivationPreserved { get; init; }
    public bool RouterPromotionReady { get; init; }
    public double CandidatePairwiseAccuracy { get; init; }
    public double ReferencePairwiseAccuracy { get; init; }
    public double TreeBaselinePairwiseAccuracy { get; init; }
    public double ShadowCanaryAgreementRate { get; init; }
    public int FailureClusterCount { get; init; }
    public IReadOnlyList<string> FailureClusterIds { get; init; } = Array.Empty<string>();
    public bool KillSwitchArmed { get; init; }
    public bool RollbackReady { get; init; }
    public int HumanReviewBacklogQueueEntryCount { get; init; }
    // Synthetic test knobs
    public bool HumanReviewAsGateAuthorityOverride { get; init; }
    public bool HumanFeedbackAutoIngestOverride { get; init; }
    public bool AutoIngestOverride { get; init; }
    public bool RuntimePromotionAppliedOverride { get; init; }
    public bool RuntimePilotExecutionAppliedOverride { get; init; }
    public bool RuntimeRerankerChangedOverride { get; init; }
    public bool RuntimeRouterChangedOverride { get; init; }
    public bool ProductionDecisionChangedOverride { get; init; }
    public bool PackageOutputChangedOverride { get; init; }
    public bool FormalPackageWrittenOverride { get; init; }
    public bool GlobalDefaultOnOverride { get; init; }
    public bool MLAuthorityOverride { get; init; }
    public bool LLMAuthorityOverride { get; init; }
    public bool RuntimeAuthorityOverride { get; init; }
    public bool GateAuthorityOverride { get; init; }
    public bool EvidenceSufficientFalselyTrueOverride { get; init; }
    public bool SignalLeakageRiskIgnoredOverride { get; init; }
}

public sealed class LearningEvidenceCalibratedSelfValidationPackDecision
{
    public string Status { get; init; } = LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked;
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;
}

public static class LearningEvidenceCalibratedSelfValidationPackPolicy
{
    public static LearningEvidenceCalibratedSelfValidationPackDecision Evaluate(
        LearningEvidenceCalibratedSelfValidationPackContext ctx,
        bool rtPassed, bool p15Passed,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent)
    {
        var blocked = new List<string>();
        if (!ctx.V10PilotGatePresent) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.V10PilotGateMissing);
        else if (!ctx.V10PilotGatePassed) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.V10PilotGateNotPassed);
        if (!ctx.OfflineReplayPresent) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.OfflineReplayMissing);
        if (!ctx.ShadowCanaryPresent) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.ShadowCanaryMissing);
        if (!ctx.HardNegativeCandidatesPresent || ctx.HardNegativeCandidateCount <= 0)
            blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.HardNegativeCandidatesMissing);
        // Honesty invariants
        if (ctx.EvidenceSufficientFalselyTrueOverride) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.EvidenceSufficientFalselyTrueUnderSignalLeakage);
        if (ctx.SignalLeakageRiskIgnoredOverride) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.SignalLeakageRiskIgnored);
        // Authority demotion of human review
        if (ctx.HumanReviewAsGateAuthorityOverride) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.HumanReviewAsGateAuthorityTrue);
        if (ctx.HumanFeedbackAutoIngestOverride) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.HumanFeedbackAutoIngestTrue);
        if (ctx.AutoIngestOverride) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.AutoIngestTrue);
        if (ctx.RuntimePromotionAppliedOverride) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.RuntimePromotionAppliedTrue);
        if (ctx.RuntimePilotExecutionAppliedOverride) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.RuntimePilotExecutionAppliedTrue);
        if (ctx.RuntimeRerankerChangedOverride) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.RuntimeRerankerChangedTrue);
        if (ctx.RuntimeRouterChangedOverride) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.RuntimeRouterChangedTrue);
        if (ctx.ProductionDecisionChangedOverride) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.ProductionDecisionChangedTrue);
        if (ctx.PackageOutputChangedOverride) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.PackageOutputChangedTrue);
        if (ctx.FormalPackageWrittenOverride) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.FormalPackageWrittenTrue);
        if (ctx.GlobalDefaultOnOverride) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.GlobalDefaultOnTrue);
        if (ctx.MLAuthorityOverride) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.MLAuthorityTrue);
        if (ctx.LLMAuthorityOverride) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.LLMAuthorityTrue);
        if (ctx.RuntimeAuthorityOverride) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.RuntimeAuthorityTrue);
        if (ctx.GateAuthorityOverride) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.GateAuthorityTrue);
        if (!ctx.V8ScopedActivationPreserved) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.V8ScopedActivationLost);
        if (mainlineEvidencePresent) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.MainlineTrustRegistryPresent);
        if (!rtPassed) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.P15GateNotPassed);
        var finalBlocked = blocked.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        var ready = finalBlocked.Length == 0;
        return new LearningEvidenceCalibratedSelfValidationPackDecision
        {
            Status = ready
                ? LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackReady
                : LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked,
            BlockedReasons = finalBlocked,
            Reasoning = ready
                ? "evidence-calibrated self-validation pack policy ready — upstream + authority invariants satisfied; sub-signals + decision computed below."
                : $"{finalBlocked.Length} blocked reason(s); evidence-calibrated self-validation pack blocked."
        };
    }

    /// <summary>Compute evidence sufficiency — deterministic, multi-signal, NO single metric decides readiness.</summary>
    public static EvidenceSufficiencyReport ComputeEvidenceSufficiency(LearningEvidenceCalibratedSelfValidationPackContext ctx)
    {
        var delta = ctx.CandidatePairwiseAccuracy - ctx.ReferencePairwiseAccuracy;
        var offlineReplayStrength = Math.Max(0.0, Math.Min(1.0, 0.5 + delta));
        var canaryAgreement = Math.Max(0.0, Math.Min(1.0, ctx.ShadowCanaryAgreementRate));
        var failureClusterCoverage = ctx.FailureClusterCount > 0
            ? Math.Min(1.0, (double)ctx.HardNegativeCandidateCount / Math.Max(1, ctx.FailureClusterCount * 15))
            : 1.0;
        var hardNegativeCandidateCoverage = Math.Min(1.0, ctx.HardNegativeCandidateCount / 50.0);
        var routerRiskHigh = ctx.RouterPromotionReady;
        var routerRiskPenalty = routerRiskHigh ? 0.3 : 0.0;
        // SignalLeakageRisk: candidate ≥0.99 AND tree ≥0.99 AND weighted clearly lower → positiveScore dominance suspected
        var signalLeakageSuspected = ctx.CandidatePairwiseAccuracy >= 0.99
            && ctx.TreeBaselinePairwiseAccuracy >= 0.99
            && ctx.ReferencePairwiseAccuracy < 0.92;
        // Even a single 100% baseline is suspicious — flag as "at least suspected" with weaker penalty
        var atLeastSuspected = signalLeakageSuspected
            || ctx.CandidatePairwiseAccuracy >= 0.995
            || ctx.TreeBaselinePairwiseAccuracy >= 0.995;
        var signalLeakagePenalty = signalLeakageSuspected ? 0.4 : (atLeastSuspected ? 0.2 : 0.0);
        var regressionSafetyScore = ctx.V8ScopedActivationPreserved && !ctx.ProductionDecisionChangedOverride
            && !ctx.RuntimeRerankerChangedOverride && !ctx.RuntimeRouterChangedOverride ? 1.0 : 0.0;
        var rollbackSafetyScore = (ctx.KillSwitchArmed && ctx.RollbackReady) ? 1.0 : 0.0;
        // Hard-negative evidence insufficient: candidate specs are not labeled hard negatives.
        var hardNegativeEvidenceInsufficient = ctx.HardNegativeLabeledCount < Math.Max(20, ctx.HardNegativeCandidateCount / 4);

        var positive = 0.20 * offlineReplayStrength
                     + 0.20 * canaryAgreement
                     + 0.10 * failureClusterCoverage
                     + 0.10 * hardNegativeCandidateCoverage
                     + 0.20 * regressionSafetyScore
                     + 0.20 * rollbackSafetyScore;
        var aggregate = Math.Max(0.0, positive - routerRiskPenalty - signalLeakagePenalty);
        // No single metric decides; must clear threshold AND no leakage AND not router-promoted AND hard-neg evidence sufficient.
        var sufficient = aggregate >= 0.70
            && !signalLeakageSuspected
            && !atLeastSuspected
            && !routerRiskHigh
            && !hardNegativeEvidenceInsufficient;

        var notes = new List<string>
        {
            $"OfflineReplayStrength={offlineReplayStrength:F3} (raw delta={delta:F3})",
            $"ShadowCanaryAgreement={canaryAgreement:F3}",
            $"FailureClusterCoverage={failureClusterCoverage:F3} ({ctx.HardNegativeCandidateCount} candidate specs / {ctx.FailureClusterCount} clusters)",
            $"HardNegativeCandidateCoverage={hardNegativeCandidateCoverage:F3} ({ctx.HardNegativeCandidateCount}/50 target)",
            $"HardNegativeLabeledCount={ctx.HardNegativeLabeledCount} (specs are NOT labeled negatives; threshold 25%+ of candidate count or ≥20)",
            $"HardNegativeEvidenceInsufficient={hardNegativeEvidenceInsufficient}",
            $"RouterRiskPenalty={routerRiskPenalty:F3} (RouterRiskHigh={routerRiskHigh})",
            $"SignalLeakageRiskPenalty={signalLeakagePenalty:F3} (suspected={signalLeakageSuspected}, atLeastSuspected={atLeastSuspected})",
            $"RegressionSafetyScore={regressionSafetyScore:F3}",
            $"RollbackSafetyScore={rollbackSafetyScore:F3}",
            $"EvidenceSufficiencyScore={aggregate:F3} threshold=0.700 → sufficient={sufficient}"
        };
        var risks = new List<string>();
        if (signalLeakageSuspected)
            risks.Add("SignalLeakageRisk=true: candidate + tree both ≥0.99 while weighted only " + ctx.ReferencePairwiseAccuracy.ToString("F3", CultureInfo.InvariantCulture) + " — feature 'positiveScore' likely dominant. Pilot must verify on labeled hard-negative-expanded eval before runtime execution.");
        else if (atLeastSuspected)
            risks.Add("AtLeastSignalLeakageSuspected=true: at least one baseline ≥0.995 — flag for cross-baseline disagreement analysis before runtime execution.");
        if (hardNegativeEvidenceInsufficient) risks.Add($"HardNegativeEvidenceInsufficient=true: {ctx.HardNegativeLabeledCount} labeled vs {ctx.HardNegativeCandidateCount} candidate specs — proposals are not training evidence.");
        if (routerRiskHigh) risks.Add("RouterRiskHigh=true: router promotion remains blocked per V9.7 readiness; do not couple reranker pilot to router progress.");
        if (canaryAgreement < 0.9) risks.Add("Shadow canary agreement < 0.9 — disagreement clusters need triage.");
        if (!ctx.V8ScopedActivationPreserved) risks.Add("V8 scoped activation degraded — abort pilot consideration immediately.");

        return new EvidenceSufficiencyReport
        {
            OfflineReplayStrength = offlineReplayStrength,
            ShadowCanaryAgreement = canaryAgreement,
            FailureClusterCoverage = failureClusterCoverage,
            HardNegativeCandidateCoverage = hardNegativeCandidateCoverage,
            RouterRiskPenalty = routerRiskPenalty,
            SignalLeakageRiskPenalty = signalLeakagePenalty,
            RegressionSafetyScore = regressionSafetyScore,
            RollbackSafetyScore = rollbackSafetyScore,
            EvidenceSufficiencyScore = aggregate,
            Threshold = 0.7,
            EvidenceSufficient = sufficient,
            SignalLeakageRisk = signalLeakageSuspected,
            AtLeastSignalLeakageSuspected = atLeastSuspected,
            HardNegativeEvidenceInsufficient = hardNegativeEvidenceInsufficient,
            RouterRiskHigh = routerRiskHigh,
            SubscoreNotes = notes,
            Risks = risks
        };
    }
}

public sealed record LearningEvidenceCalibratedSelfValidationPackScenario(
    string CaseName,
    LearningEvidenceCalibratedSelfValidationPackContext Context,
    bool RtPassed, bool P15Passed,
    bool MainlineEvidencePresent, bool MainlineRegistryPresent,
    string ExpectedStatus, string? ExpectedBlockedReason);

public sealed class LearningEvidenceCalibratedSelfValidationPackRunner
{
    private static readonly JsonSerializerOptions WriteIndented = new() { WriteIndented = true };

    public LearningEvidenceCalibratedSelfValidationPackReport Run(
        LearningEvidenceCalibratedSelfValidationPackContext realContext,
        string outputDir,
        bool rtPassed, bool p15Passed,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent,
        LearningEvidenceCalibratedSelfValidationPackOptions? opt = null)
    {
        opt ??= new LearningEvidenceCalibratedSelfValidationPackOptions();
        var now = DateTimeOffset.UtcNow;

        var cases = BuildScenarios().Select(static scenario =>
        {
            var decision = LearningEvidenceCalibratedSelfValidationPackPolicy.Evaluate(
                scenario.Context, scenario.RtPassed, scenario.P15Passed,
                scenario.MainlineEvidencePresent, scenario.MainlineRegistryPresent);
            var statusMatched = string.Equals(scenario.ExpectedStatus, decision.Status, StringComparison.Ordinal);
            var blockedReasonMatched = scenario.ExpectedBlockedReason is null
                || decision.BlockedReasons.Contains(scenario.ExpectedBlockedReason, StringComparer.Ordinal);
            return new LearningEvidenceCalibratedSelfValidationPackCase
            {
                CaseName = scenario.CaseName,
                ExpectedStatus = scenario.ExpectedStatus,
                ActualStatus = decision.Status,
                ExpectedBlockedReason = scenario.ExpectedBlockedReason ?? string.Empty,
                ActualBlockedReasons = decision.BlockedReasons,
                Reasoning = decision.Reasoning,
                StatusMatched = statusMatched,
                BlockedReasonMatched = blockedReasonMatched,
                PassedAsExpected = statusMatched && blockedReasonMatched
            };
        }).ToArray();

        var blocked = new List<string>();
        if (cases.Length < 25) blocked.Add("InsufficientLearningEvidenceCalibratedSelfValidationPackCases");
        if (cases.Any(static c => !c.PassedAsExpected)) blocked.Add("LearningEvidenceCalibratedSelfValidationPackMatrixFailed");
        foreach (var status in new[] {
            LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackReady,
            LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked })
        {
            if (!cases.Any(c => string.Equals(c.ActualStatus, status, StringComparison.Ordinal)))
                blocked.Add($"StatusBranchNotCovered:{status}");
        }

        var realDecision = LearningEvidenceCalibratedSelfValidationPackPolicy.Evaluate(
            realContext, rtPassed, p15Passed, mainlineEvidencePresent, mainlineRegistryPresent);
        if (!string.Equals(realDecision.Status, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackReady, StringComparison.Ordinal))
            blocked.AddRange(realDecision.BlockedReasons.Select(static x => $"RealLearningEvidenceCalibratedSelfValidationPack:{x}"));
        if (!rtPassed) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(LearningEvidenceCalibratedSelfValidationPackBlockedReasons.MainlineTrustRegistryPresent);

        var canBuild = string.Equals(realDecision.Status, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackReady, StringComparison.Ordinal);
        EvidenceSufficiencyReport evidence = new();
        DisagreementAnalysis disagreement = new();
        HardNegativeReplayReadiness hardNegReplay = new();
        HumanFeedbackSignalPolicy feedbackPolicy = new();
        SelfValidationDecision selfDecision = new();
        var evidencePath = string.Empty;
        var disagreementPath = string.Empty;
        var hardNegReplayPath = string.Empty;
        var feedbackPolicyPath = string.Empty;
        var selfDecisionPath = string.Empty;

        if (canBuild)
        {
            evidence = LearningEvidenceCalibratedSelfValidationPackPolicy.ComputeEvidenceSufficiency(realContext);
            evidencePath = Path.Combine(outputDir, "evidence-sufficiency-report.json");
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(evidence, WriteIndented), new UTF8Encoding(true));

            disagreement = BuildDisagreementAnalysis(realContext, evidence);
            disagreementPath = Path.Combine(outputDir, "disagreement-analysis.json");
            File.WriteAllText(disagreementPath, JsonSerializer.Serialize(disagreement, WriteIndented), new UTF8Encoding(true));

            hardNegReplay = BuildHardNegativeReplayReadiness(realContext, evidence);
            hardNegReplayPath = Path.Combine(outputDir, "hard-negative-replay-readiness.json");
            File.WriteAllText(hardNegReplayPath, JsonSerializer.Serialize(hardNegReplay, WriteIndented), new UTF8Encoding(true));

            feedbackPolicy = BuildHumanFeedbackSignalPolicy(realContext);
            feedbackPolicyPath = Path.Combine(outputDir, "human-feedback-signal-policy.json");
            File.WriteAllText(feedbackPolicyPath, JsonSerializer.Serialize(feedbackPolicy, WriteIndented), new UTF8Encoding(true));

            selfDecision = BuildSelfValidationDecision(realContext, evidence, hardNegReplay, now);
            selfDecisionPath = Path.Combine(outputDir, "self-validation-decision.json");
            File.WriteAllText(selfDecisionPath, JsonSerializer.Serialize(selfDecision, WriteIndented), new UTF8Encoding(true));
        }

        // Authority leak checks on produced artifacts
        if (disagreement.AIArbitration) blocked.Add("DisagreementAnalysisAIArbitrationLeak");
        if (hardNegReplay.AutoIngest) blocked.Add("HardNegReplayAutoIngestLeak");
        if (feedbackPolicy.HumanReviewAsGateAuthority || feedbackPolicy.HumanFeedbackAutoIngest) blocked.Add("FeedbackPolicyAuthorityLeak");
        if (!feedbackPolicy.HumanFeedbackUsedAsTrainingSignalOnly || !feedbackPolicy.HumanFeedbackRequiresEvidenceBinding) blocked.Add("FeedbackPolicyGuardrailIncomplete");
        if (selfDecision.AIArbitration || selfDecision.RuntimePromotionApplied || selfDecision.RuntimePilotExecutionApplied
            || selfDecision.ProductionDecisionChanged || selfDecision.PackageOutputChanged || selfDecision.GlobalDefaultOn)
            blocked.Add("SelfValidationDecisionRuntimeLeak");

        var finalBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var passed = finalBlocked.Length == 0;
        return new LearningEvidenceCalibratedSelfValidationPackReport
        {
            OperationId = $"v10-learning-evidence-calibrated-self-validation-pack-{Guid.NewGuid():N}",
            CreatedAt = now,
            EvidenceCalibratedSelfValidationPackPassed = passed,
            GatePassed = opt.IsGate && passed,
            TotalCases = cases.Length,
            PassedCases = cases.Count(static c => c.PassedAsExpected),
            FailedCases = cases.Count(static c => !c.PassedAsExpected),
            ReadyCases = cases.Count(c => string.Equals(c.ActualStatus, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackReady, StringComparison.Ordinal)),
            BlockedCases = cases.Count(c => string.Equals(c.ActualStatus, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, StringComparison.Ordinal)),
            Cases = cases,
            EvidenceSufficiencyReport = evidence,
            DisagreementAnalysis = disagreement,
            HardNegativeReplayReadiness = hardNegReplay,
            HumanFeedbackSignalPolicy = feedbackPolicy,
            SelfValidationDecision = selfDecision,
            EvidenceSufficiencyReportReady = canBuild,
            DisagreementAnalysisReady = canBuild,
            HardNegativeReplayReadinessReady = canBuild,
            HumanFeedbackSignalPolicyReady = canBuild,
            SelfValidationDecisionReady = canBuild,
            EvidenceSufficient = evidence.EvidenceSufficient,
            SignalLeakageRisk = evidence.SignalLeakageRisk,
            AtLeastSignalLeakageSuspected = evidence.AtLeastSignalLeakageSuspected,
            HardNegativeEvidenceInsufficient = evidence.HardNegativeEvidenceInsufficient,
            RouterRiskHigh = evidence.RouterRiskHigh,
            RuntimePilotExecutionReadyForSeparateGate = selfDecision.RuntimePilotExecutionReadyForSeparateGate,
            BlockedForRuntimePilotExecutionBy = selfDecision.BlockedForRuntimePilotExecutionBy,
            HumanReviewRequired = false,
            HumanReviewAsGateAuthority = false,
            HumanFeedbackAccepted = true,
            HumanFeedbackUsedAsTrainingSignalOnly = true,
            HumanFeedbackAutoIngest = false,
            RuntimePromotionApplied = false,
            RuntimePilotExecutionApplied = false,
            RuntimeRerankerChanged = false,
            RuntimeRouterChanged = false,
            ProductionDecisionChanged = false,
            PackageOutputChanged = false,
            FormalPackageWritten = false,
            GlobalDefaultOn = false,
            MLAuthority = false,
            LLMAuthority = false,
            RuntimeAuthority = false,
            GateAuthority = false,
            AutoIngest = false,
            RequiresSeparatePromotionGate = true,
            V8ScopedActivationPreserved = realContext.V8ScopedActivationPreserved,
            UpstreamV10PilotGatePresent = realContext.V10PilotGatePresent,
            UpstreamV10PilotGatePassed = realContext.V10PilotGatePassed,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent,
            EvidenceSufficiencyReportPath = evidencePath,
            DisagreementAnalysisPath = disagreementPath,
            HardNegativeReplayReadinessPath = hardNegReplayPath,
            HumanFeedbackSignalPolicyPath = feedbackPolicyPath,
            SelfValidationDecisionPath = selfDecisionPath,
            Recommendation = passed
                ? (selfDecision.RuntimePilotExecutionReadyForSeparateGate ? "ProceedToV10.7PilotExecutionGate" : "BlockedForRuntimePilotExecution-EvidenceCalibrated-AccumulateMoreEvidence")
                : "Blocked",
            NextAllowedPhase = passed
                ? (selfDecision.RuntimePilotExecutionReadyForSeparateGate ? "V10.7PilotExecutionGate" : "V10.7PilotExecution-pending-evidence")
                : string.Empty,
            BlockedReasons = finalBlocked,
            Diagnostics = new[]
            {
                $"total={cases.Length}",
                $"realStatus={realDecision.Status}",
                $"evidenceSufficiencyScore={evidence.EvidenceSufficiencyScore:F3}",
                $"evidenceSufficient={evidence.EvidenceSufficient}",
                $"signalLeakageRisk={evidence.SignalLeakageRisk}",
                $"atLeastSignalLeakageSuspected={evidence.AtLeastSignalLeakageSuspected}",
                $"hardNegativeEvidenceInsufficient={evidence.HardNegativeEvidenceInsufficient}",
                $"routerRiskHigh={evidence.RouterRiskHigh}",
                $"runtimePilotExecReadyForSeparateGate={selfDecision.RuntimePilotExecutionReadyForSeparateGate}",
                $"blockedExecBy={string.Join('|', selfDecision.BlockedForRuntimePilotExecutionBy)}"
            }
        };
    }

    private static IReadOnlyList<LearningEvidenceCalibratedSelfValidationPackScenario> BuildScenarios()
    {
        var clean = BuildCleanContext();
        return [
            new("AllUpstreamClean", clean, true, true, false, false, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackReady, null),
            new("V10PilotGateMissing", clean with { V10PilotGatePresent = false }, true, true, false, false, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, LearningEvidenceCalibratedSelfValidationPackBlockedReasons.V10PilotGateMissing),
            new("V10PilotGateNotPassed", clean with { V10PilotGatePassed = false }, true, true, false, false, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, LearningEvidenceCalibratedSelfValidationPackBlockedReasons.V10PilotGateNotPassed),
            new("OfflineReplayMissing", clean with { OfflineReplayPresent = false }, true, true, false, false, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, LearningEvidenceCalibratedSelfValidationPackBlockedReasons.OfflineReplayMissing),
            new("ShadowCanaryMissing", clean with { ShadowCanaryPresent = false }, true, true, false, false, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, LearningEvidenceCalibratedSelfValidationPackBlockedReasons.ShadowCanaryMissing),
            new("HardNegativeCandidatesMissing", clean with { HardNegativeCandidatesPresent = false, HardNegativeCandidateCount = 0 }, true, true, false, false, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, LearningEvidenceCalibratedSelfValidationPackBlockedReasons.HardNegativeCandidatesMissing),
            new("EvidenceSufficientFalselyTrue", clean with { EvidenceSufficientFalselyTrueOverride = true }, true, true, false, false, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, LearningEvidenceCalibratedSelfValidationPackBlockedReasons.EvidenceSufficientFalselyTrueUnderSignalLeakage),
            new("SignalLeakageRiskIgnored", clean with { SignalLeakageRiskIgnoredOverride = true }, true, true, false, false, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, LearningEvidenceCalibratedSelfValidationPackBlockedReasons.SignalLeakageRiskIgnored),
            new("HumanReviewAsGateAuthorityTrue", clean with { HumanReviewAsGateAuthorityOverride = true }, true, true, false, false, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, LearningEvidenceCalibratedSelfValidationPackBlockedReasons.HumanReviewAsGateAuthorityTrue),
            new("HumanFeedbackAutoIngestTrue", clean with { HumanFeedbackAutoIngestOverride = true }, true, true, false, false, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, LearningEvidenceCalibratedSelfValidationPackBlockedReasons.HumanFeedbackAutoIngestTrue),
            new("AutoIngestTrue", clean with { AutoIngestOverride = true }, true, true, false, false, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, LearningEvidenceCalibratedSelfValidationPackBlockedReasons.AutoIngestTrue),
            new("RuntimePromotionAppliedTrue", clean with { RuntimePromotionAppliedOverride = true }, true, true, false, false, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, LearningEvidenceCalibratedSelfValidationPackBlockedReasons.RuntimePromotionAppliedTrue),
            new("RuntimePilotExecutionAppliedTrue", clean with { RuntimePilotExecutionAppliedOverride = true }, true, true, false, false, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, LearningEvidenceCalibratedSelfValidationPackBlockedReasons.RuntimePilotExecutionAppliedTrue),
            new("RuntimeRerankerChangedTrue", clean with { RuntimeRerankerChangedOverride = true }, true, true, false, false, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, LearningEvidenceCalibratedSelfValidationPackBlockedReasons.RuntimeRerankerChangedTrue),
            new("RuntimeRouterChangedTrue", clean with { RuntimeRouterChangedOverride = true }, true, true, false, false, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, LearningEvidenceCalibratedSelfValidationPackBlockedReasons.RuntimeRouterChangedTrue),
            new("ProductionDecisionChangedTrue", clean with { ProductionDecisionChangedOverride = true }, true, true, false, false, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, LearningEvidenceCalibratedSelfValidationPackBlockedReasons.ProductionDecisionChangedTrue),
            new("PackageOutputChangedTrue", clean with { PackageOutputChangedOverride = true }, true, true, false, false, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, LearningEvidenceCalibratedSelfValidationPackBlockedReasons.PackageOutputChangedTrue),
            new("FormalPackageWrittenTrue", clean with { FormalPackageWrittenOverride = true }, true, true, false, false, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, LearningEvidenceCalibratedSelfValidationPackBlockedReasons.FormalPackageWrittenTrue),
            new("GlobalDefaultOnTrue", clean with { GlobalDefaultOnOverride = true }, true, true, false, false, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, LearningEvidenceCalibratedSelfValidationPackBlockedReasons.GlobalDefaultOnTrue),
            new("MLAuthorityTrue", clean with { MLAuthorityOverride = true }, true, true, false, false, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, LearningEvidenceCalibratedSelfValidationPackBlockedReasons.MLAuthorityTrue),
            new("LLMAuthorityTrue", clean with { LLMAuthorityOverride = true }, true, true, false, false, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, LearningEvidenceCalibratedSelfValidationPackBlockedReasons.LLMAuthorityTrue),
            new("RuntimeAuthorityTrue", clean with { RuntimeAuthorityOverride = true }, true, true, false, false, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, LearningEvidenceCalibratedSelfValidationPackBlockedReasons.RuntimeAuthorityTrue),
            new("GateAuthorityTrue", clean with { GateAuthorityOverride = true }, true, true, false, false, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, LearningEvidenceCalibratedSelfValidationPackBlockedReasons.GateAuthorityTrue),
            new("V8ScopedActivationLost", clean with { V8ScopedActivationPreserved = false }, true, true, false, false, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, LearningEvidenceCalibratedSelfValidationPackBlockedReasons.V8ScopedActivationLost),
            new("MainlineEvidencePresent", clean, true, true, true, false, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, LearningEvidenceCalibratedSelfValidationPackBlockedReasons.MainlineEvidencePresent),
            new("MainlineTrustRegistryPresent", clean, true, true, false, true, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, LearningEvidenceCalibratedSelfValidationPackBlockedReasons.MainlineTrustRegistryPresent),
            new("RuntimeGateNotPassed", clean, false, true, false, false, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, LearningEvidenceCalibratedSelfValidationPackBlockedReasons.RuntimeChangeGateNotPassed),
            new("P15GateNotPassed", clean, true, false, false, false, LearningEvidenceCalibratedSelfValidationPackStatuses.LearningEvidenceCalibratedSelfValidationPackBlocked, LearningEvidenceCalibratedSelfValidationPackBlockedReasons.P15GateNotPassed)
        ];
    }

    private static LearningEvidenceCalibratedSelfValidationPackContext BuildCleanContext() => new()
    {
        V10PilotGatePresent = true,
        V10PilotGatePassed = true,
        OfflineReplayPresent = true,
        ShadowCanaryPresent = true,
        HardNegativeCandidatesPresent = true,
        HardNegativeCandidateCount = 60,
        HardNegativeLabeledCount = 0,
        V8ScopedActivationPreserved = true,
        RouterPromotionReady = false,
        CandidatePairwiseAccuracy = 1.0,
        ReferencePairwiseAccuracy = 0.862,
        TreeBaselinePairwiseAccuracy = 1.0,
        ShadowCanaryAgreementRate = 0.931,
        FailureClusterCount = 3,
        KillSwitchArmed = true,
        RollbackReady = true,
        HumanReviewBacklogQueueEntryCount = 12
    };

    private static DisagreementAnalysis BuildDisagreementAnalysis(
        LearningEvidenceCalibratedSelfValidationPackContext ctx, EvidenceSufficiencyReport evidence)
    {
        var total = Math.Max(50, ctx.HardNegativeCandidateCount);
        var agreeCount = (int)Math.Round(total * ctx.ShadowCanaryAgreementRate);
        var disagreeCount = total - agreeCount;
        // Signal-leakage-aware bucketing: when leakage suspected, candidate-better evidence is doubted → shift more to high-risk / missing-evidence.
        double candidateBetterRatio = evidence.SignalLeakageRisk ? 0.15 : 0.50;
        double referenceBetterRatio = evidence.SignalLeakageRisk ? 0.15 : 0.20;
        double uncertainRatio = 0.20;
        double missingRatio = evidence.HardNegativeEvidenceInsufficient ? 0.30 : 0.10;
        double highRiskRatio = Math.Max(0.0, 1.0 - candidateBetterRatio - referenceBetterRatio - uncertainRatio - missingRatio);
        var candidateBetter = (int)Math.Round(disagreeCount * candidateBetterRatio);
        var referenceBetter = (int)Math.Round(disagreeCount * referenceBetterRatio);
        var uncertain = (int)Math.Round(disagreeCount * uncertainRatio);
        var missing = (int)Math.Round(disagreeCount * missingRatio);
        var highRisk = Math.Max(0, disagreeCount - candidateBetter - referenceBetter - uncertain - missing);
        var buckets = new[]
        {
            new DisagreementBucket { BucketName = "candidate-better-evidence", Count = candidateBetter, Rate = total > 0 ? (double)candidateBetter / total : 0, EvidenceSources = new[] { "offline-replay-summary AccuracyDelta", "deterministic feature deltas" } },
            new DisagreementBucket { BucketName = "reference-better-evidence", Count = referenceBetter, Rate = total > 0 ? (double)referenceBetter / total : 0, EvidenceSources = new[] { "WeightedBaseline TopFailures cluster" } },
            new DisagreementBucket { BucketName = "uncertain", Count = uncertain, Rate = total > 0 ? (double)uncertain / total : 0, EvidenceSources = new[] { "score margin below confidence threshold", "feature parity tie" } },
            new DisagreementBucket { BucketName = "missing-evidence", Count = missing, Rate = total > 0 ? (double)missing / total : 0, EvidenceSources = new[] { "no labeled hard-negative paired with this sample", "no failure cluster covers this query mode" } },
            new DisagreementBucket { BucketName = "high-risk", Count = highRisk, Rate = total > 0 ? (double)highRisk / total : 0, EvidenceSources = new[] { "signal-leakage suspected — candidate-better outcomes downgraded", "router promotion blocked" } }
        };
        return new DisagreementAnalysis
        {
            Mode = "DeterministicFeatureBasedAnalysis",
            TotalSimulatedQueries = total,
            CandidateAgreeWithReferenceCount = agreeCount,
            CandidateDisagreeWithReferenceCount = disagreeCount,
            Buckets = buckets,
            AIArbitration = false,
            SupportingFailureClusterIds = ctx.FailureClusterIds,
            Notes = new[]
            {
                "Bucketing is deterministic: derived from canary agreement, signal-leakage suspicion, hard-negative coverage.",
                "AI arbitration is disabled — humans inspect 'missing-evidence', 'uncertain', 'high-risk' buckets manually.",
                "RuntimeDecision unaffected by this analysis; descriptive only."
            }
        };
    }

    private static HardNegativeReplayReadiness BuildHardNegativeReplayReadiness(
        LearningEvidenceCalibratedSelfValidationPackContext ctx, EvidenceSufficiencyReport evidence)
    {
        var coverage = ctx.FailureClusterCount > 0
            ? Math.Min(1.0, (double)ctx.HardNegativeCandidateCount / Math.Max(1, ctx.FailureClusterCount * 15))
            : 0;
        var coverageSufficient = coverage >= 0.5 && !evidence.HardNegativeEvidenceInsufficient;
        // Replay-ready means we can run dry-run replay; doesn't mean evidence is sufficient.
        var replayReady = ctx.HardNegativeCandidateCount > 0;
        var candidatesAreLabeled = ctx.HardNegativeLabeledCount >= Math.Max(20, ctx.HardNegativeCandidateCount / 4);
        var notes = new List<string>
        {
            $"HardNegativeCandidateCount={ctx.HardNegativeCandidateCount}",
            $"HardNegativeLabeledCount={ctx.HardNegativeLabeledCount}",
            $"HardNegativeCoverageRate={coverage:F3} (target ≥0.5)",
            $"CandidatesAreLabeled={candidatesAreLabeled} (candidate specs ARE NOT labeled hard negatives until human review approves them)",
            "Replay is dry-run only — hard-negatives never enter the formal training set automatically.",
            "Each replay run produces shadow scores; runtime decisions remain on the rule-based reranker."
        };
        if (!candidatesAreLabeled)
            notes.Add($"NOT-READY-AS-EVIDENCE: only {ctx.HardNegativeLabeledCount} labeled vs {ctx.HardNegativeCandidateCount} candidate specs. Replay can run but evidence sufficiency is not satisfied by spec proposals alone.");
        return new HardNegativeReplayReadiness
        {
            HardNegativeReplayReady = replayReady,
            HardNegativeCoverageSufficient = coverageSufficient,
            HardNegativeCandidateCount = ctx.HardNegativeCandidateCount,
            HardNegativeLabeledCount = ctx.HardNegativeLabeledCount,
            HardNegativeCoverageRate = coverage,
            AutoIngest = false,
            CandidatesAreLabeled = candidatesAreLabeled,
            ReadyForReplayDryRunOnly = true,
            ReplayMode = "ShadowReplayDryRun",
            Notes = notes
        };
    }

    private static HumanFeedbackSignalPolicy BuildHumanFeedbackSignalPolicy(LearningEvidenceCalibratedSelfValidationPackContext ctx)
        => new()
        {
            PolicyVersion = "v10.3-human-feedback-signal/v1",
            HumanReviewAsGateAuthority = false,
            HumanFeedbackAccepted = true,
            HumanFeedbackAutoIngest = false,
            HumanFeedbackRequiresEvidenceBinding = true,
            HumanFeedbackUsedAsTrainingSignalOnly = true,
            HumanReviewBacklogObserved = ctx.HumanReviewBacklogQueueEntryCount > 0,
            HumanReviewBacklogQueueEntryCount = ctx.HumanReviewBacklogQueueEntryCount,
            AcceptedSignalTypes = new[]
            {
                "labeled hard-negative confirmation (per V9.4 candidate spec)",
                "router intent ground-truth annotation (per V9.4 repair plan)",
                "failure cluster root-cause categorization (per V9.4 diagnosis pack)",
                "package coverage / constraint gap labeling (per V9.5 feedback ingestion contract)"
            },
            RejectedAuthorityClaims = new[]
            {
                "human review CANNOT mark RuntimePilotExecutionReadyForSeparateGate=true on its own",
                "human review CANNOT bypass signal-leakage suspicion / hard-negative evidence requirement",
                "human review CANNOT auto-ingest feedback into training set without evidence-binding verification",
                "human review CANNOT grant runtime / gate / ML / LLM authority"
            },
            Notes = "Human review is now a feedback SIGNAL feeding the dataset, not a GATE deciding readiness. Evidence calibration alone gates runtime pilot execution. Backlog observed only — not consumed here."
        };

    private static SelfValidationDecision BuildSelfValidationDecision(
        LearningEvidenceCalibratedSelfValidationPackContext ctx,
        EvidenceSufficiencyReport evidence,
        HardNegativeReplayReadiness hardNegReplay,
        DateTimeOffset now)
    {
        var blockedBy = new List<string>();
        if (!evidence.EvidenceSufficient) blockedBy.Add("EvidenceInsufficient");
        if (evidence.SignalLeakageRisk) blockedBy.Add("SignalLeakageRisk");
        else if (evidence.AtLeastSignalLeakageSuspected) blockedBy.Add("AtLeastSignalLeakageSuspected");
        if (evidence.HardNegativeEvidenceInsufficient) blockedBy.Add("HardNegativeEvidenceInsufficient");
        if (evidence.RouterRiskHigh) blockedBy.Add("RouterRiskHigh");
        if (!ctx.V8ScopedActivationPreserved) blockedBy.Add("V8ScopedActivationLost");
        var pilotReady = blockedBy.Count == 0;
        var notes = new List<string>
        {
            $"EvidenceSufficiencyScore={evidence.EvidenceSufficiencyScore:F3} threshold={evidence.Threshold:F3}",
            $"SignalLeakageRisk={evidence.SignalLeakageRisk} (AtLeastSuspected={evidence.AtLeastSignalLeakageSuspected})",
            $"HardNegativeEvidenceInsufficient={evidence.HardNegativeEvidenceInsufficient} (labeled={ctx.HardNegativeLabeledCount} / candidates={ctx.HardNegativeCandidateCount})",
            $"RouterRiskHigh={evidence.RouterRiskHigh}",
            "Decision is evidence-calibrated and deterministic; human approval is NOT required to render this verdict.",
            pilotReady
                ? "PilotReady=true: evidence threshold met, no leakage, hard-neg labeled, router blocked, V8 preserved."
                : $"PilotReady=false: blocked by [{string.Join(", ", blockedBy)}]; accumulate more evidence before retrying."
        };
        return new SelfValidationDecision
        {
            DecisionId = $"v10-self-validation-decision-{Guid.NewGuid():N}",
            CreatedAt = now.ToString("O"),
            SelfValidationPassed = true,  // the validation completed; the pilot-execution decision is encoded separately
            EvidenceSufficient = evidence.EvidenceSufficient,
            SignalLeakageRisk = evidence.SignalLeakageRisk,
            HardNegativeEvidenceInsufficient = evidence.HardNegativeEvidenceInsufficient,
            RouterRiskHigh = evidence.RouterRiskHigh,
            RuntimePilotExecutionReadyForSeparateGate = pilotReady,
            BlockedForRuntimePilotExecutionBy = blockedBy,
            RuntimePromotionApplied = false,
            RuntimePilotExecutionApplied = false,
            ProductionDecisionChanged = false,
            PackageOutputChanged = false,
            GlobalDefaultOn = false,
            EvidenceSourcesConsidered = new[]
            {
                "learning/v10/offline-replay-summary.json",
                "learning/v10/shadow-canary-simulation.json",
                "learning/v10/pilot-audit-manifest.json",
                "learning/v9/shadow-comparison-summary.json",
                "learning/v9/failure-diagnosis-input-pack.json",
                "learning/v9/hard-negative-expansion-candidates.jsonl",
                "learning/v9/router-intent-repair-plan.json"
            },
            DecisionNotes = notes,
            AIArbitration = false
        };
    }

    public static string BuildMarkdown(string title, LearningEvidenceCalibratedSelfValidationPackReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"- EvidenceCalibratedSelfValidationPackPassed: `{report.EvidenceCalibratedSelfValidationPackPassed}`");
        sb.AppendLine($"- GatePassed: `{report.GatePassed}`");
        sb.AppendLine($"- TotalCases: `{report.TotalCases}` PassedCases: `{report.PassedCases}` FailedCases: `{report.FailedCases}`");
        sb.AppendLine();
        sb.AppendLine("## Evidence Sufficiency");
        sb.AppendLine($"- EvidenceSufficiencyScore: `{report.EvidenceSufficiencyReport.EvidenceSufficiencyScore:F3}` Threshold: `{report.EvidenceSufficiencyReport.Threshold:F3}`");
        sb.AppendLine($"- EvidenceSufficient: `{report.EvidenceSufficient}` SignalLeakageRisk: `{report.SignalLeakageRisk}` AtLeastSignalLeakageSuspected: `{report.AtLeastSignalLeakageSuspected}`");
        sb.AppendLine($"- HardNegativeEvidenceInsufficient: `{report.HardNegativeEvidenceInsufficient}` RouterRiskHigh: `{report.RouterRiskHigh}`");
        sb.AppendLine($"- OfflineReplayStrength: `{report.EvidenceSufficiencyReport.OfflineReplayStrength:F3}`  ShadowCanaryAgreement: `{report.EvidenceSufficiencyReport.ShadowCanaryAgreement:F3}`");
        sb.AppendLine($"- FailureClusterCoverage: `{report.EvidenceSufficiencyReport.FailureClusterCoverage:F3}`  HardNegativeCandidateCoverage: `{report.EvidenceSufficiencyReport.HardNegativeCandidateCoverage:F3}`");
        sb.AppendLine($"- RegressionSafetyScore: `{report.EvidenceSufficiencyReport.RegressionSafetyScore:F3}`  RollbackSafetyScore: `{report.EvidenceSufficiencyReport.RollbackSafetyScore:F3}`");
        sb.AppendLine($"- RouterRiskPenalty: `{report.EvidenceSufficiencyReport.RouterRiskPenalty:F3}`  SignalLeakageRiskPenalty: `{report.EvidenceSufficiencyReport.SignalLeakageRiskPenalty:F3}`");
        sb.AppendLine();
        sb.AppendLine("## Disagreement Analysis");
        sb.AppendLine($"- TotalSimulatedQueries: `{report.DisagreementAnalysis.TotalSimulatedQueries}` Agree: `{report.DisagreementAnalysis.CandidateAgreeWithReferenceCount}` Disagree: `{report.DisagreementAnalysis.CandidateDisagreeWithReferenceCount}`");
        sb.AppendLine($"- AIArbitration: `{report.DisagreementAnalysis.AIArbitration}` Mode: `{report.DisagreementAnalysis.Mode}`");
        foreach (var b in report.DisagreementAnalysis.Buckets)
            sb.AppendLine($"  - {b.BucketName}: count={b.Count} rate={b.Rate:F3}");
        sb.AppendLine();
        sb.AppendLine("## Hard Negative Replay Readiness");
        sb.AppendLine($"- HardNegativeReplayReady: `{report.HardNegativeReplayReadiness.HardNegativeReplayReady}`  HardNegativeCoverageSufficient: `{report.HardNegativeReplayReadiness.HardNegativeCoverageSufficient}`");
        sb.AppendLine($"- CandidateCount: `{report.HardNegativeReplayReadiness.HardNegativeCandidateCount}` LabeledCount: `{report.HardNegativeReplayReadiness.HardNegativeLabeledCount}` CoverageRate: `{report.HardNegativeReplayReadiness.HardNegativeCoverageRate:F3}`");
        sb.AppendLine($"- CandidatesAreLabeled: `{report.HardNegativeReplayReadiness.CandidatesAreLabeled}` AutoIngest: `{report.HardNegativeReplayReadiness.AutoIngest}`");
        sb.AppendLine();
        sb.AppendLine("## Human Feedback Signal Policy");
        sb.AppendLine($"- HumanReviewAsGateAuthority: `{report.HumanFeedbackSignalPolicy.HumanReviewAsGateAuthority}` HumanFeedbackAccepted: `{report.HumanFeedbackSignalPolicy.HumanFeedbackAccepted}`");
        sb.AppendLine($"- HumanFeedbackAutoIngest: `{report.HumanFeedbackSignalPolicy.HumanFeedbackAutoIngest}` HumanFeedbackRequiresEvidenceBinding: `{report.HumanFeedbackSignalPolicy.HumanFeedbackRequiresEvidenceBinding}`");
        sb.AppendLine($"- HumanFeedbackUsedAsTrainingSignalOnly: `{report.HumanFeedbackSignalPolicy.HumanFeedbackUsedAsTrainingSignalOnly}` HumanReviewBacklogObserved: `{report.HumanFeedbackSignalPolicy.HumanReviewBacklogObserved}` ({report.HumanFeedbackSignalPolicy.HumanReviewBacklogQueueEntryCount} entries)");
        sb.AppendLine();
        sb.AppendLine("## Self-Validation Decision");
        sb.AppendLine($"- SelfValidationPassed: `{report.SelfValidationDecision.SelfValidationPassed}`  AIArbitration: `{report.SelfValidationDecision.AIArbitration}`");
        sb.AppendLine($"- RuntimePilotExecutionReadyForSeparateGate: `{report.SelfValidationDecision.RuntimePilotExecutionReadyForSeparateGate}`");
        if (report.SelfValidationDecision.BlockedForRuntimePilotExecutionBy.Count > 0)
            sb.AppendLine($"- BlockedForRuntimePilotExecutionBy: `{string.Join(", ", report.SelfValidationDecision.BlockedForRuntimePilotExecutionBy)}`");
        sb.AppendLine();
        sb.AppendLine("## Authority Invariants");
        sb.AppendLine($"- MLAuthority: `{report.MLAuthority}` LLMAuthority: `{report.LLMAuthority}` RuntimeAuthority: `{report.RuntimeAuthority}` GateAuthority: `{report.GateAuthority}`");
        sb.AppendLine($"- RuntimePromotionApplied: `{report.RuntimePromotionApplied}` RuntimePilotExecutionApplied: `{report.RuntimePilotExecutionApplied}`");
        sb.AppendLine($"- RuntimeRerankerChanged: `{report.RuntimeRerankerChanged}` RuntimeRouterChanged: `{report.RuntimeRouterChanged}` ProductionDecisionChanged: `{report.ProductionDecisionChanged}`");
        sb.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}` FormalPackageWritten: `{report.FormalPackageWritten}` GlobalDefaultOn: `{report.GlobalDefaultOn}`");
        sb.AppendLine($"- HumanReviewRequired: `{report.HumanReviewRequired}` HumanReviewAsGateAuthority: `{report.HumanReviewAsGateAuthority}` AutoIngest: `{report.AutoIngest}`");
        sb.AppendLine($"- V8ScopedActivationPreserved: `{report.V8ScopedActivationPreserved}`");
        sb.AppendLine();
        sb.AppendLine($"- Recommendation: `{report.Recommendation}` NextAllowedPhase: `{report.NextAllowedPhase}`");
        if (report.BlockedReasons.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Blocked Reasons");
            foreach (var r in report.BlockedReasons) sb.AppendLine($"- `{r}`");
        }
        return sb.ToString();
    }
}

public sealed class LearningEvidenceCalibratedSelfValidationPackCase
{
    public string CaseName { get; init; } = string.Empty;
    public string ExpectedStatus { get; init; } = string.Empty;
    public string ActualStatus { get; init; } = string.Empty;
    public string ExpectedBlockedReason { get; init; } = string.Empty;
    public IReadOnlyList<string> ActualBlockedReasons { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;
    public bool StatusMatched { get; init; }
    public bool BlockedReasonMatched { get; init; }
    public bool PassedAsExpected { get; init; }
}

public sealed class LearningEvidenceCalibratedSelfValidationPackReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool EvidenceCalibratedSelfValidationPackPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public int ReadyCases { get; init; }
    public int BlockedCases { get; init; }
    public IReadOnlyList<LearningEvidenceCalibratedSelfValidationPackCase> Cases { get; init; } = Array.Empty<LearningEvidenceCalibratedSelfValidationPackCase>();
    public EvidenceSufficiencyReport EvidenceSufficiencyReport { get; init; } = new();
    public DisagreementAnalysis DisagreementAnalysis { get; init; } = new();
    public HardNegativeReplayReadiness HardNegativeReplayReadiness { get; init; } = new();
    public HumanFeedbackSignalPolicy HumanFeedbackSignalPolicy { get; init; } = new();
    public SelfValidationDecision SelfValidationDecision { get; init; } = new();
    public bool EvidenceSufficiencyReportReady { get; init; }
    public bool DisagreementAnalysisReady { get; init; }
    public bool HardNegativeReplayReadinessReady { get; init; }
    public bool HumanFeedbackSignalPolicyReady { get; init; }
    public bool SelfValidationDecisionReady { get; init; }
    public bool EvidenceSufficient { get; init; }
    public bool SignalLeakageRisk { get; init; }
    public bool AtLeastSignalLeakageSuspected { get; init; }
    public bool HardNegativeEvidenceInsufficient { get; init; }
    public bool RouterRiskHigh { get; init; }
    public bool RuntimePilotExecutionReadyForSeparateGate { get; init; }
    public IReadOnlyList<string> BlockedForRuntimePilotExecutionBy { get; init; } = Array.Empty<string>();
    public bool HumanReviewRequired { get; init; }
    public bool HumanReviewAsGateAuthority { get; init; }
    public bool HumanFeedbackAccepted { get; init; }
    public bool HumanFeedbackUsedAsTrainingSignalOnly { get; init; }
    public bool HumanFeedbackAutoIngest { get; init; }
    public bool RuntimePromotionApplied { get; init; }
    public bool RuntimePilotExecutionApplied { get; init; }
    public bool RuntimeRerankerChanged { get; init; }
    public bool RuntimeRouterChanged { get; init; }
    public bool ProductionDecisionChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool MLAuthority { get; init; }
    public bool LLMAuthority { get; init; }
    public bool RuntimeAuthority { get; init; }
    public bool GateAuthority { get; init; }
    public bool AutoIngest { get; init; }
    public bool RequiresSeparatePromotionGate { get; init; }
    public bool V8ScopedActivationPreserved { get; init; }
    public bool UpstreamV10PilotGatePresent { get; init; }
    public bool UpstreamV10PilotGatePassed { get; init; }
    public bool MainlineEvidencePresent { get; init; }
    public bool MainlineTrustRegistryPresent { get; init; }
    public string EvidenceSufficiencyReportPath { get; init; } = string.Empty;
    public string DisagreementAnalysisPath { get; init; } = string.Empty;
    public string HardNegativeReplayReadinessPath { get; init; } = string.Empty;
    public string HumanFeedbackSignalPolicyPath { get; init; } = string.Empty;
    public string SelfValidationDecisionPath { get; init; } = string.Empty;
    public string Recommendation { get; init; } = string.Empty;
    public string NextAllowedPhase { get; init; } = string.Empty;
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class LearningEvidenceCalibratedSelfValidationPackOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
