using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Planning;
using ContextCore.Core.Services.Storage;
using ContextCore.Embedding;
using ContextCore.Embedding.Providers;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.ControlRoom.Commands;

public static partial class EvalCommand
{
private static async Task ExecuteStorageBoundaryReportAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "storage-boundary-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "storage-boundary-report.md");
        var report = StorageResponsibilityRegistry.BuildReport();

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(StorageResponsibilityRegistry.BuildMarkdownReport(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[StorageBoundary] JSON: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[StorageBoundary] Markdown: {Path.GetFullPath(markdownPath)}");
        Console.WriteLine($"[StorageBoundary] DatabaseRecommended={report.DatabaseRecommendedCount}, MigrationCandidates={report.MigrationCandidates.Count}");
    }

private static async Task ExecutePostgresStorageDiagnosticsAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres-storage-diagnostics.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres-storage-diagnostics.md");
        var diagnostics = await BuildCliPostgresDiagnosticsAsync(args, cancellationToken).ConfigureAwait(false);

        await WriteTextAsync(JsonSerializer.Serialize(diagnostics, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresDiagnosticsMarkdown(diagnostics), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresStorage] Diagnostics: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresStorage] Status={diagnostics.Status}, Pending={diagnostics.PendingMigrations}, MissingTables={diagnostics.RequiredTableMissingCount}");
    }

private static async Task ExecutePostgresMigrationPreviewAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres-migration-preview.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres-migration-preview.md");
        var options = BuildCliPostgresOptions(args);
        PostgresMigrationPlanResponse response;
        if (!options.Enabled)
        {
            var disabled = PostgresOperationalStoreDiagnosticsBuilder.BuildNotConfigured(options);
            response = new PostgresMigrationPlanResponse
            {
                DryRun = true,
                ProviderEnabled = false,
                ProviderId = disabled.ProviderId,
                PendingMigrations = [PostgresMigrationRunner.BaselineMigrationId],
                RequiredTables = disabled.RequiredTables,
                MissingRequiredTables = disabled.MissingRequiredTables,
                Diagnostics = disabled.Diagnostics
            };
        }
        else
        {
            await using var factory = new PostgresConnectionFactory(options);
            var runner = new PostgresMigrationRunner(factory);
            response = ToCliPlanResponse(await runner.PreviewMigrationsAsync(cancellationToken).ConfigureAwait(false));
        }

        await WriteTextAsync(JsonSerializer.Serialize(response, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresMigrationPreviewMarkdown(response), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresMigration] Preview: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresMigration] Pending={response.PendingMigrations.Count}, MissingTables={response.MissingRequiredTables.Count}");
    }

private static async Task ExecutePostgresMigrationApplyAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres-migration-apply.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres-migration-apply.md");
        var confirm = CommandHelpers.HasFlag(args, "--confirm");
        var options = BuildCliPostgresOptions(args);
        PostgresMigrationApplyResponse response;
        if (!confirm)
        {
            response = new PostgresMigrationApplyResponse
            {
                Applied = false,
                ConfirmRequired = true,
                Diagnostics = ["ConfirmRequired"]
            };
        }
        else if (!options.Enabled)
        {
            response = new PostgresMigrationApplyResponse
            {
                Applied = false,
                ConfirmRequired = false,
                Diagnostics = ["NotConfigured"]
            };
        }
        else
        {
            await using var factory = new PostgresConnectionFactory(options);
            var runner = new PostgresMigrationRunner(factory);
            var result = await runner.ApplyMigrationsAsync(confirm, cancellationToken).ConfigureAwait(false);
            response = new PostgresMigrationApplyResponse
            {
                Applied = result.Applied,
                ConfirmRequired = result.ConfirmRequired,
                SchemaVersion = result.SchemaVersion,
                AppliedMigrations = result.AppliedMigrations,
                Diagnostics = result.Diagnostics
            };
        }

        await WriteTextAsync(JsonSerializer.Serialize(response, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresMigrationApplyMarkdown(response), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresMigration] Apply: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresMigration] Applied={response.Applied}, ConfirmRequired={response.ConfirmRequired}");
    }

private static async Task ExecutePostgresMigrationSmokeAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-schema-verification-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-schema-verification-report.md");
        var confirm = CommandHelpers.HasFlag(args, "--confirm");
        var dropConfirm = CommandHelpers.HasFlag(args, "--drop-confirm");
        var options = BuildCliPostgresOptions(args, defaultSchemaName: "contextcore_smoke");

        PostgresSchemaVerificationReport report;
        if (!options.Enabled)
        {
            report = new PostgresSchemaVerificationReport
            {
                ProviderEnabled = false,
                ConnectionAvailable = false,
                SchemaName = options.SchemaName,
                RequiredTableCount = PostgresMigrationRunner.RequiredOperationalTableSuffixes.Count,
                MissingRequiredTableCount = PostgresMigrationRunner.RequiredOperationalTableSuffixes.Count,
                RequiredIndexCount = PostgresMigrationRunner.RequiredOperationalIndexDefinitions.Count,
                MissingIndexCount = PostgresMigrationRunner.RequiredOperationalIndexDefinitions.Count,
                RequiredTables = PostgresMigrationRunner.GetRequiredTableNames(options),
                MissingRequiredTables = PostgresMigrationRunner.GetRequiredTableNames(options),
                RequiredIndexes = PostgresMigrationRunner.GetRequiredIndexNames(options),
                MissingIndexes = PostgresMigrationRunner.GetRequiredIndexNames(options),
                Diagnostics = ["NotConfigured"],
                Recommendation = "NotConfigured"
            };
        }
        else
        {
            await using var factory = new PostgresConnectionFactory(options);
            var runner = new PostgresMigrationRunner(factory);
            var ping = await factory.PingAsync(cancellationToken).ConfigureAwait(false);
            if (!ping.Success)
            {
                report = new PostgresSchemaVerificationReport
                {
                    ProviderEnabled = true,
                    ConnectionAvailable = false,
                    SchemaName = options.SchemaName,
                    RequiredTableCount = PostgresMigrationRunner.RequiredOperationalTableSuffixes.Count,
                    MissingRequiredTableCount = PostgresMigrationRunner.RequiredOperationalTableSuffixes.Count,
                    RequiredIndexCount = PostgresMigrationRunner.RequiredOperationalIndexDefinitions.Count,
                    MissingIndexCount = PostgresMigrationRunner.RequiredOperationalIndexDefinitions.Count,
                    RequiredTables = PostgresMigrationRunner.GetRequiredTableNames(options),
                    MissingRequiredTables = PostgresMigrationRunner.GetRequiredTableNames(options),
                    RequiredIndexes = PostgresMigrationRunner.GetRequiredIndexNames(options),
                    MissingIndexes = PostgresMigrationRunner.GetRequiredIndexNames(options),
                    Diagnostics = string.IsNullOrWhiteSpace(ping.ErrorMessage)
                        ? ["BlockedByConnection"]
                        : ["BlockedByConnection", RedactPostgresDiagnostic(ping.ErrorMessage)],
                    Recommendation = "BlockedByConnection"
                };
            }
            else if (!confirm)
            {
                var plan = await runner.PreviewMigrationsAsync(cancellationToken).ConfigureAwait(false);
                report = new PostgresSchemaVerificationReport
                {
                    ProviderEnabled = true,
                    ConnectionAvailable = true,
                    SchemaName = options.SchemaName,
                    CurrentSchemaVersion = plan.CurrentSchemaVersion,
                    RequiredTableCount = PostgresMigrationRunner.RequiredOperationalTableSuffixes.Count,
                    MissingRequiredTableCount = plan.MissingRequiredTables.Count,
                    RequiredIndexCount = PostgresMigrationRunner.RequiredOperationalIndexDefinitions.Count,
                    MissingIndexCount = PostgresMigrationRunner.RequiredOperationalIndexDefinitions.Count,
                    RequiredTables = plan.RequiredTables,
                    MissingRequiredTables = plan.MissingRequiredTables,
                    RequiredIndexes = PostgresMigrationRunner.GetRequiredIndexNames(options),
                    MissingIndexes = PostgresMigrationRunner.GetRequiredIndexNames(options),
                    Diagnostics = ["ConfirmRequired", .. plan.Diagnostics],
                    Recommendation = "SchemaIncomplete"
                };
            }
            else
            {
                var apply = await runner.ApplyMigrationsAsync(confirm, cancellationToken).ConfigureAwait(false);
                report = await runner.VerifySchemaAsync(cancellationToken).ConfigureAwait(false);
                if (!apply.Applied)
                {
                    report = report with
                    {
                        Diagnostics = [.. report.Diagnostics, "MigrationFailed"],
                        Recommendation = "MigrationFailed"
                    };
                }

                if (dropConfirm)
                {
                    await runner.DropSchemaAsync(confirm: true, cancellationToken).ConfigureAwait(false);
                    report = report with
                    {
                        Diagnostics = [.. report.Diagnostics, "SmokeSchemaDropped"]
                    };
                }
            }
        }

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresSchemaVerificationMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresMigration] Smoke verification: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresMigration] Recommendation={report.Recommendation}, MissingTables={report.MissingRequiredTableCount}, MissingIndexes={report.MissingIndexCount}");
    }

private static async Task ExecutePostgresRelationStoreDiagnosticsAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-store-diagnostics.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-store-diagnostics.md");
        var options = BuildCliPostgresOptions(args);
        PostgresRelationStoreDiagnostics diagnostics;
        if (!options.Enabled)
        {
            diagnostics = PostgresRelationStoreDiagnosticsBuilder.BuildNotConfigured(options);
        }
        else
        {
            await using var factory = new PostgresConnectionFactory(options);
            var runner = new PostgresMigrationRunner(factory);
            diagnostics = await PostgresRelationStoreDiagnosticsBuilder.BuildAsync(
                options,
                factory,
                runner,
                useForRuntime: false,
                cancellationToken).ConfigureAwait(false);
        }

        await WriteTextAsync(JsonSerializer.Serialize(diagnostics, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresRelationStoreDiagnosticsMarkdown(diagnostics), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresRelationStore] Diagnostics: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresRelationStore] Recommendation={diagnostics.Recommendation}, Relations={diagnostics.RelationCount}, MissingIndexes={diagnostics.MissingRequiredIndexes.Count}");
    }

private static async Task ExecutePostgresRelationStoreParityAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-store-parity-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-store-parity-report.md");
        var cleanupConfirm = CommandHelpers.HasFlag(args, "--cleanup-confirm");
        var options = BuildCliPostgresOptions(args);
        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? "postgres-relation-parity";
        var collectionId = CommandHelpers.GetOption(args, "--collection") ?? "relation-store";

        PostgresRelationStoreParityReport report;
        if (!options.Enabled)
        {
            report = new PostgresRelationStoreParityReport
            {
                ProviderEnabled = false,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Diagnostics = ["NotConfigured"],
                Recommendation = "NotConfigured"
            };
        }
        else
        {
            await using var factory = new PostgresConnectionFactory(options);
            var serializer = new PostgresJsonSerializer();
            var migrationRunner = new PostgresMigrationRunner(factory);
            var postgresStore = new PostgresRelationStore(factory, serializer, migrationRunner);
            var fileRoot = Path.Combine(Path.GetTempPath(), "contextcore-relation-parity", Guid.NewGuid().ToString("N"));
            var fileStore = new FileRelationStore(new FileStorageOptions { RootPath = fileRoot });
            try
            {
                report = await RunPostgresRelationStoreParityAsync(
                    fileStore,
                    postgresStore,
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

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresRelationStoreParityMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresRelationStore] Parity: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresRelationStore] Recommendation={report.Recommendation}, Mismatches={report.Mismatches.Count}");
    }

private static async Task ExecutePostgresRelationReviewDiagnosticsAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-review-diagnostics.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-review-diagnostics.md");
        var options = BuildCliPostgresOptions(args);
        PostgresRelationReviewProviderDiagnostics diagnostics;
        if (!options.Enabled)
        {
            diagnostics = PostgresRelationReviewDiagnosticsBuilder.BuildNotConfigured(options);
        }
        else
        {
            await using var factory = new PostgresConnectionFactory(options);
            var runner = new PostgresMigrationRunner(factory);
            await runner.ApplyMigrationsAsync(confirm: true, cancellationToken).ConfigureAwait(false);
            diagnostics = await PostgresRelationReviewDiagnosticsBuilder.BuildAsync(
                options,
                factory,
                runner,
                useForRuntime: false,
                cancellationToken).ConfigureAwait(false);
        }

        await WriteTextAsync(JsonSerializer.Serialize(diagnostics, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresRelationReviewDiagnosticsMarkdown(diagnostics), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresRelationReview] Diagnostics: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresRelationReview] Recommendation={diagnostics.Recommendation}, Reviews={diagnostics.ReviewCount}, Diagnostics={diagnostics.DiagnosticsCount}, MissingIndexes={diagnostics.MissingRequiredIndexes.Count}");
    }

private static async Task ExecutePostgresRelationReviewParityAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-review-parity-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-review-parity-report.md");
        var cleanupConfirm = CommandHelpers.HasFlag(args, "--cleanup-confirm");
        var options = BuildCliPostgresOptions(args);
        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? "postgres-relation-review-parity";
        var collectionId = CommandHelpers.GetOption(args, "--collection") ?? "relation-review";

        PostgresRelationReviewParityReport report;
        if (!options.Enabled)
        {
            report = new PostgresRelationReviewParityReport
            {
                ProviderEnabled = false,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Diagnostics = ["NotConfigured"],
                Recommendation = "NotConfigured"
            };
        }
        else
        {
            await using var factory = new PostgresConnectionFactory(options);
            var serializer = new PostgresJsonSerializer();
            var migrationRunner = new PostgresMigrationRunner(factory);
            await migrationRunner.ApplyMigrationsAsync(confirm: true, cancellationToken).ConfigureAwait(false);
            var postgresReviewStore = new PostgresRelationReviewStore(factory, serializer, migrationRunner);
            var postgresDiagnosticsStore = new PostgresRelationDiagnosticsStore(factory, serializer, migrationRunner);
            var postgresRelationStore = new PostgresRelationStore(factory, serializer, migrationRunner);
            var fileRoot = Path.Combine(Path.GetTempPath(), "contextcore-relation-review-parity", Guid.NewGuid().ToString("N"));
            var fileOptions = new FileStorageOptions { RootPath = fileRoot };
            var filePaths = new FilePathResolver(fileOptions);
            var fileSerializer = new FileFormatSerializer();
            var fileReviewStore = new FileRelationReviewStore(filePaths, fileSerializer);
            var fileDiagnosticsStore = new FileRelationDiagnosticsStore(filePaths, fileSerializer);
            try
            {
                report = await RunPostgresRelationReviewParityAsync(
                    fileReviewStore,
                    fileDiagnosticsStore,
                    postgresReviewStore,
                    postgresDiagnosticsStore,
                    postgresRelationStore,
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

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresRelationReviewParityMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresRelationReview] Parity: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresRelationReview] Recommendation={report.Recommendation}, Mismatches={report.Mismatches.Count}");
    }

private static async Task ExecutePostgresRelationGovernanceParityAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-governance-parity-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-governance-parity-report.md");
        var cleanupConfirm = CommandHelpers.HasFlag(args, "--cleanup-confirm");
        var options = BuildCliPostgresOptions(args);
        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? "postgres-relation-governance-parity";
        var collectionId = CommandHelpers.GetOption(args, "--collection") ?? "relation-governance";

        PostgresRelationGovernanceParityReport report;
        if (!options.Enabled)
        {
            report = new PostgresRelationGovernanceParityReport
            {
                ProviderEnabled = false,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                BlockedReasons = ["NotConfigured"],
                Diagnostics = ["NotConfigured"],
                Recommendation = "NotConfigured"
            };
        }
        else
        {
            await using var factory = new PostgresConnectionFactory(options);
            var serializer = new PostgresJsonSerializer();
            var migrationRunner = new PostgresMigrationRunner(factory);
            await migrationRunner.ApplyMigrationsAsync(confirm: true, cancellationToken).ConfigureAwait(false);
            var postgresRelationStore = new PostgresRelationStore(factory, serializer, migrationRunner);
            var postgresReviewStore = new PostgresRelationReviewStore(factory, serializer, migrationRunner);
            var postgresDiagnosticsStore = new PostgresRelationDiagnosticsStore(factory, serializer, migrationRunner);
            var fileRoot = Path.Combine(Path.GetTempPath(), "contextcore-relation-governance-parity", Guid.NewGuid().ToString("N"));
            var fileOptions = new FileStorageOptions { RootPath = fileRoot };
            var filePaths = new FilePathResolver(fileOptions);
            var fileSerializer = new FileFormatSerializer();
            var fileRelationStore = new FileRelationStore(fileOptions);
            var fileReviewStore = new FileRelationReviewStore(filePaths, fileSerializer);
            var fileDiagnosticsStore = new FileRelationDiagnosticsStore(filePaths, fileSerializer);
            try
            {
                report = await RunPostgresRelationGovernanceParityAsync(
                    fileRelationStore,
                    fileReviewStore,
                    fileDiagnosticsStore,
                    postgresRelationStore,
                    postgresReviewStore,
                    postgresDiagnosticsStore,
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

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresRelationGovernanceParityMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresRelationGovernance] Parity: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresRelationGovernance] Recommendation={report.Recommendation}, Mismatches={report.Mismatches.Count}, Cleanup={report.CleanupPerformed}");
    }

private static async Task ExecutePostgresRelationGovernanceReadinessGateAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-governance-readiness-gate.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-governance-readiness-gate.md");
        var report = await BuildPostgresRelationGovernanceReadinessGateAsync(cancellationToken).ConfigureAwait(false);

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresRelationGovernanceReadinessGateMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresRelationGovernance] Readiness gate: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresRelationGovernance] Passed={report.Passed}, Recommendation={report.Recommendation}, Blocked={report.BlockedReasons.Count}");
    }

private static async Task ExecutePostgresRelationDualWriteSmokeAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-dual-write-smoke-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-dual-write-smoke-report.md");
        var tracePath = CommandHelpers.GetOption(args, "--trace-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-dual-write-traces.jsonl");
        var cleanupConfirm = CommandHelpers.HasFlag(args, "--cleanup-confirm");
        var options = BuildCliPostgresOptions(args);
        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? "postgres-relation-dual-write-smoke";
        var collectionId = CommandHelpers.GetOption(args, "--collection") ?? "relation-dual-write";

        PostgresRelationDualWriteSmokeReport report;
        if (!options.Enabled)
        {
            report = new PostgresRelationDualWriteSmokeReport
            {
                ProviderEnabled = false,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Diagnostics = ["NotConfigured"],
                Recommendation = "NotConfigured"
            };
        }
        else
        {
            await using var factory = new PostgresConnectionFactory(options);
            var serializer = new PostgresJsonSerializer();
            var migrationRunner = new PostgresMigrationRunner(factory);
            await migrationRunner.ApplyMigrationsAsync(confirm: true, cancellationToken).ConfigureAwait(false);
            var postgresRelationStore = new PostgresRelationStore(factory, serializer, migrationRunner);
            var postgresReviewStore = new PostgresRelationReviewStore(factory, serializer, migrationRunner);
            var postgresDiagnosticsStore = new PostgresRelationDiagnosticsStore(factory, serializer, migrationRunner);
            var fileRoot = Path.Combine(Path.GetTempPath(), "contextcore-relation-dual-write-smoke", Guid.NewGuid().ToString("N"));
            var fileOptions = new FileStorageOptions { RootPath = fileRoot };
            var filePaths = new FilePathResolver(fileOptions);
            var fileSerializer = new FileFormatSerializer();
            var fileRelationStore = new FileRelationStore(fileOptions);
            var fileReviewStore = new FileRelationReviewStore(filePaths, fileSerializer);
            var fileDiagnosticsStore = new FileRelationDiagnosticsStore(filePaths, fileSerializer);
            var artifactWriter = new TraceArtifactWriter(new FileArtifactStore(new FileStorageOptions()));
            var traces = new List<RelationGovernanceDualWriteTrace>();
            var dualWriteOptions = new RelationGovernanceDualWriteOptions
            {
                Enabled = true,
                WritePostgres = true,
                TraceEnabled = true,
                FallbackOnPostgresFailure = true,
                FailOnMismatch = false
            };
            var coordinator = new RelationGovernanceDualWriteCoordinator(
                fileRelationStore,
                fileReviewStore,
                fileDiagnosticsStore,
                postgresRelationStore,
                postgresReviewStore,
                postgresDiagnosticsStore,
                dualWriteOptions,
                async (trace, token) =>
                {
                    traces.Add(trace);
                    await AppendJsonLineFileAsync(tracePath, trace, token).ConfigureAwait(false);
                    await artifactWriter.AppendTraceJsonLineAsync(
                        trace.WorkspaceId,
                        trace.CollectionId,
                        ArtifactKind.TraceRelationDualWrite,
                        trace,
                        operationId: trace.OperationId,
                        capabilityId: "relation-governance",
                        reportId: "relation-dual-write-traces",
                        cancellationToken: token).ConfigureAwait(false);
                });

            try
            {
                report = await RunPostgresRelationDualWriteSmokeAsync(
                    coordinator,
                    fileRelationStore,
                    fileReviewStore,
                    fileDiagnosticsStore,
                    postgresRelationStore,
                    postgresReviewStore,
                    postgresDiagnosticsStore,
                    workspaceId,
                    collectionId,
                    cleanupConfirm,
                    traces,
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

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresRelationDualWriteSmokeMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresRelationDualWrite] Smoke: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresRelationDualWrite] Recommendation={report.Recommendation}, Traces={report.TraceCount}, Mismatches={report.Mismatches.Count}");
    }

