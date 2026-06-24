using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>基于 JSONL 文件的向量存储，提供轻量本地相似度检索。</summary>
public sealed class FileVectorStore : IVectorStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FileJsonLineStore _jsonLines;
    private readonly FilePathResolver _paths;

    public FileVectorStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileVectorStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
    }

    public async Task UpsertAsync(VectorRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = Normalize(record);
        var path = _paths.GetVectorsJsonlPath(
            normalized.WorkspaceId,
            normalized.CollectionId ?? string.Empty);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _jsonLines.UpsertAsync(path, normalized, item => item.Id, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VectorRecord?> GetAsync(
        string workspaceId,
        string vectorId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var path in ResolveVectorPaths(workspaceId, null))
            {
                var records = await _jsonLines.ReadAsync<VectorRecord>(path, cancellationToken)
                    .ConfigureAwait(false);
                var record = records.FirstOrDefault(item => string.Equals(item.Id, vectorId, StringComparison.OrdinalIgnoreCase));
                if (record is not null)
                {
                    return Clone(record);
                }
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        VectorQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = new List<VectorRecord>();
            foreach (var path in ResolveVectorPaths(query.WorkspaceId, query.CollectionId))
            {
                records.AddRange(await _jsonLines.ReadAsync<VectorRecord>(path, cancellationToken)
                    .ConfigureAwait(false));
            }

            var tags = query.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sourceKinds = query.SourceKinds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var topK = query.TopK > 0 ? query.TopK : 10;

            return [.. records
                .Where(record => string.Equals(record.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase))
                .Where(record => string.IsNullOrWhiteSpace(query.CollectionId)
                    || string.Equals(record.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
                .Where(record => sourceKinds.Count == 0 || sourceKinds.Contains(record.SourceKind))
                .Where(record => tags.Count == 0 || tags.All(record.Tags.Contains))
                .Select(record => new
                {
                    Record = record,
                    Score = Cosine(query.Vector, record.Vector)
                })
                .Where(item => query.MinScore is null || item.Score >= query.MinScore.Value)
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Record.UpdatedAt)
                .Take(topK)
                .Select((item, index) => new VectorSearchResult
                {
                    Record = Clone(item.Record, includeVector: query.IncludeVector),
                    Score = item.Score,
                    Rank = index + 1
                })];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(
        string workspaceId,
        string vectorId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var path in ResolveVectorPaths(workspaceId, null))
            {
                var records = await _jsonLines.ReadAsync<VectorRecord>(path, cancellationToken)
                    .ConfigureAwait(false);
                var updated = records
                    .Where(item => !string.Equals(item.Id, vectorId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (updated.Length != records.Count)
                {
                    await _jsonLines.WriteAsync(path, updated, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private IEnumerable<string> ResolveVectorPaths(string workspaceId, string? collectionId)
    {
        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            yield return _paths.GetVectorsJsonlPath(workspaceId, collectionId);
            yield break;
        }

        var collectionsDirectory = _paths.GetCollectionsDirectory(workspaceId);
        if (!Directory.Exists(collectionsDirectory))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(collectionsDirectory))
        {
            var id = Path.GetFileName(directory);
            if (!string.IsNullOrWhiteSpace(id))
            {
                yield return _paths.GetVectorsJsonlPath(workspaceId, id);
            }
        }
    }

    private static VectorRecord Normalize(VectorRecord record)
    {
        var now = DateTimeOffset.UtcNow;
        return new VectorRecord
        {
            Id = string.IsNullOrWhiteSpace(record.Id) ? Guid.NewGuid().ToString("N") : record.Id,
            WorkspaceId = record.WorkspaceId,
            CollectionId = record.CollectionId,
            SourceId = record.SourceId,
            SourceKind = record.SourceKind,
            ModelName = record.ModelName,
            Dimensions = record.Dimensions > 0 ? record.Dimensions : record.Vector.Count,
            Vector = record.Vector.ToArray(),
            ContentHash = record.ContentHash,
            Tags = record.Tags.ToArray(),
            Metadata = new Dictionary<string, string>(record.Metadata),
            CreatedAt = record.CreatedAt == default ? now : record.CreatedAt,
            UpdatedAt = record.UpdatedAt == default ? now : record.UpdatedAt
        };
    }

    private static VectorRecord Clone(VectorRecord record, bool includeVector = true)
    {
        return new VectorRecord
        {
            Id = record.Id,
            WorkspaceId = record.WorkspaceId,
            CollectionId = record.CollectionId,
            SourceId = record.SourceId,
            SourceKind = record.SourceKind,
            ModelName = record.ModelName,
            Dimensions = record.Dimensions,
            Vector = includeVector ? record.Vector.ToArray() : Array.Empty<float>(),
            ContentHash = record.ContentHash,
            Tags = record.Tags.ToArray(),
            Metadata = new Dictionary<string, string>(record.Metadata),
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt
        };
    }

    private static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        var length = Math.Min(left.Count, right.Count);
        if (length == 0)
        {
            return 0;
        }

        var dot = 0.0;
        var leftNorm = 0.0;
        var rightNorm = 0.0;
        for (var i = 0; i < length; i++)
        {
            dot += left[i] * right[i];
            leftNorm += left[i] * left[i];
            rightNorm += right[i] * right[i];
        }

        return leftNorm <= 0 || rightNorm <= 0
            ? 0
            : dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
    }
}
