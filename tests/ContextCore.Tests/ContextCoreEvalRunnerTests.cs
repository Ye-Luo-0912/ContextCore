using System.Text.Json;
using System.Text.Json.Serialization;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Services;

namespace ContextCore.Tests;

/// <summary>覆盖 ContextEvalRunner 评测运行器及其指标计算。</summary>
[TestClass]
public sealed class ContextCoreEvalRunnerTests
{
    private const string PromotionRuleConstraintText =
        "重复解释、重复澄清、重复说明本身不应被提升为长期偏好或稳定事实；只有用户明确确认其为长期规则时才可提升。";

    [TestMethod]
    public async Task ContextEvalRunner_ShouldRunOnSeedCategoriesAndCalculateMetrics()
    {
        var runner = new ContextEvalRunner();
        var report = await runner.RunAsync(FindContextsRoot());

        Assert.IsNotNull(report);
        
        // 如果失败，打印并抛出详细诊断信息
        if (report.PassRate < 0.99 || report.AvgRetrievalRecall10 < 0.99)
        {
            var summary = string.Join("\n", report.Results.Select(result => 
                $"Sample: {result.SampleId} | Succeeded: {result.Succeeded} | Error: {result.ErrorMessage}\n" +
                $"  MustHitCount: {result.MustHitCount} | MustHitRecalledCount: {result.MustHitRecalledCount}\n" +
                $"  MustNotHitCount: {result.MustNotHitCount} | MustNotHitRecalledCount: {result.MustNotHitRecalledCount}\n" +
                $"  PackageHasAllConstraints: {result.PackageHasAllConstraints}\n" +
                $"  PackageHasAllEntities: {result.PackageHasAllEntities}"));
            Assert.Fail($"Evaluation failed. Detailed Diagnostics:\n{summary}");
        }

        var missingUnc = report.WarningSources.TryGetValue("MissingUncertainty", out var muCount) ? muCount : 0;
        var mrrLow = report.WarningSources.TryGetValue("MRRLow", out var mlCount) ? mlCount : 0;

        Assert.AreEqual(1.0, report.AvgRetrievalRecall10, "Recall@10 must be 100%");
        Assert.AreEqual(0.0, report.AvgRetrievalNoiseViolationRatio, "NoiseViolationRatio must be 0");
        Assert.IsTrue(report.AvgRetrievalMrrAnyMustHit >= 0.42, $"MRRAnyMustHit ({report.AvgRetrievalMrrAnyMustHit:F4}) must be >= 0.42");
        Assert.IsTrue(report.AvgRetrievalRecall3 >= 0.72, $"Recall@3 ({report.AvgRetrievalRecall3:P2}) must be >= 0.72");
        Assert.IsTrue(report.AvgAttentionMrr > 0, "Attention shadow MRR should be recorded");
        Assert.IsTrue(report.AvgAttentionRecall5 >= 0, "Attention shadow Recall@5 should be recorded");
        Assert.IsTrue(report.SelectedSetChangeRatio >= 0, "Attention selected-set change ratio should be recorded");
        var expectedProfileCount = ContextCore.Abstractions.ContextAttentionProfile.CreateShadowExperimentProfiles().Count;
        Assert.AreEqual(expectedProfileCount, report.AttentionProfileSummaries.Count, "All attention profile experiment summaries should be recorded.");
        CollectionAssert.Contains(
            report.AttentionProfileSummaries.Select(summary => summary.ProfileId).ToArray(),
            "conservative-v1");
        CollectionAssert.Contains(
            report.AttentionProfileSummaries.Select(summary => summary.ProfileId).ToArray(),
            "guarded-shadow-v1");
        Assert.IsTrue(report.Results.Where(result => result.Succeeded).All(result => result.AttentionProfiles.Count == expectedProfileCount));
        Assert.IsTrue(missingUnc <= 6, $"Missing Expected Uncertainties ({missingUnc}) must be <= 6");
        Assert.IsTrue(mrrLow <= 5, $"MRRLow warnings ({mrrLow}) must be <= 5");

        // 验证按模式固化的汇总指标，确保 JSON 报告不依赖展示层临时分组。
        Assert.AreEqual(report.Results.Select(r => r.Mode).Distinct().Count(), report.ModeSummaries.Count);
        Assert.AreEqual(report.TotalSamples, report.ModeSummaries.Sum(summary => summary.TotalSamples));
        foreach (var summary in report.ModeSummaries)
        {
            var modeResults = report.Results.Where(result => result.Mode == summary.Mode).ToArray();
            Assert.AreEqual(modeResults.Length, summary.TotalSamples);
            Assert.AreEqual(modeResults.Count(result => result.Succeeded) / (double)modeResults.Length, summary.PassRate, 0.0001);
            Assert.AreEqual(modeResults.Average(result => result.RetrievalRecall10), summary.AvgRetrievalRecall10, 0.0001);
            Assert.AreEqual(modeResults.Average(result => result.AttentionMrr), summary.AvgAttentionMrr, 0.0001);
            Assert.AreEqual(modeResults.Average(result => result.PackageTokenWasteRatio), summary.AvgPackageWasteRatio, 0.0001);
        }

        // 验证单个 sample 数据。
        Assert.IsTrue(report.Results.Any(r => r.SampleId == "chat-sample-001"));
        var sample1 = report.Results.First(r => r.SampleId == "chat-sample-001");
        Assert.IsTrue(sample1.Succeeded);
        Assert.AreEqual(1, sample1.MustHitCount);
        Assert.AreEqual(1, sample1.MustHitRecalledCount);
        Assert.AreEqual(0, sample1.MustNotHitRecalledCount);
        Assert.IsTrue(sample1.PackageHasAllConstraints);
    }

