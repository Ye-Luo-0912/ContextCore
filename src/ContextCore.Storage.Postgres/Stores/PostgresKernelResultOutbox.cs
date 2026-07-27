using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// R29 WP-B-2 / P0-2：PostgreSQL 持久化 Kernel Result Outbox（租约模型）。
/// 替代 <see cref="ContextCore.Core.Services.AgentKernel.InMemoryKernelResultOutbox"/>，
/// 让 HA 场景下未投递的 AgentKernelResult 可跨进程持久化与崩溃恢复重放。
/// </summary>
/// <remarks>
/// <b>P0-2：租约模型（crash-recoverable outbox）</b>。
/// 旧版 <see cref="DequeueAsync"/> 将行标记为 <c>Dispatched</c>（终态），若消费方在 Dequeue 后、
/// 实际投递前崩溃，该行将永久滞留（无 Ack/Nack/Retry 机制）。P0-2 改为租约状态机：
/// <code>
/// Pending → Leased(owner, expires_at, token) → Acked(DELETE)
///                ↓ (lease expires)
///          RequeueExpired → Pending
/// </code>
///
/// 设计要点：
///   1. 表 <c>kernel_result_outbox</c> 以 <c>outbox_id</c> 为主键（GUID，由实现生成）。
///      反规范化 <c>instruction_id</c> 字段以便按指令查询；完整 <see cref="AgentKernelResult"/> 保存在 <c>data jsonb</c>。
///   2. <see cref="EnqueueAsync"/> 使用 <c>INSERT</c> 写入一条 Pending 行。
///      同一 instruction_id 可多次入队（每次失败都新增一条），不冲突。
///   3. <see cref="LeaseAsync"/> 使用 <c>SELECT ... FOR UPDATE SKIP LOCKED</c> 原子获取最旧的 Pending 行
///      并 CAS 为 Leased（<b>不删除</b>）；返回 <see cref="LeasedOutboxResult"/> 含 lease token。
///   4. <see cref="AckAsync"/>：Leased → DELETE（需 token 匹配，否则抛 <see cref="InvalidOperationException"/>）。
///   5. <see cref="NackAsync"/>：Leased → Pending（立即回滚，需 token 匹配）。
///   6. <see cref="RenewLeaseAsync"/>：延长 lease_expires_at（需 token 匹配）。
///   7. <see cref="RequeueExpiredAsync"/>：扫描所有 state='Leased' AND lease_expires_at &lt; now 的行，回滚为 Pending。
///   8. <see cref="PendingCount"/> 同步属性执行 DB COUNT(*) 查询，返回<b>全局精确值</b>（跨实例）；
///      <b>避免热路径调用</b>（同步阻塞），推荐 <see cref="GetPendingCountAsync"/> 或后台聚合的 global_pending_count 指标。
///
/// <b>遗留 API 兼容</b>：<see cref="DequeueAsync"/> 内部调用 <see cref="LeaseAsync"/>（默认租约 5 分钟）并丢弃 token，
/// 仅供单实例/测试场景使用；生产消费者应使用 <see cref="LeaseAsync"/> + <see cref="AckAsync"/> 显式管理租约生命周期。
/// 遗留 API 不 Ack 的行将在租约过期后由 <see cref="RequeueExpiredAsync"/> 回滚为 Pending。
/// </remarks>
public sealed class PostgresKernelResultOutbox : PostgresStoreBase, IPersistentKernelResultOutbox
{
    /// <summary>遗留 <see cref="DequeueAsync"/> 使用的默认租约有效期。</summary>
    /// <remarks>
    /// 选用 5 分钟以覆盖典型结果投递时长；过长会延迟崩溃 worker 持有租约的回收，
    /// 过短会误回滚仍在处理的投递。生产场景应使用 <see cref="LeaseAsync"/> 显式指定。
    /// </remarks>
    public static readonly TimeSpan DefaultLegacyLeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// P2：outbox Pending 结果数的<b>本实例趋势值</b>（非全局精确）。
    /// Enqueue (+1) / Lease (-1) / Nack (+1) 时通过 <see cref="Interlocked"/> 维护；GetPendingCountAsync 时从 DB 重新同步。
    /// <b>注意</b>：当前实现中此字段仅供 <see cref="GetPendingCountAsync"/> 内部缓存使用，
    /// <see cref="PendingCount"/> 同步属性实际执行 DB COUNT(*) 查询（不读此字段）。
    /// <b>不可靠场景</b>：新实例启动时为 0（不反映 DB 已有 backlog）、其他实例的增减不会被本地感知。
    /// <b>不可用于调度/安全判断</b>；生产指标应使用 <see cref="GetPendingCountAsync"/> 或后台聚合服务导出的 global_pending_count。
    /// </summary>
    private volatile int _pendingCountApprox;

