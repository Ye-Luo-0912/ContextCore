using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Storage.InMemory;

namespace ContextCore.Tests;

/// <summary>
/// 验证生产 <see cref="TraceBackedDecisionEvidenceProvider"/> 行为契约。
/// 覆盖：trace 命中→Complete；trace 缺失→Incomplete；store 未注册→Incomplete；
/// retrieval/package 两条 source 路径分别独立分派；候选 ItemId 缺失→Incomplete。
/// </summary>
[TestClass]
[TestCategory("Decision")]
public sealed class TraceBackedDecisionEvidenceProviderTests
{
    private const string WorkspaceId = "workspace-evidence";
    private const string CollectionId = "collection-evidence";

    /// <summary>
    /// 验证（十三）：Decision Evidence 使用稳定主键点查，不受"最近 N 条"可见窗口限制——
    /// 目标 trace 落库后即使又产生 101+ 条更新 trace，审计仍能命中（数据存在即可查，
    /// 不因 QueryRecent(take=100) 窗口滚动而丢失可见性）。
    /// </summary>
    [TestMethod]
    public async Task Resolve_RetrievalBeyondRecentWindow_StillResolvableByStableKey()
    {
        var retrievalStore = new InMemoryRetrievalTraceStore();
        var provider = new TraceBackedDecisionEvidenceProvider(
            retrievalTraceStore: retrievalStore,
            packageBuildTraceStore: null);

        // 目标决策（Decision A）的 trace 最早落库（CreatedAt 最早）。
        await retrievalStore.SaveAsync(new ContextRetrievalTrace
        {
            RetrievalId = "decision-A",
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            Stages = Array.Empty<ContextRetrievalStageTrace>(),
            SelectedItems = new[]
            {
                new ContextRetrievalDecision
                {
                    CandidateId = "cand-A",
                    SourceId = "src-A",
                    Kind = ContextRetrievalCandidateKind.ContextItem,
                    Type = "note",
                    Reason = "top-score",
                    Score = 0.9
                }
            },
            DroppedItems = Array.Empty<ContextRetrievalDecision>()
        });

        // 之后产生 101+ 条更新 trace（把 Decision A 挤出最近 100 条窗口）。
        for (var i = 0; i < 101; i++)
        {
            await retrievalStore.SaveAsync(new ContextRetrievalTrace
            {
                RetrievalId = $"later-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                CreatedAt = DateTimeOffset.UtcNow,
                Stages = Array.Empty<ContextRetrievalStageTrace>(),
                SelectedItems = Array.Empty<ContextRetrievalDecision>(),
                DroppedItems = Array.Empty<ContextRetrievalDecision>()
            });
        }

        var record = new ContextDecisionRecord
        {
            DecisionId = "decision-A",
            Source = ContextDecisionSource.Retrieval,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Candidates = new[] { new ContextDecisionCandidate { ItemId = "cand-A" } }
        };

        var result = await provider.ResolveEvidenceAsync(record);

        Assert.IsTrue(result.IsComplete,
            "稳定主键点查：窗口外的决策 trace 仍可审计（数据存在即可查）。");
        Assert.AreEqual(1, result.Evidence.Count);
        Assert.AreEqual("top-score", result.Evidence[0].PrimaryRationale);
    }