    [TestMethod]
    public async Task ContextEvalRunner_ShouldPassChat20260529003WhenActivatedConstraintExists()
    {
        var root = Path.Combine(Path.GetTempPath(), "context-eval-p15-" + Guid.NewGuid().ToString("N"));
        var chatDir = Path.Combine(root, "chat");
        Directory.CreateDirectory(chatDir);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(chatDir, "seed_samples.json"),
                JsonSerializer.Serialize(new[]
                {
                    new ContextEvalSample
                    {
                        Id = "chat-20260529-003",
                        Query = "总结一下这轮对话里真正需要下次继续用的结论。",
                        Mode = "ChatMode",
                        MustHit = ["memory:session-conclusion", "candidate:promotion-working"],
                        MustNotHit = [],
                        ExpectedScopes = ["session"],
                        ExpectedEntities = ["阶段性结论"],
                        ExpectedConstraints = [PromotionRuleConstraintText],
                        ExpectedUncertainties = ["长期有效性需要复核"],
                        GoldenNotes = "应抽取阶段性结论，并把长期有效性标记为需复核。"
                    }
                }, EvalJsonOptions));

            await File.WriteAllTextAsync(
                Path.Combine(chatDir, "corpus.json"),
                JsonSerializer.Serialize(new ContextEvalCorpus
                {
                    Memories =
                    [
                        CreateEvalMemory(
                            "memory:session-conclusion",
                            "会话阶段性结论：A3 扩展评测已完成，下一步继续处理约束激活闭环。",
                            "conclusion",
                            0.95),
                        CreateEvalMemory(
                            "candidate:promotion-working",
                            "短期 promotion candidate：阶段性结论可以进入候选队列，但长期有效性需要复核，不能把重复说明直接当作稳定事实。",
                            "promotion-candidate",
                            0.9),
                        CreateEvalMemory(
                            "uncertainty:long-term-validity",
                            "不确定性：长期有效性需要复核，只有用户明确确认长期规则后才可提升。",
                            "uncertainty",
                            0.85)
                    ],
                    ActivatedConstraintGaps =
                    [
                        new ConstraintGapCandidate
                        {
                            GapId = "constraint-gap-chat-20260529-003-no-promote-repetition",
                            WorkspaceId = "eval",
                            CollectionId = "chat",
                            SessionId = "session-chat-20260529",
                            Source = "eval-constraint-gap-fixture",
                            SourceSampleId = "chat-20260529-003",
                            SourceOperationId = "phase-p15-constraint-activation-closure",
                            ExpectedConstraintText = PromotionRuleConstraintText,
                            SuggestedConstraintTitle = "重复说明不得自动提升",
                            SuggestedConstraintScope = "Collection",
                            SuggestedConstraintType = "Hard",
                            Severity = ConstraintGapSeverity.High,
                            Reason = "P15 eval closure fixture",
                            EvidenceRefs = ["eval:chat-20260529-003", "phase:p15"],
                            Status = ConstraintGapStatus.Pending,
                            CreatedAt = DateTimeOffset.UtcNow
                        }
                    ]
                }, EvalJsonOptions));

            var report = await new ContextEvalRunner().RunAsync(root, categoryFilter: "chat");
            var result = report.Results.Single(item => item.SampleId == "chat-20260529-003");

