using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL vector reindex 报告存储。
/// R14-PG-5：替代 UnsupportedVectorReindexReportStore，让 Postgres provider 在 HA 场景下能持久化 reindex 执行报告。
/// </summary>
public sealed class PostgresVectorReindexReportStore : PostgresStoreBase, IVectorReindexReportStore
{
    public PostgresVectorReindexReportStore(PostgresConnectionFactory connectionFactory, PostgresJsonSerializer serializer, PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task SaveAsync(VectorReindexResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var normalized = EnsureReportId(result);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("vector_reindex_reports")} (workspace_id, collection_id, report_id, started_at, completed_at, data)
VALUES (@workspace_id, @collection_id, @report_id, @started_at, @completed_at, @data)
ON CONFLICT (workspace_id, collection_id, report_id) DO UPDATE SET
    started_at = EXCLUDED.started_at,
    completed_at = EXCLUDED.completed_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", normalized.CollectionId);
        command.Parameters.AddWithValue("report_id", normalized.ReportId);
        command.Parameters.AddWithValue("started_at", normalized.StartedAt);
        command.Parameters.AddWithValue("completed_at",
            normalized.CompletedAt == DateTimeOffset.MinValue ? (object)DBNull.Value : normalized.CompletedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VectorReindexResult>> QueryAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("vector_reindex_reports")}
WHERE workspace_id = @workspace_id AND collection_id = @collection_id
ORDER BY completed_at DESC
LIMIT @take;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("take", TakeOrDefault(take));

        return await ExecuteReaderJsonAsync<VectorReindexResult>(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VectorReindexResult?> GetAsync(string reportId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("vector_reindex_reports")}
WHERE report_id = @report_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("report_id", reportId);

        return await ExecuteScalarJsonAsync<VectorReindexResult>(command, cancellationToken).ConfigureAwait(false);
    }

    private static VectorReindexResult EnsureReportId(VectorReindexResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.ReportId))
        {
            return result;
        }

        return new VectorReindexResult
        {
            ReportId = Guid.NewGuid().ToString("N"),
            OperationId = result.OperationId,
            JobId = result.JobId,
            WorkspaceId = result.WorkspaceId,
            CollectionId = result.CollectionId,
            Plan = result.Plan,
            Summary = result.Summary,
            ProcessedItems = result.ProcessedItems,
            Warnings = result.Warnings,
            Errors = result.Errors,
            StartedAt = result.StartedAt,
            CompletedAt = result.CompletedAt
        };
    }
}
