using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 持久化 Cluster Model Slot Store。
/// 单行集群指针（single champion）：每个 slot_name（如 "primary"）至多一行记录，
/// 通过 CAS on revision 原子切换 ActiveModelArtifactId，确保集群内同一时刻只有一个 Champion 模型。
/// </summary>
/// <remarks>
/// 替代 PostgresDesiredModelStateStore 的多行 Active/Inactive 模型——
/// 旧实现允许同一时刻多条 Active 记录并存，激活新模型时也没有同事务把旧模型改为 Inactive。
/// 单行表 + CAS 是最轻量的 HA 真相源：一次 UPDATE 即可原子完成"旧模型失效 + 新模型激活"。
/// </remarks>
public sealed class PostgresClusterModelSlotStore : PostgresStoreBase, IClusterModelSlotStore
{
    public PostgresClusterModelSlotStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async ValueTask<ClusterModelSlot?> GetAsync(string slotName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slotName))
        {
            return null;
        }

        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT slot_name, active_model_artifact_id, content_hash, revision, desired_status, updated_at, updated_by
FROM {Table("cluster_model_slots")}
WHERE slot_name = @slot_name;
""";
        command.Parameters.AddWithValue("slot_name", slotName);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return ReadSlot(reader);
    }

    public async ValueTask<ClusterModelSlot?> TryUpdateAsync(
        string slotName,
        long expectedRevision,
        string? activeModelArtifactId,
        string? contentHash,
        string desiredStatus,
        string? updatedBy,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slotName))
        {
            return null;
        }

        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // CAS：仅当当前 revision = expectedRevision 时才更新，revision 自增到 revision+1。
        // 并发更新（expectedRevision 过旧）时 WHERE 不匹配，RETURNING 无行 → 返回 null。
        // 一次 UPDATE 原子完成"旧 Champion 失效 + 新 Champion 激活"，无需同事务改多条 DesiredModelState。
        command.CommandText = $"""
UPDATE {Table("cluster_model_slots")}
SET active_model_artifact_id = @artifactId,
    content_hash = @contentHash,
    revision = revision + 1,
    desired_status = @desiredStatus,
    updated_at = now(),
    updated_by = @updatedBy
WHERE slot_name = @slot_name AND revision = @expectedRevision
RETURNING slot_name, active_model_artifact_id, content_hash, revision, desired_status, updated_at, updated_by;
""";
        command.Parameters.AddWithValue("slot_name", slotName);
        command.Parameters.AddWithValue("expectedRevision", expectedRevision);
        command.Parameters.AddWithValue("artifactId", (object?)activeModelArtifactId ?? DBNull.Value);
        command.Parameters.AddWithValue("contentHash", (object?)contentHash ?? DBNull.Value);
        command.Parameters.AddWithValue("desiredStatus", desiredStatus);
        command.Parameters.AddWithValue("updatedBy", (object?)updatedBy ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null; // CAS 失败：expectedRevision 不匹配（并发更新）
        }

        return ReadSlot(reader);
    }

    public async ValueTask<ClusterModelSlot> GetOrCreateAsync(string slotName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slotName))
        {
            throw new ArgumentException("slotName 不能为空。", nameof(slotName));
        }

        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // INSERT ... ON CONFLICT DO NOTHING：首次创建时插入 Inactive 空槽；
        // 已存在时返回已有行。RETURNING 保证两种情况都能取到当前 slot 状态。
        command.CommandText = $"""
INSERT INTO {Table("cluster_model_slots")} (slot_name, desired_status)
VALUES (@slot_name, 'Inactive')
ON CONFLICT (slot_name) DO NOTHING
RETURNING slot_name, active_model_artifact_id, content_hash, revision, desired_status, updated_at, updated_by;
""";
        command.Parameters.AddWithValue("slot_name", slotName);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return ReadSlot(reader);
        }

        // ON CONFLICT DO NOTHING 且 RETURNING 无行：表示行已存在但被并发事务锁定/未返回。
        // 走 GetAsync 兜底读取已存在的行。
        return (await GetAsync(slotName, ct).ConfigureAwait(false))
            ?? throw new InvalidOperationException(
                $"ClusterModelSlot '{slotName}' 初始化失败：既无法 INSERT 也无法 SELECT。");
    }

    private static ClusterModelSlot ReadSlot(System.Data.Common.DbDataReader reader)
    {
        var slotNameOrdinal = reader.GetOrdinal("slot_name");
        var artifactIdOrdinal = reader.GetOrdinal("active_model_artifact_id");
        var contentHashOrdinal = reader.GetOrdinal("content_hash");
        var revisionOrdinal = reader.GetOrdinal("revision");
        var desiredStatusOrdinal = reader.GetOrdinal("desired_status");
        var updatedAtOrdinal = reader.GetOrdinal("updated_at");
        var updatedByOrdinal = reader.GetOrdinal("updated_by");

        return new ClusterModelSlot
        {
            SlotName = reader.GetString(slotNameOrdinal),
            ActiveModelArtifactId = reader.IsDBNull(artifactIdOrdinal) ? null : reader.GetString(artifactIdOrdinal),
            ContentHash = reader.IsDBNull(contentHashOrdinal) ? null : reader.GetString(contentHashOrdinal),
            Revision = reader.GetInt64(revisionOrdinal),
            DesiredStatus = reader.GetString(desiredStatusOrdinal),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(updatedAtOrdinal),
            UpdatedBy = reader.IsDBNull(updatedByOrdinal) ? null : reader.GetString(updatedByOrdinal)
        };
    }
}