            Assert.IsTrue(result.Succeeded, result.ErrorMessage + Environment.NewLine + result.PackageBuildTrace);
            Assert.IsTrue(result.PackageHasAllConstraints, result.PackageBuildTrace);
            StringAssert.Contains(result.PackageBuildTrace, "constraint:gap:constraint-gap-chat-20260529-003-no-promote-repetition");
            StringAssert.Contains(result.PackageBuildTrace, "eval:chat-20260529-003");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ContextEvalRunner_ShouldAggregateAttentionProfileDiagnostics()
    {
        var report = BuildReportForDiagnostics([
            new ContextEvalResult
            {
                SampleId = "diagnostic-001",
                Mode = "ChatMode",
                Succeeded = true,
                Status = "Passed",
                AttentionProfiles =
                [
                    new ContextEvalAttentionProfileResult
                    {
                        ProfileId = "conservative-v1",
                        PolicyVersion = "context-attention-shadow-policy/conservative-v1",
                        CurrentMrr = 0.5,
                        AttentionMrr = 0.25,
                        AttentionRecall3 = 0,
                        AttentionRecall5 = 1,
                        Regressed = true,
                        WouldChangeSelectedSet = true,
                        MustHitDemotedCount = 1,
                        MustNotHitPromotedCount = 2,
                        MustNotHitWouldBeSelectedCount = 1,
                        SelectedSetChangeRatio = 0.5
                    }
                ]
            },
            new ContextEvalResult
            {
                SampleId = "diagnostic-002",
                Mode = "ProjectMode",
                Succeeded = true,
                Status = "Passed",
                AttentionProfiles =
                [
                    new ContextEvalAttentionProfileResult
                    {
                        ProfileId = "conservative-v1",
                        PolicyVersion = "context-attention-shadow-policy/conservative-v1",
                        CurrentMrr = 0.25,
                        AttentionMrr = 0.5,
                        AttentionRecall3 = 1,
                        AttentionRecall5 = 1,
                        Improved = true,
                        SelectedSetChangeRatio = 0
                    }
                ]
            }
        ]);

        var summary = report.AttentionProfileSummaries.Single();
        Assert.AreEqual("conservative-v1", summary.ProfileId);
        Assert.AreEqual(2, summary.SampleCount);
        Assert.AreEqual(1, summary.ImprovedSamples);
        Assert.AreEqual(1, summary.RegressedSamples);
        Assert.AreEqual(2, summary.MustNotHitPromotedCount);
        Assert.AreEqual(2, summary.CategoryBreakdown.Count);
        Assert.AreEqual(1, report.AttentionDiagnostics.TopRegressedSamples.Count);
        Assert.AreEqual(1, report.AttentionDiagnostics.MustHitDemotedSamples.Count);
        Assert.AreEqual(1, report.AttentionDiagnostics.MustNotHitPromotedSamples.Count);
        Assert.AreEqual(1, report.AttentionDiagnostics.SelectedSetChangedSamples.Count);
    }

    [TestMethod]
    public void GuardedAttentionRerankReportBuilder_ShouldAggregateComparisonReport()
    {
        var evalReport = new ContextEvalReport
        {
            Results =
            [
                new ContextEvalResult
                {
                    SampleId = "rerank-001",
                    Mode = "ChatMode",
                    Succeeded = true,
                    AttentionRerankComparison = new ContextCore.Abstractions.AttentionRerankComparisonReport
                    {
                        Enabled = true,
                        Mode = "SelectedSetPreserving",
                        ProfileId = "old-score-anchored-v1",
                        Applied = true,
                        OrderChanges =
                        [
                            new ContextCore.Abstractions.AttentionRerankItemChange
                            {
                                CandidateId = "ContextItem:ctx-a",
                                SourceId = "ctx-a",
                                CurrentRank = 2,
                                RerankedRank = 1,
                                RankDelta = 1,
                                IsMustHit = true
                            }
                        ],
                        MustHitRankDeltas =
                        [
                            new ContextCore.Abstractions.AttentionRerankItemChange
                            {
                                CandidateId = "ContextItem:ctx-a",
                                SourceId = "ctx-a",
                                CurrentRank = 2,
                                RerankedRank = 1,
                                RankDelta = 1,
                                IsMustHit = true
                            }
                        ]
                    }
                },
                new ContextEvalResult
                {
                    SampleId = "rerank-002",
                    Mode = "ProjectMode",
                    Succeeded = true,
                    AttentionRerankComparison = new ContextCore.Abstractions.AttentionRerankComparisonReport
                    {
                        Enabled = true,
                        Mode = "SelectedSetPreserving",
                        ProfileId = "old-score-anchored-v1",
                        Blocked = true,
                        BlockedReason = "must_not_hit_promotion_blocked",
                        MustNotHitRankDeltas =
                        [
                            new ContextCore.Abstractions.AttentionRerankItemChange
                            {
                                CandidateId = "ContextItem:ctx-noise",
                                SourceId = "ctx-noise",
                                CurrentRank = 2,
                                RerankedRank = 1,
                                RankDelta = 1,
                                IsMustNotHit = true
                            }
                        ]
                    }
                }
            ]
        };

        var report = GuardedAttentionRerankReportBuilder.Build(evalReport);

        Assert.AreEqual(2, report.TotalSamples);
        Assert.AreEqual(1, report.AppliedSamples);
        Assert.AreEqual(1, report.BlockedSamples);
        Assert.AreEqual(1, report.OrderChanges);
        Assert.AreEqual(1, report.MustHitRankDeltaCount);
        Assert.AreEqual(1, report.MustNotHitRankDeltaCount);
        Assert.AreEqual(1, report.BlockedReasons["must_not_hit_promotion_blocked"]);
        Assert.AreEqual("ctx-a", report.Samples[0].TopOrderChanges.Single().SourceId);
    }

