using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Policy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextCore.Tests;

// ===========================================================================
// Allocator V2.1 验收测试（section rollover + MMR diversity）
//
// 覆盖：
// 1. 空候选 / mandatory 优先 / 预算截断 / section 顺序
// 2. MMR diversity 重排序（lambda=0.5 / 1.0 / 0.0）
// 3. Section rollover 启用 / 禁用 / RolloverRatio 缩放
// 4. 确定性验证（相同输入 → 相同输出）
// 5. 基接口委托（IGlobalAllocator.Allocate 委托给 base allocator）
// 6. 诊断信息（Outcome.Diagnostics 含 V2.1 特有字段）
// 7. DI 注册验证（IAllocatorV2_1 可从 ServiceCollection 解析）
//
// 设计原则：
// - 复用共享 TestHelpers 的 MakeEnvelope 构建候选（保持与同类测试一致）
// - 所有代码注释使用中文
// ===========================================================================

/// <summary>
/// Allocator V2.1 验收测试。
/// </summary>
[TestClass]
[TestCategory("R28")]
[TestCategory("DecisionEngine")]
public sealed class R28B_AllocatorV2_1Tests
{
    // =======================================================================
    // 辅助方法
    // =======================================================================

    /// <summary>构建 AllocationContext（Retrieval 用途，允许 mandatory 溢出）。</summary>
    private static AllocationContext MakeContext(int tokenBudget) => new()
    {
        Purpose = ContextDecisionPurpose.Retrieval,
        Budget = new BudgetProfile
        {
            ProfileId = "test-budget",
            DefaultTokenBudget = tokenBudget,
            DefaultTopK = 50
        },
        MandatoryOverflowPolicy = MandatoryOverflowPolicy.AllowOverflowWithDiagnostic
    };

    /// <summary>构建带 mandatory 标记的候选。</summary>
    private static ContextCandidateEnvelope MakeMandatoryEnvelope(
        string candidateId,
        int tokens,
        double score = 1.0) => new()
    {
        CandidateId = candidateId,
        CanonicalKey = CanonicalCandidateKey.Create(
            workspaceId: "test-ws",
            collectionId: "test-col",
            entityKind: "mandatory",
            entityId: candidateId,
            entityVersion: "v1"),
        Source = ContextCandidateSource.Mandatory,
        Type = "mandatory-type",
        TokenCost = new CandidateTokenCost
        {
            ContentTokens = tokens,
            TokenizerId = "length-div-4",
            IsEstimated = true
        },
        Safety = new CandidateSafetyState { IsMandatory = true, PassesSafetyGate = true },
        Utility = new CandidateUtilityScore { DeterministicScore = score, FinalScore = score, ReasonCode = "mandatory" }
    };

    /// <summary>构建普通（non-mandatory）候选。</summary>
    private static ContextCandidateEnvelope MakeNonMandatoryEnvelope(
        string candidateId,
        ContextCandidateSource source,
        int tokens,
        double score,
        string type = "test-type") => new()
    {
        CandidateId = candidateId,
        CanonicalKey = CanonicalCandidateKey.Create(
            workspaceId: "test-ws",
            collectionId: "test-col",
            entityKind: "test-entity",
            entityId: candidateId,
            entityVersion: "v1"),
        Source = source,
        Type = type,
        TokenCost = new CandidateTokenCost
        {
            ContentTokens = tokens,
            TokenizerId = "length-div-4",
            IsEstimated = true
        },
        Safety = new CandidateSafetyState { PassesSafetyGate = true },
        Utility = new CandidateUtilityScore { DeterministicScore = score, FinalScore = score, ReasonCode = "test" }
    };

    /// <summary>构建 V2.1 分配器实例。</summary>
    private static DefaultAllocatorV2_1 MakeAllocator() => new(new DefaultGlobalAllocator());

