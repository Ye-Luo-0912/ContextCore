using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;

namespace ContextCore.Tests;

/// <summary>
/// Graph 全局预算 Seed 公平性契约测试：
/// 1) 每种子最低配额：早期富种子不能耗尽全部预算，后续种子始终有结果；
/// 2) SkippedByGlobalBudget：仅当 GlobalEdgeLimit &lt; 种子数 时出现；
/// 3) 诊断字段：SeedOrdinal 升序、ScannedCount、CandidateCountBeforeGlobalLimit；
/// 4) 空种子也返回诊断行（每个种子都有结果）。
/// 同一套断言在 InMemory / FileSystem 两个 provider 上运行。
/// </summary>
[TestClass]
[TestCategory("Storage")]
[TestCategory("Graph")]
public class R29J_GraphSeedFairnessTests
{
    private const string WorkspaceId = "ws-seed-fair";
    private static readonly DateTimeOffset BaseTime =
        new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);

    private static async Task RunAcrossRelationStoresAsync(Func<IRelationStore, Task> test)
    {
        await test(new InMemoryRelationStore());

        var root = Path.Combine(Path.GetTempPath(), "ctx-graph-fair-" + Guid.NewGuid().ToString("N"));
        try
        {
            await test(new FileRelationStore(new FileStorageOptions { RootPath = root }));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ContextRelation MakeRelation(string id, string sourceId, string targetId, double weight = 1.0)
    {
        return new ContextRelation
        {
            Id = id,
            WorkspaceId = WorkspaceId,
            CollectionId = "col",
            SourceId = sourceId,
            TargetId = targetId,
            RelationType = "related_to",
            Confidence = 0.9,
            Weight = weight,
            Lifecycle = RelationLifecycles.Active,
            ReviewStatus = string.Empty,
            CreatedAt = BaseTime,
            UpdatedAt = BaseTime
        };
    }

    /// <summary>
    /// 核心公平性回归：seed0 是富种子（100 条候选），seed1-4 各 10 条，
    /// 全局上限 25。每种子最低配额 floor(25/5)=5 → 每个种子都交付 5 条。
    /// 旧实现下 seed0 会耗尽全部 25 条预算，seed1-4 完全无结果；新实现必须全部有结果。
    /// </summary>
    [TestMethod]
    public async Task FairShare_RichEarlySeed_DoesNotStarveLaterSeeds()
    {
        var relations = new List<ContextRelation>();
        for (var i = 0; i < 100; i++)
        {
            relations.Add(MakeRelation($"r-s0-{i:D3}", "seed0", $"n-s0-{i:D3}", weight: 1000 - i));
        }
        for (var seed = 1; seed <= 4; seed++)
        {
            for (var i = 0; i < 10; i++)
            {
                relations.Add(MakeRelation($"r-s{seed}-{i}", $"seed{seed}", $"n-s{seed}-{i}", weight: 10 - i));
            }
        }

        await RunAcrossRelationStoresAsync(async store =>
        {
            await store.BatchUpsertAsync(relations, CancellationToken.None);
            var batch = await store.QueryNeighborsBatchAsync(new RelationNeighborBatchQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemIds = ["seed0", "seed1", "seed2", "seed3", "seed4"],
                Direction = RelationDirection.Outgoing,
                Take = 100,
                GlobalEdgeLimit = 25
            }, CancellationToken.None);

            Assert.AreEqual(5, batch.Count, "每个种子都应有结果（公平配额，后续种子不再饿死）");
            for (var ordinal = 0; ordinal < 5; ordinal++)
            {
                Assert.AreEqual(ordinal, batch[ordinal].SeedOrdinal, "结果应按 SeedOrdinal 升序返回");
                Assert.AreEqual($"seed{ordinal}", batch[ordinal].ItemId, "种子序应与输入一致");
                Assert.AreEqual(5, batch[ordinal].Relations.Count,
                    $"种子 seed{ordinal} 应恰好交付最低配额 5 条");
                Assert.IsTrue(batch[ordinal].Truncated,
                    $"种子 seed{ordinal} 候选数超过交付数，应标记 Truncated");
                Assert.IsFalse(batch[ordinal].SkippedByGlobalBudget,
                    "GlobalEdgeLimit(25) ≥ 种子数(5)，不应有种子被跳过");
                Assert.AreEqual(5, batch[ordinal].ScannedCount,
                    $"种子 seed{ordinal} 应恰好扫描 5 条候选");
            }

            Assert.AreEqual(100, batch[0].CandidateCountBeforeGlobalLimit, "seed0 全局截断前候选数 = 100");
            Assert.AreEqual(10, batch[1].CandidateCountBeforeGlobalLimit, "seed1 全局截断前候选数 = 10");
            Assert.AreEqual(25, batch.Sum(b => b.Relations.Count), "全局返回边数不得超过 GlobalEdgeLimit");
        });
    }

    /// <summary>
    /// SkippedByGlobalBudget 只在 GlobalEdgeLimit &lt; 种子数 时出现：
    /// 4 个种子各 2 条候选，全局上限 3 → floor(3/4)=1，前 3 个种子各 1 条，第 4 个种子被跳过
    /// （返回空结果 + SkippedByGlobalBudget=true + ScannedCount=0），其余种子不被标记跳过。
    /// </summary>
    [TestMethod]
    public async Task SkippedByGlobalBudget_OnlyWhenLimitBelowSeedCount()
    {
        var relations = new List<ContextRelation>();
        for (var seed = 0; seed < 4; seed++)
        {
            for (var i = 0; i < 2; i++)
            {
                relations.Add(MakeRelation($"r-s{seed}-{i}", $"seed{seed}", $"n-s{seed}-{i}", weight: 2 - i));
            }
        }

        await RunAcrossRelationStoresAsync(async store =>
        {
            await store.BatchUpsertAsync(relations, CancellationToken.None);
            var batch = await store.QueryNeighborsBatchAsync(new RelationNeighborBatchQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemIds = ["seed0", "seed1", "seed2", "seed3"],
                Direction = RelationDirection.Outgoing,
                Take = 100,
                GlobalEdgeLimit = 3
            }, CancellationToken.None);

            Assert.AreEqual(4, batch.Count, "每个种子都应返回结果（含被跳过的种子）");
            Assert.AreEqual(3, batch[3].SeedOrdinal, "第 4 个种子序号应为 3");

            for (var ordinal = 0; ordinal < 3; ordinal++)
            {
                Assert.AreEqual(1, batch[ordinal].Relations.Count, $"seed{ordinal} 应交付最低配额 1 条");
                Assert.IsFalse(batch[ordinal].SkippedByGlobalBudget, $"seed{ordinal} 不应被标记跳过");
                Assert.AreEqual(1, batch[ordinal].ScannedCount, $"seed{ordinal} 扫描 1 条");
            }

            Assert.AreEqual(0, batch[3].Relations.Count, "seed3 预算耗尽，无结果交付");
            Assert.IsTrue(batch[3].SkippedByGlobalBudget, "seed3 应在预算耗尽后标记 SkippedByGlobalBudget");
            Assert.AreEqual(0, batch[3].ScannedCount, "seed3 未被扫描，ScannedCount 应为 0");
            Assert.AreEqual(2, batch[3].CandidateCountBeforeGlobalLimit,
                "seed3 全局截断前候选数 = 2（物化存储可精确报告）");
            Assert.IsTrue(batch[3].Truncated, "seed3 有候选但未交付，应标记 Truncated");
            Assert.AreEqual(3, batch.Sum(b => b.Relations.Count), "全局返回边数不得超过 GlobalEdgeLimit");
        });
    }

    /// <summary>
    /// 空种子也返回诊断行：seedB 无任何候选，仍应出现在结果中
    /// （Relations 为空、SkippedByGlobalBudget=false、ScannedCount=0、不截断）。
    /// </summary>
    [TestMethod]
    public async Task EmptySeed_AlwaysEmitDiagnosticRow()
    {
        var relations = new List<ContextRelation>
        {
            MakeRelation("r-a-1", "seedA", "n-a-1", weight: 2.0),
            MakeRelation("r-a-2", "seedA", "n-a-2", weight: 1.0),
            MakeRelation("r-c-1", "seedC", "n-c-1", weight: 3.0),
            MakeRelation("r-c-2", "seedC", "n-c-2", weight: 2.0),
            MakeRelation("r-c-3", "seedC", "n-c-3", weight: 1.0)
        };

        await RunAcrossRelationStoresAsync(async store =>
        {
            await store.BatchUpsertAsync(relations, CancellationToken.None);
            var batch = await store.QueryNeighborsBatchAsync(new RelationNeighborBatchQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemIds = ["seedA", "seedB", "seedC"],
                Direction = RelationDirection.Outgoing,
                Take = 100
            }, CancellationToken.None);

            Assert.AreEqual(3, batch.Count, "空种子 seedB 也应返回诊断行");
            var seedB = batch.First(b => b.ItemId == "seedB");
            Assert.AreEqual(0, seedB.Relations.Count, "seedB 无候选，Relations 应为空");
            Assert.IsFalse(seedB.SkippedByGlobalBudget, "seedB 无候选，不应被标记跳过");
            Assert.IsFalse(seedB.Truncated, "seedB 无候选，不应标记截断");
            Assert.AreEqual(0, seedB.ScannedCount, "seedB 扫描 0 条");
            Assert.AreEqual(0, seedB.CandidateCountBeforeGlobalLimit, "seedB 全局截断前候选数 = 0");

            var seedA = batch.First(b => b.ItemId == "seedA");
            Assert.AreEqual(2, seedA.Relations.Count, "seedA 应全量返回 2 条");
            Assert.IsFalse(seedA.Truncated, "seedA 未触及任何上限，不应截断");

            var seedC = batch.First(b => b.ItemId == "seedC");
            Assert.AreEqual(3, seedC.Relations.Count, "seedC 应全量返回 3 条");
            Assert.IsFalse(seedC.Truncated, "seedC 未触及任何上限，不应截断");
        });
    }

    /// <summary>
    /// MaxScan 截断时诊断字段：seedC 有 3 条候选、MaxScan=2 → ScannedCount=2、
    /// CandidateCountBeforeGlobalLimit=2、Truncated=true，且只交付窗口内 2 条。
    /// </summary>
    [TestMethod]
    public async Task Diagnostics_MaxScanTruncation_ReportsScannedAndCandidates()
    {
        var relations = new List<ContextRelation>
        {
            MakeRelation("r-a-1", "seedA", "n-a-1", weight: 1.0),
            MakeRelation("r-c-1", "seedC", "n-c-1", weight: 3.0),
            MakeRelation("r-c-2", "seedC", "n-c-2", weight: 2.0),
            MakeRelation("r-c-3", "seedC", "n-c-3", weight: 1.0)
        };

        await RunAcrossRelationStoresAsync(async store =>
        {
            await store.BatchUpsertAsync(relations, CancellationToken.None);
            var batch = await store.QueryNeighborsBatchAsync(new RelationNeighborBatchQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemIds = ["seedA", "seedC"],
                Direction = RelationDirection.Outgoing,
                Take = 100,
                MaxScan = 2
            }, CancellationToken.None);

            Assert.AreEqual(2, batch.Count);
            var seedC = batch.First(b => b.ItemId == "seedC");
            Assert.AreEqual(2, seedC.Relations.Count, "seedC 只交付扫描窗口内 2 条");
            Assert.IsTrue(seedC.Truncated, "seedC 候选数(3) > MaxScan(2)，应标记截断");
            Assert.AreEqual(2, seedC.ScannedCount, "seedC 扫描 2 条");
            Assert.AreEqual(2, seedC.CandidateCountBeforeGlobalLimit, "seedC 全局截断前候选数 = 2（窗口上限）");

            var seedA = batch.First(b => b.ItemId == "seedA");
            Assert.AreEqual(1, seedA.Relations.Count);
            Assert.IsFalse(seedA.Truncated, "seedA 候选数(1) < MaxScan(2)，不应截断");
            Assert.AreEqual(1, seedA.ScannedCount);
            Assert.AreEqual(1, seedA.CandidateCountBeforeGlobalLimit);
        });
    }

    /// <summary>
    /// 种子去重 + 显式 SeedOrdinal 排序：重复种子只保留首次出现，
    /// 结果按去重后的种子序返回（SeedOrdinal 0..n-1）。
    /// </summary>
    [TestMethod]
    public async Task SeedOrdinal_DedupedSeeds_KeepInputOrder()
    {
        var relations = new List<ContextRelation>
        {
            MakeRelation("r-z-1", "seedZ", "n-z-1", weight: 2.0),
            MakeRelation("r-a-1", "seedA", "n-a-1", weight: 2.0),
            MakeRelation("r-m-1", "seedM", "n-m-1", weight: 2.0)
        };

        await RunAcrossRelationStoresAsync(async store =>
        {
            await store.BatchUpsertAsync(relations, CancellationToken.None);
            // 输入含重复 seedA：去重后应为 [seedZ, seedA, seedM]。
            var batch = await store.QueryNeighborsBatchAsync(new RelationNeighborBatchQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemIds = ["seedZ", "seedA", "seedA", "seedM"],
                Direction = RelationDirection.Outgoing,
                Take = 100
            }, CancellationToken.None);

            Assert.AreEqual(3, batch.Count, "重复种子应去重");
            Assert.AreEqual("seedZ", batch[0].ItemId);
            Assert.AreEqual("seedA", batch[1].ItemId);
            Assert.AreEqual("seedM", batch[2].ItemId);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, batch.Select(b => b.SeedOrdinal).ToArray(),
                "SeedOrdinal 应为去重后种子列表中的序号（0 起升序）");
        });
    }
}
