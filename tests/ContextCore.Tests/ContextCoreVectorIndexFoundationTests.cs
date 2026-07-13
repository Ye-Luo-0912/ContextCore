using System.Net;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Screens;
using ContextCore.ControlRoom.Services;
using ContextCore.Evaluation.Runners;
using ContextCore.Evaluation.Models;
using ContextCore.Core.Services;
using ContextCore.Embedding;
using ContextCore.Embedding.Providers;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;
using ContextCore.Evaluation.Vector;

namespace ContextCore.Tests;

/// <summary>覆盖 Vector Index Foundation V1 的纯基础设施行为，不接正式 retrieval/package。</summary>
[TestClass]
[TestCategory("Vector")]
public sealed class ContextCoreVectorIndexFoundationTests
{
    [TestMethod]
    public async Task InMemoryVectorIndexStore_ShouldUpsertGetAndDelete()
    {
        var store = new InMemoryVectorIndexStore();
        var entry = CreateEntry("entry-1", "item-1", [1.0f, 0.0f]);

        await store.UpsertAsync(entry);
        var found = await store.GetByItemIdAsync("workspace-test", "collection-test", "item-1");
        Assert.AreEqual(1, found.Count);
        Assert.AreEqual("entry-1", found[0].EntryId);

        await store.DeleteAsync("workspace-test", "collection-test", "entry-1");
        found = await store.GetByItemIdAsync("workspace-test", "collection-test", "item-1");
        Assert.AreEqual(0, found.Count);
    }

    [TestMethod]
    public async Task FileSystemVectorIndexStore_ShouldUpsertGetAndDelete()
    {
        var root = CreateTempRoot();
        try
        {
            var store = new FileVectorIndexStore(new FileStorageOptions { RootPath = root });
            var entry = CreateEntry("entry-1", "item-1", [1.0f, 0.0f]);

            await store.UpsertAsync(entry);
            var found = await store.GetByItemIdAsync("workspace-test", "collection-test", "item-1");
            Assert.AreEqual(1, found.Count);
            Assert.AreEqual("entry-1", found[0].EntryId);

            await store.DeleteAsync("workspace-test", "collection-test", "entry-1");
            found = await store.GetByItemIdAsync("workspace-test", "collection-test", "item-1");
            Assert.AreEqual(0, found.Count);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [TestMethod]
    public async Task DeterministicEmbeddingGenerator_ShouldReturnStableVector()
    {
        var generator = new DeterministicHashEmbeddingGenerator();
        var request = CreateEmbeddingRequest("item-1", "同一段内容");

        var first = await generator.GenerateAsync(request);
        var second = await generator.GenerateAsync(request);

        CollectionAssert.AreEqual(
            first.Entries[0].Vector.ToArray(),
            second.Entries[0].Vector.ToArray());
        Assert.AreEqual(first.Entries[0].ContentHash, second.Entries[0].ContentHash);
        Assert.AreEqual(generator.Dimension, first.Entries[0].Dimension);
    }

    [TestMethod]
    public async Task CosineQuery_ShouldReturnExpectedNearestItem()
    {
        var store = new InMemoryVectorIndexStore();
        await store.UpsertAsync(CreateEntry("entry-near", "item-near", [1.0f, 0.0f]));
        await store.UpsertAsync(CreateEntry("entry-far", "item-far", [0.0f, 1.0f]));

        var results = await store.SearchAsync(new VectorIndexSearchQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Vector = [0.95f, 0.05f],
            TopK = 2
        });

        Assert.AreEqual(2, results.Count);
        Assert.AreEqual("item-near", results[0].Entry.ItemId);
        Assert.IsTrue(results[0].Score > results[1].Score);
    }

    [TestMethod]
    public async Task CosineQuery_ShouldFilterByProviderModelAndDimension()
    {
        var store = new InMemoryVectorIndexStore();
        await store.UpsertAsync(CreateEntry("entry-fixed", "item-fixed", [1.0f, 0.0f]));
        await store.UpsertAsync(CreateEntry(
            "entry-other-provider",
            "item-other-provider",
            [1.0f, 0.0f],
            metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            embeddingProvider: "other-provider",
            embeddingModel: "other-model"));

        var results = await store.SearchAsync(new VectorIndexSearchQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Vector = [1.0f, 0.0f],
            EmbeddingProvider = "fixed-test",
            EmbeddingModel = "fixed-test-v1",
            Dimension = 2,
            TopK = 10
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("item-fixed", results[0].Entry.ItemId);
    }

    [TestMethod]
    public async Task VectorIndexDiagnostics_ShouldDetectStaleEmbedding()
    {
        var contextStore = new InMemoryContextStore();
        var vectorStore = new InMemoryVectorIndexStore();
        var generator = new DeterministicHashEmbeddingGenerator();
        var service = new VectorIndexService(vectorStore, generator, contextStore, null);

        await contextStore.SaveAsync(new ContextItem
        {
            Id = "item-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "note",
            Content = "新内容",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        var generated = await generator.GenerateAsync(CreateEmbeddingRequest("item-1", "旧内容"));
        await vectorStore.UpsertAsync(generated.Entries[0]);

        var diagnostics = await service.GetDiagnosticsAsync("workspace-test", "collection-test");

        Assert.IsTrue(diagnostics.Diagnostics.Any(item => item.Type == VectorIndexDiagnosticTypes.StaleEmbedding));
        Assert.IsTrue(diagnostics.Diagnostics.Any(item => item.Type == VectorIndexDiagnosticTypes.ContentHashMismatch));
    }

    [TestMethod]
    public async Task VectorIndexDiagnostics_ShouldReportDimensionMismatch()
    {
        var store = new InMemoryVectorIndexStore();
        await store.UpsertAsync(CreateEntry("entry-bad-dimension", "item-1", [1.0f, 0.0f], dimension: 3));

        var diagnostics = await store.GetDiagnosticsAsync(new VectorIndexQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            IncludeVector = true
        });

        Assert.IsTrue(diagnostics.Any(item => item.Type == VectorIndexDiagnosticTypes.DimensionMismatch));
    }

    [TestMethod]
    public async Task VectorIndexDiagnostics_ShouldReportDuplicateEntry()
    {
        var store = new InMemoryVectorIndexStore();
        await store.UpsertAsync(CreateEntry("entry-a", "item-1", [1.0f, 0.0f]));
        await store.UpsertAsync(CreateEntry("entry-b", "item-1", [0.9f, 0.1f]));

        var diagnostics = await store.GetDiagnosticsAsync(new VectorIndexQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            IncludeVector = true
        });

        Assert.AreEqual(2, diagnostics.Count(item => item.Type == VectorIndexDiagnosticTypes.DuplicateVectorEntry));
    }

    [TestMethod]
    public async Task VectorQueryPreview_ShouldReturnNearestVectorCandidate()
    {
        var store = new InMemoryVectorIndexStore();
        await store.UpsertAsync(CreateEntry("entry-alpha", "item-alpha", [1.0f, 0.0f]));
        await store.UpsertAsync(CreateEntry("entry-beta", "item-beta", [0.0f, 1.0f]));
        var service = CreateQueryPreviewService(store, new FixedEmbeddingGenerator(), new InMemoryContextStore());

        var result = await service.PreviewAsync(CreateQueryPreviewRequest("alpha query"));

        Assert.AreEqual(2, result.Candidates.Count);
        Assert.AreEqual("item-alpha", result.Candidates[0].ItemId);
        Assert.IsTrue(result.Candidates[0].Similarity > result.Candidates[1].Similarity);
    }

    [TestMethod]
    public async Task VectorQueryPreview_ShouldApplyLayerFilter()
    {
        var store = new InMemoryVectorIndexStore();
        await store.UpsertAsync(CreateEntry("entry-context", "item-context", [1.0f, 0.0f], layer: "context"));
        await store.UpsertAsync(CreateEntry("entry-stable", "item-stable", [1.0f, 0.0f], layer: "stable_memory"));
        var service = CreateQueryPreviewService(store, new FixedEmbeddingGenerator(), new InMemoryContextStore());

        var result = await service.PreviewAsync(CreateQueryPreviewRequest("alpha query", layer: "stable_memory"));

        Assert.AreEqual(1, result.Candidates.Count);
        Assert.AreEqual("item-stable", result.Candidates[0].ItemId);
        Assert.AreEqual("stable_memory", result.Candidates[0].Layer);
    }

    [TestMethod]
    public async Task VectorQueryPreview_ShouldBlockBelowMinSimilarityByPolicy()
    {
        var contextStore = new InMemoryContextStore();
        await contextStore.SaveAsync(CreateContextItem("item-alpha", "alpha source", "note"));
        await contextStore.SaveAsync(CreateContextItem("item-beta", "beta source", "note"));
        var store = new InMemoryVectorIndexStore();
        await store.UpsertAsync(await CreateSourceBackedEntryAsync(
            "entry-alpha",
            "item-alpha",
            "alpha source",
            [1.0f, 0.0f],
            metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lifecycle"] = "Active",
                ["sourceKind"] = "memory"
            }));
        await store.UpsertAsync(await CreateSourceBackedEntryAsync(
            "entry-beta",
            "item-beta",
            "beta source",
            [0.0f, 1.0f],
            metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lifecycle"] = "Active",
                ["sourceKind"] = "memory"
            }));
        var service = CreateQueryPreviewService(store, new FixedEmbeddingGenerator(), contextStore);

        var result = await service.PreviewAsync(CreateQueryPreviewRequest("alpha query", minSimilarity: 0.8));

        Assert.AreEqual(2, result.Candidates.Count);
        Assert.AreEqual("item-alpha", result.Candidates[0].ItemId);
        Assert.AreEqual(VectorCandidateEligibilityStatuses.Eligible, result.Candidates[0].EligibilityStatus);
        Assert.AreEqual("item-beta", result.Candidates[1].ItemId);
        Assert.IsTrue(result.Candidates[1].BlockedReasons.Contains(VectorCandidateBlockedReason.SimilarityBelowThreshold));
    }

