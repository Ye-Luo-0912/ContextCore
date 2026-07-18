using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// R13.2 验收：Package Read Plan 行为——merged constraint 去重、current_task 并行化、Store call count 跟踪。
/// </summary>
[TestClass]
[TestCategory("Package")]
public sealed class PackageReadPlanTests
{
    [TestMethod]
    public async Task ReadPlan_WhenMergedAndHardSectionBothEnabled_DedupHitsIncrement()
    {
        // R13.2 #1：merged 与 hard_constraints section 同时启用 Hard 时应去重，DedupHits 至少为 1。
        var constraintStore = new InMemoryConstraintStore();
        var builder = new BasicContextPackageBuilder(
            new InMemoryContextStore(),
            constraintStore,
            globalContextStore: null,
            memoryStore: null,
            relationStore: null);

        await constraintStore.SaveAsync(CreateConstraint(
            "hard-1",
            ConstraintLevel.Hard,
            "硬约束：项目级强制规则。"));

        var result = await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TokenBudget = 2_000,
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 2_000,
                IncludeGlobalContext = false,
                IncludeHardConstraints = true,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = false,
                IncludeStableMemory = false,
                IncludeRecentRawContext = false,
                Metadata = new Dictionary<string, string>
                {
                    ["includeMergedConstraintsSection"] = "true",
                    ["constraintMergeMaxItems"] = "10"
                }
            }
        });

        Assert.IsNotNull(result.ReadPlan, "ReadPlan 应被填充");
        Assert.AreEqual(1, result.ReadPlan.DedupHits, "Hard section 与 merged 同时启用应去重 Hard 查询");
        // Hard 1 次（section+merged 共享）+ Soft 1 次（merged 用，section 未启用）+ All 1 次 = 3 次
        Assert.AreEqual(3, result.ReadPlan.TotalStoreCalls,
            "Hard 1 + Soft 1（merged 用）+ All 1 = 3 次 ConstraintStore 调用");
    }

    [TestMethod]
    public async Task ReadPlan_WhenMergedAndSoftSectionBothEnabled_DedupHitsIncrement()
    {
        // R13.2 #1：merged 与 soft_constraints section 同时启用 Soft 时应去重。
        var constraintStore = new InMemoryConstraintStore();
        var builder = new BasicContextPackageBuilder(
            new InMemoryContextStore(),
            constraintStore,
            globalContextStore: null,
            memoryStore: null,
            relationStore: null);

        await constraintStore.SaveAsync(CreateConstraint(
            "soft-1",
            ConstraintLevel.Soft,
            "软约束：风格偏好。"));

        var result = await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TokenBudget = 2_000,
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 2_000,
                IncludeGlobalContext = false,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = true,
                IncludeWorkingMemory = false,
                IncludeStableMemory = false,
                IncludeRecentRawContext = false,
                Metadata = new Dictionary<string, string>
                {
                    ["includeMergedConstraintsSection"] = "true",
                    ["constraintMergeMaxItems"] = "10"
                }
            }
        });

        Assert.IsNotNull(result.ReadPlan);
        Assert.AreEqual(1, result.ReadPlan.DedupHits, "Soft section 与 merged 同时启用应去重 Soft 查询");
        // Hard 1 次（merged 用，section 未启用）+ Soft 1 次（section+merged 共享）+ All 1 次 = 3 次
        Assert.AreEqual(3, result.ReadPlan.TotalStoreCalls);
    }

    [TestMethod]
    public async Task ReadPlan_WhenMergedAndBothHardSoftSectionsEnabled_DedupHitsIsTwo()
    {
        // R13.2 #1：Hard + Soft section + merged 全启用时，DedupHits = 2（Hard 和 Soft 各去重一次）。
        var constraintStore = new InMemoryConstraintStore();
        var builder = new BasicContextPackageBuilder(
            new InMemoryContextStore(),
            constraintStore,
            globalContextStore: null,
            memoryStore: null,
            relationStore: null);

        await constraintStore.SaveAsync(CreateConstraint("h1", ConstraintLevel.Hard, "硬约束"));
        await constraintStore.SaveAsync(CreateConstraint("s1", ConstraintLevel.Soft, "软约束"));

        var result = await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TokenBudget = 2_000,
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 2_000,
                IncludeGlobalContext = false,
                IncludeHardConstraints = true,
                IncludeSoftConstraints = true,
                IncludeWorkingMemory = false,
                IncludeStableMemory = false,
                IncludeRecentRawContext = false,
                Metadata = new Dictionary<string, string>
                {
                    ["includeMergedConstraintsSection"] = "true",
                    ["constraintMergeMaxItems"] = "10"
                }
            }
        });

        Assert.IsNotNull(result.ReadPlan);
        Assert.AreEqual(2, result.ReadPlan.DedupHits, "Hard + Soft 各去重一次，DedupHits = 2");
        // 旧实现：Hard(1) + Soft(1) + merged Hard(1) + merged Soft(1) + merged All(1) = 5
        // 新实现（去重后）：Hard(1) + Soft(1) + All(1) = 3
        Assert.AreEqual(3, result.ReadPlan.TotalStoreCalls,
            "Hard 1 + Soft 1 + All 1 = 3 次（旧实现为 5，去重后省 2 次）");
    }

    [TestMethod]
    public async Task ReadPlan_WhenMergedOnlyNoSections_QueriesOnlyAllLevel()
    {
        // R13.2 #1：仅 merged 启用，section 都关闭时，无 DedupHits，但 Hard/Soft 仍需查询供 merged 用。
        var constraintStore = new InMemoryConstraintStore();
        var builder = new BasicContextPackageBuilder(
            new InMemoryContextStore(),
            constraintStore,
            globalContextStore: null,
            memoryStore: null,
            relationStore: null);

        await constraintStore.SaveAsync(CreateConstraint("h1", ConstraintLevel.Hard, "硬约束"));
        await constraintStore.SaveAsync(CreateConstraint("s1", ConstraintLevel.Soft, "软约束"));

        var result = await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TokenBudget = 2_000,
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 2_000,
                IncludeGlobalContext = false,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = false,
                IncludeStableMemory = false,
                IncludeRecentRawContext = false,
                Metadata = new Dictionary<string, string>
                {
                    ["includeMergedConstraintsSection"] = "true",
                    ["constraintMergeMaxItems"] = "10"
                }
            }
        });

        Assert.IsNotNull(result.ReadPlan);
        Assert.AreEqual(0, result.ReadPlan.DedupHits, "未启用 section 时无去重命中");
        // merged 需要 Hard + Soft + All 三路独立查询
        Assert.AreEqual(3, result.ReadPlan.TotalStoreCalls,
            "merged 启用、section 关闭：Hard(1) + Soft(1) + All(1) = 3 次");
    }

    [TestMethod]
    public async Task ReadPlan_TracksContextStoreAndMemoryStoreCalls()
    {
        // R13.2 #4：验证 ReadPlan 记录 ContextStore / MemoryStore / GlobalContextStore 调用。
        var now = DateTimeOffset.UtcNow;
        var contextStore = new InMemoryContextStore();
        var memoryStore = new InMemoryMemoryStore();
        var globalStore = new InMemoryGlobalContextStore();
        var builder = new BasicContextPackageBuilder(
            contextStore,
            constraintStore: null,
            globalStore,
            memoryStore,
            relationStore: null);

        await contextStore.SaveAsync(CreateItem("recent-1", "最近上下文内容", now));
        await memoryStore.SaveAsync(new ContextMemoryItem
        {
            Id = "working-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Layer = ContextMemoryLayer.Working,
            Content = "工作记忆",
            CreatedAt = now
        });
        await memoryStore.SaveAsync(new ContextMemoryItem
        {
            Id = "stable-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Layer = ContextMemoryLayer.Stable,
            Status = ContextMemoryStatus.Stable,
            Content = "稳定记忆",
            CreatedAt = now
        });

        var result = await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TokenBudget = 2_000,
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 2_000,
                IncludeGlobalContext = true,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = true,
                IncludeStableMemory = true,
                IncludeRecentRawContext = true,
                MaxRecentItems = 10
            }
        });

        Assert.IsNotNull(result.ReadPlan);
        var counts = result.ReadPlan.StoreCallCounts;
        Assert.IsTrue(counts.TryGetValue("ContextStore.Query", out var ctxCalls) && ctxCalls == 1,
            "ContextStore.Query 应被记录 1 次");
        Assert.IsTrue(counts.TryGetValue("MemoryStore.Query(Working)", out var workingCalls) && workingCalls == 1,
            "MemoryStore.Query(Working) 应被记录 1 次");
        Assert.IsTrue(counts.TryGetValue("MemoryStore.Query(Stable)", out var stableCalls) && stableCalls == 1,
            "MemoryStore.Query(Stable) 应被记录 1 次");
        Assert.IsTrue(counts.TryGetValue("GlobalContextStore.Query", out var globalCalls) && globalCalls == 1,
            "GlobalContextStore.Query 应被记录 1 次");
        Assert.AreEqual(0, result.ReadPlan.DedupHits, "无 merged 时无去重命中");
    }

    [TestMethod]
    public async Task ReadPlan_TracksCurrentTaskServiceCall()
    {
        // R13.2 #3 + #4：current_task 解析应记录 WorkingMemoryService.GetCurrentTask 调用。
        // InMemoryMemoryStore 同时实现 IMemoryStore + IWorkingMemoryService（共享存储）。
        var workingMemoryService = new InMemoryMemoryStore();
        var builder = new BasicContextPackageBuilder(
            new InMemoryContextStore(),
            constraintStore: null,
            globalContextStore: null,
            memoryStore: null,
            relationStore: null,
            traceStore: null,
            tokenizerResolver: null,
            workingMemoryService);

        var result = await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TokenBudget = 1_000,
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 1_000,
                IncludeGlobalContext = false,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = false,
                IncludeStableMemory = false,
                IncludeRecentRawContext = false,
                Metadata = new Dictionary<string, string>
                {
                    ["includeCurrentTaskSection"] = "true"
                }
            }
        });

        Assert.IsNotNull(result.ReadPlan);
        Assert.IsTrue(
            result.ReadPlan.StoreCallCounts.TryGetValue("WorkingMemoryService.GetCurrentTask", out var calls) && calls == 1,
            "current_task 解析应记录 WorkingMemoryService.GetCurrentTask 调用 1 次");
    }

    private static ContextConstraint CreateConstraint(
        string id,
        ConstraintLevel level,
        string content,
        Dictionary<string, string>? metadata = null)
    {
        return new ContextConstraint
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Level = level,
            Content = content,
            Confidence = 0.9,
            UpdatedAt = DateTimeOffset.UtcNow,
            Metadata = metadata ?? new Dictionary<string, string>()
        };
    }

    private static ContextItem CreateItem(
        string id,
        string content,
        DateTimeOffset createdAt,
        string[]? tags = null)
    {
        return new ContextItem
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Content = content,
            CreatedAt = createdAt,
            Tags = tags ?? []
        };
    }
}
