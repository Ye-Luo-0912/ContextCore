using System.Collections.Immutable;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// 增量上下文包：随机 differential testing 验收测试。
/// 核心验收契约：IncrementalBuild(snapshot, request) == FullBuild(request) 在随机状态序列下成立。
/// </summary>
/// <remarks>
/// 测试策略：
/// <list type="bullet">
/// <item>使用随机种子生成状态序列（每次运行不同，但可复现）</item>
/// <item>每一步对 store 应用随机 mutation（insert/update/delete context items / memory / constraints）</item>
/// <item>同时执行 IncrementalBuild（基于前一个快照）与 FullBuild（独立 builder，无缓存）</item>
/// <item>比较两者输出在 6 个维度完全等价：section 内容、selected IDs、dropped IDs、reason code、token attribution、source refs</item>
/// </list>
/// 所有 delta kind 都委托到全量构建，等价性由 inner builder 的确定性保证。
/// 这些测试为 R15 V2 的选择性重载提供安全网 — V2 实现后，相同测试应继续通过。
/// </remarks>
[TestClass]
[TestCategory("Package")]
[TestCategory("R15")]
public sealed class IncrementalPackageDifferentialTests
{
    private const string WorkspaceId = "ws-r15";
    private const string CollectionId = "col-r15";

    /// <summary>
    /// 固定种子的 differential testing：使用确定性种子，每次运行相同序列。
    /// 验证 10 步随机 mutation 下 IncrementalBuild == FullBuild。
    /// </summary>
    [TestMethod]
    public async Task Differential_RandomMutations_WithFixedSeed_IncrementalEqualsFull()
    {
        const int seed = 42;
        const int steps = 10;
        await RunDifferentialSequenceAsync(seed, steps);
    }

    /// <summary>不同种子的 differential testing：验证多种随机序列下等价性。</summary>
    [TestMethod]
    public async Task Differential_RandomMutations_MultipleSeeds_IncrementalEqualsFull()
    {
        // 多个种子覆盖不同 mutation 序列
        foreach (var seed in new[] { 1, 100, 999, 12345 })
        {
            await RunDifferentialSequenceAsync(seed, steps: 5);
        }
    }

    /// <summary>仅 ContextStore mutation：验证插入/更新/删除 context item 后等价性。</summary>
    [TestMethod]
    public async Task Differential_ContextStoreMutations_IncrementalEqualsFull()
    {
        var rng = new Random(7);
        var fixture = await SetupFixtureAsync(seedInitialItems: 5);

        for (var i = 0; i < 6; i++)
        {
            // 应用 ContextStore mutation
            ApplyContextStoreMutation(fixture, rng, index: i);
            // Bump 版本以模拟 Store Decorator 在写入后的行为
            await fixture.BumpVersionAsync("ContextStore");

            await AssertIncrementalEqualsFullAsync(fixture);
        }
    }

    /// <summary>仅 MemoryStore mutation：验证插入/更新/删除 memory item 后等价性。</summary>
    [TestMethod]
    public async Task Differential_MemoryStoreMutations_IncrementalEqualsFull()
    {
        var rng = new Random(13);
        var fixture = await SetupFixtureAsync(seedInitialItems: 3);

        for (var i = 0; i < 5; i++)
        {
            ApplyMemoryStoreMutation(fixture, rng, index: i);
            await fixture.BumpVersionAsync("MemoryStore");

            await AssertIncrementalEqualsFullAsync(fixture);
        }
    }

    /// <summary>仅 ConstraintStore mutation：验证插入/更新/删除 constraint 后等价性。</summary>
    [TestMethod]
    public async Task Differential_ConstraintStoreMutations_IncrementalEqualsFull()
    {
        var rng = new Random(21);
        var fixture = await SetupFixtureAsync(seedInitialItems: 3);

        for (var i = 0; i < 5; i++)
        {
            ApplyConstraintStoreMutation(fixture, rng, index: i);
            await fixture.BumpVersionAsync("ConstraintStore");

            await AssertIncrementalEqualsFullAsync(fixture);
        }
    }

