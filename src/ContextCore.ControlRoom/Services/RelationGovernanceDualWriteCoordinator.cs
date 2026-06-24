using System.Diagnostics;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.ControlRoom.Services;

/// <summary>Relation governance 双写协调器；FileSystem 始终是 source of truth。</summary>
public sealed class RelationGovernanceDualWriteCoordinator
{
    private readonly FileRelationStore _fileRelationStore;
    private readonly FileRelationReviewStore _fileReviewStore;
    private readonly FileRelationDiagnosticsStore _fileDiagnosticsStore;
    private readonly PostgresRelationStore _postgresRelationStore;
    private readonly PostgresRelationReviewStore _postgresReviewStore;
    private readonly PostgresRelationDiagnosticsStore _postgresDiagnosticsStore;
    private readonly RelationGovernanceDualWriteOptions _options;
    private readonly Func<RelationGovernanceDualWriteTrace, CancellationToken, Task> _traceSink;

    public RelationGovernanceDualWriteCoordinator(
        FileRelationStore fileRelationStore,
        FileRelationReviewStore fileReviewStore,
        FileRelationDiagnosticsStore fileDiagnosticsStore,
        PostgresRelationStore postgresRelationStore,
        PostgresRelationReviewStore postgresReviewStore,
        PostgresRelationDiagnosticsStore postgresDiagnosticsStore,
        RelationGovernanceDualWriteOptions options,
        Func<RelationGovernanceDualWriteTrace, CancellationToken, Task> traceSink)
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

    public Task<RelationGovernanceDualWriteTrace> UpsertRelationAsync(
        string operationId,
        ContextRelation relation,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            operationId,
            relation.WorkspaceId,
            relation.CollectionId,
            "Relation",
            relation.Id,
            fileWrite: token => _fileRelationStore.SaveAsync(relation, token),
            postgresWrite: token => _postgresRelationStore.SaveAsync(relation, token),
            cancellationToken);

    public Task<RelationGovernanceDualWriteTrace> DeleteRelationAsync(
        string operationId,
        string workspaceId,
        string collectionId,
        string relationId,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            operationId,
            workspaceId,
            collectionId,
            "RelationDelete",
            relationId,
            fileWrite: async token => { await _fileRelationStore.DeleteAsync(workspaceId, collectionId, relationId, token).ConfigureAwait(false); },
            postgresWrite: async token => { await _postgresRelationStore.DeleteAsync(workspaceId, collectionId, relationId, token).ConfigureAwait(false); },
            cancellationToken);

    public Task<RelationGovernanceDualWriteTrace> AppendReviewAsync(
        string operationId,
        RelationReviewRecord review,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            operationId,
            review.WorkspaceId,
            review.CollectionId ?? string.Empty,
            "RelationReview",
            review.ReviewId,
            fileWrite: token => _fileReviewStore.AppendReviewAsync(review, token),
            postgresWrite: token => _postgresReviewStore.AppendReviewAsync(review, token),
            cancellationToken);

    public Task<RelationGovernanceDualWriteTrace> WriteDiagnosticsAsync(
        string operationId,
        RelationDiagnosticsSnapshot snapshot,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            operationId,
            snapshot.WorkspaceId,
            snapshot.CollectionId ?? string.Empty,
            "RelationDiagnostics",
            snapshot.DiagnosticId,
            fileWrite: token => _fileDiagnosticsStore.WriteAsync(snapshot, token),
            postgresWrite: token => _postgresDiagnosticsStore.WriteAsync(snapshot, token),
            cancellationToken);

    private async Task<RelationGovernanceDualWriteTrace> ExecuteAsync(
        string operationId,
        string workspaceId,
        string collectionId,
        string targetKind,
        string targetId,
        Func<CancellationToken, Task> fileWrite,
        Func<CancellationToken, Task> postgresWrite,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var fileSucceeded = false;
        var postgresSucceeded = false;
        var fallbackUsed = false;
        var postgresError = string.Empty;

        try
        {
            await fileWrite(cancellationToken).ConfigureAwait(false);
            fileSucceeded = true;
        }
        catch
        {
            stopwatch.Stop();
            throw;
        }

        if (_options.Enabled && _options.WritePostgres)
        {
            try
            {
                await postgresWrite(cancellationToken).ConfigureAwait(false);
                postgresSucceeded = true;
            }
            catch (Exception ex) when (_options.FallbackOnPostgresFailure)
            {
                fallbackUsed = true;
                postgresError = ex.GetType().Name;
            }
        }

        stopwatch.Stop();
        var trace = new RelationGovernanceDualWriteTrace
        {
            OperationId = operationId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            TargetKind = targetKind,
            TargetId = targetId,
            FileSystemWriteSucceeded = fileSucceeded,
            PostgresWriteSucceeded = postgresSucceeded,
            MismatchDetected = fileSucceeded && _options.Enabled && _options.WritePostgres && !postgresSucceeded,
            MismatchReason = fileSucceeded && _options.Enabled && _options.WritePostgres && !postgresSucceeded
                ? "PostgresWriteFailed"
                : string.Empty,
            PostgresError = postgresError,
            FallbackUsed = fallbackUsed,
            DurationMs = stopwatch.Elapsed.TotalMilliseconds,
            CreatedAt = DateTimeOffset.UtcNow
        };

        if (_options.TraceEnabled)
        {
            await _traceSink(trace, cancellationToken).ConfigureAwait(false);
        }

        if (trace.MismatchDetected && _options.FailOnMismatch)
        {
            throw new InvalidOperationException($"Relation dual-write mismatch: {trace.MismatchReason}");
        }

        return trace;
    }
}
