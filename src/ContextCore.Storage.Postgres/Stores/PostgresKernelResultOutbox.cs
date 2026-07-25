using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// R29 WP-B-2：PostgreSQL 持久化 Kernel Result Outbox。
/// 替代 <see cref="ContextCore.Core.Services.AgentKernel.InMemoryKernelResultOutbox"/>，
/// 让 HA 场景下未投递的 AgentKernelResult 可跨进程持久化与崩溃恢复重放。
/// </summary>
/// <remarks>
/// 设计要点：
///   1. 表 <c>kernel_result_outbox</c> 以 <c>outbox_id</c> 为主键（GUID，由实现生成）。
///      反规范化 <c>instruction_id</c> 字段以便按指令查询；完整 <see cref="AgentKernelResult"/> 保存在 <c>data jsonb</c>。
///   2. <see cref="EnqueueAsync"/> 使用 <c>INSERT</c> 写入一条 Pending 行。
///      同一 instruction_id 可多次入队（每次失败都新增一条），不冲突。
///   3. <see cref="DequeueAsync"/> 使用 <c>SELECT ... FOR UPDATE SKIP LOCKED</c> 原子获取最旧的 Pending 行
///      并标记为 Dispatched（与 <c>PostgresRelationOutboxStore.AcquirePendingAsync</c> 一致）。
///      返回的 result 可供重放 worker 通过 transport 重新投递。
///   4. <see cref="PendingCount"/> 返回 <c>state = 'Pending'</c> 的行数（同步查询，非精确计数）。
/// </remarks>
public sealed class PostgresKernelResultOutbox : PostgresStoreBase, IPersistentKernelResultOutbox
{
    /// <summary>初始化 Postgres 持久化 Kernel Result Outbox。</summary>
    public PostgresKernelResultOutbox(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <inheritdoc />
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
    }

    /// <inheritdoc />
    public async ValueTask<AgentKernelResult?> DequeueAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // 原子获取最旧的 Pending 行并标记为 Dispatched（FOR UPDATE SKIP LOCKED 支持多 worker 并发）
        command.CommandText = $"""
WITH pending AS (
    SELECT outbox_id, data
    FROM {Table("kernel_result_outbox")}
    WHERE state = 'Pending'
    ORDER BY created_at ASC, outbox_id ASC
    LIMIT 1
    FOR UPDATE SKIP LOCKED
)
UPDATE {Table("kernel_result_outbox")} AS o
SET state = 'Dispatched', updated_at = @updated_at
FROM pending
WHERE o.outbox_id = pending.outbox_id
RETURNING pending.data;
""";
        command.Parameters.AddWithValue("updated_at", DateTimeOffset.UtcNow);
        return await ExecuteScalarJsonAsync<AgentKernelResult>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public int PendingCount
    {
        get
        {
            // 同步查询 Pending 行数；使用 Task.Run 避免异步方法在同步属性中的 sync-over-async
            // 这是属性语义的折衷；调用方应避免在热路径频繁调用。
            using var connection = ConnectionFactory.OpenConnectionAsync(CancellationToken.None).GetAwaiter().GetResult();
            using var command = connection.CreateCommand();
            command.CommandTimeout = Options.CommandTimeoutSeconds;
            command.CommandText = $"""
SELECT COUNT(*) FROM {Table("kernel_result_outbox")} WHERE state = 'Pending';
""";
            var count = command.ExecuteScalar();
            return count is int i ? i : 0;
        }
    }
}
