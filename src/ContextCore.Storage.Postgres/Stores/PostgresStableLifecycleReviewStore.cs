using System.Collections.Generic;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL Stable memory 生命周期人工 review 审核历史存储。
/// R14-PG-4：替代 UnsupportedStableLifecycleReviewStore，让 Postgres provider 在 HA 场景下能持久化生命周期审核记录。
/// </summary>
public sealed class PostgresStableLifecycleReviewStore : PostgresStoreBase, IStableLifecycleReviewStore
{
    public PostgresStableLifecycleReviewStore(PostgresConnectionFactory connectionFactory, PostgresJsonSerializer serializer, PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task AppendReviewAsync(StableLifecycleReviewRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = Normalize(record);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("stable_lifecycle_reviews")} (workspace_id, collection_id, review_id, stable_item_id, reviewed_at, created_at, data)
VALUES (@workspace_id, @collection_id, @review_id, @stable_item_id, @reviewed_at, @created_at, @data)
ON CONFLICT (workspace_id, collection_id, review_id) DO UPDATE SET
    stable_item_id = EXCLUDED.stable_item_id,
    reviewed_at = EXCLUDED.reviewed_at,
    created_at = EXCLUDED.created_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", CollectionKey(normalized.CollectionId));
        command.Parameters.AddWithValue("review_id", normalized.ReviewId);
        command.Parameters.AddWithValue("stable_item_id", normalized.StableItemId);
        command.Parameters.AddWithValue("reviewed_at", normalized.ReviewedAt);
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StableLifecycleReviewRecord>> QueryReviewsAsync(string stableItemId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableItemId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("stable_lifecycle_reviews")}
WHERE stable_item_id = @stable_item_id
ORDER BY reviewed_at DESC;
""";
        command.Parameters.AddWithValue("stable_item_id", stableItemId);

        return await ExecuteReaderJsonAsync<StableLifecycleReviewRecord>(command, cancellationToken).ConfigureAwait(false);
    }

    private static StableLifecycleReviewRecord Normalize(StableLifecycleReviewRecord item)
    {
        return new StableLifecycleReviewRecord
        {
            ReviewId = string.IsNullOrWhiteSpace(item.ReviewId) ? Guid.NewGuid().ToString("N") : item.ReviewId,
            StableItemId = item.StableItemId,
            StableKind = item.StableKind,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Action = item.Action,
            FromStatus = item.FromStatus,
            ToStatus = item.ToStatus,
            FromLifecycle = item.FromLifecycle,
            ToLifecycle = item.ToLifecycle,
            Reviewer = item.Reviewer,
            Reason = item.Reason,
            ReplacementItemId = item.ReplacementItemId,
            EvidenceRefs = [.. item.EvidenceRefs],
            SourceRefs = [.. item.SourceRefs],
            CreatedAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt,
            ReviewedAt = item.ReviewedAt == default ? DateTimeOffset.UtcNow : item.ReviewedAt,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase),
            Warnings = [.. item.Warnings],
            Errors = [.. item.Errors]
        };
    }
}
