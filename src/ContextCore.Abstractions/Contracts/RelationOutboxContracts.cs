using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

/// <summary>
/// P1-5：关系写入 outbox 操作类型。标识 outbox 记录对应的写入语义。
/// </summary>
public enum RelationOutboxOperationKind
{
    /// <summary>新增或更新关系（对应 BatchUpsertAsync）。</summary>
    Upsert,

    /// <summary>删除关系（对应 DeleteAsync）。</summary>
    Delete
}

/// <summary>
/// P1-5：关系写入 outbox 记录。承载单条关系的写入意图与生命周期元数据。
/// </summary>
/// <remarks>
/// 语义：
/// <list type="bullet">
/// <item><see cref="State"/> = <see cref="RelationOutboxStates.Pending"/>：已入队，待 worker 调度。</item>
/// <item><see cref="State"/> = <see cref="RelationOutboxStates.Dispatched"/>：worker 已取出并开始处理（持有租约）。</item>
/// <item><see cref="State"/> = <see cref="RelationOutboxStates.Applied"/>：worker 验证关系已落库或已删除，标记完成。</item>
/// <item><see cref="State"/> = <see cref="RelationOutboxStates.Failed"/>：达到 <see cref="MaxRetryCount"/> 仍无法应用。</item>
/// </list>
/// 一条 outbox 记录对应一条 <see cref="ContextRelation"/>（per-relation 粒度，与 context_jobs 的 per-job 模型一致）。
/// </remarks>
public sealed class RelationOutboxRecord
{
    /// <summary>Outbox 记录唯一标识符。</summary>
    public string OutboxId { get; init; } = string.Empty;

    /// <summary>所属工作空间 ID。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>所属集合 ID。</summary>
    public string CollectionId { get; init; } = string.Empty;

    /// <summary>关联的关系 ID（upsert 时为 relation.Id；delete 时为待删除 relation 的 ID）。</summary>
    public string RelationId { get; init; } = string.Empty;

    /// <summary>操作类型（upsert / delete）。</summary>
    public RelationOutboxOperationKind OperationKind { get; init; } = RelationOutboxOperationKind.Upsert;

    /// <summary>
    /// 来源场景标识（与 <see cref="ContextRelation.Provenance"/> 同源）：
    /// "ingest" / "compression" / "promotion" / "lifecycle-review"。
    /// worker 按此字段过滤需处理的 outbox 记录。
    /// </summary>
    public string Provenance { get; init; } = string.Empty;

    /// <summary>
    /// 序列化的 <see cref="ContextRelation"/>（upsert 时为完整 relation；delete 时为 null/空）。
    /// 用于 worker 在发现落库缺失时回放写入。
    /// </summary>
    public ContextRelation? Payload { get; init; }

    /// <summary>当前状态。</summary>
    public string State { get; init; } = RelationOutboxStates.Pending;

    /// <summary>已重试次数。</summary>
    public int RetryCount { get; init; }

    /// <summary>最大重试次数。超过后状态转为 <see cref="RelationOutboxStates.Failed"/>。</summary>
    public int MaxRetryCount { get; init; } = 3;

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>最近更新时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>worker 取出时间（开始处理）。</summary>
    public DateTimeOffset? DispatchedAt { get; init; }

    /// <summary>worker 标记完成时间。</summary>
    public DateTimeOffset? AppliedAt { get; init; }

    /// <summary>当前持有此记录的 worker 标识。</summary>
    public string? LeaseOwner { get; init; }

    /// <summary>租约到期时间。超时后其他 worker 可抢占。</summary>
    public DateTimeOffset? LeaseExpiresAt { get; init; }

    /// <summary>worker 最后心跳时间。</summary>
    public DateTimeOffset? LastHeartbeatAt { get; init; }

    /// <summary>失败时的错误信息。</summary>
    public string? LastErrorMessage { get; init; }
}

/// <summary>P1-5：关系写入 outbox 记录的状态常量。</summary>
public static class RelationOutboxStates
{
    /// <summary>已入队，待 worker 调度。</summary>
    public const string Pending = "Pending";

