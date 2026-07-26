using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// R29 WP-B-4：PostgreSQL 持久化 Durable Transport。
/// 替代 <see cref="ContextCore.Core.Services.AgentKernel.InProcessTransport"/>（进程内 Channel），
/// 让 HA 场景下指令（inbox）与结果（outbox）跨进程持久化，支持崩溃恢复后由新实例继续消费。
/// </summary>
/// <remarks>
/// 设计要点：
///   1. 两张表：<c>kernel_transport_inbox</c>（待处理指令）+ <c>kernel_transport_outbox</c>（待读取结果）。
///      每张表以 GUID 主键标识一条记录；<c>created_at</c> 用于 FIFO 排序。
///   2. <see cref="SubmitAsync"/>（inbox 写入）与 <see cref="SendResultAsync"/>（outbox 写入）使用 <c>INSERT</c>；
///      幂等性由调用方保证（同一 instruction_id 多次入队会因主键冲突而失败，符合 exactly-once 语义）。
///   3. <see cref="ReceiveAsync"/>（inbox 读取）与 <see cref="ReceiveResultAsync"/>（outbox 读取）使用
///      <c>SELECT ... FOR UPDATE SKIP LOCKED</c> 原子取最旧记录并 <c>DELETE</c>，支持多 worker 并发。
///   4. <see cref="PendingInstructionCount"/> / <see cref="PendingResultCount"/> 同步查询行数（非精确计数）。
///   5. <see cref="Complete"/> 为 no-op：PG 表状态持久化，进程退出不影响表数据；
///      调用方按需清理（如重启时清空 inbox）。
///
/// 与 <see cref="InProcessTransport"/> 的 API 对齐：
///   - <see cref="SubmitAsync"/> / <see cref="ReceiveAsync"/> / <see cref="SendResultAsync"/> / <see cref="ReceiveResultAsync"/>
///   - <see cref="PendingInstructionCount"/> / <see cref="PendingResultCount"/> / <see cref="Complete"/>
///
/// <b>注意</b>：默认 <see cref="DefaultAgentKernel"/> 不调用 <see cref="ReceiveAsync"/>；
/// 该方法仅供自定义 Kernel 实现从远程 transport 拉取指令使用。
/// </remarks>
public sealed class PostgresDurableTransport : PostgresStoreBase, IDurableTransport
{
    /// <summary>初始化 Postgres 持久化 Durable Transport。</summary>
    public PostgresDurableTransport(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <summary>提交指令到 Transport 的 inbox（持久化）。</summary>
    /// <param name="instruction">要提交的指令。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 使用 <c>INSERT</c> 写入一行；instruction_id 主键保证幂等（重复提交同一 ID 会因主键冲突失败）。
    /// 自定义 Kernel 实现可通过 <see cref="ReceiveAsync"/> 读取；默认 Kernel 不读取 Transport inbox。
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
    }

    /// <inheritdoc />
    /// <remarks>
    /// 原子获取最旧的 inbox 行并 <c>DELETE</c>（<c>FOR UPDATE SKIP LOCKED</c> 支持多 worker 并发）。
    /// Transport 关闭或 inbox 为空时返回 null。
    /// </remarks>
    public async ValueTask<AgentKernelInstruction?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
WITH oldest AS (
    SELECT instruction_id, data
    FROM {Table("kernel_transport_inbox")}
    ORDER BY created_at ASC, instruction_id ASC
    LIMIT 1
    FOR UPDATE SKIP LOCKED
)
DELETE FROM {Table("kernel_transport_inbox")} AS i
USING oldest
WHERE i.instruction_id = oldest.instruction_id
RETURNING oldest.data;
""";
        return await ExecuteScalarJsonAsync<AgentKernelInstruction>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 使用 <c>INSERT</c> 写入 outbox 一行；result_id 主键由实现生成（GUID）。
    /// 同一 instruction_id 可多次入队（每次失败都新增一行），不冲突。
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
    }

    /// <summary>从 outbox 读取下一条结果（持久化，阻塞直到有结果或取消）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>读取到的结果；outbox 为空时返回 null。</returns>
    /// <remarks>
    /// 原子获取最旧的 outbox 行并 <c>DELETE</c>（<c>FOR UPDATE SKIP LOCKED</c> 支持多 worker 并发）。
    /// 与 <see cref="InProcessTransport.ReceiveResultAsync"/> API 对齐。
    /// </remarks>
    public async ValueTask<AgentKernelResult?> ReceiveResultAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
WITH oldest AS (
    SELECT result_id, data
    FROM {Table("kernel_transport_outbox")}
    ORDER BY created_at ASC, result_id ASC
    LIMIT 1
    FOR UPDATE SKIP LOCKED
)
DELETE FROM {Table("kernel_transport_outbox")} AS o
USING oldest
WHERE o.result_id = oldest.result_id
RETURNING oldest.data;
""";
        return await ExecuteScalarJsonAsync<AgentKernelResult>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>当前 inbox 中待处理指令数。</summary>
    public int PendingInstructionCount
    {
        get
        {
            using var connection = ConnectionFactory.OpenConnectionAsync(CancellationToken.None).GetAwaiter().GetResult();
            using var command = connection.CreateCommand();
            command.CommandTimeout = Options.CommandTimeoutSeconds;
            command.CommandText = $"SELECT COUNT(*) FROM {Table("kernel_transport_inbox")};";
            var count = command.ExecuteScalar();
            return count is int i ? i : 0;
        }
    }

    /// <summary>当前 outbox 中待读取结果数。</summary>
    public int PendingResultCount
    {
        get
        {
            using var connection = ConnectionFactory.OpenConnectionAsync(CancellationToken.None).GetAwaiter().GetResult();
            using var command = connection.CreateCommand();
            command.CommandTimeout = Options.CommandTimeoutSeconds;
            command.CommandText = $"SELECT COUNT(*) FROM {Table("kernel_transport_outbox")};";
            var count = command.ExecuteScalar();
            return count is int i ? i : 0;
        }
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
