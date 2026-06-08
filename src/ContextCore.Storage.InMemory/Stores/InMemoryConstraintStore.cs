using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.InMemory;

/// <summary>基于内存的 <see cref="IConstraintStore"/> 实现，适用于测试和短生命周期场景。</summary>
public sealed class InMemoryConstraintStore : IConstraintStore
{
    private readonly ConcurrentDictionary<string, ContextConstraint> _constraints = new();

    public Task SaveAsync(ContextConstraint constraint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(constraint);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = Clone(constraint, string.IsNullOrWhiteSpace(constraint.Id) ? Guid.NewGuid().ToString("N") : constraint.Id);
        _constraints[Key(normalized.WorkspaceId, normalized.CollectionId, normalized.Id)] = normalized;

        return Task.CompletedTask;
    }

    public Task<ContextConstraint?> GetAsync(
        string constraintId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintId);
        cancellationToken.ThrowIfCancellationRequested();

        var match = _constraints.Values.FirstOrDefault(item =>
            string.Equals(item.Id, constraintId, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(match is null ? null : Clone(match));
    }

    public Task<IReadOnlyList<ContextConstraint>> QueryAsync(
        ContextConstraintQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var take = query.Take > 0 ? query.Take : 50;
        var results = _constraints.Values
            .Where(item => string.Equals(item.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(query.CollectionId)
                || string.IsNullOrWhiteSpace(item.CollectionId)
                || string.Equals(item.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            .Where(item => query.Scope is null || item.Scope == query.Scope)
            .Where(item => query.Level is null || item.Level == query.Level)
            .Where(item => query.Status is null || item.Status == query.Status)
            .Where(item => query.AppliesToRefs.Count == 0
                || query.AppliesToRefs.Any(reference =>
                    item.AppliesToRefs.Contains(reference, StringComparer.OrdinalIgnoreCase)
                    || item.SourceRefs.Contains(reference, StringComparer.OrdinalIgnoreCase)))
            .OrderByDescending(item => item.Level == ConstraintLevel.Hard)
            .ThenByDescending(item => item.Confidence)
            .ThenByDescending(item => item.UpdatedAt)
            .Take(take)
            .Select(item => Clone(item))
            .ToArray();

        return Task.FromResult<IReadOnlyList<ContextConstraint>>(results);
    }

    private static ContextConstraint Clone(ContextConstraint item, string? id = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new ContextConstraint
        {
            Id = id ?? item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Scope = item.Scope,
            Level = item.Level,
            Content = item.Content,
            AppliesToRefs = item.AppliesToRefs.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            Status = item.Status,
            Confidence = item.Confidence,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt == default ? now : item.CreatedAt,
            UpdatedAt = item.UpdatedAt == default ? now : item.UpdatedAt
        };
    }

    private static string Key(string workspaceId, string? collectionId, string id)
    {
        return $"{workspaceId}\u001f{collectionId}\u001f{id}";
    }
}
