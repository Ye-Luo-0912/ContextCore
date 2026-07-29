using System.Text.RegularExpressions;
using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

// ── P2: Unified Lease/Fencing Infrastructure — PostgreSQL Implementation ──

/// <summary>
/// 租约工作存储的表/列映射配置。描述如何将 <see cref="ILeasedWorkStore{TWork, TLeased}"/>
/// 的通用操作映射到具体的 PostgreSQL 表结构。
/// </summary>
/// <typeparam name="TWork">工作项类型。</typeparam>
/// <remarks>
/// <b>Leader 模式</b>（<see cref="IsLeaderLease"/>=true）对应 agent_run_leases / canary_leader_leases 表：
/// <list type="bullet">
/// <item>必有列：work_id, owner, lease_token, lease_expires_at。</item>
/// <item>可选列：fencing_token, acquired_at。</item>
/// <item>无 state / attempts 列（leader 模式通过行存在性 + lease_expires_at 判断持有状态）。</item>
/// </list>
///
/// <b>Queue 模式</b>（<see cref="IsLeaderLease"/>=false）对应 learning_event_outbox / context_jobs 等表：
/// <list type="bullet">
/// <item>必有列：work_id, state, lease_owner, lease_token, lease_expires_at。</item>
/// <item>可选列：attempts/retry_count, work_payload, acquired_at, last_error。</item>
/// <item>无 fencing_token 列（queue 模式不使用 fencing token，<see cref="LeasedWork{TWork}.FencingToken"/> 恒为 0）。</item>
/// </list>
/// </remarks>
public sealed record LeasedWorkStoreConfiguration<TWork>
{
    /// <summary>完全限定的表名（可含 schema 前缀，如 <c>public.ctx_agent_run_leases</c>）。</summary>
    public required string TableName { get; init; }

    /// <summary>工作项 ID 列名。</summary>
    public required string WorkIdColumn { get; init; }

    /// <summary>租约 token 列名。</summary>
    public required string LeaseTokenColumn { get; init; }

    /// <summary>租约持有者列名。</summary>
    public required string LeaseOwnerColumn { get; init; }

    /// <summary>租约过期时间列名。</summary>
    public required string LeaseExpiresAtColumn { get; init; }

    /// <summary>
    /// Fencing token 列名（Leader 模式必填；Queue 模式为 null）。
    /// 为 null 时 <see cref="LeasedWork{TWork}.FencingToken"/> 恒返回 0。
    /// </summary>
    public string? FencingTokenColumn { get; init; }

    /// <summary>
    /// 状态列名（Queue 模式必填；Leader 模式为 null）。
    /// </summary>
    public string? StateColumn { get; init; }

    /// <summary>
    /// 尝试次数列名（Queue 模式可选；为 null 时不跟踪重试次数，NackAsync 始终回退 Pending）。
    /// </summary>
    public string? AttemptsColumn { get; init; }

    /// <summary>
    /// 工作负载列名（Queue 模式可选；存储序列化的 TWork）。
    /// 为 null 时 <see cref="Work"/> = <see cref="DeserializeWork"/>(workId)（Leader 模式典型用法）。
    /// </summary>
    public string? WorkPayloadColumn { get; init; }

    /// <summary>获取时间列名（可选；为 null 时 <see cref="LeasedWork{TWork}.AcquiredAt"/> 为 null）。</summary>
    public string? AcquiredAtColumn { get; init; }

    /// <summary>最后错误信息列名（可选；NackAsync 时写入 errorMessage）。</summary>
    public string? LastErrorColumn { get; init; }

    /// <summary>排序列名（可选；TryAcquireBatchAsync 的 ORDER BY 列；为 null 时按 WorkIdColumn 排序）。</summary>
    public string? OrderByColumn { get; init; }

    /// <summary>Pending 状态值（Queue 模式）。默认 "Pending"。</summary>
    public string PendingStateValue { get; init; } = "Pending";

    /// <summary>Leased/Processing 状态值（Queue 模式）。默认 "Processing"。</summary>
    public string LeasedStateValue { get; init; } = "Processing";

    /// <summary>Acked/完成 状态值（Queue 模式）。默认 "Acked"。</summary>
    public string AckedStateValue { get; init; } = "Acked";