    /// <summary>构建最小化 EffectivePolicySnapshot（用于基接口委托测试）。</summary>
    private static EffectivePolicySnapshot MakeSnapshot()
    {
        var bundle = DefaultPolicyBundleFactory.Create();
        return new EffectivePolicySnapshot
        {
            Reference = new ResolvedPolicyReference
            {
                BundleId = bundle.BundleId,
                BundleVersion = bundle.Version,
                BundleContentHash = DefaultResolvedPolicyProvider.DefaultContentHash,
                ActivationEpoch = DefaultResolvedPolicyProvider.DefaultActivationEpoch
            },
            Safety = bundle.Safety,
            Budget = bundle.Budget,
            Routing = bundle.Routing,
            FeatureSchemaVersion = bundle.Policies.DecisionSchemaVersion,
            ResolutionScope = new ContextDecisionScope("test-ws", "test-col")
        };
    }

    // =======================================================================
    // 1. 基础行为
    // =======================================================================

    [TestMethod]
    public void AllocateWithDiversity_EmptyCandidates_ReturnsEmptyResult()
    {
        // 空候选集合：返回空结果，不抛异常
        var allocator = MakeAllocator();
        var context = MakeContext(tokenBudget: 1000);
        var options = new DiversityOptions();

        var result = allocator.AllocateWithDiversity(
            Array.Empty<ContextCandidateEnvelope>(), context, options);

        Assert.AreEqual(0, result.Selected.Count, "空候选应产出 0 selected。");
        Assert.AreEqual(0, result.Dropped.Count, "空候选应产出 0 dropped。");
        Assert.AreEqual(0, result.AllocationDecisions.Count, "空候选应产出 0 decisions。");
        Assert.AreEqual(0, result.Outcome.EffectiveTokens, "空候选 estimated tokens 应为 0。");
    }

    [TestMethod]
    public void AllocateWithDiversity_MandatoryAlwaysSelected_RegardlessOfBudget()
    {
        // mandatory 候选始终选入（即使超出预算），overflow 允许
        var allocator = MakeAllocator();
        var context = MakeContext(tokenBudget: 50); // 预算 50
        var options = new DiversityOptions();

        var mandatory = MakeMandatoryEnvelope("m-1", tokens: 100); // 100 tokens，超出预算

        var result = allocator.AllocateWithDiversity(
            new[] { mandatory }, context, options);

        Assert.AreEqual(1, result.Selected.Count, "mandatory 候选必须被选入（overflow 允许）。");
        Assert.AreEqual(0, result.Dropped.Count);
        // 验证 decision 的 ReasonCode 为 SelectedMandatory
        var decision = result.AllocationDecisions.Single();
        Assert.AreEqual(CandidateDecisionReasonCode.SelectedMandatory, decision.ReasonCode);
        Assert.AreEqual(100, decision.IncludedTokens, "mandatory 候选不应被截断。");
        Assert.IsFalse(decision.IsTruncated, "mandatory 候选不应被截断。");
    }

    [TestMethod]
    public void AllocateWithDiversity_NonMandatoryRespectsBudget_DropsExcess()
    {
        // non-mandatory 候选尊重预算：预算耗尽后，后续候选被完全丢弃
        var allocator = MakeAllocator();
        var context = MakeContext(tokenBudget: 100); // 预算 100，仅够 c-1
        var options = new DiversityOptions { Lambda = 1.0 }; // 纯 relevance，禁用 MMR

        var c1 = MakeNonMandatoryEnvelope("c-1", ContextCandidateSource.Semantic, tokens: 100, score: 0.9);
        var c2 = MakeNonMandatoryEnvelope("c-2", ContextCandidateSource.Semantic, tokens: 100, score: 0.5);

        var result = allocator.AllocateWithDiversity(
            new[] { c1, c2 }, context, options);

        // c-1 分数更高，优先选入并耗尽预算；c-2 因预算耗尽被完全丢弃
        Assert.AreEqual(1, result.Selected.Count, "只有 c-1 应被选入（预算仅够 100 tokens）。");
        Assert.IsTrue(result.Selected.Any(e => e.CandidateId == "c-1"));
        Assert.AreEqual(1, result.Dropped.Count);
        Assert.IsTrue(result.Dropped.Any(e => e.CandidateId == "c-2"));

        // c-2 的 decision 应为 TokenBudgetExceeded
        var c2Decision = result.AllocationDecisions.First(d => d.CandidateKey == c2.CanonicalKey);
        Assert.AreEqual(CandidateDecisionReasonCode.TokenBudgetExceeded, c2Decision.ReasonCode);
        Assert.AreEqual(0, c2Decision.IncludedTokens);
    }

