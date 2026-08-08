using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 决策 Trace 存储，用于持久化 V17.0 统一上下文决策记录。
/// 该 store 只写只读 trace artifact，不参与 retrieval/package/planning 运行时决策。
/// 替代 UnsupportedDecisionTraceStore，让 Postgres provider 在 HA 场景下能持久化决策审计。
/// </summary>
public sealed class PostgresDecisionTraceStore : PostgresStoreBase, IDecisionTraceStore
{
    public PostgresDecisionTraceStore(PostgresConnectionFactory connectionFactory, PostgresJsonSerializer serializer, PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task SaveAsync(ContextDecisionRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = Normalize(record);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("decision_traces")} (workspace_id, collection_id, decision_id, source, query_text, created_at, data)
VALUES (@workspace_id, @collection_id, @decision_id, @source, @query_text, @created_at, @data)
ON CONFLICT (workspace_id, collection_id, decision_id) DO UPDATE SET
    source = EXCLUDED.source,
    query_text = EXCLUDED.query_text,
    created_at = EXCLUDED.created_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", normalized.CollectionId);
        command.Parameters.AddWithValue("decision_id", normalized.DecisionId);
        command.Parameters.AddWithValue("source", normalized.Source.ToString());
        command.Parameters.AddWithValue("query_text", (object?)normalized.QueryText ?? DBNull.Value);
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextDecisionRecord>> QueryRecentAsync(string workspaceId, string collectionId, int take, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("decision_traces")}
WHERE workspace_id = @workspace_id AND collection_id = @collection_id
ORDER BY created_at DESC
LIMIT @take;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("take", TakeOrDefault(take));

        return await ExecuteReaderJsonAsync<ContextDecisionRecord>(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextDecisionRecord?> GetAsync(
        string workspaceId,
        string collectionId,
        string decisionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // 稳定主键 (workspace_id, collection_id, decision_id) 点查
        // （Decision Evidence Plane：Durable / Point Lookup，不依赖"最近 N 条"窗口）。
        command.CommandText = $"""
SELECT data
FROM {Table("decision_traces")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id
  AND decision_id = @decision_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("decision_id", decisionId);

        var data = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return data is null ? null : Serializer.Deserialize<ContextDecisionRecord>(data);
    }

    private static ContextDecisionRecord Normalize(ContextDecisionRecord record)
    {
        return new ContextDecisionRecord
        {
            DecisionId = string.IsNullOrWhiteSpace(record.DecisionId) ? Guid.NewGuid().ToString("N") : record.DecisionId,
            Source = record.Source,
            WorkspaceId = record.WorkspaceId,
            CollectionId = record.CollectionId,
            QueryText = record.QueryText,
            Candidates = [.. record.Candidates],
            Outcome = record.Outcome,
            Risk = record.Risk,
            Quality = record.Quality,
            PolicyVersion = record.PolicyVersion,
            Metadata = new Dictionary<string, string>(record.Metadata),
            CreatedAt = record.CreatedAt == default ? DateTimeOffset.UtcNow : record.CreatedAt
        };
    }
}
