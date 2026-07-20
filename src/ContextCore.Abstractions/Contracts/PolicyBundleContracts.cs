using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

// ===========================================================================
// R19-1：策略包契约（Context Policy Bundle Contracts）
//
// 目标：
//   把分散在 ContextDecisionPolicyVersions（5 个版本常量）+ 各处
//   profile 隐式约定（BasicContextPackageBuilder 的默认 section ratios、
//   HybridContextRetriever 的 TopK、Engine 的 ModelConfidenceThreshold）
//   升级为显式策略包：ContextPolicyBundle 全局不可变 + PolicyActivation
//   按 workspace/collection 激活 + 3 个 Profile（Safety/Budget/Routing）。
//
// 设计原则（用户澄清 #2 / #3）：
//   1. Bundle 全局不可变：版本号一旦发布不可修改，supersede 通过新建 bundle 实现。
//   2. Activation 按 workspace/collection：暂不引入 tenant。
//      同一 workspace+collection 同一时刻只有一个 active bundle。
//   3. Request Policy 是受限 override：不允许替换安全边界（SafetyProfile /
//      RequiredTags）和正式模型（ModelArtifactReference）。仅允许调整
//      非安全相关参数（TopK、TokenBudget、SectionRatios、EnableModel）。
//   4. RolloutPolicy 复用 R16 EvolutionContracts.RollbackCondition：
//      bundle rollout 阶段（Shadow → ScopedCanary → Promoted）任一
//      condition 命中 → 自动回滚到上一稳定 bundle。
//   5. 不引入存储 I/O：契约是内存中的不可变 record/class。
//      PolicyRegistry 接口允许实现层注入 Postgres / InMemory store。
//
// 子阶段进度：
//   R19-1（当前）：契约定义 + 单元测试验证可实施性。
//   R19-2：PolicyBundle Provider 适配（从 ContextDecisionPolicyVersions
//          静态常量 + 现有 hardcoded profile 迁移到 ContextPolicyBundle）。
//   R19-3：Pipeline 集成（Engine.DecideAsync 读取 PolicyBundleId →
//          通过 IPolicyRegistry 解析 → 应用 Safety/Budget/Routing profile）。
// ===========================================================================

/// <summary>
/// R19-1：策略包版本集合。复用 <see cref="ContextDecisionPolicyVersions"/> 5 个版本常量，
/// 把"能力作用域的版本字符串集合"显式提升为 bundle 内部字段。
/// </summary>
/// <remarks>
/// 这 5 个版本字段均不可被 per-request override（用户澄清 #3）：
/// 安全边界（DecisionSchema + QualityContract）与正式模型
/// （PackagePolicy + RetrievalPolicy + RelationProfile）由 bundle 全局决定。
/// </remarks>
public sealed record ContextPolicySet
{
    /// <summary>决策 schema 版本（对应 ContextDecisionRecord 结构）。</summary>
    public string DecisionSchemaVersion { get; init; } = ContextDecisionPolicyVersions.DecisionSchemaV2_0;

    /// <summary>Package 策略版本（section 模板 / order / assembly rules）。</summary>
    public string PackagePolicyVersion { get; init; } = ContextDecisionPolicyVersions.PackagePolicyV3_1;

    /// <summary>Retrieval 策略版本（channel priority / packing policy）。</summary>
    public string RetrievalPolicyVersion { get; init; } = ContextDecisionPolicyVersions.RetrievalPolicyV4_0;

    /// <summary>Relation Profile 版本（graph traversal / expansion depth）。</summary>
    public string RelationProfileVersion { get; init; } = ContextDecisionPolicyVersions.RelationProfileV2_0;

    /// <summary>Quality Contract 版本（package quality metrics contract）。</summary>
    public string QualityContractVersion { get; init; } = ContextDecisionPolicyVersions.QualityContractV1_0;
}

