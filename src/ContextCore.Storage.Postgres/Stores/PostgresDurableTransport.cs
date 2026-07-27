using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// R29 WP-B-4 / P0-1：PostgreSQL 持久化 Durable Transport（租约模型）。
/// 替代 <see cref="ContextCore.Core.Services.AgentKernel.InProcessTransport"/>（进程内 Channel），
/// 让 HA 场景下指令（inbox）与结果（outbox）跨进程持久化，支持崩溃恢复后由新实例继续消费。
/// </summary>
/// <remarks>
/// <b>P0-1：租约模型（crash-recoverable durable transport）</b>。
/// 旧版使用破坏性 DELETE 出队，崩溃窗口（DELETE 成功 → Kernel 未处理 → 进程崩溃）会导致指令永久丢失。
/// P0-1 改为租约状态机：
/// <code>
/// Pending → Leased(owner, expires_at, token) → Acked(DELETE)
///                ↓ (lease expires)
///          RequeueExpired → Pending
/// </code>
///
/// 设计要点：
///   1. 两张表：<c>kernel_transport_inbox</c>（待处理指令）+ <c>kernel_transport_outbox</c>（待读取结果）。
///      每张表以 GUID/text 主键标识一条记录；<c>created_at</c> 用于 FIFO 排序；
///      <c>state</c> 列跟踪 Pending/Leased；<c>lease_token</c> 防止其他 worker 误 Ack。
///   2. <see cref="SubmitAsync"/>（inbox 写入）与 <see cref="SendResultAsync"/>（outbox 写入）使用 <c>INSERT</c>，
///      新行默认 state='Pending'；幂等性由调用方保证（同一 instruction_id 多次入队会因主键冲突失败，符合 exactly-once 语义）。
///   3. <see cref="LeaseAsync"/> / <see cref="LeaseResultAsync"/> 使用 <c>FOR UPDATE SKIP LOCKED</c> 原子取最旧 Pending 行并
///      CAS 为 Leased（<b>不删除</b>）；返回 <see cref="LeasedInstruction"/> / <see cref="LeasedResult"/> 含 lease token。
///   4. <see cref="AckAsync"/> / <see cref="AckResultAsync"/>：Leased → DELETE（需 token 匹配，否则抛 <see cref="InvalidOperationException"/>）。
///   5. <see cref="NackAsync"/> / <see cref="NackResultAsync"/>：Leased → Pending（立即回滚，需 token 匹配）。
///   6. <see cref="RenewLeaseAsync"/> / <see cref="RenewResultLeaseAsync"/>：延长 lease_expires_at（需 token 匹配）。
///   7. <see cref="RequeueExpiredAsync"/>：扫描所有 state='Leased' AND lease_expires_at &lt; now 的行，回滚为 Pending（崩溃 worker 持有的租约最终释放）。
///   8. <see cref="PendingInstructionCount"/> / <see cref="PendingResultCount"/> 返回<b>本实例趋势值</b>（Interlocked 维护的本地 counter，不从 DB 读取）；
///      已有 DB backlog、其他实例操作或新实例启动时均不可靠，<b>不可用于调度/安全判断</b>。生产指标应使用
///      <see cref="GetPendingInstructionCountAsync"/> / <see cref="GetPendingResultCountAsync"/> 或后台聚合服务导出的 global_pending_count。
///   9. <see cref="Complete"/> 为 no-op：PG 表状态持久化，进程退出不影响表数据。
///
/// <b>遗留 API 兼容</b>：<see cref="ReceiveAsync"/> / <see cref="ReceiveResultAsync"/> 内部调用
/// <see cref="LeaseAsync"/> / <see cref="LeaseResultAsync"/>（默认租约 5 分钟）并丢弃 token，
/// 仅供单实例/测试场景使用；生产消费者应使用 <see cref="LeaseAsync"/> + <see cref="AckAsync"/> 显式管理租约生命周期。
/// 遗留 API 不 Ack 的行将在租约过期后由 <see cref="RequeueExpiredAsync"/> 回滚为 Pending。
/// </remarks>
public sealed class PostgresDurableTransport : PostgresStoreBase, IDurableTransport
{
    /// <summary>遗留 <see cref="ReceiveAsync"/> / <see cref="ReceiveResultAsync"/> 使用的默认租约有效期。</summary>
    /// <remarks>
    /// 选用 5 分钟以覆盖典型 Kernel 处理时长；过长会延迟崩溃 worker 持有租约的回收，
    /// 过短会误回滚仍在处理的指令。生产场景应使用 <see cref="LeaseAsync"/> 显式指定。
    /// </remarks>
    public static readonly TimeSpan DefaultLegacyLeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// P2：inbox Pending 指令数的<b>本实例趋势值</b>（非全局精确）。
    /// Enqueue (+1) / Lease (-1) / Nack (+1) 时通过 <see cref="Interlocked"/> 维护；RequeueExpired / GetPendingInstructionCountAsync 时从 DB 重新同步。
    /// 不访问 DB，仅供 <see cref="PendingInstructionCount"/> 同步属性快速读取。
    /// <b>不可靠场景</b>：新实例启动时为 0（不反映 DB 已有 backlog）、其他实例的增减不会被本地感知、
    /// 并发竞态下可能短暂为负（导出时已 clamp 到 0）。<b>不可用于调度/安全判断</b>（如 shutdown、限流、拒绝请求）；
    /// 生产指标应使用 <see cref="GetPendingInstructionCountAsync"/> 或后台聚合服务导出的 global_pending_count。
    /// </summary>
    private volatile int _pendingInstructionCountApprox;

    /// <summary>
    /// P2：outbox Pending 结果数的<b>本实例趋势值</b>（非全局精确）。
    /// SendResult (+1) / LeaseResult (-1) / NackResult (+1) 时通过 <see cref="Interlocked"/> 维护；RequeueExpired / GetPendingResultCountAsync 时从 DB 重新同步。
    /// 不访问 DB，仅供 <see cref="PendingResultCount"/> 同步属性快速读取。
    /// <b>不可靠场景</b>：新实例启动时为 0（不反映 DB 已有 backlog）、其他实例的增减不会被本地感知、
    /// 并发竞态下可能短暂为负（导出时已 clamp 到 0）。<b>不可用于调度/安全判断</b>（如 shutdown、限流、拒绝请求）；
    /// 生产指标应使用 <see cref="GetPendingResultCountAsync"/> 或后台聚合服务导出的 global_pending_count。
    /// </summary>
    private volatile int _pendingResultCountApprox;

