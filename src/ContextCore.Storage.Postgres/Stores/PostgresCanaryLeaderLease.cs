using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// 任务 D：PostgreSQL 持久化 Canary Leader 租约实现。
/// </summary>
/// <remarks>
/// 确保 <see cref="ContextCore.Core.Services.Evolution.CanaryProgressionHostedService"/>
/// 同一时刻仅一个实例处理同一 run，避免多实例同时推进/回滚同一 Canary。
///
/// <b>租约模型</b>（每个 run_id 至多一条行）：
/// <code>
/// TryAcquireAsync:
///   INSERT INTO canary_leader_leases (run_id, owner, lease_token, acquired_at, lease_expires_at)
///   VALUES (...)
///   ON CONFLICT (run_id) DO UPDATE
///     SET owner = EXCLUDED.owner, lease_token = EXCLUDED.lease_token, ...
///     WHERE canary_leader_leases.lease_expires_at &lt; now
///   RETURNING lease_token;
///   - 无现有行 → INSERT 成功，返回 token
///   - 现有行过期 → ON CONFLICT DO UPDATE WHERE 子句命中，更新并返回 token
///   - 现有行未过期 → ON CONFLICT DO UPDATE WHERE 子句不命中，0 行返回，返回 null
/// </code>
///
/// <b>RenewAsync</b>：UPDATE WHERE lease_token = @token，延长 lease_expires_at。
/// <b>ReleaseAsync</b>：DELETE WHERE lease_token = @token（主动让出）。
/// <b>ReapExpiredAsync</b>：DELETE WHERE lease_expires_at &lt; now（崩溃 leader 持有的过期租约最终释放）。
///
/// 复用 P0-1/P0-2 的租约模式（CAS + token 匹配），但状态机更简单：
/// leader 租约无需 Pending → Leased → Acked 流转，只有 "持有" 与 "未持有" 两个状态。
///
/// <b>Perf-7 严格 HA 单事务接口</b>：本类同时实现 <see cref="ICanaryDecisionApplier"/>，
/// 将 lease/fencing 校验 + pipeline revision CAS + transition audit 写入 + epoch 递增
/// 合并为单一 PostgreSQL 事务，修复旧路径 <c>AdvanceAsync</c> → <c>AdvanceEpochAsync</c>
/// 分两步导致的 HA 正确性问题（旧 Leader 可能已推进 rollout 后 fencing 才失败）。
/// </remarks>
public sealed class PostgresCanaryLeaderLease : PostgresStoreBase, ICanaryLeaderLease, ICanaryDecisionApplier
{
    /// <summary>初始化 PostgreSQL Canary Leader 租约存储。</summary>
    public PostgresCanaryLeaderLease(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <inheritdoc />
    /// <remarks>
    /// 原子 CAS 获取租约：使用 <c>INSERT ... ON CONFLICT DO UPDATE WHERE lease_expires_at &lt; now</c>。
    /// 无现有行或现有行过期时获取成功；现有行未过期时返回 null（已被其他实例持有）。
    /// P12：成功获取时 <c>fencing_token = 旧值 + 1</c>（新插入为 1），RETURNING 返回新的 fencing_token，
    /// 供调用方在副作用 UPDATE（如 AdvanceEpochAsync）的 WHERE 子句中校验。续约（RenewAsync）不递增 fencing_token。
    /// </remarks>
    public async ValueTask<LeasedLeadership?> TryAcquireAsync(
        string runId,
        TimeSpan leaseDuration,
        string owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "租约有效期必须为正。");
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        var token = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(leaseDuration);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // P12：fencing_token 在 ON CONFLICT DO UPDATE 时 = canary_leader_leases.fencing_token + 1（抢占过期），
        // 新插入时 = 1（VALUES 中指定）。RETURNING 同时返回 lease_token 与 fencing_token 以便调用方使用。
        command.CommandText = $"""
INSERT INTO {Table("canary_leader_leases")} (run_id, owner, lease_token, fencing_token, acquired_at, lease_expires_at)
VALUES (@run_id, @owner, @token, 1, @now, @expires_at)
ON CONFLICT (run_id) DO UPDATE
SET owner = EXCLUDED.owner,
    lease_token = EXCLUDED.lease_token,
    fencing_token = {Table("canary_leader_leases")}.fencing_token + 1,
    acquired_at = EXCLUDED.acquired_at,
    lease_expires_at = EXCLUDED.lease_expires_at
WHERE {Table("canary_leader_leases")}.lease_expires_at < @now
RETURNING lease_token, fencing_token;
""";
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("token", token);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("expires_at", expiresAt);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // 0 行返回（ON CONFLICT WHERE 不命中）→ 已被其他实例持有，返回 null
            return null;
        }

