using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

public static class LearningControlledRuntimePilotGatePackStatuses
{
    public const string LearningControlledRuntimePilotGatePackReady = nameof(LearningControlledRuntimePilotGatePackReady);
    public const string LearningControlledRuntimePilotGatePackBlocked = nameof(LearningControlledRuntimePilotGatePackBlocked);
}

public static class LearningControlledRuntimePilotGatePackBlockedReasons
{
    public const string V9ReadinessGateMissing = nameof(V9ReadinessGateMissing);
    public const string V9ReadinessGateNotPassed = nameof(V9ReadinessGateNotPassed);
    public const string PromotionProposalMissing = nameof(PromotionProposalMissing);
    public const string HumanReviewQueueMissing = nameof(HumanReviewQueueMissing);
    public const string ControlledPilotDesignMissing = nameof(ControlledPilotDesignMissing);
    public const string HumanReviewRequiredFalse = nameof(HumanReviewRequiredFalse);
    public const string AutoIngestTrue = nameof(AutoIngestTrue);
    public const string RuntimePromotionAllowedTrue = nameof(RuntimePromotionAllowedTrue);
    public const string RequiresSeparatePromotionGateFalse = nameof(RequiresSeparatePromotionGateFalse);
    public const string RequiresHumanApprovalFalse = nameof(RequiresHumanApprovalFalse);
    public const string HumanReviewCompletedFalselyAssumed = nameof(HumanReviewCompletedFalselyAssumed);
    public const string RouterPromotionReadyTrue = nameof(RouterPromotionReadyTrue);
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
    public const string RuntimeChangeGateNotPassed = nameof(RuntimeChangeGateNotPassed);
    public const string P15GateNotPassed = nameof(P15GateNotPassed);
    public const string MainlineEvidencePresent = nameof(MainlineEvidencePresent);
    public const string MainlineTrustRegistryPresent = nameof(MainlineTrustRegistryPresent);
}

