namespace ContextCore.Abstractions;

/// <summary>
/// HA 迁移协调器阶段（Migration Coordinator Phase）。
/// </summary>
/// <remarks>
/// 协调器将「确保 schema 迁移完成」建模为显式阶段状态机：
/// <see cref="Idle"/>（未运行）→ <see cref="AcquiringLock"/>（等待/获取迁移互斥锁）→
/// <see cref="Migrating"/>（正在执行 DDL）→ <see cref="UpToDate"/>（成功）或
/// <see cref="Failed"/>（失败）。多实例并发启动时，pg_advisory_lock 保证同一时刻
/// 仅一个实例处于 <see cref="Migrating"/>，其余实例在 <see cref="AcquiringLock"/> 等待。
/// </remarks>
public enum MigrationCoordinatorPhase : byte
{
    /// <summary>尚未执行过协调（初始状态）。</summary>
    Idle = 0,

    /// <summary>正在等待/获取迁移互斥锁（其他实例可能正在迁移）。</summary>
    AcquiringLock = 1,

    /// <summary>已持有锁，正在执行 schema 迁移（DDL）。</summary>
    Migrating = 2,

    /// <summary>迁移完成，schema 已与代码版本一致。</summary>
    UpToDate = 3,

    /// <summary>迁移失败（抛出异常，等待重试）。</summary>
    Failed = 4
}

/// <summary>
/// HA 迁移协调器状态报告（供 operator 状态端点与可观测性消费）。
/// </summary>
public sealed class MigrationCoordinatorStatus
{
    /// <summary>协调器当前阶段。</summary>
    public required MigrationCoordinatorPhase Phase { get; init; }

    /// <summary>协调器是否启用（仅 Postgres provider 注册；非 Postgres 时为 false）。</summary>
    public required bool Enabled { get; init; }

    /// <summary>实例 ID（区分多实例部署中的哪个实例执行了迁移）。</summary>
    public required string InstanceId { get; init; }

    /// <summary>迁移互斥锁键（FNV-1a 哈希；同一部署多实例收敛到同一键）。</summary>
    public required long LockKey { get; init; }

    /// <summary>数据库已应用版本（未迁移时为 null）。</summary>
    public string? AppliedVersion { get; init; }

    /// <summary>代码版本（SchemaVersion 常量）。</summary>
    public string? CodeVersion { get; init; }

    /// <summary>是否已是最新（AppliedVersion == CodeVersion）。</summary>
    public required bool UpToDate { get; init; }

    /// <summary>最近一次协调运行时间（UTC；null = 尚未运行）。</summary>
    public DateTimeOffset? LastRunAtUtc { get; init; }

    /// <summary>最近一次协调是否成功。</summary>
    public required bool LastRunSucceeded { get; init; }

    /// <summary>最近一次协调耗时（毫秒）。</summary>
    public long LastRunDurationMs { get; init; }

    /// <summary>最近一次协调结果消息（"already up to date" / "migration applied" / 异常信息）。</summary>
    public string? LastRunMessage { get; init; }

    /// <summary>附加说明（如非 Postgres provider 的提示）。</summary>
    public string? Note { get; init; }
}

/// <summary>
/// HA 迁移协调器抽象：确保 schema 迁移在多变实例并发启动时只由一个实例执行
/// （Postgres 实现通过 pg_advisory_lock 互斥），其余实例在锁上等待/重试。
/// </summary>
/// <remarks>
/// <para>
/// 解决的问题：HA 部署 N 个实例同时启动，每个实例首次访问存储时都会触发
/// <c>EnsureMigratedAsync</c>。迁移 SQL 幂等，但并发执行仍存在
/// schema_versions 竞态与重复 DDL 开销。协调器将迁移执行收敛为「单执行者」：
/// 同一时刻只有一个实例执行 DDL，其余实例等待锁释放后复查版本直接通过。
/// </para>
/// <para>
/// 仅 Postgres provider 注册。非 Postgres provider 时调用方检测 null 返回
/// Enabled=false 状态（不抛错）。实现：
/// <c>ContextCore.Storage.Postgres.Stores.PostgresMigrationCoordinator</c>。
/// </para>
/// </remarks>
public interface IMigrationCoordinator
{
    /// <summary>
    /// 确保 schema 迁移完成（幂等，可并发调用）。内部以互斥锁串行化：
    /// 并发调用只有一个执行迁移，其余等待后复查版本短路返回。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>协调后的最新状态。</returns>
    Task<MigrationCoordinatorStatus> EnsureSchemaAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取当前协调器状态快照（不触发迁移）。
    /// </summary>
    ValueTask<MigrationCoordinatorStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}