    /// <summary>
    /// 验证（十二）：QueuedRetrievalTraceStore 下，审计前 Flush 确保"已接受"即"已可查证"——
    /// 队列异步窗口不造成 Evidence 缺失（诊断 trace 可丢，但 Evidence 查证不依赖竞态窗口）。
    /// </summary>
    [TestMethod]
    public async Task Resolve_QueuedStore_FlushBeforeRead_EvidenceComplete()
    {
        var inner = new InMemoryRetrievalTraceStore();
        using var queued = new QueuedRetrievalTraceStore(inner, capacity: 16);
        var provider = new TraceBackedDecisionEvidenceProvider(
            retrievalTraceStore: queued,
            packageBuildTraceStore: null);

        // Save 入队即返回（不等待落库）。
        await queued.SaveAsync(new ContextRetrievalTrace
        {
            RetrievalId = "queued-001",
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            CreatedAt = DateTimeOffset.UtcNow,
            Stages = Array.Empty<ContextRetrievalStageTrace>(),
            SelectedItems = new[]
            {
                new ContextRetrievalDecision
                {
                    CandidateId = "cand-Q",
                    SourceId = "src-Q",
                    Kind = ContextRetrievalCandidateKind.ContextItem,
                    Type = "note",
                    Reason = "queued-hit",
                    Score = 0.8
                }
            },
            DroppedItems = Array.Empty<ContextRetrievalDecision>()
        });

        var record = new ContextDecisionRecord
        {
            DecisionId = "queued-001",
            Source = ContextDecisionSource.Retrieval,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Candidates = new[] { new ContextDecisionCandidate { ItemId = "cand-Q" } }
        };

        var result = await provider.ResolveEvidenceAsync(record);

        Assert.IsTrue(result.IsComplete,
            "审计路径应先排空队列再读取——已接受的 trace 必须可查证。");
        Assert.AreEqual("queued-hit", result.Evidence[0].PrimaryRationale);
    }

    [TestMethod]
    public async Task Resolve_RetrievalWithMatchingTrace_ReturnsCompleteEvidence()
    {
        // 准备 trace store，写入一条与 DecisionId 匹配的 retrieval trace
        var retrievalStore = new InMemoryRetrievalTraceStore();
        var trace = new ContextRetrievalTrace
        {
            RetrievalId = "retrieval-001",
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Stages = new[]
            {
                new ContextRetrievalStageTrace { Name = "rule-recall" },
                new ContextRetrievalStageTrace { Name = "vector-recall" }
            },
            SelectedItems = new[]
            {
                new ContextRetrievalDecision
                {
                    CandidateId = "cand-1",
                    SourceId = "src-1",
                    Kind = ContextRetrievalCandidateKind.ContextItem,
                    Type = "note",
                    Reason = "top-score",
                    Score = 0.92
                }
            },
            DroppedItems = new[]
            {
                new ContextRetrievalDecision
                {
                    CandidateId = "cand-2",
                    SourceId = "src-2",
                    Kind = ContextRetrievalCandidateKind.ContextItem,
                    Type = "note",
                    Reason = "below-threshold",
                    Score = 0.1
                }
            }
        };
        await retrievalStore.SaveAsync(trace);

        var provider = new TraceBackedDecisionEvidenceProvider(
            retrievalTraceStore: retrievalStore,
            packageBuildTraceStore: null);

        var record = new ContextDecisionRecord
        {
            DecisionId = "retrieval-001",
            Source = ContextDecisionSource.Retrieval,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Candidates = new[]
            {
                new ContextDecisionCandidate { ItemId = "cand-1", Outcome = ContextDecisionCandidateOutcome.Selected },
                new ContextDecisionCandidate { ItemId = "cand-2", Outcome = ContextDecisionCandidateOutcome.Dropped }
            }
        };

        var result = await provider.ResolveEvidenceAsync(record);

        Assert.IsTrue(result.IsComplete, "trace 命中且所有候选都有匹配证据时应返回 IsComplete=true");
        Assert.AreEqual(2, result.Evidence.Count);
        Assert.AreEqual(0, result.MissingItemIds.Count);

        var selectedEvidence = result.Evidence.Single(e => e.ItemId == "cand-1");
        Assert.AreEqual("top-score", selectedEvidence.PrimaryRationale);
        Assert.AreEqual("retrieval-trace-selected", selectedEvidence.Provenance);
        Assert.AreEqual(0.92, selectedEvidence.Confidence, 0.0001);
        CollectionAssert.Contains(selectedEvidence.EvidenceRefs.ToArray(), "retrieval-001");
        // 二级依据包含 stage 名称
        CollectionAssert.Contains(selectedEvidence.SecondaryRationales.ToArray(), "rule-recall");
        CollectionAssert.Contains(selectedEvidence.SecondaryRationales.ToArray(), "vector-recall");

        var droppedEvidence = result.Evidence.Single(e => e.ItemId == "cand-2");
        Assert.AreEqual("below-threshold", droppedEvidence.PrimaryRationale);
        Assert.AreEqual("retrieval-trace-dropped", droppedEvidence.Provenance);
    }

