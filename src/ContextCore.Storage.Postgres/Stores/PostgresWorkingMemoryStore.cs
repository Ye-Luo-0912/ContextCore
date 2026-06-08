using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 工作记忆存储，同时实现：
/// <list type="bullet">
///   <item><see cref="IWorkingMemoryService"/> — 短期工作记忆条目与活跃上下文管理</item>
///   <item><see cref="IPromotionRecordStore"/> — 晋升记录持久化</item>
///   <item><see cref="IPromotionCandidateStore"/> — 晋升候选项管理</item>
/// </list>
/// </summary>
public sealed class PostgresWorkingMemoryStore : PostgresStoreBase,
    IWorkingMemoryService,
    IPromotionRecordStore,
    IPromotionCandidateStore
{
    public PostgresWorkingMemoryStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    // ──────────────────────────────────────────
    // IWorkingMemoryService
    // ──────────────────────────────────────────

    public async Task<WorkingMemoryItem> AddAsync(
        WorkingMemoryItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var now = DateTimeOffset.UtcNow;
        var isNew = string.IsNullOrWhiteSpace(item.Id);
        var normalized = new WorkingMemoryItem
        {
            Id = isNew ? Guid.NewGuid().ToString("N") : item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Type = item.Type,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            Tags = item.Tags,
            SourceRefs = item.SourceRefs,
            RelationRefs = item.RelationRefs,
            Importance = item.Importance,
            Confidence = item.Confidence,
            Metadata = item.Metadata,
            CreatedAt = isNew ? now : item.CreatedAt,
            UpdatedAt = now,
        };

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("working_memory_items")} (
    workspace_id, collection_id, id, type, created_at, updated_at, data)
VALUES (
    @workspace_id, @collection_id, @id, @type, @created_at, @updated_at, @data)
ON CONFLICT (workspace_id, collection_id, id) DO UPDATE SET
    type = EXCLUDED.type,
    updated_at = EXCLUDED.updated_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", normalized.CollectionId);
        command.Parameters.AddWithValue("id", normalized.Id);
        command.Parameters.AddWithValue("type", normalized.Type);
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        command.Parameters.AddWithValue("updated_at", normalized.UpdatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return normalized;
    }

    public async Task<IReadOnlyList<WorkingMemoryItem>> GetRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data FROM {Table("working_memory_items")}
WHERE workspace_id = @workspace_id AND collection_id = @collection_id
ORDER BY created_at DESC
LIMIT {(take > 0 ? take : 50)};
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);

        var results = new List<WorkingMemoryItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var item = Serializer.Deserialize<WorkingMemoryItem>(reader.GetString(0));
            if (item is not null) results.Add(item);
        }

        return results;
    }

    public async Task ClearAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
DELETE FROM {Table("working_memory_items")}
WHERE workspace_id = @workspace_id AND collection_id = @collection_id;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkingMemoryActiveContext?> GetActiveContextAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data FROM {Table("working_memory_state")}
WHERE workspace_id = @workspace_id AND collection_id = @collection_id AND key = 'active_context'
LIMIT 1;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);

        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return string.IsNullOrWhiteSpace(json) ? null : Serializer.Deserialize<WorkingMemoryActiveContext>(json);
    }

    public async Task<WorkingMemoryActiveContext> SetActiveContextAsync(
        WorkingMemoryActiveContext activeContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activeContext);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("working_memory_state")} (workspace_id, collection_id, key, data)
VALUES (@workspace_id, @collection_id, 'active_context', @data)
ON CONFLICT (workspace_id, collection_id, key) DO UPDATE SET data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", activeContext.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", activeContext.CollectionId);
        AddJson(command, "data", activeContext);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return activeContext;
    }

    public async Task<WorkingMemoryCurrentTask?> GetCurrentTaskAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data FROM {Table("working_memory_state")}
WHERE workspace_id = @workspace_id AND collection_id = @collection_id AND key = 'current_task'
LIMIT 1;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);

        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return string.IsNullOrWhiteSpace(json) ? null : Serializer.Deserialize<WorkingMemoryCurrentTask>(json);
    }

    public async Task<WorkingMemoryCurrentTask> SetCurrentTaskAsync(
        WorkingMemoryCurrentTask currentTask,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentTask);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("working_memory_state")} (workspace_id, collection_id, key, data)
VALUES (@workspace_id, @collection_id, 'current_task', @data)
ON CONFLICT (workspace_id, collection_id, key) DO UPDATE SET data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", currentTask.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", currentTask.CollectionId);
        AddJson(command, "data", currentTask);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return currentTask;
    }

    // ──────────────────────────────────────────
    // IPromotionRecordStore
    // ──────────────────────────────────────────

    public async Task SavePromotionRecordAsync(
        ContextPromotionRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var now = DateTimeOffset.UtcNow;
        var normalized = string.IsNullOrWhiteSpace(record.Id)
            ? new ContextPromotionRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                WorkspaceId = record.WorkspaceId,
                CollectionId = record.CollectionId,
                SourceMemoryId = record.SourceMemoryId,
                FromStatus = record.FromStatus,
                ToStatus = record.ToStatus,
                Strategy = record.Strategy,
                Reviewer = record.Reviewer,
                TargetLayer = record.TargetLayer,
                SourceRefs = record.SourceRefs,
                RelationRefs = record.RelationRefs,
                Reason = record.Reason,
                Confidence = record.Confidence,
                CreatedAt = now,
            }
            : record;

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("promotion_records")} (
    workspace_id, collection_id, id, source_memory_id, strategy, created_at, data)
