using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

public static class LearningFormalEvidenceRealizationPackStatuses
{
    public const string LearningFormalEvidenceRealizationPackReady = nameof(LearningFormalEvidenceRealizationPackReady);
    public const string LearningFormalEvidenceRealizationPackBlocked = nameof(LearningFormalEvidenceRealizationPackBlocked);
}

public static class LearningFormalEvidenceRealizationPackBlockedReasons
{
    public const string FormalEvidenceBoundaryMissing = nameof(FormalEvidenceBoundaryMissing);
    public const string FormalEvidenceBoundaryNotPassed = nameof(FormalEvidenceBoundaryNotPassed);
    public const string RealizationContractMissing = nameof(RealizationContractMissing);
    public const string ShadowLabelsMissing = nameof(ShadowLabelsMissing);
    public const string HashMismatchDetected = nameof(HashMismatchDetected);
    public const string EvidencePathMissing = nameof(EvidencePathMissing);
    public const string ExpectedPreferenceMismatch = nameof(ExpectedPreferenceMismatch);
    public const string FormalCandidatesTreatedAsFormalLabels = nameof(FormalCandidatesTreatedAsFormalLabels);
    public const string AutoIngestTrue = nameof(AutoIngestTrue);
    public const string FormalTrainingSetChangedUnexpectedly = nameof(FormalTrainingSetChangedUnexpectedly);
    public const string HumanReviewAsGateAuthorityTrue = nameof(HumanReviewAsGateAuthorityTrue);
    public const string HumanFeedbackAutoIngestTrue = nameof(HumanFeedbackAutoIngestTrue);
    public const string RuntimePilotReadyWhileFormalLabelsUnrealized = nameof(RuntimePilotReadyWhileFormalLabelsUnrealized);
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

public sealed class FormalLabelCandidate
{
    public string CandidateLabelId { get; init; } = string.Empty;
    public string SourceShadowLabelId { get; init; } = string.Empty;
    public string EvidencePath { get; init; } = string.Empty;
    public string ExpectedPreference { get; init; } = string.Empty;
    public string DeterministicBindingHash { get; init; } = string.Empty;
    public string PromotionEligibility { get; init; } = string.Empty;
    public string LifecycleState { get; init; } = "Proposed";
    public bool IntegrityVerified { get; init; }
    public bool FormalLabelCandidateIsFormal { get; init; }
    public bool ShadowOnly { get; init; } = true;
    public bool AutoIngest { get; init; }
    public string PolicyVersion { get; init; } = "v10.16-formal-label-candidate/v1";
}

public sealed class FormalLabelIntegrityManifestEntry
{
    public string CandidateLabelId { get; init; } = string.Empty;
    public string SourceShadowLabelId { get; init; } = string.Empty;
    public string EvidencePath { get; init; } = string.Empty;
    public string ExpectedPreference { get; init; } = string.Empty;
    public string DeterministicBindingHash { get; init; } = string.Empty;
    public string ExpectedHash { get; init; } = string.Empty;
    public bool HashMatches { get; init; }
    public string IntegrityStatus { get; init; } = string.Empty;
}

public sealed class FormalLabelIntegrityManifest
{
    public string ManifestId { get; init; } = string.Empty;
    public string ManifestVersion { get; init; } = "v10.16-formal-label-integrity/v1";
    public string HashAlgorithm { get; init; } = "SHA-256";
    public int TotalEntries { get; init; }
    public int VerifiedEntries { get; init; }
    public int MismatchedEntries { get; init; }
    public IReadOnlyList<FormalLabelIntegrityManifestEntry> Entries { get; init; } = Array.Empty<FormalLabelIntegrityManifestEntry>();
    public bool AnyHashMismatch { get; init; }
    public bool ShadowOnly { get; init; } = true;
}

public sealed class FormalLabelRealizationDecision
{
    public string DecisionId { get; init; } = string.Empty;
    public string CreatedAt { get; init; } = string.Empty;
    public int FormalLabelCandidateCount { get; init; }
    public int RealizableFormalLabelCount { get; init; }
    public int InvalidBindingCount { get; init; }
    public bool FormalLabelCandidatesReady { get; init; }
    public bool FormalLabelsRealized { get; init; }
    public bool FormalEvidenceSufficient { get; init; }
    public bool RuntimePilotExecutionReadyForSeparateGate { get; init; }
    public IReadOnlyList<string> BlockedForRuntimePilotExecutionBy { get; init; } = Array.Empty<string>();
    public bool HumanFeedbackAsSignal { get; init; } = true;
    public bool HumanReviewAsGateAuthority { get; init; }
    public bool HumanFeedbackAutoIngest { get; init; }
    public bool FormalTrainingSetChanged { get; init; }
    public bool FormalLabelCandidatesAreFormal { get; init; }
    public bool AIArbitration { get; init; }
    public IReadOnlyList<string> EvidenceSourcesConsidered { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DecisionNotes { get; init; } = Array.Empty<string>();
}

public sealed class FormalLabelRollbackContractField
{
    public string FieldName { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public bool Required { get; init; }
    public string Description { get; init; } = string.Empty;
}

public sealed class FormalLabelRollbackContract
{
    public string ContractVersion { get; init; } = "v10.16-formal-label-rollback/v1";
    public string ContractMode { get; init; } = "SchemaOnlyNoRollbackExecuted";
    public IReadOnlyList<FormalLabelRollbackContractField> Fields { get; init; } = Array.Empty<FormalLabelRollbackContractField>();
    public IReadOnlyList<string> RollbackTriggers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RollbackPreconditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RollbackActions { get; init; } = Array.Empty<string>();
    public bool FormalTrainingSetChanged { get; init; }
    public bool AutoIngest { get; init; }
    public bool RuntimeRollbackApplied { get; init; }
}

public sealed record LearningFormalEvidenceRealizationPackContext
{
    public bool FormalEvidenceBoundaryPresent { get; init; }
    public bool FormalEvidenceBoundaryPassed { get; init; }
    public bool RealizationContractPresent { get; init; }
    public bool ShadowLabelsPresent { get; init; }
    public int ShadowLabelCount { get; init; }
    public bool V8ScopedActivationPreserved { get; init; }
    public IReadOnlyList<EvidenceBoundShadowLabel> ShadowLabels { get; init; } = Array.Empty<EvidenceBoundShadowLabel>();
    public IReadOnlyList<RankerPair> RankerPairs { get; init; } = Array.Empty<RankerPair>();
    // Synthetic test knobs
    public bool HashMismatchOverride { get; init; }
    public bool EvidencePathMissingOverride { get; init; }
    public bool ExpectedPreferenceMismatchOverride { get; init; }
    public bool FormalCandidatesTreatedAsFormalLabelsOverride { get; init; }
    public bool AutoIngestOverride { get; init; }
    public bool FormalTrainingSetChangedOverride { get; init; }
    public bool HumanReviewAsGateAuthorityOverride { get; init; }
    public bool HumanFeedbackAutoIngestOverride { get; init; }
    public bool RuntimePilotReadyWhileFormalLabelsUnrealizedOverride { get; init; }
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

public sealed class LearningFormalEvidenceRealizationPackDecision
{
    public string Status { get; init; } = LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked;
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;
}

public static class LearningFormalEvidenceRealizationPackPolicy
{
    public static LearningFormalEvidenceRealizationPackDecision Evaluate(
        LearningFormalEvidenceRealizationPackContext ctx,
        bool rtPassed, bool p15Passed,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent)
    {
        var blocked = new List<string>();
        if (!ctx.FormalEvidenceBoundaryPresent) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.FormalEvidenceBoundaryMissing);
        else if (!ctx.FormalEvidenceBoundaryPassed) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.FormalEvidenceBoundaryNotPassed);
        if (!ctx.RealizationContractPresent) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.RealizationContractMissing);
        if (!ctx.ShadowLabelsPresent || ctx.ShadowLabelCount <= 0) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.ShadowLabelsMissing);
        if (ctx.HashMismatchOverride) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.HashMismatchDetected);
        if (ctx.EvidencePathMissingOverride) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.EvidencePathMissing);
        if (ctx.ExpectedPreferenceMismatchOverride) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.ExpectedPreferenceMismatch);
        if (ctx.FormalCandidatesTreatedAsFormalLabelsOverride) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.FormalCandidatesTreatedAsFormalLabels);
        if (ctx.AutoIngestOverride) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.AutoIngestTrue);
        if (ctx.FormalTrainingSetChangedOverride) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.FormalTrainingSetChangedUnexpectedly);
        if (ctx.HumanReviewAsGateAuthorityOverride) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.HumanReviewAsGateAuthorityTrue);
        if (ctx.HumanFeedbackAutoIngestOverride) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.HumanFeedbackAutoIngestTrue);
        if (ctx.RuntimePilotReadyWhileFormalLabelsUnrealizedOverride) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.RuntimePilotReadyWhileFormalLabelsUnrealized);
        if (ctx.RuntimePilotExecutionAppliedOverride) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.RuntimePilotExecutionAppliedTrue);
        if (ctx.RuntimePromotionAppliedOverride) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.RuntimePromotionAppliedTrue);
        if (ctx.RuntimeRerankerChangedOverride) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.RuntimeRerankerChangedTrue);
        if (ctx.RuntimeRouterChangedOverride) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.RuntimeRouterChangedTrue);
        if (ctx.ProductionDecisionChangedOverride) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.ProductionDecisionChangedTrue);
        if (ctx.PackageOutputChangedOverride) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.PackageOutputChangedTrue);
        if (ctx.FormalPackageWrittenOverride) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.FormalPackageWrittenTrue);
        if (ctx.GlobalDefaultOnOverride) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.GlobalDefaultOnTrue);
        if (ctx.MLAuthorityOverride) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.MLAuthorityTrue);
        if (ctx.LLMAuthorityOverride) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.LLMAuthorityTrue);
        if (ctx.RuntimeAuthorityOverride) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.RuntimeAuthorityTrue);
        if (ctx.GateAuthorityOverride) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.GateAuthorityTrue);
        if (!ctx.V8ScopedActivationPreserved) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.V8ScopedActivationLost);
        if (mainlineEvidencePresent) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.MainlineTrustRegistryPresent);
        if (!rtPassed) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.P15GateNotPassed);
        var finalBlocked = blocked.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        var ready = finalBlocked.Length == 0;
        return new LearningFormalEvidenceRealizationPackDecision
        {
            Status = ready
                ? LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackReady
                : LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked,
            BlockedReasons = finalBlocked,
            Reasoning = ready
                ? "formal evidence realization pack policy ready — candidate computation + integrity manifest + rollback contract proceed; formal dataset stays untouched."
                : $"{finalBlocked.Length} blocked reason(s); formal evidence realization pack blocked."
        };
    }
}