    [TestMethod]
    public void AllocateWithDiversity_PartialTruncation_WhenBudgetInsufficient()
    {
        // 候选部分截断：剩余预算不足以完整包含候选时，截断到剩余预算
        var allocator = MakeAllocator();
        var context = MakeContext(tokenBudget: 150); // 预算 150
        var options = new DiversityOptions { Lambda = 1.0 };

        var c1 = MakeNonMandatoryEnvelope("c-1", ContextCandidateSource.Semantic, tokens: 100, score: 0.9);
        var c2 = MakeNonMandatoryEnvelope("c-2", ContextCandidateSource.Lexical, tokens: 100, score: 0.5);

        var result = allocator.AllocateWithDiversity(
            new[] { c1, c2 }, context, options);

        // c-1 完整选入（100 tokens），c-2 部分截断（剩余 50 tokens）
        Assert.AreEqual(2, result.Selected.Count, "两个候选都应被选入（一个完整，一个截断）。");

        var c2Decision = result.AllocationDecisions.First(d => d.CandidateKey == c2.CanonicalKey);
        Assert.IsTrue(c2Decision.IsTruncated, "c-2 应被截断。");
        Assert.AreEqual(50, c2Decision.IncludedTokens, "c-2 截断后应剩余 50 tokens。");
    }

    // =======================================================================
    // 2. Section 分组与顺序
    // =======================================================================

    [TestMethod]
    public void AllocateWithDiversity_SectionsAllocatedInPriorityOrder()
    {
        // section 分配顺序：mandatory → memory → relations → global → related → default
        var allocator = MakeAllocator();
        var context = MakeContext(tokenBudget: 1000);
        var options = new DiversityOptions { Lambda = 1.0 };

        var mandatory = MakeMandatoryEnvelope("m-1", tokens: 50, score: 0.5);
        var memory = MakeNonMandatoryEnvelope("mem-1", ContextCandidateSource.WorkingMemory, tokens: 50, score: 0.8);
        var relations = MakeNonMandatoryEnvelope("rel-1", ContextCandidateSource.Graph, tokens: 50, score: 0.6);

        var result = allocator.AllocateWithDiversity(
            new[] { relations, memory, mandatory }, context, options);

        // 验证 sections 按优先级排序
        var sections = result.Outcome.Sections.ToList();
        Assert.IsTrue(sections.IndexOf("mandatory") < sections.IndexOf("memory"),
            "mandatory section 应在 memory 之前分配。");
        Assert.IsTrue(sections.IndexOf("memory") < sections.IndexOf("relations"),
            "memory section 应在 relations 之前分配。");
    }

    // =======================================================================
    // 3. MMR Diversity
    // =======================================================================

