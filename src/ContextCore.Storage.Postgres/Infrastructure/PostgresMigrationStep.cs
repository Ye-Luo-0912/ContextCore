using Npgsql;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>版本化迁移步骤的 DDL 阶段类型，按执行顺序排列。</summary>
public enum PostgresMigrationStage
{
    /// <summary>在线结构变更：ADD COLUMN / CREATE INDEX 等非破坏性 DDL，可随时执行。</summary>
    Online,

    /// <summary>数据回填：对存量行执行 UPDATE / COALESCE 等数据修复，幂等可重入。</summary>
    Backfill,

    /// <summary>约束校验与切换：DROP CONSTRAINT / ADD PRIMARY KEY 等结构切换，执行前必须确认数据满足新约束。</summary>
    ConstraintValidate
}

/// <summary>
/// 版本化迁移步骤。每个步骤负责一段明确的 schema 演进，具备幂等性与可重入性：
/// 步骤通过 <see cref="PreCheckAsync"/> 三态判定当前数据库状态——
/// 返回 null 表示无需执行（已应用或目标表不存在）；返回空字符串表示可以执行；
/// 返回非空字符串表示存在阻断性问题（如数据冲突），迁移将中止并抛出异常。
/// </summary>
public interface IPostgresMigrationStep
{
    /// <summary>迁移步骤唯一标识，例如 "0002_tool_dispatch_results_result_key"。</summary>
    string MigrationId { get; }

    /// <summary>该步骤适用的起始 schema 版本（低于此版本的数据库无需执行）。</summary>
    string FromSchemaVersion { get; }

    /// <summary>该步骤应用后的 schema 版本。</summary>
    string ToSchemaVersion { get; }

    /// <summary>人类可读描述。</summary>
    string Description { get; }

    /// <summary>该步骤包含的阶段，按执行顺序排列。</summary>
    IReadOnlyList<PostgresMigrationStage> Stages { get; }

    /// <summary>
    /// 迁移前检查（在迁移互斥锁内执行）。
    /// 返回 null：跳过（已应用或目标表不存在）；返回空字符串：可以执行；
    /// 返回非空字符串：阻断性错误，迁移中止。
    /// </summary>
    Task<string?> PreCheckAsync(
        NpgsqlConnection connection,
        PostgresOptions options,
        CancellationToken cancellationToken);

    /// <summary>执行指定阶段。每个阶段都必须幂等，可安全重入。</summary>
    Task ExecuteStageAsync(
        PostgresMigrationStage stage,
        NpgsqlConnection connection,
        PostgresOptions options,
        CancellationToken cancellationToken);
}

/// <summary>
/// 版本化迁移步骤注册表，按 FromSchemaVersion / ToSchemaVersion 升序排列。
/// 当前包含 v48 → v49 的 tool_dispatch_results 主键迁移、v52 → v53 的 agent_runs
/// 调度/重试列迁移；后续 schema 演进在此追加新步骤。
/// 注册表是不可变的，启动时构建一次。
/// </summary>
public static class PostgresMigrationStepRegistry
{
    /// <summary>已知迁移步骤，按版本升序排列。</summary>
    public static IReadOnlyList<IPostgresMigrationStep> Steps { get; } =
    [
        new PostgresMigrationToolDispatchResultsResultKey(),
        new PostgresMigrationAgentRunScheduling(),
        new PostgresMigrationModelNodeAppliedStateIsolation(),
        new PostgresMigrationRecoveryCanaryLearningDurability(),
        new PostgresMigrationToolReconciliationLease(),
        new PostgresMigrationAgentRunClaimLease(),
        new PostgresMigrationModelNodeMembership(),
        new PostgresMigrationRetrievalPlanFeedbackHardening(),
        new PostgresMigrationToolReconciliationDecisionRequestId(),
        new PostgresMigrationAgentRunClaimAttempt(),
        new PostgresMigrationWorkspaceQuotaDurability(),
        new PostgresMigrationModelNodeIdentitySplit(),
        new PostgresMigrationRetrievalPlanFeedbackTenantIsolation(),
        new PostgresMigrationAgentRunLeaseWorkspaceKey(),
        new PostgresMigrationQuotaReservationWorkspaceKey(),
        new PostgresMigrationToolDispatchWorkspaceKey(),
        new PostgresMigrationSettlementOutboxExactlyOnce(),
        new PostgresMigrationToolIdempotencyKeyNamespace(),
        new PostgresMigrationLearningArtifact(),
        new PostgresMigrationDecisionCommitOutbox()
    ];
}
