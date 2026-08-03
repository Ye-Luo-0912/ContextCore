namespace ContextCore.Abstractions;

// ===========================================================================
// 性能自动回退按组件归因契约
//
// 目标：
// 1. 在 IPerformanceMonitor（整体 V2 路径耗时监控）之上，提供更细粒度的组件级
// 性能归因：将 V2 路径拆分为 Provider / Merge / Feature / Inference / Scoring /
// Allocation / Projection 七个组件，分别记录耗时与成功/失败。
// 2. 当某个组件 P95 超过该组件阈值时，标记该组件进入 FallbackActive 状态；
// 下次请求 Engine/Runtime 据此切换到安全回退路径（如 Inference → Deterministic、
// Allocation V2.1 → V2.0、Semantic Provider → Lexical）。
// 3. 组件级回退可自愈：连续 RecoverySamples 低于阈值后清除 FallbackActive。
// 4. 与现有 IPerformanceMonitor.ShouldFallbackToV20 共存：
// - IPerformanceMonitor 负责 V2 整体路径回退（Allocation 子集，向后兼容）。
// - IComponentHealthRegistry 负责细粒度组件回退（覆盖全部 7 个组件）。
// - 调用方可同时查询两者；Allocation 组件回退优先使用 IComponentHealthRegistry，
// IPerformanceMonitor.ShouldFallbackToV20 作为兜底入口保留。
//
// 设计原则：
// 1. 接口极薄：仅 RecordComponentTime / GetComponentHealth / ShouldFallbackComponent /
// RecordComponentFallback / GetDegradedComponents 五个方法。
// 2. 接口可选注入：Engine/Runtime 在 IComponentHealthRegistry 为 null 时
// 保持旧行为（不归因、不回退），向后兼容 之前的测试。
// 3. 接口无副作用：调用方负责实际路径切换；Registry 仅返回布尔值/状态。
// 4. 线程安全：实现应线程安全；多个 Engine/Runtime 实例可能并发调用同一 Registry。
// 5. per-scope 隔离：scopeKey（workspaceId + "/" + collectionId）是隔离单元，
// 一个 scope 的某组件触发回退不影响其他 scope 或其他组件。
// ===========================================================================

/// <summary>
/// V2 路径组件归因枚举。覆盖 V2 决策路径的 7 个核心组件。
/// </summary>
/// <remarks>
/// 组件边界与 V2 路径调用链对应：
/// - Provider：所有 ICandidateProvider 调用总耗时（Phase 1 + Phase 2 Graph 扩展）。
/// - Merge：ICanonicalCandidateMerger 合并 Provider 输出耗时。
/// - Feature：IFeaturePipeline 特征构造（FeatureVector / FeatureBatch）耗时。
/// - Inference：IBatchInferenceEngine 模型推理耗时（在 IUtilityScorer 内部调用）。
/// - Scoring：IUtilityScorer 评分总耗时（含 Inference；Inference 是 Scoring 的子集）。
/// - Allocation：IGlobalAllocator / IAllocatorV2_1 分配耗时。
/// - Projection：IAgentContextProjector 投影耗时（AgentContext 路径）。
/// </remarks>
public enum ComponentKind
{
    /// <summary>所有 ICandidateProvider 调用总耗时（Phase 1 + Phase 2 Graph 扩展）。</summary>
    Provider = 0,

    /// <summary>ICanonicalCandidateMerger 合并 Provider 输出耗时。</summary>
    Merge = 1,

    /// <summary>IFeaturePipeline 特征构造（FeatureVector / FeatureBatch）耗时。</summary>
    Feature = 2,

    /// <summary>IBatchInferenceEngine 模型推理耗时（在 IUtilityScorer 内部调用）。</summary>
    Inference = 3,

    /// <summary>IUtilityScorer 评分总耗时（含 Inference；Inference 是 Scoring 的子集）。</summary>
    Scoring = 4,

    /// <summary>IGlobalAllocator / IAllocatorV2_1 分配耗时。</summary>
    Allocation = 5,

    /// <summary>IAgentContextProjector 投影耗时（AgentContext 路径）。</summary>
    Projection = 6
}

