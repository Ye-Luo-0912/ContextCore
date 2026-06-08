using ContextCore.Abstractions;

namespace ContextCore.Embedding;

/// <summary>执行 embedding 请求并将结果写入向量存储的任务服务。</summary>
public sealed class EmbeddingJobService : IEmbeddingJobService
{
    private readonly IEmbeddingProvider _provider;
    private readonly IVectorStore _vectorStore;
    private readonly List<EmbeddingJob> _jobs = new();
    private readonly object _gate = new();

    public EmbeddingJobService(
        IEmbeddingProvider provider,
        IVectorStore vectorStore)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _vectorStore = vectorStore ?? throw new ArgumentNullException(nameof(vectorStore));
    }

    public Task<EmbeddingJob> EnqueueAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;
        var job = new EmbeddingJob
        {
            JobId = string.IsNullOrWhiteSpace(request.OperationId)
                ? Guid.NewGuid().ToString("N")
                : request.OperationId,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            State = EmbeddingJobState.Queued,
            Request = request,
            CreatedAt = now
        };

        lock (_gate)
        {
            _jobs.Add(job);
        }

        return Task.FromResult(job);
    }

    public async Task<EmbeddingJob> ProcessAsync(
        EmbeddingJob job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();

        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            var result = await _provider.EmbedAsync(job.Request, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return StoreProcessed(job, new EmbeddingJob
                {
                    JobId = job.JobId,
                    WorkspaceId = job.WorkspaceId,
                    CollectionId = job.CollectionId,
                    State = EmbeddingJobState.Failed,
                    Request = job.Request,
                    Result = result,
                    ErrorMessage = result.ErrorMessage,
                    CreatedAt = job.CreatedAt,
                    StartedAt = startedAt,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Metadata = new Dictionary<string, string>(job.Metadata)
                });
            }

            foreach (var vector in result.Vectors)
            {
                var input = job.Request.Inputs.FirstOrDefault(item =>
                    string.Equals(item.Id, vector.InputId, StringComparison.OrdinalIgnoreCase));
                var contentHash = vector.Metadata.GetValueOrDefault("contentHash")
                    ?? (input is null
                        ? string.Empty
                        : EmbeddingContentHasher.HashInput(input, job.Request.InputKind, result.ModelName));

                await _vectorStore.UpsertAsync(new VectorRecord
                {
                    Id = $"{job.JobId}-{vector.InputId}",
                    WorkspaceId = job.WorkspaceId,
                    CollectionId = job.CollectionId,
                    SourceId = vector.SourceRef,
                    SourceKind = job.Request.InputKind.ToString(),
                    ModelName = result.ModelName,
                    Dimensions = result.Dimensions,
                    Vector = vector.Values,
                    ContentHash = contentHash,
                    Tags = input?.Tags ?? Array.Empty<string>(),
                    Metadata = new Dictionary<string, string>(vector.Metadata),
                    CreatedAt = startedAt,
                    UpdatedAt = DateTimeOffset.UtcNow
                }, cancellationToken).ConfigureAwait(false);
            }

            return StoreProcessed(job, new EmbeddingJob
            {
                JobId = job.JobId,
                WorkspaceId = job.WorkspaceId,
                CollectionId = job.CollectionId,
                State = EmbeddingJobState.Succeeded,
                Request = job.Request,
                Result = result,
                CreatedAt = job.CreatedAt,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>(job.Metadata)
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StoreProcessed(job, new EmbeddingJob
            {
                JobId = job.JobId,
                WorkspaceId = job.WorkspaceId,
                CollectionId = job.CollectionId,
                State = EmbeddingJobState.Cancelled,
                Request = job.Request,
                ErrorMessage = "Embedding 任务已取消。",
                CreatedAt = job.CreatedAt,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>(job.Metadata)
            });
        }
        catch (Exception ex)
        {
            return StoreProcessed(job, new EmbeddingJob
            {
                JobId = job.JobId,
                WorkspaceId = job.WorkspaceId,
                CollectionId = job.CollectionId,
                State = EmbeddingJobState.Failed,
                Request = job.Request,
                ErrorMessage = ex.Message,
                CreatedAt = job.CreatedAt,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>(job.Metadata)
            });
        }
    }

    public Task<IReadOnlyList<EmbeddingJob>> QueryRecentAsync(
        string workspaceId,
        string? collectionId,
        int take,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var jobs = _jobs
                .Where(job => string.Equals(job.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
                .Where(job => string.IsNullOrWhiteSpace(collectionId)
                    || string.Equals(job.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(job => job.CompletedAt ?? job.StartedAt ?? job.CreatedAt)
                .Take(take > 0 ? take : 50)
                .ToArray();

            return Task.FromResult<IReadOnlyList<EmbeddingJob>>(jobs);
        }
    }

    private EmbeddingJob StoreProcessed(
        EmbeddingJob original,
        EmbeddingJob processed)
    {
        lock (_gate)
        {
            _jobs.RemoveAll(job => string.Equals(job.JobId, original.JobId, StringComparison.OrdinalIgnoreCase));
            _jobs.Add(processed);
        }

        return processed;
    }
}
