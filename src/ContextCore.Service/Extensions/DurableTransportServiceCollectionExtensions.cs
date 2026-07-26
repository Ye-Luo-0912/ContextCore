using ContextCore.Abstractions;
using ContextCore.Service.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.Service.Extensions;

/// <summary>
/// P0-4：Durable Transport 后台托管服务注册扩展。
/// </summary>
/// <remarks>
/// 调用 <c>services.AddDurableTransportHostedServices()</c> 注册：
///   - <see cref="DurableTransportInstructionPumpService"/>：从 PG inbox 租约指令 → SubmitAsync
///   - <see cref="ResultOutboxReplayService"/>：从 outbox 租约结果 → SendResultAsync → Ack
///   - <see cref="LeaseReaperService"/>：定时回滚过期 Leased 行
///
/// 通常由 <c>AddContextCorePostgresStorage(options, transportOptions)</c> 在
/// <see cref="KernelTransportOptions.UseDurableTransport"/> = true 时自动调用。
/// 也可手动调用以覆盖默认配置。
/// </remarks>
public static class DurableTransportServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Durable Transport 后台托管服务（pump / replayer / reaper）。
    /// 仅当 <see cref="IAgentKernelTransport"/> 运行时实现为 <see cref="IDurableTransport"/> 时生效。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configure">可选的选项配置委托。</param>
    public static IServiceCollection AddDurableTransportHostedServices(
        this IServiceCollection services,
        Action<DurableTransportHostingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<DurableTransportHostingOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddHostedService<DurableTransportInstructionPumpService>();
        services.AddHostedService<ResultOutboxReplayService>();
        services.AddHostedService<LeaseReaperService>();
        return services;
    }
}
