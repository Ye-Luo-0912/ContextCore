using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem;

/// <summary>
/// 在 <see cref="FileJsonLineStore"/> 之上封装按 scope 的 Upsert/Read/范围查询模式。
/// 组合 <see cref="FileJsonLineStore"/>（单文件原子原语）+ <see cref="FileScopeCatalog"/>（scope 枚举），
/// 为简单的审核/诊断/报告记录提供标准化的存储操作。
/// </summary>
/// <typeparam name="T">记录类型。</typeparam>
public sealed class ScopedJsonLineRepository<T>
    where T : class
{
    private readonly FileJsonLineStore _jsonLines;
    private readonly FileScopeCatalog _scopeCatalog;

    public ScopedJsonLineRepository(FileJsonLineStore jsonLines, FileScopeCatalog scopeCatalog)
    {
        _jsonLines = jsonLines;
        _scopeCatalog = scopeCatalog;
    }

    /// <summary>
    /// 在指定 scope 的 JSONL 文件中按键 Upsert 一条记录。
    /// </summary>
    public async Task UpsertAsync(
        string workspaceId,
        string collectionId,
        T item,
        Func<T, string> keySelector,
        Func<string, string, string> pathSelector,
        CancellationToken cancellationToken = default)
    {
        var path = pathSelector(workspaceId, collectionId);
        await _jsonLines.UpsertAsync(path, item, keySelector, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 在指定 scope 的 JSONL 文件中执行读改写事务。
    /// </summary>
    public async Task UpdateAsync(
        string workspaceId,
        string collectionId,
        Func<IReadOnlyList<T>, IReadOnlyList<T>> update,
        Func<string, string, string> pathSelector,
        CancellationToken cancellationToken = default)
    {
        var path = pathSelector(workspaceId, collectionId);
        await _jsonLines.UpdateAsync(path, update, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 读取指定 scope 的所有记录。
    /// </summary>
    public async Task<IReadOnlyList<T>> ReadScopeAsync(
        string workspaceId,
        string collectionId,
        Func<string, string, string> pathSelector,
        CancellationToken cancellationToken = default)
    {
        var path = pathSelector(workspaceId, collectionId);
        return await _jsonLines.ReadAsync<T>(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 跨所有 scope 查询记录，用 <paramref name="predicate"/> 过滤。
    /// </summary>
    public async Task<IReadOnlyList<T>> QueryAcrossScopesAsync(
        Func<string, string, string> pathSelector,
        Func<T, bool>? predicate = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<T>();
        foreach (var scope in _scopeCatalog.EnumerateScopes(pathSelector))
        {
            var items = await _jsonLines.ReadAsync<T>(
                pathSelector(scope.WorkspaceId, scope.CollectionId),
                cancellationToken).ConfigureAwait(false);

            if (predicate is null)
            {
                results.AddRange(items);
            }
            else
            {
                results.AddRange(items.Where(predicate));
            }
        }

        return results;
    }

    /// <summary>
    /// 跨指定 workspace 的所有 collection 查询记录。
    /// </summary>
    public async Task<IReadOnlyList<T>> QueryInWorkspaceAsync(
        string workspaceId,
        string? collectionId,
        Func<string, string, string> pathSelector,
        Func<T, bool>? predicate = null,
        CancellationToken cancellationToken = default)
    {
        var scopes = _scopeCatalog.ResolveScopes(workspaceId, collectionId, pathSelector);
        var results = new List<T>();
        foreach (var scope in scopes)
        {
            var items = await _jsonLines.ReadAsync<T>(
                pathSelector(scope.WorkspaceId, scope.CollectionId),
                cancellationToken).ConfigureAwait(false);

            if (predicate is null)
            {
                results.AddRange(items);
            }
            else
            {
                results.AddRange(items.Where(predicate));
            }
        }

        return results;
    }

    /// <summary>
    /// 跨所有 scope 查询记录（含 legacy 回退路径），用 <paramref name="predicate"/> 过滤。
    /// legacy 路径的记录追加在 primary 之后。
    /// </summary>
    public async Task<IReadOnlyList<T>> QueryAcrossScopesWithLegacyAsync(
        Func<string, string, string> pathSelector,
        Func<string, string, string> legacyPathSelector,
        Func<T, bool>? predicate = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<T>();
        foreach (var scope in _scopeCatalog.EnumerateScopes(pathSelector, legacyPathSelector))
        {
            var primaryPath = pathSelector(scope.WorkspaceId, scope.CollectionId);
            var items = await _jsonLines.ReadAsync<T>(primaryPath, cancellationToken).ConfigureAwait(false);

            var legacyPath = legacyPathSelector(scope.WorkspaceId, scope.CollectionId);
            if (!string.Equals(primaryPath, legacyPath, StringComparison.OrdinalIgnoreCase) && File.Exists(legacyPath))
            {
                var legacy = await _jsonLines.ReadAsync<T>(legacyPath, cancellationToken).ConfigureAwait(false);
                if (legacy.Count > 0)
                {
                    items = [.. items, .. legacy];
                }
            }

            if (predicate is null)
            {
                results.AddRange(items);
            }
            else
            {
                results.AddRange(items.Where(predicate));
            }
        }

        return results;
    }
}
