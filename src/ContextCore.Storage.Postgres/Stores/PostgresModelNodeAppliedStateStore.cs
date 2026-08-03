using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 持久化 Model Node Applied State Store。
/// 每个 (node_id, slot_name) 一行，记录节点最后成功应用的集群槽位 Revision 与模型内容，
/// 供节点重启后查询本节点上次应用了什么（审计 / 漂移分析）。
/// </summary>
/// <remarks>
/// Upsert 通过 AppliedRevision 做乐观并发控制（CAS）：仅当新 Revision ≥ 已存 Revision 时覆盖，
/// 防止陈旧节点回写覆盖更新的应用记录。
/// </remarks>
public sealed class PostgresModelNodeAppliedStateStore : PostgresStoreBase, IModelNodeAppliedStateStore
{
    public PostgresModelNodeAppliedStateStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async ValueTask<ModelNodeAppliedState?> GetAsync(
        string nodeId,
        string slotName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(slotName))
        {
            return null;
        }

        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT node_id, slot_name, applied_revision, model_artifact_id, content_hash, engine_generation, is_isolated, drift_reported_at, isolation_reason, applied_at
FROM {Table("model_node_applied_state")}
WHERE node_id = @node_id AND slot_name = @slot_name;
""";
        command.Parameters.AddWithValue("node_id", nodeId);
        command.Parameters.AddWithValue("slot_name", slotName);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return ReadState(reader);
    }

    public async ValueTask<ModelNodeAppliedState> UpsertAsync(ModelNodeAppliedState state, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state.NodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.SlotName);

        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // CAS upsert：ON CONFLICT DO UPDATE 仅在 新 AppliedRevision ≥ 已存 AppliedRevision 时生效；
        // WHERE 不满足时无 RETURNING 行，回退 SELECT 返回已存记录（拒绝陈旧回写）。
        // 成功应用（记录反映本地引擎实际内容）时 is_isolated=false，漂移隔离随之清除。
        command.CommandText = $"""
INSERT INTO {Table("model_node_applied_state")} (node_id, slot_name, applied_revision, model_artifact_id, content_hash, engine_generation, is_isolated, drift_reported_at, isolation_reason, applied_at)
VALUES (@node_id, @slot_name, @applied_revision, @model_artifact_id, @content_hash, @engine_generation, false, NULL, NULL, now())
ON CONFLICT (node_id, slot_name) DO UPDATE
SET applied_revision = EXCLUDED.applied_revision,
    model_artifact_id = EXCLUDED.model_artifact_id,
    content_hash = EXCLUDED.content_hash,
    engine_generation = EXCLUDED.engine_generation,
    is_isolated = false,
    drift_reported_at = NULL,
    isolation_reason = NULL,
    applied_at = EXCLUDED.applied_at
WHERE {Table("model_node_applied_state")}.applied_revision <= EXCLUDED.applied_revision
RETURNING node_id, slot_name, applied_revision, model_artifact_id, content_hash, engine_generation, is_isolated, drift_reported_at, isolation_reason, applied_at;
""";
        command.Parameters.AddWithValue("node_id", state.NodeId);
        command.Parameters.AddWithValue("slot_name", state.SlotName);
        command.Parameters.AddWithValue("applied_revision", state.AppliedRevision);
        command.Parameters.AddWithValue("model_artifact_id", (object?)state.ModelArtifactId ?? DBNull.Value);
        command.Parameters.AddWithValue("content_hash", (object?)state.ContentHash ?? DBNull.Value);
        command.Parameters.AddWithValue("engine_generation", (object?)state.EngineGeneration ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return ReadState(reader);
        }

        // CAS 拒绝（新 Revision < 已存 Revision）：返回已存记录。
        return (await GetAsync(state.NodeId, state.SlotName, ct).ConfigureAwait(false))
            ?? throw new InvalidOperationException(
                $"ModelNodeAppliedState '{state.NodeId}/{state.SlotName}' 写入失败：既无法 INSERT 也无法 SELECT。");
    }

    public async ValueTask<IReadOnlyList<ModelNodeAppliedState>> ListBySlotAsync(string slotName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slotName))
        {
            return Array.Empty<ModelNodeAppliedState>();
        }

        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT node_id, slot_name, applied_revision, model_artifact_id, content_hash, engine_generation, is_isolated, drift_reported_at, isolation_reason, applied_at
FROM {Table("model_node_applied_state")}
WHERE slot_name = @slot_name
ORDER BY node_id;
""";
        command.Parameters.AddWithValue("slot_name", slotName);