    [TestMethod]
    public void GuardedAttentionOrderQualityReportBuilder_ShouldAggregateSelectedOrderMetrics()
    {
        var evalReport = new ContextEvalReport
        {
            Results =
            [
                new ContextEvalResult
                {
                    SampleId = "order-001",
                    Mode = "ChatMode",
                    Succeeded = true,
                    AttentionRerankComparison = new ContextCore.Abstractions.AttentionRerankComparisonReport
                    {
                        Enabled = true,
                        Mode = "SelectedSetPreserving",
                        ProfileId = "old-score-anchored-v1",
                        Applied = true,
                        OldSelectedOrder =
                        [
                            OrderItem("constraint-hard", 1, isConstraint: true, isHardConstraint: true),
                            OrderItem("ctx-safe", 2),
                            OrderItem("ctx-must", 3, isMustHit: true),
                            OrderItem("ctx-risk", 4, isLifecycleRisk: true, lifecycle: "Deprecated")
                        ],
                        NewSelectedOrder =
                        [
                            OrderItem("constraint-hard", 1, isConstraint: true, isHardConstraint: true),
                            OrderItem("ctx-must", 2, isMustHit: true),
                            OrderItem("ctx-safe", 3),
                            OrderItem("ctx-risk", 4, isLifecycleRisk: true, lifecycle: "Deprecated")
                        ],
                        OrderChanges =
                        [
                            Move("ctx-must", oldRank: 3, newRank: 2, rankDelta: 1, isMustHit: true),
                            Move("ctx-safe", oldRank: 2, newRank: 3, rankDelta: -1)
                        ],
                        MovedUpItems =
                        [
                            Move("ctx-must", oldRank: 3, newRank: 2, rankDelta: 1, isMustHit: true)
                        ],
                        MovedDownItems =
                        [
                            Move("ctx-safe", oldRank: 2, newRank: 3, rankDelta: -1)
                        ]
                    }
                }
            ]
        };

        var report = GuardedAttentionOrderQualityReportBuilder.Build(evalReport);

        Assert.AreEqual(1, report.TotalSamples);
        Assert.AreEqual(1, report.AppliedSamples);
        Assert.AreEqual(0, report.SelectedSetDiffCount);
        Assert.AreEqual(0, report.AddedItems);
        Assert.AreEqual(0, report.DroppedItems);
        Assert.AreEqual(1d / 3d, report.Baseline.SelectedOrderMRR, 0.0001);
        Assert.AreEqual(0.5, report.Reranked.SelectedOrderMRR, 0.0001);
        Assert.AreEqual(1, report.Reranked.MovedUpMustHitCount);
        Assert.IsTrue(report.SafetyGates.All(gate => gate.Passed));
        Assert.IsTrue(report.SortingGates.All(gate => gate.Passed));
        Assert.AreEqual("ctx-must", report.Samples.Single().MovedUpItems.Single().SourceId);
    }

