using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.ControlRoom.Services;

/// <summary>Relation governance 影子读协调器；正式返回值始终来自 FileSystem。</summary>
public sealed class RelationGovernanceShadowReadCoordinator
{
    private static readonly JsonSerializerOptions HashJsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly FileRelationStore _fileRelationStore;
    private readonly FileRelationReviewStore _fileReviewStore;
    private readonly FileRelationDiagnosticsStore _fileDiagnosticsStore;
    private readonly PostgresRelationStore _postgresRelationStore;
    private readonly PostgresRelationReviewStore _postgresReviewStore;
    private readonly PostgresRelationDiagnosticsStore _postgresDiagnosticsStore;
    private readonly RelationGovernanceShadowReadOptions _options;
    private readonly Func<RelationGovernanceShadowReadTrace, CancellationToken, Task> _traceSink;

    public RelationGovernanceShadowReadCoordinator(
        FileRelationStore fileRelationStore,
        FileRelationReviewStore fileReviewStore,
        FileRelationDiagnosticsStore fileDiagnosticsStore,
        PostgresRelationStore postgresRelationStore,
        PostgresRelationReviewStore postgresReviewStore,
        PostgresRelationDiagnosticsStore postgresDiagnosticsStore,
        RelationGovernanceShadowReadOptions options,
        Func<RelationGovernanceShadowReadTrace, CancellationToken, Task> traceSink)
    {
        _fileRelationStore = fileRelationStore;
        _fileReviewStore = fileReviewStore;
        _fileDiagnosticsStore = fileDiagnosticsStore;
        _postgresRelationStore = postgresRelationStore;
        _postgresReviewStore = postgresReviewStore;
        _postgresDiagnosticsStore = postgresDiagnosticsStore;
        _options = options;
        _traceSink = traceSink;
    }

    public Task<ContextRelation?> GetRelationAsync(string operationId, string workspaceId, string collectionId, string relationId, CancellationToken cancellationToken = default)
        => ExecuteAsync(operationId, workspaceId, collectionId, "RelationGet", relationId,
            token => _fileRelationStore.GetAsync(workspaceId, collectionId, relationId, token),
            token => _postgresRelationStore.GetAsync(workspaceId, collectionId, relationId, token),
            cancellationToken);

    public Task<IReadOnlyList<ContextRelation>> ListRelationsAsync(string operationId, string workspaceId, string collectionId, CancellationToken cancellationToken = default)
        => ExecuteAsync(operationId, workspaceId, collectionId, "RelationList", collectionId,
            token => _fileRelationStore.QueryAsync(new ContextRelationQuery { WorkspaceId = workspaceId, CollectionId = collectionId, Take = int.MaxValue }, token),
            token => _postgresRelationStore.QueryAsync(new ContextRelationQuery { WorkspaceId = workspaceId, CollectionId = collectionId, Take = int.MaxValue }, token),
            cancellationToken);

    public Task<IReadOnlyList<ContextRelation>> QueryBySourceAsync(string operationId, string workspaceId, string collectionId, string sourceId, CancellationToken cancellationToken = default)
        => ExecuteAsync(operationId, workspaceId, collectionId, "RelationBySource", sourceId,
            token => _fileRelationStore.QueryBySourceAsync(workspaceId, collectionId, sourceId, token),
            token => _postgresRelationStore.QueryBySourceAsync(workspaceId, collectionId, sourceId, token),
            cancellationToken);

    public Task<IReadOnlyList<ContextRelation>> QueryByTargetAsync(string operationId, string workspaceId, string collectionId, string targetId, CancellationToken cancellationToken = default)
        => ExecuteAsync(operationId, workspaceId, collectionId, "RelationByTarget", targetId,
            token => _fileRelationStore.QueryByTargetAsync(workspaceId, collectionId, targetId, token),
            token => _postgresRelationStore.QueryByTargetAsync(workspaceId, collectionId, targetId, token),
            cancellationToken);

