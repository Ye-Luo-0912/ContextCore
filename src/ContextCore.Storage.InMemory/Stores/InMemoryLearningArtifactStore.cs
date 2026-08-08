using ContextCore.Abstractions;

namespace ContextCore.Storage.InMemory.Stores;

/// <summary>基于内存的 Learning Artifact 存储（数据集快照工件），适用于测试与单节点。</summary>
public sealed class InMemoryLearningArtifactStore : ILearningArtifactStore
{
    private readonly Dictionary<string, DatasetSnapshotArtifact> _artifacts = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public ValueTask SaveAsync(
        DatasetSnapshotArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        cancellationToken.ThrowIfCancellationRequested();

        var key = Key(artifact.Snapshot.WorkspaceId, artifact.Snapshot.SnapshotId);
        lock (_gate)
        {
            _artifacts[key] = artifact;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<DatasetSnapshotArtifact?> GetAsync(
        string workspaceId,
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return ValueTask.FromResult(
                _artifacts.TryGetValue(Key(workspaceId, snapshotId), out var artifact)
                    ? artifact
                    : null);
        }
    }

    public ValueTask<IReadOnlyList<DatasetSnapshotArtifact>> ListRecentAsync(
        string workspaceId,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = take > 0 ? take : 20;

        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<DatasetSnapshotArtifact>>(_artifacts.Values
                .Where(a => string.Equals(a.Snapshot.WorkspaceId, workspaceId, StringComparison.Ordinal))
                .OrderByDescending(a => a.StoredAt)
                .Take(count)
                .ToArray());
        }
    }

    private static string Key(string workspaceId, string snapshotId)
        => $"{workspaceId}\u001f{snapshotId}";
}
