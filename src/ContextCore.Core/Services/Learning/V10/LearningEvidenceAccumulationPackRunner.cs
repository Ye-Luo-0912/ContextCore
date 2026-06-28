using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

public static class LearningEvidenceAccumulationPackStatuses
{
    public const string LearningEvidenceAccumulationPackReady = nameof(LearningEvidenceAccumulationPackReady);
    public const string LearningEvidenceAccumulationPackBlocked = nameof(LearningEvidenceAccumulationPackBlocked);
}

public static class LearningEvidenceAccumulationPackBlockedReasons
{
    public const string SelfValidationPackMissing = nameof(SelfValidationPackMissing);
    public const string SelfValidationPackNotPassed = nameof(SelfValidationPackNotPassed);
    public const string HardNegativeCandidatesMissing = nameof(HardNegativeCandidatesMissing);
    public const string SignalLeakageAblationMissing = nameof(SignalLeakageAblationMissing);
    public const string SyntheticLabelsTreatedAsAuthority = nameof(SyntheticLabelsTreatedAsAuthority);
    public const string AutoIngestTrue = nameof(AutoIngestTrue);
    public const string TrainingSetChangedTrue = nameof(TrainingSetChangedTrue);
    public const string PositiveScoreDominanceIgnored = nameof(PositiveScoreDominanceIgnored);
    public const string EvidenceSufficientFalselyTrueUnderUnresolvedLeakage = nameof(EvidenceSufficientFalselyTrueUnderUnresolvedLeakage);
    public const string RuntimePilotExecutionAppliedTrue = nameof(RuntimePilotExecutionAppliedTrue);
    public const string RuntimePromotionAppliedTrue = nameof(RuntimePromotionAppliedTrue);
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

public sealed class HardNegativeLabeledEvidenceSimulation
{
    public string SimulationId { get; init; } = string.Empty;
    public string SimulationMode { get; init; } = "ShadowSyntheticLabelDryRun";
    public int CandidateSpecCount { get; init; }
    public int SimulatedLabeledHardNegativeCount { get; init; }
    public double SyntheticLabelConfidence { get; init; }
    public bool HardNegativeEvidenceStillInsufficient { get; init; }
    public bool EvidenceImprovedIfLabelsWereReal { get; init; }
    public bool SyntheticLabelAuthority { get; init; }
    public bool RuntimeAuthority { get; init; }
    public bool AutoIngest { get; init; }
    public bool TrainingSetChanged { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed class SignalLeakageAblationVariant
{
    public string VariantName { get; init; } = string.Empty;
    public IReadOnlyList<string> IncludedFeatures { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludedFeatures { get; init; } = Array.Empty<string>();
    public int TrainCount { get; init; }
    public int EvalCount { get; init; }
    public double PairwiseAccuracy { get; init; }
}

public sealed class SignalLeakageAblation
{
    public string AblationMode { get; init; } = "DeterministicFeatureMaskedRetrain";
    public IReadOnlyList<SignalLeakageAblationVariant> Variants { get; init; } = Array.Empty<SignalLeakageAblationVariant>();
    public double BaselineAccuracy { get; init; }
    public double AccuracyWithoutPositiveScore { get; init; }
    public double AccuracyWithoutScoreLikeFeatures { get; init; }
    public double AccuracyDropFromPositiveScoreRemoval { get; init; }
    public double AccuracyDropFromAllScoreLikeRemoval { get; init; }
    public bool PositiveScoreDominanceDetected { get; init; }
    public bool LeakageRiskReduced { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed class CounterexampleReplayReport
{
    public string ReplayMode { get; init; } = "ShadowCounterexampleReplay";
    public int CounterexampleCount { get; init; }
    public bool CounterexampleReplayReady { get; init; }
    public double CandidateFailureRateOnCounterexamples { get; init; }
    public double ReferenceFailureRateOnCounterexamples { get; init; }
    public IReadOnlyList<string> SourceFailureClusterIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public bool RuntimeDecisionChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
}

public sealed class EvidenceSufficiencyRecomputed
{
    public double PreviousEvidenceSufficiencyScore { get; init; }
    public double NewEvidenceSufficiencyScore { get; init; }
    public double Threshold { get; init; } = 0.7;
    public bool EvidenceSufficientUnderRealLabels { get; init; }
    public bool EvidenceSufficientUnderSyntheticLabelsOnly { get; init; }
    public bool EvidenceSufficient { get; init; }
    public bool SignalLeakageStillSuspected { get; init; }
    public bool HardNegativeEvidenceStillInsufficient { get; init; }
    public IReadOnlyList<string> SubscoreDeltas { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed class SelfOptimizationPlanUpdate
{
    public string PlanVersion { get; init; } = "v10.7-self-optimization/v1";
    public IReadOnlyList<string> ResolvedItems { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> OpenItems { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RecommendedActions { get; init; } = Array.Empty<string>();
    public bool RuntimeAuthority { get; init; }
    public bool GateAuthority { get; init; }
    public bool AutoIngest { get; init; }
}

public sealed record LearningEvidenceAccumulationPackContext
{
    public bool SelfValidationPackPresent { get; init; }
    public bool SelfValidationPackPassed { get; init; }
    public bool HardNegativeCandidatesPresent { get; init; }
    public int HardNegativeCandidateCount { get; init; }
    public bool V8ScopedActivationPreserved { get; init; }
    public IReadOnlyList<RankerPair> RankerPairs { get; init; } = Array.Empty<RankerPair>();
    public IReadOnlyList<string> FailureClusterIds { get; init; } = Array.Empty<string>();
    public double PreviousEvidenceSufficiencyScore { get; init; }
    // Synthetic test knobs
    public bool AutoIngestOverride { get; init; }
    public bool TrainingSetChangedOverride { get; init; }
    public bool SyntheticLabelsTreatedAsAuthorityOverride { get; init; }
    public bool PositiveScoreDominanceIgnoredOverride { get; init; }
    public bool EvidenceSufficientFalselyTrueOverride { get; init; }
    public bool RuntimePilotExecutionAppliedOverride { get; init; }
    public bool RuntimePromotionAppliedOverride { get; init; }
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
    public bool SignalLeakageAblationMissingOverride { get; init; }
}

public sealed class LearningEvidenceAccumulationPackDecision
{
    public string Status { get; init; } = LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked;
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;
}

public static class LearningEvidenceAccumulationPackPolicy
{
    public static LearningEvidenceAccumulationPackDecision Evaluate(
        LearningEvidenceAccumulationPackContext ctx,
        bool rtPassed, bool p15Passed,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent)
    {
        var blocked = new List<string>();
        if (!ctx.SelfValidationPackPresent) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.SelfValidationPackMissing);
        else if (!ctx.SelfValidationPackPassed) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.SelfValidationPackNotPassed);
        if (!ctx.HardNegativeCandidatesPresent || ctx.HardNegativeCandidateCount <= 0)
            blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.HardNegativeCandidatesMissing);
        if (ctx.SignalLeakageAblationMissingOverride) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.SignalLeakageAblationMissing);
        if (ctx.SyntheticLabelsTreatedAsAuthorityOverride) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.SyntheticLabelsTreatedAsAuthority);
        if (ctx.AutoIngestOverride) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.AutoIngestTrue);
        if (ctx.TrainingSetChangedOverride) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.TrainingSetChangedTrue);
        if (ctx.PositiveScoreDominanceIgnoredOverride) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.PositiveScoreDominanceIgnored);
        if (ctx.EvidenceSufficientFalselyTrueOverride) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.EvidenceSufficientFalselyTrueUnderUnresolvedLeakage);
        if (ctx.RuntimePilotExecutionAppliedOverride) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.RuntimePilotExecutionAppliedTrue);
        if (ctx.RuntimePromotionAppliedOverride) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.RuntimePromotionAppliedTrue);
        if (ctx.RuntimeRerankerChangedOverride) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.RuntimeRerankerChangedTrue);
        if (ctx.RuntimeRouterChangedOverride) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.RuntimeRouterChangedTrue);
        if (ctx.ProductionDecisionChangedOverride) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.ProductionDecisionChangedTrue);
        if (ctx.PackageOutputChangedOverride) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.PackageOutputChangedTrue);
        if (ctx.FormalPackageWrittenOverride) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.FormalPackageWrittenTrue);
        if (ctx.GlobalDefaultOnOverride) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.GlobalDefaultOnTrue);
        if (ctx.MLAuthorityOverride) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.MLAuthorityTrue);
        if (ctx.LLMAuthorityOverride) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.LLMAuthorityTrue);
        if (ctx.RuntimeAuthorityOverride) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.RuntimeAuthorityTrue);
        if (ctx.GateAuthorityOverride) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.GateAuthorityTrue);
        if (!ctx.V8ScopedActivationPreserved) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.V8ScopedActivationLost);
        if (mainlineEvidencePresent) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.MainlineTrustRegistryPresent);
        if (!rtPassed) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.P15GateNotPassed);
        var finalBlocked = blocked.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        var ready = finalBlocked.Length == 0;
        return new LearningEvidenceAccumulationPackDecision
        {
            Status = ready
                ? LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackReady
                : LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked,
            BlockedReasons = finalBlocked,
            Reasoning = ready
                ? "evidence accumulation pack policy ready — upstream + authority invariants satisfied; ablation + simulation + replay + recomputation computed below."
                : $"{finalBlocked.Length} blocked reason(s); evidence accumulation pack blocked."
        };
    }
}

