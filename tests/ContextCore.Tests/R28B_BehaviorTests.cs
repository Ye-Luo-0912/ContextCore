using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.DecisionEngine;

namespace ContextCore.Tests;

// ===========================================================================
// R28-B P0-10：行为测试 — DecisionEngine 基础设施
//
// 覆盖范围（10 个测试类）：
//   1. RetrievalResultProjectorTests — Projector 投影 + Material sidecar 恢复 + AllocationDecisions 消费
//   2. PackageResultProjectorTests — Projector 投影 + section budget 构建 + IsTruncated 标记
//   3. ShadowGateTests — Parity 验收门控（Hard/Diagnostic/Divergent）
//   4. DecisionExperimentPlaneTests — Parity 对比 + Jaccard 数学（P0-4 回归）
//   5. CutoverControllerTests — 流量比例切换 + 稳定哈希
//   6. CutoverConfigurationTests — 配置默认值 + 环境变量解析
//   7. InMemoryExperimentRecorderTests — 持久化抽象默认实现（P0-9）
//   8. DecisionExperimentPlaneIntegrationTests — RecordShadowReport + Sampled shadow + 历史评估
//   9. ReplayFixtureTests — FromReport vs FromShadowReport（WorkingSet/V2Result 传播）
//  10. ShadowDecisionRuntimeTests — Shadow tee 编排 + Parity 报告
//
// 设计原则：
//   - 每个 [TestClass] 自包含，无共享 fixture（与现有 DecisionEngineTests 模式一致）
//   - 共享 helper（MakeEnvelope / MakeResult / MakeParityReport）放在 file-level internal static class
//   - 重点回归 P0-4 / P0-7 / P0-8 / P0-9 修复点
// ===========================================================================

internal static class R28BTestHelpers
{
    public static ContextCandidateEnvelope MakeEnvelope(
        string candidateId,
        ContextCandidateSource source,
        double score,
        int tokens,
        CandidateSafetyState? safety = null,
        CandidateUtilityScore? utility = null)
    {
        return new ContextCandidateEnvelope
        {
            CandidateId = candidateId,
            CanonicalKey = CanonicalCandidateKey.Create(
                workspaceId: "test-ws",
                collectionId: "test-col",
                entityKind: "test-entity",
                entityId: candidateId,
                entityVersion: "v1"),
            Source = source,
            Type = "test-type",
            EstimatedTokens = tokens,
            Safety = safety ?? new CandidateSafetyState(),
            Utility = utility ?? new CandidateUtilityScore
            {
                DeterministicScore = score,
                FinalScore = score,
                ReasonCode = "deterministic-only"
            }
        };
    }

    public static ContextDecisionResult MakeResult(
        string requestId,
        IReadOnlyList<ContextCandidateEnvelope>? selected = null,
        IReadOnlyList<ContextCandidateEnvelope>? dropped = null,
        int estimatedTokens = 0,
        int tokenBudget = 0,
        IReadOnlyList<CandidateAllocationDecision>? allocationDecisions = null)
    {
        return new ContextDecisionResult
        {
            RequestId = requestId,
            DecisionSource = ContextDecisionSource.Retrieval,
            SelectedEnvelopes = selected ?? Array.Empty<ContextCandidateEnvelope>(),
            DroppedEnvelopes = dropped ?? Array.Empty<ContextCandidateEnvelope>(),
            Outcome = new ContextDecisionOutcomeSummary
            {
                SelectedCount = selected?.Count ?? 0,
                DroppedCount = dropped?.Count ?? 0,
                EstimatedTokens = estimatedTokens,
                TokenBudget = tokenBudget,
                Sections = Array.Empty<string>(),
                SafetyGateBlockedCount = 0,
                BudgetExceededCount = 0
            },
            PolicyVersion = "test-policy/v1",
            ModelEnabled = false,
            AllocationDecisions = allocationDecisions ?? Array.Empty<CandidateAllocationDecision>()
        };
    }

    public static CandidateMaterial MakeMaterial(CanonicalCandidateKey key, string content)
    {
        return new CandidateMaterial
        {
            Key = key,
            Content = content,
            NativeKind = "test-type"
        };
    }

    public static CandidateAllocationDecision MakeAllocation(
        CanonicalCandidateKey key,
        string section,
        int includedTokens,
        bool isTruncated = false,
        CandidateDecisionReasonCode reasonCode = CandidateDecisionReasonCode.SelectedHighestUtility)
    {
        return new CandidateAllocationDecision
        {
            CandidateKey = key,
            Section = section,
            IncludedTokens = includedTokens,
            IsTruncated = isTruncated,
            ReasonCode = reasonCode
        };
    }
}

// ===========================================================================
// 1. RetrievalResultProjectorTests
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
public sealed class RetrievalResultProjectorTests
{
    [TestMethod]
    public void Project_BasicResult_MapsSelectedAndDroppedEnvelopes()
    {
        var result = R28BTestHelpers.MakeResult(
            requestId: "op-1",
            selected: new[]
            {
                R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, score: 0.8, tokens: 100),
                R28BTestHelpers.MakeEnvelope("c2", ContextCandidateSource.Lexical, score: 0.5, tokens: 200)
            },
            dropped: new[]
            {
                R28BTestHelpers.MakeEnvelope("drop-1", ContextCandidateSource.Lexical, score: 0.1, tokens: 50,
                    safety: new CandidateSafetyState
                    {
                        PassesSafetyGate = false,
                        BlockReasonCode = CandidateDecisionReasonCode.TokenBudgetExceeded
                    })
            },
            estimatedTokens: 300);

        var projector = new RetrievalResultProjector();
        var dto = projector.Project(result);

        Assert.AreEqual("op-1", dto.OperationId);
        Assert.IsTrue(dto.Succeeded);
        Assert.AreEqual(2, dto.SelectedItems.Count);
        Assert.AreEqual("c1", dto.SelectedItems[0].CandidateId);
        Assert.AreEqual(0.8, dto.SelectedItems[0].Score);
        Assert.AreEqual(1, dto.DroppedItems.Count);
        Assert.AreEqual("drop-1", dto.DroppedItems[0].CandidateId);
        StringAssert.Contains(dto.DroppedItems[0].Reason, "TokenBudgetExceeded");
        Assert.AreEqual(300, dto.EstimatedTokens);
    }

    [TestMethod]
    public void Project_WithWorkingSet_RestoresContentFromMaterialSidecar()
    {
        // P0-7 回归：Projector 从 workingSet.Materials 恢复候选正文
        var envelope = R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, score: 0.8, tokens: 100);
        var result = R28BTestHelpers.MakeResult(
            requestId: "op-2",
            selected: new[] { envelope },
            estimatedTokens: 100);

        var workingSet = new CandidateWorkingSet
        {
            Envelopes = new[] { envelope },
            Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
            {
                [envelope.CanonicalKey] = R28BTestHelpers.MakeMaterial(envelope.CanonicalKey, "hello world content")
            }
        };

        var projector = new RetrievalResultProjector();
        var dto = projector.Project(result, workingSet);

        Assert.AreEqual(1, dto.SelectedItems.Count);
        // P0-7：Content 从 sidecar 恢复
        Assert.AreEqual("hello world content", dto.SelectedItems[0].Content);
    }

    [TestMethod]
    public void Project_WithWorkingSet_ConsumesAllocationIncludedTokensAndIsTruncated()
    {
        // P0-7 回归：Projector 从 AllocationDecisions 消费 IncludedTokens + IsTruncated
        var envelope = R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Lexical, score: 0.7, tokens: 200);
        var allocation = R28BTestHelpers.MakeAllocation(
            envelope.CanonicalKey,
            section: "recent_context",
            includedTokens: 150,
            isTruncated: true);

        var result = R28BTestHelpers.MakeResult(
            requestId: "op-3",
            selected: new[] { envelope },
            estimatedTokens: 150,
            tokenBudget: 1000,
            allocationDecisions: new[] { allocation });

        var workingSet = new CandidateWorkingSet
        {
            Envelopes = new[] { envelope },
            Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
            {
                [envelope.CanonicalKey] = R28BTestHelpers.MakeMaterial(envelope.CanonicalKey, "content")
            }
        };

        var projector = new RetrievalResultProjector();
        var dto = projector.Project(result, workingSet);

        Assert.AreEqual(1, dto.SelectedItems.Count);
        // P0-7：IncludedTokens 从 AllocationDecision 恢复（200 → 150）
        Assert.AreEqual(150, dto.SelectedItems[0].EstimatedTokens);
        // P0-7：IsTruncated=true 添加 "truncated" reason
        CollectionAssert.Contains(dto.SelectedItems[0].Reasons.ToList(), "truncated");
    }

    [TestMethod]
    public void Project_WithWorkingSet_MissingMaterial_LeavesContentEmpty()
    {
        // 边界：Material sidecar 缺失时 Content 保持空字符串（不抛异常）
        var envelope = R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, score: 0.8, tokens: 100);
        var result = R28BTestHelpers.MakeResult(
            requestId: "op-4",
            selected: new[] { envelope });

        var workingSet = new CandidateWorkingSet
        {
            Envelopes = new[] { envelope },
            Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>()
        };

        var projector = new RetrievalResultProjector();
        var dto = projector.Project(result, workingSet);

        Assert.AreEqual(1, dto.SelectedItems.Count);
        Assert.AreEqual(string.Empty, dto.SelectedItems[0].Content);
    }
}

