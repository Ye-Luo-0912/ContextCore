using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.MemoryEvolution;

/// <summary>
/// <see cref="IUserFeedbackLedger"/> 的 in-memory 实现。
/// </summary>
/// <remarks>
/// 设计原则：
///   1. 读 API：QueryFeedbackAsync / GetLatestFeedbackForCandidateAsync。
///   2. 写 API：AppendFeedbackAsync — 单条追加，不去重（同 IdempotencyKey 可有多条历史快照）。
///   3. 关联校验跳过：InMemory 实现不做 EXISTS 校验以保持测试友好；生产路径由 Postgres 实现负责。
///   4. 生产部署应替换为 PostgresUserFeedbackLedgerStore（实现同一 <see cref="IUserFeedbackLedger"/> 契约）。
/// </remarks>
public sealed class InMemoryUserFeedbackLedgerStore : IUserFeedbackLedger
{
    private readonly ConcurrentBag<UserFeedbackEntry> _entries = new();

    /// <inheritdoc />
    public Task AppendFeedbackAsync(
        UserFeedbackEntry feedback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        cancellationToken.ThrowIfCancellationRequested();
        _entries.Add(feedback);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<UserFeedbackEntry>> QueryFeedbackAsync(
        UserFeedbackQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<UserFeedbackEntry> results = _entries;

        if (!string.IsNullOrEmpty(query.WorkspaceId))
        {
            results = results.Where(e => e.WorkspaceId == query.WorkspaceId);
        }
        if (query.CollectionId is not null)
        {
            results = results.Where(e => e.CollectionId == query.CollectionId);
        }
        if (query.DecisionId is not null)
        {
            results = results.Where(e => e.DecisionId == query.DecisionId);
        }
        if (query.CandidateItemId is not null)
        {
            results = results.Where(e => e.CandidateItemId == query.CandidateItemId);
        }
        if (query.Kind is not null)
        {
            results = results.Where(e => e.Kind == query.Kind.Value);
        }
        if (query.GivenBy is not null)
        {
            results = results.Where(e => e.GivenBy == query.GivenBy);
        }
        if (query.Since is not null)
        {
            results = results.Where(e => e.GivenAt >= query.Since.Value);
        }
        if (query.Until is not null)
        {
            results = results.Where(e => e.GivenAt <= query.Until.Value);
        }

        var ordered = results.OrderByDescending(e => e.GivenAt).ToList();
        if (query.Take > 0 && ordered.Count > query.Take)
        {
            ordered = ordered.Take(query.Take).ToList();
        }

        return Task.FromResult<IReadOnlyList<UserFeedbackEntry>>(ordered);
    }

    /// <inheritdoc />
    public Task<UserFeedbackEntry?> GetLatestFeedbackForCandidateAsync(
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
            .MaxBy(e => e.GivenAt);

        return Task.FromResult(latest);
    }
}