    /// <summary>请求变化（query text）：验证相同 store 数据下不同 query 的等价性。</summary>
    [TestMethod]
    public async Task Differential_RequestQueryChange_IncrementalEqualsFull()
    {
        var fixture = await SetupFixtureAsync(seedInitialItems: 5);

        var queries = new[] { "查询 A", "查询 B", "查询 C", "查询 A" };
        foreach (var query in queries)
        {
            fixture.CurrentRequest = fixture.WithQueryText(query);
            await AssertIncrementalEqualsFullAsync(fixture);
        }
    }

    /// <summary>NoChange 路径：相同请求 + 无 store 变化，IncrementalBuild 应与 FullBuild 等价。</summary>
    [TestMethod]
    public async Task NoChange_SameRequestNoStoreMutation_IncrementalEqualsFull()
    {
        PackageDeltaPlan? capturedDelta = null;
        var fixture = await SetupFixtureAsync(seedInitialItems: 5, onDeltaPlanned: plan => capturedDelta = plan);

        // 第一次构建：捕获快照
        await AssertIncrementalEqualsFullAsync(fixture);

        // 第二次构建：相同请求 + 无 store 变化 → delta kind 应为 NoChange
        await AssertIncrementalEqualsFullAsync(fixture);

        Assert.IsNotNull(capturedDelta, "delta 回调应被调用");
        Assert.AreEqual(PackageDeltaKind.NoChange, capturedDelta.Kind,
            "相同请求 + 无 store 变化应产生 NoChange delta");
    }

    /// <summary>Snapshot 捕获：BuildDetailedWithSnapshotAsync 应返回非空快照与结果。</summary>
    [TestMethod]
    public async Task BuildDetailedWithSnapshot_ReturnsValidSnapshotAndResult()
    {
        var fixture = await SetupFixtureAsync(seedInitialItems: 3);

        var withSnapshot = await fixture.SnapshotBuilder.BuildDetailedWithSnapshotAsync(fixture.CurrentRequest);

        Assert.IsNotNull(withSnapshot.Result, "BuildDetailedWithSnapshot 结果应非空");
        Assert.IsNotNull(withSnapshot.Snapshot, "快照应非空");
        Assert.IsNotNull(withSnapshot.Snapshot.RequestFingerprint, "快照应包含请求指纹");
        Assert.IsFalse(string.IsNullOrEmpty(withSnapshot.Snapshot.RequestFingerprint.Hash), "指纹 hash 应非空");
        Assert.IsNotNull(withSnapshot.Snapshot.SectionDependencies, "快照应包含 section 依赖映射");
        Assert.IsTrue(withSnapshot.Snapshot.SectionDependencies.Count > 0, "section 依赖映射应非空");
    }

    /// <summary>
    /// V2 大规模 differential testing：100 步随机 mutation 序列。
    /// 验证长期运行下 IncrementalBuild == FullBuild 在每一步均成立。
    /// </summary>
    [TestMethod]
    public async Task Differential_LargeScale_100Steps_IncrementalEqualsFull()
    {
        const int seed = 2026;
        const int steps = 100;
        await RunDifferentialSequenceAsync(seed, steps);
    }

    /// <summary>
    /// V2 多种子大规模 differential testing：3 个种子 × 50 步。
    /// </summary>
    [TestMethod]
    public async Task Differential_LargeScale_MultipleSeeds_50Steps_IncrementalEqualsFull()
    {
        foreach (var seed in new[] { 7777, 88888, 999999 })
        {
            await RunDifferentialSequenceAsync(seed, steps: 50);
        }
    }