/// <summary>
/// 单个组件一次执行的指标快照。
/// </summary>
/// <param name="Kind">组件类型。</param>
/// <param name="DurationMs">本次执行耗时（毫秒）。</param>
/// <param name="Succeeded">本次是否成功（false 表示异常/超时/降级）。</param>
/// <param name="SampleCount">当前 scope 内该组件的累计样本数（诊断用）。</param>
public sealed record ComponentMetrics(
    ComponentKind Kind,
    double DurationMs,
    bool Succeeded,
    int SampleCount);

/// <summary>
/// 组件健康状态。
/// </summary>
/// <remarks>
/// 状态转移：
/// Healthy → Degraded（P95 接近阈值或单次失败）
/// Degraded → FallbackActive（P95 超过阈值 + 样本数 >= MinSamplesBeforeFallback）
/// FallbackActive → Healthy（连续 RecoverySamples 低于阈值后自愈）
/// Disabled（手动禁用，不参与监控；如 Operator 显式关闭某 provider）
/// </remarks>
public enum ComponentHealthState
{
    /// <summary>健康：P95 低于阈值，未触发回退。</summary>
    Healthy = 0,

    /// <summary>降级：P95 接近阈值或出现单次失败，但尚未触发回退（告警态）。</summary>
    Degraded = 1,

    /// <summary>回退激活：P95 超过阈值且样本数充足，调用方应切换到安全回退路径。</summary>
    FallbackActive = 2,

    /// <summary>禁用：Operator 显式关闭该组件（如关闭某 provider），不参与监控。</summary>
    Disabled = 3
}

/// <summary>
/// 组件健康注册表抽象。提供 per-scope per-component 的耗时记录与健康状态查询。
/// </summary>
/// <remarks>
/// 设计原则：
/// 1. 接口线程安全；实现应支持多 Engine/Runtime 实例并发调用。
/// 2. scope（workspaceId + "/" + collectionId）是隔离单元：一个 scope 的某组件
/// 触发回退不影响其他 scope 或其他组件。
/// 3. 组件回退可恢复：低于阈值的连续样本累积到
/// <see cref="ComponentFallbackPolicy.RecoverySamplesRequired"/> 后，
/// <see cref="ShouldFallbackComponent"/> 返回 false（自愈）。
/// 4. 与 <see cref="IPerformanceMonitor"/> 互补：
/// - IPerformanceMonitor 监控 V2 整体路径（向后兼容 之前的行为）。
/// - IComponentHealthRegistry 监控细粒度组件（新增）。
/// 5. 接口可选注入：Engine/Runtime 在 IComponentHealthRegistry 为 null 时
/// 保持旧行为（不归因、不回退），向后兼容 之前的测试。
/// </remarks>
public interface IComponentHealthRegistry
{
    /// <summary>
    /// 记录一次组件执行的耗时与成功/失败状态。
    /// </summary>
    /// <param name="kind">组件类型。</param>
    /// <param name="durationMs">本次执行耗时（毫秒）。</param>
    /// <param name="succeeded">本次是否成功（false 表示异常/超时/降级）。</param>
    /// <param name="scopeKey">scope 标识（通常是 workspaceId + "/" + collectionId）。</param>
    /// <param name="ct">取消令牌。</param>
    void RecordComponentTime(
        ComponentKind kind,
        double durationMs,
        bool succeeded,
        string scopeKey,
        CancellationToken ct = default);

    /// <summary>
    /// 查询指定 scope 内某组件的当前健康状态。
    /// </summary>
    /// <param name="kind">组件类型。</param>
    /// <param name="scopeKey">scope 标识。</param>
    /// <returns>当前健康状态（无样本时返回 Healthy）。</returns>
    ComponentHealthState GetComponentHealth(ComponentKind kind, string scopeKey);

    /// <summary>
    /// 查询指定 scope 内某组件当前是否应回退（FallbackActive 状态）。
    /// </summary>
    /// <param name="kind">组件类型。</param>
    /// <param name="scopeKey">scope 标识。</param>
    /// <returns>true = 应回退到安全路径；false = 可继续走原路径。</returns>
    bool ShouldFallbackComponent(ComponentKind kind, string scopeKey);

    /// <summary>
    /// 记录一次组件回退事件（调用方因阈值触发而切换到回退路径）。
    /// 用于诊断与可观测性（设置 FallbackActive 状态并记录原因）。
    /// </summary>
    /// <param name="kind">组件类型。</param>
    /// <param name="scopeKey">scope 标识。</param>
    /// <param name="reason">回退原因（如 "p95_exceeded_threshold" / "consecutive_failures"）。</param>
    /// <param name="ct">取消令牌。</param>
    void RecordComponentFallback(
        ComponentKind kind,
        string scopeKey,
        string reason,
        CancellationToken ct = default);

