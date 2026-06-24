using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.InMemory;

/// <summary>V1 vector index 的内存实现，只用于基础设施、诊断和测试。</summary>
public sealed class InMemoryVectorIndexStore : IVectorIndexStore
{
    private readonly ConcurrentDictionary<string, VectorIndexEntry> _entries = new();

    public Task UpsertAsync(VectorIndexEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = Normalize(entry);
        _entries[Key(normalized.WorkspaceId, normalized.CollectionId, normalized.EntryId)] = normalized;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(
        string workspaceId,
        string collectionId,
        string entryId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _entries.TryRemove(Key(workspaceId, collectionId, entryId), out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VectorIndexEntry>> GetByItemIdAsync(
        string workspaceId,
        string collectionId,
        string itemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = _entries.Values
            .Where(entry => MatchesScope(entry, workspaceId, collectionId))
            .Where(entry => string.Equals(entry.ItemId, itemId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.UpdatedAt)
            .Select(entry => Clone(entry))
            .ToArray();

        return Task.FromResult<IReadOnlyList<VectorIndexEntry>>(results);
    }

    public Task<IReadOnlyList<VectorIndexEntry>> ListAsync(
        VectorIndexQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var take = query.Take > 0 ? query.Take : 100;
        var results = Filter(_entries.Values, query)
            .OrderBy(entry => entry.ItemId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.EntryId, StringComparer.OrdinalIgnoreCase)
            .Skip(Math.Max(0, query.Skip))
            .Take(take)
            .Select(entry => Clone(entry, query.IncludeVector))
            .ToArray();

        return Task.FromResult<IReadOnlyList<VectorIndexEntry>>(results);
    }

    public Task<IReadOnlyList<VectorIndexSearchResult>> SearchAsync(
        VectorIndexSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var topK = query.TopK > 0 ? query.TopK : 10;
        var results = _entries.Values
            .Where(entry => MatchesScope(entry, query.WorkspaceId, query.CollectionId))
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

        return Task.FromResult<IReadOnlyList<VectorIndexSearchResult>>(results);
    }

    public Task<IReadOnlyList<VectorIndexDiagnostic>> GetDiagnosticsAsync(
        VectorIndexQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = BuildStoreDiagnostics(Filter(_entries.Values, query)).ToArray();
        return Task.FromResult<IReadOnlyList<VectorIndexDiagnostic>>(diagnostics);
    }

    internal static IReadOnlyList<VectorIndexDiagnostic> BuildStoreDiagnostics(IEnumerable<VectorIndexEntry> entries)
    {
        var records = entries.ToArray();
        var diagnostics = new List<VectorIndexDiagnostic>();

        foreach (var entry in records.Where(entry => entry.Dimension != entry.Vector.Count))
        {
            diagnostics.Add(NewDiagnostic(
                VectorIndexDiagnosticTypes.DimensionMismatch,
                entry,
                $"向量维度声明为 {entry.Dimension}，实际向量长度为 {entry.Vector.Count}。",
                "重新生成该条 embedding。"));
        }

        foreach (var group in records
            .GroupBy(entry => DuplicateKey(entry), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1))
        {
            foreach (var entry in group)
            {
                diagnostics.Add(NewDiagnostic(
                    VectorIndexDiagnosticTypes.DuplicateVectorEntry,
                    entry,
                    "同一 item/model/provider 存在多条 vector index entry。",
                    "保留最新 entry 并清理旧 entry，避免重复噪声。"));
            }
        }

        return diagnostics;
    }

    private static IEnumerable<VectorIndexEntry> Filter(
        IEnumerable<VectorIndexEntry> entries,
        VectorIndexQuery query)
    {
        return entries
            .Where(entry => MatchesScope(entry, query.WorkspaceId, query.CollectionId))
            .Where(entry => string.IsNullOrWhiteSpace(query.ItemKind)
                || string.Equals(entry.ItemKind, query.ItemKind, StringComparison.OrdinalIgnoreCase))
            .Where(entry => string.IsNullOrWhiteSpace(query.Layer)
                || string.Equals(entry.Layer, query.Layer, StringComparison.OrdinalIgnoreCase))
            .Where(entry => string.IsNullOrWhiteSpace(query.EmbeddingModel)
                || string.Equals(entry.EmbeddingModel, query.EmbeddingModel, StringComparison.OrdinalIgnoreCase))
            .Where(entry => string.IsNullOrWhiteSpace(query.EmbeddingProvider)
                || string.Equals(entry.EmbeddingProvider, query.EmbeddingProvider, StringComparison.OrdinalIgnoreCase));
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

    internal static VectorIndexEntry Clone(VectorIndexEntry entry, bool includeVector = true)
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

    internal static VectorIndexDiagnostic NewDiagnostic(
        string type,
        VectorIndexEntry entry,
        string message,
        string suggestedAction,
        string severity = "Warning")
    {
        return new VectorIndexDiagnostic
        {
            DiagnosticId = $"{type}:{entry.WorkspaceId}:{entry.CollectionId}:{entry.EntryId}",
            Type = type,
            Severity = severity,
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

    internal static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
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

    private static bool MatchesScope(VectorIndexEntry entry, string workspaceId, string? collectionId)
    {
        return string.Equals(entry.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase)
               && (string.IsNullOrWhiteSpace(collectionId)
                   || string.Equals(entry.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase));
    }

    private static string DuplicateKey(VectorIndexEntry entry)
    {
        return $"{entry.WorkspaceId}\u001f{entry.CollectionId}\u001f{entry.ItemId}\u001f{entry.EmbeddingProvider}\u001f{entry.EmbeddingModel}";
    }

    private static string Key(string workspaceId, string collectionId, string entryId)
    {
        return $"{workspaceId}\u001f{collectionId}\u001f{entryId}";
    }
}