    /// <summary>
    /// P0-6-7：重试与死信默认配置。可通过 <see cref="DefaultMaxAttempts"/> /
    /// <see cref="DefaultRetryBaseDelay"/> / <see cref="DefaultRetryMaxDelay"/> 在调用方覆盖。
    /// </summary>
    /// <remarks>
    /// 指数退避：next_attempt_at = now + Min(base * 2^(attempt-1), max)。
    /// base=1s，max=5min，最大尝试次数=5 → 退避序列 1s, 2s, 4s, 8s, 16s（封顶 5min）。
    /// </remarks>
    public const int DefaultMaxAttempts = 5;
    public static readonly TimeSpan DefaultRetryBaseDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DefaultRetryMaxDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// P0-6-7：计算指数退避时长。delay = Min(base * 2^(attempt-1), max)。
    /// attempt ≤ 1 时返回 base（首次失败立即退避基础值）。
    /// </summary>
    private static TimeSpan ComputeBackoff(int attempt, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        if (attempt <= 1) return baseDelay;
        // 防止溢出：用 double 计算后 clamp 到 maxDelay。
        var ms = baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        return ms >= maxDelay.TotalMilliseconds ? maxDelay : TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>初始化 Postgres 持久化 Durable Transport。</summary>
    public PostgresDurableTransport(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    // ── Inbox：指令写入 ──────────────────────────────────────────────

    /// <summary>提交指令到 Transport 的 inbox（持久化，state='Pending'）。</summary>
    /// <param name="instruction">要提交的指令。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 使用 <c>INSERT</c> 写入一行；instruction_id 主键保证幂等（重复提交同一 ID 会因主键冲突失败）。
    /// 新行默认 state='Pending'，等待 <see cref="LeaseAsync"/> 或遗留 <see cref="ReceiveAsync"/> 租约。
    /// </remarks>
    public async ValueTask SubmitAsync(AgentKernelInstruction instruction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instruction);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("kernel_transport_inbox")} (instruction_id, created_at, data)
VALUES (@instruction_id, @created_at, @data);
""";
        command.Parameters.AddWithValue("instruction_id", instruction.InstructionId);
        command.Parameters.AddWithValue("created_at", DateTimeOffset.UtcNow);
        AddJson(command, "data", instruction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        // P2：本地 counter 维护（Pending 行 +1）
        Interlocked.Increment(ref _pendingInstructionCountApprox);
    }

    // ── Inbox：指令租约 ──────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// P0-1：原子 CAS（Pending → Leased）。使用 <c>FOR UPDATE SKIP LOCKED</c> 支持多 worker 并发；
    /// 返回的行标记为 Leased，<b>不删除</b>。调用方必须在处理完成后调用 <see cref="AckAsync"/> 确认；
    /// 否则租约过期后由 <see cref="RequeueExpiredAsync"/> 回滚。
    /// </remarks>
    public async ValueTask<LeasedInstruction?> LeaseAsync(TimeSpan leaseDuration, string? owner = null, CancellationToken cancellationToken = default)
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
    SELECT instruction_id, data
    FROM {Table("kernel_transport_inbox")}
    WHERE state = 'Pending'
      AND (next_attempt_at IS NULL OR next_attempt_at <= @now)
    ORDER BY created_at ASC, instruction_id ASC
    LIMIT 1
    FOR UPDATE SKIP LOCKED
)
UPDATE {Table("kernel_transport_inbox")} AS i
SET state = 'Leased',
    lease_owner = @owner,
    lease_expires_at = @expires_at,
    lease_token = @token
FROM oldest
WHERE i.instruction_id = oldest.instruction_id
RETURNING oldest.data;
""";
        command.Parameters.AddWithValue("owner", (object?)owner ?? DBNull.Value);
        command.Parameters.AddWithValue("expires_at", expiresAt);
        command.Parameters.AddWithValue("token", token);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);

        var instruction = await ExecuteScalarJsonAsync<AgentKernelInstruction>(command, cancellationToken).ConfigureAwait(false);
        if (instruction is null)
        {
            return null;
        }

        // P2：本地 counter 维护（Pending 行 -1）
        Interlocked.Decrement(ref _pendingInstructionCountApprox);
        return new LeasedInstruction
        {
            Instruction = instruction,
            LeaseToken = token,
            LeaseExpiresAt = expiresAt
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// P1：批量原子 CAS（Pending → Leased），单次 SQL 事务内 UPDATE 多行 + RETURNING。
    /// 与 <see cref="LeaseAsync"/> 语义对称，但一次往返可拉取最多 <paramref name="limit"/> 条指令，
    /// 减少高并发下的网络/锁开销。返回的行标记为 Leased，<b>不删除</b>。
    /// </remarks>
    public async ValueTask<IReadOnlyList<LeasedInstruction>> LeaseBatchAsync(int limit, TimeSpan leaseDuration, string? owner = null, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "limit 必须大于 0。");
        }
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
    SELECT instruction_id, data
    FROM {Table("kernel_transport_inbox")}
    WHERE state = 'Pending'
      AND (next_attempt_at IS NULL OR next_attempt_at <= @now)
    ORDER BY created_at ASC, instruction_id ASC
    LIMIT @limit
    FOR UPDATE SKIP LOCKED
)
UPDATE {Table("kernel_transport_inbox")} AS i
SET state = 'Leased',
    lease_owner = @owner,
    lease_expires_at = @expires_at,
    lease_token = @token