private static async Task ExecutePostgresRelationDualWriteQualityAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var inputPath = CommandHelpers.GetOption(args, "--input")
            ?? Path.Combine("storage", "postgres", "postgres-relation-dual-write-traces.jsonl");
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-dual-write-quality-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-dual-write-quality-report.md");
        var report = await BuildPostgresRelationDualWriteQualityReportAsync(inputPath, cancellationToken)
            .ConfigureAwait(false);

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresRelationDualWriteQualityMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresRelationDualWrite] Quality: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresRelationDualWrite] Recommendation={report.Recommendation}, Traces={report.TraceCount}, Mismatches={report.MismatchCount}");
    }

private static async Task ExecutePostgresRelationShadowReadSmokeAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-shadow-read-smoke-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-shadow-read-smoke-report.md");
        var tracePath = CommandHelpers.GetOption(args, "--trace-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-shadow-read-traces.jsonl");
        var cleanupConfirm = CommandHelpers.HasFlag(args, "--cleanup-confirm");
        var options = BuildCliPostgresOptions(args);
        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? "postgres-relation-shadow-read-smoke";
        var collectionId = CommandHelpers.GetOption(args, "--collection") ?? "relation-shadow-read";

        PostgresRelationShadowReadSmokeReport report;
        if (!options.Enabled)
        {
            report = new PostgresRelationShadowReadSmokeReport
            {
                ProviderEnabled = false,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Diagnostics = ["NotConfigured"],
                Recommendation = "NotConfigured"
            };
        }
        else
        {
            await using var factory = new PostgresConnectionFactory(options);
            var serializer = new PostgresJsonSerializer();
            var migrationRunner = new PostgresMigrationRunner(factory);
            await migrationRunner.ApplyMigrationsAsync(confirm: true, cancellationToken).ConfigureAwait(false);
            var postgresRelationStore = new PostgresRelationStore(factory, serializer, migrationRunner);
            var postgresReviewStore = new PostgresRelationReviewStore(factory, serializer, migrationRunner);
            var postgresDiagnosticsStore = new PostgresRelationDiagnosticsStore(factory, serializer, migrationRunner);
            var fileRoot = Path.Combine(Path.GetTempPath(), "contextcore-relation-shadow-read-smoke", Guid.NewGuid().ToString("N"));
            var fileOptions = new FileStorageOptions { RootPath = fileRoot };
            var filePaths = new FilePathResolver(fileOptions);
            var fileSerializer = new FileFormatSerializer();
            var fileRelationStore = new FileRelationStore(fileOptions);
            var fileReviewStore = new FileRelationReviewStore(filePaths, fileSerializer);
            var fileDiagnosticsStore = new FileRelationDiagnosticsStore(filePaths, fileSerializer);
            var artifactWriter = new TraceArtifactWriter(new FileArtifactStore(new FileStorageOptions()));
            var traces = new List<RelationGovernanceShadowReadTrace>();

            try
            {
                report = await RunPostgresRelationShadowReadSmokeAsync(
                    fileRelationStore,
                    fileReviewStore,
                    fileDiagnosticsStore,
                    postgresRelationStore,
                    postgresReviewStore,
                    postgresDiagnosticsStore,
                    workspaceId,
                    collectionId,
                    cleanupConfirm,
                    traces,
                    async (trace, token) =>
                    {
                        traces.Add(trace);
                        await AppendJsonLineFileAsync(tracePath, trace, token).ConfigureAwait(false);
                        await artifactWriter.AppendTraceJsonLineAsync(
                            trace.WorkspaceId,
                            trace.CollectionId,
                            ArtifactKind.TraceRelationShadowRead,
                            trace,
                            operationId: trace.OperationId,
                            capabilityId: "relation-governance",
                            reportId: "relation-shadow-read-traces",
                            cancellationToken: token).ConfigureAwait(false);
                    },
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

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresRelationShadowReadSmokeMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresRelationShadowRead] Smoke: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresRelationShadowRead] Recommendation={report.Recommendation}, Traces={report.TraceCount}, Mismatches={report.Mismatches.Count}");
    }

private static async Task ExecutePostgresRelationShadowReadQualityAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var inputPath = CommandHelpers.GetOption(args, "--input")
            ?? Path.Combine("storage", "postgres", "postgres-relation-shadow-read-traces.jsonl");
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-shadow-read-quality-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-shadow-read-quality-report.md");
        var report = await BuildPostgresRelationShadowReadQualityReportAsync(inputPath, cancellationToken)
            .ConfigureAwait(false);

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresRelationShadowReadQualityMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresRelationShadowRead] Quality: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresRelationShadowRead] Recommendation={report.Recommendation}, Traces={report.TraceCount}, Mismatches={report.MismatchCount}");
    }

private static async Task ExecutePostgresRelationProviderSwitchSmokeAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-provider-switch-smoke-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-provider-switch-smoke-report.md");
        var tracePath = CommandHelpers.GetOption(args, "--trace-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-provider-switch-traces.jsonl");
        var cleanupConfirm = CommandHelpers.HasFlag(args, "--cleanup-confirm");
        var options = BuildCliPostgresOptions(args);
        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? "postgres-relation-provider-switch-smoke";
        var collectionId = CommandHelpers.GetOption(args, "--collection") ?? "relation-provider-switch";
        var readinessGate = await ReadJsonFileAsync<PostgresRelationGovernanceReadinessGateReport>(
            Path.Combine("storage", "postgres", "postgres-relation-governance-readiness-gate.json"),
            cancellationToken).ConfigureAwait(false);
        var shadowQuality = await ReadJsonFileAsync<PostgresRelationShadowReadQualityReport>(
            Path.Combine("storage", "postgres", "postgres-relation-shadow-read-quality-report.json"),
            cancellationToken).ConfigureAwait(false);

        PostgresRelationProviderSwitchSmokeReport report;
        if (!options.Enabled)
        {
            report = new PostgresRelationProviderSwitchSmokeReport
            {
                ProviderEnabled = false,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Mode = RelationGovernanceProviderMode.GuardedPostgresPrimary.ToString(),
                Diagnostics = ["NotConfigured"],
                Recommendation = "NotConfigured"
            };
        }
        else
        {
            await using var factory = new PostgresConnectionFactory(options);
            var serializer = new PostgresJsonSerializer();
            var migrationRunner = new PostgresMigrationRunner(factory);
            await migrationRunner.ApplyMigrationsAsync(confirm: true, cancellationToken).ConfigureAwait(false);
            var postgresRelationStore = new PostgresRelationStore(factory, serializer, migrationRunner);
            var postgresReviewStore = new PostgresRelationReviewStore(factory, serializer, migrationRunner);
            var postgresDiagnosticsStore = new PostgresRelationDiagnosticsStore(factory, serializer, migrationRunner);
            var fileRoot = Path.Combine(Path.GetTempPath(), "contextcore-relation-provider-switch-smoke", Guid.NewGuid().ToString("N"));
            var fileOptions = new FileStorageOptions { RootPath = fileRoot };
            var filePaths = new FilePathResolver(fileOptions);
            var fileSerializer = new FileFormatSerializer();
            var fileRelationStore = new FileRelationStore(fileOptions);
            var fileReviewStore = new FileRelationReviewStore(filePaths, fileSerializer);
            var fileDiagnosticsStore = new FileRelationDiagnosticsStore(filePaths, fileSerializer);
            var artifactWriter = new TraceArtifactWriter(new FileArtifactStore(new FileStorageOptions()));
            var traces = new List<RelationGovernanceProviderSwitchTrace>();

            try
            {
                report = await RunPostgresRelationProviderSwitchSmokeAsync(
                    fileRelationStore,
                    fileReviewStore,
                    fileDiagnosticsStore,
                    postgresRelationStore,
                    postgresReviewStore,
                    postgresDiagnosticsStore,
                    workspaceId,
                    collectionId,
                    cleanupConfirm,
                    readinessGate?.Passed == true,
                    string.Equals(shadowQuality?.Recommendation, "ReadyForGuardedProviderSwitch", StringComparison.OrdinalIgnoreCase),
                    traces,
                    async (trace, token) =>
                    {
                        traces.Add(trace);
                        await AppendJsonLineFileAsync(tracePath, trace, token).ConfigureAwait(false);
                        await artifactWriter.AppendTraceJsonLineAsync(
                            trace.WorkspaceId,
                            trace.CollectionId,
                            ArtifactKind.TraceRelationProviderSwitch,
                            trace,
                            operationId: trace.OperationId,
                            capabilityId: "relation-governance",
                            reportId: "relation-provider-switch-traces",
                            cancellationToken: token).ConfigureAwait(false);
                    },
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

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresRelationProviderSwitchSmokeMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresRelationProviderSwitch] Smoke: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresRelationProviderSwitch] Recommendation={report.Recommendation}, Traces={report.TraceCount}, Mismatches={report.Mismatches.Count}");
    }

private static async Task ExecutePostgresRelationProviderSwitchGateAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-provider-switch-gate.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-provider-switch-gate.md");
        var report = await BuildPostgresRelationProviderSwitchGateAsync(cancellationToken).ConfigureAwait(false);

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresRelationProviderSwitchGateMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresRelationProviderSwitch] Gate: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresRelationProviderSwitch] Passed={report.Passed}, Recommendation={report.Recommendation}, Blocked={report.BlockedReasons.Count}");
    }

private static async Task ExecutePostgresRelationRuntimeCanaryAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-runtime-canary-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-runtime-canary-report.md");
        var tracePath = CommandHelpers.GetOption(args, "--trace-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-runtime-canary-traces.jsonl");
        var cleanupConfirm = CommandHelpers.HasFlag(args, "--cleanup-confirm");
        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? "contextcore_canary";
        var collectionId = CommandHelpers.GetOption(args, "--collection") ?? "relation-governance-canary";
        var options = BuildCliPostgresOptions(args);
        var storageDiagnostics = await BuildCliPostgresDiagnosticsAsync(args, cancellationToken).ConfigureAwait(false);
        var readinessGate = await ReadJsonFileAsync<PostgresRelationGovernanceReadinessGateReport>(
            Path.Combine("storage", "postgres", "postgres-relation-governance-readiness-gate.json"),
            cancellationToken).ConfigureAwait(false);
        var dualWriteQuality = await ReadJsonFileAsync<PostgresRelationDualWriteQualityReport>(
            Path.Combine("storage", "postgres", "postgres-relation-dual-write-quality-report.json"),
            cancellationToken).ConfigureAwait(false);
        var shadowReadQuality = await ReadJsonFileAsync<PostgresRelationShadowReadQualityReport>(
            Path.Combine("storage", "postgres", "postgres-relation-shadow-read-quality-report.json"),
            cancellationToken).ConfigureAwait(false);
        var providerSwitchGate = await ReadJsonFileAsync<PostgresRelationProviderSwitchGateReport>(
            Path.Combine("storage", "postgres", "postgres-relation-provider-switch-gate.json"),
            cancellationToken).ConfigureAwait(false);

        var blocked = new List<string>();
        var diagnostics = new List<string>();
        AddReasonIfFalse(blocked, options.Enabled, "NotConfigured");
        AddReasonIfFalse(blocked, string.Equals(storageDiagnostics.Status, "Ready", StringComparison.OrdinalIgnoreCase), "PostgresStorageNotReady");
        AddReasonIfFalse(blocked, readinessGate?.Passed == true, "GovernanceReadinessGateNotPassed");
        AddReasonIfFalse(blocked, string.Equals(dualWriteQuality?.Recommendation, "ReadyForShadowRead", StringComparison.OrdinalIgnoreCase), "DualWriteQualityNotReady");
        AddReasonIfFalse(blocked, string.Equals(shadowReadQuality?.Recommendation, "ReadyForGuardedProviderSwitch", StringComparison.OrdinalIgnoreCase), "ShadowReadQualityNotReady");
        AddReasonIfFalse(blocked, providerSwitchGate?.Passed == true, "ProviderSwitchGateNotPassed");
        if (readinessGate is null)
        {
            diagnostics.Add("RunPostgresRelationGovernanceReadinessGateFirst");
        }

        if (dualWriteQuality is null)
        {
            diagnostics.Add("RunPostgresRelationDualWriteQualityFirst");
        }

        if (shadowReadQuality is null)
        {
            diagnostics.Add("RunPostgresRelationShadowReadQualityFirst");
        }

        if (providerSwitchGate is null)
        {
            diagnostics.Add("RunPostgresRelationProviderSwitchGateFirst");
        }

        PostgresRelationRuntimeCanaryReport report;
        if (!options.Enabled)
        {
            report = new PostgresRelationRuntimeCanaryReport
            {
                CanaryScope = $"{workspaceId}/{collectionId}",
                ProviderMode = RelationGovernanceProviderMode.GuardedPostgresPrimary.ToString(),
                GatePassed = false,
                CleanupPerformed = cleanupConfirm,
                Diagnostics = diagnostics.Count == 0 ? ["NotConfigured"] : diagnostics,
                BlockedReasons = blocked,
                Recommendation = "NotConfigured"
            };
        }
        else if (blocked.Count > 0)
        {
            report = new PostgresRelationRuntimeCanaryReport
            {
                CanaryScope = $"{workspaceId}/{collectionId}",
                ProviderMode = RelationGovernanceProviderMode.GuardedPostgresPrimary.ToString(),
                GatePassed = false,
                CleanupPerformed = cleanupConfirm,
                Diagnostics = diagnostics,
                BlockedReasons = blocked,
                Recommendation = "GateNotPassed"
            };
        }
        else
        {
            await using var factory = new PostgresConnectionFactory(options);
            var serializer = new PostgresJsonSerializer();
            var migrationRunner = new PostgresMigrationRunner(factory);
            var postgresRelationStore = new PostgresRelationStore(factory, serializer, migrationRunner);
            var postgresReviewStore = new PostgresRelationReviewStore(factory, serializer, migrationRunner);
            var postgresDiagnosticsStore = new PostgresRelationDiagnosticsStore(factory, serializer, migrationRunner);
            var fileRoot = Path.Combine(Path.GetTempPath(), "contextcore-relation-runtime-canary", Guid.NewGuid().ToString("N"));
            var fileOptions = new FileStorageOptions { RootPath = fileRoot };
            var filePaths = new FilePathResolver(fileOptions);
            var fileSerializer = new FileFormatSerializer();
            var fileRelationStore = new FileRelationStore(fileOptions);
            var fileReviewStore = new FileRelationReviewStore(filePaths, fileSerializer);
            var fileDiagnosticsStore = new FileRelationDiagnosticsStore(filePaths, fileSerializer);
            var artifactWriter = new TraceArtifactWriter(new FileArtifactStore(new FileStorageOptions()));

            try
            {
                var canaryRunner = new RelationGovernanceCanaryRunner(
                    fileRelationStore,
                    fileReviewStore,
                    fileDiagnosticsStore,
                    postgresRelationStore,
                    postgresReviewStore,
                    postgresDiagnosticsStore,
                    new RelationGovernanceCanaryOptions
                    {
                        Enabled = true,
                        WorkspaceAllowlist = [workspaceId],
                        CollectionAllowlist = [collectionId],
                        Mode = RelationGovernanceProviderMode.GuardedPostgresPrimary,
                        FallbackToFileSystem = true,
                        ContinueComparisonTrace = true,
                        FailClosedOnMismatch = true,
                        RequireProviderSwitchGate = true
                    },
                    providerSwitchGate?.Passed == true,
                    async (trace, token) =>
                    {
                        await AppendJsonLineFileAsync(tracePath, trace, token).ConfigureAwait(false);
                        await artifactWriter.AppendTraceJsonLineAsync(
                            trace.WorkspaceId,
                            trace.CollectionId,
                            ArtifactKind.TraceRelationProviderSwitch,
                            trace,
                            operationId: trace.OperationId,
                            capabilityId: "relation-governance",
                            reportId: "relation-runtime-canary-traces",
                            cancellationToken: token).ConfigureAwait(false);
                    });

                report = await canaryRunner.RunAsync(workspaceId, collectionId, cleanupConfirm, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (Directory.Exists(fileRoot))
                {
                    Directory.Delete(fileRoot, recursive: true);
                }
            }
        }

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresRelationRuntimeCanaryMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresRelationRuntimeCanary] Report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresRelationRuntimeCanary] Recommendation={report.Recommendation}, Reads={report.PostgresPrimaryReadCount}, Writes={report.PostgresPrimaryWriteCount}, Mismatches={report.MismatchCount}");
    }

