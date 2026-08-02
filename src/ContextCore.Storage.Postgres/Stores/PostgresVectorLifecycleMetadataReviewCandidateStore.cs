using System.Collections.Generic;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL vector lifecycle metadata review candidate 存储。
/// 替代 UnsupportedVectorLifecycleMetadataReviewCandidateStore，让 Postgres provider 在 HA 场景下能持久化人工 review 队列。
/// </summary>
public sealed class PostgresVectorLifecycleMetadataReviewCandidateStore : PostgresStoreBase, IVectorLifecycleMetadataReviewCandidateStore
{
    public PostgresVectorLifecycleMetadataReviewCandidateStore(PostgresConnectionFactory connectionFactory, PostgresJsonSerializer serializer, PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task SaveAsync(VectorLifecycleMetadataReviewCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var normalized = Normalize(candidate);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("vector_lifecycle_metadata_review_candidates")} (workspace_id, collection_id, candidate_id, status, layer, item_kind, must_hit_item_id, source_eval_set, created_at, data)
VALUES (@workspace_id, @collection_id, @candidate_id, @status, @layer, @item_kind, @must_hit_item_id, @source_eval_set, @created_at, @data)
ON CONFLICT (workspace_id, collection_id, candidate_id) DO UPDATE SET
    status = EXCLUDED.status,
    layer = EXCLUDED.layer,
    item_kind = EXCLUDED.item_kind,
    must_hit_item_id = EXCLUDED.must_hit_item_id,
    source_eval_set = EXCLUDED.source_eval_set,
    created_at = EXCLUDED.created_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", normalized.CollectionId);
        command.Parameters.AddWithValue("candidate_id", normalized.CandidateId);
        command.Parameters.AddWithValue("status", normalized.Status ?? string.Empty);
        command.Parameters.AddWithValue("layer", normalized.Layer ?? string.Empty);
        command.Parameters.AddWithValue("item_kind", normalized.ItemKind ?? string.Empty);
        command.Parameters.AddWithValue("must_hit_item_id", normalized.MustHitItemId ?? string.Empty);
        command.Parameters.AddWithValue("source_eval_set", normalized.SourceEvalSet ?? string.Empty);
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<VectorLifecycleMetadataReviewCandidate?> GetAsync(string candidateId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("vector_lifecycle_metadata_review_candidates")}
WHERE candidate_id = @candidate_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("candidate_id", candidateId);

        return await ExecuteScalarJsonAsync<VectorLifecycleMetadataReviewCandidate>(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VectorLifecycleMetadataReviewCandidate>> QueryAsync(
        VectorLifecycleMetadataReviewCandidateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        var where = new StringBuilder("WHERE workspace_id = @workspace_id");
        command.Parameters.AddWithValue("workspace_id", query.WorkspaceId);

        if (!string.IsNullOrWhiteSpace(query.CollectionId))
        {
            where.Append(" AND collection_id = @collection_id");
            command.Parameters.AddWithValue("collection_id", query.CollectionId);
        }
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            where.Append(" AND status = @status");
            command.Parameters.AddWithValue("status", query.Status);
        }
        if (!string.IsNullOrWhiteSpace(query.Layer))
        {
            where.Append(" AND layer = @layer");
            command.Parameters.AddWithValue("layer", query.Layer);
        }
        if (!string.IsNullOrWhiteSpace(query.ItemKind))
        {
            where.Append(" AND item_kind = @item_kind");
            command.Parameters.AddWithValue("item_kind", query.ItemKind);
        }
        if (!string.IsNullOrWhiteSpace(query.MustHitItemId))
        {
            where.Append(" AND must_hit_item_id = @must_hit_item_id");
            command.Parameters.AddWithValue("must_hit_item_id", query.MustHitItemId);
        }
        if (!string.IsNullOrWhiteSpace(query.SourceEvalSet))
        {
            where.Append(" AND source_eval_set = @source_eval_set");
            command.Parameters.AddWithValue("source_eval_set", query.SourceEvalSet);
        }

        var limit = query.Limit > 0 ? query.Limit : 50;
        var offset = Math.Max(0, query.Offset);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("offset", offset);
        command.CommandText = $"""
SELECT data
FROM {Table("vector_lifecycle_metadata_review_candidates")}
{where}
ORDER BY created_at DESC
OFFSET @offset LIMIT @limit;
""";

        return await ExecuteReaderJsonAsync<VectorLifecycleMetadataReviewCandidate>(command, cancellationToken).ConfigureAwait(false);
    }

    private static VectorLifecycleMetadataReviewCandidate Normalize(VectorLifecycleMetadataReviewCandidate item)
    {
        return new VectorLifecycleMetadataReviewCandidate
        {
            CandidateId = string.IsNullOrWhiteSpace(item.CandidateId) ? Guid.NewGuid().ToString("N") : item.CandidateId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SourceSampleId = item.SourceSampleId,
            SourceEvalSet = item.SourceEvalSet,
            MustHitItemId = item.MustHitItemId,
            ItemKind = item.ItemKind,
            Layer = item.Layer,
            CurrentLifecycle = item.CurrentLifecycle,
            CurrentReviewStatus = item.CurrentReviewStatus,
            CurrentTargetSection = item.CurrentTargetSection,
            ProposedLifecycle = item.ProposedLifecycle,
            ProposedReviewStatus = item.ProposedReviewStatus,
            ProposedTargetSection = item.ProposedTargetSection,
            RepairReason = item.RepairReason,
            EvidenceRefs = [.. item.EvidenceRefs],
            SourceRefs = [.. item.SourceRefs],
            ProvenanceAvailable = item.ProvenanceAvailable,
            RelationEvidenceAvailable = item.RelationEvidenceAvailable,
            ReviewEvidenceAvailable = item.ReviewEvidenceAvailable,
            RiskIfApproved = [.. item.RiskIfApproved],
            RiskIfRejected = [.. item.RiskIfRejected],
            RequiresHumanReview = item.RequiresHumanReview,
            Status = item.Status,
            CreatedAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }
}
