using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;
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

    /// <summary>
    /// P0-8：审计事件必须落盘。PostgresContextEventSink 声明为 <see cref="ContextEventSinkKind.Required"/>，
    /// 使 <see cref="CompositeContextEventSink"/> 的 Kind 升级为 Required，
    /// 外层 <see cref="BoundedChannelContextEventSink"/> 绕过有界通道、直接同步写入 PostgreSQL。
    /// 审计事件不会因通道满而丢失。
    /// </summary>
    public ContextEventSinkKind Kind => ContextEventSinkKind.Required;

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
                event_id, workspace_id, collection_id, operation_id, operation_name, level, message, duration_ms, created_at, entity_type, entity_id, operation, data)
            VALUES (
                @event_id, @workspace_id, @collection_id, @operation_id, @operation_name, @level, @message, @duration_ms, @created_at, @entity_type, @entity_id, @operation, @data)
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
        command.Parameters.AddWithValue("entity_type", (object?)operationEvent.EntityType ?? DBNull.Value);
        command.Parameters.AddWithValue("entity_id", (object?)operationEvent.EntityId ?? DBNull.Value);
        command.Parameters.AddWithValue("operation", (object?)operationEvent.Operation ?? DBNull.Value);
        AddJson(command, "data", operationEvent);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// R13.4 #1：批量写入路径。在单个连接 + 单个事务内通过 multi-row VALUES INSERT
    /// 一次性写入所有事件，避免逐行 round-trip 开销。
    /// 事件分块提交以避免单条 SQL 参数数量超过 PostgreSQL 65535 限制
    /// （每行 13 个参数，分块上限 4000 行留出余量）。
    /// </summary>
    public async Task EmitBatchAsync(
        IReadOnlyList<ContextOperationEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return;
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        const int ChunkSize = 4000;
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        for (var offset = 0; offset < events.Count; offset += ChunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkLength = Math.Min(ChunkSize, events.Count - offset);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandTimeout = Options.CommandTimeoutSeconds;

                var sql = new System.Text.StringBuilder();
                sql.Append("INSERT INTO ");
                sql.Append(Table("context_operation_events"));
                sql.Append(" (event_id, workspace_id, collection_id, operation_id, operation_name, level, message, duration_ms, created_at, entity_type, entity_id, operation, data) VALUES ");

                for (var i = 0; i < chunkLength; i++)
                {
                    if (i > 0) sql.Append(", ");
                    sql.Append('(');
                    sql.Append("@event_id_").Append(i);
                    sql.Append(", @workspace_id_").Append(i);
                    sql.Append(", @collection_id_").Append(i);
                    sql.Append(", @operation_id_").Append(i);
                    sql.Append(", @operation_name_").Append(i);
                    sql.Append(", @level_").Append(i);
                    sql.Append(", @message_").Append(i);
                    sql.Append(", @duration_ms_").Append(i);
                    sql.Append(", @created_at_").Append(i);
                    sql.Append(", @entity_type_").Append(i);
                    sql.Append(", @entity_id_").Append(i);
                    sql.Append(", @operation_").Append(i);
                    sql.Append(", @data_").Append(i);
                    sql.Append(')');

                    var evt = events[offset + i];
                    command.Parameters.AddWithValue($"event_id_{i}", evt.EventId);
                    command.Parameters.AddWithValue($"workspace_id_{i}", evt.WorkspaceId);
                    command.Parameters.AddWithValue($"collection_id_{i}", (object?)evt.CollectionId ?? DBNull.Value);
                    command.Parameters.AddWithValue($"operation_id_{i}", evt.OperationId);
                    command.Parameters.AddWithValue($"operation_name_{i}", evt.OperationName);
                    command.Parameters.AddWithValue($"level_{i}", evt.Level.ToString());
                    command.Parameters.AddWithValue($"message_{i}", evt.Message);
                    command.Parameters.AddWithValue($"duration_ms_{i}", (object?)evt.Duration?.TotalMilliseconds ?? DBNull.Value);
                    command.Parameters.AddWithValue($"created_at_{i}", evt.CreatedAt);
                    command.Parameters.AddWithValue($"entity_type_{i}", (object?)evt.EntityType ?? DBNull.Value);
                    command.Parameters.AddWithValue($"entity_id_{i}", (object?)evt.EntityId ?? DBNull.Value);
                    command.Parameters.AddWithValue($"operation_{i}", (object?)evt.Operation ?? DBNull.Value);
                    AddJson(command, $"data_{i}", evt);
                }

                sql.Append(" ON CONFLICT (workspace_id, event_id) DO NOTHING;");
                command.CommandText = sql.ToString();
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await SafeRollbackAsync(transaction, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }

    private static async Task SafeRollbackAsync(NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        try
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 回滚失败时静默：原始异常更重要
        }
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
            results.Add(item);
        }

        return results;
    }
}