private static async Task ExecutePostgresRelationScopedServiceModeSmokeAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-scoped-service-mode-smoke-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-scoped-service-mode-smoke-report.md");
        var tracePath = CommandHelpers.GetOption(args, "--trace-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-scoped-service-mode-traces.jsonl");
        var cleanupConfirm = CommandHelpers.HasFlag(args, "--cleanup-confirm");
        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? "contextcore_scoped_service";
        var collectionId = CommandHelpers.GetOption(args, "--collection") ?? "relation-governance-scoped-service";
        var options = BuildCliPostgresOptions(args);
        var storageDiagnostics = await BuildCliPostgresDiagnosticsAsync(args, cancellationToken).ConfigureAwait(false);
        var readinessGate = await ReadJsonFileAsync<PostgresRelationGovernanceReadinessGateReport>(
            Path.Combine("storage", "postgres", "postgres-relation-governance-readiness-gate.json"),
            cancellationToken).ConfigureAwait(false);
        var providerSwitchGate = await ReadJsonFileAsync<PostgresRelationProviderSwitchGateReport>(
            Path.Combine("storage", "postgres", "postgres-relation-provider-switch-gate.json"),
            cancellationToken).ConfigureAwait(false);
        var runtimeCanary = await ReadJsonFileAsync<PostgresRelationRuntimeCanaryReport>(
            Path.Combine("storage", "postgres", "postgres-relation-runtime-canary-report.json"),
            cancellationToken).ConfigureAwait(false);

        var blocked = new List<string>();
        var diagnostics = new List<string>();
        AddReasonIfFalse(blocked, options.Enabled, "NotConfigured");
        AddReasonIfFalse(blocked, string.Equals(storageDiagnostics.Status, "Ready", StringComparison.OrdinalIgnoreCase), "PostgresStorageNotReady");
        AddReasonIfFalse(blocked, readinessGate?.Passed == true, "GovernanceReadinessGateNotPassed");
        AddReasonIfFalse(blocked, providerSwitchGate?.Passed == true, "ProviderSwitchGateNotPassed");
        AddReasonIfFalse(blocked, string.Equals(runtimeCanary?.Recommendation, "ReadyForScopedServiceMode", StringComparison.OrdinalIgnoreCase), "RuntimeCanaryNotPassed");

        PostgresRelationScopedServiceModeSmokeReport report;
        if (blocked.Count > 0)
        {
            report = new PostgresRelationScopedServiceModeSmokeReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ProviderMode = RelationGovernanceProviderMode.GuardedPostgresPrimary.ToString(),
                GatePassed = false,
                CleanupPerformed = cleanupConfirm,
                Diagnostics = diagnostics,
                BlockedReasons = blocked,
                Recommendation = options.Enabled ? "GateNotPassed" : "NotConfigured"
            };
        }
        else
        {
            await using var factory = new PostgresConnectionFactory(options);
            var serializer = new PostgresJsonSerializer();
            var migrationRunner = new PostgresMigrationRunner(factory);
            var postgresRelationStore = new PostgresRelationStore(factory, serializer, migrationRunner);
            var postgresReviewStore = new PostgresRelationReviewStore(factory, serializer, migrationRunner);
            var postgresDiagnosticsStore = new PostgresRelationDiagnosticsStore(factory, serializer, migrationRunner);
            var fileRoot = Path.Combine(Path.GetTempPath(), "contextcore-relation-scoped-service-mode", Guid.NewGuid().ToString("N"));
            var fileOptions = new FileStorageOptions { RootPath = fileRoot };
            var filePaths = new FilePathResolver(fileOptions);
            var fileSerializer = new FileFormatSerializer();
            var fileRelationStore = new FileRelationStore(fileOptions);
            var fileReviewStore = new FileRelationReviewStore(filePaths, fileSerializer);
            var fileDiagnosticsStore = new FileRelationDiagnosticsStore(filePaths, fileSerializer);

            try
            {
                var traces = new List<RelationGovernanceProviderSwitchTrace>();
                var runner = new RelationGovernanceCanaryRunner(
                    fileRelationStore,
                    fileReviewStore,
                    fileDiagnosticsStore,
                    postgresRelationStore,
                    postgresReviewStore,
                    postgresDiagnosticsStore,
                    new RelationGovernanceCanaryOptions
                    {
                        Enabled = true,
                        WorkspaceAllowlist = [workspaceId],
                        CollectionAllowlist = [collectionId],
                        Mode = RelationGovernanceProviderMode.GuardedPostgresPrimary,
                        FallbackToFileSystem = true,
                        ContinueComparisonTrace = true,
                        FailClosedOnMismatch = true,
                        RequireProviderSwitchGate = true,
                        RequireRuntimeCanaryPassed = true
                    },
                    providerSwitchGate?.Passed == true,
                    async (trace, token) =>
                    {
                        traces.Add(trace);
                        await AppendJsonLineFileAsync(tracePath, trace, token).ConfigureAwait(false);
                    });

                var canary = await runner.RunAsync(workspaceId, collectionId, cleanupConfirm, cancellationToken)
                    .ConfigureAwait(false);
                var nonAllowlistedRelation = CreateParityRelation(
                    "scoped-non-allowlisted-relation",
                    "contextcore_scoped_service_outside",
                    collectionId,
                    "outside-source",
                    "outside-target",
                    ContextRelationTypes.References,
                    0.1,
                    1.0,
                    DateTimeOffset.UtcNow,
                    "Active",
                    "Reviewed");
                await fileRelationStore.SaveAsync(nonAllowlistedRelation, cancellationToken).ConfigureAwait(false);
                var fileOnly = await fileRelationStore.GetAsync(
                    nonAllowlistedRelation.WorkspaceId,
                    nonAllowlistedRelation.CollectionId,
                    nonAllowlistedRelation.Id,
                    cancellationToken).ConfigureAwait(false);

                var allowlistedUsedPostgres = canary.PostgresPrimaryReadCount > 0 && canary.PostgresPrimaryWriteCount > 0;
                var nonAllowlistedFileSystem = fileOnly?.Id == nonAllowlistedRelation.Id;
                var fallbackTested = canary.Diagnostics.Contains("RuntimeProviderStillScopedCanaryOnly", StringComparer.OrdinalIgnoreCase);
                var comparisonTrace = canary.ComparisonTraceCount > 0 || traces.Count > 0;
                var mismatches = canary.MismatchCount;
                var failures = canary.PostgresFailureCount;
                var smokeBlocked = new List<string>();
                AddReasonIfFalse(smokeBlocked, allowlistedUsedPostgres, "AllowlistedScopeDidNotUsePostgresPrimary");
                AddReasonIfFalse(smokeBlocked, nonAllowlistedFileSystem, "NonAllowlistedScopeDidNotRemainFileSystem");
                AddReasonIfFalse(smokeBlocked, fallbackTested, "FallbackPathNotVerified");
                AddReasonIfFalse(smokeBlocked, comparisonTrace, "ComparisonTraceMissing");
                AddReasonIfFalse(smokeBlocked, mismatches == 0, "MismatchDetected");
                AddReasonIfFalse(smokeBlocked, failures == 0, "PostgresFailureDetected");
                AddReasonIfFalse(smokeBlocked, cleanupConfirm, "CleanupNotConfirmed");

                report = new PostgresRelationScopedServiceModeSmokeReport
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    ProviderMode = RelationGovernanceProviderMode.GuardedPostgresPrimary.ToString(),
                    GatePassed = providerSwitchGate?.Passed == true,
                    AllowlistedScopeUsedPostgresPrimary = allowlistedUsedPostgres,
                    NonAllowlistedScopeUsedFileSystem = nonAllowlistedFileSystem,
                    FallbackTested = fallbackTested,
                    ComparisonTraceRecorded = comparisonTrace,
                    MismatchCount = mismatches,
                    PostgresFailureCount = failures,
                    CleanupPerformed = cleanupConfirm,
                    Diagnostics = ["GlobalDefaultStillOff", "RuntimeProviderScopedOnly"],
                    BlockedReasons = smokeBlocked,
                    Recommendation = smokeBlocked.Count == 0 ? "ReadyForScopedServiceMode" : "NeedsScopedModeFix"
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

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresRelationScopedServiceModeSmokeMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresRelationScopedServiceMode] Smoke: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresRelationScopedServiceMode] Recommendation={report.Recommendation}, Mismatches={report.MismatchCount}, Failures={report.PostgresFailureCount}");
    }

private static async Task ExecutePostgresRelationScopedServiceModeGateAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-scoped-service-mode-gate.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-scoped-service-mode-gate.md");
        var report = await BuildPostgresRelationScopedServiceModeGateAsync(cancellationToken).ConfigureAwait(false);

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresRelationScopedServiceModeGateMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresRelationScopedServiceMode] Gate: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresRelationScopedServiceMode] Passed={report.Passed}, Recommendation={report.Recommendation}, Blocked={report.BlockedReasons.Count}");
    }

private static async Task ExecutePostgresRelationScopedExtendedCanaryAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-scoped-extended-canary-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-scoped-extended-canary-report.md");
        var tracePath = CommandHelpers.GetOption(args, "--trace-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-scoped-extended-canary-traces.jsonl");
        var cleanupConfirm = CommandHelpers.HasFlag(args, "--cleanup-confirm");
        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? "contextcore_canary";
        var collectionId = CommandHelpers.GetOption(args, "--collection") ?? "relation-governance-extended-canary";
        ResetTraceOutput(tracePath);
        var options = BuildCliPostgresOptions(args);
        var storageDiagnostics = await BuildCliPostgresDiagnosticsAsync(args, cancellationToken).ConfigureAwait(false);
        var scopedGate = await ReadJsonFileAsync<PostgresRelationScopedServiceModeGateReport>(
            Path.Combine("storage", "postgres", "postgres-relation-scoped-service-mode-gate.json"),
            cancellationToken).ConfigureAwait(false);

        var blocked = new List<string>();
        var diagnostics = new List<string>();
        AddReasonIfFalse(blocked, options.Enabled, "NotConfigured");
        AddReasonIfFalse(blocked, string.Equals(storageDiagnostics.Status, "Ready", StringComparison.OrdinalIgnoreCase), "PostgresStorageNotReady");
        AddReasonIfFalse(blocked, scopedGate?.Passed == true, "ScopedServiceModeGateNotPassed");

        PostgresRelationScopedExtendedCanaryReport report;
        if (blocked.Count > 0)
        {
            if (scopedGate is null)
            {
                diagnostics.Add("RunPostgresRelationScopedServiceModeGateFirst");
            }

            report = new PostgresRelationScopedExtendedCanaryReport
            {
                GatePassed = false,
                ProviderMode = RelationGovernanceProviderMode.GuardedPostgresPrimary.ToString(),
                CanaryScope = $"{workspaceId}/{collectionId}",
                CleanupPerformed = cleanupConfirm,
                Diagnostics = diagnostics,
                BlockedReasons = blocked,
                Recommendation = options.Enabled ? "GateNotPassed" : "NotConfigured"
            };
        }
        else
        {
            await using var factory = new PostgresConnectionFactory(options);
            var serializer = new PostgresJsonSerializer();
            var migrationRunner = new PostgresMigrationRunner(factory);
            var postgresRelationStore = new PostgresRelationStore(factory, serializer, migrationRunner);
            var postgresReviewStore = new PostgresRelationReviewStore(factory, serializer, migrationRunner);
            var postgresDiagnosticsStore = new PostgresRelationDiagnosticsStore(factory, serializer, migrationRunner);
            var fileRoot = Path.Combine(Path.GetTempPath(), "contextcore-relation-scoped-extended-canary", Guid.NewGuid().ToString("N"));
            var fileOptions = new FileStorageOptions { RootPath = fileRoot };
            var filePaths = new FilePathResolver(fileOptions);
            var fileSerializer = new FileFormatSerializer();
            var fileRelationStore = new FileRelationStore(fileOptions);
            var fileReviewStore = new FileRelationReviewStore(filePaths, fileSerializer);
            var fileDiagnosticsStore = new FileRelationDiagnosticsStore(filePaths, fileSerializer);

            try
            {
                var runner = new RelationGovernanceExtendedCanaryRunner(
                    fileRelationStore,
                    fileReviewStore,
                    fileDiagnosticsStore,
                    postgresRelationStore,
                    postgresReviewStore,
                    postgresDiagnosticsStore,
                    new RelationGovernanceExtendedCanaryOptions
                    {
                        Enabled = true,
                        WorkspaceAllowlist = [workspaceId],
                        CollectionAllowlist = [collectionId],
                        Mode = RelationGovernanceProviderMode.GuardedPostgresPrimary,
                        FallbackToFileSystem = true,
                        ContinueComparisonTrace = true,
                        FailClosedOnMismatch = true,
                        RequireScopedServiceModeGate = true
                    },
                    scopedGate?.Passed == true,
                    (trace, token) => AppendJsonLineFileAsync(tracePath, trace, token));

                report = await runner.RunAsync(workspaceId, collectionId, cleanupConfirm, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (Directory.Exists(fileRoot))
                {
                    Directory.Delete(fileRoot, recursive: true);
                }
            }
        }

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresRelationScopedExtendedCanaryMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresRelationScopedExtendedCanary] Report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresRelationScopedExtendedCanary] Recommendation={report.Recommendation}, Operations={report.OperationCount}, Mismatches={report.MismatchCount}, Failures={report.PostgresFailureCount}");
    }

private static async Task ExecutePostgresRelationSelectedWorkspaceCanaryAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-selected-workspace-canary-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-selected-workspace-canary-report.md");
        var tracePath = CommandHelpers.GetOption(args, "--trace-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-selected-workspace-canary-traces.jsonl");
        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? "contextcore_selected_canary";
        var collectionId = CommandHelpers.GetOption(args, "--collection") ?? "relation-governance-selected-canary";
        var maxOperations = ParseIntOption(args, "--max-operations", 100);
        var observationWindowMinutes = ParseIntOption(args, "--observation-window-minutes", 30);
        ResetTraceOutput(tracePath);
        var options = BuildCliPostgresOptions(args);
        var storageDiagnostics = await BuildCliPostgresDiagnosticsAsync(args, cancellationToken).ConfigureAwait(false);
        var readinessGate = await ReadJsonFileAsync<PostgresRelationGovernanceReadinessGateReport>(
            Path.Combine("storage", "postgres", "postgres-relation-governance-readiness-gate.json"),
            cancellationToken).ConfigureAwait(false);
        var providerSwitchGate = await ReadJsonFileAsync<PostgresRelationProviderSwitchGateReport>(
            Path.Combine("storage", "postgres", "postgres-relation-provider-switch-gate.json"),
            cancellationToken).ConfigureAwait(false);
        var runtimeCanary = await ReadJsonFileAsync<PostgresRelationRuntimeCanaryReport>(
            Path.Combine("storage", "postgres", "postgres-relation-runtime-canary-report.json"),
            cancellationToken).ConfigureAwait(false);
        var scopedGate = await ReadJsonFileAsync<PostgresRelationScopedServiceModeGateReport>(
            Path.Combine("storage", "postgres", "postgres-relation-scoped-service-mode-gate.json"),
            cancellationToken).ConfigureAwait(false);
        var extendedCanary = await ReadJsonFileAsync<PostgresRelationScopedExtendedCanaryReport>(
            Path.Combine("storage", "postgres", "postgres-relation-scoped-extended-canary-report.json"),
            cancellationToken).ConfigureAwait(false);

        var blocked = new List<string>();
        var diagnostics = new List<string>();
        var p15Passed = IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-a3.json"))
                        && IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-extended.json"));
        AddReasonIfFalse(blocked, options.Enabled, "NotConfigured");
        AddReasonIfFalse(blocked, string.Equals(storageDiagnostics.Status, "Ready", StringComparison.OrdinalIgnoreCase), "PostgresStorageNotReady");
        AddReasonIfFalse(blocked, readinessGate?.Passed == true, "GovernanceReadinessGateNotPassed");
        AddReasonIfFalse(blocked, providerSwitchGate?.Passed == true, "ProviderSwitchGateNotPassed");
        AddReasonIfFalse(blocked, string.Equals(runtimeCanary?.Recommendation, "ReadyForScopedServiceMode", StringComparison.OrdinalIgnoreCase), "RuntimeCanaryNotPassed");
        AddReasonIfFalse(blocked, scopedGate?.Passed == true, "ScopedServiceModeGateNotPassed");
        AddReasonIfFalse(blocked, string.Equals(extendedCanary?.Recommendation, "ReadyForSelectedWorkspaceCanary", StringComparison.OrdinalIgnoreCase), "ExtendedCanaryNotPassed");
        AddReasonIfFalse(blocked, !string.IsNullOrWhiteSpace(workspaceId), "SelectedWorkspaceMissing");
        AddReasonIfFalse(blocked, !string.IsNullOrWhiteSpace(collectionId), "SelectedCollectionMissing");
        AddReasonIfFalse(blocked, p15Passed, "P15GateNotPassed");
        if (extendedCanary is null)
        {
            diagnostics.Add("RunPostgresRelationScopedExtendedCanaryFirst");
        }

        PostgresRelationSelectedWorkspaceCanaryReport report;
        if (blocked.Count > 0)
        {
            report = new PostgresRelationSelectedWorkspaceCanaryReport
            {
                GatePassed = false,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ProviderMode = RelationGovernanceProviderMode.GuardedPostgresPrimary.ToString(),
                RollbackInstruction = "Keep RelationGovernanceProviderSwitchOptions.Enabled=false or remove selected workspace/collection from allowlist.",
                Diagnostics = diagnostics,
                BlockedReasons = blocked,
                Recommendation = options.Enabled ? "GateNotPassed" : "NotConfigured"
            };
        }
        else
        {
            await using var factory = new PostgresConnectionFactory(options);
            var serializer = new PostgresJsonSerializer();
            var migrationRunner = new PostgresMigrationRunner(factory);
            var postgresRelationStore = new PostgresRelationStore(factory, serializer, migrationRunner);
            var postgresReviewStore = new PostgresRelationReviewStore(factory, serializer, migrationRunner);
            var postgresDiagnosticsStore = new PostgresRelationDiagnosticsStore(factory, serializer, migrationRunner);
            var fileRoot = Path.Combine(Path.GetTempPath(), "contextcore-relation-selected-workspace-canary", Guid.NewGuid().ToString("N"));
            var fileOptions = new FileStorageOptions { RootPath = fileRoot };
            var filePaths = new FilePathResolver(fileOptions);
            var fileSerializer = new FileFormatSerializer();
            var fileRelationStore = new FileRelationStore(fileOptions);
            var fileReviewStore = new FileRelationReviewStore(filePaths, fileSerializer);
            var fileDiagnosticsStore = new FileRelationDiagnosticsStore(filePaths, fileSerializer);

            try
            {
                var runner = new RelationGovernanceSelectedWorkspaceCanaryRunner(
                    fileRelationStore,
                    fileReviewStore,
                    fileDiagnosticsStore,
                    postgresRelationStore,
                    postgresReviewStore,
                    postgresDiagnosticsStore,
                    new RelationGovernanceSelectedWorkspaceCanaryOptions
                    {
                        Enabled = true,
                        WorkspaceId = workspaceId,
                        CollectionId = collectionId,
                        Mode = RelationGovernanceProviderMode.GuardedPostgresPrimary,
                        FallbackToFileSystem = true,
                        ContinueComparisonTrace = true,
                        FailClosedOnMismatch = true,
                        RequireExtendedCanaryPassed = true,
                        MaxOperations = maxOperations,
                        ObservationWindowMinutes = observationWindowMinutes
                    },
                    preflightGatePassed: true,
                    (trace, token) => AppendJsonLineFileAsync(tracePath, trace, token));

                report = await runner.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (Directory.Exists(fileRoot))
                {
                    Directory.Delete(fileRoot, recursive: true);
                }
            }
        }

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresRelationSelectedWorkspaceCanaryMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresRelationSelectedWorkspaceCanary] Report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresRelationSelectedWorkspaceCanary] Recommendation={report.Recommendation}, Operations={report.OperationCount}, Mismatches={report.MismatchCount}, Failures={report.PostgresFailureCount}");
    }

private static async Task ExecutePostgresRelationScopedExpansionPlanAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-scoped-expansion-plan.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-scoped-expansion-plan.md");
        var scopes = BuildDefaultScopedExpansionRules();
        var preflight = await BuildPostgresRelationScopedExpansionPreflightAsync(args, cancellationToken).ConfigureAwait(false);
        var runner = new RelationGovernanceScopedExpansionRunner(
            new FileRelationStore(new FileStorageOptions { RootPath = Path.Combine(Path.GetTempPath(), "contextcore-relation-expansion-plan") }),
            new FileRelationReviewStore(new FilePathResolver(new FileStorageOptions { RootPath = Path.Combine(Path.GetTempPath(), "contextcore-relation-expansion-plan") }), new FileFormatSerializer()),
            new FileRelationDiagnosticsStore(new FilePathResolver(new FileStorageOptions { RootPath = Path.Combine(Path.GetTempPath(), "contextcore-relation-expansion-plan") }), new FileFormatSerializer()),
            null!,
            null!,
            null!,
            scopes,
            preflight.Passed,
            (_, _) => Task.CompletedTask);
        var plans = runner.BuildPlans();
        var report = new PostgresRelationScopedExpansionReport
        {
            GatePassed = preflight.Passed,
            ScopeCount = scopes.Count,
            AllowlistedScopeCount = scopes.Count(static scope => scope.Enabled),
            NonAllowlistedScopeChecked = false,
            Plans = plans,
            Diagnostics = preflight.Diagnostics,
            BlockedReasons = preflight.BlockedReasons,
            Recommendation = preflight.Passed ? "ReadyForScopedExpansion" : "GateNotPassed"
        };

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresRelationScopedExpansionMarkdown(report, "Plan"), markdownPath, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[PostgresRelationScopedExpansion] Plan: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresRelationScopedExpansion] Recommendation={report.Recommendation}, Scopes={report.ScopeCount}, Blocked={report.BlockedReasons.Count}");
    }