    /// <summary>初始化 Postgres 持久化 Kernel Result Outbox。</summary>
    public PostgresKernelResultOutbox(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <inheritdoc />
    /// <remarks>
    /// 使用 <c>INSERT</c> 写入一条 Pending 行。同一 instruction_id 可多次入队（每次失败都新增一条），不冲突。
    /// </remarks>
    public async ValueTask EnqueueAsync(AgentKernelResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("kernel_result_outbox")} (
    outbox_id, instruction_id, state, created_at, updated_at, data)
VALUES (
    @outbox_id, @instruction_id, @state, @created_at, @updated_at, @data);
""";
        var outboxId = Guid.NewGuid().ToString("N");
        command.Parameters.AddWithValue("outbox_id", outboxId);
        command.Parameters.AddWithValue("instruction_id", result.InstructionId);
        command.Parameters.AddWithValue("state", "Pending");
        command.Parameters.AddWithValue("created_at", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("updated_at", DateTimeOffset.UtcNow);
        AddJson(command, "data", result);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        // P2：本地 counter 维护（Pending 行 +1）
        Interlocked.Increment(ref _pendingCountApprox);
    }

    // ── 租约模型（P0-2） ──────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// P0-2：原子 CAS（Pending → Leased）。使用 <c>FOR UPDATE SKIP LOCKED</c> 支持多 worker 并发；
    /// 返回的行标记为 Leased，<b>不删除</b>。调用方必须在处理完成后调用 <see cref="AckAsync"/> 确认；
    /// 否则租约过期后由 <see cref="RequeueExpiredAsync"/> 回滚。
    /// </remarks>
    public async ValueTask<LeasedOutboxResult?> LeaseAsync(TimeSpan leaseDuration, string? owner = null, CancellationToken cancellationToken = default)
    {
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "租约有效期必须为正。");
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        var token = Guid.NewGuid().ToString("N");
        var expiresAt = DateTimeOffset.UtcNow.Add(leaseDuration);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
WITH oldest AS (
    SELECT outbox_id, data
    FROM {Table("kernel_result_outbox")}
    WHERE state = 'Pending'
    ORDER BY created_at ASC, outbox_id ASC
    LIMIT 1
    FOR UPDATE SKIP LOCKED
)
UPDATE {Table("kernel_result_outbox")} AS o
SET state = 'Leased',
    lease_owner = @owner,
    lease_expires_at = @expires_at,
    lease_token = @token,
    updated_at = @updated_at
FROM oldest
WHERE o.outbox_id = oldest.outbox_id
RETURNING oldest.outbox_id, oldest.data;
""";
        command.Parameters.AddWithValue("owner", (object?)owner ?? DBNull.Value);
        command.Parameters.AddWithValue("expires_at", expiresAt);
        command.Parameters.AddWithValue("token", token);
        command.Parameters.AddWithValue("updated_at", DateTimeOffset.UtcNow);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var outboxId = reader.GetString(0);
        var result = Serializer.Deserialize<AgentKernelResult>(reader.GetString(1));
        // P2：本地 counter 维护（Pending 行 -1）
        Interlocked.Decrement(ref _pendingCountApprox);
        return new LeasedOutboxResult
        {
            OutboxId = outboxId,
            Result = result,
            LeaseToken = token,
            LeaseExpiresAt = expiresAt
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// P0-2：Leased → DELETE。WHERE 子句要求 state='Leased' AND lease_token 匹配，
    /// 防止其他 worker 误删。0 行受影响时抛 <see cref="InvalidOperationException"/>。
    /// </remarks>
    public async ValueTask AckAsync(string outboxId, string leaseToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outboxId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
DELETE FROM {Table("kernel_result_outbox")}
WHERE outbox_id = @outbox_id
  AND lease_token = @token
  AND state = 'Leased';
""";
        command.Parameters.AddWithValue("outbox_id", outboxId);
        command.Parameters.AddWithValue("token", leaseToken);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new InvalidOperationException(
                $"Ack 失败：outbox_id={outboxId} 的租约不匹配、已过期回滚或已确认。" +
                "可能原因：租约被其他 worker 接管、租约已过期被 RequeueExpiredAsync 回滚为 Pending、或已调用过 Ack。");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// P0-2：Leased → Pending（立即回滚）。清除 lease_owner / lease_expires_at / lease_token，
    /// 让该行可被其他 worker 重新 <see cref="LeaseAsync"/>。0 行受影响时抛 <see cref="InvalidOperationException"/>。
    /// </remarks>
    public async ValueTask NackAsync(string outboxId, string leaseToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outboxId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
UPDATE {Table("kernel_result_outbox")}
SET state = 'Pending',
    lease_owner = NULL,
    lease_expires_at = NULL,
    lease_token = NULL,
    updated_at = @updated_at
WHERE outbox_id = @outbox_id
  AND lease_token = @token
  AND state = 'Leased';
""";
        command.Parameters.AddWithValue("outbox_id", outboxId);
        command.Parameters.AddWithValue("token", leaseToken);
        command.Parameters.AddWithValue("updated_at", DateTimeOffset.UtcNow);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new InvalidOperationException(
                $"Nack 失败：outbox_id={outboxId} 的租约不匹配、已过期回滚或已确认。");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// P0-2：延长 lease_expires_at（从当前 UTC 时间开始计算新的 expires_at = now + extension）。
    /// 适用于长耗时投递需要更多时间。0 行受影响时抛 <see cref="InvalidOperationException"/>。
    /// </remarks>
    public async ValueTask RenewLeaseAsync(string outboxId, string leaseToken, TimeSpan extension, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outboxId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
        if (extension <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(extension), "续租时间必须为正。");
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        var newExpiresAt = DateTimeOffset.UtcNow.Add(extension);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
UPDATE {Table("kernel_result_outbox")}
SET lease_expires_at = @new_expires_at,
    updated_at = @updated_at
WHERE outbox_id = @outbox_id
  AND lease_token = @token
  AND state = 'Leased';
""";
        command.Parameters.AddWithValue("outbox_id", outboxId);
        command.Parameters.AddWithValue("token", leaseToken);
        command.Parameters.AddWithValue("new_expires_at", newExpiresAt);
        command.Parameters.AddWithValue("updated_at", DateTimeOffset.UtcNow);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new InvalidOperationException(
                $"RenewLease 失败：outbox_id={outboxId} 的租约不匹配、已过期回滚或已确认。");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// P0-2：扫描所有 state='Leased' AND lease_expires_at &lt; now 的行，回滚为 Pending（清除 lease 字段）。
    /// 应由后台定时任务或新实例启动时调用，确保崩溃 worker 持有的租约最终被释放。
    /// </remarks>
    public async ValueTask<int> RequeueExpiredAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
UPDATE {Table("kernel_result_outbox")}
SET state = 'Pending',
    lease_owner = NULL,
    lease_expires_at = NULL,
    lease_token = NULL,
    updated_at = @updated_at
WHERE state = 'Leased' AND lease_expires_at < @now;
""";
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("updated_at", now);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ── 遗留 API（向后兼容） ────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <b>遗留 API</b>：内部调用 <see cref="LeaseAsync"/>（默认租约 <see cref="DefaultLegacyLeaseDuration"/>），
    /// 返回结果本体（<b>丢弃 lease token</b>）。仅供单实例/测试场景使用。
    /// 生产消费者应使用 <see cref="LeaseAsync"/> + <see cref="AckAsync"/> 显式管理租约。
    /// 不 Ack 的行将在租约过期后由 <see cref="RequeueExpiredAsync"/> 回滚为 Pending。
    /// </remarks>
    public async ValueTask<AgentKernelResult?> DequeueAsync(CancellationToken cancellationToken = default)
    {
        var leased = await LeaseAsync(DefaultLegacyLeaseDuration, owner: "legacy-dequeue", cancellationToken).ConfigureAwait(false);
        return leased?.Result;
    }

    /// <summary>
    /// 当前 outbox 中 state='Pending' 的结果数（不含 Leased）——同步 DB COUNT(*) 查询，返回<b>全局精确值</b>。
    /// </summary>
    /// <remarks>
    /// P0-2：仅计数 Pending 行，Leased 行不计入（已被 worker 持有，等待 Ack 或过期回滚）。
    /// 此属性执行同步 DB 查询（<c>ExecuteScalar</c> 阻塞调用线程），返回<b>跨实例全局精确值</b>，
    /// 与 <see cref="PostgresDurableTransport.PendingInstructionCount"/>（本实例趋势值）语义不同。
    /// <b>避免在热路径调用</b>——同步 DB I/O 在高并发下会成为瓶颈；推荐使用 <see cref="GetPendingCountAsync"/>
    /// 或后台聚合服务导出的 <c>global_pending_count</c> 指标。
    /// </remarks>
    [Obsolete("Sync DB COUNT(*) query blocks the calling thread; use GetPendingCountAsync for hot paths, or consume global_pending_count metric from PendingCountMetricsService.")]
    public int PendingCount
    {
        get
        {
            // PostgreSQL COUNT(*) 返回 Int64（long），需用 Convert.ToInt32 转换。
            using var connection = ConnectionFactory.OpenConnectionAsync(CancellationToken.None).GetAwaiter().GetResult();
            using var command = connection.CreateCommand();
            command.CommandTimeout = Options.CommandTimeoutSeconds;
            command.CommandText = $"""
SELECT COUNT(*) FROM {Table("kernel_result_outbox")} WHERE state = 'Pending';
""";
            var count = command.ExecuteScalar();
            return count is int i ? i : Convert.ToInt32(count);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// P2：异步版本 <see cref="PendingCount"/>，推荐用于热路径。
    /// 返回 <b>DB 精确值（全局，跨实例）</b>——反映所有实例的累积 Pending 行数，可用于调度/安全判断。
    /// 同步属性 <see cref="PendingCount"/> 保留向后兼容，但 Postgres 实现内部走 COUNT(*) 同步阻塞，应避免热路径调用。
    /// </remarks>
    public async ValueTask<int> GetPendingCountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT COUNT(*) FROM {Table("kernel_result_outbox")} WHERE state = 'Pending';
""";
        var count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var exact = count is int i ? i : Convert.ToInt32(count);
        // 顺便同步本地 counter，避免长期漂移
        _pendingCountApprox = exact;
        return exact;
    }
}
