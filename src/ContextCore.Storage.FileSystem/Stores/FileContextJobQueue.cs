using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>
/// 基于文件系统的作业队列，同时实现 <see cref="IContextJobQueue"/> 和 <see cref="IContextJobQueryStore"/>。
/// 作业状态持久化为 JSONL 文件，支持入队、出队、确认与重试操作。
/// Dequeue 在跨进程文件锁内完成读-找-改-写的原子状态转换，避免 TOCTOU 竞态。
/// </summary>
/// <remarks>
/// 维护进程内 JobId → jobs.jsonl 路径索引（<see cref="_jobPathIndex"/>），
/// 让 Ack/Nack 在 Enqueue 已记录路径时跳过全量扫描。索引为纯优化：
/// 未命中时回退到目录扫描，扫描命中后再回填缓存。jobs 不在文件间移动、不删除，
/// 故映射在 job 生命周期内稳定；缓存指向已删除文件时由 <see cref="File.Exists"/> 守卫回退到扫描。
/// 多进程场景下他进程 Enqueue 的 job 不在本进程缓存中，首次 Ack 走扫描并回填。
/// </remarks>
public sealed class FileContextJobQueue : IContextJobQueue, IContextJobQueryStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;
    private readonly FileSystemWriter _writer;
    private readonly FileFormatSerializer _serializer;
    // JobId → jobs.jsonl 路径的进程内索引，Ack/Nack 定位用。ConcurrentDictionary 保证无锁读写在 _gate 之外也安全。
    private readonly ConcurrentDictionary<string, string> _jobPathIndex = new(StringComparer.OrdinalIgnoreCase);

    public FileContextJobQueue(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileContextJobQueue(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _serializer = serializer;
        _jsonLines = new FileJsonLineStore(serializer);
        _writer = new FileSystemWriter();
    }

    /// <summary>进程内 JobId→路径索引的条目数，供测试观察索引是否被 Enqueue 命中。</summary>
    internal int JobPathIndexCount => _jobPathIndex.Count;

    public async Task EnqueueAsync(ContextJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var normalized = Copy(
            job,
            state: ContextJobState.Queued,
            clearCompletedAt: true,
            clearErrorMessage: true);

        var path = GetJobsPath(normalized.WorkspaceId, normalized.CollectionId);
        await _jsonLines.UpsertAsync(path, normalized, item => item.JobId, cancellationToken)
            .ConfigureAwait(false);

        // Enqueue 已知 job 落地的文件，记录到索引，后续 Ack/Nack 直接 O(1) 定位无需扫描。
        _jobPathIndex[normalized.JobId] = path;
    }

    public async Task<ContextJob?> DequeueAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var workspacesDirectory = Path.Combine(_paths.RootPath, "workspaces");
            if (!Directory.Exists(workspacesDirectory))
            {
                return null;
            }

            // 逐文件在跨进程文件锁内完成读-找-改-写的原子 claim，消除 TOCTOU 竞态。
            foreach (var jobFile in Directory.EnumerateFiles(workspacesDirectory, "jobs.jsonl", SearchOption.AllDirectories))
            {
                var claimed = await TryClaimJobFromFileAsync(jobFile, cancellationToken).ConfigureAwait(false);
                if (claimed is not null)
                {
                    return claimed;
                }
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AckAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await UpdateAsync(jobId, job =>
        {
            // CAS — 仅当 job 处于 Running 时才转换为 Succeeded。
            // 过期的 Ack（job 已被前一次执行 Nack 为 WaitingRetry/Failed，或已被 Ack 为 Succeeded）是 no-op，
            // 防止终态被还原或进行中的执行被干扰。
            if (job.State != ContextJobState.Running)
            {
                return job;
            }
            return Copy(
                job,
                state: ContextJobState.Succeeded,
                completedAt: DateTimeOffset.UtcNow,
                clearErrorMessage: true);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task NackAsync(
        string jobId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await UpdateAsync(jobId, job =>
        {
            // CAS — 仅当 job 处于 Running 时才转换为 WaitingRetry/Failed。
            // 过期的 Nack（job 已被 Ack 为 Succeeded，或已被 Nack 为 WaitingRetry/Failed）是 no-op，
            // 防止已成功的作业被还原为重试/失败状态。
            if (job.State != ContextJobState.Running)
            {
                return job;
            }
            var retryCount = job.RetryCount + 1;
            var state = retryCount <= job.MaxRetryCount
                ? ContextJobState.WaitingRetry
                : ContextJobState.Failed;

            return Copy(
                job,
                state: state,
                retryCount: retryCount,
                completedAt: state == ContextJobState.Failed ? DateTimeOffset.UtcNow : null,
                errorMessage: reason);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextJob>> QueryAsync(
        ContextJobQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var take = query.Take > 0 ? query.Take : 100;
        var jobs = await ReadAllJobsAsync(cancellationToken).ConfigureAwait(false);

        return [.. jobs
            .Where(job => string.IsNullOrWhiteSpace(query.WorkspaceId)
                || string.Equals(job.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            .Where(job => string.IsNullOrWhiteSpace(query.CollectionId)
                || string.Equals(job.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            .Where(job => query.State is null || job.State == query.State)
            .Where(job => query.Kind is null || job.Kind == query.Kind)
            .OrderByDescending(job => job.Priority)
            .ThenByDescending(job => job.CreatedAt)
            .Take(take)];
    }

    /// <summary>
    /// 在跨进程文件锁内对单个 jobs.jsonl 文件执行原子 claim：
    /// 读取所有行 → 反序列化 → 找到第一个可运行的 job → 修改状态为 Running → 序列化回写 → 返回 claimed job。
    /// 如果文件中没有可运行的 job，返回 null。
    /// </summary>
    private async Task<ContextJob?> TryClaimJobFromFileAsync(string jobFile, CancellationToken cancellationToken)
    {
        ContextJob? claimedJob = null;

        await _writer.UpdateLinesAsync(
            jobFile,
            lines =>
            {
                var jobs = DeserializeJobs(lines);
                var match = jobs
                    .Where(IsReadyToRun)
                    .OrderByDescending(job => job.Priority)
                    .ThenBy(job => job.CreatedAt)
                    .FirstOrDefault();

                if (match is null)
                {
                    return lines;
                }

                claimedJob = Copy(match, state: ContextJobState.Running, startedAt: DateTimeOffset.UtcNow);
                var updated = jobs
                    .Where(job => !string.Equals(job.JobId, match.JobId, StringComparison.OrdinalIgnoreCase))
                    .Append(claimedJob)
                    .OrderBy(job => job.JobId, StringComparer.OrdinalIgnoreCase)
                    .Select(_serializer.Serialize)
                    .ToArray();

                return updated;
            },
            cancellationToken).ConfigureAwait(false);

        return claimedJob;
    }

    private async Task UpdateAsync(
        string jobId,
        Func<ContextJob, ContextJob> update,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 定位阶段：扫描所有 jobs.jsonl 找到包含目标 jobId 的文件路径。
            // 这是只读扫描，不需要持锁；即使定位期间文件被其他进程修改，
            // 后续的 _jsonLines.UpdateAsync 会在单文件锁内重新读取最新状态。
            var path = await LocateJobFileAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (path is null)
            {
                return;
            }

            // 原子更新阶段：在单个 FileLock lease 内完成完整 Read/Modify/Write。
            // 即使另一个进程/线程同时在修改同一文件，文件锁会串行化，
            // 第二次进入时会看到第一次的写入结果，不会互相覆盖。
            await _jsonLines.UpdateAsync<ContextJob>(
                path,
                jobs =>
                {
                    var match = jobs.FirstOrDefault(job =>
                        string.Equals(job.JobId, jobId, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                    {
                        // 作业在此文件中不存在（可能已被其他进程移走或删除），原样返回不修改。
                        return jobs;
                    }

                    var updated = update(match);
                    return jobs
                        .Where(job => !string.Equals(job.JobId, jobId, StringComparison.OrdinalIgnoreCase))
                        .Append(updated)
                        .OrderBy(job => job.JobId, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 定位包含指定 jobId 的 jobs.jsonl 文件路径。
    /// 优先查进程内 JobId→路径索引（O(1)）；未命中或缓存指向已删除文件时
    /// 回退到全量扫描，扫描命中后回填索引。仅用于定位，不持锁；
    /// 后续的原子更新由 _jsonLines.UpdateAsync 在单文件锁内完成。
    /// </summary>
    private async Task<string?> LocateJobFileAsync(string jobId, CancellationToken cancellationToken)
    {
        // 索引命中且文件仍存在时直接返回，跳过全量扫描。
        if (_jobPathIndex.TryGetValue(jobId, out var cached) && File.Exists(cached))
        {
            return cached;
        }

        var workspacesDirectory = Path.Combine(_paths.RootPath, "workspaces");
        if (!Directory.Exists(workspacesDirectory))
        {
            return null;
        }

        foreach (var jobFile in Directory.EnumerateFiles(workspacesDirectory, "jobs.jsonl", SearchOption.AllDirectories))
        {
            var jobs = await _jsonLines.ReadAsync<ContextJob>(jobFile, cancellationToken)
                .ConfigureAwait(false);
            if (jobs.Any(job => string.Equals(job.JobId, jobId, StringComparison.OrdinalIgnoreCase)))
            {
                // 扫描命中后回填索引，后续 Ack/Nack 直接命中缓存。
                _jobPathIndex[jobId] = jobFile;
                return jobFile;
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<ContextJob>> ReadAllJobsAsync(CancellationToken cancellationToken)
    {
        var workspacesDirectory = Path.Combine(_paths.RootPath, "workspaces");
        if (!Directory.Exists(workspacesDirectory))
        {
            return Array.Empty<ContextJob>();
        }

        var jobs = new List<ContextJob>();
        // 队列查询面向控制室和监控，需要跨 workspace/collection 汇总所有 jobs.jsonl。
        foreach (var jobFile in Directory.EnumerateFiles(workspacesDirectory, "jobs.jsonl", SearchOption.AllDirectories))
        {
            jobs.AddRange(await _jsonLines.ReadAsync<ContextJob>(jobFile, cancellationToken)
                .ConfigureAwait(false));
        }

        return jobs;
    }

    private IReadOnlyList<ContextJob> DeserializeJobs(IReadOnlyList<string> lines)
    {
        var jobs = new List<ContextJob>();
        foreach (var line in lines.Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            try
            {
                var job = _serializer.Deserialize<ContextJob>(line);
                if (job is not null)
                {
                    jobs.Add(job);
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // 损坏行隔离：跳过无法反序列化的行，不影响其他作业。
            }
        }

        return jobs;
    }

    private string GetJobsPath(string workspaceId, string collectionId)
    {
        return Path.Combine(
            _paths.GetCollectionDirectory(workspaceId, collectionId),
            "jobs",
            "jobs.jsonl");
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
