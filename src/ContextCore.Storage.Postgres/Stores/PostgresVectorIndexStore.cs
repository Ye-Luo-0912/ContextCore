using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL pgvector-backed VectorIndexStore。
/// 仅用于显式 diagnostics/parity/smoke，不作为正式 retrieval provider。
/// </summary>
public sealed class PostgresVectorIndexStore : PostgresStoreBase, IVectorIndexStore
{
    public PostgresVectorIndexStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task UpsertAsync(VectorIndexEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var normalized = Normalize(entry);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("vector_index_entries")} (
    workspace_id, collection_id, entry_id, item_id, source_id, source_kind, item_kind, layer,
    embedding_provider, provider_id, embedding_model, model_id, dimension, normalized,
    content_hash, vector, metadata_json, created_at, updated_at, data)
VALUES (
    @workspace_id, @collection_id, @entry_id, @item_id, @source_id, @source_kind, @item_kind, @layer,
    @embedding_provider, @provider_id, @embedding_model, @model_id, @dimension, @normalized,
    @content_hash, @vector::vector, @metadata_json, @created_at, @updated_at, @data)
ON CONFLICT (workspace_id, collection_id, entry_id) DO UPDATE SET
    item_id = EXCLUDED.item_id,
    source_id = EXCLUDED.source_id,
    source_kind = EXCLUDED.source_kind,
    item_kind = EXCLUDED.item_kind,
    layer = EXCLUDED.layer,
    embedding_provider = EXCLUDED.embedding_provider,
    provider_id = EXCLUDED.provider_id,
    embedding_model = EXCLUDED.embedding_model,
    model_id = EXCLUDED.model_id,
    dimension = EXCLUDED.dimension,
    normalized = EXCLUDED.normalized,
    content_hash = EXCLUDED.content_hash,
    vector = EXCLUDED.vector,
    metadata_json = EXCLUDED.metadata_json,
    updated_at = EXCLUDED.updated_at,
    data = EXCLUDED.data;
""";
        AddEntryParameters(command, normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<VectorIndexEntry?> GetByEntryIdAsync(
        string workspaceId,
        string collectionId,
        string entryId,
        bool includeVector = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("vector_index_entries")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id
  AND entry_id = @entry_id;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", CollectionKey(collectionId));
        command.Parameters.AddWithValue("entry_id", entryId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is string json ? Clone(Serializer.Deserialize<VectorIndexEntry>(json), includeVector) : null;
    }

    public async Task DeleteAsync(
        string workspaceId,
        string collectionId,
        string entryId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
DELETE FROM {Table("vector_index_entries")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id
  AND entry_id = @entry_id;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", CollectionKey(collectionId));
        command.Parameters.AddWithValue("entry_id", entryId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VectorIndexEntry>> GetByItemIdAsync(
        string workspaceId,
        string collectionId,
        string itemId,
        CancellationToken cancellationToken = default)
    {
        return await ListAsync(new VectorIndexQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Take = int.MaxValue,
            IncludeVector = true
        }, cancellationToken).ConfigureAwait(false) is { } entries
            ? entries
                .Where(entry => string.Equals(entry.ItemId, itemId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.UpdatedAt)
                .ToArray()
            : Array.Empty<VectorIndexEntry>();
    }

    public async Task<IReadOnlyList<VectorIndexEntry>> ListAsync(
        VectorIndexQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        var filters = BuildListFilters(command, query);
        command.Parameters.AddWithValue("skip", Math.Max(0, query.Skip));
        command.Parameters.AddWithValue("take", TakeOrDefault(query.Take));
        command.CommandText = $"""
SELECT data
FROM {Table("vector_index_entries")}
WHERE {string.Join(" AND ", filters)}
ORDER BY item_id, entry_id
OFFSET @skip
LIMIT @take;
""";
        var results = new List<VectorIndexEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Clone(Serializer.Deserialize<VectorIndexEntry>(reader.GetString(0)), query.IncludeVector));
        }

        return results;
    }

    public async Task<IReadOnlyList<VectorIndexSearchResult>> SearchAsync(
        VectorIndexSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateSearchCompatibility(query);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        var filters = new List<string>
        {
            "workspace_id = @workspace_id",
            "provider_id = @provider_id",
            "model_id = @model_id",
            "dimension = @dimension"
        };
        command.Parameters.AddWithValue("workspace_id", query.WorkspaceId);
        command.Parameters.AddWithValue("provider_id", query.EmbeddingProvider!);
        command.Parameters.AddWithValue("model_id", query.EmbeddingModel!);
        command.Parameters.AddWithValue("dimension", query.Dimension!.Value);
        if (!string.IsNullOrWhiteSpace(query.CollectionId))
        {
            filters.Add("collection_id = @collection_id");
            command.Parameters.AddWithValue("collection_id", CollectionKey(query.CollectionId));
        }

        var scoreExpression = "(1 - (vector <=> @query_vector::vector))";
        if (query.MinScore is not null)
        {
            filters.Add($"{scoreExpression} >= @min_score");
            command.Parameters.AddWithValue("min_score", query.MinScore.Value);
        }

        command.Parameters.AddWithValue("query_vector", PostgresVectorFormat.ToVectorLiteral(query.Vector));
        command.Parameters.AddWithValue("take", query.TopK > 0 ? query.TopK : 10);
        command.CommandText = $"""
SELECT data, {scoreExpression} AS score
FROM {Table("vector_index_entries")}
WHERE {string.Join(" AND ", filters)}
ORDER BY vector <=> @query_vector::vector, updated_at DESC
LIMIT @take;
""";
        var results = new List<VectorIndexSearchResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rank = 1;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new VectorIndexSearchResult
            {
                Entry = Clone(Serializer.Deserialize<VectorIndexEntry>(reader.GetString(0)), query.IncludeVector),
                Score = reader.GetDouble(1),
                Rank = rank++
            });
        }

        return results;
    }

    public async Task<IReadOnlyList<VectorIndexDiagnostic>> GetDiagnosticsAsync(
        VectorIndexQuery query,
        CancellationToken cancellationToken = default)
    {
        var entries = await ListAsync(WithTakeAll(), cancellationToken).ConfigureAwait(false);
        var diagnostics = entries
            .Where(entry => entry.Dimension != entry.Vector.Count)
            .Select(entry => new VectorIndexDiagnostic
            {
                DiagnosticId = $"{VectorIndexDiagnosticTypes.DimensionMismatch}:{entry.WorkspaceId}:{entry.CollectionId}:{entry.EntryId}",
                Type = VectorIndexDiagnosticTypes.DimensionMismatch,
                Severity = "Error",
                WorkspaceId = entry.WorkspaceId,
                CollectionId = entry.CollectionId,
                ItemId = entry.ItemId,
                EntryId = entry.EntryId,
                Message = "Postgres vector index entry dimension does not match stored vector length.",
                SuggestedAction = "Rebuild the vector index entry before enabling query smoke."
            })
            .ToList();
        diagnostics.AddRange(entries
            .GroupBy(DuplicateKey, StringComparer.OrdinalIgnoreCase)
            .Where(duplicateGroup => duplicateGroup.Count() > 1)
            .SelectMany(duplicateGroup => duplicateGroup)
            .Select(entry => new VectorIndexDiagnostic
            {
                DiagnosticId = $"{VectorIndexDiagnosticTypes.DuplicateVectorEntry}:{entry.WorkspaceId}:{entry.CollectionId}:{entry.EntryId}",
                Type = VectorIndexDiagnosticTypes.DuplicateVectorEntry,
                Severity = "Warning",
                WorkspaceId = entry.WorkspaceId,
                CollectionId = entry.CollectionId,
                ItemId = entry.ItemId,
                EntryId = entry.EntryId,
                Message = "Duplicate vector index entry for source/provider/model.",
                SuggestedAction = "Cleanup duplicate test entries or rebuild source index."
            }));
        return diagnostics;

        VectorIndexQuery WithTakeAll() => new()
        {
            WorkspaceId = query.WorkspaceId,
            CollectionId = query.CollectionId,
            ItemKind = query.ItemKind,
            Layer = query.Layer,
            EmbeddingModel = query.EmbeddingModel,
            EmbeddingProvider = query.EmbeddingProvider,
            Take = int.MaxValue,
            IncludeVector = true
        };
    }

    public async Task<IReadOnlyList<int>> GetSupportedDimensionsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"SELECT DISTINCT dimension FROM {Table("vector_index_entries")} ORDER BY dimension;";
        var dimensions = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            dimensions.Add(reader.GetInt32(0));
        }

        return dimensions;
    }

    public async Task<IReadOnlyList<PostgresVectorProviderDistribution>> GetProviderModelDistributionAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT provider_id, model_id, dimension, count(*)
FROM {Table("vector_index_entries")}
GROUP BY provider_id, model_id, dimension
ORDER BY provider_id, model_id, dimension;
""";
        var results = new List<PostgresVectorProviderDistribution>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new PostgresVectorProviderDistribution
            {
                ProviderId = reader.GetString(0),
                ModelId = reader.GetString(1),
                Dimension = reader.GetInt32(2),
                Count = checked((int)reader.GetInt64(3))
            });
        }

        return results;
    }

    public async Task<int> CountAsync(VectorIndexQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        var filters = BuildListFilters(command, query);
        command.CommandText = $"SELECT count(*) FROM {Table("vector_index_entries")} WHERE {string.Join(" AND ", filters)};";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long count ? checked((int)count) : 0;
    }

    public async Task<int> CountCompatibleEntriesAsync(
        string providerId,
        string modelId,
        int dimension,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT count(*)
FROM {Table("vector_index_entries")}
WHERE provider_id = @provider_id
  AND model_id = @model_id
  AND dimension = @dimension;
""";
        command.Parameters.AddWithValue("provider_id", providerId);
        command.Parameters.AddWithValue("model_id", modelId);
        command.Parameters.AddWithValue("dimension", dimension);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long count ? checked((int)count) : 0;
    }

    public async Task<int> CountIncompatibleEntriesAsync(
        string providerId,
        string modelId,
        int dimension,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT count(*)
FROM {Table("vector_index_entries")}
WHERE provider_id <> @provider_id
   OR model_id <> @model_id
   OR dimension <> @dimension;
""";
        command.Parameters.AddWithValue("provider_id", providerId);
        command.Parameters.AddWithValue("model_id", modelId);
        command.Parameters.AddWithValue("dimension", dimension);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long count ? checked((int)count) : 0;
    }

    public async Task<int> CleanupTestEntriesAsync(
        string workspaceId,
        string collectionId,
        string entryPrefix,
        bool confirm,
        CancellationToken cancellationToken = default)
    {
        if (!confirm)
        {
            return 0;
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
DELETE FROM {Table("vector_index_entries")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id
  AND entry_id LIKE @entry_prefix;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", CollectionKey(collectionId));
        command.Parameters.AddWithValue("entry_prefix", entryPrefix + "%");
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected;
    }

    private static VectorIndexEntry Normalize(VectorIndexEntry entry)
    {
        var now = DateTimeOffset.UtcNow;
        var vector = entry.Vector.ToArray();
        return new VectorIndexEntry
        {
            EntryId = string.IsNullOrWhiteSpace(entry.EntryId) ? Guid.NewGuid().ToString("N") : entry.EntryId,
            ItemId = entry.ItemId,
            ItemKind = entry.ItemKind,
            Layer = entry.Layer,
            WorkspaceId = entry.WorkspaceId,
            CollectionId = CollectionKey(entry.CollectionId),
            ContentHash = entry.ContentHash,
            EmbeddingModel = entry.EmbeddingModel,
            EmbeddingProvider = entry.EmbeddingProvider,
            Dimension = entry.Dimension > 0 ? entry.Dimension : vector.Length,
            Vector = vector,
            CreatedAt = entry.CreatedAt == default ? now : entry.CreatedAt,
            UpdatedAt = now,
            Metadata = new Dictionary<string, string>(entry.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private void AddEntryParameters(Npgsql.NpgsqlCommand command, VectorIndexEntry entry)
    {
        command.Parameters.AddWithValue("workspace_id", entry.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", CollectionKey(entry.CollectionId));
        command.Parameters.AddWithValue("entry_id", entry.EntryId);
        command.Parameters.AddWithValue("item_id", entry.ItemId);
        command.Parameters.AddWithValue("source_id", entry.Metadata.TryGetValue("sourceId", out var sourceId) ? sourceId : entry.ItemId);
        command.Parameters.AddWithValue("source_kind", entry.Metadata.TryGetValue("sourceKind", out var sourceKind) ? sourceKind : entry.ItemKind);
        command.Parameters.AddWithValue("item_kind", entry.ItemKind);
        command.Parameters.AddWithValue("layer", entry.Layer);
        command.Parameters.AddWithValue("embedding_provider", entry.EmbeddingProvider);
        command.Parameters.AddWithValue("provider_id", entry.EmbeddingProvider);
        command.Parameters.AddWithValue("embedding_model", entry.EmbeddingModel);
        command.Parameters.AddWithValue("model_id", entry.EmbeddingModel);
        command.Parameters.AddWithValue("dimension", entry.Dimension);
        command.Parameters.AddWithValue("normalized", IsNormalized(entry));
        command.Parameters.AddWithValue("content_hash", entry.ContentHash);
        command.Parameters.AddWithValue("vector", PostgresVectorFormat.ToVectorLiteral(entry.Vector));
        AddJson(command, "metadata_json", entry.Metadata);
        command.Parameters.AddWithValue("created_at", entry.CreatedAt);
        command.Parameters.AddWithValue("updated_at", entry.UpdatedAt);
        AddJson(command, "data", entry);
    }

    private static List<string> BuildListFilters(Npgsql.NpgsqlCommand command, VectorIndexQuery query)
    {
        var filters = new List<string> { "workspace_id = @workspace_id" };
        command.Parameters.AddWithValue("workspace_id", query.WorkspaceId);
        if (!string.IsNullOrWhiteSpace(query.CollectionId))
        {
            filters.Add("collection_id = @collection_id");
            command.Parameters.AddWithValue("collection_id", CollectionKey(query.CollectionId));
        }

        if (!string.IsNullOrWhiteSpace(query.ItemKind))
        {
            filters.Add("item_kind = @item_kind");
            command.Parameters.AddWithValue("item_kind", query.ItemKind);
        }

        if (!string.IsNullOrWhiteSpace(query.Layer))
        {
            filters.Add("layer = @layer");
            command.Parameters.AddWithValue("layer", query.Layer);
        }

        if (!string.IsNullOrWhiteSpace(query.EmbeddingProvider))
        {
            filters.Add("provider_id = @provider_id");
            command.Parameters.AddWithValue("provider_id", query.EmbeddingProvider);
        }

        if (!string.IsNullOrWhiteSpace(query.EmbeddingModel))
        {
            filters.Add("model_id = @model_id");
            command.Parameters.AddWithValue("model_id", query.EmbeddingModel);
        }

        return filters;
    }

    private static void ValidateSearchCompatibility(VectorIndexSearchQuery query)
    {
        if (query.Vector.Count == 0)
        {
            throw new InvalidOperationException("Vector query requires a non-empty vector.");
        }

        if (string.IsNullOrWhiteSpace(query.EmbeddingProvider) || string.IsNullOrWhiteSpace(query.EmbeddingModel))
        {
            throw new InvalidOperationException("Vector query requires explicit provider/model filters to avoid mixed embedding spaces.");
        }

        if (query.Dimension is null || query.Dimension.Value != query.Vector.Count)
        {
            throw new InvalidOperationException("Vector query dimension must match the supplied vector length.");
        }
    }

    private static VectorIndexEntry Clone(VectorIndexEntry entry, bool includeVector)
    {
        return new VectorIndexEntry
        {
            EntryId = entry.EntryId,
            ItemId = entry.ItemId,
            ItemKind = entry.ItemKind,
            Layer = entry.Layer,
            WorkspaceId = entry.WorkspaceId,
            CollectionId = entry.CollectionId,
            ContentHash = entry.ContentHash,
            EmbeddingModel = entry.EmbeddingModel,
            EmbeddingProvider = entry.EmbeddingProvider,
            Dimension = entry.Dimension,
            Vector = includeVector ? entry.Vector.ToArray() : Array.Empty<float>(),
            CreatedAt = entry.CreatedAt,
            UpdatedAt = entry.UpdatedAt,
            Metadata = new Dictionary<string, string>(entry.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static bool IsNormalized(VectorIndexEntry entry)
    {
        if (entry.Vector.Count == 0)
        {
            return false;
        }

        var sum = 0.0;
        for (var i = 0; i < entry.Vector.Count; i++)
        {
            sum += entry.Vector[i] * entry.Vector[i];
        }

        return Math.Abs(Math.Sqrt(sum) - 1.0) <= 0.001;
    }

    private static string DuplicateKey(VectorIndexEntry entry)
    {
        return $"{entry.WorkspaceId}\u001f{entry.CollectionId}\u001f{entry.ItemId}\u001f{entry.EmbeddingProvider}\u001f{entry.EmbeddingModel}";
    }
}
