using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;

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