FROM oldest
WHERE i.instruction_id = oldest.instruction_id
RETURNING oldest.data;
""";
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("owner", (object?)owner ?? DBNull.Value);
        command.Parameters.AddWithValue("expires_at", expiresAt);
        command.Parameters.AddWithValue("token", token);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);

        var instructions = await ExecuteReaderJsonAsync<AgentKernelInstruction>(command, cancellationToken).ConfigureAwait(false);
        if (instructions.Count == 0)
        {
            return Array.Empty<LeasedInstruction>();
        }

        // P2：本地 counter 维护（Pending 行 -count）
        Interlocked.Add(ref _pendingInstructionCountApprox, -instructions.Count);
        var result = new LeasedInstruction[instructions.Count];
        for (var i = 0; i < instructions.Count; i++)
        {
            result[i] = new LeasedInstruction
            {
                Instruction = instructions[i],
                LeaseToken = token,
                LeaseExpiresAt = expiresAt
            };
        }
        return result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// P0-1：Leased → DELETE。WHERE 子句要求 state='Leased' AND lease_token 匹配，
    /// 防止其他 worker 误删。0 行受影响时抛 <see cref="InvalidOperationException"/>（token 不匹配、租约已过期回滚或已确认）。
    /// </remarks>
    public async ValueTask AckAsync(string instructionId, string leaseToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instructionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
DELETE FROM {Table("kernel_transport_inbox")}
WHERE instruction_id = @instruction_id
  AND lease_token = @token
  AND state = 'Leased';
""";
        command.Parameters.AddWithValue("instruction_id", instructionId);
        command.Parameters.AddWithValue("token", leaseToken);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new InvalidOperationException(
                $"Ack 失败：instruction_id={instructionId} 的租约不匹配、已过期回滚或已确认。" +
                "可能原因：租约被其他 worker 接管、租约已过期被 RequeueExpiredAsync 回滚为 Pending、或已调用过 Ack。");
        }
        // P2：本地 counter 不变（Pending 计数在 LeaseAsync 时已 -1；Ack 仅删除 Leased 行）
    }

    /// <inheritdoc />
    /// <remarks>
    /// P2：批量单事务 DELETE —— 使用 <c>unnest</c> 展开输入数组，单条 SQL 完成 DELETE 并通过 CTE
    /// <c>RETURNING</c> + <c>NOT EXISTS</c> 计算未匹配的 instruction_id（token 不匹配或状态非 Leased）。
    /// 相比旧版循环逐条 DELETE，减少 N 次网络往返为 1 次，且单条 SQL 天然原子（单事务）。
    /// 部分成功不抛异常；调用方根据返回的失败列表决定后续处理。
    /// </remarks>
    public async ValueTask<IReadOnlyList<string>> AckBatchAsync(IReadOnlyList<(string InstructionId, string LeaseToken)> acks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acks);
        if (acks.Count == 0)
        {
            return Array.Empty<string>();
        }

        // 预过滤：分离有效输入与无效输入（空 id/token 直接计入失败列表，不参与批量 DELETE）
        var valid = new List<(string Id, string Token)>(acks.Count);
        var failed = new List<string>();
        foreach (var (id, token) in acks)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(token))
            {
                failed.Add(id);
            }
            else
            {
                valid.Add((id, token));
            }
        }

        if (valid.Count == 0)
        {
            return failed;
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        var ids = new string[valid.Count];
        var tokens = new string[valid.Count];
        for (var i = 0; i < valid.Count; i++)
        {
            ids[i] = valid[i].Id;
            tokens[i] = valid[i].Token;
        }

        // CTE：input_rows 展开输入数组；deleted 执行批量 DELETE 并 RETURNING 已删行；
        // 最终 SELECT 返回未出现在 deleted 中的输入 id（即 token 不匹配 / 状态非 Leased / 行不存在）。
        command.CommandText = $"""
WITH input_rows AS (
    SELECT id, token FROM unnest(@ids::text[], @tokens::text[]) AS v(id, token)
),
deleted AS (
    DELETE FROM {Table("kernel_transport_inbox")} AS q
    USING input_rows
    WHERE q.instruction_id = input_rows.id
      AND q.lease_token = input_rows.token
      AND q.state = 'Leased'
    RETURNING q.instruction_id
)
SELECT i.id FROM input_rows i
WHERE NOT EXISTS (
    SELECT 1 FROM deleted d WHERE d.instruction_id = i.id
);
""";
        AddTextArray(command, "ids", ids);
        AddTextArray(command, "tokens", tokens);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            failed.Add(reader.GetString(0));
        }
        // P2：本地 counter 不变（Pending 计数在 LeaseAsync 时已 -1；Ack 仅删除 Leased 行）
        return failed;
    }

    /// <inheritdoc />
    /// <remarks>
    /// P0-1：Leased → Pending（立即回滚）。清除 lease_owner / lease_expires_at / lease_token，
    /// 让该行可被其他 worker 重新 <see cref="LeaseAsync"/>。0 行受影响时抛 <see cref="InvalidOperationException"/>。
    /// P0-6-7：失败重试与死信。Nack 时 attempt_count + 1；若新 attempt_count &gt; max_attempts，将行
    /// 移入 <c>kernel_transport_dead_letter</c> 表 + DELETE 原行；否则 UPDATE state='Pending' +
    /// next_attempt_at = now + 指数退避，让 <see cref="LeaseAsync"/> 在退避时间后才重新租约。
    /// </remarks>
    public async ValueTask NackAsync(string instructionId, string leaseToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instructionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // P0-6-7：先 UPDATE 提升 attempt_count + 计算是否进入 DLQ。RETURNING 旧 attempt_count/max_attempts/data
        // 用于决策：若新 attempt_count > max_attempts → 移入 DLQ；否则退避回滚为 Pending。
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
WITH bumped AS (
    UPDATE {Table("kernel_transport_inbox")}
    SET attempt_count = attempt_count + 1,
        last_error = @last_error,
        lease_owner = NULL,
        lease_expires_at = NULL,
        lease_token = NULL
    WHERE instruction_id = @instruction_id
      AND lease_token = @token
      AND state = 'Leased'
    RETURNING instruction_id, attempt_count, max_attempts, data, created_at
)
SELECT instruction_id, attempt_count, max_attempts, data, created_at FROM bumped;
""";
        command.Parameters.AddWithValue("instruction_id", instructionId);
        command.Parameters.AddWithValue("token", leaseToken);
        command.Parameters.AddWithValue("last_error", (object?)null ?? DBNull.Value);

        string? rowData = null;
        int newAttempt = 0;
        int maxAttempts = DefaultMaxAttempts;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"Nack 失败：instruction_id={instructionId} 的租约不匹配、已过期回滚或已确认。");
            }
            instructionId = reader.GetString(0);
            newAttempt = reader.GetInt32(1);
            maxAttempts = reader.GetInt32(2);
            rowData = reader.GetString(3);
        }

        // P0-6-7：超过 max_attempts → 移入 DLQ + DELETE 原行
        if (newAttempt > maxAttempts)
        {
            await MoveToDeadLetterAsync(connection, "inbox", instructionId, newAttempt, "exceeded max_attempts",
                rowData, cancellationToken).ConfigureAwait(false);
            await DeleteInboxRowAsync(connection, instructionId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // 退避回滚为 Pending；next_attempt_at = now + 指数退避
            var backoff = ComputeBackoff(newAttempt, DefaultRetryBaseDelay, DefaultRetryMaxDelay);
            var nextAttemptAt = DateTimeOffset.UtcNow.Add(backoff);
            await using var updateCmd = connection.CreateCommand();
            updateCmd.CommandTimeout = Options.CommandTimeoutSeconds;
            updateCmd.CommandText = $"""
UPDATE {Table("kernel_transport_inbox")}
SET state = 'Pending', next_attempt_at = @next_attempt_at
WHERE instruction_id = @instruction_id;
""";
            updateCmd.Parameters.AddWithValue("instruction_id", instructionId);
            updateCmd.Parameters.AddWithValue("next_attempt_at", nextAttemptAt);
            await updateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        // P2：本地 counter 维护（Leased → Pending；Pending 行 +1）
        Interlocked.Increment(ref _pendingInstructionCountApprox);
    }

    /// <summary>P0-6-7：将一行写入 kernel_transport_dead_letter 表。</summary>
    private async Task MoveToDeadLetterAsync(
        NpgsqlConnection connection,
        string source,
        string originalId,
        int attemptCount,
        string deadLetterReason,
        string originalDataJson,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandTimeout = Options.CommandTimeoutSeconds;
        cmd.CommandText = $"""
INSERT INTO {Table("kernel_transport_dead_letter")} (
    dead_letter_id, source, original_id, attempt_count, last_error, dead_letter_reason, original_data, created_at)
VALUES (
    @dead_letter_id, @source, @original_id, @attempt_count, @last_error, @dead_letter_reason, @original_data, @created_at);
""";
        cmd.Parameters.AddWithValue("dead_letter_id", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("source", source);
        cmd.Parameters.AddWithValue("original_id", originalId);
        cmd.Parameters.AddWithValue("attempt_count", attemptCount);
        cmd.Parameters.AddWithValue("last_error", (object?)null ?? DBNull.Value);
        cmd.Parameters.AddWithValue("dead_letter_reason", deadLetterReason);
        // originalDataJson 来自 data 列（jsonb 读为 string），用 NpgsqlDbType.Jsonb 写回
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter("original_data", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = originalDataJson });
        cmd.Parameters.AddWithValue("created_at", DateTimeOffset.UtcNow);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>P0-6-7：DELETE inbox 行（用于将行移入 DLQ 后清理原表）。</summary>
    private async Task DeleteInboxRowAsync(NpgsqlConnection connection, string instructionId, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandTimeout = Options.CommandTimeoutSeconds;
        cmd.CommandText = $"""DELETE FROM {Table("kernel_transport_inbox")} WHERE instruction_id = @instruction_id;""";
        cmd.Parameters.AddWithValue("instruction_id", instructionId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// P2：批量单事务 UPDATE（Leased → Pending）—— 使用 <c>unnest</c> 展开输入数组，单条 SQL 完成
    /// 批量回滚并通过 CTE <c>RETURNING</c> + <c>NOT EXISTS</c> 计算未匹配的 instruction_id。
    /// 相比循环逐条 UPDATE，减少 N 次网络往返为 1 次，且单条 SQL 天然原子（单事务）。
    /// 部分成功不抛异常；调用方根据返回的失败列表决定后续处理。
    /// </remarks>
    public async ValueTask<IReadOnlyList<string>> NackBatchAsync(IReadOnlyList<(string InstructionId, string LeaseToken)> nacks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nacks);
        if (nacks.Count == 0)
        {
            return Array.Empty<string>();
        }

        // 预过滤：分离有效输入与无效输入（空 id/token 直接计入失败列表）
        var valid = new List<(string Id, string Token)>(nacks.Count);
        var failed = new List<string>();
        foreach (var (id, token) in nacks)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(token))
            {
                failed.Add(id);
            }
            else
            {
                valid.Add((id, token));
            }
        }

        if (valid.Count == 0)
        {
            return failed;
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        var ids = new string[valid.Count];
        var tokens = new string[valid.Count];
        for (var i = 0; i < valid.Count; i++)
        {
            ids[i] = valid[i].Id;
            tokens[i] = valid[i].Token;
        }

        // CTE：input_rows 展开输入数组；updated 执行批量 UPDATE 并 RETURNING 已更新行；
        // 最终 SELECT 返回未出现在 updated 中的输入 id（即 token 不匹配 / 状态非 Leased / 行不存在）。
        command.CommandText = $"""
WITH input_rows AS (
    SELECT id, token FROM unnest(@ids::text[], @tokens::text[]) AS v(id, token)
),
updated AS (
    UPDATE {Table("kernel_transport_inbox")} AS q
    SET state = 'Pending',
        lease_owner = NULL,
        lease_expires_at = NULL,
        lease_token = NULL
    FROM input_rows
    WHERE q.instruction_id = input_rows.id
      AND q.lease_token = input_rows.token
      AND q.state = 'Leased'
    RETURNING q.instruction_id
)
SELECT i.id FROM input_rows i
WHERE NOT EXISTS (
    SELECT 1 FROM updated u WHERE u.instruction_id = i.id
);
""";
        AddTextArray(command, "ids", ids);
        AddTextArray(command, "tokens", tokens);

        var sqlFailedCount = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            failed.Add(reader.GetString(0));
            sqlFailedCount++;
        }
        // 成功回滚数 = 有效输入数 - SQL 失败数；本地 counter 维护（Leased → Pending；Pending 行 +nackedCount）
        var nackedCount = valid.Count - sqlFailedCount;
        if (nackedCount > 0)
        {
            Interlocked.Add(ref _pendingInstructionCountApprox, nackedCount);
        }
        return failed;
    }

    /// <inheritdoc />
    /// <remarks>
    /// P0-1：延长 lease_expires_at（从当前 UTC 时间开始计算新的 expires_at = now + extension）。
    /// 适用于长耗时处理需要更多时间。0 行受影响时抛 <see cref="InvalidOperationException"/>。
    /// </remarks>
    public async ValueTask RenewLeaseAsync(string instructionId, string leaseToken, TimeSpan extension, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instructionId);
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
UPDATE {Table("kernel_transport_inbox")}
SET lease_expires_at = @new_expires_at
WHERE instruction_id = @instruction_id
  AND lease_token = @token
  AND state = 'Leased';
""";
        command.Parameters.AddWithValue("instruction_id", instructionId);
        command.Parameters.AddWithValue("token", leaseToken);
        command.Parameters.AddWithValue("new_expires_at", newExpiresAt);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new InvalidOperationException(
                $"RenewLease 失败：instruction_id={instructionId} 的租约不匹配、已过期回滚或已确认。");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// P2：批量单事务 UPDATE（延长 lease_expires_at）—— 使用 <c>unnest</c> 展开输入数组，单条 SQL 完成
    /// 批量续租并通过 CTE <c>RETURNING</c> + <c>NOT EXISTS</c> 计算未匹配的 instruction_id。
    /// 所有续租共用同一 <paramref name="extension"/> 时长（新的 expires_at = now + extension）。
    /// 相比循环逐条 UPDATE，减少 N 次网络往返为 1 次，且单条 SQL 天然原子（单事务）。
    /// 部分成功不抛异常；调用方根据返回的失败列表决定后续处理。
    /// </remarks>
    public async ValueTask<IReadOnlyList<string>> RenewLeaseBatchAsync(IReadOnlyList<(string InstructionId, string LeaseToken)> renewals, TimeSpan extension, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(renewals);
        if (extension <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(extension), "续租时间必须为正。");
        }
        if (renewals.Count == 0)
        {
            return Array.Empty<string>();
        }

        // 预过滤：分离有效输入与无效输入（空 id/token 直接计入失败列表）
        var valid = new List<(string Id, string Token)>(renewals.Count);
        var failed = new List<string>();
        foreach (var (id, token) in renewals)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(token))
            {
                failed.Add(id);
            }
            else
            {
                valid.Add((id, token));
            }
        }

        if (valid.Count == 0)
        {
            return failed;
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        var newExpiresAt = DateTimeOffset.UtcNow.Add(extension);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        var ids = new string[valid.Count];
        var tokens = new string[valid.Count];
        for (var i = 0; i < valid.Count; i++)
        {
            ids[i] = valid[i].Id;
            tokens[i] = valid[i].Token;
        }

        // CTE：input_rows 展开输入数组；updated 执行批量 UPDATE 并 RETURNING 已更新行；
        // 最终 SELECT 返回未出现在 updated 中的输入 id（即 token 不匹配 / 状态非 Leased / 行不存在）。
        command.CommandText = $"""
WITH input_rows AS (
    SELECT id, token FROM unnest(@ids::text[], @tokens::text[]) AS v(id, token)
),
updated AS (
    UPDATE {Table("kernel_transport_inbox")} AS q
    SET lease_expires_at = @new_expires_at
    FROM input_rows
    WHERE q.instruction_id = input_rows.id
      AND q.lease_token = input_rows.token
      AND q.state = 'Leased'
    RETURNING q.instruction_id
)
SELECT i.id FROM input_rows i
WHERE NOT EXISTS (
    SELECT 1 FROM updated u WHERE u.instruction_id = i.id
);
""";
        AddTextArray(command, "ids", ids);
        AddTextArray(command, "tokens", tokens);
        command.Parameters.AddWithValue("new_expires_at", newExpiresAt);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            failed.Add(reader.GetString(0));
        }
        // P2：本地 counter 不变（RenewLease 仅延长 expires_at，不改变 state；Pending 计数不变）
        return failed;
    }

    // ── Outbox：结果写入 ─────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// P0-4：使用稳定 result_id = 'result-' || instruction_id，配合
    /// <c>kernel_transport_outbox.instruction_id</c> 的 UNIQUE 部分索引（WHERE instruction_id &lt;&gt; ''）
    /// 实现<b>幂等投递</b>。Replayer 在 Ack 失败后重发同一 instruction_id 的结果时，
    /// <c>ON CONFLICT (instruction_id) WHERE instruction_id &lt;&gt; '' DO NOTHING</c> 跳过插入，
    /// 避免消费者侧重复投递（消费者也可基于 instruction_id 去重）。
    /// 空 instruction_id 不参与 UNIQUE 约束（partial index 排除），回退用 GUID 保证主键唯一性。
    /// </remarks>
    public async ValueTask SendResultAsync(AgentKernelResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // P0-4：稳定 result_id。非空 instruction_id → 'result-' + instruction_id（幂等键）；
        // 空 instruction_id → GUID（partial index 排除空值，无 UNIQUE 约束，用 GUID 保证主键唯一）。
        var stableResultId = result.InstructionId.Length > 0
            ? "result-" + result.InstructionId
            : Guid.NewGuid().ToString("N");
        command.CommandText = $"""
INSERT INTO {Table("kernel_transport_outbox")} (result_id, instruction_id, created_at, data)
VALUES (@result_id, @instruction_id, @created_at, @data)
ON CONFLICT (instruction_id) WHERE instruction_id <> '' DO NOTHING;
""";
        command.Parameters.AddWithValue("result_id", stableResultId);
        command.Parameters.AddWithValue("instruction_id", result.InstructionId);
        command.Parameters.AddWithValue("created_at", DateTimeOffset.UtcNow);
        AddJson(command, "data", result);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        // P2：本地 counter 维护（仅在实际插入新行时 +1；ON CONFLICT 跳过时 affected=0）
        if (affected > 0)
        {
            Interlocked.Increment(ref _pendingResultCountApprox);
        }
    }

    // ── Outbox：结果租约 ─────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// P0-1：与 <see cref="LeaseAsync"/> 对称，用于结果消费方管理租约生命周期。
    /// P0-6-7：与 <see cref="LeaseAsync"/> 一致，过滤 <c>next_attempt_at</c>：仅租约
    /// <c>next_attempt_at IS NULL OR next_attempt_at &lt;= now</c> 的 Pending 行，让退避中的结果不会被提前租约。
    /// </remarks>
    public async ValueTask<LeasedResult?> LeaseResultAsync(TimeSpan leaseDuration, string? owner = null, CancellationToken cancellationToken = default)
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
    SELECT result_id, data
    FROM {Table("kernel_transport_outbox")}
    WHERE state = 'Pending'
      AND (next_attempt_at IS NULL OR next_attempt_at <= @now)
    ORDER BY created_at ASC, result_id ASC
    LIMIT 1
    FOR UPDATE SKIP LOCKED
)
UPDATE {Table("kernel_transport_outbox")} AS o
SET state = 'Leased',
    lease_owner = @owner,
    lease_expires_at = @expires_at,
    lease_token = @token
