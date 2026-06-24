using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Graph;

/// <summary>导出 runtime graph expansion shadow trace；只读读取检索 trace。</summary>
public sealed class GraphExpansionShadowTraceExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IRetrievalTraceStore? _traceStore;

    public GraphExpansionShadowTraceExportService(IRetrievalTraceStore? traceStore)
    {
        _traceStore = traceStore;
    }

    public async Task<IReadOnlyList<GraphExpansionShadowTraceRecord>> QueryAsync(
        string workspaceId,
        string? collectionId,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        if (_traceStore is null)
        {
            return Array.Empty<GraphExpansionShadowTraceRecord>();
        }

        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            throw new ArgumentException("workspaceId is required.", nameof(workspaceId));
        }

        if (string.IsNullOrWhiteSpace(collectionId))
        {
            return Array.Empty<GraphExpansionShadowTraceRecord>();
        }

        var traces = await _traceStore
            .QueryRecentAsync(
                workspaceId.Trim(),
                collectionId.Trim(),
                take > 0 ? take : 50,
                cancellationToken)
            .ConfigureAwait(false);

        var records = traces
            .Where(static trace => trace.GraphExpansionShadowTrace.GraphExpansionShadowEnabled)
            .Where(static trace => trace.GraphExpansionShadowTrace.AcceptedRelations.Count > 0
                || trace.GraphExpansionShadowTrace.BlockedRelations.Count > 0)
            .Select(ToRecord)
            .Where(static record => !IsDuplicateSuppressed(record))
            .ToArray();

        return records
            .GroupBy(ResolveSignature, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(static record => record.CreatedAt)
                .First())
            .OrderByDescending(static record => record.CreatedAt)
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

    private static GraphExpansionShadowTraceRecord ToRecord(ContextRetrievalTrace trace)
    {
        var metadata = new Dictionary<string, string>(trace.Metadata, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in trace.GraphExpansionShadowTrace.Metadata)
        {
            metadata[$"graphExpansionShadow.{pair.Key}"] = pair.Value;
        }

        return new GraphExpansionShadowTraceRecord
        {
            RetrievalId = trace.RetrievalId,
            WorkspaceId = trace.WorkspaceId,
            CollectionId = trace.CollectionId,
            Query = trace.QueryText ?? trace.RewrittenQueryText ?? string.Empty,
            Profiles = trace.GraphExpansionShadowTrace.GraphExpansionProfiles,
            CreatedAt = trace.CreatedAt,
            AcceptedRelations = trace.GraphExpansionShadowTrace.AcceptedRelations,
            BlockedRelations = trace.GraphExpansionShadowTrace.BlockedRelations,
            TargetSections = new Dictionary<string, int>(
                trace.GraphExpansionShadowTrace.TargetSections,
                StringComparer.OrdinalIgnoreCase),
            RiskIfNormal = trace.GraphExpansionShadowTrace.RiskIfNormal,
            RiskAfterRouting = trace.GraphExpansionShadowTrace.RiskAfterRouting,
            HistoricalAuditCount = trace.GraphExpansionShadowTrace.HistoricalAuditCount,
            ConflictEvidenceCount = trace.GraphExpansionShadowTrace.ConflictEvidenceCount,
            WrongSectionRisk = trace.GraphExpansionShadowTrace.WrongSectionRisk,
            Metadata = metadata
        };
    }

    private static bool IsDuplicateSuppressed(GraphExpansionShadowTraceRecord record)
    {
        return record.Metadata.TryGetValue("graphExpansionShadow.duplicateSuppressed", out var value)
            && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveSignature(GraphExpansionShadowTraceRecord record)
    {
        if (record.Metadata.TryGetValue("graphExpansionShadow.traceSignature", out var graphSignature)
            && !string.IsNullOrWhiteSpace(graphSignature))
        {
            return graphSignature.Trim();
        }

        if (record.Metadata.TryGetValue("graphExpansionTraceSignature", out var traceSignature)
            && !string.IsNullOrWhiteSpace(traceSignature))
        {
            return traceSignature.Trim();
        }

        return $"retrieval:{record.RetrievalId}";
    }
}
