using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.ControlRoom.Services;

/// <summary>DB5.3 pgvector query preview；只读对比，不接正式 retrieval。</summary>
public sealed class PostgresVectorQueryPreviewRunner
{
    public async Task<PostgresVectorQueryPreviewReport> RunAsync(
        PostgresOptions options,
        IReadOnlyList<VectorReindexSourceItem> sourceItems,
        IReadOnlyList<ContextEvalSample> samples,
        IEmbeddingGenerator generator,
        string workspaceId,
        string collectionId,
        string providerId,
        string modelId,
        int dimension,
        bool normalized,
        int topK,
        string profileId,
        double? minSimilarity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sourceItems);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(generator);

        var diagnostics = await new PostgresVectorIndexProviderEvalRunner()
            .BuildDiagnosticsAsync(options, cancellationToken)
            .ConfigureAwait(false);
        if (!diagnostics.ProviderEnabled || !diagnostics.ConnectionAvailable || diagnostics.Recommendation != "ReadyForVectorParityEval")
        {
            return NewUnavailableReport(
                workspaceId,
                collectionId,
                providerId,
                modelId,
                dimension,
                normalized,
                topK,
                profileId,
                diagnostics.Diagnostics.Concat(["PostgresVectorDiagnosticsNotReady:" + diagnostics.Recommendation]).ToArray(),
                "NotConfigured");
        }

        var compatibilityDiagnostics = ValidateGeneratorCompatibility(generator, providerId, modelId, dimension, normalized);
        if (compatibilityDiagnostics.Count > 0)
        {
            var recommendation = compatibilityDiagnostics.Any(item => item.StartsWith("GeneratorDimensionMismatch:", StringComparison.OrdinalIgnoreCase))
                ? "BlockedByDimensionMismatch"
                : "BlockedByProviderModelMismatch";
            return NewUnavailableReport(
                workspaceId,
                collectionId,
                providerId,
                modelId,
                dimension,
                normalized,
                topK,
                profileId,
                compatibilityDiagnostics,
                recommendation);
        }