FROM oldest
WHERE o.result_id = oldest.result_id
RETURNING oldest.result_id, oldest.data;
""";
        command.Parameters.AddWithValue("owner", (object?)owner ?? DBNull.Value);
        command.Parameters.AddWithValue("expires_at", expiresAt);
        command.Parameters.AddWithValue("token", token);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var resultId = reader.GetString(0);
        var result = Serializer.Deserialize<AgentKernelResult>(reader.GetString(1));
        // P2：本地 counter 维护（Pending 行 -1）
        Interlocked.Decrement(ref _pendingResultCountApprox);
        return new LeasedResult
        {
            Result = result,
            ResultId = resultId,
            LeaseToken = token,
            LeaseExpiresAt = expiresAt
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// P1：批量原子 CAS（Pending → Leased），与 <see cref="LeaseBatchAsync"/> 对称，用于结果消费方批量租约。
    /// P0-6-7：与 <see cref="LeaseResultAsync"/> 一致，过滤 <c>next_attempt_at</c> 让退避中的结果不会被提前租约。
    /// </remarks>
    public async ValueTask<IReadOnlyList<LeasedResult>> LeaseResultBatchAsync(int limit, TimeSpan leaseDuration, string? owner = null, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "limit 必须大于 0。");
        }
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
    SELECT result_id, data
    FROM {Table("kernel_transport_outbox")}
    WHERE state = 'Pending'
      AND (next_attempt_at IS NULL OR next_attempt_at <= @now)
    ORDER BY created_at ASC, result_id ASC
    LIMIT @limit
    FOR UPDATE SKIP LOCKED
)
UPDATE {Table("kernel_transport_outbox")} AS o
SET state = 'Leased',
    lease_owner = @owner,
    lease_expires_at = @expires_at,
    lease_token = @token
FROM oldest
WHERE o.result_id = oldest.result_id
RETURNING oldest.result_id, oldest.data;
""";
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("owner", (object?)owner ?? DBNull.Value);
        command.Parameters.AddWithValue("expires_at", expiresAt);
        command.Parameters.AddWithValue("token", token);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);

        var results = new List<LeasedResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var resultId = reader.GetString(0);
            var result = Serializer.Deserialize<AgentKernelResult>(reader.GetString(1));
            results.Add(new LeasedResult
            {
                Result = result,
                ResultId = resultId,
                LeaseToken = token,
                LeaseExpiresAt = expiresAt
            });
        }

        if (results.Count > 0)
        {
            // P2：本地 counter 维护（Pending 行 -count）
            Interlocked.Add(ref _pendingResultCountApprox, -results.Count);
        }
        return results;
    }

    /// <inheritdoc />
    public async ValueTask AckResultAsync(string resultId, string leaseToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resultId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
DELETE FROM {Table("kernel_transport_outbox")}
WHERE result_id = @result_id
  AND lease_token = @token
  AND state = 'Leased';
""";
        command.Parameters.AddWithValue("result_id", resultId);
        command.Parameters.AddWithValue("token", leaseToken);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new InvalidOperationException(
                $"AckResult 失败：result_id={resultId} 的租约不匹配、已过期回滚或已确认。");
        }
        // P2：本地 counter 不变（Pending 计数在 LeaseResultAsync 时已 -1）
    }

    /// <inheritdoc />
    /// <remarks>
    /// P2：批量单事务 DELETE —— 使用 <c>unnest</c> 展开输入数组，单条 SQL 完成 DELETE 并通过 CTE
    /// <c>RETURNING</c> + <c>NOT EXISTS</c> 计算未匹配的 result_id（token 不匹配或状态非 Leased）。
    /// 与 <see cref="AckBatchAsync"/> 对称，相比旧版循环逐条 DELETE 减少网络往返且天然原子。
    /// 部分成功不抛异常；调用方根据返回的失败列表决定后续处理。
    /// </remarks>
    public async ValueTask<IReadOnlyList<string>> AckResultBatchAsync(IReadOnlyList<(string ResultId, string LeaseToken)> acks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acks);
        if (acks.Count == 0)
        {
            return Array.Empty<string>();
        }

        // 预过滤：分离有效输入与无效输入（空 id/token 直接计入失败列表，不参与批量 DELETE）
        var valid = new List<(string Id, string Token)>(acks.Count);
        var failed = new List<string>();
        foreach (var (id, token) in acks)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(token))
            {
                failed.Add(id);
            }
            else
            {
                valid.Add((id, token));
            }
        }

        if (valid.Count == 0)
        {
            return failed;
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        var ids = new string[valid.Count];
        var tokens = new string[valid.Count];
        for (var i = 0; i < valid.Count; i++)
        {
            ids[i] = valid[i].Id;
            tokens[i] = valid[i].Token;
        }

        // CTE：input_rows 展开输入数组；deleted 执行批量 DELETE 并 RETURNING 已删行；
        // 最终 SELECT 返回未出现在 deleted 中的输入 id（即 token 不匹配 / 状态非 Leased / 行不存在）。
        command.CommandText = $"""
WITH input_rows AS (
    SELECT id, token FROM unnest(@ids::text[], @tokens::text[]) AS v(id, token)
),
deleted AS (
    DELETE FROM {Table("kernel_transport_outbox")} AS q
    USING input_rows
    WHERE q.result_id = input_rows.id
      AND q.lease_token = input_rows.token
      AND q.state = 'Leased'
    RETURNING q.result_id
)
SELECT i.id FROM input_rows i
WHERE NOT EXISTS (
    SELECT 1 FROM deleted d WHERE d.result_id = i.id
);
""";
        AddTextArray(command, "ids", ids);
        AddTextArray(command, "tokens", tokens);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            failed.Add(reader.GetString(0));
        }
        // P2：本地 counter 不变（Pending 计数在 LeaseResultAsync 时已 -1；Ack 仅删除 Leased 行）
        return failed;
    }

    /// <inheritdoc />
    /// <remarks>
    /// P0-6-7：与 <see cref="NackAsync"/> 对称的失败重试与死信。Nack 时 attempt_count + 1；
    /// 若新 attempt_count &gt; max_attempts，将行移入 <c>kernel_transport_dead_letter</c> 表 + DELETE 原行；
    /// 否则 UPDATE state='Pending' + next_attempt_at = now + 指数退避，让 <see cref="LeaseResultAsync"/>
    /// 在退避时间后才重新租约。
    /// </remarks>
    public async ValueTask NackResultAsync(string resultId, string leaseToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resultId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // P0-6-7：先 UPDATE 提升 attempt_count + 计算是否进入 DLQ。RETURNING 旧 attempt_count/max_attempts/data
        // 用于决策：若新 attempt_count > max_attempts → 移入 DLQ；否则退避回滚为 Pending。
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
WITH bumped AS (
    UPDATE {Table("kernel_transport_outbox")}
    SET attempt_count = attempt_count + 1,
        last_error = @last_error,
        lease_owner = NULL,
        lease_expires_at = NULL,
        lease_token = NULL
    WHERE result_id = @result_id
      AND lease_token = @token
      AND state = 'Leased'
    RETURNING result_id, attempt_count, max_attempts, data, created_at
)
SELECT result_id, attempt_count, max_attempts, data, created_at FROM bumped;
""";
        command.Parameters.AddWithValue("result_id", resultId);
        command.Parameters.AddWithValue("token", leaseToken);
        command.Parameters.AddWithValue("last_error", (object?)null ?? DBNull.Value);

        string? rowData = null;
        int newAttempt = 0;
        int maxAttempts = DefaultMaxAttempts;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"NackResult 失败：result_id={resultId} 的租约不匹配、已过期回滚或已确认。");
            }
            resultId = reader.GetString(0);
            newAttempt = reader.GetInt32(1);
            maxAttempts = reader.GetInt32(2);
            rowData = reader.GetString(3);
        }

        // P0-6-7：超过 max_attempts → 移入 DLQ + DELETE 原行
        if (newAttempt > maxAttempts)
        {
            await MoveToDeadLetterAsync(connection, "outbox", resultId, newAttempt, "exceeded max_attempts",
                rowData, cancellationToken).ConfigureAwait(false);
            await DeleteOutboxRowAsync(connection, resultId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // 退避回滚为 Pending；next_attempt_at = now + 指数退避
            var backoff = ComputeBackoff(newAttempt, DefaultRetryBaseDelay, DefaultRetryMaxDelay);
            var nextAttemptAt = DateTimeOffset.UtcNow.Add(backoff);
            await using var updateCmd = connection.CreateCommand();
            updateCmd.CommandTimeout = Options.CommandTimeoutSeconds;
            updateCmd.CommandText = $"""
UPDATE {Table("kernel_transport_outbox")}
SET state = 'Pending', next_attempt_at = @next_attempt_at
WHERE result_id = @result_id;
""";
            updateCmd.Parameters.AddWithValue("result_id", resultId);
            updateCmd.Parameters.AddWithValue("next_attempt_at", nextAttemptAt);
            await updateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            // P2：本地 counter 维护（Leased → Pending；Pending 行 +1）；DLQ 路径不 +1（行已删除）
            Interlocked.Increment(ref _pendingResultCountApprox);
        }
    }

    /// <summary>P0-6-7：DELETE outbox 行（用于将行移入 DLQ 后清理原表）。</summary>
    private async Task DeleteOutboxRowAsync(NpgsqlConnection connection, string resultId, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandTimeout = Options.CommandTimeoutSeconds;
        cmd.CommandText = $"""DELETE FROM {Table("kernel_transport_outbox")} WHERE result_id = @result_id;""";
        cmd.Parameters.AddWithValue("result_id", resultId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// P2：批量单事务 UPDATE（Leased → Pending）—— 使用 <c>unnest</c> 展开输入数组，单条 SQL 完成
    /// 批量回滚并通过 CTE <c>RETURNING</c> + <c>NOT EXISTS</c> 计算未匹配的 result_id。
    /// 与 <see cref="NackBatchAsync"/> 对称，相比循环逐条 UPDATE 减少网络往返且天然原子。
    /// 部分成功不抛异常；调用方根据返回的失败列表决定后续处理。
    /// </remarks>
    public async ValueTask<IReadOnlyList<string>> NackResultBatchAsync(IReadOnlyList<(string ResultId, string LeaseToken)> nacks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nacks);
        if (nacks.Count == 0)
        {
            return Array.Empty<string>();
        }

        // 预过滤：分离有效输入与无效输入（空 id/token 直接计入失败列表）
        var valid = new List<(string Id, string Token)>(nacks.Count);
        var failed = new List<string>();
        foreach (var (id, token) in nacks)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(token))
            {
                failed.Add(id);
            }
            else
            {
                valid.Add((id, token));
            }
        }

        if (valid.Count == 0)
        {
            return failed;
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        var ids = new string[valid.Count];
        var tokens = new string[valid.Count];
        for (var i = 0; i < valid.Count; i++)
        {
            ids[i] = valid[i].Id;
            tokens[i] = valid[i].Token;
        }

        // CTE：input_rows 展开输入数组；updated 执行批量 UPDATE 并 RETURNING 已更新行；
        // 最终 SELECT 返回未出现在 updated 中的输入 id（即 token 不匹配 / 状态非 Leased / 行不存在）。
        command.CommandText = $"""
WITH input_rows AS (
    SELECT id, token FROM unnest(@ids::text[], @tokens::text[]) AS v(id, token)
),
updated AS (
    UPDATE {Table("kernel_transport_outbox")} AS q
    SET state = 'Pending',
        lease_owner = NULL,
        lease_expires_at = NULL,
        lease_token = NULL
    FROM input_rows
    WHERE q.result_id = input_rows.id
      AND q.lease_token = input_rows.token
      AND q.state = 'Leased'
    RETURNING q.result_id
)
SELECT i.id FROM input_rows i
WHERE NOT EXISTS (
    SELECT 1 FROM updated u WHERE u.result_id = i.id
);
""";
        AddTextArray(command, "ids", ids);
        AddTextArray(command, "tokens", tokens);

        var sqlFailedCount = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            failed.Add(reader.GetString(0));
            sqlFailedCount++;
        }
        // 成功回滚数 = 有效输入数 - SQL 失败数；本地 counter 维护（Leased → Pending；Pending 行 +nackedCount）
        var nackedCount = valid.Count - sqlFailedCount;
        if (nackedCount > 0)
        {
            Interlocked.Add(ref _pendingResultCountApprox, nackedCount);
        }
        return failed;
    }

    /// <inheritdoc />
    public async ValueTask RenewResultLeaseAsync(string resultId, string leaseToken, TimeSpan extension, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resultId);
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
UPDATE {Table("kernel_transport_outbox")}
SET lease_expires_at = @new_expires_at
WHERE result_id = @result_id
  AND lease_token = @token
  AND state = 'Leased';
