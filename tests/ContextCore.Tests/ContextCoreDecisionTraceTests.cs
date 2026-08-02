using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// Context Decision Foundation 测试。
/// 验证 decision trace 不改变 package/retrieval 正式输出，非激活契约恒成立，投影保留 ID。
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ContextCoreDecisionTraceTests
{
    private static readonly string WorkspaceId = "workspace-decision";
    private static readonly string CollectionId = "collection-decision";

    [TestMethod]
    public async Task DecisionTrace_DoesNotChangePackageOutput()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryContextStore();
        await store.SaveAsync(MakeItem("item-a", "决策 trace 不改变输出 A", ["decision"], now));
        await store.SaveAsync(MakeItem("item-b", "决策 trace 不改变输出 B", ["decision"], now));

        // 无 decision trace store 的构建器
        var builderWithoutTrace = new BasicContextPackageBuilder(store);
        // 有 decision trace store 的构建器
        var decisionStore = new InMemoryDecisionTraceStore();
        var builderWithTrace = new BasicContextPackageBuilder(
            store, null, null, null, null, null, null, null, decisionStore);

        var request = new ContextPackageRequest
        {
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            QueryText = "决策 trace",
            TokenBudget = 1000
        };

        var resultWithout = await builderWithoutTrace.BuildDetailedAsync(request);
        var resultWith = await builderWithTrace.BuildDetailedAsync(request);

        // 正式 package 输出应完全一致
        Assert.AreEqual(resultWithout.Package.EstimatedTokens, resultWith.Package.EstimatedTokens);
        Assert.AreEqual(resultWithout.Package.Sections.Count, resultWith.Package.Sections.Count);
        Assert.AreEqual(resultWithout.SelectedItems.Count, resultWith.SelectedItems.Count);
        Assert.AreEqual(resultWithout.DroppedItems.Count, resultWith.DroppedItems.Count);

        for (var i = 0; i < resultWithout.SelectedItems.Count; i++)
        {
            Assert.AreEqual(resultWithout.SelectedItems[i].ItemId, resultWith.SelectedItems[i].ItemId);
        }

        // decision trace 应已写入
        var traces = await decisionStore.QueryRecentAsync(WorkspaceId, CollectionId, 10);
        Assert.AreEqual(1, traces.Count);
        Assert.AreEqual(ContextDecisionSource.Package, traces[0].Source);
    }

    [TestMethod]
    public async Task DecisionTrace_SelectedDroppedProjection_PreservesIds()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryContextStore();
        await store.SaveAsync(MakeItem("keep-1", "保留条目 1", ["keep"], now));
        await store.SaveAsync(MakeItem("keep-2", "保留条目 2", ["keep"], now));
        await store.SaveAsync(MakeItem("drop-1", "丢弃条目 1", ["drop"], now));

        var decisionStore = new InMemoryDecisionTraceStore();
        var builder = new BasicContextPackageBuilder(
            store, null, null, null, null, null, null, null, decisionStore);

        var request = new ContextPackageRequest
        {
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            QueryText = "保留",
            TokenBudget = 500,
            RequiredTags = ["keep"]
        };

        var result = await builder.BuildDetailedAsync(request);

        // 直接投影校验
        var record = ContextDecisionProjector.ProjectPackage(result);

        var selectedIds = result.SelectedItems.Select(s => s.ItemId).ToHashSet();
        var droppedIds = result.DroppedItems.Select(d => d.ItemId).ToHashSet();

        var projectedSelectedIds = record.Candidates
            .Where(c => c.Outcome == ContextDecisionCandidateOutcome.Selected)
            .Select(c => c.ItemId).ToHashSet();
        var projectedDroppedIds = record.Candidates
            .Where(c => c.Outcome == ContextDecisionCandidateOutcome.Dropped)
            .Select(c => c.ItemId).ToHashSet();

        Assert.AreEqual(selectedIds.Count, projectedSelectedIds.Count);
        Assert.IsTrue(selectedIds.SetEquals(projectedSelectedIds));
        Assert.AreEqual(droppedIds.Count, projectedDroppedIds.Count);
        Assert.IsTrue(droppedIds.SetEquals(projectedDroppedIds));

        // 所有投影 ItemId 不应为空
        Assert.IsTrue(record.Candidates.All(c => !string.IsNullOrWhiteSpace(c.ItemId)));

        // 计数一致
        Assert.AreEqual(result.SelectedItems.Count, record.Outcome.SelectedCount);
        Assert.AreEqual(result.DroppedItems.Count, record.Outcome.DroppedCount);
    }

    [TestMethod]
    public async Task RetrievalDecisionTrace_DoesNotEnableFormalVector()
    {
        var now = DateTimeOffset.UtcNow;
        var contextStore = new InMemoryContextStore();
        await contextStore.SaveAsync(MakeItem("retrieval-1", "检索决策 trace 不启 formal vector", ["retrieval"], now));

        var decisionStore = new InMemoryDecisionTraceStore();
        var retriever = new HybridContextRetriever(
            contextStore,
            traceStore: null,
            decisionTraceStore: decisionStore);

        var result = await retriever.RetrieveAsync(new ContextRetrievalRequest
        {
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            QueryText = "检索决策 trace",
            IncludeKeywordRecall = true,
            IncludeVectorRecall = false,
            IncludeRelationExpansion = false,
            IncludeWorkingMemory = false,
            IncludeStableMemory = false,
            CandidateTake = 10,
            TopK = 10,
            TokenBudget = 1000
        });

        // 检索结果不受影响
        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.SelectedItems.Count > 0);

        // decision trace 已写入
        var traces = await decisionStore.QueryRecentAsync(WorkspaceId, CollectionId, 10);
        Assert.AreEqual(1, traces.Count);
        var record = traces[0];

        // 非激活契约：所有标志位恒为 false
        Assert.IsFalse(record.Risk.FormalRetrievalAllowed);
        Assert.IsFalse(record.Risk.FormalVectorStoreBinding);
        Assert.IsFalse(record.Risk.RuntimeSwitchAllowed);
        Assert.IsFalse(record.Risk.FormalPackageWrite);
        Assert.IsFalse(record.Risk.PackageOutputChanged);
        Assert.IsFalse(record.Risk.PackingPolicyChanged);
        Assert.IsFalse(record.Risk.GraphApplyFormalChanged);
        Assert.IsFalse(record.Risk.LearningPolicyApplied);
        Assert.IsFalse(record.Risk.ModelTrainingStarted);

        Assert.AreEqual(ContextDecisionSource.Retrieval, record.Source);
    }

    [TestMethod]
    public async Task DecisionTrace_NoPackingPolicyChange()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryContextStore();
        await store.SaveAsync(MakeItem("policy-1", "策略不变测试", ["policy"], now));

        var decisionStore = new InMemoryDecisionTraceStore();
        var policy = new ContextPackagePolicy
        {
            Id = "test-policy",
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            TokenBudget = 800
        };

        var builder = new BasicContextPackageBuilder(
            store, null, null, null, null, null, null, null, decisionStore);

        var request = new ContextPackageRequest
        {
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            QueryText = "策略不变",
            TokenBudget = 800,
            Policy = policy
        };

        var result = await builder.BuildDetailedAsync(request);

        // PackingPolicyChanged 恒为 false
        Assert.IsFalse(result.Metadata.TryGetValue("packingPolicyChanged", out var v) && bool.TryParse(v, out var changed) && changed);

        var traces = await decisionStore.QueryRecentAsync(WorkspaceId, CollectionId, 10);
        Assert.AreEqual(1, traces.Count);
        Assert.IsFalse(traces[0].Risk.PackingPolicyChanged);
    }

    [TestMethod]
    public async Task DecisionTrace_NoFormalRetrievalAllowedChange()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryContextStore();
        await store.SaveAsync(MakeItem("formal-1", "formal retrieval 不变测试", ["formal"], now));

        var decisionStore = new InMemoryDecisionTraceStore();
        var builder = new BasicContextPackageBuilder(
            store, null, null, null, null, null, null, null, decisionStore);

        var result = await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            QueryText = "formal retrieval",
            TokenBudget = 500
        });

        // FormalRetrievalAllowed 恒为 false（非激活契约）
        var traces = await decisionStore.QueryRecentAsync(WorkspaceId, CollectionId, 10);
        Assert.AreEqual(1, traces.Count);
        Assert.IsFalse(traces[0].Risk.FormalRetrievalAllowed);

        // 审计 runner 校验非激活契约
        var violations = ContextDecisionAuditRunner.AuditNonActivationContract(traces[0].Risk);
        Assert.AreEqual(0, violations.Count);
    }

    [TestMethod]
    public async Task DecisionAuditRunner_GeneratesReportWithContractHolding()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryContextStore();
        await store.SaveAsync(MakeItem("audit-1", "审计报告生成测试", ["audit"], now));
        await store.SaveAsync(MakeItem("audit-2", "审计报告生成测试 2", ["audit"], now));

        var decisionStore = new InMemoryDecisionTraceStore();
        var builder = new BasicContextPackageBuilder(
            store, null, null, null, null, null, null, null, decisionStore);

        await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            QueryText = "审计报告",
            TokenBudget = 500
        });

        var runner = new ContextDecisionAuditRunner(decisionStore);
        var report = await runner.RunAsync(WorkspaceId, CollectionId, 10);

        Assert.AreEqual(1, report.TraceCount);
        Assert.AreEqual(1, report.PackageDecisionCount);
        Assert.AreEqual(0, report.RetrievalDecisionCount);
        Assert.IsTrue(report.NonActivationContractHolds);
        Assert.AreEqual(0, report.ContractViolations.Count);
        Assert.IsTrue(report.ProjectionPreservesIds);
    }

    [TestMethod]
    public async Task DecisionAuditRunner_WritesJsonAndMarkdownFiles()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryContextStore();
        await store.SaveAsync(MakeItem("write-1", "报告写入文件测试", ["write"], now));

        var decisionStore = new InMemoryDecisionTraceStore();
        var builder = new BasicContextPackageBuilder(
            store, null, null, null, null, null, null, null, decisionStore);

        await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            QueryText = "写入文件",
            TokenBudget = 500
        });

        var tempDir = Path.Combine(Path.GetTempPath(), "contextcore-decision-audit-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var runner = new ContextDecisionAuditRunner(decisionStore);
            var report = await runner.RunAndWriteAsync(WorkspaceId, CollectionId, tempDir, 10);

            Assert.IsTrue(report.NonActivationContractHolds);

            var files = Directory.GetFiles(tempDir);
            Assert.IsTrue(files.Any(f => f.EndsWith(".json")));
            Assert.IsTrue(files.Any(f => f.EndsWith(".md")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task DecisionAudit_WithoutEvidenceProvider_ReportsEvidenceNotComplete()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryContextStore();
        await store.SaveAsync(MakeItem("ev-none-1", "无证据提供者测试", ["evidence"], now));

        var decisionStore = new InMemoryDecisionTraceStore();
        var builder = new BasicContextPackageBuilder(
            store, null, null, null, null, null, null, null, decisionStore);

        await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            QueryText = "无证据",
            TokenBudget = 500
        });

        // 无证据提供者：EvidenceStatus=NotConfigured（替代旧的 EvidenceComplete=false 二态语义）
        var runner = new ContextDecisionAuditRunner(decisionStore);
        var report = await runner.RunAsync(WorkspaceId, CollectionId, 10);

        Assert.IsFalse(report.EvidenceComplete);
        Assert.AreEqual(EvidenceAuditStatus.NotConfigured, report.EvidenceStatus);
        Assert.AreEqual(0, report.EvidenceResolvedCount);
        Assert.AreEqual(0, report.EvidenceMissingCount);
        Assert.AreEqual(0, report.EvidenceIncompleteDecisionIds.Count);
    }

    [TestMethod]
    public async Task DecisionAudit_WithIncompleteEvidenceProvider_ReportsIncompleteWithMissing()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryContextStore();
        await store.SaveAsync(MakeItem("ev-null-1", "incomplete 证据提供者测试", ["evidence"], now));

        var decisionStore = new InMemoryDecisionTraceStore();
        var builder = new BasicContextPackageBuilder(
            store, null, null, null, null, null, null, null, decisionStore);

        await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            QueryText = "incomplete 证据",
            TokenBudget = 500
        });

        // FakeIncompleteEvidenceProvider：标记所有候选为 missing（替代已删除的 NullDecisionEvidenceProvider）
        var runner = new ContextDecisionAuditRunner(decisionStore, new FakeIncompleteEvidenceProvider());
        var report = await runner.RunAsync(WorkspaceId, CollectionId, 10);

        Assert.IsFalse(report.EvidenceComplete);
        Assert.AreEqual(EvidenceAuditStatus.Incomplete, report.EvidenceStatus);
        Assert.AreEqual(0, report.EvidenceResolvedCount);
        Assert.IsTrue(report.EvidenceMissingCount > 0);
        Assert.AreEqual(1, report.EvidenceIncompleteDecisionIds.Count);
    }

    [TestMethod]
    public async Task DecisionAudit_WithFailingEvidenceProvider_ReportsFailed()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryContextStore();
        await store.SaveAsync(MakeItem("ev-fail-1", "failing 证据提供者测试", ["evidence"], now));

        var decisionStore = new InMemoryDecisionTraceStore();
        var builder = new BasicContextPackageBuilder(
            store, null, null, null, null, null, null, null, decisionStore);

        await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            QueryText = "failing 证据",
            TokenBudget = 500
        });

        var runner = new ContextDecisionAuditRunner(decisionStore, new FakeFailingEvidenceProvider());
        var report = await runner.RunAsync(WorkspaceId, CollectionId, 10);

        Assert.IsFalse(report.EvidenceComplete);
        Assert.AreEqual(EvidenceAuditStatus.Failed, report.EvidenceStatus);
    }

    [TestMethod]
    public async Task DecisionAudit_WithCompleteEvidenceProvider_ReportsComplete()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryContextStore();
        await store.SaveAsync(MakeItem("ev-complete-1", "完整证据测试", ["evidence"], now));

        var decisionStore = new InMemoryDecisionTraceStore();
        var builder = new BasicContextPackageBuilder(
            store, null, null, null, null, null, null, null, decisionStore);

        await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            QueryText = "完整证据",
            TokenBudget = 500
        });

        // FakeEvidenceProvider：为所有候选返回完整证据
        var runner = new ContextDecisionAuditRunner(decisionStore, new FakeCompleteEvidenceProvider());
        var report = await runner.RunAsync(WorkspaceId, CollectionId, 10);

        Assert.IsTrue(report.EvidenceComplete);
        Assert.AreEqual(EvidenceAuditStatus.Complete, report.EvidenceStatus);
        Assert.IsTrue(report.EvidenceResolvedCount > 0);
        Assert.AreEqual(0, report.EvidenceMissingCount);
        Assert.AreEqual(0, report.EvidenceIncompleteDecisionIds.Count);
    }

    [TestMethod]
    public async Task DecisionAudit_WithCompleteEvidenceProvider_IncludesEvidenceInMarkdown()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryContextStore();
        await store.SaveAsync(MakeItem("ev-md-1", "证据 markdown 测试", ["evidence"], now));

        var decisionStore = new InMemoryDecisionTraceStore();
        var builder = new BasicContextPackageBuilder(
            store, null, null, null, null, null, null, null, decisionStore);

        await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            QueryText = "markdown 证据",
            TokenBudget = 500
        });

        var tempDir = Path.Combine(Path.GetTempPath(), "contextcore-decision-evidence-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var runner = new ContextDecisionAuditRunner(decisionStore, new FakeCompleteEvidenceProvider());
            var report = await runner.RunAndWriteAsync(WorkspaceId, CollectionId, tempDir, 10);

            var mdPath = Directory.GetFiles(tempDir).First(f => f.EndsWith(".md"));
            var markdown = await File.ReadAllTextAsync(mdPath);

            StringAssert.Contains(markdown, "## Evidence Completeness");
            StringAssert.Contains(markdown, "EvidenceComplete");
            StringAssert.Contains(markdown, "EvidenceResolvedCount");
            StringAssert.Contains(markdown, "EvidenceMissingCount");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    /// <summary>
    /// Fake 证据提供者：为所有候选返回完整证据（IsComplete=true）。
    /// </summary>
    private sealed class FakeCompleteEvidenceProvider : IDecisionEvidenceProvider
    {
        public Task<DecisionEvidenceResult> ResolveEvidenceAsync(
            ContextDecisionRecord record,
            CancellationToken cancellationToken = default)
        {
            var evidence = record.Candidates
                .Where(c => !string.IsNullOrWhiteSpace(c.ItemId))
                .Select(c => new DecisionEvidence
                {
                    ItemId = c.ItemId,
                    PrimaryRationale = c.Reason.Length > 0 ? c.Reason : "fake-rationale",
                    Confidence = 1.0,
                    Provenance = "fake-complete-provider"
                })
                .ToList();

            return Task.FromResult(new DecisionEvidenceResult
            {
                DecisionId = record.DecisionId,
                Evidence = evidence,
                IsComplete = true,
                MissingItemIds = Array.Empty<string>(),
                ResolvedAt = DateTimeOffset.UtcNow
            });
        }
    }

    /// <summary>
    /// Fake 证据提供者：返回空证据列表，所有候选标记为 missing（IsComplete=false）。
    /// 替代已删除的 NullDecisionEvidenceProvider 用于测试 Incomplete 路径。
    /// </summary>
    private sealed class FakeIncompleteEvidenceProvider : IDecisionEvidenceProvider
    {
        public Task<DecisionEvidenceResult> ResolveEvidenceAsync(
            ContextDecisionRecord record,
            CancellationToken cancellationToken = default)
        {
            var missingItemIds = record.Candidates
                .Select(c => c.ItemId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Task.FromResult(new DecisionEvidenceResult
            {
                DecisionId = record.DecisionId,
                Evidence = Array.Empty<DecisionEvidence>(),
                IsComplete = false,
                MissingItemIds = missingItemIds,
                ResolvedAt = DateTimeOffset.UtcNow
            });
        }
    }

    /// <summary>
    /// Fake 证据提供者：始终抛出异常，用于测试 Failed 路径。
    /// </summary>
    private sealed class FakeFailingEvidenceProvider : IDecisionEvidenceProvider
    {
        public Task<DecisionEvidenceResult> ResolveEvidenceAsync(
            ContextDecisionRecord record,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("FakeFailingEvidenceProvider: simulated evidence resolution failure");
        }
    }

    private static ContextItem MakeItem(
        string id,
        string content,
        IReadOnlyList<string> tags,
        DateTimeOffset now)
    {
        return new ContextItem
        {
            Id = id,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Type = "note",
            Content = content,
            ContentFormat = ContextContentFormat.PlainText,
            Tags = tags,
            SourceRefs = [$"source:{id}"],
            Importance = 0.8,
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
