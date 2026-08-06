using Npgsql;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// v64 → v65：Model Node 身份拆分（NodeGroupId + InstanceId 主键）。
/// - model_node_membership：node_id → node_group_id（列改名），主键 (node_id) →
///   (node_group_id, instance_id)——同一节点组可驻留多个实例，各实例独立持有成员租约
///   （修复"每节点仅一个活跃实例可入集群"的部署限制）。
/// - model_node_applied_state：node_id → node_group_id（列改名），新增 instance_id
///   （存量行回填为 node_group_id——旧行是节点级记录，拆分后按实例隔离，不再匹配任何
///   活跃实例，仅作为历史审计行保留），主键 (node_id, slot_name) →
///   (node_group_id, instance_id, slot_name)。
/// 三个阶段（全部按表/列存在性守卫，幂等可重入）：
///   Online：列改名 + ADD COLUMN；
///   Backfill：存量行 instance_id 回填；
///   ConstraintValidate：DROP 旧主键 + ADD 新主键。
/// 执行时机说明：版本化迁移步骤先于基线 DDL 执行。新库路径下 model_node_membership
/// 由 v59→v60 步骤先以旧结构创建（本步骤负责就地改造），而 model_node_applied_state
/// 尚不存在（由基线按新结构创建）——所有 applied_state 操作以表存在性为守卫。
/// </summary>
public sealed class PostgresMigrationModelNodeIdentitySplit : IPostgresMigrationStep
{
    public string MigrationId => "0012_model_node_identity_split";

    public string FromSchemaVersion => "cc-schema-v64";

    public string ToSchemaVersion => "cc-schema-v65";

    public string Description =>
        "model_node_membership / model_node_applied_state 拆分 NodeGroupId + InstanceId 主键："
        + "同一节点组多实例独立持有成员租约与已应用状态。";

    public IReadOnlyList<PostgresMigrationStage> Stages { get; } =
    [
        PostgresMigrationStage.Online,
        PostgresMigrationStage.Backfill,
        PostgresMigrationStage.ConstraintValidate
    ];

    public async Task<string?> PreCheckAsync(
        NpgsqlConnection connection,
        PostgresOptions options,
        CancellationToken cancellationToken)
    {
        var table = PostgresNames.Table(options, "model_node_membership");
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;

        // 目标表不存在时无需执行：新数据库由基线 DDL 直接以新列创建。
        command.CommandText = "SELECT to_regclass(@table_name)::text;";
        command.Parameters.AddWithValue("table_name", table);
        var tableExists = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (tableExists is null or DBNull)
        {
            return null;
        }

        // node_group_id 列已存在 = 已迁移（或新库基线已建）；返回 null 跳过。
        command.CommandText = """
            SELECT 1
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            WHERE c.oid = to_regclass(@table_name)
              AND a.attname = 'node_group_id'
            LIMIT 1;
            """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("table_name", table);
        var exists = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return exists is null or DBNull ? string.Empty : null;
    }

    public async Task ExecuteStageAsync(
        PostgresMigrationStage stage,
        NpgsqlConnection connection,
        PostgresOptions options,
        CancellationToken cancellationToken)
    {
        var memberships = PostgresNames.Table(options, "model_node_membership");
        var appliedStates = PostgresNames.Table(options, "model_node_applied_state");
        var membershipPk = $"{options.TablePrefix}model_node_membership_pkey";
        var appliedStatePk = $"{options.TablePrefix}model_node_applied_state_pkey";
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;

        if (stage == PostgresMigrationStage.Online)
        {
            // 列改名（node_id → node_group_id）仅在表与旧列都存在时执行；instance_id 仅加在
            // 已存在的 applied_state（新库由基线按新结构创建，此处无需触碰）。幂等重入。
            command.CommandText = $"""
DO $mig$
BEGIN
    IF to_regclass('{memberships}') IS NOT NULL
       AND EXISTS (SELECT 1 FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid
                   WHERE c.oid = to_regclass('{memberships}') AND a.attname = 'node_id') THEN
        EXECUTE 'ALTER TABLE {memberships} RENAME COLUMN node_id TO node_group_id';
    END IF;
    IF to_regclass('{appliedStates}') IS NOT NULL
       AND EXISTS (SELECT 1 FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid
                   WHERE c.oid = to_regclass('{appliedStates}') AND a.attname = 'node_id') THEN
        EXECUTE 'ALTER TABLE {appliedStates} RENAME COLUMN node_id TO node_group_id';
    END IF;
    IF to_regclass('{appliedStates}') IS NOT NULL THEN
        EXECUTE 'ALTER TABLE {appliedStates} ADD COLUMN IF NOT EXISTS instance_id text NULL';
    END IF;
END
$mig$;
""";
        }
        else if (stage == PostgresMigrationStage.Backfill)
        {
            // 存量行回填：旧节点级记录 instance_id = node_group_id（拆分后不再匹配任何活跃实例，
            // 仅作为历史审计行保留；新实例写入时按真实 (NodeGroupId, InstanceId) 键）。
            command.CommandText = $"""
DO $mig$
BEGIN
    IF to_regclass('{appliedStates}') IS NOT NULL THEN
        EXECUTE 'UPDATE {appliedStates} SET instance_id = node_group_id WHERE instance_id IS NULL';
    END IF;
END
$mig$;
""";
        }
        else if (stage == PostgresMigrationStage.ConstraintValidate)
        {
            // 先删旧主键再建新主键（先删后建，重入安全）；applied_state 未存在（新库路径）时跳过。
            command.CommandText = $"""
DO $mig$
BEGIN
    IF to_regclass('{memberships}') IS NOT NULL THEN
        EXECUTE 'ALTER TABLE {memberships} DROP CONSTRAINT IF EXISTS {membershipPk}';
        EXECUTE 'ALTER TABLE {memberships} ADD PRIMARY KEY (node_group_id, instance_id)';
    END IF;
    IF to_regclass('{appliedStates}') IS NOT NULL THEN
        EXECUTE 'ALTER TABLE {appliedStates} DROP CONSTRAINT IF EXISTS {appliedStatePk}';
        EXECUTE 'ALTER TABLE {appliedStates} ADD PRIMARY KEY (node_group_id, instance_id, slot_name)';
    END IF;
END
$mig$;
""";
        }
        else
        {
            return;
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