    /// <summary>
    /// 获取指定 scope 内所有处于非 Healthy 状态的组件列表（诊断用）。
    /// </summary>
    /// <param name="scopeKey">scope 标识。</param>
    /// <returns>非 Healthy 组件列表（无则返回空列表）。</returns>
    IReadOnlyList<ComponentKind> GetDegradedComponents(string scopeKey);
}

/// <summary>
/// 单个组件的回退策略配置。
/// </summary>
/// <param name="Kind">组件类型。</param>
/// <param name="MaxP95Ms">触发回退的 P95 阈值（毫秒）。</param>
/// <param name="MinSamplesBeforeFallback">触发回退的最小样本数（避免冷启动抖动误判，默认 3）。</param>
/// <param name="RecoverySamplesRequired">解除回退状态所需的连续低于阈值样本数（默认 5）。</param>
public sealed record ComponentFallbackPolicy(
    ComponentKind Kind,
    double MaxP95Ms,
    int MinSamplesBeforeFallback = 3,
    int RecoverySamplesRequired = 5);

/// <summary>
/// 组件级回退配置。包含每个组件的独立阈值策略。
/// </summary>
/// <remarks>
/// 默认策略基于组件性质设定不同阈值：
/// - Provider：300ms（外部存储调用，延迟较高）
/// - Merge：50ms（内存合并，应极快）
/// - Feature：100ms（特征构造，CPU 密集）
/// - Inference：200ms（模型推理，GPU/CPU 推理）
/// - Scoring：250ms（含 Inference，应略高于 Inference）
/// - Allocation：100ms（内存分配，应极快）
/// - Projection：150ms（投影序列化，CPU 密集）
/// </remarks>
public sealed class ComponentFallbackOptions
{
    /// <summary>每个组件的回退策略（按 ComponentKind 索引）。</summary>
    public Dictionary<ComponentKind, ComponentFallbackPolicy> Policies { get; init; } = CreateDefaultPolicies();

    /// <summary>每个 scope 每个组件保留的最近样本数（ring buffer 容量，默认 16）。</summary>
    public int SampleWindow { get; init; } = 16;

    /// <summary>默认配置（7 个组件各自阈值）。</summary>
    public static ComponentFallbackOptions Default { get; } = new();

    /// <summary>
    /// 创建默认组件策略字典。每组件不同阈值（见类注释）。
    /// </summary>
    /// <returns>按 ComponentKind 索引的策略字典。</returns>
    private static Dictionary<ComponentKind, ComponentFallbackPolicy> CreateDefaultPolicies()
    {
        return new Dictionary<ComponentKind, ComponentFallbackPolicy>
        {
            [ComponentKind.Provider] = new ComponentFallbackPolicy(ComponentKind.Provider, MaxP95Ms: 300.0),
            [ComponentKind.Merge] = new ComponentFallbackPolicy(ComponentKind.Merge, MaxP95Ms: 50.0),
            [ComponentKind.Feature] = new ComponentFallbackPolicy(ComponentKind.Feature, MaxP95Ms: 100.0),
            [ComponentKind.Inference] = new ComponentFallbackPolicy(ComponentKind.Inference, MaxP95Ms: 200.0),
            [ComponentKind.Scoring] = new ComponentFallbackPolicy(ComponentKind.Scoring, MaxP95Ms: 250.0),
            [ComponentKind.Allocation] = new ComponentFallbackPolicy(ComponentKind.Allocation, MaxP95Ms: 100.0),
            [ComponentKind.Projection] = new ComponentFallbackPolicy(ComponentKind.Projection, MaxP95Ms: 150.0)
        };
    }

    /// <summary>
    /// 获取指定组件的策略（未配置时返回该组件的默认策略）。
    /// </summary>
    /// <param name="kind">组件类型。</param>
    /// <returns>该组件的回退策略。</returns>
    public ComponentFallbackPolicy GetPolicy(ComponentKind kind)
    {
        return Policies.TryGetValue(kind, out var policy)
            ? policy
            : new ComponentFallbackPolicy(kind, MaxP95Ms: 500.0);
    }
}
