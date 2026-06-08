using ContextCore.Abstractions;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 上下文事件接收器，实现 <see cref="IContextEventSink"/>。
/// 将操作审计事件持久化写入 cc_context_operation_events 表。
/// </summary>
public sealed class PostgresContextEventSink : PostgresStoreBase, IContextEventSink
{
    public PostgresContextEventSink(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <summary>将操作事件序列化并保存到 PostgreSQL 中。</summary>
    public async Task EmitAsync(
        ContextOperationEvent operationEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationEvent);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
            INSERT INTO {Table("context_operation_events")} (
                event_id, workspace_id, collection_id, operation_id, operation_name, level, message, duration_ms, created_at, data)
            VALUES (
                @event_id, @workspace_id, @collection_id, @operation_id, @operation_name, @level, @message, @duration_ms, @created_at, @data)
            ON CONFLICT (workspace_id, event_id) DO NOTHING;
            """;

        command.Parameters.AddWithValue("event_id", operationEvent.EventId);
        command.Parameters.AddWithValue("workspace_id", operationEvent.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", (object?)operationEvent.CollectionId ?? DBNull.Value);
        command.Parameters.AddWithValue("operation_id", operationEvent.OperationId);
        command.Parameters.AddWithValue("operation_name", operationEvent.OperationName);
        command.Parameters.AddWithValue("level", operationEvent.Level.ToString());
        command.Parameters.AddWithValue("message", operationEvent.Message);
        command.Parameters.AddWithValue("duration_ms", (object?)operationEvent.Duration?.TotalMilliseconds ?? DBNull.Value);
        command.Parameters.AddWithValue("created_at", operationEvent.CreatedAt);
        AddJson(command, "data", operationEvent);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>查询指定工作空间的最近操作审计事件列表（用于测试及控制台显示）。</summary>
    public async Task<IReadOnlyList<ContextOperationEvent>> QueryEventsAsync(
        string workspaceId,
        int take,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
            SELECT data FROM {Table("context_operation_events")}
            WHERE workspace_id = @workspace_id
            ORDER BY created_at DESC
            LIMIT {TakeOrDefault(take)};
            """;
        command.Parameters.AddWithValue("workspace_id", workspaceId);

        var results = new List<ContextOperationEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var item = Serializer.Deserialize<ContextOperationEvent>(reader.GetString(0));
            if (item is not null)
            {
                results.Add(item);
            }
        }

        return results;
    }
}
