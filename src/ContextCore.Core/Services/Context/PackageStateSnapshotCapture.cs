using System.Collections.Immutable;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// R15 增量上下文包：section 依赖映射器。
/// 基于 section 名称与策略推断该 section 依赖的 VersionScope 集合。
/// </summary>
/// <remarks>
/// 这是一个静态映射：section 名称到 scope 的对应关系是稳定的（不依赖运行时状态）。
/// 映射规则与 <see cref="PackageRequestFingerprintBuilder.BuildDependencyScopes"/> 保持一致，
/// 但细化到 section 级别。
/// </remarks>
internal static class SectionDependencyMapper
{
    /// <summary>
    /// 根据 PackageTemplate 的 section 列表与 workspace/collection 推断每个 section 的依赖 scope。
    /// </summary>
    public static IReadOnlyDictionary<string, SectionDependencySet> BuildDependencies(
        ImmutableArray<ContextPackageSection> sections,
        string workspaceId,
        string collectionId)
    {
        var result = new Dictionary<string, SectionDependencySet>(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            var name = section.Name ?? string.Empty;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }
            var scopes = ResolveScopesForSection(name, workspaceId, collectionId);
            if (scopes.Count > 0 && !result.ContainsKey(name))
            {
                result[name] = new SectionDependencySet(name, scopes);
            }
        }
        return result;
    }

    /// <summary>
    /// 根据 section 名称推断其依赖的 VersionScope 集合。
    /// 规则基于现有 build pipeline 的实际数据流：
    /// <list type="bullet">
    /// <item>recent_context → ContextStore</item>
    /// <item>hard_constraints / soft_constraints → ConstraintStore</item>
    /// <item>working_memory → MemoryStore + WorkingMemoryService</item>
    /// <item>stable_memory → MemoryStore</item>
    /// <item>global_context → GlobalContextStore (collection + workspace)</item>
    /// <item>current_task → WorkingMemoryService</item>
    /// <item>merged_constraints → ConstraintStore</item>
    /// <item>packing → ContextStore + MemoryStore + ConstraintStore + GlobalContextStore + RelationStore</item>
    /// <item>未知 section → 全 scope（保守策略，视为受所有 store 影响）</item>
    /// </list>
    /// </summary>
    private static IReadOnlyList<VersionScope> ResolveScopesForSection(
        string sectionName,
        string workspaceId,
        string collectionId)
    {
        var scopes = new List<VersionScope>();

        switch (sectionName)
        {
            case "recent_context":
            case "recent":
                scopes.Add(new VersionScope(workspaceId, collectionId, "ContextStore"));
                break;
            case "hard_constraints":
            case "hard":
                scopes.Add(new VersionScope(workspaceId, collectionId, "ConstraintStore"));
                break;
            case "soft_constraints":
            case "soft":
                scopes.Add(new VersionScope(workspaceId, collectionId, "ConstraintStore"));
                break;
            case "working_memory":
                scopes.Add(new VersionScope(workspaceId, collectionId, "MemoryStore"));
                scopes.Add(new VersionScope(workspaceId, collectionId, "WorkingMemoryService"));
                break;
            case "stable_memory":
                scopes.Add(new VersionScope(workspaceId, collectionId, "MemoryStore"));
                break;
            case "global_context":
                scopes.Add(new VersionScope(workspaceId, collectionId, "GlobalContextStore"));
                scopes.Add(new VersionScope(workspaceId, string.Empty, "GlobalContextStore"));
                break;
            case "current_task":
                scopes.Add(new VersionScope(workspaceId, collectionId, "WorkingMemoryService"));
                break;
            case "merged_constraints":
            case "merged":
                scopes.Add(new VersionScope(workspaceId, collectionId, "ConstraintStore"));
                break;
            case "packing":
                // packing 是全局重新打包，受所有 store 影响
                scopes.Add(new VersionScope(workspaceId, collectionId, "ContextStore"));
                scopes.Add(new VersionScope(workspaceId, collectionId, "MemoryStore"));
                scopes.Add(new VersionScope(workspaceId, collectionId, "ConstraintStore"));
                scopes.Add(new VersionScope(workspaceId, collectionId, "GlobalContextStore"));
                scopes.Add(new VersionScope(workspaceId, string.Empty, "GlobalContextStore"));
                scopes.Add(new VersionScope(workspaceId, collectionId, "RelationStore"));
                scopes.Add(new VersionScope(workspaceId, collectionId, "WorkingMemoryService"));
                break;
            default:
                // 未知 section → 保守策略，绑定到全 scope（任何 store 变化都触发重载）
                scopes.Add(new VersionScope(workspaceId, collectionId, "ContextStore"));
                scopes.Add(new VersionScope(workspaceId, collectionId, "MemoryStore"));
                scopes.Add(new VersionScope(workspaceId, collectionId, "ConstraintStore"));
                scopes.Add(new VersionScope(workspaceId, collectionId, "GlobalContextStore"));
                scopes.Add(new VersionScope(workspaceId, string.Empty, "GlobalContextStore"));
                scopes.Add(new VersionScope(workspaceId, collectionId, "RelationStore"));
                scopes.Add(new VersionScope(workspaceId, collectionId, "WorkingMemoryService"));
                break;
        }

        return scopes;
    }
}

