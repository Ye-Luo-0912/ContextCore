using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 持久化 Workspace 配额服务。
/// 替代 <see cref="ContextCore.Service.Security.InMemoryWorkspaceQuotaService"/> 的进程内字典实现，
/// 让生产多实例部署下配额真相源落在数据库：
/// - 每个节点读取同一 ledger，不再各自计算配额；
/// - 节点重启不丢失已用量与预留（workspace_quota_reservations 持久化）；
/// - 同一 Run 的幂等预留跨节点有效（reservation_id 主键幂等）。
/// 预留 / 释放 / 结算通过 ledger 行 FOR UPDATE 串行化，周期过期在预留时惰性重置。
/// </summary>
/// <remarks>
/// workspace 配额上限来源：新 workspace 首次触达时使用构造参数传入的默认上限
/// （由组合根从配置解析）；已配置的 workspace 使用 ledger 行内的持久化上限
/// （原子准入 / SetLimitAsync 写入）。
/// </remarks>
public sealed class PostgresWorkspaceQuotaService : PostgresStoreBase, IWorkspaceQuotaService
{
    private readonly long _defaultMaxTokens;
    private readonly double _defaultMaxCostUsd;
    private readonly TimeSpan _defaultPeriod;

    /// <summary>初始化 Postgres 持久化配额服务。</summary>
    /// <param name="connectionFactory">Postgres 连接工厂。</param>
    /// <param name="serializer">JSON 序列化器（与其它 store 共用实例）。</param>
    /// <param name="migrationRunner">迁移运行器。</param>
    /// <param name="defaultMaxTokens">未配置 workspace 的默认 token 上限（0 = 无限制）。</param>
    /// <param name="defaultMaxCostUsd">未配置 workspace 的默认费用上限（0 = 无限制）。</param>
    /// <param name="defaultPeriod">默认配额周期。</param>
    public PostgresWorkspaceQuotaService(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner,
        long defaultMaxTokens = 0,
        double defaultMaxCostUsd = 0,
        TimeSpan defaultPeriod = default)
        : base(connectionFactory, serializer, migrationRunner)
    {
        _defaultMaxTokens = defaultMaxTokens;
        _defaultMaxCostUsd = defaultMaxCostUsd;
        _defaultPeriod = defaultPeriod > TimeSpan.Zero ? defaultPeriod : TimeSpan.FromHours(1);
    }

    /// <inheritdoc />
    public async ValueTask<WorkspaceQuota> GetQuotaAsync(
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT max_tokens, tokens_used, reserved_tokens,
       max_cost_usd, cost_used_usd, reserved_cost_usd,
       period_seconds, period_started_at
FROM {Table("workspace_quota_ledger")}
WHERE workspace_id = @workspace_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return ReadQuota(workspaceId, reader);
        }