    [TestMethod]
    public async Task VectorQueryPreview_ShouldReturnDiagnosticsForEmptyIndex()
    {
        var service = CreateQueryPreviewService(new InMemoryVectorIndexStore(), new FixedEmbeddingGenerator(), new InMemoryContextStore());

        var result = await service.PreviewAsync(CreateQueryPreviewRequest("alpha query"));

        Assert.AreEqual(0, result.Candidates.Count);
        Assert.IsTrue(result.Diagnostics.IndexEmpty);
        Assert.IsTrue(result.Warnings.Any(warning => warning.Contains("vector index 当前为空", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task VectorQueryPreview_ShouldSurfaceDuplicateStaleAndOrphanDiagnostics()
    {
        var contextStore = new InMemoryContextStore();
        await contextStore.SaveAsync(CreateContextItem("ctx-duplicate", "当前内容会让旧 embedding 过期。", "vector-diagnostic"));
        await contextStore.SaveAsync(CreateContextItem("ctx-stale", "新的稳定事实需要重新生成 embedding。", "vector-diagnostic"));
        var store = new InMemoryVectorIndexStore();
        await store.UpsertAsync(CreateEntry("entry-duplicate-a", "ctx-duplicate", [1.0f, 0.0f]));
        await store.UpsertAsync(CreateEntry("entry-duplicate-b", "ctx-duplicate", [0.99f, 0.01f]));
        await store.UpsertAsync(CreateEntry("entry-stale", "ctx-stale", [1.0f, 0.0f]));
        await store.UpsertAsync(CreateEntry("entry-orphan", "ctx-orphan", [1.0f, 0.0f]));
        var service = CreateQueryPreviewService(store, new FixedEmbeddingGenerator(), contextStore);

        var result = await service.PreviewAsync(CreateQueryPreviewRequest("alpha query", topK: 10));

        Assert.IsTrue(result.Candidates.Any(candidate => candidate.IsDuplicate));
        Assert.IsTrue(result.Candidates.Any(candidate => candidate.IsStale));
        Assert.IsTrue(result.Candidates.Any(candidate => candidate.IsOrphan));
        Assert.IsTrue(result.Diagnostics.DuplicateCount > 0);
        Assert.IsTrue(result.Diagnostics.OrphanCount > 0);
    }

    [TestMethod]
    public async Task VectorCandidateEligibility_NormalProfileShouldBlockDeprecatedCandidate()
    {
        var store = new InMemoryVectorIndexStore();
        await store.UpsertAsync(CreateEntry(
            "entry-deprecated",
            "item-deprecated",
            [1.0f, 0.0f],
            metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lifecycle"] = "Deprecated",
                ["sourceKind"] = "memory"
            }));
        var service = CreateQueryPreviewService(store, new FixedEmbeddingGenerator(), new InMemoryContextStore());

        var result = await service.PreviewAsync(CreateQueryPreviewRequest("alpha query", profileId: VectorQueryProfileIds.NormalV1));

        var candidate = result.Candidates.Single();
        Assert.AreEqual(VectorCandidateEligibilityStatuses.Blocked, candidate.EligibilityStatus);
        Assert.IsTrue(candidate.BlockedReasons.Contains(VectorCandidateBlockedReason.DeprecatedCandidateBlocked));
        Assert.AreEqual(VectorQueryTargetSections.Excluded, candidate.TargetSection);
    }

    [TestMethod]
    public async Task VectorCandidateEligibility_NormalProfileShouldBlockHistoricalCandidate()
    {
        var store = new InMemoryVectorIndexStore();
        await store.UpsertAsync(CreateEntry(
            "entry-historical",
            "item-historical",
            [1.0f, 0.0f],
            layer: "historical_context",
            metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lifecycle"] = "Historical",
                ["sourceKind"] = "memory"
            }));
        var service = CreateQueryPreviewService(store, new FixedEmbeddingGenerator(), new InMemoryContextStore());

        var result = await service.PreviewAsync(CreateQueryPreviewRequest("alpha query", profileId: VectorQueryProfileIds.NormalV1));

        var candidate = result.Candidates.Single();
        Assert.AreEqual(VectorCandidateEligibilityStatuses.Blocked, candidate.EligibilityStatus);
        Assert.IsTrue(candidate.BlockedReasons.Contains(VectorCandidateBlockedReason.HistoricalCandidateBlocked));
        Assert.IsTrue(candidate.RiskIfNormalSelected);
        Assert.IsFalse(candidate.RiskAfterPolicy);
    }

    [TestMethod]
    public async Task VectorCandidateEligibility_AuditProfileShouldRouteHistoricalCandidateToAuditContext()
    {
        var contextStore = new InMemoryContextStore();
        await contextStore.SaveAsync(CreateContextItem("item-audit-historical", "historical source", "note"));
        var store = new InMemoryVectorIndexStore();
        await store.UpsertAsync(await CreateSourceBackedEntryAsync(
            "entry-audit-historical",
            "item-audit-historical",
            "historical source",
            [1.0f, 0.0f],
            layer: "historical_context",
            metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lifecycle"] = "Historical",
                ["sourceKind"] = "memory"
            }));
        var service = CreateQueryPreviewService(store, new FixedEmbeddingGenerator(), contextStore);

        var result = await service.PreviewAsync(CreateQueryPreviewRequest("alpha query", profileId: VectorQueryProfileIds.AuditV1));

        var candidate = result.Candidates.Single();
        Assert.AreEqual(VectorCandidateEligibilityStatuses.Eligible, candidate.EligibilityStatus);
        Assert.AreEqual(VectorQueryTargetSections.AuditContext, candidate.TargetSection);
        Assert.IsTrue(candidate.RiskIfNormalSelected);
        Assert.IsFalse(candidate.RiskAfterPolicy);
    }

    [TestMethod]
    public async Task VectorCandidateEligibility_NormalProfileShouldBlockUnknownLifecycle()
    {
        var store = new InMemoryVectorIndexStore();
        await store.UpsertAsync(CreateEntry(
            "entry-unknown-lifecycle",
            "item-unknown-lifecycle",
            [1.0f, 0.0f],
            metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sourceKind"] = "context"
            }));
        var service = CreateQueryPreviewService(store, new FixedEmbeddingGenerator(), new InMemoryContextStore());

        var result = await service.PreviewAsync(CreateQueryPreviewRequest("alpha query", profileId: VectorQueryProfileIds.NormalV1));

        var candidate = result.Candidates.Single();
        Assert.AreEqual(VectorCandidateEligibilityStatuses.Blocked, candidate.EligibilityStatus);
        Assert.IsTrue(candidate.BlockedReasons.Contains(VectorCandidateBlockedReason.UnknownLifecycleBlocked));
        Assert.IsTrue(candidate.BlockedReasons.Contains(VectorCandidateBlockedReason.LifecycleMetadataIncompleteBlocked));
        Assert.AreEqual(VectorQueryTargetSections.Excluded, candidate.TargetSection);
    }

    [TestMethod]
    public async Task VectorCandidateEligibility_NormalProfileShouldBlockIncompleteLifecycleMetadata()
    {
        var store = new InMemoryVectorIndexStore();
        await store.UpsertAsync(CreateEntry(
            "entry-incomplete-lifecycle",
            "item-incomplete-lifecycle",
            [1.0f, 0.0f],
            layer: "historical_context",
            metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lifecycle"] = "Historical",
                ["sourceKind"] = "memory"
            }));
        var service = CreateQueryPreviewService(store, new FixedEmbeddingGenerator(), new InMemoryContextStore());

        var result = await service.PreviewAsync(CreateQueryPreviewRequest("alpha query", profileId: VectorQueryProfileIds.NormalV1));

