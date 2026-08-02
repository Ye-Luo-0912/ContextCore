using System.Collections.Generic;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL vector lifecycle sidecar metadata 存储。
/// 替代 UnsupportedVectorLifecycleSidecarMetadataStore，让 Postgres provider 在 HA 场景下能持久化旁路 override。
/// </summary>
public sealed class PostgresVectorLifecycleSidecarMetadataStore : PostgresStoreBase, IVectorLifecycleSidecarMetadataStore
{
    public PostgresVectorLifecycleSidecarMetadataStore(PostgresConnectionFactory connectionFactory, PostgresJsonSerializer serializer, PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task SaveAsync(VectorLifecycleSidecarMetadataEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var normalized = Normalize(entry);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("vector_lifecycle_sidecar_metadata")} (workspace_id, collection_id, item_id, source_review_id, source_candidate_id, created_at, data)
VALUES (@workspace_id, @collection_id, @item_id, @source_review_id, @source_candidate_id, @created_at, @data)
ON CONFLICT (workspace_id, collection_id, item_id) DO UPDATE SET
    source_review_id = EXCLUDED.source_review_id,
    source_candidate_id = EXCLUDED.source_candidate_id,
    created_at = EXCLUDED.created_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", normalized.CollectionId);
        command.Parameters.AddWithValue("item_id", normalized.ItemId);
        command.Parameters.AddWithValue("source_review_id", normalized.SourceReviewId ?? string.Empty);
        command.Parameters.AddWithValue("source_candidate_id", normalized.SourceCandidateId ?? string.Empty);
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VectorLifecycleSidecarMetadataEntry>> QueryAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        var where = new StringBuilder("WHERE workspace_id = @workspace_id");
        command.Parameters.AddWithValue("workspace_id", workspaceId);

        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            where.Append(" AND collection_id = @collection_id");
            command.Parameters.AddWithValue("collection_id", collectionId);
        }

        command.CommandText = $"""
SELECT data
FROM {Table("vector_lifecycle_sidecar_metadata")}
{where}
ORDER BY created_at DESC;
""";

        return await ExecuteReaderJsonAsync<VectorLifecycleSidecarMetadataEntry>(command, cancellationToken).ConfigureAwait(false);
    }

    private static VectorLifecycleSidecarMetadataEntry Normalize(VectorLifecycleSidecarMetadataEntry item)
    {
        return new VectorLifecycleSidecarMetadataEntry
        {
            ItemId = item.ItemId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            LifecycleOverride = item.LifecycleOverride,
            ReviewStatusOverride = item.ReviewStatusOverride,
            TargetSectionOverride = item.TargetSectionOverride,
            SourceReviewId = item.SourceReviewId,
            SourceCandidateId = item.SourceCandidateId,
            Reviewer = item.Reviewer,
            Reason = item.Reason,
            EvidenceRefs = [.. item.EvidenceRefs],
            SourceRefs = [.. item.SourceRefs],
            CreatedAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt,
            PolicyVersion = item.PolicyVersion,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }
}