/// <summary>
/// R19-1：模型 artifact 引用。表示 bundle 内部引用的模型版本，
/// 用于 trace 溯源 + canary shadow 跟踪。
/// </summary>
/// <remarks>
/// 模型 artifact 不直接驱动运行时决策；Engine 通过 RoutingProfile.ModelArtifactId
/// 间接引用。Model failure 时（ModelConfidence=0），Engine 精确回退到
/// DeterministicScore（验收标准 #6）。
/// </remarks>
public sealed record ModelArtifactReference
{
    /// <summary>artifact 唯一 ID（如 "router-v1.2.3" / "reranker-v0.4.0"）。</summary>
    public required string ArtifactId { get; init; }

    /// <summary>模型类型（router / reranker / listwise-ranker / embedding）。</summary>
    public required string ModelType { get; init; }

    /// <summary>模型版本（semver 或内部版本号）。</summary>
    public required string Version { get; init; }

    /// <summary>模型存储 URI（如 model registry path / OSS bucket key）。</summary>
    public string? StorageUri { get; init; }

    /// <summary>注册时间（UTC）。</summary>
    public DateTimeOffset RegisteredAt { get; init; }

    /// <summary>artifact 当前生命周期状态（复用 LearningLoopContracts.ModelArtifactStatus）。</summary>
    /// <remarks>
    /// 状态流转：Draft → Validated → Staged → Active → Deprecated → Retired。
    /// bundle 引用 Active 状态的 artifact 作为正式模型；Staged 状态用于 shadow 测试。
    /// </remarks>
    public ModelArtifactStatus Status { get; init; } = ModelArtifactStatus.Draft;
}

// ---------------------------------------------------------------------------
// 3 个 Profile（Safety / Budget / Routing）
// ---------------------------------------------------------------------------

/// <summary>
/// R19-1：安全策略 profile。承载 safety gate 判定所需参数。
/// 此 profile 由 bundle 全局决定，**不允许 per-request override**（用户澄清 #3）。
/// </summary>
public sealed record SafetyProfile
{
    /// <summary>profile 唯一 ID（如 "safety-default-v1"）。</summary>
    public required string ProfileId { get; init; }

    /// <summary>是否允许 deprecated 但被 active chain 引用的候选仍参与评分。</summary>
    /// <remarks>
    /// true = 标记但不阻断（仍参与 utility 评分）；
    /// false = 直接 drop（PassesSafetyGate=false）。
    /// </remarks>
    public bool AllowDeprecatedUsedByActiveChain { get; init; } = true;

    /// <summary>是否允许同一 ContentHash 的候选重复出现（仍标记 IsDuplicate）。</summary>
    public bool AllowDuplicateReference { get; init; } = false;

    /// <summary>必须命中的 tag 列表；候选缺少任一 tag → IsRequiredTagMismatch=true。</summary>
    public IReadOnlyList<string> RequiredTags { get; init; } = Array.Empty<string>();

    /// <summary>禁止的 tag 列表；候选命中任一 tag → PassesSafetyGate=false。</summary>
    public IReadOnlyList<string> ForbiddenTags { get; init; } = Array.Empty<string>();
}

/// <summary>
/// R19-1：预算策略 profile。承载 token budget + TopK + section 比例分配。
/// 此 profile 由 bundle 决定，但 **允许 per-request override**（用户澄清 #3）。
/// </summary>
public sealed record BudgetProfile
{
    /// <summary>profile 唯一 ID（如 "budget-default-v1"）。</summary>
    public required string ProfileId { get; init; }

    /// <summary>默认整体 token 预算（per-request 可 override）。</summary>
    public int DefaultTokenBudget { get; init; } = 8000;

    /// <summary>默认 TopK 上限（per-request 可 override）。</summary>
    public int DefaultTopK { get; init; } = 50;

    /// <summary>section 比例分配（key = section 名，value = [0,1]）。</summary>
    /// <remarks>per-request 可通过 Request.SectionRatios override。</remarks>
    public IReadOnlyDictionary<string, double> SectionRatios { get; init; }
        = new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>是否强制严格执行预算（false = 允许部分截断接受）。</summary>
    public bool StrictBudgetEnforcement { get; init; } = true;
}

