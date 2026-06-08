using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>Read-only export service for lifecycle-aware ranker shadow traces captured in retrieval traces.</summary>
public sealed class RankerShadowTraceExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IRetrievalTraceStore? _traceStore;

    public RankerShadowTraceExportService(IRetrievalTraceStore? traceStore)
    {
        _traceStore = traceStore;
    }

    public async Task<IReadOnlyList<LifecycleAwareRankerShadowTraceRecord>> QueryAsync(
        string workspaceId,
        string? collectionId,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        if (_traceStore is null)
        {
            return Array.Empty<LifecycleAwareRankerShadowTraceRecord>();
        }

        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            throw new ArgumentException("workspaceId is required.", nameof(workspaceId));
        }

        if (string.IsNullOrWhiteSpace(collectionId))
        {
            return Array.Empty<LifecycleAwareRankerShadowTraceRecord>();
        }

        var traces = await _traceStore.QueryRecentAsync(
                workspaceId.Trim(),
                collectionId.Trim(),
                take > 0 ? take : 50,
                cancellationToken)
            .ConfigureAwait(false);

        return traces
            .Where(static trace => trace.RankerShadowTrace.RankerShadowEnabled)
            .Where(static trace => trace.RankerShadowTrace.CandidateShadowScores.Count > 0)
            .Select(ToRecord)
            .ToArray();
    }

    public async Task<string> ExportJsonLinesAsync(
        string workspaceId,
        string? collectionId,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        var records = await QueryAsync(workspaceId, collectionId, take, cancellationToken).ConfigureAwait(false);
        return string.Join(
            Environment.NewLine,
            records.Select(static record => JsonSerializer.Serialize(record, JsonOptions)));
    }

    private static LifecycleAwareRankerShadowTraceRecord ToRecord(ContextRetrievalTrace trace)
    {
        return new LifecycleAwareRankerShadowTraceRecord
        {
            RetrievalId = trace.RetrievalId,
            WorkspaceId = trace.WorkspaceId,
            CollectionId = trace.CollectionId,
            Query = trace.QueryText ?? trace.RewrittenQueryText ?? string.Empty,
            Profile = trace.RankerShadowTrace.RankerShadowProfile,
            CreatedAt = trace.CreatedAt,
            CandidateScores = trace.RankerShadowTrace.CandidateShadowScores,
            DeprecatedDemotions = trace.RankerShadowTrace.DeprecatedDemotions,
            VersionConflictFixes = trace.RankerShadowTrace.VersionConflictFixes,
            MustHitDemotions = trace.RankerShadowTrace.MustHitDemotions,
            MustNotHitPromotions = trace.RankerShadowTrace.MustNotHitPromotions,
            Metadata = new Dictionary<string, string>(trace.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }
}