    [TestMethod]
    public void AllocateWithDiversity_LambdaOne_PureRelevance_NoMmr()
    {
        // Lambda=1.0：纯 relevance 排序，禁用 MMR，按 FinalScore 降序选入
        var allocator = MakeAllocator();
        var context = MakeContext(tokenBudget: 1000);
        var options = new DiversityOptions { Lambda = 1.0 };

        // 3 个同 section 候选，分数递减
        var c1 = MakeNonMandatoryEnvelope("c-1", ContextCandidateSource.Semantic, tokens: 100, score: 0.9, type: "A");
        var c2 = MakeNonMandatoryEnvelope("c-2", ContextCandidateSource.Semantic, tokens: 100, score: 0.7, type: "A");
        var c3 = MakeNonMandatoryEnvelope("c-3", ContextCandidateSource.Semantic, tokens: 100, score: 0.5, type: "B");

        var result = allocator.AllocateWithDiversity(
            new[] { c2, c3, c1 }, context, options);

        // 全部选入（预算充足）。Selected 列表保持输入顺序（便于 trace 溯源），
        // 不按分数重排；relevance 顺序体现在 AllocationDecisions 中。
        Assert.AreEqual(3, result.Selected.Count);
        var selectedIds = result.Selected.Select(e => e.CandidateId).ToHashSet();
        Assert.IsTrue(selectedIds.Contains("c-1"));
        Assert.IsTrue(selectedIds.Contains("c-2"));
        Assert.IsTrue(selectedIds.Contains("c-3"));

        // AllocationDecisions 应按分数降序排列（c-1 先于 c-2 先于 c-3）
        var includedDecisionIds = result.AllocationDecisions
            .Where(d => d.IncludedTokens > 0)
            .Select(d => d.CandidateKey.EntityId)
            .ToList();
        Assert.AreEqual(3, includedDecisionIds.Count);
        Assert.AreEqual("c-1", includedDecisionIds[0], "AllocationDecisions 应按分数降序，c-1 最先分配。");
        Assert.AreEqual("c-2", includedDecisionIds[1]);
        Assert.AreEqual("c-3", includedDecisionIds[2]);
    }

    [TestMethod]
    public void AllocateWithDiversity_LambdaHalf_PromotesDiversity()
    {
        // Lambda=0.5：MMR 启用，相同 Type 的候选相似度高，diverse 候选被提前
        var allocator = MakeAllocator();
        var context = MakeContext(tokenBudget: 300); // 预算仅够 3 个候选
        var options = new DiversityOptions { Lambda = 0.5 };

        // 4 个同 section 候选：3 个 Type=A（高相似），1 个 Type=B（低相似）
        var a1 = MakeNonMandatoryEnvelope("a-1", ContextCandidateSource.Semantic, tokens: 100, score: 0.9, type: "A");
        var a2 = MakeNonMandatoryEnvelope("a-2", ContextCandidateSource.Semantic, tokens: 100, score: 0.85, type: "A");
        var a3 = MakeNonMandatoryEnvelope("a-3", ContextCandidateSource.Semantic, tokens: 100, score: 0.8, type: "A");
        var b1 = MakeNonMandatoryEnvelope("b-1", ContextCandidateSource.Lexical, tokens: 100, score: 0.6, type: "B");

        var result = allocator.AllocateWithDiversity(
            new[] { a1, a2, a3, b1 }, context, options);

        // 预算 300 仅够 3 个候选。MMR 应优先选入 diverse 的 b-1（Type=B）
        // 而非第 3 个 Type=A 候选（a-3），因为 a-3 与已选的 a-1/a-2 高度相似。
        Assert.AreEqual(3, result.Selected.Count, "预算 300 应选入 3 个候选。");
        Assert.IsTrue(result.Selected.Any(e => e.CandidateId == "b-1"),
            "MMR 应优先选入 diverse 的 b-1（Type=B）而非第 3 个相似候选。");
    }

    [TestMethod]
    public void AllocateWithDiversity_LambdaZero_PureDiversity()
    {
        // Lambda=0.0：纯 diversity 排序，最大化候选间差异
        var allocator = MakeAllocator();
        var context = MakeContext(tokenBudget: 300);
        var options = new DiversityOptions { Lambda = 0.0 };

        var a1 = MakeNonMandatoryEnvelope("a-1", ContextCandidateSource.Semantic, tokens: 100, score: 0.9, type: "A");
        var b1 = MakeNonMandatoryEnvelope("b-1", ContextCandidateSource.Lexical, tokens: 100, score: 0.5, type: "B");

        var result = allocator.AllocateWithDiversity(
            new[] { a1, b1 }, context, options);

        // 两个不同 Type 的候选都应被选入
        Assert.AreEqual(2, result.Selected.Count);
    }

    // =======================================================================
    // 4. Section Rollover
    // =======================================================================