""";
        command.Parameters.AddWithValue("result_id", resultId);
        command.Parameters.AddWithValue("token", leaseToken);
        command.Parameters.AddWithValue("new_expires_at", newExpiresAt);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new InvalidOperationException(
                $"RenewResultLease 失败：result_id={resultId} 的租约不匹配、已过期回滚或已确认。");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// P2：批量单事务 UPDATE（延长 lease_expires_at）—— 使用 <c>unnest</c> 展开输入数组，单条 SQL 完成
    /// 批量续租并通过 CTE <c>RETURNING</c> + <c>NOT EXISTS</c> 计算未匹配的 result_id。
    /// 与 <see cref="RenewLeaseBatchAsync"/> 对称，所有续租共用同一 <paramref name="extension"/> 时长。
    /// 部分成功不抛异常；调用方根据返回的失败列表决定后续处理。
    /// </remarks>
    public async ValueTask<IReadOnlyList<string>> RenewResultLeaseBatchAsync(IReadOnlyList<(string ResultId, string LeaseToken)> renewals, TimeSpan extension, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(renewals);
        if (extension <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(extension), "续租时间必须为正。");
        }
        if (renewals.Count == 0)
        {
            return Array.Empty<string>();
        }

        // 预过滤：分离有效输入与无效输入（空 id/token 直接计入失败列表）
        var valid = new List<(string Id, string Token)>(renewals.Count);
        var failed = new List<string>();
        foreach (var (id, token) in renewals)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(token))
            {
                failed.Add(id);
            }
            else
            {
                valid.Add((id, token));
            }
        }

        if (valid.Count == 0)
        {
            return failed;
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        var newExpiresAt = DateTimeOffset.UtcNow.Add(extension);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        var ids = new string[valid.Count];
        var tokens = new string[valid.Count];
        for (var i = 0; i < valid.Count; i++)
        {
            ids[i] = valid[i].Id;
            tokens[i] = valid[i].Token;
        }

        // CTE：input_rows 展开输入数组；updated 执行批量 UPDATE 并 RETURNING 已更新行；
        // 最终 SELECT 返回未出现在 updated 中的输入 id（即 token 不匹配 / 状态非 Leased / 行不存在）。
        command.CommandText = $"""