    /// <summary>DeadLettered 状态值（Queue 模式，attempts 超限时）。默认 "DeadLettered"。</summary>
    public string DeadLetteredStateValue { get; init; } = "DeadLettered";

    /// <summary>是否为 Leader/Hold 租约模式。true=Leader；false=Queue/Outbox。</summary>
    public required bool IsLeaderLease { get; init; }

    /// <summary>序列化工作项为字符串（存储到 WorkPayloadColumn）。</summary>
    public required Func<TWork, string> SerializeWork { get; init; }

    /// <summary>从字符串反序列化工作项。</summary>
    public required Func<string, TWork> DeserializeWork { get; init; }

    /// <summary>最大重试次数（Queue 模式；超限转 DeadLettered）。默认 5。</summary>
    public int MaxAttempts { get; init; } = 5;
}

/// <summary>
/// PostgreSQL 专属的租约工作存储接口，扩展 <see cref="ILeasedWorkStore{TWork, TLeased}"/>
/// 以提供 <see cref="ExecuteFencedAsync"/> 方法（需要 Npgsql 原生连接/事务类型）。
/// </summary>
/// <typeparam name="TWork">工作项类型。</typeparam>
/// <remarks>
/// <see cref="ExecuteFencedAsync"/> 定义在此接口而非 <see cref="ILeasedWorkStore{TWork, TLeased}"/> 上，
/// 因为 Abstractions 项目不引用 Npgsql，无法在 provider-agnostic 接口中暴露 Npgsql 类型。
/// </remarks>
public interface IPostgresLeasedWorkStore<TWork> : ILeasedWorkStore<TWork, LeasedWork<TWork>>
{
    /// <summary>
    /// 执行租约保护的原子操作（fencing 校验 → 用户操作，单事务）。
    /// 在同一 PostgreSQL 事务内：
    /// <list type="number">
    /// <item>SELECT FOR UPDATE 锁住 lease 行，校验 fencing_token + lease_token + 未过期。</item>
    /// <item>校验失败 → 回滚，抛 <see cref="InvalidOperationException"/>（"Lease lost"）。</item>
    /// <item>校验成功 → 执行 <paramref name="operation"/>，传入同一连接/事务。</item>
    /// <item>提交事务。operation 抛异常 → 回滚并重新抛出。</item>
    /// </list>
    /// </summary>
    /// <typeparam name="TResult">操作返回类型。</typeparam>
    /// <param name="workId">工作项 ID。</param>
    /// <param name="leaseToken">租约 token。</param>
    /// <param name="fencingToken">fencing token（Leader 模式；Queue 模式传 0）。</param>
    /// <param name="operation">在事务内执行的业务操作。传入的 connection/transaction 已绑定到同一事务，
    /// operation 内创建的 command 必须设置 <c>command.Transaction = transaction</c>。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>操作返回值。</returns>
    ValueTask<TResult> ExecuteFencedAsync<TResult>(
        string workId,
        string leaseToken,
        long fencingToken,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, ValueTask<TResult>> operation,
        CancellationToken ct = default);
}

/// <summary>
/// 通用 PostgreSQL 租约工作存储实现。通过 <see cref="LeasedWorkStoreConfiguration{TWork}"/>
/// 配置表名与列映射，统一支撑 Leader/Hold 与 Queue/Outbox 两类租约模式。
/// </summary>
/// <typeparam name="TWork">工作项类型。</typeparam>
/// <remarks>
/// <b>设计目标</b>：消除 PostgresAgentRunLease / PostgresCanaryLeaderLease 等实现间 ~95% 的重复代码，
/// 同时为 Queue/Outbox 类租约（LearningEventOutbox / JobQueue / DurableTransport 等）提供统一抽象。
///
/// <b>线程安全</b>：所有方法无状态，可并发调用。每次调用独立连接。
///
/// <b>SQL 注入防护</b>：列名/表名在构造时通过 <see cref="ValidateIdentifier"/> 校验（仅允许 [A-Za-z_][A-Za-z0-9_]*）。
/// </remarks>
public sealed partial class PostgresLeasedWorkStore<TWork> : PostgresStoreBase, IPostgresLeasedWorkStore<TWork>
{
    private readonly LeasedWorkStoreConfiguration<TWork> _config;

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifierRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeTableNameRegex();

