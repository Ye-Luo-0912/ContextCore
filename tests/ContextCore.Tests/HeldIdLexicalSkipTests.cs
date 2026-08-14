using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Policy;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

// 已持有的种子 ID 不占 Lexical TopK：忘掉的条目才有名额进候选池。
// 种子本身仍保留在运行时合并里，去留由分配器决定；这里只验证词法召回跳过已持有。

[TestClass]
[TestCategory("Retrieval")]
public sealed class HeldIdLexicalSkipTests
{
    [TestMethod]
    public async Task Lexical_SkipsHeldSeedIds_StillFindsOthers()
    {
        var store = new InMemoryContextStore();
        await store.SaveAsync(new ContextItem
        {
            Id = "keep-1",
            WorkspaceId = "ws",
            CollectionId = "col",
            Type = "note",
            Title = "KeepOne resident note",
            Content = "resident body"
        });
        await store.SaveAsync(new ContextItem
        {
            Id = "amber-1",
            WorkspaceId = "ws",
            CollectionId = "col",
            Type = "note",
            Title = "AmberCompass-17",
            Content = "amber target"
        });

        var keepKey = CanonicalCandidateKey.Create("ws", "col", "note", "keep-1", "v1");
        var provider = new LexicalCandidateProvider(store);
        var result = await provider.ExecuteAsync(MakeContext(
            queryTexts: new[] { "AmberCompass-17 resident" },
            seed: new CandidateWorkingSet
            {
                Envelopes = new[] { MakeEnvelope("keep-1", keepKey) },
                Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
                {
                    [keepKey] = new CandidateMaterial { Key = keepKey, Content = "resident body", NativeKind = "note" }
                }
            }));

        // 问句能命中种子之外的条目；已持有的 keep-1 不再占词法名额。
        CollectionAssert.Contains(result.Envelopes.Select(item => item.CanonicalKey.EntityId).ToList(), "amber-1",
            "问句应命中种子之外的条目。");
        Assert.IsFalse(result.Envelopes.Any(item => item.CanonicalKey.EntityId == "keep-1"),
            "种子里的已持有 ID 不应再占 Lexical TopK。");
    }

    [TestMethod]
    public async Task Lexical_NoSeed_KeepsOldBehavior()
    {
        var store = new InMemoryContextStore();
        await store.SaveAsync(new ContextItem
        {
            Id = "keep-1",
            WorkspaceId = "ws",
            CollectionId = "col",
            Type = "note",
            Title = "KeepOne resident note",
            Content = "resident body"
        });

        var provider = new LexicalCandidateProvider(store);
        var result = await provider.ExecuteAsync(MakeContext(
            queryTexts: new[] { "resident" },
            seed: null));

        CollectionAssert.Contains(result.Envelopes.Select(item => item.CanonicalKey.EntityId).ToList(), "keep-1",
            "无种子时不排除任何条目（旧行为不变）。");
    }

    private static CandidateProviderContext MakeContext(
        IReadOnlyList<string> queryTexts,
        CandidateWorkingSet? seed)
    {
        var bundle = DefaultPolicyBundleFactory.Create();
        var snapshot = new EffectivePolicySnapshot
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
            ResolutionScope = new ContextDecisionScope("ws", "col")
        };

        return new CandidateProviderContext(
            Request: new ContextDecisionRuntimeRequest
            {
                RequestId = "req-held-skip",
                Scope = new ContextDecisionScope("ws", "col"),
                Purpose = ContextDecisionPurpose.AgentContext,
                QueryText = string.Join(" ", queryTexts),
                TokenBudget = 4096,
                TopK = 10,
                SeedWorkingSet = seed,
                RetrievalInput = new RetrievalInput
                {
                    IncludeContent = false,
                    QueryTexts = queryTexts
                }
            },
            Policy: snapshot,
            Routing: new ExpertRoutingDecision
            {
                Expert = RetrievalExpert.Lexical,
                Enabled = true,
                TopK = 10,
                TokenBudget = 4096,
                Weight = 1.0,
                ReasonCode = "test"
            },
            AdaptationContext: new CandidateAdaptationContext
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                ObservedAt = DateTimeOffset.UtcNow
            });
    }

    private static ContextCandidateEnvelope MakeEnvelope(string id, CanonicalCandidateKey key)
        => new()
        {
            CandidateId = id,
            Source = ContextCandidateSource.Lexical,
            CanonicalKey = key,
            Utility = new CandidateUtilityScore { DeterministicScore = 0.8, FinalScore = 0.8 }
        };
}
