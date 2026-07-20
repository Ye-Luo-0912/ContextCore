using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// R15 增量上下文包：默认增量构建器实现。
/// 基于前一个快照与当前请求执行增量构建，保证与全量构建完全等价。
/// </summary>
/// <remarks>
/// <b>核心验收契约</b>：<see cref="IncrementalBuildAsync"/> 的输出必须与
/// 对当前状态执行全量构建（<see cref="IContextPackageBuilder.BuildDetailedAsync"/>）的输出
/// 在以下维度完全等价：section 内容、selected IDs、dropped IDs、reason code、token attribution、source refs。
///
/// <b>R15 V2 实现策略</b>：
/// <list type="bullet">
/// <item><see cref="PackageDeltaKind.NoChange"/>：调用 <see cref="ISnapshotCapablePackageBuilder.RebuildFromSnapshotAsync"/>
///   直接复用快照中的 PackageTemplate，跳过 build pipeline（PackageInputLoader + CandidateSelector），
///   仅重新投影生成新的 PackageId/BuildId/CreatedAt/metadata。性能提升来自跳过 store 查询与候选选择。</item>
/// <item><see cref="PackageDeltaKind.RequestOnlyChange"/>/<see cref="PackageDeltaKind.PartialSectionChange"/>/
///   <see cref="PackageDeltaKind.FullRebuildRequired"/>：委托到 <see cref="IContextPackageBuilder.BuildDetailedAsync"/>
///   执行全量构建。PartialSectionChange 的选择性重载留待 V3，V2 保守策略保证等价性。</item>
/// </list>
/// 等价性保证：NoChange 路径的 <see cref="ISnapshotCapablePackageBuilder.RebuildFromSnapshotAsync"/>
/// 调用 <see cref="ResultProjector"/>.ProjectResult(template, options)，是纯函数；
/// 全量构建路径也调用同一 ProjectResult 方法，因此两条路径在相同 (template, options) 下输出完全一致。
/// <see cref="IPackageDeltaPlanner"/> 的输出仅用于决定走哪条路径，不影响路径内部的等价性。
/// </remarks>
public sealed class PackageIncrementalBuilder : IPackageIncrementalBuilder
{
    private readonly ISnapshotCapablePackageBuilder _innerBuilder;
    private readonly IPackageDeltaPlanner _deltaPlanner;
    private readonly IContextStateVersionStore? _versionStore;
    private readonly Action<PackageDeltaPlan>? _onDeltaPlanned;

    /// <summary>构造增量构建器。</summary>
    /// <param name="innerBuilder">内部支持快照的全量构建器（非空，需实现 ISnapshotCapablePackageBuilder）。</param>
    /// <param name="deltaPlanner">delta 规划器（非空）。</param>
    /// <param name="versionStore">版本存储（可为 null，无版本追踪时所有变化都视为 FullRebuild）。</param>
    /// <param name="onDeltaPlanned">delta 规划完成后的回调（用于可观测性，可为 null）。</param>
    public PackageIncrementalBuilder(
        ISnapshotCapablePackageBuilder innerBuilder,
        IPackageDeltaPlanner deltaPlanner,
        IContextStateVersionStore? versionStore = null,
        Action<PackageDeltaPlan>? onDeltaPlanned = null)
    {
        ArgumentNullException.ThrowIfNull(innerBuilder);
        ArgumentNullException.ThrowIfNull(deltaPlanner);
        _innerBuilder = innerBuilder;
        _deltaPlanner = deltaPlanner;
        _versionStore = versionStore;
        _onDeltaPlanned = onDeltaPlanned;
    }

    /// <inheritdoc />
    public async Task<ContextPackageBuildResult> IncrementalBuildAsync(
        PackageStateSnapshot previousSnapshot,
        ContextPackageRequest currentRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(previousSnapshot);
        ArgumentNullException.ThrowIfNull(currentRequest);

        // 1. 捕获当前请求的指纹 + 版本向量
        var policy = currentRequest.Policy ?? PackagePolicyResolver.CreateDefaultProductionPolicy(currentRequest);
        var currentFingerprint = CaptureCurrentFingerprint(currentRequest, policy);
        var currentStoreVersions = await CaptureCurrentStoreVersionsAsync(currentRequest, cancellationToken);

        // 2. 规划 delta，决定走 NoChange 快速路径还是全量构建路径
        var deltaPlan = _deltaPlanner.Plan(previousSnapshot, currentFingerprint, currentStoreVersions);
        _onDeltaPlanned?.Invoke(deltaPlan);

        // 3. 根据 delta kind 选择路径
        // R15 V2: NoChange 路径直接复用快照中的 PackageTemplate，跳过 build pipeline
        if (deltaPlan.Kind == PackageDeltaKind.NoChange)
        {
            return await _innerBuilder.RebuildFromSnapshotAsync(
                previousSnapshot, currentRequest, cancellationToken).ConfigureAwait(false);
        }

        // R15 V2: 其他 delta kind 委托到全量构建（PartialSectionChange 选择性重载留待 V3）
        return await _innerBuilder.BuildDetailedAsync(currentRequest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>捕获当前请求的语义指纹（用于 delta 规划）。</summary>
    private static RequestSemanticFingerprint CaptureCurrentFingerprint(
        ContextPackageRequest request,
        ContextPackagePolicy policy)
    {
        var hash = PackageRequestFingerprintBuilder.BuildHashed(request, policy);
        var components = BuildFingerprintComponents(request);
        return new RequestSemanticFingerprint(hash, components);
    }

    /// <summary>捕获当前 store 版本向量（用于 delta 规划）。</summary>
    private async Task<StoreVersionVector> CaptureCurrentStoreVersionsAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken)
    {
        if (_versionStore is null)
        {
            return StoreVersionVector.Empty;
        }

        var workspaceId = request.WorkspaceId;
        var collectionId = request.CollectionId ?? string.Empty;
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
        var versions = await _versionStore.GetVersionsAsync(scopes, cancellationToken).ConfigureAwait(false);
        return new StoreVersionVector(versions);
    }

    private static IReadOnlyDictionary<string, string> BuildFingerprintComponents(ContextPackageRequest request)
    {
        var components = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workspaceId"] = request.WorkspaceId ?? string.Empty,
            ["collectionId"] = request.CollectionId ?? string.Empty,
            ["queryText"] = request.QueryText ?? string.Empty,
            ["tokenBudget"] = request.TokenBudget.ToString(),
            ["mode"] = ((int)request.Mode).ToString(),
        };
        return components;
    }
}
