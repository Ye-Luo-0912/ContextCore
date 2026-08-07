using Npgsql;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// v59 → v60：新增 model_node_membership 表（节点成员资格租约）。
/// </summary>
/// <remarks>
/// 列：
/// - node_id：稳定节点标识（机器名，跨进程重启保持同一身份）；
/// - instance_id：同一节点上的具体进程实例（区分同机器的多个进程）；
/// - lease_token：租约令牌（SetServingEnabled 等写操作 fencing 校验，过期持有者不得篡改）；
/// - lease_expires_at：租约过期时间（stale cutoff——过期即视为节点下线，Rollout Ready
///   只基于活跃成员，不再被历史 Applied State 行永久阻止 Converged）；
/// - last_heartbeat：最后心跳时间；
/// - serving_enabled：是否允许承接模型流量（Isolated 节点由 Reconciler 置 false，
///   Admission/Middleware 据此真正停止接收模型流量，不能只写 Applied State 数据库标志）。
/// 
/// 单 Online 阶段：CREATE TABLE IF NOT EXISTS（幂等）；新数据库由基线 DDL 直接建好，
/// PreCheck 检测表存在即跳过。
/// </remarks>
public sealed class PostgresMigrationModelNodeMembership : IPostgresMigrationStep
{
    public string MigrationId => "0007_model_node_membership";

    public string FromSchemaVersion => "cc-schema-v59";

    public string ToSchemaVersion => "cc-schema-v60";

    public string Description =>
        "新增 model_node_membership 表（P0-15 节点成员资格租约：node_id / instance_id / "
        + "lease_token / lease_expires_at / last_heartbeat / serving_enabled）——"
        + "Rollout Ready 基于当前活跃成员而非历史 Applied State 行。";

    public IReadOnlyList<PostgresMigrationStage> Stages { get; } =
    [
        PostgresMigrationStage.Online
    ];

    public async Task<string?> PreCheckAsync(
        NpgsqlConnection connection,
        PostgresOptions options,
        CancellationToken cancellationToken)
    {
        var table = PostgresNames.Table(options, "model_node_membership");
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;

        // 目标表已存在 = 已迁移（或新库基线已建）；返回 null 跳过。
        command.CommandText = "SELECT to_regclass(@table_name)::text;";
        command.Parameters.AddWithValue("table_name", table);
        var tableExists = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return tableExists is null or DBNull ? string.Empty : null;
    }

    public async Task ExecuteStageAsync(
        PostgresMigrationStage stage,
        NpgsqlConnection connection,
        PostgresOptions options,
        CancellationToken cancellationToken)
    {
        if (stage != PostgresMigrationStage.Online)
        {
            return;
        }

        var table = PostgresNames.Table(options, "model_node_membership");
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;
        command.CommandText = $"""
CREATE TABLE IF NOT EXISTS {table} (
    node_id text NOT NULL,
    instance_id text NOT NULL,
    lease_token text NOT NULL,
    lease_expires_at timestamptz NOT NULL,
    last_heartbeat timestamptz NOT NULL,
    serving_enabled boolean NOT NULL DEFAULT true,
    PRIMARY KEY (node_id)
);
""";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
