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
///   8. <see cref="PendingInstructionCount"/> / <see cref="PendingResultCount"/> 同步查询 state='Pending' 行数。
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
    /// P2：inbox Pending 指令数的本地近似 counter。
    /// Enqueue (+1) / Lease (-1) / Nack (+1) 时维护；RequeueExpired 时重新同步。
    /// 不访问 DB，仅供 <see cref="PendingInstructionCount"/> 同步属性快速读取；
    /// 精确值用 <see cref="GetPendingInstructionCountAsync"/>。
    /// </summary>
    private volatile int _pendingInstructionCountApprox;

    /// <summary>
    /// P2：outbox Pending 结果数的本地近似 counter。
    /// SendResult (+1) / LeaseResult (-1) / NackResult (+1) 时维护；RequeueExpired 时重新同步。
    /// 不访问 DB，仅供 <see cref="PendingResultCount"/> 同步属性快速读取；
    /// 精确值用 <see cref="GetPendingResultCountAsync"/>。
    /// </summary>
    private volatile int _pendingResultCountApprox;

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
    /// P1：单次连接内循环 DELETE，收集失败的 instruction_id（token 不匹配或状态非 Leased）。
    /// 部分成功不抛异常；调用方根据返回的失败列表决定后续处理。
    /// </remarks>
    public async ValueTask<IReadOnlyList<string>> AckBatchAsync(IReadOnlyList<(string InstructionId, string LeaseToken)> acks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acks);
        if (acks.Count == 0)
        {
            return Array.Empty<string>();
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var failed = new List<string>();
        // 单次连接内循环 DELETE；不为整体事务（部分失败不影响其他 ack）。
        foreach (var (instructionId, leaseToken) in acks)
        {
            if (string.IsNullOrWhiteSpace(instructionId) || string.IsNullOrWhiteSpace(leaseToken))
            {
                failed.Add(instructionId);
                continue;
            }

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
                failed.Add(instructionId);
            }
            // P2：本地 counter 不变（Pending 计数在 LeaseAsync 时已 -1）
        }

        return failed;
    }

    /// <inheritdoc />
    /// <remarks>
    /// P0-1：Leased → Pending（立即回滚）。清除 lease_owner / lease_expires_at / lease_token，
    /// 让该行可被其他 worker 重新 <see cref="LeaseAsync"/>。0 行受影响时抛 <see cref="InvalidOperationException"/>。
    /// </remarks>
    public async ValueTask NackAsync(string instructionId, string leaseToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instructionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
UPDATE {Table("kernel_transport_inbox")}
SET state = 'Pending',
    lease_owner = NULL,
    lease_expires_at = NULL,
    lease_token = NULL
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
                $"Nack 失败：instruction_id={instructionId} 的租约不匹配、已过期回滚或已确认。");
        }
        // P2：本地 counter 维护（Leased → Pending；Pending 行 +1）
        Interlocked.Increment(ref _pendingInstructionCountApprox);
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

    // ── Outbox：结果写入 ─────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// 使用 <c>INSERT</c> 写入 outbox 一行；result_id 主键由实现生成（GUID）。
    /// 同一 instruction_id 可多次入队（每次失败都新增一行），不冲突。新行默认 state='Pending'。
    /// </remarks>
    public async ValueTask SendResultAsync(AgentKernelResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("kernel_transport_outbox")} (result_id, instruction_id, created_at, data)
VALUES (@result_id, @instruction_id, @created_at, @data);
""";
        command.Parameters.AddWithValue("result_id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("instruction_id", result.InstructionId);
        command.Parameters.AddWithValue("created_at", DateTimeOffset.UtcNow);
        AddJson(command, "data", result);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        // P2：本地 counter 维护（Pending 行 +1）
        Interlocked.Increment(ref _pendingResultCountApprox);
    }

    // ── Outbox：结果租约 ─────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// P0-1：与 <see cref="LeaseAsync"/> 对称，用于结果消费方管理租约生命周期。
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
    /// P1：单次连接内循环 DELETE，收集失败的 result_id（token 不匹配或状态非 Leased）。
    /// 与 <see cref="AckBatchAsync"/> 对称。
    /// </remarks>
    public async ValueTask<IReadOnlyList<string>> AckResultBatchAsync(IReadOnlyList<(string ResultId, string LeaseToken)> acks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acks);
        if (acks.Count == 0)
        {
            return Array.Empty<string>();
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var failed = new List<string>();
        foreach (var (resultId, leaseToken) in acks)
        {
            if (string.IsNullOrWhiteSpace(resultId) || string.IsNullOrWhiteSpace(leaseToken))
            {
                failed.Add(resultId);
                continue;
            }

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
                failed.Add(resultId);
            }
            // P2：本地 counter 不变（Pending 计数在 LeaseResultAsync 时已 -1）
        }

        return failed;
    }

    /// <inheritdoc />
    public async ValueTask NackResultAsync(string resultId, string leaseToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resultId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
UPDATE {Table("kernel_transport_outbox")}
SET state = 'Pending',
    lease_owner = NULL,
    lease_expires_at = NULL,
    lease_token = NULL
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
                $"NackResult 失败：result_id={resultId} 的租约不匹配、已过期回滚或已确认。");
        }
        // P2：本地 counter 维护（Leased → Pending；Pending 行 +1）
        Interlocked.Increment(ref _pendingResultCountApprox);
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

    /// <summary>当前 inbox 中 state='Pending' 的指令数（不含 Leased）。</summary>
    /// <remarks>
    /// P0-1：仅计数 Pending 行，Leased 行不计入（已被 worker 持有，等待 Ack 或过期回滚）。
    /// P2：返回本地 counter 近似值（不访问 DB），可能短暂偏差；精确值用 <see cref="GetPendingInstructionCountAsync"/>。
    /// </remarks>
    [Obsolete("Use GetPendingInstructionCountAsync. Sync property returns a local approximate counter; avoid in hot paths that require exact counts.")]
    public int PendingInstructionCount => _pendingInstructionCountApprox;

    /// <summary>
    /// P2：异步获取 inbox 中 Pending 指令数（推荐用于热路径）。
    /// 纯异步 <c>ExecuteScalarAsync</c>，不阻塞；返回 DB 精确值。
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

    /// <summary>当前 outbox 中 state='Pending' 的结果数（不含 Leased）。</summary>
    /// <remarks>
    /// P0-1：仅计数 Pending 行，Leased 行不计入。
    /// P2：返回本地 counter 近似值（不访问 DB），可能短暂偏差；精确值用 <see cref="GetPendingResultCountAsync"/>。
    /// </remarks>
    [Obsolete("Use GetPendingResultCountAsync. Sync property returns a local approximate counter; avoid in hot paths that require exact counts.")]
    public int PendingResultCount => _pendingResultCountApprox;

    /// <summary>
    /// P2：异步获取 outbox 中 Pending 结果数（推荐用于热路径）。
    /// 纯异步 <c>ExecuteScalarAsync</c>，不阻塞；返回 DB 精确值。
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
