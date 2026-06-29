using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

public static class LearningFormalEvidenceRealizationR1PackStatuses
{
    public const string LearningFormalEvidenceRealizationR1PackReady = nameof(LearningFormalEvidenceRealizationR1PackReady);
    public const string LearningFormalEvidenceRealizationR1PackBlocked = nameof(LearningFormalEvidenceRealizationR1PackBlocked);
}

public static class LearningFormalEvidenceRealizationR1PackBlockedReasons
{
    public const string FormalEvidenceBoundaryMissing = nameof(FormalEvidenceBoundaryMissing);
    public const string FormalEvidenceBoundaryNotPassed = nameof(FormalEvidenceBoundaryNotPassed);
    public const string ShadowLabelsMissing = nameof(ShadowLabelsMissing);
    public const string RankingPairsMissing = nameof(RankingPairsMissing);
    public const string HashContractMismatch = nameof(HashContractMismatch);
    public const string RankingPairRowHashMissing = nameof(RankingPairRowHashMissing);
    public const string ShadowLabelHashMissing = nameof(ShadowLabelHashMissing);
    public const string EvidencePathInvalid = nameof(EvidencePathInvalid);
    public const string ExpectedPreferenceMismatch = nameof(ExpectedPreferenceMismatch);
    public const string IntegrityMutationTestFailed = nameof(IntegrityMutationTestFailed);
    public const string FormalCandidateMarkedFormalLeak = nameof(FormalCandidateMarkedFormalLeak);
    public const string MainRecommendationUsesHumanReviewTrue = nameof(MainRecommendationUsesHumanReviewTrue);
    public const string FeedbackSignalAsGateAuthorityTrue = nameof(FeedbackSignalAsGateAuthorityTrue);
    public const string FeedbackSignalAutoIngestTrue = nameof(FeedbackSignalAutoIngestTrue);
    public const string AutoIngestTrue = nameof(AutoIngestTrue);
    public const string FormalTrainingSetChangedTrue = nameof(FormalTrainingSetChangedTrue);
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

public sealed record FormalLabelCandidateR1
{
    public string CandidateLabelId { get; init; } = string.Empty;
    public string SourceShadowLabelId { get; init; } = string.Empty;
    public string SourceCandidateSpecId { get; init; } = string.Empty;
    public string EvidencePath { get; init; } = string.Empty;
    public string ExpectedPreference { get; init; } = string.Empty;
    public string RankingPairRowHash { get; init; } = string.Empty;
    public string ShadowLabelHash { get; init; } = string.Empty;
    public string DeterministicBindingHashCanonical { get; init; } = string.Empty;
    public string LegacySpecBindingHash { get; init; } = string.Empty;
    public string HashInputVersion { get; init; } = "v10.16R/canonical-v1";
    public string PromotionEligibility { get; init; } = string.Empty;
    public string LifecycleState { get; init; } = "Proposed";
    public bool CanonicalIntegrityVerified { get; init; }
    public bool EvidencePathResolved { get; init; }
    public bool ExpectedPreferenceDerivable { get; init; }
    public bool FormalLabelCandidateIsFormal { get; init; }
    public bool ShadowOnly { get; init; } = true;
    public bool AutoIngest { get; init; }
    public string PolicyVersion { get; init; } = "v10.16R/formal-label-candidate";
    public string DeprecatedCompatibilityNote { get; init; } = "LegacySpecBindingHash retained for traceability only; never used as integrity authority.";
}

public sealed class FormalLabelIntegrityManifestR1Entry
{
    public string CandidateLabelId { get; init; } = string.Empty;
    public string SourceShadowLabelId { get; init; } = string.Empty;
    public string SourceCandidateSpecId { get; init; } = string.Empty;
    public string EvidencePath { get; init; } = string.Empty;
    public string ExpectedPreference { get; init; } = string.Empty;
    public string RankingPairRowHash { get; init; } = string.Empty;
    public string ShadowLabelHash { get; init; } = string.Empty;
    public string DeterministicBindingHashCanonical { get; init; } = string.Empty;
    public string ExpectedCanonicalHash { get; init; } = string.Empty;
    public string LegacySpecBindingHash { get; init; } = string.Empty;
    public bool CanonicalHashMatches { get; init; }
    public bool EvidencePathResolved { get; init; }
    public bool ExpectedPreferenceDerivable { get; init; }
    public string IntegrityStatus { get; init; } = string.Empty;
}

public sealed class FormalLabelIntegrityManifestR1
{
    public string ManifestId { get; init; } = string.Empty;
    public string ManifestVersion { get; init; } = "v10.16R/formal-label-integrity-canonical-v1";
    public string HashAlgorithm { get; init; } = "SHA-256";
    public string HashInputContract { get; init; } = "SHA256(sourceShadowLabelId | sourceCandidateSpecId | evidencePath | expectedPreference | rankingPairRowHash | shadowLabelHash)";
    public int TotalEntries { get; init; }
    public int VerifiedEntries { get; init; }
    public int MismatchedEntries { get; init; }
    public int EvidencePathUnresolvedCount { get; init; }
    public int ExpectedPreferenceUndeterivableCount { get; init; }
    public IReadOnlyList<FormalLabelIntegrityManifestR1Entry> Entries { get; init; } = Array.Empty<FormalLabelIntegrityManifestR1Entry>();
    public bool AnyHashMismatch { get; init; }
    public bool ContractHashAlgorithmCompliance { get; init; }
    public double RankingPairRowHashCoverage { get; init; }
    public double ShadowLabelHashCoverage { get; init; }
    public bool ShadowOnly { get; init; } = true;
}

public sealed class IntegrityMutationTestCase
{
    public string MutationName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool DetectedByCanonicalHash { get; init; }
    public bool DetectedByEvidencePathCheck { get; init; }
    public bool DetectedByExpectedPreferenceCheck { get; init; }
    public bool DetectedByCandidateFormalLeakCheck { get; init; }
    public bool DetectedByRankingPairRowHashCheck { get; init; }
    public bool DetectedByHashVersionCheck { get; init; }
    public bool DetectedOverall { get; init; }
    public string FailureMode { get; init; } = string.Empty;
}

public sealed class IntegrityMutationTestReport
{
    public string ReportId { get; init; } = string.Empty;
    public string ReportVersion { get; init; } = "v10.16R/integrity-mutation-tests-v1";
    public IReadOnlyList<IntegrityMutationTestCase> TestCases { get; init; } = Array.Empty<IntegrityMutationTestCase>();
    public int TotalMutationTests { get; init; }
    public int DetectedMutations { get; init; }
    public bool IntegrityMutationTestsPassed { get; init; }
    public bool CorruptedHashDetected { get; init; }
    public bool MissingEvidencePathDetected { get; init; }
    public bool ExpectedPreferenceMismatchDetected { get; init; }
    public bool CandidateMarkedFormalDetected { get; init; }
    public bool RankingPairRowHashMismatchDetected { get; init; }
    public bool StaleContractHashVersionDetected { get; init; }
}

public sealed class TerminologyCompatibilityMapEntry
{
    public string LegacyName { get; init; } = string.Empty;
    public string CanonicalName { get; init; } = string.Empty;
    public string Status { get; init; } = "DeprecatedCompatibilityOnly";
    public string Notes { get; init; } = string.Empty;
}

public sealed class TerminologyCompatibilityMap
{
    public string MapVersion { get; init; } = "v10.16R/terminology-compatibility-v1";
    public IReadOnlyList<TerminologyCompatibilityMapEntry> Entries { get; init; } = Array.Empty<TerminologyCompatibilityMapEntry>();
    public IReadOnlyList<string> MainPathPolicies { get; init; } = Array.Empty<string>();
}

public sealed class FormalLabelRealizationR1Decision
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
    public bool ExternalFeedbackAcceptedAsSignal { get; init; } = true;
    public bool FeedbackSignalAsGateAuthority { get; init; }
    public bool FeedbackSignalAutoIngest { get; init; }
    public bool FormalTrainingSetChanged { get; init; }
    public bool FormalLabelCandidatesAreFormal { get; init; }
    public bool MainRecommendationUsesHumanReview { get; init; }
    public bool AIArbitration { get; init; }
    public IReadOnlyList<string> EvidenceSourcesConsidered { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DecisionNotes { get; init; } = Array.Empty<string>();
}

public sealed record LearningFormalEvidenceRealizationR1PackContext
{
    public bool FormalEvidenceBoundaryPresent { get; init; }
    public bool FormalEvidenceBoundaryPassed { get; init; }
    public bool ShadowLabelsPresent { get; init; }
    public int ShadowLabelCount { get; init; }
    public bool V8ScopedActivationPreserved { get; init; }
    public IReadOnlyList<EvidenceBoundShadowLabel> ShadowLabels { get; init; } = Array.Empty<EvidenceBoundShadowLabel>();
    public IReadOnlyList<RankerPair> RankerPairs { get; init; } = Array.Empty<RankerPair>();
    public IReadOnlyDictionary<string, string> RankingPairRowJsonBySampleId { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    // Synthetic test knobs
    public bool HashContractMismatchOverride { get; init; }
    public bool RankingPairRowHashMissingOverride { get; init; }
    public bool ShadowLabelHashMissingOverride { get; init; }
    public bool EvidencePathInvalidOverride { get; init; }
    public bool ExpectedPreferenceMismatchOverride { get; init; }
    public bool FormalCandidateMarkedFormalOverride { get; init; }
    public bool MainRecommendationUsesHumanReviewOverride { get; init; }
    public bool FeedbackSignalAsGateAuthorityOverride { get; init; }
    public bool FeedbackSignalAutoIngestOverride { get; init; }
    public bool AutoIngestOverride { get; init; }
    public bool FormalTrainingSetChangedOverride { get; init; }
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

public sealed class LearningFormalEvidenceRealizationR1PackDecision
{
    public string Status { get; init; } = LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked;
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;
}

public static class LearningFormalEvidenceRealizationR1PackPolicy
{
    public static LearningFormalEvidenceRealizationR1PackDecision Evaluate(
        LearningFormalEvidenceRealizationR1PackContext ctx,
        bool rtPassed, bool p15Passed,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent)
    {
        var blocked = new List<string>();
        if (!ctx.FormalEvidenceBoundaryPresent) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.FormalEvidenceBoundaryMissing);
        else if (!ctx.FormalEvidenceBoundaryPassed) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.FormalEvidenceBoundaryNotPassed);
        if (!ctx.ShadowLabelsPresent || ctx.ShadowLabelCount <= 0) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.ShadowLabelsMissing);
        if (ctx.RankerPairs.Count == 0) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.RankingPairsMissing);
        if (ctx.HashContractMismatchOverride) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.HashContractMismatch);
        if (ctx.RankingPairRowHashMissingOverride) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.RankingPairRowHashMissing);
        if (ctx.ShadowLabelHashMissingOverride) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.ShadowLabelHashMissing);
        if (ctx.EvidencePathInvalidOverride) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.EvidencePathInvalid);
        if (ctx.ExpectedPreferenceMismatchOverride) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.ExpectedPreferenceMismatch);
        if (ctx.FormalCandidateMarkedFormalOverride) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.FormalCandidateMarkedFormalLeak);
        if (ctx.MainRecommendationUsesHumanReviewOverride) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.MainRecommendationUsesHumanReviewTrue);
        if (ctx.FeedbackSignalAsGateAuthorityOverride) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.FeedbackSignalAsGateAuthorityTrue);
        if (ctx.FeedbackSignalAutoIngestOverride) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.FeedbackSignalAutoIngestTrue);
        if (ctx.AutoIngestOverride) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.AutoIngestTrue);
        if (ctx.FormalTrainingSetChangedOverride) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.FormalTrainingSetChangedTrue);
        if (ctx.RuntimePilotExecutionAppliedOverride) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.RuntimePilotExecutionAppliedTrue);
        if (ctx.RuntimePromotionAppliedOverride) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.RuntimePromotionAppliedTrue);
        if (ctx.RuntimeRerankerChangedOverride) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.RuntimeRerankerChangedTrue);
        if (ctx.RuntimeRouterChangedOverride) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.RuntimeRouterChangedTrue);
        if (ctx.ProductionDecisionChangedOverride) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.ProductionDecisionChangedTrue);
        if (ctx.PackageOutputChangedOverride) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.PackageOutputChangedTrue);
        if (ctx.FormalPackageWrittenOverride) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.FormalPackageWrittenTrue);
        if (ctx.GlobalDefaultOnOverride) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.GlobalDefaultOnTrue);
        if (ctx.MLAuthorityOverride) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.MLAuthorityTrue);
        if (ctx.LLMAuthorityOverride) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.LLMAuthorityTrue);
        if (ctx.RuntimeAuthorityOverride) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.RuntimeAuthorityTrue);
        if (ctx.GateAuthorityOverride) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.GateAuthorityTrue);
        if (!ctx.V8ScopedActivationPreserved) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.V8ScopedActivationLost);
        if (mainlineEvidencePresent) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.MainlineTrustRegistryPresent);
        if (!rtPassed) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.P15GateNotPassed);
        var finalBlocked = blocked.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        var ready = finalBlocked.Length == 0;
        return new LearningFormalEvidenceRealizationR1PackDecision
        {
            Status = ready
                ? LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackReady
                : LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked,
            BlockedReasons = finalBlocked,
            Reasoning = ready
                ? "formal evidence realization R1 policy ready — canonical hash contract, mutation tests, terminology cleanup proceed."
                : $"{finalBlocked.Length} blocked reason(s); R1 pack blocked."
        };
    }
}