    [TestMethod]
    public void AllocateWithDiversity_RolloverEnabled_UnusedBudgetTransferredToNextSection()
    {
        // 启用 rollover：第一个 section 未用完的预算结转到下一 section
        var allocator = MakeAllocator();
        var context = MakeContext(tokenBudget: 200); // 总预算 200
        var options = new DiversityOptions
        {
            Lambda = 1.0,
            EnableSectionRollover = true,
            RolloverRatio = 1.0
        };

        // mandatory section：仅 50 tokens，剩余 150 应 rollover 到 memory section
        var mandatory = MakeMandatoryEnvelope("m-1", tokens: 50);
        // memory section：150 tokens，刚好用完 rollover 的预算
        var memory = MakeNonMandatoryEnvelope("mem-1", ContextCandidateSource.WorkingMemory, tokens: 150, score: 0.8);

        var result = allocator.AllocateWithDiversity(
            new[] { mandatory, memory }, context, options);

        // 两个候选都应被选入（rollover 让 memory section 获得 150 tokens 预算）
        Assert.AreEqual(2, result.Selected.Count, "rollover 应让 memory section 获得剩余预算。");

        // 验证 diagnostics 中 memory section 的 borrowed tokens
        Assert.IsTrue(result.Outcome.Diagnostics!.ContainsKey("section.memory.borrowed"),
            "diagnostics 应记录 memory section 的 borrowed tokens。");
    }

    [TestMethod]
    public void AllocateWithDiversity_RolloverDisabled_EachSectionGetsEqualShare()
    {
        // 禁用 rollover：每个 section 获得等分预算，未用完的不结转
        var allocator = MakeAllocator();
        var context = MakeContext(tokenBudget: 200); // 总预算 200，2 个 section 各 100
        var options = new DiversityOptions
        {
            Lambda = 1.0,
            EnableSectionRollover = false
        };

        // mandatory section：仅 50 tokens，剩余 50 不结转
        var mandatory = MakeMandatoryEnvelope("m-1", tokens: 50);
        // memory section：需要 150 tokens，但只有等分 100 tokens，不够
        var memory = MakeNonMandatoryEnvelope("mem-1", ContextCandidateSource.WorkingMemory, tokens: 150, score: 0.8);

        var result = allocator.AllocateWithDiversity(
            new[] { mandatory, memory }, context, options);

        // memory section 只获得等分预算（100），mem-1 需 150 tokens，应被部分截断
        Assert.AreEqual(2, result.Selected.Count, "两个候选都应被选入。");
        var memDecision = result.AllocationDecisions.First(d => d.CandidateKey == memory.CanonicalKey);
        Assert.IsTrue(memDecision.IsTruncated, "禁用 rollover 时 mem-1 应被截断到等分预算。");
        Assert.AreEqual(100, memDecision.IncludedTokens, "mem-1 截断后应为等分预算 100 tokens。");
    }

    [TestMethod]
    public void AllocateWithDiversity_RolloverRatio_ScalesTransferredBudget()
    {
        // RolloverRatio=0.5：结转的预算按 50% 缩放
        var allocator = MakeAllocator();
        var context = MakeContext(tokenBudget: 200);
        var options = new DiversityOptions
        {
            Lambda = 1.0,
            EnableSectionRollover = true,
            RolloverRatio = 0.5 // 仅结转 50%
        };

        // mandatory section：50 tokens，剩余 150，但仅 75（50%）结转
        var mandatory = MakeMandatoryEnvelope("m-1", tokens: 50);
        // memory section：需要 75 tokens，刚好匹配结转的 75
        var memory = MakeNonMandatoryEnvelope("mem-1", ContextCandidateSource.WorkingMemory, tokens: 75, score: 0.8);

        var result = allocator.AllocateWithDiversity(
            new[] { mandatory, memory }, context, options);

        // memory section 获得 75 tokens（150 * 0.5），mem-1 刚好 75 tokens
        Assert.AreEqual(2, result.Selected.Count);
        var memDecision = result.AllocationDecisions.First(d => d.CandidateKey == memory.CanonicalKey);
        Assert.IsFalse(memDecision.IsTruncated, "mem-1 应完整包含（75 tokens 刚好匹配 rollover 预算）。");
        Assert.AreEqual(75, memDecision.IncludedTokens);
    }