// ===========================================================================
// 2. PackageResultProjectorTests
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
public sealed class PackageResultProjectorTests
{
    [TestMethod]
    public void Project_BasicResult_MapsSelectedAndDropped()
    {
        var result = R28BTestHelpers.MakeResult(
            requestId: "build-1",
            selected: new[]
            {
                R28BTestHelpers.MakeEnvelope("item-1", ContextCandidateSource.WorkingMemory, score: 0.9, tokens: 100)
            },
            dropped: new[]
            {
                R28BTestHelpers.MakeEnvelope("item-2", ContextCandidateSource.Lexical, score: 0.2, tokens: 50,
                    safety: new CandidateSafetyState
                    {
                        PassesSafetyGate = false,
                        BlockReasonCode = CandidateDecisionReasonCode.DeprecatedBlocked
                    })
            },
            estimatedTokens: 100);

        var projector = new PackageResultProjector();
        var dto = projector.Project(result);

        Assert.AreEqual("build-1", dto.BuildId);
        Assert.AreEqual(1, dto.SelectedItems.Count);
        Assert.AreEqual("item-1", dto.SelectedItems[0].ItemId);
        Assert.AreEqual("working_memory", dto.SelectedItems[0].Kind);
        Assert.AreEqual("working_memory", dto.SelectedItems[0].SectionName);
        Assert.AreEqual(1, dto.DroppedItems.Count);
        Assert.AreEqual("item-2", dto.DroppedItems[0].ItemId);
        StringAssert.Contains(dto.DroppedItems[0].Reason, "DeprecatedBlocked");
    }

    [TestMethod]
    public void Project_WithWorkingSet_RestoresContentAndMarksTruncated()
    {
        // P0-7 回归：Package Projector 从 sidecar 恢复 Content + IsTruncated 写入 metadata
        var envelope = R28BTestHelpers.MakeEnvelope("item-1", ContextCandidateSource.Lexical, score: 0.6, tokens: 200);
        var allocation = R28BTestHelpers.MakeAllocation(
            envelope.CanonicalKey,
            section: "custom-section",
            includedTokens: 120,
            isTruncated: true);

        var result = R28BTestHelpers.MakeResult(
            requestId: "build-2",
            selected: new[] { envelope },
            estimatedTokens: 120,
            tokenBudget: 1000,
            allocationDecisions: new[] { allocation });

        var workingSet = new CandidateWorkingSet
        {
            Envelopes = new[] { envelope },
            Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
            {
                [envelope.CanonicalKey] = R28BTestHelpers.MakeMaterial(envelope.CanonicalKey, "package content body")
            }
        };

        var projector = new PackageResultProjector();
        var dto = projector.Project(result, workingSet);

        Assert.AreEqual(1, dto.SelectedItems.Count);
        var decision = dto.SelectedItems[0];
        // P0-7：Section 从 AllocationDecision 恢复
        Assert.AreEqual("custom-section", decision.SectionName);
        // P0-7：IncludedTokens 从 AllocationDecision 恢复（200 → 120）
        Assert.AreEqual(120, decision.EstimatedTokens);
        // P0-7：IsTruncated=true 写入 metadata["truncated"]="true"
        Assert.IsNotNull(decision.Metadata);
        Assert.IsTrue(decision.Metadata.TryGetValue("truncated", out var truncatedVal));
        Assert.AreEqual("true", truncatedVal);
    }

    [TestMethod]
    public void Project_WithWorkingSet_BuildsSectionBudgetsFromAllocationDecisions()
    {
        // P0-7 回归：从 AllocationDecisions 按 Section 聚合构建 section budgets
        var env1 = R28BTestHelpers.MakeEnvelope("i1", ContextCandidateSource.Lexical, score: 0.5, tokens: 100);
        var env2 = R28BTestHelpers.MakeEnvelope("i2", ContextCandidateSource.Semantic, score: 0.6, tokens: 100);
        var env3 = R28BTestHelpers.MakeEnvelope("i3", ContextCandidateSource.WorkingMemory, score: 0.7, tokens: 100);

        var allocations = new[]
        {
            R28BTestHelpers.MakeAllocation(env1.CanonicalKey, "recent_context", 80),
            R28BTestHelpers.MakeAllocation(env2.CanonicalKey, "recent_context", 60),
            R28BTestHelpers.MakeAllocation(env3.CanonicalKey, "working_memory", 100)
        };

        var result = R28BTestHelpers.MakeResult(
            requestId: "build-3",
            selected: new[] { env1, env2, env3 },
            estimatedTokens: 240,
            tokenBudget: 1000,
            allocationDecisions: allocations);

        var workingSet = new CandidateWorkingSet
        {
            Envelopes = new[] { env1, env2, env3 },
            Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
            {
                [env1.CanonicalKey] = R28BTestHelpers.MakeMaterial(env1.CanonicalKey, "c1"),
                [env2.CanonicalKey] = R28BTestHelpers.MakeMaterial(env2.CanonicalKey, "c2"),
                [env3.CanonicalKey] = R28BTestHelpers.MakeMaterial(env3.CanonicalKey, "c3")
            }
        };

        var projector = new PackageResultProjector();
        var dto = projector.Project(result, workingSet);

        Assert.IsNotNull(dto.Budget);
        Assert.AreEqual(1000, dto.Budget.TokenBudget);
        Assert.AreEqual(240, dto.Budget.UsedTokens);

        // 两个 section：recent_context（80+60=140）和 working_memory（100）
        var sections = dto.Budget.Sections.ToList();
        Assert.AreEqual(2, sections.Count);

        var recentSection = sections.Single(s => s.SectionName == "recent_context");
        Assert.AreEqual(140, recentSection.AllocatedTokens);

        var memorySection = sections.Single(s => s.SectionName == "working_memory");
        Assert.AreEqual(100, memorySection.AllocatedTokens);
    }
}

