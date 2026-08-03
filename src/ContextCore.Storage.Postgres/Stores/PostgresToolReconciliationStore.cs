using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

// ===========================================================================
// PostgresToolReconciliationStore — Tool 对账记录存储（PostgreSQL 持久化实现）
//
// Tool Reconciliation Control Plane（-B1）：对账记录跨进程持久化，
// 替代 InMemoryToolReconciliationStore 成为 ProductionHA 组合根下的真相源：
// - 多实例 ToolReconciliationWorker / 人工 resolve 端点共享同一数据库，
// 杜绝"对账记录只在创建它的实例内存中"导致的裁决丢失；
// - CreateAsync 按 (run_id, request_id) UNIQUE 幂等（重复创建返回既有记录）；
// - TryBeginAsync / TryResetToPendingAsync / MarkResolvedAsync / MarkRejectedAsync
// 使用 expected-state CAS（0 行受影响 = 并发冲突/已裁决，幂等）；
// - deadline_utc 列 + ControlRoom 列表（ListAsync）支持过期未决高亮与告警计数；
// - external_operation_id partial 索引支持按 journal 外部操作 ID 反查。
// ===========================================================================

/// <summary>
/// Tool 对账记录存储（PostgreSQL 实现）。完整 <see cref="ToolReconciliationRecord"/>
/// 持久化到 <c>data jsonb</c>，规范化字段（run_id / request_id / status / deadline_utc /
/// external_operation_id 等）用于索引查询与 CAS。
/// </summary>
public sealed class PostgresToolReconciliationStore : PostgresStoreBase, IToolReconciliationStore
{
    /// <summary>ControlRoom 分页大小上限（服务端 clamp，防止无界读取）。</summary>
    public const int MaxPageSize = 200;

    /// <summary>规范化读取列清单：CAS 更新只改这些列，读取必须走列而非 data jsonb（jsonb 会过期）。</summary>
    private const string ReadColumns = """
        reconciliation_id, run_id, workspace_id, request_id, tool_name,
        external_operation_id, reconciliation_handler, status,
        result, side_effect_occurred, reason,
        created_at, updated_at, resolved_at, deadline_utc
        """;

