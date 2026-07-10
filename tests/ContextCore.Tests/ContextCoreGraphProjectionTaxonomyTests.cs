using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.Graph;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// GRAPH-09：投影与 taxonomy 闭环测试。
/// 验证所有 projector 输出通过 registry/validation 零 High 级诊断，以及 Ingest reconcile 语义。
/// </summary>
[TestClass]
[TestCategory("Relation")]
[TestCategory("GRAPH-09")]
public sealed class ContextCoreGraphProjectionTaxonomyTests
{
    private static readonly RelationTypeRegistry Registry = new();
    private static readonly RelationTypeNormalizer Normalizer = new();
    private static readonly RelationProjectorOutputValidator Validator = new(Registry, Normalizer);
    private static readonly RelationProjector Projector = new();

    // ──────────────────────────────────────────────────────────
    // 1. Projector 输出零 High 级诊断
    // ──────────────────────────────────────────────────────────

    /// <summary>ProjectForIngest 输出必须通过验证器（零 High 诊断）。</summary>
    [TestMethod]
    public void ProjectForIngest_PassesValidation_ZeroHighDiagnostics()
    {
        var item = CreateContextItem("item-a", refs: ["item-b", "item-c"]);

        var relations = Projector.ProjectForIngest(item);

        Assert.IsTrue(relations.Count > 0, "Should produce related_to edges for refs");
        var diagnostics = Validator.Validate(relations, "ingest");
        var highDiagnostics = diagnostics.Where(d => d.Severity == "High").ToArray();
        Assert.AreEqual(0, highDiagnostics.Length,
            $"ProjectForIngest should produce zero High diagnostics. Found: {string.Join("; ", highDiagnostics.Select(d => d.Message))}");
    }

    /// <summary>ProjectForCompression 输出必须通过验证器（零 High 诊断）。</summary>
    [TestMethod]
    public void ProjectForCompression_PassesValidation_ZeroHighDiagnostics()
    {
        var response = new CompressionResponse
        {
            OperationId = "op-compress-1",
            Status = CompressionStatus.Succeeded,
            GeneratedItems =
            [
                CreateContextItem("gen-summary-1", type: "summary", refs: ["src-1", "src-2"])
            ]
        };

        var relations = Projector.ProjectForCompression(response);

        Assert.IsTrue(relations.Count > 0, "Should produce derived_from/summarizes/generated_by edges");
        var diagnostics = Validator.Validate(relations, "compression");
        var highDiagnostics = diagnostics.Where(d => d.Severity == "High").ToArray();
        Assert.AreEqual(0, highDiagnostics.Length,
            $"ProjectForCompression should produce zero High diagnostics. Found: {string.Join("; ", highDiagnostics.Select(d => d.Message))}");
    }

    /// <summary>ProjectForPromotion 输出必须通过验证器（零 High 诊断），包括 CandidateMemory source。</summary>
    [TestMethod]
    public void ProjectForPromotion_PassesValidation_ZeroHighDiagnostics()
    {
        var candidate = new ShortTermPromotionCandidate
        {
            CandidateId = "cand-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceWorkingItemId = "working-item-1",
            Kind = "RecentDecision",
            Title = "测试候选",
            Summary = "测试",
            SuggestedTargetLayer = "CandidateMemory",
            Confidence = 0.9,
            Importance = 0.85,
            EvidenceRefs = ["evidence-1", "evidence-2"]
        };

        var relations = Projector.ProjectForPromotion(candidate, "mem:stp:cand-1", "memory", DateTimeOffset.UtcNow);

        Assert.IsTrue(relations.Count > 0, "Should produce promoted_from/derived_from/evidence_for edges");
        var diagnostics = Validator.Validate(relations, "promotion");
        var highDiagnostics = diagnostics.Where(d => d.Severity == "High").ToArray();
        Assert.AreEqual(0, highDiagnostics.Length,
            $"ProjectForPromotion should produce zero High diagnostics. Found: {string.Join("; ", highDiagnostics.Select(d => d.Message))}");
    }

    /// <summary>ProjectForPromotion constraint 路径也必须通过验证器。</summary>
    [TestMethod]
    public void ProjectForPromotion_ConstraintPath_PassesValidation()
    {
        var candidate = new ShortTermPromotionCandidate
        {
            CandidateId = "cand-2",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceWorkingItemId = "working-item-2",
            Kind = "TemporaryConstraint",
            Title = "约束候选",
            Summary = "测试约束",
            SuggestedTargetLayer = "CandidateConstraint",
            Confidence = 0.9,
            Importance = 0.85,
            EvidenceRefs = ["evidence-3"]
        };

        var relations = Projector.ProjectForPromotion(candidate, "constraint:stp:cand-2", "constraint", DateTimeOffset.UtcNow);

        Assert.IsTrue(relations.Count > 0);
        var diagnostics = Validator.Validate(relations, "promotion");
        var highDiagnostics = diagnostics.Where(d => d.Severity == "High").ToArray();
        Assert.AreEqual(0, highDiagnostics.Length,
            $"ProjectForPromotion constraint path should produce zero High diagnostics. Found: {string.Join("; ", highDiagnostics.Select(d => d.Message))}");
    }