// ===========================================================================
// 3. ShadowGateTests
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
public sealed class ShadowGateTests
{
    [TestMethod]
    public void Evaluate_JaccardOne_TokensEqual_CountsEqual_ReturnsHard()
    {
        var report = new ParityReport(
            LegacySelectedCount: 3,
            V2SelectedCount: 3,
            CommonSelectedCount: 3,
            OnlyInLegacyCount: 0,
            OnlyInV2Count: 0,
            JaccardIndex: 1.0,
            ParityLevel: ParityLevel.Hard,
            LegacyTokenTotal: 300,
            V2TokenTotal: 300,
            WorkingSetCandidateCount: 3);

        var gate = new ShadowGate();
        var result = gate.Evaluate(report);

        Assert.AreEqual(ParityLevel.Hard, result.OverallLevel);
        Assert.IsTrue(result.CanCutover);
        Assert.IsFalse(result.HasWarnings);
    }

    [TestMethod]
    public void Evaluate_JaccardLessThan090_ReturnsDivergent()
    {
        var report = new ParityReport(
            LegacySelectedCount: 5,
            V2SelectedCount: 5,
            CommonSelectedCount: 1,
            OnlyInLegacyCount: 4,
            OnlyInV2Count: 4,
            JaccardIndex: 0.111,
            ParityLevel: ParityLevel.Divergent,
            LegacyTokenTotal: 300,
            V2TokenTotal: 300,
            WorkingSetCandidateCount: 5);

        var gate = new ShadowGate();
        var result = gate.Evaluate(report);

        Assert.AreEqual(ParityLevel.Divergent, result.OverallLevel);
        Assert.IsFalse(result.CanCutover);
    }

    [TestMethod]
    public void Evaluate_TokenDeviationBeyondTolerance_ReturnsDivergent()
    {
        // Jaccard=1.0 但 token 偏差超过 5%（300 vs 500 = 66.7% deviation）
        var report = new ParityReport(
            LegacySelectedCount: 2,
            V2SelectedCount: 2,
            CommonSelectedCount: 2,
            OnlyInLegacyCount: 0,
            OnlyInV2Count: 0,
            JaccardIndex: 1.0,
            ParityLevel: ParityLevel.Hard,
            LegacyTokenTotal: 300,
            V2TokenTotal: 500,
            WorkingSetCandidateCount: 2);

        var gate = new ShadowGate();
        var result = gate.Evaluate(report);

        // 即使 Jaccard=1.0，token 偏差过大也会拉低 OverallLevel 到 Divergent
        Assert.AreEqual(ParityLevel.Divergent, result.OverallLevel);
        Assert.IsFalse(result.CanCutover);
    }

    [TestMethod]
    public void Evaluate_CustomTokenTolerance_ShiftsTokenDimensionLevel()
    {
        // 自定义宽松 token 容忍度：30% 偏差在默认 5% 容忍下为 Divergent，
        // 在自定义 50% 容忍下 token 维度变 Hard。Jaccard=1.0 使 Jaccard 维度恒为 Hard，
        // 因此 OverallLevel 由 token 维度决定。
        var report = new ParityReport(
            LegacySelectedCount: 2,
            V2SelectedCount: 2,
            CommonSelectedCount: 2,
            OnlyInLegacyCount: 0,
            OnlyInV2Count: 0,
            JaccardIndex: 1.0,
            ParityLevel: ParityLevel.Hard,
            LegacyTokenTotal: 100,
            V2TokenTotal: 130, // 30% token deviation
            WorkingSetCandidateCount: 2);

        var defaultGate = new ShadowGate();
        var defaultResult = defaultGate.Evaluate(report);
        // 默认 5% 容忍下，30% 偏差 > 5%*2=10% → token 维度 Divergent → OverallLevel Divergent
        var defaultTokenDim = defaultResult.Dimensions.Single(d => d.Dimension == "token-budget");
        Assert.AreEqual(ParityLevel.Divergent, defaultTokenDim.Level);
        Assert.AreEqual(ParityLevel.Divergent, defaultResult.OverallLevel);

        var looseGate = new ShadowGate(
            hardJaccardThreshold: 1.0,
            diagnosticJaccardThreshold: 0.90,
            tokenTolerance: 0.5, // 50% 容忍
            droppedTolerance: 2);
        var looseResult = looseGate.Evaluate(report);
        // 自定义 50% 容忍下，30% 偏差 < 50% → token 维度 Hard → OverallLevel Hard
        var looseTokenDim = looseResult.Dimensions.Single(d => d.Dimension == "token-budget");
        Assert.AreEqual(ParityLevel.Hard, looseTokenDim.Level);
        Assert.AreEqual(ParityLevel.Hard, looseResult.OverallLevel);
        Assert.IsTrue(looseResult.CanCutover);
    }

    [TestMethod]
    public void Evaluate_NullReport_Throws()
    {
        var gate = new ShadowGate();
        Assert.ThrowsException<ArgumentNullException>(() => gate.Evaluate(null!));
    }
}

// ===========================================================================
// 4. DecisionExperimentPlaneTests — Parity 对比 + Jaccard 数学（P0-4 回归）
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
public sealed class DecisionExperimentPlaneTests
{
    [TestMethod]
    public void Compare_IdenticalSelections_ReturnsHardParityWithJaccardOne()
    {
        var envelopes = new[]
        {
            R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, 0.8, 100),
            R28BTestHelpers.MakeEnvelope("c2", ContextCandidateSource.Lexical, 0.6, 100),
            R28BTestHelpers.MakeEnvelope("c3", ContextCandidateSource.WorkingMemory, 0.5, 100)
        };

        var legacy = R28BTestHelpers.MakeResult("op-1", selected: envelopes, estimatedTokens: 300);
        var v2 = R28BTestHelpers.MakeResult("op-1", selected: envelopes, estimatedTokens: 300);

        var plane = new DecisionExperimentPlane();
        var report = plane.Compare(legacy, v2);

