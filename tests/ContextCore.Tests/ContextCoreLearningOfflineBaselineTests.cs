using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Planning;
using ContextCore.Storage.InMemory;

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
    public async Task RouterIntentClassifierR1_ShouldGenerateReports()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"contextcore-router-r1-{Guid.NewGuid():N}");
        try
        {
            var inputPath = Path.Combine(tempRoot, "features", LearningDatasetQualityReportBuilder.RouterIntentExamplesFileName);
            var outputDir = Path.Combine(tempRoot, "router");
            Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
            await WriteJsonLinesAsync(inputPath, CreateRouterTrainingExamples().ToArray());

            var report = await new RouterIntentEvaluationRunner().RunAsync(inputPath, outputDir);

            Assert.IsTrue(report.Ready);
            Assert.AreEqual(32, report.SampleCount);
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, RouterIntentEvaluationRunner.ReportFileName)));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, RouterIntentEvaluationRunner.MarkdownReportFileName)));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, RouterIntentEvaluationRunner.ConfusionMatrixFileName)));
            Assert.AreEqual(2, report.Baselines.Count);
            Assert.IsTrue(report.Baselines.Any(item => item.BaselineName == RouterIntentClassifierBaselineNames.ExistingRuleBasedRouterBaseline));
            Assert.IsTrue(report.Baselines.Any(item => item.BaselineName == RouterIntentClassifierBaselineNames.TokenCentroidRouterBaseline));
            Assert.IsFalse(string.IsNullOrWhiteSpace(report.Recommendation));
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
    public void TokenCentroidRouterBaseline_ShouldNotUseIdsForPrediction()
    {
        var training = CreateRouterTrainingExamples().ToArray();
        var classifier = new TokenCentroidRouterBaseline();
        classifier.Fit(training);
        var first = CreateRouterExample(
            "router-id-a",
            "CodingMode",
            PlanningIntentDetector.CodingTask,
            inputSummary: "compile module verification build");
        var second = CreateRouterExample(
            "router-id-b",
            "ChatMode",
            PlanningIntentDetector.FuzzyQuestion,
            inputSummary: "compile module verification build");

        var firstPrediction = classifier.Predict(first);
        var secondPrediction = classifier.Predict(second);

        Assert.AreEqual(firstPrediction.Intent, secondPrediction.Intent);
    }

    [TestMethod]
    public async Task RouterShadow_ShouldBeDisabledByDefault()
    {
        var store = new InMemoryRouterIntentShadowTraceStore();
        var service = new RouterIntentShadowService(
            new RouterShadowOptions(),
            store,
            new PlanningIntentDetector());

        var trace = await service.RecordAsync(new RouterIntentShadowRecordRequest
        {
            RequestId = "router-shadow-disabled",
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            EntryPoint = "planning",
            QueryText = "build verification task",
            RuntimeIntent = PlanningIntentDetector.CodingTask
        });
        var records = await store.QueryAsync(new RouterIntentShadowTraceQuery
        {
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1"
        });

        Assert.IsNull(trace);
        Assert.AreEqual(0, records.Count);
    }

    [TestMethod]
    public async Task RouterShadow_ShouldRecordDisagreementWithoutChangingRuntimeIntent()
    {
        var store = new InMemoryRouterIntentShadowTraceStore();
        var service = new RouterIntentShadowService(
            new RouterShadowOptions
            {
                Enabled = true,
                TraceCollectionEnabled = true
            },
            store,
            new PlanningIntentDetector());
        const string runtimeIntent = PlanningIntentDetector.CodingTask;

        var trace = await service.RecordAsync(new RouterIntentShadowRecordRequest
        {
            RequestId = "router-shadow-disagreement",
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            EntryPoint = "planning",
            QueryText = "plain runtime query",
            RuntimeIntent = runtimeIntent
        });

        Assert.IsNotNull(trace);
        Assert.AreEqual(runtimeIntent, trace.RuntimeIntent);
        Assert.IsFalse(trace.FormalOutputChanged);
        Assert.IsFalse(trace.Agreement);
        Assert.AreEqual(PlanningIntentDetector.FuzzyQuestion, trace.ShadowIntent);
        var records = await store.QueryAsync(new RouterIntentShadowTraceQuery
        {
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1"
        });
        Assert.AreEqual(1, records.Count);
    }

    [TestMethod]
    public void RouterShadowTraceQuality_ShouldCountLowConfidenceAndConfusion()
    {
        var report = new RouterIntentShadowReportBuilder().BuildTraceQualityReport(
        [
            new RouterIntentShadowTrace
            {
                RequestId = "trace-1",
                WorkspaceId = "workspace-1",
                CollectionId = "collection-1",
                RuntimeIntent = PlanningIntentDetector.CodingTask,
                ShadowIntent = PlanningIntentDetector.FuzzyQuestion,
                Agreement = false,
                LowConfidence = true,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new RouterIntentShadowTrace
            {
                RequestId = "trace-2",
                WorkspaceId = "workspace-1",
                CollectionId = "collection-1",
                RuntimeIntent = PlanningIntentDetector.CurrentTask,
                ShadowIntent = PlanningIntentDetector.CurrentTask,
                Agreement = true,
                CreatedAt = DateTimeOffset.UtcNow
            }
        ], "workspace-1", "collection-1");

        Assert.AreEqual(2, report.TraceCount);
        Assert.AreEqual(1, report.LowConfidenceCount);
        Assert.IsTrue(report.TopConfusionPairs.ContainsKey($"{PlanningIntentDetector.CodingTask}->{PlanningIntentDetector.FuzzyQuestion}"));
    }

    [TestMethod]
    public async Task RouterShadowEval_ShouldGenerateReports()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"contextcore-router-r2-{Guid.NewGuid():N}");
        try
        {
            var inputPath = Path.Combine(tempRoot, "features", LearningDatasetQualityReportBuilder.RouterIntentExamplesFileName);
            var outputDir = Path.Combine(tempRoot, "router");
            Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
            await WriteJsonLinesAsync(inputPath, CreateRouterTrainingExamples().ToArray());

            var (a3, extended) = await new RouterIntentShadowReportBuilder()
                .RunShadowEvalAsync(inputPath, outputDir);

            Assert.IsTrue(File.Exists(Path.Combine(outputDir, RouterIntentShadowReportBuilder.ShadowEvalA3FileName)));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, RouterIntentShadowReportBuilder.ShadowEvalExtendedFileName)));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, RouterIntentShadowReportBuilder.ShadowEvalMarkdownFileName)));
            Assert.IsTrue(a3.SampleCount > 0);
            Assert.AreEqual(a3.SampleCount, extended.SampleCount);
            Assert.IsFalse(string.IsNullOrWhiteSpace(a3.Recommendation));
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
    public void RouterDisagreementTriage_ShouldClassifyShadowFixesAndBreaks()
    {
        var examples = CreateRouterTriageExamples().ToArray();

        var report = new RouterDisagreementTriageRunner().BuildReport(examples, "A3", "router-intent-examples.jsonl");

        Assert.IsTrue(report.DisagreementCount >= 2);
        Assert.IsTrue(report.Disagreements.Any(item => item.TriageCategory == RouterDisagreementTriageCategories.ShadowFixesRuntime));
        Assert.IsTrue(report.Disagreements.Any(item => item.TriageCategory == RouterDisagreementTriageCategories.ShadowBreaksRuntime));
        Assert.IsTrue(report.ShadowFixesRuntime > 0);
        Assert.IsTrue(report.ShadowBreaksRuntime > 0);
    }

    [TestMethod]
    public async Task RouterDisagreementTriage_ShouldGenerateStableHardNegativeJsonl()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"contextcore-router-triage-{Guid.NewGuid():N}");
        try
        {
            var inputPath = Path.Combine(tempRoot, "features", LearningDatasetQualityReportBuilder.RouterIntentExamplesFileName);
            var outputDir = Path.Combine(tempRoot, "router");
            Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
            await WriteJsonLinesAsync(inputPath, CreateRouterTriageExamples().ToArray());

            var (a3, extended) = await new RouterDisagreementTriageRunner().RunAsync(inputPath, outputDir);
            var hardNegativePath = Path.Combine(outputDir, RouterDisagreementTriageRunner.HardNegativesFileName);
            var hardNegativeLines = await File.ReadAllLinesAsync(hardNegativePath);

            Assert.IsTrue(File.Exists(Path.Combine(outputDir, RouterDisagreementTriageRunner.A3ReportFileName)));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, RouterDisagreementTriageRunner.ExtendedReportFileName)));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, RouterDisagreementTriageRunner.MarkdownReportFileName)));
            Assert.IsTrue(hardNegativeLines.Length > 0);
            Assert.AreEqual(hardNegativeLines.Distinct(StringComparer.OrdinalIgnoreCase).Count(), hardNegativeLines.Length);
            Assert.IsTrue(a3.HardNegativeCount > 0);
            Assert.AreEqual(a3.DisagreementCount, extended.DisagreementCount);
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
    public void RouterIntentBoundaryMarkdown_ShouldContainDefinitions()
    {
        var path = FindRepoFile("docs", "router-intent-boundaries.md");

        Assert.IsTrue(File.Exists(path));
        var markdown = File.ReadAllText(path);
        StringAssert.Contains(markdown, "Intent 定义");
        StringAssert.Contains(markdown, "常见混淆");
        StringAssert.Contains(markdown, "Hard Negative 使用规则");
    }

    [TestMethod]
    public void RouterDisagreementTriage_ShouldNotUseSampleIdForPrediction()
    {
        var training = CreateRouterTrainingExamples().ToArray();
        var classifier = new TokenCentroidRouterBaseline();
        classifier.Fit(training);
        var first = CreateRouterExample(
            "router-triage-id-a",
            "ChatMode",
            PlanningIntentDetector.CurrentTask,
            inputSummary: "active focus next step");
        var second = CreateRouterExample(
            "router-triage-id-b",
            "AutomationMode",
            PlanningIntentDetector.AutomationRecovery,
            inputSummary: "active focus next step");

        var firstPrediction = classifier.Predict(first);
        var secondPrediction = classifier.Predict(second);

        Assert.AreEqual(firstPrediction.Intent, secondPrediction.Intent);
    }

    [TestMethod]
    public void RouterGuardedOptInGate_ShouldFailWhenBreaksExceedFixes()
    {
        var report = new RouterGuardedOptInReadinessGateRunner().BuildReport(
            [
                CreateRouterShadowEvalReport(
                    sampleCount: 10,
                    agreementRate: 0.9,
                    fixes: 1,
                    breaks: 3,
                    regressionCount: 3)
            ],
            [
                CreateRouterTriageReport(fixes: 1, breaks: 3)
            ],
            p15GatePassed: true);

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(
            report.FailureReasons.ToArray(),
            RouterGuardedOptInGateFailureReasons.ShadowBreaksRuntimeGreaterThanFixes);
        Assert.AreEqual(RouterGuardedOptInGateRecommendations.KeepRuleBased, report.Recommendation);
    }

    [TestMethod]
    public void RouterGuardedOptInGate_ShouldPassWhenFixesPositiveAndNoRegression()
    {
        var report = new RouterGuardedOptInReadinessGateRunner().BuildReport(
            [
                CreateRouterShadowEvalReport(
                    sampleCount: 10,
                    agreementRate: 0.95,
                    fixes: 2,
                    breaks: 0,
                    regressionCount: 0)
            ],
            [
                CreateRouterTriageReport(fixes: 2, breaks: 0)
            ],
            p15GatePassed: true);

        Assert.IsTrue(report.Passed);
        Assert.AreEqual(2, report.ShadowFixesRuntime);
        Assert.AreEqual(0, report.ShadowBreaksRuntime);
        Assert.AreEqual(RouterGuardedOptInGateRecommendations.ReadyForGuardedOptIn, report.Recommendation);
    }

    [TestMethod]
    public void RouterGuardedOptInGate_ShouldNotUseSampleIdForDecision()
    {
        var first = new RouterGuardedOptInReadinessGateRunner().BuildReport(
            [
                CreateRouterShadowEvalReport(
                    sampleCount: 12,
                    agreementRate: 0.92,
                    fixes: 1,
                    breaks: 0,
                    regressionCount: 0,
                    operationId: "gate-report-a")
            ],
            [
                CreateRouterTriageReport(fixes: 1, breaks: 0, operationId: "triage-report-a")
            ],
            p15GatePassed: true);
        var second = new RouterGuardedOptInReadinessGateRunner().BuildReport(
            [
                CreateRouterShadowEvalReport(
                    sampleCount: 12,
                    agreementRate: 0.92,
                    fixes: 1,
                    breaks: 0,
                    regressionCount: 0,
                    operationId: "gate-report-b")
            ],
            [
                CreateRouterTriageReport(fixes: 1, breaks: 0, operationId: "triage-report-b")
            ],
            p15GatePassed: true);

        Assert.AreEqual(first.Passed, second.Passed);
        CollectionAssert.AreEqual(first.FailureReasons.ToArray(), second.FailureReasons.ToArray());
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

    [TestMethod]
    public async Task LearningReadinessRegistry_ShouldFreezeCurrentShadowCapabilities()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"contextcore-learning-readiness-{Guid.NewGuid():N}");
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(tempRoot, "eval"));
            Directory.CreateDirectory(Path.Combine(tempRoot, "learning", "router"));
            Directory.CreateDirectory(Path.Combine(tempRoot, "learning", "ranker"));
            await WriteJsonAsync(
                Path.Combine(tempRoot, "eval", "graph-expansion-guarded-optin-gate.json"),
                new GraphExpansionGuardedOptInGateReport
                {
                    CreatedAt = DateTimeOffset.Parse("2026-06-12T00:00:00Z"),
                    Passed = true
                });
            await WriteJsonAsync(
                Path.Combine(tempRoot, "eval", "vector-retrieval-shadow-readiness-gate.json"),
                new VectorRetrievalShadowReadinessGateReport
                {
                    CreatedAt = DateTimeOffset.Parse("2026-06-12T00:00:00Z"),
                    Passed = false,
                    FailReasons = ["A3RecallAtLeast80Percent"]
                });
            await WriteJsonAsync(
                Path.Combine(tempRoot, "learning", "router", "router-guarded-optin-readiness-gate.json"),
                new RouterGuardedOptInReadinessGateReport
                {
                    GeneratedAt = DateTimeOffset.Parse("2026-06-12T00:00:00Z"),
                    Passed = false,
                    ShadowFixesRuntime = 1,
                    ShadowBreaksRuntime = 3,
                    NetGain = -2,
                    FailureReasons =
                    [
                        RouterGuardedOptInGateFailureReasons.ShadowBreaksRuntimeGreaterThanFixes
                    ],
                    Recommendation = RouterGuardedOptInGateRecommendations.KeepRuleBased
                });
            await WriteJsonAsync(
                Path.Combine(tempRoot, "learning", "ranker", "candidate-reranker-shadow-eval-a3.json"),
                new CandidateRerankerShadowEvalReport
                {
                    GeneratedAt = DateTimeOffset.Parse("2026-06-12T00:00:00Z"),
                    NetGain = -17,
                    Recommendation = CandidateRerankerShadowRecommendations.KeepFormalRanking
                });
            await WriteJsonAsync(
                Path.Combine(tempRoot, "learning", "ranker", "candidate-reranker-shadow-eval-extended.json"),
                new CandidateRerankerShadowEvalReport
                {
                    GeneratedAt = DateTimeOffset.Parse("2026-06-12T00:00:00Z"),
                    NetGain = -3,
                    Recommendation = CandidateRerankerShadowRecommendations.KeepFormalRanking
                });

            Directory.SetCurrentDirectory(tempRoot);
            var registry = await new LearningReadinessFreezeRunner().BuildRegistryFromCurrentFilesAsync();

            var graph = registry.Capabilities.Single(item => item.CapabilityId == ShadowCapabilityIds.GraphExpansion);
            var vector = registry.Capabilities.Single(item => item.CapabilityId == ShadowCapabilityIds.VectorRetrieval);
            var router = registry.Capabilities.Single(item => item.CapabilityId == ShadowCapabilityIds.RouterIntentClassifier);
            var ranker = registry.Capabilities.Single(item => item.CapabilityId == ShadowCapabilityIds.CandidateReranker);

            Assert.IsTrue(graph.GatePassed);
            CollectionAssert.Contains(graph.AllowedRuntimeModes.ToArray(), "ApplyGuarded:audit-v1");
            CollectionAssert.Contains(graph.AllowedRuntimeModes.ToArray(), "ApplyGuarded:conflict-v1");
            CollectionAssert.Contains(graph.ForbiddenRuntimeModes.ToArray(), "ApplyGuarded:normal-v1");
            CollectionAssert.Contains(graph.ForbiddenRuntimeModes.ToArray(), "ApplyGuarded:current-task-v1");
            Assert.IsFalse(vector.GatePassed);
            Assert.AreEqual("BlockedByRecall", vector.Recommendation);
            CollectionAssert.Contains(vector.BlockedReasons.ToArray(), "A3RecallAtLeast80Percent");
            CollectionAssert.Contains(vector.ForbiddenRuntimeModes.ToArray(), ShadowRuntimeModes.RuntimeShadow);
            Assert.IsFalse(router.GatePassed);
            Assert.AreEqual(RouterGuardedOptInGateRecommendations.KeepRuleBased, router.Recommendation);
            CollectionAssert.Contains(router.ForbiddenRuntimeModes.ToArray(), ShadowRuntimeModes.ApplyGuarded);
            Assert.IsFalse(ranker.GatePassed);
            Assert.AreEqual(ShadowCapabilityReadinessStatuses.KeepFormalRanking, ranker.Status);
            CollectionAssert.Contains(ranker.BlockedReasons.ToArray(), "NetGainNotPositive");
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public void LearningRuntimeChangeGate_ShouldFailWhenBlockedCapabilityAllowsRuntimeShadow()
    {
        var registry = new LearningReadinessRegistry
        {
            Capabilities =
            [
                new ShadowCapabilityReadiness
                {
                    CapabilityId = ShadowCapabilityIds.VectorRetrieval,
                    GatePassed = false,
                    BlockedReasons = ["A3RecallAtLeast80Percent"],
                    AllowedRuntimeModes = [ShadowRuntimeModes.RuntimeShadow],
                    ForbiddenRuntimeModes = [ShadowRuntimeModes.DefaultOn]
                }
            ]
        };

        var report = new LearningReadinessFreezeRunner().BuildRuntimeChangeGate(registry);

        Assert.IsFalse(report.Passed);
        Assert.IsTrue(report.FailedConditions.Any(item =>
            item.Contains("NotReadyDoesNotAllowRuntimeModes", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void LearningRuntimeChangeGate_ShouldPassWhenRegistryBlocksUnsafeModes()
    {
        var registry = new LearningReadinessRegistry
        {
            Capabilities =
            [
                new ShadowCapabilityReadiness
                {
                    CapabilityId = ShadowCapabilityIds.VectorRetrieval,
                    GatePassed = false,
                    BlockedReasons = ["A3RecallAtLeast80Percent"],
                    AllowedRuntimeModes = [ShadowRuntimeModes.Off, ShadowRuntimeModes.PreviewOnly],
                    ForbiddenRuntimeModes =
                    [
                        ShadowRuntimeModes.RuntimeShadow,
                        ShadowRuntimeModes.ApplyGuarded,
                        ShadowRuntimeModes.DefaultOn
                    ]
                },
                new ShadowCapabilityReadiness
                {
                    CapabilityId = ShadowCapabilityIds.RouterIntentClassifier,
                    GatePassed = false,
                    BlockedReasons =
                    [
                        RouterGuardedOptInGateFailureReasons.ShadowBreaksRuntimeGreaterThanFixes
                    ],
                    AllowedRuntimeModes = [ShadowRuntimeModes.ExistingRuntime],
                    ForbiddenRuntimeModes =
                    [
                        ShadowRuntimeModes.RuntimeShadow,
                        ShadowRuntimeModes.ApplyGuarded,
                        ShadowRuntimeModes.DefaultOn
                    ]
                },
                new ShadowCapabilityReadiness
                {
                    CapabilityId = ShadowCapabilityIds.CandidateReranker,
                    GatePassed = false,
                    BlockedReasons = ["NetGainNotPositive"],
                    AllowedRuntimeModes = [ShadowRuntimeModes.Off],
                    ForbiddenRuntimeModes =
                    [
                        ShadowRuntimeModes.RuntimeShadow,
                        ShadowRuntimeModes.ApplyGuarded,
                        ShadowRuntimeModes.DefaultOn
                    ]
                },
                new ShadowCapabilityReadiness
                {
                    CapabilityId = ShadowCapabilityIds.GraphExpansion,
                    GatePassed = true,
                    AllowedRuntimeModes =
                    [
                        "ApplyGuarded:audit-v1",
                        "ApplyGuarded:conflict-v1"
                    ],
                    ForbiddenRuntimeModes =
                    [
                        "ApplyGuarded:normal-v1",
                        "ApplyGuarded:current-task-v1",
                        ShadowRuntimeModes.DefaultOn
                    ]
                }
            ]
        };

        var report = new LearningReadinessFreezeRunner().BuildRuntimeChangeGate(registry);

        Assert.IsTrue(report.Passed);
    }

    private static ContextPolicyFeatureExample CreateRouterExample(
        string id,
        string mode,
        string intent,
        string? inputSummary = null)
        => new()
        {
            ExampleId = id,
            SourceType = "PlanningShadowComparison",
            SourceId = id,
            TaskKind = "RouterIntent",
            Mode = mode,
            Intent = intent,
            Label = intent,
            InputSummary = inputSummary ?? $"{intent}/{mode}",
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

    private static IEnumerable<ContextPolicyFeatureExample> CreateRouterTrainingExamples()
    {
        for (var index = 0; index < 8; index++)
        {
            yield return CreateRouterExample(
                $"router-current-{index}",
                "ChatMode",
                PlanningIntentDetector.CurrentTask,
                inputSummary: $"active focus next step task {index}");
            yield return CreateRouterExample(
                $"router-coding-{index}",
                "CodingMode",
                PlanningIntentDetector.CodingTask,
                inputSummary: $"compile module verification build {index}");
            yield return CreateRouterExample(
                $"router-novel-{index}",
                "NovelMode",
                PlanningIntentDetector.NovelGeneration,
                inputSummary: $"chapter scene character arc {index}");
            yield return CreateRouterExample(
                $"router-automation-{index}",
                "AutomationMode",
                PlanningIntentDetector.AutomationRecovery,
                inputSummary: $"retry recovery failure checkpoint {index}");
        }
    }

    private static IEnumerable<ContextPolicyFeatureExample> CreateRouterTriageExamples()
    {
        for (var index = 0; index < 18; index++)
        {
            yield return CreateRouterExample(
                $"triage-train-current-{index}",
                "ChatMode",
                PlanningIntentDetector.CurrentTask,
                inputSummary: $"focus marker active task route {index}");
            yield return CreateRouterExample(
                $"triage-train-fuzzy-{index}",
                "ChatMode",
                PlanningIntentDetector.FuzzyQuestion,
                inputSummary: $"plain marker general question route {index}");
            yield return CreateRouterExample(
                $"triage-train-coding-{index}",
                "CodingMode",
                PlanningIntentDetector.CodingTask,
                inputSummary: $"compile module verification route {index}");
        }

        yield return CreateRouterExample(
            "triage-fix-0",
            "ChatMode",
            PlanningIntentDetector.CurrentTask,
            inputSummary: "focus marker active route");
        yield return CreateRouterExample(
            "triage-break-1",
            "CodingMode",
            PlanningIntentDetector.CodingTask,
            inputSummary: "plain marker general route");
    }

    private static RouterIntentShadowEvalReport CreateRouterShadowEvalReport(
        int sampleCount,
        double agreementRate,
        int fixes,
        int breaks,
        int regressionCount,
        string operationId = "router-shadow-eval-test")
        => new()
        {
            OperationId = operationId,
            DatasetName = "test",
            SampleCount = sampleCount,
            AgreementRate = agreementRate,
            LowConfidenceCount = 0,
            ShadowFixesRuntime = fixes,
            ShadowBreaksRuntime = breaks,
            NetGain = fixes - breaks,
            PerIntentRegression = regressionCount == 0
                ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    [PlanningIntentDetector.CodingTask] = regressionCount
                }
        };

    private static RouterDisagreementTriageReport CreateRouterTriageReport(
        int fixes,
        int breaks,
        string operationId = "router-triage-test")
        => new()
        {
            OperationId = operationId,
            DatasetName = "test",
            SampleCount = fixes + breaks,
            DisagreementCount = fixes + breaks,
            ShadowFixesRuntime = fixes,
            ShadowBreaksRuntime = breaks,
            Recommendation = fixes > breaks
                ? RouterDisagreementTriageRecommendations.NeedsHardNegativeDataset
                : RouterDisagreementTriageRecommendations.KeepRuleBased
        };

    private static string FindRepoFile(params string[] segments)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var path = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(path))
            {
                return path;
            }

            directory = directory.Parent;
        }

        return Path.Combine(segments);
    }

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

    private static Task WriteJsonAsync<T>(string path, T value)
        => File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions));
}
