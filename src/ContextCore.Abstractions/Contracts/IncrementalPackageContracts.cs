using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

/// <summary>
/// R15 增量上下文包：将多个 <see cref="VersionScope"/> 的版本号作为整体向量引用，
/// 用于检测自上次构建以来 store 数据是否变化。
/// </summary>
/// <remarks>
/// 不可变快照；按 scope 比较两个向量是否一致。空向量视为"无版本追踪"，
/// 与 <see cref="ContextStateCacheAccessor"/> 既有行为兼容（无 versionStore 时跳过比较）。
/// </remarks>
public sealed class StoreVersionVector
{
    private readonly IReadOnlyDictionary<VersionScope, long> _versions;

    /// <summary>构造版本向量。</summary>
    /// <param name="versions">scope → 版本号映射（不可为 null）。</param>
    public StoreVersionVector(IReadOnlyDictionary<VersionScope, long> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        _versions = versions;
    }

    /// <summary>返回空向量（表示无版本追踪）。</summary>
    public static StoreVersionVector Empty { get; } =
        new StoreVersionVector(new Dictionary<VersionScope, long>());

    /// <summary>所有 scope 的版本号（只读视图）。</summary>
    public IReadOnlyDictionary<VersionScope, long> Versions => _versions;

    /// <summary>scope 数量。</summary>
    public int Count => _versions.Count;

    /// <summary>比较两个版本向量是否完全一致（scope 集合 + 每个版本号）。</summary>
    public bool Equals(StoreVersionVector? other)
    {
        if (other is null)
        {
            return false;
        }
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        if (_versions.Count != other._versions.Count)
        {
            return false;
        }
        foreach (var (scope, version) in _versions)
        {
            if (!other._versions.TryGetValue(scope, out var otherVersion) || otherVersion != version)
            {
                return false;
            }
        }
        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as StoreVersionVector);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (scope, version) in _versions)
        {
            hash.Add(scope);
            hash.Add(version);
        }
        return hash.ToHashCode();
    }
}

/// <summary>
/// R15 增量上下文包：请求语义指纹，包含哈希值与影响构建输出的语义组件分解。
/// </summary>
/// <remarks>
/// 指纹仅包含影响构建输出的字段，排除 OperationId/RequestId 等 per-call 标识。
/// <see cref="Hash"/> 为 SHA-256 64 字符 hex，可直接用于字典 key 与比较。
/// <see cref="Components"/> 用于 DeltaPlanner 判断 query/task/memory/constraints 是否变化。
/// </remarks>
public sealed class RequestSemanticFingerprint
{
    /// <summary>构造请求语义指纹。</summary>
    /// <param name="hash">SHA-256 64 字符 hex（非空）。</param>
    /// <param name="components">影响构建输出的语义组件（非空，按 Ordinal 排序）。</param>
    public RequestSemanticFingerprint(string hash, IReadOnlyDictionary<string, string> components)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ArgumentNullException.ThrowIfNull(components);
        Hash = hash;
        Components = components;
    }

    /// <summary>SHA-256 64 字符 hex 哈希值。</summary>
    public string Hash { get; }

    /// <summary>
    /// 影响构建输出的语义组件分解（如 queryText/currentTask/requiredTags/policy/timeBucket）。
    /// 用于 DeltaPlanner 细粒度判断哪部分变化。
    /// </summary>
    public IReadOnlyDictionary<string, string> Components { get; }

    /// <inheritdoc />
    public bool Equals(RequestSemanticFingerprint? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(Hash, other.Hash, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as RequestSemanticFingerprint);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Hash);
}

/// <summary>
/// R15 增量上下文包：单个 section 的依赖集合，记录影响该 section 内容的所有 scope。
/// </summary>
/// <remarks>
/// 用于 DeltaPlanner 判断哪些 section 需要重载：当 section 的某个依赖 scope 版本变化时，
/// 该 section 必须重载；其他 section 可复用快照。
/// </remarks>
public sealed class SectionDependencySet
{
    /// <summary>构造 section 依赖集合。</summary>
    /// <param name="sectionName">section 名称（非空，如 "recent_context"、"current_task"）。</param>
    /// <param name="dependencyScopes">该 section 依赖的所有 VersionScope（不可为空集合）。</param>
    public SectionDependencySet(string sectionName, IReadOnlyList<VersionScope> dependencyScopes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);
        ArgumentNullException.ThrowIfNull(dependencyScopes);
        if (dependencyScopes.Count == 0)
        {
            throw new ArgumentException(
                "SectionDependencySet 必须包含至少一个依赖 scope。",
                nameof(dependencyScopes));
        }
        SectionName = sectionName;
        DependencyScopes = dependencyScopes;
    }

    /// <summary>section 名称。</summary>
    public string SectionName { get; }

    /// <summary>该 section 依赖的所有 VersionScope（只读视图）。</summary>
    public IReadOnlyList<VersionScope> DependencyScopes { get; }
}