VALUES (
    @workspace_id, @collection_id, @id, @source_memory_id, @strategy, @created_at, @data)
ON CONFLICT (workspace_id, collection_id, id) DO NOTHING;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", normalized.CollectionId);
        command.Parameters.AddWithValue("id", normalized.Id);
        command.Parameters.AddWithValue("source_memory_id", normalized.SourceMemoryId);
        command.Parameters.AddWithValue("strategy", normalized.Strategy);
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextPromotionRecord>> QueryPromotionRecordsAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data FROM {Table("promotion_records")}
WHERE workspace_id = @workspace_id AND collection_id = @collection_id
ORDER BY created_at DESC
LIMIT {(take > 0 ? take : 50)};
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);

        var results = new List<ContextPromotionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var item = Serializer.Deserialize<ContextPromotionRecord>(reader.GetString(0));
            if (item is not null) results.Add(item);
        }

        return results;
    }

    // ──────────────────────────────────────────
    // IPromotionCandidateStore
    // ──────────────────────────────────────────

    public async Task SavePromotionCandidateAsync(
        PromotionCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var now = DateTimeOffset.UtcNow;
        var isNew = string.IsNullOrWhiteSpace(candidate.Id);
        var normalized = new PromotionCandidate
        {
            Id = isNew ? Guid.NewGuid().ToString("N") : candidate.Id,
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            SourceId = candidate.SourceId,
            SourceKind = candidate.SourceKind,
            Content = candidate.Content,
            TargetLayer = candidate.TargetLayer,
            Status = candidate.Status,
            Decision = candidate.Decision,
            Category = candidate.Category,
            Reason = candidate.Reason,
            Confidence = candidate.Confidence,
            MatchedRules = candidate.MatchedRules,
            SourceRefs = candidate.SourceRefs,
            Reviewer = candidate.Reviewer,
            Metadata = candidate.Metadata,
            CreatedAt = isNew ? now : candidate.CreatedAt,
            UpdatedAt = now,
        };

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("promotion_candidates")} (
    workspace_id, collection_id, id, status, created_at, updated_at, data)
VALUES (
    @workspace_id, @collection_id, @id, @status, @created_at, @updated_at, @data)
ON CONFLICT (workspace_id, collection_id, id) DO UPDATE SET
    status = EXCLUDED.status,
    updated_at = EXCLUDED.updated_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", normalized.CollectionId);
        command.Parameters.AddWithValue("id", normalized.Id);
        command.Parameters.AddWithValue("status", normalized.Status.ToString());
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        command.Parameters.AddWithValue("updated_at", normalized.UpdatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PromotionCandidate?> GetPromotionCandidateAsync(
        string workspaceId,
        string collectionId,
        string id,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data FROM {Table("promotion_candidates")}
WHERE workspace_id = @workspace_id AND collection_id = @collection_id AND id = @id
LIMIT 1;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("id", id);

        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return string.IsNullOrWhiteSpace(json) ? null : Serializer.Deserialize<PromotionCandidate>(json);
    }

    public async Task<IReadOnlyList<PromotionCandidate>> QueryPromotionCandidatesAsync(
        string workspaceId,
        string collectionId,
        PromotionCandidateStatus? status,
        int take,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        var conditions = new List<string>
        {
            "workspace_id = @workspace_id",
            "collection_id = @collection_id"
        };
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);

        if (status.HasValue)
        {
            conditions.Add("status = @status");
            command.Parameters.AddWithValue("status", status.Value.ToString());
        }

        var where = string.Join(" AND ", conditions);
        command.CommandText = $"""
SELECT data FROM {Table("promotion_candidates")}
WHERE {where}
ORDER BY created_at DESC
LIMIT {(take > 0 ? take : 50)};
""";

        var results = new List<PromotionCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var item = Serializer.Deserialize<PromotionCandidate>(reader.GetString(0));
            if (item is not null) results.Add(item);
        }

        return results;
    }

    public async Task<PromotionCandidate?> UpdatePromotionCandidateStatusAsync(
        string workspaceId,
        string collectionId,
        string id,
        PromotionCandidateStatus status,
        string? reviewer = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        var existing = await GetPromotionCandidateAsync(workspaceId, collectionId, id, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null) return null;

        var updated = new PromotionCandidate
        {
            Id = existing.Id,
            WorkspaceId = existing.WorkspaceId,
            CollectionId = existing.CollectionId,
            SourceId = existing.SourceId,
            SourceKind = existing.SourceKind,
            Content = existing.Content,
            TargetLayer = existing.TargetLayer,
            Status = status,
            Decision = existing.Decision,
            Category = existing.Category,
            Reason = reason ?? existing.Reason,
            Confidence = existing.Confidence,
            MatchedRules = existing.MatchedRules,
            SourceRefs = existing.SourceRefs,
            Reviewer = reviewer ?? existing.Reviewer,
            Metadata = existing.Metadata,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await SavePromotionCandidateAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }
}
