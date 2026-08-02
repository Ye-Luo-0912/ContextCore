using System.Runtime.CompilerServices;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Graph;
using ContextCore.Evaluation.Runners;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// 流式图诊断（IRelationStreamStore + ValidateStreamAsync）单元测试。
/// 验证流式路径产出与非流式 ValidateAsync 的语义对齐，覆盖 fallback 路径与各阶段诊断。
/// </summary>
[TestClass]
[TestCategory("Relation")]
[TestCategory("P1-7")]
public sealed class ContextCoreRelationStreamDiagnosticsTests
{
    [TestMethod]
    public async Task InMemoryRelationStore_ImplementsIRelationStreamStore()
    {
        IRelationStore store = new InMemoryRelationStore();

        var streamStore = store as IRelationStreamStore;

        Assert.IsNotNull(streamStore, "InMemoryRelationStore should implement IRelationStreamStore.");
    }

    [TestMethod]
    public async Task StreamRelationsAsync_ReturnsAllMatchingRelations()
    {
        var store = new InMemoryRelationStore();
        await store.BatchUpsertAsync(new[]
        {
            Relation("rel-1", "src-a", "references", "tgt-a"),
            Relation("rel-2", "src-a", "references", "tgt-b"),
            Relation("rel-3", "src-b", "references", "tgt-c")
        }, CancellationToken.None);

        var streamStore = (IRelationStreamStore)store;
        var collected = new List<ContextRelation>();
        await foreach (var relation in streamStore.StreamRelationsAsync(
            "workspace-test", "collection-test", null, CancellationToken.None)
            .ConfigureAwait(false))
        {
            collected.Add(relation);
        }

        Assert.AreEqual(3, collected.Count);
        CollectionAssert.AreEquivalent(
            new[] { "rel-1", "rel-2", "rel-3" },
            collected.Select(r => r.Id).ToArray());
    }

    [TestMethod]
    public async Task StreamRelationsAsync_ItemFilter_ReturnsOnlyMatchingEdges()
    {
        var store = new InMemoryRelationStore();
        await store.BatchUpsertAsync(new[]
        {
            Relation("rel-1", "src-a", "references", "tgt-a"),
            Relation("rel-2", "src-a", "references", "tgt-b"),
            Relation("rel-3", "src-b", "references", "tgt-c")
        }, CancellationToken.None);

        var streamStore = (IRelationStreamStore)store;
        var collected = new List<ContextRelation>();
        await foreach (var relation in streamStore.StreamRelationsAsync(
            "workspace-test", "collection-test", "src-a", CancellationToken.None)
            .ConfigureAwait(false))
        {
            collected.Add(relation);
        }

        Assert.AreEqual(2, collected.Count);
        CollectionAssert.AreEquivalent(
            new[] { "rel-1", "rel-2" },
            collected.Select(r => r.Id).ToArray());
    }

    [TestMethod]
    public async Task ValidateStreamAsync_EmitsUnknownRelationTypeDiagnostic()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-a"));
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-b"));
        await fixture.RelationStore.SaveAsync(Relation("rel-unknown", "stable-a", "made_up", "stable-b"));

        var diagnostics = await CollectStreamDiagnosticsAsync(
            fixture.Service, "workspace-test", "collection-test", CancellationToken.None);

