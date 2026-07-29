using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// P0-3：PostgreSQL 持久化 Durable Tool Result 缓存。
/// 让 HA 场景下已 Committed/ResultDelivered 的 tool 结果可跨进程持久化与崩溃恢复读取，
/// 防止崩溃恢复时已执行的外部副作用结果丢失（被迫重新 Dispatch）。
/// </summary>
/// <remarks>
/// 设计要点（参考 <see cref="PostgresToolDispatchJournal"/> 的连接/迁移模式）：
///   1. 表 <c>tool_dispatch_results</c> 以 <c>tool_call_id</c> 为主键，按 toolCallId 幂等覆盖。
///   2. <see cref="GetAsync"/> 通过主键读取 <c>result</c> jsonb 列并反序列化为 <see cref="DurableToolResult"/>。
///   3. <see cref="SaveAsync"/> 使用 <c>INSERT ... ON CONFLICT (tool_call_id) DO UPDATE</c> 幂等 upsert，
///      同时写入 <c>result</c> jsonb（完整对象，供读取反序列化）与若干反规范化列（request_id / side_effect /
///      succeeded 等，供 SQL 查询/对账）。
///   4. 与 <see cref="IToolDispatchJournal.MarkCommittedWithResultAsync"/> 的关系：
///      写入路径优先走 Journal 同事务持久化 state + result；本接口的 <see cref="SaveAsync"/>
///      用于无 journal 路径或独立缓存场景（接口注释所述）。
/// </remarks>
public sealed class PostgresDurableToolResultStore : PostgresStoreBase, IDurableToolResultStore
{
    /// <summary>初始化 Postgres 持久化 Durable Tool Result 缓存。</summary>
    public PostgresDurableToolResultStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <inheritdoc />
    public async Task<DurableToolResult?> GetAsync(string toolCallId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            return null;
        }

        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT result
FROM {Table("tool_dispatch_results")}
WHERE tool_call_id = @tool_call_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("tool_call_id", toolCallId);
        return await ExecuteScalarJsonAsync<DurableToolResult>(command, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveAsync(string toolCallId, DurableToolResult result, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolCallId);
        ArgumentNullException.ThrowIfNull(result);

        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("tool_dispatch_results")} (
    tool_call_id, request_id, idempotency_key, side_effect, external_operation_id,
    result, succeeded, error, duration_ms, created_at)
VALUES (
    @tool_call_id, @request_id, @idempotency_key, @side_effect, @external_operation_id,
    @result, @succeeded, @error, @duration_ms, @created_at)
ON CONFLICT (tool_call_id) DO UPDATE SET
    request_id = EXCLUDED.request_id,
    idempotency_key = EXCLUDED.idempotency_key,
    side_effect = EXCLUDED.side_effect,
    external_operation_id = EXCLUDED.external_operation_id,
    result = EXCLUDED.result,
    succeeded = EXCLUDED.succeeded,
    error = EXCLUDED.error,
    duration_ms = EXCLUDED.duration_ms,
    created_at = EXCLUDED.created_at;
""";
        command.Parameters.AddWithValue("tool_call_id", toolCallId);
        command.Parameters.AddWithValue("request_id", result.RequestId);
        command.Parameters.AddWithValue("idempotency_key", (object?)result.IdempotencyKey ?? DBNull.Value);
        command.Parameters.AddWithValue("side_effect", result.SideEffect.ToString());
        command.Parameters.AddWithValue("external_operation_id", (object?)result.ExternalOperationId ?? DBNull.Value);
        AddJson(command, "result", result);
        command.Parameters.AddWithValue("succeeded", result.Succeeded);
        command.Parameters.AddWithValue("error", (object?)result.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("duration_ms", (long)Math.Round(result.DurationMs));
        command.Parameters.AddWithValue("created_at", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
