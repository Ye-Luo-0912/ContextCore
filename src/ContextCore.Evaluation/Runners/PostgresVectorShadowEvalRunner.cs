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

/// <summary>DB5.4 pgvector shadow eval；只读对比，不改变正式检索。</summary>
public sealed class PostgresVectorShadowEvalRunner
{
    public async Task<PostgresVectorShadowEvalReport> RunAsync(
        string datasetName,
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
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetName);
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
                datasetName,
                workspaceId,
                collectionId,
                providerId,
                modelId,
                dimension,
                normalized,
                topK,
                profileId,
                "NotConfigured",
                diagnostics.Diagnostics.Concat(["PostgresVectorDiagnosticsNotReady:" + diagnostics.Recommendation]).ToArray());
        }

        var compatibilityDiagnostics = PostgresVectorQueryPreviewRunner.ValidateGeneratorCompatibility(
            generator,
            providerId,
            modelId,
            dimension,
            normalized);
        if (compatibilityDiagnostics.Count > 0)
        {
            return NewUnavailableReport(
                datasetName,
                workspaceId,
                collectionId,
                providerId,
                modelId,
                dimension,
                normalized,
                topK,
                profileId,
                "NotConfigured",
                compatibilityDiagnostics);
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
                datasetName,
                workspaceId,
                collectionId,
                providerId,
                modelId,
                dimension,
                normalized,
                topK,
                profileId,
                "NotConfigured",
                ["PgVectorIndexEmpty", "NeedsReindex", "UseForRuntime=false"]);
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "contextcore-pgvector-shadow-eval", Guid.NewGuid().ToString("N"));
        try
        {
            var fileStore = new FileVectorIndexStore(new FileStorageOptions { RootPath = tempRoot });
            var canonicalSources = sourceItems
                .Where(item => !string.IsNullOrWhiteSpace(item.ItemId) && !string.IsNullOrWhiteSpace(item.Text))
                .GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.UpdatedAt).First())
                .OrderBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            await PostgresVectorQueryPreviewRunner.PopulateFilePreviewStoreAsync(
                    fileStore,
                    generator,
                    workspaceId,
                    collectionId,
                    canonicalSources,
                    cancellationToken)
                .ConfigureAwait(false);

            var filePreview = PostgresVectorQueryPreviewRunner.CreatePreviewService(fileStore, generator, canonicalSources);
            var postgresPreview = PostgresVectorQueryPreviewRunner.CreatePreviewService(postgresStore, generator, canonicalSources);
            var fileShadowSamples = new List<VectorQueryShadowEvalSample>();
            var postgresShadowSamples = new List<VectorQueryShadowEvalSample>();
            var comparisonSamples = new List<PostgresVectorQueryPreviewSample>();
            foreach (var sample in samples.Where(sample => !string.IsNullOrWhiteSpace(sample.Query)).OrderBy(sample => sample.Id, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = new VectorQueryPreviewRequest
                {
                    OperationId = "postgres-vector-shadow-eval-" + datasetName + "-" + sample.Id,
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    QueryText = sample.Query,
                    TopK = topK,
                    ProfileId = profileId,
                    MinSimilarity = minSimilarity,
                    Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["mode"] = sample.Mode,
                        ["createdFrom"] = "postgres_vector_shadow_eval"
                    }
                };
                var fileResult = await filePreview.PreviewAsync(request, cancellationToken).ConfigureAwait(false);
                var postgresResult = await postgresPreview.PreviewAsync(request, cancellationToken).ConfigureAwait(false);
                fileShadowSamples.Add(VectorQueryShadowEvalRunner.BuildSampleResult(sample, fileResult, 0.25));
                postgresShadowSamples.Add(VectorQueryShadowEvalRunner.BuildSampleResult(sample, postgresResult, 0.25));
                comparisonSamples.Add(PostgresVectorQueryPreviewRunner.BuildSample(sample.Id, sample.Query, fileResult, postgresResult));
            }

            var fileShadow = VectorQueryShadowEvalRunner.BuildReport("postgres-vector-shadow-eval-filesystem-" + datasetName, fileShadowSamples);
            var postgresShadow = VectorQueryShadowEvalRunner.BuildReport("postgres-vector-shadow-eval-pgvector-" + datasetName, postgresShadowSamples);
            var recallDelta = postgresShadow.MustHitRecallAfterPolicy - fileShadow.MustHitRecallAfterPolicy;
            var pgCandidateCount = comparisonSamples.Sum(item => item.PgVectorCandidateCount);
            var fileCandidateCount = comparisonSamples.Sum(item => item.FileSystemCandidateCount);
            var topKOverlapCount = comparisonSamples.Sum(item => item.TopKOverlapCount);
            var denominator = comparisonSamples.Sum(item => Math.Max(item.PgVectorTopKIds.Count, item.FileSystemTopKIds.Count));
            var metadataMismatchCount = comparisonSamples.Sum(item => item.MetadataMismatchCount);
            var eligibilityMismatchCount = comparisonSamples.Sum(item => item.EligibilityMetadataMismatchCount);
            var riskProjectionMismatchCount = comparisonSamples.Sum(item => item.RiskProjectionMismatchCount);
            var orderingMismatchCount = comparisonSamples.Count(item => !item.OrderingMatched);
            var scoreDeltaMax = comparisonSamples.Count == 0 ? 0 : comparisonSamples.Max(item => item.ScoreDeltaMax);
            var recommendation = BuildRecommendation(
                recallDelta,
                fileShadow,
                postgresShadow,
                orderingMismatchCount,
                metadataMismatchCount,
                eligibilityMismatchCount,
                riskProjectionMismatchCount);

            return new PostgresVectorShadowEvalReport
            {
                DatasetName = datasetName,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ProviderId = providerId,
                ModelId = modelId,
                Dimension = dimension,
                Normalized = normalized,
                ProfileId = profileId,
                TopK = topK,
                Recommendation = recommendation,
                SampleCount = postgresShadow.Samples,
                QueryCount = postgresShadow.QueryCount,
                PgVectorCandidateCount = pgCandidateCount,
                FileSystemCandidateCount = fileCandidateCount,
                RecallAfterPolicy = postgresShadow.MustHitRecallAfterPolicy,
                MrrAfterPolicy = CalculateMrr(postgresShadowSamples),
                FileSystemRecallAfterPolicy = fileShadow.MustHitRecallAfterPolicy,
                RecallDelta = recallDelta,
                RiskAfterPolicy = postgresShadow.RiskAfterPolicy,
                MustNotHitRiskAfterPolicy = postgresShadow.MustNotHitRiskAfterPolicy,
                LifecycleRiskAfterPolicy = postgresShadow.LifecycleRiskAfterPolicy,
                FormalOutputChanged = 0,
                TopKOverlapRate = denominator == 0 ? 1.0 : (double)topKOverlapCount / denominator,
                OrderingMismatchCount = orderingMismatchCount,
                ScoreDeltaMax = scoreDeltaMax,
                MetadataMismatchCount = metadataMismatchCount,
                EligibilityMetadataMismatchCount = eligibilityMismatchCount,
                RiskProjectionMismatchCount = riskProjectionMismatchCount,
                UseForRuntime = false,
                Samples = comparisonSamples,
                Diagnostics =
                [
                    "UseForRuntime=false",
                    "RetrievalProviderUnchanged",
                    "PackingPolicyUnchanged"
                ]
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

    public static PostgresVectorShadowEvalSummaryReport BuildSummary(
        IReadOnlyList<PostgresVectorShadowEvalReport> reports,
        IReadOnlyList<string>? preconditionDiagnostics = null)
    {
        var preconditions = preconditionDiagnostics ?? Array.Empty<string>();
        var recommendation = preconditions.Count > 0
            ? "NotConfigured"
            : BuildSummaryRecommendation(reports);
        return new PostgresVectorShadowEvalSummaryReport
        {
            Recommendation = recommendation,
            UseForRuntime = false,
            Reports = reports,
            Diagnostics =
            [
                "UseForRuntime=false",
                "VectorRetrievalReadinessUnchanged",
                .. preconditions
            ]
        };
    }

    public static string BuildMarkdown(PostgresVectorShadowEvalReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Vector Shadow Eval");
        builder.AppendLine();
        AppendReport(builder, report);
        return builder.ToString();
    }

    public static string BuildSummaryMarkdown(PostgresVectorShadowEvalSummaryReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Vector Shadow Eval Summary");
        builder.AppendLine();
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine();
        builder.AppendLine("| Dataset | Samples | Recall | FS Recall | Delta | MRR | Risk | MustNotRisk | LifecycleRisk | FormalChanged | Overlap | OrderingMismatch | ProjectionMismatch | Recommendation |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var item in report.Reports)
        {
            var projectionMismatch = item.MetadataMismatchCount + item.EligibilityMetadataMismatchCount + item.RiskProjectionMismatchCount;
            builder.AppendLine($"| {Escape(item.DatasetName)} | {item.SampleCount} | {item.RecallAfterPolicy:P2} | {item.FileSystemRecallAfterPolicy:P2} | {item.RecallDelta:0.########} | {item.MrrAfterPolicy:F4} | {item.RiskAfterPolicy} | {item.MustNotHitRiskAfterPolicy:P2} | {item.LifecycleRiskAfterPolicy:P2} | {item.FormalOutputChanged} | {item.TopKOverlapRate:P2} | {item.OrderingMismatchCount} | {projectionMismatch} | {item.Recommendation} |");
        }

        return builder.ToString();
    }

    private static void AppendReport(StringBuilder builder, PostgresVectorShadowEvalReport report)
    {
        builder.AppendLine($"- DatasetName: `{report.DatasetName}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- ProviderId: `{report.ProviderId}`");
        builder.AppendLine($"- ModelId: `{report.ModelId}`");
        builder.AppendLine($"- Dimension: `{report.Dimension}`");
        builder.AppendLine($"- Normalized: `{report.Normalized}`");
        builder.AppendLine($"- ProfileId: `{report.ProfileId}`");
        builder.AppendLine($"- TopK: `{report.TopK}`");
        builder.AppendLine($"- SampleCount: `{report.SampleCount}`");
        builder.AppendLine($"- QueryCount: `{report.QueryCount}`");
        builder.AppendLine($"- PgVectorCandidateCount: `{report.PgVectorCandidateCount}`");
        builder.AppendLine($"- FileSystemCandidateCount: `{report.FileSystemCandidateCount}`");
        builder.AppendLine($"- RecallAfterPolicy: `{report.RecallAfterPolicy:P2}`");
        builder.AppendLine($"- MrrAfterPolicy: `{report.MrrAfterPolicy:F4}`");
        builder.AppendLine($"- FileSystemRecallAfterPolicy: `{report.FileSystemRecallAfterPolicy:P2}`");
        builder.AppendLine($"- RecallDelta: `{report.RecallDelta:0.########}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- MustNotHitRiskAfterPolicy: `{report.MustNotHitRiskAfterPolicy:P2}`");
        builder.AppendLine($"- LifecycleRiskAfterPolicy: `{report.LifecycleRiskAfterPolicy:P2}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- TopKOverlapRate: `{report.TopKOverlapRate:P2}`");
        builder.AppendLine($"- OrderingMismatchCount: `{report.OrderingMismatchCount}`");
        builder.AppendLine($"- ScoreDeltaMax: `{report.ScoreDeltaMax:0.########}`");
        builder.AppendLine($"- MetadataMismatchCount: `{report.MetadataMismatchCount}`");
        builder.AppendLine($"- EligibilityMetadataMismatchCount: `{report.EligibilityMetadataMismatchCount}`");
        builder.AppendLine($"- RiskProjectionMismatchCount: `{report.RiskProjectionMismatchCount}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine();
        builder.AppendLine("| Sample | PgVector | FileSystem | Overlap | Ordering | ScoreDeltaMax | MetadataMismatch | EligibilityMismatch | RiskMismatch |");
        builder.AppendLine("|---|---:|---:|---:|---|---:|---:|---:|---:|");
        foreach (var sample in report.Samples.Take(50))
        {
            builder.AppendLine($"| {Escape(sample.SampleId)} | {sample.PgVectorCandidateCount} | {sample.FileSystemCandidateCount} | {sample.TopKOverlapCount} | {sample.OrderingMatched} | {sample.ScoreDeltaMax:0.########} | {sample.MetadataMismatchCount} | {sample.EligibilityMetadataMismatchCount} | {sample.RiskProjectionMismatchCount} |");
        }
    }

    private static string BuildRecommendation(
        double recallDelta,
        VectorQueryShadowEvalReport fileShadow,
        VectorQueryShadowEvalReport postgresShadow,
        int orderingMismatchCount,
        int metadataMismatchCount,
        int eligibilityMismatchCount,
        int riskProjectionMismatchCount)
    {
        if (postgresShadow.FormalOutputChanged != 0)
        {
            return "BlockedByFormalOutputChange";
        }

        if (riskProjectionMismatchCount > 0 || eligibilityMismatchCount > 0 || metadataMismatchCount > 0)
        {
            return "BlockedByProjectionMismatch";
        }

        if (orderingMismatchCount > 0)
        {
            return "BlockedByProjectionMismatch";
        }

        if (recallDelta < -0.000001)
        {
            return "BlockedByRecallRegression";
        }

        if (postgresShadow.RiskAfterPolicy > fileShadow.RiskAfterPolicy
            || postgresShadow.MustNotHitRiskAfterPolicy > fileShadow.MustNotHitRiskAfterPolicy + 0.000001
            || postgresShadow.LifecycleRiskAfterPolicy > fileShadow.LifecycleRiskAfterPolicy + 0.000001)
        {
            return "BlockedByRiskRegression";
        }

        return postgresShadow.RiskAfterPolicy == 0
            ? "ReadyForVectorPostgresFreeze"
            : "KeepPreviewOnly";
    }

    private static string BuildSummaryRecommendation(IReadOnlyList<PostgresVectorShadowEvalReport> reports)
    {
        if (reports.Count == 0 || reports.Any(item => string.Equals(item.Recommendation, "NotConfigured", StringComparison.OrdinalIgnoreCase)))
        {
            return "NotConfigured";
        }

        foreach (var blocked in new[]
        {
            "BlockedByFormalOutputChange",
            "BlockedByProjectionMismatch",
            "BlockedByRecallRegression",
            "BlockedByRiskRegression"
        })
        {
            if (reports.Any(item => string.Equals(item.Recommendation, blocked, StringComparison.OrdinalIgnoreCase)))
            {
                return blocked;
            }
        }

        return reports.All(item => string.Equals(item.Recommendation, "ReadyForVectorPostgresFreeze", StringComparison.OrdinalIgnoreCase))
            ? "ReadyForVectorPostgresFreeze"
            : "KeepPreviewOnly";
    }

    private static double CalculateMrr(IReadOnlyList<VectorQueryShadowEvalSample> samples)
    {
        if (samples.Count == 0)
        {
            return 0;
        }

        var total = 0.0;
        foreach (var sample in samples)
        {
            var reciprocal = 0.0;
            for (var index = 0; index < sample.Candidates.Count; index++)
            {
                var candidate = sample.Candidates[index];
                if (sample.MustHitMatchedAfterPolicy.Any(expected => EvalIdMatches(expected, candidate.ItemId)))
                {
                    reciprocal = 1.0 / (index + 1);
                    break;
                }
            }

            total += reciprocal;
        }

        return total / samples.Count;
    }

    private static bool EvalIdMatches(string expected, string candidateId)
    {
        return string.Equals(expected, candidateId, StringComparison.OrdinalIgnoreCase)
               || candidateId.EndsWith(expected, StringComparison.OrdinalIgnoreCase)
               || expected.EndsWith(candidateId, StringComparison.OrdinalIgnoreCase);
    }

    private static PostgresVectorShadowEvalReport NewUnavailableReport(
        string datasetName,
        string workspaceId,
        string collectionId,
        string providerId,
        string modelId,
        int dimension,
        bool normalized,
        int topK,
        string profileId,
        string recommendation,
        IReadOnlyList<string> diagnostics)
    {
        return new PostgresVectorShadowEvalReport
        {
            DatasetName = datasetName,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            ProviderId = providerId,
            ModelId = modelId,
            Dimension = dimension,
            Normalized = normalized,
            TopK = topK,
            ProfileId = profileId,
            Recommendation = recommendation,
            UseForRuntime = false,
            Diagnostics = diagnostics
        };
    }

    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}