/// <summary>
/// R15 增量上下文包：构建完成后的不可变状态快照，作为下次增量构建的基线。
/// </summary>
/// <remarks>
/// 包含 PackageTemplate（不可变）+ 请求指纹 + store 版本向量 + section 依赖映射，
/// 足以让 DeltaPlanner 判断下次构建是否需要重载及哪些 section 受影响。
/// PackageTemplate 为 internal 类型，此快照仅由 Core 层构造与使用；
/// Abstractions 层只暴露非泛型的契约接口。
/// </remarks>
public sealed class PackageStateSnapshot
{
    private readonly object _template;

    /// <summary>构造包状态快照。</summary>
    /// <param name="template">不可变 PackageTemplate（Core 内部类型，以 object 传入避免循环依赖）。</param>
    /// <param name="requestFingerprint">请求语义指纹（非空）。</param>
    /// <param name="storeVersions">store 版本向量（非空）。</param>
    /// <param name="sectionDependencies">section 依赖映射（非空，key 为 section name）。</param>
    /// <param name="capturedAt">快照捕获时间。</param>
    public PackageStateSnapshot(
        object template,
        RequestSemanticFingerprint requestFingerprint,
        StoreVersionVector storeVersions,
        IReadOnlyDictionary<string, SectionDependencySet> sectionDependencies,
        DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(requestFingerprint);
        ArgumentNullException.ThrowIfNull(storeVersions);
        ArgumentNullException.ThrowIfNull(sectionDependencies);
        _template = template;
        RequestFingerprint = requestFingerprint;
        StoreVersions = storeVersions;
        SectionDependencies = sectionDependencies;
        CapturedAt = capturedAt;
    }

    /// <summary>不可变 PackageTemplate（Core 内部类型，调用方需 cast 回 PackageTemplate）。</summary>
    public object Template => _template;

    /// <summary>请求语义指纹。</summary>
    public RequestSemanticFingerprint RequestFingerprint { get; }

    /// <summary>store 版本向量。</summary>
    public StoreVersionVector StoreVersions { get; }

    /// <summary>section 依赖映射（key 为 section name）。</summary>
    public IReadOnlyDictionary<string, SectionDependencySet> SectionDependencies { get; }

    /// <summary>快照捕获时间。</summary>
    public DateTimeOffset CapturedAt { get; }
}

/// <summary>R15 增量上下文包：delta 种类，决定增量构建策略。</summary>
public enum PackageDeltaKind
{
    /// <summary>无变化：请求指纹一致 + 所有 store 版本一致 → 直接复用快照。</summary>
    NoChange,

    /// <summary>仅请求变化：query/task/metadata 变化但 store 数据未变 → 需要重新选择候选。</summary>
    RequestOnlyChange,

    /// <summary>部分 section 变化：某些 store scope 版本变化，仅影响部分 section → 选择性重载。</summary>
    PartialSectionChange,

    /// <summary>需要完整重建：结构性变化（policy/section 顺序/tokenBudget 变化）或无法确定 delta。</summary>
    FullRebuildRequired
}

/// <summary>R15 增量上下文包：delta 计划，由 DeltaPlanner 输出，描述变化范围。</summary>
public sealed record PackageDeltaPlan(
    PackageDeltaKind Kind,
    IReadOnlyList<string> AffectedSectionNames,
    string ReasonDescription)
{
    /// <summary>构造 NoChange delta 计划。</summary>
    public static PackageDeltaPlan NoChange(string reason = "请求指纹与 store 版本均未变化")
        => new(PackageDeltaKind.NoChange, Array.Empty<string>(), reason);

    /// <summary>构造 FullRebuildRequired delta 计划。</summary>
    public static PackageDeltaPlan FullRebuild(string reason)
        => new(PackageDeltaKind.FullRebuildRequired, Array.Empty<string>(), reason);
}

