using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ContextCore.Service.Infrastructure;

// ===========================================================================
// 生产准入控制器（实时探针 + TTL 缓存）
//
// 目标：
//   1. 在启动时一次性校验（ProductionAdmissionValidator）之上，提供请求阶段的
//      运行时准入判定：启动后运行时降级（Postgres 断连 / Model Slot 停用 /
//      应用重启窗口）不再静默放行业务流量。
//   2. 为请求阶段 admission 中间件提供缓存报告（TTL 内复用，避免每个请求
//      都执行全量校验），为 /api/admission/status 提供强制刷新入口。
//
// 实时探针（仅 ProductionHA）：
//   1. postgres-live — IPostgresConnectionFactory.PingAsync 实时连通性。
//   2. model-slot-live — 重新查询 cluster model slot 'primary' 是否仍处于 Active。
//   3. application-started-live — 应用是否已完成启动（所有 HostedService.StartAsync）。
//
// 缓存语义：
//   - 非 ProductionHA：直接透传 validator 的 Skipped 报告（AllPassed=true），不执行实时探针。
//   - TTL 内返回缓存报告；TTL 到期或 forceRefresh=true 时执行全量刷新。
//   - SemaphoreSlim 串行化刷新，双检锁避免并发重复执行全量校验。
// ===========================================================================

/// <summary>
/// 生产准入控制器：组合启动期强制项校验与运行时实时探针，输出带 TTL 缓存的准入报告。
/// </summary>
public sealed class ProductionAdmissionController
{
    private readonly ProductionAdmissionValidator _validator;
    private readonly IServiceProvider _services;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ProductionAdmissionOptions _options;
    private readonly ILogger<ProductionAdmissionController> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ProductionAdmissionReport? _cached;
    private DateTimeOffset _nextRefreshAllowedUtc = DateTimeOffset.MinValue;

    /// <summary>构造函数。</summary>
    public ProductionAdmissionController(
        ProductionAdmissionValidator validator,
        IServiceProvider services,
        IHostApplicationLifetime lifetime,
        ProductionAdmissionOptions options,
        ILogger<ProductionAdmissionController> logger)
    {
        _validator = validator;
        _services = services;
        _lifetime = lifetime;
        _options = options;
        _logger = logger;
    }

    /// <summary>实时探针轮询间隔（TTL）。</summary>
    public TimeSpan ProbeInterval => _options.ProbeInterval;

    /// <summary>
    /// 获取（必要时刷新）生产准入报告。
    /// 非 ProductionHA profile 直接透传 validator 的 Skipped 报告，不执行实时探针。
    /// </summary>
    /// <param name="forceRefresh">true 时忽略 TTL 强制刷新。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>生产准入报告。</returns>
    public async Task<ProductionAdmissionReport> GetOrRefreshAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (!forceRefresh && _cached is not null && now < _nextRefreshAllowedUtc)
        {
            return _cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 双检：等待锁期间可能已被其他请求刷新
            now = DateTimeOffset.UtcNow;
            if (!forceRefresh && _cached is not null && now < _nextRefreshAllowedUtc)
            {
                return _cached;
            }

            var staticReport = await _validator.ValidateAsync(cancellationToken).ConfigureAwait(false);
            ProductionAdmissionReport report;
            if (!staticReport.AdmissionRequired)
            {
                // 非 ProductionHA：无实时探针，直接透传（AllPassed=true）
                report = staticReport;
            }
            else
            {
                var checks = new List<ProductionAdmissionCheck>(staticReport.Checks);
                await RunLiveProbesAsync(checks, cancellationToken).ConfigureAwait(false);
                var allPassed = checks.All(c => c.Status == ProductionAdmissionCheckStatus.Pass);
                report = new ProductionAdmissionReport(
                    AdmissionRequired: true,
                    AllPassed: allPassed,
                    Checks: checks,
                    CheckedAt: DateTimeOffset.UtcNow);
            }

            _cached = report;
            _nextRefreshAllowedUtc = DateTimeOffset.UtcNow + _options.ProbeInterval;
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── 实时探针 ────────────────────────────────────────────────────────

    private async Task RunLiveProbesAsync(
        List<ProductionAdmissionCheck> checks,
        CancellationToken cancellationToken)
    {
        // 探针 1：postgres-live — 实时数据库连通性
        var pgFactory = _services.GetService<IPostgresConnectionFactory>();
        if (pgFactory is null)
        {
            checks.Add(Fail("postgres-live",
                "IPostgresConnectionFactory 未注册——ProductionHA 要求 Postgres 存储，无法执行实时连通性探针。"));
        }
        else
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(_options.ProbeTimeout);
                var (success, error) = await pgFactory.PingAsync(cts.Token).ConfigureAwait(false);
                checks.Add(success
                    ? Pass("postgres-live", "PostgreSQL 实时连通性正常（PingAsync 通过）。")
                    : Fail("postgres-live", $"PostgreSQL 实时连通性失败：{error}"));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                checks.Add(Fail("postgres-live",
                    $"PostgreSQL 实时连通性探针超时（超过 {_options.ProbeTimeout.TotalSeconds:0.#} 秒）。"));
            }
            catch (Exception ex)
            {
                checks.Add(Fail("postgres-live",
                    $"PostgreSQL 实时连通性探针异常：{ex.GetType().Name}: {ex.Message}"));
            }
        }

