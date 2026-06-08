using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public sealed class ContextCoreLearningOfflineBaselineTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void EmptyDatasets_ShouldReturnNotReady()
    {
        var runner = new LearningOfflineBaselineRunner();

        var router = runner.BuildRouterReport([]);
        var ranker = runner.BuildRankerReport([]);

        Assert.IsFalse(router.Ready);
        Assert.AreEqual(LearningDatasetReadinessStatus.NotReady, router.Status);
        CollectionAssert.Contains(router.NotReadyReasons.ToArray(), "router-intent-examples is empty");
        Assert.IsFalse(ranker.Ready);
        Assert.AreEqual(LearningDatasetReadinessStatus.NotReady, ranker.Status);
        CollectionAssert.Contains(ranker.NotReadyReasons.ToArray(), "ranking-pairs is empty");
    }

    [TestMethod]
    public void RouterReport_ShouldComputeMetrics()
    {
        var examples = Enumerable.Range(0, 30)
            .Select(index => index % 3 == 0
                ? CreateRouterExample($"router-auto-{index}", "AutomationMode", PlanningIntentDetector.AutomationRecovery)
                : index % 3 == 1
                    ? CreateRouterExample($"router-code-{index}", "CodingMode", PlanningIntentDetector.CodingTask)
                    : CreateRouterExample($"router-novel-{index}", "NovelMode", PlanningIntentDetector.NovelGeneration))
            .ToArray();

        var report = new LearningOfflineBaselineRunner().BuildRouterReport(examples, "router-intent-examples.jsonl");

        Assert.IsTrue(report.Ready);
        Assert.AreEqual(30, report.SampleCount);
        Assert.AreEqual("DeterministicGroupHash80_20", report.Split.Strategy);
        Assert.IsTrue(report.Split.TestExampleCount > 0);
        Assert.AreEqual(2, report.Baselines.Count);
        var rule = report.Baselines.Single(item => item.BaselineName == LearningOfflineBaselineRunner.RuleBasedBaseline);
        Assert.IsTrue(rule.Accuracy > 0);
        Assert.IsTrue(rule.MacroF1 > 0);
        Assert.IsTrue(rule.PerIntentPrecision.ContainsKey(PlanningIntentDetector.AutomationRecovery));
        Assert.IsTrue(rule.PerIntentRecall.ContainsKey(PlanningIntentDetector.CodingTask));
        Assert.IsTrue(rule.ConfusionMatrix.ContainsKey(PlanningIntentDetector.NovelGeneration));
    }

    [TestMethod]
    public void RankerReport_ShouldComputePairwiseAccuracy()
    {
        var pairs = Enumerable.Range(0, 12)
            .Select(index => CreateRankingPair(
                $"sample-ranker-{index}",
                positiveScore: index == 0 ? 1 : 10 + index,
                negativeScore: index == 0 ? 20 : 1))
            .ToArray();

        var report = new LearningOfflineBaselineRunner().BuildRankerReport(pairs, "ranking-pairs.jsonl");

        Assert.IsTrue(report.Ready);
        Assert.AreEqual(12, report.PairCount);
        Assert.AreEqual("DeterministicGroupHash80_20", report.Split.Strategy);
        Assert.AreEqual(2, report.Baselines.Count);
        var rule = report.Baselines.Single(item => item.BaselineName == LearningOfflineBaselineRunner.RuleScoreBaseline);
        Assert.IsTrue(rule.PairwiseAccuracy > 0);
        Assert.IsTrue(rule.Auc.HasValue);
        Assert.IsTrue(rule.FalsePositiveRate >= 0);
        var weighted = report.Baselines.Single(item => item.BaselineName == LearningOfflineBaselineRunner.SimpleFeatureWeightedBaseline);
        Assert.IsTrue(weighted.PairwiseAccuracy >= rule.PairwiseAccuracy);
    }

    [TestMethod]
    public async Task OutputFiles_ShouldBeGenerated()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"contextcore-learning-baseline-{Guid.NewGuid():N}");
        try
        {
            var featureDir = Path.Combine(tempRoot, "features");
            var outputDir = Path.Combine(tempRoot, "baselines");
            Directory.CreateDirectory(featureDir);
            Directory.CreateDirectory(outputDir);
            var routerPath = Path.Combine(featureDir, LearningDatasetQualityReportBuilder.RouterIntentExamplesFileName);
            var rankerPath = Path.Combine(featureDir, LearningDatasetQualityReportBuilder.RankingPairsFileName);
            await WriteJsonLinesAsync(routerPath, Enumerable.Range(0, 12)
                .Select(index => CreateRouterExample($"router-output-{index}", "CodingMode", PlanningIntentDetector.CodingTask))
                .ToArray());
            await WriteJsonLinesAsync(rankerPath, Enumerable.Range(0, 12)
                .Select(index => CreateRankingPair($"sample-output-{index}", 10 + index, 1))
                .ToArray());

            var runner = new LearningOfflineBaselineRunner();
            var routerReport = await runner.RunRouterAsync(
                routerPath,
                Path.Combine(outputDir, "router-intent-baseline-report.json"),
                Path.Combine(outputDir, "router-intent-baseline-report.md"));
            var rankerReport = await runner.RunRankerAsync(
                rankerPath,
                Path.Combine(outputDir, "ranker-baseline-report.json"),
                Path.Combine(outputDir, "ranker-baseline-report.md"));

            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "router-intent-baseline-report.json")));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "router-intent-baseline-report.md")));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "ranker-baseline-report.json")));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "ranker-baseline-report.md")));
            Assert.AreEqual(12, routerReport.SampleCount);
            Assert.AreEqual(12, rankerReport.PairCount);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task RankerAblationReport_ShouldBeGenerated()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"contextcore-ranker-ablation-{Guid.NewGuid():N}");
        try
        {
            var inputPath = Path.Combine(tempRoot, "features", LearningDatasetQualityReportBuilder.RankingPairsFileName);
            var outputDir = Path.Combine(tempRoot, "baselines");
            Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
            Directory.CreateDirectory(outputDir);
            await WriteJsonLinesAsync(inputPath, Enumerable.Range(0, 12)
                .Select(index => CreateRankingPair($"sample-ablation-{index}", 10 + index, 1))
                .ToArray());

            var report = await new LearningOfflineBaselineRunner().RunRankerAblationAsync(
                inputPath,
                Path.Combine(outputDir, "ranker-ablation-report.json"),
                Path.Combine(outputDir, "ranker-ablation-report.md"));

            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "ranker-ablation-report.json")));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "ranker-ablation-report.md")));
            Assert.IsTrue(report.Ready);
            Assert.AreEqual(12, report.PairCount);
            Assert.AreEqual(10, report.Ablations.Count);
            Assert.IsTrue(report.Ablations.Any(item => item.DisabledFeature == "lifecycle"));
            Assert.IsTrue(report.Ablations.Any(item => item.DisabledFeature == "semantic anchor match"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task RankerWeightSweepReport_ShouldBeGenerated()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"contextcore-ranker-sweep-{Guid.NewGuid():N}");
        try
        {
            var inputPath = Path.Combine(tempRoot, "features", LearningDatasetQualityReportBuilder.RankingPairsFileName);
            var outputDir = Path.Combine(tempRoot, "baselines");
            Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
            Directory.CreateDirectory(outputDir);
            await WriteJsonLinesAsync(inputPath, Enumerable.Range(0, 12)
                .Select(index => CreateRankingPair($"sample-sweep-{index}", 10 + index, 1))
                .ToArray());

            var report = await new LearningOfflineBaselineRunner().RunRankerWeightSweepAsync(
                inputPath,
                Path.Combine(outputDir, "ranker-weight-sweep-report.json"),
                Path.Combine(outputDir, "ranker-weight-sweep-report.md"));

            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "ranker-weight-sweep-report.json")));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "ranker-weight-sweep-report.md")));
            Assert.IsTrue(report.Ready);
            Assert.AreEqual(12, report.PairCount);
            Assert.IsTrue(report.SweepResults.Any(item => item.ParameterName == "lifecyclePenaltyWeight"));
            Assert.IsTrue(report.SweepResults.Any(item => item.ParameterName == "stablePreferenceBoost"));
            Assert.IsFalse(string.IsNullOrWhiteSpace(report.BestResult.ConfigurationId));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public void RankerFailureClustering_ShouldClassifyDeprecatedNoise()
    {
        var pairs = new[]
        {
            CreateRankingPair(
                "sample-cluster-deprecated",
                positiveScore: 1,
                negativeScore: 50,
                negativeKind: "deprecated_memory",
                negativeSection: "historical_context",
                negativeId: "memory:old-deprecated-preference")
        };

        var report = new LearningOfflineBaselineRunner().BuildRankerAblationReport(pairs, "ranking-pairs.jsonl");

        var lifecycle = report.Ablations.Single(item => item.DisabledFeature == "lifecycle");
        Assert.IsTrue(lifecycle.FailureClusters.Any(item => item.Cluster == "DeprecatedNoise"));
        Assert.IsTrue(lifecycle.FailureClusters.Single(item => item.Cluster == "DeprecatedNoise").Count > 0);
    }

    [TestMethod]
    public void RankerAnalysis_ShouldKeepBaselineMetricsUnchanged()
    {
        var pairs = Enumerable.Range(0, 12)
            .Select(index => CreateRankingPair(
                $"sample-stable-baseline-{index}",
                positiveScore: index == 0 ? 1 : 10 + index,
                negativeScore: index == 0 ? 20 : 1))
            .ToArray();
        var runner = new LearningOfflineBaselineRunner();

        var baseline = runner.BuildRankerReport(pairs, "ranking-pairs.jsonl")
            .Baselines.Single(item => item.BaselineName == LearningOfflineBaselineRunner.SimpleFeatureWeightedBaseline);
        var ablation = runner.BuildRankerAblationReport(pairs, "ranking-pairs.jsonl");
        var sweep = runner.BuildRankerWeightSweepReport(pairs, "ranking-pairs.jsonl");

        Assert.AreEqual(baseline.PairwiseAccuracy, ablation.Baseline.PairwiseAccuracy, 0.0000001);
        Assert.AreEqual(baseline.PairwiseAccuracy, sweep.Baseline.PairwiseAccuracy, 0.0000001);
        Assert.AreEqual(baseline.FalsePositiveRate, ablation.Baseline.FalsePositiveRate, 0.0000001);
        Assert.AreEqual(baseline.FalseNegativeRate, sweep.Baseline.FalseNegativeRate, 0.0000001);
    }

    [TestMethod]
    public void RankerResidualAudit_EmptyPairs_ShouldReturnNotReady()
    {
        var report = new LearningOfflineBaselineRunner().BuildRankerResidualAuditReport([], "ranking-pairs.jsonl");

        Assert.IsFalse(report.Ready);
        Assert.AreEqual(LearningDatasetReadinessStatus.NotReady, report.Status);
        CollectionAssert.Contains(report.NotReadyReasons.ToArray(), "ranking-pairs is empty");
        Assert.AreEqual(0, report.Failures.Count);
    }

    [TestMethod]
    public void RankerResidualAudit_DeprecatedNegativeOutrankingPositive_ShouldClusterAsDeprecatedNoise()
    {
        var pairs = new[]
        {
            CreateRankingPair(
                "sample-residual-deprecated",
                positiveScore: 10,
                negativeScore: 40,
                negativeKind: "deprecated_memory",
                negativeSection: "historical_context",
                negativeId: "memory:old-deprecated-preference")
        };

        var report = new LearningOfflineBaselineRunner().BuildRankerResidualAuditReport(pairs, "ranking-pairs.jsonl");

        Assert.AreEqual(1, report.Failures.Count);
        Assert.AreEqual("DeprecatedNoise", report.Failures[0].FailureCluster);
        Assert.IsTrue(report.FailureClusters.Any(item => item.Cluster == "DeprecatedNoise"));
    }

    [TestMethod]
    public void RankerResidualAudit_ShouldIncludeFeatureDeltas()
    {
        var pairs = new[]
        {
            CreateRankingPair(
                "sample-residual-delta",
                positiveScore: 20,
                negativeScore: 50,
                negativeKind: "deprecated_memory",
                negativeSection: "historical_context")
        };

        var report = new LearningOfflineBaselineRunner().BuildRankerResidualAuditReport(pairs, "ranking-pairs.jsonl");
        var failure = report.Failures.Single();

        Assert.IsTrue(failure.PositiveKeywordMatchScore > 0);
        Assert.IsTrue(failure.NegativeKeywordMatchScore > failure.PositiveKeywordMatchScore);
        Assert.IsTrue(failure.NegativeSemanticAnchorMatchScore > failure.PositiveSemanticAnchorMatchScore);
        Assert.IsTrue(report.FeatureConflicts.Any(item => item.FeatureName == "KeywordMatch" && item.AverageDelta < 0));
        Assert.IsTrue(report.FeatureConflicts.Any(item => item.FeatureName == "SemanticAnchorMatch" && item.AverageDelta < 0));
    }

    [TestMethod]
    public void RankerResidualAudit_ShouldGenerateHardNegativeRecommendationsForDeprecatedNoise()
    {
        var pairs = new[]
        {
            CreateRankingPair(
                "sample-residual-recommendation",
                positiveScore: 20,
                negativeScore: 60,
                negativeKind: "deprecated_memory",
                negativeSection: "historical_context")
        };

        var report = new LearningOfflineBaselineRunner().BuildRankerResidualAuditReport(pairs, "ranking-pairs.jsonl");

        CollectionAssert.Contains(report.HardNegativeRecommendations.Select(item => item.RecommendationType).ToArray(), "DeprecatedSameKeyword");
        CollectionAssert.Contains(report.HardNegativeRecommendations.Select(item => item.RecommendationType).ToArray(), "VersionConflict");
        CollectionAssert.Contains(report.HardNegativeRecommendations.Select(item => item.RecommendationType).ToArray(), "HistoricalSelectedNoise");
        CollectionAssert.Contains(report.HardNegativeRecommendations.Select(item => item.RecommendationType).ToArray(), "WeakLifecycleMarker");
        CollectionAssert.Contains(report.HardNegativeRecommendations.Select(item => item.RecommendationType).ToArray(), "SemanticAnchorOvermatch");
    }

    [TestMethod]
    public void RankerResidualAuditMarkdown_ShouldContainFailureTable()
    {
        var pairs = new[]
        {
            CreateRankingPair(
                "sample-residual-markdown",
                positiveScore: 20,
                negativeScore: 60,
                negativeKind: "deprecated_memory",
                negativeSection: "historical_context")
        };
        var report = new LearningOfflineBaselineRunner().BuildRankerResidualAuditReport(pairs, "ranking-pairs.jsonl");

        var markdown = LearningOfflineBaselineRunner.BuildRankerResidualAuditMarkdownReport(report);

        StringAssert.Contains(markdown, "## Residual Failure Details");
        StringAssert.Contains(markdown, "| Sample | Mode | Intent | Positive | Negative |");
        StringAssert.Contains(markdown, "DeprecatedNoise");
    }

    [TestMethod]
    public void HardNegativeGenerationFromResidualAudit_ShouldGenerateExamples()
    {
        var pairs = new[]
        {
            CreateRankingPair(
                "sample-hard-negative-source",
                positiveScore: 20,
                negativeScore: 60,
                negativeKind: "deprecated_memory",
                negativeSection: "historical_context",
                negativeId: "memory:old-deprecated-keyword")
        };
        var runner = new LearningOfflineBaselineRunner();
        var residual = runner.BuildRankerResidualAuditReport(pairs, "ranking-pairs.jsonl");

        var report = runner.BuildHardNegativeReport(residual, "ranker-residual-audit-report.json", "hard-negatives.jsonl");

        Assert.IsTrue(report.Ready);
        Assert.IsTrue(report.ExampleCount >= 5);
        Assert.IsTrue(report.Examples.All(item => item.ExpectedPreference == "PositiveOverNegative"));
        CollectionAssert.Contains(report.TypeCounts.Keys.ToArray(), "DeprecatedSameKeyword");
        CollectionAssert.Contains(report.TypeCounts.Keys.ToArray(), "SemanticAnchorOvermatch");
    }

    [TestMethod]
    public void HardNegativeGeneration_ShouldClassifyDeprecatedSameKeyword()
    {
        var pairs = new[]
        {
            CreateRankingPair(
                "sample-hard-negative-deprecated",
                positiveScore: 20,
                negativeScore: 60,
                negativeKind: "deprecated_memory",
                negativeSection: "historical_context")
        };
        var runner = new LearningOfflineBaselineRunner();
        var residual = runner.BuildRankerResidualAuditReport(pairs, "ranking-pairs.jsonl");

        var report = runner.BuildHardNegativeReport(residual);

        Assert.IsTrue(report.Examples.Any(item => item.HardNegativeType == "DeprecatedSameKeyword"));
        var example = report.Examples.First(item => item.HardNegativeType == "DeprecatedSameKeyword");
        Assert.IsTrue(example.NegativeFeatures.TryGetValue("isDeprecated", out var deprecated) && deprecated == "true");
    }

    [TestMethod]
    public void HardNegativeGeneration_ShouldClassifyVersionConflict()
    {
        var pairs = new[]
        {
            CreateRankingPair(
                "sample-hard-negative-version",
                positiveScore: 20,
                negativeScore: 60,
                positiveId: "memory:current-rule-v2",
                negativeId: "memory:old-rule-v1",
                negativeKind: "historical_context",
                negativeSection: "historical_context")
        };
        var runner = new LearningOfflineBaselineRunner();
        var residual = runner.BuildRankerResidualAuditReport(pairs, "ranking-pairs.jsonl");

        var report = runner.BuildHardNegativeReport(residual);

        Assert.IsTrue(report.Examples.Any(item => item.HardNegativeType == "VersionConflict"));
        var example = report.Examples.First(item => item.HardNegativeType == "VersionConflict");
        Assert.IsTrue(example.PositiveFeatures.TryGetValue("isCurrentVersion", out var current) && current == "true");
        Assert.IsTrue(example.NegativeFeatures.TryGetValue("versionDistance", out var distance) && double.Parse(distance, System.Globalization.CultureInfo.InvariantCulture) > 0);
    }

    [TestMethod]
    public void LifecycleFeatureExtraction_ShouldPopulateLifecycleFlags()
    {
        var pair = CreateRankingPair(
            "sample-lifecycle-features",
            positiveScore: 20,
            negativeScore: 60,
            positiveId: "memory:current-rule-v2",
            negativeId: "memory:deprecated-rule-v1",
            negativeKind: "deprecated_memory",
            negativeSection: "historical_context");

        var positive = LearningOfflineBaselineRunner.ExtractLifecycleAwareFeatures(pair, "positive");
        var negative = LearningOfflineBaselineRunner.ExtractLifecycleAwareFeatures(pair, "negative");

        Assert.IsTrue(positive.IsCurrentVersion);
        Assert.IsFalse(positive.IsDeprecated);
        Assert.IsTrue(negative.IsDeprecated);
        Assert.IsTrue(negative.IsHistorical);
        Assert.IsTrue(negative.VersionDistance > 0);
        Assert.IsTrue(negative.LifecycleConfidence > positive.LifecycleConfidence);
    }

    [TestMethod]
    public void LifecycleAwareBaseline_ShouldComputeMetrics()
    {
        var pairs = Enumerable.Range(0, 12)
            .Select(index => index % 4 == 0
                ? CreateRankingPair(
                    $"sample-lifecycle-baseline-{index}",
                    positiveScore: 20,
                    negativeScore: 60,
                    positiveId: $"memory:active-plan-v2-{index}",
                    negativeId: $"memory:old-plan-v1-{index}",
                    negativeKind: "historical_context",
                    negativeSection: "historical_context")
                : CreateRankingPair(
                    $"sample-lifecycle-baseline-{index}",
                    positiveScore: 30 + index,
                    negativeScore: 1,
                    positiveId: $"memory:active-plan-v2-{index}",
                    negativeId: $"memory:noise-{index}"))
            .ToArray();

        var report = new LearningOfflineBaselineRunner().BuildLifecycleAwareRankerReport(pairs, "ranking-pairs.jsonl");

        Assert.IsTrue(report.Ready);
        Assert.AreEqual(3, report.Baselines.Count);
        var simple = report.Baselines.Single(item => item.BaselineName == LearningOfflineBaselineRunner.SimpleFeatureWeightedBaseline);
        var lifecycle = report.Baselines.Single(item => item.BaselineName == LearningOfflineBaselineRunner.LifecycleAwareFeatureBaseline);
        Assert.IsTrue(lifecycle.PairwiseAccuracy >= simple.PairwiseAccuracy);
        Assert.IsTrue(lifecycle.ResidualFailures <= simple.ResidualFailures);
    }

    [TestMethod]
    public async Task HardNegativeAndLifecycleAwareOutputFiles_ShouldBeGenerated()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"contextcore-lifecycle-hardnegative-{Guid.NewGuid():N}");
        try
        {
            var featureDir = Path.Combine(tempRoot, "features");
            var outputDir = Path.Combine(tempRoot, "baselines");
            Directory.CreateDirectory(featureDir);
            Directory.CreateDirectory(outputDir);
            var inputPath = Path.Combine(featureDir, LearningDatasetQualityReportBuilder.RankingPairsFileName);
            var pairs = Enumerable.Range(0, 12)
                .Select(index => CreateRankingPair(
                    $"sample-output-lifecycle-{index}",
                    positiveScore: 20,
                    negativeScore: index % 3 == 0 ? 60 : 1,
                    positiveId: $"memory:active-plan-v2-{index}",
                    negativeId: index % 3 == 0 ? $"memory:old-plan-v1-{index}" : $"memory:low-value-{index}",
                    negativeKind: index % 3 == 0 ? "historical_context" : "recent_context",
                    negativeSection: index % 3 == 0 ? "historical_context" : "recent_context"))
                .ToArray();
            await WriteJsonLinesAsync(inputPath, pairs);
            var runner = new LearningOfflineBaselineRunner();
            var residual = runner.BuildRankerResidualAuditReport(
                [
                    CreateRankingPair(
                        "sample-output-hard-negative",
                        positiveScore: 20,
                        negativeScore: 60,
                        positiveId: "memory:active-plan-v2-output",
                        negativeId: "memory:old-plan-v1-output",
                        negativeKind: "historical_context",
                        negativeSection: "historical_context")
                ],
                inputPath);
            var residualPath = Path.Combine(outputDir, "ranker-residual-audit-report.json");
            await File.WriteAllTextAsync(residualPath, JsonSerializer.Serialize(residual, JsonOptions));

            var hardNegativeReport = await runner.RunHardNegativeGenerationAsync(
                residualPath,
                Path.Combine(featureDir, "hard-negatives.jsonl"),
                Path.Combine(outputDir, "hard-negative-report.json"),
                Path.Combine(outputDir, "hard-negative-report.md"));
            var lifecycleReport = await runner.RunLifecycleAwareRankerAsync(
                inputPath,
                Path.Combine(outputDir, "lifecycle-aware-ranker-report.json"),
                Path.Combine(outputDir, "lifecycle-aware-ranker-report.md"));

            Assert.IsTrue(File.Exists(Path.Combine(featureDir, "hard-negatives.jsonl")));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "hard-negative-report.json")));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "hard-negative-report.md")));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "lifecycle-aware-ranker-report.json")));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "lifecycle-aware-ranker-report.md")));
            Assert.IsTrue(hardNegativeReport.ExampleCount > 0);
            Assert.AreEqual(3, lifecycleReport.Baselines.Count);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public void OfflineBaseline_ShouldNotMutateInputExamples()
    {
        var examples = new[]
        {
            CreateRouterExample("router-readonly-1", "CodingMode", PlanningIntentDetector.CodingTask),
            CreateRouterExample("router-readonly-2", "AutomationMode", PlanningIntentDetector.AutomationRecovery)
        };
        var originalIds = examples.Select(item => item.ExampleId).ToArray();

        _ = new LearningOfflineBaselineRunner().BuildRouterReport(examples, "router-intent-examples.jsonl");

        CollectionAssert.AreEqual(originalIds, examples.Select(item => item.ExampleId).ToArray());
    }

    private static ContextPolicyFeatureExample CreateRouterExample(
        string id,
        string mode,
        string intent)
        => new()
        {
            ExampleId = id,
            SourceType = "PlanningShadowComparison",
            SourceId = id,
            TaskKind = "RouterIntent",
            Mode = mode,
            Intent = intent,
            Label = intent,
            InputSummary = $"{intent}/{mode}",
            CandidateKind = "RetrievalPlanProposal",
            CandidateLayer = "Planning",
            CandidateStatus = "NativeValid",
            ChannelSources = ["keyword", "working"],
            ShortTermMatchScore = 1,
            Selected = true,
            Accepted = true,
            EvidenceRefs = [$"{id}-evidence"],
            PolicyVersion = LearningFeatureDatasetService.PolicyVersion,
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static RankingPairExample CreateRankingPair(
        string sampleId,
        double positiveScore,
        double negativeScore,
        string positiveKind = "working_memory",
        string negativeKind = "historical_context",
        string positiveSection = "working_memory",
        string negativeSection = "historical_context",
        string? positiveId = null,
        string? negativeId = null)
        => new()
        {
            Query = $"query {sampleId}",
            Mode = "CodingMode",
            Intent = PlanningIntentDetector.CodingTask,
            PositiveCandidateId = positiveId ?? $"{sampleId}-positive",
            NegativeCandidateId = negativeId ?? $"{sampleId}-negative",
            Reason = "mustHit should rank above mustNotHit",
            EvalSampleId = sampleId,
            FeatureSnapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["positiveScore"] = positiveScore.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["negativeScore"] = negativeScore.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["positiveSelected"] = "true",
                ["negativeSelected"] = "false",
                ["positiveRank"] = "1",
                ["negativeRank"] = "0",
                ["positiveSection"] = positiveSection,
                ["negativeSection"] = negativeSection,
                ["positiveKind"] = positiveKind,
                ["negativeKind"] = negativeKind
            }
        };

    private static async Task WriteJsonLinesAsync<T>(
        string path,
        IReadOnlyList<T> records)
    {
        var lines = records.Select(record => JsonSerializer.Serialize(record, JsonOptions));
        await File.WriteAllTextAsync(path, string.Join(Environment.NewLine, lines));
    }
}