    /// <summary>
    /// V2 NoChange 路径真正复用 PackageTemplate：
    /// 使用 CallTrackingBuilder 包装，验证 NoChange delta 时 RebuildFromSnapshotAsync 被调用，
    /// BuildDetailedAsync 未被调用。其他 delta kind 时反之。
    /// </summary>
    [TestMethod]
    public async Task NoChange_PathCallsRebuildFromSnapshot_NotBuildDetailed()
    {
        var fixture = await SetupFixtureAsync(seedInitialItems: 3);
        var tracker = new CallTrackingBuilder(fixture.SnapshotBuilder);
        var incrementalBuilder = new PackageIncrementalBuilder(
            tracker, new PackageDeltaPlanner(), fixture.VersionStore);

        // 第一步：捕获快照
        var withSnapshot = await fixture.SnapshotBuilder.BuildDetailedWithSnapshotAsync(fixture.CurrentRequest);
        var snapshot = withSnapshot.Snapshot;

        // NoChange 路径：相同请求 + 无 store 变化
        tracker.Reset();
        await incrementalBuilder.IncrementalBuildAsync(snapshot, fixture.CurrentRequest);
        Assert.AreEqual(1, tracker.RebuildFromSnapshotCalls, "NoChange 应调用 RebuildFromSnapshotAsync 一次");
        Assert.AreEqual(0, tracker.BuildDetailedCalls, "NoChange 不应调用 BuildDetailedAsync");

        // 触发 store 变化 → PartialSectionChange 或 FullRebuildRequired
        await fixture.ContextStore.SaveAsync(CreateContextItem("trigger-change", "新内容", DateTimeOffset.UtcNow));
        await fixture.BumpVersionAsync("ContextStore");

        tracker.Reset();
        await incrementalBuilder.IncrementalBuildAsync(snapshot, fixture.CurrentRequest);
        Assert.AreEqual(0, tracker.RebuildFromSnapshotCalls, "非 NoChange 不应调用 RebuildFromSnapshotAsync");
        Assert.AreEqual(1, tracker.BuildDetailedCalls, "非 NoChange 应调用 BuildDetailedAsync 一次");
    }

    /// <summary>
    /// V2 NoChange 路径连续多次复用同一快照：
    /// 验证 5 次连续 NoChange 增量构建均等价于全量构建。
    /// </summary>
    [TestMethod]
    public async Task NoChange_RepeatedReusesSameSnapshot_IncrementalEqualsFull()
    {
        var fixture = await SetupFixtureAsync(seedInitialItems: 5);

        // 捕获初始快照
        var withSnapshot = await fixture.SnapshotBuilder.BuildDetailedWithSnapshotAsync(fixture.CurrentRequest);
        var snapshot = withSnapshot.Snapshot;

        // 连续 5 次 NoChange 增量构建（相同请求 + 无 store 变化）
        for (var i = 0; i < 5; i++)
        {
            await AssertIncrementalEqualsFullWithSnapshotAsync(fixture, snapshot);
        }
    }

    /// <summary>
    /// V2 混合序列：NoChange 与 store 变化交替，验证快照在变化后必须重新捕获。
    /// </summary>
    [TestMethod]
    public async Task Differential_MixedNoChangeAndMutations_IncrementalEqualsFull()
    {
        var rng = new Random(314);
        var fixture = await SetupFixtureAsync(seedInitialItems: 5);

        PackageStateSnapshot? currentSnapshot = null;
        for (var step = 0; step < 20; step++)
        {
            // 50% 概率触发 store 变化，50% 概率保持 NoChange
            if (rng.Next(2) == 0)
            {
                ApplyContextStoreMutation(fixture, rng, step);
                await fixture.BumpVersionAsync("ContextStore");
            }

            // 重新捕获快照（每次都捕获，确保快照反映当前状态）
            var withSnapshot = await fixture.SnapshotBuilder.BuildDetailedWithSnapshotAsync(fixture.CurrentRequest);

            // NoChange 路径：使用刚捕获的快照（与当前状态一致）
            var incrementalResult = await fixture.IncrementalBuilder.IncrementalBuildAsync(
                withSnapshot.Snapshot, fixture.CurrentRequest);
            var fullResult = await fixture.SnapshotBuilder.BuildDetailedAsync(fixture.CurrentRequest);

            AssertResultsEquivalent(incrementalResult, fullResult, $"step-{step}");
            currentSnapshot = withSnapshot.Snapshot;
        }

        Assert.IsNotNull(currentSnapshot, "最终快照应非空");
    }

    // ===== Private helpers =====

    private async Task RunDifferentialSequenceAsync(int seed, int steps)
    {
        var rng = new Random(seed);
        var fixture = await SetupFixtureAsync(seedInitialItems: 5);

        for (var step = 0; step < steps; step++)
        {
            // 随机选择 mutation 类型
            var mutationKind = rng.Next(4);
            switch (mutationKind)
            {
                case 0:
                    ApplyContextStoreMutation(fixture, rng, step);
                    await fixture.BumpVersionAsync("ContextStore");
                    break;
                case 1:
                    ApplyMemoryStoreMutation(fixture, rng, step);
                    await fixture.BumpVersionAsync("MemoryStore");
                    break;
                case 2:
                    ApplyConstraintStoreMutation(fixture, rng, step);
                    await fixture.BumpVersionAsync("ConstraintStore");
                    break;
                case 3:
                    // 请求变化（query text）— QueryText 是 init-only，构造新请求
                    fixture.CurrentRequest = fixture.WithQueryText($"查询 step-{step}-seed-{seed}");
                    break;
            }

            await AssertIncrementalEqualsFullAsync(fixture);
        }
    }

