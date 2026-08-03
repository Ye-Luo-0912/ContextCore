using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Graph;
using ContextCore.Storage.InMemory;

namespace ContextCore.Tests;

// ===========================================================================
// R30E Retrieval Decision Truth —— 决策产出摘要重算 / Tokenizer 画像 / Selected 关系水合
//
// 验收：
//   - DecisionOutcomeRecomputer 是纯函数：摘要（计数 / token / sections）由最终
//     Selected/Dropped 分区派生，任何调用方传入同一分区得到同一摘要；
//   - TokenizerProfile 解析器按内容脚本分类（CJK 主导 → cjk-v1，复用 unicode-cjk-v1 判定）；
//   - Selected 关系水合优先探测 IRelationHydrationStore 批量路径，未实现时回退逐条查询，
//     统计恒等（RequestedCount = HydratedCount + MissingCount）。
// ===========================================================================

[TestClass]
[TestCategory("R30")]
[TestCategory("WP-E")]
public sealed class R30E_RetrievalDecisionTruthTests
{
    // ===========================================================================
    // DecisionOutcomeRecomputer —— Outcome 重算（结果真相）
    // ===========================================================================

    [TestMethod]
    public void Recompute_CountsAndTokensDerivedFromFinalPartitions()
    {
        var selected = new[]
        {
            Envelope("s1", ContextCandidateSource.Mandatory, 10),
            Envelope("s2", ContextCandidateSource.Graph, 20)
        };
        var dropped = new[]
        {
            Envelope("d1", ContextCandidateSource.Lexical, 5),
            Envelope("d2", ContextCandidateSource.Lexical, 5),
            Envelope("d3", ContextCandidateSource.Lexical, 5)
        };

        var outcome = DecisionOutcomeRecomputer.Recompute(
            selected, dropped, tokenBudget: 100, safetyGateBlockedCount: 1, budgetExceededCount: 2);

        Assert.AreEqual(2, outcome.SelectedCount);
        Assert.AreEqual(3, outcome.DroppedCount);
        Assert.AreEqual(30, outcome.EffectiveTokens);
        Assert.AreEqual(100, outcome.TokenBudget);
        Assert.AreEqual(1, outcome.SafetyGateBlockedCount);
        Assert.AreEqual(2, outcome.BudgetExceededCount);
        Assert.AreEqual(0, outcome.Diagnostics.Count);
    }

    [TestMethod]
    public void Recompute_ExactTokenOverrideWins()
    {
        var selected = new[] { Envelope("s1", ContextCandidateSource.Mandatory, 10) };
        var outcome = DecisionOutcomeRecomputer.Recompute(
            selected, Array.Empty<ContextCandidateEnvelope>(), 100, 0, 0, exactEffectiveTokens: 99);
        Assert.AreEqual(99, outcome.EffectiveTokens);
    }

    [TestMethod]
    public void Recompute_SectionsDerivedFromSelectedSources()
    {
        var selected = new[]
        {
            Envelope("s1", ContextCandidateSource.Mandatory, 10),
            Envelope("s2", ContextCandidateSource.Graph, 20),
            Envelope("s3", ContextCandidateSource.WorkingMemory, 30)
        };
        var outcome = DecisionOutcomeRecomputer.Recompute(
            selected, Array.Empty<ContextCandidateEnvelope>(), 100, 0, 0);
        CollectionAssert.AreEqual(new[] { "mandatory", "memory", "relations" }, outcome.Sections.ToArray());
    }

    [TestMethod]
    public void Recompute_SectionsOverride_WhenProvided()
    {
        var selected = new[] { Envelope("s1", ContextCandidateSource.Mandatory, 10) };
        var outcome = DecisionOutcomeRecomputer.Recompute(
            selected, Array.Empty<ContextCandidateEnvelope>(), 100, 0, 0,
            sectionsOverride: new[] { "custom" });
        CollectionAssert.AreEqual(new[] { "custom" }, outcome.Sections.ToArray());
    }

