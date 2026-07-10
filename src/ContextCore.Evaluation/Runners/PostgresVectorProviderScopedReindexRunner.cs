using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.ControlRoom.Services;

/// <summary>DB5.2 provider-scoped pgvector reindex runner；只写 PostgresVectorIndexStore，不接正式 retrieval。</summary>
public sealed class PostgresVectorProviderScopedReindexRunner
{
    public async Task<PostgresVectorProviderScopedReindexPlan> BuildPlanAsync(
        PostgresOptions options,
        IReadOnlyList<VectorReindexSourceItem> sourceItems,
        IEmbeddingGenerator generator,
        string workspaceId,
        string collectionId,
        string providerId,
        string modelId,
        int dimension,
        bool normalized,
        string? sourceKindFilter,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sourceItems);
        ArgumentNullException.ThrowIfNull(generator);

        var diagnostics = await new PostgresVectorIndexProviderEvalRunner()
            .BuildDiagnosticsAsync(options, cancellationToken)
            .ConfigureAwait(false);
        if (!diagnostics.ProviderEnabled || !diagnostics.ConnectionAvailable || diagnostics.Recommendation != "ReadyForVectorParityEval")
        {
            return NewPlan(
                workspaceId,
                collectionId,
                providerId,
                modelId,
                dimension,
                normalized,
                sourceKindFilter,
                dryRun,
                diagnostics.Diagnostics.Concat(["PostgresVectorDiagnosticsNotReady:" + diagnostics.Recommendation]).ToArray(),
                "NotConfigured");
        }

