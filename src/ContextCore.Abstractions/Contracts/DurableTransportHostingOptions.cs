namespace ContextCore.Abstractions;

/// <summary>
/// P0-4：Durable Transport 后台托管服务选项。
/// 控制 DurableTransportInstructionPumpService（指令 pump）、
/// ResultOutboxReplayService（结果重放）、LeaseReaperService（过期租约清理）的行为。
/// </summary>
/// <remarks>
/// 仅当 <see cref="KernelTransportOptions.UseDurableTransport"/> 为 true 且
/// <see cref="IAgentKernelTransport"/> 实现为 <see cref="IDurableTransport"/> 时生效。
/// 开发环境（InProcessTransport）不受此选项影响。
/// 放在 Abstractions 层以便 Storage.Postgres 与 Service 两个项目均能引用。
/// </remarks>
public sealed class DurableTransportHostingOptions
{
    /// <summary>是否启用后台托管服务（默认 true）。设为 false 可在测试场景中手动控制 pump/replay/reaper。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>指令 pump 轮询间隔（默认 200ms）。inbox 为空时 pump 休眠此时长后重试。</summary>
    /// <remarks>
    /// P1：连续空轮询时实际休眠时长会按 <see cref="PollBackoffMultiplier"/> 指数增长（上限 <see cref="MaxPollInterval"/>）；
    /// 拉取到指令时立即重置为本值。
    /// </remarks>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// P1：批量租约大小（默认 32）。<see cref="DurableTransportInstructionPumpService"/> 单次 <c>LeaseBatchAsync</c> 拉取的最大指令数。
    /// </summary>
    /// <remarks>
    /// 较大的值减少高并发下的网络往返，但增加单次事务持锁行数与本地 channel 占用；
    /// 较小的值降低锁竞争但提升往返频率。32 为常见 production 折中值。
    /// </remarks>
    public int BatchLeaseLimit { get; set; } = 32;

    /// <summary>
    /// P1：指数退避轮询上限（默认 5 秒）。连续空轮询时 polling interval 增长至此为止。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="PollBackoffMultiplier"/> 配合控制空队列下的轮询频率。
    /// 设为 0 或小于 <see cref="PollInterval"/> 时退化为不退避（始终使用 <see cref="PollInterval"/>）。
    /// </remarks>
    public TimeSpan MaxPollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// P1：指数退避倍率（默认 1.5）。连续空轮询时 polling interval × multiplier，直到 <see cref="MaxPollInterval"/>。
    /// </summary>
    /// <remarks>
    /// 必须大于 1.0 才能产生退避效果；1.0 等价于不退避。典型范围 1.2 ~ 2.0。
    /// </remarks>
    public double PollBackoffMultiplier { get; set; } = 1.5;

    /// <summary>指令租约有效期（默认 5 分钟）。覆盖 Kernel 处理一条指令的预期时长；过期后由 reaper 回滚为 Pending。</summary>
    public TimeSpan InstructionLeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>结果 outbox 重放轮询间隔（默认 500ms）。outbox 为空时 replayer 休眠此时长后重试。</summary>
    public TimeSpan OutboxPollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>结果 outbox 租约有效期（默认 2 分钟）。覆盖结果投递的预期时长。</summary>
    public TimeSpan OutboxLeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>过期租约清理间隔（默认 30 秒）。reaper 周期性扫描并回滚过期 Leased 行。</summary>
    public TimeSpan ReaperInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>结果重放失败后的退避时长（默认 1 秒）。避免 SendResultAsync 持续失败时 tight-loop。</summary>
    public TimeSpan OutboxRetryBackoff { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>pump/reaper 实例所有者标识（默认自动生成 GUID）。用于诊断哪个实例持有了租约。</summary>
    public string? Owner { get; set; }
}