    [TestMethod]
    public void Recompute_EmptyPartitions_ReturnsZeroCounts()
    {
        var outcome = DecisionOutcomeRecomputer.Recompute(
            Array.Empty<ContextCandidateEnvelope>(), Array.Empty<ContextCandidateEnvelope>(), 100, 0, 0);
        Assert.AreEqual(0, outcome.SelectedCount);
        Assert.AreEqual(0, outcome.DroppedCount);
        Assert.AreEqual(0, outcome.EffectiveTokens);
        Assert.AreEqual(0, outcome.Sections.Count);
    }

    [TestMethod]
    public void GetEffectiveTokens_FallsBackToCoarseEstimateWhenTokenCostNull()
    {
#pragma warning disable CS0618 // 显式构造 [Obsolete] 回退字段以验证 fallback 行为
        var envelope = new ContextCandidateEnvelope
        {
            CandidateId = "e1",
            CanonicalKey = Key("e1"),
            Source = ContextCandidateSource.Lexical,
            EstimatedTokens = 42
        };
#pragma warning restore CS0618
        Assert.AreEqual(42, DecisionOutcomeRecomputer.GetEffectiveTokens(envelope));
    }

    [TestMethod]
    public void ResolveSection_MapsSourcesToSections()
    {
        Assert.AreEqual("mandatory", DecisionOutcomeRecomputer.ResolveSection(Envelope("a", ContextCandidateSource.Constraint, 1)));
        Assert.AreEqual("memory", DecisionOutcomeRecomputer.ResolveSection(Envelope("b", ContextCandidateSource.StableMemory, 1)));
        Assert.AreEqual("relations", DecisionOutcomeRecomputer.ResolveSection(Envelope("c", ContextCandidateSource.Graph, 1)));
        Assert.AreEqual("global", DecisionOutcomeRecomputer.ResolveSection(Envelope("d", ContextCandidateSource.GlobalContext, 1)));
        Assert.AreEqual("related", DecisionOutcomeRecomputer.ResolveSection(Envelope("e", ContextCandidateSource.RelatedContext, 1)));
        Assert.AreEqual("default", DecisionOutcomeRecomputer.ResolveSection(Envelope("f", ContextCandidateSource.Lexical, 1)));
    }

    // ===========================================================================
    // TokenizerProfile —— CJK 画像解析（复用 unicode-cjk-v1 脚本判定）
    // ===========================================================================

    [TestMethod]
    public void ProfileResolver_UnknownOrEmpty_ReturnsDefaultLatin()
    {
        var resolver = new DefaultTokenizerProfileResolver();
        Assert.AreEqual(DefaultTokenizerProfileResolver.LatinProfileId, resolver.Resolve(null).ProfileId);
        Assert.AreEqual(DefaultTokenizerProfileResolver.LatinProfileId, resolver.Resolve(string.Empty).ProfileId);
        Assert.AreEqual(DefaultTokenizerProfileResolver.LatinProfileId, resolver.Resolve("no-such-profile").ProfileId);
    }

    [TestMethod]
    public void ProfileResolver_GetAll_ContainsCjkAndLatin()
    {
        var resolver = new DefaultTokenizerProfileResolver();
        var ids = resolver.GetAll().Select(p => p.ProfileId).ToArray();
        CollectionAssert.AreEquivalent(
            new[] { DefaultTokenizerProfileResolver.CjkProfileId, DefaultTokenizerProfileResolver.LatinProfileId }, ids);

        var cjk = resolver.Resolve(DefaultTokenizerProfileResolver.CjkProfileId);
        Assert.AreEqual("unicode-cjk-v1", cjk.TokenizerName);
        Assert.AreEqual("cjk", cjk.LanguageCategory);
    }

    [TestMethod]
    public void ProfileResolver_ResolveForContent_CjkDominant_ReturnsCjk()
    {
        var resolver = new DefaultTokenizerProfileResolver();
        var profile = resolver.ResolveForContent("你好世界，这是一段中文测试内容。你好世界。");
        Assert.AreEqual(DefaultTokenizerProfileResolver.CjkProfileId, profile.ProfileId);
    }