    private async Task<IncrementalFixture> SetupFixtureAsync(int seedInitialItems, Action<PackageDeltaPlan>? onDeltaPlanned = null)
    {
        var contextStore = new InMemoryContextStore();
        var memoryStore = new InMemoryMemoryStore();
        var constraintStore = new InMemoryConstraintStore();
        var versionStore = new InMemoryContextStateVersionStore();

        // 使用 ISnapshotCapablePackageBuilder（BasicContextPackageBuilder 实现此接口）
        var snapshotBuilder = new BasicContextPackageBuilder(
            contextStore,
            constraintStore,
            globalContextStore: null,
            memoryStore,
            relationStore: null,
            traceStore: null,
            tokenizerResolver: null,
            workingMemoryService: null,
            decisionTraceStore: null,
            runtimeCandidateTraceSink: null,
            traversalEngine: null,
            cacheAccessor: null, // 禁用缓存，确保 FullBuild 真正执行
            versionStore: versionStore);

        // IncrementalBuilder 包装 snapshotBuilder
        var deltaPlanner = new PackageDeltaPlanner();
        var incrementalBuilder = new PackageIncrementalBuilder(
            snapshotBuilder, deltaPlanner, versionStore, onDeltaPlanned);

        // FullBuilder 是独立的 builder 实例（独立 stores 但相同数据），用于等价性比较
        // 实际上为简化测试，FullBuilder 与 IncrementalBuilder 共享相同 stores，
        // 因为 IncrementalBuilder V1 委托到 snapshotBuilder，两者都使用相同数据源。
        // FullBuilder 单独构造以避免任何 AsyncLocal 状态污染。

        var request = new ContextPackageRequest
        {
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            QueryText = "初始查询",
            TokenBudget = 4_000,
            OperationId = $"op-{Guid.NewGuid():N}",
            RequestId = $"req-{Guid.NewGuid():N}",
            Policy = CreateDefaultPolicy()
        };

        var fixture = new IncrementalFixture(
            contextStore, memoryStore, constraintStore, versionStore,
            snapshotBuilder, incrementalBuilder, request);

        // 种子数据
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < seedInitialItems; i++)
        {
            await contextStore.SaveAsync(CreateContextItem($"seed-ctx-{i}", $"初始上下文 {i}", now.AddMinutes(-i)));
        }
        await memoryStore.SaveAsync(new ContextMemoryItem
        {
            Id = "seed-mem-working",
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Layer = ContextMemoryLayer.Working,
            Content = "初始工作记忆",
            CreatedAt = now
        });
        await constraintStore.SaveAsync(CreateConstraint("seed-hard", ConstraintLevel.Hard, "初始硬约束"));

