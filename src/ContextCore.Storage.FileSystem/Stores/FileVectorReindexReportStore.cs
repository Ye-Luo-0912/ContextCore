using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>Vector reindex 报告的文件系统存储实现。</summary>
public sealed class FileVectorReindexReportStore : IVectorReindexReportStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FileJsonLineStore _jsonLines;
    private readonly FilePathResolver _paths;

    public FileVectorReindexReportStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileVectorReindexReportStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
    }

    public async Task SaveAsync(VectorReindexResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var report = EnsureReportId(result);
        var path = _paths.GetVectorReindexReportsJsonlPath(report.WorkspaceId, report.CollectionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _jsonLines.UpsertAsync(path, report, item => item.ReportId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<VectorReindexResult>> QueryAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
    {
        var limit = take > 0 ? take : 50;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetVectorReindexReportsJsonlPath(workspaceId, collectionId);
            var reports = await _jsonLines.ReadAsync<VectorReindexResult>(path, cancellationToken)
                .ConfigureAwait(false);
            return
            [
                .. reports
                    .Where(report => string.Equals(report.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
                    .Where(report =>
                        string.Equals(report.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(report => report.CompletedAt)
                    .Take(limit)
            ];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VectorReindexResult?> GetAsync(
        string reportId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var workspacesDirectory = Path.Combine(_paths.RootPath, "workspaces");
            if (!Directory.Exists(workspacesDirectory))
            {
                return null;
            }

            foreach (var path in Directory.EnumerateFiles(workspacesDirectory, "reindex-reports.jsonl", SearchOption.AllDirectories))
            {
                var reports = await _jsonLines.ReadAsync<VectorReindexResult>(path, cancellationToken)
                    .ConfigureAwait(false);
                var match = reports.FirstOrDefault(report =>
                    string.Equals(report.ReportId, reportId, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return match;
                }
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static VectorReindexResult EnsureReportId(VectorReindexResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.ReportId))
        {
            return result;
        }

        return new VectorReindexResult
        {
            ReportId = Guid.NewGuid().ToString("N"),
            OperationId = result.OperationId,
            JobId = result.JobId,
            WorkspaceId = result.WorkspaceId,
            CollectionId = result.CollectionId,
            Plan = result.Plan,
            Summary = result.Summary,
            ProcessedItems = result.ProcessedItems,
            Warnings = result.Warnings,
            Errors = result.Errors,
            StartedAt = result.StartedAt,
            CompletedAt = result.CompletedAt
        };
    }
}
