using System.IO.Compression;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Service.Infrastructure;
using ContextCore.Service.Security;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Backup;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Service.Endpoints;

/// <summary>
/// 管理员专用端点（备份 / 校验 / Schema 版本），全部需要 API Key 认证。
/// <list type="bullet">
/// <item><c>GET /api/admin/backup/status</c>：存储信息概览。</item>
/// <item><c>POST /api/admin/backup/create</c>：创建 FileSystem 数据目录 ZIP 快照。</item>
/// <item><c>GET /api/admin/backup/validate</c>：校验所有 JSONL 文件完整性。</item>
/// <item><c>GET /api/admin/schema-version</c>：返回 Postgres schema 版本。</item>
/// </list>
/// </summary>
internal static class AdminEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // Admin 端点全部需要 Admin 角色（含备份 / 校验 / Schema 迁移 / 配置写入）。
        // RBAC 强制校验未启用时（Security:Rbac:Enforce=false）自动放行，仅记录审计日志。
        var group = app.MapGroup("/api/admin")
            .WithTags("Admin")
            .RequireWorkspaceRole(WorkspaceRole.Admin);

        // ── Admin ingest ───────────────────────────────────────────────
        group.MapPost("/ingest", async Task<IResult> (
            ContextInputCommand command,
            ContextInputIngestionService ingestionService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            try
            {
                var result = await ingestionService.IngestDetailedAsync(command, ct).ConfigureAwait(false);
                return ContextCoreHttpResultMapper.Success(result);
            }
            catch (Exception ex)
            {
                return ContextCoreHttpResultMapper.Error(httpContext, ex, command.OperationId, "admin.ingest");
            }
        })
        .WithName("AdminIngestContextInput")
        .WithSummary("通过 ContextInputCommand 执行标准化输入摄取，返回幂等与顺序信息")
        .Produces<ContextInputIngestionResult>(StatusCodes.Status200OK);

        // ── Admin status ───────────────────────────────────────────────
        group.MapGet("/status", (
            StorageOptions storage,
            string? workspaceId,
            string? collectionId) =>
        {
            return Results.Ok(new ContextCoreAdminStatusResponse
            {
                Storage = new ContextCoreStorageInfo
                {
                    Provider = storage.Provider,
                    RootPath = storage.IsFileSystem ? storage.ResolvedRootPath : null
                },
                Workspace = workspaceId,
                Collection = collectionId,
                RetrievalBaseline = ServiceAlphaRuntimeInspector.RetrievalBaselineName
            });
        })
        .WithName("AdminStatus")
        .WithSummary("返回 Admin 视角的存储与 retrieval baseline 状态摘要")
        .Produces<ContextCoreAdminStatusResponse>(StatusCodes.Status200OK);

        // ── Backup status ──────────────────────────────────────────────
        group.MapGet("/backup/status", async (StorageOptions storage, IServiceProvider sp, CancellationToken ct) =>
        {
            if (storage.IsFileSystem)
            {
                var root = storage.ResolvedRootPath;
                var exists = Directory.Exists(root);
                long totalBytes = 0;
                var fileCount = 0;
                var jsonlCount = 0;
                if (exists)
                {
                    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var info = new FileInfo(file);
                            totalBytes += info.Length;
                            fileCount++;
                            if (file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                                jsonlCount++;
                        }
                        catch { /* 跳过无法访问的文件 */ }
                    }
                }
                return Results.Ok(new ContextCoreBackupStatusResponse
                {
                    Provider = "filesystem",
                    Root = root,
                    Exists = exists,
                    FileCount = fileCount,
                    JsonlFileCount = jsonlCount,
                    TotalSizeBytes = totalBytes,
                    TotalSizeMb = Math.Round(totalBytes / 1_048_576.0, 2)
                });
            }

            if (storage.IsPostgres)
            {
                // 检测 PostgresBackupRunner 是否已注册，并返回最近 dump 文件信息（若默认备份目录中存在）。
                var runnerRegistered = sp.GetService<PostgresBackupRunner>() is not null;

                // 默认备份目录：与 ControlRoom BackupCommand 一致——<data-root>/../_backups/
                // Postgres 模式下 data-root 来自 storage 配置；此处仅作 best-effort 探测。
                string? lastDumpPath = null;
                long? lastDumpSizeBytes = null;
                string? lastDumpHash = null;
                try
                {
                    var dataRoot = storage.ResolvedRootPath;
                    var backupDir = Path.Combine(Path.GetDirectoryName(dataRoot) ?? dataRoot, "_backups");
                    if (Directory.Exists(backupDir))
                    {
                        var dumpFile = Directory.EnumerateFiles(backupDir, "postgres_*.dump", SearchOption.TopDirectoryOnly)
                            .OrderByDescending(f => f)
                            .FirstOrDefault();
                        if (dumpFile is not null)
                        {
                            lastDumpPath = dumpFile;
                            lastDumpSizeBytes = new FileInfo(dumpFile).Length;
                            // 哈希计算可能较慢，仅在已注册 runner（即正式运行时）执行
                            if (runnerRegistered)
                            {
                                lastDumpHash = ContextCore.Storage.Shared.Sha256Utility.HashFile(dumpFile);
                            }
                        }
                    }
                }
                catch
                {
                    // best-effort：失败时仅置空
                }

                return Results.Ok(new ContextCoreBackupStatusResponse
                {
                    Provider = "postgres",
                    SchemaVersion = PostgresMigrationRunner.SchemaVersion,
                    Note = runnerRegistered
                        ? "PostgresBackupRunner 已注册；使用 POST /api/admin/backup/pg-create 创建转储，POST /api/admin/backup/pg-restore 恢复。"
                        : "PostgresBackupRunner 未注册（Storage provider 不为 postgres 或注册缺失）。可使用 pg_dump 命令行工具备份。",
                    LastDumpPath = lastDumpPath,
                    LastDumpSizeBytes = lastDumpSizeBytes,
                    LastDumpHash = lastDumpHash
                });
            }

            return Results.Ok(new ContextCoreBackupStatusResponse
            {
                Provider = storage.Provider,
                Note = "memory provider 无持久化数据，无备份需要。"
            });
        })
        .WithName("AdminBackupStatus")
        .WithSummary("存储备份信息概览（FS：文件统计 + 大小；Postgres：schema 版本 + 最近 dump 文件信息）")
        .Produces<ContextCoreBackupStatusResponse>(StatusCodes.Status200OK);

        // ── Create filesystem ZIP backup ───────────────────────────────
        group.MapPost("/backup/create", async Task<IResult> (StorageOptions storage, HttpContext httpContext, CancellationToken ct) =>
        {
            if (!storage.IsFileSystem)
            {
                return ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext,
                    string.Empty,
                    "admin.backup.create",
                    $"备份创建仅支持 filesystem provider（当前：{storage.Provider}）。Postgres 请使用 pg_dump。");
            }

            var root = storage.ResolvedRootPath;
            if (!Directory.Exists(root))
            {
                return ContextCoreHttpResultMapper.NotFound(
                    httpContext,
                    string.Empty,
                    "admin.backup.create",
                    $"数据根目录不存在：{root}",
                    detailCode: "storage_root_not_found");
            }

            // 备份目录：数据根目录的同级 _backups 目录
            var backupDir = Path.Combine(Path.GetDirectoryName(root) ?? root, "_backups");
            Directory.CreateDirectory(backupDir);

            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss");
            var zipPath = Path.Combine(backupDir, $"contextcore_backup_{timestamp}.zip");

            try
            {
                await Task.Run(() => ZipFile.CreateFromDirectory(root, zipPath,
                    CompressionLevel.Fastest, includeBaseDirectory: false), ct)
                    .ConfigureAwait(false);

                var zipInfo = new FileInfo(zipPath);
                return Results.Ok(new ContextCoreBackupCreateResponse
                {
                    BackupPath = zipPath,
                    BackupSizeBytes = zipInfo.Length,
                    BackupSizeMb = Math.Round(zipInfo.Length / 1_048_576.0, 2),
                    SourceRoot = root,
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
            catch (Exception ex)
            {
                // 失败时删除不完整的 zip，避免留下损坏文件
                if (File.Exists(zipPath))
                {
                    try { File.Delete(zipPath); } catch { /* ignore */ }
                }
                return ContextCoreHttpResultMapper.InternalError(
                    httpContext,
                    string.Empty,
                    "admin.backup.create",
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        })
        .WithName("AdminBackupCreate")
        .WithSummary("创建 FileSystem 数据目录 ZIP 快照（保存到 <data-root>/../_backups/）")
        .Produces<ContextCoreBackupCreateResponse>(StatusCodes.Status200OK)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status404NotFound);

        // ── Postgres pg_dump create ────────────────────────────────────
        group.MapPost("/backup/pg-create", async Task<IResult> (
            StorageOptions storage,
            HttpContext httpContext,
            IServiceProvider sp,
            CancellationToken ct) =>
        {
            if (!storage.IsPostgres)
            {
                return ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext,
                    string.Empty,
                    "admin.backup.pg-create",
                    $"Postgres 备份仅支持 postgres provider（当前：{storage.Provider}）。");
            }

            var runner = sp.GetService<PostgresBackupRunner>();
            if (runner is null)
            {
                return ContextCoreHttpResultMapper.InternalError(
                    httpContext,
                    string.Empty,
                    "admin.backup.pg-create",
                    "PostgresBackupRunner 未注册；请确认 AddContextCorePostgresStorage 已调用。");
            }

            // 备份目录：与 ControlRoom BackupCommand 一致
            var dataRoot = storage.ResolvedRootPath;
            var backupDir = Path.Combine(Path.GetDirectoryName(dataRoot) ?? dataRoot, "_backups");
            Directory.CreateDirectory(backupDir);

            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss");
            var dumpPath = Path.Combine(backupDir, $"postgres_{timestamp}.dump");
            var manifestPath = dumpPath + ".manifest.json";

            try
            {
                var dumpResult = await runner.DumpAsync(dumpPath, ct).ConfigureAwait(false);
                var manifest = await BackupManifestGenerator.ForPostgresDumpAsync(
                    dumpPath, storage.ResolvedPostgresConnectionString, dumpResult, ct).ConfigureAwait(false);
                await BackupManifestGenerator.WriteAsync(manifest, manifestPath, ct).ConfigureAwait(false);

                return Results.Ok(new ContextCoreBackupCreateResponse
                {
                    BackupPath = dumpPath,
                    BackupSizeBytes = dumpResult.DumpSizeBytes,
                    BackupSizeMb = Math.Round(dumpResult.DumpSizeBytes / 1_048_576.0, 2),
                    SourceRoot = BackupManifestGenerator.StripCredentialsFromConnectionString(storage.ResolvedPostgresConnectionString),
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
            catch (Exception ex)
            {
                if (File.Exists(dumpPath)) { try { File.Delete(dumpPath); } catch { /* ignore */ } }
                if (File.Exists(manifestPath)) { try { File.Delete(manifestPath); } catch { /* ignore */ } }
                return ContextCoreHttpResultMapper.InternalError(
                    httpContext,
                    string.Empty,
                    "admin.backup.pg-create",
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        })
        .WithName("AdminBackupPgCreate")
        .WithSummary("通过 pg_dump 创建 PostgreSQL 转储（.dump）并生成清单（admin-gated）")
        .Produces<ContextCoreBackupCreateResponse>(StatusCodes.Status200OK)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest);

        // ── Postgres pg_restore ────────────────────────────────────────
        group.MapPost("/backup/pg-restore", async Task<IResult> (
            PostgresBackupRestoreRequest request,
            StorageOptions storage,
            HttpContext httpContext,
            IServiceProvider sp,
            CancellationToken ct) =>
        {
            if (!storage.IsPostgres)
            {
                return ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext,
                    string.Empty,
                    "admin.backup.pg-restore",
                    $"Postgres 恢复仅支持 postgres provider（当前：{storage.Provider}）。");
            }

            if (string.IsNullOrWhiteSpace(request.DumpPath) || !File.Exists(request.DumpPath))
            {
                return ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext,
                    string.Empty,
                    "admin.backup.pg-restore",
                    $"转储文件不存在或路径为空：{request.DumpPath}");
            }

            if (!request.Confirm)
            {
                return ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext,
                    string.Empty,
                    "admin.backup.pg-restore",
                    "破坏性操作：必须显式设置 confirm=true 才能执行 pg_restore --clean --if-exists。");
            }

            var runner = sp.GetService<PostgresBackupRunner>();
            if (runner is null)
            {
                return ContextCoreHttpResultMapper.InternalError(
                    httpContext,
                    string.Empty,
                    "admin.backup.pg-restore",
                    "PostgresBackupRunner 未注册；请确认 AddContextCorePostgresStorage 已调用。");
            }

            try
            {
                await runner.RestoreAsync(request.DumpPath, cleanBeforeRestore: true, ct).ConfigureAwait(false);
                return Results.Ok(new { Restored = true, request.DumpPath, RestoredAt = DateTimeOffset.UtcNow });
            }
            catch (Exception ex)
            {
                return ContextCoreHttpResultMapper.InternalError(
                    httpContext,
                    string.Empty,
                    "admin.backup.pg-restore",
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        })
        .WithName("AdminBackupPgRestore")
        .WithSummary("通过 pg_restore 将 .dump 恢复到目标 PostgreSQL 数据库（破坏性，需 confirm=true；admin-gated）")
        .Produces<object>(StatusCodes.Status200OK)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest);

        // ── Validate all JSONL files ───────────────────────────────────
        group.MapGet("/backup/validate", async Task<IResult> (StorageOptions storage, HttpContext httpContext, CancellationToken ct) =>
        {
            if (!storage.IsFileSystem)
            {
                return ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext,
                    string.Empty,
                    "admin.backup.validate",
                    $"JSONL 校验仅适用于 filesystem provider（当前：{storage.Provider}）。");
            }

            var root = storage.ResolvedRootPath;
            if (!Directory.Exists(root))
                return ContextCoreHttpResultMapper.NotFound(
                    httpContext,
                    string.Empty,
                    "admin.backup.validate",
                    $"数据根目录不存在：{root}",
                    detailCode: "storage_root_not_found");

            var inspector = new FileJsonLineInspector();
            var reports = new List<ContextCoreBackupValidateFile>();
            var corruptFiles = 0;

            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var report = await inspector.InspectAsync(file, ct).ConfigureAwait(false);
                if (!report.IsHealthy)
                {
                    corruptFiles++;
                    reports.Add(new ContextCoreBackupValidateFile
                    {
                        File = Path.GetRelativePath(root, file),
                        TotalLines = report.TotalLines,
                        ValidLines = report.ValidLines,
                        CorruptLines = report.CorruptLines,
                        Issues = report.Issues.Take(10).Select(i => new ContextCoreBackupValidateIssue
                        {
                            Line = i.LineNumber,
                            Message = i.Message,
                            Preview = i.Preview.Length > 80 ? i.Preview[..80] + "…" : i.Preview
                        }).ToArray()
                    });
                }
            }

            var allJsonlFiles = Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories).Length;
            return corruptFiles == 0
                ? Results.Ok(new ContextCoreBackupValidateResponse
                {
                    Healthy = true,
                    Message = $"所有 {allJsonlFiles} 个 JSONL 文件均通过校验。",
                    ScannedFiles = allJsonlFiles,
                    CorruptFiles = 0
                })
                : Results.Ok(new ContextCoreBackupValidateResponse
                {
                    Healthy = false,
                    ScannedFiles = allJsonlFiles,
                    CorruptFiles = corruptFiles,
                    Files = reports
                });
        })
        .WithName("AdminBackupValidate")
        .WithSummary("校验所有 JSONL 文件完整性（filesystem only），返回损坏行详情")
        .Produces<ContextCoreBackupValidateResponse>(StatusCodes.Status200OK)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status404NotFound);

        // ── Postgres schema version ────────────────────────────────────
        group.MapGet("/schema-version", async (StorageOptions storage, IServiceProvider sp, CancellationToken ct) =>
        {
            if (!storage.IsPostgres)
            {
                return Results.Ok(new ContextCoreSchemaVersionResponse
                {
                    Provider = storage.Provider,
                    SchemaVersion = null,
                    Note = "schema 版本仅用于 postgres provider。"
                });
            }

            // 从数据库读取已记录的最新版本；如尚未迁移则返回 null。
            var migrationRunner = sp.GetService<PostgresMigrationRunner>();
            var appliedVersion = migrationRunner is not null
                ? await migrationRunner.GetAppliedVersionAsync(ct).ConfigureAwait(false)
                : null;

            return Results.Ok(new ContextCoreSchemaVersionResponse
            {
                Provider = "postgres",
                CodeVersion = PostgresMigrationRunner.SchemaVersion,
                AppliedVersion = appliedVersion,
                UpToDate = appliedVersion == PostgresMigrationRunner.SchemaVersion,
                // 暴露真实 AutoBootstrap 配置值（默认 true）；保留 AutoMigrate=true 仅为向后兼容。
                AutoMigrate = true,
                AutoBootstrap = storage.AutoBootstrap
            });
        })
        .WithName("AdminSchemaVersion")
        .WithSummary("返回 Postgres schema 版本：代码版本 vs 数据库已应用版本")
        .Produces<ContextCoreSchemaVersionResponse>(StatusCodes.Status200OK);

        group.MapGet("/storage/postgres/status", async (
            StorageOptions storage,
            IServiceProvider sp,
            CancellationToken ct) =>
        {
            var diagnostics = await BuildPostgresDiagnosticsAsync(storage, sp, ct).ConfigureAwait(false);
            return Results.Ok(new PostgresStorageStatusResponse
            {
                Enabled = diagnostics.ProviderEnabled,
                ProviderId = diagnostics.ProviderId,
                ConnectionAvailable = diagnostics.ConnectionAvailable,
                CurrentSchemaVersion = diagnostics.CurrentSchemaVersion,
                PendingMigrations = diagnostics.PendingMigrations,
                RequiredTableMissingCount = diagnostics.RequiredTableMissingCount,
                CapabilityStatus = diagnostics.ProviderCapabilityStatus,
                Diagnostics = diagnostics.Diagnostics
            });
        })
        .WithName("AdminPostgresStorageStatus")
        .WithSummary("返回 PostgreSQL operational store 状态摘要，不包含明文连接串")
        .Produces<PostgresStorageStatusResponse>(StatusCodes.Status200OK);

        group.MapGet("/storage/postgres/diagnostics", async (
            StorageOptions storage,
            IServiceProvider sp,
            CancellationToken ct) =>
        {
            var diagnostics = await BuildPostgresDiagnosticsAsync(storage, sp, ct).ConfigureAwait(false);
            return Results.Ok(diagnostics);
        })
        .WithName("AdminPostgresStorageDiagnostics")
        .WithSummary("返回 PostgreSQL operational store 诊断，不自动迁移")
        .Produces<PostgresOperationalStoreDiagnostics>(StatusCodes.Status200OK);

        group.MapPost("/storage/postgres/migrations/dry-run", async (
            StorageOptions storage,
            IServiceProvider sp,
            CancellationToken ct) =>
        {
            var runner = sp.GetService<IStoreMigrationRunner>();
            if (!storage.IsPostgres || runner is null)
            {
                var options = BuildEndpointPostgresOptions(storage, enabled: false);
                var disabled = PostgresOperationalStoreDiagnosticsBuilder.BuildNotConfigured(options);
                return Results.Ok(new PostgresMigrationPlanResponse
                {
                    DryRun = true,
                    ProviderEnabled = false,
                    ProviderId = disabled.ProviderId,
                    CurrentSchemaVersion = null,
                    PendingMigrations = [PostgresMigrationRunner.BaselineMigrationId],
                    RequiredTables = disabled.RequiredTables,
                    MissingRequiredTables = disabled.MissingRequiredTables,
                    Diagnostics = disabled.Diagnostics
                });
            }

            var plan = await runner.PreviewMigrationsAsync(ct).ConfigureAwait(false);
            return Results.Ok(ToPlanResponse(plan));
        })
        .WithName("AdminPostgresMigrationDryRun")
        .WithSummary("预览 PostgreSQL baseline migrations，不写数据库")
        .Produces<PostgresMigrationPlanResponse>(StatusCodes.Status200OK);

        group.MapPost("/storage/postgres/migrations/apply", async (
            PostgresMigrationRequest request,
            StorageOptions storage,
            IServiceProvider sp,
            CancellationToken ct) =>
        {
            var runner = sp.GetService<IStoreMigrationRunner>();
            if (!storage.IsPostgres || runner is null)
            {
                return Results.Ok(new PostgresMigrationApplyResponse
                {
                    Applied = false,
                    ConfirmRequired = false,
                    Diagnostics = ["NotConfigured"]
                });
            }

            var result = await runner.ApplyMigrationsAsync(request.Confirm, ct).ConfigureAwait(false);
            return Results.Ok(new PostgresMigrationApplyResponse
            {
                Applied = result.Applied,
                ConfirmRequired = result.ConfirmRequired,
                SchemaVersion = result.SchemaVersion,
                AppliedMigrations = result.AppliedMigrations,
                Diagnostics = result.Diagnostics
            });
        })
        .WithName("AdminPostgresMigrationApply")
        .WithSummary("显式确认后应用 PostgreSQL baseline migrations")
        .Produces<PostgresMigrationApplyResponse>(StatusCodes.Status200OK);

        group.MapGet("/storage/relation-provider/status", (
            IServiceProvider sp) =>
        {
            var status = sp.GetService<RelationGovernanceScopedServiceModeStatusService>()?.GetStatus()
                ?? new PostgresRelationScopedServiceModeStatusResponse
                {
                    CurrentMode = RelationGovernanceProviderMode.FileSystemPrimary.ToString(),
                    ActiveRuntimeProvider = "FileSystemRelationStore",
                    Diagnostics = ["ScopedServiceModeNotConfigured"],
                    Recommendation = "FileSystemPrimary"
                };
            return Results.Ok(status);
        })
        .WithName("AdminRelationProviderStatus")
        .WithSummary("返回 RelationStore scoped service mode 状态，不包含明文连接串")
        .Produces<PostgresRelationScopedServiceModeStatusResponse>(StatusCodes.Status200OK);

        group.MapGet("/storage/relation-provider/scoped-diagnostics", (
            IServiceProvider sp) =>
        {
            var status = sp.GetService<RelationGovernanceScopedServiceModeStatusService>()?.GetStatus()
                ?? new PostgresRelationScopedServiceModeStatusResponse
                {
                    CurrentMode = RelationGovernanceProviderMode.FileSystemPrimary.ToString(),
                    ActiveRuntimeProvider = "FileSystemRelationStore",
                    Diagnostics = ["ScopedServiceModeNotConfigured"],
                    Recommendation = "FileSystemPrimary"
                };
            return Results.Ok(status);
        })
        .WithName("AdminRelationProviderScopedDiagnostics")
        .WithSummary("返回 Relation governance scoped service mode 诊断")
        .Produces<PostgresRelationScopedServiceModeStatusResponse>(StatusCodes.Status200OK);

        // ── In-process metrics ─────────────────────────────────────────
        group.MapGet("/metrics", (Infrastructure.ContextCoreMetrics metrics) =>
            Results.Ok(metrics.GetSnapshot()))
            .WithName("AdminMetrics")
            .WithSummary("API 级别延迟统计（P50/P95/P99）+ 错误率，基于内存滚动窗口（最近 2000 次请求）")
            .Produces<MetricsSnapshot>(StatusCodes.Status200OK);

        return app;
    }

    private static async Task<PostgresOperationalStoreDiagnostics> BuildPostgresDiagnosticsAsync(
        StorageOptions storage,
        IServiceProvider sp,
        CancellationToken cancellationToken)
    {
        if (!storage.IsPostgres)
        {
            return PostgresOperationalStoreDiagnosticsBuilder.BuildNotConfigured(
                BuildEndpointPostgresOptions(storage, enabled: false));
        }

        var options = sp.GetService<PostgresOptions>() ?? BuildEndpointPostgresOptions(storage, enabled: true);
        return await PostgresOperationalStoreDiagnosticsBuilder.BuildAsync(
            options,
            sp.GetService<IPostgresConnectionFactory>(),
            sp.GetService<IStoreMigrationRunner>(),
            cancellationToken).ConfigureAwait(false);
    }

    private static PostgresOptions BuildEndpointPostgresOptions(StorageOptions storage, bool enabled)
    {
        return new PostgresOptions
        {
            Enabled = enabled,
            ConnectionString = storage.ResolvedPostgresConnectionString,
            AutoMigrate = false,
            EnablePgVectorExtension = true
        };
    }

    private static PostgresMigrationPlanResponse ToPlanResponse(PostgresMigrationPlan plan)
    {
        return new PostgresMigrationPlanResponse
        {
            DryRun = true,
            ProviderEnabled = plan.ProviderEnabled,
            ProviderId = plan.ProviderId,
            CurrentSchemaVersion = plan.CurrentSchemaVersion,
            PendingMigrations = plan.PendingMigrations,
            RequiredTables = plan.RequiredTables,
            MissingRequiredTables = plan.MissingRequiredTables,
            Diagnostics = plan.Diagnostics
        };
    }
}
