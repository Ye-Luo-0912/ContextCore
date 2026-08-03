namespace ContextCore.Abstractions;

// ===========================================================================
// Learning Lease 契约（：Learning Durability）
//
// 背景：
// Learning Loop Durable Outbox（learning_event_outbox）已具备记录级租约
// （ILearningEventOutboxStore.LeaseToken：AcquirePendingAsync 生成唯一 token，
// Ack/Nack/Renew 通过 token CAS 校验持有者），保证多 worker 不重复消费同一事件。
// 但"哪个 worker 实例负责轮询/物化"本身没有池级协调——多实例部署中每个实例
// 都会启动 LearningMaterializationWorker 各自轮询。记录级租约已保证不重复消费，
// 池级租约进一步将物化调度收敛到单一持有者，减少跨实例的 SKIP LOCKED 争用与
// 重复的指标上报。
//
// 本契约定义 worker 池级租约（per-pool 至多一行），与记录级租约互补：
// - 记录级租约：ILearningEventOutboxStore（per-event lease_token，SKIP LOCKED）。
// - 池级租约：ILearningLeaseStore（per-lease_id 至多一行，CAS 获取/续约/释放）。
//
// 实现层对齐 IAgentRunLease / ICanaryLeaderLease 模式：
// - Postgres 实现使用 INSERT ... ON CONFLICT DO UPDATE WHERE expires_at < now()
// 原子抢占过期租约；Renew/Release 通过 lease_token CAS 校验持有者。
// - InMemory 实现（开发/测试）为 ConcurrentDictionary + token CAS。
// ===========================================================================

/// <summary>Learning Materialization worker 池级租约。</summary>
public sealed record LearningLease
{
    /// <summary>租约 ID（pool 标识，如 "learning-materialization"）。</summary>
    public required string LeaseId { get; init; }

    /// <summary>当前持有者标识（实例 ID / 主机名 + PID）。</summary>
    public required string Owner { get; init; }

    /// <summary>
    /// 当前租约的唯一 token（每次获取生成新 GUID）。
    /// <see cref="ILearningLeaseStore.RenewAsync"/> / <see cref="ILearningLeaseStore.ReleaseAsync"/>
    /// 必须传入此 token，store 通过 CAS 校验仅持有者可续约/释放——防止旧实例在租约过期被
    /// 抢占后越权续约新实例的租约。
    /// </summary>
    public required string LeaseToken { get; init; }

    /// <summary>获取时间（UTC）。</summary>
    public required DateTimeOffset AcquiredAt { get; init; }

    /// <summary>租约到期时间（UTC）。超时后其他 worker 可抢占。</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// Learning Materialization worker 池级租约存储契约。
/// </summary>
/// <remarks>
/// 语义与 <see cref="IAgentRunLease"/> 对齐：
/// <list type="bullet">
/// <item><see cref="TryAcquireAsync"/>：无现有行或现有行过期时获取成功；未过期时返回 null（已被其他实例持有）。</item>
/// <item><see cref="RenewAsync"/>：仅当 lease_token 匹配且租约未过期时生效；返回 false 表示已被抢占或过期，调用方应停止处理。</item>
/// <item><see cref="ReleaseAsync"/>：主动让出（DELETE WHERE lease_token 匹配）；0 行受影响不抛异常。</item>
/// <item><see cref="ReapExpiredAsync"/>：清理崩溃实例持有的过期租约（最终释放）。</item>
/// </list>
/// Postgres 实现注册后覆盖 CoreExtensions 的 InMemory 默认实现（TryAddSingleton 语义）。
/// </remarks>
public interface ILearningLeaseStore
{
    /// <summary>
    /// 原子 CAS 获取租约。无现有行或现有行过期时获取成功；现有行未过期时返回 null。
    /// </summary>
    /// <param name="leaseId">租约 ID（pool 标识）。</param>
    /// <param name="leaseDuration">租约有效期（必须为正）。</param>
    /// <param name="owner">当前 worker 实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>获取成功的租约；已被其他实例持有（未过期）时返回 null。</returns>
    [StoreOperation(StoreOperationKind.Write)]
    ValueTask<LearningLease?> TryAcquireAsync(
        string leaseId,
        TimeSpan leaseDuration,
        string owner,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 续租约（心跳）。仅当 lease_token 匹配且租约未过期时生效。
    /// </summary>
    /// <param name="leaseId">租约 ID。</param>
    /// <param name="leaseToken">获取时返回的 lease token（CAS 校验）。</param>
    /// <param name="leaseDuration">续约时长（必须为正）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true=续约成功；false=租约已被抢占或已过期，调用方应停止处理。</returns>
    [StoreOperation(StoreOperationKind.Write)]
    ValueTask<bool> RenewAsync(
        string leaseId,
        string leaseToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 释放租约（主动让出）。0 行受影响（token 不匹配）不抛异常。
    /// </summary>
    /// <param name="leaseId">租约 ID。</param>
    /// <param name="leaseToken">获取时返回的 lease token（CAS 校验）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true=已释放；false=token 不匹配或租约不存在。</returns>
    [StoreOperation(StoreOperationKind.Write)]
    ValueTask<bool> ReleaseAsync(
        string leaseId,
        string leaseToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 清理所有已过期租约（崩溃实例持有的过期租约最终释放）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>清理的过期租约数量。</returns>
    [StoreOperation(StoreOperationKind.Write)]
    ValueTask<int> ReapExpiredAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 查询指定租约是否仍活跃（存在且未过期）。
    /// </summary>
    /// <param name="leaseId">租约 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true=租约存在且未过期。</returns>
    [StoreOperation(StoreOperationKind.Read)]
    ValueTask<bool> HasActiveLeaseAsync(string leaseId, CancellationToken cancellationToken = default);
}
