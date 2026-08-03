namespace ContextCore.Abstractions;

// ===========================================================================
// Model Activation Audit 契约
//
// 目标（对齐 Model Control Plane API Activation Audit）：
// 把 ModelActivationManager.ActivateAsync / Rollback / Retire 等模型生命周期事件
// 显式契约化为可持久化的审计记录，让 HA 场景下激活历史可跨进程查询与对账，
// 让 Champion/Challenger 推进决策可追溯。
//
// 与 AuditLogMiddleware 的边界：
// - AuditLogMiddleware 记录请求级元数据（method/path/status/duration），不感知业务语义。
// - IModelActivationAuditStore 记录模型生命周期业务事件（activate/rollback/retire/shadow），
// 包含 previous_model_id / operator / reason 等业务字段。
//
// 设计原则：
// 1. 契约层不引入存储 I/O：所有抽象为进程内接口，实现层可注入持久化 store。
// 2. 不可变语义：审计记录一旦写入不可修改（append-only）。
// 3. 不抛异常：失败由调用方记录到日志，不影响激活主流程。
// ===========================================================================

/// <summary>
/// 模型激活审计存储 — 管理模型生命周期事件的审计记录。
/// </summary>
/// <remarks>
/// 实现层可在 DI 中注册为持久化（Postgres）或 in-memory（默认）。
/// 调用方应在 ActivateAsync / Rollback / Retire 等关键路径中调用 AppendAsync 追加审计记录。
/// </remarks>
public interface IModelActivationAuditStore
{
    /// <summary>
    /// 追加一条审计记录（append-only，不可修改）。
    /// </summary>
    /// <param name="entry">审计记录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask AppendAsync(ModelActivationAuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按模型工件 ID 查询审计历史（按时间升序）。
    /// </summary>
    /// <param name="modelArtifactId">模型工件 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>审计记录列表（升序）。</returns>
    ValueTask<IReadOnlyList<ModelActivationAuditEntry>> ListByModelAsync(
        string modelArtifactId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出全部审计记录（按时间升序）。
    /// </summary>
    /// <param name="take">返回前 N 条；&lt;=0 时返回全部（上限由实现决定）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>审计记录列表（升序）。</returns>
    ValueTask<IReadOnlyList<ModelActivationAuditEntry>> ListAllAsync(
        int take = 100,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 模型生命周期审计操作种类。
/// </summary>
public enum ModelActivationOperation : byte
{
    /// <summary>注册新模型工件。</summary>
    Register = 0,

    /// <summary>验证模型（schema/calibration/ONNX 格式）。</summary>
    Validate = 1,

    /// <summary>预热模型（warmup）。</summary>
    Warmup = 2,

    /// <summary>进入影子模式（不替换 active）。</summary>
    Shadow = 3,

    /// <summary>激活模型（热切换，替换 active）。</summary>
    Activate = 4,

    /// <summary>回滚到上一个 active 模型。</summary>
    Rollback = 5,

    /// <summary>退役模型。</summary>
    Retire = 6
}

/// <summary>
/// 单条模型激活审计记录。
/// </summary>
public sealed record ModelActivationAuditEntry
{
    /// <summary>审计记录 ID（GUID，由 store 在 AppendAsync 时写入）。</summary>
    public required string AuditId { get; init; }

    /// <summary>事件发生时间（UTC）。</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>关联的模型工件 ID。</summary>
    public required string ModelArtifactId { get; init; }

    /// <summary>关联的模型名（用于按模型名查询）。</summary>
    public required string ModelName { get; init; }

    /// <summary>操作种类。</summary>
    public required ModelActivationOperation Operation { get; init; }

    /// <summary>操作前 active 模型工件 ID（首次激活时为 null）。</summary>
    public string? PreviousModelArtifactId { get; init; }

    /// <summary>操作发起者标识（用户/服务名；可空）。</summary>
    public string? Operator { get; init; }

    /// <summary>操作原因（人类可读；可空）。</summary>
    public string? Reason { get; init; }

    /// <summary>操作是否成功（false 时 ErrorMessage 填充失败原因）。</summary>
    public required bool Succeeded { get; init; }

    /// <summary>失败原因（Succeeded=true 时为 null）。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>发起操作的节点标识（HA 多节点场景下用于一致性对账）。</summary>
    public string? NodeId { get; init; }
}