public sealed record LearningFormalEvidenceRealizationPackScenario(
    string CaseName,
    LearningFormalEvidenceRealizationPackContext Context,
    bool RtPassed, bool P15Passed,
    bool MainlineEvidencePresent, bool MainlineRegistryPresent,
    string ExpectedStatus, string? ExpectedBlockedReason);

public sealed class LearningFormalEvidenceRealizationPackRunner
{
    private static readonly JsonSerializerOptions WriteIndented = new() { WriteIndented = true };

    public LearningFormalEvidenceRealizationPackReport Run(
        LearningFormalEvidenceRealizationPackContext realContext,
        string outputDir,
        bool rtPassed, bool p15Passed,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent,
        LearningFormalEvidenceRealizationPackOptions? opt = null)
    {
        opt ??= new LearningFormalEvidenceRealizationPackOptions();
        var now = DateTimeOffset.UtcNow;

        var cases = BuildScenarios().Select(static scenario =>
        {
            var decision = LearningFormalEvidenceRealizationPackPolicy.Evaluate(
                scenario.Context, scenario.RtPassed, scenario.P15Passed,
                scenario.MainlineEvidencePresent, scenario.MainlineRegistryPresent);
            var statusMatched = string.Equals(scenario.ExpectedStatus, decision.Status, StringComparison.Ordinal);
            var blockedReasonMatched = scenario.ExpectedBlockedReason is null
                || decision.BlockedReasons.Contains(scenario.ExpectedBlockedReason, StringComparer.Ordinal);
            return new LearningFormalEvidenceRealizationPackCase
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
        if (cases.Length < 25) blocked.Add("InsufficientLearningFormalEvidenceRealizationPackCases");
        if (cases.Any(static c => !c.PassedAsExpected)) blocked.Add("LearningFormalEvidenceRealizationPackMatrixFailed");
        foreach (var status in new[] {
            LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackReady,
            LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked })
        {
            if (!cases.Any(c => string.Equals(c.ActualStatus, status, StringComparison.Ordinal)))
                blocked.Add($"StatusBranchNotCovered:{status}");
        }

        var realDecision = LearningFormalEvidenceRealizationPackPolicy.Evaluate(
            realContext, rtPassed, p15Passed, mainlineEvidencePresent, mainlineRegistryPresent);
        if (!string.Equals(realDecision.Status, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackReady, StringComparison.Ordinal))
            blocked.AddRange(realDecision.BlockedReasons.Select(static x => $"RealLearningFormalEvidenceRealizationPack:{x}"));
        if (!rtPassed) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(LearningFormalEvidenceRealizationPackBlockedReasons.MainlineTrustRegistryPresent);

        var canBuild = string.Equals(realDecision.Status, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackReady, StringComparison.Ordinal);
        var candidates = new List<FormalLabelCandidate>();
        FormalLabelIntegrityManifest manifest = new();
        FormalLabelRealizationDecision realizationDecision = new();
        FormalLabelRollbackContract rollbackContract = new();
        var candidatesPath = string.Empty;
        var manifestPath = string.Empty;
        var decisionPath = string.Empty;
        var rollbackPath = string.Empty;

        if (canBuild)
        {
            // 1. Build formal label candidates — deterministic, integrity-checked, but NEVER formal labels.
            var samplesInPairs = new HashSet<string>(realContext.RankerPairs.Select(p => p.EvalSampleId), StringComparer.Ordinal);
            int idx = 0;
            foreach (var shadow in realContext.ShadowLabels.OrderBy(s => s.LabelId, StringComparer.Ordinal))
            {
                var hash = ComputeSha256(shadow.SourceCandidateSpecId + "|" + shadow.EvidencePath + "|" + shadow.ExpectedPreference);
                bool evidenceExists = !string.IsNullOrWhiteSpace(shadow.EvidencePath)
                    && shadow.EvidencePath.Contains("evalSampleId=", StringComparison.Ordinal)
                    && samplesInPairs.Any(s => shadow.EvidencePath.EndsWith("evalSampleId=" + s, StringComparison.Ordinal));
                bool preferenceValid = string.Equals(shadow.ExpectedPreference, "PositiveOverNegative", StringComparison.Ordinal);
                bool integrityOk = !string.IsNullOrWhiteSpace(hash) && evidenceExists && preferenceValid;
                var eligibility = integrityOk ? "Eligible" : (!preferenceValid ? "Rejected" : !evidenceExists ? "InvalidBinding" : "Pending");
                candidates.Add(new FormalLabelCandidate
                {
                    CandidateLabelId = $"flc-{idx++:D4}-{shadow.SourceSampleId}",
                    SourceShadowLabelId = shadow.LabelId,
                    EvidencePath = shadow.EvidencePath,
                    ExpectedPreference = shadow.ExpectedPreference,
                    DeterministicBindingHash = hash,
                    PromotionEligibility = eligibility,
                    LifecycleState = "Proposed",
                    IntegrityVerified = integrityOk,
                    FormalLabelCandidateIsFormal = false,
                    ShadowOnly = true,
                    AutoIngest = false,
                    PolicyVersion = "v10.16-formal-label-candidate/v1"
                });
            }
            candidatesPath = Path.Combine(outputDir, "formal-label-candidates.jsonl");
            WriteCandidatesJsonl(candidatesPath, candidates);

            // 2. Build integrity manifest — recompute hashes and verify match.
            manifest = BuildIntegrityManifest(candidates, realContext);
            manifestPath = Path.Combine(outputDir, "formal-label-integrity-manifest.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, WriteIndented), new UTF8Encoding(true));

            // 3. Realization decision — candidates are NOT formal labels until formal ingestion writes them.
            realizationDecision = BuildRealizationDecision(candidates, manifest, now);
            decisionPath = Path.Combine(outputDir, "formal-label-realization-decision.json");
            File.WriteAllText(decisionPath, JsonSerializer.Serialize(realizationDecision, WriteIndented), new UTF8Encoding(true));

            // 4. Rollback contract — schema only.
            rollbackContract = BuildRollbackContract();
            rollbackPath = Path.Combine(outputDir, "formal-label-rollback-contract.json");
            File.WriteAllText(rollbackPath, JsonSerializer.Serialize(rollbackContract, WriteIndented), new UTF8Encoding(true));
        }

        // Authority leak checks on produced artifacts
        foreach (var c in candidates)
        {
            if (c.FormalLabelCandidateIsFormal) blocked.Add($"FormalLabelCandidateIsFormalLeak:{c.CandidateLabelId}");
            if (!c.ShadowOnly) blocked.Add($"FormalLabelCandidateShadowOnlyFalse:{c.CandidateLabelId}");
            if (c.AutoIngest) blocked.Add($"FormalLabelCandidateAutoIngestLeak:{c.CandidateLabelId}");
        }
        if (manifest.AnyHashMismatch) blocked.Add("IntegrityManifestHashMismatchDetected");
        if (realizationDecision.AIArbitration || realizationDecision.HumanReviewAsGateAuthority
            || realizationDecision.HumanFeedbackAutoIngest || realizationDecision.FormalTrainingSetChanged
            || realizationDecision.FormalLabelCandidatesAreFormal
            || realizationDecision.FormalLabelsRealized) blocked.Add("RealizationDecisionAuthorityOrIngestLeak");
        // Honesty invariant: pilot ready while labels unrealized → block
        if (realizationDecision.RuntimePilotExecutionReadyForSeparateGate && !realizationDecision.FormalLabelsRealized)
            blocked.Add("RealizationDecisionPilotReadyWhileFormalLabelsUnrealizedLeak");
        if (rollbackContract.FormalTrainingSetChanged || rollbackContract.AutoIngest || rollbackContract.RuntimeRollbackApplied)
            blocked.Add("RollbackContractRuntimeOrIngestLeak");

        var finalBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var passed = finalBlocked.Length == 0;

        return new LearningFormalEvidenceRealizationPackReport
        {
            OperationId = $"v10-learning-formal-evidence-realization-pack-{Guid.NewGuid():N}",
            CreatedAt = now,
            FormalEvidenceRealizationPackPassed = passed,
            GatePassed = opt.IsGate && passed,
            TotalCases = cases.Length,
            PassedCases = cases.Count(static c => c.PassedAsExpected),
            FailedCases = cases.Count(static c => !c.PassedAsExpected),
            ReadyCases = cases.Count(c => string.Equals(c.ActualStatus, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackReady, StringComparison.Ordinal)),
            BlockedCases = cases.Count(c => string.Equals(c.ActualStatus, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, StringComparison.Ordinal)),
            Cases = cases,
            FormalLabelCandidates = candidates,
            FormalLabelIntegrityManifest = manifest,
            FormalLabelRealizationDecision = realizationDecision,
            FormalLabelRollbackContract = rollbackContract,
            FormalLabelCandidatesReady = canBuild && candidates.Count > 0,
            FormalLabelIntegrityManifestReady = canBuild && manifest.Entries.Count > 0,
            FormalLabelRealizationDecisionReady = canBuild,
            FormalLabelRollbackContractReady = canBuild && rollbackContract.Fields.Count > 0,
            FormalLabelCandidateCount = candidates.Count,
            RealizableFormalLabelCount = realizationDecision.RealizableFormalLabelCount,
            InvalidBindingCount = realizationDecision.InvalidBindingCount,
            FormalLabelsRealized = realizationDecision.FormalLabelsRealized,
            FormalEvidenceSufficient = realizationDecision.FormalEvidenceSufficient,
            RuntimePilotExecutionReadyForSeparateGate = realizationDecision.RuntimePilotExecutionReadyForSeparateGate,
            BlockedForRuntimePilotExecutionBy = realizationDecision.BlockedForRuntimePilotExecutionBy,
            FormalLabelCandidatesAreFormal = false,
            ShadowOnly = true,
            FormalTrainingSetChanged = false,
            AutoIngest = false,
            TrainingSetChanged = false,
            HumanFeedbackAsSignal = true,
            HumanReviewAsGateAuthority = false,
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
            V8ScopedActivationPreserved = realContext.V8ScopedActivationPreserved,
            UpstreamFormalEvidenceBoundaryPackGatePresent = realContext.FormalEvidenceBoundaryPresent,
            UpstreamFormalEvidenceBoundaryPackGatePassed = realContext.FormalEvidenceBoundaryPassed,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent,
            FormalLabelCandidatesPath = candidatesPath,
            FormalLabelIntegrityManifestPath = manifestPath,
            FormalLabelRealizationDecisionPath = decisionPath,
            FormalLabelRollbackContractPath = rollbackPath,
            Recommendation = passed ? "ProceedToControlledFormalLabelIngestionViaV9.5" : "Blocked",
            NextAllowedPhase = passed ? "ControlledFormalLabelIngestion-pending-V9.5-human-feedback" : string.Empty,
            BlockedReasons = finalBlocked,
            Diagnostics = new[]
            {
                $"total={cases.Length}",
                $"realStatus={realDecision.Status}",
                $"formalLabelCandidateCount={candidates.Count}",
                $"realizableFormalLabelCount={realizationDecision.RealizableFormalLabelCount}",
                $"invalidBindingCount={realizationDecision.InvalidBindingCount}",
                $"hashVerifiedEntries={manifest.VerifiedEntries}",
                $"hashMismatchedEntries={manifest.MismatchedEntries}",
                $"formalLabelsRealized={realizationDecision.FormalLabelsRealized}",
                $"formalEvidenceSufficient={realizationDecision.FormalEvidenceSufficient}",
                $"runtimePilotReady={realizationDecision.RuntimePilotExecutionReadyForSeparateGate}"
            }
        };
    }

    private static IReadOnlyList<LearningFormalEvidenceRealizationPackScenario> BuildScenarios()
    {
        var clean = BuildCleanContext();
        return [
            new("AllUpstreamClean", clean, true, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackReady, null),
            new("FormalEvidenceBoundaryMissing", clean with { FormalEvidenceBoundaryPresent = false }, true, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.FormalEvidenceBoundaryMissing),
            new("FormalEvidenceBoundaryNotPassed", clean with { FormalEvidenceBoundaryPassed = false }, true, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.FormalEvidenceBoundaryNotPassed),
            new("RealizationContractMissing", clean with { RealizationContractPresent = false }, true, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.RealizationContractMissing),
            new("ShadowLabelsMissing", clean with { ShadowLabelsPresent = false, ShadowLabelCount = 0 }, true, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.ShadowLabelsMissing),
            new("HashMismatchDetected", clean with { HashMismatchOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.HashMismatchDetected),
            new("EvidencePathMissing", clean with { EvidencePathMissingOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.EvidencePathMissing),
            new("ExpectedPreferenceMismatch", clean with { ExpectedPreferenceMismatchOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.ExpectedPreferenceMismatch),
            new("FormalCandidatesTreatedAsFormalLabels", clean with { FormalCandidatesTreatedAsFormalLabelsOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.FormalCandidatesTreatedAsFormalLabels),
            new("AutoIngestTrue", clean with { AutoIngestOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.AutoIngestTrue),
            new("FormalTrainingSetChangedUnexpectedly", clean with { FormalTrainingSetChangedOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.FormalTrainingSetChangedUnexpectedly),
            new("HumanReviewAsGateAuthorityTrue", clean with { HumanReviewAsGateAuthorityOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.HumanReviewAsGateAuthorityTrue),
            new("HumanFeedbackAutoIngestTrue", clean with { HumanFeedbackAutoIngestOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.HumanFeedbackAutoIngestTrue),
            new("RuntimePilotReadyWhileFormalLabelsUnrealized", clean with { RuntimePilotReadyWhileFormalLabelsUnrealizedOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.RuntimePilotReadyWhileFormalLabelsUnrealized),
            new("RuntimePilotExecutionAppliedTrue", clean with { RuntimePilotExecutionAppliedOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.RuntimePilotExecutionAppliedTrue),
            new("RuntimePromotionAppliedTrue", clean with { RuntimePromotionAppliedOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.RuntimePromotionAppliedTrue),
            new("RuntimeRerankerChangedTrue", clean with { RuntimeRerankerChangedOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.RuntimeRerankerChangedTrue),
            new("RuntimeRouterChangedTrue", clean with { RuntimeRouterChangedOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.RuntimeRouterChangedTrue),
            new("ProductionDecisionChangedTrue", clean with { ProductionDecisionChangedOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.ProductionDecisionChangedTrue),
            new("PackageOutputChangedTrue", clean with { PackageOutputChangedOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.PackageOutputChangedTrue),
            new("FormalPackageWrittenTrue", clean with { FormalPackageWrittenOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.FormalPackageWrittenTrue),
            new("GlobalDefaultOnTrue", clean with { GlobalDefaultOnOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.GlobalDefaultOnTrue),
            new("MLAuthorityTrue", clean with { MLAuthorityOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.MLAuthorityTrue),
            new("LLMAuthorityTrue", clean with { LLMAuthorityOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.LLMAuthorityTrue),
            new("RuntimeAuthorityTrue", clean with { RuntimeAuthorityOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.RuntimeAuthorityTrue),
            new("GateAuthorityTrue", clean with { GateAuthorityOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.GateAuthorityTrue),
            new("V8ScopedActivationLost", clean with { V8ScopedActivationPreserved = false }, true, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.V8ScopedActivationLost),
            new("MainlineEvidencePresent", clean, true, true, true, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.MainlineEvidencePresent),
            new("MainlineTrustRegistryPresent", clean, true, true, false, true, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.MainlineTrustRegistryPresent),
            new("RuntimeGateNotPassed", clean, false, true, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.RuntimeChangeGateNotPassed),
            new("P15GateNotPassed", clean, true, false, false, false, LearningFormalEvidenceRealizationPackStatuses.LearningFormalEvidenceRealizationPackBlocked, LearningFormalEvidenceRealizationPackBlockedReasons.P15GateNotPassed)
        ];
    }

    private static LearningFormalEvidenceRealizationPackContext BuildCleanContext() => new()
    {
        FormalEvidenceBoundaryPresent = true,
        FormalEvidenceBoundaryPassed = true,
        RealizationContractPresent = true,
        ShadowLabelsPresent = true,
        ShadowLabelCount = 60,
        V8ScopedActivationPreserved = true,
        ShadowLabels = Array.Empty<EvidenceBoundShadowLabel>(),
        RankerPairs = Array.Empty<RankerPair>()
    };

    private static FormalLabelIntegrityManifest BuildIntegrityManifest(
        IReadOnlyList<FormalLabelCandidate> candidates,
        LearningFormalEvidenceRealizationPackContext ctx)
    {
        var entries = new List<FormalLabelIntegrityManifestEntry>();
        int verified = 0;
        int mismatched = 0;
        // Reconstruct expected hash from shadow label data and compare.
        var shadowById = ctx.ShadowLabels.ToDictionary(s => s.LabelId, s => s, StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            var shadow = shadowById.GetValueOrDefault(c.SourceShadowLabelId);
            var expected = shadow is not null
                ? ComputeSha256(shadow.SourceCandidateSpecId + "|" + shadow.EvidencePath + "|" + shadow.ExpectedPreference)
                : string.Empty;
            var matches = !string.IsNullOrEmpty(expected) && string.Equals(c.DeterministicBindingHash, expected, StringComparison.Ordinal);
            if (matches) verified++; else mismatched++;
            entries.Add(new FormalLabelIntegrityManifestEntry
            {
                CandidateLabelId = c.CandidateLabelId,
                SourceShadowLabelId = c.SourceShadowLabelId,
                EvidencePath = c.EvidencePath,
                ExpectedPreference = c.ExpectedPreference,
                DeterministicBindingHash = c.DeterministicBindingHash,
                ExpectedHash = expected,
                HashMatches = matches,
                IntegrityStatus = matches
                    ? (c.IntegrityVerified ? "Verified" : "HashOnlyVerified-EvidenceUnresolved")
                    : "HashMismatch"
            });
        }
        return new FormalLabelIntegrityManifest
        {
            ManifestId = $"v10-formal-label-integrity-manifest-{Guid.NewGuid():N}",
            ManifestVersion = "v10.16-formal-label-integrity/v1",
            HashAlgorithm = "SHA-256",
            TotalEntries = entries.Count,
            VerifiedEntries = verified,
            MismatchedEntries = mismatched,
            Entries = entries,
            AnyHashMismatch = mismatched > 0,
            ShadowOnly = true
        };
    }

    private static FormalLabelRealizationDecision BuildRealizationDecision(
        IReadOnlyList<FormalLabelCandidate> candidates,
        FormalLabelIntegrityManifest manifest,
        DateTimeOffset now)
    {
        var realizable = candidates.Count(c => c.IntegrityVerified && c.PromotionEligibility == "Eligible");
        var invalid = candidates.Count(c => c.PromotionEligibility == "InvalidBinding" || c.PromotionEligibility == "Rejected");
        // Even with all candidates eligible, formal labels are NOT realized — we never write the formal dataset.
        var formalRealized = false;
        var formalSufficient = formalRealized;  // strictly couples to realization
        var pilotReady = formalSufficient;
        var blockedBy = new List<string>();
        if (!formalRealized) blockedBy.Add("FormalLabelsNotRealizedInDataset");
        if (!formalSufficient) blockedBy.Add("FormalEvidenceInsufficientV3");
        if (manifest.AnyHashMismatch) blockedBy.Add($"IntegrityManifestHashMismatch:{manifest.MismatchedEntries}");
        var notes = new List<string>
        {
            $"FormalLabelCandidateCount={candidates.Count}",
            $"RealizableFormalLabelCount={realizable} (passed integrity + eligibility checks)",
            $"InvalidBindingCount={invalid}",
            $"IntegrityManifest: verified={manifest.VerifiedEntries}, mismatched={manifest.MismatchedEntries}",
            "Candidates are NOT formal labels: they are eligible-for-promotion descriptors. The formal dataset has not been changed.",
            "FormalLabelsRealized stays false until controlled formal ingestion (V9.5 feedback pipeline) actually writes labels into the formal training set under human review.",
            "RuntimePilotExecutionReadyForSeparateGate stays false until FormalEvidenceSufficient becomes true."
        };
        return new FormalLabelRealizationDecision
        {
            DecisionId = $"v10-formal-label-realization-decision-{Guid.NewGuid():N}",
            CreatedAt = now.ToString("O"),
            FormalLabelCandidateCount = candidates.Count,
            RealizableFormalLabelCount = realizable,
            InvalidBindingCount = invalid,
            FormalLabelCandidatesReady = candidates.Count > 0,
            FormalLabelsRealized = formalRealized,
            FormalEvidenceSufficient = formalSufficient,
            RuntimePilotExecutionReadyForSeparateGate = pilotReady,
            BlockedForRuntimePilotExecutionBy = blockedBy,
            HumanFeedbackAsSignal = true,
            HumanReviewAsGateAuthority = false,
            HumanFeedbackAutoIngest = false,
            FormalTrainingSetChanged = false,
            FormalLabelCandidatesAreFormal = false,
            AIArbitration = false,
            EvidenceSourcesConsidered = new[]
            {
                "learning/v10/formal-evidence-boundary-pack-gate.json",
                "learning/v10/formal-label-realization-contract.json",
                "learning/v10/formal-label-gap-report.json",
                "learning/v10/evidence-bound-hard-negative-labels.jsonl",
                "learning/v10/pre-pilot-readiness-decision.json"
            },
            DecisionNotes = notes
        };
    }

    private static FormalLabelRollbackContract BuildRollbackContract()
        => new()
        {
            ContractVersion = "v10.16-formal-label-rollback/v1",
            ContractMode = "SchemaOnlyNoRollbackExecuted",
            Fields = new[]
            {
                new FormalLabelRollbackContractField { FieldName = "rollbackId", Type = "string", Required = true, Description = "Stable id for the rollback operation; immutable once issued." },
                new FormalLabelRollbackContractField { FieldName = "targetFormalLabelIds", Type = "string[]", Required = true, Description = "Formal label ids to retract (must reference labels that were actually realized into the formal training set)." },
                new FormalLabelRollbackContractField { FieldName = "rollbackReason", Type = "string", Required = true, Description = "Reason code: HashMismatch / EvidencePathInvalid / OperatorRequest / DownstreamFailure." },
                new FormalLabelRollbackContractField { FieldName = "rollbackTrigger", Type = "string", Required = true, Description = "What surfaced the need: Integrity / Operator / Downstream." },
                new FormalLabelRollbackContractField { FieldName = "rollbackSnapshotReference", Type = "string", Required = true, Description = "Path to the formal-training-set snapshot from before the rollback (V8-style restore reference)." },
                new FormalLabelRollbackContractField { FieldName = "revocationRecordReference", Type = "string", Required = true, Description = "Path to the revocation record produced by the rollback (V8.18 revocation contract)." },
                new FormalLabelRollbackContractField { FieldName = "lifecycleStateAfter", Type = "string", Required = true, Description = "Must be one of Retracted / Quarantined; never Realized." },
                new FormalLabelRollbackContractField { FieldName = "executedAt", Type = "datetime", Required = false, Description = "Set when the rollback was applied (this contract never sets it)." }
            },
            RollbackTriggers = new[]
            {
                "IntegrityManifest hash mismatch on any realized label",
                "Evidence path invalidated (source ranking pair removed or rewritten)",
                "Downstream pilot detects regression vs reference",
                "Operator-initiated rollback under V9.5 review"
            },
            RollbackPreconditions = new[]
            {
                "Rollback may only be issued AFTER labels are actually realized (FormalLabelsRealized=true).",
                "Rollback MUST reference an existing formal-training-set snapshot (V8-style rollback binding required).",
                "Rollback MUST emit a revocation record under the V8.18 schema.",
                "Rollback MUST keep V8 scoped activation state intact (no cross-pollination with V8 evidence)."
            },
            RollbackActions = new[]
            {
                "Read snapshot, restore formal training set entries to pre-realization state.",
                "Mark affected labels LifecycleState=Retracted in the integrity manifest.",
                "Emit revocation record per V8.18 contract.",
                "Notify human reviewers (signal, not authority)."
            },
            FormalTrainingSetChanged = false,
            AutoIngest = false,
            RuntimeRollbackApplied = false
        };

    private static string ComputeSha256(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static void WriteCandidatesJsonl(string path, IReadOnlyList<FormalLabelCandidate> candidates)
    {
        var sb = new StringBuilder();
        foreach (var c in candidates) sb.AppendLine(JsonSerializer.Serialize(c));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    public static string BuildMarkdown(string title, LearningFormalEvidenceRealizationPackReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"- FormalEvidenceRealizationPackPassed: `{report.FormalEvidenceRealizationPackPassed}`");
        sb.AppendLine($"- GatePassed: `{report.GatePassed}`");
        sb.AppendLine($"- TotalCases: `{report.TotalCases}` PassedCases: `{report.PassedCases}` FailedCases: `{report.FailedCases}`");
        sb.AppendLine();
        sb.AppendLine("## Formal Label Realization Decision");
        sb.AppendLine($"- FormalLabelCandidateCount: `{report.FormalLabelCandidateCount}`");
        sb.AppendLine($"- RealizableFormalLabelCount: `{report.RealizableFormalLabelCount}`");
        sb.AppendLine($"- InvalidBindingCount: `{report.InvalidBindingCount}`");
        sb.AppendLine($"- **FormalLabelCandidatesReady**: `{report.FormalLabelCandidatesReady}`");
        sb.AppendLine($"- **FormalLabelsRealized**: `{report.FormalLabelsRealized}` (must remain false until controlled formal ingestion)");
        sb.AppendLine($"- **FormalEvidenceSufficient**: `{report.FormalEvidenceSufficient}`");
        sb.AppendLine($"- **RuntimePilotExecutionReadyForSeparateGate**: `{report.RuntimePilotExecutionReadyForSeparateGate}`");
        if (report.BlockedForRuntimePilotExecutionBy.Count > 0)
            sb.AppendLine($"- BlockedForRuntimePilotExecutionBy: `{string.Join(", ", report.BlockedForRuntimePilotExecutionBy)}`");
        sb.AppendLine();
        sb.AppendLine("## Integrity Manifest");
        sb.AppendLine($"- HashAlgorithm: `{report.FormalLabelIntegrityManifest.HashAlgorithm}`");
        sb.AppendLine($"- TotalEntries: `{report.FormalLabelIntegrityManifest.TotalEntries}`");
        sb.AppendLine($"- VerifiedEntries: `{report.FormalLabelIntegrityManifest.VerifiedEntries}`");
        sb.AppendLine($"- MismatchedEntries: `{report.FormalLabelIntegrityManifest.MismatchedEntries}`");
        sb.AppendLine($"- AnyHashMismatch: `{report.FormalLabelIntegrityManifest.AnyHashMismatch}`");
        sb.AppendLine();
        sb.AppendLine("## Rollback Contract");
        sb.AppendLine($"- ContractVersion: `{report.FormalLabelRollbackContract.ContractVersion}`");
        sb.AppendLine($"- ContractMode: `{report.FormalLabelRollbackContract.ContractMode}`");
        sb.AppendLine($"- Fields: `{report.FormalLabelRollbackContract.Fields.Count}` Triggers: `{report.FormalLabelRollbackContract.RollbackTriggers.Count}` Preconditions: `{report.FormalLabelRollbackContract.RollbackPreconditions.Count}` Actions: `{report.FormalLabelRollbackContract.RollbackActions.Count}`");
        sb.AppendLine($"- FormalTrainingSetChanged: `{report.FormalLabelRollbackContract.FormalTrainingSetChanged}` AutoIngest: `{report.FormalLabelRollbackContract.AutoIngest}` RuntimeRollbackApplied: `{report.FormalLabelRollbackContract.RuntimeRollbackApplied}`");
        sb.AppendLine();
        sb.AppendLine("## Authority Invariants");
        sb.AppendLine($"- ShadowOnly: `{report.ShadowOnly}` FormalLabelCandidatesAreFormal: `{report.FormalLabelCandidatesAreFormal}` FormalTrainingSetChanged: `{report.FormalTrainingSetChanged}`");
        sb.AppendLine($"- AutoIngest: `{report.AutoIngest}` TrainingSetChanged: `{report.TrainingSetChanged}`");
        sb.AppendLine($"- HumanFeedbackAsSignal: `{report.HumanFeedbackAsSignal}` HumanReviewAsGateAuthority: `{report.HumanReviewAsGateAuthority}` HumanFeedbackAutoIngest: `{report.HumanFeedbackAutoIngest}`");
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
}

public sealed class LearningFormalEvidenceRealizationPackCase
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

public sealed class LearningFormalEvidenceRealizationPackReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool FormalEvidenceRealizationPackPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public int ReadyCases { get; init; }
    public int BlockedCases { get; init; }
    public IReadOnlyList<LearningFormalEvidenceRealizationPackCase> Cases { get; init; } = Array.Empty<LearningFormalEvidenceRealizationPackCase>();
    public IReadOnlyList<FormalLabelCandidate> FormalLabelCandidates { get; init; } = Array.Empty<FormalLabelCandidate>();
    public FormalLabelIntegrityManifest FormalLabelIntegrityManifest { get; init; } = new();
    public FormalLabelRealizationDecision FormalLabelRealizationDecision { get; init; } = new();
    public FormalLabelRollbackContract FormalLabelRollbackContract { get; init; } = new();
    public bool FormalLabelCandidatesReady { get; init; }
    public bool FormalLabelIntegrityManifestReady { get; init; }
    public bool FormalLabelRealizationDecisionReady { get; init; }
    public bool FormalLabelRollbackContractReady { get; init; }
    public int FormalLabelCandidateCount { get; init; }
    public int RealizableFormalLabelCount { get; init; }
    public int InvalidBindingCount { get; init; }
    public bool FormalLabelsRealized { get; init; }
    public bool FormalEvidenceSufficient { get; init; }
    public bool RuntimePilotExecutionReadyForSeparateGate { get; init; }
    public IReadOnlyList<string> BlockedForRuntimePilotExecutionBy { get; init; } = Array.Empty<string>();
    public bool FormalLabelCandidatesAreFormal { get; init; }
    public bool ShadowOnly { get; init; }
    public bool FormalTrainingSetChanged { get; init; }
    public bool AutoIngest { get; init; }
    public bool TrainingSetChanged { get; init; }
    public bool HumanFeedbackAsSignal { get; init; }
    public bool HumanReviewAsGateAuthority { get; init; }
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
    public bool V8ScopedActivationPreserved { get; init; }
    public bool UpstreamFormalEvidenceBoundaryPackGatePresent { get; init; }
    public bool UpstreamFormalEvidenceBoundaryPackGatePassed { get; init; }
    public bool MainlineEvidencePresent { get; init; }
    public bool MainlineTrustRegistryPresent { get; init; }
    public string FormalLabelCandidatesPath { get; init; } = string.Empty;
    public string FormalLabelIntegrityManifestPath { get; init; } = string.Empty;
    public string FormalLabelRealizationDecisionPath { get; init; } = string.Empty;
    public string FormalLabelRollbackContractPath { get; init; } = string.Empty;
    public string Recommendation { get; init; } = string.Empty;
    public string NextAllowedPhase { get; init; } = string.Empty;
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class LearningFormalEvidenceRealizationPackOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