    /// <summary>worker 已取出并开始处理（持有租约）。</summary>
    public const string Dispatched = "Dispatched";

    /// <summary>worker 验证关系已落库或已删除，标记完成。</summary>
    public const string Applied = "Applied";

    /// <summary>达到 MaxRetryCount 仍无法应用。</summary>
    public const string Failed = "Failed";
}

/// <summary>
/// P1-5：关系写入 outbox 存储契约。
/// Postgres provider 注册此接口；FileSystem/InMemory 不注册，worker 检测到 null 时跳过 outbox 调度。
/// </summary>
/// <remarks>
/// 原子性契约：
/// <list type="bullet">
/// <item>
/// <see cref="EnqueueAsync"/> 接受可选的 <see cref="IWriteTransactionScope"/>：
/// 当 scope 非空时，outbox 行插入与调用方的事务共享同一 Postgres 事务——commit 时一起持久化，rollback 时一起回滚。
/// 当 scope 为空时，使用独立短生命周期事务（best-effort，非原子）。
/// </item>
/// <item>
/// <see cref="AcquirePendingAsync"/> 使用 SELECT ... FOR UPDATE SKIP LOCKED 语义，
/// 让多 worker 并发调度不会重复取出同一记录——与 <see cref="ILeasedJobQueue"/> 一致。
/// </item>
/// </list>
/// </remarks>
public interface IRelationOutboxStore
{
    /// <summary>
    /// 入队一条 outbox 记录。当 scope 非空时与调用方事务原子提交；为空时使用独立事务（best-effort）。
    /// </summary>
    /// <param name="record">Outbox 记录。</param>
    /// <param name="scope">可选的事务作用域（Postgres provider 下传入则原子提交）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [StoreOperation(StoreOperationKind.Write)]
    Task EnqueueAsync(
        RelationOutboxRecord record,
        IWriteTransactionScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量入队多条 outbox 记录。语义同 <see cref="EnqueueAsync"/> 但单次往返。
    /// </summary>
    [StoreOperation(StoreOperationKind.Write)]
    Task EnqueueBatchAsync(
        IReadOnlyList<RelationOutboxRecord> records,
        IWriteTransactionScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 取出一批 state=Pending（或 Dispatched 但租约过期）的 outbox 记录。
    /// 调用方负责在处理完成后调用 <see cref="MarkAppliedAsync"/> 或 <see cref="MarkFailedAsync"/>。
    /// </summary>
    /// <param name="limit">最多取出的记录数。</param>
    /// <param name="owner">当前 worker 标识（用于租约持有者识别）。</param>
    /// <param name="leaseDuration">租约有效期。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>取出的 outbox 记录列表（可能为空）。</returns>
    [StoreOperation(StoreOperationKind.Write)]
    Task<IReadOnlyList<RelationOutboxRecord>> AcquirePendingAsync(
        int limit,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 标记记录为已应用（state=Applied）。CAS 语义：仅当当前 state=Dispatched 时生效。
    /// </summary>
    [StoreOperation(StoreOperationKind.Write)]
    Task<bool> MarkAppliedAsync(
        string outboxId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 标记记录为失败或重试。CAS 语义：仅当当前 state=Dispatched 时生效。
    /// 当 retryCount+1 超过 maxRetryCount 时转为 Failed，否则回退为 Pending 等待下次调度。
    /// </summary>
    [StoreOperation(StoreOperationKind.Write)]
    Task<bool> MarkFailedAsync(
        string outboxId,
        string errorMessage,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 续约当前持有的租约。返回 false 表示租约已过期或被其他 worker 抢占，调用方应停止处理。
    /// </summary>
    [StoreOperation(StoreOperationKind.Write)]
    Task<bool> RenewHeartbeatAsync(
        string outboxId,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 统计 state=Dispatched 且 lease_expires_at 已过期的记录数。
    /// 用于 worker 自检与可观测性。
    /// </summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<int> CountStaleLeasesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 按 state 分组统计记录数。用于诊断与可观测性。
    /// </summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<IReadOnlyDictionary<string, int>> CountByStateAsync(CancellationToken cancellationToken = default);
}