private static async Task ExecutePostgresRelationScopedExpansionSmokeAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-scoped-expansion-smoke-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-scoped-expansion-smoke-report.md");
        var tracePath = CommandHelpers.GetOption(args, "--trace-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-scoped-expansion-traces.jsonl");
        var cleanupConfirm = CommandHelpers.HasFlag(args, "--cleanup-confirm");
        ResetTraceOutput(tracePath);
        var scopes = BuildDefaultScopedExpansionRules();
        var options = BuildCliPostgresOptions(args);
        var preflight = await BuildPostgresRelationScopedExpansionPreflightAsync(args, cancellationToken).ConfigureAwait(false);

        PostgresRelationScopedExpansionReport report;
        if (!options.Enabled || !preflight.Passed)
        {
            report = new PostgresRelationScopedExpansionReport
            {
                GatePassed = false,
                ScopeCount = scopes.Count,
                AllowlistedScopeCount = scopes.Count(static scope => scope.Enabled),
                Plans = BuildScopedExpansionPlans(scopes, preflight.Passed),
                Diagnostics = preflight.Diagnostics,
                BlockedReasons = preflight.BlockedReasons,
                Recommendation = options.Enabled ? "GateNotPassed" : "NotConfigured"
            };
        }
        else
        {
            await using var factory = new PostgresConnectionFactory(options);
            var serializer = new PostgresJsonSerializer();
            var migrationRunner = new PostgresMigrationRunner(factory);
            var postgresRelationStore = new PostgresRelationStore(factory, serializer, migrationRunner);
            var postgresReviewStore = new PostgresRelationReviewStore(factory, serializer, migrationRunner);
            var postgresDiagnosticsStore = new PostgresRelationDiagnosticsStore(factory, serializer, migrationRunner);
            var fileRoot = Path.Combine(Path.GetTempPath(), "contextcore-relation-scoped-expansion", Guid.NewGuid().ToString("N"));
            var fileOptions = new FileStorageOptions { RootPath = fileRoot };
            var filePaths = new FilePathResolver(fileOptions);
            var fileSerializer = new FileFormatSerializer();
            var fileRelationStore = new FileRelationStore(fileOptions);
            var fileReviewStore = new FileRelationReviewStore(filePaths, fileSerializer);
            var fileDiagnosticsStore = new FileRelationDiagnosticsStore(filePaths, fileSerializer);

            try
            {
                var runner = new RelationGovernanceScopedExpansionRunner(
                    fileRelationStore,
                    fileReviewStore,
                    fileDiagnosticsStore,
                    postgresRelationStore,
                    postgresReviewStore,
                    postgresDiagnosticsStore,
                    scopes,
                    preflight.Passed,
                    (trace, token) => AppendJsonLineFileAsync(tracePath, trace, token));
                report = await runner.RunSmokeAsync(cleanupConfirm, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (Directory.Exists(fileRoot))
                {
                    Directory.Delete(fileRoot, recursive: true);
                }
            }
        }

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresRelationScopedExpansionMarkdown(report, "Smoke"), markdownPath, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[PostgresRelationScopedExpansion] Smoke: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresRelationScopedExpansion] Recommendation={report.Recommendation}, Operations={report.OperationCount}, Mismatches={report.MismatchCount}, Failures={report.PostgresFailureCount}");
    }

private static async Task ExecutePostgresRelationScopedExpansionGateAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-scoped-expansion-gate.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-scoped-expansion-gate.md");
        var scopes = BuildDefaultScopedExpansionRules();
        var preflight = await BuildPostgresRelationScopedExpansionPreflightAsync(args, cancellationToken).ConfigureAwait(false);
        var smoke = await ReadJsonFileAsync<PostgresRelationScopedExpansionReport>(
            Path.Combine("storage", "postgres", "postgres-relation-scoped-expansion-smoke-report.json"),
            cancellationToken).ConfigureAwait(false);
        var p15Passed = IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-a3.json"))
                        && IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-extended.json"));
        var runner = new RelationGovernanceScopedExpansionRunner(
            new FileRelationStore(new FileStorageOptions { RootPath = Path.Combine(Path.GetTempPath(), "contextcore-relation-expansion-gate") }),
            new FileRelationReviewStore(new FilePathResolver(new FileStorageOptions { RootPath = Path.Combine(Path.GetTempPath(), "contextcore-relation-expansion-gate") }), new FileFormatSerializer()),
            new FileRelationDiagnosticsStore(new FilePathResolver(new FileStorageOptions { RootPath = Path.Combine(Path.GetTempPath(), "contextcore-relation-expansion-gate") }), new FileFormatSerializer()),
            null!,
            null!,
            null!,
            scopes,
            preflight.Passed,
            (_, _) => Task.CompletedTask);
        var report = runner.BuildGateReport(smoke, p15Passed);
        if (preflight.BlockedReasons.Count > 0)
        {
            report = new PostgresRelationScopedExpansionReport
            {
                GatePassed = false,
                ScopeCount = report.ScopeCount,
                AllowlistedScopeCount = report.AllowlistedScopeCount,
                NonAllowlistedScopeChecked = report.NonAllowlistedScopeChecked,
                OperationCount = report.OperationCount,
                PostgresPrimaryReadCount = report.PostgresPrimaryReadCount,
                PostgresPrimaryWriteCount = report.PostgresPrimaryWriteCount,
                FileSystemScopeReadCount = report.FileSystemScopeReadCount,
                FallbackCount = report.FallbackCount,
                ComparisonTraceCount = report.ComparisonTraceCount,
                MismatchCount = report.MismatchCount,
                PostgresFailureCount = report.PostgresFailureCount,
                AveragePostgresReadMs = report.AveragePostgresReadMs,
                AveragePostgresWriteMs = report.AveragePostgresWriteMs,
                Plans = report.Plans,
                PerScopeStatus = report.PerScopeStatus,
                Diagnostics = report.Diagnostics.Concat(preflight.Diagnostics).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                BlockedReasons = report.BlockedReasons.Concat(preflight.BlockedReasons).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Recommendation = "GateNotPassed"
            };
        }

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresRelationScopedExpansionMarkdown(report, "Gate"), markdownPath, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[PostgresRelationScopedExpansion] Gate: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresRelationScopedExpansion] Passed={report.GatePassed}, Recommendation={report.Recommendation}, Blocked={report.BlockedReasons.Count}");
    }

private static async Task ExecutePostgresRelationScopedObservationWindowAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-scoped-observation-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-scoped-observation-report.md");
        var tracePath = CommandHelpers.GetOption(args, "--trace-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-scoped-observation-traces.jsonl");
        var cleanupConfirm = CommandHelpers.HasFlag(args, "--cleanup-confirm");
        ResetTraceOutput(tracePath);

        var scopes = BuildDefaultScopedExpansionRules();
        var postgresOptions = BuildCliPostgresOptions(args);
        var preflight = await BuildPostgresRelationScopedExpansionPreflightAsync(args, cancellationToken).ConfigureAwait(false);
        var scopedExpansionGate = await ReadJsonFileAsync<PostgresRelationScopedExpansionReport>(
            Path.Combine("storage", "postgres", "postgres-relation-scoped-expansion-gate.json"),
            cancellationToken).ConfigureAwait(false);
        var maxOperations = ParseIntOption(args, "--max-operations", 100);
        var observationOptions = new RelationGovernanceScopedObservationOptions
        {
            Enabled = true,
            ObservationWindowMinutes = ParseIntOption(args, "--observation-window-minutes", 30),
            OperationIntervalSeconds = ParseIntOption(args, "--operation-interval-seconds", 1),
            MaxOperations = maxOperations,
            ScopedRules = scopes,
            FallbackToFileSystem = true,
            ContinueComparisonTrace = true,
            FailClosedOnMismatch = true,
            CleanupAfterRun = cleanupConfirm,
            RequireScopedExpansionGate = true
        };

        PostgresRelationScopedObservationReport report;
        if (!postgresOptions.Enabled || !preflight.Passed || scopedExpansionGate?.GatePassed != true)
        {
            report = new PostgresRelationScopedObservationReport
            {
                GatePassed = false,
                ScopeCount = scopes.Count,
                ObservationWindowMinutes = observationOptions.ObservationWindowMinutes,
                OperationIntervalSeconds = observationOptions.OperationIntervalSeconds,
                MaxOperations = observationOptions.MaxOperations,
                Diagnostics = preflight.Diagnostics,
                BlockedReasons = preflight.BlockedReasons
                    .Concat(scopedExpansionGate?.GatePassed == true ? [] : ["ScopedExpansionGateNotPassed"])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                RollbackInstruction = "Keep RelationGovernanceProviderSwitchOptions.Enabled=false for scoped observation.",
                Recommendation = postgresOptions.Enabled ? "GateNotPassed" : "NotConfigured"
            };
        }
        else
        {
            await using var factory = new PostgresConnectionFactory(postgresOptions);
            var serializer = new PostgresJsonSerializer();
            var migrationRunner = new PostgresMigrationRunner(factory);
            var postgresRelationStore = new PostgresRelationStore(factory, serializer, migrationRunner);
            var postgresReviewStore = new PostgresRelationReviewStore(factory, serializer, migrationRunner);
            var postgresDiagnosticsStore = new PostgresRelationDiagnosticsStore(factory, serializer, migrationRunner);
            var fileRoot = Path.Combine(Path.GetTempPath(), "contextcore-relation-scoped-observation", Guid.NewGuid().ToString("N"));
            var fileOptions = new FileStorageOptions { RootPath = fileRoot };
            var filePaths = new FilePathResolver(fileOptions);
            var fileSerializer = new FileFormatSerializer();
            var fileRelationStore = new FileRelationStore(fileOptions);
            var fileReviewStore = new FileRelationReviewStore(filePaths, fileSerializer);
            var fileDiagnosticsStore = new FileRelationDiagnosticsStore(filePaths, fileSerializer);

            try
            {
                var runner = new RelationGovernanceScopedObservationRunner(
                    fileRelationStore,
                    fileReviewStore,
                    fileDiagnosticsStore,
                    postgresRelationStore,
                    postgresReviewStore,
                    postgresDiagnosticsStore,
                    observationOptions,
                    scopedExpansionGate.GatePassed,
                    (trace, token) => AppendJsonLineFileAsync(tracePath, trace, token));
                report = await runner.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (Directory.Exists(fileRoot))
                {
                    Directory.Delete(fileRoot, recursive: true);
                }
            }
        }

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresRelationScopedObservationMarkdown(report, "Report"), markdownPath, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[PostgresRelationScopedObservation] Window: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresRelationScopedObservation] Recommendation={report.Recommendation}, Operations={report.OperationCount}, Mismatches={report.MismatchCount}, Failures={report.PostgresFailureCount}, ScopeLeaks={report.NonAllowlistedScopeLeakCount}");
    }

private static async Task ExecutePostgresRelationScopedObservationQualityAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-scoped-observation-quality-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-scoped-observation-quality-report.md");
        var p95ThresholdMs = ParseIntOption(args, "--p95-threshold-ms", 5000);
        var scopes = BuildDefaultScopedExpansionRules();
        var preflight = await BuildPostgresRelationScopedExpansionPreflightAsync(args, cancellationToken).ConfigureAwait(false);
        var observation = await ReadJsonFileAsync<PostgresRelationScopedObservationReport>(
            Path.Combine("storage", "postgres", "postgres-relation-scoped-observation-report.json"),
            cancellationToken).ConfigureAwait(false);
        var p15Passed = IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-a3.json"))
                        && IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-extended.json"));
        var runner = new RelationGovernanceScopedObservationRunner(
            new FileRelationStore(new FileStorageOptions { RootPath = Path.Combine(Path.GetTempPath(), "contextcore-relation-observation-quality") }),
            new FileRelationReviewStore(new FilePathResolver(new FileStorageOptions { RootPath = Path.Combine(Path.GetTempPath(), "contextcore-relation-observation-quality") }), new FileFormatSerializer()),
            new FileRelationDiagnosticsStore(new FilePathResolver(new FileStorageOptions { RootPath = Path.Combine(Path.GetTempPath(), "contextcore-relation-observation-quality") }), new FileFormatSerializer()),
            null!,
            null!,
            null!,
            new RelationGovernanceScopedObservationOptions { Enabled = true, ScopedRules = scopes },
            preflight.Passed,
            (_, _) => Task.CompletedTask);
        var report = runner.BuildQualityReport(observation, p15Passed, p95ThresholdMs);

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresRelationScopedObservationMarkdown(report, "Quality"), markdownPath, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[PostgresRelationScopedObservation] Quality: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresRelationScopedObservation] Passed={report.GatePassed}, Recommendation={report.Recommendation}, Blocked={report.BlockedReasons.Count}");
    }

private static async Task ExecutePostgresRelationSelectedNormalWorkspaceCanaryAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-selected-normal-workspace-canary-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-selected-normal-workspace-canary-report.md");
        var tracePath = CommandHelpers.GetOption(args, "--trace-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-selected-normal-workspace-canary-traces.jsonl");
        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? "contextcore_selected_normal";
        var collectionId = CommandHelpers.GetOption(args, "--collection") ?? "relation-governance-selected-normal";
        var maxOperations = ParseIntOption(args, "--max-operations", 100);
        var observationWindowMinutes = ParseIntOption(args, "--observation-window-minutes", 30);
        var cleanupMode = ParseSelectedNormalCleanupMode(args);
        ResetTraceOutput(tracePath);

        var postgresOptions = BuildCliPostgresOptions(args);
        var storageDiagnostics = await BuildCliPostgresDiagnosticsAsync(args, cancellationToken).ConfigureAwait(false);
        var preflight = await BuildPostgresRelationScopedExpansionPreflightAsync(args, cancellationToken).ConfigureAwait(false);
        var providerSwitchGate = await ReadJsonFileAsync<PostgresRelationProviderSwitchGateReport>(
            Path.Combine("storage", "postgres", "postgres-relation-provider-switch-gate.json"),
            cancellationToken).ConfigureAwait(false);
        var runtimeCanary = await ReadJsonFileAsync<PostgresRelationRuntimeCanaryReport>(
            Path.Combine("storage", "postgres", "postgres-relation-runtime-canary-report.json"),
            cancellationToken).ConfigureAwait(false);
        var scopedGate = await ReadJsonFileAsync<PostgresRelationScopedServiceModeGateReport>(
            Path.Combine("storage", "postgres", "postgres-relation-scoped-service-mode-gate.json"),
            cancellationToken).ConfigureAwait(false);
        var extendedCanary = await ReadJsonFileAsync<PostgresRelationScopedExtendedCanaryReport>(
            Path.Combine("storage", "postgres", "postgres-relation-scoped-extended-canary-report.json"),
            cancellationToken).ConfigureAwait(false);
        var selectedWorkspaceCanary = await ReadJsonFileAsync<PostgresRelationSelectedWorkspaceCanaryReport>(
            Path.Combine("storage", "postgres", "postgres-relation-selected-workspace-canary-report.json"),
            cancellationToken).ConfigureAwait(false);
        var scopedExpansionGate = await ReadJsonFileAsync<PostgresRelationScopedExpansionReport>(
            Path.Combine("storage", "postgres", "postgres-relation-scoped-expansion-gate.json"),
            cancellationToken).ConfigureAwait(false);
        var scopedObservationQuality = await ReadJsonFileAsync<PostgresRelationScopedObservationReport>(
            Path.Combine("storage", "postgres", "postgres-relation-scoped-observation-quality-report.json"),
            cancellationToken).ConfigureAwait(false);

        var blocked = new List<string>();
        var diagnostics = new List<string>();
        var p15Passed = IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-a3.json"))
                        && IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-extended.json"));
        AddReasonIfFalse(blocked, postgresOptions.Enabled, "NotConfigured");
        AddReasonIfFalse(blocked, string.Equals(storageDiagnostics.Status, "Ready", StringComparison.OrdinalIgnoreCase), "PostgresStorageNotReady");
        AddReasonIfFalse(blocked, preflight.Passed, "GovernancePreflightNotPassed");
        AddReasonIfFalse(blocked, providerSwitchGate?.Passed == true, "ProviderSwitchGateNotPassed");
        AddReasonIfFalse(blocked, string.Equals(runtimeCanary?.Recommendation, "ReadyForScopedServiceMode", StringComparison.OrdinalIgnoreCase), "RuntimeCanaryNotPassed");
        AddReasonIfFalse(blocked, scopedGate?.Passed == true, "ScopedServiceModeGateNotPassed");
        AddReasonIfFalse(blocked, string.Equals(extendedCanary?.Recommendation, "ReadyForSelectedWorkspaceCanary", StringComparison.OrdinalIgnoreCase), "ExtendedCanaryNotPassed");
        AddReasonIfFalse(blocked, string.Equals(selectedWorkspaceCanary?.Recommendation, "ReadyForScopedServiceModeExpansion", StringComparison.OrdinalIgnoreCase), "SelectedWorkspaceCanaryNotPassed");
        AddReasonIfFalse(blocked, scopedExpansionGate?.GatePassed == true, "ScopedExpansionGateNotPassed");
        AddReasonIfFalse(blocked, scopedObservationQuality?.GatePassed == true, "ScopedObservationQualityNotPassed");
        AddReasonIfFalse(blocked, !string.IsNullOrWhiteSpace(workspaceId), "SelectedNormalWorkspaceMissing");
        AddReasonIfFalse(blocked, !string.IsNullOrWhiteSpace(collectionId), "SelectedNormalCollectionMissing");
        AddReasonIfFalse(blocked, p15Passed, "P15GateNotPassed");
        if (scopedObservationQuality is null)
        {
            diagnostics.Add("RunPostgresRelationScopedObservationQualityFirst");
        }

        PostgresRelationSelectedNormalWorkspaceCanaryReport report;
        if (blocked.Count > 0)
        {
            report = new PostgresRelationSelectedNormalWorkspaceCanaryReport
            {
                GatePassed = false,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ProviderMode = RelationGovernanceProviderMode.GuardedPostgresPrimary.ToString(),
                RollbackInstruction = "Keep RelationGovernanceProviderSwitchOptions.Enabled=false or remove selected normal workspace/collection from allowlist.",
                Diagnostics = diagnostics.Concat(preflight.Diagnostics).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                BlockedReasons = blocked.Concat(preflight.BlockedReasons).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Recommendation = postgresOptions.Enabled ? "GateNotPassed" : "NotConfigured"
            };
        }
        else
        {
            await using var factory = new PostgresConnectionFactory(postgresOptions);
            var serializer = new PostgresJsonSerializer();
            var migrationRunner = new PostgresMigrationRunner(factory);
            var postgresRelationStore = new PostgresRelationStore(factory, serializer, migrationRunner);
            var postgresReviewStore = new PostgresRelationReviewStore(factory, serializer, migrationRunner);
            var postgresDiagnosticsStore = new PostgresRelationDiagnosticsStore(factory, serializer, migrationRunner);
            var fileRoot = Path.Combine(Path.GetTempPath(), "contextcore-relation-selected-normal-workspace-canary", Guid.NewGuid().ToString("N"));
            var fileOptions = new FileStorageOptions { RootPath = fileRoot };
            var filePaths = new FilePathResolver(fileOptions);
            var fileSerializer = new FileFormatSerializer();
            var fileRelationStore = new FileRelationStore(fileOptions);
            var fileReviewStore = new FileRelationReviewStore(filePaths, fileSerializer);
            var fileDiagnosticsStore = new FileRelationDiagnosticsStore(filePaths, fileSerializer);

            try
            {
                var runner = new RelationGovernanceSelectedNormalWorkspaceRunner(
                    fileRelationStore,
                    fileReviewStore,
                    fileDiagnosticsStore,
                    postgresRelationStore,
                    postgresReviewStore,
                    postgresDiagnosticsStore,
                    new RelationGovernanceSelectedNormalWorkspaceOptions
                    {
                        Enabled = true,
                        WorkspaceId = workspaceId,
                        CollectionId = collectionId,
                        Mode = RelationGovernanceProviderMode.GuardedPostgresPrimary,
                        FallbackToFileSystem = true,
                        ContinueComparisonTrace = true,
                        FailClosedOnMismatch = true,
                        RequireScopedObservationPassed = true,
                        ObservationWindowMinutes = observationWindowMinutes,
                        MaxOperations = maxOperations,
                        CleanupMode = cleanupMode
                    },
                    scopedObservationGatePassed: true,
                    (trace, token) => AppendJsonLineFileAsync(tracePath, trace, token));

                report = await runner.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (Directory.Exists(fileRoot))
                {
                    Directory.Delete(fileRoot, recursive: true);
                }
            }
        }

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresRelationSelectedNormalWorkspaceCanaryMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresRelationSelectedNormalWorkspaceCanary] Report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresRelationSelectedNormalWorkspaceCanary] Recommendation={report.Recommendation}, Operations={report.OperationCount}, Mismatches={report.MismatchCount}, Failures={report.PostgresFailureCount}, ScopeLeaks={report.ScopeLeakCount}");
    }

