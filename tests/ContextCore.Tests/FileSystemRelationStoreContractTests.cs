using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;

namespace ContextCore.Tests;

/// <summary>GRAPH-10：FileSystem provider 的 RelationStore contract 测试。</summary>
/// <remarks>
/// 每个测试创建独立的临时根目录，避免 JSONL 文件跨测试干扰；类清理时删除整个目录。
/// </remarks>
[TestClass]
[TestCategory("Storage")]
[TestCategory("Graph")]
public sealed class FileSystemRelationStoreContractTests : RelationStoreContractBase
{
    private string? _rootPath;

    protected override Task<IRelationStore> CreateStoreAsync(CancellationToken cancellationToken)
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "cc-graph10-fs-" + Guid.NewGuid().ToString("N"));
        var options = new FileStorageOptions { RootPath = _rootPath };
        IRelationStore store = new FileRelationStore(options);
        return Task.FromResult(store);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_rootPath is not null && Directory.Exists(_rootPath))
        {
            try { Directory.Delete(_rootPath, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// 验证 QueryNeighborsAsync 先排序再 Take(maxScan)，
    /// 文件后部的高权重关系也能进入结果（修复前会被提前 Take 丢弃）。
    /// </summary>
    [TestMethod]
    public async Task QueryNeighbors_HighWeightRelationAtFileEnd_IsIncludedInResults()
    {
        var store = await CreateStoreAsync(default);
        const string ws = "ws-sort-test";
        const string col = "col-sort-test";
        const string itemId = "item-center";

        // 创建 20 条低权重关系（排在文件前部）
        var lowWeightRelations = Enumerable.Range(0, 20)
            .Select(i => new ContextRelation
            {
                Id = $"rel-low-{i}",
                WorkspaceId = ws,
                CollectionId = col,
                SourceId = itemId,
                TargetId = $"target-low-{i}",
                RelationType = "depends-on",
                Weight = 0.1,
                Confidence = 0.5,
                CreatedAt = DateTimeOffset.UtcNow
            });

        // 创建 1 条高权重关系（排在文件后部，第 21 条）
        var highWeightRelation = new ContextRelation
        {
            Id = "rel-high-tail",
            WorkspaceId = ws,
            CollectionId = col,
            SourceId = itemId,
            TargetId = "target-high-tail",
            RelationType = "depends-on",
            Weight = 0.99,
            Confidence = 0.95,
            CreatedAt = DateTimeOffset.UtcNow
        };

        // 按低权重在前、高权重在后的顺序写入（模拟文件后部高权重）
        var allRelations = lowWeightRelations.Concat(new[] { highWeightRelation }).ToArray();
        await store.BatchUpsertAsync(allRelations);

        // maxScan=10 < 21 条总数，修复前高权重关系（第 21 条）会被提前 Take 丢弃
        var results = await store.QueryNeighborsAsync(new RelationNeighborQuery
        {
            WorkspaceId = ws,
            CollectionId = col,
            ItemId = itemId,
            Direction = RelationDirection.Outgoing,
            Take = 5,
            MaxScan = 10
        });

        // 修复后：高权重关系应排在第一位
        Assert.IsTrue(results.Count > 0);
        Assert.AreEqual("rel-high-tail", results[0].Id);
        Assert.AreEqual(0.99, results[0].Weight);
    }

    /// <summary>
    /// 验证排序后的前 N 条确实是权重最高的 N 条。
    /// </summary>
    [TestMethod]
    public async Task QueryNeighbors_ReturnsTopWeightedRelations()
    {
        var store = await CreateStoreAsync(default);
        const string ws = "ws-top-weight";
        const string col = "col-top-weight";
        const string itemId = "item-top";

        // 创建 50 条关系，权重从 0.01 到 0.50，随机顺序写入
        var relations = Enumerable.Range(0, 50)
            .Select(i => new ContextRelation
            {
                Id = $"rel-{i:D2}",
                WorkspaceId = ws,
                CollectionId = col,
                SourceId = itemId,
                TargetId = $"target-{i:D2}",
                RelationType = "depends-on",
                Weight = (i + 1) * 0.01,
                Confidence = 0.5,
                CreatedAt = DateTimeOffset.UtcNow
            })
            .OrderBy(r => Guid.NewGuid()) // 随机顺序写入
            .ToArray();

        await store.BatchUpsertAsync(relations);

        var results = await store.QueryNeighborsAsync(new RelationNeighborQuery
        {
            WorkspaceId = ws,
            CollectionId = col,
            ItemId = itemId,
            Direction = RelationDirection.Outgoing,
            Take = 5,
            MaxScan = 100
        });

        Assert.AreEqual(5, results.Count);
        // 最高权重应是 0.50（rel-49）；浮点运算 (i+1)*0.01 有精度误差，使用容差比较。
        const double tol = 1e-9;
        Assert.AreEqual(0.50, results[0].Weight, tol);
        Assert.AreEqual(0.49, results[1].Weight, tol);
        Assert.AreEqual(0.48, results[2].Weight, tol);
        Assert.AreEqual(0.47, results[3].Weight, tol);
        Assert.AreEqual(0.46, results[4].Weight, tol);
    }
}
