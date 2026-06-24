using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.ControlRoom.Services;

/// <summary>DB5.1 VectorIndexStore FileSystem/Postgres parity eval。</summary>
public sealed class PostgresVectorIndexParityRunner
{
    private const string Prefix = "db5-vector-parity";

    public async Task<PostgresVectorIndexParityReport> RunParityAsync(
        PostgresOptions options,
        string workspaceId,
        string collectionId,
        string providerId,
        string modelId,
        int dimension,
        bool cleanupConfirm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!cleanupConfirm)
        {
            return new PostgresVectorIndexParityReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ProviderId = providerId,
                ModelId = modelId,
                Dimension = dimension,
                UseForRuntime = false,
                Diagnostics = ["CleanupConfirmRequired", "UseForRuntime=false", "RetrievalProviderUnchanged"],
                Recommendation = "NotConfigured"
            };
        }

        var diagnostics = await new PostgresVectorIndexProviderEvalRunner()
            .BuildDiagnosticsAsync(options, cancellationToken)
            .ConfigureAwait(false);
        if (!diagnostics.ProviderEnabled || !diagnostics.ConnectionAvailable || diagnostics.Recommendation != "ReadyForVectorParityEval")
        {
            return new PostgresVectorIndexParityReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ProviderId = providerId,
                ModelId = modelId,
                Dimension = dimension,
                UseForRuntime = false,
                Diagnostics = diagnostics.Diagnostics
                    .Concat(["PostgresVectorDiagnosticsNotReady:" + diagnostics.Recommendation])
                    .ToArray(),
                Recommendation = "NotConfigured"
            };
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "contextcore-vector-parity", Guid.NewGuid().ToString("N"));
        var mismatches = new List<string>();
        var operationCount = 0;
        var inserted = 0;
        var upserted = 0;
        var deleted = 0;
        var queryCount = 0;
        var orderingMismatchCount = 0;
        var metadataMismatchCount = 0;
        var scoreDeltaMax = 0.0;
        var dimensionMismatchBlocked = false;
        var providerModelMismatchBlocked = false;
        var cleanupPerformed = false;

        try
        {
            var fileStore = new FileVectorIndexStore(new FileStorageOptions { RootPath = tempRoot });
            await using var factory = new PostgresConnectionFactory(options);
            var serializer = new PostgresJsonSerializer();
            var migrationRunner = new PostgresMigrationRunner(factory);
            await migrationRunner.ApplyMigrationsAsync(confirm: true, cancellationToken).ConfigureAwait(false);
            var postgresStore = new PostgresVectorIndexStore(factory, serializer, migrationRunner);

            await postgresStore.CleanupTestEntriesAsync(workspaceId, collectionId, Prefix, confirm: true, cancellationToken)
                .ConfigureAwait(false);
            operationCount++;

            var entries = CreateParityEntries(workspaceId, collectionId, providerId, modelId, dimension);
            foreach (var entry in entries)
            {
                await fileStore.UpsertAsync(entry, cancellationToken).ConfigureAwait(false);
                await postgresStore.UpsertAsync(entry, cancellationToken).ConfigureAwait(false);
                inserted++;
                operationCount++;
            }

            var otherProviderEntry = CreateEntry(
                $"{Prefix}-other-provider",
                "source-other-provider",
                workspaceId,
                collectionId,
                "other-provider",
                modelId,
                dimension,
                UnitVector(dimension, 0));
            await fileStore.UpsertAsync(otherProviderEntry, cancellationToken).ConfigureAwait(false);
            await postgresStore.UpsertAsync(otherProviderEntry, cancellationToken).ConfigureAwait(false);
            inserted++;
            operationCount++;

            var updated = WithUpdatedMetadata(entries[0], "content-updated", "updated");
            await fileStore.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
            await postgresStore.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
            upserted++;
            operationCount++;

            var fileGet = await GetByEntryIdAsync(fileStore, workspaceId, collectionId, updated.EntryId, cancellationToken)
                .ConfigureAwait(false);
            var postgresGet = await postgresStore.GetByEntryIdAsync(workspaceId, collectionId, updated.EntryId, includeVector: true, cancellationToken)
                .ConfigureAwait(false);
            operationCount++;
            AddMismatchIfFalse(mismatches, fileGet is not null && postgresGet is not null, "GetByEntryIdMissing");
            AddMismatchIfFalse(mismatches, fileGet?.ContentHash == postgresGet?.ContentHash, "DuplicateUpsertContentHashMismatch");
            if (!MetadataEquals(fileGet, postgresGet))
            {
                metadataMismatchCount++;
                mismatches.Add("GetByEntryIdMetadataMismatch");
            }

            var fileListed = await ListCompatibleAsync(fileStore, workspaceId, collectionId, providerId, modelId, dimension, cancellationToken)
                .ConfigureAwait(false);
            var postgresListed = await ListCompatibleAsync(postgresStore, workspaceId, collectionId, providerId, modelId, dimension, cancellationToken)
                .ConfigureAwait(false);
            operationCount++;
            AddMismatchIfFalse(mismatches, fileListed.Count == entries.Length, "FileSystemCompatibleListCountMismatch");
            AddMismatchIfFalse(mismatches, postgresListed.Count == entries.Length, "PostgresCompatibleListCountMismatch");
            AddMismatchIfFalse(mismatches, SameEntryIds(fileListed, postgresListed), "CompatibleListOrderingMismatch");
            metadataMismatchCount += CountMetadataMismatches(fileListed, postgresListed);

            var queryVector = entries[0].Vector;
            var fileResults = await StrictSearchAsync(fileStore, workspaceId, collectionId, providerId, modelId, dimension, queryVector, topK: 3, cancellationToken)
                .ConfigureAwait(false);
            var postgresResults = await StrictSearchAsync(postgresStore, workspaceId, collectionId, providerId, modelId, dimension, queryVector, topK: 3, cancellationToken)
                .ConfigureAwait(false);
            queryCount += 2;
            operationCount++;
            if (!SameResultOrder(fileResults, postgresResults))
            {
                orderingMismatchCount++;
                mismatches.Add("NearestNeighborOrderingMismatch");
            }

            scoreDeltaMax = MaxScoreDelta(fileResults, postgresResults);
            if (scoreDeltaMax > 0.000001)
            {
                mismatches.Add("NearestNeighborScoreDeltaExceeded");
            }

            try
            {
                await StrictSearchAsync(postgresStore, workspaceId, collectionId, providerId, modelId, dimension + 1, queryVector, topK: 1, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                dimensionMismatchBlocked = true;
            }

            operationCount++;

            try
            {
                await StrictSearchAsync(postgresStore, workspaceId, collectionId, string.Empty, modelId, dimension, queryVector, topK: 1, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                providerModelMismatchBlocked = true;
            }

            var mixedProviderResults = await StrictSearchAsync(postgresStore, workspaceId, collectionId, providerId, modelId, dimension, queryVector, topK: 10, cancellationToken)
                .ConfigureAwait(false);
            providerModelMismatchBlocked = providerModelMismatchBlocked
                && mixedProviderResults.All(result => !string.Equals(result.Entry.EntryId, otherProviderEntry.EntryId, StringComparison.OrdinalIgnoreCase));
            queryCount++;
            operationCount++;

            await fileStore.DeleteAsync(workspaceId, collectionId, entries[2].EntryId, cancellationToken).ConfigureAwait(false);
            await postgresStore.DeleteAsync(workspaceId, collectionId, entries[2].EntryId, cancellationToken).ConfigureAwait(false);
            deleted++;
            operationCount++;
            var fileDeleted = await GetByEntryIdAsync(fileStore, workspaceId, collectionId, entries[2].EntryId, cancellationToken)
                .ConfigureAwait(false);
            var postgresDeleted = await postgresStore.GetByEntryIdAsync(workspaceId, collectionId, entries[2].EntryId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AddMismatchIfFalse(mismatches, fileDeleted is null && postgresDeleted is null, "DeleteByEntryIdMismatch");

            var finalFileCount = (await fileStore.ListAsync(new VectorIndexQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Take = 100
            }, cancellationToken).ConfigureAwait(false)).Count;
            var finalPostgresCount = await postgresStore.CountAsync(new VectorIndexQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Take = 100
            }, cancellationToken).ConfigureAwait(false);
            operationCount++;
            AddMismatchIfFalse(mismatches, finalFileCount == finalPostgresCount, "FinalEntryCountMismatch");

            await postgresStore.CleanupTestEntriesAsync(workspaceId, collectionId, Prefix, confirm: true, cancellationToken)
                .ConfigureAwait(false);
            cleanupPerformed = true;
            operationCount++;

            return new PostgresVectorIndexParityReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ProviderId = providerId,
                ModelId = modelId,
                Dimension = dimension,
                OperationCount = operationCount,
                FileSystemEntryCount = finalFileCount,
                PostgresEntryCount = finalPostgresCount,
                InsertedCount = inserted,
                UpsertedCount = upserted,
                DeletedCount = deleted,
                QueryCount = queryCount,
                MismatchCount = mismatches.Count,
                OrderingMismatchCount = orderingMismatchCount,
                ScoreDeltaMax = scoreDeltaMax,
                MetadataMismatchCount = metadataMismatchCount,
                DimensionMismatchBlocked = dimensionMismatchBlocked,
                ProviderModelMismatchBlocked = providerModelMismatchBlocked,
                NormalizedMismatchWarned = true,
                CleanupPerformed = cleanupPerformed,
                UseForRuntime = false,
                Mismatches = mismatches,
                Diagnostics = ["UseForRuntime=false", "RetrievalProviderUnchanged", "NormalizedFlagNotPartOfIVectorIndexStoreContract"],
                Recommendation = BuildRecommendation(mismatches.Count, orderingMismatchCount, dimensionMismatchBlocked, providerModelMismatchBlocked)
            };
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    public static string BuildMarkdown(PostgresVectorIndexParityReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Vector Index Parity");
        builder.AppendLine();
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- ProviderId: `{report.ProviderId}`");
        builder.AppendLine($"- ModelId: `{report.ModelId}`");
        builder.AppendLine($"- Dimension: `{report.Dimension}`");
        builder.AppendLine($"- OperationCount: `{report.OperationCount}`");
        builder.AppendLine($"- FileSystemEntryCount: `{report.FileSystemEntryCount}`");
        builder.AppendLine($"- PostgresEntryCount: `{report.PostgresEntryCount}`");
        builder.AppendLine($"- InsertedCount: `{report.InsertedCount}`");
        builder.AppendLine($"- UpsertedCount: `{report.UpsertedCount}`");
        builder.AppendLine($"- DeletedCount: `{report.DeletedCount}`");
        builder.AppendLine($"- QueryCount: `{report.QueryCount}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- OrderingMismatchCount: `{report.OrderingMismatchCount}`");
        builder.AppendLine($"- ScoreDeltaMax: `{report.ScoreDeltaMax:0.########}`");
        builder.AppendLine($"- MetadataMismatchCount: `{report.MetadataMismatchCount}`");
        builder.AppendLine($"- DimensionMismatchBlocked: `{report.DimensionMismatchBlocked}`");
        builder.AppendLine($"- ProviderModelMismatchBlocked: `{report.ProviderModelMismatchBlocked}`");
        builder.AppendLine($"- NormalizedMismatchWarned: `{report.NormalizedMismatchWarned}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        AppendList(builder, "Mismatches", report.Mismatches);
        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    private static async Task<IReadOnlyList<VectorIndexEntry>> ListCompatibleAsync(
        IVectorIndexStore store,
        string workspaceId,
        string collectionId,
        string providerId,
        string modelId,
        int dimension,
        CancellationToken cancellationToken)
    {
        var results = await store.ListAsync(new VectorIndexQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            EmbeddingProvider = providerId,
            EmbeddingModel = modelId,
            IncludeVector = true,
            Take = 100
        }, cancellationToken).ConfigureAwait(false);
        return results
            .Where(entry => entry.Dimension == dimension)
            .ToArray();
    }

    private static async Task<VectorIndexEntry?> GetByEntryIdAsync(
        IVectorIndexStore store,
        string workspaceId,
        string collectionId,
        string entryId,
        CancellationToken cancellationToken)
    {
        var entries = await store.ListAsync(new VectorIndexQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            IncludeVector = true,
            Take = 100
        }, cancellationToken).ConfigureAwait(false);
        return entries.FirstOrDefault(entry => string.Equals(entry.EntryId, entryId, StringComparison.OrdinalIgnoreCase));
    }

    private static Task<IReadOnlyList<VectorIndexSearchResult>> StrictSearchAsync(
        IVectorIndexStore store,
        string workspaceId,
        string collectionId,
        string providerId,
        string modelId,
        int dimension,
        IReadOnlyList<float> vector,
        int topK,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException("Vector parity query requires explicit provider/model filters.");
        }

        if (dimension != vector.Count)
        {
            throw new InvalidOperationException("Vector parity query dimension must match vector length.");
        }

        return store.SearchAsync(new VectorIndexSearchQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            EmbeddingProvider = providerId,
            EmbeddingModel = modelId,
            Dimension = dimension,
            Vector = vector,
            IncludeVector = false,
            TopK = topK
        }, cancellationToken);
    }

    private static VectorIndexEntry[] CreateParityEntries(
        string workspaceId,
        string collectionId,
        string providerId,
        string modelId,
        int dimension)
    {
        return
        [
            CreateEntry($"{Prefix}-entry-a", "source-a", workspaceId, collectionId, providerId, modelId, dimension, UnitVector(dimension, 0)),
            CreateEntry($"{Prefix}-entry-b", "source-b", workspaceId, collectionId, providerId, modelId, dimension, TwoDimVector(dimension, 0.8f, 0.6f)),
            CreateEntry($"{Prefix}-entry-c", "source-c", workspaceId, collectionId, providerId, modelId, dimension, UnitVector(dimension, 1))
        ];
    }

    private static VectorIndexEntry CreateEntry(
        string entryId,
        string sourceId,
        string workspaceId,
        string collectionId,
        string providerId,
        string modelId,
        int dimension,
        IReadOnlyList<float> vector)
    {
        return new VectorIndexEntry
        {
            EntryId = entryId,
            ItemId = sourceId,
            ItemKind = "parity-item",
            Layer = "parity",
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            ContentHash = "content-" + sourceId,
            EmbeddingProvider = providerId,
            EmbeddingModel = modelId,
            Dimension = dimension,
            Vector = vector,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sourceId"] = sourceId,
                ["sourceKind"] = "parity-source",
                ["metadataProbe"] = "roundtrip",
                ["trainingUse"] = "smoke_test_only"
            }
        };
    }

    private static VectorIndexEntry WithUpdatedMetadata(VectorIndexEntry entry, string contentHash, string value)
    {
        var metadata = new Dictionary<string, string>(entry.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["metadataProbe"] = value
        };
        return new VectorIndexEntry
        {
            EntryId = entry.EntryId,
            ItemId = entry.ItemId,
            ItemKind = entry.ItemKind,
            Layer = entry.Layer,
            WorkspaceId = entry.WorkspaceId,
            CollectionId = entry.CollectionId,
            ContentHash = contentHash,
            EmbeddingProvider = entry.EmbeddingProvider,
            EmbeddingModel = entry.EmbeddingModel,
            Dimension = entry.Dimension,
            Vector = entry.Vector.ToArray(),
            CreatedAt = entry.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            Metadata = metadata
        };
    }

    private static float[] UnitVector(int dimension, int index)
    {
        var vector = new float[dimension];
        vector[Math.Clamp(index, 0, dimension - 1)] = 1;
        return vector;
    }

    private static float[] TwoDimVector(int dimension, float first, float second)
    {
        var vector = new float[dimension];
        if (dimension > 0)
        {
            vector[0] = first;
        }

        if (dimension > 1)
        {
            vector[1] = second;
        }

        return vector;
    }

    private static bool SameEntryIds(IReadOnlyList<VectorIndexEntry> left, IReadOnlyList<VectorIndexEntry> right)
    {
        return left.Select(entry => entry.EntryId).SequenceEqual(right.Select(entry => entry.EntryId), StringComparer.OrdinalIgnoreCase);
    }

    private static bool SameResultOrder(
        IReadOnlyList<VectorIndexSearchResult> left,
        IReadOnlyList<VectorIndexSearchResult> right)
    {
        return left.Select(result => result.Entry.EntryId)
            .SequenceEqual(right.Select(result => result.Entry.EntryId), StringComparer.OrdinalIgnoreCase);
    }

    private static double MaxScoreDelta(
        IReadOnlyList<VectorIndexSearchResult> left,
        IReadOnlyList<VectorIndexSearchResult> right)
    {
        var count = Math.Min(left.Count, right.Count);
        var max = 0.0;
        for (var i = 0; i < count; i++)
        {
            max = Math.Max(max, Math.Abs(left[i].Score - right[i].Score));
        }

        return max;
    }

    private static int CountMetadataMismatches(
        IReadOnlyList<VectorIndexEntry> left,
        IReadOnlyList<VectorIndexEntry> right)
    {
        var rightById = right.ToDictionary(entry => entry.EntryId, StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var entry in left)
        {
            if (!rightById.TryGetValue(entry.EntryId, out var matched) || !MetadataEquals(entry, matched))
            {
                count++;
            }
        }

        return count;
    }

    private static bool MetadataEquals(VectorIndexEntry? left, VectorIndexEntry? right)
    {
        if (left is null || right is null || left.Metadata.Count != right.Metadata.Count)
        {
            return false;
        }

        foreach (var pair in left.Metadata)
        {
            if (!right.Metadata.TryGetValue(pair.Key, out var value)
                || !string.Equals(pair.Value, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void AddMismatchIfFalse(List<string> mismatches, bool condition, string mismatch)
    {
        if (!condition)
        {
            mismatches.Add(mismatch);
        }
    }

    private static string BuildRecommendation(
        int mismatchCount,
        int orderingMismatchCount,
        bool dimensionMismatchBlocked,
        bool providerModelMismatchBlocked)
    {
        if (!dimensionMismatchBlocked)
        {
            return "BlockedByDimensionMismatch";
        }

        if (!providerModelMismatchBlocked)
        {
            return "BlockedByProviderMismatch";
        }

        if (orderingMismatchCount > 0)
        {
            return "BlockedByOrderingMismatch";
        }

        return mismatchCount == 0 ? "ReadyForProviderScopedReindex" : "BlockedByMismatch";
    }

    private static void AppendList(StringBuilder builder, string title, IReadOnlyList<string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        if (values.Count == 0)
        {
            builder.AppendLine("- none");
            return;
        }

        foreach (var value in values)
        {
            builder.AppendLine($"- {value.Replace("|", "\\|", StringComparison.Ordinal)}");
        }
    }
}
