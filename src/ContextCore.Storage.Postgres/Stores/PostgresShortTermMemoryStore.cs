using System.Collections.Generic;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 短期记忆存储，负责保存短期原始事件、工作项并提供只读摘要。
/// active 与 archive 分表保存，archive 为保留而非删除。
/// R14-PG-3：替代 UnsupportedShortTermMemoryStore，让 Postgres provider 在 HA 场景下能持久化短期记忆。
/// </summary>
public sealed class PostgresShortTermMemoryStore : PostgresStoreBase, IShortTermMemoryStore
{
    public PostgresShortTermMemoryStore(PostgresConnectionFactory connectionFactory, PostgresJsonSerializer serializer, PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task AppendRawEventAsync(ShortTermRawEvent rawEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rawEvent);
        var normalized = Normalize(rawEvent);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("short_term_raw_events")} (workspace_id, collection_id, event_id, kind, created_at, data)
VALUES (@workspace_id, @collection_id, @event_id, @kind, @created_at, @data)
ON CONFLICT (workspace_id, collection_id, event_id) DO NOTHING;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", normalized.CollectionId);
        command.Parameters.AddWithValue("event_id", normalized.EventId);
        command.Parameters.AddWithValue("kind", normalized.EventKind ?? string.Empty);
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveWorkingItemAsync(ShortTermWorkingItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var normalized = Normalize(item);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("short_term_working_items")} (workspace_id, collection_id, item_id, kind, created_at, updated_at, expires_at, data)
VALUES (@workspace_id, @collection_id, @item_id, @kind, @created_at, @updated_at, @expires_at, @data)
ON CONFLICT (workspace_id, collection_id, item_id) DO UPDATE SET
    kind = EXCLUDED.kind,
    created_at = EXCLUDED.created_at,
    updated_at = EXCLUDED.updated_at,
    expires_at = EXCLUDED.expires_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", normalized.CollectionId);
        command.Parameters.AddWithValue("item_id", normalized.ItemId);
        command.Parameters.AddWithValue("kind", normalized.Kind ?? string.Empty);
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        command.Parameters.AddWithValue("updated_at", normalized.UpdatedAt);
        command.Parameters.AddWithValue("expires_at", (object?)normalized.ExpiresAt ?? DBNull.Value);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceRawEventsAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<ShortTermRawEvent> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentNullException.ThrowIfNull(items);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var deleteCommand = connection.CreateCommand())
            {
                deleteCommand.Transaction = tx;
                deleteCommand.CommandTimeout = Options.CommandTimeoutSeconds;
                deleteCommand.CommandText = $"""
DELETE FROM {Table("short_term_raw_events")}
WHERE workspace_id = @workspace_id AND collection_id = @collection_id;
""";
                deleteCommand.Parameters.AddWithValue("workspace_id", workspaceId);
                deleteCommand.Parameters.AddWithValue("collection_id", collectionId);
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var raw in items)
            {
                var normalized = Normalize(raw);
                await using var insertCommand = connection.CreateCommand();
                insertCommand.Transaction = tx;
                insertCommand.CommandTimeout = Options.CommandTimeoutSeconds;
                insertCommand.CommandText = $"""
INSERT INTO {Table("short_term_raw_events")} (workspace_id, collection_id, event_id, kind, created_at, data)
VALUES (@workspace_id, @collection_id, @event_id, @kind, @created_at, @data)
ON CONFLICT (workspace_id, collection_id, event_id) DO NOTHING;
""";
                insertCommand.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
                insertCommand.Parameters.AddWithValue("collection_id", normalized.CollectionId);
                insertCommand.Parameters.AddWithValue("event_id", normalized.EventId);
                insertCommand.Parameters.AddWithValue("kind", normalized.EventKind ?? string.Empty);
                insertCommand.Parameters.AddWithValue("created_at", normalized.CreatedAt);
                AddJson(insertCommand, "data", normalized);
                await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task ReplaceWorkingItemsAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<ShortTermWorkingItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentNullException.ThrowIfNull(items);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var deleteCommand = connection.CreateCommand())
            {
                deleteCommand.Transaction = tx;
                deleteCommand.CommandTimeout = Options.CommandTimeoutSeconds;
                deleteCommand.CommandText = $"""
DELETE FROM {Table("short_term_working_items")}
WHERE workspace_id = @workspace_id AND collection_id = @collection_id;
""";
                deleteCommand.Parameters.AddWithValue("workspace_id", workspaceId);
                deleteCommand.Parameters.AddWithValue("collection_id", collectionId);
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var item in items)
            {
                var normalized = Normalize(item);
                await using var insertCommand = connection.CreateCommand();
                insertCommand.Transaction = tx;
                insertCommand.CommandTimeout = Options.CommandTimeoutSeconds;
                insertCommand.CommandText = $"""
INSERT INTO {Table("short_term_working_items")} (workspace_id, collection_id, item_id, kind, created_at, updated_at, expires_at, data)
VALUES (@workspace_id, @collection_id, @item_id, @kind, @created_at, @updated_at, @expires_at, @data)
ON CONFLICT (workspace_id, collection_id, item_id) DO UPDATE SET
    kind = EXCLUDED.kind,
    created_at = EXCLUDED.created_at,
    updated_at = EXCLUDED.updated_at,
    expires_at = EXCLUDED.expires_at,
    data = EXCLUDED.data;
""";
                insertCommand.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
                insertCommand.Parameters.AddWithValue("collection_id", normalized.CollectionId);
                insertCommand.Parameters.AddWithValue("item_id", normalized.ItemId);
                insertCommand.Parameters.AddWithValue("kind", normalized.Kind ?? string.Empty);
                insertCommand.Parameters.AddWithValue("created_at", normalized.CreatedAt);
                insertCommand.Parameters.AddWithValue("updated_at", normalized.UpdatedAt);
                insertCommand.Parameters.AddWithValue("expires_at", (object?)normalized.ExpiresAt ?? DBNull.Value);
                AddJson(insertCommand, "data", normalized);
                await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task AppendArchivedRawEventsAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<ShortTermRawEvent> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
        {
            return;
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var archivedAt = DateTimeOffset.UtcNow;
        foreach (var raw in items)
        {
            var normalized = Normalize(raw);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = Options.CommandTimeoutSeconds;
            command.CommandText = $"""
INSERT INTO {Table("short_term_archived_raw_events")} (workspace_id, collection_id, event_id, archived_at, data)
VALUES (@workspace_id, @collection_id, @event_id, @archived_at, @data);
""";
            command.Parameters.AddWithValue("workspace_id", workspaceId);
            command.Parameters.AddWithValue("collection_id", collectionId);
            command.Parameters.AddWithValue("event_id", normalized.EventId);
            command.Parameters.AddWithValue("archived_at", archivedAt);
            AddJson(command, "data", normalized);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task AppendArchivedWorkingItemsAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<ShortTermWorkingItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
        {
            return;
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var archivedAt = DateTimeOffset.UtcNow;
        foreach (var item in items)
        {
            var normalized = Normalize(item);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = Options.CommandTimeoutSeconds;
            command.CommandText = $"""
INSERT INTO {Table("short_term_archived_working_items")} (workspace_id, collection_id, item_id, archived_at, data)
VALUES (@workspace_id, @collection_id, @item_id, @archived_at, @data);
""";
            command.Parameters.AddWithValue("workspace_id", workspaceId);
            command.Parameters.AddWithValue("collection_id", collectionId);
            command.Parameters.AddWithValue("item_id", normalized.ItemId);
            command.Parameters.AddWithValue("archived_at", archivedAt);
            AddJson(command, "data", normalized);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<ShortTermWorkingItem?> GetWorkingItemAsync(
        string workspaceId,
        string collectionId,
        string itemId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("short_term_working_items")}
WHERE workspace_id = @workspace_id AND collection_id = @collection_id AND item_id = @item_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("item_id", itemId);

        return await ExecuteScalarJsonAsync<ShortTermWorkingItem>(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ShortTermRawEvent>> QueryRawEventsAsync(ShortTermRawEventQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await QueryRawEventsInternalAsync(query, archived: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ShortTermWorkingItem>> QueryWorkingItemsAsync(ShortTermWorkingItemQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await QueryWorkingItemsInternalAsync(query, archived: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ShortTermRawEvent>> QueryArchivedRawEventsAsync(ShortTermRawEventQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await QueryRawEventsInternalAsync(query, archived: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ShortTermWorkingItem>> QueryArchivedWorkingItemsAsync(ShortTermWorkingItemQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await QueryWorkingItemsInternalAsync(query, archived: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ShortTermMemoryScope>> QueryScopesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT DISTINCT workspace_id, collection_id FROM {Table("short_term_raw_events")}
UNION
SELECT DISTINCT workspace_id, collection_id FROM {Table("short_term_working_items")};
""";
        var results = new List<ShortTermMemoryScope>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new ShortTermMemoryScope
            {
                WorkspaceId = reader.GetString(0),
                CollectionId = reader.GetString(1)
            });
        }

        return results;
    }

    public async Task<ShortTermMemorySummary> GetSummaryAsync(ShortTermSummaryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var latestRaw = await QueryRawEventsAsync(new ShortTermRawEventQuery
        {
            WorkspaceId = query.WorkspaceId,
            CollectionId = query.CollectionId,
            SessionId = query.SessionId,
            Take = query.LatestRawTake > 0 ? query.LatestRawTake : 10
        }, cancellationToken).ConfigureAwait(false);

        var allRaw = await QueryRawEventsAsync(new ShortTermRawEventQuery
        {
            WorkspaceId = query.WorkspaceId,
            CollectionId = query.CollectionId,
            SessionId = query.SessionId,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);

        var working = await QueryWorkingItemsAsync(new ShortTermWorkingItemQuery
        {
            WorkspaceId = query.WorkspaceId,
            CollectionId = query.CollectionId,
            SessionId = query.SessionId,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);

        return BuildSummary(query.WorkspaceId, query.CollectionId, query.SessionId, allRaw, latestRaw, working);
    }

    public async Task<ShortTermArchiveSummary> GetArchiveSummaryAsync(ShortTermArchiveSummaryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var rawEvents = await QueryArchivedRawEventsAsync(new ShortTermRawEventQuery
        {
            WorkspaceId = query.WorkspaceId,
            CollectionId = query.CollectionId,
            SessionId = query.SessionId,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);

        var working = await QueryArchivedWorkingItemsAsync(new ShortTermWorkingItemQuery
        {
            WorkspaceId = query.WorkspaceId,
            CollectionId = query.CollectionId,
            SessionId = query.SessionId,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);

        return BuildArchiveSummary(query.WorkspaceId, query.CollectionId, query.SessionId, rawEvents, working);
    }

    public async Task AppendCompactionRunAsync(ShortTermCompactionRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        var normalized = Normalize(run);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("short_term_compaction_runs")} (workspace_id, collection_id, run_id, started_at, completed_at, data)
VALUES (@workspace_id, @collection_id, @run_id, @started_at, @completed_at, @data)
ON CONFLICT (workspace_id, collection_id, run_id) DO UPDATE SET
    started_at = EXCLUDED.started_at,
    completed_at = EXCLUDED.completed_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", normalized.CollectionId);
        command.Parameters.AddWithValue("run_id", normalized.RunId);
        command.Parameters.AddWithValue("started_at", normalized.StartedAt);
        command.Parameters.AddWithValue("completed_at", (object?)normalized.CompletedAt ?? DBNull.Value);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ShortTermCompactionRun>> QueryCompactionRunsAsync(ShortTermCompactionRunQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        var where = new StringBuilder("WHERE 1=1");
        if (!string.IsNullOrWhiteSpace(query.WorkspaceId))
        {
            where.Append(" AND workspace_id = @workspace_id");
            command.Parameters.AddWithValue("workspace_id", query.WorkspaceId);
        }
        if (!string.IsNullOrWhiteSpace(query.CollectionId))
        {
            where.Append(" AND collection_id = @collection_id");
            command.Parameters.AddWithValue("collection_id", query.CollectionId);
        }
        if (!string.IsNullOrWhiteSpace(query.SessionId))
        {
            where.Append(" AND data->>'SessionId' = @session_id");
            command.Parameters.AddWithValue("session_id", query.SessionId);
        }
        if (!string.IsNullOrWhiteSpace(query.Trigger))
        {
            where.Append(" AND data->>'Trigger' = @trigger");
            command.Parameters.AddWithValue("trigger", query.Trigger);
        }

        var take = query.Take > 0 ? query.Take : 20;
        command.Parameters.AddWithValue("take", take);
        command.CommandText = $"""
SELECT data
FROM {Table("short_term_compaction_runs")}
{where}
ORDER BY started_at DESC
LIMIT @take;
""";

        return await ExecuteReaderJsonAsync<ShortTermCompactionRun>(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ShortTermCompactionRun?> GetCompactionRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("short_term_compaction_runs")}
WHERE run_id = @run_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("run_id", runId);

        return await ExecuteScalarJsonAsync<ShortTermCompactionRun>(command, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ShortTermRawEvent>> QueryRawEventsInternalAsync(
        ShortTermRawEventQuery query,
        bool archived,
        CancellationToken cancellationToken)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        var table = archived ? Table("short_term_archived_raw_events") : Table("short_term_raw_events");
        var orderColumn = archived ? "archived_at" : "created_at";

        var where = new StringBuilder("WHERE workspace_id = @workspace_id");
        command.Parameters.AddWithValue("workspace_id", query.WorkspaceId);

        if (!string.IsNullOrWhiteSpace(query.CollectionId))
        {
            where.Append(" AND collection_id = @collection_id");
            command.Parameters.AddWithValue("collection_id", query.CollectionId);
        }
        if (!string.IsNullOrWhiteSpace(query.SessionId))
        {
            where.Append(" AND data->>'SessionId' = @session_id");
            command.Parameters.AddWithValue("session_id", query.SessionId);
        }
        if (!string.IsNullOrWhiteSpace(query.Source))
        {
            where.Append(" AND data->>'Source' = @source");
            command.Parameters.AddWithValue("source", query.Source);
        }
        if (!string.IsNullOrWhiteSpace(query.EventKind))
        {
            if (archived)
            {
                where.Append(" AND data->>'EventKind' = @event_kind");
            }
            else
            {
                where.Append(" AND kind = @event_kind");
            }
            command.Parameters.AddWithValue("event_kind", query.EventKind);
        }

        var take = query.Take > 0 ? query.Take : 100;
        command.Parameters.AddWithValue("take", take);
        command.CommandText = $"""
SELECT data
FROM {table}
{where}
ORDER BY {orderColumn} DESC
LIMIT @take;
""";

        return await ExecuteReaderJsonAsync<ShortTermRawEvent>(command, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ShortTermWorkingItem>> QueryWorkingItemsInternalAsync(
        ShortTermWorkingItemQuery query,
        bool archived,
        CancellationToken cancellationToken)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        var table = archived ? Table("short_term_archived_working_items") : Table("short_term_working_items");
        var orderColumn = archived ? "archived_at" : "updated_at";

        var where = new StringBuilder("WHERE workspace_id = @workspace_id");
        command.Parameters.AddWithValue("workspace_id", query.WorkspaceId);

        if (!string.IsNullOrWhiteSpace(query.CollectionId))
        {
            where.Append(" AND collection_id = @collection_id");
            command.Parameters.AddWithValue("collection_id", query.CollectionId);
        }
        if (!string.IsNullOrWhiteSpace(query.SessionId))
        {
            where.Append(" AND data->>'SessionId' = @session_id");
            command.Parameters.AddWithValue("session_id", query.SessionId);
        }
        if (!string.IsNullOrWhiteSpace(query.Kind))
        {
            if (archived)
            {
                where.Append(" AND data->>'Kind' = @kind");
            }
            else
            {
                where.Append(" AND kind = @kind");
            }
            command.Parameters.AddWithValue("kind", query.Kind);
        }
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            where.Append(" AND data->>'Status' = @status");
            command.Parameters.AddWithValue("status", query.Status);
        }

        var take = query.Take > 0 ? query.Take : 100;
        command.Parameters.AddWithValue("take", take);
        command.CommandText = $"""
SELECT data
FROM {table}
{where}
ORDER BY {orderColumn} DESC
LIMIT @take;
""";

        return await ExecuteReaderJsonAsync<ShortTermWorkingItem>(command, cancellationToken).ConfigureAwait(false);
    }

    private static ShortTermMemorySummary BuildSummary(
        string workspaceId,
        string? collectionId,
        string? sessionId,
        IReadOnlyList<ShortTermRawEvent> allRaw,
        IReadOnlyList<ShortTermRawEvent> latestRaw,
        IReadOnlyList<ShortTermWorkingItem> working)
    {
        var activeTasks = working.Where(item => IsWorkingKind(item, "ActiveTask", "task")).Take(10).ToArray();
        var recentDecisions = working.Where(item => IsWorkingKind(item, "RecentDecision", "decision")).Take(10).ToArray();
        var openQuestions = working.Where(item => IsWorkingKind(item, "OpenQuestion", "question")).Take(10).ToArray();
        var knownIssues = working.Where(item => IsWorkingKind(item, "KnownIssue", "issue")).Take(10).ToArray();
        var recentWarnings = working.Where(item => IsWorkingKind(item, "RecentWarning", "warning")).Take(10).ToArray();

        return new ShortTermMemorySummary
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SessionId = sessionId,
            RawEventCount = allRaw.Count,
            WorkingItemCount = working.Count,
            ActiveTaskCount = working.Count(item => IsWorkingKind(item, "ActiveTask", "task")),
            RecentDecisionCount = working.Count(item => IsWorkingKind(item, "RecentDecision", "decision")),
            OpenQuestionCount = working.Count(item => IsWorkingKind(item, "OpenQuestion", "question")),
            KnownIssueCount = working.Count(item => IsWorkingKind(item, "KnownIssue", "issue")),
            RecentWarningCount = working.Count(item => IsWorkingKind(item, "RecentWarning", "warning")),
            ActiveTasks = activeTasks,
            RecentDecisions = recentDecisions,
            OpenQuestions = openQuestions,
            KnownIssues = knownIssues,
            RecentWarnings = recentWarnings,
            LatestRawEvents = latestRaw,
            Policy = new ShortTermMemoryPolicy()
        };
    }

    private static ShortTermArchiveSummary BuildArchiveSummary(
        string workspaceId,
        string? collectionId,
        string? sessionId,
        IReadOnlyList<ShortTermRawEvent> rawEvents,
        IReadOnlyList<ShortTermWorkingItem> workingItems)
    {
        return new ShortTermArchiveSummary
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SessionId = sessionId,
            ArchivedRawEventCount = rawEvents.Count,
            ArchivedWorkingItemCount = workingItems.Count,
            ArchivedResolvedWorkingItemCount = workingItems.Count(IsResolvedItem),
            ArchivedActiveTaskCount = workingItems.Count(item => IsWorkingKind(item, "ActiveTask", "task")),
            ArchivedRecentDecisionCount = workingItems.Count(item => IsWorkingKind(item, "RecentDecision", "decision")),
            ArchivedOpenQuestionCount = workingItems.Count(item => IsWorkingKind(item, "OpenQuestion", "question")),
            ArchivedKnownIssueCount = workingItems.Count(item => IsWorkingKind(item, "KnownIssue", "issue")),
            ArchivedRecentWarningCount = workingItems.Count(item => IsWorkingKind(item, "RecentWarning", "warning")),
            LatestArchivedAt = rawEvents
                .Select(item => item.Metadata.GetValueOrDefault("archivedAt"))
                .Concat(workingItems.Select(item => item.Metadata.GetValueOrDefault("archivedAt")))
                .Select(ParseDateTimeOffset)
                .Where(static value => value is not null)
                .Max()
        };
    }

    private static bool IsWorkingKind(ShortTermWorkingItem item, string canonicalKind, string legacyToken)
    {
        return string.Equals(item.Kind, canonicalKind, StringComparison.OrdinalIgnoreCase)
            || item.Kind.Contains(legacyToken, StringComparison.OrdinalIgnoreCase)
            || item.Tags.Contains(canonicalKind, StringComparer.OrdinalIgnoreCase)
            || item.Tags.Contains(legacyToken, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsResolvedItem(ShortTermWorkingItem item)
    {
        return item.Status.Contains("resolved", StringComparison.OrdinalIgnoreCase)
            || item.Status.Contains("closed", StringComparison.OrdinalIgnoreCase)
            || item.Status.Contains("done", StringComparison.OrdinalIgnoreCase)
            || item.Status.Contains("completed", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static ShortTermRawEvent Normalize(ShortTermRawEvent item)
    {
        return new ShortTermRawEvent
        {
            EventId = string.IsNullOrWhiteSpace(item.EventId) ? Guid.NewGuid().ToString("N") : item.EventId,
            OperationId = item.OperationId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            Source = item.Source,
            EventKind = item.EventKind,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            CreatedAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt,
            SequenceId = item.SequenceId,
            Tags = [.. item.Tags],
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static ShortTermWorkingItem Normalize(ShortTermWorkingItem item)
    {
        var now = DateTimeOffset.UtcNow;
        return new ShortTermWorkingItem
        {
            ItemId = string.IsNullOrWhiteSpace(item.ItemId) ? Guid.NewGuid().ToString("N") : item.ItemId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            Kind = item.Kind,
            Title = item.Title,
            Summary = item.Summary,
            Status = item.Status,
            Lifecycle = item.Lifecycle,
            Importance = item.Importance,
            Tags = [.. item.Tags],
            Refs = [.. item.Refs],
            SourceRefs = [.. item.SourceRefs],
            CreatedAt = item.CreatedAt == default ? now : item.CreatedAt,
            UpdatedAt = item.UpdatedAt == default ? now : item.UpdatedAt,
            ExpiresAt = item.ExpiresAt,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static ShortTermCompactionRun Normalize(ShortTermCompactionRun item)
    {
        return new ShortTermCompactionRun
        {
            RunId = string.IsNullOrWhiteSpace(item.RunId) ? Guid.NewGuid().ToString("N") : item.RunId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            Trigger = item.Trigger,
            StartedAt = item.StartedAt == default ? DateTimeOffset.UtcNow : item.StartedAt,
            CompletedAt = item.CompletedAt == default ? DateTimeOffset.UtcNow : item.CompletedAt,
            DurationMs = item.DurationMs,
            CompactedRawEvents = item.CompactedRawEvents,
            CompactedWorkingItems = item.CompactedWorkingItems,
            ArchivedRawEvents = item.ArchivedRawEvents,
            ArchivedWorkingItems = item.ArchivedWorkingItems,
            RemovedDuplicates = item.RemovedDuplicates,
            Warnings = [.. item.Warnings],
            Errors = [.. item.Errors]
        };
    }
}