    // =======================================================================
    // 5. 确定性
    // =======================================================================

    [TestMethod]
    public void AllocateWithDiversity_SameInput_ProducesSameOutput()
    {
        // 确定性验证：相同输入 + 相同 options 产出相同结果
        var allocator = MakeAllocator();
        var context = MakeContext(tokenBudget: 500);
        var options = new DiversityOptions { Lambda = 0.5 };

        var candidates = new[]
        {
            MakeNonMandatoryEnvelope("c-1", ContextCandidateSource.Semantic, tokens: 100, score: 0.9, type: "A"),
            MakeNonMandatoryEnvelope("c-2", ContextCandidateSource.Semantic, tokens: 100, score: 0.7, type: "A"),
            MakeNonMandatoryEnvelope("c-3", ContextCandidateSource.Lexical, tokens: 100, score: 0.6, type: "B")
        };

        var result1 = allocator.AllocateWithDiversity(candidates, context, options);
        var result2 = allocator.AllocateWithDiversity(candidates, context, options);

        // 验证 selected 列表相同（顺序 + 内容）
        Assert.AreEqual(result1.Selected.Count, result2.Selected.Count);
        for (var i = 0; i < result1.Selected.Count; i++)
        {
            Assert.AreEqual(result1.Selected[i].CandidateId, result2.Selected[i].CandidateId,
                $"位置 {i} 的 selected 候选应相同（确定性）。");
        }

        // 验证 decisions 相同
        Assert.AreEqual(result1.AllocationDecisions.Count, result2.AllocationDecisions.Count);
        for (var i = 0; i < result1.AllocationDecisions.Count; i++)
        {
            Assert.AreEqual(result1.AllocationDecisions[i].CandidateKey, result2.AllocationDecisions[i].CandidateKey);
            Assert.AreEqual(result1.AllocationDecisions[i].IncludedTokens, result2.AllocationDecisions[i].IncludedTokens);
        }
    }

    [TestMethod]
    public void AllocateWithDiversity_DifferentInputOrder_SameSelectedSet()
    {
        // 不同输入顺序：selected 集合应相同（虽然顺序可能不同）
        var allocator = MakeAllocator();
        var context = MakeContext(tokenBudget: 1000);
        var options = new DiversityOptions { Lambda = 1.0 };

        var c1 = MakeNonMandatoryEnvelope("c-1", ContextCandidateSource.Semantic, tokens: 100, score: 0.9);
        var c2 = MakeNonMandatoryEnvelope("c-2", ContextCandidateSource.Lexical, tokens: 100, score: 0.7);

        var result1 = allocator.AllocateWithDiversity(new[] { c1, c2 }, context, options);
        var result2 = allocator.AllocateWithDiversity(new[] { c2, c1 }, context, options);

        // selected 数量应相同
        Assert.AreEqual(result1.Selected.Count, result2.Selected.Count);
        // selected 的 CandidateId 集合应相同（不考虑顺序）
        var ids1 = result1.Selected.Select(e => e.CandidateId).ToHashSet();
        var ids2 = result2.Selected.Select(e => e.CandidateId).ToHashSet();
        Assert.IsTrue(ids1.SetEquals(ids2), "不同输入顺序应产出相同的 selected 集合。");
    }

    // =======================================================================
    // 6. 基接口委托
    // =======================================================================