        Assert.AreEqual(3, report.LegacySelectedCount);
        Assert.AreEqual(3, report.V2SelectedCount);
        Assert.AreEqual(3, report.CommonSelectedCount);
        Assert.AreEqual(0, report.OnlyInLegacyCount);
        Assert.AreEqual(0, report.OnlyInV2Count);
        Assert.AreEqual(1.0, report.JaccardIndex);
        Assert.AreEqual(ParityLevel.Hard, report.ParityLevel);
    }

    [TestMethod]
    public void Compare_PartialOverlap_ComputesJaccardUsingUnionDenominator()
    {
        // P0-4 回归：Jaccard 分母必须是 |A ∪ B| = |A| + |B| - |A ∩ B|，不是 |A| + |B|。
        // 旧实现把分母算成 |A| + |B| = 6，导致 Jaccard=2/6=0.33（被误判为 Divergent）。
        // 正确分母是 |A ∪ B| = 4，Jaccard=2/4=0.5（Diagnostic）。
        var legacySelected = new[]
        {
            R28BTestHelpers.MakeEnvelope("common-1", ContextCandidateSource.Lexical, 0.5, 100),
            R28BTestHelpers.MakeEnvelope("common-2", ContextCandidateSource.Semantic, 0.6, 100),
            R28BTestHelpers.MakeEnvelope("only-legacy", ContextCandidateSource.Lexical, 0.4, 100)
        };
        var v2Selected = new[]
        {
            R28BTestHelpers.MakeEnvelope("common-1", ContextCandidateSource.Lexical, 0.5, 100),
            R28BTestHelpers.MakeEnvelope("common-2", ContextCandidateSource.Semantic, 0.6, 100),
            R28BTestHelpers.MakeEnvelope("only-v2", ContextCandidateSource.Semantic, 0.7, 100)
        };

        var legacy = R28BTestHelpers.MakeResult("op-2", selected: legacySelected, estimatedTokens: 300);
        var v2 = R28BTestHelpers.MakeResult("op-2", selected: v2Selected, estimatedTokens: 300);

        var plane = new DecisionExperimentPlane();
        var report = plane.Compare(legacy, v2);

        Assert.AreEqual(2, report.CommonSelectedCount);
        Assert.AreEqual(1, report.OnlyInLegacyCount);
        Assert.AreEqual(1, report.OnlyInV2Count);
        // 期望 Jaccard = 2 / (3 + 3 - 2) = 2 / 4 = 0.5（而非错误的 2/6=0.33）
        Assert.AreEqual(0.5, report.JaccardIndex, 0.0001);
        // Jaccard=0.5 < 0.90 → Divergent（注意：原误判为 Diagnostic 是因为旧分母错误算出 0.33，
        // 现在正确算出 0.5，仍是 Divergent，但 union 分母修复后不会再出现 0.33 的假性发散值）
        Assert.AreEqual(ParityLevel.Divergent, report.ParityLevel);
    }

    [TestMethod]
    public void Compare_EmptyLegacyVsNonEmptyV2_ReturnsDivergentNotHard()
    {
        // P0-4 回归：空集与任何集合的交集恒为 0；Jaccard=0/|V2|=0 → Divergent。
        // 旧实现错误地令 commonSelected=v2Selected.Count，导致空 vs 非空被误判为 Hard。
        var legacy = R28BTestHelpers.MakeResult("op-3", selected: Array.Empty<ContextCandidateEnvelope>());
        var v2 = R28BTestHelpers.MakeResult(
            "op-3",
            selected: new[]
            {
                R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, 0.8, 100),
                R28BTestHelpers.MakeEnvelope("c2", ContextCandidateSource.Lexical, 0.6, 100)
            },
            estimatedTokens: 200);

        var plane = new DecisionExperimentPlane();
        var report = plane.Compare(legacy, v2);

        Assert.AreEqual(0, report.CommonSelectedCount);
        Assert.AreEqual(0, report.OnlyInLegacyCount);
        Assert.AreEqual(2, report.OnlyInV2Count);
        Assert.AreEqual(0.0, report.JaccardIndex);
        Assert.AreEqual(ParityLevel.Divergent, report.ParityLevel);
    }

    [TestMethod]
    public void Compare_BothEmpty_ReturnsHardParityByConvention()
    {
        // 双空集：union=0，约定 Jaccard=1.0（视为"无差异"）
        var legacy = R28BTestHelpers.MakeResult("op-4");
        var v2 = R28BTestHelpers.MakeResult("op-4");

        var plane = new DecisionExperimentPlane();
        var report = plane.Compare(legacy, v2);

        Assert.AreEqual(0, report.CommonSelectedCount);
        Assert.AreEqual(1.0, report.JaccardIndex);
        Assert.AreEqual(ParityLevel.Hard, report.ParityLevel);
    }

    [TestMethod]
    public void Compare_DisjointSelections_ReturnsJaccardZero()
    {
        var legacy = R28BTestHelpers.MakeResult(
            "op-5",
            selected: new[] { R28BTestHelpers.MakeEnvelope("a", ContextCandidateSource.Lexical, 0.5, 100) });
        var v2 = R28BTestHelpers.MakeResult(
            "op-5",
            selected: new[] { R28BTestHelpers.MakeEnvelope("b", ContextCandidateSource.Semantic, 0.6, 100) });

        var plane = new DecisionExperimentPlane();
        var report = plane.Compare(legacy, v2);

        Assert.AreEqual(0, report.CommonSelectedCount);
        Assert.AreEqual(1, report.OnlyInLegacyCount);
        Assert.AreEqual(1, report.OnlyInV2Count);
        Assert.AreEqual(0.0, report.JaccardIndex);
        Assert.AreEqual(ParityLevel.Divergent, report.ParityLevel);
    }

    [TestMethod]
    public void Compare_WithWorkingSet_PopulatesWorkingSetCandidateCount()
    {
        var envelopes = new[]
        {
            R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, 0.8, 100)
        };
        var legacy = R28BTestHelpers.MakeResult("op-6", selected: envelopes);
        var v2 = R28BTestHelpers.MakeResult("op-6", selected: envelopes);

        var workingSet = new CandidateWorkingSet
        {
            Envelopes = envelopes.Concat(new[]
            {
                R28BTestHelpers.MakeEnvelope("extra", ContextCandidateSource.Lexical, 0.4, 100)
            }).ToArray(),
            Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>()
        };

        var plane = new DecisionExperimentPlane();
        var report = plane.Compare(legacy, v2, workingSet);

        Assert.AreEqual(2, report.WorkingSetCandidateCount);
    }
}

// ===========================================================================
// 5. CutoverControllerTests
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
public sealed class CutoverControllerTests
{
    [TestMethod]
    public void ShouldUseV2_AtZeroPercent_AlwaysReturnsFalse()
    {
        var controller = new CutoverController(cutoverPercentage: 0);

        for (var i = 0; i < 20; i++)
        {
            Assert.IsFalse(controller.ShouldUseV2($"req-{i}"));
        }
    }

    [TestMethod]
    public void ShouldUseV2_AtHundredPercent_AlwaysReturnsTrue()
    {
        var controller = new CutoverController(cutoverPercentage: 100);

        for (var i = 0; i < 20; i++)
        {
            Assert.IsTrue(controller.ShouldUseV2($"req-{i}"));
        }
    }

    [TestMethod]
    public void ShouldUseV2_SameRequestId_AlwaysReturnsSameResult()
    {
        // 稳定哈希：同一 requestId 在同一 percentage 下始终走同一路径
        var controller = new CutoverController(cutoverPercentage: 50);
        var firstResult = controller.ShouldUseV2("stable-id-12345");

        for (var i = 0; i < 5; i++)
        {
            Assert.AreEqual(firstResult, controller.ShouldUseV2("stable-id-12345"));
        }
    }

    [TestMethod]
    public void ShouldUseV2_AtFiftyPercent_DistributesApproximatelyHalf()
    {
        // 50% 切换下，1000 个不同 requestId 中应有约 500 个走 V2
        var controller = new CutoverController(cutoverPercentage: 50);

        var v2Count = 0;
        for (var i = 0; i < 1000; i++)
        {
            if (controller.ShouldUseV2($"req-{i:D4}")) v2Count++;
        }

        // 期望 500 ± 100（容忍哈希分布波动）
        Assert.IsTrue(v2Count >= 400 && v2Count <= 600,
            $"Expected ~500 V2 routes at 50%, got {v2Count}");
    }

    [TestMethod]
    public void SetCutoverPercentage_UpdatesPercentage()
    {
        var controller = new CutoverController(cutoverPercentage: 0);
        Assert.AreEqual(0, controller.CutoverPercentage);

        controller.SetCutoverPercentage(75);
        Assert.AreEqual(75, controller.CutoverPercentage);
    }

    [TestMethod]
    public void Constructor_InvalidPercentage_Throws()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new CutoverController(-1));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new CutoverController(101));
    }

    [TestMethod]
    public void SetCutoverPercentage_InvalidValue_Throws()
    {
        var controller = new CutoverController();
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => controller.SetCutoverPercentage(-5));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => controller.SetCutoverPercentage(150));
    }
}