    [TestMethod]
    public void ProfileResolver_ResolveForContent_LatinDominant_ReturnsLatin()
    {
        var resolver = new DefaultTokenizerProfileResolver();
        var profile = resolver.ResolveForContent("The quick brown fox jumps over the lazy dog. Context is king.");
        Assert.AreEqual(DefaultTokenizerProfileResolver.LatinProfileId, profile.ProfileId);
    }

    [TestMethod]
    public void ProfileResolver_ResolveForContent_Empty_ReturnsLatin()
    {
        var resolver = new DefaultTokenizerProfileResolver();
        Assert.AreEqual(DefaultTokenizerProfileResolver.LatinProfileId, resolver.ResolveForContent(null).ProfileId);
        Assert.AreEqual(DefaultTokenizerProfileResolver.LatinProfileId, resolver.ResolveForContent("  ").ProfileId);
    }

    [TestMethod]
    public void CjkScriptClassifier_Ratio_IsConsistent()
    {
        Assert.AreEqual(1.0, CjkScriptClassifier.ComputeCjkRatio("你好世界"), 0.001);
        Assert.AreEqual(0.0, CjkScriptClassifier.ComputeCjkRatio("hello world"), 0.001);
        var mixed = CjkScriptClassifier.ComputeCjkRatio("你好 hello 世界");
        Assert.IsTrue(mixed > 0 && mixed < 1.0);
        Assert.IsTrue(CjkScriptClassifier.IsCjkDominant("你好世界，中文内容", threshold: 0.2));
        Assert.IsFalse(CjkScriptClassifier.IsCjkDominant("just english text here", threshold: 0.2));
    }

    // ===========================================================================
    // Selected 关系水合 —— probe 批量 / 回退逐条
    // ===========================================================================

    [TestMethod]
    public async Task Hydrate_ProbePath_UsesBatchHydrationStore()
    {
        var store = new InMemoryRelationStore();
        await store.BatchUpsertAsync([
            Relation("r1", "a", "b"),
            Relation("r2", "b", "c")
        ]);
        var service = new DefaultSelectedRelationHydrationService(store);

        var response = await service.HydrateAsync(new RelationHydrationRequest
        {
            WorkspaceId = "ws-1",
            CollectionId = "col-1",
            RelationIds = ["r1", "r2", "missing-1"]
        });

        Assert.AreEqual("relation-hydration-store", response.Source);
        Assert.AreEqual(3, response.RequestedCount);
        Assert.AreEqual(2, response.HydratedCount);
        Assert.AreEqual(1, response.MissingCount);
        CollectionAssert.AreEquivalent(new[] { "missing-1" }, response.MissingIds.ToArray());
        Assert.AreEqual(2, response.Relations.Count);
        Assert.AreEqual("r1", response.Relations[0].RelationId);
        Assert.AreEqual("references", response.Relations[0].RelationType);
    }

    [TestMethod]
    public async Task Hydrate_FallbackPath_UsesPerIdGet()
    {
        var store = new FallbackOnlyRelationStore();
        store.Relations["r1"] = Relation("r1", "a", "b");
        var service = new DefaultSelectedRelationHydrationService(store);

        var response = await service.HydrateAsync(new RelationHydrationRequest
        {
            WorkspaceId = "ws-1",
            CollectionId = "col-1",
            RelationIds = ["r1", "r2"]
        });

        Assert.AreEqual("relation-store-fallback", response.Source);
        Assert.AreEqual(2, store.GetAsyncCalls);
        Assert.AreEqual(1, response.HydratedCount);
        Assert.AreEqual(1, response.MissingCount);
        CollectionAssert.AreEquivalent(new[] { "r2" }, response.MissingIds.ToArray());
        Assert.AreEqual("r1", response.Relations[0].RelationId);
    }

