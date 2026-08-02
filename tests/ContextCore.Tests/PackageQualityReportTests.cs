using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// Package Quality 指标测试。验证 8 个确定性指标的计算逻辑与边界条件。
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class PackageQualityReportTests
{
    private static readonly string WorkspaceId = "workspace-quality";
    private static readonly string CollectionId = "collection-quality";

    // =========================================================================
    // Projector 集成测试
    // =========================================================================

    [TestMethod]
    public async Task ProjectPackage_PopulatesQuality_ForPackageDecision()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryContextStore();
        await store.SaveAsync(MakeItem("item-a", "Package Quality 集成测试 A", ["quality"], now));

        var builder = new BasicContextPackageBuilder(store);
        var request = new ContextPackageRequest
        {
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            QueryText = "Package Quality",
            TokenBudget = 1000
        };

        var result = await builder.BuildDetailedAsync(request);
        var record = ContextDecisionProjector.ProjectPackage(result);

        Assert.IsNotNull(record.Quality);
        Assert.AreEqual(ContextDecisionPolicyVersions.V18_0, record.Quality.PolicyVersion);
        Assert.IsTrue(record.Quality.OverallScore >= 0.0 && record.Quality.OverallScore <= 1.0);
        Assert.IsFalse(string.IsNullOrEmpty(record.Quality.AnchorCoverage.Name));
        Assert.IsFalse(string.IsNullOrEmpty(record.Quality.HardConstraintSatisfaction.Name));
        Assert.IsFalse(string.IsNullOrEmpty(record.Quality.RequiredItemCoverage.Name));
        Assert.IsFalse(string.IsNullOrEmpty(record.Quality.Redundancy.Name));
        Assert.IsFalse(string.IsNullOrEmpty(record.Quality.ProvenanceCompleteness.Name));
        Assert.IsFalse(string.IsNullOrEmpty(record.Quality.LifecycleRisk.Name));
        Assert.IsFalse(string.IsNullOrEmpty(record.Quality.TokenEfficiency.Name));
        Assert.IsFalse(string.IsNullOrEmpty(record.Quality.SectionBalance.Name));
    }

    [TestMethod]
    public void ProjectRetrieval_DoesNotPopulateQuality()
    {
        var retrievalResult = new ContextRetrievalResult
        {
            OperationId = "op-1",
            Trace = new ContextRetrievalTrace
            {
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId
            },
            CreatedAt = DateTimeOffset.UtcNow
        };

        var record = ContextDecisionProjector.ProjectRetrieval(retrievalResult);
        Assert.IsNull(record.Quality);
    }

    // =========================================================================
    // Calculator 单元测试
    // =========================================================================

    [TestMethod]
    public void Compute_NoAnchors_ReturnsPerfectAnchorCoverage()
    {
        var result = MakeMinimalResult();
        var report = PackageQualityCalculator.Compute(result);

        Assert.AreEqual(1.0, report.AnchorCoverage.Score);
        Assert.AreEqual(0, report.AnchorCoverage.Denominator);
        Assert.IsTrue(report.AnchorCoverage.Detail.Contains("no anchors"));
    }

    [TestMethod]
    public void Compute_AnchorNamePresentInSection_IncreasesCoverage()
    {
        var result = MakeMinimalResult(packageMetadata: new Dictionary<string, string>
        {
            ["anchor.count"] = "2",
            ["anchor.names"] = "task,context",
            ["anchor.semanticAnchors"] = "task",
            ["anchor.rawSearchTokens"] = "context"
        }, sections: new[]
        {
            new ContextPackageSection
            {
                Name = "recent_context",
                Content = "this section mentions task and context anchors"
            }
        });

        var report = PackageQualityCalculator.Compute(result);
        Assert.AreEqual(2, report.AnchorCoverage.Numerator);
        Assert.AreEqual(2, report.AnchorCoverage.Denominator);
        Assert.AreEqual(1.0, report.AnchorCoverage.Score);
    }

    [TestMethod]
    public void Compute_AnchorNameNotInSection_ReducesCoverage()
    {
        var result = MakeMinimalResult(packageMetadata: new Dictionary<string, string>
        {
            ["anchor.count"] = "2",
            ["anchor.names"] = "task,missing-anchor",
            ["anchor.semanticAnchors"] = "task",
            ["anchor.rawSearchTokens"] = "missing-anchor"
        }, sections: new[]
        {
            new ContextPackageSection
            {
                Name = "recent_context",
                Content = "this section only mentions task"
            }
        });

        var report = PackageQualityCalculator.Compute(result);
        Assert.AreEqual(1, report.AnchorCoverage.Numerator);
        Assert.AreEqual(2, report.AnchorCoverage.Denominator);
        Assert.AreEqual(0.5, report.AnchorCoverage.Score, 0.001);
    }

    [TestMethod]
    public void Compute_NoHardConstraints_ReturnsPerfectSatisfaction()
    {
        var result = MakeMinimalResult();
        var report = PackageQualityCalculator.Compute(result);

        Assert.AreEqual(1.0, report.HardConstraintSatisfaction.Score);
        Assert.AreEqual(0, report.HardConstraintSatisfaction.Denominator);
    }

    [TestMethod]
    public void Compute_HardConstraintSelected_IncreasesSatisfaction()
    {
        var result = MakeMinimalResult(selectedItems: new[]
        {
            new ContextPackageDecision
            {
                ItemId = "hc-1",
                Kind = "hard_constraint",
                SectionName = "hard_constraints",
                SourceRefs = new[] { "src:hc-1" }
            }
        });

        var report = PackageQualityCalculator.Compute(result);
        Assert.AreEqual(1, report.HardConstraintSatisfaction.Numerator);
        Assert.AreEqual(1, report.HardConstraintSatisfaction.Denominator);
        Assert.AreEqual(1.0, report.HardConstraintSatisfaction.Score);
    }

    [TestMethod]
    public void Compute_HardConstraintDropped_ReducesSatisfaction()
    {
        var result = MakeMinimalResult(selectedItems: new[]
        {
            new ContextPackageDecision
            {
                ItemId = "hc-1",
                Kind = "hard_constraint",
                SectionName = "hard_constraints",
                SourceRefs = new[] { "src:hc-1" }
            }
        }, droppedItems: new[]
        {
            new DroppedContextItem
            {
                ItemId = "hc-2",
                Kind = "hard_constraint",
                Reason = "constraint is deprecated or rejected"
            }
        });

        var report = PackageQualityCalculator.Compute(result);
        Assert.AreEqual(1, report.HardConstraintSatisfaction.Numerator);
        Assert.AreEqual(2, report.HardConstraintSatisfaction.Denominator);
        Assert.AreEqual(0.5, report.HardConstraintSatisfaction.Score, 0.001);
    }

    [TestMethod]
    public void Compute_NoMustHitIds_ReturnsPerfectRequiredItemCoverage()
    {
        var result = MakeMinimalResult();
        var report = PackageQualityCalculator.Compute(result);

        Assert.AreEqual(1.0, report.RequiredItemCoverage.Score);
        Assert.AreEqual(0, report.RequiredItemCoverage.Denominator);
    }

    [TestMethod]
    public void Compute_MustHitIdInSelected_ReturnsFullCoverage()
    {
        var result = MakeMinimalResult(packageMetadata: new Dictionary<string, string>
        {
            ["mustHit"] = "must-1,must-2"
        }, selectedItems: new[]
        {
            new ContextPackageDecision
            {
                ItemId = "must-1",
                SourceRefs = new[] { "src:must-1" }
            },
            new ContextPackageDecision
            {
                ItemId = "must-2",
                SourceRefs = new[] { "src:must-2" }
            }
        });

        var report = PackageQualityCalculator.Compute(result);
        Assert.AreEqual(2, report.RequiredItemCoverage.Numerator);
        Assert.AreEqual(2, report.RequiredItemCoverage.Denominator);
        Assert.AreEqual(1.0, report.RequiredItemCoverage.Score);
    }

    [TestMethod]
    public void Compute_MustHitIdMissing_ReducesCoverage()
    {
        var result = MakeMinimalResult(packageMetadata: new Dictionary<string, string>
        {
            ["eval.mustHit"] = "must-1,must-2"
        }, selectedItems: new[]
        {
            new ContextPackageDecision
            {
                ItemId = "must-1",
                SourceRefs = new[] { "src:must-1" }
            }
        });

        var report = PackageQualityCalculator.Compute(result);
        Assert.AreEqual(1, report.RequiredItemCoverage.Numerator);
        Assert.AreEqual(2, report.RequiredItemCoverage.Denominator);
        Assert.AreEqual(0.5, report.RequiredItemCoverage.Score, 0.001);
        Assert.IsTrue(report.RequiredItemCoverage.Detail.Contains("missing"));
    }

    [TestMethod]
    public void Compute_NoDuplicates_ReturnsPerfectRedundancy()
    {
        var result = MakeMinimalResult();
        var report = PackageQualityCalculator.Compute(result);

        Assert.AreEqual(1.0, report.Redundancy.Score);
        Assert.AreEqual(0, report.Redundancy.Numerator);
    }

    [TestMethod]
    public void Compute_DroppedDuplicate_ReducesRedundancyScore()
    {
        var result = MakeMinimalResult(
            selectedItems: new[]
            {
                new ContextPackageDecision
                {
                    ItemId = "item-1",
                    SourceRefs = new[] { "src:1" }
                }
            },
            droppedItems: new[]
            {
                new DroppedContextItem
                {
                    ItemId = "item-2",
                    Reason = "duplicate-suppressed: same content hash"
                }
            });

        var report = PackageQualityCalculator.Compute(result);
        Assert.AreEqual(1, report.Redundancy.Numerator);
        Assert.AreEqual(2, report.Redundancy.Denominator);
        Assert.AreEqual(0.5, report.Redundancy.Score, 0.001);
    }

    [TestMethod]
    public void Compute_AllItemsWithSourceRefs_ReturnsPerfectProvenance()
    {
        var result = MakeMinimalResult(selectedItems: new[]
        {
            new ContextPackageDecision
            {
                ItemId = "item-1",
                SourceRefs = new[] { "src:1" }
            },
            new ContextPackageDecision
            {
                ItemId = "item-2",
                SourceRefs = new[] { "src:2" }
            }
        });

        var report = PackageQualityCalculator.Compute(result);
        Assert.AreEqual(2, report.ProvenanceCompleteness.Numerator);
        Assert.AreEqual(2, report.ProvenanceCompleteness.Denominator);
        Assert.AreEqual(1.0, report.ProvenanceCompleteness.Score);
    }

    [TestMethod]
    public void Compute_ItemWithoutSourceRefs_ReducesProvenance()
    {
        var result = MakeMinimalResult(selectedItems: new[]
        {
            new ContextPackageDecision
            {
                ItemId = "item-1",
                SourceRefs = new[] { "src:1" }
            },
            new ContextPackageDecision
            {
                ItemId = "item-2",
                SourceRefs = Array.Empty<string>()
            }
        });

        var report = PackageQualityCalculator.Compute(result);
        Assert.AreEqual(1, report.ProvenanceCompleteness.Numerator);
        Assert.AreEqual(2, report.ProvenanceCompleteness.Denominator);
        Assert.AreEqual(0.5, report.ProvenanceCompleteness.Score, 0.001);
    }

    [TestMethod]
    public void Compute_NoSelectedItems_ReturnsPerfectLifecycleRisk()
    {
        var result = MakeMinimalResult();
        var report = PackageQualityCalculator.Compute(result);

        Assert.AreEqual(1.0, report.LifecycleRisk.Score);
        Assert.AreEqual(0, report.LifecycleRisk.Denominator);
    }

    [TestMethod]
    public void Compute_SelectedItemWithDeprecatedMetadata_ReducesLifecycleScore()
    {
        var result = MakeMinimalResult(selectedItems: new[]
        {
            new ContextPackageDecision
            {
                ItemId = "item-active",
                SourceRefs = new[] { "src:1" }
            },
            new ContextPackageDecision
            {
                ItemId = "item-deprecated",
                SourceRefs = new[] { "src:2" },
                Metadata = new Dictionary<string, string>
                {
                    ["lifecycleStatus"] = "Deprecated"
                }
            }
        });

        var report = PackageQualityCalculator.Compute(result);
        Assert.AreEqual(1, report.LifecycleRisk.Numerator);
        Assert.AreEqual(2, report.LifecycleRisk.Denominator);
        Assert.AreEqual(0.5, report.LifecycleRisk.Score, 0.001);
        Assert.IsTrue(report.LifecycleRisk.Detail.Contains("deprecated=1"));
    }

    [TestMethod]
    public void Compute_SelectedItemWithDeprecatedUsedByActiveReason_ReducesLifecycleScore()
    {
        var result = MakeMinimalResult(selectedItems: new[]
        {
            new ContextPackageDecision
            {
                ItemId = "item-active",
                SourceRefs = new[] { "src:1" }
            },
            new ContextPackageDecision
            {
                ItemId = "item-deprecated",
                Reason = "deprecated-used-by-active chain",
                SourceRefs = new[] { "src:2" }
            }
        });

        var report = PackageQualityCalculator.Compute(result);
        Assert.AreEqual(1, report.LifecycleRisk.Numerator);
        Assert.AreEqual(0.5, report.LifecycleRisk.Score, 0.001);
    }

    [TestMethod]
    public void Compute_ZeroTokenBudget_ReturnsZeroTokenEfficiency()
    {
        var result = MakeMinimalResult(budget: new ContextPackageBudgetReport
        {
            TokenBudget = 0,
            UsedTokens = 100
        });

        var report = PackageQualityCalculator.Compute(result);
        Assert.AreEqual(0.0, report.TokenEfficiency.Score);
        Assert.IsTrue(report.TokenEfficiency.Detail.Contains("no budget"));
    }

    [TestMethod]
    public void Compute_UsedTokensExceedBudget_ReturnsZeroTokenEfficiency()
    {
        var result = MakeMinimalResult(budget: new ContextPackageBudgetReport
        {
            TokenBudget = 1000,
            UsedTokens = 1200
        });

        var report = PackageQualityCalculator.Compute(result);
        Assert.AreEqual(0.0, report.TokenEfficiency.Score);
        Assert.IsTrue(report.TokenEfficiency.Detail.Contains("overrun"));
    }

    [TestMethod]
    public void Compute_PartialBudgetUtilization_ReturnsRatioScore()
    {
        var result = MakeMinimalResult(budget: new ContextPackageBudgetReport
        {
            TokenBudget = 1000,
            UsedTokens = 750
        });

        var report = PackageQualityCalculator.Compute(result);
        Assert.AreEqual(0.75, report.TokenEfficiency.Score, 0.001);
        Assert.AreEqual(750, report.TokenEfficiency.Numerator);
        Assert.AreEqual(1000, report.TokenEfficiency.Denominator);
    }

    [TestMethod]
    public void Compute_SingleSection_ReturnsPerfectSectionBalance()
    {
        var result = MakeMinimalResult(budget: new ContextPackageBudgetReport
        {
            TokenBudget = 1000,
            UsedTokens = 500,
            Sections = new[]
            {
                new ContextPackageSectionBudget
                {
                    SectionName = "recent_context",
                    AllocatedTokens = 1000,
                    UsedTokens = 500,
                    UsageRatio = 0.5
                }
            }
        });

        var report = PackageQualityCalculator.Compute(result);
        Assert.AreEqual(1.0, report.SectionBalance.Score);
        Assert.IsTrue(report.SectionBalance.Detail.Contains("single section"));
    }

    [TestMethod]
    public void Compute_BalancedSections_HighSectionBalanceScore()
    {
        var result = MakeMinimalResult(budget: new ContextPackageBudgetReport
        {
            TokenBudget = 1000,
            UsedTokens = 800,
            Sections = new[]
            {
                new ContextPackageSectionBudget
                {
                    SectionName = "recent_context",
                    AllocatedTokens = 500,
                    UsedTokens = 400,
                    UsageRatio = 0.8
                },
                new ContextPackageSectionBudget
                {
                    SectionName = "working_memory",
                    AllocatedTokens = 500,
                    UsedTokens = 400,
                    UsageRatio = 0.8
                }
            }
        });

        var report = PackageQualityCalculator.Compute(result);
        // 两 section 完全均衡，stddev=0
        Assert.AreEqual(1.0, report.SectionBalance.Score, 0.001);
    }

    [TestMethod]
    public void Compute_UnbalancedSections_LowerSectionBalanceScore()
    {
        var result = MakeMinimalResult(budget: new ContextPackageBudgetReport
        {
            TokenBudget = 1000,
            UsedTokens = 500,
            Sections = new[]
            {
                new ContextPackageSectionBudget
                {
                    SectionName = "recent_context",
                    AllocatedTokens = 500,
                    UsedTokens = 500,
                    UsageRatio = 1.0
                },
                new ContextPackageSectionBudget
                {
                    SectionName = "working_memory",
                    AllocatedTokens = 500,
                    UsedTokens = 0,
                    UsageRatio = 0.0
                }
            }
        });

        var report = PackageQualityCalculator.Compute(result);
        // stddev=0.5, score = 1 - 0.5 = 0.5
        Assert.AreEqual(0.5, report.SectionBalance.Score, 0.001);
    }

    [TestMethod]
    public void Compute_AllScoresClampedToZeroOne()
    {
        // 构造一个会有多种边界情况的结果
        var result = MakeMinimalResult(
            packageMetadata: new Dictionary<string, string>
            {
                ["mustHit"] = "missing-1,missing-2"
            },
            selectedItems: new[]
            {
                new ContextPackageDecision
                {
                    ItemId = "item-1",
                    SourceRefs = Array.Empty<string>() // 无 source refs，provenance 低
                }
            },
            droppedItems: new[]
            {
                new DroppedContextItem
                {
                    ItemId = "dup-1",
                    Reason = "duplicate-suppressed"
                }
            },
            budget: new ContextPackageBudgetReport
            {
                TokenBudget = 100,
                UsedTokens = 50,
                Sections = new[]
                {
                    new ContextPackageSectionBudget
                    {
                        SectionName = "s1",
                        UsageRatio = 1.0
                    },
                    new ContextPackageSectionBudget
                    {
                        SectionName = "s2",
                        UsageRatio = 0.0
                    }
                }
            });

        var report = PackageQualityCalculator.Compute(result);

        AssertAllInZeroOne(report.AnchorCoverage.Score, nameof(report.AnchorCoverage));
        AssertAllInZeroOne(report.HardConstraintSatisfaction.Score, nameof(report.HardConstraintSatisfaction));
        AssertAllInZeroOne(report.RequiredItemCoverage.Score, nameof(report.RequiredItemCoverage));
        AssertAllInZeroOne(report.Redundancy.Score, nameof(report.Redundancy));
        AssertAllInZeroOne(report.ProvenanceCompleteness.Score, nameof(report.ProvenanceCompleteness));
        AssertAllInZeroOne(report.LifecycleRisk.Score, nameof(report.LifecycleRisk));
        AssertAllInZeroOne(report.TokenEfficiency.Score, nameof(report.TokenEfficiency));
        AssertAllInZeroOne(report.SectionBalance.Score, nameof(report.SectionBalance));
        AssertAllInZeroOne(report.OverallScore, "OverallScore");
    }

    [TestMethod]
    public void Compute_OverallScore_WeightedAverageOfMetrics()
    {
        // 全部指标都为 1.0 时，OverallScore 应为 1.0
        var result = MakeMinimalResult(
            selectedItems: new[]
            {
                new ContextPackageDecision
                {
                    ItemId = "item-1",
                    SourceRefs = new[] { "src:1" }
                }
            },
            budget: new ContextPackageBudgetReport
            {
                TokenBudget = 100,
                UsedTokens = 100,
                Sections = new[]
                {
                    new ContextPackageSectionBudget
                    {
                        SectionName = "s1",
                        UsageRatio = 1.0
                    }
                }
            });

        var report = PackageQualityCalculator.Compute(result);

        // 全部指标都应为 1.0（无 anchor / 无 hard constraint / 无 mustHit / 无 duplicate / 全 provenance / 无 lifecycle risk / 满预算 / 单 section）
        Assert.AreEqual(1.0, report.AnchorCoverage.Score, 0.001);
        Assert.AreEqual(1.0, report.HardConstraintSatisfaction.Score, 0.001);
        Assert.AreEqual(1.0, report.RequiredItemCoverage.Score, 0.001);
        Assert.AreEqual(1.0, report.Redundancy.Score, 0.001);
        Assert.AreEqual(1.0, report.ProvenanceCompleteness.Score, 0.001);
        Assert.AreEqual(1.0, report.LifecycleRisk.Score, 0.001);
        Assert.AreEqual(1.0, report.TokenEfficiency.Score, 0.001);
        Assert.AreEqual(1.0, report.SectionBalance.Score, 0.001);
        Assert.AreEqual(1.0, report.OverallScore, 0.001);
    }

    [TestMethod]
    public void Compute_NullResult_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(() => PackageQualityCalculator.Compute(null!));
    }

    // =========================================================================
    // 辅助方法
    // =========================================================================

    private static void AssertAllInZeroOne(double value, string name)
    {
        Assert.IsTrue(value >= 0.0 && value <= 1.0,
            $"{name} score {value} 不在 [0,1] 范围内");
    }

    private static ContextPackageBuildResult MakeMinimalResult(
        Dictionary<string, string>? packageMetadata = null,
        IReadOnlyList<ContextPackageDecision>? selectedItems = null,
        IReadOnlyList<DroppedContextItem>? droppedItems = null,
        ContextPackageBudgetReport? budget = null,
        IReadOnlyList<ContextPackageSection>? sections = null)
    {
        var metadata = packageMetadata ?? new Dictionary<string, string>();
        return new ContextPackageBuildResult
        {
            BuildId = "build-quality-test",
            Package = new ContextPackage
            {
                PackageId = "pkg-quality-test",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Sections = sections ?? Array.Empty<ContextPackageSection>(),
                EstimatedTokens = 0,
                SourceRefs = Array.Empty<string>(),
                Metadata = metadata,
                CreatedAt = DateTimeOffset.UtcNow
            },
            SelectedItems = selectedItems ?? Array.Empty<ContextPackageDecision>(),
            DroppedItems = droppedItems ?? Array.Empty<DroppedContextItem>(),
            Budget = budget ?? new ContextPackageBudgetReport
            {
                TokenBudget = 0,
                UsedTokens = 0
            },
            TokenBudget = 0,
            EstimatedTokens = 0,
            Metadata = new Dictionary<string, string>(metadata),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static ContextItem MakeItem(
        string id,
        string content,
        IReadOnlyList<string> tags,
        DateTimeOffset now)
    {
        return new ContextItem
        {
            Id = id,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Type = "note",
            Content = content,
            ContentFormat = ContextContentFormat.PlainText,
            Tags = tags,
            SourceRefs = [$"source:{id}"],
            Importance = 0.8,
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
