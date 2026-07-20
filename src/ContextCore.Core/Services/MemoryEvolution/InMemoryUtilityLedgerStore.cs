using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.MemoryEvolution;

/// <summary>
/// R21-3：IUtilityLedgerStore 的 in-memory 实现（read-only 公共 API）。
/// </summary>
/// <remarks>
/// 设计原则（对齐澄清 #4）：
///   1. 公共 API 是 read-only（QueryAsync / GetLatestEntryAsync / GetExpertContributionsAsync）。
///   2. 写入由 internal AppendEntries 方法暴露，仅供 UtilityLedgerMaterializer 调用。
///   3. 生产部署应替换为 PostgresUtilityLedgerStore（仍保持 read-only 公共 API，
///      写入由 materializer 通过 store 内部的 bulk insert SQL 完成）。
/// </remarks>
public sealed class InMemoryUtilityLedgerStore : IUtilityLedgerStore
{
    private readonly ConcurrentBag<UtilityLedgerEntry> _entries = new();

    /// <summary>
    /// 内部写入方法（仅供 UtilityLedgerMaterializer 调用）。
    /// 批量追加 ledger 条目；不去重（同 candidate 可有多条历史快照）。
    /// </summary>
    internal void AppendEntries(IEnumerable<UtilityLedgerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            _entries.Add(entry);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<UtilityLedgerEntry>> QueryAsync(
        UtilityLedgerQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<UtilityLedgerEntry> results = _entries;

        if (query.CollectionId is not null)
        {
            results = results.Where(e => e.CollectionId == query.CollectionId);
        }
        if (query.CandidateItemId is not null)
        {
            results = results.Where(e => e.CandidateItemId == query.CandidateItemId);
        }
        if (query.Expert is not null)
        {
            results = results.Where(e => e.Expert == query.Expert.Value);
        }
        if (query.DecisionId is not null)
        {
            results = results.Where(e => e.DecisionId == query.DecisionId);
        }
        if (query.IsSelected is not null)
        {
            results = results.Where(e => e.IsSelected == query.IsSelected.Value);
        }
        if (query.Since is not null)
        {
            results = results.Where(e => e.MaterializedAt >= query.Since.Value);
        }
        if (query.Until is not null)
        {
            results = results.Where(e => e.MaterializedAt <= query.Until.Value);
        }

        var ordered = results.OrderByDescending(e => e.MaterializedAt).ToList();
        if (query.Take > 0 && ordered.Count > query.Take)
        {
            ordered = ordered.Take(query.Take).ToList();
        }

        return Task.FromResult<IReadOnlyList<UtilityLedgerEntry>>(ordered);
    }

    /// <inheritdoc />
    public Task<UtilityLedgerEntry?> GetLatestEntryAsync(
        string workspaceId,
        string collectionId,
        string candidateItemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateItemId);
        cancellationToken.ThrowIfCancellationRequested();

        var latest = _entries
            .Where(e => e.WorkspaceId == workspaceId
                && e.CollectionId == collectionId
                && e.CandidateItemId == candidateItemId)
            .OrderByDescending(e => e.MaterializedAt)
            .FirstOrDefault();

        return Task.FromResult(latest);
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<RetrievalExpert, double>> GetExpertContributionsAsync(
        string workspaceId,
        string collectionId,
        string candidateItemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateItemId);
        cancellationToken.ThrowIfCancellationRequested();

        var contributions = _entries
            .Where(e => e.WorkspaceId == workspaceId
                && e.CollectionId == collectionId
                && e.CandidateItemId == candidateItemId)
            .GroupBy(e => e.Expert)
            .ToDictionary(
                g => g.Key,
                g => g.Average(e => e.UtilityContribution));

        return Task.FromResult<IReadOnlyDictionary<RetrievalExpert, double>>(contributions);
    }
}
