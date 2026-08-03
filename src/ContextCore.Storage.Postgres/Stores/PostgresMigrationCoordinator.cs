using System.Diagnostics;
using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL HA 迁移协调器（Migration Coordinator）。
/// </summary>
/// <remarks>
/// <para>
/// 将「确保 schema 迁移完成」建模为显式阶段状态机并串行化并发调用：
/// 同一时刻仅一个调用方执行迁移（<see cref="PostgresMigrationRunner.MigrateAsync"/>
/// 内部已用 pg_advisory_lock 保证多实例互斥），其余调用方在进程内
/// <see cref="SemaphoreSlim"/> 门闩上等待，复查版本后短路返回。
/// </para>
/// <para>
/// 阶段推进：<see cref="MigrationCoordinatorPhase.Idle"/> → 
/// <see cref="MigrationCoordinatorPhase.AcquiringLock"/>（等待/获取锁）→
/// <see cref="MigrationCoordinatorPhase.Migrating"/>（执行 DDL）→
/// <see cref="MigrationCoordinatorPhase.UpToDate"/> 或
/// <see cref="MigrationCoordinatorPhase.Failed"/>。每次运行记录
/// 结果（时间/耗时/成功/消息），供 operator 状态端点消费。
/// </para>
/// </remarks>
public sealed class PostgresMigrationCoordinator : IMigrationCoordinator
{
    private readonly Func<CancellationToken, Task> _migrateExecutor;
    private readonly Func<CancellationToken, Task<string?>> _appliedVersionReader;
    private readonly PostgresOptions _options;
    private readonly MigrationCoordinatorOptions _coordinatorOptions;
    private readonly ILogger<PostgresMigrationCoordinator> _logger;

    // 进程内串行化门闩：并发 EnsureSchemaAsync 只有一个执行迁移，其余等待后复查。
    private readonly SemaphoreSlim _gate = new(1, 1);

    // 最近一次运行结果（volatile 保证多线程可见性；状态端点与启动协调可并发访问）。
    private volatile MigrationCoordinatorPhase _phase = MigrationCoordinatorPhase.Idle;
    private DateTimeOffset? _lastRunAtUtc;
    private bool _lastRunSucceeded;
    private long _lastRunDurationMs;
    private string? _lastRunMessage;

    /// <summary>初始化 Postgres 迁移协调器（DI 路径）。</summary>
    public PostgresMigrationCoordinator(
        PostgresMigrationRunner migrationRunner,
        PostgresOptions options,
        MigrationCoordinatorOptions coordinatorOptions,
        ILogger<PostgresMigrationCoordinator>? logger = null)
        : this(
            ct => migrationRunner.MigrateAsync(ct),
            ct => migrationRunner.GetAppliedVersionAsync(ct),
            options,
            coordinatorOptions,
            logger)
    {
    }

    /// <summary>
    /// 初始化迁移协调器（测试注入路径：可替换迁移执行器与版本读取器，无需真实 DB）。
    /// </summary>
    internal PostgresMigrationCoordinator(
        Func<CancellationToken, Task> migrateExecutor,
        Func<CancellationToken, Task<string?>> appliedVersionReader,
        PostgresOptions options,
        MigrationCoordinatorOptions coordinatorOptions,
        ILogger<PostgresMigrationCoordinator>? logger = null)
    {
        _migrateExecutor = migrateExecutor;
        _appliedVersionReader = appliedVersionReader;
        _options = options;
        _coordinatorOptions = coordinatorOptions;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgresMigrationCoordinator>.Instance;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 幂等 + 可并发：SemaphoreSlim 门闩串行化；已是最新版本时短路返回（不执行迁移）。
    /// 迁移失败时记录 Failed 状态并重新抛出（调用方决定重试/快速失败策略）。
    /// </remarks>
    public async Task<MigrationCoordinatorStatus> EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 双检：等待期间其他调用方可能已完成迁移。
            var appliedVersion = await _appliedVersionReader(cancellationToken).ConfigureAwait(false);
            if (string.Equals(appliedVersion, PostgresMigrationRunner.SchemaVersion, StringComparison.Ordinal))
            {
                return RecordRun(phase: MigrationCoordinatorPhase.UpToDate, succeeded: true, stopwatch,
                    message: "already up to date", appliedVersion);
            }

            _phase = MigrationCoordinatorPhase.AcquiringLock;
            _logger.LogInformation(
                "Migration coordinator acquiring migration lock (instance={InstanceId}, applied={AppliedVersion}, code={CodeVersion}).",
                _coordinatorOptions.InstanceId, appliedVersion ?? "<none>", PostgresMigrationRunner.SchemaVersion);

            _phase = MigrationCoordinatorPhase.Migrating;
            await _migrateExecutor(cancellationToken).ConfigureAwait(false);

            var afterVersion = await _appliedVersionReader(cancellationToken).ConfigureAwait(false);
            return RecordRun(phase: MigrationCoordinatorPhase.UpToDate, succeeded: true, stopwatch,
                message: "migration applied", afterVersion);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failedStatus = RecordRun(phase: MigrationCoordinatorPhase.Failed, succeeded: false, stopwatch,
                message: ex.Message, appliedVersion: null);
            _logger.LogError(ex,
                "Migration coordinator failed (instance={InstanceId}).",
                _coordinatorOptions.InstanceId);
            throw;
        }
        finally
        {
            // 门闩必须在所有路径释放（成功 / 失败 / 取消），否则后续调用永久阻塞。
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask<MigrationCoordinatorStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        // 不触发迁移；读取当前状态快照。版本读取失败（如 DB 不可达）时降级返回内存状态。
        string? appliedVersion = null;
        try
        {
            appliedVersion = _appliedVersionReader(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // 版本读取失败不阻塞状态端点：返回内存中已知状态。
        }

        var status = BuildStatus(_phase, appliedVersion);
        return ValueTask.FromResult(status);
    }

    private MigrationCoordinatorStatus RecordRun(
        MigrationCoordinatorPhase phase,
        bool succeeded,
        Stopwatch stopwatch,
        string message,
        string? appliedVersion)
    {
        stopwatch.Stop();
        _phase = phase;
        _lastRunAtUtc = DateTimeOffset.UtcNow;
        _lastRunSucceeded = succeeded;
        _lastRunDurationMs = stopwatch.ElapsedMilliseconds;
        _lastRunMessage = message;
        return BuildStatus(phase, appliedVersion);
    }

    private MigrationCoordinatorStatus BuildStatus(MigrationCoordinatorPhase phase, string? appliedVersion)
    {
        return new MigrationCoordinatorStatus
        {
            Phase = phase,
            Enabled = true,
            InstanceId = _coordinatorOptions.InstanceId,
            LockKey = PostgresMigrationRunner.ComputeMigrationLockKey(_options),
            AppliedVersion = appliedVersion,
            CodeVersion = PostgresMigrationRunner.SchemaVersion,
            UpToDate = string.Equals(appliedVersion, PostgresMigrationRunner.SchemaVersion, StringComparison.Ordinal),
            LastRunAtUtc = _lastRunAtUtc,
            LastRunSucceeded = _lastRunSucceeded,
            LastRunDurationMs = _lastRunDurationMs,
            LastRunMessage = _lastRunMessage,
            Note = null
        };
    }
}
