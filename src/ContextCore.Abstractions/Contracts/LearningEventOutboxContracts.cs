namespace ContextCore.Abstractions;

// ===========================================================================
// Learning Loop Durable Outbox 契约层
//
// 目标（修复 Learning Loop 静默丢训练数据问题）：
//   1. 定义 LearningEventOutboxRecord：持久化的 Decision 物化事件，承载完整 payload。
//   2. 定义 ILearningEventOutboxStore：outbox 存储抽象（Enqueue / AcquirePending / Ack / DeadLetter）。
//   3. 定义 LearningMaterializationMetricsSnapshot：可观测性指标快照。
//
// 设计原则：
//   1. 复用 RelationOutboxStore 的 lease + retry + CAS 模式（SELECT FOR UPDATE SKIP LOCKED）。
//   2. 契约层不引入存储 I/O；持久化由 IPersistent 标记接口区分。
//   3. Postgres provider 注册此接口；FileSystem/InMemory 不注册——Dispatcher 检测到 null
//      时回退到 in-memory bounded Channel + fixed worker（非持久但消除 Task.Run）。
// ===========================================================================

/// <summary>
/// Learning Loop Durable Outbox 记录。承载一次 Decision 物化事件的完整 payload 与生命周期元数据。
/// </summary>
/// <remarks>
/// 语义：
/// <list type="bullet">
/// <item><see cref="State"/> = <see cref="LearningEventOutboxStates.Pending"/>：已入队，待 worker 调度。</item>
/// <item><see cref="State"/> = <see cref="LearningEventOutboxStates.Processing"/>：worker 已取出并开始物化（持有租约）。</item>
/// <item><see cref="State"/> = <see cref="LearningEventOutboxStates.Acked"/>：物化完成，Utility Ledger 已写入。</item>
/// <item><see cref="State"/> = <see cref="LearningEventOutboxStates.DeadLettered"/>：达到 <see cref="MaxRetryCount"/> 仍无法物化，进入死信。</item>
/// </list>
/// 一条 outbox 记录对应一次 ContextDecisionResult（per-decision 粒度）。
/// </remarks>
public sealed class LearningEventOutboxRecord
{
    /// <summary>Outbox 记录唯一标识符。</summary>
    public string EventId { get; init; } = string.Empty;

    /// <summary>所属工作空间 ID（空字符串 = 全局/默认）。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>所属集合 ID（空字符串 = 全局/默认）。</summary>
    public string CollectionId { get; init; } = string.Empty;

    /// <summary>关联的 Decision ID（与 ContextDecisionResult.RequestId 对应）。</summary>
    public string DecisionId { get; init; } = string.Empty;

    /// <summary>
    /// 序列化的 ContextDecisionResult（JSON 字符串）。worker 反序列化后调用 MaterializeAsync。
    /// </summary>
    public string Payload { get; init; } = string.Empty;

    /// <summary>当前状态。</summary>
    public string State { get; init; } = LearningEventOutboxStates.Pending;

    /// <summary>已重试次数。</summary>
    public int RetryCount { get; init; }

    /// <summary>最大重试次数。超过后状态转为 <see cref="LearningEventOutboxStates.DeadLettered"/>。</summary>
    public int MaxRetryCount { get; init; } = 5;

    /// <summary>创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>最近更新时间（UTC）。</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>物化完成时间（State=Acked 时设置）。</summary>
    public DateTimeOffset? ProcessedAt { get; init; }

    /// <summary>当前持有此记录的 worker 标识。</summary>
    public string? LeaseOwner { get; init; }

    /// <summary>租约到期时间。超时后其他 worker 可抢占。</summary>
    public DateTimeOffset? LeaseExpiresAt { get; init; }

    /// <summary>
    /// 当前租约的唯一 token（每次 AcquirePendingAsync 生成新 GUID）。
    /// <see cref="ILearningEventOutboxStore.MarkAckedAsync"/> / <see cref="ILearningEventOutboxStore.MarkFailedAsync"/>
    /// / <see cref="ILearningEventOutboxStore.RenewLeaseAsync"/> 必须传入此 token，store 通过 CAS 校验
    /// 仅持有者可 Ack/Nack/Renew——防止旧 Worker 在 lease 过期被抢占后越权 Ack 新 Worker 的 lease。
    /// </summary>
    public string? LeaseToken { get; init; }

