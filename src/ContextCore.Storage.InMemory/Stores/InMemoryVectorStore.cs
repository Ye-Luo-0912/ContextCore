using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.InMemory;

/// <summary>基于内存的向量存储，适用于测试、Demo 和短生命周期运行。</summary>
public sealed class InMemoryVectorStore : IVectorStore, IVectorStoreMultiSearch
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
        var excludedSourceIds = query.ExcludeSourceIds.Count == 0
            ? null
            : new HashSet<string>(query.ExcludeSourceIds, StringComparer.OrdinalIgnoreCase);
        var topK = query.TopK > 0 ? query.TopK : 10;

        var results = _records.Values
            .Where(record => string.Equals(record.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            .Where(record => string.IsNullOrWhiteSpace(query.CollectionId)
                || string.Equals(record.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            .Where(record => sourceKinds.Count == 0 || sourceKinds.Contains(record.SourceKind))
            .Where(record => tags.Count == 0 || tags.All(record.Tags.Contains))
            // 本次查询不返回的来源 ID 在排序/截断前排除，避免已持有 ID 占满 TopK。
            .Where(record => excludedSourceIds is null || !excludedSourceIds.Contains(record.SourceId))
            .Select(record => new
            {
                Record = record,
                Score = Cosine(query.Vector, record.Vector)
            })
            .Where(item => query.MinScore is null || item.Score >= query.MinScore.Value)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Record.UpdatedAt)
            // 确定性 tie-break — 同 Score/UpdatedAt 的命中按 SourceId 升序，
            // 避免 topK 截断时依赖字典枚举顺序导致向量检索结果不稳定。
            .ThenBy(item => item.Record.SourceId, StringComparer.OrdinalIgnoreCase)
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

    /// <summary>
    /// 多问句向量检索：单次枚举完成全部问句，避免 q 次 SearchAsync 各自枚举。
    /// 共享过滤（作用域/来源类型/tags/排除）只评估一次；每条问句独立计算余弦并保留各自 TopK，
    /// 语义与逐条 SearchAsync 完全一致（含 MinScore 过滤时机与确定性 tie-break）。
    /// </summary>
    public Task<IReadOnlyList<VectorMultiSearchResult>> SearchMultiAsync(
        VectorMultiQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        if (query.Queries.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<VectorMultiSearchResult>>(Array.Empty<VectorMultiSearchResult>());
        }

        var tags = query.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceKinds = query.SourceKinds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excludedSourceIds = query.ExcludeSourceIds.Count == 0
            ? null
            : new HashSet<string>(query.ExcludeSourceIds, StringComparer.OrdinalIgnoreCase);
        var topK = query.TopK > 0 ? query.TopK : 10;

        // 单次枚举：共享过滤只评估一次，按问句再做相似度计算与各自 TopK。
        var candidates = _records.Values
            .Where(record => string.Equals(record.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            .Where(record => string.IsNullOrWhiteSpace(query.CollectionId)
                || string.Equals(record.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            .Where(record => sourceKinds.Count == 0 || sourceKinds.Contains(record.SourceKind))
            .Where(record => tags.Count == 0 || tags.All(record.Tags.Contains))
            .Where(record => excludedSourceIds is null || !excludedSourceIds.Contains(record.SourceId))
            .ToArray();

        var results = new List<VectorMultiSearchResult>(query.Queries.Count);
        foreach (var q in query.Queries)
        {
            var hits = candidates
                .Select(record => new
                {
                    Record = record,
                    Score = Cosine(q.Vector, record.Vector)
                })
                .Where(item => query.MinScore is null || item.Score >= query.MinScore.Value)
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Record.UpdatedAt)
                .ThenBy(item => item.Record.SourceId, StringComparer.OrdinalIgnoreCase)
                .Take(topK)
                .Select((item, index) => new VectorSearchResult
                {
                    Record = Clone(item.Record, includeVector: query.IncludeVector),
                    Score = item.Score,
                    Rank = index + 1
                })
                .ToArray();
            results.Add(new VectorMultiSearchResult { QueryId = q.Id, Hits = hits });
        }

        return Task.FromResult<IReadOnlyList<VectorMultiSearchResult>>(results);
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