private static async Task ExecutePostgresRelationLimitedNormalScopeObservationAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-limited-normal-scope-observation-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-limited-normal-scope-observation-report.md");
        var tracePath = CommandHelpers.GetOption(args, "--trace-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-limited-normal-scope-traces.jsonl");
        var selectedCanary = await ReadJsonFileAsync<PostgresRelationSelectedNormalWorkspaceCanaryReport>(
            Path.Combine("storage", "postgres", "postgres-relation-selected-normal-workspace-canary-report.json"),
            cancellationToken).ConfigureAwait(false);
        var workspaceId = CommandHelpers.GetOption(args, "--workspace")
                          ?? selectedCanary?.WorkspaceId
                          ?? "contextcore_selected_normal";
        var collectionId = CommandHelpers.GetOption(args, "--collection")
                           ?? selectedCanary?.CollectionId
                           ?? "relation-governance-selected-normal";
        var maxOperations = ParseIntOption(args, "--max-operations", 100);
        var observationWindowMinutes = ParseIntOption(args, "--observation-window-minutes", 60);
        var operationIntervalSeconds = ParseIntOption(args, "--operation-interval-seconds", 0);
        var cleanupMode = ParseSelectedNormalCleanupMode(args);
        ResetTraceOutput(tracePath);

        var postgresOptions = BuildCliPostgresOptions(args);
        var selectedNormalPassed = string.Equals(selectedCanary?.Recommendation, "ReadyForLimitedNormalScope", StringComparison.OrdinalIgnoreCase);
        var blocked = new List<string>();
        var diagnostics = new List<string>();
        AddReasonIfFalse(blocked, postgresOptions.Enabled, "NotConfigured");
        AddReasonIfFalse(blocked, selectedNormalPassed, "SelectedNormalWorkspaceCanaryNotPassed");
        AddReasonIfFalse(blocked, !string.IsNullOrWhiteSpace(workspaceId), "LimitedNormalWorkspaceMissing");
        AddReasonIfFalse(blocked, !string.IsNullOrWhiteSpace(collectionId), "LimitedNormalCollectionMissing");
        if (selectedCanary is null)
        {
            diagnostics.Add("RunPostgresRelationSelectedNormalWorkspaceCanaryFirst");
        }

        PostgresRelationLimitedNormalScopeObservationReport report;
        if (blocked.Count > 0)
        {
            report = new PostgresRelationLimitedNormalScopeObservationReport
            {
                GatePassed = false,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ObservationWindowMinutes = observationWindowMinutes,
                OperationIntervalSeconds = operationIntervalSeconds,
                MaxOperations = maxOperations,
                ProviderMode = RelationGovernanceProviderMode.GuardedPostgresPrimary.ToString(),
                RollbackInstruction = "Keep RelationGovernanceProviderSwitchOptions.Enabled=false or remove limited normal scope allowlist.",
                Diagnostics = diagnostics,
                BlockedReasons = blocked,
                Recommendation = postgresOptions.Enabled ? "GateNotPassed" : "NotConfigured"
            };
        }
        else
        {
            await using var factory = new PostgresConnectionFactory(postgresOptions);
            var serializer = new PostgresJsonSerializer();
            var migrationRunner = new PostgresMigrationRunner(factory);
            var postgresRelationStore = new PostgresRelationStore(factory, serializer, migrationRunner);
            var postgresReviewStore = new PostgresRelationReviewStore(factory, serializer, migrationRunner);
            var postgresDiagnosticsStore = new PostgresRelationDiagnosticsStore(factory, serializer, migrationRunner);
            var fileRoot = Path.Combine(Path.GetTempPath(), "contextcore-relation-limited-normal-scope-observation", Guid.NewGuid().ToString("N"));
            var fileOptions = new FileStorageOptions { RootPath = fileRoot };
            var filePaths = new FilePathResolver(fileOptions);
            var fileSerializer = new FileFormatSerializer();
            var fileRelationStore = new FileRelationStore(fileOptions);
            var fileReviewStore = new FileRelationReviewStore(filePaths, fileSerializer);
            var fileDiagnosticsStore = new FileRelationDiagnosticsStore(filePaths, fileSerializer);

            try
            {
                var runner = new RelationGovernanceLimitedNormalScopeObservationRunner(
                    fileRelationStore,
                    fileReviewStore,
                    fileDiagnosticsStore,
                    postgresRelationStore,
                    postgresReviewStore,
                    postgresDiagnosticsStore,
                    new RelationGovernanceLimitedNormalScopeObservationOptions
                    {
                        Enabled = true,
                        WorkspaceId = workspaceId,
                        CollectionId = collectionId,
                        ObservationWindowMinutes = observationWindowMinutes,
                        OperationIntervalSeconds = operationIntervalSeconds,
                        MaxOperations = maxOperations,
                        Mode = RelationGovernanceProviderMode.GuardedPostgresPrimary,
                        FallbackToFileSystem = true,
                        ContinueComparisonTrace = true,
                        FailClosedOnMismatch = true,
                        RequireSelectedNormalCanaryPassed = true,
                        CleanupMode = cleanupMode
                    },
                    selectedNormalCanaryPassed: true,
                    (trace, token) => AppendJsonLineFileAsync(tracePath, trace, token));
                report = await runner.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (Directory.Exists(fileRoot))
                {
                    Directory.Delete(fileRoot, recursive: true);
                }
            }
        }

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresRelationLimitedNormalScopeObservationMarkdown(report, "Report"), markdownPath, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[PostgresRelationLimitedNormalScopeObservation] Report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresRelationLimitedNormalScopeObservation] Recommendation={report.Recommendation}, Operations={report.OperationCount}, Mismatches={report.MismatchCount}, Failures={report.PostgresFailureCount}, ScopeLeaks={report.ScopeLeakCount}");
    }

private static async Task ExecutePostgresRelationLimitedNormalScopeQualityAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-limited-normal-scope-quality-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-limited-normal-scope-quality-report.md");
        var fallbackRateThreshold = ParseDoubleOption(CommandHelpers.GetOption(args, "--fallback-rate-threshold"), 0.05);
        var p95ThresholdMs = ParseDoubleOption(CommandHelpers.GetOption(args, "--p95-threshold-ms"), 5000);
        var selectedCanary = await ReadJsonFileAsync<PostgresRelationSelectedNormalWorkspaceCanaryReport>(
            Path.Combine("storage", "postgres", "postgres-relation-selected-normal-workspace-canary-report.json"),
            cancellationToken).ConfigureAwait(false);
        var observation = await ReadJsonFileAsync<PostgresRelationLimitedNormalScopeObservationReport>(
            Path.Combine("storage", "postgres", "postgres-relation-limited-normal-scope-observation-report.json"),
            cancellationToken).ConfigureAwait(false);
        var p15Passed = IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-a3.json"))
                        && IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-extended.json"));
        var runner = new RelationGovernanceLimitedNormalScopeObservationRunner(
            new FileRelationStore(new FileStorageOptions { RootPath = Path.Combine(Path.GetTempPath(), "contextcore-relation-limited-normal-quality") }),
            new FileRelationReviewStore(new FilePathResolver(new FileStorageOptions { RootPath = Path.Combine(Path.GetTempPath(), "contextcore-relation-limited-normal-quality") }), new FileFormatSerializer()),
            new FileRelationDiagnosticsStore(new FilePathResolver(new FileStorageOptions { RootPath = Path.Combine(Path.GetTempPath(), "contextcore-relation-limited-normal-quality") }), new FileFormatSerializer()),
            null!,
            null!,
            null!,
            new RelationGovernanceLimitedNormalScopeObservationOptions
            {
                Enabled = true,
                WorkspaceId = observation?.WorkspaceId ?? string.Empty,
                CollectionId = observation?.CollectionId ?? string.Empty
            },
            string.Equals(selectedCanary?.Recommendation, "ReadyForLimitedNormalScope", StringComparison.OrdinalIgnoreCase),
            (_, _) => Task.CompletedTask);
        var report = runner.BuildQualityReport(observation, p15Passed, fallbackRateThreshold, p95ThresholdMs);

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresRelationLimitedNormalScopeObservationMarkdown(report, "Quality"), markdownPath, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[PostgresRelationLimitedNormalScopeObservation] Quality: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresRelationLimitedNormalScopeObservation] Passed={report.GatePassed}, Recommendation={report.Recommendation}, Blocked={report.BlockedReasons.Count}");
    }

private static async Task ExecutePostgresRelationMultiNormalScopeCanaryAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-multi-normal-scope-canary-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-multi-normal-scope-canary-report.md");
        var tracePath = CommandHelpers.GetOption(args, "--trace-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-multi-normal-scope-traces.jsonl");
        var maxOperationsPerScope = ParseIntOption(args, "--max-operations-per-scope", 100);
        var observationWindowMinutes = ParseIntOption(args, "--observation-window-minutes", 60);
        var cleanupMode = ParseSelectedNormalCleanupMode(args);
        var scopeRunId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x");
        var scopes = BuildDefaultMultiNormalScopeRules(cleanupMode, scopeRunId);
        ResetTraceOutput(tracePath);

        var postgresOptions = BuildCliPostgresOptions(args);
        var storageDiagnostics = await BuildCliPostgresDiagnosticsAsync(args, cancellationToken).ConfigureAwait(false);
        var preflight = await BuildPostgresRelationScopedExpansionPreflightAsync(args, cancellationToken).ConfigureAwait(false);
        var scopedExpansionGate = await ReadJsonFileAsync<PostgresRelationScopedExpansionReport>(
            Path.Combine("storage", "postgres", "postgres-relation-scoped-expansion-gate.json"),
            cancellationToken).ConfigureAwait(false);
        var selectedNormalCanary = await ReadJsonFileAsync<PostgresRelationSelectedNormalWorkspaceCanaryReport>(
            Path.Combine("storage", "postgres", "postgres-relation-selected-normal-workspace-canary-report.json"),
            cancellationToken).ConfigureAwait(false);
        var limitedNormalQuality = await ReadJsonFileAsync<PostgresRelationLimitedNormalScopeObservationReport>(
            Path.Combine("storage", "postgres", "postgres-relation-limited-normal-scope-quality-report.json"),
            cancellationToken).ConfigureAwait(false);
        var p15Passed = IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-a3.json"))
                        && IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-extended.json"));

        var blocked = new List<string>();
        var diagnostics = new List<string>();
        AddReasonIfFalse(blocked, postgresOptions.Enabled, "NotConfigured");
        AddReasonIfFalse(blocked, string.Equals(storageDiagnostics.Status, "Ready", StringComparison.OrdinalIgnoreCase), "PostgresStorageNotReady");
        AddReasonIfFalse(blocked, preflight.Passed, "GovernancePreflightNotPassed");
        AddReasonIfFalse(blocked, scopedExpansionGate?.GatePassed == true, "ScopedExpansionGateNotPassed");
        AddReasonIfFalse(blocked, string.Equals(selectedNormalCanary?.Recommendation, "ReadyForLimitedNormalScope", StringComparison.OrdinalIgnoreCase), "SelectedNormalWorkspaceCanaryNotPassed");
        AddReasonIfFalse(blocked, limitedNormalQuality?.GatePassed == true, "LimitedNormalScopeObservationQualityNotPassed");
        AddReasonIfFalse(blocked, string.Equals(limitedNormalQuality?.Recommendation, "ReadyForMultiNormalScopeCanary", StringComparison.OrdinalIgnoreCase), "LimitedNormalScopeObservationNotReady");
        AddReasonIfFalse(blocked, scopes.Count(static scope => scope.Enabled) >= 2, "AtLeastTwoNormalScopesRequired");
        AddReasonIfFalse(blocked, p15Passed, "P15GateNotPassed");
        if (limitedNormalQuality is null)
        {
            diagnostics.Add("RunPostgresRelationLimitedNormalScopeQualityFirst");
        }

        PostgresRelationMultiNormalScopeCanaryReport report;
        if (blocked.Count > 0)
        {
            report = new PostgresRelationMultiNormalScopeCanaryReport
            {
                GatePassed = false,
                ScopeCount = scopes.Count,
                EnabledScopeCount = scopes.Count(static scope => scope.Enabled),
                RollbackInstruction = "Keep RelationGovernanceProviderSwitchOptions.Enabled=false or remove multi-normal scope rules.",
                Diagnostics = diagnostics.Concat(preflight.Diagnostics).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                BlockedReasons = blocked.Concat(preflight.BlockedReasons).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Recommendation = postgresOptions.Enabled ? "GateNotPassed" : "NotConfigured"
            };
        }
        else
        {
            await using var factory = new PostgresConnectionFactory(postgresOptions);
            var serializer = new PostgresJsonSerializer();
            var migrationRunner = new PostgresMigrationRunner(factory);
            var postgresRelationStore = new PostgresRelationStore(factory, serializer, migrationRunner);
            var postgresReviewStore = new PostgresRelationReviewStore(factory, serializer, migrationRunner);
            var postgresDiagnosticsStore = new PostgresRelationDiagnosticsStore(factory, serializer, migrationRunner);
            var fileRoot = Path.Combine(Path.GetTempPath(), "contextcore-relation-multi-normal-scope-canary", Guid.NewGuid().ToString("N"));
            var fileOptions = new FileStorageOptions { RootPath = fileRoot };
            var filePaths = new FilePathResolver(fileOptions);
            var fileSerializer = new FileFormatSerializer();
            var fileRelationStore = new FileRelationStore(fileOptions);
            var fileReviewStore = new FileRelationReviewStore(filePaths, fileSerializer);
            var fileDiagnosticsStore = new FileRelationDiagnosticsStore(filePaths, fileSerializer);

            try
            {
                var runner = new RelationGovernanceMultiNormalScopeCanaryRunner(
                    fileRelationStore,
                    fileReviewStore,
                    fileDiagnosticsStore,
                    postgresRelationStore,
                    postgresReviewStore,
                    postgresDiagnosticsStore,
                    new RelationGovernanceMultiNormalScopeCanaryOptions
                    {
                        Enabled = true,
                        Scopes = scopes,
                        Mode = RelationGovernanceProviderMode.GuardedPostgresPrimary,
                        FallbackToFileSystem = true,
                        ContinueComparisonTrace = true,
                        FailClosedOnMismatch = true,
                        RequireLimitedNormalScopeObservationPassed = true,
                        ObservationWindowMinutes = observationWindowMinutes,
                        MaxOperationsPerScope = maxOperationsPerScope,
                        CleanupMode = cleanupMode
                    },
                    limitedNormalObservationPassed: true,
                    (trace, token) => AppendJsonLineFileAsync(tracePath, trace, token));
                report = await runner.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (Directory.Exists(fileRoot))
                {
                    Directory.Delete(fileRoot, recursive: true);
                }
            }
        }

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresRelationMultiNormalScopeCanaryMarkdown(report, "Report"), markdownPath, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[PostgresRelationMultiNormalScopeCanary] Report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresRelationMultiNormalScopeCanary] Recommendation={report.Recommendation}, Scopes={report.EnabledScopeCount}, Operations={report.OperationCount}, Mismatches={report.MismatchCount}, Failures={report.PostgresFailureCount}, ScopeLeaks={report.ScopeLeakCount}");
    }

private static async Task ExecutePostgresRelationMultiNormalScopeQualityAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-multi-normal-scope-quality-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-relation-multi-normal-scope-quality-report.md");
        var p95ThresholdMs = ParseDoubleOption(CommandHelpers.GetOption(args, "--p95-threshold-ms"), 5000);
        var limitedNormalQuality = await ReadJsonFileAsync<PostgresRelationLimitedNormalScopeObservationReport>(
            Path.Combine("storage", "postgres", "postgres-relation-limited-normal-scope-quality-report.json"),
            cancellationToken).ConfigureAwait(false);
        var canary = await ReadJsonFileAsync<PostgresRelationMultiNormalScopeCanaryReport>(
            Path.Combine("storage", "postgres", "postgres-relation-multi-normal-scope-canary-report.json"),
            cancellationToken).ConfigureAwait(false);
        var p15Passed = IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-a3.json"))
                        && IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-extended.json"));
        var runner = new RelationGovernanceMultiNormalScopeCanaryRunner(
            new FileRelationStore(new FileStorageOptions { RootPath = Path.Combine(Path.GetTempPath(), "contextcore-relation-multi-normal-quality") }),
            new FileRelationReviewStore(new FilePathResolver(new FileStorageOptions { RootPath = Path.Combine(Path.GetTempPath(), "contextcore-relation-multi-normal-quality") }), new FileFormatSerializer()),
            new FileRelationDiagnosticsStore(new FilePathResolver(new FileStorageOptions { RootPath = Path.Combine(Path.GetTempPath(), "contextcore-relation-multi-normal-quality") }), new FileFormatSerializer()),
            null!,
            null!,
            null!,
            new RelationGovernanceMultiNormalScopeCanaryOptions
            {
                Enabled = true,
                Scopes = BuildDefaultMultiNormalScopeRules(RelationGovernanceSelectedNormalWorkspaceCleanupMode.None)
            },
            string.Equals(limitedNormalQuality?.Recommendation, "ReadyForMultiNormalScopeCanary", StringComparison.OrdinalIgnoreCase),
            (_, _) => Task.CompletedTask);
        var report = runner.BuildQualityReport(canary, p15Passed, p95ThresholdMs);

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(BuildPostgresRelationMultiNormalScopeCanaryMarkdown(report, "Quality"), markdownPath, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[PostgresRelationMultiNormalScopeCanary] Quality: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresRelationMultiNormalScopeCanary] Passed={report.GatePassed}, Recommendation={report.Recommendation}, Blocked={report.BlockedReasons.Count}");
    }

private static async Task ExecutePostgresLearningFeedbackDiagnosticsAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-diagnostics.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-diagnostics.md");
        var options = BuildCliPostgresOptions(args);
        var runner = new PostgresLearningFeedbackProviderEvalRunner();
        var report = await runner.BuildDiagnosticsAsync(options, cancellationToken).ConfigureAwait(false);

        await WriteTextAsync(PostgresLearningFeedbackProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresLearningFeedbackProviderEvalRunner.BuildDiagnosticsMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresLearningFeedback] Diagnostics: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresLearningFeedback] Status={report.Status}, Feedback={report.FeedbackCount}, Reviews={report.ReviewCount}, Candidates={report.FeatureCandidateCount}");
    }

private static async Task ExecutePostgresLearningFeedbackParityAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-parity-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-parity-report.md");
        var cleanupConfirm = CommandHelpers.HasFlag(args, "--cleanup-confirm");
        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? "postgres-learning-feedback-parity";
        var collectionId = CommandHelpers.GetOption(args, "--collection") ?? "learning-feedback";
        var options = BuildCliPostgresOptions(args);
        var runner = new PostgresLearningFeedbackProviderEvalRunner();
        var report = await runner.RunParityAsync(
            options,
            workspaceId,
            collectionId,
            cleanupConfirm,
            cancellationToken).ConfigureAwait(false);

        await WriteTextAsync(PostgresLearningFeedbackProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresLearningFeedbackProviderEvalRunner.BuildParityMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresLearningFeedback] Parity: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresLearningFeedback] Recommendation={report.Recommendation}, Mismatches={report.Mismatches.Count}, Cleanup={report.CleanupPerformed}");
    }

private static async Task ExecutePostgresLearningFeedbackReadinessGateAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-readiness-gate.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-readiness-gate.md");
        var diagnosticsPath = CommandHelpers.GetOption(args, "--diagnostics")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-diagnostics.json");
        var parityPath = CommandHelpers.GetOption(args, "--parity")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-parity-report.json");
        var runner = new PostgresLearningFeedbackProviderEvalRunner();
        var report = await runner.BuildReadinessGateAsync(
            BuildCliPostgresOptions(args),
            diagnosticsPath,
            parityPath,
            cancellationToken).ConfigureAwait(false);

        await WriteTextAsync(PostgresLearningFeedbackProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresLearningFeedbackProviderEvalRunner.BuildReadinessGateMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresLearningFeedback] Readiness gate: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresLearningFeedback] GatePassed={report.GatePassed}, Recommendation={report.Recommendation}, Failed={report.FailedConditions.Count}");
    }

