using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// Learning Artifact Plane / Decision Evidence Plane 测试（P1-十五 三层 Evidence 架构）：
/// 1. ILearningArtifactStore：数据集快照工件持久化（点查 / 最近列表 / 幂等覆盖）；
/// 2. IDecisionTraceStore.GetAsync：决策记录稳定主键点查（Durable Point Lookup，
///    不受"最近 N 条"窗口限制）。
/// </summary>
[TestClass]
[TestCategory("Learning-Event")]
public sealed class LearningArtifactPlaneTests
{
    private const string Ws = "ws-artifact";

    private static DatasetSnapshotArtifact BuildArtifact(string snapshotId, string modelArtifactId = "model-1") => new()
    {
        Snapshot = new DatasetSnapshotReport
        {
            SnapshotId = snapshotId,
            SchemaVersion = "training-data-export/v1",
            CreatedAt = DateTimeOffset.UtcNow,
            WorkspaceId = Ws,
            CollectionId = null,
            ModelArtifactId = modelArtifactId,
            InputEvidenceCount = 10,
            MaterializedCount = 8,
            CompletenessRatio = 0.8,
            MissingCount = 2,
            MissingReasons = new[] { "below-threshold" },
            ContentHash = "abc123",
            PolicyVersions = new[] { "policy/v1" },
            LineageDecisionCount = 4
        },
        DataFilePath = "/tmp/training-data.jsonl",
        ManifestFilePath = "/tmp/training-data.manifest.json",
        StoredAt = DateTimeOffset.UtcNow
    };

    [TestMethod]
    public async Task ArtifactStore_SaveGet_RoundtripsBySnapshotId()
    {
        var store = new InMemoryLearningArtifactStore();
        var artifact = BuildArtifact("snapshot-abc");

        await store.SaveAsync(artifact);

        var fetched = await store.GetAsync(Ws, "snapshot-abc");
        Assert.IsNotNull(fetched, "保存后应按快照 ID 点查命中。");
        Assert.AreEqual("snapshot-abc", fetched!.Snapshot.SnapshotId);
        Assert.AreEqual(0.8, fetched.Snapshot.CompletenessRatio!.Value, 0.0001, "完整率应保留。");
        Assert.AreEqual("abc123", fetched.Snapshot.ContentHash, "内容哈希应保留。");
        CollectionAssert.Contains(fetched.Snapshot.MissingReasons.ToList(), "below-threshold",
            "缺失原因应保留。");
        Assert.AreEqual(4, fetched.Snapshot.LineageDecisionCount, "血缘决策数应保留。");
        Assert.AreEqual("/tmp/training-data.jsonl", fetched.DataFilePath, "物化文件路径应保留。");
    }

    [TestMethod]
    public async Task ArtifactStore_GetUnknown_ReturnsNull()
    {
        var store = new InMemoryLearningArtifactStore();
        Assert.IsNull(await store.GetAsync(Ws, "snapshot-missing"), "未知快照 ID 返回 null。");
        Assert.IsNull(await store.GetAsync("ws-other", "snapshot-abc"), "跨工作区不可见（隔离）。");
    }

    [TestMethod]
    public async Task ArtifactStore_SaveSameSnapshotId_IsIdempotentOverwrite()
    {
        var store = new InMemoryLearningArtifactStore();
        await store.SaveAsync(BuildArtifact("snapshot-same", modelArtifactId: "model-v1"));
        await store.SaveAsync(BuildArtifact("snapshot-same", modelArtifactId: "model-v2"));

        var fetched = await store.GetAsync(Ws, "snapshot-same");
        Assert.AreEqual("model-v2", fetched!.Snapshot.ModelArtifactId, "同 (ws, snapshotId) 幂等覆盖。");
        var all = await store.ListRecentAsync(Ws);
        Assert.AreEqual(1, all.Count, "同快照 ID 只保留一条。");
    }

    [TestMethod]
    public async Task ArtifactStore_ListRecent_OrdersByStoredAtDescending()
    {
        var store = new InMemoryLearningArtifactStore();
        await store.SaveAsync(BuildArtifact("snapshot-a"));
        await store.SaveAsync(BuildArtifact("snapshot-b"));
        await store.SaveAsync(BuildArtifact("snapshot-c"));

        var all = await store.ListRecentAsync(Ws);
        Assert.AreEqual(3, all.Count);
        Assert.AreEqual("snapshot-c", all[0].Snapshot.SnapshotId, "最新入库在前。");
    }

    [TestMethod]
    public async Task DecisionTraceStore_GetAsync_PointLookupBeyondRecentWindow()
    {
        // Decision Evidence Plane：稳定主键点查——目标决策在"最近 N 条"窗口外仍可查证。
        var store = new InMemoryDecisionTraceStore();
        await store.SaveAsync(BuildDecision("decision-old", createdDaysAgo: 30));

        for (var i = 0; i < 101; i++)
        {
            await store.SaveAsync(BuildDecision($"decision-later-{i}", createdDaysAgo: 0));
        }

        var fetched = await store.GetAsync(Ws, "col-artifact", "decision-old");
        Assert.IsNotNull(fetched, "窗口外的决策记录仍可按稳定主键点查。");
        Assert.AreEqual("decision-old", fetched!.DecisionId);
        Assert.IsNull(await store.GetAsync(Ws, "col-artifact", "decision-missing"), "未知决策返回 null。");
    }

    private static ContextDecisionRecord BuildDecision(string decisionId, int createdDaysAgo) => new()
    {
        DecisionId = decisionId,
        Source = ContextDecisionSource.Retrieval,
        WorkspaceId = Ws,
        CollectionId = "col-artifact",
        QueryText = "test",
        Candidates = Array.Empty<ContextDecisionCandidate>(),
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-createdDaysAgo),
        PolicyVersion = "policy/v1"
    };
}