    public Task<IReadOnlyList<ContextRelation>> QueryByTypeAsync(string operationId, string workspaceId, string collectionId, string relationType, CancellationToken cancellationToken = default)
        => ExecuteAsync(operationId, workspaceId, collectionId, "RelationByType", relationType,
            token => _fileRelationStore.QueryByTypeAsync(workspaceId, collectionId, relationType, token),
            token => _postgresRelationStore.QueryByTypeAsync(workspaceId, collectionId, relationType, token),
            cancellationToken);

    public Task<IReadOnlyList<ContextRelation>> QueryByLifecycleAsync(string operationId, string workspaceId, string collectionId, string lifecycle, CancellationToken cancellationToken = default)
        => ExecuteAsync(operationId, workspaceId, collectionId, "RelationByLifecycle", lifecycle,
            token => QueryFileRelationByMetadataAsync(workspaceId, collectionId, "lifecycle", lifecycle, token),
            token => _postgresRelationStore.QueryByLifecycleAsync(workspaceId, collectionId, lifecycle, token),
            cancellationToken);

    public Task<IReadOnlyList<ContextRelation>> QueryByReviewStatusAsync(string operationId, string workspaceId, string collectionId, string reviewStatus, CancellationToken cancellationToken = default)
        => ExecuteAsync(operationId, workspaceId, collectionId, "RelationByReviewStatus", reviewStatus,
            token => QueryFileRelationByMetadataAsync(workspaceId, collectionId, "reviewStatus", reviewStatus, token),
            token => _postgresRelationStore.QueryByReviewStatusAsync(workspaceId, collectionId, reviewStatus, token),
            cancellationToken);

    public Task<IReadOnlyList<ContextRelation>> QueryReplacementChainAsync(string operationId, string workspaceId, string collectionId, string itemId, CancellationToken cancellationToken = default)
        => ExecuteAsync(operationId, workspaceId, collectionId, "RelationReplacementChain", itemId,
            token => QueryFileReplacementChainAsync(workspaceId, collectionId, itemId, token),
            token => _postgresRelationStore.QueryReplacementChainRelationsAsync(workspaceId, collectionId, itemId, token),
            cancellationToken);

    public Task<RelationReviewRecord?> GetLatestReviewAsync(string operationId, string workspaceId, string collectionId, string relationId, CancellationToken cancellationToken = default)
        => ExecuteAsync(operationId, workspaceId, collectionId, "RelationReviewLatest", relationId,
            token => _fileReviewStore.GetLatestReviewAsync(relationId, token),
            token => _postgresReviewStore.GetLatestReviewAsync(relationId, token),
            cancellationToken);

    public Task<IReadOnlyList<RelationReviewRecord>> QueryReviewsAsync(string operationId, string workspaceId, string collectionId, string relationId, CancellationToken cancellationToken = default)
        => ExecuteAsync(operationId, workspaceId, collectionId, "RelationReviewList", relationId,
            token => _fileReviewStore.QueryReviewsAsync(relationId, token),
            token => _postgresReviewStore.QueryReviewsAsync(relationId, token),
            cancellationToken);

    public Task<IReadOnlyList<RelationReviewRecord>> QueryReviewsByStatusAsync(string operationId, string workspaceId, string collectionId, string reviewStatus, CancellationToken cancellationToken = default)
        => ExecuteAsync(operationId, workspaceId, collectionId, "RelationReviewByStatus", reviewStatus,
            token => _fileReviewStore.QueryByReviewStatusAsync(workspaceId, collectionId, reviewStatus, token),
            token => _postgresReviewStore.QueryByReviewStatusAsync(workspaceId, collectionId, reviewStatus, token),
            cancellationToken);

    public Task<IReadOnlyList<RelationDiagnosticsSnapshot>> QueryDiagnosticsByRelationAsync(string operationId, string workspaceId, string collectionId, string relationId, CancellationToken cancellationToken = default)
        => ExecuteAsync(operationId, workspaceId, collectionId, "RelationDiagnosticsByRelation", relationId,
            token => _fileDiagnosticsStore.QueryByRelationAsync(workspaceId, collectionId, relationId, token),
            token => _postgresDiagnosticsStore.QueryByRelationAsync(workspaceId, collectionId, relationId, token),
            cancellationToken);

