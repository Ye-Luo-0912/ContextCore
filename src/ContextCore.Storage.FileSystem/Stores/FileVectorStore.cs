using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>基于 JSONL 文件的向量存储，提供轻量本地相似度检索。</summary>
public sealed class FileVectorStore : IVectorStore, IVectorStoreMultiSearch
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

        await _jsonLines.UpsertAsync(path, normalized, item => item.Id, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<VectorRecord?> GetAsync(
        string workspaceId,
        string vectorId,
        CancellationToken cancellationToken = default)
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

    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        VectorQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var records = new List<VectorRecord>();
        foreach (var path in ResolveVectorPaths(query.WorkspaceId, query.CollectionId))
        {
            records.AddRange(await _jsonLines.ReadAsync<VectorRecord>(path, cancellationToken)
                .ConfigureAwait(false));
        }

        var tags = query.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceKinds = query.SourceKinds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excludedSourceIds = query.ExcludeSourceIds.Count == 0
            ? null
            : new HashSet<string>(query.ExcludeSourceIds, StringComparer.OrdinalIgnoreCase);
        var topK = query.TopK > 0 ? query.TopK : 10;

        return [.. records
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
            // 避免 topK 截断时依赖文件行顺序导致向量检索结果不稳定。
            .ThenBy(item => item.Record.SourceId, StringComparer.OrdinalIgnoreCase)
            .Take(topK)
            .Select((item, index) => new VectorSearchResult
            {
                Record = Clone(item.Record, includeVector: query.IncludeVector),
                Score = item.Score,
                Rank = index + 1
            })];
    }

    /// <summary>
    /// 多问句向量检索：一次快照完成全部问句——全部向量文件只读一次，
    /// 共享过滤只评估一次；每条问句独立计算余弦并保留各自 TopK，
    /// 语义与逐条 SearchAsync 完全一致（含 MinScore 过滤时机与确定性 tie-break）。
    /// </summary>
    public async Task<IReadOnlyList<VectorMultiSearchResult>> SearchMultiAsync(
        VectorMultiQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Queries.Count == 0)
        {
            return Array.Empty<VectorMultiSearchResult>();
        }

        // 一次快照：全部问句共享同一批读取记录（单次文件 I/O，不再 q 次全量读）。
        var records = new List<VectorRecord>();
        foreach (var path in ResolveVectorPaths(query.WorkspaceId, query.CollectionId))
        {
            records.AddRange(await _jsonLines.ReadAsync<VectorRecord>(path, cancellationToken)
                .ConfigureAwait(false));
        }

        var tags = query.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceKinds = query.SourceKinds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excludedSourceIds = query.ExcludeSourceIds.Count == 0
            ? null
            : new HashSet<string>(query.ExcludeSourceIds, StringComparer.OrdinalIgnoreCase);
        var topK = query.TopK > 0 ? query.TopK : 10;

        var candidates = records
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

        return results;
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
                // TryUpdateAsync 在跨进程锁内读改写；未匹配到目标时返回 null 跳过写入，
                // 避免对未创建/未变更的 vectors.jsonl 创建空文件。
                await _jsonLines.TryUpdateAsync<VectorRecord>(
                    path,
                    existing =>
                    {
                        var updated = existing
                            .Where(item => !string.Equals(item.Id, vectorId, StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                        return updated.Length == existing.Count ? null : updated;
                    },
                    cancellationToken).ConfigureAwait(false);
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