/// <summary>
/// R19-1：路由策略 profile。控制模型启用 + 专家开关 + 置信度阈值。
/// 此 profile 由 bundle 决定，但 **允许 per-request 部分字段 override**（用户澄清 #3）。
/// </summary>
/// <remarks>
/// 可 override 字段：EnableModel（Request.EnableModel）。
/// 不可 override 字段：ModelArtifactId / DeterministicWeight / ModelWeight /
/// ModelConfidenceThreshold（涉及正式模型决策权重）。
/// </remarks>
public sealed record RoutingProfile
{
    /// <summary>profile 唯一 ID（如 "routing-default-v1"）。</summary>
    public required string ProfileId { get; init; }

    /// <summary>是否启用模型评分（per-request 可通过 Request.EnableModel=false 强制关闭）。</summary>
    public bool EnableModelScoring { get; init; } = false;

    /// <summary>引用的模型 artifact ID（null = 纯 deterministic 路径）。</summary>
    public string? ModelArtifactId { get; init; }

    /// <summary>deterministic 评分权重（FinalScore = w_d*Det + w_m*Model）。</summary>
    public double DeterministicWeight { get; init; } = 1.0;

    /// <summary>model 评分权重（仅当 EnableModelScoring=true 时生效）。</summary>
    public double ModelWeight { get; init; } = 0.0;

    /// <summary>模型置信度阈值；ModelConfidence 低于此值时回退到 DeterministicScore。</summary>
    public double ModelConfidenceThreshold { get; init; } = 0.70;

    /// <summary>启用的专家列表（R20 Multi-Expert；空 = 全部启用）。</summary>
    public IReadOnlyList<string> EnabledExperts { get; init; } = Array.Empty<string>();
}

// ---------------------------------------------------------------------------
// RolloutPolicy + ContextPolicyBundle
// ---------------------------------------------------------------------------

/// <summary>
/// R19-1：策略包 rollout 策略。控制 bundle 从 shadow → canary → promoted 的生命周期。
/// </summary>
/// <remarks>
/// 复用 R16 EvolutionContracts.RollbackCondition；任一 condition 命中 →
/// 自动回滚到上一稳定 bundle（用户澄清 #2）。
/// </remarks>
public sealed record RolloutPolicy
{
    /// <summary>policy 唯一 ID（如 "rollout-bundle-2026-07-shadow"）。</summary>
    public required string PolicyId { get; init; }

    /// <summary>rollout 阶段。</summary>
    public PolicyRolloutStrategy Strategy { get; init; } = PolicyRolloutStrategy.Shadow;

    /// <summary>限定的 workspace ID 列表（Shadow 阶段为空；ScopedCanary 阶段为非空）。</summary>
    public IReadOnlyList<string> ScopedWorkspaceIds { get; init; } = Array.Empty<string>();

    /// <summary>限定的 collection ID 列表（Shadow 阶段为空；ScopedCanary 阶段为非空）。</summary>
    public IReadOnlyList<string> ScopedCollectionIds { get; init; } = Array.Empty<string>();

    /// <summary>rollout 开始时间（UTC；null = 立即生效）。</summary>
    public DateTimeOffset? StartAt { get; init; }

    /// <summary>rollout 结束时间（UTC；null = 不自动结束）。</summary>
    public DateTimeOffset? EndAt { get; init; }

    /// <summary>回滚条件列表（任一命中 → 自动回滚）。</summary>
    public IReadOnlyList<RollbackCondition> RollbackConditions { get; init; }
        = Array.Empty<RollbackCondition>();
}

/// <summary>
/// R19-1：策略包 rollout 阶段。对齐 R17 OptimizationStage 但作用域限制在 bundle。
/// </summary>
public enum PolicyRolloutStrategy : byte
{
    /// <summary>Inactive：bundle 已注册但未启用。</summary>
    Inactive = 0,

    /// <summary>Shadow：bundle 在后台运行但不影响正式决策（仅 trace）。</summary>
    Shadow = 1,

    /// <summary>Scoped canary：在限定 workspace/collection 内启用。</summary>
    ScopedCanary = 2,

    /// <summary>Promoted：bundle 作为正式基线。</summary>
    Promoted = 3,

    /// <summary>Rolled back：bundle 因 condition 命中被自动回滚。</summary>
    RolledBack = 4
}

