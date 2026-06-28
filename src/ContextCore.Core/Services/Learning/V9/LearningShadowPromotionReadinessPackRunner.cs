using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

public static class LearningShadowPromotionReadinessPackStatuses
{
    public const string LearningShadowPromotionReadinessPackReady = nameof(LearningShadowPromotionReadinessPackReady);
    public const string LearningShadowPromotionReadinessPackBlocked = nameof(LearningShadowPromotionReadinessPackBlocked);
}

public static class LearningShadowPromotionReadinessPackBlockedReasons
{
    public const string FailureFeedbackPackMissing = nameof(FailureFeedbackPackMissing);
    public const string FailureFeedbackPackNotPassed = nameof(FailureFeedbackPackNotPassed);
    public const string ShadowImplementationPackMissing = nameof(ShadowImplementationPackMissing);
    public const string ShadowComparisonSummaryMissing = nameof(ShadowComparisonSummaryMissing);
    public const string HardNegativeCandidatesMissing = nameof(HardNegativeCandidatesMissing);
    public const string FeedbackContractMissing = nameof(FeedbackContractMissing);
    public const string HumanReviewRequiredFalse = nameof(HumanReviewRequiredFalse);
    public const string AutoIngestTrue = nameof(AutoIngestTrue);
    public const string RuntimePromotionAllowedTrue = nameof(RuntimePromotionAllowedTrue);
    public const string RequiresSeparatePromotionGateFalse = nameof(RequiresSeparatePromotionGateFalse);
    public const string RuntimeAuthorityTrue = nameof(RuntimeAuthorityTrue);
    public const string GateAuthorityTrue = nameof(GateAuthorityTrue);
    public const string LLMAuthorityTrue = nameof(LLMAuthorityTrue);
    public const string MLAuthorityTrue = nameof(MLAuthorityTrue);
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

public sealed class ShadowPromotionCandidateProposal
{
    public string BestShadowCandidate { get; init; } = string.Empty;
    public double BestShadowCandidatePairwiseAccuracy { get; init; }
    public string TaskFamily { get; init; } = string.Empty;
    public IReadOnlyList<string> EligibilityReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Risks { get; init; } = Array.Empty<string>();
    public bool RuntimePromotionAllowed { get; init; }
    public bool RequiresSeparatePromotionGate { get; init; } = true;
    public bool RequiresHumanApproval { get; init; } = true;
    public string PromotionMode { get; init; } = "ShadowOnlyProposalForOfflineHumanReview";
    public bool ShadowOnly { get; init; } = true;
    public bool MLAuthority { get; init; }
    public bool LLMAuthority { get; init; }
    public bool RuntimeAuthority { get; init; }
    public bool GateAuthority { get; init; }
}

public sealed class RouterPromotionReadinessAssessment
{
    public bool RouterPromotionReady { get; init; }
    public bool RouterRepairRequired { get; init; } = true;
    public double BestRouterBaselineAccuracy { get; init; }
    public string BestRouterBaselineName { get; init; } = string.Empty;
    public IReadOnlyList<string> BlockingReasons { get; init; } = Array.Empty<string>();
    public string RepairPlanReference { get; init; } = "learning/v9/router-intent-repair-plan.json";
    public bool RuntimeRouterChanged { get; init; }
}

public sealed class HumanReviewQueueEntry
{
    public string ReviewId { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string TaskFamily { get; init; } = string.Empty;
    public string Priority { get; init; } = string.Empty;
    public string ProposedAction { get; init; } = string.Empty;
    public string ReferenceArtifact { get; init; } = string.Empty;
    public bool HumanReviewRequired { get; init; } = true;
    public bool AutoIngest { get; init; }
    public string CreatedAt { get; init; } = string.Empty;
}

public sealed class ControlledPilotDesign
{
    public string PilotMode { get; init; } = "ShadowOnlyCanaryDesign";
    public string Scope { get; init; } = "demo-workspace/demo-collection";
    public string Capability { get; init; } = "FormalRetrievalActivation";
    public bool RuntimeRerankerChanged { get; init; }
    public bool RuntimeRouterChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool KillSwitchRequired { get; init; } = true;
    public bool RollbackRequired { get; init; } = true;
    public bool ManualPromotionRequired { get; init; } = true;
    public bool RequiresSeparatePromotionGate { get; init; } = true;
    public IReadOnlyList<string> CanaryStages { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> EntryCriteria { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExitCriteria { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ObservabilityRequirements { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AbortConditions { get; init; } = Array.Empty<string>();
    public string ExpectedNextGate { get; init; } = "V10ControlledRuntimePilotGate";
}

public sealed class PromotionSafetyContract
{
    public bool MLAuthority { get; init; }
    public bool LLMAuthority { get; init; }
    public bool RuntimeAuthority { get; init; }
    public bool GateAuthority { get; init; }
    public bool RuntimePromotionAllowed { get; init; }
    public bool RequiresSeparatePromotionGate { get; init; } = true;
    public bool RequiresHumanApproval { get; init; } = true;
    public bool AutoIngest { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public string ContractVersion { get; init; } = "v9.7-promotion-safety/v1";
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed record LearningShadowPromotionReadinessPackContext
{
    public bool FailureFeedbackPackPresent { get; init; }
    public bool FailureFeedbackPackPassed { get; init; }
    public bool ShadowImplementationPackPresent { get; init; }
    public bool ShadowComparisonSummaryPresent { get; init; }
    public bool HardNegativeCandidatesPresent { get; init; }
    public int HardNegativeCandidateCount { get; init; }
    public bool FeedbackContractPresent { get; init; }
    public bool RouterIntentRepairPlanPresent { get; init; }
    public bool V8ScopedActivationPreserved { get; init; }
    public string BestShadowCandidate { get; init; } = string.Empty;
    public double BestShadowCandidatePairwiseAccuracy { get; init; }
    public string BestRouterCandidate { get; init; } = string.Empty;
    public double BestRouterAccuracy { get; init; }
    // Synthetic test knobs
    public bool HumanReviewRequiredOverride { get; init; } = true;
    public bool AutoIngestOverride { get; init; }
    public bool RuntimePromotionAllowedOverride { get; init; }
    public bool RequiresSeparatePromotionGateOverride { get; init; } = true;
    public bool RuntimeAuthorityOverride { get; init; }
    public bool GateAuthorityOverride { get; init; }
    public bool LLMAuthorityOverride { get; init; }
    public bool MLAuthorityOverride { get; init; }
    public bool RuntimeRerankerChangedOverride { get; init; }
    public bool RuntimeRouterChangedOverride { get; init; }
    public bool PackageOutputChangedOverride { get; init; }
    public bool FormalPackageWrittenOverride { get; init; }
    public bool GlobalDefaultOnOverride { get; init; }
}

public sealed class LearningShadowPromotionReadinessPackDecision
{
    public string Status { get; init; } = LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked;
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;
}

public static class LearningShadowPromotionReadinessPackPolicy
{
    public static LearningShadowPromotionReadinessPackDecision Evaluate(
        LearningShadowPromotionReadinessPackContext ctx,
        bool rtPassed, bool p15Passed,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent)
    {
        var blocked = new List<string>();
        if (!ctx.FailureFeedbackPackPresent) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.FailureFeedbackPackMissing);
        else if (!ctx.FailureFeedbackPackPassed) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.FailureFeedbackPackNotPassed);
        if (!ctx.ShadowImplementationPackPresent) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.ShadowImplementationPackMissing);
        if (!ctx.ShadowComparisonSummaryPresent) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.ShadowComparisonSummaryMissing);
        if (!ctx.HardNegativeCandidatesPresent || ctx.HardNegativeCandidateCount <= 0) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.HardNegativeCandidatesMissing);
        if (!ctx.FeedbackContractPresent) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.FeedbackContractMissing);
        if (!ctx.HumanReviewRequiredOverride) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.HumanReviewRequiredFalse);
        if (ctx.AutoIngestOverride) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.AutoIngestTrue);
        if (ctx.RuntimePromotionAllowedOverride) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.RuntimePromotionAllowedTrue);
        if (!ctx.RequiresSeparatePromotionGateOverride) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.RequiresSeparatePromotionGateFalse);
        if (ctx.RuntimeAuthorityOverride) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.RuntimeAuthorityTrue);
        if (ctx.GateAuthorityOverride) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.GateAuthorityTrue);
        if (ctx.LLMAuthorityOverride) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.LLMAuthorityTrue);
        if (ctx.MLAuthorityOverride) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.MLAuthorityTrue);
        if (ctx.RuntimeRerankerChangedOverride) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.RuntimeRerankerChangedTrue);
        if (ctx.RuntimeRouterChangedOverride) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.RuntimeRouterChangedTrue);
        if (ctx.PackageOutputChangedOverride) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.PackageOutputChangedTrue);
        if (ctx.FormalPackageWrittenOverride) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.FormalPackageWrittenTrue);
        if (ctx.GlobalDefaultOnOverride) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.GlobalDefaultOnTrue);
        if (!ctx.V8ScopedActivationPreserved) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.V8ScopedActivationLost);
        if (!rtPassed) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.MainlineTrustRegistryPresent);
        var finalBlocked = blocked.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        var ready = finalBlocked.Length == 0;
        return new LearningShadowPromotionReadinessPackDecision
        {
            Status = ready
                ? LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackReady
                : LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked,
            BlockedReasons = finalBlocked,
            Reasoning = ready
                ? "shadow promotion readiness pack policy ready — all upstream gates and authority invariants satisfied; outputs are proposals only, no runtime promotion."
                : $"{finalBlocked.Length} blocked reason(s); shadow promotion readiness pack blocked."
        };
    }
}

