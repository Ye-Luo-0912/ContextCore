using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

public static class LearningLayerBootstrapStatuses
{
    public const string LearningLayerBootstrapReady = nameof(LearningLayerBootstrapReady);
    public const string LearningLayerBootstrapBlocked = nameof(LearningLayerBootstrapBlocked);
}

public static class LearningLayerBootstrapBlockedReasons
{
    public const string DatasetQualityReportMissing = nameof(DatasetQualityReportMissing);
    public const string RankingPairsMissing = nameof(RankingPairsMissing);
    public const string RouterIntentExamplesMissing = nameof(RouterIntentExamplesMissing);
    public const string AllFeaturesMissing = nameof(AllFeaturesMissing);
    public const string RankerAblationReportMissing = nameof(RankerAblationReportMissing);
    public const string ShadowOnlyFalse = nameof(ShadowOnlyFalse);
    public const string RuntimeAuthorityTrue = nameof(RuntimeAuthorityTrue);
    public const string GateAuthorityTrue = nameof(GateAuthorityTrue);
    public const string PackageOutputChangedTrue = nameof(PackageOutputChangedTrue);
    public const string FormalPackageWrittenTrue = nameof(FormalPackageWrittenTrue);
    public const string GlobalDefaultOnTrue = nameof(GlobalDefaultOnTrue);
    public const string V8ScopedActivationCloseoutMissing = nameof(V8ScopedActivationCloseoutMissing);
    public const string V8ScopedActivationCloseoutNotPassed = nameof(V8ScopedActivationCloseoutNotPassed);
    public const string V8ScopedActivationLost = nameof(V8ScopedActivationLost);
    public const string RuntimeChangeGateNotPassed = nameof(RuntimeChangeGateNotPassed);
    public const string P15GateNotPassed = nameof(P15GateNotPassed);
    public const string MainlineEvidencePresent = nameof(MainlineEvidencePresent);
    public const string MainlineTrustRegistryPresent = nameof(MainlineTrustRegistryPresent);
}

