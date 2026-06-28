using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

public static class LearningFormalEvidenceBoundaryPackStatuses
{
    public const string LearningFormalEvidenceBoundaryPackReady = nameof(LearningFormalEvidenceBoundaryPackReady);
    public const string LearningFormalEvidenceBoundaryPackBlocked = nameof(LearningFormalEvidenceBoundaryPackBlocked);
}

public static class LearningFormalEvidenceBoundaryPackBlockedReasons
{
    public const string CounterexampleRepairPackMissing = nameof(CounterexampleRepairPackMissing);
    public const string CounterexampleRepairPackNotPassed = nameof(CounterexampleRepairPackNotPassed);
    public const string EvidenceBoundLabelsMissing = nameof(EvidenceBoundLabelsMissing);
    public const string EvidenceSufficiencyV2Missing = nameof(EvidenceSufficiencyV2Missing);
    public const string ShadowLabelsTreatedAsFormal = nameof(ShadowLabelsTreatedAsFormal);
    public const string FormalTrainingSetChangedTrue = nameof(FormalTrainingSetChangedTrue);
    public const string AutoIngestTrue = nameof(AutoIngestTrue);
    public const string RuntimePilotReadyWhileFormalEvidenceFalse = nameof(RuntimePilotReadyWhileFormalEvidenceFalse);
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

public sealed class FormalLabelRealizationContractField
{
    public string FieldName { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public bool Required { get; init; }
    public string Description { get; init; } = string.Empty;
}

public sealed class FormalLabelRealizationContract
{
    public string ContractVersion { get; init; } = "v10.13-formal-label-realization/v1";
    public string ContractMode { get; init; } = "SchemaOnlyNoDatasetWrite";
    public IReadOnlyList<FormalLabelRealizationContractField> Fields { get; init; } = Array.Empty<FormalLabelRealizationContractField>();
    public IReadOnlyList<string> PromotionEligibilityRules { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LifecycleStates { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RejectedAuthorityClaims { get; init; } = Array.Empty<string>();
    public bool FormalTrainingSetChanged { get; init; }
    public bool AutoIngest { get; init; }
    public bool ShadowOnly { get; init; } = true;
}

public sealed class FormalLabelGapReport
{
    public int ShadowLabelCount { get; init; }
    public int FormalizedCount { get; init; }
    public int PendingFormalizationCount { get; init; }
    public int RejectedCount { get; init; }
    public int InvalidBindingCount { get; init; }
    public double FormalizationCoverageRate { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public bool FormalTrainingSetChanged { get; init; }
}

public sealed class PrePilotReadinessDecision
{
    public string DecisionId { get; init; } = string.Empty;
    public string CreatedAt { get; init; } = string.Empty;
    public bool ShadowEvidenceSufficient { get; init; }
    public bool FormalEvidenceSufficient { get; init; }
    public bool FormalLabelRealizationRequired { get; init; }
    public bool PrePilotGateReady { get; init; }
    public bool RuntimePilotExecutionReadyForSeparateGate { get; init; }
    public IReadOnlyList<string> BlockedForRuntimePilotExecutionBy { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> EvidenceSourcesConsidered { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DecisionNotes { get; init; } = Array.Empty<string>();
    public bool AIArbitration { get; init; }
    public bool RuntimePilotExecutionApplied { get; init; }
    public bool RuntimePromotionApplied { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
}

public sealed record LearningFormalEvidenceBoundaryPackContext
{
    public bool CounterexampleRepairPackPresent { get; init; }
    public bool CounterexampleRepairPackPassed { get; init; }
    public bool EvidenceBoundLabelsPresent { get; init; }
    public int EvidenceBoundShadowLabelCount { get; init; }
    public bool EvidenceSufficiencyV2Present { get; init; }
    public bool ShadowEvidenceSufficient { get; init; }
    public bool V8ScopedActivationPreserved { get; init; }
    // Synthetic test knobs
    public bool ShadowLabelsTreatedAsFormalOverride { get; init; }
    public bool FormalTrainingSetChangedOverride { get; init; }
    public bool AutoIngestOverride { get; init; }
    public bool RuntimePilotReadyWhileFormalEvidenceFalseOverride { get; init; }
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

public sealed class LearningFormalEvidenceBoundaryPackDecision
{
    public string Status { get; init; } = LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked;
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;
}

public static class LearningFormalEvidenceBoundaryPackPolicy
{
    public static LearningFormalEvidenceBoundaryPackDecision Evaluate(
        LearningFormalEvidenceBoundaryPackContext ctx,
        bool rtPassed, bool p15Passed,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent)
    {
        var blocked = new List<string>();
        if (!ctx.CounterexampleRepairPackPresent) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.CounterexampleRepairPackMissing);
        else if (!ctx.CounterexampleRepairPackPassed) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.CounterexampleRepairPackNotPassed);
        if (!ctx.EvidenceBoundLabelsPresent || ctx.EvidenceBoundShadowLabelCount <= 0) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.EvidenceBoundLabelsMissing);
        if (!ctx.EvidenceSufficiencyV2Present) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.EvidenceSufficiencyV2Missing);
        if (ctx.ShadowLabelsTreatedAsFormalOverride) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.ShadowLabelsTreatedAsFormal);
        if (ctx.FormalTrainingSetChangedOverride) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.FormalTrainingSetChangedTrue);
        if (ctx.AutoIngestOverride) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.AutoIngestTrue);
        if (ctx.RuntimePilotReadyWhileFormalEvidenceFalseOverride) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.RuntimePilotReadyWhileFormalEvidenceFalse);
        if (ctx.RuntimePilotExecutionAppliedOverride) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.RuntimePilotExecutionAppliedTrue);
        if (ctx.RuntimePromotionAppliedOverride) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.RuntimePromotionAppliedTrue);
        if (ctx.RuntimeRerankerChangedOverride) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.RuntimeRerankerChangedTrue);
        if (ctx.RuntimeRouterChangedOverride) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.RuntimeRouterChangedTrue);
        if (ctx.ProductionDecisionChangedOverride) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.ProductionDecisionChangedTrue);
        if (ctx.PackageOutputChangedOverride) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.PackageOutputChangedTrue);
        if (ctx.FormalPackageWrittenOverride) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.FormalPackageWrittenTrue);
        if (ctx.GlobalDefaultOnOverride) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.GlobalDefaultOnTrue);
        if (ctx.MLAuthorityOverride) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.MLAuthorityTrue);
        if (ctx.LLMAuthorityOverride) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.LLMAuthorityTrue);
        if (ctx.RuntimeAuthorityOverride) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.RuntimeAuthorityTrue);
        if (ctx.GateAuthorityOverride) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.GateAuthorityTrue);
        if (!ctx.V8ScopedActivationPreserved) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.V8ScopedActivationLost);
        if (mainlineEvidencePresent) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.MainlineTrustRegistryPresent);
        if (!rtPassed) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.P15GateNotPassed);
        var finalBlocked = blocked.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        var ready = finalBlocked.Length == 0;
        return new LearningFormalEvidenceBoundaryPackDecision
        {
            Status = ready
                ? LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackReady
                : LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked,
            BlockedReasons = finalBlocked,
            Reasoning = ready
                ? "formal evidence boundary pack policy ready — upstream + authority invariants satisfied; formal vs shadow boundary computed below."
                : $"{finalBlocked.Length} blocked reason(s); formal evidence boundary pack blocked."
        };
    }
}

