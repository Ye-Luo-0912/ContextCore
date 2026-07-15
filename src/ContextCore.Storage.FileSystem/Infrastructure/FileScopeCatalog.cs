using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem;

/// <summary>
/// 统一 workspace/collection 目录遍历和 scope 枚举。
/// 替代各 Store 中 27 个同质的 EnumerateScopes / ResolveScopes / ResolveCollectionIds 方法。
/// </summary>
public sealed class FileScopeCatalog
{
    private readonly FilePathResolver _paths;

    public FileScopeCatalog(FilePathResolver paths)
    {
        _paths = paths;
    }

    /// <summary>
    /// 遍历所有包含指定 JSONL 文件的 workspace/collection 对。
    /// 仅当 <paramref name="jsonlPathSelector"/> 返回的路径存在时，该 scope 才被包含。
    /// </summary>
    public IReadOnlyList<ShortTermMemoryScope> EnumerateScopes(
        Func<string, string, string> jsonlPathSelector)
    {
        ArgumentNullException.ThrowIfNull(jsonlPathSelector);
        return EnumerateScopesCore(jsonlPathSelector, checkLegacy: null);
    }

    /// <summary>
    /// 遍历所有包含指定 JSONL 文件（或 legacy 回退路径）的 workspace/collection 对。
    /// 当 primary 路径或 legacy 路径任一存在时，该 scope 被包含。
    /// </summary>
    public IReadOnlyList<ShortTermMemoryScope> EnumerateScopes(
        Func<string, string, string> jsonlPathSelector,
        Func<string, string, string> legacyJsonlPathSelector)
    {
        ArgumentNullException.ThrowIfNull(jsonlPathSelector);
        ArgumentNullException.ThrowIfNull(legacyJsonlPathSelector);
        return EnumerateScopesCore(jsonlPathSelector, checkLegacy: legacyJsonlPathSelector);
    }

    /// <summary>
    /// 解析 scope：若 <paramref name="collectionId"/> 非空，返回单元素列表；
    /// 否则枚举该 workspace 下所有包含指定文件的 collections。
    /// </summary>
    public IReadOnlyList<ShortTermMemoryScope> ResolveScopes(
        string workspaceId,
        string? collectionId,
        Func<string, string, string> jsonlPathSelector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(jsonlPathSelector);

        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            return [new ShortTermMemoryScope { WorkspaceId = workspaceId, CollectionId = collectionId }];
        }

        return EnumerateScopes(jsonlPathSelector)
            .Where(s => string.Equals(s.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    /// <summary>
    /// 解析指定 workspace 下所有包含数据文件的 collectionId 列表。
    /// </summary>
    public IReadOnlyList<string> ResolveCollectionIds(
        string workspaceId,
        Func<string, string, string>? jsonlPathSelector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var collectionsRoot = Path.Combine(_paths.RootPath, "workspaces", _paths.SanitizeSegment(workspaceId), "collections");
        if (!Directory.Exists(collectionsRoot))
        {
            return Array.Empty<string>();
        }

        var collectionIds = new List<string>();
        foreach (var collectionDir in Directory.EnumerateDirectories(collectionsRoot))
        {
            var collectionId = Path.GetFileName(collectionDir);
            if (string.IsNullOrWhiteSpace(collectionId))
            {
                continue;
            }

            if (jsonlPathSelector is null || File.Exists(jsonlPathSelector(workspaceId, collectionId)))
            {
                collectionIds.Add(collectionId);
            }
        }

        return collectionIds;
    }

    /// <summary>
    /// 枚举存储根目录下所有 workspaceId 列表。
    /// </summary>
    public IReadOnlyList<string> ResolveWorkspaceIds()
    {
        var workspacesRoot = Path.Combine(_paths.RootPath, "workspaces");
        if (!Directory.Exists(workspacesRoot))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateDirectories(workspacesRoot)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray()!;
    }

    private IReadOnlyList<ShortTermMemoryScope> EnumerateScopesCore(
        Func<string, string, string> jsonlPathSelector,
        Func<string, string, string>? checkLegacy)
    {
        var workspacesRoot = Path.Combine(_paths.RootPath, "workspaces");
        if (!Directory.Exists(workspacesRoot))
        {
            return Array.Empty<ShortTermMemoryScope>();
        }

        return Directory.EnumerateDirectories(workspacesRoot)
            .SelectMany(workspaceDirectory =>
            {
                var workspaceId = Path.GetFileName(workspaceDirectory);
                if (string.IsNullOrWhiteSpace(workspaceId))
                {
                    return Array.Empty<ShortTermMemoryScope>();
                }

                var collectionsRoot = Path.Combine(workspaceDirectory, "collections");
                if (!Directory.Exists(collectionsRoot))
                {
                    return Array.Empty<ShortTermMemoryScope>();
                }

                return Directory.EnumerateDirectories(collectionsRoot)
                    .Select(collectionDirectory => new
                    {
                        WorkspaceId = workspaceId,
                        CollectionId = Path.GetFileName(collectionDirectory)
                    })
                    .Where(item => !string.IsNullOrWhiteSpace(item.CollectionId))
                    .Where(item =>
                        File.Exists(jsonlPathSelector(item.WorkspaceId, item.CollectionId))
                        || (checkLegacy is not null
                            && File.Exists(checkLegacy(item.WorkspaceId, item.CollectionId))))
                    .Select(item => new ShortTermMemoryScope
                    {
                        WorkspaceId = item.WorkspaceId,
                        CollectionId = item.CollectionId
                    })
                    .ToArray();
            })
            .DistinctBy(scope => $"{scope.WorkspaceId}\u001f{scope.CollectionId}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
