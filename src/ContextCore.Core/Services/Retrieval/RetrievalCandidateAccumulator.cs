using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 统一维护检索候选的去重和合并。
/// 同一 sourceId/kind 的候选项只保留一个 builder，并叠加通道贡献。
/// </summary>
internal sealed class RetrievalCandidateAccumulator
{
    private readonly Dictionary<string, RetrievalCandidateBuilder> _items = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _items.Count;

    public bool Contains(ContextRetrievalCandidateKind kind, string sourceId)
        => _items.ContainsKey(Key(kind, sourceId));

    public void AddOrMerge(RetrievalChannelResult result)
    {
        foreach (var candidate in result.Candidates)
        {
            AddOrMerge(candidate);
        }
    }

    public void AddOrMerge(RetrievalChannelCandidate candidate)
    {
        var key = Key(candidate.Kind, candidate.SourceId);
        if (!_items.TryGetValue(key, out var builder))
        {
            builder = CreateBuilder(candidate);
            _items[key] = builder;
        }

        builder.AddOrMerge(candidate);
    }

    public IReadOnlyList<ContextRetrievalCandidate> ToCandidates(bool includeContent)
    {
        return [.. _items.Values.Select(item => item.Build(includeContent))];
    }

    private static RetrievalCandidateBuilder CreateBuilder(RetrievalChannelCandidate candidate)
    {
        if (candidate.ContextItem is not null)
        {
            return RetrievalCandidateBuilder.FromContextItem(candidate.ContextItem);
        }

        if (candidate.MemoryItem is not null)
        {
            return RetrievalCandidateBuilder.FromMemoryItem(candidate.MemoryItem);
        }

        if (candidate.RelationTarget is not null)
        {
            return RetrievalCandidateBuilder.FromRelationTarget(candidate.RelationTarget);
        }

        throw new InvalidOperationException("RetrievalChannelCandidate 缺少可用于初始化 builder 的来源对象。");
    }

    private static string Key(ContextRetrievalCandidateKind kind, string sourceId)
    {
        return $"{kind}:{sourceId}";
    }
}
