using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.Policy;

// ===========================================================================
// 策略包注册表默认实现 + 默认 bundle 工厂。
//
// 目标：
//   把 R19-1 契约（ContextPolicyBundle / IPolicyRegistry）落到 Core 层，
//   提供可立即注入 DI 的默认实现 + 一个"全局默认 bundle"
//   （由 ContextDecisionPolicyVersions 静态常量 + 现有 hardcoded profile 默认值组装）。
//
// 设计原则：
//   1. DefaultPolicyRegistry 是 in-memory 实现：ConcurrentDictionary 线程安全。
//      生产部署应替换为 PostgresPolicyRegistry（持久化）+ MemoryCache。
//   2. GetActiveBundleAsync 在未激活时返回 DefaultPolicyBundleFactory.Create()，
//      保证调用方始终拿到非 null bundle（用户澄清 #2：bundle 全局不可变）。
//   3. P0-4 修复：RegisterBundleAsync 改为 insert-if-absent（相同 BundleId+Version
//      已存在时抛 InvalidOperationException，不再静默覆盖）。
//   4. P0-4 修复：TryActivateAsync 实现 CAS（compare-and-swap）原子激活。
//   5. P0-2 修复：GetBundleAsync 按 bundleId + version 精确加载；未找到返回 null。
//   6. ListBundlesAsync(includeSuperseded=false) 默认排除已 supersede 的 bundle。
//
// 与 R19-3 Pipeline 集成：
//   Engine.DecideAsync 在请求 PolicyBundleId 为空时，通过 IPolicyRegistry
//   解析当前 workspace+collection 的激活 bundle；未激活时使用默认 bundle。
//   PolicyBundleId 非空时通过 GetBundleAsync 精确加载（fail-closed）。
// ===========================================================================

/// <summary>
/// 默认策略包注册表（in-memory）。
/// </summary>
/// <remarks>
/// 线程安全：所有读写操作通过 ConcurrentDictionary 保护。
/// 生产部署应替换为 PostgresPolicyRegistry；契约不变。
/// </remarks>
public sealed class DefaultPolicyRegistry : IPolicyRegistry
{
    // bundle 主键改为 (BundleId, Version) 复合键，保证不可变语义。
    private readonly ConcurrentDictionary<string, ContextPolicyBundle> _bundles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PolicyActivation> _activations = new(StringComparer.Ordinal);

    /// <summary>默认 bundle（懒加载；首次访问时创建）。</summary>
    private readonly Lazy<ContextPolicyBundle> _defaultBundle = new(DefaultPolicyBundleFactory.Create);

