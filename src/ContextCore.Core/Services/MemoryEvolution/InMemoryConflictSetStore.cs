using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.MemoryEvolution;

/// <summary>
/// R21-3：IConflictSetStore 的 in-memory 实现（read-only 公共 API）。
/// </summary>
/// <remarks>
/// 设计原则（对齐澄清 #4）：
///   1. 公共 API 是 read-only（QueryAsync / GetAsync / GetConflictsForCandidateAsync）。
///   2. 写入由 internal AppendConflictSets 方法暴露，仅供 UtilityLedgerMaterializer 调用。
///   3. 生产部署应替换为 PostgresConflictSetStore（仍保持 read-only 公共 API）。
/// </remarks>
public sealed class InMemoryConflictSetStore : IConflictSetStore
{
    private readonly ConcurrentBag<ConflictSet> _conflictSets = new();

    /// <summary>
    /// 内部写入方法（仅供 UtilityLedgerMaterializer 调用）。
    /// 批量追加 ConflictSet；不去重（同 decision 可有多个 ConflictSet）。
    /// </summary>
    internal void AppendConflictSets(IEnumerable<ConflictSet> conflictSets)
    {
        ArgumentNullException.ThrowIfNull(conflictSets);
        foreach (var set in conflictSets)
        {
            ArgumentNullException.ThrowIfNull(set);
            _conflictSets.Add(set);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ConflictSet>> QueryAsync(
        ConflictSetQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<ConflictSet> results = _conflictSets;

        if (query.CollectionId is not null)
        {
            results = results.Where(c => c.CollectionId == query.CollectionId);
        }
        if (query.Kind is not null)
        {
            results = results.Where(c => c.Kind == query.Kind.Value);
        }
        if (query.CandidateItemId is not null)
        {
            results = results.Where(c => c.Entries.Any(e => e.CandidateItemId == query.CandidateItemId));
        }
        if (query.DecisionId is not null)
        {
            results = results.Where(c => c.DecisionId == query.DecisionId);
        }
        if (query.Since is not null)
        {
            results = results.Where(c => c.MaterializedAt >= query.Since.Value);
        }
        if (query.Until is not null)
        {
            results = results.Where(c => c.MaterializedAt <= query.Until.Value);
        }

        var ordered = results.OrderByDescending(c => c.MaterializedAt).ToList();
        if (query.Take > 0 && ordered.Count > query.Take)
        {
            ordered = ordered.Take(query.Take).ToList();
        }

        return Task.FromResult<IReadOnlyList<ConflictSet>>(ordered);
    }

    /// <inheritdoc />
    public Task<ConflictSet?> GetAsync(
        string workspaceId,
        string collectionId,
        string conflictSetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conflictSetId);
        cancellationToken.ThrowIfCancellationRequested();

        var set = _conflictSets.FirstOrDefault(c =>
            c.WorkspaceId == workspaceId
            && c.CollectionId == collectionId
            && c.ConflictSetId == conflictSetId);

        return Task.FromResult(set);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ConflictSet>> GetConflictsForCandidateAsync(
        string workspaceId,
        string collectionId,
        string candidateItemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateItemId);
        cancellationToken.ThrowIfCancellationRequested();

        var results = _conflictSets
            .Where(c => c.WorkspaceId == workspaceId
                && c.CollectionId == collectionId
                && c.Entries.Any(e => e.CandidateItemId == candidateItemId))
            .OrderByDescending(c => c.MaterializedAt)
            .ToList();

        return Task.FromResult<IReadOnlyList<ConflictSet>>(results);
    }
}