        var returnedToken = reader.GetString(reader.GetOrdinal("lease_token"));
        var fencingToken = reader.GetInt64(reader.GetOrdinal("fencing_token"));

        return new LeasedLeadership
        {
            RunId = runId,
            LeaseToken = returnedToken,
            Owner = owner,
            ExpiresAt = expiresAt,
            FencingToken = fencingToken
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// 续租约（leader 心跳）：UPDATE WHERE lease_token = @token AND lease_expires_at > now()，延长 lease_expires_at。
    /// 0 行受影响表示租约已被抢占或已过期，返回 false；调用方应立即停止处理该 run。
    /// 过期检查防止 stale leader 续租已过期的租约（fencing 安全边界）。
    /// </remarks>
    public async ValueTask<bool> RenewAsync(
        string runId,
        string leaseToken,
        TimeSpan extension,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
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
UPDATE {Table("canary_leader_leases")}
SET lease_expires_at = @new_expires_at
WHERE run_id = @run_id
  AND lease_token = @token
  AND lease_expires_at > clock_timestamp();
""";
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("token", leaseToken);
        command.Parameters.AddWithValue("new_expires_at", newExpiresAt);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 释放租约（主动让出 leader）：DELETE WHERE lease_token = @token。
    /// 通常在 run 完成（Promoted）或回滚后调用。0 行受影响不抛异常（租约可能已过期被 reaper 释放）。
    /// </remarks>
    public async ValueTask ReleaseAsync(
        string runId,
        string leaseToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
DELETE FROM {Table("canary_leader_leases")}
WHERE run_id = @run_id
  AND lease_token = @token;
""";
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("token", leaseToken);

        // 0 行受影响不抛异常：租约可能已过期被 reaper 释放，或已被其他实例抢占。
        // ReleaseAsync 是"尽力让出"语义，调用方不关心是否真正由自己释放。
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 回收过期租约（后台清理）：DELETE WHERE lease_expires_at &lt; now。
    /// 应由定时任务（如 <c>CanaryLeaderHostedService</c>）周期性调用，
    /// 确保崩溃 leader 持有的过期租约最终被释放，让其他实例可以重新获取租约。
    /// </remarks>
    public async ValueTask<int> ReapExpiredAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
DELETE FROM {Table("canary_leader_leases")}
WHERE lease_expires_at < @now;
""";
        command.Parameters.AddWithValue("now", now);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // Perf-7：ICanaryDecisionApplier 实现（单一 PostgreSQL 事务）
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// <b>事务流程</b>：
    /// <code>
    /// BEGIN;
    /// -- 1. lease/fencing 验证（SELECT FOR UPDATE 锁住 lease 行，防止并发续约/释放）
    /// SELECT 1 FROM canary_leader_leases
    ///   WHERE run_id = @runId AND fencing_token = @fencingToken
    ///     AND lease_expires_at > clock_timestamp()
    ///   FOR UPDATE;
    /// -- 无行 → ROLLBACK，返回 LeaseLost
    ///
    /// -- 2. 读取当前 pipeline 状态（percentage + revision）
    /// SELECT percentage, revision FROM canary_pipelines WHERE run_id = @runId;
    /// -- revision != @expectedRevision → ROLLBACK，返回 RevisionMismatch
    ///
    /// -- 3. pipeline revision CAS（UPSERT 处理首次初始化 + 后续更新）
    /// INSERT INTO canary_pipelines (run_id, percentage, status, revision, ...)
    ///   VALUES (@runId, @newPct, @newStatus, 1, ...)
    ///   ON CONFLICT (run_id) DO UPDATE SET
    ///     percentage = EXCLUDED.percentage,
    ///     status = EXCLUDED.status,
    ///     revision = canary_pipelines.revision + 1,
    ///     updated_at = now()
    ///   WHERE canary_pipelines.revision = @expectedRevision
    ///   RETURNING revision;
    /// -- 0 行 → ROLLBACK，返回 RevisionMismatch
    ///
    /// -- 4. transition audit 写入（同事务）
    /// INSERT INTO canary_transition_audit (...) VALUES (...);
    ///
    /// -- 5. epoch 更新（UPSERT，同事务）
    /// INSERT INTO canary_run_epochs (run_id, current_epoch, advanced_at)
    ///   VALUES (@runId, @newEpoch, now())
    ///   ON CONFLICT (run_id) DO UPDATE SET
    ///     current_epoch = EXCLUDED.current_epoch,
    ///     advanced_at = EXCLUDED.advanced_at;
    /// COMMIT;
    /// </code>
    /// 任一步骤失败则整个事务 ROLLBACK，确保旧 Leader 无法在 lease 失效后修改 rollout。
    /// </remarks>
    public async ValueTask<CanaryDecisionResult> ApplyCanaryDecisionAsync(
        CanaryDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FencingToken);
        cancellationToken.ThrowIfCancellationRequested();

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        // 解析 fencing token（字符串 → long，与 canary_leader_leases.fencing_token bigint 列匹配）
        if (!long.TryParse(request.FencingToken, out var fencingTokenLong) || fencingTokenLong <= 0)
        {
            return new CanaryDecisionResult
            {
                Applied = false,
                PreviousPercentage = 0,
                CurrentPercentage = 0,
                NewRevision = 0,
                NewEpoch = 0,
                FailureReason = "LeaseLost"
            };
        }

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        int previousPercentage = 0;
        int newRevision = 0;

        try
        {
            // 步骤 1：lease/fencing 验证（SELECT FOR UPDATE 锁住 lease 行）
            // 使用 clock_timestamp() 而非 now()，确保 lease 过期判断基于真实当前时间而非事务开始时间。
            await using (var leaseCommand = connection.CreateCommand())
            {
                leaseCommand.Transaction = transaction;
                leaseCommand.CommandTimeout = Options.CommandTimeoutSeconds;
                leaseCommand.CommandText = $"""
SELECT 1 FROM {Table("canary_leader_leases")}
WHERE run_id = @run_id
  AND fencing_token = @fencing_token
  AND lease_expires_at > clock_timestamp()
FOR UPDATE;
""";
                leaseCommand.Parameters.AddWithValue("run_id", request.RunId);
                leaseCommand.Parameters.AddWithValue("fencing_token", fencingTokenLong);

                var leaseResult = await leaseCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (leaseResult is null)
                {
                    // lease 不存在 / fencing token 不匹配 / lease 已过期 → 放弃事务
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return new CanaryDecisionResult
                    {
                        Applied = false,
                        PreviousPercentage = 0,
                        CurrentPercentage = (int)request.NewPercentage,
                        NewRevision = 0,
                        NewEpoch = 0,
                        FailureReason = "LeaseLost"
                    };
                }
            }

            // 步骤 2：读取当前 pipeline 状态（percentage + revision），用于 CAS 校验与审计的 from_percentage
            await using (var stateCommand = connection.CreateCommand())
            {
                stateCommand.Transaction = transaction;
                stateCommand.CommandTimeout = Options.CommandTimeoutSeconds;
                stateCommand.CommandText = $"""
SELECT percentage, revision FROM {Table("canary_pipelines")}
WHERE run_id = @run_id;
""";
                stateCommand.Parameters.AddWithValue("run_id", request.RunId);

                await using var reader = await stateCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    previousPercentage = reader.GetInt32(reader.GetOrdinal("percentage"));
                    var currentRevision = reader.GetInt32(reader.GetOrdinal("revision"));
                    if (currentRevision != request.ExpectedRevision)
                    {
                        // revision 不匹配 → 已被其他 Leader 推进，放弃事务
                        await reader.CloseAsync().ConfigureAwait(false);
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        return new CanaryDecisionResult
                        {
                            Applied = false,
                            PreviousPercentage = previousPercentage,
                            CurrentPercentage = previousPercentage,
                            NewRevision = currentRevision,
                            NewEpoch = 0,
                            FailureReason = "RevisionMismatch"
                        };
                    }
                }
                else
                {
                    // 行不存在 → ExpectedRevision 必须为 0（首次初始化）
                    if (request.ExpectedRevision != 0)
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        return new CanaryDecisionResult
                        {
                            Applied = false,
                            PreviousPercentage = 0,
                            CurrentPercentage = 0,
                            NewRevision = 0,
                            NewEpoch = 0,
                            FailureReason = "RevisionMismatch"
                        };
                    }
                }
            }

            // 步骤 3：pipeline revision CAS（UPSERT 处理首次初始化 + 后续更新）
            // ON CONFLICT WHERE revision = @expectedRevision 实现 CAS：
            //   - 首次（expected=0，行不存在）→ INSERT 成功，revision=1
            //   - 后续（expected=N，行存在且 revision=N）→ UPDATE 成功，revision=N+1
            //   - 冲突（expected=N，行存在但 revision≠N）→ ON CONFLICT WHERE 不命中，0 行返回
            var newStatus = request.Decision switch
            {
                CanaryDecision.Rollback => "RolledBack",
                CanaryDecision.Promote when (int)request.NewPercentage >= 100 => "Promoted",
                _ => "Active"
            };

            await using (var casCommand = connection.CreateCommand())
            {
                casCommand.Transaction = transaction;
                casCommand.CommandTimeout = Options.CommandTimeoutSeconds;
                casCommand.CommandText = $"""
INSERT INTO {Table("canary_pipelines")} (run_id, percentage, status, revision, created_at, updated_at)
VALUES (@run_id, @new_percentage, @new_status, 1, now(), now())
ON CONFLICT (run_id) DO UPDATE SET
    percentage = EXCLUDED.percentage,
    status = EXCLUDED.status,
    revision = {Table("canary_pipelines")}.revision + 1,
    updated_at = now()
WHERE {Table("canary_pipelines")}.revision = @expected_revision
RETURNING revision;
""";
                casCommand.Parameters.AddWithValue("run_id", request.RunId);
                casCommand.Parameters.AddWithValue("new_percentage", (int)request.NewPercentage);
                casCommand.Parameters.AddWithValue("new_status", newStatus);
                casCommand.Parameters.AddWithValue("expected_revision", request.ExpectedRevision);

                var casResult = await casCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (casResult is not long rev || rev <= 0)
                {
                    // 0 行 → CAS 失败（revision 已被其他 Leader 推进）
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return new CanaryDecisionResult
                    {
                        Applied = false,
                        PreviousPercentage = previousPercentage,
                        CurrentPercentage = previousPercentage,
                        NewRevision = 0,
                        NewEpoch = 0,
                        FailureReason = "RevisionMismatch"
                    };
                }
                newRevision = checked((int)rev);
            }

            // 步骤 4：transition audit 写入（同事务，确保审计与状态强一致）
            var auditId = Guid.NewGuid().ToString("N");
            var transitionId = string.IsNullOrWhiteSpace(request.TransitionId)
                ? $"t-{request.RunId}-{auditId.Substring(0, 12)}"
                : request.TransitionId;
            var decisionText = request.Decision.ToString();
            var transitionedAt = DateTimeOffset.UtcNow;

            await using (var auditCommand = connection.CreateCommand())
            {
                auditCommand.Transaction = transaction;
                auditCommand.CommandTimeout = Options.CommandTimeoutSeconds;
                auditCommand.CommandText = $"""
INSERT INTO {Table("canary_transition_audit")} (
    audit_id, run_id, transition_id, from_percentage, to_percentage,
    decision, rationale, transition, fencing_token, new_epoch, transitioned_at, data
) VALUES (
    @audit_id, @run_id, @transition_id, @from_percentage, @to_percentage,
    @decision, @rationale, @transition, @fencing_token, @new_epoch, @transitioned_at, @data
)
ON CONFLICT (audit_id) DO NOTHING;
""";
                auditCommand.Parameters.AddWithValue("audit_id", auditId);
                auditCommand.Parameters.AddWithValue("run_id", request.RunId);
                auditCommand.Parameters.AddWithValue("transition_id", transitionId);
                auditCommand.Parameters.AddWithValue("from_percentage", previousPercentage);
                auditCommand.Parameters.AddWithValue("to_percentage", (int)request.NewPercentage);
                auditCommand.Parameters.AddWithValue("decision", decisionText);
                auditCommand.Parameters.AddWithValue("rationale", request.Rationale ?? string.Empty);
                auditCommand.Parameters.AddWithValue("transition", request.Transition ?? string.Empty);
                auditCommand.Parameters.AddWithValue("fencing_token", fencingTokenLong);
                auditCommand.Parameters.AddWithValue("new_epoch", request.NewEpoch);
                auditCommand.Parameters.AddWithValue("transitioned_at", transitionedAt);
                AddJson(auditCommand, "data", new
                {
                    audit_id = auditId,
                    run_id = request.RunId,
                    transition_id = transitionId,
                    from_percentage = previousPercentage,
                    to_percentage = (int)request.NewPercentage,
                    decision = decisionText,
                    rationale = request.Rationale ?? string.Empty,
                    transition = request.Transition ?? string.Empty,
                    fencing_token = fencingTokenLong,
                    new_epoch = request.NewEpoch,
                    transitioned_at = transitionedAt
                });

                await auditCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // 步骤 5：epoch 更新（UPSERT，同事务）
            // 使用 EXCLUDED.current_epoch 直接覆盖为调用方传入的 NewEpoch
            // （调用方应传入 current_epoch + 1，事务内不再自增，确保与请求一致）
            await using (var epochCommand = connection.CreateCommand())
            {
                epochCommand.Transaction = transaction;
                epochCommand.CommandTimeout = Options.CommandTimeoutSeconds;
                epochCommand.CommandText = $"""
INSERT INTO {Table("canary_run_epochs")} (run_id, current_epoch, advanced_at)
VALUES (@run_id, @new_epoch, now())
ON CONFLICT (run_id) DO UPDATE SET
    current_epoch = EXCLUDED.current_epoch,
    advanced_at = EXCLUDED.advanced_at;
""";
                epochCommand.Parameters.AddWithValue("run_id", request.RunId);
                epochCommand.Parameters.AddWithValue("new_epoch", request.NewEpoch);

                await epochCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // 提交事务（所有 4 步成功）
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new CanaryDecisionResult
            {
                Applied = true,
                PreviousPercentage = previousPercentage,
                CurrentPercentage = (int)request.NewPercentage,
                NewRevision = newRevision,
                NewEpoch = request.NewEpoch,
                FailureReason = "Success"
            };
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* 不掩盖原始异常 */ }
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<CanaryPipelineState> GetCanaryPipelineStateAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT percentage, revision, status FROM {Table("canary_pipelines")}
WHERE run_id = @run_id;
""";
        command.Parameters.AddWithValue("run_id", runId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // 行不存在 → 返回 Revision=0, Percentage=0（调用方可据此判断需要首次初始化）
            return new CanaryPipelineState
            {
                RunId = runId,
                Revision = 0,
                Percentage = 0,
                Status = null
            };
        }

        var percentage = reader.GetInt32(reader.GetOrdinal("percentage"));
        var revision = reader.GetInt32(reader.GetOrdinal("revision"));
        var statusOrdinal = reader.GetOrdinal("status");
        var status = reader.IsDBNull(statusOrdinal) ? null : reader.GetString(statusOrdinal);

        return new CanaryPipelineState
        {
            RunId = runId,
            Revision = revision,
            Percentage = percentage,
            Status = status
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// P0-7：批量读取所有活跃 pipeline 状态。过滤终态（Promoted/RolledBack），
    /// 仅返回 status='Active'（或其它非终态）的行，供服务启动时恢复 in-memory 路由状态。
    /// </remarks>
    public async ValueTask<IReadOnlyList<CanaryPipelineState>> GetAllActivePipelineStatesAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // 终态 = Promoted（已晋升到 100%）/ RolledBack（已回滚）。Active 行（含首次初始化与推进中）返回。
        command.CommandText = $"""
SELECT run_id, percentage, revision, status FROM {Table("canary_pipelines")}
WHERE status IS NULL OR status NOT IN ('Promoted', 'RolledBack');
""";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<CanaryPipelineState>();
        var runIdOrdinal = reader.GetOrdinal("run_id");
        var percentageOrdinal = reader.GetOrdinal("percentage");
        var revisionOrdinal = reader.GetOrdinal("revision");
        var statusOrdinal2 = reader.GetOrdinal("status");

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new CanaryPipelineState
            {
                RunId = reader.GetString(runIdOrdinal),
                Revision = reader.GetInt32(revisionOrdinal),
                Percentage = reader.GetInt32(percentageOrdinal),
                Status = reader.IsDBNull(statusOrdinal2) ? null : reader.GetString(statusOrdinal2)
            });
        }

        return results;
    }
}
