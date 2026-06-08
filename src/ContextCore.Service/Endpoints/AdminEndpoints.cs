using System.IO.Compression;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Core.Services;
using ContextCore.Service.Infrastructure;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.Postgres;

namespace ContextCore.Service.Endpoints;

/// <summary>
/// 管理员专用端点（备份 / 校验 / Schema 版本），全部需要 API Key 认证。
/// <list type="bullet">
///   <item><c>GET  /api/admin/backup/status</c>：存储信息概览。</item>
///   <item><c>POST /api/admin/backup/create</c>：创建 FileSystem 数据目录 ZIP 快照。</item>
///   <item><c>GET  /api/admin/backup/validate</c>：校验所有 JSONL 文件完整性。</item>
///   <item><c>GET  /api/admin/schema-version</c>：返回 Postgres schema 版本。</item>
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
        var group = app.MapGroup("/api/admin").WithTags("Admin");

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
        .WithSummary("通过 ContextInputCommand 执行标准化输入摄取，返回幂等与顺序信息");

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
        .WithSummary("返回 Admin 视角的存储与 retrieval baseline 状态摘要");

        // ── Backup status ──────────────────────────────────────────────
        group.MapGet("/backup/status", (StorageOptions storage, IServiceProvider sp) =>
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
                return Results.Ok(new ContextCoreBackupStatusResponse
                {
                    Provider = "postgres",
                    SchemaVersion = PostgresMigrationRunner.SchemaVersion,
                    Note = "备份请使用 pg_dump，示例：pg_dump -Fc <db_name> > backup.dump"
                });
            }

            return Results.Ok(new ContextCoreBackupStatusResponse
            {
                Provider = storage.Provider,
                Note = "memory provider 无持久化数据，无备份需要。"
            });
        })
        .WithName("AdminBackupStatus")
        .WithSummary("存储备份信息概览（FS：文件统计 + 大小；Postgres：schema 版本 + pg_dump 建议）");

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
        .WithSummary("创建 FileSystem 数据目录 ZIP 快照（保存到 <data-root>/../_backups/）");

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
        .WithSummary("校验所有 JSONL 文件完整性（filesystem only），返回损坏行详情");

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
                AutoMigrate = true
            });
        })
        .WithName("AdminSchemaVersion")
        .WithSummary("返回 Postgres schema 版本：代码版本 vs 数据库已应用版本");

        // ── In-process metrics ─────────────────────────────────────────
        group.MapGet("/metrics", (Infrastructure.ContextCoreMetrics metrics) =>
            Results.Ok(metrics.GetSnapshot()))
            .WithName("AdminMetrics")
            .WithSummary("API 级别延迟统计（P50/P95/P99）+ 错误率，基于内存滚动窗口（最近 2000 次请求）");

        return app;
    }
}
