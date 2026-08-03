namespace ContextCore.Abstractions;

/// <summary>后台作业的类型。</summary>
public enum ContextJobKind
{
    /// <summary>压缩作业。</summary>
    Compression,
    /// <summary>索引构建作业。</summary>
    IndexBuild,
    /// <summary>Embedding 生成作业。</summary>
    Embedding,
    /// <summary>Vector index 重建作业。</summary>
    VectorReindex,
    /// <summary>包刷新作业。</summary>
    PackageRefresh,
    /// <summary>自定义作业。</summary>
    Custom
}

/// <summary>后台作业的执行状态。</summary>
public enum ContextJobState
{
    /// <summary>已入队，等待执行。</summary>
    Queued,
    /// <summary>执行中。</summary>
    Running,
    /// <summary>等待重试。</summary>
    WaitingRetry,
    /// <summary>已成功完成。</summary>
    Succeeded,
    /// <summary>已失败。</summary>
    Failed,
    /// <summary>已取消。</summary>
    Cancelled,
    /// <summary>需要人工审核。</summary>
    RequiresReview
}

/// <summary>表示一个后台处理作业。</summary>
public sealed class ContextJob
{
    /// <summary>作业唯一标识符。</summary>
    public string JobId { get; init; } = string.Empty;

    /// <summary>所属工作空间 ID。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>所属集合 ID。</summary>
    public string CollectionId { get; init; } = string.Empty;

    /// <summary>作业类型。</summary>
    public ContextJobKind Kind { get; init; } = ContextJobKind.Custom;

    /// <summary>作业载荷（JSON 格式）。</summary>
    public string PayloadJson { get; init; } = string.Empty;

    /// <summary>当前状态。</summary>
    public ContextJobState State { get; init; } = ContextJobState.Queued;

    /// <summary>优先级（值越大越优先）。</summary>
    public int Priority { get; init; }

    /// <summary>已重试次数。</summary>
    public int RetryCount { get; init; }

    /// <summary>最大重试次数。</summary>
    public int MaxRetryCount { get; init; }

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>开始执行时间。</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>完成时间。</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>失败时的错误信息。</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>作业查询条件。</summary>
public sealed class ContextJobQuery
{
    /// <summary>筛选指定工作空间的作业。</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>筛选指定集合的作业。</summary>
    public string? CollectionId { get; init; }

    /// <summary>筛选指定状态的作业。</summary>
    public ContextJobState? State { get; init; }

    /// <summary>筛选指定类型的作业。</summary>
    public ContextJobKind? Kind { get; init; }

    /// <summary>最多返回的记录数，默认 100。</summary>
    public int Take { get; init; } = 100;
}

/// <summary>提供作业队列的入队、出队及确认操作。</summary>
public interface IContextJobQueue
{
    /// <summary>将作业加入队列。</summary>
    Task EnqueueAsync(ContextJob job, CancellationToken cancellationToken = default);

    /// <summary>取出下一个待处理的作业。</summary>
    Task<ContextJob?> DequeueAsync(CancellationToken cancellationToken = default);