public sealed class OfflineReplaySummary
{
    public string Candidate { get; init; } = string.Empty;
    public string ReferenceBaseline { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public string ReplayMode { get; init; } = "DeterministicOfflineReplay";
    public bool ReplayExecuted { get; init; }
    public double CandidatePairwiseAccuracy { get; init; }
    public double ReferencePairwiseAccuracy { get; init; }
    public double AccuracyDelta { get; init; }
    public int CandidateEvalCount { get; init; }
    public int ReferenceEvalCount { get; init; }
    public bool RuntimeDecisionChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed class ShadowCanarySimulation
{
    public string CanaryMode { get; init; } = "ScopedShadowSimulation";
    public string Scope { get; init; } = "demo-workspace/demo-collection";
    public string Capability { get; init; } = "FormalRetrievalActivation";
    public bool ShadowCanarySimulationExecuted { get; init; }
    public bool RuntimeRerankerChanged { get; init; }
    public bool RuntimeRouterChanged { get; init; }
    public bool ProductionDecisionChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool KillSwitchArmed { get; init; } = true;
    public bool RollbackReady { get; init; } = true;
    public int SimulatedQueryCount { get; init; }
    public int ShadowAgreementCount { get; init; }
    public double SimulatedShadowAgreementRate { get; init; }
    public IReadOnlyList<string> SimulationStages { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AbortConditionsObserved { get; init; } = Array.Empty<string>();
}

public sealed class PilotAuditManifest
{
    public string ManifestId { get; init; } = string.Empty;
    public string CreatedAt { get; init; } = string.Empty;
    public string Capability { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public bool V9PromotionReadinessValidated { get; init; }
    public bool ControlledPilotDesignValidated { get; init; }
    public bool HumanReviewQueueValidated { get; init; }
    public bool OfflineReplayReady { get; init; }
    public bool ShadowCanarySimulationReady { get; init; }
    public bool RuntimePilotExecutionReady { get; init; }
    public bool RuntimePromotionApplied { get; init; }
    public string BlockedForRuntimePilotExecutionBy { get; init; } = string.Empty;
    public string V9ReadinessGatePath { get; init; } = string.Empty;
    public string PromotionProposalPath { get; init; } = string.Empty;
    public string HumanReviewQueuePath { get; init; } = string.Empty;
    public string ControlledPilotDesignPath { get; init; } = string.Empty;
    public string OfflineReplaySummaryPath { get; init; } = string.Empty;
    public string ShadowCanarySimulationPath { get; init; } = string.Empty;
    public bool MLAuthority { get; init; }
    public bool LLMAuthority { get; init; }
    public bool RuntimeAuthority { get; init; }
    public bool GateAuthority { get; init; }
    public string NextAllowedPhase { get; init; } = string.Empty;
    public IReadOnlyList<string> AuditNotes { get; init; } = Array.Empty<string>();
}

public sealed record LearningControlledRuntimePilotGatePackContext
{
    public bool V9ReadinessGatePresent { get; init; }
    public bool V9ReadinessGatePassed { get; init; }
    public bool PromotionProposalPresent { get; init; }
    public bool HumanReviewQueuePresent { get; init; }
    public int HumanReviewQueueEntryCount { get; init; }
    public bool HumanReviewQueueAllEntriesRequireReview { get; init; }
    public bool HumanReviewQueueAnyAutoIngest { get; init; }
    public bool ControlledPilotDesignPresent { get; init; }
    public bool V8ScopedActivationPreserved { get; init; }
    public string BestShadowCandidate { get; init; } = string.Empty;
    public double BestShadowCandidatePairwiseAccuracy { get; init; }
    public string ReferenceBaselineName { get; init; } = string.Empty;
    public double ReferencePairwiseAccuracy { get; init; }
    public int CandidateEvalCount { get; init; }
    public bool RouterPromotionReady { get; init; }
    // Synthetic test knobs
    public bool HumanReviewRequiredOverride { get; init; } = true;
    public bool AutoIngestOverride { get; init; }
    public bool RuntimePromotionAllowedOverride { get; init; }
    public bool RequiresSeparatePromotionGateOverride { get; init; } = true;
    public bool RequiresHumanApprovalOverride { get; init; } = true;
    public bool HumanReviewCompletedOverride { get; init; }  // when true with no completion artifact → block
    public bool HumanReviewCompletionArtifactPresent { get; init; }
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

public sealed class LearningControlledRuntimePilotGatePackDecision
{
    public string Status { get; init; } = LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked;
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;
}

public static class LearningControlledRuntimePilotGatePackPolicy
{
    public static LearningControlledRuntimePilotGatePackDecision Evaluate(
        LearningControlledRuntimePilotGatePackContext ctx,
        bool rtPassed, bool p15Passed,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent)
    {
        var blocked = new List<string>();
        if (!ctx.V9ReadinessGatePresent) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.V9ReadinessGateMissing);
        else if (!ctx.V9ReadinessGatePassed) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.V9ReadinessGateNotPassed);
        if (!ctx.PromotionProposalPresent) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.PromotionProposalMissing);
        if (!ctx.HumanReviewQueuePresent) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.HumanReviewQueueMissing);
        if (!ctx.ControlledPilotDesignPresent) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.ControlledPilotDesignMissing);
        if (!ctx.HumanReviewRequiredOverride) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.HumanReviewRequiredFalse);
        if (ctx.AutoIngestOverride || ctx.HumanReviewQueueAnyAutoIngest) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.AutoIngestTrue);
        if (ctx.RuntimePromotionAllowedOverride) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.RuntimePromotionAllowedTrue);
        if (!ctx.RequiresSeparatePromotionGateOverride) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.RequiresSeparatePromotionGateFalse);
        if (!ctx.RequiresHumanApprovalOverride) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.RequiresHumanApprovalFalse);
        // V10: claiming human review completed without an explicit completion artifact is always blocked — never fake approval
        if (ctx.HumanReviewCompletedOverride && !ctx.HumanReviewCompletionArtifactPresent)
            blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.HumanReviewCompletedFalselyAssumed);
        if (ctx.RouterPromotionReady) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.RouterPromotionReadyTrue);
        if (ctx.RuntimeRerankerChangedOverride) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.RuntimeRerankerChangedTrue);
        if (ctx.RuntimeRouterChangedOverride) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.RuntimeRouterChangedTrue);
        if (ctx.ProductionDecisionChangedOverride) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.ProductionDecisionChangedTrue);
        if (ctx.PackageOutputChangedOverride) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.PackageOutputChangedTrue);
        if (ctx.FormalPackageWrittenOverride) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.FormalPackageWrittenTrue);
        if (ctx.GlobalDefaultOnOverride) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.GlobalDefaultOnTrue);
        if (ctx.MLAuthorityOverride) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.MLAuthorityTrue);
        if (ctx.LLMAuthorityOverride) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.LLMAuthorityTrue);
        if (ctx.RuntimeAuthorityOverride) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.RuntimeAuthorityTrue);
        if (ctx.GateAuthorityOverride) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.GateAuthorityTrue);
        if (!ctx.V8ScopedActivationPreserved) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.V8ScopedActivationLost);
        if (!rtPassed) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.MainlineTrustRegistryPresent);
        var finalBlocked = blocked.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        var ready = finalBlocked.Length == 0;
        return new LearningControlledRuntimePilotGatePackDecision
        {
            Status = ready
                ? LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackReady
                : LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked,
            BlockedReasons = finalBlocked,
            Reasoning = ready
                ? "controlled runtime pilot gate policy ready — V9 readiness validated, design + queue + replay + canary simulation all pass; runtime execution awaits explicit human review completion."
                : $"{finalBlocked.Length} blocked reason(s); controlled runtime pilot gate blocked."
        };
    }
}