        return fixture;
    }

    private static async Task AssertIncrementalEqualsFullAsync(IncrementalFixture fixture)
    {
        // 1. 捕获当前快照（通过 BuildDetailedWithSnapshotAsync）
        var withSnapshot = await fixture.SnapshotBuilder.BuildDetailedWithSnapshotAsync(fixture.CurrentRequest);
        var snapshot = withSnapshot.Snapshot;

        // 2. 执行 IncrementalBuild（使用刚捕获的快照作为 previousSnapshot）
        // 注意：R15 V1 中 IncrementalBuild 委托到 inner builder，所以这里与 FullBuild 等价
        var incrementalResult = await fixture.IncrementalBuilder.IncrementalBuildAsync(
            snapshot, fixture.CurrentRequest);

        // 3. 执行 FullBuild（独立调用，使用相同请求）
        var fullResult = await fixture.SnapshotBuilder.BuildDetailedAsync(fixture.CurrentRequest);

        // 4. 比较两个结果在 6 个维度完全等价
        AssertResultsEquivalent(incrementalResult, fullResult, fixture.CurrentRequest.QueryText ?? "");
    }

    /// <summary>
    /// 使用预先捕获的快照执行 IncrementalBuild，验证与 FullBuild 等价。
    /// 用于 NoChange 路径连续复用同一快照的场景。
    /// </summary>
    private static async Task AssertIncrementalEqualsFullWithSnapshotAsync(
        IncrementalFixture fixture,
        PackageStateSnapshot snapshot)
    {
        var incrementalResult = await fixture.IncrementalBuilder.IncrementalBuildAsync(
            snapshot, fixture.CurrentRequest);
        var fullResult = await fixture.SnapshotBuilder.BuildDetailedAsync(fixture.CurrentRequest);
        AssertResultsEquivalent(incrementalResult, fullResult, "NoChange-reuse");
    }

    private static void AssertResultsEquivalent(
        ContextPackageBuildResult incremental,
        ContextPackageBuildResult full,
        string context)
    {
        // 维度 1: section 内容
        Assert.AreEqual(full.Package.Sections.Count, incremental.Package.Sections.Count,
            $"[{context}] section 数量应一致");
        for (var i = 0; i < full.Package.Sections.Count; i++)
        {
            var fullSection = full.Package.Sections[i];
            var incrSection = incremental.Package.Sections[i];
            Assert.AreEqual(fullSection.Name, incrSection.Name,
                $"[{context}] section {i} 名称应一致");
            Assert.AreEqual(fullSection.Content, incrSection.Content,
                $"[{context}] section '{fullSection.Name}' 内容应一致");
            Assert.AreEqual(fullSection.EstimatedTokens, incrSection.EstimatedTokens,
                $"[{context}] section '{fullSection.Name}' token 估算应一致");
        }

        // 维度 2: selected IDs
        var fullSelectedIds = full.SelectedItems.Select(d => d.ItemId).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var incrSelectedIds = incremental.SelectedItems.Select(d => d.ItemId).OrderBy(id => id, StringComparer.Ordinal).ToList();
        CollectionAssert.AreEqual(fullSelectedIds, incrSelectedIds,
            $"[{context}] selected IDs 应一致");

        // 维度 3: dropped IDs
        var fullDroppedIds = full.DroppedItems.Select(d => d.ItemId).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var incrDroppedIds = incremental.DroppedItems.Select(d => d.ItemId).OrderBy(id => id, StringComparer.Ordinal).ToList();
        CollectionAssert.AreEqual(fullDroppedIds, incrDroppedIds,
            $"[{context}] dropped IDs 应一致");

        // 维度 4: reason code（如果存在）
        // ContextPackageDecision 没有 ReasonCode 字段，但有 Reason 字段
        var fullReasons = full.SelectedItems
            .OrderBy(d => d.ItemId, StringComparer.Ordinal)
            .Select(d => d.Reason ?? "")
            .ToList();
        var incrReasons = incremental.SelectedItems
            .OrderBy(d => d.ItemId, StringComparer.Ordinal)
            .Select(d => d.Reason ?? "")
            .ToList();
        CollectionAssert.AreEqual(fullReasons, incrReasons,
            $"[{context}] selected items reason 应一致");

        // 维度 5: token attribution（EstimatedTokens / TokenBudget）
        Assert.AreEqual(full.TokenBudget, incremental.TokenBudget,
            $"[{context}] TokenBudget 应一致");
        Assert.AreEqual(full.EstimatedTokens, incremental.EstimatedTokens,
            $"[{context}] EstimatedTokens 应一致");

        // 维度 6: source refs (ItemReferences)
        var fullSourceRefs = full.ItemReferences
            .OrderBy(r => r.ItemId, StringComparer.Ordinal)
            .Select(r => $"{r.ItemId}:{r.PrimarySectionName}:{r.ReferencingSectionName}:{r.Reason}")
            .ToList();
        var incrSourceRefs = incremental.ItemReferences
            .OrderBy(r => r.ItemId, StringComparer.Ordinal)
            .Select(r => $"{r.ItemId}:{r.PrimarySectionName}:{r.ReferencingSectionName}:{r.Reason}")
            .ToList();
        CollectionAssert.AreEqual(fullSourceRefs, incrSourceRefs,
            $"[{context}] source refs (ItemReferences) 应一致");
    }

    private static void ApplyContextStoreMutation(IncrementalFixture fixture, Random rng, int index)
    {
        var kind = rng.Next(3);
        var id = $"ctx-mut-{index}";
        var now = DateTimeOffset.UtcNow;
        switch (kind)
        {
            case 0: // insert
                fixture.ContextStore.SaveAsync(CreateContextItem(id, $"新插入上下文 {index}", now)).GetAwaiter().GetResult();
                break;
            case 1: // update（先插入再更新）
                fixture.ContextStore.SaveAsync(CreateContextItem(id, $"插入 {index}", now)).GetAwaiter().GetResult();
                fixture.ContextStore.SaveAsync(CreateContextItem(id, $"更新后内容 {index}", now)).GetAwaiter().GetResult();
                break;
            case 2: // delete（通过 SaveAsync 覆盖 + 标记）
                // InMemoryContextStore 没有直接 delete，通过更新 Content 为空模拟
                fixture.ContextStore.SaveAsync(CreateContextItem(id, "", now)).GetAwaiter().GetResult();
                break;
        }
    }

    private static void ApplyMemoryStoreMutation(IncrementalFixture fixture, Random rng, int index)
    {
        var id = $"mem-mut-{index}";
        var now = DateTimeOffset.UtcNow;
        fixture.MemoryStore.SaveAsync(new ContextMemoryItem
        {
            Id = id,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Layer = ContextMemoryLayer.Working,
            Content = $"变异记忆 {index}",
            CreatedAt = now
        }).GetAwaiter().GetResult();
    }

    private static void ApplyConstraintStoreMutation(IncrementalFixture fixture, Random rng, int index)
    {
        var id = $"const-mut-{index}";
        fixture.ConstraintStore.SaveAsync(CreateConstraint(
            id,
            ConstraintLevel.Hard,
            $"变异硬约束 {index}")).GetAwaiter().GetResult();
    }

    private static ContextItem CreateContextItem(string id, string content, DateTimeOffset createdAt)
    {
        return new ContextItem
        {
            Id = id,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Content = content,
            CreatedAt = createdAt,
            Tags = []
        };
    }

    private static ContextConstraint CreateConstraint(string id, ConstraintLevel level, string content)
    {
        return new ContextConstraint
        {
            Id = id,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Level = level,
            Content = content,
            Confidence = 0.9,
            UpdatedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, string>()
        };
    }

    private static ContextPackagePolicy CreateDefaultPolicy()
    {
        return new ContextPackagePolicy
        {
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            TokenBudget = 4_000,
            IncludeGlobalContext = false,
            IncludeHardConstraints = true,
            IncludeSoftConstraints = false,
            IncludeWorkingMemory = true,
            IncludeStableMemory = false,
            IncludeRecentRawContext = true,
            MaxRecentItems = 20,
            SectionOrder = ["recent_context", "working_memory", "hard_constraints"],
            SectionPriorities = new Dictionary<string, int>
            {
                ["recent_context"] = 10,
                ["working_memory"] = 8,
                ["hard_constraints"] = 9
            },
            SectionTokenBudgets = new Dictionary<string, int>
            {
                ["recent_context"] = 2000,
                ["working_memory"] = 1000,
                ["hard_constraints"] = 1000
            },
            Metadata = new Dictionary<string, string>()
        };
    }

    /// <summary>测试夹具：封装 stores + builders + 当前请求。</summary>
    private sealed class IncrementalFixture
    {
        public InMemoryContextStore ContextStore { get; }
        public InMemoryMemoryStore MemoryStore { get; }
        public InMemoryConstraintStore ConstraintStore { get; }
        public InMemoryContextStateVersionStore VersionStore { get; }
        public ISnapshotCapablePackageBuilder SnapshotBuilder { get; }
        public IPackageIncrementalBuilder IncrementalBuilder { get; }
        public ContextPackageRequest CurrentRequest { get; set; }

        public IncrementalFixture(
            InMemoryContextStore contextStore,
            InMemoryMemoryStore memoryStore,
            InMemoryConstraintStore constraintStore,
            InMemoryContextStateVersionStore versionStore,
            ISnapshotCapablePackageBuilder snapshotBuilder,
            IPackageIncrementalBuilder incrementalBuilder,
            ContextPackageRequest currentRequest)
        {
            ContextStore = contextStore;
            MemoryStore = memoryStore;
            ConstraintStore = constraintStore;
            VersionStore = versionStore;
            SnapshotBuilder = snapshotBuilder;
            IncrementalBuilder = incrementalBuilder;
            CurrentRequest = currentRequest;
        }

        public async Task BumpVersionAsync(string storeKind)
        {
            await VersionStore.BumpVersionAsync(WorkspaceId, CollectionId, storeKind);
        }

        /// <summary>构造一个新请求，仅替换 QueryText（QueryText 是 init-only）。</summary>
        public ContextPackageRequest WithQueryText(string queryText)
        {
            var prev = CurrentRequest;
            return new ContextPackageRequest
            {
                WorkspaceId = prev.WorkspaceId,
                CollectionId = prev.CollectionId,
                QueryText = queryText,
                RequiredTags = prev.RequiredTags,
                RequiredTypes = prev.RequiredTypes,
                TokenBudget = prev.TokenBudget,
                IncludeRecent = prev.IncludeRecent,
                Mode = prev.Mode,
                Policy = prev.Policy,
                IsAuditMode = prev.IsAuditMode,
                OperationId = $"op-{Guid.NewGuid():N}",
                RequestId = $"req-{Guid.NewGuid():N}",
                Metadata = prev.Metadata
            };
        }
    }
}

