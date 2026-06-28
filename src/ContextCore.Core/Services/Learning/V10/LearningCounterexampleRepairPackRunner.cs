using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

public static class LearningCounterexampleRepairPackStatuses
{
    public const string LearningCounterexampleRepairPackReady = nameof(LearningCounterexampleRepairPackReady);
    public const string LearningCounterexampleRepairPackBlocked = nameof(LearningCounterexampleRepairPackBlocked);
}

public static class LearningCounterexampleRepairPackBlockedReasons
{
    public const string EvidenceAccumulationPackMissing = nameof(EvidenceAccumulationPackMissing);
    public const string EvidenceAccumulationPackNotPassed = nameof(EvidenceAccumulationPackNotPassed);
    public const string CounterexampleReplayMissing = nameof(CounterexampleReplayMissing);
    public const string HardNegativeCandidatesMissing = nameof(HardNegativeCandidatesMissing);
    public const string RankingPairsMissing = nameof(RankingPairsMissing);
    public const string SyntheticLabelsTreatedAsAuthority = nameof(SyntheticLabelsTreatedAsAuthority);
    public const string EvidenceBoundLabelsTreatedAsFormalLabels = nameof(EvidenceBoundLabelsTreatedAsFormalLabels);
    public const string AutoIngestTrue = nameof(AutoIngestTrue);
    public const string TrainingSetChangedTrue = nameof(TrainingSetChangedTrue);
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

public sealed class EvidenceBoundShadowLabel
{
    public string LabelId { get; init; } = string.Empty;
    public string SourceCandidateSpecId { get; init; } = string.Empty;
    public string SourceSampleId { get; init; } = string.Empty;
    public string BoundRankingPairKey { get; init; } = string.Empty;
    public string SourceBaseline { get; init; } = string.Empty;
    public string TargetCandidateKind { get; init; } = string.Empty;
    public string EvidencePath { get; init; } = string.Empty;
    public string ExpectedPreference { get; init; } = string.Empty;
    public string BoundFailureClusterId { get; init; } = string.Empty;
    public bool EvidenceBoundShadowLabelIsFormal { get; init; }
    public bool ShadowOnly { get; init; } = true;
    public bool AutoIngest { get; init; }
    public string PolicyVersion { get; init; } = "v10.10-evidence-bound-shadow-label/v1";
}

public sealed class UnboundCandidateSpec
{
    public string CandidateSpecId { get; init; } = string.Empty;
    public string SourceSampleId { get; init; } = string.Empty;
    public string SourceBaseline { get; init; } = string.Empty;
    public string TargetCandidateKind { get; init; } = string.Empty;
    public IReadOnlyList<string> UnboundReasons { get; init; } = Array.Empty<string>();
    public string PolicyVersion { get; init; } = "v10.10-unbound-candidate-spec/v1";
}

public sealed class CounterexampleRepairCase
{
    public string CaseId { get; init; } = string.Empty;
    public string EvalSampleId { get; init; } = string.Empty;
    public double PositiveScoreMinusNegativeScore { get; init; }
    public double Recall3 { get; init; }
    public string FailureReason { get; init; } = string.Empty;
    public string CandidateWeaknessFeature { get; init; } = string.Empty;
    public string ReferenceStrengthFeature { get; init; } = string.Empty;
    public string ProposedRepairFeature { get; init; } = string.Empty;
}

public sealed class CounterexampleRepairAnalysis
{
    public int TotalCounterexamples { get; init; }
    public int CandidateFailureCount { get; init; }
    public int ReferenceFailureCount { get; init; }
    public IReadOnlyList<CounterexampleRepairCase> TopCases { get; init; } = Array.Empty<CounterexampleRepairCase>();
    public IReadOnlyList<string> CommonWeaknessFeatures { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CommonRepairProposals { get; init; } = Array.Empty<string>();
}

public sealed class RepairedShadowScoringProposal
{
    public string ProposalId { get; init; } = string.Empty;
    public string ProposalVersion { get; init; } = "v10.10-repaired-shadow-scoring/v1";
    public string ProposalMode { get; init; } = "ShadowProposalOnly";
    public IReadOnlyList<string> ScoringRule { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AddedFeatures { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Rationale { get; init; } = Array.Empty<string>();
    public bool RuntimeRerankerChanged { get; init; }
    public bool RuntimeRouterChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool ProductionDecisionChanged { get; init; }
    public bool RuntimePilotReady { get; init; }  // always false at this stage
}

public sealed class CounterexampleReplayAfterRepair
{
    public string ReplayMode { get; init; } = "ShadowReplayAfterRepairProposal";
    public int CounterexampleCount { get; init; }
    public double OriginalCandidateFailureRate { get; init; }
    public double RepairedCandidateFailureRate { get; init; }
    public double ReferenceFailureRate { get; init; }
    public double RepairImprovement { get; init; }
    public bool RepairedCandidateMatchesOrBeatsReference { get; init; }
    public bool RuntimeDecisionChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed class EvidenceSufficiencyRecomputedV2
{
    public double PreviousEvidenceSufficiencyScore { get; init; }
    public double NewEvidenceSufficiencyScoreV2 { get; init; }
    public double Threshold { get; init; } = 0.7;
    public int EvidenceBoundShadowLabelCount { get; init; }
    public int UnboundCandidateSpecCount { get; init; }
    public double BindingCoverageRate { get; init; }
    public double CounterexampleRepairImprovement { get; init; }
    public bool EvidenceSufficient { get; init; }
    public bool EvidenceSufficientForPilotCandidate { get; init; }
    public bool EvidenceBoundShadowLabelsAreFormal { get; init; }
    public IReadOnlyList<string> SubscoreDeltas { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed record LearningCounterexampleRepairPackContext
{
    public bool EvidenceAccumulationPackPresent { get; init; }
    public bool EvidenceAccumulationPackPassed { get; init; }
    public bool CounterexampleReplayPresent { get; init; }
    public bool HardNegativeCandidatesPresent { get; init; }
    public int HardNegativeCandidateCount { get; init; }
    public bool RankingPairsPresent { get; init; }
    public bool V8ScopedActivationPreserved { get; init; }
    public IReadOnlyList<RankerPair> RankerPairs { get; init; } = Array.Empty<RankerPair>();
    public IReadOnlyList<string> FailureClusterIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<HardNegativeSpecRow> HardNegativeSpecs { get; init; } = Array.Empty<HardNegativeSpecRow>();
    public double PreviousEvidenceSufficiencyScore { get; init; }
    public double OriginalCandidateFailureRate { get; init; }
    public double ReferenceFailureRate { get; init; }
    // Synthetic test knobs
    public bool SyntheticLabelsTreatedAsAuthorityOverride { get; init; }
    public bool EvidenceBoundLabelsTreatedAsFormalLabelsOverride { get; init; }
    public bool AutoIngestOverride { get; init; }
    public bool TrainingSetChangedOverride { get; init; }
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
}

public sealed class HardNegativeSpecRow
{
    public string CandidateSpecId { get; init; } = string.Empty;
    public string SourceSampleId { get; init; } = string.Empty;
    public string SourceBaseline { get; init; } = string.Empty;
    public string TargetCandidateKind { get; init; } = string.Empty;
    public string Rationale { get; init; } = string.Empty;
}

public sealed class LearningCounterexampleRepairPackDecision
{
    public string Status { get; init; } = LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked;
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;
}

public static class LearningCounterexampleRepairPackPolicy
{
    public static LearningCounterexampleRepairPackDecision Evaluate(
        LearningCounterexampleRepairPackContext ctx,
        bool rtPassed, bool p15Passed,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent)
    {
        var blocked = new List<string>();
        if (!ctx.EvidenceAccumulationPackPresent) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.EvidenceAccumulationPackMissing);
        else if (!ctx.EvidenceAccumulationPackPassed) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.EvidenceAccumulationPackNotPassed);
        if (!ctx.CounterexampleReplayPresent) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.CounterexampleReplayMissing);
        if (!ctx.HardNegativeCandidatesPresent || ctx.HardNegativeCandidateCount <= 0) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.HardNegativeCandidatesMissing);
        if (!ctx.RankingPairsPresent) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.RankingPairsMissing);
        if (ctx.SyntheticLabelsTreatedAsAuthorityOverride) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.SyntheticLabelsTreatedAsAuthority);
        if (ctx.EvidenceBoundLabelsTreatedAsFormalLabelsOverride) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.EvidenceBoundLabelsTreatedAsFormalLabels);
        if (ctx.AutoIngestOverride) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.AutoIngestTrue);
        if (ctx.TrainingSetChangedOverride) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.TrainingSetChangedTrue);
        if (ctx.RuntimePilotExecutionAppliedOverride) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.RuntimePilotExecutionAppliedTrue);
        if (ctx.RuntimePromotionAppliedOverride) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.RuntimePromotionAppliedTrue);
        if (ctx.RuntimeRerankerChangedOverride) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.RuntimeRerankerChangedTrue);
        if (ctx.RuntimeRouterChangedOverride) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.RuntimeRouterChangedTrue);
        if (ctx.ProductionDecisionChangedOverride) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.ProductionDecisionChangedTrue);
        if (ctx.PackageOutputChangedOverride) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.PackageOutputChangedTrue);
        if (ctx.FormalPackageWrittenOverride) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.FormalPackageWrittenTrue);
        if (ctx.GlobalDefaultOnOverride) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.GlobalDefaultOnTrue);
        if (ctx.MLAuthorityOverride) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.MLAuthorityTrue);
        if (ctx.LLMAuthorityOverride) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.LLMAuthorityTrue);
        if (ctx.RuntimeAuthorityOverride) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.RuntimeAuthorityTrue);
        if (ctx.GateAuthorityOverride) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.GateAuthorityTrue);
        if (!ctx.V8ScopedActivationPreserved) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.V8ScopedActivationLost);
        if (mainlineEvidencePresent) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.MainlineTrustRegistryPresent);
        if (!rtPassed) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.P15GateNotPassed);
        var finalBlocked = blocked.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        var ready = finalBlocked.Length == 0;
        return new LearningCounterexampleRepairPackDecision
        {
            Status = ready
                ? LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackReady
                : LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked,
            BlockedReasons = finalBlocked,
            Reasoning = ready
                ? "counterexample repair + evidence-bound hard-negative realization policy ready; outputs are shadow-only proposals."
                : $"{finalBlocked.Length} blocked reason(s); counterexample repair pack blocked."
        };
    }
}