    /// <summary>确认作业已成功处理。</summary>
    Task AckAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>标记作业处理失败并附加原因。</summary>
    Task NackAsync(
        string jobId,
        string reason,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 支持租约（lease）的作业队列扩展契约。
/// 实现此接口的队列（如 Postgres）提供带租约的获取与心跳续约，
/// 使 worker 进程崩溃后过期租约可被其他 worker 通过 <see cref="AcquireLeaseAsync"/> 抢占恢复。
/// </summary>
/// <remarks>
/// 语义：
/// <list type="bullet">
/// <item><see cref="AcquireLeaseAsync"/>：原子地获取一个 Queued/WaitingRetry 或过期 Running 的作业，
/// 设置 state=Running、lease_owner、lease_expires_at、last_heartbeat_at。返回 null 表示无可消费作业。</item>
/// <item><see cref="RenewHeartbeatAsync"/>：续约租约。返回 true 表示续约成功；
/// 返回 false 表示租约已丢失（被其他 worker 抢占或状态已改变）——调用方应中止处理。</item>
/// <item>Ack/Nack 复用 <see cref="IContextJobQueue"/> 上的方法，CAS WHERE state='Running' 已防止重复 Ack/Nack。</item>
/// </list>
/// 未实现此接口的队列（如 InMemory/File）仍走 <see cref="IContextJobQueue.DequeueAsync"/> 路径，
/// worker 检测到 <c>queue is ILeasedJobQueue</c> 时切换到租约路径。
/// </remarks>
public interface ILeasedJobQueue
{
    /// <summary>
    /// 原子地获取一个作业并设置租约。
    /// 选择条件：state 为 Queued/WaitingRetry，或 state=Running 且 lease_expires_at 已过期。
    /// 使用 SELECT FOR UPDATE SKIP LOCKED 确保多 worker / 多实例无重复消费。
    /// </summary>
    /// <param name="owner">租约持有者标识（worker 实例唯一）。同一 owner 可重新获取自己过期租约的作业。</param>
    /// <param name="leaseDuration">租约有效期。过期后其他 worker 可通过本方法抢占。</param>
    /// <param name="kind">可选：仅获取指定类型的作业。</param>
    /// <param name="workspaceId">可选：仅获取指定工作空间的作业。</param>
    /// <param name="collectionId">可选：仅获取指定集合的作业。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>获取到的作业（state=Running），或 null 表示队列无可消费作业。</returns>
    Task<ContextJob?> AcquireLeaseAsync(
        string owner,
        TimeSpan leaseDuration,
        ContextJobKind? kind = null,
        string? workspaceId = null,
        string? collectionId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 续约租约。应在 <paramref name="leaseDuration"/> 过期前周期性调用。
    /// </summary>
    /// <param name="jobId">作业 ID。</param>
    /// <param name="owner">租约持有者标识（必须与 <see cref="AcquireLeaseAsync"/> 一致）。</param>
    /// <param name="leaseDuration">续约后的新有效期。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true 表示续约成功；false 表示租约已丢失（lease_owner 不匹配或 state 非 Running），调用方应中止处理。</returns>
    Task<bool> RenewHeartbeatAsync(
        string jobId,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 原子地批量获取作业并设置租约，按 workspace 公平分配。
    /// 选择条件与 <see cref="AcquireLeaseAsync"/> 相同（Queued/WaitingRetry 或已过期 Running），
    /// 使用 SELECT FOR UPDATE SKIP LOCKED 确保多 worker / 多实例无重复消费。
    /// 每个 workspace 最多领取 <paramref name="perWorkspace"/> 个，避免单一 workspace 占满整批。
    /// </summary>
    /// <param name="owner">租约持有者标识（worker 实例唯一）。</param>
    /// <param name="leaseDuration">租约有效期。过期后其他 worker 可通过本方法抢占。</param>
    /// <param name="take">最多领取的作业数。</param>
    /// <param name="perWorkspace">每个 workspace 最多领取数；小于等于 0 时按 <paramref name="take"/> 处理。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>领取到的作业列表（state=Running），可能为空列表。</returns>
    Task<IReadOnlyList<ContextJob>> AcquireLeaseBatchAsync(
        string owner,
        TimeSpan leaseDuration,
        int take,
        int perWorkspace,
        CancellationToken cancellationToken = default);
}

/// <summary>提供作业的查询功能。</summary>
public interface IContextJobQueryStore
{
    /// <summary>按条件查询作业列表。</summary>
    Task<IReadOnlyList<ContextJob>> QueryAsync(
        ContextJobQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>处理指定类型后台作业的执行器。</summary>
public interface IContextJobProcessor
{
    /// <summary>此处理器支持的作业类型。</summary>
    ContextJobKind Kind { get; }

    /// <summary>执行作业。</summary>
    Task ProcessAsync(ContextJob job, CancellationToken cancellationToken = default);
}

/// <summary>按作业类型分发到对应处理器。</summary>
public interface IContextJobDispatcher
{
    /// <summary>分发并执行作业。</summary>
    Task DispatchAsync(ContextJob job, CancellationToken cancellationToken = default);
}