    /// <summary>初始化 PostgreSQL 租约工作存储。</summary>
    public PostgresLeasedWorkStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner,
        LeasedWorkStoreConfiguration<TWork> configuration)
        : base(connectionFactory, serializer, migrationRunner)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ValidateConfiguration(configuration);
        _config = configuration;
    }

    // ── TryAcquireAsync ──────────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask<LeasedWork<TWork>?> TryAcquireAsync(
        string workId, TimeSpan leaseDuration, string owner, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workId);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "租约有效期必须为正。");

        await EnsureMigratedAsync(ct).ConfigureAwait(false);

        var token = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(leaseDuration);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);

        if (_config.IsLeaderLease)
        {
            return await TryAcquireLeaderAsync(connection, workId, owner, token, now, expiresAt, ct).ConfigureAwait(false);
        }

        return await TryAcquireQueueAsync(connection, workId, owner, token, now, expiresAt, ct).ConfigureAwait(false);
    }

    private async ValueTask<LeasedWork<TWork>?> TryAcquireLeaderAsync(
        NpgsqlConnection connection, string workId, string owner, string token,
        DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken ct)
    {
        var t = _config.TableName;

        var insertCols = new List<string> { _config.WorkIdColumn, _config.LeaseOwnerColumn, _config.LeaseTokenColumn };
        var insertVals = new List<string> { "@work_id", "@owner", "@token" };

        if (_config.FencingTokenColumn is not null)
        {
            insertCols.Add(_config.FencingTokenColumn);
            insertVals.Add("1");
        }
        if (_config.AcquiredAtColumn is not null)
        {
            insertCols.Add(_config.AcquiredAtColumn);
            insertVals.Add("@now");
        }
        insertCols.Add(_config.LeaseExpiresAtColumn);
        insertVals.Add("@expires_at");

        var setClauses = new List<string>
        {
            $"{_config.LeaseOwnerColumn} = EXCLUDED.{_config.LeaseOwnerColumn}",
            $"{_config.LeaseTokenColumn} = EXCLUDED.{_config.LeaseTokenColumn}"
        };
        if (_config.FencingTokenColumn is not null)
            setClauses.Add($"{_config.FencingTokenColumn} = {t}.{_config.FencingTokenColumn} + 1");
        if (_config.AcquiredAtColumn is not null)
            setClauses.Add($"{_config.AcquiredAtColumn} = EXCLUDED.{_config.AcquiredAtColumn}");
        setClauses.Add($"{_config.LeaseExpiresAtColumn} = EXCLUDED.{_config.LeaseExpiresAtColumn}");

        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {t} ({string.Join(", ", insertCols)})
VALUES ({string.Join(", ", insertVals)})
ON CONFLICT ({_config.WorkIdColumn}) DO UPDATE
SET {string.Join(", ", setClauses)}
WHERE {t}.{_config.LeaseExpiresAtColumn} < @now
RETURNING {BuildReturningClause()};
""";
        command.Parameters.AddWithValue("work_id", workId);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("token", token);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("expires_at", expiresAt);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null; // 已被其他实例持有

        return ReadLeasedWork(reader, expiresAt);
    }

    private async ValueTask<LeasedWork<TWork>?> TryAcquireQueueAsync(
        NpgsqlConnection connection, string workId, string owner, string token,
        DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken ct)
    {
        var t = _config.TableName;
        var stateCol = _config.StateColumn!;
        var setCols = new List<string>
        {
            $"{stateCol} = @leased_state",
            $"{_config.LeaseOwnerColumn} = @owner",
            $"{_config.LeaseTokenColumn} = @token",
            $"{_config.LeaseExpiresAtColumn} = @expires_at"
        };
        if (_config.AcquiredAtColumn is not null)
            setCols.Add($"{_config.AcquiredAtColumn} = @now");

        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
UPDATE {t}
SET {string.Join(", ", setCols)}
WHERE {_config.WorkIdColumn} = @work_id
  AND ({stateCol} = @pending_state
       OR ({stateCol} = @leased_state AND {_config.LeaseExpiresAtColumn} IS NOT NULL AND {_config.LeaseExpiresAtColumn} <= @now))
RETURNING {BuildReturningClause()};
""";
        command.Parameters.AddWithValue("work_id", workId);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("token", token);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("expires_at", expiresAt);
        command.Parameters.AddWithValue("pending_state", _config.PendingStateValue);
        command.Parameters.AddWithValue("leased_state", _config.LeasedStateValue);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null; // 不存在或已被持有

        return ReadLeasedWork(reader, expiresAt);
    }

    // ── TryAcquireBatchAsync ─────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<LeasedWork<TWork>>> TryAcquireBatchAsync(
        int limit, TimeSpan leaseDuration, string owner, CancellationToken ct = default)
    {
        if (_config.IsLeaderLease)
            throw new NotSupportedException("TryAcquireBatchAsync 仅支持 Queue/Outbox 模式，Leader 模式不支持批量获取。");

        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (limit <= 0) return Array.Empty<LeasedWork<TWork>>();
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "租约有效期必须为正。");

        await EnsureMigratedAsync(ct).ConfigureAwait(false);

        var t = _config.TableName;
        var stateCol = _config.StateColumn!;
        var orderCol = _config.OrderByColumn ?? _config.WorkIdColumn;
        var token = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(leaseDuration);

        var setCols = new List<string>
        {
            $"{stateCol} = @leased_state",
            $"{_config.LeaseOwnerColumn} = @owner",
            $"{_config.LeaseTokenColumn} = @token",
            $"{_config.LeaseExpiresAtColumn} = @expires_at"
        };
        if (_config.AcquiredAtColumn is not null)
            setCols.Add($"{_config.AcquiredAtColumn} = @now");

        await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $$"""
WITH pending AS (
    SELECT {{_config.WorkIdColumn}} FROM {{t}}
    WHERE {{stateCol}} = @pending_state
       OR ({{stateCol}} = @leased_state AND {{_config.LeaseExpiresAtColumn}} IS NOT NULL AND {{_config.LeaseExpiresAtColumn}} <= @now)
    ORDER BY {{orderCol}} ASC
    LIMIT @limit
    FOR UPDATE SKIP LOCKED
)
UPDATE {{t}}
SET {{string.Join(", ", setCols)}}
FROM pending
WHERE {{t}}.{{_config.WorkIdColumn}} = pending.{{_config.WorkIdColumn}}
RETURNING {{BuildReturningClause()}};
""";
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("token", token);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("expires_at", expiresAt);
        command.Parameters.AddWithValue("pending_state", _config.PendingStateValue);
        command.Parameters.AddWithValue("leased_state", _config.LeasedStateValue);
        command.Parameters.AddWithValue("limit", limit);

        var results = new List<LeasedWork<TWork>>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                results.Add(ReadLeasedWork(reader, expiresAt));
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return results;
    }

    // ── RenewAsync ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask<bool> RenewAsync(
        string workId, string leaseToken, TimeSpan extension, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
        if (extension <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(extension), "续租时间必须为正。");

        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        var newExpiresAt = DateTimeOffset.UtcNow.Add(extension);
        var t = _config.TableName;

        var conditions = new List<string>
        {
            $"{_config.WorkIdColumn} = @work_id",
            $"{_config.LeaseTokenColumn} = @token"
        };
        if (!_config.IsLeaderLease && _config.StateColumn is not null)
            conditions.Add($"{_config.StateColumn} = @leased_state");

        await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
