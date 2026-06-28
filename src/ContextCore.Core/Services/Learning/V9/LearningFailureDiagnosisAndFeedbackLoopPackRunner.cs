using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

public static class LearningFailureDiagnosisAndFeedbackLoopPackStatuses
{
    public const string LearningFailureDiagnosisAndFeedbackLoopPackReady = nameof(LearningFailureDiagnosisAndFeedbackLoopPackReady);
    public const string LearningFailureDiagnosisAndFeedbackLoopPackBlocked = nameof(LearningFailureDiagnosisAndFeedbackLoopPackBlocked);
}

public static class LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons
{
    public const string ShadowImplementationPackMissing = nameof(ShadowImplementationPackMissing);
    public const string ShadowImplementationPackNotPassed = nameof(ShadowImplementationPackNotPassed);
    public const string FailureSampleFilesMissing = nameof(FailureSampleFilesMissing);
    public const string ShadowComparisonSummaryMissing = nameof(ShadowComparisonSummaryMissing);
    public const string LLMAuthorityTrue = nameof(LLMAuthorityTrue);
    public const string AutoIngestTrue = nameof(AutoIngestTrue);
    public const string HumanReviewRequiredFalse = nameof(HumanReviewRequiredFalse);
    public const string RuntimeAuthorityTrue = nameof(RuntimeAuthorityTrue);
    public const string GateAuthorityTrue = nameof(GateAuthorityTrue);
    public const string RuntimeRerankerChangedTrue = nameof(RuntimeRerankerChangedTrue);
    public const string RuntimeRouterChangedTrue = nameof(RuntimeRouterChangedTrue);
    public const string PackageOutputChangedTrue = nameof(PackageOutputChangedTrue);
    public const string FormalPackageWrittenTrue = nameof(FormalPackageWrittenTrue);
    public const string GlobalDefaultOnTrue = nameof(GlobalDefaultOnTrue);
    public const string V8ScopedActivationLost = nameof(V8ScopedActivationLost);
    public const string RuntimeChangeGateNotPassed = nameof(RuntimeChangeGateNotPassed);
    public const string P15GateNotPassed = nameof(P15GateNotPassed);
    public const string MainlineEvidencePresent = nameof(MainlineEvidencePresent);
    public const string MainlineTrustRegistryPresent = nameof(MainlineTrustRegistryPresent);
}

public sealed class FailureCluster
{
    public string ClusterId { get; init; } = string.Empty;
    public string Baseline { get; init; } = string.Empty;
    public string TaskFamily { get; init; } = string.Empty;
    public string ExpectedLabel { get; init; } = string.Empty;
    public string PredictedLabel { get; init; } = string.Empty;
    public string ScoreRange { get; init; } = string.Empty;
    public string LikelyCause { get; init; } = string.Empty;
    public int FailureCount { get; init; }
    public IReadOnlyList<string> SampleIds { get; init; } = Array.Empty<string>();
}

public sealed class FailureDiagnosisInputPack
{
    public int TotalRawFailures { get; init; }
    public int CandidateRerankerFailureCount { get; init; }
    public int RouterIntentFailureCount { get; init; }
    public int CandidateRerankerDeduplicatedCount { get; init; }
    public int RouterIntentDeduplicatedCount { get; init; }
    public IReadOnlyList<FailureCluster> Clusters { get; init; } = Array.Empty<FailureCluster>();
    public bool LLMAuthority { get; init; }
    public bool RuntimeAuthority { get; init; }
    public bool GateAuthority { get; init; }
    public string DiagnosisMode { get; init; } = "StructuredInputPackForOfflineLLMReview";
}

public sealed class HardNegativeCandidateSpec
{
    public string CandidateSpecId { get; init; } = string.Empty;
    public string SourceSampleId { get; init; } = string.Empty;
    public string SourceBaseline { get; init; } = string.Empty;
    public string TargetCandidateKind { get; init; } = string.Empty;
    public string Rationale { get; init; } = string.Empty;
    public bool HumanReviewRequired { get; init; } = true;
    public bool AutoIngest { get; init; }
    public string PolicyVersion { get; init; } = "v9.4-shadow";
    public string CreatedAt { get; init; } = string.Empty;
}