/// <summary>
/// R15 增量上下文包：包状态快照捕获扩展。
/// 在 <see cref="IContextPackageBuilder.BuildDetailedAsync"/> 完成后调用，捕获不可变快照。
/// </summary>
internal static class PackageStateSnapshotCapture
{
    /// <summary>
    /// 捕获包状态快照。
    /// 在 build 完成后调用，需要传入 PackageTemplate（internal 类型）+ 请求 + policy + 版本存储。
    /// </summary>
    public static async Task<PackageStateSnapshot> CaptureAsync(
        PackageTemplate template,
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        IContextStateVersionStore? versionStore,
        CancellationToken cancellationToken)
    {
        var workspaceId = request.WorkspaceId;
        var collectionId = request.CollectionId ?? string.Empty;

        // 1. 请求指纹（含 SHA-256 hash）
        var hash = PackageRequestFingerprintBuilder.BuildHashed(request, policy);
        var components = BuildFingerprintComponents(request, policy);
        var fingerprint = new RequestSemanticFingerprint(hash, components);

        // 2. store 版本向量
        var storeVersions = await CaptureStoreVersionsAsync(workspaceId, collectionId, versionStore, cancellationToken);

        // 3. section 依赖映射
        var sectionDependencies = SectionDependencyMapper.BuildDependencies(
            template.OrderedSections, workspaceId, collectionId);

        return new PackageStateSnapshot(
            template,
            fingerprint,
            storeVersions,
            sectionDependencies,
            DateTimeOffset.UtcNow);
    }

    /// <summary>构建请求语义指纹组件分解（用于 DeltaPlanner 细粒度判断）。</summary>
    private static IReadOnlyDictionary<string, string> BuildFingerprintComponents(
        ContextPackageRequest request,
        ContextPackagePolicy policy)
    {
        var components = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workspaceId"] = request.WorkspaceId ?? string.Empty,
            ["collectionId"] = request.CollectionId ?? string.Empty,
            ["queryText"] = request.QueryText ?? string.Empty,
            ["tokenBudget"] = request.TokenBudget.ToString(),
            ["mode"] = ((int)request.Mode).ToString(),
            ["policyId"] = policy.Id,
            ["timeBucket"] = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300).ToString(),
        };
        return components;
    }

    /// <summary>从版本存储批量捕获所有相关 scope 的版本号。</summary>
    private static async Task<StoreVersionVector> CaptureStoreVersionsAsync(
        string workspaceId,
        string collectionId,
        IContextStateVersionStore? versionStore,
        CancellationToken cancellationToken)
    {
        if (versionStore is null)
        {
            return StoreVersionVector.Empty;
        }

        // 与 PackageRequestFingerprintBuilder.BuildDependencyScopes 一致的 scope 集合
        var scopes = new List<VersionScope>
        {
            new(workspaceId, collectionId, "ContextStore"),
            new(workspaceId, collectionId, "MemoryStore"),
            new(workspaceId, collectionId, "ConstraintStore"),
            new(workspaceId, collectionId, "GlobalContextStore"),
            new(workspaceId, string.Empty, "GlobalContextStore"),
            new(workspaceId, collectionId, "RelationStore"),
            new(workspaceId, collectionId, "WorkingMemoryService"),
        };

        var versions = await versionStore.GetVersionsAsync(scopes, cancellationToken).ConfigureAwait(false);
        return new StoreVersionVector(versions);
    }
}
