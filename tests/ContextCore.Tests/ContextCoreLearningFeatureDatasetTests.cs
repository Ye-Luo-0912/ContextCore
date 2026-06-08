using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public sealed class ContextCoreLearningFeatureDatasetTests
{
    private const string WorkspaceId = "workspace-learning-features";
    private const string CollectionId = "collection-learning-features";
    private const string SessionId = "session-learning-features";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void PromotionAccept_ShouldMapToPositiveFeatureExample()
    {
        var dataset = CreatePolicyFeedbackDataset(CreatePolicyFeedbackRecord(
            "policy-promotion-accept",
            "PromotionCandidateReviewRecord",
            "promotion-review-1",
            "accept",
            PolicyFeedbackLabels.Positive,
            "CandidateMemory",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["candidateId"] = "promotion-candidate-1",
                ["candidateKind"] = "Preference",
                ["candidateStatus"] = "Accepted",
                ["candidateImportance"] = "0.87",
                ["keywordMatchScore"] = "0.42",
                ["shortTermMatchScore"] = "0.76"
            }));

        var example = new LearningFeatureDatasetService()
            .GeneratePolicyFeedbackFeatureExamples(dataset)
            .Single();

        Assert.AreEqual("PolicyFeedback", example.TaskKind);
        Assert.AreEqual(PolicyFeedbackLabels.Positive, example.Label);
        Assert.AreEqual("promotion-candidate-1", example.CandidateId);
        Assert.AreEqual("Preference", example.CandidateKind);
        Assert.AreEqual("CandidateMemory", example.CandidateLayer);
        Assert.IsTrue(example.Accepted);
        Assert.IsFalse(example.Rejected);
        Assert.AreEqual(0.87, example.CandidateImportance, 0.001);
        Assert.AreEqual(0.42, example.KeywordMatchScore, 0.001);
        Assert.AreEqual(0.76, example.ShortTermMatchScore, 0.001);
        CollectionAssert.Contains(example.EvidenceRefs.ToArray(), "evidence-policy-promotion-accept");
    }

    [TestMethod]
    public void PromotionReject_ShouldMapToNegativeFeatureExample()
    {
        var dataset = CreatePolicyFeedbackDataset(CreatePolicyFeedbackRecord(
            "policy-promotion-reject",
            "PromotionCandidateReviewRecord",
            "promotion-review-2",
            "reject",
            PolicyFeedbackLabels.Negative,
            "RejectedCandidateMemory",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["candidateId"] = "promotion-candidate-2",
                ["candidateKind"] = "KnownIssue",
                ["candidateStatus"] = "Rejected"
            }));

        var example = new LearningFeatureDatasetService()
            .GeneratePolicyFeedbackFeatureExamples(dataset)
            .Single();

        Assert.AreEqual(PolicyFeedbackLabels.Negative, example.Label);
        Assert.AreEqual("promotion-candidate-2", example.CandidateId);
        Assert.IsFalse(example.Accepted);
        Assert.IsTrue(example.Rejected);
        Assert.AreEqual(1, example.LifecycleRisk);
    }

    [TestMethod]
    public void StableAccept_ShouldMapToPositiveFeatureExample()
    {
        var dataset = CreatePolicyFeedbackDataset(CreatePolicyFeedbackRecord(
            "policy-stable-accept",
            "StableReviewRecord",
            "stable-review-1",
            "accept",
            PolicyFeedbackLabels.Positive,
            "StableMemory",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["stableReviewCandidateId"] = "stable-review-candidate-1",
                ["suggestedStableTarget"] = "StableMemory",
                ["candidateStatus"] = "Accepted"
            }));

        var example = new LearningFeatureDatasetService()
            .GeneratePolicyFeedbackFeatureExamples(dataset)
            .Single();

        Assert.AreEqual("StableReviewRecord", example.SourceType);
        Assert.AreEqual("stable-review-candidate-1", example.CandidateId);
        Assert.AreEqual("StableMemory", example.CandidateKind);
        Assert.AreEqual("StableMemory", example.CandidateLayer);
        Assert.IsTrue(example.Accepted);
    }

    [TestMethod]
    public void ConstraintGapAccept_ShouldMapToPositiveFeatureExample()
    {
        var dataset = CreatePolicyFeedbackDataset(CreatePolicyFeedbackRecord(
            "policy-constraint-gap-accept",
            "ConstraintGapReviewRecord",
            "constraint-gap-review-1",
            "accept",
            PolicyFeedbackLabels.Positive,
            "Hard",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["gapId"] = "constraint-gap-1",
                ["candidateKind"] = "HardConstraint",
                ["sourceSampleId"] = "chat-20260529-003"
            }));

        var example = new LearningFeatureDatasetService()
            .GeneratePolicyFeedbackFeatureExamples(dataset)
            .Single();

        Assert.AreEqual("constraint-gap-1", example.CandidateId);
        Assert.AreEqual("HardConstraint", example.CandidateKind);
        Assert.AreEqual("Hard", example.CandidateLayer);
        Assert.AreEqual("chat-20260529-003", example.Metadata["sourceSampleId"]);
        Assert.IsTrue(example.Accepted);
    }

    [TestMethod]
    public void EvalMustHitAndMustNotHit_ShouldGenerateRankingPair()
    {
        var report = new ContextEvalReport
        {
            Results =
            [
                new ContextEvalResult
                {
                    SampleId = "sample-ranking-1",
                    Query = "current task recovery",
                    Mode = "AutomationMode",
                    Status = "Passed",
                    RetrievalRecall3 = 1,
                    RetrievalRecall5 = 1,
                    RetrievalRecall10 = 1,
                    RetrievalMrrAnyMustHit = 1,
                    SelectedCount = 2,
                    TokenBudget = 4000,
                    MustHit = ["must-hit-1"],
                    MustNotHit = ["must-not-hit-1"],
                    SelectedIds = ["must-hit-1", "supporting-item"],
                    PackageHasAllConstraints = true,
                    PackageHasAllEntities = true,
                    PackageHasAllUncertainties = true,
                    SelectedItemDiagnostics =
                    [
                        new ContextEvalItemDiagnostic
                        {
                            ItemId = "must-hit-1",
                            Kind = "working_memory",
                            SectionName = "working",
                            Score = 25,
                            Rank = 1,
                            IsMustHit = true
                        }
                    ],
                    DroppedItemDiagnostics =
                    [
                        new ContextEvalItemDiagnostic
                        {
                            ItemId = "must-not-hit-1",
                            Kind = "historical_context",
                            SectionName = "historical_context",
                            Score = 2,
                            Rank = 0,
                            IsMustNotHit = true
                        }
                    ]
                }
            ]
        };

        var pair = new LearningFeatureDatasetService()
            .GenerateRankingPairsFromEvalReport(report)
            .Single();

        Assert.AreEqual("sample-ranking-1", pair.EvalSampleId);
        Assert.AreEqual("must-hit-1", pair.PositiveCandidateId);
        Assert.AreEqual("must-not-hit-1", pair.NegativeCandidateId);
        Assert.AreEqual("True", pair.FeatureSnapshot["positiveSelected"]);
        Assert.AreEqual("False", pair.FeatureSnapshot["negativeSelected"]);
        Assert.AreEqual("working", pair.FeatureSnapshot["positiveSection"]);
        Assert.AreEqual("historical_context", pair.FeatureSnapshot["negativeSection"]);
    }

    [TestMethod]
    public async Task Export_ShouldWriteJsonLinesFiles()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"contextcore-learning-features-{Guid.NewGuid():N}");
        try
        {
            var dataset = new LearningFeatureDataset
            {
                DatasetId = "learning-feature-dataset-test",
                FeatureExamples =
                [
                    CreateFeatureExample("feature-1", "PolicyFeedback", PolicyFeedbackLabels.Positive)
                ],
                RankingPairs =
                [
                    new RankingPairExample
                    {
                        Query = "query",
                        Mode = "ChatMode",
                        Intent = "CurrentTask",
                        PositiveCandidateId = "positive-1",
                        NegativeCandidateId = "negative-1",
                        Reason = "mustHit above mustNotHit",
                        EvalSampleId = "sample-1"
                    }
                ],
                RouterIntentExamples =
                [
                    CreateFeatureExample("router-1", "RouterIntent", "CurrentTask")
                ],
                FeatureCount = 1,
                RankingPairCount = 1,
                RouterIntentExampleCount = 1,
                PolicyVersion = LearningFeatureDatasetService.PolicyVersion
            };

            var result = await new LearningFeatureDatasetService().ExportAsync(dataset, outputDirectory);

            Assert.IsTrue(File.Exists(result.PolicyFeedbackFeaturesPath));
            Assert.IsTrue(File.Exists(result.RankingPairsPath));
            Assert.IsTrue(File.Exists(result.RouterIntentExamplesPath));
            Assert.AreEqual(1, result.FeatureCount);
            Assert.AreEqual(1, result.RankingPairCount);
            Assert.AreEqual(1, result.RouterIntentExampleCount);

            var featureLine = File.ReadAllLines(result.PolicyFeedbackFeaturesPath).Single();
            var rankingLine = File.ReadAllLines(result.RankingPairsPath).Single();
            var routerLine = File.ReadAllLines(result.RouterIntentExamplesPath).Single();
            Assert.AreEqual("feature-1", JsonSerializer.Deserialize<ContextPolicyFeatureExample>(featureLine, JsonOptions)!.ExampleId);
            Assert.AreEqual("positive-1", JsonSerializer.Deserialize<RankingPairExample>(rankingLine, JsonOptions)!.PositiveCandidateId);
            Assert.AreEqual("router-1", JsonSerializer.Deserialize<ContextPolicyFeatureExample>(routerLine, JsonOptions)!.ExampleId);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void Build_ShouldOnlyProjectInputData()
    {
        var policyFeedback = CreatePolicyFeedbackDataset(CreatePolicyFeedbackRecord(
            "policy-readonly",
            "PromotionCandidateReviewRecord",
            "promotion-review-readonly",
            "accept",
            PolicyFeedbackLabels.Positive,
            "CandidateMemory",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["candidateId"] = "promotion-readonly"
            }));
        var originalRecordCount = policyFeedback.Records.Count;

        var dataset = new LearningFeatureDatasetService().Build(policyFeedback);

        Assert.AreEqual(originalRecordCount, policyFeedback.Records.Count);
        Assert.AreEqual(1, dataset.FeatureCount);
        Assert.AreEqual(0, dataset.RankingPairCount);
        Assert.AreEqual(0, dataset.RouterIntentExampleCount);
    }

    [TestMethod]
    public void PlanningShadowReport_ShouldGenerateRouterIntentExample()
    {
        var report = new ShadowRetrievalComparisonReport
        {
            ReportId = "planning-shadow-report-test",
            SampleSet = "a3",
            GeneratedAt = DateTimeOffset.UtcNow,
            Samples =
            [
                new ShadowRetrievalComparisonItem
                {
                    SampleId = "sample-router-1",
                    Mode = "CodingMode",
                    ProposalId = "proposal-1",
                    ProposalSummary = "CodingTask/CodingMode keyword=8 memory=8 relation=4 final=10",
                    LegacyOperationId = "legacy-1",
                    ShadowOperationId = "shadow-1",
                    ValidPlan = true,
                    NativeValidPlan = true,
                    ShadowRecall10 = 1,
                    LegacyRecall10 = 1,
                    ShadowMrr = 1,
                    ShadowConstraintHitRate = 1,
                    ShadowSelectedMustHit = ["must-hit-router"],
                    ShadowChannelSources = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["keyword"] = ["must-hit-router"],
                        ["relations"] = ["relation-evidence"]
                    },
                    LegacyChannelSources = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["working"] = ["working-item"]
                    }
                }
            ]
        };

        var example = new LearningFeatureDatasetService()
            .GenerateRouterIntentExamples(report)
            .Single();

        Assert.AreEqual("RouterIntent", example.TaskKind);
        Assert.AreEqual("CodingTask", example.Intent);
        Assert.AreEqual("CodingTask", example.Label);
        Assert.AreEqual("RetrievalPlanProposal", example.CandidateKind);
        Assert.IsTrue(example.ChannelSources.Contains("keyword"));
        Assert.IsTrue(example.ChannelSources.Contains("relations"));
    }

    [TestMethod]
    public void ControlRoom_ShouldRenderLearningFeaturesSummary()
    {
        var snapshot = new ServiceLearningFeaturesSnapshot
        {
            CurrentTime = DateTimeOffset.Parse("2026-06-06T12:00:00Z"),
            BaseUrl = "http://localhost:5079/",
            Limit = 50,
            Offset = 0,
            Dataset = new LearningFeatureDataset
            {
                DatasetId = "learning-feature-dataset-test",
                FeatureCount = 1,
                RankingPairCount = 2,
                RouterIntentExampleCount = 3,
                LatestExportPath = "learning/features",
                PolicyVersion = LearningFeatureDatasetService.PolicyVersion,
                LabelDistribution = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    [PolicyFeedbackLabels.Positive] = 1,
                    ["CurrentTask"] = 3
                },
                SourceTypeDistribution = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["PromotionCandidateReviewRecord"] = 1,
                    ["PlanningShadowComparison"] = 3
                },
                FeatureExamples =
                [
                    CreateFeatureExample("feature-render-1", "PolicyFeedback", PolicyFeedbackLabels.Positive)
                ]
            },
            QualityReport = new LearningDatasetQualityReport
            {
                PolicyFeedbackFeatureCount = 1,
                RankingPairCount = 2,
                RouterIntentExampleCount = 3,
                PositiveCount = 1,
                NegativeCount = 0,
                NeutralCount = 0,
                DataRisks =
                [
                    LearningDatasetDataRisks.MissingNegativeSamples
                ],
                TaskReadiness = new Dictionary<string, LearningDatasetTaskReadiness>(StringComparer.OrdinalIgnoreCase)
                {
                    [LearningDatasetTaskNames.RouterIntentClassifier] = new LearningDatasetTaskReadiness
                    {
                        TaskName = LearningDatasetTaskNames.RouterIntentClassifier,
                        Ready = true,
                        Status = LearningDatasetReadinessStatus.Ready,
                        RecommendedNextAction = "offline router analysis only"
                    }
                },
                RecommendedNextAction = "Add rejected examples."
            }
        };

        var output = ServiceOperationalRenderer.RenderLearningFeatures(snapshot);

        StringAssert.Contains(output, "Service Learning Features");
        StringAssert.Contains(output, "features=1 rankingPairs=2 routerIntent=3");
        StringAssert.Contains(output, "Dataset Quality");
        StringAssert.Contains(output, "policy=1 rankingPairs=2 routerIntent=3");
        StringAssert.Contains(output, "MissingNegativeSamples");
        StringAssert.Contains(output, "RouterIntentClassifier: Ready");
        StringAssert.Contains(output, "Add rejected examples.");
        StringAssert.Contains(output, "Positive: 1");
        StringAssert.Contains(output, "PromotionCandidateReviewRecord: 1");
        StringAssert.Contains(output, "feature-render-1");
    }

    [TestMethod]
    public async Task EmptyPolicyFeedbackFile_ShouldTriggerNoPolicyFeedback()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"contextcore-learning-quality-empty-policy-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, LearningDatasetQualityReportBuilder.PolicyFeedbackFeaturesFileName), string.Empty);
            await WriteJsonLinesAsync(
                Path.Combine(outputDirectory, LearningDatasetQualityReportBuilder.RankingPairsFileName),
                [
                    CreateRankingPair("sample-quality-1", "ChatMode", "CurrentTask")
                ]);
            await WriteJsonLinesAsync(
                Path.Combine(outputDirectory, LearningDatasetQualityReportBuilder.RouterIntentExamplesFileName),
                [
                    CreateFeatureExample("router-quality-1", "RouterIntent", "CurrentTask")
                ]);

            var report = await new LearningDatasetQualityReportBuilder().BuildAsync(outputDirectory);

            Assert.AreEqual(0, report.PolicyFeedbackFeatureCount);
            Assert.AreEqual(1, report.RankingPairCount);
            Assert.AreEqual(1, report.RouterIntentExampleCount);
            CollectionAssert.Contains(report.DataRisks.ToArray(), LearningDatasetDataRisks.NoPolicyFeedback);
            CollectionAssert.Contains(report.DataRisks.ToArray(), LearningDatasetDataRisks.MissingNegativeSamples);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task EvalOnlyRankingPairs_ShouldTriggerEvalOnlyDataset()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"contextcore-learning-quality-eval-only-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, LearningDatasetQualityReportBuilder.PolicyFeedbackFeaturesFileName), string.Empty);
            await WriteJsonLinesAsync(
                Path.Combine(outputDirectory, LearningDatasetQualityReportBuilder.RankingPairsFileName),
                [
                    CreateRankingPair("sample-eval-only-1", "AutomationMode", "AutomationRecovery")
                ]);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, LearningDatasetQualityReportBuilder.RouterIntentExamplesFileName), string.Empty);

            var report = await new LearningDatasetQualityReportBuilder().BuildAsync(outputDirectory);

            Assert.AreEqual(0, report.PolicyFeedbackFeatureCount);
            Assert.AreEqual(1, report.RankingPairCount);
            Assert.AreEqual(0, report.RouterIntentExampleCount);
            CollectionAssert.Contains(report.DataRisks.ToArray(), LearningDatasetDataRisks.EvalOnlyDataset);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task JsonLineParser_ShouldHandleEmptyFiles()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"contextcore-learning-quality-empty-files-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, LearningDatasetQualityReportBuilder.PolicyFeedbackFeaturesFileName), string.Empty);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, LearningDatasetQualityReportBuilder.RankingPairsFileName), string.Empty);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, LearningDatasetQualityReportBuilder.RouterIntentExamplesFileName), string.Empty);

            var report = await new LearningDatasetQualityReportBuilder().BuildAsync(outputDirectory);

            Assert.AreEqual(0, report.PolicyFeedbackFeatureCount);
            Assert.AreEqual(0, report.RankingPairCount);
            Assert.AreEqual(0, report.RouterIntentExampleCount);
            CollectionAssert.Contains(report.DataRisks.ToArray(), LearningDatasetDataRisks.NoPolicyFeedback);
            CollectionAssert.Contains(report.DataRisks.ToArray(), LearningDatasetDataRisks.LowIntentCoverage);
            CollectionAssert.Contains(report.DataRisks.ToArray(), LearningDatasetDataRisks.LowModeCoverage);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void TaskReadiness_ShouldBeCalculatedFromCoverage()
    {
        var policyFeatures = Enumerable.Range(0, 20)
            .Select(index => CreatePolicyFeatureExample(
                $"promotion-ready-{index}",
                "PromotionCandidateReviewRecord",
                index % 2 == 0 ? PolicyFeedbackLabels.Positive : PolicyFeedbackLabels.Negative,
                index % 2 == 0,
                index % 2 != 0))
            .Concat(Enumerable.Range(0, 20)
                .Select(index => CreatePolicyFeatureExample(
                    $"constraint-ready-{index}",
                    "ConstraintGapReviewRecord",
                    index % 2 == 0 ? PolicyFeedbackLabels.Positive : PolicyFeedbackLabels.Negative,
                    index % 2 == 0,
                    index % 2 != 0)))
            .Concat(Enumerable.Range(0, 20)
                .Select(index => CreatePolicyFeatureExample(
                    $"attention-ready-{index}",
                    "AttentionReviewRecord",
                    PolicyFeedbackLabels.Positive,
                    accepted: true,
                    rejected: false)))
            .ToArray();
        var rankingPairs = Enumerable.Range(0, 100)
            .Select(index => CreateRankingPair($"sample-rank-{index}", index % 3 == 0 ? "ChatMode" : index % 3 == 1 ? "CodingMode" : "NovelMode", $"Intent{index % 4}"))
            .ToArray();
        var routerExamples = Enumerable.Range(0, 100)
            .Select(index => CreateRouterExample($"router-ready-{index}", index % 3 == 0 ? "ChatMode" : index % 3 == 1 ? "CodingMode" : "NovelMode", $"Intent{index % 4}"))
            .ToArray();

        var report = new LearningDatasetQualityReportBuilder().Build(
            policyFeatures,
            rankingPairs,
            routerExamples,
            "learning/features");

        Assert.AreEqual(LearningDatasetReadinessStatus.Ready, report.TaskReadiness[LearningDatasetTaskNames.RouterIntentClassifier].Status);
        Assert.AreEqual(LearningDatasetReadinessStatus.Ready, report.TaskReadiness[LearningDatasetTaskNames.CandidateReranker].Status);
        Assert.AreEqual(LearningDatasetReadinessStatus.Ready, report.TaskReadiness[LearningDatasetTaskNames.PromotionJudge].Status);
        Assert.AreEqual(LearningDatasetReadinessStatus.Ready, report.TaskReadiness[LearningDatasetTaskNames.ConstraintGapJudge].Status);
        Assert.AreEqual(LearningDatasetReadinessStatus.Ready, report.TaskReadiness[LearningDatasetTaskNames.AttentionScorer].Status);
        Assert.IsFalse(report.DataRisks.Contains(LearningDatasetDataRisks.NoPolicyFeedback));
        Assert.IsFalse(report.DataRisks.Contains(LearningDatasetDataRisks.MissingNegativeSamples));
    }

    private static PolicyFeedbackDataset CreatePolicyFeedbackDataset(params PolicyFeedbackRecord[] records)
        => new()
        {
            DatasetId = "policy-feedback-dataset-test",
            Name = "Policy Feedback Dataset",
            Scope = $"workspace:{WorkspaceId}/collection:{CollectionId}/session:{SessionId}",
            CreatedAt = DateTimeOffset.UtcNow,
            Records = records,
            PositiveCount = records.Count(record => record.Label == PolicyFeedbackLabels.Positive),
            NegativeCount = records.Count(record => record.Label == PolicyFeedbackLabels.Negative),
            NeutralCount = records.Count(record => record.Label == PolicyFeedbackLabels.Neutral),
            SourceTypes = records
                .GroupBy(record => record.SourceType, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            PolicyVersion = PolicyFeedbackDatasetService.PolicyVersion,
            EvalBaselineRef = PolicyFeedbackDatasetService.EvalBaselineRef
        };

    private static PolicyFeedbackRecord CreatePolicyFeedbackRecord(
        string feedbackRecordId,
        string sourceType,
        string sourceId,
        string action,
        string label,
        string targetLayer,
        Dictionary<string, string> metadata)
        => new()
        {
            FeedbackRecordId = feedbackRecordId,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            SessionId = SessionId,
            SourceType = sourceType,
            SourceId = sourceId,
            Action = action,
            Label = label,
            Reason = $"reason for {feedbackRecordId}",
            PositiveRefs = label == PolicyFeedbackLabels.Positive ? [$"positive-{feedbackRecordId}"] : [],
            NegativeRefs = label == PolicyFeedbackLabels.Negative ? [$"negative-{feedbackRecordId}"] : [],
            EvidenceRefs = [$"evidence-{feedbackRecordId}"],
            TargetLayer = targetLayer,
            CreatedAt = DateTimeOffset.UtcNow,
            Reviewer = "tester",
            PolicyVersion = PolicyFeedbackDatasetService.PolicyVersion,
            Metadata = metadata
        };

    private static ContextPolicyFeatureExample CreateFeatureExample(
        string exampleId,
        string taskKind,
        string label)
        => new()
        {
            ExampleId = exampleId,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            SourceType = taskKind == "RouterIntent"
                ? "PlanningShadowComparison"
                : "PromotionCandidateReviewRecord",
            SourceId = $"{exampleId}-source",
            TaskKind = taskKind,
            Mode = "ChatMode",
            Intent = label,
            Label = label,
            InputSummary = "feature summary",
            CandidateId = $"{exampleId}-candidate",
            CandidateKind = "Preference",
            CandidateLayer = "CandidateMemory",
            CandidateStatus = "Accepted",
            CandidateImportance = 0.8,
            CandidateRecency = 1,
            ChannelSources = ["policy-feedback"],
            Selected = true,
            Accepted = label == PolicyFeedbackLabels.Positive || taskKind == "RouterIntent",
            Rejected = label == PolicyFeedbackLabels.Negative,
            EvidenceRefs = [$"{exampleId}-evidence"],
            PolicyVersion = LearningFeatureDatasetService.PolicyVersion,
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static ContextPolicyFeatureExample CreateRouterExample(
        string exampleId,
        string mode,
        string intent)
        => new()
        {
            ExampleId = exampleId,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            SourceType = "PlanningShadowComparison",
            SourceId = $"{exampleId}-source",
            TaskKind = "RouterIntent",
            Mode = mode,
            Intent = intent,
            Label = intent,
            InputSummary = "router summary",
            CandidateId = $"{exampleId}-proposal",
            CandidateKind = "RetrievalPlanProposal",
            CandidateLayer = "Planning",
            CandidateStatus = "NativeValid",
            Selected = true,
            Accepted = true,
            EvidenceRefs = [$"{exampleId}-evidence"],
            PolicyVersion = LearningFeatureDatasetService.PolicyVersion,
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static ContextPolicyFeatureExample CreatePolicyFeatureExample(
        string exampleId,
        string sourceType,
        string label,
        bool accepted,
        bool rejected)
        => new()
        {
            ExampleId = exampleId,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            SourceType = sourceType,
            SourceId = $"{exampleId}-source",
            TaskKind = "PolicyFeedback",
            Mode = "ChatMode",
            Intent = "CurrentTask",
            Label = label,
            InputSummary = "policy feedback summary",
            CandidateId = $"{exampleId}-candidate",
            CandidateKind = "Preference",
            CandidateLayer = "CandidateMemory",
            CandidateStatus = accepted ? "Accepted" : "Rejected",
            CandidateImportance = 0.8,
            CandidateRecency = 1,
            ChannelSources = ["policy-feedback"],
            Selected = true,
            Accepted = accepted,
            Rejected = rejected,
            EvidenceRefs = [$"{exampleId}-evidence"],
            PolicyVersion = LearningFeatureDatasetService.PolicyVersion,
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static RankingPairExample CreateRankingPair(
        string sampleId,
        string mode,
        string intent)
        => new()
        {
            Query = $"query {sampleId}",
            Mode = mode,
            Intent = intent,
            PositiveCandidateId = $"{sampleId}-positive",
            NegativeCandidateId = $"{sampleId}-negative",
            Reason = "mustHit above mustNotHit",
            EvalSampleId = sampleId
        };

    private static async Task WriteJsonLinesAsync<T>(
        string path,
        IReadOnlyList<T> records)
    {
        var lines = records.Select(record => JsonSerializer.Serialize(record, JsonOptions));
        await File.WriteAllTextAsync(path, string.Join(Environment.NewLine, lines));
    }
}