        // 探针 2：model-slot-live — 重新查询 'primary' 槽位是否仍处于应用状态
        var slotStore = _services.GetService<IClusterModelSlotStore>();
        if (slotStore is null)
        {
            checks.Add(Fail("model-slot-live",
                "IClusterModelSlotStore 未注册——ProductionHA 要求 cluster model slot 存储，无法执行实时状态探针。"));
        }
        else
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(_options.ProbeTimeout);
                var slot = await slotStore.GetAsync("primary", cts.Token).ConfigureAwait(false);
                if (slot is null)
                {
                    checks.Add(Fail("model-slot-live",
                        "cluster model slot 'primary' 不存在——期望模型已丢失。"));
                }
                else if (slot.DesiredStatus != ClusterModelSlotDesiredStatus.Active
                    || string.IsNullOrWhiteSpace(slot.ActiveModelArtifactId))
                {
                    checks.Add(Fail("model-slot-live",
                        $"cluster model slot 'primary' 未处于应用状态（DesiredStatus={slot.DesiredStatus}，"
                        + $"ActiveModelArtifactId='{slot.ActiveModelArtifactId ?? "(空)"}'）——模型已停用或未激活。"));
                }
                else
                {
                    checks.Add(Pass("model-slot-live",
                        $"cluster model slot 'primary' 实时状态正常（{slot.ActiveModelArtifactId}，revision={slot.Revision}）。"));
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                checks.Add(Fail("model-slot-live",
                    $"cluster model slot 实时查询超时（超过 {_options.ProbeTimeout.TotalSeconds:0.#} 秒）。"));
            }
            catch (Exception ex)
            {
                checks.Add(Fail("model-slot-live",
                    $"cluster model slot 实时查询异常：{ex.GetType().Name}: {ex.Message}"));
            }
        }

        // 探针 3：application-started-live — 应用是否已完成启动
        var started = _lifetime.ApplicationStarted.IsCancellationRequested;
        checks.Add(started
            ? Pass("application-started-live", "应用已完成启动（所有 HostedService.StartAsync 已触发）。")
            : Fail("application-started-live",
                "应用尚未完成启动——请求阶段准入在启动完成前拒绝业务流量。"));
    }

    // ── 结果构造辅助 ────────────────────────────────────────────────────

    private static ProductionAdmissionCheck Pass(string name, string message)
        => new(name, ProductionAdmissionCheckStatus.Pass, message);

    private static ProductionAdmissionCheck Fail(string name, string message)
        => new(name, ProductionAdmissionCheckStatus.Fail, message);
}
