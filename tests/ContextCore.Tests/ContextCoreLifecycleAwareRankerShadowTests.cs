using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Commands;
using ContextCore.ControlRoom.Services;
using ContextCore.Core.Services;
using ContextCore.Storage.InMemory;

namespace ContextCore.Tests;

[TestClass]
public sealed class ContextCoreLifecycleAwareRankerShadowTests
{
    [TestMethod]
    public void ShadowScorer_DefaultOptions_ShouldStayDisabled()
    {
        var trace = new LifecycleAwareRankerShadowScorer().Score(
            [CreateDiagnostic("memory:active-rule-v2", score: 10, rank: 1, isMustHit: true)],
            []);

        Assert.IsFalse(trace.RankerShadowEnabled);
        Assert.AreEqual(0, trace.CandidateShadowScores.Count);
    }

    [TestMethod]
    public void ShadowScorer_ShouldComputeLifecycleAwareScore()
    {
        var scorer = new LifecycleAwareRankerShadowScorer();

        var trace = scorer.Score(
            [CreateDiagnostic("memory:active-rule-v2", score: 10, rank: 1, isMustHit: true)],
            [CreateDiagnostic("memory:deprecated-rule-v1", score: 20, rank: 2, kind: "deprecated_memory", section: "historical_context")],
            new LifecycleAwareRankerShadowOptions { Enabled = true });

        Assert.IsTrue(trace.RankerShadowEnabled);
        Assert.AreEqual(2, trace.CandidateShadowScores.Count);
        Assert.IsTrue(trace.CandidateShadowScores.Any(item => item.ScoreDelta != 0));
        Assert.IsTrue(trace.CandidateShadowScores.All(item => !string.IsNullOrWhiteSpace(item.Reason)));
    }

    [TestMethod]
    public void ShadowScorer_ShouldNotChangeSelectedSet()
    {
        var selected = new[]
        {
            CreateDiagnostic("memory:active-rule-v2", score: 10, rank: 1, isMustHit: true),
            CreateDiagnostic("memory:active-task", score: 9, rank: 2)
        };
        var originalIds = selected.Select(static item => item.ItemId).ToArray();

        var trace = new LifecycleAwareRankerShadowScorer().Score(
            selected,
            [CreateDiagnostic("memory:deprecated-rule-v1", score: 20, rank: 3, kind: "deprecated_memory", section: "historical_context")],
            new LifecycleAwareRankerShadowOptions { Enabled = true });

        CollectionAssert.AreEqual(originalIds, selected.Select(static item => item.ItemId).ToArray());
        CollectionAssert.AreEqual(
            originalIds,
            trace.CandidateShadowScores
                .Where(static item => item.Selected)
                .OrderBy(static item => item.LegacyRank)
                .Select(static item => item.CandidateId)
                .ToArray());
    }

    [TestMethod]
    public void ShadowScorer_DeprecatedCandidate_ShouldReceiveLowerLifecycleAwareScore()
    {
        var trace = new LifecycleAwareRankerShadowScorer().Score(
            [CreateDiagnostic("memory:active-rule-v2", score: 10, rank: 1, isMustHit: true)],
            [CreateDiagnostic("memory:deprecated-rule-v1", score: 20, rank: 2, kind: "deprecated_memory", section: "historical_context")],
            new LifecycleAwareRankerShadowOptions { Enabled = true });

        var deprecated = trace.CandidateShadowScores.Single(item => item.CandidateId == "memory:deprecated-rule-v1");

        Assert.IsTrue(deprecated.LifecycleAwareScore < deprecated.LegacyScore);
        Assert.IsTrue(deprecated.LifecycleFeatures.IsDeprecated);
        Assert.IsTrue(trace.DeprecatedDemotions.Any(item => item.CandidateId == deprecated.CandidateId));
    }

    [TestMethod]
    public void LifecycleRankerShadowReportBuilder_ShouldGenerateReport()
    {
        var report = LifecycleAwareRankerShadowReportBuilder.Build(
            new ContextEvalReport
            {
                Results =
                [
                    CreateEvalResult()
                ]
            },
            includeSeedBatches: false);

        Assert.AreEqual(1, report.TotalSamples);
        Assert.AreEqual(0, report.FormalOutputChanged);
        Assert.AreEqual(0, report.SelectedSetChanged);
        Assert.AreEqual(LifecycleAwareRankerShadowScorer.PolicyVersion, report.PolicyVersion);
        Assert.IsTrue(report.Samples[0].Trace.RankerShadowEnabled);
        Assert.IsTrue(report.Samples[0].Trace.CandidateShadowScores.Count >= 2);
    }

    [TestMethod]
    public void LifecycleRankerShadowReportBuilder_FormalOutputShouldRemainUnchanged()
    {
        var result = CreateEvalResult();
        var report = LifecycleAwareRankerShadowReportBuilder.Build(
            new ContextEvalReport
            {
                Results = [result]
            },
            includeSeedBatches: false);
        var sample = report.Samples.Single();

        CollectionAssert.AreEqual(result.SelectedIds.ToArray(), sample.LegacySelectedIds.ToArray());
        CollectionAssert.AreEqual(result.SelectedIds.ToArray(), sample.ShadowSelectedIds.ToArray());
        Assert.IsFalse(sample.FormalOutputChanged);
        Assert.IsFalse(sample.SelectedSetChanged);
    }

    [TestMethod]
    public async Task RankerShadowDebugService_ShouldReturnScoresWithoutChangingOutput()
    {
        var service = new LifecycleAwareRankerDebugService(
            new FakeRankerDebugRetriever(),
            new LifecycleAwareRankerShadowScorer());

        var response = await service.DebugAsync(new LifecycleAwareRankerShadowDebugRequest
        {
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            Query = "prefer current rule",
            Mode = "ChatMode"
        });

        Assert.IsFalse(response.FormalOutputChanged);
        Assert.IsFalse(response.SelectedSetChanged);
        CollectionAssert.AreEqual(response.LegacySelectedIds.ToArray(), response.FinalSelectedIds.ToArray());
        Assert.IsTrue(response.CandidateScores.Count >= 2);
        Assert.IsTrue(response.DeprecatedDemotions.Any(item => item.CandidateId == "memory:deprecated-rule-v1"));
        Assert.IsTrue(response.CurrentActivePromotions.Any(item => item.CandidateId == "memory:active-rule-v2"));
    }