/// <summary>
/// R15 增量上下文包：delta 规划器，比较前一个快照与当前请求/版本，输出 delta 计划。
/// </summary>
/// <remarks>
/// 实现必须为纯函数：相同输入产生相同输出，不依赖外部状态。
/// 实现失败不得影响正式输出（调用方负责回退到全量构建）。
/// </remarks>
public interface IPackageDeltaPlanner
{
    /// <summary>规划 delta。</summary>
    /// <param name="previous">前一个状态快照（非空）。</param>
    /// <param name="currentRequestFingerprint">当前请求的语义指纹（非空）。</param>
    /// <param name="currentStoreVersions">当前 store 版本向量（非空）。</param>
    /// <returns>delta 计划。</returns>
    PackageDeltaPlan Plan(
        PackageStateSnapshot previous,
        RequestSemanticFingerprint currentRequestFingerprint,
        StoreVersionVector currentStoreVersions);
}

/// <summary>
/// R15 增量上下文包：增量构建器，基于前一个快照与当前请求执行增量构建。
/// </summary>
/// <remarks>
/// 核心验收契约：<see cref="IncrementalBuildAsync"/> 的输出必须与
/// 对当前状态执行全量构建（<see cref="IContextPackageBuilder.BuildDetailedAsync"/>）的输出
/// 在以下维度完全等价：section 内容、selected IDs、dropped IDs、reason code、token attribution、source refs。
/// 性能提升是副产品，不是首要目标；正确性（等价性）优先于性能。
/// </remarks>
public interface IPackageIncrementalBuilder
{
    /// <summary>基于前一个快照执行增量构建。</summary>
    /// <param name="previousSnapshot">前一个状态快照（非空）。</param>
    /// <param name="currentRequest">当前请求（非空）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>构建结果（与全量构建等价）。</returns>
    Task<ContextPackageBuildResult> IncrementalBuildAsync(
        PackageStateSnapshot previousSnapshot,
        ContextPackageRequest currentRequest,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// R15 增量上下文包：支持快照捕获的包构建器。
/// 在全量构建完成后返回构建结果与状态快照，调用方将快照传给
/// <see cref="IPackageIncrementalBuilder.IncrementalBuildAsync"/> 执行下次增量构建。
/// </summary>
/// <remarks>
/// 此接口的存在使快照捕获成为 build pipeline 的一部分，
/// 避免调用方需要重新执行 build 流水线来获取 internal PackageTemplate。
/// R15 V2 新增 <see cref="RebuildFromSnapshotAsync"/>：在 NoChange delta 路径上
/// 直接复用快照中的 PackageTemplate，跳过 build pipeline，仅重新投影为
/// 新的 <see cref="ContextPackageBuildResult"/>（重新生成 PackageId/BuildId/CreatedAt/metadata）。
/// </remarks>
public interface ISnapshotCapablePackageBuilder : IContextPackageBuilder
{
    /// <summary>执行全量构建并捕获状态快照。</summary>
    /// <param name="request">构建请求（非空）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>构建结果与状态快照。</returns>
    Task<PackageBuildWithSnapshot> BuildDetailedWithSnapshotAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// R15 V2：从既有快照复用 PackageTemplate，重新投影为新的 <see cref="ContextPackageBuildResult"/>。
    /// 仅用于 <see cref="PackageDeltaKind.NoChange"/> 路径：请求指纹 + store 版本均未变化，
    /// 因此快照中的 PackageTemplate 仍有效，可跳过 build pipeline。
    /// </summary>
    /// <remarks>
    /// 调用方必须确保 <paramref name="snapshot"/> 的请求指纹与 <paramref name="request"/> 一致
    /// 且 store 版本未变化（即 delta plan 为 NoChange）。
    /// 实现端从 <see cref="PackageStateSnapshot.Template"/>（object）cast 回 internal PackageTemplate，
    /// 调用 ResultProjector 生成新的 PackageId/BuildId/CreatedAt/metadata。
    /// 此方法不写 trace（trace 在 build pipeline 内部，复用 template 时已无新候选决策可记录）。
    /// </remarks>
    /// <param name="snapshot">前一个状态快照（非空，Template 字段需为 internal PackageTemplate 实例）。</param>
    /// <param name="request">当前请求（非空，需与快照的请求指纹一致）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>与全量构建等价的构建结果（仅身份字段不同）。</returns>
    Task<ContextPackageBuildResult> RebuildFromSnapshotAsync(
        PackageStateSnapshot snapshot,
        ContextPackageRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>R15 增量上下文包：全量构建结果与状态快照的组合返回。</summary>
public sealed record PackageBuildWithSnapshot(
    ContextPackageBuildResult Result,
    PackageStateSnapshot Snapshot);