    public PostgresToolReconciliationStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <inheritdoc />
    public async ValueTask<ToolReconciliationRecord> CreateAsync(ToolReconciliationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("tool_reconciliation_entries")} (
    reconciliation_id, run_id, workspace_id, request_id, tool_name,
    external_operation_id, reconciliation_handler, status,
    result, side_effect_occurred, reason,
    created_at, updated_at, resolved_at, deadline_utc, data)
VALUES (
    @reconciliation_id, @run_id, @workspace_id, @request_id, @tool_name,
    @external_operation_id, @reconciliation_handler, @status,
    @result, @side_effect_occurred, @reason,
    @created_at, @updated_at, @resolved_at, @deadline_utc, @data)
ON CONFLICT (run_id, request_id) DO NOTHING;
""";
        command.Parameters.AddWithValue("reconciliation_id", record.ReconciliationId);
        command.Parameters.AddWithValue("run_id", record.RunId);
        command.Parameters.AddWithValue("workspace_id", record.WorkspaceId);
        command.Parameters.AddWithValue("request_id", record.RequestId);
        command.Parameters.AddWithValue("tool_name", record.ToolName);
        command.Parameters.AddWithValue("external_operation_id", (object?)record.ExternalOperationId ?? DBNull.Value);
        command.Parameters.AddWithValue("reconciliation_handler", (object?)record.ReconciliationHandler ?? DBNull.Value);
        command.Parameters.AddWithValue("status", (byte)record.Status);
        command.Parameters.AddWithValue("result", (object?)record.Result ?? DBNull.Value);
        command.Parameters.AddWithValue("side_effect_occurred", (object?)record.SideEffectOccurred ?? DBNull.Value);
        command.Parameters.AddWithValue("reason", (object?)record.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("created_at", record.CreatedAt);
        command.Parameters.AddWithValue("updated_at", (object?)record.UpdatedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("resolved_at", (object?)record.ResolvedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("deadline_utc", (object?)record.DeadlineUtc ?? DBNull.Value);
        AddJson(command, "data", record);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // 幂等：无论 INSERT 是否生效，返回 (run_id, request_id) 对应的既有记录。
        var existing = await GetByRunAndRequestAsync(connection, record.RunId, record.RequestId, cancellationToken).ConfigureAwait(false);
        return existing ?? record;
    }

    /// <inheritdoc />
    public async ValueTask<ToolReconciliationRecord?> GetAsync(string reconciliationId, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"SELECT {ReadColumns} FROM {Table("tool_reconciliation_entries")} WHERE reconciliation_id = @reconciliation_id;";
        command.Parameters.AddWithValue("reconciliation_id", reconciliationId);
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ToolReconciliationRecord>> ListByRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT {ReadColumns} FROM {Table("tool_reconciliation_entries")}
WHERE run_id = @run_id
ORDER BY created_at ASC;
""";
        command.Parameters.AddWithValue("run_id", runId);
        return await ReadManyAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ToolReconciliationRecord>> QueryByExternalOperationIdAsync(string externalOperationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(externalOperationId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT {ReadColumns} FROM {Table("tool_reconciliation_entries")}
WHERE external_operation_id = @external_operation_id
ORDER BY created_at DESC;
""";
        command.Parameters.AddWithValue("external_operation_id", externalOperationId);
        return await ReadManyAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ReconciliationListResult> ListAsync(ReconciliationQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        var limit = Math.Clamp(query.Limit > 0 ? query.Limit : 50, 1, MaxPageSize);
        var offset = Math.Max(0, query.Offset);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // 页面/总数 WHERE：基础过滤 + OverdueOnly（查询请求只看过期未决时）。
        // OverdueCount WHERE：始终按"过期未决"计算（DeadlineUtc < now 且 Pending/Running）。
        var pageWhere = BuildWhereClause(query, includeOverduePredicate: query.OverdueOnly);
        var overdueWhere = BuildWhereClause(query, includeOverduePredicate: true);
        command.CommandText = $"""
SELECT
    (SELECT count(*) FROM {Table("tool_reconciliation_entries")} WHERE {pageWhere}),
    (SELECT count(*) FROM {Table("tool_reconciliation_entries")} WHERE {overdueWhere})
""";
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("offset", offset);
        AddQueryParameters(command, query);

        int total;
        int overdueCount;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            // count(*) 在 Postgres 中返回 bigint；显式转 long 再收窄到 int。
            total = checked((int)reader.GetInt64(0));
            overdueCount = checked((int)reader.GetInt64(1));
        }

        // 分页条目：与过滤条件一致，按 CreatedAt 倒序（最新在前）。
        var pageCommand = connection.CreateCommand();
        pageCommand.CommandTimeout = Options.CommandTimeoutSeconds;
        pageCommand.CommandText = $"""
SELECT {ReadColumns} FROM {Table("tool_reconciliation_entries")}
WHERE {pageWhere}
ORDER BY created_at DESC
LIMIT @limit OFFSET @offset;
""";
        pageCommand.Parameters.AddWithValue("limit", limit);
        pageCommand.Parameters.AddWithValue("offset", offset);
        AddQueryParameters(pageCommand, query);
        var items = await ReadManyAsync(pageCommand, cancellationToken).ConfigureAwait(false);

        return new ReconciliationListResult
        {
            Items = items,
            Total = total,
            OverdueCount = overdueCount
        };
    }

    /// <inheritdoc />
    public async ValueTask<bool> HasUnresolvedForRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT EXISTS (
    SELECT 1 FROM {Table("tool_reconciliation_entries")}
    WHERE run_id = @run_id
      AND status IN ({(byte)ToolReconciliationStatus.Pending}, {(byte)ToolReconciliationStatus.Running})
);
""";
        command.Parameters.AddWithValue("run_id", runId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is bool b && b;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ToolReconciliationRecord>> ListPendingAsync(int take, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT {ReadColumns} FROM {Table("tool_reconciliation_entries")}
WHERE status = @status
ORDER BY created_at ASC
LIMIT @take;
""";
        command.Parameters.AddWithValue("status", (byte)ToolReconciliationStatus.Pending);
        command.Parameters.AddWithValue("take", TakeOrDefault(take));
        return await ReadManyAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryBeginAsync(string reconciliationId, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
UPDATE {Table("tool_reconciliation_entries")}
SET status = @running, updated_at = @now
WHERE reconciliation_id = @reconciliation_id AND status = @pending;
""";
        command.Parameters.AddWithValue("running", (byte)ToolReconciliationStatus.Running);
        command.Parameters.AddWithValue("pending", (byte)ToolReconciliationStatus.Pending);
        command.Parameters.AddWithValue("reconciliation_id", reconciliationId);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryResetToPendingAsync(string reconciliationId, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
UPDATE {Table("tool_reconciliation_entries")}
SET status = @pending, updated_at = @now
WHERE reconciliation_id = @reconciliation_id AND status = @running;
""";
        command.Parameters.AddWithValue("pending", (byte)ToolReconciliationStatus.Pending);
        command.Parameters.AddWithValue("running", (byte)ToolReconciliationStatus.Running);
        command.Parameters.AddWithValue("reconciliation_id", reconciliationId);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc />
    public ValueTask<bool> MarkResolvedAsync(string reconciliationId, ToolReconciliationOutcome outcome, CancellationToken cancellationToken = default)
        => MarkTerminalAsync(reconciliationId, ToolReconciliationStatus.Resolved, outcome, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> MarkRejectedAsync(string reconciliationId, ToolReconciliationOutcome outcome, CancellationToken cancellationToken = default)
        => MarkTerminalAsync(reconciliationId, ToolReconciliationStatus.Rejected, outcome, cancellationToken);

    /// <summary>CAS 推进到终态（Resolved/Rejected）；已终态（幂等冲突）返回 false。</summary>
    private async ValueTask<bool> MarkTerminalAsync(
        string reconciliationId,
        ToolReconciliationStatus target,
        ToolReconciliationOutcome outcome,
        CancellationToken cancellationToken)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
UPDATE {Table("tool_reconciliation_entries")}
SET status = @target,
    result = @result,
    side_effect_occurred = @side_effect_occurred,
    reason = @reason,
    updated_at = @now,
    resolved_at = @now
WHERE reconciliation_id = @reconciliation_id
  AND status IN (@pending, @running);
""";
        command.Parameters.AddWithValue("target", (byte)target);
        command.Parameters.AddWithValue("result", (object?)outcome.Result ?? DBNull.Value);
        command.Parameters.AddWithValue("side_effect_occurred", (object?)outcome.SideEffectOccurred ?? DBNull.Value);
        command.Parameters.AddWithValue("reason", (object?)outcome.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("reconciliation_id", reconciliationId);
        command.Parameters.AddWithValue("pending", (byte)ToolReconciliationStatus.Pending);
        command.Parameters.AddWithValue("running", (byte)ToolReconciliationStatus.Running);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>按 RunId+RequestId 查询既有记录（CreateAsync 幂等返回）。</summary>
    private async Task<ToolReconciliationRecord?> GetByRunAndRequestAsync(
        NpgsqlConnection connection,
        string runId,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT {ReadColumns} FROM {Table("tool_reconciliation_entries")}
WHERE run_id = @run_id AND request_id = @request_id;
""";
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("request_id", requestId);
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取单条记录（0 行 → null）。</summary>
    private static async Task<ToolReconciliationRecord?> ReadSingleAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return ReadRecord(reader);
    }

    /// <summary>读取多条记录（按查询 ORDER BY）。</summary>
    private static async Task<IReadOnlyList<ToolReconciliationRecord>> ReadManyAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var results = new List<ToolReconciliationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadRecord(reader));
        }
        return results;
    }

    /// <summary>从规范化列构造 ToolReconciliationRecord（与 ReadColumns 顺序一致）。</summary>
    private static ToolReconciliationRecord ReadRecord(NpgsqlDataReader reader)
    {
        static string? NullableStr(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        static DateTimeOffset? NullableTime(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

        return new ToolReconciliationRecord
        {
            ReconciliationId = reader.GetString(0),
            RunId = reader.GetString(1),
            WorkspaceId = reader.GetString(2),
            RequestId = reader.GetString(3),
            ToolName = reader.GetString(4),
            ExternalOperationId = NullableStr(reader, 5),
            ReconciliationHandler = NullableStr(reader, 6),
            Status = (ToolReconciliationStatus)reader.GetInt16(7),
            Result = NullableStr(reader, 8),
            SideEffectOccurred = reader.IsDBNull(9) ? null : reader.GetBoolean(9),
            Reason = NullableStr(reader, 10),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(11),
            UpdatedAt = NullableTime(reader, 12),
            ResolvedAt = NullableTime(reader, 13),
            DeadlineUtc = NullableTime(reader, 14)
        };
    }

    /// <summary>构造 ListAsync 过滤 WHERE 子句（无参数注入；参数由 AddQueryParameters 统一添加）。</summary>
    private static string BuildWhereClause(ReconciliationQuery query, bool includeOverduePredicate)
    {
        var clauses = new List<string> { "1 = 1" };

        if (!string.IsNullOrEmpty(query.WorkspaceId))
        {
            clauses.Add("workspace_id = @workspace_id");
        }
        if (!string.IsNullOrEmpty(query.RunId))
        {
            clauses.Add("run_id = @run_id");
        }
        if (query.Status.HasValue)
        {
            clauses.Add("status = @status");
        }
        if (includeOverduePredicate)
        {
            clauses.Add("deadline_utc IS NOT NULL AND deadline_utc < now()");
            clauses.Add($"status IN ({(byte)ToolReconciliationStatus.Pending}, {(byte)ToolReconciliationStatus.Running})");
        }

        return string.Join(" AND ", clauses);
    }

    /// <summary>为 ListAsync 添加过滤参数（与 BuildWhereClause 保持一致）。</summary>
    private static void AddQueryParameters(NpgsqlCommand command, ReconciliationQuery query)
    {
        if (!string.IsNullOrEmpty(query.WorkspaceId))
        {
            command.Parameters.AddWithValue("workspace_id", query.WorkspaceId);
        }
        if (!string.IsNullOrEmpty(query.RunId))
        {
            command.Parameters.AddWithValue("run_id", query.RunId);
        }
        if (query.Status.HasValue)
        {
            command.Parameters.AddWithValue("status", (byte)query.Status.Value);
        }
    }
}
