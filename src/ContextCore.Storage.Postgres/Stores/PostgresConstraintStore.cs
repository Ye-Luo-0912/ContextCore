using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 约束规则存储，实现 <see cref="IConstraintStore"/>。
/// Perf-2：SaveAsync 计算 SHA-256 + token 数并持久化到专用列，Provider 读取时直接复用、跳过在线重算。
/// </summary>
public sealed class PostgresConstraintStore : PostgresStoreBase, IConstraintStore
{
    public PostgresConstraintStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner,
        IContextTokenizerResolver? tokenizerResolver = null,
        string? tokenizerModelName = null)
        : base(connectionFactory, serializer, migrationRunner)
    {
        TokenizerResolver = tokenizerResolver;
        TokenizerModelName = tokenizerModelName;
    }

    public async Task SaveAsync(ContextConstraint constraint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(constraint);
        var now = DateTimeOffset.UtcNow;
        var isNew = string.IsNullOrWhiteSpace(constraint.Id);
        // Perf-2：摄取阶段计算 tokenization metadata，写入专用列与 Metadata（与 ContextItem 摄取逻辑一致）。
        var tokenization = ComputeTokenizationMetadata(constraint.Content);
        var metadataWithTokenization = WithTokenizationMetadata(constraint.Metadata, tokenization);
        var normalized = new ContextConstraint
        {
            Id = isNew ? Guid.NewGuid().ToString("N") : constraint.Id,
            WorkspaceId = constraint.WorkspaceId,
            CollectionId = constraint.CollectionId,
            Scope = constraint.Scope,
            Level = constraint.Level,
            Content = constraint.Content,
            AppliesToRefs = constraint.AppliesToRefs,
            SourceRefs = constraint.SourceRefs,
            Status = constraint.Status,
            Confidence = constraint.Confidence,
            Metadata = metadataWithTokenization,
            CreatedAt = isNew ? now : constraint.CreatedAt,
            UpdatedAt = now,
        };

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("constraints")} (
    workspace_id, collection_id, id, scope, level, status, confidence, created_at, updated_at, data,
    content_hash, content_length, tokenizer_id, tokenizer_version, token_count, counted_at)
VALUES (
    @workspace_id, @collection_id, @id, @scope, @level, @status, @confidence, @created_at, @updated_at, @data,
    @content_hash, @content_length, @tokenizer_id, @tokenizer_version, @token_count, @counted_at)
