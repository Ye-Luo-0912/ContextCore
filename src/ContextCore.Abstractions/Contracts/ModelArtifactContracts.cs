namespace ContextCore.Abstractions;

// ===========================================================================
// R29 WP-A-1：Model Artifact Registry 契约
//
// 目标（对齐 R29 Production Intelligence Spec §8 Workstream A）：
//   1. 把 ModelExecutionSnapshot 中的 ModelArtifactId / ModelVersion / FeatureSchemaVersion /
//      CalibrationVersion / InferenceEngineKind / ContentHash 提升为可注册、可查询的工件元数据，
//      让生产环境能从 PostgreSQL 加载权威模型描述符，而非依赖代码硬编码。
//   2. IModelArtifactRegistry 抽象注册/查询能力；实现层可注入持久化 store
//      （PostgresModelArtifactRegistry）或 in-memory（默认）。
//   3. ModelArtifactDescriptor 为不可变 record；同一 ModelArtifactId 仅允许注册一次，
//      新版本通过新 ModelArtifactId 注册实现（与 FeatureSchema 不可变语义一致）。
//   4. IPersistentModelArtifactRegistry 标记接口（不添加成员），让消费方显式区分
//      持久化能力与 in-memory 回退，与 IPersistentToolDispatchJournal /
//      IPersistentKernelResultOutbox / IPersistentAgentCheckpointStore 模式对齐。
//
// 设计边界：
//   - 契约层不引入存储 I/O：所有抽象为进程内接口，实现层可注入持久化 store。
//   - 不与 IBatchInferenceEngine 耦合：descriptor 描述"模型工件应是什么"，
//     引擎实现（如 OnnxInferenceEngine）在构造时从 registry 读取 descriptor 并自我描述。
//   - ContentHash 由调用方计算并传入（通常为模型文件的 SHA-256）；
//     registry 不负责哈希计算，仅负责存储与查询。
// ===========================================================================

/// <summary>
/// R29 WP-A-1：Model Artifact 注册表 — 管理模型工件描述符的注册与查询。
/// </summary>
/// <remarks>
/// <b>使用模式</b>：
/// <code>
/// var registry = host.Services.GetRequiredService&lt;IModelArtifactRegistry&gt;();
/// var descriptor = new ModelArtifactDescriptor { ... };
/// registry.Register(descriptor);
/// var resolved = registry.Get("model-text-classifier-v1.2.0");
/// </code>
/// <para>
/// 与 <see cref="IFeatureRegistry"/> 的关系：FeatureRegistry 管理特征 schema 版本，
/// ModelArtifactRegistry 管理完整模型工件描述符（包含 schema 版本引用、校准版本、引擎类型、内容哈希等）。
/// 两者互补：descriptor.FeatureSchemaVersion 引用 IFeatureRegistry 中已注册的 schema。
/// </para>
/// </remarks>
public interface IModelArtifactRegistry
{
    /// <summary>
    /// 注册模型工件描述符。同一 ModelArtifactId 仅允许注册一次；
    /// 重复注册时抛 <see cref="InvalidOperationException"/>（与 FeatureSchema 不可变语义一致）。
    /// </summary>
    /// <param name="descriptor">待注册的模型工件描述符。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask RegisterAsync(ModelArtifactDescriptor descriptor, CancellationToken cancellationToken = default);