    [TestMethod]
    public void GuardedAttentionProfileSweepReportBuilder_ShouldAggregateProfileMetrics()
    {
        var profile = ContextCore.Abstractions.ContextAttentionProfile.CreateOldScoreAnchoredV1Balanced();
        var orderReport = new GuardedAttentionOrderQualityReport
        {
            TotalSamples = 2,
            AppliedSamples = 2,
            SelectedSetDiffCount = 0,
            AddedItems = 0,
            DroppedItems = 0,
            LifecycleViolationCount = 0,
            HardConstraintMissingCount = 0,
            Reranked = new SelectedOrderQualityMetrics
            {
                SelectedOrderMRR = 0.75,
                FirstMustHitSelectedRank = 1.5,
                MustHitAverageSelectedRank = 2.0,
                ConstraintAverageRank = 1.0,
                LifecycleRiskAverageRank = 5.0,
                AttentionOrderDelta = 0.5,
                MovedUpMustHitCount = 1,
                MovedDownMustHitCount = 0
            },
            SafetyGates =
            [
                new SelectedOrderQualityGateResult { Name = "selected_set_diff_zero", Passed = true }
            ],
            SortingGates =
            [
                new SelectedOrderQualityGateResult { Name = "selected_order_mrr_not_lower", Passed = true }
            ]
        };

        var report = GuardedAttentionProfileSweepReportBuilder.Build(
            [(profile, orderReport)],
            includeSeedBatches: true);
        var row = report.Profiles.Single();

        Assert.AreEqual("SelectedSetPreserving", report.Mode);
        Assert.IsTrue(report.IncludeSeedBatches);
        Assert.AreEqual(2, report.TotalSamples);
        Assert.AreEqual("old-score-anchored-v1-balanced", row.ProfileId);
        Assert.AreEqual(0.90, row.Weights["oldScoreAnchorWeight"], 0.0001);
        Assert.AreEqual(0.04, row.Weights["mustHitBoost"], 0.0001);
        Assert.AreEqual(0, row.SelectedSetDiffCount);
        Assert.AreEqual(0.75, row.SelectedOrderMRR, 0.0001);
        Assert.AreEqual(1, row.MovedUpMustHitCount);
        Assert.IsTrue(row.SafetyGatePassed);
        Assert.IsTrue(row.SortingGatePassed);
    }

    [TestMethod]
    public void GuardedAttentionOrderQualityReportBuilder_ShouldFailLifecyclePromotionGate()
    {
        var evalReport = new ContextEvalReport
        {
            Results =
            [
                new ContextEvalResult
                {
                    SampleId = "order-risk-001",
                    Mode = "ProjectMode",
                    Succeeded = true,
                    AttentionRerankComparison = new ContextCore.Abstractions.AttentionRerankComparisonReport
                    {
                        Enabled = true,
                        Mode = "SelectedSetPreserving",
                        ProfileId = "old-score-anchored-v1",
                        Applied = true,
                        OldSelectedOrder =
                        [
                            OrderItem("ctx-must", 1, isMustHit: true),
                            OrderItem("ctx-safe", 2),
                            OrderItem("ctx-risk", 3, isLifecycleRisk: true, lifecycle: "Deprecated")
                        ],
                        NewSelectedOrder =
                        [
                            OrderItem("ctx-must", 1, isMustHit: true),
                            OrderItem("ctx-risk", 2, isLifecycleRisk: true, lifecycle: "Deprecated"),
                            OrderItem("ctx-safe", 3)
                        ],
                        OrderChanges =
                        [
                            Move("ctx-risk", oldRank: 3, newRank: 2, rankDelta: 1, isLifecycleRisk: true),
                            Move("ctx-safe", oldRank: 2, newRank: 3, rankDelta: -1)
                        ],
                        MovedUpItems =
                        [
                            Move("ctx-risk", oldRank: 3, newRank: 2, rankDelta: 1, isLifecycleRisk: true)
                        ]
                    }
                }
            ]
        };

        var report = GuardedAttentionOrderQualityReportBuilder.Build(evalReport);

        Assert.AreEqual(1, report.LifecycleViolationCount);
        Assert.IsFalse(report.SafetyGates.Single(gate => gate.Name == "lifecycle_violation_zero").Passed);
        Assert.IsFalse(report.SortingGates.Single(gate => gate.Name == "lifecycle_risk_not_promoted").Passed);
    }

