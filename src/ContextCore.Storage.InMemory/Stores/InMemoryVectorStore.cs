using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Storage.InMemory;

/// <summary>基于内存的向量存储，适用于测试、Demo 和短生命周期运行。</summary>
public sealed class InMemoryVectorStore : IVectorStore
{
    private readonly ConcurrentDictionary<string, VectorRecord> _records = new();

    public Task UpsertAsync(VectorRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = Normalize(record);
        _records[Key(normalized.WorkspaceId, normalized.Id)] = normalized;
        return Task.CompletedTask;
    }

    public Task<VectorRecord?> GetAsync(
        string workspaceId,
        string vectorId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_records.TryGetValue(Key(workspaceId, vectorId), out var record)
            ? Clone(record)
            : null);
    }

    public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        VectorQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var tags = query.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceKinds = query.SourceKinds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var topK = query.TopK > 0 ? query.TopK : 10;

        var results = _records.Values
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
            })
            .ToArray();

        return Task.FromResult<IReadOnlyList<VectorSearchResult>>(results);
    }

    public Task DeleteAsync(
        string workspaceId,
        string vectorId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _records.TryRemove(Key(workspaceId, vectorId), out _);
        return Task.CompletedTask;
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

    private static string Key(string workspaceId, string id)
    {
        return $"{workspaceId}\u001f{id}";
    }
}
