using Npgsql;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// v70 → v71：Tool 外部业务幂等键作用域重新定义。
///
/// v69 将幂等键唯一约束升级为 (workspace_id, run_id, idempotency_key)——租户隔离正确，
/// 但把业务级幂等范围缩得过小：客户端因网络失败重建另一个 Run、提供相同业务幂等键时，
/// 不同 Run 仍然可以再次执行同一业务操作（如支付订单重复扣款）。
///
/// 三种身份正式分开：
/// - InvocationId：单次 Agent Tool Invocation（toolCallId，运行时身份）；
/// - RequestId：Durable 内部调用身份（TenantRunKey-scoped 哈希，journal 复合键）；
/// - ExternalIdempotencyKey：业务外部操作身份——唯一范围应为
///   (workspace_id, provider_namespace, idempotency_key)，与 Run 生命周期解耦。
///
/// 本迁移把幂等键唯一索引从 (workspace_id, run_id, idempotency_key) 升级为
/// (workspace_id, tool_name, idempotency_key)（tool_name 即 provider/tool 命名空间）：
/// 同一工作区同一 Provider 下，相同业务幂等键只能执行一次——跨 Run 去重生效，
/// 重建 Run 重放同一业务操作被唯一约束拒绝。
///
/// 阶段：Online（索引切换，幂等可重入）。
/// </summary>
public sealed class PostgresMigrationToolIdempotencyKeyNamespace : IPostgresMigrationStep
{
    public string MigrationId => "0018_tool_idempotency_key_namespace";

    public string FromSchemaVersion => "cc-schema-v70";

    public string ToSchemaVersion => "cc-schema-v71";

    public string Description =>
        "tool_dispatch_journal_entries 幂等键唯一索引作用域从 (workspace_id, run_id, idempotency_key) "
        + "升级为 (workspace_id, tool_name, idempotency_key)——ExternalIdempotencyKey 是业务外部操作身份，"
        + "跨 Run 去重（客户端重建 Run 重放同一业务操作被唯一约束拒绝）。";

    public IReadOnlyList<PostgresMigrationStage> Stages { get; } =
    [
        PostgresMigrationStage.Online
    ];

    public async Task<string?> PreCheckAsync(
        NpgsqlConnection connection,
        PostgresOptions options,
        CancellationToken cancellationToken)
    {
        var table = PostgresNames.Table(options, "tool_dispatch_journal_entries");
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;

        // 目标表不存在时无需执行：新数据库由基线 DDL 直接以新结构创建。
        command.CommandText = "SELECT to_regclass(@table_name)::text;";
        command.Parameters.AddWithValue("table_name", table);
        var tableExists = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (tableExists is null or DBNull)
        {
            return null;
        }

        // 幂等键唯一索引已含 tool_name（4 列 = workspace_id, tool_name, idempotency_key
        // 语义；实际索引列数 3）——检查索引定义是否引用 tool_name。
        command.CommandText = """
            SELECT 1
            FROM pg_index i
            JOIN pg_class c ON c.oid = i.indrelid
            WHERE c.relname = @table_name
              AND i.indisunique
              AND EXISTS (
                  SELECT 1 FROM pg_attribute a
                  WHERE a.attrelid = c.oid
                    AND a.attnum = ANY(i.indkey)
                    AND a.attname = 'tool_name')
            LIMIT 1;
            """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("table_name", options.TablePrefix + "tool_dispatch_journal_entries");
        var exists = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return exists is null or DBNull ? string.Empty : null;
    }

    public async Task ExecuteStageAsync(
        PostgresMigrationStage stage,
        NpgsqlConnection connection,
        PostgresOptions options,
        CancellationToken cancellationToken)
    {
        var journal = PostgresNames.Table(options, "tool_dispatch_journal_entries");
        var idempotencyIndex = PostgresNames.Index(options, "tool_dispatch_journal_entries", "idempotency");
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;
        // 幂等键唯一索引作用域：(workspace_id, tool_name, idempotency_key)。
        // partial WHERE idempotency_key IS NOT NULL：NULL 幂等键不参与唯一约束
        // （与"未声明幂等键"语义一致）。
        command.CommandText = $"""
            DROP INDEX IF EXISTS {idempotencyIndex};
            CREATE UNIQUE INDEX IF NOT EXISTS {idempotencyIndex}
                ON {journal} (workspace_id, tool_name, idempotency_key)
                WHERE idempotency_key IS NOT NULL;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