public sealed record LearningFormalEvidenceBoundaryPackScenario(
    string CaseName,
    LearningFormalEvidenceBoundaryPackContext Context,
    bool RtPassed, bool P15Passed,
    bool MainlineEvidencePresent, bool MainlineRegistryPresent,
    string ExpectedStatus, string? ExpectedBlockedReason);

public sealed class LearningFormalEvidenceBoundaryPackRunner
{
    private static readonly JsonSerializerOptions WriteIndented = new() { WriteIndented = true };

    public LearningFormalEvidenceBoundaryPackReport Run(
        LearningFormalEvidenceBoundaryPackContext realContext,
        string outputDir,
        IReadOnlyList<EvidenceBoundShadowLabel> realBoundLabels,
        bool rtPassed, bool p15Passed,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent,
        LearningFormalEvidenceBoundaryPackOptions? opt = null)
    {
        opt ??= new LearningFormalEvidenceBoundaryPackOptions();
        var now = DateTimeOffset.UtcNow;

        var cases = BuildScenarios().Select(static scenario =>
        {
            var decision = LearningFormalEvidenceBoundaryPackPolicy.Evaluate(
                scenario.Context, scenario.RtPassed, scenario.P15Passed,
                scenario.MainlineEvidencePresent, scenario.MainlineRegistryPresent);
            var statusMatched = string.Equals(scenario.ExpectedStatus, decision.Status, StringComparison.Ordinal);
            var blockedReasonMatched = scenario.ExpectedBlockedReason is null
                || decision.BlockedReasons.Contains(scenario.ExpectedBlockedReason, StringComparer.Ordinal);
            return new LearningFormalEvidenceBoundaryPackCase
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
        if (cases.Length < 25) blocked.Add("InsufficientLearningFormalEvidenceBoundaryPackCases");
        if (cases.Any(static c => !c.PassedAsExpected)) blocked.Add("LearningFormalEvidenceBoundaryPackMatrixFailed");
        foreach (var status in new[] {
            LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackReady,
            LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked })
        {
            if (!cases.Any(c => string.Equals(c.ActualStatus, status, StringComparison.Ordinal)))
                blocked.Add($"StatusBranchNotCovered:{status}");
        }

        var realDecision = LearningFormalEvidenceBoundaryPackPolicy.Evaluate(
            realContext, rtPassed, p15Passed, mainlineEvidencePresent, mainlineRegistryPresent);
        if (!string.Equals(realDecision.Status, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackReady, StringComparison.Ordinal))
            blocked.AddRange(realDecision.BlockedReasons.Select(static x => $"RealLearningFormalEvidenceBoundaryPack:{x}"));
        if (!rtPassed) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(LearningFormalEvidenceBoundaryPackBlockedReasons.MainlineTrustRegistryPresent);

        var canBuild = string.Equals(realDecision.Status, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackReady, StringComparison.Ordinal);
        FormalLabelRealizationContract contract = new();
        FormalLabelGapReport gap = new();
        PrePilotReadinessDecision preDecision = new();
        var contractPath = string.Empty;
        var gapPath = string.Empty;
        var decisionPath = string.Empty;

        if (canBuild)
        {
            contract = BuildFormalLabelRealizationContract();
            contractPath = Path.Combine(outputDir, "formal-label-realization-contract.json");
            File.WriteAllText(contractPath, JsonSerializer.Serialize(contract, WriteIndented), new UTF8Encoding(true));

            gap = BuildFormalLabelGapReport(realBoundLabels);
            gapPath = Path.Combine(outputDir, "formal-label-gap-report.json");
            File.WriteAllText(gapPath, JsonSerializer.Serialize(gap, WriteIndented), new UTF8Encoding(true));

            // The corrected pre-pilot decision:
            // - ShadowEvidenceSufficient = true (from V10.10 outcome)
            // - FormalEvidenceSufficient = false (no formal labels realized)
            // - PrePilotGateReady = true (shadow is fully ready; pre-pilot work is done)
            // - RuntimePilotExecutionReadyForSeparateGate = false (corrected — formal evidence is required)
            preDecision = BuildPrePilotReadinessDecision(realContext, gap, now);
            decisionPath = Path.Combine(outputDir, "pre-pilot-readiness-decision.json");
            File.WriteAllText(decisionPath, JsonSerializer.Serialize(preDecision, WriteIndented), new UTF8Encoding(true));
        }

        // Authority leak checks on produced artifacts
        if (contract.FormalTrainingSetChanged) blocked.Add("ContractFormalTrainingSetChangedLeak");
        if (contract.AutoIngest) blocked.Add("ContractAutoIngestLeak");
        if (!contract.ShadowOnly) blocked.Add("ContractShadowOnlyFalse");
        if (gap.FormalTrainingSetChanged) blocked.Add("GapReportFormalTrainingSetChangedLeak");
        if (preDecision.AIArbitration || preDecision.RuntimePilotExecutionApplied
            || preDecision.RuntimePromotionApplied || preDecision.PackageOutputChanged
            || preDecision.GlobalDefaultOn) blocked.Add("PrePilotDecisionRuntimeLeak");
        // The new invariant: if FormalEvidenceSufficient=false AND RuntimePilotReady=true → leak.
        if (preDecision.RuntimePilotExecutionReadyForSeparateGate && !preDecision.FormalEvidenceSufficient)
            blocked.Add("PrePilotDecisionPilotReadyWhileFormalEvidenceFalseLeak");

        var finalBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var passed = finalBlocked.Length == 0;

        return new LearningFormalEvidenceBoundaryPackReport
        {
            OperationId = $"v10-learning-formal-evidence-boundary-pack-{Guid.NewGuid():N}",
            CreatedAt = now,
            FormalEvidenceBoundaryPackPassed = passed,
            GatePassed = opt.IsGate && passed,
            TotalCases = cases.Length,
            PassedCases = cases.Count(static c => c.PassedAsExpected),
            FailedCases = cases.Count(static c => !c.PassedAsExpected),
            ReadyCases = cases.Count(c => string.Equals(c.ActualStatus, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackReady, StringComparison.Ordinal)),
            BlockedCases = cases.Count(c => string.Equals(c.ActualStatus, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked, StringComparison.Ordinal)),
            Cases = cases,
            FormalLabelRealizationContract = contract,
            FormalLabelGapReport = gap,
            PrePilotReadinessDecision = preDecision,
            FormalLabelRealizationContractReady = canBuild && contract.Fields.Count > 0,
            FormalLabelGapReportReady = canBuild,
            PrePilotReadinessDecisionReady = canBuild,
            ShadowEvidenceSufficient = preDecision.ShadowEvidenceSufficient,
            FormalEvidenceSufficient = preDecision.FormalEvidenceSufficient,
            FormalLabelRealizationRequired = preDecision.FormalLabelRealizationRequired,
            PrePilotGateReady = preDecision.PrePilotGateReady,
            RuntimePilotExecutionReadyForSeparateGate = preDecision.RuntimePilotExecutionReadyForSeparateGate,
            FormalizedCount = gap.FormalizedCount,
            PendingFormalizationCount = gap.PendingFormalizationCount,
            BlockedForRuntimePilotExecutionBy = preDecision.BlockedForRuntimePilotExecutionBy,
            ShadowOnly = true,
            EvidenceBoundShadowLabelsAreFormal = false,
            FormalTrainingSetChanged = false,
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
            UpstreamCounterexampleRepairPackGatePresent = realContext.CounterexampleRepairPackPresent,
            UpstreamCounterexampleRepairPackGatePassed = realContext.CounterexampleRepairPackPassed,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent,
            FormalLabelRealizationContractPath = contractPath,
            FormalLabelGapReportPath = gapPath,
            PrePilotReadinessDecisionPath = decisionPath,
            Recommendation = passed ? "ProceedToFormalLabelRealizationViaV9.5FeedbackIngestion" : "Blocked",
            NextAllowedPhase = passed ? "FormalLabelRealization-pending-V9.5-human-feedback-ingestion" : string.Empty,
            BlockedReasons = finalBlocked,
            Diagnostics = new[]
            {
                $"total={cases.Length}",
                $"realStatus={realDecision.Status}",
                $"shadowEvidenceSufficient={preDecision.ShadowEvidenceSufficient}",
                $"formalEvidenceSufficient={preDecision.FormalEvidenceSufficient}",
                $"formalizedCount={gap.FormalizedCount}",
                $"pendingFormalizationCount={gap.PendingFormalizationCount}",
                $"prePilotGateReady={preDecision.PrePilotGateReady}",
                $"runtimePilotReady={preDecision.RuntimePilotExecutionReadyForSeparateGate} (must be false until formal evidence)",
                $"runtimeGate={rtPassed}",
                $"p15Gate={p15Passed}"
            }
        };
    }

    private static IReadOnlyList<LearningFormalEvidenceBoundaryPackScenario> BuildScenarios()
    {
        var clean = BuildCleanContext();
        return [
            new("AllUpstreamClean", clean, true, true, false, false, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackReady, null),
            new("CounterexampleRepairPackMissing", clean with { CounterexampleRepairPackPresent = false }, true, true, false, false, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked, LearningFormalEvidenceBoundaryPackBlockedReasons.CounterexampleRepairPackMissing),
            new("CounterexampleRepairPackNotPassed", clean with { CounterexampleRepairPackPassed = false }, true, true, false, false, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked, LearningFormalEvidenceBoundaryPackBlockedReasons.CounterexampleRepairPackNotPassed),
            new("EvidenceBoundLabelsMissing", clean with { EvidenceBoundLabelsPresent = false, EvidenceBoundShadowLabelCount = 0 }, true, true, false, false, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked, LearningFormalEvidenceBoundaryPackBlockedReasons.EvidenceBoundLabelsMissing),
            new("EvidenceSufficiencyV2Missing", clean with { EvidenceSufficiencyV2Present = false }, true, true, false, false, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked, LearningFormalEvidenceBoundaryPackBlockedReasons.EvidenceSufficiencyV2Missing),
            new("ShadowLabelsTreatedAsFormal", clean with { ShadowLabelsTreatedAsFormalOverride = true }, true, true, false, false, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked, LearningFormalEvidenceBoundaryPackBlockedReasons.ShadowLabelsTreatedAsFormal),
            new("FormalTrainingSetChangedTrue", clean with { FormalTrainingSetChangedOverride = true }, true, true, false, false, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked, LearningFormalEvidenceBoundaryPackBlockedReasons.FormalTrainingSetChangedTrue),
            new("AutoIngestTrue", clean with { AutoIngestOverride = true }, true, true, false, false, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked, LearningFormalEvidenceBoundaryPackBlockedReasons.AutoIngestTrue),
            new("RuntimePilotReadyWhileFormalEvidenceFalse", clean with { RuntimePilotReadyWhileFormalEvidenceFalseOverride = true }, true, true, false, false, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked, LearningFormalEvidenceBoundaryPackBlockedReasons.RuntimePilotReadyWhileFormalEvidenceFalse),
            new("RuntimePilotExecutionAppliedTrue", clean with { RuntimePilotExecutionAppliedOverride = true }, true, true, false, false, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked, LearningFormalEvidenceBoundaryPackBlockedReasons.RuntimePilotExecutionAppliedTrue),
            new("RuntimePromotionAppliedTrue", clean with { RuntimePromotionAppliedOverride = true }, true, true, false, false, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked, LearningFormalEvidenceBoundaryPackBlockedReasons.RuntimePromotionAppliedTrue),
            new("RuntimeRerankerChangedTrue", clean with { RuntimeRerankerChangedOverride = true }, true, true, false, false, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked, LearningFormalEvidenceBoundaryPackBlockedReasons.RuntimeRerankerChangedTrue),
            new("RuntimeRouterChangedTrue", clean with { RuntimeRouterChangedOverride = true }, true, true, false, false, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked, LearningFormalEvidenceBoundaryPackBlockedReasons.RuntimeRouterChangedTrue),
            new("ProductionDecisionChangedTrue", clean with { ProductionDecisionChangedOverride = true }, true, true, false, false, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked, LearningFormalEvidenceBoundaryPackBlockedReasons.ProductionDecisionChangedTrue),
            new("PackageOutputChangedTrue", clean with { PackageOutputChangedOverride = true }, true, true, false, false, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked, LearningFormalEvidenceBoundaryPackBlockedReasons.PackageOutputChangedTrue),
            new("FormalPackageWrittenTrue", clean with { FormalPackageWrittenOverride = true }, true, true, false, false, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked, LearningFormalEvidenceBoundaryPackBlockedReasons.FormalPackageWrittenTrue),
            new("GlobalDefaultOnTrue", clean with { GlobalDefaultOnOverride = true }, true, true, false, false, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked, LearningFormalEvidenceBoundaryPackBlockedReasons.GlobalDefaultOnTrue),
            new("MLAuthorityTrue", clean with { MLAuthorityOverride = true }, true, true, false, false, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked, LearningFormalEvidenceBoundaryPackBlockedReasons.MLAuthorityTrue),
            new("LLMAuthorityTrue", clean with { LLMAuthorityOverride = true }, true, true, false, false, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked, LearningFormalEvidenceBoundaryPackBlockedReasons.LLMAuthorityTrue),
            new("RuntimeAuthorityTrue", clean with { RuntimeAuthorityOverride = true }, true, true, false, false, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked, LearningFormalEvidenceBoundaryPackBlockedReasons.RuntimeAuthorityTrue),
            new("GateAuthorityTrue", clean with { GateAuthorityOverride = true }, true, true, false, false, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked, LearningFormalEvidenceBoundaryPackBlockedReasons.GateAuthorityTrue),
            new("V8ScopedActivationLost", clean with { V8ScopedActivationPreserved = false }, true, true, false, false, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked, LearningFormalEvidenceBoundaryPackBlockedReasons.V8ScopedActivationLost),
            new("MainlineEvidencePresent", clean, true, true, true, false, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked, LearningFormalEvidenceBoundaryPackBlockedReasons.MainlineEvidencePresent),
            new("MainlineTrustRegistryPresent", clean, true, true, false, true, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked, LearningFormalEvidenceBoundaryPackBlockedReasons.MainlineTrustRegistryPresent),
            new("RuntimeGateNotPassed", clean, false, true, false, false, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked, LearningFormalEvidenceBoundaryPackBlockedReasons.RuntimeChangeGateNotPassed),
            new("P15GateNotPassed", clean, true, false, false, false, LearningFormalEvidenceBoundaryPackStatuses.LearningFormalEvidenceBoundaryPackBlocked, LearningFormalEvidenceBoundaryPackBlockedReasons.P15GateNotPassed)
        ];
    }

    private static LearningFormalEvidenceBoundaryPackContext BuildCleanContext() => new()
    {
        CounterexampleRepairPackPresent = true,
        CounterexampleRepairPackPassed = true,
        EvidenceBoundLabelsPresent = true,
        EvidenceBoundShadowLabelCount = 60,
        EvidenceSufficiencyV2Present = true,
        ShadowEvidenceSufficient = true,
        V8ScopedActivationPreserved = true
    };

    private static FormalLabelRealizationContract BuildFormalLabelRealizationContract()
        => new()
        {
            ContractVersion = "v10.13-formal-label-realization/v1",
            ContractMode = "SchemaOnlyNoDatasetWrite",
            Fields = new[]
            {
                new FormalLabelRealizationContractField { FieldName = "formalLabelId", Type = "string", Required = true, Description = "New stable id minted at formalization time; different from sourceShadowLabelId to track the realization step." },
                new FormalLabelRealizationContractField { FieldName = "sourceShadowLabelId", Type = "string", Required = true, Description = "References the V10.10 EvidenceBoundShadowLabel.LabelId; one-to-one." },
                new FormalLabelRealizationContractField { FieldName = "evidencePath", Type = "string", Required = true, Description = "Stable on-disk path to the underlying evidence row (ranking-pairs.jsonl#evalSampleId=...)." },
                new FormalLabelRealizationContractField { FieldName = "expectedPreference", Type = "string", Required = true, Description = "Canonical preference label, e.g. PositiveOverNegative." },
                new FormalLabelRealizationContractField { FieldName = "deterministicBindingHash", Type = "string", Required = true, Description = "SHA-256 of (sourceShadowLabelId + evidencePath + expectedPreference) — used to detect tampering between shadow and formal." },
                new FormalLabelRealizationContractField { FieldName = "promotionEligibility", Type = "string", Required = true, Description = "Eligible / Pending / Rejected / InvalidBinding." },
                new FormalLabelRealizationContractField { FieldName = "lifecycleState", Type = "string", Required = true, Description = "Proposed / UnderReview / Approved / Realized / Retracted." },
                new FormalLabelRealizationContractField { FieldName = "reviewSignals", Type = "string[]", Required = false, Description = "Human-feedback signals attached (per V10.3 HumanFeedbackSignalPolicy)." },
                new FormalLabelRealizationContractField { FieldName = "realizedAt", Type = "datetime", Required = false, Description = "Set only when lifecycleState=Realized." }
            },
            PromotionEligibilityRules = new[]
            {
                "Eligible iff: sourceShadowLabelId resolves, evidencePath exists, expectedPreference matches V10.10 binding, deterministicBindingHash matches recomputed hash.",
                "Realization MAY only proceed via V9.5 feedback ingestion pipeline; this contract NEVER writes the formal dataset itself.",
                "Rejected labels MUST keep the source shadow label intact for re-binding under a future evidence cycle."
            },
            LifecycleStates = new[] { "Proposed", "UnderReview", "Approved", "Realized", "Retracted" },
            RejectedAuthorityClaims = new[]
            {
                "This contract CANNOT auto-promote shadow labels to formal labels.",
                "This contract CANNOT modify learning/features/hard-negatives.jsonl.",
                "This contract CANNOT bypass the V9.5 human feedback ingestion path.",
                "This contract CANNOT grant ML / LLM / Runtime / Gate authority."
            },
            FormalTrainingSetChanged = false,
            AutoIngest = false,
            ShadowOnly = true
        };

    private static FormalLabelGapReport BuildFormalLabelGapReport(IReadOnlyList<EvidenceBoundShadowLabel> bound)
    {
        var shadowCount = bound.Count;
        // No formalized labels exist yet — formal dataset is still untouched.
        var formalized = 0;
        var rejected = 0;
        // Invalid bindings: labels whose evidencePath is empty or expectedPreference is empty.
        var invalidBindings = bound.Count(l => string.IsNullOrWhiteSpace(l.EvidencePath) || string.IsNullOrWhiteSpace(l.ExpectedPreference));
        var pending = shadowCount - formalized - rejected - invalidBindings;
        if (pending < 0) pending = 0;
        var coverage = shadowCount > 0 ? (double)formalized / shadowCount : 0;
        var notes = new List<string>
        {
            $"ShadowLabelCount={shadowCount} (from V10.10 evidence-bound-hard-negative-labels.jsonl)",
            $"FormalizedCount={formalized} (no formal labels exist on disk yet)",
            $"PendingFormalizationCount={pending}",
            $"RejectedCount={rejected}",
            $"InvalidBindingCount={invalidBindings}",
            $"FormalizationCoverageRate={coverage:F3}",
            "Formal training set is NOT modified by this report. Realization happens through V9.5 feedback ingestion only."
        };
        if (shadowCount > 0 && formalized == 0)
            notes.Add("Status: ALL shadow labels are pending formalization. RuntimePilotExecutionReadyForSeparateGate=false until ≥1 formalized.");
        return new FormalLabelGapReport
        {
            ShadowLabelCount = shadowCount,
            FormalizedCount = formalized,
            PendingFormalizationCount = pending,
            RejectedCount = rejected,
            InvalidBindingCount = invalidBindings,
            FormalizationCoverageRate = coverage,
            Notes = notes,
            FormalTrainingSetChanged = false
        };
    }

    private static PrePilotReadinessDecision BuildPrePilotReadinessDecision(
        LearningFormalEvidenceBoundaryPackContext ctx,
        FormalLabelGapReport gap,
        DateTimeOffset now)
    {
        // Corrected readiness semantics:
        // - ShadowEvidenceSufficient: yes (V10.10 already proved it)
        // - FormalEvidenceSufficient: no (FormalizedCount==0)
        // - PrePilotGateReady: yes (shadow side is complete; the bridge artifact is in place)
        // - RuntimePilotExecutionReadyForSeparateGate: NO (corrected from V10.10 mis-report)
        var shadowSufficient = ctx.ShadowEvidenceSufficient;
        var formalSufficient = gap.FormalizedCount > 0;
        var pilotReady = formalSufficient;  // strictly false right now
        var blockedBy = new List<string>();
        if (!formalSufficient) blockedBy.Add("FormalEvidenceInsufficient");
        if (gap.PendingFormalizationCount > 0) blockedBy.Add($"FormalLabelsPendingRealization:{gap.PendingFormalizationCount}");
        return new PrePilotReadinessDecision
        {
            DecisionId = $"v10-pre-pilot-readiness-decision-{Guid.NewGuid():N}",
            CreatedAt = now.ToString("O"),
            ShadowEvidenceSufficient = shadowSufficient,
            FormalEvidenceSufficient = formalSufficient,
            FormalLabelRealizationRequired = true,
            PrePilotGateReady = true,
            RuntimePilotExecutionReadyForSeparateGate = pilotReady,
            BlockedForRuntimePilotExecutionBy = blockedBy,
            EvidenceSourcesConsidered = new[]
            {
                "learning/v10/counterexample-repair-pack-gate.json",
                "learning/v10/evidence-bound-hard-negative-labels.jsonl",
                "learning/v10/evidence-sufficiency-recomputed-v2.json",
                "learning/v10/repaired-shadow-scoring-proposal.json",
                "learning/v10/counterexample-replay-after-repair.json"
            },
            DecisionNotes = new[]
            {
                "Shadow evidence is sufficient (V10.10 RepairedCandidateFailureRate=0 matches Reference).",
                "Formal evidence is NOT sufficient: zero labels in the formal dataset have been realized from the 60 shadow-bound labels.",
                "Pre-pilot gate is ready: realization contract published, gap report written, decision recorded.",
                "RuntimePilotExecutionReadyForSeparateGate=false — this corrects the V10.10 semantic mistake where shadow-only sufficiency was conflated with pilot readiness.",
                "Path forward: V9.5 feedback ingestion realizes ≥1 (preferably all 60) shadow labels to formal labels before any runtime pilot."
            },
            AIArbitration = false,
            RuntimePilotExecutionApplied = false,
            RuntimePromotionApplied = false,
            PackageOutputChanged = false,
            GlobalDefaultOn = false
        };
    }

    public static string BuildMarkdown(string title, LearningFormalEvidenceBoundaryPackReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"- FormalEvidenceBoundaryPackPassed: `{report.FormalEvidenceBoundaryPackPassed}`");
        sb.AppendLine($"- GatePassed: `{report.GatePassed}`");
        sb.AppendLine($"- TotalCases: `{report.TotalCases}` PassedCases: `{report.PassedCases}` FailedCases: `{report.FailedCases}`");
        sb.AppendLine();
        sb.AppendLine("## Shadow vs Formal Evidence Boundary");
        sb.AppendLine($"- **ShadowEvidenceSufficient**: `{report.ShadowEvidenceSufficient}`");
        sb.AppendLine($"- **FormalEvidenceSufficient**: `{report.FormalEvidenceSufficient}`");
        sb.AppendLine($"- **FormalLabelRealizationRequired**: `{report.FormalLabelRealizationRequired}`");
        sb.AppendLine($"- **PrePilotGateReady**: `{report.PrePilotGateReady}`");
        sb.AppendLine($"- **RuntimePilotExecutionReadyForSeparateGate**: `{report.RuntimePilotExecutionReadyForSeparateGate}` (must remain false until FormalEvidenceSufficient=true)");
        if (report.BlockedForRuntimePilotExecutionBy.Count > 0)
            sb.AppendLine($"- BlockedForRuntimePilotExecutionBy: `{string.Join(", ", report.BlockedForRuntimePilotExecutionBy)}`");
        sb.AppendLine();
        sb.AppendLine("## Formal Label Gap Report");
        sb.AppendLine($"- ShadowLabelCount: `{report.FormalLabelGapReport.ShadowLabelCount}`");
        sb.AppendLine($"- **FormalizedCount**: `{report.FormalizedCount}`");
        sb.AppendLine($"- **PendingFormalizationCount**: `{report.PendingFormalizationCount}`");
        sb.AppendLine($"- RejectedCount: `{report.FormalLabelGapReport.RejectedCount}`");
        sb.AppendLine($"- InvalidBindingCount: `{report.FormalLabelGapReport.InvalidBindingCount}`");
        sb.AppendLine($"- FormalizationCoverageRate: `{report.FormalLabelGapReport.FormalizationCoverageRate:F3}`");
        sb.AppendLine($"- FormalTrainingSetChanged: `{report.FormalLabelGapReport.FormalTrainingSetChanged}`");
        sb.AppendLine();
        sb.AppendLine("## Formal Label Realization Contract");
        sb.AppendLine($"- ContractVersion: `{report.FormalLabelRealizationContract.ContractVersion}`");
        sb.AppendLine($"- ContractMode: `{report.FormalLabelRealizationContract.ContractMode}`");
        sb.AppendLine($"- Fields: `{report.FormalLabelRealizationContract.Fields.Count}` LifecycleStates: `{report.FormalLabelRealizationContract.LifecycleStates.Count}`");
        sb.AppendLine($"- AutoIngest: `{report.FormalLabelRealizationContract.AutoIngest}` ShadowOnly: `{report.FormalLabelRealizationContract.ShadowOnly}` FormalTrainingSetChanged: `{report.FormalLabelRealizationContract.FormalTrainingSetChanged}`");
        sb.AppendLine();
        sb.AppendLine("## Authority Invariants");
        sb.AppendLine($"- ShadowOnly: `{report.ShadowOnly}` EvidenceBoundShadowLabelsAreFormal: `{report.EvidenceBoundShadowLabelsAreFormal}` FormalTrainingSetChanged: `{report.FormalTrainingSetChanged}`");
        sb.AppendLine($"- AutoIngest: `{report.AutoIngest}` TrainingSetChanged: `{report.TrainingSetChanged}`");
        sb.AppendLine($"- MLAuthority: `{report.MLAuthority}` LLMAuthority: `{report.LLMAuthority}` RuntimeAuthority: `{report.RuntimeAuthority}` GateAuthority: `{report.GateAuthority}`");
        sb.AppendLine($"- RuntimePromotionApplied: `{report.RuntimePromotionApplied}` RuntimePilotExecutionApplied: `{report.RuntimePilotExecutionApplied}`");
        sb.AppendLine($"- RuntimeRerankerChanged: `{report.RuntimeRerankerChanged}` RuntimeRouterChanged: `{report.RuntimeRouterChanged}` ProductionDecisionChanged: `{report.ProductionDecisionChanged}`");
        sb.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}` FormalPackageWritten: `{report.FormalPackageWritten}` GlobalDefaultOn: `{report.GlobalDefaultOn}`");
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

    /// <summary>V10.13: load real V10.10 evidence-bound shadow labels from disk.</summary>
    public static IReadOnlyList<EvidenceBoundShadowLabel> LoadEvidenceBoundShadowLabels(string path)
    {
        if (!File.Exists(path)) return Array.Empty<EvidenceBoundShadowLabel>();
        var result = new List<EvidenceBoundShadowLabel>();
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var label = JsonSerializer.Deserialize<EvidenceBoundShadowLabel>(line, opts);
                if (label is not null) result.Add(label);
            }
            catch { }
        }
        return result;
    }
}

public sealed class LearningFormalEvidenceBoundaryPackCase
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

public sealed class LearningFormalEvidenceBoundaryPackReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool FormalEvidenceBoundaryPackPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public int ReadyCases { get; init; }
    public int BlockedCases { get; init; }
    public IReadOnlyList<LearningFormalEvidenceBoundaryPackCase> Cases { get; init; } = Array.Empty<LearningFormalEvidenceBoundaryPackCase>();
    public FormalLabelRealizationContract FormalLabelRealizationContract { get; init; } = new();
    public FormalLabelGapReport FormalLabelGapReport { get; init; } = new();
    public PrePilotReadinessDecision PrePilotReadinessDecision { get; init; } = new();
    public bool FormalLabelRealizationContractReady { get; init; }
    public bool FormalLabelGapReportReady { get; init; }
    public bool PrePilotReadinessDecisionReady { get; init; }
    public bool ShadowEvidenceSufficient { get; init; }
    public bool FormalEvidenceSufficient { get; init; }
    public bool FormalLabelRealizationRequired { get; init; }
    public bool PrePilotGateReady { get; init; }
    public bool RuntimePilotExecutionReadyForSeparateGate { get; init; }
    public int FormalizedCount { get; init; }
    public int PendingFormalizationCount { get; init; }
    public IReadOnlyList<string> BlockedForRuntimePilotExecutionBy { get; init; } = Array.Empty<string>();
    public bool ShadowOnly { get; init; }
    public bool EvidenceBoundShadowLabelsAreFormal { get; init; }
    public bool FormalTrainingSetChanged { get; init; }
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
    public bool UpstreamCounterexampleRepairPackGatePresent { get; init; }
    public bool UpstreamCounterexampleRepairPackGatePassed { get; init; }
    public bool MainlineEvidencePresent { get; init; }
    public bool MainlineTrustRegistryPresent { get; init; }
    public string FormalLabelRealizationContractPath { get; init; } = string.Empty;
    public string FormalLabelGapReportPath { get; init; } = string.Empty;
    public string PrePilotReadinessDecisionPath { get; init; } = string.Empty;
    public string Recommendation { get; init; } = string.Empty;
    public string NextAllowedPhase { get; init; } = string.Empty;
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class LearningFormalEvidenceBoundaryPackOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