    /// <summary>失败时的错误信息。</summary>
    public string? LastError { get; init; }

    /// <summary>死信原因（State=DeadLettered 时设置）。</summary>
    public string? DeadLetterReason { get; init; }
}

/// <summary>Learning Loop Durable Outbox 记录的状态常量。</summary>
public static class LearningEventOutboxStates
{
    /// <summary>已入队，待 worker 调度。</summary>
    public const string Pending = "Pending";

    /// <summary>worker 已取出并开始物化（持有租约）。</summary>
    public const string Processing = "Processing";

    /// <summary>物化完成，Utility Ledger 已写入。</summary>
    public const string Acked = "Acked";

    /// <summary>达到 MaxRetryCount 仍无法物化，进入死信。</summary>
    public const string DeadLettered = "DeadLettered";
}

/// <summary>
/// Learning Loop Durable Outbox 存储契约。
/// Postgres provider 注册此接口；FileSystem/InMemory 不注册，
/// LearningMaterializationDispatcher 检测到 null 时回退到 in-memory bounded Channel。
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
/// 让多 worker 并发调度不会重复取出同一记录——与 IRelationOutboxStore 一致。
/// </item>
/// </list>
/// </remarks>
public interface ILearningEventOutboxStore
{
    /// <summary>
    /// 入队一条 outbox 记录。当 scope 非空时与调用方事务原子提交；为空时使用独立事务（best-effort）。
    /// </summary>
    [StoreOperation(StoreOperationKind.Write)]
    Task EnqueueAsync(
        LearningEventOutboxRecord record,
        IWriteTransactionScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 取出一批 state=Pending（或 Processing 但租约过期）的 outbox 记录。
    /// 调用方负责在处理完成后调用 <see cref="MarkAckedAsync"/> 或 <see cref="MarkFailedAsync"/>。
    /// </summary>
    /// <param name="limit">最多取出的记录数。</param>
    /// <param name="owner">当前 worker 标识（用于租约持有者识别）。</param>
    /// <param name="leaseDuration">租约有效期。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>取出的 outbox 记录列表（可能为空）。每条记录的 <see cref="LearningEventOutboxRecord.LeaseToken"/>
    /// 为本次租约的唯一 token，调用方需保留并在 Ack/Nack/Renew 时回传。</returns>
    [StoreOperation(StoreOperationKind.Write)]
    Task<IReadOnlyList<LearningEventOutboxRecord>> AcquirePendingAsync(
        int limit,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 标记记录为已物化完成（state=Acked）。CAS 语义：仅当当前 state=Processing 且 lease_token 匹配时生效。
    /// </summary>
    /// <param name="eventId">记录事件 ID。</param>
    /// <param name="leaseToken">调用方在 <see cref="AcquirePendingAsync"/> 获取的 lease token；不匹配则 0 行受影响。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true=已成功 Ack；false=lease 已被其他 worker 抢占或已 Ack/Nack，调用方应放弃该记录。</returns>
    [StoreOperation(StoreOperationKind.Write)]
    Task<bool> MarkAckedAsync(
        string eventId,
        string leaseToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 标记记录为失败或重试。CAS 语义：仅当当前 state=Processing 且 lease_token 匹配时生效。
    /// 当 retryCount+1 超过 maxRetryCount 时转为 DeadLettered，否则回退为 Pending 等待下次调度。
    /// </summary>
    /// <param name="eventId">记录事件 ID。</param>
    /// <param name="leaseToken">调用方在 <see cref="AcquirePendingAsync"/> 获取的 lease token；不匹配则 0 行受影响。</param>
    /// <param name="errorMessage">失败错误信息。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true=已成功 MarkFailed；false=lease 已被其他 worker 抢占或已 Ack/Nack。</returns>
    [StoreOperation(StoreOperationKind.Write)]
    Task<bool> MarkFailedAsync(
        string eventId,
        string leaseToken,
        string errorMessage,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 续约当前持有的租约。CAS 语义：仅当 lease_token 匹配且 state=Processing 时生效。
    /// 返回 false 表示租约已过期或被其他 worker 抢占，调用方应停止处理。
    /// </summary>
    /// <param name="eventId">记录事件 ID。</param>
    /// <param name="leaseToken">调用方在 <see cref="AcquirePendingAsync"/> 获取的 lease token；不匹配则 0 行受影响。</param>
    /// <param name="leaseDuration">续约时长。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [StoreOperation(StoreOperationKind.Write)]
    Task<bool> RenewLeaseAsync(
        string eventId,
        string leaseToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Perf-7：批量续约当前实例持有的多个 lease。由 batched heartbeat coordinator 调用，
    /// 替代每 record 独立 heartbeat Task，消除高积压下大量 Task/Timer/DB UPDATE。
    /// </summary>
    /// <param name="leases">要续约的 (eventId, leaseToken) 集合。</param>
    /// <param name="leaseDuration">续约时长。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功续约的 eventId 集合（失败的表示 lease 已丢失或被抢占）。</returns>
    [StoreOperation(StoreOperationKind.Write)]
    Task<IReadOnlySet<string>> RenewLeaseBatchAsync(
        IReadOnlyList<(string EventId, string LeaseToken)> leases,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按 state 分组统计记录数。用于诊断与可观测性。
    /// </summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<IReadOnlyDictionary<string, int>> CountByStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 查询最后成功物化时间（state=Acked 的最大 processed_at）。
    /// </summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<DateTimeOffset?> GetLastSuccessAtAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Learning Loop 物化可观测性指标快照。
/// </summary>
public sealed record LearningMaterializationMetricsSnapshot
{
    /// <summary>最后成功物化时间（UTC）。null = 从未成功物化。</summary>
    public DateTimeOffset? LastSuccessAt { get; init; }

    /// <summary>outbox 中待处理事件数（state=Pending）。</summary>
    public long PendingEvents { get; init; }

    /// <summary>重试失败次数（累计 MarkFailed 调用次数）。</summary>
    public long FailedEvents { get; init; }

    /// <summary>死信事件数（state=DeadLettered）。</summary>
    public long DeadLetterCount { get; init; }

    /// <summary>物化延迟样本数（用于计算 P50/P95/P99）。</summary>
    public long MaterializationLagSampleCount { get; init; }

    /// <summary>物化延迟 P50（毫秒）。</summary>
    public double MaterializationLagP50Ms { get; init; }

    /// <summary>物化延迟 P95（毫秒）。</summary>
    public double MaterializationLagP95Ms { get; init; }

    /// <summary>物化延迟 P99（毫秒）。</summary>
    public double MaterializationLagP99Ms { get; init; }

    /// <summary>当前处理中事件数（state=Processing）。</summary>
    public long ProcessingEvents { get; init; }

    /// <summary>累计已物化事件数（state=Acked）。</summary>
    public long AckedEvents { get; init; }
}

/// <summary>
/// Learning Loop 物化配置。控制 Durable Outbox worker 与 bounded Channel 行为。
/// </summary>
public sealed class LearningMaterializationOptions
{
    /// <summary>是否启用 worker。默认 false——生产需显式启用。</summary>
    public bool Enabled { get; set; }

    /// <summary>是否在服务启动时立即执行一次 outbox 扫描。默认 false。</summary>
    public bool RunOnStartup { get; set; }

    /// <summary>worker 轮询间隔（秒）。默认 5。仅 Postgres outbox 路径使用。</summary>
    public int IntervalSeconds { get; set; } = 5;

    /// <summary>单次轮询最多取出的 outbox 记录数。默认 32。</summary>
    public int BatchSize { get; set; } = 32;

    /// <summary>固定 worker 数（并行物化任务数）。默认 2。</summary>
    public int WorkerCount { get; set; } = 2;

    /// <summary>bounded Channel 容量上限。超过后拒绝入队（背压）。默认 1024。</summary>
    public int ChannelCapacity { get; set; } = 1024;

    /// <summary>outbox 调度租约有效期。默认 2 分钟。</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>心跳续约间隔。默认 30 秒。</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>最大重试次数（写入 outbox 记录的 max_retry_count）。默认 5。</summary>
    public int MaxRetryCount { get; set; } = 5;

    /// <summary>
    /// worker 实例标识（用于 outbox 记录的 lease_owner 字段）。
    /// 留空时使用主机名 + PID 自动生成。
    /// </summary>
    public string OwnerId { get; set; } = string.Empty;
}