    [TestMethod]
    public void Allocate_BaseInterface_DelegatesToBaseAllocator()
    {
        // IGlobalAllocator.Allocate(envelopes, snapshot) 应委托给 base allocator
        var baseAllocator = new DefaultGlobalAllocator();
        var allocator = new DefaultAllocatorV2_1(baseAllocator);

        var envelopes = new[]
        {
            R28BTestHelpers.MakeEnvelope("c-1", ContextCandidateSource.Semantic, 0.8, 100)
        };
        var snapshot = MakeSnapshot();

        var v2Result = allocator.Allocate(envelopes, snapshot);
        var baseResult = baseAllocator.Allocate(envelopes, snapshot);

        // 委托结果应与 base allocator 直接调用一致
        Assert.AreEqual(baseResult.Selected.Count, v2Result.Selected.Count);
        Assert.AreEqual(baseResult.Dropped.Count, v2Result.Dropped.Count);
    }

    [TestMethod]
    public void Allocate_WithContext_DelegatesToBaseAllocator()
    {
        // IGlobalAllocator.Allocate(envelopes, snapshot, context) 应委托给 base allocator
        var baseAllocator = new DefaultGlobalAllocator();
        var allocator = new DefaultAllocatorV2_1(baseAllocator);

        var envelopes = new[]
        {
            R28BTestHelpers.MakeEnvelope("c-1", ContextCandidateSource.Semantic, 0.8, 100)
        };
        var snapshot = MakeSnapshot();
        var context = MakeContext(tokenBudget: 1000);

        var v2Result = allocator.Allocate(envelopes, snapshot, context);
        var baseResult = baseAllocator.Allocate(envelopes, snapshot, context);

        Assert.AreEqual(baseResult.Selected.Count, v2Result.Selected.Count);
    }

    // =======================================================================
    // 7. 诊断信息
    // =======================================================================

    [TestMethod]
    public void AllocateWithDiversity_OutcomeContainsV21Diagnostics()
    {
        // Outcome.Diagnostics 应包含 V2.1 特有字段
        var allocator = MakeAllocator();
        var context = MakeContext(tokenBudget: 1000);
        var options = new DiversityOptions { Lambda = 0.5, EnableSectionRollover = true };

        var c1 = MakeNonMandatoryEnvelope("c-1", ContextCandidateSource.Semantic, tokens: 100, score: 0.9);
        var result = allocator.AllocateWithDiversity(new[] { c1 }, context, options);

        Assert.IsNotNull(result.Outcome.Diagnostics);
        Assert.IsTrue(result.Outcome.Diagnostics.ContainsKey("AllocatorVersion"));
        Assert.AreEqual("V2.1", result.Outcome.Diagnostics["AllocatorVersion"]);
        Assert.IsTrue(result.Outcome.Diagnostics.ContainsKey("DiversityLambda"));
        Assert.IsTrue(result.Outcome.Diagnostics.ContainsKey("SectionRolloverEnabled"));
        Assert.IsTrue(result.Outcome.Diagnostics.ContainsKey("RolloverRatio"));
        Assert.IsTrue(result.Outcome.Diagnostics.ContainsKey("SectionCount"));
    }

    [TestMethod]
    public void AllocateWithDiversity_SingleSection_DiagnosticsContainsSectionDetails()
    {
        // 单 section 分配后，diagnostics 应记录该 section 的 allocated / rollover / borrowed
        var allocator = MakeAllocator();
        var context = MakeContext(tokenBudget: 500);
        var options = new DiversityOptions { Lambda = 1.0 };

        var c1 = MakeNonMandatoryEnvelope("c-1", ContextCandidateSource.Semantic, tokens: 100, score: 0.9);
        var result = allocator.AllocateWithDiversity(new[] { c1 }, context, options);

        Assert.IsNotNull(result.Outcome.Diagnostics);
        // Semantic source → "default" section
        Assert.IsTrue(result.Outcome.Diagnostics.ContainsKey("section.default.allocated"),
            "diagnostics 应记录 default section 的 allocated tokens。");
        Assert.IsTrue(result.Outcome.Diagnostics.ContainsKey("section.default.rollover"));
        Assert.IsTrue(result.Outcome.Diagnostics.ContainsKey("section.default.borrowed"));
    }

    // =======================================================================
    // 8. 参数校验
    // =======================================================================