private static async Task ExecutePostgresLearningFeedbackDualWriteSmokeAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-dual-write-smoke-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-dual-write-smoke-report.md");
        var tracePath = CommandHelpers.GetOption(args, "--trace-out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-dual-write-traces.jsonl");
        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? "postgres-learning-feedback-dual-write-smoke";
        var collectionId = CommandHelpers.GetOption(args, "--collection") ?? "learning-feedback";
        var cleanupConfirm = CommandHelpers.HasFlag(args, "--cleanup-confirm");
        var traces = new List<LearningFeedbackDualWriteTrace>();
        var runner = new PostgresLearningFeedbackProviderEvalRunner();
        var report = await runner.RunDualWriteSmokeAsync(
            BuildCliPostgresOptions(args),
            workspaceId,
            collectionId,
            cleanupConfirm,
            traces,
            cancellationToken).ConfigureAwait(false);

        await WriteTextAsync(PostgresLearningFeedbackProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresLearningFeedbackProviderEvalRunner.BuildDualWriteSmokeMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteJsonLinesAsync(traces, tracePath, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[PostgresLearningFeedback] Dual-write smoke: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresLearningFeedback] Recommendation={report.Recommendation}, Mismatches={report.MismatchCount}, PostgresFailures={report.PostgresFailureCount}");
    }

private static async Task ExecutePostgresLearningFeedbackShadowReadSmokeAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-shadow-read-smoke-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-shadow-read-smoke-report.md");
        var tracePath = CommandHelpers.GetOption(args, "--trace-out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-shadow-read-traces.jsonl");
        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? "postgres-learning-feedback-shadow-read-smoke";
        var collectionId = CommandHelpers.GetOption(args, "--collection") ?? "learning-feedback";
        var cleanupConfirm = CommandHelpers.HasFlag(args, "--cleanup-confirm");
        var traces = new List<LearningFeedbackShadowReadTrace>();
        var runner = new PostgresLearningFeedbackProviderEvalRunner();
        var report = await runner.RunShadowReadSmokeAsync(
            BuildCliPostgresOptions(args),
            workspaceId,
            collectionId,
            cleanupConfirm,
            traces,
            cancellationToken).ConfigureAwait(false);

        await WriteTextAsync(PostgresLearningFeedbackProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresLearningFeedbackProviderEvalRunner.BuildShadowReadSmokeMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteJsonLinesAsync(traces, tracePath, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[PostgresLearningFeedback] Shadow-read smoke: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresLearningFeedback] Recommendation={report.Recommendation}, Mismatches={report.MismatchCount}, PostgresFailures={report.PostgresFailureCount}");
    }

private static async Task ExecutePostgresLearningFeedbackProviderQualityAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-provider-quality-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-provider-quality-report.md");
        var dualTracePath = CommandHelpers.GetOption(args, "--dual-traces")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-dual-write-traces.jsonl");
        var shadowTracePath = CommandHelpers.GetOption(args, "--shadow-traces")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-shadow-read-traces.jsonl");
        var dualTraces = await ReadJsonLinesAsync<LearningFeedbackDualWriteTrace>(dualTracePath, cancellationToken)
            .ConfigureAwait(false);
        var shadowTraces = await ReadJsonLinesAsync<LearningFeedbackShadowReadTrace>(shadowTracePath, cancellationToken)
            .ConfigureAwait(false);
        var runner = new PostgresLearningFeedbackProviderEvalRunner();
        var report = runner.BuildProviderQualityReport(dualTraces, shadowTraces);

        await WriteTextAsync(PostgresLearningFeedbackProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresLearningFeedbackProviderEvalRunner.BuildProviderQualityMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresLearningFeedback] Provider quality: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresLearningFeedback] Recommendation={report.Recommendation}, Traces={report.TraceCount}, Mismatches={report.MismatchCount}, PostgresFailures={report.PostgresFailureCount}");
    }

private static async Task ExecutePostgresLearningFeedbackScopedServiceModeSmokeAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-scoped-service-mode-smoke-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-scoped-service-mode-smoke-report.md");
        var tracePath = CommandHelpers.GetOption(args, "--trace-out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-provider-switch-traces.jsonl");
        var qualityPath = CommandHelpers.GetOption(args, "--provider-quality")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-provider-quality-report.json");
        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? "postgres-learning-feedback-scoped-service-mode";
        var collectionId = CommandHelpers.GetOption(args, "--collection") ?? "learning-feedback";
        var cleanupConfirm = CommandHelpers.HasFlag(args, "--cleanup-confirm");
        var providerQuality = await ReadJsonFileAsync<LearningFeedbackProviderQualityReport>(qualityPath, cancellationToken)
            .ConfigureAwait(false);
        var providerQualityReady = string.Equals(providerQuality?.Recommendation, "ReadyForScopedServiceMode", StringComparison.OrdinalIgnoreCase);
        var traces = new List<LearningFeedbackProviderSwitchTrace>();
        var runner = new PostgresLearningFeedbackProviderEvalRunner();
        var report = await runner.RunScopedServiceModeSmokeAsync(
            BuildCliPostgresOptions(args),
            workspaceId,
            collectionId,
            cleanupConfirm,
            providerQualityReady,
            traces,
            cancellationToken).ConfigureAwait(false);

        await WriteTextAsync(PostgresLearningFeedbackProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresLearningFeedbackProviderEvalRunner.BuildScopedServiceModeSmokeMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteJsonLinesAsync(traces, tracePath, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[PostgresLearningFeedback] Scoped service mode smoke: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresLearningFeedback] Recommendation={report.Recommendation}, Mismatches={report.MismatchCount}, PostgresFailures={report.PostgresFailureCount}");
    }

private static async Task ExecutePostgresLearningFeedbackScopedServiceModeGateAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-scoped-service-mode-gate.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-scoped-service-mode-gate.md");
        var readinessPath = CommandHelpers.GetOption(args, "--readiness")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-readiness-gate.json");
        var dualWritePath = CommandHelpers.GetOption(args, "--dual-write")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-dual-write-smoke-report.json");
        var shadowReadPath = CommandHelpers.GetOption(args, "--shadow-read")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-shadow-read-smoke-report.json");
        var qualityPath = CommandHelpers.GetOption(args, "--provider-quality")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-provider-quality-report.json");
        var scopedSmokePath = CommandHelpers.GetOption(args, "--scoped-smoke")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-scoped-service-mode-smoke-report.json");
        var runner = new PostgresLearningFeedbackProviderEvalRunner();
        var report = await runner.BuildScopedServiceModeGateAsync(
            readinessPath,
            dualWritePath,
            shadowReadPath,
            qualityPath,
            scopedSmokePath,
            cancellationToken).ConfigureAwait(false);

        await WriteTextAsync(PostgresLearningFeedbackProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresLearningFeedbackProviderEvalRunner.BuildScopedServiceModeGateMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresLearningFeedback] Scoped service mode gate: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresLearningFeedback] Passed={report.Passed}, Recommendation={report.Recommendation}, Blocked={report.BlockedReasons.Count}");
    }

private static async Task ExecutePostgresLearningFeedbackSelectedNormalScopeCanaryAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-selected-normal-scope-canary-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-selected-normal-scope-canary-report.md");
        var tracePath = CommandHelpers.GetOption(args, "--trace-out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-selected-normal-scope-canary-traces.jsonl");
        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? "contextcore_selected_normal";
        var collectionId = CommandHelpers.GetOption(args, "--collection") ?? "learning-feedback-selected-normal";
        var maxOperations = ParseIntOption(args, "--max-operations", 100);
        var cleanupMode = ParseLearningFeedbackSelectedNormalCleanupMode(args);
        ResetTraceOutput(tracePath);

        var postgresOptions = BuildCliPostgresOptions(args);
        var storageDiagnostics = await BuildCliPostgresDiagnosticsAsync(args, cancellationToken).ConfigureAwait(false);
        var readiness = await ReadJsonFileAsync<LearningFeedbackPostgresReadinessGateReport>(
            Path.Combine("storage", "postgres", "postgres-learning-feedback-readiness-gate.json"),
            cancellationToken).ConfigureAwait(false);
        var dualWrite = await ReadJsonFileAsync<LearningFeedbackDualWriteSmokeReport>(
            Path.Combine("storage", "postgres", "postgres-learning-feedback-dual-write-smoke-report.json"),
            cancellationToken).ConfigureAwait(false);
        var shadowRead = await ReadJsonFileAsync<LearningFeedbackShadowReadSmokeReport>(
            Path.Combine("storage", "postgres", "postgres-learning-feedback-shadow-read-smoke-report.json"),
            cancellationToken).ConfigureAwait(false);
        var providerQuality = await ReadJsonFileAsync<LearningFeedbackProviderQualityReport>(
            Path.Combine("storage", "postgres", "postgres-learning-feedback-provider-quality-report.json"),
            cancellationToken).ConfigureAwait(false);
        var scopedGate = await ReadJsonFileAsync<LearningFeedbackScopedServiceModeGateReport>(
            Path.Combine("storage", "postgres", "postgres-learning-feedback-scoped-service-mode-gate.json"),
            cancellationToken).ConfigureAwait(false);
        var p15Passed = IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-a3.json"))
                        && IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-extended.json"));
        var traces = new List<LearningFeedbackProviderSwitchTrace>();
        var runner = new PostgresLearningFeedbackProviderEvalRunner();
        var report = await runner.RunSelectedNormalScopeCanaryAsync(
            postgresOptions,
            new LearningFeedbackSelectedNormalScopeOptions
            {
                Enabled = true,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Mode = LearningFeedbackProviderMode.GuardedPostgresPrimary,
                FallbackToFileSystem = true,
                ContinueComparisonTrace = true,
                FailClosedOnMismatch = true,
                RequireScopedServiceModeGate = true,
                MaxOperations = maxOperations,
                CleanupMode = cleanupMode
            },
            storageReady: string.Equals(storageDiagnostics.Status, "Ready", StringComparison.OrdinalIgnoreCase),
            readinessGatePassed: readiness?.GatePassed == true,
            dualWriteSmokePassed: string.Equals(dualWrite?.Recommendation, "ReadyForShadowReadSmoke", StringComparison.OrdinalIgnoreCase),
            shadowReadSmokePassed: string.Equals(shadowRead?.Recommendation, "ReadyForProviderQuality", StringComparison.OrdinalIgnoreCase),
            providerQualityReady: string.Equals(providerQuality?.Recommendation, "ReadyForScopedServiceMode", StringComparison.OrdinalIgnoreCase),
            scopedServiceModeGatePassed: scopedGate?.Passed == true,
            p15GatePassed: p15Passed,
            traces,
            cancellationToken).ConfigureAwait(false);

        await WriteTextAsync(PostgresLearningFeedbackProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresLearningFeedbackProviderEvalRunner.BuildSelectedNormalScopeCanaryMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteJsonLinesAsync(traces, tracePath, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[PostgresLearningFeedback] Selected normal scope canary: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresLearningFeedback] Recommendation={report.Recommendation}, Operations={report.OperationCount}, Mismatches={report.MismatchCount}, Failures={report.PostgresFailureCount}, ScopeLeaks={report.ScopeLeakCount}");
    }

private static async Task ExecutePostgresLearningFeedbackLimitedScopeObservationAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-limited-scope-observation-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-limited-scope-observation-report.md");
        var tracePath = CommandHelpers.GetOption(args, "--trace-out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-limited-scope-traces.jsonl");
        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? "contextcore_selected_normal";
        var collectionId = CommandHelpers.GetOption(args, "--collection") ?? "learning-feedback-selected-normal";
        var maxOperations = ParseIntOption(args, "--max-operations", 100);
        var observationWindowMinutes = ParseIntOption(args, "--observation-window-minutes", 10);
        var cleanupMode = ParseLearningFeedbackSelectedNormalCleanupMode(args);
        ResetTraceOutput(tracePath);

        var postgresOptions = BuildCliPostgresOptions(args);
        var storageDiagnostics = await BuildCliPostgresDiagnosticsAsync(args, cancellationToken).ConfigureAwait(false);
        var readiness = await ReadJsonFileAsync<LearningFeedbackPostgresReadinessGateReport>(
            Path.Combine("storage", "postgres", "postgres-learning-feedback-readiness-gate.json"),
            cancellationToken).ConfigureAwait(false);
        var dualWrite = await ReadJsonFileAsync<LearningFeedbackDualWriteSmokeReport>(
            Path.Combine("storage", "postgres", "postgres-learning-feedback-dual-write-smoke-report.json"),
            cancellationToken).ConfigureAwait(false);
        var shadowRead = await ReadJsonFileAsync<LearningFeedbackShadowReadSmokeReport>(
            Path.Combine("storage", "postgres", "postgres-learning-feedback-shadow-read-smoke-report.json"),
            cancellationToken).ConfigureAwait(false);
        var providerQuality = await ReadJsonFileAsync<LearningFeedbackProviderQualityReport>(
            Path.Combine("storage", "postgres", "postgres-learning-feedback-provider-quality-report.json"),
            cancellationToken).ConfigureAwait(false);
        var scopedGate = await ReadJsonFileAsync<LearningFeedbackScopedServiceModeGateReport>(
            Path.Combine("storage", "postgres", "postgres-learning-feedback-scoped-service-mode-gate.json"),
            cancellationToken).ConfigureAwait(false);
        var selectedCanary = await ReadJsonFileAsync<LearningFeedbackSelectedNormalScopeCanaryReport>(
            Path.Combine("storage", "postgres", "postgres-learning-feedback-selected-normal-scope-canary-report.json"),
            cancellationToken).ConfigureAwait(false);
        var p15Passed = IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-a3.json"))
                        && IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-extended.json"));
        var traces = new List<LearningFeedbackProviderSwitchTrace>();
        var runner = new PostgresLearningFeedbackProviderEvalRunner();
        var report = await runner.RunLimitedScopeObservationAsync(
            postgresOptions,
            new LearningFeedbackLimitedScopeObservationOptions
            {
                Enabled = true,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ObservationWindowMinutes = observationWindowMinutes,
                MaxOperations = maxOperations,
                Mode = LearningFeedbackProviderMode.GuardedPostgresPrimary,
                FallbackToFileSystem = true,
                ContinueComparisonTrace = true,
                FailClosedOnMismatch = true,
                RequireSelectedNormalScopeCanaryPassed = true,
                CleanupMode = cleanupMode
            },
            storageReady: string.Equals(storageDiagnostics.Status, "Ready", StringComparison.OrdinalIgnoreCase),
            readinessGatePassed: readiness?.GatePassed == true,
            dualWriteSmokePassed: string.Equals(dualWrite?.Recommendation, "ReadyForShadowReadSmoke", StringComparison.OrdinalIgnoreCase),
            shadowReadSmokePassed: string.Equals(shadowRead?.Recommendation, "ReadyForProviderQuality", StringComparison.OrdinalIgnoreCase),
            providerQualityReady: string.Equals(providerQuality?.Recommendation, "ReadyForScopedServiceMode", StringComparison.OrdinalIgnoreCase),
            scopedServiceModeGatePassed: scopedGate?.Passed == true,
            selectedNormalScopeCanaryPassed: string.Equals(selectedCanary?.Recommendation, "ReadyForLimitedFeedbackScope", StringComparison.OrdinalIgnoreCase),
            p15GatePassed: p15Passed,
            traces,
            cancellationToken).ConfigureAwait(false);

        await WriteTextAsync(PostgresLearningFeedbackProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresLearningFeedbackProviderEvalRunner.BuildLimitedScopeObservationMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteJsonLinesAsync(traces, tracePath, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[PostgresLearningFeedback] Limited scope observation: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresLearningFeedback] Recommendation={report.Recommendation}, Operations={report.OperationCount}, Mismatches={report.MismatchCount}, Failures={report.PostgresFailureCount}, ScopeLeaks={report.ScopeLeakCount}");
    }

private static async Task ExecutePostgresLearningFeedbackLimitedScopeQualityAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-limited-scope-quality-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-limited-scope-quality-report.md");
        var observationPath = CommandHelpers.GetOption(args, "--observation")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-limited-scope-observation-report.json");
        var observation = await ReadJsonFileAsync<LearningFeedbackLimitedScopeObservationReport>(
            observationPath,
            cancellationToken).ConfigureAwait(false);
        var runner = new PostgresLearningFeedbackProviderEvalRunner();
        var report = runner.BuildLimitedScopeQualityReport(observation);

        await WriteTextAsync(PostgresLearningFeedbackProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresLearningFeedbackProviderEvalRunner.BuildLimitedScopeQualityMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresLearningFeedback] Limited scope quality: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresLearningFeedback] Passed={report.Passed}, Recommendation={report.Recommendation}, Mismatches={report.MismatchCount}, Failures={report.PostgresFailureCount}, ScopeLeaks={report.ScopeLeakCount}");
    }

private static async Task ExecutePostgresLearningFeedbackFreezeGateAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-freeze-gate.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-learning-feedback-freeze-gate.md");
        var readiness = await ReadJsonFileAsync<LearningFeedbackPostgresReadinessGateReport>(
            Path.Combine("storage", "postgres", "postgres-learning-feedback-readiness-gate.json"),
            cancellationToken).ConfigureAwait(false);
        var providerQuality = await ReadJsonFileAsync<LearningFeedbackProviderQualityReport>(
            Path.Combine("storage", "postgres", "postgres-learning-feedback-provider-quality-report.json"),
            cancellationToken).ConfigureAwait(false);
        var scopedGate = await ReadJsonFileAsync<LearningFeedbackScopedServiceModeGateReport>(
            Path.Combine("storage", "postgres", "postgres-learning-feedback-scoped-service-mode-gate.json"),
            cancellationToken).ConfigureAwait(false);
        var selectedCanary = await ReadJsonFileAsync<LearningFeedbackSelectedNormalScopeCanaryReport>(
            Path.Combine("storage", "postgres", "postgres-learning-feedback-selected-normal-scope-canary-report.json"),
            cancellationToken).ConfigureAwait(false);
        var limitedQuality = await ReadJsonFileAsync<LearningFeedbackLimitedScopeQualityReport>(
            Path.Combine("storage", "postgres", "postgres-learning-feedback-limited-scope-quality-report.json"),
            cancellationToken).ConfigureAwait(false);
        var p15Passed = IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-a3.json"))
                        && IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-extended.json"));
        var report = new PostgresLearningFeedbackProviderEvalRunner().BuildFreezeGateReport(
            readiness,
            providerQuality,
            scopedGate,
            selectedCanary,
            limitedQuality,
            p15Passed);

        await WriteTextAsync(PostgresLearningFeedbackProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresLearningFeedbackProviderEvalRunner.BuildFreezeGateMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresLearningFeedback] Freeze gate: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresLearningFeedback] Passed={report.Passed}, Recommendation={report.Recommendation}, Blocked={report.BlockedReasons.Count}");
    }

private static async Task ExecutePostgresJobQueueDiagnosticsAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-diagnostics.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-diagnostics.md");
        var runner = new PostgresJobQueueProviderEvalRunner();
        var report = await runner.BuildDiagnosticsAsync(BuildCliPostgresOptions(args), cancellationToken)
            .ConfigureAwait(false);

        await WriteTextAsync(PostgresJobQueueProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresJobQueueProviderEvalRunner.BuildDiagnosticsMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresJobQueue] Diagnostics: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresJobQueue] Recommendation={report.Recommendation}, Jobs={report.JobCount}, StaleLeases={report.StaleLeaseCount}, MissingIndexes={report.MissingIndexes.Count}");
    }

private static async Task ExecutePostgresJobQueueParityAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-parity-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-parity-report.md");
        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? "postgres-job-queue-parity";
        var collectionId = CommandHelpers.GetOption(args, "--collection") ?? "jobs";
        var cleanupConfirm = CommandHelpers.HasFlag(args, "--cleanup-confirm");
        var runner = new PostgresJobQueueProviderEvalRunner();
        var report = await runner.RunParityAsync(
            BuildCliPostgresOptions(args),
            workspaceId,
            collectionId,
            cleanupConfirm,
            cancellationToken).ConfigureAwait(false);

        await WriteTextAsync(PostgresJobQueueProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresJobQueueProviderEvalRunner.BuildParityMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresJobQueue] Parity: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresJobQueue] Recommendation={report.Recommendation}, Operations={report.OperationCount}, Mismatches={report.MismatchCount}, Failures={report.PostgresFailureCount}, Cleanup={report.CleanupPerformed}");
    }

