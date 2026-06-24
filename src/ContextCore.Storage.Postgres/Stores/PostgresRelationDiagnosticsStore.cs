using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>PostgreSQL relation diagnostics 投影存储；用于显式报告和 parity，不参与 runtime 决策。</summary>
public sealed class PostgresRelationDiagnosticsStore : PostgresStoreBase
{
    public PostgresRelationDiagnosticsStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task WriteAsync(
        RelationDiagnosticsSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var normalized = Normalize(snapshot);
        if (string.IsNullOrWhiteSpace(normalized.CollectionId))
        {
            throw new ArgumentException("Relation diagnostics 必须包含 collectionId。", nameof(snapshot));
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("relation_diagnostics")} (
    workspace_id, collection_id, diagnostic_id, relation_id, item_id,
    diagnostic_kind, severity, message, created_at, data)
VALUES (
    @workspace_id, @collection_id, @diagnostic_id, @relation_id, @item_id,
    @diagnostic_kind, @severity, @message, @created_at, @data)
ON CONFLICT (workspace_id, collection_id, diagnostic_id) DO UPDATE SET
    relation_id = EXCLUDED.relation_id,
    item_id = EXCLUDED.item_id,
    diagnostic_kind = EXCLUDED.diagnostic_kind,
    severity = EXCLUDED.severity,
    message = EXCLUDED.message,
    created_at = EXCLUDED.created_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", normalized.CollectionId!);
        command.Parameters.AddWithValue("diagnostic_id", normalized.DiagnosticId);
        command.Parameters.AddWithValue("relation_id", string.IsNullOrWhiteSpace(normalized.RelationId) ? DBNull.Value : normalized.RelationId);
        command.Parameters.AddWithValue("item_id", string.IsNullOrWhiteSpace(normalized.ItemId) ? DBNull.Value : normalized.ItemId);
        command.Parameters.AddWithValue("diagnostic_kind", normalized.DiagnosticKind);
        command.Parameters.AddWithValue("severity", normalized.Severity);
        command.Parameters.AddWithValue("message", normalized.Message);
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<RelationDiagnosticsSnapshot>> QueryByScopeAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        return QueryAsync(
            ["workspace_id = @workspace_id", "collection_id = @collection_id"],
            command =>
            {
                command.Parameters.AddWithValue("workspace_id", workspaceId);
                command.Parameters.AddWithValue("collection_id", collectionId);
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<RelationDiagnosticsSnapshot>> QueryByRelationAsync(
        string workspaceId,
        string collectionId,
        string relationId,
        CancellationToken cancellationToken = default)
    {
        return QueryAsync(
            ["workspace_id = @workspace_id", "collection_id = @collection_id", "relation_id = @relation_id"],
            command =>
            {
                command.Parameters.AddWithValue("workspace_id", workspaceId);
                command.Parameters.AddWithValue("collection_id", collectionId);
                command.Parameters.AddWithValue("relation_id", relationId);
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<RelationDiagnosticsSnapshot>> QueryByItemAsync(
        string workspaceId,
        string collectionId,
        string itemId,
        CancellationToken cancellationToken = default)
    {
        return QueryAsync(
            ["workspace_id = @workspace_id", "collection_id = @collection_id", "item_id = @item_id"],
            command =>
            {
                command.Parameters.AddWithValue("workspace_id", workspaceId);
                command.Parameters.AddWithValue("collection_id", collectionId);
                command.Parameters.AddWithValue("item_id", itemId);
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<RelationDiagnosticsSnapshot>> QueryByKindAsync(
        string workspaceId,
        string collectionId,
        string diagnosticKind,
        CancellationToken cancellationToken = default)
    {
        return QueryAsync(
            ["workspace_id = @workspace_id", "collection_id = @collection_id", "diagnostic_kind = @diagnostic_kind"],
            command =>
            {
                command.Parameters.AddWithValue("workspace_id", workspaceId);
                command.Parameters.AddWithValue("collection_id", collectionId);
                command.Parameters.AddWithValue("diagnostic_kind", diagnosticKind);
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<RelationDiagnosticsSnapshot>> QueryBySeverityAsync(
        string workspaceId,
        string collectionId,
        string severity,
        CancellationToken cancellationToken = default)
    {
        return QueryAsync(
            ["workspace_id = @workspace_id", "collection_id = @collection_id", "severity = @severity"],
            command =>
            {
                command.Parameters.AddWithValue("workspace_id", workspaceId);
                command.Parameters.AddWithValue("collection_id", collectionId);
                command.Parameters.AddWithValue("severity", severity);
            },
            cancellationToken);
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"SELECT count(*) FROM {Table("relation_diagnostics")};";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

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
DELETE FROM {Table("relation_diagnostics")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<RelationDiagnosticsSnapshot>> QueryAsync(
        IReadOnlyList<string> filters,
        Action<NpgsqlCommand> bind,
        CancellationToken cancellationToken)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("relation_diagnostics")}
WHERE {string.Join(" AND ", filters)}
ORDER BY created_at DESC, diagnostic_id ASC;
""";
        bind(command);

        var results = new List<RelationDiagnosticsSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Serializer.Deserialize<RelationDiagnosticsSnapshot>(reader.GetString(0)));
        }

        return results;
    }

    private static RelationDiagnosticsSnapshot Normalize(RelationDiagnosticsSnapshot snapshot)
    {
        return new RelationDiagnosticsSnapshot
        {
            DiagnosticId = string.IsNullOrWhiteSpace(snapshot.DiagnosticId) ? Guid.NewGuid().ToString("N") : snapshot.DiagnosticId,
            WorkspaceId = snapshot.WorkspaceId,
            CollectionId = snapshot.CollectionId,
            RelationId = snapshot.RelationId,
            ItemId = snapshot.ItemId,
            DiagnosticKind = snapshot.DiagnosticKind,
            Severity = snapshot.Severity,
            Message = snapshot.Message,
            CreatedAt = snapshot.CreatedAt == default ? DateTimeOffset.UtcNow : snapshot.CreatedAt,
            Metadata = new Dictionary<string, string>(snapshot.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }
}
