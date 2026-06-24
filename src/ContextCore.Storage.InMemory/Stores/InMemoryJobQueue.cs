using ContextCore.Abstractions;

namespace ContextCore.Storage.InMemory;

/// <summary>
/// 基于内存的作业队列，同时实现 <see cref="IContextJobQueue"/> 和 <see cref="IContextJobQueryStore"/>，
/// 适用于测试和短生命周期场景。
/// </summary>
public sealed class InMemoryJobQueue : IContextJobQueue, IContextJobQueryStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ContextJob> _jobs = new(StringComparer.OrdinalIgnoreCase);

    public Task EnqueueAsync(ContextJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();

        var queuedJob = Copy(
            job,
            state: ContextJobState.Queued,
            clearCompletedAt: true,
            clearErrorMessage: true);

        lock (_gate)
        {
            _jobs[queuedJob.JobId] = queuedJob;
        }

        return Task.CompletedTask;
    }

    public Task<ContextJob?> DequeueAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var match = _jobs.Values
                .Where(IsReadyToRun)
                .OrderByDescending(job => job.Priority)
                .ThenBy(job => job.CreatedAt)
                .FirstOrDefault();

            if (match is null)
            {
                return Task.FromResult<ContextJob?>(null);
            }

            var runningJob = Copy(
                match,
                state: ContextJobState.Running,
                startedAt: DateTimeOffset.UtcNow);

            _jobs[runningJob.JobId] = runningJob;

            return Task.FromResult<ContextJob?>(runningJob);
        }
    }

    public Task AckAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_jobs.TryGetValue(jobId, out var job))
            {
                _jobs[jobId] = Copy(
                    job,
                    state: ContextJobState.Succeeded,
                    completedAt: DateTimeOffset.UtcNow,
                    clearErrorMessage: true);
            }
        }

        return Task.CompletedTask;
    }

    public Task NackAsync(
        string jobId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var job)) return Task.CompletedTask;
            var retryCount = job.RetryCount + 1;
            // Nack 后仍在重试预算内则进入 WaitingRetry，便于控制室观察失败重试状态。
            var state = retryCount <= job.MaxRetryCount
                ? ContextJobState.WaitingRetry
                : ContextJobState.Failed;

            _jobs[jobId] = Copy(
                job,
                state: state,
                retryCount: retryCount,
                completedAt: state == ContextJobState.Failed ? DateTimeOffset.UtcNow : null,
                errorMessage: reason);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ContextJob>> QueryAsync(
        ContextJobQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var take = query.Take > 0 ? query.Take : 100;
            var jobs = _jobs.Values
                .Where(job => string.IsNullOrWhiteSpace(query.WorkspaceId)
                    || string.Equals(job.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase))
                .Where(job => string.IsNullOrWhiteSpace(query.CollectionId)
                    || string.Equals(job.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
                .Where(job => query.State is null || job.State == query.State)
                .Where(job => query.Kind is null || job.Kind == query.Kind)
                .OrderByDescending(job => job.Priority)
                .ThenByDescending(job => job.CreatedAt)
                .Take(take)
                .Select(job => Copy(job))
                .ToArray();

            return Task.FromResult<IReadOnlyList<ContextJob>>(jobs);
        }
    }

    private static ContextJob Copy(
        ContextJob job,
        ContextJobState? state = null,
        int? retryCount = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? completedAt = null,
        string? errorMessage = null,
        bool clearCompletedAt = false,
        bool clearErrorMessage = false)
    {
        return new ContextJob
        {
            JobId = string.IsNullOrWhiteSpace(job.JobId) ? Guid.NewGuid().ToString("N") : job.JobId,
            WorkspaceId = job.WorkspaceId,
            CollectionId = job.CollectionId,
            Kind = job.Kind,
            PayloadJson = job.PayloadJson,
            State = state ?? job.State,
            Priority = job.Priority,
            RetryCount = retryCount ?? job.RetryCount,
            MaxRetryCount = job.MaxRetryCount,
            CreatedAt = job.CreatedAt == default ? DateTimeOffset.UtcNow : job.CreatedAt,
            StartedAt = startedAt ?? job.StartedAt,
            CompletedAt = clearCompletedAt ? null : completedAt ?? job.CompletedAt,
            ErrorMessage = clearErrorMessage ? null : errorMessage ?? job.ErrorMessage
        };
    }

    private static bool IsReadyToRun(ContextJob job)
    {
        return job.State is ContextJobState.Queued or ContextJobState.WaitingRetry;
    }
}