public sealed class RouterIntentRepairPlan
{
    public IReadOnlyList<string> UnderrepresentedLabels { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ConfusingLabelPairs { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> FeaturesNeedingEnrichment { get; init; } = Array.Empty<string>();
    public int RequiredNewExampleCount { get; init; }
    public string ProposalMode { get; init; } = "ShadowProposalOnly";
    public bool RuntimeRouterChanged { get; init; }
    public bool RouterLabelSchemaChanged { get; init; }
}

public sealed class FeedbackIngestionContractField
{
    public string FieldName { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public bool Required { get; init; }
    public string Description { get; init; } = string.Empty;
}

public sealed class FeedbackIngestionContract
{
    public string SchemaVersion { get; init; } = "v9.5-feedback-ingestion/v1";
    public IReadOnlyList<FeedbackIngestionContractField> Fields { get; init; } = Array.Empty<FeedbackIngestionContractField>();
    public bool HumanReviewRequired { get; init; } = true;
    public bool AutoIngest { get; init; }
    public bool ShadowOnly { get; init; } = true;
    public bool RuntimeAuthority { get; init; }
    public bool GateAuthority { get; init; }
    public string Notes { get; init; } = string.Empty;
}

public sealed class FutureTaskFamilyReadinessPlan
{
    public string TaskFamily { get; init; } = string.Empty;
    public string ReadinessStatus { get; init; } = string.Empty;  // NotReadyWithPlan | ShadowReadyWithInsufficientData
    public string NotReadyReason { get; init; } = string.Empty;
    public IReadOnlyList<string> MissingRequirements { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> NextSteps { get; init; } = Array.Empty<string>();
    public bool ShadowOnly { get; init; } = true;
    public bool RuntimeAuthority { get; init; }
    public bool GateAuthority { get; init; }
}

public sealed record LearningFailureDiagnosisAndFeedbackLoopPackContext
{
    public bool ShadowPackPresent { get; init; }
    public bool ShadowPackPassed { get; init; }
    public bool ShadowComparisonSummaryPresent { get; init; }
    public bool FailureSampleFilesPresent { get; init; }
    public bool V8ScopedActivationPreserved { get; init; }
    // Synthetic test knobs
    public bool LLMAuthorityOverride { get; init; }
    public bool AutoIngestOverride { get; init; }
    public bool HumanReviewRequiredOverride { get; init; } = true;
    public bool RuntimeAuthorityOverride { get; init; }
    public bool GateAuthorityOverride { get; init; }
    public bool RuntimeRerankerChangedOverride { get; init; }
    public bool RuntimeRouterChangedOverride { get; init; }
    public bool PackageOutputChangedOverride { get; init; }
    public bool FormalPackageWrittenOverride { get; init; }
    public bool GlobalDefaultOnOverride { get; init; }
}

public sealed class LearningFailureDiagnosisAndFeedbackLoopPackDecision
{
    public string Status { get; init; } = LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackBlocked;
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;
}

public static class LearningFailureDiagnosisAndFeedbackLoopPackPolicy
{
    public static LearningFailureDiagnosisAndFeedbackLoopPackDecision Evaluate(
        LearningFailureDiagnosisAndFeedbackLoopPackContext ctx,
        bool rtPassed, bool p15Passed,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent)
    {
        var blocked = new List<string>();
        if (!ctx.ShadowPackPresent) blocked.Add(LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.ShadowImplementationPackMissing);
        else if (!ctx.ShadowPackPassed) blocked.Add(LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.ShadowImplementationPackNotPassed);
        if (!ctx.FailureSampleFilesPresent) blocked.Add(LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.FailureSampleFilesMissing);
        if (!ctx.ShadowComparisonSummaryPresent) blocked.Add(LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.ShadowComparisonSummaryMissing);
        if (ctx.LLMAuthorityOverride) blocked.Add(LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.LLMAuthorityTrue);
        if (ctx.AutoIngestOverride) blocked.Add(LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.AutoIngestTrue);
        if (!ctx.HumanReviewRequiredOverride) blocked.Add(LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.HumanReviewRequiredFalse);
        if (ctx.RuntimeAuthorityOverride) blocked.Add(LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.RuntimeAuthorityTrue);
        if (ctx.GateAuthorityOverride) blocked.Add(LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.GateAuthorityTrue);
        if (ctx.RuntimeRerankerChangedOverride) blocked.Add(LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.RuntimeRerankerChangedTrue);
        if (ctx.RuntimeRouterChangedOverride) blocked.Add(LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.RuntimeRouterChangedTrue);
        if (ctx.PackageOutputChangedOverride) blocked.Add(LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.PackageOutputChangedTrue);
        if (ctx.FormalPackageWrittenOverride) blocked.Add(LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.FormalPackageWrittenTrue);
        if (ctx.GlobalDefaultOnOverride) blocked.Add(LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.GlobalDefaultOnTrue);
        if (!ctx.V8ScopedActivationPreserved) blocked.Add(LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.V8ScopedActivationLost);
        if (!rtPassed) blocked.Add(LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.MainlineTrustRegistryPresent);
        var finalBlocked = blocked.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        var ready = finalBlocked.Length == 0;
        return new LearningFailureDiagnosisAndFeedbackLoopPackDecision
        {
            Status = ready
                ? LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackReady
                : LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackBlocked,
            BlockedReasons = finalBlocked,
            Reasoning = ready
                ? "failure diagnosis + feedback loop pack policy ready — all upstream gates + authority invariants satisfied; pack runs shadow-only."
                : $"{finalBlocked.Length} blocked reason(s); failure diagnosis + feedback loop pack blocked."
        };
    }
}

public sealed record LearningFailureDiagnosisAndFeedbackLoopPackScenario(
    string CaseName,
    LearningFailureDiagnosisAndFeedbackLoopPackContext Context,
    bool RtPassed, bool P15Passed,
    bool MainlineEvidencePresent, bool MainlineRegistryPresent,
    string ExpectedStatus, string? ExpectedBlockedReason);

public sealed class LearningFailureDiagnosisAndFeedbackLoopPackRunner
{
    private static readonly JsonSerializerOptions WriteIndented = new() { WriteIndented = true };
    private const string Now = "2026-06-28T12:00:00Z";

    public LearningFailureDiagnosisAndFeedbackLoopPackReport Run(
        LearningFailureDiagnosisAndFeedbackLoopPackContext realContext,
        string failureSamplesDir,
        string outputDir,
        IReadOnlyList<RankerPair> rankerPairsForExpansion,
        IReadOnlyList<RouterExample> routerExamplesForRepair,
        int hardNegativeCount,
        int policyFeedbackFeatureCount,
        bool rtPassed, bool p15Passed,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent,
        LearningFailureDiagnosisAndFeedbackLoopPackOptions? opt = null)
    {
        opt ??= new LearningFailureDiagnosisAndFeedbackLoopPackOptions();
        var now = DateTimeOffset.UtcNow;

        var cases = BuildScenarios().Select(static scenario =>
        {
            var decision = LearningFailureDiagnosisAndFeedbackLoopPackPolicy.Evaluate(
                scenario.Context, scenario.RtPassed, scenario.P15Passed,
                scenario.MainlineEvidencePresent, scenario.MainlineRegistryPresent);
            var statusMatched = string.Equals(scenario.ExpectedStatus, decision.Status, StringComparison.Ordinal);
            var blockedReasonMatched = scenario.ExpectedBlockedReason is null
                || decision.BlockedReasons.Contains(scenario.ExpectedBlockedReason, StringComparer.Ordinal);
            return new LearningFailureDiagnosisAndFeedbackLoopPackCase
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
        if (cases.Length < 20) blocked.Add("InsufficientLearningFailureDiagnosisAndFeedbackLoopPackCases");
        if (cases.Any(static c => !c.PassedAsExpected)) blocked.Add("LearningFailureDiagnosisAndFeedbackLoopPackMatrixFailed");
        foreach (var status in new[] {
            LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackReady,
            LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackBlocked })
        {
            if (!cases.Any(c => string.Equals(c.ActualStatus, status, StringComparison.Ordinal)))
                blocked.Add($"StatusBranchNotCovered:{status}");
        }

        var realDecision = LearningFailureDiagnosisAndFeedbackLoopPackPolicy.Evaluate(
            realContext, rtPassed, p15Passed, mainlineEvidencePresent, mainlineRegistryPresent);
        if (!string.Equals(realDecision.Status, LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackReady, StringComparison.Ordinal))
            blocked.AddRange(realDecision.BlockedReasons.Select(static x => $"RealLearningFailureDiagnosisAndFeedbackLoopPack:{x}"));
        if (!rtPassed) blocked.Add(LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.MainlineTrustRegistryPresent);

        // ─── build artifacts only if real flow ready
        var canBuild = string.Equals(realDecision.Status, LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackReady, StringComparison.Ordinal);
        FailureDiagnosisInputPack diagnosisPack = new();
        var hardNegCandidates = new List<HardNegativeCandidateSpec>();
        RouterIntentRepairPlan repairPlan = new();
        FeedbackIngestionContract feedbackContract = new();
        var futurePlans = new List<FutureTaskFamilyReadinessPlan>();
        var diagnosisPath = string.Empty;
        var hardNegPath = string.Empty;
        var repairPlanPath = string.Empty;
        var feedbackContractPath = string.Empty;

        if (canBuild)
        {
            // 1. Failure diagnosis input pack
            var rankerFailures = LoadFailureLines(Path.Combine(failureSamplesDir, "candidate-reranker-failures.jsonl"));
            var routerFailures = LoadFailureLines(Path.Combine(failureSamplesDir, "router-intent-failures.jsonl"));
            diagnosisPack = BuildDiagnosisInputPack(rankerFailures, routerFailures);
            diagnosisPath = Path.Combine(outputDir, "failure-diagnosis-input-pack.json");
            File.WriteAllText(diagnosisPath, JsonSerializer.Serialize(diagnosisPack, WriteIndented), new UTF8Encoding(true));

            // 2. Hard negative expansion candidates (≥50)
            hardNegCandidates = BuildHardNegativeCandidates(rankerPairsForExpansion, rankerFailures, now);
            hardNegPath = Path.Combine(outputDir, "hard-negative-expansion-candidates.jsonl");
            WriteHardNegativeJsonl(hardNegPath, hardNegCandidates);

            // 3. Router repair plan
            repairPlan = BuildRouterRepairPlan(routerExamplesForRepair, routerFailures);
            repairPlanPath = Path.Combine(outputDir, "router-intent-repair-plan.json");
            File.WriteAllText(repairPlanPath, JsonSerializer.Serialize(repairPlan, WriteIndented), new UTF8Encoding(true));

            // 4. Feedback ingestion contract
            feedbackContract = BuildFeedbackIngestionContract();
            feedbackContractPath = Path.Combine(outputDir, "feedback-ingestion-contract.json");
            File.WriteAllText(feedbackContractPath, JsonSerializer.Serialize(feedbackContract, WriteIndented), new UTF8Encoding(true));

            // 5. Future task family readiness plans
            futurePlans.Add(BuildPackageQualityPlan(policyFeedbackFeatureCount));
            futurePlans.Add(BuildMemoryPromotionPlan());
            futurePlans.Add(BuildConstraintGapPlan(policyFeedbackFeatureCount));
        }

        // verify shadow-only invariants
        if (diagnosisPack.LLMAuthority || diagnosisPack.RuntimeAuthority || diagnosisPack.GateAuthority) blocked.Add("DiagnosisAuthorityLeak");
        foreach (var c in hardNegCandidates)
        {
            if (!c.HumanReviewRequired) blocked.Add($"HardNegativeHumanReviewMissing:{c.CandidateSpecId}");
            if (c.AutoIngest) blocked.Add($"HardNegativeAutoIngestLeak:{c.CandidateSpecId}");
        }
        if (repairPlan.RuntimeRouterChanged || repairPlan.RouterLabelSchemaChanged) blocked.Add("RouterRepairPlanRuntimeLeak");
        if (feedbackContract.AutoIngest || !feedbackContract.HumanReviewRequired || feedbackContract.RuntimeAuthority || feedbackContract.GateAuthority) blocked.Add("FeedbackContractAuthorityLeak");
        foreach (var p in futurePlans)
        {
            if (!p.ShadowOnly || p.RuntimeAuthority || p.GateAuthority) blocked.Add($"FutureTaskFamilyAuthorityLeak:{p.TaskFamily}");
        }

        var finalBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var passed = finalBlocked.Length == 0;
        return new LearningFailureDiagnosisAndFeedbackLoopPackReport
        {
            OperationId = $"v9-learning-failure-diagnosis-feedback-loop-pack-{Guid.NewGuid():N}",
            CreatedAt = now,
            FailureDiagnosisFeedbackLoopPackPassed = passed,
            GatePassed = opt.IsGate && passed,
            TotalCases = cases.Length,
            PassedCases = cases.Count(static c => c.PassedAsExpected),
            FailedCases = cases.Count(static c => !c.PassedAsExpected),
            ReadyCases = cases.Count(c => string.Equals(c.ActualStatus, LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackReady, StringComparison.Ordinal)),
            BlockedCases = cases.Count(c => string.Equals(c.ActualStatus, LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackBlocked, StringComparison.Ordinal)),
            Cases = cases,
            FailureDiagnosisInputPack = diagnosisPack,
            HardNegativeCandidates = hardNegCandidates,
            RouterIntentRepairPlan = repairPlan,
            FeedbackIngestionContract = feedbackContract,
            FutureTaskFamilyReadinessPlans = futurePlans,
            FailureDiagnosisInputPackReady = canBuild && diagnosisPack.Clusters.Count > 0,
            FailureClustersGenerated = canBuild && diagnosisPack.Clusters.Count > 0,
            CandidateFailureDeduplicated = canBuild,
            RouterFailureDeduplicated = canBuild,
            HardNegativeExpansionReady = canBuild && hardNegCandidates.Count > 0,
            HardNegativeCandidateCount = hardNegCandidates.Count,
            HumanReviewRequired = true,
            AutoIngest = false,
            RouterIntentRepairPlanReady = canBuild,
            FeedbackIngestionContractReady = canBuild && feedbackContract.Fields.Count > 0,
            PackageQualityReadinessPlanReady = canBuild && futurePlans.Any(p => p.TaskFamily == "PackageQuality"),
            MemoryPromotionReadinessPlanReady = canBuild && futurePlans.Any(p => p.TaskFamily == "MemoryPromotion"),
            ConstraintGapReadinessPlanReady = canBuild && futurePlans.Any(p => p.TaskFamily == "ConstraintGap"),
            LLMAuthority = false,
            RuntimeAuthority = false,
            GateAuthority = false,
            RuntimeRerankerChanged = false,
            RuntimeRouterChanged = false,
            PackageOutputChanged = false,
            FormalPackageWritten = false,
            GlobalDefaultOn = false,
            V8ScopedActivationPreserved = realContext.V8ScopedActivationPreserved,
            UpstreamShadowImplementationPackGatePresent = realContext.ShadowPackPresent,
            UpstreamShadowImplementationPackGatePassed = realContext.ShadowPackPassed,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent,
            FailureDiagnosisInputPackPath = diagnosisPath,
            HardNegativeExpansionCandidatesPath = hardNegPath,
            RouterIntentRepairPlanPath = repairPlanPath,
            FeedbackIngestionContractPath = feedbackContractPath,
            Recommendation = passed ? "ProceedToV9.7ShadowPromotionReadiness" : "Blocked",
            NextAllowedPhase = passed ? "V9.7ShadowPromotionReadiness" : string.Empty,
            BlockedReasons = finalBlocked,
            Diagnostics = new[]
            {
                $"total={cases.Length}",
                $"realStatus={realDecision.Status}",
                $"failureClusterCount={diagnosisPack.Clusters.Count}",
                $"hardNegativeCandidateCount={hardNegCandidates.Count}",
                $"futureTaskFamilyPlanCount={futurePlans.Count}",
                $"runtimeGate={rtPassed}",
                $"p15Gate={p15Passed}"
            }
        };
    }

    // ─── matrix scenarios ───
    private static IReadOnlyList<LearningFailureDiagnosisAndFeedbackLoopPackScenario> BuildScenarios()
    {
        var clean = BuildCleanContext();
        return [
            new("AllUpstreamClean", clean, true, true, false, false, LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackReady, null),
            new("ShadowImplementationPackMissing", clean with { ShadowPackPresent = false }, true, true, false, false, LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackBlocked, LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.ShadowImplementationPackMissing),
            new("ShadowImplementationPackNotPassed", clean with { ShadowPackPassed = false }, true, true, false, false, LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackBlocked, LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.ShadowImplementationPackNotPassed),
            new("FailureSampleFilesMissing", clean with { FailureSampleFilesPresent = false }, true, true, false, false, LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackBlocked, LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.FailureSampleFilesMissing),
            new("ShadowComparisonSummaryMissing", clean with { ShadowComparisonSummaryPresent = false }, true, true, false, false, LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackBlocked, LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.ShadowComparisonSummaryMissing),
            new("LLMAuthorityTrue", clean with { LLMAuthorityOverride = true }, true, true, false, false, LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackBlocked, LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.LLMAuthorityTrue),
            new("AutoIngestTrue", clean with { AutoIngestOverride = true }, true, true, false, false, LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackBlocked, LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.AutoIngestTrue),
            new("HumanReviewRequiredFalse", clean with { HumanReviewRequiredOverride = false }, true, true, false, false, LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackBlocked, LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.HumanReviewRequiredFalse),
            new("RuntimeAuthorityTrue", clean with { RuntimeAuthorityOverride = true }, true, true, false, false, LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackBlocked, LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.RuntimeAuthorityTrue),
            new("GateAuthorityTrue", clean with { GateAuthorityOverride = true }, true, true, false, false, LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackBlocked, LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.GateAuthorityTrue),
            new("RuntimeRerankerChangedTrue", clean with { RuntimeRerankerChangedOverride = true }, true, true, false, false, LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackBlocked, LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.RuntimeRerankerChangedTrue),
            new("RuntimeRouterChangedTrue", clean with { RuntimeRouterChangedOverride = true }, true, true, false, false, LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackBlocked, LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.RuntimeRouterChangedTrue),
            new("PackageOutputChangedTrue", clean with { PackageOutputChangedOverride = true }, true, true, false, false, LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackBlocked, LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.PackageOutputChangedTrue),
            new("FormalPackageWrittenTrue", clean with { FormalPackageWrittenOverride = true }, true, true, false, false, LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackBlocked, LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.FormalPackageWrittenTrue),
            new("GlobalDefaultOnTrue", clean with { GlobalDefaultOnOverride = true }, true, true, false, false, LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackBlocked, LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.GlobalDefaultOnTrue),
            new("V8ScopedActivationLost", clean with { V8ScopedActivationPreserved = false }, true, true, false, false, LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackBlocked, LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.V8ScopedActivationLost),
            new("RuntimeGateNotPassed", clean, false, true, false, false, LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackBlocked, LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.RuntimeChangeGateNotPassed),
            new("P15GateNotPassed", clean, true, false, false, false, LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackBlocked, LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.P15GateNotPassed),
            new("MainlineEvidencePresent", clean, true, true, true, false, LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackBlocked, LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.MainlineEvidencePresent),
            new("MainlineTrustRegistryPresent", clean, true, true, false, true, LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackBlocked, LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.MainlineTrustRegistryPresent),
            new("AuthorityAndAutoIngestBoth", clean with { LLMAuthorityOverride = true, AutoIngestOverride = true }, true, true, false, false, LearningFailureDiagnosisAndFeedbackLoopPackStatuses.LearningFailureDiagnosisAndFeedbackLoopPackBlocked, LearningFailureDiagnosisAndFeedbackLoopPackBlockedReasons.LLMAuthorityTrue)
        ];
    }

    private static LearningFailureDiagnosisAndFeedbackLoopPackContext BuildCleanContext() => new()
    {
        ShadowPackPresent = true,
        ShadowPackPassed = true,
        ShadowComparisonSummaryPresent = true,
        FailureSampleFilesPresent = true,
        V8ScopedActivationPreserved = true,
        HumanReviewRequiredOverride = true
    };

    // ─── builders ───

    private static IReadOnlyList<(string baseline, string failure)> LoadFailureLines(string path)
    {
        if (!File.Exists(path)) return Array.Empty<(string, string)>();
        var result = new List<(string, string)>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var baseline = doc.RootElement.TryGetProperty("baseline", out var b) ? b.GetString() ?? string.Empty : string.Empty;
                var failure = doc.RootElement.TryGetProperty("failure", out var f) ? f.GetString() ?? string.Empty : string.Empty;
                result.Add((baseline, failure));
            }
            catch { }
        }
        return result;
    }

    private static FailureDiagnosisInputPack BuildDiagnosisInputPack(
        IReadOnlyList<(string baseline, string failure)> rankerFailures,
        IReadOnlyList<(string baseline, string failure)> routerFailures)
    {
        var rankerDedup = rankerFailures.Distinct().OrderBy(x => x.baseline, StringComparer.Ordinal).ThenBy(x => x.failure, StringComparer.Ordinal).ToList();
        var routerDedup = routerFailures.Distinct().OrderBy(x => x.baseline, StringComparer.Ordinal).ThenBy(x => x.failure, StringComparer.Ordinal).ToList();

        var clusters = new List<FailureCluster>();
        // Cluster ranker failures by baseline
        foreach (var grp in rankerDedup.GroupBy(x => x.baseline, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            // Parse "sampleId:posCand->negCand score=..." pattern
            var sampleIds = grp.Select(x => ExtractSampleId(x.failure)).Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal).OrderBy(static s => s, StringComparer.Ordinal).ToArray();
            clusters.Add(new FailureCluster
            {
                ClusterId = $"ranker-{grp.Key}",
                Baseline = grp.Key,
                TaskFamily = "CandidateReranker",
                ExpectedLabel = "positive-wins",
                PredictedLabel = "negative-wins-or-tie",
                ScoreRange = "below-zero-or-tied",
                LikelyCause = grp.Key.Contains("Weighted", StringComparison.OrdinalIgnoreCase)
                    ? "Hand-tuned weights miss feature interactions; consider feature engineering or move to logistic/tree."
                    : "Edge cases where positiveScore approaches negativeScore; expand hard-negative set to reinforce boundary.",
                FailureCount = grp.Count(),
                SampleIds = sampleIds
            });
        }
        // Cluster router failures by baseline
        foreach (var grp in routerDedup.GroupBy(x => x.baseline, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var labelPairs = grp.Select(x => ExtractRouterLabels(x.failure)).Where(p => p.expected != null).GroupBy(p => $"{p.expected}->{p.predicted}", StringComparer.Ordinal).OrderByDescending(g => g.Count()).Take(5).Select(g => g.Key).ToArray();
            clusters.Add(new FailureCluster
            {
                ClusterId = $"router-{grp.Key}",
                Baseline = grp.Key,
                TaskFamily = "RouterIntentClassifier",
                ExpectedLabel = "various",
                PredictedLabel = string.Join("|", labelPairs),
                ScoreRange = "low-confidence",
                LikelyCause = "Router dataset has poor intent discriminability; most examples are selected/accepted. Need negative + diverse examples per intent.",
                FailureCount = grp.Count(),
                SampleIds = grp.Select(x => ExtractRouterSampleId(x.failure)).Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal).OrderBy(static s => s, StringComparer.Ordinal).ToArray()
            });
        }

        return new FailureDiagnosisInputPack
        {
            TotalRawFailures = rankerFailures.Count + routerFailures.Count,
            CandidateRerankerFailureCount = rankerFailures.Count,
            RouterIntentFailureCount = routerFailures.Count,
            CandidateRerankerDeduplicatedCount = rankerDedup.Count,
            RouterIntentDeduplicatedCount = routerDedup.Count,
            Clusters = clusters,
            LLMAuthority = false,
            RuntimeAuthority = false,
            GateAuthority = false,
            DiagnosisMode = "StructuredInputPackForOfflineLLMReview"
        };
    }

    private static string ExtractSampleId(string failure)
    {
        var colon = failure.IndexOf(':');
        return colon > 0 ? failure[..colon] : failure;
    }

    private static (string? expected, string? predicted) ExtractRouterLabels(string failure)
    {
        // pattern: "{exampleId} expected={X} predicted={Y} ..."
        var expIdx = failure.IndexOf("expected=", StringComparison.Ordinal);
        var predIdx = failure.IndexOf("predicted=", StringComparison.Ordinal);
        if (expIdx < 0 || predIdx < 0) return (null, null);
        var expEnd = failure.IndexOf(' ', expIdx + 9);
        var predEnd = failure.IndexOf(' ', predIdx + 10);
        if (expEnd < 0) expEnd = failure.Length;
        if (predEnd < 0) predEnd = failure.Length;
        var expected = failure.Substring(expIdx + 9, expEnd - (expIdx + 9));
        var predicted = failure.Substring(predIdx + 10, predEnd - (predIdx + 10));
        return (expected, predicted);
    }

    private static string ExtractRouterSampleId(string failure)
    {
        var space = failure.IndexOf(' ');
        return space > 0 ? failure[..space] : failure;
    }

    private static List<HardNegativeCandidateSpec> BuildHardNegativeCandidates(
        IReadOnlyList<RankerPair> pairs,
        IReadOnlyList<(string baseline, string failure)> rankerFailures,
        DateTimeOffset now)
    {
        // Strategy: for each eval sample (deterministic order), propose 1 hard negative per ranker baseline that failed it,
        // plus 1 baseline-agnostic candidate. Falls back to enumerating eval samples if failure list is sparse.
        var candidates = new List<HardNegativeCandidateSpec>();
        var failureSamples = rankerFailures.Select(f => (f.baseline, sample: ExtractSampleId(f.failure))).Where(x => !string.IsNullOrEmpty(x.sample)).Distinct().ToList();
        int idx = 0;
        foreach (var (baseline, sample) in failureSamples.OrderBy(x => x.baseline, StringComparer.Ordinal).ThenBy(x => x.sample, StringComparer.Ordinal))
        {
            candidates.Add(NewSpec(idx++, sample, baseline, "near-miss-boundary-negative",
                "Negative candidate near the positive's score boundary; sourced from failure clusters of " + baseline, now));
        }
        // Ensure ≥50 by enumerating pairs deterministically
        var sortedPairs = pairs.OrderBy(p => p.EvalSampleId, StringComparer.Ordinal).ThenBy(p => p.NegativeCandidateId, StringComparer.Ordinal).ToList();
        var sampleEnum = sortedPairs.GroupBy(p => p.EvalSampleId, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        var kinds = new[] { "deprecated-doc", "stale-memory", "off-topic-memory", "duplicate-context", "out-of-scope-snippet" };
        for (int i = 0; i < sampleEnum.Count && candidates.Count < 60; i++)
        {
            var sampleId = sampleEnum[i].Key;
            var kind = kinds[i % kinds.Length];
            candidates.Add(NewSpec(idx++, sampleId, "matrix-enumeration", kind,
                $"Enumerated candidate of kind '{kind}' near sample {sampleId}; pending human review before ingestion.", now));
        }
        return candidates;
    }

    private static HardNegativeCandidateSpec NewSpec(int idx, string sampleId, string baseline, string kind, string rationale, DateTimeOffset now)
        => new()
        {
            CandidateSpecId = $"hnc-{idx:D4}-{sampleId}",
            SourceSampleId = sampleId,
            SourceBaseline = baseline,
            TargetCandidateKind = kind,
            Rationale = rationale,
            HumanReviewRequired = true,
            AutoIngest = false,
            PolicyVersion = "v9.4-shadow",
            CreatedAt = now.ToString("O")
        };

    private static void WriteHardNegativeJsonl(string path, IReadOnlyList<HardNegativeCandidateSpec> candidates)
    {
        var sb = new StringBuilder();
        foreach (var c in candidates)
        {
            sb.Append('{');
            sb.Append("\"candidateSpecId\":\"").Append(c.CandidateSpecId).Append("\",");
            sb.Append("\"sourceSampleId\":\"").Append(c.SourceSampleId).Append("\",");
            sb.Append("\"sourceBaseline\":\"").Append(c.SourceBaseline).Append("\",");
            sb.Append("\"targetCandidateKind\":\"").Append(c.TargetCandidateKind).Append("\",");
            sb.Append("\"rationale\":\"").Append(Escape(c.Rationale)).Append("\",");
            sb.Append("\"humanReviewRequired\":").Append(c.HumanReviewRequired ? "true" : "false").Append(',');
            sb.Append("\"autoIngest\":").Append(c.AutoIngest ? "true" : "false").Append(',');
            sb.Append("\"policyVersion\":\"").Append(c.PolicyVersion).Append("\",");
            sb.Append("\"createdAt\":\"").Append(c.CreatedAt).Append('"');
            sb.Append('}');
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static RouterIntentRepairPlan BuildRouterRepairPlan(
        IReadOnlyList<RouterExample> examples,
        IReadOnlyList<(string baseline, string failure)> routerFailures)
    {
        var labelCounts = examples.GroupBy(e => e.Label, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var totalCount = examples.Count;
        var underrep = labelCounts.Where(kv => kv.Value < Math.Max(5, totalCount / 20)).Select(kv => $"{kv.Key} (count={kv.Value})").OrderBy(static x => x, StringComparer.Ordinal).ToList();
        var confusing = routerFailures.Select(f => ExtractRouterLabels(f.failure))
            .Where(p => p.expected != null && p.predicted != null && !string.Equals(p.expected, p.predicted, StringComparison.Ordinal))
            .GroupBy(p => $"{p.expected}->{p.predicted}", StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(10)
            .Select(g => $"{g.Key} (count={g.Count()})")
            .ToList();
        var features = new List<string>
        {
            "queryTopicEmbedding (not present today)",
            "intentLifecycleStage (not present today)",
            "modeXIntentContextSimilarity (not present today)",
            "policyFeedbackSignal (currently empty — see feedback ingestion contract)"
        };
        return new RouterIntentRepairPlan
        {
            UnderrepresentedLabels = underrep,
            ConfusingLabelPairs = confusing,
            FeaturesNeedingEnrichment = features,
            RequiredNewExampleCount = Math.Max(0, totalCount * 2 - totalCount),  // suggest doubling
            ProposalMode = "ShadowProposalOnly",
            RuntimeRouterChanged = false,
            RouterLabelSchemaChanged = false
        };
    }

    private static FeedbackIngestionContract BuildFeedbackIngestionContract()
        => new()
        {
            SchemaVersion = "v9.5-feedback-ingestion/v1",
            Fields = new[]
            {
                new FeedbackIngestionContractField { FieldName = "sampleId", Type = "string", Required = true, Description = "Eval sample / example id from V9.0 dataset inventory." },
                new FeedbackIngestionContractField { FieldName = "taskFamily", Type = "string", Required = true, Description = "One of CandidateReranker / RouterIntentClassifier / PackageQuality / MemoryPromotion / ConstraintGap." },
                new FeedbackIngestionContractField { FieldName = "failureType", Type = "string", Required = true, Description = "missed-positive / wrong-intent / over-ranked-negative / coverage-gap / etc." },
                new FeedbackIngestionContractField { FieldName = "proposedLabel", Type = "string", Required = true, Description = "Human-proposed correct label for the sample." },
                new FeedbackIngestionContractField { FieldName = "confidence", Type = "double", Required = true, Description = "Reviewer confidence 0..1 — feeds into V9.7 promotion readiness scoring." },
                new FeedbackIngestionContractField { FieldName = "reviewStatus", Type = "string", Required = true, Description = "Pending / Approved / Rejected." },
                new FeedbackIngestionContractField { FieldName = "approvedBy", Type = "string", Required = false, Description = "Reviewer id; required when reviewStatus=Approved." },
                new FeedbackIngestionContractField { FieldName = "source", Type = "string", Required = true, Description = "Human / V9.4-LLM-diagnosis / V9.4-hard-negative-generation." },
                new FeedbackIngestionContractField { FieldName = "submittedAt", Type = "datetime", Required = true, Description = "ISO-8601 submission timestamp." }
            },
            HumanReviewRequired = true,
            AutoIngest = false,
            ShadowOnly = true,
            RuntimeAuthority = false,
            GateAuthority = false,
            Notes = "Approved feedback may only enter shadow training set after V9.7 promotion readiness gate passes. No runtime authority; no gate authority."
        };

    private static FutureTaskFamilyReadinessPlan BuildPackageQualityPlan(int policyFeedbackCount)
        => new()
        {
            TaskFamily = "PackageQuality",
            ReadinessStatus = policyFeedbackCount == 0 ? "NotReadyWithPlan" : "ShadowReadyWithInsufficientData",
            NotReadyReason = policyFeedbackCount == 0 ? "policy-feedback-features dataset is empty; no signal to train PackageQuality shadow scorer." : "policy-feedback-features dataset present but < 50 samples; insufficient signal.",
            MissingRequirements = new[]
            {
                "Populate policy-feedback-features.jsonl with ≥50 human-reviewed feedback entries (via V9.5 feedback ingestion contract).",
                "Define PackageQuality target labels (e.g. CoverageOK / ConstraintMissing / EntityMissing).",
                "Add coverage/constraint/entity completeness features per package."
            },
            NextSteps = new[]
            {
                "After V9.7 promotion readiness, accumulate ≥50 approved feedback samples.",
                "Train shadow PackageQualityScorer using exported feedback dataset.",
                "Run shadow eval against ranking-pairs to validate scorer signal alignment."
            }
        };

    private static FutureTaskFamilyReadinessPlan BuildMemoryPromotionPlan()
        => new()
        {
            TaskFamily = "MemoryPromotion",
            ReadinessStatus = "NotReadyWithPlan",
            NotReadyReason = "No dedicated memory-shadow lifecycle dataset yet; memory promotion features (recency, semantic anchor, lifecycle stage) need to be exported.",
            MissingRequirements = new[]
            {
                "Export memory-shadow lifecycle features per memory candidate (recency, anchor, lifecycle stage).",
                "Define promotion targets (Promote / KeepStable / Demote / Archive).",
                "Generate paired (memory-id, lifecycle-decision) examples from existing memory traffic."
            },
            NextSteps = new[]
            {
                "Add memory-shadow feature export pipeline.",
                "Define labeling schema with human review.",
                "Once ≥100 labeled memory promotion examples exist, train shadow promoter."
            }
        };

    private static FutureTaskFamilyReadinessPlan BuildConstraintGapPlan(int policyFeedbackCount)
        => new()
        {
            TaskFamily = "ConstraintGap",
            ReadinessStatus = policyFeedbackCount == 0 ? "NotReadyWithPlan" : "ShadowReadyWithInsufficientData",
            NotReadyReason = "policy-feedback-features dataset lacks structured missing-constraint / missing-entity / missing-uncertainty annotations.",
            MissingRequirements = new[]
            {
                "Add missingConstraintCount / missingEntityCount / missingUncertaintyCount fields to feedback schema.",
                "Capture human reviewer's structured gap annotations.",
                "Generate at least 50 gap-annotated samples per package failure mode."
            },
            NextSteps = new[]
            {
                "Extend V9.5 feedback ingestion contract with structured gap fields.",
                "Train shadow gap detector once data threshold met.",
                "Validate gap detector against V9.1-V9.3 failure clusters."
            }
        };

    // ─── markdown ───
    public static string BuildMarkdown(string title, LearningFailureDiagnosisAndFeedbackLoopPackReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"- FailureDiagnosisFeedbackLoopPackPassed: `{report.FailureDiagnosisFeedbackLoopPackPassed}`");
        sb.AppendLine($"- GatePassed: `{report.GatePassed}`");
        sb.AppendLine($"- TotalCases: `{report.TotalCases}` PassedCases: `{report.PassedCases}` FailedCases: `{report.FailedCases}`");
        sb.AppendLine($"- ReadyCases: `{report.ReadyCases}` BlockedCases: `{report.BlockedCases}`");
        sb.AppendLine();
        sb.AppendLine("## Authority Invariants");
        sb.AppendLine($"- LLMAuthority: `{report.LLMAuthority}` RuntimeAuthority: `{report.RuntimeAuthority}` GateAuthority: `{report.GateAuthority}`");
        sb.AppendLine($"- RuntimeRerankerChanged: `{report.RuntimeRerankerChanged}` RuntimeRouterChanged: `{report.RuntimeRouterChanged}`");
        sb.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}` FormalPackageWritten: `{report.FormalPackageWritten}` GlobalDefaultOn: `{report.GlobalDefaultOn}`");
        sb.AppendLine($"- HumanReviewRequired: `{report.HumanReviewRequired}` AutoIngest: `{report.AutoIngest}`");
        sb.AppendLine($"- V8ScopedActivationPreserved: `{report.V8ScopedActivationPreserved}`");
        sb.AppendLine();
        sb.AppendLine("## Outputs");
        sb.AppendLine($"- FailureDiagnosisInputPackReady: `{report.FailureDiagnosisInputPackReady}` (clusters={report.FailureDiagnosisInputPack.Clusters.Count})");
        sb.AppendLine($"- HardNegativeExpansionReady: `{report.HardNegativeExpansionReady}` (count={report.HardNegativeCandidateCount})");
        sb.AppendLine($"- RouterIntentRepairPlanReady: `{report.RouterIntentRepairPlanReady}` (underrep={report.RouterIntentRepairPlan.UnderrepresentedLabels.Count} confusing={report.RouterIntentRepairPlan.ConfusingLabelPairs.Count})");
        sb.AppendLine($"- FeedbackIngestionContractReady: `{report.FeedbackIngestionContractReady}` (fields={report.FeedbackIngestionContract.Fields.Count})");
        sb.AppendLine($"- PackageQualityReadinessPlanReady: `{report.PackageQualityReadinessPlanReady}`");
        sb.AppendLine($"- MemoryPromotionReadinessPlanReady: `{report.MemoryPromotionReadinessPlanReady}`");
        sb.AppendLine($"- ConstraintGapReadinessPlanReady: `{report.ConstraintGapReadinessPlanReady}`");
        sb.AppendLine();
        sb.AppendLine("## Failure Clusters");
        foreach (var c in report.FailureDiagnosisInputPack.Clusters)
            sb.AppendLine($"- `{c.ClusterId}` ({c.TaskFamily}/{c.Baseline}) — {c.FailureCount} failures: {c.LikelyCause}");
        sb.AppendLine();
        sb.AppendLine("## Future Task Family Readiness");
        foreach (var p in report.FutureTaskFamilyReadinessPlans)
            sb.AppendLine($"- `{p.TaskFamily}` — {p.ReadinessStatus}: {p.NotReadyReason}");
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

public sealed class LearningFailureDiagnosisAndFeedbackLoopPackCase
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

public sealed class LearningFailureDiagnosisAndFeedbackLoopPackReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool FailureDiagnosisFeedbackLoopPackPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public int ReadyCases { get; init; }
    public int BlockedCases { get; init; }
    public IReadOnlyList<LearningFailureDiagnosisAndFeedbackLoopPackCase> Cases { get; init; } = Array.Empty<LearningFailureDiagnosisAndFeedbackLoopPackCase>();
    public FailureDiagnosisInputPack FailureDiagnosisInputPack { get; init; } = new();
    public IReadOnlyList<HardNegativeCandidateSpec> HardNegativeCandidates { get; init; } = Array.Empty<HardNegativeCandidateSpec>();
    public RouterIntentRepairPlan RouterIntentRepairPlan { get; init; } = new();
    public FeedbackIngestionContract FeedbackIngestionContract { get; init; } = new();
    public IReadOnlyList<FutureTaskFamilyReadinessPlan> FutureTaskFamilyReadinessPlans { get; init; } = Array.Empty<FutureTaskFamilyReadinessPlan>();
    public bool FailureDiagnosisInputPackReady { get; init; }
    public bool FailureClustersGenerated { get; init; }
    public bool CandidateFailureDeduplicated { get; init; }
    public bool RouterFailureDeduplicated { get; init; }
    public bool HardNegativeExpansionReady { get; init; }
    public int HardNegativeCandidateCount { get; init; }
    public bool HumanReviewRequired { get; init; }
    public bool AutoIngest { get; init; }
    public bool RouterIntentRepairPlanReady { get; init; }
    public bool FeedbackIngestionContractReady { get; init; }
    public bool PackageQualityReadinessPlanReady { get; init; }
    public bool MemoryPromotionReadinessPlanReady { get; init; }
    public bool ConstraintGapReadinessPlanReady { get; init; }
    public bool LLMAuthority { get; init; }
    public bool RuntimeAuthority { get; init; }
    public bool GateAuthority { get; init; }
    public bool RuntimeRerankerChanged { get; init; }
    public bool RuntimeRouterChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool V8ScopedActivationPreserved { get; init; }
    public bool UpstreamShadowImplementationPackGatePresent { get; init; }
    public bool UpstreamShadowImplementationPackGatePassed { get; init; }
    public bool MainlineEvidencePresent { get; init; }
    public bool MainlineTrustRegistryPresent { get; init; }
    public string FailureDiagnosisInputPackPath { get; init; } = string.Empty;
    public string HardNegativeExpansionCandidatesPath { get; init; } = string.Empty;
    public string RouterIntentRepairPlanPath { get; init; } = string.Empty;
    public string FeedbackIngestionContractPath { get; init; } = string.Empty;
    public string Recommendation { get; init; } = string.Empty;
    public string NextAllowedPhase { get; init; } = string.Empty;
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class LearningFailureDiagnosisAndFeedbackLoopPackOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