WITH input_rows AS (
    SELECT id, token FROM unnest(@ids::text[], @tokens::text[]) AS v(id, token)
),
updated AS (
    UPDATE {Table("kernel_transport_outbox")} AS q
    SET lease_expires_at = @new_expires_at
    FROM input_rows
    WHERE q.result_id = input_rows.id
      AND q.lease_token = input_rows.token
      AND q.state = 'Leased'
    RETURNING q.result_id
)
SELECT i.id FROM input_rows i
WHERE NOT EXISTS (
    SELECT 1 FROM updated u WHERE u.result_id = i.id
);
""";
        AddTextArray(command, "ids", ids);
        AddTextArray(command, "tokens", tokens);
        command.Parameters.AddWithValue("new_expires_at", newExpiresAt);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            failed.Add(reader.GetString(0));
        }
        // P2：本地 counter 不变（RenewResultLease 仅延长 expires_at，不改变 state；Pending 计数不变）
        return failed;
    }

    // ── 过期租约回收 ─────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// P0-1：扫描 inbox + outbox 中所有 state='Leased' AND lease_expires_at &lt; now 的行，
    /// 回滚为 Pending（清除 lease 字段）。应由后台定时任务或新实例启动时调用，
    /// 确保崩溃 worker 持有的租约最终被释放。
    /// </remarks>
    public async ValueTask<int> RequeueExpiredAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var total = 0;

        await using (var inboxCommand = connection.CreateCommand())
        {
            inboxCommand.CommandTimeout = Options.CommandTimeoutSeconds;
            inboxCommand.CommandText = $"""
