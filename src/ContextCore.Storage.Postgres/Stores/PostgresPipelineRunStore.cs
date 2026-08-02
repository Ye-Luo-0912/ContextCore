using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL Pipeline Run 持久化存储。
/// 替代 <see cref="ContextCore.Core.Services.Evolution.InMemoryPipelineRunStore"/>，
/// 让 Postgres provider 在 HA 场景下能持久化 Evolution Pipeline 运行状态与审计记录。
/// </summary>
/// <remarks>
/// 设计要点：
///   1. 4 张表反规范化查询字段（proposal_id / run_id / status 等）以便索引查询；
///      完整对象保存在 <c>data jsonb</c>，由 store 反序列化。
///   2. 主键：
///      - pipeline_runs: run_id
///      - pipeline_canary_assignments: assignment_id
///      - pipeline_rollback_records: record_id
///      - pipeline_baseline_comparisons: comparison_id
///   3. <see cref="ListRunsByProposalAsync"/> 按 proposal_id 过滤 + updated_at DESC。
///   4. <see cref="SaveRunAsync"/> 幂等（同主键覆盖）— 仅用于 StartAsync 创建新 run；
///      后续推进必须走 <see cref="TryTransitionAsync"/>（P0-7 CAS 路径）。
///   5. 与 PostgresAgentCheckpointStore 设计模式对齐（R26-2）。
///   6. P0-7：<see cref="TryTransitionAsync"/> 在单事务内完成 CAS UPDATE + audit 批量 INSERT，
///      SELECT FOR UPDATE 锁定行，避免并发推进导致状态分裂。
/// </remarks>
public sealed class PostgresPipelineRunStore : PostgresStoreBase, IPipelineRunStore
{
    public PostgresPipelineRunStore(PostgresConnectionFactory connectionFactory, PostgresJsonSerializer serializer, PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    // ---------- Pipeline runs ----------

    /// <inheritdoc />
    public async Task SaveRunAsync(PipelineRunSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("pipeline_runs")} (
    run_id, proposal_id, proposal_major, proposal_minor, target_component,
    current_stage, status, started_at, updated_at, completed_at, rollback_reason,
    revision, lease_owner, lease_expires_at, last_transition_id, data)
VALUES (
    @run_id, @proposal_id, @proposal_major, @proposal_minor, @target_component,
    @current_stage, @status, @started_at, @updated_at, @completed_at, @rollback_reason,
    @revision, @lease_owner, @lease_expires_at, @last_transition_id, @data)
ON CONFLICT (run_id) DO UPDATE SET
    proposal_id = EXCLUDED.proposal_id,
    proposal_major = EXCLUDED.proposal_major,
    proposal_minor = EXCLUDED.proposal_minor,
    target_component = EXCLUDED.target_component,
    current_stage = EXCLUDED.current_stage,
    status = EXCLUDED.status,
    started_at = EXCLUDED.started_at,
    updated_at = EXCLUDED.updated_at,
    completed_at = EXCLUDED.completed_at,
    rollback_reason = EXCLUDED.rollback_reason,
    revision = EXCLUDED.revision,
    lease_owner = EXCLUDED.lease_owner,
    lease_expires_at = EXCLUDED.lease_expires_at,
    last_transition_id = EXCLUDED.last_transition_id,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("run_id", snapshot.RunId);
        command.Parameters.AddWithValue("proposal_id", snapshot.ProposalId);
        command.Parameters.AddWithValue("proposal_major", snapshot.ProposalVersion.Major);
        command.Parameters.AddWithValue("proposal_minor", snapshot.ProposalVersion.Minor);
        command.Parameters.AddWithValue("target_component", snapshot.Proposal.TargetComponent.ToString());
        command.Parameters.AddWithValue("current_stage", snapshot.CurrentStage.ToString());
        command.Parameters.AddWithValue("status", snapshot.Status.ToString());
        command.Parameters.AddWithValue("started_at", snapshot.StartedAt);
        command.Parameters.AddWithValue("updated_at", snapshot.UpdatedAt);
        command.Parameters.AddWithValue("completed_at", (object?)snapshot.CompletedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("rollback_reason", (object?)snapshot.RollbackReason ?? DBNull.Value);
        // HA 字段
        command.Parameters.AddWithValue("revision", snapshot.Revision);
        command.Parameters.AddWithValue("lease_owner", (object?)snapshot.LeaseOwner ?? DBNull.Value);
        command.Parameters.AddWithValue("lease_expires_at", (object?)snapshot.LeaseExpiresAt ?? DBNull.Value);
        command.Parameters.AddWithValue("last_transition_id", (object?)snapshot.LastTransitionId ?? DBNull.Value);
        AddJson(command, "data", snapshot);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>P2-1：使用 ON CONFLICT (run_id) DO NOTHING 实现 insert-if-absent 语义。</remarks>
    public async Task<bool> TryCreateRunAsync(PipelineRunSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("pipeline_runs")} (
    run_id, proposal_id, proposal_major, proposal_minor, target_component,
    current_stage, status, started_at, updated_at, completed_at, rollback_reason,
    revision, lease_owner, lease_expires_at, last_transition_id, data)
VALUES (
    @run_id, @proposal_id, @proposal_major, @proposal_minor, @target_component,
    @current_stage, @status, @started_at, @updated_at, @completed_at, @rollback_reason,
    @revision, @lease_owner, @lease_expires_at, @last_transition_id, @data)
ON CONFLICT (run_id) DO NOTHING;
""";
        command.Parameters.AddWithValue("run_id", snapshot.RunId);
        command.Parameters.AddWithValue("proposal_id", snapshot.ProposalId);
        command.Parameters.AddWithValue("proposal_major", snapshot.ProposalVersion.Major);
        command.Parameters.AddWithValue("proposal_minor", snapshot.ProposalVersion.Minor);
        command.Parameters.AddWithValue("target_component", snapshot.Proposal.TargetComponent.ToString());
        command.Parameters.AddWithValue("current_stage", snapshot.CurrentStage.ToString());
        command.Parameters.AddWithValue("status", snapshot.Status.ToString());
        command.Parameters.AddWithValue("started_at", snapshot.StartedAt);
        command.Parameters.AddWithValue("updated_at", snapshot.UpdatedAt);
        command.Parameters.AddWithValue("completed_at", (object?)snapshot.CompletedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("rollback_reason", (object?)snapshot.RollbackReason ?? DBNull.Value);
        command.Parameters.AddWithValue("revision", snapshot.Revision);
        command.Parameters.AddWithValue("lease_owner", (object?)snapshot.LeaseOwner ?? DBNull.Value);
        command.Parameters.AddWithValue("lease_expires_at", (object?)snapshot.LeaseExpiresAt ?? DBNull.Value);
        command.Parameters.AddWithValue("last_transition_id", (object?)snapshot.LastTransitionId ?? DBNull.Value);
        AddJson(command, "data", snapshot);
        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rowsAffected > 0;
    }

    /// <inheritdoc />
    public async Task<PipelineRunSnapshot?> GetRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("pipeline_runs")}
WHERE run_id = @run_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("run_id", runId);
        return await ExecuteScalarJsonAsync<PipelineRunSnapshot>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PipelineRunSnapshot>> ListRunsByProposalAsync(
        string proposalId,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);
        if (take < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "take must be >= 0");
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("pipeline_runs")}
WHERE proposal_id = @proposal_id
ORDER BY updated_at DESC, run_id DESC
LIMIT @take;
""";
        command.Parameters.AddWithValue("proposal_id", proposalId);
        command.Parameters.AddWithValue("take", take == 0 ? int.MaxValue : take);

        return await ExecuteReaderJsonAsync<PipelineRunSnapshot>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PipelineRunSnapshot>> ListRunsByStageAsync(
        OptimizationStage stage,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        if (take < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "take must be >= 0");
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("pipeline_runs")}
WHERE current_stage = @current_stage
ORDER BY updated_at DESC, run_id DESC
LIMIT @take;
""";
        command.Parameters.AddWithValue("current_stage", stage.ToString());
        command.Parameters.AddWithValue("take", take == 0 ? int.MaxValue : take);

        return await ExecuteReaderJsonAsync<PipelineRunSnapshot>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
DELETE FROM {Table("pipeline_runs")}
WHERE run_id = @run_id;
""";
        command.Parameters.AddWithValue("run_id", runId);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 在单事务内完成：
    /// <list type="number">
    /// <item>SELECT FOR UPDATE 锁定 run 行（防止并发修改）。</item>
    /// <item>读取当前 data，反序列化为 <see cref="PipelineRunSnapshot"/>。</item>
    /// <item>幂等检查：若 next.LastTransitionId 非 null 且等于 current.LastTransitionId，COMMIT 并返回 current。</item>
    /// <item>CAS UPDATE：WHERE revision = expectedRevision AND current_stage = expectedStage；
    ///   SET revision = next.Revision, current_stage = next.CurrentStage, ... data = next。</item>
    /// <item>若 RowsAffected == 1：INSERT audit 批量记录；COMMIT；返回 next。</item>
    /// <item>若 RowsAffected == 0：ROLLBACK；返回 null（CAS 失败）。</item>
    /// </list>
    /// </remarks>
    public async Task<PipelineRunSnapshot?> TryTransitionAsync(
        string runId,
        long expectedRevision,
        OptimizationStage expectedStage,
        PipelineRunSnapshot next,
        PipelineAuditBatch? audit = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(next);
        if (!string.Equals(runId, next.RunId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"runId ({runId}) 必须与 next.RunId ({next.RunId}) 一致", nameof(runId));
        }
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Step 1: SELECT FOR UPDATE 锁定行
            PipelineRunSnapshot? current;
            {
                await using var selectCmd = connection.CreateCommand();
                selectCmd.Transaction = transaction;
                selectCmd.CommandTimeout = Options.CommandTimeoutSeconds;
                selectCmd.CommandText = $"""
SELECT data
FROM {Table("pipeline_runs")}
WHERE run_id = @run_id
FOR UPDATE;
""";
                selectCmd.Parameters.AddWithValue("run_id", runId);
                current = await ExecuteScalarJsonAsync<PipelineRunSnapshot>(selectCmd, cancellationToken).ConfigureAwait(false);
            }

            if (current is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            // Step 2: 幂等重试检查
            if (next.LastTransitionId is not null
                && string.Equals(current.LastTransitionId, next.LastTransitionId, StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return current;
            }

            // Step 3: CAS UPDATE
            int rowsAffected;
            {
                await using var updateCmd = connection.CreateCommand();
                updateCmd.Transaction = transaction;
                updateCmd.CommandTimeout = Options.CommandTimeoutSeconds;
                updateCmd.CommandText = $"""
UPDATE {Table("pipeline_runs")}
SET proposal_id = @proposal_id,
    proposal_major = @proposal_major,
    proposal_minor = @proposal_minor,
    target_component = @target_component,
    current_stage = @next_stage,
    status = @next_status,
    started_at = @started_at,
    updated_at = @updated_at,
    completed_at = @completed_at,
    rollback_reason = @rollback_reason,
    revision = @next_revision,
    lease_owner = @lease_owner,
    lease_expires_at = @lease_expires_at,
    last_transition_id = @last_transition_id,
    data = @data
WHERE run_id = @run_id
  AND revision = @expected_revision
  AND current_stage = @expected_stage;
""";
                updateCmd.Parameters.AddWithValue("run_id", runId);
                updateCmd.Parameters.AddWithValue("proposal_id", next.ProposalId);
                updateCmd.Parameters.AddWithValue("proposal_major", next.ProposalVersion.Major);
                updateCmd.Parameters.AddWithValue("proposal_minor", next.ProposalVersion.Minor);
                updateCmd.Parameters.AddWithValue("target_component", next.Proposal.TargetComponent.ToString());
                updateCmd.Parameters.AddWithValue("next_stage", next.CurrentStage.ToString());
                updateCmd.Parameters.AddWithValue("next_status", next.Status.ToString());
                updateCmd.Parameters.AddWithValue("started_at", next.StartedAt);
                updateCmd.Parameters.AddWithValue("updated_at", next.UpdatedAt);
                updateCmd.Parameters.AddWithValue("completed_at", (object?)next.CompletedAt ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("rollback_reason", (object?)next.RollbackReason ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("next_revision", next.Revision);
                updateCmd.Parameters.AddWithValue("lease_owner", (object?)next.LeaseOwner ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("lease_expires_at", (object?)next.LeaseExpiresAt ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("last_transition_id", (object?)next.LastTransitionId ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("expected_revision", expectedRevision);
                updateCmd.Parameters.AddWithValue("expected_stage", expectedStage.ToString());
                AddJson(updateCmd, "data", next);
                rowsAffected = await updateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (rowsAffected == 0)
            {
                // CAS 失败：revision 或 stage 不匹配
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            // Step 4: 写入 audit 批量（同事务）
            if (audit is not null)
            {
                if (audit.BaselineComparison is { } cmp)
                {
                    await InsertBaselineComparisonAsync(connection, transaction, cmp, cancellationToken).ConfigureAwait(false);
                }
                if (audit.CanaryAssignment is { } assign)
                {
                    await InsertCanaryAssignmentAsync(connection, transaction, assign, cancellationToken).ConfigureAwait(false);
                }
                if (audit.RollbackRecord is { } rb)
                {
                    await InsertRollbackRecordAsync(connection, transaction, rb, cancellationToken).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return next;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task InsertCanaryAssignmentAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        CanaryAssignment assignment,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("pipeline_canary_assignments")} (
    assignment_id, run_id, proposal_id, strategy, assigned_at, data)
VALUES (
    @assignment_id, @run_id, @proposal_id, @strategy, @assigned_at, @data)
ON CONFLICT (assignment_id) DO UPDATE SET
    run_id = EXCLUDED.run_id,
    proposal_id = EXCLUDED.proposal_id,
    strategy = EXCLUDED.strategy,
    assigned_at = EXCLUDED.assigned_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("assignment_id", assignment.AssignmentId);
        command.Parameters.AddWithValue("run_id", assignment.RunId);
        command.Parameters.AddWithValue("proposal_id", assignment.ProposalId);
        command.Parameters.AddWithValue("strategy", assignment.Strategy.ToString());
        command.Parameters.AddWithValue("assigned_at", assignment.AssignedAt);
        AddJson(command, "data", assignment);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertRollbackRecordAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        RollbackRecord record,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("pipeline_rollback_records")} (
    record_id, run_id, proposal_id, reason, triggered_at, data)
VALUES (
    @record_id, @run_id, @proposal_id, @reason, @triggered_at, @data)
ON CONFLICT (record_id) DO UPDATE SET
    run_id = EXCLUDED.run_id,
    proposal_id = EXCLUDED.proposal_id,
    reason = EXCLUDED.reason,
    triggered_at = EXCLUDED.triggered_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("record_id", record.RecordId);
        command.Parameters.AddWithValue("run_id", record.RunId);
        command.Parameters.AddWithValue("proposal_id", record.ProposalId);
        command.Parameters.AddWithValue("reason", record.Reason.ToString());
        command.Parameters.AddWithValue("triggered_at", record.TriggeredAt);
        AddJson(command, "data", record);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertBaselineComparisonAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        BaselineComparison comparison,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("pipeline_baseline_comparisons")} (
    comparison_id, proposal_id, compared_at, data)
VALUES (
    @comparison_id, @proposal_id, @compared_at, @data)
ON CONFLICT (comparison_id) DO UPDATE SET
    proposal_id = EXCLUDED.proposal_id,
    compared_at = EXCLUDED.compared_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("comparison_id", comparison.ComparisonId);
        command.Parameters.AddWithValue("proposal_id", comparison.ProposalId);
        command.Parameters.AddWithValue("compared_at", comparison.ComparedAt);
        AddJson(command, "data", comparison);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---------- Canary assignments ----------

    /// <inheritdoc />
    public async Task SaveCanaryAssignmentAsync(CanaryAssignment assignment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("pipeline_canary_assignments")} (
    assignment_id, run_id, proposal_id, strategy, assigned_at, data)
VALUES (
    @assignment_id, @run_id, @proposal_id, @strategy, @assigned_at, @data)
ON CONFLICT (assignment_id) DO UPDATE SET
    run_id = EXCLUDED.run_id,
    proposal_id = EXCLUDED.proposal_id,
    strategy = EXCLUDED.strategy,
    assigned_at = EXCLUDED.assigned_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("assignment_id", assignment.AssignmentId);
        command.Parameters.AddWithValue("run_id", assignment.RunId);
        command.Parameters.AddWithValue("proposal_id", assignment.ProposalId);
        command.Parameters.AddWithValue("strategy", assignment.Strategy.ToString());
        command.Parameters.AddWithValue("assigned_at", assignment.AssignedAt);
        AddJson(command, "data", assignment);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CanaryAssignment>> ListCanaryAssignmentsByRunAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("pipeline_canary_assignments")}
WHERE run_id = @run_id
ORDER BY assigned_at ASC, assignment_id ASC;
""";
        command.Parameters.AddWithValue("run_id", runId);

        return await ExecuteReaderJsonAsync<CanaryAssignment>(command, cancellationToken).ConfigureAwait(false);
    }

    // ---------- Rollback records ----------

    /// <inheritdoc />
    public async Task SaveRollbackRecordAsync(RollbackRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("pipeline_rollback_records")} (
    record_id, run_id, proposal_id, reason, triggered_at, data)
VALUES (
    @record_id, @run_id, @proposal_id, @reason, @triggered_at, @data)
ON CONFLICT (record_id) DO UPDATE SET
    run_id = EXCLUDED.run_id,
    proposal_id = EXCLUDED.proposal_id,
    reason = EXCLUDED.reason,
    triggered_at = EXCLUDED.triggered_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("record_id", record.RecordId);
        command.Parameters.AddWithValue("run_id", record.RunId);
        command.Parameters.AddWithValue("proposal_id", record.ProposalId);
        command.Parameters.AddWithValue("reason", record.Reason.ToString());
        command.Parameters.AddWithValue("triggered_at", record.TriggeredAt);
        AddJson(command, "data", record);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RollbackRecord?> GetRollbackRecordByRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("pipeline_rollback_records")}
WHERE run_id = @run_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("run_id", runId);
        return await ExecuteScalarJsonAsync<RollbackRecord>(command, cancellationToken).ConfigureAwait(false);
    }

    // ---------- Baseline comparisons ----------

    /// <inheritdoc />
    public async Task SaveBaselineComparisonAsync(BaselineComparison comparison, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("pipeline_baseline_comparisons")} (
    comparison_id, proposal_id, compared_at, data)
VALUES (
    @comparison_id, @proposal_id, @compared_at, @data)
ON CONFLICT (comparison_id) DO UPDATE SET
    proposal_id = EXCLUDED.proposal_id,
    compared_at = EXCLUDED.compared_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("comparison_id", comparison.ComparisonId);
        command.Parameters.AddWithValue("proposal_id", comparison.ProposalId);
        command.Parameters.AddWithValue("compared_at", comparison.ComparedAt);
        AddJson(command, "data", comparison);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BaselineComparison>> ListBaselineComparisonsByProposalAsync(
        string proposalId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("pipeline_baseline_comparisons")}
WHERE proposal_id = @proposal_id
ORDER BY compared_at DESC, comparison_id DESC;
""";
        command.Parameters.AddWithValue("proposal_id", proposalId);

        return await ExecuteReaderJsonAsync<BaselineComparison>(command, cancellationToken).ConfigureAwait(false);
    }

    // ---------- Stage transitions (R28-B.8) ----------

    /// <inheritdoc />
    /// <remarks>
    /// stage_transitions 表使用与 pipeline_runs 等表一致的“反规范化索引列 + data jsonb”模式。
    /// from_stage / to_stage 列存百分比值的文本形式（与迁移 DDL 的 text 类型一致），完整记录保存在 data jsonb。
    /// ON CONFLICT (transition_id) DO UPDATE 实现同 TransitionId 覆盖（幂等）。
    /// </remarks>
    public async Task SaveStageTransitionAsync(StageTransitionRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("stage_transitions")} (
    transition_id, run_id, from_stage, to_stage, transitioned_at,
    idempotency_key, observation_batch_id, data)
VALUES (
    @transition_id, @run_id, @from_stage, @to_stage, @transitioned_at,
    @idempotency_key, @observation_batch_id, @data)
ON CONFLICT (transition_id) DO UPDATE SET
    run_id = EXCLUDED.run_id,
    from_stage = EXCLUDED.from_stage,
    to_stage = EXCLUDED.to_stage,
    transitioned_at = EXCLUDED.transitioned_at,
    idempotency_key = EXCLUDED.idempotency_key,
    observation_batch_id = EXCLUDED.observation_batch_id,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("transition_id", record.TransitionId);
        command.Parameters.AddWithValue("run_id", record.RunId);
        command.Parameters.AddWithValue("from_stage", record.FromPercentage.ToString());
        command.Parameters.AddWithValue("to_stage", record.ToPercentage.ToString());
        command.Parameters.AddWithValue("transitioned_at", record.TransitionedAt);
        command.Parameters.AddWithValue("idempotency_key", (object?)record.IdempotencyKey ?? DBNull.Value);
        command.Parameters.AddWithValue("observation_batch_id", (object?)record.ObservationBatchId ?? DBNull.Value);
        AddJson(command, "data", record);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StageTransitionRecord>> ListStageTransitionsByRunAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("stage_transitions")}
WHERE run_id = @run_id
ORDER BY transitioned_at ASC, transition_id ASC;
""";
        command.Parameters.AddWithValue("run_id", runId);

        return await ExecuteReaderJsonAsync<StageTransitionRecord>(command, cancellationToken).ConfigureAwait(false);
    }
}
