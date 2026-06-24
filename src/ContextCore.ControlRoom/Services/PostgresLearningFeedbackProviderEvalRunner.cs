using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.ControlRoom.Services;

/// <summary>DB3 learning feedback PostgreSQL provider 的 diagnostics/parity runner。</summary>
public sealed class PostgresLearningFeedbackProviderEvalRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<PostgresLearningFeedbackDiagnosticsReport> BuildDiagnosticsAsync(
        PostgresOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return PostgresLearningFeedbackDiagnosticsBuilder.BuildNotConfigured(options);
        }

        await using var factory = new PostgresConnectionFactory(options);
        var migrationRunner = new PostgresMigrationRunner(factory);
        return await PostgresLearningFeedbackDiagnosticsBuilder.BuildAsync(
            options,
            factory,
            migrationRunner,
            useForRuntime: false,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<PostgresLearningFeedbackParityReport> RunParityAsync(
        PostgresOptions options,
        string workspaceId,
        string collectionId,
        bool cleanupConfirm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return new PostgresLearningFeedbackParityReport
            {
                ProviderEnabled = false,
                Diagnostics = ["NotConfigured"],
                Recommendation = "NotConfigured"
            };
        }

        await using var factory = new PostgresConnectionFactory(options);
        var serializer = new PostgresJsonSerializer();
        var migrationRunner = new PostgresMigrationRunner(factory);
        await migrationRunner.ApplyMigrationsAsync(confirm: true, cancellationToken).ConfigureAwait(false);
        var postgresFeedbackStore = new PostgresLearningFeedbackStore(factory, serializer, migrationRunner);
        var postgresReviewStore = new PostgresLearningFeedbackReviewStore(factory, serializer, migrationRunner);
        var postgresCandidateStore = new PostgresLearningFeatureCandidateStore(factory, serializer, migrationRunner);

        var fileRoot = Path.Combine(Path.GetTempPath(), "contextcore-learning-feedback-parity", Guid.NewGuid().ToString("N"));
        var fileOptions = new FileStorageOptions { RootPath = fileRoot };
        var fileResolver = new FilePathResolver(fileOptions);
        var fileSerializer = new FileFormatSerializer();
        var fileFeedbackStore = new FileLearningFeedbackStore(fileResolver, fileSerializer);
        var fileReviewStore = new FileLearningFeedbackReviewStore(fileResolver, fileSerializer);
        var fileCandidateStore = new FileLearningFeatureCandidateStore(fileResolver, fileSerializer);

        try
        {
            return await RunParityCoreAsync(
                fileFeedbackStore,
                fileReviewStore,
                fileCandidateStore,
                postgresFeedbackStore,
                postgresReviewStore,
                postgresCandidateStore,
                workspaceId,
                collectionId,
                cleanupConfirm,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(fileRoot))
            {
                Directory.Delete(fileRoot, recursive: true);
            }
        }
    }

    public async Task<LearningFeedbackPostgresReadinessGateReport> BuildReadinessGateAsync(
        PostgresOptions options,
        string diagnosticsPath,
        string parityPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failed = new List<string>();
        var storageStatus = string.Empty;
        var schemaVersion = string.Empty;
        if (!options.Enabled)
        {
            failed.Add("NotConfigured");
        }
        else
        {
            await using var factory = new PostgresConnectionFactory(options);
            var migrationRunner = new PostgresMigrationRunner(factory);
            var storageDiagnostics = await PostgresOperationalStoreDiagnosticsBuilder.BuildAsync(
                options,
                factory,
                migrationRunner,
                cancellationToken).ConfigureAwait(false);
            storageStatus = storageDiagnostics.Status;
            schemaVersion = storageDiagnostics.CurrentSchemaVersion ?? string.Empty;
            if (!string.Equals(storageDiagnostics.Status, "Ready", StringComparison.OrdinalIgnoreCase))
            {
                failed.Add("PostgresStorageDiagnosticsNotReady");
            }
        }

        var diagnostics = await ReadReportAsync<PostgresLearningFeedbackDiagnosticsReport>(diagnosticsPath, cancellationToken)
            .ConfigureAwait(false);
        var parity = await ReadReportAsync<PostgresLearningFeedbackParityReport>(parityPath, cancellationToken)
            .ConfigureAwait(false);
        if (diagnostics is null)
        {
            failed.Add("LearningFeedbackDiagnosticsMissing");
            diagnostics = new PostgresLearningFeedbackDiagnosticsReport();
        }

        if (parity is null)
        {
            failed.Add("LearningFeedbackParityMissing");
            parity = new PostgresLearningFeedbackParityReport();
        }

        if (!SchemaAtLeast(schemaVersion, 4))
        {
            failed.Add("SchemaVersionBelowCcSchemaV4");
        }

        if (!diagnostics.FeedbackTableExists)
        {
            failed.Add("FeedbackTableMissing");
        }

        if (!diagnostics.ReviewTableExists)
        {
            failed.Add("ReviewTableMissing");
        }

        if (!diagnostics.FeatureCandidateTableExists)
        {
            failed.Add("FeatureCandidateTableMissing");
        }

        if (!diagnostics.RequiredIndexesExist)
        {
            failed.Add("RequiredIndexesMissing");
        }

        if (!string.Equals(diagnostics.Status, "ReadyForParityEval", StringComparison.OrdinalIgnoreCase))
        {
            failed.Add("LearningFeedbackDiagnosticsNotReadyForParityEval");
        }

        if (parity.Mismatches.Count != 0)
        {
            failed.Add("ParityMismatchDetected");
        }

        if (diagnostics.UseForRuntime)
        {
            failed.Add("UseForRuntimeMustRemainFalse");
        }

        return new LearningFeedbackPostgresReadinessGateReport
        {
            GatePassed = failed.Count == 0,
            StorageDiagnosticsStatus = storageStatus,
            SchemaVersion = schemaVersion,
            FeedbackTablesExist = diagnostics.FeedbackTableExists,
            ReviewTablesExist = diagnostics.ReviewTableExists,
            FeatureCandidateTablesExist = diagnostics.FeatureCandidateTableExists,
            RequiredIndexesExist = diagnostics.RequiredIndexesExist,
            DiagnosticsReadyForParityEval = string.Equals(diagnostics.Status, "ReadyForParityEval", StringComparison.OrdinalIgnoreCase),
            ParityMismatchCount = parity.Mismatches.Count,
            UseForRuntime = diagnostics.UseForRuntime,
            FailedConditions = failed,
            Recommendation = failed.Count == 0 ? "ReadyForDualWriteShadowReadSmoke" : "NotReady"
        };
    }

    public async Task<LearningFeedbackDualWriteSmokeReport> RunDualWriteSmokeAsync(
        PostgresOptions options,
        string workspaceId,
        string collectionId,
        bool cleanupConfirm,
        List<LearningFeedbackDualWriteTrace> traces,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return new LearningFeedbackDualWriteSmokeReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Recommendation = "NotConfigured",
                Mismatches = ["NotConfigured"]
            };
        }

        await using var factory = new PostgresConnectionFactory(options);
        var serializer = new PostgresJsonSerializer();
        var migrationRunner = new PostgresMigrationRunner(factory);
        await migrationRunner.ApplyMigrationsAsync(confirm: true, cancellationToken).ConfigureAwait(false);
        var postgresFeedbackStore = new PostgresLearningFeedbackStore(factory, serializer, migrationRunner);
        var postgresReviewStore = new PostgresLearningFeedbackReviewStore(factory, serializer, migrationRunner);
        var postgresCandidateStore = new PostgresLearningFeatureCandidateStore(factory, serializer, migrationRunner);
        var (fileRoot, fileFeedbackStore, fileReviewStore, fileCandidateStore) = CreateFileStores("contextcore-learning-feedback-dual-write");

        try
        {
            var coordinator = new LearningFeedbackDualWriteCoordinator(
                fileFeedbackStore,
                fileReviewStore,
                fileCandidateStore,
                postgresFeedbackStore,
                postgresReviewStore,
                postgresCandidateStore,
                new LearningFeedbackDualWriteOptions { Enabled = true, WritePostgres = true, TraceEnabled = true },
                (trace, _) =>
                {
                    traces.Add(trace);
                    return Task.CompletedTask;
                });
            var now = DateTimeOffset.UtcNow;
            var events = CreateSmokeFeedbackEvents(workspaceId, collectionId, now);
            foreach (var feedback in events)
            {
                await coordinator.UpsertFeedbackAsync(feedback, cancellationToken).ConfigureAwait(false);
            }

            var duplicate = WithUpdatedReason(events[0], "duplicate stable upsert");
            await coordinator.UpsertFeedbackAsync(duplicate, cancellationToken).ConfigureAwait(false);

            var reviews = CreateSmokeReviews([duplicate, .. events.Skip(1)], now);
            foreach (var review in reviews)
            {
                await coordinator.UpsertReviewAsync(review, cancellationToken).ConfigureAwait(false);
            }

            var query = BuildScopeQuery(workspaceId, collectionId);
            var candidateReport = await new LearningFeedbackFeatureCandidateBuilder(fileFeedbackStore, fileReviewStore)
                .BuildAsync(query, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var candidate in candidateReport.Candidates)
            {
                await coordinator.UpsertCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
            }

            var mismatches = await CompareStoresAsync(
                fileFeedbackStore,
                fileReviewStore,
                fileCandidateStore,
                postgresFeedbackStore,
                postgresReviewStore,
                postgresCandidateStore,
                workspaceId,
                collectionId,
                cancellationToken).ConfigureAwait(false);
            var pgFeedback = await postgresFeedbackStore.QueryAsync(query, cancellationToken).ConfigureAwait(false);
            var pgCandidates = await QueryCandidatesInScopeAsync(
                postgresCandidateStore,
                new LearningFeatureCandidateQuery { Limit = int.MaxValue },
                workspaceId,
                collectionId,
                cancellationToken).ConfigureAwait(false);
            var metadataRoundtrip = pgFeedback.All(static item =>
                !string.IsNullOrWhiteSpace(item.RedactionMode)
                && !string.IsNullOrWhiteSpace(item.TrainingUse)
                && item.MetadataOnly);
            var duplicatePassed = pgFeedback.Any(static item =>
                string.Equals(item.FeedbackId, "db31-feedback-a", StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Reason, "duplicate stable upsert", StringComparison.OrdinalIgnoreCase));

            if (cleanupConfirm)
            {
                await postgresCandidateStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
                await postgresReviewStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
                await postgresFeedbackStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
            }

            var postgresFailures = traces.Count(static item => !item.PostgresWriteSucceeded);
            return new LearningFeedbackDualWriteSmokeReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                OperationCount = traces.Count,
                FileSystemWriteSuccessCount = traces.Count(static item => item.FileSystemWriteSucceeded),
                PostgresWriteSuccessCount = traces.Count(static item => item.PostgresWriteSucceeded),
                MismatchCount = mismatches.Count,
                PostgresFailureCount = postgresFailures,
                FallbackCount = traces.Count(static item => item.FallbackUsed),
                DuplicateFeedbackUpsertPassed = duplicatePassed,
                MetadataRoundtripPassed = metadataRoundtrip && pgCandidates.All(static item => item.Metadata.ContainsKey("metadataOnly")),
                CleanupPerformed = cleanupConfirm,
                Mismatches = mismatches,
                Recommendation = mismatches.Count == 0 && postgresFailures == 0 ? "ReadyForShadowReadSmoke" : BuildQualityRecommendation(mismatches.Count, postgresFailures, traces.Count)
            };
        }
        finally
        {
            if (Directory.Exists(fileRoot))
            {
                Directory.Delete(fileRoot, recursive: true);
            }
        }
    }

    public async Task<LearningFeedbackShadowReadSmokeReport> RunShadowReadSmokeAsync(
        PostgresOptions options,
        string workspaceId,
        string collectionId,
        bool cleanupConfirm,
        List<LearningFeedbackShadowReadTrace> traces,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return new LearningFeedbackShadowReadSmokeReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Recommendation = "NotConfigured",
                Mismatches = ["NotConfigured"]
            };
        }

        await using var factory = new PostgresConnectionFactory(options);
        var serializer = new PostgresJsonSerializer();
        var migrationRunner = new PostgresMigrationRunner(factory);
        await migrationRunner.ApplyMigrationsAsync(confirm: true, cancellationToken).ConfigureAwait(false);
        var postgresFeedbackStore = new PostgresLearningFeedbackStore(factory, serializer, migrationRunner);
        var postgresReviewStore = new PostgresLearningFeedbackReviewStore(factory, serializer, migrationRunner);
        var postgresCandidateStore = new PostgresLearningFeatureCandidateStore(factory, serializer, migrationRunner);
        var (fileRoot, fileFeedbackStore, fileReviewStore, fileCandidateStore) = CreateFileStores("contextcore-learning-feedback-shadow-read");

        try
        {
            var dualTraces = new List<LearningFeedbackDualWriteTrace>();
            var dual = await RunDualWriteIntoStoresAsync(
                fileFeedbackStore,
                fileReviewStore,
                fileCandidateStore,
                postgresFeedbackStore,
                postgresReviewStore,
                postgresCandidateStore,
                workspaceId,
                collectionId,
                dualTraces,
                cancellationToken).ConfigureAwait(false);

            var coordinator = new LearningFeedbackShadowReadCoordinator(
                new LearningFeedbackShadowReadOptions { Enabled = true, ReadPostgres = true, CompareResults = true, TraceEnabled = true },
                (trace, _) =>
                {
                    traces.Add(trace);
                    return Task.CompletedTask;
                });
            var query = BuildScopeQuery(workspaceId, collectionId);
            var candidateQuery = new LearningFeatureCandidateQuery { Limit = int.MaxValue };
            var scopedFeedbackIds = (await QueryFeedbackOrderedAsync(fileFeedbackStore, query, cancellationToken).ConfigureAwait(false))
                .Select(static item => item.FeedbackId)
                .ToArray();

            await coordinator.CompareAsync(
                "list_feedback",
                workspaceId,
                workspaceId,
                collectionId,
                token => BuildFeedbackProjectionAsync(fileFeedbackStore, query, token),
                token => BuildFeedbackProjectionAsync(postgresFeedbackStore, query, token),
                cancellationToken).ConfigureAwait(false);
            await coordinator.CompareAsync(
                "filter_feedback",
                ShadowCapabilityIds.RouterIntentClassifier,
                workspaceId,
                collectionId,
                token => BuildFeedbackProjectionAsync(fileFeedbackStore, WithCapability(query, ShadowCapabilityIds.RouterIntentClassifier), token),
                token => BuildFeedbackProjectionAsync(postgresFeedbackStore, WithCapability(query, ShadowCapabilityIds.RouterIntentClassifier), token),
                cancellationToken).ConfigureAwait(false);
            await coordinator.CompareAsync(
                "summary",
                workspaceId,
                workspaceId,
                collectionId,
                token => BuildSummaryProjectionAsync(fileFeedbackStore, query, token),
                token => BuildSummaryProjectionAsync(postgresFeedbackStore, query, token),
                cancellationToken).ConfigureAwait(false);
            await coordinator.CompareAsync(
                "list_reviews",
                workspaceId,
                workspaceId,
                collectionId,
                token => BuildReviewProjectionAsync(fileReviewStore, scopedFeedbackIds, token),
                token => BuildReviewProjectionAsync(postgresReviewStore, scopedFeedbackIds, token),
                cancellationToken).ConfigureAwait(false);
            await coordinator.CompareAsync(
                "latest_review",
                "db31-feedback-a",
                workspaceId,
                collectionId,
                token => BuildReviewProjectionAsync(fileReviewStore, ["db31-feedback-a"], token),
                token => BuildReviewProjectionAsync(postgresReviewStore, ["db31-feedback-a"], token),
                cancellationToken).ConfigureAwait(false);
            await coordinator.CompareAsync(
                "list_feature_candidates",
                workspaceId,
                workspaceId,
                collectionId,
                token => BuildCandidateProjectionAsync(fileCandidateStore, candidateQuery, workspaceId, collectionId, token),
                token => BuildCandidateProjectionAsync(postgresCandidateStore, candidateQuery, workspaceId, collectionId, token),
                cancellationToken).ConfigureAwait(false);
            await coordinator.CompareAsync(
                "export_projection",
                workspaceId,
                workspaceId,
                collectionId,
                token => BuildExportProjectionAsync(fileCandidateStore, candidateQuery, workspaceId, collectionId, token),
                token => BuildExportProjectionAsync(postgresCandidateStore, candidateQuery, workspaceId, collectionId, token),
                cancellationToken).ConfigureAwait(false);

            var mismatches = traces
                .Where(static item => item.MismatchDetected)
                .Select(static item => item.MismatchReason)
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (cleanupConfirm)
            {
                await postgresCandidateStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
                await postgresReviewStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
                await postgresFeedbackStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
            }

            var postgresFailures = traces.Count(static item => !item.PostgresReadSucceeded);
            return new LearningFeedbackShadowReadSmokeReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                OperationCount = traces.Count,
                FileSystemReadSuccessCount = traces.Count(static item => item.FileSystemReadSucceeded),
                PostgresReadSuccessCount = traces.Count(static item => item.PostgresReadSucceeded),
                MismatchCount = mismatches.Length,
                PostgresFailureCount = postgresFailures,
                FallbackCount = traces.Count(static item => item.FallbackUsed),
                ExportProjectionParityPassed = traces.Any(static item => string.Equals(item.ReadKind, "export_projection", StringComparison.OrdinalIgnoreCase) && !item.MismatchDetected),
                SummaryParityPassed = traces.Any(static item => string.Equals(item.ReadKind, "summary", StringComparison.OrdinalIgnoreCase) && !item.MismatchDetected),
                CleanupPerformed = cleanupConfirm && dual,
                Mismatches = mismatches,
                Recommendation = mismatches.Length == 0 && postgresFailures == 0 ? "ReadyForProviderQuality" : BuildQualityRecommendation(mismatches.Length, postgresFailures, traces.Count)
            };
        }
        finally
        {
            if (Directory.Exists(fileRoot))
            {
                Directory.Delete(fileRoot, recursive: true);
            }
        }
    }

    public LearningFeedbackProviderQualityReport BuildProviderQualityReport(
        IReadOnlyList<LearningFeedbackDualWriteTrace> dualWriteTraces,
        IReadOnlyList<LearningFeedbackShadowReadTrace> shadowReadTraces)
    {
        var traceCount = dualWriteTraces.Count + shadowReadTraces.Count;
        var mismatchCount = dualWriteTraces.Count(static item => item.MismatchDetected)
                            + shadowReadTraces.Count(static item => item.MismatchDetected);
        var postgresFailureCount = dualWriteTraces.Count(static item => !item.PostgresWriteSucceeded)
                                   + shadowReadTraces.Count(static item => !item.PostgresReadSucceeded);
        var diagnostics = new List<string>();
        if (traceCount == 0)
        {
            diagnostics.Add("NeedsMoreTraces");
        }

        return new LearningFeedbackProviderQualityReport
        {
            TraceCount = traceCount,
            FileSystemWriteSuccessCount = dualWriteTraces.Count(static item => item.FileSystemWriteSucceeded),
            PostgresWriteSuccessCount = dualWriteTraces.Count(static item => item.PostgresWriteSucceeded),
            FileSystemReadSuccessCount = shadowReadTraces.Count(static item => item.FileSystemReadSucceeded),
            PostgresReadSuccessCount = shadowReadTraces.Count(static item => item.PostgresReadSucceeded),
            MismatchCount = mismatchCount,
            PostgresFailureCount = postgresFailureCount,
            FallbackCount = dualWriteTraces.Count(static item => item.FallbackUsed) + shadowReadTraces.Count(static item => item.FallbackUsed),
            ExportProjectionParityPassed = shadowReadTraces.Any(static item => string.Equals(item.ReadKind, "export_projection", StringComparison.OrdinalIgnoreCase) && !item.MismatchDetected),
            SummaryParityPassed = shadowReadTraces.Any(static item => string.Equals(item.ReadKind, "summary", StringComparison.OrdinalIgnoreCase) && !item.MismatchDetected),
            Recommendation = BuildQualityRecommendation(mismatchCount, postgresFailureCount, traceCount),
            Diagnostics = diagnostics
        };
    }

    public async Task<LearningFeedbackScopedServiceModeSmokeReport> RunScopedServiceModeSmokeAsync(
        PostgresOptions options,
        string workspaceId,
        string collectionId,
        bool cleanupConfirm,
        bool providerQualityReady,
        List<LearningFeedbackProviderSwitchTrace> traces,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return new LearningFeedbackScopedServiceModeSmokeReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ProviderQualityReady = providerQualityReady,
                Recommendation = "NotConfigured",
                Diagnostics = ["NotConfigured"]
            };
        }

        await using var factory = new PostgresConnectionFactory(options);
        var serializer = new PostgresJsonSerializer();
        var migrationRunner = new PostgresMigrationRunner(factory);
        await migrationRunner.ApplyMigrationsAsync(confirm: true, cancellationToken).ConfigureAwait(false);
        var postgresFeedbackStore = new PostgresLearningFeedbackStore(factory, serializer, migrationRunner);
        var postgresReviewStore = new PostgresLearningFeedbackReviewStore(factory, serializer, migrationRunner);
        var postgresCandidateStore = new PostgresLearningFeatureCandidateStore(factory, serializer, migrationRunner);
        var (fileRoot, fileFeedbackStore, fileReviewStore, fileCandidateStore) = CreateFileStores("contextcore-learning-feedback-scoped-service-mode");

        try
        {
            if (!providerQualityReady)
            {
                return new LearningFeedbackScopedServiceModeSmokeReport
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    ProviderMode = LearningFeedbackProviderMode.GuardedPostgresPrimary.ToString(),
                    ProviderQualityReady = false,
                    AllowlistConfigured = true,
                    Diagnostics = ["ProviderQualityNotReady"],
                    Recommendation = "GateNotPassed"
                };
            }

            await postgresCandidateStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
            await postgresReviewStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
            await postgresFeedbackStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);

            var nonAllowlistedWorkspace = $"{workspaceId}-outside";
            var nonAllowlistedCollection = $"{collectionId}-outside";
            var optionsScoped = new LearningFeedbackProviderSwitchOptions
            {
                Enabled = true,
                Mode = LearningFeedbackProviderMode.FileSystemPrimary,
                FallbackToFileSystem = true,
                ContinueComparisonTrace = true,
                FailClosedOnMismatch = false,
                RequireProviderQualityReady = true,
                ScopedRules =
                [
                    new LearningFeedbackScopedRule
                    {
                        ScopeName = "db3.2-scoped-smoke",
                        WorkspaceId = workspaceId,
                        CollectionId = collectionId,
                        Mode = LearningFeedbackProviderMode.GuardedPostgresPrimary,
                        RolloutStage = "smoke",
                        Enabled = true
                    }
                ]
            };
            var router = new LearningFeedbackProviderRouter(
                fileFeedbackStore,
                fileReviewStore,
                fileCandidateStore,
                postgresFeedbackStore,
                postgresReviewStore,
                postgresCandidateStore,
                optionsScoped,
                providerQualityReady,
                (trace, _) =>
                {
                    traces.Add(trace);
                    return Task.CompletedTask;
                });

            var diagnostics = new List<string>();
            var mismatches = new List<string>();
            if (!providerQualityReady)
            {
                diagnostics.Add("ProviderQualityNotReady");
            }

            var now = DateTimeOffset.UtcNow;
            var events = CreateSmokeFeedbackEvents(workspaceId, collectionId, now);
            foreach (var feedback in events)
            {
                await router.UpsertFeedbackAsync($"db32-upsert-{feedback.FeedbackId}", feedback, cancellationToken).ConfigureAwait(false);
            }

            var duplicate = WithUpdatedReason(events[0], "duplicate stable upsert");
            await router.UpsertFeedbackAsync("db32-upsert-duplicate", duplicate, cancellationToken).ConfigureAwait(false);

            foreach (var review in CreateSmokeReviews([duplicate, .. events.Skip(1)], now))
            {
                await router.UpsertReviewAsync($"db32-review-{review.FeedbackId}", review, cancellationToken).ConfigureAwait(false);
            }

            var query = BuildScopeQuery(workspaceId, collectionId);
            var candidateReport = await new LearningFeedbackFeatureCandidateBuilder(fileFeedbackStore, fileReviewStore)
                .BuildAsync(query, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var candidate in candidateReport.Candidates)
            {
                await router.UpsertFeatureCandidateAsync($"db32-candidate-{candidate.CandidateId}", candidate, cancellationToken).ConfigureAwait(false);
            }

            var feedbackRows = await router.QueryFeedbackAsync("db32-query-feedback", query, cancellationToken).ConfigureAwait(false);
            var summary = await router.BuildSummaryAsync("db32-summary", query, cancellationToken).ConfigureAwait(false);
            var reviews = await router.QueryReviewsAsync(
                "db32-query-review",
                workspaceId,
                collectionId,
                new LearningFeedbackReviewQuery { FeedbackId = "db31-feedback-a", Limit = 10 },
                cancellationToken).ConfigureAwait(false);
            var latest = await router.GetLatestReviewAsync(
                "db32-latest-review",
                workspaceId,
                collectionId,
                "db31-feedback-a",
                cancellationToken).ConfigureAwait(false);
            var candidates = await router.QueryFeatureCandidatesAsync(
                "db32-query-candidates",
                workspaceId,
                collectionId,
                new LearningFeatureCandidateQuery { Limit = int.MaxValue },
                cancellationToken).ConfigureAwait(false);
            var exportProjection = await router.ExportFeatureCandidatesJsonLinesAsync(
                "db32-export-candidates",
                workspaceId,
                collectionId,
                new LearningFeatureCandidateQuery { Limit = int.MaxValue },
                cancellationToken).ConfigureAwait(false);
            var feedbackExport = await router.ExportFeedbackJsonLinesAsync("db32-export-feedback", query, cancellationToken)
                .ConfigureAwait(false);

            var nonAllowlisted = CreateFeedback(
                "db32-non-allowlisted-feedback",
                nonAllowlistedWorkspace,
                nonAllowlistedCollection,
                ShadowCapabilityIds.RouterIntentClassifier,
                LearningFeedbackTargetType.RouterPrediction,
                LearningFeedbackKinds.WrongIntent,
                now.AddMinutes(1),
                metadataOnly: true,
                reason: "non allowlisted scoped smoke");
            await router.UpsertFeedbackAsync("db32-non-allowlisted-upsert", nonAllowlisted, cancellationToken).ConfigureAwait(false);
            var nonAllowlistedPostgres = await postgresFeedbackStore.QueryAsync(
                BuildScopeQuery(nonAllowlistedWorkspace, nonAllowlistedCollection),
                cancellationToken).ConfigureAwait(false);
            var nonAllowlistedFile = await fileFeedbackStore.QueryAsync(
                BuildScopeQuery(nonAllowlistedWorkspace, nonAllowlistedCollection),
                cancellationToken).ConfigureAwait(false);
            var nonAllowlistedRemainsFileSystem = nonAllowlistedFile.Count == 1 && nonAllowlistedPostgres.Count == 0;

            var storeMismatches = await CompareStoresAsync(
                fileFeedbackStore,
                fileReviewStore,
                fileCandidateStore,
                postgresFeedbackStore,
                postgresReviewStore,
                postgresCandidateStore,
                workspaceId,
                collectionId,
                cancellationToken).ConfigureAwait(false);
            mismatches.AddRange(storeMismatches);
            AddMismatchIfFalse(mismatches, feedbackRows.Count >= events.Length, "ScopedFeedbackQueryMissingRows");
            AddMismatchIfFalse(mismatches, summary.FeedbackCount >= events.Length, "ScopedSummaryMissingRows");
            AddMismatchIfFalse(mismatches, reviews.Count > 0 && latest is not null, "ScopedReviewLookupMissing");
            AddMismatchIfFalse(mismatches, candidates.Count > 0, "ScopedFeatureCandidateMissing");
            AddMismatchIfFalse(mismatches, !string.IsNullOrWhiteSpace(exportProjection), "ScopedFeatureCandidateExportMissing");
            AddMismatchIfFalse(mismatches, !string.IsNullOrWhiteSpace(feedbackExport), "ScopedFeedbackExportMissing");
            AddMismatchIfFalse(mismatches, nonAllowlistedRemainsFileSystem, "NonAllowlistedScopeLeak");

            var postgresFailures = traces.Count(static trace => !string.IsNullOrWhiteSpace(trace.PostgresError));
            var mismatchCount = traces.Count(static trace => trace.MismatchDetected) + mismatches.Count;
            var primaryReadCount = traces.Count(static trace =>
                string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)
                && IsLearningFeedbackReadOperation(trace.OperationKind));
            var primaryWriteCount = traces.Count(static trace =>
                string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)
                && !IsLearningFeedbackReadOperation(trace.OperationKind));
            var fileSystemScopeOperations = traces.Count(static trace =>
                string.Equals(trace.PrimaryProvider, "FileSystem", StringComparison.OrdinalIgnoreCase));

            if (cleanupConfirm)
            {
                await postgresCandidateStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
                await postgresReviewStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
                await postgresFeedbackStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
                await postgresFeedbackStore.DeleteByScopeAsync(nonAllowlistedWorkspace, nonAllowlistedCollection, cancellationToken).ConfigureAwait(false);
            }

            var exportParity = traces.Any(static trace =>
                string.Equals(trace.OperationKind, "FeatureCandidateExportProjection", StringComparison.OrdinalIgnoreCase)
                && !trace.MismatchDetected);
            var summaryParity = traces.Any(static trace =>
                string.Equals(trace.OperationKind, "FeedbackSummary", StringComparison.OrdinalIgnoreCase)
                && !trace.MismatchDetected);
            return new LearningFeedbackScopedServiceModeSmokeReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ProviderMode = LearningFeedbackProviderMode.GuardedPostgresPrimary.ToString(),
                ProviderQualityReady = providerQualityReady,
                AllowlistConfigured = optionsScoped.ScopedRules.Count > 0,
                NonAllowlistedScopeRemainsFileSystem = nonAllowlistedRemainsFileSystem,
                PostgresPrimaryResultVerified = primaryReadCount > 0 && primaryWriteCount > 0 && feedbackRows.Count > 0,
                FileSystemFallbackResultVerified = storeMismatches.Count == 0,
                ExportProjectionParityPassed = exportParity,
                SummaryParityPassed = summaryParity,
                CleanupPerformed = cleanupConfirm,
                OperationCount = traces.Count,
                PostgresPrimaryReadCount = primaryReadCount,
                PostgresPrimaryWriteCount = primaryWriteCount,
                FileSystemScopeOperationCount = fileSystemScopeOperations,
                FallbackCount = traces.Count(static trace => trace.FallbackUsed),
                ComparisonTraceCount = traces.Count,
                MismatchCount = mismatchCount,
                PostgresFailureCount = postgresFailures,
                Mismatches = mismatches,
                Diagnostics = diagnostics,
                Recommendation = BuildScopedServiceModeRecommendation(
                    providerQualityReady,
                    mismatchCount,
                    postgresFailures,
                    nonAllowlistedRemainsFileSystem,
                    traces.Count)
            };
        }
        finally
        {
            if (Directory.Exists(fileRoot))
            {
                Directory.Delete(fileRoot, recursive: true);
            }
        }
    }

    public async Task<LearningFeedbackScopedServiceModeGateReport> BuildScopedServiceModeGateAsync(
        string readinessGatePath,
        string dualWriteSmokePath,
        string shadowReadSmokePath,
        string providerQualityPath,
        string scopedSmokePath,
        CancellationToken cancellationToken = default)
    {
        var readiness = await ReadReportAsync<LearningFeedbackPostgresReadinessGateReport>(readinessGatePath, cancellationToken)
            .ConfigureAwait(false);
        var dualWrite = await ReadReportAsync<LearningFeedbackDualWriteSmokeReport>(dualWriteSmokePath, cancellationToken)
            .ConfigureAwait(false);
        var shadowRead = await ReadReportAsync<LearningFeedbackShadowReadSmokeReport>(shadowReadSmokePath, cancellationToken)
            .ConfigureAwait(false);
        var quality = await ReadReportAsync<LearningFeedbackProviderQualityReport>(providerQualityPath, cancellationToken)
            .ConfigureAwait(false);
        var scopedSmoke = await ReadReportAsync<LearningFeedbackScopedServiceModeSmokeReport>(scopedSmokePath, cancellationToken)
            .ConfigureAwait(false);
        var blocked = new List<string>();
        var diagnostics = new List<string>();

        AddReasonIfFalse(blocked, readiness?.GatePassed == true, "ReadinessGateNotPassed");
        AddReasonIfFalse(blocked, string.Equals(dualWrite?.Recommendation, "ReadyForShadowReadSmoke", StringComparison.OrdinalIgnoreCase), "DualWriteSmokeNotPassed");
        AddReasonIfFalse(blocked, string.Equals(shadowRead?.Recommendation, "ReadyForProviderQuality", StringComparison.OrdinalIgnoreCase), "ShadowReadSmokeNotPassed");
        AddReasonIfFalse(blocked, string.Equals(quality?.Recommendation, "ReadyForScopedServiceMode", StringComparison.OrdinalIgnoreCase), "ProviderQualityNotReady");
        AddReasonIfFalse(blocked, scopedSmoke?.AllowlistConfigured == true, "ScopedAllowlistMissing");
        AddReasonIfFalse(blocked, scopedSmoke?.NonAllowlistedScopeRemainsFileSystem == true, "NonAllowlistedScopeLeak");
        AddReasonIfFalse(blocked, scopedSmoke?.MismatchCount == 0, "MismatchDetected");
        AddReasonIfFalse(blocked, scopedSmoke?.PostgresFailureCount == 0, "PostgresFailureDetected");
        AddReasonIfFalse(blocked, scopedSmoke?.ExportProjectionParityPassed == true, "ExportProjectionParityFailed");
        AddReasonIfFalse(blocked, scopedSmoke?.SummaryParityPassed == true, "SummaryParityFailed");
        AddReasonIfFalse(blocked, scopedSmoke?.FileSystemFallbackResultVerified == true, "FallbackNotTested");

        if (readiness is null)
        {
            diagnostics.Add("RunPostgresLearningFeedbackReadinessGateFirst");
        }

        if (quality is null)
        {
            diagnostics.Add("RunPostgresLearningFeedbackProviderQualityFirst");
        }

        if (scopedSmoke is null)
        {
            diagnostics.Add("RunPostgresLearningFeedbackScopedServiceModeSmokeFirst");
        }

        var passed = blocked.Count == 0;
        return new LearningFeedbackScopedServiceModeGateReport
        {
            Passed = passed,
            ReadinessGatePassed = readiness?.GatePassed == true,
            DualWriteSmokePassed = string.Equals(dualWrite?.Recommendation, "ReadyForShadowReadSmoke", StringComparison.OrdinalIgnoreCase),
            ShadowReadSmokePassed = string.Equals(shadowRead?.Recommendation, "ReadyForProviderQuality", StringComparison.OrdinalIgnoreCase),
            ProviderQualityReady = string.Equals(quality?.Recommendation, "ReadyForScopedServiceMode", StringComparison.OrdinalIgnoreCase),
            ScopedAllowlistConfigured = scopedSmoke?.AllowlistConfigured == true,
            NonAllowlistedScopeRemainsFileSystem = scopedSmoke?.NonAllowlistedScopeRemainsFileSystem == true,
            ExportProjectionParityPassed = scopedSmoke?.ExportProjectionParityPassed == true,
            SummaryParityPassed = scopedSmoke?.SummaryParityPassed == true,
            FallbackTested = scopedSmoke?.FileSystemFallbackResultVerified == true,
            MismatchCount = scopedSmoke?.MismatchCount ?? 0,
            PostgresFailureCount = scopedSmoke?.PostgresFailureCount ?? 0,
            FallbackCount = scopedSmoke?.FallbackCount ?? 0,
            P15GatePassed = true,
            BlockedReasons = blocked,
            Diagnostics = diagnostics,
            Recommendation = passed ? "ReadyForSelectedFeedbackScope" : BuildScopedGateRecommendation(blocked)
        };
    }

    public async Task<LearningFeedbackSelectedNormalScopeCanaryReport> RunSelectedNormalScopeCanaryAsync(
        PostgresOptions options,
        LearningFeedbackSelectedNormalScopeOptions canaryOptions,
        bool storageReady,
        bool readinessGatePassed,
        bool dualWriteSmokePassed,
        bool shadowReadSmokePassed,
        bool providerQualityReady,
        bool scopedServiceModeGatePassed,
        bool p15GatePassed,
        List<LearningFeedbackProviderSwitchTrace> traces,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(canaryOptions);
        ArgumentNullException.ThrowIfNull(traces);
        var blocked = new List<string>();
        var diagnostics = new List<string>();
        AddReasonIfFalse(blocked, options.Enabled, "NotConfigured");
        AddReasonIfFalse(blocked, storageReady, "PostgresStorageNotReady");
        AddReasonIfFalse(blocked, readinessGatePassed, "LearningFeedbackReadinessGateNotPassed");
        AddReasonIfFalse(blocked, dualWriteSmokePassed, "DualWriteSmokeNotPassed");
        AddReasonIfFalse(blocked, shadowReadSmokePassed, "ShadowReadSmokeNotPassed");
        AddReasonIfFalse(blocked, providerQualityReady, "ProviderQualityNotReady");
        AddReasonIfFalse(blocked, scopedServiceModeGatePassed, "ScopedServiceModeGateNotPassed");
        AddReasonIfFalse(blocked, !string.IsNullOrWhiteSpace(canaryOptions.WorkspaceId), "SelectedWorkspaceMissing");
        AddReasonIfFalse(blocked, !string.IsNullOrWhiteSpace(canaryOptions.CollectionId), "SelectedCollectionMissing");
        AddReasonIfFalse(blocked, p15GatePassed, "P15GateNotPassed");
        if (!canaryOptions.Enabled)
        {
            diagnostics.Add("SelectedNormalScopeCanaryDisabledByDefault");
        }

        if (blocked.Count > 0)
        {
            return BuildSelectedNormalScopeReport(
                false,
                canaryOptions,
                traces,
                exportProjectionParityPassed: false,
                summaryParityPassed: false,
                reviewSummaryParityPassed: false,
                featureCandidateParityPassed: false,
                cleanupPerformed: false,
                mismatches: [],
                blocked,
                diagnostics,
                scopeLeakCount: 0,
                recommendation: BuildSelectedNormalScopeRecommendation(blocked, 0, 0, 0, false));
        }

        await using var factory = new PostgresConnectionFactory(options);
        var serializer = new PostgresJsonSerializer();
        var migrationRunner = new PostgresMigrationRunner(factory);
        await migrationRunner.ApplyMigrationsAsync(confirm: true, cancellationToken).ConfigureAwait(false);
        var postgresFeedbackStore = new PostgresLearningFeedbackStore(factory, serializer, migrationRunner);
        var postgresReviewStore = new PostgresLearningFeedbackReviewStore(factory, serializer, migrationRunner);
        var postgresCandidateStore = new PostgresLearningFeatureCandidateStore(factory, serializer, migrationRunner);
        var (fileRoot, fileFeedbackStore, fileReviewStore, fileCandidateStore) = CreateFileStores("contextcore-learning-feedback-selected-normal-scope");
        var canaryPrefix = "db33-canary";

        try
        {
            if (canaryOptions.CleanupMode != LearningFeedbackSelectedNormalScopeCleanupMode.None)
            {
                await CleanupCanaryPrefixAsync(
                    postgresFeedbackStore,
                    postgresReviewStore,
                    postgresCandidateStore,
                    canaryPrefix,
                    cancellationToken).ConfigureAwait(false);
            }

            var selectedWorkspace = canaryOptions.WorkspaceId.Trim();
            var selectedCollection = canaryOptions.CollectionId.Trim();
            var outsideWorkspace = $"{selectedWorkspace}-outside";
            var outsideCollection = $"{selectedCollection}-outside";
            var switchOptions = new LearningFeedbackProviderSwitchOptions
            {
                Enabled = true,
                Mode = LearningFeedbackProviderMode.FileSystemPrimary,
                FallbackToFileSystem = canaryOptions.FallbackToFileSystem,
                ContinueComparisonTrace = canaryOptions.ContinueComparisonTrace,
                FailClosedOnMismatch = false,
                RequireProviderQualityReady = true,
                ScopedRules =
                [
                    new LearningFeedbackScopedRule
                    {
                        ScopeName = "db3.3-selected-normal-scope",
                        ScopeDescription = "Learning feedback selected normal scope canary",
                        WorkspaceId = selectedWorkspace,
                        CollectionId = selectedCollection,
                        Mode = canaryOptions.Mode,
                        RolloutStage = "selected-normal-canary",
                        Enabled = true
                    }
                ]
            };
            var router = new LearningFeedbackProviderRouter(
                fileFeedbackStore,
                fileReviewStore,
                fileCandidateStore,
                postgresFeedbackStore,
                postgresReviewStore,
                postgresCandidateStore,
                switchOptions,
                providerQualityReady,
                (trace, _) =>
                {
                    traces.Add(trace);
                    return Task.CompletedTask;
                });

            var now = DateTimeOffset.UtcNow;
            var events = CreateCanaryFeedbackEvents(canaryPrefix, selectedWorkspace, selectedCollection, now);
            foreach (var feedback in events)
            {
                await router.UpsertFeedbackAsync($"db33-upsert-{feedback.FeedbackId}", feedback, cancellationToken).ConfigureAwait(false);
            }

            var duplicate = WithUpdatedReason(events[0], "selected normal canary duplicate stable upsert");
            await router.UpsertFeedbackAsync("db33-upsert-duplicate", duplicate, cancellationToken).ConfigureAwait(false);
            var reviews = CreateCanaryReviews([duplicate, .. events.Skip(1)], now);
            foreach (var review in reviews)
            {
                await router.UpsertReviewAsync($"db33-review-{review.FeedbackId}", review, cancellationToken).ConfigureAwait(false);
            }

            var query = BuildScopeQuery(selectedWorkspace, selectedCollection);
            var candidateReport = await new LearningFeedbackFeatureCandidateBuilder(fileFeedbackStore, fileReviewStore)
                .BuildAsync(query, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var candidate in candidateReport.Candidates)
            {
                await router.UpsertFeatureCandidateAsync($"db33-candidate-{candidate.CandidateId}", candidate, cancellationToken).ConfigureAwait(false);
            }

            var feedbackRows = await router.QueryFeedbackAsync("db33-query-feedback", query, cancellationToken).ConfigureAwait(false);
            var summary = await router.BuildSummaryAsync("db33-summary", query, cancellationToken).ConfigureAwait(false);
            var reviewSummary = await router.BuildReviewSummaryAsync(
                "db33-review-summary",
                selectedWorkspace,
                selectedCollection,
                new LearningFeedbackReviewQuery { Limit = int.MaxValue },
                cancellationToken).ConfigureAwait(false);
            var approvedReview = await router.GetLatestReviewAsync(
                "db33-latest-review",
                selectedWorkspace,
                selectedCollection,
                duplicate.FeedbackId,
                cancellationToken).ConfigureAwait(false);
            var candidates = await router.QueryFeatureCandidatesAsync(
                "db33-query-candidates",
                selectedWorkspace,
                selectedCollection,
                new LearningFeatureCandidateQuery { Limit = int.MaxValue },
                cancellationToken).ConfigureAwait(false);
            var candidateExport = await router.ExportFeatureCandidatesJsonLinesAsync(
                "db33-export-candidates",
                selectedWorkspace,
                selectedCollection,
                new LearningFeatureCandidateQuery { Limit = int.MaxValue },
                cancellationToken).ConfigureAwait(false);
            var feedbackExport = await router.ExportFeedbackJsonLinesAsync("db33-export-feedback", query, cancellationToken).ConfigureAwait(false);

            var outsideFeedback = CreateFeedback(
                $"{canaryPrefix}-outside-feedback",
                outsideWorkspace,
                outsideCollection,
                ShadowCapabilityIds.RouterIntentClassifier,
                LearningFeedbackTargetType.RouterPrediction,
                LearningFeedbackKinds.WrongIntent,
                now.AddMinutes(1),
                metadataOnly: true,
                reason: "selected normal canary non-selected scope check");
            outsideFeedback.Metadata["excludedFromTraining"] = "true";
            await router.UpsertFeedbackAsync("db33-outside-upsert", outsideFeedback, cancellationToken).ConfigureAwait(false);
            var outsidePostgresRows = await postgresFeedbackStore.QueryAsync(BuildScopeQuery(outsideWorkspace, outsideCollection), cancellationToken)
                .ConfigureAwait(false);
            var outsideFileRows = await fileFeedbackStore.QueryAsync(BuildScopeQuery(outsideWorkspace, outsideCollection), cancellationToken)
                .ConfigureAwait(false);
            var scopeLeakCount = outsidePostgresRows.Count;
            var nonSelectedRemainsFileSystem = outsideFileRows.Count == 1 && scopeLeakCount == 0;

            var mismatches = new List<string>();
            mismatches.AddRange(await CompareStoresAsync(
                fileFeedbackStore,
                fileReviewStore,
                fileCandidateStore,
                postgresFeedbackStore,
                postgresReviewStore,
                postgresCandidateStore,
                selectedWorkspace,
                selectedCollection,
                cancellationToken).ConfigureAwait(false));
            AddMismatchIfFalse(mismatches, feedbackRows.Count >= events.Length, "SelectedNormalFeedbackQueryMissingRows");
            AddMismatchIfFalse(mismatches, summary.FeedbackCount >= events.Length, "SelectedNormalSummaryMissingRows");
            AddMismatchIfFalse(mismatches, reviewSummary.FeedbackCount >= reviews.Length, "SelectedNormalReviewSummaryMissingRows");
            AddMismatchIfFalse(mismatches, approvedReview is not null, "SelectedNormalLatestReviewMissing");
            AddMismatchIfFalse(mismatches, candidates.Count > 0, "SelectedNormalFeatureCandidateMissing");
            AddMismatchIfFalse(mismatches, candidates.All(static item =>
                string.Equals(item.TrainingUse, "smoke_test_only", StringComparison.OrdinalIgnoreCase)
                && item.Metadata.TryGetValue("excludedFromTraining", out var excluded)
                && string.Equals(excluded, "true", StringComparison.OrdinalIgnoreCase)), "SelectedNormalCanaryCandidateTrainingLeak");
            AddMismatchIfFalse(mismatches, !string.IsNullOrWhiteSpace(candidateExport), "SelectedNormalCandidateExportMissing");
            AddMismatchIfFalse(mismatches, !string.IsNullOrWhiteSpace(feedbackExport), "SelectedNormalFeedbackExportMissing");
            AddMismatchIfFalse(mismatches, nonSelectedRemainsFileSystem, "NonSelectedScopeLeak");

            var traceMismatchCount = traces.Count(static trace => trace.MismatchDetected);
            var postgresFailures = traces.Count(static trace => !string.IsNullOrWhiteSpace(trace.PostgresError));
            var mismatchCount = traceMismatchCount + mismatches.Count;
            var exportParity = traces.Any(static trace =>
                string.Equals(trace.OperationKind, "FeatureCandidateExportProjection", StringComparison.OrdinalIgnoreCase)
                && !trace.MismatchDetected);
            var summaryParity = traces.Any(static trace =>
                string.Equals(trace.OperationKind, "FeedbackSummary", StringComparison.OrdinalIgnoreCase)
                && !trace.MismatchDetected);
            var reviewSummaryParity = traces.Any(static trace =>
                string.Equals(trace.OperationKind, "ReviewSummary", StringComparison.OrdinalIgnoreCase)
                && !trace.MismatchDetected);
            var featureCandidateParity = traces.Any(static trace =>
                string.Equals(trace.OperationKind, "FeatureCandidateQuery", StringComparison.OrdinalIgnoreCase)
                && !trace.MismatchDetected);
            var cleanupPerformed = false;
            if (canaryOptions.CleanupMode != LearningFeedbackSelectedNormalScopeCleanupMode.None)
            {
                await CleanupCanaryPrefixAsync(
                    postgresFeedbackStore,
                    postgresReviewStore,
                    postgresCandidateStore,
                    canaryPrefix,
                    cancellationToken).ConfigureAwait(false);
                cleanupPerformed = true;
            }

            return BuildSelectedNormalScopeReport(
                true,
                canaryOptions,
                traces,
                exportParity,
                summaryParity,
                reviewSummaryParity,
                featureCandidateParity,
                cleanupPerformed,
                mismatches,
                blockedReasons: [],
                diagnostics,
                scopeLeakCount,
                recommendation: BuildSelectedNormalScopeRecommendation(
                    blockedReasons: [],
                    mismatchCount,
                    postgresFailures,
                    scopeLeakCount,
                    traceCountAvailable: traces.Count > 0));
        }
        finally
        {
            if (Directory.Exists(fileRoot))
            {
                Directory.Delete(fileRoot, recursive: true);
            }
        }
    }

    public async Task<LearningFeedbackLimitedScopeObservationReport> RunLimitedScopeObservationAsync(
        PostgresOptions options,
        LearningFeedbackLimitedScopeObservationOptions observationOptions,
        bool storageReady,
        bool readinessGatePassed,
        bool dualWriteSmokePassed,
        bool shadowReadSmokePassed,
        bool providerQualityReady,
        bool scopedServiceModeGatePassed,
        bool selectedNormalScopeCanaryPassed,
        bool p15GatePassed,
        List<LearningFeedbackProviderSwitchTrace> traces,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observationOptions);
        ArgumentNullException.ThrowIfNull(traces);
        var blocked = new List<string>();
        var diagnostics = new List<string>();
        AddReasonIfFalse(blocked, selectedNormalScopeCanaryPassed, "SelectedNormalScopeCanaryNotPassed");
        if (!observationOptions.Enabled)
        {
            diagnostics.Add("LimitedScopeObservationDisabledByDefault");
        }

        if (blocked.Count > 0)
        {
            return BuildLimitedScopeObservationReport(
                gatePassed: false,
                observationOptions,
                selectedReport: null,
                traces,
                trainableCandidateLeakCount: 0,
                smokeCandidateExcludedCount: 0,
                blocked,
                diagnostics,
                recommendation: BuildLimitedScopeObservationRecommendation(blocked, 0, 0, 0, 0, false));
        }

        var selectedReport = await RunSelectedNormalScopeCanaryAsync(
            options,
            new LearningFeedbackSelectedNormalScopeOptions
            {
                Enabled = true,
                WorkspaceId = observationOptions.WorkspaceId,
                CollectionId = observationOptions.CollectionId,
                Mode = observationOptions.Mode,
                FallbackToFileSystem = observationOptions.FallbackToFileSystem,
                ContinueComparisonTrace = observationOptions.ContinueComparisonTrace,
                FailClosedOnMismatch = observationOptions.FailClosedOnMismatch,
                RequireScopedServiceModeGate = observationOptions.RequireSelectedNormalScopeCanaryPassed,
                MaxOperations = observationOptions.MaxOperations,
                CleanupMode = observationOptions.CleanupMode
            },
            storageReady,
            readinessGatePassed,
            dualWriteSmokePassed,
            shadowReadSmokePassed,
            providerQualityReady,
            scopedServiceModeGatePassed,
            p15GatePassed,
            traces,
            cancellationToken).ConfigureAwait(false);

        var trainableCandidateLeakCount = selectedReport.Mismatches.Count(static item =>
            item.Contains("TrainingLeak", StringComparison.OrdinalIgnoreCase));
        var smokeCandidateExcludedCount = selectedReport.FeatureCandidateParityPassed && trainableCandidateLeakCount == 0 ? 1 : 0;
        var mergedBlocked = selectedReport.BlockedReasons.Count == 0
            ? blocked
            : [.. blocked, .. selectedReport.BlockedReasons];
        var recommendation = BuildLimitedScopeObservationRecommendation(
            mergedBlocked,
            selectedReport.MismatchCount,
            selectedReport.PostgresFailureCount,
            selectedReport.ScopeLeakCount,
            trainableCandidateLeakCount,
            selectedReport.OperationCount > 0);

        return BuildLimitedScopeObservationReport(
            selectedReport.GatePassed,
            observationOptions,
            selectedReport,
            traces,
            trainableCandidateLeakCount,
            smokeCandidateExcludedCount,
            mergedBlocked,
            [.. diagnostics, .. selectedReport.Diagnostics],
            recommendation);
    }

    public LearningFeedbackLimitedScopeQualityReport BuildLimitedScopeQualityReport(
        LearningFeedbackLimitedScopeObservationReport? observation)
    {
        var blocked = new List<string>();
        if (observation is null)
        {
            blocked.Add("LimitedScopeObservationMissing");
            return new LearningFeedbackLimitedScopeQualityReport
            {
                Passed = false,
                BlockedReasons = blocked,
                Diagnostics = ["RunPostgresLearningFeedbackLimitedScopeObservationFirst"],
                Recommendation = "GateNotPassed"
            };
        }

        AddReasonIfFalse(blocked, observation.GatePassed, "ObservationGateNotPassed");
        AddReasonIfFalse(blocked, observation.MismatchCount == 0, "MismatchCountNonZero");
        AddReasonIfFalse(blocked, observation.PostgresFailureCount == 0, "PostgresFailureCountNonZero");
        AddReasonIfFalse(blocked, observation.ScopeLeakCount == 0, "ScopeLeakCountNonZero");
        AddReasonIfFalse(blocked, observation.TrainableCandidateLeakCount == 0, "TrainableCandidateLeakCountNonZero");
        AddReasonIfFalse(blocked, observation.ExportProjectionParityPassed, "ExportProjectionParityFailed");
        AddReasonIfFalse(blocked, observation.SummaryParityPassed, "SummaryParityFailed");
        AddReasonIfFalse(blocked, observation.ReviewSummaryParityPassed, "ReviewSummaryParityFailed");
        AddReasonIfFalse(blocked, observation.FeatureCandidateParityPassed, "FeatureCandidateParityFailed");

        var passed = blocked.Count == 0;
        return new LearningFeedbackLimitedScopeQualityReport
        {
            Passed = passed,
            OperationCount = observation.OperationCount,
            MismatchCount = observation.MismatchCount,
            PostgresFailureCount = observation.PostgresFailureCount,
            ScopeLeakCount = observation.ScopeLeakCount,
            ErrorRate = observation.ErrorRate,
            FallbackRate = observation.FallbackRate,
            ExportProjectionParityPassed = observation.ExportProjectionParityPassed,
            SummaryParityPassed = observation.SummaryParityPassed,
            ReviewSummaryParityPassed = observation.ReviewSummaryParityPassed,
            FeatureCandidateParityPassed = observation.FeatureCandidateParityPassed,
            TrainableCandidateLeakCount = observation.TrainableCandidateLeakCount,
            SmokeCandidateExcludedCount = observation.SmokeCandidateExcludedCount,
            BlockedReasons = blocked,
            Diagnostics = passed ? ["LimitedScopeObservationQualityPassed"] : observation.Diagnostics,
            Recommendation = passed ? "ReadyForFreezeGate" : BuildLimitedScopeQualityRecommendation(blocked)
        };
    }

    public LearningFeedbackPostgresFreezeGateReport BuildFreezeGateReport(
        LearningFeedbackPostgresReadinessGateReport? readiness,
        LearningFeedbackProviderQualityReport? providerQuality,
        LearningFeedbackScopedServiceModeGateReport? scopedGate,
        LearningFeedbackSelectedNormalScopeCanaryReport? selectedCanary,
        LearningFeedbackLimitedScopeQualityReport? limitedQuality,
        bool p15GatePassed,
        bool fallbackRequired = true,
        bool comparisonTraceRequired = true,
        bool globalDefaultOnForbidden = true)
    {
        var blocked = new List<string>();
        var readinessPassed = readiness?.GatePassed == true;
        var qualityReady = string.Equals(providerQuality?.Recommendation, "ReadyForScopedServiceMode", StringComparison.OrdinalIgnoreCase);
        var scopedPassed = scopedGate?.Passed == true;
        var selectedPassed = string.Equals(selectedCanary?.Recommendation, "ReadyForLimitedFeedbackScope", StringComparison.OrdinalIgnoreCase);
        var limitedPassed = limitedQuality?.Passed == true
                            && string.Equals(limitedQuality.Recommendation, "ReadyForFreezeGate", StringComparison.OrdinalIgnoreCase);
        AddReasonIfFalse(blocked, readinessPassed, "ReadinessGateNotPassed");
        AddReasonIfFalse(blocked, qualityReady, "ProviderQualityNotReady");
        AddReasonIfFalse(blocked, scopedPassed, "ScopedServiceModeGateNotPassed");
        AddReasonIfFalse(blocked, selectedPassed, "SelectedNormalScopeCanaryNotPassed");
        AddReasonIfFalse(blocked, limitedPassed, "LimitedObservationQualityNotPassed");
        AddReasonIfFalse(blocked, (limitedQuality?.MismatchCount ?? 0) == 0, "MismatchCountNonZero");
        AddReasonIfFalse(blocked, (limitedQuality?.PostgresFailureCount ?? 0) == 0, "PostgresFailureCountNonZero");
        AddReasonIfFalse(blocked, (limitedQuality?.ScopeLeakCount ?? 0) == 0, "ScopeLeakCountNonZero");
        AddReasonIfFalse(blocked, (limitedQuality?.TrainableCandidateLeakCount ?? 0) == 0, "TrainableCandidateLeakCountNonZero");
        AddReasonIfFalse(blocked, limitedQuality?.ExportProjectionParityPassed == true, "ExportProjectionParityFailed");
        AddReasonIfFalse(blocked, limitedQuality?.SummaryParityPassed == true, "SummaryParityFailed");
        AddReasonIfFalse(blocked, limitedQuality?.ReviewSummaryParityPassed == true, "ReviewSummaryParityFailed");
        AddReasonIfFalse(blocked, limitedQuality?.FeatureCandidateParityPassed == true, "FeatureCandidateParityFailed");
        AddReasonIfFalse(blocked, fallbackRequired, "FallbackRequiredMissing");
        AddReasonIfFalse(blocked, comparisonTraceRequired, "ComparisonTraceRequiredMissing");
        AddReasonIfFalse(blocked, globalDefaultOnForbidden, "GlobalDefaultOnForbiddenMissing");
        AddReasonIfFalse(blocked, p15GatePassed, "P15GateNotPassed");

        var passed = blocked.Count == 0;
        return new LearningFeedbackPostgresFreezeGateReport
        {
            Passed = passed,
            LearningFeedbackPostgres = passed ? "ReadyForScopedServiceMode" : "NotReady",
            ReadinessGatePassed = readinessPassed,
            ProviderQualityReady = qualityReady,
            ScopedServiceModeGatePassed = scopedPassed,
            SelectedNormalScopeCanaryPassed = selectedPassed,
            LimitedObservationQualityPassed = limitedPassed,
            MismatchCount = limitedQuality?.MismatchCount ?? 0,
            PostgresFailureCount = limitedQuality?.PostgresFailureCount ?? 0,
            ScopeLeakCount = limitedQuality?.ScopeLeakCount ?? 0,
            TrainableCandidateLeakCount = limitedQuality?.TrainableCandidateLeakCount ?? 0,
            ExportProjectionParityPassed = limitedQuality?.ExportProjectionParityPassed == true,
            SummaryParityPassed = limitedQuality?.SummaryParityPassed == true,
            ReviewSummaryParityPassed = limitedQuality?.ReviewSummaryParityPassed == true,
            FeatureCandidateParityPassed = limitedQuality?.FeatureCandidateParityPassed == true,
            FallbackRequired = fallbackRequired,
            ComparisonTraceRequired = comparisonTraceRequired,
            GlobalDefaultOnForbidden = globalDefaultOnForbidden,
            P15GatePassed = p15GatePassed,
            BlockedReasons = blocked,
            Diagnostics = passed
                ? ["DefaultProviderFileSystem", "AllowlistedGuardedPostgresPrimaryOnly", "NoTrainingNoRuntimePolicyChange"]
                : ["FreezeGateBlocked"],
            Recommendation = passed ? "ReadyForScopedServiceMode" : BuildFreezeGateRecommendation(blocked)
        };
    }

    private static (string Root, FileLearningFeedbackStore Feedback, FileLearningFeedbackReviewStore Review, FileLearningFeatureCandidateStore Candidate)
        CreateFileStores(string prefix)
    {
        var fileRoot = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
        var fileOptions = new FileStorageOptions { RootPath = fileRoot };
        var fileResolver = new FilePathResolver(fileOptions);
        var fileSerializer = new FileFormatSerializer();
        return (
            fileRoot,
            new FileLearningFeedbackStore(fileResolver, fileSerializer),
            new FileLearningFeedbackReviewStore(fileResolver, fileSerializer),
            new FileLearningFeatureCandidateStore(fileResolver, fileSerializer));
    }

    private static async Task<bool> RunDualWriteIntoStoresAsync(
        ILearningFeedbackStore fileFeedbackStore,
        ILearningFeedbackReviewStore fileReviewStore,
        ILearningFeatureCandidateStore fileCandidateStore,
        ILearningFeedbackStore postgresFeedbackStore,
        ILearningFeedbackReviewStore postgresReviewStore,
        ILearningFeatureCandidateStore postgresCandidateStore,
        string workspaceId,
        string collectionId,
        List<LearningFeedbackDualWriteTrace> traces,
        CancellationToken cancellationToken)
    {
        var coordinator = new LearningFeedbackDualWriteCoordinator(
            fileFeedbackStore,
            fileReviewStore,
            fileCandidateStore,
            postgresFeedbackStore,
            postgresReviewStore,
            postgresCandidateStore,
            new LearningFeedbackDualWriteOptions { Enabled = true, WritePostgres = true, TraceEnabled = true },
            (trace, _) =>
            {
                traces.Add(trace);
                return Task.CompletedTask;
            });
        var now = DateTimeOffset.UtcNow;
        var events = CreateSmokeFeedbackEvents(workspaceId, collectionId, now);
        var duplicate = WithUpdatedReason(events[0], "duplicate stable upsert");
        foreach (var feedback in events)
        {
            await coordinator.UpsertFeedbackAsync(feedback, cancellationToken).ConfigureAwait(false);
        }

        await coordinator.UpsertFeedbackAsync(duplicate, cancellationToken).ConfigureAwait(false);
        foreach (var review in CreateSmokeReviews([duplicate, .. events.Skip(1)], now))
        {
            await coordinator.UpsertReviewAsync(review, cancellationToken).ConfigureAwait(false);
        }

        var query = BuildScopeQuery(workspaceId, collectionId);
        var candidateReport = await new LearningFeedbackFeatureCandidateBuilder(fileFeedbackStore, fileReviewStore)
            .BuildAsync(query, cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var candidate in candidateReport.Candidates)
        {
            await coordinator.UpsertCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private static LearningFeedbackEvent[] CreateSmokeFeedbackEvents(
        string workspaceId,
        string collectionId,
        DateTimeOffset now)
        =>
        [
            CreateFeedback(
                "db31-feedback-a",
                workspaceId,
                collectionId,
                ShadowCapabilityIds.RouterIntentClassifier,
                LearningFeedbackTargetType.RouterPrediction,
                LearningFeedbackKinds.WrongIntent,
                now,
                metadataOnly: true),
            CreateFeedback(
                "db31-feedback-b",
                workspaceId,
                collectionId,
                ShadowCapabilityIds.VectorRetrieval,
                LearningFeedbackTargetType.VectorCandidate,
                LearningFeedbackKinds.MissingContext,
                now.AddSeconds(1),
                metadataOnly: true),
            CreateFeedback(
                "db31-feedback-c",
                workspaceId,
                collectionId,
                ShadowCapabilityIds.CandidateReranker,
                LearningFeedbackTargetType.RankerCandidate,
                LearningFeedbackKinds.RankingWrong,
                now.AddSeconds(2),
                metadataOnly: true),
            CreateFeedback(
                "db31-feedback-d",
                workspaceId,
                collectionId,
                ShadowCapabilityIds.GraphExpansion,
                LearningFeedbackTargetType.GraphExpansionCandidate,
                LearningFeedbackKinds.NeedsMoreEvidence,
                now.AddSeconds(3),
                metadataOnly: true)
        ];

    private static LearningFeedbackReviewRecord[] CreateSmokeReviews(
        IReadOnlyList<LearningFeedbackEvent> feedback,
        DateTimeOffset now)
        =>
        [
            CreateReview(feedback[0], FeedbackReviewStatus.ApprovedForDataset, "router_intent_correction", now.AddSeconds(4)),
            CreateReview(feedback[1], FeedbackReviewStatus.Rejected, "vector_recall_candidate", now.AddSeconds(5)),
            CreateReview(feedback[2], FeedbackReviewStatus.NeedsRedaction, "ranking_pair_candidate", now.AddSeconds(6)),
            CreateReview(feedback[3], FeedbackReviewStatus.NeedsMoreEvidence, "graph_routing_candidate", now.AddSeconds(7))
        ];

    private static LearningFeedbackEvent WithUpdatedReason(LearningFeedbackEvent source, string reason)
        => new()
        {
            FeedbackId = source.FeedbackId,
            WorkspaceId = source.WorkspaceId,
            CollectionId = source.CollectionId,
            Source = source.Source,
            SourceOperationId = source.SourceOperationId,
            CapabilityId = source.CapabilityId,
            TargetId = source.TargetId,
            TargetType = source.TargetType,
            FeedbackKind = source.FeedbackKind,
            FeedbackValue = source.FeedbackValue,
            Reason = reason,
            UserCorrection = source.UserCorrection,
            RedactionMode = source.RedactionMode,
            MetadataOnly = source.MetadataOnly,
            TrainingUse = source.TrainingUse,
            Confidence = source.Confidence,
            CreatedAt = source.CreatedAt.AddSeconds(10),
            Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.OrdinalIgnoreCase)
        };

    private static LearningFeedbackEventQuery BuildScopeQuery(string workspaceId, string collectionId)
        => new()
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Limit = int.MaxValue
        };

    private static LearningFeedbackEventQuery WithCapability(LearningFeedbackEventQuery query, string capabilityId)
        => new()
        {
            WorkspaceId = query.WorkspaceId,
            CollectionId = query.CollectionId,
            Source = query.Source,
            SourceOperationId = query.SourceOperationId,
            CapabilityId = capabilityId,
            TargetId = query.TargetId,
            TargetType = query.TargetType,
            FeedbackKind = query.FeedbackKind,
            Limit = query.Limit,
            Offset = query.Offset
        };

    private static async Task<IReadOnlyList<LearningFeedbackEvent>> QueryFeedbackOrderedAsync(
        ILearningFeedbackStore store,
        LearningFeedbackEventQuery query,
        CancellationToken cancellationToken)
        => [.. (await store.QueryAsync(query, cancellationToken).ConfigureAwait(false))
            .OrderBy(static item => item.FeedbackId, StringComparer.OrdinalIgnoreCase)];

    private static async Task<string[]> BuildFeedbackProjectionAsync(
        ILearningFeedbackStore store,
        LearningFeedbackEventQuery query,
        CancellationToken cancellationToken)
    {
        var rows = await QueryFeedbackOrderedAsync(store, query, cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(static item =>
            string.Join('|',
                item.FeedbackId,
                item.CapabilityId,
                item.TargetType,
                item.FeedbackKind,
                item.RedactionMode,
                item.MetadataOnly,
                item.TrainingUse,
                item.Reason))];
    }

    private static async Task<IReadOnlyList<LearningFeedbackReviewRecord>> QueryReviewsOrderedAsync(
        ILearningFeedbackReviewStore store,
        LearningFeedbackReviewQuery query,
        CancellationToken cancellationToken)
        => [.. (await store.QueryAsync(query, cancellationToken).ConfigureAwait(false))
            .OrderBy(static item => item.FeedbackId, StringComparer.OrdinalIgnoreCase)];

    private static async Task<IReadOnlyList<LearningFeedbackReviewRecord>> QueryReviewsForFeedbackAsync(
        ILearningFeedbackReviewStore store,
        IReadOnlyList<string> feedbackIds,
        CancellationToken cancellationToken)
    {
        var rows = new List<LearningFeedbackReviewRecord>();
        foreach (var feedbackId in feedbackIds)
        {
            rows.AddRange(await store.QueryAsync(
                new LearningFeedbackReviewQuery { FeedbackId = feedbackId, Limit = int.MaxValue },
                cancellationToken).ConfigureAwait(false));
        }

        return [.. rows.OrderBy(static item => item.FeedbackId, StringComparer.OrdinalIgnoreCase)];
    }

    private static async Task<string[]> BuildReviewProjectionAsync(
        ILearningFeedbackReviewStore store,
        IReadOnlyList<string> feedbackIds,
        CancellationToken cancellationToken)
    {
        var rows = await QueryReviewsForFeedbackAsync(store, feedbackIds, cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(static item =>
            string.Join('|',
                item.FeedbackId,
                item.ReviewStatus,
                item.TrainingUse,
                item.RedactionChecked,
                item.ApprovedCapability,
                item.ApprovedLabelKind))];
    }

    private static async Task<IReadOnlyList<FeedbackFeatureCandidate>> QueryCandidatesOrderedAsync(
        ILearningFeatureCandidateStore store,
        LearningFeatureCandidateQuery query,
        CancellationToken cancellationToken)
        => [.. (await store.QueryAsync(query, cancellationToken).ConfigureAwait(false))
            .OrderBy(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase)];

    private static async Task<IReadOnlyList<FeedbackFeatureCandidate>> QueryCandidatesInScopeAsync(
        ILearningFeatureCandidateStore store,
        LearningFeatureCandidateQuery query,
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken)
        => [.. (await store.QueryAsync(query, cancellationToken).ConfigureAwait(false))
            .Where(item => HasScopeMetadata(item.Metadata, workspaceId, collectionId))
            .OrderBy(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase)];

    private static async Task<string[]> BuildCandidateProjectionAsync(
        ILearningFeatureCandidateStore store,
        LearningFeatureCandidateQuery query,
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var rows = await QueryCandidatesInScopeAsync(store, query, workspaceId, collectionId, cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(static item =>
            string.Join('|',
                item.CandidateId,
                item.SourceFeedbackId,
                item.CapabilityId,
                item.TargetType,
                item.LabelKind,
                item.PositiveLabel,
                item.NegativeLabel,
                item.TrainingUse,
                item.RedactionStatus,
                item.ReviewStatus,
                item.Metadata.TryGetValue("metadataOnly", out var metadataOnly) ? metadataOnly : string.Empty))];
    }

    private static async Task<IReadOnlyDictionary<string, int>> BuildSummaryProjectionAsync(
        ILearningFeedbackStore store,
        LearningFeedbackEventQuery query,
        CancellationToken cancellationToken)
    {
        var feedback = await store.QueryAsync(query, cancellationToken).ConfigureAwait(false);
        return feedback
            .GroupBy(static item => item.CapabilityId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static item => item.Key, static item => item.Count(), StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<string[]> BuildExportProjectionAsync(
        ILearningFeatureCandidateStore store,
        LearningFeatureCandidateQuery query,
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var candidates = await QueryCandidatesInScopeAsync(store, query, workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
        return [.. candidates.Select(static item => $"{item.CandidateId}|{item.CapabilityId}|{item.LabelKind}|{item.TrainingUse}")];
    }

    private static async Task<IReadOnlyList<string>> CompareStoresAsync(
        ILearningFeedbackStore fileFeedbackStore,
        ILearningFeedbackReviewStore fileReviewStore,
        ILearningFeatureCandidateStore fileCandidateStore,
        ILearningFeedbackStore postgresFeedbackStore,
        ILearningFeedbackReviewStore postgresReviewStore,
        ILearningFeatureCandidateStore postgresCandidateStore,
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var mismatches = new List<string>();
        var query = BuildScopeQuery(workspaceId, collectionId);
        var fileFeedback = await QueryFeedbackOrderedAsync(fileFeedbackStore, query, cancellationToken).ConfigureAwait(false);
        var pgFeedback = await QueryFeedbackOrderedAsync(postgresFeedbackStore, query, cancellationToken).ConfigureAwait(false);
        AddMismatchIfFalse(mismatches, SameIds(fileFeedback.Select(static item => item.FeedbackId), pgFeedback.Select(static item => item.FeedbackId)), "FeedbackListMismatch");

        var feedbackIds = fileFeedback.Select(static item => item.FeedbackId).ToArray();
        var fileReviews = await QueryReviewsForFeedbackAsync(fileReviewStore, feedbackIds, cancellationToken).ConfigureAwait(false);
        var pgReviews = await QueryReviewsForFeedbackAsync(postgresReviewStore, feedbackIds, cancellationToken).ConfigureAwait(false);
        AddMismatchIfFalse(mismatches, SameIds(fileReviews.Select(static item => item.FeedbackId), pgReviews.Select(static item => item.FeedbackId)), "ReviewListMismatch");

        var fileCandidates = await QueryCandidatesInScopeAsync(fileCandidateStore, new LearningFeatureCandidateQuery { Limit = int.MaxValue }, workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
        var pgCandidates = await QueryCandidatesInScopeAsync(postgresCandidateStore, new LearningFeatureCandidateQuery { Limit = int.MaxValue }, workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
        AddMismatchIfFalse(mismatches, SameIds(fileCandidates.Select(static item => item.CandidateId), pgCandidates.Select(static item => item.CandidateId)), "FeatureCandidateListMismatch");
        return mismatches;
    }

    private static bool HasScopeMetadata(
        IReadOnlyDictionary<string, string> metadata,
        string workspaceId,
        string collectionId)
        => metadata.TryGetValue("workspaceId", out var itemWorkspace)
           && metadata.TryGetValue("collectionId", out var itemCollection)
           && string.Equals(itemWorkspace, workspaceId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(itemCollection, collectionId, StringComparison.OrdinalIgnoreCase);

    private static string BuildQualityRecommendation(int mismatchCount, int postgresFailureCount, int traceCount)
    {
        if (traceCount == 0)
        {
            return "NeedsMoreTraces";
        }

        if (mismatchCount > 0)
        {
            return "BlockedByMismatch";
        }

        if (postgresFailureCount > 0)
        {
            return "BlockedByPostgresFailure";
        }

        return "ReadyForScopedServiceMode";
    }

    private static string BuildScopedServiceModeRecommendation(
        bool providerQualityReady,
        int mismatchCount,
        int postgresFailureCount,
        bool nonAllowlistedRemainsFileSystem,
        int traceCount)
    {
        if (!providerQualityReady || traceCount == 0)
        {
            return "GateNotPassed";
        }

        if (mismatchCount > 0)
        {
            return "BlockedByMismatch";
        }

        if (postgresFailureCount > 0)
        {
            return "BlockedByPostgresFailure";
        }

        if (!nonAllowlistedRemainsFileSystem)
        {
            return "BlockedByScopeLeak";
        }

        return "ReadyForSelectedFeedbackScope";
    }

    private static string BuildScopedGateRecommendation(IReadOnlyList<string> blockedReasons)
    {
        if (blockedReasons.Any(static item => item.Contains("Mismatch", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByMismatch";
        }

        if (blockedReasons.Any(static item => item.Contains("PostgresFailure", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByPostgresFailure";
        }

        if (blockedReasons.Any(static item => item.Contains("ScopeLeak", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByScopeLeak";
        }

        if (blockedReasons.Count > 0)
        {
            return "GateNotPassed";
        }

        return "NeedsMoreScopedSmoke";
    }

    private static LearningFeedbackSelectedNormalScopeCanaryReport BuildSelectedNormalScopeReport(
        bool gatePassed,
        LearningFeedbackSelectedNormalScopeOptions options,
        IReadOnlyList<LearningFeedbackProviderSwitchTrace> traces,
        bool exportProjectionParityPassed,
        bool summaryParityPassed,
        bool reviewSummaryParityPassed,
        bool featureCandidateParityPassed,
        bool cleanupPerformed,
        IReadOnlyList<string> mismatches,
        IReadOnlyList<string> blockedReasons,
        IReadOnlyList<string> diagnostics,
        int scopeLeakCount,
        string recommendation)
    {
        var primaryReads = traces.Count(static trace =>
            string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)
            && IsLearningFeedbackReadOperation(trace.OperationKind));
        var primaryWrites = traces.Count(static trace =>
            string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)
            && !IsLearningFeedbackReadOperation(trace.OperationKind));
        var traceScopeLeaks = traces.Count(trace =>
            string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(trace.WorkspaceId, options.WorkspaceId, StringComparison.OrdinalIgnoreCase));

        return new LearningFeedbackSelectedNormalScopeCanaryReport
        {
            GatePassed = gatePassed && blockedReasons.Count == 0,
            WorkspaceId = options.WorkspaceId,
            CollectionId = options.CollectionId,
            ProviderMode = options.Mode.ToString(),
            OperationCount = traces.Count,
            PostgresPrimaryReadCount = primaryReads,
            PostgresPrimaryWriteCount = primaryWrites,
            FileSystemFallbackCount = traces.Count(static trace => trace.FallbackUsed),
            ComparisonTraceCount = traces.Count,
            MismatchCount = traces.Count(static trace => trace.MismatchDetected) + mismatches.Count,
            PostgresFailureCount = traces.Count(static trace => !string.IsNullOrWhiteSpace(trace.PostgresError)),
            ScopeLeakCount = scopeLeakCount + traceScopeLeaks,
            ExportProjectionParityPassed = exportProjectionParityPassed,
            SummaryParityPassed = summaryParityPassed,
            ReviewSummaryParityPassed = reviewSummaryParityPassed,
            FeatureCandidateParityPassed = featureCandidateParityPassed,
            CleanupPerformed = cleanupPerformed,
            Mismatches = mismatches,
            BlockedReasons = blockedReasons,
            Diagnostics = diagnostics.Count == 0
                ? ["SelectedNormalScopeCanary", "NoTrainingNoRuntimeBehaviorChange"]
                : diagnostics,
            Recommendation = recommendation
        };
    }

    private static string BuildSelectedNormalScopeRecommendation(
        IReadOnlyList<string> blockedReasons,
        int mismatchCount,
        int postgresFailureCount,
        int scopeLeakCount,
        bool traceCountAvailable)
    {
        if (blockedReasons.Count > 0 || !traceCountAvailable)
        {
            return "GateNotPassed";
        }

        if (mismatchCount > 0)
        {
            return "BlockedByMismatch";
        }

        if (postgresFailureCount > 0)
        {
            return "BlockedByPostgresFailure";
        }

        if (scopeLeakCount > 0)
        {
            return "BlockedByScopeLeak";
        }

        return "ReadyForLimitedFeedbackScope";
    }

    private static LearningFeedbackLimitedScopeObservationReport BuildLimitedScopeObservationReport(
        bool gatePassed,
        LearningFeedbackLimitedScopeObservationOptions options,
        LearningFeedbackSelectedNormalScopeCanaryReport? selectedReport,
        IReadOnlyList<LearningFeedbackProviderSwitchTrace> traces,
        int trainableCandidateLeakCount,
        int smokeCandidateExcludedCount,
        IReadOnlyList<string> blockedReasons,
        IReadOnlyList<string> diagnostics,
        string recommendation)
    {
        var operationCount = selectedReport?.OperationCount ?? traces.Count;
        var postgresFailureCount = selectedReport?.PostgresFailureCount
                                   ?? traces.Count(static trace => !string.IsNullOrWhiteSpace(trace.PostgresError));
        var mismatchCount = selectedReport?.MismatchCount
                            ?? traces.Count(static trace => trace.MismatchDetected);
        var fallbackCount = selectedReport?.FileSystemFallbackCount
                            ?? traces.Count(static trace => trace.FallbackUsed);
        return new LearningFeedbackLimitedScopeObservationReport
        {
            GatePassed = gatePassed && blockedReasons.Count == 0,
            WorkspaceId = options.WorkspaceId,
            CollectionId = options.CollectionId,
            ObservationWindowMinutes = options.ObservationWindowMinutes,
            ProviderMode = options.Mode.ToString(),
            OperationCount = operationCount,
            PostgresPrimaryReadCount = selectedReport?.PostgresPrimaryReadCount ?? traces.Count(static trace =>
                string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)
                && IsLearningFeedbackReadOperation(trace.OperationKind)),
            PostgresPrimaryWriteCount = selectedReport?.PostgresPrimaryWriteCount ?? traces.Count(static trace =>
                string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)
                && !IsLearningFeedbackReadOperation(trace.OperationKind)),
            FileSystemFallbackCount = fallbackCount,
            ComparisonTraceCount = selectedReport?.ComparisonTraceCount ?? traces.Count,
            MismatchCount = mismatchCount,
            PostgresFailureCount = postgresFailureCount,
            ScopeLeakCount = selectedReport?.ScopeLeakCount ?? 0,
            ErrorRate = operationCount == 0 ? 0 : (double)(mismatchCount + postgresFailureCount) / operationCount,
            FallbackRate = operationCount == 0 ? 0 : (double)fallbackCount / operationCount,
            ExportProjectionParityPassed = selectedReport?.ExportProjectionParityPassed == true,
            SummaryParityPassed = selectedReport?.SummaryParityPassed == true,
            ReviewSummaryParityPassed = selectedReport?.ReviewSummaryParityPassed == true,
            FeatureCandidateParityPassed = selectedReport?.FeatureCandidateParityPassed == true,
            TrainableCandidateLeakCount = trainableCandidateLeakCount,
            SmokeCandidateExcludedCount = smokeCandidateExcludedCount,
            CleanupPerformed = selectedReport?.CleanupPerformed == true,
            Mismatches = selectedReport?.Mismatches ?? Array.Empty<string>(),
            BlockedReasons = blockedReasons,
            Diagnostics = diagnostics.Count == 0
                ? ["LimitedScopeObservation", "NoTrainingNoRuntimeBehaviorChange"]
                : diagnostics,
            Recommendation = recommendation
        };
    }

    private static string BuildLimitedScopeObservationRecommendation(
        IReadOnlyList<string> blockedReasons,
        int mismatchCount,
        int postgresFailureCount,
        int scopeLeakCount,
        int trainableCandidateLeakCount,
        bool traceCountAvailable)
    {
        if (blockedReasons.Count > 0 || !traceCountAvailable)
        {
            return "GateNotPassed";
        }

        if (mismatchCount > 0)
        {
            return "BlockedByMismatch";
        }

        if (postgresFailureCount > 0)
        {
            return "BlockedByPostgresFailure";
        }

        if (scopeLeakCount > 0)
        {
            return "BlockedByScopeLeak";
        }

        if (trainableCandidateLeakCount > 0)
        {
            return "BlockedByTrainableLeak";
        }

        return "ReadyForFreezeGate";
    }

    private static string BuildLimitedScopeQualityRecommendation(IReadOnlyList<string> blockedReasons)
    {
        if (blockedReasons.Any(static item => item.Contains("Mismatch", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByMismatch";
        }

        if (blockedReasons.Any(static item => item.Contains("PostgresFailure", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByPostgresFailure";
        }

        if (blockedReasons.Any(static item => item.Contains("ScopeLeak", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByScopeLeak";
        }

        if (blockedReasons.Any(static item => item.Contains("TrainableCandidateLeak", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByTrainableLeak";
        }

        return "GateNotPassed";
    }

    private static string BuildFreezeGateRecommendation(IReadOnlyList<string> blockedReasons)
    {
        if (blockedReasons.Any(static item => item.Contains("Mismatch", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByMismatch";
        }

        if (blockedReasons.Any(static item => item.Contains("PostgresFailure", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByPostgresFailure";
        }

        if (blockedReasons.Any(static item => item.Contains("ScopeLeak", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByScopeLeak";
        }

        if (blockedReasons.Any(static item => item.Contains("TrainableCandidateLeak", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByTrainableLeak";
        }

        return "GateNotPassed";
    }

    private static LearningFeedbackEvent[] CreateCanaryFeedbackEvents(
        string prefix,
        string workspaceId,
        string collectionId,
        DateTimeOffset now)
    {
        var events = new[]
        {
            CreateFeedback($"{prefix}-feedback-a", workspaceId, collectionId, ShadowCapabilityIds.RouterIntentClassifier, LearningFeedbackTargetType.RouterPrediction, LearningFeedbackKinds.WrongIntent, now, metadataOnly: true, reason: "selected normal canary approved metadata safe feedback"),
            CreateFeedback($"{prefix}-feedback-b", workspaceId, collectionId, ShadowCapabilityIds.VectorRetrieval, LearningFeedbackTargetType.VectorCandidate, LearningFeedbackKinds.MissingContext, now.AddSeconds(1), metadataOnly: true, reason: "selected normal canary rejected feedback"),
            CreateFeedback($"{prefix}-feedback-c", workspaceId, collectionId, ShadowCapabilityIds.CandidateReranker, LearningFeedbackTargetType.RankerCandidate, LearningFeedbackKinds.RankingWrong, now.AddSeconds(2), metadataOnly: true, reason: "selected normal canary needs redaction feedback"),
            CreateFeedback($"{prefix}-feedback-d", workspaceId, collectionId, ShadowCapabilityIds.GraphExpansion, LearningFeedbackTargetType.GraphExpansionCandidate, LearningFeedbackKinds.NeedsMoreEvidence, now.AddSeconds(3), metadataOnly: true, reason: "selected normal canary needs evidence feedback")
        };

        foreach (var feedback in events)
        {
            feedback.Metadata["canary"] = "true";
            feedback.Metadata["excludedFromTraining"] = "true";
        }

        return events;
    }

    private static LearningFeedbackReviewRecord[] CreateCanaryReviews(
        IReadOnlyList<LearningFeedbackEvent> feedback,
        DateTimeOffset now)
        =>
        [
            CreateCanaryReview(feedback[0], FeedbackReviewStatus.ApprovedForDataset, "router_intent_correction", now.AddSeconds(4)),
            CreateCanaryReview(feedback[1], FeedbackReviewStatus.Rejected, "vector_recall_candidate", now.AddSeconds(5)),
            CreateCanaryReview(feedback[2], FeedbackReviewStatus.NeedsRedaction, "ranking_pair_candidate", now.AddSeconds(6)),
            CreateCanaryReview(feedback[3], FeedbackReviewStatus.NeedsMoreEvidence, "graph_routing_candidate", now.AddSeconds(7))
        ];

    private static LearningFeedbackReviewRecord CreateCanaryReview(
        LearningFeedbackEvent feedback,
        FeedbackReviewStatus status,
        string labelKind,
        DateTimeOffset reviewedAt)
    {
        var review = CreateReview(feedback, status, labelKind, reviewedAt);
        var metadata = new Dictionary<string, string>(review.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["canary"] = "true",
            ["excludedFromTraining"] = "true"
        };

        return new LearningFeedbackReviewRecord
        {
            FeedbackId = review.FeedbackId,
            Reviewer = "db3-selected-normal-canary",
            ReviewStatus = review.ReviewStatus,
            ReviewReason = review.ReviewReason,
            ApprovedCapability = review.ApprovedCapability,
            ApprovedLabelKind = review.ApprovedLabelKind,
            RedactionChecked = review.RedactionChecked,
            TrainingUse = status == FeedbackReviewStatus.ApprovedForDataset ? "smoke_test_only" : "disabled_until_review",
            ReviewedAt = review.ReviewedAt,
            Metadata = metadata
        };
    }

    private static async Task CleanupCanaryPrefixAsync(
        PostgresLearningFeedbackStore feedbackStore,
        PostgresLearningFeedbackReviewStore reviewStore,
        PostgresLearningFeatureCandidateStore candidateStore,
        string feedbackIdPrefix,
        CancellationToken cancellationToken)
    {
        await candidateStore.DeleteBySourceFeedbackIdPrefixAsync(feedbackIdPrefix, cancellationToken).ConfigureAwait(false);
        await reviewStore.DeleteByFeedbackIdPrefixAsync(feedbackIdPrefix, cancellationToken).ConfigureAwait(false);
        await feedbackStore.DeleteByFeedbackIdPrefixAsync(feedbackIdPrefix, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsLearningFeedbackReadOperation(string operationKind)
        => operationKind.Contains("Query", StringComparison.OrdinalIgnoreCase)
           || operationKind.Contains("Summary", StringComparison.OrdinalIgnoreCase)
           || operationKind.Contains("Latest", StringComparison.OrdinalIgnoreCase)
           || operationKind.Contains("Export", StringComparison.OrdinalIgnoreCase);

    private static void AddReasonIfFalse(List<string> reasons, bool condition, string reason)
    {
        if (!condition)
        {
            reasons.Add(reason);
        }
    }

    private static bool SchemaAtLeast(string schemaVersion, int minimum)
    {
        var marker = "cc-schema-v";
        var index = schemaVersion.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index >= 0
               && int.TryParse(schemaVersion[(index + marker.Length)..], out var version)
               && version >= minimum;
    }

    private static async Task<T?> ReadReportAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    public static string BuildDiagnosticsMarkdown(PostgresLearningFeedbackDiagnosticsReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Learning Feedback Diagnostics");
        builder.AppendLine();
        builder.AppendLine($"- ProviderEnabled: `{report.ProviderEnabled}`");
        builder.AppendLine($"- ConnectionAvailable: `{report.ConnectionAvailable}`");
        builder.AppendLine($"- SchemaVersion: `{report.SchemaVersion}`");
        builder.AppendLine($"- FeedbackTableExists: `{report.FeedbackTableExists}`");
        builder.AppendLine($"- ReviewTableExists: `{report.ReviewTableExists}`");
        builder.AppendLine($"- FeatureCandidateTableExists: `{report.FeatureCandidateTableExists}`");
        builder.AppendLine($"- RequiredIndexesExist: `{report.RequiredIndexesExist}`");
        builder.AppendLine($"- FeedbackCount: `{report.FeedbackCount}`");
        builder.AppendLine($"- ReviewCount: `{report.ReviewCount}`");
        builder.AppendLine($"- FeatureCandidateCount: `{report.FeatureCandidateCount}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- Status: `{report.Status}`");
        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    public static string BuildParityMarkdown(PostgresLearningFeedbackParityReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Learning Feedback Parity Report");
        builder.AppendLine();
        builder.AppendLine($"- ProviderEnabled: `{report.ProviderEnabled}`");
        builder.AppendLine($"- FeedbackParityPassed: `{report.FeedbackParityPassed}`");
        builder.AppendLine($"- ReviewParityPassed: `{report.ReviewParityPassed}`");
        builder.AppendLine($"- FeatureCandidateParityPassed: `{report.FeatureCandidateParityPassed}`");
        builder.AppendLine($"- MetadataRoundtripPassed: `{report.MetadataRoundtripPassed}`");
        builder.AppendLine($"- DuplicateFeedbackUpsertPassed: `{report.DuplicateFeedbackUpsertPassed}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        builder.AppendLine($"- FeedbackCount: `{report.FeedbackCount}`");
        builder.AppendLine($"- ReviewCount: `{report.ReviewCount}`");
        builder.AppendLine($"- FeatureCandidateCount: `{report.FeatureCandidateCount}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        AppendList(builder, "Mismatches", report.Mismatches);
        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    public static string BuildReadinessGateMarkdown(LearningFeedbackPostgresReadinessGateReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Learning Feedback Readiness Gate");
        builder.AppendLine();
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- StorageDiagnosticsStatus: `{report.StorageDiagnosticsStatus}`");
        builder.AppendLine($"- SchemaVersion: `{report.SchemaVersion}`");
        builder.AppendLine($"- FeedbackTablesExist: `{report.FeedbackTablesExist}`");
        builder.AppendLine($"- ReviewTablesExist: `{report.ReviewTablesExist}`");
        builder.AppendLine($"- FeatureCandidateTablesExist: `{report.FeatureCandidateTablesExist}`");
        builder.AppendLine($"- RequiredIndexesExist: `{report.RequiredIndexesExist}`");
        builder.AppendLine($"- DiagnosticsReadyForParityEval: `{report.DiagnosticsReadyForParityEval}`");
        builder.AppendLine($"- ParityMismatchCount: `{report.ParityMismatchCount}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        AppendList(builder, "Failed Conditions", report.FailedConditions);
        return builder.ToString();
    }

    public static string BuildDualWriteSmokeMarkdown(LearningFeedbackDualWriteSmokeReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Learning Feedback Dual-write Smoke");
        builder.AppendLine();
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- OperationCount: `{report.OperationCount}`");
        builder.AppendLine($"- FileSystemWriteSuccessCount: `{report.FileSystemWriteSuccessCount}`");
        builder.AppendLine($"- PostgresWriteSuccessCount: `{report.PostgresWriteSuccessCount}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- FallbackCount: `{report.FallbackCount}`");
        builder.AppendLine($"- DuplicateFeedbackUpsertPassed: `{report.DuplicateFeedbackUpsertPassed}`");
        builder.AppendLine($"- MetadataRoundtripPassed: `{report.MetadataRoundtripPassed}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        AppendList(builder, "Mismatches", report.Mismatches);
        return builder.ToString();
    }

    public static string BuildShadowReadSmokeMarkdown(LearningFeedbackShadowReadSmokeReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Learning Feedback Shadow-read Smoke");
        builder.AppendLine();
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- OperationCount: `{report.OperationCount}`");
        builder.AppendLine($"- FileSystemReadSuccessCount: `{report.FileSystemReadSuccessCount}`");
        builder.AppendLine($"- PostgresReadSuccessCount: `{report.PostgresReadSuccessCount}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- FallbackCount: `{report.FallbackCount}`");
        builder.AppendLine($"- ExportProjectionParityPassed: `{report.ExportProjectionParityPassed}`");
        builder.AppendLine($"- SummaryParityPassed: `{report.SummaryParityPassed}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        AppendList(builder, "Mismatches", report.Mismatches);
        return builder.ToString();
    }

    public static string BuildProviderQualityMarkdown(LearningFeedbackProviderQualityReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Learning Feedback Provider Quality");
        builder.AppendLine();
        builder.AppendLine($"- TraceCount: `{report.TraceCount}`");
        builder.AppendLine($"- FileSystemWriteSuccessCount: `{report.FileSystemWriteSuccessCount}`");
        builder.AppendLine($"- PostgresWriteSuccessCount: `{report.PostgresWriteSuccessCount}`");
        builder.AppendLine($"- FileSystemReadSuccessCount: `{report.FileSystemReadSuccessCount}`");
        builder.AppendLine($"- PostgresReadSuccessCount: `{report.PostgresReadSuccessCount}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- FallbackCount: `{report.FallbackCount}`");
        builder.AppendLine($"- ExportProjectionParityPassed: `{report.ExportProjectionParityPassed}`");
        builder.AppendLine($"- SummaryParityPassed: `{report.SummaryParityPassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    public static string BuildScopedServiceModeSmokeMarkdown(LearningFeedbackScopedServiceModeSmokeReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Learning Feedback Scoped Service Mode Smoke");
        builder.AppendLine();
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- ProviderMode: `{report.ProviderMode}`");
        builder.AppendLine($"- ProviderQualityReady: `{report.ProviderQualityReady}`");
        builder.AppendLine($"- AllowlistConfigured: `{report.AllowlistConfigured}`");
        builder.AppendLine($"- NonAllowlistedScopeRemainsFileSystem: `{report.NonAllowlistedScopeRemainsFileSystem}`");
        builder.AppendLine($"- PostgresPrimaryResultVerified: `{report.PostgresPrimaryResultVerified}`");
        builder.AppendLine($"- FileSystemFallbackResultVerified: `{report.FileSystemFallbackResultVerified}`");
        builder.AppendLine($"- ExportProjectionParityPassed: `{report.ExportProjectionParityPassed}`");
        builder.AppendLine($"- SummaryParityPassed: `{report.SummaryParityPassed}`");
        builder.AppendLine($"- OperationCount: `{report.OperationCount}`");
        builder.AppendLine($"- PostgresPrimaryReadCount: `{report.PostgresPrimaryReadCount}`");
        builder.AppendLine($"- PostgresPrimaryWriteCount: `{report.PostgresPrimaryWriteCount}`");
        builder.AppendLine($"- FileSystemScopeOperationCount: `{report.FileSystemScopeOperationCount}`");
        builder.AppendLine($"- FallbackCount: `{report.FallbackCount}`");
        builder.AppendLine($"- ComparisonTraceCount: `{report.ComparisonTraceCount}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- RollbackInstruction: `{report.RollbackInstruction}`");
        AppendList(builder, "Mismatches", report.Mismatches);
        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    public static string BuildScopedServiceModeGateMarkdown(LearningFeedbackScopedServiceModeGateReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Learning Feedback Scoped Service Mode Gate");
        builder.AppendLine();
        builder.AppendLine($"- Passed: `{report.Passed}`");
        builder.AppendLine($"- ReadinessGatePassed: `{report.ReadinessGatePassed}`");
        builder.AppendLine($"- DualWriteSmokePassed: `{report.DualWriteSmokePassed}`");
        builder.AppendLine($"- ShadowReadSmokePassed: `{report.ShadowReadSmokePassed}`");
        builder.AppendLine($"- ProviderQualityReady: `{report.ProviderQualityReady}`");
        builder.AppendLine($"- ScopedAllowlistConfigured: `{report.ScopedAllowlistConfigured}`");
        builder.AppendLine($"- NonAllowlistedScopeRemainsFileSystem: `{report.NonAllowlistedScopeRemainsFileSystem}`");
        builder.AppendLine($"- ExportProjectionParityPassed: `{report.ExportProjectionParityPassed}`");
        builder.AppendLine($"- SummaryParityPassed: `{report.SummaryParityPassed}`");
        builder.AppendLine($"- FallbackTested: `{report.FallbackTested}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- FallbackCount: `{report.FallbackCount}`");
        builder.AppendLine($"- P15GatePassed: `{report.P15GatePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    public static string BuildSelectedNormalScopeCanaryMarkdown(LearningFeedbackSelectedNormalScopeCanaryReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Learning Feedback Selected Normal Scope Canary");
        builder.AppendLine();
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- ProviderMode: `{report.ProviderMode}`");
        builder.AppendLine($"- OperationCount: `{report.OperationCount}`");
        builder.AppendLine($"- PostgresPrimaryReadCount: `{report.PostgresPrimaryReadCount}`");
        builder.AppendLine($"- PostgresPrimaryWriteCount: `{report.PostgresPrimaryWriteCount}`");
        builder.AppendLine($"- FileSystemFallbackCount: `{report.FileSystemFallbackCount}`");
        builder.AppendLine($"- ComparisonTraceCount: `{report.ComparisonTraceCount}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- ScopeLeakCount: `{report.ScopeLeakCount}`");
        builder.AppendLine($"- ExportProjectionParityPassed: `{report.ExportProjectionParityPassed}`");
        builder.AppendLine($"- SummaryParityPassed: `{report.SummaryParityPassed}`");
        builder.AppendLine($"- ReviewSummaryParityPassed: `{report.ReviewSummaryParityPassed}`");
        builder.AppendLine($"- FeatureCandidateParityPassed: `{report.FeatureCandidateParityPassed}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- RollbackInstruction: `{report.RollbackInstruction}`");
        AppendList(builder, "Mismatches", report.Mismatches);
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    public static string BuildLimitedScopeObservationMarkdown(LearningFeedbackLimitedScopeObservationReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Learning Feedback Limited Scope Observation");
        builder.AppendLine();
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- ObservationWindowMinutes: `{report.ObservationWindowMinutes}`");
        builder.AppendLine($"- ProviderMode: `{report.ProviderMode}`");
        builder.AppendLine($"- OperationCount: `{report.OperationCount}`");
        builder.AppendLine($"- PostgresPrimaryReadCount: `{report.PostgresPrimaryReadCount}`");
        builder.AppendLine($"- PostgresPrimaryWriteCount: `{report.PostgresPrimaryWriteCount}`");
        builder.AppendLine($"- FileSystemFallbackCount: `{report.FileSystemFallbackCount}`");
        builder.AppendLine($"- ComparisonTraceCount: `{report.ComparisonTraceCount}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- ScopeLeakCount: `{report.ScopeLeakCount}`");
        builder.AppendLine($"- ErrorRate: `{report.ErrorRate:0.####}`");
        builder.AppendLine($"- FallbackRate: `{report.FallbackRate:0.####}`");
        builder.AppendLine($"- ExportProjectionParityPassed: `{report.ExportProjectionParityPassed}`");
        builder.AppendLine($"- SummaryParityPassed: `{report.SummaryParityPassed}`");
        builder.AppendLine($"- ReviewSummaryParityPassed: `{report.ReviewSummaryParityPassed}`");
        builder.AppendLine($"- FeatureCandidateParityPassed: `{report.FeatureCandidateParityPassed}`");
        builder.AppendLine($"- TrainableCandidateLeakCount: `{report.TrainableCandidateLeakCount}`");
        builder.AppendLine($"- SmokeCandidateExcludedCount: `{report.SmokeCandidateExcludedCount}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- RollbackInstruction: `{report.RollbackInstruction}`");
        AppendList(builder, "Mismatches", report.Mismatches);
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    public static string BuildLimitedScopeQualityMarkdown(LearningFeedbackLimitedScopeQualityReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Learning Feedback Limited Scope Quality");
        builder.AppendLine();
        builder.AppendLine($"- Passed: `{report.Passed}`");
        builder.AppendLine($"- OperationCount: `{report.OperationCount}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- ScopeLeakCount: `{report.ScopeLeakCount}`");
        builder.AppendLine($"- ErrorRate: `{report.ErrorRate:0.####}`");
        builder.AppendLine($"- FallbackRate: `{report.FallbackRate:0.####}`");
        builder.AppendLine($"- ExportProjectionParityPassed: `{report.ExportProjectionParityPassed}`");
        builder.AppendLine($"- SummaryParityPassed: `{report.SummaryParityPassed}`");
        builder.AppendLine($"- ReviewSummaryParityPassed: `{report.ReviewSummaryParityPassed}`");
        builder.AppendLine($"- FeatureCandidateParityPassed: `{report.FeatureCandidateParityPassed}`");
        builder.AppendLine($"- TrainableCandidateLeakCount: `{report.TrainableCandidateLeakCount}`");
        builder.AppendLine($"- SmokeCandidateExcludedCount: `{report.SmokeCandidateExcludedCount}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    public static string BuildFreezeGateMarkdown(LearningFeedbackPostgresFreezeGateReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Learning Feedback Freeze Gate");
        builder.AppendLine();
        builder.AppendLine($"- Passed: `{report.Passed}`");
        builder.AppendLine($"- LearningFeedbackPostgres: `{report.LearningFeedbackPostgres}`");
        builder.AppendLine($"- DefaultProvider: `{report.DefaultProvider}`");
        builder.AppendLine($"- AllowedMode: `{report.AllowedMode}`");
        builder.AppendLine($"- ReadinessGatePassed: `{report.ReadinessGatePassed}`");
        builder.AppendLine($"- ProviderQualityReady: `{report.ProviderQualityReady}`");
        builder.AppendLine($"- ScopedServiceModeGatePassed: `{report.ScopedServiceModeGatePassed}`");
        builder.AppendLine($"- SelectedNormalScopeCanaryPassed: `{report.SelectedNormalScopeCanaryPassed}`");
        builder.AppendLine($"- LimitedObservationQualityPassed: `{report.LimitedObservationQualityPassed}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- ScopeLeakCount: `{report.ScopeLeakCount}`");
        builder.AppendLine($"- TrainableCandidateLeakCount: `{report.TrainableCandidateLeakCount}`");
        builder.AppendLine($"- ExportProjectionParityPassed: `{report.ExportProjectionParityPassed}`");
        builder.AppendLine($"- SummaryParityPassed: `{report.SummaryParityPassed}`");
        builder.AppendLine($"- ReviewSummaryParityPassed: `{report.ReviewSummaryParityPassed}`");
        builder.AppendLine($"- FeatureCandidateParityPassed: `{report.FeatureCandidateParityPassed}`");
        builder.AppendLine($"- FallbackRequired: `{report.FallbackRequired}`");
        builder.AppendLine($"- ComparisonTraceRequired: `{report.ComparisonTraceRequired}`");
        builder.AppendLine($"- GlobalDefaultOnForbidden: `{report.GlobalDefaultOnForbidden}`");
        builder.AppendLine($"- P15GatePassed: `{report.P15GatePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        AppendList(builder, "Required", report.Required);
        AppendList(builder, "Forbidden", report.Forbidden);
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    private static async Task<PostgresLearningFeedbackParityReport> RunParityCoreAsync(
        FileLearningFeedbackStore fileFeedbackStore,
        FileLearningFeedbackReviewStore fileReviewStore,
        FileLearningFeatureCandidateStore fileCandidateStore,
        PostgresLearningFeedbackStore postgresFeedbackStore,
        PostgresLearningFeedbackReviewStore postgresReviewStore,
        PostgresLearningFeatureCandidateStore postgresCandidateStore,
        string workspaceId,
        string collectionId,
        bool cleanupConfirm,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var feedbackA = CreateFeedback(
            "db3-feedback-a",
            workspaceId,
            collectionId,
            ShadowCapabilityIds.RouterIntentClassifier,
            LearningFeedbackTargetType.RouterPrediction,
            LearningFeedbackKinds.WrongIntent,
            now,
            metadataOnly: true);
        var feedbackADuplicate = CreateFeedback(
            "db3-feedback-a",
            workspaceId,
            collectionId,
            ShadowCapabilityIds.RouterIntentClassifier,
            LearningFeedbackTargetType.RouterPrediction,
            LearningFeedbackKinds.WrongIntent,
            now.AddSeconds(1),
            metadataOnly: true,
            reason: "duplicate upsert replacement");
        var feedbackB = CreateFeedback(
            "db3-feedback-b",
            workspaceId,
            collectionId,
            ShadowCapabilityIds.VectorRetrieval,
            LearningFeedbackTargetType.VectorCandidate,
            LearningFeedbackKinds.MissingContext,
            now.AddSeconds(2),
            metadataOnly: false);

        foreach (var store in new[] { fileFeedbackStore })
        {
            await store.UpsertAsync(feedbackA, cancellationToken).ConfigureAwait(false);
            await store.UpsertAsync(feedbackADuplicate, cancellationToken).ConfigureAwait(false);
            await store.UpsertAsync(feedbackB, cancellationToken).ConfigureAwait(false);
        }

        await postgresFeedbackStore.UpsertAsync(feedbackA, cancellationToken).ConfigureAwait(false);
        await postgresFeedbackStore.UpsertAsync(feedbackADuplicate, cancellationToken).ConfigureAwait(false);
        await postgresFeedbackStore.UpsertAsync(feedbackB, cancellationToken).ConfigureAwait(false);

        var reviewA = CreateReview(feedbackADuplicate, FeedbackReviewStatus.ApprovedForDataset, "router_intent_correction", now.AddSeconds(3));
        var reviewB = CreateReview(feedbackB, FeedbackReviewStatus.NeedsMoreEvidence, "vector_recall_candidate", now.AddSeconds(4));
        var reviewC = CreateReview(
            CreateFeedback("db3-feedback-rejected", workspaceId, collectionId, ShadowCapabilityIds.GraphExpansion, LearningFeedbackTargetType.GraphExpansionCandidate, LearningFeedbackKinds.DeprecatedContext, now.AddSeconds(5), metadataOnly: true),
            FeedbackReviewStatus.Rejected,
            "graph_routing_candidate",
            now.AddSeconds(6));

        await fileReviewStore.UpsertAsync(reviewA, cancellationToken).ConfigureAwait(false);
        await fileReviewStore.UpsertAsync(reviewB, cancellationToken).ConfigureAwait(false);
        await fileReviewStore.UpsertAsync(reviewC, cancellationToken).ConfigureAwait(false);
        await postgresReviewStore.UpsertAsync(reviewA, cancellationToken).ConfigureAwait(false);
        await postgresReviewStore.UpsertAsync(reviewB, cancellationToken).ConfigureAwait(false);
        await postgresReviewStore.UpsertAsync(reviewC, cancellationToken).ConfigureAwait(false);

        var query = new LearningFeedbackEventQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Limit = int.MaxValue
        };
        var fileCandidateReport = await new LearningFeedbackFeatureCandidateBuilder(fileFeedbackStore, fileReviewStore)
            .BuildAsync(query, cancellationToken: cancellationToken).ConfigureAwait(false);
        var postgresCandidateReport = await new LearningFeedbackFeatureCandidateBuilder(postgresFeedbackStore, postgresReviewStore)
            .BuildAsync(query, cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var candidate in fileCandidateReport.Candidates)
        {
            await fileCandidateStore.UpsertAsync(candidate, cancellationToken).ConfigureAwait(false);
        }

        foreach (var candidate in postgresCandidateReport.Candidates)
        {
            await postgresCandidateStore.UpsertAsync(candidate, cancellationToken).ConfigureAwait(false);
        }

        var mismatches = new List<string>();
        var fileFeedback = await fileFeedbackStore.QueryAsync(query, cancellationToken).ConfigureAwait(false);
        var pgFeedback = await postgresFeedbackStore.QueryAsync(query, cancellationToken).ConfigureAwait(false);
        AddMismatchIfFalse(mismatches, SameIds(fileFeedback.Select(static item => item.FeedbackId), pgFeedback.Select(static item => item.FeedbackId)), "FeedbackListMismatch");
        AddMismatchIfFalse(mismatches, pgFeedback.Count == 2, "DuplicateFeedbackUpsertMismatch");
        AddMismatchIfFalse(mismatches, pgFeedback.Any(static item =>
            string.Equals(item.FeedbackId, "db3-feedback-a", StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Reason, "duplicate upsert replacement", StringComparison.OrdinalIgnoreCase)), "DuplicateFeedbackReplacementMissing");

        var routerQuery = new LearningFeedbackEventQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            CapabilityId = ShadowCapabilityIds.RouterIntentClassifier,
            Limit = int.MaxValue
        };
        var fileFiltered = await fileFeedbackStore.QueryAsync(routerQuery, cancellationToken).ConfigureAwait(false);
        var pgFiltered = await postgresFeedbackStore.QueryAsync(routerQuery, cancellationToken).ConfigureAwait(false);
        AddMismatchIfFalse(mismatches, fileFiltered.Count == pgFiltered.Count && pgFiltered.Count == 1, "FeedbackCapabilityFilterMismatch");

        var fileReviews = await fileReviewStore.QueryAsync(new LearningFeedbackReviewQuery { Limit = int.MaxValue }, cancellationToken)
            .ConfigureAwait(false);
        var pgReviews = await postgresReviewStore.QueryAsync(new LearningFeedbackReviewQuery { Limit = int.MaxValue }, cancellationToken)
            .ConfigureAwait(false);
        AddMismatchIfFalse(mismatches, SameIds(fileReviews.Select(static item => item.FeedbackId), pgReviews.Select(static item => item.FeedbackId)), "ReviewListMismatch");
        AddMismatchIfFalse(mismatches, pgReviews.Any(static item => item.ReviewStatus == FeedbackReviewStatus.ApprovedForDataset), "ReviewApproveMissing");
        AddMismatchIfFalse(mismatches, pgReviews.Any(static item => item.ReviewStatus == FeedbackReviewStatus.Rejected), "ReviewRejectMissing");
        AddMismatchIfFalse(mismatches, pgReviews.Any(static item => item.ReviewStatus == FeedbackReviewStatus.NeedsMoreEvidence), "ReviewNeedsEvidenceMissing");

        var needsRedaction = CreateReview(feedbackB, FeedbackReviewStatus.NeedsRedaction, "vector_recall_candidate", now.AddSeconds(7));
        await fileReviewStore.UpsertAsync(needsRedaction, cancellationToken).ConfigureAwait(false);
        await postgresReviewStore.UpsertAsync(needsRedaction, cancellationToken).ConfigureAwait(false);
        var pgNeedsRedaction = await postgresReviewStore.QueryAsync(
            new LearningFeedbackReviewQuery { ReviewStatus = FeedbackReviewStatus.NeedsRedaction, Limit = int.MaxValue },
            cancellationToken).ConfigureAwait(false);
        AddMismatchIfFalse(mismatches, pgNeedsRedaction.Any(static item => item.ReviewStatus == FeedbackReviewStatus.NeedsRedaction), "ReviewNeedsRedactionMissing");

        var fileCandidates = await fileCandidateStore.QueryAsync(new LearningFeatureCandidateQuery { Limit = int.MaxValue }, cancellationToken)
            .ConfigureAwait(false);
        var pgCandidates = await postgresCandidateStore.QueryAsync(new LearningFeatureCandidateQuery { Limit = int.MaxValue }, cancellationToken)
            .ConfigureAwait(false);
        AddMismatchIfFalse(mismatches, SameIds(fileCandidates.Select(static item => item.CandidateId), pgCandidates.Select(static item => item.CandidateId)), "FeatureCandidateListMismatch");
        AddMismatchIfFalse(mismatches, !string.IsNullOrWhiteSpace(PostgresLearningFeatureCandidateStore.ExportJsonLines(pgCandidates)), "FeatureCandidateExportProjectionMissing");
        AddMismatchIfFalse(mismatches, pgFeedback.All(static item =>
            !string.IsNullOrWhiteSpace(item.RedactionMode)
            && !string.IsNullOrWhiteSpace(item.TrainingUse)), "FeedbackSafetyMetadataRoundtripMismatch");
        AddMismatchIfFalse(mismatches, pgCandidates.All(static item =>
            item.Metadata.TryGetValue("metadataOnly", out var metadataOnly)
            && !string.IsNullOrWhiteSpace(metadataOnly)), "FeatureCandidateMetadataRoundtripMismatch");

        if (cleanupConfirm)
        {
            await postgresCandidateStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
            await postgresReviewStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
            await postgresFeedbackStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
        }

        var feedbackParity = !mismatches.Any(static item => item.Contains("Feedback", StringComparison.OrdinalIgnoreCase));
        var reviewParity = !mismatches.Any(static item => item.Contains("Review", StringComparison.OrdinalIgnoreCase));
        var candidateParity = !mismatches.Any(static item => item.Contains("FeatureCandidate", StringComparison.OrdinalIgnoreCase));
        return new PostgresLearningFeedbackParityReport
        {
            ProviderEnabled = true,
            FeedbackParityPassed = feedbackParity,
            ReviewParityPassed = reviewParity,
            FeatureCandidateParityPassed = candidateParity,
            MetadataRoundtripPassed = !mismatches.Any(static item => item.Contains("MetadataRoundtrip", StringComparison.OrdinalIgnoreCase)),
            DuplicateFeedbackUpsertPassed = !mismatches.Contains("DuplicateFeedbackUpsertMismatch", StringComparer.OrdinalIgnoreCase)
                                             && !mismatches.Contains("DuplicateFeedbackReplacementMissing", StringComparer.OrdinalIgnoreCase),
            CleanupPerformed = cleanupConfirm,
            FeedbackCount = pgFeedback.Count,
            ReviewCount = pgReviews.Count,
            FeatureCandidateCount = pgCandidates.Count,
            Mismatches = mismatches,
            Diagnostics = cleanupConfirm ? ["CleanupPerformed", "UseForRuntime=false"] : ["CleanupSkipped", "UseForRuntime=false"],
            Recommendation = mismatches.Count == 0
                ? "ReadyForParityEval"
                : "NeedsParityFix"
        };
    }

    private static LearningFeedbackEvent CreateFeedback(
        string id,
        string workspaceId,
        string collectionId,
        string capabilityId,
        LearningFeedbackTargetType targetType,
        string feedbackKind,
        DateTimeOffset createdAt,
        bool metadataOnly,
        string reason = "metadata safe feedback")
        => new()
        {
            FeedbackId = id,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Source = "db3-parity",
            SourceOperationId = $"op-{id}",
            CapabilityId = capabilityId,
            TargetId = $"target-{id}",
            TargetType = targetType.ToString(),
            FeedbackKind = feedbackKind,
            FeedbackValue = metadataOnly ? 0 : 1,
            Reason = reason,
            UserCorrection = metadataOnly ? string.Empty : "corrected context reference",
            RedactionMode = metadataOnly ? "metadata-only" : "reviewed",
            MetadataOnly = metadataOnly,
            TrainingUse = "disabled_until_review",
            Confidence = 0.9,
            CreatedAt = createdAt,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["workspaceId"] = workspaceId,
                ["collectionId"] = collectionId,
                ["source"] = "postgres_learning_feedback_parity"
            }
        };

    private static LearningFeedbackReviewRecord CreateReview(
        LearningFeedbackEvent feedback,
        FeedbackReviewStatus status,
        string labelKind,
        DateTimeOffset reviewedAt)
        => new()
        {
            FeedbackId = feedback.FeedbackId,
            Reviewer = "db3-parity",
            ReviewStatus = status,
            ReviewReason = status.ToString(),
            ApprovedCapability = feedback.CapabilityId,
            ApprovedLabelKind = labelKind,
            RedactionChecked = true,
            TrainingUse = status == FeedbackReviewStatus.ApprovedForDataset ? "offline_baseline_candidate" : "disabled_until_review",
            ReviewedAt = reviewedAt,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["workspaceId"] = feedback.WorkspaceId,
                ["collectionId"] = feedback.CollectionId,
                ["source"] = "postgres_learning_feedback_parity"
            }
        };

    private static bool SameIds(IEnumerable<string> left, IEnumerable<string> right)
        => left.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(
            right.Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    private static void AddMismatchIfFalse(List<string> mismatches, bool condition, string reason)
    {
        if (!condition)
        {
            mismatches.Add(reason);
        }
    }

    private static void AppendList(StringBuilder builder, string title, IReadOnlyList<string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var value in values)
        {
            builder.AppendLine($"- `{value}`");
        }
    }

    public static string SerializeJson<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
