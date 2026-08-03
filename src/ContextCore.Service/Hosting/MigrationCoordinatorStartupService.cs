using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ContextCore.Service.Hosting;

/// <summary>
/// HA 迁移协调器启动服务：服务启动时主动执行一次 schema 迁移协调。
/// </summary>
/// <remarks>
/// <para>
/// 解决的问题：HA 部署 N 个实例同时启动，若只依赖各 store 的惰性
/// <c>EnsureMigratedAsync</c>，迁移发生在首个请求/首个 store 访问时，且
/// 无显式协调观测。本服务在启动阶段主动调用
/// <see cref="IMigrationCoordinator.EnsureSchemaAsync"/>：多实例并发启动时
/// pg_advisory_lock 保证只有一个实例执行 DDL，其余实例在锁上等待后短路通过。
/// </para>
/// <para>
/// 行为：
/// <list type="bullet">
/// <item>未注册协调器（非 Postgres provider）→ 立即退出（no-op）。</item>
/// <item><see cref="MigrationCoordinatorOptions.StartupRunEnabled"/> = false → 退出（惰性迁移）。</item>
/// <item>迁移失败或超过 <see cref="MigrationCoordinatorOptions.StartupTimeoutSeconds"/> →
/// 记录错误并重新抛出（fail-fast：schema 未就绪时应用不应继续服务流量）。</item>
/// </list>
/// </para>
/// </remarks>
public sealed class MigrationCoordinatorStartupService : BackgroundService
{
    private readonly IMigrationCoordinator _coordinator;
    private readonly MigrationCoordinatorOptions _options;
    private readonly ILogger<MigrationCoordinatorStartupService> _logger;

    public MigrationCoordinatorStartupService(
        IMigrationCoordinator coordinator,
        MigrationCoordinatorOptions options,
        ILogger<MigrationCoordinatorStartupService>? logger = null)
    {
        _coordinator = coordinator;
        _options = options;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MigrationCoordinatorStartupService>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.StartupRunEnabled)
        {
            _logger.LogInformation("Migration coordinator startup run disabled (MigrationCoordinator:StartupRunEnabled=false).");
            return;
        }

        // 有界等待：StartupTimeoutSeconds 内未完成（含等待锁）视为失败（fail-fast）。
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(_options.StartupTimeoutSeconds, 1)));
        try
        {
            var status = await _coordinator.EnsureSchemaAsync(timeoutCts.Token).ConfigureAwait(false);
            _logger.LogInformation(
                "Migration coordinator startup run completed (instance={InstanceId}, phase={Phase}, upToDate={UpToDate}, applied={AppliedVersion}, durationMs={DurationMs}).",
                status.InstanceId, status.Phase, status.UpToDate, status.AppliedVersion ?? "<none>", status.LastRunDurationMs);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(
                "Migration coordinator startup run timed out after {TimeoutSeconds}s (instance={InstanceId}).",
                _options.StartupTimeoutSeconds, _options.InstanceId);
            throw new TimeoutException(
                $"Migration coordinator startup run timed out after {_options.StartupTimeoutSeconds}s " +
                $"(instance={_options.InstanceId}). Schema migration did not complete; failing fast.");
        }
    }
}
