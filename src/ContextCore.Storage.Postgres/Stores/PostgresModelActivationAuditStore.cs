using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// P0-6：PostgreSQL 持久化 Model Activation Audit Store。
/// 让 HA 场景下模型生命周期审计记录（Activate/Rollback/Retire/Shadow 等）可跨进程持久化与查询。
/// </summary>
/// <remarks>
/// 设计要点：
///   1. 表 <c>model_activation_audit</c> 以 <c>audit_id</c> 为主键，append-only（无 ON CONFLICT 子句）。
///   2. <see cref="AppendAsync"/> 通过 INSERT 追加；不抛异常（best-effort，与契约"不抛异常"语义一致）。
///   3. <see cref="ListByModelAsync"/> 通过 (model_artifact_id, timestamp DESC) 索引按模型工件 ID 查询历史。
///   4. <see cref="ListAllAsync"/> 通过 (timestamp DESC) 索引按时间倒序列举全部审计记录。
///   5. 反规范化列（model_artifact_id / model_name / operation / succeeded / timestamp 等）便于索引查询；
///      完整 <see cref="ModelActivationAuditEntry"/> 保存在 data jsonb。
/// </remarks>
public sealed class PostgresModelActivationAuditStore : PostgresStoreBase, IModelActivationAuditStore
{
    /// <summary>初始化 Postgres 持久化 Model Activation Audit Store。</summary>
    public PostgresModelActivationAuditStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <inheritdoc />
    public async ValueTask AppendAsync(ModelActivationAuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("model_activation_audit")} (
    audit_id, model_artifact_id, model_name, operation, succeeded,
    timestamp, previous_model_artifact_id, operator, reason,
    error_message, node_id, data)
VALUES (
    @audit_id, @model_artifact_id, @model_name, @operation, @succeeded,
    @timestamp, @previous_model_artifact_id, @operator, @reason,
    @error_message, @node_id, @data);
""";
        command.Parameters.AddWithValue("audit_id", entry.AuditId);
        command.Parameters.AddWithValue("model_artifact_id", entry.ModelArtifactId);
        command.Parameters.AddWithValue("model_name", entry.ModelName);
        command.Parameters.AddWithValue("operation", (byte)entry.Operation);
        command.Parameters.AddWithValue("succeeded", entry.Succeeded);
        command.Parameters.AddWithValue("timestamp", entry.Timestamp);
        command.Parameters.AddWithValue("previous_model_artifact_id", (object?)entry.PreviousModelArtifactId ?? DBNull.Value);
        command.Parameters.AddWithValue("operator", (object?)entry.Operator ?? DBNull.Value);
        command.Parameters.AddWithValue("reason", (object?)entry.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("error_message", (object?)entry.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("node_id", (object?)entry.NodeId ?? DBNull.Value);
        AddJson(command, "data", entry);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ModelActivationAuditEntry>> ListByModelAsync(
        string modelArtifactId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelArtifactId))
        {
            return Array.Empty<ModelActivationAuditEntry>();
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("model_activation_audit")}
WHERE model_artifact_id = @model_artifact_id
ORDER BY timestamp ASC;
""";
        command.Parameters.AddWithValue("model_artifact_id", modelArtifactId);
        return await ExecuteReaderJsonAsync<ModelActivationAuditEntry>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ModelActivationAuditEntry>> ListAllAsync(
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        var limit = take > 0 ? take : 100;
        command.CommandText = $"""
SELECT data
FROM {Table("model_activation_audit")}
ORDER BY timestamp ASC
LIMIT @limit;
""";
        command.Parameters.AddWithValue("limit", limit);
        return await ExecuteReaderJsonAsync<ModelActivationAuditEntry>(command, cancellationToken).ConfigureAwait(false);
    }
}
