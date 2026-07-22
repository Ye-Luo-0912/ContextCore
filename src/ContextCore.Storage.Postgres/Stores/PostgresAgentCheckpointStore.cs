using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL Agent checkpoint 持久化存储。
/// R26-2：替代 <see cref="ContextCore.Core.Services.Agent.InMemoryAgentCheckpointStore"/>，
/// 让 Postgres provider 在 HA 场景下能持久化 agent session checkpoint 以支持跨进程 resume。
/// </summary>
/// <remarks>
/// 设计要点：
///   1. 表 <c>agent_checkpoints</c> 反规范化 session 字段（session_value / runtime_kind / workspace_id / collection_id）
///      以便按 session 索引查询；完整 <see cref="AgentCheckpoint"/> 对象保存在 <c>data jsonb</c>，由 store 反序列化。
///   2. 主键 (workspace_id, checkpoint_id)：跨 workspace 隔离 + 同 workspace 内 checkpoint id 唯一。
///   3. <see cref="ListAsync"/> 按 session_value 过滤 + created_at DESC，与 InMemory 实现语义一致。
///   4. <see cref="SaveAsync"/> 幂等（同主键覆盖）。
///   5. P0-6 修复：<see cref="GetAsync"/> / <see cref="DeleteAsync"/> 必须传 workspaceId，
///      SQL WHERE 同时匹配 (workspace_id, checkpoint_id)，避免跨 workspace 误读 / 误删。
/// </remarks>
public sealed class PostgresAgentCheckpointStore : PostgresStoreBase, IAgentCheckpointStore
{
    public PostgresAgentCheckpointStore(PostgresConnectionFactory connectionFactory, PostgresJsonSerializer serializer, PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <inheritdoc />
    public async Task SaveAsync(AgentCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("agent_checkpoints")} (
    workspace_id, collection_id, session_value, runtime_kind, checkpoint_id,
    turn_id, snapshot_id, created_at, state_json, data)
VALUES (
    @workspace_id, @collection_id, @session_value, @runtime_kind, @checkpoint_id,
    @turn_id, @snapshot_id, @created_at, @state_json, @data)
ON CONFLICT (workspace_id, checkpoint_id) DO UPDATE SET
    collection_id = EXCLUDED.collection_id,
    session_value = EXCLUDED.session_value,
    runtime_kind = EXCLUDED.runtime_kind,
    turn_id = EXCLUDED.turn_id,
    snapshot_id = EXCLUDED.snapshot_id,
    created_at = EXCLUDED.created_at,
    state_json = EXCLUDED.state_json,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", checkpoint.Session.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", (object?)checkpoint.Session.CollectionId ?? DBNull.Value);
        command.Parameters.AddWithValue("session_value", checkpoint.Session.Value);
        command.Parameters.AddWithValue("runtime_kind", checkpoint.Session.RuntimeKind.ToString());
        command.Parameters.AddWithValue("checkpoint_id", checkpoint.CheckpointId);
        command.Parameters.AddWithValue("turn_id", (object?)checkpoint.TurnId ?? DBNull.Value);
        command.Parameters.AddWithValue("snapshot_id", (object?)checkpoint.SnapshotId ?? DBNull.Value);
        command.Parameters.AddWithValue("created_at", checkpoint.CreatedAt);
        command.Parameters.AddWithValue("state_json", checkpoint.StateJson ?? string.Empty);
        AddJson(command, "data", checkpoint);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AgentCheckpoint?> GetAsync(
        string workspaceId,
        string checkpointId,
        CancellationToken cancellationToken = default)
    {
        // P0-6：必须同时匹配 (workspace_id, checkpoint_id)，避免跨 workspace 误读
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("agent_checkpoints")}
WHERE workspace_id = @workspace_id AND checkpoint_id = @checkpoint_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("checkpoint_id", checkpointId);
        return await ExecuteScalarJsonAsync<AgentCheckpoint>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentCheckpoint>> ListAsync(
        AgentSessionId sessionId,
        int take = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (take < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "take must be >= 0");
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("agent_checkpoints")}
WHERE workspace_id = @workspace_id AND session_value = @session_value
ORDER BY created_at DESC, checkpoint_id DESC
LIMIT @take;
""";
        command.Parameters.AddWithValue("workspace_id", sessionId.WorkspaceId);
        command.Parameters.AddWithValue("session_value", sessionId.Value);
        command.Parameters.AddWithValue("take", take == 0 ? int.MaxValue : take);

        return await ExecuteReaderJsonAsync<AgentCheckpoint>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        string workspaceId,
        string checkpointId,
        CancellationToken cancellationToken = default)
    {
        // P0-6：必须同时匹配 (workspace_id, checkpoint_id)，避免跨 workspace 误删
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
DELETE FROM {Table("agent_checkpoints")}
WHERE workspace_id = @workspace_id AND checkpoint_id = @checkpoint_id;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("checkpoint_id", checkpointId);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }
}