// ===========================================================================
// 6. CutoverConfigurationTests
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
public sealed class CutoverConfigurationTests
{
    [TestMethod]
    public void FromEnvironment_NoEnvVars_ReturnsDefaults()
    {
        // 清除环境变量确保默认值生效
        Environment.SetEnvironmentVariable(CutoverConfiguration.CutoverPercentageEnvVar, null);
        Environment.SetEnvironmentVariable("CC_SHADOW_SAMPLE_ENABLED", null);
        Environment.SetEnvironmentVariable("CC_SHADOW_SAMPLE_RATE", null);

        var config = CutoverConfiguration.FromEnvironment();

        Assert.AreEqual(CutoverConfiguration.DefaultCutoverPercentage, config.CutoverPercentage);
        Assert.IsTrue(config.EnableSampledShadow);
        Assert.AreEqual(0.01, config.ShadowSampleRate);
    }

    [TestMethod]
    public void FromEnvironment_InvalidPercentage_FallsBackToDefault()
    {
        Environment.SetEnvironmentVariable(CutoverConfiguration.CutoverPercentageEnvVar, "not-a-number");

        var config = CutoverConfiguration.FromEnvironment();

        Assert.AreEqual(CutoverConfiguration.DefaultCutoverPercentage, config.CutoverPercentage);

        Environment.SetEnvironmentVariable(CutoverConfiguration.CutoverPercentageEnvVar, null);
    }

    [TestMethod]
    public void FromEnvironment_ClampsOutOfRangePercentage()
    {
        Environment.SetEnvironmentVariable(CutoverConfiguration.CutoverPercentageEnvVar, "150");

        var config = CutoverConfiguration.FromEnvironment();

        Assert.AreEqual(100, config.CutoverPercentage);

        Environment.SetEnvironmentVariable(CutoverConfiguration.CutoverPercentageEnvVar, null);
    }

    [TestMethod]
    public void FromEnvironment_ClampsNegativePercentage()
    {
        Environment.SetEnvironmentVariable(CutoverConfiguration.CutoverPercentageEnvVar, "-50");

        var config = CutoverConfiguration.FromEnvironment();

        Assert.AreEqual(0, config.CutoverPercentage);

        Environment.SetEnvironmentVariable(CutoverConfiguration.CutoverPercentageEnvVar, null);
    }

    [TestMethod]
    public void ApplyTo_SetsControllerPercentage()
    {
        var config = new CutoverConfiguration { CutoverPercentage = 42 };
        var controller = new CutoverController(cutoverPercentage: 0);

        config.ApplyTo(controller);

        Assert.AreEqual(42, controller.CutoverPercentage);
    }

    [TestMethod]
    public void ApplyTo_NullController_Throws()
    {
        var config = new CutoverConfiguration();
        Assert.ThrowsException<ArgumentNullException>(() => config.ApplyTo(null!));
    }
}

// ===========================================================================
// 7. InMemoryExperimentRecorderTests — P0-9 持久化抽象默认实现
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
public sealed class InMemoryExperimentRecorderTests
{
    [TestMethod]
    public async Task RecordAsync_AddsFixtureToHistory()
    {
        var recorder = new InMemoryExperimentRecorder();
        var fixture = MakeFixture("fx-1");

        await recorder.RecordAsync(fixture);

        var history = await recorder.GetHistoryAsync();
        Assert.AreEqual(1, history.Count);
        Assert.AreEqual("fx-1", history[0].FixtureId);
    }

    [TestMethod]
    public async Task GetHistoryAsync_ReturnsAllRecordedInOrder()
    {
        var recorder = new InMemoryExperimentRecorder();
        await recorder.RecordAsync(MakeFixture("fx-1"));
        await recorder.RecordAsync(MakeFixture("fx-2"));
        await recorder.RecordAsync(MakeFixture("fx-3"));

        var history = await recorder.GetHistoryAsync();

        Assert.AreEqual(3, history.Count);
        Assert.AreEqual("fx-1", history[0].FixtureId);
        Assert.AreEqual("fx-2", history[1].FixtureId);
        Assert.AreEqual("fx-3", history[2].FixtureId);
    }

    [TestMethod]
    public async Task ClearAsync_RemovesAllFixtures()
    {
        var recorder = new InMemoryExperimentRecorder();
        await recorder.RecordAsync(MakeFixture("fx-1"));
        await recorder.RecordAsync(MakeFixture("fx-2"));

        await recorder.ClearAsync();

        var history = await recorder.GetHistoryAsync();
        Assert.AreEqual(0, history.Count);
    }

    [TestMethod]
    public async Task RecordAsync_ExceedsCapacity_EvictsOldest()
    {
        // 容量 = 3，写入 5 条，应保留最后 3 条
        var recorder = new InMemoryExperimentRecorder(maxCapacity: 3);

        for (var i = 1; i <= 5; i++)
        {
            await recorder.RecordAsync(MakeFixture($"fx-{i}"));
        }

        var history = await recorder.GetHistoryAsync();
        Assert.AreEqual(3, history.Count);
        Assert.AreEqual("fx-3", history[0].FixtureId);
        Assert.AreEqual("fx-4", history[1].FixtureId);
        Assert.AreEqual("fx-5", history[2].FixtureId);
    }

    [TestMethod]
    public async Task RecordAsync_NullFixture_Throws()
    {
        var recorder = new InMemoryExperimentRecorder();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            async () => await recorder.RecordAsync(null!));
    }

    [TestMethod]
    public void Constructor_InvalidCapacity_Throws()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new InMemoryExperimentRecorder(maxCapacity: 0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new InMemoryExperimentRecorder(maxCapacity: -1));
    }

    private static ReplayFixture MakeFixture(string fixtureId)
    {
        return new ReplayFixture(
            FixtureId: fixtureId,
            RecordedAt: DateTimeOffset.UtcNow,
            Purpose: "test",
            LegacySelectedCount: 1,
            V2SelectedCount: 1,
            CommonSelectedCount: 1,
            OnlyInLegacyCount: 0,
            OnlyInV2Count: 0,
            JaccardIndex: 1.0,
            LegacyTokenTotal: 100,
            V2TokenTotal: 100,
            WorkingSetCandidateCount: 1,
            ParityLevel: ParityLevel.Hard,
            Notes: "");
    }
}

// ===========================================================================
// 8. DecisionExperimentPlaneIntegrationTests — P0-9 集成入口
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
public sealed class DecisionExperimentPlaneIntegrationTests
{
    [TestMethod]
    public void ShouldRunSampledShadow_DisabledConfig_ReturnsFalse()
    {
        var integration = MakeIntegration(enableSampledShadow: false, sampleRate: 0.01);

        Assert.IsFalse(integration.ShouldRunSampledShadow("any-id"));
    }

    [TestMethod]
    public void ShouldRunSampledShadow_ZeroSampleRate_ReturnsFalse()
    {
        var integration = MakeIntegration(enableSampledShadow: true, sampleRate: 0.0);

        Assert.IsFalse(integration.ShouldRunSampledShadow("any-id"));
    }

    [TestMethod]
    public void ShouldRunSampledShadow_FullSampleRate_ReturnsTrue()
    {
        var integration = MakeIntegration(enableSampledShadow: true, sampleRate: 1.0);

        Assert.IsTrue(integration.ShouldRunSampledShadow("any-id"));
    }