UPDATE {t}
SET {_config.LeaseExpiresAtColumn} = @new_expires_at
WHERE {string.Join(" AND ", conditions)};
""";
        command.Parameters.AddWithValue("work_id", workId);
        command.Parameters.AddWithValue("token", leaseToken);
        command.Parameters.AddWithValue("new_expires_at", newExpiresAt);
        if (!_config.IsLeaderLease && _config.StateColumn is not null)
            command.Parameters.AddWithValue("leased_state", _config.LeasedStateValue);

        var affected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return affected > 0;
    }

    // ── AckAsync ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask<bool> AckAsync(string workId, string leaseToken, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
        await EnsureMigratedAsync(ct).ConfigureAwait(false);

        var t = _config.TableName;

        if (_config.IsLeaderLease)
        {
            // Leader 模式：DELETE（主动释放）
            await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = Options.CommandTimeoutSeconds;
            command.CommandText = $"""
DELETE FROM {t}
WHERE {_config.WorkIdColumn} = @work_id AND {_config.LeaseTokenColumn} = @token;
""";
            command.Parameters.AddWithValue("work_id", workId);
            command.Parameters.AddWithValue("token", leaseToken);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        }

        // Queue 模式：UPDATE SET state='Acked'
        var stateCol = _config.StateColumn!;
        await using var conn = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = Options.CommandTimeoutSeconds;
        cmd.CommandText = $"""