public sealed record LearningShadowPromotionReadinessPackScenario(
    string CaseName,
    LearningShadowPromotionReadinessPackContext Context,
    bool RtPassed, bool P15Passed,
    bool MainlineEvidencePresent, bool MainlineRegistryPresent,
    string ExpectedStatus, string? ExpectedBlockedReason);

public sealed class LearningShadowPromotionReadinessPackRunner
{
    private static readonly JsonSerializerOptions WriteIndented = new() { WriteIndented = true };

    public LearningShadowPromotionReadinessPackReport Run(
        LearningShadowPromotionReadinessPackContext realContext,
        string outputDir,
        int hardNegativeCandidateCount,
        IReadOnlyList<string> failureClusterIds,
        IReadOnlyList<string> routerRepairUnderrepLabels,
        bool rtPassed, bool p15Passed,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent,
        LearningShadowPromotionReadinessPackOptions? opt = null)
    {
        opt ??= new LearningShadowPromotionReadinessPackOptions();
        var now = DateTimeOffset.UtcNow;

        var cases = BuildScenarios().Select(static scenario =>
        {
            var decision = LearningShadowPromotionReadinessPackPolicy.Evaluate(
                scenario.Context, scenario.RtPassed, scenario.P15Passed,
                scenario.MainlineEvidencePresent, scenario.MainlineRegistryPresent);
            var statusMatched = string.Equals(scenario.ExpectedStatus, decision.Status, StringComparison.Ordinal);
            var blockedReasonMatched = scenario.ExpectedBlockedReason is null
                || decision.BlockedReasons.Contains(scenario.ExpectedBlockedReason, StringComparer.Ordinal);
            return new LearningShadowPromotionReadinessPackCase
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
        if (cases.Length < 20) blocked.Add("InsufficientLearningShadowPromotionReadinessPackCases");
        if (cases.Any(static c => !c.PassedAsExpected)) blocked.Add("LearningShadowPromotionReadinessPackMatrixFailed");
        foreach (var status in new[] {
            LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackReady,
            LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked })
        {
            if (!cases.Any(c => string.Equals(c.ActualStatus, status, StringComparison.Ordinal)))
                blocked.Add($"StatusBranchNotCovered:{status}");
        }

        var realDecision = LearningShadowPromotionReadinessPackPolicy.Evaluate(
            realContext, rtPassed, p15Passed, mainlineEvidencePresent, mainlineRegistryPresent);
        if (!string.Equals(realDecision.Status, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackReady, StringComparison.Ordinal))
            blocked.AddRange(realDecision.BlockedReasons.Select(static x => $"RealLearningShadowPromotionReadinessPack:{x}"));
        if (!rtPassed) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(LearningShadowPromotionReadinessPackBlockedReasons.MainlineTrustRegistryPresent);

        // ─── build artifacts only if real flow ready
        var canBuild = string.Equals(realDecision.Status, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackReady, StringComparison.Ordinal);
        ShadowPromotionCandidateProposal proposal = new();
        RouterPromotionReadinessAssessment routerAssessment = new();
        var humanReviewQueue = new List<HumanReviewQueueEntry>();
        ControlledPilotDesign pilotDesign = new();
        PromotionSafetyContract safetyContract = new();
        var proposalPath = string.Empty;
        var queuePath = string.Empty;
        var pilotPath = string.Empty;

        if (canBuild)
        {
            proposal = BuildPromotionProposal(realContext);
            proposalPath = Path.Combine(outputDir, "shadow-promotion-candidate-proposal.json");
            File.WriteAllText(proposalPath, JsonSerializer.Serialize(proposal, WriteIndented), new UTF8Encoding(true));

            routerAssessment = BuildRouterAssessment(realContext);

            humanReviewQueue = BuildHumanReviewQueue(hardNegativeCandidateCount, failureClusterIds, routerRepairUnderrepLabels, now);
            queuePath = Path.Combine(outputDir, "human-review-queue-plan.jsonl");
            WriteQueueJsonl(queuePath, humanReviewQueue);

            pilotDesign = BuildPilotDesign();
            pilotPath = Path.Combine(outputDir, "controlled-pilot-design.json");
            File.WriteAllText(pilotPath, JsonSerializer.Serialize(pilotDesign, WriteIndented), new UTF8Encoding(true));

            safetyContract = BuildSafetyContract();
        }

        // ─── verify authority invariants on every produced artifact
        if (proposal.MLAuthority || proposal.LLMAuthority || proposal.RuntimeAuthority || proposal.GateAuthority || proposal.RuntimePromotionAllowed)
            blocked.Add("PromotionProposalAuthorityLeak");
        if (routerAssessment.RuntimeRouterChanged) blocked.Add("RouterAssessmentRuntimeRouterChangedLeak");
        if (routerAssessment.RouterPromotionReady && routerAssessment.BestRouterBaselineAccuracy < 0.85)
            blocked.Add("RouterPromotionReadyInconsistent");
        foreach (var q in humanReviewQueue)
        {
            if (!q.HumanReviewRequired) blocked.Add($"HumanReviewQueueHumanReviewMissing:{q.ReviewId}");
            if (q.AutoIngest) blocked.Add($"HumanReviewQueueAutoIngestLeak:{q.ReviewId}");
        }
        if (pilotDesign.RuntimeRerankerChanged || pilotDesign.RuntimeRouterChanged || pilotDesign.PackageOutputChanged
            || pilotDesign.FormalPackageWritten || pilotDesign.GlobalDefaultOn) blocked.Add("PilotDesignRuntimeLeak");
        if (!pilotDesign.KillSwitchRequired || !pilotDesign.RollbackRequired || !pilotDesign.ManualPromotionRequired)
            blocked.Add("PilotDesignSafetyContractIncomplete");
        if (safetyContract.MLAuthority || safetyContract.LLMAuthority || safetyContract.RuntimeAuthority
            || safetyContract.GateAuthority || safetyContract.RuntimePromotionAllowed
            || safetyContract.AutoIngest || safetyContract.PackageOutputChanged
            || safetyContract.FormalPackageWritten || safetyContract.GlobalDefaultOn)
            blocked.Add("SafetyContractAuthorityLeak");
        if (!safetyContract.RequiresSeparatePromotionGate || !safetyContract.RequiresHumanApproval)
            blocked.Add("SafetyContractGuardrailIncomplete");

        var finalBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var passed = finalBlocked.Length == 0;

        return new LearningShadowPromotionReadinessPackReport
        {
            OperationId = $"v9-learning-shadow-promotion-readiness-pack-{Guid.NewGuid():N}",
            CreatedAt = now,
            ShadowPromotionReadinessPackPassed = passed,
            GatePassed = opt.IsGate && passed,
            TotalCases = cases.Length,
            PassedCases = cases.Count(static c => c.PassedAsExpected),
            FailedCases = cases.Count(static c => !c.PassedAsExpected),
            ReadyCases = cases.Count(c => string.Equals(c.ActualStatus, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackReady, StringComparison.Ordinal)),
            BlockedCases = cases.Count(c => string.Equals(c.ActualStatus, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked, StringComparison.Ordinal)),
            Cases = cases,
            ShadowPromotionCandidateProposal = proposal,
            RouterPromotionReadinessAssessment = routerAssessment,
            HumanReviewQueue = humanReviewQueue,
            ControlledPilotDesign = pilotDesign,
            PromotionSafetyContract = safetyContract,
            CandidatePromotionProposalReady = canBuild && !string.IsNullOrEmpty(proposal.BestShadowCandidate),
            BestShadowCandidate = proposal.BestShadowCandidate,
            BestShadowCandidatePairwiseAccuracy = proposal.BestShadowCandidatePairwiseAccuracy,
            RouterPromotionReady = routerAssessment.RouterPromotionReady,
            RouterRepairRequired = routerAssessment.RouterRepairRequired,
            HumanReviewQueuePlanReady = canBuild && humanReviewQueue.Count > 0,
            ControlledPilotDesignReady = canBuild,
            RuntimePromotionAllowed = false,
            RequiresSeparatePromotionGate = true,
            RequiresHumanApproval = true,
            HumanReviewRequired = true,
            AutoIngest = false,
            MLAuthority = false,
            LLMAuthority = false,
            RuntimeAuthority = false,
            GateAuthority = false,
            RuntimeRerankerChanged = false,
            RuntimeRouterChanged = false,
            PackageOutputChanged = false,
            FormalPackageWritten = false,
            GlobalDefaultOn = false,
            V8ScopedActivationPreserved = realContext.V8ScopedActivationPreserved,
            UpstreamFailureFeedbackPackGatePresent = realContext.FailureFeedbackPackPresent,
            UpstreamFailureFeedbackPackGatePassed = realContext.FailureFeedbackPackPassed,
            UpstreamShadowImplementationPackGatePresent = realContext.ShadowImplementationPackPresent,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent,
            ShadowPromotionCandidateProposalPath = proposalPath,
            HumanReviewQueuePlanPath = queuePath,
            ControlledPilotDesignPath = pilotPath,
            Recommendation = passed ? "ProceedToV10ControlledRuntimePilotGate" : "Blocked",
            NextAllowedPhase = passed ? "V10ControlledRuntimePilotGate" : string.Empty,
            BlockedReasons = finalBlocked,
            Diagnostics = new[]
            {
                $"total={cases.Length}",
                $"realStatus={realDecision.Status}",
                $"bestShadowCandidate={proposal.BestShadowCandidate}({proposal.BestShadowCandidatePairwiseAccuracy:F3})",
                $"routerPromotionReady={routerAssessment.RouterPromotionReady}",
                $"routerBestAccuracy={realContext.BestRouterAccuracy:F3}",
                $"queueEntries={humanReviewQueue.Count}",
                $"runtimeGate={rtPassed}",
                $"p15Gate={p15Passed}"
            }
        };
    }

    // ─── matrix scenarios (25 cases) ────────────────────────────────────────────
    private static IReadOnlyList<LearningShadowPromotionReadinessPackScenario> BuildScenarios()
    {
        var clean = BuildCleanContext();
        return [
            new("AllUpstreamClean", clean, true, true, false, false, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackReady, null),
            new("FailureFeedbackPackMissing", clean with { FailureFeedbackPackPresent = false }, true, true, false, false, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked, LearningShadowPromotionReadinessPackBlockedReasons.FailureFeedbackPackMissing),
            new("FailureFeedbackPackNotPassed", clean with { FailureFeedbackPackPassed = false }, true, true, false, false, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked, LearningShadowPromotionReadinessPackBlockedReasons.FailureFeedbackPackNotPassed),
            new("ShadowImplementationPackMissing", clean with { ShadowImplementationPackPresent = false }, true, true, false, false, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked, LearningShadowPromotionReadinessPackBlockedReasons.ShadowImplementationPackMissing),
            new("ShadowComparisonSummaryMissing", clean with { ShadowComparisonSummaryPresent = false }, true, true, false, false, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked, LearningShadowPromotionReadinessPackBlockedReasons.ShadowComparisonSummaryMissing),
            new("HardNegativeCandidatesMissing", clean with { HardNegativeCandidatesPresent = false, HardNegativeCandidateCount = 0 }, true, true, false, false, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked, LearningShadowPromotionReadinessPackBlockedReasons.HardNegativeCandidatesMissing),
            new("FeedbackContractMissing", clean with { FeedbackContractPresent = false }, true, true, false, false, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked, LearningShadowPromotionReadinessPackBlockedReasons.FeedbackContractMissing),
            new("HumanReviewRequiredFalse", clean with { HumanReviewRequiredOverride = false }, true, true, false, false, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked, LearningShadowPromotionReadinessPackBlockedReasons.HumanReviewRequiredFalse),
            new("AutoIngestTrue", clean with { AutoIngestOverride = true }, true, true, false, false, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked, LearningShadowPromotionReadinessPackBlockedReasons.AutoIngestTrue),
            new("RuntimePromotionAllowedTrue", clean with { RuntimePromotionAllowedOverride = true }, true, true, false, false, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked, LearningShadowPromotionReadinessPackBlockedReasons.RuntimePromotionAllowedTrue),
            new("RequiresSeparatePromotionGateFalse", clean with { RequiresSeparatePromotionGateOverride = false }, true, true, false, false, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked, LearningShadowPromotionReadinessPackBlockedReasons.RequiresSeparatePromotionGateFalse),
            new("RuntimeAuthorityTrue", clean with { RuntimeAuthorityOverride = true }, true, true, false, false, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked, LearningShadowPromotionReadinessPackBlockedReasons.RuntimeAuthorityTrue),
            new("GateAuthorityTrue", clean with { GateAuthorityOverride = true }, true, true, false, false, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked, LearningShadowPromotionReadinessPackBlockedReasons.GateAuthorityTrue),
            new("LLMAuthorityTrue", clean with { LLMAuthorityOverride = true }, true, true, false, false, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked, LearningShadowPromotionReadinessPackBlockedReasons.LLMAuthorityTrue),
            new("MLAuthorityTrue", clean with { MLAuthorityOverride = true }, true, true, false, false, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked, LearningShadowPromotionReadinessPackBlockedReasons.MLAuthorityTrue),
            new("RuntimeRerankerChangedTrue", clean with { RuntimeRerankerChangedOverride = true }, true, true, false, false, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked, LearningShadowPromotionReadinessPackBlockedReasons.RuntimeRerankerChangedTrue),
            new("RuntimeRouterChangedTrue", clean with { RuntimeRouterChangedOverride = true }, true, true, false, false, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked, LearningShadowPromotionReadinessPackBlockedReasons.RuntimeRouterChangedTrue),
            new("PackageOutputChangedTrue", clean with { PackageOutputChangedOverride = true }, true, true, false, false, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked, LearningShadowPromotionReadinessPackBlockedReasons.PackageOutputChangedTrue),
            new("FormalPackageWrittenTrue", clean with { FormalPackageWrittenOverride = true }, true, true, false, false, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked, LearningShadowPromotionReadinessPackBlockedReasons.FormalPackageWrittenTrue),
            new("GlobalDefaultOnTrue", clean with { GlobalDefaultOnOverride = true }, true, true, false, false, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked, LearningShadowPromotionReadinessPackBlockedReasons.GlobalDefaultOnTrue),
            new("V8ScopedActivationLost", clean with { V8ScopedActivationPreserved = false }, true, true, false, false, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked, LearningShadowPromotionReadinessPackBlockedReasons.V8ScopedActivationLost),
            new("RuntimeGateNotPassed", clean, false, true, false, false, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked, LearningShadowPromotionReadinessPackBlockedReasons.RuntimeChangeGateNotPassed),
            new("P15GateNotPassed", clean, true, false, false, false, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked, LearningShadowPromotionReadinessPackBlockedReasons.P15GateNotPassed),
            new("MainlineEvidencePresent", clean, true, true, true, false, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked, LearningShadowPromotionReadinessPackBlockedReasons.MainlineEvidencePresent),
            new("MainlineTrustRegistryPresent", clean, true, true, false, true, LearningShadowPromotionReadinessPackStatuses.LearningShadowPromotionReadinessPackBlocked, LearningShadowPromotionReadinessPackBlockedReasons.MainlineTrustRegistryPresent)
        ];
    }

    private static LearningShadowPromotionReadinessPackContext BuildCleanContext() => new()
    {
        FailureFeedbackPackPresent = true,
        FailureFeedbackPackPassed = true,
        ShadowImplementationPackPresent = true,
        ShadowComparisonSummaryPresent = true,
        HardNegativeCandidatesPresent = true,
        HardNegativeCandidateCount = 60,
        FeedbackContractPresent = true,
        RouterIntentRepairPlanPresent = true,
        V8ScopedActivationPreserved = true,
        BestShadowCandidate = "LogisticBaseline",
        BestShadowCandidatePairwiseAccuracy = 1.0,
        BestRouterCandidate = "RouterIntentLogistic",
        BestRouterAccuracy = 0.121,
        HumanReviewRequiredOverride = true,
        RequiresSeparatePromotionGateOverride = true
    };

    // ─── builders ───

    private static ShadowPromotionCandidateProposal BuildPromotionProposal(LearningShadowPromotionReadinessPackContext ctx)
    {
        var eligibility = new List<string>();
        var risks = new List<string>();
        if (ctx.BestShadowCandidatePairwiseAccuracy >= 0.95) eligibility.Add($"pairwiseAccuracy={ctx.BestShadowCandidatePairwiseAccuracy:F3} ≥ 0.95 threshold");
        else risks.Add($"pairwiseAccuracy={ctx.BestShadowCandidatePairwiseAccuracy:F3} suspiciously high — investigate signal leakage before any promotion");
        eligibility.Add("Failure clusters analyzed and documented in V9.4 failure-diagnosis-input-pack");
        eligibility.Add($"Hard-negative expansion candidates available ({ctx.HardNegativeCandidateCount} specs)");
        eligibility.Add("Feedback ingestion contract published (V9.5 schema)");
        risks.Add("LogisticBaseline 100% accuracy likely driven by positiveScore feature dominance; promote only after V9.4 hard-negative expansion + V10 canary observation");
        risks.Add("Test set is small (n=58); confidence intervals wide");
        risks.Add("No production traffic exposure yet — pilot must be scoped");
        return new ShadowPromotionCandidateProposal
        {
            BestShadowCandidate = ctx.BestShadowCandidate,
            BestShadowCandidatePairwiseAccuracy = ctx.BestShadowCandidatePairwiseAccuracy,
            TaskFamily = "CandidateReranker",
            EligibilityReasons = eligibility,
            Risks = risks,
            RuntimePromotionAllowed = false,
            RequiresSeparatePromotionGate = true,
            RequiresHumanApproval = true,
            PromotionMode = "ShadowOnlyProposalForOfflineHumanReview",
            ShadowOnly = true,
            MLAuthority = false,
            LLMAuthority = false,
            RuntimeAuthority = false,
            GateAuthority = false
        };
    }

    private static RouterPromotionReadinessAssessment BuildRouterAssessment(LearningShadowPromotionReadinessPackContext ctx)
    {
        var blocking = new List<string>();
        if (ctx.BestRouterAccuracy < 0.85) blocking.Add($"BestRouterAccuracy={ctx.BestRouterAccuracy:F3} below 0.85 promotion threshold");
        blocking.Add("Router dataset has poor intent discriminability (most examples selected/accepted)");
        blocking.Add("Router intent repair plan generated; see learning/v9/router-intent-repair-plan.json");
        return new RouterPromotionReadinessAssessment
        {
            RouterPromotionReady = false,
            RouterRepairRequired = true,
            BestRouterBaselineAccuracy = ctx.BestRouterAccuracy,
            BestRouterBaselineName = ctx.BestRouterCandidate,
            BlockingReasons = blocking,
            RepairPlanReference = "learning/v9/router-intent-repair-plan.json",
            RuntimeRouterChanged = false
        };
    }

    private static List<HumanReviewQueueEntry> BuildHumanReviewQueue(
        int hardNegativeCount, IReadOnlyList<string> failureClusterIds,
        IReadOnlyList<string> routerRepairUnderrepLabels, DateTimeOffset now)
    {
        var queue = new List<HumanReviewQueueEntry>();
        int idx = 0;
        // Hard-negative review entries (one per 10 candidates as bulk batches)
        var batchCount = Math.Max(1, hardNegativeCount / 10);
        for (int b = 0; b < batchCount; b++)
        {
            queue.Add(new HumanReviewQueueEntry
            {
                ReviewId = $"hrq-{idx++:D4}-hardneg-batch-{b:D2}",
                Source = "V9.4HardNegativeExpansion",
                TaskFamily = "CandidateReranker",
                Priority = b < 2 ? "High" : "Medium",
                ProposedAction = $"Review batch {b} of hard-negative candidates (10 specs); approve / reject per candidate before V9.5 ingestion",
                ReferenceArtifact = "learning/v9/hard-negative-expansion-candidates.jsonl",
                HumanReviewRequired = true,
                AutoIngest = false,
                CreatedAt = now.ToString("O")
            });
        }
        // Failure cluster review entries
        foreach (var cluster in failureClusterIds.OrderBy(static c => c, StringComparer.Ordinal))
        {
            queue.Add(new HumanReviewQueueEntry
            {
                ReviewId = $"hrq-{idx++:D4}-cluster-{cluster}",
                Source = "V9.4FailureDiagnosisInputPack",
                TaskFamily = cluster.StartsWith("router-", StringComparison.Ordinal) ? "RouterIntentClassifier" : "CandidateReranker",
                Priority = cluster.StartsWith("router-", StringComparison.Ordinal) ? "High" : "Medium",
                ProposedAction = $"Review failure cluster {cluster}; classify root cause; propose dataset / feature additions",
                ReferenceArtifact = "learning/v9/failure-diagnosis-input-pack.json",
                HumanReviewRequired = true,
                AutoIngest = false,
                CreatedAt = now.ToString("O")
            });
        }
        // Router repair entries (one per underrepresented label)
        foreach (var label in routerRepairUnderrepLabels.OrderBy(static c => c, StringComparer.Ordinal))
        {
            queue.Add(new HumanReviewQueueEntry
            {
                ReviewId = $"hrq-{idx++:D4}-router-underrep-{NormalizeId(label)}",
                Source = "V9.4RouterIntentRepairPlan",
                TaskFamily = "RouterIntentClassifier",
                Priority = "High",
                ProposedAction = $"Generate ≥10 new examples for underrepresented label `{label}`; obtain human-labeled ground truth",
                ReferenceArtifact = "learning/v9/router-intent-repair-plan.json",
                HumanReviewRequired = true,
                AutoIngest = false,
                CreatedAt = now.ToString("O")
            });
        }
        return queue;
    }

    private static string NormalizeId(string s) => new string(s.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());

    private static void WriteQueueJsonl(string path, IReadOnlyList<HumanReviewQueueEntry> queue)
    {
        var sb = new StringBuilder();
        foreach (var q in queue)
        {
            sb.Append('{');
            sb.Append("\"reviewId\":\"").Append(q.ReviewId).Append("\",");
            sb.Append("\"source\":\"").Append(q.Source).Append("\",");
            sb.Append("\"taskFamily\":\"").Append(q.TaskFamily).Append("\",");
            sb.Append("\"priority\":\"").Append(q.Priority).Append("\",");
            sb.Append("\"proposedAction\":\"").Append(Escape(q.ProposedAction)).Append("\",");
            sb.Append("\"referenceArtifact\":\"").Append(q.ReferenceArtifact).Append("\",");
            sb.Append("\"humanReviewRequired\":").Append(q.HumanReviewRequired ? "true" : "false").Append(',');
            sb.Append("\"autoIngest\":").Append(q.AutoIngest ? "true" : "false").Append(',');
            sb.Append("\"createdAt\":\"").Append(q.CreatedAt).Append('"');
            sb.Append('}');
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static ControlledPilotDesign BuildPilotDesign()
        => new()
        {
            PilotMode = "ShadowOnlyCanaryDesign",
            Scope = "demo-workspace/demo-collection",
            Capability = "FormalRetrievalActivation",
            RuntimeRerankerChanged = false,
            RuntimeRouterChanged = false,
            PackageOutputChanged = false,
            FormalPackageWritten = false,
            GlobalDefaultOn = false,
            KillSwitchRequired = true,
            RollbackRequired = true,
            ManualPromotionRequired = true,
            RequiresSeparatePromotionGate = true,
            CanaryStages = new[]
            {
                "Stage 0 — Offline replay on held-out test set (V9.1-V9.3 artefact); human reviewer sign-off",
                "Stage 1 — Shadow inference alongside production reranker; emit shadow scores to audit log only",
                "Stage 2 — Limited canary (≤1% of demo-workspace/demo-collection queries); shadow scores compared, no decision change",
                "Stage 3 — Full canary in demo-workspace/demo-collection scope only; production traffic unaffected outside scope",
                "Stage 4 — Manual promotion gate (V10ControlledRuntimePilotGate); human reviewer + V8-style guarded scoped activation"
            },
            EntryCriteria = new[]
            {
                "V9.7 readiness pack passed",
                "Hard-negative expansion ≥50 + ≥50% human-reviewed",
                "Router repair plan executed for top 3 underrepresented labels",
                "Failure clusters reviewed; root causes documented",
                "V8 scoped activation still preserved (state=Active, no mainline leak)"
            },
            ExitCriteria = new[]
            {
                "Stage abort criteria triggered",
                "Manual abort by human reviewer",
                "Any safety invariant violated (RuntimeAuthority, PackageOutputChanged, GlobalDefaultOn)",
                "V8 scoped activation degraded"
            },
            ObservabilityRequirements = new[]
            {
                "Per-query shadow score logged with reproducible request id",
                "Per-stage error-rate / latency / coverage delta tracked",
                "Failure cluster drift detector (compare with V9.4 baseline clusters)",
                "Per-mode / per-intent accuracy heatmap"
            },
            AbortConditions = new[]
            {
                "pairwiseAccuracy drops below baseline rule reranker by >2 absolute points on the demo scope",
                "Router intent accuracy drops below 50% within scope",
                "Any RuntimeAuthority / GateAuthority leak detected",
                "Any PackageOutputChanged / FormalPackageWritten observed",
                "Mainline evidence / trust registry written without separate approval"
            },
            ExpectedNextGate = "V10ControlledRuntimePilotGate"
        };

    private static PromotionSafetyContract BuildSafetyContract()
        => new()
        {
            MLAuthority = false,
            LLMAuthority = false,
            RuntimeAuthority = false,
            GateAuthority = false,
            RuntimePromotionAllowed = false,
            RequiresSeparatePromotionGate = true,
            RequiresHumanApproval = true,
            AutoIngest = false,
            PackageOutputChanged = false,
            FormalPackageWritten = false,
            GlobalDefaultOn = false,
            ContractVersion = "v9.7-promotion-safety/v1",
            Notes = new[]
            {
                "ML / LLM produce proposals only; they never decide promotion outcomes.",
                "Every promotion step requires a separate gate authored by V10ControlledRuntimePilotGate or later.",
                "Human approval is required at every promotion boundary; not even a quorum of shadow baselines can bypass it.",
                "Package output, formal package, and vector store bindings are immutable from V9 — only V8.x guarded scoped activation may modify them inside the demo scope.",
                "Global default-on is reserved for a future V11+ phase under explicit operator sign-off; V9 must never enable it."
            }
        };

    // ─── markdown ───
    public static string BuildMarkdown(string title, LearningShadowPromotionReadinessPackReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"- ShadowPromotionReadinessPackPassed: `{report.ShadowPromotionReadinessPackPassed}`");
        sb.AppendLine($"- GatePassed: `{report.GatePassed}`");
        sb.AppendLine($"- TotalCases: `{report.TotalCases}` PassedCases: `{report.PassedCases}` FailedCases: `{report.FailedCases}`");
        sb.AppendLine($"- ReadyCases: `{report.ReadyCases}` BlockedCases: `{report.BlockedCases}`");
        sb.AppendLine();
        sb.AppendLine("## Authority Invariants");
        sb.AppendLine($"- MLAuthority: `{report.MLAuthority}` LLMAuthority: `{report.LLMAuthority}` RuntimeAuthority: `{report.RuntimeAuthority}` GateAuthority: `{report.GateAuthority}`");
        sb.AppendLine($"- RuntimePromotionAllowed: `{report.RuntimePromotionAllowed}` RequiresSeparatePromotionGate: `{report.RequiresSeparatePromotionGate}` RequiresHumanApproval: `{report.RequiresHumanApproval}`");
        sb.AppendLine($"- HumanReviewRequired: `{report.HumanReviewRequired}` AutoIngest: `{report.AutoIngest}`");
        sb.AppendLine($"- RuntimeRerankerChanged: `{report.RuntimeRerankerChanged}` RuntimeRouterChanged: `{report.RuntimeRouterChanged}`");
        sb.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}` FormalPackageWritten: `{report.FormalPackageWritten}` GlobalDefaultOn: `{report.GlobalDefaultOn}`");
        sb.AppendLine($"- V8ScopedActivationPreserved: `{report.V8ScopedActivationPreserved}`");
        sb.AppendLine();
        sb.AppendLine("## Shadow Promotion Candidate Proposal");
        sb.AppendLine($"- BestShadowCandidate: `{report.BestShadowCandidate}` (pairwiseAccuracy={report.BestShadowCandidatePairwiseAccuracy:F3})");
        sb.AppendLine($"- CandidatePromotionProposalReady: `{report.CandidatePromotionProposalReady}`");
        sb.AppendLine("- Eligibility:");
        foreach (var e in report.ShadowPromotionCandidateProposal.EligibilityReasons) sb.AppendLine($"  - {e}");
        sb.AppendLine("- Risks:");
        foreach (var r in report.ShadowPromotionCandidateProposal.Risks) sb.AppendLine($"  - {r}");
        sb.AppendLine();
        sb.AppendLine("## Router Promotion Readiness");
        sb.AppendLine($"- RouterPromotionReady: `{report.RouterPromotionReady}` RouterRepairRequired: `{report.RouterRepairRequired}`");
        sb.AppendLine($"- BestRouterBaseline: `{report.RouterPromotionReadinessAssessment.BestRouterBaselineName}` accuracy={report.RouterPromotionReadinessAssessment.BestRouterBaselineAccuracy:F3}");
        sb.AppendLine("- Blocking Reasons:");
        foreach (var b in report.RouterPromotionReadinessAssessment.BlockingReasons) sb.AppendLine($"  - {b}");
        sb.AppendLine();
        sb.AppendLine("## Human Review Queue");
        sb.AppendLine($"- Entries: `{report.HumanReviewQueue.Count}` (path: `{report.HumanReviewQueuePlanPath}`)");
        sb.AppendLine($"- All entries HumanReviewRequired=true / AutoIngest=false");
        sb.AppendLine();
        sb.AppendLine("## Controlled Pilot Design");
        sb.AppendLine($"- PilotMode: `{report.ControlledPilotDesign.PilotMode}` Scope: `{report.ControlledPilotDesign.Scope}`");
        sb.AppendLine($"- KillSwitchRequired: `{report.ControlledPilotDesign.KillSwitchRequired}` RollbackRequired: `{report.ControlledPilotDesign.RollbackRequired}` ManualPromotionRequired: `{report.ControlledPilotDesign.ManualPromotionRequired}`");
        sb.AppendLine($"- CanaryStages: `{report.ControlledPilotDesign.CanaryStages.Count}` EntryCriteria: `{report.ControlledPilotDesign.EntryCriteria.Count}` AbortConditions: `{report.ControlledPilotDesign.AbortConditions.Count}`");
        sb.AppendLine($"- ExpectedNextGate: `{report.ControlledPilotDesign.ExpectedNextGate}`");
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

public sealed class LearningShadowPromotionReadinessPackCase
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

public sealed class LearningShadowPromotionReadinessPackReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ShadowPromotionReadinessPackPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public int ReadyCases { get; init; }
    public int BlockedCases { get; init; }
    public IReadOnlyList<LearningShadowPromotionReadinessPackCase> Cases { get; init; } = Array.Empty<LearningShadowPromotionReadinessPackCase>();
    public ShadowPromotionCandidateProposal ShadowPromotionCandidateProposal { get; init; } = new();
    public RouterPromotionReadinessAssessment RouterPromotionReadinessAssessment { get; init; } = new();
    public IReadOnlyList<HumanReviewQueueEntry> HumanReviewQueue { get; init; } = Array.Empty<HumanReviewQueueEntry>();
    public ControlledPilotDesign ControlledPilotDesign { get; init; } = new();
    public PromotionSafetyContract PromotionSafetyContract { get; init; } = new();
    public bool CandidatePromotionProposalReady { get; init; }
    public string BestShadowCandidate { get; init; } = string.Empty;
    public double BestShadowCandidatePairwiseAccuracy { get; init; }
    public bool RouterPromotionReady { get; init; }
    public bool RouterRepairRequired { get; init; }
    public bool HumanReviewQueuePlanReady { get; init; }
    public bool ControlledPilotDesignReady { get; init; }
    public bool RuntimePromotionAllowed { get; init; }
    public bool RequiresSeparatePromotionGate { get; init; }
    public bool RequiresHumanApproval { get; init; }
    public bool HumanReviewRequired { get; init; }
    public bool AutoIngest { get; init; }
    public bool MLAuthority { get; init; }
    public bool LLMAuthority { get; init; }
    public bool RuntimeAuthority { get; init; }
    public bool GateAuthority { get; init; }
    public bool RuntimeRerankerChanged { get; init; }
    public bool RuntimeRouterChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool V8ScopedActivationPreserved { get; init; }
    public bool UpstreamFailureFeedbackPackGatePresent { get; init; }
    public bool UpstreamFailureFeedbackPackGatePassed { get; init; }
    public bool UpstreamShadowImplementationPackGatePresent { get; init; }
    public bool MainlineEvidencePresent { get; init; }
    public bool MainlineTrustRegistryPresent { get; init; }
    public string ShadowPromotionCandidateProposalPath { get; init; } = string.Empty;
    public string HumanReviewQueuePlanPath { get; init; } = string.Empty;
    public string ControlledPilotDesignPath { get; init; } = string.Empty;
    public string Recommendation { get; init; } = string.Empty;
    public string NextAllowedPhase { get; init; } = string.Empty;
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class LearningShadowPromotionReadinessPackOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