UPDATE {Table("kernel_transport_inbox")}
SET state = 'Pending',
    lease_owner = NULL,
    lease_expires_at = NULL,
    lease_token = NULL
WHERE state = 'Leased' AND lease_expires_at < @now;
""";
            inboxCommand.Parameters.AddWithValue("now", now);
            total += await inboxCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var outboxCommand = connection.CreateCommand())
        {
            outboxCommand.CommandTimeout = Options.CommandTimeoutSeconds;
            outboxCommand.CommandText = $"""
UPDATE {Table("kernel_transport_outbox")}
SET state = 'Pending',
    lease_owner = NULL,
    lease_expires_at = NULL,
    lease_token = NULL
WHERE state = 'Leased' AND lease_expires_at < @now;
""";
            outboxCommand.Parameters.AddWithValue("now", now);
            total += await outboxCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // P2：RequeueExpired 改变了多行 Leased → Pending 状态，本地 counter 难以精确同步；
        // 重新执行一次 SELECT COUNT(*) 同步两个 counter（属于后台低频路径，可接受 DB 访问）。
        await ResyncPendingCountersAsync(connection, cancellationToken).ConfigureAwait(false);

        return total;
    }

    /// <summary>
    /// P2：通过单次 SELECT COUNT(*) 同步 inbox/outbox 的本地 Pending counter。
    /// 仅在 RequeueExpiredAsync 等批量状态变更后调用（低频路径）；高频热路径使用本地 counter 近似值。
    /// </summary>
    private async Task ResyncPendingCountersAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using (var inboxCountCmd = connection.CreateCommand())
        {
            inboxCountCmd.CommandTimeout = Options.CommandTimeoutSeconds;
            inboxCountCmd.CommandText = $"SELECT COUNT(*) FROM {Table("kernel_transport_inbox")} WHERE state = 'Pending';";
            var inboxCount = await inboxCountCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            _pendingInstructionCountApprox = inboxCount is int i ? i : Convert.ToInt32(inboxCount);
        }

        await using (var outboxCountCmd = connection.CreateCommand())
        {
            outboxCountCmd.CommandTimeout = Options.CommandTimeoutSeconds;
            outboxCountCmd.CommandText = $"SELECT COUNT(*) FROM {Table("kernel_transport_outbox")} WHERE state = 'Pending';";
            var outboxCount = await outboxCountCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            _pendingResultCountApprox = outboxCount is int j ? j : Convert.ToInt32(outboxCount);
        }
    }

    // ── 遗留 API（向后兼容） ────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <b>遗留 API</b>：内部调用 <see cref="LeaseAsync"/>（默认租约 <see cref="DefaultLegacyLeaseDuration"/>），
    /// 返回指令本体（<b>丢弃 lease token</b>）。仅供单实例/测试场景使用。
    /// 生产消费者应使用 <see cref="LeaseAsync"/> + <see cref="AckAsync"/> 显式管理租约。
    /// 不 Ack 的行将在租约过期后由 <see cref="RequeueExpiredAsync"/> 回滚为 Pending。
    /// </remarks>
    public async ValueTask<AgentKernelInstruction?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var leased = await LeaseAsync(DefaultLegacyLeaseDuration, owner: "legacy-receive", cancellationToken).ConfigureAwait(false);
        return leased?.Instruction;
    }

    /// <summary>从 outbox 读取下一条结果（持久化，遗留 API）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>读取到的结果；outbox 为空（无 Pending 行）时返回 null。</returns>
    /// <remarks>
    /// <b>遗留 API</b>：内部调用 <see cref="LeaseResultAsync"/>（默认租约 <see cref="DefaultLegacyLeaseDuration"/>），
    /// 返回结果本体（<b>丢弃 lease token</b>）。与 <see cref="InProcessTransport.ReceiveResultAsync"/> API 对齐。
    /// 生产消费者应使用 <see cref="LeaseResultAsync"/> + <see cref="AckResultAsync"/> 显式管理租约。
    /// </remarks>
    public async ValueTask<AgentKernelResult?> ReceiveResultAsync(CancellationToken cancellationToken = default)
    {
        var leased = await LeaseResultAsync(DefaultLegacyLeaseDuration, owner: "legacy-receive", cancellationToken).ConfigureAwait(false);
        return leased?.Result;
    }

    // ── 计数与生命周期 ───────────────────────────────────────────────

    /// <summary>当前 inbox 中 state='Pending' 的指令数（不含 Leased）——<b>本实例趋势值</b>，非全局精确。</summary>
    /// <remarks>
    /// P0-1：仅计数 Pending 行，Leased 行不计入（已被 worker 持有，等待 Ack 或过期回滚）。
    /// P2：返回<b>本实例</b> <see cref="Interlocked"/> 维护的本地 counter 近似值（不访问 DB）：
    /// <list type="bullet">
    ///   <item>新实例启动时为 0，<b>不反映 DB 已有 backlog</b>。</item>
    ///   <item>其他实例的 Enqueue/Lease 不会被本地感知。</item>
    ///   <item>并发竞态下可能短暂为负，导出时已 <see cref="Math.Max(int,int)"/> clamp 到 0。</item>
    /// </list>
    /// <b>不可用于调度或安全判断</b>（如 shutdown、限流、拒绝请求）；生产指标应使用
    /// <see cref="GetPendingInstructionCountAsync"/> 或后台聚合服务导出的 <c>global_pending_count</c>。
    /// </remarks>
    [Obsolete("本实例趋势值，非全局精确；不可用于调度/安全判断。Use GetPendingInstructionCountAsync for exact DB count, or consume global_pending_count metric from PendingCountMetricsService.")]
    public int PendingInstructionCount => Math.Max(0, _pendingInstructionCountApprox);

    /// <summary>
    /// P2：异步获取 inbox 中 Pending 指令数（推荐用于热路径）。
    /// 纯异步 <c>ExecuteScalarAsync</c>，不阻塞；返回 <b>DB 精确值（全局，跨实例）</b>——
    /// 与 <see cref="PendingInstructionCount"/>（本实例趋势值）不同，此值反映所有实例的累积状态，可用于调度/安全判断。
    /// </summary>
    public async ValueTask<int> GetPendingInstructionCountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"SELECT COUNT(*) FROM {Table("kernel_transport_inbox")} WHERE state = 'Pending';";
        var count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var exact = count is int i ? i : Convert.ToInt32(count);
        // 顺便同步本地 counter，避免长期漂移
        _pendingInstructionCountApprox = exact;
        return exact;
    }

    /// <summary>当前 outbox 中 state='Pending' 的结果数（不含 Leased）——<b>本实例趋势值</b>，非全局精确。</summary>
    /// <remarks>
    /// P0-1：仅计数 Pending 行，Leased 行不计入。
    /// P2：返回<b>本实例</b> <see cref="Interlocked"/> 维护的本地 counter 近似值（不访问 DB）：
    /// <list type="bullet">
    ///   <item>新实例启动时为 0，<b>不反映 DB 已有 backlog</b>。</item>
    ///   <item>其他实例的 SendResult/LeaseResult 不会被本地感知。</item>
    ///   <item>并发竞态下可能短暂为负，导出时已 <see cref="Math.Max(int,int)"/> clamp 到 0。</item>
    /// </list>
    /// <b>不可用于调度或安全判断</b>（如 shutdown、限流、拒绝请求）；生产指标应使用
    /// <see cref="GetPendingResultCountAsync"/> 或后台聚合服务导出的 <c>global_pending_count</c>。
    /// </remarks>
    [Obsolete("本实例趋势值，非全局精确；不可用于调度/安全判断。Use GetPendingResultCountAsync for exact DB count, or consume global_pending_count metric from PendingCountMetricsService.")]
    public int PendingResultCount => Math.Max(0, _pendingResultCountApprox);

    /// <summary>
    /// P2：异步获取 outbox 中 Pending 结果数（推荐用于热路径）。
    /// 纯异步 <c>ExecuteScalarAsync</c>，不阻塞；返回 <b>DB 精确值（全局，跨实例）</b>——
    /// 与 <see cref="PendingResultCount"/>（本实例趋势值）不同，此值反映所有实例的累积状态，可用于调度/安全判断。
    /// </summary>
    public async ValueTask<int> GetPendingResultCountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"SELECT COUNT(*) FROM {Table("kernel_transport_outbox")} WHERE state = 'Pending';";
        var count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var exact = count is int i ? i : Convert.ToInt32(count);
        // 顺便同步本地 counter，避免长期漂移
        _pendingResultCountApprox = exact;
        return exact;
    }

    /// <summary>
    /// P0-6-7：异步获取死信队列（DLQ）行数（DB 精确值，跨实例累积）。
    /// 供 <c>PendingCountMetricsService</c> 定期查询并导出为 <c>global_dead_letter_count</c> 指标。
    /// 持续增长表明消费者持续失败并超过 max_attempts，需人工介入。
    /// </summary>
    public async ValueTask<int> GetDeadLetterCountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"SELECT COUNT(*) FROM {Table("kernel_transport_dead_letter")};";
        var count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return count is int i ? i : Convert.ToInt32(count);
    }

    /// <summary>完成 transport（no-op：PG 表状态持久化，进程退出不影响表数据）。</summary>
    /// <remarks>
    /// 与 <see cref="InProcessTransport.Complete"/> API 对齐。持久化 transport 无需关闭 Channel；
    /// 调用方如需清空 inbox/outbox（如重启时丢弃过期数据）应显式执行 <c>DELETE</c> SQL。
    /// </remarks>
    public void Complete()
    {
        // Intentionally no-op: PG 表状态持久化。
    }
}
