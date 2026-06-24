using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Jobs;
using ContextCore.Service;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>覆盖 Phase 0 后台任务与压缩链路的基础行为。</summary>
[TestClass]
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
            new RelationBuilder());

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
        var relations = await relationStore.QueryBySourceAsync(
            "workspace-test",
            "collection-test",
            "job-compress-1-summary");

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
    public async Task UnavailableContextCompressor_ShouldFailInsteadOfUsingMockOutput()
    {
        var compressor = new UnavailableContextCompressor("llm");

        var response = await compressor.CompressAsync(new CompressionRequest
        {
            OperationId = "operation-llm",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test"
        });

        Assert.AreEqual(CompressionStatus.Failed, response.Status);
        Assert.AreEqual(0, response.GeneratedItems.Count);
        Assert.AreEqual("CompressionProviderUnavailable", response.Errors[0].Code);
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