        var compatibilityDiagnostics = ValidateGeneratorCompatibility(
            generator,
            providerId,
            modelId,
            dimension,
            normalized);
        var sources = FilterSources(sourceItems, sourceKindFilter);
        var duplicateSourceCount = Math.Max(0, sources.Count - sources
            .Select(item => item.ItemId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count());
        var canonicalSources = sources
            .GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.UpdatedAt).First())
            .OrderBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await using var factory = new PostgresConnectionFactory(options);
        var serializer = new PostgresJsonSerializer();
        var migrationRunner = new PostgresMigrationRunner(factory);
        await migrationRunner.ApplyMigrationsAsync(confirm: true, cancellationToken).ConfigureAwait(false);
        var store = new PostgresVectorIndexStore(factory, serializer, migrationRunner);
        var existing = await store.ListAsync(new VectorIndexQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            IncludeVector = false,
            Take = 100_000
        }, cancellationToken).ConfigureAwait(false);

        var sourceIds = canonicalSources.Select(item => item.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var compatibleExisting = existing
            .Where(entry => IsProviderModel(entry, providerId, modelId) && entry.Dimension == dimension)
            .ToArray();
        var orphanEntryCount = compatibleExisting.Count(entry => !sourceIds.Contains(entry.ItemId));
        var items = new List<PostgresVectorProviderScopedReindexPlanItem>(canonicalSources.Length);
        var staleEntryCount = 0;
        var dimensionMismatchCount = compatibilityDiagnostics.DimensionMismatch ? 1 : 0;
        var providerModelMismatchCount = compatibilityDiagnostics.ProviderModelMismatch ? 1 : 0;

        foreach (var source in canonicalSources)
        {
            var sourceExisting = existing
                .Where(entry => string.Equals(entry.ItemId, source.ItemId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.UpdatedAt)
                .ToArray();
            dimensionMismatchCount += sourceExisting.Count(entry => IsProviderModel(entry, providerId, modelId) && entry.Dimension != dimension);

            var currentHash = HashText(source.Text);
            var entry = sourceExisting.FirstOrDefault(entry =>
                IsProviderModel(entry, providerId, modelId) && entry.Dimension == dimension);
            if (entry is null)
            {
                items.Add(NewItem(source, ExpectedEntryId(workspaceId, collectionId, source.ItemId, modelId, providerId), "Insert", currentHash, string.Empty, "source item 尚未写入当前 pgvector provider scope。"));
                continue;
            }

            var normalizedMismatch = entry.Metadata.TryGetValue("normalize", out var normalizeValue)
                                     && bool.TryParse(normalizeValue, out var entryNormalized)
                                     && entryNormalized != normalized;
            var stale = !string.Equals(entry.ContentHash, currentHash, StringComparison.OrdinalIgnoreCase)
                        || normalizedMismatch;
            if (stale)
            {
                staleEntryCount++;
                var reason = normalizedMismatch
                    ? "normalized metadata 与目标 provider scope 不一致。"
                    : "source item content hash 已变化。";
                items.Add(NewItem(source, entry.EntryId, "Update", currentHash, entry.ContentHash, reason));
                continue;
            }

            items.Add(NewItem(source, entry.EntryId, "Skip", currentHash, entry.ContentHash, "pgvector entry 已是当前 provider scope。"));
        }

        var planDiagnostics = new List<string>
        {
            "UseForRuntime=false",
            "RetrievalProviderUnchanged",
            "FileSystemVectorStoreUnchanged"
        };
        planDiagnostics.AddRange(compatibilityDiagnostics.Diagnostics);

        return new PostgresVectorProviderScopedReindexPlan
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            ProviderId = providerId,
            ModelId = modelId,
            Dimension = dimension,
            Normalized = normalized,
            SourceKindFilter = sourceKindFilter ?? string.Empty,
            DryRun = dryRun,
            CandidateCount = canonicalSources.Length,
            PlannedInsertCount = items.Count(item => item.Action == "Insert"),
            PlannedUpdateCount = items.Count(item => item.Action == "Update"),
            PlannedDeleteCount = orphanEntryCount,
            PlannedSkipCount = items.Count(item => item.Action == "Skip"),
            StaleEntryCount = staleEntryCount,
            OrphanEntryCount = orphanEntryCount,
            DuplicateSourceCount = duplicateSourceCount,
            DimensionMismatchCount = dimensionMismatchCount,
            ProviderModelMismatchCount = providerModelMismatchCount,
            UseForRuntime = false,
            Items = items,
            Diagnostics = planDiagnostics,
            Recommendation = BuildPlanRecommendation(dimensionMismatchCount, providerModelMismatchCount)
        };
    }

    public async Task<PostgresVectorProviderScopedReindexResult> ApplyAsync(
        PostgresOptions options,
        IReadOnlyList<VectorReindexSourceItem> sourceItems,
        IEmbeddingGenerator generator,
        string workspaceId,
        string collectionId,
        string providerId,
        string modelId,
        int dimension,
        bool normalized,
        string? sourceKindFilter,
        bool confirm,
        CancellationToken cancellationToken = default)
    {
        if (!confirm)
        {
            return new PostgresVectorProviderScopedReindexResult
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ProviderId = providerId,
                ModelId = modelId,
                Dimension = dimension,
                Normalized = normalized,
                Confirmed = false,
                UseForRuntime = false,
                Diagnostics = ["ConfirmRequired", "UseForRuntime=false", "RetrievalProviderUnchanged"],
                Recommendation = "NotConfigured"
            };
        }

        var plan = await BuildPlanAsync(
            options,
            sourceItems,
            generator,
            workspaceId,
            collectionId,
            providerId,
            modelId,
            dimension,
            normalized,
            sourceKindFilter,
            dryRun: false,
            cancellationToken).ConfigureAwait(false);
        if (plan.Recommendation is "NotConfigured" or "BlockedByDimensionMismatch" or "BlockedByProviderModelMismatch")
        {
            return NewBlockedResult(plan, confirm);
        }

        await using var factory = new PostgresConnectionFactory(options);
        var serializer = new PostgresJsonSerializer();
        var migrationRunner = new PostgresMigrationRunner(factory);
        await migrationRunner.ApplyMigrationsAsync(confirm: true, cancellationToken).ConfigureAwait(false);
        var store = new PostgresVectorIndexStore(factory, serializer, migrationRunner);

        var sourceById = FilterSources(sourceItems, sourceKindFilter)
            .GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.UpdatedAt).First(),
                StringComparer.OrdinalIgnoreCase);
        var appliedInsert = 0;
        var appliedUpdate = 0;
        var metadataMismatches = 0;
        var mismatches = new List<string>();

        foreach (var item in plan.Items.Where(item => item.Action is "Insert" or "Update"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sourceById.TryGetValue(item.SourceId, out var source))
            {
                mismatches.Add("SourceMissingDuringApply:" + item.SourceId);
                continue;
            }

            var entry = (await GenerateEntriesAsync(
                    generator,
                    workspaceId,
                    collectionId,
                    [source],
                    cancellationToken).ConfigureAwait(false))
                .First();
            await store.UpsertAsync(entry, cancellationToken).ConfigureAwait(false);
            if (item.Action == "Insert")
            {
                appliedInsert++;
            }
            else
            {
                appliedUpdate++;
            }

            var loaded = await store.GetByEntryIdAsync(
                    workspaceId,
                    collectionId,
                    entry.EntryId,
                    includeVector: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!MetadataEquals(entry.Metadata, loaded?.Metadata) || loaded?.ItemId != entry.ItemId || loaded?.ItemKind != entry.ItemKind)
            {
                metadataMismatches++;
                mismatches.Add("MetadataRoundtripMismatch:" + entry.EntryId);
            }
        }

        var indexedCount = await store.CountAsync(new VectorIndexQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            EmbeddingProvider = providerId,
            EmbeddingModel = modelId,
            Take = 100_000
        }, cancellationToken).ConfigureAwait(false);

        return new PostgresVectorProviderScopedReindexResult
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            ProviderId = providerId,
            ModelId = modelId,
            Dimension = dimension,
            Normalized = normalized,
            Confirmed = true,
            CandidateCount = plan.CandidateCount,
            PlannedInsertCount = plan.PlannedInsertCount,
            PlannedUpdateCount = plan.PlannedUpdateCount,
            PlannedSkipCount = plan.PlannedSkipCount,
            AppliedInsertCount = appliedInsert,
            AppliedUpdateCount = appliedUpdate,
            MetadataRoundtripMismatchCount = metadataMismatches,
            IndexedEntryCountAfterApply = indexedCount,
            UseForRuntime = false,
            Diagnostics = ["UseForRuntime=false", "RetrievalProviderUnchanged", "FileSystemVectorStoreUnchanged"],
            Mismatches = mismatches,
            Recommendation = metadataMismatches == 0 && mismatches.Count == 0
                ? "ReadyForPgVectorQueryPreview"
                : "BlockedByMetadataMismatch"
        };
    }

    public async Task<PostgresVectorProviderScopedReindexReport> BuildQualityAsync(
        PostgresOptions options,
        IReadOnlyList<VectorReindexSourceItem> sourceItems,
        IEmbeddingGenerator generator,
        string workspaceId,
        string collectionId,
        string providerId,
        string modelId,
        int dimension,
        bool normalized,
        string? sourceKindFilter,
        PostgresVectorProviderScopedReindexResult? latestApply,
        CancellationToken cancellationToken = default)
    {
        var plan = await BuildPlanAsync(
            options,
            sourceItems,
            generator,
            workspaceId,
            collectionId,
            providerId,
            modelId,
            dimension,
            normalized,
            sourceKindFilter,
            dryRun: true,
            cancellationToken).ConfigureAwait(false);
        if (plan.Recommendation == "NotConfigured")
        {
            return NewQuality(plan, latestApply, indexedEntryCount: 0, metadataMismatchCount: 0, plan.Recommendation);
        }

        await using var factory = new PostgresConnectionFactory(options);
        var serializer = new PostgresJsonSerializer();
        var migrationRunner = new PostgresMigrationRunner(factory);
        await migrationRunner.ApplyMigrationsAsync(confirm: true, cancellationToken).ConfigureAwait(false);
        var store = new PostgresVectorIndexStore(factory, serializer, migrationRunner);
        var indexedCount = await store.CountAsync(new VectorIndexQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            EmbeddingProvider = providerId,
            EmbeddingModel = modelId,
            Take = 100_000
        }, cancellationToken).ConfigureAwait(false);
        var metadataMismatches = await CountMetadataRoundtripMismatchesAsync(
            store,
            sourceItems,
            generator,
            workspaceId,
            collectionId,
            sourceKindFilter,
            cancellationToken).ConfigureAwait(false);
        var recommendation = BuildQualityRecommendation(plan, indexedCount, metadataMismatches);
        return NewQuality(plan, latestApply, indexedCount, metadataMismatches, recommendation);
    }

    public static string BuildPlanMarkdown(PostgresVectorProviderScopedReindexPlan plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Vector Provider-scoped Reindex Plan");
        AppendPlanCommon(builder, plan.Recommendation, plan.WorkspaceId, plan.CollectionId, plan.ProviderId, plan.ModelId, plan.Dimension, plan.Normalized, plan.UseForRuntime);
        builder.AppendLine($"- SourceKindFilter: `{Display(plan.SourceKindFilter)}`");
        builder.AppendLine($"- DryRun: `{plan.DryRun}`");
        builder.AppendLine($"- CandidateCount: `{plan.CandidateCount}`");
        builder.AppendLine($"- PlannedInsertCount: `{plan.PlannedInsertCount}`");
        builder.AppendLine($"- PlannedUpdateCount: `{plan.PlannedUpdateCount}`");
        builder.AppendLine($"- PlannedDeleteCount: `{plan.PlannedDeleteCount}`");
        builder.AppendLine($"- PlannedSkipCount: `{plan.PlannedSkipCount}`");
        builder.AppendLine($"- StaleEntryCount: `{plan.StaleEntryCount}`");
        builder.AppendLine($"- OrphanEntryCount: `{plan.OrphanEntryCount}`");
        builder.AppendLine($"- DuplicateSourceCount: `{plan.DuplicateSourceCount}`");
        builder.AppendLine($"- DimensionMismatchCount: `{plan.DimensionMismatchCount}`");
        builder.AppendLine($"- ProviderModelMismatchCount: `{plan.ProviderModelMismatchCount}`");
        AppendPlanItems(builder, plan.Items);
        AppendList(builder, "Diagnostics", plan.Diagnostics);
        return builder.ToString();
    }

    public static string BuildResultMarkdown(PostgresVectorProviderScopedReindexResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Vector Provider-scoped Reindex Apply");
        AppendPlanCommon(builder, result.Recommendation, result.WorkspaceId, result.CollectionId, result.ProviderId, result.ModelId, result.Dimension, result.Normalized, result.UseForRuntime);
        builder.AppendLine($"- Confirmed: `{result.Confirmed}`");
        builder.AppendLine($"- CandidateCount: `{result.CandidateCount}`");
        builder.AppendLine($"- PlannedInsertCount: `{result.PlannedInsertCount}`");
        builder.AppendLine($"- PlannedUpdateCount: `{result.PlannedUpdateCount}`");
        builder.AppendLine($"- PlannedSkipCount: `{result.PlannedSkipCount}`");
        builder.AppendLine($"- AppliedInsertCount: `{result.AppliedInsertCount}`");
        builder.AppendLine($"- AppliedUpdateCount: `{result.AppliedUpdateCount}`");
        builder.AppendLine($"- MetadataRoundtripMismatchCount: `{result.MetadataRoundtripMismatchCount}`");
        builder.AppendLine($"- IndexedEntryCountAfterApply: `{result.IndexedEntryCountAfterApply}`");
        AppendList(builder, "Mismatches", result.Mismatches);
        AppendList(builder, "Diagnostics", result.Diagnostics);
        return builder.ToString();
    }

    public static string BuildQualityMarkdown(PostgresVectorProviderScopedReindexReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Vector Provider-scoped Reindex Quality");
        AppendPlanCommon(builder, report.Recommendation, report.WorkspaceId, report.CollectionId, report.ProviderId, report.ModelId, report.Dimension, report.Normalized, report.UseForRuntime);
        builder.AppendLine($"- CandidateCount: `{report.CandidateCount}`");
        builder.AppendLine($"- PlannedInsertCount: `{report.PlannedInsertCount}`");
        builder.AppendLine($"- PlannedUpdateCount: `{report.PlannedUpdateCount}`");
        builder.AppendLine($"- PlannedSkipCount: `{report.PlannedSkipCount}`");
        builder.AppendLine($"- AppliedInsertCount: `{report.AppliedInsertCount}`");
        builder.AppendLine($"- AppliedUpdateCount: `{report.AppliedUpdateCount}`");
        builder.AppendLine($"- StaleEntryCount: `{report.StaleEntryCount}`");
        builder.AppendLine($"- OrphanEntryCount: `{report.OrphanEntryCount}`");
        builder.AppendLine($"- DuplicateSourceCount: `{report.DuplicateSourceCount}`");
        builder.AppendLine($"- DimensionMismatchCount: `{report.DimensionMismatchCount}`");
        builder.AppendLine($"- ProviderModelMismatchCount: `{report.ProviderModelMismatchCount}`");
        builder.AppendLine($"- MetadataRoundtripMismatchCount: `{report.MetadataRoundtripMismatchCount}`");
        builder.AppendLine($"- IndexedEntryCountAfterApply: `{report.IndexedEntryCountAfterApply}`");
        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    private static PostgresVectorProviderScopedReindexPlan NewPlan(
        string workspaceId,
        string collectionId,
        string providerId,
        string modelId,
        int dimension,
        bool normalized,
        string? sourceKindFilter,
        bool dryRun,
        IReadOnlyList<string> diagnostics,
        string recommendation)
    {
        return new PostgresVectorProviderScopedReindexPlan
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            ProviderId = providerId,
            ModelId = modelId,
            Dimension = dimension,
            Normalized = normalized,
            SourceKindFilter = sourceKindFilter ?? string.Empty,
            DryRun = dryRun,
            UseForRuntime = false,
            Diagnostics = diagnostics,
            Recommendation = recommendation
        };
    }

    private static PostgresVectorProviderScopedReindexResult NewBlockedResult(
        PostgresVectorProviderScopedReindexPlan plan,
        bool confirm)
    {
        return new PostgresVectorProviderScopedReindexResult
        {
            WorkspaceId = plan.WorkspaceId,
            CollectionId = plan.CollectionId,
            ProviderId = plan.ProviderId,
            ModelId = plan.ModelId,
            Dimension = plan.Dimension,
            Normalized = plan.Normalized,
            Confirmed = confirm,
            CandidateCount = plan.CandidateCount,
            PlannedInsertCount = plan.PlannedInsertCount,
            PlannedUpdateCount = plan.PlannedUpdateCount,
            PlannedSkipCount = plan.PlannedSkipCount,
            UseForRuntime = false,
            Diagnostics = plan.Diagnostics,
            Recommendation = plan.Recommendation
        };
    }

    private static PostgresVectorProviderScopedReindexReport NewQuality(
        PostgresVectorProviderScopedReindexPlan plan,
        PostgresVectorProviderScopedReindexResult? latestApply,
        int indexedEntryCount,
        int metadataMismatchCount,
        string recommendation)
    {
        return new PostgresVectorProviderScopedReindexReport
        {
            WorkspaceId = plan.WorkspaceId,
            CollectionId = plan.CollectionId,
            ProviderId = plan.ProviderId,
            ModelId = plan.ModelId,
            Dimension = plan.Dimension,
            Normalized = plan.Normalized,
            Recommendation = recommendation,
            CandidateCount = plan.CandidateCount,
            PlannedInsertCount = plan.PlannedInsertCount,
            PlannedUpdateCount = plan.PlannedUpdateCount,
            PlannedSkipCount = plan.PlannedSkipCount,
            AppliedInsertCount = latestApply?.AppliedInsertCount ?? 0,
            AppliedUpdateCount = latestApply?.AppliedUpdateCount ?? 0,
            StaleEntryCount = plan.StaleEntryCount,
            OrphanEntryCount = plan.OrphanEntryCount,
            DuplicateSourceCount = plan.DuplicateSourceCount,
            DimensionMismatchCount = plan.DimensionMismatchCount,
            ProviderModelMismatchCount = plan.ProviderModelMismatchCount,
            MetadataRoundtripMismatchCount = metadataMismatchCount,
            IndexedEntryCountAfterApply = indexedEntryCount,
            UseForRuntime = false,
            Diagnostics = plan.Diagnostics
        };
    }

    private static async Task<IReadOnlyList<VectorIndexEntry>> GenerateEntriesAsync(
        IEmbeddingGenerator generator,
        string workspaceId,
        string collectionId,
        IReadOnlyList<VectorReindexSourceItem> sourceItems,
        CancellationToken cancellationToken)
    {
        if (sourceItems.Count == 0)
        {
            return Array.Empty<VectorIndexEntry>();
        }

        var result = await generator.GenerateAsync(new EmbeddingGeneratorRequest
        {
            OperationId = "postgres-vector-provider-scoped-reindex",
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Inputs = sourceItems.Select(source => new EmbeddingGeneratorInput
            {
                ItemId = source.ItemId,
                Text = source.Text,
                ItemKind = source.ItemKind,
                Layer = source.Layer,
                Metadata = BuildSourceMetadata(source)
            }).ToArray()
        }, cancellationToken).ConfigureAwait(false);
        return result.Entries;
    }

    private static async Task<int> CountMetadataRoundtripMismatchesAsync(
        PostgresVectorIndexStore store,
        IReadOnlyList<VectorReindexSourceItem> sourceItems,
        IEmbeddingGenerator generator,
        string workspaceId,
        string collectionId,
        string? sourceKindFilter,
        CancellationToken cancellationToken)
    {
        var sources = FilterSources(sourceItems, sourceKindFilter)
            .GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.UpdatedAt).First())
            .OrderBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var entries = await GenerateEntriesAsync(generator, workspaceId, collectionId, sources, cancellationToken)
            .ConfigureAwait(false);
        var count = 0;
        foreach (var expected in entries)
        {
            var loaded = await store.GetByEntryIdAsync(
                    workspaceId,
                    collectionId,
                    expected.EntryId,
                    includeVector: false,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!MetadataEquals(expected.Metadata, loaded?.Metadata)
                || loaded?.ItemId != expected.ItemId
                || loaded?.ItemKind != expected.ItemKind
                || loaded?.Layer != expected.Layer)
            {
                count++;
            }
        }

        return count;
    }

    private static IReadOnlyList<VectorReindexSourceItem> FilterSources(
        IReadOnlyList<VectorReindexSourceItem> sourceItems,
        string? sourceKindFilter)
    {
        if (string.IsNullOrWhiteSpace(sourceKindFilter))
        {
            return sourceItems;
        }

        return sourceItems
            .Where(item =>
                item.Metadata.TryGetValue("sourceKind", out var sourceKind)
                    ? string.Equals(sourceKind, sourceKindFilter, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(item.ItemKind, sourceKindFilter, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static PostgresVectorProviderScopedReindexPlanItem NewItem(
        VectorReindexSourceItem source,
        string entryId,
        string action,
        string currentHash,
        string existingHash,
        string reason)
    {
        return new PostgresVectorProviderScopedReindexPlanItem
        {
            SourceId = source.ItemId,
            SourceKind = ResolveSourceKind(source),
            ItemKind = source.ItemKind,
            Layer = source.Layer,
            EntryId = entryId,
            Action = action,
            CurrentContentHash = currentHash,
            ExistingContentHash = existingHash,
            Reason = reason,
            Metadata = BuildSourceMetadata(source)
        };
    }

    private static Dictionary<string, string> BuildSourceMetadata(VectorReindexSourceItem source)
    {
        return new Dictionary<string, string>(source.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["sourceId"] = source.ItemId,
            ["sourceKind"] = ResolveSourceKind(source),
            ["createdFrom"] = "postgres_vector_provider_scoped_reindex"
        };
    }

    private static string ResolveSourceKind(VectorReindexSourceItem source)
    {
        return source.Metadata.TryGetValue("sourceKind", out var sourceKind) && !string.IsNullOrWhiteSpace(sourceKind)
            ? sourceKind
            : source.ItemKind;
    }

    private static CompatibilityDiagnostics ValidateGeneratorCompatibility(
        IEmbeddingGenerator generator,
        string providerId,
        string modelId,
        int dimension,
        bool normalized)
    {
        var diagnostics = new List<string>();
        var providerModelMismatch = !string.Equals(generator.Provider, providerId, StringComparison.OrdinalIgnoreCase)
                                    || !string.Equals(generator.Model, modelId, StringComparison.OrdinalIgnoreCase);
        var dimensionMismatch = generator.Dimension != dimension;
        var descriptor = generator as IEmbeddingGeneratorDescriptor;
        if (providerModelMismatch)
        {
            diagnostics.Add($"GeneratorProviderModelMismatch:{generator.Provider}/{generator.Model}");
        }

        if (dimensionMismatch)
        {
            diagnostics.Add($"GeneratorDimensionMismatch:{generator.Dimension}");
        }

        if (descriptor is not null && descriptor.Normalize != normalized)
        {
            diagnostics.Add($"GeneratorNormalizationMismatch:{descriptor.Normalize}");
        }

        return new CompatibilityDiagnostics(providerModelMismatch, dimensionMismatch, diagnostics);
    }

    private static bool IsProviderModel(VectorIndexEntry entry, string providerId, string modelId)
    {
        return string.Equals(entry.EmbeddingProvider, providerId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(entry.EmbeddingModel, modelId, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExpectedEntryId(
        string workspaceId,
        string collectionId,
        string itemId,
        string modelId,
        string providerId)
    {
        return $"{workspaceId}:{collectionId}:{itemId}:{modelId}:{providerId}";
    }

    private static string HashText(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool MetadataEquals(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string>? actual)
    {
        if (actual is null || actual.Count != expected.Count)
        {
            return false;
        }

        foreach (var pair in expected)
        {
            if (!actual.TryGetValue(pair.Key, out var value)
                || !string.Equals(value, pair.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildPlanRecommendation(int dimensionMismatchCount, int providerModelMismatchCount)
    {
        if (dimensionMismatchCount > 0)
        {
            return "BlockedByDimensionMismatch";
        }

        return providerModelMismatchCount > 0
            ? "BlockedByProviderModelMismatch"
            : "ReadyForPgVectorQueryPreview";
    }

    private static string BuildQualityRecommendation(
        PostgresVectorProviderScopedReindexPlan plan,
        int indexedEntryCount,
        int metadataMismatchCount)
    {
        if (plan.Recommendation is "NotConfigured" or "BlockedByDimensionMismatch" or "BlockedByProviderModelMismatch")
        {
            return plan.Recommendation;
        }

        if (metadataMismatchCount > 0)
        {
            return "BlockedByMetadataMismatch";
        }

        return indexedEntryCount >= plan.CandidateCount
            ? "ReadyForPgVectorQueryPreview"
            : "NeedsProviderCompatibilityFix";
    }

    private static void AppendPlanCommon(
        StringBuilder builder,
        string recommendation,
        string workspaceId,
        string collectionId,
        string providerId,
        string modelId,
        int dimension,
        bool normalized,
        bool useForRuntime)
    {
        builder.AppendLine();
        builder.AppendLine($"- Recommendation: `{recommendation}`");
        builder.AppendLine($"- WorkspaceId: `{workspaceId}`");
        builder.AppendLine($"- CollectionId: `{collectionId}`");
        builder.AppendLine($"- ProviderId: `{providerId}`");
        builder.AppendLine($"- ModelId: `{modelId}`");
        builder.AppendLine($"- Dimension: `{dimension}`");
        builder.AppendLine($"- Normalized: `{normalized}`");
        builder.AppendLine($"- UseForRuntime: `{useForRuntime}`");
    }

    private static void AppendPlanItems(
        StringBuilder builder,
        IReadOnlyList<PostgresVectorProviderScopedReindexPlanItem> items)
    {
        builder.AppendLine();
        builder.AppendLine("## Planned Items");
        builder.AppendLine();
        builder.AppendLine("| Action | SourceId | SourceKind | ItemKind | Layer | Reason |");
        builder.AppendLine("|---|---|---|---|---|---|");
        foreach (var item in items.Take(50))
        {
            builder.AppendLine($"| {Escape(item.Action)} | {Escape(item.SourceId)} | {Escape(item.SourceKind)} | {Escape(item.ItemKind)} | {Escape(item.Layer)} | {Escape(item.Reason)} |");
        }
    }

    private static void AppendList(StringBuilder builder, string title, IReadOnlyList<string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        foreach (var value in values.DefaultIfEmpty("none"))
        {
            builder.AppendLine($"- {Escape(value)}");
        }
    }

    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);

    private static string Display(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private sealed record CompatibilityDiagnostics(
        bool ProviderModelMismatch,
        bool DimensionMismatch,
        IReadOnlyList<string> Diagnostics);
}
