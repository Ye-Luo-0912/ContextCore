using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;
using NpgsqlTypes;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>PostgreSQL 检索 Trace 存储，用于 ControlRoom 和后续审计查看检索过程。</summary>
public sealed class PostgresRetrievalTraceStore : PostgresStoreBase, IRetrievalTraceStore
{
    public PostgresRetrievalTraceStore(PostgresConnectionFactory connectionFactory, PostgresJsonSerializer serializer, PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task SaveAsync(ContextRetrievalTrace trace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var normalized = Normalize(trace);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("retrieval_traces")} (workspace_id, collection_id, retrieval_id, query_text, created_at, data)
VALUES (@workspace_id, @collection_id, @retrieval_id, @query_text, @created_at, @data)
ON CONFLICT (workspace_id, collection_id, retrieval_id) DO UPDATE SET
    query_text = EXCLUDED.query_text,
    created_at = EXCLUDED.created_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", normalized.CollectionId);
        command.Parameters.AddWithValue("retrieval_id", normalized.RetrievalId);
        command.Parameters.AddWithValue("query_text", (object?)normalized.QueryText ?? DBNull.Value);
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveBatchAsync(
        IReadOnlyList<ContextRetrievalTrace> traces,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(traces);
        if (traces.Count == 0)
        {
            return;
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // 批量 unnest：单次 Postgres roundtrip 写入整批 trace（Diagnostic Plane 性能路径）。
        var count = traces.Count;
        var workspaceIds = new string[count];
        var collectionIds = new string[count];
        var retrievalIds = new string[count];
        var queryTexts = new string?[count];
        var createdAt = new DateTimeOffset[count];
        var datas = new string[count];
        for (var i = 0; i < count; i++)
        {
            var normalized = Normalize(traces[i]);
            workspaceIds[i] = normalized.WorkspaceId;
            collectionIds[i] = normalized.CollectionId;
            retrievalIds[i] = normalized.RetrievalId;
            queryTexts[i] = normalized.QueryText;
            createdAt[i] = normalized.CreatedAt;
            datas[i] = Serializer.Serialize(normalized);
        }

        command.CommandText = $"""
INSERT INTO {Table("retrieval_traces")} (workspace_id, collection_id, retrieval_id, query_text, created_at, data)
SELECT ws, col, rid, qtext, cat, d::jsonb
FROM unnest(
    @workspace_ids::text[], @collection_ids::text[], @retrieval_ids::text[],
    @query_texts::text[], @created_ats::timestamptz[], @datas::jsonb[])
AS t(ws, col, rid, qtext, cat, d)
ON CONFLICT (workspace_id, collection_id, retrieval_id) DO UPDATE SET
    query_text = EXCLUDED.query_text,
    created_at = EXCLUDED.created_at,
    data = EXCLUDED.data;
""";
        AddTextArray(command, "workspace_ids", workspaceIds);
        AddTextArray(command, "collection_ids", collectionIds);
        AddTextArray(command, "retrieval_ids", retrievalIds);
        AddNullableTextArray(command, "query_texts", queryTexts);
        AddArrayParameter(command, "created_ats", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz, createdAt);
        AddArrayParameter(command, "datas", NpgsqlDbType.Array | NpgsqlDbType.Jsonb, datas);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextRetrievalTrace>> QueryRecentAsync(string workspaceId, string collectionId, int take, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("retrieval_traces")}
WHERE workspace_id = @workspace_id AND collection_id = @collection_id
ORDER BY created_at DESC
LIMIT @take;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("take", TakeOrDefault(take));

        var results = new List<ContextRetrievalTrace>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Serializer.Deserialize<ContextRetrievalTrace>(reader.GetString(0)));
        }

        return results;
    }

    public async Task<ContextRetrievalTrace?> GetAsync(
        string workspaceId,
        string collectionId,
        string retrievalId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // 稳定主键 (workspace_id, collection_id, retrieval_id) 点查：
        // Decision Evidence 审计不依赖"最近 N 条"窗口（窗口外的决策仍可查证）。
        command.CommandText = $"""
SELECT data
FROM {Table("retrieval_traces")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id
  AND retrieval_id = @retrieval_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("retrieval_id", retrievalId);

        var data = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return data is null ? null : Serializer.Deserialize<ContextRetrievalTrace>(data);
    }

    private static ContextRetrievalTrace Normalize(ContextRetrievalTrace trace)
    {
        return new ContextRetrievalTrace
        {
            RetrievalId = string.IsNullOrWhiteSpace(trace.RetrievalId) ? Guid.NewGuid().ToString("N") : trace.RetrievalId,
            WorkspaceId = trace.WorkspaceId,
            CollectionId = trace.CollectionId,
            QueryText = trace.QueryText,
            RewrittenQueryText = trace.RewrittenQueryText,
            Stages = [.. trace.Stages],
            Candidates = [.. trace.Candidates],
            SelectedItems = [.. trace.SelectedItems],
            DroppedItems = [.. trace.DroppedItems],
            Metadata = new Dictionary<string, string>(trace.Metadata),
            CreatedAt = trace.CreatedAt == default ? DateTimeOffset.UtcNow : trace.CreatedAt
        };
    }
}
