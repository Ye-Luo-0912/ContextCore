using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>PostgreSQL Context Package Policy 存储，实现 <see cref="IContextPackagePolicyStore"/>。</summary>
public sealed class PostgresContextPackagePolicyStore : PostgresStoreBase, IContextPackagePolicyStore
{
    public PostgresContextPackagePolicyStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task SaveAsync(ContextPackagePolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var normalized = string.IsNullOrWhiteSpace(policy.Id)
            ? new ContextPackagePolicy
            {
                Id = Guid.NewGuid().ToString("N"),
                WorkspaceId = policy.WorkspaceId,
                CollectionId = policy.CollectionId,
                Name = policy.Name,
                Description = policy.Description,
                TokenBudget = policy.TokenBudget,
                IncludeGlobalContext = policy.IncludeGlobalContext,
                IncludeHardConstraints = policy.IncludeHardConstraints,
                IncludeSoftConstraints = policy.IncludeSoftConstraints,
                IncludeWorkingMemory = policy.IncludeWorkingMemory,
                IncludeStableMemory = policy.IncludeStableMemory,
                IncludeRecentRawContext = policy.IncludeRecentRawContext,
                MaxRecentItems = policy.MaxRecentItems,
                SectionOrder = policy.SectionOrder,
                SectionPriorities = policy.SectionPriorities,
                SectionTokenBudgets = policy.SectionTokenBudgets,
                Metadata = policy.Metadata,
            }
            : policy;

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("package_policies")} (
    workspace_id, collection_id, id, name, data)
VALUES (
    @workspace_id, @collection_id, @id, @name, @data)
ON CONFLICT (workspace_id, collection_id, id) DO UPDATE SET
    name = EXCLUDED.name,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", (object?)normalized.CollectionId ?? DBNull.Value);
        command.Parameters.AddWithValue("id", normalized.Id);
        command.Parameters.AddWithValue("name", normalized.Name);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextPackagePolicy?> GetAsync(
        string workspaceId,
        string collectionId,
        string policyId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data FROM {Table("package_policies")}
WHERE workspace_id = @workspace_id AND collection_id = @collection_id AND id = @id
LIMIT 1;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("id", policyId);

        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return string.IsNullOrWhiteSpace(json) ? null : Serializer.Deserialize<ContextPackagePolicy>(json);
    }

    public async Task<IReadOnlyList<ContextPackagePolicy>> QueryAsync(
        ContextPackagePolicyQuery query,
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

        if (!string.IsNullOrWhiteSpace(query.QueryText))
        {
            conditions.Add("name ILIKE @query_text");
            command.Parameters.AddWithValue("query_text", $"%{query.QueryText}%");
        }

        var take = query.Take > 0 ? query.Take : 50;
        var where = string.Join(" AND ", conditions);
        command.CommandText = $"""
SELECT data FROM {Table("package_policies")}
WHERE {where}
ORDER BY name
LIMIT {take};
""";

        var results = new List<ContextPackagePolicy>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var json = reader.GetString(0);
            var item = Serializer.Deserialize<ContextPackagePolicy>(json);
            results.Add(item);
        }

        return results;
    }
}
