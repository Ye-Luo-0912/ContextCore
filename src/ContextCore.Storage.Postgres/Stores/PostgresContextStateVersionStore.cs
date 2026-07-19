using System.Text;
using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 分布式 <see cref="IContextStateVersionStore"/> 实现。
/// R14-PG-6：替代 InMemoryContextStateVersionStore，让多实例 Worker 通过 Postgres 行级锁共享同一份单调递增的版本号，
/// 支持跨实例 cache invalidation。版本号持久化在 context_state_versions 表中，重启不丢失。
/// </summary>
/// <remarks>
/// <para>
/// <b>BumpVersionAsync 原子性</b>：使用 <c>INSERT ... ON CONFLICT DO UPDATE SET version = table.version + 1 RETURNING version</c>
/// 在 Postgres 中是原子的（行级锁），多实例并发调用不会丢失更新。
/// </para>
/// <para>
/// <b>GetVersionsAsync 批量查询</b>：使用 <c>IN (VALUES ...)</c> 子句一次查询多个 scope，
/// 避免分布式实现下每次命中 N 次网络调用。
/// </para>
/// </remarks>
public sealed class PostgresContextStateVersionStore : PostgresStoreBase, IContextStateVersionStore
{
    public PostgresContextStateVersionStore(PostgresConnectionFactory connectionFactory, PostgresJsonSerializer serializer, PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task<long> GetVersionAsync(string workspaceId, string collectionId, string storeKind, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeKind);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT version FROM {Table("context_state_versions")}
WHERE workspace_id = @workspace_id AND collection_id = @collection_id AND store_kind = @store_kind;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("store_kind", storeKind);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long v ? v : 0L;
    }

    public async Task<IReadOnlyDictionary<VersionScope, long>> GetVersionsAsync(
        IReadOnlyCollection<VersionScope> scopes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        // 去重：VersionScope 是 readonly record struct，自带值相等语义
        var uniqueScopes = new HashSet<VersionScope>(scopes);
        if (uniqueScopes.Count == 0)
        {
            return new Dictionary<VersionScope, long>();
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        var tableName = Table("context_state_versions");
        var valuesBuilder = new StringBuilder();
        var index = 0;
        foreach (var scope in uniqueScopes)
        {
            if (index > 0)
            {
                valuesBuilder.Append(", ");
            }

            valuesBuilder.Append($"(@ws{index}, @col{index}, @kind{index})");

            command.Parameters.AddWithValue($"ws{index}", scope.WorkspaceId);
            command.Parameters.AddWithValue($"col{index}", scope.CollectionId);
            command.Parameters.AddWithValue($"kind{index}", scope.StoreKind);
            index++;
        }

        command.CommandText = $"""
SELECT workspace_id, collection_id, store_kind, version
FROM {tableName}
WHERE (workspace_id, collection_id, store_kind) IN (VALUES {valuesBuilder});
""";

        var result = new Dictionary<VersionScope, long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var workspaceId = reader.GetString(0);
            var collectionId = reader.GetString(1);
            var storeKind = reader.GetString(2);
            var version = reader.GetInt64(3);
            result[new VersionScope(workspaceId, collectionId, storeKind)] = version;
        }

        // 不在数据库的 scope 不出现在字典中（与 InMemory 版行为一致：未包含的范围不在返回字典中）
        return result;
    }

    public async Task<long> BumpVersionAsync(string workspaceId, string collectionId, string storeKind, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeKind);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        var tableName = Table("context_state_versions");
        // INSERT ... ON CONFLICT DO UPDATE 在 Postgres 中是原子的（行级锁），
        // RETURNING version 返回更新后的版本号。多实例并发调用不会丢失更新。
        command.CommandText = $"""
INSERT INTO {tableName} (workspace_id, collection_id, store_kind, version, updated_at)
VALUES (@workspace_id, @collection_id, @store_kind, 1, now())
ON CONFLICT (workspace_id, collection_id, store_kind) DO UPDATE SET
    version = {tableName}.version + 1,
    updated_at = now()
RETURNING version;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("store_kind", storeKind);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long v ? v : throw new InvalidOperationException("BumpVersionAsync RETURNING 未返回版本号");
    }
}
