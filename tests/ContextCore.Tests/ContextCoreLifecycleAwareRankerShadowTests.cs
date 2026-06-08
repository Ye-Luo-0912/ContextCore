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

    private static ContextEvalItemDiagnostic CreateDiagnostic(
        string id,
        double score,
        int rank,
        string kind = "stable_memory",
        string type = "memory",
        string section = "stable_memory",
        bool isMustHit = false,
        bool isMustNotHit = false)
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
            IsMustNotHit = isMustNotHit
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