private static async Task ExecutePostgresJobQueueLeaseSmokeAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-lease-smoke-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-lease-smoke-report.md");
        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? "postgres-job-queue-lease-smoke";
        var collectionId = CommandHelpers.GetOption(args, "--collection") ?? "jobs";
        var cleanupConfirm = CommandHelpers.HasFlag(args, "--cleanup-confirm");
        var runner = new PostgresJobQueueProviderEvalRunner();
        var report = await runner.RunLeaseSmokeAsync(
            BuildCliPostgresOptions(args),
            workspaceId,
            collectionId,
            cleanupConfirm,
            cancellationToken).ConfigureAwait(false);

        await WriteTextAsync(PostgresJobQueueProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresJobQueueProviderEvalRunner.BuildLeaseSmokeMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresJobQueue] Lease smoke: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresJobQueue] Recommendation={report.Recommendation}, Operations={report.OperationCount}, Mismatches={report.MismatchCount}, LeaseAcquire={report.LeaseAcquireCount}, LeaseConflicts={report.LeaseConflictCount}");
    }

private static async Task ExecutePostgresJobQueueDualWriteSmokeAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-dual-write-smoke-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-dual-write-smoke-report.md");
        var tracePath = CommandHelpers.GetOption(args, "--trace-out")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-dual-write-traces.jsonl");
        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? "postgres-job-queue-dual-write-smoke";
        var collectionId = CommandHelpers.GetOption(args, "--collection") ?? "jobs";
        var cleanupConfirm = CommandHelpers.HasFlag(args, "--cleanup-confirm");
        var runner = new PostgresJobQueueProviderEvalRunner();
        var report = await runner.RunDualWriteSmokeAsync(
            BuildCliPostgresOptions(args),
            workspaceId,
            collectionId,
            cleanupConfirm,
            cancellationToken).ConfigureAwait(false);

        await WriteTextAsync(PostgresJobQueueProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresJobQueueProviderEvalRunner.BuildDualWriteMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresJobQueueProviderEvalRunner.SerializeJsonLines(report.Traces), tracePath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresJobQueue] Dual-write smoke: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresJobQueue] Recommendation={report.Recommendation}, Traces={report.TraceCount}, Mismatches={report.MismatchCount}, Failures={report.PostgresFailureCount}, Cleanup={report.CleanupPerformed}");
    }

private static async Task ExecutePostgresJobQueueShadowReadSmokeAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-shadow-read-smoke-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-shadow-read-smoke-report.md");
        var tracePath = CommandHelpers.GetOption(args, "--trace-out")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-shadow-read-traces.jsonl");
        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? "postgres-job-queue-shadow-read-smoke";
        var collectionId = CommandHelpers.GetOption(args, "--collection") ?? "jobs";
        var cleanupConfirm = CommandHelpers.HasFlag(args, "--cleanup-confirm");
        var runner = new PostgresJobQueueProviderEvalRunner();
        var report = await runner.RunShadowReadSmokeAsync(
            BuildCliPostgresOptions(args),
            workspaceId,
            collectionId,
            cleanupConfirm,
            cancellationToken).ConfigureAwait(false);

        await WriteTextAsync(PostgresJobQueueProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresJobQueueProviderEvalRunner.BuildShadowReadMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresJobQueueProviderEvalRunner.SerializeJsonLines(report.Traces), tracePath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresJobQueue] Shadow-read smoke: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresJobQueue] Recommendation={report.Recommendation}, Traces={report.TraceCount}, Mismatches={report.MismatchCount}, Failures={report.PostgresFailureCount}, Cleanup={report.CleanupPerformed}");
    }

private static async Task ExecutePostgresJobQueueProviderQualityAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var dualPath = CommandHelpers.GetOption(args, "--dual")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-dual-write-smoke-report.json");
        var shadowPath = CommandHelpers.GetOption(args, "--shadow")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-shadow-read-smoke-report.json");
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-provider-quality-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-provider-quality-report.md");
        var dual = await ReadJsonFileAsync<PostgresJobQueueDualWriteSmokeReport>(dualPath, cancellationToken)
            .ConfigureAwait(false) ?? new PostgresJobQueueDualWriteSmokeReport { Diagnostics = ["DualWriteSmokeMissing"], Recommendation = "NeedsMoreTraces" };
        var shadow = await ReadJsonFileAsync<PostgresJobQueueShadowReadSmokeReport>(shadowPath, cancellationToken)
            .ConfigureAwait(false) ?? new PostgresJobQueueShadowReadSmokeReport { Diagnostics = ["ShadowReadSmokeMissing"], Recommendation = "NeedsMoreTraces" };
        var report = PostgresJobQueueProviderEvalRunner.BuildProviderQuality(dual, shadow);

        await WriteTextAsync(PostgresJobQueueProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresJobQueueProviderEvalRunner.BuildProviderQualityMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresJobQueue] Provider quality: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresJobQueue] Recommendation={report.Recommendation}, Traces={report.TraceCount}, Mismatches={report.MismatchCount}, Failures={report.PostgresFailureCount}");
    }

private static async Task ExecutePostgresJobQueueScopedWorkerCanaryAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var qualityPath = CommandHelpers.GetOption(args, "--quality")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-provider-quality-report.json");
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-scoped-worker-canary-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-scoped-worker-canary-report.md");
        var tracePath = CommandHelpers.GetOption(args, "--trace-out")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-scoped-worker-traces.jsonl");
        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? "postgres-job-queue-scoped-worker-canary";
        var collectionId = CommandHelpers.GetOption(args, "--collection") ?? "jobs";
        var cleanupConfirm = CommandHelpers.HasFlag(args, "--cleanup-confirm");
        var providerQuality = await ReadJsonFileAsync<PostgresJobQueueProviderQualityReport>(qualityPath, cancellationToken)
            .ConfigureAwait(false) ?? new PostgresJobQueueProviderQualityReport
            {
                Diagnostics = ["PostgresJobQueueProviderQualityMissing"],
                Recommendation = "NeedsMoreTraces"
            };
        var runner = new PostgresJobQueueProviderEvalRunner();
        var report = await runner.RunScopedWorkerCanaryAsync(
            BuildCliPostgresOptions(args),
            providerQuality,
            workspaceId,
            collectionId,
            cleanupConfirm,
            cancellationToken).ConfigureAwait(false);

        await WriteTextAsync(PostgresJobQueueProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresJobQueueProviderEvalRunner.BuildScopedWorkerCanaryMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresJobQueueProviderEvalRunner.SerializeJsonLines(report.Traces), tracePath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresJobQueue] Scoped worker canary: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresJobQueue] Recommendation={report.Recommendation}, Jobs={report.JobCount}, Completed={report.CompletedCount}, Retried={report.RetriedCount}, DeadLetter={report.DeadLetterCount}, Mismatches={report.MismatchCount}, Failures={report.PostgresFailureCount}, ScopeLeaks={report.ScopeLeakCount}");
    }

private static async Task ExecutePostgresJobQueueScopedWorkerQualityAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var canaryPath = CommandHelpers.GetOption(args, "--canary")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-scoped-worker-canary-report.json");
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-scoped-worker-quality-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-scoped-worker-quality-report.md");
        var canary = await ReadJsonFileAsync<PostgresJobQueueScopedWorkerCanaryReport>(canaryPath, cancellationToken)
            .ConfigureAwait(false) ?? new PostgresJobQueueScopedWorkerCanaryReport
            {
                BlockedReasons = ["PostgresJobQueueScopedWorkerCanaryMissing"],
                Recommendation = "NeedsMoreWorkerCanary"
            };
        var report = PostgresJobQueueProviderEvalRunner.BuildScopedWorkerQuality(canary);

        await WriteTextAsync(PostgresJobQueueProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresJobQueueProviderEvalRunner.BuildScopedWorkerQualityMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresJobQueue] Scoped worker quality: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresJobQueue] Passed={report.Passed}, Recommendation={report.Recommendation}, Mismatches={report.MismatchCount}, Failures={report.PostgresFailureCount}, ScopeLeaks={report.ScopeLeakCount}");
    }

private static async Task ExecutePostgresJobQueueLimitedWorkerScopeObservationAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var qualityPath = CommandHelpers.GetOption(args, "--quality")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-scoped-worker-quality-report.json");
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-limited-worker-scope-observation-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-limited-worker-scope-observation-report.md");
        var tracePath = CommandHelpers.GetOption(args, "--trace-out")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-limited-worker-scope-traces.jsonl");
        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? "postgres-job-queue-scoped-worker-canary";
        var collectionId = CommandHelpers.GetOption(args, "--collection") ?? "jobs";
        var observationWindowSeconds = int.TryParse(
            CommandHelpers.GetOption(args, "--observation-window-seconds"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsedSeconds)
            ? parsedSeconds
            : 120;
        var cleanupConfirm = CommandHelpers.HasFlag(args, "--cleanup-confirm");
        var scopedQuality = await ReadJsonFileAsync<PostgresJobQueueScopedWorkerQualityReport>(qualityPath, cancellationToken)
            .ConfigureAwait(false) ?? new PostgresJobQueueScopedWorkerQualityReport
            {
                BlockedReasons = ["PostgresJobQueueScopedWorkerQualityMissing"],
                Recommendation = "GateNotPassed"
            };
        var runner = new PostgresJobQueueProviderEvalRunner();
        var report = await runner.RunLimitedWorkerScopeObservationAsync(
            BuildCliPostgresOptions(args),
            scopedQuality,
            workspaceId,
            collectionId,
            Math.Max(1, observationWindowSeconds),
            cleanupConfirm,
            cancellationToken).ConfigureAwait(false);

        await WriteTextAsync(PostgresJobQueueProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresJobQueueProviderEvalRunner.BuildLimitedWorkerScopeObservationMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresJobQueueProviderEvalRunner.SerializeJsonLines(report.Traces), tracePath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresJobQueue] Limited worker scope observation: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresJobQueue] Recommendation={report.Recommendation}, Jobs={report.JobCount}, Completed={report.CompletedCount}, Retried={report.RetriedCount}, DeadLetter={report.DeadLetterCount}, Duplicate={report.DuplicateExecutionCount}, LeaseViolations={report.LeaseViolationCount}, Failures={report.PostgresFailureCount}, ScopeLeaks={report.ScopeLeakCount}");
    }

private static async Task ExecutePostgresJobQueueLimitedWorkerScopeQualityAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var observationPath = CommandHelpers.GetOption(args, "--observation")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-limited-worker-scope-observation-report.json");
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-limited-worker-scope-quality-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-limited-worker-scope-quality-report.md");
        var observation = await ReadJsonFileAsync<PostgresJobQueueLimitedWorkerScopeObservationReport>(observationPath, cancellationToken)
            .ConfigureAwait(false) ?? new PostgresJobQueueLimitedWorkerScopeObservationReport
            {
                BlockedReasons = ["PostgresJobQueueLimitedWorkerObservationMissing"],
                Recommendation = "NeedsLongerObservation"
            };
        var report = PostgresJobQueueProviderEvalRunner.BuildLimitedWorkerScopeQuality(observation);

        await WriteTextAsync(PostgresJobQueueProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresJobQueueProviderEvalRunner.BuildLimitedWorkerScopeQualityMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresJobQueue] Limited worker scope quality: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresJobQueue] Passed={report.Passed}, Recommendation={report.Recommendation}, Duplicate={report.DuplicateExecutionCount}, LeaseViolations={report.LeaseViolationCount}, Failures={report.PostgresFailureCount}, ScopeLeaks={report.ScopeLeakCount}");
    }

private static async Task ExecutePostgresJobQueueFreezeGateAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-freeze-gate.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-job-queue-freeze-gate.md");
        var diagnostics = await ReadJsonFileAsync<PostgresJobQueueDiagnosticsReport>(
            Path.Combine("storage", "postgres", "postgres-job-queue-diagnostics.json"),
            cancellationToken).ConfigureAwait(false);
        var providerQuality = await ReadJsonFileAsync<PostgresJobQueueProviderQualityReport>(
            Path.Combine("storage", "postgres", "postgres-job-queue-provider-quality-report.json"),
            cancellationToken).ConfigureAwait(false);
        var scopedCanary = await ReadJsonFileAsync<PostgresJobQueueScopedWorkerCanaryReport>(
            Path.Combine("storage", "postgres", "postgres-job-queue-scoped-worker-canary-report.json"),
            cancellationToken).ConfigureAwait(false);
        var limitedQuality = await ReadJsonFileAsync<PostgresJobQueueLimitedWorkerScopeQualityReport>(
            Path.Combine("storage", "postgres", "postgres-job-queue-limited-worker-scope-quality-report.json"),
            cancellationToken).ConfigureAwait(false);
        var p15Passed = IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-a3.json"))
                        && IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-extended.json"));
        var report = new PostgresJobQueueProviderEvalRunner().BuildFreezeGateReport(
            diagnostics,
            providerQuality,
            scopedCanary,
            limitedQuality,
            p15Passed);

        await WriteTextAsync(PostgresJobQueueProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresJobQueueProviderEvalRunner.BuildFreezeGateMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        await new LearningReadinessFreezeRunner()
            .RunFreezeReportAsync(Path.Combine(Directory.GetCurrentDirectory(), LearningReadinessFreezeRunner.DefaultOutputDirectory), cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresJobQueue] Freeze gate: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresJobQueue] Passed={report.Passed}, Recommendation={report.Recommendation}, Status={report.JobQueuePostgres}, Blocked={report.BlockedReasons.Count}");
    }

private static async Task ExecutePostgresVectorDiagnosticsAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-vector-diagnostics.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-vector-diagnostics.md");
        var runner = new PostgresVectorIndexProviderEvalRunner();
        var report = await runner.BuildDiagnosticsAsync(BuildCliPostgresOptions(args), cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresVectorIndexProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresVectorIndexProviderEvalRunner.BuildDiagnosticsMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[PostgresVector] Diagnostics: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresVector] Recommendation={report.Recommendation}, PgVector={report.PgVectorAvailable}, MissingIndexes={report.MissingIndexCount}");
    }

private static async Task ExecutePostgresVectorCompatibilityAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-vector-compatibility.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-vector-compatibility.md");
        var providerId = CommandHelpers.GetOption(args, "--provider") ?? "deterministic-hash";
        var modelId = CommandHelpers.GetOption(args, "--model") ?? "deterministic-hash-v1";
        var dimension = CommandHelpers.GetIntOption(args, "--dimension", 16);
        var normalized = ParseBoolOption(CommandHelpers.GetOption(args, "--normalized"), defaultValue: true);
        var runner = new PostgresVectorIndexProviderEvalRunner();
        var report = await runner.BuildCompatibilityAsync(
                BuildCliPostgresOptions(args),
                providerId,
                modelId,
                dimension,
                normalized,
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresVectorIndexProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresVectorIndexProviderEvalRunner.BuildCompatibilityMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[PostgresVector] Compatibility: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresVector] Recommendation={report.Recommendation}, CompatibleEntries={report.ExistingCompatibleEntryCount}, Stale={report.StaleProviderModelEntriesCount}");
    }

private static async Task ExecutePostgresVectorProviderSmokeAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-vector-provider-smoke-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-vector-provider-smoke-report.md");
        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? "postgres-vector-smoke";
        var collectionId = CommandHelpers.GetOption(args, "--collection") ?? "vector-index-store";
        var providerId = CommandHelpers.GetOption(args, "--provider") ?? "deterministic-hash";
        var modelId = CommandHelpers.GetOption(args, "--model") ?? "deterministic-hash-v1";
        var dimension = CommandHelpers.GetIntOption(args, "--dimension", 16);
        var cleanupConfirm = CommandHelpers.HasFlag(args, "--cleanup-confirm");
        var runner = new PostgresVectorIndexProviderEvalRunner();
        var report = await runner.RunProviderSmokeAsync(
                BuildCliPostgresOptions(args),
                workspaceId,
                collectionId,
                providerId,
                modelId,
                dimension,
                cleanupConfirm,
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresVectorIndexProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresVectorIndexProviderEvalRunner.BuildSmokeMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[PostgresVector] Provider smoke: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresVector] Recommendation={report.Recommendation}, Inserted={report.InsertedCount}, Mismatches={report.MismatchCount}, Cleanup={report.CleanupPerformed}");
    }

private static async Task ExecutePostgresVectorParityAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-vector-parity-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-vector-parity-report.md");
        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? "postgres-vector-parity";
        var collectionId = CommandHelpers.GetOption(args, "--collection") ?? "vector-index-store";
        var providerId = CommandHelpers.GetOption(args, "--provider") ?? "deterministic-hash";
        var modelId = CommandHelpers.GetOption(args, "--model") ?? "deterministic-hash-v1";
        var dimension = CommandHelpers.GetIntOption(args, "--dimension", 16);
        var cleanupConfirm = CommandHelpers.HasFlag(args, "--cleanup-confirm");
        var runner = new PostgresVectorIndexParityRunner();
        var report = await runner.RunParityAsync(
                BuildCliPostgresOptions(args),
                workspaceId,
                collectionId,
                providerId,
                modelId,
                dimension,
                cleanupConfirm,
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresVectorIndexProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresVectorIndexParityRunner.BuildMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[PostgresVector] Parity: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresVector] Recommendation={report.Recommendation}, Mismatches={report.MismatchCount}, Ordering={report.OrderingMismatchCount}, Cleanup={report.CleanupPerformed}");
    }

private static async Task ExecutePostgresVectorProviderScopedReindexPlanAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-vector-provider-scoped-reindex-plan.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-vector-provider-scoped-reindex-plan.md");
        var sourceItems = await LoadPostgresVectorProviderScopedReindexSourceItemsAsync(service, args, cancellationToken)
            .ConfigureAwait(false);
        var providerOptions = BuildEmbeddingProviderOptions(args);
        var generator = CreateVectorCommandEmbeddingGenerator(providerOptions);
        var runner = new PostgresVectorProviderScopedReindexRunner();
        var report = await runner.BuildPlanAsync(
                BuildCliPostgresOptions(args),
                sourceItems,
                generator,
                ResolveVectorCommandWorkspaceId(service, args),
                ResolveVectorCommandCollectionId(service, args),
                generator.Provider,
                generator.Model,
                generator.Dimension,
                ResolveGeneratorNormalize(generator, providerOptions),
                CommandHelpers.GetOption(args, "--source-kind"),
                dryRun: true,
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresVectorIndexProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresVectorProviderScopedReindexRunner.BuildPlanMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[PostgresVector] Provider-scoped reindex plan: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresVector] Recommendation={report.Recommendation}, Candidates={report.CandidateCount}, Insert={report.PlannedInsertCount}, Update={report.PlannedUpdateCount}, Skip={report.PlannedSkipCount}");
    }

private static async Task ExecutePostgresVectorProviderScopedReindexApplyAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-vector-provider-scoped-reindex-apply-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-vector-provider-scoped-reindex-apply-report.md");
        var sourceItems = await LoadPostgresVectorProviderScopedReindexSourceItemsAsync(service, args, cancellationToken)
            .ConfigureAwait(false);
        var providerOptions = BuildEmbeddingProviderOptions(args);
        var generator = CreateVectorCommandEmbeddingGenerator(providerOptions);
        var runner = new PostgresVectorProviderScopedReindexRunner();
        var report = await runner.ApplyAsync(
                BuildCliPostgresOptions(args),
                sourceItems,
                generator,
                ResolveVectorCommandWorkspaceId(service, args),
                ResolveVectorCommandCollectionId(service, args),
                generator.Provider,
                generator.Model,
                generator.Dimension,
                ResolveGeneratorNormalize(generator, providerOptions),
                CommandHelpers.GetOption(args, "--source-kind"),
                CommandHelpers.HasFlag(args, "--confirm"),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresVectorIndexProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresVectorProviderScopedReindexRunner.BuildResultMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[PostgresVector] Provider-scoped reindex apply: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresVector] Recommendation={report.Recommendation}, AppliedInsert={report.AppliedInsertCount}, AppliedUpdate={report.AppliedUpdateCount}, MetadataMismatch={report.MetadataRoundtripMismatchCount}");
    }