        var candidate = result.Candidates.Single();
        Assert.AreEqual(VectorCandidateEligibilityStatuses.Blocked, candidate.EligibilityStatus);
        Assert.IsTrue(candidate.BlockedReasons.Contains(VectorCandidateBlockedReason.LifecycleMetadataIncompleteBlocked));
        Assert.IsTrue(candidate.BlockedReasons.Contains(VectorCandidateBlockedReason.ReplacementMetadataMissingBlocked));
    }

    [TestMethod]
    public async Task VectorCandidateEligibility_AuditProfileShouldRouteUnknownLifecycleToAuditContext()
    {
        var contextStore = new InMemoryContextStore();
        await contextStore.SaveAsync(CreateContextItem("item-audit-unknown", "unknown lifecycle source", "note"));
        var store = new InMemoryVectorIndexStore();
        await store.UpsertAsync(await CreateSourceBackedEntryAsync(
            "entry-audit-unknown",
            "item-audit-unknown",
            "unknown lifecycle source",
            [1.0f, 0.0f],
            metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sourceKind"] = "context"
            }));
        var service = CreateQueryPreviewService(store, new FixedEmbeddingGenerator(), contextStore);

        var result = await service.PreviewAsync(CreateQueryPreviewRequest("alpha query", profileId: VectorQueryProfileIds.AuditV1));

        var candidate = result.Candidates.Single();
        Assert.AreEqual(VectorCandidateEligibilityStatuses.Eligible, candidate.EligibilityStatus);
        Assert.AreEqual(VectorQueryTargetSections.AuditContext, candidate.TargetSection);
        Assert.IsTrue(candidate.RiskIfNormalSelected);
        Assert.IsFalse(candidate.RiskAfterPolicy);
    }

    [TestMethod]
    public void VectorSourceLifecycleMetadataResolver_ShouldNotReadEvalLabels()
    {
        var resolver = new VectorSourceLifecycleMetadataResolver();
        var entry = CreateEntry(
            "entry-no-label-shortcut",
            "item-no-label-shortcut",
            [1.0f, 0.0f],
            metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sampleId"] = "sample-should-not-matter",
                ["mustHit"] = "true",
                ["mustNotHit"] = "true",
                ["sourceKind"] = "context"
            });

        var metadata = resolver.Resolve(entry);

        Assert.IsFalse(metadata.IsKnownLifecycle);
        Assert.IsFalse(metadata.IsLifecycleMetadataComplete);
    }

    [TestMethod]
    public async Task VectorCandidateEligibility_DuplicateAndOrphanDiagnosticsShouldBlock()
    {
        var store = new InMemoryVectorIndexStore();
        await store.UpsertAsync(CreateEntry("entry-duplicate-a", "item-duplicate", [1.0f, 0.0f]));
        await store.UpsertAsync(CreateEntry("entry-duplicate-b", "item-duplicate", [0.99f, 0.01f]));
        var service = CreateQueryPreviewService(store, new FixedEmbeddingGenerator(), new InMemoryContextStore());

        var result = await service.PreviewAsync(CreateQueryPreviewRequest("alpha query", topK: 2));

        Assert.IsTrue(result.Candidates.All(candidate => candidate.EligibilityStatus == VectorCandidateEligibilityStatuses.Blocked));
        Assert.IsTrue(result.Candidates.Any(candidate => candidate.BlockedReasons.Contains(VectorCandidateBlockedReason.DuplicateVectorEntryBlocked)));
        Assert.IsTrue(result.Candidates.Any(candidate => candidate.BlockedReasons.Contains(VectorCandidateBlockedReason.OrphanVectorEntryBlocked)));
    }

    [TestMethod]
    public void VectorCandidateEligibilityPolicy_ShouldNotReadEvalLabels()
    {
        var policy = new VectorCandidateEligibilityPolicy();
        var profile = new VectorQueryProfileRegistry().Resolve(VectorQueryProfileIds.NormalV1);
        var entry = CreateEntry(
            "entry-label-metadata",
            "item-label-metadata",
            [1.0f, 0.0f],
            metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sampleId"] = "sample-with-labels",
                ["mustHit"] = "false",
                ["mustNotHit"] = "true",
                ["lifecycle"] = "Active",
                ["sourceKind"] = "memory"
            });

        var result = policy.Evaluate(profile, entry, 0.95, Array.Empty<string>());

        Assert.AreEqual(VectorCandidateEligibilityStatuses.Eligible, result.EligibilityStatus);
        Assert.AreEqual(0, result.BlockedReasons.Count);
    }

    [TestMethod]
    public void VectorCandidateEligibilityPolicy_ShouldNotUseItemIdOrSampleIdShortcut()
    {
        var policy = new VectorCandidateEligibilityPolicy();
        var profile = new VectorQueryProfileRegistry().Resolve(VectorQueryProfileIds.NormalV1);
        var normalKindEntry = CreateEntry(
            "entry-budget-like-id",
            "memory:chat-budget-stress",
            [1.0f, 0.0f],
            itemKind: "note",
            metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sampleId"] = "sample-that-looks-risky",
                ["lifecycle"] = "Active",
                ["sourceKind"] = "memory"
            });
        var diagnosticsKindEntry = CreateEntry(
            "entry-diagnostics-kind",
            "item-without-special-id",
            [1.0f, 0.0f],
            itemKind: "stress-test",
            metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lifecycle"] = "Active",
                ["sourceKind"] = "memory"
            });

        var normalResult = policy.Evaluate(profile, normalKindEntry, 0.95, Array.Empty<string>());
        var diagnosticsResult = policy.Evaluate(profile, diagnosticsKindEntry, 0.95, Array.Empty<string>());

        Assert.AreEqual(VectorCandidateEligibilityStatuses.Eligible, normalResult.EligibilityStatus);
        Assert.AreEqual(VectorCandidateEligibilityStatuses.Blocked, diagnosticsResult.EligibilityStatus);
        Assert.IsTrue(diagnosticsResult.BlockedReasons.Contains(VectorCandidateBlockedReason.DiagnosticsOnlyItemKindBlocked));
    }

    [TestMethod]
    public async Task VectorQueryShadowEval_ShouldCountMustNotHitRisk()
    {
        var contextStore = new InMemoryContextStore();
        await contextStore.SaveAsync(CreateContextItem("item-banned", "banned source", "note"));
        var store = new InMemoryVectorIndexStore();
        await store.UpsertAsync(await CreateSourceBackedEntryAsync(
            "entry-banned",
            "item-banned",
            "banned source",
            [1.0f, 0.0f],
            metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lifecycle"] = "Active",
                ["sourceKind"] = "memory"
            }));
        var runner = new VectorQueryShadowEvalRunner(
            CreateQueryPreviewService(store, new FixedEmbeddingGenerator(), contextStore));
        var samples = new[]
        {
            new ContextEvalSample
            {
                Id = "sample-risk",
                Mode = "ChatMode",
                Query = "alpha query",
                MustNotHit = ["item-banned"]
            }
        };

        var report = await runner.RunAsync(samples, "workspace-test", "collection-test");

        Assert.AreEqual(1, report.Samples);
        Assert.IsTrue(report.MustNotHitRiskAtK > 0);
        Assert.AreEqual(VectorQueryShadowRecommendations.BlockedByRisk, report.Recommendation);
    }

    [TestMethod]
    public async Task VectorQueryShadowEval_ShouldNotAffectFormalRetrievalOutput()
    {
        var store = new InMemoryVectorIndexStore();
        await store.UpsertAsync(CreateEntry("entry-alpha", "item-alpha", [1.0f, 0.0f]));
        var before = await store.ListAsync(new VectorIndexQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Take = 10
        });
        var runner = new VectorQueryShadowEvalRunner(
            CreateQueryPreviewService(store, new FixedEmbeddingGenerator(), new InMemoryContextStore()));

        var report = await runner.RunAsync(
            [
                new ContextEvalSample
                {
                    Id = "sample-shadow",
                    Mode = "ProjectMode",
                    Query = "alpha query",
                    MustHit = ["item-alpha"]
                }
            ],
            "workspace-test",
            "collection-test");
        var after = await store.ListAsync(new VectorIndexQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Take = 10
        });

        Assert.AreEqual(0, report.FormalOutputChanged);
        Assert.AreEqual(before.Count, after.Count);
        CollectionAssert.AreEqual(
            before.Select(item => item.EntryId).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            after.Select(item => item.EntryId).Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [TestMethod]
    public void VectorQueryShadowEval_RiskAfterPolicyShouldBlockRecommendation()
    {
        var report = VectorQueryShadowEvalRunner.BuildReport(
            "risk-after-test",
            [
                new VectorQueryShadowEvalSample
                {
                    SampleId = "sample-risk-after",
                    RawCandidateCount = 1,
                    EligibleCandidateCount = 1,
                    CandidateCount = 1,
                    RiskAfterPolicy = 1
                }
            ]);

        Assert.AreEqual(VectorQueryShadowRecommendations.BlockedByRisk, report.Recommendation);
    }

    [TestMethod]
    public void EmbeddingProviderDiagnostics_OnnxMissingModel_ShouldReportUnavailableAndMissingModel()
    {
        var missingModelPath = Path.Combine(Path.GetTempPath(), "contextcore-missing-model", Guid.NewGuid().ToString("N"), "model.onnx");

        var diagnostics = EmbeddingProviderDiagnosticsBuilder.Build(new EmbeddingProviderOptions
        {
            ProviderId = "onnx-local-test",
            ProviderType = EmbeddingProviderTypes.OnnxLocal,
            ModelPath = missingModelPath,
            TokenizerPath = Path.Combine(Path.GetTempPath(), "contextcore-missing-model", "vocab.txt"),
            EmbeddingModel = "local-test-model",
            Dimension = 384,
            Enabled = true
        });

        Assert.IsTrue(diagnostics.Any(item => item.Type == VectorIndexDiagnosticTypes.ProviderUnavailable));
        Assert.IsTrue(diagnostics.Any(item => item.Type == VectorIndexDiagnosticTypes.ModelFileMissing));
    }

    [TestMethod]
    public void EmbeddingProviderDiagnostics_DefaultDeterministicHash_ShouldNotRequireModelFile()
    {
        var diagnostics = EmbeddingProviderDiagnosticsBuilder.Build(new EmbeddingProviderOptions());

        Assert.AreEqual(0, diagnostics.Count);
    }

    [TestMethod]
    public void EmbeddingProviderDiagnostics_DisabledOnnx_ShouldReportProviderUnavailable()
    {
        var diagnostics = EmbeddingProviderDiagnosticsBuilder.Build(new EmbeddingProviderOptions
        {
            ProviderType = EmbeddingProviderTypes.OnnxLocal,
            Enabled = false
        });

        Assert.IsTrue(diagnostics.Any(item => item.Type == VectorIndexDiagnosticTypes.ProviderUnavailable));
        Assert.IsFalse(diagnostics.Any(item => item.Type == VectorIndexDiagnosticTypes.ModelFileMissing));
    }

    [TestMethod]
    public async Task EmbeddingProviderSmoke_MissingModel_ShouldFailWithModelFileMissing()
    {
        var missingModelPath = Path.Combine(Path.GetTempPath(), "contextcore-smoke-missing-model", Guid.NewGuid().ToString("N"), "model.onnx");

        var report = await new EmbeddingProviderSmokeTester().RunAsync(new EmbeddingProviderOptions
        {
            ProviderId = "onnx-local-test",
            ProviderType = EmbeddingProviderTypes.OnnxLocal,
            ModelPath = missingModelPath,
            TokenizerPath = Path.Combine(Path.GetTempPath(), "contextcore-smoke-missing-model", "vocab.txt"),
            EmbeddingModel = "local-test-model",
            Dimension = 384,
            Enabled = true
        });

        Assert.IsFalse(report.Succeeded);
        Assert.IsTrue(report.Diagnostics.Any(item => item.Type == VectorIndexDiagnosticTypes.ModelFileMissing));
    }

    [TestMethod]
    public async Task EmbeddingProviderSmoke_MissingTokenizer_ShouldFailWithTokenizerUnavailable()
    {
        var root = CreateTempRoot();
        try
        {
            var modelPath = Path.Combine(root, "model.onnx");
            await File.WriteAllBytesAsync(modelPath, [0x00, 0x01, 0x02]);

            var report = await new EmbeddingProviderSmokeTester().RunAsync(new EmbeddingProviderOptions
            {
                ProviderId = "onnx-local-test",
                ProviderType = EmbeddingProviderTypes.OnnxLocal,
                ModelPath = modelPath,
                TokenizerPath = Path.Combine(root, "missing-vocab.txt"),
                EmbeddingModel = "local-test-model",
                Dimension = 384,
                Enabled = true
            });

            Assert.IsFalse(report.Succeeded);
            Assert.IsTrue(report.ModelPathExists);
            Assert.IsFalse(report.TokenizerPathExists);
            Assert.IsTrue(report.Diagnostics.Any(item => item.Type == VectorIndexDiagnosticTypes.TokenizerUnavailable));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [TestMethod]
    public async Task EmbeddingProviderSmoke_Deterministic_ShouldPassWithoutModelFile()
    {
        var report = await new EmbeddingProviderSmokeTester().RunAsync(new EmbeddingProviderOptions());

        Assert.IsTrue(report.Succeeded);
        Assert.AreEqual(0, report.Diagnostics.Count);
        Assert.IsFalse(report.ModelPathExists);
    }

    [TestMethod]
    public void VectorQwen3ReadinessGate_A3RecallBelowThreshold_ShouldBlock()
    {
        var report = new VectorQwen3ProviderEvalRunner().BuildReadinessGate(
            NewSucceededQwenSmokeReport(),
            NewShadowReport(0.79, riskAfterPolicy: 0),
            NewShadowReport(0.85, riskAfterPolicy: 0),
            pgVectorParityPassed: true,
            p15GatePassed: true);

        Assert.IsFalse(report.Passed);
        Assert.AreEqual("BlockedByA3Recall", report.Recommendation);
        Assert.IsTrue(report.BlockedReasons.Contains("A3RecallBelow80Percent"));
        Assert.IsFalse(report.FormalRetrievalAllowed);
    }

    [TestMethod]
    public void VectorQwen3ReadinessGate_RiskAfterPolicy_ShouldBlock()
    {
        var report = new VectorQwen3ProviderEvalRunner().BuildReadinessGate(
            NewSucceededQwenSmokeReport(),
            NewShadowReport(0.85, riskAfterPolicy: 1),
            NewShadowReport(0.85, riskAfterPolicy: 0),
            pgVectorParityPassed: true,
            p15GatePassed: true);

        Assert.IsFalse(report.Passed);
        Assert.AreEqual("BlockedByRisk", report.Recommendation);
        Assert.IsTrue(report.BlockedReasons.Contains("RiskAfterPolicyNonZero"));
    }

    [TestMethod]
    public void VectorQwen3ReadinessGate_ProjectionMismatch_ShouldBlock()
    {
        var report = new VectorQwen3ProviderEvalRunner().BuildReadinessGate(
            NewSucceededQwenSmokeReport(),
            NewShadowReport(0.85, riskAfterPolicy: 0),
            NewShadowReport(0.85, riskAfterPolicy: 0),
            pgVectorParityPassed: false,
            p15GatePassed: true,
            projectionMismatchCount: 1);

        Assert.IsFalse(report.Passed);
        Assert.AreEqual("BlockedByProjectionMismatch", report.Recommendation);
        Assert.IsTrue(report.BlockedReasons.Contains("ProjectionMismatchNonZero"));
        Assert.IsTrue(report.BlockedReasons.Contains("PgVectorFileSystemParityNotPassed"));
    }

    [TestMethod]
    public async Task VectorQueryPreview_DimensionMismatch_ShouldBlockCandidateWithDiagnostics()
    {
        var store = new InMemoryVectorIndexStore();
        await store.UpsertAsync(CreateEntry("entry-dimension-mismatch", "item-dimension-mismatch", [1.0f, 0.0f, 0.0f], dimension: 3));
        var service = CreateQueryPreviewService(store, new FixedEmbeddingGenerator(), new InMemoryContextStore());

        var result = await service.PreviewAsync(CreateQueryPreviewRequest("alpha query"));

        var candidate = result.Candidates.Single();
        Assert.AreEqual(VectorCandidateEligibilityStatuses.Blocked, candidate.EligibilityStatus);
        Assert.IsTrue(candidate.Diagnostics.Contains(VectorIndexDiagnosticTypes.DimensionMismatch));
        Assert.IsTrue(candidate.BlockedReasons.Contains(VectorCandidateBlockedReason.DimensionMismatchBlocked));
    }

    [TestMethod]
    public async Task VectorIndexDiagnostics_ProviderChanged_ShouldRequireReindex()
    {
        var contextStore = new InMemoryContextStore();
        await contextStore.SaveAsync(CreateContextItem("item-provider-change", "provider change source", "note"));
        var store = new InMemoryVectorIndexStore();
        await store.UpsertAsync(await CreateSourceBackedEntryAsync(
            "entry-provider-change",
            "item-provider-change",
            "provider change source",
            [1.0f, 0.0f],
            embeddingProvider: "old-provider",
            embeddingModel: "old-model"));
        var service = new VectorIndexService(
            store,
            new DeterministicHashEmbeddingGenerator(),
            contextStore,
            null);

        var diagnostics = await service.GetDiagnosticsAsync("workspace-test", "collection-test");

        Assert.IsTrue(diagnostics.Diagnostics.Any(item => item.Type == VectorIndexDiagnosticTypes.EmbeddingProviderChanged));
        Assert.IsTrue(diagnostics.Diagnostics.Any(item => item.Type == VectorIndexDiagnosticTypes.EmbeddingModelChanged));
        Assert.IsTrue(diagnostics.Diagnostics.Any(item => item.Type == VectorIndexDiagnosticTypes.RequiresReindex));
    }

    [TestMethod]
    public void ServiceVectorIndexRenderer_ShouldRenderStatusAndDiagnostics()
    {
        var snapshot = new ServiceVectorIndexSnapshot
        {
            CurrentTime = DateTimeOffset.UtcNow,
            BaseUrl = "http://localhost:5079",
            Status = new VectorIndexStatusResponse
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Provider = "deterministic-hash",
                Model = "deterministic-hash-v1",
                Dimension = 16,
                IndexedCount = 2,
                StaleCount = 1,
                DuplicateCount = 1,
                StoreAvailable = true,
                GeneratorAvailable = true
            },
            Diagnostics = new VectorIndexDiagnosticsReport
            {
                Diagnostics =
                [
                    new VectorIndexDiagnostic
                    {
                        Type = VectorIndexDiagnosticTypes.DuplicateVectorEntry,
                        Severity = "Warning",
                        ItemId = "item-1",
                        EntryId = "entry-a",
                        Message = "duplicate",
                        SuggestedAction = "cleanup"
                    }
                ],
                CountsByType = new Dictionary<string, int>
                {
                    [VectorIndexDiagnosticTypes.DuplicateVectorEntry] = 1
                },
                DuplicateCount = 1
            },
            ReindexPreview = new VectorReindexPreviewResponse
            {
                SourceItemCount = 2,
                WouldCreateCount = 1,
                Items =
                [
                    new VectorReindexPreviewItem
                    {
                        ItemId = "item-2",
                        ItemKind = "note",
                        Layer = "context",
                        Action = "Create",
                        Reason = "missing"
                    }
                ]
            },
            Coverage = new VectorIndexCoverageReport
            {
                TotalSourceItems = 2,
                IndexedItems = 1,
                CoverageRate = 0.5,
                DuplicateCount = 1,
                Recommendation = VectorIndexCoverageRecommendations.BlockedByDiagnostics,
                MissingByLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["context"] = 1
                }
            },
            ShadowQuality = new ServiceVectorShadowQualitySummary
            {
                Available = true,
                SourcePath = "eval/vector-query-profile-sweep-extended.json",
                CurrentRecommendation = VectorQueryShadowRecommendations.NeedsPolicyTuning,
                BestProfile = VectorQueryProfileIds.NormalV1,
                BestTopK = 20,
                BestMinSimilarity = 0.1,
                RiskAfterPolicy = 0,
                SimilaritySeparation = -0.01,
                ResidualRiskSourcePath = "eval/vector-residual-risk-audit-extended.json",
                ResidualRiskCount = 2,
                TopResidualRiskTypes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    [VectorResidualRiskTypes.LifecycleMetadataGap] = 2
                },
                TopWhyPolicyAllowed = ["候选在运行时 metadata 中 lifecycle/status='missing'，未触发当前 profile 的阻断规则。"],
                TopExpectedActions = ["补齐 lifecycle/status/reviewStatus metadata 后重建 vector index。"],
                LifecycleMetadataCoverageSourcePath = "eval/vector-lifecycle-metadata-coverage.json",
                LifecycleMetadataCoverageRate = 0.8,
                UnknownLifecycleCount = 2,
                MissingReviewStatusCount = 4,
                MissingReplacementInfoCount = 1,
                BlockedByLifecycleMetadataGate = 2,
                V4ReadinessGateSourcePath = "eval/vector-retrieval-shadow-readiness-gate.json",
                V4ReadinessGatePassed = false,
                V4ReadinessGateFailReasons = ["A3RecallAtLeast80Percent"],
                V4GateSatisfied = false
            }
        };

        var rendered = ServiceOperationalRenderer.RenderVectorIndex(snapshot);

        StringAssert.Contains(rendered, "Service Vector Index");
        StringAssert.Contains(rendered, "deterministic-hash-v1");
        StringAssert.Contains(rendered, "Coverage Summary");
        StringAssert.Contains(rendered, "Shadow Quality Summary");
        StringAssert.Contains(rendered, "Residual Risk Summary");
        StringAssert.Contains(rendered, "Lifecycle Metadata Coverage");
        StringAssert.Contains(rendered, "A3RecallAtLeast80Percent");
        StringAssert.Contains(rendered, "blockedByGate");
        StringAssert.Contains(rendered, "LifecycleMetadataGap");
        StringAssert.Contains(rendered, "whyAllowed");
        StringAssert.Contains(rendered, "normal-v1");
        StringAssert.Contains(rendered, "BlockedByDiagnostics");
        StringAssert.Contains(rendered, "DuplicateVectorEntry");
        StringAssert.Contains(rendered, "Reindex Preview");
        StringAssert.Contains(rendered, "Q Query Preview");
    }

    [TestMethod]
    public void ServiceVectorIndexRenderer_ShouldRenderVectorQueryPreview()
    {
        var rendered = ServiceOperationalRenderer.RenderVectorQueryPreview(new VectorQueryPreviewResult
        {
            OperationId = "vector-query-op",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "alpha query",
            TopK = 5,
            Diagnostics = new VectorQueryPreviewDiagnostics
            {
                IndexedCount = 3,
                DuplicateCount = 1,
                StoreAvailable = true,
                GeneratorAvailable = true
            },
            Candidates =
            [
                new VectorQueryPreviewCandidate
                {
                    Rank = 1,
                    ItemId = "item-alpha",
                    EntryId = "entry-alpha",
                    ItemKind = "note",
                    Layer = "context",
                    Similarity = 0.98,
                    IsDuplicate = true,
                    Diagnostics = [VectorIndexDiagnosticTypes.DuplicateVectorEntry],
                    EmbeddingModel = "fixed-test-v1",
                    EmbeddingProvider = "fixed-test"
                }
            ]
        });

        StringAssert.Contains(rendered, "Vector Query Preview");
        StringAssert.Contains(rendered, "item-alpha");
        StringAssert.Contains(rendered, "sim=0.9800");
        StringAssert.Contains(rendered, "duplicate");
    }

    [TestMethod]
    public void ServiceDashboardInput_ShouldExposeVectorIndexEntry()
    {
        var action = ControlRoomInteraction.InterpretDashboardInput("37");

        Assert.AreEqual(ControlRoomActionKind.OpenServiceVectorIndex, action.Kind);
    }

    [TestMethod]
    public async Task ServiceVectorIndexScreen_ApplyShouldRequireYesConfirmation()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        EnqueueVectorSnapshotHandlers(handlers);
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/vector/reindex-plan", request.RequestUri?.AbsolutePath);
            return JsonResponse(new VectorReindexPlan
            {
                PlanId = "plan-confirm",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TotalCandidates = 1,
                ToCreate = 1,
                DryRun = true,
                Items =
                [
                    new VectorReindexPlanItem
                    {
                        ItemId = "ctx-confirm",
                        ItemKind = "safety-note",
                        Layer = "context",
                        Action = "Create",
                        Reason = "source item 尚未建立 embedding。"
                    }
                ]
            });
        });
        EnqueueVectorSnapshotHandlers(handlers);
        using var http = new HttpClient(new StubHttpMessageHandler(request => handlers.Dequeue().Invoke(request)))
        {
            BaseAddress = new Uri("http://localhost")
        };
        var service = new ControlRoomService(new ControlRoomState
        {
            Mode = ControlRoomMode.Service,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            ServiceBaseUrl = "http://localhost",
            ServiceClient = new ContextCoreClient(http)
        });
        var originalIn = Console.In;
        var originalOut = Console.Out;
        using var input = new StringReader("a\nNO\nb\n");
        using var output = new StringWriter();
        try
        {
            Console.SetIn(input);
            Console.SetOut(output);
            var action = await ServiceVectorIndexScreen.ShowAsync(service);
            Assert.AreEqual(ControlRoomActionKind.Back, action);
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }

        StringAssert.Contains(output.ToString(), "Type YES");
        StringAssert.Contains(output.ToString(), "Apply cancelled.");
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public async Task VectorReindexPlan_ShouldDetectMissingEntries()
    {
        var contextStore = new InMemoryContextStore();
        var memoryStore = new InMemoryMemoryStore();
        var vectorStore = new InMemoryVectorIndexStore();
        await contextStore.SaveAsync(CreateContextItem("ctx-alpha", "项目方案采用审计上下文保存旧版本证据。", "design-note"));
        await memoryStore.SaveAsync(CreateMemoryItem("mem-beta", "用户确认长期偏好需要人工 review 后才能稳定化。", ContextMemoryLayer.Structured));
        var planner = CreatePlanner(contextStore, memoryStore, vectorStore);

        var plan = await planner.CreatePlanAsync(CreateReindexRequest());

        Assert.AreEqual(2, plan.TotalCandidates);
        Assert.AreEqual(2, plan.ToCreate);
        CollectionAssert.Contains(plan.MissingItems.ToList(), "ctx-alpha");
        CollectionAssert.Contains(plan.MissingItems.ToList(), "mem-beta");
    }

    [TestMethod]
    public async Task VectorReindexPlan_ShouldDetectStaleEntryByContentHash()
    {
        var contextStore = new InMemoryContextStore();
        var vectorStore = new InMemoryVectorIndexStore();
        var generator = new DeterministicHashEmbeddingGenerator();
        await contextStore.SaveAsync(CreateContextItem("ctx-stale", "当前接口改为使用新的超时配置。", "coding-contract"));
        var old = await generator.GenerateAsync(CreateEmbeddingRequest("ctx-stale", "旧接口仍使用过时超时配置。"));
        await vectorStore.UpsertAsync(old.Entries[0]);
        var planner = new VectorReindexPlanner(contextStore, null, vectorStore, generator);

        var plan = await planner.CreatePlanAsync(CreateReindexRequest());

        Assert.AreEqual(1, plan.ToUpdate);
        CollectionAssert.Contains(plan.StaleItems.ToList(), "ctx-stale");
        Assert.AreEqual("source item 内容 hash 已变化。", plan.Items.Single(item => item.Action == "Update").Reason);
    }

    [TestMethod]
    public async Task VectorReindexApply_ShouldCreateVectorEntries()
    {
        var contextStore = new InMemoryContextStore();
        var vectorStore = new InMemoryVectorIndexStore();
        await contextStore.SaveAsync(CreateContextItem("ctx-create", "候选约束必须先进入 Candidate 状态。", "constraint-rule"));
        var executor = CreateExecutor(contextStore, null, vectorStore, new InMemoryVectorReindexReportStore());

        var result = await executor.ExecuteAsync(CreateReindexRequest(apply: true));
        var entries = await vectorStore.GetByItemIdAsync("workspace-test", "collection-test", "ctx-create");

        Assert.IsTrue(result.Summary.Applied);
        Assert.AreEqual(1, result.Summary.Created);
        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("deterministic-hash-v1", entries[0].EmbeddingModel);
    }

    [TestMethod]
    public async Task VectorReindexApply_WithExternalSource_ShouldCreateVectorEntry()
    {
        var vectorStore = new InMemoryVectorIndexStore();
        var request = CreateExternalSourceReindexRequest(apply: true);
        var executor = CreateExecutor(null, null, vectorStore, null);

        var result = await executor.ExecuteAsync(request);
        var entries = await vectorStore.GetByItemIdAsync("workspace-test", "collection-test", "eval:item-current");
        var indexService = new VectorIndexService(
            vectorStore,
            new DeterministicHashEmbeddingGenerator(),
            null,
            null,
            request.SourceItems);
        var diagnostics = await indexService.GetDiagnosticsAsync("workspace-test", "collection-test");

        Assert.IsTrue(result.Summary.Applied);
        Assert.AreEqual(1, result.Summary.Created);
        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual(0, diagnostics.OrphanCount);
        Assert.AreEqual("Active", entries[0].Metadata["status"]);
    }

    [TestMethod]
    public async Task VectorReindexApply_WithExternalSource_ShouldUpsertWithoutDuplicateNoise()
    {
        var vectorStore = new InMemoryVectorIndexStore();
        var request = CreateExternalSourceReindexRequest(apply: true);
        var executor = CreateExecutor(null, null, vectorStore, null);

        _ = await executor.ExecuteAsync(request);
        _ = await executor.ExecuteAsync(request);
        var entries = await vectorStore.GetByItemIdAsync("workspace-test", "collection-test", "eval:item-current");
        var indexService = new VectorIndexService(
            vectorStore,
            new DeterministicHashEmbeddingGenerator(),
            null,
            null,
            request.SourceItems);
        var diagnostics = await indexService.GetDiagnosticsAsync("workspace-test", "collection-test");

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual(0, diagnostics.DuplicateCount);
    }

    [TestMethod]
    public async Task VectorReindexApply_ShouldUpdateStaleEntry()
    {
        var contextStore = new InMemoryContextStore();
        var vectorStore = new InMemoryVectorIndexStore();
        var generator = new DeterministicHashEmbeddingGenerator();
        await contextStore.SaveAsync(CreateContextItem("ctx-update", "最新恢复策略使用 dead-letter 状态确认。", "automation-rule"));
        var old = await generator.GenerateAsync(CreateEmbeddingRequest("ctx-update", "过期恢复策略只重试三次。"));
        await vectorStore.UpsertAsync(old.Entries[0]);
        var executor = CreateExecutor(contextStore, null, vectorStore, new InMemoryVectorReindexReportStore());

        var result = await executor.ExecuteAsync(CreateReindexRequest(apply: true));
        var entries = await vectorStore.GetByItemIdAsync("workspace-test", "collection-test", "ctx-update");

        Assert.AreEqual(1, result.Summary.Updated);
        Assert.AreEqual(1, entries.Count);
        Assert.AreNotEqual(old.Entries[0].ContentHash, entries[0].ContentHash);
    }

    [TestMethod]
    public async Task VectorReindexPlan_ShouldReportDuplicateEntries()
    {
        var contextStore = new InMemoryContextStore();
        var vectorStore = new InMemoryVectorIndexStore();
        await contextStore.SaveAsync(CreateContextItem("ctx-duplicate", "图谱替代链只允许 latest 方向进入正常上下文。", "graph-policy"));
        await vectorStore.UpsertAsync(CreateDeterministicEntry("entry-current", "ctx-duplicate", "duplicate-current-hash", [1.0f, 0.0f], DateTimeOffset.UtcNow));
        await vectorStore.UpsertAsync(CreateDeterministicEntry("entry-old", "ctx-duplicate", "duplicate-old-hash", [0.9f, 0.1f], DateTimeOffset.UtcNow.AddMinutes(-5)));
        var planner = CreatePlanner(contextStore, null, vectorStore);

        var plan = await planner.CreatePlanAsync(CreateReindexRequest());

        Assert.AreEqual(1, plan.DuplicateItems.Count);
        Assert.AreEqual(1, plan.Items.Count(item => item.Action == "Duplicate"));
        CollectionAssert.Contains(plan.DuplicateItems.ToList(), "ctx-duplicate");
    }

    [TestMethod]
    public async Task VectorReindexPlan_ShouldReportOrphanEntries()
    {
        var vectorStore = new InMemoryVectorIndexStore();
        await vectorStore.UpsertAsync(CreateDeterministicEntry("entry-orphan", "ctx-orphan", "hash-orphan", [0.0f, 1.0f], DateTimeOffset.UtcNow));
        var planner = CreatePlanner(new InMemoryContextStore(), new InMemoryMemoryStore(), vectorStore);

        var plan = await planner.CreatePlanAsync(CreateReindexRequest());

        Assert.AreEqual(1, plan.ToDeleteOrphan);
        CollectionAssert.Contains(plan.OrphanItems.ToList(), "ctx-orphan");
    }

    [TestMethod]
    public async Task VectorReindexDryRun_ShouldNotWriteVectorEntries()
    {
        var contextStore = new InMemoryContextStore();
        var vectorStore = new InMemoryVectorIndexStore();
        await contextStore.SaveAsync(CreateContextItem("ctx-dry-run", "Dry run 只产生计划，不写入 vector index。", "diagnostic-note"));
        var executor = CreateExecutor(contextStore, null, vectorStore, null);

        var result = await executor.ExecuteAsync(CreateReindexRequest(apply: false));
        var entries = await vectorStore.ListAsync(new VectorIndexQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Take = 20
        });

        Assert.IsTrue(result.Summary.DryRun);
        Assert.AreEqual(0, entries.Count);
    }

    [TestMethod]
    public async Task VectorReindexApply_ShouldRequireExplicitConfirm()
    {
        var contextStore = new InMemoryContextStore();
        var vectorStore = new InMemoryVectorIndexStore();
        await contextStore.SaveAsync(CreateContextItem("ctx-confirm", "Apply 必须显式确认，避免误写入。", "safety-note"));
        var executor = CreateExecutor(contextStore, null, vectorStore, null);
        var request = CreateReindexRequest(apply: true);
        request = new VectorReindexRequest
        {
            OperationId = request.OperationId,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            DryRun = request.DryRun,
            Apply = request.Apply,
            ConfirmApply = false,
            MaxItems = request.MaxItems,
            BatchSize = request.BatchSize,
            IncludeContextItems = request.IncludeContextItems,
            IncludeMemoryItems = request.IncludeMemoryItems
        };

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => executor.ExecuteAsync(request));
    }

    [TestMethod]
    public async Task FileSystemVectorReindexPipeline_ShouldCreateEntryAndReport()
    {
        var root = CreateTempRoot();
        try
        {
            var options = new FileStorageOptions { RootPath = root };
            var contextStore = new FileContextStore(options);
            var vectorStore = new FileVectorIndexStore(options);
            var reportStore = new FileVectorReindexReportStore(options);
            await contextStore.SaveAsync(CreateContextItem("ctx-filesystem", "文件系统 reindex 报告应按 ReportId upsert 保存。", "filesystem-note"));
            var executor = CreateExecutor(contextStore, null, vectorStore, reportStore);

            var result = await executor.ExecuteAsync(CreateReindexRequest(apply: true));
            var entries = await vectorStore.GetByItemIdAsync("workspace-test", "collection-test", "ctx-filesystem");
            var reports = await reportStore.QueryAsync("workspace-test", "collection-test", 10);

            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual(1, reports.Count);
            Assert.AreEqual(result.ReportId, reports[0].ReportId);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [TestMethod]
    public async Task VectorIndexCoverage_EmptyIndex_ShouldRecommendInitialIndexing()
    {
        var contextStore = new InMemoryContextStore();
        var vectorStore = new InMemoryVectorIndexStore();
        await contextStore.SaveAsync(CreateContextItem("ctx-empty-coverage", "coverage 应真实报告空索引。", "coverage-note"));

        var report = await BuildCoverageReportAsync(contextStore, null, vectorStore);

        Assert.AreEqual(1, report.TotalSourceItems);
        Assert.AreEqual(0, report.IndexedItems);
        Assert.AreEqual(VectorIndexCoverageRecommendations.NeedsInitialIndexing, report.Recommendation);
    }

    [TestMethod]
    public async Task VectorIndexCoverage_IndexedEntries_ShouldReportPositiveCoverage()
    {
        var contextStore = new InMemoryContextStore();
        var vectorStore = new InMemoryVectorIndexStore();
        await contextStore.SaveAsync(CreateContextItem("ctx-indexed-coverage", "coverage 已索引项应显示正覆盖率。", "coverage-note"));
        await CreateExecutor(contextStore, null, vectorStore, null)
            .ExecuteAsync(CreateReindexRequest(apply: true));

        var report = await BuildCoverageReportAsync(contextStore, null, vectorStore);

        Assert.AreEqual(1, report.TotalSourceItems);
        Assert.AreEqual(1, report.IndexedItems);
        Assert.IsTrue(report.CoverageRate > 0);
        Assert.AreEqual(VectorIndexCoverageRecommendations.ReadyForVectorShadowEval, report.Recommendation);
    }

    [TestMethod]
    public async Task VectorIndexCoverage_DuplicateEntries_ShouldBlockByDiagnostics()
    {
        var contextStore = new InMemoryContextStore();
        var vectorStore = new InMemoryVectorIndexStore();
        await contextStore.SaveAsync(CreateContextItem("ctx-coverage-duplicate", "重复索引应被 coverage 阻断。", "coverage-note"));
        await vectorStore.UpsertAsync(CreateDeterministicEntry("entry-dup-a", "ctx-coverage-duplicate", "hash-a", [1.0f, 0.0f], DateTimeOffset.UtcNow));
        await vectorStore.UpsertAsync(CreateDeterministicEntry("entry-dup-b", "ctx-coverage-duplicate", "hash-b", [0.9f, 0.1f], DateTimeOffset.UtcNow.AddMinutes(-1)));

        var report = await BuildCoverageReportAsync(contextStore, null, vectorStore);

        Assert.IsTrue(report.DuplicateCount > 0);
        Assert.AreEqual(VectorIndexCoverageRecommendations.BlockedByDiagnostics, report.Recommendation);
    }

    [TestMethod]
    public async Task VectorIndexCoverage_StaleEntries_ShouldRecommendReindex()
    {
        var contextStore = new InMemoryContextStore();
        var vectorStore = new InMemoryVectorIndexStore();
        var generator = new DeterministicHashEmbeddingGenerator();
        await contextStore.SaveAsync(CreateContextItem("ctx-coverage-stale", "当前内容用于覆盖率报告。", "coverage-note"));
        var old = await generator.GenerateAsync(CreateEmbeddingRequest("ctx-coverage-stale", "旧内容"));
        await vectorStore.UpsertAsync(old.Entries[0]);

        var report = await BuildCoverageReportAsync(contextStore, null, vectorStore);

        Assert.IsTrue(report.StaleByLayer.Values.Sum() > 0);
        Assert.AreEqual(VectorIndexCoverageRecommendations.NeedsReindex, report.Recommendation);
    }

    [TestMethod]
    public async Task VectorIndexCoverage_ShouldNotWriteIndex()
    {
        var contextStore = new InMemoryContextStore();
        var vectorStore = new InMemoryVectorIndexStore();
        await contextStore.SaveAsync(CreateContextItem("ctx-coverage-readonly", "coverage report 只读，不写入 index。", "coverage-note"));
        var before = await vectorStore.ListAsync(new VectorIndexQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Take = 10
        });

        _ = await BuildCoverageReportAsync(contextStore, null, vectorStore);
        var after = await vectorStore.ListAsync(new VectorIndexQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Take = 10
        });

        Assert.AreEqual(before.Count, after.Count);
    }

    private static EmbeddingGeneratorRequest CreateEmbeddingRequest(string itemId, string text)
    {
        return new EmbeddingGeneratorRequest
        {
            OperationId = "test-op",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Inputs =
            [
                new EmbeddingGeneratorInput
                {
                    ItemId = itemId,
                    Text = text,
                    ItemKind = "note",
                    Layer = "context"
                }
            ]
        };
    }

    private static VectorQueryPreviewCandidate NewPreviewCandidate(
        string itemId,
        int rank,
        int rawRank,
        double similarity,
        string eligibility = VectorCandidateEligibilityStatuses.Eligible,
        IReadOnlyList<string>? blockedReasons = null)
    {
        return new VectorQueryPreviewCandidate
        {
            CandidateId = itemId,
            EntryId = $"entry-{itemId}",
            ItemId = itemId,
            ItemKind = "note",
            Layer = "context",
            Rank = rank,
            RawRank = rawRank,
            Similarity = similarity,
            ContentHash = $"hash-{itemId}",
            EmbeddingProvider = "fixed-test",
            EmbeddingModel = "fixed-test-v1",
            Dimension = 2,
            EligibilityStatus = eligibility,
            BlockedReasons = blockedReasons ?? Array.Empty<string>(),
            TargetSection = VectorQueryTargetSections.NormalContext,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lifecycle"] = "Active",
                ["sourceKind"] = "memory"
            }
        };
    }

    private static VectorIndexEntry CreateEntry(
        string entryId,
        string itemId,
        IReadOnlyList<float> vector,
        int? dimension = null,
        string layer = "context",
        string itemKind = "note",
        Dictionary<string, string>? metadata = null,
        string? embeddingProvider = null,
        string? embeddingModel = null)
    {
        return new VectorIndexEntry
        {
            EntryId = entryId,
            ItemId = itemId,
            ItemKind = itemKind,
            Layer = layer,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            ContentHash = $"hash-{itemId}",
            EmbeddingProvider = embeddingProvider ?? "fixed-test",
            EmbeddingModel = embeddingModel ?? "fixed-test-v1",
            Dimension = dimension ?? vector.Count,
            Vector = vector.ToArray(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Metadata = metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static async Task<VectorIndexEntry> CreateSourceBackedEntryAsync(
        string entryId,
        string itemId,
        string sourceText,
        IReadOnlyList<float> vector,
        string layer = "context",
        string itemKind = "note",
        Dictionary<string, string>? metadata = null,
        string? embeddingProvider = null,
        string? embeddingModel = null)
    {
        var generated = await new DeterministicHashEmbeddingGenerator().GenerateAsync(new EmbeddingGeneratorRequest
        {
            OperationId = "source-backed-entry-test",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Inputs =
            [
                new EmbeddingGeneratorInput
                {
                    ItemId = itemId,
                    Text = sourceText,
                    ItemKind = itemKind,
                    Layer = layer,
                    Metadata = metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                }
            ]
        });
        var template = generated.Entries[0];
        return new VectorIndexEntry
        {
            EntryId = entryId,
            ItemId = itemId,
            ItemKind = itemKind,
            Layer = layer,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            ContentHash = template.ContentHash,
            EmbeddingProvider = embeddingProvider ?? "fixed-test",
            EmbeddingModel = embeddingModel ?? "fixed-test-v1",
            Dimension = vector.Count,
            Vector = vector.ToArray(),
            CreatedAt = template.CreatedAt,
            UpdatedAt = template.UpdatedAt,
            Metadata = metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static VectorQueryPreviewRequest CreateQueryPreviewRequest(
        string query,
        int topK = 10,
        string? layer = null,
        string? itemKind = null,
        double? minSimilarity = null,
        string profileId = VectorQueryProfileIds.NormalV1)
    {
        return new VectorQueryPreviewRequest
        {
            OperationId = "vector-query-test",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = query,
            TopK = topK,
            ProfileId = profileId,
            Layer = layer,
            ItemKind = itemKind,
            MinSimilarity = minSimilarity
        };
    }

    private static VectorQueryPreviewService CreateQueryPreviewService(
        IVectorIndexStore store,
        IEmbeddingGenerator generator,
        IContextStore contextStore)
    {
        var indexService = new VectorIndexService(store, generator, contextStore, null);
        return new VectorQueryPreviewService(store, generator, indexService);
    }

    private static ContextItem CreateContextItem(
        string id,
        string content,
        string type)
    {
        var now = DateTimeOffset.UtcNow;
        return new ContextItem
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = type,
            Content = content,
            CreatedAt = now,
            UpdatedAt = now,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["fixture"] = "vector-reindex-v2"
            }
        };
    }

    private static ContextMemoryItem CreateMemoryItem(
        string id,
        string content,
        ContextMemoryLayer layer)
    {
        var now = DateTimeOffset.UtcNow;
        return new ContextMemoryItem
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "candidate-memory-note",
            Content = content,
            Layer = layer,
            Status = ContextMemoryStatus.Candidate,
            CreatedAt = now,
            UpdatedAt = now,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["fixture"] = "vector-reindex-v2"
            }
        };
    }

    private static VectorReindexRequest CreateReindexRequest(bool apply = false)
    {
        return new VectorReindexRequest
        {
            OperationId = "vector-reindex-test",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            DryRun = !apply,
            Apply = apply,
            ConfirmApply = apply,
            MaxItems = 50,
            BatchSize = 10,
            IncludeContextItems = true,
            IncludeMemoryItems = true
        };
    }

    private static VectorReindexRequest CreateExternalSourceReindexRequest(bool apply = false)
    {
        return new VectorReindexRequest
        {
            OperationId = "vector-reindex-external-source-test",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            DryRun = !apply,
            Apply = apply,
            ConfirmApply = apply,
            MaxItems = 50,
            BatchSize = 10,
            IncludeContextItems = false,
            IncludeMemoryItems = false,
            SourceItems =
            [
                new VectorReindexSourceItem
                {
                    ItemId = "eval:item-current",
                    ItemKind = "preference",
                    Layer = "Stable",
                    Text = "当前稳定偏好来自真实 eval corpus source，不需要先写入 context store。",
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["status"] = "Active",
                        ["lifecycle"] = "Active",
                        ["sourceMode"] = "test-external-source"
                    }
                }
            ]
        };
    }

    private static VectorReindexPlanner CreatePlanner(
        IContextStore? contextStore,
        IMemoryStore? memoryStore,
        IVectorIndexStore vectorStore)
    {
        return new VectorReindexPlanner(
            contextStore,
            memoryStore,
            vectorStore,
            new DeterministicHashEmbeddingGenerator());
    }

    private static VectorReindexExecutor CreateExecutor(
        IContextStore? contextStore,
        IMemoryStore? memoryStore,
        IVectorIndexStore vectorStore,
        IVectorReindexReportStore? reportStore)
    {
        var generator = new DeterministicHashEmbeddingGenerator();
        var planner = new VectorReindexPlanner(contextStore, memoryStore, vectorStore, generator);
        return new VectorReindexExecutor(planner, generator, vectorStore, reportStore);
    }

    private static async Task<VectorIndexCoverageReport> BuildCoverageReportAsync(
        IContextStore? contextStore,
        IMemoryStore? memoryStore,
        IVectorIndexStore vectorStore)
    {
        var generator = new DeterministicHashEmbeddingGenerator();
        var planner = new VectorReindexPlanner(contextStore, memoryStore, vectorStore, generator);
        var indexService = new VectorIndexService(vectorStore, generator, contextStore, memoryStore);
        var request = CreateReindexRequest();
        var plan = await planner.CreatePlanAsync(request);
        var diagnostics = await indexService.GetDiagnosticsAsync(request.WorkspaceId, request.CollectionId);
        var status = await indexService.GetStatusAsync(request.WorkspaceId, request.CollectionId);

        return VectorIndexCoverageReportBuilder.Build(plan, diagnostics, status);
    }

    private static VectorReindexSourceItem CreateLifecycleBackfillSource(
        string itemId,
        Dictionary<string, string> metadata,
        string layer = "context")
    {
        return new VectorReindexSourceItem
        {
            ItemId = itemId,
            ItemKind = "note",
            Layer = layer,
            Text = $"source text for {itemId}",
            UpdatedAt = DateTimeOffset.UtcNow,
            Metadata = metadata
        };
    }

    private static VectorIndexEntry CreateDeterministicEntry(
        string entryId,
        string itemId,
        string contentHash,
        IReadOnlyList<float> vector,
        DateTimeOffset updatedAt)
    {
        return new VectorIndexEntry
        {
            EntryId = entryId,
            ItemId = itemId,
            ItemKind = "note",
            Layer = "context",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            ContentHash = contentHash,
            EmbeddingProvider = "deterministic-hash",
            EmbeddingModel = "deterministic-hash-v1",
            Dimension = vector.Count,
            Vector = vector.ToArray(),
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt
        };
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "contextcore-vector-index-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void EnqueueVectorSnapshotHandlers(Queue<Func<HttpRequestMessage, HttpResponseMessage>> handlers)
    {
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/vector/status", request.RequestUri?.AbsolutePath);
            return JsonResponse(new VectorIndexStatusResponse
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Provider = "deterministic-hash",
                Model = "deterministic-hash-v1",
                Dimension = 16,
                StoreAvailable = true,
                GeneratorAvailable = true
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/vector/diagnostics", request.RequestUri?.AbsolutePath);
            return JsonResponse(new VectorIndexDiagnosticsReport
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test"
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/vector/reindex-preview", request.RequestUri?.AbsolutePath);
            return JsonResponse(new VectorReindexPreviewResponse
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                SourceItemCount = 1,
                WouldCreateCount = 1,
                Items =
                [
                    new VectorReindexPreviewItem
                    {
                        ItemId = "ctx-preview",
                        ItemKind = "diagnostic-note",
                        Layer = "context",
                        Action = "Create",
                        Reason = "missing"
                    }
                ]
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/vector/reindex-plan", request.RequestUri?.AbsolutePath);
            return JsonResponse(new VectorReindexPlan
            {
                PlanId = "plan-coverage",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TotalCandidates = 1,
                ToCreate = 1,
                DryRun = true,
                Items =
                [
                    new VectorReindexPlanItem
                    {
                        ItemId = "ctx-preview",
                        ItemKind = "note",
                        Layer = "context",
                        Action = "Create",
                        Reason = "source item 尚未建立 embedding。"
                    }
                ],
                MissingItems = ["ctx-preview"]
            });
        });
    }

    private static HttpResponseMessage JsonResponse<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static EmbeddingProviderSmokeReport NewSucceededQwenSmokeReport()
    {
        return new EmbeddingProviderSmokeReport
        {
            ProviderId = "qwen3-embedding-0.6b-onnx",
            ProviderType = EmbeddingProviderTypes.OnnxLocal,
            EmbeddingModel = "qwen3-embedding-0.6b",
            ModelPath = "src/ContextCore.Embedding/Models/qwen3-embedding-0.6b-onnx/model_int8.onnx",
            TokenizerPath = "src/ContextCore.Embedding/Models/qwen3-embedding-0.6b-onnx/tokenizer.json",
            ExpectedDimension = 1024,
            ActualDimension = 1024,
            UseForRuntime = false,
            ProviderEnabled = true,
            ModelPathExists = true,
            TokenizerPathExists = true,
            TokenizationWorks = true,
            OnnxInferenceWorks = true,
            DimensionMatchesConfig = true,
            NormalizationWorks = true,
            BatchEmbeddingWorks = true,
            Succeeded = true
        };
    }

    private static VectorQueryShadowEvalReport NewShadowReport(double recall, int riskAfterPolicy)
    {
        return new VectorQueryShadowEvalReport
        {
            Samples = 10,
            QueryCount = 10,
            CandidateCount = 100,
            MustHitRecallAfterPolicy = recall,
            MustNotHitRiskAfterPolicy = 0,
            LifecycleRiskAfterPolicy = 0,
            RiskAfterPolicy = riskAfterPolicy,
            FormalOutputChanged = 0,
            Recommendation = riskAfterPolicy == 0
                ? VectorQueryShadowRecommendations.ReadyForRetrievalShadow
                : "BlockedByRisk"
        };
    }

    private sealed class FixedEmbeddingGenerator : IEmbeddingGenerator
    {
        public string Provider => "fixed-test";

        public string Model => "fixed-test-v1";

        public int Dimension => 2;

        public Task<EmbeddingGeneratorResult> GenerateAsync(
            EmbeddingGeneratorRequest request,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            var entries = request.Inputs.Select(input =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var vector = input.Text.Contains("beta", StringComparison.OrdinalIgnoreCase)
                    ? new[] { 0.0f, 1.0f }
                    : new[] { 1.0f, 0.0f };
                return new VectorIndexEntry
                {
                    EntryId = $"{request.WorkspaceId}:{request.CollectionId}:{input.ItemId}:fixed-test",
                    ItemId = input.ItemId,
                    ItemKind = input.ItemKind,
                    Layer = input.Layer,
                    WorkspaceId = request.WorkspaceId,
                    CollectionId = request.CollectionId,
                    ContentHash = $"hash-{input.Text}",
                    EmbeddingProvider = Provider,
                    EmbeddingModel = Model,
                    Dimension = Dimension,
                    Vector = vector,
                    CreatedAt = now,
                    UpdatedAt = now,
                    Metadata = new Dictionary<string, string>(input.Metadata, StringComparer.OrdinalIgnoreCase)
                };
            }).ToArray();

            return Task.FromResult(new EmbeddingGeneratorResult
            {
                OperationId = request.OperationId,
                EmbeddingModel = Model,
                EmbeddingProvider = Provider,
                Dimension = Dimension,
                Entries = entries
            });
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