    [TestMethod]
    public async Task Hydrate_DeduplicatesIds_PreservesRequestOrder()
    {
        var store = new InMemoryRelationStore();
        await store.BatchUpsertAsync([Relation("r1", "a", "b")]);
        var service = new DefaultSelectedRelationHydrationService(store);

        var response = await service.HydrateAsync(new RelationHydrationRequest
        {
            WorkspaceId = "ws-1",
            CollectionId = "col-1",
            RelationIds = ["r1", "r1", "r1"]
        });

        Assert.AreEqual(1, response.RequestedCount);
        Assert.AreEqual(1, response.HydratedCount);
        Assert.AreEqual(0, response.MissingCount);
    }

    [TestMethod]
    public async Task Hydrate_WhitespaceIds_CountedAsMissing()
    {
        var store = new InMemoryRelationStore();
        var service = new DefaultSelectedRelationHydrationService(store);

        var response = await service.HydrateAsync(new RelationHydrationRequest
        {
            WorkspaceId = "ws-1",
            CollectionId = "col-1",
            RelationIds = ["  "]
        });

        Assert.AreEqual(0, response.HydratedCount);
        Assert.AreEqual(1, response.MissingCount);
        CollectionAssert.AreEquivalent(new[] { "  " }, response.MissingIds.ToArray());
    }

    [TestMethod]
    public async Task Hydrate_InvalidInput_Throws()
    {
        var store = new InMemoryRelationStore();
        var service = new DefaultSelectedRelationHydrationService(store);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.HydrateAsync(new RelationHydrationRequest
        {
            WorkspaceId = string.Empty,
            CollectionId = "col-1",
            RelationIds = ["r1"]
        }));

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.HydrateAsync(new RelationHydrationRequest
        {
            WorkspaceId = "ws-1",
            CollectionId = "col-1",
            RelationIds = []
        }));
    }

    // ===========================================================================
    // Helpers
    // ===========================================================================

    private static ContextCandidateEnvelope Envelope(string id, ContextCandidateSource source, int tokens) => new()
    {
        CandidateId = id,
        CanonicalKey = Key(id),
        Source = source,
        TokenCost = new CandidateTokenCost
        {
            ContentTokens = tokens,
            TokenizerId = "unicode-cjk-v1",
            IsEstimated = false
        }
    };

    private static CanonicalCandidateKey Key(string entityId)
        => CanonicalCandidateKey.Create("test-ws", "test-col", "test-entity", entityId, "v1");

    private static ContextRelation Relation(string id, string sourceId, string targetId) => new()
    {
        Id = id,
        WorkspaceId = "ws-1",
        CollectionId = "col-1",
        SourceId = sourceId,
        TargetId = targetId,
        RelationType = "references",
        Weight = 1.0,
        Confidence = 0.9
    };
}

/// <summary>
/// 仅实现 IRelationStore 的 fake（无 IRelationHydrationStore 能力），
/// 用于验证水合服务的回退路径（逐条 GetAsync）。
/// </summary>
internal sealed class FallbackOnlyRelationStore : IRelationStore
{
    public Dictionary<string, ContextRelation> Relations { get; } = new(StringComparer.Ordinal);

    public int GetAsyncCalls { get; private set; }

    public Task<ContextRelation?> GetAsync(
        string workspaceId,
        string collectionId,
        string relationId,
        CancellationToken cancellationToken = default)
    {
        GetAsyncCalls++;
        return Task.FromResult(Relations.TryGetValue(relationId, out var relation) ? relation : null);
    }

    public Task SaveAsync(ContextRelation relation, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<ContextRelation>> QueryAsync(
        ContextRelationQuery query,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<bool> DeleteAsync(
        string workspaceId,
        string collectionId,
        string relationId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task BatchUpsertAsync(
        IEnumerable<ContextRelation> relations,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<ContextRelation>> QueryNeighborsAsync(
        RelationNeighborQuery query,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<RelationNeighborBatchResult>> QueryNeighborsBatchAsync(
        RelationNeighborBatchQuery query,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
