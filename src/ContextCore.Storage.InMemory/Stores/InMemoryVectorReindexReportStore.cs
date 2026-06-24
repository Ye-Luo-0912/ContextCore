using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.InMemory;

/// <summary>Vector reindex 报告的内存存储实现。</summary>
public sealed class InMemoryVectorReindexReportStore : IVectorReindexReportStore
{
    private readonly ConcurrentDictionary<string, VectorReindexResult> _reports = new(StringComparer.OrdinalIgnoreCase);

    public Task SaveAsync(VectorReindexResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();

        var report = EnsureReportId(result);
        _reports[report.ReportId] = report;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VectorReindexResult>> QueryAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var limit = take > 0 ? take : 50;
        var results = _reports.Values
            .Where(report => string.Equals(report.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
            .Where(report => string.Equals(report.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(report => report.CompletedAt)
            .Take(limit)
            .ToArray();
        return Task.FromResult<IReadOnlyList<VectorReindexResult>>(results);
    }

    public Task<VectorReindexResult?> GetAsync(
        string reportId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_reports.TryGetValue(reportId, out var report) ? report : null);
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
