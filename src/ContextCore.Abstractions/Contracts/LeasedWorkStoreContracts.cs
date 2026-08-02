namespace ContextCore.Abstractions;

// ──  Unified Lease/Fencing Infrastructure ──────────────────────────────

/// <summary>
/// 统一租约工作项（被租约保护的工作单元）。
/// 覆盖 Leader/Hold 租约（如 IAgentRunLease / ICanaryLeaderLease）与 Queue/Outbox 租约
/// （如 ILearningEventOutboxStore / ILeasedJobQueue）两种模式。
/// </summary>
/// <typeparam name="TWork">工作项类型（Leader 模式下通常为 string 即 workId；Queue 模式下为工作负载类型）。</typeparam>
public sealed record LeasedWork<TWork>
{
    /// <summary>工作项内容。Leader 模式下通常为 workId 本身；Queue 模式下为反序列化的工作负载。</summary>
    public required TWork Work { get; init; }

    /// <summary>租约 token（续约/确认/否定确认时必须提供，CAS 校验）。</summary>
    public required string LeaseToken { get; init; }

    /// <summary>
    /// Fencing token（单调递增，从 1 开始）。仅 Leader 模式使用；Queue 模式恒为 0。
    /// 每次租约被重新获取（新持有者或过期抢占）时递增；续约不递增。
    /// </summary>
    public required long FencingToken { get; init; }

    /// <summary>当前租约持有者标识（如实例 ID / worker ID）。</summary>
    public required string Owner { get; init; }

    /// <summary>租约过期时间（UTC）。</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>租约获取时间（UTC）；表无对应列时为 null。</summary>
    public DateTimeOffset? AcquiredAt { get; init; }
}

/// <summary>租约获取结果。</summary>
public enum LeaseAcquireResult
{
    /// <summary>成功获取租约。</summary>
    Acquired,

    /// <summary>租约已被其他持有者持有且未过期。</summary>
    AlreadyHeld,

    /// <summary>工作项不存在（Queue 模式下 workId 在表中无对应行）。</summary>
    NotFound
}

/// <summary>
/// 非泛型标记接口，用于 DI 注册与发现所有 <see cref="ILeasedWorkStore{TWork, TLeased}"/> 实现。
/// </summary>
/// <remarks>
/// 使用 <c>IEnumerable&lt;ILeasedWorkStore&gt;</c> 可枚举所有已注册的租约工作存储，
/// 供后台 reaper / 监控等通用服务使用。
/// </remarks>
public interface ILeasedWorkStore
{
}

/// <summary>
/// 统一租约工作存储接口，覆盖 Leader/Hold 和 Queue/Outbox 两类租约。
/// </summary>
/// <typeparam name="TWork">工作项类型。</typeparam>
/// <typeparam name="TLeased">
/// 租约记录类型（用于 DI 区分不同租约存储；统一实现使用 <see cref="LeasedWork{TWork}"/>）。
/// </typeparam>
/// <remarks>
/// <b>Leader/Hold 模式</b>（<c>IsLeaderLease=true</c>）：
/// <list type="bullet">
/// <item><see cref="TryAcquireAsync"/>：INSERT ... ON CONFLICT DO UPDATE WHERE lease_expires_at &lt; now（CAS 抢占过期租约）。</item>
/// <item><see cref="AckAsync"/> / <see cref="NackAsync"/>：DELETE（主动释放）。</item>
/// <item><see cref="ReapExpiredAsync"/>：DELETE WHERE lease_expires_at &lt; now。</item>
/// <item>FencingToken 单调递增，用于副作用操作的 fencing 校验。</item>
/// </list>
///
/// <b>Queue/Outbox 模式</b>（<c>IsLeaderLease=false</c>）：
/// <list type="bullet">
/// <item><see cref="TryAcquireAsync"/>：UPDATE ... SET state='Processing' WHERE state='Pending'（单条获取）。</item>
/// <item><see cref="TryAcquireBatchAsync"/>：FOR UPDATE SKIP LOCKED 批量获取。</item>
/// <item><see cref="AckAsync"/>：UPDATE SET state='Acked'（完成确认）。</item>
/// <item><see cref="NackAsync"/>：UPDATE SET state='Pending', attempts+1（回退重试；超限转 DeadLettered）。</item>
/// <item><see cref="ReapExpiredAsync"/>：UPDATE SET state='Pending' WHERE lease_expires_at &lt; now AND state='Processing'。</item>
/// </list>
///
/// <b>ExecuteFencedAsync</b>：需要传入选定 provider 的原生连接/事务类型（如 NpgsqlConnection），
/// 因此定义在 provider 专属接口上而非此 provider-agnostic 接口上。
/// PostgreSQL 实现见 <c>ContextCore.Storage.Postgres.Stores.IPostgresLeasedWorkStore&lt;TWork&gt;</c>。
/// </remarks>
public interface ILeasedWorkStore<TWork, TLeased> : ILeasedWorkStore where TLeased : class
{
    /// <summary>
    /// 尝试获取租约（原子 CAS）。
    /// Leader 模式：仅当无持有者或已过期时获取（INSERT ON CONFLICT WHERE lease_expires_at &lt; now）。
    /// Queue 模式：仅当 state=Pending 或 state=Processing 且已过期时获取。
    /// </summary>
    /// <param name="workId">工作项 ID。</param>
    /// <param name="leaseDuration">租约有效期。</param>
    /// <param name="owner">候选持有者标识（如实例 ID）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>租约信息；已被其他实例持有时返回 null。</returns>
    ValueTask<LeasedWork<TWork>?> TryAcquireAsync(string workId, TimeSpan leaseDuration, string owner, CancellationToken ct = default);