    [TestMethod]
    public void ShouldRunSampledShadow_StableHashForSameRequestId()
    {
        // 同一 requestId 在同一采样率下应始终得到相同结果（稳定性）
        var integration = MakeIntegration(enableSampledShadow: true, sampleRate: 0.5);
        var firstResult = integration.ShouldRunSampledShadow("stable-id-42");

        for (var i = 0; i < 5; i++)
        {
            Assert.AreEqual(firstResult, integration.ShouldRunSampledShadow("stable-id-42"));
        }
    }

    [TestMethod]
    public async Task RecordShadowReport_Retrieval_PersistsFixtureWithWorkingSetAndV2Result()
    {
        // P0-9 回归：RecordShadowReport 携带完整 WorkingSet + V2Result
        var customRecorder = new InMemoryExperimentRecorder();
        var integration = MakeIntegration(recorder: customRecorder);

        var envelope = R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, 0.8, 100);
        var v2Result = R28BTestHelpers.MakeResult("op-1", selected: new[] { envelope }, estimatedTokens: 100);
        var workingSet = new CandidateWorkingSet
        {
            Envelopes = new[] { envelope },
            Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
            {
                [envelope.CanonicalKey] = R28BTestHelpers.MakeMaterial(envelope.CanonicalKey, "content body")
            }
        };
        var parity = new ParityReport(
            LegacySelectedCount: 1, V2SelectedCount: 1, CommonSelectedCount: 1,
            OnlyInLegacyCount: 0, OnlyInV2Count: 0,
            JaccardIndex: 1.0, ParityLevel: ParityLevel.Hard,
            LegacyTokenTotal: 100, V2TokenTotal: 100, WorkingSetCandidateCount: 1);
        var shadowReport = new RetrievalShadowReport(
            LegacyResult: new ContextRetrievalResult { OperationId = "op-1" },
            V2Result: v2Result,
            WorkingSet: workingSet,
            Parity: parity);

        integration.RecordShadowReport(shadowReport, "fx-1", "retrieval-mixed");

        var history = await customRecorder.GetHistoryAsync();
        Assert.AreEqual(1, history.Count);
        var fixture = history[0];
        Assert.AreEqual("fx-1", fixture.FixtureId);
        Assert.AreEqual("retrieval-mixed", fixture.Purpose);
        // P0-9：fixture 携带 WorkingSet
        Assert.IsNotNull(fixture.WorkingSet);
        Assert.AreEqual(1, fixture.WorkingSet!.Envelopes.Count);
        // P0-9：fixture 携带 V2Result
        Assert.IsNotNull(fixture.V2Result);
        Assert.AreEqual("op-1", fixture.V2Result!.RequestId);
    }

    [TestMethod]
    public async Task RecordShadowReport_Package_PersistsFixtureWithWorkingSetAndV2Result()
    {
        var customRecorder = new InMemoryExperimentRecorder();
        var integration = MakeIntegration(recorder: customRecorder);

        var envelope = R28BTestHelpers.MakeEnvelope("i1", ContextCandidateSource.WorkingMemory, 0.7, 100);
        var v2Result = R28BTestHelpers.MakeResult("build-1", selected: new[] { envelope }, estimatedTokens: 100);
        var workingSet = new CandidateWorkingSet
        {
            Envelopes = new[] { envelope },
            Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>()
        };
        var parity = new ParityReport(
            LegacySelectedCount: 1, V2SelectedCount: 1, CommonSelectedCount: 1,
            OnlyInLegacyCount: 0, OnlyInV2Count: 0,
            JaccardIndex: 1.0, ParityLevel: ParityLevel.Hard,
            LegacyTokenTotal: 100, V2TokenTotal: 100, WorkingSetCandidateCount: 1);

        var legacyResult = new ContextPackageBuildResult
        {
            BuildId = "build-1",
            SelectedItems = Array.Empty<ContextPackageDecision>(),
            DroppedItems = Array.Empty<DroppedContextItem>()
        };
        var shadowReport = new PackageShadowReport(
            LegacyResult: legacyResult,
            V2Result: v2Result,
            WorkingSet: workingSet,
            Parity: parity);

        integration.RecordShadowReport(shadowReport, "fx-pkg-1", "package-mixed");

        var history = await customRecorder.GetHistoryAsync();
        Assert.AreEqual(1, history.Count);
        Assert.AreEqual("fx-pkg-1", history[0].FixtureId);
        Assert.IsNotNull(history[0].WorkingSet);
        Assert.IsNotNull(history[0].V2Result);
    }

    [TestMethod]
    public async Task EvaluateHistoricalFixtures_NoFixtures_ReturnsNotReady()
    {
        var integration = MakeIntegration();

        var assessment = integration.EvaluateHistoricalFixtures();

        Assert.IsFalse(assessment.IsReady);
        Assert.AreEqual(0, assessment.TotalReports);
        Assert.AreEqual(ParityLevel.Divergent, assessment.OverallLevel);
    }

    [TestMethod]
    public async Task EvaluateHistoricalFixtures_AllHard_ReturnsReady()
    {
        var integration = MakeIntegration();

        // 记录 3 条全部 Hard parity 的 fixture
        for (var i = 0; i < 3; i++)
        {
            integration.RecordFixture(
                MakeHardParityReport(),
                fixtureId: $"fx-{i}",
                purpose: "ci-validation");
        }

        await Task.Yield(); // 让 RecordAsync 完成
        var assessment = integration.EvaluateHistoricalFixtures();

        Assert.IsTrue(assessment.IsReady);
        Assert.AreEqual(3, assessment.TotalReports);
        Assert.AreEqual(3, assessment.HardCount);
        Assert.AreEqual(0, assessment.DivergentCount);
    }

    [TestMethod]
    public async Task EvaluateHistoricalFixtures_AnyDivergent_ReturnsNotReady()
    {
        var integration = MakeIntegration();

        integration.RecordFixture(MakeHardParityReport(), "fx-1", "ci");
        integration.RecordFixture(MakeDivergentParityReport(), "fx-2", "ci");

        await Task.Yield();
        var assessment = integration.EvaluateHistoricalFixtures();

        Assert.IsFalse(assessment.IsReady);
        Assert.AreEqual(1, assessment.DivergentCount);
    }

    [TestMethod]
    public async Task ClearHistory_RemovesAllFixtures()
    {
        var integration = MakeIntegration();
        integration.RecordFixture(MakeHardParityReport(), "fx-1", "ci");
        integration.RecordFixture(MakeHardParityReport(), "fx-2", "ci");

        integration.ClearHistory();
        await Task.Yield();

        Assert.AreEqual(0, integration.FixtureHistory.Count);
    }

    [TestMethod]
    public async Task CustomRecorder_IsUsedInsteadOfDefault()
    {
        // P0-9 回归：integration 必须把 fixture 写入注入的 recorder，而非内部 List
        var customRecorder = new InMemoryExperimentRecorder();
        var integration = MakeIntegration(recorder: customRecorder);

        integration.RecordFixture(MakeHardParityReport(), "fx-1", "test");

        // 从 integration.FixtureHistory 读取（应委托给 customRecorder）
        Assert.AreEqual(1, integration.FixtureHistory.Count);
        // 直接从 customRecorder 读取（应与 integration.FixtureHistory 一致）
        var directHistory = await customRecorder.GetHistoryAsync();
        Assert.AreEqual(1, directHistory.Count);
        Assert.AreEqual("fx-1", directHistory[0].FixtureId);
    }

    private static DecisionExperimentPlaneIntegration MakeIntegration(
        bool enableSampledShadow = true,
        double sampleRate = 0.01,
        IExperimentRecorder? recorder = null)
    {
        return new DecisionExperimentPlaneIntegration(
            experimentPlane: new DecisionExperimentPlane(),
            gateEvaluator: new ShadowGateEvaluator(),
            configuration: new CutoverConfiguration
            {
                CutoverPercentage = 100,
                EnableSampledShadow = enableSampledShadow,
                ShadowSampleRate = sampleRate
            },
            recorder: recorder);
    }

    private static ParityReport MakeHardParityReport() => new(
        LegacySelectedCount: 2, V2SelectedCount: 2, CommonSelectedCount: 2,
        OnlyInLegacyCount: 0, OnlyInV2Count: 0,
        JaccardIndex: 1.0, ParityLevel: ParityLevel.Hard,
        LegacyTokenTotal: 200, V2TokenTotal: 200, WorkingSetCandidateCount: 2);

    private static ParityReport MakeDivergentParityReport() => new(
        LegacySelectedCount: 2, V2SelectedCount: 2, CommonSelectedCount: 0,
        OnlyInLegacyCount: 2, OnlyInV2Count: 2,
        JaccardIndex: 0.0, ParityLevel: ParityLevel.Divergent,
        LegacyTokenTotal: 200, V2TokenTotal: 200, WorkingSetCandidateCount: 2);
}

