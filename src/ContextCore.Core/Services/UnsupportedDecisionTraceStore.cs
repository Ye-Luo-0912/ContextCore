using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>未实现持久化后端时的显式占位存储，避免运行时静默丢弃决策记录。</summary>
public sealed class UnsupportedDecisionTraceStore : IDecisionTraceStore
{
    private readonly string _provider;

    public UnsupportedDecisionTraceStore(string provider)
    {
        _provider = string.IsNullOrWhiteSpace(provider) ? "unknown" : provider;
    }

    public Task SaveAsync(
        ContextDecisionRecord record,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<IReadOnlyList<ContextDecisionRecord>> QueryRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    private NotSupportedException CreateException()
    {
        return new NotSupportedException(
            $"Decision trace store is not implemented for storage provider '{_provider}'.");
    }
}