/// <summary>
/// R19-1：策略包（Context Policy Bundle）。
/// 全局不可变，包含 PolicySet + 3 个 Profile + ModelArtifacts + RolloutPolicy。
/// </summary>
/// <remarks>
/// 设计原则（用户澄清 #2）：
///   - Bundle 全局不可变：版本号一旦发布不可修改。
///   - Supersede 通过新建 bundle 实现（SupersededByBundleId 字段）。
///   - Activation 按 workspace/collection：同一时刻只有一个 active bundle。
///   - 暂不引入 tenant：tenant 字段保留为 future extension。
/// </remarks>
public sealed record ContextPolicyBundle
{
    /// <summary>bundle 唯一 ID（如 "bundle-2026-07-20-v1"）。</summary>
    public required string BundleId { get; init; }

    /// <summary>bundle 版本号（semver 或日期版本，如 "2026-07-20/v1"）。</summary>
    public required string Version { get; init; }

    /// <summary>策略集（5 个能力作用域的版本字符串）。</summary>
    public ContextPolicySet Policies { get; init; } = new();

    /// <summary>安全 profile（不可 per-request override）。</summary>
    public SafetyProfile Safety { get; init; } = new() { ProfileId = "safety-default-v1" };

    /// <summary>预算 profile（可 per-request override 部分字段）。</summary>
    public BudgetProfile Budget { get; init; } = new() { ProfileId = "budget-default-v1" };

    /// <summary>路由 profile（可 per-request override EnableModel）。</summary>
    public RoutingProfile Routing { get; init; } = new() { ProfileId = "routing-default-v1" };

    /// <summary>引用的模型 artifact 列表（可空；空 = 纯 deterministic）。</summary>
    public IReadOnlyList<ModelArtifactReference> ModelArtifacts { get; init; }
        = Array.Empty<ModelArtifactReference>();

    /// <summary>rollout 策略（控制 bundle 生命周期）。</summary>
    public RolloutPolicy? Rollout { get; init; }

    /// <summary>bundle 创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>bundle 被 supersede 的时间（UTC；未 supersede 为 MinValue）。</summary>
    public DateTimeOffset SupersededAt { get; init; } = DateTimeOffset.MinValue;

    /// <summary>supersede 本 bundle 的新 bundle ID（未 supersede 为 null）。</summary>
    public string? SupersededByBundleId { get; init; }

    /// <summary>是否已被 supersede（SupersededAt 非 MinValue 即视为 superseded）。</summary>
    public bool IsSuperseded => SupersededAt != DateTimeOffset.MinValue;
}

// ---------------------------------------------------------------------------
// PolicyActivation + IPolicyRegistry
// ---------------------------------------------------------------------------

/// <summary>
/// R19-1：策略激活记录。表示某 workspace+collection 当前激活的 bundle + 可选 profile override。
/// </summary>
/// <remarks>
/// 设计原则（用户澄清 #2）：
///   - Activation 按 workspace/collection 隔离；同一 workspace+collection 同一时刻只有一个 active bundle。
///   - Profile override 受限：不允许替换 SafetyProfile；BudgetProfile / RoutingProfile 仅允许
///     部分字段 override（用户澄清 #3）。
/// </remarks>
public sealed record PolicyActivation
{
    /// <summary>workspace 作用域（必填）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>collection 作用域（必填）。</summary>
    public required string CollectionId { get; init; }

    /// <summary>激活的 bundle ID（必填）。</summary>
    public required string BundleId { get; init; }

    /// <summary>激活时间（UTC）。</summary>
    public required DateTimeOffset ActivatedAt { get; init; }

    /// <summary>激活的操作者（user / system / agent）。</summary>
    public string ActivatedBy { get; init; } = "system";

    /// <summary>rollout 状态（来自 bundle.Rollout，可被 activation 覆盖为 RolledBack）。</summary>
    public PolicyRolloutStrategy RolloutStatus { get; init; } = PolicyRolloutStrategy.Promoted;

    /// <summary>
    /// Budget profile override（受限 override；不允许替换 SafetyProfile）。
    /// null = 使用 bundle 中的 BudgetProfile。
    /// </summary>
    public BudgetProfile? BudgetOverride { get; init; }