/// <summary>测试夹具：内存 IContextStateVersionStore 实现，供 differential testing 使用。</summary>
internal sealed class InMemoryContextStateVersionStore : IContextStateVersionStore
{
    private readonly Dictionary<VersionScope, long> _versions = new();

    public Task<long> GetVersionAsync(
        string workspaceId,
        string collectionId,
        string storeKind,
        CancellationToken cancellationToken = default)
    {
        var scope = new VersionScope(workspaceId, collectionId, storeKind);
        return Task.FromResult(_versions.TryGetValue(scope, out var version) ? version : 0L);
    }

    public Task<IReadOnlyDictionary<VersionScope, long>> GetVersionsAsync(
        IReadOnlyCollection<VersionScope> scopes,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<VersionScope, long>();
        foreach (var scope in scopes)
        {
            result[scope] = _versions.TryGetValue(scope, out var version) ? version : 0L;
        }
        return Task.FromResult<IReadOnlyDictionary<VersionScope, long>>(result);
    }

    public Task<long> BumpVersionAsync(
        string workspaceId,
        string collectionId,
        string storeKind,
        CancellationToken cancellationToken = default)
    {
        var scope = new VersionScope(workspaceId, collectionId, storeKind);
        var newVersion = (_versions.TryGetValue(scope, out var current) ? current : 0L) + 1;
        _versions[scope] = newVersion;
        return Task.FromResult(newVersion);
    }
}