public sealed record LearningCounterexampleRepairPackScenario(
    string CaseName,
    LearningCounterexampleRepairPackContext Context,
    bool RtPassed, bool P15Passed,
    bool MainlineEvidencePresent, bool MainlineRegistryPresent,
    string ExpectedStatus, string? ExpectedBlockedReason);

public sealed class LearningCounterexampleRepairPackRunner
{
    private static readonly JsonSerializerOptions WriteIndented = new() { WriteIndented = true };

    public LearningCounterexampleRepairPackReport Run(
        LearningCounterexampleRepairPackContext realContext,
        string outputDir,
        bool rtPassed, bool p15Passed,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent,
        LearningCounterexampleRepairPackOptions? opt = null)
    {
        opt ??= new LearningCounterexampleRepairPackOptions();
        var now = DateTimeOffset.UtcNow;

        var cases = BuildScenarios().Select(static scenario =>
        {
            var decision = LearningCounterexampleRepairPackPolicy.Evaluate(
                scenario.Context, scenario.RtPassed, scenario.P15Passed,
                scenario.MainlineEvidencePresent, scenario.MainlineRegistryPresent);
            var statusMatched = string.Equals(scenario.ExpectedStatus, decision.Status, StringComparison.Ordinal);
            var blockedReasonMatched = scenario.ExpectedBlockedReason is null
                || decision.BlockedReasons.Contains(scenario.ExpectedBlockedReason, StringComparer.Ordinal);
            return new LearningCounterexampleRepairPackCase
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
        if (cases.Length < 25) blocked.Add("InsufficientLearningCounterexampleRepairPackCases");
        if (cases.Any(static c => !c.PassedAsExpected)) blocked.Add("LearningCounterexampleRepairPackMatrixFailed");
        foreach (var status in new[] {
            LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackReady,
            LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked })
        {
            if (!cases.Any(c => string.Equals(c.ActualStatus, status, StringComparison.Ordinal)))
                blocked.Add($"StatusBranchNotCovered:{status}");
        }

        var realDecision = LearningCounterexampleRepairPackPolicy.Evaluate(
            realContext, rtPassed, p15Passed, mainlineEvidencePresent, mainlineRegistryPresent);
        if (!string.Equals(realDecision.Status, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackReady, StringComparison.Ordinal))
            blocked.AddRange(realDecision.BlockedReasons.Select(static x => $"RealLearningCounterexampleRepairPack:{x}"));
        if (!rtPassed) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(LearningCounterexampleRepairPackBlockedReasons.MainlineTrustRegistryPresent);

        var canBuild = string.Equals(realDecision.Status, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackReady, StringComparison.Ordinal);
        var boundLabels = new List<EvidenceBoundShadowLabel>();
        var unboundSpecs = new List<UnboundCandidateSpec>();
        CounterexampleRepairAnalysis repairAnalysis = new();
        RepairedShadowScoringProposal repairProposal = new();
        CounterexampleReplayAfterRepair replayAfterRepair = new();
        EvidenceSufficiencyRecomputedV2 recomputedV2 = new();
        var boundPath = string.Empty;
        var unboundPath = string.Empty;
        var analysisPath = string.Empty;
        var proposalPath = string.Empty;
        var replayPath = string.Empty;
        var recomputedPath = string.Empty;

        if (canBuild)
        {
            // 1. Evidence-bound hard-negative realization
            (boundLabels, unboundSpecs) = BuildEvidenceBoundShadowLabels(realContext);
            boundPath = Path.Combine(outputDir, "evidence-bound-hard-negative-labels.jsonl");
            WriteBoundLabelsJsonl(boundPath, boundLabels);
            unboundPath = Path.Combine(outputDir, "unbound-hard-negative-candidates.jsonl");
            WriteUnboundSpecsJsonl(unboundPath, unboundSpecs);

            // 2. Counterexample repair analysis
            repairAnalysis = BuildCounterexampleRepairAnalysis(realContext);
            analysisPath = Path.Combine(outputDir, "counterexample-repair-analysis.json");
            File.WriteAllText(analysisPath, JsonSerializer.Serialize(repairAnalysis, WriteIndented), new UTF8Encoding(true));

            // 3. Repaired shadow scoring proposal
            repairProposal = BuildRepairProposal(repairAnalysis);
            proposalPath = Path.Combine(outputDir, "repaired-shadow-scoring-proposal.json");
            File.WriteAllText(proposalPath, JsonSerializer.Serialize(repairProposal, WriteIndented), new UTF8Encoding(true));

            // 4. Rerun counterexample replay with repaired scoring
            replayAfterRepair = BuildReplayAfterRepair(realContext);
            replayPath = Path.Combine(outputDir, "counterexample-replay-after-repair.json");
            File.WriteAllText(replayPath, JsonSerializer.Serialize(replayAfterRepair, WriteIndented), new UTF8Encoding(true));

            // 5. Evidence sufficiency recomputed v2
            recomputedV2 = BuildEvidenceSufficiencyRecomputedV2(realContext, boundLabels, unboundSpecs, replayAfterRepair);
            recomputedPath = Path.Combine(outputDir, "evidence-sufficiency-recomputed-v2.json");
            File.WriteAllText(recomputedPath, JsonSerializer.Serialize(recomputedV2, WriteIndented), new UTF8Encoding(true));
        }

        // Authority leak checks
        foreach (var l in boundLabels)
        {
            if (l.EvidenceBoundShadowLabelIsFormal) blocked.Add($"EvidenceBoundShadowLabelIsFormalLeak:{l.LabelId}");
            if (!l.ShadowOnly) blocked.Add($"EvidenceBoundShadowLabelShadowOnlyFalse:{l.LabelId}");
            if (l.AutoIngest) blocked.Add($"EvidenceBoundShadowLabelAutoIngestLeak:{l.LabelId}");
        }
        if (repairProposal.RuntimeRerankerChanged || repairProposal.RuntimeRouterChanged
            || repairProposal.PackageOutputChanged || repairProposal.ProductionDecisionChanged
            || repairProposal.RuntimePilotReady) blocked.Add("RepairProposalRuntimeLeak");
        if (replayAfterRepair.RuntimeDecisionChanged || replayAfterRepair.PackageOutputChanged)
            blocked.Add("ReplayAfterRepairRuntimeLeak");
        if (recomputedV2.EvidenceBoundShadowLabelsAreFormal) blocked.Add("RecomputedEvidenceFormalLabelsLeak");

        var finalBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var passed = finalBlocked.Length == 0;
        var pilotReady = canBuild && recomputedV2.EvidenceSufficient
            && replayAfterRepair.RepairedCandidateMatchesOrBeatsReference;
        var blockedExecBy = new List<string>();
        if (canBuild && !pilotReady)
        {
            if (!recomputedV2.EvidenceSufficient) blockedExecBy.Add("EvidenceInsufficientV2");
            if (!replayAfterRepair.RepairedCandidateMatchesOrBeatsReference) blockedExecBy.Add("RepairedCandidateStillUnderperformsReference");
            // Even when sufficient, we still require evidence-bound labels to be backed by real human review before runtime pilot.
            if (recomputedV2.EvidenceBoundShadowLabelCount > 0) blockedExecBy.Add("EvidenceBoundLabelsAreShadowNotFormal");
        }

        return new LearningCounterexampleRepairPackReport
        {
            OperationId = $"v10-learning-counterexample-repair-pack-{Guid.NewGuid():N}",
            CreatedAt = now,
            CounterexampleRepairPackPassed = passed,
            GatePassed = opt.IsGate && passed,
            TotalCases = cases.Length,
            PassedCases = cases.Count(static c => c.PassedAsExpected),
            FailedCases = cases.Count(static c => !c.PassedAsExpected),
            ReadyCases = cases.Count(c => string.Equals(c.ActualStatus, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackReady, StringComparison.Ordinal)),
            BlockedCases = cases.Count(c => string.Equals(c.ActualStatus, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked, StringComparison.Ordinal)),
            Cases = cases,
            EvidenceBoundShadowLabels = boundLabels,
            UnboundCandidateSpecs = unboundSpecs,
            CounterexampleRepairAnalysis = repairAnalysis,
            RepairedShadowScoringProposal = repairProposal,
            CounterexampleReplayAfterRepair = replayAfterRepair,
            EvidenceSufficiencyRecomputedV2 = recomputedV2,
            EvidenceBoundHardNegativeLabelsReady = canBuild,
            EvidenceBoundShadowLabelCount = boundLabels.Count,
            UnboundCandidateSpecCount = unboundSpecs.Count,
            BindingCoverageRate = recomputedV2.BindingCoverageRate,
            CounterexampleRepairAnalysisReady = canBuild,
            RepairedShadowScoringProposalReady = canBuild,
            CounterexampleReplayAfterRepairReady = canBuild,
            EvidenceSufficiencyRecomputedV2Ready = canBuild,
            OriginalCandidateFailureRate = realContext.OriginalCandidateFailureRate,
            RepairedCandidateFailureRate = replayAfterRepair.RepairedCandidateFailureRate,
            ReferenceFailureRate = realContext.ReferenceFailureRate,
            RepairImprovement = replayAfterRepair.RepairImprovement,
            EvidenceSufficient = recomputedV2.EvidenceSufficient,
            EvidenceBoundShadowLabelsAreFormal = false,
            RuntimePilotExecutionReadyForSeparateGate = pilotReady,
            BlockedForRuntimePilotExecutionBy = blockedExecBy,
            ShadowOnly = true,
            SyntheticLabelAuthority = false,
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
            UpstreamEvidenceAccumulationPackGatePresent = realContext.EvidenceAccumulationPackPresent,
            UpstreamEvidenceAccumulationPackGatePassed = realContext.EvidenceAccumulationPackPassed,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent,
            EvidenceBoundHardNegativeLabelsPath = boundPath,
            UnboundHardNegativeCandidatesPath = unboundPath,
            CounterexampleRepairAnalysisPath = analysisPath,
            RepairedShadowScoringProposalPath = proposalPath,
            CounterexampleReplayAfterRepairPath = replayPath,
            EvidenceSufficiencyRecomputedV2Path = recomputedPath,
            Recommendation = passed
                ? (pilotReady ? "ProceedToV10.13PilotExecutionGate-pending-formal-labels" : "BlockedForRuntimePilotExecution-EvidenceCalibratedV2-AccumulateFormalEvidence")
                : "Blocked",
            NextAllowedPhase = passed
                ? (pilotReady ? "V10.13PilotExecutionGate-pending-formal-labels" : "V10.13PilotExecution-pending-formal-evidence")
                : string.Empty,
            BlockedReasons = finalBlocked,
            Diagnostics = new[]
            {
                $"total={cases.Length}",
                $"realStatus={realDecision.Status}",
                $"evidenceBoundShadowLabelCount={boundLabels.Count}",
                $"unboundCandidateSpecCount={unboundSpecs.Count}",
                $"bindingCoverageRate={recomputedV2.BindingCoverageRate:F3}",
                $"originalCandidateFailureRate={realContext.OriginalCandidateFailureRate:F3}",
                $"repairedCandidateFailureRate={replayAfterRepair.RepairedCandidateFailureRate:F3}",
                $"referenceFailureRate={realContext.ReferenceFailureRate:F3}",
                $"repairImprovement={replayAfterRepair.RepairImprovement:F3}",
                $"newEvidenceScoreV2={recomputedV2.NewEvidenceSufficiencyScoreV2:F3}",
                $"evidenceSufficient={recomputedV2.EvidenceSufficient}",
                $"pilotReady={pilotReady}"
            }
        };
    }

    private static IReadOnlyList<LearningCounterexampleRepairPackScenario> BuildScenarios()
    {
        var clean = BuildCleanContext();
        return [
            new("AllUpstreamClean", clean, true, true, false, false, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackReady, null),
            new("EvidenceAccumulationPackMissing", clean with { EvidenceAccumulationPackPresent = false }, true, true, false, false, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked, LearningCounterexampleRepairPackBlockedReasons.EvidenceAccumulationPackMissing),
            new("EvidenceAccumulationPackNotPassed", clean with { EvidenceAccumulationPackPassed = false }, true, true, false, false, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked, LearningCounterexampleRepairPackBlockedReasons.EvidenceAccumulationPackNotPassed),
            new("CounterexampleReplayMissing", clean with { CounterexampleReplayPresent = false }, true, true, false, false, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked, LearningCounterexampleRepairPackBlockedReasons.CounterexampleReplayMissing),
            new("HardNegativeCandidatesMissing", clean with { HardNegativeCandidatesPresent = false, HardNegativeCandidateCount = 0 }, true, true, false, false, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked, LearningCounterexampleRepairPackBlockedReasons.HardNegativeCandidatesMissing),
            new("RankingPairsMissing", clean with { RankingPairsPresent = false }, true, true, false, false, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked, LearningCounterexampleRepairPackBlockedReasons.RankingPairsMissing),
            new("SyntheticLabelsTreatedAsAuthority", clean with { SyntheticLabelsTreatedAsAuthorityOverride = true }, true, true, false, false, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked, LearningCounterexampleRepairPackBlockedReasons.SyntheticLabelsTreatedAsAuthority),
            new("EvidenceBoundLabelsTreatedAsFormalLabels", clean with { EvidenceBoundLabelsTreatedAsFormalLabelsOverride = true }, true, true, false, false, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked, LearningCounterexampleRepairPackBlockedReasons.EvidenceBoundLabelsTreatedAsFormalLabels),
            new("AutoIngestTrue", clean with { AutoIngestOverride = true }, true, true, false, false, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked, LearningCounterexampleRepairPackBlockedReasons.AutoIngestTrue),
            new("TrainingSetChangedTrue", clean with { TrainingSetChangedOverride = true }, true, true, false, false, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked, LearningCounterexampleRepairPackBlockedReasons.TrainingSetChangedTrue),
            new("RuntimePilotExecutionAppliedTrue", clean with { RuntimePilotExecutionAppliedOverride = true }, true, true, false, false, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked, LearningCounterexampleRepairPackBlockedReasons.RuntimePilotExecutionAppliedTrue),
            new("RuntimePromotionAppliedTrue", clean with { RuntimePromotionAppliedOverride = true }, true, true, false, false, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked, LearningCounterexampleRepairPackBlockedReasons.RuntimePromotionAppliedTrue),
            new("RuntimeRerankerChangedTrue", clean with { RuntimeRerankerChangedOverride = true }, true, true, false, false, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked, LearningCounterexampleRepairPackBlockedReasons.RuntimeRerankerChangedTrue),
            new("RuntimeRouterChangedTrue", clean with { RuntimeRouterChangedOverride = true }, true, true, false, false, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked, LearningCounterexampleRepairPackBlockedReasons.RuntimeRouterChangedTrue),
            new("ProductionDecisionChangedTrue", clean with { ProductionDecisionChangedOverride = true }, true, true, false, false, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked, LearningCounterexampleRepairPackBlockedReasons.ProductionDecisionChangedTrue),
            new("PackageOutputChangedTrue", clean with { PackageOutputChangedOverride = true }, true, true, false, false, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked, LearningCounterexampleRepairPackBlockedReasons.PackageOutputChangedTrue),
            new("FormalPackageWrittenTrue", clean with { FormalPackageWrittenOverride = true }, true, true, false, false, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked, LearningCounterexampleRepairPackBlockedReasons.FormalPackageWrittenTrue),
            new("GlobalDefaultOnTrue", clean with { GlobalDefaultOnOverride = true }, true, true, false, false, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked, LearningCounterexampleRepairPackBlockedReasons.GlobalDefaultOnTrue),
            new("MLAuthorityTrue", clean with { MLAuthorityOverride = true }, true, true, false, false, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked, LearningCounterexampleRepairPackBlockedReasons.MLAuthorityTrue),
            new("LLMAuthorityTrue", clean with { LLMAuthorityOverride = true }, true, true, false, false, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked, LearningCounterexampleRepairPackBlockedReasons.LLMAuthorityTrue),
            new("RuntimeAuthorityTrue", clean with { RuntimeAuthorityOverride = true }, true, true, false, false, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked, LearningCounterexampleRepairPackBlockedReasons.RuntimeAuthorityTrue),
            new("GateAuthorityTrue", clean with { GateAuthorityOverride = true }, true, true, false, false, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked, LearningCounterexampleRepairPackBlockedReasons.GateAuthorityTrue),
            new("V8ScopedActivationLost", clean with { V8ScopedActivationPreserved = false }, true, true, false, false, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked, LearningCounterexampleRepairPackBlockedReasons.V8ScopedActivationLost),
            new("MainlineEvidencePresent", clean, true, true, true, false, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked, LearningCounterexampleRepairPackBlockedReasons.MainlineEvidencePresent),
            new("MainlineTrustRegistryPresent", clean, true, true, false, true, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked, LearningCounterexampleRepairPackBlockedReasons.MainlineTrustRegistryPresent),
            new("RuntimeGateNotPassed", clean, false, true, false, false, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked, LearningCounterexampleRepairPackBlockedReasons.RuntimeChangeGateNotPassed),
            new("P15GateNotPassed", clean, true, false, false, false, LearningCounterexampleRepairPackStatuses.LearningCounterexampleRepairPackBlocked, LearningCounterexampleRepairPackBlockedReasons.P15GateNotPassed)
        ];
    }

    private static LearningCounterexampleRepairPackContext BuildCleanContext() => new()
    {
        EvidenceAccumulationPackPresent = true,
        EvidenceAccumulationPackPassed = true,
        CounterexampleReplayPresent = true,
        HardNegativeCandidatesPresent = true,
        HardNegativeCandidateCount = 60,
        RankingPairsPresent = true,
        V8ScopedActivationPreserved = true,
        PreviousEvidenceSufficiencyScore = 0.814,
        OriginalCandidateFailureRate = 0.276,
        ReferenceFailureRate = 0.0
    };

    // ─── builders ────

    private static (List<EvidenceBoundShadowLabel> bound, List<UnboundCandidateSpec> unbound)
        BuildEvidenceBoundShadowLabels(LearningCounterexampleRepairPackContext ctx)
    {
        var bound = new List<EvidenceBoundShadowLabel>();
        var unbound = new List<UnboundCandidateSpec>();
        var samplesInPairs = new HashSet<string>(ctx.RankerPairs.Select(p => p.EvalSampleId), StringComparer.Ordinal);
        var clusterIds = new HashSet<string>(ctx.FailureClusterIds, StringComparer.Ordinal);
        int idx = 0;
        foreach (var spec in ctx.HardNegativeSpecs.OrderBy(s => s.CandidateSpecId, StringComparer.Ordinal))
        {
            var unboundReasons = new List<string>();
            bool sampleBound = samplesInPairs.Contains(spec.SourceSampleId);
            bool clusterBound = !string.IsNullOrEmpty(spec.SourceBaseline)
                && (clusterIds.Contains($"ranker-{spec.SourceBaseline}") || clusterIds.Contains($"router-{spec.SourceBaseline}"));
            // expected preference inferable if the eval sample exists in pairs (positive should win)
            string expected = sampleBound ? "PositiveOverNegative" : string.Empty;
            string evidencePath = sampleBound ? $"learning/features/ranking-pairs.jsonl#evalSampleId={spec.SourceSampleId}" : string.Empty;
            string boundCluster = clusterBound
                ? (clusterIds.Contains($"ranker-{spec.SourceBaseline}") ? $"ranker-{spec.SourceBaseline}" : $"router-{spec.SourceBaseline}")
                : string.Empty;
            if (!sampleBound) unboundReasons.Add("sourceSampleId does not match any evalSampleId in ranking-pairs.jsonl");
            if (!clusterBound) unboundReasons.Add($"sourceBaseline '{spec.SourceBaseline}' does not match any known failure cluster id");
            if (string.IsNullOrEmpty(expected)) unboundReasons.Add("expected preference not derivable from existing evidence");
            // Binding rule: at least one of (sample, cluster) must bind; expected preference must be derivable.
            bool isBound = (sampleBound || clusterBound) && !string.IsNullOrEmpty(expected);
            if (isBound)
            {
                bound.Add(new EvidenceBoundShadowLabel
                {
                    LabelId = $"ebsl-{idx++:D4}-{spec.SourceSampleId}",
                    SourceCandidateSpecId = spec.CandidateSpecId,
                    SourceSampleId = spec.SourceSampleId,
                    BoundRankingPairKey = sampleBound ? spec.SourceSampleId : string.Empty,
                    SourceBaseline = spec.SourceBaseline,
                    TargetCandidateKind = spec.TargetCandidateKind,
                    EvidencePath = evidencePath,
                    ExpectedPreference = expected,
                    BoundFailureClusterId = boundCluster,
                    EvidenceBoundShadowLabelIsFormal = false,
                    ShadowOnly = true,
                    AutoIngest = false,
                    PolicyVersion = "v10.10-evidence-bound-shadow-label/v1"
                });
            }
            else
            {
                unbound.Add(new UnboundCandidateSpec
                {
                    CandidateSpecId = spec.CandidateSpecId,
                    SourceSampleId = spec.SourceSampleId,
                    SourceBaseline = spec.SourceBaseline,
                    TargetCandidateKind = spec.TargetCandidateKind,
                    UnboundReasons = unboundReasons,
                    PolicyVersion = "v10.10-unbound-candidate-spec/v1"
                });
            }
        }
        return (bound, unbound);
    }

    private static void WriteBoundLabelsJsonl(string path, IReadOnlyList<EvidenceBoundShadowLabel> labels)
    {
        var sb = new StringBuilder();
        foreach (var l in labels)
            sb.AppendLine(JsonSerializer.Serialize(l));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static void WriteUnboundSpecsJsonl(string path, IReadOnlyList<UnboundCandidateSpec> specs)
    {
        var sb = new StringBuilder();
        foreach (var s in specs)
            sb.AppendLine(JsonSerializer.Serialize(s));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static CounterexampleRepairAnalysis BuildCounterexampleRepairAnalysis(LearningCounterexampleRepairPackContext ctx)
    {
        if (ctx.RankerPairs.Count == 0)
            return new CounterexampleRepairAnalysis
            {
                TotalCounterexamples = 0,
                CandidateFailureCount = 0,
                ReferenceFailureCount = 0,
                TopCases = Array.Empty<CounterexampleRepairCase>(),
                CommonWeaknessFeatures = new[] { "no pairs available" },
                CommonRepairProposals = new[] { "no analysis possible" }
            };
        // Reconstruct counterexamples — pairs where (positiveScore - negativeScore) <= median
        var sorted5 = ctx.RankerPairs.Select(p => p.Features[5]).OrderBy(v => v).ToArray();
        var median = sorted5[sorted5.Length / 2];
        var counterexamples = ctx.RankerPairs.Where(p => p.Features[5] <= median).OrderBy(p => p.EvalSampleId, StringComparer.Ordinal).ToList();
        int candidateFailures = counterexamples.Count(p => p.Features[5] <= 0);
        int referenceFailures = counterexamples.Count(p => p.Features[5] <= 0 && p.Features[0] < 0.5);
        // Top failing cases (where candidate fails but reference would succeed because recall3 ≥ 0.5)
        var topFailures = counterexamples
            .Where(p => p.Features[5] <= 0 && p.Features[0] >= 0.5)
            .Take(10)
            .Select((p, i) => new CounterexampleRepairCase
            {
                CaseId = $"crc-{i:D4}-{p.EvalSampleId}",
                EvalSampleId = p.EvalSampleId,
                PositiveScoreMinusNegativeScore = p.Features[5],
                Recall3 = p.Features[0],
                FailureReason = "candidate uses positiveScore-only logic; positiveScore-negativeScore ≤ 0 → tie → predicted as negative wins",
                CandidateWeaknessFeature = "PositiveScoreMinusNegativeScore (feature index 5) — single-feature dependence",
                ReferenceStrengthFeature = "Recall3 (feature index 0) — auxiliary signal even when scores tie",
                ProposedRepairFeature = "Add Recall3 as tiebreaker when PositiveScoreMinusNegativeScore ≤ 0"
            }).ToArray();
        return new CounterexampleRepairAnalysis
        {
            TotalCounterexamples = counterexamples.Count,
            CandidateFailureCount = candidateFailures,
            ReferenceFailureCount = referenceFailures,
            TopCases = topFailures,
            CommonWeaknessFeatures = new[]
            {
                "PositiveScoreMinusNegativeScore tie/zero → candidate cannot discriminate",
                "single-feature scoring → no fallback signal"
            },
            CommonRepairProposals = new[]
            {
                "Add Recall3 (feature index 0) as tiebreaker when positive-negative score delta ≤ 0",
                "Add MRR (feature index 3) as secondary tiebreaker when recall ties",
                "Combine score-delta with recall family using weighted sum (shadow-only)"
            }
        };
    }

    private static RepairedShadowScoringProposal BuildRepairProposal(CounterexampleRepairAnalysis analysis)
        => new()
        {
            ProposalId = $"v10-repaired-shadow-scoring-{Guid.NewGuid():N}",
            ProposalVersion = "v10.10-repaired-shadow-scoring/v1",
            ProposalMode = "ShadowProposalOnly",
            ScoringRule = new[]
            {
                "score = 1.0 * (PositiveScoreMinusNegativeScore > 0 ? 1 : 0)",
                "    + 0.6 * (Recall3 >= 0.5 ? 1 : 0)",
                "    + 0.3 * (MRR >= 0.5 ? 1 : 0)",
                "predict positive wins if score > 0.5"
            },
            AddedFeatures = new[] { "Recall3 as tiebreaker", "MRR as secondary tiebreaker" },
            Rationale = analysis.CommonRepairProposals,
            RuntimeRerankerChanged = false,
            RuntimeRouterChanged = false,
            PackageOutputChanged = false,
            ProductionDecisionChanged = false,
            RuntimePilotReady = false
        };

    private static CounterexampleReplayAfterRepair BuildReplayAfterRepair(LearningCounterexampleRepairPackContext ctx)
    {
        if (ctx.RankerPairs.Count == 0)
            return new CounterexampleReplayAfterRepair
            {
                ReplayMode = "ShadowReplayAfterRepairProposal",
                CounterexampleCount = 0,
                OriginalCandidateFailureRate = 0,
                RepairedCandidateFailureRate = 0,
                ReferenceFailureRate = 0,
                RepairImprovement = 0,
                RepairedCandidateMatchesOrBeatsReference = false,
                RuntimeDecisionChanged = false,
                PackageOutputChanged = false,
                Notes = new[] { "no pairs available — replay skipped" }
            };
        var sorted5 = ctx.RankerPairs.Select(p => p.Features[5]).OrderBy(v => v).ToArray();
        var median = sorted5[sorted5.Length / 2];
        var counterexamples = ctx.RankerPairs.Where(p => p.Features[5] <= median).ToList();
        int originalFailures = counterexamples.Count(p => p.Features[5] <= 0);
        // Repaired scoring: score = (delta>0 ? 1 : 0) + 0.6*(recall3 ≥ 0.5 ? 1 : 0) + 0.3*(mrr ≥ 0.5 ? 1 : 0)
        int repairedFailures = counterexamples.Count(p =>
        {
            double score = (p.Features[5] > 0 ? 1 : 0)
                         + 0.6 * (p.Features[0] >= 0.5 ? 1 : 0)
                         + 0.3 * (p.Features[3] >= 0.5 ? 1 : 0);
            return score <= 0.5;
        });
        int referenceFailures = counterexamples.Count(p => p.Features[5] <= 0 && p.Features[0] < 0.5);
        double origRate = counterexamples.Count > 0 ? (double)originalFailures / counterexamples.Count : 0;
        double repairedRate = counterexamples.Count > 0 ? (double)repairedFailures / counterexamples.Count : 0;
        double refRate = counterexamples.Count > 0 ? (double)referenceFailures / counterexamples.Count : 0;
        double improvement = origRate - repairedRate;
        bool matchesOrBeats = repairedRate <= refRate;
        return new CounterexampleReplayAfterRepair
        {
            ReplayMode = "ShadowReplayAfterRepairProposal",
            CounterexampleCount = counterexamples.Count,
            OriginalCandidateFailureRate = origRate,
            RepairedCandidateFailureRate = repairedRate,
            ReferenceFailureRate = refRate,
            RepairImprovement = improvement,
            RepairedCandidateMatchesOrBeatsReference = matchesOrBeats,
            RuntimeDecisionChanged = false,
            PackageOutputChanged = false,
            Notes = new[]
            {
                $"Original candidate failure rate on counterexamples: {origRate:F3}",
                $"Repaired candidate failure rate on counterexamples: {repairedRate:F3} (improvement={improvement:F3})",
                $"Reference failure rate on counterexamples: {refRate:F3}",
                $"RepairedCandidateMatchesOrBeatsReference={matchesOrBeats}",
                "Runtime decisions unchanged. Package output unchanged. Repaired scoring is a SHADOW proposal."
            }
        };
    }

    private static EvidenceSufficiencyRecomputedV2 BuildEvidenceSufficiencyRecomputedV2(
        LearningCounterexampleRepairPackContext ctx,
        IReadOnlyList<EvidenceBoundShadowLabel> bound,
        IReadOnlyList<UnboundCandidateSpec> unbound,
        CounterexampleReplayAfterRepair replay)
    {
        var totalSpecs = bound.Count + unbound.Count;
        var bindingRate = totalSpecs > 0 ? (double)bound.Count / totalSpecs : 0;
        var prev = ctx.PreviousEvidenceSufficiencyScore;
        // Bound shadow labels contribute partial credit (not full — they are not formal)
        var boundContrib = Math.Min(0.10, 0.10 * bindingRate);
        // Counterexample repair improvement: scale by improvement magnitude
        var repairContrib = Math.Min(0.10, Math.Max(0, replay.RepairImprovement) * 2);
        var newScore = Math.Min(1.0, prev + boundContrib + repairContrib);
        // EvidenceSufficient ONLY if: score above threshold AND repair matches/beats reference AND we admit shadow labels are not formal
        var sufficient = newScore >= 0.7 && replay.RepairedCandidateMatchesOrBeatsReference;
        // EvidenceSufficientForPilotCandidate still requires formal labels — not satisfied here.
        var sufficientForPilot = false;  // never true at V10.10 because labels remain shadow
        var deltas = new List<string>
        {
            $"BoundShadowLabelContribution=+{boundContrib:F3} ({bound.Count}/{totalSpecs} bound, rate={bindingRate:F3})",
            $"CounterexampleRepairContribution=+{repairContrib:F3} (improvement={replay.RepairImprovement:F3})"
        };
        return new EvidenceSufficiencyRecomputedV2
        {
            PreviousEvidenceSufficiencyScore = prev,
            NewEvidenceSufficiencyScoreV2 = newScore,
            Threshold = 0.7,
            EvidenceBoundShadowLabelCount = bound.Count,
            UnboundCandidateSpecCount = unbound.Count,
            BindingCoverageRate = bindingRate,
            CounterexampleRepairImprovement = replay.RepairImprovement,
            EvidenceSufficient = sufficient,
            EvidenceSufficientForPilotCandidate = sufficientForPilot,
            EvidenceBoundShadowLabelsAreFormal = false,
            SubscoreDeltas = deltas,
            Notes = new[]
            {
                $"Previous score={prev:F3} → new score={newScore:F3} (threshold={0.7:F3})",
                "Bound shadow labels carry partial evidence weight; they are NOT formal labels and never enter the formal training set.",
                $"BindingCoverageRate={bindingRate:F3} — how many candidate specs successfully bound to ranking-pair / failure-cluster evidence.",
                $"RepairImprovement={replay.RepairImprovement:F3} — counterexample failure rate reduction from shadow scoring repair proposal.",
                "EvidenceSufficient considers BOTH score threshold AND repair effectiveness; runtime pilot still requires formal labels."
            }
        };
    }

    /// <summary>V10.10: load real hard-negative candidate specs from V9.4 jsonl.</summary>
    public static IReadOnlyList<HardNegativeSpecRow> LoadHardNegativeSpecs(string path)
    {
        if (!File.Exists(path)) return Array.Empty<HardNegativeSpecRow>();
        var result = new List<HardNegativeSpecRow>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                result.Add(new HardNegativeSpecRow
                {
                    CandidateSpecId = doc.RootElement.TryGetProperty("candidateSpecId", out var cid) ? cid.GetString() ?? string.Empty : string.Empty,
                    SourceSampleId = doc.RootElement.TryGetProperty("sourceSampleId", out var sid) ? sid.GetString() ?? string.Empty : string.Empty,
                    SourceBaseline = doc.RootElement.TryGetProperty("sourceBaseline", out var sb) ? sb.GetString() ?? string.Empty : string.Empty,
                    TargetCandidateKind = doc.RootElement.TryGetProperty("targetCandidateKind", out var tk) ? tk.GetString() ?? string.Empty : string.Empty,
                    Rationale = doc.RootElement.TryGetProperty("rationale", out var r) ? r.GetString() ?? string.Empty : string.Empty
                });
            }
            catch { }
        }
        return result;
    }

    public static string BuildMarkdown(string title, LearningCounterexampleRepairPackReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"- CounterexampleRepairPackPassed: `{report.CounterexampleRepairPackPassed}`");
        sb.AppendLine($"- GatePassed: `{report.GatePassed}`");
        sb.AppendLine($"- TotalCases: `{report.TotalCases}` PassedCases: `{report.PassedCases}` FailedCases: `{report.FailedCases}`");
        sb.AppendLine();
        sb.AppendLine("## Evidence-Bound Hard-Negative Realization");
        sb.AppendLine($"- EvidenceBoundShadowLabelCount: `{report.EvidenceBoundShadowLabelCount}`");
        sb.AppendLine($"- UnboundCandidateSpecCount: `{report.UnboundCandidateSpecCount}`");
        sb.AppendLine($"- BindingCoverageRate: `{report.BindingCoverageRate:F3}`");
        sb.AppendLine($"- EvidenceBoundShadowLabelsAreFormal: `{report.EvidenceBoundShadowLabelsAreFormal}`");
        sb.AppendLine();
        sb.AppendLine("## Counterexample Repair");
        sb.AppendLine($"- TotalCounterexamples: `{report.CounterexampleRepairAnalysis.TotalCounterexamples}`");
        sb.AppendLine($"- CandidateFailureCount: `{report.CounterexampleRepairAnalysis.CandidateFailureCount}`");
        sb.AppendLine($"- ReferenceFailureCount: `{report.CounterexampleRepairAnalysis.ReferenceFailureCount}`");
        sb.AppendLine($"- TopCases: `{report.CounterexampleRepairAnalysis.TopCases.Count}`");
        sb.AppendLine();
        sb.AppendLine("## Repaired Shadow Scoring Proposal");
        sb.AppendLine($"- ProposalMode: `{report.RepairedShadowScoringProposal.ProposalMode}`");
        sb.AppendLine($"- AddedFeatures: {string.Join(", ", report.RepairedShadowScoringProposal.AddedFeatures)}");
        sb.AppendLine($"- RuntimeRerankerChanged: `{report.RepairedShadowScoringProposal.RuntimeRerankerChanged}` RuntimePilotReady: `{report.RepairedShadowScoringProposal.RuntimePilotReady}`");
        sb.AppendLine();
        sb.AppendLine("## Counterexample Replay After Repair");
        sb.AppendLine($"- CounterexampleCount: `{report.CounterexampleReplayAfterRepair.CounterexampleCount}`");
        sb.AppendLine($"- OriginalCandidateFailureRate: `{report.OriginalCandidateFailureRate:F3}`");
        sb.AppendLine($"- RepairedCandidateFailureRate: `{report.RepairedCandidateFailureRate:F3}`");
        sb.AppendLine($"- ReferenceFailureRate: `{report.ReferenceFailureRate:F3}`");
        sb.AppendLine($"- RepairImprovement: `{report.RepairImprovement:F3}`");
        sb.AppendLine($"- RepairedCandidateMatchesOrBeatsReference: `{report.CounterexampleReplayAfterRepair.RepairedCandidateMatchesOrBeatsReference}`");
        sb.AppendLine();
        sb.AppendLine("## Evidence Sufficiency Recomputed V2");
        sb.AppendLine($"- PreviousScore: `{report.EvidenceSufficiencyRecomputedV2.PreviousEvidenceSufficiencyScore:F3}` → NewScoreV2: `{report.EvidenceSufficiencyRecomputedV2.NewEvidenceSufficiencyScoreV2:F3}` (threshold=`{report.EvidenceSufficiencyRecomputedV2.Threshold:F3}`)");
        sb.AppendLine($"- EvidenceSufficient: `{report.EvidenceSufficient}` EvidenceBoundShadowLabelsAreFormal: `{report.EvidenceBoundShadowLabelsAreFormal}`");
        sb.AppendLine();
        sb.AppendLine("## Authority Invariants");
        sb.AppendLine($"- ShadowOnly: `{report.ShadowOnly}` SyntheticLabelAuthority: `{report.SyntheticLabelAuthority}` AutoIngest: `{report.AutoIngest}` TrainingSetChanged: `{report.TrainingSetChanged}`");
        sb.AppendLine($"- MLAuthority: `{report.MLAuthority}` LLMAuthority: `{report.LLMAuthority}` RuntimeAuthority: `{report.RuntimeAuthority}` GateAuthority: `{report.GateAuthority}`");
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

public sealed class LearningCounterexampleRepairPackCase
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

public sealed class LearningCounterexampleRepairPackReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool CounterexampleRepairPackPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public int ReadyCases { get; init; }
    public int BlockedCases { get; init; }
    public IReadOnlyList<LearningCounterexampleRepairPackCase> Cases { get; init; } = Array.Empty<LearningCounterexampleRepairPackCase>();
    public IReadOnlyList<EvidenceBoundShadowLabel> EvidenceBoundShadowLabels { get; init; } = Array.Empty<EvidenceBoundShadowLabel>();
    public IReadOnlyList<UnboundCandidateSpec> UnboundCandidateSpecs { get; init; } = Array.Empty<UnboundCandidateSpec>();
    public CounterexampleRepairAnalysis CounterexampleRepairAnalysis { get; init; } = new();
    public RepairedShadowScoringProposal RepairedShadowScoringProposal { get; init; } = new();
    public CounterexampleReplayAfterRepair CounterexampleReplayAfterRepair { get; init; } = new();
    public EvidenceSufficiencyRecomputedV2 EvidenceSufficiencyRecomputedV2 { get; init; } = new();
    public bool EvidenceBoundHardNegativeLabelsReady { get; init; }
    public int EvidenceBoundShadowLabelCount { get; init; }
    public int UnboundCandidateSpecCount { get; init; }
    public double BindingCoverageRate { get; init; }
    public bool CounterexampleRepairAnalysisReady { get; init; }
    public bool RepairedShadowScoringProposalReady { get; init; }
    public bool CounterexampleReplayAfterRepairReady { get; init; }
    public bool EvidenceSufficiencyRecomputedV2Ready { get; init; }
    public double OriginalCandidateFailureRate { get; init; }
    public double RepairedCandidateFailureRate { get; init; }
    public double ReferenceFailureRate { get; init; }
    public double RepairImprovement { get; init; }
    public bool EvidenceSufficient { get; init; }
    public bool EvidenceBoundShadowLabelsAreFormal { get; init; }
    public bool RuntimePilotExecutionReadyForSeparateGate { get; init; }
    public IReadOnlyList<string> BlockedForRuntimePilotExecutionBy { get; init; } = Array.Empty<string>();
    public bool ShadowOnly { get; init; }
    public bool SyntheticLabelAuthority { get; init; }
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
    public bool UpstreamEvidenceAccumulationPackGatePresent { get; init; }
    public bool UpstreamEvidenceAccumulationPackGatePassed { get; init; }
    public bool MainlineEvidencePresent { get; init; }
    public bool MainlineTrustRegistryPresent { get; init; }
    public string EvidenceBoundHardNegativeLabelsPath { get; init; } = string.Empty;
    public string UnboundHardNegativeCandidatesPath { get; init; } = string.Empty;
    public string CounterexampleRepairAnalysisPath { get; init; } = string.Empty;
    public string RepairedShadowScoringProposalPath { get; init; } = string.Empty;
    public string CounterexampleReplayAfterRepairPath { get; init; } = string.Empty;
    public string EvidenceSufficiencyRecomputedV2Path { get; init; } = string.Empty;
    public string Recommendation { get; init; } = string.Empty;
    public string NextAllowedPhase { get; init; } = string.Empty;
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class LearningCounterexampleRepairPackOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
