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

    /// <summary>
    /// P0-6-5：指令租约自动续租间隔（默认 <see cref="InstructionLeaseDuration"/> / 3）。
    /// Kernel 处理指令期间启动后台 Task 按此间隔调用 <see cref="IDurableTransport.RenewLeaseAsync"/>，
    /// 避免长耗时处理在 lease 过期前被 reaper 回滚导致重复执行。
    /// </summary>
    /// <remarks>
    /// 必须小于 <see cref="InstructionLeaseDuration"/> / 2 才能在过期前续租；
    /// 默认值为 LeaseDuration / 3 提供安全余量。设为 ≤ 0 时禁用续租（仅靠 lease 时长覆盖）。
    /// 实际生效值由 <see cref="ContextCore.Core.Services.AgentKernel.DefaultAgentKernel"/> 读取
    /// <see cref="KernelTransportOptions.DurableLeaseRenewalInterval"/>，DI 注册时应同步两者。
    /// </remarks>
    public TimeSpan LeaseRenewalInterval { get; set; } = TimeSpan.FromMinutes(5) / 3;

    /// <summary>
    /// P0-6-5：单条指令最大处理时长（默认 10 分钟）。超过此时长未完成的指令视为永久故障，
    /// outcome 标记为 <see cref="InstructionProcessingOutcome.PermanentFault"/>，
    /// 结果 Metadata 标记 <see cref="DurableDeliveryStatus.PermanentFault"/>，指令 Ack 删除进入死信对账。
    /// </summary>
    /// <remarks>
    /// 此上限防止僵尸指令无限续租占用 lease。应大于预期最长处理时长，但小于人工介入阈值。
    /// 实际生效值由 <see cref="ContextCore.Core.Services.AgentKernel.DefaultAgentKernel"/> 读取
    /// <see cref="KernelTransportOptions.DurableMaxProcessingTime"/>，DI 注册时应同步两者。
    /// </remarks>
    public TimeSpan MaxProcessingTime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>结果 outbox 重放轮询间隔（默认 500ms）。outbox 为空时 replayer 休眠此时长后重试。</summary>
    public TimeSpan OutboxPollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>结果 outbox 租约有效期（默认 2 分钟）。覆盖结果投递的预期时长。</summary>
    public TimeSpan OutboxLeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>过期租约清理间隔（默认 30 秒）。reaper 周期性扫描并回滚过期 Leased 行。</summary>
    public TimeSpan ReaperInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// P2：Pending 计数 OTel 指标采样间隔（默认 30 秒）。
    /// <see cref="ContextCore.Service.Hosting.PendingCountMetricsService"/> 按此间隔查询 DB 精确值（global_pending_count）
    /// 并采样本实例趋势值（local_pending_count），更新 <see cref="ContextCore.Core.CoreMetrics"/> 共享状态。
    /// </summary>
    /// <remarks>
    /// 间隔过短会增加 DB COUNT(*) 查询频率（每次 2 条 SELECT：inbox + outbox + result_outbox）；
    /// 间隔过长会导致 OTel 指标滞后于实际 backlog。30 秒为常见 production 折中值。
    /// 设为 <see cref="TimeSpan.Zero"/> 或负值时禁用指标采样服务（指标不更新，保留初始值 0）。
    /// </remarks>
    public TimeSpan MetricsInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>结果重放失败后的退避时长（默认 1 秒）。避免 SendResultAsync 持续失败时 tight-loop。</summary>
    public TimeSpan OutboxRetryBackoff { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>pump/reaper 实例所有者标识（默认自动生成 GUID）。用于诊断哪个实例持有了租约。</summary>
    public string? Owner { get; set; }
}