    /// <summary>
    /// Routing profile override（受限 override；不允许替换 ModelArtifactId）。
    /// null = 使用 bundle 中的 RoutingProfile。
    /// </summary>
    public RoutingProfile? RoutingOverride { get; init; }
}

/// <summary>
/// R19-1：策略包注册表接口。负责解析给定 workspace+collection 当前激活的 bundle。
/// </summary>
/// <remarks>
/// 接口契约最小化：
///   - GetActiveBundleAsync：返回当前激活的 bundle（未找到时返回全局默认 bundle）。
///   - GetActivationAsync：返回激活记录（含 profile override）。
///   - ListBundlesAsync：列出所有 bundle（可选包含 superseded）。
///   - RegisterBundleAsync：注册新 bundle（不激活）。
///   - ActivateAsync：激活 bundle 到 workspace+collection 作用域。
///
/// 实现层可注入 Postgres / InMemory store；契约本身不依赖存储。
/// </remarks>
public interface IPolicyRegistry
{
    /// <summary>获取指定 workspace+collection 当前激活的 bundle。</summary>
    /// <param name="workspaceId">workspace ID。</param>
    /// <param name="collectionId">collection ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>激活的 bundle；未找到时返回全局默认 bundle（实现层决定）。</returns>
    Task<ContextPolicyBundle> GetActiveBundleAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default);

    /// <summary>获取指定 workspace+collection 的激活记录。</summary>
    /// <returns>激活记录；未激活时返回 null。</returns>
    Task<PolicyActivation?> GetActivationAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default);

    /// <summary>列出所有 bundle。</summary>
    /// <param name="includeSuperseded">是否包含已被 supersede 的 bundle。</param>
    Task<IReadOnlyList<ContextPolicyBundle>> ListBundlesAsync(
        bool includeSuperseded = false,
        CancellationToken cancellationToken = default);

    /// <summary>注册新 bundle（不激活）。</summary>
    Task RegisterBundleAsync(
        ContextPolicyBundle bundle,
        CancellationToken cancellationToken = default);

    /// <summary>激活 bundle 到 workspace+collection 作用域。</summary>
    /// <param name="activation">激活记录（含可选 profile override）。</param>
    Task ActivateAsync(
        PolicyActivation activation,
        CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// PolicyOverride（per-request 受限 override）
// ---------------------------------------------------------------------------

/// <summary>
/// R19-1：per-request 策略 override。允许调用方在不替换 bundle 的前提下
/// 调整非安全相关参数。
/// </summary>
/// <remarks>
/// 设计原则（用户澄清 #3）：
///   - 不允许替换 SafetyProfile（安全边界由 bundle 全局决定）。
///   - 不允许替换 ModelArtifactReference（正式模型由 bundle 全局决定）。
///   - 仅允许调整：BudgetProfile 的部分字段 + RoutingProfile.EnableModelScoring。
///   - 字段全为可选：null = 使用 bundle 中的默认 profile。
/// </remarks>
public sealed record ContextPolicyOverride
{
    /// <summary>引用的 bundle ID（null = 使用当前激活 bundle）。</summary>
    /// <remarks>
    /// 此字段仅用于 trace 溯源；不允许通过此字段替换 bundle 中的 SafetyProfile。
    /// </remarks>
    public string? BundleId { get; init; }

    /// <summary>预算 profile override（仅允许调整 TopK / TokenBudget / SectionRatios）。</summary>
    public BudgetProfile? BudgetOverride { get; init; }

    /// <summary>路由 profile override（仅允许调整 EnableModelScoring）。</summary>
    public RoutingProfile? RoutingOverride { get; init; }

    /// <summary>验证 override 是否符合受限规则（不替换安全边界 / 正式模型）。</summary>
    /// <returns>true = 合规；false = 违反受限规则。</returns>
    /// <remarks>
    /// 实现层（PolicyRegistry）在 ActivateAsync 时调用此方法校验 override。
    /// 当前契约层仅定义字段，验证逻辑在 R19-2 实现层提供。
    /// </remarks>
    public bool IsCompliant()
    {
        // 暂时仅做存在性检查；详细字段验证在 R19-2 实现
        return BudgetOverride != null || RoutingOverride != null || BundleId != null;
    }
}