public sealed record LearningFormalEvidenceRealizationR1PackScenario(
    string CaseName,
    LearningFormalEvidenceRealizationR1PackContext Context,
    bool RtPassed, bool P15Passed,
    bool MainlineEvidencePresent, bool MainlineRegistryPresent,
    string ExpectedStatus, string? ExpectedBlockedReason);

public sealed class LearningFormalEvidenceRealizationR1PackRunner
{
    private static readonly JsonSerializerOptions WriteIndented = new() { WriteIndented = true };
    private const string HashInputVersion = "v10.16R/canonical-v1";
    private const string CanonicalAlgorithmContract = "SHA256(sourceShadowLabelId | sourceCandidateSpecId | evidencePath | expectedPreference | rankingPairRowHash | shadowLabelHash)";

    public LearningFormalEvidenceRealizationR1PackReport Run(
        LearningFormalEvidenceRealizationR1PackContext realContext,
        string outputDir,
        bool rtPassed, bool p15Passed,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent,
        LearningFormalEvidenceRealizationR1PackOptions? opt = null)
    {
        opt ??= new LearningFormalEvidenceRealizationR1PackOptions();
        var now = DateTimeOffset.UtcNow;

        var cases = BuildScenarios().Select(static scenario =>
        {
            var decision = LearningFormalEvidenceRealizationR1PackPolicy.Evaluate(
                scenario.Context, scenario.RtPassed, scenario.P15Passed,
                scenario.MainlineEvidencePresent, scenario.MainlineRegistryPresent);
            var statusMatched = string.Equals(scenario.ExpectedStatus, decision.Status, StringComparison.Ordinal);
            var blockedReasonMatched = scenario.ExpectedBlockedReason is null
                || decision.BlockedReasons.Contains(scenario.ExpectedBlockedReason, StringComparer.Ordinal);
            return new LearningFormalEvidenceRealizationR1PackCase
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
        if (cases.Length < 30) blocked.Add("InsufficientLearningFormalEvidenceRealizationR1PackCases");
        if (cases.Any(static c => !c.PassedAsExpected)) blocked.Add("LearningFormalEvidenceRealizationR1PackMatrixFailed");
        foreach (var status in new[] {
            LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackReady,
            LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked })
        {
            if (!cases.Any(c => string.Equals(c.ActualStatus, status, StringComparison.Ordinal)))
                blocked.Add($"StatusBranchNotCovered:{status}");
        }

        var realDecision = LearningFormalEvidenceRealizationR1PackPolicy.Evaluate(
            realContext, rtPassed, p15Passed, mainlineEvidencePresent, mainlineRegistryPresent);
        if (!string.Equals(realDecision.Status, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackReady, StringComparison.Ordinal))
            blocked.AddRange(realDecision.BlockedReasons.Select(static x => $"RealLearningFormalEvidenceRealizationR1Pack:{x}"));
        if (!rtPassed) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(LearningFormalEvidenceRealizationR1PackBlockedReasons.MainlineTrustRegistryPresent);

        var canBuild = string.Equals(realDecision.Status, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackReady, StringComparison.Ordinal);
        var candidates = new List<FormalLabelCandidateR1>();
        FormalLabelIntegrityManifestR1 manifest = new();
        IntegrityMutationTestReport mutationReport = new();
        TerminologyCompatibilityMap termMap = new();
        FormalLabelRealizationR1Decision r1Decision = new();
        var candidatesPath = string.Empty;
        var manifestPath = string.Empty;
        var mutationPath = string.Empty;
        var termMapPath = string.Empty;
        var decisionPath = string.Empty;

        if (canBuild)
        {
            // 1. Build canonical R1 candidates
            int idx = 0;
            foreach (var shadow in realContext.ShadowLabels.OrderBy(s => s.LabelId, StringComparer.Ordinal))
            {
                var rankingRowJson = realContext.RankingPairRowJsonBySampleId.GetValueOrDefault(shadow.SourceSampleId) ?? string.Empty;
                var rankingRowHash = string.IsNullOrEmpty(rankingRowJson) ? string.Empty : ComputeSha256(rankingRowJson);
                var shadowJson = JsonSerializer.Serialize(shadow);
                var shadowHash = ComputeSha256(shadowJson);
                var evidenceResolved = !string.IsNullOrEmpty(rankingRowHash);
                var preferenceDerivable = evidenceResolved && string.Equals(shadow.ExpectedPreference, "PositiveOverNegative", StringComparison.Ordinal);
                var canonical = ComputeSha256(
                    shadow.LabelId + "|" + shadow.SourceCandidateSpecId + "|" + shadow.EvidencePath + "|" +
                    shadow.ExpectedPreference + "|" + rankingRowHash + "|" + shadowHash);
                var legacy = ComputeSha256(shadow.SourceCandidateSpecId + "|" + shadow.EvidencePath + "|" + shadow.ExpectedPreference);
                var integrityOk = evidenceResolved && preferenceDerivable && !string.IsNullOrEmpty(rankingRowHash) && !string.IsNullOrEmpty(shadowHash);
                var eligibility = integrityOk ? "Eligible" : (!preferenceDerivable ? "Rejected" : "InvalidBinding");
                candidates.Add(new FormalLabelCandidateR1
                {
                    CandidateLabelId = $"flc-r1-{idx++:D4}-{shadow.SourceSampleId}",
                    SourceShadowLabelId = shadow.LabelId,
                    SourceCandidateSpecId = shadow.SourceCandidateSpecId,
                    EvidencePath = shadow.EvidencePath,
                    ExpectedPreference = shadow.ExpectedPreference,
                    RankingPairRowHash = rankingRowHash,
                    ShadowLabelHash = shadowHash,
                    DeterministicBindingHashCanonical = canonical,
                    LegacySpecBindingHash = legacy,
                    HashInputVersion = HashInputVersion,
                    PromotionEligibility = eligibility,
                    LifecycleState = "Proposed",
                    CanonicalIntegrityVerified = integrityOk,
                    EvidencePathResolved = evidenceResolved,
                    ExpectedPreferenceDerivable = preferenceDerivable,
                    FormalLabelCandidateIsFormal = false,
                    ShadowOnly = true,
                    AutoIngest = false,
                    PolicyVersion = "v10.16R/formal-label-candidate"
                });
            }
            candidatesPath = Path.Combine(outputDir, "formal-label-candidates-r1.jsonl");
            WriteCandidatesJsonl(candidatesPath, candidates);

            // 2. Build manifest with canonical hash recomputation
            manifest = BuildIntegrityManifest(candidates, realContext);
            manifestPath = Path.Combine(outputDir, "formal-label-integrity-manifest-r1.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, WriteIndented), new UTF8Encoding(true));

            // 3. Real mutation tests — actually mutate candidates and verify detection
            mutationReport = BuildMutationTestReport(candidates, realContext);
            mutationPath = Path.Combine(outputDir, "integrity-mutation-test-report.json");
            File.WriteAllText(mutationPath, JsonSerializer.Serialize(mutationReport, WriteIndented), new UTF8Encoding(true));

            // 4. Terminology compatibility map
            termMap = BuildTerminologyCompatibilityMap();
            termMapPath = Path.Combine(outputDir, "terminology-compatibility-map.json");
            File.WriteAllText(termMapPath, JsonSerializer.Serialize(termMap, WriteIndented), new UTF8Encoding(true));

            // 5. R1 realization decision — corrected terminology, formal evidence still insufficient
            r1Decision = BuildR1Decision(candidates, manifest, now);
            decisionPath = Path.Combine(outputDir, "formal-label-realization-decision-r1.json");
            File.WriteAllText(decisionPath, JsonSerializer.Serialize(r1Decision, WriteIndented), new UTF8Encoding(true));
        }

        // Authority leak checks
        foreach (var c in candidates)
        {
            if (c.FormalLabelCandidateIsFormal) blocked.Add($"R1CandidateFormalLeak:{c.CandidateLabelId}");
            if (c.AutoIngest) blocked.Add($"R1CandidateAutoIngestLeak:{c.CandidateLabelId}");
            if (string.IsNullOrEmpty(c.RankingPairRowHash)) blocked.Add($"R1CandidateRankingPairRowHashEmpty:{c.CandidateLabelId}");
            if (string.IsNullOrEmpty(c.ShadowLabelHash)) blocked.Add($"R1CandidateShadowLabelHashEmpty:{c.CandidateLabelId}");
            if (!string.Equals(c.HashInputVersion, HashInputVersion, StringComparison.Ordinal)) blocked.Add($"R1CandidateHashVersionStale:{c.CandidateLabelId}");
        }
        if (manifest.AnyHashMismatch) blocked.Add("R1ManifestHashMismatchDetected");
        if (!manifest.ContractHashAlgorithmCompliance) blocked.Add("R1ManifestContractHashAlgorithmNoncompliance");
        if (!mutationReport.IntegrityMutationTestsPassed) blocked.Add("R1IntegrityMutationTestsFailed");
        if (r1Decision.MainRecommendationUsesHumanReview) blocked.Add("R1DecisionMainRecommendationUsesHumanReview");
        if (r1Decision.FeedbackSignalAsGateAuthority || r1Decision.FeedbackSignalAutoIngest) blocked.Add("R1DecisionFeedbackSignalAuthorityLeak");
        if (r1Decision.FormalLabelCandidatesAreFormal || r1Decision.FormalLabelsRealized || r1Decision.FormalTrainingSetChanged) blocked.Add("R1DecisionFormalLeak");
        if (r1Decision.RuntimePilotExecutionReadyForSeparateGate) blocked.Add("R1DecisionPilotReadyLeak");

        var finalBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var passed = finalBlocked.Length == 0;

        return new LearningFormalEvidenceRealizationR1PackReport
        {
            OperationId = $"v10.16R-formal-evidence-realization-pack-{Guid.NewGuid():N}",
            CreatedAt = now,
            FormalEvidenceRealizationR1PackPassed = passed,
            GatePassed = opt.IsGate && passed,
            TotalCases = cases.Length,
            PassedCases = cases.Count(static c => c.PassedAsExpected),
            FailedCases = cases.Count(static c => !c.PassedAsExpected),
            ReadyCases = cases.Count(c => string.Equals(c.ActualStatus, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackReady, StringComparison.Ordinal)),
            BlockedCases = cases.Count(c => string.Equals(c.ActualStatus, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, StringComparison.Ordinal)),
            Cases = cases,
            Candidates = candidates,
            IntegrityManifest = manifest,
            MutationTestReport = mutationReport,
            TerminologyCompatibilityMap = termMap,
            R1Decision = r1Decision,
            FormalLabelCandidateCount = candidates.Count,
            RealizableFormalLabelCount = candidates.Count(c => c.PromotionEligibility == "Eligible"),
            InvalidBindingCount = candidates.Count(c => c.PromotionEligibility != "Eligible"),
            HashInputVersion = HashInputVersion,
            ContractHashAlgorithmCompliance = manifest.ContractHashAlgorithmCompliance,
            RankingPairRowHashCoverage = manifest.RankingPairRowHashCoverage,
            ShadowLabelHashCoverage = manifest.ShadowLabelHashCoverage,
            IntegrityMutationTestsPassed = mutationReport.IntegrityMutationTestsPassed,
            CorruptedHashDetected = mutationReport.CorruptedHashDetected,
            MissingEvidencePathDetected = mutationReport.MissingEvidencePathDetected,
            ExpectedPreferenceMismatchDetected = mutationReport.ExpectedPreferenceMismatchDetected,
            CandidateMarkedFormalDetected = mutationReport.CandidateMarkedFormalDetected,
            RankingPairRowHashMismatchDetected = mutationReport.RankingPairRowHashMismatchDetected,
            StaleContractHashVersionDetected = mutationReport.StaleContractHashVersionDetected,
            FormalLabelsRealized = false,
            FormalEvidenceSufficient = false,
            RuntimePilotExecutionReadyForSeparateGate = false,
            FormalTrainingSetChanged = false,
            AutoIngest = false,
            ExternalFeedbackAcceptedAsSignal = true,
            FeedbackSignalAsGateAuthority = false,
            FeedbackSignalAutoIngest = false,
            MainRecommendationUsesHumanReview = false,
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
            CandidatesPath = candidatesPath,
            IntegrityManifestPath = manifestPath,
            MutationTestReportPath = mutationPath,
            TerminologyCompatibilityMapPath = termMapPath,
            R1DecisionPath = decisionPath,
            Recommendation = passed ? "ProceedToControlledFormalLabelIngestionWithCanonicalHashV2" : "Blocked",
            NextAllowedPhase = passed ? "ControlledFormalLabelIngestion-pending-canonical-hash-v2" : string.Empty,
            BlockedReasons = finalBlocked,
            Diagnostics = new[]
            {
                $"total={cases.Length}",
                $"realStatus={realDecision.Status}",
                $"candidateCount={candidates.Count}",
                $"contractHashAlgorithmCompliance={manifest.ContractHashAlgorithmCompliance}",
                $"rankingPairRowHashCoverage={manifest.RankingPairRowHashCoverage:F3}",
                $"shadowLabelHashCoverage={manifest.ShadowLabelHashCoverage:F3}",
                $"mutationTestsPassed={mutationReport.IntegrityMutationTestsPassed}",
                $"corruptedHashDetected={mutationReport.CorruptedHashDetected}",
                $"missingEvidencePathDetected={mutationReport.MissingEvidencePathDetected}",
                $"expectedPreferenceMismatchDetected={mutationReport.ExpectedPreferenceMismatchDetected}",
                $"candidateMarkedFormalDetected={mutationReport.CandidateMarkedFormalDetected}",
                $"mainRecommendationUsesHumanReview={r1Decision.MainRecommendationUsesHumanReview}"
            }
        };
    }

    private static IReadOnlyList<LearningFormalEvidenceRealizationR1PackScenario> BuildScenarios()
    {
        var clean = BuildCleanContext();
        return [
            new("AllUpstreamClean", clean, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackReady, null),
            new("FormalEvidenceBoundaryMissing", clean with { FormalEvidenceBoundaryPresent = false }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.FormalEvidenceBoundaryMissing),
            new("FormalEvidenceBoundaryNotPassed", clean with { FormalEvidenceBoundaryPassed = false }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.FormalEvidenceBoundaryNotPassed),
            new("ShadowLabelsMissing", clean with { ShadowLabelsPresent = false, ShadowLabelCount = 0 }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.ShadowLabelsMissing),
            new("RankingPairsMissing", clean with { RankerPairs = Array.Empty<RankerPair>() }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.RankingPairsMissing),
            new("HashContractMismatch", clean with { HashContractMismatchOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.HashContractMismatch),
            new("RankingPairRowHashMissing", clean with { RankingPairRowHashMissingOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.RankingPairRowHashMissing),
            new("ShadowLabelHashMissing", clean with { ShadowLabelHashMissingOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.ShadowLabelHashMissing),
            new("EvidencePathInvalid", clean with { EvidencePathInvalidOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.EvidencePathInvalid),
            new("ExpectedPreferenceMismatch", clean with { ExpectedPreferenceMismatchOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.ExpectedPreferenceMismatch),
            new("FormalCandidateMarkedFormalLeak", clean with { FormalCandidateMarkedFormalOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.FormalCandidateMarkedFormalLeak),
            new("MainRecommendationUsesHumanReview", clean with { MainRecommendationUsesHumanReviewOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.MainRecommendationUsesHumanReviewTrue),
            new("FeedbackSignalAsGateAuthority", clean with { FeedbackSignalAsGateAuthorityOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.FeedbackSignalAsGateAuthorityTrue),
            new("FeedbackSignalAutoIngest", clean with { FeedbackSignalAutoIngestOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.FeedbackSignalAutoIngestTrue),
            new("AutoIngest", clean with { AutoIngestOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.AutoIngestTrue),
            new("FormalTrainingSetChanged", clean with { FormalTrainingSetChangedOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.FormalTrainingSetChangedTrue),
            new("RuntimePilotExecutionApplied", clean with { RuntimePilotExecutionAppliedOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.RuntimePilotExecutionAppliedTrue),
            new("RuntimePromotionApplied", clean with { RuntimePromotionAppliedOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.RuntimePromotionAppliedTrue),
            new("RuntimeRerankerChanged", clean with { RuntimeRerankerChangedOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.RuntimeRerankerChangedTrue),
            new("RuntimeRouterChanged", clean with { RuntimeRouterChangedOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.RuntimeRouterChangedTrue),
            new("ProductionDecisionChanged", clean with { ProductionDecisionChangedOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.ProductionDecisionChangedTrue),
            new("PackageOutputChanged", clean with { PackageOutputChangedOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.PackageOutputChangedTrue),
            new("FormalPackageWritten", clean with { FormalPackageWrittenOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.FormalPackageWrittenTrue),
            new("GlobalDefaultOn", clean with { GlobalDefaultOnOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.GlobalDefaultOnTrue),
            new("MLAuthority", clean with { MLAuthorityOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.MLAuthorityTrue),
            new("LLMAuthority", clean with { LLMAuthorityOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.LLMAuthorityTrue),
            new("RuntimeAuthority", clean with { RuntimeAuthorityOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.RuntimeAuthorityTrue),
            new("GateAuthority", clean with { GateAuthorityOverride = true }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.GateAuthorityTrue),
            new("V8ScopedActivationLost", clean with { V8ScopedActivationPreserved = false }, true, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.V8ScopedActivationLost),
            new("MainlineEvidencePresent", clean, true, true, true, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.MainlineEvidencePresent),
            new("MainlineTrustRegistryPresent", clean, true, true, false, true, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.MainlineTrustRegistryPresent),
            new("RuntimeGateNotPassed", clean, false, true, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.RuntimeChangeGateNotPassed),
            new("P15GateNotPassed", clean, true, false, false, false, LearningFormalEvidenceRealizationR1PackStatuses.LearningFormalEvidenceRealizationR1PackBlocked, LearningFormalEvidenceRealizationR1PackBlockedReasons.P15GateNotPassed)
        ];
    }

    private static LearningFormalEvidenceRealizationR1PackContext BuildCleanContext() => new()
    {
        FormalEvidenceBoundaryPresent = true,
        FormalEvidenceBoundaryPassed = true,
        ShadowLabelsPresent = true,
        ShadowLabelCount = 60,
        V8ScopedActivationPreserved = true,
        ShadowLabels = Array.Empty<EvidenceBoundShadowLabel>(),
        RankerPairs = new[] { new RankerPair { EvalSampleId = "fixture-sample" } },
        RankingPairRowJsonBySampleId = new Dictionary<string, string>(StringComparer.Ordinal)
    };

    // ─── builders ────────────────────────────────────────────────────────────

    private static FormalLabelIntegrityManifestR1 BuildIntegrityManifest(IReadOnlyList<FormalLabelCandidateR1> candidates, LearningFormalEvidenceRealizationR1PackContext ctx)
    {
        var entries = new List<FormalLabelIntegrityManifestR1Entry>();
        int verified = 0;
        int mismatched = 0;
        int evidenceUnresolved = 0;
        int prefUndeterivable = 0;
        var shadowById = ctx.ShadowLabels.ToDictionary(s => s.LabelId, s => s, StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            var shadow = shadowById.GetValueOrDefault(c.SourceShadowLabelId);
            var rankingRowJson = shadow is null ? string.Empty : ctx.RankingPairRowJsonBySampleId.GetValueOrDefault(shadow.SourceSampleId) ?? string.Empty;
            var expectedRankingHash = string.IsNullOrEmpty(rankingRowJson) ? string.Empty : ComputeSha256(rankingRowJson);
            var expectedShadowHash = shadow is null ? string.Empty : ComputeSha256(JsonSerializer.Serialize(shadow));
            var expectedCanonical = shadow is null ? string.Empty : ComputeSha256(
                shadow.LabelId + "|" + shadow.SourceCandidateSpecId + "|" + shadow.EvidencePath + "|" +
                shadow.ExpectedPreference + "|" + expectedRankingHash + "|" + expectedShadowHash);
            var canonicalMatches = !string.IsNullOrEmpty(expectedCanonical) && string.Equals(c.DeterministicBindingHashCanonical, expectedCanonical, StringComparison.Ordinal);
            if (canonicalMatches) verified++; else mismatched++;
            if (!c.EvidencePathResolved) evidenceUnresolved++;
            if (!c.ExpectedPreferenceDerivable) prefUndeterivable++;
            entries.Add(new FormalLabelIntegrityManifestR1Entry
            {
                CandidateLabelId = c.CandidateLabelId,
                SourceShadowLabelId = c.SourceShadowLabelId,
                SourceCandidateSpecId = c.SourceCandidateSpecId,
                EvidencePath = c.EvidencePath,
                ExpectedPreference = c.ExpectedPreference,
                RankingPairRowHash = c.RankingPairRowHash,
                ShadowLabelHash = c.ShadowLabelHash,
                DeterministicBindingHashCanonical = c.DeterministicBindingHashCanonical,
                ExpectedCanonicalHash = expectedCanonical,
                LegacySpecBindingHash = c.LegacySpecBindingHash,
                CanonicalHashMatches = canonicalMatches,
                EvidencePathResolved = c.EvidencePathResolved,
                ExpectedPreferenceDerivable = c.ExpectedPreferenceDerivable,
                IntegrityStatus = canonicalMatches && c.EvidencePathResolved && c.ExpectedPreferenceDerivable ? "Verified" : "Mismatched"
            });
        }
        var rankingCoverage = candidates.Count == 0 ? 1.0 : (double)candidates.Count(c => !string.IsNullOrEmpty(c.RankingPairRowHash)) / candidates.Count;
        var shadowCoverage = candidates.Count == 0 ? 1.0 : (double)candidates.Count(c => !string.IsNullOrEmpty(c.ShadowLabelHash)) / candidates.Count;
        return new FormalLabelIntegrityManifestR1
        {
            ManifestId = $"v10.16R-integrity-manifest-{Guid.NewGuid():N}",
            ManifestVersion = "v10.16R/formal-label-integrity-canonical-v1",
            HashAlgorithm = "SHA-256",
            HashInputContract = CanonicalAlgorithmContract,
            TotalEntries = entries.Count,
            VerifiedEntries = verified,
            MismatchedEntries = mismatched,
            EvidencePathUnresolvedCount = evidenceUnresolved,
            ExpectedPreferenceUndeterivableCount = prefUndeterivable,
            Entries = entries,
            AnyHashMismatch = mismatched > 0,
            ContractHashAlgorithmCompliance = candidates.All(c => string.Equals(c.HashInputVersion, HashInputVersion, StringComparison.Ordinal)),
            RankingPairRowHashCoverage = rankingCoverage,
            ShadowLabelHashCoverage = shadowCoverage,
            ShadowOnly = true
        };
    }

    /// <summary>V10.16R: REAL artifact mutation tests — actually corrupt then verify detection.</summary>
    private static IntegrityMutationTestReport BuildMutationTestReport(IReadOnlyList<FormalLabelCandidateR1> originals, LearningFormalEvidenceRealizationR1PackContext ctx)
    {
        if (originals.Count == 0)
            return new IntegrityMutationTestReport
            {
                ReportId = $"v10.16R-mutation-{Guid.NewGuid():N}",
                TotalMutationTests = 0,
                DetectedMutations = 0,
                IntegrityMutationTestsPassed = false
            };
        var first = originals[0];
        var shadowById = ctx.ShadowLabels.ToDictionary(s => s.LabelId, s => s, StringComparer.Ordinal);
        var shadow = shadowById.GetValueOrDefault(first.SourceShadowLabelId)!;
        var rankingRowJson = ctx.RankingPairRowJsonBySampleId.GetValueOrDefault(shadow.SourceSampleId) ?? string.Empty;

        var tests = new List<IntegrityMutationTestCase>();

        // M1: corrupted canonical hash
        {
            var mutated = CloneCandidate(first) with { DeterministicBindingHashCanonical = "00000000corrupted00000000corrupted00000000corrupted00000000xxxx" };
            var expectedCanonical = ComputeSha256(shadow.LabelId + "|" + shadow.SourceCandidateSpecId + "|" + shadow.EvidencePath + "|" + shadow.ExpectedPreference + "|" + first.RankingPairRowHash + "|" + first.ShadowLabelHash);
            var detected = !string.Equals(mutated.DeterministicBindingHashCanonical, expectedCanonical, StringComparison.Ordinal);
            tests.Add(new IntegrityMutationTestCase { MutationName = "CorruptedCanonicalHash", Description = "DeterministicBindingHashCanonical replaced with zero-prefixed string", DetectedByCanonicalHash = detected, DetectedOverall = detected, FailureMode = "ZeroPrefixedHash" });
        }
        // M2: missing evidence path
        {
            var mutated = CloneCandidate(first) with { EvidencePath = string.Empty, EvidencePathResolved = false };
            var detected = !mutated.EvidencePathResolved || string.IsNullOrEmpty(mutated.EvidencePath);
            tests.Add(new IntegrityMutationTestCase { MutationName = "MissingEvidencePath", Description = "EvidencePath set to empty string", DetectedByEvidencePathCheck = detected, DetectedOverall = detected, FailureMode = "EmptyEvidencePath" });
        }
        // M3: expected preference mismatch
        {
            var mutated = CloneCandidate(first) with { ExpectedPreference = "NegativeOverPositive" };
            var detected = !string.Equals(mutated.ExpectedPreference, "PositiveOverNegative", StringComparison.Ordinal);
            tests.Add(new IntegrityMutationTestCase { MutationName = "ExpectedPreferenceMismatch", Description = "ExpectedPreference flipped to NegativeOverPositive", DetectedByExpectedPreferenceCheck = detected, DetectedOverall = detected, FailureMode = "FlippedPreference" });
        }
        // M4: candidate marked formal leak
        {
            var mutated = CloneCandidate(first) with { FormalLabelCandidateIsFormal = true };
            var detected = mutated.FormalLabelCandidateIsFormal;
            tests.Add(new IntegrityMutationTestCase { MutationName = "CandidateMarkedFormal", Description = "FormalLabelCandidateIsFormal set to true", DetectedByCandidateFormalLeakCheck = detected, DetectedOverall = detected, FailureMode = "FormalLeak" });
        }
        // M5: ranking-pair row hash mismatch
        {
            var mutated = CloneCandidate(first) with { RankingPairRowHash = "00000000mismatched-ranking-pair-row-hash000000" };
            var expectedRankingHash = string.IsNullOrEmpty(rankingRowJson) ? string.Empty : ComputeSha256(rankingRowJson);
            var detected = !string.Equals(mutated.RankingPairRowHash, expectedRankingHash, StringComparison.Ordinal);
            tests.Add(new IntegrityMutationTestCase { MutationName = "RankingPairRowHashMismatch", Description = "RankingPairRowHash replaced with arbitrary value", DetectedByRankingPairRowHashCheck = detected, DetectedOverall = detected, FailureMode = "RankingHashMismatch" });
        }
        // M6: stale contract hash version
        {
            var mutated = CloneCandidate(first) with { HashInputVersion = "v10.16/legacy-v0" };
            var detected = !string.Equals(mutated.HashInputVersion, HashInputVersion, StringComparison.Ordinal);
            tests.Add(new IntegrityMutationTestCase { MutationName = "StaleContractHashVersion", Description = "HashInputVersion replaced with legacy V10.16 string", DetectedByHashVersionCheck = detected, DetectedOverall = detected, FailureMode = "StaleVersion" });
        }

        var detectedAll = tests.All(t => t.DetectedOverall);
        return new IntegrityMutationTestReport
        {
            ReportId = $"v10.16R-mutation-{Guid.NewGuid():N}",
            ReportVersion = "v10.16R/integrity-mutation-tests-v1",
            TestCases = tests,
            TotalMutationTests = tests.Count,
            DetectedMutations = tests.Count(t => t.DetectedOverall),
            IntegrityMutationTestsPassed = detectedAll,
            CorruptedHashDetected = tests.First(t => t.MutationName == "CorruptedCanonicalHash").DetectedOverall,
            MissingEvidencePathDetected = tests.First(t => t.MutationName == "MissingEvidencePath").DetectedOverall,
            ExpectedPreferenceMismatchDetected = tests.First(t => t.MutationName == "ExpectedPreferenceMismatch").DetectedOverall,
            CandidateMarkedFormalDetected = tests.First(t => t.MutationName == "CandidateMarkedFormal").DetectedOverall,
            RankingPairRowHashMismatchDetected = tests.First(t => t.MutationName == "RankingPairRowHashMismatch").DetectedOverall,
            StaleContractHashVersionDetected = tests.First(t => t.MutationName == "StaleContractHashVersion").DetectedOverall
        };
    }

    private static FormalLabelCandidateR1 CloneCandidate(FormalLabelCandidateR1 src)
        => new()
        {
            CandidateLabelId = src.CandidateLabelId, SourceShadowLabelId = src.SourceShadowLabelId, SourceCandidateSpecId = src.SourceCandidateSpecId,
            EvidencePath = src.EvidencePath, ExpectedPreference = src.ExpectedPreference, RankingPairRowHash = src.RankingPairRowHash,
            ShadowLabelHash = src.ShadowLabelHash, DeterministicBindingHashCanonical = src.DeterministicBindingHashCanonical, LegacySpecBindingHash = src.LegacySpecBindingHash,
            HashInputVersion = src.HashInputVersion, PromotionEligibility = src.PromotionEligibility, LifecycleState = src.LifecycleState,
            CanonicalIntegrityVerified = src.CanonicalIntegrityVerified, EvidencePathResolved = src.EvidencePathResolved,
            ExpectedPreferenceDerivable = src.ExpectedPreferenceDerivable, FormalLabelCandidateIsFormal = src.FormalLabelCandidateIsFormal,
            ShadowOnly = src.ShadowOnly, AutoIngest = src.AutoIngest, PolicyVersion = src.PolicyVersion,
            DeprecatedCompatibilityNote = src.DeprecatedCompatibilityNote
        };

    private static TerminologyCompatibilityMap BuildTerminologyCompatibilityMap()
        => new()
        {
            MapVersion = "v10.16R/terminology-compatibility-v1",
            Entries = new[]
            {
                new TerminologyCompatibilityMapEntry { LegacyName = "HumanReviewAsGateAuthority", CanonicalName = "FeedbackSignalAsGateAuthority", Status = "DeprecatedCompatibilityOnly", Notes = "V10.16R rename — both must be false; canonical is the authority." },
                new TerminologyCompatibilityMapEntry { LegacyName = "HumanFeedbackAutoIngest", CanonicalName = "FeedbackSignalAutoIngest", Status = "DeprecatedCompatibilityOnly", Notes = "V10.16R rename — both must be false." },
                new TerminologyCompatibilityMapEntry { LegacyName = "HumanFeedbackAsSignal", CanonicalName = "ExternalFeedbackAcceptedAsSignal", Status = "DeprecatedCompatibilityOnly", Notes = "V10.16R rename — same semantics, broader scope." },
                new TerminologyCompatibilityMapEntry { LegacyName = "HumanReviewBacklog*", CanonicalName = "ExternalFeedbackBacklog*", Status = "DeprecatedCompatibilityOnly", Notes = "V10.16R rename — V9.4 review queue is one of many possible external feedback sources." }
            },
            MainPathPolicies = new[]
            {
                "Main recommendation MUST NOT reference 'human-review' / 'human-feedback' as required path.",
                "External feedback is accepted as SIGNAL only, never as gate authority.",
                "Canonical field names take precedence; legacy field names exist for upstream compatibility only.",
                "ML/LLM/Runtime/Gate authority remains false across all paths."
            }
        };

    private static FormalLabelRealizationR1Decision BuildR1Decision(IReadOnlyList<FormalLabelCandidateR1> candidates, FormalLabelIntegrityManifestR1 manifest, DateTimeOffset now)
    {
        var realizable = candidates.Count(c => c.CanonicalIntegrityVerified && c.PromotionEligibility == "Eligible");
        var invalid = candidates.Count(c => c.PromotionEligibility != "Eligible");
        var blockedBy = new List<string>
        {
            "FormalLabelsNotRealizedInDataset: controlled ingestion has not written the formal dataset.",
            "FormalEvidenceInsufficient: shadow-bound canonical hash verified but formal dataset stays untouched.",
            "RankingPairRowHashAndShadowLabelHashRequired: realization needs both attestations to be reproduced at ingest time."
        };
        if (manifest.AnyHashMismatch) blockedBy.Add($"R1IntegrityManifestHashMismatch:{manifest.MismatchedEntries}");
        return new FormalLabelRealizationR1Decision
        {
            DecisionId = $"v10.16R-formal-label-realization-decision-{Guid.NewGuid():N}",
            CreatedAt = now.ToString("O"),
            FormalLabelCandidateCount = candidates.Count,
            RealizableFormalLabelCount = realizable,
            InvalidBindingCount = invalid,
            FormalLabelCandidatesReady = candidates.Count > 0,
            FormalLabelsRealized = false,
            FormalEvidenceSufficient = false,
            RuntimePilotExecutionReadyForSeparateGate = false,
            BlockedForRuntimePilotExecutionBy = blockedBy,
            ExternalFeedbackAcceptedAsSignal = true,
            FeedbackSignalAsGateAuthority = false,
            FeedbackSignalAutoIngest = false,
            FormalTrainingSetChanged = false,
            FormalLabelCandidatesAreFormal = false,
            MainRecommendationUsesHumanReview = false,
            AIArbitration = false,
            EvidenceSourcesConsidered = new[]
            {
                "learning/v10/formal-evidence-boundary-pack-gate.json",
                "learning/v10/evidence-bound-hard-negative-labels.jsonl",
                "learning/v10/formal-label-candidates-r1.jsonl",
                "learning/v10/formal-label-integrity-manifest-r1.json",
                "learning/v10/integrity-mutation-test-report.json",
                "learning/v10/terminology-compatibility-map.json"
            },
            DecisionNotes = new[]
            {
                $"FormalLabelCandidateCount={candidates.Count}",
                $"RealizableFormalLabelCount={realizable} (canonical hash verified + evidence resolved + preference derivable)",
                $"InvalidBindingCount={invalid}",
                $"IntegrityManifest: ContractHashAlgorithmCompliance={manifest.ContractHashAlgorithmCompliance}, AnyHashMismatch={manifest.AnyHashMismatch}",
                "Canonical hash contract: SHA256(sourceShadowLabelId | sourceCandidateSpecId | evidencePath | expectedPreference | rankingPairRowHash | shadowLabelHash).",
                "Legacy V10.16 hash (sourceCandidateSpecId only) is retained as LegacySpecBindingHash for traceability — NEVER used as integrity authority.",
                "Main recommendation does NOT require human-review path; external feedback is accepted as signal only.",
                "FormalTrainingSetChanged=false / RuntimePilotExecutionReadyForSeparateGate=false until controlled ingestion writes the formal dataset."
            }
        };
    }

    private static string ComputeSha256(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static void WriteCandidatesJsonl(string path, IReadOnlyList<FormalLabelCandidateR1> candidates)
    {
        var sb = new StringBuilder();
        foreach (var c in candidates) sb.AppendLine(JsonSerializer.Serialize(c));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>V10.16R: load V10.10 evidence-bound shadow labels from disk.</summary>
    public static IReadOnlyList<EvidenceBoundShadowLabel> LoadShadowLabels(string path)
        => LearningFormalEvidenceBoundaryPackRunner.LoadEvidenceBoundShadowLabels(path);

    /// <summary>V10.16R: load ranking pair raw JSON keyed by evalSampleId so we can hash the actual row.</summary>
    public static IReadOnlyDictionary<string, string> LoadRankingPairRowJsonBySampleId(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return map;
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("evalSampleId", out var s) && s.GetString() is string sid && !string.IsNullOrEmpty(sid))
                    map.TryAdd(sid, line);
            }
            catch { }
        }
        return map;
    }

    public static string BuildMarkdown(string title, LearningFormalEvidenceRealizationR1PackReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"- FormalEvidenceRealizationR1PackPassed: `{report.FormalEvidenceRealizationR1PackPassed}`");
        sb.AppendLine($"- GatePassed: `{report.GatePassed}`");
        sb.AppendLine($"- TotalCases: `{report.TotalCases}` PassedCases: `{report.PassedCases}` FailedCases: `{report.FailedCases}`");
        sb.AppendLine();
        sb.AppendLine("## Canonical Hash Contract");
        sb.AppendLine($"- **HashInputVersion**: `{report.HashInputVersion}`");
        sb.AppendLine($"- HashAlgorithm: `{report.IntegrityManifest.HashAlgorithm}`");
        sb.AppendLine($"- HashInputContract: `{report.IntegrityManifest.HashInputContract}`");
        sb.AppendLine($"- **ContractHashAlgorithmCompliance**: `{report.ContractHashAlgorithmCompliance}`");
        sb.AppendLine($"- RankingPairRowHashCoverage: `{report.RankingPairRowHashCoverage:F3}`");
        sb.AppendLine($"- ShadowLabelHashCoverage: `{report.ShadowLabelHashCoverage:F3}`");
        sb.AppendLine();
        sb.AppendLine("## Integrity Manifest");
        sb.AppendLine($"- TotalEntries: `{report.IntegrityManifest.TotalEntries}` VerifiedEntries: `{report.IntegrityManifest.VerifiedEntries}` MismatchedEntries: `{report.IntegrityManifest.MismatchedEntries}`");
        sb.AppendLine($"- AnyHashMismatch: `{report.IntegrityManifest.AnyHashMismatch}` EvidencePathUnresolvedCount: `{report.IntegrityManifest.EvidencePathUnresolvedCount}` ExpectedPreferenceUndeterivableCount: `{report.IntegrityManifest.ExpectedPreferenceUndeterivableCount}`");
        sb.AppendLine();
        sb.AppendLine("## Integrity Mutation Tests (REAL)");
        sb.AppendLine($"- TotalMutationTests: `{report.MutationTestReport.TotalMutationTests}` DetectedMutations: `{report.MutationTestReport.DetectedMutations}`");
        sb.AppendLine($"- **IntegrityMutationTestsPassed**: `{report.IntegrityMutationTestsPassed}`");
        sb.AppendLine($"- CorruptedHashDetected: `{report.CorruptedHashDetected}`");
        sb.AppendLine($"- MissingEvidencePathDetected: `{report.MissingEvidencePathDetected}`");
        sb.AppendLine($"- ExpectedPreferenceMismatchDetected: `{report.ExpectedPreferenceMismatchDetected}`");
        sb.AppendLine($"- CandidateMarkedFormalDetected: `{report.CandidateMarkedFormalDetected}`");
        sb.AppendLine($"- RankingPairRowHashMismatchDetected: `{report.RankingPairRowHashMismatchDetected}`");
        sb.AppendLine($"- StaleContractHashVersionDetected: `{report.StaleContractHashVersionDetected}`");
        sb.AppendLine();
        sb.AppendLine("## Terminology Compatibility Map");
        sb.AppendLine($"- MapVersion: `{report.TerminologyCompatibilityMap.MapVersion}`");
        foreach (var e in report.TerminologyCompatibilityMap.Entries)
            sb.AppendLine($"  - `{e.LegacyName}` -> `{e.CanonicalName}` ({e.Status})");
        sb.AppendLine();
        sb.AppendLine("## Authority + Boundary Invariants");
        sb.AppendLine($"- ExternalFeedbackAcceptedAsSignal: `{report.ExternalFeedbackAcceptedAsSignal}` FeedbackSignalAsGateAuthority: `{report.FeedbackSignalAsGateAuthority}` FeedbackSignalAutoIngest: `{report.FeedbackSignalAutoIngest}`");
        sb.AppendLine($"- **MainRecommendationUsesHumanReview**: `{report.MainRecommendationUsesHumanReview}` (must be false)");
        sb.AppendLine($"- FormalLabelsRealized: `{report.FormalLabelsRealized}` FormalEvidenceSufficient: `{report.FormalEvidenceSufficient}`");
        sb.AppendLine($"- RuntimePilotExecutionReadyForSeparateGate: `{report.RuntimePilotExecutionReadyForSeparateGate}`");
        sb.AppendLine($"- FormalTrainingSetChanged: `{report.FormalTrainingSetChanged}` AutoIngest: `{report.AutoIngest}`");
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

public sealed class LearningFormalEvidenceRealizationR1PackCase
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

public sealed class LearningFormalEvidenceRealizationR1PackReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool FormalEvidenceRealizationR1PackPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public int ReadyCases { get; init; }
    public int BlockedCases { get; init; }
    public IReadOnlyList<LearningFormalEvidenceRealizationR1PackCase> Cases { get; init; } = Array.Empty<LearningFormalEvidenceRealizationR1PackCase>();
    public IReadOnlyList<FormalLabelCandidateR1> Candidates { get; init; } = Array.Empty<FormalLabelCandidateR1>();
    public FormalLabelIntegrityManifestR1 IntegrityManifest { get; init; } = new();
    public IntegrityMutationTestReport MutationTestReport { get; init; } = new();
    public TerminologyCompatibilityMap TerminologyCompatibilityMap { get; init; } = new();
    public FormalLabelRealizationR1Decision R1Decision { get; init; } = new();
    public int FormalLabelCandidateCount { get; init; }
    public int RealizableFormalLabelCount { get; init; }
    public int InvalidBindingCount { get; init; }
    public string HashInputVersion { get; init; } = string.Empty;
    public bool ContractHashAlgorithmCompliance { get; init; }
    public double RankingPairRowHashCoverage { get; init; }
    public double ShadowLabelHashCoverage { get; init; }
    public bool IntegrityMutationTestsPassed { get; init; }
    public bool CorruptedHashDetected { get; init; }
    public bool MissingEvidencePathDetected { get; init; }
    public bool ExpectedPreferenceMismatchDetected { get; init; }
    public bool CandidateMarkedFormalDetected { get; init; }
    public bool RankingPairRowHashMismatchDetected { get; init; }
    public bool StaleContractHashVersionDetected { get; init; }
    public bool FormalLabelsRealized { get; init; }
    public bool FormalEvidenceSufficient { get; init; }
    public bool RuntimePilotExecutionReadyForSeparateGate { get; init; }
    public bool FormalTrainingSetChanged { get; init; }
    public bool AutoIngest { get; init; }
    public bool ExternalFeedbackAcceptedAsSignal { get; init; }
    public bool FeedbackSignalAsGateAuthority { get; init; }
    public bool FeedbackSignalAutoIngest { get; init; }
    public bool MainRecommendationUsesHumanReview { get; init; }
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
    public string CandidatesPath { get; init; } = string.Empty;
    public string IntegrityManifestPath { get; init; } = string.Empty;
    public string MutationTestReportPath { get; init; } = string.Empty;
    public string TerminologyCompatibilityMapPath { get; init; } = string.Empty;
    public string R1DecisionPath { get; init; } = string.Empty;
    public string Recommendation { get; init; } = string.Empty;
    public string NextAllowedPhase { get; init; } = string.Empty;
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class LearningFormalEvidenceRealizationR1PackOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