    public Task<IReadOnlyList<RelationDiagnosticsSnapshot>> QueryDiagnosticsByItemAsync(string operationId, string workspaceId, string collectionId, string itemId, CancellationToken cancellationToken = default)
        => ExecuteAsync(operationId, workspaceId, collectionId, "RelationDiagnosticsByItem", itemId,
            token => _fileDiagnosticsStore.QueryByItemAsync(workspaceId, collectionId, itemId, token),
            token => _postgresDiagnosticsStore.QueryByItemAsync(workspaceId, collectionId, itemId, token),
            cancellationToken);

    public Task<IReadOnlyList<RelationDiagnosticsSnapshot>> QueryDiagnosticsByKindAsync(string operationId, string workspaceId, string collectionId, string kind, CancellationToken cancellationToken = default)
        => ExecuteAsync(operationId, workspaceId, collectionId, "RelationDiagnosticsByKind", kind,
            token => _fileDiagnosticsStore.QueryByKindAsync(workspaceId, collectionId, kind, token),
            token => _postgresDiagnosticsStore.QueryByKindAsync(workspaceId, collectionId, kind, token),
            cancellationToken);

    public Task<IReadOnlyList<RelationDiagnosticsSnapshot>> QueryDiagnosticsBySeverityAsync(string operationId, string workspaceId, string collectionId, string severity, CancellationToken cancellationToken = default)
        => ExecuteAsync(operationId, workspaceId, collectionId, "RelationDiagnosticsBySeverity", severity,
            token => _fileDiagnosticsStore.QueryBySeverityAsync(workspaceId, collectionId, severity, token),
            token => _postgresDiagnosticsStore.QueryBySeverityAsync(workspaceId, collectionId, severity, token),
            cancellationToken);

    private async Task<IReadOnlyList<ContextRelation>> QueryFileRelationByMetadataAsync(string workspaceId, string collectionId, string key, string value, CancellationToken cancellationToken)
    {
        var relations = await _fileRelationStore.QueryAsync(new ContextRelationQuery { WorkspaceId = workspaceId, CollectionId = collectionId, Take = int.MaxValue }, cancellationToken).ConfigureAwait(false);
        return [.. relations.Where(relation => relation.Metadata.TryGetValue(key, out var metadataValue)
            && string.Equals(metadataValue, value, StringComparison.OrdinalIgnoreCase))];
    }

    private async Task<IReadOnlyList<ContextRelation>> QueryFileReplacementChainAsync(string workspaceId, string collectionId, string itemId, CancellationToken cancellationToken)
    {
        var relations = await _fileRelationStore.QueryForItemAsync(workspaceId, collectionId, itemId, cancellationToken).ConfigureAwait(false);
        return [.. relations.Where(static relation =>
            string.Equals(relation.RelationType, ContextRelationTypes.SupersededBy, StringComparison.OrdinalIgnoreCase)
            || string.Equals(relation.RelationType, ContextRelationTypes.Replaces, StringComparison.OrdinalIgnoreCase)
            || string.Equals(relation.RelationType, ContextRelationTypes.ReplacedBy, StringComparison.OrdinalIgnoreCase))];
    }