private static async Task ExecutePostgresVectorProviderScopedReindexQualityAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-vector-provider-scoped-reindex-quality-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-vector-provider-scoped-reindex-quality-report.md");
        var sourceItems = await LoadVectorReindexSourceItemsForCommandAsync(args, cancellationToken)
            .ConfigureAwait(false);
        var providerOptions = BuildEmbeddingProviderOptions(args);
        var generator = CreateVectorCommandEmbeddingGenerator(providerOptions);
        var applyReportPath = CommandHelpers.GetOption(args, "--apply-report")
            ?? Path.Combine("storage", "postgres", "postgres-vector-provider-scoped-reindex-apply-report.json");
        var latestApply = ReadJsonFileOrDefault<PostgresVectorProviderScopedReindexResult>(applyReportPath);
        var runner = new PostgresVectorProviderScopedReindexRunner();
        var report = await runner.BuildQualityAsync(
                BuildCliPostgresOptions(args),
                sourceItems,
                generator,
                ResolveVectorCommandWorkspaceId(service, args),
                ResolveVectorCommandCollectionId(service, args),
                generator.Provider,
                generator.Model,
                generator.Dimension,
                ResolveGeneratorNormalize(generator, providerOptions),
                CommandHelpers.GetOption(args, "--source-kind"),
                latestApply,
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresVectorIndexProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresVectorProviderScopedReindexRunner.BuildQualityMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[PostgresVector] Provider-scoped reindex quality: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresVector] Recommendation={report.Recommendation}, Indexed={report.IndexedEntryCountAfterApply}, MetadataMismatch={report.MetadataRoundtripMismatchCount}, Runtime={report.UseForRuntime}");
    }

private static async Task ExecutePostgresVectorQueryPreviewAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-vector-query-preview-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-vector-query-preview-report.md");
        var sourceItems = await LoadPostgresVectorProviderScopedReindexSourceItemsAsync(service, args, cancellationToken)
            .ConfigureAwait(false);
        var contextsRoot = CommandHelpers.GetOption(args, "--contexts") ?? Path.Combine("eval", "contexts");
        var categoryFilter = CommandHelpers.GetOption(args, "--category") ?? CommandHelpers.GetOption(args, "-c");
        var includeSeedBatches = !CommandHelpers.HasFlag(args, "--baseline-only");
        var samples = await LoadVectorEvalSamplesAsync(
                contextsRoot,
                categoryFilter,
                includeSeedBatches,
                cancellationToken)
            .ConfigureAwait(false);
        var maxQueries = CommandHelpers.GetIntOption(args, "--max-queries", 0);
        if (maxQueries > 0)
        {
            samples = samples.Take(maxQueries).ToArray();
        }

        var providerOptions = BuildEmbeddingProviderOptions(args);
        var generator = CreateVectorCommandEmbeddingGenerator(providerOptions);
        var profileId = CommandHelpers.GetOption(args, "--profile") ?? VectorQueryProfileIds.NormalV1;
        var topK = CommandHelpers.GetIntOption(args, "--top-k", 10);
        var minSimilarity = GetDoubleOption(args, "--min-similarity");
        var runner = new PostgresVectorQueryPreviewRunner();
        var report = await runner.RunAsync(
                BuildCliPostgresOptions(args),
                sourceItems,
                samples,
                generator,
                ResolveVectorCommandWorkspaceId(service, args),
                ResolveVectorCommandCollectionId(service, args),
                generator.Provider,
                generator.Model,
                generator.Dimension,
                ResolveGeneratorNormalize(generator, providerOptions),
                topK,
                profileId,
                minSimilarity,
                cancellationToken)
            .ConfigureAwait(false);
        report = AttachProviderMetadata(report, providerOptions);
        await WriteTextAsync(PostgresVectorIndexProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresVectorQueryPreviewRunner.BuildMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[PostgresVector] Query preview: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresVector] Recommendation={report.Recommendation}, Queries={report.QueryCount}, PgCandidates={report.PgVectorCandidateCount}, OrderingMismatch={report.OrderingMismatchCount}, MetadataMismatch={report.MetadataMismatchCount}");
    }

private static async Task ExecutePostgresVectorShadowEvalAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var runA3 = !string.Equals(subcommand, "postgres-vector-shadow-eval-extended", StringComparison.OrdinalIgnoreCase);
        var runExtended = !string.Equals(subcommand, "postgres-vector-shadow-eval-a3", StringComparison.OrdinalIgnoreCase);
        var a3OutputPath = CommandHelpers.GetOption(args, "--out-a3")
            ?? (string.Equals(subcommand, "postgres-vector-shadow-eval-a3", StringComparison.OrdinalIgnoreCase)
                ? CommandHelpers.GetOption(args, "--out") ?? Path.Combine("storage", "postgres", "postgres-vector-shadow-eval-a3.json")
                : Path.Combine("storage", "postgres", "postgres-vector-shadow-eval-a3.json"));
        var a3MarkdownPath = CommandHelpers.GetOption(args, "--md-out-a3")
            ?? (string.Equals(subcommand, "postgres-vector-shadow-eval-a3", StringComparison.OrdinalIgnoreCase)
                ? CommandHelpers.GetOption(args, "--md-out") ?? Path.Combine("storage", "postgres", "postgres-vector-shadow-eval-a3.md")
                : Path.Combine("storage", "postgres", "postgres-vector-shadow-eval-a3.md"));
        var extendedOutputPath = CommandHelpers.GetOption(args, "--out-extended")
            ?? (string.Equals(subcommand, "postgres-vector-shadow-eval-extended", StringComparison.OrdinalIgnoreCase)
                ? CommandHelpers.GetOption(args, "--out") ?? Path.Combine("storage", "postgres", "postgres-vector-shadow-eval-extended.json")
                : Path.Combine("storage", "postgres", "postgres-vector-shadow-eval-extended.json"));
        var extendedMarkdownPath = CommandHelpers.GetOption(args, "--md-out-extended")
            ?? (string.Equals(subcommand, "postgres-vector-shadow-eval-extended", StringComparison.OrdinalIgnoreCase)
                ? CommandHelpers.GetOption(args, "--md-out") ?? Path.Combine("storage", "postgres", "postgres-vector-shadow-eval-extended.md")
                : Path.Combine("storage", "postgres", "postgres-vector-shadow-eval-extended.md"));
        var summaryOutputPath = CommandHelpers.GetOption(args, "--out-summary")
            ?? Path.Combine("storage", "postgres", "postgres-vector-shadow-eval-summary.json");
        var summaryMarkdownPath = CommandHelpers.GetOption(args, "--summary-md-out")
            ?? Path.Combine("storage", "postgres", "postgres-vector-shadow-eval-summary.md");
        var contextsRoot = CommandHelpers.GetOption(args, "--contexts") ?? Path.Combine("eval", "contexts");
        var categoryFilter = CommandHelpers.GetOption(args, "--category") ?? CommandHelpers.GetOption(args, "-c");
        var sourceItems = await LoadPostgresVectorProviderScopedReindexSourceItemsAsync(service, args, cancellationToken)
            .ConfigureAwait(false);
        var providerOptions = BuildEmbeddingProviderOptions(args);
        var generator = CreateVectorCommandEmbeddingGenerator(providerOptions);
        var profileId = CommandHelpers.GetOption(args, "--profile") ?? VectorQueryProfileIds.NormalV1;
        var topK = CommandHelpers.GetIntOption(args, "--top-k", 10);
        var minSimilarity = GetDoubleOption(args, "--min-similarity");
        var workspaceId = ResolveVectorCommandWorkspaceId(service, args);
        var collectionId = ResolveVectorCommandCollectionId(service, args);
        var runner = new PostgresVectorShadowEvalRunner();
        var reports = new List<PostgresVectorShadowEvalReport>(capacity: 2);

        if (runA3)
        {
            var a3Samples = await LoadVectorEvalSamplesAsync(
                    contextsRoot,
                    categoryFilter,
                    includeSeedBatches: false,
                    cancellationToken)
                .ConfigureAwait(false);
            a3Samples = LimitSamples(a3Samples, args);
            var a3Report = await runner.RunAsync(
                    "A3",
                    BuildCliPostgresOptions(args),
                    sourceItems,
                    a3Samples,
                    generator,
                    workspaceId,
                    collectionId,
                    generator.Provider,
                    generator.Model,
                    generator.Dimension,
                    ResolveGeneratorNormalize(generator, providerOptions),
                    topK,
                    profileId,
                    minSimilarity,
                    cancellationToken)
                .ConfigureAwait(false);
            a3Report = AttachProviderMetadata(a3Report, providerOptions);
            reports.Add(a3Report);
            await WriteTextAsync(PostgresVectorIndexProviderEvalRunner.SerializeJson(a3Report), a3OutputPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(PostgresVectorShadowEvalRunner.BuildMarkdown(a3Report), a3MarkdownPath, cancellationToken)
                .ConfigureAwait(false);
        }

        if (runExtended)
        {
            var extendedSamples = await LoadVectorEvalSamplesAsync(
                    contextsRoot,
                    categoryFilter,
                    includeSeedBatches: true,
                    cancellationToken)
                .ConfigureAwait(false);
            extendedSamples = LimitSamples(extendedSamples, args);
            var extendedReport = await runner.RunAsync(
                    "Extended",
                    BuildCliPostgresOptions(args),
                    sourceItems,
                    extendedSamples,
                    generator,
                    workspaceId,
                    collectionId,
                    generator.Provider,
                    generator.Model,
                    generator.Dimension,
                    ResolveGeneratorNormalize(generator, providerOptions),
                    topK,
                    profileId,
                    minSimilarity,
                    cancellationToken)
                .ConfigureAwait(false);
            extendedReport = AttachProviderMetadata(extendedReport, providerOptions);
            reports.Add(extendedReport);
            await WriteTextAsync(PostgresVectorIndexProviderEvalRunner.SerializeJson(extendedReport), extendedOutputPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(PostgresVectorShadowEvalRunner.BuildMarkdown(extendedReport), extendedMarkdownPath, cancellationToken)
                .ConfigureAwait(false);
        }

        var summary = PostgresVectorShadowEvalRunner.BuildSummary(
            reports,
            BuildPostgresVectorShadowEvalPreconditionDiagnostics(args));
        summary = AttachProviderMetadata(summary, providerOptions);
        await WriteTextAsync(PostgresVectorIndexProviderEvalRunner.SerializeJson(summary), summaryOutputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(PostgresVectorShadowEvalRunner.BuildSummaryMarkdown(summary), summaryMarkdownPath, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"[PostgresVector] Shadow eval summary: {Path.GetFullPath(summaryOutputPath)}");
        Console.WriteLine($"[PostgresVector] Recommendation={summary.Recommendation}, Reports={summary.Reports.Count}, Runtime={summary.UseForRuntime}");
    }

private static async Task ExecutePostgresVectorFreezeGateAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "postgres", "postgres-vector-freeze-gate.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "postgres", "postgres-vector-freeze-gate.md");

        var diagnostics = await ReadJsonFileAsync<PostgresVectorDiagnosticsReport>(
            CommandHelpers.GetOption(args, "--diagnostics-report") ?? Path.Combine("storage", "postgres", "postgres-vector-diagnostics.json"),
            cancellationToken).ConfigureAwait(false);
        var compatibility = await ReadJsonFileAsync<PostgresVectorCompatibilityReport>(
            CommandHelpers.GetOption(args, "--compatibility-report") ?? Path.Combine("storage", "postgres", "postgres-vector-compatibility.json"),
            cancellationToken).ConfigureAwait(false);
        var parity = await ReadJsonFileAsync<PostgresVectorIndexParityReport>(
            CommandHelpers.GetOption(args, "--parity-report") ?? Path.Combine("storage", "postgres", "postgres-vector-parity-report.json"),
            cancellationToken).ConfigureAwait(false);
        var reindexQuality = await ReadJsonFileAsync<PostgresVectorProviderScopedReindexReport>(
            CommandHelpers.GetOption(args, "--reindex-quality-report") ?? Path.Combine("storage", "postgres", "postgres-vector-provider-scoped-reindex-quality-report.json"),
            cancellationToken).ConfigureAwait(false);
        var queryPreview = await ReadJsonFileAsync<PostgresVectorQueryPreviewReport>(
            CommandHelpers.GetOption(args, "--query-preview-report") ?? Path.Combine("storage", "postgres", "postgres-vector-query-preview-report.json"),
            cancellationToken).ConfigureAwait(false);
        var shadowSummary = await ReadJsonFileAsync<PostgresVectorShadowEvalSummaryReport>(
            CommandHelpers.GetOption(args, "--shadow-summary-report") ?? Path.Combine("storage", "postgres", "postgres-vector-shadow-eval-summary.json"),
            cancellationToken).ConfigureAwait(false);
        var p15Passed = IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-a3.json"))
                        && IsP15EvalReportPassed(Path.Combine("eval", "eval-report-p15-extended.json"));

        var report = new VectorPostgresProviderFreezeGateRunner().BuildFreezeGateReport(
            diagnostics,
            compatibility,
            parity,
            reindexQuality,
            queryPreview,
            shadowSummary,
            p15Passed);

        await WriteTextAsync(PostgresVectorIndexProviderEvalRunner.SerializeJson(report), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(VectorPostgresProviderFreezeGateRunner.BuildMarkdown(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);
        await new LearningReadinessFreezeRunner()
            .RunFreezeReportAsync(Path.Combine(Directory.GetCurrentDirectory(), LearningReadinessFreezeRunner.DefaultOutputDirectory), cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[PostgresVector] Freeze gate: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[PostgresVector] Passed={report.Passed}, Recommendation={report.Recommendation}, Status={report.VectorPostgresProvider}, Blocked={report.BlockedReasons.Count}");
    }

private static async Task ExecuteStorageCheckAsync(
        ControlRoomService service,
        CancellationToken cancellationToken)
    {
        var state = service.State;
        const string ProbeWs = "__readiness_probe__";
        const string ProbeColl = "__probe__";
        var probeId = $"probe-{DateTimeOffset.UtcNow.Ticks}";

        Console.WriteLine("\n========================================================");
        Console.WriteLine("          A0 §2.4  存储可读写深度检查");
        Console.WriteLine("========================================================");
        Console.WriteLine($"  存储类型 : {state.StorageKind}");
        Console.WriteLine($"  探针 ID  : {probeId}");
        Console.WriteLine();

        var now = DateTimeOffset.UtcNow;
        var results = new List<StorageCheckResult>
        {
            // 1. IContextStore
            await RunStorageCheckAsync("context-store", cancellationToken, async token =>
            {
                var item = new ContextItem
                {
                    Id = probeId,
                    WorkspaceId = ProbeWs,
                    CollectionId = ProbeColl,
                    Type = "readiness-probe",
                    Content = "readiness probe — safe to delete",
                    CreatedAt = now
                };
                await state.ContextStore.SaveAsync(item, token);
                var readBack = await state.ContextStore.GetAsync(ProbeWs, ProbeColl, probeId, token);
                await state.ContextStore.DeleteAsync(ProbeWs, ProbeColl, probeId, token);
                if (readBack is null || readBack.Id != probeId)
                    throw new InvalidOperationException($"读回 ID 不匹配：expected={probeId}");
                return "写入→读取→删除 成功";
            }),

            // 2. IMemoryStore
            await RunStorageCheckAsync("memory-store", cancellationToken, async token =>
            {
                var item = new ContextMemoryItem
                {
                    Id = probeId,
                    WorkspaceId = ProbeWs,
                    CollectionId = ProbeColl,
                    Type = "readiness-probe",
                    Content = "readiness probe — safe to delete",
                    Layer = ContextMemoryLayer.Working,
                    Status = ContextMemoryStatus.Candidate,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                await state.MemoryStore.SaveAsync(item, token);
                var readBack = await state.MemoryStore.GetAsync(ProbeWs, ProbeColl, probeId, token);
                if (readBack is null || readBack.Id != probeId)
                    throw new InvalidOperationException($"读回 ID 不匹配：expected={probeId}");
                return "写入→读取 成功（接口无 DeleteAsync）";
            }),

            // 3. IRelationStore
            await RunStorageCheckAsync("relation-store", cancellationToken, async token =>
            {
                var relation = new ContextRelation
                {
                    Id = probeId,
                    WorkspaceId = ProbeWs,
                    CollectionId = ProbeColl,
                    SourceId = probeId,
                    TargetId = probeId,
                    RelationType = "readiness-probe",
                    CreatedAt = now
                };
                await state.RelationStore.SaveAsync(relation, token);
                var readBack = await state.RelationStore.QueryBySourceAsync(ProbeWs, ProbeColl, probeId, token);
                if (!readBack.Any(r => r.Id == probeId))
                    throw new InvalidOperationException("写入成功但 QueryBySourceAsync 找不到探针关系");
                return "写入→QueryBySource 成功（接口无 DeleteAsync）";
            }),

            // 4. IConstraintStore
            await RunStorageCheckAsync("constraint-store", cancellationToken, async token =>
            {
                var constraint = new ContextConstraint
                {
                    Id = probeId,
                    WorkspaceId = ProbeWs,
                    CollectionId = ProbeColl,
                    Content = "readiness probe — safe to delete",
                    Level = ConstraintLevel.Soft,
                    Scope = ContextScope.Collection,
                    Status = ContextMemoryStatus.Candidate,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                await state.ConstraintStore.SaveAsync(constraint, token);
                var readBack = await state.ConstraintStore.QueryAsync(new ContextConstraintQuery
                {
                    WorkspaceId = ProbeWs,
                    CollectionId = ProbeColl,
                    Take = 100
                }, token);
                if (!readBack.Any(c => c.Id == probeId))
                    throw new InvalidOperationException("写入成功但 QueryAsync 找不到探针约束");
                return "写入→QueryAsync 成功（接口无 DeleteAsync）";
            }),

            // 5. IContextJobQueue
            await RunStorageCheckAsync("job-queue", cancellationToken, async token =>
            {
                var job = new ContextJob
                {
                    JobId = probeId,
                    WorkspaceId = ProbeWs,
                    CollectionId = ProbeColl,
                    Kind = ContextJobKind.Custom,
                    PayloadJson = "{}",
                    State = ContextJobState.Queued,
                    CreatedAt = now
                };
                await state.JobQueue.EnqueueAsync(job, token);
                var queued = await state.JobQueryStore.QueryAsync(new ContextJobQuery
                {
                    WorkspaceId = ProbeWs,
                    State = ContextJobState.Queued,
                    Take = 100
                }, token);
                if (!queued.Any(j => j.JobId == probeId))
                    throw new InvalidOperationException("入队成功但 QueryAsync 找不到探针作业");
                return "入队→QueryAsync 成功（探针作业将由处理器 Nack 或手动清理）";
            }),

            // 6. IRetrievalTraceStore
            await RunStorageCheckAsync("retrieval-trace", cancellationToken, async token =>
            {
                var trace = new ContextRetrievalTrace
                {
                    RetrievalId = probeId,
                    WorkspaceId = ProbeWs,
                    CollectionId = ProbeColl,
                    QueryText = "readiness probe",
                    CreatedAt = now
                };
                await state.RetrievalTraceStore.SaveAsync(trace, token);
                var readBack = await state.RetrievalTraceStore.QueryRecentAsync(ProbeWs, ProbeColl, 100, token);
                if (!readBack.Any(t => t.RetrievalId == probeId))
                    throw new InvalidOperationException("写入成功但 QueryRecentAsync 找不到探针 trace");
                return "写入→QueryRecent 成功（接口无 DeleteAsync）";
            })
        };

        // 打印结果表格
        int passed = 0, failed = 0;
        Console.WriteLine($"  {"存储",-22} {"状态",-8} {"耗时",7}  说明");
        Console.WriteLine($"  {new string('-', 72)}");
        foreach (var r in results)
        {
            var icon = r.Ok ? "✅" : "❌";
            Console.WriteLine($"  {icon} {r.Name,-20} {r.Status,-8} {r.ElapsedMs,5} ms  {r.Message}");
            if (r.Ok) passed++; else failed++;
        }

        Console.WriteLine();
        Console.WriteLine($"  结论: {passed}/{results.Count} 通过 — {(failed == 0 ? "所有存储可读写 ✅" : $"{failed} 项失败 ❌")}");
        Console.WriteLine("========================================================");
    }
}