    [TestMethod]
    public void RankerShadowTraceBuilder_ShouldRecordDemotionReasonsForDeprecatedCandidate()
    {
        var builder = new LifecycleAwareRankerTraceBuilder(new LifecycleAwareRankerShadowScorer());
        var selected = CreateRetrievalCandidate(
            "memory:active-rule-v2",
            score: 10,
            metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["memoryLayer"] = "Stable",
                ["lifecycleStatus"] = "Stable",
                ["status"] = "Active",
                ["version"] = "v2"
            });
        var deprecated = CreateRetrievalCandidate(
            "memory:deprecated-rule-v1",
            score: 20,
            metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["memoryLayer"] = "historical_context",
                ["lifecycleStatus"] = "Deprecated",
                ["section"] = "historical_context",
                ["version"] = "v1"
            });

        var trace = builder.Build(
            [selected],
            [ToDecision(deprecated)],
            [selected, deprecated],
            new LifecycleAwareRankerShadowOptions
            {
                Enabled = true,
                TraceCollectionEnabled = true,
                Profile = "lifecycle-aware-v1",
                MaxCandidatesPerTrace = 50
            });

        var deprecatedScore = trace.CandidateShadowScores.Single(item => item.CandidateId == "memory:deprecated-rule-v1");
        Assert.IsTrue(deprecatedScore.ScoreDelta < 0);
        CollectionAssert.Contains(deprecatedScore.DemotionReasons.ToArray(), "deprecated_demotion");
        Assert.IsTrue(trace.DeprecatedDemotions.Any(item => item.CandidateId == "memory:deprecated-rule-v1"));
    }

    [TestMethod]
    public async Task RankerShadowTraceExportService_ShouldReturnJsonLinesCompatibleRecords()
    {
        var store = new InMemoryRetrievalTraceStore();
        await store.SaveAsync(new ContextRetrievalTrace
        {
            RetrievalId = "retrieval-shadow-1",
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            QueryText = "current rule",
            CreatedAt = DateTimeOffset.UtcNow,
            RankerShadowTrace = new LifecycleAwareRankerShadowTrace
            {
                RankerShadowEnabled = true,
                RankerShadowProfile = "lifecycle-aware-v1",
                CandidateShadowScores =
                [
                    new LifecycleAwareRankerShadowCandidateScore
                    {
                        CandidateId = "memory:deprecated-rule-v1",
                        LegacyScore = 20,
                        LifecycleAwareScore = -18,
                        ScoreDelta = -38,
                        Reason = "deprecated_demotion",
                        DemotionReasons = ["deprecated_demotion"]
                    }
                ],
                DeprecatedDemotions =
                [
                    new LifecycleAwareRankerShadowCandidateScore
                    {
                        CandidateId = "memory:deprecated-rule-v1",
                        ScoreDelta = -38,
                        Reason = "deprecated_demotion",
                        DemotionReasons = ["deprecated_demotion"]
                    }
                ]
            }
        });
        var export = new RankerShadowTraceExportService(store);

        var records = await export.QueryAsync("workspace-1", "collection-1", take: 10);
        var jsonl = await export.ExportJsonLinesAsync("workspace-1", "collection-1", take: 10);

        Assert.AreEqual(1, records.Count);
        Assert.AreEqual("retrieval-shadow-1", records[0].RetrievalId);
        StringAssert.Contains(jsonl, "\"retrievalId\":\"retrieval-shadow-1\"");
        StringAssert.Contains(jsonl, "\"candidateId\":\"memory:deprecated-rule-v1\"");
        Assert.IsFalse(jsonl.Contains(Environment.NewLine + Environment.NewLine, StringComparison.Ordinal));
    }

    [TestMethod]
    public void RankerShadowTraceQualityReportBuilder_EmptyTrace_ShouldRecommendNeedsMoreRealTraces()
    {
        var report = new RankerShadowTraceQualityReportBuilder().Build(
            Array.Empty<LifecycleAwareRankerShadowTraceRecord>(),
            "workspace-1",
            "collection-1");

        Assert.AreEqual(0, report.TraceCount);
        Assert.AreEqual(0, report.CandidateScoreCount);
        Assert.AreEqual(RankerShadowTraceRecommendedNextSteps.NeedsMoreRealTraces, report.RecommendedNextStep);
    }

    [TestMethod]
    public void RankerShadowTraceQualityReportBuilder_ShouldCountDemotionsAndRisks()
    {
        var report = new RankerShadowTraceQualityReportBuilder().Build(
            [
                CreateTraceRecord(
                    [
                        new LifecycleAwareRankerShadowCandidateScore
                        {
                            CandidateId = "memory:deprecated-rule-v1",
                            IsMustHit = true,
                            LegacyScore = 20,
                            LifecycleAwareScore = -18,
                            ScoreDelta = -38,
                            Reason = "deprecated_demotion;historical_demotion",
                            DemotionReasons = ["deprecated_demotion", "historical_demotion"],
                            LifecycleFeatures = new LifecycleAwareFeatureSet
                            {
                                IsDeprecated = true,
                                IsHistorical = true
                            }
                        },
                        new LifecycleAwareRankerShadowCandidateScore
                        {
                            CandidateId = "memory:must-not-hit-current",
                            IsMustNotHit = true,
                            LegacyScore = 5,
                            LifecycleAwareScore = 17,
                            ScoreDelta = 12,
                            Reason = "current_version_boost",
                            PromotionReasons = ["current_version_boost"],
                            LifecycleFeatures = new LifecycleAwareFeatureSet
                            {
                                IsCurrentVersion = true
                            }
                        }
                    ])
            ],
            "workspace-1",
            "collection-1");

        Assert.AreEqual(1, report.TraceCount);
        Assert.AreEqual(2, report.CandidateScoreCount);
        Assert.AreEqual(1, report.DeprecatedDemotionCount);
        Assert.AreEqual(1, report.HistoricalDemotionCount);
        Assert.AreEqual(1, report.CurrentVersionPromotionCount);
        Assert.AreEqual(1, report.MustHitDemotedCount);
        Assert.AreEqual(1, report.MustNotHitPromotedCount);
        Assert.AreEqual(RankerShadowTraceRecommendedNextSteps.NeedsMoreRealTraces, report.RecommendedNextStep);
        Assert.AreEqual(2, report.RiskSamples.Count);
        Assert.IsTrue(report.ModeBreakdown.ContainsKey("ChatMode"));
        Assert.IsTrue(report.IntentBreakdown.ContainsKey("CurrentTask"));
    }

    [TestMethod]
    public void RankerShadowTraceQualityReportBuilder_NonEmptySafeTraceFixture_ShouldBecomeReady()
    {
        var records = Enumerable.Range(0, 30)
            .Select(index => CreateTraceRecord(
                [
                    new LifecycleAwareRankerShadowCandidateScore
                    {
                        CandidateId = $"memory:deprecated-rule-v{index}",
                        Selected = false,
                        LegacyScore = 20,
                        LifecycleAwareScore = -2,
                        ScoreDelta = -22,
                        Reason = "deprecated_demotion",
                        DemotionReasons = ["deprecated_demotion"],
                        LifecycleFeatures = new LifecycleAwareFeatureSet
                        {
                            IsDeprecated = true
                        }
                    },
                    new LifecycleAwareRankerShadowCandidateScore
                    {
                        CandidateId = $"memory:current-rule-v{index}",
                        Selected = true,
                        IsMustHit = true,
                        LegacyScore = 10,
                        LifecycleAwareScore = 22,
                        ScoreDelta = 12,
                        Reason = "current_version_boost",
                        PromotionReasons = ["current_version_boost"],
                        LifecycleFeatures = new LifecycleAwareFeatureSet
                        {
                            IsCurrentVersion = true
                        }
                    }
                ]))
            .ToArray();

        var report = new RankerShadowTraceQualityReportBuilder().Build(records, "workspace-1", "collection-1");

        Assert.AreEqual(30, report.TraceCount);
        Assert.AreEqual(60, report.CandidateScoreCount);
        Assert.AreEqual(30, report.DeprecatedDemotionCount);
        Assert.AreEqual(0, report.MustHitDemotedCount);
        Assert.AreEqual(0, report.MustNotHitPromotedCount);
        Assert.AreEqual(0, report.LifecycleViolationCount);
        Assert.AreEqual(RankerShadowTraceRecommendedNextSteps.ReadyForGuardedOptIn, report.RecommendedNextStep);
    }

    [TestMethod]
    public async Task EvalCommand_RankerShadowTraceQuality_ShouldWriteReportFiles()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "contextcore-ranker-shadow-quality-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var state = ControlRoomService.CreateState(
                "memory",
                tempRoot,
                "workspace-1",
                "collection-1");
            await state.RetrievalTraceStore.SaveAsync(new ContextRetrievalTrace
            {
                RetrievalId = "retrieval-quality-1",
                WorkspaceId = "workspace-1",
                CollectionId = "collection-1",
                QueryText = "current rule",
                CreatedAt = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["rankerShadowQueryMode"] = "ChatMode",
                    ["planningIntent"] = "CurrentTask",
                    ["rankerShadowFormalOutputChanged"] = "false"
                },
                RankerShadowTrace = new LifecycleAwareRankerShadowTrace
                {
                    RankerShadowEnabled = true,
                    RankerShadowProfile = "lifecycle-aware-v1",
                    CandidateShadowScores =
                    [
                        new LifecycleAwareRankerShadowCandidateScore
                        {
                            CandidateId = "memory:deprecated-rule-v1",
                            ScoreDelta = -38,
                            Reason = "deprecated_demotion",
                            DemotionReasons = ["deprecated_demotion"],
                            LifecycleFeatures = new LifecycleAwareFeatureSet
                            {
                                IsDeprecated = true
                            }
                        }
                    ]
                }
            });
            var service = new ControlRoomService(state);
            var jsonPath = Path.Combine(tempRoot, "quality.json");
            var markdownPath = Path.Combine(tempRoot, "quality.md");

            await EvalCommand.ExecuteAsync(
                service,
                [
                    "ranker-shadow-trace-quality",
                    "--workspace", "workspace-1",
                    "--collection", "collection-1",
                    "--take", "10",
                    "--out", jsonPath,
                    "--md-out", markdownPath
                ]);

            Assert.IsTrue(File.Exists(jsonPath));
            Assert.IsTrue(File.Exists(markdownPath));
            StringAssert.Contains(await File.ReadAllTextAsync(jsonPath), "\"TraceCount\": 1");
            StringAssert.Contains(await File.ReadAllTextAsync(markdownPath), "Ranker Shadow Trace Quality Report");
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
    public void CandidateRerankerShadowOptions_ShouldStayDisabledByDefault()
    {
        var options = new CandidateRerankerShadowOptions();

        Assert.IsFalse(options.Enabled);
        Assert.IsFalse(options.TraceCollectionEnabled);
        Assert.AreEqual("LifecycleAwareFeatureBaseline", options.ShadowRanker);
    }

    [TestMethod]
    public void CandidateRerankerShadowTrace_ShouldRecordTopKChangeWithoutFormalOutputChange()
    {
        var trace = new LifecycleAwareRankerShadowScorer().Score(
            [CreateDiagnostic("memory:active-rule-v2", score: 10, rank: 1, isMustHit: true)],
            [CreateDiagnostic("memory:current-rule-v2", score: 9, rank: 2, section: "stable_memory")],
            new LifecycleAwareRankerShadowOptions { Enabled = true });

        var candidateTrace = CandidateRerankerShadowTraceFactory.Build(
            "request-1",
            "ChatMode",
            "CurrentTask",
            "current rule",
            trace,
            recordTopK: 2);

        Assert.IsFalse(candidateTrace.FormalOutputChanged);
        Assert.IsTrue(candidateTrace.CandidateCount >= 2);
        Assert.AreEqual(2, candidateTrace.FormalTopCandidates.Count);
        Assert.AreEqual(2, candidateTrace.ShadowTopCandidates.Count);
    }

    [TestMethod]
    public void CandidateRerankerShadowEval_RiskyCandidateShouldNotBecomeSafeImprovement()
    {
        var report = new CandidateRerankerShadowEvalRunner().Build(
            new ContextEvalReport
            {
                Results =
                [
                    new ContextEvalResult
                    {
                        SampleId = "sample-risk",
                        Query = "current rule",
                        Mode = "ChatMode",
                        MustHit = ["memory:active-rule-v2"],
                        MustNotHit = ["memory:deprecated-rule-v1"],
                        SelectedIds = ["memory:active-rule-v2"],
                        ExcludedIds = ["memory:deprecated-rule-v1"],
                        SelectedItemDiagnostics =
                        [
                            CreateDiagnostic("memory:active-rule-v2", score: 10, rank: 1, isMustHit: true)
                        ],
                        DroppedItemDiagnostics =
                        [
                            CreateDiagnostic(
                                "memory:deprecated-rule-v1",
                                score: 50,
                                rank: 2,
                                kind: "deprecated_memory",
                                section: "historical_context",
                                isMustNotHit: true)
                        ]
                    }
                ]
            },
            "test");

        var sample = report.SampleResults.Single();
        Assert.IsFalse(sample.WouldImprove);
        Assert.IsTrue(sample.WouldRegress
            || sample.DeprecatedRiskCount > 0
            || sample.MustNotRiskCount > 0
            || sample.RiskCandidateBlockedBeforeRerank > 0);
    }

    [TestMethod]
    public void CandidateRerankerTraceQuality_EmptyTrace_ShouldRecommendNeedsMoreRealTraces()
    {
        var report = new CandidateRerankerShadowTraceQualityReportBuilder().Build(
            Array.Empty<LifecycleAwareRankerShadowTraceRecord>(),
            "workspace-1",
            "collection-1");

        Assert.AreEqual(0, report.TraceCount);
        Assert.AreEqual(CandidateRerankerShadowRecommendations.NeedsMoreRealTraces, report.Recommendation);
    }

    [TestMethod]
    public void CandidateRerankerShadowEval_ShouldNotUseSampleIdForDecision()
    {
        var first = BuildCandidateRerankerNoSpecialCaseReport("sample-a");
        var second = BuildCandidateRerankerNoSpecialCaseReport("sample-b");

        Assert.AreEqual(first.Recommendation, second.Recommendation);
        Assert.AreEqual(first.NetGain, second.NetGain);
        Assert.AreEqual(first.WouldImproveCount, second.WouldImproveCount);
        Assert.AreEqual(first.WouldRegressCount, second.WouldRegressCount);
    }

    [TestMethod]
    public void CandidateRerankerScoreContract_HigherScore_ShouldRankFirst()
    {
        var trace = new LifecycleAwareRankerShadowScorer().Score(
            [CreateDiagnostic("memory:lower-score", score: 10, rank: 1)],
            [CreateDiagnostic("memory:higher-score", score: 20, rank: 2)],
            new LifecycleAwareRankerShadowOptions { Enabled = true });

        var higher = trace.CandidateShadowScores.Single(item => item.CandidateId == "memory:higher-score");
        var lower = trace.CandidateShadowScores.Single(item => item.CandidateId == "memory:lower-score");

        Assert.IsTrue(higher.ShadowRank < lower.ShadowRank);
        Assert.AreEqual("HigherScoreRanksFirst", CandidateRerankerShadowAuditRules.ResolveScoreDirection(trace.CandidateShadowScores));
    }

    [TestMethod]
    public void CandidateRerankerScoreContract_DeprecatedCandidate_ShouldNotOutrankActiveEquivalent()
    {
        var trace = new LifecycleAwareRankerShadowScorer().Score(
            [CreateDiagnostic("memory:deprecated-rule-v1", score: 20, rank: 1, kind: "deprecated_memory", section: "historical_context")],
            [CreateDiagnostic("memory:active-rule-v2", score: 10, rank: 2)],
            new LifecycleAwareRankerShadowOptions { Enabled = true });

        var deprecated = trace.CandidateShadowScores.Single(item => item.CandidateId == "memory:deprecated-rule-v1");
        var active = trace.CandidateShadowScores.Single(item => item.CandidateId == "memory:active-rule-v2");

        Assert.IsTrue(active.ShadowRank < deprecated.ShadowRank);
        Assert.IsTrue(deprecated.ScoreDelta < 0);
    }

    [TestMethod]
    public void CandidateRerankerScoreContract_LifecyclePenaltyDirection_ShouldBeNegative()
    {
        var trace = new LifecycleAwareRankerShadowScorer().Score(
            [CreateDiagnostic("memory:active-rule-v2", score: 10, rank: 1)],
            [CreateDiagnostic("memory:deprecated-rule-v1", score: 10, rank: 2, kind: "deprecated_memory", section: "historical_context")],
            new LifecycleAwareRankerShadowOptions { Enabled = true });

        var active = trace.CandidateShadowScores.Single(item => item.CandidateId == "memory:active-rule-v2");
        var deprecated = trace.CandidateShadowScores.Single(item => item.CandidateId == "memory:deprecated-rule-v1");

        Assert.IsTrue(active.ScoreDelta > 0);
        Assert.IsTrue(deprecated.ScoreDelta < 0);
    }

    [TestMethod]
    public void CandidateRerankerScoreContract_MissingLifecycleMetadata_ShouldNotBePositive()
    {
        var trace = new LifecycleAwareRankerShadowScorer().Score(
            [CreateDiagnostic("memory:plain-candidate", score: 10, rank: 1)],
            [],
            new LifecycleAwareRankerShadowOptions { Enabled = true });

        var plain = trace.CandidateShadowScores.Single();

        Assert.AreEqual(0, plain.ScoreDelta);
        Assert.AreEqual(0, plain.PromotionReasons.Count);
        Assert.IsFalse(CandidateRerankerShadowAuditRules.HasLifecycleMetadata(plain));
    }

    [TestMethod]
    public void CandidateRerankerShadowFailureAudit_ShouldRecordRegressionReasonAndScoreContract()
    {
        var report = new CandidateRerankerShadowFailureAuditRunner().Build(
            new ContextEvalReport
            {
                Results =
                [
                    new ContextEvalResult
                    {
                        SampleId = "sample-audit",
                        Query = "current rule",
                        Mode = "ChatMode",
                        MustHit = ["memory:active-rule-v2"],
                        MustNotHit = ["memory:must-not-hit-current-v2"],
                        SelectedItemDiagnostics =
                        [
                            CreateDiagnostic("memory:active-rule-v2", score: 10, rank: 1, isMustHit: true)
                        ],
                        DroppedItemDiagnostics =
                        [
                            CreateDiagnostic(
                                "memory:must-not-hit-current-v2",
                                score: 50,
                                rank: 2,
                                isMustNotHit: true,
                                sourceRefs: ["review:stable-2"])
                        ]
                    }
                ]
            },
            "test");

        Assert.AreEqual(1, report.RegressionCount);
        Assert.AreEqual(CandidateRerankerScoreContractStatuses.NeedsAudit, report.ScoreContractStatus);
        Assert.IsTrue(report.RiskCandidateInShadowTopK > 0);
        var regression = report.Regressions.Single();
        Assert.AreEqual(CandidateRerankerEligibilityStatuses.Rankable, regression.EligibilityStatus);
        Assert.IsFalse(string.IsNullOrWhiteSpace(regression.WhyShadowPromoted));
        Assert.IsFalse(string.IsNullOrWhiteSpace(regression.RecommendedAction));
    }

    [TestMethod]
    public void CandidateRerankerShadowEval_ShouldExposeScoreContractSummary()
    {
        var report = new CandidateRerankerShadowEvalRunner().Build(
            new ContextEvalReport
            {
                Results =
                [
                    new ContextEvalResult
                    {
                        SampleId = "sample-contract",
                        Query = "current rule",
                        Mode = "ChatMode",
                        MustHit = ["memory:active-rule-v2"],
                        MustNotHit = ["memory:must-not-hit-current-v2"],
                        SelectedItemDiagnostics =
                        [
                            CreateDiagnostic("memory:active-rule-v2", score: 10, rank: 1, isMustHit: true)
                        ],
                        DroppedItemDiagnostics =
                        [
                            CreateDiagnostic(
                                "memory:must-not-hit-current-v2",
                                score: 50,
                                rank: 2,
                                isMustNotHit: true,
                                sourceRefs: ["review:stable-2"])
                        ]
                    }
                ]
            },
            "test");

        Assert.AreEqual(CandidateRerankerScoreContractStatuses.NeedsAudit, report.ScoreContractStatus);
        Assert.IsTrue(report.RiskCandidateInShadowTopK > 0);
        Assert.IsTrue(report.RankableCandidateCount >= 0);
        Assert.IsTrue(report.RegressionReasonSummary.Count > 0);
    }

    [TestMethod]
    public void CandidateFeatureEnvelopeBuilder_ShouldBuildCompleteEnvelope()
    {
        var envelope = new CandidateFeatureEnvelopeBuilder().Build(CreateDiagnostic(
            "candidate-safe",
            score: 10,
            rank: 1,
            kind: "stable_memory",
            section: "stable_context",
            sourceRefs: ["review:stable-1"]));

        Assert.AreEqual("stable_context", envelope.Layer);
        Assert.AreEqual("Active", envelope.Lifecycle);
        Assert.IsFalse(string.IsNullOrWhiteSpace(envelope.ReviewStatus));
        Assert.IsTrue(envelope.FeatureCompleteness >= 0.75);
    }

    [TestMethod]
    public void RankerEligibilityGuard_MissingLifecycleCandidate_ShouldBeBlocked()
    {
        var envelope = new CandidateFeatureEnvelopeBuilder().Build(CreateDiagnostic(
            "candidate-unknown",
            score: 10,
            rank: 1,
            kind: "unknown_kind",
            section: "unknown_section"));

        var decision = new RankerCandidateEligibilityGuard().EvaluateOne(envelope);

        Assert.AreEqual(CandidateRerankerEligibilityStatuses.Blocked, decision.Status);
        CollectionAssert.Contains(decision.BlockedReasons.ToArray(), CandidateRerankerBlockedReasons.MissingLifecycleMetadata);
    }

    [TestMethod]
    public void RankerEligibilityGuard_DeprecatedCandidate_ShouldBeAuditOnly()
    {
        var envelope = new CandidateFeatureEnvelopeBuilder().Build(CreateDiagnostic(
            "candidate-deprecated",
            score: 10,
            rank: 1,
            kind: "stable_memory",
            section: "deprecated_context",
            sourceRefs: ["review:stable-1"]));

        var decision = new RankerCandidateEligibilityGuard().EvaluateOne(envelope);

        Assert.AreEqual(CandidateRerankerEligibilityStatuses.AuditOnly, decision.Status);
        CollectionAssert.Contains(decision.BlockedReasons.ToArray(), CandidateRerankerBlockedReasons.DeprecatedCandidateBlocked);
    }

    [TestMethod]
    public void RankerEligibilityGuard_SupersededCandidate_ShouldBeBlocked()
    {
        var envelope = new CandidateFeatureEnvelopeBuilder().Build(CreateDiagnostic(
            "candidate-superseded",
            score: 10,
            rank: 1,
            kind: "stable_memory",
            section: "superseded_context",
            sourceRefs: ["review:stable-1"]));

        var decision = new RankerCandidateEligibilityGuard().EvaluateOne(envelope);

        Assert.AreEqual(CandidateRerankerEligibilityStatuses.Blocked, decision.Status);
        CollectionAssert.Contains(decision.BlockedReasons.ToArray(), CandidateRerankerBlockedReasons.SupersededCandidateBlocked);
        CollectionAssert.Contains(decision.BlockedReasons.ToArray(), CandidateRerankerBlockedReasons.MissingReplacementMetadata);
    }

    [TestMethod]
    public void CandidateRerankerShadowEval_RiskCandidate_ShouldNotEnterShadowTopKAfterGuard()
    {
        var report = new CandidateRerankerShadowEvalRunner().Build(
            new ContextEvalReport
            {
                Results =
                [
                    new ContextEvalResult
                    {
                        SampleId = "sample-guard",
                        Query = "current rule",
                        Mode = "ChatMode",
                        MustHit = ["candidate-safe"],
                        SelectedItemDiagnostics =
                        [
                            CreateDiagnostic(
                                "candidate-safe",
                                score: 10,
                                rank: 1,
                                kind: "stable_memory",
                                section: "stable_context",
                                isMustHit: true,
                                sourceRefs: ["review:stable-1"])
                        ],
                        DroppedItemDiagnostics =
                        [
                            CreateDiagnostic(
                                "candidate-deprecated",
                                score: 100,
                                rank: 2,
                                kind: "stable_memory",
                                section: "deprecated_context",
                                sourceRefs: ["review:stable-2"])
                        ]
                    }
                ]
            },
            "test");

        var sample = report.SampleResults.Single();
        Assert.AreEqual(0, sample.RiskCandidateInShadowTopK);
        Assert.IsTrue(sample.RiskCandidateBlockedBeforeRerank > 0);
        Assert.AreEqual("candidate-safe", sample.ShadowTopCandidateId);
    }

    [TestMethod]
    public void CandidateRerankerFeatureCompletenessRunner_ShouldGenerateSummary()
    {
        var report = new CandidateRerankerFeatureCompletenessRunner().Build(
            new ContextEvalReport
            {
                Results =
                [
                    new ContextEvalResult
                    {
                        SampleId = "sample-feature",
                        Query = "current rule",
                        Mode = "ChatMode",
                        SelectedItemDiagnostics =
                        [
                            CreateDiagnostic(
                                "candidate-safe",
                                score: 10,
                                rank: 1,
                                kind: "stable_memory",
                                section: "stable_context",
                                sourceRefs: ["review:stable-1"])
                        ],
                        DroppedItemDiagnostics =
                        [
                            CreateDiagnostic(
                                "candidate-deprecated",
                                score: 100,
                                rank: 2,
                                kind: "stable_memory",
                                section: "deprecated_context",
                                sourceRefs: ["review:stable-2"])
                        ]
                    }
                ]
            },
            "test");

        Assert.AreEqual(1, report.Samples);
        Assert.AreEqual(2, report.RawCandidateCount);
        Assert.AreEqual(1, report.RankableCandidateCount);
        Assert.AreEqual(1, report.AuditOnlyCandidateCount);
        Assert.AreEqual(CandidateRerankerEligibilityGuardStatuses.Guarded, report.EligibilityGuardStatus);
    }

    [TestMethod]
    public void CandidateRerankerScoreDistribution_ShouldComputeMetrics()
    {
        var report = new CandidateRerankerScoreDistributionRunner().BuildFromShadowReport(
            CreateCalibrationShadowReport("calibration-score", lowMargin: false, priorityMismatch: false),
            "test");

        Assert.AreEqual(1, report.Samples);
        Assert.AreEqual(3, report.CandidateCount);
        Assert.IsTrue(report.ScoreMax > report.ScoreMin);
        Assert.IsTrue(report.ScoreStdDev > 0);
        Assert.IsTrue(report.Top1MarginAverage > 0);
        Assert.IsTrue(report.FeatureContributionByType.Count > 0);
    }

    [TestMethod]
    public void CandidateRerankerListwiseCalibration_LowMargin_ShouldBeClassified()
    {
        var report = new CandidateRerankerListwiseCalibrationRunner().BuildFromShadowReport(
            CreateCalibrationShadowReport("calibration-low-margin", lowMargin: true, priorityMismatch: false),
            "test");

        Assert.AreEqual(1, report.LowMarginDecisionCount);
        Assert.AreEqual(CandidateRerankerCalibrationIssues.LowMarginAmbiguity, report.SampleResults[0].CalibrationIssue);
    }

    [TestMethod]
    public void CandidateRerankerListwiseCalibration_PairwiseMismatch_ShouldBeClassified()
    {
        var report = new CandidateRerankerListwiseCalibrationRunner().BuildFromShadowReport(
            CreateCalibrationShadowReport("calibration-pairwise", lowMargin: false, priorityMismatch: false),
            "test");

        Assert.AreEqual(CandidateRerankerCalibrationIssues.PairwiseToListwiseMismatch, report.SampleResults[0].CalibrationIssue);
    }

    [TestMethod]
    public void CandidateRerankerListwiseCalibration_FormalPriorityComparison_ShouldNotChangeFormalOutput()
    {
        var report = new CandidateRerankerListwiseCalibrationRunner().BuildFromShadowReport(
            CreateCalibrationShadowReport("calibration-priority", lowMargin: false, priorityMismatch: true),
            "test");

        Assert.IsFalse(report.FormalOutputChanged);
        Assert.IsTrue(report.FormalPriorityMismatchCount > 0);
        Assert.AreEqual(CandidateRerankerCalibrationIssues.FormalRankingHasImplicitPriority, report.SampleResults[0].CalibrationIssue);
    }

    [TestMethod]
    public void CandidateRerankerCalibration_ShouldNotDependOnSampleId()
    {
        var first = new CandidateRerankerListwiseCalibrationRunner().BuildFromShadowReport(
            CreateCalibrationShadowReport("calibration-a", lowMargin: false, priorityMismatch: false),
            "test");
        var second = new CandidateRerankerListwiseCalibrationRunner().BuildFromShadowReport(
            CreateCalibrationShadowReport("calibration-b", lowMargin: false, priorityMismatch: false),
            "test");

        Assert.AreEqual(first.SampleResults[0].CalibrationIssue, second.SampleResults[0].CalibrationIssue);
        Assert.AreEqual(first.SampleResults[0].Top1Margin, second.SampleResults[0].Top1Margin);
    }

    [TestMethod]
    public void FormalPriorityFeatureExtractor_ShouldBeStable()
    {
        var extractor = new FormalPriorityFeatureExtractor();
        var first = extractor.Extract(CreateCandidateScore(
            "candidate-feature-a",
            legacyRank: 1,
            shadowRank: 1,
            score: 10,
            section: "constraints",
            isMustHit: true));
        var second = extractor.Extract(CreateCandidateScore(
            "candidate-feature-b",
            legacyRank: 1,
            shadowRank: 1,
            score: 10,
            section: "constraints",
            isMustHit: false));

        Assert.AreEqual(first.LayerPriority, second.LayerPriority);
        Assert.AreEqual(first.ConstraintRelevance, second.ConstraintRelevance);
        Assert.AreEqual(first.PackagePolicyPriority, second.PackagePolicyPriority);
    }

    [TestMethod]
    public void CandidateRerankerShadowEval_WithAbstain_ShouldNotChangeFormalOutput()
    {
        var report = new CandidateRerankerShadowEvalRunner().Build(
            CreateLowMarginEvalReport("low-margin-a"),
            "test",
            new CandidateRerankerShadowOptions
            {
                ShadowProfile = CandidateRerankerShadowProfiles.FormalPriorityAwareWithAbstainV1,
                RecordTopK = 10
            });

        Assert.AreEqual(1, report.AbstainCount);
        Assert.AreEqual(0, report.WouldApplyCount);
        Assert.AreEqual(0, report.NetGainAfterAbstain);
        Assert.IsFalse(report.SampleResults[0].Trace.FormalOutputChanged);
    }

    [TestMethod]
    public void CandidateRerankerFormalPriorityAlignment_ShouldReportRecoveryAndUnexplained()
    {
        var report = new CandidateRerankerFormalPriorityAlignmentRunner().BuildFromShadowReports(
            CreateAlignmentShadowReport("alignment-a", baselineRegressed: true, recovered: false, abstained: false),
            CreateAlignmentShadowReport("alignment-a", baselineRegressed: false, recovered: true, abstained: false),
            CreateAlignmentShadowReport("alignment-a", baselineRegressed: true, recovered: false, abstained: true),
            "test");

        Assert.AreEqual(1, report.RegressionCount);
        Assert.AreEqual(1, report.RecoveredCount);
        Assert.AreEqual(0, report.UnexplainedMismatchCount);
        Assert.AreEqual(1, report.AbstainCount);
        Assert.IsTrue(report.RecoveredByConstraintRelevance > 0);
    }

    [TestMethod]
    public void CandidateRerankerFormalPriorityAlignment_ShouldNotDependOnSampleId()
    {
        var first = new CandidateRerankerFormalPriorityAlignmentRunner().BuildFromShadowReports(
            CreateAlignmentShadowReport("alignment-a", baselineRegressed: true, recovered: false, abstained: false),
            CreateAlignmentShadowReport("alignment-a", baselineRegressed: false, recovered: true, abstained: false),
            CreateAlignmentShadowReport("alignment-a", baselineRegressed: true, recovered: false, abstained: true),
            "test");
        var second = new CandidateRerankerFormalPriorityAlignmentRunner().BuildFromShadowReports(
            CreateAlignmentShadowReport("alignment-b", baselineRegressed: true, recovered: false, abstained: false),
            CreateAlignmentShadowReport("alignment-b", baselineRegressed: false, recovered: true, abstained: false),
            CreateAlignmentShadowReport("alignment-b", baselineRegressed: true, recovered: false, abstained: true),
            "test");

        Assert.AreEqual(first.RecoveredCount, second.RecoveredCount);
        Assert.AreEqual(first.UnexplainedMismatchCount, second.UnexplainedMismatchCount);
        Assert.AreEqual(first.AbstainCount, second.AbstainCount);
    }

    private static ContextEvalResult CreateEvalResult()
    {
        return new ContextEvalResult
        {
            SampleId = "sample-shadow",
            Mode = "ChatMode",
            RetrievalMrrAnyMustHit = 1,
            MustHit = ["memory:active-rule-v2"],
            MustNotHit = ["memory:deprecated-rule-v1"],
            SelectedIds = ["memory:active-rule-v2"],
            ExcludedIds = ["memory:deprecated-rule-v1"],
            SelectedItemDiagnostics =
            [
                CreateDiagnostic("memory:active-rule-v2", score: 10, rank: 1, isMustHit: true)
            ],
            DroppedItemDiagnostics =
            [
                CreateDiagnostic(
                    "memory:deprecated-rule-v1",
                    score: 20,
                    rank: 2,
                    kind: "deprecated_memory",
                    section: "historical_context",
                    isMustNotHit: true)
            ]
        };
    }

    private static ContextEvalReport CreateLowMarginEvalReport(string sampleId)
    {
        return new ContextEvalReport
        {
            Results =
            [
                new ContextEvalResult
                {
                    SampleId = sampleId,
                    Query = "current task",
                    Mode = "ChatMode",
                    MustHit = ["candidate-alpha-current"],
                    SelectedItemDiagnostics =
                    [
                        CreateDiagnostic(
                            "candidate-alpha-current",
                            score: 10,
                            rank: 1,
                            section: "stable_context",
                            isMustHit: true,
                            sourceRefs: ["review:stable-1"])
                    ],
                    DroppedItemDiagnostics =
                    [
                        CreateDiagnostic(
                            "candidate-beta-current",
                            score: 14.8,
                            rank: 2,
                            section: "stable_context",
                            sourceRefs: ["review:stable-2"])
                    ]
                }
            ]
        };
    }

    private static ContextEvalItemDiagnostic CreateDiagnostic(
        string id,
        double score,
        int rank,
        string kind = "stable_memory",
        string type = "memory",
        string section = "stable_memory",
        bool isMustHit = false,
        bool isMustNotHit = false,
        IReadOnlyList<string>? sourceRefs = null)
    {
        return new ContextEvalItemDiagnostic
        {
            ItemId = id,
            Kind = kind,
            Type = type,
            SectionName = section,
            Reason = id,
            Score = score,
            Rank = rank,
            IsMustHit = isMustHit,
            IsMustNotHit = isMustNotHit,
            SourceRefs = sourceRefs ?? Array.Empty<string>()
        };
    }

    private static ContextRetrievalCandidate CreateRetrievalCandidate(
        string id,
        double score,
        Dictionary<string, string> metadata)
    {
        return new ContextRetrievalCandidate
        {
            CandidateId = $"MemoryItem:{id}",
            SourceId = id,
            Kind = ContextRetrievalCandidateKind.MemoryItem,
            Type = "memory",
            Content = id,
            Score = score,
            EstimatedTokens = 8,
            Reasons = [id],
            SourceRefs = [id],
            Metadata = metadata
        };
    }

    private static ContextRetrievalDecision ToDecision(ContextRetrievalCandidate candidate)
    {
        return new ContextRetrievalDecision
        {
            CandidateId = candidate.CandidateId,
            SourceId = candidate.SourceId,
            Kind = candidate.Kind,
            Type = candidate.Type,
            Reason = "dropped",
            Score = candidate.Score,
            EstimatedTokens = candidate.EstimatedTokens,
            Metadata = candidate.Metadata
        };
    }

    private static LifecycleAwareRankerShadowTraceRecord CreateTraceRecord(
        IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> candidateScores)
    {
        return new LifecycleAwareRankerShadowTraceRecord
        {
            RetrievalId = "retrieval-quality-1",
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            Query = "current rule",
            Profile = "lifecycle-aware-v1",
            CreatedAt = DateTimeOffset.UtcNow,
            CandidateScores = candidateScores,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["rankerShadowQueryMode"] = "ChatMode",
                ["planningIntent"] = "CurrentTask",
                ["rankerShadowFormalOutputChanged"] = "false"
            }
        };
    }

    private static CandidateRerankerShadowEvalReport BuildCandidateRerankerNoSpecialCaseReport(string sampleId)
    {
        return new CandidateRerankerShadowEvalRunner().Build(
            new ContextEvalReport
            {
                Results =
                [
                    new ContextEvalResult
                    {
                        SampleId = sampleId,
                        Query = "current rule",
                        Mode = "ChatMode",
                        MustHit = ["memory:active-rule-v2"],
                        SelectedIds = ["memory:active-rule-v2"],
                        SelectedItemDiagnostics =
                        [
                            CreateDiagnostic("memory:active-rule-v2", score: 10, rank: 1, isMustHit: true)
                        ],
                        DroppedItemDiagnostics =
                        [
                            CreateDiagnostic("memory:active-rule-v1", score: 8, rank: 2)
                        ]
                    }
                ]
            },
            "test");
    }

    private static CandidateRerankerShadowEvalReport CreateCalibrationShadowReport(
        string sampleId,
        bool lowMargin,
        bool priorityMismatch)
    {
        var hitSection = priorityMismatch ? "constraints" : "stable_context";
        var missSection = "stable_context";
        var hitScore = priorityMismatch ? 5 : (lowMargin ? 9.5 : 5);
        var missScore = lowMargin ? 10 : 20;
        var formalHit = !priorityMismatch;
        var scores = new[]
        {
            CreateCandidateScore("candidate-hit", legacyRank: 1, shadowRank: 2, score: hitScore, section: hitSection, isMustHit: formalHit),
            CreateCandidateScore("candidate-miss", legacyRank: 2, shadowRank: 1, score: missScore, section: missSection),
            CreateCandidateScore("candidate-extra", legacyRank: 3, shadowRank: 3, score: 4, section: "stable_context")
        };
        var sample = new CandidateRerankerShadowEvalSample
        {
            SampleId = sampleId,
            Mode = "ChatMode",
            Intent = "CurrentTask",
            CandidateCount = scores.Length,
            RawCandidateCount = scores.Length,
            RankableCandidateCount = scores.Length,
            FormalTopCandidateId = "candidate-hit",
            ShadowTopCandidateId = "candidate-miss",
            FormalTop1Correct = formalHit,
            ShadowTop1Correct = false,
            FormalMrr = priorityMismatch ? 0.5 : 1,
            ShadowMrr = priorityMismatch ? 0 : 0.5,
            WouldChangeTop1 = true,
            WouldChangeTopK = true,
            WouldRegress = true,
            Trace = new CandidateRerankerShadowTrace
            {
                RequestId = sampleId,
                Mode = "ChatMode",
                Intent = "CurrentTask",
                QueryText = "current task",
                CandidateCount = scores.Length,
                FormalTopCandidates =
                [
                    new CandidateRerankerShadowCandidateRef
                    {
                        CandidateId = "candidate-hit",
                        Rank = 1,
                        Score = 10,
                        Selected = true,
                        IsMustHit = formalHit,
                        SectionName = hitSection
                    },
                    new CandidateRerankerShadowCandidateRef
                    {
                        CandidateId = "candidate-miss",
                        Rank = 2,
                        Score = 8,
                        Selected = true,
                        SectionName = missSection
                    }
                ],
                ShadowTopCandidates =
                [
                    new CandidateRerankerShadowCandidateRef
                    {
                        CandidateId = "candidate-miss",
                        Rank = 1,
                        Score = missScore,
                        Selected = true,
                        SectionName = missSection
                    },
                    new CandidateRerankerShadowCandidateRef
                    {
                        CandidateId = "candidate-hit",
                        Rank = 2,
                        Score = hitScore,
                        Selected = true,
                        IsMustHit = formalHit,
                        SectionName = hitSection
                    }
                ],
                ScoreBreakdown = scores,
                FormalOutputChanged = false
            }
        };

        return new CandidateRerankerShadowEvalReport
        {
            DatasetName = "test",
            Samples = 1,
            CandidateCount = scores.Length,
            RawCandidateCount = scores.Length,
            WouldRegressCount = 1,
            NetGain = -1,
            SampleResults = [sample],
            Recommendation = CandidateRerankerShadowRecommendations.KeepFormalRanking
        };
    }

    private static CandidateRerankerShadowEvalReport CreateAlignmentShadowReport(
        string sampleId,
        bool baselineRegressed,
        bool recovered,
        bool abstained)
    {
        var shadowTop = recovered ? "candidate-hit" : "candidate-miss";
        var shadowMrr = recovered ? 1 : 0.5;
        var scores = new[]
        {
            CreateCandidateScore("candidate-hit", legacyRank: 1, shadowRank: recovered ? 1 : 2, score: recovered ? 30 : 10, section: "constraints", isMustHit: true),
            CreateCandidateScore("candidate-miss", legacyRank: 2, shadowRank: recovered ? 2 : 1, score: recovered ? 10 : 30, section: "stable_context")
        };
        var sample = new CandidateRerankerShadowEvalSample
        {
            SampleId = sampleId,
            Mode = "ChatMode",
            Intent = "CurrentTask",
            CandidateCount = scores.Length,
            RawCandidateCount = scores.Length,
            RankableCandidateCount = scores.Length,
            FormalTopCandidateId = "candidate-hit",
            ShadowTopCandidateId = shadowTop,
            FormalTop1Correct = true,
            ShadowTop1Correct = recovered,
            FormalMrr = 1,
            ShadowMrr = shadowMrr,
            WouldChangeTop1 = !recovered,
            WouldChangeTopK = !recovered,
            WouldRegress = baselineRegressed && !recovered,
            WouldApply = !abstained && !recovered,
            Abstained = abstained,
            WouldRegressAfterAbstain = baselineRegressed && !recovered && !abstained,
            Top1Margin = abstained ? 0.5 : 20,
            Trace = new CandidateRerankerShadowTrace
            {
                RequestId = sampleId,
                Mode = "ChatMode",
                Intent = "CurrentTask",
                QueryText = "current task",
                CandidateCount = scores.Length,
                FormalTopCandidates =
                [
                    new CandidateRerankerShadowCandidateRef
                    {
                        CandidateId = "candidate-hit",
                        Rank = 1,
                        Score = 10,
                        Selected = true,
                        IsMustHit = true,
                        SectionName = "constraints"
                    }
                ],
                ShadowTopCandidates =
                [
                    new CandidateRerankerShadowCandidateRef
                    {
                        CandidateId = shadowTop,
                        Rank = 1,
                        Score = recovered ? 30 : 10,
                        Selected = true,
                        IsMustHit = recovered,
                        SectionName = recovered ? "constraints" : "stable_context"
                    }
                ],
                ScoreBreakdown = scores,
                FormalOutputChanged = false
            }
        };

        return new CandidateRerankerShadowEvalReport
        {
            DatasetName = "test",
            Samples = 1,
            CandidateCount = scores.Length,
            RawCandidateCount = scores.Length,
            WouldRegressCount = sample.WouldRegress ? 1 : 0,
            AbstainCount = abstained ? 1 : 0,
            NetGain = sample.WouldRegress ? -1 : 0,
            NetGainAfterAbstain = sample.WouldRegressAfterAbstain ? -1 : 0,
            SampleResults = [sample],
            Recommendation = CandidateRerankerShadowRecommendations.KeepFormalRanking
        };
    }

    private static LifecycleAwareRankerShadowCandidateScore CreateCandidateScore(
        string candidateId,
        int legacyRank,
        int shadowRank,
        double score,
        string section,
        bool isMustHit = false)
    {
        return new LifecycleAwareRankerShadowCandidateScore
        {
            CandidateId = candidateId,
            Kind = "stable_memory",
            Type = "memory",
            SectionName = section,
            Selected = true,
            IsMustHit = isMustHit,
            LegacyRank = legacyRank,
            ShadowRank = shadowRank,
            LegacyScore = score,
            LifecycleAwareScore = score,
            ScoreDelta = 2,
            Reason = "current_version_boost",
            PromotionReasons = ["current_version_boost"],
            LifecycleFeatures = new LifecycleAwareFeatureSet
            {
                IsCurrentVersion = true,
                LifecycleConfidence = 0.9
            }
        };
    }

    private sealed class FakeRankerDebugRetriever : IContextRetriever
    {
        public Task<ContextRetrievalResult> RetrieveAsync(
            ContextRetrievalRequest request,
            CancellationToken cancellationToken = default)
        {
            var selected = CreateRetrievalCandidate(
                "memory:active-rule-v2",
                score: 10,
                metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["memoryLayer"] = "Stable",
                    ["lifecycleStatus"] = "Stable",
                    ["status"] = "active",
                    ["version"] = "v2"
                });
            var dropped = CreateRetrievalCandidate(
                "memory:deprecated-rule-v1",
                score: 20,
                metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["memoryLayer"] = "historical_context",
                    ["lifecycleStatus"] = "Deprecated",
                    ["section"] = "historical_context",
                    ["version"] = "v1"
                });

            return Task.FromResult(new ContextRetrievalResult
            {
                OperationId = request.OperationId,
                SelectedItems = [selected],
                DroppedItems =
                [
                    new ContextRetrievalDecision
                    {
                        CandidateId = dropped.CandidateId,
                        SourceId = dropped.SourceId,
                        Kind = dropped.Kind,
                        Type = dropped.Type,
                        Reason = "debug dropped",
                        Score = dropped.Score,
                        EstimatedTokens = dropped.EstimatedTokens,
                        Metadata = dropped.Metadata
                    }
                ],
                Trace = new ContextRetrievalTrace
                {
                    Candidates = [selected, dropped]
                },
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        private static ContextRetrievalCandidate CreateRetrievalCandidate(
            string id,
            double score,
            Dictionary<string, string> metadata)
        {
            return new ContextRetrievalCandidate
            {
                CandidateId = $"MemoryItem:{id}",
                SourceId = id,
                Kind = ContextRetrievalCandidateKind.MemoryItem,
                Type = "memory",
                Content = id,
                Score = score,
                EstimatedTokens = 8,
                Reasons = [id],
                SourceRefs = [id],
                Metadata = metadata
            };
        }
    }
}