// ===========================================================================
// 9. ReplayFixtureTests — P0-9 完整重放数据传播
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
public sealed class ReplayFixtureTests
{
    [TestMethod]
    public void FromReport_ProducesScalarOnlyFixture_WithoutWorkingSetOrV2Result()
    {
        var report = new ParityReport(
            LegacySelectedCount: 3, V2SelectedCount: 3, CommonSelectedCount: 3,
            OnlyInLegacyCount: 0, OnlyInV2Count: 0,
            JaccardIndex: 1.0, ParityLevel: ParityLevel.Hard,
            LegacyTokenTotal: 300, V2TokenTotal: 300, WorkingSetCandidateCount: 3);

        var fixture = ReplayFixture.FromReport(report, fixtureId: "fx-1", purpose: "ci", notes: "n/a");

        Assert.AreEqual("fx-1", fixture.FixtureId);
        Assert.AreEqual("ci", fixture.Purpose);
        Assert.AreEqual(3, fixture.LegacySelectedCount);
        Assert.AreEqual(1.0, fixture.JaccardIndex);
        Assert.AreEqual(ParityLevel.Hard, fixture.ParityLevel);
        // P0-9：旧入口 FromReport 不携带完整重放数据
        Assert.IsNull(fixture.WorkingSet);
        Assert.IsNull(fixture.V2Result);
    }

    [TestMethod]
    public void FromShadowReport_PropagatesWorkingSetAndV2Result()
    {
        // P0-9 回归：FromShadowReport 应将 WorkingSet + V2Result 写入 fixture
        var envelope = R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, 0.8, 100);
        var v2Result = R28BTestHelpers.MakeResult("op-1", selected: new[] { envelope }, estimatedTokens: 100);
        var workingSet = new CandidateWorkingSet
        {
            Envelopes = new[] { envelope },
            Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
            {
                [envelope.CanonicalKey] = R28BTestHelpers.MakeMaterial(envelope.CanonicalKey, "body")
            }
        };
        var report = new ParityReport(
            LegacySelectedCount: 1, V2SelectedCount: 1, CommonSelectedCount: 1,
            OnlyInLegacyCount: 0, OnlyInV2Count: 0,
            JaccardIndex: 1.0, ParityLevel: ParityLevel.Hard,
            LegacyTokenTotal: 100, V2TokenTotal: 100, WorkingSetCandidateCount: 1);

        var fixture = ReplayFixture.FromShadowReport(
            report, workingSet, v2Result, fixtureId: "fx-2", purpose: "replay", notes: "");

        Assert.IsNotNull(fixture.WorkingSet);
        Assert.AreEqual(1, fixture.WorkingSet!.Envelopes.Count);
        Assert.IsNotNull(fixture.V2Result);
        Assert.AreEqual("op-1", fixture.V2Result!.RequestId);
        // 标量字段也应正确填充
        Assert.AreEqual(1, fixture.V2SelectedCount);
        Assert.AreEqual(1.0, fixture.JaccardIndex);
    }

    [TestMethod]
    public void FromShadowReport_WithNullWorkingSet_StillPropagatesV2Result()
    {
        // 边界：WorkingSet 可为 null（B-4 由 Store 访问填充时可能临时为 null）
        var v2Result = R28BTestHelpers.MakeResult("op-2");
        var report = new ParityReport(
            LegacySelectedCount: 0, V2SelectedCount: 0, CommonSelectedCount: 0,
            OnlyInLegacyCount: 0, OnlyInV2Count: 0,
            JaccardIndex: 1.0, ParityLevel: ParityLevel.Hard,
            LegacyTokenTotal: 0, V2TokenTotal: 0, WorkingSetCandidateCount: 0);

        var fixture = ReplayFixture.FromShadowReport(
            report, workingSet: null, v2Result: v2Result, fixtureId: "fx-3", purpose: "edge");

        Assert.IsNull(fixture.WorkingSet);
        Assert.IsNotNull(fixture.V2Result);
    }

    [TestMethod]
    public void FromShadowReport_NullReport_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            ReplayFixture.FromShadowReport(null!, workingSet: null, v2Result: null,
                fixtureId: "fx", purpose: "p"));
    }

    [TestMethod]
    public void FromReport_NullReport_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            ReplayFixture.FromReport(null!, fixtureId: "fx", purpose: "p"));
    }
}

// ===========================================================================
// 10. ShadowDecisionRuntimeTests — Shadow tee 编排 + Parity 报告
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
public sealed class ShadowDecisionRuntimeTests
{
    [TestMethod]
    public async Task ExecuteRetrievalShadowAsync_ProducesShadowReportWithParity()
    {
        // 验证 Shadow 编排：Legacy 结果 → WorkingSet → V2 决策 → Parity 报告
        var envelope = R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, 0.8, 100);
        var v2Result = R28BTestHelpers.MakeResult(
            requestId: "op-1",
            selected: new[] { envelope },
            estimatedTokens: 100);

        var stubRuntime = new StubDecisionRuntime(v2Result);
        var shadowRuntime = new ShadowDecisionRuntime(
            stubRuntime, new DecisionExperimentPlane());

        var legacyRequest = new ContextRetrievalRequest
        {
            OperationId = "op-1",
            WorkspaceId = "ws-1",
            CollectionId = "col-1",
            QueryText = "test query",
            TopK = 10
        };
        var legacyResult = new ContextRetrievalResult
        {
            OperationId = "op-1",
            SelectedItems = new[]
            {
                new ContextRetrievalCandidate
                {
                    CandidateId = "c1",
                    SourceId = "c1",
                    Score = 0.8,
                    EstimatedTokens = 100,
                    Content = "legacy content"
                }
            },
            EstimatedTokens = 100
        };

        var context = new CandidateAdaptationContext
        {
            WorkspaceId = "ws-1",
            CollectionId = "col-1",
            RequestId = "op-1",
            QueryText = "test query",
            ObservedAt = DateTimeOffset.UtcNow
        };

        var report = await shadowRuntime.ExecuteRetrievalShadowAsync(
            legacyRequest, legacyResult, tokenBudget: 1000, topK: 10,
            context: context, cancellationToken: CancellationToken.None);