    private async Task<T> ExecuteAsync<T>(
        string operationId,
        string workspaceId,
        string collectionId,
        string readKind,
        string targetId,
        Func<CancellationToken, Task<T>> fileRead,
        Func<CancellationToken, Task<T>> postgresRead,
        CancellationToken cancellationToken)
    {
        var fileStopwatch = Stopwatch.StartNew();
        var fileResult = await fileRead(cancellationToken).ConfigureAwait(false);
        fileStopwatch.Stop();
        var fileHash = ComputeStableHash(fileResult);

        var postgresStopwatch = new Stopwatch();
        var postgresSucceeded = false;
        var postgresHash = string.Empty;
        var postgresError = string.Empty;
        var fallbackUsed = false;

        if (_options.Enabled && _options.ReadPostgres)
        {
            try
            {
                postgresStopwatch.Start();
                var postgresResult = await postgresRead(cancellationToken).ConfigureAwait(false);
                postgresStopwatch.Stop();
                postgresHash = ComputeStableHash(postgresResult);
                postgresSucceeded = true;
            }
            catch (Exception ex)
            {
                if (postgresStopwatch.IsRunning)
                {
                    postgresStopwatch.Stop();
                }

                fallbackUsed = true;
                postgresError = ex.GetType().Name;
            }
        }

        var mismatch = _options.Enabled
            && _options.ReadPostgres
            && _options.CompareResults
            && (!postgresSucceeded || !string.Equals(fileHash, postgresHash, StringComparison.Ordinal));
        var trace = new RelationGovernanceShadowReadTrace
        {
            OperationId = operationId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            ReadKind = readKind,
            TargetId = targetId,
            FileSystemReadSucceeded = true,
            PostgresReadSucceeded = postgresSucceeded,
            FileSystemResultHash = fileHash,
            PostgresResultHash = postgresHash,
            MismatchDetected = mismatch,
            MismatchReason = mismatch
                ? postgresSucceeded ? "ResultHashMismatch" : "PostgresReadFailed"
                : string.Empty,
            PostgresError = postgresError,
            FallbackUsed = fallbackUsed,
            FileSystemDurationMs = fileStopwatch.Elapsed.TotalMilliseconds,
            PostgresDurationMs = postgresStopwatch.Elapsed.TotalMilliseconds,
            CreatedAt = DateTimeOffset.UtcNow
        };

        if (_options.TraceEnabled)
        {
            await _traceSink(trace, cancellationToken).ConfigureAwait(false);
        }

        if (trace.MismatchDetected && _options.FailOnMismatch)
        {
            throw new InvalidOperationException($"Relation shadow-read mismatch: {trace.MismatchReason}");
        }

        return fileResult;
    }

    public static string ComputeStableHash<T>(T value)
    {
        var canonical = Canonicalize(value);
        var json = JsonSerializer.Serialize(canonical, HashJsonOptions);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static object? Canonicalize(object? value)
    {
        return value switch
        {
            null => null,
            ContextRelation relation => new
            {
                relation.Id,
                relation.WorkspaceId,
                relation.CollectionId,
                relation.SourceId,
                relation.TargetId,
                relation.RelationType,
                relation.Weight,
                relation.Confidence,
                relation.CreatedAt,
                SourceRefs = relation.SourceRefs.Order(StringComparer.Ordinal).ToArray(),
                Metadata = relation.Metadata.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase).ToArray()
            },
            RelationReviewRecord review => new
            {
                review.ReviewId,
                review.RelationId,
                review.WorkspaceId,
                review.CollectionId,
                review.Action,
                review.FromLifecycle,
                review.ToLifecycle,
                review.FromReviewStatus,
                review.ToReviewStatus,
                review.Reviewer,
                review.Reason,
                review.CreatedAt,
                review.ReviewedAt,
                Metadata = review.Metadata.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase).ToArray()
            },
            RelationDiagnosticsSnapshot diagnostic => new
            {
                diagnostic.DiagnosticId,
                diagnostic.WorkspaceId,
                diagnostic.CollectionId,
                diagnostic.RelationId,
                diagnostic.ItemId,
                diagnostic.DiagnosticKind,
                diagnostic.Severity,
                diagnostic.Message,
                diagnostic.CreatedAt,
                Metadata = diagnostic.Metadata.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase).ToArray()
            },
            IEnumerable<ContextRelation> relations => relations
                .OrderBy(static item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Select(Canonicalize)
                .ToArray(),
            IEnumerable<RelationReviewRecord> reviews => reviews
                .OrderBy(static item => item.ReviewId, StringComparer.OrdinalIgnoreCase)
                .Select(Canonicalize)
                .ToArray(),
            IEnumerable<RelationDiagnosticsSnapshot> diagnostics => diagnostics
                .OrderBy(static item => item.DiagnosticId, StringComparer.OrdinalIgnoreCase)
                .Select(Canonicalize)
                .ToArray(),
            _ => value
        };
    }
}