    [TestMethod]
    public async Task Resolve_RetrievalWithoutMatchingTrace_ReturnsIncompleteWithAllMissing()
    {
        var retrievalStore = new InMemoryRetrievalTraceStore();
        // 不保存任何 trace，导致 DecisionId 无法匹配

        var provider = new TraceBackedDecisionEvidenceProvider(
            retrievalTraceStore: retrievalStore,
            packageBuildTraceStore: null);

        var record = new ContextDecisionRecord
        {
            DecisionId = "retrieval-missing",
            Source = ContextDecisionSource.Retrieval,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Candidates = new[]
            {
                new ContextDecisionCandidate { ItemId = "cand-a", Outcome = ContextDecisionCandidateOutcome.Selected }
            }
        };

        var result = await provider.ResolveEvidenceAsync(record);

        Assert.IsFalse(result.IsComplete);
        CollectionAssert.AreEqual(new[] { "cand-a" }, result.MissingItemIds.ToArray());
        Assert.AreEqual(0, result.Evidence.Count);
    }

    [TestMethod]
    public async Task Resolve_WithNullRetrievalStore_ReturnsIncomplete()
    {
        // 两个 store 都为 null（如某些 provider 不实现 retrieval trace 时）
        var provider = new TraceBackedDecisionEvidenceProvider(
            retrievalTraceStore: null,
            packageBuildTraceStore: null);

        var record = new ContextDecisionRecord
        {
            DecisionId = "retrieval-002",
            Source = ContextDecisionSource.Retrieval,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Candidates = new[]
            {
                new ContextDecisionCandidate { ItemId = "cand-x", Outcome = ContextDecisionCandidateOutcome.Selected }
            }
        };

        var result = await provider.ResolveEvidenceAsync(record);

        Assert.IsFalse(result.IsComplete);
        CollectionAssert.AreEqual(new[] { "cand-x" }, result.MissingItemIds.ToArray());
    }

    [TestMethod]
    public async Task Resolve_PackageWithMatchingTrace_ReturnsCompleteEvidenceWithUncertaintyRefs()
    {
        var packageStore = new InMemoryContextPackageBuildTraceStore();
        var build = new ContextPackageBuildResult
        {
            BuildId = "build-001",
            Package = new ContextPackage
            {
                PackageId = "pkg-001",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Sections = Array.Empty<ContextPackageSection>()
            },
            SelectedItems = new[]
            {
                new ContextPackageDecision
                {
                    ItemId = "pkg-item-1",
                    Reason = "section-anchor",
                    Score = 0.8,
                    SourceRefs = new[] { "source:path/a.md" }
                }
            },
            DroppedItems = new[]
            {
                new DroppedContextItem
                {
                    ItemId = "pkg-item-2",
                    Reason = "token-budget-exceeded",
                    Score = 0.2
                }
            },
            Uncertainties = new[]
            {
                new ContextPackageUncertainty
                {
                    Code = "budget-pressure",
                    Severity = "Warning",
                    Message = "budget tight",
                    ItemRefs = new[] { "pkg-item-2" }
                }
            }
        };
        await packageStore.SaveAsync(build);

        var provider = new TraceBackedDecisionEvidenceProvider(
            retrievalTraceStore: null,
            packageBuildTraceStore: packageStore);

        var record = new ContextDecisionRecord
        {
            DecisionId = "build-001",
            Source = ContextDecisionSource.Package,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Candidates = new[]
            {
                new ContextDecisionCandidate { ItemId = "pkg-item-1", Outcome = ContextDecisionCandidateOutcome.Selected },
                new ContextDecisionCandidate { ItemId = "pkg-item-2", Outcome = ContextDecisionCandidateOutcome.Dropped }
            }
        };

        var result = await provider.ResolveEvidenceAsync(record);

        Assert.IsTrue(result.IsComplete);
        Assert.AreEqual(2, result.Evidence.Count);
        Assert.AreEqual(0, result.MissingItemIds.Count);

        var selectedEvidence = result.Evidence.Single(e => e.ItemId == "pkg-item-1");
        Assert.AreEqual("section-anchor", selectedEvidence.PrimaryRationale);
        Assert.AreEqual("package-build-trace-selected", selectedEvidence.Provenance);
        CollectionAssert.Contains(selectedEvidence.EvidenceRefs.ToArray(), "build-001");
        CollectionAssert.Contains(selectedEvidence.EvidenceRefs.ToArray(), "source:path/a.md");

        var droppedEvidence = result.Evidence.Single(e => e.ItemId == "pkg-item-2");
        Assert.AreEqual("token-budget-exceeded", droppedEvidence.PrimaryRationale);
        Assert.AreEqual("package-build-trace-dropped", droppedEvidence.Provenance);
        // 不确定性 Code 应作为二级依据
        CollectionAssert.Contains(droppedEvidence.SecondaryRationales.ToArray(), "budget-pressure");
    }

