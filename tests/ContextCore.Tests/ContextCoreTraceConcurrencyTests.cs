using System.Collections.Concurrent;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.Learning.V14_0;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// TRACE-01 并发串线测试。
/// 验证 BasicContextPackageBuilder 在 50-100 并发请求下不产生 trace 串线（cross-talk）。
/// AsyncLocal 请求级上下文确保每个请求的 OperationId/RequestId 隔离。
/// </summary>
[TestClass]
[TestCategory("Concurrency")]
public sealed class ContextCoreTraceConcurrencyTests
{
    /// <summary>
    /// 捕获所有 trace 写入的 sink，用于验证 OperationId/RequestId 不串线。
    /// </summary>
    private sealed class CapturingTraceSink : IRuntimeCandidateTraceSink
    {
        private readonly ConcurrentBag<RuntimeCandidateTraceRow> _rows = new();

        public bool Enabled => true;
        public int WriteCount => _rows.Count;
        public IReadOnlyCollection<RuntimeCandidateTraceRow> Rows => _rows;

        public void Write(RuntimeCandidateTraceRow row) => _rows.Add(row);
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [TestMethod]
    public async Task BuildDetailedAsync_ConcurrentRequests_ShouldNotCrossTalkTraceContext()
    {
        // 准备共享数据
        var store = new InMemoryContextStore();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 20; i++)
        {
            await store.SaveAsync(new ContextItem
            {
                Id = $"item-{i}",
                WorkspaceId = "ws-concurrent",
                CollectionId = "col-concurrent",
                Type = "note",
                Content = $"并发测试条目 {i}",
                ContentFormat = ContextContentFormat.PlainText,
                Tags = ["concurrent"],
                Importance = 0.8,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        var sink = new CapturingTraceSink();
        // 使用共享的 builder（Singleton 模拟），验证 AsyncLocal 隔离
        var builder = new BasicContextPackageBuilder(
            store, null, null, null, null, null, null, null, null, sink);

        const int requestCount = 80;
        var operationIds = new string[requestCount];
        var requestIds = new string[requestCount];

        // 并发发起 80 个构建请求
        var tasks = new Task[requestCount];
        for (var i = 0; i < requestCount; i++)
        {
            var index = i;
            operationIds[index] = $"op-concurrent-{index}";
            requestIds[index] = $"req-concurrent-{index}";
            tasks[index] = Task.Run(async () =>
            {
                await builder.BuildDetailedAsync(new ContextPackageRequest
                {
                    WorkspaceId = "ws-concurrent",
                    CollectionId = "col-concurrent",
                    QueryText = "并发测试",
                    TokenBudget = 500,
                    OperationId = operationIds[index],
                    RequestId = requestIds[index]
                });
            });
        }

        await Task.WhenAll(tasks);

        // 验证：所有 trace 行的 OperationId/RequestId 应匹配某个请求
        var rows = sink.Rows;
        Assert.IsTrue(rows.Count > 0, "应至少捕获到一条 trace 行");

        // 检查每条 trace 行的 OperationId 都在预期的集合中
        var expectedOpIds = operationIds.ToHashSet();
        var expectedReqIds = requestIds.ToHashSet();

        foreach (var row in rows)
        {
            Assert.IsTrue(expectedOpIds.Contains(row.OperationId),
                $"trace 行 OperationId={row.OperationId} 不在预期集合中，存在串线");
            Assert.IsTrue(expectedReqIds.Contains(row.RequestId),
                $"trace 行 RequestId={row.RequestId} 不在预期集合中，存在串线");
        }

        // 检查 OperationId 和 RequestId 的配对一致性
        // 同一条 trace 行的 OperationId 和 RequestId 应来自同一个请求
        var opToReq = new Dictionary<string, string>();
        for (var i = 0; i < requestCount; i++)
        {
            opToReq[operationIds[i]] = requestIds[i];
        }

        foreach (var row in rows)
        {
            Assert.AreEqual(opToReq[row.OperationId], row.RequestId,
                $"trace 行 OperationId={row.OperationId} 与 RequestId={row.RequestId} 不配对，存在串线");
        }
    }

    [TestMethod]
    public async Task BuildDetailedAsync_ConcurrentRequests_ShouldProduceCorrectPackageOutput()
    {
        // 验证并发构建不改变 package 输出
        var store = new InMemoryContextStore();
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(new ContextItem
        {
            Id = "shared-item",
            WorkspaceId = "ws-output",
            CollectionId = "col-output",
            Type = "note",
            Content = "并发输出验证",
            ContentFormat = ContextContentFormat.PlainText,
            Tags = ["output"],
            Importance = 0.9,
            CreatedAt = now,
            UpdatedAt = now
        });

        var builder = new BasicContextPackageBuilder(store);

        const int requestCount = 50;
        var tasks = new Task<ContextPackageBuildResult>[requestCount];
        for (var i = 0; i < requestCount; i++)
        {
            tasks[i] = Task.Run(() => builder.BuildDetailedAsync(new ContextPackageRequest
            {
                WorkspaceId = "ws-output",
                CollectionId = "col-output",
                QueryText = "并发输出",
                TokenBudget = 500,
                OperationId = $"op-output-{i}",
                RequestId = $"req-output-{i}"
            }));
        }

        var results = await Task.WhenAll(tasks);

        // 所有结果应一致（相同输入 → 相同输出）
        var firstTokenCount = results[0].Package.EstimatedTokens;
        var firstSectionCount = results[0].Package.Sections.Count;
        var firstSelectedCount = results[0].SelectedItems.Count;

        for (var i = 1; i < requestCount; i++)
        {
            Assert.AreEqual(firstTokenCount, results[i].Package.EstimatedTokens,
                $"请求 {i} 的 EstimatedTokens 不一致");
            Assert.AreEqual(firstSectionCount, results[i].Package.Sections.Count,
                $"请求 {i} 的 Sections.Count 不一致");
            Assert.AreEqual(firstSelectedCount, results[i].SelectedItems.Count,
                $"请求 {i} 的 SelectedItems.Count 不一致");
        }
    }
}