    [TestMethod]
    public void AllocateWithDiversity_NullCandidates_ThrowsArgumentNullException()
    {
        var allocator = MakeAllocator();
        var context = MakeContext(tokenBudget: 1000);
        var options = new DiversityOptions();

        Assert.ThrowsException<ArgumentNullException>(() =>
            allocator.AllocateWithDiversity(null!, context, options));
    }

    [TestMethod]
    public void AllocateWithDiversity_NullContext_ThrowsArgumentNullException()
    {
        var allocator = MakeAllocator();
        var candidates = new[] { R28BTestHelpers.MakeEnvelope("c-1", ContextCandidateSource.Semantic, 0.5, 100) };
        var options = new DiversityOptions();

        Assert.ThrowsException<ArgumentNullException>(() =>
            allocator.AllocateWithDiversity(candidates, null!, options));
    }

    [TestMethod]
    public void AllocateWithDiversity_NullOptions_ThrowsArgumentNullException()
    {
        var allocator = MakeAllocator();
        var context = MakeContext(tokenBudget: 1000);
        var candidates = new[] { R28BTestHelpers.MakeEnvelope("c-1", ContextCandidateSource.Semantic, 0.5, 100) };

        Assert.ThrowsException<ArgumentNullException>(() =>
            allocator.AllocateWithDiversity(candidates, context, null!));
    }

    [TestMethod]
    public void Constructor_NullBaseAllocator_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            new DefaultAllocatorV2_1(null!));
    }

    // =======================================================================
    // 9. DI 注册验证
    // =======================================================================

    [TestMethod]
    public void DI_RegisterDefaultAllocatorV2_1_ResolvesAsIAllocatorV2_1()
    {
        // 验证 DI 注册：IAllocatorV2_1 可从 ServiceCollection 解析
        var services = new ServiceCollection();
        services.AddSingleton<DefaultGlobalAllocator>();
        services.AddSingleton<IGlobalAllocator>(sp => sp.GetRequiredService<DefaultGlobalAllocator>());
        services.TryAddSingletonV2_1Allocator();

        var sp = services.BuildServiceProvider();
        var v2_1 = sp.GetService<IAllocatorV2_1>();
        Assert.IsNotNull(v2_1, "IAllocatorV2_1 应可从 DI 解析。");
        Assert.IsInstanceOfType<DefaultAllocatorV2_1>(v2_1);
    }

    [TestMethod]
    public void DI_DefaultAllocatorV2_1_SameInstanceForBothRegistrations()
    {
        // 验证 DefaultAllocatorV2_1 和 IAllocatorV2_1 解析到同一实例（Singleton）
        var services = new ServiceCollection();
        services.AddSingleton<DefaultGlobalAllocator>();
        services.AddSingleton<IGlobalAllocator>(sp => sp.GetRequiredService<DefaultGlobalAllocator>());
        services.TryAddSingletonV2_1Allocator();

        var sp = services.BuildServiceProvider();
        var concrete = sp.GetRequiredService<DefaultAllocatorV2_1>();
        var interface_ = sp.GetRequiredService<IAllocatorV2_1>();

        Assert.AreSame(concrete, interface_, "Singleton 注册应保证同一实例。");
    }
}

/// <summary>
/// 测试专用 DI 扩展：注册 DefaultAllocatorV2_1 为 IAllocatorV2_1 Singleton。
/// 与 CoreExtensions 中的注册逻辑一致，但独立于 Service 项目（测试不依赖 Service 项目）。
/// </summary>
internal static class AllocatorV2_1TestExtensions
{
    /// <summary>注册 DefaultAllocatorV2_1 + IAllocatorV2_1（Singleton，可选注入）。</summary>
    public static IServiceCollection TryAddSingletonV2_1Allocator(this IServiceCollection services)
    {
        services.TryAddSingleton<DefaultAllocatorV2_1>(sp => new DefaultAllocatorV2_1(
            sp.GetRequiredService<IGlobalAllocator>()));
        services.TryAddSingleton<IAllocatorV2_1>(sp => sp.GetRequiredService<DefaultAllocatorV2_1>());
        return services;
    }
}