        Assert.IsTrue(diagnostics.Any(d => d.DiagnosticType == RelationGraphDiagnosticTypes.UnknownRelationType),
            "Streaming path should emit UnknownRelationType.");
    }

    [TestMethod]
    public async Task ValidateStreamAsync_EmitsBrokenTargetDiagnostic()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-a"));
        await fixture.RelationStore.SaveAsync(Relation("rel-broken", "stable-a", "references", "missing-target", withEvidence: true));

        var diagnostics = await CollectStreamDiagnosticsAsync(
            fixture.Service, "workspace-test", "collection-test", CancellationToken.None);

        Assert.IsTrue(diagnostics.Any(d => d.DiagnosticType == RelationGraphDiagnosticTypes.BrokenTarget),
            "Streaming path should emit BrokenTarget (item-index phase).");
    }

    [TestMethod]
    public async Task ValidateStreamAsync_EmitsMissingInverseDiagnostic()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-old", lifecycle: StableMemoryLifecycle.Superseded, supersededBy: "stable-new"));
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-new"));
        await fixture.RelationStore.SaveAsync(Relation("rel-super", "stable-old", ContextRelationTypes.SupersededBy, "stable-new", withEvidence: true));

        var diagnostics = await CollectStreamDiagnosticsAsync(
            fixture.Service, "workspace-test", "collection-test", CancellationToken.None);

        Assert.IsTrue(diagnostics.Any(d => d.DiagnosticType == RelationGraphDiagnosticTypes.MissingInverseRelation),
            "Streaming path should emit MissingInverseRelation.");
    }

    [TestMethod]
    public async Task ValidateStreamAsync_EmitsDuplicateRelationDiagnostic()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-a"));
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-b"));
        await fixture.RelationStore.SaveAsync(Relation("rel-dup-1", "stable-a", "references", "stable-b", withEvidence: true));
        await fixture.RelationStore.SaveAsync(Relation("rel-dup-2", "stable-a", "references", "stable-b", withEvidence: true));

        var diagnostics = await CollectStreamDiagnosticsAsync(
            fixture.Service, "workspace-test", "collection-test", CancellationToken.None);

        Assert.IsTrue(diagnostics.Any(d => d.DiagnosticType == RelationGraphDiagnosticTypes.DuplicateRelation),
            "Streaming path should emit DuplicateRelation (cross-relation phase).");
    }

    [TestMethod]
    public async Task ValidateStreamAsync_SupersedeCycle_EmitsCycleDiagnostic()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-a"));
        await fixture.MemoryStore.SaveAsync(StableMemory("stable-b"));
        // a -> b -> a 形成 supersede 环
        await fixture.RelationStore.SaveAsync(Relation("rel-cycle-1", "stable-a", ContextRelationTypes.SupersededBy, "stable-b", withEvidence: true));
        await fixture.RelationStore.SaveAsync(Relation("rel-cycle-2", "stable-b", ContextRelationTypes.SupersededBy, "stable-a", withEvidence: true));

        var diagnostics = await CollectStreamDiagnosticsAsync(
            fixture.Service, "workspace-test", "collection-test", CancellationToken.None);

        Assert.IsTrue(diagnostics.Any(d => d.DiagnosticType == RelationGraphDiagnosticTypes.SupersedeCycle),
            "Streaming path should emit SupersedeCycle (DFS phase 4).");
    }

    [TestMethod]
    public async Task ValidateStreamAsync_FallbackPath_WhenStoreNotStreamable()
    {
        // NonStreamableRelationStore 仅实现 IRelationStore（不实现 IRelationStreamStore），触发 fallback 路径
        var inner = new InMemoryRelationStore();
        var memoryStore = new InMemoryMemoryStore();
        var store = new NonStreamableRelationStore(inner);
        var registry = new RelationTypeRegistry();
        var service = new RelationGraphValidationService(
            store, null, memoryStore, new InMemoryConstraintStore(),
            new InMemoryGlobalContextStore(), registry, new RelationEvalBackfillPolicy());

        await memoryStore.SaveAsync(StableMemory("stable-a"));
        await memoryStore.SaveAsync(StableMemory("stable-b"));
        await store.SaveAsync(Relation("rel-unknown", "stable-a", "made_up", "stable-b"));

        var diagnostics = await CollectStreamDiagnosticsAsync(
            service, "workspace-test", "collection-test", CancellationToken.None);

        Assert.IsTrue(diagnostics.Any(d => d.DiagnosticType == RelationGraphDiagnosticTypes.UnknownRelationType),
            "Fallback path should yield diagnostics via ValidateAsync.");
    }

    [TestMethod]
    public async Task ValidateStreamAsync_CancelEnumerator_StopsEarly()
    {
        var fixture = CreateFixture();
        for (var i = 0; i < 10; i++)
        {
            await fixture.RelationStore.SaveAsync(Relation(
                $"rel-unknown-{i}", $"src-{i}", "made_up", $"tgt-{i}"));
        }

        using var cts = new CancellationTokenSource();
        var collected = new List<RelationGraphDiagnostic>();
        try
        {
            await foreach (var diag in fixture.Service.ValidateStreamAsync(
                "workspace-test", "collection-test", null, cts.Token)
                .ConfigureAwait(false))
            {
                collected.Add(diag);
                if (collected.Count == 2)
                {
                    await cts.CancelAsync();
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }

        Assert.IsTrue(collected.Count >= 2, "At least 2 diagnostics should be emitted before cancellation.");
        Assert.IsTrue(collected.Count < 20, "Cancellation should stop enumeration before all 10 relations are processed.");
    }

    [TestMethod]
    public async Task ValidateStreamAsync_EmptyGraph_ProducesNoDiagnostics()
    {
        var fixture = CreateFixture();

        var diagnostics = await CollectStreamDiagnosticsAsync(
            fixture.Service, "workspace-test", "collection-test", CancellationToken.None);

        Assert.AreEqual(0, diagnostics.Count, "Empty graph should not produce diagnostics.");
    }

    private static async Task<List<RelationGraphDiagnostic>> CollectStreamDiagnosticsAsync(
        RelationGraphValidationService service,
        string workspaceId,
        string? collectionId,
        CancellationToken cancellationToken)
    {
        var list = new List<RelationGraphDiagnostic>();
        await foreach (var diag in service.ValidateStreamAsync(workspaceId, collectionId, null, cancellationToken)
            .ConfigureAwait(false))
        {
            list.Add(diag);
        }
        return list;
    }

    private static RelationGraphFixture CreateFixture()
    {
        var relationStore = new InMemoryRelationStore();
        var memoryStore = new InMemoryMemoryStore();
        var constraintStore = new InMemoryConstraintStore();
        var globalStore = new InMemoryGlobalContextStore();
        var registry = new RelationTypeRegistry();
        var service = new RelationGraphValidationService(
            relationStore, null, memoryStore, constraintStore, globalStore, registry,
            new RelationEvalBackfillPolicy());
        return new RelationGraphFixture(relationStore, memoryStore, service);
    }

    private static ContextMemoryItem StableMemory(
        string id,
        string lifecycle = StableMemoryLifecycle.Current,
        string? supersededBy = null)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["lifecycle"] = lifecycle,
            ["sourceStableReviewCandidateId"] = $"src-{id}",
            ["evidenceRefs"] = $"event-{id}"
        };
        if (!string.IsNullOrWhiteSpace(supersededBy))
        {
            metadata["supersededBy"] = supersededBy;
        }
        return new ContextMemoryItem
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Layer = ContextMemoryLayer.Stable,
            Status = string.Equals(lifecycle, StableMemoryLifecycle.Superseded, StringComparison.OrdinalIgnoreCase)
                ? ContextMemoryStatus.Deprecated
                : ContextMemoryStatus.Stable,
            Type = "preference",
            Content = id,
            SourceRefs = [$"event-{id}"],
            Metadata = metadata,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static ContextRelation Relation(
        string id,
        string sourceId,
        string relationType,
        string targetId,
        bool withEvidence = false)
    {
        return new ContextRelation
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = sourceId,
            TargetId = targetId,
            RelationType = relationType,
            Weight = 1.0,
            Confidence = 1.0,
            SourceRefs = withEvidence ? ["slr-test"] : Array.Empty<string>(),
            Metadata = withEvidence
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["source"] = "stable_lifecycle_review",
                    ["reviewId"] = "slr-test",
                    ["lifecycle"] = StableMemoryLifecycle.Active,
                    ["reviewStatus"] = "Reviewed",
                    ["confidenceReason"] = "stable_lifecycle_review",
                    ["evidenceRefs"] = "slr-test",
                    ["sourceRefs"] = "slr-test"
                }
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// 仅实现 IRelationStore（不实现 IRelationStreamStore），用于触发 ValidateStreamAsync 的 fallback 路径。
    /// 内部委托给 InMemoryRelationStore，保证数据语义正确。
    /// </summary>
    private sealed class NonStreamableRelationStore : IRelationStore
    {
        private readonly InMemoryRelationStore _inner;

        public NonStreamableRelationStore(InMemoryRelationStore inner) => _inner = inner;

        public Task SaveAsync(ContextRelation relation, CancellationToken cancellationToken = default)
            => _inner.SaveAsync(relation, cancellationToken);

        public Task<ContextRelation?> GetAsync(string workspaceId, string collectionId, string relationId, CancellationToken cancellationToken = default)
            => _inner.GetAsync(workspaceId, collectionId, relationId, cancellationToken);

        public Task<bool> DeleteAsync(string workspaceId, string collectionId, string relationId, CancellationToken cancellationToken = default)
            => _inner.DeleteAsync(workspaceId, collectionId, relationId, cancellationToken);

        public Task BatchUpsertAsync(IEnumerable<ContextRelation> relations, CancellationToken cancellationToken = default)
            => _inner.BatchUpsertAsync(relations, cancellationToken);

        public Task<IReadOnlyList<ContextRelation>> QueryAsync(ContextRelationQuery query, CancellationToken cancellationToken = default)
            => _inner.QueryAsync(query, cancellationToken);

        public Task<IReadOnlyList<RelationNeighborBatchResult>> QueryNeighborsBatchAsync(RelationNeighborBatchQuery query, CancellationToken cancellationToken = default)
            => _inner.QueryNeighborsBatchAsync(query, cancellationToken);

        public Task<IReadOnlyList<ContextRelation>> QueryNeighborsAsync(RelationNeighborQuery query, CancellationToken cancellationToken = default)
            => _inner.QueryNeighborsAsync(query, cancellationToken);
    }

    private sealed record RelationGraphFixture(
        InMemoryRelationStore RelationStore,
        InMemoryMemoryStore MemoryStore,
        RelationGraphValidationService Service);
}