        await using var factory = new PostgresConnectionFactory(options);
        var serializer = new PostgresJsonSerializer();
        var migrationRunner = new PostgresMigrationRunner(factory);
        await migrationRunner.ApplyMigrationsAsync(confirm: true, cancellationToken).ConfigureAwait(false);
        var postgresStore = new PostgresVectorIndexStore(factory, serializer, migrationRunner);
        var indexedCount = await postgresStore.CountAsync(new VectorIndexQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            EmbeddingProvider = providerId,
            EmbeddingModel = modelId,
            Take = 100_000
        }, cancellationToken).ConfigureAwait(false);
        if (indexedCount == 0)
        {
            return NewUnavailableReport(
                workspaceId,
                collectionId,
                providerId,
                modelId,
                dimension,
                normalized,
                topK,
                profileId,
                ["PgVectorIndexEmpty", "UseForRuntime=false", "RetrievalProviderUnchanged"],
                "NotConfigured");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "contextcore-pgvector-query-preview", Guid.NewGuid().ToString("N"));
        try
        {
            var fileStore = new FileVectorIndexStore(new FileStorageOptions { RootPath = tempRoot });
            var canonicalSources = sourceItems
                .Where(item => !string.IsNullOrWhiteSpace(item.ItemId) && !string.IsNullOrWhiteSpace(item.Text))
                .GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.UpdatedAt).First())
                .OrderBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            await PopulateFilePreviewStoreAsync(
                    fileStore,
                    generator,
                    workspaceId,
                    collectionId,
                    canonicalSources,
                    cancellationToken)
                .ConfigureAwait(false);

            var filePreview = CreatePreviewService(fileStore, generator, canonicalSources);
            var postgresPreview = CreatePreviewService(postgresStore, generator, canonicalSources);
            var sampleReports = new List<PostgresVectorQueryPreviewSample>();
            var querySamples = samples
                .Where(sample => !string.IsNullOrWhiteSpace(sample.Query))
                .OrderBy(sample => sample.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var sample in querySamples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = new VectorQueryPreviewRequest
                {
                    OperationId = "postgres-vector-query-preview-" + sample.Id,
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    QueryText = sample.Query,
                    TopK = topK,
                    ProfileId = profileId,
                    MinSimilarity = minSimilarity,
                    Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["mode"] = sample.Mode,
                        ["createdFrom"] = "postgres_vector_query_preview"
                    }
                };
                var fileResult = await filePreview.PreviewAsync(request, cancellationToken).ConfigureAwait(false);
                var postgresResult = await postgresPreview.PreviewAsync(request, cancellationToken).ConfigureAwait(false);
                sampleReports.Add(BuildSample(sample.Id, sample.Query, fileResult, postgresResult));
            }

            var dimensionMismatchBlocked = await CheckDimensionMismatchBlockedAsync(
                    postgresStore,
                    workspaceId,
                    collectionId,
                    generator,
                    dimension,
                    providerId,
                    modelId,
                    cancellationToken)
                .ConfigureAwait(false);
            var providerModelMismatchBlocked = await CheckProviderModelMismatchBlockedAsync(
                    postgresStore,
                    workspaceId,
                    collectionId,
                    generator,
                    dimension,
                    providerId,
                    modelId,
                    cancellationToken)
                .ConfigureAwait(false);

            var pgCandidateCount = sampleReports.Sum(item => item.PgVectorCandidateCount);
            var fileCandidateCount = sampleReports.Sum(item => item.FileSystemCandidateCount);
            var topKOverlapCount = sampleReports.Sum(item => item.TopKOverlapCount);
            var denominator = sampleReports.Sum(item => Math.Max(item.PgVectorTopKIds.Count, item.FileSystemTopKIds.Count));
            var metadataMismatchCount = sampleReports.Sum(item => item.MetadataMismatchCount);
            var eligibilityMismatchCount = sampleReports.Sum(item => item.EligibilityMetadataMismatchCount);
            var riskMismatchCount = sampleReports.Sum(item => item.RiskProjectionMismatchCount);
            var orderingMismatchCount = sampleReports.Count(item => !item.OrderingMatched);
            var scoreDeltaMax = sampleReports.Count == 0 ? 0 : sampleReports.Max(item => item.ScoreDeltaMax);
            var reportDiagnostics = new List<string>
            {
                "UseForRuntime=false",
                "RetrievalProviderUnchanged",
                "FileSystemPreviewUsesTemporaryIndexOnly"
            };
            var recommendation = BuildRecommendation(
                orderingMismatchCount,
                metadataMismatchCount,
                eligibilityMismatchCount,
                riskMismatchCount,
                dimensionMismatchBlocked,
                providerModelMismatchBlocked);

            return new PostgresVectorQueryPreviewReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ProviderId = providerId,
                ModelId = modelId,
                Dimension = dimension,
                Normalized = normalized,
                TopK = topK,
                ProfileId = profileId,
                Recommendation = recommendation,
                QueryCount = sampleReports.Count,
                CandidateCount = pgCandidateCount,
                PgVectorCandidateCount = pgCandidateCount,
                FileSystemCandidateCount = fileCandidateCount,
                TopKOverlapCount = topKOverlapCount,
                TopKOverlapRate = denominator == 0 ? 1.0 : (double)topKOverlapCount / denominator,
                OrderingMismatchCount = orderingMismatchCount,
                ScoreDeltaMax = scoreDeltaMax,
                MetadataMismatchCount = metadataMismatchCount,
                EligibilityMetadataMismatchCount = eligibilityMismatchCount,
                RiskProjectionMismatchCount = riskMismatchCount,
                DimensionMismatchBlocked = dimensionMismatchBlocked,
                ProviderModelMismatchBlocked = providerModelMismatchBlocked,
                UseForRuntime = false,
                Samples = sampleReports,
                Diagnostics = reportDiagnostics
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

    public static string BuildMarkdown(PostgresVectorQueryPreviewReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Vector Query Preview");
        builder.AppendLine();
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- ProviderId: `{report.ProviderId}`");
        builder.AppendLine($"- ModelId: `{report.ModelId}`");
        builder.AppendLine($"- Dimension: `{report.Dimension}`");
        builder.AppendLine($"- Normalized: `{report.Normalized}`");
        builder.AppendLine($"- ProfileId: `{report.ProfileId}`");
        builder.AppendLine($"- TopK: `{report.TopK}`");
        builder.AppendLine($"- QueryCount: `{report.QueryCount}`");
        builder.AppendLine($"- CandidateCount: `{report.CandidateCount}`");
        builder.AppendLine($"- PgVectorCandidateCount: `{report.PgVectorCandidateCount}`");
        builder.AppendLine($"- FileSystemCandidateCount: `{report.FileSystemCandidateCount}`");
        builder.AppendLine($"- TopKOverlapCount: `{report.TopKOverlapCount}`");
        builder.AppendLine($"- TopKOverlapRate: `{report.TopKOverlapRate:P2}`");
        builder.AppendLine($"- OrderingMismatchCount: `{report.OrderingMismatchCount}`");
        builder.AppendLine($"- ScoreDeltaMax: `{report.ScoreDeltaMax:0.########}`");
        builder.AppendLine($"- MetadataMismatchCount: `{report.MetadataMismatchCount}`");
        builder.AppendLine($"- EligibilityMetadataMismatchCount: `{report.EligibilityMetadataMismatchCount}`");
        builder.AppendLine($"- RiskProjectionMismatchCount: `{report.RiskProjectionMismatchCount}`");
        builder.AppendLine($"- DimensionMismatchBlocked: `{report.DimensionMismatchBlocked}`");
        builder.AppendLine($"- ProviderModelMismatchBlocked: `{report.ProviderModelMismatchBlocked}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        AppendList(builder, "Diagnostics", report.Diagnostics);
        builder.AppendLine();
        builder.AppendLine("## Sample Preview");
        builder.AppendLine();
        builder.AppendLine("| Sample | PgVector | FileSystem | Overlap | Ordering | ScoreDeltaMax | MetadataMismatch | EligibilityMismatch | RiskMismatch |");
        builder.AppendLine("|---|---:|---:|---:|---|---:|---:|---:|---:|");
        foreach (var sample in report.Samples.Take(50))
        {
            builder.AppendLine($"| {Escape(sample.SampleId)} | {sample.PgVectorCandidateCount} | {sample.FileSystemCandidateCount} | {sample.TopKOverlapCount} | {sample.OrderingMatched} | {sample.ScoreDeltaMax:0.########} | {sample.MetadataMismatchCount} | {sample.EligibilityMetadataMismatchCount} | {sample.RiskProjectionMismatchCount} |");
        }

        return builder.ToString();
    }

    internal static VectorQueryPreviewService CreatePreviewService(
        IVectorIndexStore store,
        IEmbeddingGenerator generator,
        IReadOnlyList<VectorReindexSourceItem> sourceItems)
    {
        var indexService = new VectorIndexService(store, generator, null, null, sourceItems);
        return new VectorQueryPreviewService(store, generator, indexService);
    }

    internal static async Task PopulateFilePreviewStoreAsync(
        IVectorIndexStore store,
        IEmbeddingGenerator generator,
        string workspaceId,
        string collectionId,
        IReadOnlyList<VectorReindexSourceItem> sourceItems,
        CancellationToken cancellationToken)
    {
        if (sourceItems.Count == 0)
        {
            return;
        }

        var result = await generator.GenerateAsync(new EmbeddingGeneratorRequest
        {
            OperationId = "postgres-vector-query-preview-filesystem-baseline",
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
        foreach (var entry in result.Entries)
        {
            await store.UpsertAsync(entry, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static PostgresVectorQueryPreviewSample BuildSample(
        string sampleId,
        string queryText,
        VectorQueryPreviewResult fileResult,
        VectorQueryPreviewResult postgresResult)
    {
        var pgIds = postgresResult.Candidates.Select(candidate => candidate.ItemId).ToArray();
        var fileIds = fileResult.Candidates.Select(candidate => candidate.ItemId).ToArray();
        var overlap = pgIds.Intersect(fileIds, StringComparer.OrdinalIgnoreCase).Count();
        var orderingMatched = pgIds.SequenceEqual(fileIds, StringComparer.OrdinalIgnoreCase);
        var metadataMismatchCount = CountProjectionMismatches(fileResult, postgresResult, MetadataProjectionEquals);
        var eligibilityMismatchCount = CountProjectionMismatches(fileResult, postgresResult, EligibilityProjectionEquals);
        var riskMismatchCount = CountProjectionMismatches(fileResult, postgresResult, RiskProjectionEquals);
        var mismatches = new List<string>();
        if (!orderingMatched)
        {
            mismatches.Add("TopKOrderingMismatch");
        }

        if (metadataMismatchCount > 0)
        {
            mismatches.Add("MetadataProjectionMismatch");
        }

        if (eligibilityMismatchCount > 0)
        {
            mismatches.Add("EligibilityProjectionMismatch");
        }

        if (riskMismatchCount > 0)
        {
            mismatches.Add("RiskProjectionMismatch");
        }

        return new PostgresVectorQueryPreviewSample
        {
            SampleId = sampleId,
            QueryText = queryText,
            PgVectorCandidateCount = postgresResult.Candidates.Count,
            FileSystemCandidateCount = fileResult.Candidates.Count,
            PgVectorTopKIds = pgIds,
            FileSystemTopKIds = fileIds,
            TopKOverlapCount = overlap,
            OrderingMatched = orderingMatched,
            ScoreDeltaMax = MaxScoreDelta(fileResult, postgresResult),
            MetadataMismatchCount = metadataMismatchCount,
            EligibilityMetadataMismatchCount = eligibilityMismatchCount,
            RiskProjectionMismatchCount = riskMismatchCount,
            Mismatches = mismatches
        };
    }

    internal static async Task<bool> CheckDimensionMismatchBlockedAsync(
        IVectorIndexStore store,
        string workspaceId,
        string collectionId,
        IEmbeddingGenerator generator,
        int dimension,
        string providerId,
        string modelId,
        CancellationToken cancellationToken)
    {
        var queryVector = await GenerateQueryVectorAsync(generator, workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
        try
        {
            await store.SearchAsync(new VectorIndexSearchQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                EmbeddingProvider = providerId,
                EmbeddingModel = modelId,
                Dimension = dimension + 1,
                Vector = queryVector,
                TopK = 1
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return true;
        }

        return false;
    }

    internal static async Task<bool> CheckProviderModelMismatchBlockedAsync(
        IVectorIndexStore store,
        string workspaceId,
        string collectionId,
        IEmbeddingGenerator generator,
        int dimension,
        string providerId,
        string modelId,
        CancellationToken cancellationToken)
    {
        var queryVector = await GenerateQueryVectorAsync(generator, workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
        var missingFilterBlocked = false;
        try
        {
            await store.SearchAsync(new VectorIndexSearchQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                EmbeddingProvider = string.Empty,
                EmbeddingModel = modelId,
                Dimension = dimension,
                Vector = queryVector,
                TopK = 1
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            missingFilterBlocked = true;
        }

        var mismatchedProviderResults = await store.SearchAsync(new VectorIndexSearchQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            EmbeddingProvider = providerId + "-mismatch",
            EmbeddingModel = modelId,
            Dimension = dimension,
            Vector = queryVector,
            TopK = 1
        }, cancellationToken).ConfigureAwait(false);
        return missingFilterBlocked && mismatchedProviderResults.Count == 0;
    }

    private static async Task<IReadOnlyList<float>> GenerateQueryVectorAsync(
        IEmbeddingGenerator generator,
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var result = await generator.GenerateAsync(new EmbeddingGeneratorRequest
        {
            OperationId = "postgres-vector-query-preview-compatibility-probe",
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Inputs =
            [
                new EmbeddingGeneratorInput
                {
                    ItemId = "__query__",
                    Text = "compatibility probe",
                    ItemKind = "query",
                    Layer = "query"
                }
            ]
        }, cancellationToken).ConfigureAwait(false);
        return result.Entries.FirstOrDefault()?.Vector ?? Array.Empty<float>();
    }

    private static int CountProjectionMismatches(
        VectorQueryPreviewResult fileResult,
        VectorQueryPreviewResult postgresResult,
        Func<VectorQueryPreviewCandidate, VectorQueryPreviewCandidate, bool> equals)
    {
        var fileById = fileResult.Candidates.ToDictionary(candidate => candidate.ItemId, StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var candidate in postgresResult.Candidates)
        {
            if (!fileById.TryGetValue(candidate.ItemId, out var fileCandidate) || !equals(fileCandidate, candidate))
            {
                count++;
            }
        }

        return count;
    }

    private static bool MetadataProjectionEquals(VectorQueryPreviewCandidate left, VectorQueryPreviewCandidate right)
    {
        return left.ContentHash == right.ContentHash
               && left.EmbeddingProvider == right.EmbeddingProvider
               && left.EmbeddingModel == right.EmbeddingModel
               && left.Dimension == right.Dimension
               && DictionaryEquals(left.Metadata, right.Metadata);
    }

    private static bool EligibilityProjectionEquals(VectorQueryPreviewCandidate left, VectorQueryPreviewCandidate right)
    {
        return string.Equals(left.EligibilityStatus, right.EligibilityStatus, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.TargetSection, right.TargetSection, StringComparison.OrdinalIgnoreCase)
               && left.BlockedReasons.SequenceEqual(right.BlockedReasons, StringComparer.OrdinalIgnoreCase)
               && left.Diagnostics.SequenceEqual(right.Diagnostics, StringComparer.OrdinalIgnoreCase);
    }

    private static bool RiskProjectionEquals(VectorQueryPreviewCandidate left, VectorQueryPreviewCandidate right)
    {
        return left.IsLifecycleRisk == right.IsLifecycleRisk
               && left.RiskIfNormalSelected == right.RiskIfNormalSelected
               && left.RiskAfterPolicy == right.RiskAfterPolicy;
    }

    private static double MaxScoreDelta(VectorQueryPreviewResult fileResult, VectorQueryPreviewResult postgresResult)
    {
        var pgById = postgresResult.Candidates.ToDictionary(candidate => candidate.ItemId, StringComparer.OrdinalIgnoreCase);
        var max = 0.0;
        foreach (var fileCandidate in fileResult.Candidates)
        {
            if (pgById.TryGetValue(fileCandidate.ItemId, out var pgCandidate))
            {
                max = Math.Max(max, Math.Abs(fileCandidate.Similarity - pgCandidate.Similarity));
            }
        }

        return max;
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

    internal static IReadOnlyList<string> ValidateGeneratorCompatibility(
        IEmbeddingGenerator generator,
        string providerId,
        string modelId,
        int dimension,
        bool normalized)
    {
        var diagnostics = new List<string>();
        if (!string.Equals(generator.Provider, providerId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(generator.Model, modelId, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add($"GeneratorProviderModelMismatch:{generator.Provider}/{generator.Model}");
        }

        if (generator.Dimension != dimension)
        {
            diagnostics.Add($"GeneratorDimensionMismatch:{generator.Dimension}");
        }

        if (generator is IEmbeddingGeneratorDescriptor descriptor && descriptor.Normalize != normalized)
        {
            diagnostics.Add($"GeneratorNormalizationMismatch:{descriptor.Normalize}");
        }

        return diagnostics;
    }

    private static bool DictionaryEquals(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var value)
                || !string.Equals(pair.Value, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildRecommendation(
        int orderingMismatchCount,
        int metadataMismatchCount,
        int eligibilityMismatchCount,
        int riskProjectionMismatchCount,
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

        if (riskProjectionMismatchCount > 0)
        {
            return "BlockedByRiskProjectionMismatch";
        }

        if (eligibilityMismatchCount > 0)
        {
            return "BlockedByEligibilityMismatch";
        }

        if (metadataMismatchCount > 0)
        {
            return "BlockedByMetadataMismatch";
        }

        return orderingMismatchCount > 0 ? "BlockedByOrderingMismatch" : "ReadyForPgVectorShadowEval";
    }

    private static PostgresVectorQueryPreviewReport NewUnavailableReport(
        string workspaceId,
        string collectionId,
        string providerId,
        string modelId,
        int dimension,
        bool normalized,
        int topK,
        string profileId,
        IReadOnlyList<string> diagnostics,
        string recommendation)
    {
        return new PostgresVectorQueryPreviewReport
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            ProviderId = providerId,
            ModelId = modelId,
            Dimension = dimension,
            Normalized = normalized,
            TopK = topK,
            ProfileId = profileId,
            Recommendation = recommendation,
            DimensionMismatchBlocked = recommendation != "BlockedByDimensionMismatch",
            ProviderModelMismatchBlocked = recommendation != "BlockedByProviderModelMismatch",
            UseForRuntime = false,
            Diagnostics = diagnostics
        };
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
}