    [TestMethod]
    public async Task ContextEvalRunner_ShouldIncludeSeedBatchesWhenRequested()
    {
        var runner = new ContextEvalRunner();
        var report = await runner.RunAsync(FindContextsRoot(), includeSeedBatches: true);

        // 扩展批次用于发现质量缺口，不作为 99% 通过率的稳定回归门禁。
        Assert.IsTrue(report.TotalSamples > 50);
        AssertModeSamplesAtLeast(report, "ChatMode", 30);
        AssertModeSamplesAtLeast(report, "NovelMode", 30);
        AssertModeSamplesAtLeast(report, "AutomationMode", 20);
        AssertModeSamplesAtLeast(report, "CodingMode", 20);
    }

    [TestMethod]
    public void UncertaintyMatchResolver_ShouldMatchExpandedDiagnosticSurfaces()
    {
        var diagnostics = UncertaintyMatchResolver.Resolve(
            "负责人是否已配置",
            [],
            [],
            [],
            [
                new ContextPackageSection
                {
                    Name = "diagnostics",
                    Content = "风险提示：负责人是否已配置仍需确认。"
                }
            ]);
        Assert.IsTrue(diagnostics.Satisfied);
        Assert.AreEqual("diagnostics", diagnostics.Source);

        var conflict = UncertaintyMatchResolver.Resolve(
            "ConflictEvidence",
            [],
            [
                new ContextPackageDecision
                {
                    ItemId = "conflict:latest",
                    SectionName = "conflict_evidence"
                }
            ],
            []);
        Assert.IsTrue(conflict.Satisfied);
        Assert.AreEqual("conflict_evidence", conflict.Source);

        var excludedReason = UncertaintyMatchResolver.Resolve(
            "命令是否受当前环境权限限制",
            [],
            [],
            [
                new DroppedContextItem
                {
                    ItemId = "risk:permissions",
                    Reason = "excluded reason: 命令是否受当前环境权限限制"
                }
            ]);
        Assert.IsTrue(excludedReason.Satisfied);
        Assert.AreEqual("excluded_reason", excludedReason.Source);

        var riskFlags = UncertaintyMatchResolver.Resolve(
            "外部状态是否变化",
            [],
            [
                new ContextPackageDecision
                {
                    ItemId = "risk:external-state",
                    Metadata = new Dictionary<string, string>
                    {
                        ["riskFlags"] = "恢复点之后的外部状态是否变化"
                    }
                }
            ],
            []);
        Assert.IsTrue(riskFlags.Satisfied);
        Assert.AreEqual("risk_flags", riskFlags.Source);
    }

    [TestMethod]
    public void UncertaintyMatchResolver_ShouldMatchSemanticAliasesAndClassifyFailures()
    {
        var aliasMatch = UncertaintyMatchResolver.Resolve(
            "命令是否受当前环境权限限制",
            [
                new ContextPackageUncertainty
                {
                    Code = "EvidenceUncertainty",
                    Message = "已选中证据包含不确定性线索：命令可能受当前环境权限限制。",
                    SectionName = "uncertainties",
                    ItemRefs = ["verification:test"]
                }
            ],
            [],
            []);
        Assert.IsTrue(aliasMatch.Satisfied);
        Assert.AreEqual("uncertainties", aliasMatch.Source);

        var wrongSection = UncertaintyMatchResolver.Resolve(
            "兑现方式可多选",
            [],
            [],
            [],
            [
                new ContextPackageSection
                {
                    Name = "working_memory",
                    Content = "伏笔铃声已经被选中，但兑现方式可多选。"
                }
            ]);
        Assert.IsFalse(wrongSection.Satisfied);
        Assert.AreEqual("UncertaintyPresentButWrongSection", wrongSection.FailureType);

        var aliasSurface = UncertaintyMatchResolver.Resolve(
            "负责人是否已配置",
            [],
            [
                new ContextPackageDecision
                {
                    ItemId = "queue:dead-letter",
                    Kind = "working_memory",
                    Type = "queue-state",
                    SectionName = "working_memory",
                    Metadata = new Dictionary<string, string>
                    {
                        ["queue"] = "dead-letter"
                    }
                }
            ],
            []);
        Assert.IsFalse(aliasSurface.Satisfied);
        Assert.AreEqual("UncertaintyPresentButAliasMismatch", aliasSurface.FailureType);

        var lifecycleMissing = UncertaintyMatchResolver.Resolve("废弃设定是否仍被召回", [], [], []);
        var conflictMissing = UncertaintyMatchResolver.Resolve("上下文版本是否存在冲突", [], [], []);
        var budgetMissing = UncertaintyMatchResolver.Resolve("token 预算是否导致关键证据丢失", [], [], []);
        var scopeMissing = UncertaintyMatchResolver.Resolve("命令是否受当前环境权限限制", [], [], []);
        var evidenceMissing = UncertaintyMatchResolver.Resolve("样本窗口是否足够大", [], [], []);

        Assert.AreEqual("MissingLifecycleUncertainty", lifecycleMissing.FailureType);
        Assert.AreEqual("MissingConflictUncertainty", conflictMissing.FailureType);
        Assert.AreEqual("MissingBudgetUncertainty", budgetMissing.FailureType);
        Assert.AreEqual("MissingScopeUncertainty", scopeMissing.FailureType);
        Assert.AreEqual("MissingEvidenceUncertainty", evidenceMissing.FailureType);
    }