    /// <summary>ProjectForSupersede 输出必须通过验证器（零 High 诊断），包括 inverse pair。</summary>
    [TestMethod]
    public void ProjectForSupersede_PassesValidation_ZeroHighDiagnostics()
    {
        var request = new SupersedeProjectionRequest(
            WorkspaceId: "workspace-test",
            CollectionId: "collection-test",
            SourceId: "stable-old",
            ReplacementId: "stable-new",
            SourceStableKind: StableMemoryKinds.StableMemory,
            ReplacementStableKind: StableMemoryKinds.StableMemory,
            ReviewId: "review-1",
            OperationId: "op-1",
            Reviewer: "tester",
            Reason: "测试替换",
            SourceRefs: ["src-1"],
            EvidenceRefs: ["evidence-1"],
            RequestMetadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Now: DateTimeOffset.UtcNow);

        var relations = Projector.ProjectForSupersede(request);

        Assert.AreEqual(2, relations.Count, "Should produce superseded_by + replaces inverse pair");
        var diagnostics = Validator.Validate(relations, "lifecycle-review");
        var highDiagnostics = diagnostics.Where(d => d.Severity == "High").ToArray();
        Assert.AreEqual(0, highDiagnostics.Length,
            $"ProjectForSupersede should produce zero High diagnostics. Found: {string.Join("; ", highDiagnostics.Select(d => d.Message))}");
    }

    // ──────────────────────────────────────────────────────────
    // 2. Registry taxonomy 闭环
    // ──────────────────────────────────────────────────────────

    /// <summary>PromotedFrom 必须允许 CandidateMemory/CandidateConstraint 作为 source。</summary>
    [TestMethod]
    public void Registry_PromotedFrom_AllowsCandidateSourceKinds()
    {
        var definition = Registry.Find(ContextRelationTypes.PromotedFrom);

        Assert.IsNotNull(definition);
        Assert.IsTrue(definition!.AllowedSourceKinds.Contains(nameof(GraphNodeKind.CandidateMemory)),
            "PromotedFrom must allow CandidateMemory as source (short-term promotion path)");
        Assert.IsTrue(definition.AllowedSourceKinds.Contains(nameof(GraphNodeKind.CandidateConstraint)),
            "PromotedFrom must allow CandidateConstraint as source (short-term promotion path)");
        Assert.IsTrue(definition.AllowedSourceKinds.Contains(nameof(GraphNodeKind.StableMemory)),
            "PromotedFrom must still allow StableMemory as source (stable promotion path)");
    }

    /// <summary>验证器应捕获 UnknownRelationType High 诊断。</summary>
    [TestMethod]
    public void Validator_DetectsUnknownRelationType_High()
    {
        var relations = new[]
        {
            new ContextRelation
            {
                Id = "rel-bad",
                WorkspaceId = "ws",
                CollectionId = "col",
                SourceId = "a",
                TargetId = "b",
                RelationType = "totally_made_up_type",
                SourceNodeKind = nameof(GraphNodeKind.ContextItem),
                TargetNodeKind = nameof(GraphNodeKind.ContextItem),
                CreatedAt = DateTimeOffset.UtcNow
            }
        };

        var diagnostics = Validator.Validate(relations, "test");
        Assert.IsTrue(diagnostics.Any(d => d.Severity == "High" && d.DiagnosticType == RelationGraphDiagnosticTypes.UnknownRelationType));
    }

    /// <summary>验证器应捕获 InvalidSourceKind High 诊断。</summary>
    [TestMethod]
    public void Validator_DetectsInvalidSourceKind_High()
    {
        var relations = new[]
        {
            new ContextRelation
            {
                Id = "rel-bad-kind",
                WorkspaceId = "ws",
                CollectionId = "col",
                SourceId = "a",
                TargetId = "b",
                RelationType = ContextRelationTypes.GeneratedBy,
                SourceNodeKind = nameof(GraphNodeKind.ContextItem),
                TargetNodeKind = nameof(GraphNodeKind.ContextItem), // should be Operation
                CreatedAt = DateTimeOffset.UtcNow
            }
        };

        var diagnostics = Validator.Validate(relations, "test");
        Assert.IsTrue(diagnostics.Any(d => d.Severity == "High" && d.DiagnosticType == RelationGraphDiagnosticTypes.InvalidTargetKind));
    }

    // ──────────────────────────────────────────────────────────
    // 3. Ingest reconcile — 新增需要的边，删除已经移除的 refs 边
    // ──────────────────────────────────────────────────────────

