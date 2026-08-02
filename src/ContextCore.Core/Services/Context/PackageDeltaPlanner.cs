using ContextCore.Abstractions;

namespace ContextCore.Core;

/// <summary>
/// 增量上下文包：默认 delta 规划器实现。
/// 比较前一个快照与当前请求指纹/store 版本向量，输出 delta 计划。
/// </summary>
/// <remarks>
/// 实现为纯函数：相同输入产生相同输出，不依赖外部状态。
/// 决策规则：
/// <list type="bullet">
/// <item>请求指纹 + store 版本均一致 → <see cref="PackageDeltaKind.NoChange"/>（直接复用快照）</item>
/// <item>请求指纹变化但 store 版本一致 → <see cref="PackageDeltaKind.RequestOnlyChange"/>（重新选择候选）</item>
/// <item>部分 store scope 版本变化 → <see cref="PackageDeltaKind.PartialSectionChange"/>（仅受影响 section 重载）</item>
/// <item>结构性变化或无法判断 → <see cref="PackageDeltaKind.FullRebuildRequired"/>（全量重建）</item>
/// </list>
/// V1 实现策略：<see cref="PackageDeltaKind.PartialSectionChange"/> 仍委托到全量构建，
/// 仅 <see cref="PackageDeltaKind.NoChange"/> 路径真正复用快照（性能优化）。
/// 这种保守策略保证等价性：增量构建输出与全量构建输出在所有维度完全一致。
/// </remarks>
public sealed class PackageDeltaPlanner : IPackageDeltaPlanner
{
    /// <inheritdoc />
    public PackageDeltaPlan Plan(
        PackageStateSnapshot previous,
        RequestSemanticFingerprint currentRequestFingerprint,
        StoreVersionVector currentStoreVersions)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(currentRequestFingerprint);
        ArgumentNullException.ThrowIfNull(currentStoreVersions);

        // 1. 请求指纹比较
        var requestChanged = !previous.RequestFingerprint.Equals(currentRequestFingerprint);

        // 2. store 版本向量比较
        var (storeChanged, changedScopes) = DiffVersions(previous.StoreVersions, currentStoreVersions);

        // 3. 决策
        if (!requestChanged && !storeChanged)
        {
            return PackageDeltaPlan.NoChange("请求指纹与 store 版本均未变化");
        }

        if (storeChanged)
        {
            // 找出受影响 section（基于 changedScopes 与 section 依赖映射）
            var affectedSections = ResolveAffectedSections(previous.SectionDependencies, changedScopes);
            if (affectedSections.Count > 0 && affectedSections.Count < previous.SectionDependencies.Count)
            {
                // 部分变化也委托全量构建（保守策略保证等价性）
                // V2 可在此分支实现真正的选择性重载
                var reason = $"部分 store scope 变化（{changedScopes.Count} 个 scope），影响 sections: {string.Join(", ", affectedSections)}；R15 V1 委托全量构建";
                return new PackageDeltaPlan(
                    PackageDeltaKind.PartialSectionChange,
                    affectedSections,
                    reason);
            }

            // 所有 section 都受影响，或无法定位受影响 section → 全量重建
            return PackageDeltaPlan.FullRebuild(
                $"store 版本变化影响全部 {previous.SectionDependencies.Count} 个 section");
        }

        // 仅请求变化（query/task/metadata 变化），store 数据未变 → 需要重新选择候选
        return new PackageDeltaPlan(
            PackageDeltaKind.RequestOnlyChange,
            Array.Empty<string>(),
            "请求指纹变化（query/task/metadata），store 数据未变；需要重新选择候选");
    }

    /// <summary>比较两个版本向量，返回是否变化与变化的 scope 列表。</summary>
    private static (bool changed, IReadOnlyList<VersionScope> changedScopes) DiffVersions(
        StoreVersionVector previous,
        StoreVersionVector current)
    {
        if (previous.Count == 0 || current.Count == 0)
        {
            // 无版本追踪 → 视为变化（保守策略，强制全量构建）
            return (true, Array.Empty<VersionScope>());
        }

        if (previous.Count != current.Count)
        {
            // scope 集合变化 → 全部视为变化
            return (true, current.Versions.Keys.ToList());
        }

        var changed = new List<VersionScope>();
        foreach (var (scope, prevVersion) in previous.Versions)
        {
            if (!current.Versions.TryGetValue(scope, out var currVersion))
            {
                // scope 缺失 → 视为变化
                changed.Add(scope);
            }
            else if (currVersion != prevVersion)
            {
                // 版本号变化
                changed.Add(scope);
            }
        }

        // 检查 current 中是否有 previous 没有的 scope
        foreach (var scope in current.Versions.Keys)
        {
            if (!previous.Versions.ContainsKey(scope))
            {
                changed.Add(scope);
            }
        }

        return (changed.Count > 0, changed);
    }

    /// <summary>基于变化的 scope 与 section 依赖映射，找出受影响的 section 名称。</summary>
    private static IReadOnlyList<string> ResolveAffectedSections(
        IReadOnlyDictionary<string, SectionDependencySet> sectionDependencies,
        IReadOnlyList<VersionScope> changedScopes)
    {
        if (changedScopes.Count == 0)
        {
            // 无版本追踪 → 所有 section 都视为受影响
            return sectionDependencies.Keys.ToList();
        }

        var changedScopeSet = new HashSet<VersionScope>(changedScopes);
        var affected = new List<string>();
        foreach (var (sectionName, deps) in sectionDependencies)
        {
            foreach (var scope in deps.DependencyScopes)
            {
                if (changedScopeSet.Contains(scope))
                {
                    affected.Add(sectionName);
                    break;
                }
            }
        }
        return affected;
    }
}