    /// <summary>
    /// 批量获取租约（FOR UPDATE SKIP LOCKED）。仅 Queue 模式支持；Leader 模式抛 <see cref="NotSupportedException"/>。
    /// </summary>
    /// <param name="limit">最多获取的工作项数。</param>
    /// <param name="leaseDuration">租约有效期。</param>
    /// <param name="owner">候选持有者标识。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>获取到的租约工作项列表（可能为空）。</returns>
    ValueTask<IReadOnlyList<LeasedWork<TWork>>> TryAcquireBatchAsync(int limit, TimeSpan leaseDuration, string owner, CancellationToken ct = default);

    /// <summary>
    /// 续约（仅持有者可续约，FencingToken 不变）。
    /// 0 行受影响表示租约已被抢占或过期，返回 false；调用方应立即停止处理。
    /// </summary>
    /// <param name="workId">工作项 ID。</param>
    /// <param name="leaseToken">租约 token（来自 TryAcquireAsync）。</param>
    /// <param name="extension">延长时间量。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true = 续约成功；false = 租约已丢失。</returns>
    ValueTask<bool> RenewAsync(string workId, string leaseToken, TimeSpan extension, CancellationToken ct = default);

    /// <summary>
    /// 确认完成。
    /// Queue 模式：UPDATE SET state='Acked'；Leader 模式：DELETE（RELEASE）。
    /// </summary>
    /// <param name="workId">工作项 ID。</param>
    /// <param name="leaseToken">租约 token。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true = 确认成功；false = lease 已丢失（token 不匹配或已过期）。</returns>
    ValueTask<bool> AckAsync(string workId, string leaseToken, CancellationToken ct = default);

    /// <summary>
    /// 否定确认。
    /// Queue 模式：回退到 Pending + 递增 attempts（超限转 DeadLettered）；Leader 模式：RELEASE（同 Ack）。
    /// </summary>
    /// <param name="workId">工作项 ID。</param>
    /// <param name="leaseToken">租约 token。</param>
    /// <param name="errorMessage">可选错误信息（Queue 模式写入 last_error 列，若配置）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true = 否定确认成功；false = lease 已丢失。</returns>
    ValueTask<bool> NackAsync(string workId, string leaseToken, string? errorMessage = null, CancellationToken ct = default);

    /// <summary>
    /// 心跳续约（与 RenewAsync 相同，但语义上表示"仍在处理"，按 owner 匹配而非 token）。
    /// 适用于 worker 不便传递 leaseToken 的场景（如后台定时心跳）。
    /// </summary>
    /// <param name="workId">工作项 ID。</param>
    /// <param name="owner">持有者标识。</param>
    /// <param name="leaseDuration">续约后的新有效期。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true = 续约成功；false = 租约已丢失。</returns>
    ValueTask<bool> HeartbeatAsync(string workId, string owner, TimeSpan leaseDuration, CancellationToken ct = default);

    /// <summary>
    /// 回收过期租约。应由定时任务周期性调用。
    /// Leader 模式：DELETE WHERE lease_expires_at &lt; now。
    /// Queue 模式：UPDATE SET state='Pending' WHERE lease_expires_at &lt; now AND state='Processing'。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>回收的过期租约数。</returns>
    ValueTask<int> ReapExpiredAsync(CancellationToken ct = default);
}
