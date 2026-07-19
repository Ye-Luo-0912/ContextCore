using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>PostgreSQL 关系存储，使用结构化列加 jsonb 原文保存关系边。</summary>
public sealed class PostgresRelationStore : PostgresStoreBase, IRelationStore
{
    public PostgresRelationStore(PostgresConnectionFactory connectionFactory, PostgresJsonSerializer serializer, PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <summary>GRAPH-11：SaveAsync 委托 BatchUpsertAsync，保留为单条便利方法。</summary>
    public Task SaveAsync(ContextRelation relation, CancellationToken cancellationToken = default)
        => BatchUpsertAsync([relation], cancellationToken);

    /// <summary>按关系 ID 读取单条边；供 Postgres provider diagnostics/parity 使用，不改变 IRelationStore 契约。</summary>
    public async Task<ContextRelation?> GetAsync(
        string workspaceId,
        string collectionId,
        string relationId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("relations")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id
  AND id = @id
LIMIT 1;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("id", relationId);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is string json ? Serializer.Deserialize<ContextRelation>(json) : null;
    }

    /// <summary>删除单条边；仅用于显式 provider parity/cleanup，不参与默认运行时。</summary>
    public async Task<bool> DeleteAsync(
        string workspaceId,
        string collectionId,
        string relationId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
DELETE FROM {Table("relations")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id
  AND id = @id;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("id", relationId);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <summary>清理显式 smoke/canary scope；只供受控验证流程调用。</summary>
    public async Task<int> DeleteByScopeAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
DELETE FROM {Table("relations")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// GRAPH-11：批量 upsert 改用 NpgsqlBatch，单次往返提交所有语句；单事务保证原子性。
    /// </summary>
    public async Task BatchUpsertAsync(IEnumerable<ContextRelation> relations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relations);
        var list = relations.ToList();
        if (list.Count == 0) return;

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // GRAPH-11：NpgsqlBatch 单次往返提交全部 upsert 语句
            using var batch = new NpgsqlBatch(connection as NpgsqlConnection, transaction as NpgsqlTransaction);
            batch.Timeout = Options.CommandTimeoutSeconds;

            foreach (var relation in list)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = CompositeContextNormalizer.Normalize(relation);
                var batchCommand = new NpgsqlBatchCommand
                {
                    CommandText = $"""
INSERT INTO {Table("relations")} (
    workspace_id, collection_id, id, source_id, target_id, relation_type,
    weight, confidence, created_at, data)
VALUES (
    @workspace_id, @collection_id, @id, @source_id, @target_id, @relation_type,
    @weight, @confidence, @created_at, @data)
ON CONFLICT (workspace_id, collection_id, id) DO UPDATE SET
    source_id = EXCLUDED.source_id,
    target_id = EXCLUDED.target_id,
    relation_type = EXCLUDED.relation_type,
    weight = EXCLUDED.weight,
    confidence = EXCLUDED.confidence,
    created_at = EXCLUDED.created_at,
    data = EXCLUDED.data;
"""
                };
                batchCommand.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
                batchCommand.Parameters.AddWithValue("collection_id", normalized.CollectionId);
                batchCommand.Parameters.AddWithValue("id", normalized.Id);
                batchCommand.Parameters.AddWithValue("source_id", normalized.SourceId);
                batchCommand.Parameters.AddWithValue("target_id", normalized.TargetId);
                batchCommand.Parameters.AddWithValue("relation_type", normalized.RelationType);
                batchCommand.Parameters.AddWithValue("weight", normalized.Weight);
                batchCommand.Parameters.AddWithValue("confidence", normalized.Confidence);
                batchCommand.Parameters.AddWithValue("created_at", normalized.CreatedAt);
                var dataParam = batchCommand.Parameters.Add("data", NpgsqlDbType.Jsonb);
                dataParam.Value = Serializer.Serialize(normalized);
                batch.BatchCommands.Add(batchCommand);
            }

            await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<ContextRelation>> QueryAsync(ContextRelationQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        var filters = new List<string> { "workspace_id = @workspace_id" };
        command.Parameters.AddWithValue("workspace_id", query.WorkspaceId);
        if (!string.IsNullOrWhiteSpace(query.CollectionId))
        {
            filters.Add("collection_id = @collection_id");
            command.Parameters.AddWithValue("collection_id", query.CollectionId);
        }

        if (!string.IsNullOrWhiteSpace(query.SourceId))
        {
            filters.Add("source_id = @source_id");
            command.Parameters.AddWithValue("source_id", query.SourceId);
        }

        if (!string.IsNullOrWhiteSpace(query.TargetId))
        {
            filters.Add("target_id = @target_id");
            command.Parameters.AddWithValue("target_id", query.TargetId);
        }

        if (!string.IsNullOrWhiteSpace(query.ItemId))
        {
            filters.Add("(source_id = @item_id OR target_id = @item_id)");
            command.Parameters.AddWithValue("item_id", query.ItemId);
        }

        if (!string.IsNullOrWhiteSpace(query.RelationType))
        {
            filters.Add("relation_type = @relation_type");
            command.Parameters.AddWithValue("relation_type", query.RelationType);
        }

        command.Parameters.AddWithValue("take", TakeOrDefault(query.Take));
        var skip = query.Skip > 0 ? query.Skip : 0;
        command.Parameters.AddWithValue("skip", skip);
        command.CommandText = $"""
SELECT data
FROM {Table("relations")}
WHERE {string.Join(" AND ", filters)}
ORDER BY weight DESC, confidence DESC, created_at DESC
LIMIT @take OFFSET @skip;
""";

        var results = new List<ContextRelation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Serializer.Deserialize<ContextRelation>(reader.GetString(0)));
        }

        return results;
    }

    /// <summary>GRAPH-10：统一邻居查询，在 SQL 中过滤和 Limit。</summary>
    public async Task<IReadOnlyList<ContextRelation>> QueryNeighborsAsync(
        RelationNeighborQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        var filters = new List<string> { "workspace_id = @workspace_id" };
        command.Parameters.AddWithValue("workspace_id", query.WorkspaceId);

        if (!string.IsNullOrWhiteSpace(query.CollectionId))
        {
            filters.Add("collection_id = @collection_id");
            command.Parameters.AddWithValue("collection_id", query.CollectionId);
        }

        switch (query.Direction)
        {
            case RelationDirection.Outgoing:
                filters.Add("source_id = @item_id");
                break;
            case RelationDirection.Incoming:
                filters.Add("target_id = @item_id");
                break;
            default:
                filters.Add("(source_id = @item_id OR target_id = @item_id)");
                break;
        }
        command.Parameters.AddWithValue("item_id", query.ItemId);

        if (!string.IsNullOrWhiteSpace(query.RelationType))
        {
            // P0-2：使用真实列 relation_type 而非 JSON 提取（既有索引，且避免大小写敏感问题）
            filters.Add("relation_type = @relation_type");
            command.Parameters.AddWithValue("relation_type", query.RelationType);
        }

        // P3-02：多类型过滤优先于单类型，在 LIMIT 前下推到 SQL
        if (query.AllowedRelationTypes.Count > 0)
        {
            var paramNames = new List<string>();
            for (var i = 0; i < query.AllowedRelationTypes.Count; i++)
            {
                var paramName = $"allowed_rt_{i}";
                paramNames.Add($"@{paramName}");
                command.Parameters.AddWithValue(paramName, query.AllowedRelationTypes[i]);
            }
            // P0-2：使用真实列 relation_type
            filters.Add($"relation_type IN ({string.Join(", ", paramNames)})");
        }

        if (query.MinConfidence > 0)
        {
            // P0-2：修复 cast 优先级 bug。
            // 旧：data ->> 'Confidence'::numeric 实际解析为 data ->> ('Confidence'::numeric)，
            // 即先 cast 字面量 'Confidence' 为 numeric（运行时抛错），而非 cast JSON 提取值。
            // 新：直接使用真实列 confidence（double precision，既有索引）。
            filters.Add("confidence >= @min_confidence");
            command.Parameters.AddWithValue("min_confidence", query.MinConfidence);
        }

        if (query.ExcludedLifecycles.Count > 0)
        {
            var paramNames = new List<string>();
            for (var i = 0; i < query.ExcludedLifecycles.Count; i++)
            {
                var paramName = $"ex_lc_{i}";
                paramNames.Add($"@{paramName}");
                command.Parameters.AddWithValue(paramName, query.ExcludedLifecycles[i]);
            }
            filters.Add($"(data ->> 'Lifecycle' IS NULL OR data ->> 'Lifecycle' NOT IN ({string.Join(", ", paramNames)}))");
        }

        if (query.ExcludedReviewStatuses.Count > 0)
        {
            var paramNames = new List<string>();
            for (var i = 0; i < query.ExcludedReviewStatuses.Count; i++)
            {
                var paramName = $"ex_rs_{i}";
                paramNames.Add($"@{paramName}");
                command.Parameters.AddWithValue(paramName, query.ExcludedReviewStatuses[i]);
            }
            filters.Add($"(data ->> 'ReviewStatus' IS NULL OR data ->> 'ReviewStatus' NOT IN ({string.Join(", ", paramNames)}))");
        }

        var effectiveTake = query.Take > 0 ? query.Take : 100;
        var effectiveSkip = query.Skip > 0 ? query.Skip : 0;
        var maxScan = query.MaxScan > 0 ? query.MaxScan : 1000;
        command.Parameters.AddWithValue("take", effectiveTake);
        command.Parameters.AddWithValue("skip", effectiveSkip);
        command.Parameters.AddWithValue("max_scan", maxScan);

        // P0-2：修复 3 个 SQL bug：
        //   1) 旧：data ->> 'Confidence'::numeric（cast 优先级错误，运行时抛错）→ 改用真实列 confidence
        //   2) 旧：ORDER BY data ->> 'Weight'（字符串排序，"10" 排在 "9" 之前）→ 改用真实列 weight（double precision）
        //   3) 旧：内层 LIMIT @max_scan 无 ORDER BY（截断未排序集合，可能漏掉高权重边）
        //      新：ORDER BY 移入内层，max_scan 作为"取 top N 后再分页"的扫描上限
        command.CommandText = $"""
SELECT data
FROM (
    SELECT data
    FROM {Table("relations")}
    WHERE {string.Join(" AND ", filters)}
    ORDER BY weight DESC, confidence DESC, created_at DESC
    LIMIT @max_scan
) scanned
LIMIT @take OFFSET @skip;
""";
        return await ReadRelationsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 lifecycle 查询；GRAPH-08 起优先查正式字段，兼容旧 Metadata 数据。</summary>
    public async Task<IReadOnlyList<ContextRelation>> QueryByLifecycleAsync(
        string workspaceId,
        string collectionId,
        string lifecycle,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("relations")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id
  AND (
      data ->> 'Lifecycle' = @expected_value
      OR data -> 'Metadata' ->> 'lifecycle' = @expected_value
      OR data -> 'Metadata' ->> 'Lifecycle' = @expected_value
      OR data -> 'metadata' ->> 'lifecycle' = @expected_value
  )
ORDER BY weight DESC, confidence DESC, created_at DESC;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("expected_value", lifecycle);
        return await ReadRelationsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 reviewStatus 查询；GRAPH-08 起优先查正式字段，兼容旧 Metadata 数据。</summary>
    public async Task<IReadOnlyList<ContextRelation>> QueryByReviewStatusAsync(
        string workspaceId,
        string collectionId,
        string reviewStatus,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("relations")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id
  AND (
      data ->> 'ReviewStatus' = @expected_value
      OR data -> 'Metadata' ->> 'reviewStatus' = @expected_value
      OR data -> 'Metadata' ->> 'ReviewStatus' = @expected_value
      OR data -> 'metadata' ->> 'reviewStatus' = @expected_value
  )
ORDER BY weight DESC, confidence DESC, created_at DESC;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("expected_value", reviewStatus);
        return await ReadRelationsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>查询 replacement chain 相关边，不执行图扩展或运行时排序。</summary>
    public async Task<IReadOnlyList<ContextRelation>> QueryReplacementChainRelationsAsync(
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
FROM {Table("relations")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id
  AND (source_id = @item_id OR target_id = @item_id)
  AND relation_type = ANY(@relation_types)
ORDER BY weight DESC, confidence DESC, created_at DESC;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("relation_types", new[]
        {
            ContextRelationTypes.SupersededBy,
            ContextRelationTypes.Replaces,
            ContextRelationTypes.ReplacedBy
        });

        return await ReadRelationsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>统计当前 schema 中的 relation 数量。</summary>
    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"SELECT count(*) FROM {Table("relations")};";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<IReadOnlyList<ContextRelation>> QueryByMetadataAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<string> metadataKeys,
        string expectedValue,
        CancellationToken cancellationToken)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("relations")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id
  AND (
      data -> 'Metadata' ->> @metadata_key_0 = @expected_value
      OR data -> 'Metadata' ->> @metadata_key_1 = @expected_value
      OR data -> 'metadata' ->> @metadata_key_0 = @expected_value
      OR data -> 'metadata' ->> @metadata_key_1 = @expected_value
  )
ORDER BY weight DESC, confidence DESC, created_at DESC;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("metadata_key_0", metadataKeys[0]);
        command.Parameters.AddWithValue("metadata_key_1", metadataKeys.Count > 1 ? metadataKeys[1] : metadataKeys[0]);
        command.Parameters.AddWithValue("expected_value", expectedValue);

        return await ReadRelationsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ContextRelation>> ReadRelationsAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var results = new List<ContextRelation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Serializer.Deserialize<ContextRelation>(reader.GetString(0)));
        }

        return results;
    }
}