    [TestMethod]
    public void ContextEvalRunner_ShouldMatchConservativeExpectationAliases()
    {
        AssertExpectedTextSatisfied("处理人", "负责人是否已配置待确认。", isEntity: true);
        AssertExpectedTextSatisfied("断剑", "主角的剑已经断了，不能再用旧武器描写。", isEntity: true);
        AssertExpectedTextSatisfied("伏笔兑现", "钟声伏笔需要在本章兑现，兑现方式可多选。", isEntity: false);
        AssertExpectedTextSatisfied("只修复相关断言，不做无关重构", "应该只修复相关断言，不改业务逻辑。", isEntity: false);
        AssertExpectedTextSatisfied("废弃设定不得进入上下文包", "禁止吃书设定，人物实力必须严格遵守苍穹大陆境界划分。", isEntity: false);
    }

    [TestMethod]
    public void ExtendedFailureTriageReportBuilder_ShouldClassifyFailedSample()
    {
        var evalReport = new ContextEvalReport
        {
            TotalSamples = 1,
            FailedSamples = 1,
            Results =
            [
                new ContextEvalResult
                {
                    SampleId = "extended-failed-001",
                    Mode = "AutomationMode",
                    Succeeded = false,
                    Status = "Failed",
                    RetrievalRecall10 = 0.5,
                    MustHitTokenShare = 0.01,
                    SelectedCount = 22,
                    TokenBudget = 100,
                    MustHit = ["must:selected-low", "must:dropped"],
                    ExpectedConstraints = ["必须人工确认"],
                    ExpectedEntities = ["死信队列"],
                    ExpectedUncertainties = ["负责人是否已配置"],
                    PackageHasAllConstraints = false,
                    PackageHasAllEntities = false,
                    PackageHasAllUncertainties = false,
                    PackageBuildTrace = "ExpectedUncertainties (UncertaintyMatchResolver):\n  - [✗] 负责人是否已配置 (source=none, failureType=MissingEvidenceUncertainty)",
                    BudgetPressureBreakdown = new ContextEvalBudgetPressureBreakdown
                    {
                        MandatoryTokens = 10,
                        ConstraintsTokens = 10,
                        WorkingTokens = 30,
                        StableTokens = 20,
                        EvidenceTokens = 0,
                        DiagnosticsTokens = 5,
                        HistoricalTokens = 0,
                        DroppedMustHitTokens = 35,
                        DroppedLowPriorityTokens = 40
                    },
                    SelectedItemDiagnostics =
                    [
                        new ContextEvalItemDiagnostic
                        {
                            ItemId = "must:selected-low",
                            Kind = "working_memory",
                            Type = "state",
                            SectionName = "working_memory",
                            Score = 40,
                            EstimatedTokens = 25,
                            Rank = 12,
                            IsMustHit = true
                        }
                    ],
                    DroppedItemDiagnostics =
                    [
                        new ContextEvalItemDiagnostic
                        {
                            ItemId = "must:dropped",
                            Kind = "working_memory",
                            Type = "state",
                            Reason = "token budget exhausted",
                            Score = 45,
                            EstimatedTokens = 35,
                            IsMustHit = true
                        }
                    ]
                }
            ]
        };

        var report = ExtendedFailureTriageReportBuilder.Build(evalReport);
        var sample = report.Samples.Single();

        Assert.AreEqual(1, report.FailedSamples);
        CollectionAssert.Contains(sample.FailureCategories.ToArray(), "MissingMustHit");
        CollectionAssert.Contains(sample.FailureCategories.ToArray(), "ConstraintMiss");
        CollectionAssert.Contains(sample.FailureCategories.ToArray(), "EntityMiss");
        CollectionAssert.Contains(sample.FailureCategories.ToArray(), "MissingUncertainty");
        CollectionAssert.Contains(sample.FailureCategories.ToArray(), "BudgetDroppedImportantItem");
        CollectionAssert.Contains(sample.FailureCategories.ToArray(), "MustHitSelectedButTooLow");
        CollectionAssert.Contains(sample.FailureCategories.ToArray(), "TooManyLowValueSelected");
        Assert.AreEqual(35, sample.BudgetPressureBreakdown.DroppedMustHitTokens);
        Assert.AreEqual("must:dropped", sample.TopDroppedImportantItems.Single().ItemId);
        CollectionAssert.Contains(sample.UncertaintyFailureTypes.ToArray(), "MissingEvidenceUncertainty");
        Assert.AreEqual("uncertainty mapping", sample.SuggestedFixType);

        var markdown = ExtendedFailureTriageReportBuilder.BuildMarkdownReport(report);
        Assert.AreEqual("extended-failed-001", report.FixPlan.Single().SampleId);
        StringAssert.Contains(markdown, "Failed Sample Fix Plan");
        StringAssert.Contains(markdown, "BudgetPressureBreakdown");
    }