UPDATE {t}
SET {stateCol} = @acked_state,
    {_config.LeaseOwnerColumn} = NULL,
    {_config.LeaseExpiresAtColumn} = NULL,
    {_config.LeaseTokenColumn} = NULL
WHERE {_config.WorkIdColumn} = @work_id
  AND {_config.LeaseTokenColumn} = @token
  AND {stateCol} = @leased_state;
""";
        cmd.Parameters.AddWithValue("work_id", workId);
        cmd.Parameters.AddWithValue("token", leaseToken);
        cmd.Parameters.AddWithValue("acked_state", _config.AckedStateValue);
        cmd.Parameters.AddWithValue("leased_state", _config.LeasedStateValue);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    // ── NackAsync ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask<bool> NackAsync(
        string workId, string leaseToken, string? errorMessage = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
        await EnsureMigratedAsync(ct).ConfigureAwait(false);

        var t = _config.TableName;

        if (_config.IsLeaderLease)
        {
            // Leader 模式：DELETE（同 Ack，主动释放）
            await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = Options.CommandTimeoutSeconds;
            command.CommandText = $"""
DELETE FROM {t}
WHERE {_config.WorkIdColumn} = @work_id AND {_config.LeaseTokenColumn} = @token;
""";
            command.Parameters.AddWithValue("work_id", workId);
            command.Parameters.AddWithValue("token", leaseToken);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        }

        // Queue 模式：回退 Pending + attempts+1（超限转 DeadLettered）
        var stateCol = _config.StateColumn!;
        var setCols = new List<string>
        {
            $"{_config.LeaseOwnerColumn} = NULL",
            $"{_config.LeaseExpiresAtColumn} = NULL",
            $"{_config.LeaseTokenColumn} = NULL"
        };

        if (_config.AttemptsColumn is not null)
        {
            setCols.Insert(0, $"{_config.AttemptsColumn} = {_config.AttemptsColumn} + 1");
            setCols.Insert(1, $"{stateCol} = CASE WHEN {_config.AttemptsColumn} + 1 >= @max_attempts THEN @dead_lettered_state ELSE @pending_state END");
        }
        else
        {
            setCols.Insert(0, $"{stateCol} = @pending_state");
        }

        if (_config.LastErrorColumn is not null)
            setCols.Add($"{_config.LastErrorColumn} = @error_message");

        await using var conn = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = Options.CommandTimeoutSeconds;
        cmd.CommandText = $"""
UPDATE {t}
SET {string.Join(", ", setCols)}
WHERE {_config.WorkIdColumn} = @work_id
  AND {_config.LeaseTokenColumn} = @token
  AND {stateCol} = @leased_state;