/// <summary>V9.0: inventory of what learning data we have on disk + known gaps. Pure description, no authority.</summary>
public sealed record LearningLayerDatasetInventory
{
    public int RankingPairCount { get; init; }
    public int RouterIntentExampleCount { get; init; }
    public int PolicyFeedbackFeatureCount { get; init; }
    public int HardNegativeCount { get; init; }
    public bool RankingPairsFilePresent { get; init; }
    public bool RouterIntentExamplesFilePresent { get; init; }
    public bool PolicyFeedbackFeaturesFilePresent { get; init; }
    public bool HardNegativesFilePresent { get; init; }
    public bool DatasetQualityReportPresent { get; init; }
    public IReadOnlyList<string> UsableTaskFamilies { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> NotReadyTaskFamilies { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> KnownRisks { get; init; } = Array.Empty<string>();
}

/// <summary>V9.0: contract describing every feature a shadow learner may consume. All fields are ShadowOnly + non-authoritative.</summary>
public sealed class LearningLayerFeatureContractEntry
{
    public string TaskFamily { get; init; } = string.Empty;
    public string FeatureName { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public bool Nullable { get; init; }
    public bool ShadowOnly { get; init; } = true;
    public bool RuntimeAuthority { get; init; }
    public bool GateAuthority { get; init; }
}

public sealed class LearningLayerFeatureContract
{
    public IReadOnlyList<LearningLayerFeatureContractEntry> Entries { get; init; } = Array.Empty<LearningLayerFeatureContractEntry>();
    public IReadOnlyList<string> TaskFamilies { get; init; } = Array.Empty<string>();
    public bool AllEntriesShadowOnly { get; init; } = true;
    public bool AnyEntryRuntimeAuthority { get; init; }
    public bool AnyEntryGateAuthority { get; init; }
}

public sealed class LearningLayerBaselinePlanEntry
{
    public string BaselineName { get; init; } = string.Empty;
    public string TaskFamily { get; init; } = string.Empty;
    public string Stage { get; init; } = string.Empty;
    public bool ShadowOnly { get; init; } = true;
    public bool RuntimeAuthority { get; init; }
    public bool GateAuthority { get; init; }
    public string Notes { get; init; } = string.Empty;
}

public sealed class LearningLayerBaselinePlan
{
    public IReadOnlyList<LearningLayerBaselinePlanEntry> Entries { get; init; } = Array.Empty<LearningLayerBaselinePlanEntry>();
    public bool AllEntriesShadowOnly { get; init; } = true;
    public bool AnyEntryRuntimeAuthority { get; init; }
    public bool AnyEntryGateAuthority { get; init; }
}

public sealed record LearningLayerBootstrapContext
{
    public LearningLayerDatasetInventory Inventory { get; init; } = new();
    public bool RankerAblationReportPresent { get; init; }
    public bool RankerWeightSweepReportPresent { get; init; }
    public FormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutReport? V8ScopedActivationCloseoutReport { get; init; }
    // Synthetic test knobs — real flow never overrides these.
    public bool ShadowOnlyOverride { get; init; } = true;
    public bool RuntimeAuthorityOverride { get; init; }
    public bool GateAuthorityOverride { get; init; }
    public bool PackageOutputChangedOverride { get; init; }
    public bool FormalPackageWrittenOverride { get; init; }
    public bool GlobalDefaultOnOverride { get; init; }
}

public sealed class LearningLayerBootstrapDecision
{
    public string Status { get; init; } = LearningLayerBootstrapStatuses.LearningLayerBootstrapBlocked;
    public LearningLayerDatasetInventory DatasetInventory { get; init; } = new();
    public LearningLayerFeatureContract FeatureContract { get; init; } = new();
    public LearningLayerBaselinePlan BaselinePlan { get; init; } = new();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;
    public bool ShadowOnly { get; init; } = true;
    public bool RuntimeAuthority { get; init; }
    public bool GateAuthority { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool V8ScopedActivationPreserved { get; init; }
    public string Recommendation { get; init; } = string.Empty;
    public string NextAllowedPhase { get; init; } = string.Empty;
}

public static class LearningLayerBootstrapPolicy
{
    public static LearningLayerBootstrapDecision Evaluate(
        LearningLayerBootstrapContext ctx,
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent)
    {
        var blocked = new List<string>();
        var inv = ctx.Inventory;

        // Dataset prerequisites
        if (!inv.DatasetQualityReportPresent) blocked.Add(LearningLayerBootstrapBlockedReasons.DatasetQualityReportMissing);
        if (!inv.RankingPairsFilePresent || inv.RankingPairCount <= 0) blocked.Add(LearningLayerBootstrapBlockedReasons.RankingPairsMissing);
        if (!inv.RouterIntentExamplesFilePresent || inv.RouterIntentExampleCount <= 0) blocked.Add(LearningLayerBootstrapBlockedReasons.RouterIntentExamplesMissing);
        if (!inv.RankingPairsFilePresent && !inv.RouterIntentExamplesFilePresent
            && !inv.PolicyFeedbackFeaturesFilePresent && !inv.HardNegativesFilePresent)
            blocked.Add(LearningLayerBootstrapBlockedReasons.AllFeaturesMissing);
        if (!ctx.RankerAblationReportPresent) blocked.Add(LearningLayerBootstrapBlockedReasons.RankerAblationReportMissing);

        // Authority invariants — V9 may NEVER claim runtime/gate authority.
        if (!ctx.ShadowOnlyOverride) blocked.Add(LearningLayerBootstrapBlockedReasons.ShadowOnlyFalse);
        if (ctx.RuntimeAuthorityOverride) blocked.Add(LearningLayerBootstrapBlockedReasons.RuntimeAuthorityTrue);
        if (ctx.GateAuthorityOverride) blocked.Add(LearningLayerBootstrapBlockedReasons.GateAuthorityTrue);
        if (ctx.PackageOutputChangedOverride) blocked.Add(LearningLayerBootstrapBlockedReasons.PackageOutputChangedTrue);
        if (ctx.FormalPackageWrittenOverride) blocked.Add(LearningLayerBootstrapBlockedReasons.FormalPackageWrittenTrue);
        if (ctx.GlobalDefaultOnOverride) blocked.Add(LearningLayerBootstrapBlockedReasons.GlobalDefaultOnTrue);

        // V8 closeout must still be intact — V9 is layered on top of V8 scoped activation.
        var v8 = ctx.V8ScopedActivationCloseoutReport;
        if (v8 is null) blocked.Add(LearningLayerBootstrapBlockedReasons.V8ScopedActivationCloseoutMissing);
        else
        {
            if (!v8.ScopedLiveActivationSafetyCloseoutPassed || !v8.GatePassed)
                blocked.Add(LearningLayerBootstrapBlockedReasons.V8ScopedActivationCloseoutNotPassed);
            if (!v8.ActivationStillActive || !v8.RuntimeActivationCurrent)
                blocked.Add(LearningLayerBootstrapBlockedReasons.V8ScopedActivationLost);
        }

        // Environment
        if (!rtPassed) blocked.Add(LearningLayerBootstrapBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(LearningLayerBootstrapBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(LearningLayerBootstrapBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(LearningLayerBootstrapBlockedReasons.MainlineTrustRegistryPresent);

        var featureContract = BuildFeatureContract();
        var baselinePlan = BuildBaselinePlan();

        var finalBlocked = blocked.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        var ready = finalBlocked.Length == 0;
        var v8Preserved = v8 is not null && v8.ScopedLiveActivationSafetyCloseoutPassed && v8.ActivationStillActive
            && v8.RuntimeActivationCurrent && v8.FormalRetrievalAllowedCurrent && v8.RuntimeSwitchAllowedCurrent
            && !v8.GlobalDefaultOn && !v8.PackageOutputChanged && !v8.FormalPackageWritten
            && !v8.VectorStoreBindingChanged && !v8.MainlineEvidencePresent && !v8.MainlineTrustRegistryPresent;

        return new LearningLayerBootstrapDecision
        {
            Status = ready ? LearningLayerBootstrapStatuses.LearningLayerBootstrapReady : LearningLayerBootstrapStatuses.LearningLayerBootstrapBlocked,
            DatasetInventory = inv,
            FeatureContract = featureContract,
            BaselinePlan = baselinePlan,
            BlockedReasons = finalBlocked,
            Reasoning = ready
                ? "learning layer bootstrap ready — dataset inventory, feature contract, and baseline plan in place; all entries shadow-only; V8 scoped activation preserved."
                : $"{finalBlocked.Length} blocked reason(s); learning layer bootstrap blocked.",
            ShadowOnly = true,
            RuntimeAuthority = false,
            GateAuthority = false,
            PackageOutputChanged = false,
            FormalPackageWritten = false,
            GlobalDefaultOn = false,
            V8ScopedActivationPreserved = v8Preserved,
            Recommendation = ready ? "ProceedToV9BaselineImplementation" : "Blocked",
            NextAllowedPhase = ready ? "V9.1BaselineImplementation" : string.Empty
        };
    }

    /// <summary>V9.0 feature contract — every entry is ShadowOnly + non-authoritative. Future task families list features they will eventually consume, also ShadowOnly.</summary>
    private static LearningLayerFeatureContract BuildFeatureContract()
    {
        var entries = new List<LearningLayerFeatureContractEntry>
        {
            // CandidateReranker — ranking-pairs.jsonl
            Entry("CandidateReranker", "Recall3", "ranking-pairs.featureSnapshot.recall3", "float", false),
            Entry("CandidateReranker", "Recall5", "ranking-pairs.featureSnapshot.recall5", "float", false),
            Entry("CandidateReranker", "Recall10", "ranking-pairs.featureSnapshot.recall10", "float", false),
            Entry("CandidateReranker", "MRR", "ranking-pairs.featureSnapshot.mrr", "float", false),
            Entry("CandidateReranker", "PositiveRank", "ranking-pairs.featureSnapshot.positiveRank", "int", false),
            Entry("CandidateReranker", "NegativeRank", "ranking-pairs.featureSnapshot.negativeRank", "int", false),
            Entry("CandidateReranker", "PositiveScore", "ranking-pairs.featureSnapshot.positiveScore", "int", false),
            Entry("CandidateReranker", "NegativeScore", "ranking-pairs.featureSnapshot.negativeScore", "int", false),
            Entry("CandidateReranker", "PositiveKind", "ranking-pairs.featureSnapshot.positiveKind", "string", true),
            Entry("CandidateReranker", "PositiveSection", "ranking-pairs.featureSnapshot.positiveSection", "string", true),
            Entry("CandidateReranker", "PackageHasAllConstraints", "ranking-pairs.featureSnapshot.packageHasAllConstraints", "bool", false),
            Entry("CandidateReranker", "PackageHasAllEntities", "ranking-pairs.featureSnapshot.packageHasAllEntities", "bool", false),
            // RouterIntentClassifier — router-intent-examples.jsonl
            Entry("RouterIntentClassifier", "Mode", "router-intent-examples.mode", "string", false),
            Entry("RouterIntentClassifier", "Intent", "router-intent-examples.intent", "string", false),
            Entry("RouterIntentClassifier", "CandidateImportance", "router-intent-examples.candidateImportance", "float", false),
            Entry("RouterIntentClassifier", "CandidateRecency", "router-intent-examples.candidateRecency", "float", false),
            Entry("RouterIntentClassifier", "KeywordMatchScore", "router-intent-examples.keywordMatchScore", "float", false),
            Entry("RouterIntentClassifier", "SemanticAnchorMatchScore", "router-intent-examples.semanticAnchorMatchScore", "float", false),
            Entry("RouterIntentClassifier", "ShortTermMatchScore", "router-intent-examples.shortTermMatchScore", "float", false),
            Entry("RouterIntentClassifier", "ConstraintMatchScore", "router-intent-examples.constraintMatchScore", "float", false),
            Entry("RouterIntentClassifier", "LifecycleRisk", "router-intent-examples.lifecycleRisk", "float", false),
            Entry("RouterIntentClassifier", "RelationPathCount", "router-intent-examples.relationPathCount", "int", false),
            // PackageQuality (future) — policy-feedback-features.jsonl (currently empty)
            Entry("PackageQuality", "PackageConstraintCoverage", "policy-feedback-features.constraintCoverage", "float", true),
            Entry("PackageQuality", "PackageEntityCoverage", "policy-feedback-features.entityCoverage", "float", true),
            Entry("PackageQuality", "PackageUncertaintyCoverage", "policy-feedback-features.uncertaintyCoverage", "float", true),
            // MemoryPromotion (future)
            Entry("MemoryPromotion", "MemoryRecency", "memory-shadow.recency", "float", true),
            Entry("MemoryPromotion", "MemorySemanticAnchor", "memory-shadow.semanticAnchor", "float", true),
            Entry("MemoryPromotion", "MemoryLifecycleStage", "memory-shadow.lifecycleStage", "string", true),
            // ConstraintGap (future)
            Entry("ConstraintGap", "MissingConstraintCount", "policy-feedback-features.missingConstraintCount", "int", true),
            Entry("ConstraintGap", "MissingEntityCount", "policy-feedback-features.missingEntityCount", "int", true),
            Entry("ConstraintGap", "MissingUncertaintyCount", "policy-feedback-features.missingUncertaintyCount", "int", true)
        };
        var families = entries.Select(e => e.TaskFamily).Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        return new LearningLayerFeatureContract
        {
            Entries = entries,
            TaskFamilies = families,
            AllEntriesShadowOnly = entries.All(static e => e.ShadowOnly),
            AnyEntryRuntimeAuthority = entries.Any(static e => e.RuntimeAuthority),
            AnyEntryGateAuthority = entries.Any(static e => e.GateAuthority)
        };
    }

    private static LearningLayerFeatureContractEntry Entry(string family, string name, string source, string type, bool nullable)
        => new()
        {
            TaskFamily = family,
            FeatureName = name,
            Source = source,
            Type = type,
            Nullable = nullable,
            ShadowOnly = true,
            RuntimeAuthority = false,
            GateAuthority = false
        };

    /// <summary>V9.0 baseline plan — every entry ShadowOnly. None of these touches production rerank / gates / package output.</summary>
    private static LearningLayerBaselinePlan BuildBaselinePlan()
    {
        var entries = new[]
        {
            Plan("WeightedBaseline", "CandidateReranker", "V9.1", "rule-driven weighted score with existing feature ablation; mirrors current rules."),
            Plan("LogisticBaseline", "CandidateReranker", "V9.1", "logistic regression over hand-crafted features; pairwise loss."),
            Plan("GBDTBaseline", "CandidateReranker", "V9.2", "gradient-boosted decision tree; learns interactions between features."),
            Plan("LightweightMLPShadowCandidate", "CandidateReranker", "V9.3", "small MLP, train-on-cpu; shadow-only inference."),
            Plan("RouterIntentLogistic", "RouterIntentClassifier", "V9.1", "logistic regression over router intent features."),
            Plan("RouterIntentGBDT", "RouterIntentClassifier", "V9.2", "gradient-boosted decision tree for intent classification."),
            Plan("LLMAssistedFailureDiagnosis", "CandidateReranker", "V9.4", "LLM analyzes top failure examples + emits structured diagnoses (still shadow)."),
            Plan("HardNegativeGeneration", "CandidateReranker", "V9.4", "LLM proposes hard negatives; human-review gated before ingestion."),
            Plan("HumanReviewFeedbackIngestion", "PackageQuality", "V9.5", "ingest human-reviewed feedback to grow policy-feedback-features dataset."),
            Plan("PackageQualityShadowScorer", "PackageQuality", "V9.5", "shadow scorer for package coverage / constraint completeness."),
            Plan("MemoryPromotionShadowScorer", "MemoryPromotion", "V9.6", "shadow scorer for memory lifecycle promotion candidates."),
            Plan("ConstraintGapShadowDetector", "ConstraintGap", "V9.6", "shadow detector for missing constraints / entities / uncertainties.")
        };
        return new LearningLayerBaselinePlan
        {
            Entries = entries,
            AllEntriesShadowOnly = entries.All(static e => e.ShadowOnly),
            AnyEntryRuntimeAuthority = entries.Any(static e => e.RuntimeAuthority),
            AnyEntryGateAuthority = entries.Any(static e => e.GateAuthority)
        };
    }

    private static LearningLayerBaselinePlanEntry Plan(string baseline, string family, string stage, string notes)
        => new()
        {
            BaselineName = baseline,
            TaskFamily = family,
            Stage = stage,
            ShadowOnly = true,
            RuntimeAuthority = false,
            GateAuthority = false,
            Notes = notes
        };
}

public sealed record LearningLayerBootstrapScenario(
    string CaseName,
    LearningLayerBootstrapContext Context,
    bool RtPassed,
    bool P15Passed,
    bool MainlineEvidencePresent,
    bool MainlineRegistryPresent,
    string ExpectedStatus,
    string? ExpectedBlockedReason);

public sealed class LearningLayerBootstrapRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public LearningLayerBootstrapReport Run(
        LearningLayerBootstrapContext realContext,
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent,
        LearningLayerBootstrapOptions? opt = null)
    {
        opt ??= new LearningLayerBootstrapOptions();
        var now = DateTimeOffset.UtcNow;
        var cases = BuildScenarios().Select(static scenario =>
        {
            var decision = LearningLayerBootstrapPolicy.Evaluate(
                scenario.Context, scenario.RtPassed, scenario.P15Passed,
                scenario.MainlineEvidencePresent, scenario.MainlineRegistryPresent);
            var statusMatched = string.Equals(scenario.ExpectedStatus, decision.Status, StringComparison.Ordinal);
            var blockedReasonMatched = scenario.ExpectedBlockedReason is null
                || decision.BlockedReasons.Contains(scenario.ExpectedBlockedReason, StringComparer.Ordinal);
            return new LearningLayerBootstrapCase
            {
                CaseName = scenario.CaseName,
                ExpectedStatus = scenario.ExpectedStatus,
                ActualStatus = decision.Status,
                ExpectedBlockedReason = scenario.ExpectedBlockedReason ?? string.Empty,
                ActualBlockedReasons = decision.BlockedReasons,
                ShadowOnly = decision.ShadowOnly,
                RuntimeAuthority = decision.RuntimeAuthority,
                GateAuthority = decision.GateAuthority,
                PackageOutputChanged = decision.PackageOutputChanged,
                FormalPackageWritten = decision.FormalPackageWritten,
                GlobalDefaultOn = decision.GlobalDefaultOn,
                V8ScopedActivationPreserved = decision.V8ScopedActivationPreserved,
                Reasoning = decision.Reasoning,
                StatusMatched = statusMatched,
                BlockedReasonMatched = blockedReasonMatched,
                // Authority invariants: V9 may never claim runtime / gate authority, never change package output / write formal package / enable global default.
                PassedAsExpected = statusMatched && blockedReasonMatched
                    && decision.ShadowOnly
                    && !decision.RuntimeAuthority && !decision.GateAuthority
                    && !decision.PackageOutputChanged && !decision.FormalPackageWritten
                    && !decision.GlobalDefaultOn
            };
        }).ToArray();

        var blocked = new List<string>();
        if (cases.Length < 14) blocked.Add("InsufficientLearningLayerBootstrapCases");
        if (cases.Any(static c => !c.PassedAsExpected)) blocked.Add("LearningLayerBootstrapMatrixFailed");
        foreach (var status in new[] { LearningLayerBootstrapStatuses.LearningLayerBootstrapReady, LearningLayerBootstrapStatuses.LearningLayerBootstrapBlocked })
        {
            if (!cases.Any(c => string.Equals(c.ActualStatus, status, StringComparison.Ordinal)))
                blocked.Add($"StatusBranchNotCovered:{status}");
        }
        if (cases.Any(static c => c.RuntimeAuthority)) blocked.Add("RuntimeAuthorityLeaked");
        if (cases.Any(static c => c.GateAuthority)) blocked.Add("GateAuthorityLeaked");
        if (cases.Any(static c => c.PackageOutputChanged)) blocked.Add("PackageOutputChangedLeaked");
        if (cases.Any(static c => c.FormalPackageWritten)) blocked.Add("FormalPackageWrittenLeaked");
        if (cases.Any(static c => c.GlobalDefaultOn)) blocked.Add("GlobalDefaultOnLeaked");

        var realDecision = LearningLayerBootstrapPolicy.Evaluate(realContext, rtPassed, p15Passed, mainlineEvidencePresent, mainlineRegistryPresent);
        if (!string.Equals(realDecision.Status, LearningLayerBootstrapStatuses.LearningLayerBootstrapReady, StringComparison.Ordinal))
            blocked.AddRange(realDecision.BlockedReasons.Select(static x => $"RealLearningLayerBootstrap:{x}"));
        if (!rtPassed) blocked.Add(LearningLayerBootstrapBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(LearningLayerBootstrapBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(LearningLayerBootstrapBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(LearningLayerBootstrapBlockedReasons.MainlineTrustRegistryPresent);

        var finalBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var passed = finalBlocked.Length == 0;
        return new LearningLayerBootstrapReport
        {
            OperationId = $"v9-learning-layer-bootstrap-{Guid.NewGuid():N}",
            CreatedAt = now,
            LearningLayerBootstrapPassed = passed,
            GatePassed = opt.IsGate && passed,
            TotalCases = cases.Length,
            PassedCases = cases.Count(static c => c.PassedAsExpected),
            FailedCases = cases.Count(static c => !c.PassedAsExpected),
            ReadyCases = cases.Count(c => string.Equals(c.ActualStatus, LearningLayerBootstrapStatuses.LearningLayerBootstrapReady, StringComparison.Ordinal)),
            BlockedCases = cases.Count(c => string.Equals(c.ActualStatus, LearningLayerBootstrapStatuses.LearningLayerBootstrapBlocked, StringComparison.Ordinal)),
            Cases = cases,
            DatasetInventory = realDecision.DatasetInventory,
            FeatureContract = realDecision.FeatureContract,
            BaselinePlan = realDecision.BaselinePlan,
            ShadowOnly = true,
            RuntimeAuthority = false,
            GateAuthority = false,
            PackageOutputChanged = false,
            FormalPackageWritten = false,
            GlobalDefaultOn = false,
            V8ScopedActivationPreserved = passed && realDecision.V8ScopedActivationPreserved,
            UpstreamV8ScopedActivationCloseoutGatePresent = realContext.V8ScopedActivationCloseoutReport is not null,
            UpstreamV8ScopedActivationCloseoutGatePassed = realContext.V8ScopedActivationCloseoutReport?.GatePassed ?? false,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent,
            Recommendation = passed ? "ProceedToV9BaselineImplementation" : "Blocked",
            NextAllowedPhase = passed ? "V9.1BaselineImplementation" : string.Empty,
            BlockedReasons = finalBlocked,
            Diagnostics = new[]
            {
                $"total={cases.Length}",
                $"realStatus={realDecision.Status}",
                $"rankingPairCount={realDecision.DatasetInventory.RankingPairCount}",
                $"routerIntentExampleCount={realDecision.DatasetInventory.RouterIntentExampleCount}",
                $"policyFeedbackFeatureCount={realDecision.DatasetInventory.PolicyFeedbackFeatureCount}",
                $"hardNegativeCount={realDecision.DatasetInventory.HardNegativeCount}",
                $"featureContractEntryCount={realDecision.FeatureContract.Entries.Count}",
                $"baselinePlanEntryCount={realDecision.BaselinePlan.Entries.Count}",
                $"runtimeGate={rtPassed}",
                $"p15Gate={p15Passed}",
                $"v8Preserved={realDecision.V8ScopedActivationPreserved}"
            }
        };
    }

    private static IReadOnlyList<LearningLayerBootstrapScenario> BuildScenarios()
    {
        var clean = BuildCleanContext();
        return [
            new("AllUpstreamClean", clean, true, true, false, false, LearningLayerBootstrapStatuses.LearningLayerBootstrapReady, null),
            new("DatasetQualityReportMissing", clean with { Inventory = clean.Inventory with { DatasetQualityReportPresent = false } }, true, true, false, false, LearningLayerBootstrapStatuses.LearningLayerBootstrapBlocked, LearningLayerBootstrapBlockedReasons.DatasetQualityReportMissing),
            new("RankingPairsMissing", clean with { Inventory = clean.Inventory with { RankingPairsFilePresent = false, RankingPairCount = 0 } }, true, true, false, false, LearningLayerBootstrapStatuses.LearningLayerBootstrapBlocked, LearningLayerBootstrapBlockedReasons.RankingPairsMissing),
            new("RouterIntentExamplesMissing", clean with { Inventory = clean.Inventory with { RouterIntentExamplesFilePresent = false, RouterIntentExampleCount = 0 } }, true, true, false, false, LearningLayerBootstrapStatuses.LearningLayerBootstrapBlocked, LearningLayerBootstrapBlockedReasons.RouterIntentExamplesMissing),
            new("AllFeaturesMissing", clean with { Inventory = clean.Inventory with { RankingPairsFilePresent = false, RankingPairCount = 0, RouterIntentExamplesFilePresent = false, RouterIntentExampleCount = 0, PolicyFeedbackFeaturesFilePresent = false, HardNegativesFilePresent = false, HardNegativeCount = 0 } }, true, true, false, false, LearningLayerBootstrapStatuses.LearningLayerBootstrapBlocked, LearningLayerBootstrapBlockedReasons.AllFeaturesMissing),
            new("RankerAblationReportMissing", clean with { RankerAblationReportPresent = false }, true, true, false, false, LearningLayerBootstrapStatuses.LearningLayerBootstrapBlocked, LearningLayerBootstrapBlockedReasons.RankerAblationReportMissing),
            new("ShadowOnlyFalse", clean with { ShadowOnlyOverride = false }, true, true, false, false, LearningLayerBootstrapStatuses.LearningLayerBootstrapBlocked, LearningLayerBootstrapBlockedReasons.ShadowOnlyFalse),
            new("RuntimeAuthorityTrue", clean with { RuntimeAuthorityOverride = true }, true, true, false, false, LearningLayerBootstrapStatuses.LearningLayerBootstrapBlocked, LearningLayerBootstrapBlockedReasons.RuntimeAuthorityTrue),
            new("GateAuthorityTrue", clean with { GateAuthorityOverride = true }, true, true, false, false, LearningLayerBootstrapStatuses.LearningLayerBootstrapBlocked, LearningLayerBootstrapBlockedReasons.GateAuthorityTrue),
            new("PackageOutputChangedTrue", clean with { PackageOutputChangedOverride = true }, true, true, false, false, LearningLayerBootstrapStatuses.LearningLayerBootstrapBlocked, LearningLayerBootstrapBlockedReasons.PackageOutputChangedTrue),
            new("FormalPackageWrittenTrue", clean with { FormalPackageWrittenOverride = true }, true, true, false, false, LearningLayerBootstrapStatuses.LearningLayerBootstrapBlocked, LearningLayerBootstrapBlockedReasons.FormalPackageWrittenTrue),
            new("GlobalDefaultOnTrue", clean with { GlobalDefaultOnOverride = true }, true, true, false, false, LearningLayerBootstrapStatuses.LearningLayerBootstrapBlocked, LearningLayerBootstrapBlockedReasons.GlobalDefaultOnTrue),
            new("V8ScopedActivationCloseoutMissing", clean with { V8ScopedActivationCloseoutReport = null }, true, true, false, false, LearningLayerBootstrapStatuses.LearningLayerBootstrapBlocked, LearningLayerBootstrapBlockedReasons.V8ScopedActivationCloseoutMissing),
            new("V8ScopedActivationCloseoutNotPassed", clean with { V8ScopedActivationCloseoutReport = CloneV8(clean.V8ScopedActivationCloseoutReport!, passed:false, gatePassed:false) }, true, true, false, false, LearningLayerBootstrapStatuses.LearningLayerBootstrapBlocked, LearningLayerBootstrapBlockedReasons.V8ScopedActivationCloseoutNotPassed),
            new("V8ScopedActivationLost", clean with { V8ScopedActivationCloseoutReport = CloneV8(clean.V8ScopedActivationCloseoutReport!, activationStillActive:false) }, true, true, false, false, LearningLayerBootstrapStatuses.LearningLayerBootstrapBlocked, LearningLayerBootstrapBlockedReasons.V8ScopedActivationLost),
            new("RuntimeGateNotPassed", clean, false, true, false, false, LearningLayerBootstrapStatuses.LearningLayerBootstrapBlocked, LearningLayerBootstrapBlockedReasons.RuntimeChangeGateNotPassed),
            new("P15GateNotPassed", clean, true, false, false, false, LearningLayerBootstrapStatuses.LearningLayerBootstrapBlocked, LearningLayerBootstrapBlockedReasons.P15GateNotPassed),
            new("MainlineEvidencePresent", clean, true, true, true, false, LearningLayerBootstrapStatuses.LearningLayerBootstrapBlocked, LearningLayerBootstrapBlockedReasons.MainlineEvidencePresent),
            new("MainlineTrustRegistryPresent", clean, true, true, false, true, LearningLayerBootstrapStatuses.LearningLayerBootstrapBlocked, LearningLayerBootstrapBlockedReasons.MainlineTrustRegistryPresent)
        ];
    }

    private static LearningLayerBootstrapContext BuildCleanContext()
        => new()
        {
            Inventory = new LearningLayerDatasetInventory
            {
                RankingPairCount = 253,
                RouterIntentExampleCount = 163,
                PolicyFeedbackFeatureCount = 0,
                HardNegativeCount = 17,
                RankingPairsFilePresent = true,
                RouterIntentExamplesFilePresent = true,
                PolicyFeedbackFeaturesFilePresent = true,
                HardNegativesFilePresent = true,
                DatasetQualityReportPresent = true,
                UsableTaskFamilies = new[] { "CandidateReranker", "RouterIntentClassifier" },
                NotReadyTaskFamilies = new[] { "PackageQuality", "MemoryPromotion", "ConstraintGap" },
                KnownRisks = new[] { "NoPolicyFeedback", "EvalOnlyDataset", "MissingNegativeSamples" }
            },
            RankerAblationReportPresent = true,
            RankerWeightSweepReportPresent = true,
            V8ScopedActivationCloseoutReport = BuildCleanV8Closeout(),
            ShadowOnlyOverride = true,
            RuntimeAuthorityOverride = false,
            GateAuthorityOverride = false,
            PackageOutputChangedOverride = false,
            FormalPackageWrittenOverride = false,
            GlobalDefaultOnOverride = false
        };

    private static FormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutReport BuildCleanV8Closeout()
        => new()
        {
            OperationId = "fixture-v8-closeout",
            CreatedAt = DateTimeOffset.Parse("2026-06-28T12:00:00Z"),
            ScopedLiveActivationSafetyCloseoutPassed = true,
            GatePassed = true,
            SourceActivationId = "frp-guarded-live-runtime-activation-fixture",
            BoundGrantId = "frp-grant-fixture",
            BoundCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation,
            BoundScope = "demo-workspace/demo-collection",
            ActivationStillActive = true,
            RuntimeActivationCurrent = true,
            FormalRetrievalAllowedCurrent = true,
            RuntimeSwitchAllowedCurrent = true,
            RollbackDryRunReady = true,
            KillSwitchDryRunReady = true,
            RevocationDryRunReady = true,
            StateMutationApplied = false,
            ActivationActuallyRevoked = false,
            RuntimeStateChangedOutsideScope = false,
            GlobalDefaultOn = false,
            PackageOutputChanged = false,
            FormalPackageWritten = false,
            VectorStoreBindingChanged = false,
            MainlineEvidencePresent = false,
            MainlineTrustRegistryPresent = false,
            Recommendation = "V8ScopedActivationClosed",
            NextAllowedPhase = "V9LearningLayer"
        };

    private static FormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutReport CloneV8(
        FormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutReport source,
        bool? passed = null, bool? gatePassed = null, bool? activationStillActive = null)
        => new()
        {
            OperationId = source.OperationId,
            CreatedAt = source.CreatedAt,
            ScopedLiveActivationSafetyCloseoutPassed = passed ?? source.ScopedLiveActivationSafetyCloseoutPassed,
            GatePassed = gatePassed ?? source.GatePassed,
            SourceActivationId = source.SourceActivationId,
            BoundGrantId = source.BoundGrantId,
            BoundCapability = source.BoundCapability,
            BoundScope = source.BoundScope,
            ActivationStillActive = activationStillActive ?? source.ActivationStillActive,
            RuntimeActivationCurrent = source.RuntimeActivationCurrent,
            FormalRetrievalAllowedCurrent = source.FormalRetrievalAllowedCurrent,
            RuntimeSwitchAllowedCurrent = source.RuntimeSwitchAllowedCurrent,
            RollbackDryRunReady = source.RollbackDryRunReady,
            KillSwitchDryRunReady = source.KillSwitchDryRunReady,
            RevocationDryRunReady = source.RevocationDryRunReady,
            StateMutationApplied = source.StateMutationApplied,
            ActivationActuallyRevoked = source.ActivationActuallyRevoked,
            RuntimeStateChangedOutsideScope = source.RuntimeStateChangedOutsideScope,
            GlobalDefaultOn = source.GlobalDefaultOn,
            PackageOutputChanged = source.PackageOutputChanged,
            FormalPackageWritten = source.FormalPackageWritten,
            VectorStoreBindingChanged = source.VectorStoreBindingChanged,
            MainlineEvidencePresent = source.MainlineEvidencePresent,
            MainlineTrustRegistryPresent = source.MainlineTrustRegistryPresent,
            Recommendation = source.Recommendation,
            NextAllowedPhase = source.NextAllowedPhase
        };

    /// <summary>V9.0: load the real on-disk learning inventory + V8.26 closeout.</summary>
    public static LearningLayerBootstrapContext LoadRealContext(
        FormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutReport? v8Closeout)
    {
        var featuresDir = Path.Combine("learning", "features");
        var rankingPairsPath = Path.Combine(featuresDir, "ranking-pairs.jsonl");
        var routerIntentPath = Path.Combine(featuresDir, "router-intent-examples.jsonl");
        var policyFeedbackPath = Path.Combine(featuresDir, "policy-feedback-features.jsonl");
        var hardNegativesPath = Path.Combine(featuresDir, "hard-negatives.jsonl");
        var datasetQualityPath = Path.Combine(featuresDir, "dataset-quality-report.json");
        var ablationPath = Path.Combine("learning", "baselines", "ranker-ablation-report.json");
        var weightSweepPath = Path.Combine("learning", "baselines", "ranker-weight-sweep-report.json");

        var rankingPairCount = CountLines(rankingPairsPath);
        var routerIntentCount = CountLines(routerIntentPath);
        var policyFeedbackCount = CountLines(policyFeedbackPath);
        var hardNegativeCount = CountLines(hardNegativesPath);

        // Prefer dataset-quality-report.json counts when present — those are the authoritative dataset metrics.
        if (File.Exists(datasetQualityPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(datasetQualityPath));
                if (doc.RootElement.TryGetProperty("rankingPairCount", out var rp)) rankingPairCount = rp.GetInt32();
                if (doc.RootElement.TryGetProperty("routerIntentExampleCount", out var ri)) routerIntentCount = ri.GetInt32();
                if (doc.RootElement.TryGetProperty("policyFeedbackFeatureCount", out var pf)) policyFeedbackCount = pf.GetInt32();
            }
            catch { /* fall back to line counts */ }
        }

        var usable = new List<string>();
        var notReady = new List<string>();
        var risks = new List<string>();
        if (rankingPairCount > 0) usable.Add("CandidateReranker"); else notReady.Add("CandidateReranker");
        if (routerIntentCount > 0) usable.Add("RouterIntentClassifier"); else notReady.Add("RouterIntentClassifier");
        notReady.Add("PackageQuality");
        notReady.Add("MemoryPromotion");
        notReady.Add("ConstraintGap");
        if (policyFeedbackCount == 0) risks.Add("NoPolicyFeedback");
        risks.Add("EvalOnlyDataset");
        if (hardNegativeCount < 50) risks.Add("MissingNegativeSamples");

        var inventory = new LearningLayerDatasetInventory
        {
            RankingPairCount = rankingPairCount,
            RouterIntentExampleCount = routerIntentCount,
            PolicyFeedbackFeatureCount = policyFeedbackCount,
            HardNegativeCount = hardNegativeCount,
            RankingPairsFilePresent = File.Exists(rankingPairsPath),
            RouterIntentExamplesFilePresent = File.Exists(routerIntentPath),
            PolicyFeedbackFeaturesFilePresent = File.Exists(policyFeedbackPath),
            HardNegativesFilePresent = File.Exists(hardNegativesPath),
            DatasetQualityReportPresent = File.Exists(datasetQualityPath),
            UsableTaskFamilies = usable.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray(),
            NotReadyTaskFamilies = notReady.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray(),
            KnownRisks = risks.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray()
        };
        return new LearningLayerBootstrapContext
        {
            Inventory = inventory,
            RankerAblationReportPresent = File.Exists(ablationPath),
            RankerWeightSweepReportPresent = File.Exists(weightSweepPath),
            V8ScopedActivationCloseoutReport = v8Closeout,
            ShadowOnlyOverride = true,
            RuntimeAuthorityOverride = false,
            GateAuthorityOverride = false,
            PackageOutputChangedOverride = false,
            FormalPackageWrittenOverride = false,
            GlobalDefaultOnOverride = false
        };
    }

    private static int CountLines(string path)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            var n = 0;
            using var reader = new StreamReader(path);
            while (reader.ReadLine() is not null) n++;
            return n;
        }
        catch { return 0; }
    }

    public static string BuildMarkdown(string title, LearningLayerBootstrapReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"- LearningLayerBootstrapPassed: `{report.LearningLayerBootstrapPassed}`");
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- TotalCases: `{report.TotalCases}` PassedCases: `{report.PassedCases}` FailedCases: `{report.FailedCases}`");
        builder.AppendLine($"- ReadyCases: `{report.ReadyCases}` BlockedCases: `{report.BlockedCases}`");
        builder.AppendLine($"- ShadowOnly: `{report.ShadowOnly}` RuntimeAuthority: `{report.RuntimeAuthority}` GateAuthority: `{report.GateAuthority}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}` FormalPackageWritten: `{report.FormalPackageWritten}` GlobalDefaultOn: `{report.GlobalDefaultOn}`");
        builder.AppendLine($"- V8ScopedActivationPreserved: `{report.V8ScopedActivationPreserved}`");
        builder.AppendLine($"- MainlineEvidencePresent: `{report.MainlineEvidencePresent}` MainlineTrustRegistryPresent: `{report.MainlineTrustRegistryPresent}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}` NextAllowedPhase: `{report.NextAllowedPhase}`");
        builder.AppendLine();
        builder.AppendLine("## Dataset Inventory");
        builder.AppendLine($"- RankingPairCount: `{report.DatasetInventory.RankingPairCount}`");
        builder.AppendLine($"- RouterIntentExampleCount: `{report.DatasetInventory.RouterIntentExampleCount}`");
        builder.AppendLine($"- PolicyFeedbackFeatureCount: `{report.DatasetInventory.PolicyFeedbackFeatureCount}`");
        builder.AppendLine($"- HardNegativeCount: `{report.DatasetInventory.HardNegativeCount}`");
        builder.AppendLine($"- UsableTaskFamilies: {string.Join(", ", report.DatasetInventory.UsableTaskFamilies)}");
        builder.AppendLine($"- NotReadyTaskFamilies: {string.Join(", ", report.DatasetInventory.NotReadyTaskFamilies)}");
        builder.AppendLine($"- KnownRisks: {string.Join(", ", report.DatasetInventory.KnownRisks)}");
        builder.AppendLine();
        builder.AppendLine("## Feature Contract");
        builder.AppendLine($"- Entry count: `{report.FeatureContract.Entries.Count}` Task families: {string.Join(", ", report.FeatureContract.TaskFamilies)}");
        builder.AppendLine($"- AllEntriesShadowOnly: `{report.FeatureContract.AllEntriesShadowOnly}` AnyEntryRuntimeAuthority: `{report.FeatureContract.AnyEntryRuntimeAuthority}` AnyEntryGateAuthority: `{report.FeatureContract.AnyEntryGateAuthority}`");
        builder.AppendLine();
        builder.AppendLine("## Baseline Plan");
        builder.AppendLine($"- Entry count: `{report.BaselinePlan.Entries.Count}`");
        builder.AppendLine($"- AllEntriesShadowOnly: `{report.BaselinePlan.AllEntriesShadowOnly}` AnyEntryRuntimeAuthority: `{report.BaselinePlan.AnyEntryRuntimeAuthority}` AnyEntryGateAuthority: `{report.BaselinePlan.AnyEntryGateAuthority}`");
        foreach (var entry in report.BaselinePlan.Entries)
            builder.AppendLine($"  - `{entry.BaselineName}` ({entry.TaskFamily} / {entry.Stage}) — ShadowOnly={entry.ShadowOnly}");
        if (report.BlockedReasons.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Blocked Reasons");
            foreach (var reason in report.BlockedReasons) builder.AppendLine($"- `{reason}`");
        }
        return builder.ToString();
    }
}

public sealed class LearningLayerBootstrapCase
{
    public string CaseName { get; init; } = string.Empty;
    public string ExpectedStatus { get; init; } = string.Empty;
    public string ActualStatus { get; init; } = string.Empty;
    public string ExpectedBlockedReason { get; init; } = string.Empty;
    public IReadOnlyList<string> ActualBlockedReasons { get; init; } = Array.Empty<string>();
    public bool ShadowOnly { get; init; }
    public bool RuntimeAuthority { get; init; }
    public bool GateAuthority { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool V8ScopedActivationPreserved { get; init; }
    public string Reasoning { get; init; } = string.Empty;
    public bool StatusMatched { get; init; }
    public bool BlockedReasonMatched { get; init; }
    public bool PassedAsExpected { get; init; }
}

public sealed class LearningLayerBootstrapReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool LearningLayerBootstrapPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public int ReadyCases { get; init; }
    public int BlockedCases { get; init; }
    public IReadOnlyList<LearningLayerBootstrapCase> Cases { get; init; } = Array.Empty<LearningLayerBootstrapCase>();
    public LearningLayerDatasetInventory DatasetInventory { get; init; } = new();
    public LearningLayerFeatureContract FeatureContract { get; init; } = new();
    public LearningLayerBaselinePlan BaselinePlan { get; init; } = new();
    public bool ShadowOnly { get; init; }
    public bool RuntimeAuthority { get; init; }
    public bool GateAuthority { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool V8ScopedActivationPreserved { get; init; }
    public bool UpstreamV8ScopedActivationCloseoutGatePresent { get; init; }
    public bool UpstreamV8ScopedActivationCloseoutGatePassed { get; init; }
    public bool MainlineEvidencePresent { get; init; }
    public bool MainlineTrustRegistryPresent { get; init; }
    public string Recommendation { get; init; } = string.Empty;
    public string NextAllowedPhase { get; init; } = string.Empty;
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class LearningLayerBootstrapOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
