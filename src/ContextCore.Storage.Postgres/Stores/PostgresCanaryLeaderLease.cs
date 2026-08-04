using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 持久化 Canary Leader 租约实现。
/// </summary>
/// <remarks>
/// 确保 <see cref="ContextCore.Core.Services.Evolution.CanaryProgressionHostedService"/>
/// 同一时刻仅一个实例处理同一 run，避免多实例同时推进/回滚同一 Canary。
///
/// <b>租约模型</b>（每个 run_id 至多一条行）：
/// <code>
/// TryAcquireAsync:
/// INSERT INTO canary_leader_leases (run_id, owner, lease_token, acquired_at, lease_expires_at)
/// VALUES (...)
/// ON CONFLICT (run_id) DO UPDATE
/// SET owner = EXCLUDED.owner, lease_token = EXCLUDED.lease_token, ...
/// WHERE canary_leader_leases.lease_expires_at &lt; now
/// RETURNING lease_token;
/// - 无现有行 → INSERT 成功，返回 token
/// - 现有行过期 → ON CONFLICT DO UPDATE WHERE 子句命中，更新并返回 token
/// - 现有行未过期 → ON CONFLICT DO UPDATE WHERE 子句不命中，0 行返回，返回 null
/// </code>
///
/// <b>RenewAsync</b>：UPDATE WHERE lease_token = @token，延长 lease_expires_at。
/// <b>ReleaseAsync</b>：DELETE WHERE lease_token = @token（主动让出）。
/// <b>ReapExpiredAsync</b>：DELETE WHERE lease_expires_at &lt; now（崩溃 leader 持有的过期租约最终释放）。
///
/// 复用租约模式（CAS + token 匹配），但状态机更简单：
/// leader 租约无需 Pending → Leased → Acked 流转，只有 "持有" 与 "未持有" 两个状态。
///
/// <b>Perf-7 严格 HA 单事务接口</b>：本类同时实现 <see cref="ICanaryDecisionApplier"/>，
/// 将 lease/fencing 校验 + pipeline revision CAS + transition audit 写入 + epoch 递增
/// 合并为单一 PostgreSQL 事务，修复旧路径 <c>AdvanceAsync</c> → <c>AdvanceEpochAsync</c>
/// 分两步导致的 HA 正确性问题（旧 Leader 可能已推进 rollout 后 fencing 才失败）。
///
/// <b>P0-12 单一真相源</b>：Canary 的 percentage/revision/epoch 直接 CAS 写入
/// <c>pipeline_runs</c>（<see cref="PipelineRunSnapshot"/> 的 Canary* 字段 + 专用列），
/// transition audit 与 snapshot CAS 同事务提交，<c>canary_pipelines</c> 不再有任何
/// 生产写入——仅 <see cref="GetAllActivePipelineStatesAsync"/> 保留 legacy 一次性迁移读取。
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
    /// 成功获取时 <c>fencing_token = 旧值 + 1</c>（新插入为 1），RETURNING 返回新的 fencing_token，
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
        // fencing_token 在 ON CONFLICT DO UPDATE 时 = canary_leader_leases.fencing_token + 1（抢占过期），
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
    // ICanaryDecisionApplier 实现（单一 PostgreSQL 事务）
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// <b>事务流程</b>（P0-12：Canary 真相源 = pipeline_runs snapshot）：
    /// <code>
    /// BEGIN;
    /// -- 1. lease/fencing 验证（SELECT FOR UPDATE 锁住 lease 行，防止并发续约/释放）
    /// SELECT 1 FROM canary_leader_leases
    /// WHERE run_id = @runId AND fencing_token = @fencingToken
    /// AND lease_expires_at > clock_timestamp()
    /// FOR UPDATE;
    /// -- 无行 → ROLLBACK，返回 LeaseLost
    ///
    /// -- 2. 读取当前 pipeline run snapshot（单一真相源：CanaryPercentage/CanaryRevision）
    /// SELECT data FROM pipeline_runs WHERE run_id = @runId;
    /// -- CanaryRevision != @expectedRevision（或行不存在）→ ROLLBACK，返回 RevisionMismatch
    ///
    /// -- 3. snapshot CAS（修补 data jsonb + 专用列，WHERE canary_revision 双保险）
    /// UPDATE pipeline_runs
    /// SET canary_percentage = @newPct, canary_revision = canary_revision + 1,
    ///     canary_epoch = @newEpoch, updated_at = now(), data = @patched
    /// WHERE run_id = @runId AND canary_revision = @expectedRevision
    /// RETURNING canary_revision;
    /// -- 0 行 → ROLLBACK，返回 RevisionMismatch
    ///
    /// -- 4. transition audit 写入（同事务，与 snapshot CAS 强一致）
    /// INSERT INTO canary_transition_audit (...) VALUES (...);
    ///
    /// -- 5. epoch 更新（UPSERT，同事务）
    /// INSERT INTO canary_run_epochs (run_id, current_epoch, advanced_at)
    /// VALUES (@runId, @newEpoch, now())
    /// ON CONFLICT (run_id) DO UPDATE SET
    /// current_epoch = EXCLUDED.current_epoch,
    /// advanced_at = EXCLUDED.advanced_at;
    /// COMMIT;
    /// </code>
    /// 任一步骤失败则整个事务 ROLLBACK，确保旧 Leader 无法在 lease 失效后修改 rollout，
    /// 且 audit 与 snapshot 状态不会撕裂（P0-12：删除对 canary_pipelines 的生产写入）。
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

            // 步骤 2：读取 pipeline run snapshot（单一真相源），用于 CAS 校验与审计的 from_percentage。
            // run 行由 pipeline 在进入 Canary 阶段前创建（TryCreateRunAsync）；Canary 推进只负责
            // 推进 snapshot 内的 Canary* 状态，不为缺失的 run 建行（行缺失 = 状态异常 → CAS 失败）。
            PipelineRunSnapshot? currentSnapshot;
            await using (var stateCommand = connection.CreateCommand())
            {
                stateCommand.Transaction = transaction;
                stateCommand.CommandTimeout = Options.CommandTimeoutSeconds;
                stateCommand.CommandText = $"""
SELECT data FROM {Table("pipeline_runs")}
WHERE run_id = @run_id;
""";
                stateCommand.Parameters.AddWithValue("run_id", request.RunId);

                currentSnapshot = await ExecuteScalarJsonAsync<PipelineRunSnapshot>(stateCommand, cancellationToken).ConfigureAwait(false);
            }

            if (currentSnapshot is null)
            {
                // run 行不存在（或 data 缺失）→ 无 snapshot 可 CAS，放弃事务
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

            if (currentSnapshot.CanaryRevision != request.ExpectedRevision)
            {
                // CanaryRevision 不匹配 → 已被其他 Leader 推进，放弃事务
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new CanaryDecisionResult
                {
                    Applied = false,
                    PreviousPercentage = currentSnapshot.CanaryPercentage,
                    CurrentPercentage = currentSnapshot.CanaryPercentage,
                    NewRevision = (int)currentSnapshot.CanaryRevision,
                    NewEpoch = 0,
                    FailureReason = "RevisionMismatch"
                };
            }
            previousPercentage = currentSnapshot.CanaryPercentage;

            // 步骤 3：snapshot CAS（修补 data jsonb + 专用列，WHERE canary_revision 双保险）
            // - 首次（expected=0，CanaryRevision=0）→ 0 → 1，percentage 从 0 跳到首档
            // - 后续（expected=N，CanaryRevision=N）→ N → N+1
            // - 冲突（CanaryRevision ≠ N）→ WHERE 不命中，0 行返回
            var updatedAt = DateTimeOffset.UtcNow;
            var nextSnapshot = currentSnapshot with
            {
                CanaryPercentage = (int)request.NewPercentage,
                CanaryRevision = request.ExpectedRevision + 1,
                CanaryEpoch = request.NewEpoch,
                UpdatedAt = updatedAt
            };

            await using (var casCommand = connection.CreateCommand())
            {
                casCommand.Transaction = transaction;
                casCommand.CommandTimeout = Options.CommandTimeoutSeconds;
                casCommand.CommandText = $"""
UPDATE {Table("pipeline_runs")}
SET canary_percentage = @canary_percentage,
    canary_revision = @canary_revision,
    canary_epoch = @canary_epoch,
    updated_at = @updated_at,
    data = @data
WHERE run_id = @run_id
  AND canary_revision = @expected_revision
RETURNING canary_revision;
""";
                casCommand.Parameters.AddWithValue("run_id", request.RunId);
                casCommand.Parameters.AddWithValue("canary_percentage", nextSnapshot.CanaryPercentage);
                casCommand.Parameters.AddWithValue("canary_revision", nextSnapshot.CanaryRevision);
                casCommand.Parameters.AddWithValue("canary_epoch", nextSnapshot.CanaryEpoch);
                casCommand.Parameters.AddWithValue("updated_at", updatedAt);
                casCommand.Parameters.AddWithValue("expected_revision", request.ExpectedRevision);
                AddJson(casCommand, "data", nextSnapshot);

                var casResult = await casCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                var rev = casResult switch
                {
                    long l => l,
                    int i => i,
                    _ => 0L
                };
                if (rev <= 0)
                {
                    // 0 行 → CAS 失败（CanaryRevision 已被其他推进者更新）
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
    /// <remarks>
    /// 单节点/本地模式——跳过 lease/fencing 校验（步骤 1），仅执行步骤 2-5
    /// （pipeline_runs snapshot CAS + transition audit + epoch update），
    /// 确保单节点模式也写 DB 真相源（P0-12：Canary 状态直接 CAS 进 pipeline_runs）。
    /// </remarks>
    public async ValueTask<CanaryDecisionResult> ApplyCanaryDecisionLocalAsync(
        CanaryDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);
        cancellationToken.ThrowIfCancellationRequested();

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        int previousPercentage = 0;
        int newRevision = 0;

        try
        {
            // 跳过步骤 1（lease/fencing 校验）——单节点模式无 Leader lease。

            // 步骤 2：读取 pipeline run snapshot（单一真相源），用于 CAS 校验与审计的 from_percentage。
            // run 行由 pipeline 在进入 Canary 阶段前创建；行缺失 = 状态异常 → CAS 失败（不建行）。
            PipelineRunSnapshot? currentSnapshot;
            await using (var stateCommand = connection.CreateCommand())
            {
                stateCommand.Transaction = transaction;
                stateCommand.CommandTimeout = Options.CommandTimeoutSeconds;
                stateCommand.CommandText = $"""
SELECT data FROM {Table("pipeline_runs")}
WHERE run_id = @run_id;
""";
                stateCommand.Parameters.AddWithValue("run_id", request.RunId);

                currentSnapshot = await ExecuteScalarJsonAsync<PipelineRunSnapshot>(stateCommand, cancellationToken).ConfigureAwait(false);
            }

            if (currentSnapshot is null)
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

            if (currentSnapshot.CanaryRevision != request.ExpectedRevision)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new CanaryDecisionResult
                {
                    Applied = false,
                    PreviousPercentage = currentSnapshot.CanaryPercentage,
                    CurrentPercentage = currentSnapshot.CanaryPercentage,
                    NewRevision = (int)currentSnapshot.CanaryRevision,
                    NewEpoch = 0,
                    FailureReason = "RevisionMismatch"
                };
            }
            previousPercentage = currentSnapshot.CanaryPercentage;

            // 步骤 3：snapshot CAS（修补 data jsonb + 专用列，WHERE canary_revision 双保险）
            var updatedAt = DateTimeOffset.UtcNow;
            var nextSnapshot = currentSnapshot with
            {
                CanaryPercentage = (int)request.NewPercentage,
                CanaryRevision = request.ExpectedRevision + 1,
                CanaryEpoch = request.NewEpoch,
                UpdatedAt = updatedAt
            };

            await using (var casCommand = connection.CreateCommand())
            {
                casCommand.Transaction = transaction;
                casCommand.CommandTimeout = Options.CommandTimeoutSeconds;
                casCommand.CommandText = $"""
UPDATE {Table("pipeline_runs")}
SET canary_percentage = @canary_percentage,
    canary_revision = @canary_revision,
    canary_epoch = @canary_epoch,
    updated_at = @updated_at,
    data = @data
WHERE run_id = @run_id
  AND canary_revision = @expected_revision
RETURNING canary_revision;
""";
                casCommand.Parameters.AddWithValue("run_id", request.RunId);
                casCommand.Parameters.AddWithValue("canary_percentage", nextSnapshot.CanaryPercentage);
                casCommand.Parameters.AddWithValue("canary_revision", nextSnapshot.CanaryRevision);
                casCommand.Parameters.AddWithValue("canary_epoch", nextSnapshot.CanaryEpoch);
                casCommand.Parameters.AddWithValue("updated_at", updatedAt);
                casCommand.Parameters.AddWithValue("expected_revision", request.ExpectedRevision);
                AddJson(casCommand, "data", nextSnapshot);

                var casResult = await casCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                var rev = casResult switch
                {
                    long l => l,
                    int i => i,
                    _ => 0L
                };
                if (rev <= 0)
                {
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

            // 步骤 4：transition audit 写入（同事务）
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
                auditCommand.Parameters.AddWithValue("fencing_token", 0L);
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
                    fencing_token = 0L,
                    new_epoch = request.NewEpoch,
                    transitioned_at = transitionedAt,
                    local_mode = true
                });

                await auditCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // 步骤 5：epoch 更新（UPSERT，同事务）
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
    /// <remarks>
    /// P0-12：从 <c>pipeline_runs</c> snapshot（单一真相源）读取 Canary 状态，
    /// 不再读取 legacy <c>canary_pipelines</c> 表。snapshot CanaryRevision == 0 表示
    /// 尚未推进（或 legacy 数据，恢复时由 <see cref="GetAllActivePipelineStatesAsync"/>
    /// 一次性迁移读取兜底），返回 Revision=0 / Percentage=0。
    /// </remarks>
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
SELECT data FROM {Table("pipeline_runs")}
WHERE run_id = @run_id;
""";
        command.Parameters.AddWithValue("run_id", runId);

        var snapshot = await ExecuteScalarJsonAsync<PipelineRunSnapshot>(command, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            // run 行不存在 → 返回 Revision=0, Percentage=0（调用方可据此判断需要首次初始化）
            return new CanaryPipelineState
            {
                RunId = runId,
                Revision = 0,
                Percentage = 0,
                Status = null
            };
        }

        // status 映射：终态语义与 legacy canary_pipelines.status 对齐
        var status = snapshot.Status switch
        {
            PipelineRunStatus.Promoted => "Promoted",
            PipelineRunStatus.RolledBack => "RolledBack",
            _ => "Active"
        };

        return new CanaryPipelineState
        {
            RunId = runId,
            Revision = (int)snapshot.CanaryRevision,
            Percentage = snapshot.CanaryPercentage,
            Status = status
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// 从 <c>canary_run_epochs</c> 表读取当前 stage epoch。行不存在时返回 0。
    /// 供单节点模式下 <c>CanaryProgressionService.ApplyDecisionToStoreAsync</c> 计算
    /// <c>newEpoch = currentEpoch + 1</c>，确保重启后 epoch 不回退。
    /// </remarks>
    public async ValueTask<long> GetCurrentEpochAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT current_epoch FROM {Table("canary_run_epochs")}
WHERE run_id = @run_id;
""";
        command.Parameters.AddWithValue("run_id", runId);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long epoch ? epoch : 0;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>legacy 一次性迁移读取</b>（P0-12）：仍从 <c>canary_pipelines</c> 表读取，
    /// 仅用于 <c>RecoverFromStoreAsync</c> 对 snapshot CanaryRevision == 0 的 legacy run
    /// 恢复 in-memory 路由状态。生产路径（<see cref="ApplyCanaryDecisionAsync"/> /
    /// <see cref="ApplyCanaryDecisionLocalAsync"/>）已不再写入该表；本表只读不写。
    /// 过滤终态（Promoted/RolledBack），仅返回 status='Active'（或其它非终态）的行。
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