""";
        cmd.Parameters.AddWithValue("work_id", workId);
        cmd.Parameters.AddWithValue("token", leaseToken);
        cmd.Parameters.AddWithValue("pending_state", _config.PendingStateValue);
        cmd.Parameters.AddWithValue("leased_state", _config.LeasedStateValue);
        if (_config.AttemptsColumn is not null)
        {
            cmd.Parameters.AddWithValue("max_attempts", _config.MaxAttempts);
            cmd.Parameters.AddWithValue("dead_lettered_state", _config.DeadLetteredStateValue);
        }
        if (_config.LastErrorColumn is not null)
            cmd.Parameters.AddWithValue("error_message", errorMessage ?? string.Empty);

        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    // ── HeartbeatAsync ───────────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask<bool> HeartbeatAsync(
        string workId, string owner, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workId);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "租约有效期必须为正。");

        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var newExpiresAt = now.Add(leaseDuration);
        var t = _config.TableName;

        var conditions = new List<string>
        {
            $"{_config.WorkIdColumn} = @work_id",
            $"{_config.LeaseOwnerColumn} = @owner",
            $"{_config.LeaseExpiresAtColumn} > @now"
        };
        if (!_config.IsLeaderLease && _config.StateColumn is not null)
            conditions.Add($"{_config.StateColumn} = @leased_state");

        await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
UPDATE {t}
SET {_config.LeaseExpiresAtColumn} = @new_expires_at
WHERE {string.Join(" AND ", conditions)};
""";
        command.Parameters.AddWithValue("work_id", workId);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("new_expires_at", newExpiresAt);
        if (!_config.IsLeaderLease && _config.StateColumn is not null)
            command.Parameters.AddWithValue("leased_state", _config.LeasedStateValue);

        var affected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return affected > 0;
    }

    // ── ReapExpiredAsync ─────────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask<int> ReapExpiredAsync(CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var t = _config.TableName;

        await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        if (_config.IsLeaderLease)
        {
            // Leader 模式：DELETE 过期租约
            command.CommandText = $"""
DELETE FROM {t}
WHERE {_config.LeaseExpiresAtColumn} < @now;
""";
        }
        else
        {
            // Queue 模式：回退过期 Processing 为 Pending
            var stateCol = _config.StateColumn!;
            command.CommandText = $"""
UPDATE {t}
SET {stateCol} = @pending_state,
    {_config.LeaseOwnerColumn} = NULL,
    {_config.LeaseExpiresAtColumn} = NULL,
    {_config.LeaseTokenColumn} = NULL
WHERE {_config.LeaseExpiresAtColumn} < @now AND {stateCol} = @leased_state;
""";
            command.Parameters.AddWithValue("pending_state", _config.PendingStateValue);
            command.Parameters.AddWithValue("leased_state", _config.LeasedStateValue);
        }

        command.Parameters.AddWithValue("now", now);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ── ExecuteFencedAsync ───────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask<TResult> ExecuteFencedAsync<TResult>(
        string workId, string leaseToken, long fencingToken,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, ValueTask<TResult>> operation,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
        ArgumentNullException.ThrowIfNull(operation);
        await EnsureMigratedAsync(ct).ConfigureAwait(false);

        var t = _config.TableName;

        await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            // 步骤 1：lease/fencing 验证（SELECT FOR UPDATE 锁住 lease 行，防止并发续约/释放）
            await using var leaseCmd = connection.CreateCommand();
            leaseCmd.Transaction = transaction;
            leaseCmd.CommandTimeout = Options.CommandTimeoutSeconds;

            var conditions = new List<string>
            {
                $"{_config.WorkIdColumn} = @work_id",
                $"{_config.LeaseTokenColumn} = @lease_token",
                $"{_config.LeaseExpiresAtColumn} > clock_timestamp()"
            };
            leaseCmd.Parameters.AddWithValue("work_id", workId);
            leaseCmd.Parameters.AddWithValue("lease_token", leaseToken);

            if (_config.FencingTokenColumn is not null)
            {
                conditions.Add($"{_config.FencingTokenColumn} = @fencing_token");
                leaseCmd.Parameters.AddWithValue("fencing_token", fencingToken);
            }

            leaseCmd.CommandText = $"""
SELECT 1 FROM {t}
WHERE {string.Join(" AND ", conditions)}
FOR UPDATE;
""";

            var leaseResult = await leaseCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (leaseResult is null)
            {
                // lease 不存在 / token 不匹配 / fencing 不匹配 / 已过期
                throw new InvalidOperationException(
                    $"Lease lost: workId={workId}, fencingToken={fencingToken} — " +
                    "lease 不存在、token/fencing 不匹配或已过期。");
            }

            // 步骤 2：执行用户操作（同一连接/事务）
            var result = await operation(connection, transaction, ct).ConfigureAwait(false);

            // 步骤 3：提交事务
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* 不掩盖原始异常 */ }
            throw;
        }
    }

    // ── 辅助方法 ─────────────────────────────────────────────────────────

    private string BuildReturningClause()
    {
        var cols = new List<string>
        {
            _config.WorkIdColumn,
            _config.LeaseTokenColumn,
            _config.LeaseOwnerColumn,
            _config.LeaseExpiresAtColumn
        };
        if (_config.FencingTokenColumn is not null) cols.Add(_config.FencingTokenColumn);
        if (_config.AcquiredAtColumn is not null) cols.Add(_config.AcquiredAtColumn);
        if (_config.WorkPayloadColumn is not null) cols.Add(_config.WorkPayloadColumn);
        return string.Join(", ", cols);
    }

    private LeasedWork<TWork> ReadLeasedWork(NpgsqlDataReader reader, DateTimeOffset expiresAt)
    {
        var workId = reader.GetString(reader.GetOrdinal(_config.WorkIdColumn));
        var leaseToken = reader.GetString(reader.GetOrdinal(_config.LeaseTokenColumn));
        var owner = reader.GetString(reader.GetOrdinal(_config.LeaseOwnerColumn));

        var fencingToken = 0L;
        if (_config.FencingTokenColumn is not null)
        {
            var ftOrd = reader.GetOrdinal(_config.FencingTokenColumn);
            fencingToken = reader.IsDBNull(ftOrd) ? 0L : reader.GetInt64(ftOrd);
        }

        DateTimeOffset? acquiredAt = null;
        if (_config.AcquiredAtColumn is not null)
        {
            var aaOrd = reader.GetOrdinal(_config.AcquiredAtColumn);
            acquiredAt = reader.IsDBNull(aaOrd) ? null : reader.GetFieldValue<DateTimeOffset>(aaOrd);
        }

        TWork work;
        if (_config.WorkPayloadColumn is not null)
        {
            var wpOrd = reader.GetOrdinal(_config.WorkPayloadColumn);
            var payload = reader.IsDBNull(wpOrd) ? string.Empty : reader.GetString(wpOrd);
            work = _config.DeserializeWork(payload);
        }
        else
        {
            work = _config.DeserializeWork(workId);
        }

        return new LeasedWork<TWork>
        {
            Work = work,
            LeaseToken = leaseToken,
            FencingToken = fencingToken,
            Owner = owner,
            ExpiresAt = expiresAt,
            AcquiredAt = acquiredAt
        };
    }

    private static void ValidateConfiguration(LeasedWorkStoreConfiguration<TWork> config)
    {
        ValidateTableName(config.TableName, nameof(config.TableName));
        ValidateIdentifier(config.WorkIdColumn, nameof(config.WorkIdColumn));
        ValidateIdentifier(config.LeaseTokenColumn, nameof(config.LeaseTokenColumn));
        ValidateIdentifier(config.LeaseOwnerColumn, nameof(config.LeaseOwnerColumn));
        ValidateIdentifier(config.LeaseExpiresAtColumn, nameof(config.LeaseExpiresAtColumn));

        if (config.FencingTokenColumn is not null)
            ValidateIdentifier(config.FencingTokenColumn, nameof(config.FencingTokenColumn));
        if (config.StateColumn is not null)
            ValidateIdentifier(config.StateColumn, nameof(config.StateColumn));
        if (config.AttemptsColumn is not null)
            ValidateIdentifier(config.AttemptsColumn, nameof(config.AttemptsColumn));
        if (config.WorkPayloadColumn is not null)
            ValidateIdentifier(config.WorkPayloadColumn, nameof(config.WorkPayloadColumn));
        if (config.AcquiredAtColumn is not null)
            ValidateIdentifier(config.AcquiredAtColumn, nameof(config.AcquiredAtColumn));
        if (config.LastErrorColumn is not null)
            ValidateIdentifier(config.LastErrorColumn, nameof(config.LastErrorColumn));
        if (config.OrderByColumn is not null)
            ValidateIdentifier(config.OrderByColumn, nameof(config.OrderByColumn));

        if (!config.IsLeaderLease && config.StateColumn is null)
            throw new ArgumentException("Queue/Outbox 模式（IsLeaderLease=false）必须配置 StateColumn。", nameof(config));

        if (config.MaxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(config.MaxAttempts), "MaxAttempts 必须 >= 1。");
    }

    private static void ValidateIdentifier(string name, string paramName)
    {
        if (string.IsNullOrWhiteSpace(name) || !SafeIdentifierRegex().IsMatch(name))
            throw new ArgumentException($"无效的 SQL 标识符: '{name}'（仅允许字母、数字和下划线，且必须以字母或下划线开头）。", paramName);
    }

    private static void ValidateTableName(string name, string paramName)
    {
        if (string.IsNullOrWhiteSpace(name) || !SafeTableNameRegex().IsMatch(name))
            throw new ArgumentException($"无效的 SQL 表名: '{name}'（仅允许字母、数字、下划线和可选 schema 前缀）。", paramName);
    }
}