    /// <inheritdoc />
    public async Task<ContextPolicyBundle> GetActiveBundleAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        var key = BuildActivationKey(workspaceId, collectionId);
        if (_activations.TryGetValue(key, out var activation))
        {
            // 精确读取 (BundleId, BundleVersion)，不再漂移到"最新版本"。
            // activation 记录了激活时的 BundleVersion，必须精确匹配。
            var bundle = await GetBundleAsync(activation.BundleId, activation.BundleVersion, cancellationToken)
                .ConfigureAwait(false);
            if (bundle is not null)
            {
                return bundle;
            }
        }
        // 未激活或 bundle 已删除 → 返回全局默认 bundle
        return _defaultBundle.Value;
    }

    /// <inheritdoc />
    public Task<ContextPolicyBundle?> GetBundleAsync(
        string bundleId,
        string? version,
        CancellationToken cancellationToken = default)
    {
        // + 精确加载。
        // - version 非空：按 (BundleId, Version) 复合主键精确查找。
        // - version 为空：返回该 BundleId 下最新非 superseded 版本。
        if (version is not null)
        {
            if (_bundles.TryGetValue(BuildBundleKey(bundleId, version), out var bundle))
            {
                return Task.FromResult<ContextPolicyBundle?>(bundle);
            }
            return Task.FromResult<ContextPolicyBundle?>(null);
        }

        // version 为空 → 查找最新非 superseded 版本
        var latest = FindLatestBundleForId(bundleId);
        return Task.FromResult(latest);
    }

    /// <summary>
    /// 在 _bundles 中查找指定 BundleId 下最新非 superseded 的版本。
    /// 用于 GetActiveBundleAsync 和 GetBundleAsync(version=null) 路径。
    /// </summary>
    private ContextPolicyBundle? FindLatestBundleForId(string bundleId)
    {
        ContextPolicyBundle? best = null;
        foreach (var kvp in _bundles)
        {
            var bundle = kvp.Value;
            if (!string.Equals(bundle.BundleId, bundleId, StringComparison.Ordinal))
            {
                continue;
            }
            if (bundle.IsSuperseded)
            {
                continue;
            }
            // 选 Version 字典序最大的（简化语义；生产 Postgres 实现按 SemVer 排序）
            if (best is null || string.Compare(bundle.Version, best.Version, StringComparison.Ordinal) > 0)
            {
                best = bundle;
            }
        }
        return best;
    }

    /// <inheritdoc />
    public Task<PolicyActivation?> GetActivationAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        var key = BuildActivationKey(workspaceId, collectionId);
        _activations.TryGetValue(key, out var activation);
        return Task.FromResult(activation);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContextPolicyBundle>> ListBundlesAsync(
        bool includeSuperseded = false,
        CancellationToken cancellationToken = default)
    {
        var bundles = _bundles.Values
            .Where(b => includeSuperseded || !b.IsSuperseded)
            .OrderBy(b => b.BundleId, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult<IReadOnlyList<ContextPolicyBundle>>(bundles);
    }

    /// <inheritdoc />
    public Task RegisterBundleAsync(
        ContextPolicyBundle bundle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        // insert-if-absent。(BundleId, Version) 为复合主键：
        //   - 相同 (BundleId, Version) 已存在 → 抛 InvalidOperationException（不可变）
        //   - 同 BundleId 不同 Version → 视为不同 bundle（支持 supersede 链）
        //   - 同 BundleId 同 Version → 不允许（bundle 全局不可变）
        var key = BuildBundleKey(bundle.BundleId, bundle.Version);
        if (!_bundles.TryAdd(key, bundle))
        {
            throw new InvalidOperationException(
                $"Bundle already registered: BundleId={bundle.BundleId}, Version={bundle.Version}. " +
                "Bundle is immutable; supersede by registering a new bundle with a different BundleId or Version.");
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> TryActivateAsync(
        PolicyActivation next,
        long expectedEpoch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(next);
        var key = BuildActivationKey(next.WorkspaceId, next.CollectionId);

        // CAS 语义
        // expectedEpoch == 0：首次激活（当前无 activation 记录）
        // expectedEpoch > 0：仅当当前 activation.Epoch == expectedEpoch 时才激活
        if (expectedEpoch == 0)
        {
            // 首次激活：仅当当前无记录时才成功
            var activated = new PolicyActivation
            {
                WorkspaceId = next.WorkspaceId,
                CollectionId = next.CollectionId,
                BundleId = next.BundleId,
                // 传播版本固定字段，确保 activation 精确指向激活时的 bundle 版本。
                BundleVersion = next.BundleVersion,
                BundleContentHash = next.BundleContentHash,
                ActivatedAt = next.ActivatedAt,
                ActivatedBy = next.ActivatedBy,
                RolloutStatus = next.RolloutStatus,
                Epoch = 1,
                BudgetOverride = next.BudgetOverride,
                RoutingOverride = next.RoutingOverride
            };
            return Task.FromResult(_activations.TryAdd(key, activated));
        }

        // CAS：读取当前 epoch，匹配则更新
        if (_activations.TryGetValue(key, out var current))
        {
            if (current.Epoch != expectedEpoch)
            {
                return Task.FromResult(false);
            }
            var updated = new PolicyActivation
            {
                WorkspaceId = next.WorkspaceId,
                CollectionId = next.CollectionId,
                BundleId = next.BundleId,
                // 传播版本固定字段，确保 activation 精确指向激活时的 bundle 版本。
                BundleVersion = next.BundleVersion,
                BundleContentHash = next.BundleContentHash,
                ActivatedAt = next.ActivatedAt,
                ActivatedBy = next.ActivatedBy,
                RolloutStatus = next.RolloutStatus,
                Epoch = current.Epoch + 1,
                BudgetOverride = next.BudgetOverride,
                RoutingOverride = next.RoutingOverride
            };
            return Task.FromResult(_activations.TryUpdate(key, updated, current));
        }

        // expectedEpoch > 0 但当前无记录 → CAS 失败
        return Task.FromResult(false);
    }

    /// <summary>暴露默认 bundle（仅用于测试与诊断）。</summary>
    internal ContextPolicyBundle GetDefaultBundle() => _defaultBundle.Value;

    private static string BuildActivationKey(string workspaceId, string collectionId)
        => $"{workspaceId}/{collectionId}";

    private static string BuildBundleKey(string bundleId, string? version)
        => version is null ? bundleId : $"{bundleId}@{version}";
}

// ===========================================================================
// 默认 bundle 工厂
// ===========================================================================

/// <summary>
/// 默认策略包工厂。从 ContextDecisionPolicyVersions 静态常量
/// + 现有 hardcoded profile 默认值组装全局默认 bundle。
/// </summary>
/// <remarks>
/// 此工厂生产的 bundle 用作"未激活任何 workspace+collection 时的兜底"。
/// 所有字段保持与现有 BasicContextPackageBuilder / HybridContextRetriever
/// 隐式默认值一致，避免 R19-2 引入行为变更。
/// </remarks>
public static class DefaultPolicyBundleFactory
{
    /// <summary>默认 bundle ID（与 R19-1 测试中的"未激活兜底"对齐）。</summary>
    public const string DefaultBundleId = "bundle-default";

    /// <summary>默认 bundle 版本号。</summary>
    public const string DefaultBundleVersion = "2026-07/default";

    /// <summary>创建全局默认 bundle。</summary>
    public static ContextPolicyBundle Create()
    {
        return new ContextPolicyBundle
        {
            BundleId = DefaultBundleId,
            Version = DefaultBundleVersion,
            Policies = new ContextPolicySet(),
            // 默认 Safety profile：保守 — 允许 deprecated-used-by-active-chain（仍参与评分），
            // 不允许 duplicate reference；不强制 required tags。
            Safety = new SafetyProfile
            {
                ProfileId = "safety-default-v1",
                AllowDeprecatedUsedByActiveChain = true,
                AllowDuplicateReference = false,
                RequiredTags = Array.Empty<string>(),
                ForbiddenTags = Array.Empty<string>()
            },
            // 默认 Budget profile：与 BasicContextPackageBuilder.DefaultTokenBudget 对齐
            Budget = new BudgetProfile
            {
                ProfileId = "budget-default-v1",
                DefaultTokenBudget = 8000,
                DefaultTopK = 50,
                SectionRatios = CreateDefaultSectionRatios(),
                StrictBudgetEnforcement = true
            },
            // 默认 Routing profile：纯 deterministic（无模型）
            Routing = new RoutingProfile
            {
                ProfileId = "routing-default-v1",
                EnableModelScoring = false,
                ModelArtifactId = null,
                DeterministicWeight = 1.0,
                ModelWeight = 0.0,
                ModelConfidenceThreshold = 0.70,
                EnabledExperts = Array.Empty<string>()
            },
            ModelArtifacts = Array.Empty<ModelArtifactReference>(),
            Rollout = null,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// 默认 section 比例分配（对齐 BasicContextPackageBuilder 默认模板）。
    /// </summary>
    /// <remarks>
    /// 当前 5 个 section 的默认比例：
    ///   - working_memory: 0.30（任务状态与短期信号）
    ///   - recent_context: 0.20（最近交互）
    ///   - related_context: 0.20（图扩展）
    ///   - stable_memory: 0.20（长期记忆）
    ///   - global_context: 0.10（全局上下文）
    /// 比例之和 = 1.0；可在 R19-3 Pipeline 集成后通过 PolicyBundle override 调整。
    /// </remarks>
    private static IReadOnlyDictionary<string, double> CreateDefaultSectionRatios()
    {
        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["working_memory"] = 0.30,
            ["recent_context"] = 0.20,
            ["related_context"] = 0.20,
            ["stable_memory"] = 0.20,
            ["global_context"] = 0.10
        };
    }
}