    [TestMethod]
    public async Task Resolve_PackageRetrievalSourceMismatch_DoesNotCrossQueryStores()
    {
        // Package 决策不应查 retrieval store；Retrieval 决策不应查 package store
        var retrievalStore = new InMemoryRetrievalTraceStore();
        await retrievalStore.SaveAsync(new ContextRetrievalTrace
        {
            RetrievalId = "shared-id",
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId
        });

        var packageStore = new InMemoryContextPackageBuildTraceStore();
        await packageStore.SaveAsync(new ContextPackageBuildResult
        {
            BuildId = "shared-id",
            Package = new ContextPackage
            {
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId
            }
        });

        var provider = new TraceBackedDecisionEvidenceProvider(
            retrievalTraceStore: retrievalStore,
            packageBuildTraceStore: packageStore);

        // Package 决策只查 packageStore（命中），不应受 retrievalStore 影响
        var packageRecord = new ContextDecisionRecord
        {
            DecisionId = "shared-id",
            Source = ContextDecisionSource.Package,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Candidates = new[]
            {
                new ContextDecisionCandidate { ItemId = "x" }
            }
        };

        var packageResult = await provider.ResolveEvidenceAsync(packageRecord);
        Assert.IsFalse(packageResult.IsComplete, "packageStore 命中但候选 ItemId 不在 trace 中应返回 Incomplete");
        CollectionAssert.AreEqual(new[] { "x" }, packageResult.MissingItemIds.ToArray());

        // Retrieval 决策只查 retrievalStore（命中但候选列表为空），不应受 packageStore 影响
        var retrievalRecord = new ContextDecisionRecord
        {
            DecisionId = "shared-id",
            Source = ContextDecisionSource.Retrieval,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Candidates = new[]
            {
                new ContextDecisionCandidate { ItemId = "y" }
            }
        };

        var retrievalResult = await provider.ResolveEvidenceAsync(retrievalRecord);
        Assert.IsFalse(retrievalResult.IsComplete);
        CollectionAssert.AreEqual(new[] { "y" }, retrievalResult.MissingItemIds.ToArray());
    }