ON CONFLICT (workspace_id, id) DO UPDATE SET
    collection_id = EXCLUDED.collection_id,
    scope = EXCLUDED.scope,
    level = EXCLUDED.level,
    status = EXCLUDED.status,
    confidence = EXCLUDED.confidence,
    updated_at = EXCLUDED.updated_at,
    data = EXCLUDED.data,
    content_hash = EXCLUDED.content_hash,
    content_length = EXCLUDED.content_length,
    tokenizer_id = EXCLUDED.tokenizer_id,
    tokenizer_version = EXCLUDED.tokenizer_version,
    token_count = EXCLUDED.token_count,
    counted_at = EXCLUDED.counted_at;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", (object?)normalized.CollectionId ?? DBNull.Value);
        command.Parameters.AddWithValue("id", normalized.Id);
        command.Parameters.AddWithValue("scope", normalized.Scope.ToString());
        command.Parameters.AddWithValue("level", normalized.Level.ToString());
        command.Parameters.AddWithValue("status", normalized.Status.ToString());
        command.Parameters.AddWithValue("confidence", normalized.Confidence);
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        command.Parameters.AddWithValue("updated_at", normalized.UpdatedAt);
        AddJson(command, "data", normalized);
        AddTokenizationColumnParameters(command, tokenization);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextConstraint?> GetAsync(
        string constraintId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data, content_hash, content_length, tokenizer_id, tokenizer_version, token_count, counted_at
FROM {Table("constraints")}
WHERE id = @id
ORDER BY updated_at DESC
LIMIT 1;
""";
        command.Parameters.AddWithValue("id", constraintId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return ReadConstraintWithTokenization(reader);
    }

    public async Task<IReadOnlyList<ContextConstraint>> QueryAsync(
        ContextConstraintQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        var conditions = new List<string> { "workspace_id = @workspace_id" };
        command.Parameters.AddWithValue("workspace_id", query.WorkspaceId);

        if (!string.IsNullOrWhiteSpace(query.CollectionId))
        {
            conditions.Add("collection_id = @collection_id");
            command.Parameters.AddWithValue("collection_id", query.CollectionId);
        }

        if (query.Level.HasValue)
        {
            conditions.Add("level = @level");
            command.Parameters.AddWithValue("level", query.Level.Value.ToString());
        }

        if (query.Scope.HasValue)
        {
            conditions.Add("scope = @scope");
            command.Parameters.AddWithValue("scope", query.Scope.Value.ToString());
        }

        if (query.Status.HasValue)
        {
            conditions.Add("status = @status");
            command.Parameters.AddWithValue("status", query.Status.Value.ToString());
        }

        var take = query.Take > 0 ? query.Take : 100;
        var where = string.Join(" AND ", conditions);
        command.CommandText = $"""
SELECT data, content_hash, content_length, tokenizer_id, tokenizer_version, token_count, counted_at
FROM {Table("constraints")}
WHERE {where}
ORDER BY confidence DESC, created_at DESC
LIMIT {take};
""";

        var results = new List<ContextConstraint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadConstraintWithTokenization(reader));
        }

        return results;
    }

    /// <summary>
    /// Perf-2：从 reader 当前行读取 ContextConstraint，并把专用列的 tokenization metadata 合并到 Metadata 字典。
    /// 列顺序：data(0), content_hash(1), content_length(2), tokenizer_id(3), tokenizer_version(4), token_count(5), counted_at(6)。
    /// </summary>
    private ContextConstraint ReadConstraintWithTokenization(System.Data.Common.DbDataReader reader)
    {
        var json = reader.GetString(0);
        var item = Serializer.Deserialize<ContextConstraint>(json);
        var contentHash = reader.IsDBNull(1) ? null : reader.GetString(1);
        var contentLength = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
        var tokenizerId = reader.IsDBNull(3) ? null : reader.GetString(3);
        var tokenizerVersion = reader.IsDBNull(4) ? null : reader.GetString(4);
        var tokenCount = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5);
        var countedAt = reader.IsDBNull(6) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(6);

        var mergedMetadata = MergePersistedTokenizationColumns(
            item.Metadata, contentHash, contentLength, tokenizerId, tokenizerVersion, tokenCount, countedAt);
        return mergedMetadata == item.Metadata
            ? item
            : new ContextConstraint
            {
                Id = item.Id,
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                Scope = item.Scope,
                Level = item.Level,
                Content = item.Content,
                AppliesToRefs = item.AppliesToRefs,
                SourceRefs = item.SourceRefs,
                Status = item.Status,
                Confidence = item.Confidence,
                Metadata = mergedMetadata,
                CreatedAt = item.CreatedAt,
                UpdatedAt = item.UpdatedAt
            };
    }

    /// <summary>Perf-2：把 tokenization metadata 列值绑定到 NpgsqlCommand 参数。</summary>
    private static void AddTokenizationColumnParameters(NpgsqlCommand command, TokenizationMetadata metadata)
    {
        command.Parameters.AddWithValue("content_hash", (object?)metadata.ContentHash ?? DBNull.Value);
        command.Parameters.AddWithValue("content_length", metadata.ContentLength);
        command.Parameters.AddWithValue("tokenizer_id", (object?)metadata.TokenizerId ?? DBNull.Value);
        command.Parameters.AddWithValue("tokenizer_version", (object?)metadata.TokenizerVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("token_count", metadata.TokenCount);
        command.Parameters.AddWithValue("counted_at", (object?)metadata.CountedAt ?? DBNull.Value);
    }
}