    /// <summary>按 ModelArtifactId 获取已注册的描述符；不存在时返回 null。</summary>
    ValueTask<ModelArtifactDescriptor?> GetAsync(string modelArtifactId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按 ModelName 获取该模型名的最新版本描述符（按 <see cref="ModelArtifactDescriptor.RegisteredAt"/> 倒序取首条）。
    /// 不存在时返回 null。
    /// </summary>
    ValueTask<ModelArtifactDescriptor?> GetLatestAsync(string modelName, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按 ModelName 列出所有已注册版本（按 RegisteredAt 升序）。
    /// 不存在时返回空列表。
    /// </summary>
    ValueTask<IReadOnlyList<ModelArtifactDescriptor>> ListByVersionAsync(string modelName, CancellationToken cancellationToken = default);

    /// <summary>列出所有已注册的模型工件描述符（按 RegisteredAt 升序）。</summary>
    ValueTask<IReadOnlyList<ModelArtifactDescriptor>> ListAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// R29 WP-A-1：模型工件描述符。描述一次模型注册的完整元数据，
/// 对应 <see cref="ModelExecutionSnapshot"/> 中的六个字段。
/// </summary>
/// <remarks>
/// 不可变 record：注册后字段不可修改（registry 实现应保证存储语义与 record 不可变性一致）。
/// <para>
/// 字段对应关系：
/// <list type="table">
/// <item><term>ModelArtifactId</term><description><see cref="ModelExecutionSnapshot.ModelArtifactId"/></description></item>
/// <item><term>ModelName</term><description>逻辑模型名（可多版本共享）</description></item>
/// <item><term>ModelVersion</term><description><see cref="ModelExecutionSnapshot.ModelVersion"/></description></item>
/// <item><term>FeatureSchemaVersion</term><description><see cref="ModelExecutionSnapshot.FeatureSchemaVersion"/></description></item>
/// <item><term>CalibrationVersion</term><description><see cref="ModelExecutionSnapshot.CalibrationVersion"/></description></item>
/// <item><term>EngineKind</term><description><see cref="ModelExecutionSnapshot.EngineKind"/></description></item>
/// <item><term>ContentHash</term><description><see cref="ModelExecutionSnapshot.ContentHash"/></description></item>
/// </list>
/// </para>
/// </remarks>
public sealed record ModelArtifactDescriptor
{
    /// <summary>
    /// 模型工件 ID（全局唯一，对应 RoutingProfile.ModelArtifactId）。
    /// 推荐格式：<c>{modelName}-{version}-{shortHash}</c>，如 <c>text-classifier-1.2.0-a1b2c3d4</c>。
    /// </summary>
    public required string ModelArtifactId { get; init; }

    /// <summary>
    /// 逻辑模型名（多个版本共享同一名称；GetLatest / ListByVersion 按此字段查询）。
    /// </summary>
    public required string ModelName { get; init; }

    /// <summary>模型版本号（语义化版本，如 "1.2.0"）。</summary>
    public required string ModelVersion { get; init; }

    /// <summary>
    /// 特征 schema 版本号（对应 IFeatureRegistry 中已注册的 schema）。
    /// 用于推理前验证输入特征与 schema 一致性（WP-A-4 FeatureSchemaValidator 消费）。
    /// </summary>
    public required string FeatureSchemaVersion { get; init; }

    /// <summary>
    /// 校准版本号（对应 ICalibrationService 中已注册的参数版本）。
    /// 用于校准验证（WP-A-3 ICalibrationValidator 消费）。
    /// </summary>
    public required string CalibrationVersion { get; init; }

    /// <summary>推理引擎类型（RealModel / DeterministicReplay / Disabled）。</summary>
    public required InferenceEngineKind EngineKind { get; init; }

    /// <summary>
    /// 模型工件内容哈希（通常为模型文件的 SHA-256）。
    /// 用于检测跨节点 HA 不一致（不同节点加载了不同 ContentHash）。
    /// </summary>
    public required string ContentHash { get; init; }

    /// <summary>
    /// 模型工件存储路径或 URI（如 onnx 文件路径 / 远程模型服务 URL）。
    /// 可空：DeterministicReplay 引擎可能无对应文件。
    /// </summary>
    public string? ArtifactPath { get; init; }

    /// <summary>可选的模型描述（人类可读）。</summary>
    public string? Description { get; init; }

    /// <summary>注册时间戳（由 registry 在 RegisterAsync 时写入）。</summary>
    public required DateTimeOffset RegisteredAt { get; init; }
}

/// <summary>
/// R29 WP-A-1：持久化模型工件注册表标记接口。
/// 不添加成员；让消费方（如 HA 部署的 OnnxInferenceEngine 启动加载器）显式区分
/// 持久化 registry（PostgreSQL）与 in-memory 默认实现。
/// </summary>
/// <remarks>
/// 与 <c>IPersistentToolDispatchJournal</c> / <c>IPersistentKernelResultOutbox</c> /
/// <c>IPersistentAgentCheckpointStore</c> 模式对齐：marker interface 仅继承基础契约，
/// DefaultAgentKernel 等消费方需感知持久化能力时按此 marker 解析。
/// </remarks>
public interface IPersistentModelArtifactRegistry : IModelArtifactRegistry
{
}

// ===========================================================================
// R29 WP-A-2：Desired Model State Store 契约（HA 多节点一致性）
//
// 目标（对齐 R29 Production Intelligence Spec §8 Workstream A）：
//   1. 在 HA 部署中，Model Control Plane 的 Activate/Deactivate 操作需跨节点同步。
//   2. DesiredModelStateStore 存储"期望状态"（Active/Inactive），由各节点的
//      ReconcilerWorker 定期拉取并应用到本地 ModelActivationManager。
//   3. Generation 字段用于乐观并发控制：仅当本地 Generation < 远端 Generation 时才应用。
//
// 设计边界：
//   - 契约层不引入存储 I/O：所有抽象为进程内接口，实现层可注入持久化 store。
//   - 不与 IModelActivationManager 耦合：store 仅负责存储/查询期望状态，
//     ReconcilerWorker 负责读取并调用 IModelActivationManager 应用变更。
// ===========================================================================

/// <summary>
/// R29 WP-A-2：期望模型状态。描述某模型在 HA 集群中的目标状态。
/// </summary>
/// <remarks>
/// 不可变 record：状态变更通过写入新记录实现（与 ModelArtifactDescriptor 不可变语义一致）。
/// <para>
/// 状态机：
/// <list type="bullet">
/// <item><term>Active</term><description>模型应被激活（Champion）</description></item>
/// <item><term>Inactive</term><description>模型应被停用（非 Champion）</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed record DesiredModelState
{
    /// <summary>模型工件 ID（对应 ModelArtifactDescriptor.ModelArtifactId）。</summary>
    public required string ModelId { get; init; }

    /// <summary>期望状态："Active" 或 "Inactive"。</summary>
    public required string DesiredState { get; init; }

    /// <summary>乐观并发控制版本号（单调递增）。</summary>
    public required long Generation { get; init; }

    /// <summary>模型内容哈希（用于检测跨节点不一致）。</summary>
    public required string ContentHash { get; init; }

    /// <summary>状态更新时间戳（UTC）。</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>最后更新者标识（节点 ID 或服务实例 ID）。</summary>
    public required string UpdatedBy { get; init; }
}

/// <summary>
/// R29 WP-A-2：Desired Model State Store — 存储 HA 集群中各模型的期望状态。
/// </summary>
/// <remarks>
/// <b>使用模式</b>：
/// <code>
/// // Model Control Plane 写入期望状态
/// await store.SetAsync(new DesiredModelState {
///     ModelId = "model-v1",
///     DesiredState = "Active",
///     Generation = 42,
///     ContentHash = "sha256:abc...",
///     UpdatedAt = DateTimeOffset.UtcNow,
///     UpdatedBy = "node-1"
/// });
///
/// // ReconcilerWorker 读取并应用
/// var state = await store.GetAsync("model-v1");
/// if (state != null && state.Generation > localGeneration) {
///     await activationManager.ActivateAsync(state.ModelId);
/// }
/// </code>
/// </remarks>
public interface IDesiredModelStateStore
{
    /// <summary>按 ModelId 获取期望状态；不存在时返回 null。</summary>
    ValueTask<DesiredModelState?> GetAsync(string modelId, CancellationToken ct = default);

    /// <summary>
    /// 写入期望状态（CAS：仅当 state.Generation > 已存在 Generation 时才更新）。
    /// </summary>
    /// <returns>true 表示已应用；false 表示 Generation 过旧（已被更高 Generation 的写入覆盖），未更新。</returns>
    ValueTask<bool> SetAsync(DesiredModelState state, CancellationToken ct = default);

    /// <summary>列出所有模型的期望状态（用于 ReconcilerWorker 全量同步）。</summary>
    ValueTask<IReadOnlyList<DesiredModelState>> GetAllAsync(CancellationToken ct = default);
}