        // 未触达过的 workspace：返回配置默认上限（与进程内实现的惰性初始化语义一致，不落库）。
        return DefaultQuota(workspaceId);
    }

    /// <inheritdoc />
    public async ValueTask<QuotaReservationResult> ReserveAsync(
        string workspaceId,
        string reservationId,
        long tokens,
        double costUsd,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        // 幂等快路径：预留已存在 → 直接成功（不重复占容量，与进程内实现一致）。
        await using var probeConnection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (await ReservationExistsAsync(probeConnection, workspaceId, reservationId, cancellationToken).ConfigureAwait(false))
        {
            return new QuotaReservationResult
            {
                Allowed = true,
                ReservationId = reservationId,
                UpdatedQuota = await GetQuotaAsync(workspaceId, cancellationToken).ConfigureAwait(false)
            };
        }

        var now = DateTimeOffset.UtcNow;
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // 1. 确保 ledger 行存在（幂等）；未配置的 workspace 使用默认上限。
            await using (var seedCmd = connection.CreateCommand())
            {
                seedCmd.Transaction = transaction;
                seedCmd.CommandTimeout = Options.CommandTimeoutSeconds;
                seedCmd.CommandText = $"""
INSERT INTO {Table("workspace_quota_ledger")} (
    workspace_id, max_tokens, tokens_used, reserved_tokens,
    max_cost_usd, cost_used_usd, reserved_cost_usd,
    period_seconds, period_started_at, updated_at)
VALUES (
    @workspace_id, @max_tokens, 0, 0,
    @max_cost_usd, 0, 0,
    @period_seconds, @now, @now)
ON CONFLICT (workspace_id) DO NOTHING;
""";
                seedCmd.Parameters.AddWithValue("workspace_id", workspaceId);
                seedCmd.Parameters.AddWithValue("max_tokens", _defaultMaxTokens);
                seedCmd.Parameters.AddWithValue("max_cost_usd", _defaultMaxCostUsd);
                seedCmd.Parameters.AddWithValue("period_seconds", (long)_defaultPeriod.TotalSeconds);
                seedCmd.Parameters.AddWithValue("now", now);
                await seedCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // 2. 锁定 ledger 行（串行化同一 workspace 的并发预留/结算）。
            LedgerState ledger;
            await using (var lockCmd = connection.CreateCommand())
            {
                lockCmd.Transaction = transaction;
                lockCmd.CommandTimeout = Options.CommandTimeoutSeconds;
                lockCmd.CommandText = $"""
SELECT max_tokens, tokens_used, reserved_tokens,
       max_cost_usd, cost_used_usd, reserved_cost_usd,
       period_seconds, period_started_at
FROM {Table("workspace_quota_ledger")}
WHERE workspace_id = @workspace_id
FOR UPDATE;
""";
                lockCmd.Parameters.AddWithValue("workspace_id", workspaceId);
                await using var reader = await lockCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    throw new InvalidOperationException($"配额 ledger 行缺失：workspace_id={workspaceId}。");
                }
                ledger = ReadLedger(reader);
            }

            // 3. 周期过期 → 惰性轮转：新周期已用归零，但已预留必须按现存 reservation 行
            //    重新求和（跨周期长 Run 的预留继续保留、继续计入新周期容量），
            //    否则周期切换后已预留被清零 → 过度放行 + Actualize 时跨周期错误归属。
            if (ledger.PeriodSeconds > 0 && now >= ledger.PeriodStartedAt.AddSeconds(ledger.PeriodSeconds))
            {
                await using (var resetCmd = connection.CreateCommand())
                {
                    resetCmd.Transaction = transaction;
                    resetCmd.CommandTimeout = Options.CommandTimeoutSeconds;
                    resetCmd.CommandText = $"""
UPDATE {Table("workspace_quota_ledger")}
SET tokens_used = 0,
    cost_used_usd = 0,
    reserved_tokens = COALESCE((
        SELECT SUM(tokens) FROM {Table("workspace_quota_reservations")}
        WHERE workspace_id = @workspace_id), 0),
    reserved_cost_usd = COALESCE((
        SELECT SUM(cost_usd) FROM {Table("workspace_quota_reservations")}
        WHERE workspace_id = @workspace_id), 0),
    period_started_at = @now, updated_at = @now
WHERE workspace_id = @workspace_id;
""";
                    resetCmd.Parameters.AddWithValue("workspace_id", workspaceId);
                    resetCmd.Parameters.AddWithValue("now", now);
                    await resetCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                ledger = ledger with
                {
                    TokensUsed = 0,
                    CostUsedUsd = 0,
                    ReservedTokens = await SumReservedAsync(connection, workspaceId, cancellationToken, transaction),
                    ReservedCostUsd = await SumReservedCostAsync(connection, workspaceId, cancellationToken, transaction),
                    PeriodStartedAt = now
                };
            }

            // 4. 锁定后复查预留是否已存在（并发幂等重放：等待锁期间另一事务已插入）。
            if (await ReservationExistsAsync(connection, workspaceId, reservationId, cancellationToken, transaction).ConfigureAwait(false))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new QuotaReservationResult
                {
                    Allowed = true,
                    ReservationId = reservationId,
                    UpdatedQuota = QuotaFromLedger(workspaceId, ledger)
                };
            }

            // 5. 容量判定（Max=0 视为无限制；已用 + 已预留 + 本次预留计入）。
            if (ledger.MaxTokens > 0 && ledger.TokensUsed + ledger.ReservedTokens + tokens > ledger.MaxTokens)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new QuotaReservationResult
                {
                    Allowed = false,
                    ReservationId = reservationId,
                    FailureReason = $"Token 配额不足：已用 {ledger.TokensUsed}、已预留 {ledger.ReservedTokens}、上限 {ledger.MaxTokens}，本次预留 {tokens}。",
                    UpdatedQuota = QuotaFromLedger(workspaceId, ledger)
                };
            }
            if (ledger.MaxCostUsd > 0 && ledger.CostUsedUsd + ledger.ReservedCostUsd + costUsd > ledger.MaxCostUsd)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new QuotaReservationResult
                {
                    Allowed = false,
                    ReservationId = reservationId,
                    FailureReason = $"费用配额不足：已用 {ledger.CostUsedUsd:F2}、已预留 {ledger.ReservedCostUsd:F2}、上限 {ledger.MaxCostUsd:F2} USD，本次预留 {costUsd:F2}。",
                    UpdatedQuota = QuotaFromLedger(workspaceId, ledger)
                };
            }

            // 6. 写入预留行 + ledger 已预留增量（同一事务）。
            await using (var reserveCmd = connection.CreateCommand())
            {
                reserveCmd.Transaction = transaction;
                reserveCmd.CommandTimeout = Options.CommandTimeoutSeconds;
                reserveCmd.CommandText = $"""
INSERT INTO {Table("workspace_quota_reservations")} (
    reservation_id, workspace_id, tokens, cost_usd, created_at)
VALUES (@reservation_id, @workspace_id, @tokens, @cost_usd, @now)
ON CONFLICT (workspace_id, reservation_id) DO NOTHING;
""";
                reserveCmd.Parameters.AddWithValue("reservation_id", reservationId);
                reserveCmd.Parameters.AddWithValue("workspace_id", workspaceId);
                reserveCmd.Parameters.AddWithValue("tokens", tokens);
                reserveCmd.Parameters.AddWithValue("cost_usd", costUsd);
                reserveCmd.Parameters.AddWithValue("now", now);
                await reserveCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var applyCmd = connection.CreateCommand())
            {
                applyCmd.Transaction = transaction;
                applyCmd.CommandTimeout = Options.CommandTimeoutSeconds;
                applyCmd.CommandText = $"""
UPDATE {Table("workspace_quota_ledger")}
SET reserved_tokens = reserved_tokens + @tokens,
    reserved_cost_usd = reserved_cost_usd + @cost_usd,
    updated_at = @now
WHERE workspace_id = @workspace_id;
""";
                applyCmd.Parameters.AddWithValue("workspace_id", workspaceId);
                applyCmd.Parameters.AddWithValue("tokens", tokens);
                applyCmd.Parameters.AddWithValue("cost_usd", costUsd);
                applyCmd.Parameters.AddWithValue("now", now);
                await applyCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new QuotaReservationResult
            {
                Allowed = true,
                ReservationId = reservationId,
                UpdatedQuota = QuotaFromLedger(workspaceId, ledger with
                {
                    ReservedTokens = ledger.ReservedTokens + tokens,
                    ReservedCostUsd = ledger.ReservedCostUsd + costUsd
                })
            };
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask ReleaseAsync(
        string workspaceId,
        string reservationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // 幂等：未知 reservationId 视为已释放（0 行删除 → 无操作）。
            long? releasedTokens = null;
            double? releasedCost = null;
            string? targetWorkspace = null;
            await using (var delCmd = connection.CreateCommand())
            {
                delCmd.Transaction = transaction;
                delCmd.CommandTimeout = Options.CommandTimeoutSeconds;
                delCmd.CommandText = $"""
DELETE FROM {Table("workspace_quota_reservations")}
WHERE workspace_id = @workspace_id
  AND reservation_id = @reservation_id
RETURNING workspace_id, tokens, cost_usd;
""";
                delCmd.Parameters.AddWithValue("workspace_id", workspaceId);
                delCmd.Parameters.AddWithValue("reservation_id", reservationId);
                await using var reader = await delCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    targetWorkspace = reader.GetString(reader.GetOrdinal("workspace_id"));
                    releasedTokens = reader.GetInt64(reader.GetOrdinal("tokens"));
                    releasedCost = reader.GetDouble(reader.GetOrdinal("cost_usd"));
                }
            }

            if (targetWorkspace is not null && releasedTokens.HasValue)
            {
                await using var updateCmd = connection.CreateCommand();
                updateCmd.Transaction = transaction;
                updateCmd.CommandTimeout = Options.CommandTimeoutSeconds;
                updateCmd.CommandText = $"""
UPDATE {Table("workspace_quota_ledger")}
SET reserved_tokens = GREATEST(0, reserved_tokens - @tokens),
    reserved_cost_usd = GREATEST(0, reserved_cost_usd - @cost_usd),
    updated_at = @now
WHERE workspace_id = @workspace_id;
""";
                updateCmd.Parameters.AddWithValue("workspace_id", targetWorkspace);
                updateCmd.Parameters.AddWithValue("tokens", releasedTokens.Value);
                updateCmd.Parameters.AddWithValue("cost_usd", releasedCost ?? 0);
                updateCmd.Parameters.AddWithValue("now", now);
                await updateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<QuotaConsumptionResult> ActualizeAsync(
        string workspaceId,
        string reservationId,
        long actualTokens,
        double actualCostUsd,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // 幂等：未知 reservationId 视为已结算（0 行删除 → 无操作，按传入 workspace 返回快照）。
            long? reservedTokens = null;
            double? reservedCost = null;
            string? targetWorkspace = null;
            await using (var delCmd = connection.CreateCommand())
            {
                delCmd.Transaction = transaction;
                delCmd.CommandTimeout = Options.CommandTimeoutSeconds;
                delCmd.CommandText = $"""
DELETE FROM {Table("workspace_quota_reservations")}
WHERE workspace_id = @workspace_id
  AND reservation_id = @reservation_id
RETURNING workspace_id, tokens, cost_usd;
""";
                delCmd.Parameters.AddWithValue("workspace_id", workspaceId);
                delCmd.Parameters.AddWithValue("reservation_id", reservationId);
                await using var reader = await delCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    targetWorkspace = reader.GetString(reader.GetOrdinal("workspace_id"));
                    reservedTokens = reader.GetInt64(reader.GetOrdinal("tokens"));
                    reservedCost = reader.GetDouble(reader.GetOrdinal("cost_usd"));
                }
            }

            if (targetWorkspace is not null && reservedTokens.HasValue)
            {
                // 预留转正：按实际用量计入消耗，释放剩余预留（多退少补）。
                await using var updateCmd = connection.CreateCommand();
                updateCmd.Transaction = transaction;
                updateCmd.CommandTimeout = Options.CommandTimeoutSeconds;
                updateCmd.CommandText = $"""
UPDATE {Table("workspace_quota_ledger")}
SET reserved_tokens = GREATEST(0, reserved_tokens - @reserved_tokens),
    reserved_cost_usd = GREATEST(0, reserved_cost_usd - @reserved_cost_usd),
    tokens_used = tokens_used + @actual_tokens,
    cost_used_usd = cost_used_usd + @actual_cost_usd,
    updated_at = @now
WHERE workspace_id = @workspace_id;
""";
                updateCmd.Parameters.AddWithValue("workspace_id", targetWorkspace);
                updateCmd.Parameters.AddWithValue("reserved_tokens", reservedTokens.Value);
                updateCmd.Parameters.AddWithValue("reserved_cost_usd", reservedCost ?? 0);
                updateCmd.Parameters.AddWithValue("actual_tokens", actualTokens);
                updateCmd.Parameters.AddWithValue("actual_cost_usd", actualCostUsd);
                updateCmd.Parameters.AddWithValue("now", now);
                await updateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                targetWorkspace = workspaceId;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new QuotaConsumptionResult
            {
                Allowed = true,
                UpdatedQuota = await GetQuotaAsync(targetWorkspace, cancellationToken).ConfigureAwait(false)
            };
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask ResetAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var resetCmd = connection.CreateCommand();
            resetCmd.Transaction = transaction;
            resetCmd.CommandTimeout = Options.CommandTimeoutSeconds;
            resetCmd.CommandText = $"""
UPDATE {Table("workspace_quota_ledger")}
SET tokens_used = 0, reserved_tokens = 0,
    cost_used_usd = 0, reserved_cost_usd = 0,
    period_started_at = @now, updated_at = @now
WHERE workspace_id = @workspace_id;

DELETE FROM {Table("workspace_quota_reservations")}
WHERE workspace_id = @workspace_id;
""";
            resetCmd.Parameters.AddWithValue("workspace_id", workspaceId);
            resetCmd.Parameters.AddWithValue("now", now);
            await resetCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask SetLimitAsync(
        string workspaceId,
        long maxTokens,
        double maxCostUsd,
        TimeSpan period,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var periodSeconds = period > TimeSpan.Zero ? (long)period.TotalSeconds : 3600;
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var upsertCmd = connection.CreateCommand();
            upsertCmd.Transaction = transaction;
            upsertCmd.CommandTimeout = Options.CommandTimeoutSeconds;
            // 只更新上限（max_tokens / max_cost_usd / period_seconds）与更新时间戳：
            // 绝不清零 usage / reserved / period_started_at——SetLimit 是"改上限"不是 Reset。
            // 若兼做 Reset，旧 reservation 行不清除却把 reserved 清零，会同时产生
            // 过度放行与跨周期错误归属；显式重置应走 ResetQuotaAsync（清 ledger + 删预留）。
            upsertCmd.CommandText = $"""
INSERT INTO {Table("workspace_quota_ledger")} (
    workspace_id, max_tokens, tokens_used, reserved_tokens,
    max_cost_usd, cost_used_usd, reserved_cost_usd,
    period_seconds, period_started_at, updated_at)
VALUES (
    @workspace_id, @max_tokens, 0, 0,
    @max_cost_usd, 0, 0,
    @period_seconds, @now, @now)
ON CONFLICT (workspace_id) DO UPDATE SET
    max_tokens = EXCLUDED.max_tokens,
    max_cost_usd = EXCLUDED.max_cost_usd,
    period_seconds = EXCLUDED.period_seconds,
    updated_at = EXCLUDED.updated_at;
""";
            upsertCmd.Parameters.AddWithValue("workspace_id", workspaceId);
            upsertCmd.Parameters.AddWithValue("max_tokens", maxTokens);
            upsertCmd.Parameters.AddWithValue("max_cost_usd", maxCostUsd);
            upsertCmd.Parameters.AddWithValue("period_seconds", periodSeconds);
            upsertCmd.Parameters.AddWithValue("now", now);
            await upsertCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<bool> ReservationExistsAsync(
        NpgsqlConnection connection,
        string workspaceId,
        string reservationId,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT 1 FROM {Table("workspace_quota_reservations")}
WHERE workspace_id = @workspace_id
  AND reservation_id = @reservation_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("reservation_id", reservationId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null and not DBNull;
    }

    /// <summary>当前工作区现存预留行 token 总和（周期轮转后保留跨周期长 Run 的预留容量）。</summary>
    private async ValueTask<long> SumReservedAsync(
        NpgsqlConnection connection,
        string workspaceId,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT COALESCE(SUM(tokens), 0) FROM {Table("workspace_quota_reservations")}
WHERE workspace_id = @workspace_id;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    /// <summary>当前工作区现存预留行 cost 总和（周期轮转后保留跨周期长 Run 的预留容量）。</summary>
    private async ValueTask<double> SumReservedCostAsync(
        NpgsqlConnection connection,
        string workspaceId,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT COALESCE(SUM(cost_usd), 0) FROM {Table("workspace_quota_reservations")}
WHERE workspace_id = @workspace_id;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? 0 : Convert.ToDouble(result);
    }

    private WorkspaceQuota DefaultQuota(string workspaceId) => new()
    {
        WorkspaceId = workspaceId,
        MaxTokens = _defaultMaxTokens,
        TokensUsed = 0,
        ReservedTokens = 0,
        MaxCostUsd = _defaultMaxCostUsd,
        CostUsedUsd = 0,
        ReservedCostUsd = 0,
        Period = _defaultPeriod,
        PeriodStartedAt = DateTimeOffset.UtcNow
    };

    private static WorkspaceQuota ReadQuota(string workspaceId, NpgsqlDataReader reader)
        => QuotaFromLedger(workspaceId, ReadLedger(reader));

    /// <summary>由 ledger 状态构造配额快照（事务内锁定/更新后的状态，避免额外查询）。</summary>
    private static WorkspaceQuota QuotaFromLedger(string workspaceId, LedgerState ledger) => new()
    {
        WorkspaceId = workspaceId,
        MaxTokens = ledger.MaxTokens,
        TokensUsed = ledger.TokensUsed,
        ReservedTokens = ledger.ReservedTokens,
        MaxCostUsd = ledger.MaxCostUsd,
        CostUsedUsd = ledger.CostUsedUsd,
        ReservedCostUsd = ledger.ReservedCostUsd,
        Period = TimeSpan.FromSeconds(ledger.PeriodSeconds),
        PeriodStartedAt = ledger.PeriodStartedAt
    };

    private static LedgerState ReadLedger(NpgsqlDataReader reader) => new()
    {
        MaxTokens = reader.GetInt64(reader.GetOrdinal("max_tokens")),
        TokensUsed = reader.GetInt64(reader.GetOrdinal("tokens_used")),
        ReservedTokens = reader.GetInt64(reader.GetOrdinal("reserved_tokens")),
        MaxCostUsd = reader.GetDouble(reader.GetOrdinal("max_cost_usd")),
        CostUsedUsd = reader.GetDouble(reader.GetOrdinal("cost_used_usd")),
        ReservedCostUsd = reader.GetDouble(reader.GetOrdinal("reserved_cost_usd")),
        PeriodSeconds = reader.GetInt64(reader.GetOrdinal("period_seconds")),
        PeriodStartedAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("period_started_at"))
    };

    private sealed record LedgerState
    {
        public long MaxTokens { get; init; }
        public long TokensUsed { get; init; }
        public long ReservedTokens { get; init; }
        public double MaxCostUsd { get; init; }
        public double CostUsedUsd { get; init; }
        public double ReservedCostUsd { get; init; }
        public long PeriodSeconds { get; init; }
        public DateTimeOffset PeriodStartedAt { get; init; }
    }
}