    /// <summary>Ingest reconcile：Refs 变化后，旧 refs 对应的 related_to 边应被删除。</summary>
    [TestMethod]
    public async Task IngestReconcile_RemovesStaleRelatedToEdges()
    {
        var contextStore = new InMemoryContextStore();
        var relationStore = new InMemoryRelationStore();
        var projector = new RelationProjector();
        var service = new BasicContextIngestionService(contextStore, projector, relationStore);

        // 第一次 ingest：refs = [B, C]
        await service.IngestAsync(CreateContextItem("item-a", refs: ["item-b", "item-c"]));

        var afterFirst = await relationStore.QueryAsync(new ContextRelationQuery { WorkspaceId = "workspace-test", CollectionId = "collection-test", SourceId = "item-a", Take = int.MaxValue });
        Assert.AreEqual(2, afterFirst.Count, "Should have 2 related_to edges after first ingest");

        // 第二次 ingest：refs = [B, D] — C 被移除，D 被新增
        await service.IngestAsync(CreateContextItem("item-a", refs: ["item-b", "item-d"]));

        var afterSecond = await relationStore.QueryAsync(new ContextRelationQuery { WorkspaceId = "workspace-test", CollectionId = "collection-test", SourceId = "item-a", Take = int.MaxValue });
        var targetIds = afterSecond.Select(r => r.TargetId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.AreEqual(2, afterSecond.Count, "Should still have 2 related_to edges");
        Assert.IsTrue(targetIds.Contains("item-b"), "Edge to item-b should remain");
        Assert.IsTrue(targetIds.Contains("item-d"), "Edge to item-d should be created");
        Assert.IsFalse(targetIds.Contains("item-c"), "Stale edge to item-c should be deleted");
    }

    /// <summary>Ingest reconcile：Refs 清空后，所有 related_to 边应被删除。</summary>
    [TestMethod]
    public async Task IngestReconcile_RemovesAllEdgesWhenRefsCleared()
    {
        var contextStore = new InMemoryContextStore();
        var relationStore = new InMemoryRelationStore();
        var projector = new RelationProjector();
        var service = new BasicContextIngestionService(contextStore, projector, relationStore);

        await service.IngestAsync(CreateContextItem("item-x", refs: ["item-y", "item-z"]));
        Assert.AreEqual(2, (await relationStore.QueryAsync(new ContextRelationQuery { WorkspaceId = "workspace-test", CollectionId = "collection-test", SourceId = "item-x", Take = int.MaxValue })).Count);

        // 清空 refs
        await service.IngestAsync(CreateContextItem("item-x", refs: []));

        var remaining = await relationStore.QueryAsync(new ContextRelationQuery { WorkspaceId = "workspace-test", CollectionId = "collection-test", SourceId = "item-x", Take = int.MaxValue });
        Assert.AreEqual(0, remaining.Count, "All related_to edges should be deleted when refs cleared");
    }

    /// <summary>Ingest reconcile：不影响其他 projector 生产的非 related_to 边。</summary>
    [TestMethod]
    public async Task IngestReconcile_DoesNotAffectNonRelatedToEdges()
    {
        var contextStore = new InMemoryContextStore();
        var relationStore = new InMemoryRelationStore();
        var projector = new RelationProjector();
        var service = new BasicContextIngestionService(contextStore, projector, relationStore);

        // 先 ingest 建立 related_to 边
        await service.IngestAsync(CreateContextItem("item-a", refs: ["item-b"]));

        // 手动添加一条 derived_from 边（模拟 compression projector 产出）
        await relationStore.SaveAsync(new ContextRelation
        {
            Id = "rel-derived-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = "item-a",
            TargetId = "item-source",
            RelationType = ContextRelationTypes.DerivedFrom,
            Weight = 1.0,
            Confidence = 1.0,
            SourceNodeKind = nameof(GraphNodeKind.ContextItem),
            TargetNodeKind = nameof(GraphNodeKind.ContextItem),
            Lifecycle = StableMemoryLifecycle.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Provenance = "compression"
        });

        // 重新 ingest，refs 不变
        await service.IngestAsync(CreateContextItem("item-a", refs: ["item-b"]));

        // related_to 边应保留
        var relatedTo = await relationStore.QueryAsync(new ContextRelationQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = "item-a",
            RelationType = ContextRelationTypes.RelatedTo,
            Take = int.MaxValue
        });
        Assert.AreEqual(1, relatedTo.Count, "related_to edge should remain");

        // derived_from 边不应被删除
        var derivedFrom = await relationStore.QueryAsync(new ContextRelationQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = "item-a",
            RelationType = ContextRelationTypes.DerivedFrom,
            Take = int.MaxValue
        });
        Assert.AreEqual(1, derivedFrom.Count, "derived_from edge should not be affected by ingest reconcile");
    }

    // ──────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────

    private static ContextItem CreateContextItem(string id, string type = "note", string[]? refs = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new ContextItem
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = type,
            Title = $"Item {id}",
            Content = $"Content for {id}",
            Refs = refs ?? [],
            Importance = 0.8,
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
