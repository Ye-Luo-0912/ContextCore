using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL Agent task state 持久化存储。
/// 替代 <see cref="ContextCore.Core.Services.Agent.InMemoryAgentTaskStateStore"/>，
/// 让 Postgres provider 在 HA 场景下能持久化 agent task state 以支持跨请求恢复。
/// </summary>
/// <remarks>
/// 设计要点：
///   1. 表 <c>agent_task_states</c> 反规范化 session 字段（session_value / runtime_kind / workspace_id / collection_id）
///      以便按 session 索引查询；完整 <see cref="AgentTaskState"/> 对象保存在 <c>data jsonb</c>，由 store 反序列化。
///   2. 主键 (workspace_id, task_id)：跨 workspace 隔离 + 同 workspace 内 task id 唯一。
///   3. <see cref="ListBySessionAsync"/> 按 session_value 过滤 + updated_at DESC，与 InMemory 实现语义一致。
///   4. <see cref="SaveAsync"/> 幂等（同主键覆盖）；状态机转换由调用方负责。
///   5. P0-6 修复：<see cref="GetAsync"/> / <see cref="DeleteAsync"/> 必须传 workspaceId，
///      SQL WHERE 同时匹配 (workspace_id, task_id)，避免跨 workspace 误读 / 误删。
/// </remarks>
public sealed class PostgresAgentTaskStateStore : PostgresStoreBase, IAgentTaskStateStore
{
    public PostgresAgentTaskStateStore(PostgresConnectionFactory connectionFactory, PostgresJsonSerializer serializer, PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <inheritdoc />
    public async Task SaveAsync(AgentTaskState taskState, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskState);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("agent_task_states")} (
    workspace_id, collection_id, session_value, runtime_kind, task_id,
    status, created_at, updated_at, data)
VALUES (
    @workspace_id, @collection_id, @session_value, @runtime_kind, @task_id,
    @status, @created_at, @updated_at, @data)
ON CONFLICT (workspace_id, task_id) DO UPDATE SET
    collection_id = EXCLUDED.collection_id,
    session_value = EXCLUDED.session_value,
    runtime_kind = EXCLUDED.runtime_kind,
    status = EXCLUDED.status,
    created_at = EXCLUDED.created_at,
    updated_at = EXCLUDED.updated_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", taskState.Session.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", (object?)taskState.Session.CollectionId ?? DBNull.Value);
        command.Parameters.AddWithValue("session_value", taskState.Session.Value);
        command.Parameters.AddWithValue("runtime_kind", taskState.Session.RuntimeKind.ToString());
        command.Parameters.AddWithValue("task_id", taskState.TaskId);
        command.Parameters.AddWithValue("status", taskState.Status ?? string.Empty);
        command.Parameters.AddWithValue("created_at", taskState.CreatedAt);
        command.Parameters.AddWithValue("updated_at", taskState.UpdatedAt);
        AddJson(command, "data", taskState);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AgentTaskState?> GetAsync(
        string workspaceId,
        string taskId,
        CancellationToken cancellationToken = default)
    {
        // 必须同时匹配 (workspace_id, task_id)，避免跨 workspace 误读
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("agent_task_states")}
WHERE workspace_id = @workspace_id AND task_id = @task_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("task_id", taskId);
        return await ExecuteScalarJsonAsync<AgentTaskState>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentTaskState>> ListBySessionAsync(
        AgentSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("agent_task_states")}
WHERE workspace_id = @workspace_id AND session_value = @session_value
ORDER BY updated_at DESC, task_id DESC;
""";
        command.Parameters.AddWithValue("workspace_id", sessionId.WorkspaceId);
        command.Parameters.AddWithValue("session_value", sessionId.Value);

        return await ExecuteReaderJsonAsync<AgentTaskState>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        string workspaceId,
        string taskId,
        CancellationToken cancellationToken = default)
    {
        // 必须同时匹配 (workspace_id, task_id)，避免跨 workspace 误删
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
DELETE FROM {Table("agent_task_states")}
WHERE workspace_id = @workspace_id AND task_id = @task_id;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("task_id", taskId);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }
}
