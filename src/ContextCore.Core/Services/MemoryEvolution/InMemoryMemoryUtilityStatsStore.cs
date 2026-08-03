using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.MemoryEvolution;

/// <summary>
/// IMemoryUtilityStatsStore 的 in-memory 实现（read-only 公共 API）。
/// </summary>
/// <remarks>
/// 设计原则（对齐澄清）：
/// 1. 公共 API 是 read-only（QueryAsync / GetAsync / GetSelectedCountByExpertAsync）。
/// 2. 写入由 internal UpsertSnapshot 方法暴露，仅供 MemoryUtilityStatsMaterializer 调用。
/// 3. Stats 按 (WorkspaceId, CollectionId, SourceItemId) 唯一索引；
/// UpsertSnapshot 替换现有记录（stats 是当前快照，不是历史事件流）。
/// 4. 生产部署应替换为 PostgresMemoryUtilityStatsStore。
/// </remarks>
public sealed class InMemoryMemoryUtilityStatsStore : IMemoryUtilityStatsStore
{
    private readonly ConcurrentDictionary<string, MemoryUtilityStats> _statsByKey = new(StringComparer.Ordinal);

    private static string BuildKey(string workspaceId, string collectionId, string sourceItemId)
        => $"{workspaceId}|{collectionId}|{sourceItemId}";

    /// <summary>
    /// 内部写入方法（仅供 MemoryUtilityStatsMaterializer 调用）。
    /// 按 (WorkspaceId, CollectionId, SourceItemId) upsert。
    /// </summary>
    internal void UpsertSnapshot(MemoryUtilityStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        var key = BuildKey(stats.WorkspaceId, stats.CollectionId, stats.SourceItemId);
        _statsByKey[key] = stats;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MemoryUtilityStats>> QueryAsync(
        MemoryUtilityStatsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<MemoryUtilityStats> results = _statsByKey.Values;

        if (query.CollectionId is not null)
        {
            results = results.Where(s => s.CollectionId == query.CollectionId);
        }
        if (query.SourceItemId is not null)
        {
            results = results.Where(s => s.SourceItemId == query.SourceItemId);
        }
        if (query.ItemType is not null)
        {
            results = results.Where(s => s.ItemType == query.ItemType);
        }
        if (query.MinSelectedCount is not null)
        {
            results = results.Where(s => s.SelectedCount >= query.MinSelectedCount.Value);
        }
        if (query.MaxSelectedCount is not null)
        {
            results = results.Where(s => s.SelectedCount <= query.MaxSelectedCount.Value);
        }
        if (query.BeforeLastUsefulTime is not null)
        {
            results = results.Where(s =>
                s.LastUsefulTime is null || s.LastUsefulTime.Value <= query.BeforeLastUsefulTime.Value);
        }
        if (query.MinRecallCount is not null)
        {
            results = results.Where(s => s.RecallCount >= query.MinRecallCount.Value);
        }

        var ordered = results.OrderByDescending(s => s.UpdatedAt).ToList();
        if (query.Take > 0 && ordered.Count > query.Take)
        {
            ordered = ordered.Take(query.Take).ToList();
        }

        return Task.FromResult<IReadOnlyList<MemoryUtilityStats>>(ordered);
    }

    /// <inheritdoc />
    public Task<MemoryUtilityStats?> GetAsync(
        string workspaceId,
        string collectionId,
        string sourceItemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceItemId);
        cancellationToken.ThrowIfCancellationRequested();

        var key = BuildKey(workspaceId, collectionId, sourceItemId);
        _statsByKey.TryGetValue(key, out var stats);
        return Task.FromResult(stats);
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<RetrievalExpert, int>> GetSelectedCountByExpertAsync(
        string workspaceId,
        string collectionId,
        string sourceItemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceItemId);
        cancellationToken.ThrowIfCancellationRequested();

        // InMemory 实现不存储 per-Expert 统计；返回空字典。
        // 生产 Postgres 实现应通过 UtilityLedgerStore 聚合查询。
        // 此方法在 InMemory 测试中返回空字典，调用方应回退到 UtilityLedgerStore 查询。
        var empty = new Dictionary<RetrievalExpert, int>();
        return Task.FromResult<IReadOnlyDictionary<RetrievalExpert, int>>(empty);
    }
}
