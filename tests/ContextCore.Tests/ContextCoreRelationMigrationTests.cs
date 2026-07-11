using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Services;
using ContextCore.Core.Services.Graph;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// P3.1-e：关系迁移测试 — 验证 collection 范围、dry-run、幂等性和批量节点加载。
/// </summary>
[TestClass]
public class ContextCoreRelationMigrationTests
{
    private const string Workspace = "ws-migration-test";

    [TestMethod]
    public async Task DryRun_DoesNotWriteChanges()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(StableMemory("col-a", "item-a"));
        await fixture.MemoryStore.SaveAsync(StableMemory("col-a", "item-b"));
        await fixture.RelationStore.SaveAsync(LegacyRelation("rel-1", "col-a", "item-a", "item-b"));

        var report = await fixture.Service.MigrateRelationsAsync(new RelationMigrationOptions
        {
            CollectionId = "col-a",
            Apply = false
        });

        Assert.IsTrue(report.DryRun, "dry-run 报告应标记 DryRun=true");
        Assert.IsTrue(report.UpdatedRelations > 0, "dry-run 仍应统计待更新关系数");
        Assert.IsTrue(report.NodeKindBackfilled > 0, "应检测到 NodeKind 待回填");

        // 验证存储中关系未被修改：正式字段仍为空/默认
        var stored = await fixture.RelationStore.GetAsync(Workspace, "col-a", "rel-1");
        Assert.IsNotNull(stored);
        Assert.AreEqual(string.Empty, stored!.SourceNodeKind, "dry-run 不应写入 SourceNodeKind");
        Assert.AreEqual(string.Empty, stored.TargetNodeKind, "dry-run 不应写入 TargetNodeKind");
        Assert.IsTrue(string.IsNullOrWhiteSpace(stored.ReviewStatus), "dry-run 不应写入 ReviewStatus");
    }

    [TestMethod]
    public async Task Apply_WritesBackfilledFields()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(StableMemory("col-a", "item-a"));
        await fixture.MemoryStore.SaveAsync(StableMemory("col-a", "item-b"));
        await fixture.RelationStore.SaveAsync(LegacyRelation("rel-1", "col-a", "item-a", "item-b"));

        var report = await fixture.Service.MigrateRelationsAsync(new RelationMigrationOptions
        {
            CollectionId = "col-a",
            Apply = true
        });

        Assert.IsFalse(report.DryRun, "apply 报告应标记 DryRun=false");
        Assert.AreEqual(1, report.UpdatedRelations);
        Assert.IsTrue(report.NodeKindBackfilled >= 2, "应回填 source + target 两个 NodeKind");
        Assert.IsTrue(report.LifecycleBackfilled > 0, "应从 Metadata 回填 Lifecycle");
        Assert.IsTrue(report.ReviewStatusBackfilled > 0, "应从 Metadata 回填 ReviewStatus");

        var stored = await fixture.RelationStore.GetAsync(Workspace, "col-a", "rel-1");
        Assert.IsNotNull(stored);
        Assert.AreEqual(nameof(GraphNodeKind.StableMemory), stored!.SourceNodeKind);
        Assert.AreEqual(nameof(GraphNodeKind.StableMemory), stored.TargetNodeKind);
        Assert.AreEqual(RelationLifecycles.Deprecated, stored.Lifecycle);
        Assert.AreEqual(RelationReviewStatuses.Rejected, stored.ReviewStatus);
        Assert.IsTrue(!string.IsNullOrWhiteSpace(stored.Provenance), "应回填 Provenance");
    }

    [TestMethod]
    public async Task CollectionScope_OnlyMigratesSpecifiedCollection()
    {
        var fixture = CreateFixture();
        // 两个 collection 各放一条 legacy 关系
        await fixture.MemoryStore.SaveAsync(StableMemory("col-a", "a-src"));
        await fixture.MemoryStore.SaveAsync(StableMemory("col-a", "a-tgt"));
        await fixture.MemoryStore.SaveAsync(StableMemory("col-b", "b-src"));
        await fixture.MemoryStore.SaveAsync(StableMemory("col-b", "b-tgt"));
        await fixture.RelationStore.SaveAsync(LegacyRelation("rel-a", "col-a", "a-src", "a-tgt"));
        await fixture.RelationStore.SaveAsync(LegacyRelation("rel-b", "col-b", "b-src", "b-tgt"));

        // 仅迁移 col-a
        var report = await fixture.Service.MigrateRelationsAsync(new RelationMigrationOptions
        {
            CollectionId = "col-a",
            Apply = true
        });

        Assert.AreEqual(1, report.TotalRelations, "应只扫描 col-a 的 1 条关系");
        Assert.AreEqual(1, report.UpdatedRelations);

        // col-a 关系已迁移
        var storedA = await fixture.RelationStore.GetAsync(Workspace, "col-a", "rel-a");
        Assert.IsNotNull(storedA);
        Assert.AreEqual(nameof(GraphNodeKind.StableMemory), storedA!.SourceNodeKind);

        // col-b 关系未被触碰（正式字段仍为空）
        var storedB = await fixture.RelationStore.GetAsync(Workspace, "col-b", "rel-b");
        Assert.IsNotNull(storedB);
        Assert.AreEqual(string.Empty, storedB!.SourceNodeKind, "col-b 关系不应被迁移");
        Assert.AreEqual(string.Empty, storedB.TargetNodeKind, "col-b 关系不应被迁移");
    }

    [TestMethod]
    public async Task Idempotent_SecondRunMakesNoChanges()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(StableMemory("col-a", "item-a"));
        await fixture.MemoryStore.SaveAsync(StableMemory("col-a", "item-b"));
        await fixture.RelationStore.SaveAsync(LegacyRelation("rel-1", "col-a", "item-a", "item-b"));

        // 第一次 apply：应有变更
        var first = await fixture.Service.MigrateRelationsAsync(new RelationMigrationOptions
        {
            CollectionId = "col-a",
            Apply = true
        });
        Assert.IsTrue(first.UpdatedRelations > 0, "首次迁移应写入变更");
        Assert.AreEqual(0, first.SkippedRelations, "首次迁移前无跳过");

        // 第二次 apply：应无变更（幂等）
        var second = await fixture.Service.MigrateRelationsAsync(new RelationMigrationOptions
        {
            CollectionId = "col-a",
            Apply = true
        });
        Assert.AreEqual(0, second.UpdatedRelations, "幂等：第二次迁移不应有变更");
        Assert.AreEqual(second.TotalRelations, second.SkippedRelations, "全部关系应已跳过");
        Assert.AreEqual(0, second.NodeKindBackfilled, "幂等：不应再次回填 NodeKind");
        Assert.AreEqual(0, second.LifecycleBackfilled, "幂等：不应再次回填 Lifecycle");
    }

    [TestMethod]
    public async Task BatchNodeLoading_InfersKindFromMemoryStore()
    {
        var fixture = CreateFixture();
        // 不同层级的 memory 应分类为不同 NodeKind
        await fixture.MemoryStore.SaveAsync(StableMemory("col-a", "stable-item"));
        await fixture.MemoryStore.SaveAsync(CandidateMemory("col-a", "candidate-item"));
        await fixture.RelationStore.SaveAsync(LegacyRelation("rel-stable", "col-a", "stable-item", "candidate-item"));

        var report = await fixture.Service.MigrateRelationsAsync(new RelationMigrationOptions
        {
            CollectionId = "col-a",
            Apply = true
        });

        Assert.AreEqual(1, report.UpdatedRelations);
        Assert.AreEqual(2, report.NodeKindBackfilled, "应批量推断 source + target");

        var stored = await fixture.RelationStore.GetAsync(Workspace, "col-a", "rel-stable");
        Assert.IsNotNull(stored);
        Assert.AreEqual(nameof(GraphNodeKind.StableMemory), stored!.SourceNodeKind, "Stable 层 memory 应推断为 StableMemory");
        Assert.AreEqual(nameof(GraphNodeKind.CandidateMemory), stored.TargetNodeKind, "Working 层 memory 应推断为 CandidateMemory");
    }

    [TestMethod]
    public async Task CrossCollection_AllCollectionsWhenScopeNull()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(StableMemory("col-a", "a-src"));
        await fixture.MemoryStore.SaveAsync(StableMemory("col-a", "a-tgt"));
        await fixture.MemoryStore.SaveAsync(StableMemory("col-b", "b-src"));
        await fixture.MemoryStore.SaveAsync(StableMemory("col-b", "b-tgt"));
        await fixture.RelationStore.SaveAsync(LegacyRelation("rel-a", "col-a", "a-src", "a-tgt"));
        await fixture.RelationStore.SaveAsync(LegacyRelation("rel-b", "col-b", "b-src", "b-tgt"));

        // CollectionId=null → 跨 collection 迁移工作空间内所有关系
        var report = await fixture.Service.MigrateRelationsAsync(new RelationMigrationOptions
        {
            CollectionId = null,
            Apply = true
        });

        Assert.AreEqual(2, report.TotalRelations, "应扫描两个 collection 的全部关系");
        Assert.AreEqual(2, report.UpdatedRelations, "两条关系都应被迁移");

        var storedA = await fixture.RelationStore.GetAsync(Workspace, "col-a", "rel-a");
        var storedB = await fixture.RelationStore.GetAsync(Workspace, "col-b", "rel-b");
        Assert.IsNotNull(storedA);
        Assert.IsNotNull(storedB);
        Assert.AreEqual(nameof(GraphNodeKind.StableMemory), storedA!.SourceNodeKind);
        Assert.AreEqual(nameof(GraphNodeKind.StableMemory), storedB!.SourceNodeKind);
    }

    private static ContextMemoryItem StableMemory(string collectionId, string id)
    {
        return new ContextMemoryItem
        {
            Id = id,
            WorkspaceId = Workspace,
            CollectionId = collectionId,
            Layer = ContextMemoryLayer.Stable,
            Status = ContextMemoryStatus.Stable,
            Type = "preference",
            Content = id,
            SourceRefs = [$"event-{id}"],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static ContextMemoryItem CandidateMemory(string collectionId, string id)
    {
        return new ContextMemoryItem
        {
            Id = id,
            WorkspaceId = Workspace,
            CollectionId = collectionId,
            Layer = ContextMemoryLayer.Working,
            Status = ContextMemoryStatus.Candidate,
            Type = "note",
            Content = id,
            SourceRefs = [$"event-{id}"],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>构造 legacy 关系：正式字段为空/默认，Metadata 携带 lifecycle/reviewStatus/source。</summary>
    private static ContextRelation LegacyRelation(string id, string collectionId, string sourceId, string targetId)
    {
        return new ContextRelation
        {
            Id = id,
            WorkspaceId = Workspace,
            CollectionId = collectionId,
            SourceId = sourceId,
            TargetId = targetId,
            RelationType = ContextRelationTypes.RelatedTo,
            Weight = 1.0,
            Confidence = 0.9,
            SourceRefs = [$"event-{sourceId}"],
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lifecycle"] = RelationLifecycles.Deprecated,
                ["reviewStatus"] = RelationReviewStatuses.Rejected,
                ["source"] = "compression"
            },
            CreatedAt = DateTimeOffset.UtcNow
            // 故意不设 SourceNodeKind/TargetNodeKind/Lifecycle/ReviewStatus/Provenance 正式字段，模拟旧数据
        };
    }

    private sealed record MigrationFixture(
        InMemoryRelationStore RelationStore,
        InMemoryMemoryStore MemoryStore,
        InMemoryContextStore ContextStore,
        ControlRoomService Service);

    private static MigrationFixture CreateFixture()
    {
        var relationStore = new InMemoryRelationStore();
        var memoryStore = new InMemoryMemoryStore();
        var contextStore = new InMemoryContextStore();

        var state = new ControlRoomState
        {
            WorkspaceId = Workspace,
            CollectionId = "col-a",
            ContextStore = contextStore,
            MemoryStore = memoryStore,
            RelationStore = relationStore
        };
        var service = new ControlRoomService(state);
        return new MigrationFixture(relationStore, memoryStore, contextStore, service);
    }
}
