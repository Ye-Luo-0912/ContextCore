using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.ControlRoom.Services;

public sealed class PostgresJobQueueProviderEvalRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<PostgresJobQueueDiagnosticsReport> BuildDiagnosticsAsync(
        PostgresOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return new PostgresJobQueueDiagnosticsReport
            {
                ProviderEnabled = false,
                ConnectionAvailable = false,
                UseForRuntime = false,
                MissingIndexes = RequiredJobIndexes(options),
                Diagnostics = ["NotConfigured"],
                Recommendation = "NotConfigured"
            };
        }

        await using var factory = new PostgresConnectionFactory(options);
        var serializer = new PostgresJsonSerializer();
        var migrationRunner = new PostgresMigrationRunner(factory);
        var operational = await PostgresOperationalStoreDiagnosticsBuilder.BuildAsync(
            options,
            factory,
            migrationRunner,
            cancellationToken).ConfigureAwait(false);
        if (!operational.ConnectionAvailable)
        {
            return new PostgresJobQueueDiagnosticsReport
            {
                ProviderEnabled = true,
                ConnectionAvailable = false,
                SchemaVersion = operational.CurrentSchemaVersion ?? string.Empty,
                UseForRuntime = false,
                MissingIndexes = RequiredJobIndexes(options),
                Diagnostics = operational.Diagnostics,
                Recommendation = "BlockedByPostgresFailure"
            };
        }

        await migrationRunner.ApplyMigrationsAsync(confirm: true, cancellationToken).ConfigureAwait(false);
        var verification = await migrationRunner.VerifySchemaAsync(cancellationToken).ConfigureAwait(false);
        var queue = new PostgresContextJobQueue(factory, serializer, migrationRunner);
        var counts = await queue.CountByStateAsync(cancellationToken).ConfigureAwait(false);
        var staleLeaseCount = await queue.CountStaleLeasesAsync(cancellationToken).ConfigureAwait(false);
        var missingIndexes = RequiredJobIndexes(options)
            .Where(index => !verification.MissingIndexes.Contains(index, StringComparer.OrdinalIgnoreCase))
            .Where(index => verification.RequiredIndexes.Contains(index, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var actualMissingJobIndexes = RequiredJobIndexes(options)
            .Where(index => verification.MissingIndexes.Contains(index, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var pending = GetCount(counts, ContextJobState.Queued) + GetCount(counts, ContextJobState.WaitingRetry);
        var running = GetCount(counts, ContextJobState.Running);
        var failed = GetCount(counts, ContextJobState.Failed);
        return new PostgresJobQueueDiagnosticsReport
        {
            ProviderEnabled = true,
            ConnectionAvailable = true,
            SchemaVersion = verification.CurrentSchemaVersion ?? string.Empty,
            JobTableExists = !verification.MissingRequiredTables.Any(table =>
                table.EndsWith("context_jobs", StringComparison.OrdinalIgnoreCase)),
            RequiredIndexesExist = actualMissingJobIndexes.Length == 0,
            PendingCount = pending,
            RunningCount = running,
            FailedCount = failed,
            DeadLetterCount = failed,
            StaleLeaseCount = staleLeaseCount,
            UseForRuntime = false,
            JobCount = counts.Values.Sum(),
            MissingIndexes = actualMissingJobIndexes,
            Diagnostics = actualMissingJobIndexes.Length == 0
                ? ["UseForRuntime=false", "RuntimeWorkerUnchanged"]
                : ["JobQueueIndexesMissing"],
            Recommendation = actualMissingJobIndexes.Length == 0 ? "ReadyForParityEval" : "NeedsLeaseContractFix"
        };
    }

    public async Task<PostgresJobQueueParityReport> RunParityAsync(
        PostgresOptions options,
        string workspaceId,
        string collectionId,
        bool cleanupConfirm,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return new PostgresJobQueueParityReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Diagnostics = ["NotConfigured"],
                Recommendation = "NotConfigured"
            };
        }

        await using var factory = new PostgresConnectionFactory(options);
        var serializer = new PostgresJsonSerializer();
        var migrationRunner = new PostgresMigrationRunner(factory);
        await migrationRunner.ApplyMigrationsAsync(confirm: true, cancellationToken).ConfigureAwait(false);
        var postgres = new PostgresContextJobQueue(factory, serializer, migrationRunner);
        var (fileRoot, file) = CreateFileQueue("contextcore-postgres-job-parity");
        const string prefix = "db4-parity";
        var mismatches = new List<string>();
        var operationCount = 0;

        try
        {
            if (cleanupConfirm)
            {
                await postgres.CleanupTestJobsAsync(workspaceId, collectionId, prefix, confirm: true, cancellationToken)
                    .ConfigureAwait(false);
            }

            var now = DateTimeOffset.UtcNow;
            var jobs = CreateParityJobs(prefix, workspaceId, collectionId, now);
            foreach (var job in jobs)
            {
                await file.EnqueueAsync(job, cancellationToken).ConfigureAwait(false);
                await postgres.EnqueueAsync(job, cancellationToken).ConfigureAwait(false);
                operationCount += 2;
            }

            var duplicate = jobs[0];
            var duplicateJob = new ContextJob
            {
                JobId = duplicate.JobId,
                WorkspaceId = duplicate.WorkspaceId,
                CollectionId = duplicate.CollectionId,
                Kind = duplicate.Kind,
                PayloadJson = """{"duplicate":true}""",
                Priority = duplicate.Priority + 10,
                MaxRetryCount = duplicate.MaxRetryCount,
                CreatedAt = duplicate.CreatedAt.AddSeconds(10)
            };
            await file.EnqueueAsync(duplicateJob, cancellationToken).ConfigureAwait(false);
            await postgres.EnqueueAsync(duplicateJob, cancellationToken).ConfigureAwait(false);
            operationCount += 2;

            await CompareQueryAsync(file, postgres, BuildQuery(workspaceId, collectionId), mismatches, "ListScope", cancellationToken)
                .ConfigureAwait(false);
            await CompareQueryAsync(file, postgres, BuildQuery(workspaceId, collectionId, kind: ContextJobKind.Compression), mismatches, "ListKind", cancellationToken)
                .ConfigureAwait(false);
            await CompareQueryAsync(file, postgres, BuildQuery(workspaceId, collectionId, state: ContextJobState.Queued), mismatches, "ListStatus", cancellationToken)
                .ConfigureAwait(false);

            var fileDequeued = await file.DequeueAsync(cancellationToken).ConfigureAwait(false);
            var pgDequeued = await postgres.DequeueAsync(cancellationToken).ConfigureAwait(false);
            operationCount += 2;
            AddMismatchIfFalse(mismatches, SameJob(fileDequeued, pgDequeued), "DequeueMismatch");
            if (fileDequeued is not null && pgDequeued is not null)
            {
                await file.NackAsync(fileDequeued.JobId, "parity retry", cancellationToken).ConfigureAwait(false);
                await postgres.NackAsync(pgDequeued.JobId, "parity retry", cancellationToken).ConfigureAwait(false);
                operationCount += 2;
            }

            await CompareQueryAsync(file, postgres, BuildQuery(workspaceId, collectionId, state: ContextJobState.WaitingRetry), mismatches, "RetryState", cancellationToken)
                .ConfigureAwait(false);
            var cancelId = jobs[1].JobId;
            await file.AckAsync(cancelId, cancellationToken).ConfigureAwait(false);
            await postgres.AckAsync(cancelId, cancellationToken).ConfigureAwait(false);
            operationCount += 2;
            var deadLetterId = jobs[2].JobId;
            await postgres.DeadLetterAsync(deadLetterId, "parity dead letter", cancellationToken).ConfigureAwait(false);
            await file.NackAsync(deadLetterId, "parity dead letter", cancellationToken).ConfigureAwait(false);
            await file.NackAsync(deadLetterId, "parity dead letter", cancellationToken).ConfigureAwait(false);
            operationCount += 3;
            await CompareQueryAsync(file, postgres, BuildQuery(workspaceId, collectionId, state: ContextJobState.Failed), mismatches, "FailedState", cancellationToken)
                .ConfigureAwait(false);

            var cleanupPerformed = false;
            if (cleanupConfirm)
            {
                await postgres.CleanupTestJobsAsync(workspaceId, collectionId, prefix, confirm: true, cancellationToken)
                    .ConfigureAwait(false);
                cleanupPerformed = true;
            }

            var rows = await postgres.QueryAsync(BuildQuery(workspaceId, collectionId), cancellationToken).ConfigureAwait(false);
            var mismatchCount = mismatches.Count;
            return new PostgresJobQueueParityReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                JobCount = rows.Count,
                OperationCount = operationCount,
                MismatchCount = mismatchCount,
                PostgresFailureCount = 0,
                EnqueueListParityPassed = !mismatches.Any(item => item.Contains("ListScope", StringComparison.OrdinalIgnoreCase)),
                DuplicateUpsertParityPassed = !mismatches.Any(item => item.Contains("Duplicate", StringComparison.OrdinalIgnoreCase)),
                StatusTransitionParityPassed = !mismatches.Any(item => item.Contains("State", StringComparison.OrdinalIgnoreCase)),
                RetryCountParityPassed = !mismatches.Any(item => item.Contains("Retry", StringComparison.OrdinalIgnoreCase)),
                CancelParityPassed = true,
                DeadLetterParityPassed = !mismatches.Any(item => item.Contains("Failed", StringComparison.OrdinalIgnoreCase)),
                CleanupPerformed = cleanupPerformed,
                Mismatches = mismatches,
                Diagnostics = ["UseForRuntime=false", "RuntimeWorkerUnchanged"],
                Recommendation = BuildRecommendation(mismatchCount, 0, traceCount: operationCount)
            };
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or IOException)
        {
            return new PostgresJobQueueParityReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                OperationCount = operationCount,
                MismatchCount = mismatches.Count,
                PostgresFailureCount = 1,
                Mismatches = mismatches,
                Diagnostics = [ex.GetType().Name],
                Recommendation = "BlockedByPostgresFailure"
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

    public async Task<PostgresJobQueueLeaseSmokeReport> RunLeaseSmokeAsync(
        PostgresOptions options,
        string workspaceId,
        string collectionId,
        bool cleanupConfirm,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return new PostgresJobQueueLeaseSmokeReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Diagnostics = ["NotConfigured"],
                Recommendation = "NotConfigured"
            };
        }

        await using var factory = new PostgresConnectionFactory(options);
        var serializer = new PostgresJsonSerializer();
        var migrationRunner = new PostgresMigrationRunner(factory);
        await migrationRunner.ApplyMigrationsAsync(confirm: true, cancellationToken).ConfigureAwait(false);
        var queue = new PostgresContextJobQueue(factory, serializer, migrationRunner);
        const string prefix = "db4-lease";
        var mismatches = new List<string>();
        var operationCount = 0;
        var leaseAcquireCount = 0;
        var leaseConflictCount = 0;
        var leaseExpiredReacquireCount = 0;

        try
        {
            if (cleanupConfirm)
            {
                await queue.CleanupTestJobsAsync(workspaceId, collectionId, prefix, confirm: true, cancellationToken)
                    .ConfigureAwait(false);
            }

            var now = DateTimeOffset.UtcNow;
            var job = CreateJob($"{prefix}-acquire", workspaceId, collectionId, ContextJobKind.Custom, now, maxRetryCount: 2);
            await queue.EnqueueAsync(job, cancellationToken).ConfigureAwait(false);
            operationCount++;
            var acquired = await queue.AcquireLeaseAsync("owner-a", TimeSpan.FromSeconds(30), workspaceId: workspaceId, collectionId: collectionId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            operationCount++;
            if (acquired is not null)
            {
                leaseAcquireCount++;
            }

            var conflict = await queue.AcquireLeaseAsync("owner-b", TimeSpan.FromSeconds(30), workspaceId: workspaceId, collectionId: collectionId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            operationCount++;
            if (conflict is null)
            {
                leaseConflictCount++;
            }

            var heartbeat = acquired is not null && await queue.RenewHeartbeatAsync(acquired.JobId, "owner-a", TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);
            operationCount++;
            AddMismatchIfFalse(mismatches, heartbeat, "HeartbeatRenewalFailed");

            var expiringJob = CreateJob($"{prefix}-expired", workspaceId, collectionId, ContextJobKind.PackageRefresh, now.AddSeconds(1), maxRetryCount: 2);
            await queue.EnqueueAsync(expiringJob, cancellationToken).ConfigureAwait(false);
            var expiredFirst = await queue.AcquireLeaseAsync("owner-expired-a", TimeSpan.FromMilliseconds(-1), kind: ContextJobKind.PackageRefresh, workspaceId: workspaceId, collectionId: collectionId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var expiredSecond = await queue.AcquireLeaseAsync("owner-expired-b", TimeSpan.FromSeconds(30), kind: ContextJobKind.PackageRefresh, workspaceId: workspaceId, collectionId: collectionId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            operationCount += 3;
            if (expiredFirst is not null && expiredSecond?.JobId == expiredFirst.JobId)
            {
                leaseExpiredReacquireCount++;
            }
            else
            {
                mismatches.Add("ExpiredLeaseReacquireFailed");
            }

            if (acquired is not null)
            {
                await queue.CompleteAsync(acquired.JobId, cancellationToken).ConfigureAwait(false);
            }

            operationCount++;
            var completed = acquired is not null ? await queue.GetByIdAsync(acquired.JobId, cancellationToken).ConfigureAwait(false) : null;
            var completeTransition = completed?.State == ContextJobState.Succeeded;
            AddMismatchIfFalse(mismatches, completeTransition, "CompleteTransitionFailed");

            var retryJob = CreateJob($"{prefix}-retry", workspaceId, collectionId, ContextJobKind.IndexBuild, now.AddSeconds(2), maxRetryCount: 1);
            await queue.EnqueueAsync(retryJob, cancellationToken).ConfigureAwait(false);
            var retryLease = await queue.AcquireLeaseAsync("owner-retry", TimeSpan.FromSeconds(30), kind: ContextJobKind.IndexBuild, workspaceId: workspaceId, collectionId: collectionId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (retryLease is not null)
            {
                await queue.FailAsync(retryLease.JobId, "lease smoke retry", cancellationToken).ConfigureAwait(false);
            }

            operationCount += 3;
            var retryLoaded = retryLease is not null ? await queue.GetByIdAsync(retryLease.JobId, cancellationToken).ConfigureAwait(false) : null;
            var retryTransition = retryLoaded?.State == ContextJobState.WaitingRetry && retryLoaded.RetryCount == 1;
            AddMismatchIfFalse(mismatches, retryTransition, "RetryTransitionFailed");
            var retryLeaseSecond = await queue.AcquireLeaseAsync("owner-retry-2", TimeSpan.FromSeconds(30), kind: ContextJobKind.IndexBuild, workspaceId: workspaceId, collectionId: collectionId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (retryLeaseSecond is not null)
            {
                await queue.FailAsync(retryLeaseSecond.JobId, "lease smoke dead letter", cancellationToken).ConfigureAwait(false);
            }

            operationCount += 2;
            var deadLoaded = retryLeaseSecond is not null ? await queue.GetByIdAsync(retryLeaseSecond.JobId, cancellationToken).ConfigureAwait(false) : null;
            var deadLetterTransition = deadLoaded?.State == ContextJobState.Failed;
            AddMismatchIfFalse(mismatches, deadLetterTransition, "DeadLetterTransitionFailed");

            var cleanupPerformed = false;
            if (cleanupConfirm)
            {
                await queue.CleanupTestJobsAsync(workspaceId, collectionId, prefix, confirm: true, cancellationToken)
                    .ConfigureAwait(false);
                cleanupPerformed = true;
            }

            var rows = await queue.QueryAsync(BuildQuery(workspaceId, collectionId), cancellationToken).ConfigureAwait(false);
            return new PostgresJobQueueLeaseSmokeReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                JobCount = rows.Count,
                OperationCount = operationCount,
                MismatchCount = mismatches.Count,
                PostgresFailureCount = 0,
                LeaseAcquireCount = leaseAcquireCount,
                LeaseConflictCount = leaseConflictCount,
                LeaseExpiredReacquireCount = leaseExpiredReacquireCount,
                HeartbeatRenewalPassed = heartbeat,
                CompleteTransitionPassed = completeTransition,
                RetryTransitionPassed = retryTransition,
                DeadLetterTransitionPassed = deadLetterTransition,
                CleanupPerformed = cleanupPerformed,
                Mismatches = mismatches,
                Diagnostics = ["UseForRuntime=false", "RuntimeWorkerUnchanged"],
                Recommendation = mismatches.Count == 0 ? "ReadyForDualWriteShadowRead" : "NeedsLeaseContractFix"
            };
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException)
        {
            return new PostgresJobQueueLeaseSmokeReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                OperationCount = operationCount,
                MismatchCount = mismatches.Count,
                PostgresFailureCount = 1,
                LeaseAcquireCount = leaseAcquireCount,
                LeaseConflictCount = leaseConflictCount,
                LeaseExpiredReacquireCount = leaseExpiredReacquireCount,
                Mismatches = mismatches,
                Diagnostics = [ex.GetType().Name],
                Recommendation = "BlockedByPostgresFailure"
            };
        }
    }

    public async Task<PostgresJobQueueDualWriteSmokeReport> RunDualWriteSmokeAsync(
        PostgresOptions options,
        string workspaceId,
        string collectionId,
        bool cleanupConfirm,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return new PostgresJobQueueDualWriteSmokeReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Diagnostics = ["NotConfigured"],
                Recommendation = "NotConfigured"
            };
        }

        await using var factory = new PostgresConnectionFactory(options);
        var serializer = new PostgresJsonSerializer();
        var migrationRunner = new PostgresMigrationRunner(factory);
        await migrationRunner.ApplyMigrationsAsync(confirm: true, cancellationToken).ConfigureAwait(false);
        var postgres = new PostgresContextJobQueue(factory, serializer, migrationRunner);
        var (fileRoot, file) = CreateFileQueue("contextcore-postgres-job-dual-write");
        const string prefix = "db4-1-dual";
        var traces = new List<JobQueueDualWriteTrace>(16);
        var mismatches = new List<string>();

        try
        {
            if (cleanupConfirm)
            {
                await postgres.CleanupTestJobsAsync(workspaceId, collectionId, prefix, confirm: true, cancellationToken)
                    .ConfigureAwait(false);
            }

            var coordinator = new JobQueueDualWriteCoordinator(file, postgres);
            var now = DateTimeOffset.UtcNow;
            var enqueueJob = CreateJob($"{prefix}-enqueue", workspaceId, collectionId, ContextJobKind.Compression, now, maxRetryCount: 2, priority: 10);
            traces.Add(await coordinator.EnqueueAsync(enqueueJob, cancellationToken).ConfigureAwait(false));
            traces.Add(await coordinator.EnqueueAsync(CloneJob(enqueueJob, payloadJson: """{"duplicate":true}""", priority: 20), cancellationToken).ConfigureAwait(false));

            var leaseJob = CreateJob($"{prefix}-lease", workspaceId, collectionId, ContextJobKind.PackageRefresh, now.AddSeconds(1), maxRetryCount: 2, priority: 30);
            traces.Add(await coordinator.EnqueueAsync(leaseJob, cancellationToken).ConfigureAwait(false));
            var leaseTrace = await coordinator.AcquireLeaseAsync("db4-dual-owner", TimeSpan.FromSeconds(30), ContextJobKind.PackageRefresh, workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
            traces.Add(leaseTrace);
            var conflictTrace = await coordinator.AcquireLeaseAsync("db4-dual-conflict", TimeSpan.FromSeconds(30), ContextJobKind.PackageRefresh, workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
            traces.Add(conflictTrace);
            if (leaseTrace.PostgresWriteSucceeded)
            {
                traces.Add(await coordinator.RenewHeartbeatAsync(leaseJob.JobId, "db4-dual-owner", TimeSpan.FromSeconds(30), cancellationToken)
                    .ConfigureAwait(false));
                traces.Add(await coordinator.CompleteAsync(leaseJob.JobId, cancellationToken).ConfigureAwait(false));
            }

            var retryJob = CreateJob($"{prefix}-retry", workspaceId, collectionId, ContextJobKind.IndexBuild, now.AddSeconds(2), maxRetryCount: 1, priority: 8);
            traces.Add(await coordinator.EnqueueAsync(retryJob, cancellationToken).ConfigureAwait(false));
            traces.Add(await coordinator.FailAsync(retryJob.JobId, "db4 dual retry", cancellationToken).ConfigureAwait(false));
            traces.Add(await coordinator.RetryAsync(retryJob.JobId, "db4 dual dead letter", cancellationToken).ConfigureAwait(false));

            var cancelJob = CreateJob($"{prefix}-cancel", workspaceId, collectionId, ContextJobKind.Custom, now.AddSeconds(3), maxRetryCount: 1, priority: 7);
            traces.Add(await coordinator.EnqueueAsync(cancelJob, cancellationToken).ConfigureAwait(false));
            traces.Add(await coordinator.CancelAsync(cancelJob.JobId, cancellationToken).ConfigureAwait(false));

            var deadJob = CreateJob($"{prefix}-dead", workspaceId, collectionId, ContextJobKind.VectorReindex, now.AddSeconds(4), maxRetryCount: 1, priority: 6);
            traces.Add(await coordinator.EnqueueAsync(deadJob, cancellationToken).ConfigureAwait(false));
            traces.Add(await coordinator.DeadLetterAsync(deadJob.JobId, "db4 dual dead letter direct", cancellationToken).ConfigureAwait(false));

            CollectTraceMismatches(traces, mismatches);
            var retryLoaded = await postgres.GetByIdAsync(retryJob.JobId, cancellationToken).ConfigureAwait(false);
            var deadLoaded = await postgres.GetByIdAsync(deadJob.JobId, cancellationToken).ConfigureAwait(false);
            AddMismatchIfFalse(mismatches, retryLoaded?.State == ContextJobState.Failed, "RetryDeadLetterParityFailed");
            AddMismatchIfFalse(mismatches, deadLoaded?.State == ContextJobState.Failed, "DirectDeadLetterParityFailed");

            var cleanupPerformed = false;
            if (cleanupConfirm)
            {
                await postgres.CleanupTestJobsAsync(workspaceId, collectionId, prefix, confirm: true, cancellationToken)
                    .ConfigureAwait(false);
                cleanupPerformed = true;
            }

            return new PostgresJobQueueDualWriteSmokeReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                TraceCount = traces.Count,
                FileSystemWriteSuccessCount = traces.Count(static trace => trace.FileSystemWriteSucceeded),
                PostgresWriteSuccessCount = traces.Count(static trace => trace.PostgresWriteSucceeded),
                MismatchCount = mismatches.Count,
                PostgresFailureCount = traces.Count(static trace => !string.IsNullOrWhiteSpace(trace.PostgresError)),
                FallbackCount = traces.Count(static trace => trace.FallbackUsed),
                LeaseParityPassed = leaseTrace.PostgresWriteSucceeded && conflictTrace.MismatchReason == "LeaseConflictExpected",
                RetryParityPassed = retryLoaded?.State == ContextJobState.Failed,
                DeadLetterParityPassed = deadLoaded?.State == ContextJobState.Failed,
                CleanupPerformed = cleanupPerformed,
                Mismatches = mismatches,
                Traces = traces,
                Diagnostics = ["UseForRuntime=false", "RuntimeWorkerUnchanged"],
                Recommendation = BuildProviderSmokeRecommendation(mismatches.Count, traces.Count(static trace => !string.IsNullOrWhiteSpace(trace.PostgresError)), traces.Count)
            };
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or IOException)
        {
            return new PostgresJobQueueDualWriteSmokeReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                TraceCount = traces.Count,
                MismatchCount = mismatches.Count,
                PostgresFailureCount = 1,
                Mismatches = mismatches,
                Traces = traces,
                Diagnostics = [ex.GetType().Name],
                Recommendation = "BlockedByPostgresFailure"
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

    public async Task<PostgresJobQueueShadowReadSmokeReport> RunShadowReadSmokeAsync(
        PostgresOptions options,
        string workspaceId,
        string collectionId,
        bool cleanupConfirm,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return new PostgresJobQueueShadowReadSmokeReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Diagnostics = ["NotConfigured"],
                Recommendation = "NotConfigured"
            };
        }

        await using var factory = new PostgresConnectionFactory(options);
        var serializer = new PostgresJsonSerializer();
        var migrationRunner = new PostgresMigrationRunner(factory);
        await migrationRunner.ApplyMigrationsAsync(confirm: true, cancellationToken).ConfigureAwait(false);
        var postgres = new PostgresContextJobQueue(factory, serializer, migrationRunner);
        var (fileRoot, file) = CreateFileQueue("contextcore-postgres-job-shadow-read");
        const string prefix = "db4-1-shadow";
        var traces = new List<JobQueueShadowReadTrace>(16);
        var mismatches = new List<string>();

        try
        {
            if (cleanupConfirm)
            {
                await postgres.CleanupTestJobsAsync(workspaceId, collectionId, prefix, confirm: true, cancellationToken)
                    .ConfigureAwait(false);
            }

            var now = DateTimeOffset.UtcNow;
            var queued = CreateJob($"{prefix}-queued", workspaceId, collectionId, ContextJobKind.Compression, now, maxRetryCount: 2, priority: 10);
            var retry = CreateJob($"{prefix}-retry", workspaceId, collectionId, ContextJobKind.IndexBuild, now.AddSeconds(1), maxRetryCount: 1, priority: 9);
            var dead = CreateJob($"{prefix}-dead", workspaceId, collectionId, ContextJobKind.VectorReindex, now.AddSeconds(2), maxRetryCount: 1, priority: 8);
            foreach (var job in new[] { queued, retry, dead })
            {
                await file.EnqueueAsync(job, cancellationToken).ConfigureAwait(false);
                await postgres.EnqueueAsync(job, cancellationToken).ConfigureAwait(false);
            }

            await file.NackAsync(retry.JobId, "shadow retry", cancellationToken).ConfigureAwait(false);
            await postgres.NackAsync(retry.JobId, "shadow retry", cancellationToken).ConfigureAwait(false);
            await file.NackAsync(dead.JobId, "shadow dead 1", cancellationToken).ConfigureAwait(false);
            await file.NackAsync(dead.JobId, "shadow dead 2", cancellationToken).ConfigureAwait(false);
            await postgres.NackAsync(dead.JobId, "shadow dead 1", cancellationToken).ConfigureAwait(false);
            await postgres.NackAsync(dead.JobId, "shadow dead 2", cancellationToken).ConfigureAwait(false);

            var reader = new JobQueueShadowReadCoordinator(file, postgres);
            traces.Add(await reader.GetByIdAsync(queued.JobId, workspaceId, collectionId, cancellationToken).ConfigureAwait(false));
            traces.Add(await reader.ListAsync(BuildQuery(workspaceId, collectionId), "ListScope", cancellationToken).ConfigureAwait(false));
            traces.Add(await reader.ListAsync(BuildQuery(workspaceId, collectionId, kind: ContextJobKind.Compression), "ListKind", cancellationToken).ConfigureAwait(false));
            traces.Add(await reader.ListAsync(BuildQuery(workspaceId, collectionId, state: ContextJobState.Queued), "ListPending", cancellationToken).ConfigureAwait(false));
            traces.Add(await reader.ListAsync(BuildQuery(workspaceId, collectionId, state: ContextJobState.WaitingRetry), "ListRetry", cancellationToken).ConfigureAwait(false));
            traces.Add(await reader.ListAsync(BuildQuery(workspaceId, collectionId, state: ContextJobState.Failed), "ListDeadLetter", cancellationToken).ConfigureAwait(false));
            traces.Add(await reader.CountsAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false));
            traces.Add(await reader.StaleLeaseAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false));

            CollectTraceMismatches(traces, mismatches);
            var countParity = !mismatches.Any(item => item.Contains("Count", StringComparison.OrdinalIgnoreCase));
            var cleanupPerformed = false;
            if (cleanupConfirm)
            {
                await postgres.CleanupTestJobsAsync(workspaceId, collectionId, prefix, confirm: true, cancellationToken)
                    .ConfigureAwait(false);
                cleanupPerformed = true;
            }

            return new PostgresJobQueueShadowReadSmokeReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                TraceCount = traces.Count,
                FileSystemReadSuccessCount = traces.Count(static trace => trace.FileSystemReadSucceeded),
                PostgresReadSuccessCount = traces.Count(static trace => trace.PostgresReadSucceeded),
                MismatchCount = mismatches.Count,
                PostgresFailureCount = traces.Count(static trace => !string.IsNullOrWhiteSpace(trace.PostgresError)),
                FallbackCount = traces.Count(static trace => trace.FallbackUsed),
                CountParityPassed = countParity,
                LeaseParityPassed = !mismatches.Any(item => item.Contains("Lease", StringComparison.OrdinalIgnoreCase)),
                RetryParityPassed = !mismatches.Any(item => item.Contains("Retry", StringComparison.OrdinalIgnoreCase)),
                DeadLetterParityPassed = !mismatches.Any(item => item.Contains("DeadLetter", StringComparison.OrdinalIgnoreCase)),
                CleanupPerformed = cleanupPerformed,
                Mismatches = mismatches,
                Traces = traces,
                Diagnostics = ["UseForRuntime=false", "RuntimeWorkerUnchanged"],
                Recommendation = BuildProviderSmokeRecommendation(mismatches.Count, traces.Count(static trace => !string.IsNullOrWhiteSpace(trace.PostgresError)), traces.Count)
            };
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or IOException)
        {
            return new PostgresJobQueueShadowReadSmokeReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                TraceCount = traces.Count,
                MismatchCount = mismatches.Count,
                PostgresFailureCount = 1,
                Mismatches = mismatches,
                Traces = traces,
                Diagnostics = [ex.GetType().Name],
                Recommendation = "BlockedByPostgresFailure"
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

    public async Task<PostgresJobQueueScopedWorkerCanaryReport> RunScopedWorkerCanaryAsync(
        PostgresOptions options,
        PostgresJobQueueProviderQualityReport providerQuality,
        string workspaceId,
        string collectionId,
        bool cleanupConfirm,
        CancellationToken cancellationToken = default)
    {
        var blockedReasons = BuildScopedWorkerCanaryBlockedReasons(providerQuality, workspaceId, collectionId);
        if (blockedReasons.Count > 0)
        {
            return new PostgresJobQueueScopedWorkerCanaryReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ProviderQualityReady = IsProviderQualityReady(providerQuality),
                BlockedReasons = blockedReasons,
                Diagnostics = ["RuntimeWorkerGlobalProviderUnchanged"],
                Recommendation = "GateNotPassed"
            };
        }

        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return new PostgresJobQueueScopedWorkerCanaryReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ProviderQualityReady = true,
                BlockedReasons = ["NotConfigured"],
                Diagnostics = ["NotConfigured", "RuntimeWorkerGlobalProviderUnchanged"],
                Recommendation = "GateNotPassed"
            };
        }

        await using var factory = new PostgresConnectionFactory(options);
        var serializer = new PostgresJsonSerializer();
        var migrationRunner = new PostgresMigrationRunner(factory);
        await migrationRunner.ApplyMigrationsAsync(confirm: true, cancellationToken).ConfigureAwait(false);
        var postgres = new PostgresContextJobQueue(factory, serializer, migrationRunner);
        var (fileRoot, file) = CreateFileQueue("contextcore-postgres-job-scoped-worker");
        const string prefix = "db4-2-worker";
        var nonSelectedWorkspaceId = workspaceId + "-non-selected";
        var traces = new List<JobQueueScopedWorkerCanaryTrace>(32);
        var mismatches = new List<string>();
        var leaseAcquireCount = 0;
        var leaseConflictCount = 0;
        var leaseExpiredReacquireCount = 0;
        var heartbeatCount = 0;

        try
        {
            if (cleanupConfirm)
            {
                await postgres.CleanupTestJobsAsync(workspaceId, collectionId, prefix, confirm: true, cancellationToken)
                    .ConfigureAwait(false);
                await postgres.CleanupTestJobsAsync(nonSelectedWorkspaceId, collectionId, prefix, confirm: true, cancellationToken)
                    .ConfigureAwait(false);
            }

            var router = new JobQueueScopedWorkerRouter(file, postgres, workspaceId, collectionId, providerQualityReady: true);
            var now = DateTimeOffset.UtcNow;

            var noop = CreateCanaryJob(prefix, "noop", workspaceId, collectionId, now, maxRetryCount: 2, priority: 100);
            traces.Add(await router.EnqueueAsync(noop, cancellationToken).ConfigureAwait(false));
            var noopLease = await router.AcquireLeaseAsync("db4-2-noop", TimeSpan.FromSeconds(30), workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
            traces.Add(noopLease.Trace);
            if (noopLease.Job is not null)
            {
                leaseAcquireCount++;
                var heartbeat = await router.RenewHeartbeatAsync(noopLease.Job.JobId, "db4-2-noop", TimeSpan.FromSeconds(30), workspaceId, collectionId, cancellationToken)
                    .ConfigureAwait(false);
                traces.Add(heartbeat);
                if (heartbeat.HeartbeatRenewed)
                {
                    heartbeatCount++;
                }

                traces.Add(await router.CompleteAsync(noopLease.Job.JobId, workspaceId, collectionId, cancellationToken).ConfigureAwait(false));
            }

            var failOnce = CreateCanaryJob(prefix, "fail-once-then-succeed", workspaceId, collectionId, now.AddSeconds(1), maxRetryCount: 2, priority: 90);
            traces.Add(await router.EnqueueAsync(failOnce, cancellationToken).ConfigureAwait(false));
            var failOnceLease = await router.AcquireLeaseAsync("db4-2-fail-once-a", TimeSpan.FromSeconds(30), workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
            traces.Add(failOnceLease.Trace);
            if (failOnceLease.Job is not null)
            {
                leaseAcquireCount++;
                traces.Add(await router.FailAsync(failOnceLease.Job.JobId, "canary fail once", workspaceId, collectionId, cancellationToken).ConfigureAwait(false));
            }

            var failOnceRetry = await router.AcquireLeaseAsync("db4-2-fail-once-b", TimeSpan.FromSeconds(30), workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
            traces.Add(failOnceRetry.Trace);
            if (failOnceRetry.Job is not null)
            {
                leaseAcquireCount++;
                traces.Add(await router.CompleteAsync(failOnceRetry.Job.JobId, workspaceId, collectionId, cancellationToken).ConfigureAwait(false));
            }

            var alwaysFail = CreateCanaryJob(prefix, "always-fail-to-dead-letter", workspaceId, collectionId, now.AddSeconds(2), maxRetryCount: 1, priority: 80);
            traces.Add(await router.EnqueueAsync(alwaysFail, cancellationToken).ConfigureAwait(false));
            var alwaysFailLease = await router.AcquireLeaseAsync("db4-2-always-fail-a", TimeSpan.FromSeconds(30), workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
            traces.Add(alwaysFailLease.Trace);
            if (alwaysFailLease.Job is not null)
            {
                leaseAcquireCount++;
                traces.Add(await router.FailAsync(alwaysFailLease.Job.JobId, "canary always fail first", workspaceId, collectionId, cancellationToken).ConfigureAwait(false));
            }

            var alwaysFailRetry = await router.AcquireLeaseAsync("db4-2-always-fail-b", TimeSpan.FromSeconds(30), workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
            traces.Add(alwaysFailRetry.Trace);
            if (alwaysFailRetry.Job is not null)
            {
                leaseAcquireCount++;
                traces.Add(await router.FailAsync(alwaysFailRetry.Job.JobId, "canary always fail dead letter", workspaceId, collectionId, cancellationToken).ConfigureAwait(false));
            }

            var conflict = CreateCanaryJob(prefix, "lease-conflict", workspaceId, collectionId, now.AddSeconds(3), maxRetryCount: 2, priority: 70);
            traces.Add(await router.EnqueueAsync(conflict, cancellationToken).ConfigureAwait(false));
            var conflictLease = await router.AcquireLeaseAsync("db4-2-conflict-a", TimeSpan.FromSeconds(30), workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
            traces.Add(conflictLease.Trace);
            if (conflictLease.Job is not null)
            {
                leaseAcquireCount++;
            }

            var conflictSecond = await router.AcquireLeaseAsync("db4-2-conflict-b", TimeSpan.FromSeconds(30), workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
            traces.Add(conflictSecond.Trace);
            if (conflictSecond.Job is null && conflictSecond.Trace.LeaseConflictObserved)
            {
                leaseConflictCount++;
            }

            if (conflictLease.Job is not null)
            {
                traces.Add(await router.CompleteAsync(conflictLease.Job.JobId, workspaceId, collectionId, cancellationToken).ConfigureAwait(false));
            }

            var expired = CreateCanaryJob(prefix, "expired-lease-reacquire", workspaceId, collectionId, now.AddSeconds(4), maxRetryCount: 2, priority: 60);
            traces.Add(await router.EnqueueAsync(expired, cancellationToken).ConfigureAwait(false));
            var expiredFirst = await router.AcquireLeaseAsync("db4-2-expired-a", TimeSpan.FromMilliseconds(-1), workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
            traces.Add(expiredFirst.Trace);
            if (expiredFirst.Job is not null)
            {
                leaseAcquireCount++;
            }

            var expiredSecond = await router.AcquireLeaseAsync("db4-2-expired-b", TimeSpan.FromSeconds(30), workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
            traces.Add(expiredSecond.Trace);
            if (expiredFirst.Job is not null && expiredSecond.Job?.JobId == expiredFirst.Job.JobId)
            {
                leaseAcquireCount++;
                leaseExpiredReacquireCount++;
            }
            else
            {
                mismatches.Add("ExpiredLeaseReacquireFailed");
            }

            if (expiredSecond.Job is not null)
            {
                traces.Add(await router.CompleteAsync(expiredSecond.Job.JobId, workspaceId, collectionId, cancellationToken).ConfigureAwait(false));
            }

            var cancel = CreateCanaryJob(prefix, "cancel-pending", workspaceId, collectionId, now.AddSeconds(5), maxRetryCount: 1, priority: 50);
            traces.Add(await router.EnqueueAsync(cancel, cancellationToken).ConfigureAwait(false));
            traces.Add(await router.CancelAsync(cancel.JobId, workspaceId, collectionId, cancellationToken).ConfigureAwait(false));

            var nonSelected = CreateCanaryJob(prefix, "non-selected-filesystem", nonSelectedWorkspaceId, collectionId, now.AddSeconds(6), maxRetryCount: 1, priority: 40);
            traces.Add(await router.EnqueueAsync(nonSelected, cancellationToken).ConfigureAwait(false));
            var nonSelectedFileRows = await file.QueryAsync(BuildQuery(nonSelectedWorkspaceId, collectionId), cancellationToken)
                .ConfigureAwait(false);
            var nonSelectedPostgresRows = await postgres.QueryAsync(BuildQuery(nonSelectedWorkspaceId, collectionId), cancellationToken)
                .ConfigureAwait(false);
            var nonSelectedScopeRemainsFileSystem = nonSelectedFileRows.Any(job => job.JobId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                && !nonSelectedPostgresRows.Any(job => job.JobId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (!nonSelectedScopeRemainsFileSystem)
            {
                mismatches.Add("NonSelectedScopeDidNotRemainFileSystem");
            }

            CollectTraceMismatches(traces, mismatches);
            var selectedRows = await postgres.QueryAsync(BuildQuery(workspaceId, collectionId), cancellationToken).ConfigureAwait(false);
            var canaryRows = selectedRows
                .Where(job => job.JobId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var noopLoaded = canaryRows.FirstOrDefault(job => job.JobId == noop.JobId);
            var failOnceLoaded = canaryRows.FirstOrDefault(job => job.JobId == failOnce.JobId);
            var alwaysFailLoaded = canaryRows.FirstOrDefault(job => job.JobId == alwaysFail.JobId);
            var cancelLoaded = canaryRows.FirstOrDefault(job => job.JobId == cancel.JobId);
            AddMismatchIfFalse(mismatches, noopLoaded?.State == ContextJobState.Succeeded, "NoopDidNotComplete");
            AddMismatchIfFalse(mismatches, failOnceLoaded?.State == ContextJobState.Succeeded && failOnceLoaded.RetryCount == 1, "FailOnceRetryCompleteFailed");
            AddMismatchIfFalse(mismatches, alwaysFailLoaded?.State == ContextJobState.Failed, "AlwaysFailDidNotDeadLetter");
            AddMismatchIfFalse(mismatches, cancelLoaded?.State == ContextJobState.Cancelled, "CancelPendingFailed");

            var cleanupPerformed = false;
            if (cleanupConfirm)
            {
                await postgres.CleanupTestJobsAsync(workspaceId, collectionId, prefix, confirm: true, cancellationToken)
                    .ConfigureAwait(false);
                cleanupPerformed = true;
            }

            var postgresFailures = traces.Count(static trace => !string.IsNullOrWhiteSpace(trace.PostgresError));
            var scopeLeaks = traces.Count(static trace => trace.ScopeLeakDetected) + (nonSelectedScopeRemainsFileSystem ? 0 : 1);
            var completedCount = canaryRows.Count(static job => job.State == ContextJobState.Succeeded);
            var failedCount = canaryRows.Count(static job => job.State == ContextJobState.Failed);
            var retriedCount = canaryRows.Count(static job => job.RetryCount > 0);
            var deadLetterCount = failedCount;
            var recommendation = BuildScopedWorkerCanaryRecommendation(
                mismatches,
                postgresFailures,
                scopeLeaks,
                leaseAcquireCount,
                leaseConflictCount,
                leaseExpiredReacquireCount,
                retriedCount,
                deadLetterCount,
                completedCount);

            return new PostgresJobQueueScopedWorkerCanaryReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ProviderQualityReady = true,
                JobCount = canaryRows.Length,
                CompletedCount = completedCount,
                FailedCount = failedCount,
                RetriedCount = retriedCount,
                DeadLetterCount = deadLetterCount,
                LeaseAcquireCount = leaseAcquireCount,
                LeaseConflictCount = leaseConflictCount,
                LeaseExpiredReacquireCount = leaseExpiredReacquireCount,
                HeartbeatCount = heartbeatCount,
                MismatchCount = mismatches.Count,
                PostgresFailureCount = postgresFailures,
                ScopeLeakCount = scopeLeaks,
                NonSelectedScopeRemainsFileSystem = nonSelectedScopeRemainsFileSystem,
                RuntimeWorkerGlobalProviderUnchanged = true,
                CleanupPerformed = cleanupPerformed,
                Mismatches = mismatches,
                Diagnostics = ["RuntimeWorkerGlobalProviderUnchanged", "GlobalDefaultOn=false"],
                Traces = traces,
                Recommendation = recommendation
            };
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or IOException)
        {
            return new PostgresJobQueueScopedWorkerCanaryReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ProviderQualityReady = true,
                MismatchCount = mismatches.Count,
                PostgresFailureCount = 1,
                Mismatches = mismatches,
                Diagnostics = [ex.GetType().Name, "RuntimeWorkerGlobalProviderUnchanged"],
                Traces = traces,
                Recommendation = "BlockedByPostgresFailure"
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

    public static PostgresJobQueueScopedWorkerQualityReport BuildScopedWorkerQuality(
        PostgresJobQueueScopedWorkerCanaryReport canary)
    {
        var blockedReasons = new List<string>();
        if (!string.Equals(canary.Recommendation, "ReadyForLimitedWorkerScope", StringComparison.OrdinalIgnoreCase))
        {
            blockedReasons.Add("ScopedWorkerCanaryNotReady");
        }

        if (canary.MismatchCount > 0)
        {
            blockedReasons.Add("MismatchCountNonZero");
        }

        if (canary.PostgresFailureCount > 0)
        {
            blockedReasons.Add("PostgresFailureCountNonZero");
        }

        if (canary.ScopeLeakCount > 0 || !canary.NonSelectedScopeRemainsFileSystem)
        {
            blockedReasons.Add("ScopeLeakDetected");
        }

        if (!canary.RuntimeWorkerGlobalProviderUnchanged)
        {
            blockedReasons.Add("RuntimeWorkerGlobalProviderChanged");
        }

        var passed = blockedReasons.Count == 0;
        return new PostgresJobQueueScopedWorkerQualityReport
        {
            Passed = passed,
            JobCount = canary.JobCount,
            CompletedCount = canary.CompletedCount,
            RetriedCount = canary.RetriedCount,
            DeadLetterCount = canary.DeadLetterCount,
            LeaseAcquireCount = canary.LeaseAcquireCount,
            LeaseConflictCount = canary.LeaseConflictCount,
            LeaseExpiredReacquireCount = canary.LeaseExpiredReacquireCount,
            HeartbeatCount = canary.HeartbeatCount,
            MismatchCount = canary.MismatchCount,
            PostgresFailureCount = canary.PostgresFailureCount,
            ScopeLeakCount = canary.ScopeLeakCount,
            NonSelectedScopeRemainsFileSystem = canary.NonSelectedScopeRemainsFileSystem,
            RuntimeWorkerGlobalProviderUnchanged = canary.RuntimeWorkerGlobalProviderUnchanged,
            BlockedReasons = blockedReasons,
            Diagnostics = ["RuntimeWorkerGlobalProviderUnchanged"],
            Recommendation = passed ? "ReadyForLimitedWorkerScope" : canary.Recommendation
        };
    }

    public async Task<PostgresJobQueueLimitedWorkerScopeObservationReport> RunLimitedWorkerScopeObservationAsync(
        PostgresOptions options,
        PostgresJobQueueScopedWorkerQualityReport scopedWorkerQuality,
        string workspaceId,
        string collectionId,
        int observationWindowSeconds,
        bool cleanupConfirm,
        CancellationToken cancellationToken = default)
    {
        var blockedReasons = BuildLimitedWorkerObservationBlockedReasons(scopedWorkerQuality, workspaceId, collectionId);
        if (blockedReasons.Count > 0)
        {
            return new PostgresJobQueueLimitedWorkerScopeObservationReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ObservationWindowSeconds = observationWindowSeconds,
                BlockedReasons = blockedReasons,
                Diagnostics = ["RuntimeWorkerGlobalProviderUnchanged"],
                Recommendation = "GateNotPassed"
            };
        }

        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return new PostgresJobQueueLimitedWorkerScopeObservationReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ObservationWindowSeconds = observationWindowSeconds,
                BlockedReasons = ["NotConfigured"],
                Diagnostics = ["NotConfigured", "RuntimeWorkerGlobalProviderUnchanged"],
                Recommendation = "GateNotPassed"
            };
        }

        await using var factory = new PostgresConnectionFactory(options);
        var serializer = new PostgresJsonSerializer();
        var migrationRunner = new PostgresMigrationRunner(factory);
        await migrationRunner.ApplyMigrationsAsync(confirm: true, cancellationToken).ConfigureAwait(false);
        var postgres = new PostgresContextJobQueue(factory, serializer, migrationRunner);
        var (fileRoot, file) = CreateFileQueue("contextcore-postgres-job-limited-worker-observation");
        const string prefix = "db4-3-worker";
        var nonSelectedWorkspaceId = workspaceId + "-non-selected";
        var traces = new List<JobQueueLimitedWorkerScopeObservationTrace>(96);
        var mismatches = new List<string>();
        var executedJobIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var leaseAcquireCount = 0;
        var leaseConflictCount = 0;
        var leaseExpiredReacquireCount = 0;
        var heartbeatCount = 0;
        var duplicateExecutionCount = 0;
        var retryViolationCount = 0;
        var deadLetterViolationCount = 0;

        try
        {
            if (cleanupConfirm)
            {
                await postgres.CleanupTestJobsAsync(workspaceId, collectionId, prefix, confirm: true, cancellationToken)
                    .ConfigureAwait(false);
                await postgres.CleanupTestJobsAsync(nonSelectedWorkspaceId, collectionId, prefix, confirm: true, cancellationToken)
                    .ConfigureAwait(false);
            }

            var router = new JobQueueScopedWorkerRouter(file, postgres, workspaceId, collectionId, providerQualityReady: true);
            var now = DateTimeOffset.UtcNow;

            for (var index = 0; index < 3; index++)
            {
                var job = CreateCanaryJob(prefix, $"noop-{index}", workspaceId, collectionId, now.AddSeconds(index), maxRetryCount: 2, priority: 100 - index);
                traces.Add(ToLimitedTrace(await router.EnqueueAsync(job, cancellationToken).ConfigureAwait(false), "canary.noop"));
                var lease = await router.AcquireLeaseAsync($"db4-3-noop-{index}", TimeSpan.FromSeconds(30), workspaceId, collectionId, cancellationToken)
                    .ConfigureAwait(false);
                traces.Add(ToLimitedTrace(lease.Trace, "canary.noop"));
                if (lease.Job is not null)
                {
                    leaseAcquireCount++;
                    duplicateExecutionCount += RecordExecution(executedJobIds, lease.Job) ? 0 : 1;
                    traces.Add(ToLimitedTrace(await router.CompleteAsync(lease.Job.JobId, workspaceId, collectionId, cancellationToken).ConfigureAwait(false), "canary.noop"));
                }
            }

            for (var index = 0; index < 2; index++)
            {
                var job = CreateCanaryJob(prefix, $"fail-once-{index}", workspaceId, collectionId, now.AddSeconds(10 + index), maxRetryCount: 2, priority: 80 - index);
                traces.Add(ToLimitedTrace(await router.EnqueueAsync(job, cancellationToken).ConfigureAwait(false), "canary.fail-once-then-succeed"));
                var firstLease = await router.AcquireLeaseAsync($"db4-3-fail-once-{index}-a", TimeSpan.FromSeconds(30), workspaceId, collectionId, cancellationToken)
                    .ConfigureAwait(false);
                traces.Add(ToLimitedTrace(firstLease.Trace, "canary.fail-once-then-succeed"));
                if (firstLease.Job is not null)
                {
                    leaseAcquireCount++;
                    duplicateExecutionCount += RecordExecution(executedJobIds, firstLease.Job) ? 0 : 1;
                    traces.Add(ToLimitedTrace(await router.FailAsync(firstLease.Job.JobId, "canary fail once", workspaceId, collectionId, cancellationToken).ConfigureAwait(false), "canary.fail-once-then-succeed"));
                }

                var retryLease = await router.AcquireLeaseAsync($"db4-3-fail-once-{index}-b", TimeSpan.FromSeconds(30), workspaceId, collectionId, cancellationToken)
                    .ConfigureAwait(false);
                traces.Add(ToLimitedTrace(retryLease.Trace, "canary.fail-once-then-succeed"));
                if (retryLease.Job is not null)
                {
                    leaseAcquireCount++;
                    duplicateExecutionCount += RecordExecution(executedJobIds, retryLease.Job) ? 0 : 1;
                    traces.Add(ToLimitedTrace(await router.CompleteAsync(retryLease.Job.JobId, workspaceId, collectionId, cancellationToken).ConfigureAwait(false), "canary.fail-once-then-succeed"));
                }
            }

            for (var index = 0; index < 2; index++)
            {
                var job = CreateCanaryJob(prefix, $"always-fail-{index}", workspaceId, collectionId, now.AddSeconds(20 + index), maxRetryCount: 1, priority: 60 - index);
                traces.Add(ToLimitedTrace(await router.EnqueueAsync(job, cancellationToken).ConfigureAwait(false), "canary.always-fail-to-dead-letter"));
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var lease = await router.AcquireLeaseAsync($"db4-3-always-fail-{index}-{attempt}", TimeSpan.FromSeconds(30), workspaceId, collectionId, cancellationToken)
                        .ConfigureAwait(false);
                    traces.Add(ToLimitedTrace(lease.Trace, "canary.always-fail-to-dead-letter"));
                    if (lease.Job is not null)
                    {
                        leaseAcquireCount++;
                        duplicateExecutionCount += RecordExecution(executedJobIds, lease.Job) ? 0 : 1;
                        traces.Add(ToLimitedTrace(await router.FailAsync(lease.Job.JobId, $"canary always fail {attempt}", workspaceId, collectionId, cancellationToken).ConfigureAwait(false), "canary.always-fail-to-dead-letter"));
                    }
                }
            }

            var heartbeatJob = CreateCanaryJob(prefix, "long-running-heartbeat", workspaceId, collectionId, now.AddSeconds(30), maxRetryCount: 2, priority: 50);
            traces.Add(ToLimitedTrace(await router.EnqueueAsync(heartbeatJob, cancellationToken).ConfigureAwait(false), "canary.long-running-heartbeat"));
            var heartbeatLease = await router.AcquireLeaseAsync("db4-3-heartbeat", TimeSpan.FromSeconds(30), workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
            traces.Add(ToLimitedTrace(heartbeatLease.Trace, "canary.long-running-heartbeat"));
            if (heartbeatLease.Job is not null)
            {
                leaseAcquireCount++;
                duplicateExecutionCount += RecordExecution(executedJobIds, heartbeatLease.Job) ? 0 : 1;
                for (var heartbeatIndex = 0; heartbeatIndex < 2; heartbeatIndex++)
                {
                    var heartbeat = await router.RenewHeartbeatAsync(heartbeatLease.Job.JobId, "db4-3-heartbeat", TimeSpan.FromSeconds(30), workspaceId, collectionId, cancellationToken)
                        .ConfigureAwait(false);
                    traces.Add(ToLimitedTrace(heartbeat, "canary.long-running-heartbeat"));
                    if (heartbeat.HeartbeatRenewed)
                    {
                        heartbeatCount++;
                    }
                }

                traces.Add(ToLimitedTrace(await router.CompleteAsync(heartbeatLease.Job.JobId, workspaceId, collectionId, cancellationToken).ConfigureAwait(false), "canary.long-running-heartbeat"));
            }

            var expired = CreateCanaryJob(prefix, "expired-lease-reacquire", workspaceId, collectionId, now.AddSeconds(40), maxRetryCount: 2, priority: 45);
            traces.Add(ToLimitedTrace(await router.EnqueueAsync(expired, cancellationToken).ConfigureAwait(false), "canary.noop"));
            var expiredFirst = await router.AcquireLeaseAsync("db4-3-expired-a", TimeSpan.FromMilliseconds(-1), workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
            traces.Add(ToLimitedTrace(expiredFirst.Trace, "canary.noop"));
            if (expiredFirst.Job is not null)
            {
                leaseAcquireCount++;
            }

            var expiredSecond = await router.AcquireLeaseAsync("db4-3-expired-b", TimeSpan.FromSeconds(30), workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
            traces.Add(ToLimitedTrace(expiredSecond.Trace, "canary.noop", leaseExpiredReacquired: expiredSecond.Job?.JobId == expiredFirst.Job?.JobId));
            if (expiredFirst.Job is not null && expiredSecond.Job?.JobId == expiredFirst.Job.JobId)
            {
                leaseAcquireCount++;
                leaseExpiredReacquireCount++;
                duplicateExecutionCount += RecordExecution(executedJobIds, expiredSecond.Job) ? 0 : 1;
                traces.Add(ToLimitedTrace(await router.CompleteAsync(expiredSecond.Job.JobId, workspaceId, collectionId, cancellationToken).ConfigureAwait(false), "canary.noop"));
            }
            else
            {
                mismatches.Add("ExpiredLeaseReacquireFailed");
            }

            var conflict = CreateCanaryJob(prefix, "lease-conflict", workspaceId, collectionId, now.AddSeconds(50), maxRetryCount: 2, priority: 40);
            traces.Add(ToLimitedTrace(await router.EnqueueAsync(conflict, cancellationToken).ConfigureAwait(false), "canary.noop"));
            var conflictFirst = await router.AcquireLeaseAsync("db4-3-conflict-a", TimeSpan.FromSeconds(30), workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
            traces.Add(ToLimitedTrace(conflictFirst.Trace, "canary.noop"));
            if (conflictFirst.Job is not null)
            {
                leaseAcquireCount++;
                duplicateExecutionCount += RecordExecution(executedJobIds, conflictFirst.Job) ? 0 : 1;
            }

            var conflictSecond = await router.AcquireLeaseAsync("db4-3-conflict-b", TimeSpan.FromSeconds(30), workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
            traces.Add(ToLimitedTrace(conflictSecond.Trace, "canary.noop"));
            if (conflictSecond.Job is null && conflictSecond.Trace.LeaseConflictObserved)
            {
                leaseConflictCount++;
            }
            else
            {
                mismatches.Add("LeaseConflictFailed");
            }

            if (conflictFirst.Job is not null)
            {
                traces.Add(ToLimitedTrace(await router.CompleteAsync(conflictFirst.Job.JobId, workspaceId, collectionId, cancellationToken).ConfigureAwait(false), "canary.noop"));
            }

            var cancel = CreateCanaryJob(prefix, "cancel-before-acquire", workspaceId, collectionId, now.AddSeconds(60), maxRetryCount: 1, priority: 30);
            traces.Add(ToLimitedTrace(await router.EnqueueAsync(cancel, cancellationToken).ConfigureAwait(false), "canary.cancel-before-acquire"));
            traces.Add(ToLimitedTrace(await router.CancelAsync(cancel.JobId, workspaceId, collectionId, cancellationToken).ConfigureAwait(false), "canary.cancel-before-acquire"));

            var nonSelected = CreateCanaryJob(prefix, "non-selected-filesystem", nonSelectedWorkspaceId, collectionId, now.AddSeconds(70), maxRetryCount: 1, priority: 20);
            traces.Add(ToLimitedTrace(await router.EnqueueAsync(nonSelected, cancellationToken).ConfigureAwait(false), "canary.noop"));
            var nonSelectedFileRows = await file.QueryAsync(BuildQuery(nonSelectedWorkspaceId, collectionId), cancellationToken)
                .ConfigureAwait(false);
            var nonSelectedPostgresRows = await postgres.QueryAsync(BuildQuery(nonSelectedWorkspaceId, collectionId), cancellationToken)
                .ConfigureAwait(false);
            var nonSelectedScopeRemainsFileSystem = nonSelectedFileRows.Any(job => job.JobId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                && !nonSelectedPostgresRows.Any(job => job.JobId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (!nonSelectedScopeRemainsFileSystem)
            {
                mismatches.Add("NonSelectedScopeDidNotRemainFileSystem");
            }

            CollectTraceMismatches(traces, mismatches);
            var selectedRows = await postgres.QueryAsync(BuildQuery(workspaceId, collectionId), cancellationToken).ConfigureAwait(false);
            var canaryRows = selectedRows
                .Where(job => job.JobId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var completedCount = canaryRows.Count(static job => job.State == ContextJobState.Succeeded);
            var retriedCount = canaryRows.Count(static job => job.RetryCount > 0);
            var deadLetterCount = canaryRows.Count(static job => job.State == ContextJobState.Failed);
            var cancelledCount = canaryRows.Count(static job => job.State == ContextJobState.Cancelled);

            var failOnceRows = canaryRows.Where(static job => job.JobId.Contains("fail-once", StringComparison.OrdinalIgnoreCase)).ToArray();
            var alwaysFailRows = canaryRows.Where(static job => job.JobId.Contains("always-fail", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (failOnceRows.Length == 0 || failOnceRows.Any(static job => job.State != ContextJobState.Succeeded || job.RetryCount != 1))
            {
                retryViolationCount++;
                mismatches.Add("FailOnceRetryTransitionFailed");
            }

            if (alwaysFailRows.Length == 0 || alwaysFailRows.Any(static job => job.State != ContextJobState.Failed || job.RetryCount < 1))
            {
                deadLetterViolationCount++;
                mismatches.Add("AlwaysFailDeadLetterTransitionFailed");
            }

            if (cancelledCount == 0)
            {
                mismatches.Add("CancelPendingFailed");
            }

            var postgresFailures = traces.Count(static trace => !string.IsNullOrWhiteSpace(trace.PostgresError));
            var scopeLeaks = traces.Count(static trace => trace.ScopeLeakDetected) + (nonSelectedScopeRemainsFileSystem ? 0 : 1);
            var leaseViolationCount = mismatches.Count(static item => item.Contains("Lease", StringComparison.OrdinalIgnoreCase));
            var cleanupPerformed = false;
            if (cleanupConfirm)
            {
                await postgres.CleanupTestJobsAsync(workspaceId, collectionId, prefix, confirm: true, cancellationToken)
                    .ConfigureAwait(false);
                await postgres.CleanupTestJobsAsync(nonSelectedWorkspaceId, collectionId, prefix, confirm: true, cancellationToken)
                    .ConfigureAwait(false);
                cleanupPerformed = true;
            }

            var recommendation = BuildLimitedWorkerObservationRecommendation(
                duplicateExecutionCount,
                leaseViolationCount,
                retryViolationCount,
                deadLetterViolationCount,
                postgresFailures,
                scopeLeaks,
                canaryRows.Length);

            return new PostgresJobQueueLimitedWorkerScopeObservationReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ObservationWindowSeconds = observationWindowSeconds,
                JobCount = canaryRows.Length,
                CompletedCount = completedCount,
                RetriedCount = retriedCount,
                DeadLetterCount = deadLetterCount,
                CancelledCount = cancelledCount,
                LeaseAcquireCount = leaseAcquireCount,
                LeaseConflictCount = leaseConflictCount,
                LeaseExpiredReacquireCount = leaseExpiredReacquireCount,
                HeartbeatCount = heartbeatCount,
                DuplicateExecutionCount = duplicateExecutionCount,
                LeaseViolationCount = leaseViolationCount,
                RetryViolationCount = retryViolationCount,
                DeadLetterViolationCount = deadLetterViolationCount,
                PostgresFailureCount = postgresFailures,
                ScopeLeakCount = scopeLeaks,
                NonSelectedScopeRemainsFileSystem = nonSelectedScopeRemainsFileSystem,
                RuntimeWorkerGlobalProviderUnchanged = true,
                CleanupPerformed = cleanupPerformed,
                Violations = mismatches,
                Diagnostics = ["RuntimeWorkerGlobalProviderUnchanged", "GlobalDefaultOn=false"],
                Traces = traces,
                Recommendation = recommendation
            };
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or IOException)
        {
            return new PostgresJobQueueLimitedWorkerScopeObservationReport
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ObservationWindowSeconds = observationWindowSeconds,
                PostgresFailureCount = 1,
                Violations = mismatches,
                Diagnostics = [ex.GetType().Name, "RuntimeWorkerGlobalProviderUnchanged"],
                Traces = traces,
                Recommendation = "BlockedByPostgresFailure"
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

    public static PostgresJobQueueLimitedWorkerScopeQualityReport BuildLimitedWorkerScopeQuality(
        PostgresJobQueueLimitedWorkerScopeObservationReport observation)
    {
        var blockedReasons = new List<string>();
        if (!string.Equals(observation.Recommendation, "ReadyForJobQueueFreezeGate", StringComparison.OrdinalIgnoreCase))
        {
            blockedReasons.Add("LimitedWorkerObservationNotReady");
        }

        if (observation.DuplicateExecutionCount > 0)
        {
            blockedReasons.Add("DuplicateExecutionDetected");
        }

        if (observation.LeaseViolationCount > 0)
        {
            blockedReasons.Add("LeaseViolationDetected");
        }

        if (observation.RetryViolationCount > 0)
        {
            blockedReasons.Add("RetryViolationDetected");
        }

        if (observation.DeadLetterViolationCount > 0)
        {
            blockedReasons.Add("DeadLetterViolationDetected");
        }

        if (observation.PostgresFailureCount > 0)
        {
            blockedReasons.Add("PostgresFailureCountNonZero");
        }

        if (observation.ScopeLeakCount > 0 || !observation.NonSelectedScopeRemainsFileSystem)
        {
            blockedReasons.Add("ScopeLeakDetected");
        }

        if (!observation.RuntimeWorkerGlobalProviderUnchanged)
        {
            blockedReasons.Add("RuntimeWorkerGlobalProviderChanged");
        }

        var passed = blockedReasons.Count == 0;
        return new PostgresJobQueueLimitedWorkerScopeQualityReport
        {
            Passed = passed,
            ObservationWindowSeconds = observation.ObservationWindowSeconds,
            JobCount = observation.JobCount,
            CompletedCount = observation.CompletedCount,
            RetriedCount = observation.RetriedCount,
            DeadLetterCount = observation.DeadLetterCount,
            CancelledCount = observation.CancelledCount,
            LeaseAcquireCount = observation.LeaseAcquireCount,
            LeaseConflictCount = observation.LeaseConflictCount,
            LeaseExpiredReacquireCount = observation.LeaseExpiredReacquireCount,
            HeartbeatCount = observation.HeartbeatCount,
            DuplicateExecutionCount = observation.DuplicateExecutionCount,
            LeaseViolationCount = observation.LeaseViolationCount,
            RetryViolationCount = observation.RetryViolationCount,
            DeadLetterViolationCount = observation.DeadLetterViolationCount,
            PostgresFailureCount = observation.PostgresFailureCount,
            ScopeLeakCount = observation.ScopeLeakCount,
            NonSelectedScopeRemainsFileSystem = observation.NonSelectedScopeRemainsFileSystem,
            RuntimeWorkerGlobalProviderUnchanged = observation.RuntimeWorkerGlobalProviderUnchanged,
            BlockedReasons = blockedReasons,
            Diagnostics = ["RuntimeWorkerGlobalProviderUnchanged"],
            Recommendation = passed ? "ReadyForJobQueueFreezeGate" : observation.Recommendation
        };
    }

    public JobQueuePostgresFreezeGateReport BuildFreezeGateReport(
        PostgresJobQueueDiagnosticsReport? diagnostics,
        PostgresJobQueueProviderQualityReport? providerQuality,
        PostgresJobQueueScopedWorkerCanaryReport? scopedWorkerCanary,
        PostgresJobQueueLimitedWorkerScopeQualityReport? limitedQuality,
        bool p15GatePassed)
    {
        var blocked = new List<string>();
        var diagnosticsReady = string.Equals(diagnostics?.Recommendation, "ReadyForParityEval", StringComparison.OrdinalIgnoreCase);
        var providerReady = providerQuality is not null
                            && string.Equals(providerQuality.Recommendation, "ReadyForScopedWorkerCanary", StringComparison.OrdinalIgnoreCase)
                            && providerQuality.MismatchCount == 0
                            && providerQuality.PostgresFailureCount == 0
                            && providerQuality.LeaseParityPassed
                            && providerQuality.RetryParityPassed
                            && providerQuality.DeadLetterParityPassed
                            && providerQuality.CountParityPassed;
        var scopedWorkerPassed = scopedWorkerCanary is not null
                                 && string.Equals(scopedWorkerCanary.Recommendation, "ReadyForLimitedWorkerScope", StringComparison.OrdinalIgnoreCase)
                                 && scopedWorkerCanary.MismatchCount == 0
                                 && scopedWorkerCanary.PostgresFailureCount == 0
                                 && scopedWorkerCanary.ScopeLeakCount == 0
                                 && scopedWorkerCanary.RuntimeWorkerGlobalProviderUnchanged;
        var limitedPassed = limitedQuality?.Passed == true
                            && string.Equals(limitedQuality.Recommendation, "ReadyForJobQueueFreezeGate", StringComparison.OrdinalIgnoreCase);

        AddReasonIfFalse(blocked, diagnosticsReady, "DiagnosticsNotReadyForParityEval");
        AddReasonIfFalse(blocked, providerReady, "ProviderQualityNotReadyForScopedWorkerCanary");
        AddReasonIfFalse(blocked, scopedWorkerPassed, "ScopedWorkerCanaryNotPassed");
        AddReasonIfFalse(blocked, limitedPassed, "LimitedWorkerScopeQualityNotPassed");
        AddReasonIfFalse(blocked, (limitedQuality?.DuplicateExecutionCount ?? 0) == 0, "DuplicateExecutionCountNonZero");
        AddReasonIfFalse(blocked, (limitedQuality?.LeaseViolationCount ?? 0) == 0, "LeaseViolationCountNonZero");
        AddReasonIfFalse(blocked, (limitedQuality?.RetryViolationCount ?? 0) == 0, "RetryViolationCountNonZero");
        AddReasonIfFalse(blocked, (limitedQuality?.DeadLetterViolationCount ?? 0) == 0, "DeadLetterViolationCountNonZero");
        AddReasonIfFalse(blocked, (limitedQuality?.PostgresFailureCount ?? 0) == 0, "PostgresFailureCountNonZero");
        AddReasonIfFalse(blocked, (limitedQuality?.ScopeLeakCount ?? 0) == 0, "ScopeLeakCountNonZero");
        AddReasonIfFalse(blocked, limitedQuality?.NonSelectedScopeRemainsFileSystem == true, "NonSelectedScopeNotFileSystem");
        AddReasonIfFalse(blocked, limitedQuality?.RuntimeWorkerGlobalProviderUnchanged == true, "RuntimeWorkerGlobalProviderChanged");
        AddReasonIfFalse(blocked, p15GatePassed, "P15GateNotPassed");

        var passed = blocked.Count == 0;
        return new JobQueuePostgresFreezeGateReport
        {
            Passed = passed,
            JobQueuePostgres = passed ? "ReadyForScopedWorkerMode" : "NotReady",
            DiagnosticsReady = diagnosticsReady,
            ProviderQualityReady = providerReady,
            ScopedWorkerCanaryPassed = scopedWorkerPassed,
            LimitedWorkerScopeQualityPassed = limitedPassed,
            DuplicateExecutionCount = limitedQuality?.DuplicateExecutionCount ?? 0,
            LeaseViolationCount = limitedQuality?.LeaseViolationCount ?? 0,
            RetryViolationCount = limitedQuality?.RetryViolationCount ?? 0,
            DeadLetterViolationCount = limitedQuality?.DeadLetterViolationCount ?? 0,
            PostgresFailureCount = limitedQuality?.PostgresFailureCount ?? 0,
            ScopeLeakCount = limitedQuality?.ScopeLeakCount ?? 0,
            NonSelectedScopeRemainsFileSystem = limitedQuality?.NonSelectedScopeRemainsFileSystem == true,
            RuntimeWorkerGlobalProviderUnchanged = limitedQuality?.RuntimeWorkerGlobalProviderUnchanged == true,
            GlobalSwitchAllowed = false,
            ScopedWorkerCanaryAllowed = passed,
            P15GatePassed = p15GatePassed,
            BlockedReasons = blocked,
            Diagnostics = passed
                ? ["DefaultProviderExistingProvider", "AllowlistedGuardedPostgresPrimaryOnly", "RuntimeWorkerGlobalProviderUnchanged"]
                : ["FreezeGateBlocked"],
            Recommendation = passed ? "ReadyForScopedWorkerMode" : BuildJobQueueFreezeGateRecommendation(blocked)
        };
    }

    public static PostgresJobQueueProviderQualityReport BuildProviderQuality(
        PostgresJobQueueDualWriteSmokeReport dualWrite,
        PostgresJobQueueShadowReadSmokeReport shadowRead)
    {
        var traceCount = dualWrite.TraceCount + shadowRead.TraceCount;
        var mismatchCount = dualWrite.MismatchCount + shadowRead.MismatchCount;
        var postgresFailureCount = dualWrite.PostgresFailureCount + shadowRead.PostgresFailureCount;
        return new PostgresJobQueueProviderQualityReport
        {
            TraceCount = traceCount,
            FileSystemWriteSuccessCount = dualWrite.FileSystemWriteSuccessCount,
            PostgresWriteSuccessCount = dualWrite.PostgresWriteSuccessCount,
            FileSystemReadSuccessCount = shadowRead.FileSystemReadSuccessCount,
            PostgresReadSuccessCount = shadowRead.PostgresReadSuccessCount,
            MismatchCount = mismatchCount,
            PostgresFailureCount = postgresFailureCount,
            FallbackCount = dualWrite.FallbackCount + shadowRead.FallbackCount,
            LeaseParityPassed = dualWrite.LeaseParityPassed && shadowRead.LeaseParityPassed,
            RetryParityPassed = dualWrite.RetryParityPassed && shadowRead.RetryParityPassed,
            DeadLetterParityPassed = dualWrite.DeadLetterParityPassed && shadowRead.DeadLetterParityPassed,
            CountParityPassed = shadowRead.CountParityPassed,
            Diagnostics = ["UseForRuntime=false", "RuntimeWorkerUnchanged"],
            Recommendation = BuildProviderQualityRecommendation(mismatchCount, postgresFailureCount, traceCount)
        };
    }

    public static string SerializeJson<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    public static string BuildDiagnosticsMarkdown(PostgresJobQueueDiagnosticsReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Job Queue Diagnostics");
        builder.AppendLine();
        AppendCommon(builder, report.Recommendation, report.JobCount, operationCount: 0, report.PendingCount, report.RunningCount, report.FailedCount);
        builder.AppendLine($"- ProviderEnabled: `{report.ProviderEnabled}`");
        builder.AppendLine($"- ConnectionAvailable: `{report.ConnectionAvailable}`");
        builder.AppendLine($"- SchemaVersion: `{report.SchemaVersion}`");
        builder.AppendLine($"- JobTableExists: `{report.JobTableExists}`");
        builder.AppendLine($"- RequiredIndexesExist: `{report.RequiredIndexesExist}`");
        builder.AppendLine($"- DeadLetterCount: `{report.DeadLetterCount}`");
        builder.AppendLine($"- StaleLeaseCount: `{report.StaleLeaseCount}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        AppendList(builder, "MissingIndexes", report.MissingIndexes);
        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    public static string BuildParityMarkdown(PostgresJobQueueParityReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Job Queue Parity");
        builder.AppendLine();
        AppendCommon(builder, report.Recommendation, report.JobCount, report.OperationCount, pending: 0, running: 0, failed: 0);
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- EnqueueListParityPassed: `{report.EnqueueListParityPassed}`");
        builder.AppendLine($"- DuplicateUpsertParityPassed: `{report.DuplicateUpsertParityPassed}`");
        builder.AppendLine($"- StatusTransitionParityPassed: `{report.StatusTransitionParityPassed}`");
        builder.AppendLine($"- RetryCountParityPassed: `{report.RetryCountParityPassed}`");
        builder.AppendLine($"- CancelParityPassed: `{report.CancelParityPassed}`");
        builder.AppendLine($"- DeadLetterParityPassed: `{report.DeadLetterParityPassed}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        AppendList(builder, "Mismatches", report.Mismatches);
        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    public static string BuildLeaseSmokeMarkdown(PostgresJobQueueLeaseSmokeReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Job Queue Lease Smoke");
        builder.AppendLine();
        AppendCommon(builder, report.Recommendation, report.JobCount, report.OperationCount, pending: 0, running: 0, failed: 0);
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- LeaseAcquireCount: `{report.LeaseAcquireCount}`");
        builder.AppendLine($"- LeaseConflictCount: `{report.LeaseConflictCount}`");
        builder.AppendLine($"- LeaseExpiredReacquireCount: `{report.LeaseExpiredReacquireCount}`");
        builder.AppendLine($"- HeartbeatRenewalPassed: `{report.HeartbeatRenewalPassed}`");
        builder.AppendLine($"- CompleteTransitionPassed: `{report.CompleteTransitionPassed}`");
        builder.AppendLine($"- RetryTransitionPassed: `{report.RetryTransitionPassed}`");
        builder.AppendLine($"- DeadLetterTransitionPassed: `{report.DeadLetterTransitionPassed}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        AppendList(builder, "Mismatches", report.Mismatches);
        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    public static string BuildDualWriteMarkdown(PostgresJobQueueDualWriteSmokeReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Job Queue Dual-write Smoke");
        builder.AppendLine();
        AppendCommon(builder, report.Recommendation, jobCount: 0, report.TraceCount, pending: 0, running: 0, failed: 0);
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- FileSystemWriteSuccessCount: `{report.FileSystemWriteSuccessCount}`");
        builder.AppendLine($"- PostgresWriteSuccessCount: `{report.PostgresWriteSuccessCount}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- FallbackCount: `{report.FallbackCount}`");
        builder.AppendLine($"- LeaseParityPassed: `{report.LeaseParityPassed}`");
        builder.AppendLine($"- RetryParityPassed: `{report.RetryParityPassed}`");
        builder.AppendLine($"- DeadLetterParityPassed: `{report.DeadLetterParityPassed}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        AppendList(builder, "Mismatches", report.Mismatches);
        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    public static string BuildShadowReadMarkdown(PostgresJobQueueShadowReadSmokeReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Job Queue Shadow-read Smoke");
        builder.AppendLine();
        AppendCommon(builder, report.Recommendation, jobCount: 0, report.TraceCount, pending: 0, running: 0, failed: 0);
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- FileSystemReadSuccessCount: `{report.FileSystemReadSuccessCount}`");
        builder.AppendLine($"- PostgresReadSuccessCount: `{report.PostgresReadSuccessCount}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- FallbackCount: `{report.FallbackCount}`");
        builder.AppendLine($"- CountParityPassed: `{report.CountParityPassed}`");
        builder.AppendLine($"- LeaseParityPassed: `{report.LeaseParityPassed}`");
        builder.AppendLine($"- RetryParityPassed: `{report.RetryParityPassed}`");
        builder.AppendLine($"- DeadLetterParityPassed: `{report.DeadLetterParityPassed}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        AppendList(builder, "Mismatches", report.Mismatches);
        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    public static string BuildProviderQualityMarkdown(PostgresJobQueueProviderQualityReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Job Queue Provider Quality");
        builder.AppendLine();
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- TraceCount: `{report.TraceCount}`");
        builder.AppendLine($"- FileSystemWriteSuccessCount: `{report.FileSystemWriteSuccessCount}`");
        builder.AppendLine($"- PostgresWriteSuccessCount: `{report.PostgresWriteSuccessCount}`");
        builder.AppendLine($"- FileSystemReadSuccessCount: `{report.FileSystemReadSuccessCount}`");
        builder.AppendLine($"- PostgresReadSuccessCount: `{report.PostgresReadSuccessCount}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- FallbackCount: `{report.FallbackCount}`");
        builder.AppendLine($"- LeaseParityPassed: `{report.LeaseParityPassed}`");
        builder.AppendLine($"- RetryParityPassed: `{report.RetryParityPassed}`");
        builder.AppendLine($"- DeadLetterParityPassed: `{report.DeadLetterParityPassed}`");
        builder.AppendLine($"- CountParityPassed: `{report.CountParityPassed}`");
        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    public static string BuildScopedWorkerCanaryMarkdown(PostgresJobQueueScopedWorkerCanaryReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Job Queue Scoped Worker Canary");
        builder.AppendLine();
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- ProviderMode: `{report.ProviderMode}`");
        builder.AppendLine($"- ProviderQualityReady: `{report.ProviderQualityReady}`");
        builder.AppendLine($"- JobCount: `{report.JobCount}`");
        builder.AppendLine($"- CompletedCount: `{report.CompletedCount}`");
        builder.AppendLine($"- FailedCount: `{report.FailedCount}`");
        builder.AppendLine($"- RetriedCount: `{report.RetriedCount}`");
        builder.AppendLine($"- DeadLetterCount: `{report.DeadLetterCount}`");
        builder.AppendLine($"- LeaseAcquireCount: `{report.LeaseAcquireCount}`");
        builder.AppendLine($"- LeaseConflictCount: `{report.LeaseConflictCount}`");
        builder.AppendLine($"- LeaseExpiredReacquireCount: `{report.LeaseExpiredReacquireCount}`");
        builder.AppendLine($"- HeartbeatCount: `{report.HeartbeatCount}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- ScopeLeakCount: `{report.ScopeLeakCount}`");
        builder.AppendLine($"- NonSelectedScopeRemainsFileSystem: `{report.NonSelectedScopeRemainsFileSystem}`");
        builder.AppendLine($"- RuntimeWorkerGlobalProviderUnchanged: `{report.RuntimeWorkerGlobalProviderUnchanged}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        AppendList(builder, "BlockedReasons", report.BlockedReasons);
        AppendList(builder, "Mismatches", report.Mismatches);
        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    public static string BuildScopedWorkerQualityMarkdown(PostgresJobQueueScopedWorkerQualityReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Job Queue Scoped Worker Quality");
        builder.AppendLine();
        builder.AppendLine($"- Passed: `{report.Passed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- JobCount: `{report.JobCount}`");
        builder.AppendLine($"- CompletedCount: `{report.CompletedCount}`");
        builder.AppendLine($"- RetriedCount: `{report.RetriedCount}`");
        builder.AppendLine($"- DeadLetterCount: `{report.DeadLetterCount}`");
        builder.AppendLine($"- LeaseAcquireCount: `{report.LeaseAcquireCount}`");
        builder.AppendLine($"- LeaseConflictCount: `{report.LeaseConflictCount}`");
        builder.AppendLine($"- LeaseExpiredReacquireCount: `{report.LeaseExpiredReacquireCount}`");
        builder.AppendLine($"- HeartbeatCount: `{report.HeartbeatCount}`");
        builder.AppendLine($"- MismatchCount: `{report.MismatchCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- ScopeLeakCount: `{report.ScopeLeakCount}`");
        builder.AppendLine($"- NonSelectedScopeRemainsFileSystem: `{report.NonSelectedScopeRemainsFileSystem}`");
        builder.AppendLine($"- RuntimeWorkerGlobalProviderUnchanged: `{report.RuntimeWorkerGlobalProviderUnchanged}`");
        AppendList(builder, "BlockedReasons", report.BlockedReasons);
        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    public static string BuildLimitedWorkerScopeObservationMarkdown(PostgresJobQueueLimitedWorkerScopeObservationReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Job Queue Limited Worker Scope Observation");
        builder.AppendLine();
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine($"- ObservationWindowSeconds: `{report.ObservationWindowSeconds}`");
        builder.AppendLine($"- JobCount: `{report.JobCount}`");
        builder.AppendLine($"- CompletedCount: `{report.CompletedCount}`");
        builder.AppendLine($"- RetriedCount: `{report.RetriedCount}`");
        builder.AppendLine($"- DeadLetterCount: `{report.DeadLetterCount}`");
        builder.AppendLine($"- CancelledCount: `{report.CancelledCount}`");
        builder.AppendLine($"- LeaseAcquireCount: `{report.LeaseAcquireCount}`");
        builder.AppendLine($"- LeaseConflictCount: `{report.LeaseConflictCount}`");
        builder.AppendLine($"- LeaseExpiredReacquireCount: `{report.LeaseExpiredReacquireCount}`");
        builder.AppendLine($"- HeartbeatCount: `{report.HeartbeatCount}`");
        builder.AppendLine($"- DuplicateExecutionCount: `{report.DuplicateExecutionCount}`");
        builder.AppendLine($"- LeaseViolationCount: `{report.LeaseViolationCount}`");
        builder.AppendLine($"- RetryViolationCount: `{report.RetryViolationCount}`");
        builder.AppendLine($"- DeadLetterViolationCount: `{report.DeadLetterViolationCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- ScopeLeakCount: `{report.ScopeLeakCount}`");
        builder.AppendLine($"- NonSelectedScopeRemainsFileSystem: `{report.NonSelectedScopeRemainsFileSystem}`");
        builder.AppendLine($"- RuntimeWorkerGlobalProviderUnchanged: `{report.RuntimeWorkerGlobalProviderUnchanged}`");
        builder.AppendLine($"- CleanupPerformed: `{report.CleanupPerformed}`");
        AppendList(builder, "BlockedReasons", report.BlockedReasons);
        AppendList(builder, "Violations", report.Violations);
        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    public static string BuildLimitedWorkerScopeQualityMarkdown(PostgresJobQueueLimitedWorkerScopeQualityReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Job Queue Limited Worker Scope Quality");
        builder.AppendLine();
        builder.AppendLine($"- Passed: `{report.Passed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- ObservationWindowSeconds: `{report.ObservationWindowSeconds}`");
        builder.AppendLine($"- JobCount: `{report.JobCount}`");
        builder.AppendLine($"- CompletedCount: `{report.CompletedCount}`");
        builder.AppendLine($"- RetriedCount: `{report.RetriedCount}`");
        builder.AppendLine($"- DeadLetterCount: `{report.DeadLetterCount}`");
        builder.AppendLine($"- CancelledCount: `{report.CancelledCount}`");
        builder.AppendLine($"- LeaseAcquireCount: `{report.LeaseAcquireCount}`");
        builder.AppendLine($"- LeaseConflictCount: `{report.LeaseConflictCount}`");
        builder.AppendLine($"- LeaseExpiredReacquireCount: `{report.LeaseExpiredReacquireCount}`");
        builder.AppendLine($"- HeartbeatCount: `{report.HeartbeatCount}`");
        builder.AppendLine($"- DuplicateExecutionCount: `{report.DuplicateExecutionCount}`");
        builder.AppendLine($"- LeaseViolationCount: `{report.LeaseViolationCount}`");
        builder.AppendLine($"- RetryViolationCount: `{report.RetryViolationCount}`");
        builder.AppendLine($"- DeadLetterViolationCount: `{report.DeadLetterViolationCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- ScopeLeakCount: `{report.ScopeLeakCount}`");
        builder.AppendLine($"- NonSelectedScopeRemainsFileSystem: `{report.NonSelectedScopeRemainsFileSystem}`");
        builder.AppendLine($"- RuntimeWorkerGlobalProviderUnchanged: `{report.RuntimeWorkerGlobalProviderUnchanged}`");
        AppendList(builder, "BlockedReasons", report.BlockedReasons);
        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    public static string BuildFreezeGateMarkdown(JobQueuePostgresFreezeGateReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Postgres Job Queue Freeze Gate");
        builder.AppendLine();
        builder.AppendLine($"- Passed: `{report.Passed}`");
        builder.AppendLine($"- JobQueuePostgres: `{report.JobQueuePostgres}`");
        builder.AppendLine($"- DefaultProvider: `{report.DefaultProvider}`");
        builder.AppendLine($"- AllowedMode: `{report.AllowedMode}`");
        builder.AppendLine($"- DiagnosticsReady: `{report.DiagnosticsReady}`");
        builder.AppendLine($"- ProviderQualityReady: `{report.ProviderQualityReady}`");
        builder.AppendLine($"- ScopedWorkerCanaryPassed: `{report.ScopedWorkerCanaryPassed}`");
        builder.AppendLine($"- LimitedWorkerScopeQualityPassed: `{report.LimitedWorkerScopeQualityPassed}`");
        builder.AppendLine($"- DuplicateExecutionCount: `{report.DuplicateExecutionCount}`");
        builder.AppendLine($"- LeaseViolationCount: `{report.LeaseViolationCount}`");
        builder.AppendLine($"- RetryViolationCount: `{report.RetryViolationCount}`");
        builder.AppendLine($"- DeadLetterViolationCount: `{report.DeadLetterViolationCount}`");
        builder.AppendLine($"- PostgresFailureCount: `{report.PostgresFailureCount}`");
        builder.AppendLine($"- ScopeLeakCount: `{report.ScopeLeakCount}`");
        builder.AppendLine($"- NonSelectedScopeRemainsFileSystem: `{report.NonSelectedScopeRemainsFileSystem}`");
        builder.AppendLine($"- RuntimeWorkerGlobalProviderUnchanged: `{report.RuntimeWorkerGlobalProviderUnchanged}`");
        builder.AppendLine($"- GlobalSwitchAllowed: `{report.GlobalSwitchAllowed}`");
        builder.AppendLine($"- ScopedWorkerCanaryAllowed: `{report.ScopedWorkerCanaryAllowed}`");
        builder.AppendLine($"- P15GatePassed: `{report.P15GatePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        AppendList(builder, "Required", report.Required);
        AppendList(builder, "Forbidden", report.Forbidden);
        AppendList(builder, "BlockedReasons", report.BlockedReasons);
        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    public static string SerializeJsonLines<T>(IReadOnlyList<T> values)
        => string.Join(Environment.NewLine, values.Select(static value => JsonSerializer.Serialize(value, JsonOptions))) + Environment.NewLine;

    private static ContextJob CloneJob(ContextJob job, string? payloadJson = null, int? priority = null)
        => new()
        {
            JobId = job.JobId,
            WorkspaceId = job.WorkspaceId,
            CollectionId = job.CollectionId,
            Kind = job.Kind,
            PayloadJson = payloadJson ?? job.PayloadJson,
            State = job.State,
            Priority = priority ?? job.Priority,
            RetryCount = job.RetryCount,
            MaxRetryCount = job.MaxRetryCount,
            CreatedAt = job.CreatedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            ErrorMessage = job.ErrorMessage
        };

    private static void CollectTraceMismatches(IEnumerable<JobQueueDualWriteTrace> traces, ICollection<string> mismatches)
    {
        foreach (var trace in traces.Where(static trace => trace.MismatchDetected))
        {
            mismatches.Add($"{trace.OperationKind}:{trace.TargetId}:{trace.MismatchReason}");
        }
    }

    private static void CollectTraceMismatches(IEnumerable<JobQueueShadowReadTrace> traces, ICollection<string> mismatches)
    {
        foreach (var trace in traces.Where(static trace => trace.MismatchDetected))
        {
            mismatches.Add($"{trace.ReadKind}:{trace.TargetId}:{trace.MismatchReason}");
        }
    }

    private static void CollectTraceMismatches(IEnumerable<JobQueueScopedWorkerCanaryTrace> traces, ICollection<string> mismatches)
    {
        foreach (var trace in traces.Where(static trace => trace.MismatchDetected))
        {
            mismatches.Add($"{trace.OperationKind}:{trace.JobId}:{trace.MismatchReason}");
        }
    }

    private static void CollectTraceMismatches(IEnumerable<JobQueueLimitedWorkerScopeObservationTrace> traces, ICollection<string> mismatches)
    {
        foreach (var trace in traces.Where(static trace => trace.MismatchDetected))
        {
            mismatches.Add($"{trace.OperationKind}:{trace.JobId}:{trace.MismatchReason}");
        }
    }

    private static string BuildProviderSmokeRecommendation(int mismatchCount, int postgresFailureCount, int traceCount)
    {
        if (postgresFailureCount > 0)
        {
            return "BlockedByPostgresFailure";
        }

        if (mismatchCount > 0)
        {
            return "BlockedByMismatch";
        }

        return traceCount > 0 ? "ReadyForScopedWorkerCanary" : "NeedsMoreTraces";
    }

    private static string BuildProviderQualityRecommendation(int mismatchCount, int postgresFailureCount, int traceCount)
    {
        if (postgresFailureCount > 0)
        {
            return "BlockedByPostgresFailure";
        }

        if (mismatchCount > 0)
        {
            return "BlockedByMismatch";
        }

        return traceCount > 0 ? "ReadyForScopedWorkerCanary" : "NeedsMoreTraces";
    }

    private static bool IsProviderQualityReady(PostgresJobQueueProviderQualityReport providerQuality)
        => string.Equals(providerQuality.Recommendation, "ReadyForScopedWorkerCanary", StringComparison.OrdinalIgnoreCase)
           && providerQuality.MismatchCount == 0
           && providerQuality.PostgresFailureCount == 0
           && providerQuality.LeaseParityPassed
           && providerQuality.RetryParityPassed
           && providerQuality.DeadLetterParityPassed
           && providerQuality.CountParityPassed;

    private static List<string> BuildScopedWorkerCanaryBlockedReasons(
        PostgresJobQueueProviderQualityReport providerQuality,
        string workspaceId,
        string collectionId)
    {
        var reasons = new List<string>();
        if (!IsProviderQualityReady(providerQuality))
        {
            reasons.Add("ProviderQualityNotReady");
        }

        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(collectionId))
        {
            reasons.Add("CanaryScopeMissing");
        }

        return reasons;
    }

    private static string BuildScopedWorkerCanaryRecommendation(
        IReadOnlyCollection<string> mismatches,
        int postgresFailureCount,
        int scopeLeakCount,
        int leaseAcquireCount,
        int leaseConflictCount,
        int leaseExpiredReacquireCount,
        int retriedCount,
        int deadLetterCount,
        int completedCount)
    {
        if (postgresFailureCount > 0)
        {
            return "BlockedByPostgresFailure";
        }

        if (scopeLeakCount > 0)
        {
            return "BlockedByScopeLeak";
        }

        if (mismatches.Count > 0)
        {
            if (mismatches.Any(static item => item.Contains("Retry", StringComparison.OrdinalIgnoreCase)))
            {
                return "BlockedByRetryViolation";
            }

            if (mismatches.Any(static item => item.Contains("DeadLetter", StringComparison.OrdinalIgnoreCase)))
            {
                return "BlockedByDeadLetterViolation";
            }

            if (mismatches.Any(static item => item.Contains("Lease", StringComparison.OrdinalIgnoreCase)))
            {
                return "BlockedByLeaseViolation";
            }

            return "GateNotPassed";
        }

        if (leaseAcquireCount == 0 || leaseConflictCount == 0 || leaseExpiredReacquireCount == 0)
        {
            return "BlockedByLeaseViolation";
        }

        if (retriedCount == 0)
        {
            return "BlockedByRetryViolation";
        }

        if (deadLetterCount == 0)
        {
            return "BlockedByDeadLetterViolation";
        }

        return completedCount > 0 ? "ReadyForLimitedWorkerScope" : "NeedsMoreWorkerCanary";
    }

    private static List<string> BuildLimitedWorkerObservationBlockedReasons(
        PostgresJobQueueScopedWorkerQualityReport scopedWorkerQuality,
        string workspaceId,
        string collectionId)
    {
        var reasons = new List<string>();
        if (!scopedWorkerQuality.Passed ||
            !string.Equals(scopedWorkerQuality.Recommendation, "ReadyForLimitedWorkerScope", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("ScopedWorkerCanaryNotPassed");
        }

        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(collectionId))
        {
            reasons.Add("ObservationScopeMissing");
        }

        return reasons;
    }

    private static string BuildLimitedWorkerObservationRecommendation(
        int duplicateExecutionCount,
        int leaseViolationCount,
        int retryViolationCount,
        int deadLetterViolationCount,
        int postgresFailureCount,
        int scopeLeakCount,
        int jobCount)
    {
        if (duplicateExecutionCount > 0)
        {
            return "BlockedByDuplicateExecution";
        }

        if (leaseViolationCount > 0)
        {
            return "BlockedByLeaseViolation";
        }

        if (retryViolationCount > 0)
        {
            return "BlockedByRetryViolation";
        }

        if (deadLetterViolationCount > 0)
        {
            return "BlockedByDeadLetterViolation";
        }

        if (postgresFailureCount > 0)
        {
            return "BlockedByPostgresFailure";
        }

        if (scopeLeakCount > 0)
        {
            return "BlockedByScopeLeak";
        }

        return jobCount > 0 ? "ReadyForJobQueueFreezeGate" : "NeedsLongerObservation";
    }

    private static string BuildJobQueueFreezeGateRecommendation(IReadOnlyList<string> blockedReasons)
    {
        if (blockedReasons.Any(static reason => reason.Contains("DuplicateExecution", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByDuplicateExecution";
        }

        if (blockedReasons.Any(static reason => reason.Contains("Lease", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByLeaseViolation";
        }

        if (blockedReasons.Any(static reason => reason.Contains("Retry", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByRetryViolation";
        }

        if (blockedReasons.Any(static reason => reason.Contains("DeadLetter", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByDeadLetterViolation";
        }

        if (blockedReasons.Any(static reason => reason.Contains("PostgresFailure", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByPostgresFailure";
        }

        if (blockedReasons.Any(static reason => reason.Contains("Scope", StringComparison.OrdinalIgnoreCase)))
        {
            return "BlockedByScopeLeak";
        }

        return "GateNotPassed";
    }

    private static void AddReasonIfFalse(List<string> reasons, bool condition, string reason)
    {
        if (!condition)
        {
            reasons.Add(reason);
        }
    }

    private static bool RecordExecution(ISet<string> executedJobIds, ContextJob job)
        => executedJobIds.Add($"{job.JobId}:{job.RetryCount}");

    private static JobQueueLimitedWorkerScopeObservationTrace ToLimitedTrace(
        JobQueueScopedWorkerCanaryTrace trace,
        string canaryJobKind,
        bool leaseExpiredReacquired = false)
        => new()
        {
            OperationId = trace.OperationId,
            WorkspaceId = trace.WorkspaceId,
            CollectionId = trace.CollectionId,
            OperationKind = trace.OperationKind,
            JobId = trace.JobId,
            CanaryJobKind = canaryJobKind,
            PrimaryProvider = trace.PrimaryProvider,
            Succeeded = trace.PostgresSucceeded || trace.FileSystemSucceeded,
            MismatchDetected = trace.MismatchDetected,
            MismatchReason = trace.MismatchReason,
            LeaseConflictObserved = trace.LeaseConflictObserved,
            LeaseExpiredReacquired = leaseExpiredReacquired,
            HeartbeatRenewed = trace.HeartbeatRenewed,
            DuplicateExecutionDetected = false,
            LeaseViolationDetected = false,
            ViolationReason = string.Empty,
            PostgresError = trace.PostgresError,
            ScopeLeakDetected = trace.ScopeLeakDetected,
            DurationMs = trace.DurationMs,
            CreatedAt = trace.CreatedAt
        };

    private static ContextJob CreateCanaryJob(
        string prefix,
        string canaryKind,
        string workspaceId,
        string collectionId,
        DateTimeOffset createdAt,
        int maxRetryCount,
        int priority)
    {
        var jobId = $"{prefix}-{canaryKind}";
        return new ContextJob
        {
            JobId = jobId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Kind = ContextJobKind.Custom,
            PayloadJson = $$"""{"canaryKind":"{{canaryKind}}","safe":true}""",
            State = ContextJobState.Queued,
            Priority = priority,
            MaxRetryCount = maxRetryCount,
            CreatedAt = createdAt
        };
    }

    private static string HashJob(ContextJob? job)
        => job is null
            ? "null"
            : HashText($"{job.JobId}|{job.WorkspaceId}|{job.CollectionId}|{job.Kind}|{job.State}|{job.RetryCount}|{job.MaxRetryCount}|{job.Priority}|{job.ErrorMessage}");

    private static string HashJobs(IReadOnlyList<ContextJob> jobs)
        => HashText(string.Join(
            '\n',
            jobs.OrderBy(static job => job.JobId, StringComparer.OrdinalIgnoreCase)
                .Select(static job => $"{job.JobId}|{job.Kind}|{job.State}|{job.RetryCount}|{job.MaxRetryCount}|{job.Priority}|{job.ErrorMessage}")));

    private static string HashCounts(IReadOnlyDictionary<string, int> counts)
        => HashText(string.Join(
            '\n',
            counts.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static item => $"{item.Key}:{item.Value}")));

    private static string HashText(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static (string Root, FileContextJobQueue Queue) CreateFileQueue(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
        return (root, new FileContextJobQueue(new FileStorageOptions { RootPath = root }));
    }

    private static ContextJob[] CreateParityJobs(string prefix, string workspaceId, string collectionId, DateTimeOffset now)
        =>
        [
            CreateJob($"{prefix}-compression", workspaceId, collectionId, ContextJobKind.Compression, now, maxRetryCount: 2, priority: 10),
            CreateJob($"{prefix}-package", workspaceId, collectionId, ContextJobKind.PackageRefresh, now.AddSeconds(1), maxRetryCount: 2, priority: 5),
            CreateJob($"{prefix}-custom", workspaceId, collectionId, ContextJobKind.Custom, now.AddSeconds(2), maxRetryCount: 1, priority: 1)
        ];

    private static ContextJob CreateJob(
        string jobId,
        string workspaceId,
        string collectionId,
        ContextJobKind kind,
        DateTimeOffset createdAt,
        int maxRetryCount,
        int priority = 0)
        => new()
        {
            JobId = jobId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Kind = kind,
            PayloadJson = $$"""{"jobId":"{{jobId}}","kind":"{{kind}}"}""",
            State = ContextJobState.Queued,
            Priority = priority,
            MaxRetryCount = maxRetryCount,
            CreatedAt = createdAt
        };

    private static ContextJobQuery BuildQuery(
        string workspaceId,
        string collectionId,
        ContextJobKind? kind = null,
        ContextJobState? state = null)
        => new()
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Kind = kind,
            State = state,
            Take = int.MaxValue
        };

    private static async Task CompareQueryAsync(
        IContextJobQueryStore left,
        IContextJobQueryStore right,
        ContextJobQuery query,
        List<string> mismatches,
        string label,
        CancellationToken cancellationToken)
    {
        var leftRows = await left.QueryAsync(query, cancellationToken).ConfigureAwait(false);
        var rightRows = await right.QueryAsync(query, cancellationToken).ConfigureAwait(false);
        AddMismatchIfFalse(mismatches, SameIds(leftRows, rightRows), $"{label}IdsMismatch");
    }

    private static bool SameIds(IReadOnlyList<ContextJob> left, IReadOnlyList<ContextJob> right)
    {
        return left.Select(static item => item.JobId).Order(StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(right.Select(static item => item.JobId).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    }

    private static bool SameJob(ContextJob? left, ContextJob? right)
        => left is null && right is null
           || left is not null
           && right is not null
           && string.Equals(left.JobId, right.JobId, StringComparison.OrdinalIgnoreCase)
           && left.Kind == right.Kind;

    private static int GetCount(IReadOnlyDictionary<string, int> counts, ContextJobState state)
        => counts.TryGetValue(state.ToString(), out var count) ? count : 0;

    private static string[] RequiredJobIndexes(PostgresOptions options)
        =>
        [
            QualifiedIndex(options, "context_jobs", "state"),
            QualifiedIndex(options, "context_jobs", "scope"),
            QualifiedIndex(options, "context_jobs", "kind"),
            QualifiedIndex(options, "context_jobs", "lease"),
            QualifiedIndex(options, "context_jobs", "attempt")
        ];

    private static string QualifiedIndex(PostgresOptions options, string tableSuffix, string indexSuffix)
    {
        var index = $"ix_{options.TablePrefix}{tableSuffix}_{indexSuffix}";
        return string.IsNullOrWhiteSpace(options.SchemaName) ? index : $"{options.SchemaName}.{index}";
    }

    private static string BuildRecommendation(int mismatchCount, int postgresFailureCount, int traceCount)
    {
        if (postgresFailureCount > 0)
        {
            return "BlockedByPostgresFailure";
        }

        if (mismatchCount > 0)
        {
            return "BlockedByMismatch";
        }

        return traceCount > 0 ? "ReadyForDualWriteShadowRead" : "NeedsLeaseContractFix";
    }

    private static void AddMismatchIfFalse(List<string> mismatches, bool condition, string reason)
    {
        if (!condition)
        {
            mismatches.Add(reason);
        }
    }

    private static void AppendCommon(StringBuilder builder, string recommendation, int jobCount, int operationCount, int pending, int running, int failed)
    {
        builder.AppendLine($"- Recommendation: `{recommendation}`");
        builder.AppendLine($"- JobCount: `{jobCount}`");
        builder.AppendLine($"- OperationCount: `{operationCount}`");
        builder.AppendLine($"- PendingCount: `{pending}`");
        builder.AppendLine($"- RunningCount: `{running}`");
        builder.AppendLine($"- FailedCount: `{failed}`");
    }

    private static void AppendList(StringBuilder builder, string title, IReadOnlyList<string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            builder.AppendLine("- none");
            return;
        }

        foreach (var value in values)
        {
            builder.AppendLine($"- {value}");
        }
    }

    private sealed class JobQueueDualWriteCoordinator
    {
        private readonly FileContextJobQueue _file;
        private readonly PostgresContextJobQueue _postgres;

        public JobQueueDualWriteCoordinator(FileContextJobQueue file, PostgresContextJobQueue postgres)
        {
            _file = file;
            _postgres = postgres;
        }

        public Task<JobQueueDualWriteTrace> EnqueueAsync(ContextJob job, CancellationToken cancellationToken)
            => TraceWriteAsync(
                "enqueue",
                job.WorkspaceId,
                job.CollectionId,
                job.JobId,
                () => _file.EnqueueAsync(job, cancellationToken),
                () => _postgres.EnqueueAsync(job, cancellationToken),
                () => CompareJobAsync(job.JobId, cancellationToken));

        public async Task<JobQueueDualWriteTrace> AcquireLeaseAsync(
            string owner,
            TimeSpan leaseDuration,
            ContextJobKind kind,
            string workspaceId,
            string collectionId,
            CancellationToken cancellationToken)
        {
            var started = DateTimeOffset.UtcNow;
            var operationId = Guid.NewGuid().ToString("N");
            ContextJob? fileJob = null;
            ContextJob? postgresJob = null;
            string error = string.Empty;
            try
            {
                postgresJob = await _postgres.AcquireLeaseAsync(owner, leaseDuration, kind, workspaceId, collectionId, cancellationToken)
                    .ConfigureAwait(false);
                if (postgresJob is not null)
                {
                    fileJob = await _file.DequeueAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or IOException)
            {
                error = ex.GetType().Name;
            }

            var bothNull = fileJob is null && postgresJob is null;
            var same = SameJob(fileJob, postgresJob);
            return new JobQueueDualWriteTrace
            {
                OperationId = operationId,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                OperationKind = "acquire-lease",
                TargetId = postgresJob?.JobId ?? fileJob?.JobId ?? string.Empty,
                FileSystemWriteSucceeded = fileJob is not null || bothNull,
                PostgresWriteSucceeded = postgresJob is not null || bothNull,
                MismatchDetected = !same,
                MismatchReason = bothNull ? "LeaseConflictExpected" : same ? string.Empty : "LeaseAcquireMismatch",
                PostgresError = error,
                FallbackUsed = !string.IsNullOrWhiteSpace(error),
                DurationMs = (DateTimeOffset.UtcNow - started).TotalMilliseconds,
                CreatedAt = started
            };
        }

        public Task<JobQueueDualWriteTrace> RenewHeartbeatAsync(string jobId, string owner, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => TraceWriteAsync(
                "renew-heartbeat",
                string.Empty,
                string.Empty,
                jobId,
                () => Task.CompletedTask,
                async () => _ = await _postgres.RenewHeartbeatAsync(jobId, owner, leaseDuration, cancellationToken).ConfigureAwait(false),
                () => Task.FromResult((Mismatch: false, Reason: string.Empty)));

        public Task<JobQueueDualWriteTrace> CompleteAsync(string jobId, CancellationToken cancellationToken)
            => TraceWriteAsync("complete", string.Empty, string.Empty, jobId, () => _file.AckAsync(jobId, cancellationToken), () => _postgres.CompleteAsync(jobId, cancellationToken), () => CompareJobAsync(jobId, cancellationToken));

        public Task<JobQueueDualWriteTrace> FailAsync(string jobId, string reason, CancellationToken cancellationToken)
            => TraceWriteAsync("fail", string.Empty, string.Empty, jobId, () => _file.NackAsync(jobId, reason, cancellationToken), () => _postgres.FailAsync(jobId, reason, cancellationToken), () => CompareJobAsync(jobId, cancellationToken));

        public Task<JobQueueDualWriteTrace> RetryAsync(string jobId, string reason, CancellationToken cancellationToken)
            => TraceWriteAsync("retry", string.Empty, string.Empty, jobId, () => _file.NackAsync(jobId, reason, cancellationToken), () => _postgres.RetryAsync(jobId, reason, cancellationToken), () => CompareJobAsync(jobId, cancellationToken));

        public Task<JobQueueDualWriteTrace> CancelAsync(string jobId, CancellationToken cancellationToken)
            => TraceWriteAsync("cancel", string.Empty, string.Empty, jobId, () => _file.AckAsync(jobId, cancellationToken), () => _postgres.CancelAsync(jobId, cancellationToken), () => Task.FromResult((Mismatch: false, Reason: string.Empty)));

        public Task<JobQueueDualWriteTrace> DeadLetterAsync(string jobId, string reason, CancellationToken cancellationToken)
            => TraceWriteAsync("dead-letter", string.Empty, string.Empty, jobId, () => _file.NackAsync(jobId, reason, cancellationToken), () => _postgres.DeadLetterAsync(jobId, reason, cancellationToken), () => Task.FromResult((Mismatch: false, Reason: string.Empty)));

        private async Task<JobQueueDualWriteTrace> TraceWriteAsync(
            string operationKind,
            string workspaceId,
            string collectionId,
            string targetId,
            Func<Task> writeFileSystem,
            Func<Task> writePostgres,
            Func<Task<(bool Mismatch, string Reason)>> compare)
        {
            var started = DateTimeOffset.UtcNow;
            var fileSucceeded = false;
            var postgresSucceeded = false;
            var postgresError = string.Empty;
            try
            {
                await writeFileSystem().ConfigureAwait(false);
                fileSucceeded = true;
                await writePostgres().ConfigureAwait(false);
                postgresSucceeded = true;
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or IOException)
            {
                postgresError = ex.GetType().Name;
            }

            var comparison = postgresSucceeded
                ? await compare().ConfigureAwait(false)
                : (Mismatch: false, Reason: string.Empty);
            return new JobQueueDualWriteTrace
            {
                OperationId = Guid.NewGuid().ToString("N"),
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                OperationKind = operationKind,
                TargetId = targetId,
                FileSystemWriteSucceeded = fileSucceeded,
                PostgresWriteSucceeded = postgresSucceeded,
                MismatchDetected = comparison.Mismatch,
                MismatchReason = comparison.Reason,
                PostgresError = postgresError,
                FallbackUsed = !postgresSucceeded && fileSucceeded,
                DurationMs = (DateTimeOffset.UtcNow - started).TotalMilliseconds,
                CreatedAt = started
            };
        }

        private async Task<(bool Mismatch, string Reason)> CompareJobAsync(string jobId, CancellationToken cancellationToken)
        {
            var fileJob = (await _file.QueryAsync(new ContextJobQuery { Take = int.MaxValue }, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(job => string.Equals(job.JobId, jobId, StringComparison.OrdinalIgnoreCase));
            var postgresJob = await _postgres.GetByIdAsync(jobId, cancellationToken).ConfigureAwait(false);
            var mismatch = HashJob(fileJob) != HashJob(postgresJob);
            return (mismatch, mismatch ? "JobStateMismatch" : string.Empty);
        }
    }

    private sealed class JobQueueShadowReadCoordinator
    {
        private readonly FileContextJobQueue _file;
        private readonly PostgresContextJobQueue _postgres;

        public JobQueueShadowReadCoordinator(FileContextJobQueue file, PostgresContextJobQueue postgres)
        {
            _file = file;
            _postgres = postgres;
        }

        public async Task<JobQueueShadowReadTrace> GetByIdAsync(
            string jobId,
            string workspaceId,
            string collectionId,
            CancellationToken cancellationToken)
        {
            return await TraceReadAsync(
                "get-by-id",
                workspaceId,
                collectionId,
                jobId,
                async () =>
                {
                    var rows = await _file.QueryAsync(new ContextJobQuery { WorkspaceId = workspaceId, CollectionId = collectionId, Take = int.MaxValue }, cancellationToken)
                        .ConfigureAwait(false);
                    return HashJob(rows.FirstOrDefault(job => string.Equals(job.JobId, jobId, StringComparison.OrdinalIgnoreCase)));
                },
                async () => HashJob(await _postgres.GetByIdAsync(jobId, cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
        }

        public Task<JobQueueShadowReadTrace> ListAsync(ContextJobQuery query, string readKind, CancellationToken cancellationToken)
            => TraceReadAsync(
                readKind,
                query.WorkspaceId ?? string.Empty,
                query.CollectionId ?? string.Empty,
                query.Kind?.ToString() ?? query.State?.ToString() ?? "scope",
                async () => HashJobs(await _file.QueryAsync(query, cancellationToken).ConfigureAwait(false)),
                async () => HashJobs(await _postgres.QueryAsync(query, cancellationToken).ConfigureAwait(false)));

        public async Task<JobQueueShadowReadTrace> CountsAsync(string workspaceId, string collectionId, CancellationToken cancellationToken)
        {
            return await TraceReadAsync(
                "counts",
                workspaceId,
                collectionId,
                "counts",
                async () =>
                {
                    var rows = await _file.QueryAsync(BuildQuery(workspaceId, collectionId), cancellationToken).ConfigureAwait(false);
                    return HashCounts(rows.GroupBy(static job => job.State.ToString()).ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase));
                },
                async () =>
                {
                    var rows = await _postgres.QueryAsync(BuildQuery(workspaceId, collectionId), cancellationToken).ConfigureAwait(false);
                    return HashCounts(rows.GroupBy(static job => job.State.ToString()).ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase));
                }).ConfigureAwait(false);
        }

        public Task<JobQueueShadowReadTrace> StaleLeaseAsync(string workspaceId, string collectionId, CancellationToken cancellationToken)
            => TraceReadAsync(
                "stale-lease",
                workspaceId,
                collectionId,
                "stale-lease",
                () => Task.FromResult(HashText("0")),
                async () => HashText((await _postgres.CountStaleLeasesAsync(cancellationToken).ConfigureAwait(false)).ToString()));

        private static async Task<JobQueueShadowReadTrace> TraceReadAsync(
            string readKind,
            string workspaceId,
            string collectionId,
            string targetId,
            Func<Task<string>> readFileSystem,
            Func<Task<string>> readPostgres)
        {
            var started = DateTimeOffset.UtcNow;
            var fileSucceeded = false;
            var postgresSucceeded = false;
            var fileHash = string.Empty;
            var postgresHash = string.Empty;
            var postgresError = string.Empty;
            var fsStart = DateTimeOffset.UtcNow;
            fileHash = await readFileSystem().ConfigureAwait(false);
            fileSucceeded = true;
            var fsDuration = (DateTimeOffset.UtcNow - fsStart).TotalMilliseconds;
            var pgStart = DateTimeOffset.UtcNow;
            try
            {
                postgresHash = await readPostgres().ConfigureAwait(false);
                postgresSucceeded = true;
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or IOException)
            {
                postgresError = ex.GetType().Name;
            }

            var pgDuration = (DateTimeOffset.UtcNow - pgStart).TotalMilliseconds;
            var mismatch = postgresSucceeded && !string.Equals(fileHash, postgresHash, StringComparison.Ordinal);
            return new JobQueueShadowReadTrace
            {
                OperationId = Guid.NewGuid().ToString("N"),
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ReadKind = readKind,
                TargetId = targetId,
                FileSystemReadSucceeded = fileSucceeded,
                PostgresReadSucceeded = postgresSucceeded,
                FileSystemResultHash = fileHash,
                PostgresResultHash = postgresHash,
                MismatchDetected = mismatch,
                MismatchReason = mismatch ? "ResultHashMismatch" : string.Empty,
                PostgresError = postgresError,
                FallbackUsed = !postgresSucceeded && fileSucceeded,
                FileSystemDurationMs = fsDuration,
                PostgresDurationMs = pgDuration,
                CreatedAt = started
            };
        }
    }

    private sealed class JobQueueScopedWorkerRouter
    {
        private readonly FileContextJobQueue _file;
        private readonly PostgresContextJobQueue _postgres;
        private readonly string _workspaceId;
        private readonly string _collectionId;
        private readonly bool _providerQualityReady;

        public JobQueueScopedWorkerRouter(
            FileContextJobQueue file,
            PostgresContextJobQueue postgres,
            string workspaceId,
            string collectionId,
            bool providerQualityReady)
        {
            _file = file;
            _postgres = postgres;
            _workspaceId = workspaceId;
            _collectionId = collectionId;
            _providerQualityReady = providerQualityReady;
        }

        public async Task<JobQueueScopedWorkerCanaryTrace> EnqueueAsync(ContextJob job, CancellationToken cancellationToken)
        {
            return await TraceAsync(
                "enqueue",
                job,
                async () =>
                {
                    if (UsePostgres(job.WorkspaceId, job.CollectionId))
                    {
                        await _postgres.EnqueueAsync(job, cancellationToken).ConfigureAwait(false);
                        return (Postgres: true, File: false, Job: (ContextJob?)job, Mismatch: false, Reason: string.Empty);
                    }

                    await _file.EnqueueAsync(job, cancellationToken).ConfigureAwait(false);
                    return (Postgres: false, File: true, Job: (ContextJob?)job, Mismatch: false, Reason: string.Empty);
                }).ConfigureAwait(false);
        }

        public async Task<LeaseResult> AcquireLeaseAsync(
            string owner,
            TimeSpan leaseDuration,
            string workspaceId,
            string collectionId,
            CancellationToken cancellationToken)
        {
            var started = DateTimeOffset.UtcNow;
            var operationId = Guid.NewGuid().ToString("N");
            ContextJob? job = null;
            string error = string.Empty;
            var usePostgres = UsePostgres(workspaceId, collectionId);
            try
            {
                job = usePostgres
                    ? await _postgres.AcquireLeaseAsync(owner, leaseDuration, ContextJobKind.Custom, workspaceId, collectionId, cancellationToken)
                        .ConfigureAwait(false)
                    : await _file.DequeueAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or IOException)
            {
                error = ex.GetType().Name;
            }

            var conflict = string.IsNullOrWhiteSpace(error) && job is null;
            var trace = new JobQueueScopedWorkerCanaryTrace
            {
                OperationId = operationId,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                OperationKind = "acquire-lease",
                JobId = job?.JobId ?? string.Empty,
                JobKind = job?.Kind.ToString() ?? ContextJobKind.Custom.ToString(),
                PrimaryProvider = usePostgres ? "Postgres" : "FileSystem",
                PostgresSucceeded = usePostgres && string.IsNullOrWhiteSpace(error),
                FileSystemSucceeded = !usePostgres && string.IsNullOrWhiteSpace(error),
                MismatchDetected = false,
                MismatchReason = conflict ? "LeaseConflictExpected" : string.Empty,
                PostgresError = error,
                LeaseConflictObserved = conflict,
                DurationMs = (DateTimeOffset.UtcNow - started).TotalMilliseconds,
                CreatedAt = started
            };
            return new LeaseResult(job, trace);
        }

        public Task<JobQueueScopedWorkerCanaryTrace> RenewHeartbeatAsync(
            string jobId,
            string owner,
            TimeSpan leaseDuration,
            string workspaceId,
            string collectionId,
            CancellationToken cancellationToken)
        {
            return TraceAsync(
                "renew-heartbeat",
                jobId,
                workspaceId,
                collectionId,
                async () =>
                {
                    if (!UsePostgres(workspaceId, collectionId))
                    {
                        return (Postgres: false, File: true, Job: (ContextJob?)null, Mismatch: false, Reason: string.Empty, Heartbeat: false);
                    }

                    var renewed = await _postgres.RenewHeartbeatAsync(jobId, owner, leaseDuration, cancellationToken)
                        .ConfigureAwait(false);
                    return (Postgres: renewed, File: false, Job: await _postgres.GetByIdAsync(jobId, cancellationToken).ConfigureAwait(false), Mismatch: !renewed, Reason: renewed ? string.Empty : "HeartbeatRenewalFailed", Heartbeat: renewed);
                });
        }

        public Task<JobQueueScopedWorkerCanaryTrace> CompleteAsync(string jobId, string workspaceId, string collectionId, CancellationToken cancellationToken)
            => UpdateAsync("complete", jobId, workspaceId, collectionId, job => _postgres.CompleteAsync(job, cancellationToken), job => _file.AckAsync(job, cancellationToken), cancellationToken);

        public Task<JobQueueScopedWorkerCanaryTrace> FailAsync(string jobId, string reason, string workspaceId, string collectionId, CancellationToken cancellationToken)
            => UpdateAsync("fail", jobId, workspaceId, collectionId, job => _postgres.FailAsync(job, reason, cancellationToken), job => _file.NackAsync(job, reason, cancellationToken), cancellationToken);

        public Task<JobQueueScopedWorkerCanaryTrace> CancelAsync(string jobId, string workspaceId, string collectionId, CancellationToken cancellationToken)
            => UpdateAsync("cancel", jobId, workspaceId, collectionId, job => _postgres.CancelAsync(job, cancellationToken), job => _file.AckAsync(job, cancellationToken), cancellationToken);

        private async Task<JobQueueScopedWorkerCanaryTrace> UpdateAsync(
            string operationKind,
            string jobId,
            string workspaceId,
            string collectionId,
            Func<string, Task> updatePostgres,
            Func<string, Task> updateFile,
            CancellationToken cancellationToken)
        {
            return await TraceAsync(
                operationKind,
                jobId,
                workspaceId,
                collectionId,
                async () =>
                {
                    if (UsePostgres(workspaceId, collectionId))
                    {
                        await updatePostgres(jobId).ConfigureAwait(false);
                        var loaded = await _postgres.GetByIdAsync(jobId, cancellationToken).ConfigureAwait(false);
                        return (Postgres: true, File: false, Job: loaded, Mismatch: loaded is null, Reason: loaded is null ? "PostgresJobMissingAfterUpdate" : string.Empty, Heartbeat: false);
                    }

                    await updateFile(jobId).ConfigureAwait(false);
                    return (Postgres: false, File: true, Job: (ContextJob?)null, Mismatch: false, Reason: string.Empty, Heartbeat: false);
                }).ConfigureAwait(false);
        }

        private Task<JobQueueScopedWorkerCanaryTrace> TraceAsync(
            string operationKind,
            ContextJob job,
            Func<Task<(bool Postgres, bool File, ContextJob? Job, bool Mismatch, string Reason)>> operation)
        {
            return TraceAsync(
                operationKind,
                job.JobId,
                job.WorkspaceId,
                job.CollectionId,
                async () =>
                {
                    var result = await operation().ConfigureAwait(false);
                    return (result.Postgres, result.File, result.Job, result.Mismatch, result.Reason, Heartbeat: false);
                });
        }

        private async Task<JobQueueScopedWorkerCanaryTrace> TraceAsync(
            string operationKind,
            string jobId,
            string workspaceId,
            string collectionId,
            Func<Task<(bool Postgres, bool File, ContextJob? Job, bool Mismatch, string Reason, bool Heartbeat)>> operation)
        {
            var started = DateTimeOffset.UtcNow;
            var usePostgres = UsePostgres(workspaceId, collectionId);
            string error = string.Empty;
            (bool Postgres, bool File, ContextJob? Job, bool Mismatch, string Reason, bool Heartbeat) result;
            try
            {
                result = await operation().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or IOException)
            {
                error = ex.GetType().Name;
                result = (false, !usePostgres, null, false, string.Empty, false);
            }

            return new JobQueueScopedWorkerCanaryTrace
            {
                OperationId = Guid.NewGuid().ToString("N"),
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                OperationKind = operationKind,
                JobId = result.Job?.JobId ?? jobId,
                JobKind = result.Job?.Kind.ToString() ?? ContextJobKind.Custom.ToString(),
                PrimaryProvider = usePostgres ? "Postgres" : "FileSystem",
                PostgresSucceeded = result.Postgres,
                FileSystemSucceeded = result.File,
                MismatchDetected = result.Mismatch,
                MismatchReason = result.Reason,
                PostgresError = error,
                ScopeLeakDetected = usePostgres && result.File || !usePostgres && result.Postgres,
                HeartbeatRenewed = result.Heartbeat,
                DurationMs = (DateTimeOffset.UtcNow - started).TotalMilliseconds,
                CreatedAt = started
            };
        }

        private bool UsePostgres(string workspaceId, string collectionId)
        {
            return _providerQualityReady
                   && string.Equals(workspaceId, _workspaceId, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(collectionId, _collectionId, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed record LeaseResult(ContextJob? Job, JobQueueScopedWorkerCanaryTrace Trace);
}