        Assert.IsNotNull(report);
        Assert.IsNotNull(report.WorkingSet);
        Assert.AreEqual(1, report.WorkingSet.Envelopes.Count);
        Assert.IsNotNull(report.V2Result);
        Assert.AreEqual("op-1", report.V2Result.RequestId);
        Assert.IsNotNull(report.Parity);
        // V2 runtime 应被调用一次
        Assert.AreEqual(1, stubRuntime.ExecuteCallCount);
    }

    [TestMethod]
    public async Task ExecuteRetrievalShadowAsync_LegacySelectedExcludesDropped()
    {
        // P0-4 回归：Shadow 报告的 Legacy SelectedEnvelopes 不应包含 dropped 候选
        var envelope = R28BTestHelpers.MakeEnvelope("selected-1", ContextCandidateSource.Semantic, 0.8, 100);
        var v2Result = R28BTestHelpers.MakeResult(
            requestId: "op-2",
            selected: new[] { envelope },
            estimatedTokens: 100);

        var stubRuntime = new StubDecisionRuntime(v2Result);
        var shadowRuntime = new ShadowDecisionRuntime(
            stubRuntime, new DecisionExperimentPlane());

        var legacyRequest = new ContextRetrievalRequest
        {
            OperationId = "op-2",
            WorkspaceId = "ws-1",
            CollectionId = "col-1"
        };
        // Legacy 结果：1 个 selected + 1 个 dropped
        var legacyResult = new ContextRetrievalResult
        {
            OperationId = "op-2",
            SelectedItems = new[]
            {
                new ContextRetrievalCandidate
                {
                    CandidateId = "selected-1",
                    SourceId = "selected-1",
                    Score = 0.8,
                    EstimatedTokens = 100
                }
            },
            DroppedItems = new[]
            {
                new ContextRetrievalDecision
                {
                    CandidateId = "dropped-1",
                    SourceId = "dropped-1",
                    Reason = "budget exceeded"
                }
            },
            EstimatedTokens = 100
        };

        var context = new CandidateAdaptationContext
        {
            WorkspaceId = "ws-1",
            CollectionId = "col-1",
            RequestId = "op-2",
            ObservedAt = DateTimeOffset.UtcNow
        };

        var report = await shadowRuntime.ExecuteRetrievalShadowAsync(
            legacyRequest, legacyResult, tokenBudget: 1000, topK: 10,
            context: context, cancellationToken: CancellationToken.None);

        // Parity 中 Legacy selected count 应为 1（不含 dropped-1）
        Assert.AreEqual(1, report.Parity.LegacySelectedCount);
        Assert.AreEqual(1, report.Parity.CommonSelectedCount);
        Assert.AreEqual(1.0, report.Parity.JaccardIndex);
    }

    [TestMethod]
    public async Task ExecuteRetrievalShadowAsync_NullArguments_Throw()
    {
        var shadowRuntime = new ShadowDecisionRuntime(
            new StubDecisionRuntime(R28BTestHelpers.MakeResult("op")),
            new DecisionExperimentPlane());

        var context = new CandidateAdaptationContext
        {
            WorkspaceId = "ws",
            CollectionId = "col",
            RequestId = "op",
            ObservedAt = DateTimeOffset.UtcNow
        };
        var request = new ContextRetrievalRequest { OperationId = "op" };
        var result = new ContextRetrievalResult();

        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            async () => await shadowRuntime.ExecuteRetrievalShadowAsync(
                null!, result, 100, 10, context, CancellationToken.None));
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            async () => await shadowRuntime.ExecuteRetrievalShadowAsync(
                request, null!, 100, 10, context, CancellationToken.None));
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            async () => await shadowRuntime.ExecuteRetrievalShadowAsync(
                request, result, 100, 10, null!, CancellationToken.None));
    }

    /// <summary>
    /// 测试桩：可配置返回值的 IContextDecisionRuntime，避免依赖真实 Engine 编排。
    /// </summary>
    private sealed class StubDecisionRuntime : IContextDecisionRuntime
    {
        private readonly ContextDecisionResult _result;
        public int ExecuteCallCount { get; private set; }
        public ContextDecisionRuntimeRequest? LastRequest { get; private set; }

        public StubDecisionRuntime(ContextDecisionResult result)
        {
            _result = result ?? throw new ArgumentNullException(nameof(result));
        }

        public ValueTask<ContextDecisionResult> ExecuteAsync(
            ContextDecisionRuntimeRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteCallCount++;
            LastRequest = request;
            return new ValueTask<ContextDecisionResult>(_result);
        }
    }
}

// ===========================================================================
// 11. ShadowGateEvaluatorTests — 批量评估 + cutover 就绪判定
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
public sealed class ShadowGateEvaluatorTests
{
    [TestMethod]
    public void EvaluateBatch_EmptyReports_ReturnsNotReady()
    {
        var evaluator = new ShadowGateEvaluator();

        var assessment = evaluator.EvaluateBatch(Array.Empty<ParityReport>());

        Assert.IsFalse(assessment.IsReady);
        Assert.AreEqual(0, assessment.TotalReports);
        Assert.AreEqual(ParityLevel.Divergent, assessment.OverallLevel);
    }

    [TestMethod]
    public void EvaluateBatch_AllHardReports_ReturnsReady()
    {
        var evaluator = new ShadowGateEvaluator();
        var reports = new[]
        {
            MakeReport(1.0, ParityLevel.Hard, 0, 0),
            MakeReport(1.0, ParityLevel.Hard, 0, 0),
            MakeReport(1.0, ParityLevel.Hard, 0, 0)
        };

        var assessment = evaluator.EvaluateBatch(reports);

        Assert.IsTrue(assessment.IsReady);
        Assert.AreEqual(3, assessment.HardCount);
        Assert.AreEqual(0, assessment.DivergentCount);
    }

    [TestMethod]
    public void EvaluateBatch_AnyDivergent_ReturnsNotReady()
    {
        var evaluator = new ShadowGateEvaluator();
        var reports = new[]
        {
            MakeReport(1.0, ParityLevel.Hard, 0, 0),
            MakeReport(0.5, ParityLevel.Divergent, 1, 1) // Divergent
        };

        var assessment = evaluator.EvaluateBatch(reports);

        Assert.IsFalse(assessment.IsReady);
        Assert.AreEqual(1, assessment.DivergentCount);
    }

    [TestMethod]
    public void EvaluateBatch_TooManyDiagnostic_ReturnsNotReady()
    {
        // Diagnostic 比例 > 20% → NotReady
        var evaluator = new ShadowGateEvaluator();
        var reports = new[]
        {
            MakeReport(1.0, ParityLevel.Hard, 0, 0), // 1 hard
            MakeReport(0.95, ParityLevel.Diagnostic, 0, 1), // diag
            MakeReport(0.95, ParityLevel.Diagnostic, 0, 1), // diag
            MakeReport(0.95, ParityLevel.Diagnostic, 0, 1) // diag → 75% diag > 20%
        };

        var assessment = evaluator.EvaluateBatch(reports);

        Assert.IsFalse(assessment.IsReady);
        Assert.AreEqual(0, assessment.DivergentCount);
        Assert.AreEqual(3, assessment.DiagnosticCount);
    }

    [TestMethod]
    public void EvaluateBatch_NullReports_Throws()
    {
        var evaluator = new ShadowGateEvaluator();
        Assert.ThrowsException<ArgumentNullException>(() => evaluator.EvaluateBatch(null!));
    }

    private static ParityReport MakeReport(
        double jaccard, ParityLevel level, int onlyInLegacy, int onlyInV2) => new(
        LegacySelectedCount: 2, V2SelectedCount: 2, CommonSelectedCount: 2 - onlyInLegacy,
        OnlyInLegacyCount: onlyInLegacy, OnlyInV2Count: onlyInV2,
        JaccardIndex: jaccard, ParityLevel: level,
        LegacyTokenTotal: 200, V2TokenTotal: 200, WorkingSetCandidateCount: 2);
}
