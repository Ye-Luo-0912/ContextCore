using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL Pipeline Run 持久化存储。
/// R27-2：替代 <see cref="ContextCore.Core.Services.Evolution.InMemoryPipelineRunStore"/>，
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
///   4. <see cref="SaveRunAsync"/> 幂等（同主键覆盖）。
///   5. 与 PostgresAgentCheckpointStore 设计模式对齐（R26-2）。
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
    current_stage, status, started_at, updated_at, completed_at, rollback_reason, data)
VALUES (
    @run_id, @proposal_id, @proposal_major, @proposal_minor, @target_component,
    @current_stage, @status, @started_at, @updated_at, @completed_at, @rollback_reason, @data)
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
        AddJson(command, "data", snapshot);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
}
