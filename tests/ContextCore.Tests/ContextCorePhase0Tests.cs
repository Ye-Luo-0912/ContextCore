using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Jobs;
using ContextCore.Core.Services.Graph;
using ContextCore.Service;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>覆盖 Phase 0 后台任务与压缩链路的基础行为。</summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ContextCorePhase0Tests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task CompressionJobProcessor_ShouldGenerateSummaryIndexAndRelations()
    {
        var contextStore = new InMemoryContextStore();
        var index = new InMemoryContextIndex();
        var relationStore = new InMemoryRelationStore();
        var processor = new CompressionJobProcessor(
            contextStore,
            index,
            new MockContextCompressor(),
            relationStore,
            new RelationProjector(),
            new RelationProjectionWriter(relationStore, new RelationProjectorOutputValidator(new RelationTypeRegistry(), new RelationTypeNormalizer())));

        await contextStore.SaveAsync(new ContextItem
        {
            Id = "source-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "note",
            Title = "Source",
            Content = "Important source content for compression.",
            Tags = ["phase0"],
            Importance = 1.0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        await processor.ProcessAsync(new ContextJob
        {
            JobId = "job-compress-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Kind = ContextJobKind.Compression,
            PayloadJson = JsonSerializer.Serialize(new CompressionRequest
            {
                OperationId = "job-compress-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Options = new CompressionOptions
                {
                    GenerateIndexHints = true,
                    PreserveSourceRefs = true
                }
            }, JsonOptions),
            CreatedAt = DateTimeOffset.UtcNow
        });

        var summaries = await contextStore.QueryAsync(new ContextQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Types = ["summary"],
            IncludeContent = true
        });
        var indexEntries = await index.SearchAsync(new IndexQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Key = "phase0"
        });
        var relations = await relationStore.QueryAsync(new ContextRelationQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = "job-compress-1-summary",
            Take = int.MaxValue
        });

        Assert.AreEqual(1, summaries.Count);
        Assert.IsTrue(summaries[0].Content.Contains("Important source content", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(indexEntries.Count > 0);
        Assert.IsTrue(relations.Any(relation => relation.TargetId == "source-1"));
    }

    [TestMethod]
    public async Task ContextJobDispatcher_ShouldDispatchRegisteredProcessor()
    {
        var processor = new RecordingJobProcessor(ContextJobKind.Custom);
        var dispatcher = new ContextJobDispatcher([processor]);
        var job = new ContextJob
        {
            JobId = "job-custom",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Kind = ContextJobKind.Custom,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await dispatcher.DispatchAsync(job);

        Assert.AreEqual("job-custom", processor.ProcessedJobId);
    }

    [TestMethod]
    public async Task InMemoryJobQueue_ShouldExposeWaitingRetryBeforeNextAttempt()
    {
        var queue = new InMemoryJobQueue();

        await AssertJobQueueRetryLifecycleAsync(queue, queue);
    }

    [TestMethod]
    public async Task FileContextJobQueue_ShouldExposeWaitingRetryBeforeNextAttempt()
    {
        var rootPath = Path.Combine(AppContext.BaseDirectory, ".test-data", Guid.NewGuid().ToString("N"));

        try
        {
            var queue = new FileContextJobQueue(new FileStorageOptions { RootPath = rootPath });

            await AssertJobQueueRetryLifecycleAsync(queue, queue);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void CompressionProviderOptions_ShouldDefaultToNonMockProvider()
    {
        var options = new CompressionProviderOptions();

        Assert.AreEqual("llm", options.Provider);
    }

    private static async Task AssertJobQueueRetryLifecycleAsync(
        IContextJobQueue queue,
        IContextJobQueryStore queryStore)
    {
        await queue.EnqueueAsync(new ContextJob
        {
            JobId = "job-retry",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Kind = ContextJobKind.Compression,
            PayloadJson = "{}",
            MaxRetryCount = 1,
            CreatedAt = DateTimeOffset.UtcNow
        });

        var firstAttempt = await queue.DequeueAsync();
        Assert.IsNotNull(firstAttempt);
        Assert.AreEqual(ContextJobState.Running, firstAttempt!.State);

        await queue.NackAsync(firstAttempt.JobId, "first failure");

        var waiting = await queryStore.QueryAsync(new ContextJobQuery
        {
            State = ContextJobState.WaitingRetry,
            Take = 10
        });
        Assert.AreEqual(1, waiting.Count);
        Assert.AreEqual(1, waiting[0].RetryCount);
        Assert.AreEqual("first failure", waiting[0].ErrorMessage);

        var retryAttempt = await queue.DequeueAsync();
        Assert.IsNotNull(retryAttempt);
        Assert.AreEqual(ContextJobState.Running, retryAttempt!.State);
        Assert.AreEqual(1, retryAttempt.RetryCount);

        await queue.NackAsync(retryAttempt.JobId, "final failure");

        var failed = await queryStore.QueryAsync(new ContextJobQuery
        {
            State = ContextJobState.Failed,
            Take = 10
        });
        Assert.AreEqual(1, failed.Count);
        Assert.AreEqual(2, failed[0].RetryCount);
        Assert.AreEqual("final failure", failed[0].ErrorMessage);
        Assert.IsNotNull(failed[0].CompletedAt);
    }

    /// <summary>
    /// #6: Ack/Nack 原子化（CAS）— 仅当 job 处于 Running 时才转换状态。
    /// 验证过期的 Ack/Nack 不会还原终态或干扰进行中的执行。
    /// </summary>
    [TestMethod]
    public async Task JobQueue_AckNack_Cas_OnlyTransitionsFromRunning()
    {
        var queue = new InMemoryJobQueue();
        var queryStore = (IContextJobQueryStore)queue;

        await queue.EnqueueAsync(new ContextJob
        {
            JobId = "job-cas",
            WorkspaceId = "ws",
            CollectionId = "col",
            Kind = ContextJobKind.Custom,
            PayloadJson = "{}",
            MaxRetryCount = 3,
            CreatedAt = DateTimeOffset.UtcNow
        });

        // 1. Ack on Queued job (not Running) → no-op
        await queue.AckAsync("job-cas");
        var queuedJobs = await queryStore.QueryAsync(new ContextJobQuery { Take = 10 });
        Assert.AreEqual(ContextJobState.Queued, queuedJobs.Single(j => j.JobId == "job-cas").State,
            "Ack on Queued job 应为 no-op，状态不变");

        // 2. Nack on Queued job (not Running) → no-op
        await queue.NackAsync("job-cas", "stale nack before dequeue");
        queuedJobs = await queryStore.QueryAsync(new ContextJobQuery { Take = 10 });
        Assert.AreEqual(ContextJobState.Queued, queuedJobs.Single(j => j.JobId == "job-cas").State,
            "Nack on Queued job 应为 no-op，状态不变");
        Assert.AreEqual(0, queuedJobs.Single(j => j.JobId == "job-cas").RetryCount,
            "Nack on Queued job 不应增加 RetryCount");

        // 3. Dequeue → Running → Ack → Succeeded
        var dequeued = await queue.DequeueAsync();
        Assert.AreEqual(ContextJobState.Running, dequeued!.State);
        await queue.AckAsync("job-cas");
        var succeededJobs = await queryStore.QueryAsync(new ContextJobQuery { Take = 10 });
        Assert.AreEqual(ContextJobState.Succeeded, succeededJobs.Single(j => j.JobId == "job-cas").State);

        // 4. Nack on Succeeded job (not Running) → no-op（过期 Nack 不还原终态）
        await queue.NackAsync("job-cas", "stale nack after success");
        succeededJobs = await queryStore.QueryAsync(new ContextJobQuery { Take = 10 });
        Assert.AreEqual(ContextJobState.Succeeded, succeededJobs.Single(j => j.JobId == "job-cas").State,
            "Nack on Succeeded job 应为 no-op，不应还原终态");
        Assert.AreEqual(0, succeededJobs.Single(j => j.JobId == "job-cas").RetryCount,
            "Nack on Succeeded job 不应增加 RetryCount");
    }

    /// <summary>
    /// #6: Double-Ack 幂等 — 第二次 Ack 不改变状态（job 已不是 Running）。
    /// </summary>
    [TestMethod]
    public async Task JobQueue_DoubleAck_IsNoOp()
    {
        var queue = new InMemoryJobQueue();
        var queryStore = (IContextJobQueryStore)queue;

        await queue.EnqueueAsync(new ContextJob
        {
            JobId = "job-double-ack",
            WorkspaceId = "ws",
            CollectionId = "col",
            Kind = ContextJobKind.Custom,
            PayloadJson = "{}",
            MaxRetryCount = 3,
            CreatedAt = DateTimeOffset.UtcNow
        });

        var dequeued = await queue.DequeueAsync();
        Assert.AreEqual(ContextJobState.Running, dequeued!.State);

        await queue.AckAsync("job-double-ack");
        await queue.AckAsync("job-double-ack"); // stale second Ack

        var jobs = await queryStore.QueryAsync(new ContextJobQuery { Take = 10 });
        Assert.AreEqual(ContextJobState.Succeeded, jobs.Single(j => j.JobId == "job-double-ack").State,
            "Double-Ack 应幂等，第二次 Ack 为 no-op");
    }

    /// <summary>
    /// #6: Double-Nack（未重新 dequeue）— 第二次 Nack 不改变状态（job 已不是 Running）。
    /// </summary>
    [TestMethod]
    public async Task JobQueue_DoubleNack_WithoutRedequeue_IsNoOp()
    {
        var queue = new InMemoryJobQueue();
        var queryStore = (IContextJobQueryStore)queue;

        await queue.EnqueueAsync(new ContextJob
        {
            JobId = "job-double-nack",
            WorkspaceId = "ws",
            CollectionId = "col",
            Kind = ContextJobKind.Custom,
            PayloadJson = "{}",
            MaxRetryCount = 3,
            CreatedAt = DateTimeOffset.UtcNow
        });

        var dequeued = await queue.DequeueAsync();
        Assert.AreEqual(ContextJobState.Running, dequeued!.State);

        await queue.NackAsync("job-double-nack", "first failure");
        var jobs = await queryStore.QueryAsync(new ContextJobQuery { Take = 10 });
        Assert.AreEqual(ContextJobState.WaitingRetry, jobs.Single(j => j.JobId == "job-double-nack").State);
        Assert.AreEqual(1, jobs.Single(j => j.JobId == "job-double-nack").RetryCount);

        // Stale second Nack without re-dequeue: job is now WaitingRetry, not Running → no-op
        await queue.NackAsync("job-double-nack", "stale second nack");
        jobs = await queryStore.QueryAsync(new ContextJobQuery { Take = 10 });
        Assert.AreEqual(ContextJobState.WaitingRetry, jobs.Single(j => j.JobId == "job-double-nack").State,
            "Double-Nack 未重新 dequeue 时应为 no-op，状态不变");
        Assert.AreEqual(1, jobs.Single(j => j.JobId == "job-double-nack").RetryCount,
            "Double-Nack 未重新 dequeue 时不应增加 RetryCount");
    }

    private sealed class RecordingJobProcessor : IContextJobProcessor
    {
        public RecordingJobProcessor(ContextJobKind kind)
        {
            Kind = kind;
        }

        public ContextJobKind Kind { get; }

        public string? ProcessedJobId { get; private set; }

        public Task ProcessAsync(ContextJob job, CancellationToken cancellationToken = default)
        {
            ProcessedJobId = job.JobId;
            return Task.CompletedTask;
        }
    }
}
