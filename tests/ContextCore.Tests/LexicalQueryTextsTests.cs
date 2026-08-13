using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.ModelExecution;
using ContextCore.Core.Services.Policy;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

[TestClass]
[TestCategory("Retrieval")]
public sealed class LexicalQueryTextsTests
{
    [TestMethod]
    public async Task QueryTexts_ScoresAgainstEachQuery_TitleMatchUsesObservationQuery()
    {
        var store = new InMemoryContextStore();
        await store.SaveAsync(new ContextItem
        {
            Id = "note-compass",
            WorkspaceId = "ws",
            CollectionId = "col",
            Type = "note",
            Title = "AmberCompass-17",
            Content = "observation target"
        });

        var provider = new LexicalCandidateProvider(store);
        var merged = await provider.ExecuteAsync(MakeContext(
            queryText: "summarize project notes AmberCompass-17",
            queryTexts: null,
            includeContent: false));
        var split = await provider.ExecuteAsync(MakeContext(
            queryText: "summarize project notes AmberCompass-17",
            queryTexts: new[] { "summarize project notes", "AmberCompass-17" },
            includeContent: false));

        var mergedHit = merged.Envelopes.Single(item => item.CanonicalKey.EntityId == "note-compass");
        var splitHit = split.Envelopes.Single(item => item.CanonicalKey.EntityId == "note-compass");
        Assert.IsTrue(
            splitHit.Utility.DeterministicScore > mergedHit.Utility.DeterministicScore,
            "观察问句单独检索时，标题整句才能命中加分；拼成一句则标题对不上。");
    }

    [TestMethod]
    public async Task HttpRetrieval_SplitQueryTexts_ShortTitleNoteSelected()
    {
        var store = new InMemoryContextStore();
        await store.SaveAsync(new ContextItem
        {
            Id = "note-compass",
            WorkspaceId = "ws",
            CollectionId = "col",
            Type = "note",
            Title = "AmberCompass-17",
            Content = "observation target"
        });

        var realV2 = BuildRuntime(new ICandidateProvider[]
        {
            new LexicalCandidateProvider(store, new DefaultContextTokenizerResolver())
        });
        var shadowRuntime = new ShadowDecisionRuntime(realV2, new DecisionExperimentPlane());
        var runtime = new AuthoritativeRetrievalRuntime(
            new HybridContextRetriever(store), realV2, shadowRuntime,
            new RetrievalResultProjector(), new CutoverController(cutoverPercentage: 100));

        // 对照：只填 QueryText（旧行为）仍能命中，分条不破坏单条回退。
        var single = await runtime.RetrieveAsync(new ContextRetrievalRequest
        {
            OperationId = "op-http-single",
            WorkspaceId = "ws",
            CollectionId = "col",
            QueryText = "summarize project notes AmberCompass-17",
            TopK = 10,
            TokenBudget = 4096
        }, CancellationToken.None);
        Assert.IsTrue(single.SelectedItems.Any(i => i.CandidateId == "Lexical:note-compass"),
            "只设 QueryText 时短标题笔记仍应命中（回退单条）。");

        // 分条 QueryTexts：观察实体问句单独检索，短标题笔记进 selected。
        // QueryText 只放自然语言任务（不含实体词）——若 QueryTexts 未接线，
        // 回退单条 QueryText 必然落空，能区分接线是否生效。
        var split = await runtime.RetrieveAsync(new ContextRetrievalRequest
        {
            OperationId = "op-http-split",
            WorkspaceId = "ws",
            CollectionId = "col",
            QueryText = "summarize project notes",
            QueryTexts = new[] { "summarize project notes", "AmberCompass-17" },
            TopK = 10,
            TokenBudget = 4096
        }, CancellationToken.None);
        Assert.IsTrue(split.SelectedItems.Any(i => i.CandidateId == "Lexical:note-compass"),
            "分条 QueryTexts 时短标题笔记应进 selected。");
    }

    [TestMethod]
    public async Task HttpPackage_SplitQueryTexts_ShortTitleNoteIncluded()
    {
        var store = new InMemoryContextStore();
        await store.SaveAsync(new ContextItem
        {
            Id = "note-compass",
            WorkspaceId = "ws",
            CollectionId = "col",
            Type = "note",
            Title = "AmberCompass-17",
            Content = "observation target"
        });

        var realV2 = BuildRuntime(new ICandidateProvider[]
        {
            new LexicalCandidateProvider(store, new DefaultContextTokenizerResolver())
        });
        var shadowRuntime = new ShadowDecisionRuntime(realV2, new DecisionExperimentPlane());
        var runtime = new AuthoritativePackageRuntime(
            new BasicContextPackageBuilder(store), realV2, shadowRuntime,
            new PackageResultProjector(), new CutoverController(cutoverPercentage: 100));

        var result = await runtime.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = "ws",
            CollectionId = "col",
            QueryText = "summarize project notes",
            QueryTexts = new[] { "summarize project notes", "AmberCompass-17" },
            TokenBudget = 4096
        }, CancellationToken.None);

        Assert.IsTrue(result.SelectedItems.Any(i => i.ItemId == "Lexical:note-compass"),
            "打包路径分条 QueryTexts 时短标题笔记应进 selected。");
    }

    private static DefaultContextDecisionRuntime BuildRuntime(IReadOnlyList<ICandidateProvider> providers)
    {
        var engine = new DefaultContextDecisionEngine(
            policyRegistry: null,
            safetyGate: new DefaultSafetyGate(),
            lifecycleGate: new DefaultLifecycleGate(),
            utilityScorer: new DefaultUtilityScorer(new DefaultFeatureSchemaValidator()),
            globalAllocator: new DefaultGlobalAllocator());

        return new DefaultContextDecisionRuntime(
            engine: engine,
            policyProvider: new DefaultResolvedPolicyProvider(),
            router: new DefaultRouter(new DefaultExpertCatalog()),
            expertCatalog: new DefaultExpertCatalog(),
            candidateProviders: providers,
            canonicalMerger: new DefaultCanonicalCandidateMerger(),
            earlyAdmissionGate: new DefaultEarlyAdmissionGate(),
            featurePipeline: new DefaultFeaturePipeline(),
            safetyGate: new DefaultSafetyGate(),
            lifecycleGate: new DefaultLifecycleGate(),
            utilityScorer: new DefaultUtilityScorer(new DefaultFeatureSchemaValidator()));
    }

    private static CandidateProviderContext MakeContext(
        string queryText,
        IReadOnlyList<string>? queryTexts,
        bool includeContent)
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
                RequestId = "req-query-texts",
                Scope = new ContextDecisionScope("ws", "col"),
                Purpose = ContextDecisionPurpose.AgentContext,
                QueryText = queryText,
                TokenBudget = 4096,
                TopK = 10,
                RetrievalInput = new RetrievalInput
                {
                    IncludeContent = includeContent,
                    QueryTexts = queryTexts ?? Array.Empty<string>()
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
}
