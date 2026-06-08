using ContextCore.Abstractions;
using ContextCore.Storage.Postgres;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 作业队列，同时实现 <see cref="IContextJobQueue"/> 和 <see cref="IContextJobQueryStore"/>。
/// 使用 SELECT FOR UPDATE SKIP LOCKED 实现并发安全的出队。
/// </summary>
public sealed class PostgresContextJobQueue : PostgresStoreBase, IContextJobQueue, IContextJobQueryStore
{
    public PostgresContextJobQueue(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task EnqueueAsync(ContextJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var now = DateTimeOffset.UtcNow;
        var normalized = new ContextJob
        {
            JobId = string.IsNullOrWhiteSpace(job.JobId) ? Guid.NewGuid().ToString("N") : job.JobId,
            WorkspaceId = job.WorkspaceId,
            CollectionId = job.CollectionId,
            Kind = job.Kind,
            PayloadJson = job.PayloadJson,
            State = ContextJobState.Queued,
            Priority = job.Priority,
            MaxRetryCount = job.MaxRetryCount,
            CreatedAt = job.CreatedAt == default ? now : job.CreatedAt,
        };

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("context_jobs")} (
    job_id, workspace_id, collection_id, kind, state, priority, retry_count,
    max_retry_count, created_at, data)
VALUES (
    @job_id, @workspace_id, @collection_id, @kind, @state, @priority, @retry_count,
    @max_retry_count, @created_at, @data)
ON CONFLICT (job_id) DO UPDATE SET
    state = CASE WHEN {Table("context_jobs")}.state IN ('Succeeded','Failed','Cancelled')
                 THEN EXCLUDED.state ELSE {Table("context_jobs")}.state END,
    priority = EXCLUDED.priority,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("job_id", normalized.JobId);
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", normalized.CollectionId);
        command.Parameters.AddWithValue("kind", normalized.Kind.ToString());
        command.Parameters.AddWithValue("state", normalized.State.ToString());
        command.Parameters.AddWithValue("priority", normalized.Priority);
        command.Parameters.AddWithValue("retry_count", normalized.RetryCount);
        command.Parameters.AddWithValue("max_retry_count", normalized.MaxRetryCount);
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextJob?> DequeueAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using var selectCmd = connection.CreateCommand();
        selectCmd.Transaction = transaction;
        selectCmd.CommandTimeout = Options.CommandTimeoutSeconds;
        selectCmd.CommandText = $"""
SELECT job_id, data FROM {Table("context_jobs")}
WHERE state IN ('Queued','WaitingRetry')
ORDER BY priority DESC, created_at ASC
LIMIT 1
FOR UPDATE SKIP LOCKED;
""";

        string? jobId = null;
        ContextJob? job = null;
        await using (var reader = await selectCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                jobId = reader.GetString(0);
                var json = reader.GetString(1);
                job = Serializer.Deserialize<ContextJob>(json);
            }
        }

        if (jobId is null || job is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var running = new ContextJob
        {
            JobId = job.JobId,
            WorkspaceId = job.WorkspaceId,
            CollectionId = job.CollectionId,
            Kind = job.Kind,
            PayloadJson = job.PayloadJson,
            State = ContextJobState.Running,
            Priority = job.Priority,
            RetryCount = job.RetryCount,
            MaxRetryCount = job.MaxRetryCount,
            CreatedAt = job.CreatedAt,
            StartedAt = now,
            CompletedAt = job.CompletedAt,
            ErrorMessage = job.ErrorMessage,
        };

        await using var updateCmd = connection.CreateCommand();
        updateCmd.Transaction = transaction;
        updateCmd.CommandTimeout = Options.CommandTimeoutSeconds;
        updateCmd.CommandText = $"""
UPDATE {Table("context_jobs")}
SET state = 'Running', data = @data
WHERE job_id = @job_id;
""";
        updateCmd.Parameters.AddWithValue("job_id", jobId);
        AddJson(updateCmd, "data", running);
        await updateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return running;
    }

    public async Task AckAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        var tbl1 = Table("context_jobs");
        command.CommandText = $@"UPDATE {tbl1}
SET state = 'Succeeded',
    data = jsonb_set(jsonb_set(data, '{{State}}', '""Succeeded""'), '{{CompletedAt}}', to_jsonb(now()))
WHERE job_id = @job_id;";
        command.Parameters.AddWithValue("job_id", jobId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task NackAsync(string jobId, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        // 读当前 retry_count，超过 max_retry_count 则 Failed，否则 WaitingRetry
        var tbl2 = Table("context_jobs");
        command.CommandText = $@"UPDATE {tbl2}
SET retry_count = retry_count + 1,
    state = CASE WHEN retry_count + 1 >= max_retry_count THEN 'Failed' ELSE 'WaitingRetry' END,
    data = jsonb_set(
        jsonb_set(
            jsonb_set(data, '{{RetryCount}}', to_jsonb(retry_count + 1)),
            '{{State}}',
            CASE WHEN retry_count + 1 >= max_retry_count THEN '""Failed""' ELSE '""WaitingRetry""' END::jsonb),
        '{{ErrorMessage}}', to_jsonb(@reason::text))
WHERE job_id = @job_id;";
        command.Parameters.AddWithValue("job_id", jobId);
        command.Parameters.AddWithValue("reason", reason);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextJob>> QueryAsync(
        ContextJobQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        var conditions = new List<string>();

        if (!string.IsNullOrWhiteSpace(query.WorkspaceId))
        {
            conditions.Add("workspace_id = @workspace_id");
            command.Parameters.AddWithValue("workspace_id", query.WorkspaceId);
        }

        if (!string.IsNullOrWhiteSpace(query.CollectionId))
        {
            conditions.Add("collection_id = @collection_id");
            command.Parameters.AddWithValue("collection_id", query.CollectionId);
        }

        if (query.State.HasValue)
        {
            conditions.Add("state = @state");
            command.Parameters.AddWithValue("state", query.State.Value.ToString());
        }

        var take = query.Take > 0 ? query.Take : 100;
        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;
        command.CommandText = $"""
SELECT data FROM {Table("context_jobs")}
{where}
ORDER BY priority DESC, created_at ASC
LIMIT {take};
""";

        var results = new List<ContextJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var json = reader.GetString(0);
            var item = Serializer.Deserialize<ContextJob>(json);
            if (item is not null) results.Add(item);
        }

        return results;
    }
}
