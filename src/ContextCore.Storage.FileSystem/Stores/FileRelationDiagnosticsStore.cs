using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>基于文件系统的 relation diagnostics 投影存储；用于报告/parity，不参与 runtime 决策。</summary>
public sealed class FileRelationDiagnosticsStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;

    public FileRelationDiagnosticsStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
    }

    public async Task WriteAsync(
        RelationDiagnosticsSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var normalized = Normalize(snapshot);
        if (string.IsNullOrWhiteSpace(normalized.CollectionId))
        {
            throw new ArgumentException("Relation diagnostics 必须包含 collectionId。", nameof(snapshot));
        }

        var path = GetPath(normalized.WorkspaceId, normalized.CollectionId!);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _jsonLines.UpsertAsync(path, normalized, static item => item.DiagnosticId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<IReadOnlyList<RelationDiagnosticsSnapshot>> QueryByScopeAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        return QueryAsync(workspaceId, collectionId, static _ => true, cancellationToken);
    }

    public Task<IReadOnlyList<RelationDiagnosticsSnapshot>> QueryByRelationAsync(
        string workspaceId,
        string collectionId,
        string relationId,
        CancellationToken cancellationToken = default)
    {
        return QueryAsync(
            workspaceId,
            collectionId,
            item => string.Equals(item.RelationId, relationId, StringComparison.OrdinalIgnoreCase),
            cancellationToken);
    }

    public Task<IReadOnlyList<RelationDiagnosticsSnapshot>> QueryByItemAsync(
        string workspaceId,
        string collectionId,
        string itemId,
        CancellationToken cancellationToken = default)
    {
        return QueryAsync(
            workspaceId,
            collectionId,
            item => string.Equals(item.ItemId, itemId, StringComparison.OrdinalIgnoreCase),
            cancellationToken);
    }

    public Task<IReadOnlyList<RelationDiagnosticsSnapshot>> QueryByKindAsync(
        string workspaceId,
        string collectionId,
        string diagnosticKind,
        CancellationToken cancellationToken = default)
    {
        return QueryAsync(
            workspaceId,
            collectionId,
            item => string.Equals(item.DiagnosticKind, diagnosticKind, StringComparison.OrdinalIgnoreCase),
            cancellationToken);
    }

    public Task<IReadOnlyList<RelationDiagnosticsSnapshot>> QueryBySeverityAsync(
        string workspaceId,
        string collectionId,
        string severity,
        CancellationToken cancellationToken = default)
    {
        return QueryAsync(
            workspaceId,
            collectionId,
            item => string.Equals(item.Severity, severity, StringComparison.OrdinalIgnoreCase),
            cancellationToken);
    }

    private async Task<IReadOnlyList<RelationDiagnosticsSnapshot>> QueryAsync(
        string workspaceId,
        string collectionId,
        Func<RelationDiagnosticsSnapshot, bool> predicate,
        CancellationToken cancellationToken)
    {
        var path = GetPath(workspaceId, collectionId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshots = await _jsonLines.ReadAsync<RelationDiagnosticsSnapshot>(path, cancellationToken)
                .ConfigureAwait(false);
            return
            [
                .. snapshots
                    .Where(predicate)
                    .OrderByDescending(static item => item.CreatedAt)
                    .Select(Normalize)
            ];
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetPath(string workspaceId, string collectionId)
    {
        return Path.Combine(
            _paths.GetCollectionDirectory(workspaceId, collectionId),
            "relations",
            "diagnostics.jsonl");
    }

    private static RelationDiagnosticsSnapshot Normalize(RelationDiagnosticsSnapshot snapshot)
    {
        return new RelationDiagnosticsSnapshot
        {
            DiagnosticId = string.IsNullOrWhiteSpace(snapshot.DiagnosticId) ? Guid.NewGuid().ToString("N") : snapshot.DiagnosticId,
            WorkspaceId = snapshot.WorkspaceId,
            CollectionId = snapshot.CollectionId,
            RelationId = snapshot.RelationId,
            ItemId = snapshot.ItemId,
            DiagnosticKind = snapshot.DiagnosticKind,
            Severity = snapshot.Severity,
            Message = snapshot.Message,
            CreatedAt = snapshot.CreatedAt == default ? DateTimeOffset.UtcNow : snapshot.CreatedAt,
            Metadata = new Dictionary<string, string>(snapshot.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }
}