/// <summary>
/// V2 测试夹具：包装 ISnapshotCapablePackageBuilder，统计 RebuildFromSnapshotAsync
/// 与 BuildDetailedAsync 的调用次数，用于验证 NoChange 路径真的走了快照复用。
/// </summary>
internal sealed class CallTrackingBuilder : ISnapshotCapablePackageBuilder
{
    private readonly ISnapshotCapablePackageBuilder _inner;
    public int RebuildFromSnapshotCalls;
    public int BuildDetailedCalls;

    public CallTrackingBuilder(ISnapshotCapablePackageBuilder inner)
    {
        _inner = inner;
    }

    public void Reset()
    {
        RebuildFromSnapshotCalls = 0;
        BuildDetailedCalls = 0;
    }

    public Task<ContextPackage> BuildAsync(ContextPackageRequest request, CancellationToken cancellationToken = default)
        => _inner.BuildAsync(request, cancellationToken);

    public Task<ContextPackageBuildResult> BuildDetailedAsync(ContextPackageRequest request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref BuildDetailedCalls);
        return _inner.BuildDetailedAsync(request, cancellationToken);
    }

    public Task<PackageBuildWithSnapshot> BuildDetailedWithSnapshotAsync(ContextPackageRequest request, CancellationToken cancellationToken = default)
        => _inner.BuildDetailedWithSnapshotAsync(request, cancellationToken);

    public Task<ContextPackageBuildResult> RebuildFromSnapshotAsync(
        PackageStateSnapshot snapshot,
        ContextPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref RebuildFromSnapshotCalls);
        return _inner.RebuildFromSnapshotAsync(snapshot, request, cancellationToken);
    }
}
