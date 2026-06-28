using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

public static class LearningShadowImplementationPackStatuses
{
    public const string LearningShadowImplementationPackReady = nameof(LearningShadowImplementationPackReady);
    public const string LearningShadowImplementationPackBlocked = nameof(LearningShadowImplementationPackBlocked);
}

public static class LearningShadowImplementationPackBlockedReasons
{
    public const string BootstrapGateMissing = nameof(BootstrapGateMissing);
    public const string BootstrapGateNotPassed = nameof(BootstrapGateNotPassed);
    public const string RankingPairsMissing = nameof(RankingPairsMissing);
    public const string RouterIntentExamplesMissing = nameof(RouterIntentExamplesMissing);
    public const string ShadowOnlyFalse = nameof(ShadowOnlyFalse);
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

// Public feature-vector DTOs for baselines (consumed by the EvalCommand handler via the loader methods)
public sealed class RankerPair
{
    public string EvalSampleId { get; init; } = string.Empty;
    public string Query { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public string Intent { get; init; } = string.Empty;
    public string PositiveCandidateId { get; init; } = string.Empty;
    public string NegativeCandidateId { get; init; } = string.Empty;
    public double[] Features { get; init; } = Array.Empty<double>();  // positive - negative delta
}

public sealed class RouterExample
{
    public string ExampleId { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public string Intent { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public double[] Features { get; init; } = Array.Empty<double>();
}

public sealed class ShadowBaselineResult
{
    public string BaselineName { get; init; } = string.Empty;
    public string TaskFamily { get; init; } = string.Empty;
    public bool Ready { get; init; }
    public string NotReadyReason { get; init; } = string.Empty;
    public int TrainCount { get; init; }
    public int EvalCount { get; init; }
    public double Accuracy { get; init; }
    public double PairwiseAccuracy { get; init; }
    public double Loss { get; init; }
    public IReadOnlyList<string> TopFailures { get; init; } = Array.Empty<string>();
    public bool ShadowOnly { get; init; } = true;
    public bool RuntimeAuthority { get; init; }
    public bool GateAuthority { get; init; }
}

public sealed class HardNegativeCoverageReport
{
    public int HardNegativeCount { get; init; }
    public int CoveredEvalSamples { get; init; }
    public int TotalEvalSamples { get; init; }
    public double CoverageRate { get; init; }
    public bool HardNegativeTrainingReady { get; init; }
    public string NotReadyReason { get; init; } = string.Empty;
    public IReadOnlyList<string> Gaps { get; init; } = Array.Empty<string>();
    public bool ShadowOnly { get; init; } = true;
}

public sealed class ShadowComparisonSummary
{
    public string BestRankerCandidate { get; init; } = string.Empty;
    public double BestRankerPairwiseAccuracy { get; init; }
    public string BestRouterCandidate { get; init; } = string.Empty;
    public double BestRouterAccuracy { get; init; }
    public IReadOnlyList<string> ResidualFailureClusters { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> NextTrainingPriorities { get; init; } = Array.Empty<string>();
    public bool ShadowOnly { get; init; } = true;
    public bool RuntimeAuthority { get; init; }
    public bool GateAuthority { get; init; }
}

public sealed record LearningShadowImplementationPackContext
{
    public bool BootstrapGatePresent { get; init; }
    public bool BootstrapGatePassed { get; init; }
    public bool V8ScopedActivationPreserved { get; init; }
    public int RankingPairCount { get; init; }
    public int RouterExampleCount { get; init; }
    public int HardNegativeCount { get; init; }
    // Synthetic test knobs
    public bool ShadowOnlyOverride { get; init; } = true;
    public bool RuntimeAuthorityOverride { get; init; }
    public bool GateAuthorityOverride { get; init; }
    public bool RuntimeRerankerChangedOverride { get; init; }
    public bool RuntimeRouterChangedOverride { get; init; }
    public bool PackageOutputChangedOverride { get; init; }
    public bool FormalPackageWrittenOverride { get; init; }
    public bool GlobalDefaultOnOverride { get; init; }
}

public sealed class LearningShadowImplementationPackDecision
{
    public string Status { get; init; } = LearningShadowImplementationPackStatuses.LearningShadowImplementationPackBlocked;
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;
}

public static class LearningShadowImplementationPackPolicy
{
    public static LearningShadowImplementationPackDecision Evaluate(
        LearningShadowImplementationPackContext ctx,
        bool rtPassed, bool p15Passed,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent)
    {
        var blocked = new List<string>();
        if (!ctx.BootstrapGatePresent) blocked.Add(LearningShadowImplementationPackBlockedReasons.BootstrapGateMissing);
        else if (!ctx.BootstrapGatePassed) blocked.Add(LearningShadowImplementationPackBlockedReasons.BootstrapGateNotPassed);
        if (ctx.RankingPairCount <= 0) blocked.Add(LearningShadowImplementationPackBlockedReasons.RankingPairsMissing);
        if (ctx.RouterExampleCount <= 0) blocked.Add(LearningShadowImplementationPackBlockedReasons.RouterIntentExamplesMissing);
        if (!ctx.ShadowOnlyOverride) blocked.Add(LearningShadowImplementationPackBlockedReasons.ShadowOnlyFalse);
        if (ctx.RuntimeAuthorityOverride) blocked.Add(LearningShadowImplementationPackBlockedReasons.RuntimeAuthorityTrue);
        if (ctx.GateAuthorityOverride) blocked.Add(LearningShadowImplementationPackBlockedReasons.GateAuthorityTrue);
        if (ctx.RuntimeRerankerChangedOverride) blocked.Add(LearningShadowImplementationPackBlockedReasons.RuntimeRerankerChangedTrue);
        if (ctx.RuntimeRouterChangedOverride) blocked.Add(LearningShadowImplementationPackBlockedReasons.RuntimeRouterChangedTrue);
        if (ctx.PackageOutputChangedOverride) blocked.Add(LearningShadowImplementationPackBlockedReasons.PackageOutputChangedTrue);
        if (ctx.FormalPackageWrittenOverride) blocked.Add(LearningShadowImplementationPackBlockedReasons.FormalPackageWrittenTrue);
        if (ctx.GlobalDefaultOnOverride) blocked.Add(LearningShadowImplementationPackBlockedReasons.GlobalDefaultOnTrue);
        if (!ctx.V8ScopedActivationPreserved) blocked.Add(LearningShadowImplementationPackBlockedReasons.V8ScopedActivationLost);
        if (!rtPassed) blocked.Add(LearningShadowImplementationPackBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(LearningShadowImplementationPackBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(LearningShadowImplementationPackBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(LearningShadowImplementationPackBlockedReasons.MainlineTrustRegistryPresent);
        var finalBlocked = blocked.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        var ready = finalBlocked.Length == 0;
        return new LearningShadowImplementationPackDecision
        {
            Status = ready ? LearningShadowImplementationPackStatuses.LearningShadowImplementationPackReady : LearningShadowImplementationPackStatuses.LearningShadowImplementationPackBlocked,
            BlockedReasons = finalBlocked,
            Reasoning = ready
                ? "shadow implementation pack policy ready — all upstream and authority invariants satisfied; baselines may run shadow-only."
                : $"{finalBlocked.Length} blocked reason(s); shadow implementation pack blocked."
        };
    }
}

public sealed record LearningShadowImplementationPackScenario(
    string CaseName,
    LearningShadowImplementationPackContext Context,
    bool RtPassed, bool P15Passed,
    bool MainlineEvidencePresent, bool MainlineRegistryPresent,
    string ExpectedStatus, string? ExpectedBlockedReason);

public sealed class LearningShadowImplementationPackRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public LearningShadowImplementationPackReport Run(
        LearningShadowImplementationPackContext realContext,
        IReadOnlyList<RankerPair> rankerPairs,
        IReadOnlyList<RouterExample> routerExamples,
        int hardNegativeCount,
        string outputDir,
        bool rtPassed, bool p15Passed,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent,
        LearningShadowImplementationPackOptions? opt = null)
    {
        opt ??= new LearningShadowImplementationPackOptions();
        var now = DateTimeOffset.UtcNow;

        // ─── matrix
        var cases = BuildScenarios().Select(static scenario =>
        {
            var decision = LearningShadowImplementationPackPolicy.Evaluate(
                scenario.Context, scenario.RtPassed, scenario.P15Passed,
                scenario.MainlineEvidencePresent, scenario.MainlineRegistryPresent);
            var statusMatched = string.Equals(scenario.ExpectedStatus, decision.Status, StringComparison.Ordinal);
            var blockedReasonMatched = scenario.ExpectedBlockedReason is null
                || decision.BlockedReasons.Contains(scenario.ExpectedBlockedReason, StringComparer.Ordinal);
            return new LearningShadowImplementationPackCase
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
        if (cases.Length < 20) blocked.Add("InsufficientLearningShadowImplementationPackCases");
        if (cases.Any(static c => !c.PassedAsExpected)) blocked.Add("LearningShadowImplementationPackMatrixFailed");
        foreach (var status in new[] { LearningShadowImplementationPackStatuses.LearningShadowImplementationPackReady, LearningShadowImplementationPackStatuses.LearningShadowImplementationPackBlocked })
        {
            if (!cases.Any(c => string.Equals(c.ActualStatus, status, StringComparison.Ordinal)))
                blocked.Add($"StatusBranchNotCovered:{status}");
        }

        var realDecision = LearningShadowImplementationPackPolicy.Evaluate(realContext, rtPassed, p15Passed, mainlineEvidencePresent, mainlineRegistryPresent);
        if (!string.Equals(realDecision.Status, LearningShadowImplementationPackStatuses.LearningShadowImplementationPackReady, StringComparison.Ordinal))
            blocked.AddRange(realDecision.BlockedReasons.Select(static x => $"RealLearningShadowImplementationPack:{x}"));
        if (!rtPassed) blocked.Add(LearningShadowImplementationPackBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(LearningShadowImplementationPackBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(LearningShadowImplementationPackBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(LearningShadowImplementationPackBlockedReasons.MainlineTrustRegistryPresent);

        // ─── train baselines (only if real flow ready and rankerPairs non-empty)
        var rankerBaselines = new List<ShadowBaselineResult>();
        var routerBaselines = new List<ShadowBaselineResult>();
        var hardNegReport = new HardNegativeCoverageReport();
        var failureSamplePaths = new List<string>();
        ShadowComparisonSummary summary = new();
        var summaryPath = string.Empty;

        var canTrain = string.Equals(realDecision.Status, LearningShadowImplementationPackStatuses.LearningShadowImplementationPackReady, StringComparison.Ordinal);
        if (canTrain && rankerPairs.Count > 0 && routerExamples.Count > 0)
        {
            // deterministic 80/20 split by EvalSampleId hash
            var (rankerTrain, rankerEval) = DeterministicSplitRanker(rankerPairs);
            rankerBaselines.Add(TrainWeightedRanker(rankerTrain, rankerEval));
            rankerBaselines.Add(TrainLogisticRanker(rankerTrain, rankerEval));
            rankerBaselines.Add(TrainTreeRanker(rankerTrain, rankerEval));
            rankerBaselines.Add(BuildMlpReady(rankerTrain, rankerEval, outputDir));

            var (routerTrain, routerEval) = DeterministicSplitRouter(routerExamples);
            routerBaselines.Add(TrainLogisticRouter(routerTrain, routerEval));
            routerBaselines.Add(TrainTreeRouter(routerTrain, routerEval));

            hardNegReport = BuildHardNegativeCoverage(rankerPairs, hardNegativeCount);

            // failure export
            var failureDir = Path.Combine(outputDir, "failure-samples");
            Directory.CreateDirectory(failureDir);
            var rankerFailurePath = Path.Combine(failureDir, "candidate-reranker-failures.jsonl");
            ExportRankerFailures(rankerEval, rankerBaselines, rankerFailurePath);
            failureSamplePaths.Add(rankerFailurePath);
            var routerFailurePath = Path.Combine(failureDir, "router-intent-failures.jsonl");
            ExportRouterFailures(routerEval, routerBaselines, routerFailurePath);
            failureSamplePaths.Add(routerFailurePath);

            // shadow comparison summary
            summary = BuildShadowComparisonSummary(rankerBaselines, routerBaselines, hardNegReport);
            summaryPath = Path.Combine(outputDir, "shadow-comparison-summary.json");
            File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(true));
            var summaryMdPath = Path.Combine(outputDir, "shadow-comparison-summary.md");
            File.WriteAllText(summaryMdPath, BuildSummaryMarkdown(summary), new UTF8Encoding(true));
        }

        // verify shadow-only invariants on all baselines
        foreach (var b in rankerBaselines.Concat(routerBaselines))
        {
            if (!b.ShadowOnly) blocked.Add($"BaselineShadowOnlyFalse:{b.BaselineName}");
            if (b.RuntimeAuthority) blocked.Add($"BaselineRuntimeAuthority:{b.BaselineName}");
            if (b.GateAuthority) blocked.Add($"BaselineGateAuthority:{b.BaselineName}");
        }

        var finalBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var passed = finalBlocked.Length == 0;

        return new LearningShadowImplementationPackReport
        {
            OperationId = $"v9-learning-shadow-implementation-pack-{Guid.NewGuid():N}",
            CreatedAt = now,
            ShadowImplementationPackPassed = passed,
            GatePassed = opt.IsGate && passed,
            TotalCases = cases.Length,
            PassedCases = cases.Count(static c => c.PassedAsExpected),
            FailedCases = cases.Count(static c => !c.PassedAsExpected),
            ReadyCases = cases.Count(c => string.Equals(c.ActualStatus, LearningShadowImplementationPackStatuses.LearningShadowImplementationPackReady, StringComparison.Ordinal)),
            BlockedCases = cases.Count(c => string.Equals(c.ActualStatus, LearningShadowImplementationPackStatuses.LearningShadowImplementationPackBlocked, StringComparison.Ordinal)),
            Cases = cases,
            CandidateRerankerBaselines = rankerBaselines,
            RouterIntentBaselines = routerBaselines,
            HardNegativeCoverage = hardNegReport,
            ShadowComparisonSummary = summary,
            CandidateWeightedBaselineReady = rankerBaselines.Any(b => b.BaselineName == "WeightedBaseline" && b.Ready),
            CandidateLogisticBaselineReady = rankerBaselines.Any(b => b.BaselineName == "LogisticBaseline" && b.Ready),
            CandidateTreeBaselineReady = rankerBaselines.Any(b => b.BaselineName == "TreeBaseline" && b.Ready),
            CandidateMLPShadowReady = rankerBaselines.Any(b => b.BaselineName == "LightweightMLPShadowCandidate" && b.Ready),
            MLPDatasetExportReady = rankerBaselines.Any(b => b.BaselineName == "LightweightMLPShadowCandidate"),
            RouterIntentLogisticBaselineReady = routerBaselines.Any(b => b.BaselineName == "RouterIntentLogistic" && b.Ready),
            RouterIntentTreeBaselineReady = routerBaselines.Any(b => b.BaselineName == "RouterIntentTree" && b.Ready),
            HardNegativeCoverageReady = hardNegReport.HardNegativeCount > 0,
            FailureSampleExported = failureSamplePaths.Count >= 2,
            ShadowComparisonSummaryWritten = !string.IsNullOrEmpty(summaryPath) && File.Exists(summaryPath),
            FailureSamplePaths = failureSamplePaths,
            ShadowComparisonSummaryPath = summaryPath,
            ShadowOnly = true,
            RuntimeAuthority = false,
            GateAuthority = false,
            RuntimeRerankerChanged = false,
            RuntimeRouterChanged = false,
            PackageOutputChanged = false,
            FormalPackageWritten = false,
            GlobalDefaultOn = false,
            V8ScopedActivationPreserved = realContext.V8ScopedActivationPreserved,
            UpstreamLearningLayerBootstrapGatePresent = realContext.BootstrapGatePresent,
            UpstreamLearningLayerBootstrapGatePassed = realContext.BootstrapGatePassed,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent,
            Recommendation = passed ? "ProceedToV9.4FailureDiagnosisAndHardNegativeLoop" : "Blocked",
            NextAllowedPhase = passed ? "V9.4FailureDiagnosisAndHardNegativeLoop" : string.Empty,
            BlockedReasons = finalBlocked,
            Diagnostics = new[]
            {
                $"total={cases.Length}",
                $"realStatus={realDecision.Status}",
                $"rankerBaselineCount={rankerBaselines.Count}",
                $"routerBaselineCount={routerBaselines.Count}",
                $"rankerPairsLoaded={rankerPairs.Count}",
                $"routerExamplesLoaded={routerExamples.Count}",
                $"hardNegativeCount={hardNegativeCount}",
                $"hardNegativeCoverageRate={hardNegReport.CoverageRate:F3}"
            }
        };
    }

    private static IReadOnlyList<LearningShadowImplementationPackScenario> BuildScenarios()
    {
        var clean = BuildCleanContext();
        return [
            new("AllUpstreamClean", clean, true, true, false, false, LearningShadowImplementationPackStatuses.LearningShadowImplementationPackReady, null),
            new("BootstrapGateMissing", clean with { BootstrapGatePresent = false }, true, true, false, false, LearningShadowImplementationPackStatuses.LearningShadowImplementationPackBlocked, LearningShadowImplementationPackBlockedReasons.BootstrapGateMissing),
            new("BootstrapGateNotPassed", clean with { BootstrapGatePassed = false }, true, true, false, false, LearningShadowImplementationPackStatuses.LearningShadowImplementationPackBlocked, LearningShadowImplementationPackBlockedReasons.BootstrapGateNotPassed),
            new("RankingPairsMissing", clean with { RankingPairCount = 0 }, true, true, false, false, LearningShadowImplementationPackStatuses.LearningShadowImplementationPackBlocked, LearningShadowImplementationPackBlockedReasons.RankingPairsMissing),
            new("RouterIntentExamplesMissing", clean with { RouterExampleCount = 0 }, true, true, false, false, LearningShadowImplementationPackStatuses.LearningShadowImplementationPackBlocked, LearningShadowImplementationPackBlockedReasons.RouterIntentExamplesMissing),
            new("ShadowOnlyFalse", clean with { ShadowOnlyOverride = false }, true, true, false, false, LearningShadowImplementationPackStatuses.LearningShadowImplementationPackBlocked, LearningShadowImplementationPackBlockedReasons.ShadowOnlyFalse),
            new("RuntimeAuthorityTrue", clean with { RuntimeAuthorityOverride = true }, true, true, false, false, LearningShadowImplementationPackStatuses.LearningShadowImplementationPackBlocked, LearningShadowImplementationPackBlockedReasons.RuntimeAuthorityTrue),
            new("GateAuthorityTrue", clean with { GateAuthorityOverride = true }, true, true, false, false, LearningShadowImplementationPackStatuses.LearningShadowImplementationPackBlocked, LearningShadowImplementationPackBlockedReasons.GateAuthorityTrue),
            new("RuntimeRerankerChangedTrue", clean with { RuntimeRerankerChangedOverride = true }, true, true, false, false, LearningShadowImplementationPackStatuses.LearningShadowImplementationPackBlocked, LearningShadowImplementationPackBlockedReasons.RuntimeRerankerChangedTrue),
            new("RuntimeRouterChangedTrue", clean with { RuntimeRouterChangedOverride = true }, true, true, false, false, LearningShadowImplementationPackStatuses.LearningShadowImplementationPackBlocked, LearningShadowImplementationPackBlockedReasons.RuntimeRouterChangedTrue),
            new("PackageOutputChangedTrue", clean with { PackageOutputChangedOverride = true }, true, true, false, false, LearningShadowImplementationPackStatuses.LearningShadowImplementationPackBlocked, LearningShadowImplementationPackBlockedReasons.PackageOutputChangedTrue),
            new("FormalPackageWrittenTrue", clean with { FormalPackageWrittenOverride = true }, true, true, false, false, LearningShadowImplementationPackStatuses.LearningShadowImplementationPackBlocked, LearningShadowImplementationPackBlockedReasons.FormalPackageWrittenTrue),
            new("GlobalDefaultOnTrue", clean with { GlobalDefaultOnOverride = true }, true, true, false, false, LearningShadowImplementationPackStatuses.LearningShadowImplementationPackBlocked, LearningShadowImplementationPackBlockedReasons.GlobalDefaultOnTrue),
            new("V8ScopedActivationLost", clean with { V8ScopedActivationPreserved = false }, true, true, false, false, LearningShadowImplementationPackStatuses.LearningShadowImplementationPackBlocked, LearningShadowImplementationPackBlockedReasons.V8ScopedActivationLost),
            new("RuntimeGateNotPassed", clean, false, true, false, false, LearningShadowImplementationPackStatuses.LearningShadowImplementationPackBlocked, LearningShadowImplementationPackBlockedReasons.RuntimeChangeGateNotPassed),
            new("P15GateNotPassed", clean, true, false, false, false, LearningShadowImplementationPackStatuses.LearningShadowImplementationPackBlocked, LearningShadowImplementationPackBlockedReasons.P15GateNotPassed),
            new("MainlineEvidencePresent", clean, true, true, true, false, LearningShadowImplementationPackStatuses.LearningShadowImplementationPackBlocked, LearningShadowImplementationPackBlockedReasons.MainlineEvidencePresent),
            new("MainlineTrustRegistryPresent", clean, true, true, false, true, LearningShadowImplementationPackStatuses.LearningShadowImplementationPackBlocked, LearningShadowImplementationPackBlockedReasons.MainlineTrustRegistryPresent),
            new("BootstrapGateAndPairsMissing", clean with { BootstrapGatePresent = false, RankingPairCount = 0 }, true, true, false, false, LearningShadowImplementationPackStatuses.LearningShadowImplementationPackBlocked, LearningShadowImplementationPackBlockedReasons.RankingPairsMissing),
            new("AuthorityAndRuntimeRerankerBoth", clean with { RuntimeAuthorityOverride = true, RuntimeRerankerChangedOverride = true }, true, true, false, false, LearningShadowImplementationPackStatuses.LearningShadowImplementationPackBlocked, LearningShadowImplementationPackBlockedReasons.RuntimeRerankerChangedTrue),
            new("AllSafetyLeaksAtOnce", clean with { ShadowOnlyOverride = false, RuntimeAuthorityOverride = true, GateAuthorityOverride = true, PackageOutputChangedOverride = true, FormalPackageWrittenOverride = true, GlobalDefaultOnOverride = true }, true, true, false, false, LearningShadowImplementationPackStatuses.LearningShadowImplementationPackBlocked, LearningShadowImplementationPackBlockedReasons.ShadowOnlyFalse)
        ];
    }

    private static LearningShadowImplementationPackContext BuildCleanContext() => new()
    {
        BootstrapGatePresent = true,
        BootstrapGatePassed = true,
        V8ScopedActivationPreserved = true,
        RankingPairCount = 253,
        RouterExampleCount = 163,
        HardNegativeCount = 18,
        ShadowOnlyOverride = true
    };

    // ─── Determinism helpers ──────────────────────────────────────────────────

    private static int GroupHash(string key)
    {
        // FNV-1a 32-bit
        const int prime = 16777619;
        int hash = unchecked((int)2166136261);
        foreach (var c in key) { hash = (hash ^ c) * prime; }
        return hash & 0x7FFFFFFF;
    }

    private static (List<RankerPair> train, List<RankerPair> eval) DeterministicSplitRanker(IReadOnlyList<RankerPair> pairs)
    {
        // 80/20 by EvalSampleId
        var sorted = pairs.OrderBy(p => p.EvalSampleId, StringComparer.Ordinal).ThenBy(p => p.PositiveCandidateId, StringComparer.Ordinal).ThenBy(p => p.NegativeCandidateId, StringComparer.Ordinal).ToList();
        var train = new List<RankerPair>();
        var eval = new List<RankerPair>();
        foreach (var p in sorted)
        {
            if (GroupHash(p.EvalSampleId) % 5 == 0) eval.Add(p); else train.Add(p);
        }
        return (train, eval);
    }

    private static (List<RouterExample> train, List<RouterExample> eval) DeterministicSplitRouter(IReadOnlyList<RouterExample> examples)
    {
        var sorted = examples.OrderBy(e => e.ExampleId, StringComparer.Ordinal).ToList();
        var train = new List<RouterExample>();
        var eval = new List<RouterExample>();
        foreach (var e in sorted)
        {
            if (GroupHash(e.ExampleId) % 5 == 0) eval.Add(e); else train.Add(e);
        }
        return (train, eval);
    }

    // ─── Ranker baselines (pairwise: predict positive_score > negative_score) ─

    /// <summary>WeightedBaseline: deterministic weighted sum of feature deltas. Predicts positive wins if delta-sum > 0.</summary>
    private static ShadowBaselineResult TrainWeightedRanker(IReadOnlyList<RankerPair> train, IReadOnlyList<RankerPair> eval)
    {
        // Fixed hand-tuned weights (no training): rely on positiveScore delta + recall + mrr signal
        // Feature indices: 0=recall3, 1=recall5, 2=recall10, 3=mrr, 4=positiveRank-neg(inverted), 5=positiveScore-neg, 6=positiveSelected-neg, 7=packageHasAllConstraints
        var weights = new[] { 0.5, 0.5, 0.5, 1.0, 0.2, 1.0, 0.5, 0.3 };
        var failures = new List<string>();
        int correct = 0;
        foreach (var p in eval)
        {
            var score = ScoreLinear(weights, p.Features);
            if (score > 0) correct++;
            else failures.Add($"{p.EvalSampleId}:{p.PositiveCandidateId}->{p.NegativeCandidateId} score={score:F3}");
        }
        var accuracy = eval.Count > 0 ? (double)correct / eval.Count : 0.0;
        return new ShadowBaselineResult
        {
            BaselineName = "WeightedBaseline",
            TaskFamily = "CandidateReranker",
            Ready = true,
            TrainCount = train.Count,
            EvalCount = eval.Count,
            Accuracy = accuracy,
            PairwiseAccuracy = accuracy,
            Loss = 0.0,
            TopFailures = failures.Take(10).ToArray()
        };
    }

    /// <summary>LogisticBaseline: pairwise logistic regression with deterministic gradient descent. Loss = -log(sigmoid(w·x)) over delta features.</summary>
    private static ShadowBaselineResult TrainLogisticRanker(IReadOnlyList<RankerPair> train, IReadOnlyList<RankerPair> eval)
    {
        var dim = train.Count > 0 ? train[0].Features.Length : 0;
        if (dim == 0)
        {
            return new ShadowBaselineResult { BaselineName = "LogisticBaseline", TaskFamily = "CandidateReranker", Ready = false, NotReadyReason = "no features available", TrainCount = train.Count, EvalCount = eval.Count };
        }
        var weights = new double[dim];
        const double lr = 0.05;
        const int iters = 200;
        var sortedTrain = train.OrderBy(p => p.EvalSampleId, StringComparer.Ordinal).ToList();
        for (int it = 0; it < iters; it++)
        {
            var grad = new double[dim];
            foreach (var p in sortedTrain)
            {
                var z = ScoreLinear(weights, p.Features);
                var sig = Sigmoid(z);
                // target label = 1 (positive wins). loss = -log(sigmoid(z)). gradient = -(1-sig)*x
                var coef = -(1.0 - sig);
                for (int i = 0; i < dim; i++) grad[i] += coef * p.Features[i];
            }
            for (int i = 0; i < dim; i++) weights[i] -= lr * grad[i] / Math.Max(1, sortedTrain.Count);
        }
        var failures = new List<string>();
        int correct = 0;
        double loss = 0;
        foreach (var p in eval)
        {
            var z = ScoreLinear(weights, p.Features);
            if (z > 0) correct++;
            else failures.Add($"{p.EvalSampleId}:{p.PositiveCandidateId}->{p.NegativeCandidateId} z={z:F3}");
            loss += -Math.Log(Math.Max(1e-9, Sigmoid(z)));
        }
        var accuracy = eval.Count > 0 ? (double)correct / eval.Count : 0.0;
        return new ShadowBaselineResult
        {
            BaselineName = "LogisticBaseline",
            TaskFamily = "CandidateReranker",
            Ready = true,
            TrainCount = train.Count,
            EvalCount = eval.Count,
            Accuracy = accuracy,
            PairwiseAccuracy = accuracy,
            Loss = eval.Count > 0 ? loss / eval.Count : 0.0,
            TopFailures = failures.Take(10).ToArray()
        };
    }

    /// <summary>TreeBaseline: deterministic boosted stumps. Each stump picks the feature with the best pairwise accuracy gain on residuals.</summary>
    private static ShadowBaselineResult TrainTreeRanker(IReadOnlyList<RankerPair> train, IReadOnlyList<RankerPair> eval)
    {
        var dim = train.Count > 0 ? train[0].Features.Length : 0;
        if (dim == 0)
        {
            return new ShadowBaselineResult { BaselineName = "TreeBaseline", TaskFamily = "CandidateReranker", Ready = false, NotReadyReason = "no features available", TrainCount = train.Count, EvalCount = eval.Count };
        }
        // Boosted stumps: 5 stumps. Each stump = (featureIdx, threshold, leftWeight, rightWeight)
        const int numStumps = 5;
        var stumps = new List<(int feat, double thresh, double left, double right)>();
        var residuals = train.Select(p => 1.0).ToArray();  // start: all want positive prediction
        for (int s = 0; s < numStumps; s++)
        {
            int bestFeat = 0;
            double bestThresh = 0;
            double bestLeft = 0, bestRight = 0;
            double bestGain = -1;
            // try each feature, with threshold = median of training deltas
            for (int f = 0; f < dim; f++)
            {
                var values = train.Select(p => p.Features[f]).OrderBy(v => v).ToArray();
                var thresh = values[values.Length / 2];
                double leftSum = 0, leftCount = 0, rightSum = 0, rightCount = 0;
                for (int i = 0; i < train.Count; i++)
                {
                    if (train[i].Features[f] <= thresh) { leftSum += residuals[i]; leftCount++; }
                    else { rightSum += residuals[i]; rightCount++; }
                }
                if (leftCount == 0 || rightCount == 0) continue;
                double left = leftSum / leftCount;
                double right = rightSum / rightCount;
                double gain = Math.Abs(right - left);
                if (gain > bestGain) { bestGain = gain; bestFeat = f; bestThresh = thresh; bestLeft = left; bestRight = right; }
            }
            stumps.Add((bestFeat, bestThresh, bestLeft, bestRight));
            // update residuals: subtract 0.5 * (predicted value)
            for (int i = 0; i < train.Count; i++)
            {
                double pred = train[i].Features[bestFeat] <= bestThresh ? bestLeft : bestRight;
                residuals[i] -= 0.5 * pred;
            }
        }
        var failures = new List<string>();
        int correct = 0;
        foreach (var p in eval)
        {
            double score = stumps.Sum(s => p.Features[s.feat] <= s.thresh ? s.left : s.right);
            if (score > 0) correct++;
            else failures.Add($"{p.EvalSampleId}:{p.PositiveCandidateId}->{p.NegativeCandidateId} treeScore={score:F3}");
        }
        var accuracy = eval.Count > 0 ? (double)correct / eval.Count : 0.0;
        return new ShadowBaselineResult
        {
            BaselineName = "TreeBaseline",
            TaskFamily = "CandidateReranker",
            Ready = true,
            TrainCount = train.Count,
            EvalCount = eval.Count,
            Accuracy = accuracy,
            PairwiseAccuracy = accuracy,
            Loss = 0,
            TopFailures = failures.Take(10).ToArray()
        };
    }

    /// <summary>MLP-ready: exports feature vectors to disk for downstream training. Reports NotReadyWithReason — full MLP training deferred.</summary>
    private static ShadowBaselineResult BuildMlpReady(IReadOnlyList<RankerPair> train, IReadOnlyList<RankerPair> eval, string outputDir)
    {
        var datasetDir = Path.Combine(outputDir, "mlp-dataset");
        Directory.CreateDirectory(datasetDir);
        var trainPath = Path.Combine(datasetDir, "candidate-reranker-mlp-train.jsonl");
        var evalPath = Path.Combine(datasetDir, "candidate-reranker-mlp-eval.jsonl");
        WriteFeatureJsonl(trainPath, train);
        WriteFeatureJsonl(evalPath, eval);
        return new ShadowBaselineResult
        {
            BaselineName = "LightweightMLPShadowCandidate",
            TaskFamily = "CandidateReranker",
            Ready = false,
            NotReadyReason = "MLP training deferred to V9.3 dedicated phase to avoid pulling heavy ML dependency. Dataset exported in jsonl form for downstream training.",
            TrainCount = train.Count,
            EvalCount = eval.Count
        };
    }

    private static void WriteFeatureJsonl(string path, IReadOnlyList<RankerPair> pairs)
    {
        var sb = new StringBuilder();
        foreach (var p in pairs)
        {
            sb.Append('{');
            sb.Append("\"evalSampleId\":\"").Append(p.EvalSampleId).Append("\",");
            sb.Append("\"label\":1,");
            sb.Append("\"features\":[");
            for (int i = 0; i < p.Features.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(p.Features[i].ToString("R", CultureInfo.InvariantCulture));
            }
            sb.Append("]}");
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    // ─── Router baselines (multi-class intent classification) ──────────────────

    /// <summary>RouterIntent Logistic: one-vs-rest binary logistic regression per intent class.</summary>
    private static ShadowBaselineResult TrainLogisticRouter(IReadOnlyList<RouterExample> train, IReadOnlyList<RouterExample> eval)
    {
        var classes = train.Select(e => e.Label).Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        if (classes.Length < 2 || train.Count == 0)
        {
            return new ShadowBaselineResult { BaselineName = "RouterIntentLogistic", TaskFamily = "RouterIntentClassifier", Ready = false, NotReadyReason = $"insufficient classes ({classes.Length}) or training set ({train.Count})", TrainCount = train.Count, EvalCount = eval.Count };
        }
        var dim = train[0].Features.Length;
        var weights = new Dictionary<string, double[]>(StringComparer.Ordinal);
        const double lr = 0.05;
        const int iters = 200;
        foreach (var cls in classes)
        {
            var w = new double[dim];
            var sortedTrain = train.OrderBy(e => e.ExampleId, StringComparer.Ordinal).ToList();
            for (int it = 0; it < iters; it++)
            {
                var grad = new double[dim];
                foreach (var e in sortedTrain)
                {
                    var y = string.Equals(e.Label, cls, StringComparison.Ordinal) ? 1.0 : 0.0;
                    var z = ScoreLinear(w, e.Features);
                    var sig = Sigmoid(z);
                    var coef = sig - y;
                    for (int i = 0; i < dim; i++) grad[i] += coef * e.Features[i];
                }
                for (int i = 0; i < dim; i++) w[i] -= lr * grad[i] / Math.Max(1, sortedTrain.Count);
            }
            weights[cls] = w;
        }
        var failures = new List<string>();
        int correct = 0;
        foreach (var e in eval)
        {
            string best = classes[0];
            double bestScore = double.NegativeInfinity;
            foreach (var cls in classes)
            {
                var z = ScoreLinear(weights[cls], e.Features);
                if (z > bestScore) { bestScore = z; best = cls; }
            }
            if (string.Equals(best, e.Label, StringComparison.Ordinal)) correct++;
            else failures.Add($"{e.ExampleId} expected={e.Label} predicted={best} score={bestScore:F3}");
        }
        var accuracy = eval.Count > 0 ? (double)correct / eval.Count : 0.0;
        return new ShadowBaselineResult
        {
            BaselineName = "RouterIntentLogistic",
            TaskFamily = "RouterIntentClassifier",
            Ready = true,
            TrainCount = train.Count,
            EvalCount = eval.Count,
            Accuracy = accuracy,
            PairwiseAccuracy = 0,
            Loss = 0,
            TopFailures = failures.Take(10).ToArray()
        };
    }

    /// <summary>RouterIntent Tree: per-class decision stumps; predict argmax.</summary>
    private static ShadowBaselineResult TrainTreeRouter(IReadOnlyList<RouterExample> train, IReadOnlyList<RouterExample> eval)
    {
        var classes = train.Select(e => e.Label).Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        if (classes.Length < 2 || train.Count == 0)
        {
            return new ShadowBaselineResult { BaselineName = "RouterIntentTree", TaskFamily = "RouterIntentClassifier", Ready = false, NotReadyReason = $"insufficient classes ({classes.Length}) or training set ({train.Count})", TrainCount = train.Count, EvalCount = eval.Count };
        }
        var dim = train[0].Features.Length;
        // per-class single stump: best feature/threshold maximizing accuracy on that class
        var perClass = new Dictionary<string, (int feat, double thresh, double pos, double neg)>(StringComparer.Ordinal);
        foreach (var cls in classes)
        {
            int bestFeat = 0;
            double bestThresh = 0;
            double bestGain = -1;
            double bestPos = 0, bestNeg = 0;
            for (int f = 0; f < dim; f++)
            {
                var values = train.Select(e => e.Features[f]).OrderBy(v => v).ToArray();
                if (values.Length == 0) continue;
                var thresh = values[values.Length / 2];
                double leftPos = 0, leftCount = 0, rightPos = 0, rightCount = 0;
                foreach (var e in train)
                {
                    bool isClass = string.Equals(e.Label, cls, StringComparison.Ordinal);
                    if (e.Features[f] <= thresh) { if (isClass) leftPos++; leftCount++; }
                    else { if (isClass) rightPos++; rightCount++; }
                }
                double leftRate = leftCount > 0 ? leftPos / leftCount : 0;
                double rightRate = rightCount > 0 ? rightPos / rightCount : 0;
                double gain = Math.Abs(rightRate - leftRate);
                if (gain > bestGain) { bestGain = gain; bestFeat = f; bestThresh = thresh; bestPos = Math.Max(leftRate, rightRate); bestNeg = Math.Min(leftRate, rightRate); }
            }
            perClass[cls] = (bestFeat, bestThresh, bestPos, bestNeg);
        }
        var failures = new List<string>();
        int correct = 0;
        foreach (var e in eval)
        {
            string best = classes[0];
            double bestScore = double.NegativeInfinity;
            foreach (var cls in classes)
            {
                var (feat, thresh, pos, neg) = perClass[cls];
                double score = e.Features[feat] <= thresh ? (pos >= neg ? pos : neg) : (pos >= neg ? neg : pos);
                if (score > bestScore) { bestScore = score; best = cls; }
            }
            if (string.Equals(best, e.Label, StringComparison.Ordinal)) correct++;
            else failures.Add($"{e.ExampleId} expected={e.Label} predicted={best}");
        }
        var accuracy = eval.Count > 0 ? (double)correct / eval.Count : 0.0;
        return new ShadowBaselineResult
        {
            BaselineName = "RouterIntentTree",
            TaskFamily = "RouterIntentClassifier",
            Ready = true,
            TrainCount = train.Count,
            EvalCount = eval.Count,
            Accuracy = accuracy,
            PairwiseAccuracy = 0,
            Loss = 0,
            TopFailures = failures.Take(10).ToArray()
        };
    }

    // ─── Hard negative + failure export + comparison ──────────────────────────

    private static HardNegativeCoverageReport BuildHardNegativeCoverage(IReadOnlyList<RankerPair> pairs, int hardNegativeCount)
    {
        var evalSamples = pairs.Select(p => p.EvalSampleId).Distinct(StringComparer.Ordinal).ToArray();
        // proxy: assume each hard negative covers one eval sample (data we have is line-counted only)
        var covered = Math.Min(hardNegativeCount, evalSamples.Length);
        var coverage = evalSamples.Length > 0 ? (double)covered / evalSamples.Length : 0.0;
        var gaps = new List<string>();
        if (hardNegativeCount < 50) gaps.Add("low hard-negative count (<50); likely under-coverage on rare failure modes");
        if (coverage < 0.3) gaps.Add($"coverage rate {coverage:F2} below 0.30 threshold");
        return new HardNegativeCoverageReport
        {
            HardNegativeCount = hardNegativeCount,
            CoveredEvalSamples = covered,
            TotalEvalSamples = evalSamples.Length,
            CoverageRate = coverage,
            HardNegativeTrainingReady = hardNegativeCount >= 50 && coverage >= 0.3,
            NotReadyReason = hardNegativeCount < 50 ? "hard-negative count below 50; awaiting V9.4 hard-negative-generation expansion" : string.Empty,
            Gaps = gaps,
            ShadowOnly = true
        };
    }

    private static void ExportRankerFailures(IReadOnlyList<RankerPair> eval, IReadOnlyList<ShadowBaselineResult> baselines, string path)
    {
        var sb = new StringBuilder();
        foreach (var b in baselines.Where(x => x.Ready))
        {
            foreach (var failure in b.TopFailures)
            {
                sb.Append('{');
                sb.Append("\"baseline\":\"").Append(b.BaselineName).Append("\",");
                sb.Append("\"failure\":\"").Append(failure.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
                sb.Append('}');
                sb.AppendLine();
            }
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static void ExportRouterFailures(IReadOnlyList<RouterExample> eval, IReadOnlyList<ShadowBaselineResult> baselines, string path)
    {
        var sb = new StringBuilder();
        foreach (var b in baselines.Where(x => x.Ready))
        {
            foreach (var failure in b.TopFailures)
            {
                sb.Append('{');
                sb.Append("\"baseline\":\"").Append(b.BaselineName).Append("\",");
                sb.Append("\"failure\":\"").Append(failure.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
                sb.Append('}');
                sb.AppendLine();
            }
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static ShadowComparisonSummary BuildShadowComparisonSummary(
        IReadOnlyList<ShadowBaselineResult> rankerBaselines,
        IReadOnlyList<ShadowBaselineResult> routerBaselines,
        HardNegativeCoverageReport hn)
    {
        var bestRanker = rankerBaselines.Where(b => b.Ready).OrderByDescending(b => b.PairwiseAccuracy).FirstOrDefault();
        var bestRouter = routerBaselines.Where(b => b.Ready).OrderByDescending(b => b.Accuracy).FirstOrDefault();
        var residual = new List<string>();
        foreach (var b in rankerBaselines.Where(b => b.Ready && b.TopFailures.Count > 0))
            residual.Add($"{b.BaselineName}: {b.TopFailures.Count} top failures (acc={b.PairwiseAccuracy:F3})");
        foreach (var b in routerBaselines.Where(b => b.Ready && b.TopFailures.Count > 0))
            residual.Add($"{b.BaselineName}: {b.TopFailures.Count} top failures (acc={b.Accuracy:F3})");

        var priorities = new List<string>();
        if (hn.HardNegativeCount < 50) priorities.Add("Expand hard-negative dataset to 50+ samples (V9.4 LLM-assisted generation)");
        if (rankerBaselines.Any(b => b.BaselineName == "LightweightMLPShadowCandidate" && !b.Ready)) priorities.Add("Train full MLP on exported dataset (V9.3 dedicated)");
        if (bestRanker?.PairwiseAccuracy < 0.95) priorities.Add("Improve top ranker baseline pairwiseAccuracy beyond 0.95");
        if (bestRouter?.Accuracy < 0.85) priorities.Add("Improve top router baseline accuracy beyond 0.85");
        priorities.Add("Run V9.4 LLM-assisted failure diagnosis on exported failure samples");

        return new ShadowComparisonSummary
        {
            BestRankerCandidate = bestRanker?.BaselineName ?? "none",
            BestRankerPairwiseAccuracy = bestRanker?.PairwiseAccuracy ?? 0,
            BestRouterCandidate = bestRouter?.BaselineName ?? "none",
            BestRouterAccuracy = bestRouter?.Accuracy ?? 0,
            ResidualFailureClusters = residual,
            NextTrainingPriorities = priorities,
            ShadowOnly = true,
            RuntimeAuthority = false,
            GateAuthority = false
        };
    }

    private static string BuildSummaryMarkdown(ShadowComparisonSummary s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# V9.1-V9.3 Shadow Comparison Summary");
        sb.AppendLine();
        sb.AppendLine($"- BestRankerCandidate: `{s.BestRankerCandidate}` (pairwiseAccuracy={s.BestRankerPairwiseAccuracy:F3})");
        sb.AppendLine($"- BestRouterCandidate: `{s.BestRouterCandidate}` (accuracy={s.BestRouterAccuracy:F3})");
        sb.AppendLine($"- ShadowOnly: `{s.ShadowOnly}` RuntimeAuthority: `{s.RuntimeAuthority}` GateAuthority: `{s.GateAuthority}`");
        sb.AppendLine();
        sb.AppendLine("## Residual Failure Clusters");
        foreach (var r in s.ResidualFailureClusters) sb.AppendLine($"- {r}");
        sb.AppendLine();
        sb.AppendLine("## Next Training Priorities");
        foreach (var p in s.NextTrainingPriorities) sb.AppendLine($"- {p}");
        return sb.ToString();
    }

    // ─── Loaders ────────────────────────────────────────────────────────────────

    /// <summary>V9.1-V9.3: load real ranking pairs from disk, extracting deterministic feature deltas.</summary>
    public static IReadOnlyList<RankerPair> LoadRankerPairs(string path)
    {
        if (!File.Exists(path)) return Array.Empty<RankerPair>();
        var result = new List<RankerPair>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var fs = root.GetProperty("featureSnapshot");
                double Recall(string key) => fs.TryGetProperty(key, out var v) ? ParseDouble(v.GetString() ?? "0") : 0;
                double Bool(string key) => fs.TryGetProperty(key, out var v) && bool.TryParse(v.GetString() ?? "false", out var b) && b ? 1.0 : 0.0;
                double Num(string key) => fs.TryGetProperty(key, out var v) ? ParseDouble(v.GetString() ?? "0") : 0;
                var posRank = Num("positiveRank");
                var negRank = Num("negativeRank");
                var rankDelta = negRank > 0 ? (1.0 / posRank - 1.0 / negRank) : (posRank > 0 ? 1.0 / posRank : 0);
                var features = new[]
                {
                    Recall("recall3"),
                    Recall("recall5"),
                    Recall("recall10"),
                    Recall("mrr"),
                    rankDelta,
                    Num("positiveScore") - Num("negativeScore"),
                    Bool("positiveSelected") - Bool("negativeSelected"),
                    Bool("packageHasAllConstraints")
                };
                result.Add(new RankerPair
                {
                    EvalSampleId = root.TryGetProperty("evalSampleId", out var es) ? es.GetString() ?? string.Empty : string.Empty,
                    Query = root.TryGetProperty("query", out var q) ? q.GetString() ?? string.Empty : string.Empty,
                    Mode = root.TryGetProperty("mode", out var m) ? m.GetString() ?? string.Empty : string.Empty,
                    Intent = root.TryGetProperty("intent", out var i) ? i.GetString() ?? string.Empty : string.Empty,
                    PositiveCandidateId = root.TryGetProperty("positiveCandidateId", out var pc) ? pc.GetString() ?? string.Empty : string.Empty,
                    NegativeCandidateId = root.TryGetProperty("negativeCandidateId", out var nc) ? nc.GetString() ?? string.Empty : string.Empty,
                    Features = features
                });
            }
            catch { /* skip malformed lines */ }
        }
        return result;
    }

    /// <summary>V9.1-V9.3: load real router intent examples from disk.</summary>
    public static IReadOnlyList<RouterExample> LoadRouterExamples(string path)
    {
        if (!File.Exists(path)) return Array.Empty<RouterExample>();
        var result = new List<RouterExample>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                double Num(string key) => root.TryGetProperty(key, out var v) ? (v.ValueKind == JsonValueKind.Number ? v.GetDouble() : ParseDouble(v.GetString() ?? "0")) : 0;
                double Bool(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True ? 1.0 : 0.0;
                var features = new[]
                {
                    Num("candidateImportance"),
                    Num("candidateRecency"),
                    Num("keywordMatchScore"),
                    Num("semanticAnchorMatchScore"),
                    Num("shortTermMatchScore"),
                    Num("stableMatchScore"),
                    Num("constraintMatchScore"),
                    Num("lifecycleRisk"),
                    Num("relationPathCount"),
                    Bool("selected"),
                    Bool("accepted")
                };
                result.Add(new RouterExample
                {
                    ExampleId = root.TryGetProperty("exampleId", out var ei) ? ei.GetString() ?? string.Empty : string.Empty,
                    Mode = root.TryGetProperty("mode", out var m) ? m.GetString() ?? string.Empty : string.Empty,
                    Intent = root.TryGetProperty("intent", out var i) ? i.GetString() ?? string.Empty : string.Empty,
                    Label = root.TryGetProperty("label", out var lb) ? lb.GetString() ?? string.Empty : string.Empty,
                    Features = features
                });
            }
            catch { /* skip malformed lines */ }
        }
        return result;
    }

    private static double ParseDouble(string s) => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
    private static double ScoreLinear(double[] weights, double[] features)
    {
        double s = 0;
        for (int i = 0; i < Math.Min(weights.Length, features.Length); i++) s += weights[i] * features[i];
        return s;
    }
    private static double Sigmoid(double z) => 1.0 / (1.0 + Math.Exp(-z));

    public static string BuildMarkdown(string title, LearningShadowImplementationPackReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"- ShadowImplementationPackPassed: `{report.ShadowImplementationPackPassed}`");
        sb.AppendLine($"- GatePassed: `{report.GatePassed}`");
        sb.AppendLine($"- TotalCases: `{report.TotalCases}` PassedCases: `{report.PassedCases}` FailedCases: `{report.FailedCases}`");
        sb.AppendLine($"- ReadyCases: `{report.ReadyCases}` BlockedCases: `{report.BlockedCases}`");
        sb.AppendLine();
        sb.AppendLine("## Authority Invariants");
        sb.AppendLine($"- ShadowOnly: `{report.ShadowOnly}` RuntimeAuthority: `{report.RuntimeAuthority}` GateAuthority: `{report.GateAuthority}`");
        sb.AppendLine($"- RuntimeRerankerChanged: `{report.RuntimeRerankerChanged}` RuntimeRouterChanged: `{report.RuntimeRouterChanged}`");
        sb.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}` FormalPackageWritten: `{report.FormalPackageWritten}` GlobalDefaultOn: `{report.GlobalDefaultOn}`");
        sb.AppendLine($"- V8ScopedActivationPreserved: `{report.V8ScopedActivationPreserved}`");
        sb.AppendLine();
        sb.AppendLine("## Candidate Reranker Baselines");
        foreach (var b in report.CandidateRerankerBaselines)
            sb.AppendLine($"- `{b.BaselineName}` ready={b.Ready} train={b.TrainCount} eval={b.EvalCount} pairwiseAcc={b.PairwiseAccuracy:F3} {(b.Ready ? string.Empty : "(reason: " + b.NotReadyReason + ")")}");
        sb.AppendLine();
        sb.AppendLine("## Router Intent Baselines");
        foreach (var b in report.RouterIntentBaselines)
            sb.AppendLine($"- `{b.BaselineName}` ready={b.Ready} train={b.TrainCount} eval={b.EvalCount} acc={b.Accuracy:F3} {(b.Ready ? string.Empty : "(reason: " + b.NotReadyReason + ")")}");
        sb.AppendLine();
        sb.AppendLine("## Hard Negative Coverage");
        sb.AppendLine($"- HardNegativeCount: `{report.HardNegativeCoverage.HardNegativeCount}`");
        sb.AppendLine($"- CoverageRate: `{report.HardNegativeCoverage.CoverageRate:F3}`");
        sb.AppendLine($"- HardNegativeTrainingReady: `{report.HardNegativeCoverage.HardNegativeTrainingReady}`");
        if (!string.IsNullOrWhiteSpace(report.HardNegativeCoverage.NotReadyReason))
            sb.AppendLine($"- NotReadyReason: `{report.HardNegativeCoverage.NotReadyReason}`");
        sb.AppendLine();
        sb.AppendLine("## Outputs");
        sb.AppendLine($"- FailureSampleExported: `{report.FailureSampleExported}` ({report.FailureSamplePaths.Count} files)");
        sb.AppendLine($"- ShadowComparisonSummaryWritten: `{report.ShadowComparisonSummaryWritten}`");
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

public sealed class LearningShadowImplementationPackCase
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

public sealed class LearningShadowImplementationPackReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ShadowImplementationPackPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public int ReadyCases { get; init; }
    public int BlockedCases { get; init; }
    public IReadOnlyList<LearningShadowImplementationPackCase> Cases { get; init; } = Array.Empty<LearningShadowImplementationPackCase>();
    public IReadOnlyList<ShadowBaselineResult> CandidateRerankerBaselines { get; init; } = Array.Empty<ShadowBaselineResult>();
    public IReadOnlyList<ShadowBaselineResult> RouterIntentBaselines { get; init; } = Array.Empty<ShadowBaselineResult>();
    public HardNegativeCoverageReport HardNegativeCoverage { get; init; } = new();
    public ShadowComparisonSummary ShadowComparisonSummary { get; init; } = new();
    public bool CandidateWeightedBaselineReady { get; init; }
    public bool CandidateLogisticBaselineReady { get; init; }
    public bool CandidateTreeBaselineReady { get; init; }
    public bool CandidateMLPShadowReady { get; init; }
    public bool MLPDatasetExportReady { get; init; }
    public bool RouterIntentLogisticBaselineReady { get; init; }
    public bool RouterIntentTreeBaselineReady { get; init; }
    public bool HardNegativeCoverageReady { get; init; }
    public bool FailureSampleExported { get; init; }
    public bool ShadowComparisonSummaryWritten { get; init; }
    public IReadOnlyList<string> FailureSamplePaths { get; init; } = Array.Empty<string>();
    public string ShadowComparisonSummaryPath { get; init; } = string.Empty;
    public bool ShadowOnly { get; init; }
    public bool RuntimeAuthority { get; init; }
    public bool GateAuthority { get; init; }
    public bool RuntimeRerankerChanged { get; init; }
    public bool RuntimeRouterChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool V8ScopedActivationPreserved { get; init; }
    public bool UpstreamLearningLayerBootstrapGatePresent { get; init; }
    public bool UpstreamLearningLayerBootstrapGatePassed { get; init; }
    public bool MainlineEvidencePresent { get; init; }
    public bool MainlineTrustRegistryPresent { get; init; }
    public string Recommendation { get; init; } = string.Empty;
    public string NextAllowedPhase { get; init; } = string.Empty;
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class LearningShadowImplementationPackOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
