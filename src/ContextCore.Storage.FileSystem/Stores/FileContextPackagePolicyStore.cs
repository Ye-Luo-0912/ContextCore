using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>将上下文包策略持久化为集合目录下的 JSONL 文件。</summary>
public sealed class FileContextPackagePolicyStore : IContextPackagePolicyStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FileJsonLineStore _jsonLines;
    private readonly FilePathResolver _paths;

    public FileContextPackagePolicyStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileContextPackagePolicyStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
    }

    public async Task SaveAsync(
        ContextPackagePolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(policy.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(policy.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(policy.CollectionId);

        var path = _paths.GetPackagePoliciesJsonlPath(policy.WorkspaceId, policy.CollectionId!);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _jsonLines.UpsertAsync(
                path,
                Clone(policy),
                item => item.Id,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ContextPackagePolicy?> GetAsync(
        string workspaceId,
        string collectionId,
        string policyId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);

        var policies = await QueryAsync(new ContextPackagePolicyQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);

        return policies.FirstOrDefault(item =>
            string.Equals(item.Id, policyId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<ContextPackagePolicy>> QueryAsync(
        ContextPackagePolicyQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.CollectionId);

        var path = _paths.GetPackagePoliciesJsonlPath(query.WorkspaceId, query.CollectionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var policies = await _jsonLines.ReadAsync<ContextPackagePolicy>(path, cancellationToken)
                .ConfigureAwait(false);
            var take = query.Take > 0 ? query.Take : 50;

            return [.. policies
                .Where(item => MatchesQuery(item, query.QueryText))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Take(take)
                .Select(Clone)];
        }
        finally
        {
            _gate.Release();
        }
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