public sealed record LearningEvidenceAccumulationPackScenario(
    string CaseName,
    LearningEvidenceAccumulationPackContext Context,
    bool RtPassed, bool P15Passed,
    bool MainlineEvidencePresent, bool MainlineRegistryPresent,
    string ExpectedStatus, string? ExpectedBlockedReason);

public sealed class LearningEvidenceAccumulationPackRunner
{
    private static readonly JsonSerializerOptions WriteIndented = new() { WriteIndented = true };
    private static readonly string[] AllFeatureNames =
    {
        "Recall3", "Recall5", "Recall10", "MRR",
        "PositiveRankInverseDelta", "PositiveScoreMinusNegativeScore",
        "PositiveSelectedMinusNegativeSelected", "PackageHasAllConstraints"
    };

    public LearningEvidenceAccumulationPackReport Run(
        LearningEvidenceAccumulationPackContext realContext,
        string outputDir,
        bool rtPassed, bool p15Passed,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent,
        LearningEvidenceAccumulationPackOptions? opt = null)
    {
        opt ??= new LearningEvidenceAccumulationPackOptions();
        var now = DateTimeOffset.UtcNow;

        var cases = BuildScenarios().Select(static scenario =>
        {
            var decision = LearningEvidenceAccumulationPackPolicy.Evaluate(
                scenario.Context, scenario.RtPassed, scenario.P15Passed,
                scenario.MainlineEvidencePresent, scenario.MainlineRegistryPresent);
            var statusMatched = string.Equals(scenario.ExpectedStatus, decision.Status, StringComparison.Ordinal);
            var blockedReasonMatched = scenario.ExpectedBlockedReason is null
                || decision.BlockedReasons.Contains(scenario.ExpectedBlockedReason, StringComparer.Ordinal);
            return new LearningEvidenceAccumulationPackCase
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
        if (cases.Length < 25) blocked.Add("InsufficientLearningEvidenceAccumulationPackCases");
        if (cases.Any(static c => !c.PassedAsExpected)) blocked.Add("LearningEvidenceAccumulationPackMatrixFailed");
        foreach (var status in new[] {
            LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackReady,
            LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked })
        {
            if (!cases.Any(c => string.Equals(c.ActualStatus, status, StringComparison.Ordinal)))
                blocked.Add($"StatusBranchNotCovered:{status}");
        }

        var realDecision = LearningEvidenceAccumulationPackPolicy.Evaluate(
            realContext, rtPassed, p15Passed, mainlineEvidencePresent, mainlineRegistryPresent);
        if (!string.Equals(realDecision.Status, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackReady, StringComparison.Ordinal))
            blocked.AddRange(realDecision.BlockedReasons.Select(static x => $"RealLearningEvidenceAccumulationPack:{x}"));
        if (!rtPassed) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(LearningEvidenceAccumulationPackBlockedReasons.MainlineTrustRegistryPresent);

        var canBuild = string.Equals(realDecision.Status, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackReady, StringComparison.Ordinal);
        HardNegativeLabeledEvidenceSimulation hardNegSim = new();
        SignalLeakageAblation ablation = new();
        CounterexampleReplayReport counterexample = new();
        EvidenceSufficiencyRecomputed recomputed = new();
        SelfOptimizationPlanUpdate planUpdate = new();
        var hardNegSimPath = string.Empty;
        var ablationPath = string.Empty;
        var counterexamplePath = string.Empty;
        var recomputedPath = string.Empty;
        var planUpdatePath = string.Empty;

        if (canBuild)
        {
            // 1. Hard-negative labeled evidence simulation — synthetic but explicitly NOT authority.
            hardNegSim = BuildHardNegativeLabeledEvidenceSimulation(realContext, now);
            hardNegSimPath = Path.Combine(outputDir, "hard-negative-labeled-evidence-simulation.json");
            File.WriteAllText(hardNegSimPath, JsonSerializer.Serialize(hardNegSim, WriteIndented), new UTF8Encoding(true));

            // 2. Signal leakage ablation — REAL retrain on feature subsets.
            ablation = BuildSignalLeakageAblation(realContext.RankerPairs);
            ablationPath = Path.Combine(outputDir, "signal-leakage-ablation.json");
            File.WriteAllText(ablationPath, JsonSerializer.Serialize(ablation, WriteIndented), new UTF8Encoding(true));

            // 3. Counterexample replay — failure clusters + hard-neg candidates as adversarial set.
            counterexample = BuildCounterexampleReplay(realContext, ablation);
            counterexamplePath = Path.Combine(outputDir, "counterexample-replay-report.json");
            File.WriteAllText(counterexamplePath, JsonSerializer.Serialize(counterexample, WriteIndented), new UTF8Encoding(true));

            // 4. Evidence sufficiency recomputed — with synthetic vs real labels.
            recomputed = BuildEvidenceSufficiencyRecomputed(realContext, ablation, hardNegSim, counterexample);
            recomputedPath = Path.Combine(outputDir, "evidence-sufficiency-recomputed.json");
            File.WriteAllText(recomputedPath, JsonSerializer.Serialize(recomputed, WriteIndented), new UTF8Encoding(true));

            // 5. Self-optimization plan update.
            planUpdate = BuildSelfOptimizationPlanUpdate(ablation, hardNegSim, recomputed);
            planUpdatePath = Path.Combine(outputDir, "self-optimization-plan-updated.json");
            File.WriteAllText(planUpdatePath, JsonSerializer.Serialize(planUpdate, WriteIndented), new UTF8Encoding(true));
        }

        // Authority leak checks on produced artifacts
        if (hardNegSim.SyntheticLabelAuthority || hardNegSim.RuntimeAuthority || hardNegSim.AutoIngest || hardNegSim.TrainingSetChanged)
            blocked.Add("HardNegSimAuthorityOrIngestLeak");
        if (counterexample.RuntimeDecisionChanged || counterexample.PackageOutputChanged) blocked.Add("CounterexampleRuntimeLeak");
        if (planUpdate.RuntimeAuthority || planUpdate.GateAuthority || planUpdate.AutoIngest) blocked.Add("PlanUpdateAuthorityLeak");

        var finalBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var passed = finalBlocked.Length == 0;
        var pilotReady = canBuild && recomputed.EvidenceSufficient && !recomputed.SignalLeakageStillSuspected && !recomputed.HardNegativeEvidenceStillInsufficient;
        var blockedExecBy = new List<string>();
        if (canBuild && !pilotReady)
        {
            if (!recomputed.EvidenceSufficient) blockedExecBy.Add("EvidenceInsufficient");
            if (recomputed.SignalLeakageStillSuspected) blockedExecBy.Add("SignalLeakageStillSuspected");
            if (recomputed.HardNegativeEvidenceStillInsufficient) blockedExecBy.Add("HardNegativeEvidenceStillInsufficient");
        }

        return new LearningEvidenceAccumulationPackReport
        {
            OperationId = $"v10-learning-evidence-accumulation-pack-{Guid.NewGuid():N}",
            CreatedAt = now,
            EvidenceAccumulationPackPassed = passed,
            GatePassed = opt.IsGate && passed,
            TotalCases = cases.Length,
            PassedCases = cases.Count(static c => c.PassedAsExpected),
            FailedCases = cases.Count(static c => !c.PassedAsExpected),
            ReadyCases = cases.Count(c => string.Equals(c.ActualStatus, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackReady, StringComparison.Ordinal)),
            BlockedCases = cases.Count(c => string.Equals(c.ActualStatus, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked, StringComparison.Ordinal)),
            Cases = cases,
            HardNegativeLabeledEvidenceSimulation = hardNegSim,
            SignalLeakageAblation = ablation,
            CounterexampleReplayReport = counterexample,
            EvidenceSufficiencyRecomputed = recomputed,
            SelfOptimizationPlanUpdate = planUpdate,
            HardNegativeLabeledEvidenceSimulationReady = canBuild && hardNegSim.SimulatedLabeledHardNegativeCount > 0,
            SignalLeakageAblationReady = canBuild && ablation.Variants.Count >= 3,
            CounterexampleReplayReady = canBuild && counterexample.CounterexampleReplayReady,
            EvidenceSufficiencyRecomputedReady = canBuild,
            SelfOptimizationPlanUpdateReady = canBuild,
            PositiveScoreDominanceDetected = ablation.PositiveScoreDominanceDetected,
            LeakageRiskReduced = ablation.LeakageRiskReduced,
            EvidenceSufficient = recomputed.EvidenceSufficient,
            SignalLeakageStillSuspected = recomputed.SignalLeakageStillSuspected,
            HardNegativeEvidenceStillInsufficient = recomputed.HardNegativeEvidenceStillInsufficient,
            RuntimePilotExecutionReadyForSeparateGate = pilotReady,
            BlockedForRuntimePilotExecutionBy = blockedExecBy,
            SyntheticLabelAuthority = false,
            HumanReviewAsGateAuthority = false,
            HumanFeedbackAutoIngest = false,
            AutoIngest = false,
            TrainingSetChanged = false,
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
            V8ScopedActivationPreserved = realContext.V8ScopedActivationPreserved,
            UpstreamSelfValidationPackGatePresent = realContext.SelfValidationPackPresent,
            UpstreamSelfValidationPackGatePassed = realContext.SelfValidationPackPassed,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent,
            HardNegativeLabeledEvidenceSimulationPath = hardNegSimPath,
            SignalLeakageAblationPath = ablationPath,
            CounterexampleReplayReportPath = counterexamplePath,
            EvidenceSufficiencyRecomputedPath = recomputedPath,
            SelfOptimizationPlanUpdatedPath = planUpdatePath,
            Recommendation = passed
                ? (pilotReady ? "ProceedToV10.10PilotExecutionGate" : "BlockedForRuntimePilotExecution-EvidenceCalibrated-AccumulateMoreEvidence")
                : "Blocked",
            NextAllowedPhase = passed
                ? (pilotReady ? "V10.10PilotExecutionGate" : "V10.10PilotExecution-pending-evidence")
                : string.Empty,
            BlockedReasons = finalBlocked,
            Diagnostics = new[]
            {
                $"total={cases.Length}",
                $"realStatus={realDecision.Status}",
                $"ablationVariants={ablation.Variants.Count}",
                $"baselineAccuracy={ablation.BaselineAccuracy:F3}",
                $"accuracyWithoutPositiveScore={ablation.AccuracyWithoutPositiveScore:F3}",
                $"accuracyDrop={ablation.AccuracyDropFromPositiveScoreRemoval:F3}",
                $"positiveScoreDominanceDetected={ablation.PositiveScoreDominanceDetected}",
                $"leakageRiskReduced={ablation.LeakageRiskReduced}",
                $"simulatedLabeledHardNegativeCount={hardNegSim.SimulatedLabeledHardNegativeCount}",
                $"counterexampleCount={counterexample.CounterexampleCount}",
                $"recomputedEvidenceScore={recomputed.NewEvidenceSufficiencyScore:F3}",
                $"evidenceSufficient={recomputed.EvidenceSufficient}",
                $"runtimePilotReady={pilotReady}"
            }
        };
    }

    // ─── matrix scenarios (28 cases) ────────────────────────────────────────────
    private static IReadOnlyList<LearningEvidenceAccumulationPackScenario> BuildScenarios()
    {
        var clean = BuildCleanContext();
        return [
            new("AllUpstreamClean", clean, true, true, false, false, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackReady, null),
            new("SelfValidationPackMissing", clean with { SelfValidationPackPresent = false }, true, true, false, false, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked, LearningEvidenceAccumulationPackBlockedReasons.SelfValidationPackMissing),
            new("SelfValidationPackNotPassed", clean with { SelfValidationPackPassed = false }, true, true, false, false, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked, LearningEvidenceAccumulationPackBlockedReasons.SelfValidationPackNotPassed),
            new("HardNegativeCandidatesMissing", clean with { HardNegativeCandidatesPresent = false, HardNegativeCandidateCount = 0 }, true, true, false, false, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked, LearningEvidenceAccumulationPackBlockedReasons.HardNegativeCandidatesMissing),
            new("SignalLeakageAblationMissing", clean with { SignalLeakageAblationMissingOverride = true }, true, true, false, false, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked, LearningEvidenceAccumulationPackBlockedReasons.SignalLeakageAblationMissing),
            new("SyntheticLabelsTreatedAsAuthority", clean with { SyntheticLabelsTreatedAsAuthorityOverride = true }, true, true, false, false, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked, LearningEvidenceAccumulationPackBlockedReasons.SyntheticLabelsTreatedAsAuthority),
            new("AutoIngestTrue", clean with { AutoIngestOverride = true }, true, true, false, false, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked, LearningEvidenceAccumulationPackBlockedReasons.AutoIngestTrue),
            new("TrainingSetChangedTrue", clean with { TrainingSetChangedOverride = true }, true, true, false, false, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked, LearningEvidenceAccumulationPackBlockedReasons.TrainingSetChangedTrue),
            new("PositiveScoreDominanceIgnored", clean with { PositiveScoreDominanceIgnoredOverride = true }, true, true, false, false, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked, LearningEvidenceAccumulationPackBlockedReasons.PositiveScoreDominanceIgnored),
            new("EvidenceSufficientFalselyTrue", clean with { EvidenceSufficientFalselyTrueOverride = true }, true, true, false, false, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked, LearningEvidenceAccumulationPackBlockedReasons.EvidenceSufficientFalselyTrueUnderUnresolvedLeakage),
            new("RuntimePilotExecutionAppliedTrue", clean with { RuntimePilotExecutionAppliedOverride = true }, true, true, false, false, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked, LearningEvidenceAccumulationPackBlockedReasons.RuntimePilotExecutionAppliedTrue),
            new("RuntimePromotionAppliedTrue", clean with { RuntimePromotionAppliedOverride = true }, true, true, false, false, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked, LearningEvidenceAccumulationPackBlockedReasons.RuntimePromotionAppliedTrue),
            new("RuntimeRerankerChangedTrue", clean with { RuntimeRerankerChangedOverride = true }, true, true, false, false, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked, LearningEvidenceAccumulationPackBlockedReasons.RuntimeRerankerChangedTrue),
            new("RuntimeRouterChangedTrue", clean with { RuntimeRouterChangedOverride = true }, true, true, false, false, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked, LearningEvidenceAccumulationPackBlockedReasons.RuntimeRouterChangedTrue),
            new("ProductionDecisionChangedTrue", clean with { ProductionDecisionChangedOverride = true }, true, true, false, false, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked, LearningEvidenceAccumulationPackBlockedReasons.ProductionDecisionChangedTrue),
            new("PackageOutputChangedTrue", clean with { PackageOutputChangedOverride = true }, true, true, false, false, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked, LearningEvidenceAccumulationPackBlockedReasons.PackageOutputChangedTrue),
            new("FormalPackageWrittenTrue", clean with { FormalPackageWrittenOverride = true }, true, true, false, false, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked, LearningEvidenceAccumulationPackBlockedReasons.FormalPackageWrittenTrue),
            new("GlobalDefaultOnTrue", clean with { GlobalDefaultOnOverride = true }, true, true, false, false, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked, LearningEvidenceAccumulationPackBlockedReasons.GlobalDefaultOnTrue),
            new("MLAuthorityTrue", clean with { MLAuthorityOverride = true }, true, true, false, false, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked, LearningEvidenceAccumulationPackBlockedReasons.MLAuthorityTrue),
            new("LLMAuthorityTrue", clean with { LLMAuthorityOverride = true }, true, true, false, false, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked, LearningEvidenceAccumulationPackBlockedReasons.LLMAuthorityTrue),
            new("RuntimeAuthorityTrue", clean with { RuntimeAuthorityOverride = true }, true, true, false, false, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked, LearningEvidenceAccumulationPackBlockedReasons.RuntimeAuthorityTrue),
            new("GateAuthorityTrue", clean with { GateAuthorityOverride = true }, true, true, false, false, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked, LearningEvidenceAccumulationPackBlockedReasons.GateAuthorityTrue),
            new("V8ScopedActivationLost", clean with { V8ScopedActivationPreserved = false }, true, true, false, false, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked, LearningEvidenceAccumulationPackBlockedReasons.V8ScopedActivationLost),
            new("MainlineEvidencePresent", clean, true, true, true, false, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked, LearningEvidenceAccumulationPackBlockedReasons.MainlineEvidencePresent),
            new("MainlineTrustRegistryPresent", clean, true, true, false, true, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked, LearningEvidenceAccumulationPackBlockedReasons.MainlineTrustRegistryPresent),
            new("RuntimeGateNotPassed", clean, false, true, false, false, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked, LearningEvidenceAccumulationPackBlockedReasons.RuntimeChangeGateNotPassed),
            new("P15GateNotPassed", clean, true, false, false, false, LearningEvidenceAccumulationPackStatuses.LearningEvidenceAccumulationPackBlocked, LearningEvidenceAccumulationPackBlockedReasons.P15GateNotPassed)
        ];
    }

    private static LearningEvidenceAccumulationPackContext BuildCleanContext() => new()
    {
        SelfValidationPackPresent = true,
        SelfValidationPackPassed = true,
        HardNegativeCandidatesPresent = true,
        HardNegativeCandidateCount = 60,
        V8ScopedActivationPreserved = true,
        RankerPairs = Array.Empty<RankerPair>(),  // matrix scenarios don't need real pairs
        FailureClusterIds = new[] { "ranker-WeightedBaseline", "router-RouterIntentLogistic", "router-RouterIntentTree" },
        PreviousEvidenceSufficiencyScore = 0.514
    };

    // ─── builders ────

    private static HardNegativeLabeledEvidenceSimulation BuildHardNegativeLabeledEvidenceSimulation(
        LearningEvidenceAccumulationPackContext ctx, DateTimeOffset now)
    {
        // Synthetic simulation: assume each candidate spec WOULD become a labeled hard-neg if reviewed.
        // We do NOT actually create labels — this artifact reports the hypothetical state.
        var simulatedLabeled = ctx.HardNegativeCandidateCount;
        var threshold = Math.Max(20, ctx.HardNegativeCandidateCount / 4);
        var stillInsufficient = simulatedLabeled < threshold;  // unlikely to fire since we simulate all; we keep flag honest
        return new HardNegativeLabeledEvidenceSimulation
        {
            SimulationId = $"v10-hard-neg-label-sim-{Guid.NewGuid():N}",
            SimulationMode = "ShadowSyntheticLabelDryRun",
            CandidateSpecCount = ctx.HardNegativeCandidateCount,
            SimulatedLabeledHardNegativeCount = simulatedLabeled,
            SyntheticLabelConfidence = 0.0,  // synthetic labels have no human-graded confidence
            HardNegativeEvidenceStillInsufficient = true,  // SIMULATION ONLY — real labels still required
            EvidenceImprovedIfLabelsWereReal = simulatedLabeled >= threshold,
            SyntheticLabelAuthority = false,
            RuntimeAuthority = false,
            AutoIngest = false,
            TrainingSetChanged = false,
            Notes = new[]
            {
                "Simulation only: this report describes the hypothetical state IF every candidate spec were approved by human reviewers.",
                "Synthetic labels carry confidence=0.0 and DO NOT count as real training evidence.",
                "HardNegativeEvidenceStillInsufficient=true because no real labels exist on disk yet.",
                "AutoIngest=false / TrainingSetChanged=false — the formal training set is not modified by this simulation.",
                "Path to resolution: V9.5 feedback ingestion contract collects labeled hard-negatives → unblocks V10.7 evidence sufficiency."
            }
        };
    }

    /// <summary>Real signal-leakage ablation: retrain LogisticBaseline with feature subsets, observe accuracy drop.</summary>
    private static SignalLeakageAblation BuildSignalLeakageAblation(IReadOnlyList<RankerPair> pairs)
    {
        if (pairs.Count == 0)
        {
            return new SignalLeakageAblation
            {
                AblationMode = "DeterministicFeatureMaskedRetrain",
                Variants = Array.Empty<SignalLeakageAblationVariant>(),
                Notes = new[] { "no ranker pairs available — ablation skipped" }
            };
        }

        // Deterministic 80/20 split
        var sorted = pairs.OrderBy(p => p.EvalSampleId, StringComparer.Ordinal).ThenBy(p => p.PositiveCandidateId, StringComparer.Ordinal).ToList();
        var train = new List<RankerPair>();
        var eval = new List<RankerPair>();
        foreach (var p in sorted)
        {
            if (GroupHash(p.EvalSampleId) % 5 == 0) eval.Add(p); else train.Add(p);
        }

        // Feature indices: 0=Recall3, 1=Recall5, 2=Recall10, 3=MRR,
        //                  4=PositiveRankInverseDelta, 5=PositiveScoreMinusNegativeScore,
        //                  6=PositiveSelectedMinusNegativeSelected, 7=PackageHasAllConstraints
        // Variants:
        //  - All features (baseline)
        //  - Without positiveScore (index 5)
        //  - Without score-like features (indices 5, 6)
        //  - Only structural features (indices 7) — minimal
        //  - Only recall family (indices 0, 1, 2)
        var variants = new List<SignalLeakageAblationVariant>();
        var allIndices = Enumerable.Range(0, 8).ToArray();
        variants.Add(TrainAblationVariant("all-features", allIndices, train, eval));
        variants.Add(TrainAblationVariant("without-positiveScore", new[] { 0, 1, 2, 3, 4, 6, 7 }, train, eval));
        variants.Add(TrainAblationVariant("without-score-like", new[] { 0, 1, 2, 3, 4, 7 }, train, eval));
        variants.Add(TrainAblationVariant("only-structural", new[] { 7 }, train, eval));
        variants.Add(TrainAblationVariant("only-recall-family", new[] { 0, 1, 2 }, train, eval));

        var baseline = variants[0].PairwiseAccuracy;
        var withoutPS = variants[1].PairwiseAccuracy;
        var withoutScoreLike = variants[2].PairwiseAccuracy;
        var dropPS = baseline - withoutPS;
        var dropAllScore = baseline - withoutScoreLike;
        // Heuristic: dominance detected if removing positiveScore drops accuracy ≥ 0.05 absolute points
        var dominance = dropPS >= 0.05;
        // Risk reduced if without-score-like still ≥ 0.85 (i.e., structural features alone carry signal)
        var leakageReduced = withoutScoreLike >= 0.85;

        var notes = new List<string>
        {
            $"BaselineAccuracy={baseline:F3}",
            $"AccuracyWithoutPositiveScore={withoutPS:F3} (drop={dropPS:F3})",
            $"AccuracyWithoutScoreLikeFeatures={withoutScoreLike:F3} (drop={dropAllScore:F3})",
            $"PositiveScoreDominanceDetected={dominance} (threshold: drop ≥0.05)",
            $"LeakageRiskReduced={leakageReduced} (structural features alone score ≥0.85)",
            "Ablation is REAL: each variant retrains Logistic regression on a feature subset using the same 80/20 split as V9.1-V9.3.",
            "If dominance detected and leakage not reduced, the candidate's apparent strength is positively driven by a single dominant feature — pilot must address before promotion."
        };
        return new SignalLeakageAblation
        {
            AblationMode = "DeterministicFeatureMaskedRetrain",
            Variants = variants,
            BaselineAccuracy = baseline,
            AccuracyWithoutPositiveScore = withoutPS,
            AccuracyWithoutScoreLikeFeatures = withoutScoreLike,
            AccuracyDropFromPositiveScoreRemoval = dropPS,
            AccuracyDropFromAllScoreLikeRemoval = dropAllScore,
            PositiveScoreDominanceDetected = dominance,
            LeakageRiskReduced = leakageReduced,
            Notes = notes
        };
    }

    private static SignalLeakageAblationVariant TrainAblationVariant(string name, int[] featureIndices, List<RankerPair> train, List<RankerPair> eval)
    {
        if (train.Count == 0 || eval.Count == 0)
            return new SignalLeakageAblationVariant { VariantName = name, IncludedFeatures = featureIndices.Select(i => AllFeatureNames[i]).ToArray(), TrainCount = train.Count, EvalCount = eval.Count, PairwiseAccuracy = 0 };
        var dim = featureIndices.Length;
        var weights = new double[dim];
        const double lr = 0.05;
        const int iters = 200;
        var sortedTrain = train.OrderBy(p => p.EvalSampleId, StringComparer.Ordinal).ToList();
        for (int it = 0; it < iters; it++)
        {
            var grad = new double[dim];
            foreach (var p in sortedTrain)
            {
                var z = ScoreLinearSubset(weights, p.Features, featureIndices);
                var sig = 1.0 / (1.0 + Math.Exp(-z));
                var coef = -(1.0 - sig);
                for (int i = 0; i < dim; i++) grad[i] += coef * p.Features[featureIndices[i]];
            }
            for (int i = 0; i < dim; i++) weights[i] -= lr * grad[i] / Math.Max(1, sortedTrain.Count);
        }
        int correct = 0;
        foreach (var p in eval)
        {
            var z = ScoreLinearSubset(weights, p.Features, featureIndices);
            if (z > 0) correct++;
        }
        return new SignalLeakageAblationVariant
        {
            VariantName = name,
            IncludedFeatures = featureIndices.Select(i => AllFeatureNames[i]).ToArray(),
            ExcludedFeatures = Enumerable.Range(0, AllFeatureNames.Length).Except(featureIndices).Select(i => AllFeatureNames[i]).ToArray(),
            TrainCount = train.Count,
            EvalCount = eval.Count,
            PairwiseAccuracy = eval.Count > 0 ? (double)correct / eval.Count : 0
        };
    }

    private static double ScoreLinearSubset(double[] weights, double[] features, int[] indices)
    {
        double s = 0;
        for (int i = 0; i < indices.Length && i < weights.Length; i++) s += weights[i] * features[indices[i]];
        return s;
    }

    private static int GroupHash(string key)
    {
        const int prime = 16777619;
        int hash = unchecked((int)2166136261);
        foreach (var c in key) { hash = (hash ^ c) * prime; }
        return hash & 0x7FFFFFFF;
    }

    private static CounterexampleReplayReport BuildCounterexampleReplay(
        LearningEvidenceAccumulationPackContext ctx, SignalLeakageAblation ablation)
    {
        // Counterexample set: pairs whose features are below the median of the dominant feature (positiveScore - negativeScore).
        // Idea: hard cases where the candidate cannot rely on positiveScore dominance.
        if (ctx.RankerPairs.Count == 0)
        {
            return new CounterexampleReplayReport
            {
                ReplayMode = "ShadowCounterexampleReplay",
                CounterexampleCount = 0,
                CounterexampleReplayReady = false,
                CandidateFailureRateOnCounterexamples = 0,
                ReferenceFailureRateOnCounterexamples = 0,
                SourceFailureClusterIds = ctx.FailureClusterIds,
                Notes = new[] { "no ranker pairs available — counterexample replay skipped" },
                RuntimeDecisionChanged = false,
                PackageOutputChanged = false
            };
        }
        // Determine median of feature index 5 (PositiveScoreMinusNegativeScore)
        var sorted5 = ctx.RankerPairs.Select(p => p.Features[5]).OrderBy(v => v).ToArray();
        var median = sorted5[sorted5.Length / 2];
        // Counterexamples: pairs where positiveScore - negativeScore <= median (harder cases)
        var counterexamples = ctx.RankerPairs.Where(p => p.Features[5] <= median).ToList();
        // Candidate (Logistic-like, full features): scores positive wins if positiveScore > negativeScore
        // We rely on the actual scoreDelta — on counterexamples, scoreDelta is small/zero so candidate often ties/fails
        int candidateFailures = counterexamples.Count(p => p.Features[5] <= 0);
        // Reference (Weighted, hand-tuned): same logic but with weights — approximate as feature 5 directly (positiveScore is the strongest signal in weighted too)
        int referenceFailures = counterexamples.Count(p => p.Features[5] <= 0 && p.Features[0] < 0.5);
        return new CounterexampleReplayReport
        {
            ReplayMode = "ShadowCounterexampleReplay",
            CounterexampleCount = counterexamples.Count,
            CounterexampleReplayReady = counterexamples.Count > 0,
            CandidateFailureRateOnCounterexamples = counterexamples.Count > 0 ? (double)candidateFailures / counterexamples.Count : 0,
            ReferenceFailureRateOnCounterexamples = counterexamples.Count > 0 ? (double)referenceFailures / counterexamples.Count : 0,
            SourceFailureClusterIds = ctx.FailureClusterIds,
            Notes = new[]
            {
                $"Counterexamples are pairs where (positiveScore - negativeScore) ≤ median = {median:F3}.",
                "These are the hard cases where positiveScore dominance fails to discriminate.",
                $"Candidate fails on {candidateFailures}/{counterexamples.Count} counterexamples; reference fails on {referenceFailures}/{counterexamples.Count}.",
                "Runtime decision unchanged. Package output unchanged. This is shadow replay only."
            },
            RuntimeDecisionChanged = false,
            PackageOutputChanged = false
        };
    }

    private static EvidenceSufficiencyRecomputed BuildEvidenceSufficiencyRecomputed(
        LearningEvidenceAccumulationPackContext ctx,
        SignalLeakageAblation ablation,
        HardNegativeLabeledEvidenceSimulation hardNegSim,
        CounterexampleReplayReport counterexample)
    {
        // Recompute: take previous score, then adjust based on ablation + counterexample evidence.
        var prev = ctx.PreviousEvidenceSufficiencyScore;
        // Ablation contribution: if leakage reduced, +0.20; else +0
        var ablationContrib = ablation.LeakageRiskReduced ? 0.20 : 0.0;
        // Counterexample contribution: if candidate failure rate < reference failure rate AND <50%, +0.05
        var counterexampleContrib = (counterexample.CounterexampleReplayReady
            && counterexample.CandidateFailureRateOnCounterexamples < counterexample.ReferenceFailureRateOnCounterexamples
            && counterexample.CandidateFailureRateOnCounterexamples < 0.5) ? 0.05 : 0.0;
        // Hard-negative simulation: ONLY counts if labels were real — we never assume synthetic
        var hardNegContrib = 0.0;  // synthetic simulation produces zero real evidence
        // Penalty for signal leakage still suspected
        var leakageStillSuspected = ablation.PositiveScoreDominanceDetected && !ablation.LeakageRiskReduced;
        var leakagePenalty = leakageStillSuspected ? 0.0 : 0.10;  // bonus to undo previous penalty when resolved
        var newScore = Math.Min(1.0, Math.Max(0.0, prev + ablationContrib + counterexampleContrib + hardNegContrib + leakagePenalty));
        // EvidenceSufficient: require new score ≥0.7 AND leakage resolved AND real labels exist (always false here without real labels)
        var hardNegStillInsufficient = hardNegSim.HardNegativeEvidenceStillInsufficient;
        var sufficient = newScore >= 0.7 && !leakageStillSuspected && !hardNegStillInsufficient;
        // What-if: if synthetic labels were real
        var sufficientWithSynthetic = newScore >= 0.7 && !leakageStillSuspected;
        var deltas = new List<string>
        {
            $"AblationContribution=+{ablationContrib:F3} (LeakageRiskReduced={ablation.LeakageRiskReduced})",
            $"CounterexampleContribution=+{counterexampleContrib:F3} (candidateFailRate={counterexample.CandidateFailureRateOnCounterexamples:F3} vs reference={counterexample.ReferenceFailureRateOnCounterexamples:F3})",
            $"HardNegativeContribution=+{hardNegContrib:F3} (synthetic labels carry zero real evidence)",
            $"LeakageResolutionBonus=+{leakagePenalty:F3} (only when leakage actually resolved)"
        };
        return new EvidenceSufficiencyRecomputed
        {
            PreviousEvidenceSufficiencyScore = prev,
            NewEvidenceSufficiencyScore = newScore,
            Threshold = 0.7,
            EvidenceSufficientUnderRealLabels = sufficient,
            EvidenceSufficientUnderSyntheticLabelsOnly = sufficientWithSynthetic,
            EvidenceSufficient = sufficient,
            SignalLeakageStillSuspected = leakageStillSuspected,
            HardNegativeEvidenceStillInsufficient = hardNegStillInsufficient,
            SubscoreDeltas = deltas,
            Notes = new[]
            {
                $"Previous score={prev:F3} → new score={newScore:F3} (threshold={0.7:F3})",
                "Evidence sufficiency requires BOTH leakage resolved AND real labels — neither can be substituted by synthetic data.",
                $"EvidenceSufficientUnderRealLabels={sufficient}",
                $"EvidenceSufficientUnderSyntheticLabelsOnly={sufficientWithSynthetic} (what-if: real labels in place)",
                "SyntheticLabelsTreatedAsAuthority is NOT permitted in this pack — see policy invariant."
            }
        };
    }

    private static SelfOptimizationPlanUpdate BuildSelfOptimizationPlanUpdate(
        SignalLeakageAblation ablation,
        HardNegativeLabeledEvidenceSimulation hardNegSim,
        EvidenceSufficiencyRecomputed recomputed)
    {
        var resolved = new List<string>();
        var open = new List<string>();
        var actions = new List<string>();
        if (ablation.LeakageRiskReduced) resolved.Add("Signal leakage risk reduced (structural features alone score ≥0.85)");
        else open.Add($"Signal leakage NOT resolved (without-score-like accuracy={ablation.AccuracyWithoutScoreLikeFeatures:F3} < 0.85)");
        if (ablation.PositiveScoreDominanceDetected) open.Add($"PositiveScoreDominance detected (drop={ablation.AccuracyDropFromPositiveScoreRemoval:F3})");
        if (recomputed.HardNegativeEvidenceStillInsufficient) open.Add($"Hard-negative real labels still missing (simulated={hardNegSim.SimulatedLabeledHardNegativeCount}, real=0)");
        if (recomputed.EvidenceSufficient) resolved.Add($"EvidenceSufficiencyScore={recomputed.NewEvidenceSufficiencyScore:F3} ≥ threshold");
        else open.Add($"EvidenceSufficiencyScore={recomputed.NewEvidenceSufficiencyScore:F3} below threshold {recomputed.Threshold:F3}");

        // recommended actions, ordered by priority
        if (ablation.PositiveScoreDominanceDetected && !ablation.LeakageRiskReduced)
        {
            actions.Add("Add structural features (queryTopicEmbedding, intentLifecycleStage, modeXIntentSimilarity) to ranker baselines and retrain. Goal: structural-only accuracy ≥0.85.");
            actions.Add("Construct adversarial test set where positiveScore - negativeScore = 0; observe candidate accuracy degradation.");
        }
        if (recomputed.HardNegativeEvidenceStillInsufficient)
        {
            actions.Add("Drive 60 hard-negative candidate specs through V9.5 feedback ingestion: reviewers label ≥20 as confirmed negatives. After ingestion, re-run V10.7.");
            actions.Add("Augment hard-negative dataset with adversarial generation from failure clusters (within shadow scope only).");
        }
        if (!ablation.LeakageRiskReduced || recomputed.HardNegativeEvidenceStillInsufficient)
            actions.Add("Do NOT promote LogisticBaseline to pilot execution. Repeat V10.3-V10.7 cycle after evidence accumulation.");
        if (resolved.Count > 0 && open.Count == 0)
            actions.Add("All open items resolved — V10.10 PilotExecutionGate may now consider runtime pilot under V8-style guarded scoped activation.");
        if (actions.Count == 0) actions.Add("No remediation needed; await V10.10 PilotExecutionGate.");

        return new SelfOptimizationPlanUpdate
        {
            PlanVersion = "v10.7-self-optimization/v1",
            ResolvedItems = resolved,
            OpenItems = open,
            RecommendedActions = actions,
            RuntimeAuthority = false,
            GateAuthority = false,
            AutoIngest = false
        };
    }

    public static string BuildMarkdown(string title, LearningEvidenceAccumulationPackReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"- EvidenceAccumulationPackPassed: `{report.EvidenceAccumulationPackPassed}`");
        sb.AppendLine($"- GatePassed: `{report.GatePassed}`");
        sb.AppendLine($"- TotalCases: `{report.TotalCases}` PassedCases: `{report.PassedCases}` FailedCases: `{report.FailedCases}`");
        sb.AppendLine();
        sb.AppendLine("## Signal Leakage Ablation (REAL retrain)");
        sb.AppendLine($"- BaselineAccuracy: `{report.SignalLeakageAblation.BaselineAccuracy:F3}`");
        sb.AppendLine($"- AccuracyWithoutPositiveScore: `{report.SignalLeakageAblation.AccuracyWithoutPositiveScore:F3}` (drop=`{report.SignalLeakageAblation.AccuracyDropFromPositiveScoreRemoval:F3}`)");
        sb.AppendLine($"- AccuracyWithoutScoreLikeFeatures: `{report.SignalLeakageAblation.AccuracyWithoutScoreLikeFeatures:F3}` (drop=`{report.SignalLeakageAblation.AccuracyDropFromAllScoreLikeRemoval:F3}`)");
        sb.AppendLine($"- **PositiveScoreDominanceDetected**: `{report.PositiveScoreDominanceDetected}`");
        sb.AppendLine($"- **LeakageRiskReduced**: `{report.LeakageRiskReduced}`");
        sb.AppendLine();
        foreach (var v in report.SignalLeakageAblation.Variants)
            sb.AppendLine($"  - `{v.VariantName}` train={v.TrainCount} eval={v.EvalCount} pairwiseAcc={v.PairwiseAccuracy:F3} included=[{string.Join(",", v.IncludedFeatures)}]");
        sb.AppendLine();
        sb.AppendLine("## Hard-Negative Labeled Evidence Simulation");
        sb.AppendLine($"- SimulationMode: `{report.HardNegativeLabeledEvidenceSimulation.SimulationMode}`");
        sb.AppendLine($"- CandidateSpecCount: `{report.HardNegativeLabeledEvidenceSimulation.CandidateSpecCount}` SimulatedLabeledHardNegativeCount: `{report.HardNegativeLabeledEvidenceSimulation.SimulatedLabeledHardNegativeCount}`");
        sb.AppendLine($"- SyntheticLabelConfidence: `{report.HardNegativeLabeledEvidenceSimulation.SyntheticLabelConfidence:F3}`");
        sb.AppendLine($"- **HardNegativeEvidenceStillInsufficient**: `{report.HardNegativeLabeledEvidenceSimulation.HardNegativeEvidenceStillInsufficient}` (synthetic labels do NOT count as real)");
        sb.AppendLine($"- EvidenceImprovedIfLabelsWereReal: `{report.HardNegativeLabeledEvidenceSimulation.EvidenceImprovedIfLabelsWereReal}`");
        sb.AppendLine($"- SyntheticLabelAuthority: `{report.HardNegativeLabeledEvidenceSimulation.SyntheticLabelAuthority}` AutoIngest: `{report.HardNegativeLabeledEvidenceSimulation.AutoIngest}` TrainingSetChanged: `{report.HardNegativeLabeledEvidenceSimulation.TrainingSetChanged}`");
        sb.AppendLine();
        sb.AppendLine("## Counterexample Replay");
        sb.AppendLine($"- CounterexampleCount: `{report.CounterexampleReplayReport.CounterexampleCount}` ReplayReady: `{report.CounterexampleReplayReport.CounterexampleReplayReady}`");
        sb.AppendLine($"- CandidateFailureRate: `{report.CounterexampleReplayReport.CandidateFailureRateOnCounterexamples:F3}` ReferenceFailureRate: `{report.CounterexampleReplayReport.ReferenceFailureRateOnCounterexamples:F3}`");
        sb.AppendLine();
        sb.AppendLine("## Evidence Sufficiency Recomputed");
        sb.AppendLine($"- PreviousScore: `{report.EvidenceSufficiencyRecomputed.PreviousEvidenceSufficiencyScore:F3}` → NewScore: `{report.EvidenceSufficiencyRecomputed.NewEvidenceSufficiencyScore:F3}` (threshold=`{report.EvidenceSufficiencyRecomputed.Threshold:F3}`)");
        sb.AppendLine($"- **EvidenceSufficient**: `{report.EvidenceSufficient}` (Real labels: `{report.EvidenceSufficiencyRecomputed.EvidenceSufficientUnderRealLabels}` / Synthetic-only what-if: `{report.EvidenceSufficiencyRecomputed.EvidenceSufficientUnderSyntheticLabelsOnly}`)");
        sb.AppendLine($"- **SignalLeakageStillSuspected**: `{report.SignalLeakageStillSuspected}` HardNegativeEvidenceStillInsufficient: `{report.HardNegativeEvidenceStillInsufficient}`");
        sb.AppendLine();
        sb.AppendLine("## Self-Optimization Plan Update");
        sb.AppendLine($"- PlanVersion: `{report.SelfOptimizationPlanUpdate.PlanVersion}`");
        sb.AppendLine("- Resolved:");
        foreach (var r in report.SelfOptimizationPlanUpdate.ResolvedItems) sb.AppendLine($"  - {r}");
        sb.AppendLine("- Open:");
        foreach (var o in report.SelfOptimizationPlanUpdate.OpenItems) sb.AppendLine($"  - {o}");
        sb.AppendLine("- RecommendedActions:");
        foreach (var a in report.SelfOptimizationPlanUpdate.RecommendedActions) sb.AppendLine($"  - {a}");
        sb.AppendLine();
        sb.AppendLine("## Authority Invariants");
        sb.AppendLine($"- MLAuthority: `{report.MLAuthority}` LLMAuthority: `{report.LLMAuthority}` RuntimeAuthority: `{report.RuntimeAuthority}` GateAuthority: `{report.GateAuthority}`");
        sb.AppendLine($"- SyntheticLabelAuthority: `{report.SyntheticLabelAuthority}` HumanReviewAsGateAuthority: `{report.HumanReviewAsGateAuthority}` AutoIngest: `{report.AutoIngest}` TrainingSetChanged: `{report.TrainingSetChanged}`");
        sb.AppendLine($"- RuntimePromotionApplied: `{report.RuntimePromotionApplied}` RuntimePilotExecutionApplied: `{report.RuntimePilotExecutionApplied}`");
        sb.AppendLine($"- RuntimeRerankerChanged: `{report.RuntimeRerankerChanged}` RuntimeRouterChanged: `{report.RuntimeRouterChanged}` ProductionDecisionChanged: `{report.ProductionDecisionChanged}`");
        sb.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}` FormalPackageWritten: `{report.FormalPackageWritten}` GlobalDefaultOn: `{report.GlobalDefaultOn}`");
        sb.AppendLine($"- V8ScopedActivationPreserved: `{report.V8ScopedActivationPreserved}`");
        sb.AppendLine();
        sb.AppendLine($"- RuntimePilotExecutionReadyForSeparateGate: `{report.RuntimePilotExecutionReadyForSeparateGate}`");
        if (report.BlockedForRuntimePilotExecutionBy.Count > 0)
            sb.AppendLine($"- BlockedForRuntimePilotExecutionBy: `{string.Join(", ", report.BlockedForRuntimePilotExecutionBy)}`");
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

public sealed class LearningEvidenceAccumulationPackCase
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

public sealed class LearningEvidenceAccumulationPackReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool EvidenceAccumulationPackPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public int ReadyCases { get; init; }
    public int BlockedCases { get; init; }
    public IReadOnlyList<LearningEvidenceAccumulationPackCase> Cases { get; init; } = Array.Empty<LearningEvidenceAccumulationPackCase>();
    public HardNegativeLabeledEvidenceSimulation HardNegativeLabeledEvidenceSimulation { get; init; } = new();
    public SignalLeakageAblation SignalLeakageAblation { get; init; } = new();
    public CounterexampleReplayReport CounterexampleReplayReport { get; init; } = new();
    public EvidenceSufficiencyRecomputed EvidenceSufficiencyRecomputed { get; init; } = new();
    public SelfOptimizationPlanUpdate SelfOptimizationPlanUpdate { get; init; } = new();
    public bool HardNegativeLabeledEvidenceSimulationReady { get; init; }
    public bool SignalLeakageAblationReady { get; init; }
    public bool CounterexampleReplayReady { get; init; }
    public bool EvidenceSufficiencyRecomputedReady { get; init; }
    public bool SelfOptimizationPlanUpdateReady { get; init; }
    public bool PositiveScoreDominanceDetected { get; init; }
    public bool LeakageRiskReduced { get; init; }
    public bool EvidenceSufficient { get; init; }
    public bool SignalLeakageStillSuspected { get; init; }
    public bool HardNegativeEvidenceStillInsufficient { get; init; }
    public bool RuntimePilotExecutionReadyForSeparateGate { get; init; }
    public IReadOnlyList<string> BlockedForRuntimePilotExecutionBy { get; init; } = Array.Empty<string>();
    public bool SyntheticLabelAuthority { get; init; }
    public bool HumanReviewAsGateAuthority { get; init; }
    public bool HumanFeedbackAutoIngest { get; init; }
    public bool AutoIngest { get; init; }
    public bool TrainingSetChanged { get; init; }
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
    public bool V8ScopedActivationPreserved { get; init; }
    public bool UpstreamSelfValidationPackGatePresent { get; init; }
    public bool UpstreamSelfValidationPackGatePassed { get; init; }
    public bool MainlineEvidencePresent { get; init; }
    public bool MainlineTrustRegistryPresent { get; init; }
    public string HardNegativeLabeledEvidenceSimulationPath { get; init; } = string.Empty;
    public string SignalLeakageAblationPath { get; init; } = string.Empty;
    public string CounterexampleReplayReportPath { get; init; } = string.Empty;
    public string EvidenceSufficiencyRecomputedPath { get; init; } = string.Empty;
    public string SelfOptimizationPlanUpdatedPath { get; init; } = string.Empty;
    public string Recommendation { get; init; } = string.Empty;
    public string NextAllowedPhase { get; init; } = string.Empty;
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class LearningEvidenceAccumulationPackOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