    private static void AssertModeSamplesAtLeast(ContextEvalReport report, string mode, int minimum)
    {
        var summary = report.ModeSummaries.FirstOrDefault(item => item.Mode == mode);
        Assert.IsNotNull(summary, $"缺少 {mode} 的模式汇总。");
        Assert.IsTrue(summary.TotalSamples >= minimum, $"{mode} 样本数不足，当前 {summary.TotalSamples}，最低要求 {minimum}。");
    }

    private static ContextEvalReport BuildReportForDiagnostics(IReadOnlyList<ContextEvalResult> results)
    {
        var method = typeof(ContextEvalRunner).GetMethod(
            "BuildReport",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        return (ContextEvalReport)method.Invoke(null, [results])!;
    }

    private static readonly JsonSerializerOptions EvalJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static ContextMemoryItem CreateEvalMemory(
        string id,
        string content,
        string type,
        double importance)
    {
        return new ContextMemoryItem
        {
            Id = id,
            WorkspaceId = "eval",
            CollectionId = "chat",
            Layer = ContextMemoryLayer.Working,
            Status = ContextMemoryStatus.Active,
            Type = type,
            Content = content,
            Tags = [type, "chat"],
            SourceRefs = [$"eval:{id}"],
            Importance = importance,
            Confidence = 0.9,
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
    }

    private static void AssertExpectedTextSatisfied(string expected, string actualText, bool isEntity)
    {
        var method = typeof(ContextEvalRunner).GetMethod(
            "IsExpectedTextSatisfied",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        var satisfied = (bool)method.Invoke(null, [expected, actualText, isEntity])!;
        Assert.IsTrue(satisfied, $"期望 `{expected}` 应匹配 `{actualText}`。");
    }

    private static ContextCore.Abstractions.AttentionRerankOrderItem OrderItem(
        string sourceId,
        int rank,
        bool isMustHit = false,
        bool isConstraint = false,
        bool isHardConstraint = false,
        bool isLifecycleRisk = false,
        string lifecycle = "Active")
    {
        return new ContextCore.Abstractions.AttentionRerankOrderItem
        {
            CandidateId = $"ContextItem:{sourceId}",
            SourceId = sourceId,
            Rank = rank,
            Lifecycle = lifecycle,
            IsMustHit = isMustHit,
            IsConstraint = isConstraint,
            IsHardConstraint = isHardConstraint,
            IsLifecycleRisk = isLifecycleRisk
        };
    }

    private static ContextCore.Abstractions.AttentionRerankItemChange Move(
        string sourceId,
        int oldRank,
        int newRank,
        int rankDelta,
        bool isMustHit = false,
        bool isLifecycleRisk = false)
    {
        return new ContextCore.Abstractions.AttentionRerankItemChange
        {
            CandidateId = $"ContextItem:{sourceId}",
            SourceId = sourceId,
            CurrentRank = oldRank,
            RerankedRank = newRank,
            RankDelta = rankDelta,
            IsMustHit = isMustHit,
            IsLifecycleRisk = isLifecycleRisk
        };
    }

    private static string FindContextsRoot()
    {
        // 自动解析当前目录获取 eval/contexts。
        var current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            var target = Path.Combine(current, "eval", "contexts");
            if (Directory.Exists(target))
            {
                return target;
            }

            current = Path.GetDirectoryName(current);
        }

        Assert.Fail("应该定位到 eval/contexts 目录");
        return string.Empty;
    }
}