        var results = new List<ModelNodeAppliedState>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(ReadState(reader));
        }
        return results;
    }

    public async ValueTask<ModelNodeAppliedState?> MarkIsolatedAsync(
        string nodeId,
        string slotName,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(slotName);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // 幂等隔离标记：已隔离时仅更新原因与时间；无记录时创建隔离标记（审计链完整）。
        // 不改变 applied_revision / 模型内容（隔离是叠加状态，下一次成功应用自然清除）。
        command.CommandText = $"""
INSERT INTO {Table("model_node_applied_state")} (node_id, slot_name, applied_revision, applied_at, is_isolated, drift_reported_at, isolation_reason)
VALUES (@node_id, @slot_name, 0, now(), true, now(), @reason)
ON CONFLICT (node_id, slot_name) DO UPDATE
SET is_isolated = true,
    drift_reported_at = now(),
    isolation_reason = EXCLUDED.isolation_reason
RETURNING node_id, slot_name, applied_revision, model_artifact_id, content_hash, engine_generation, is_isolated, drift_reported_at, isolation_reason, applied_at;
""";
        command.Parameters.AddWithValue("node_id", nodeId);
        command.Parameters.AddWithValue("slot_name", slotName);
        command.Parameters.AddWithValue("reason", reason);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return ReadState(reader);
        }

        return null;
    }

    private static ModelNodeAppliedState ReadState(System.Data.Common.DbDataReader reader)
    {
        var nodeIdOrdinal = reader.GetOrdinal("node_id");
        var slotNameOrdinal = reader.GetOrdinal("slot_name");
        var appliedRevisionOrdinal = reader.GetOrdinal("applied_revision");
        var artifactIdOrdinal = reader.GetOrdinal("model_artifact_id");
        var contentHashOrdinal = reader.GetOrdinal("content_hash");
        var engineGenerationOrdinal = reader.GetOrdinal("engine_generation");
        var isIsolatedOrdinal = reader.GetOrdinal("is_isolated");
        var driftReportedAtOrdinal = reader.GetOrdinal("drift_reported_at");
        var isolationReasonOrdinal = reader.GetOrdinal("isolation_reason");
        var appliedAtOrdinal = reader.GetOrdinal("applied_at");

        return new ModelNodeAppliedState
        {
            NodeId = reader.GetString(nodeIdOrdinal),
            SlotName = reader.GetString(slotNameOrdinal),
            AppliedRevision = reader.GetInt64(appliedRevisionOrdinal),
            ModelArtifactId = reader.IsDBNull(artifactIdOrdinal) ? null : reader.GetString(artifactIdOrdinal),
            ContentHash = reader.IsDBNull(contentHashOrdinal) ? null : reader.GetString(contentHashOrdinal),
            EngineGeneration = reader.IsDBNull(engineGenerationOrdinal) ? null : reader.GetInt64(engineGenerationOrdinal),
            Isolated = reader.GetBoolean(isIsolatedOrdinal),
            DriftReportedAt = reader.IsDBNull(driftReportedAtOrdinal) ? null : reader.GetFieldValue<DateTimeOffset>(driftReportedAtOrdinal),
            IsolationReason = reader.IsDBNull(isolationReasonOrdinal) ? null : reader.GetString(isolationReasonOrdinal),
            AppliedAt = reader.GetFieldValue<DateTimeOffset>(appliedAtOrdinal)
        };
    }
}
