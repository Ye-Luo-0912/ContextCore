using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ContextCore.Abstractions;

namespace ContextCore.Core;

/// <summary>
/// 基于内存的 <see cref="IContextStateVersionStore"/> 实现。
/// 使用 ConcurrentDictionary&lt;StrongBox&lt;long&gt;&gt; + Interlocked.Increment 保证线程安全的单调递增。
/// 进程内有效，重启丢失；适用于单实例 Alpha 与测试。多实例场景需替换为持久化实现。
/// </summary>
public sealed class InMemoryContextStateVersionStore : IContextStateVersionStore
{
    private readonly ConcurrentDictionary<string, StrongBox<long>> _versions = new();

    public Task<long> GetVersionAsync(
        string workspaceId,
        string collectionId,
        string storeKind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var version = _versions.TryGetValue(VersionKey(workspaceId, collectionId, storeKind), out var box)
            ? Interlocked.Read(ref box.Value)
            : 0L;
        return Task.FromResult(version);
    }

    public Task<IReadOnlyDictionary<VersionScope, long>> GetVersionsAsync(
        IReadOnlyCollection<VersionScope> scopes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(scopes);

        var result = new Dictionary<VersionScope, long>();
        foreach (var scope in scopes)
        {
            if (result.ContainsKey(scope))
            {
                continue;
            }

            var version = _versions.TryGetValue(VersionKey(scope.WorkspaceId, scope.CollectionId, scope.StoreKind), out var box)
                ? Interlocked.Read(ref box.Value)
                : 0L;
            result[scope] = version;
        }

        return Task.FromResult<IReadOnlyDictionary<VersionScope, long>>(result);
    }

    public Task<long> BumpVersionAsync(
        string workspaceId,
        string collectionId,
        string storeKind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // GetOrAdd 保证返回同一个 StrongBox 实例；Interlocked.Increment 原子自增其 Value 字段。
        var box = _versions.GetOrAdd(VersionKey(workspaceId, collectionId, storeKind), _ => new StrongBox<long>(0));
        return Task.FromResult(Interlocked.Increment(ref box.Value));
    }

    private static string VersionKey(string workspaceId, string collectionId, string storeKind)
        => $"{storeKind}|{workspaceId}|{collectionId}";
}
