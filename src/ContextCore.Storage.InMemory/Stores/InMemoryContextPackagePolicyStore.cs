using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.InMemory.Stores;

/// <summary>基于内存的上下文包策略存储，主要用于测试和临时运行。</summary>
public sealed class InMemoryContextPackagePolicyStore : IContextPackagePolicyStore
{
    private readonly ConcurrentDictionary<string, ContextPackagePolicy> _policies = new();

    public Task SaveAsync(
        ContextPackagePolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(policy.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(policy.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(policy.CollectionId);
        cancellationToken.ThrowIfCancellationRequested();

        _policies[Key(policy.WorkspaceId, policy.CollectionId!, policy.Id)] = Clone(policy);
        return Task.CompletedTask;
    }

    public Task<ContextPackagePolicy?> GetAsync(
        string workspaceId,
        string collectionId,
        string policyId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            _policies.TryGetValue(Key(workspaceId, collectionId, policyId), out var policy)
                ? Clone(policy)
                : null);
    }

    public Task<IReadOnlyList<ContextPackagePolicy>> QueryAsync(
        ContextPackagePolicyQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        var take = query.Take > 0 ? query.Take : 50;

        var items = _policies.Values
            .Where(item => string.Equals(item.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.Equals(item.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            .Where(item => MatchesQuery(item, query.QueryText))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .Select(Clone)
            .ToArray();

        return Task.FromResult<IReadOnlyList<ContextPackagePolicy>>(items);
    }

    private static bool MatchesQuery(ContextPackagePolicy policy, string? queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            return true;
        }

        return Contains(policy.Id, queryText)
            || Contains(policy.Name, queryText)
            || Contains(policy.Description, queryText);
    }

    private static bool Contains(string? value, string queryText)
    {
        return value?.Contains(queryText, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string Key(string workspaceId, string collectionId, string policyId)
    {
        return $"{workspaceId}\u001f{collectionId}\u001f{policyId}";
    }

    private static ContextPackagePolicy Clone(ContextPackagePolicy policy)
    {
        return new ContextPackagePolicy
        {
            Id = policy.Id,
            WorkspaceId = policy.WorkspaceId,
            CollectionId = policy.CollectionId,
            Name = policy.Name,
            Description = policy.Description,
            TokenBudget = policy.TokenBudget,
            IncludeGlobalContext = policy.IncludeGlobalContext,
            IncludeHardConstraints = policy.IncludeHardConstraints,
            IncludeSoftConstraints = policy.IncludeSoftConstraints,
            IncludeWorkingMemory = policy.IncludeWorkingMemory,
            IncludeStableMemory = policy.IncludeStableMemory,
            IncludeRecentRawContext = policy.IncludeRecentRawContext,
            MaxRecentItems = policy.MaxRecentItems,
            SectionOrder = policy.SectionOrder.ToArray(),
            SectionPriorities = new Dictionary<string, int>(policy.SectionPriorities),
            SectionTokenBudgets = new Dictionary<string, int>(policy.SectionTokenBudgets),
            Metadata = new Dictionary<string, string>(policy.Metadata)
        };
    }
}