using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>V1 vector index 的文件系统实现，使用独立 JSONL 文件避免污染旧向量存储。</summary>
public sealed class FileVectorIndexStore : IVectorIndexStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FileJsonLineStore _jsonLines;
    private readonly FilePathResolver _paths;

    public FileVectorIndexStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileVectorIndexStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
    }

    public async Task UpsertAsync(VectorIndexEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var normalized = Normalize(entry);
        var path = _paths.GetVectorIndexJsonlPath(normalized.WorkspaceId, normalized.CollectionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _jsonLines.UpsertAsync(path, normalized, item => item.EntryId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(
        string workspaceId,
        string collectionId,
        string entryId,
        CancellationToken cancellationToken = default)
    {
        var path = _paths.GetVectorIndexJsonlPath(workspaceId, collectionId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await _jsonLines.ReadAsync<VectorIndexEntry>(path, cancellationToken)
                .ConfigureAwait(false);
            var updated = entries
                .Where(entry => !string.Equals(entry.EntryId, entryId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (updated.Length != entries.Count)
            {
                await _jsonLines.WriteAsync(path, updated, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<VectorIndexEntry>> GetByItemIdAsync(
        string workspaceId,
        string collectionId,
        string itemId,
        CancellationToken cancellationToken = default)
    {
        var entries = await ReadEntriesAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
        return entries
            .Where(entry => string.Equals(entry.ItemId, itemId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.UpdatedAt)
            .Select(entry => Clone(entry))
            .ToArray();
    }

    public async Task<IReadOnlyList<VectorIndexEntry>> ListAsync(
        VectorIndexQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var entries = await ReadEntriesAsync(query.WorkspaceId, query.CollectionId, cancellationToken)
            .ConfigureAwait(false);
        var take = query.Take > 0 ? query.Take : 100;

        return entries
            .Where(entry => string.IsNullOrWhiteSpace(query.ItemKind)
                || string.Equals(entry.ItemKind, query.ItemKind, StringComparison.OrdinalIgnoreCase))
            .Where(entry => string.IsNullOrWhiteSpace(query.Layer)
                || string.Equals(entry.Layer, query.Layer, StringComparison.OrdinalIgnoreCase))
            .Where(entry => string.IsNullOrWhiteSpace(query.EmbeddingModel)
                || string.Equals(entry.EmbeddingModel, query.EmbeddingModel, StringComparison.OrdinalIgnoreCase))
            .Where(entry => string.IsNullOrWhiteSpace(query.EmbeddingProvider)
                || string.Equals(entry.EmbeddingProvider, query.EmbeddingProvider, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.ItemId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.EntryId, StringComparer.OrdinalIgnoreCase)
            .Skip(Math.Max(0, query.Skip))
            .Take(take)
            .Select(entry => Clone(entry, query.IncludeVector))
            .ToArray();
    }

    public async Task<IReadOnlyList<VectorIndexSearchResult>> SearchAsync(
        VectorIndexSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var entries = await ReadEntriesAsync(query.WorkspaceId, query.CollectionId, cancellationToken)
            .ConfigureAwait(false);
        var topK = query.TopK > 0 ? query.TopK : 10;

        return entries
            .Where(entry => string.IsNullOrWhiteSpace(query.EmbeddingProvider)
                || string.Equals(entry.EmbeddingProvider, query.EmbeddingProvider, StringComparison.OrdinalIgnoreCase))
            .Where(entry => string.IsNullOrWhiteSpace(query.EmbeddingModel)
                || string.Equals(entry.EmbeddingModel, query.EmbeddingModel, StringComparison.OrdinalIgnoreCase))
            .Select(entry => new
            {
                Entry = entry,
                Score = Cosine(query.Vector, entry.Vector)
            })
            .Where(item => query.MinScore is null || item.Score >= query.MinScore.Value)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Entry.UpdatedAt)
            .Take(topK)
            .Select((item, index) => new VectorIndexSearchResult
            {
                Entry = Clone(item.Entry, query.IncludeVector),
                Score = item.Score,
                Rank = index + 1
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<VectorIndexDiagnostic>> GetDiagnosticsAsync(
        VectorIndexQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var entries = await ReadEntriesAsync(query.WorkspaceId, query.CollectionId, cancellationToken)
            .ConfigureAwait(false);
        var filtered = entries
            .Where(entry => string.IsNullOrWhiteSpace(query.ItemKind)
                || string.Equals(entry.ItemKind, query.ItemKind, StringComparison.OrdinalIgnoreCase))
            .Where(entry => string.IsNullOrWhiteSpace(query.Layer)
                || string.Equals(entry.Layer, query.Layer, StringComparison.OrdinalIgnoreCase))
            .Where(entry => string.IsNullOrWhiteSpace(query.EmbeddingModel)
                || string.Equals(entry.EmbeddingModel, query.EmbeddingModel, StringComparison.OrdinalIgnoreCase))
            .Where(entry => string.IsNullOrWhiteSpace(query.EmbeddingProvider)
                || string.Equals(entry.EmbeddingProvider, query.EmbeddingProvider, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return BuildStoreDiagnostics(filtered);
    }

    private async Task<IReadOnlyList<VectorIndexEntry>> ReadEntriesAsync(
        string workspaceId,
        string? collectionId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = new List<VectorIndexEntry>();
            foreach (var path in ResolveVectorIndexPaths(workspaceId, collectionId))
            {
                entries.AddRange(await _jsonLines.ReadAsync<VectorIndexEntry>(path, cancellationToken)
                    .ConfigureAwait(false));
            }

            return entries
                .Where(entry => string.Equals(entry.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
                .Where(entry => string.IsNullOrWhiteSpace(collectionId)
                    || string.Equals(entry.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private IEnumerable<string> ResolveVectorIndexPaths(string workspaceId, string? collectionId)
    {
        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            yield return _paths.GetVectorIndexJsonlPath(workspaceId, collectionId);
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
                yield return _paths.GetVectorIndexJsonlPath(workspaceId, id);
            }
        }
    }

    private static VectorIndexEntry Normalize(VectorIndexEntry entry)
    {
        var now = DateTimeOffset.UtcNow;
        return new VectorIndexEntry
        {
            EntryId = string.IsNullOrWhiteSpace(entry.EntryId) ? Guid.NewGuid().ToString("N") : entry.EntryId,
            ItemId = entry.ItemId,
            ItemKind = entry.ItemKind,
            Layer = entry.Layer,
            WorkspaceId = entry.WorkspaceId,
            CollectionId = entry.CollectionId,
            ContentHash = entry.ContentHash,
            EmbeddingModel = entry.EmbeddingModel,
            EmbeddingProvider = entry.EmbeddingProvider,
            Dimension = entry.Dimension > 0 ? entry.Dimension : entry.Vector.Count,
            Vector = entry.Vector.ToArray(),
            CreatedAt = entry.CreatedAt == default ? now : entry.CreatedAt,
            UpdatedAt = now,
            Metadata = new Dictionary<string, string>(entry.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static IReadOnlyList<VectorIndexDiagnostic> BuildStoreDiagnostics(IEnumerable<VectorIndexEntry> entries)
    {
        var records = entries.ToArray();
        var diagnostics = records
            .Where(entry => entry.Dimension != entry.Vector.Count)
            .Select(entry => NewDiagnostic(VectorIndexDiagnosticTypes.DimensionMismatch, entry, $"向量维度声明为 {entry.Dimension}，实际向量长度为 {entry.Vector.Count}。", "重新生成该条 embedding。")).ToList();

        diagnostics.AddRange(from @group in records.GroupBy(DuplicateKey, StringComparer.OrdinalIgnoreCase)
                .Where(@group => @group.Count() > 1)
            from entry in @group
            select NewDiagnostic(VectorIndexDiagnosticTypes.DuplicateVectorEntry, entry, "同一 item/model/provider 存在多条 vector index entry。", "保留最新 entry 并清理旧 entry，避免重复噪声。"));

        return diagnostics;
    }

    private static VectorIndexEntry Clone(VectorIndexEntry entry, bool includeVector = true)
    {
        return new VectorIndexEntry
        {
            EntryId = entry.EntryId,
            ItemId = entry.ItemId,
            ItemKind = entry.ItemKind,
            Layer = entry.Layer,
            WorkspaceId = entry.WorkspaceId,
            CollectionId = entry.CollectionId,
            ContentHash = entry.ContentHash,
            EmbeddingModel = entry.EmbeddingModel,
            EmbeddingProvider = entry.EmbeddingProvider,
            Dimension = entry.Dimension,
            Vector = includeVector ? entry.Vector.ToArray() : Array.Empty<float>(),
            CreatedAt = entry.CreatedAt,
            UpdatedAt = entry.UpdatedAt,
            Metadata = new Dictionary<string, string>(entry.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static VectorIndexDiagnostic NewDiagnostic(
        string type,
        VectorIndexEntry entry,
        string message,
        string suggestedAction)
    {
        return new VectorIndexDiagnostic
        {
            DiagnosticId = $"{type}:{entry.WorkspaceId}:{entry.CollectionId}:{entry.EntryId}",
            Type = type,
            Severity = "Warning",
            WorkspaceId = entry.WorkspaceId,
            CollectionId = entry.CollectionId,
            ItemId = entry.ItemId,
            EntryId = entry.EntryId,
            Message = message,
            SuggestedAction = suggestedAction,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["embeddingModel"] = entry.EmbeddingModel,
                ["embeddingProvider"] = entry.EmbeddingProvider
            }
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

    private static string DuplicateKey(VectorIndexEntry entry)
    {
        return $"{entry.WorkspaceId}\u001f{entry.CollectionId}\u001f{entry.ItemId}\u001f{entry.EmbeddingProvider}\u001f{entry.EmbeddingModel}";
    }
}