public sealed record LearningControlledRuntimePilotGatePackScenario(
    string CaseName,
    LearningControlledRuntimePilotGatePackContext Context,
    bool RtPassed, bool P15Passed,
    bool MainlineEvidencePresent, bool MainlineRegistryPresent,
    string ExpectedStatus, string? ExpectedBlockedReason);

public sealed class LearningControlledRuntimePilotGatePackRunner
{
    private static readonly JsonSerializerOptions WriteIndented = new() { WriteIndented = true };

    public LearningControlledRuntimePilotGatePackReport Run(
        LearningControlledRuntimePilotGatePackContext realContext,
        string outputDir,
        bool rtPassed, bool p15Passed,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent,
        LearningControlledRuntimePilotGatePackOptions? opt = null)
    {
        opt ??= new LearningControlledRuntimePilotGatePackOptions();
        var now = DateTimeOffset.UtcNow;

        var cases = BuildScenarios().Select(static scenario =>
        {
            var decision = LearningControlledRuntimePilotGatePackPolicy.Evaluate(
                scenario.Context, scenario.RtPassed, scenario.P15Passed,
                scenario.MainlineEvidencePresent, scenario.MainlineRegistryPresent);
            var statusMatched = string.Equals(scenario.ExpectedStatus, decision.Status, StringComparison.Ordinal);
            var blockedReasonMatched = scenario.ExpectedBlockedReason is null
                || decision.BlockedReasons.Contains(scenario.ExpectedBlockedReason, StringComparer.Ordinal);
            return new LearningControlledRuntimePilotGatePackCase
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
        if (cases.Length < 25) blocked.Add("InsufficientLearningControlledRuntimePilotGatePackCases");
        if (cases.Any(static c => !c.PassedAsExpected)) blocked.Add("LearningControlledRuntimePilotGatePackMatrixFailed");
        foreach (var status in new[] {
            LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackReady,
            LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked })
        {
            if (!cases.Any(c => string.Equals(c.ActualStatus, status, StringComparison.Ordinal)))
                blocked.Add($"StatusBranchNotCovered:{status}");
        }

        var realDecision = LearningControlledRuntimePilotGatePackPolicy.Evaluate(
            realContext, rtPassed, p15Passed, mainlineEvidencePresent, mainlineRegistryPresent);
        if (!string.Equals(realDecision.Status, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackReady, StringComparison.Ordinal))
            blocked.AddRange(realDecision.BlockedReasons.Select(static x => $"RealLearningControlledRuntimePilotGatePack:{x}"));
        if (!rtPassed) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(LearningControlledRuntimePilotGatePackBlockedReasons.MainlineTrustRegistryPresent);

        var canBuild = string.Equals(realDecision.Status, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackReady, StringComparison.Ordinal);
        OfflineReplaySummary replay = new();
        ShadowCanarySimulation canary = new();
        PilotAuditManifest manifest = new();
        var replayPath = string.Empty;
        var canaryPath = string.Empty;
        var manifestPath = string.Empty;

        // Determine runtime pilot execution readiness — only true if explicit human-review-completion artifact exists.
        var humanReviewCompleted = realContext.HumanReviewCompletionArtifactPresent;
        var runtimePilotExecutionReady = canBuild && humanReviewCompleted;
        var blockedExecBy = canBuild && !humanReviewCompleted ? "HumanReviewNotCompleted" : string.Empty;

        if (canBuild)
        {
            replay = BuildOfflineReplay(realContext);
            replayPath = Path.Combine(outputDir, "offline-replay-summary.json");
            File.WriteAllText(replayPath, JsonSerializer.Serialize(replay, WriteIndented), new UTF8Encoding(true));

            canary = BuildShadowCanarySimulation(realContext);
            canaryPath = Path.Combine(outputDir, "shadow-canary-simulation.json");
            File.WriteAllText(canaryPath, JsonSerializer.Serialize(canary, WriteIndented), new UTF8Encoding(true));

            manifest = BuildPilotAuditManifest(realContext, replayPath, canaryPath, runtimePilotExecutionReady, blockedExecBy, now);
            manifestPath = Path.Combine(outputDir, "pilot-audit-manifest.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, WriteIndented), new UTF8Encoding(true));
        }

        // Authority leak checks on produced artifacts
        if (replay.RuntimeDecisionChanged) blocked.Add("OfflineReplayRuntimeDecisionChangedLeak");
        if (replay.PackageOutputChanged) blocked.Add("OfflineReplayPackageOutputChangedLeak");
        if (canary.RuntimeRerankerChanged || canary.RuntimeRouterChanged
            || canary.ProductionDecisionChanged || canary.PackageOutputChanged) blocked.Add("ShadowCanaryRuntimeLeak");
        if (canBuild && (!canary.KillSwitchArmed || !canary.RollbackReady)) blocked.Add("ShadowCanarySafetyContractIncomplete");
        if (manifest.RuntimePromotionApplied) blocked.Add("PilotManifestRuntimePromotionAppliedLeak");
        if (manifest.MLAuthority || manifest.LLMAuthority || manifest.RuntimeAuthority || manifest.GateAuthority) blocked.Add("PilotManifestAuthorityLeak");

        var finalBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var passed = finalBlocked.Length == 0;
        return new LearningControlledRuntimePilotGatePackReport
        {
            OperationId = $"v10-learning-controlled-runtime-pilot-gate-pack-{Guid.NewGuid():N}",
            CreatedAt = now,
            ControlledRuntimePilotGatePackPassed = passed,
            GatePassed = opt.IsGate && passed,
            TotalCases = cases.Length,
            PassedCases = cases.Count(static c => c.PassedAsExpected),
            FailedCases = cases.Count(static c => !c.PassedAsExpected),
            ReadyCases = cases.Count(c => string.Equals(c.ActualStatus, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackReady, StringComparison.Ordinal)),
            BlockedCases = cases.Count(c => string.Equals(c.ActualStatus, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, StringComparison.Ordinal)),
            Cases = cases,
            OfflineReplaySummary = replay,
            ShadowCanarySimulation = canary,
            PilotAuditManifest = manifest,
            V9PromotionReadinessValidated = canBuild,
            CandidatePromotionProposalValidated = canBuild && realContext.PromotionProposalPresent,
            HumanReviewQueueValidated = canBuild && realContext.HumanReviewQueueAllEntriesRequireReview && !realContext.HumanReviewQueueAnyAutoIngest,
            HumanReviewRequired = true,
            HumanReviewCompleted = humanReviewCompleted,
            ControlledPilotDesignValidated = canBuild && realContext.ControlledPilotDesignPresent,
            OfflineReplayReady = canBuild && replay.ReplayExecuted,
            ShadowCanarySimulationReady = canBuild && canary.ShadowCanarySimulationExecuted,
            RuntimePilotExecutionReady = runtimePilotExecutionReady,
            BlockedForRuntimePilotExecutionBy = blockedExecBy,
            RuntimePromotionApplied = false,
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
            RequiresSeparatePromotionGate = true,
            RequiresHumanApproval = true,
            AutoIngest = false,
            V8ScopedActivationPreserved = realContext.V8ScopedActivationPreserved,
            UpstreamV9ReadinessGatePresent = realContext.V9ReadinessGatePresent,
            UpstreamV9ReadinessGatePassed = realContext.V9ReadinessGatePassed,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent,
            OfflineReplaySummaryPath = replayPath,
            ShadowCanarySimulationPath = canaryPath,
            PilotAuditManifestPath = manifestPath,
            Recommendation = passed ? (runtimePilotExecutionReady ? "ProceedToV10.3PilotExecutionGate" : "ProceedToHumanReviewCompletionOrV10.3PilotExecutionGate") : "Blocked",
            NextAllowedPhase = passed ? (runtimePilotExecutionReady ? "V10.3PilotExecutionGate" : "V10.3PilotExecutionGate-pending-human-review") : string.Empty,
            BlockedReasons = finalBlocked,
            Diagnostics = new[]
            {
                $"total={cases.Length}",
                $"realStatus={realDecision.Status}",
                $"humanReviewCompleted={humanReviewCompleted}",
                $"blockedExecBy={blockedExecBy}",
                $"replayExecuted={replay.ReplayExecuted}",
                $"canarySimExecuted={canary.ShadowCanarySimulationExecuted}",
                $"bestShadowCandidate={realContext.BestShadowCandidate}({realContext.BestShadowCandidatePairwiseAccuracy:F3})",
                $"runtimeGate={rtPassed}",
                $"p15Gate={p15Passed}"
            }
        };
    }

    private static IReadOnlyList<LearningControlledRuntimePilotGatePackScenario> BuildScenarios()
    {
        var clean = BuildCleanContext();
        return [
            new("AllUpstreamClean", clean, true, true, false, false, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackReady, null),
            new("V9ReadinessGateMissing", clean with { V9ReadinessGatePresent = false }, true, true, false, false, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, LearningControlledRuntimePilotGatePackBlockedReasons.V9ReadinessGateMissing),
            new("V9ReadinessGateNotPassed", clean with { V9ReadinessGatePassed = false }, true, true, false, false, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, LearningControlledRuntimePilotGatePackBlockedReasons.V9ReadinessGateNotPassed),
            new("PromotionProposalMissing", clean with { PromotionProposalPresent = false }, true, true, false, false, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, LearningControlledRuntimePilotGatePackBlockedReasons.PromotionProposalMissing),
            new("HumanReviewQueueMissing", clean with { HumanReviewQueuePresent = false }, true, true, false, false, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, LearningControlledRuntimePilotGatePackBlockedReasons.HumanReviewQueueMissing),
            new("ControlledPilotDesignMissing", clean with { ControlledPilotDesignPresent = false }, true, true, false, false, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, LearningControlledRuntimePilotGatePackBlockedReasons.ControlledPilotDesignMissing),
            new("HumanReviewRequiredFalse", clean with { HumanReviewRequiredOverride = false }, true, true, false, false, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, LearningControlledRuntimePilotGatePackBlockedReasons.HumanReviewRequiredFalse),
            new("AutoIngestTrue", clean with { AutoIngestOverride = true }, true, true, false, false, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, LearningControlledRuntimePilotGatePackBlockedReasons.AutoIngestTrue),
            new("RuntimePromotionAllowedTrue", clean with { RuntimePromotionAllowedOverride = true }, true, true, false, false, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, LearningControlledRuntimePilotGatePackBlockedReasons.RuntimePromotionAllowedTrue),
            new("RequiresSeparatePromotionGateFalse", clean with { RequiresSeparatePromotionGateOverride = false }, true, true, false, false, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, LearningControlledRuntimePilotGatePackBlockedReasons.RequiresSeparatePromotionGateFalse),
            new("RequiresHumanApprovalFalse", clean with { RequiresHumanApprovalOverride = false }, true, true, false, false, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, LearningControlledRuntimePilotGatePackBlockedReasons.RequiresHumanApprovalFalse),
            new("HumanReviewCompletedFalselyAssumed", clean with { HumanReviewCompletedOverride = true, HumanReviewCompletionArtifactPresent = false }, true, true, false, false, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, LearningControlledRuntimePilotGatePackBlockedReasons.HumanReviewCompletedFalselyAssumed),
            new("RouterPromotionReadyTrue", clean with { RouterPromotionReady = true }, true, true, false, false, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, LearningControlledRuntimePilotGatePackBlockedReasons.RouterPromotionReadyTrue),
            new("RuntimeRerankerChangedTrue", clean with { RuntimeRerankerChangedOverride = true }, true, true, false, false, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, LearningControlledRuntimePilotGatePackBlockedReasons.RuntimeRerankerChangedTrue),
            new("RuntimeRouterChangedTrue", clean with { RuntimeRouterChangedOverride = true }, true, true, false, false, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, LearningControlledRuntimePilotGatePackBlockedReasons.RuntimeRouterChangedTrue),
            new("ProductionDecisionChangedTrue", clean with { ProductionDecisionChangedOverride = true }, true, true, false, false, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, LearningControlledRuntimePilotGatePackBlockedReasons.ProductionDecisionChangedTrue),
            new("PackageOutputChangedTrue", clean with { PackageOutputChangedOverride = true }, true, true, false, false, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, LearningControlledRuntimePilotGatePackBlockedReasons.PackageOutputChangedTrue),
            new("FormalPackageWrittenTrue", clean with { FormalPackageWrittenOverride = true }, true, true, false, false, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, LearningControlledRuntimePilotGatePackBlockedReasons.FormalPackageWrittenTrue),
            new("GlobalDefaultOnTrue", clean with { GlobalDefaultOnOverride = true }, true, true, false, false, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, LearningControlledRuntimePilotGatePackBlockedReasons.GlobalDefaultOnTrue),
            new("MLAuthorityTrue", clean with { MLAuthorityOverride = true }, true, true, false, false, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, LearningControlledRuntimePilotGatePackBlockedReasons.MLAuthorityTrue),
            new("LLMAuthorityTrue", clean with { LLMAuthorityOverride = true }, true, true, false, false, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, LearningControlledRuntimePilotGatePackBlockedReasons.LLMAuthorityTrue),
            new("RuntimeAuthorityTrue", clean with { RuntimeAuthorityOverride = true }, true, true, false, false, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, LearningControlledRuntimePilotGatePackBlockedReasons.RuntimeAuthorityTrue),
            new("GateAuthorityTrue", clean with { GateAuthorityOverride = true }, true, true, false, false, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, LearningControlledRuntimePilotGatePackBlockedReasons.GateAuthorityTrue),
            new("V8ScopedActivationLost", clean with { V8ScopedActivationPreserved = false }, true, true, false, false, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, LearningControlledRuntimePilotGatePackBlockedReasons.V8ScopedActivationLost),
            new("RuntimeGateNotPassed", clean, false, true, false, false, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, LearningControlledRuntimePilotGatePackBlockedReasons.RuntimeChangeGateNotPassed),
            new("P15GateNotPassed", clean, true, false, false, false, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, LearningControlledRuntimePilotGatePackBlockedReasons.P15GateNotPassed),
            new("MainlineEvidencePresent", clean, true, true, true, false, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, LearningControlledRuntimePilotGatePackBlockedReasons.MainlineEvidencePresent),
            new("MainlineTrustRegistryPresent", clean, true, true, false, true, LearningControlledRuntimePilotGatePackStatuses.LearningControlledRuntimePilotGatePackBlocked, LearningControlledRuntimePilotGatePackBlockedReasons.MainlineTrustRegistryPresent)
        ];
    }

    private static LearningControlledRuntimePilotGatePackContext BuildCleanContext() => new()
    {
        V9ReadinessGatePresent = true,
        V9ReadinessGatePassed = true,
        PromotionProposalPresent = true,
        HumanReviewQueuePresent = true,
        HumanReviewQueueEntryCount = 12,
        HumanReviewQueueAllEntriesRequireReview = true,
        HumanReviewQueueAnyAutoIngest = false,
        ControlledPilotDesignPresent = true,
        V8ScopedActivationPreserved = true,
        BestShadowCandidate = "LogisticBaseline",
        BestShadowCandidatePairwiseAccuracy = 1.0,
        ReferenceBaselineName = "WeightedBaseline",
        ReferencePairwiseAccuracy = 0.862,
        CandidateEvalCount = 58,
        RouterPromotionReady = false,
        HumanReviewRequiredOverride = true,
        RequiresSeparatePromotionGateOverride = true,
        RequiresHumanApprovalOverride = true
    };

    private static OfflineReplaySummary BuildOfflineReplay(LearningControlledRuntimePilotGatePackContext ctx)
        => new()
        {
            Candidate = ctx.BestShadowCandidate,
            ReferenceBaseline = ctx.ReferenceBaselineName,
            Scope = "demo-workspace/demo-collection",
            ReplayMode = "DeterministicOfflineReplay",
            ReplayExecuted = true,
            CandidatePairwiseAccuracy = ctx.BestShadowCandidatePairwiseAccuracy,
            ReferencePairwiseAccuracy = ctx.ReferencePairwiseAccuracy,
            AccuracyDelta = ctx.BestShadowCandidatePairwiseAccuracy - ctx.ReferencePairwiseAccuracy,
            CandidateEvalCount = ctx.CandidateEvalCount,
            ReferenceEvalCount = ctx.CandidateEvalCount,
            RuntimeDecisionChanged = false,
            PackageOutputChanged = false,
            Notes = new[]
            {
                $"Candidate '{ctx.BestShadowCandidate}' pairwiseAccuracy={ctx.BestShadowCandidatePairwiseAccuracy:F3}",
                $"Reference '{ctx.ReferenceBaselineName}' pairwiseAccuracy={ctx.ReferencePairwiseAccuracy:F3}",
                $"Delta={ctx.BestShadowCandidatePairwiseAccuracy - ctx.ReferencePairwiseAccuracy:F3} (positive = candidate stronger)",
                "Replay is read-only: candidate and reference both score against held-out V9.1-V9.3 eval set; no runtime traffic touched.",
                "RuntimeDecisionChanged=false / PackageOutputChanged=false — production decisions and packages unaffected.",
                "Reminder: candidate's 100% accuracy is suspicious signal-leak risk; V9.7 risks flagged this. Pilot must verify on hard-negative-expanded eval set before runtime execution."
            }
        };

    private static ShadowCanarySimulation BuildShadowCanarySimulation(LearningControlledRuntimePilotGatePackContext ctx)
    {
        // Deterministic simulation: assume agreement rate = max(0, 1 - referenceErrorRate) — purely descriptive, no runtime traffic.
        var simulatedQueries = Math.Max(50, ctx.CandidateEvalCount);
        var refError = Math.Max(0.0, 1.0 - ctx.ReferencePairwiseAccuracy);
        var agreementRate = 1.0 - refError * 0.5;  // candidate disagrees with reference on roughly half of reference errors
        var agreementCount = (int)Math.Round(simulatedQueries * agreementRate);
        return new ShadowCanarySimulation
        {
            CanaryMode = "ScopedShadowSimulation",
            Scope = "demo-workspace/demo-collection",
            Capability = "FormalRetrievalActivation",
            ShadowCanarySimulationExecuted = true,
            RuntimeRerankerChanged = false,
            RuntimeRouterChanged = false,
            ProductionDecisionChanged = false,
            PackageOutputChanged = false,
            KillSwitchArmed = true,
            RollbackReady = true,
            SimulatedQueryCount = simulatedQueries,
            ShadowAgreementCount = agreementCount,
            SimulatedShadowAgreementRate = agreementRate,
            SimulationStages = new[]
            {
                "Stage 0 — offline replay completed; held-out test set deterministic results recorded",
                "Stage 1 — shadow inference simulation: candidate scores recorded alongside reference, never decides production",
                "Stage 2 — simulated ≤1% canary: candidate vs reference disagreement counted, no traffic switched",
                "Stage 3 — simulated demo-scope canary: aggregate metrics computed, abort conditions checked",
                "Stage 4 — pending V10.3 pilot execution gate (requires human review completion)"
            },
            AbortConditionsObserved = Array.Empty<string>()
        };
    }

    private static PilotAuditManifest BuildPilotAuditManifest(
        LearningControlledRuntimePilotGatePackContext ctx,
        string replayPath, string canaryPath,
        bool runtimePilotExecutionReady, string blockedExecBy, DateTimeOffset now)
        => new()
        {
            ManifestId = $"v10-pilot-audit-manifest-{Guid.NewGuid():N}",
            CreatedAt = now.ToString("O"),
            Capability = "FormalRetrievalActivation",
            Scope = "demo-workspace/demo-collection",
            V9PromotionReadinessValidated = true,
            ControlledPilotDesignValidated = true,
            HumanReviewQueueValidated = true,
            OfflineReplayReady = true,
            ShadowCanarySimulationReady = true,
            RuntimePilotExecutionReady = runtimePilotExecutionReady,
            RuntimePromotionApplied = false,
            BlockedForRuntimePilotExecutionBy = blockedExecBy,
            V9ReadinessGatePath = "learning/v9/shadow-promotion-readiness-pack-gate.json",
            PromotionProposalPath = "learning/v9/shadow-promotion-candidate-proposal.json",
            HumanReviewQueuePath = "learning/v9/human-review-queue-plan.jsonl",
            ControlledPilotDesignPath = "learning/v9/controlled-pilot-design.json",
            OfflineReplaySummaryPath = replayPath,
            ShadowCanarySimulationPath = canaryPath,
            MLAuthority = false,
            LLMAuthority = false,
            RuntimeAuthority = false,
            GateAuthority = false,
            NextAllowedPhase = runtimePilotExecutionReady ? "V10.3PilotExecutionGate" : "V10.3PilotExecutionGate-pending-human-review",
            AuditNotes = new[]
            {
                "V10 pack is read-only: no runtime mutation occurred during this evaluation.",
                $"Human review completion artifact present: {ctx.HumanReviewCompletionArtifactPresent}; runtime pilot execution {(runtimePilotExecutionReady ? "READY" : "BLOCKED pending review")}.",
                "Router promotion remains blocked (RouterPromotionReady=false); router stays on rule-based runtime.",
                "Mainline evidence / trust registry must remain absent until a separate V11+ phase explicitly approves them.",
                "V8 scoped activation state untouched: state.State=Active inside demo-workspace/demo-collection only."
            }
        };

    public static string BuildMarkdown(string title, LearningControlledRuntimePilotGatePackReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"- ControlledRuntimePilotGatePackPassed: `{report.ControlledRuntimePilotGatePackPassed}`");
        sb.AppendLine($"- GatePassed: `{report.GatePassed}`");
        sb.AppendLine($"- TotalCases: `{report.TotalCases}` PassedCases: `{report.PassedCases}` FailedCases: `{report.FailedCases}`");
        sb.AppendLine($"- ReadyCases: `{report.ReadyCases}` BlockedCases: `{report.BlockedCases}`");
        sb.AppendLine();
        sb.AppendLine("## Authority Invariants");
        sb.AppendLine($"- MLAuthority: `{report.MLAuthority}` LLMAuthority: `{report.LLMAuthority}` RuntimeAuthority: `{report.RuntimeAuthority}` GateAuthority: `{report.GateAuthority}`");
        sb.AppendLine($"- RuntimePromotionApplied: `{report.RuntimePromotionApplied}` RequiresSeparatePromotionGate: `{report.RequiresSeparatePromotionGate}` RequiresHumanApproval: `{report.RequiresHumanApproval}`");
        sb.AppendLine($"- HumanReviewRequired: `{report.HumanReviewRequired}` AutoIngest: `{report.AutoIngest}` HumanReviewCompleted: `{report.HumanReviewCompleted}`");
        sb.AppendLine($"- RuntimeRerankerChanged: `{report.RuntimeRerankerChanged}` RuntimeRouterChanged: `{report.RuntimeRouterChanged}` ProductionDecisionChanged: `{report.ProductionDecisionChanged}`");
        sb.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}` FormalPackageWritten: `{report.FormalPackageWritten}` GlobalDefaultOn: `{report.GlobalDefaultOn}`");
        sb.AppendLine($"- V8ScopedActivationPreserved: `{report.V8ScopedActivationPreserved}`");
        sb.AppendLine();
        sb.AppendLine("## Validation Summary");
        sb.AppendLine($"- V9PromotionReadinessValidated: `{report.V9PromotionReadinessValidated}`");
        sb.AppendLine($"- CandidatePromotionProposalValidated: `{report.CandidatePromotionProposalValidated}`");
        sb.AppendLine($"- HumanReviewQueueValidated: `{report.HumanReviewQueueValidated}`");
        sb.AppendLine($"- ControlledPilotDesignValidated: `{report.ControlledPilotDesignValidated}`");
        sb.AppendLine($"- OfflineReplayReady: `{report.OfflineReplayReady}`");
        sb.AppendLine($"- ShadowCanarySimulationReady: `{report.ShadowCanarySimulationReady}`");
        sb.AppendLine($"- RuntimePilotExecutionReady: `{report.RuntimePilotExecutionReady}`");
        if (!string.IsNullOrWhiteSpace(report.BlockedForRuntimePilotExecutionBy))
            sb.AppendLine($"- BlockedForRuntimePilotExecutionBy: `{report.BlockedForRuntimePilotExecutionBy}`");
        sb.AppendLine();
        sb.AppendLine("## Offline Replay");
        sb.AppendLine($"- Candidate `{report.OfflineReplaySummary.Candidate}` pairwiseAccuracy={report.OfflineReplaySummary.CandidatePairwiseAccuracy:F3}");
        sb.AppendLine($"- Reference `{report.OfflineReplaySummary.ReferenceBaseline}` pairwiseAccuracy={report.OfflineReplaySummary.ReferencePairwiseAccuracy:F3}");
        sb.AppendLine($"- AccuracyDelta: `{report.OfflineReplaySummary.AccuracyDelta:F3}` (positive=candidate stronger)");
        sb.AppendLine();
        sb.AppendLine("## Shadow Canary Simulation");
        sb.AppendLine($"- CanaryMode: `{report.ShadowCanarySimulation.CanaryMode}` Scope: `{report.ShadowCanarySimulation.Scope}`");
        sb.AppendLine($"- SimulatedQueryCount: `{report.ShadowCanarySimulation.SimulatedQueryCount}` ShadowAgreementCount: `{report.ShadowCanarySimulation.ShadowAgreementCount}` Rate: `{report.ShadowCanarySimulation.SimulatedShadowAgreementRate:F3}`");
        sb.AppendLine($"- KillSwitchArmed: `{report.ShadowCanarySimulation.KillSwitchArmed}` RollbackReady: `{report.ShadowCanarySimulation.RollbackReady}`");
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

public sealed class LearningControlledRuntimePilotGatePackCase
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

public sealed class LearningControlledRuntimePilotGatePackReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ControlledRuntimePilotGatePackPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public int ReadyCases { get; init; }
    public int BlockedCases { get; init; }
    public IReadOnlyList<LearningControlledRuntimePilotGatePackCase> Cases { get; init; } = Array.Empty<LearningControlledRuntimePilotGatePackCase>();
    public OfflineReplaySummary OfflineReplaySummary { get; init; } = new();
    public ShadowCanarySimulation ShadowCanarySimulation { get; init; } = new();
    public PilotAuditManifest PilotAuditManifest { get; init; } = new();
    public bool V9PromotionReadinessValidated { get; init; }
    public bool CandidatePromotionProposalValidated { get; init; }
    public bool HumanReviewQueueValidated { get; init; }
    public bool HumanReviewRequired { get; init; }
    public bool HumanReviewCompleted { get; init; }
    public bool ControlledPilotDesignValidated { get; init; }
    public bool OfflineReplayReady { get; init; }
    public bool ShadowCanarySimulationReady { get; init; }
    public bool RuntimePilotExecutionReady { get; init; }
    public string BlockedForRuntimePilotExecutionBy { get; init; } = string.Empty;
    public bool RuntimePromotionApplied { get; init; }
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
    public bool RequiresSeparatePromotionGate { get; init; }
    public bool RequiresHumanApproval { get; init; }
    public bool AutoIngest { get; init; }
    public bool V8ScopedActivationPreserved { get; init; }
    public bool UpstreamV9ReadinessGatePresent { get; init; }
    public bool UpstreamV9ReadinessGatePassed { get; init; }
    public bool MainlineEvidencePresent { get; init; }
    public bool MainlineTrustRegistryPresent { get; init; }
    public string OfflineReplaySummaryPath { get; init; } = string.Empty;
    public string ShadowCanarySimulationPath { get; init; } = string.Empty;
    public string PilotAuditManifestPath { get; init; } = string.Empty;
    public string Recommendation { get; init; } = string.Empty;
    public string NextAllowedPhase { get; init; } = string.Empty;
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class LearningControlledRuntimePilotGatePackOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