    [TestMethod]
    public async Task Resolve_WithEmptyCandidates_ReturnsCompleteWithEmptyEvidence()
    {
        // 边界：候选列表为空时（理论上不应发生，但 contract 上要稳定）
        var retrievalStore = new InMemoryRetrievalTraceStore();
        await retrievalStore.SaveAsync(new ContextRetrievalTrace
        {
            RetrievalId = "empty-cand",
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId
        });

        var provider = new TraceBackedDecisionEvidenceProvider(
            retrievalTraceStore: retrievalStore,
            packageBuildTraceStore: null);

        var record = new ContextDecisionRecord
        {
            DecisionId = "empty-cand",
            Source = ContextDecisionSource.Retrieval,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Candidates = Array.Empty<ContextDecisionCandidate>()
        };

        var result = await provider.ResolveEvidenceAsync(record);

        // trace 命中但证据集为空：按 IsComplete 判定逻辑要求 EvidenceByItemId.Count > 0，应返回 Incomplete
        Assert.IsFalse(result.IsComplete);
        Assert.AreEqual(0, result.Evidence.Count);
    }

    [TestMethod]
    public async Task Resolve_WhenStoreThrows_DoesNotSwallowException()
    {
        // ContextDecisionAuditRunner 上层依赖 catch 异常标记 Failed 路径
        var throwingStore = new ThrowingRetrievalTraceStore();

        var provider = new TraceBackedDecisionEvidenceProvider(
            retrievalTraceStore: throwingStore,
            packageBuildTraceStore: null);

        var record = new ContextDecisionRecord
        {
            DecisionId = "throw-test",
            Source = ContextDecisionSource.Retrieval,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Candidates = new[]
            {
                new ContextDecisionCandidate { ItemId = "x" }
            }
        };

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => provider.ResolveEvidenceAsync(record));
    }

    [TestMethod]
    public void Constructor_WithNegativeLookupTake_Throws()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new TraceBackedDecisionEvidenceProvider(
                retrievalTraceStore: null,
                packageBuildTraceStore: null,
                lookupTake: 0));
    }

    [TestMethod]
    public async Task Resolve_NullRecord_Throws()
    {
        var provider = new TraceBackedDecisionEvidenceProvider();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => provider.ResolveEvidenceAsync(null!));
    }

    /// <summary>极简内存 IContextPackageBuildTraceStore 实现，供测试使用。</summary>
    private sealed class InMemoryContextPackageBuildTraceStore : IContextPackageBuildTraceStore
    {
        private readonly List<ContextPackageBuildResult> _builds = new();

        public Task SaveAsync(ContextPackageBuildResult result, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(result);
            _builds.RemoveAll(b => string.Equals(b.BuildId, result.BuildId, StringComparison.OrdinalIgnoreCase));
            _builds.Add(result);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ContextPackageBuildResult>> QueryRecentAsync(
            string workspaceId,
            string collectionId,
            int take,
            CancellationToken cancellationToken = default)
        {
            var count = take > 0 ? take : 50;
            IReadOnlyList<ContextPackageBuildResult> result = _builds
                .Where(b => string.Equals(b.Package.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
                .Where(b => string.Equals(b.Package.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(b => b.CreatedAt)
                .Take(count)
                .ToArray();
            return Task.FromResult(result);
        }

        public Task<ContextPackageBuildResult?> GetAsync(
            string workspaceId,
            string collectionId,
            string buildId,
            CancellationToken cancellationToken = default)
        {
            var result = _builds.FirstOrDefault(b =>
                string.Equals(b.Package.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(b.Package.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(b.BuildId, buildId, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(result);
        }
    }

    /// <summary>始终抛 InvalidOperationException 的 retrieval trace store，用于测试 Failed 路径。</summary>
    private sealed class ThrowingRetrievalTraceStore : IRetrievalTraceStore
    {
        public Task SaveAsync(ContextRetrievalTrace trace, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("test: SaveAsync");

        public Task<IReadOnlyList<ContextRetrievalTrace>> QueryRecentAsync(
            string workspaceId,
            string collectionId,
            int take,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("test: QueryRecentAsync");

        public Task<ContextRetrievalTrace?> GetAsync(
            string workspaceId,
            string collectionId,
            string retrievalId,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("test: GetAsync");
    }
}
