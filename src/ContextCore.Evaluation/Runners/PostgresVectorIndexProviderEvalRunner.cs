using System.Text;
using System.Text.Json;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Npgsql;

namespace ContextCore.ControlRoom.Services;

/// <summary>DB5.0 pgvector VectorIndexStore provider 的 diagnostics / compatibility / smoke runner。</summary>
public sealed class PostgresVectorIndexProviderEvalRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<PostgresVectorDiagnosticsReport> BuildDiagnosticsAsync(
        PostgresOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return new PostgresVectorDiagnosticsReport
            {
                ProviderEnabled = false,
                ConnectionAvailable = false,
                UseForRuntime = false,
                Diagnostics = ["NotConfigured"],
                Recommendation = "NotConfigured"
            };
        }

        try
        {
            await using var factory = new PostgresConnectionFactory(options);
            var serializer = new PostgresJsonSerializer();
            var migrationRunner = new PostgresMigrationRunner(factory);
            var ping = await factory.PingAsync(cancellationToken).ConfigureAwait(false);
            if (!ping.Success)
            {
                return new PostgresVectorDiagnosticsReport
                {
                    ProviderEnabled = true,
                    ConnectionAvailable = false,
                    UseForRuntime = false,
                    Diagnostics = ["BlockedByConnection", PostgresMigrationRunner.RedactConnectionString(ping.ErrorMessage ?? string.Empty)],
                    Recommendation = "NotConfigured"
                };
            }

            await migrationRunner.ApplyMigrationsAsync(confirm: true, cancellationToken).ConfigureAwait(false);
            var verification = await migrationRunner.VerifySchemaAsync(cancellationToken).ConfigureAwait(false);
            var store = new PostgresVectorIndexStore(factory, serializer, migrationRunner);
            var distribution = await store.GetProviderModelDistributionAsync(cancellationToken).ConfigureAwait(false);
            var dimensions = await store.GetSupportedDimensionsAsync(cancellationToken).ConfigureAwait(false);
            var pgvectorAvailable = await IsPgVectorAvailableAsync(factory, cancellationToken).ConfigureAwait(false);
            var tableExists = !verification.MissingRequiredTables.Any(table =>
                table.EndsWith("vector_index_entries", StringComparison.OrdinalIgnoreCase));
            var missingIndexes = RequiredVectorIndexNames(options)
                .Where(index => verification.MissingIndexes.Contains(index, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            var diagnostics = new List<string> { "UseForRuntime=false", "RetrievalProviderUnchanged" };
            if (!pgvectorAvailable)
            {
                diagnostics.Add("PgVectorExtensionMissing");
            }

            if (!tableExists)
            {
                diagnostics.Add("VectorIndexTableMissing");
            }

            if (missingIndexes.Length > 0)
            {
                diagnostics.Add("VectorIndexRequiredIndexesMissing");
            }

            return new PostgresVectorDiagnosticsReport
            {
                ProviderEnabled = true,
                ConnectionAvailable = true,
                PgVectorAvailable = pgvectorAvailable,
                SchemaVersion = verification.CurrentSchemaVersion ?? string.Empty,
                TableExists = tableExists,
                RequiredIndexesExist = missingIndexes.Length == 0,
                MissingIndexCount = missingIndexes.Length,
                MissingIndexes = missingIndexes,
                SupportedDimensionCount = dimensions.Count,
                SupportedDimensions = dimensions,
                IndexedEntryCount = distribution.Sum(item => item.Count),
                ProviderModelDistribution = distribution,
                UseForRuntime = false,
                Diagnostics = diagnostics,
                Recommendation = BuildDiagnosticsRecommendation(pgvectorAvailable, tableExists, missingIndexes.Length)
            };
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            return new PostgresVectorDiagnosticsReport
            {
                ProviderEnabled = true,
                ConnectionAvailable = false,
                UseForRuntime = false,
                Diagnostics = [ex.GetType().Name],
                Recommendation = ex is PostgresException { SqlState: "58P01" or "42704" }
                    ? "NeedsPgVectorExtension"
                    : "NotConfigured"
            };
        }
    }

    public async Task<PostgresVectorCompatibilityReport> BuildCompatibilityAsync(
        PostgresOptions options,
        string providerId,
        string modelId,
        int dimension,
        bool normalized,
        CancellationToken cancellationToken = default)
    {
        var diagnostics = await BuildDiagnosticsAsync(options, cancellationToken).ConfigureAwait(false);
        if (!diagnostics.ProviderEnabled || !diagnostics.ConnectionAvailable)
        {
            return new PostgresVectorCompatibilityReport
            {
                RequestedProviderId = providerId,
                ModelId = modelId,
                Dimension = dimension,
                Normalized = normalized,
                ProviderEnabled = diagnostics.ProviderEnabled,
                ConnectionAvailable = diagnostics.ConnectionAvailable,
                PgVectorAvailable = diagnostics.PgVectorAvailable,
                TableExists = diagnostics.TableExists,
                UseForRuntime = false,
                Diagnostics = diagnostics.Diagnostics,
                Recommendation = diagnostics.Recommendation
            };
        }

        await using var factory = new PostgresConnectionFactory(options);
        var serializer = new PostgresJsonSerializer();
        var migrationRunner = new PostgresMigrationRunner(factory);
        var store = new PostgresVectorIndexStore(factory, serializer, migrationRunner);
        var compatible = await store.CountCompatibleEntriesAsync(providerId, modelId, dimension, cancellationToken)
            .ConfigureAwait(false);
        var stale = await store.CountIncompatibleEntriesAsync(providerId, modelId, dimension, cancellationToken)
            .ConfigureAwait(false);
        var tableDimensionCompatible = dimension > 0 && diagnostics.PgVectorAvailable && diagnostics.TableExists;
        var existingIndexCompatible = tableDimensionCompatible;
        var notes = new List<string> { "UseForRuntime=false", "RetrievalProviderUnchanged" };
        if (!tableDimensionCompatible)
        {
            notes.Add("DimensionCompatibilityFailed");
        }

        if (!existingIndexCompatible)
        {
            notes.Add("ExistingIndexCompatibilityFailed");
        }

        if (stale > 0)
        {
            notes.Add("OtherProviderModelEntriesPresent");
        }

        return new PostgresVectorCompatibilityReport
        {
            RequestedProviderId = providerId,
            ModelId = modelId,
            Dimension = dimension,
            Normalized = normalized,
            ProviderEnabled = diagnostics.ProviderEnabled,
            ConnectionAvailable = diagnostics.ConnectionAvailable,
            PgVectorAvailable = diagnostics.PgVectorAvailable,
            TableExists = diagnostics.TableExists,
            TableDimensionCompatible = tableDimensionCompatible,
            ExistingIndexCompatible = existingIndexCompatible,
            ExistingCompatibleEntryCount = compatible,
            StaleProviderModelEntriesCount = stale,
            UseForRuntime = false,
            Diagnostics = notes,
            Recommendation = BuildCompatibilityRecommendation(diagnostics, tableDimensionCompatible, existingIndexCompatible)
        };
    }

    public async Task<PostgresVectorProviderSmokeReport> RunProviderSmokeAsync(
        PostgresOptions options,
        string workspaceId,
        string collectionId,
        string providerId,
        string modelId,
        int dimension,
        bool cleanupConfirm,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return new PostgresVectorProviderSmokeReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ProviderId = providerId,
                ModelId = modelId,
                Dimension = dimension,
                ProviderEnabled = false,
                ConnectionAvailable = false,
                UseForRuntime = false,
                Diagnostics = ["NotConfigured"],
                Recommendation = "NotConfigured"
            };
        }

        var mismatches = new List<string>();
        var inserted = 0;
        var upserted = 0;
        var queryCount = 0;
        var dimensionMismatchBlocked = false;
        var providerModelMismatchBlocked = false;
        try
        {
            await using var factory = new PostgresConnectionFactory(options);
            var serializer = new PostgresJsonSerializer();
            var migrationRunner = new PostgresMigrationRunner(factory);
            await migrationRunner.ApplyMigrationsAsync(confirm: true, cancellationToken).ConfigureAwait(false);
            var verification = await migrationRunner.VerifySchemaAsync(cancellationToken).ConfigureAwait(false);
            var store = new PostgresVectorIndexStore(factory, serializer, migrationRunner);
            const string prefix = "db5-vector-smoke";
            if (cleanupConfirm)
            {
                await store.CleanupTestEntriesAsync(workspaceId, collectionId, prefix, confirm: true, cancellationToken)
                    .ConfigureAwait(false);
            }

            var entries = CreateSmokeEntries(prefix, workspaceId, collectionId, providerId, modelId, dimension);
            foreach (var entry in entries)
            {
                await store.UpsertAsync(entry, cancellationToken).ConfigureAwait(false);
                inserted++;
            }

            var updated = entries[0].WithUpdatedHash("content-updated");
            await store.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
            upserted++;
            var loaded = await store.GetByEntryIdAsync(workspaceId, collectionId, updated.EntryId, includeVector: true, cancellationToken)
                .ConfigureAwait(false);
            AddMismatchIfFalse(mismatches, loaded?.ContentHash == "content-updated", "GetByEntryIdOrUpsertMismatch");

            var listed = await store.ListAsync(new VectorIndexQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                EmbeddingProvider = providerId,
                EmbeddingModel = modelId,
                IncludeVector = true,
                Take = 10
            }, cancellationToken).ConfigureAwait(false);
            AddMismatchIfFalse(mismatches, listed.Count == entries.Length, "ListByProviderModelDimensionMismatch");

            var results = await store.SearchAsync(new VectorIndexSearchQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                EmbeddingProvider = providerId,
                EmbeddingModel = modelId,
                Dimension = dimension,
                Vector = entries[1].Vector,
                IncludeVector = false,
                TopK = 3
            }, cancellationToken).ConfigureAwait(false);
            queryCount++;
            AddMismatchIfFalse(mismatches, results.Count > 0 && results[0].Entry.EntryId == entries[1].EntryId, "NearestNeighborOrderMismatch");

            try
            {
                await store.SearchAsync(new VectorIndexSearchQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    EmbeddingProvider = providerId,
                    EmbeddingModel = modelId,
                    Dimension = dimension + 1,
                    Vector = entries[1].Vector,
                    TopK = 1
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                dimensionMismatchBlocked = true;
            }

            var wrongProviderResults = await store.SearchAsync(new VectorIndexSearchQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                EmbeddingProvider = "postgres-vector-smoke-other-provider",
                EmbeddingModel = modelId,
                Dimension = dimension,
                Vector = entries[1].Vector,
                TopK = 1
            }, cancellationToken).ConfigureAwait(false);
            queryCount++;
            providerModelMismatchBlocked = wrongProviderResults.Count == 0;

            await store.DeleteAsync(workspaceId, collectionId, entries[2].EntryId, cancellationToken).ConfigureAwait(false);
            var deleted = await store.GetByEntryIdAsync(workspaceId, collectionId, entries[2].EntryId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AddMismatchIfFalse(mismatches, deleted is null, "DeleteByEntryIdMismatch");

            var cleanupPerformed = false;
            if (cleanupConfirm)
            {
                await store.CleanupTestEntriesAsync(workspaceId, collectionId, prefix, confirm: true, cancellationToken)
                    .ConfigureAwait(false);
                cleanupPerformed = true;
            }

            var missingIndexes = RequiredVectorIndexNames(options)
                .Where(index => verification.MissingIndexes.Contains(index, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            var pgvector = await IsPgVectorAvailableAsync(factory, cancellationToken).ConfigureAwait(false);
            return new PostgresVectorProviderSmokeReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ProviderId = providerId,
                ModelId = modelId,
                Dimension = dimension,
                ProviderEnabled = true,
                ConnectionAvailable = true,
                PgVectorAvailable = pgvector,
                SchemaVersion = verification.CurrentSchemaVersion ?? string.Empty,
                TableExists = !verification.MissingRequiredTables.Any(table => table.EndsWith("vector_index_entries", StringComparison.OrdinalIgnoreCase)),
                MissingIndexCount = missingIndexes.Length,
                SupportedDimensionCount = (await store.GetSupportedDimensionsAsync(cancellationToken).ConfigureAwait(false)).Count,
                InsertedCount = inserted,
                UpsertedCount = upserted,
                QueryCount = queryCount,
                MismatchCount = mismatches.Count,
                DimensionMismatchBlocked = dimensionMismatchBlocked,
                ProviderModelMismatchBlocked = providerModelMismatchBlocked,
                CleanupPerformed = cleanupPerformed,
                UseForRuntime = false,
                Diagnostics = ["UseForRuntime=false", "RetrievalProviderUnchanged"],
                Mismatches = mismatches,
                Recommendation = BuildSmokeRecommendation(pgvector, missingIndexes.Length, mismatches.Count, dimensionMismatchBlocked, providerModelMismatchBlocked)
            };
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            return new PostgresVectorProviderSmokeReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ProviderId = providerId,
                ModelId = modelId,
                Dimension = dimension,
                ProviderEnabled = true,
                ConnectionAvailable = false,
                UseForRuntime = false,
                InsertedCount = inserted,
                UpsertedCount = upserted,
                QueryCount = queryCount,
                MismatchCount = mismatches.Count,
                DimensionMismatchBlocked = dimensionMismatchBlocked,
                ProviderModelMismatchBlocked = providerModelMismatchBlocked,
                Diagnostics = [ex.GetType().Name],
                Mismatches = mismatches,
                Recommendation = "NotConfigured"
            };
        }
    }

    public static string SerializeJson<T>(T report) => JsonSerializer.Serialize(report, JsonOptions);

    public static string BuildDiagnosticsMarkdown(PostgresVectorDiagnosticsReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Vector Diagnostics");
        builder.AppendLine();
        AppendCommon(builder, report.Recommendation, report.ProviderEnabled, report.ConnectionAvailable, report.PgVectorAvailable, report.SchemaVersion, report.TableExists, report.MissingIndexCount, report.UseForRuntime);
        builder.AppendLine($"- IndexedEntryCount: `{report.IndexedEntryCount}`");
        builder.AppendLine($"- SupportedDimensionCount: `{report.SupportedDimensionCount}`");
        builder.AppendLine();
        builder.AppendLine("## Provider / Model Distribution");
        builder.AppendLine();
        builder.AppendLine("| Provider | Model | Dimension | Count |");
        builder.AppendLine("|---|---|---:|---:|");
        foreach (var item in report.ProviderModelDistribution)
        {
            builder.AppendLine($"| {Escape(item.ProviderId)} | {Escape(item.ModelId)} | {item.Dimension} | {item.Count} |");
        }

        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    public static string BuildCompatibilityMarkdown(PostgresVectorCompatibilityReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Vector Compatibility");
        builder.AppendLine();
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- RequestedProviderId: `{report.RequestedProviderId}`");
        builder.AppendLine($"- ModelId: `{report.ModelId}`");
        builder.AppendLine($"- Dimension: `{report.Dimension}`");
        builder.AppendLine($"- Normalized: `{report.Normalized}`");
        builder.AppendLine($"- PgVectorAvailable: `{report.PgVectorAvailable}`");
        builder.AppendLine($"- TableExists: `{report.TableExists}`");
        builder.AppendLine($"- TableDimensionCompatible: `{report.TableDimensionCompatible}`");
        builder.AppendLine($"- ExistingIndexCompatible: `{report.ExistingIndexCompatible}`");
        builder.AppendLine($"- ExistingCompatibleEntryCount: `{report.ExistingCompatibleEntryCount}`");
        builder.AppendLine($"- StaleProviderModelEntriesCount: `{report.StaleProviderModelEntriesCount}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    public static string BuildSmokeMarkdown(PostgresVectorProviderSmokeReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Vector Provider Smoke");
        builder.AppendLine();
        AppendCommon(builder, report.Recommendation, report.ProviderEnabled, report.ConnectionAvailable, report.PgVectorAvailable, report.SchemaVersion, report.TableExists, report.MissingIndexCount, report.UseForRuntime);
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- ProviderId: `{report.ProviderId}`");
        builder.AppendLine($"- ModelId: `{report.ModelId}`");
        builder.AppendLine($"- Dimension: `{report.Dimension}`");
        builder.AppendLine($"- InsertedCount: `{report.InsertedCount}`");
        builder.AppendLine($"- UpsertedCount: `{report.UpsertedCount}`");
        builder.AppendLine($"- QueryCount: `{report.QueryCount}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- DimensionMismatchBlocked: `{report.DimensionMismatchBlocked}`");
        builder.AppendLine($"- ProviderModelMismatchBlocked: `{report.ProviderModelMismatchBlocked}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        AppendList(builder, "Mismatches", report.Mismatches);
        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    private static async Task<bool> IsPgVectorAvailableAsync(
        PostgresConnectionFactory factory,
        CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = factory.Options.CommandTimeoutSeconds;
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'vector');";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is bool value && value;
    }

    private static IReadOnlyList<string> RequiredVectorIndexNames(PostgresOptions options)
    {
        return PostgresMigrationRunner.GetRequiredIndexNames(options)
            .Where(index => index.Contains("vector_index_entries", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static VectorIndexEntry[] CreateSmokeEntries(
        string prefix,
        string workspaceId,
        string collectionId,
        string providerId,
        string modelId,
        int dimension)
    {
        return
        [
            CreateEntry($"{prefix}-entry-a", "source-a", workspaceId, collectionId, providerId, modelId, dimension, 0),
            CreateEntry($"{prefix}-entry-b", "source-b", workspaceId, collectionId, providerId, modelId, dimension, 1),
            CreateEntry($"{prefix}-entry-c", "source-c", workspaceId, collectionId, providerId, modelId, dimension, 2)
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
        int hotIndex)
    {
        var vector = new float[dimension];
        vector[Math.Clamp(hotIndex, 0, dimension - 1)] = 1;
        return new VectorIndexEntry
        {
            EntryId = entryId,
            ItemId = sourceId,
            ItemKind = "smoke-item",
            Layer = "smoke",
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
                ["sourceKind"] = "smoke-item",
                ["trainingUse"] = "smoke_test_only"
            }
        };
    }

    private static string BuildDiagnosticsRecommendation(bool pgvectorAvailable, bool tableExists, int missingIndexCount)
    {
        if (!pgvectorAvailable)
        {
            return "NeedsPgVectorExtension";
        }

        if (!tableExists || missingIndexCount > 0)
        {
            return "NeedsSchemaMigration";
        }

        return "ReadyForVectorParityEval";
    }

    private static string BuildCompatibilityRecommendation(
        PostgresVectorDiagnosticsReport diagnostics,
        bool tableDimensionCompatible,
        bool existingIndexCompatible)
    {
        if (!diagnostics.ProviderEnabled || !diagnostics.ConnectionAvailable)
        {
            return "NotConfigured";
        }

        if (!diagnostics.PgVectorAvailable)
        {
            return "NeedsPgVectorExtension";
        }

        if (!diagnostics.TableExists || diagnostics.MissingIndexCount > 0)
        {
            return "NeedsSchemaMigration";
        }

        if (!tableDimensionCompatible)
        {
            return "BlockedByDimensionMismatch";
        }

        return existingIndexCompatible ? "ReadyForVectorParityEval" : "BlockedByProviderMismatch";
    }

    private static string BuildSmokeRecommendation(
        bool pgvectorAvailable,
        int missingIndexCount,
        int mismatchCount,
        bool dimensionMismatchBlocked,
        bool providerModelMismatchBlocked)
    {
        if (!pgvectorAvailable)
        {
            return "NeedsPgVectorExtension";
        }

        if (missingIndexCount > 0)
        {
            return "NeedsSchemaMigration";
        }

        if (!dimensionMismatchBlocked)
        {
            return "BlockedByDimensionMismatch";
        }

        if (!providerModelMismatchBlocked)
        {
            return "BlockedByProviderMismatch";
        }

        return mismatchCount == 0 ? "ReadyForVectorParityEval" : "BlockedByProviderMismatch";
    }

    private static void AddMismatchIfFalse(List<string> mismatches, bool condition, string mismatch)
    {
        if (!condition)
        {
            mismatches.Add(mismatch);
        }
    }

    private static void AppendCommon(
        StringBuilder builder,
        string recommendation,
        bool providerEnabled,
        bool connectionAvailable,
        bool pgvectorAvailable,
        string schemaVersion,
        bool tableExists,
        int missingIndexCount,
        bool useForRuntime)
    {
        builder.AppendLine($"- Recommendation: `{recommendation}`");
        builder.AppendLine($"- ProviderEnabled: `{providerEnabled}`");
        builder.AppendLine($"- ConnectionAvailable: `{connectionAvailable}`");
        builder.AppendLine($"- PgVectorAvailable: `{pgvectorAvailable}`");
        builder.AppendLine($"- SchemaVersion: `{schemaVersion}`");
        builder.AppendLine($"- TableExists: `{tableExists}`");
        builder.AppendLine($"- MissingIndexCount: `{missingIndexCount}`");
        builder.AppendLine($"- UseForRuntime: `{useForRuntime}`");
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
            builder.AppendLine($"- {Escape(value)}");
        }
    }

    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}

file static class VectorIndexEntrySmokeExtensions
{
    public static VectorIndexEntry WithUpdatedHash(this VectorIndexEntry entry, string contentHash)
    {
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
            Metadata = new Dictionary<string, string>(entry.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }
}
